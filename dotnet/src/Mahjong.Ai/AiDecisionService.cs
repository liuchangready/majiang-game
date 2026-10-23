using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// P3.1/P3.3 integration port. The table runtime implements this by capturing, inside its
    /// serial authority queue, the current round id, active <see cref="DecisionKey"/>,
    /// <see cref="DecisionTiming.DeadlineMillis"/>, control epoch, automation state, pause
    /// state and frozen candidate list. The AI assembly never receives the live clock,
    /// controller registry or round state.
    /// </summary>
    public interface IAiAuthorityPort
    {
        AiAuthoritySnapshot Capture(int seat);
    }

    /// <summary>
    /// Runs AI reasoning outside the authority queue and admits the completed result back
    /// through an explicit, snapshot-based check.
    ///
    /// Lifecycle:
    /// 1. the actor captures an <see cref="AiObservation"/> and calls <see cref="ReasonAsync"/>;
    /// 2. the strategy runs on a worker thread and returns a <see cref="CommandIntent"/> plus a
    ///    non-authoritative score;
    /// 3. the actor calls <see cref="Submit(AiDecision, long)"/>, which compares the recorded
    ///    roundId, decisionId and controlEpoch with a fresh authority snapshot and rejects a
    ///    stale, late or non-candidate result without side effects;
    /// 4. the accepted <see cref="AiDecisionSubmission"/> is processed by the Domain
    ///    <see cref="DeadlineArbiter"/>, whose timeout-first order remains authoritative.
    ///
    /// Replay never runs a strategy. <see cref="Replay"/> injects the recorded intent directly.
    /// </summary>
    public sealed class AiDecisionService
    {
        private readonly IAiStrategy _strategy;
        private readonly IAiAuthorityPort? _authority;

        public AiDecisionService(IAiStrategy strategy, IAiAuthorityPort? authority = null)
        {
            _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
            AiGuard.RequireText(_strategy.AlgorithmId, "strategy.AlgorithmId");
            if (_strategy.AlgorithmVersion < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(strategy),
                    _strategy.AlgorithmVersion,
                    "Strategy algorithm version must be positive.");
            }

            _authority = authority;
        }

        /// <summary>
        /// Asynchronous reasoning over the immutable observation. The authority port is not
        /// touched on this path, and the caller's actor/thread is not blocked.
        /// </summary>
        public Task<AiDecision> ReasonAsync(
            AiObservation observation,
            CancellationToken cancellationToken = default)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            return Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AiStrategicChoice choice = _strategy.Decide(observation, cancellationToken) ??
                        throw new InvalidOperationException("The AI strategy returned no choice.");
                    cancellationToken.ThrowIfCancellationRequested();
                    var intent = new CommandIntent(
                        observation,
                        choice.Action,
                        _strategy.AlgorithmId,
                        _strategy.AlgorithmVersion);
                    return new AiDecision(observation, intent, choice.HeuristicScore);
                },
                cancellationToken);
        }

        /// <summary>Admits a result using the configured authoritative port.</summary>
        public AiSubmissionResult Submit(AiDecision decision, long submittedAtMillis)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (_authority == null)
            {
                throw new InvalidOperationException(
                    "No IAiAuthorityPort is configured; submit against an explicit authority snapshot instead.");
            }

            AiAuthoritySnapshot snapshot = _authority.Capture(decision.Intent.DecisionKey.Seat) ??
                throw new InvalidOperationException("The authority port returned no snapshot.");
            return Submit(decision, snapshot, submittedAtMillis);
        }

        /// <summary>
        /// Admits a result against an explicit snapshot captured by the actor. This overload
        /// is deterministic and testable; it performs no clock, registry or state reads.
        /// </summary>
        public AiSubmissionResult Submit(
            AiDecision decision,
            AiAuthoritySnapshot authority,
            long submittedAtMillis)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (authority == null)
            {
                throw new ArgumentNullException(nameof(authority));
            }

            if (submittedAtMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(submittedAtMillis),
                    submittedAtMillis,
                    "Time must be non-negative.");
            }

            CommandIntent intent = decision.Intent;

            // First reject a result that is internally inconsistent with the observation it
            // claims to have reasoned over. Such a value is malformed, not merely stale.
            if (decision.Observation.DecisionKey != intent.DecisionKey ||
                decision.Observation.ControlEpoch != intent.ControlEpoch ||
                decision.Observation.Version != intent.ObservationVersion ||
                decision.Observation.StateVersion != intent.ObservationStateVersion ||
                !decision.Observation.ContainsLegalAction(intent.Action))
            {
                return AiSubmissionResult.Reject(
                    intent,
                    TickRejectionCode.InvalidSubmission,
                    "The AI decision is inconsistent with its observation.");
            }

            DecisionKey? currentKey = authority.CurrentKey;
            if (!string.Equals(intent.DecisionKey.RoundId, authority.RoundId, StringComparison.Ordinal))
            {
                return AiSubmissionResult.Reject(
                    intent,
                    TickRejectionCode.StaleRound,
                    "The AI result belongs to another round.");
            }

            if (intent.DecisionKey.Seat != authority.Seat)
            {
                return AiSubmissionResult.Reject(
                    intent,
                    TickRejectionCode.StaleDecision,
                    "The AI result belongs to another seat.");
            }

            if (intent.ControlEpoch != authority.ControlEpoch)
            {
                return AiSubmissionResult.Reject(
                    intent,
                    TickRejectionCode.StaleEpoch,
                    "The AI result belongs to a superseded control epoch.");
            }

            if (!authority.SeatAutomated)
            {
                return AiSubmissionResult.Reject(
                    intent,
                    TickRejectionCode.SeatNotAutomated,
                    "The seat is no longer under automated control.");
            }

            if (authority.ClockPaused)
            {
                return AiSubmissionResult.Reject(
                    intent,
                    TickRejectionCode.ClockPaused,
                    "The decision clock is paused.");
            }

            if (!currentKey.HasValue || currentKey.Value != intent.DecisionKey)
            {
                return AiSubmissionResult.Reject(
                    intent,
                    TickRejectionCode.StaleDecision,
                    "The decision id no longer identifies the active decision.");
            }

            // At or after the authoritative deadline is late. Domain's DeadlineArbiter
            // processes the timeout first, so accepting at the boundary would contradict it.
            if (submittedAtMillis >= authority.DeadlineMillis)
            {
                return AiSubmissionResult.Reject(
                    intent,
                    TickRejectionCode.DeadlineExpired,
                    "The AI result arrived at or after the authoritative deadline.");
            }

            if (!authority.ContainsLegalAction(intent.Action))
            {
                return AiSubmissionResult.Reject(
                    intent,
                    TickRejectionCode.IllegalAction,
                    "The selected action is not a current legal candidate.");
            }

            return AiSubmissionResult.Accept(intent, decision.ToDomainSubmission(submittedAtMillis));
        }

        /// <summary>
        /// Deterministic replay: inject the recorded intent, never re-run a heuristic. The
        /// observation must still be the same decision and expose the recorded action as a
        /// legal candidate; a replay of an unrelated or corrupted record is refused.
        /// </summary>
        public static AiDecision Replay(CommandIntent recordedIntent, AiObservation observation)
        {
            if (recordedIntent == null)
            {
                throw new ArgumentNullException(nameof(recordedIntent));
            }

            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            if (observation.DecisionKey != recordedIntent.DecisionKey ||
                observation.ControlEpoch != recordedIntent.ControlEpoch ||
                observation.Version != recordedIntent.ObservationVersion ||
                observation.StateVersion != recordedIntent.ObservationStateVersion)
            {
                throw new ArgumentException(
                    "The recorded intent does not belong to this observation.",
                    nameof(recordedIntent));
            }

            if (!observation.ContainsLegalAction(recordedIntent.Action))
            {
                throw new ArgumentException(
                    "The recorded action is not a legal candidate of this observation.",
                    nameof(recordedIntent));
            }

            // The heuristic score is intentionally zero: it is not recorded authority data.
            return new AiDecision(observation, recordedIntent, 0d);
        }
    }
}
