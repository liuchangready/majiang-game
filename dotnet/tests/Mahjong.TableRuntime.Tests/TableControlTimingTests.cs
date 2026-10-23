using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableControlTimingTests
    {
        [Fact]
        public async Task PlayerAtDeadlineIsRejectedAndTheFollowingTickCommitsTimeout()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            harness.Clock.Now = 2000L;
            TableCommandEnvelope command = P31Fixtures.Command(
                P31Fixtures.DiscardCurrent(harness.InitialRound),
                0,
                "late-player",
                new DecisionId("d1"),
                0UL);
            TableOperationResult rejected = await harness.SubmitAsync(command);

            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(TableRuntimeReasonCode.DeadlineExpired, rejected.ReasonCode);
            Assert.Empty(harness.CommitPort.Requests);
            Assert.Empty(harness.Sink.Publications);

            TableTickResult tick = await harness.TickAsync();
            Assert.Single(tick.Entries);
            Assert.Equal(TickEntryKind.Timeout, tick.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, tick.Entries[0].Transition!.Status);
            Assert.Equal(1UL, tick.FinalStateVersion.Value);
            TableEventRecord discard = tick.Entries[0].Transition!.Events
                .Single(record => record.Type == RoundEventType.TileDiscarded);
            Assert.Equal(decision.Fallback.Tile!.Value, discard.Tile);
        }

        [Fact]
        public async Task TickQueuedBeforeLatePlayerCommitsTimeoutAndThePlayerCannotReviveTheDecision()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            harness.Clock.Now = 2000L;
            TableTickResult tick = await harness.TickAsync();
            Assert.Equal(TickEntryKind.Timeout, tick.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, tick.Entries[0].Transition!.Status);
            Assert.Equal(1UL, tick.FinalStateVersion.Value);

            TableCommandEnvelope late = P31Fixtures.Command(
                P31Fixtures.DiscardCurrent(harness.InitialRound),
                0,
                "late-after-tick",
                new DecisionId("d1"),
                0UL);
            TableOperationResult rejected = await harness.SubmitAsync(late);
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(TableRuntimeReasonCode.DecisionMismatch, rejected.ReasonCode);
            Assert.Single(harness.CommitPort.Requests);
            Assert.Equal(1UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Theory]
        [InlineData(DisconnectMode.ImmediateAi, SeatControllerKind.Takeover, 1L, false)]
        [InlineData(DisconnectMode.GraceThenAi, SeatControllerKind.LocalHuman, 0L, true)]
        [InlineData(DisconnectMode.TimeoutOnly, SeatControllerKind.LocalHuman, 0L, false)]
        public async Task DisconnectPolicyControlsGraceAndImmediateTakeover(
            DisconnectMode mode,
            SeatControllerKind expectedKind,
            long expectedEpoch,
            bool expectGrace)
        {
            var policy = new DisconnectPolicy(mode, 10000L, "ai-profile");
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                disconnectPolicy: policy);

            TableControlResult result = await harness.DisconnectAsync(0, 0UL);

            Assert.True(result.IsChanged);
            Assert.Equal(TableControlChangeKind.Disconnect, result.Change!.Kind);
            Assert.Equal(1L, result.ControlVersion);
            TableSeatControlView seat = result.Snapshot!.Seats[0];
            Assert.Equal(ConnectionStatus.Disconnected, seat.Status);
            Assert.Equal(expectedKind, seat.Kind);
            Assert.Equal(expectedEpoch, seat.ControlEpoch);
            if (expectGrace)
            {
                Assert.Equal(11000L, seat.GraceDeadlineMillis);
                Assert.Equal(1000L, seat.DisconnectedAtMillis);
                Assert.Equal("ai-profile", seat.AiProfileId);
            }
            else
            {
                Assert.Null(seat.GraceDeadlineMillis);
            }

            Assert.Single(harness.Sink.Publications);
            Assert.Equal(TablePublicationKind.ControlChanged, harness.Sink.Publications[0].Kind);
        }

        [Fact]
        public async Task DefaultRuleClockAndDisconnectGraceUseIndependentDefaults()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                decisionClockConfig: new DecisionClockConfig());

            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            Assert.Equal(1000L + 3000L + 20000L, timing.Seats[0].Timing!.DeadlineMillis);

            TableControlResult disconnected = await harness.DisconnectAsync(0, 0UL);
            Assert.Equal(1000L + 10000L, disconnected.Snapshot!.Seats[0].GraceDeadlineMillis);
            Assert.Equal(24000L, timing.Seats[0].Timing!.DeadlineMillis);
        }

        [Fact]
        public async Task GraceDeadlineAndRuleDeadlineAreIndependent()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                disconnectPolicy: new DisconnectPolicy(DisconnectMode.GraceThenAi, 10000L));
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            TableControlResult disconnected = await harness.DisconnectAsync(0, 0UL);
            Assert.Equal(11000L, disconnected.Snapshot!.Seats[0].GraceDeadlineMillis);

            // The 2,000 ms rule deadline still expires while the 11,000 ms disconnect grace
            // is untouched.
            harness.Clock.Now = 2000L;
            TableTickResult tick = await harness.TickAsync();
            Assert.Equal(TickEntryKind.Timeout, tick.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, tick.Entries[0].Transition!.Status);

            TableControlSnapshot control = await harness.GetControlSnapshotAsync();
            Assert.Equal(ConnectionStatus.Disconnected, control.Seats[0].Status);
            Assert.Equal(SeatControllerKind.LocalHuman, control.Seats[0].Kind);
            Assert.Equal(11000L, control.Seats[0].GraceDeadlineMillis);
            Assert.Equal(0L, control.Seats[0].ControlEpoch);

            harness.Clock.Now = 10999L;
            TableGraceExpiryFence early = await FenceAsync(harness, 0, 11000L);
            TableControlResult notDue = await harness.ExpireGraceAsync(early);
            Assert.Equal(TableControlStatus.Rejected, notDue.Status);
            Assert.Equal(TableRuntimeReasonCode.GraceNotDue, notDue.ReasonCode);
            Assert.Equal(0L, (await harness.GetControlSnapshotAsync()).Seats[0].ControlEpoch);

            harness.Clock.Now = 11000L;
            TableGraceExpiryFence due = await FenceAsync(harness, 0, 11000L);
            TableControlResult taken = await harness.ExpireGraceAsync(due);
            Assert.True(taken.IsChanged);
            Assert.Equal(TableControlChangeKind.GraceExpired, taken.Change!.Kind);
            Assert.Equal(SeatControllerKind.Takeover, taken.Snapshot!.Seats[0].Kind);
            Assert.Equal(1L, taken.Snapshot.Seats[0].ControlEpoch);
        }

        [Fact]
        public async Task LateGraceCallbackCannotTakeOverAfterExplicitTakeover()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                disconnectPolicy: new DisconnectPolicy(DisconnectMode.GraceThenAi, 10000L));

            TableControlResult disconnected = await harness.DisconnectAsync(0, 0UL);
            Assert.Equal(11000L, disconnected.Snapshot!.Seats[0].GraceDeadlineMillis);
            TableGraceExpiryFence staleFence = await FenceAsync(harness, 0, 11000L);

            harness.Clock.Now = 10500L;
            TableControlResult takeover = await harness.RequestTakeoverAsync(0, 0UL);
            Assert.True(takeover.IsChanged);
            Assert.Equal(1L, takeover.Snapshot!.Seats[0].ControlEpoch);

            harness.Clock.Now = 11000L;
            TableControlResult late = await harness.ExpireGraceAsync(staleFence);
            Assert.Equal(TableControlStatus.Rejected, late.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, late.ReasonCode);
            Assert.Equal(1L, (await harness.GetControlSnapshotAsync()).Seats[0].ControlEpoch);

            var wrongRound = new TableGraceExpiryFence(
                0,
                "other-round",
                new OwnerEpoch(1),
                new ControlEpoch(1),
                11000L,
                staleFence.ExpectedRecoveryGeneration);
            TableControlResult wrongRoundResult = await harness.ExpireGraceAsync(wrongRound);
            Assert.Equal(TableRuntimeReasonCode.StaleGrace, wrongRoundResult.ReasonCode);
        }

        [Fact]
        public async Task ReconnectNeverReclaimsButExplicitReclaimInvalidatesOldAiAndPlayerEpochs()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                disconnectPolicy: new DisconnectPolicy(DisconnectMode.ImmediateAi));
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            DiscardDecisionFixture staleAi = P31Fixtures.BuildDiscardDecision(harness, controlEpoch: 0UL);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            TableControlResult disconnected = await harness.DisconnectAsync(0, 0UL);
            Assert.Equal(SeatControllerKind.Takeover, disconnected.Snapshot!.Seats[0].Kind);
            Assert.Equal(1L, disconnected.Snapshot.Seats[0].ControlEpoch);

            harness.Clock.Now = 1100L;
            TableTickResult staleResult = await harness.CompleteAiAsync(staleAi.Ai);
            Assert.Single(staleResult.Entries);
            Assert.Equal(TickRejectionCode.StaleEpoch, staleResult.Entries[0].RejectionCode);
            Assert.Equal(0UL, staleResult.FinalStateVersion.Value);

            harness.Clock.Now = 1200L;
            TableControlResult reconnected = await harness.ReconnectAsync(0, 1UL);
            Assert.True(reconnected.IsChanged);
            Assert.Equal(ConnectionStatus.Connected, reconnected.Snapshot!.Seats[0].Status);
            Assert.Equal(SeatControllerKind.Takeover, reconnected.Snapshot.Seats[0].Kind);
            Assert.Equal(1L, reconnected.Snapshot.Seats[0].ControlEpoch);

            DiscardDecisionFixture takeoverAi = P31Fixtures.BuildDiscardDecision(harness, controlEpoch: 1UL);
            harness.Clock.Now = 1400L;
            TableControlResult reclaimed = await harness.ReclaimControlAsync(0, 1UL);
            Assert.True(reclaimed.IsChanged);
            Assert.Equal(SeatControllerKind.LocalHuman, reclaimed.Snapshot!.Seats[0].Kind);
            Assert.Equal(2L, reclaimed.Snapshot.Seats[0].ControlEpoch);

            harness.Clock.Now = 1500L;
            TableTickResult oldTakeoverAi = await harness.CompleteAiAsync(takeoverAi.Ai);
            Assert.Single(oldTakeoverAi.Entries);
            Assert.Equal(TickEntryKind.AiRejected, oldTakeoverAi.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.StaleEpoch, oldTakeoverAi.Entries[0].RejectionCode);
            Assert.Equal(0UL, oldTakeoverAi.FinalStateVersion.Value);
            Assert.Empty(harness.CommitPort.Requests);

            TableCommandEnvelope oldEpochPlayer = P31Fixtures.Command(
                RoundCommand.Discard(harness.InitialRound.RoundId, 0, decision.Chosen.Tile!.Value),
                0,
                "old-epoch-player",
                new DecisionId("d1"),
                1UL);
            TableOperationResult oldPlayer = await harness.SubmitAsync(oldEpochPlayer);
            Assert.Equal(TableOperationStatus.Rejected, oldPlayer.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, oldPlayer.ReasonCode);
            Assert.Empty(harness.CommitPort.Requests);

            TableCommandEnvelope player = P31Fixtures.Command(
                RoundCommand.Discard(harness.InitialRound.RoundId, 0, decision.Chosen.Tile!.Value),
                0,
                "reclaimed-player",
                new DecisionId("d1"),
                2UL);
            TableOperationResult accepted = await harness.SubmitAsync(player);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(1UL, accepted.CommandVersion!.Value.Value);
        }

        [Fact]
        public async Task RecoveryRefusesTickAiControlAndOldFencesThenInstallsNewControlBaseline()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture oldAi = P31Fixtures.BuildDiscardDecision(harness, controlEpoch: 0UL);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(oldAi.Pending)).Status);

            harness.Clock.Now = 1100L;
            TableOperationResult began = await harness.BeginRecoveryAsync("cross-round recovery");
            Assert.Equal(TableOperationStatus.Configured, began.Status);

            TableTickResult tick = await harness.TickAsync();
            Assert.Equal(TableOperationStatus.Rejected, tick.Status);
            Assert.Equal(TableRuntimeReasonCode.Recovering, tick.ReasonCode);
            TableTickResult ai = await harness.CompleteAiAsync(oldAi.Ai);
            Assert.Equal(TableOperationStatus.Rejected, ai.Status);
            Assert.Equal(TableRuntimeReasonCode.Recovering, ai.ReasonCode);
            TableAiObservationResult observation = await harness.CaptureAiObservationAsync(0);
            Assert.Equal(TableAiObservationStatus.Rejected, observation.Status);
            Assert.Equal(TableRuntimeReasonCode.Recovering, observation.ReasonCode);

            TableControlResult disconnect = await harness.DisconnectAsync(0, 0UL);
            Assert.Equal(TableControlStatus.Rejected, disconnect.Status);
            Assert.Equal(TableRuntimeReasonCode.Recovering, disconnect.ReasonCode);
            Assert.Equal(TableControlStatus.Rejected, (await harness.RequestTakeoverAsync(0, 0UL)).Status);
            Assert.Equal(TableControlStatus.Rejected, (await harness.ReclaimControlAsync(0, 0UL)).Status);
            Assert.Equal(
                TableControlStatus.Rejected,
                (await harness.ConfigureControlPolicyAsync(new DisconnectPolicy(DisconnectMode.TimeoutOnly))).Status);
            Assert.Empty(harness.CommitPort.Requests);

            RoundStateMachine round2 = P31Fixtures.Dealt("round-2");
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                P31Fixtures.Connected(SeatControllerKind.Computer, 5L, 0),
                P31Fixtures.Connected(SeatControllerKind.Computer, 5L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 5L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 5L, 3));
            var baseline = new TableRecoveryBaseline(
                round2,
                new StateVersion(10UL),
                new EventSeq(50UL),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(control, 4L),
                new TableRecoveryTiming(null, new long[] { 0L, 0L, 0L, 0L }));
            harness.CommitPort.SetDurableBaseline(new StateVersion(10UL), new EventSeq(50UL));

            TableOperationResult completed = await harness.CompleteRecoveryAsync(baseline);
            Assert.True(
                completed.Status == TableOperationStatus.Configured,
                completed.ReasonCode + ": " + completed.Detail);
            TableControlSnapshot snapshot = await harness.GetControlSnapshotAsync();
            Assert.Equal("round-2", snapshot.RoundId);
            Assert.Equal(4L, snapshot.ControlVersion);
            Assert.Equal(5L, snapshot.Seats[0].ControlEpoch);
            Assert.Equal(1L, snapshot.RecoveryGeneration);

            TableTickResult oldRoundAi = await harness.CompleteAiAsync(oldAi.Ai);
            Assert.Single(oldRoundAi.Entries);
            Assert.Equal(TickRejectionCode.StaleRound, oldRoundAi.Entries[0].RejectionCode);
            Assert.Equal(10UL, oldRoundAi.FinalStateVersion.Value);
            Assert.Null(oldRoundAi.Entries[0].Transition);

            TableControlResult staleControl = await harness.ReclaimControlAsync(0, 0UL);
            Assert.Equal(TableControlStatus.Rejected, staleControl.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, staleControl.ReasonCode);

            var oldGenerationFence = new TableGraceExpiryFence(
                0,
                "round-2",
                new OwnerEpoch(1),
                new ControlEpoch(5),
                11000L,
                0L);
            TableControlResult oldGrace = await harness.ExpireGraceAsync(oldGenerationFence);
            Assert.Equal(TableControlStatus.Rejected, oldGrace.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleGrace, oldGrace.ReasonCode);

            var oldRoundFence = new TableGraceExpiryFence(
                0,
                "round-1",
                new OwnerEpoch(1),
                new ControlEpoch(5),
                11000L,
                snapshot.RecoveryGeneration);
            Assert.Equal(
                TableRuntimeReasonCode.StaleGrace,
                (await harness.ExpireGraceAsync(oldRoundFence)).ReasonCode);

            TableCommandEnvelope newEpochCommand = P31Fixtures.Command(
                P31Fixtures.DiscardCurrent(round2),
                10,
                "after-recovery",
                null,
                5UL,
                1UL,
                snapshot.RecoveryGeneration);
            TableOperationResult accepted = await harness.SubmitAsync(newEpochCommand);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(11UL, accepted.CommandVersion!.Value.Value);
        }

        [Fact]
        public async Task RecoveryRestoresExplicitTimingAndControlEpochWithoutUtcArithmetic()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.Takeover),
                initialNow: 1000L);
            RoundStateMachine round2 = P31Fixtures.Dealt("round-2");
            TileId tile = round2.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            var recovered = new TableRecoveredDecision(
                new DecisionKey("round-2", 0, "recovered-d1"),
                7L,
                new[] { new LegalActionOption(LegalActionKind.Discard, 0, tile) },
                tile,
                1000L,
                0L,
                500L);
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                P31Fixtures.Connected(SeatControllerKind.Takeover, 7L, 0),
                P31Fixtures.Connected(SeatControllerKind.Computer, 7L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 7L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 7L, 3));

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("explicit timing recovery")).Status);

            harness.Clock.Now = 5000L;
            var baseline = new TableRecoveryBaseline(
                round2,
                new StateVersion(10UL),
                new EventSeq(50UL),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(control, 4L),
                new TableRecoveryTiming(new[] { recovered }, new long[] { 0L, 0L, 0L, 0L }));
            TableOperationResult completed = await harness.CompleteRecoveryAsync(baseline);
            Assert.True(
                completed.Status == TableOperationStatus.Configured,
                completed.ReasonCode + ": " + completed.Detail);

            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            TableSeatTimingView seat0 = timing.Seats[0];
            Assert.NotNull(seat0.Timing);
            Assert.Equal("recovered-d1", seat0.PendingKey!.Value.DecisionId);
            Assert.Equal(1L, timing.RecoveryGeneration);
            Assert.Equal(5500L, seat0.Timing!.DeadlineMillis);
            Assert.Equal(0L, seat0.RemainingReserveMillis);
            Assert.Equal(500L, seat0.Timing.DeadlineMillis - 5000L);

            TableAiObservationResult captured = await harness.CaptureAiObservationAsync(0);
            Assert.True(captured.IsCaptured);
            Assert.Equal(7L, captured.Observation!.ControlEpoch);
            Assert.Equal(5500L, captured.Observation.DecisionDeadlineMillis);
        }

        [Fact]
        public async Task RecoveryTimingWithWrongEpochIsRejectedWithoutInstallingAnything()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.Takeover));
            RoundStateMachine round2 = P31Fixtures.Dealt("round-2");
            TileId tile = round2.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                P31Fixtures.Connected(SeatControllerKind.Takeover, 7L, 0),
                P31Fixtures.Connected(SeatControllerKind.Computer, 7L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 7L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 7L, 3));
            var mismatched = new TableRecoveredDecision(
                new DecisionKey("round-2", 0, "d1"),
                6L,
                new[] { new LegalActionOption(LegalActionKind.Discard, 0, tile) },
                tile,
                1000L,
                0L,
                0L);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("bad timing")).Status);

            harness.Clock.Now = 4000L;
            var baseline = new TableRecoveryBaseline(
                round2,
                new StateVersion(10UL),
                new EventSeq(50UL),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(control, 4L),
                new TableRecoveryTiming(new[] { mismatched }));
            TableOperationResult completed = await harness.CompleteRecoveryAsync(baseline);

            Assert.Equal(TableOperationStatus.Rejected, completed.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryTimingInvalid, completed.ReasonCode);
            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(TableActorLifecycle.Recovering, snapshot.Lifecycle);
            Assert.Equal("round-1", snapshot.RoundId);
        }

        [Fact]
        public async Task RecoveryControlCannotChangeControllerKindWithoutBumpingEpoch()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            RoundStateMachine round2 = P31Fixtures.Dealt("round-2");
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                P31Fixtures.Connected(SeatControllerKind.Takeover, 0L, 0),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 3));
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("unsafe control baseline")).Status);

            var baseline = new TableRecoveryBaseline(
                round2,
                new StateVersion(10UL),
                new EventSeq(50UL),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(control, 1L),
                TableRecoveryTiming.None);
            TableOperationResult completed = await harness.CompleteRecoveryAsync(baseline);

            Assert.Equal(TableOperationStatus.Rejected, completed.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryControlInvalid, completed.ReasonCode);
            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            Assert.Equal(TableActorLifecycle.Recovering, snapshot.Lifecycle);
            Assert.Equal("round-1", snapshot.RoundId);
        }

        [Fact]
        public async Task ControlCommitFailureLeavesAuthorityUntouchedAndRetrySucceeds()
        {
            var controlPort = new RecordingControlCommitPort();
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                controlCommitPort: controlPort);

            controlPort.FailNext();
            TableControlResult failed = await harness.DisconnectAsync(0, 0UL);
            Assert.Equal(TableControlStatus.Failed, failed.Status);
            Assert.Equal(TableRuntimeReasonCode.ControlCommitFailed, failed.ReasonCode);
            TableControlSnapshot afterFailure = await harness.GetControlSnapshotAsync();
            Assert.Equal(ConnectionStatus.Connected, afterFailure.Seats[0].Status);
            Assert.Equal(0L, afterFailure.Seats[0].ControlEpoch);
            Assert.Equal(0L, afterFailure.ControlVersion);
            Assert.Empty(harness.Sink.Publications);

            TableControlResult retried = await harness.DisconnectAsync(0, 0UL);
            Assert.True(retried.IsChanged);
            Assert.Equal(1L, retried.ControlVersion);
            Assert.Equal(2, controlPort.Requests.Count);
            Assert.Equal(0L, controlPort.Requests[0].ExpectedControlVersion);
            Assert.Equal(1L, controlPort.Requests[0].ControlVersion);
            Assert.Equal(0L, controlPort.Requests[1].ExpectedControlVersion);
            Assert.Equal(1L, controlPort.Requests[1].ControlVersion);
        }

        [Fact]
        public async Task SlowControlCommitQueuesLaterControlMessagesWithoutParallelMutation()
        {
            var controlPort = new RecordingControlCommitPort();
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                disconnectPolicy: new DisconnectPolicy(DisconnectMode.ImmediateAi),
                controlCommitPort: controlPort);

            controlPort.BlockNext();
            Task<TableControlResult> disconnect = harness.DisconnectAsync(0, 0UL).AsTask();
            await controlPort.Entered;
            Task<TableControlResult> reconnect = harness.ReconnectAsync(0, 1UL).AsTask();

            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(reconnect.IsCompleted, "a second control message must wait for the in-flight control commit");
            Assert.Equal(1, controlPort.MaxConcurrentCommits);

            controlPort.Release();
            TableControlResult disconnected = await disconnect;
            TableControlResult reconnected = await reconnect;

            Assert.True(disconnected.IsChanged);
            Assert.Equal(1L, disconnected.ControlVersion);
            Assert.True(reconnected.IsChanged);
            Assert.Equal(2L, reconnected.ControlVersion);
            Assert.Equal(SeatControllerKind.Takeover, reconnected.Snapshot!.Seats[0].Kind);
            Assert.Equal(1L, reconnected.Snapshot.Seats[0].ControlEpoch);
            Assert.Equal(1, controlPort.MaxConcurrentCommits);
        }

        [Fact]
        public async Task ControlMessageWaitsBehindAnInFlightPlayerCommit()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman));
            List<TableCommandEnvelope> chain = P31Fixtures.BuildDiscardChain(harness.InitialRound, 1);

            harness.CommitPort.BlockNext();
            Task<TableOperationResult> player = harness.SubmitAsync(chain[0]).AsTask();
            await harness.CommitPort.Entered;
            Task<TableControlResult> disconnect = harness.DisconnectAsync(0, 0UL).AsTask();

            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(disconnect.IsCompleted);

            harness.CommitPort.Release();
            TableOperationResult accepted = await player;
            TableControlResult control = await disconnect;

            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(1UL, accepted.CommandVersion!.Value.Value);
            Assert.True(control.IsChanged);
            Assert.Equal(ConnectionStatus.Disconnected, control.Snapshot!.Seats[0].Status);
        }

        [Fact]
        public async Task LeaseLossRefusesNewControlTickAndAiWithoutAdvancingAnything()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture oldAi = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(oldAi.Pending)).Status);

            TableOperationResult lease = await harness.ReportLeaseLostAsync(new OwnerEpoch(1), "test lease loss");
            Assert.Equal(TableOperationStatus.Configured, lease.Status);

            TableTickResult tick = await harness.TickAsync();
            Assert.Equal(TableOperationStatus.Rejected, tick.Status);
            Assert.Equal(TableRuntimeReasonCode.LeaseLost, tick.ReasonCode);
            TableTickResult ai = await harness.CompleteAiAsync(oldAi.Ai);
            Assert.Equal(TableOperationStatus.Rejected, ai.Status);
            Assert.Equal(TableRuntimeReasonCode.LeaseLost, ai.ReasonCode);
            TableControlResult control = await harness.DisconnectAsync(0, 0UL);
            Assert.Equal(TableControlStatus.Rejected, control.Status);
            Assert.Equal(TableRuntimeReasonCode.LeaseLost, control.ReasonCode);
            Assert.Empty(harness.CommitPort.Requests);
            Assert.Equal(0UL, (await harness.GetSnapshotAsync()).StateVersion.Value);
        }

        [Fact]
        public async Task ReclaimAndAiAreSerializedAndReclaimMakesTheTakeoverAiStale()
        {
            var controlPort = new RecordingControlCommitPort();
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                disconnectPolicy: new DisconnectPolicy(DisconnectMode.ImmediateAi),
                controlCommitPort: controlPort);
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            TableControlResult disconnected = await harness.DisconnectAsync(0, 0UL);
            Assert.Equal(1L, disconnected.Snapshot!.Seats[0].ControlEpoch);
            DiscardDecisionFixture takeoverAi = P31Fixtures.BuildDiscardDecision(harness, controlEpoch: 1UL);

            controlPort.BlockNext();
            Task<TableControlResult> reclaim = harness.ReclaimControlAsync(0, 1UL).AsTask();
            await controlPort.Entered;
            Task<TableTickResult> ai = harness.CompleteAiAsync(takeoverAi.Ai).AsTask();

            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(ai.IsCompleted);

            controlPort.Release();
            TableControlResult reclaimed = await reclaim;
            TableTickResult aiResult = await ai;

            Assert.True(reclaimed.IsChanged);
            Assert.Equal(SeatControllerKind.LocalHuman, reclaimed.Snapshot!.Seats[0].Kind);
            Assert.Equal(2L, reclaimed.Snapshot.Seats[0].ControlEpoch);
            Assert.Single(aiResult.Entries);
            Assert.Equal(TickEntryKind.AiRejected, aiResult.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.StaleEpoch, aiResult.Entries[0].RejectionCode);
            Assert.Empty(harness.CommitPort.Requests);
        }

        [Fact]
        public async Task MultipleSeatResponseWindowAcceptsOneAiPerSeatAndRejectsTheSameSeatTwice()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            RoundStateMachine round = harness.InitialRound;
            int discarder = round.CurrentSeat;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, discarder)[0];
            TableCommandEnvelope opening = P31Fixtures.Command(
                RoundCommand.Discard(round.RoundId, discarder, tile, new[] { 1, 2, 3 }),
                0,
                "open-window");
            Assert.Equal(TableOperationStatus.Accepted, (await harness.SubmitAsync(opening)).Status);

            for (int seat = 1; seat <= 2; seat++)
            {
                var pending = new PendingDecision(
                    new DecisionKey(round.RoundId, seat, "pass-" + seat),
                    0L,
                    new[] { new LegalActionOption(LegalActionKind.Pass, seat) });
                Assert.Equal(
                    TableOperationStatus.Configured,
                    (await harness.OpenDecisionAsync(pending)).Status);
            }

            SingleActionDecisionFixture seat1 = P31Fixtures.BuildSingleActionDecision(
                harness,
                LegalActionKind.Pass,
                "pass-1",
                1);
            SingleActionDecisionFixture seat2 = P31Fixtures.BuildSingleActionDecision(
                harness,
                LegalActionKind.Pass,
                "pass-2",
                2);

            harness.Clock.Now = 1500L;
            TableTickResult first = await harness.CompleteAiAsync(seat1.Ai);
            Assert.Equal(TickEntryKind.AiAccepted, first.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, first.Entries[0].Transition!.Status);
            Assert.Equal(2UL, first.FinalStateVersion.Value);

            TableTickResult duplicate = await harness.CompleteAiAsync(seat1.Ai);
            Assert.Single(duplicate.Entries);
            Assert.Equal(TickEntryKind.AiRejected, duplicate.Entries[0].Kind);
            Assert.Equal(TickRejectionCode.StaleDecision, duplicate.Entries[0].RejectionCode);
            Assert.Equal(2UL, duplicate.FinalStateVersion.Value);

            TableTickResult second = await harness.CompleteAiAsync(seat2.Ai);
            Assert.Equal(TickEntryKind.AiAccepted, second.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, second.Entries[0].Transition!.Status);
            Assert.Equal(3UL, second.FinalStateVersion.Value);
        }

        [Fact]
        public async Task ActorCapturedAiObservationIsReadOnlyAndItsResultIsRevalidated()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);

            TableAiObservationResult captured = await harness.CaptureAiObservationAsync(0);
            Assert.True(captured.IsCaptured);
            AiObservation observation = captured.Observation!;
            Assert.Equal("d1", observation.DecisionId);
            Assert.Equal(0L, observation.ControlEpoch);
            Assert.Equal(2000L, observation.DecisionDeadlineMillis);
            Assert.Equal(observation.MyHandTileIds.Count, harness.InitialRound.Tiles.TilesInZone(TileZone.Hand, 0).Count);
            Assert.Equal(harness.InitialRound.Tiles.WallRemaining, observation.RemainingWallCount);

            var intent = new CommandIntent(observation, decision.Chosen, "test-ai", 1);
            var result = new AiDecision(observation, intent, 0.5d);
            harness.Clock.Now = 1500L;
            TableTickResult tick = await harness.CompleteAiAsync(result);
            Assert.Equal(TickEntryKind.AiAccepted, tick.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, tick.Entries[0].Transition!.Status);
            TableEventRecord discard = tick.Entries[0].Transition!.Events
                .Single(record => record.Type == RoundEventType.TileDiscarded);
            Assert.Equal(decision.Chosen.Tile!.Value, discard.Tile);
        }

        [Fact]
        public async Task MonotonicClockRegressionIsRejectedWithoutMutation()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman));
            TableCommandEnvelope command = P31Fixtures.BuildDiscardChain(harness.InitialRound, 1)[0];

            harness.Clock.Now = 900L;
            TableOperationResult rejected = await harness.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(TableRuntimeReasonCode.ClockRegression, rejected.ReasonCode);
            Assert.Empty(harness.CommitPort.Requests);

            TableTickResult tick = await harness.TickAsync();
            Assert.Equal(TableOperationStatus.Failed, tick.Status);
            Assert.Equal(TableRuntimeReasonCode.ClockRegression, tick.ReasonCode);

            TableControlResult control = await harness.DisconnectAsync(0, 0UL);
            Assert.Equal(TableControlStatus.Failed, control.Status);
            Assert.Equal(TableRuntimeReasonCode.ClockRegression, control.ReasonCode);
            Assert.Equal(ConnectionStatus.Connected, (await harness.GetControlSnapshotAsync()).Seats[0].Status);

            harness.Clock.Now = 1000L;
            TableOperationResult accepted = await harness.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(1UL, accepted.CommandVersion!.Value.Value);
        }

        [Fact]
        public async Task DecisionDeadlineOverflowIsRejectedWithoutOpeningOrConsumingAnything()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(initialNow: long.MaxValue - 500L);
            RoundStateMachine round = harness.InitialRound;
            int seat = round.CurrentSeat;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
            var pending = new PendingDecision(
                new DecisionKey(round.RoundId, seat, "overflow-d1"),
                0L,
                new[] { new LegalActionOption(LegalActionKind.Discard, seat, tile) },
                tile);

            TableOperationResult opened = await harness.OpenDecisionAsync(pending);

            Assert.Equal(TableOperationStatus.Failed, opened.Status);
            Assert.Equal(TableRuntimeReasonCode.DeadlineOverflow, opened.ReasonCode);
            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            Assert.Null(timing.Seats[0].Timing);
            Assert.Null(timing.Seats[0].PendingKey);
            Assert.Empty(harness.CommitPort.Requests);
        }

        [Fact]
        public async Task ControlGraceOverflowIsRejectedWithoutChangingControl()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                disconnectPolicy: new DisconnectPolicy(DisconnectMode.GraceThenAi, 10000L),
                initialNow: long.MaxValue - 5L);

            TableControlResult result = await harness.DisconnectAsync(0, 0UL);

            Assert.Equal(TableControlStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.ControlTimeOverflow, result.ReasonCode);
            TableControlSnapshot snapshot = await harness.GetControlSnapshotAsync();
            Assert.Equal(ConnectionStatus.Connected, snapshot.Seats[0].Status);
            Assert.Equal(0L, snapshot.Seats[0].ControlEpoch);
            Assert.Equal(0L, snapshot.ControlVersion);
        }

        [Fact]
        public async Task PolicyConfigurationChangesOnlyAfterEverySeatReconnects()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                disconnectPolicy: new DisconnectPolicy(DisconnectMode.GraceThenAi, 10000L));

            Assert.True((await harness.DisconnectAsync(0, 0UL)).IsChanged);
            TableControlResult busy = await harness.ConfigureControlPolicyAsync(
                new DisconnectPolicy(DisconnectMode.TimeoutOnly));
            Assert.Equal(TableControlStatus.Rejected, busy.Status);
            Assert.Equal(TableRuntimeReasonCode.ControlPolicyBusy, busy.ReasonCode);

            Assert.True((await harness.ReconnectAsync(0, 0UL)).IsChanged);
            TableControlResult changed = await harness.ConfigureControlPolicyAsync(
                new DisconnectPolicy(DisconnectMode.TimeoutOnly));
            Assert.True(changed.IsChanged);
            Assert.Equal(DisconnectMode.TimeoutOnly, changed.Snapshot!.Policy.Mode);

            TableControlResult disconnected = await harness.DisconnectAsync(0, 0UL);
            Assert.True(disconnected.IsChanged);
            Assert.Null(disconnected.Snapshot!.Seats[0].GraceDeadlineMillis);
            Assert.Equal(SeatControllerKind.LocalHuman, disconnected.Snapshot.Seats[0].Kind);
        }

        [Fact]
        public async Task CrossStateRecoveryRequiresExplicitIdleSeatReservesAndKeepsTheLedger()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                decisionClockConfig: new DecisionClockConfig(100L, 1000L, ReserveScope.Round));
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            harness.Clock.Now = 1500L;
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.CompleteAiAsync(decision.Ai)).Entries[0].Transition!.Status);
            Assert.Equal(600L, (await harness.GetTimingSnapshotAsync()).Seats[0].RemainingReserveMillis);
            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            RoundStateMachine round2 = P31Fixtures.Dealt("round-2");
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 0),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 3));
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("cross-state")).Status);

            var missingReserves = new TableRecoveryBaseline(
                round2,
                snapshot.StateVersion,
                snapshot.LastEventSeq,
                snapshot.OwnerEpoch,
                new TableControlRecoveryBaseline(control, 0L),
                TableRecoveryTiming.None);
            TableOperationResult rejected = await harness.CompleteRecoveryAsync(missingReserves);
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryTimingInvalid, rejected.ReasonCode);
            Assert.Equal(TableActorLifecycle.Recovering, (await harness.GetSnapshotAsync()).Lifecycle);

            var explicitReserves = new TableRecoveryBaseline(
                round2,
                snapshot.StateVersion,
                snapshot.LastEventSeq,
                snapshot.OwnerEpoch,
                new TableControlRecoveryBaseline(control, 0L),
                new TableRecoveryTiming(null, new long[] { 600L, 1000L, 1000L, 1000L }));
            TableOperationResult completed = await harness.CompleteRecoveryAsync(explicitReserves);
            Assert.Equal(TableOperationStatus.Configured, completed.Status);
            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            Assert.Equal(600L, timing.Seats[0].RemainingReserveMillis);
            Assert.Equal(1000L, timing.Seats[1].RemainingReserveMillis);
            Assert.Equal(1000L, timing.Seats[2].RemainingReserveMillis);
            Assert.Equal(1000L, timing.Seats[3].RemainingReserveMillis);
        }

        [Fact]
        public void RecoveredDecisionConsumesBaseBeforeReserveAndDoesNotRegrantBase()
        {
            var clock = new DecisionClock(new DecisionClockConfig(3000L, 5000L, ReserveScope.Round));
            clock.BeginRound("r", 1000L);
            DecisionTiming first = clock.RestoreDecision(
                new DecisionKey("r", 0, "d1"),
                1000L,
                3000L,
                5000L,
                3000L);
            Assert.Equal(6000L, first.DeadlineMillis);
            Assert.Equal(0L, first.BaseMillis);
            Assert.Equal(5000L, first.ReserveAtOpenMillis);

            Assert.True(clock.Complete(first.Key, 2000L));
            Assert.Equal(4000L, clock.RemainingReserve(0));
            DecisionTiming second = clock.Open(new DecisionKey("r", 0, "d2"), 2000L);
            Assert.Equal(3000L, second.BaseMillis);
            Assert.Equal(4000L, second.ReserveAtOpenMillis);
            Assert.Equal(9000L, second.DeadlineMillis);

            var overdue = new DecisionClock(new DecisionClockConfig(1000L, 1000L, ReserveScope.Round));
            overdue.BeginRound("r2", 100L);
            DecisionTiming dueNow = overdue.RestoreDecision(
                new DecisionKey("r2", 0, "due"),
                100L,
                1000L,
                1000L,
                5000L);
            Assert.Equal(100L, dueNow.DeadlineMillis);
            Assert.Equal(0L, dueNow.BaseMillis);
            Assert.Equal(0L, dueNow.ReserveAtOpenMillis);
            Assert.True(overdue.Expire(dueNow.Key, dueNow.Generation, 100L));

            var overflow = new DecisionClock(new DecisionClockConfig(1000L, 1000L, ReserveScope.Round));
            overflow.BeginRound("r3", long.MaxValue - 500L);
            Assert.Throws<OverflowException>(() => overflow.RestoreDecision(
                new DecisionKey("r3", 0, "overflow"),
                long.MaxValue - 500L,
                1000L,
                1000L,
                0L));
            Assert.Throws<ArgumentOutOfRangeException>(() => overflow.RestoreDecision(
                new DecisionKey("r3", 0, "over-config"),
                100L,
                1001L,
                1000L,
                0L));
        }

        [Fact]
        public async Task RecoveredDecisionTimingExposesBaseFirstAccountingInTheActor()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.Computer),
                decisionClockConfig: new DecisionClockConfig(3000L, 5000L, ReserveScope.Round),
                initialNow: 5000L);
            RoundStateMachine round2 = P31Fixtures.Dealt("round-2");
            TileId tile = round2.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            var recovered = new TableRecoveredDecision(
                new DecisionKey("round-2", 0, "base-first"),
                0L,
                new[] { new LegalActionOption(LegalActionKind.Discard, 0, tile) },
                tile,
                3000L,
                5000L,
                3000L);
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 0),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 3));
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("base-first")).Status);

            var baseline = new TableRecoveryBaseline(
                round2,
                new StateVersion(0UL),
                new EventSeq(0UL),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(control, 0L),
                new TableRecoveryTiming(new[] { recovered }, new long[] { 0L, 5000L, 5000L, 5000L }));
            Assert.Equal(TableOperationStatus.Configured, (await harness.CompleteRecoveryAsync(baseline)).Status);

            TableTimingSnapshot timing = await harness.GetTimingSnapshotAsync();
            DecisionTiming seatTiming = timing.Seats[0].Timing!;
            Assert.Equal(0L, seatTiming.BaseMillis);
            Assert.Equal(5000L, seatTiming.ReserveAtOpenMillis);
            Assert.Equal(10000L, seatTiming.DeadlineMillis);
            Assert.Equal(5000L, timing.Seats[0].RemainingReserveMillis);
            Assert.Equal(5000L, timing.Seats[1].RemainingReserveMillis);
        }

        [Fact]
        public async Task StrictRecoveryFenceRejectsOldAiAndOldPlayerButAcceptsFreshObservation()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            DiscardDecisionFixture decision = P31Fixtures.BuildDiscardDecision(harness);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.OpenDecisionAsync(decision.Pending)).Status);
            TableSnapshot snapshot = await harness.GetSnapshotAsync();
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 0),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 3));
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("same decision")).Status);
            var recovered = new TableRecoveredDecision(
                decision.Pending.Key,
                0L,
                decision.Pending.Candidates,
                decision.Pending.DrawnTile,
                1000L,
                0L,
                0L);
            var baseline = new TableRecoveryBaseline(
                harness.InitialRound,
                snapshot.StateVersion,
                snapshot.LastEventSeq,
                snapshot.OwnerEpoch,
                new TableControlRecoveryBaseline(control, 0L),
                new TableRecoveryTiming(new[] { recovered }));
            Assert.Equal(TableOperationStatus.Configured, (await harness.CompleteRecoveryAsync(baseline)).Status);

            TableTickResult oldAi = await harness.CompleteAiAsync(decision.Ai);
            Assert.DoesNotContain(
                oldAi.Entries,
                entry => entry.Kind == TickEntryKind.AiAccepted &&
                         entry.Transition?.Status == TableOperationStatus.Accepted);
            Assert.Equal(TableRuntimeReasonCode.StaleRecoveryGeneration, oldAi.ReasonCode);

            TableCommandEnvelope oldPlayer = P31Fixtures.Command(
                RoundCommand.Discard(harness.InitialRound.RoundId, 0, decision.Chosen.Tile!.Value),
                0,
                "old-player",
                new DecisionId("d1"),
                0UL);
            TableOperationResult oldPlayerResult = await harness.SubmitAsync(oldPlayer);
            Assert.Equal(TableOperationStatus.Rejected, oldPlayerResult.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleRecoveryGeneration, oldPlayerResult.ReasonCode);

            TableAiObservationResult captured = await harness.CaptureAiObservationAsync(0);
            Assert.True(captured.IsCaptured);
            TableTickResult oldAiAfterFreshCapture = await harness.CompleteAiAsync(decision.Ai);
            Assert.Equal(TableRuntimeReasonCode.StaleRecoveryGeneration, oldAiAfterFreshCapture.ReasonCode);
            var intent = new CommandIntent(captured.Observation!, decision.Chosen, "test-ai", 1);
            var fresh = new AiDecision(captured.Observation!, intent, 0.5d);
            harness.Clock.Now = 1500L;
            TableTickResult accepted = await harness.CompleteAiAsync(fresh);
            Assert.Equal(TickEntryKind.AiAccepted, accepted.Entries[0].Kind);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Entries[0].Transition!.Status);
        }

        [Fact]
        public async Task RecoveryGraceIsRebuiltFromExplicitRemainingTimeAtTheLocalMonotonicStart()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                initialNow: 50000L);
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                new SeatControlState(
                    0,
                    ConnectionStatus.Disconnected,
                    SeatControllerKind.LocalHuman,
                    0L,
                    1000L,
                    11000L,
                    "ai"),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 3));
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("grace recovery")).Status);
            var baseline = new TableRecoveryBaseline(
                harness.InitialRound,
                new StateVersion(0UL),
                new EventSeq(0UL),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(
                    control,
                    0L,
                    new TableGraceRecoveryTiming(new[] { new TableRecoveredGrace(0, 4000L, 0L) })),
                TableRecoveryTiming.None);
            Assert.Equal(TableOperationStatus.Configured, (await harness.CompleteRecoveryAsync(baseline)).Status);

            TableControlSnapshot snapshot = await harness.GetControlSnapshotAsync();
            Assert.Equal(50000L, snapshot.Seats[0].DisconnectedAtMillis);
            Assert.Equal(54000L, snapshot.Seats[0].GraceDeadlineMillis);

            harness.Clock.Now = 50000L;
            TableGraceExpiryFence early = await FenceAsync(harness, 0, 54000L);
            Assert.Equal(TableRuntimeReasonCode.GraceNotDue, (await harness.ExpireGraceAsync(early)).ReasonCode);

            harness.Clock.Now = 54000L;
            TableGraceExpiryFence due = await FenceAsync(harness, 0, 54000L);
            TableControlResult taken = await harness.ExpireGraceAsync(due);
            Assert.True(taken.IsChanged);
            Assert.Equal(SeatControllerKind.Takeover, taken.Snapshot!.Seats[0].Kind);
            Assert.Equal(1L, taken.Snapshot.Seats[0].ControlEpoch);
        }

        [Fact]
        public async Task RecoveryGraceWithoutExplicitTimingIsRejected()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                initialNow: 50000L);
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                new SeatControlState(
                    0,
                    ConnectionStatus.Disconnected,
                    SeatControllerKind.LocalHuman,
                    0L,
                    1000L,
                    11000L,
                    null),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 3));
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("missing grace timing")).Status);
            var baseline = new TableRecoveryBaseline(
                harness.InitialRound,
                new StateVersion(0UL),
                new EventSeq(0UL),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(control, 0L),
                TableRecoveryTiming.None);
            TableOperationResult rejected = await harness.CompleteRecoveryAsync(baseline);

            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryGraceTimingRequired, rejected.ReasonCode);
            Assert.Equal(TableActorLifecycle.Recovering, (await harness.GetSnapshotAsync()).Lifecycle);
        }

        [Fact]
        public async Task RecoveryGraceRemainingOverflowIsRejected()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                initialNow: long.MaxValue - 10L);
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                new SeatControlState(
                    0,
                    ConnectionStatus.Disconnected,
                    SeatControllerKind.LocalHuman,
                    0L,
                    1000L,
                    11000L,
                    null),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 3));
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("grace overflow")).Status);
            var baseline = new TableRecoveryBaseline(
                harness.InitialRound,
                new StateVersion(0UL),
                new EventSeq(0UL),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(
                    control,
                    0L,
                    new TableGraceRecoveryTiming(new[] { new TableRecoveredGrace(0, 100L, 0L) })),
                TableRecoveryTiming.None);
            TableOperationResult rejected = await harness.CompleteRecoveryAsync(baseline);

            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal(TableRuntimeReasonCode.RecoveryGraceTimingInvalid, rejected.ReasonCode);
            Assert.Equal(TableActorLifecycle.Recovering, (await harness.GetSnapshotAsync()).Lifecycle);
        }

        [Fact]
        public async Task RecoveryGraceOverdueIsDueAtTheLocalNowNotThePersistedTimestamp()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor(
                controllers: P31Fixtures.Controllers(SeatControllerKind.LocalHuman),
                initialNow: 50000L);
            ControlRegistrySnapshot control = P31Fixtures.ControlSnapshot(
                new SeatControlState(
                    0,
                    ConnectionStatus.Disconnected,
                    SeatControllerKind.LocalHuman,
                    0L,
                    1000L,
                    11000L,
                    null),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 1),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 2),
                P31Fixtures.Connected(SeatControllerKind.Computer, 0L, 3));
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.BeginRecoveryAsync("overdue grace")).Status);
            var baseline = new TableRecoveryBaseline(
                harness.InitialRound,
                new StateVersion(0UL),
                new EventSeq(0UL),
                new OwnerEpoch(1),
                new TableControlRecoveryBaseline(
                    control,
                    0L,
                    new TableGraceRecoveryTiming(new[] { new TableRecoveredGrace(0, 0L, 2000L) })),
                TableRecoveryTiming.None);
            Assert.Equal(TableOperationStatus.Configured, (await harness.CompleteRecoveryAsync(baseline)).Status);

            TableControlSnapshot snapshot = await harness.GetControlSnapshotAsync();
            Assert.Equal(50000L, snapshot.Seats[0].GraceDeadlineMillis);
            harness.Clock.Now = 50000L;
            TableGraceExpiryFence due = await FenceAsync(harness, 0, 50000L);
            TableControlResult taken = await harness.ExpireGraceAsync(due);
            Assert.True(taken.IsChanged);
            Assert.Equal(SeatControllerKind.Takeover, taken.Snapshot!.Seats[0].Kind);
        }

        [Fact]
        public async Task SlowTickCommitRevalidatesDifferentSeatDeadlines()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            RoundStateMachine round = harness.InitialRound;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Discard(round.RoundId, 0, tile, new[] { 1, 2, 3 }),
                0,
                "open"));
            var first = P31Fixtures.BuildSingleActionDecision(harness, LegalActionKind.Pass, "p1", 1);
            var second = P31Fixtures.BuildSingleActionDecision(harness, LegalActionKind.Pass, "p2", 2);
            harness.Clock.Now = 1000L;
            await harness.OpenDecisionAsync(first.Pending);
            harness.Clock.Now = 1200L;
            await harness.OpenDecisionAsync(second.Pending);

            harness.Clock.Now = 1300L;
            harness.CommitPort.BlockNext();
            Task<TableTickResult> running = harness.TickAsync(new TableTickRequest(new[]
            {
                first.Ai.ToDomainSubmission(1300L),
                second.Ai.ToDomainSubmission(1300L)
            })).AsTask();
            await harness.CommitPort.Entered;
            harness.Clock.Now = 2300L;
            harness.CommitPort.Release();
            TableTickResult result = await running;

            Assert.DoesNotContain(
                result.Entries,
                entry => entry.Seat == 2 &&
                         entry.Kind == TickEntryKind.AiAccepted &&
                         entry.Transition?.Status == TableOperationStatus.Accepted);
            Assert.Contains(
                result.Entries,
                entry => entry.Seat == 2 && entry.Kind == TickEntryKind.Timeout);
            Assert.Contains(
                result.Entries,
                entry => entry.Seat == 2 &&
                         entry.Kind == TickEntryKind.AiRejected &&
                         entry.RejectionCode == TickRejectionCode.DeadlineExpired);
        }

        [Fact]
        public async Task SlowTickFirstCommitFailureStopsLaterSeatAi()
        {
            await using ActorHarness harness = P31Fixtures.CreateActor();
            RoundStateMachine round = harness.InitialRound;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Discard(round.RoundId, 0, tile, new[] { 1, 2, 3 }),
                0,
                "open"));
            var first = P31Fixtures.BuildSingleActionDecision(harness, LegalActionKind.Pass, "p1", 1);
            var second = P31Fixtures.BuildSingleActionDecision(harness, LegalActionKind.Pass, "p2", 2);
            harness.Clock.Now = 1000L;
            await harness.OpenDecisionAsync(first.Pending);
            await harness.OpenDecisionAsync(second.Pending);

            harness.Clock.Now = 1300L;
            harness.CommitPort.FailNext();
            TableTickResult result = await harness.TickAsync(new TableTickRequest(new[]
            {
                first.Ai.ToDomainSubmission(1300L),
                second.Ai.ToDomainSubmission(1300L)
            }));

            Assert.DoesNotContain(
                result.Entries,
                entry => entry.Kind == TickEntryKind.AiAccepted &&
                         entry.Transition?.Status == TableOperationStatus.Accepted);
            Assert.Contains(
                result.Entries,
                entry => entry.Seat == 2 &&
                         entry.Transition?.Status == TableOperationStatus.Failed);
        }

        private static async Task<TableGraceExpiryFence> FenceAsync(
            ActorHarness harness,
            int seat,
            long graceDeadline)
        {
            TableControlSnapshot snapshot = await harness.GetControlSnapshotAsync();
            return new TableGraceExpiryFence(
                seat,
                snapshot.RoundId,
                snapshot.OwnerEpoch,
                new ControlEpoch((ulong)snapshot.Seats[seat].ControlEpoch),
                graceDeadline,
                snapshot.RecoveryGeneration);
        }
    }
}
