using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// N-1b real actor tests: every cheat command goes through SubmitCheatAsync/SubmitAsync on
    /// the serial actor, through the unique P1.4 CheatSubmission surface, and through the real
    /// commit/checkpoint/stream paths. No helper fills expected values after the fact and no
    /// core transition is mocked.
    /// </summary>
    public sealed class N1bCheatExecutionTests
    {
        private const int InitialPoints = 1000;

        [Fact]
        public async Task tile_mark_commits_one_version_two_private_events_and_charges_once()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, 0);
            TileId[] tiles = hand.Take(4).ToArray();

            TableOperationResult result = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitTileMark,
                    "mark-1",
                    RoundCheatPayload.TileMark("g1", tiles),
                    0,
                    "mark-1"),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Accepted, result.Status);
            Assert.Equal(1UL, result.CommandVersion!.Value.Value);
            Assert.Equal(3, result.Events.Count);
            Assert.Equal(RoundEventType.CheatApplied, result.Events[0].Type);
            Assert.False(result.Events[0].IsCheatEvent);
            Assert.Equal(CheatEventKind.CheatPointsChanged, result.Events[1].Cheat!.Event.Kind);
            Assert.Equal(-5, result.Events[1].Cheat!.Event.Amount);
            Assert.Equal(995, result.Events[1].Cheat!.Event.NewBalance);
            Assert.Equal(CheatEventKind.TileMarkApplied, result.Events[2].Cheat!.Event.Kind);
            Assert.Equal("g1", result.Events[2].Cheat!.Event.GroupId);

            // Same request id and same payload must return the original receipt object without a
            // second version, charge or mark.
            TableOperationResult replay = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitTileMark,
                    "mark-1",
                    RoundCheatPayload.TileMark("g1", tiles),
                    0,
                    "mark-1"),
                TestContext.Current.CancellationToken);
            Assert.Same(result, replay);

            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(1UL, snapshot.StateVersion.Value);
        }

        [Fact]
        public async Task same_request_id_with_a_different_payload_digest_is_a_conflict()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();

            TableOperationResult first = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitTileMark,
                    "mark-1",
                    RoundCheatPayload.TileMark("g1", tiles),
                    0,
                    "mark-1",
                    digest: "digest-A"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);

            TableOperationResult conflict = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitTileMark,
                    "mark-1",
                    RoundCheatPayload.TileMark("g2", tiles),
                    1,
                    "mark-1",
                    digest: "digest-B"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Conflict, conflict.Status);
            Assert.Equal(1UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task stale_owner_control_version_round_and_recovery_generation_are_rejected_with_zero_state_change()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();

            (string Name, TableCheatCommandEnvelope Envelope)[] cases =
            {
                ("owner", Cheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "r1", RoundCheatPayload.TileMark("g", tiles), 0, "r1", owner: 2UL)),
                ("control", Cheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "r2", RoundCheatPayload.TileMark("g", tiles), 0, "r2", control: 1UL)),
                ("version", Cheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "r3", RoundCheatPayload.TileMark("g", tiles), 3, "r3")),
                ("round", Cheat("other-round", 0, RoundCheatAction.SubmitTileMark, "r4", RoundCheatPayload.TileMark("g", tiles), 0, "r4"))
            };

            foreach ((string name, TableCheatCommandEnvelope envelope) in cases)
            {
                TableOperationResult result = await harness.Actor.SubmitCheatAsync(
                    envelope,
                    TestContext.Current.CancellationToken);
                Assert.True(
                    result.Status == TableOperationStatus.Rejected ||
                    result.Status == TableOperationStatus.Failed,
                    name + " should be rejected, actual " + result.Status);
                Assert.Empty(result.Events);
                Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
            }
        }

        [Fact]
        public async Task immediate_sleeve_bloom_activation_and_choice_use_the_real_offer_and_physical_map()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            await DiscardOnceAsync(harness, "discard-0");

            TableOperationResult activation = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    1,
                    RoundCheatAction.SubmitSleeveBloom,
                    "sb-1",
                    RoundCheatPayload.SleeveBloom(),
                    1,
                    "sb-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activation.Status);
            Assert.Equal(2UL, activation.CommandVersion!.Value.Value);
            Assert.Contains(activation.Events, e => e.Cheat?.Event.Kind == CheatEventKind.CheatPointsChanged);
            Assert.Equal(990, activation.Events.Single(e => e.Cheat?.Event.Kind == CheatEventKind.CheatPointsChanged).Cheat!.Event.NewBalance);

            TableOperationResult choice = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    1,
                    RoundCheatAction.SubmitSleeveBloomChoice,
                    "sb-choice-1",
                    RoundCheatPayload.SleeveBloomChoice("bloom-1-sb-1", SleeveBloomKeep.Second),
                    2,
                    "sb-choice-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, choice.Status);
            Assert.Equal(3UL, choice.CommandVersion!.Value.Value);
            TableEventRecord resolved = Assert.Single(
                choice.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            Assert.Equal(SleeveBloomKeep.Second, resolved.Cheat!.Keep);
            Assert.Equal("bloom-1-sb-1", resolved.Cheat.OfferId);
            Assert.True(resolved.Cheat.ResultTile.HasValue);

            TableOperationResult stale = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    1,
                    RoundCheatAction.SubmitSleeveBloomChoice,
                    "sb-choice-2",
                    RoundCheatPayload.SleeveBloomChoice("bloom-1-sb-1", SleeveBloomKeep.First),
                    3,
                    "sb-choice-2"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, stale.Status);
            Assert.Equal(TableRuntimeReasonCode.FromCheat(RoundCheatErrorCode.CheatRejected, CheatErrorCode.NoPendingOffer), stale.ReasonCode);
            Assert.Empty(stale.Events);
            Assert.Equal(3UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task frozen_wall_activation_skips_a_foreign_reservation_and_replays_the_same_receipt()
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            TileId reserved = round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer));
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            cheat = cheat.WithSleeveBloom(cheat.SleeveBloom.WithReservation(
                new SleeveBloomReservation(2, "foreign-reservation", reserved, "window", 0)));
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(options, round, commitPort, clock, sink);

            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 1);
            TableOperationResult discard = await actor.SubmitAsync(
                new TableCommandEnvelope(
                    chain[0].Command,
                    new RequestId("frozen-discard"),
                    chain[0].ExpectedStateVersion,
                    new OwnerEpoch(1),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, discard.Status);

            TableCheatCommandEnvelope envelope = Cheat(
                round.RoundId,
                1,
                RoundCheatAction.SubmitSleeveBloom,
                "sb-frozen",
                RoundCheatPayload.SleeveBloom(),
                1,
                "sb-frozen");
            TableOperationResult activation = await actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activation.Status);
            Assert.Equal(2UL, activation.CommandVersion!.Value.Value);

            TilePositionMap map = GetRound(actor).Tiles;
            int expectedIndex = -1;
            for (int i = 0; i < map.WallEndExclusive; i++)
            {
                if (!map.TryGetTileAt(TilePosition.Wall(i), out TileId tile) ||
                    map.PositionOf(tile).Zone != TileZone.Wall ||
                    tile.Equals(reserved))
                {
                    continue;
                }

                expectedIndex = i;
                break;
            }

            Assert.True(expectedIndex >= 0);
            SleeveBloomOffer offer = GetCheat(actor)!.SleeveBloom.ImmediateOffer!;
            Assert.Equal(map.TileAt(TilePosition.Wall(expectedIndex)), offer.SecondTile);
            Assert.NotEqual(reserved, offer.SecondTile);
            Assert.Equal(990, GetCheat(actor)!.Ledger.BalanceOf(1));

            TableOperationResult replay = await actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);
            Assert.Same(activation, replay);
            Assert.Equal(990, GetCheat(actor)!.Ledger.BalanceOf(1));
            Assert.Equal(2UL, (await actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
        }

        [Fact]
        public async Task sleeve_activation_commit_failure_publishes_nothing_and_leaves_no_charge_or_offer()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            await DiscardOnceAsync(harness, "freeze-discard");
            int publishedBeforeFailure = harness.Sink.Publications.Count;
            harness.CommitPort.FailNext();

            TableOperationResult failed = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    1,
                    RoundCheatAction.SubmitSleeveBloom,
                    "sb-freeze-fail",
                    RoundCheatPayload.SleeveBloom(),
                    1,
                    "sb-freeze-fail"),
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            Assert.Empty(failed.Events);
            Assert.Empty(harness.Sink.Publications.Skip(publishedBeforeFailure));
            Assert.Equal(1UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
            Assert.Equal(1000, GetCheat(harness.Actor)!.Ledger.BalanceOf(1));
            Assert.Null(GetCheat(harness.Actor)!.SleeveBloom.ImmediateOffer);

            TableOperationResult retry = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    1,
                    RoundCheatAction.SubmitSleeveBloom,
                    "sb-freeze-retry",
                    RoundCheatPayload.SleeveBloom(),
                    1,
                    "sb-freeze-retry"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, retry.Status);
            Assert.Equal(2UL, retry.CommandVersion!.Value.Value);
            Assert.Equal(990, GetCheat(harness.Actor)!.Ledger.BalanceOf(1));
            Assert.NotNull(GetCheat(harness.Actor)!.SleeveBloom.ImmediateOffer);
        }

        [Fact]
        public async Task frozen_missing_wall_candidate_rejects_activation_with_zero_side_effects_and_replays()
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = BuildNoLiveWallRound("round-empty-wall", out _);
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(options, round, commitPort, clock, sink);

            TableCheatCommandEnvelope envelope = Cheat(
                round.RoundId,
                0,
                RoundCheatAction.SubmitSleeveBloom,
                "sb-empty-wall",
                RoundCheatPayload.SleeveBloom(),
                0,
                "sb-empty-wall");
            TableOperationResult rejected = await actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(
                TableRuntimeReasonCode.FromCheat(RoundCheatErrorCode.CheatRejected, CheatErrorCode.NoWallCandidate),
                rejected.ReasonCode);
            Assert.Empty(rejected.Events);
            Assert.Empty(sink.Publications);
            Assert.Equal(0UL, (await actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
            Assert.Equal(1000, GetCheat(actor)!.Ledger.BalanceOf(0));
            Assert.Null(GetCheat(actor)!.SleeveBloom.ImmediateOffer);

            TableOperationResult replay = await actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, replay.Status);
            Assert.Equal(rejected.ReasonCode, replay.ReasonCode);
            Assert.Empty(replay.Events);
            Assert.Equal(0UL, (await actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
            Assert.Equal(1000, GetCheat(actor)!.Ledger.BalanceOf(0));
            Assert.Null(GetCheat(actor)!.SleeveBloom.ImmediateOffer);
        }

        private static RoundStateMachine BuildNoLiveWallRound(string roundId, out TileId drawn)
        {
            var placements = new List<TilePlacement>();
            var used = new bool[TileId.Count];
            void Place(TileId tile, TilePosition position)
            {
                Assert.False(used[tile.Value]);
                used[tile.Value] = true;
                placements.Add(new TilePlacement(tile, position));
            }

            for (int slot = 0; slot < 13; slot++)
            {
                Place(new TileId(slot), TilePosition.Hand(0, slot));
            }

            drawn = new TileId(13);
            Place(drawn, TilePosition.Hand(0, 13));

            int next = 14;
            for (int seat = 1; seat < 4; seat++)
            {
                for (int slot = 0; slot < 13; slot++)
                {
                    Place(new TileId(next++), TilePosition.Hand(seat, slot));
                }
            }

            for (int slot = 0; slot < TilePositionMap.DeadWallSlotCount; slot++)
            {
                Place(new TileId(next++), TilePosition.DeadWall(slot));
            }

            int discard = 0;
            while (next < TileId.Count)
            {
                Place(new TileId(next++), TilePosition.Discard(discard % 4, discard / 4));
                discard++;
            }

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                0,
                0,
                0,
                null,
                0,
                null,
                Array.Empty<TileId>(),
                0,
                new DrawnTileEvidence(drawn, 0, DrawSource.Wall));
            var spec = new RoundSpec(
                roundId,
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            return RoundStateMachine.CreateDealt(spec, map, RoundPhase.CheatWindow);
        }

        [Fact]
        public async Task beam_swap_activation_and_keep_original_commit_only_the_owner_events()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            await DiscardOnceAsync(harness, "discard-0");

            TableOperationResult activation = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    1,
                    RoundCheatAction.SubmitBeamSwap,
                    "bs-1",
                    RoundCheatPayload.BeamSwap(),
                    1,
                    "bs-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activation.Status);
            Assert.Contains(activation.Events, e => e.Cheat?.Event.Kind == CheatEventKind.CheatPointsChanged);

            TableOperationResult choice = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    1,
                    RoundCheatAction.SubmitBeamSwapChoice,
                    "bs-choice-1",
                    RoundCheatPayload.BeamSwapChoice("beam-1-bs-1", BeamSwapAction.KeepOriginal, null, null),
                    2,
                    "bs-choice-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, choice.Status);
            TableEventRecord resolved = Assert.Single(
                choice.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.BeamSwapResolved);
            Assert.Equal(BeamSwapAction.KeepOriginal, resolved.Cheat!.Action);
            Assert.Equal("beam-1-bs-1", resolved.Cheat.OfferId);
        }

        [Fact]
        public async Task expired_off_turn_choice_persists_forced_first_but_reports_rejection_and_replays_identically()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            await DiscardOnceAsync(harness, "discard-0");

            TableOperationResult activation = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitSleeveBloomOffTurn,
                    "off-1",
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"),
                    1,
                    "off-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activation.Status);
            Assert.Equal(2UL, activation.CommandVersion!.Value.Value);

            harness.Clock.Now = 8000L;
            TableCheatCommandEnvelope late = Cheat(
                round.RoundId,
                0,
                RoundCheatAction.SubmitSleeveBloomOffTurnChoice,
                "off-choice-late",
                RoundCheatPayload.OffTurnChoice("bloom-offturn-0-off-1", SleeveBloomKeep.Second),
                2,
                "off-choice-late");
            TableOperationResult rejected = await harness.Actor.SubmitCheatAsync(
                late,
                TestContext.Current.CancellationToken);

            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(TableRuntimeReasonCode.DeadlineExpired, rejected.ReasonCode);
            Assert.Equal(3UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
            TableEventRecord forced = Assert.Single(
                rejected.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            Assert.Equal(SleeveBloomKeep.First, forced.Cheat!.Keep);
            Assert.Equal("bloom-offturn-0-off-1", forced.Cheat.OfferId);

            // The exact original request returns the original rejection receipt, not a second
            // forced settlement.
            TableOperationResult replay = await harness.Actor.SubmitCheatAsync(
                late,
                TestContext.Current.CancellationToken);
            Assert.Same(rejected, replay);
            Assert.Equal(3UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task authoritative_off_turn_sweep_settles_first_and_a_late_manual_choice_is_rejected()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            await DiscardOnceAsync(harness, "discard-0");
            TableOperationResult activation = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitSleeveBloomOffTurn,
                    "off-1",
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"),
                    1,
                    "off-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activation.Status);

            harness.Clock.Now = 8000L;
            TableTickResult tick = await harness.TickAsync();
            Assert.Equal(TableOperationStatus.Accepted, tick.Status);
            Assert.Equal(3UL, tick.FinalStateVersion.Value);

            TableOperationResult late = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitSleeveBloomOffTurnChoice,
                    "off-choice",
                    RoundCheatPayload.OffTurnChoice("bloom-offturn-0-off-1", SleeveBloomKeep.First),
                    3,
                    "off-choice"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, late.Status);
            Assert.Equal(
                TableRuntimeReasonCode.FromCheat(RoundCheatErrorCode.CheatRejected, CheatErrorCode.NoPendingOffer),
                late.ReasonCode);
            Assert.Equal(3UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task commit_failure_rolls_back_every_cheat_side_effect()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            harness.CommitPort.FailNext();

            TableOperationResult failed = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitTileMark,
                    "mark-fail",
                    RoundCheatPayload.TileMark("g1", round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray()),
                    0,
                    "mark-fail"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);

            // A successful retry starts from version 0 and charges exactly once.
            TableOperationResult retry = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitTileMark,
                    "mark-retry",
                    RoundCheatPayload.TileMark("g1", round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray()),
                    0,
                    "mark-retry"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, retry.Status);
            Assert.Equal(1UL, retry.CommandVersion!.Value.Value);
            Assert.Equal(995, retry.Events.Single(e => e.Cheat?.Event.Kind == CheatEventKind.CheatPointsChanged).Cheat!.Event.NewBalance);
        }

        [Fact]
        public async Task terminal_round_rejects_every_cheat_command_without_version_step()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            long[] finalScores = { 26000, 24000, 25000, 25000 };
            var settlement = new RoundSettlement(
                "result-1",
                RoundOutcome.Tsumo(0),
                finalScores,
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero)),
                1234L);
            TableOperationResult tsumo = await harness.SubmitAsync(
                new TableCommandEnvelope(
                    RoundCommand.Tsumo(round.RoundId, 0, settlement),
                    new RequestId("tsumo-1"),
                    new StateVersion(0),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)));
            Assert.Equal(TableOperationStatus.Accepted, tsumo.Status);

            TableOperationResult rejected = await harness.Actor.SubmitCheatAsync(
                Cheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitTileMark,
                    "mark-terminal",
                    RoundCheatPayload.TileMark("g1", round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray()),
                    1,
                    "mark-terminal"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal("ROUND_FINISHED", rejected.ReasonCode);
            Assert.Empty(rejected.Events);
            Assert.Equal(1UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task reservation_is_delivered_on_the_next_real_draw_without_a_second_charge()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 4);

            Assert.Equal(TableOperationStatus.Accepted, (await SubmitRoundAsync(harness, chain[0], 0, "d0")).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurn, "off-1",
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"), 1, "off-1"),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurnChoice, "off-choice",
                    RoundCheatPayload.OffTurnChoice("bloom-offturn-0-off-1", SleeveBloomKeep.First), 2, "off-choice"),
                TestContext.Current.CancellationToken)).Status);

            Assert.Equal(TableOperationStatus.Accepted, (await SubmitRoundAsync(harness, chain[1], 3, "d1")).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await SubmitRoundAsync(harness, chain[2], 4, "d2")).Status);
            TableOperationResult deliveryDiscard = await SubmitRoundAsync(harness, chain[3], 5, "d3");

            Assert.True(
                deliveryDiscard.Status == TableOperationStatus.Accepted,
                deliveryDiscard.ReasonCode + " | " + deliveryDiscard.Detail);
            Assert.Equal(6UL, deliveryDiscard.CommandVersion!.Value.Value);
            TableEventRecord drawn = Assert.Single(
                deliveryDiscard.Events,
                e => e.Type == RoundEventType.TileDrawn);
            TableEventRecord delivered = Assert.Single(
                deliveryDiscard.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomReservationDelivered);
            Assert.Equal(0, drawn.Seat);
            Assert.Equal("bloom-offturn-0-off-1", delivered.Cheat!.OfferId);
            Assert.DoesNotContain(
                deliveryDiscard.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.CheatPointsChanged);

            // No extra DrawTile command is accepted at this point: the delivery already advanced
            // the state to seat 0's CHEAT_WINDOW.
            TableOperationResult normalDraw = await SubmitRoundAsync(
                harness,
                new TableCommandEnvelope(
                    RoundCommand.Draw(round.RoundId, 0),
                    new RequestId("draw-0"),
                    new StateVersion(0),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)),
                6,
                "draw-0");
            Assert.Equal(TableOperationStatus.Rejected, normalDraw.Status);
            Assert.Equal(6UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task durable_checkpoint_and_journal_cold_recovery_restore_full_cheat_state_and_replay_the_same_receipt()
        {
            var clock = new ManualTableClock { Now = 1000L };
            var scopes = new InMemoryRequestScopeAuthority();
            TableScopeRecord scope = scopes.Issue(
                DurableHarness.Account,
                TableScopeRole.Player,
                DurableHarness.Room,
                (ulong)DurableHarness.UtcBase,
                (ulong)DurableHarness.UtcBase + 3_600_000UL);
            var store = new InMemoryTableStore(clock, scopes);
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            TableCheckpoint checkpoint = WithCheat(
                DurableHarness.InitialCheckpoint(round, cheat.Ledger),
                cheat);
            await store.CreateRoomAsync(checkpoint, TestContext.Current.CancellationToken);
            TableLeaseAcquireResult lease = await store.AcquireAsync(
                DurableHarness.Room,
                DurableHarness.Owner,
                TimeSpan.FromMinutes(10),
                TestContext.Current.CancellationToken);
            Assert.True(lease.Acquired, lease.Detail);

            var durable = new TableDurableContext(
                store,
                store,
                store,
                store,
                scopes,
                DurableHarness.Owner,
                TimeSpan.FromMinutes(10),
                DurableHarness.Boot,
                1,
                DurableHarness.Room);
            durable.RecoveryStateSink = new RecordingRecoveryStateSink();
            var projection = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var options = new TableActorOptions
            {
                RoomId = DurableHarness.Room,
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(
                options,
                round,
                new RecordingCommitPort(),
                clock,
                new RecordingPublicationSink(new RecordingCommitPort()),
                null,
                projection,
                null,
                durable);

            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();
            TableCheatCommandEnvelope envelope = DurableCheat(
                round.RoundId,
                0,
                RoundCheatAction.SubmitTileMark,
                "mark-1",
                RoundCheatPayload.TileMark("g1", tiles),
                0,
                scope);
            TableOperationResult accepted = await actor.SubmitCheatAsync(
                envelope,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(1UL, accepted.CommandVersion!.Value.Value);

            TableRecoveryResult recovered = await actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableRecoveryStatus.Recovered, recovered.Status);
            Assert.Equal(1, recovered.Report!.ReplayedCommands);
            Assert.True(recovered.Report.CheatStateRestored);
            Assert.Equal(1UL, recovered.Report.FinalStateVersion.Value);

            TableOperationResult replay = await actor.SubmitCheatAsync(
                envelope,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(1UL, replay.CommandVersion!.Value.Value);
            Assert.Equal(1UL, (await actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
        }

        private static TableCheckpoint WithCheat(TableCheckpoint source, CheatState cheat)
        {
            byte[] payload = TableCheatLedgerCheckpointCodec.Encode(TableCheatLedgerCheckpoint.FromCheatState(cheat));
            return new TableCheckpoint(
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
        }

        private static TableCheatCommandEnvelope DurableCheat(
            string roundId,
            int seat,
            RoundCheatAction action,
            string requestId,
            RoundCheatPayload payload,
            int expectedVersion,
            TableScopeRecord scope)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                "CHEAT|" + requestId + "|" + action + "|" + seat);
            return new TableCheatCommandEnvelope(
                new RoundCheatCommand(roundId, seat, action, requestId, payload),
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(0),
                null,
                0L,
                scope.ScopeId,
                "CHEAT",
                TablePayloadSha256.Compute(bytes));
        }

        [Fact]
        public async Task owner_stream_receives_typed_cheat_events_while_spectator_and_other_seat_do_not()
        {
            var codec = new StrictWireStreamCodec();
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = new RecordingStreamTerminationSink()
            };
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var publicationSink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(
                options,
                round,
                commitPort,
                clock,
                publicationSink,
                null,
                source,
                streamOptions);

            var owner = new RecordingStreamSink();
            var other = new RecordingStreamSink();
            var spectator = new RecordingStreamSink();
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.ForSeat(1), "owner", owner),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.ForSeat(0), "other", other),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.Spectator, "spectator", spectator),
                TestContext.Current.CancellationToken)).Status);
            await owner.WaitForCountAsync(1);
            await other.WaitForCountAsync(1);
            await spectator.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 1);
            Assert.Equal(TableOperationStatus.Accepted, (await SubmitActorRoundAsync(actor, chain[0], 0, "d0")).Status);
            TableOperationResult activation = await actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitSleeveBloom, "sb-1", RoundCheatPayload.SleeveBloom(), 1, "sb-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activation.Status);
            TableOperationResult choice = await actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitSleeveBloomChoice, "sb-choice", RoundCheatPayload.SleeveBloomChoice("bloom-1-sb-1", SleeveBloomKeep.First), 2, "sb-choice"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, choice.Status);

            await owner.WaitForCountAsync(4);
            await other.WaitForCountAsync(4);
            await spectator.WaitForCountAsync(4);

            PlayerUpdate ownerUpdate = StrictWireStreamCodec.DecodeAndStrict(
                owner.Frames[owner.Frames.Count - 1].EncodedPayload!).PlayerUpdate;
            PublicUpdate spectatorUpdate = StrictWireStreamCodec.DecodeAndStrict(
                spectator.Frames[spectator.Frames.Count - 1].EncodedPayload!).PublicUpdate;
            PlayerUpdate otherUpdate = StrictWireStreamCodec.DecodeAndStrict(
                other.Frames[other.Frames.Count - 1].EncodedPayload!).PlayerUpdate;

            var ownerEvents = new List<EventEnvelope>();
            for (int i = 0; i < owner.Frames.Count; i++)
            {
                ownerEvents.AddRange(StrictWireStreamCodec.DecodeAndStrict(
                    owner.Frames[i].EncodedPayload!).PlayerUpdate.VisibleEvents);
            }

            Assert.Contains(ownerEvents, e => e.EventCase == EventEnvelope.EventOneofCase.CheatPointsChanged);
            Assert.Contains(ownerEvents, e => e.EventCase == EventEnvelope.EventOneofCase.SleeveBloomResolved);
            CheatPointsChanged points = ownerEvents
                .Single(e => e.EventCase == EventEnvelope.EventOneofCase.CheatPointsChanged)
                .CheatPointsChanged;
            Assert.Equal(-10L, points.Amount);
            Assert.Equal(990L, points.NewBalance);
            SleeveBloomResolved resolved = ownerEvents
                .Single(e => e.EventCase == EventEnvelope.EventOneofCase.SleeveBloomResolved)
                .SleeveBloomResolved;
            Assert.Equal("bloom-1-sb-1", resolved.OfferId);
            Assert.True(resolved.KeptFirst);
            Assert.True(resolved.HasResultTileId);

            string spectatorWire = System.Text.Encoding.UTF8.GetString(
                spectator.Frames[spectator.Frames.Count - 1].EncodedPayload!);
            string otherWire = System.Text.Encoding.UTF8.GetString(
                other.Frames[other.Frames.Count - 1].EncodedPayload!);
            Assert.DoesNotContain("bloom-1-sb-1", spectatorWire);
            Assert.DoesNotContain("bloom-1-sb-1", otherWire);
            Assert.DoesNotContain(otherUpdate.VisibleEvents, e =>
                e.EventCase == EventEnvelope.EventOneofCase.CheatPointsChanged ||
                e.EventCase == EventEnvelope.EventOneofCase.SleeveBloomResolved);
            Assert.Equal(990L, ownerUpdate.Snapshot.PrivateView.Cheat.CheatPoints);
        }

        private static TableStreamSubscribeRequest Subscribe(
            RoundStateMachine round,
            TableStreamAudience audience,
            string session,
            RecordingStreamSink sink)
        {
            return new TableStreamSubscribeRequest(
                new TableStreamIdentity(
                    new SessionId(session),
                    audience,
                    new BindingEpoch(1),
                    new SyncEpoch(1)),
                new OwnerEpoch(1),
                0L,
                sink,
                new RoundId(round.RoundId),
                audience.IsSpectator ? null : new ControlEpoch(0));
        }

        private static async Task<TableOperationResult> SubmitActorRoundAsync(
            TableActor actor,
            TableCommandEnvelope source,
            int expectedVersion,
            string requestId)
        {
            var envelope = new TableCommandEnvelope(
                source.Command,
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(0));
            return await actor.SubmitAsync(envelope, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task tile_mark_from_the_wrong_seat_is_rejected_with_zero_side_effects()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 1).Take(4).ToArray();
            TableOperationResult rejected = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitTileMark, "mark-wrong-seat",
                    RoundCheatPayload.TileMark("g1", tiles), 0, "mark-wrong-seat"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal("NOT_YOUR_TURN", rejected.ReasonCode);
            Assert.Empty(rejected.Events);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task pending_decision_and_authoritative_deadline_bind_cheat_commands()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            var key = new DecisionKey(round.RoundId, 0, "d1");
            var pending = new PendingDecision(
                key,
                0L,
                new[] { new LegalActionOption(LegalActionKind.Tsumo, 0, null) });
            Assert.Equal(TableOperationStatus.Configured, (await harness.OpenDecisionAsync(pending)).Status);
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();

            TableOperationResult wrongDecision = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "mark-wrong",
                    RoundCheatPayload.TileMark("g1", tiles), 0, "mark-wrong",
                    decision: new DecisionId("other")),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, wrongDecision.Status);
            Assert.Equal(TableRuntimeReasonCode.DecisionMismatch, wrongDecision.ReasonCode);

            harness.Clock.Now = 3000L;
            TableOperationResult late = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "mark-late",
                    RoundCheatPayload.TileMark("g1", tiles), 0, "mark-late",
                    decision: new DecisionId("d1")),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, late.Status);
            Assert.Equal(TableRuntimeReasonCode.DeadlineExpired, late.ReasonCode);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task accepted_cheat_command_does_not_silently_consume_an_unrelated_open_decision()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            var key = new DecisionKey(round.RoundId, 0, "d1");
            var pending = new PendingDecision(
                key,
                0L,
                new[] { new LegalActionOption(LegalActionKind.Tsumo, 0, null) });
            Assert.Equal(TableOperationStatus.Configured, (await harness.OpenDecisionAsync(pending)).Status);
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();

            TableOperationResult accepted = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "mark-1",
                    RoundCheatPayload.TileMark("g1", tiles), 0, "mark-1",
                    decision: new DecisionId("d1")),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);

            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            Assert.NotNull(timing.Seats[0].PendingKey);
            Assert.Equal("d1", timing.Seats[0].PendingKey!.Value.DecisionId);
        }

        [Fact]
        public async Task beam_swap_swap_moves_exactly_the_frozen_hand_and_wall_entities_and_keeps_136_unique()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            await DiscardOnceAsync(harness, "discard-0");
            TableOperationResult activation = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitBeamSwap, "bs-1", RoundCheatPayload.BeamSwap(), 1, "bs-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activation.Status);

            RoundStateMachine before = GetRound(harness.Actor);
            CheatState cheatBefore = GetCheat(harness.Actor)!;
            BeamSwapOffer offer = cheatBefore.BeamSwap.Offer!;
            BeamSwapCandidate candidate = offer.Candidates[0];
            TileId handTile = offer.EligibleHandTileIds[0];
            TilePosition handBefore = before.Tiles.PositionOf(handTile);
            TilePosition wallBefore = before.Tiles.PositionOf(candidate.Tile);

            TableOperationResult choice = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitBeamSwapChoice, "bs-choice",
                    RoundCheatPayload.BeamSwapChoice(offer.OfferId, BeamSwapAction.Swap, handTile, candidate.Slot),
                    2, "bs-choice"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, choice.Status);
            TableEventRecord resolved = Assert.Single(
                choice.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.BeamSwapResolved);
            Assert.Equal(BeamSwapAction.Swap, resolved.Cheat!.Action);
            Assert.Equal(handTile, resolved.Cheat.HandTile);
            Assert.Equal(candidate.Slot, resolved.Cheat.TargetSlot);

            RoundStateMachine after = GetRound(harness.Actor);
            Assert.Equal(wallBefore.Zone == TileZone.Wall ? TileZone.Hand : wallBefore.Zone, after.Tiles.PositionOf(candidate.Tile).Zone);
            Assert.Equal(TileZone.Wall, after.Tiles.PositionOf(handTile).Zone);
            Assert.Equal(136, after.Tiles.AsPlacements().Select(p => p.Tile.Value).Distinct().Count());
            Assert.Contains(after.Tiles.AsPlacements(), p => p.Tile.Equals(handTile) && p.Position.Zone == TileZone.Wall);
        }

        [Fact]
        public async Task durable_recovery_fence_rejects_a_delayed_cheat_command_from_the_old_generation()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();
            TableOperationResult first = await harness.Actor.SubmitCheatAsync(
                DurableCheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "mark-1",
                    RoundCheatPayload.TileMark("g1", tiles), 0, harness.Scope),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);

            TableRecoveryResult recovered = await harness.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableRecoveryStatus.Recovered, recovered.Status);

            TableCheatCommandEnvelope stale = new TableCheatCommandEnvelope(
                new RoundCheatCommand(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "mark-stale",
                    RoundCheatPayload.TileMark("g2", tiles)),
                new RequestId("mark-stale"),
                new StateVersion(1),
                new OwnerEpoch(2),
                new ControlEpoch(0),
                null,
                0L,
                harness.Scope.ScopeId,
                "CHEAT",
                TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes("stale")));
            TableOperationResult rejected = await harness.Actor.SubmitCheatAsync(
                stale,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleRecoveryGeneration, rejected.ReasonCode);
            Assert.Equal(1UL, (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
        }

        private static RoundStateMachine GetRound(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField("_round", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (RoundStateMachine)field.GetValue(actor)!;
        }

        private static CheatState? GetCheat(TableActor actor)
        {
            FieldInfo field = typeof(TableActor).GetField("_cheat", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (CheatState?)field.GetValue(actor);
        }

        private static async Task<DurableHarness> CreateDurableCheatHarnessAsync(
            int cheatRequestBindingCapacity = 8192)
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(
                    request,
                    new WireEventFactOptions
                    {
                        WinYaku = new[] { new Mahjong.Contracts.YakuView("TSUMO", 1U) },
                        WinHan = 1U,
                        WinFu = 30U,
                        WinScore = 1000L
                    })
            };
            return await DurableHarness.CreateAsync(
                ledger: cheat.Ledger,
                initialCheckpoint: WithCheat(DurableHarness.InitialCheckpoint(round, cheat.Ledger), cheat),
                projectionSource: source,
                configure: options =>
                {
                    options.CheatEconomy = V32Fixtures.Economy(InitialPoints);
                    options.SleeveBloomConfig = V32Fixtures.SleeveConfig();
                    options.CheatRequestBindingCapacity = cheatRequestBindingCapacity;
                    options.InitialCheatState = cheat;
                });
        }

        [Fact]
        public async Task durable_journal_recovery_restores_the_off_turn_reservation_and_still_delivers_it_once()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            RoundStateMachine round = harness.InitialRound;
            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 4);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitAsync(
                DurableRound(harness.Scope, chain[0].Command, 0, "d0"),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                DurableCheat(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurn, "off-1",
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"), 1, harness.Scope),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                DurableCheat(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurnChoice, "off-choice",
                    RoundCheatPayload.OffTurnChoice("bloom-offturn-0-off-1", SleeveBloomKeep.First), 2, harness.Scope),
                TestContext.Current.CancellationToken)).Status);

            TableRecoveryResult recovered = await harness.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableRecoveryStatus.Recovered, recovered.Status);
            Assert.Equal(3, recovered.Report!.ReplayedCommands);
            Assert.NotNull(GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(0));

            // Save a checkpoint that contains the open offer/reservation/candidates and the
            // committed cheat history, then recover from that checkpoint alone.
            await harness.SaveCheckpointAsync();
            TableRecoveryResult checkpointRecovery = await harness.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(TableRecoveryStatus.Recovered, checkpointRecovery.Status);
            Assert.Equal(0, checkpointRecovery.Report!.ReplayedCommands);
            Assert.NotNull(GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(0));

            TableSnapshot afterCheckpoint = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            ulong ownerEpoch = afterCheckpoint.OwnerEpoch.Value;
            TableOperationResult firstDiscard = await harness.Actor.SubmitAsync(
                DurableRound(harness.Scope, chain[1].Command, 3, "d1", 2L, ownerEpoch),
                TestContext.Current.CancellationToken);
            Assert.True(
                firstDiscard.Status == TableOperationStatus.Accepted,
                firstDiscard.ReasonCode + "|" + firstDiscard.Detail);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitAsync(
                DurableRound(harness.Scope, chain[2].Command, 4, "d2", 2L, ownerEpoch),
                TestContext.Current.CancellationToken)).Status);
            TableOperationResult delivery = await harness.Actor.SubmitAsync(
                DurableRound(harness.Scope, chain[3].Command, 5, "d3", 2L, ownerEpoch),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, delivery.Status);
            Assert.Contains(delivery.Events, e => e.Type == RoundEventType.TileDrawn && e.Seat == 0);
            Assert.Contains(delivery.Events, e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomReservationDelivered);
            Assert.DoesNotContain(delivery.Events, e => e.Cheat?.Event.Kind == CheatEventKind.CheatPointsChanged);
        }

        private static TableCommandEnvelope DurableRound(
            TableScopeRecord scope,
            RoundCommand command,
            int expectedVersion,
            string requestId,
            long recoveryGeneration = 0L,
            ulong ownerEpoch = 1UL)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("ROUND|" + requestId + "|" + command.Action);
            return new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(ownerEpoch),
                new ControlEpoch(0),
                null,
                recoveryGeneration,
                scope.ScopeId,
                "ROUND",
                TablePayloadSha256.Compute(bytes));
        }

        [Fact]
        public async Task beam_swap_resolution_wire_carries_offer_action_hand_and_target()
        {
            var codec = new StrictWireStreamCodec();
            var source = new TestProjectionSource { EventFactsFactory = request => WireEventFacts.Build(request) };
            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = new RecordingStreamTerminationSink()
            };
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var publicationSink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(
                options, round, commitPort, clock, publicationSink, null, source, streamOptions);
            var owner = new RecordingStreamSink();
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.ForSeat(1), "owner", owner),
                TestContext.Current.CancellationToken)).Status);
            await owner.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 1);
            Assert.Equal(TableOperationStatus.Accepted, (await SubmitActorRoundAsync(actor, chain[0], 0, "d0")).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitBeamSwap, "bs-1", RoundCheatPayload.BeamSwap(), 1, "bs-1"),
                TestContext.Current.CancellationToken)).Status);
            CheatState current = GetCheat(actor)!;
            BeamSwapOffer offer = current.BeamSwap.Offer!;
            BeamSwapCandidate candidate = offer.Candidates[0];
            TileId handTile = offer.EligibleHandTileIds[0];
            Assert.Equal(TableOperationStatus.Accepted, (await actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitBeamSwapChoice, "bs-choice",
                    RoundCheatPayload.BeamSwapChoice(offer.OfferId, BeamSwapAction.Swap, handTile, candidate.Slot),
                    2, "bs-choice"),
                TestContext.Current.CancellationToken)).Status);

            await owner.WaitForCountAsync(4);
            var events = new List<EventEnvelope>();
            for (int i = 0; i < owner.Frames.Count; i++)
            {
                events.AddRange(StrictWireStreamCodec.DecodeAndStrict(
                    owner.Frames[i].EncodedPayload!).PlayerUpdate.VisibleEvents);
            }

            BeamSwapResolved resolved = events
                .Single(e => e.EventCase == EventEnvelope.EventOneofCase.BeamSwapResolved)
                .BeamSwapResolved;
            Assert.Equal("beam-1-bs-1", resolved.OfferId);
            Assert.Equal("SWAP", resolved.Action);
            Assert.Equal(unchecked((uint)handTile.Value), resolved.HandTileId);
            Assert.Equal(unchecked((uint)candidate.Slot.Index), resolved.TargetSlot);
        }

        private static async Task<TableOperationResult> SubmitRoundAsync(
            ActorHarness harness,
            TableCommandEnvelope source,
            int expectedVersion,
            string requestId)
        {
            return await harness.SubmitAsync(new TableCommandEnvelope(
                source.Command,
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(1),
                new ControlEpoch(0)));
        }

        [Fact]
        public async Task inner_and_outer_request_ids_must_match_before_domain_execution()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(8).ToArray();
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "r1",
                    RoundCheatPayload.TileMark("g1", tiles.Take(4).ToArray()), 0, "first"),
                TestContext.Current.CancellationToken)).Status);

            var mismatched = new TableCheatCommandEnvelope(
                new RoundCheatCommand(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "r1",
                    RoundCheatPayload.TileMark("g2", tiles.Skip(4).ToArray())),
                new RequestId("r2"), new StateVersion(1), new OwnerEpoch(1), new ControlEpoch(0),
                null, 0, new RequestScopeId("scope-1"), "CHEAT", TablePayloadSha256.Compute(new byte[] { 2 }));
            TableOperationResult result = await harness.Actor.SubmitCheatAsync(
                mismatched, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, result.Status);
            Assert.Empty(result.Events);
            Assert.Single(GetCheat(harness.Actor)!.TileMarks.Groups);
            Assert.Equal(995, GetCheat(harness.Actor)!.Ledger.BalanceOf(0));
        }

        [Fact]
        public async Task business_rejection_replays_after_turn_changes_and_distinct_scope_cannot_reuse_private_result()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            TileId[] seat1Tiles = round.Tiles.TilesInZone(TileZone.Hand, 1).Take(4).ToArray();
            TableOperationResult first = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitTileMark, "reject-once",
                    RoundCheatPayload.TileMark("g", seat1Tiles), 0, "same"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, first.Status);

            await DiscardOnceAsync(harness, "discard");
            TableOperationResult replay = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitTileMark, "reject-once",
                    RoundCheatPayload.TileMark("g", seat1Tiles), 1, "same"),
                TestContext.Current.CancellationToken);
            Assert.Equal(first.Status, replay.Status);
            Assert.Equal(first.ReasonCode, replay.ReasonCode);
            Assert.Empty(replay.Events);

            TileId[] seat0Tiles = round.Tiles.TilesInZone(TileZone.Hand, 1).Take(4).ToArray();
            TableCheatCommandEnvelope accepted = Cheat(
                round.RoundId, 1, RoundCheatAction.SubmitTileMark, "scope-request",
                RoundCheatPayload.TileMark("g0", seat0Tiles), 1, "scope-request");
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                accepted, TestContext.Current.CancellationToken)).Status);

            var otherScope = new TableCheatCommandEnvelope(
                accepted.CheatCommand, accepted.RequestId, accepted.ExpectedStateVersion,
                accepted.OwnerEpoch, accepted.ControlEpoch, null, 0,
                new RequestScopeId("other-scope"), "OTHER_API", accepted.PayloadSha256);
            TableOperationResult isolated = await harness.Actor.SubmitCheatAsync(
                otherScope, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, isolated.Status);
            Assert.Empty(isolated.Events);
        }

        [Fact]
        public async Task internal_timeout_discard_opens_window_and_preserves_foreign_reservation()
        {
            await using ActorHarness harness = CreateHarness();
            await DiscardOnceAsync(harness, "d0");
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat("round-1", 0, RoundCheatAction.SubmitSleeveBloomOffTurn, "off",
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"), 1, "off"),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat("round-1", 0, RoundCheatAction.SubmitSleeveBloomOffTurnChoice, "choose",
                    RoundCheatPayload.OffTurnChoice("bloom-offturn-0-off", SleeveBloomKeep.First), 2, "choose"),
                TestContext.Current.CancellationToken)).Status);
            TileId reserved = GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(0)!.Tile;

            RoundStateMachine round = GetRound(harness.Actor);
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, 1)[0];
            var pending = new PendingDecision(
                new DecisionKey("round-1", 1, "timeout"), 0L,
                new[] { new LegalActionOption(LegalActionKind.Discard, 1, tile) }, tile);
            Assert.Equal(TableOperationStatus.Configured, (await harness.OpenDecisionAsync(pending)).Status);
            harness.Clock.Now = 3000L;
            await harness.TickAsync();
            Assert.Equal(TileZone.Wall, GetRound(harness.Actor).Tiles.PositionOf(reserved).Zone);
            Assert.NotNull(GetCheat(harness.Actor)!.SleeveBloom.WindowIdFor(1));
        }

        [Fact]
        public async Task response_pass_after_discard_preserves_foreign_reservation()
        {
            await using ActorHarness harness = CreateHarness();
            await DiscardOnceAsync(harness, "d0");
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat("round-1", 0, RoundCheatAction.SubmitSleeveBloomOffTurn, "off",
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"), 1, "off"),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat("round-1", 0, RoundCheatAction.SubmitSleeveBloomOffTurnChoice, "choose",
                    RoundCheatPayload.OffTurnChoice("bloom-offturn-0-off", SleeveBloomKeep.First), 2, "choose"),
                TestContext.Current.CancellationToken)).Status);
            TileId reserved = GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(0)!.Tile;

            RoundStateMachine round = GetRound(harness.Actor);
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, 1)[0];
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(new TableCommandEnvelope(
                RoundCommand.Discard("round-1", 1, tile, new[] { 2 }), new RequestId("discard1"),
                new StateVersion(3), new OwnerEpoch(1), new ControlEpoch(0)))).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(new TableCommandEnvelope(
                RoundCommand.Pass("round-1", 2), new RequestId("pass"),
                new StateVersion(4), new OwnerEpoch(1), new ControlEpoch(0)))).Status);
            Assert.Equal(TileZone.Wall, GetRound(harness.Actor).Tiles.PositionOf(reserved).Zone);
        }

        [Fact]
        public async Task normal_discard_settles_pending_immediate_and_beam_offers()
        {
            await using ActorHarness harness = CreateHarness();
            await DiscardOnceAsync(harness, "d0");
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat("round-1", 1, RoundCheatAction.SubmitSleeveBloom, "immediate",
                    RoundCheatPayload.SleeveBloom(), 1, "immediate"),
                TestContext.Current.CancellationToken)).Status);
            RoundStateMachine round = GetRound(harness.Actor);
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, 1)[0];
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(new TableCommandEnvelope(
                RoundCommand.Discard("round-1", 1, tile), new RequestId("discard1"),
                new StateVersion(2), new OwnerEpoch(1), new ControlEpoch(0)))).Status);
            Assert.Null(GetCheat(harness.Actor)!.SleeveBloom.ImmediateOffer);

            await using ActorHarness beamHarness = CreateHarness();
            await DiscardOnceAsync(beamHarness, "d0");
            Assert.Equal(TableOperationStatus.Accepted, (await beamHarness.Actor.SubmitCheatAsync(
                Cheat("round-1", 1, RoundCheatAction.SubmitBeamSwap, "beam",
                    RoundCheatPayload.BeamSwap(), 1, "beam"),
                TestContext.Current.CancellationToken)).Status);
            RoundStateMachine beamRound = GetRound(beamHarness.Actor);
            TileId beamTile = beamRound.Tiles.TilesInZone(TileZone.Hand, 1)[0];
            Assert.Equal(TableOperationStatus.Accepted, (await beamHarness.SubmitAsync(new TableCommandEnvelope(
                RoundCommand.Discard("round-1", 1, beamTile), new RequestId("beam-discard"),
                new StateVersion(2), new OwnerEpoch(1), new ControlEpoch(0)))).Status);
            Assert.Null(GetCheat(beamHarness.Actor)!.BeamSwap.Offer);
        }

        [Fact]
        public async Task durable_cheat_binding_is_persisted_and_tamper_recovery_is_refused()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                DurableCheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "bind",
                    RoundCheatPayload.TileMark("bind", tiles), 0, harness.Scope),
                TestContext.Current.CancellationToken)).Status);

            CheatState current = GetCheat(harness.Actor)!;
            TableStateRow state = (await harness.Store.LoadStateAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken))!;
            Assert.Equal(current.Version, state.CheatVersion);
            Assert.Equal(current.Fingerprint, state.CheatFingerprint);

            harness.Store.SetCheatBindingForTest(
                DurableHarness.Room, current.Version + 1, current.Fingerprint);
            TableRecoveryResult recovered = await harness.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(TableRecoveryStatus.Failed, recovered.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryStateHashMismatch, recovered.ReasonCode);
        }

        [Fact]
        public async Task durable_commit_fault_leaves_no_cheat_state_version_or_charge()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();
            harness.Store.ArmedFaultPoint = TableStoreFaultPoint.BeforeApply;
            TableOperationResult failed = await harness.Actor.SubmitCheatAsync(
                DurableCheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "fault",
                    RoundCheatPayload.TileMark("fault", tiles), 0, harness.Scope),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            TableStateRow state = (await harness.Store.LoadStateAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken))!;
            Assert.Equal(0UL, state.StateVersion.Value);
            Assert.Equal(0L, state.CheatVersion);
            Assert.Equal(0UL, state.CheatFingerprint);
        }

        [Fact]
        public async Task terminal_tsumo_settles_a_pending_immediate_offer_before_ending_the_round()
        {
            await using ActorHarness harness = CreateHarness();
            await DiscardOnceAsync(harness, "d0");
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat("round-1", 1, RoundCheatAction.SubmitSleeveBloom, "immediate",
                    RoundCheatPayload.SleeveBloom(), 1, "immediate"),
                TestContext.Current.CancellationToken)).Status);

            long[] finalScores = { 25000, 26000, 24000, 25000 };
            var settlement = new RoundSettlement(
                "terminal-1",
                RoundOutcome.Tsumo(1),
                finalScores,
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero)),
                4321L);
            TableOperationResult tsumo = await harness.SubmitAsync(new TableCommandEnvelope(
                RoundCommand.Tsumo("round-1", 1, settlement),
                new RequestId("tsumo"),
                new StateVersion(2),
                new OwnerEpoch(1),
                new ControlEpoch(0)));
            Assert.Equal(TableOperationStatus.Accepted, tsumo.Status);
            Assert.Contains(tsumo.Events, e => e.Type == RoundEventType.TsumoWin);
            Assert.Null(GetCheat(harness.Actor)!.SleeveBloom.ImmediateOffer);
            Assert.Equal(3UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task closed_kan_replacement_skips_a_protected_live_wall_tail_tile()
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = BuildKanProtectionRound("kan-protect");
            const int protectedValue = 135;
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            cheat = cheat.WithSleeveBloom(cheat.SleeveBloom.WithReservation(
                new SleeveBloomReservation(1, "reservation", new TileId(protectedValue), null, 0)));
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(options, round, commitPort, clock, sink);
            TableOperationResult result = await actor.SubmitAsync(
                new TableCommandEnvelope(
                    RoundCommand.ClosedKan(
                        round.RoundId,
                        0,
                        new TileId(0),
                        new TileId(1),
                        new TileId(2),
                        new TileId(3)),
                    new RequestId("kan"),
                    new StateVersion(0),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);
            Assert.True(result.Status == TableOperationStatus.Accepted, result.ReasonCode + "|" + result.Detail);
            RoundStateMachine after = GetRound(actor);
            Assert.Equal(TileZone.Wall, after.Tiles.PositionOf(new TileId(protectedValue)).Zone);
            Assert.NotEqual(protectedValue, after.Tiles.DrawnTile!.Value.Tile.Value);
            Assert.Equal(136, after.Tiles.AsPlacements().Select(p => p.Tile.Value).Distinct().Count());
        }

        private static RoundStateMachine BuildKanProtectionRound(string roundId)
        {
            var placements = new List<TilePlacement>();
            for (int value = 0; value < 4; value++)
            {
                placements.Add(new TilePlacement(new TileId(value), TilePosition.Hand(0, value == 3 ? 13 : value)));
            }

            for (int value = 4; value < 14; value++)
            {
                placements.Add(new TilePlacement(new TileId(value), TilePosition.Hand(0, value - 1)));
            }

            int next = 14;
            for (int seat = 1; seat < 4; seat++)
            {
                for (int slot = 0; slot < 13; slot++)
                {
                    placements.Add(new TilePlacement(new TileId(next++), TilePosition.Hand(seat, slot)));
                }
            }

            for (int slot = 0; slot < TilePositionMap.DeadWallSlotCount; slot++)
            {
                placements.Add(new TilePlacement(new TileId(next++), TilePosition.DeadWall(slot)));
            }

            for (int slot = 0; slot < 69; slot++)
            {
                placements.Add(new TilePlacement(new TileId(next++), TilePosition.Wall(slot)));
            }

            Assert.Equal(136, next);
            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                0,
                69,
                0,
                null,
                0,
                null,
                Array.Empty<TileId>(),
                0,
                new DrawnTileEvidence(new TileId(3), 0, DrawSource.DealExtra));
            var spec = new RoundSpec(
                roundId,
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            return RoundStateMachine.CreateDealt(spec, map, RoundPhase.CheatWindow);
        }

        [Fact]
        public async Task riichi_automatic_draw_skips_a_protected_reservation_tile()
        {
            RoundStateMachine round = BuildRiichiProtectionRound("riichi-protect", out TileId protectedWall);
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            cheat = cheat.WithSleeveBloom(cheat.SleeveBloom.WithReservation(
                new SleeveBloomReservation(1, "reservation", protectedWall, null, 0)));
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(options, round, commitPort, clock, sink);
            TableOperationResult result = await actor.SubmitAsync(
                new TableCommandEnvelope(
                    RoundCommand.Riichi(round.RoundId, 0, TileId.FromTypeIndex(26 + 4, 0)),
                    new RequestId("riichi"),
                    new StateVersion(0),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);
            Assert.True(result.Status == TableOperationStatus.Accepted, result.ReasonCode + "|" + result.Detail);
            RoundStateMachine after = GetRound(actor);
            Assert.Null(GetCheat(actor)!.SleeveBloom.ReservationFor(1));
            Assert.Equal(TileZone.Hand, after.Tiles.PositionOf(protectedWall).Zone);
            Assert.Equal(1, after.Tiles.DrawnTile!.Value.Seat);
            Assert.Equal(protectedWall, after.Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(136, after.Tiles.AsPlacements().Select(p => p.Tile.Value).Distinct().Count());
        }

        private static RoundStateMachine BuildRiichiProtectionRound(string roundId, out TileId protectedWall)
        {
            var hand = new[]
            {
                TileId.FromTypeIndex(30 + 1, 0), TileId.FromTypeIndex(30 + 1, 1), TileId.FromTypeIndex(30 + 1, 2),
                TileId.FromTypeIndex(1 - 1, 0), TileId.FromTypeIndex(2 - 1, 0), TileId.FromTypeIndex(3 - 1, 0),
                TileId.FromTypeIndex(17 + 6, 0), TileId.FromTypeIndex(17 + 7, 0), TileId.FromTypeIndex(17 + 8, 0),
                TileId.FromTypeIndex(8 + 2, 0), TileId.FromTypeIndex(8 + 2, 1), TileId.FromTypeIndex(8 + 5, 0),
                TileId.FromTypeIndex(8 + 6, 0)
            };
            var placements = new List<TilePlacement>();
            var used = new bool[TileId.Count];
            void Place(TileId tile, TilePosition position)
            {
                Assert.False(used[tile.Value]);
                used[tile.Value] = true;
                placements.Add(new TilePlacement(tile, position));
            }

            for (int i = 0; i < hand.Length; i++)
            {
                Place(hand[i], TilePosition.Hand(0, i));
            }

            Place(TileId.FromTypeIndex(26 + 4, 0), TilePosition.Hand(0, 13));

            int Next()
            {
                for (int value = 0; value < TileId.Count; value++)
                {
                    if (!used[value])
                    {
                        return value;
                    }
                }

                throw new InvalidOperationException("no tile left");
            }

            for (int slot = 0; slot < TilePositionMap.DeadWallSlotCount; slot++)
            {
                Place(new TileId(Next()), TilePosition.DeadWall(slot));
            }

            for (int seat = 1; seat < 4; seat++)
            {
                for (int slot = 0; slot < 13; slot++)
                {
                    Place(new TileId(Next()), TilePosition.Hand(seat, slot));
                }
            }

            int wallCount = 0;
            while (wallCount < 69)
            {
                int value = Next();
                Place(new TileId(value), TilePosition.Wall(wallCount++));
            }

            protectedWall = new TileId(0);
            for (int value = 0; value < TileId.Count; value++)
            {
                if (placements.Exists(p => p.Tile.Value == value && p.Position.Equals(TilePosition.Wall(0))))
                {
                    protectedWall = new TileId(value);
                    break;
                }
            }

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                0,
                69,
                0,
                null,
                0,
                null,
                new[] { placements.Find(p => p.Position.Equals(TilePosition.DeadWall(4))).Tile },
                0,
                new DrawnTileEvidence(TileId.FromTypeIndex(26 + 4, 0), 0, DrawSource.DealExtra));
            var spec = new RoundSpec(roundId, 0, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0);
            return RoundStateMachine.CreateDealt(spec, map, RoundPhase.CheatWindow);
        }

        [Fact]
        public async Task durable_terminal_tsumo_with_pending_offer_commits_checkpoint_and_outbox()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            RoundStateMachine round = harness.InitialRound;
            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 1);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitAsync(
                DurableRound(harness.Scope, chain[0].Command, 0, "d0"),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                DurableCheat(round.RoundId, 1, RoundCheatAction.SubmitSleeveBloom, "immediate",
                    RoundCheatPayload.SleeveBloom(), 1, harness.Scope),
                TestContext.Current.CancellationToken)).Status);

            long[] finalScores = { 25000, 26000, 24000, 25000 };
            var settlement = new RoundSettlement(
                "terminal-durable",
                RoundOutcome.Tsumo(1),
                finalScores,
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.Zero)),
                9999L);
            TableOperationResult tsumo = await harness.Actor.SubmitAsync(
                DurableRound(harness.Scope, RoundCommand.Tsumo(round.RoundId, 1, settlement), 2, "tsumo"),
                TestContext.Current.CancellationToken);
            Assert.True(tsumo.Status == TableOperationStatus.Accepted, tsumo.ReasonCode + "|" + tsumo.Detail);
            Assert.Null(GetCheat(harness.Actor)!.SleeveBloom.ImmediateOffer);
            Assert.NotNull(await harness.Store.LoadCheckpointAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken));

            TableRecoveryResult recovered = await harness.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(TableRecoveryStatus.Recovered, recovered.Status);
            Assert.True(recovered.Report!.Terminal);
        }

        [Fact]
        public async Task old_owner_epoch_is_rejected_after_recovery_and_concurrent_cheats_commit_at_most_once()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                DurableCheat(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "old-owner-first",
                    RoundCheatPayload.TileMark("g1", tiles), 0, harness.Scope),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableRecoveryStatus.Recovered, (await harness.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken)).Status);

            var oldOwner = new TableCheatCommandEnvelope(
                new RoundCheatCommand(round.RoundId, 0, RoundCheatAction.SubmitTileMark, "old-owner",
                    RoundCheatPayload.TileMark("g2", tiles)),
                new RequestId("old-owner"), new StateVersion(1), new OwnerEpoch(1), new ControlEpoch(0),
                null, 1L, harness.Scope.ScopeId, "CHEAT",
                TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes("old-owner")));
            TableOperationResult ownerRejected = await harness.Actor.SubmitCheatAsync(
                oldOwner, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, ownerRejected.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, ownerRejected.ReasonCode);
            Assert.Empty(ownerRejected.Events);

            await using ActorHarness concurrent = CreateHarness();
            TileId[] concurrentTiles = concurrent.InitialRound.Tiles
                .TilesInZone(TileZone.Hand, 0).Take(8).ToArray();
            Task<TableOperationResult> first = concurrent.Actor.SubmitCheatAsync(
                Cheat("round-1", 0, RoundCheatAction.SubmitTileMark, "parallel-1",
                    RoundCheatPayload.TileMark("p1", concurrentTiles.Take(4).ToArray()), 0, "parallel-1"),
                TestContext.Current.CancellationToken).AsTask();
            Task<TableOperationResult> second = concurrent.Actor.SubmitCheatAsync(
                Cheat("round-1", 0, RoundCheatAction.SubmitTileMark, "parallel-2",
                    RoundCheatPayload.TileMark("p2", concurrentTiles.Skip(4).ToArray()), 0, "parallel-2"),
                TestContext.Current.CancellationToken).AsTask();
            TableOperationResult[] results = await Task.WhenAll(first, second);
            Assert.Equal(1, results.Count(r => r.Status == TableOperationStatus.Accepted));
            Assert.Equal(1UL, (await concurrent.GetSnapshotAsync()).StateVersion.Value);
            Assert.Single(GetCheat(concurrent.Actor)!.TileMarks.Groups);
        }

        [Fact]
        public async Task response_pass_delivers_a_reservation_to_the_next_owner_draw()
        {
            await using ActorHarness harness = CreateHarness();
            RoundStateMachine round = harness.InitialRound;
            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 4);
            Assert.Equal(TableOperationStatus.Accepted, (await SubmitRoundAsync(harness, chain[0], 0, "d0")).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat("round-1", 0, RoundCheatAction.SubmitSleeveBloomOffTurn, "off",
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"), 1, "off"),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                Cheat("round-1", 0, RoundCheatAction.SubmitSleeveBloomOffTurnChoice, "choose",
                    RoundCheatPayload.OffTurnChoice("bloom-offturn-0-off", SleeveBloomKeep.First), 2, "choose"),
                TestContext.Current.CancellationToken)).Status);
            TileId reserved = GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(0)!.Tile;

            Assert.Equal(TableOperationStatus.Accepted, (await SubmitRoundAsync(harness, chain[1], 3, "d1")).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await SubmitRoundAsync(harness, chain[2], 4, "d2")).Status);
            RoundStateMachine beforeSeat3 = GetRound(harness.Actor);
            TileId seat3Tile = beforeSeat3.Tiles.TilesInZone(TileZone.Hand, 3)[0];
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(new TableCommandEnvelope(
                RoundCommand.Discard("round-1", 3, seat3Tile, new[] { 1 }),
                new RequestId("d3"),
                new StateVersion(5),
                new OwnerEpoch(1),
                new ControlEpoch(0)))).Status);

            TableOperationResult pass = await harness.SubmitAsync(new TableCommandEnvelope(
                RoundCommand.Pass("round-1", 1),
                new RequestId("pass"),
                new StateVersion(6),
                new OwnerEpoch(1),
                new ControlEpoch(0)));
            Assert.Equal(TableOperationStatus.Accepted, pass.Status);
            Assert.Equal(7UL, pass.CommandVersion!.Value.Value);
            Assert.Null(GetCheat(harness.Actor)!.SleeveBloom.ReservationFor(0));
            TableEventRecord drawn = Assert.Single(pass.Events, e => e.Type == RoundEventType.TileDrawn && e.Seat == 0);
            Assert.Equal(reserved, drawn.Tile);
        }

        [Fact]
        public async Task cheat_request_binding_capacity_rejects_new_ids_without_evicting_replays()
        {
            await using ActorHarness harness = CreateHarness(cheatRequestBindingCapacity: 2);
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 1).Take(4).ToArray();
            TableOperationResult first = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitTileMark, "bound-1",
                    RoundCheatPayload.TileMark("g1", tiles), 0, "bound-1"),
                TestContext.Current.CancellationToken);
            TableOperationResult second = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitTileMark, "bound-2",
                    RoundCheatPayload.TileMark("g2", tiles), 0, "bound-2"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, first.Status);
            Assert.Equal(TableOperationStatus.Rejected, second.Status);

            TableOperationResult third = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitTileMark, "bound-3",
                    RoundCheatPayload.TileMark("g3", tiles), 0, "bound-3"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, third.Status);
            Assert.Equal(TableRuntimeReasonCode.CheatReceiptCapacityExceeded, third.ReasonCode);

            TableOperationResult replay = await harness.Actor.SubmitCheatAsync(
                Cheat(round.RoundId, 1, RoundCheatAction.SubmitTileMark, "bound-1",
                    RoundCheatPayload.TileMark("g1", tiles), 0, "bound-1"),
                TestContext.Current.CancellationToken);
            Assert.Equal(first.Status, replay.Status);
            Assert.Equal(first.ReasonCode, replay.ReasonCode);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task cold_recovery_capacity_shrink_still_replays_a_committed_durable_receipt()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            RoundStateMachine round = harness.InitialRound;
            TileId[] tiles = round.Tiles.TilesInZone(TileZone.Hand, 0).Take(8).ToArray();
            TableCheatCommandEnvelope first = DurableCheat(
                round.RoundId, 0, RoundCheatAction.SubmitTileMark, "cold-first",
                RoundCheatPayload.TileMark("g1", tiles.Take(4).ToArray()), 0, harness.Scope);
            TableCheatCommandEnvelope second = DurableCheat(
                round.RoundId, 0, RoundCheatAction.SubmitTileMark, "cold-second",
                RoundCheatPayload.TileMark("g2", tiles.Skip(4).ToArray()), 1, harness.Scope);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                first, TestContext.Current.CancellationToken)).Status);
            TableOperationResult original = await harness.Actor.SubmitCheatAsync(
                second, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, original.Status);
            await harness.SaveCheckpointAsync();

            await using TableActor actor = AttachDurable(harness, harness.Clock, DurableHarness.Boot, 1);
            Assert.Equal(TableRecoveryStatus.Recovered, (await actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken)).Status);
            TableOperationResult replay = await actor.SubmitCheatAsync(
                second, TestContext.Current.CancellationToken);
            Assert.True(replay.Status == TableOperationStatus.Replayed ||
                        replay.Status == TableOperationStatus.Accepted,
                replay.Status + "|" + replay.ReasonCode);
            Assert.Equal(original.CommandVersion, replay.CommandVersion);
            Assert.Equal(2, GetCheat(actor)!.TileMarks.Groups.Count);
        }

        [Fact]
        public async Task cross_boot_offer_survives_checkpoint_and_second_recovery_but_cannot_be_selected()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            SleeveBloomOffer offer = await OpenDurableOffTurnOfferAsync(harness);
            var clock = new BootShiftClock
            {
                MonotonicMillis = 10L,
                UtcNowMs = harness.Clock.UtcNowMs + 10000L
            };
            await using TableActor actor = AttachDurable(harness, clock, "boot-2");
            Assert.Equal(TableRecoveryStatus.Recovered, (await actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Configured, (await actor.SaveCheckpointAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableRecoveryStatus.Recovered, (await actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken)).Status);

            TableSnapshot snapshot = await actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            TableOperationResult choice = await actor.SubmitCheatAsync(new TableCheatCommandEnvelope(
                new RoundCheatCommand(
                    "round-1", 0, RoundCheatAction.SubmitSleeveBloomOffTurnChoice, "reboot-second",
                    RoundCheatPayload.OffTurnChoice(offer.OfferId, SleeveBloomKeep.Second)),
                new RequestId("reboot-second"), new StateVersion(2), snapshot.OwnerEpoch,
                new ControlEpoch(0), null, 2L, harness.Scope.ScopeId, "CHEAT",
                TablePayloadSha256.Compute(new byte[] { 9, 8, 7 })),
                TestContext.Current.CancellationToken);
            Assert.NotEqual(TableOperationStatus.Accepted, choice.Status);
            Assert.Equal(TableRuntimeReasonCode.CheatBootReconciliationRequired, choice.ReasonCode);
        }

        [Fact]
        public async Task cross_boot_offer_is_forced_first_by_authority_tick()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            SleeveBloomOffer offer = await OpenDurableOffTurnOfferAsync(harness);
            var clock = new BootShiftClock
            {
                MonotonicMillis = 10L,
                UtcNowMs = harness.Clock.UtcNowMs + 10000L
            };
            await using TableActor actor = AttachDurable(harness, clock, "boot-2");
            Assert.Equal(TableRecoveryStatus.Recovered, (await actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken)).Status);
            await actor.TickAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Null(GetCheat(actor)!.SleeveBloom.OffTurnOfferFor(0));
            Assert.Equal(offer.FirstTile, GetCheat(actor)!.SleeveBloom.ReservationFor(0)!.Tile);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task cross_boot_tick_commit_replays_with_and_without_new_boot_checkpoint(
            bool checkpointInNewBoot)
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            SleeveBloomOffer offer = await OpenDurableOffTurnOfferAsync(harness);
            var clock = new BootShiftClock
            {
                MonotonicMillis = 10L,
                UtcNowMs = harness.Clock.UtcNowMs + 10000L
            };
            await using TableActor actor = AttachDurable(harness, clock, "boot-2");
            Assert.Equal(TableRecoveryStatus.Recovered, (await actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken)).Status);
            if (checkpointInNewBoot)
            {
                Assert.Equal(TableOperationStatus.Configured, (await actor.SaveCheckpointAsync(
                    TestContext.Current.CancellationToken)).Status);
            }

            await actor.TickAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Null(GetCheat(actor)!.SleeveBloom.OffTurnOfferFor(0));
            Assert.Equal(offer.FirstTile, GetCheat(actor)!.SleeveBloom.ReservationFor(0)!.Tile);
            ulong fingerprint = GetCheat(actor)!.Fingerprint;
            Assert.Equal(TableRecoveryStatus.Recovered, (await actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(fingerprint, GetCheat(actor)!.Fingerprint);
        }

        [Fact]
        public async Task new_boot_offer_journal_preserves_its_creation_boot()
        {
            await using DurableHarness harness = await CreateDurableCheatHarnessAsync();
            var clock = new BootShiftClock
            {
                MonotonicMillis = 10L,
                UtcNowMs = harness.Clock.UtcNowMs + 10000L
            };
            await using TableActor actor = AttachDurable(harness, clock, "boot-2");
            Assert.Equal(TableRecoveryStatus.Recovered, (await actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken)).Status);
            TableSnapshot snapshot = await actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(
                harness.InitialRound, 1);
            Assert.Equal(TableOperationStatus.Accepted, (await actor.SubmitAsync(
                DurableRound(
                    harness.Scope, chain[0].Command, 0, "newboot-discard",
                    recoveryGeneration: 1L,
                    ownerEpoch: snapshot.OwnerEpoch.Value),
                TestContext.Current.CancellationToken)).Status);

            var command = new TableCheatCommandEnvelope(
                new RoundCheatCommand(
                    "round-1", 0, RoundCheatAction.SubmitSleeveBloomOffTurn, "newboot-offer",
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0")),
                new RequestId("newboot-offer"), new StateVersion(1), snapshot.OwnerEpoch,
                new ControlEpoch(0), null, 1L, harness.Scope.ScopeId, "CHEAT",
                TablePayloadSha256.Compute(new byte[] { 9, 8, 7 }));
            Assert.Equal(TableOperationStatus.Accepted, (await actor.SubmitCheatAsync(
                command, TestContext.Current.CancellationToken)).Status);
            Assert.Equal("boot-2", GetCheat(actor)!.SleeveBloom.OffTurnOfferFor(0)!.BootId);
            ulong fingerprint = GetCheat(actor)!.Fingerprint;
            Assert.Equal(TableRecoveryStatus.Recovered, (await actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(fingerprint, GetCheat(actor)!.Fingerprint);
            Assert.Equal("boot-2", GetCheat(actor)!.SleeveBloom.OffTurnOfferFor(0)!.BootId);
        }

        private static async Task<SleeveBloomOffer> OpenDurableOffTurnOfferAsync(DurableHarness harness)
        {
            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 1);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitAsync(
                DurableRound(harness.Scope, chain[0].Command, 0, "boot-discard"),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await harness.Actor.SubmitCheatAsync(
                DurableCheat("round-1", 0, RoundCheatAction.SubmitSleeveBloomOffTurn, "boot-offer",
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"), 1, harness.Scope),
                TestContext.Current.CancellationToken)).Status);
            SleeveBloomOffer offer = GetCheat(harness.Actor)!.SleeveBloom.OffTurnOfferFor(0)!;
            await harness.SaveCheckpointAsync();
            return offer;
        }

        private static TableActor AttachDurable(
            DurableHarness harness,
            ITableClock clock,
            string boot,
            int capacity = 8192)
        {
            var durable = new TableDurableContext(
                harness.Store, harness.Store, harness.Store, harness.Store, harness.Scopes,
                DurableHarness.Owner, TimeSpan.FromMinutes(10), boot, 1, DurableHarness.Room);
            durable.RecoveryStateSink = new RecordingRecoveryStateSink();
            var options = new TableActorOptions
            {
                RoomId = DurableHarness.Room,
                MailboxCapacity = 16,
                ControlReserve = 16,
                CheatRequestBindingCapacity = capacity,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = V32Fixtures.WithRegisteredRound(
                    V32Fixtures.Cheat(InitialPoints), "round-1")
            };
            return new TableActor(
                options, harness.InitialRound, new RecordingCommitPort(), clock,
                null, null, harness.ProjectionSource, null, durable);
        }

        private sealed class BootShiftClock : ITableClock
        {
            public long MonotonicMillis { get; set; }

            public long UtcNowMs { get; set; }
        }

        [Fact]
        public async Task ExhaustiveWallPassExternalEntryDoesNotRequireReservationDelivery()
        {
            var spec = new RoundSpec(
                "exhausted-external",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            RoundStateMachine round = RoundStateMachine.CreateDealt(
                spec,
                TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("exhausted-external"), 0));
            while (round.Tiles.WallRemaining > 0)
            {
                RoundTransition discard = round.Execute(
                    RoundCommand.Discard(
                        round.RoundId,
                        round.CurrentSeat,
                        round.Tiles.DrawnTile!.Value.Tile));
                Assert.True(discard.IsAccepted);
                round = discard.State!;
            }

            int responseSeat = (round.CurrentSeat + 1) % 4;
            RoundTransition opening = round.Execute(
                RoundCommand.Discard(
                    round.RoundId,
                    round.CurrentSeat,
                    round.Tiles.DrawnTile!.Value.Tile,
                    new[] { responseSeat }));
            Assert.True(opening.IsAccepted);
            round = opening.State!;
            Assert.True(round.Window != null);

            CheatState cheat = V32Fixtures.WithRegisteredRound(
                V32Fixtures.Cheat(InitialPoints),
                round.RoundId);
            TileId reserved = round.Tiles.TilesInZone(TileZone.Hand, responseSeat)[0];
            cheat = cheat.WithSleeveBloom(cheat.SleeveBloom.WithReservation(
                new SleeveBloomReservation(responseSeat, "exhausted-reservation", reserved, "window", 0)));
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.LocalHuman,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                CheatRequestBindingCapacity = 8192,
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(options, round, commitPort, clock, sink);

            TableOperationResult pass = await actor.SubmitAsync(
                new TableCommandEnvelope(
                    RoundCommand.Pass(round.RoundId, responseSeat),
                    new RequestId("exhausted-pass"),
                    new StateVersion(0),
                    new OwnerEpoch(1),
                    new ControlEpoch(0)),
                TestContext.Current.CancellationToken);

            Assert.True(
                pass.Status == TableOperationStatus.Accepted,
                pass.ReasonCode + ": " + pass.EvaluatedStateVersion);
            Assert.Contains(pass.Events, e => e.Type == RoundEventType.WallExhausted);
        }

        [Fact]
        public void cheat_checkpoint_roundtrips_octopus_third_and_reads_legacy_as_player()
        {
            CheatState octopus = V32Fixtures.Cheat(InitialPoints)
                .WithPlayerCharacter(PlayerCharacterIds.Octopus)
                .WithSleeveBloom(new SleeveBloomState().WithImmediateOffer(new SleeveBloomOffer(
                    "octopus-offer", 0, Mahjong.Domain.Cheat.SleeveBloomDrawKind.Normal, new TileId(0), new TileId(1), 1,
                    SleeveBloomOfferMode.Immediate, thirdTile: new TileId(2))));
            byte[] current = TableCheatLedgerCheckpointCodec.Encode(TableCheatLedgerCheckpoint.FromCheatState(octopus));
            TableCheatLedgerCheckpoint decodedCurrent = TableCheatLedgerCheckpointCodec.Decode(current);
            Assert.Equal(TableCheatLedgerCheckpointCodec.FormatVersion, decodedCurrent.FormatSchemaVersion);
            CheatState recovered = decodedCurrent.RestoreCheatState();
            Assert.Equal(PlayerCharacterIds.Octopus, recovered.PlayerCharacterId);
            Assert.Equal(PlayerCharacterIds.CurrentTraitsVersion, recovered.CharacterTraitsVersion);
            Assert.Equal(new TileId(2), recovered.SleeveBloom.ImmediateOffer!.ThirdTile);
            Assert.Equal(octopus.Fingerprint, recovered.Fingerprint);

            CheatState legacyDefault = V32Fixtures.Cheat(InitialPoints);
            byte[] legacy4 = TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                TableCheatLedgerCheckpoint.FromCheatState(legacyDefault), 4);
            CheatState oldSave4 = TableCheatLedgerCheckpointCodec.Decode(legacy4).RestoreCheatState();
            Assert.Equal(PlayerCharacterIds.Player, oldSave4.PlayerCharacterId);
            Assert.Equal(PlayerCharacterIds.CurrentTraitsVersion, oldSave4.CharacterTraitsVersion);
            Assert.Equal(legacyDefault.Fingerprint, oldSave4.Fingerprint);

            byte[] legacy5 = TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                TableCheatLedgerCheckpoint.FromCheatState(octopus), 5);
            CheatState oldSave5 = TableCheatLedgerCheckpointCodec.Decode(legacy5).RestoreCheatState();
            Assert.Equal(PlayerCharacterIds.Octopus, oldSave5.CharacterIdOf(0));
            Assert.Equal(PlayerCharacterIds.Player, oldSave5.CharacterIdOf(1));
            Assert.Equal(octopus.Fingerprint, oldSave5.Fingerprint);

            byte[] legacy6 = TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                TableCheatLedgerCheckpoint.FromCheatState(octopus), 6);
            CheatState oldSave6 = TableCheatLedgerCheckpointCodec.Decode(legacy6).RestoreCheatState();
            Assert.Equal(PlayerCharacterIds.Octopus, oldSave6.CharacterIdOf(0));
            Assert.Equal(PlayerCharacterIds.Player, oldSave6.CharacterIdOf(1));
            Assert.Equal(octopus.Fingerprint, oldSave6.Fingerprint);
        }

        [Fact]
        public async Task octopus_immediate_sleeve_bloom_third_emits_matching_authoritative_result_tile()
        {
            var codec = new StrictWireStreamCodec();
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = new RecordingStreamTerminationSink()
            };
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var publicationSink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithPlayerCharacter(PlayerCharacterIds.Octopus);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(
                options,
                round,
                commitPort,
                clock,
                publicationSink,
                null,
                source,
                streamOptions);

            var owner = new RecordingStreamSink();
            var other = new RecordingStreamSink();
            var spectator = new RecordingStreamSink();
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.ForSeat(0), "owner", owner),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.ForSeat(1), "other", other),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.Spectator, "spectator", spectator),
                TestContext.Current.CancellationToken)).Status);
            await owner.WaitForCountAsync(1);
            await other.WaitForCountAsync(1);
            await spectator.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 4);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(TableOperationStatus.Accepted,
                    (await SubmitActorRoundAsync(actor, chain[i], i, "d" + i)).Status);
            }

            RoundStateMachine seat0DrawRound = GetRound(actor);
            Assert.Equal(0, seat0DrawRound.CurrentSeat);
            Assert.Equal(RoundPhase.CheatWindow, seat0DrawRound.Phase);
            int turnNumber = CountActorDrawTurns(seat0DrawRound);
            string hitRequest = FindOctopusRequest(seat0DrawRound.RoundId, 0, turnNumber, hit: true);

            TableOperationResult activation = await actor.SubmitCheatAsync(
                Cheat(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloom, hitRequest, RoundCheatPayload.SleeveBloom(), 4, hitRequest),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activation.Status);

            SleeveBloomOffer offer = GetCheat(actor)!.SleeveBloom.ImmediateOffer!;
            Assert.True(offer.ThirdTile.HasValue);

            TableCheatCommandEnvelope thirdEnvelope = Cheat(
                round.RoundId,
                0,
                RoundCheatAction.SubmitSleeveBloomChoice,
                "sb-choice-third",
                RoundCheatPayload.SleeveBloomChoice(offer.OfferId, SleeveBloomKeep.Third),
                5,
                "sb-choice-third");
            TableOperationResult choice = await actor.SubmitCheatAsync(
                thirdEnvelope,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, choice.Status);

            TableEventRecord resolvedRecord = Assert.Single(
                choice.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            Assert.Equal(SleeveBloomKeep.Third, resolvedRecord.Cheat!.Keep);
            Assert.Equal(offer.OfferId, resolvedRecord.Cheat.OfferId);
            Assert.Equal(offer.ThirdTile!.Value, resolvedRecord.Cheat.ResultTile);
            Assert.Equal(offer.ThirdTile.Value, GetRound(actor).Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(TileZone.Hand, GetRound(actor).Tiles.PositionOf(offer.ThirdTile.Value).Zone);
            Assert.True(TilePositionMap.Validate(GetRound(actor).Tiles).IsValid);
            Assert.Equal(990, GetCheat(actor)!.Ledger.BalanceOf(0));

            // Idempotent replay returns exact receipt without re-charging.
            TableOperationResult replay = await actor.SubmitCheatAsync(
                thirdEnvelope,
                TestContext.Current.CancellationToken);
            Assert.Same(choice, replay);
            Assert.Equal(990, GetCheat(actor)!.Ledger.BalanceOf(0));

            await owner.WaitForCountAsync(7);
            await other.WaitForCountAsync(7);
            await spectator.WaitForCountAsync(7);

            var ownerEvents = new List<EventEnvelope>();
            for (int i = 0; i < owner.Frames.Count; i++)
            {
                ownerEvents.AddRange(StrictWireStreamCodec.DecodeAndStrict(
                    owner.Frames[i].EncodedPayload!).PlayerUpdate.VisibleEvents);
            }

            SleeveBloomResolved resolvedWire = ownerEvents
                .Single(e => e.EventCase == EventEnvelope.EventOneofCase.SleeveBloomResolved)
                .SleeveBloomResolved;
            Assert.Equal(offer.OfferId, resolvedWire.OfferId);
            Assert.False(resolvedWire.KeptFirst);
            Assert.True(resolvedWire.HasResultTileId);
            Assert.Equal((uint)offer.ThirdTile.Value.Value, resolvedWire.ResultTileId);

            string spectatorWire = System.Text.Encoding.UTF8.GetString(
                spectator.Frames[spectator.Frames.Count - 1].EncodedPayload!);
            string otherWire = System.Text.Encoding.UTF8.GetString(
                other.Frames[other.Frames.Count - 1].EncodedPayload!);
            Assert.DoesNotContain(offer.OfferId, spectatorWire);
            Assert.DoesNotContain(offer.OfferId, otherWire);

            // Ordinary character offer must still reject Third without state change.
            await using ActorHarness plain = CreateHarness();
            IReadOnlyList<TableCommandEnvelope> plainChain = P31Fixtures.BuildDiscardChain(plain.InitialRound, 4);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(TableOperationStatus.Accepted,
                    (await SubmitRoundAsync(plain, plainChain[i], i, "pd" + i)).Status);
            }

            RoundStateMachine plainDraw = GetRound(plain.Actor);
            int plainTurn = CountActorDrawTurns(plainDraw);
            string plainHit = FindOctopusRequest(plainDraw.RoundId, 0, plainTurn, hit: true);
            TableOperationResult plainActivation = await plain.Actor.SubmitCheatAsync(
                Cheat(plainDraw.RoundId, 0, RoundCheatAction.SubmitSleeveBloom, plainHit, RoundCheatPayload.SleeveBloom(), 4, plainHit),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, plainActivation.Status);
            SleeveBloomOffer plainOffer = GetCheat(plain.Actor)!.SleeveBloom.ImmediateOffer!;
            Assert.Null(plainOffer.ThirdTile);

            TableOperationResult plainThird = await plain.Actor.SubmitCheatAsync(
                Cheat(plainDraw.RoundId, 0, RoundCheatAction.SubmitSleeveBloomChoice, "plain-third",
                    RoundCheatPayload.SleeveBloomChoice(plainOffer.OfferId, SleeveBloomKeep.Third), 5, "plain-third"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, plainThird.Status);
            Assert.Equal(
                TableRuntimeReasonCode.FromCheat(RoundCheatErrorCode.CheatRejected, CheatErrorCode.InvalidSelection),
                plainThird.ReasonCode);
            Assert.NotNull(GetCheat(plain.Actor)!.SleeveBloom.ImmediateOffer);
            Assert.Equal(5UL, (await plain.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task octopus_off_turn_sleeve_bloom_third_emits_matching_result_tile_and_delivers_third()
        {
            var codec = new StrictWireStreamCodec();
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = new RecordingStreamTerminationSink()
            };
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var publicationSink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithPlayerCharacter(PlayerCharacterIds.Octopus);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            await using var actor = new TableActor(
                options,
                round,
                commitPort,
                clock,
                publicationSink,
                null,
                source,
                streamOptions);

            var owner = new RecordingStreamSink();
            var other = new RecordingStreamSink();
            var spectator = new RecordingStreamSink();
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.ForSeat(0), "owner", owner),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.ForSeat(1), "other", other),
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, (await actor.SubscribeStreamAsync(
                Subscribe(round, TableStreamAudience.Spectator, "spectator", spectator),
                TestContext.Current.CancellationToken)).Status);
            await owner.WaitForCountAsync(1);
            await other.WaitForCountAsync(1);
            await spectator.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(round, 4);
            Assert.Equal(TableOperationStatus.Accepted,
                (await SubmitActorRoundAsync(actor, chain[0], 0, "d0")).Status);

            RoundStateMachine afterDiscard = GetRound(actor);
            int offTurnNumber = CountActorDrawTurns(afterDiscard);
            string hitRequest = FindOctopusRequest(afterDiscard.RoundId, 0, offTurnNumber, hit: true);

            TableOperationResult activation = await actor.SubmitCheatAsync(
                Cheat(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurn, hitRequest,
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"), 1, hitRequest),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activation.Status);
            SleeveBloomOffer offer = GetCheat(actor)!.SleeveBloom.OffTurnOfferFor(0)!;
            Assert.True(offer.ThirdTile.HasValue);

            TableOperationResult choice = await actor.SubmitCheatAsync(
                Cheat(round.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurnChoice, "off-choice-third",
                    RoundCheatPayload.OffTurnChoice(offer.OfferId, SleeveBloomKeep.Third), 2, "off-choice-third"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, choice.Status);

            TableEventRecord resolvedRecord = Assert.Single(
                choice.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            Assert.Equal(SleeveBloomKeep.Third, resolvedRecord.Cheat!.Keep);
            Assert.Equal(offer.OfferId, resolvedRecord.Cheat.OfferId);
            Assert.Equal(offer.ThirdTile!.Value, resolvedRecord.Cheat.ResultTile);
            Assert.Equal(offer.ThirdTile.Value, GetCheat(actor)!.SleeveBloom.ReservationFor(0)!.Tile);
            Assert.Equal(990, GetCheat(actor)!.Ledger.BalanceOf(0));

            await owner.WaitForCountAsync(4);
            await other.WaitForCountAsync(4);
            await spectator.WaitForCountAsync(4);

            var ownerEvents = new List<EventEnvelope>();
            for (int i = 0; i < owner.Frames.Count; i++)
            {
                ownerEvents.AddRange(StrictWireStreamCodec.DecodeAndStrict(
                    owner.Frames[i].EncodedPayload!).PlayerUpdate.VisibleEvents);
            }

            SleeveBloomResolved resolvedWire = ownerEvents
                .Single(e => e.EventCase == EventEnvelope.EventOneofCase.SleeveBloomResolved)
                .SleeveBloomResolved;
            Assert.Equal(offer.OfferId, resolvedWire.OfferId);
            Assert.False(resolvedWire.KeptFirst);
            Assert.True(resolvedWire.HasResultTileId);
            Assert.Equal((uint)offer.ThirdTile.Value.Value, resolvedWire.ResultTileId);

            string spectatorWire = System.Text.Encoding.UTF8.GetString(
                spectator.Frames[spectator.Frames.Count - 1].EncodedPayload!);
            string otherWire = System.Text.Encoding.UTF8.GetString(
                other.Frames[other.Frames.Count - 1].EncodedPayload!);
            Assert.DoesNotContain(offer.OfferId, spectatorWire);
            Assert.DoesNotContain(offer.OfferId, otherWire);

            // Deliver reserved third candidate on seat 0's next normal draw without a second charge.
            Assert.Equal(TableOperationStatus.Accepted, (await SubmitActorRoundAsync(actor, chain[1], 3, "d1")).Status);
            Assert.Equal(TableOperationStatus.Accepted, (await SubmitActorRoundAsync(actor, chain[2], 4, "d2")).Status);
            TableOperationResult delivery = await SubmitActorRoundAsync(actor, chain[3], 5, "d3");
            Assert.Equal(TableOperationStatus.Accepted, delivery.Status);

            TableEventRecord drawn = Assert.Single(delivery.Events, e => e.Type == RoundEventType.TileDrawn);
            Assert.Equal(0, drawn.Seat);
            Assert.Equal(offer.ThirdTile.Value, drawn.Tile);
            Assert.Equal(offer.ThirdTile.Value, GetRound(actor).Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(990, GetCheat(actor)!.Ledger.BalanceOf(0));
            Assert.True(TilePositionMap.Validate(GetRound(actor).Tiles).IsValid);

            // Off-turn timeout still falls back to FirstTile even for Octopus offer.
            RoundStateMachine timeoutRound = P31Fixtures.Dealt("round-timeout");
            CheatState timeoutCheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), timeoutRound.RoundId)
                .WithPlayerCharacter(PlayerCharacterIds.Octopus);
            var timeoutClock = new ManualTableClock { Now = 1000L };
            var timeoutCommit = new RecordingCommitPort();
            var timeoutSink = new RecordingPublicationSink(timeoutCommit);
            await using var timeoutActor = new TableActor(
                new TableActorOptions
                {
                    RoomId = "room-timeout",
                    MailboxCapacity = 16,
                    ControlReserve = 16,
                    DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                    InitialControllers = P31Fixtures.Controllers(
                        SeatControllerKind.Computer,
                        SeatControllerKind.Computer,
                        SeatControllerKind.Computer,
                        SeatControllerKind.Computer),
                    CheatEconomy = V32Fixtures.Economy(InitialPoints),
                    SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                    InitialCheatState = timeoutCheat
                },
                timeoutRound,
                timeoutCommit,
                timeoutClock,
                timeoutSink);

            IReadOnlyList<TableCommandEnvelope> timeoutChain = P31Fixtures.BuildDiscardChain(timeoutRound, 1);
            Assert.Equal(TableOperationStatus.Accepted,
                (await SubmitActorRoundAsync(timeoutActor, timeoutChain[0], 0, "td0")).Status);
            RoundStateMachine afterTimeoutDiscard = GetRound(timeoutActor);
            string timeoutHit = FindOctopusRequest(afterTimeoutDiscard.RoundId, 0, CountActorDrawTurns(afterTimeoutDiscard), hit: true);
            Assert.Equal(TableOperationStatus.Accepted, (await timeoutActor.SubmitCheatAsync(
                Cheat(timeoutRound.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurn, timeoutHit,
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"), 1, timeoutHit),
                TestContext.Current.CancellationToken)).Status);
            SleeveBloomOffer timeoutOffer = GetCheat(timeoutActor)!.SleeveBloom.OffTurnOfferFor(0)!;
            Assert.True(timeoutOffer.ThirdTile.HasValue);

            timeoutClock.Now = 8000L;
            TableOperationResult expired = await timeoutActor.SubmitCheatAsync(
                Cheat(timeoutRound.RoundId, 0, RoundCheatAction.SubmitSleeveBloomOffTurnChoice, "off-late-third",
                    RoundCheatPayload.OffTurnChoice(timeoutOffer.OfferId, SleeveBloomKeep.Third), 2, "off-late-third"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, expired.Status);
            Assert.Equal(TableRuntimeReasonCode.DeadlineExpired, expired.ReasonCode);
            TableEventRecord forced = Assert.Single(
                expired.Events,
                e => e.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            Assert.Equal(SleeveBloomKeep.First, forced.Cheat!.Keep);
            Assert.Equal(timeoutOffer.FirstTile, forced.Cheat.ResultTile);
            Assert.Equal(timeoutOffer.FirstTile, GetCheat(timeoutActor)!.SleeveBloom.ReservationFor(0)!.Tile);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task third_choice_cold_recovery_supports_current_and_legacy_first_for_both_modes(
            bool offTurn,
            bool legacyResult)
        {
            var ct = TestContext.Current.CancellationToken;
            var codec = new StrictWireStreamCodec();
            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = new RecordingStreamTerminationSink()
            };
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
                },
                streamOptions: streamOptions);

            int version = offTurn ? 1 : 4;
            var chain = P31Fixtures.BuildDiscardChain(round, 4);
            for (int i = 0; i < version; i++)
            {
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(h.Scope, chain[i].Command, i, "rec-r-" + i))).Status);
            }

            string request = FindOctopusRequest(round.RoundId, 0, CountActorDrawTurns(GetRound(h.Actor)), true);
            TableOperationResult activated = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    offTurn ? RoundCheatAction.SubmitSleeveBloomOffTurn : RoundCheatAction.SubmitSleeveBloom,
                    request,
                    offTurn ? RoundCheatPayload.OffTurn("bloom-window-0-0-0") : RoundCheatPayload.SleeveBloom(),
                    version,
                    h.Scope),
                ct);
            Assert.True(activated.Status == TableOperationStatus.Accepted, activated.ReasonCode + " " + activated.Detail);

            SleeveBloomState bloom = GetCheat(h.Actor)!.SleeveBloom;
            SleeveBloomOffer offer = (offTurn ? bloom.OffTurnOfferFor(0) : bloom.ImmediateOffer)!;
            Assert.True(offer.ThirdTile.HasValue);

            await h.SaveCheckpointAsync();

            TableOperationResult chosen = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    offTurn ? RoundCheatAction.SubmitSleeveBloomOffTurnChoice : RoundCheatAction.SubmitSleeveBloomChoice,
                    "third",
                    offTurn
                        ? RoundCheatPayload.OffTurnChoice(offer.OfferId, SleeveBloomKeep.Third)
                        : RoundCheatPayload.SleeveBloomChoice(offer.OfferId, SleeveBloomKeep.Third),
                    version + 1,
                    h.Scope),
                ct);
            Assert.True(chosen.Status == TableOperationStatus.Accepted, chosen.ReasonCode + " " + chosen.Detail);

            TableEventRecord resolved = Assert.Single(
                chosen.Events,
                row => row.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            TableCheatEventRecord c = resolved.Cheat!;
            Assert.Equal(offer.ThirdTile, c.ResultTile);
            Assert.NotEqual(offer.FirstTile, c.ResultTile);

            if (legacyResult)
            {
                RewriteStoredEvent(
                    h.Store,
                    resolved.Seq,
                    row => new TableEventRecord(
                        row.Seq,
                        row.CommandVersion,
                        row.Ordinal,
                        row.Type,
                        row.Seat,
                        row.Tile,
                        row.Detail,
                        row.EventSchema,
                        row.Payload,
                        new TableCheatEventRecord(
                            c.Event,
                            c.OfferId,
                            c.Keep,
                            c.Action,
                            offer.FirstTile,
                            c.HandTile,
                            c.TargetSlot,
                            c.TurnNumber)));
            }

            TableRecoveryResult result = await h.Actor.RecoverFromStoreAsync(ct);
            Assert.True(
                result.Recovered,
                "offTurn=" + offTurn + " legacy=" + legacyResult + " " + result.ReasonCode + " " + result.Detail);
            Assert.Equal((ulong)(version + 2), result.Report!.FinalStateVersion.Value);
            Assert.Equal(990, GetCheat(h.Actor)!.Ledger.BalanceOf(0));

            if (offTurn)
            {
                Assert.Equal(offer.ThirdTile.Value, GetCheat(h.Actor)!.SleeveBloom.ReservationFor(0)!.Tile);
            }
            else
            {
                Assert.Equal(offer.ThirdTile.Value, GetRound(h.Actor).Tiles.DrawnTile!.Value.Tile);
                Assert.Equal(TileZone.Hand, GetRound(h.Actor).Tiles.PositionOf(offer.ThirdTile.Value).Zone);
            }

            Assert.True(TilePositionMap.Validate(GetRound(h.Actor).Tiles).IsValid);

            // Normalized committed log ensures subsequent checkpoint history/facts carry the true ThirdTile.
            TableCheckpoint nextCheckpoint = await h.SaveCheckpointAsync();
            IReadOnlyList<TableEventRecord> savedHistory = TableCommittedHistoryCodec.Decode(
                nextCheckpoint.CommittedHistoryPayload!);
            TableEventRecord savedResolved = Assert.Single(
                savedHistory,
                row => row.Seq.Equals(resolved.Seq));
            Assert.Equal(offer.ThirdTile, savedResolved.Cheat!.ResultTile);
            TableProjectionContextData savedFacts = TableProjectionFactsCodec.Decode(
                nextCheckpoint.CommittedFactsPayload!,
                round.RoundId,
                nextCheckpoint.StateVersion);
            TableProjectionEventFact savedFact = Assert.Single(
                savedFacts.EventFacts,
                fact => fact.EventSeq.Equals(resolved.Seq));
            Assert.Equal((uint)offer.ThirdTile.Value.Value, savedFact.Facts.ResultTileId);

            TableRecoveryResult secondRecovery = await h.Actor.RecoverFromStoreAsync(ct);
            Assert.True(secondRecovery.Recovered, secondRecovery.ReasonCode + " " + secondRecovery.Detail);
        }

        [Theory]
        [InlineData(false, "second-tile")]
        [InlineData(true, "second-tile")]
        [InlineData(false, "arbitrary-tile")]
        [InlineData(true, "arbitrary-tile")]
        [InlineData(false, "null-tile")]
        [InlineData(true, "null-tile")]
        [InlineData(false, "wrong-schema")]
        [InlineData(true, "wrong-schema")]
        [InlineData(false, "offer-mismatch")]
        [InlineData(true, "offer-mismatch")]
        [InlineData(false, "owner-mismatch")]
        [InlineData(true, "owner-mismatch")]
        [InlineData(false, "keep-first-with-third-tile")]
        [InlineData(true, "keep-first-with-third-tile")]
        [InlineData(false, "keep-second-with-first-tile")]
        [InlineData(true, "keep-second-with-first-tile")]
        [InlineData(true, "expiry-sweep-with-third-tile")]
        [InlineData(false, "beam-swap-tampered")]
        public async Task third_choice_cold_recovery_rejects_invalid_result_tiles_and_non_legacy_records(
            bool offTurn,
            string mode)
        {
            var ct = TestContext.Current.CancellationToken;
            var codec = new StrictWireStreamCodec();
            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = new RecordingStreamTerminationSink()
            };
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
                },
                streamOptions: streamOptions);

            int version = offTurn ? 1 : 4;
            var chain = P31Fixtures.BuildDiscardChain(round, 4);
            for (int i = 0; i < version; i++)
            {
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(h.Scope, chain[i].Command, i, "neg-r-" + i))).Status);
            }

            if (mode == "beam-swap-tampered")
            {
                TableOperationResult beamActivated = await h.Actor.SubmitCheatAsync(
                    DurableCheat(round.RoundId, 0, RoundCheatAction.SubmitBeamSwap, "beam-1",
                        RoundCheatPayload.BeamSwap(), version, h.Scope),
                    ct);
                Assert.Equal(TableOperationStatus.Accepted, beamActivated.Status);
                BeamSwapOffer beamOffer = GetCheat(h.Actor)!.BeamSwap.Offer!;
                await h.SaveCheckpointAsync();

                TableOperationResult beamChosen = await h.Actor.SubmitCheatAsync(
                    DurableCheat(round.RoundId, 0, RoundCheatAction.SubmitBeamSwapChoice, "beam-c",
                        RoundCheatPayload.BeamSwapChoice(beamOffer.OfferId, BeamSwapAction.KeepOriginal, null, null),
                        version + 1, h.Scope),
                    ct);
                Assert.Equal(TableOperationStatus.Accepted, beamChosen.Status);
                TableEventRecord beamRow = Assert.Single(
                    beamChosen.Events,
                    row => row.Cheat?.Event.Kind == CheatEventKind.BeamSwapResolved);
                RewriteStoredEvent(
                    h.Store,
                    beamRow.Seq,
                    row => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(
                            row.Cheat!.Event, row.Cheat.OfferId, row.Cheat.Keep, row.Cheat.Action,
                            beamOffer.DrawnTileId, row.Cheat.HandTile, row.Cheat.TargetSlot, row.Cheat.TurnNumber)));

                TableRecoveryResult beamResult = await h.Actor.RecoverFromStoreAsync(ct);
                Assert.Equal(TableRecoveryStatus.Failed, beamResult.Status);
                Assert.Equal(TableRuntimeReasonCode.RecoveryReplayMismatch, beamResult.ReasonCode);
                return;
            }

            string request = FindOctopusRequest(round.RoundId, 0, CountActorDrawTurns(GetRound(h.Actor)), true);
            TableOperationResult activated = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    offTurn ? RoundCheatAction.SubmitSleeveBloomOffTurn : RoundCheatAction.SubmitSleeveBloom,
                    request,
                    offTurn ? RoundCheatPayload.OffTurn("bloom-window-0-0-0") : RoundCheatPayload.SleeveBloom(),
                    version,
                    h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);
            SleeveBloomState bloom = GetCheat(h.Actor)!.SleeveBloom;
            SleeveBloomOffer offer = (offTurn ? bloom.OffTurnOfferFor(0) : bloom.ImmediateOffer)!;
            Assert.True(offer.ThirdTile.HasValue);

            await h.SaveCheckpointAsync();

            TableEventRecord targetRow;
            if (mode == "expiry-sweep-with-third-tile")
            {
                h.Clock.Now = 8_000L;
                TableTickResult ticked = await h.Actor.TickAsync(cancellationToken: ct);
                Assert.Equal(TableOperationStatus.Accepted, ticked.Status);
                IReadOnlyList<TableEventRecord> afterEvents = await h.Store.LoadEventsAfterAsync(
                    DurableHarness.Room,
                    new EventSeq(0),
                    ct);
                targetRow = afterEvents.Last(row => row.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            }
            else
            {
                SleeveBloomKeep keepChoice = mode switch
                {
                    "keep-first-with-third-tile" => SleeveBloomKeep.First,
                    "keep-second-with-first-tile" => SleeveBloomKeep.Second,
                    _ => SleeveBloomKeep.Third
                };
                TableOperationResult chosen = await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        0,
                        offTurn ? RoundCheatAction.SubmitSleeveBloomOffTurnChoice : RoundCheatAction.SubmitSleeveBloomChoice,
                        "neg-choice-" + mode,
                        offTurn
                            ? RoundCheatPayload.OffTurnChoice(offer.OfferId, keepChoice)
                            : RoundCheatPayload.SleeveBloomChoice(offer.OfferId, keepChoice),
                        version + 1,
                        h.Scope),
                    ct);
                Assert.Equal(TableOperationStatus.Accepted, chosen.Status);
                targetRow = Assert.Single(
                    chosen.Events,
                    row => row.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            }

            TileId arbitraryOtherTile = Enumerable.Range(0, TileId.Count)
                .Select(value => new TileId(value))
                .First(tile =>
                    !tile.Equals(offer.FirstTile) &&
                    !tile.Equals(offer.SecondTile) &&
                    !tile.Equals(offer.ThirdTile.Value));

            RewriteStoredEvent(h.Store, targetRow.Seq, row =>
            {
                TableCheatEventRecord c = row.Cheat!;
                return mode switch
                {
                    "second-tile" => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(c.Event, c.OfferId, c.Keep, c.Action, offer.SecondTile, c.HandTile, c.TargetSlot, c.TurnNumber)),
                    "arbitrary-tile" => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(c.Event, c.OfferId, c.Keep, c.Action, arbitraryOtherTile, c.HandTile, c.TargetSlot, c.TurnNumber)),
                    "null-tile" => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(c.Event, c.OfferId, c.Keep, c.Action, null, c.HandTile, c.TargetSlot, c.TurnNumber)),
                    "wrong-schema" => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        2, row.Payload,
                        new TableCheatEventRecord(c.Event, c.OfferId, c.Keep, c.Action, offer.FirstTile, c.HandTile, c.TargetSlot, c.TurnNumber)),
                    "offer-mismatch" => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(c.Event, "wrong-offer", c.Keep, c.Action, offer.FirstTile, c.HandTile, c.TargetSlot, c.TurnNumber)),
                    "owner-mismatch" => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, 1, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(
                            CheatEvent.Restore(
                                c.Event.Kind,
                                1,
                                c.Event.EntryId,
                                c.Event.Amount,
                                c.Event.NewBalance,
                                c.Event.OfferId,
                                c.Event.GroupId,
                                c.Event.Keep,
                                c.Event.Action,
                                c.Event.Reason),
                            c.OfferId, c.Keep, c.Action, offer.FirstTile, c.HandTile, c.TargetSlot, c.TurnNumber)),
                    "keep-first-with-third-tile" => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(c.Event, c.OfferId, c.Keep, c.Action, offer.ThirdTile, c.HandTile, c.TargetSlot, c.TurnNumber)),
                    "keep-second-with-first-tile" => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(c.Event, c.OfferId, c.Keep, c.Action, offer.FirstTile, c.HandTile, c.TargetSlot, c.TurnNumber)),
                    "expiry-sweep-with-third-tile" => new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(c.Event, c.OfferId, c.Keep, c.Action, offer.ThirdTile, c.HandTile, c.TargetSlot, c.TurnNumber)),
                    _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
                };
            });

            TableRecoveryResult result = await h.Actor.RecoverFromStoreAsync(ct);
            Assert.Equal(TableRecoveryStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryReplayMismatch, result.ReasonCode);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task legacy_checkpoint_history_with_third_first_result_recovers_with_physical_third_and_projects_cleanly(
            bool offTurn)
        {
            var ct = TestContext.Current.CancellationToken;
            var codec = new StrictWireStreamCodec();
            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = new RecordingStreamTerminationSink()
            };
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
                },
                streamOptions: streamOptions);

            int version = offTurn ? 1 : 4;
            var chain = P31Fixtures.BuildDiscardChain(round, 4);
            for (int i = 0; i < version; i++)
            {
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(h.Scope, chain[i].Command, i, "cp-r-" + i))).Status);
            }

            string request = FindOctopusRequest(round.RoundId, 0, CountActorDrawTurns(GetRound(h.Actor)), true);
            TableOperationResult activated = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    offTurn ? RoundCheatAction.SubmitSleeveBloomOffTurn : RoundCheatAction.SubmitSleeveBloom,
                    request,
                    offTurn ? RoundCheatPayload.OffTurn("bloom-window-0-0-0") : RoundCheatPayload.SleeveBloom(),
                    version,
                    h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);
            SleeveBloomState bloom = GetCheat(h.Actor)!.SleeveBloom;
            SleeveBloomOffer offer = (offTurn ? bloom.OffTurnOfferFor(0) : bloom.ImmediateOffer)!;
            Assert.True(offer.ThirdTile.HasValue);

            TableOperationResult chosen = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    offTurn ? RoundCheatAction.SubmitSleeveBloomOffTurnChoice : RoundCheatAction.SubmitSleeveBloomChoice,
                    "cp-third",
                    offTurn
                        ? RoundCheatPayload.OffTurnChoice(offer.OfferId, SleeveBloomKeep.Third)
                        : RoundCheatPayload.SleeveBloomChoice(offer.OfferId, SleeveBloomKeep.Third),
                    version + 1,
                    h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, chosen.Status);
            TableEventRecord resolved = Assert.Single(
                chosen.Events,
                row => row.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);

            TableCheckpoint checkpoint = await h.SaveCheckpointAsync();
            TableEventRecord[] legacyHistory = TableCommittedHistoryCodec.Decode(checkpoint.CommittedHistoryPayload!)
                .Select(row =>
                {
                    if (!row.Seq.Equals(resolved.Seq))
                    {
                        return row;
                    }

                    TableCheatEventRecord c = row.Cheat!;
                    return new TableEventRecord(
                        row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                        row.EventSchema, row.Payload,
                        new TableCheatEventRecord(
                            c.Event, c.OfferId, c.Keep, c.Action, offer.FirstTile, c.HandTile, c.TargetSlot, c.TurnNumber));
                })
                .ToArray();
            byte[] legacyHistoryBytes = TableCommittedHistoryCodec.Encode(legacyHistory);

            TableProjectionContextData originalFacts = TableProjectionFactsCodec.Decode(
                checkpoint.CommittedFactsPayload!,
                round.RoundId,
                checkpoint.StateVersion);
            var legacyEventFacts = originalFacts.EventFacts
                .Select(fact => fact.EventSeq.Equals(resolved.Seq)
                    ? new TableProjectionEventFact(
                        fact.Type,
                        fact.EventSeq,
                        fact.CommandVersion,
                        fact.EventOrdinal,
                        new TableProjectionEventFacts(
                            turnNumber: fact.Facts.TurnNumber,
                            offerId: fact.Facts.OfferId,
                            keptFirst: fact.Facts.KeptFirst,
                            resultTileId: (uint)offer.FirstTile.Value))
                    : fact)
                .ToArray();
            byte[] legacyFactsBytes = TableProjectionFactsCodec.Encode(new TableProjectionContextData(
                originalFacts.RoundId,
                originalFacts.StateVersion,
                originalFacts.SessionVersion,
                originalFacts.Cheat,
                originalFacts.PublicFacts,
                originalFacts.PrivateFacts,
                originalFacts.Session,
                originalFacts.Policy,
                legacyEventFacts,
                originalFacts.ConcealedMeldFacts));

            byte[] legacyCheatPayload = TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                TableCheatLedgerCheckpointCodec.Decode(checkpoint.CheatPayload!), 5);

            var legacyCheckpoint = new TableCheckpoint(
                checkpoint.RoomId,
                checkpoint.StateVersion,
                checkpoint.LastEventSeq,
                checkpoint.StateFingerprint,
                checkpoint.RulesetVersion,
                checkpoint.StatePayload,
                checkpoint.StatePayloadSha256,
                checkpoint.ControlVersion,
                checkpoint.ControlPayload,
                checkpoint.ControlPayloadSha256,
                checkpoint.TimingPayload,
                checkpoint.TimingPayloadSha256,
                legacyCheatPayload,
                TablePayloadSha256.Compute(legacyCheatPayload),
                legacyFactsBytes,
                TablePayloadSha256.Compute(legacyFactsBytes),
                checkpoint.CreatedAtUtcMs,
                checkpoint.BootId,
                checkpoint.SchemaVersion,
                checkpoint.TimingVersion,
                checkpoint.CapturedMonotonicMillis,
                legacyHistoryBytes,
                TablePayloadSha256.Compute(legacyHistoryBytes),
                checkpoint.CommittedCommandBindingsPayload,
                checkpoint.CommittedCommandBindingsPayloadSha256);

            Assert.True(
                TableCheckpointValidator.TryValidate(legacyCheckpoint, out string reasonCode, out string detail),
                reasonCode + " " + detail);
            h.Store.ReplaceCheckpointForTest(DurableHarness.Room, legacyCheckpoint);

            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(ct);
            Assert.True(recovered.Recovered, recovered.ReasonCode + " " + recovered.Detail);

            if (offTurn)
            {
                Assert.Equal(offer.ThirdTile.Value, GetCheat(h.Actor)!.SleeveBloom.ReservationFor(0)!.Tile);
            }
            else
            {
                Assert.Equal(offer.ThirdTile.Value, GetRound(h.Actor).Tiles.DrawnTile!.Value.Tile);
                Assert.Equal(TileZone.Hand, GetRound(h.Actor).Tiles.PositionOf(offer.ThirdTile.Value).Zone);
            }

            TableCheckpoint resavedCheckpoint = await h.SaveCheckpointAsync();
            IReadOnlyList<TableEventRecord> resavedHistory = TableCommittedHistoryCodec.Decode(
                resavedCheckpoint.CommittedHistoryPayload!);
            TableEventRecord resavedResolved = Assert.Single(
                resavedHistory,
                row => row.Seq.Equals(resolved.Seq));
            Assert.Equal(offer.ThirdTile.Value, resavedResolved.Cheat!.ResultTile);
            TableProjectionContextData resavedFacts = TableProjectionFactsCodec.Decode(
                resavedCheckpoint.CommittedFactsPayload!,
                round.RoundId,
                resavedCheckpoint.StateVersion);
            TableProjectionEventFact resavedFact = Assert.Single(
                resavedFacts.EventFacts,
                fact => fact.EventSeq.Equals(resolved.Seq));
            Assert.Equal((uint)offer.ThirdTile.Value.Value, resavedFact.Facts.ResultTileId);

            TableRecoveryResult reloadedAfterResave = await h.Actor.RecoverFromStoreAsync(ct);
            Assert.True(reloadedAfterResave.Recovered, reloadedAfterResave.ReasonCode + " " + reloadedAfterResave.Detail);

            var ownerSink = new RecordingStreamSink();
            TableStreamControlResult subscribed = await h.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("owner-cp-rec"),
                        TableStreamAudience.ForSeat(0),
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    reloadedAfterResave.Report!.CurrentOwnerEpoch,
                    h.Actor.RecoveryGeneration,
                    ownerSink,
                    new RoundId(round.RoundId),
                    new ControlEpoch(0)),
                ct);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await ownerSink.WaitForCountAsync(1);
            PlayerUpdate playerUpdate = StrictWireStreamCodec.DecodeAndStrict(
                ownerSink.Frames[0].EncodedPayload!).PlayerUpdate;
            Assert.Equal(990L, playerUpdate.Snapshot.PrivateView.Cheat.CheatPoints);
            if (offTurn)
            {
                Assert.Equal((uint)offer.ThirdTile.Value.Value,
                    playerUpdate.Snapshot.PrivateView.Cheat.SleeveBloomReservation.TileId);
            }
            else
            {
                Assert.Equal((uint)offer.ThirdTile.Value.Value,
                    playerUpdate.Snapshot.PrivateView.DrawnTileId);
            }
        }

        [Theory]
        [InlineData(SleeveBloomKeep.First)]
        [InlineData(SleeveBloomKeep.Third)]
        public async Task octopus_off_turn_third_candidate_is_protected_across_draws_and_beam_and_delivers_once(
            SleeveBloomKeep choiceKeep)
        {
            var ct = TestContext.Current.CancellationToken;
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

            // Seat 0 discards -> opens off-turn window, seat 1 draws cleanly.
            var chain0 = P31Fixtures.BuildDiscardChain(round, 1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(h.Scope, chain0[0].Command, 0, "d-0"))).Status);

            string hitRequest = FindOctopusRequest(round.RoundId, 0, CountActorDrawTurns(GetRound(h.Actor)), true);
            TableOperationResult activated = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitSleeveBloomOffTurn,
                    hitRequest,
                    RoundCheatPayload.OffTurn("bloom-window-0-0-0"),
                    1,
                    h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);
            SleeveBloomOffer offer = GetCheat(h.Actor)!.SleeveBloom.OffTurnOfferFor(0)!;
            Assert.True(offer.ThirdTile.HasValue);

            // Advance seat 1 and seat 2 turns while the off-turn offer stays open: draws must skip First/Second/Third.
            RoundStateMachine r1 = GetRound(h.Actor);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(h.Scope, RoundCommand.Discard(round.RoundId, 1, r1.Tiles.DrawnTile!.Value.Tile), 2, "d-1"))).Status);
            RoundStateMachine r2 = GetRound(h.Actor);
            TileId seat2Drawn = r2.Tiles.DrawnTile!.Value.Tile;
            Assert.NotEqual(offer.FirstTile, seat2Drawn);
            Assert.NotEqual(offer.SecondTile, seat2Drawn);
            Assert.NotEqual(offer.ThirdTile.Value, seat2Drawn);

            // Another seat activating BeamSwap while seat 0's offer is open must also skip all 3 protected candidates.
            TableOperationResult beam2 = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    2,
                    RoundCheatAction.SubmitBeamSwap,
                    "beam-2",
                    RoundCheatPayload.BeamSwap(),
                    3,
                    h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, beam2.Status);
            BeamSwapOffer beamOffer2 = GetCheat(h.Actor)!.BeamSwap.Offer!;
            Assert.DoesNotContain(beamOffer2.Candidates, c =>
                c.Tile.Equals(offer.FirstTile) || c.Tile.Equals(offer.SecondTile) || c.Tile.Equals(offer.ThirdTile.Value));
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        2,
                        RoundCheatAction.SubmitBeamSwapChoice,
                        "beam-2-keep",
                        RoundCheatPayload.BeamSwapChoice(beamOffer2.OfferId, BeamSwapAction.KeepOriginal, null, null),
                        4,
                        h.Scope),
                    ct)).Status);

            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(h.Scope, RoundCommand.Discard(round.RoundId, 2, seat2Drawn), 5, "d-2"))).Status);
            RoundStateMachine r3 = GetRound(h.Actor);
            TileId seat3Drawn = r3.Tiles.DrawnTile!.Value.Tile;
            Assert.NotEqual(offer.ThirdTile.Value, seat3Drawn);

            // Owner chooses First or Third within deadline: reserved once, charged once (1000 -> 990).
            h.Clock.Now = offer.DeadlineMillis - 10;
            TableOperationResult chosen = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitSleeveBloomOffTurnChoice,
                    "choose-off-" + choiceKeep,
                    RoundCheatPayload.OffTurnChoice(offer.OfferId, choiceKeep),
                    6,
                    h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, chosen.Status);
            TileId expectedDelivered = choiceKeep == SleeveBloomKeep.Third ? offer.ThirdTile.Value : offer.FirstTile;
            Assert.Equal(expectedDelivered, GetCheat(h.Actor)!.SleeveBloom.ReservationFor(0)!.Tile);
            Assert.Equal(990, GetCheat(h.Actor)!.Ledger.BalanceOf(0));

            // Seat 3 discards -> delivers exactly once into seat 0 hand; balance & 136 conservation hold.
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(h.Scope, RoundCommand.Discard(round.RoundId, 3, seat3Drawn), 7, "d-3"))).Status);
            RoundStateMachine afterSeat0Draw = GetRound(h.Actor);
            Assert.Equal(0, afterSeat0Draw.CurrentSeat);
            Assert.Equal(expectedDelivered, afterSeat0Draw.Tiles.DrawnTile!.Value.Tile);
            Assert.Null(GetCheat(h.Actor)!.SleeveBloom.ReservationFor(0));
            Assert.Equal(990, GetCheat(h.Actor)!.Ledger.BalanceOf(0));
            Assert.True(TilePositionMap.Validate(afterSeat0Draw.Tiles).IsValid);
        }

        [Theory]
        [InlineData("v5-legacy-third-stolen")]
        [InlineData("v5-legacy-third-swapped")]
        [InlineData("v6-current-third-swapped")]
        [InlineData("v6-current-third-stolen")]
        [InlineData("v5-unrelated-first-stolen")]
        public async Task legacy_v5_journal_off_turn_third_drawn_recovers_while_v6_and_tampered_fail(string mode)
        {
            var ct = TestContext.Current.CancellationToken;
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

            var chain0 = P31Fixtures.BuildDiscardChain(round, 1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(h.Scope, chain0[0].Command, 0, "leg-d0"))).Status);

            string hitRequest = FindOctopusRequest(round.RoundId, 0, CountActorDrawTurns(GetRound(h.Actor)), true);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        0,
                        RoundCheatAction.SubmitSleeveBloomOffTurn,
                        hitRequest,
                        RoundCheatPayload.OffTurn("bloom-window-0-0-0"),
                        1,
                        h.Scope),
                    ct)).Status);

            RoundStateMachine beforeSeat2Draw = GetRound(h.Actor);
            CheatState activeCheat = GetCheat(h.Actor)!;
            SleeveBloomOffer offer = activeCheat.SleeveBloom.OffTurnOfferFor(0)!;
            TileId thirdTile = offer.ThirdTile!.Value;

            if (mode == "v5-legacy-third-swapped" || mode == "v6-current-third-swapped")
            {
                TableCheckpoint cpBeam = await h.SaveCheckpointAsync();
                if (mode == "v5-legacy-third-swapped")
                {
                    byte[] legacyBeam = TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                        TableCheatLedgerCheckpointCodec.Decode(cpBeam.CheatPayload!), 5);
                    cpBeam = new TableCheckpoint(
                        cpBeam.RoomId, cpBeam.StateVersion, cpBeam.LastEventSeq, cpBeam.StateFingerprint, cpBeam.RulesetVersion,
                        cpBeam.StatePayload, cpBeam.StatePayloadSha256, cpBeam.ControlVersion, cpBeam.ControlPayload, cpBeam.ControlPayloadSha256,
                        cpBeam.TimingPayload, cpBeam.TimingPayloadSha256, legacyBeam, TablePayloadSha256.Compute(legacyBeam),
                        cpBeam.CommittedFactsPayload, cpBeam.CommittedFactsPayloadSha256, cpBeam.CreatedAtUtcMs, cpBeam.BootId,
                        cpBeam.SchemaVersion, cpBeam.TimingVersion, cpBeam.CapturedMonotonicMillis,
                        cpBeam.CommittedHistoryPayload, cpBeam.CommittedHistoryPayloadSha256,
                        cpBeam.CommittedCommandBindingsPayload, cpBeam.CommittedCommandBindingsPayloadSha256);
                }
                h.Store.ReplaceCheckpointForTest(DurableHarness.Room, cpBeam);

                await CommitPreV6LegacyBeamSwapPairAsync(h, cpBeam, beforeSeat2Draw, activeCheat, thirdTile, ct);

                TableRecoveryResult beamRecovery = await h.Actor.RecoverFromStoreAsync(ct);
                if (mode == "v5-legacy-third-swapped")
                {
                    Assert.True(beamRecovery.Recovered, beamRecovery.ReasonCode + " " + beamRecovery.Detail);
                    RoundStateMachine recoveredRound = GetRound(h.Actor);
                    CheatState recoveredCheat = GetCheat(h.Actor)!;
                    Assert.Equal(TileZone.Hand, recoveredRound.Tiles.PositionOf(thirdTile).Zone);
                    Assert.Equal(1, recoveredRound.Tiles.PositionOf(thirdTile).Seat);
                    Assert.Equal(990, recoveredCheat.Ledger.BalanceOf(0));
                    Assert.Equal(990, recoveredCheat.Ledger.BalanceOf(1));
                    Assert.True(TilePositionMap.Validate(recoveredRound.Tiles).IsValid);
                }
                else
                {
                    Assert.Equal(TableRecoveryStatus.Failed, beamRecovery.Status);
                    Assert.Equal(TableRuntimeReasonCode.RecoveryReplayMismatch, beamRecovery.ReasonCode);
                }
                return;
            }

            // Advance via a fresh actor using unprotected-Third predicate to simulate pre-v6 live execution.
            RoundCommand seat1Discard = RoundCommand.Discard(round.RoundId, 1, beforeSeat2Draw.Tiles.DrawnTile!.Value.Tile);
            RoundPreparation v5DiscardPrep = beforeSeat2Draw.PrepareDiscardProtected(
                seat1Discard,
                tile => activeCheat.SleeveBloom.IsProtectedTileWithoutOffTurnThird(tile));
            Assert.True(v5DiscardPrep.IsReady);
            RoundTransition v5Discard = beforeSeat2Draw.Install(v5DiscardPrep.Change!);
            Assert.True(v5Discard.IsAccepted);
            RoundStateMachine v5Round = v5Discard.State!;
            Assert.Equal(thirdTile, v5Round.Tiles.DrawnTile!.Value.Tile);

            // Save checkpoint BEFORE seat 1's discard so seat 1's discard command sits in the journal.
            TableCheckpoint cp = await h.SaveCheckpointAsync();
            if (mode.StartsWith("v5-", StringComparison.Ordinal))
            {
                byte[] v5CheatPayload = TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                    TableCheatLedgerCheckpointCodec.Decode(cp.CheatPayload!), 5);
                cp = new TableCheckpoint(
                    cp.RoomId, cp.StateVersion, cp.LastEventSeq, cp.StateFingerprint, cp.RulesetVersion,
                    cp.StatePayload, cp.StatePayloadSha256,
                    cp.ControlVersion, cp.ControlPayload, cp.ControlPayloadSha256,
                    cp.TimingPayload, cp.TimingPayloadSha256,
                    v5CheatPayload, TablePayloadSha256.Compute(v5CheatPayload),
                    cp.CommittedFactsPayload, cp.CommittedFactsPayloadSha256,
                    cp.CreatedAtUtcMs, cp.BootId, cp.SchemaVersion, cp.TimingVersion, cp.CapturedMonotonicMillis,
                    cp.CommittedHistoryPayload, cp.CommittedHistoryPayloadSha256,
                    cp.CommittedCommandBindingsPayload, cp.CommittedCommandBindingsPayloadSha256);
                h.Store.ReplaceCheckpointForTest(DurableHarness.Room, cp);
            }

            // Now submit a normal draw on live actor, then rewrite the committed state/event to the v5 unprotected-Third draw (or stolen FirstTile).
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(h.Scope, seat1Discard, 2, "leg-d1"))).Status);

            if (mode == "v5-unrelated-first-stolen")
            {
                RoundPreparation stolenFirstPrep = beforeSeat2Draw.PrepareDiscardProtected(
                    seat1Discard,
                    tile => false);
                RoundStateMachine stolenFirstState = beforeSeat2Draw.Install(stolenFirstPrep.Change!).State!;
                RewriteLastDrawEventAndState(h.Store, stolenFirstState, offer.FirstTile);
            }
            else
            {
                RewriteLastDrawEventAndState(h.Store, v5Round, thirdTile);
            }

            TableRecoveryResult result = await h.Actor.RecoverFromStoreAsync(ct);
            if (mode == "v5-legacy-third-stolen")
            {
                Assert.True(result.Recovered, result.ReasonCode + " " + result.Detail);
                Assert.Equal(thirdTile, GetRound(h.Actor).Tiles.DrawnTile!.Value.Tile);

                // Offer settles cleanly to First on expiry without guessing or losing conservation.
                h.Clock.Now = offer.DeadlineMillis + 100;
                TableTickResult expired = await h.Actor.TickAsync(cancellationToken: ct);
                Assert.Equal(TableOperationStatus.Accepted, expired.Status);
                Assert.Equal(offer.FirstTile, GetCheat(h.Actor)!.SleeveBloom.ReservationFor(0)!.Tile);
                Assert.True(TilePositionMap.Validate(GetRound(h.Actor).Tiles).IsValid);
            }
            else
            {
                Assert.Equal(TableRecoveryStatus.Failed, result.Status);
                Assert.Equal(TableRuntimeReasonCode.RecoveryReplayMismatch, result.ReasonCode);
            }
        }

        [Theory]
        [InlineData("delivered-offturn-legacy")]
        [InlineData("checkpoint-only-v5-unknown")]
        [InlineData("v5-normal-third-untouched")]
        [InlineData("v6-normal-third-untouched")]
        [InlineData("v6-normal-third-returned-by-beam")]
        [InlineData("v6-normal-third-returned-by-beam-then-redrawn")]
        [InlineData("v6-normal-third-returned-by-beam-journal-tail")]
        [InlineData("v6-normal-third-drawn-then-selected-again")]
        [InlineData("v5-second-tile-rejected")]
        [InlineData("v5-facts-mismatch-rejected")]
        [InlineData("v5-delivered-arbitrary-tile-rejected")]
        [InlineData("v5-immediate-arbitrary-tile-rejected")]
        [InlineData("v6-null-result-rejected")]
        [InlineData("v6-delivered-first-rejected")]
        [InlineData("v6-delivered-arbitrary-tile-rejected")]
        public async Task checkpoint_only_recovery_resave_handles_verifiable_delivered_unknown_v5_and_rejects_tampered(
            string mode)
        {
            var ct = TestContext.Current.CancellationToken;
            var codec = new StrictWireStreamCodec();
            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = new RecordingStreamTerminationSink()
            };
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
                },
                streamOptions: streamOptions);

            bool offTurnFlow = mode == "delivered-offturn-legacy" ||
                               mode == "v5-delivered-arbitrary-tile-rejected" ||
                               mode == "v6-delivered-first-rejected" ||
                               mode == "v6-delivered-arbitrary-tile-rejected";
            int setupDiscards = offTurnFlow ? 1 : 4;
            var chain = P31Fixtures.BuildDiscardChain(round, 4);
            for (int i = 0; i < setupDiscards; i++)
            {
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(h.Scope, chain[i].Command, i, "m-r-" + i))).Status);
            }

            int expectedVersion = setupDiscards;
            string hitRequest = FindOctopusRequest(round.RoundId, 0, CountActorDrawTurns(GetRound(h.Actor)), true);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        0,
                        offTurnFlow ? RoundCheatAction.SubmitSleeveBloomOffTurn : RoundCheatAction.SubmitSleeveBloom,
                        hitRequest,
                        offTurnFlow ? RoundCheatPayload.OffTurn("bloom-window-0-0-0") : RoundCheatPayload.SleeveBloom(),
                        expectedVersion++,
                        h.Scope),
                    ct)).Status);

            SleeveBloomOffer offer = (offTurnFlow
                ? GetCheat(h.Actor)!.SleeveBloom.OffTurnOfferFor(0)
                : GetCheat(h.Actor)!.SleeveBloom.ImmediateOffer)!;
            Assert.True(offer.ThirdTile.HasValue);

            TableOperationResult chosen = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    offTurnFlow ? RoundCheatAction.SubmitSleeveBloomOffTurnChoice : RoundCheatAction.SubmitSleeveBloomChoice,
                    "m-third",
                    offTurnFlow
                        ? RoundCheatPayload.OffTurnChoice(offer.OfferId, SleeveBloomKeep.Third)
                        : RoundCheatPayload.SleeveBloomChoice(offer.OfferId, SleeveBloomKeep.Third),
                    expectedVersion++,
                    h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, chosen.Status);
            TableEventRecord resolved = Assert.Single(
                chosen.Events,
                row => row.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);

            SleeveBloomOffer? secondOffer = null;
            TableEventRecord? secondResolved = null;
            if (offTurnFlow)
            {
                // Deliver seat 0's reservation by discarding through seats 1..3.
                for (int seat = 1; seat <= 3; seat++)
                {
                    RoundStateMachine cur = GetRound(h.Actor);
                    Assert.Equal(
                        TableOperationStatus.Accepted,
                        (await h.SubmitAsync(DurableRound(
                            h.Scope,
                            RoundCommand.Discard(round.RoundId, seat, cur.Tiles.DrawnTile!.Value.Tile),
                            expectedVersion++,
                            "m-deliv-" + seat))).Status);
                }
            }
            else if (mode == "checkpoint-only-v5-unknown" ||
                     mode == "v5-normal-third-untouched" ||
                     mode == "v6-normal-third-untouched" ||
                     mode == "v5-immediate-arbitrary-tile-rejected")
            {
                // Advance one turn past immediate choice so DrawnTile moves on (no immediate checkpoint Third proof).
                RoundStateMachine cur = GetRound(h.Actor);
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(
                        h.Scope,
                        RoundCommand.Discard(round.RoundId, 0, cur.Tiles.DrawnTile!.Value.Tile),
                        expectedVersion++,
                        "m-after-d0"))).Status);
            }
            else if (mode == "v6-normal-third-returned-by-beam" ||
                     mode == "v6-normal-third-returned-by-beam-then-redrawn" ||
                     mode == "v6-normal-third-returned-by-beam-journal-tail" ||
                     mode == "v6-normal-third-drawn-then-selected-again")
            {
                TileId third = offer.ThirdTile.Value;
                bool journalTail = mode == "v6-normal-third-returned-by-beam-journal-tail";
                bool chosenAgain = mode == "v6-normal-third-drawn-then-selected-again";
                if (journalTail)
                {
                    TableCheckpoint boundary = await h.SaveCheckpointAsync();
                    Assert.Equal(expectedVersion, (int)boundary.StateVersion.Value);
                }

                for (int seat = 0; seat < 4; seat++)
                {
                    RoundStateMachine cur = GetRound(h.Actor);
                    TileId discard = seat == 0
                        ? cur.Tiles.AsPlacements().First(p => p.Position.Zone == TileZone.Hand &&
                            p.Position.Seat == seat && !p.Tile.Equals(third)).Tile
                        : cur.Tiles.DrawnTile!.Value.Tile;
                    Assert.Equal(
                        TableOperationStatus.Accepted,
                        (await h.SubmitAsync(DurableRound(
                            h.Scope,
                            RoundCommand.Discard(round.RoundId, seat, discard),
                            expectedVersion++,
                            "beam-orbit-" + seat))).Status);
                }

                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.Actor.SubmitCheatAsync(
                        DurableCheat(
                            round.RoundId,
                            0,
                            RoundCheatAction.SubmitBeamSwap,
                            "beam-return-act",
                            RoundCheatPayload.BeamSwap(),
                            expectedVersion++,
                            h.Scope),
                        ct)).Status);
                BeamSwapOffer beam = GetCheat(h.Actor)!.BeamSwap.Offer!;
                Assert.Contains(third, beam.EligibleHandTileIds);
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.Actor.SubmitCheatAsync(
                        DurableCheat(
                            round.RoundId,
                            0,
                            RoundCheatAction.SubmitBeamSwapChoice,
                            "beam-return-swap",
                            RoundCheatPayload.BeamSwapChoice(beam.OfferId, BeamSwapAction.Swap, third, beam.Candidates[0].Slot),
                            expectedVersion++,
                            h.Scope),
                        ct)).Status);
                Assert.Equal(TileZone.Wall, GetRound(h.Actor).Tiles.PositionOf(third).Zone);

                if (mode == "v6-normal-third-returned-by-beam-then-redrawn" || chosenAgain)
                {
                    RoundStateMachine beforeRedraw = GetRound(h.Actor);
                    TileId nonThirdHandTile = beforeRedraw.Tiles.DrawnTile!.Value.Tile;
                    Assert.NotEqual(third, nonThirdHandTile);
                    Assert.Equal(
                        TableOperationStatus.Accepted,
                        (await h.SubmitAsync(DurableRound(
                            h.Scope,
                            RoundCommand.Discard(round.RoundId, 0, nonThirdHandTile),
                            expectedVersion++,
                            "beam-after-redraw-d0"))).Status);
                    RoundStateMachine afterRedraw = GetRound(h.Actor);
                    Assert.Equal(1, afterRedraw.CurrentSeat);
                    Assert.Equal(third, afterRedraw.Tiles.DrawnTile!.Value.Tile);
                    Assert.Equal(TileZone.Hand, afterRedraw.Tiles.PositionOf(third).Zone);
                }

                if (chosenAgain)
                {
                    foreach (int seat in new[] { 1, 2, 3, 0 })
                    {
                        RoundStateMachine cur = GetRound(h.Actor);
                        TileId discard = seat == 1
                            ? cur.Tiles.TilesInZone(TileZone.Hand, seat).First(tile => !tile.Equals(third))
                            : cur.Tiles.DrawnTile!.Value.Tile;
                        Assert.Equal(
                            TableOperationStatus.Accepted,
                            (await h.SubmitAsync(DurableRound(
                                h.Scope,
                                RoundCommand.Discard(round.RoundId, seat, discard),
                                expectedVersion++,
                                "second-orbit-" + seat))).Status);
                    }

                    Assert.Equal(
                        TableOperationStatus.Accepted,
                        (await h.Actor.SubmitCheatAsync(
                            DurableCheat(
                                round.RoundId,
                                1,
                                RoundCheatAction.SubmitBeamSwap,
                                "second-return-act",
                                RoundCheatPayload.BeamSwap(),
                                expectedVersion++,
                                h.Scope),
                            ct)).Status);
                    BeamSwapOffer secondBeam = GetCheat(h.Actor)!.BeamSwap.Offer!;
                    int futureThirdIndex = GetRound(h.Actor).Tiles.WallPointer + 4;
                    BeamSwapCandidate futureThird = Assert.Single(secondBeam.Candidates, c => c.Slot.Index == futureThirdIndex);
                    Assert.Contains(third, secondBeam.EligibleHandTileIds);
                    Assert.Equal(
                        TableOperationStatus.Accepted,
                        (await h.Actor.SubmitCheatAsync(
                            DurableCheat(
                                round.RoundId,
                                1,
                                RoundCheatAction.SubmitBeamSwapChoice,
                                "second-return-swap",
                                RoundCheatPayload.BeamSwapChoice(secondBeam.OfferId, BeamSwapAction.Swap, third, futureThird.Slot),
                                expectedVersion++,
                                h.Scope),
                            ct)).Status);
                    foreach (int seat in new[] { 1, 2, 3 })
                    {
                        RoundStateMachine cur = GetRound(h.Actor);
                        Assert.Equal(
                            TableOperationStatus.Accepted,
                            (await h.SubmitAsync(DurableRound(
                                h.Scope,
                                RoundCommand.Discard(round.RoundId, seat, cur.Tiles.DrawnTile!.Value.Tile),
                                expectedVersion++,
                                "third-orbit-" + seat))).Status);
                    }

                    string secondHit = FindOctopusRequest(round.RoundId, 0, CountActorDrawTurns(GetRound(h.Actor)), true);
                    Assert.Equal(
                        TableOperationStatus.Accepted,
                        (await h.Actor.SubmitCheatAsync(
                            DurableCheat(
                                round.RoundId,
                                0,
                                RoundCheatAction.SubmitSleeveBloom,
                                secondHit,
                                RoundCheatPayload.SleeveBloom(),
                                expectedVersion++,
                                h.Scope),
                            ct)).Status);
                    secondOffer = GetCheat(h.Actor)!.SleeveBloom.ImmediateOffer!;
                    Assert.Equal(third, secondOffer.ThirdTile);
                    TableOperationResult secondChoice = await h.Actor.SubmitCheatAsync(
                        DurableCheat(
                            round.RoundId,
                            0,
                            RoundCheatAction.SubmitSleeveBloomChoice,
                            "second-third",
                            RoundCheatPayload.SleeveBloomChoice(secondOffer.OfferId, SleeveBloomKeep.Third),
                            expectedVersion++,
                            h.Scope),
                        ct);
                    Assert.Equal(TableOperationStatus.Accepted, secondChoice.Status);
                    secondResolved = Assert.Single(
                        secondChoice.Events,
                        row => row.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
                    Assert.Equal(third, secondResolved.Cheat!.ResultTile);
                    Assert.Equal(
                        TableOperationStatus.Accepted,
                        (await h.SubmitAsync(DurableRound(
                            h.Scope,
                            RoundCommand.Discard(round.RoundId, 0, third),
                            expectedVersion++,
                            "second-third-discard"))).Status);
                }
            }

            bool skipInitialCheckpointSave = mode == "v6-normal-third-returned-by-beam-journal-tail";
            bool patchHistory = mode != "v5-normal-third-untouched" &&
                                mode != "v6-normal-third-untouched" &&
                                mode != "v6-normal-third-returned-by-beam" &&
                                mode != "v6-normal-third-returned-by-beam-then-redrawn" &&
                                mode != "v6-normal-third-returned-by-beam-journal-tail" &&
                                mode != "v6-normal-third-drawn-then-selected-again";
            bool downgradeCheatV5 = mode == "delivered-offturn-legacy" ||
                                    mode == "checkpoint-only-v5-unknown" ||
                                    mode.StartsWith("v5-", StringComparison.Ordinal);

            if (!skipInitialCheckpointSave)
            {
                TableCheckpoint cp = await h.SaveCheckpointAsync();
                if (patchHistory || downgradeCheatV5)
                {
                    TileId arbitraryWallOrOtherTile = Enumerable.Range(0, TileId.Count)
                        .Select(id => new TileId(id))
                        .First(tile => !tile.Equals(offer.FirstTile) &&
                                       !tile.Equals(offer.SecondTile) &&
                                       !tile.Equals(offer.ThirdTile.Value) &&
                                       GetRound(h.Actor).Tiles.PositionOf(tile).Zone == TileZone.Wall);
                    TileId? patchedHistoryTile = mode switch
                    {
                        "v5-second-tile-rejected" => offer.SecondTile,
                        "v6-null-result-rejected" => null,
                        "v5-delivered-arbitrary-tile-rejected" => arbitraryWallOrOtherTile,
                        "v5-immediate-arbitrary-tile-rejected" => arbitraryWallOrOtherTile,
                        "v6-delivered-arbitrary-tile-rejected" => arbitraryWallOrOtherTile,
                        _ => offer.FirstTile
                    };

                    byte[] cheatPayload = downgradeCheatV5
                        ? TableCheatLedgerCheckpointCodec.EncodeWithSchema(
                            TableCheatLedgerCheckpointCodec.Decode(cp.CheatPayload!), 5)
                        : (byte[])cp.CheatPayload!.Clone();

                    byte[] historyBytes = cp.CommittedHistoryPayload!;
                    if (patchHistory)
                    {
                        TableEventRecord[] rows = TableCommittedHistoryCodec.Decode(historyBytes)
                            .Select(row =>
                            {
                                if (!row.Seq.Equals(resolved.Seq))
                                {
                                    return row;
                                }

                                TableCheatEventRecord c = row.Cheat!;
                                return new TableEventRecord(
                                    row.Seq, row.CommandVersion, row.Ordinal, row.Type, row.Seat, row.Tile, row.Detail,
                                    row.EventSchema, row.Payload,
                                    new TableCheatEventRecord(
                                        c.Event, c.OfferId, c.Keep, c.Action, patchedHistoryTile, c.HandTile, c.TargetSlot, c.TurnNumber));
                            })
                            .ToArray();
                        historyBytes = TableCommittedHistoryCodec.Encode(rows);
                    }

                    byte[] factsBytes = cp.CommittedFactsPayload!;
                    if (patchHistory)
                    {
                        uint? patchedFactTile = mode == "v5-facts-mismatch-rejected"
                            ? (uint)offer.SecondTile.Value
                            : (patchedHistoryTile.HasValue ? (uint)patchedHistoryTile.Value.Value : (uint?)null);
                        TableProjectionContextData origFacts = TableProjectionFactsCodec.Decode(
                            factsBytes, round.RoundId, cp.StateVersion);
                        var updatedEventFacts = origFacts.EventFacts
                            .Select(fact => fact.EventSeq.Equals(resolved.Seq)
                                ? new TableProjectionEventFact(
                                    fact.Type, fact.EventSeq, fact.CommandVersion, fact.EventOrdinal,
                                    new TableProjectionEventFacts(
                                        turnNumber: fact.Facts.TurnNumber,
                                        offerId: fact.Facts.OfferId,
                                        keptFirst: fact.Facts.KeptFirst,
                                        resultTileId: patchedFactTile))
                                : fact)
                            .ToArray();
                        factsBytes = TableProjectionFactsCodec.Encode(new TableProjectionContextData(
                            origFacts.RoundId, origFacts.StateVersion, origFacts.SessionVersion, origFacts.Cheat,
                            origFacts.PublicFacts, origFacts.PrivateFacts, origFacts.Session, origFacts.Policy,
                            updatedEventFacts, origFacts.ConcealedMeldFacts));
                    }

                    var updatedCp = new TableCheckpoint(
                        cp.RoomId, cp.StateVersion, cp.LastEventSeq, cp.StateFingerprint, cp.RulesetVersion,
                        cp.StatePayload, cp.StatePayloadSha256,
                        cp.ControlVersion, cp.ControlPayload, cp.ControlPayloadSha256,
                        cp.TimingPayload, cp.TimingPayloadSha256,
                        cheatPayload, TablePayloadSha256.Compute(cheatPayload),
                        factsBytes, TablePayloadSha256.Compute(factsBytes),
                        cp.CreatedAtUtcMs, cp.BootId, cp.SchemaVersion, cp.TimingVersion, cp.CapturedMonotonicMillis,
                        historyBytes, TablePayloadSha256.Compute(historyBytes),
                        cp.CommittedCommandBindingsPayload, cp.CommittedCommandBindingsPayloadSha256);
                    h.Store.ReplaceCheckpointForTest(DurableHarness.Room, updatedCp);
                    var factsField = typeof(InMemoryTableStore).GetField("_facts", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var factsMap = (Dictionary<string, TableCommittedFactsSnapshot>)factsField.GetValue(h.Store)!;
                    factsMap[DurableHarness.Room] = new TableCommittedFactsSnapshot(
                        factsBytes,
                        TablePayloadSha256.Compute(factsBytes));
                }
            }

            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(ct);
            if (mode.EndsWith("-rejected", StringComparison.Ordinal))
            {
                Assert.Equal(TableRecoveryStatus.Failed, recovered.Status);
                Assert.Equal(TableRuntimeReasonCode.CheckpointInvalid, recovered.ReasonCode);
                return;
            }

            Assert.True(recovered.Recovered, recovered.ReasonCode + " " + recovered.Detail);
            TableCheckpoint resaved = await h.SaveCheckpointAsync();
            IReadOnlyList<TableEventRecord> resavedHistory = TableCommittedHistoryCodec.Decode(resaved.CommittedHistoryPayload!);
            TableProjectionEventFact[] resavedEventFacts = TableProjectionFactsCodec
                .Decode(resaved.CommittedFactsPayload!, round.RoundId, resaved.StateVersion)
                .EventFacts
                .ToArray();
            TableEventRecord resavedRow = Assert.Single(resavedHistory, row => row.Seq.Equals(resolved.Seq));
            TableProjectionEventFact resavedFact = Assert.Single(resavedEventFacts, fact => fact.EventSeq.Equals(resolved.Seq));

            TileId? expectedStored = mode == "checkpoint-only-v5-unknown" ? null : offer.ThirdTile.Value;
            uint? expectedFactId = expectedStored.HasValue ? (uint)expectedStored.Value.Value : (uint?)null;
            Assert.Equal(expectedStored, resavedRow.Cheat!.ResultTile);
            Assert.Equal(expectedFactId, resavedFact.Facts.ResultTileId);
            if (secondResolved != null)
            {
                TableEventRecord resavedSecondRow = Assert.Single(resavedHistory, row => row.Seq.Equals(secondResolved.Seq));
                TableProjectionEventFact resavedSecondFact = Assert.Single(
                    resavedEventFacts,
                    fact => fact.EventSeq.Equals(secondResolved.Seq));
                Assert.Equal(expectedStored, resavedSecondRow.Cheat!.ResultTile);
                Assert.Equal(expectedFactId, resavedSecondFact.Facts.ResultTileId);
            }

            TableRecoveryResult secondReload = await h.Actor.RecoverFromStoreAsync(ct);
            Assert.True(secondReload.Recovered, secondReload.ReasonCode + " " + secondReload.Detail);
            RoundStateMachine reloadedRound = GetRound(h.Actor);
            Assert.Equal(TileId.Count, reloadedRound.Tiles.AsPlacements().Select(p => p.Tile).Distinct().Count());
            if (mode == "v6-normal-third-returned-by-beam" ||
                mode == "v6-normal-third-returned-by-beam-journal-tail")
            {
                Assert.Equal(TileZone.Wall, reloadedRound.Tiles.PositionOf(offer.ThirdTile.Value).Zone);
            }
            else if (mode == "v6-normal-third-returned-by-beam-then-redrawn")
            {
                TilePosition redrawnPos = reloadedRound.Tiles.PositionOf(offer.ThirdTile.Value);
                Assert.Equal(TileZone.Hand, redrawnPos.Zone);
                Assert.Equal(1, redrawnPos.Seat);
            }
            else if (mode == "v6-normal-third-drawn-then-selected-again")
            {
                Assert.Equal(TileZone.Discard, reloadedRound.Tiles.PositionOf(offer.ThirdTile.Value).Zone);
            }

            // Spectator/other-seat privacy stays closed when re-subscribing after recovery.
            var specSink = new RecordingStreamSink();
            TableStreamControlResult specSub = await h.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("spec-cp-rec"),
                        TableStreamAudience.Spectator,
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    secondReload.Report!.CurrentOwnerEpoch,
                    h.Actor.RecoveryGeneration,
                    specSink,
                    new RoundId(round.RoundId),
                    null),
                ct);
            Assert.Equal(TableStreamControlStatus.Subscribed, specSub.Status);
            await specSink.WaitForCountAsync(1);
            string specWire = System.Text.Encoding.UTF8.GetString(specSink.Frames[0].EncodedPayload!);
            Assert.DoesNotContain(offer.OfferId, specWire);
            if (secondOffer != null)
            {
                Assert.DoesNotContain(secondOffer.OfferId, specWire);
            }

            // Owner stream after recovery projects null ResultTileId for unknown v5 without fabricating an id.
            var ownerSink = new RecordingStreamSink();
            TableStreamControlResult ownerSub = await h.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("owner-cp-v5"),
                        TableStreamAudience.ForSeat(0),
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    secondReload.Report.CurrentOwnerEpoch,
                    h.Actor.RecoveryGeneration,
                    ownerSink,
                    new RoundId(round.RoundId),
                    new ControlEpoch(0)),
                ct);
            Assert.Equal(TableStreamControlStatus.Subscribed, ownerSub.Status);
            await ownerSink.WaitForCountAsync(1);
            Envelope ownerEnv = StrictWireStreamCodec.DecodeAndStrict(ownerSink.Frames[0].EncodedPayload!);
            Assert.NotNull(ownerEnv.PlayerUpdate);
            foreach (EventEnvelope ev in ownerEnv.PlayerUpdate.VisibleEvents)
            {
                if (ev.EventCase == EventEnvelope.EventOneofCase.SleeveBloomResolved &&
                    (ev.SleeveBloomResolved.OfferId == offer.OfferId ||
                     (secondOffer != null && ev.SleeveBloomResolved.OfferId == secondOffer.OfferId)))
                {
                    Assert.Equal(expectedStored.HasValue, ev.SleeveBloomResolved.HasResultTileId);
                    if (expectedStored.HasValue)
                    {
                        Assert.Equal((uint)expectedStored.Value.Value, ev.SleeveBloomResolved.ResultTileId);
                    }
                }
            }
        }

        [Theory]
        [InlineData("before-first-choice")]
        [InlineData("after-first-choice")]
        [InlineData("before-first-beam")]
        [InlineData("after-first-beam")]
        [InlineData("after-first-redraw")]
        [InlineData("before-second-choice")]
        [InlineData("after-second-choice")]
        [InlineData("after-second-discard")]
        public async Task checkpoint_cutpoints_preserve_beam_return_redraw_and_reselection_history(
            string cutpoint)
        {
            var ct = TestContext.Current.CancellationToken;
            var codec = new StrictWireStreamCodec();
            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = new RecordingStreamTerminationSink()
            };
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
                },
                streamOptions: streamOptions);

            var chain = P31Fixtures.BuildDiscardChain(round, 4);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(h.Scope, chain[i].Command, i, "cut-setup-" + i))).Status);
            }

            int expectedVersion = 4;
            string hit1 = FindOctopusRequest(round.RoundId, 0, CountActorDrawTurns(GetRound(h.Actor)), true);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        0,
                        RoundCheatAction.SubmitSleeveBloom,
                        hit1,
                        RoundCheatPayload.SleeveBloom(),
                        expectedVersion++,
                        h.Scope),
                    ct)).Status);
            SleeveBloomOffer firstOffer = GetCheat(h.Actor)!.SleeveBloom.ImmediateOffer!;
            TileId targetTile = firstOffer.ThirdTile!.Value;
            if (cutpoint == "before-first-choice")
            {
                await h.SaveCheckpointAsync();
            }

            TableOperationResult firstChoice = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitSleeveBloomChoice,
                    "cut-third-1",
                    RoundCheatPayload.SleeveBloomChoice(firstOffer.OfferId, SleeveBloomKeep.Third),
                    expectedVersion++,
                    h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, firstChoice.Status);
            TableEventRecord firstResolved = Assert.Single(
                firstChoice.Events,
                row => row.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            if (cutpoint == "after-first-choice")
            {
                await h.SaveCheckpointAsync();
            }

            for (int seat = 0; seat < 4; seat++)
            {
                RoundStateMachine cur = GetRound(h.Actor);
                TileId discard = seat == 0
                    ? cur.Tiles.TilesInZone(TileZone.Hand, seat).First(t => !t.Equals(targetTile))
                    : cur.Tiles.DrawnTile!.Value.Tile;
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(
                        h.Scope,
                        RoundCommand.Discard(round.RoundId, seat, discard),
                        expectedVersion++,
                        "cut-orbit1-" + seat))).Status);
            }

            if (cutpoint == "before-first-beam")
            {
                await h.SaveCheckpointAsync();
            }

            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        0,
                        RoundCheatAction.SubmitBeamSwap,
                        "cut-beam1-act",
                        RoundCheatPayload.BeamSwap(),
                        expectedVersion++,
                        h.Scope),
                    ct)).Status);
            BeamSwapOffer beam1 = GetCheat(h.Actor)!.BeamSwap.Offer!;
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        0,
                        RoundCheatAction.SubmitBeamSwapChoice,
                        "cut-beam1-swap",
                        RoundCheatPayload.BeamSwapChoice(beam1.OfferId, BeamSwapAction.Swap, targetTile, beam1.Candidates[0].Slot),
                        expectedVersion++,
                        h.Scope),
                    ct)).Status);
            Assert.Equal(TileZone.Wall, GetRound(h.Actor).Tiles.PositionOf(targetTile).Zone);
            if (cutpoint == "after-first-beam")
            {
                await h.SaveCheckpointAsync();
            }

            RoundStateMachine beforeRedraw = GetRound(h.Actor);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(
                    h.Scope,
                    RoundCommand.Discard(round.RoundId, 0, beforeRedraw.Tiles.DrawnTile!.Value.Tile),
                    expectedVersion++,
                    "cut-redraw-d0"))).Status);
            Assert.Equal(targetTile, GetRound(h.Actor).Tiles.DrawnTile!.Value.Tile);
            if (cutpoint == "after-first-redraw")
            {
                await h.SaveCheckpointAsync();
            }

            foreach (int seat in new[] { 1, 2, 3, 0 })
            {
                RoundStateMachine cur = GetRound(h.Actor);
                TileId discard = seat == 1
                    ? cur.Tiles.TilesInZone(TileZone.Hand, seat).First(t => !t.Equals(targetTile))
                    : cur.Tiles.DrawnTile!.Value.Tile;
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(
                        h.Scope,
                        RoundCommand.Discard(round.RoundId, seat, discard),
                        expectedVersion++,
                        "cut-orbit2-" + seat))).Status);
            }

            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        1,
                        RoundCheatAction.SubmitBeamSwap,
                        "cut-beam2-act",
                        RoundCheatPayload.BeamSwap(),
                        expectedVersion++,
                        h.Scope),
                    ct)).Status);
            BeamSwapOffer beam2 = GetCheat(h.Actor)!.BeamSwap.Offer!;
            int futureThirdIndex = GetRound(h.Actor).Tiles.WallPointer + 4;
            BeamSwapCandidate futureThird = Assert.Single(beam2.Candidates, c => c.Slot.Index == futureThirdIndex);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        1,
                        RoundCheatAction.SubmitBeamSwapChoice,
                        "cut-beam2-swap",
                        RoundCheatPayload.BeamSwapChoice(beam2.OfferId, BeamSwapAction.Swap, targetTile, futureThird.Slot),
                        expectedVersion++,
                        h.Scope),
                    ct)).Status);

            foreach (int seat in new[] { 1, 2, 3 })
            {
                RoundStateMachine cur = GetRound(h.Actor);
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await h.SubmitAsync(DurableRound(
                        h.Scope,
                        RoundCommand.Discard(round.RoundId, seat, cur.Tiles.DrawnTile!.Value.Tile),
                        expectedVersion++,
                        "cut-orbit3-" + seat))).Status);
            }

            string hit2 = FindOctopusRequest(round.RoundId, 0, CountActorDrawTurns(GetRound(h.Actor)), true);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.Actor.SubmitCheatAsync(
                    DurableCheat(
                        round.RoundId,
                        0,
                        RoundCheatAction.SubmitSleeveBloom,
                        hit2,
                        RoundCheatPayload.SleeveBloom(),
                        expectedVersion++,
                        h.Scope),
                    ct)).Status);
            SleeveBloomOffer secondOffer = GetCheat(h.Actor)!.SleeveBloom.ImmediateOffer!;
            Assert.Equal(targetTile, secondOffer.ThirdTile);
            if (cutpoint == "before-second-choice")
            {
                await h.SaveCheckpointAsync();
            }

            TableOperationResult secondChoice = await h.Actor.SubmitCheatAsync(
                DurableCheat(
                    round.RoundId,
                    0,
                    RoundCheatAction.SubmitSleeveBloomChoice,
                    "cut-third-2",
                    RoundCheatPayload.SleeveBloomChoice(secondOffer.OfferId, SleeveBloomKeep.Third),
                    expectedVersion++,
                    h.Scope),
                ct);
            Assert.Equal(TableOperationStatus.Accepted, secondChoice.Status);
            TableEventRecord secondResolved = Assert.Single(
                secondChoice.Events,
                row => row.Cheat?.Event.Kind == CheatEventKind.SleeveBloomResolved);
            if (cutpoint == "after-second-choice")
            {
                await h.SaveCheckpointAsync();
            }

            Assert.Equal(
                TableOperationStatus.Accepted,
                (await h.SubmitAsync(DurableRound(
                    h.Scope,
                    RoundCommand.Discard(round.RoundId, 0, targetTile),
                    expectedVersion++,
                    "cut-third2-discard"))).Status);
            if (cutpoint == "after-second-discard")
            {
                await h.SaveCheckpointAsync();
            }

            TableRecoveryResult firstRecover = await h.Actor.RecoverFromStoreAsync(ct);
            Assert.True(firstRecover.Recovered, firstRecover.ReasonCode + " " + firstRecover.Detail);

            TableCheckpoint resaved = await h.SaveCheckpointAsync();
            IReadOnlyList<TableEventRecord> historyRows = TableCommittedHistoryCodec.Decode(resaved.CommittedHistoryPayload!);
            TableProjectionEventFact[] eventFacts = TableProjectionFactsCodec
                .Decode(resaved.CommittedFactsPayload!, round.RoundId, resaved.StateVersion)
                .EventFacts
                .ToArray();

            uint expectedId = unchecked((uint)targetTile.Value);
            foreach (EventSeq seq in new[] { firstResolved.Seq, secondResolved.Seq })
            {
                TableEventRecord row = Assert.Single(historyRows, item => item.Seq.Equals(seq));
                TableProjectionEventFact fact = Assert.Single(eventFacts, item => item.EventSeq.Equals(seq));
                Assert.Equal(targetTile, row.Cheat!.ResultTile);
                Assert.Equal(expectedId, fact.Facts.ResultTileId);
            }

            TableRecoveryResult secondRecover = await h.Actor.RecoverFromStoreAsync(ct);
            Assert.True(secondRecover.Recovered, secondRecover.ReasonCode + " " + secondRecover.Detail);
            RoundStateMachine finalRound = GetRound(h.Actor);
            Assert.Equal(TileId.Count, finalRound.Tiles.AsPlacements().Select(p => p.Tile).Distinct().Count());
            Assert.Equal(TileZone.Discard, finalRound.Tiles.PositionOf(targetTile).Zone);

            var specSink = new RecordingStreamSink();
            TableStreamControlResult specSub = await h.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("spec-cut"),
                        TableStreamAudience.Spectator,
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    secondRecover.Report!.CurrentOwnerEpoch,
                    h.Actor.RecoveryGeneration,
                    specSink,
                    new RoundId(round.RoundId),
                    null),
                ct);
            Assert.Equal(TableStreamControlStatus.Subscribed, specSub.Status);
            await specSink.WaitForCountAsync(1);
            string specWire = System.Text.Encoding.UTF8.GetString(specSink.Frames[0].EncodedPayload!);
            Assert.DoesNotContain(firstOffer.OfferId, specWire);
            Assert.DoesNotContain(secondOffer.OfferId, specWire);

            var ownerSink = new RecordingStreamSink();
            TableStreamControlResult ownerSub = await h.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("owner-cut"),
                        TableStreamAudience.ForSeat(0),
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    secondRecover.Report.CurrentOwnerEpoch,
                    h.Actor.RecoveryGeneration,
                    ownerSink,
                    new RoundId(round.RoundId),
                    new ControlEpoch(0)),
                ct);
            Assert.Equal(TableStreamControlStatus.Subscribed, ownerSub.Status);
            await ownerSink.WaitForCountAsync(1);
            Envelope ownerEnv = StrictWireStreamCodec.DecodeAndStrict(ownerSink.Frames[0].EncodedPayload!);
            Assert.NotNull(ownerEnv.PlayerUpdate);
            Assert.NotNull(ownerEnv.PlayerUpdate.Snapshot.PrivateView);
            Assert.Equal(
                GetCheat(h.Actor)!.Ledger.BalanceOf(0),
                ownerEnv.PlayerUpdate.Snapshot.PrivateView.Cheat.CheatPoints);

            var otherSink = new RecordingStreamSink();
            TableStreamControlResult otherSub = await h.Actor.SubscribeStreamAsync(
                new TableStreamSubscribeRequest(
                    new TableStreamIdentity(
                        new SessionId("other-cut"),
                        TableStreamAudience.ForSeat(1),
                        new BindingEpoch(1),
                        new SyncEpoch(1)),
                    secondRecover.Report.CurrentOwnerEpoch,
                    h.Actor.RecoveryGeneration,
                    otherSink,
                    new RoundId(round.RoundId),
                    new ControlEpoch(0)),
                ct);
            Assert.Equal(TableStreamControlStatus.Subscribed, otherSub.Status);
            await otherSink.WaitForCountAsync(1);
            string otherWire = System.Text.Encoding.UTF8.GetString(otherSink.Frames[0].EncodedPayload!);
            Assert.DoesNotContain(firstOffer.OfferId, otherWire);
            Assert.DoesNotContain(secondOffer.OfferId, otherWire);
        }

        private static async Task CommitPreV6LegacyBeamSwapPairAsync(
            DurableHarness h,
            TableCheckpoint currentCp,
            RoundStateMachine startRound,
            CheatState startCheat,
            TileId thirdTile,
            System.Threading.CancellationToken ct)
        {
            CheatEconomyConfig economy = V32Fixtures.Economy(InitialPoints);
            SleeveBloomConfig sleeveConfig = V32Fixtures.SleeveConfig();

            TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(currentCp.TimingPayload!);
            long timingVersion = currentCp.TimingVersion;
            ulong nextSeq = currentCp.LastEventSeq.Value + 1;
            StateVersion evaluated = currentCp.StateVersion;
            RoundStateMachine round = startRound;
            CheatState cheat = startCheat;
            var allHistory = new List<TableEventRecord>(TableCommittedHistoryCodec.Decode(currentCp.CommittedHistoryPayload!));

            async Task CommitStepAsync(RoundCheatCommand cmd, string requestId, long nowMillis)
            {
                int turnNumber = CountActorDrawTurns(round);
                var ctx = new CheatTurnContext(
                    round.CurrentSeat,
                    round.Phase,
                    round.Window != null,
                    round.RiichiDeclared,
                    turnNumber,
                    round.RoundId,
                    cheat.TileMarks.DealtRoundOrdinal,
                    Mahjong.Domain.Cheat.SleeveBloomDrawKind.Normal,
                    nowMillis,
                    DurableHarness.Boot);
                RoundCheatPreparation prep = RoundCheatTransition.Prepare(
                    round,
                    cheat,
                    cmd,
                    ctx,
                    economy,
                    sleeveConfig,
                    protectOffTurnThird: false);
                Assert.True(prep.IsReady, prep.Message);
                RoundTransition installed = round.Install(prep.Change!.RoundChange);
                Assert.True(installed.IsAccepted && installed.State != null);
                RoundStateMachine nextRound = installed.State!;
                CheatState nextCheat = prep.Change.CheatState;
                StateVersion commandVersion = evaluated.Next();

                var records = new List<TableEventRecord>(installed.Events.Count + prep.Change.Events.Count);
                int ordinal = 0;
                foreach (RoundEvent ev in installed.Events)
                {
                    records.Add(new TableEventRecord(
                        new EventSeq(nextSeq++), commandVersion, ordinal, ev.Type, ev.Seat, ev.Tile, ev.Detail,
                        1, TableEventFactCodec.Encode(commandVersion, ordinal, nextRound.SourceFingerprint)));
                    ordinal++;
                }
                foreach (RoundCheatEvent cev in prep.Change.Events)
                {
                    records.Add(new TableEventRecord(
                        new EventSeq(nextSeq++), commandVersion, ordinal, RoundEventType.CheatApplied, cev.Value.Seat, null, null,
                        1, TableEventFactCodec.Encode(commandVersion, ordinal, nextRound.SourceFingerprint),
                        new TableCheatEventRecord(
                            cev.Value, cev.OfferId, cev.Keep, cev.Action, cev.ResultTile, cev.HandTile, cev.TargetSlot, cev.TurnNumber)));
                    ordinal++;
                }

                allHistory.AddRange(records);
                byte[] cmdPayload = TableCheatCommandCodec.Encode(new RoundCheatJournal(
                    cmd, turnNumber, nowMillis, cheat.TileMarks.DealtRoundOrdinal, Mahjong.Domain.Cheat.SleeveBloomDrawKind.Normal, DurableHarness.Boot));
                TablePayloadSha256 cmdDigest = TablePayloadSha256.Compute(cmdPayload);
                EventSeq lastEventSeq = records[records.Count - 1].Seq;
                var journal = new TableCommandJournalRecord(
                    commandVersion, evaluated, new RequestId(requestId), DurableHarness.Account, h.Scope.ScopeId,
                    "CHEAT", cmdDigest, cmdPayload, 2);
                ulong nowUtc = unchecked((ulong)h.Clock.Now);
                var receipt = new TableDurableReceipt(
                    DurableHarness.Account, h.Scope.ScopeId, new RequestId(requestId), "CHEAT", cmdDigest,
                    Mahjong.Contracts.Receipt.Applied(evaluated, commandVersion, lastEventSeq, null, nowUtc),
                    string.Empty, nowUtc, nowUtc + 3_600_000UL);
                byte[] nextStatePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(nextRound));

                TableProjectionContextData captured = h.ProjectionSource!.Capture(new TableProjectionContextRequest(
                    DurableHarness.Room,
                    nextRound,
                    commandVersion,
                    lastEventSeq,
                    new OwnerEpoch(1),
                    currentCp.ControlVersion,
                    0L,
                    h.Clock.Now,
                    h.Clock.Now,
                    allHistory,
                    null))!;
                var mergeCheatFacts = typeof(TableActor).GetMethod(
                    "MergeCheatFacts",
                    BindingFlags.NonPublic | BindingFlags.Static)!;
                var mergedEventFacts = (IReadOnlyList<TableProjectionEventFact>)mergeCheatFacts.Invoke(
                    null,
                    new object[] { captured.EventFacts, allHistory })!;
                var updatedFacts = new TableProjectionContextData(
                    captured.RoundId, captured.StateVersion, captured.SessionVersion, nextCheat,
                    captured.PublicFacts, captured.PrivateFacts, captured.Session, captured.Policy,
                    mergedEventFacts, captured.ConcealedMeldFacts);
                byte[] factsPayload = TableProjectionFactsCodec.Encode(updatedFacts);
                TablePayloadSha256 factsHash = TablePayloadSha256.Compute(factsPayload);

                TableDurableCommitResult commitResult = await h.Store.CommitAsync(
                    new TableDurableCommitRequest(
                        DurableHarness.Room, DurableHarness.Owner, new OwnerEpoch(1), evaluated,
                        receipt, internalCommand: false, events: records, commandJournal: journal, outbox: Array.Empty<TableSettlementOutboxRecord>(),
                        checkpoint: null, nextStatePayload: nextStatePayload, nextStateFingerprint: nextRound.SourceFingerprint,
                        terminal: false, timing: timing, timingVersion: ++timingVersion,
                        committedFactsPayload: factsPayload, committedFactsPayloadSha256: factsHash,
                        nextCheatVersion: nextCheat.Version, nextCheatFingerprint: nextCheat.Fingerprint),
                    ct);
                Assert.True(commitResult.Disposition == TableCommitDisposition.Committed, commitResult.ReasonCode + " " + commitResult.Detail);

                round = nextRound;
                cheat = nextCheat;
                evaluated = commandVersion;
            }

            var actCmd = new RoundCheatCommand(
                round.RoundId, 1, RoundCheatAction.SubmitBeamSwap, "leg-beam", RoundCheatPayload.BeamSwap());
            await CommitStepAsync(actCmd, "leg-beam", h.Clock.Now);

            BeamSwapOffer oldBeam = cheat.BeamSwap.Offer!;
            BeamSwapCandidate oldCandidate = Assert.Single(oldBeam.Candidates, c => c.Tile.Equals(thirdTile));
            var swapCmd = new RoundCheatCommand(
                round.RoundId, 1, RoundCheatAction.SubmitBeamSwapChoice, "leg-beam-swap",
                RoundCheatPayload.BeamSwapChoice(oldBeam.OfferId, BeamSwapAction.Swap, oldBeam.EligibleHandTileIds[0], oldCandidate.Slot));
            await CommitStepAsync(swapCmd, "leg-beam-swap", h.Clock.Now);
        }

        private static void RewriteLastDrawEventAndState(
            InMemoryTableStore store,
            RoundStateMachine stateSnapshot,
            TileId drawnTile)
        {
            var eventsField = typeof(InMemoryTableStore).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var eventsMap = (Dictionary<string, List<TableEventRecord>>)eventsField.GetValue(store)!;
            List<TableEventRecord> rows = eventsMap[DurableHarness.Room];
            int idx = rows.FindLastIndex(row => row.Type == RoundEventType.TileDrawn && row.Seat == stateSnapshot.CurrentSeat);
            Assert.True(idx >= 0);
            TableEventRecord drawRow = rows[idx];
            byte[] factPayload = TableEventFactCodec.Encode(
                drawRow.CommandVersion,
                drawRow.Ordinal,
                stateSnapshot.SourceFingerprint);
            rows[idx] = new TableEventRecord(
                drawRow.Seq, drawRow.CommandVersion, drawRow.Ordinal, drawRow.Type, drawRow.Seat, drawnTile, drawRow.Detail,
                drawRow.EventSchema, factPayload, drawRow.Cheat);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].CommandVersion == drawRow.CommandVersion && i != idx)
                {
                    TableEventRecord sibling = rows[i];
                    rows[i] = new TableEventRecord(
                        sibling.Seq, sibling.CommandVersion, sibling.Ordinal, sibling.Type, sibling.Seat,
                        sibling.Tile, sibling.Detail, sibling.EventSchema,
                        TableEventFactCodec.Encode(sibling.CommandVersion, sibling.Ordinal, stateSnapshot.SourceFingerprint),
                        sibling.Cheat);
                }
            }

            var statesField = typeof(InMemoryTableStore).GetField("_states", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object statesMap = statesField.GetValue(store)!;
            var indexer = statesMap.GetType().GetProperty("Item")!;
            object stateRow = indexer.GetValue(statesMap, new object[] { DurableHarness.Room })!;
            Type rowType = stateRow.GetType();
            byte[] serializedRound = RoundRecoveryCodec.Encode(RoundRecovery.Capture(stateSnapshot));
            rowType.GetProperty("StateFingerprint")!.SetValue(stateRow, stateSnapshot.SourceFingerprint);
            rowType.GetProperty("StateHash")!.SetValue(stateRow, TablePayloadSha256.Compute(serializedRound));
        }

        private static void RewriteStoredEvent(
            InMemoryTableStore store,
            EventSeq seq,
            Func<TableEventRecord, TableEventRecord> rewrite)
        {
            var events = (Dictionary<string, List<TableEventRecord>>)typeof(InMemoryTableStore)
                .GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(store)!;
            List<TableEventRecord> rows = events[DurableHarness.Room];
            int index = rows.FindIndex(row => row.Seq.Equals(seq));
            Assert.True(index >= 0);
            rows[index] = rewrite(rows[index]);
        }

        private static int CountActorDrawTurns(RoundStateMachine round)
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

        private static string FindOctopusRequest(string roundId, int seat, int turnNumber, bool hit)
        {
            for (int i = 0; i < 10_000; i++)
            {
                string request = "octopus-actor-" + seat + "-" + turnNumber + "-" + i;
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

            throw new InvalidOperationException("Could not find a deterministic SleeveBloom hit/miss request.");
        }

        private static ActorHarness CreateHarness(int cheatRequestBindingCapacity = 8192)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer,
                    SeatControllerKind.Computer),
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                CheatRequestBindingCapacity = cheatRequestBindingCapacity,
                InitialCheatState = cheat
            };
            var actor = new TableActor(options, round, commitPort, clock, sink);
            return new ActorHarness(actor, clock, commitPort, sink, round);
        }

        private static async Task DiscardOnceAsync(ActorHarness harness, string requestId)
        {
            IReadOnlyList<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 1);
            var envelope = new TableCommandEnvelope(
                chain[0].Command,
                new RequestId(requestId),
                chain[0].ExpectedStateVersion,
                new OwnerEpoch(1),
                new ControlEpoch(0));
            TableOperationResult result = await harness.SubmitAsync(envelope);
            Assert.Equal(TableOperationStatus.Accepted, result.Status);
        }

        private static TableCheatCommandEnvelope Cheat(
            string roundId,
            int seat,
            RoundCheatAction action,
            string requestId,
            RoundCheatPayload payload,
            int expectedVersion,
            string? digestRequest = null,
            ulong owner = 1UL,
            ulong control = 0UL,
            long recovery = 0L,
            DecisionId? decision = null,
            string? digest = null)
        {
            TablePayloadSha256? sha = null;
            if (digest != null || digestRequest != null)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                    (digest ?? "payload") + "|" + (digestRequest ?? requestId) + "|" + action + "|" + seat);
                sha = TablePayloadSha256.Compute(bytes);
            }

            return new TableCheatCommandEnvelope(
                new RoundCheatCommand(roundId, seat, action, requestId, payload),
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(owner),
                new ControlEpoch(control),
                decision,
                recovery,
                sha.HasValue ? new RequestScopeId("scope-1") : (RequestScopeId?)null,
                sha.HasValue ? "CHEAT" : null,
                sha);
        }
    }
}
