using System;
using System.Collections.Generic;

namespace Mahjong.Contracts
{
    /// <summary>Stable command numbers. These match the wire CommandType values.</summary>
    public enum CommandKind
    {
        Unspecified = 0,
        CreateRoom = 1,
        JoinRoom = 2,
        ListRooms = 3,
        ResumeSession = 4,
        LeaveRoom = 5,
        GetRoomInfo = 6,
        SetReady = 7,
        UpdateConfig = 8,
        StartMatch = 9,
        SubmitAction = 10,
        SubmitShufflePlan = 11,
        SubmitTileMark = 12,
        SubmitSleeveBloom = 13,
        SubmitSleeveBloomChoice = 14,
        SubmitSleeveBloomOffTurn = 15,
        SubmitSleeveBloomOffTurnChoice = 16,
        SubmitBeamSwap = 17,
        SubmitBeamSwapChoice = 18,
        NextRound = 19,
        ClaimControl = 20,
        ReleaseControl = 21,
        QueryRoundResult = 22,
        QueryPublicRoundResult = 23,
        JoinSpectator = 24,
        ResumeSpectator = 25,
        ResumePublic = 26,
        ResumeCursor = 27
    }

    /// <summary>V4's 13 legal action types, kept numerically stable.</summary>
    public enum ProtocolActionType
    {
        Unspecified = 0,
        StartStacking = 1,
        DealComplete = 2,
        DrawTile = 3,
        DiscardTile = 4,
        DeclareChi = 5,
        DeclarePon = 6,
        DeclareKan = 7,
        DeclareRiichi = 8,
        DeclareTsumo = 9,
        DeclareRon = 10,
        Pass = 11,
        DeclareAbortiveDraw = 12,
        DeclareAgariYame = 13
    }

    public enum SleeveBloomChoice
    {
        Unspecified = 0,
        First = 1,
        Second = 2
    }

    public enum BeamSwapChoice
    {
        Unspecified = 0,
        Swap = 1,
        KeepOriginal = 2
    }

    public sealed class CommandValidationException : Exception
    {
        public CommandValidationException(string message, string code = ProtocolErrorCode.MalformedMessage) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// A client command with its immutable original business payload. The
    /// payload is never rebuilt from Protobuf; a retry calls <see cref="Retry"/>
    /// and resends the same bytes and digest.
    /// </summary>
    public sealed class ProtocolCommand
    {
        public ProtocolCommand(
            CommandKind kind,
            BusinessPayload payload,
            RequestId requestId,
            RequestScopeId? scopeId,
            StateVersion expectedStateVersion,
            RoundId? roundId = null,
            DecisionId? decisionId = null,
            ControlEpoch? controlEpoch = null,
            OwnerEpoch? ownerEpoch = null,
            ushort payloadSchemaVersion = CanonicalPayloadDigest.FormatVersion)
        {
            if (kind == CommandKind.Unspecified)
            {
                throw new ArgumentException("Command kind must be explicit.", nameof(kind));
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (payloadSchemaVersion != CanonicalPayloadDigest.FormatVersion)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payloadSchemaVersion),
                    payloadSchemaVersion,
                    "Only canonical payload schema 1 is supported.");
            }

            if (requestId.IsEmpty)
            {
                throw new ArgumentException("A command requires a stable request id.", nameof(requestId));
            }

            if (!IsQuery(kind) && (!scopeId.HasValue || scopeId.Value.IsEmpty))
            {
                throw new ArgumentException("A side-effecting command requires a server-issued request scope.", nameof(scopeId));
            }

            CommandPayloadValidator.Validate(kind, payload);

            Kind = kind;
            Payload = payload;
            RequestId = requestId;
            ScopeId = scopeId;
            ExpectedStateVersion = expectedStateVersion;
            RoundId = roundId;
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            OwnerEpoch = ownerEpoch;
            PayloadSchemaVersion = payloadSchemaVersion;
        }

        public CommandKind Kind { get; }

        public BusinessPayload Payload { get; }

        public RequestId RequestId { get; }

        public RequestScopeId? ScopeId { get; }

        public StateVersion ExpectedStateVersion { get; }

        public RoundId? RoundId { get; }

        public DecisionId? DecisionId { get; }

        public ControlEpoch? ControlEpoch { get; }

        public OwnerEpoch? OwnerEpoch { get; }

        public ushort PayloadSchemaVersion { get; }

        public PayloadDigest CanonicalDigest => Payload.Digest;

        /// <summary>Returns the same immutable payload; retries must not re-encode it.</summary>
        public ProtocolCommand Retry()
        {
            return new ProtocolCommand(
                Kind,
                Payload,
                RequestId,
                ScopeId,
                ExpectedStateVersion,
                RoundId,
                DecisionId,
                ControlEpoch,
                OwnerEpoch,
                PayloadSchemaVersion);
        }

        public static bool IsQuery(CommandKind kind)
        {
            return kind == CommandKind.ListRooms ||
                   kind == CommandKind.QueryRoundResult ||
                   kind == CommandKind.QueryPublicRoundResult ||
                   kind == CommandKind.ResumePublic ||
                   kind == CommandKind.ResumeCursor;
        }
    }

    /// <summary>
    /// Kind-specific field numbers for the MJC1 canonical payload. These are
    /// independent of Protobuf tags and are frozen with the command schema.
    /// </summary>
    public static class CommandPayloadFields
    {
        public const ushort RoomName = 1;
        public const ushort RoomId = 1;
        public const ushort RulesetVersion = 2;
        public const ushort Length = 3;
        public const ushort TargetScore = 4;
        public const ushort AllowWest = 5;
        public const ushort PreferSeat = 2;
        public const ushort DisplayName = 3;
        public const ushort Cursor = 1;
        public const ushort Limit = 2;
        public const ushort ResumeToken = 1;
        public const ushort LastKnownStateVersion = 2;
        public const ushort Ready = 1;
        public const ushort RoundIdField = 1;
        public const ushort DecisionIdField = 2;
        public const ushort ControlEpochField = 3;
        public const ushort ActionType = 4;
        public const ushort OptionId = 5;
        public const ushort TileIds = 6;
        public const ushort MeldType = 7;
        public const ushort ShuffleTarget = 2;
        public const ushort SeeThrough = 3;
        public const ushort TileMarkTileIds = 4;
        public const ushort OfferId = 1;
        public const ushort Choice = 2;
        public const ushort WindowId = 4;
        public const ushort HandTileId = 3;
        public const ushort TargetSlot = 4;
        public const ushort ExpectedControlEpoch = 1;

        /// <summary>Dedicated Claim/Release control round id tag (avoids tag 1).</summary>
        public const ushort ControlRoundId = 20;

        /// <summary>Dedicated Claim/Release expected control epoch tag (avoids tag 1).</summary>
        public const ushort ExpectedControlEpochV2 = 21;
        public const ushort SessionIdField = 1;
        public const ushort StreamVersionField = 2;
        public const ushort ProjectionSeqField = 3;
        public const ushort SyncTargetField = 2;
        public const ushort ResumeProjectionSeq = 4;
    }

    public static class CommandPayloadValidator
    {
        public static void Validate(CommandKind kind, BusinessPayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            CanonicalPayloadDocument document;
            try
            {
                document = CanonicalPayloadParser.Parse(payload);
            }
            catch (CanonicalEncodingException ex)
            {
                throw new CommandValidationException("Malformed canonical payload for " + kind + ": " + ex.Message);
            }

            if (document.CommandKind != (ushort)kind)
            {
                throw new CommandValidationException(
                    "Canonical payload kind " + document.CommandKind + " does not match command " + (ushort)kind + ".");
            }

            try
            {
                switch (kind)
                {
                case CommandKind.CreateRoom:
                    RequireString(document, CommandPayloadFields.RoomName, "roomName");
                    RequireUInt32(document, CommandPayloadFields.RulesetVersion, "rulesetVersion");
                    RequireUInt32(document, CommandPayloadFields.Length, "length");
                    OptionalInt64(document, CommandPayloadFields.TargetScore, "targetScore");
                    OptionalBool(document, CommandPayloadFields.AllowWest, "allowWest");
                    break;
                case CommandKind.JoinRoom:
                    RequireString(document, CommandPayloadFields.RoomId, "roomId");
                    OptionalUInt32(document, CommandPayloadFields.PreferSeat, "preferSeat", 0, 3);
                    OptionalString(document, CommandPayloadFields.DisplayName, "displayName");
                    break;
                case CommandKind.ListRooms:
                    OptionalUInt64(document, CommandPayloadFields.Cursor, "cursor");
                    OptionalUInt32(document, CommandPayloadFields.Limit, "limit", 1, 100);
                    break;
                case CommandKind.ResumeSession:
                    RequireString(document, CommandPayloadFields.ResumeToken, "resumeToken");
                    OptionalUInt64(document, CommandPayloadFields.LastKnownStateVersion, "lastKnownStateVersion");
                    break;
                case CommandKind.LeaveRoom:
                case CommandKind.GetRoomInfo:
                case CommandKind.StartMatch:
                case CommandKind.JoinSpectator:
                    RequireString(document, CommandPayloadFields.RoomId, "roomId");
                    break;
                case CommandKind.QueryRoundResult:
                case CommandKind.QueryPublicRoundResult:
                    RequireString(document, CommandPayloadFields.RoundIdField, "roundId");
                    break;
                case CommandKind.SetReady:
                    RequireBool(document, CommandPayloadFields.Ready, "ready");
                    break;
                case CommandKind.UpdateConfig:
                    RequireUInt32(document, CommandPayloadFields.RulesetVersion, "rulesetVersion");
                    RequireUInt32(document, CommandPayloadFields.Length, "length");
                    break;
                case CommandKind.SubmitAction:
                    RequireString(document, CommandPayloadFields.RoundIdField, "roundId");
                    RequireString(document, CommandPayloadFields.DecisionIdField, "decisionId");
                    RequireUInt64(document, CommandPayloadFields.ControlEpochField, "controlEpoch");
                    uint action = RequireUInt32(document, CommandPayloadFields.ActionType, "actionType");
                    if (action < (uint)ProtocolActionType.DrawTile || action > (uint)ProtocolActionType.DeclareAgariYame)
                    {
                        throw new CommandValidationException(
                            "actionType is outside the player-submittable action set (system StartStacking/DealComplete are not commands).");
                    }

                    OptionalString(document, CommandPayloadFields.OptionId, "optionId");
                    OptionalRepeatedUInt32(document, CommandPayloadFields.TileIds, "tileIds", 0, 14);
                    OptionalUInt32(document, CommandPayloadFields.MeldType, "meldType", 1, 5);
                    break;
                case CommandKind.SubmitShufflePlan:
                    RequireString(document, CommandPayloadFields.RoundIdField, "roundId");
                    uint target = RequireUInt32(document, CommandPayloadFields.ShuffleTarget, "target");
                    if (target > 6 && target != 255)
                    {
                        throw new CommandValidationException("shuffle target is outside the frozen target set.");
                    }

                    RequireBool(document, CommandPayloadFields.SeeThrough, "seeThrough");
                    break;
                case CommandKind.SubmitTileMark:
                    RequireString(document, CommandPayloadFields.RoundIdField, "roundId");
                    RequireString(document, CommandPayloadFields.DecisionIdField, "decisionId");
                    RequireUInt64(document, CommandPayloadFields.ControlEpochField, "controlEpoch");
                    RequireTileIds(document, CommandPayloadFields.TileMarkTileIds, "tileIds", 4, 4);
                    break;
                case CommandKind.SubmitSleeveBloom:
                case CommandKind.SubmitBeamSwap:
                    RequireString(document, CommandPayloadFields.RoundIdField, "roundId");
                    RequireString(document, CommandPayloadFields.DecisionIdField, "decisionId");
                    RequireUInt64(document, CommandPayloadFields.ControlEpochField, "controlEpoch");
                    break;
                case CommandKind.SubmitSleeveBloomOffTurn:
                    RequireString(document, CommandPayloadFields.RoundIdField, "roundId");
                    RequireString(document, CommandPayloadFields.DecisionIdField, "decisionId");
                    RequireUInt64(document, CommandPayloadFields.ControlEpochField, "controlEpoch");
                    RequireString(document, CommandPayloadFields.WindowId, "windowId");
                    break;
                case CommandKind.SubmitSleeveBloomChoice:
                case CommandKind.SubmitSleeveBloomOffTurnChoice:
                    RequireString(document, CommandPayloadFields.OfferId, "offerId");
                    uint bloomChoice = RequireUInt32(document, CommandPayloadFields.Choice, "choice");
                    if (bloomChoice != (uint)SleeveBloomChoice.First && bloomChoice != (uint)SleeveBloomChoice.Second)
                    {
                        throw new CommandValidationException("sleeve bloom choice must be FIRST or SECOND.");
                    }

                    break;
                case CommandKind.SubmitBeamSwapChoice:
                    RequireString(document, CommandPayloadFields.OfferId, "offerId");
                    uint beamChoice = RequireUInt32(document, CommandPayloadFields.Choice, "choice");
                    if (beamChoice != (uint)BeamSwapChoice.Swap && beamChoice != (uint)BeamSwapChoice.KeepOriginal)
                    {
                        throw new CommandValidationException("beam swap choice must be SWAP or KEEP_ORIGINAL.");
                    }

                    if (beamChoice == (uint)BeamSwapChoice.Swap)
                    {
                        OptionalUInt32(document, CommandPayloadFields.HandTileId, "handTileId", 0, 135, required: true);
                        OptionalUInt32(document, CommandPayloadFields.TargetSlot, "targetSlot", 0, 135, required: true);
                    }

                    break;
                case CommandKind.NextRound:
                    RequireString(document, CommandPayloadFields.RoundIdField, "roundId");
                    break;
                case CommandKind.ClaimControl:
                case CommandKind.ReleaseControl:
                    RequireUInt64(document, CommandPayloadFields.ExpectedControlEpochV2, "expectedControlEpoch");
                    RequireString(document, CommandPayloadFields.ControlRoundId, "roundId");
                    break;
                case CommandKind.ResumeSpectator:
                    RequireString(document, CommandPayloadFields.ResumeToken, "resumeToken");
                    break;
                case CommandKind.ResumePublic:
                    RequireString(document, CommandPayloadFields.SessionIdField, "sessionId");
                    RequireUInt64(document, CommandPayloadFields.StreamVersionField, "streamVersion");
                    RequireUInt64(document, CommandPayloadFields.ProjectionSeqField, "projectionSeq");
                    break;
                case CommandKind.ResumeCursor:
                    RequireString(document, CommandPayloadFields.SessionIdField, "sessionId");
                    OptionalUInt32(document, CommandPayloadFields.SyncTargetField, "target", 0, 3, required: true);
                    RequireUInt64(document, CommandPayloadFields.StreamVersionField, "streamVersion");
                    RequireUInt64(document, CommandPayloadFields.ResumeProjectionSeq, "projectionSeq");
                    break;
                default:
                    throw new CommandValidationException("Unsupported command kind " + kind + ".");
                }
            }
            catch (CanonicalEncodingException ex)
            {
                throw new CommandValidationException("Invalid " + kind + " payload: " + ex.Message);
            }
        }

        private static string RequireString(CanonicalPayloadDocument document, ushort field, string name)
        {
            string value = document.RequireString(field, name);
            if (string.IsNullOrEmpty(value))
            {
                throw new CommandValidationException(name + " must not be empty.");
            }

            return value;
        }

        private static uint RequireUInt32(CanonicalPayloadDocument document, ushort field, string name)
        {
            return document.RequireUInt32(field, name);
        }

        private static ulong RequireUInt64(CanonicalPayloadDocument document, ushort field, string name)
        {
            return document.RequireUInt64(field, name);
        }

        private static bool RequireBool(CanonicalPayloadDocument document, ushort field, string name)
        {
            return document.RequireBool(field, name);
        }

        private static void RequireTileIds(CanonicalPayloadDocument document, ushort field, string name, int min, int max)
        {
            IReadOnlyList<uint> values = document.RequireRepeatedUInt32(field, name);
            if (values.Count < min || values.Count > max)
            {
                throw new CommandValidationException(name + " must contain " + min + ".." + max + " tile ids.");
            }

            var seen = new HashSet<uint>();
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] > 135)
                {
                    throw new CommandValidationException(name + " contains a tile id outside 0..135.");
                }

                if (!seen.Add(values[i]))
                {
                    throw new CommandValidationException(name + " contains duplicate tile ids.");
                }
            }
        }

        private static void OptionalString(CanonicalPayloadDocument document, ushort field, string name)
        {
            if (document.HasField(field))
            {
                RequireString(document, field, name);
            }
        }

        private static void OptionalBool(CanonicalPayloadDocument document, ushort field, string name)
        {
            if (document.HasField(field))
            {
                document.RequireBool(field, name);
            }
        }

        private static void OptionalInt64(CanonicalPayloadDocument document, ushort field, string name)
        {
            if (document.HasField(field))
            {
                document.RequireInt64(field, name);
            }
        }

        private static void OptionalUInt64(CanonicalPayloadDocument document, ushort field, string name)
        {
            if (document.HasField(field))
            {
                document.RequireUInt64(field, name);
            }
        }

        private static void OptionalUInt32(
            CanonicalPayloadDocument document,
            ushort field,
            string name,
            uint min,
            uint max,
            bool required = false)
        {
            if (!document.HasField(field))
            {
                if (required)
                {
                    throw new CommandValidationException("Missing required field " + name + ".");
                }

                return;
            }

            uint value = document.RequireUInt32(field, name);
            if (value < min || value > max)
            {
                throw new CommandValidationException(name + " must be in " + min + ".." + max + ".");
            }
        }

        private static void OptionalRepeatedUInt32(
            CanonicalPayloadDocument document,
            ushort field,
            string name,
            int min,
            int max)
        {
            if (!document.HasField(field))
            {
                if (min > 0)
                {
                    throw new CommandValidationException("Missing required field " + name + ".");
                }

                return;
            }

            IReadOnlyList<uint> values = document.RequireRepeatedUInt32(field, name);
            if (values.Count < min || values.Count > max)
            {
                throw new CommandValidationException(name + " has an invalid count.");
            }

            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] > 135)
                {
                    throw new CommandValidationException(name + " contains a tile id outside 0..135.");
                }
            }
        }
    }

    /// <summary>Canonical command builders. They are the only supported way to create v1 payloads.</summary>
    public static class CommandPayloadFactory
    {
        public static BusinessPayload CreateRoom(string roomName, uint rulesetVersion, uint length, long? targetScore = null, bool? allowWest = null)
        {
            if (string.IsNullOrEmpty(roomName))
            {
                throw new ArgumentException("roomName is required.", nameof(roomName));
            }

            var writer = new CanonicalPayloadWriter((ushort)CommandKind.CreateRoom);
            writer.WriteString(CommandPayloadFields.RoomName, roomName);
            writer.WriteUInt32(CommandPayloadFields.RulesetVersion, rulesetVersion);
            writer.WriteUInt32(CommandPayloadFields.Length, length);
            if (targetScore.HasValue)
            {
                writer.WriteInt64(CommandPayloadFields.TargetScore, targetScore.Value);
            }

            if (allowWest.HasValue)
            {
                writer.WriteBool(CommandPayloadFields.AllowWest, allowWest.Value);
            }

            return writer.Build();
        }

        public static BusinessPayload JoinRoom(string roomId, uint? preferSeat = null, string? displayName = null)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.JoinRoom);
            writer.WriteString(CommandPayloadFields.RoomId, roomId);
            if (preferSeat.HasValue)
            {
                writer.WriteUInt32(CommandPayloadFields.PreferSeat, preferSeat.Value);
            }

            if (displayName != null)
            {
                writer.WriteString(CommandPayloadFields.DisplayName, displayName);
            }

            return writer.Build();
        }

        public static BusinessPayload SubmitAction(
            string roundId,
            string decisionId,
            ulong controlEpoch,
            ProtocolActionType actionType,
            string? optionId = null,
            IReadOnlyList<uint>? tileIds = null,
            uint? meldType = null)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.SubmitAction);
            writer.WriteString(CommandPayloadFields.RoundIdField, roundId);
            writer.WriteString(CommandPayloadFields.DecisionIdField, decisionId);
            writer.WriteUInt64(CommandPayloadFields.ControlEpochField, controlEpoch);
            writer.WriteUInt32(CommandPayloadFields.ActionType, (uint)actionType);
            if (optionId != null)
            {
                writer.WriteString(CommandPayloadFields.OptionId, optionId);
            }

            if (tileIds != null)
            {
                writer.WriteRepeatedUInt32(CommandPayloadFields.TileIds, tileIds);
            }

            if (meldType.HasValue)
            {
                writer.WriteUInt32(CommandPayloadFields.MeldType, meldType.Value);
            }

            return writer.Build();
        }

        public static BusinessPayload SubmitShufflePlan(string roundId, uint target, bool seeThrough)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.SubmitShufflePlan);
            writer.WriteString(CommandPayloadFields.RoundIdField, roundId);
            writer.WriteUInt32(CommandPayloadFields.ShuffleTarget, target);
            writer.WriteBool(CommandPayloadFields.SeeThrough, seeThrough);
            return writer.Build();
        }

        public static BusinessPayload SubmitTileMark(
            string roundId,
            string decisionId,
            ulong controlEpoch,
            IReadOnlyList<uint> tileIds)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.SubmitTileMark);
            writer.WriteString(CommandPayloadFields.RoundIdField, roundId);
            writer.WriteString(CommandPayloadFields.DecisionIdField, decisionId);
            writer.WriteUInt64(CommandPayloadFields.ControlEpochField, controlEpoch);
            writer.WriteRepeatedUInt32(CommandPayloadFields.TileMarkTileIds, tileIds);
            return writer.Build();
        }

        public static BusinessPayload SubmitSleeveBloom(string roundId, string decisionId, ulong controlEpoch)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.SubmitSleeveBloom);
            writer.WriteString(CommandPayloadFields.RoundIdField, roundId);
            writer.WriteString(CommandPayloadFields.DecisionIdField, decisionId);
            writer.WriteUInt64(CommandPayloadFields.ControlEpochField, controlEpoch);
            return writer.Build();
        }

        public static BusinessPayload SubmitSleeveBloomChoice(string offerId, SleeveBloomChoice choice)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.SubmitSleeveBloomChoice);
            writer.WriteString(CommandPayloadFields.OfferId, offerId);
            writer.WriteUInt32(CommandPayloadFields.Choice, (uint)choice);
            return writer.Build();
        }

        public static BusinessPayload SubmitBeamSwap(string roundId, string decisionId, ulong controlEpoch)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.SubmitBeamSwap);
            writer.WriteString(CommandPayloadFields.RoundIdField, roundId);
            writer.WriteString(CommandPayloadFields.DecisionIdField, decisionId);
            writer.WriteUInt64(CommandPayloadFields.ControlEpochField, controlEpoch);
            return writer.Build();
        }

        public static BusinessPayload SubmitBeamSwapChoice(
            string offerId,
            BeamSwapChoice choice,
            uint? handTileId = null,
            uint? targetSlot = null)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.SubmitBeamSwapChoice);
            writer.WriteString(CommandPayloadFields.OfferId, offerId);
            writer.WriteUInt32(CommandPayloadFields.Choice, (uint)choice);
            if (handTileId.HasValue)
            {
                writer.WriteUInt32(CommandPayloadFields.HandTileId, handTileId.Value);
            }

            if (targetSlot.HasValue)
            {
                writer.WriteUInt32(CommandPayloadFields.TargetSlot, targetSlot.Value);
            }

            return writer.Build();
        }

        public static BusinessPayload ClaimControl(string roundId, ulong expectedControlEpoch)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.ClaimControl);
            writer.WriteString(CommandPayloadFields.ControlRoundId, roundId);
            writer.WriteUInt64(CommandPayloadFields.ExpectedControlEpochV2, expectedControlEpoch);
            return writer.Build();
        }

        public static BusinessPayload ReleaseControl(string roundId, ulong expectedControlEpoch)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.ReleaseControl);
            writer.WriteString(CommandPayloadFields.ControlRoundId, roundId);
            writer.WriteUInt64(CommandPayloadFields.ExpectedControlEpochV2, expectedControlEpoch);
            return writer.Build();
        }

        public static BusinessPayload ResumePublic(string sessionId, ulong streamVersion, ulong projectionSeq)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.ResumePublic);
            writer.WriteString(CommandPayloadFields.SessionIdField, sessionId);
            writer.WriteUInt64(CommandPayloadFields.StreamVersionField, streamVersion);
            writer.WriteUInt64(CommandPayloadFields.ProjectionSeqField, projectionSeq);
            return writer.Build();
        }

        public static BusinessPayload ResumeCursor(string sessionId, uint target, ulong streamVersion, ulong projectionSeq)
        {
            var writer = new CanonicalPayloadWriter((ushort)CommandKind.ResumeCursor);
            writer.WriteString(CommandPayloadFields.SessionIdField, sessionId);
            writer.WriteUInt32(CommandPayloadFields.SyncTargetField, target);
            writer.WriteUInt64(CommandPayloadFields.StreamVersionField, streamVersion);
            writer.WriteUInt64(CommandPayloadFields.ResumeProjectionSeq, projectionSeq);
            return writer.Build();
        }
    }
}
