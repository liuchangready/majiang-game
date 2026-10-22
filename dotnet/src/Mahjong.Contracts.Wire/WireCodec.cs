using System;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Mahjong.Contracts;

namespace Mahjong.Contracts.Wire
{
    public static class WireLimits
    {
        public const int MaxInboundMessageBytes = 1024 * 1024;
        public const int MaxCommandPayloadBytes = 64 * 1024;
        public const int MaxSnapshotBytes = 1024 * 1024;
        public const int MaxHeartbeatNonceChars = 16;
    }

    public class WireProtocolException : Exception
    {
        public WireProtocolException(string message, string code) : base(message)
        {
            Code = code;
        }

        public WireProtocolException(string message, string code, Exception inner) : base(message, inner)
        {
            Code = code;
        }

        public string Code { get; }
    }

    public sealed class WireMessageTooLargeException : WireProtocolException
    {
        public WireMessageTooLargeException(string message) : base(message, ProtocolErrorCode.MessageTooLarge)
        {
        }
    }

    public sealed class WireMalformedException : WireProtocolException
    {
        public WireMalformedException(string message) : base(message, ProtocolErrorCode.MalformedMessage)
        {
        }

        public WireMalformedException(string message, Exception inner) : base(message, ProtocolErrorCode.MalformedMessage, inner)
        {
        }
    }

    public sealed class WireLegacyV4Exception : WireProtocolException
    {
        public WireLegacyV4Exception(string message) : base(message, ProtocolErrorCode.LegacyV4Rejected)
        {
        }
    }

    public enum WireDecodeStatus
    {
        Unspecified = 0,
        Ok = 1,
        UnknownMessageType = 2,
        MissingBody = 3,
        MalformedBody = 4,
        VersionRejected = 5,
        LegacyV4Rejected = 6
    }

    public sealed class WireDecodeResult
    {
        public WireDecodeResult(Envelope? envelope, WireDecodeStatus status, string errorCode, int unknownFieldCount)
        {
            Envelope = envelope;
            Status = status;
            ErrorCode = errorCode ?? string.Empty;
            UnknownFieldCount = unknownFieldCount;
        }

        public Envelope? Envelope { get; }

        public WireDecodeStatus Status { get; }

        public string ErrorCode { get; }

        public int UnknownFieldCount { get; }

        public bool IsOk => Status == WireDecodeStatus.Ok;
    }

    public enum WireEventDecodeStatus
    {
        Unspecified = 0,
        Known = 1,
        Unknown = 2,
        Malformed = 3
    }

    public sealed class WireEventDecodeResult
    {
        public WireEventDecodeResult(
            WireEventDecodeStatus status,
            ProtocolEventRecord? record,
            string errorCode,
            int unknownFieldCount)
        {
            Status = status;
            Record = record;
            ErrorCode = errorCode ?? string.Empty;
            UnknownFieldCount = unknownFieldCount;
        }

        public WireEventDecodeStatus Status { get; }

        public ProtocolEventRecord? Record { get; }

        public string ErrorCode { get; }

        public int UnknownFieldCount { get; }

        public bool IsKnown => Status == WireEventDecodeStatus.Known;

        public bool RequiresFullBaseline => Status == WireEventDecodeStatus.Unknown || Status == WireEventDecodeStatus.Malformed;
    }

    /// <summary>
    /// Protobuf wire boundary. It enforces the frozen envelope/body pairing,
    /// size caps, V4 isolation and safe unknown-field/event handling.
    /// </summary>
    public static class WireCodec
    {
        private static readonly HashSet<uint> PrivateEventTypes = new HashSet<uint>
        {
            (uint)EventType.TileDealt,
            (uint)EventType.TileDrawn,
            (uint)EventType.RinshanDraw,
            (uint)EventType.CheatPointsChanged,
            (uint)EventType.SleeveBloomResolved,
            (uint)EventType.BeamSwapResolved
        };

        public static byte[] Encode(Envelope envelope)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (envelope.ProtocolMajor != ProtocolConstants.Major)
            {
                throw new WireProtocolException(
                    "Only protocol major " + ProtocolConstants.Major + " can be encoded by the V5 codec.",
                    ProtocolErrorCode.ProtocolMajorUnsupported);
            }

            if (envelope.ProtocolMinor > ProtocolConstants.Minor)
            {
                throw new WireProtocolException(
                    "This build cannot emit minor " + envelope.ProtocolMinor + " fields.",
                    ProtocolErrorCode.ProtocolMinorUnsupported);
            }

            if (envelope.RulesetVersion != ProtocolConstants.RulesetVersion ||
                envelope.ProjectionVersion != ProtocolConstants.ProjectionVersion)
            {
                throw new WireProtocolException(
                    "Ruleset/projection versions do not match the frozen V5 version.",
                    ProtocolErrorCode.RulesetVersionUnsupported);
            }

            if (!IsKnownMessageType(envelope.MessageType))
            {
                throw new WireProtocolException("Unknown message type.", ProtocolErrorCode.UnknownMessageType);
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.None)
            {
                throw new WireMalformedException("Envelope has no body.");
            }

            if (ExpectedBodyCase(envelope.MessageType) != envelope.BodyCase)
            {
                throw new WireMalformedException(
                    "Envelope message_type " + envelope.MessageType + " does not match body " + envelope.BodyCase + ".");
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.Command)
            {
                ValidateCommand(envelope.Command);
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.Heartbeat)
            {
                ValidateHeartbeat(envelope.Heartbeat);
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.PublicSnapshot)
            {
                EnsurePublicSnapshot(envelope.PublicSnapshot);
                EnsureSnapshotSize(envelope.PublicSnapshot, "public snapshot");
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.PublicUpdate)
            {
                EnsurePublicUpdate(envelope.PublicUpdate);
                EnsureSnapshotSize(envelope.PublicUpdate.Snapshot, "public update snapshot");
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.PlayerSnapshot)
            {
                PlayerSnapshot? playerSnapshot = envelope.PlayerSnapshot;
                if (playerSnapshot == null)
                {
                    throw new WireMalformedException("Player snapshot body is missing.");
                }

                EnsurePlayerPublicView(playerSnapshot.PublicView);
                EnsureSnapshotSize(playerSnapshot, "player snapshot");
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.PlayerUpdate)
            {
                PlayerUpdate? playerUpdate = envelope.PlayerUpdate;
                if (playerUpdate == null || playerUpdate.Snapshot == null)
                {
                    throw new WireMalformedException("Player update snapshot is missing.");
                }

                EnsurePlayerPublicView(playerUpdate.Snapshot.PublicView);
                EnsureSnapshotSize(playerUpdate.Snapshot, "player update snapshot");
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.SyncResponse)
            {
                ValidateSyncResponse(envelope.SyncResponse);

                if (envelope.SyncResponse.BaselineCase == SyncResponse.BaselineOneofCase.Public)
                {
                    EnsurePublicSnapshot(envelope.SyncResponse.Public);
                    EnsureSnapshotSize(envelope.SyncResponse.Public, "sync public snapshot");
                }

                if (envelope.SyncResponse.BaselineCase == SyncResponse.BaselineOneofCase.Player)
                {
                    PlayerSnapshot? syncPlayer = envelope.SyncResponse.Player;
                    if (syncPlayer == null)
                    {
                        throw new WireMalformedException("Sync player snapshot body is missing.");
                    }

                    EnsurePlayerPublicView(syncPlayer.PublicView);
                    EnsureSnapshotSize(syncPlayer, "sync player snapshot");
                }
            }

            // Public wire exits must not re-emit any unknown field anywhere in
            // the envelope tree. Google.Protobuf preserves unknown fields on
            // parse and ToByteArray; rejecting here prevents them from being
            // used as an arbitrary byte channel (private ledger/seed bytes).
            if (IsPublicEncodeExit(envelope))
            {
                EnsureNoUnknownFields(envelope, "public envelope");
            }

            byte[] bytes = envelope.ToByteArray();
            if (bytes.Length > WireLimits.MaxInboundMessageBytes)
            {
                throw new WireMessageTooLargeException(
                    "Envelope is " + bytes.Length + " bytes; limit is " + WireLimits.MaxInboundMessageBytes + ".");
            }

            return bytes;
        }

        public static WireDecodeResult Decode(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return Decode(new ReadOnlySpan<byte>(bytes));
        }

        public static WireDecodeResult Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                throw new WireMalformedException("Empty wire message.");
            }

            if (bytes.Length > WireLimits.MaxInboundMessageBytes)
            {
                throw new WireMessageTooLargeException(
                    "Wire message is " + bytes.Length + " bytes; limit is " + WireLimits.MaxInboundMessageBytes + ".");
            }

            if (ProtocolCompatibility.HasLegacyV4Magic(bytes))
            {
                return new WireDecodeResult(null, WireDecodeStatus.LegacyV4Rejected, ProtocolErrorCode.LegacyV4Rejected, 0);
            }

            Envelope envelope;
            try
            {
                envelope = Envelope.Parser.ParseFrom(bytes.ToArray());
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new WireMalformedException("Protobuf envelope is malformed.", ex);
            }

            int unknownFieldCount = CountTopLevelUnknownFields(bytes, Envelope.Descriptor);

            if (envelope.ProtocolMajor == 0)
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.MalformedBody, ProtocolErrorCode.MalformedMessage, unknownFieldCount);
            }

            // V4 business/wire version 8 is not a V5 major. Refuse without
            // sniffing a JSON or MJTC fallback.
            if (envelope.ProtocolMajor == ProtocolConstants.LegacyV4BusinessVersion)
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.LegacyV4Rejected, ProtocolErrorCode.LegacyV4Rejected, unknownFieldCount);
            }

            if (envelope.RulesetVersion == 0)
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.VersionRejected, ProtocolErrorCode.RulesetVersionUnsupported, unknownFieldCount);
            }

            if (envelope.ProjectionVersion == 0)
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.VersionRejected, ProtocolErrorCode.ProjectionVersionUnsupported, unknownFieldCount);
            }

            var handshake = new VersionHandshake(
                new ProtocolVersion(envelope.ProtocolMajor, envelope.ProtocolMinor),
                envelope.RulesetVersion,
                envelope.ProjectionVersion,
                requiresMinorFeatures: false,
                claimsLegacyV4Wire: false);
            CompatibilityDecision decision = ProtocolCompatibility.EvaluateCurrent(handshake);
            if (!decision.Allowed)
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.VersionRejected, decision.Code, unknownFieldCount);
            }

            if (!IsKnownMessageType(envelope.MessageType))
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.UnknownMessageType, ProtocolErrorCode.UnknownMessageType, unknownFieldCount);
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.None)
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.MissingBody, ProtocolErrorCode.MalformedMessage, unknownFieldCount);
            }

            if (ExpectedBodyCase(envelope.MessageType) != envelope.BodyCase)
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.MalformedBody, ProtocolErrorCode.MalformedMessage, unknownFieldCount);
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.Command)
            {
                try
                {
                    ValidateCommand(envelope.Command);
                }
                catch (WireProtocolException ex)
                {
                    return new WireDecodeResult(envelope, WireDecodeStatus.MalformedBody, ex.Code, unknownFieldCount);
                }
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.Heartbeat)
            {
                try
                {
                    ValidateHeartbeat(envelope.Heartbeat);
                }
                catch (WireProtocolException ex)
                {
                    return new WireDecodeResult(envelope, WireDecodeStatus.MalformedBody, ex.Code, unknownFieldCount);
                }
            }

            try
            {
                switch (envelope.BodyCase)
                {
                    case Envelope.BodyOneofCase.PlayerSnapshot:
                        EnsureSnapshotSize(envelope.PlayerSnapshot, "player snapshot");
                        break;
                    case Envelope.BodyOneofCase.PublicSnapshot:
                        EnsurePublicSnapshotCore(envelope.PublicSnapshot, rejectUnknownFields: false);
                        EnsureSnapshotSize(envelope.PublicSnapshot, "public snapshot");
                        break;
                    case Envelope.BodyOneofCase.PlayerUpdate:
                        EnsureSnapshotSize(envelope.PlayerUpdate.Snapshot, "player update snapshot");
                        break;
                    case Envelope.BodyOneofCase.PublicUpdate:
                        EnsurePublicUpdateCore(envelope.PublicUpdate, rejectUnknownFields: false);
                        EnsureSnapshotSize(envelope.PublicUpdate.Snapshot, "public update snapshot");
                        break;
                    case Envelope.BodyOneofCase.SyncResponse:
                        ValidateSyncResponse(envelope.SyncResponse);
                        if (envelope.SyncResponse.BaselineCase == SyncResponse.BaselineOneofCase.Public)
                        {
                            EnsurePublicSnapshotCore(envelope.SyncResponse.Public, rejectUnknownFields: false);
                            EnsureSnapshotSize(envelope.SyncResponse.Public, "sync public snapshot");
                        }

                        if (envelope.SyncResponse.BaselineCase == SyncResponse.BaselineOneofCase.Player)
                        {
                            EnsureSnapshotSize(envelope.SyncResponse.Player, "sync player snapshot");
                        }

                        break;
                }
            }
            catch (PublicProjectionException ex)
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.MalformedBody, ex.Code, unknownFieldCount);
            }
            catch (WireProtocolException ex)
            {
                return new WireDecodeResult(envelope, WireDecodeStatus.MalformedBody, ex.Code, unknownFieldCount);
            }

            return new WireDecodeResult(envelope, WireDecodeStatus.Ok, string.Empty, unknownFieldCount);
        }

        public static void ValidateCommand(Command? command)
        {
            if (command == null)
            {
                throw new WireMalformedException("Command body is missing.");
            }

            if (!IsKnownCommandType(command.Type))
            {
                throw new WireProtocolException("Unknown command type.", ProtocolErrorCode.UnknownMessageType);
            }

            if (command.CanonicalPayload.Length == 0)
            {
                throw new WireMalformedException("Command canonical_payload must not be empty.");
            }

            if (command.CanonicalPayload.Length > WireLimits.MaxCommandPayloadBytes)
            {
                throw new WireMessageTooLargeException(
                    "Command canonical_payload is " + command.CanonicalPayload.Length + " bytes; limit is " + WireLimits.MaxCommandPayloadBytes + ".");
            }

            if (command.PayloadSchemaVersion != CanonicalPayloadDigest.FormatVersion)
            {
                throw new WireMalformedException("Unsupported canonical payload schema version.");
            }

            BusinessPayload payload = BusinessPayload.Retain(command.CanonicalPayload.Span);
            try
            {
                CommandPayloadValidator.Validate((CommandKind)(ushort)command.Type, payload);
            }
            catch (CommandValidationException ex)
            {
                throw new WireMalformedException("Command payload is not a valid canonical command: " + ex.Message, ex);
            }
        }

        public static void ValidateHeartbeat(Heartbeat? heartbeat)
        {
            if (heartbeat == null)
            {
                throw new WireMalformedException("Heartbeat body is missing.");
            }

            string? nonce = heartbeat.BeatCase switch
            {
                Heartbeat.BeatOneofCase.Ping => heartbeat.Ping.Nonce,
                Heartbeat.BeatOneofCase.Pong => heartbeat.Pong.Nonce,
                _ => null
            };

            if (nonce == null)
            {
                throw new WireMalformedException("Heartbeat must carry exactly one of PING/PONG.");
            }

            ValidateHeartbeatNonce(nonce);
        }

        public static void ValidateHeartbeatNonce(string? nonce)
        {
            if (nonce == null || nonce.Length != WireLimits.MaxHeartbeatNonceChars)
            {
                throw new WireMalformedException("Heartbeat nonce must be exactly 16 lowercase hex characters.");
            }

            for (int i = 0; i < nonce.Length; i++)
            {
                char c = nonce[i];
                bool valid = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!valid)
                {
                    throw new WireMalformedException("Heartbeat nonce must be exactly 16 lowercase hex characters.");
                }
            }
        }

        /// <summary>
        /// Computes the idempotency digest from the exact original canonical
        /// business bytes. It never hashes `Command.ToByteArray()`.
        /// </summary>
        public static PayloadDigest ComputeCanonicalDigest(Command command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (command.CanonicalPayload.Length == 0)
            {
                throw new WireMalformedException("Command canonical_payload must not be empty.");
            }

            if (command.CanonicalPayload.Length > WireLimits.MaxCommandPayloadBytes)
            {
                throw new WireMessageTooLargeException(
                    "Command canonical_payload exceeds " + WireLimits.MaxCommandPayloadBytes + " bytes.");
            }

            return CanonicalPayloadDigest.Compute(command.CanonicalPayload.Span);
        }

        public static PayloadDigest ComputeCanonicalDigest(ProtocolCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (command.Payload.Length > WireLimits.MaxCommandPayloadBytes)
            {
                throw new WireMessageTooLargeException(
                    "Command canonical_payload exceeds " + WireLimits.MaxCommandPayloadBytes + " bytes.");
            }

            return CanonicalPayloadDigest.Compute(command.Payload);
        }

        public static WireEventDecodeResult DecodeEvent(EventEnvelope? wireEvent)
        {
            if (wireEvent == null)
            {
                throw new ArgumentNullException(nameof(wireEvent));
            }

            byte[] wireBytes = wireEvent.ToByteArray();
            int unknownFields = CountTopLevelUnknownFields(wireBytes, EventEnvelope.Descriptor);
            if (wireEvent.EventCase != EventEnvelope.EventOneofCase.None)
            {
                FieldDescriptor? bodyField = EventEnvelope.Descriptor.FindFieldByNumber((int)wireEvent.EventCase);
                if (bodyField?.MessageType != null)
                {
                    unknownFields += CountTopLevelUnknownFields(GetEventBodyBytes(wireEvent), bodyField.MessageType);
                }
            }

            if (wireEvent.EventSchemaVersion != ProtocolConstants.EventSchemaVersion)
            {
                return UnknownResult(wireEvent, unknownFields, "Unsupported event schema version; raw event preserved.");
            }

            if (!ProtocolEventPolicy.IsKnownEventType((uint)wireEvent.EventType))
            {
                if (wireEvent.EventCase != EventEnvelope.EventOneofCase.None)
                {
                    return Malformed("Unknown event type carries a known body; refusing to interpret it.");
                }

                if (!wireEvent.HasUnknownPayload)
                {
                    return Malformed("Unknown event type is missing its raw payload.");
                }

                return UnknownResult(wireEvent, unknownFields, "Unknown event type; raw payload preserved.");
            }

            EventEnvelope.EventOneofCase expected = ExpectedEventCase(wireEvent.EventType);
            if (wireEvent.EventCase == EventEnvelope.EventOneofCase.None)
            {
                return Malformed("Known event type is missing its oneof body.");
            }

            if (wireEvent.EventCase != expected)
            {
                return Malformed("Event type and oneof body do not match.");
            }

            var record = ProtocolEventRecord.Known(
                (ProtocolEventType)(uint)wireEvent.EventType,
                (ProtocolEventVisibility)(uint)wireEvent.Visibility,
                wireEvent.EventSchemaVersion,
                new EventSeq(wireEvent.EventSeq),
                new StateVersion(wireEvent.CommandVersion),
                wireEvent.EventOrdinal,
                GetEventBodyBytes(wireEvent),
                unknownFields > 0);

            return new WireEventDecodeResult(WireEventDecodeStatus.Known, record, string.Empty, unknownFields);
        }

        public static WireEventDecodeResult DecodePublicEvent(PublicEventEnvelope? wireEvent)
        {
            if (wireEvent == null)
            {
                throw new ArgumentNullException(nameof(wireEvent));
            }

            // Public visibility is a precondition, not a postcondition: a
            // private/unspecified event must never be returned as Unknown with
            // a preserved raw payload, even when its schema/type is unknown.
            if (wireEvent.Visibility != EventVisibility.Public &&
                wireEvent.Visibility != EventVisibility.SettlementPublic)
            {
                return PublicRejected(ProtocolErrorCode.PrivateDataInPublicStream, 0);
            }

            int unknownFields;
            try
            {
                string? firstPath = null;
                unknownFields = ScanUnknownFields(
                    wireEvent.ToByteArray(),
                    PublicEventEnvelope.Descriptor,
                    "public event",
                    0,
                    ref firstPath);
            }
            catch (InvalidProtocolBufferException)
            {
                return Malformed("Public event is malformed while checking unknown fields.");
            }

            if (wireEvent.EventSchemaVersion != ProtocolConstants.EventSchemaVersion)
            {
                return PublicUnknownResult(wireEvent, unknownFields);
            }

            if (!ProtocolEventPolicy.IsKnownEventType((uint)wireEvent.EventType))
            {
                if (wireEvent.EventCase != PublicEventEnvelope.EventOneofCase.None || !wireEvent.HasUnknownPayload)
                {
                    return Malformed("Unknown public event is malformed; refusing to interpret it.");
                }

                return PublicUnknownResult(wireEvent, unknownFields);
            }

            try
            {
                // Symmetric with the encode-side semantic guard: this covers
                // event/body matching, explicit unknown_payload, private event
                // types, and ura authorization.
                EnsurePublicEventCore(wireEvent, rejectUnknownFields: false);
            }
            catch (PublicProjectionException ex)
            {
                return PublicRejected(ex.Code, unknownFields);
            }

            if (unknownFields > 0)
            {
                // A known event may have been extended by a future minor. Keep
                // its raw bytes but do not treat it as a verified known fact.
                return PublicUnknownResultFromRaw(wireEvent, unknownFields);
            }

            var record = ProtocolEventRecord.Known(
                (ProtocolEventType)(uint)wireEvent.EventType,
                (ProtocolEventVisibility)(uint)wireEvent.Visibility,
                wireEvent.EventSchemaVersion,
                new EventSeq(wireEvent.EventSeq),
                new StateVersion(wireEvent.CommandVersion),
                wireEvent.EventOrdinal,
                GetPublicEventBodyBytes(wireEvent),
                unknownFields > 0);

            return new WireEventDecodeResult(WireEventDecodeStatus.Known, record, string.Empty, unknownFields);
        }

        public static void EnsurePublicEvent(PublicEventEnvelope? wireEvent)
        {
            EnsurePublicEventCore(wireEvent, rejectUnknownFields: true);
        }

        private static void EnsurePublicEventCore(PublicEventEnvelope? wireEvent, bool rejectUnknownFields)
        {
            if (wireEvent == null)
            {
                throw new PublicProjectionException("Public stream event is missing.", ProtocolErrorCode.MalformedMessage);
            }

            if (!ProtocolEventPolicy.IsKnownEventType((uint)wireEvent.EventType) ||
                wireEvent.EventSchemaVersion != ProtocolConstants.EventSchemaVersion)
            {
                throw new PublicProjectionException(
                    "Unknown event type/schema cannot be applied to a public stream.",
                    ProtocolErrorCode.StreamGap);
            }

            if (PrivateEventTypes.Contains((uint)wireEvent.EventType) ||
                wireEvent.EventCase == PublicEventEnvelope.EventOneofCase.None ||
                ExpectedPublicEventCase(wireEvent.EventType) != wireEvent.EventCase)
            {
                throw new PublicProjectionException(
                    "Event type and public body do not match.",
                    ProtocolErrorCode.MalformedMessage);
            }

            if (wireEvent.HasUnknownPayload)
            {
                throw new PublicProjectionException(
                    "Known public events must not carry unknown_payload; it is reserved for unknown events that require a full baseline.",
                    ProtocolErrorCode.MalformedMessage);
            }

            if (wireEvent.Visibility != EventVisibility.Public &&
                wireEvent.Visibility != EventVisibility.SettlementPublic)
            {
                throw new PublicProjectionException(
                    "Event visibility must be explicitly public or settlement-public.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }

            if (wireEvent.EventCase == PublicEventEnvelope.EventOneofCase.DoraRevealed &&
                wireEvent.DoraRevealed.IsUraDora &&
                (!wireEvent.DoraRevealed.UraAuthorized || wireEvent.Visibility != EventVisibility.SettlementPublic))
            {
                throw new PublicProjectionException(
                    "Ura dora may enter a public stream only in an explicitly authorized settlement batch.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }

            if (wireEvent.EventCase == PublicEventEnvelope.EventOneofCase.TsumoWin)
            {
                EnsurePublicBreakdown(wireEvent.TsumoWin.Breakdown, wireEvent.Visibility);
            }

            if (wireEvent.EventCase == PublicEventEnvelope.EventOneofCase.RonWin)
            {
                EnsurePublicBreakdown(wireEvent.RonWin.Breakdown, wireEvent.Visibility);
            }

            if (rejectUnknownFields)
            {
                EnsureNoUnknownFields(wireEvent, "public event");
            }
        }

        private static void EnsurePublicBreakdown(ScoreBreakdownView? breakdown, EventVisibility visibility)
        {
            if (breakdown == null)
            {
                return;
            }

            if ((breakdown.UraTiles.Count > 0 || breakdown.UraHits.Count > 0) &&
                (!breakdown.UraAuthorized || visibility != EventVisibility.SettlementPublic))
            {
                throw new PublicProjectionException(
                    "Ura dora entities may enter a public stream only when explicitly authorized in a settlement batch.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }
        }

        public static void EnsurePublicSnapshot(PublicSnapshot? snapshot)
        {
            EnsurePublicSnapshotCore(snapshot, rejectUnknownFields: true);
        }

        private static void EnsurePublicSnapshotCore(PublicSnapshot? snapshot, bool rejectUnknownFields)
        {
            if (snapshot == null)
            {
                throw new PublicProjectionException("Public snapshot body is missing.", ProtocolErrorCode.MalformedMessage);
            }

            if (rejectUnknownFields)
            {
                EnsureNoUnknownFields(snapshot, "public snapshot");
            }

            EnsurePublicMatchViewCore(snapshot.View, rejectUnknownFields);
        }

        public static void EnsurePublicMatchView(PublicMatchView? view)
        {
            EnsurePublicMatchViewCore(view, rejectUnknownFields: true);
        }

        private static void EnsurePublicMatchViewCore(PublicMatchView? view, bool rejectUnknownFields)
        {
            if (view == null)
            {
                throw new PublicProjectionException("Public snapshot view is missing.", ProtocolErrorCode.MalformedMessage);
            }

            if (rejectUnknownFields)
            {
                EnsureNoUnknownFields(view, "public match view");
            }

            for (int i = 0; i < view.SettlementEvents.Count; i++)
            {
                EnsurePublicEventCore(view.SettlementEvents[i], rejectUnknownFields);
            }
        }

        public static void EnsurePublicUpdate(PublicUpdate? update)
        {
            EnsurePublicUpdateCore(update, rejectUnknownFields: true);
        }

        private static void EnsurePublicUpdateCore(PublicUpdate? update, bool rejectUnknownFields)
        {
            if (update == null)
            {
                throw new PublicProjectionException("Public update body is missing.", ProtocolErrorCode.MalformedMessage);
            }

            if (rejectUnknownFields)
            {
                EnsureNoUnknownFields(update, "public update");
            }

            for (int i = 0; i < update.VisibleEvents.Count; i++)
            {
                EnsurePublicEventCore(update.VisibleEvents[i], rejectUnknownFields);
            }

            EnsurePublicSnapshotCore(update.Snapshot, rejectUnknownFields);
        }

        private static void EnsurePlayerPublicView(PublicMatchView? view)
        {
            if (view == null)
            {
                return;
            }

            EnsurePublicMatchView(view);
        }

        private static bool IsPublicEncodeExit(Envelope envelope)
        {
            switch (envelope.BodyCase)
            {
                case Envelope.BodyOneofCase.PublicSnapshot:
                case Envelope.BodyOneofCase.PublicUpdate:
                    return true;
                case Envelope.BodyOneofCase.SyncResponse:
                    return envelope.SyncResponse.Target == SyncTarget.PublicStream;
                default:
                    return false;
            }
        }

        private const int MaxPublicTreeDepth = 64;

        /// <summary>
        /// Rejects any unknown field anywhere in a public message tree. Protobuf
        /// preserves unknown fields and ToByteArray re-emits them, so this scan
        /// is required before every public encode exit. Inbound Decode remains
        /// permissive for future minor optional fields; it never re-encodes them
        /// into a public stream.
        /// </summary>
        private static void EnsureNoUnknownFields(IMessage message, string context)
        {
            string? firstPath = null;
            int count;
            try
            {
                count = ScanUnknownFields(message.ToByteArray(), message.Descriptor, context, 0, ref firstPath);
            }
            catch (InvalidProtocolBufferException)
            {
                throw new PublicProjectionException(
                    context + " is malformed while checking unknown fields.",
                    ProtocolErrorCode.MalformedMessage);
            }

            if (count > 0)
            {
                throw new PublicProjectionException(
                    context + " contains " + count + " unknown field(s), first at " + firstPath +
                    "; refusing to publish unverified public bytes.",
                    ProtocolErrorCode.MalformedMessage);
            }
        }

        private static int ScanUnknownFields(
            ReadOnlySpan<byte> bytes,
            MessageDescriptor descriptor,
            string path,
            int depth,
            ref string? firstPath)
        {
            if (depth > MaxPublicTreeDepth)
            {
                firstPath ??= path + " (depth limit)";
                return 1;
            }

            int count = 0;
            try
            {
                using (var input = new CodedInputStream(bytes.ToArray()))
                {
                    while (!input.IsAtEnd)
                    {
                        uint tag = input.ReadTag();
                        if (tag == 0)
                        {
                            break;
                        }

                        int fieldNumber = (int)(tag >> 3);
                        int wireType = (int)(tag & 7);
                        FieldDescriptor? field = descriptor.FindFieldByNumber(fieldNumber);

                        if (field == null)
                        {
                            count++;
                            firstPath ??= path + "." + fieldNumber;
                            input.SkipLastField();
                            continue;
                        }

                        if (field.FieldType == FieldType.Message)
                        {
                            if (wireType != 2)
                            {
                                count++;
                                firstPath ??= path + "." + field.Name + " (wire type)";
                                input.SkipLastField();
                                continue;
                            }

                            ByteString child = input.ReadBytes();
                            count += ScanUnknownFields(
                                child.Span,
                                field.MessageType!,
                                path + "." + field.Name,
                                depth + 1,
                                ref firstPath);
                            continue;
                        }

                        // Packed repeated scalar values are length-delimited on
                        // the wire even though their declared type is numeric.
                        if (field.IsRepeated && IsPackable(field) && wireType == 2)
                        {
                            input.SkipLastField();
                            continue;
                        }

                        if (wireType != ExpectedWireType(field))
                        {
                            count++;
                            firstPath ??= path + "." + field.Name + " (wire type)";
                            input.SkipLastField();
                            continue;
                        }

                        input.SkipLastField();
                    }
                }
            }
            catch (InvalidProtocolBufferException)
            {
                throw;
            }

            return count;
        }

        private static bool IsPackable(FieldDescriptor field)
        {
            switch (field.FieldType)
            {
                case FieldType.Double:
                case FieldType.Float:
                case FieldType.Int64:
                case FieldType.UInt64:
                case FieldType.Int32:
                case FieldType.Fixed64:
                case FieldType.Fixed32:
                case FieldType.Bool:
                case FieldType.UInt32:
                case FieldType.SFixed32:
                case FieldType.SFixed64:
                case FieldType.SInt32:
                case FieldType.SInt64:
                case FieldType.Enum:
                    return true;
                default:
                    return false;
            }
        }

        private static int ExpectedWireType(FieldDescriptor field)
        {
            switch (field.FieldType)
            {
                case FieldType.Double:
                case FieldType.Fixed64:
                case FieldType.SFixed64:
                    return 1;
                case FieldType.String:
                case FieldType.Bytes:
                case FieldType.Message:
                    return 2;
                case FieldType.Float:
                case FieldType.Fixed32:
                case FieldType.SFixed32:
                    return 5;
                case FieldType.Group:
                    return 3;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// SYNC_RESPONSE always carries one full baseline, and the target must
        /// match the baseline oneof exactly. This prevents a public-target
        /// response from smuggling a player snapshot (including private hand
        /// and ledger data) or vice versa.
        /// </summary>
        public static void ValidateSyncResponse(SyncResponse? response)
        {
            if (response == null)
            {
                throw new WireMalformedException("SYNC_RESPONSE body is missing.");
            }

            if (!response.IsFullSnapshot)
            {
                throw new WireMalformedException("SYNC_RESPONSE must be a full baseline (is_full_snapshot=true).");
            }

            SyncResponse.BaselineOneofCase expected;
            switch (response.Target)
            {
                case SyncTarget.PlayerStream:
                    expected = SyncResponse.BaselineOneofCase.Player;
                    break;
                case SyncTarget.PublicStream:
                    expected = SyncResponse.BaselineOneofCase.Public;
                    break;
                case SyncTarget.Room:
                    expected = SyncResponse.BaselineOneofCase.Room;
                    break;
                default:
                    throw new WireMalformedException("SYNC_RESPONSE target is unspecified or unknown.");
            }

            if (response.BaselineCase != expected)
            {
                throw new WireMalformedException(
                    "SYNC_RESPONSE target " + response.Target + " does not match baseline " + response.BaselineCase + ".");
            }
        }

        private static void EnsureSnapshotSize(IMessage message, string name)
        {
            if (message == null)
            {
                throw new WireMalformedException(name + " body is missing.");
            }

            int size = message.CalculateSize();
            if (size > WireLimits.MaxSnapshotBytes)
            {
                throw new WireMessageTooLargeException(
                    name + " is " + size + " bytes; limit is " + WireLimits.MaxSnapshotBytes + ".");
            }
        }

        public static bool IsKnownMessageType(MessageType type)
        {
            return (uint)type >= (uint)MessageType.Hello && (uint)type <= (uint)MessageType.PublicSnapshotQuery;
        }

        public static bool IsKnownCommandType(CommandType type)
        {
            return (uint)type >= (uint)CommandType.CreateRoom && (uint)type <= (uint)CommandType.ResumeCursor;
        }

        public static int CountTopLevelUnknownFields(ReadOnlySpan<byte> bytes, MessageDescriptor descriptor)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            int count = 0;
            try
            {
                using (var input = new CodedInputStream(bytes.ToArray()))
                {
                    while (!input.IsAtEnd)
                    {
                        uint tag = input.ReadTag();
                        if (tag == 0)
                        {
                            break;
                        }

                        int fieldNumber = (int)(tag >> 3);
                        if (descriptor.FindFieldByNumber(fieldNumber) == null)
                        {
                            count++;
                        }

                        input.SkipLastField();
                    }
                }
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new WireMalformedException("Cannot scan unknown fields.", ex);
            }

            return count;
        }

        private static Envelope.BodyOneofCase ExpectedBodyCase(MessageType type)
        {
            switch (type)
            {
                case MessageType.Hello: return Envelope.BodyOneofCase.Hello;
                case MessageType.Welcome: return Envelope.BodyOneofCase.Welcome;
                case MessageType.Command: return Envelope.BodyOneofCase.Command;
                case MessageType.Receipt: return Envelope.BodyOneofCase.Receipt;
                case MessageType.PlayerSnapshot: return Envelope.BodyOneofCase.PlayerSnapshot;
                case MessageType.PlayerSnapshotQuery: return Envelope.BodyOneofCase.PlayerSnapshotQuery;
                case MessageType.PublicSnapshot: return Envelope.BodyOneofCase.PublicSnapshot;
                case MessageType.PublicSnapshotQuery: return Envelope.BodyOneofCase.PublicSnapshotQuery;
                case MessageType.RoomUpdate: return Envelope.BodyOneofCase.RoomUpdate;
                case MessageType.SyncRequest: return Envelope.BodyOneofCase.SyncRequest;
                case MessageType.SyncResponse: return Envelope.BodyOneofCase.SyncResponse;
                case MessageType.ReceiptQuery: return Envelope.BodyOneofCase.ReceiptQuery;
                case MessageType.ReceiptLookup: return Envelope.BodyOneofCase.ReceiptLookup;
                case MessageType.Heartbeat: return Envelope.BodyOneofCase.Heartbeat;
                case MessageType.Error: return Envelope.BodyOneofCase.Error;
                case MessageType.Close: return Envelope.BodyOneofCase.Close;
                case MessageType.PlayerUpdate: return Envelope.BodyOneofCase.PlayerUpdate;
                case MessageType.PublicUpdate: return Envelope.BodyOneofCase.PublicUpdate;
                default: return Envelope.BodyOneofCase.None;
            }
        }

        private static PublicEventEnvelope.EventOneofCase ExpectedPublicEventCase(EventType type)
        {
            switch (type)
            {
                case EventType.StackingStarted: return PublicEventEnvelope.EventOneofCase.StackingStarted;
                case EventType.WallBuilt: return PublicEventEnvelope.EventOneofCase.WallBuilt;
                case EventType.DealingComplete: return PublicEventEnvelope.EventOneofCase.DealingComplete;
                case EventType.TileDiscarded: return PublicEventEnvelope.EventOneofCase.TileDiscarded;
                case EventType.ChiDeclared: return PublicEventEnvelope.EventOneofCase.ChiDeclared;
                case EventType.PonDeclared: return PublicEventEnvelope.EventOneofCase.PonDeclared;
                case EventType.KanDeclared: return PublicEventEnvelope.EventOneofCase.KanDeclared;
                case EventType.RiichiDeclared: return PublicEventEnvelope.EventOneofCase.RiichiDeclared;
                case EventType.TsumoWin: return PublicEventEnvelope.EventOneofCase.TsumoWin;
                case EventType.RonWin: return PublicEventEnvelope.EventOneofCase.RonWin;
                case EventType.DoraRevealed: return PublicEventEnvelope.EventOneofCase.DoraRevealed;
                case EventType.DrawGame: return PublicEventEnvelope.EventOneofCase.DrawGame;
                case EventType.ScoreSettled: return PublicEventEnvelope.EventOneofCase.ScoreSettled;
                case EventType.RoundEnded: return PublicEventEnvelope.EventOneofCase.RoundEnded;
                case EventType.MatchEnded: return PublicEventEnvelope.EventOneofCase.MatchEnded;
                default: return PublicEventEnvelope.EventOneofCase.None;
            }
        }

        private static byte[] GetPublicEventBodyBytes(PublicEventEnvelope wireEvent)
        {
            switch (wireEvent.EventCase)
            {
                case PublicEventEnvelope.EventOneofCase.StackingStarted: return wireEvent.StackingStarted.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.WallBuilt: return wireEvent.WallBuilt.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.DealingComplete: return wireEvent.DealingComplete.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.TileDiscarded: return wireEvent.TileDiscarded.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.ChiDeclared: return wireEvent.ChiDeclared.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.PonDeclared: return wireEvent.PonDeclared.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.KanDeclared: return wireEvent.KanDeclared.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.RiichiDeclared: return wireEvent.RiichiDeclared.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.TsumoWin: return wireEvent.TsumoWin.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.RonWin: return wireEvent.RonWin.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.DoraRevealed: return wireEvent.DoraRevealed.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.DrawGame: return wireEvent.DrawGame.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.ScoreSettled: return wireEvent.ScoreSettled.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.RoundEnded: return wireEvent.RoundEnded.ToByteArray();
                case PublicEventEnvelope.EventOneofCase.MatchEnded: return wireEvent.MatchEnded.ToByteArray();
                default: return Array.Empty<byte>();
            }
        }

        private static WireEventDecodeResult PublicUnknownResult(PublicEventEnvelope wireEvent, int unknownFields)
        {
            byte[] raw = wireEvent.HasUnknownPayload
                ? wireEvent.UnknownPayload.ToByteArray()
                : GetPublicEventBodyBytes(wireEvent);

            var unknown = new UnknownEventRecord(
                (uint)wireEvent.EventType,
                wireEvent.EventSchemaVersion,
                raw,
                new EventSeq(wireEvent.EventSeq),
                new StateVersion(wireEvent.CommandVersion),
                wireEvent.EventOrdinal);

            return new WireEventDecodeResult(
                WireEventDecodeStatus.Unknown,
                ProtocolEventRecord.UnknownEvent(unknown),
                ProtocolErrorCode.StreamGap,
                unknownFields);
        }

        private static WireEventDecodeResult PublicUnknownResultFromRaw(PublicEventEnvelope wireEvent, int unknownFields)
        {
            var unknown = new UnknownEventRecord(
                (uint)wireEvent.EventType,
                wireEvent.EventSchemaVersion,
                wireEvent.ToByteArray(),
                new EventSeq(wireEvent.EventSeq),
                new StateVersion(wireEvent.CommandVersion),
                wireEvent.EventOrdinal);

            return new WireEventDecodeResult(
                WireEventDecodeStatus.Unknown,
                ProtocolEventRecord.UnknownEvent(unknown),
                ProtocolErrorCode.StreamGap,
                unknownFields);
        }

        private static WireEventDecodeResult PublicRejected(string code, int unknownFields)
        {
            return new WireEventDecodeResult(WireEventDecodeStatus.Malformed, null, code, unknownFields);
        }

        private static EventEnvelope.EventOneofCase ExpectedEventCase(EventType type)
        {
            switch (type)
            {
                case EventType.StackingStarted: return EventEnvelope.EventOneofCase.StackingStarted;
                case EventType.WallBuilt: return EventEnvelope.EventOneofCase.WallBuilt;
                case EventType.TileDealt: return EventEnvelope.EventOneofCase.TileDealt;
                case EventType.DealingComplete: return EventEnvelope.EventOneofCase.DealingComplete;
                case EventType.TileDrawn: return EventEnvelope.EventOneofCase.TileDrawn;
                case EventType.TileDiscarded: return EventEnvelope.EventOneofCase.TileDiscarded;
                case EventType.ChiDeclared: return EventEnvelope.EventOneofCase.ChiDeclared;
                case EventType.PonDeclared: return EventEnvelope.EventOneofCase.PonDeclared;
                case EventType.KanDeclared: return EventEnvelope.EventOneofCase.KanDeclared;
                case EventType.RinshanDraw: return EventEnvelope.EventOneofCase.RinshanDraw;
                case EventType.RiichiDeclared: return EventEnvelope.EventOneofCase.RiichiDeclared;
                case EventType.TsumoWin: return EventEnvelope.EventOneofCase.TsumoWin;
                case EventType.RonWin: return EventEnvelope.EventOneofCase.RonWin;
                case EventType.DoraRevealed: return EventEnvelope.EventOneofCase.DoraRevealed;
                case EventType.CheatPointsChanged: return EventEnvelope.EventOneofCase.CheatPointsChanged;
                case EventType.DrawGame: return EventEnvelope.EventOneofCase.DrawGame;
                case EventType.ScoreSettled: return EventEnvelope.EventOneofCase.ScoreSettled;
                case EventType.RoundEnded: return EventEnvelope.EventOneofCase.RoundEnded;
                case EventType.MatchEnded: return EventEnvelope.EventOneofCase.MatchEnded;
                case EventType.SleeveBloomResolved: return EventEnvelope.EventOneofCase.SleeveBloomResolved;
                case EventType.BeamSwapResolved: return EventEnvelope.EventOneofCase.BeamSwapResolved;
                default: return EventEnvelope.EventOneofCase.None;
            }
        }

        private static byte[] GetEventBodyBytes(EventEnvelope wireEvent)
        {
            switch (wireEvent.EventCase)
            {
                case EventEnvelope.EventOneofCase.StackingStarted: return wireEvent.StackingStarted.ToByteArray();
                case EventEnvelope.EventOneofCase.WallBuilt: return wireEvent.WallBuilt.ToByteArray();
                case EventEnvelope.EventOneofCase.TileDealt: return wireEvent.TileDealt.ToByteArray();
                case EventEnvelope.EventOneofCase.DealingComplete: return wireEvent.DealingComplete.ToByteArray();
                case EventEnvelope.EventOneofCase.TileDrawn: return wireEvent.TileDrawn.ToByteArray();
                case EventEnvelope.EventOneofCase.TileDiscarded: return wireEvent.TileDiscarded.ToByteArray();
                case EventEnvelope.EventOneofCase.ChiDeclared: return wireEvent.ChiDeclared.ToByteArray();
                case EventEnvelope.EventOneofCase.PonDeclared: return wireEvent.PonDeclared.ToByteArray();
                case EventEnvelope.EventOneofCase.KanDeclared: return wireEvent.KanDeclared.ToByteArray();
                case EventEnvelope.EventOneofCase.RinshanDraw: return wireEvent.RinshanDraw.ToByteArray();
                case EventEnvelope.EventOneofCase.RiichiDeclared: return wireEvent.RiichiDeclared.ToByteArray();
                case EventEnvelope.EventOneofCase.TsumoWin: return wireEvent.TsumoWin.ToByteArray();
                case EventEnvelope.EventOneofCase.RonWin: return wireEvent.RonWin.ToByteArray();
                case EventEnvelope.EventOneofCase.DoraRevealed: return wireEvent.DoraRevealed.ToByteArray();
                case EventEnvelope.EventOneofCase.CheatPointsChanged: return wireEvent.CheatPointsChanged.ToByteArray();
                case EventEnvelope.EventOneofCase.DrawGame: return wireEvent.DrawGame.ToByteArray();
                case EventEnvelope.EventOneofCase.ScoreSettled: return wireEvent.ScoreSettled.ToByteArray();
                case EventEnvelope.EventOneofCase.RoundEnded: return wireEvent.RoundEnded.ToByteArray();
                case EventEnvelope.EventOneofCase.MatchEnded: return wireEvent.MatchEnded.ToByteArray();
                case EventEnvelope.EventOneofCase.SleeveBloomResolved: return wireEvent.SleeveBloomResolved.ToByteArray();
                case EventEnvelope.EventOneofCase.BeamSwapResolved: return wireEvent.BeamSwapResolved.ToByteArray();
                default: return Array.Empty<byte>();
            }
        }

        private static WireEventDecodeResult UnknownResult(EventEnvelope wireEvent, int unknownFields, string message)
        {
            byte[] raw = wireEvent.HasUnknownPayload
                ? wireEvent.UnknownPayload.ToByteArray()
                : GetEventBodyBytes(wireEvent);

            var unknown = new UnknownEventRecord(
                (uint)wireEvent.EventType,
                wireEvent.EventSchemaVersion,
                raw,
                new EventSeq(wireEvent.EventSeq),
                new StateVersion(wireEvent.CommandVersion),
                wireEvent.EventOrdinal);

            return new WireEventDecodeResult(
                WireEventDecodeStatus.Unknown,
                ProtocolEventRecord.UnknownEvent(unknown),
                ProtocolErrorCode.StreamGap,
                unknownFields);
        }

        private static WireEventDecodeResult Malformed(string message)
        {
            return new WireEventDecodeResult(WireEventDecodeStatus.Malformed, null, ProtocolErrorCode.MalformedMessage, 0);
        }
    }
}
