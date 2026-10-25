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

        private static async Task<DurableHarness> CreateDurableCheatHarnessAsync()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            return await DurableHarness.CreateAsync(
                ledger: cheat.Ledger,
                initialCheckpoint: WithCheat(DurableHarness.InitialCheckpoint(round, cheat.Ledger), cheat),
                configure: options =>
                {
                    options.CheatEconomy = V32Fixtures.Economy(InitialPoints);
                    options.SleeveBloomConfig = V32Fixtures.SleeveConfig();
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

        private static ActorHarness CreateHarness()
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
