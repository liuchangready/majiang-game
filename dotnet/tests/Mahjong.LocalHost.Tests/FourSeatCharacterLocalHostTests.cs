using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    [Collection("LocalHostSerial")]
    public sealed class FourSeatCharacterLocalHostTests
    {
        private static string FindOctopusRequest(string roundId, int seat, int turnNumber, bool hit)
        {
            for (int i = 0; i < 10_000; i++)
            {
                string req = "m102-lh-" + seat + "-" + turnNumber + "-" + i;
                if (PlayerCharacterRules.GrantsThirdSleeveCandidate(
                    PlayerCharacterIds.Octopus,
                    PlayerCharacterIds.CurrentTraitsVersion,
                    roundId,
                    seat,
                    turnNumber,
                    req) == hit)
                {
                    return req;
                }
            }

            throw new InvalidOperationException("Could not find deterministic Octopus request.");
        }

        private static int CountDrawTurns(RoundStateMachine round)
        {
            int turns = 0;
            for (int i = 0; i < round.Events.Count; i++)
            {
                if (round.Events[i].Type == RoundEventType.TileDrawn ||
                    round.Events[i].Type == RoundEventType.RinshanDrawn)
                {
                    turns++;
                }
            }

            return turns;
        }

        private static TableCheatCommandEnvelope CheatEnvelope(
            RequestScopeId scopeId,
            string roundId,
            int seat,
            RoundCheatAction action,
            string requestId,
            RoundCheatPayload payload,
            ulong expectedVersion,
            ulong controlEpoch = 0UL)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(
                "lh-cheat|" + seat + "|" + action + "|" + requestId + "|" + (payload.OfferId ?? "") + "|" + payload.Keep);
            return new TableCheatCommandEnvelope(
                new RoundCheatCommand(roundId, seat, action, requestId, payload),
                new RequestId(requestId),
                new StateVersion(expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(controlEpoch),
                null,
                0L,
                scopeId,
                "CHEAT",
                TablePayloadSha256.Compute(bytes));
        }

        private static TableCommandEnvelope RoundEnvelope(
            RequestScopeId scopeId,
            RoundCommand command,
            ulong expectedVersion,
            string requestId,
            ulong controlEpoch = 0UL)
        {
            byte[] bytes = Encoding.UTF8.GetBytes("lh-round|" + expectedVersion + "|" + requestId);
            return new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                new StateVersion(expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(controlEpoch),
                null,
                0L,
                scopeId,
                "ROUND",
                TablePayloadSha256.Compute(bytes));
        }

        private static LocalTableStore GetStore(LocalTableRuntime runtime)
        {
            var field = typeof(LocalTableRuntime).GetField(
                "_store",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            return (LocalTableStore)field.GetValue(runtime)!;
        }

        private static CheatState GetCheat(TableActor actor)
        {
            var field = typeof(TableActor).GetField(
                "_cheat",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            return (CheatState)field.GetValue(actor)!;
        }

        // M1T-02 + M1T-03 + M1T-05: LocalHost seat 1/2/3 Octopus normal draw, pending Third cold recovery,
        // 1-candidate 2-choice Third rejection zero side effects, and single-seat/conflict recovery rules.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task M1T02_M1T03_M1T05_LocalHost_NonZeroSeatOctopus_ThirdChoiceAndColdRecovery(int seat)
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string[] characters = { "player", "player", "player", "player" };
            characters[seat] = PlayerCharacterIds.Octopus;

            harness.Options.AutoPlay = false;
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            harness.Options.CharacterIds = characters;

            string roundId;
            string pendingOfferId;
            TileId thirdTile;
            ulong nextVersion = 0;

            await using (LocalTableRuntime runtime = await LocalTableRuntime.OpenAsync(harness.Options, ct))
            {
                TableActor actor = runtime.ActorForTest;
                Assert.Equal(characters, actor.GetSeatCharacterIdsForHost());
                LocalTableStore store = GetStore(runtime);
                TableScopeRecord scope = store.IssueScope(
                    "local-m102-seat" + seat,
                    TableScopeRole.Player,
                    P41Harness.RoomId,
                    (ulong)harness.Clock.UtcNowMs,
                    (ulong)harness.Clock.UtcNowMs + 600_000UL,
                    "scope-m102-" + seat);

                roundId = actor.GetRoundStateForHost().RoundId;
                for (int i = 0; i < seat; i++)
                {
                    RoundStateMachine cur = actor.GetRoundStateForHost();
                    TableOperationResult d = await actor.SubmitAsync(
                        RoundEnvelope(scope.ScopeId, RoundCommand.Discard(roundId, cur.CurrentSeat, cur.Tiles.DrawnTile!.Value.Tile),
                            nextVersion++, "step-" + i),
                        ct);
                    Assert.Equal(TableOperationStatus.Accepted, d.Status);
                }

                RoundStateMachine targetRound = actor.GetRoundStateForHost();
                Assert.Equal(seat, targetRound.CurrentSeat);
                string hitReq = FindOctopusRequest(roundId, seat, CountDrawTurns(targetRound), hit: true);

                TableOperationResult act = await actor.SubmitCheatAsync(
                    CheatEnvelope(scope.ScopeId, roundId, seat, RoundCheatAction.SubmitSleeveBloom,
                        hitReq, RoundCheatPayload.SleeveBloom(), nextVersion++),
                    ct);
                Assert.Equal(TableOperationStatus.Accepted, act.Status);
                SleeveBloomOffer offer = GetCheat(actor).SleeveBloom.ImmediateOffer!;
                Assert.True(offer.ThirdTile.HasValue);
                pendingOfferId = offer.OfferId;
                thirdTile = offer.ThirdTile.Value;
                Assert.Equal(110, GetCheat(actor).Ledger.BalanceOf(seat));
            }

            // Reopen with conflicting CharacterIds in options -> persisted save characters MUST win!
            harness.Options.CharacterIds = new[] { "octopus", "player", "player", "player" };
            harness.Options.PlayerCharacterId = PlayerCharacterIds.Octopus;
            harness.Clock.UtcNowMs += 500L;
            harness.Clock.MonotonicMillis += 500L;

            await using (LocalTableRuntime recovered = await LocalTableRuntime.OpenAsync(harness.Options, ct))
            {
                TableActor actor = recovered.ActorForTest;
                Assert.Equal(characters, actor.GetSeatCharacterIdsForHost());
                CheatState recoveredCheat = GetCheat(actor);
                Assert.NotNull(recoveredCheat.SleeveBloom.ImmediateOffer);
                Assert.Equal(pendingOfferId, recoveredCheat.SleeveBloom.ImmediateOffer!.OfferId);
                Assert.Equal(thirdTile, recoveredCheat.SleeveBloom.ImmediateOffer.ThirdTile);
                Assert.Equal(110, recoveredCheat.Ledger.BalanceOf(seat));

                // Cross-boot foreign choice rejected, normal discard settles First cleanly with 0 refund.
                LocalTableStore store = GetStore(recovered);
                TableScopeRecord scope = store.IssueScope(
                    "local-m102-seat" + seat + "-r",
                    TableScopeRole.Player,
                    P41Harness.RoomId,
                    (ulong)harness.Clock.UtcNowMs,
                    (ulong)harness.Clock.UtcNowMs + 600_000UL,
                    "scope-m102-rec-" + seat);

                TableOperationResult foreignChoice = await actor.SubmitCheatAsync(
                    new TableCheatCommandEnvelope(
                        new RoundCheatCommand(roundId, seat, RoundCheatAction.SubmitSleeveBloomChoice,
                            "foreign-third", RoundCheatPayload.SleeveBloomChoice(pendingOfferId, SleeveBloomKeep.Third)),
                        new RequestId("foreign-third"),
                        new StateVersion(nextVersion),
                        new OwnerEpoch(1),
                        new ControlEpoch(0),
                        null,
                        actor.RecoveryGeneration,
                        scope.ScopeId,
                        "CHEAT",
                        TablePayloadSha256.Compute(Encoding.UTF8.GetBytes("foreign-third"))),
                    ct);
                Assert.Equal(TableOperationStatus.Rejected, foreignChoice.Status);
                Assert.Equal(TableRuntimeReasonCode.CheatBootReconciliationRequired, foreignChoice.ReasonCode);
                Assert.Equal(110, GetCheat(actor).Ledger.BalanceOf(seat));

                TileId firstTile = actor.GetRoundStateForHost().Tiles.DrawnTile!.Value.Tile;
                TableOperationResult discard = await actor.SubmitAsync(
                    new TableCommandEnvelope(
                        RoundCommand.Discard(roundId, seat, firstTile),
                        new RequestId("settle-first"),
                        new StateVersion(nextVersion++),
                        new OwnerEpoch(1),
                        new ControlEpoch(0),
                        null,
                        actor.RecoveryGeneration,
                        scope.ScopeId,
                        "ROUND",
                        TablePayloadSha256.Compute(Encoding.UTF8.GetBytes("settle-first"))),
                    ct);
                Assert.Equal(TableOperationStatus.Accepted, discard.Status);
                Assert.Null(GetCheat(actor).SleeveBloom.ImmediateOffer);
                Assert.Equal(110, GetCheat(actor).Ledger.BalanceOf(seat));
                Assert.True(TilePositionMap.Validate(actor.GetRoundStateForHost().Tiles).IsValid);
            }
        }

        // M1T-06a: LocalMatchCoordinator persists 4-seat characters across renchan/rotation/restart/takeover/reclaim
        // and supports legacy v1 .match files, .prev fallback, single-seat compatibility and validation errors.
        [Fact]
        public async Task M1T06a_LocalMatchCoordinator_PropagatesFourSeatCharactersAcrossRenchanRotationAndRecovery()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(harness.Root, "m102-match");
            string[] expectedChars = { "player", "octopus", "player", "octopus" };

            var options = new LocalMatchHostOptions
            {
                SaveDirectory = root,
                MatchId = "m102-match",
                OwnerInstanceId = "owner-m102",
                LeaseDuration = TimeSpan.FromMinutes(5),
                Environment = harness.Options.Environment,
                Rules = new MatchRuleConfig(MatchLength.Tonpuusen, handsPerWind: 4, allowWestExtension: false),
                SeatPlan = LocalTableSeatPlan.SingleHumanSeatZero,
                Ai = new LocalAiOptions { ProfileId = LocalAiProfiles.Basic },
                CharacterIds = expectedChars
            };
            options.RoundFactory = (state, _) =>
            {
                // Round 1 (dealer 0): seat 0 wins (renchan -> round 2 keeps dealer 0).
                // Round 2 (dealer 0): seat 1 wins (rotation -> round 3 has dealer 1).
                int dealer = state.DealerSeat;
                int loser = state.Ordinal == 1 ? 1 : 2;
                return BuildQuickWinRound(state.CurrentRoundId, dealer, state.RoundWind, state.RoundNumber, state.Honba,
                    state.Scores, state.RiichiSticks, loserSeat: loser);
            };

            string r1Id;
            string r2Id;
            string r3Id;

            LocalMatchCoordinator match = await LocalMatchCoordinator.OpenAsync(options, ct);
            try
            {
                Assert.Equal(expectedChars, match.Snapshot.CharacterIds);
                Assert.Equal(new[] { 1, 1, 1, 1 }, match.Snapshot.CharacterTraitsVersions);

                // Round 1 (initial): verify authoritative CheatState & Takeover/ReclaimControl never change characters.
                LocalMatchRound r1 = await match.OpenRoundAsync(ct);
                r1Id = r1.RoundId;
                await r1.Channel.OpenAsync(new TableChannelConnectRequest(r1.Ticket, "m102-p0", null), ct);
                LocalTableRuntime runtime1 = ((LocalTableChannel)r1.Channel).RuntimeForMatch!;
                TableActor actor1 = runtime1.ActorForTest;
                Assert.Equal(expectedChars, actor1.GetSeatCharacterIdsForHost());

                // Takeover + Reclaim control on seat 0 -> characters remain immutable.
                TableControlResult takeover = await actor1.RequestTakeoverAsync(
                    0, new OwnerEpoch(1), new ControlEpoch(0), "takeover", ct);
                Assert.Equal(TableControlStatus.Changed, takeover.Status);
                Assert.Equal(expectedChars, actor1.GetSeatCharacterIdsForHost());
                TableControlResult reclaim = await actor1.ReclaimControlAsync(
                    0, new OwnerEpoch(1), new ControlEpoch(1), "reclaim", ct);
                Assert.Equal(TableControlStatus.Changed, reclaim.Status);
                Assert.Equal(expectedChars, actor1.GetSeatCharacterIdsForHost());

                // Seat 0 wins (dealer renchan!).
                TableSession s1 = await PlayQuickTsumoOrDiscardWinAsync(r1, ct, seat0Wins: true, match);
                LocalMatchSnapshot afterR1 = await match.CompleteRoundAsync(ct);
                await s1.CloseAsync("TEST_DONE", CancellationToken.None);
                Assert.Equal(0, afterR1.DealerSeat);
                Assert.Equal(1, afterR1.Honba);
                Assert.Equal(expectedChars, afterR1.CharacterIds);
            }
            finally
            {
                await match.DisposeAsync();
            }

            // Cold restart coordinator WITH conflicting CharacterIds -> persisted .match characters MUST win!
            var conflictingOptions = new LocalMatchHostOptions
            {
                SaveDirectory = root,
                MatchId = "m102-match",
                OwnerInstanceId = "owner-m102-2",
                LeaseDuration = TimeSpan.FromMinutes(5),
                Environment = harness.Options.Environment,
                Rules = options.Rules,
                SeatPlan = LocalTableSeatPlan.SingleHumanSeatZero,
                Ai = new LocalAiOptions { ProfileId = LocalAiProfiles.Basic },
                PlayerCharacterId = PlayerCharacterIds.Octopus,
                CharacterIds = new[] { "octopus", "player", "player", "player" },
                RoundFactory = options.RoundFactory
            };

            LocalMatchCoordinator restarted = await LocalMatchCoordinator.OpenAsync(conflictingOptions, ct);
            try
            {
                Assert.Equal(expectedChars, restarted.Snapshot.CharacterIds);

                // Round 2 (renchan): new roundId, same 4-seat characters in CheatState.
                LocalMatchRound r2 = await restarted.OpenRoundAsync(ct);
                r2Id = r2.RoundId;
                Assert.NotEqual(r1Id, r2Id);
                await r2.Channel.OpenAsync(new TableChannelConnectRequest(r2.Ticket, "m102-p0-r2", null), ct);
                TableActor actor2 = ((LocalTableChannel)r2.Channel).RuntimeForMatch!.ActorForTest;
                Assert.Equal(expectedChars, actor2.GetSeatCharacterIdsForHost());

                // Seat 1 wins (dealer rotation -> dealer 1).
                TableSession s2 = await PlayQuickTsumoOrDiscardWinAsync(r2, ct, seat0Wins: false, restarted);
                LocalMatchSnapshot afterR2 = await restarted.CompleteRoundAsync(ct);
                await s2.CloseAsync("TEST_DONE", CancellationToken.None);
                Assert.Equal(1, afterR2.DealerSeat);
                Assert.Equal(expectedChars, afterR2.CharacterIds);

                // Round 3 (rotated dealer): new roundId, same 4-seat characters in CheatState.
                LocalMatchRound r3 = await restarted.OpenRoundAsync(ct);
                r3Id = r3.RoundId;
                Assert.NotEqual(r2Id, r3Id);
                await r3.Channel.OpenAsync(new TableChannelConnectRequest(r3.Ticket, "m102-p0-r3", null), ct);
                TableActor actor3 = ((LocalTableChannel)r3.Channel).RuntimeForMatch!.ActorForTest;
                Assert.Equal(expectedChars, actor3.GetSeatCharacterIdsForHost());
            }
            finally
            {
                await restarted.DisposeAsync();
            }
        }

        [Fact]
        public void M1T06a_OptionsValidationAndLegacyMatchStateV1Compatibility()
        {
            // Single-seat backwards compatibility
            var single = new LocalTableHostOptions
            {
                SaveDirectory = Path.GetTempPath(),
                RoomId = "r-single",
                PlayerCharacterId = PlayerCharacterIds.Octopus
            };
            single.Validate();
            Assert.Equal(new[] { "octopus", "player", "player", "player" }, single.ResolveCharacterIds());
            Assert.Equal(new[] { 1, 1, 1, 1 }, single.ResolveCharacterTraitsVersions());

            // Conflict between explicit non-default PlayerCharacterId and CharacterIds[0]
            var conflict = new LocalTableHostOptions
            {
                SaveDirectory = Path.GetTempPath(),
                RoomId = "r-conflict",
                PlayerCharacterId = PlayerCharacterIds.Octopus,
                CharacterIds = new[] { "player", "octopus", "player", "player" }
            };
            Assert.Throws<LocalTableException>(() => conflict.Validate());

            // Invalid count / character / traitsVersion rejected
            var badCount = new LocalMatchHostOptions
            {
                SaveDirectory = Path.GetTempPath(),
                MatchId = "m-bad",
                CharacterIds = new[] { "player", "octopus" }
            };
            Assert.Throws<LocalTableException>(() => badCount.Validate());

            var badTrait = new LocalMatchHostOptions
            {
                SaveDirectory = Path.GetTempPath(),
                MatchId = "m-bad-tv",
                CharacterIds = new[] { "player", "octopus", "player", "player" },
                CharacterTraitsVersions = new[] { 1, 2, 1, 1 }
            };
            Assert.Throws<LocalTableException>(() => badTrait.Validate());

            // Legacy v1 .match file (no character fields, EnvelopeVersion = 1) decodes cleanly as 4x player v1.
            string root = Path.Combine(Path.GetTempPath(), "m102-legacy-match-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "legacy.match");
                byte[] legacyPayload;
                using (var body = new MemoryStream())
                using (var w = new BinaryWriter(body, new UTF8Encoding(false, true), true))
                {
                    w.Write("legacy");
                    w.Write(1);
                    w.Write(1);
                    w.Write("legacy-r1");
                    w.Write("legacy-r1");
                    w.Write(0);
                    w.Write(1);
                    w.Write(0);
                    w.Write(0);
                    w.Write(0);
                    w.Write(4); for (int i = 0; i < 4; i++) w.Write(25_000L);
                    w.Write(0);
                    w.Write(false);
                    w.Write(string.Empty);
                    w.Write(0);
                    w.Write(0);
                    w.Flush();
                    legacyPayload = body.ToArray();
                }

                using (var env = new MemoryStream())
                using (var w = new BinaryWriter(env, new UTF8Encoding(false, true), true))
                {
                    w.Write(0x4D415443); // MATC
                    w.Write(1); // Legacy v1
                    w.Write(legacyPayload.Length);
                    w.Write(TablePayloadSha256.Compute(legacyPayload).ToArray());
                    w.Write(legacyPayload);
                    w.Flush();
                    File.WriteAllBytes(path, env.ToArray());
                }

                LocalMatchState loaded = LocalMatchStateCodec.Load(path)!;
                Assert.Equal(new[] { "player", "player", "player", "player" }, loaded.CharacterIds);
                Assert.Equal(new[] { 1, 1, 1, 1 }, loaded.CharacterTraitsVersions);

                // v2 .match with 4-seat characters falls back to .prev when primary is corrupt, and rejects when both are corrupt.
                string v2Path = Path.Combine(root, "v2.match");
                LocalMatchState v2 = LocalMatchState.Create("v2", new[] { "player", "octopus", "player", "octopus" });
                LocalMatchStateCodec.Save(v2Path, v2, 0);
                byte[] v2Bytes = File.ReadAllBytes(v2Path);
                v2Bytes[v2Bytes.Length - 1] ^= 0xFF;
                File.WriteAllBytes(v2Path, v2Bytes);
                LocalMatchState prevRecovered = LocalMatchStateCodec.Load(v2Path)!;
                Assert.Equal(new[] { "player", "octopus", "player", "octopus" }, prevRecovered.CharacterIds);

                File.WriteAllBytes(v2Path, v2Bytes);
                File.WriteAllBytes(v2Path + ".prev", v2Bytes);
                LocalTableException bothBad = Assert.Throws<LocalTableException>(() => LocalMatchStateCodec.Load(v2Path));
                Assert.Equal(LocalTableErrorKind.CorruptState, bothBad.ErrorKind);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        // D-01-01: Malformed 7-bit string length prefix in .match and .save maps to InvalidDataException/CorruptState (no FormatException leak).
        [Fact]
        public async Task D0101_CorruptSevenBitStringLength_FallsBackAndRejectsWithoutFormatExceptionLeak()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(Path.GetTempPath(), "m102-d0101-7bit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string matchPath = Path.Combine(root, "m-7bit.match");
                LocalMatchState initial = LocalMatchState.Create("m-7bit", new[] { "player", "octopus", "player", "player" });
                LocalMatchStateCodec.Save(matchPath, initial, 0);
                // Save version 2 so primary & .prev both exist cleanly with distinct versions if needed.
                LocalMatchState v2 = LocalMatchState.Create("m-7bit", new[] { "player", "octopus", "player", "player" });
                v2.Restore(
                    initial.MatchId, 2, initial.Ordinal, initial.CurrentRoomId, initial.CurrentRoundId,
                    initial.DealerSeat, initial.RoundWind, initial.RoundNumber, initial.Honba, initial.RiichiSticks,
                    initial.Scores.ToArray(), initial.FinalScores.ToArray(), initial.Finished, initial.FinishReason,
                    initial.Rankings.ToList(), initial.Records.ToList(),
                    new[] { "player", "octopus", "player", "player" },
                    new[] { 1, 1, 1, 1 });
                LocalMatchStateCodec.Save(matchPath, v2, 1);

                byte[] badMatch = RebuildEnvelopeWithCorruptSevenBitToken(File.ReadAllBytes(matchPath), "octopus");
                File.WriteAllBytes(matchPath, badMatch);

                // Must NOT throw FormatException; must fall back cleanly to valid .match.prev (version 1).
                LocalMatchState recovered = LocalMatchStateCodec.Load(matchPath)!;
                Assert.Equal(1, recovered.Version);
                Assert.Equal(new[] { "player", "octopus", "player", "player" }, recovered.CharacterIds);

                // Both primary & .prev corrupt with malformed 7-bit length -> CorruptState, never FormatException.
                File.WriteAllBytes(matchPath, badMatch);
                File.WriteAllBytes(matchPath + ".prev", badMatch);
                LocalTableException bothBad = Assert.Throws<LocalTableException>(() => LocalMatchStateCodec.Load(matchPath));
                Assert.Equal(LocalTableErrorKind.CorruptState, bothBad.ErrorKind);

                // Corrupt 7-bit string length in inner schema 7 CheatPayload of .save -> falls back to valid .save.prev cleanly,
                // and rejects with CorruptState when both .save and .save.prev have corrupt 7-bit length (never FormatException).
                string saveDir = Path.Combine(root, "save-7bit");
                await CreateTwoCommitSaveRoomAsync(saveDir, "r-7bit", new[] { "player", "octopus", "player", "player" }, ct);
                string savePath = Path.Combine(saveDir, "r-7bit.save");
                string prevPath = savePath + ".prev";
                TamperSaveInnerCheatSevenBitWithValidHashes(savePath, "octopus");
                var optsSave7Bit = new LocalTableHostOptions
                {
                    SaveDirectory = saveDir,
                    RoomId = "r-7bit",
                    AutoPlay = false,
                    SeatPlan = LocalTableSeatPlan.AllFourHumanSeats,
                    CharacterIds = new[] { "player", "octopus", "player", "player" }
                };
                await using (LocalTableRuntime recoveredSave = await LocalTableRuntime.OpenAsync(optsSave7Bit, ct))
                {
                    Assert.Equal(new[] { "player", "octopus", "player", "player" }, recoveredSave.ActorForTest.GetSeatCharacterIdsForHost());
                }

                TamperSaveInnerCheatSevenBitWithValidHashes(savePath, "octopus");
                TamperSaveInnerCheatSevenBitWithValidHashes(prevPath, "octopus");
                LocalTableException bothSaveBad = await Assert.ThrowsAsync<LocalTableException>(
                    () => LocalTableRuntime.OpenAsync(optsSave7Bit, ct).AsTask());
                Assert.Equal(LocalTableErrorKind.CorruptState, bothSaveBad.ErrorKind);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        // D-01-01: Pre-selection/pre-publish checkpoint verification validates schema 7 roles, versions & fingerprints:
        // corrupt primary falls back to valid .save.prev; corrupt missing-primary backup is NEVER published as primary;
        // outer future format version and transient I/O errors retain their existing contracts.
        [Fact]
        public async Task D0101_SavePreSelectionValidation_FallsBackToValidPrevAndNeverPublishesBadBackup()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(Path.GetTempPath(), "m102-d0101-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string[] expectedChars = { "player", "octopus", "player", "player" };

                // Case A: Corrupt primary inner schema 7 CheatPayload (with valid outer + inner SHA-256) + valid .save.prev -> falls back cleanly.
                string dirA = Path.Combine(root, "case-a");
                await CreateTwoCommitSaveRoomAsync(dirA, "r-d0101-a", expectedChars, ct);
                string saveA = Path.Combine(dirA, "r-d0101-a.save");
                string prevA = saveA + ".prev";
                Assert.True(File.Exists(saveA));
                Assert.True(File.Exists(prevA));
                TamperSaveInnerCheatCharacterWithValidHashes(saveA, "octopus", "penguin");

                var optsA = new LocalTableHostOptions
                {
                    SaveDirectory = dirA,
                    RoomId = "r-d0101-a",
                    AutoPlay = false,
                    SeatPlan = LocalTableSeatPlan.AllFourHumanSeats,
                    CharacterIds = expectedChars
                };
                ITableChannel chA = await LocalTableHost.CreateChannelAsync(optsA, ct);
                try
                {
                    string ticketA = LocalTableHost.IssueTicket(optsA);
                    await chA.OpenAsync(new TableChannelConnectRequest(ticketA, "d0101-p0-a", null), ct);
                    TableActor actorA = ((LocalTableChannel)chA).RuntimeForMatch!.ActorForTest;
                    Assert.Equal(expectedChars, actorA.GetSeatCharacterIdsForHost());
                }
                finally
                {
                    await chA.CloseAsync(new TableChannelCloseReason("LOCAL_TEST_CLOSE", true), CancellationToken.None);
                }

                // Case B: Missing primary + corrupt inner schema 7 CheatPayload in .save.prev -> refuses open and MUST NOT publish bad backup to .save.
                string dirB = Path.Combine(root, "case-b");
                await CreateTwoCommitSaveRoomAsync(dirB, "r-d0101-b", expectedChars, ct);
                string saveB = Path.Combine(dirB, "r-d0101-b.save");
                string prevB = saveB + ".prev";
                File.Delete(saveB);
                TamperSaveInnerCheatCharacterWithValidHashes(prevB, "octopus", "penguin");
                byte[] prevBytesBefore = File.ReadAllBytes(prevB);

                var optsB = new LocalTableHostOptions
                {
                    SaveDirectory = dirB,
                    RoomId = "r-d0101-b",
                    AutoPlay = false,
                    SeatPlan = LocalTableSeatPlan.AllFourHumanSeats,
                    CharacterIds = expectedChars
                };
                ITableChannel chB = await LocalTableHost.CreateChannelAsync(optsB, ct);
                try
                {
                    string ticketB = LocalTableHost.IssueTicket(optsB);
                    LocalTableException badBackup = await Assert.ThrowsAsync<LocalTableException>(
                        () => chB.OpenAsync(new TableChannelConnectRequest(ticketB, "d0101-p0-b", null), ct));
                    Assert.Equal(LocalTableErrorKind.CorruptState, badBackup.ErrorKind);
                    Assert.False(File.Exists(saveB));
                    Assert.True(File.Exists(prevB));
                    Assert.Equal(prevBytesBefore, File.ReadAllBytes(prevB));
                }
                finally
                {
                    await chB.CloseAsync(new TableChannelCloseReason("LOCAL_TEST_CLOSE", true), CancellationToken.None);
                }

                // Case C: Outer future save envelope version must reject with CorruptState WITHOUT rolling back to valid .save.prev.
                string dirC = Path.Combine(root, "case-c");
                await CreateTwoCommitSaveRoomAsync(dirC, "r-d0101-c", expectedChars, ct);
                string saveC = Path.Combine(dirC, "r-d0101-c.save");
                byte[] futureBytes = File.ReadAllBytes(saveC);
                BitConverter.TryWriteBytes(futureBytes.AsSpan(4, 4), 99);
                File.WriteAllBytes(saveC, futureBytes);

                var optsC = new LocalTableHostOptions
                {
                    SaveDirectory = dirC,
                    RoomId = "r-d0101-c",
                    AutoPlay = false,
                    SeatPlan = LocalTableSeatPlan.AllFourHumanSeats,
                    CharacterIds = expectedChars
                };
                ITableChannel chC = await LocalTableHost.CreateChannelAsync(optsC, ct);
                try
                {
                    string ticketC = LocalTableHost.IssueTicket(optsC);
                    LocalTableException futureEx = await Assert.ThrowsAsync<LocalTableException>(
                        () => chC.OpenAsync(new TableChannelConnectRequest(ticketC, "d0101-p0-c", null), ct));
                    Assert.Equal(LocalTableErrorKind.CorruptState, futureEx.ErrorKind);
                }
                finally
                {
                    await chC.CloseAsync(new TableChannelCloseReason("LOCAL_TEST_CLOSE", true), CancellationToken.None);
                }

                // Case D: Transient I/O lock on .save maps to Unavailable (never CorruptState).
                string saveA2 = Path.Combine(dirA, "r-d0101-a.save");
                using (new FileStream(saveA2, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    LocalTableException ioEx = await Assert.ThrowsAsync<LocalTableException>(
                        () => LocalTableRuntime.OpenAsync(optsA, ct).AsTask());
                    Assert.Equal(LocalTableErrorKind.Unavailable, ioEx.ErrorKind);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        // D-01-02: Mutating caller CharacterIds between checkpoint creation and actor construction MUST NOT split
        // initial actor roles from persisted checkpoint / cold-recovered authority.
        [Fact]
        public async Task D0102_MutatingOptionsCharacterIdsDuringOpen_PreservesConsistentInitialAndRecoveredAuthority()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(Path.GetTempPath(), "m102-d0102-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string[] mutableRoles = { "player", "player", "player", "player" };
                string[] expectedFrozen = { "player", "player", "player", "player" };
                int utcReads = 0;
                var mutatingClock = new DelegateTableClock(
                    () =>
                    {
                        utcReads++;
                        if (utcReads >= 2)
                        {
                            // Mutate during CreateInitialCheckpoint / CreateRoomAsync, before CreateActor runs.
                            mutableRoles[1] = PlayerCharacterIds.Octopus;
                        }

                        return 1_700_000_000_000L + utcReads;
                    },
                    () => 1_000L + utcReads);

                var opts = new LocalTableHostOptions
                {
                    SaveDirectory = root,
                    RoomId = "r-d0102",
                    AutoPlay = false,
                    SeatPlan = LocalTableSeatPlan.AllFourHumanSeats,
                    CharacterIds = mutableRoles,
                    Environment = new LocalHostEnvironment { Clock = mutatingClock }
                };

                ITableChannel chInitial = await LocalTableHost.CreateChannelAsync(opts, ct);
                try
                {
                    string ticket = LocalTableHost.IssueTicket(opts);
                    await chInitial.OpenAsync(new TableChannelConnectRequest(ticket, "d0102-p0", null), ct);
                    TableActor initialActor = ((LocalTableChannel)chInitial).RuntimeForMatch!.ActorForTest;
                    Assert.Equal(expectedFrozen, initialActor.GetSeatCharacterIdsForHost());
                    Assert.Equal(expectedFrozen, GetCheat(initialActor).CharacterIds);
                }
                finally
                {
                    await chInitial.CloseAsync(new TableChannelCloseReason("LOCAL_TEST_CLOSE", true), CancellationToken.None);
                }

                // Cold recovery on the same room must match the frozen initial roles identically.
                var recoverOpts = new LocalTableHostOptions
                {
                    SaveDirectory = root,
                    RoomId = "r-d0102",
                    AutoPlay = false,
                    SeatPlan = LocalTableSeatPlan.AllFourHumanSeats,
                    CharacterIds = new[] { "octopus", "octopus", "player", "player" }
                };
                ITableChannel chRecovered = await LocalTableHost.CreateChannelAsync(recoverOpts, ct);
                try
                {
                    string ticket = LocalTableHost.IssueTicket(recoverOpts);
                    await chRecovered.OpenAsync(new TableChannelConnectRequest(ticket, "d0102-p0-rec", null), ct);
                    TableActor recoveredActor = ((LocalTableChannel)chRecovered).RuntimeForMatch!.ActorForTest;
                    Assert.Equal(expectedFrozen, recoveredActor.GetSeatCharacterIdsForHost());
                    Assert.Equal(expectedFrozen, GetCheat(recoveredActor).CharacterIds);
                }
                finally
                {
                    await chRecovered.CloseAsync(new TableChannelCloseReason("LOCAL_TEST_CLOSE", true), CancellationToken.None);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private sealed class DelegateTableClock : ITableClock
        {
            private readonly Func<long> _utc;
            private readonly Func<long> _mono;

            public DelegateTableClock(Func<long> utc, Func<long> mono)
            {
                _utc = utc;
                _mono = mono;
            }

            public long UtcNowMs => _utc();
            public long MonotonicMillis => _mono();
        }

        private static async Task CreateTwoCommitSaveRoomAsync(
            string saveDir,
            string roomId,
            string[] characterIds,
            CancellationToken ct)
        {
            Directory.CreateDirectory(saveDir);
            var opts = new LocalTableHostOptions
            {
                SaveDirectory = saveDir,
                RoomId = roomId,
                AutoPlay = false,
                SeatPlan = LocalTableSeatPlan.AllFourHumanSeats,
                CharacterIds = characterIds
            };
            await using LocalTableRuntime runtime = await LocalTableRuntime.OpenAsync(opts, ct);
            TableActor actor = runtime.ActorForTest;
            LocalTableStore store = GetStore(runtime);
            TableScopeRecord scope = store.IssueScope(
                "local-d0101",
                TableScopeRole.Player,
                roomId,
                (ulong)(opts.Environment ?? LocalHostEnvironment.System).ResolveClock().UtcNowMs,
                (ulong)(opts.Environment ?? LocalHostEnvironment.System).ResolveClock().UtcNowMs + 600_000UL,
                "scope-d0101");
            RoundStateMachine round = actor.GetRoundStateForHost();
            TableOperationResult discard = await actor.SubmitAsync(
                RoundEnvelope(
                    scope.ScopeId,
                    RoundCommand.Discard(round.RoundId, round.CurrentSeat, round.Tiles.DrawnTile!.Value.Tile),
                    0UL,
                    "d0101-seed-discard"),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, discard.Status);
        }

        private static byte[] RebuildEnvelopeWithCorruptSevenBitToken(byte[] envelopeBytes, string asciiToken)
        {
            int magic = BitConverter.ToInt32(envelopeBytes, 0);
            int version = BitConverter.ToInt32(envelopeBytes, 4);
            int length = BitConverter.ToInt32(envelopeBytes, 8);
            byte[] payload = new byte[length];
            Buffer.BlockCopy(envelopeBytes, 12 + TablePayloadSha256.SizeBytes, payload, 0, length);

            byte[] tokenBytes = Encoding.ASCII.GetBytes(asciiToken);
            int idx = -1;
            for (int i = 1; i <= payload.Length - tokenBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < tokenBytes.Length; j++)
                {
                    if (payload[i + j] != tokenBytes[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match && payload[i - 1] == tokenBytes.Length)
                {
                    idx = i - 1;
                    break;
                }
            }

            Assert.True(idx >= 0, "Could not locate token in envelope payload: " + asciiToken);
            byte[] badPayload = new byte[payload.Length + 4];
            Buffer.BlockCopy(payload, 0, badPayload, 0, idx);
            for (int k = 0; k < 5; k++)
            {
                badPayload[idx + k] = 0xFF;
            }

            Buffer.BlockCopy(payload, idx + 1, badPayload, idx + 5, payload.Length - idx - 1);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, new UTF8Encoding(false, true), true);
            w.Write(magic);
            w.Write(version);
            w.Write(badPayload.Length);
            w.Write(TablePayloadSha256.Compute(badPayload).ToArray());
            w.Write(badPayload);
            w.Flush();
            return ms.ToArray();
        }

        private static void TamperSaveInnerCheatCharacterWithValidHashes(string savePath, string findAscii, string replaceAscii)
        {
            LocalSave save = LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(savePath));
            TableCheckpoint cp = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
            byte[] cheatBytes = (byte[])cp.CheatPayload!.Clone();
            byte[] findBytes = Encoding.ASCII.GetBytes(findAscii);
            byte[] replaceBytes = Encoding.ASCII.GetBytes(replaceAscii);
            Assert.Equal(findBytes.Length, replaceBytes.Length);
            bool replaced = false;
            for (int i = 0; i <= cheatBytes.Length - findBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < findBytes.Length; j++)
                {
                    if (cheatBytes[i + j] != findBytes[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    Buffer.BlockCopy(replaceBytes, 0, cheatBytes, i, replaceBytes.Length);
                    replaced = true;
                    break;
                }
            }

            Assert.True(replaced, "Token not found in CheatPayload: " + findAscii);
            var badCp = new TableCheckpoint(
                cp.RoomId,
                cp.StateVersion,
                cp.LastEventSeq,
                cp.StateFingerprint,
                cp.RulesetVersion,
                cp.StatePayload,
                cp.StatePayloadSha256,
                cp.ControlVersion,
                cp.ControlPayload,
                cp.ControlPayloadSha256,
                cp.TimingPayload,
                cp.TimingPayloadSha256,
                cheatBytes,
                TablePayloadSha256.Compute(cheatBytes),
                cp.CommittedFactsPayload,
                cp.CommittedFactsPayloadSha256,
                cp.CreatedAtUtcMs,
                cp.BootId,
                cp.SchemaVersion,
                cp.TimingVersion,
                cp.CapturedMonotonicMillis,
                cp.CommittedHistoryPayload,
                cp.CommittedHistoryPayloadSha256,
                cp.CommittedCommandBindingsPayload,
                cp.CommittedCommandBindingsPayloadSha256);
            save.BaseCheckpointBlob = LocalSaveCodec.EncodeCheckpoint(badCp);
            save.CheckpointSha256 = TablePayloadSha256.Compute(save.BaseCheckpointBlob);
            File.WriteAllBytes(savePath, LocalSaveCodec.EncodeEnvelope(save));
        }

        private static void TamperSaveInnerCheatSevenBitWithValidHashes(string savePath, string asciiToken)
        {
            LocalSave save = LocalSaveCodec.DecodeEnvelope(File.ReadAllBytes(savePath));
            TableCheckpoint cp = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
            byte[] cheatBytes = cp.CheatPayload!;
            byte[] tokenBytes = Encoding.ASCII.GetBytes(asciiToken);
            int idx = -1;
            for (int i = 1; i <= cheatBytes.Length - tokenBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < tokenBytes.Length; j++)
                {
                    if (cheatBytes[i + j] != tokenBytes[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match && cheatBytes[i - 1] == tokenBytes.Length)
                {
                    idx = i - 1;
                    break;
                }
            }

            Assert.True(idx >= 0, "Token not found in CheatPayload: " + asciiToken);
            byte[] badCheat = new byte[cheatBytes.Length + 4];
            Buffer.BlockCopy(cheatBytes, 0, badCheat, 0, idx);
            for (int k = 0; k < 5; k++)
            {
                badCheat[idx + k] = 0xFF;
            }

            Buffer.BlockCopy(cheatBytes, idx + 1, badCheat, idx + 5, cheatBytes.Length - idx - 1);
            var badCp = new TableCheckpoint(
                cp.RoomId,
                cp.StateVersion,
                cp.LastEventSeq,
                cp.StateFingerprint,
                cp.RulesetVersion,
                cp.StatePayload,
                cp.StatePayloadSha256,
                cp.ControlVersion,
                cp.ControlPayload,
                cp.ControlPayloadSha256,
                cp.TimingPayload,
                cp.TimingPayloadSha256,
                badCheat,
                TablePayloadSha256.Compute(badCheat),
                cp.CommittedFactsPayload,
                cp.CommittedFactsPayloadSha256,
                cp.CreatedAtUtcMs,
                cp.BootId,
                cp.SchemaVersion,
                cp.TimingVersion,
                cp.CapturedMonotonicMillis,
                cp.CommittedHistoryPayload,
                cp.CommittedHistoryPayloadSha256,
                cp.CommittedCommandBindingsPayload,
                cp.CommittedCommandBindingsPayloadSha256);
            save.BaseCheckpointBlob = LocalSaveCodec.EncodeCheckpoint(badCp);
            save.CheckpointSha256 = TablePayloadSha256.Compute(save.BaseCheckpointBlob);
            File.WriteAllBytes(savePath, LocalSaveCodec.EncodeEnvelope(save));
        }

        private static RoundStateMachine BuildQuickWinRound(
            string roundId,
            int dealerSeat,
            int roundWind,
            int roundNumber,
            int honba,
            IReadOnlyList<long> scores,
            int riichiSticks,
            int loserSeat)
        {
            // Give seat 0 a 13-tile tenpai hand (1m-9m + 3x 1p + 1x 2p, waiting on 2p/5p or tsumo)
            // Wait: simpler — give seat 0 a winning hand immediately on its deal-extra / first draw, or give seat 1 a Ron on seat 0's discard.
            // To keep both seat 0 win and seat 1 win trivial without touching AI timing:
            // - When loserSeat == 1 (seat 0 wins): seat 0 starts with complete Tsumo hand!
            // - When loserSeat == 2 (seat 1 wins): seat 1 starts tenpai waiting on 132 (Haku), seat 0 discards 132 immediately -> seat 1 Rons!
            var specific = new Dictionary<int, int>();
            if (loserSeat == 1)
            {
                // Dealer (seat 0) starts with complete hand on its 14th tile (slot 52): 1m2m3m 4m5m6m 7m8m9m 1p1p1p 2p2p
                int[] tiles = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 72, 73, 74, 76, 77 };
                int[] slots = dealerSeat == 0
                    ? new[] { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 }
                    : new[] { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49, 53 };
                for (int i = 0; i < slots.Length; i++)
                {
                    specific[slots[i]] = tiles[i];
                }
            }
            else
            {
                // Seat 0 holds all four Haku (124..127) and discards 124 (0 eligible responders).
                // Seat 1 holds 1m2m3m 4m5m6m 7m8m9m 1p1p1p Chun(132) and draws Chun(133) at slot 53 for immediate Tsumo!
                int[] seat1Tiles = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 72, 73, 74, 132 };
                int[] seat1Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
                for (int i = 0; i < seat1Slots.Length; i++)
                {
                    specific[seat1Slots[i]] = seat1Tiles[i];
                }

                specific[0] = 125;
                specific[1] = 126;
                specific[2] = 127;
                specific[52] = 124;
                specific[53] = 133;
            }

            var used = new HashSet<int>(specific.Values);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new int[TileId.Count];
            for (int i = 0; i < TileId.Count; i++)
            {
                order[i] = specific.TryGetValue(i, out int entity) ? entity : remaining.Dequeue();
            }

            RecordedRandomDraw wall = RecordedRandomDraw.Create(roundId + "-wall", order.Select(id => new TileId(id)).ToArray());
            TilePositionMap dealt = TilePositionMap.CreateDealt(wall, dealerSeat);
            var spec = new RoundSpec(roundId, dealerSeat, roundWind, roundNumber, honba, scores, riichiSticks);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }

        private static async Task<TableSession> PlayQuickTsumoOrDiscardWinAsync(
            LocalMatchRound round,
            CancellationToken ct,
            bool seat0Wins,
            LocalMatchCoordinator coordinator)
        {
            var session = new TableSession(round.Channel, new InMemoryOutboxStore());
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
            await session.OpenAsync(new TableSessionOpenRequest(round.Ticket, "m102-player"), pumpCts.Token);
            Task pump = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCts.IsCancellationRequested)
                    {
                        await session.PumpOnceAsync(pumpCts.Token);
                    }
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException ||
                    ex is TableChannelClosedException ||
                    ex is InvalidOperationException)
                {
                }
            }, TestContext.Current.CancellationToken);

            try
            {
                for (int i = 0; i < 60 && session.State != TableConnectionState.Ready; i++)
                {
                    await Task.Delay(10, pumpCts.Token);
                }

                Assert.Equal(TableConnectionState.Ready, session.State);

                // Wait for seat 0 decision and submit Tsumo (when seat0Wins) or discard 132 (so AI seat 1 Rons).
                for (int i = 0; i < 100 && session.PlayerSnapshot?.PrivateView?.Decision == null; i++)
                {
                    await Task.Delay(10, pumpCts.Token);
                }

                var snap = session.PlayerSnapshot!;
                var decision = snap.PrivateView.Decision!;
                ProtocolActionType action = seat0Wins ? ProtocolActionType.DeclareTsumo : ProtocolActionType.DiscardTile;
                uint[]? tiles = seat0Wins ? null : new[] { 124u };
                var payload = CommandPayloadFactory.SubmitAction(
                    snap.RoundId, decision.DecisionId, decision.ControlEpoch, action, tileIds: tiles);
                await session.SubmitAsync(new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    new RequestId("m102-act-" + snap.RoundId),
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(snap.StateVersion),
                    new RoundId(snap.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch)), pumpCts.Token);

                for (int i = 0; i < 150 &&
                     session.PlayerSnapshot?.PublicView?.Board?.Phase != Mahjong.Contracts.Wire.GamePhase.RoundEnd; i++)
                {
                    await Task.Delay(20, pumpCts.Token);
                }

                Assert.True(
                    session.PlayerSnapshot!.PublicView.Board.Phase == Mahjong.Contracts.Wire.GamePhase.RoundEnd,
                    "phase=" + session.PlayerSnapshot!.PublicView.Board.Phase + " diag=" + coordinator.DescribeRuntimeForTest());
                return session;
            }
            finally
            {
                pumpCts.Cancel();
                await pump;
            }
        }
    }
}
