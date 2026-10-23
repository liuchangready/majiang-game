using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    /// <summary>
    /// Immutable whole-match lifecycle (V4 <c>MatchSession</c>).
    ///
    /// Boundaries kept deliberately separate:
    /// - the round owns the single-round terminal state;
    /// - the match owns inter-round scores, honba, riichi sticks, records, agari-yame and the
    ///   final session result;
    /// - room/transport lifecycle is not modelled here.
    ///
    /// Every accepted transition creates a new instance and increments <see cref="Version"/>
    /// exactly once. Rejections return a value with no state and no version change.
    /// </summary>
    public sealed class MatchSession
    {
        private readonly ReadOnlyCollection<long> _initialScores;
        private readonly ReadOnlyCollection<long> _betweenRoundScores;
        private readonly ReadOnlyCollection<RoundRecord> _roundRecords;
        private readonly SessionResult? _result;

        private MatchSession(
            string sessionId,
            MatchRuleConfig ruleConfig,
            long version,
            SessionLifecycle lifecycle,
            SessionLifecycle prePauseLifecycle,
            int currentDealer,
            int currentRoundWind,
            int currentRoundNumber,
            int currentHonba,
            IReadOnlyList<long> initialScores,
            IReadOnlyList<long> betweenRoundScores,
            int betweenRoundRiichiSticks,
            RoundStateMachine? activeRound,
            IReadOnlyList<RoundRecord> roundRecords,
            ProgressionPlan? pendingPlan,
            bool canDealerDeclareAgariYame,
            SessionResult? result)
        {
            SessionId = sessionId;
            RuleConfig = ruleConfig;
            Version = version;
            Lifecycle = lifecycle;
            PrePauseLifecycle = prePauseLifecycle;
            CurrentDealer = currentDealer;
            CurrentRoundWind = currentRoundWind;
            CurrentRoundNumber = currentRoundNumber;
            CurrentHonba = currentHonba;
            _initialScores = new ReadOnlyCollection<long>(new List<long>(initialScores));
            _betweenRoundScores = new ReadOnlyCollection<long>(new List<long>(betweenRoundScores));
            BetweenRoundRiichiSticks = betweenRoundRiichiSticks;
            ActiveRound = activeRound;
            _roundRecords = new ReadOnlyCollection<RoundRecord>(new List<RoundRecord>(roundRecords));
            PendingPlan = pendingPlan;
            CanDealerDeclareAgariYame = canDealerDeclareAgariYame;
            _result = result;
        }

        public string SessionId { get; }

        public MatchRuleConfig RuleConfig { get; }

        public long Version { get; }

        public SessionLifecycle Lifecycle { get; }

        public SessionLifecycle PrePauseLifecycle { get; }

        public int CurrentDealer { get; }

        public int CurrentRoundWind { get; }

        public int CurrentRoundNumber { get; }

        public int CurrentHonba { get; }

        public IReadOnlyList<long> InitialScores => _initialScores;

        public IReadOnlyList<long> BetweenRoundScores => _betweenRoundScores;

        public int BetweenRoundRiichiSticks { get; }

        public RoundStateMachine? ActiveRound { get; }

        public IReadOnlyList<RoundRecord> RoundRecords => _roundRecords;

        public ProgressionPlan? PendingPlan { get; }

        public bool CanDealerDeclareAgariYame { get; }

        public SessionResult? Result => _result;

        public bool IsFinished => Lifecycle == SessionLifecycle.Finished;

        public bool IsClosed => Lifecycle == SessionLifecycle.Closed;

        public static MatchSession Create(
            string sessionId,
            MatchRuleConfig? ruleConfig = null,
            IReadOnlyList<long>? initialScores = null,
            int initialDealer = 0,
            int initialRoundWind = 1,
            int initialRoundNumber = 0,
            int initialHonba = 0,
            int initialRiichiSticks = 0)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("sessionId cannot be blank.", nameof(sessionId));
            }

            V5Guard.CheckSeat(initialDealer, nameof(initialDealer));
            if (initialRoundWind < 1 || initialRoundWind > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(initialRoundWind), initialRoundWind, "Round wind must be 1..3.");
            }

            if (initialRoundNumber < 0 || initialRoundNumber > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(initialRoundNumber), initialRoundNumber, "Round number must be 0..3.");
            }

            if (initialHonba < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialHonba), initialHonba, "Honba must be non-negative.");
            }

            if (initialRiichiSticks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialRiichiSticks), initialRiichiSticks, "Riichi sticks must be non-negative.");
            }

            IReadOnlyList<long> scores = initialScores == null
                ? new ReadOnlyCollection<long>(new List<long>(new long[] { 25000L, 25000L, 25000L, 25000L }))
                : V5Guard.CopyScores(initialScores, nameof(initialScores));

            return new MatchSession(
                sessionId,
                ruleConfig ?? new MatchRuleConfig(),
                0,
                SessionLifecycle.Created,
                SessionLifecycle.Created,
                initialDealer,
                initialRoundWind,
                initialRoundNumber,
                initialHonba,
                scores,
                scores,
                initialRiichiSticks,
                null,
                Array.Empty<RoundRecord>(),
                null,
                false,
                null);
        }

        /// <summary>Starts the first round. The supplied round must match the match's frozen seat/score state.</summary>
        public MatchTransition StartSession(RoundStateMachine firstRound)
        {
            if (firstRound == null)
            {
                throw new ArgumentNullException(nameof(firstRound));
            }

            if (Lifecycle != SessionLifecycle.Created && Lifecycle != SessionLifecycle.WaitingStart)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.InvalidLifecycle,
                    "StartSession is only legal in CREATED/WAITING_START, was " + Lifecycle + ".");
            }

            if (firstRound.IsFinished)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "The first round is already finished.");
            }

            if (firstRound.DealerSeat != CurrentDealer ||
                firstRound.RoundWind != CurrentRoundWind ||
                firstRound.RoundNumber != CurrentRoundNumber ||
                firstRound.HonbaSticks != CurrentHonba ||
                !ScoresEqual(firstRound.Scores, _betweenRoundScores) ||
                firstRound.RiichiSticks != BetweenRoundRiichiSticks)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "The first round's dealer/wind/number/honba/scores/sticks do not match the match.");
            }

            MatchSession next = Copy(
                checked(Version + 1),
                SessionLifecycle.RoundActive,
                PrePauseLifecycle,
                CurrentDealer,
                CurrentRoundWind,
                CurrentRoundNumber,
                CurrentHonba,
                _initialScores,
                _betweenRoundScores,
                BetweenRoundRiichiSticks,
                firstRound,
                _roundRecords,
                null,
                false,
                null);
            return MatchTransition.Accepted(next);
        }

        /// <summary>
        /// Accepts the finished active round, computes V4 progression and either finishes the
        /// match, waits for an agari-yame decision, or prepares the next round. This overload
        /// assumes the active round's event history was already delivered (for example by
        /// <see cref="UpdateActiveRound"/>) and therefore returns no round events.
        /// </summary>
        public MatchTransition AcceptRoundResult(PersistedUtcTime completedAtUtc, long completedAtMonotonicMillis)
        {
            if (IsFinished || IsClosed)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.SessionFinished,
                    "Session is already " + Lifecycle + ".");
            }

            if (Lifecycle != SessionLifecycle.RoundActive || ActiveRound == null)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.NoActiveRound,
                    "No active round; lifecycle is " + Lifecycle + ".");
            }

            RoundResult? roundResult = ActiveRound.Result;
            if (roundResult == null)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.ActiveRoundNotFinished,
                    "The active round has not reached ROUND_END.");
            }

            return AcceptResultCore(
                roundResult,
                Array.Empty<RoundEvent>(),
                completedAtUtc,
                completedAtMonotonicMillis);
        }

        /// <summary>
        /// Accepts the explicit finished round instance produced by
        /// <see cref="RoundStateMachine.Execute"/>. Because rounds are immutable, the authority
        /// must hand the new instance back instead of assuming the stored reference mutated.
        /// The returned transition carries only events added after the current ActiveRound
        /// history; a direct terminal accept therefore delivers previously undelivered terminal
        /// events once, while an already-installed terminal ActiveRound yields no duplicates.
        /// </summary>
        public MatchTransition AcceptRoundResult(
            RoundStateMachine finishedRound,
            PersistedUtcTime completedAtUtc,
            long completedAtMonotonicMillis)
        {
            if (finishedRound == null)
            {
                throw new ArgumentNullException(nameof(finishedRound));
            }

            if (IsFinished || IsClosed)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.SessionFinished,
                    "Session is already " + Lifecycle + ".");
            }

            if (Lifecycle != SessionLifecycle.RoundActive || ActiveRound == null)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.NoActiveRound,
                    "No active round; lifecycle is " + Lifecycle + ".");
            }

            if (!string.Equals(finishedRound.RoundId, ActiveRound.RoundId, StringComparison.Ordinal))
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "Finished round '" + finishedRound.RoundId + "' is not the active round '" + ActiveRound.RoundId + "'.");
            }

            if (!finishedRound.IsFinished || finishedRound.Result == null)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.ActiveRoundNotFinished,
                    "The supplied round has not reached ROUND_END.");
            }

            if (!IsDerivedFrom(finishedRound, ActiveRound))
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.ForeignRoundBranch,
                    "Finished round is not descended from the session's exact active round state.");
            }

            if (!TryComputeEventDelta(ActiveRound, finishedRound, out List<RoundEvent> addedEvents))
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "Finished round event history does not extend the active round history.");
            }

            return AcceptResultCore(
                finishedRound.Result,
                addedEvents,
                completedAtUtc,
                completedAtMonotonicMillis);
        }

        /// <summary>
        /// Installs a newer immutable round instance after an accepted round command. Session
        /// version is unchanged because this is not a match-level transition. The returned
        /// transition carries only the event increment between the current ActiveRound history
        /// and <paramref name="nextRound"/>; earlier events are never repeated.
        /// </summary>
        public MatchTransition UpdateActiveRound(RoundStateMachine nextRound)
        {
            if (nextRound == null)
            {
                throw new ArgumentNullException(nameof(nextRound));
            }

            if (Lifecycle != SessionLifecycle.RoundActive || ActiveRound == null)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.NoActiveRound,
                    "No active round; lifecycle is " + Lifecycle + ".");
            }

            if (!string.Equals(nextRound.RoundId, ActiveRound.RoundId, StringComparison.Ordinal))
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "Round '" + nextRound.RoundId + "' is not the active round '" + ActiveRound.RoundId + "'.");
            }

            if (nextRound.Version <= ActiveRound.Version)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "Round version " + nextRound.Version + " is not newer than " + ActiveRound.Version + ".");
            }

            if (nextRound.Version != ActiveRound.Version + 1L)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "UpdateActiveRound only installs the immediate successor; active=" +
                    ActiveRound.Version + ", candidate=" + nextRound.Version + ".");
            }

            if (!ReferenceEquals(nextRound.ParentState, ActiveRound))
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.ForeignRoundBranch,
                    "Candidate round is not a direct child of the session's exact active round state.");
            }

            if (!TryComputeEventDelta(ActiveRound, nextRound, out List<RoundEvent> addedEvents) ||
                addedEvents.Count == 0)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "Candidate round event history does not extend the active round history.");
            }

            MatchSession next = Copy(
                Version,
                Lifecycle,
                PrePauseLifecycle,
                CurrentDealer,
                CurrentRoundWind,
                CurrentRoundNumber,
                CurrentHonba,
                _initialScores,
                _betweenRoundScores,
                BetweenRoundRiichiSticks,
                nextRound,
                _roundRecords,
                PendingPlan,
                CanDealerDeclareAgariYame,
                _result);
            return MatchTransition.Accepted(next, null, addedEvents);
        }

        private MatchTransition AcceptResultCore(
            RoundResult roundResult,
            IReadOnlyList<RoundEvent> addedEvents,
            PersistedUtcTime completedAtUtc,
            long completedAtMonotonicMillis)
        {
            if (completedAtUtc == null)
            {
                throw new ArgumentNullException(nameof(completedAtUtc));
            }

            if (completedAtMonotonicMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(completedAtMonotonicMillis),
                    completedAtMonotonicMillis,
                    "Monotonic time must be non-negative.");
            }

            if (HasRecord(roundResult.RoundId))
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.DuplicateResult,
                    "Round " + roundResult.RoundId + " was already accepted.");
            }

            var progression = new MatchProgression(RuleConfig);
            ProgressionInput input = new ProgressionInput(
                CurrentDealer,
                CurrentRoundWind,
                CurrentRoundNumber,
                CurrentHonba,
                roundResult.FinalScores,
                roundResult.Outcome,
                roundResult.RemainingRiichiSticks,
                false);
            ProgressionResult planResult = progression.NextRound(input);

            var record = new RoundRecord(
                roundResult.RoundId,
                roundResult.ResultId,
                roundResult.Outcome,
                roundResult.FinalScores,
                roundResult.TotalDeltas,
                roundResult.RemainingRiichiSticks);
            var records = new List<RoundRecord>(_roundRecords) { record };

            if (planResult is ProgressionResult.MatchFinished finished)
            {
                var sessionResult = new SessionResult(
                    SessionId,
                    finished.FinalScores,
                    finished.Rankings,
                    finished.Reason,
                    finished.RemainingRiichiSticksTransferredTo,
                    completedAtUtc,
                    completedAtMonotonicMillis);
                MatchSession finishedSession = Copy(
                    checked(Version + 1),
                    SessionLifecycle.Finished,
                    PrePauseLifecycle,
                    CurrentDealer,
                    CurrentRoundWind,
                    CurrentRoundNumber,
                    CurrentHonba,
                    _initialScores,
                    finished.FinalScores,
                    0,
                    null,
                    records,
                    null,
                    false,
                    sessionResult);
                return MatchTransition.Accepted(finishedSession, sessionResult, addedEvents);
            }

            var nextRound = (ProgressionResult.NextRound)planResult;
            bool dealerChoice = nextRound.CanDealerDeclareAgariYame;
            var plan = new ProgressionPlan(
                dealerChoice ? ProgressionPlanKind.WaitDealerChoice : ProgressionPlanKind.PrepareNextRound,
                nextRound,
                null,
                dealerChoice);
            MatchSession nextSession = Copy(
                checked(Version + 1),
                dealerChoice ? SessionLifecycle.WaitingDealerChoice : SessionLifecycle.WaitingReady,
                PrePauseLifecycle,
                nextRound.DealerSeat,
                nextRound.RoundWind,
                nextRound.RoundNumber,
                nextRound.HonbaSticks,
                _initialScores,
                roundResult.FinalScores,
                roundResult.RemainingRiichiSticks,
                null,
                records,
                plan,
                dealerChoice,
                null);
            return MatchTransition.Accepted(nextSession, null, addedEvents);
        }

        /// <summary>Match-level agari-yame choice; never sent into <see cref="RoundStateMachine"/>.</summary>
        public MatchTransition ChooseDealerEnd(bool agariYame, PersistedUtcTime completedAtUtc, long completedAtMonotonicMillis)
        {
            if (completedAtUtc == null)
            {
                throw new ArgumentNullException(nameof(completedAtUtc));
            }

            if (Lifecycle != SessionLifecycle.WaitingDealerChoice || PendingPlan == null)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.DealerChoiceUnavailable,
                    "No dealer-end choice is pending; lifecycle is " + Lifecycle + ".");
            }

            if (agariYame)
            {
                var progression = new MatchProgression(RuleConfig);
                ProgressionInput input = new ProgressionInput(
                    CurrentDealer,
                    CurrentRoundWind,
                    CurrentRoundNumber,
                    CurrentHonba,
                    BetweenRoundScores,
                    BuildAgariYameOutcome(),
                    BetweenRoundRiichiSticks,
                    true);
                ProgressionResult result = progression.NextRound(input);
                if (!(result is ProgressionResult.MatchFinished finished))
                {
                    return MatchTransition.Rejected(
                        MatchTransitionErrorCode.DealerChoiceUnavailable,
                        "Agari-yame did not produce a finished match.");
                }

                var sessionResult = new SessionResult(
                    SessionId,
                    finished.FinalScores,
                    finished.Rankings,
                    MatchFinishReason.AgariYame,
                    finished.RemainingRiichiSticksTransferredTo,
                    completedAtUtc,
                    completedAtMonotonicMillis);
                MatchSession finishedSession = Copy(
                    checked(Version + 1),
                    SessionLifecycle.Finished,
                    PrePauseLifecycle,
                    CurrentDealer,
                    CurrentRoundWind,
                    CurrentRoundNumber,
                    CurrentHonba,
                    _initialScores,
                    finished.FinalScores,
                    0,
                    null,
                    _roundRecords,
                    null,
                    false,
                    sessionResult);
                return MatchTransition.Accepted(finishedSession, sessionResult);
            }

            if (PendingPlan.NextRound == null)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.NoPendingProgression,
                    "Dealer-end choice has no next-round plan.");
            }

            MatchSession nextSession = Copy(
                checked(Version + 1),
                SessionLifecycle.WaitingReady,
                PrePauseLifecycle,
                PendingPlan.NextRound.DealerSeat,
                PendingPlan.NextRound.RoundWind,
                PendingPlan.NextRound.RoundNumber,
                PendingPlan.NextRound.HonbaSticks,
                _initialScores,
                _betweenRoundScores,
                BetweenRoundRiichiSticks,
                null,
                _roundRecords,
                new ProgressionPlan(ProgressionPlanKind.PrepareNextRound, PendingPlan.NextRound),
                false,
                null);
            return MatchTransition.Accepted(nextSession);
        }

        /// <summary>
        /// Starts a brand-new round instance. A dealer repeat must still use a new roundId;
        /// reusing any recorded/active roundId is rejected without mutation.
        /// </summary>
        public MatchTransition StartNextRound(RoundStateMachine nextRound)
        {
            if (nextRound == null)
            {
                throw new ArgumentNullException(nameof(nextRound));
            }

            if (IsFinished || IsClosed)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.SessionFinished,
                    "Session is already " + Lifecycle + ".");
            }

            if (Lifecycle != SessionLifecycle.WaitingReady || PendingPlan == null || PendingPlan.NextRound == null)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.NoPendingProgression,
                    "No next round is ready; lifecycle is " + Lifecycle + ".");
            }

            ProgressionResult.NextRound expected = PendingPlan.NextRound!;
            if (nextRound.IsFinished || nextRound.Version != 0 || nextRound.Phase != RoundPhase.Stacking)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "A next round must be a fresh STACKING round at version 0.");
            }

            if (HasRecord(nextRound.RoundId))
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundIdReuse,
                    "RoundId '" + nextRound.RoundId + "' was already used in this session.");
            }

            if (nextRound.DealerSeat != expected.DealerSeat ||
                nextRound.RoundWind != expected.RoundWind ||
                nextRound.RoundNumber != expected.RoundNumber ||
                nextRound.HonbaSticks != expected.HonbaSticks ||
                nextRound.RiichiSticks != BetweenRoundRiichiSticks ||
                !ScoresEqual(nextRound.Scores, _betweenRoundScores))
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.RoundStateMismatch,
                    "Next round dealer/wind/number/honba/scores/sticks do not match the pending progression.");
            }

            MatchSession nextSession = Copy(
                checked(Version + 1),
                SessionLifecycle.RoundActive,
                PrePauseLifecycle,
                CurrentDealer,
                CurrentRoundWind,
                CurrentRoundNumber,
                CurrentHonba,
                _initialScores,
                _betweenRoundScores,
                BetweenRoundRiichiSticks,
                nextRound,
                _roundRecords,
                null,
                false,
                null);
            return MatchTransition.Accepted(nextSession);
        }

        public MatchTransition Pause()
        {
            if (Lifecycle == SessionLifecycle.Paused)
            {
                return MatchTransition.Accepted(this);
            }

            if (Lifecycle != SessionLifecycle.RoundActive &&
                Lifecycle != SessionLifecycle.WaitingReady &&
                Lifecycle != SessionLifecycle.WaitingDealerChoice)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.InvalidLifecycle,
                    "Cannot pause from " + Lifecycle + ".");
            }

            MatchSession next = Copy(
                checked(Version + 1),
                SessionLifecycle.Paused,
                Lifecycle,
                CurrentDealer,
                CurrentRoundWind,
                CurrentRoundNumber,
                CurrentHonba,
                _initialScores,
                _betweenRoundScores,
                BetweenRoundRiichiSticks,
                ActiveRound,
                _roundRecords,
                PendingPlan,
                CanDealerDeclareAgariYame,
                _result);
            return MatchTransition.Accepted(next);
        }

        public MatchTransition Resume()
        {
            if (Lifecycle != SessionLifecycle.Paused)
            {
                return MatchTransition.Rejected(
                    MatchTransitionErrorCode.InvalidLifecycle,
                    "Cannot resume from " + Lifecycle + ".");
            }

            SessionLifecycle restored = PrePauseLifecycle == SessionLifecycle.Paused
                ? SessionLifecycle.WaitingReady
                : PrePauseLifecycle;
            MatchSession next = Copy(
                checked(Version + 1),
                restored,
                restored,
                CurrentDealer,
                CurrentRoundWind,
                CurrentRoundNumber,
                CurrentHonba,
                _initialScores,
                _betweenRoundScores,
                BetweenRoundRiichiSticks,
                ActiveRound,
                _roundRecords,
                PendingPlan,
                CanDealerDeclareAgariYame,
                _result);
            return MatchTransition.Accepted(next);
        }

        public MatchTransition Close()
        {
            if (IsClosed)
            {
                return MatchTransition.Accepted(this);
            }

            MatchSession next = Copy(
                checked(Version + 1),
                SessionLifecycle.Closed,
                PrePauseLifecycle,
                CurrentDealer,
                CurrentRoundWind,
                CurrentRoundNumber,
                CurrentHonba,
                _initialScores,
                _betweenRoundScores,
                BetweenRoundRiichiSticks,
                null,
                _roundRecords,
                null,
                false,
                _result);
            return MatchTransition.Accepted(next);
        }

        private RoundOutcome BuildAgariYameOutcome()
        {
            // The recorded final round is the source of truth; agari-yame only changes the
            // match-level decision, never the completed round's outcome.
            RoundRecord last = _roundRecords[_roundRecords.Count - 1];
            return last.Outcome;
        }

        private bool HasRecord(string roundId)
        {
            for (int i = 0; i < _roundRecords.Count; i++)
            {
                if (string.Equals(_roundRecords[i].RoundId, roundId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private MatchSession Copy(
            long version,
            SessionLifecycle lifecycle,
            SessionLifecycle prePauseLifecycle,
            int dealer,
            int wind,
            int number,
            int honba,
            IReadOnlyList<long> initialScores,
            IReadOnlyList<long> betweenScores,
            int riichiSticks,
            RoundStateMachine? activeRound,
            IReadOnlyList<RoundRecord> records,
            ProgressionPlan? pending,
            bool canAgariYame,
            SessionResult? result)
        {
            return new MatchSession(
                SessionId,
                RuleConfig,
                version,
                lifecycle,
                prePauseLifecycle,
                dealer,
                wind,
                number,
                honba,
                initialScores,
                betweenScores,
                riichiSticks,
                activeRound,
                records,
                pending,
                canAgariYame,
                result);
        }

        private static bool ScoresEqual(IReadOnlyList<long> left, IReadOnlyList<long> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns only events added after <paramref name="source"/>'s history, after proving
        /// that the target history has the exact same prefix (ordinal/type/seat/tile/detail)
        /// and contiguous ordinals. It never returns the full round history.
        /// </summary>
        private static bool TryComputeEventDelta(
            RoundStateMachine source,
            RoundStateMachine target,
            out List<RoundEvent> delta)
        {
            delta = new List<RoundEvent>();
            if (target.Events.Count < source.Events.Count)
            {
                return false;
            }

            for (int i = 0; i < source.Events.Count; i++)
            {
                RoundEvent before = source.Events[i];
                RoundEvent after = target.Events[i];
                if (before.Ordinal != i || after.Ordinal != i ||
                    before.Type != after.Type ||
                    before.Seat != after.Seat ||
                    !Nullable.Equals(before.Tile, after.Tile) ||
                    !string.Equals(before.Detail, after.Detail, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            for (int i = source.Events.Count; i < target.Events.Count; i++)
            {
                RoundEvent added = target.Events[i];
                if (added.Ordinal != i)
                {
                    return false;
                }

                delta.Add(added);
            }

            return true;
        }

        /// <summary>
        /// Walks the immutable round parent chain. A completed round may be several accepted
        /// round commands ahead of the session's current active round; it is valid only when
        /// every step has a decreasing version and the chain terminates exactly at
        /// <paramref name="source"/> by reference. Foreign branches sharing roundId/version are
        /// rejected.
        /// </summary>
        private static bool IsDerivedFrom(RoundStateMachine candidate, RoundStateMachine source)
        {
            if (ReferenceEquals(candidate, source))
            {
                return true;
            }

            RoundStateMachine? cursor = candidate;
            int steps = 0;
            while (cursor != null)
            {
                if (ReferenceEquals(cursor, source))
                {
                    return true;
                }

                if (cursor.Version <= 0L || cursor.ParentState == null)
                {
                    return false;
                }

                if (cursor.ParentState.Version != cursor.Version - 1L)
                {
                    return false;
                }

                cursor = cursor.ParentState;
                steps++;
                if (steps > 100_000)
                {
                    return false;
                }
            }

            return false;
        }
    }
}
