using System;
using System.Collections.Generic;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// P3.3/P3.4 decision-ownership lifecycle fence.
    ///
    /// A pending decision is only meaningful while the transitioned Domain state still owns it:
    /// a response while the exact window still requires that seat, and a turn/cheat/settlement
    /// decision only at the acting seat with turn evidence. A committed transition that moves the
    /// turn, closes/re-targets the window, or finishes the round removes every no-longer-owned
    /// decision together with its timing. Decisions that still belong to the current state (the
    /// ordinary multi-seat submit/priority case) are retained unchanged.
    ///
    /// The filter mutates only the supplied candidate clock, which is always a clone of the
    /// installed clock. A failed/cancelled durable commit therefore leaves both the pending
    /// table and the reserve ledger untouched.
    /// </summary>
    internal static class TableResponseDecisionReconciliation
    {
        internal static List<PendingDecision> FilterAfterTransition(
            RoundStateMachine priorState,
            RoundStateMachine nextState,
            IReadOnlyList<PendingDecision> pendingAfter,
            DecisionClock candidateClock,
            long now)
        {
            if (priorState == null)
            {
                throw new ArgumentNullException(nameof(priorState));
            }

            if (nextState == null)
            {
                throw new ArgumentNullException(nameof(nextState));
            }

            if (pendingAfter == null)
            {
                throw new ArgumentNullException(nameof(pendingAfter));
            }

            if (candidateClock == null)
            {
                throw new ArgumentNullException(nameof(candidateClock));
            }

            var kept = new List<PendingDecision>(pendingAfter.Count);
            if (nextState.IsFinished)
            {
                candidateClock.Reset();
                return kept;
            }

            RoundResponseWindow? priorWindow = priorState.Window;
            RoundResponseWindow? nextWindow = nextState.Window;
            for (int i = 0; i < pendingAfter.Count; i++)
            {
                PendingDecision pending = pendingAfter[i] ??
                    throw new ArgumentException("Pending decision cannot be null.", nameof(pendingAfter));

                if (IsStillOwned(
                    priorState,
                    priorWindow,
                    nextState,
                    nextWindow,
                    pending.Key.Seat,
                    pending.Candidates))
                {
                    kept.Add(pending);
                    continue;
                }

                // The transitioned state no longer owns this decision (the turn moved on, the
                // window closed/re-targeted, or this seat already responded). Settle the elapsed
                // allowance and drop the timing in the same candidate used by the commit, never
                // after the fact.
                candidateClock.Cancel(pending.Key, now);
            }

            return kept;
        }

        /// <summary>
        /// Recovery-side reconciliation. A checkpoint/timing row can legitimately lag the
        /// committed window (for example an older accepted checkpoint or a host that opened a
        /// decision from a stale capture). Restoring such a decision would make every later tick
        /// replay a response the Domain no longer accepts. Drop only decisions that cannot be
        /// justified by the recovered Domain window; a current-turn decision is preserved.
        /// </summary>
        internal static List<TableRecoveredDecision> FilterRecovered(
            RoundStateMachine round,
            IReadOnlyList<TableRecoveredDecision> recovered)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (recovered == null)
            {
                throw new ArgumentNullException(nameof(recovered));
            }

            var kept = new List<TableRecoveredDecision>(recovered.Count);
            RoundResponseWindow? window = round.Window;
            for (int i = 0; i < recovered.Count; i++)
            {
                TableRecoveredDecision decision = recovered[i] ??
                    throw new ArgumentException("Recovered decision cannot be null.", nameof(recovered));
                if (IsStillOwned(
                    round,
                    window,
                    round,
                    window,
                    decision.Key.Seat,
                    decision.Candidates))
                {
                    kept.Add(decision);
                }
            }

            return kept;
        }

        /// <summary>
        /// Single ownership predicate shared by the transition and recovery filters, so the two
        /// paths cannot drift into different answers for the same state.
        /// </summary>
        private static bool IsStillOwned(
            RoundStateMachine priorState,
            RoundResponseWindow? priorWindow,
            RoundStateMachine nextState,
            RoundResponseWindow? nextWindow,
            int seat,
            IReadOnlyList<LegalActionOption> candidates)
        {
            if (nextWindow != null)
            {
                // A window transition only preserves decisions that were actual responses to
                // the same window identity. A stale turn for an eligible seat (the host-race
                // case) is still a turn decision and must not become a response merely because
                // the new window lists that seat.
                if (priorWindow == null ||
                    IsTurnDecision(priorState, priorWindow, seat, candidates) ||
                    !SameWindow(priorWindow, nextWindow))
                {
                    return false;
                }

                return ContainsSeat(nextWindow.EligibleSeats, seat) &&
                       !nextWindow.HasResponded(seat);
            }

            // Once a window has closed, every decision captured before it is stale. A response
            // cannot become a turn, and a stale turn opened during the window is not the new
            // turn's decision either.
            if (priorWindow != null)
            {
                return false;
            }

            return seat == nextState.CurrentSeat &&
                   IsTurnDecision(nextState, null, seat, candidates);
        }

        private static bool IsTurnDecision(
            RoundStateMachine state,
            RoundResponseWindow? window,
            int seat,
            IReadOnlyList<LegalActionOption> candidates)
        {
            if (HasTurnAnchor(candidates))
            {
                return true;
            }

            if (window != null)
            {
                // A Kan on the window's own tile is an OpenKan response. A Kan on another tile
                // cannot be a response to this window; if its hand shape supports a turn kan,
                // it is a stale turn decision.
                for (int i = 0; i < candidates.Count; i++)
                {
                    LegalActionOption candidate = candidates[i];
                    if (candidate.Kind == LegalActionKind.Kan &&
                        candidate.Tile.HasValue &&
                        candidate.Tile.Value == window.TargetTile)
                    {
                        return false;
                    }
                }

                return HasTurnKanShape(state, seat, candidates);
            }

            return HasLegalTurnKan(state, seat, candidates);
        }

        private static bool HasLegalTurnKan(
            RoundStateMachine round,
            int seat,
            IReadOnlyList<LegalActionOption> candidates)
        {
            if (round.Phase != RoundPhase.CheatWindow &&
                round.Phase != RoundPhase.Discard)
            {
                return false;
            }

            return HasTurnKanShape(round, seat, candidates);
        }

        private static bool HasTurnKanShape(
            RoundStateMachine round,
            int seat,
            IReadOnlyList<LegalActionOption> candidates)
        {
            // A response set normally carries Pass. Real turn decisions do not; keeping a
            // [Kan,Pass] pair would reintroduce the closed-window OpenKan response.
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Kind == LegalActionKind.Pass)
                {
                    return false;
                }
            }

            if (round.Tiles.KanCount >= 4 ||
                round.Tiles.RinshanPointer >= 4 ||
                round.Tiles.WallRemaining <= 0)
            {
                return false;
            }

            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            for (int i = 0; i < candidates.Count; i++)
            {
                LegalActionOption candidate = candidates[i];
                if (candidate.Kind != LegalActionKind.Kan || !candidate.Tile.HasValue)
                {
                    continue;
                }

                int type = candidate.Tile.Value.TypeIndex;
                int handCopies = 0;
                for (int h = 0; h < hand.Count; h++)
                {
                    if (hand[h].TypeIndex == type)
                    {
                        handCopies++;
                    }
                }

                if (handCopies == 4)
                {
                    return true;
                }

                if (handCopies >= 1)
                {
                    for (int m = 0; m < round.Melds.Count; m++)
                    {
                        RoundMeld meld = round.Melds[m];
                        if (meld.Seat == seat &&
                            meld.Kind == RoundMeldKind.Pon &&
                            meld.Tiles[0].TypeIndex == type)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool HasTurnAnchor(IReadOnlyList<LegalActionOption> candidates)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                switch (candidates[i].Kind)
                {
                    case LegalActionKind.Discard:
                    case LegalActionKind.Tsumo:
                    case LegalActionKind.Riichi:
                    case LegalActionKind.Cheat:
                    case LegalActionKind.Other:
                        return true;
                }
            }

            return false;
        }

        private static bool ContainsSeat(IReadOnlyList<int> seats, int seat)
        {
            for (int i = 0; i < seats.Count; i++)
            {
                if (seats[i] == seat)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameWindow(RoundResponseWindow left, RoundResponseWindow right)
        {
            return left.FromSeat == right.FromSeat &&
                   left.TargetTile == right.TargetTile &&
                   left.IsChankan == right.IsChankan;
        }
    }
}
