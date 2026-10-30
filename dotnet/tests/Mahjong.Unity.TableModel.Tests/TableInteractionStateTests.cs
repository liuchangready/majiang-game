using System;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Table;
using Xunit;

namespace Mahjong.Unity.TableModel.Tests
{
    public sealed class TableInteractionStateTests
    {
        private static readonly RequestId Request = new RequestId("request-interaction");
        private static readonly RequestScopeId Scope = new RequestScopeId(P43TestData.ScopeId);

        private static TableCommandOwnerContext Owner()
        {
            return new TableCommandOwnerContext(Request, Scope);
        }

        private static PlayerTableViewState View(ManualTableClock clock, PlayerSnapshot? snapshot = null)
        {
            return new PlayerTableProjectionMapper(clock).Project(P43TestData.View(snapshot ?? P43TestData.PlayerSnapshot()));
        }

        private static TableInteractionState State(ManualTableClock clock, out TableCommandIntentFactory factory)
        {
            factory = new TableCommandIntentFactory(clock);
            return new TableInteractionState(factory);
        }

        [Fact]
        public void SelectActionBuildsOnePendingIntent()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);

            TableInteractionOutcome selected = state.SelectAction(view.Own!.Decision!.Candidates[0]);
            TableInteractionOutcome built = state.BuildIntent(TableIntentKind.SubmitAction, Owner());

            Assert.True(selected.Accepted);
            Assert.True(built.Accepted, built.Code);
            Assert.NotNull(state.Pending);
            Assert.Equal(CommandKind.SubmitAction, state.Pending!.Intent.Kind);
            Assert.Equal("APPLIED", built.Code);
        }

        [Fact]
        public void RepeatedClickOfSameIntentDoesNotCreateSecondPending()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[0]);
            long revision = state.Revision;

            Assert.True(state.BuildIntent(TableIntentKind.SubmitAction, Owner()).Accepted);
            Assert.Equal(revision + 1, state.Revision);

            TableInteractionOutcome second = state.BuildIntent(TableIntentKind.SubmitAction, Owner());
            Assert.True(second.Accepted);
            Assert.Equal("ALREADY_PENDING", second.Code);
            Assert.Equal(revision + 1, state.Revision);
            Assert.Single(new[] { state.Pending });
        }

        [Fact]
        public void DifferentIntentWhilePendingIsRejected()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[0]);
            Assert.True(state.BuildIntent(TableIntentKind.SubmitAction, Owner()).Accepted);

            state.ClearSelection();
            state.SelectAction(view.Own.Decision.Candidates[1]);
            TableInteractionOutcome conflict = state.BuildIntent(TableIntentKind.SubmitAction, Owner());

            Assert.False(conflict.Accepted);
            Assert.Equal("PENDING_CONFLICT", conflict.Code);
        }

        [Fact]
        public void UnknownOutcomeKeepsPendingUntilClientResolvesIt()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[0]);
            state.BuildIntent(TableIntentKind.SubmitAction, Owner());

            Assert.True(state.MarkDeliveryUnknown().Accepted);
            Assert.True(state.Pending!.NeedsReconciliation);
            Assert.Equal(TablePendingOutcome.Unknown, state.Pending.LastOutcome);

            // An animation being consumed is not a receipt.
            var animation = new ConfirmedAnimationQueue();
            animation.AcceptConfirmedBaseline(view);
            Assert.True(animation.TryDequeue(out _) == false);
            Assert.NotNull(state.Pending);

            Assert.True(state.ApplyClientOutcome(TablePendingOutcome.Unknown).Accepted);
            Assert.NotNull(state.Pending);
            Assert.True(state.Pending!.NeedsReconciliation);

            Assert.True(state.ApplyClientOutcome(TablePendingOutcome.Applied).Accepted);
            Assert.Null(state.Pending);
        }

        [Fact]
        public void RejectedOutcomeAlsoClearsPending()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[0]);
            state.BuildIntent(TableIntentKind.SubmitAction, Owner());

            Assert.True(state.ApplyClientOutcome(TablePendingOutcome.Rejected).Accepted);
            Assert.Null(state.Pending);
        }

        [Fact]
        public void NewDecisionClearsSelectionButNotPending()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[1]);
            state.BuildIntent(TableIntentKind.SubmitAction, Owner());
            Assert.NotNull(state.Pending);

            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106);
            next.PrivateView.Decision.DecisionId = "decision-next";
            PlayerTableViewState nextView = View(clock, next);
            state.ApplyConfirmedView(nextView);

            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
            Assert.NotNull(state.Pending);
            Assert.Equal("decision-p43", state.Pending!.Intent.DecisionId);
        }

        [Fact]
        public void IdentityChangeClearsSelectionAndPending()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[0]);
            state.BuildIntent(TableIntentKind.SubmitAction, Owner());

            PlayerSnapshot next = P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                sessionId: "session-next");
            PlayerTableViewState nextView = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(next, sessionId: "session-next", bindingEpoch: 12));
            state.ApplyConfirmedView(nextView);

            Assert.Null(state.Pending);
            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
        }

        [Fact]
        public void BaselineResetAndFocusLossClearInput()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[0]);
            state.BuildIntent(TableIntentKind.SubmitAction, Owner());

            Assert.True(state.OnFocusLost().Accepted);
            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
            Assert.NotNull(state.Pending);

            Assert.True(state.OnPause(true).Accepted);
            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);

            Assert.True(state.OnBaselineReset(2).Accepted);
            Assert.Null(state.Pending);
        }

        [Fact]
        public void ExpiredDecisionFailsBuildAndClearsSelection()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[0]);

            clock.Advance(8001);
            TableInteractionOutcome result = state.BuildIntent(TableIntentKind.SubmitAction, Owner());

            Assert.False(result.Accepted);
            Assert.Equal("DECISION_EXPIRED", result.Code);
            Assert.Null(state.Pending);
        }

        [Fact]
        public void ExpiryIsImmediateWhileAnimationIsStillPending()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[0]);

            var animation = new ConfirmedAnimationQueue();
            animation.AcceptConfirmedBaseline(view);
            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106, projectionSeq: 11);
            next.PublicView.Board.Players[1].Discards.Add(new Mahjong.Contracts.Wire.DiscardView
            {
                TileId = 44,
                TurnNumber = 2
            });
            PlayerTableViewState nextView = View(clock, next);
            Assert.True(animation.AcceptConfirmedDifference(view, nextView, false).Accepted);
            Assert.Equal(1, animation.PendingCount);

            clock.Advance(8001);
            TableInteractionOutcome result = state.BuildIntent(TableIntentKind.SubmitAction, Owner());
            Assert.Equal("DECISION_EXPIRED", result.Code);
            Assert.Equal(1, animation.PendingCount);
        }

        [Fact]
        public void SelectDiscardTileFindsOnlyAConfirmedCandidate()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);

            Assert.True(state.SelectDiscardTile(13).Accepted);
            Assert.Equal(TableSelectionKind.Action, state.Selection.Kind);
            Assert.Equal("discard-13", state.Selection.Action!.OptionId);

            Assert.False(state.SelectDiscardTile(99).Accepted);
            Assert.Equal("NOT_A_CANDIDATE", state.SelectDiscardTile(99).Code);
        }

        [Fact]
        public void TileMarkSelectionIsClearedWhenItsCandidatesChange()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            Assert.True(state.SelectTileMark(new uint[] { 0, 1, 2, 3 }).Accepted);
            Assert.Equal(TableSelectionKind.TileMark, state.Selection.Kind);

            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106);
            next.PrivateView.Cheat.TileMark.SelectableTileIds.RemoveAt(0);
            PlayerTableViewState nextView = View(clock, next);
            state.ApplyConfirmedView(nextView);

            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
        }

        [Fact]
        public void StaleOfferSelectionIsClearedWhenOfferChanges()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            Assert.True(state.SelectSleeveBloom(false, SleeveBloomChoice.First).Accepted);

            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106);
            next.PrivateView.Cheat.SleeveBloom.OfferId = "offer-next";
            PlayerTableViewState nextView = View(clock, next);
            state.ApplyConfirmedView(nextView);

            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
        }

        [Fact]
        public void PreparationSelectionBuildsConfirmedTargetPlan()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);

            Assert.True(state.SelectPreparation(true).Accepted);
            TableInteractionOutcome built = state.BuildIntent(TableIntentKind.SubmitShufflePlan, Owner());
            Assert.True(built.Accepted, built.Code);
            Assert.Equal(CommandKind.SubmitShufflePlan, state.Pending!.Intent.Kind);
        }

        [Fact]
        public void InvalidSleeveChoiceIsRejectedBeforeSelection()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);

            TableInteractionOutcome result = state.SelectSleeveBloom(false, SleeveBloomChoice.Unspecified);
            Assert.False(result.Accepted);
            Assert.Equal("NOT_A_CANDIDATE", result.Code);
            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
        }

        [Fact]
        public void SpectatorCannotSelectOrBuild()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState spectator = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(null, P43TestData.PublicSnapshot(), seat: -1, role: ProtocolRole.Spectator));
            state.ApplyConfirmedView(spectator);

            Assert.False(state.SelectPreparation(true).Accepted);
            Assert.Equal("NO_PRIVATE_VIEW", state.SelectPreparation(true).Code);
            Assert.False(state.BuildIntent(TableIntentKind.NextRound, Owner()).Accepted);
            Assert.Null(state.Pending);
        }

        [Fact]
        public void UnknownPendingRejectsANewRequestId()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            state.SelectAction(view.Own!.Decision!.Candidates[0]);
            Assert.True(state.BuildIntent(TableIntentKind.SubmitAction, Owner()).Accepted);
            Assert.True(state.MarkDeliveryUnknown().Accepted);

            var newRequest = new TableCommandOwnerContext(
                new RequestId("request-new-after-unknown"),
                new RequestScopeId(P43TestData.ScopeId));
            TableInteractionOutcome retry = state.BuildIntent(TableIntentKind.SubmitAction, newRequest);

            Assert.True(retry.Accepted);
            Assert.Equal("ALREADY_PENDING", retry.Code);
            Assert.Equal("request-interaction", state.Pending!.Intent.Command.RequestId.Value);
            Assert.True(state.Pending.NeedsReconciliation);
        }

        [Fact]
        public void StaleControlEpochIsRejectedEvenWithTheSameDecisionId()
        {
            var clock = new ManualTableClock();
            PlayerTableProjectionMapper mapper = new PlayerTableProjectionMapper(clock);
            PlayerTableViewState oldView = mapper.Project(P43TestData.View(P43TestData.PlayerSnapshot()));
            PlayerLegalActionState oldAction = oldView.Own!.Decision!.Candidates[0];

            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106);
            next.PrivateView.Decision.ControlEpoch = 8;
            PlayerTableViewState newView = mapper.Project(P43TestData.View(next));
            var factory = new TableCommandIntentFactory(clock);

            Assert.Equal(
                "STALE_DECISION",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitAction(newView, oldAction, Owner())).Code);
        }

        [Fact]
        public void StaleOffTurnWindowIsClearedBeforeBuild()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            Assert.True(state.SelectSleeveBloom(true, SleeveBloomChoice.First).Accepted);

            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106);
            next.PrivateView.Cheat.WindowId = "window-2";
            next.PrivateView.Cheat.OffTurnSleeveBloom.WindowId = "window-2";
            next.PrivateView.Cheat.SleeveBloomReservation.WindowId = "window-2";
            PlayerTableViewState nextView = View(clock, next);
            state.ApplyConfirmedView(nextView);

            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
            TableInteractionOutcome build = state.BuildIntent(TableIntentKind.SubmitSleeveBloomOffTurnChoice, Owner());
            Assert.False(build.Accepted);
            Assert.Equal("SELECTION_REQUIRED", build.Code);
            Assert.Null(state.Pending);
        }

        [Fact]
        public void NewOfferClearsSelectionButPendingRetainsOriginalOffer()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState view = View(clock);
            state.ApplyConfirmedView(view);
            Assert.True(state.SelectSleeveBloom(false, SleeveBloomChoice.First).Accepted);
            Assert.True(state.BuildIntent(TableIntentKind.SubmitSleeveBloomChoice, Owner()).Accepted);

            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106);
            next.PrivateView.Cheat.SleeveBloom.OfferId = "offer-next";
            PlayerTableViewState nextView = View(clock, next);
            state.ApplyConfirmedView(nextView);

            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
            Assert.Equal("offer-1", state.Pending!.Intent.OfferId);
        }

        [Fact]
        public void ControlIntentUsesConfirmedAuthorityAndBlocksOppositeTransition()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState human = View(clock);
            state.ApplyConfirmedView(human);

            TableInteractionOutcome release = state.BuildIntent(TableIntentKind.ReleaseControl, Owner());
            Assert.True(release.Accepted, release.Code);
            Assert.NotNull(state.Pending);
            Assert.Equal(CommandKind.ReleaseControl, state.Pending!.Intent.Kind);
            Assert.Equal(7UL, state.Pending.Intent.ControlEpoch);

            TableInteractionOutcome duplicate = state.BuildIntent(TableIntentKind.ReleaseControl, Owner());
            Assert.True(duplicate.Accepted);
            Assert.Equal(TableInteractionCodes.AlreadyPending, duplicate.Code);

            TableInteractionOutcome opposite = state.BuildIntent(TableIntentKind.ClaimControl, Owner());
            Assert.False(opposite.Accepted);
            Assert.Equal(TableCommandIntentFactory.ControlAlreadyHuman, opposite.Code);
            Assert.Single(new[] { state.Pending });
        }

        [Fact]
        public void ConfirmedControlChangeClearsOldSelectionAndPending()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            PlayerTableViewState human = View(clock);
            state.ApplyConfirmedView(human);
            Assert.True(state.SelectAction(human.Own!.Decision!.Candidates[0]).Accepted);
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);
            Assert.NotNull(state.Pending);

            PlayerSnapshot automated = P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                controlledByComputer: true,
                controlEpoch: 8,
                controlVersion: 2);
            PlayerTableViewState next = View(clock, automated);
            TableInteractionOutcome applied = state.ApplyConfirmedView(next);

            Assert.True(applied.Accepted);
            Assert.Equal(TableSelectionKind.None, state.Selection.Kind);
            Assert.Null(state.Pending);
        }

        [Fact]
        public void UnknownControlResultRemainsPendingAndIsNotRetried()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = State(clock, out _);
            state.ApplyConfirmedView(View(clock));
            Assert.True(state.BuildIntent(TableIntentKind.ReleaseControl, Owner()).Accepted);

            Assert.True(state.MarkDeliveryUnknown().Accepted);
            Assert.Equal(TablePendingOutcome.Unknown, state.Pending!.LastOutcome);
            Assert.True(state.Pending.NeedsReconciliation);

            TableInteractionOutcome duplicate = state.BuildIntent(TableIntentKind.ReleaseControl, Owner());
            Assert.True(duplicate.Accepted);
            Assert.Equal(TableInteractionCodes.AlreadyPending, duplicate.Code);
        }
    }
}
