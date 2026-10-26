using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// N-1a targeted tests. Fixtures place all 136 physical tiles explicitly so every accepted
    /// decision can be checked for physical/score/stick conservation, not only for its enum case.
    /// </summary>
    public class RoundDecisionTests
    {
        private static TileId Man(int rank, int copy = 0) => TileId.FromTypeIndex(rank - 1, copy);
        private static TileId Pin(int rank, int copy = 0) => TileId.FromTypeIndex(17 + rank, copy);
        private static TileId Sou(int rank, int copy = 0) => TileId.FromTypeIndex(8 + rank, copy);
        private static TileId Wind(int rank, int copy = 0) => TileId.FromTypeIndex(26 + rank, copy);
        private static TileId Dragon(int rank, int copy = 0) => TileId.FromTypeIndex(30 + rank, copy);

        private static RoundOutcome RonOutcome(int winner, int loser) => RoundOutcome.Ron(winner, loser);

        private static RoundSettlement Settlement(
            string resultId,
            RoundOutcome outcome,
            long[] finalScores,
            int remainingSticks = 0)
        {
            return new RoundSettlement(
                resultId,
                outcome,
                finalScores,
                remainingSticks,
                new PersistedUtcTime(P13.FixedUtc),
                1000L);
        }

        private static void AssertConserved(RoundStateMachine round)
        {
            ConservationReport report = TilePositionMap.Validate(round.Tiles);
            Assert.True(report.IsValid, report.ToString());
            Assert.Equal(0, report.UnassignedCount);
            Assert.Equal(136, report.AssignedCount);
            long total = 0;
            for (int i = 0; i < 4; i++)
            {
                total += round.Scores[i];
            }

            Assert.Equal(100000L, total + (round.RiichiSticks * 1000L));
        }

        private static void AssertRejectedWithoutMutation(
            RoundStateMachine round,
            RoundCommand command,
            RoundTransitionErrorCode expected)
        {
            long version = round.Version;
            int events = round.Events.Count;
            ulong hash = round.Tiles.StateHash;
            ulong fingerprint = round.SourceFingerprint;
            long[] scores = round.Scores.ToArray();
            int sticks = round.RiichiSticks;
            int melds = round.Melds.Count;

            RoundTransition transition = round.Execute(command);

            Assert.False(transition.IsAccepted);
            Assert.Equal(expected, transition.Error!.Code);
            Assert.Empty(transition.Events);
            Assert.Equal(version, round.Version);
            Assert.Equal(events, round.Events.Count);
            Assert.Equal(hash, round.Tiles.StateHash);
            Assert.Equal(fingerprint, round.SourceFingerprint);
            Assert.Equal(scores, round.Scores.ToArray());
            Assert.Equal(sticks, round.RiichiSticks);
            Assert.Equal(melds, round.Melds.Count);
            AssertConserved(round);
        }

        [Fact]
        public void ChiAcceptsOnlyTheShimochaAndMovesTheExactPhysicalTiles()
        {
            var table = new N1aTable();
            table.Hand(0, Man(1), Man(2)).Drawn(0, Man(3));
            table.Hand(1, Man(1, 1), Man(2, 1));
            RoundStateMachine round = table.Build("n1a-chi");

            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-chi", 0, Man(3), new[] { 1 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());
            Assert.Equal(RoundPhase.ResponseWindow, window.State!.Phase);
            Assert.Equal(0, window.State.CurrentSeat);

            RoundTransition chi = window.State.Execute(
                RoundCommand.Chi("n1a-chi", 1, Man(3), Man(1, 1), Man(2, 1)));
            Assert.True(chi.IsAccepted, chi.Error?.ToString());
            Assert.Equal(RoundPhase.Discard, chi.State!.Phase);
            Assert.Equal(1, chi.State.CurrentSeat);
            Assert.Null(chi.State.Tiles.DrawnTile);
            Assert.Single(chi.State.Melds);
            RoundMeld meld = chi.State.Melds[0];
            Assert.Equal(RoundMeldKind.Chi, meld.Kind);
            Assert.Equal(1, meld.Seat);
            Assert.Equal(0, meld.FromSeat);
            Assert.Equal(Man(3), meld.CalledTile);
            Assert.Equal(3, chi.State.Tiles.CountAt(TileZone.Meld, 1));
            Assert.Equal(11, chi.State.Tiles.CountAt(TileZone.Hand, 1));
            Assert.Contains(chi.Events, e => e.Type == RoundEventType.ChiDeclared && e.Seat == 1);
            Assert.Equal(2L, chi.State.Version);
            AssertConserved(chi.State);
        }

        [Fact]
        public void ChiRejectsNonShimochaAndRiichiSeatWithoutMutation()
        {
            var table = new N1aTable();
            table.Hand(0, Man(1), Man(2)).Drawn(0, Man(3));
            table.Hand(2, Man(1, 1), Man(2, 1));
            RoundStateMachine round = table.Build("n1a-chi-seat");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-chi-seat", 0, Man(3), new[] { 2 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());

            AssertRejectedWithoutMutation(
                window.State!,
                RoundCommand.Chi("n1a-chi-seat", 2, Man(3), Man(1, 1), Man(2, 1)),
                RoundTransitionErrorCode.MeldNotAllowed);
        }

        [Fact]
        public void PonResolvesOnlyAfterEveryEligibleSeatResponds()
        {
            var table = new N1aTable();
            table.Hand(0).Drawn(0, Dragon(1, 3));
            table.Hand(1, Dragon(1), Dragon(1, 1));
            RoundStateMachine round = table.Build("n1a-pon");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-pon", 0, Dragon(1, 3), new[] { 1, 2 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());

            RoundTransition pon = window.State!.Execute(
                RoundCommand.Pon("n1a-pon", 1, Dragon(1, 3), Dragon(1), Dragon(1, 1)));
            Assert.True(pon.IsAccepted, pon.Error?.ToString());
            Assert.Equal(RoundPhase.ResponseWindow, pon.State!.Phase);
            Assert.Single(pon.State.Window!.Claims);
            Assert.Empty(pon.State.Melds);
            Assert.Equal(0, pon.State.CurrentSeat);

            RoundTransition complete = pon.State.Execute(RoundCommand.Pass("n1a-pon", 2));
            Assert.True(complete.IsAccepted, complete.Error?.ToString());
            Assert.Equal(RoundPhase.Discard, complete.State!.Phase);
            Assert.Equal(1, complete.State.CurrentSeat);
            Assert.Single(complete.State.Melds);
            Assert.Equal(RoundMeldKind.Pon, complete.State.Melds[0].Kind);
            Assert.Equal(3, complete.State.Tiles.CountAt(TileZone.Meld, 1));
            Assert.Contains(complete.Events, e => e.Type == RoundEventType.PonDeclared && e.Seat == 1);
            AssertConserved(complete.State);
        }

        [Fact]
        public void OpenKanClaimsItsOwnTilesDrawsRinshanAndDefersKanDoraToTheNextDiscard()
        {
            var table = new N1aTable();
            table.Hand(0).Drawn(0, Dragon(1, 3));
            table.Hand(1, Dragon(1), Dragon(1, 1), Dragon(1, 2));
            RoundStateMachine round = table.Build("n1a-open-kan");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-open-kan", 0, Dragon(1, 3), new[] { 1 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());

            RoundTransition kan = window.State!.Execute(
                RoundCommand.OpenKan("n1a-open-kan", 1, Dragon(1, 3), Dragon(1), Dragon(1, 1), Dragon(1, 2)));
            Assert.True(kan.IsAccepted, kan.Error?.ToString());
            Assert.Equal(RoundPhase.CheatWindow, kan.State!.Phase);
            Assert.Equal(1, kan.State.CurrentSeat);
            Assert.Equal(1, kan.State.Tiles.KanCount);
            Assert.Equal(1, kan.State.Tiles.RinshanPointer);
            Assert.Equal(1, kan.State.PendingKanDoraCount);
            Assert.Single(kan.State.Melds);
            Assert.Equal(RoundMeldKind.OpenKan, kan.State.Melds[0].Kind);
            Assert.DoesNotContain(kan.Events, e => e.Type == RoundEventType.DoraRevealed);
            Assert.Contains(kan.Events, e => e.Type == RoundEventType.KanDeclared && e.Seat == 1);
            Assert.Contains(kan.Events, e => e.Type == RoundEventType.RinshanDrawn && e.Seat == 1);
            AssertConserved(kan.State);

            TileId rinshan = kan.State.Tiles.DrawnTile!.Value.Tile;
            RoundTransition discard = kan.State.Execute(
                RoundCommand.Discard("n1a-open-kan", 1, rinshan));
            Assert.True(discard.IsAccepted, discard.Error?.ToString());
            Assert.Equal(0, discard.State!.PendingKanDoraCount);
            Assert.Equal(2, discard.State.Tiles.DoraIndicators.Count);
            Assert.Contains(discard.Events, e => e.Type == RoundEventType.DoraRevealed);
            AssertConserved(discard.State);
        }

        [Fact]
        public void ClosedKanRevealsItsDoraImmediatelyAndKeepsTheRinshanDraw()
        {
            var table = new N1aTable();
            table.Hand(0, Dragon(1), Dragon(1, 1), Dragon(1, 2)).Drawn(0, Dragon(1, 3));
            RoundStateMachine round = table.Build("n1a-closed-kan");
            ulong before = round.Tiles.StateHash;

            RoundTransition kan = round.Execute(
                RoundCommand.ClosedKan("n1a-closed-kan", 0, Dragon(1), Dragon(1, 1), Dragon(1, 2), Dragon(1, 3)));

            Assert.True(kan.IsAccepted, kan.Error?.ToString());
            Assert.NotEqual(before, kan.State!.Tiles.StateHash);
            Assert.Equal(RoundPhase.CheatWindow, kan.State.Phase);
            Assert.Equal(1, kan.State.Tiles.KanCount);
            Assert.Equal(1, kan.State.Tiles.RinshanPointer);
            Assert.Equal(0, kan.State.PendingKanDoraCount);
            Assert.Equal(2, kan.State.Tiles.DoraIndicators.Count);
            Assert.Single(kan.State.Melds);
            Assert.Equal(RoundMeldKind.ClosedKan, kan.State.Melds[0].Kind);
            Assert.Contains(kan.Events, e => e.Type == RoundEventType.DoraRevealed);
            Assert.Contains(kan.Events, e => e.Type == RoundEventType.KanDeclared);
            Assert.Contains(kan.Events, e => e.Type == RoundEventType.RinshanDrawn);
            AssertConserved(kan.State);
        }

        [Fact]
        public void ClosedKanAfterRiichiMustPreserveWaitsAndDecomposition()
        {
            var waiting = new[]
            {
                Dragon(1), Dragon(1, 1), Dragon(1, 2),
                Man(2), Man(3), Man(4),
                Pin(6), Pin(7), Pin(8),
                Sou(2), Sou(2, 1), Sou(5), Sou(6)
            };
            var table = new N1aTable();
            table.Hand(0, waiting).Drawn(0, Wind(4));
            table.WallPrefix(Man(9), Pin(9), Sou(9), Dragon(1, 3));
            RoundStateMachine round = table.Build("n1a-riichi-kan");

            RoundTransition riichi = round.Execute(
                RoundCommand.Riichi("n1a-riichi-kan", 0, Wind(4)));
            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());
            Assert.Equal(1, riichi.State!.RiichiSticks);
            Assert.True(riichi.State.RiichiDeclared[0]);

            RoundStateMachine current = riichi.State;
            for (int seat = 1; seat <= 3; seat++)
            {
                TileId drawn = current.Tiles.DrawnTile!.Value.Tile;
                RoundTransition discard = current.Execute(
                    RoundCommand.Discard("n1a-riichi-kan", seat, drawn));
                Assert.True(discard.IsAccepted, discard.Error?.ToString());
                current = discard.State!;
            }

            Assert.Equal(0, current.CurrentSeat);
            Assert.Equal(Dragon(1, 3), current.Tiles.DrawnTile!.Value.Tile);

            RoundTransition kan = current.Execute(
                RoundCommand.ClosedKan("n1a-riichi-kan", 0, Dragon(1), Dragon(1, 1), Dragon(1, 2), Dragon(1, 3)));
            Assert.True(kan.IsAccepted, kan.Error?.ToString());
            Assert.True(kan.State!.RiichiDeclared[0]);
            Assert.Equal(RoundMeldKind.ClosedKan, kan.State.Melds[0].Kind);
            AssertConserved(kan.State);
        }

        [Fact]
        public void ClosedKanAfterRiichiRejectsAStructuralChange()
        {
            // V4's canonical same-wait/changed-decomposition case: 666777888 + 56 + 11 with a
            // drawn fourth 6m. The wait set is unchanged, but the kan changes every regular
            // decomposition, so RiichiKanValidator must refuse it.
            var hand = new[]
            {
                Man(6), Man(6, 1), Man(6, 2),
                Man(7), Man(7, 1), Man(7, 2),
                Man(8), Man(8, 1), Man(8, 2),
                Sou(5), Sou(6), Pin(1), Pin(1, 1)
            };
            var table = new N1aTable();
            table.Hand(0, hand).Drawn(0, Wind(4));
            table.WallPrefix(Man(9), Pin(9), Sou(9), Man(6, 3));
            RoundStateMachine round = table.Build("n1a-riichi-kan-bad");

            RoundTransition riichi = round.Execute(RoundCommand.Riichi("n1a-riichi-kan-bad", 0, Wind(4)));
            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());
            RoundStateMachine current = riichi.State!;
            for (int seat = 1; seat <= 3; seat++)
            {
                TileId drawn = current.Tiles.DrawnTile!.Value.Tile;
                current = current.Execute(RoundCommand.Discard("n1a-riichi-kan-bad", seat, drawn)).State!;
            }

            Assert.Equal(Man(6, 3), current.Tiles.DrawnTile!.Value.Tile);
            AssertRejectedWithoutMutation(
                current,
                RoundCommand.ClosedKan(
                    "n1a-riichi-kan-bad",
                    0,
                    Man(6),
                    Man(6, 1),
                    Man(6, 2),
                    Man(6, 3)),
                RoundTransitionErrorCode.MeldNotAllowed);
        }

        [Fact]
        public void RiichiPaysOneThousandPointsAndAddsOneStickConservingTheTotal()
        {
            var table = new N1aTable();
            table.Hand(
                    0,
                    Man(1), Man(2), Man(3), Man(4), Man(5), Man(6), Man(7), Man(8), Man(9),
                    Pin(1), Pin(1, 1), Sou(5), Sou(6))
                .Drawn(0, Pin(3));
            RoundStateMachine round = table.Build("n1a-riichi");
            ulong before = round.Tiles.StateHash;

            RoundTransition riichi = round.Execute(
                RoundCommand.Riichi("n1a-riichi", 0, Pin(3), new[] { 1, 2, 3 }));

            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());
            Assert.Equal(RoundPhase.ResponseWindow, riichi.State!.Phase);
            Assert.Equal(24000L, riichi.State.Scores[0]);
            Assert.Equal(1, riichi.State.RiichiSticks);
            Assert.True(riichi.State.RiichiDeclared[0]);
            Assert.NotEqual(before, riichi.State.Tiles.StateHash);
            Assert.Equal(3, riichi.Events.Count);
            Assert.Equal(RoundEventType.RiichiDeclared, riichi.Events[0].Type);
            Assert.Equal(RoundEventType.TileDiscarded, riichi.Events[1].Type);
            Assert.Equal(RoundEventType.ResponseWindowOpened, riichi.Events[2].Type);
            Assert.Equal(1L, riichi.State.Version);
            AssertConserved(riichi.State);

            RoundStateMachine current = riichi.State;
            for (int seat = 1; seat <= 3; seat++)
            {
                Assert.True(current.Window!.HasResponded(seat) == false);
                RoundTransition pass = current.Execute(RoundCommand.Pass("n1a-riichi", seat));
                Assert.True(pass.IsAccepted, pass.Error?.ToString());
                current = pass.State!;
            }

            Assert.Equal(RoundPhase.CheatWindow, current.Phase);
            Assert.Equal(1, current.CurrentSeat);
            Assert.Equal(1, current.RiichiSticks);
            Assert.Equal(24000L, current.Scores[0]);
            AssertConserved(current);
        }

        [Fact]
        public void RiichiRejectsInsufficientPointsNonTenpaiAndOpenHands()
        {
            var low = new N1aTable();
            low.Hand(
                    0,
                    Man(1), Man(2), Man(3), Man(4), Man(5), Man(6), Man(7), Man(8), Man(9),
                    Pin(1), Pin(1, 1), Sou(5), Sou(6))
                .Drawn(0, Pin(3));
            RoundStateMachine lowRound = low.Build(
                "n1a-riichi-low",
                scores: new long[] { 500, 25000, 37250, 37250 });
            AssertRejectedWithoutMutation(
                lowRound,
                RoundCommand.Riichi("n1a-riichi-low", 0, Pin(3)),
                RoundTransitionErrorCode.RiichiInsufficientPoints);

            var notTenpai = new N1aTable();
            notTenpai.Hand(
                    0,
                    Man(1), Man(4), Man(7), Pin(1), Pin(4), Pin(7),
                    Sou(1), Sou(4), Sou(7), Man(2), Man(5), Man(8))
                .Drawn(0, Sou(9));
            RoundStateMachine notTenpaiRound = notTenpai.Build("n1a-riichi-notenpai");
            AssertRejectedWithoutMutation(
                notTenpaiRound,
                RoundCommand.Riichi("n1a-riichi-notenpai", 0, Sou(9)),
                RoundTransitionErrorCode.RiichiNotTenpai);

            var ponTable = new N1aTable();
            ponTable.Hand(0).Drawn(0, Dragon(1, 3));
            ponTable.Hand(1, Dragon(1), Dragon(1, 1));
            RoundStateMachine ponRound = ponTable.Build("n1a-riichi-open");
            RoundTransition window = ponRound.Execute(
                RoundCommand.Discard("n1a-riichi-open", 0, Dragon(1, 3), new[] { 1 }));
            RoundTransition pon = window.State!.Execute(
                RoundCommand.Pon("n1a-riichi-open", 1, Dragon(1, 3), Dragon(1), Dragon(1, 1)));
            Assert.True(pon.IsAccepted, pon.Error?.ToString());
            TileId openHandTile = pon.State!.Tiles.TilesInZone(TileZone.Hand, 1)[0];
            AssertRejectedWithoutMutation(
                pon.State,
                RoundCommand.Riichi("n1a-riichi-open", 1, openHandTile),
                RoundTransitionErrorCode.RiichiNotMenzen);
        }

        [Fact]
        public void RiichiLocksTheNextDiscardToTheDrawnTile()
        {
            var table = new N1aTable();
            table.Hand(
                    0,
                    Man(1), Man(2), Man(3), Man(4), Man(5), Man(6), Man(7), Man(8), Man(9),
                    Pin(1), Pin(1, 1), Sou(5), Sou(6))
                .Drawn(0, Pin(3));
            RoundStateMachine round = table.Build("n1a-riichi-lock");
            RoundTransition riichi = round.Execute(RoundCommand.Riichi("n1a-riichi-lock", 0, Pin(3)));
            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());

            RoundStateMachine current = riichi.State!;
            for (int seat = 1; seat <= 3; seat++)
            {
                current = current.Execute(
                    RoundCommand.Discard("n1a-riichi-lock", seat, current.Tiles.DrawnTile!.Value.Tile)).State!;
            }

            Assert.Equal(0, current.CurrentSeat);
            Assert.Equal(RoundPhase.CheatWindow, current.Phase);
            TileId drawn = current.Tiles.DrawnTile!.Value.Tile;
            TileId other = current.Tiles.TilesInZone(TileZone.Hand, 0).First(tile => tile != drawn);

            AssertRejectedWithoutMutation(
                current,
                RoundCommand.Discard("n1a-riichi-lock", 0, other),
                RoundTransitionErrorCode.RiichiDiscardLocked);

            RoundTransition locked = current.Execute(RoundCommand.Discard("n1a-riichi-lock", 0, drawn));
            Assert.True(locked.IsAccepted, locked.Error?.ToString());
            AssertConserved(locked.State!);
        }

        [Fact]
        public void LowPriorityFirstClaimCannotStealAnUnclosedWindow()
        {
            var table = new N1aTable();
            table.Hand(0, Man(1), Man(2)).Drawn(0, Man(3));
            table.Hand(1, Man(1, 1), Man(2, 1));
            table.Hand(2, Man(3, 1), Man(3, 2));
            RoundStateMachine round = table.Build("n1a-priority");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-priority", 0, Man(3), new[] { 1, 2 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());

            RoundTransition chi = window.State!.Execute(
                RoundCommand.Chi("n1a-priority", 1, Man(3), Man(1, 1), Man(2, 1)));
            Assert.True(chi.IsAccepted, chi.Error?.ToString());
            Assert.Equal(RoundPhase.ResponseWindow, chi.State!.Phase);
            Assert.Empty(chi.State.Melds);
            Assert.Equal(0, chi.State.CurrentSeat);

            RoundTransition pon = chi.State.Execute(
                RoundCommand.Pon("n1a-priority", 2, Man(3), Man(3, 1), Man(3, 2)));
            Assert.True(pon.IsAccepted, pon.Error?.ToString());
            Assert.Equal(RoundPhase.Discard, pon.State!.Phase);
            Assert.Equal(2, pon.State.CurrentSeat);
            Assert.Single(pon.State.Melds);
            Assert.Equal(RoundMeldKind.Pon, pon.State.Melds[0].Kind);
            Assert.Equal(2, pon.State.Melds[0].Seat);
            Assert.Equal(13, pon.State.Tiles.CountAt(TileZone.Hand, 1));
            Assert.Equal(0, pon.State.Tiles.CountAt(TileZone.Meld, 1));
            AssertConserved(pon.State);
        }

        [Fact]
        public void RonOutranksPonEvenWhenPonWasRecordedFirst()
        {
            var table = new N1aTable();
            table.Hand(0).Drawn(0, Dragon(1, 3));
            table.Hand(1, Dragon(1), Dragon(1, 1));
            table.Hand(2, Dragon(1, 2));
            RoundStateMachine round = table.Build("n1a-ron-priority");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-ron-priority", 0, Dragon(1, 3), new[] { 1, 2 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());

            RoundTransition pon = window.State!.Execute(
                RoundCommand.Pon("n1a-ron-priority", 1, Dragon(1, 3), Dragon(1), Dragon(1, 1)));
            Assert.True(pon.IsAccepted, pon.Error?.ToString());
            Assert.Equal(RoundPhase.ResponseWindow, pon.State!.Phase);

            var final = new long[] { 16000, 25000, 34000, 25000 };
            RoundTransition ron = pon.State.Execute(
                RoundCommand.Ron(
                    "n1a-ron-priority",
                    2,
                    Settlement("res-n1a-ron", RonOutcome(2, 0), final)));
            Assert.True(ron.IsAccepted, ron.Error?.ToString());
            Assert.Equal(RoundPhase.RoundEnd, ron.State!.Phase);
            Assert.Equal(RoundEndReason.Ron, ron.Result!.EndReason);
            Assert.Equal(2, ron.Result.Outcome.WinnerSeat);
            Assert.Empty(ron.State.Melds);
            Assert.Contains(ron.Events, e => e.Type == RoundEventType.RonWin && e.Seat == 2);
            AssertConserved(ron.State);
        }

        [Fact]
        public void TwoRonClaimsChooseTheNearestSeatAndNeverDoublePay()
        {
            var table = new N1aTable();
            table.Hand(0).Drawn(0, Dragon(1, 3));
            table.Hand(1, Dragon(1));
            table.Hand(3, Dragon(1, 1));
            RoundStateMachine round = table.Build("n1a-double-ron");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-double-ron", 0, Dragon(1, 3), new[] { 1, 3 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());

            RoundTransition ron3 = window.State!.Execute(
                RoundCommand.Ron(
                    "n1a-double-ron",
                    3,
                    Settlement("res-n1a-ron3", RonOutcome(3, 0), new long[] { 16000, 25000, 25000, 34000 })));
            Assert.True(ron3.IsAccepted, ron3.Error?.ToString());
            Assert.Equal(RoundPhase.ResponseWindow, ron3.State!.Phase);

            RoundTransition ron1 = ron3.State.Execute(
                RoundCommand.Ron(
                    "n1a-double-ron",
                    1,
                    Settlement("res-n1a-ron1", RonOutcome(1, 0), new long[] { 16000, 34000, 25000, 25000 })));
            Assert.True(ron1.IsAccepted, ron1.Error?.ToString());
            Assert.Equal(RoundPhase.RoundEnd, ron1.State!.Phase);
            Assert.Equal(1, ron1.Result!.Outcome.WinnerSeat);
            Assert.Single(ron1.Events, e => e.Type == RoundEventType.RonWin);
            AssertConserved(ron1.State);
        }

        [Fact]
        public void DuplicateResponsesAndExpiredWindowsRejectWithoutMutation()
        {
            var table = new N1aTable();
            table.Hand(0).Drawn(0, Dragon(1, 3));
            table.Hand(1, Dragon(1), Dragon(1, 1));
            RoundStateMachine round = table.Build("n1a-duplicate");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-duplicate", 0, Dragon(1, 3), new[] { 1, 2 }));
            RoundTransition pon = window.State!.Execute(
                RoundCommand.Pon("n1a-duplicate", 1, Dragon(1, 3), Dragon(1), Dragon(1, 1)));
            Assert.True(pon.IsAccepted, pon.Error?.ToString());

            AssertRejectedWithoutMutation(
                pon.State!,
                RoundCommand.Pon("n1a-duplicate", 1, Dragon(1, 3), Dragon(1), Dragon(1, 1)),
                RoundTransitionErrorCode.DuplicateResponse);

            RoundTransition complete = pon.State!.Execute(RoundCommand.Pass("n1a-duplicate", 2));
            Assert.True(complete.IsAccepted, complete.Error?.ToString());
            Assert.Null(complete.State!.Window);

            AssertRejectedWithoutMutation(
                complete.State,
                RoundCommand.Pass("n1a-duplicate", 1),
                RoundTransitionErrorCode.NoResponseWindow);
        }

        [Fact]
        public void ChiAndPonAreUnavailableInAChankanWindow()
        {
            RoundStateMachine round = P13.ChankanDealt("n1a-chankan-meld");
            RoundTransition window = round.Execute(
                RoundCommand.AddedKan("n1a-chankan-meld", 0, new TileId(19), new[] { 1 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());
            Assert.Equal(RoundPhase.ChankanWindow, window.State!.Phase);

            AssertRejectedWithoutMutation(
                window.State,
                RoundCommand.Pon("n1a-chankan-meld", 1, new TileId(19), new TileId(19), new TileId(19)),
                RoundTransitionErrorCode.MeldNotAllowed);

            AssertRejectedWithoutMutation(
                window.State,
                RoundCommand.Chi("n1a-chankan-meld", 1, new TileId(19), new TileId(19), new TileId(19)),
                RoundTransitionErrorCode.MeldNotAllowed);
        }

        [Fact]
        public void InvalidPhysicalTilesAndMeldShapesNeverRecordAClaim()
        {
            var table = new N1aTable();
            table.Hand(0, Man(1), Man(2)).Drawn(0, Man(3));
            table.Hand(1, Man(1, 1), Man(2, 1));
            RoundStateMachine round = table.Build("n1a-invalid-tiles");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-invalid-tiles", 0, Man(3), new[] { 1, 2 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());

            AssertRejectedWithoutMutation(
                window.State!,
                RoundCommand.Chi("n1a-invalid-tiles", 1, Man(3), Man(1, 1), Man(1, 1)),
                RoundTransitionErrorCode.InvalidMeld);

            AssertRejectedWithoutMutation(
                window.State!,
                RoundCommand.Pon("n1a-invalid-tiles", 1, Man(3), Man(1, 1), Man(2, 1)),
                RoundTransitionErrorCode.TileTransitionRejected);
        }

        [Fact]
        public void PrepareFailureLeavesNoEventVersionOrTileChange()
        {
            var table = new N1aTable();
            table.Hand(0, Man(1), Man(2)).Drawn(0, Man(3));
            table.Hand(1, Man(1, 1));
            RoundStateMachine round = table.Build("n1a-prepare-fail");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-prepare-fail", 0, Man(3), new[] { 1 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());

            RoundPreparation preparation = window.State!.Prepare(
                RoundCommand.Chi("n1a-prepare-fail", 1, Man(3), Man(1, 1), Man(2, 1)));
            Assert.False(preparation.IsReady);
            Assert.Equal(RoundTransitionErrorCode.TileTransitionRejected, preparation.Error!.Code);
            Assert.Equal(1L, window.State.Version);
            AssertConserved(window.State);
        }

        [Fact]
        public void RecoveryRoundTripsChiRiichiAndPendingKanDoraAndBehavesIdentically()
        {
            var table = new N1aTable();
            table.Hand(0, Man(1), Man(2)).Drawn(0, Man(3));
            table.Hand(1, Man(1, 1), Man(2, 1));
            RoundStateMachine initial = table.Build("n1a-recovery");
            RoundTransition window = initial.Execute(
                RoundCommand.Discard("n1a-recovery", 0, Man(3), new[] { 1 }));
            RoundTransition chi = window.State!.Execute(
                RoundCommand.Chi("n1a-recovery", 1, Man(3), Man(1, 1), Man(2, 1)));
            Assert.True(chi.IsAccepted, chi.Error?.ToString());

            RoundStateMachine chiState = chi.State!;
            RoundRecoveryState captured = RoundRecovery.Capture(chiState);
            byte[] payload = RoundRecoveryCodec.Encode(captured);
            RoundRecoveryState decoded = RoundRecoveryCodec.Decode(payload);
            RoundStateMachine restored = RoundRecovery.Restore(decoded);

            Assert.Equal(chiState.Version, restored.Version);
            Assert.Equal(chiState.SourceFingerprint, restored.SourceFingerprint);
            Assert.Equal(chiState.Tiles.StateHash, restored.Tiles.StateHash);
            Assert.Equal(chiState.Scores.ToArray(), restored.Scores.ToArray());
            Assert.Equal(chiState.RiichiSticks, restored.RiichiSticks);
            Assert.Equal(chiState.RiichiDeclared.ToArray(), restored.RiichiDeclared.ToArray());
            Assert.Equal(chiState.PendingKanDoraCount, restored.PendingKanDoraCount);
            Assert.Equal(chiState.Melds.Count, restored.Melds.Count);
            Assert.Equal(chiState.Melds[0].Kind, restored.Melds[0].Kind);

            TileId next = restored.Tiles.TilesInZone(TileZone.Hand, 1)[0];
            RoundTransition originalNext = chiState.Execute(RoundCommand.Discard("n1a-recovery", 1, next));
            RoundTransition restoredNext = restored.Execute(RoundCommand.Discard("n1a-recovery", 1, next));
            Assert.True(originalNext.IsAccepted, originalNext.Error?.ToString());
            Assert.True(restoredNext.IsAccepted, restoredNext.Error?.ToString());
            Assert.Equal(
                originalNext.State!.SourceFingerprint,
                restoredNext.State!.SourceFingerprint);
            Assert.Equal(originalNext.Events.Count, restoredNext.Events.Count);

            byte[] v1 = (byte[])payload.Clone();
            BitConverter.GetBytes(1).CopyTo(v1, 4);
            Assert.Throws<System.IO.InvalidDataException>(() => RoundRecoveryCodec.Decode(v1));
        }

        [Fact]
        public void RecoveryPreservesPendingKanDoraAndOpenKanMeld()
        {
            var table = new N1aTable();
            table.Hand(0).Drawn(0, Dragon(1, 3));
            table.Hand(1, Dragon(1), Dragon(1, 1), Dragon(1, 2));
            RoundStateMachine round = table.Build("n1a-recovery-open-kan");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-recovery-open-kan", 0, Dragon(1, 3), new[] { 1 }));
            RoundTransition kan = window.State!.Execute(
                RoundCommand.OpenKan(
                    "n1a-recovery-open-kan",
                    1,
                    Dragon(1, 3),
                    Dragon(1),
                    Dragon(1, 1),
                    Dragon(1, 2)));
            Assert.True(kan.IsAccepted, kan.Error?.ToString());
            RoundStateMachine kanState = kan.State!;

            RoundStateMachine restored = RoundRecovery.Restore(
                RoundRecoveryCodec.Decode(RoundRecoveryCodec.Encode(RoundRecovery.Capture(kanState))));
            Assert.Equal(1, restored.PendingKanDoraCount);
            Assert.Single(restored.Tiles.DoraIndicators);
            Assert.Single(restored.Melds);
            Assert.Equal(RoundMeldKind.OpenKan, restored.Melds[0].Kind);
            Assert.Equal(kanState.SourceFingerprint, restored.SourceFingerprint);

            TileId rinshan = restored.Tiles.DrawnTile!.Value.Tile;
            RoundTransition original = kanState.Execute(
                RoundCommand.Discard("n1a-recovery-open-kan", 1, rinshan));
            RoundTransition replay = restored.Execute(
                RoundCommand.Discard("n1a-recovery-open-kan", 1, rinshan));
            Assert.True(original.IsAccepted, original.Error?.ToString());
            Assert.True(replay.IsAccepted, replay.Error?.ToString());
            Assert.Equal(original.State!.SourceFingerprint, replay.State!.SourceFingerprint);
            Assert.Equal(2, replay.State!.Tiles.DoraIndicators.Count);
        }

        [Fact]
        public void RecoveryPreservesClosedKanDoraAndMeld()
        {
            var table = new N1aTable();
            table.Hand(0, Dragon(1), Dragon(1, 1), Dragon(1, 2)).Drawn(0, Dragon(1, 3));
            RoundStateMachine round = table.Build("n1a-recovery-closed-kan");
            RoundTransition kan = round.Execute(
                RoundCommand.ClosedKan(
                    "n1a-recovery-closed-kan",
                    0,
                    Dragon(1),
                    Dragon(1, 1),
                    Dragon(1, 2),
                    Dragon(1, 3)));
            Assert.True(kan.IsAccepted, kan.Error?.ToString());
            RoundStateMachine kanState = kan.State!;

            RoundStateMachine restored = RoundRecovery.Restore(
                RoundRecoveryCodec.Decode(RoundRecoveryCodec.Encode(RoundRecovery.Capture(kanState))));
            Assert.Equal(0, restored.PendingKanDoraCount);
            Assert.Equal(2, restored.Tiles.DoraIndicators.Count);
            Assert.Single(restored.Melds);
            Assert.Equal(RoundMeldKind.ClosedKan, restored.Melds[0].Kind);
            Assert.Equal(kanState.SourceFingerprint, restored.SourceFingerprint);
            Assert.Equal(kanState.Tiles.StateHash, restored.Tiles.StateHash);
        }

        [Fact]
        public void RecoveryPreservesRiichiScoresSticksFlagsAndCommands()
        {
            var table = new N1aTable();
            table.Hand(
                    0,
                    Man(1), Man(2), Man(3), Man(4), Man(5), Man(6), Man(7), Man(8), Man(9),
                    Pin(1), Pin(1, 1), Sou(5), Sou(6))
                .Drawn(0, Pin(3));
            RoundStateMachine round = table.Build("n1a-recovery-riichi");
            RoundTransition riichi = round.Execute(
                RoundCommand.Riichi("n1a-recovery-riichi", 0, Pin(3), new[] { 1, 2, 3 }));
            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());

            RoundStateMachine restored = RoundRecovery.Restore(
                RoundRecoveryCodec.Decode(RoundRecoveryCodec.Encode(RoundRecovery.Capture(riichi.State!))));

            Assert.Equal(24000L, restored.Scores[0]);
            Assert.Equal(1, restored.RiichiSticks);
            Assert.True(restored.RiichiDeclared[0]);
            Assert.Equal(riichi.State!.SourceFingerprint, restored.SourceFingerprint);

            RoundCommand command = RoundCommand.Pass("n1a-recovery-riichi", 1);
            RoundTransition original = riichi.State!.Execute(command);
            RoundTransition replay = restored.Execute(command);
            Assert.True(original.IsAccepted, original.Error?.ToString());
            Assert.True(replay.IsAccepted, replay.Error?.ToString());
            Assert.Equal(original.State!.SourceFingerprint, replay.State!.SourceFingerprint);
        }

        [Fact]
        public void MidRoundRiichiPotIsTransferredExactlyOnceAtTerminalSettlement()
        {
            var table = new N1aTable();
            table.Hand(
                    0,
                    Man(1), Man(2), Man(3), Man(4), Man(5), Man(6), Man(7), Man(8), Man(9),
                    Pin(1), Pin(1, 1), Sou(5), Sou(6))
                .Drawn(0, Pin(9));
            RoundStateMachine round = table.Build("n1a-riichi-settle");
            RoundTransition riichi = round.Execute(
                RoundCommand.Riichi("n1a-riichi-settle", 0, Pin(9), new[] { 1 }));
            Assert.True(riichi.IsAccepted, riichi.Error?.ToString());
            Assert.Equal(24000L, riichi.State!.Scores[0]);
            Assert.Equal(1, riichi.State.RiichiSticks);

            var final = new long[] { 15000, 35000, 25000, 25000 };
            RoundTransition ended = riichi.State.Execute(
                RoundCommand.Ron(
                    "n1a-riichi-settle",
                    1,
                    Settlement("res-n1a-riichi-settle", RonOutcome(1, 0), final, 0)));
            Assert.True(ended.IsAccepted, ended.Error?.ToString());
            Assert.Equal(RoundPhase.RoundEnd, ended.State!.Phase);
            Assert.Equal(new long[] { 25000, 25000, 25000, 25000 }, ended.Result!.StartingScores.ToArray());
            Assert.Equal(0, ended.Result.StartingRiichiSticks);
            Assert.Equal(0, ended.Result.RemainingRiichiSticks);
            Assert.Equal(new long[] { -10000, 10000, 0, 0 }, ended.Result.TotalDeltas.ToArray());
            Assert.Equal(final, ended.Result.FinalScores.ToArray());
            AssertConserved(ended.State);

            RoundStateMachine restored = RoundRecovery.Restore(
                RoundRecoveryCodec.Decode(RoundRecoveryCodec.Encode(RoundRecovery.Capture(ended.State))));
            Assert.Equal(ended.State.SourceFingerprint, restored.SourceFingerprint);
            Assert.Equal(final, restored.Result!.FinalScores.ToArray());
        }

        [Fact]
        public void NewDecisionCandidatesRejectStaleForeignAndTerminalInstall()
        {
            var table = new N1aTable();
            table.Hand(0, Man(1), Man(2)).Drawn(0, Man(3));
            table.Hand(1, Man(1, 1), Man(2, 1));
            RoundStateMachine round = table.Build("n1a-candidate");
            RoundTransition window = round.Execute(
                RoundCommand.Discard("n1a-candidate", 0, Man(3), new[] { 1 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());

            RoundPreparation prepared = window.State!.Prepare(
                RoundCommand.Chi("n1a-candidate", 1, Man(3), Man(1, 1), Man(2, 1)));
            Assert.True(prepared.IsReady, prepared.Error?.ToString());
            RoundTransition installed = window.State.Install(prepared.Change!);
            Assert.True(installed.IsAccepted, installed.Error?.ToString());

            RoundTransition stale = installed.State!.Install(prepared.Change!);
            Assert.False(stale.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.StaleSourceVersion, stale.Error!.Code);
            Assert.Equal(2L, installed.State.Version);

            var foreignTable = new N1aTable();
            foreignTable.Hand(0, Man(4), Man(5)).Drawn(0, Man(6));
            foreignTable.Hand(1, Man(4, 1), Man(5, 1));
            RoundStateMachine foreign = foreignTable.Build("n1a-candidate");
            RoundTransition foreignWindow = foreign.Execute(
                RoundCommand.Discard("n1a-candidate", 0, Man(6), new[] { 1 }));
            RoundPreparation foreignPrepared = foreignWindow.State!.Prepare(
                RoundCommand.Chi("n1a-candidate", 1, Man(6), Man(4, 1), Man(5, 1)));
            Assert.True(foreignPrepared.IsReady, foreignPrepared.Error?.ToString());

            RoundTransition cross = window.State.Install(foreignPrepared.Change!);
            Assert.False(cross.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.ForeignSourceState, cross.Error!.Code);

            var final = new long[] { 16000, 34000, 25000, 25000 };
            RoundTransition terminal = window.State.Execute(
                RoundCommand.Ron(
                    "n1a-candidate",
                    1,
                    Settlement("res-n1a-candidate", RonOutcome(1, 0), final)));
            Assert.True(terminal.IsAccepted, terminal.Error?.ToString());
            Assert.Equal(RoundPhase.RoundEnd, terminal.State!.Phase);
            RoundPreparation revived = terminal.State.Prepare(
                RoundCommand.Riichi("n1a-candidate", 1, Man(1, 1)));
            Assert.False(revived.IsReady);
            Assert.Equal(RoundTransitionErrorCode.RoundFinished, revived.Error!.Code);
        }

        [Fact]
        public void CommandJournalRoundTripsN1aCommandsAndRejectsMissingMeldTiles()
        {
            RoundCommand chi = RoundCommand.Chi("n1a-codec", 1, Man(3), Man(1), Man(2));
            RoundCommand riichi = RoundCommand.Riichi("n1a-codec", 0, Pin(3), new[] { 1, 2, 3 });
            RoundCommand kan = RoundCommand.ClosedKan("n1a-codec", 0, Dragon(1), Dragon(1, 1), Dragon(1, 2), Dragon(1, 3));

            RoundCommand chiBack = RoundCommandCodec.Decode(RoundCommandCodec.Encode(chi));
            Assert.Equal(RoundActionKind.DeclareChi, chiBack.Action);
            Assert.Equal(Man(3), chiBack.Tile);
            Assert.Equal(new[] { Man(1), Man(2) }, chiBack.MeldTiles!.ToArray());

            RoundCommand riichiBack = RoundCommandCodec.Decode(RoundCommandCodec.Encode(riichi));
            Assert.Equal(RoundActionKind.DeclareRiichi, riichiBack.Action);
            Assert.Equal(Pin(3), riichiBack.Tile);
            Assert.Equal(new[] { 1, 2, 3 }, riichiBack.EligibleSeats!.ToArray());

            RoundCommand kanBack = RoundCommandCodec.Decode(RoundCommandCodec.Encode(kan));
            Assert.Equal(RoundActionKind.DeclareClosedKan, kanBack.Action);
            Assert.Equal(4, kanBack.MeldTiles!.Count);

            // Pre-N-1a payloads end before the optional meld block and must still decode.
            RoundCommand passBack = RoundCommandCodec.Decode(
                RoundCommandCodec.Encode(RoundCommand.Pass("n1a-codec", 2)));
            Assert.Equal(RoundActionKind.Pass, passBack.Action);
            Assert.Null(passBack.MeldTiles);
        }
    }

    /// <summary>
    /// Explicit 136-tile fixture. Unspecified hand slots and wall tiles are filled from unused
    /// physical ids so every round starts with a complete, conserved table.
    /// </summary>
    internal sealed class N1aTable
    {
        private readonly Dictionary<int, List<TileId>> _hands = new Dictionary<int, List<TileId>>();
        private readonly List<TileId> _deadWall = new List<TileId>();
        private readonly List<TileId> _wallPrefix = new List<TileId>();
        private int _drawnSeat = -1;
        private TileId? _drawn;
        private int _lastDiscardSeat = -1;
        private TileId? _lastDiscard;

        public int Dealer { get; set; }

        public N1aTable Hand(int seat, params TileId[] tiles)
        {
            if (!_hands.TryGetValue(seat, out List<TileId>? list))
            {
                list = new List<TileId>();
                _hands.Add(seat, list);
            }

            list.AddRange(tiles);
            return this;
        }

        public N1aTable Drawn(int seat, TileId tile)
        {
            _drawnSeat = seat;
            _drawn = tile;
            return this;
        }

        public N1aTable LastDiscard(int seat, TileId tile)
        {
            _lastDiscardSeat = seat;
            _lastDiscard = tile;
            return this;
        }

        public N1aTable DeadWall(params TileId[] tiles)
        {
            _deadWall.AddRange(tiles);
            return this;
        }

        public N1aTable WallPrefix(params TileId[] tiles)
        {
            _wallPrefix.AddRange(tiles);
            return this;
        }

        public RoundStateMachine Build(
            string roundId,
            RoundPhase phase = RoundPhase.CheatWindow,
            long[]? scores = null,
            int sticks = 0)
        {
            var placements = new TilePlacement[TileId.Count];
            var used = new bool[TileId.Count];
            var placed = new bool[TileId.Count];

            void Place(TileId tile, TilePosition position)
            {
                if (used[tile.Value])
                {
                    throw new InvalidOperationException("tile " + tile + " specified twice");
                }

                used[tile.Value] = true;
                placed[tile.Value] = true;
                placements[tile.Value] = new TilePlacement(tile, position);
            }

            var drawnTiles = new TileId[4];
            var hasDrawn = new bool[4];
            if (_drawn.HasValue)
            {
                drawnTiles[_drawnSeat] = _drawn.Value;
                hasDrawn[_drawnSeat] = true;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (!_hands.TryGetValue(seat, out List<TileId>? hand))
                {
                    hand = new List<TileId>();
                }

                if (hand.Count > 13)
                {
                    throw new InvalidOperationException("seat " + seat + " specifies more than 13 closed tiles");
                }

                for (int i = 0; i < hand.Count; i++)
                {
                    Place(hand[i], TilePosition.Hand(seat, i));
                }
            }

            // Reserve drawn tiles before filling closed hands so the filler cannot consume them.
            for (int seat = 0; seat < 4; seat++)
            {
                if (!hasDrawn[seat])
                {
                    continue;
                }

                TileId tile = drawnTiles[seat];
                if (used[tile.Value])
                {
                    throw new InvalidOperationException("drawn tile " + tile + " is already placed");
                }

                used[tile.Value] = true;
            }

            if (_lastDiscard.HasValue)
            {
                Place(_lastDiscard.Value, TilePosition.Discard(_lastDiscardSeat, 0));
            }

            for (int i = 0; i < _deadWall.Count; i++)
            {
                Place(_deadWall[i], TilePosition.DeadWall(i));
            }

            for (int i = 0; i < _wallPrefix.Count; i++)
            {
                Place(_wallPrefix[i], TilePosition.Wall(i));
            }

            var cursor = 0;
            TileId NextUnused()
            {
                while (cursor < TileId.Count && used[cursor])
                {
                    cursor++;
                }

                if (cursor >= TileId.Count)
                {
                    throw new InvalidOperationException("fixture ran out of physical tiles");
                }

                used[cursor] = true;
                return new TileId(cursor);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                _hands.TryGetValue(seat, out List<TileId>? hand);
                int closed = hand?.Count ?? 0;
                for (int i = closed; i < 13; i++)
                {
                    TileId tile = NextUnused();
                    placements[tile.Value] = new TilePlacement(tile, TilePosition.Hand(seat, i));
                    placed[tile.Value] = true;
                }

                if (hasDrawn[seat])
                {
                    TileId tile = drawnTiles[seat];
                    placements[tile.Value] = new TilePlacement(tile, TilePosition.Hand(seat, 13));
                    placed[tile.Value] = true;
                }
            }

            for (int i = _deadWall.Count; i < 14; i++)
            {
                TileId tile = NextUnused();
                placements[tile.Value] = new TilePlacement(tile, TilePosition.DeadWall(i));
                placed[tile.Value] = true;
            }

            int wallStart = _wallPrefix.Count;
            int wallCount = 0;
            for (int i = 0; i < TileId.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                if (wallStart + wallCount >= TilePositionMap.LiveWallSlotCount)
                {
                    throw new InvalidOperationException("wall fixture exceeds 122 live slots");
                }

                placements[i] = new TilePlacement(new TileId(i), TilePosition.Wall(wallStart + wallCount));
                placed[i] = true;
                wallCount++;
            }

            for (int i = 0; i < TileId.Count; i++)
            {
                if (!placed[i])
                {
                    throw new InvalidOperationException("fixture left tile " + i + " unplaced");
                }
            }

            TileId dora = placements.Length > 0
                ? new TileId(Array.FindIndex(placements, p => p.Position == TilePosition.DeadWall(4)))
                : default;
            LastDiscardEvidence? lastDiscard = _lastDiscard.HasValue
                ? new LastDiscardEvidence(
                    _lastDiscard.Value,
                    TilePosition.Discard(_lastDiscardSeat, 0))
                : (LastDiscardEvidence?)null;
            DrawnTileEvidence? drawn = _drawn.HasValue
                ? new DrawnTileEvidence(_drawn.Value, _drawnSeat, DrawSource.Wall)
                : (DrawnTileEvidence?)null;

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                wallPointer: 0,
                wallEndExclusive: wallStart + wallCount,
                rinshanPointer: 0,
                lastDiscard: lastDiscard,
                revision: 0,
                rinshanDraw: null,
                doraIndicators: new[] { dora },
                kanCount: 0,
                drawnTile: drawn);

            var spec = new RoundSpec(
                roundId,
                Dealer,
                1,
                0,
                0,
                scores ?? new long[] { 25000, 25000, 25000, 25000 },
                sticks);
            return RoundStateMachine.CreateDealt(spec, map, phase);
        }
    }
}
