using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class FourSeatCharacterRecoveryTests
    {
        private const int InitialPoints = 1000;

        private static string FindOctopusRequest(string roundId, int seat, int turnNumber, bool hit)
        {
            for (int i = 0; i < 10_000; i++)
            {
                string request = "m102-tr-" + seat + "-" + turnNumber + "-" + i;
                if (PlayerCharacterRules.GrantsThirdSleeveCandidate(
                    PlayerCharacterIds.Octopus,
                    PlayerCharacterIds.CurrentTraitsVersion,
                    roundId,
                    seat,
                    turnNumber,
                    request) == hit)
                {
                    return request;
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

        private static CheatState GetCheat(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField("_cheat", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (CheatState)field.GetValue(actor)!;
        }

        private static TableCheckpoint WithCheat(TableCheckpoint source, CheatState cheat)
        {
            byte[] payload = TableCheatLedgerCheckpointCodec.Encode(TableCheatLedgerCheckpoint.FromCheatState(cheat));
            return WithCheatBytes(source, payload);
        }

        private static TableCheckpoint WithCheatBytes(TableCheckpoint source, byte[] payload) =>
            new TableCheckpoint(
                source.RoomId,
                source.StateVersion,
                source.LastEventSeq,
                source.StateFingerprint,
                source.RulesetVersion,
                source.StatePayload,
                source.StatePayloadSha256,
                source.ControlVersion,
                source.ControlPayload,
                source.ControlPayloadSha256,
                source.TimingPayload,
                source.TimingPayloadSha256,
                payload,
                TablePayloadSha256.Compute(payload),
                source.CommittedFactsPayload,
                source.CommittedFactsPayloadSha256,
                source.CreatedAtUtcMs,
                source.BootId,
                source.SchemaVersion,
                source.TimingVersion,
                source.CapturedMonotonicMillis,
                source.CommittedHistoryPayload,
                source.CommittedHistoryPayloadSha256,
                source.CommittedCommandBindingsPayload,
                source.CommittedCommandBindingsPayloadSha256);

        private static TableCommandEnvelope DurableRound(
            TableScopeRecord scope,
            RoundCommand command,
            int expectedVersion,
            string requestId)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("round|" + expectedVersion + "|" + requestId);
            return new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                scope.ScopeId,
                "ROUND",
                TablePayloadSha256.Compute(bytes));
        }

        private static TableCheatCommandEnvelope DurableCheat(
            string roundId,
            int seat,
            RoundCheatAction action,
            string requestId,
            RoundCheatPayload payload,
            int expectedVersion,
            TableScopeRecord scope,
            ulong ownerEpoch = 1UL,
            ulong controlEpoch = 0UL)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                "cheat|" + seat + "|" + action + "|" + requestId + "|" + (payload.OfferId ?? "") + "|" + payload.Keep);
            return new TableCheatCommandEnvelope(
                new RoundCheatCommand(roundId, seat, action, requestId, payload),
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(ownerEpoch),
                new ControlEpoch(controlEpoch),
                null,
                0L,
                scope.ScopeId,
                "CHEAT",
                TablePayloadSha256.Compute(bytes));
        }

        private static async Task<TableRecoveryResult> RecoverNewActorAsync(
            DurableHarness harness,
            CheatState? defaultCheatProviderState = null,
            string boot = "boot-2",
            string owner = "owner-2")
        {
            var durable = new TableDurableContext(
                harness.Store,
                harness.Store,
                harness.Store,
                harness.Store,
                harness.Scopes,
                owner,
                TimeSpan.FromMinutes(10),
                boot,
                1,
                DurableHarness.Room);
            durable.CheatLedgerProvider = () => (defaultCheatProviderState ?? V32Fixtures.Cheat(InitialPoints)).Ledger;
            durable.CheatStateProvider = () => defaultCheatProviderState ?? V32Fixtures.Cheat(InitialPoints);
            durable.RecoveryStateSink = new RecordingRecoveryStateSink();
            var legacy = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(legacy);
            var options = new TableActorOptions
            {
                RoomId = DurableHarness.Room,
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = DurableHarness.DefaultControl().Seats.ToDictionary(s => s.Seat, s => s.Kind),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig()
            };
            await using var actor = new TableActor(
                options,
                harness.InitialRound,
                legacy,
                harness.Clock,
                sink,
                null,
                null,
                null,
                durable,
                null);
            harness.Clock.Now += 700_000L;
            TableLeaseAcquireResult lease = await harness.Store.AcquireAsync(
                DurableHarness.Room,
                owner,
                TimeSpan.FromMinutes(10),
                TestContext.Current.CancellationToken);
            Assert.True(lease.Acquired, lease.Detail);
            return await actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
        }

        // M1T-02 + M1T-03: TableActor non-zero seat 1/2/3 Octopus normal draw, 1-candidate 2-choice boundary & Third rejection zero side effects.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task M1T02_M1T03_TableActor_NonZeroSeatOctopus_NormalDrawAndTwoChoiceRejection(int seat)
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            string[] characters = { "player", "player", "player", "player" };
            characters[seat] = PlayerCharacterIds.Octopus;
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithCharacters(characters);

            await using DurableHarness h = await DurableHarness.CreateAsync(
                ledger: cheat.Ledger,
                initialCheckpoint: WithCheat(DurableHarness.InitialCheckpoint(round, cheat.Ledger), cheat),
                configure: options =>
                {
                    options.CheatEconomy = V32Fixtures.Economy(InitialPoints);
                    options.SleeveBloomConfig = V32Fixtures.SleeveConfig();
                    options.InitialCheatState = cheat;
                });

            Assert.Equal(characters, h.Actor.GetSeatCharacterIdsForHost());

            // Advance from dealer 0 to target seat's first normal draw.
            int version = 0;
            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, seat);
            for (int i = 0; i < seat; i++)
            {
                Assert.Equal(TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(h.Scope, chain[i].Command, version++, "adv-" + i))).Status);
            }

            RoundStateMachine current = h.Actor.GetRoundStateForHost();
            Assert.Equal(seat, current.CurrentSeat);
            Assert.Equal(RoundPhase.CheatWindow, current.Phase);
            int turn = CountDrawTurns(current);
            string hitReq = FindOctopusRequest(round.RoundId, seat, turn, hit: true);

            TableOperationResult activated = await h.Actor.SubmitCheatAsync(
                DurableCheat(round.RoundId, seat, RoundCheatAction.SubmitSleeveBloom, hitReq,
                    RoundCheatPayload.SleeveBloom(), version++, h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);
            SleeveBloomOffer offer = GetCheat(h.Actor).SleeveBloom.ImmediateOffer!;
            Assert.Equal(seat, offer.Seat);
            Assert.True(offer.ThirdTile.HasValue);

            TableOperationResult chosen = await h.Actor.SubmitCheatAsync(
                DurableCheat(round.RoundId, seat, RoundCheatAction.SubmitSleeveBloomChoice, "pick-third",
                    RoundCheatPayload.SleeveBloomChoice(offer.OfferId, SleeveBloomKeep.Third), version++, h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, chosen.Status);
            Assert.Equal(offer.ThirdTile.Value, h.Actor.GetRoundStateForHost().Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(InitialPoints - 10, GetCheat(h.Actor).Ledger.BalanceOf(seat));
            Assert.True(TilePositionMap.Validate(h.Actor.GetRoundStateForHost().Tiles).IsValid);

            // Separate round with dealer == seat so draining 68 draws lands on target seat with WallRemaining == 1 (2-choice boundary).
            RoundStateMachine oneLeftStart = RoundStateMachine.CreateDealt(
                new RoundSpec("round-one-left", seat, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("round-one-left"), seat),
                RoundPhase.CheatWindow);
            CheatState oneCheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), oneLeftStart.RoundId)
                .WithCharacters(characters);
            await using var oneHarness = new TableActor(
                new TableActorOptions
                {
                    RoomId = "room-one-left",
                    MailboxCapacity = 16,
                    ControlReserve = 16,
                    DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                    InitialControllers = DurableHarness.DefaultControl().Seats.ToDictionary(s => s.Seat, s => s.Kind),
                    CheatEconomy = V32Fixtures.Economy(InitialPoints),
                    SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                    InitialCheatState = oneCheat
                },
                oneLeftStart,
                new RecordingCommitPort(),
                h.Clock,
                new RecordingPublicationSink(new RecordingCommitPort()));

            int oneVer = 0;
            while (oneHarness.GetRoundStateForHost().Tiles.WallRemaining > 1)
            {
                RoundStateMachine r = oneHarness.GetRoundStateForHost();
                TileId d = r.Tiles.DrawnTile!.Value.Tile;
                TableOperationResult step = await oneHarness.SubmitAsync(
                    new TableCommandEnvelope(
                        RoundCommand.Discard(oneLeftStart.RoundId, r.CurrentSeat, d),
                        new RequestId("one-drain-" + oneVer),
                        new StateVersion((ulong)oneVer++),
                        new OwnerEpoch(1),
                        new ControlEpoch(0)),
                    ct);
                Assert.Equal(TableOperationStatus.Accepted, step.Status);
            }

            RoundStateMachine oneLeft = oneHarness.GetRoundStateForHost();
            Assert.Equal(seat, oneLeft.CurrentSeat);
            Assert.Equal(1, oneLeft.Tiles.WallRemaining);
            int oneTurn = CountDrawTurns(oneLeft);
            string oneHitReq = FindOctopusRequest(oneLeftStart.RoundId, seat, oneTurn, hit: true);

            TableOperationResult oneAct = await oneHarness.SubmitCheatAsync(
                new TableCheatCommandEnvelope(
                    new RoundCheatCommand(oneLeftStart.RoundId, seat, RoundCheatAction.SubmitSleeveBloom, oneHitReq,
                        RoundCheatPayload.SleeveBloom()),
                    new RequestId(oneHitReq),
                    new StateVersion((ulong)oneVer++),
                    new OwnerEpoch(1),
                    new ControlEpoch(0),
                    null,
                    0L,
                    new RequestScopeId("scope-one"),
                    "CHEAT",
                    TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes("one-act|" + oneHitReq))),
                ct);
            Assert.True(oneAct.Status == TableOperationStatus.Accepted, oneAct.ReasonCode + " " + oneAct.Detail);
            SleeveBloomOffer twoChoiceOffer = GetCheat(oneHarness).SleeveBloom.ImmediateOffer!;
            Assert.Null(twoChoiceOffer.ThirdTile);

            // Submitting Third against a 2-choice offer must fail with zero side effects and keep the offer intact.
            TableSnapshot beforeSnap = await oneHarness.GetSnapshotAsync(ct);
            int balanceBefore = GetCheat(oneHarness).Ledger.BalanceOf(seat);
            TableOperationResult thirdRejected = await oneHarness.SubmitCheatAsync(
                new TableCheatCommandEnvelope(
                    new RoundCheatCommand(oneLeftStart.RoundId, seat, RoundCheatAction.SubmitSleeveBloomChoice, "reject-third",
                        RoundCheatPayload.SleeveBloomChoice(twoChoiceOffer.OfferId, SleeveBloomKeep.Third)),
                    new RequestId("reject-third"),
                    new StateVersion((ulong)oneVer),
                    new OwnerEpoch(1),
                    new ControlEpoch(0),
                    null,
                    0L,
                    new RequestScopeId("scope-one"),
                    "CHEAT",
                    TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes("reject-third"))),
                ct);
            Assert.Equal(TableOperationStatus.Rejected, thirdRejected.Status);
            Assert.Equal("CHEAT_INVALID_SELECTION", thirdRejected.ReasonCode);
            Assert.Equal(beforeSnap.StateVersion, (await oneHarness.GetSnapshotAsync(ct)).StateVersion);
            Assert.Equal(beforeSnap.LastEventSeq, (await oneHarness.GetSnapshotAsync(ct)).LastEventSeq);
            Assert.Equal(balanceBefore, GetCheat(oneHarness).Ledger.BalanceOf(seat));
            Assert.Equal(twoChoiceOffer.OfferId, GetCheat(oneHarness).SleeveBloom.ImmediateOffer!.OfferId);
        }

        // M1T-04: Schema 4/5/6 migration + Schema 7 roundtrip + fail-closed corruption/truncation/trailing/unknown schema/character/version.
        [Fact]
        public void M1T04_CheatCheckpointCodec_SupportsSchema4To7AndFailsClosedOnCorruption()
        {
            CheatState allPlayer = V32Fixtures.Cheat(InitialPoints);
            CheatState seat0Octopus = allPlayer.WithPlayerCharacter(PlayerCharacterIds.Octopus);
            CheatState multiSeat = allPlayer.WithCharacters(
                new[] { "player", "octopus", "player", "octopus" },
                new[] { 1, 1, 1, 1 });

            // Schema 4 -> 4x player
            byte[] v4 = TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                TableCheatLedgerCheckpoint.FromCheatState(allPlayer), 4);
            TableCheatLedgerCheckpoint d4 = TableCheatLedgerCheckpointCodec.Decode(v4);
            Assert.Equal(4, d4.FormatSchemaVersion);
            Assert.Equal(new[] { "player", "player", "player", "player" }, d4.RestoreCheatState().CharacterIds);
            Assert.Equal(allPlayer.Fingerprint, d4.RestoreCheatState().Fingerprint);

            // Schema 5 & 6 -> seat 0 preserved, seats 1..3 filled with player
            foreach (int legacySchema in new[] { 5, 6 })
            {
                byte[] vLegacy = TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                    TableCheatLedgerCheckpoint.FromCheatState(seat0Octopus), legacySchema);
                TableCheatLedgerCheckpoint decoded = TableCheatLedgerCheckpointCodec.Decode(vLegacy);
                Assert.Equal(legacySchema, decoded.FormatSchemaVersion);
                CheatState restored = decoded.RestoreCheatState();
                Assert.Equal(new[] { "octopus", "player", "player", "player" }, restored.CharacterIds);
                Assert.Equal(new[] { 1, 1, 1, 1 }, restored.CharacterTraitsVersions);
                Assert.Equal(seat0Octopus.Fingerprint, restored.Fingerprint);
            }

            // Schema 7 -> full 4-seat roundtrip
            byte[] v7 = TableCheatLedgerCheckpointCodec.Encode(TableCheatLedgerCheckpoint.FromCheatState(multiSeat));
            TableCheatLedgerCheckpoint d7 = TableCheatLedgerCheckpointCodec.Decode(v7);
            Assert.Equal(7, d7.FormatSchemaVersion);
            Assert.Equal(new[] { "player", "octopus", "player", "octopus" }, d7.RestoreCheatState().CharacterIds);
            Assert.Equal(multiSeat.Fingerprint, d7.RestoreCheatState().Fingerprint);

            // Fail-closed: unknown schema (3, 8)
            byte[] unknownLow = (byte[])v7.Clone();
            BitConverter.TryWriteBytes(unknownLow.AsSpan(4, 4), 3);
            Assert.Throws<InvalidDataException>(() => TableCheatLedgerCheckpointCodec.Decode(unknownLow));

            byte[] unknownHigh = (byte[])v7.Clone();
            BitConverter.TryWriteBytes(unknownHigh.AsSpan(4, 4), 8);
            Assert.Throws<InvalidDataException>(() => TableCheatLedgerCheckpointCodec.Decode(unknownHigh));

            // Fail-closed: tampered fingerprint (v4, v5, v6, v7)
            foreach (byte[] good in new[]
            {
                v4,
                TableCheatLedgerCheckpointCodec.EncodeWithSchema(TableCheatLedgerCheckpoint.FromCheatState(seat0Octopus), 5),
                TableCheatLedgerCheckpointCodec.EncodeWithSchema(TableCheatLedgerCheckpoint.FromCheatState(seat0Octopus), 6),
                v7
            })
            {
                byte[] badFp = (byte[])good.Clone();
                badFp[16] ^= 0x01; // inside UInt64 fingerprint at offset 16..24
                Assert.Throws<InvalidDataException>(() => TableCheatLedgerCheckpointCodec.Decode(badFp));

                byte[] truncated = new byte[good.Length - 1];
                Buffer.BlockCopy(good, 0, truncated, 0, truncated.Length);
                Assert.Throws<InvalidDataException>(() => TableCheatLedgerCheckpointCodec.Decode(truncated));

                byte[] trailing = new byte[good.Length + 1];
                Buffer.BlockCopy(good, 0, trailing, 0, good.Length);
                Assert.Throws<InvalidDataException>(() => TableCheatLedgerCheckpointCodec.Decode(trailing));
            }

            // Fail-closed: unsupported character ID or traits version in schema 5/6/7
            byte[] badV7Char = ReplaceAsciiBytes(v7, "octopus", "penguin");
            Assert.Throws<InvalidDataException>(() => TableCheatLedgerCheckpointCodec.Decode(badV7Char));

            byte[] badV5Char = ReplaceAsciiBytes(
                TableCheatLedgerCheckpointCodec.EncodeWithSchema(TableCheatLedgerCheckpoint.FromCheatState(seat0Octopus), 5),
                "octopus",
                "penguin");
            Assert.Throws<InvalidDataException>(() => TableCheatLedgerCheckpointCodec.Decode(badV5Char));

            byte[] badV7Traits = (byte[])v7.Clone();
            BitConverter.TryWriteBytes(badV7Traits.AsSpan(badV7Traits.Length - 4, 4), 2);
            Assert.Throws<InvalidDataException>(() => TableCheatLedgerCheckpointCodec.Decode(badV7Traits));

            // D-01-01: Corrupt 7-bit string length prefix in schema 5/6/7 must fail with InvalidDataException (never FormatException).
            foreach (byte[] good in new[]
            {
                TableCheatLedgerCheckpointCodec.EncodeWithSchema(TableCheatLedgerCheckpoint.FromCheatState(seat0Octopus), 5),
                TableCheatLedgerCheckpointCodec.EncodeWithSchema(TableCheatLedgerCheckpoint.FromCheatState(seat0Octopus), 6),
                v7
            })
            {
                byte[] bad7Bit = CorruptSevenBitStringLengthBeforeAscii(good, good == v7 ? "player" : "octopus");
                Assert.Throws<InvalidDataException>(() => TableCheatLedgerCheckpointCodec.Decode(bad7Bit));
            }
        }

        private static byte[] CorruptSevenBitStringLengthBeforeAscii(byte[] source, string asciiToken)
        {
            byte[] tokenBytes = System.Text.Encoding.ASCII.GetBytes(asciiToken);
            int idx = -1;
            for (int i = 1; i <= source.Length - tokenBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < tokenBytes.Length; j++)
                {
                    if (source[i + j] != tokenBytes[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match && source[i - 1] == tokenBytes.Length)
                {
                    idx = i - 1;
                    break;
                }
            }

            Assert.True(idx >= 0, "Could not locate string length prefix for token: " + asciiToken);
            byte[] mutated = new byte[source.Length + 4];
            Buffer.BlockCopy(source, 0, mutated, 0, idx);
            for (int k = 0; k < 5; k++)
            {
                mutated[idx + k] = 0xFF;
            }

            Buffer.BlockCopy(source, idx + 1, mutated, idx + 5, source.Length - idx - 1);
            return mutated;
        }

        private static byte[] ReplaceAsciiBytes(byte[] source, string find, string replace)
        {
            byte[] findBytes = System.Text.Encoding.ASCII.GetBytes(find);
            byte[] replaceBytes = System.Text.Encoding.ASCII.GetBytes(replace);
            Assert.Equal(findBytes.Length, replaceBytes.Length);
            byte[] copy = (byte[])source.Clone();
            for (int i = 0; i <= copy.Length - findBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < findBytes.Length; j++)
                {
                    if (copy[i + j] != findBytes[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    Buffer.BlockCopy(replaceBytes, 0, copy, i, replaceBytes.Length);
                    return copy;
                }
            }

            throw new InvalidOperationException("Pattern not found.");
        }

        // M1T-04: Schema 6 and Schema 7 must NOT trigger legacy v5 canLegacyRetry protection bypass.
        [Theory]
        [InlineData(6)]
        [InlineData(7)]
        public async Task M1T04_Schema6And7_NeverTriggerCanLegacyRetry(int schema)
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithPlayerCharacter(PlayerCharacterIds.Octopus);

            await using DurableHarness h = await DurableHarness.CreateAsync(
                ledger: cheat.Ledger,
                initialCheckpoint: WithCheat(DurableHarness.InitialCheckpoint(round, cheat.Ledger), cheat),
                configure: options =>
                {
                    options.CheatEconomy = V32Fixtures.Economy(InitialPoints);
                    options.SleeveBloomConfig = V32Fixtures.SleeveConfig();
                    options.InitialCheatState = cheat;
                });

            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 4);
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(h.Scope, chain[0].Command, 0, "r0"))).Status);

            string hit = FindOctopusRequest(round.RoundId, 0, CountDrawTurns(h.Actor.GetRoundStateForHost()), true);
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurn, hit,
                        RoundCheatPayload.OffTurn("bloom-window-0-0-0"), 1, h.Scope),
                    ct)).Status);

            TableCheckpoint cp = await h.SaveCheckpointAsync();
            byte[] encoded = TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                TableCheatLedgerCheckpointCodec.Decode(cp.CheatPayload!), schema);
            h.Store.ReplaceCheckpointForTest(DurableHarness.Room, WithCheatBytes(cp, encoded));

            // Now submit seat 1's discard whose draw was computed WITHOUT off-turn third protection (legacy v5 bug simulation).
            RoundStateMachine beforeDiscard = h.Actor.GetRoundStateForHost();
            CheatState activeCheat = GetCheat(h.Actor);
            TileId thirdTile = activeCheat.SleeveBloom.OffTurnOfferFor(0)!.ThirdTile!.Value;
            RoundCommand seat1Discard = RoundCommand.Discard(round.RoundId, 1, beforeDiscard.Tiles.DrawnTile!.Value.Tile);
            RoundStateMachine legacyOutcome = beforeDiscard.Install(
                beforeDiscard.PrepareDiscardProtected(
                    seat1Discard,
                    t => activeCheat.SleeveBloom.IsProtectedTileWithoutOffTurnThird(t)).Change!).State!;
            Assert.Equal(thirdTile, legacyOutcome.Tiles.DrawnTile!.Value.Tile);

            TableOperationResult liveDiscard = await h.SubmitAsync(DurableRound(h.Scope, seat1Discard, 2, "r2"));
            Assert.Equal(TableOperationStatus.Accepted, liveDiscard.Status);
            // Overwrite the stored state/events to the unprotected-Third outcome so attempt1 fails with RecoveryReplayMismatch.
            var eventsField = typeof(InMemoryTableStore).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var eventsMap = (Dictionary<string, List<TableEventRecord>>)eventsField.GetValue(h.Store)!;
            List<TableEventRecord> rows = eventsMap[DurableHarness.Room];
            int idx = rows.FindLastIndex(row => row.Type == RoundEventType.TileDrawn && row.Seat == legacyOutcome.CurrentSeat);
            Assert.True(idx >= 0);
            TableEventRecord drawRow = rows[idx];
            rows[idx] = new TableEventRecord(
                drawRow.Seq, drawRow.CommandVersion, drawRow.Ordinal, drawRow.Type, drawRow.Seat, thirdTile, drawRow.Detail,
                drawRow.EventSchema, TableEventFactCodec.Encode(drawRow.CommandVersion, drawRow.Ordinal, legacyOutcome.SourceFingerprint), drawRow.Cheat);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].CommandVersion.Equals(drawRow.CommandVersion) && i != idx)
                {
                    TableEventRecord sibling = rows[i];
                    rows[i] = new TableEventRecord(
                        sibling.Seq, sibling.CommandVersion, sibling.Ordinal, sibling.Type, sibling.Seat,
                        sibling.Tile, sibling.Detail, sibling.EventSchema,
                        TableEventFactCodec.Encode(sibling.CommandVersion, sibling.Ordinal, legacyOutcome.SourceFingerprint),
                        sibling.Cheat);
                }
            }

            var statesField = typeof(InMemoryTableStore).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object statesMap = statesField.GetValue(h.Store)!;
            object stateRow = statesMap.GetType().GetProperty("Item")!.GetValue(statesMap, new object[] { DurableHarness.Room })!;
            byte[] serializedLegacy = RoundRecoveryCodec.Encode(RoundRecovery.Capture(legacyOutcome));
            stateRow.GetType().GetProperty("StateFingerprint")!.SetValue(stateRow, legacyOutcome.SourceFingerprint);
            stateRow.GetType().GetProperty("StateHash")!.SetValue(stateRow, TablePayloadSha256.Compute(serializedLegacy));

            TableRecoveryResult recovery = await RecoverNewActorAsync(h);
            Assert.Equal(TableRecoveryStatus.Failed, recovery.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryReplayMismatch, recovery.ReasonCode);
        }

        // M1T-05: Pending third-candidate ImmediateOffer on seat 2 Octopus:
        // (1) same-boot cold recovery allows selecting Third with 0 double-charge;
        // (2) cross-boot cold recovery rejects new-scope choice as CHEAT_BOOT_RECONCILIATION_REQUIRED,
        //     and settles cleanly as First on normal discard;
        // (3) replay of original request returns FromReplay with single charge.
        [Fact]
        public async Task M1T05_Seat2OctopusPendingThirdOffer_SameBootAndCrossBootRecoveryAndReceiptReplay()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            string[] characters = { "player", "player", "octopus", "player" };
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithCharacters(characters);

            await using DurableHarness h = await DurableHarness.CreateAsync(
                ledger: cheat.Ledger,
                initialCheckpoint: WithCheat(DurableHarness.InitialCheckpoint(round, cheat.Ledger), cheat),
                configure: options =>
                {
                    options.CheatEconomy = V32Fixtures.Economy(InitialPoints);
                    options.SleeveBloomConfig = V32Fixtures.SleeveConfig();
                    options.InitialCheatState = cheat;
                });

            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 2);
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(h.Scope, chain[0].Command, 0, "d0"))).Status);
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(h.Scope, chain[1].Command, 1, "d1"))).Status);

            RoundStateMachine seat2Round = h.Actor.GetRoundStateForHost();
            Assert.Equal(2, seat2Round.CurrentSeat);
            string hitReq = FindOctopusRequest(round.RoundId, 2, CountDrawTurns(seat2Round), hit: true);

            TableCheatCommandEnvelope activateEnv = DurableCheat(
                round.RoundId, 2, RoundCheatAction.SubmitSleeveBloom, hitReq,
                RoundCheatPayload.SleeveBloom(), 2, h.Scope);
            TableOperationResult activated = await h.Actor.SubmitCheatAsync(activateEnv, ct);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);
            SleeveBloomOffer pendingOffer = GetCheat(h.Actor).SleeveBloom.ImmediateOffer!;
            Assert.True(pendingOffer.ThirdTile.HasValue);
            Assert.Equal(InitialPoints - 10, GetCheat(h.Actor).Ledger.BalanceOf(2));

            // 1. Same boot cold recovery (via new actor instance with same BootId) -> original activation replays & Third can be chosen.
            TableRecoveryResult sameBoot = await RecoverNewActorAsync(h, boot: DurableHarness.Boot, owner: "owner-sameboot");
            Assert.True(sameBoot.Recovered, sameBoot.ReasonCode + " " + sameBoot.Detail);

            // Recover a dedicated actor that keeps running for same-boot choice vs cross-boot test:
            var sameBootDurable = new TableDurableContext(
                h.Store, h.Store, h.Store, h.Store, h.Scopes,
                "owner-sameboot-2", TimeSpan.FromMinutes(10), DurableHarness.Boot, 1, DurableHarness.Room)
            {
                CheatLedgerProvider = () => cheat.Ledger,
                CheatStateProvider = () => cheat,
                RecoveryStateSink = new RecordingRecoveryStateSink()
            };
            var sbLegacy = new RecordingCommitPort();
            var sbOptions = new TableActorOptions
            {
                RoomId = DurableHarness.Room,
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = DurableHarness.DefaultControl().Seats.ToDictionary(s => s.Seat, s => s.Kind),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig()
            };
            await using (var sameBootActor = new TableActor(
                sbOptions, h.InitialRound, sbLegacy, h.Clock, new RecordingPublicationSink(sbLegacy),
                null, null, null, sameBootDurable, null))
            {
                h.Clock.Now += 700_000L;
                TableLeaseAcquireResult l = await h.Store.AcquireAsync(DurableHarness.Room, "owner-sameboot-2", TimeSpan.FromMinutes(10), ct);
                Assert.True(l.Acquired, l.Detail);
                TableRecoveryResult r = await sameBootActor.RecoverFromStoreAsync(ct);
                Assert.True(r.Recovered);
                Assert.Equal(characters, sameBootActor.GetSeatCharacterIdsForHost());

                // Replay original activation receipt -> FromReplay, 0 extra charge.
                TableOperationResult replayAct = await sameBootActor.SubmitCheatAsync(
                    new TableCheatCommandEnvelope(
                        activateEnv.CheatCommand, activateEnv.RequestId, activateEnv.ExpectedStateVersion,
                        r.Baseline!.OwnerEpoch, new ControlEpoch(0), null, sameBootActor.RecoveryGeneration,
                        activateEnv.ScopeId, activateEnv.Api, activateEnv.PayloadSha256),
                    ct);
                Assert.Equal(TableOperationStatus.Replayed, replayAct.Status);
                Assert.Equal(InitialPoints - 10, GetCheat(sameBootActor).Ledger.BalanceOf(2));
            }

            // 2. Cross-boot recovery -> new-scope choice rejected as CHEAT_BOOT_RECONCILIATION_REQUIRED,
            //    and normal discard settles the pending offer safely as First.
            var crossDurable = new TableDurableContext(
                h.Store, h.Store, h.Store, h.Store, h.Scopes,
                "owner-crossboot", TimeSpan.FromMinutes(10), "boot-cross-2", 1, DurableHarness.Room)
            {
                CheatLedgerProvider = () => cheat.Ledger,
                CheatStateProvider = () => cheat,
                RecoveryStateSink = new RecordingRecoveryStateSink()
            };
            var cbLegacy = new RecordingCommitPort();
            await using var crossActor = new TableActor(
                sbOptions, h.InitialRound, cbLegacy, h.Clock, new RecordingPublicationSink(cbLegacy),
                null, null, null, crossDurable, null);
            h.Clock.Now += 700_000L;
            TableLeaseAcquireResult crossLease = await h.Store.AcquireAsync(
                DurableHarness.Room, "owner-crossboot", TimeSpan.FromMinutes(10), ct);
            Assert.True(crossLease.Acquired, crossLease.Detail);
            TableRecoveryResult crossRecovered = await crossActor.RecoverFromStoreAsync(ct);
            Assert.True(crossRecovered.Recovered);

            long crossGen = crossActor.RecoveryGeneration;
            OwnerEpoch crossOwnerEpoch = crossRecovered.Baseline!.OwnerEpoch;
            TableOperationResult foreignChoice = await crossActor.SubmitCheatAsync(
                new TableCheatCommandEnvelope(
                    new RoundCheatCommand(
                        round.RoundId, 2, RoundCheatAction.SubmitSleeveBloomChoice, "cross-boot-third",
                        RoundCheatPayload.SleeveBloomChoice(pendingOffer.OfferId, SleeveBloomKeep.Third)),
                    new RequestId("cross-boot-third"),
                    new StateVersion(3),
                    crossOwnerEpoch,
                    new ControlEpoch(0),
                    null,
                    crossGen,
                    h.Scope.ScopeId,
                    "CHEAT",
                    TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes("cross-boot-third"))),
                ct);
            Assert.Equal(TableOperationStatus.Rejected, foreignChoice.Status);
            Assert.Equal(TableRuntimeReasonCode.CheatBootReconciliationRequired, foreignChoice.ReasonCode);
            Assert.NotNull(GetCheat(crossActor).SleeveBloom.ImmediateOffer);
            Assert.Equal(InitialPoints - 10, GetCheat(crossActor).Ledger.BalanceOf(2));

            // Normal discard settles the foreign-boot immediate offer safely as First (0 refund, 136 conserved).
            TileId discardTile = crossActor.GetRoundStateForHost().Tiles.DrawnTile!.Value.Tile;
            Assert.Equal(pendingOffer.FirstTile, discardTile);
            TableOperationResult settledByDiscard = await crossActor.SubmitAsync(
                new TableCommandEnvelope(
                    RoundCommand.Discard(round.RoundId, 2, discardTile),
                    new RequestId("cross-boot-discard"),
                    new StateVersion(3),
                    crossOwnerEpoch,
                    new ControlEpoch(0),
                    null,
                    crossGen,
                    h.Scope.ScopeId,
                    "ROUND",
                    TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes("cross-discard"))),
                ct);
            Assert.True(settledByDiscard.Status == TableOperationStatus.Accepted, settledByDiscard.ReasonCode + " " + settledByDiscard.Detail);
            Assert.Null(GetCheat(crossActor).SleeveBloom.ImmediateOffer);
            Assert.Equal(InitialPoints - 10, GetCheat(crossActor).Ledger.BalanceOf(2));
            Assert.True(TilePositionMap.Validate(crossActor.GetRoundStateForHost().Tiles).IsValid);
        }
    }
}
