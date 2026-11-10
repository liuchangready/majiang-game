using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// Minimal authority-path coverage for the new AI cheat capture/submit entry points.
    /// These tests intentionally stay on existing fixtures and focus on rejection side effects,
    /// the takeover spend fence, duplicate replay and durable recovery.
    /// </summary>
    public sealed class TableAiCheatAuthorityTests
    {
        private const int InitialPoints = 120;

        [Fact]
        public async Task NonAutomatedSeatCannotCaptureOrSubmitAiCheat()
        {
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.LocalHuman));
            TableAiCheatCaptureResult captured = await harness.Actor.CaptureAiCheatOpportunityAsync(
                0,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableAiCheatCaptureStatus.Rejected, captured.Status);
            Assert.Equal(TableRuntimeReasonCode.SeatNotAutomated, captured.ReasonCode);
            Assert.Null(captured.Opportunity);
        }

        [Fact]
        public async Task StaleStateControlAndRecoveryPlansAreRejectedWithoutFee()
        {
            await using (ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer)))
            {
                TableAiCheatPlan plan = await CaptureTileMarkPlanAsync(harness.Actor);
                long balance = CheatBalance(harness.Actor, 0);

                // Advance the round: the captured expected state version becomes stale.
                RoundStateMachine round = harness.Actor.GetRoundStateForHost();
                int seat = round.CurrentSeat;
                TileId discard = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
                TableOperationResult moved = await harness.SubmitAsync(P31Fixtures.Command(
                    RoundCommand.Discard(round.RoundId, seat, discard),
                    0,
                    "advance"));
                Assert.Equal(TableOperationStatus.Accepted, moved.Status);

                TableOperationResult staleState = await harness.Actor.SubmitAiCheatAsync(
                    plan,
                    false,
                    TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Rejected, staleState.Status);
                Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleState.ReasonCode);
                Assert.Equal(balance, CheatBalance(harness.Actor, 0));
            }

            await using (ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer)))
            {
                TableAiCheatPlan plan = await CaptureTileMarkPlanAsync(harness.Actor);
                long balance = CheatBalance(harness.Actor, 0);
                TableAiCheatPlan controlForged = WithEnvelope(
                    plan,
                    controlEpoch: new ControlEpoch(plan.Envelope.ControlEpoch.Value + 1UL));
                TableOperationResult staleControl = await harness.Actor.SubmitAiCheatAsync(
                    controlForged,
                    false,
                    TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Rejected, staleControl.Status);
                Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, staleControl.ReasonCode);
                Assert.Equal(balance, CheatBalance(harness.Actor, 0));
            }
        }

        [Fact]
        public async Task TakeoverSeatCannotInitiatePaidCheatButExplicitPolicyCan()
        {
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Takeover));

            TableAiCheatCaptureResult blocked = await harness.Actor.CaptureAiCheatOpportunityAsync(
                0,
                false,
                TestContext.Current.CancellationToken);
            Assert.True(blocked.IsCaptured);
            Assert.Empty(blocked.Opportunity!.View.Options);

            TableAiCheatCaptureResult allowed = await harness.Actor.CaptureAiCheatOpportunityAsync(
                0,
                true,
                TestContext.Current.CancellationToken);
            Assert.True(allowed.IsCaptured);
            TableAiCheatPlan plan = Plan(allowed.Opportunity!, "tile_mark");
            long before = CheatBalance(harness.Actor, 0);

            TableOperationResult defaultDenied = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, defaultDenied.Status);
            Assert.Equal(TableRuntimeReasonCode.TakeoverCheatSpendForbidden, defaultDenied.ReasonCode);
            Assert.Equal(before, CheatBalance(harness.Actor, 0));

            TableOperationResult explicitAllowed = await harness.Actor.SubmitAiCheatAsync(
                plan,
                true,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, explicitAllowed.Status);
            long cost = before - CheatBalance(harness.Actor, 0);
            Assert.InRange(cost, 1, before);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));
        }

        [Fact]
        public async Task DuplicateAiPlanIsRejectedAndChargedOnce()
        {
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer));
            TableAiCheatPlan plan = await CaptureTileMarkPlanAsync(harness.Actor);

            TableOperationResult first = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);
            long afterFirst = CheatBalance(harness.Actor, 0);
            long cost = InitialPoints - afterFirst;
            Assert.InRange(cost, 1, InitialPoints);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));

            TableOperationResult replay = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, replay.Status);
            Assert.True(
                replay.ReasonCode == TableRuntimeReasonCode.CheatReplayed ||
                replay.ReasonCode == TableRuntimeReasonCode.StaleStateVersion ||
                replay.ReasonCode == TableRuntimeReasonCode.StaleRecoveryGeneration,
                replay.ReasonCode + ": " + replay.Detail);
            Assert.Equal(afterFirst, CheatBalance(harness.Actor, 0));
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));
        }

        [Fact]
        public async Task DurableRecoveryOfPaidAiCheatKeepsLedgerAndConservationWithoutDoubleCharge()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(
                V32Fixtures.Cheat(InitialPoints),
                round.RoundId);
            TableCheckpoint checkpoint = WithCheat(
                DurableHarness.InitialCheckpoint(
                    round,
                    cheat.Ledger,
                    control: ComputerControlCheckpoint()),
                cheat);

            await using DurableHarness harness = await DurableHarness.CreateAsync(
                ledger: cheat.Ledger,
                initialCheckpoint: checkpoint,
                configure: options =>
                {
                    options.InitialControllers = P31Fixtures.Controllers(
                        SeatControllerKind.Computer,
                        SeatControllerKind.Computer,
                        SeatControllerKind.Computer,
                        SeatControllerKind.Computer);
                    options.CheatEconomy = V32Fixtures.Economy(InitialPoints);
                    options.SleeveBloomConfig = V32Fixtures.SleeveConfig();
                    options.InitialCheatState = cheat;
                });

            TableAiCheatPlan plan = await CaptureTileMarkPlanAsync(harness.Actor);
            TableOperationResult first = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);
            long afterFirst = harness.Actor.GetCheatLedgerForHost().Balances[0];
            long cost = InitialPoints - afterFirst;
            Assert.InRange(cost, 1, InitialPoints);

            TableRecoveryResult recovered = await harness.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.True(
                recovered.Status == TableRecoveryStatus.Recovered,
                recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(1, recovered.Report!.ReplayedCommands);
            Assert.True(recovered.Report.CheatStateRestored);
            Assert.Equal(afterFirst, harness.Actor.GetCheatLedgerForHost().Balances[0]);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));
            ConservationReport conservation = TilePositionMap.Validate(
                harness.Actor.GetRoundStateForHost().Tiles);
            Assert.True(conservation.IsValid, string.Join("; ", conservation.Errors));
            Assert.Equal(136, conservation.AssignedCount);

            TableOperationResult replay = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, replay.Status);
            Assert.True(
                replay.ReasonCode == TableRuntimeReasonCode.CheatReplayed ||
                replay.ReasonCode == TableRuntimeReasonCode.StaleStateVersion ||
                replay.ReasonCode == TableRuntimeReasonCode.StaleRecoveryGeneration,
                replay.ReasonCode + ": " + replay.Detail);
            Assert.Equal(afterFirst, harness.Actor.GetCheatLedgerForHost().Balances[0]);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));

            TableAiCheatPlan fresh = await CaptureTileMarkPlanAsync(harness.Actor);
            TableAiCheatPlan staleGeneration = WithEnvelope(fresh, recoveryGeneration: 0L);
            TableOperationResult staleRecovery = await harness.Actor.SubmitAiCheatAsync(
                staleGeneration,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, staleRecovery.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleRecoveryGeneration, staleRecovery.ReasonCode);
            Assert.Equal(afterFirst, harness.Actor.GetCheatLedgerForHost().Balances[0]);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));
        }

        private static ActorHarness CreateHarness(
            IReadOnlyDictionary<int, SeatControllerKind> controllers)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(
                V32Fixtures.Cheat(InitialPoints),
                round.RoundId);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = controllers,
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = cheat
            };
            var actor = new TableActor(options, round, commitPort, clock, sink);
            return new ActorHarness(actor, clock, commitPort, sink, round);
        }

        private static async Task<TableAiCheatPlan> CaptureTileMarkPlanAsync(TableActor actor)
        {
            TableAiCheatCaptureResult captured = await actor.CaptureAiCheatOpportunityAsync(
                0,
                false,
                TestContext.Current.CancellationToken);
            Assert.True(captured.IsCaptured, captured.ReasonCode + ": " + captured.Detail);
            return Plan(captured.Opportunity!, "tile_mark");
        }

        private static TableAiCheatPlan Plan(TableAiCheatOpportunity opportunity, string capabilityId)
        {
            TableAiCheatPlan? plan = opportunity.Plans.FirstOrDefault(
                candidate => string.Equals(candidate.Option.CapabilityId, capabilityId, StringComparison.Ordinal));
            Assert.NotNull(plan);
            return plan!;
        }

        private static TableAiCheatPlan WithEnvelope(
            TableAiCheatPlan source,
            ControlEpoch? controlEpoch = null,
            long? recoveryGeneration = null)
        {
            TableCheatCommandEnvelope envelope = source.Envelope;
            var changed = new TableCheatCommandEnvelope(
                envelope.CheatCommand,
                envelope.RequestId,
                envelope.ExpectedStateVersion,
                envelope.OwnerEpoch,
                controlEpoch ?? envelope.ControlEpoch,
                envelope.DecisionId,
                recoveryGeneration ?? envelope.RecoveryGeneration,
                envelope.ScopeId,
                envelope.Api,
                envelope.PayloadSha256);
            return new TableAiCheatPlan(source.Option, changed);
        }

        private static long CheatBalance(TableActor actor, int seat)
        {
            return actor.GetCheatLedgerForHost().Balances[seat];
        }

        private static TableControlCheckpoint ComputerControlCheckpoint()
        {
            var states = new SeatControlState[4];
            var preferred = new SeatControllerKind[4];
            for (int seat = 0; seat < 4; seat++)
            {
                states[seat] = new SeatControlState(
                    seat,
                    ConnectionStatus.Connected,
                    SeatControllerKind.Computer,
                    0L,
                    null,
                    null,
                    null);
                preferred[seat] = SeatControllerKind.LocalHuman;
            }

            return new TableControlCheckpoint(
                new ControlRegistrySnapshot(new DisconnectPolicy(), states, preferred),
                0L);
        }

        private static TableCheckpoint WithCheat(TableCheckpoint source, CheatState cheat)
        {
            byte[] payload = TableCheatLedgerCheckpointCodec.Encode(
                TableCheatLedgerCheckpoint.FromCheatState(cheat));
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
    }
}
