using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Unified strategy input for one AI decision window (safe V5 replacement for V4
    /// <c>com.majiang.ai.AiDecisionContext</c>). It pairs a frozen
    /// <see cref="AiSeatSnapshot"/> with the exact candidate list produced for that window.
    ///
    /// V4 copied the supplied <c>List&lt;AiCandidate&gt;</c> into an unmodifiable list and
    /// required option ids to be unique. This port does the same with an ordinal,
    /// case-sensitive comparison: <c>"opt-A"</c> and <c>"opt-a"</c> are distinct ids, and
    /// <see cref="Candidate(string)"/> never performs a case-insensitive fallback. Lookup
    /// returns the candidate carrying the exact id (ids are unique, so "first" is the only
    /// match), and <see cref="SafeDefault"/> returns the first candidate flagged
    /// <see cref="AiCandidate.IsSafeDefault"/>, exactly like V4's <c>firstOrNull</c>.
    ///
    /// The candidate list is defensive-copied at construction. <see cref="AiCandidate"/> is
    /// immutable, so a later mutation of the caller's list cannot change the context, and no
    /// public property exposes a writable collection. There is deliberately no
    /// <c>Submit</c>, authority reference, table state or callback on this type.
    /// </summary>
    public sealed class AiDecisionContext
    {
        private readonly ReadOnlyCollection<AiCandidate> _candidates;

        public AiDecisionContext(AiSeatSnapshot snapshot, IReadOnlyList<AiCandidate> candidates)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            var copy = new AiCandidate[candidates.Count];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < candidates.Count; i++)
            {
                AiCandidate candidate = candidates[i] ??
                    throw new ArgumentException("Candidates cannot contain null.", nameof(candidates));
                if (!ids.Add(candidate.OptionId))
                {
                    throw new ArgumentException(
                        "candidate optionId must be unique within a decision.",
                        nameof(candidates));
                }

                copy[i] = candidate;
            }

            _candidates = new ReadOnlyCollection<AiCandidate>(copy);
        }

        public AiSeatSnapshot Snapshot { get; }

        public AiDecisionStamp Stamp => Snapshot.Stamp;

        public int Seat => Snapshot.Seat;

        public AiPhase Phase => Snapshot.Phase;

        public string? RoundId => Snapshot.Stamp.RoundId;

        public string? DecisionId => Snapshot.Stamp.DecisionId;

        public long ControlEpoch => Snapshot.Stamp.ControlEpoch;

        public long? DeadlineMillis => Snapshot.Stamp.DeadlineMillis;

        public AiCheatView CheatView => Snapshot.CheatView;

        public AiCandidateLimits Limits => Snapshot.Limits;

        public AiCheatPolicy Policy => Snapshot.Policy;

        public bool Takeover => Snapshot.Takeover;

        public IReadOnlyList<LegalActionOption> OrdinaryLegalOptions => Snapshot.OrdinaryLegalOptions;

        public AiObservation? OrdinaryObservation => Snapshot.OrdinaryObservation;

        /// <summary>
        /// Exact frozen candidate list. The backing array is private and the property is not
        /// a <see cref="List{T}"/> or array, so no caller can append to the decision.
        /// </summary>
        public IReadOnlyList<AiCandidate> Candidates => _candidates;

        /// <summary>
        /// Finds the candidate whose <see cref="AiCandidate.OptionId"/> equals
        /// <paramref name="optionId"/> under <see cref="StringComparison.Ordinal"/>. Returns
        /// null when no candidate matches. A null id is rejected, matching the Kotlin
        /// intrinsic check on V4's non-null parameter.
        /// </summary>
        public AiCandidate? Candidate(string optionId)
        {
            if (optionId == null)
            {
                throw new ArgumentNullException(nameof(optionId));
            }

            for (int i = 0; i < _candidates.Count; i++)
            {
                if (string.Equals(_candidates[i].OptionId, optionId, StringComparison.Ordinal))
                {
                    return _candidates[i];
                }
            }

            return null;
        }

        /// <summary>
        /// First candidate flagged <see cref="AiCandidate.IsSafeDefault"/>, or null when the
        /// window has no fallback candidate. The order is the frozen projection order, as in
        /// V4's <c>firstOrNull</c>.
        /// </summary>
        public AiCandidate? SafeDefault()
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i].IsSafeDefault)
                {
                    return _candidates[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Unified AI decision entry point (V5 analogue of V4
    /// <c>com.majiang.ai.AiDecisionStrategy</c>). A strategy sees ordinary actions and
    /// authorized cheat candidates through the same <see cref="AiDecisionContext"/> and
    /// returns only an <c>optionId</c>; it never constructs or submits a domain command.
    /// Returning null hands the window to the scheduler's safe fallback, exactly like V4.
    ///
    /// <see cref="CheatPolicy"/> is an optional strategy-level restriction; the room policy
    /// is intersected with it by the authority/projection layer, not here. A null value means
    /// "no additional restriction", matching V4's default interface property.
    ///
    /// The interface intentionally has no submit, authority, clock or random surface. The
    /// existing <see cref="IAiStrategy"/> remains the ordinary-only legacy path; adapters can
    /// bridge it without widening this contract.
    /// </summary>
    public interface AiDecisionStrategy
    {
        AiCheatPolicy? CheatPolicy { get; }

        string? Choose(AiDecisionContext context);
    }
}
