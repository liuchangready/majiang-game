using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Domain.Tests
{
    internal static class P13
    {
        public static readonly DateTimeOffset FixedUtc = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);

        public static RoundSpec Spec(
            string roundId,
            int dealer = 0,
            long[]? scores = null,
            int sticks = 0,
            int wind = 1,
            int number = 0,
            int honba = 0)
        {
            return new RoundSpec(
                roundId,
                dealer,
                wind,
                number,
                honba,
                scores ?? new long[] { 25000, 25000, 25000, 25000 },
                sticks);
        }

        public static RoundStateMachine Unstarted(string roundId, int dealer = 0)
        {
            var wall = TilePositionMap.CreateWall(RecordedRandomDraw.Standard("p13-wall-" + roundId));
            return RoundStateMachine.CreateUnstarted(Spec(roundId, dealer), wall);
        }

        public static RoundStateMachine Dealt(
            string roundId,
            int dealer = 0,
            long[]? scores = null,
            int sticks = 0,
            int wind = 1,
            int number = 0,
            int honba = 0,
            RoundPhase phase = RoundPhase.CheatWindow)
        {
            TilePositionMap dealt = TilePositionMap.CreateDealt(
                RecordedRandomDraw.Standard("p13-dealt-" + roundId),
                dealer);
            return RoundStateMachine.CreateDealt(
                Spec(roundId, dealer, scores, sticks, wind, number, honba),
                dealt,
                phase);
        }

        public static RoundSettlement Settlement(
            RoundStateMachine round,
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
                new PersistedUtcTime(FixedUtc),
                1000L);
        }

        public static RoundSettlement Tsumo(RoundStateMachine round, string resultId = "res-tsumo")
        {
            return Settlement(
                round,
                resultId,
                RoundOutcome.Tsumo(round.DealerSeat),
                new long[] { 33000, 22333, 22334, 22333 });
        }

        public static RoundSettlement Ron(
            RoundStateMachine round,
            int winner,
            int loser,
            string resultId = "res-ron")
        {
            var final = new long[] { 25000, 25000, 25000, 25000 };
            final[winner] += 9000;
            final[loser] -= 9000;
            return Settlement(round, resultId, RoundOutcome.Ron(winner, loser), final);
        }

        /// <summary>
        /// Seat 0 has a pon of Man5 copies 0..2, the fourth copy is the drawn tile, and seat 1
        /// is a chankan-eligible opponent. Dead wall and live wall are fully populated.
        /// </summary>
        public static RoundStateMachine ChankanDealt(string roundId)
        {
            var placements = new TilePlacement[TileId.Count];
            var used = new bool[TileId.Count];
            int cursor = 0;

            TileId Next()
            {
                while (used[cursor])
                {
                    cursor++;
                }

                used[cursor] = true;
                return new TileId(cursor);
            }

            void Put(int tile, TilePosition position)
            {
                if (used[tile])
                {
                    throw new InvalidOperationException("tile " + tile + " already used");
                }

                used[tile] = true;
                placements[tile] = new TilePlacement(new TileId(tile), position);
            }

            // Pon of Man5 copies 0,1,2 (TileId 16,17,18) and drawn copy 3 (19).
            for (int i = 0; i < 3; i++)
            {
                Put(16 + i, TilePosition.Meld(0, i));
            }

            Put(19, TilePosition.Hand(0, 13));

            for (int i = 0; i < 10; i++)
            {
                PlaceNext(placements, Next(), TilePosition.Hand(0, i));
            }

            for (int seat = 1; seat <= 3; seat++)
            {
                for (int i = 0; i < 13; i++)
                {
                    PlaceNext(placements, Next(), TilePosition.Hand(seat, i));
                }
            }

            TileId dora = default;
            for (int i = 0; i < 14; i++)
            {
                TileId tile = Next();
                PlaceNext(placements, tile, TilePosition.DeadWall(i));
                if (i == 4)
                {
                    dora = tile;
                }
            }

            for (int i = 0; i < 69; i++)
            {
                PlaceNext(placements, Next(), TilePosition.Wall(53 + i));
            }

            CheckAllPlaced(placements, used);

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                53,
                122,
                0,
                doraIndicators: new[] { dora },
                kanCount: 0,
                drawnTile: new DrawnTileEvidence(new TileId(19), 0, DrawSource.Wall));
            return RoundStateMachine.CreateDealt(Spec(roundId), map);
        }

        /// <summary>Dealt map with no live wall tiles, for exhaustive-draw boundary tests.</summary>
        public static RoundStateMachine NoWallDealt(string roundId)
        {
            var placements = new TilePlacement[TileId.Count];
            var used = new bool[TileId.Count];
            int cursor = 0;

            TileId Next()
            {
                while (used[cursor])
                {
                    cursor++;
                }

                used[cursor] = true;
                return new TileId(cursor);
            }

            for (int i = 0; i < 14; i++)
            {
                PlaceNext(placements, Next(), TilePosition.Hand(0, i));
            }

            for (int seat = 1; seat <= 3; seat++)
            {
                for (int i = 0; i < 13; i++)
                {
                    PlaceNext(placements, Next(), TilePosition.Hand(seat, i));
                }
            }

            for (int i = 0; i < 14; i++)
            {
                PlaceNext(placements, Next(), TilePosition.DeadWall(i));
            }

            // The remaining 69 tiles are physical discards, so WallRemaining is exactly zero.
            for (int i = 0; i < 69; i++)
            {
                PlaceNext(placements, Next(), TilePosition.Discard(0, i));
            }

            CheckAllPlaced(placements, used);
            TilePositionMap map = TilePositionMap.FromPlacements(placements, 122, 122, 0);
            return RoundStateMachine.CreateDealt(Spec(roundId), map, RoundPhase.Draw);
        }

        public static TileId FirstHandTile(RoundStateMachine round, int seat)
        {
            return round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
        }

        private static void PlaceNext(TilePlacement[] placements, TileId tile, TilePosition position)
        {
            placements[tile.Value] = new TilePlacement(tile, position);
        }

        private static void CheckAllPlaced(TilePlacement[] placements, bool[] used)
        {
            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i])
                {
                    throw new InvalidOperationException("placement fixture is missing tile " + i);
                }
            }
        }
    }

    public class RoundStateMachineTests
    {
        [Fact]
        public void StartStackingDealsAndEntersCheatWindow()
        {
            RoundStateMachine round = P13.Unstarted("r-start");
            Assert.Equal(RoundPhase.Stacking, round.Phase);
            Assert.Equal(0, round.Version);

            RoundTransition transition = round.Execute(RoundCommand.StartStacking("r-start"));

            Assert.True(transition.IsAccepted, transition.Error?.ToString());
            Assert.Equal(1, transition.CommandVersion);
            Assert.Equal(RoundPhase.CheatWindow, transition.State!.Phase);
            Assert.Equal(14, transition.State.Tiles.CountAt(TileZone.Hand, 0));
            Assert.Equal(13, transition.State.Tiles.CountAt(TileZone.Hand, 1));
            Assert.Equal(69, transition.State.Tiles.WallRemaining);
            Assert.Contains(transition.Events, e => e.Type == RoundEventType.RoundStarted);
            Assert.Contains(transition.Events, e => e.Type == RoundEventType.TilesDealt);
            Assert.Contains(transition.Events, e => e.Type == RoundEventType.DoraRevealed);
        }

        [Fact]
        public void PrepareDoesNotMutateAndStaleInstallIsRejected()
        {
            RoundStateMachine round = P13.Dealt("r-prepare");
            ulong hash = round.Tiles.StateHash;
            int beforeEvents = round.Events.Count;
            RoundSettlement settlement = P13.Tsumo(round);

            RoundPreparation prepared = round.Prepare(RoundCommand.Tsumo("r-prepare", 0, settlement));

            Assert.True(prepared.IsReady);
            Assert.Equal(0, round.Version);
            Assert.Equal(hash, round.Tiles.StateHash);
            Assert.Equal(beforeEvents, round.Events.Count);
            Assert.Equal(1L, prepared.Change!.NextState.Version);

            RoundTransition installed = round.Install(prepared.Change);
            Assert.True(installed.IsAccepted);
            Assert.Equal(1, installed.CommandVersion);
            Assert.Equal(RoundPhase.RoundEnd, installed.State!.Phase);

            // The stale-candidate guard is observable on a non-terminal successor because a
            // finished round rejects every install (including stale ones) by terminal rule.
            RoundStateMachine live = P13.Dealt("r-prepare-stale");
            RoundPreparation discardPrep = live.Prepare(RoundCommand.Discard(
                "r-prepare-stale",
                0,
                P13.FirstHandTile(live, 0),
                new[] { 1 }));
            RoundTransition liveInstalled = live.Install(discardPrep.Change!);
            Assert.True(liveInstalled.IsAccepted);

            RoundTransition stale = liveInstalled.State!.Install(discardPrep.Change!);
            Assert.False(stale.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.StaleSourceVersion, stale.Error!.Code);
            Assert.Equal(1, liveInstalled.State.Version);
        }

        [Fact]
        public void ForeignCandidateFromAnotherStateWithSameRoundAndVersionIsRejected()
        {
            RoundStateMachine target = P13.Dealt("r-foreign");
            RoundStateMachine other = P13.Dealt(
                "r-foreign",
                scores: new long[] { 25000, 25000, 25000, 24000 });
            RoundSettlement settlement = P13.Settlement(
                other,
                "res-foreign",
                RoundOutcome.Tsumo(0),
                new long[] { 32000, 22333, 22334, 22333 });
            RoundPreparation foreign = other.Prepare(
                RoundCommand.Tsumo("r-foreign", 0, settlement));

            Assert.True(foreign.IsReady);
            Assert.Equal(target.RoundId, other.RoundId);
            Assert.Equal(target.Version, other.Version);
            Assert.NotEqual(target.SourceFingerprint, other.SourceFingerprint);

            ulong targetFingerprint = target.SourceFingerprint;
            ulong targetTiles = target.Tiles.StateHash;
            RoundTransition rejected = target.Install(foreign.Change!);

            Assert.False(rejected.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.ForeignSourceState, rejected.Error!.Code);
            Assert.Equal(0L, target.Version);
            Assert.Equal(targetFingerprint, target.SourceFingerprint);
            Assert.Equal(targetTiles, target.Tiles.StateHash);
            Assert.Empty(target.Events);
            Assert.Null(target.Result);

            // A candidate prepared by the exact same source instance is installable.
            RoundPreparation own = target.Prepare(
                RoundCommand.Tsumo("r-foreign", 0, P13.Tsumo(target)));
            RoundTransition accepted = target.Install(own.Change!);
            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.Equal(1L, accepted.State!.Version);
        }

        [Fact]
        public void TsumoTransitionEventsIncludeRoundEndedExactlyOnce()
        {
            RoundTransition started = P13.Unstarted("r-tsumo-events")
                .Execute(RoundCommand.StartStacking("r-tsumo-events"));
            Assert.True(started.IsAccepted, started.Error?.ToString());
            RoundStateMachine source = started.State!;

            RoundTransition terminal = source.Execute(
                RoundCommand.Tsumo("r-tsumo-events", 0, P13.Tsumo(source)));

            AssertTerminalEventDelta(source, terminal);
            Assert.Contains(terminal.Events, e => e.Type == RoundEventType.TsumoWin);
        }

        [Fact]
        public void RonTransitionEventsIncludeRoundEndedExactlyOnce()
        {
            RoundStateMachine round = P13.Dealt("r-ron-events");
            RoundTransition opened = round.Execute(RoundCommand.Discard(
                "r-ron-events",
                0,
                P13.FirstHandTile(round, 0),
                new[] { 1 }));
            Assert.True(opened.IsAccepted, opened.Error?.ToString());
            RoundStateMachine source = opened.State!;

            RoundTransition terminal = source.Execute(
                RoundCommand.Ron("r-ron-events", 1, P13.Ron(round, 1, 0, "res-ron-events")));

            AssertTerminalEventDelta(source, terminal);
            Assert.Contains(terminal.Events, e => e.Type == RoundEventType.RonWin);
        }

        [Fact]
        public void ChankanRonTransitionEventsIncludeRoundEndedExactlyOnce()
        {
            RoundStateMachine round = P13.ChankanDealt("r-chankan-events");
            RoundTransition window = round.Execute(RoundCommand.AddedKan(
                "r-chankan-events",
                0,
                new TileId(19),
                new[] { 1 }));
            Assert.True(window.IsAccepted, window.Error?.ToString());
            RoundStateMachine source = window.State!;

            RoundTransition terminal = source.Execute(RoundCommand.Ron(
                "r-chankan-events",
                1,
                P13.Ron(round, 1, 0, "res-chankan-events")));

            AssertTerminalEventDelta(source, terminal);
            Assert.Contains(terminal.Events, e => e.Type == RoundEventType.RonWin);
        }

        [Fact]
        public void ExhaustiveDrawTransitionEventsIncludeRoundEndedExactlyOnce()
        {
            RoundStateMachine source = P13.NoWallDealt("r-draw-events");
            RoundSettlement settlement = P13.Settlement(
                source,
                "res-draw-events",
                RoundOutcome.ExhaustiveDraw(new[] { 0, 2 }),
                new long[] { 25000, 25000, 25000, 25000 });

            RoundTransition terminal = source.Execute(
                RoundCommand.ExhaustiveDraw("r-draw-events", settlement));

            AssertTerminalEventDelta(source, terminal);
            Assert.Contains(terminal.Events, e => e.Type == RoundEventType.ExhaustiveDraw);
        }

        [Fact]
        public void AbortiveDrawTransitionEventsIncludeRoundEndedExactlyOnce()
        {
            RoundStateMachine source = P13.Dealt("r-abortive-events");
            RoundSettlement settlement = P13.Settlement(
                source,
                "res-abortive-events",
                RoundOutcome.AbortiveDraw("FOUR_WINDS"),
                new long[] { 25000, 25000, 25000, 25000 });

            RoundTransition terminal = source.Execute(
                RoundCommand.AbortiveDraw("r-abortive-events", settlement));

            AssertTerminalEventDelta(source, terminal);
            Assert.Contains(terminal.Events, e => e.Type == RoundEventType.AbortiveDraw);
        }

        private static void AssertTerminalEventDelta(
            RoundStateMachine source,
            RoundTransition terminal)
        {
            Assert.True(terminal.IsAccepted, terminal.Error?.ToString());
            Assert.NotNull(terminal.State);
            int before = source.Events.Count;
            Assert.Equal(before + terminal.Events.Count, terminal.State!.Events.Count);
            Assert.Equal(terminal.Events.Count, terminal.State.Events.Count - before);

            for (int i = 0; i < terminal.Events.Count; i++)
            {
                RoundEvent eventFromTransition = terminal.Events[i];
                RoundEvent eventFromHistory = terminal.State.Events[before + i];
                Assert.Equal(before + i, eventFromTransition.Ordinal);
                Assert.Equal(eventFromTransition.Ordinal, eventFromHistory.Ordinal);
                Assert.Equal(eventFromTransition.Type, eventFromHistory.Type);
                Assert.Equal(eventFromTransition.Seat, eventFromHistory.Seat);
                Assert.Equal(eventFromTransition.Tile, eventFromHistory.Tile);
                Assert.Equal(eventFromTransition.Detail, eventFromHistory.Detail);
            }

            Assert.Equal(RoundEventType.RoundEnded, terminal.Events[terminal.Events.Count - 1].Type);
            Assert.Equal(1, terminal.Events.Count(e => e.Type == RoundEventType.RoundEnded));
            Assert.Equal(1, terminal.State.Events.Count(e => e.Type == RoundEventType.RoundEnded));
            Assert.Equal(
                terminal.Events.Count,
                terminal.Events.Select(e => e.Ordinal).Distinct().Count());
        }

        [Fact]
        public void FinishedRoundCanNeverBeRevived()
        {
            RoundStateMachine round = P13.Dealt("r-terminal");
            RoundTransition finished = round.Execute(RoundCommand.Tsumo("r-terminal", 0, P13.Tsumo(round)));
            RoundStateMachine ended = finished.State!;
            ulong hash = ended.Tiles.StateHash;
            int version = (int)ended.Version;

            foreach (RoundCommand command in new[]
            {
                RoundCommand.StartStacking("r-terminal"),
                RoundCommand.Tsumo("r-terminal", 0, P13.Tsumo(ended)),
                RoundCommand.Discard("r-terminal", 0, P13.FirstHandTile(ended, 0))
            })
            {
                RoundPreparation rejected = ended.Prepare(command);
                Assert.False(rejected.IsReady);
                Assert.Equal(RoundTransitionErrorCode.RoundFinished, rejected.Error!.Code);
                Assert.Equal(version, ended.Version);
                Assert.Equal(hash, ended.Tiles.StateHash);
                Assert.Equal(1, ended.Events.Count(e => e.Type == RoundEventType.RoundEnded));
            }
        }

        [Fact]
        public void SettlementConservationViolationHasZeroSideEffects()
        {
            RoundStateMachine round = P13.Dealt("r-conservation");
            ulong hash = round.Tiles.StateHash;
            var broken = P13.Settlement(
                round,
                "res-broken",
                RoundOutcome.Tsumo(0),
                new long[] { 33000, 22333, 22334, 22332 });

            RoundTransition transition = round.Execute(RoundCommand.Tsumo("r-conservation", 0, broken));

            Assert.False(transition.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.SettlementConservationViolation, transition.Error!.Code);
            Assert.Equal(0, round.Version);
            Assert.Equal(hash, round.Tiles.StateHash);
            Assert.Null(round.Result);
            Assert.Empty(round.Events);
        }

        [Fact]
        public void SettlementOutcomeMismatchIsRejectedWithoutSideEffects()
        {
            RoundStateMachine round = P13.Dealt("r-outcome");
            RoundSettlement wrongWinner = P13.Ron(round, 2, 0);

            RoundTransition transition = round.Execute(RoundCommand.Tsumo("r-outcome", 0, wrongWinner));

            Assert.False(transition.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.SettlementOutcomeMismatch, transition.Error!.Code);
            Assert.Equal(0, round.Version);
            Assert.Null(round.Result);
        }

        [Fact]
        public void DiscardWindowPassesAdvanceInOneVersionPerCommand()
        {
            RoundStateMachine round = P13.Dealt("r-window");
            TileId discard = P13.FirstHandTile(round, 0);
            RoundTransition opened = round.Execute(RoundCommand.Discard(
                "r-window",
                0,
                discard,
                new[] { 1, 2 }));

            Assert.True(opened.IsAccepted, opened.Error?.ToString());
            Assert.Equal(RoundPhase.ResponseWindow, opened.State!.Phase);
            Assert.Equal(2, opened.State.Window!.EligibleSeats.Count);
            Assert.Equal(2, opened.Events.Count);

            RoundTransition one = opened.State.Execute(RoundCommand.Pass("r-window", 1));
            Assert.True(one.IsAccepted);
            Assert.Equal(RoundPhase.ResponseWindow, one.State!.Phase);
            Assert.True(one.State.Window!.HasResponded(1));

            RoundTransition two = one.State.Execute(RoundCommand.Pass("r-window", 2));
            Assert.True(two.IsAccepted);
            Assert.Equal(RoundPhase.CheatWindow, two.State!.Phase);
            Assert.Equal(1, two.State.CurrentSeat);
            Assert.Equal(68, two.State.Tiles.WallRemaining);
            Assert.Equal(3, two.State.Version);
            Assert.Equal(5, two.State.Events.Count);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, two.State.Events.Select(e => e.Ordinal).ToArray());
        }

        [Fact]
        public void DuplicateResponseIsRejectedWithoutAdvancingVersion()
        {
            RoundStateMachine round = P13.Dealt("r-duplicate");
            RoundTransition opened = round.Execute(RoundCommand.Discard(
                "r-duplicate",
                0,
                P13.FirstHandTile(round, 0),
                new[] { 1, 2 }));
            RoundTransition one = opened.State!.Execute(RoundCommand.Pass("r-duplicate", 1));

            RoundTransition duplicate = one.State!.Execute(RoundCommand.Pass("r-duplicate", 1));

            Assert.False(duplicate.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.DuplicateResponse, duplicate.Error!.Code);
            Assert.Equal(2, one.State.Version);
            Assert.Single(one.State.Window!.Claims);
            Assert.Null(duplicate.State);
        }

        [Fact]
        public void RonWaitsForTheWholeWindowAndNearestWinnerTakesTheRound()
        {
            RoundStateMachine round = P13.Dealt("r-ron");
            TileId discard = P13.FirstHandTile(round, 0);
            RoundTransition opened = round.Execute(RoundCommand.Discard(
                "r-ron",
                0,
                discard,
                new[] { 1, 2, 3 }));
            RoundSettlement settlement = P13.Ron(round, 1, 0);

            RoundTransition claimed = opened.State!.Execute(RoundCommand.Ron("r-ron", 1, settlement));
            Assert.True(claimed.IsAccepted);
            Assert.Equal(RoundPhase.ResponseWindow, claimed.State!.Phase);
            Assert.Null(claimed.Result);

            RoundTransition pass2 = claimed.State.Execute(RoundCommand.Pass("r-ron", 2));
            RoundTransition pass3 = pass2.State!.Execute(RoundCommand.Pass("r-ron", 3));

            Assert.True(pass3.IsAccepted, pass3.Error?.ToString());
            Assert.Equal(RoundPhase.RoundEnd, pass3.State!.Phase);
            Assert.Equal(RoundEndReason.Ron, pass3.Result!.EndReason);
            Assert.Equal(1, pass3.Result.Outcome.WinnerSeat);
            Assert.Equal(0, pass3.Result.Outcome.LoserSeat);
            Assert.Equal(4, pass3.State.Version);
            Assert.Contains(pass3.State.Events, e => e.Type == RoundEventType.RonWin);
        }

        [Fact]
        public void ChankanRonLeavesPonAndRinshanUntouched()
        {
            RoundStateMachine round = P13.ChankanDealt("r-chankan-ron");
            ulong before = round.Tiles.StateHash;
            RoundTransition window = round.Execute(RoundCommand.AddedKan(
                "r-chankan-ron",
                0,
                new TileId(19),
                new[] { 1 }));

            Assert.True(window.IsAccepted, window.Error?.ToString());
            Assert.Equal(RoundPhase.ChankanWindow, window.State!.Phase);
            Assert.Equal(before, window.State.Tiles.StateHash);
            Assert.Equal(0, window.State.Tiles.RinshanPointer);
            Assert.Equal(0, window.State.Tiles.KanCount);
            Assert.Equal(3, window.State.Tiles.CountAt(TileZone.Meld, 0));

            RoundTransition ended = window.State.Execute(
                RoundCommand.Ron("r-chankan-ron", 1, P13.Ron(round, 1, 0, "res-chankan")));

            Assert.True(ended.IsAccepted, ended.Error?.ToString());
            Assert.Equal(RoundPhase.RoundEnd, ended.State!.Phase);
            Assert.Equal(RoundEndReason.ChankanRon, ended.Result!.EndReason);
            Assert.Equal(before, ended.State.Tiles.StateHash);
            Assert.Equal(0, ended.State.Tiles.RinshanPointer);
            Assert.DoesNotContain(ended.State.Events, e => e.Type == RoundEventType.KanDeclared);
            Assert.DoesNotContain(ended.State.Events, e => e.Type == RoundEventType.RinshanDrawn);
            Assert.Contains(ended.State.Events, e => e.Type == RoundEventType.RonWin);
        }

        [Fact]
        public void ChankanPassCommitsAddedKanAndRinshan()
        {
            RoundStateMachine round = P13.ChankanDealt("r-chankan-pass");
            RoundTransition window = round.Execute(RoundCommand.AddedKan(
                "r-chankan-pass",
                0,
                new TileId(19),
                new[] { 1 }));
            int wallBefore = window.State!.Tiles.WallRemaining;

            RoundTransition ended = window.State.Execute(RoundCommand.Pass("r-chankan-pass", 1));

            Assert.True(ended.IsAccepted, ended.Error?.ToString());
            Assert.Equal(RoundPhase.CheatWindow, ended.State!.Phase);
            Assert.Equal(4, ended.State.Tiles.CountAt(TileZone.Meld, 0));
            Assert.Equal(1, ended.State.Tiles.RinshanPointer);
            Assert.Equal(1, ended.State.Tiles.KanCount);
            Assert.Equal(wallBefore - 1, ended.State.Tiles.WallRemaining);
            Assert.Contains(ended.State.Events, e => e.Type == RoundEventType.KanDeclared);
            Assert.Contains(ended.State.Events, e => e.Type == RoundEventType.RinshanDrawn);
            Assert.Null(ended.Result);
        }

        [Fact]
        public void AddedKanWithNoChankanCandidateCommitsImmediately()
        {
            RoundStateMachine round = P13.ChankanDealt("r-kan-empty");
            RoundTransition transition = round.Execute(RoundCommand.AddedKan(
                "r-kan-empty",
                0,
                new TileId(19),
                Array.Empty<int>()));

            Assert.True(transition.IsAccepted, transition.Error?.ToString());
            Assert.Equal(RoundPhase.CheatWindow, transition.State!.Phase);
            Assert.Equal(1, transition.CommandVersion);
            Assert.Equal(4, transition.State.Tiles.CountAt(TileZone.Meld, 0));
            Assert.Equal(1, transition.State.Tiles.RinshanPointer);
            Assert.Empty(transition.State.Window == null ? Array.Empty<int>() : new[] { 1 });
        }

        [Fact]
        public void DuplicateChankanPassIsRejectedWithoutMutation()
        {
            RoundStateMachine round = P13.ChankanDealt("r-chankan-dup");
            RoundTransition window = round.Execute(RoundCommand.AddedKan(
                "r-chankan-dup",
                0,
                new TileId(19),
                new[] { 1, 2 }));
            RoundTransition pass1 = window.State!.Execute(RoundCommand.Pass("r-chankan-dup", 1));
            ulong hash = pass1.State!.Tiles.StateHash;

            RoundTransition duplicate = pass1.State.Execute(RoundCommand.Pass("r-chankan-dup", 1));

            Assert.False(duplicate.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.DuplicateResponse, duplicate.Error!.Code);
            Assert.Equal(hash, pass1.State.Tiles.StateHash);
            Assert.Equal(2, pass1.State.Version);
        }

        [Fact]
        public void ExhaustiveDrawRequiresAnEmptyWall()
        {
            RoundStateMachine normal = P13.Dealt("r-wall-full", phase: RoundPhase.Draw);
            RoundSettlement settlement = P13.Settlement(
                normal,
                "res-draw",
                RoundOutcome.ExhaustiveDraw(new[] { 0, 2 }),
                new long[] { 25000, 25000, 25000, 25000 });
            RoundTransition rejected = normal.Execute(RoundCommand.ExhaustiveDraw("r-wall-full", settlement));
            Assert.False(rejected.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.WallNotExhausted, rejected.Error!.Code);

            RoundStateMachine empty = P13.NoWallDealt("r-wall-empty");
            Assert.Equal(0, empty.Tiles.WallRemaining);
            RoundTransition accepted = empty.Execute(RoundCommand.ExhaustiveDraw("r-wall-empty", settlement));
            Assert.True(accepted.IsAccepted, accepted.Error?.ToString());
            Assert.Equal(RoundEndReason.ExhaustiveDraw, accepted.Result!.EndReason);
            Assert.Equal(2, accepted.Result.Outcome.TenpaiSeats.Count);
            Assert.Contains(accepted.State!.Events, e => e.Type == RoundEventType.ExhaustiveDraw);
        }

        [Fact]
        public void AbortiveDrawEndsTheRoundWithInjectedSettlement()
        {
            RoundStateMachine round = P13.Dealt("r-abortive");
            RoundSettlement settlement = P13.Settlement(
                round,
                "res-abortive",
                RoundOutcome.AbortiveDraw("NINE_TERMINALS"),
                new long[] { 25000, 25000, 25000, 25000 });

            RoundTransition transition = round.Execute(RoundCommand.AbortiveDraw("r-abortive", settlement));

            Assert.True(transition.IsAccepted, transition.Error?.ToString());
            Assert.Equal(RoundEndReason.AbortiveDraw, transition.Result!.EndReason);
            Assert.Equal("NINE_TERMINALS", transition.Result.Outcome.Reason);
            Assert.Contains(transition.State!.Events, e => e.Type == RoundEventType.AbortiveDraw);
        }

        [Fact]
        public void InvalidEligibleSeatsAndWrongPlayersAreRejected()
        {
            RoundStateMachine round = P13.Dealt("r-eligible");
            TileId tile = P13.FirstHandTile(round, 0);
            ulong hash = round.Tiles.StateHash;
            int version = (int)round.Version;

            RoundTransition selfEligible = round.Execute(RoundCommand.Discard("r-eligible", 0, tile, new[] { 0 }));
            Assert.False(selfEligible.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.InvalidEligibleSeats, selfEligible.Error!.Code);

            RoundTransition duplicateEligible = round.Execute(RoundCommand.Discard("r-eligible", 0, tile, new[] { 1, 1 }));
            Assert.False(duplicateEligible.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.InvalidEligibleSeats, duplicateEligible.Error!.Code);

            RoundTransition wrongPlayer = round.Execute(RoundCommand.Discard("r-eligible", 1, tile));
            Assert.False(wrongPlayer.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.WrongPlayer, wrongPlayer.Error!.Code);

            Assert.Equal(version, round.Version);
            Assert.Equal(hash, round.Tiles.StateHash);
            Assert.Empty(round.Events);
        }

        [Fact]
        public void StartStackingIsOnlyLegalBeforeTheDeal()
        {
            RoundStateMachine round = P13.Dealt("r-restack");
            RoundTransition rejected = round.Execute(RoundCommand.StartStacking("r-restack"));

            Assert.False(rejected.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.WrongPhase, rejected.Error!.Code);
            Assert.Equal(0, round.Version);
            Assert.Equal(14, round.Tiles.CountAt(TileZone.Hand, 0));
        }

        [Fact]
        public void TsumoRequiresATrackedDrawnTile()
        {
            RoundStateMachine round = P13.NoWallDealt("r-no-draw");
            // NoWallDealt has no drawn-tile evidence and is in DRAW.
            RoundTransition transition = round.Execute(
                RoundCommand.Tsumo("r-no-draw", 0, P13.Settlement(
                    round,
                    "res-no-draw",
                    RoundOutcome.Tsumo(0),
                    new long[] { 33000, 22333, 22334, 22333 })));

            Assert.False(transition.IsAccepted);
            Assert.Equal(RoundTransitionErrorCode.NoDrawnTile, transition.Error!.Code);
            Assert.Equal(0, round.Version);
        }
    }
}
