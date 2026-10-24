using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Physical kan kind for one <see cref="LegalActionKind.Kan"/> candidate. V4's
    /// <c>GameAction.DeclareKan</c> carried a <c>MeldType</c>; the V5 candidate DTO does not,
    /// so the authoritative projection must state it instead of letting a strategy infer it
    /// from an option id or treat every Kan candidate as a closed kan.
    /// </summary>
    public enum BasicKanKind
    {
        Closed,
        Open,
        Added
    }

    /// <summary>
    /// Abortive-draw kind for one <see cref="LegalActionKind.Other"/> candidate. V4 had a
    /// dedicated <c>GameAction.DeclareAbortiveDraw</c>; V5 maps it to a generic candidate
    /// kind, so the semantic is explicit. In particular, a strategy must never assume that
    /// every Other candidate is 九种九牌.
    /// </summary>
    public enum BasicAbortiveDrawKind
    {
        NineTerminals
    }

    /// <summary>
    /// Typed metadata for exactly one legal candidate that <see cref="BasicAiStrategy"/>
    /// needs but the base <see cref="LegalActionOption"/> does not carry.
    ///
    /// The fact is bound to the candidate by the observation's candidate identity
    /// (kind, seat, physical tile and option id). Only Kan and Other candidates may carry
    /// facts: a Kan fact must state its physical kan kind, and an Other fact must be present
    /// to explicitly classify the candidate as an abortive draw or as not one. A null
    /// <see cref="AbortiveDraw"/> on an Other fact means "known not to be an abortive draw",
    /// which is different from the candidate having no fact at all.
    /// </summary>
    public sealed class BasicCandidateFacts
    {
        public BasicCandidateFacts(
            LegalActionOption candidate,
            BasicKanKind? kanKind = null,
            BasicAbortiveDrawKind? abortiveDraw = null)
        {
            Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
            KanKind = kanKind;
            AbortiveDraw = abortiveDraw;
            Validate();
        }

        public LegalActionOption Candidate { get; }

        public BasicKanKind? KanKind { get; }

        public BasicAbortiveDrawKind? AbortiveDraw { get; }

        public bool IsAbortiveDraw =>
            AbortiveDraw.HasValue && AbortiveDraw.Value == BasicAbortiveDrawKind.NineTerminals;

        /// <summary>
        /// Re-checks the fact's public invariants. The constructor is the primary boundary;
        /// AiObservation and the raw TryChoose entry point call this again so a future
        /// deserialized/fabricated instance cannot bypass the enum checks.
        /// </summary>
        internal void Validate()
        {
            if (Candidate == null)
            {
                throw new ArgumentException("Candidate facts require a candidate.", nameof(Candidate));
            }

            if (KanKind.HasValue && !IsDefined(KanKind.Value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(KanKind),
                    KanKind.Value,
                    "Unknown physical kan kind. Only Closed, Open and Added are defined.");
            }

            if (AbortiveDraw.HasValue && !IsDefined(AbortiveDraw.Value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(AbortiveDraw),
                    AbortiveDraw.Value,
                    "Unknown abortive-draw kind. Only NineTerminals is defined.");
            }

            if (Candidate.Kind == LegalActionKind.Kan)
            {
                if (!KanKind.HasValue)
                {
                    throw new ArgumentException(
                        "A Kan candidate requires an explicit physical kan kind.",
                        nameof(KanKind));
                }

                if (AbortiveDraw.HasValue)
                {
                    throw new ArgumentException(
                        "A Kan candidate cannot carry abortive-draw metadata.",
                        nameof(AbortiveDraw));
                }
            }
            else if (Candidate.Kind == LegalActionKind.Other)
            {
                if (KanKind.HasValue)
                {
                    throw new ArgumentException(
                        "Only a Kan candidate can carry a physical kan kind.",
                        nameof(KanKind));
                }

                // AbortiveDraw == null is meaningful here: the projection explicitly says
                // this Other candidate is not an abortive draw. Missing facts are different
                // and are rejected by AiObservation / BasicAiStrategy.
            }
            else
            {
                throw new ArgumentException(
                    "Basic candidate facts are only defined for Kan and Other candidates.",
                    nameof(Candidate));
            }
        }

        private static bool IsDefined(BasicKanKind value) =>
            value == BasicKanKind.Closed ||
            value == BasicKanKind.Open ||
            value == BasicKanKind.Added;

        private static bool IsDefined(BasicAbortiveDrawKind value) =>
            value == BasicAbortiveDrawKind.NineTerminals;
    }

    /// <summary>
    /// The minimal, typed, own-seat and public-table information that
    /// <see cref="BasicAiStrategy"/> needs in addition to <see cref="AiObservation"/>.
    ///
    /// It deliberately contains no game state, wall order, opponent closed tiles, seed,
    /// full tile-position map or authority callback. The only fields are:
    /// <list type="bullet">
    /// <item>the viewing seat (must equal the observation seat);</item>
    /// <item>the viewer's own riichi status;</item>
    /// <item>the last public discard tile, or null when the table has none;</item>
    /// <item>candidate-bound kan / abortive-draw facts.</item>
    /// </list>
    ///
    /// The context is immutable and copies the supplied fact list. It is still the table
    /// runtime's job (a later actor-wiring task) to populate these facts from authoritative
    /// state; this task only defines and consumes the contract.
    /// </summary>
    public sealed class BasicAiContext
    {
        private readonly ReadOnlyCollection<BasicCandidateFacts> _candidateFacts;

        public BasicAiContext(
            int seat,
            bool isRiichi,
            TileId? lastDiscard,
            IReadOnlyList<BasicCandidateFacts> candidateFacts)
        {
            AiGuard.CheckSeat(seat, nameof(seat));
            if (candidateFacts == null)
            {
                throw new ArgumentNullException(nameof(candidateFacts));
            }

            var copy = new List<BasicCandidateFacts>(candidateFacts.Count);
            for (int i = 0; i < candidateFacts.Count; i++)
            {
                BasicCandidateFacts facts = candidateFacts[i] ??
                    throw new ArgumentException("Candidate facts cannot be null.", nameof(candidateFacts));
                facts.Validate();
                if (facts.Candidate.Seat != seat)
                {
                    throw new ArgumentException(
                        "Candidate facts must belong to the context seat.",
                        nameof(candidateFacts));
                }

                for (int j = 0; j < copy.Count; j++)
                {
                    if (AiGuard.ActionsEqual(copy[j].Candidate, facts.Candidate))
                    {
                        throw new ArgumentException(
                            "A candidate can carry at most one facts entry.",
                            nameof(candidateFacts));
                    }
                }

                copy.Add(facts);
            }

            Seat = seat;
            IsRiichi = isRiichi;
            LastDiscard = lastDiscard;
            _candidateFacts = new ReadOnlyCollection<BasicCandidateFacts>(copy);
        }

        /// <summary>The viewing seat. AiObservation requires this to equal its own seat.</summary>
        public int Seat { get; }

        /// <summary>The viewer's own riichi status; never another seat's status.</summary>
        public bool IsRiichi { get; }

        /// <summary>The last public discard tile, or null when there is no public last discard.</summary>
        public TileId? LastDiscard { get; }

        public IReadOnlyList<BasicCandidateFacts> CandidateFacts => _candidateFacts;

        public bool TryGetFacts(LegalActionOption candidate, out BasicCandidateFacts facts)
        {
            if (candidate != null)
            {
                for (int i = 0; i < _candidateFacts.Count; i++)
                {
                    if (AiGuard.ActionsEqual(_candidateFacts[i].Candidate, candidate))
                    {
                        facts = _candidateFacts[i];
                        return true;
                    }
                }
            }

            facts = null!;
            return false;
        }

        /// <summary>
        /// The complete candidate-binding contract shared by AiObservation and the raw
        /// <see cref="BasicAiStrategy.TryChoose"/> entry point. It rejects null/foreign-seat
        /// or duplicate candidates, facts that do not name a candidate in this exact list,
        /// unknown enum values and unclassified Kan/Other candidates. It never rewrites the
        /// list, so valid candidates keep their instance, OptionId and order.
        /// </summary>
        internal static void ValidateCandidateList(
            BasicAiContext context,
            IReadOnlyList<LegalActionOption> legalActions)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (legalActions == null)
            {
                throw new ArgumentNullException(nameof(legalActions));
            }

            for (int i = 0; i < legalActions.Count; i++)
            {
                LegalActionOption candidate = legalActions[i];
                if (candidate == null)
                {
                    throw new ArgumentException(
                        "Legal candidate cannot be null.",
                        nameof(legalActions));
                }

                if (candidate.Seat != context.Seat)
                {
                    throw new ArgumentException(
                        "Every legal candidate must belong to the context seat.",
                        nameof(legalActions));
                }

                for (int j = 0; j < i; j++)
                {
                    if (AiGuard.ActionsEqual(legalActions[j], candidate))
                    {
                        throw new ArgumentException(
                            "Legal candidate list cannot contain duplicates.",
                            nameof(legalActions));
                    }
                }
            }

            for (int i = 0; i < context.CandidateFacts.Count; i++)
            {
                BasicCandidateFacts facts = context.CandidateFacts[i];
                facts.Validate();
                if (facts.Candidate.Seat != context.Seat)
                {
                    throw new ArgumentException(
                        "Candidate facts must belong to the context seat.",
                        nameof(context));
                }

                bool found = false;
                for (int j = 0; j < legalActions.Count; j++)
                {
                    if (AiGuard.ActionsEqual(legalActions[j], facts.Candidate))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new ArgumentException(
                        "Basic context binds metadata to a candidate that is not part of this candidate list.",
                        nameof(context));
                }
            }

            for (int i = 0; i < legalActions.Count; i++)
            {
                LegalActionOption action = legalActions[i];
                if (action.Kind == LegalActionKind.Kan)
                {
                    if (!context.TryGetFacts(action, out BasicCandidateFacts kanFacts) ||
                        !kanFacts.KanKind.HasValue)
                    {
                        throw new ArgumentException(
                            "Basic context is missing an explicit physical kan kind for a Kan candidate.",
                            nameof(context));
                    }
                }
                else if (action.Kind == LegalActionKind.Other)
                {
                    if (!context.TryGetFacts(action, out _))
                    {
                        throw new ArgumentException(
                            "Basic context is missing an explicit abortive-draw classification for an Other candidate.",
                            nameof(context));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Raised when BasicStrategy cannot decide without information that was not explicitly
    /// supplied. It is deliberately diagnosable rather than silently falling back to a
    /// fabricated fact (for example "all Kan candidates are closed" or "all Other
    /// candidates are 九种九牌").
    /// </summary>
    public sealed class BasicAiContextException : InvalidOperationException
    {
        public BasicAiContextException(string message)
            : base(message)
        {
        }
    }
}
