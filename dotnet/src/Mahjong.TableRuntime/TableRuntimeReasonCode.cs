using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Stable P3.1 reason codes. They are uppercase ASCII so a P3.4 receipt/P5.2 row can store
    /// them without reinterpretation. Existing codes must not change meaning; P3.4 may append.
    /// </summary>
    public static class TableRuntimeReasonCode
    {
        public const string StaleStateVersion = "STALE_STATE_VERSION";
        public const string StaleOwnerEpoch = "STALE_OWNER_EPOCH";
        public const string StaleControlEpoch = "STALE_CONTROL_EPOCH";
        public const string DecisionMismatch = "DECISION_MISMATCH";
        public const string StaleDecision = "STALE_DECISION";
        public const string DeadlineExpired = "DEADLINE_EXPIRED";
        public const string RoundMismatch = "ROUND_MISMATCH";
        public const string MailboxFull = "MAILBOX_FULL";
        public const string ActorClosed = "ACTOR_CLOSED";
        public const string LeaseLost = "LEASE_LOST";
        public const string Recovering = "RECOVERING";
        public const string NotRecovering = "NOT_RECOVERING";
        public const string CommitFailed = "COMMIT_FAILED";
        public const string CommitCancelled = "COMMIT_CANCELLED";
        public const string StateEventMissing = "STATE_EVENT_MISSING";
        public const string StaleEventSeq = "STALE_EVENT_SEQ";
        public const string DomainVersionMismatch = "DOMAIN_VERSION_MISMATCH";
        public const string UnsupportedAction = "UNSUPPORTED_ACTION";
        public const string DecisionAlreadyOpen = "DECISION_ALREADY_OPEN";
        public const string InvalidSubmission = "INVALID_SUBMISSION";
        public const string NoDefaultAction = "NO_DEFAULT_ACTION";
        public const string InternalError = "INTERNAL_ERROR";

        // P3.3 control/timing additions. Existing codes keep their P3.1 meaning.
        public const string StaleControlVersion = "STALE_CONTROL_VERSION";
        public const string ClockRegression = "CLOCK_REGRESSION";
        public const string ClockInvalid = "CLOCK_INVALID";
        public const string DeadlineOverflow = "DEADLINE_OVERFLOW";
        public const string ControlTimeOverflow = "CONTROL_TIME_OVERFLOW";
        public const string ControlEpochOverflow = "CONTROL_EPOCH_OVERFLOW";
        public const string ControlVersionOverflow = "CONTROL_VERSION_OVERFLOW";
        public const string RecoveryGenerationOverflow = "RECOVERY_GENERATION_OVERFLOW";
        public const string GraceNotDue = "GRACE_NOT_DUE";
        public const string StaleGrace = "STALE_GRACE";
        public const string NoGraceDeadline = "NO_GRACE_DEADLINE";
        public const string GraceNotApplicable = "GRACE_NOT_APPLICABLE";
        public const string ControlPolicyBusy = "CONTROL_POLICY_BUSY";
        public const string ControlCommitFailed = "CONTROL_COMMIT_FAILED";
        public const string ControlCommitCancelled = "CONTROL_COMMIT_CANCELLED";
        public const string RecoveryControlRequired = "RECOVERY_CONTROL_REQUIRED";
        public const string RecoveryTimingInvalid = "RECOVERY_TIMING_INVALID";
        public const string RecoveryControlInvalid = "RECOVERY_CONTROL_INVALID";
        public const string AiObservationUnavailable = "AI_OBSERVATION_UNAVAILABLE";
        public const string SeatNotAutomated = "SEAT_NOT_AUTOMATED";

        public static string FromDomain(RoundTransitionErrorCode code)
        {
            switch (code)
            {
                case RoundTransitionErrorCode.RoundFinished:
                    return "ROUND_FINISHED";
                case RoundTransitionErrorCode.WrongPhase:
                    return "WRONG_PHASE";
                case RoundTransitionErrorCode.WrongPlayer:
                    return "NOT_YOUR_TURN";
                case RoundTransitionErrorCode.InvalidSeat:
                    return "INVALID_SEAT";
                case RoundTransitionErrorCode.InvalidTile:
                    return "INVALID_TILE";
                case RoundTransitionErrorCode.StaleSourceVersion:
                    return "STALE_SOURCE_VERSION";
                case RoundTransitionErrorCode.RoundIdMismatch:
                    return RoundMismatch;
                case RoundTransitionErrorCode.NoResponseWindow:
                    return "NO_RESPONSE_WINDOW";
                case RoundTransitionErrorCode.SeatNotEligible:
                    return "SEAT_NOT_ELIGIBLE";
                case RoundTransitionErrorCode.DuplicateResponse:
                    return "DUPLICATE_RESPONSE";
                case RoundTransitionErrorCode.TileTransitionRejected:
                    return "TILE_TRANSITION_REJECTED";
                case RoundTransitionErrorCode.SettlementOutcomeMismatch:
                    return "SETTLEMENT_OUTCOME_MISMATCH";
                case RoundTransitionErrorCode.SettlementConservationViolation:
                    return "SETTLEMENT_CONSERVATION_VIOLATION";
                case RoundTransitionErrorCode.WallNotExhausted:
                    return "WALL_NOT_EXHAUSTED";
                case RoundTransitionErrorCode.InvalidEligibleSeats:
                    return "INVALID_ELIGIBLE_SEATS";
                case RoundTransitionErrorCode.InvalidChankan:
                    return "INVALID_CHANKAN";
                case RoundTransitionErrorCode.NoDrawnTile:
                    return "NO_DRAWN_TILE";
                case RoundTransitionErrorCode.InvalidRoundSpec:
                    return "INVALID_ROUND_SPEC";
                case RoundTransitionErrorCode.MatchCommandRejected:
                    return "MATCH_COMMAND_REJECTED";
                case RoundTransitionErrorCode.ForeignSourceState:
                    return "FOREIGN_SOURCE_STATE";
                default:
                    return InternalError;
            }
        }
    }
}
