using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Immutable, per-seat authority snapshot for one AI decision window (safe V5
    /// replacement for V4 <c>com.majiang.ai.AiSeatSnapshot</c>). V4 held a live
    /// <c>PlayerView</c>, an application-layer <c>PlayerDecision</c> and four projection
    /// view objects and then deep-copied them before crossing to the AI thread. This type
    /// instead freezes:
    /// <list type="bullet">
    /// <item><description><see cref="Stamp"/>: the complete decision identity (seat,
    /// session/round/decision ids, control epoch, phase, state version, offer/window id and
    /// nullable deadline);</description></item>
    /// <item><description><see cref="OrdinaryLegalOptions"/>: the exact ordinary legal
    /// candidates for this seat, copied and read-only (empty is valid outside an ordinary
    /// action);</description></item>
    /// <item><description><see cref="CheatView"/>: the authorized private technique facts,
    /// themselves immutable and nested-list copied;</description></item>
    /// <item><description><see cref="Limits"/>, <see cref="Policy"/> and
    /// <see cref="Takeover"/>: the frozen budget/filter/policy inputs;</description></item>
    /// <item><description><see cref="OrdinaryObservation"/>: the optional existing
    /// <see cref="AiObservation"/> view/decision replacement when a real ordinary decision
    /// exists. It is deliberately nullable because IDLE, SHUFFLE_PREPARATION and
    /// OFFER_CHOICE windows do not necessarily have an ordinary <c>DecisionKey</c>; this
    /// type never fabricates one.</description></item>
    /// </list>
    ///
    /// There is intentionally no <c>GameState</c>, full <c>TilePositionMap</c>, wall order,
    /// opponent closed hand, seed, authority callback or submit surface. The table runtime
    /// remains responsible for producing the seat-scoped facts; this constructor validates
    /// that a structurally inconsistent mix (wrong phase, seat, round, decision, control
    /// epoch, state version, offer, window or deadline) is rejected instead of frozen.
    ///
    /// V5 non-equivalences relative to V4 are deliberate and documented here:
    /// <list type="bullet">
    /// <item><description>V4 accepted any <c>PlayerView</c>/<c>PlayerDecision</c> pair and
    /// trusted the projection. V5 requires the optional observation to match the stamp
    /// field-by-field and to expose exactly the frozen ordinary candidates.</description></item>
    /// <item><description>V4 allowed a preparation view and a pending offer to coexist while
    /// its phase precedence chose preparation. V5 rejects that ambiguous mix: the snapshot
    /// has at most one active window.</description></item>
    /// <item><description>V4 could carry a preparation view whose <c>decisionId</c> was
    /// synthesized as <c>preparation-&lt;roundId&gt;-&lt;seat&gt;</c> while attached technique
    /// views still exposed the underlying ordinary decision id. V5 permits that one
    /// documented divergence only during <see cref="AiPhase.ShufflePreparation"/>; all
    /// control-epoch/state-version/round/deadline bindings still apply.</description></item>
    /// </list>
    /// </summary>
    public sealed class AiSeatSnapshot
    {
        private readonly ReadOnlyCollection<LegalActionOption> _ordinaryLegalOptions;

        public AiSeatSnapshot(
            AiDecisionStamp stamp,
            IReadOnlyList<LegalActionOption> ordinaryLegalOptions,
            AiCheatView cheatView,
            AiCandidateLimits limits,
            AiCheatPolicy policy,
            bool takeover,
            AiObservation? ordinaryObservation = null)
        {
            if (stamp == null)
            {
                throw new ArgumentNullException(nameof(stamp));
            }

            if (ordinaryLegalOptions == null)
            {
                throw new ArgumentNullException(nameof(ordinaryLegalOptions));
            }

            if (cheatView == null)
            {
                throw new ArgumentNullException(nameof(cheatView));
            }

            if (limits == null)
            {
                throw new ArgumentNullException(nameof(limits));
            }

            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            var options = CopyOrdinaryOptions(ordinaryLegalOptions, stamp.Seat);
            if (ordinaryObservation != null)
            {
                ValidateObservation(ordinaryObservation, stamp, options);
            }

            AiCheatView frozenCheatView = cheatView.DeepCopy();
            ValidatePreparation(frozenCheatView, stamp, options, ordinaryObservation);
            ValidatePhase(frozenCheatView, stamp, options, ordinaryObservation);
            ValidateFactIdentity(frozenCheatView, stamp);

            Stamp = stamp;
            _ordinaryLegalOptions = options;
            CheatView = frozenCheatView;
            Limits = limits;
            Policy = policy;
            Takeover = takeover;
            OrdinaryObservation = ordinaryObservation;
        }

        public AiDecisionStamp Stamp { get; }

        public int Seat => Stamp.Seat;

        public AiPhase Phase => Stamp.Phase;

        /// <summary>
        /// Exact ordinary legal candidates, in projection order. The list is a defensive
        /// copy and is empty when the current window has no ordinary decision.
        /// </summary>
        public IReadOnlyList<LegalActionOption> OrdinaryLegalOptions => _ordinaryLegalOptions;

        /// <summary>Frozen authorized private technique facts (never null; may be empty).</summary>
        public AiCheatView CheatView { get; }

        public AiCandidateLimits Limits { get; }

        public AiCheatPolicy Policy { get; }

        public bool Takeover { get; }

        /// <summary>
        /// Optional existing ordinary observation. Null is meaningful: IDLE,
        /// SHUFFLE_PREPARATION and offer-only windows have no ordinary decision identity.
        /// </summary>
        public AiObservation? OrdinaryObservation { get; }

        /// <summary>
        /// Independent copy. The nested cheat view is rebuilt through its validating
        /// copy path; ordinary options are recopied; the optional observation, limits,
        /// policy and stamp are immutable value surfaces and are safely shared.
        /// </summary>
        public AiSeatSnapshot DeepCopy()
        {
            return new AiSeatSnapshot(
                Stamp,
                _ordinaryLegalOptions,
                CheatView,
                Limits,
                Policy,
                Takeover,
                OrdinaryObservation);
        }

        private static ReadOnlyCollection<LegalActionOption> CopyOrdinaryOptions(
            IReadOnlyList<LegalActionOption> source,
            int seat)
        {
            var copy = new LegalActionOption[source.Count];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < source.Count; i++)
            {
                LegalActionOption option = source[i] ??
                    throw new ArgumentException("Ordinary legal options cannot contain null.", nameof(source));
                if (option.Seat != seat)
                {
                    throw new ArgumentException(
                        "Every ordinary legal option must belong to the snapshot seat.",
                        nameof(source));
                }

                if (option.OptionId != null && !ids.Add(option.OptionId))
                {
                    throw new ArgumentException(
                        "Ordinary legal option ids must be unique (ordinal comparison).",
                        nameof(source));
                }

                copy[i] = option;
            }

            return new ReadOnlyCollection<LegalActionOption>(copy);
        }

        private static void ValidateObservation(
            AiObservation observation,
            AiDecisionStamp stamp,
            ReadOnlyCollection<LegalActionOption> options)
        {
            if (observation.DecisionKey.Seat != stamp.Seat)
            {
                throw new ArgumentException(
                    "The ordinary observation belongs to another seat.",
                    nameof(observation));
            }

            if (!string.Equals(observation.RoundId, stamp.RoundId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The ordinary observation belongs to another round.",
                    nameof(observation));
            }

            if (!string.Equals(observation.DecisionId, stamp.DecisionId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The ordinary observation belongs to another decision.",
                    nameof(observation));
            }

            if (observation.ControlEpoch != stamp.ControlEpoch)
            {
                throw new ArgumentException(
                    "The ordinary observation belongs to another control epoch.",
                    nameof(observation));
            }

            if (observation.StateVersion != stamp.StateVersion)
            {
                throw new ArgumentException(
                    "The ordinary observation belongs to another state version.",
                    nameof(observation));
            }

            if (!stamp.DeadlineMillis.HasValue ||
                observation.DecisionDeadlineMillis != stamp.DeadlineMillis.Value)
            {
                throw new ArgumentException(
                    "The ordinary observation deadline does not match the stamp.",
                    nameof(observation));
            }

            if (stamp.Phase == AiPhase.Idle || stamp.Phase == AiPhase.ShufflePreparation)
            {
                throw new ArgumentException(
                    "This phase cannot carry an ordinary decision observation.",
                    nameof(observation));
            }

            if (observation.LegalActions.Count != options.Count)
            {
                throw new ArgumentException(
                    "The ordinary observation candidates do not match the frozen options.",
                    nameof(observation));
            }

            for (int i = 0; i < options.Count; i++)
            {
                if (!AiGuard.ActionsEqual(options[i], observation.LegalActions[i]))
                {
                    throw new ArgumentException(
                        "The ordinary observation candidates do not match the frozen options.",
                        nameof(observation));
                }
            }
        }

        private static void ValidatePreparation(
            AiCheatView cheatView,
            AiDecisionStamp stamp,
            ReadOnlyCollection<LegalActionOption> options,
            AiObservation? observation)
        {
            AiShufflePreparationFacts? preparation = cheatView.Preparation;
            if (preparation == null)
            {
                if (stamp.Phase == AiPhase.ShufflePreparation)
                {
                    throw new ArgumentException(
                        "A SHUFFLE_PREPARATION snapshot requires preparation facts.",
                        nameof(cheatView));
                }

                return;
            }

            if (stamp.RoundId == null ||
                !string.Equals(preparation.RoundId, stamp.RoundId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Preparation facts belong to another round.",
                    nameof(cheatView));
            }

            if (stamp.Phase == AiPhase.ShufflePreparation)
            {
                if (preparation.Confirmed)
                {
                    throw new ArgumentException(
                        "A confirmed preparation cannot still be the active preparation phase.",
                        nameof(cheatView));
                }

                if (stamp.DecisionId == null ||
                    !stamp.DeadlineMillis.HasValue ||
                    preparation.DeadlineMillis != stamp.DeadlineMillis.Value)
                {
                    throw new ArgumentException(
                        "Preparation identity/deadline does not match the stamp.",
                        nameof(cheatView));
                }

                if (options.Count != 0 || observation != null)
                {
                    throw new ArgumentException(
                        "A preparation window has no ordinary decision candidates.",
                        nameof(cheatView));
                }

                if (stamp.OfferId != null ||
                    cheatView.SleeveBloom?.PendingOffer != null ||
                    cheatView.BeamSwap?.PendingOffer != null)
                {
                    throw new ArgumentException(
                        "A preparation window cannot also carry a pending offer.",
                        nameof(cheatView));
                }
            }
            else if (!preparation.Confirmed)
            {
                throw new ArgumentException(
                    "An unconfirmed preparation is only valid in the preparation phase.",
                    nameof(cheatView));
            }
        }

        private static void ValidatePhase(
            AiCheatView cheatView,
            AiDecisionStamp stamp,
            ReadOnlyCollection<LegalActionOption> options,
            AiObservation? observation)
        {
            AiSleeveBloomOfferFacts? sleeveOffer = cheatView.SleeveBloom?.PendingOffer;
            AiBeamSwapOfferFacts? beamOffer = cheatView.BeamSwap?.PendingOffer;
            bool hasOffer = sleeveOffer != null || beamOffer != null;

            switch (stamp.Phase)
            {
                case AiPhase.Idle:
                    if (options.Count != 0)
                    {
                        throw new ArgumentException(
                            "An IDLE snapshot cannot carry ordinary legal options.",
                            nameof(cheatView));
                    }

                    if (observation != null)
                    {
                        throw new ArgumentException(
                            "An IDLE snapshot cannot carry an ordinary observation.",
                            nameof(cheatView));
                    }

                    if (hasOffer)
                    {
                        throw new ArgumentException(
                            "An IDLE snapshot cannot carry a pending offer.",
                            nameof(cheatView));
                    }

                    if (stamp.OfferId != null || stamp.DeadlineMillis.HasValue)
                    {
                        throw new ArgumentException(
                            "An IDLE snapshot has no offer id or deadline.",
                            nameof(cheatView));
                    }

                    return;

                case AiPhase.OrdinaryAction:
                    if (hasOffer)
                    {
                        throw new ArgumentException(
                            "An ordinary action cannot carry a pending offer.",
                            nameof(cheatView));
                    }

                    if (stamp.OfferId != null)
                    {
                        throw new ArgumentException(
                            "An ordinary action has no offer id.",
                            nameof(cheatView));
                    }

                    ValidateOffTurnWindow(cheatView, stamp);
                    return;

                case AiPhase.ShufflePreparation:
                    return;

                case AiPhase.OfferChoice:
                    if (sleeveOffer != null && beamOffer != null)
                    {
                        throw new ArgumentException(
                            "Only one pending offer can own an offer-choice window.",
                            nameof(cheatView));
                    }

                    if (!hasOffer)
                    {
                        throw new ArgumentException(
                            "An OFFER_CHOICE snapshot requires exactly one pending offer.",
                            nameof(cheatView));
                    }

                    if (stamp.OfferId == null ||
                        stamp.DecisionId == null ||
                        !stamp.DeadlineMillis.HasValue)
                    {
                        throw new ArgumentException(
                            "An offer-choice stamp requires offer id, decision id and deadline.",
                            nameof(cheatView));
                    }

                    if (sleeveOffer != null)
                    {
                        ValidateSleeveOffer(sleeveOffer, stamp);
                    }
                    else
                    {
                        ValidateBeamOffer(beamOffer!, cheatView, stamp);
                    }

                    return;

                default:
                    throw new ArgumentOutOfRangeException(nameof(stamp), stamp.Phase, "Unknown AI phase.");
            }
        }

        private static void ValidateSleeveOffer(AiSleeveBloomOfferFacts offer, AiDecisionStamp stamp)
        {
            if (!string.Equals(offer.OfferId, stamp.OfferId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The pending sleeve-bloom offer id does not match the stamp.",
                    nameof(offer));
            }

            if (!string.Equals(offer.DecisionId, stamp.DecisionId, StringComparison.Ordinal) ||
                offer.ControlEpoch != stamp.ControlEpoch ||
                offer.DeadlineMillis != stamp.DeadlineMillis!.Value)
            {
                throw new ArgumentException(
                    "The pending sleeve-bloom offer identity does not match the stamp.",
                    nameof(offer));
            }

            if (!string.Equals(offer.WindowId, stamp.WindowId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The pending sleeve-bloom offer window does not match the stamp.",
                    nameof(offer));
            }
        }

        private static void ValidateBeamOffer(
            AiBeamSwapOfferFacts offer,
            AiCheatView cheatView,
            AiDecisionStamp stamp)
        {
            if (!string.Equals(offer.OfferId, stamp.OfferId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The pending beam-swap offer id does not match the stamp.",
                    nameof(offer));
            }

            if (!string.Equals(offer.DecisionId, stamp.DecisionId, StringComparison.Ordinal) ||
                offer.ControlEpoch != stamp.ControlEpoch ||
                offer.DeadlineMillis != stamp.DeadlineMillis!.Value)
            {
                throw new ArgumentException(
                    "The pending beam-swap offer identity does not match the stamp.",
                    nameof(offer));
            }

            if (stamp.WindowId != null)
            {
                string? offTurnWindow = cheatView.SleeveBloom?.OffTurnWindowId;
                if (!string.Equals(stamp.WindowId, offTurnWindow, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The stamp window is neither the beam offer's window nor the current off-turn window.",
                        nameof(offer));
                }
            }
        }

        private static void ValidateOffTurnWindow(AiCheatView cheatView, AiDecisionStamp stamp)
        {
            if (stamp.WindowId == null)
            {
                return;
            }

            string? offTurnWindow = cheatView.SleeveBloom?.OffTurnWindowId;
            if (!string.Equals(stamp.WindowId, offTurnWindow, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "An ordinary-action window id must be the current off-turn window.",
                    nameof(cheatView));
            }
        }

        private static void ValidateFactIdentity(AiCheatView cheatView, AiDecisionStamp stamp)
        {
            AiTileMarkFacts? mark = cheatView.TileMark;
            if (mark != null)
            {
                if (mark.ControlEpoch != stamp.ControlEpoch ||
                    mark.StateVersion != stamp.StateVersion)
                {
                    throw new ArgumentException(
                        "The tile-mark facts belong to another control epoch or state version.",
                        nameof(cheatView));
                }

                if (stamp.Phase != AiPhase.ShufflePreparation &&
                    !string.Equals(mark.DecisionId, stamp.DecisionId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The tile-mark facts belong to another decision.",
                        nameof(cheatView));
                }
            }

            AiSleeveBloomFacts? bloom = cheatView.SleeveBloom;
            if (bloom != null)
            {
                if (bloom.ControlEpoch != stamp.ControlEpoch ||
                    bloom.StateVersion != stamp.StateVersion)
                {
                    throw new ArgumentException(
                        "The sleeve-bloom facts belong to another control epoch or state version.",
                        nameof(cheatView));
                }

                if (stamp.Phase != AiPhase.ShufflePreparation &&
                    !string.Equals(bloom.DecisionId, stamp.DecisionId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The sleeve-bloom facts belong to another decision.",
                        nameof(cheatView));
                }

                if (bloom.PendingOffer != null)
                {
                    if (bloom.PendingOffer.ControlEpoch != stamp.ControlEpoch)
                    {
                        throw new ArgumentException(
                            "The pending sleeve-bloom offer belongs to another control epoch.",
                            nameof(cheatView));
                    }

                    if (!string.Equals(bloom.PendingOffer.DecisionId, stamp.DecisionId, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "The pending sleeve-bloom offer belongs to another decision.",
                            nameof(cheatView));
                    }
                }
            }

            AiBeamSwapFacts? beam = cheatView.BeamSwap;
            if (beam != null)
            {
                if (beam.ControlEpoch != stamp.ControlEpoch ||
                    beam.StateVersion != stamp.StateVersion)
                {
                    throw new ArgumentException(
                        "The beam-swap facts belong to another control epoch or state version.",
                        nameof(cheatView));
                }

                if (stamp.Phase != AiPhase.ShufflePreparation &&
                    !string.Equals(beam.DecisionId, stamp.DecisionId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The beam-swap facts belong to another decision.",
                        nameof(cheatView));
                }

                if (beam.PendingOffer != null)
                {
                    if (beam.PendingOffer.ControlEpoch != stamp.ControlEpoch)
                    {
                        throw new ArgumentException(
                            "The pending beam-swap offer belongs to another control epoch.",
                            nameof(cheatView));
                    }

                    if (!string.Equals(beam.PendingOffer.DecisionId, stamp.DecisionId, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "The pending beam-swap offer belongs to another decision.",
                            nameof(cheatView));
                    }
                }
            }
        }
    }
}
