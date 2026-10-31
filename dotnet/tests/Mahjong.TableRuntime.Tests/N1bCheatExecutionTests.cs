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
