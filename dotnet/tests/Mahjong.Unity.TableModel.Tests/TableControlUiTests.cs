using System;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
using Xunit;

namespace Mahjong.Unity.TableModel.Tests
{
    /// <summary>
    /// Cross-layer control-UI regressions. They deliberately drive the confirmed
    /// view through the same projector/factory/interaction objects the Unity
    /// presenter uses rather than testing a private mirror of that logic.
    /// </summary>
    public sealed class TableControlUiTests
    {
        private static readonly RequestId Request = new RequestId("request-control-ui");
        private static readonly RequestScopeId Scope = new RequestScopeId(P43TestData.ScopeId);

        private static TableCommandOwnerContext Owner()
        {
            return new TableCommandOwnerContext(Request, Scope);
        }

        private static TableCommandOwnerContext Owner(string requestId)
        {
            return new TableCommandOwnerContext(new RequestId(requestId), Scope);
        }

        [Fact]
        public void RapidReleaseClicksReuseOnePendingRequestAndDoNotRenewIt()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot()));

            TableInteractionOutcome first = state.BuildIntent(TableIntentKind.ReleaseControl, Owner());
            Assert.True(first.Accepted, first.Code);
            TablePendingIntent pending = Assert.IsType<TablePendingIntent>(state.Pending);
            string requestId = pending.Intent.Command.RequestId.Value;

            TableInteractionOutcome second = state.BuildIntent(TableIntentKind.ReleaseControl, Owner());
            TableInteractionOutcome third = state.BuildIntent(TableIntentKind.ReleaseControl, Owner());
            Assert.True(second.Accepted);
            Assert.True(third.Accepted);
            Assert.Equal(TableInteractionCodes.AlreadyPending, second.Code);
            Assert.Equal(TableInteractionCodes.AlreadyPending, third.Code);
            Assert.Same(pending, state.Pending);
            Assert.Equal(requestId, state.Pending!.Intent.Command.RequestId.Value);
        }

        [Fact]
        public void BaselineResetClearsControlPendingAndSelection()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            PlayerTableViewState view = View(clock, P43TestData.PlayerSnapshot());
            state.ApplyConfirmedView(view);
            Assert.True(state.SelectAction(view.Own!.Decision!.Candidates[0]).Accepted);
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);

            TableInteractionOutcome reset = state.OnBaselineReset(view.Identity.Generation + 1);

            Assert.True(reset.Accepted);
            Assert.Null(state.Pending);
            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
        }

        [Fact]
        public void ReconnectToAutomatedAuthorityDoesNotAutoClaim()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(controlEpoch: 0, controlVersion: 0)));

            // A reconnect delivers a fresh baseline whose controller is already
            // automated. Applying it must not fabricate a claim command.
            PlayerTableViewState reconnected = View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 9,
                eventSeq: 120,
                controlledByComputer: true,
                controlEpoch: 1,
                controlVersion: 2,
                includeDecision: false));
            state.ApplyConfirmedView(reconnected);

            Assert.Null(state.Pending);
            Assert.True(reconnected.Control!.ControlledByComputer);
            Assert.Equal(1UL, reconnected.Control.ControlEpoch);

            TableInteractionOutcome claim = state.BuildIntent(TableIntentKind.ClaimControl, Owner());
            Assert.True(claim.Accepted, claim.Code);
            Assert.Equal(CommandKind.ClaimControl, state.Pending!.Intent.Kind);
            Assert.Equal(1UL, state.Pending.Intent.ControlEpoch);
        }

        [Fact]
        public void OtherSeatControlVersionChangeDoesNotClearSendingControlRequest()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(controlEpoch: 7, controlVersion: 1)));
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);
            TablePendingIntent pending = state.Pending!;
            string requestId = pending.Intent.Command.RequestId.Value;

            // The table-wide control version advances because another seat
            // changed, while this seat's epoch/controller stay identical.
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                controlEpoch: 7,
                controlVersion: 2)));

            Assert.Same(pending, state.Pending);
            Assert.Equal(requestId, state.Pending!.Intent.Command.RequestId.Value);
            Assert.Null(state.Pending.LastOutcome);
            TableInteractionOutcome duplicate = state.BuildIntent(TableIntentKind.ReleaseControl, Owner());
            Assert.True(duplicate.Accepted);
            Assert.Equal(TableInteractionCodes.AlreadyPending, duplicate.Code);
            Assert.Same(pending, state.Pending);
        }

        [Fact]
        public void OtherSeatControlVersionChangeDoesNotClearUnknownControlRequest()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(controlEpoch: 7, controlVersion: 1)));
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);
            Assert.True(state.MarkDeliveryUnknown().Accepted);
            TablePendingIntent pending = state.Pending!;

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                controlEpoch: 7,
                controlVersion: 9)));

            Assert.Same(pending, state.Pending);
            Assert.Equal(TablePendingOutcome.Unknown, state.Pending!.LastOutcome);
            Assert.True(state.Pending.NeedsReconciliation);
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);
            Assert.Equal(
                TableInteractionCodes.AlreadyPending,
                state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Code);
        }

        [Fact]
        public void OtherSeatControlVersionChangeDoesNotDropPendingOrdinaryAction()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            PlayerTableViewState view = View(clock, P43TestData.PlayerSnapshot(controlEpoch: 7, controlVersion: 1));
            state.ApplyConfirmedView(view);
            Assert.True(state.SelectAction(view.Own!.Decision!.Candidates[0]).Accepted);
            Assert.True(state.BuildIntent(TableIntentKind.SubmitAction, Owner()).Accepted);
            TablePendingIntent pending = state.Pending!;

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                controlEpoch: 7,
                controlVersion: 2)));

            Assert.Same(pending, state.Pending);
            Assert.Equal(CommandKind.SubmitAction, state.Pending!.Intent.Kind);
        }

        [Fact]
        public void OwnSeatAuthorityChangeClearsPendingAndAllowsNewClaim()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(controlEpoch: 7, controlVersion: 1)));
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                controlledByComputer: true,
                controlEpoch: 8,
                controlVersion: 2)));

            Assert.Null(state.Pending);
            TableInteractionOutcome claim = state.BuildIntent(TableIntentKind.ClaimControl, Owner("request-after-own-change"));
            Assert.True(claim.Accepted, claim.Code);
            Assert.Equal(CommandKind.ClaimControl, state.Pending!.Intent.Kind);
            Assert.Equal(8UL, state.Pending.Intent.ControlEpoch);
        }

        [Fact]
        public void BaselineReconnectClearsPendingAndAllowsFreshRequest()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(controlEpoch: 7, controlVersion: 1)));
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner("request-before-reconnect")).Accepted);
            string oldRequestId = state.Pending!.Intent.Command.RequestId.Value;
            Assert.True(state.OnBaselineReset(2).Accepted);
            Assert.Null(state.Pending);

            state.ApplyConfirmedView(View(
                clock,
                P43TestData.PlayerSnapshot(stateVersion: 9, eventSeq: 120, controlEpoch: 7, controlVersion: 3),
                generation: 2));
            TableInteractionOutcome retry = state.BuildIntent(TableIntentKind.ReleaseControl, Owner("request-after-reconnect"));
            Assert.True(retry.Accepted, retry.Code);
            Assert.NotEqual(oldRequestId, state.Pending!.Intent.Command.RequestId.Value);
        }

        [Fact]
        public void MissingThenRecoveredSameControlAuthorityKeepsSendingRequest()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(controlEpoch: 7, controlVersion: 1)));
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);
            TablePendingIntent pending = state.Pending!;
            string requestId = pending.Intent.Command.RequestId.Value;

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                includeControl: false)));
            Assert.Same(pending, state.Pending);
            Assert.Null(state.View!.Control);

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 7,
                eventSeq: 107,
                controlEpoch: 7,
                controlVersion: 2)));

            Assert.Same(pending, state.Pending);
            Assert.Equal(requestId, state.Pending!.Intent.Command.RequestId.Value);
            Assert.Equal(
                TableInteractionCodes.AlreadyPending,
                state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Code);
        }

        [Fact]
        public void MissingThenRecoveredSameControlAuthorityKeepsUnknownRequest()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(controlEpoch: 7, controlVersion: 1)));
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);
            Assert.True(state.MarkDeliveryUnknown().Accepted);
            TablePendingIntent pending = state.Pending!;

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                includeControl: false)));
            Assert.Same(pending, state.Pending);

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 7,
                eventSeq: 107,
                controlEpoch: 7,
                controlVersion: 3)));
            Assert.Same(pending, state.Pending);
            Assert.True(state.Pending!.NeedsReconciliation);
            Assert.Equal(TablePendingOutcome.Unknown, state.Pending.LastOutcome);
            Assert.Equal(
                TableInteractionCodes.AlreadyPending,
                state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Code);
        }

        [Fact]
        public void MissingThenRecoveredSameControlAuthorityKeepsOrdinaryAction()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            PlayerTableViewState view = View(clock, P43TestData.PlayerSnapshot(controlEpoch: 7, controlVersion: 1));
            state.ApplyConfirmedView(view);
            Assert.True(state.SelectAction(view.Own!.Decision!.Candidates[0]).Accepted);
            Assert.True(state.BuildIntent(TableIntentKind.SubmitAction, Owner()).Accepted);
            TablePendingIntent pending = state.Pending!;

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                includeControl: false)));
            Assert.Same(pending, state.Pending);

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 7,
                eventSeq: 107,
                controlEpoch: 7,
                controlVersion: 2)));
            Assert.Same(pending, state.Pending);
            Assert.Equal(CommandKind.SubmitAction, state.Pending!.Intent.Kind);
        }

        [Fact]
        public void MissingThenChangedControlAuthorityStillClearsPending()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(controlEpoch: 7, controlVersion: 1)));
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                includeControl: false)));
            Assert.NotNull(state.Pending);

            state.ApplyConfirmedView(View(clock, P43TestData.PlayerSnapshot(
                stateVersion: 7,
                eventSeq: 107,
                controlledByComputer: true,
                controlEpoch: 8,
                controlVersion: 2)));

            Assert.Null(state.Pending);
            TableInteractionOutcome claim = state.BuildIntent(TableIntentKind.ClaimControl, Owner("request-after-gap-change"));
            Assert.True(claim.Accepted, claim.Code);
            Assert.Equal(8UL, state.Pending!.Intent.ControlEpoch);
        }

        [Fact]
        public void AutomatedSeatRejectsPlayerActionsEvenWhenADecisionExists()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot automated = P43TestData.PlayerSnapshot(
                controlledByComputer: true,
                controlEpoch: 4,
                controlVersion: 3);
            PlayerTableViewState view = View(clock, automated);
            PlayerLegalActionState action = view.Own!.Decision!.Candidates[0];
            var factory = new TableCommandIntentFactory(clock);

            TableIntentUnavailableException error = Assert.Throws<TableIntentUnavailableException>(() =>
                factory.CreateSubmitAction(view, action, Owner()));
            Assert.Equal(TableCommandIntentFactory.ControlAutomated, error.Code);

            TableInteractionState state = new TableInteractionState(factory);
            state.ApplyConfirmedView(view);
            TableInteractionOutcome select = state.SelectAction(action);
            Assert.False(select.Accepted);
            Assert.Equal(TableCommandIntentFactory.ControlAutomated, select.Code);
        }

        [Fact]
        public void OldActionEpochIsRejectedAfterAControlTransition()
        {
            var clock = new ManualTableClock();
            PlayerTableProjectionMapper mapper = new PlayerTableProjectionMapper(clock);
            PlayerTableViewState oldView = mapper.Project(P43TestData.View(P43TestData.PlayerSnapshot()));
            PlayerLegalActionState oldAction = oldView.Own!.Decision!.Candidates[0];

            PlayerSnapshot next = P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                controlledByComputer: false,
                controlEpoch: 8,
                controlVersion: 2);
            next.PrivateView.Decision.ControlEpoch = 8;
            PlayerTableViewState newView = mapper.Project(P43TestData.View(next));
            var factory = new TableCommandIntentFactory(clock);

            Assert.Equal(
                "STALE_DECISION",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitAction(newView, oldAction, Owner())).Code);
            Assert.Equal(8UL, newView.Control!.ControlEpoch);
        }

        [Fact]
        public void PublicSpectatorSnapshotNeverCarriesControlState()
        {
            var publicOnly = new PlayerSnapshot
            {
                SessionId = P43TestData.SessionId,
                RoundId = P43TestData.RoundId,
                StateVersion = 5,
                EventSeq = 105,
                PublicView = P43TestData.PublicMatch()
            };
            PlayerTableViewState spectator = new PlayerTableProjectionMapper(new ManualTableClock()).Project(
                P43TestData.View(publicOnly, seat: -1, role: ProtocolRole.Spectator));

            Assert.False(spectator.Identity.IsPrivateAuthorized);
            Assert.Null(spectator.Own);
            Assert.Null(spectator.Control);
            Assert.Throws<TableIntentUnavailableException>(() =>
                new TableCommandIntentFactory(new ManualTableClock()).CreateReleaseControl(spectator, Owner()));
        }

        private static PlayerTableViewState View(ManualTableClock clock, PlayerSnapshot snapshot, long generation = 1)
        {
            return new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(snapshot, generation: generation));
        }
    }
}
