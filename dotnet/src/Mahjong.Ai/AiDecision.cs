using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// A pure strategy over one per-seat observation. The signature is intentionally the
    /// entire authority surface: no table state, no wall, no clock, no random source and no
    /// service object are available to an implementation.
    /// </summary>
    public interface IAiStrategy
    {
        string AlgorithmId { get; }

        int AlgorithmVersion { get; }

        /// <summary>
        /// Chooses one of <see cref="AiObservation.LegalActions"/>. The table runtime runs this
        /// method outside its authority queue. It must not read or retain any authority object.
        /// </summary>
        AiStrategicChoice Decide(AiObservation observation, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The strategy's ranked action plus an optional floating-point heuristic score. The score
    /// is only for comparison inside the strategy and never enters <see cref="CommandIntent"/>.
    /// </summary>
    public sealed class AiStrategicChoice
    {
        public AiStrategicChoice(LegalActionOption action, double heuristicScore)
        {
            Action = action ?? throw new ArgumentNullException(nameof(action));
            HeuristicScore = heuristicScore;
        }

        public LegalActionOption Action { get; }

        public double HeuristicScore { get; }
    }

    /// <summary>
    /// The completed asynchronous AI result. It keeps the observation it reasoned over so the
    /// submission step can re-check that the selected action was a legal candidate of that
    /// exact decision. <see cref="AuthorityHash"/> delegates to the recorded intent and does
    /// not include <see cref="HeuristicScore"/>.
    /// </summary>
    public sealed class AiDecision
    {
        public AiDecision(AiObservation observation, CommandIntent intent, double heuristicScore)
        {
            Observation = observation ?? throw new ArgumentNullException(nameof(observation));
            Intent = intent ?? throw new ArgumentNullException(nameof(intent));
            if (observation.DecisionKey != intent.DecisionKey ||
                observation.ControlEpoch != intent.ControlEpoch ||
                observation.Version != intent.ObservationVersion ||
                observation.StateVersion != intent.ObservationStateVersion)
            {
                throw new ArgumentException("The intent does not belong to this observation.", nameof(intent));
            }

            if (!observation.ContainsLegalAction(intent.Action))
            {
                throw new ArgumentException("The intent action is not a legal candidate of this observation.", nameof(intent));
            }

            HeuristicScore = heuristicScore;
        }

        public AiObservation Observation { get; }

        public CommandIntent Intent { get; }

        /// <summary>Non-authoritative heuristic value; excluded from <see cref="AuthorityHash"/>.</summary>
        public double HeuristicScore { get; }

        public string AuthorityHash => Intent.AuthorityHash;

        /// <summary>Adapts the accepted intent to the Domain deadline arbiter's input type.</summary>
        public AiDecisionSubmission ToDomainSubmission(long submittedAtMillis)
        {
            if (submittedAtMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(submittedAtMillis),
                    submittedAtMillis,
                    "Time must be non-negative.");
            }

            return new AiDecisionSubmission(
                Intent.DecisionKey,
                Intent.ControlEpoch,
                Intent.Action,
                submittedAtMillis);
        }
    }

    /// <summary>
    /// The authoritative state captured by the table runtime immediately before an AI result
    /// is admitted. This is a value snapshot, not a live authority object: the AI thread never
    /// holds a reference to the clock, registry or round state.
    /// </summary>
    public sealed class AiAuthoritySnapshot
    {
        private readonly ReadOnlyCollection<LegalActionOption> _legalActions;

        public AiAuthoritySnapshot(
            int seat,
            string roundId,
            DecisionKey? currentKey,
            long controlEpoch,
            bool seatAutomated,
            bool clockPaused,
            long deadlineMillis,
            IReadOnlyList<LegalActionOption> legalActions)
        {
            AiGuard.CheckSeat(seat, nameof(seat));
            RoundId = AiGuard.RequireText(roundId, nameof(roundId));
            AiGuard.CheckNonNegative(controlEpoch, nameof(controlEpoch));
            AiGuard.CheckNonNegative(deadlineMillis, nameof(deadlineMillis));
            if (currentKey.HasValue)
            {
                DecisionKey key = currentKey.Value;
                if (key.Seat != seat)
                {
                    throw new ArgumentException("Current decision must belong to the captured seat.", nameof(currentKey));
                }

                if (!string.Equals(key.RoundId, RoundId, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Current decision must belong to the captured round.", nameof(currentKey));
                }
            }

            if (legalActions == null)
            {
                throw new ArgumentNullException(nameof(legalActions));
            }

            var copy = new List<LegalActionOption>(legalActions.Count);
            for (int i = 0; i < legalActions.Count; i++)
            {
                LegalActionOption action = legalActions[i] ??
                    throw new ArgumentException("Legal candidate cannot be null.", nameof(legalActions));
                if (action.Seat != seat)
                {
                    throw new ArgumentException(
                        "Every current legal candidate must belong to the captured seat.",
                        nameof(legalActions));
                }

                copy.Add(action);
            }

            Seat = seat;
            CurrentKey = currentKey;
            ControlEpoch = controlEpoch;
            SeatAutomated = seatAutomated;
            ClockPaused = clockPaused;
            DeadlineMillis = deadlineMillis;
            _legalActions = new ReadOnlyCollection<LegalActionOption>(copy);
        }

        public int Seat { get; }

        public string RoundId { get; }

        public DecisionKey? CurrentKey { get; }

        public long ControlEpoch { get; }

        public bool SeatAutomated { get; }

        public bool ClockPaused { get; }

        public long DeadlineMillis { get; }

        public IReadOnlyList<LegalActionOption> LegalActions => _legalActions;

        public bool ContainsLegalAction(LegalActionOption action)
        {
            if (action == null)
            {
                return false;
            }

            for (int i = 0; i < _legalActions.Count; i++)
            {
                if (AiGuard.ActionsEqual(_legalActions[i], action))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Result of admission. On acceptance it carries the exact Domain submission that can be
    /// handed to <see cref="DeadlineArbiter"/>, plus the recorded intent for replay. A rejected
    /// result never carries a submission, so it cannot mutate the table.
    /// </summary>
    public sealed class AiSubmissionResult
    {
        private AiSubmissionResult(
            CommandIntent intent,
            TickRejectionCode code,
            string detail,
            AiDecisionSubmission? submission)
        {
            Intent = intent;
            Code = code;
            Detail = detail;
            Submission = submission;
        }

        public CommandIntent Intent { get; }

        public CommandIntent RecordedIntent => Intent;

        public TickRejectionCode Code { get; }

        public string Detail { get; }

        public AiDecisionSubmission? Submission { get; }

        public bool IsAccepted => Code == TickRejectionCode.None;

        internal static AiSubmissionResult Accept(CommandIntent intent, AiDecisionSubmission submission)
        {
            return new AiSubmissionResult(intent, TickRejectionCode.None, string.Empty, submission);
        }

        internal static AiSubmissionResult Reject(CommandIntent intent, TickRejectionCode code, string detail)
        {
            if (code == TickRejectionCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(code), code, "A rejection requires a non-None code.");
            }

            return new AiSubmissionResult(intent, code, detail ?? string.Empty, null);
        }
    }
}
