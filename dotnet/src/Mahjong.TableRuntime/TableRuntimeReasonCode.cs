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
        public const string RecoveryGraceTimingRequired = "RECOVERY_GRACE_TIMING_REQUIRED";
        public const string RecoveryGraceTimingInvalid = "RECOVERY_GRACE_TIMING_INVALID";
        public const string StaleRecoveryGeneration = "STALE_RECOVERY_GENERATION";
        public const string AiObservationUnavailable = "AI_OBSERVATION_UNAVAILABLE";
        public const string DeadlineNotDue = "DEADLINE_NOT_DUE";
        public const string SeatNotAutomated = "SEAT_NOT_AUTOMATED";

        // P3.4 durable receipt/lease/checkpoint additions.
        public const string CommitOutcomeUnknown = "COMMIT_OUTCOME_UNKNOWN";
        public const string DurableNotConfigured = "DURABLE_NOT_CONFIGURED";
        public const string PayloadIdentityRequired = "PAYLOAD_IDENTITY_REQUIRED";
        public const string ScopeAccountMismatch = "SCOPE_ACCOUNT_MISMATCH";
        public const string ScopeRoleMismatch = "SCOPE_ROLE_MISMATCH";
        public const string ScopeRoomMismatch = "SCOPE_ROOM_MISMATCH";
        public const string ScopeRevoked = "SCOPE_REVOKED";
        public const string ReceiptNotFound = "RECEIPT_NOT_FOUND";
        public const string RecoveryFailed = "RECOVERY_FAILED";
        public const string CheckpointMissing = "CHECKPOINT_MISSING";
        public const string CheckpointInvalid = "CHECKPOINT_INVALID";
        public const string CheckpointSchemaUnsupported = "CHECKPOINT_SCHEMA_UNSUPPORTED";
        public const string RecoveryEventGap = "RECOVERY_EVENT_GAP";
        public const string RecoveryOrdinalInvalid = "RECOVERY_ORDINAL_INVALID";
        public const string RecoveryCommandMissing = "RECOVERY_COMMAND_MISSING";
        public const string RecoveryReplayMismatch = "RECOVERY_REPLAY_MISMATCH";
        public const string RecoveryStateHashMismatch = "RECOVERY_STATE_HASH_MISMATCH";
        public const string RecoveryOutboxMissing = "RECOVERY_OUTBOX_MISSING";
        public const string RecoveryLeaseHeld = "RECOVERY_LEASE_HELD";
        public const string LeaseRenewFailed = "LEASE_RENEW_FAILED";
        public const string OutboxConflict = "OUTBOX_CONFLICT";
        public const string RecoveryStateSinkRequired = "RECOVERY_STATE_SINK_REQUIRED";
        public const string RecoveryStateSinkFailed = "RECOVERY_STATE_SINK_FAILED";
        public const string RecoveryPrivateStateUnsupported = "RECOVERY_PRIVATE_STATE_UNSUPPORTED";
        public const string RecoveryStateChanged = "RECOVERY_STATE_CHANGED";

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
