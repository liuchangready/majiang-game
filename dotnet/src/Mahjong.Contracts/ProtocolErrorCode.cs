using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Contracts
{
    /// <summary>
    /// Stable protocol error, business reason and close codes. Codes are
    /// uppercase ASCII and only ever appended: an existing code never changes
    /// meaning and a removed code is never reused.
    ///
    /// Transport/protocol failures use ERROR or CLOSE. A rule denial is a
    /// RECEIPT with status REJECTED and a reason code; it is never converted
    /// into a transport error.
    /// </summary>
    public static class ProtocolErrorCode
    {
        // Protocol, transport and capacity errors.
        public const string ProtocolMajorUnsupported = "PROTOCOL_MAJOR_UNSUPPORTED";
        public const string ProtocolMinorUnsupported = "PROTOCOL_MINOR_UNSUPPORTED";
        public const string RulesetVersionUnsupported = "RULESET_VERSION_UNSUPPORTED";
        public const string ProjectionVersionUnsupported = "PROJECTION_VERSION_UNSUPPORTED";
        public const string LegacyV4Rejected = "LEGACY_V4_REJECTED";
        public const string MalformedMessage = "MALFORMED_MESSAGE";
        public const string UnknownMessageType = "UNKNOWN_MESSAGE_TYPE";
        public const string MessageTooLarge = "MESSAGE_TOO_LARGE";
        public const string AlreadyBound = "ALREADY_BOUND";
        public const string Unauthenticated = "UNAUTHENTICATED";
        public const string InvalidToken = "INVALID_TOKEN";
        public const string RequestIdConflict = "REQUEST_ID_CONFLICT";
        public const string RequestScopeExpired = "REQUEST_SCOPE_EXPIRED";
        public const string ServerBusy = "SERVER_BUSY";
        public const string ServerFull = "SERVER_FULL";
        public const string RoomNotFound = "ROOM_NOT_FOUND";
        public const string RoomFull = "ROOM_FULL";
        public const string InternalError = "INTERNAL_ERROR";
        public const string StaleStreamVersion = "STALE_STREAM_VERSION";
        public const string StreamGap = "STREAM_GAP";
        public const string LeaseLost = "LEASE_LOST";
        public const string ReceiptNotFound = "RECEIPT_NOT_FOUND";
        public const string ReceiptUnknown = "RECEIPT_UNKNOWN";
        public const string StateEventMissing = "STATE_EVENT_MISSING";
        public const string PrivateDataInPublicStream = "PRIVATE_DATA_IN_PUBLIC_STREAM";

        // Business rejection reason codes (RECEIPT status=REJECTED).
        public const string StaleControlEpoch = "STALE_CONTROL_EPOCH";
        public const string DecisionMismatch = "DECISION_MISMATCH";
        public const string WrongPhase = "WRONG_PHASE";
        public const string DeadlineExpired = "DEADLINE_EXPIRED";
        public const string NotYourTurn = "NOT_YOUR_TURN";
        public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
        public const string TileMarkInvalidSelection = "TILE_MARK_INVALID_SELECTION";
        public const string TileMarkTileNotOwned = "TILE_MARK_TILE_NOT_OWNED";
        public const string TileMarkAlreadyMarked = "TILE_MARK_ALREADY_MARKED";
        public const string TileMarkRiichiLocked = "TILE_MARK_RIICHI_LOCKED";
        public const string SleeveBloomNoWindow = "SLEEVE_BLOOM_NO_WINDOW";
        public const string SleeveBloomNoPendingOffer = "SLEEVE_BLOOM_NO_PENDING_OFFER";
        public const string SleeveBloomStaleOffer = "SLEEVE_BLOOM_STALE_OFFER";
        public const string SleeveBloomInvalidChoice = "SLEEVE_BLOOM_INVALID_CHOICE";
        public const string BeamSwapNoPendingOffer = "BEAM_SWAP_NO_PENDING_OFFER";
        public const string BeamSwapOfferMismatch = "BEAM_SWAP_OFFER_MISMATCH";
        public const string BeamSwapInvalidChoice = "BEAM_SWAP_INVALID_CHOICE";
        public const string BeamSwapStaleOffer = "BEAM_SWAP_STALE_OFFER";

        // Application close codes.
        public const string ProtocolViolation = "PROTOCOL_VIOLATION";
        public const string DisplacedByNewConnection = "DISPLACED_BY_NEW_CONNECTION";
        public const string RoomClosed = "ROOM_CLOSED";
        public const string ServerShutdown = "SERVER_SHUTDOWN";
        public const string LocalClosed = "LOCAL_CLOSED";
        public const string HandshakeTimeout = "HANDSHAKE_TIMEOUT";
        public const string HeartbeatTimeout = "HEARTBEAT_TIMEOUT";
        public const string ServerOverloaded = "SERVER_OVERLOADED";
        public const string SlowConsumer = "SLOW_CONSUMER";

        private static readonly string[] ProtocolCodes =
        {
            ProtocolMajorUnsupported, ProtocolMinorUnsupported, RulesetVersionUnsupported,
            ProjectionVersionUnsupported, LegacyV4Rejected, MalformedMessage, UnknownMessageType,
            MessageTooLarge, AlreadyBound, Unauthenticated, InvalidToken, RequestIdConflict,
            RequestScopeExpired, ServerBusy, ServerFull, RoomNotFound, RoomFull, InternalError,
            StaleStreamVersion, StreamGap, LeaseLost, ReceiptNotFound, ReceiptUnknown, StateEventMissing,
            PrivateDataInPublicStream
        };

        private static readonly string[] ReasonCodes =
        {
            StaleControlEpoch, DecisionMismatch, WrongPhase, DeadlineExpired, NotYourTurn,
            InsufficientFunds, TileMarkInvalidSelection, TileMarkTileNotOwned,
            TileMarkAlreadyMarked, TileMarkRiichiLocked, SleeveBloomNoWindow,
            SleeveBloomNoPendingOffer, SleeveBloomStaleOffer, SleeveBloomInvalidChoice,
            BeamSwapNoPendingOffer, BeamSwapOfferMismatch, BeamSwapInvalidChoice, BeamSwapStaleOffer
        };

        private static readonly string[] CloseCodes =
        {
            ProtocolMajorUnsupported, RulesetVersionUnsupported, ProtocolViolation,
            DisplacedByNewConnection, RoomClosed, ServerShutdown, LocalClosed,
            HandshakeTimeout, HeartbeatTimeout, ServerOverloaded, SlowConsumer,
            LeaseLost, MessageTooLarge
        };

        private static readonly HashSet<string> ProtocolCodeSet = new HashSet<string>(ProtocolCodes, StringComparer.Ordinal);
        private static readonly HashSet<string> ReasonCodeSet = new HashSet<string>(ReasonCodes, StringComparer.Ordinal);
        private static readonly HashSet<string> CloseCodeSet = new HashSet<string>(CloseCodes, StringComparer.Ordinal);

        private static readonly ReadOnlyCollection<string> ProtocolCodeList = Array.AsReadOnly(ProtocolCodes);
        private static readonly ReadOnlyCollection<string> ReasonCodeList = Array.AsReadOnly(ReasonCodes);
        private static readonly ReadOnlyCollection<string> CloseCodeList = Array.AsReadOnly(CloseCodes);

        private static readonly Dictionary<string, string> V4ToV5 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["INVALID_PROTOCOL_VERSION"] = ProtocolMajorUnsupported,
            ["MALFORMED_REQUEST"] = MalformedMessage,
            ["ALREADY_BOUND"] = AlreadyBound,
            ["ROOM_NOT_FOUND"] = RoomNotFound,
            ["ROOM_FULL"] = RoomFull,
            ["SERVER_FULL"] = ServerFull,
            ["MISSING_ROOM_ID"] = MalformedMessage,
            ["MISSING_ROOM_OR_TOKEN"] = MalformedMessage,
            ["MISSING_ACTION_CHOICE"] = MalformedMessage,
            ["MISSING_SHUFFLE_PLAN"] = MalformedMessage,
            ["UNAUTHENTICATED_SESSION"] = Unauthenticated,
            ["INVALID_TOKEN"] = InvalidToken,
            ["UNKNOWN_REQUEST_TYPE"] = UnknownMessageType,
            ["CONFLICT"] = RequestIdConflict,
            ["SERVER_BUSY"] = ServerBusy,
            ["INTERNAL_ERROR"] = InternalError,
            ["REQUEST_SCOPE_EXPIRED"] = RequestScopeExpired
        };

        public static IReadOnlyList<string> AllProtocolCodes => ProtocolCodeList;

        public static IReadOnlyList<string> AllReasonCodes => ReasonCodeList;

        public static IReadOnlyList<string> AllCloseCodes => CloseCodeList;

        public static bool IsKnownProtocolCode(string? code)
        {
            return code != null && ProtocolCodeSet.Contains(code);
        }

        public static bool IsKnownReasonCode(string? code)
        {
            return code != null && ReasonCodeSet.Contains(code);
        }

        public static bool IsKnownCloseCode(string? code)
        {
            return code != null && CloseCodeSet.Contains(code);
        }

        /// <summary>
        /// Validates the stable ASCII shape without requiring the code to be a
        /// current V5 code. Unknown well-formed codes are forwarded to the UI as
        /// generic messages and never interpreted as a receipt/domain fact.
        /// </summary>
        public static bool IsWellFormed(string? code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return false;
            }

            if (code!.Length > ProtocolConstants.MaxReasonCodeChars)
            {
                return false;
            }

            if (code[0] < 'A' || code[0] > 'Z')
            {
                return false;
            }

            for (int i = 1; i < code.Length; i++)
            {
                char c = code[i];
                bool valid = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool TryMapLegacyV4Code(string? v4Code, out string mapped)
        {
            if (v4Code != null && V4ToV5.TryGetValue(v4Code, out string? value))
            {
                mapped = value;
                return true;
            }

            mapped = string.Empty;
            return false;
        }

        public static string MapLegacyV4Code(string v4Code)
        {
            if (!TryMapLegacyV4Code(v4Code, out string mapped))
            {
                throw new ArgumentException("Unknown V4 error code: " + v4Code, nameof(v4Code));
            }

            return mapped;
        }

        public static bool IsRetryable(string code)
        {
            return string.Equals(code, ServerBusy, StringComparison.Ordinal) ||
                   string.Equals(code, ServerFull, StringComparison.Ordinal) ||
                   string.Equals(code, ReceiptUnknown, StringComparison.Ordinal);
        }

        public static bool IsPermanentClose(string code)
        {
            return string.Equals(code, ProtocolMajorUnsupported, StringComparison.Ordinal) ||
                   string.Equals(code, RulesetVersionUnsupported, StringComparison.Ordinal) ||
                   string.Equals(code, ProtocolViolation, StringComparison.Ordinal) ||
                   string.Equals(code, DisplacedByNewConnection, StringComparison.Ordinal) ||
                   string.Equals(code, RoomClosed, StringComparison.Ordinal) ||
                   string.Equals(code, ServerShutdown, StringComparison.Ordinal) ||
                   string.Equals(code, LocalClosed, StringComparison.Ordinal) ||
                   string.Equals(code, LeaseLost, StringComparison.Ordinal);
        }

        /// <summary>
        /// Frozen WSS close-code mapping for application close codes. The
        /// application code is the authoritative value; the numeric WSS code
        /// is only a transport hint and never replaces the receipt/error code.
        /// </summary>
        public static uint ToWebSocketCloseCode(string code)
        {
            switch (code)
            {
                case LocalClosed:
                    return 1000;
                case ProtocolViolation:
                case ProtocolMajorUnsupported:
                case ProtocolMinorUnsupported:
                case RulesetVersionUnsupported:
                case ProjectionVersionUnsupported:
                case MalformedMessage:
                case UnknownMessageType:
                    return 4400;
                case Unauthenticated:
                case InvalidToken:
                case HandshakeTimeout:
                    return 4401;
                case DisplacedByNewConnection:
                case RoomClosed:
                    return 4403;
                case RequestIdConflict:
                    return 4409;
                case MessageTooLarge:
                    return 1009;
                case ServerBusy:
                case ServerFull:
                case ServerOverloaded:
                case SlowConsumer:
                case HeartbeatTimeout:
                    return 4429;
                case ServerShutdown:
                case LeaseLost:
                    return 4503;
                default:
                    return 1011;
            }
        }
    }
}
