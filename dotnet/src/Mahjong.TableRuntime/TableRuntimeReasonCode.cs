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

        // G1/N-1b cheat execution additions.
        public const string CheatNotConfigured = "CHEAT_NOT_CONFIGURED";
        public const string CheatRejected = "CHEAT_REJECTED";
        public const string CheatReplayed = "CHEAT_REPLAYED";
        public const string CheatContextMismatch = "CHEAT_CONTEXT_MISMATCH";
        public const string CheatRequestIdMismatch = "CHEAT_REQUEST_ID_MISMATCH";
        public const string CheatRequestIdConflict = "CHEAT_REQUEST_ID_CONFLICT";
        public const string CheatReceiptCapacityExceeded = "CHEAT_RECEIPT_CAPACITY_EXCEEDED";

        public static string FromCheat(Mahjong.Domain.RoundCheatErrorCode bridge, Mahjong.Domain.Cheat.CheatErrorCode cheat)
        {
            switch (bridge)
            {
                case Mahjong.Domain.RoundCheatErrorCode.NotConfigured:
                    return CheatNotConfigured;
                case Mahjong.Domain.RoundCheatErrorCode.ContextMismatch:
                    return CheatContextMismatch;
                case Mahjong.Domain.RoundCheatErrorCode.RoundFinished:
                    return "ROUND_FINISHED";
                case Mahjong.Domain.RoundCheatErrorCode.RoundMismatch:
                    return RoundMismatch;
                case Mahjong.Domain.RoundCheatErrorCode.InvalidCommand:
                    return "INVALID_CHEAT_COMMAND";
                default:
                    break;
            }

            switch (cheat)
            {
                case Mahjong.Domain.Cheat.CheatErrorCode.Disabled:
                    return "CHEAT_DISABLED";
                case Mahjong.Domain.Cheat.CheatErrorCode.CostNotConfigured:
                    return "CHEAT_COST_NOT_CONFIGURED";
                case Mahjong.Domain.Cheat.CheatErrorCode.CostMustBeTen:
                    return "CHEAT_COST_MUST_BE_TEN";
                case Mahjong.Domain.Cheat.CheatErrorCode.InvalidSeat:
                    return "INVALID_SEAT";
                case Mahjong.Domain.Cheat.CheatErrorCode.InvalidRequest:
                    return "INVALID_REQUEST";
                case Mahjong.Domain.Cheat.CheatErrorCode.NotYourTurn:
                    return "NOT_YOUR_TURN";
                case Mahjong.Domain.Cheat.CheatErrorCode.WrongPhase:
                    return "WRONG_PHASE";
                case Mahjong.Domain.Cheat.CheatErrorCode.ResponseWindowOpen:
                    return "RESPONSE_WINDOW_OPEN";
                case Mahjong.Domain.Cheat.CheatErrorCode.RiichiLocked:
                    return "RIICHI_LOCKED";
                case Mahjong.Domain.Cheat.CheatErrorCode.NoDrawnTile:
                    return "NO_DRAWN_TILE";
                case Mahjong.Domain.Cheat.CheatErrorCode.DrawnTileNotInHand:
                    return "DRAWN_TILE_NOT_IN_HAND";
                case Mahjong.Domain.Cheat.CheatErrorCode.DrawKindMismatch:
                    return "DRAW_KIND_MISMATCH";
                case Mahjong.Domain.Cheat.CheatErrorCode.DealExtraDraw:
                    return "DEAL_EXTRA_DRAW";
                case Mahjong.Domain.Cheat.CheatErrorCode.OfferAlreadyPending:
                    return "CHEAT_OFFER_ALREADY_PENDING";
                case Mahjong.Domain.Cheat.CheatErrorCode.OtherOfferPending:
                    return "CHEAT_OTHER_OFFER_PENDING";
                case Mahjong.Domain.Cheat.CheatErrorCode.ReservedPending:
                    return "CHEAT_RESERVATION_PENDING";
                case Mahjong.Domain.Cheat.CheatErrorCode.AlreadyUsedThisTurn:
                    return "CHEAT_ALREADY_USED_THIS_TURN";
                case Mahjong.Domain.Cheat.CheatErrorCode.InsufficientFunds:
                    return "CHEAT_INSUFFICIENT_FUNDS";
                case Mahjong.Domain.Cheat.CheatErrorCode.LedgerConflict:
                    return "CHEAT_LEDGER_CONFLICT";
                case Mahjong.Domain.Cheat.CheatErrorCode.NoWindow:
                    return "CHEAT_NO_WINDOW";
                case Mahjong.Domain.Cheat.CheatErrorCode.WindowMismatch:
                    return "CHEAT_WINDOW_MISMATCH";
                case Mahjong.Domain.Cheat.CheatErrorCode.NoPendingOffer:
                    return "CHEAT_NO_PENDING_OFFER";
                case Mahjong.Domain.Cheat.CheatErrorCode.OfferMismatch:
                    return "CHEAT_OFFER_MISMATCH";
                case Mahjong.Domain.Cheat.CheatErrorCode.WallEmpty:
                    return "CHEAT_WALL_EMPTY";
                case Mahjong.Domain.Cheat.CheatErrorCode.NoWallCandidate:
                    return "CHEAT_NO_WALL_CANDIDATE";
                case Mahjong.Domain.Cheat.CheatErrorCode.CandidateUnavailable:
                    return "CHEAT_CANDIDATE_UNAVAILABLE";
                case Mahjong.Domain.Cheat.CheatErrorCode.DeadlineExpired:
                    return DeadlineExpired;
                case Mahjong.Domain.Cheat.CheatErrorCode.StaleOffer:
                    return "CHEAT_STALE_OFFER";
                case Mahjong.Domain.Cheat.CheatErrorCode.InvalidSelection:
                    return "CHEAT_INVALID_SELECTION";
                case Mahjong.Domain.Cheat.CheatErrorCode.TileNotOwned:
                    return "CHEAT_TILE_NOT_OWNED";
                case Mahjong.Domain.Cheat.CheatErrorCode.AlreadyMarked:
                    return "CHEAT_ALREADY_MARKED";
                case Mahjong.Domain.Cheat.CheatErrorCode.MarkCapacityExceeded:
                    return "CHEAT_MARK_CAPACITY_EXCEEDED";
                case Mahjong.Domain.Cheat.CheatErrorCode.MarkRoundMismatch:
                    return "CHEAT_MARK_ROUND_MISMATCH";
                case Mahjong.Domain.Cheat.CheatErrorCode.CandidateNotFound:
                    return "CHEAT_CANDIDATE_NOT_FOUND";
                case Mahjong.Domain.Cheat.CheatErrorCode.SlotMismatch:
                    return "CHEAT_SLOT_MISMATCH";
                case Mahjong.Domain.Cheat.CheatErrorCode.HandDrift:
                    return "CHEAT_HAND_DRIFT";
                case Mahjong.Domain.Cheat.CheatErrorCode.CandidateDrift:
                    return "CHEAT_CANDIDATE_DRIFT";
                case Mahjong.Domain.Cheat.CheatErrorCode.PhysicalMoveFailed:
                    return "CHEAT_PHYSICAL_MOVE_FAILED";
                case Mahjong.Domain.Cheat.CheatErrorCode.ReservationMissing:
                    return "CHEAT_RESERVATION_MISSING";
                case Mahjong.Domain.Cheat.CheatErrorCode.DeliveryNotPossible:
                    return "CHEAT_DELIVERY_NOT_POSSIBLE";
                case Mahjong.Domain.Cheat.CheatErrorCode.LedgerOverflow:
                    return "CHEAT_LEDGER_OVERFLOW";
                case Mahjong.Domain.Cheat.CheatErrorCode.GroupIdConflict:
                    return "CHEAT_GROUP_ID_CONFLICT";
                default:
                    return CheatRejected;
            }
        }

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
