using System;
using System.Collections.Generic;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// P3.3/P3.4 response-decision lifecycle fence.
    ///
    /// A response decision is only meaningful while the Domain's exact response window still
    /// requires that seat. When a committed transition closes the window, or re-opens it with a
    /// different target/identity, every pending decision that belonged to the old window must be
    /// removed together with its timing. Decisions that still belong to the open window (the
    /// ordinary multi-seat submit/priority case) are retained unchanged.
    ///
    /// The filter mutates only the supplied candidate clock, which is always a clone of the
    /// installed clock. A failed/cancelled durable commit therefore leaves both the pending
    /// table and the reserve ledger untouched.
    /// </summary>
    internal static class TableResponseDecisionReconciliation
    {
        internal static List<PendingDecision> FilterAfterTransition(
            RoundResponseWindow? priorWindow,
            RoundStateMachine nextState,
            IReadOnlyList<PendingDecision> pendingAfter,
            DecisionClock candidateClock,
            long now)
        {
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

            RoundResponseWindow? nextWindow = nextState.Window;
            for (int i = 0; i < pendingAfter.Count; i++)
            {
                PendingDecision pending = pendingAfter[i] ??
                    throw new ArgumentException("Pending decision cannot be null.", nameof(pendingAfter));

                bool wasResponseDecision = priorWindow != null &&
                                           ContainsSeat(priorWindow.EligibleSeats, pending.Key.Seat);
                if (!wasResponseDecision)
                {
                    // A turn/cheat/settlement decision is not owned by the response window and
                    // must survive an unrelated commit unchanged.
                    kept.Add(pending);
                    continue;
                }

                if (nextWindow != null &&
                    SameWindow(priorWindow!, nextWindow) &&
                    ContainsSeat(nextWindow.EligibleSeats, pending.Key.Seat) &&
                    !nextWindow.HasResponded(pending.Key.Seat))
                {
                    kept.Add(pending);
                    continue;
                }

                // The exact window that justified this decision is gone (or this seat already
                // responded). Settle the elapsed allowance and drop the timing in the same
                // candidate used by the commit, never after the fact.
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
                if (IsStillValid(round, window, decision))
                {
                    kept.Add(decision);
                }
            }

            return kept;
        }

        private static bool IsStillValid(
            RoundStateMachine round,
            RoundResponseWindow? window,
            TableRecoveredDecision decision)
        {
            if (window != null)
            {
                return ContainsSeat(window.EligibleSeats, decision.Key.Seat) &&
                       !window.HasResponded(decision.Key.Seat);
            }

            if (decision.Key.Seat == round.CurrentSeat)
            {
                // In a non-window phase the acting seat may legitimately own a turn decision.
                // A response-only candidate set (Pass/Chi/Pon/OpenKan/Ron) is stale; it can no
                // longer be applied because the Domain has no response window.
                return !IsResponseOnly(decision.Candidates);
            }

            // No window, not the acting seat: no turn decision can be current. A settlement
            // authority decision is not a response and is left to its own typed validation.
            return !IsResponseOnly(decision.Candidates) && !HasDiscard(decision.Candidates);
        }

        private static bool IsResponseOnly(IReadOnlyList<LegalActionOption> candidates)
        {
            bool any = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                LegalActionKind kind = candidates[i].Kind;
                if (kind != LegalActionKind.Pass &&
                    kind != LegalActionKind.Chi &&
                    kind != LegalActionKind.Pon &&
                    kind != LegalActionKind.Ron)
                {
                    // Open/closed/added kan are also turn actions; treat only the pure response
                    // set as conclusive. A Kan-only turn decision is kept for Domain validation.
                    return false;
                }

                any = true;
            }

            return any;
        }

        private static bool HasDiscard(IReadOnlyList<LegalActionOption> candidates)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Kind == LegalActionKind.Discard)
                {
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
