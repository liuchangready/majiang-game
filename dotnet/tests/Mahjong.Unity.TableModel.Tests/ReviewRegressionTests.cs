using System;
using System.Collections.Generic;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
using Xunit;
using DiscardView = Mahjong.Contracts.Wire.DiscardView;
using MeldView = Mahjong.Contracts.Wire.MeldView;
using ConcealedMeldView = Mahjong.Contracts.Wire.ConcealedMeldView;

namespace Mahjong.Unity.TableModel.Tests
{
    /// <summary>
    /// Formal regressions for the independent R1 review probes. These are the
    /// long-term equivalents of the ignored probe program; no production or
    /// shared contract is changed to make them pass.
    /// </summary>
    public sealed class ReviewRegressionTests
    {
        private static readonly RequestId Request = new RequestId("review-request");
        private static readonly RequestScopeId Scope = new RequestScopeId(P43TestData.ScopeId);

        private static TableCommandOwnerContext Owner(string id = "review-request")
        {
            return new TableCommandOwnerContext(new RequestId(id), Scope);
        }

        private static PlayerTableViewState Project(
            ManualTableClock clock,
            PlayerSnapshot? snapshot = null,
            long generation = 1,
            int seat = 0,
            ulong bindingEpoch = P43TestData.BindingEpoch)
        {
            return new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(
                    snapshot ?? P43TestData.PlayerSnapshot(seat: seat),
                    generation: generation,
                    seat: seat,
                    bindingEpoch: bindingEpoch));
        }

        private static PlayerTableViewState SpectatorView(ManualTableClock clock)
        {
            return new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(
                    null,
                    P43TestData.PublicSnapshot(),
                    seat: -1,
                    role: ProtocolRole.Spectator));
        }

        private static PlayerTableViewState ActivationView(ManualTableClock clock, long generation = 1)
        {
            PlayerSnapshot source = P43TestData.PlayerSnapshot();
            source.PrivateView.Cheat.SleeveBloom = null;
            return Project(clock, source, generation: generation);
        }

        private static ConfirmedAnimationContext Context(PlayerTableViewState view)
        {
            PlayerTableIdentity id = view.Identity;
            return new ConfirmedAnimationContext(
                id.Generation,
                id.SessionId,
                id.RoomId,
                id.RoundId,
                id.BindingEpoch,
                id.StateVersion,
                id.EventSeq,
                id.StreamVersion,
                id.ProjectionSeq,
                id.Role,
                id.Seat,
                id.ScopeId,
                id.IsPrivateAuthorized);
        }

        private static ConfirmedVisibleEvent Event(
            ulong seq,
            uint ordinal,
            ConfirmedVisibleEventKind kind,
            ConfirmedEventVisibility visibility,
            int ownerSeat = -1,
            int seat = -1,
            uint? tileId = null)
        {
            return new ConfirmedVisibleEvent(seq, ordinal, kind, visibility, ownerSeat, seat, tileId);
        }

        private static PlayerSnapshot WithDiscard(PlayerSnapshot source, uint tile, ulong stateVersion)
        {
            PlayerSnapshot next = source.Clone();
            next.StateVersion = stateVersion;
            next.EventSeq = 100 + stateVersion;
            next.PublicView.Board.Players[2].Discards.Add(new DiscardView
            {
                TileId = tile,
                TurnNumber = (uint)stateVersion
            });
            return next;
        }

        private static void AddMeld(PlayerSnapshot snapshot, int seat, MeldType type, int fromSeat, uint calledTile, params uint[] tiles)
        {
            var meld = new MeldView
            {
                Type = type,
                FromSeat = fromSeat,
                CalledTileId = calledTile
            };
            for (int i = 0; i < tiles.Length; i++)
            {
                meld.TileIds.Add(tiles[i]);
            }

            snapshot.PublicView.Board.Players[seat].Melds.Add(meld);
        }

        [Fact]
        public void D01_BaselineResetInvalidatesViewUntilNewIdentityArrives()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(ActivationView(clock));

            state.OnBaselineReset(2);

            Assert.Null(state.View);
            TableInteractionOutcome oldIntent = state.BuildIntent(TableIntentKind.SubmitSleeveBloom, Owner());
            Assert.False(oldIntent.Accepted);
            Assert.Equal(TableInteractionCodes.NoConfirmedView, oldIntent.Code);

            Assert.True(state.ApplyConfirmedView(ActivationView(clock, generation: 2)).Accepted);
            Assert.True(state.BuildIntent(TableIntentKind.SubmitSleeveBloom, Owner()).Accepted);
        }

        [Fact]
        public void D01_IdentitySwitchInvalidatesOldViewAndRejectsOldSeat()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState expected = Project(clock, generation: 2, seat: 1);
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(Project(clock, generation: 2, seat: 0));

            state.OnIdentityChanged(expected.Identity);

            Assert.Null(state.View);
            Assert.False(state.BuildIntent(TableIntentKind.SubmitSleeveBloom, Owner()).Accepted);
            Assert.False(state.ApplyConfirmedView(Project(clock, generation: 2, seat: 0)).Accepted);
            Assert.True(state.ApplyConfirmedView(Project(clock, generation: 2, seat: 1)).Accepted);
            Assert.Equal(1, state.View!.Identity.Seat);
        }

        [Fact]
        public void D01_LateOlderGenerationCannotRollBackAcceptedInteraction()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(Project(clock, generation: 2, seat: 1));

            TableInteractionOutcome stale = state.ApplyConfirmedView(Project(clock, generation: 1, seat: 0));

            Assert.False(stale.Accepted);
            Assert.Equal(TableInteractionCodes.StaleGeneration, stale.Code);
            Assert.Equal(2, state.View!.Identity.Generation);
            Assert.Equal(1, state.View.Identity.Seat);
        }

        [Fact]
        public void D01_OldBindingAndOldStateCannotReplaceCurrentView()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(Project(clock, generation: 2, seat: 0));

            TableInteractionOutcome oldBinding = state.ApplyConfirmedView(
                Project(clock, generation: 2, seat: 0, bindingEpoch: P43TestData.BindingEpoch - 1));
            Assert.Equal(TableInteractionCodes.StaleBinding, oldBinding.Code);

            PlayerSnapshot older = P43TestData.PlayerSnapshot(stateVersion: 4, eventSeq: 104);
            TableInteractionOutcome oldState = state.ApplyConfirmedView(Project(clock, older, generation: 2, seat: 0));
            Assert.Equal(TableInteractionCodes.StaleState, oldState.Code);
            Assert.Equal(5UL, state.View!.Identity.StateVersion);
        }

        [Fact]
        public void D01_ScopeOwnerAndConnectionChangesRequireExplicitIdentity()
        {
            var clock = new ManualTableClock();
            TableInteractionState state = new TableInteractionState(new TableCommandIntentFactory(clock));
            state.ApplyConfirmedView(Project(clock, generation: 2, seat: 0));

            PlayerTableViewState ownerChange = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(P43TestData.PlayerSnapshot(), generation: 2, seat: 0, ownerEpoch: 4));
            Assert.Equal(TableInteractionCodes.IdentityMismatch, state.ApplyConfirmedView(ownerChange).Code);

            PlayerTableViewState connectionChange = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(P43TestData.PlayerSnapshot(), generation: 2, seat: 0, connectionEpoch: 2));
            Assert.Equal(TableInteractionCodes.IdentityMismatch, state.ApplyConfirmedView(connectionChange).Code);

            PlayerTableViewState scopeChange = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(P43TestData.PlayerSnapshot(), generation: 2, seat: 0, scopeId: "scope-next"));
            Assert.Equal(TableInteractionCodes.IdentityMismatch, state.ApplyConfirmedView(scopeChange).Code);

            state.OnIdentityChanged(ownerChange.Identity);
            Assert.True(state.ApplyConfirmedView(ownerChange).Accepted);
            Assert.Equal(4UL, state.View!.Identity.OwnerEpoch);
        }

        [Fact]
        public void D02_CachedProjectionDoesNotRenewAnyCountdown()
        {
            var clock = new ManualTableClock();
            var mapper = new PlayerTableProjectionMapper(clock);
            SessionViewState source = P43TestData.View(P43TestData.PlayerSnapshot());
            PlayerTableViewState first = mapper.Project(source);

            Assert.Equal(8000L, first.Own!.Decision!.Countdown.RemainingMilliseconds(0));
            Assert.Equal(5000L, first.Own.Cheat!.ImmediateSleeveBloom!.Countdown!.RemainingMilliseconds(0));
            Assert.Equal(7000L, first.Own.Cheat.OffTurnSleeveBloom!.Countdown!.RemainingMilliseconds(0));
            Assert.Equal(6000L, first.Own.Cheat.BeamSwap!.Countdown!.RemainingMilliseconds(0));
            Assert.Equal(3000L, first.Own.Cheat.Preparation!.Countdown.RemainingMilliseconds(0));
            Assert.Equal(8000L, first.Clock(0)!.DecisionDeadline!.RemainingMilliseconds(0));

            clock.Advance(9000);
            PlayerTableViewState second = mapper.Project(source);

            Assert.True(second.Own!.Decision!.Countdown.IsExpired(9000));
            Assert.True(second.Own.Cheat!.ImmediateSleeveBloom!.Countdown!.IsExpired(9000));
            Assert.True(second.Own.Cheat.OffTurnSleeveBloom!.Countdown!.IsExpired(9000));
            Assert.True(second.Own.Cheat.BeamSwap!.Countdown!.IsExpired(9000));
            Assert.True(second.Own.Cheat.Preparation!.Countdown.IsExpired(9000));
            Assert.True(second.Clock(0)!.DecisionDeadline!.IsExpired(9000));
        }

        [Fact]
        public void D02_NewConfirmationWithSameStateVersionRefreshesAnchor()
        {
            var clock = new ManualTableClock();
            var mapper = new PlayerTableProjectionMapper(clock);
            mapper.Project(P43TestData.View(P43TestData.PlayerSnapshot(projectionSeq: 10)));

            clock.Advance(9000);
            PlayerTableViewState next = mapper.Project(
                P43TestData.View(P43TestData.PlayerSnapshot(stateVersion: 5, eventSeq: 105, projectionSeq: 11)));

            Assert.Equal(8000L, next.Own!.Decision!.Countdown.RemainingMilliseconds(9000));
            Assert.False(next.Own.Decision.Countdown.IsExpired(9000));
            Assert.Equal(5000L, next.Own.Cheat!.ImmediateSleeveBloom!.Countdown!.RemainingMilliseconds(9000));
            Assert.Equal(7000L, next.Own.Cheat.OffTurnSleeveBloom!.Countdown!.RemainingMilliseconds(9000));
            Assert.Equal(6000L, next.Own.Cheat.BeamSwap!.Countdown!.RemainingMilliseconds(9000));
            Assert.Equal(3000L, next.Own.Cheat.Preparation!.Countdown.RemainingMilliseconds(9000));
            Assert.Equal(8000L, next.Clock(0)!.DecisionDeadline!.RemainingMilliseconds(9000));
        }

        [Fact]
        public void D02_NewIdentityDoesNotReuseOldAnchor()
        {
            var clock = new ManualTableClock();
            var mapper = new PlayerTableProjectionMapper(clock);
            mapper.Project(P43TestData.View(P43TestData.PlayerSnapshot()));

            clock.Advance(9000);
            PlayerTableViewState next = mapper.Project(
                P43TestData.View(
                    P43TestData.PlayerSnapshot(sessionId: "session-next"),
                    sessionId: "session-next",
                    bindingEpoch: P43TestData.BindingEpoch + 1,
                    generation: 2));

            Assert.Equal(8000L, next.Own!.Decision!.Countdown.RemainingMilliseconds(9000));
        }

        [Fact]
        public void D03_AddedKanIsConfirmedUpgradeNotRollback()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot s1 = P43TestData.PlayerSnapshot();
            AddMeld(s1, 3, MeldType.Pon, 2, 80, 80, 81, 82);
            PlayerSnapshot s2 = s1.Clone();
            s2.StateVersion = 6;
            s2.EventSeq = 106;
            s2.PublicView.Board.Players[3].Melds[0].Type = MeldType.AddedKan;
            s2.PublicView.Board.Players[3].Melds[0].TileIds.Add(83);

            PlayerTableViewState before = Project(clock, s1);
            PlayerTableViewState after = Project(clock, s2);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(before);

            TableAnimationResult result = queue.AcceptConfirmedDifference(before, after, false);
            Assert.True(result.Accepted);
            Assert.Equal(1, result.Enqueued);
            Assert.True(queue.TryDequeue(out TableAnimationItem? item));
            Assert.Equal(TableAnimationKind.Meld, item!.Kind);
        }

        [Theory]
        [InlineData(MeldType.Chi, 3)]
        [InlineData(MeldType.Pon, 3)]
        [InlineData(MeldType.OpenKan, 4)]
        public void D03_CalledDiscardMayMoveToConfirmedMeld(MeldType type, int tileCount)
        {
            var clock = new ManualTableClock();
            PlayerSnapshot s1 = P43TestData.PlayerSnapshot();
            s1.PublicView.Board.Players[2].Discards.Add(new DiscardView { TileId = 80, TurnNumber = 2 });
            PlayerSnapshot s2 = s1.Clone();
            s2.StateVersion = 6;
            s2.EventSeq = 106;
            s2.PublicView.Board.Players[2].Discards.RemoveAt(1);
            uint[] tiles = tileCount == 4
                ? new uint[] { 80, 81, 82, 83 }
                : new uint[] { 80, 81, 82 };
            AddMeld(s2, 3, type, 2, 80, tiles);

            var queue = new ConfirmedAnimationQueue();
            PlayerTableViewState before = Project(clock, s1);
            PlayerTableViewState after = Project(clock, s2);
            queue.AcceptConfirmedBaseline(before);

            TableAnimationResult result = queue.AcceptConfirmedDifference(before, after, false);
            Assert.True(result.Accepted, result.Code);
            Assert.True(result.Enqueued >= 1);
        }

        [Fact]
        public void D03_RemovalWithoutConfirmedDestinationStillRejectsAndClears()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot s1 = P43TestData.PlayerSnapshot();
            s1.PublicView.Board.Players[2].Discards.Add(new DiscardView { TileId = 80, TurnNumber = 2 });
            PlayerSnapshot s2 = s1.Clone();
            s2.StateVersion = 6;
            s2.EventSeq = 106;
            s2.PublicView.Board.Players[2].Discards.RemoveAt(1);

            var queue = new ConfirmedAnimationQueue();
            PlayerTableViewState before = Project(clock, s1);
            PlayerTableViewState after = Project(clock, s2);
            queue.AcceptConfirmedBaseline(before);
            Assert.Equal(TableAnimationCodes.NonMonotonicSnapshot,
                queue.AcceptConfirmedDifference(before, after, false).Code);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public void D03_ConfirmedTransferThenNextDifferenceStillPlays()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot s1 = P43TestData.PlayerSnapshot();
            s1.PublicView.Board.Players[2].Discards.Add(new DiscardView { TileId = 80, TurnNumber = 2 });
            PlayerSnapshot s2 = s1.Clone();
            s2.StateVersion = 6;
            s2.EventSeq = 106;
            s2.PublicView.Board.Players[2].Discards.RemoveAt(1);
            AddMeld(s2, 3, MeldType.Pon, 2, 80, 80, 81, 82);
            PlayerSnapshot s3 = WithDiscard(s2, 84, 7);

            var queue = new ConfirmedAnimationQueue();
            PlayerTableViewState v1 = Project(clock, s1);
            PlayerTableViewState v2 = Project(clock, s2);
            PlayerTableViewState v3 = Project(clock, s3);
            queue.AcceptConfirmedBaseline(v1);
            Assert.True(queue.AcceptConfirmedDifference(v1, v2, false).Accepted);
            queue.Drain();
            TableAnimationResult next = queue.AcceptConfirmedDifference(v2, v3, false);
            Assert.True(next.Accepted, next.Code);
            Assert.Equal(1, next.Enqueued);
        }

        [Fact]
        public void D04_OwnerClosedKanAnimationRemainsPrivate()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot s1 = P43TestData.PlayerSnapshot();
            PlayerSnapshot s2 = s1.Clone();
            s2.StateVersion = 6;
            s2.EventSeq = 106;
            var ownerKan = new MeldView
            {
                Type = MeldType.ClosedKan,
                FromSeat = -1
            };
            ownerKan.TileIds.Add(0);
            ownerKan.TileIds.Add(1);
            ownerKan.TileIds.Add(2);
            ownerKan.TileIds.Add(3);
            s2.PrivateView.OwnerMelds.Add(ownerKan);
            // The mapper's own meld list is the owner list; remove the same
            // identities from the hand so this is a confirmed owner transition.
            for (uint tile = 0; tile < 4; tile++)
            {
                s2.PrivateView.ClosedTiles.Remove(tile);
            }

            s2.PublicView.Board.Players[0].HandCount = 10;
            s2.PublicView.Board.Players[0].ConcealedMelds.Add(new ConcealedMeldView
            {
                Type = MeldType.ClosedKan,
                TileCount = 4,
                FromSeat = -1
            });

            var queue = new ConfirmedAnimationQueue();
            PlayerTableViewState before = Project(clock, s1);
            PlayerTableViewState after = Project(clock, s2);
            queue.AcceptConfirmedBaseline(before);
            TableAnimationResult result = queue.AcceptConfirmedDifference(before, after, false);
            Assert.True(result.Accepted, result.Code + " gaps=" + string.Join(",", after.Gaps) + " own=" + (after.Own != null));
            Assert.True(result.Enqueued == 1, "enqueued=" + result.Enqueued + " code=" + result.Code + " own=" + (after.Own != null) + " gaps=" + string.Join(",", after.Gaps));
            Assert.True(queue.TryDequeue(out TableAnimationItem? item));
            Assert.True(item!.IsPrivate);
            Assert.Equal(0, item.OwnerSeat);
            Assert.Equal(0U, item.TileId);
            Assert.NotNull(item.Meld);
            Assert.Equal(new uint[] { 0, 1, 2, 3 }, item.Meld!.TileIds);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void D04_PlayerBaselineSeatBindsEventAudience(int seat)
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = Project(clock, seat: seat);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(view);
            ConfirmedAnimationContext context = Context(view);

            Assert.True(queue.AcceptConfirmedEvents(
                context,
                new[] { Event(106, 0, ConfirmedVisibleEventKind.TileDrawn, ConfirmedEventVisibility.Private, ownerSeat: seat, seat: seat, tileId: 80) },
                TableAnimationAudience.Player(seat)).Accepted);

            int other = (seat + 1) % 4;
            Assert.Equal(
                TableAnimationCodes.AudienceMismatch,
                queue.AcceptConfirmedEvents(
                    context,
                    new[] { Event(107, 0, ConfirmedVisibleEventKind.TileDrawn, ConfirmedEventVisibility.Private, ownerSeat: seat, seat: seat, tileId: 81) },
                    TableAnimationAudience.Player(other)).Code);
        }

        [Fact]
        public void D04_PlayerBaselineRejectsOtherSeatAudience()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = Project(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(view);

            TableAnimationResult result = queue.AcceptConfirmedEvents(
                Context(view),
                new[] { Event(106, 0, ConfirmedVisibleEventKind.TileDrawn, ConfirmedEventVisibility.Private, 1, 1, 80) },
                TableAnimationAudience.Player(1));

            Assert.False(result.Accepted);
            Assert.Equal(TableAnimationCodes.AudienceMismatch, result.Code);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public void D04_SpectatorBaselineRejectsPlayerAudienceButAcceptsPublic()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = SpectatorView(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(view);
            ConfirmedAnimationContext context = Context(view);

            TableAnimationResult privateAttempt = queue.AcceptConfirmedEvents(
                context,
                new[] { Event(106, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 0, tileId: 80) },
                TableAnimationAudience.Player(0));
            Assert.Equal(TableAnimationCodes.AudienceMismatch, privateAttempt.Code);

            TableAnimationResult publicEvent = queue.AcceptConfirmedEvents(
                context,
                new[] { Event(106, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 0, tileId: 80) },
                TableAnimationAudience.Spectator());
            Assert.True(publicEvent.Accepted);
        }

        [Fact]
        public void D04_PrivateBatchIsAtomicWhenOneEventIsUnauthorized()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = Project(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(view);

            TableAnimationResult result = queue.AcceptConfirmedEvents(
                Context(view),
                new[]
                {
                    Event(106, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 0, tileId: 80),
                    Event(106, 1, ConfirmedVisibleEventKind.TileDrawn, ConfirmedEventVisibility.Private, ownerSeat: 1, seat: 1, tileId: 81)
                },
                TableAnimationAudience.Player(0));

            Assert.False(result.Accepted);
            Assert.Equal(TableAnimationCodes.PrivateNotAuthorized, result.Code);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public void D04_RoleChangeAfterBaselineRejectsOldPrivateBatch()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState player = Project(clock);
            PlayerTableViewState spectator = SpectatorView(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(player);
            queue.AcceptConfirmedBaseline(spectator);

            TableAnimationResult result = queue.AcceptConfirmedEvents(
                Context(player),
                new[] { Event(106, 0, ConfirmedVisibleEventKind.SleeveBloomResolved, ConfirmedEventVisibility.Private, ownerSeat: 0, seat: 0) },
                TableAnimationAudience.Player(0));

            Assert.False(result.Accepted);
            Assert.Equal(TableAnimationCodes.AudienceMismatch, result.Code);
        }

        [Fact]
        public void D05_StalePreviousSnapshotDoesNotReplay()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot s1 = P43TestData.PlayerSnapshot();
            PlayerSnapshot s2 = WithDiscard(s1, 80, 6);
            PlayerSnapshot s3 = WithDiscard(s2, 81, 7);
            PlayerTableViewState first = Project(clock, s1);
            PlayerTableViewState second = Project(clock, s2);
            PlayerTableViewState third = Project(clock, s3);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);
            Assert.True(queue.AcceptConfirmedDifference(first, second, false).Accepted);
            queue.Drain();

            TableAnimationResult result = queue.AcceptConfirmedDifference(first, third, false);
            Assert.False(result.Accepted);
            Assert.Equal(TableAnimationCodes.StalePrevious, result.Code);
            Assert.Equal(0, result.Enqueued);
        }

        [Fact]
        public void D05_PreviousIdentityIsChecked()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = Project(clock);
            PlayerTableViewState second = Project(clock, WithDiscard(P43TestData.PlayerSnapshot(), 80, 6));
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);

            TableAnimationResult result = queue.AcceptConfirmedDifference(
                Project(clock, generation: 0),
                second,
                false);
            Assert.False(result.Accepted);
            Assert.Equal(TableAnimationCodes.StalePrevious, result.Code);
        }

        [Fact]
        public void D05_SnapshotDoesNotMarkSameSeqTypedEventAsPlayed()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = Project(clock);
            PlayerTableViewState second = Project(clock, WithDiscard(P43TestData.PlayerSnapshot(), 80, 6));
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);
            Assert.True(queue.AcceptConfirmedDifference(first, second, false).Accepted);
            queue.Drain();

            TableAnimationResult result = queue.AcceptConfirmedEvents(
                Context(second),
                new[] { Event(106, 0, ConfirmedVisibleEventKind.RiichiDeclared, ConfirmedEventVisibility.Public, seat: 2) },
                TableAnimationAudience.Player(0));
            Assert.True(result.Accepted);
            Assert.Equal(1, result.Enqueued);
        }

        [Fact]
        public void D05_EventThenSnapshotDoesNotDuplicateDiscard()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState first = Project(clock);
            PlayerTableViewState second = Project(clock, WithDiscard(P43TestData.PlayerSnapshot(), 80, 6));
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(first);

            Assert.True(queue.AcceptConfirmedEvents(
                Context(second),
                new[] { Event(106, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 2, tileId: 80) },
                TableAnimationAudience.Player(0)).Accepted);
            TableAnimationResult result = queue.AcceptConfirmedDifference(first, second, false);

            Assert.True(result.Accepted, result.Code);
            Assert.Equal(0, result.Enqueued);
            Assert.Equal(1, queue.PendingCount);
        }

        [Fact]
        public void D05_EventOrdinalsAndDuplicatesRemainDistinctFromSnapshotState()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = Project(clock);
            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(view);
            ConfirmedAnimationContext context = Context(view);

            Assert.Equal(2, queue.AcceptConfirmedEvents(
                context,
                new[]
                {
                    Event(106, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 0, tileId: 80),
                    Event(106, 1, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 1, tileId: 81)
                },
                TableAnimationAudience.Player(0)).Enqueued);
            Assert.Equal(0, queue.AcceptConfirmedEvents(
                context,
                new[] { Event(106, 0, ConfirmedVisibleEventKind.TileDiscarded, ConfirmedEventVisibility.Public, seat: 0, tileId: 80) },
                TableAnimationAudience.Player(0)).Enqueued);
        }
    }
}
