using System;
using System.Collections.Generic;
using System.Globalization;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;

namespace Mahjong.TableService
{
    /// <summary>A connection-scoped identity bundle established by HELLO admission.</summary>
    public sealed class TableConnectionIdentity
    {
        public TableConnectionIdentity(
            string sessionId,
            string roomId,
            TableAdmissionRole role,
            int seat,
            string accountId,
            string scopeId,
            long scopeIssuedAtMs,
            long scopeExpiresAtMs,
            string jti)
        {
            SessionId = sessionId;
            RoomId = roomId;
            Role = role;
            Seat = seat;
            AccountId = accountId;
            ScopeId = scopeId;
            ScopeIssuedAtMs = scopeIssuedAtMs;
            ScopeExpiresAtMs = scopeExpiresAtMs;
            Jti = jti;
        }

        public string SessionId { get; }

        public string RoomId { get; }

        public TableAdmissionRole Role { get; }

        /// <summary>0..3 for a player, -1 (protocol no-seat) for a spectator.</summary>
        public int Seat { get; }

        public string AccountId { get; }

        public string ScopeId { get; }

        public long ScopeIssuedAtMs { get; }

        public long ScopeExpiresAtMs { get; }

        public string Jti { get; }

        public bool IsPlayer => Role == TableAdmissionRole.Player;
    }

    /// <summary>Outcome of mapping one inbound COMMAND envelope.</summary>
    public abstract class TableCommandRoute
    {
        private TableCommandRoute()
        {
        }

        /// <summary>Submit the mapped durable command envelope through SubmitAsync.</summary>
        public sealed class BusinessCommand : TableCommandRoute
        {
            public BusinessCommand(TableCommandEnvelope envelope)
            {
                Envelope = envelope;
            }

            public TableCommandEnvelope Envelope { get; }
        }

        /// <summary>Submit the mapped cheat command through SubmitCheatAsync.</summary>
        public sealed class CheatCommand : TableCommandRoute
        {
            public CheatCommand(TableCheatCommandEnvelope envelope)
            {
                Envelope = envelope;
            }

            public TableCheatCommandEnvelope Envelope { get; }
        }

        /// <summary>Human reclaim (CLAIM_CONTROL) or explicit takeover (RELEASE_CONTROL).</summary>
        public sealed class Control : TableCommandRoute
        {
            public Control(bool reclaim, int seat, OwnerEpoch ownerEpoch, ControlEpoch expectedControlEpoch, string reason)
            {
                Reclaim = reclaim;
                Seat = seat;
                OwnerEpoch = ownerEpoch;
                ExpectedControlEpoch = expectedControlEpoch;
                Reason = reason;
            }

            public bool Reclaim { get; }

            public int Seat { get; }

            public OwnerEpoch OwnerEpoch { get; }

            public ControlEpoch ExpectedControlEpoch { get; }

            public string Reason { get; }
        }

        /// <summary>Rejected before entering the actor: zero side effects, no receipt written.</summary>
        public sealed class PreActorRejection : TableCommandRoute
        {
            public PreActorRejection(string errorCode, string fieldPath, string detailCode, bool retryable = false)
            {
                ErrorCode = errorCode;
                FieldPath = fieldPath;
                DetailCode = detailCode;
                Retryable = retryable;
            }

            public string ErrorCode { get; }

            public string FieldPath { get; }

            public string DetailCode { get; }

            public bool Retryable { get; }
        }
    }

    /// <summary>
    /// D-1-01 (b): the trusted response-window authority is unavailable for this discard —
    /// the mapped route must fail closed (retryable) instead of silently skipping a possible
    /// legal claim window.
    /// </summary>
    public sealed class ResponseEligibilityUnavailableException : Exception
    {
        public ResponseEligibilityUnavailableException()
            : base("The trusted response-window authority is unavailable for this discard.")
        {
        }
    }

    /// <summary>
    /// Maps one inbound wire COMMAND onto the accepted TableRuntime actor surface.
    ///
    /// Frozen facts enforced here (never derived from client claims):
    ///  - envelope identity fences: session/room/seat/binding must match the admitted
    ///    connection; mismatch is a protocol violation, not a business rejection;
    ///  - spectator side-effect commands are refused before the actor;
    ///  - the idempotency digest is SHA-256 over Command.canonical_payload exactly;
    ///  - settlement-bearing actions are refused with the actor's authority semantics kept
    ///    (no ITableSettlementFactsPort is configured in P5.1, and clients never supply
    ///    trusted settlement facts);
    ///  - source versions (owner epoch, expected state version, control epoch) are passed
    ///    through untouched so the actor/commit-port fences — not the host — judge them.
    /// </summary>
    public static class TableCommandMapper
    {
        public const string SettlementAuthorityDetail = "SETTLEMENT_AUTHORITY_UNAVAILABLE";

        public const string NotTableScopeDetail = "COMMAND_KIND_NOT_TABLE_SCOPE";

        public const string BeamSwapSlotDetail = "BEAM_SWAP_SLOT_KIND_UNRESOLVED";

        public const string ResponseEligibilityDetail = "RESPONSE_WINDOW_AUTHORITY_UNAVAILABLE";

        public static TableCommandRoute Map(
            TableConnectionIdentity identity,
            Envelope envelope,
            OwnerEpoch currentOwnerEpoch,
            long recoveryGeneration,
            TableServiceOptions options,
            string? authoritativeRoundId,
            Func<uint, TableBeamSwapSlotResolution?>? beamSwapSlotResolver = null,
            Func<int, TileId, ulong, TableCommandRouteResponseEligibility?>? responseEligibilityResolver = null)
        {
            Command command = envelope.Command;

            // ---- identity fences (protocol violations: the connection lied about itself) ----
            if (!string.Equals(envelope.SessionId, identity.SessionId, StringComparison.Ordinal))
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.ProtocolViolation, "session_id", "SESSION_MISMATCH");
            }

            if (!string.Equals(envelope.RoomId, identity.RoomId, StringComparison.Ordinal))
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.ProtocolViolation, "room_id", "ROOM_MISMATCH");
            }

            if (envelope.SeatId != identity.Seat)
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.ProtocolViolation, "seat_id", "SEAT_MISMATCH");
            }

            if (envelope.BindingEpoch == 0)
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.MalformedMessage, "binding_epoch", "BINDING_EPOCH_INVALID");
            }

            if (envelope.OwnerEpoch == 0)
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.MalformedMessage, "owner_epoch", "OWNER_EPOCH_INVALID");
            }

            if (envelope.Scope != null && !string.Equals(envelope.Scope.ScopeId, identity.ScopeId, StringComparison.Ordinal))
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.MalformedMessage, "scope.scope_id", "SCOPE_MISMATCH");
            }

            // ---- role gate: spectator commands never enter the actor ----
            if (!identity.IsPlayer)
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.Unauthenticated, "scope.role", "SCOPE_ROLE_NOT_AUTHORIZED");
            }

            // ---- request id ----
            RequestId requestId;
            try
            {
                requestId = new RequestId(envelope.RequestId);
            }
            catch (ArgumentException)
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.MalformedMessage, "request_id", "REQUEST_ID_INVALID");
            }

            if (command.CanonicalPayload.IsEmpty)
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.MalformedMessage, "command.canonical_payload", "PAYLOAD_EMPTY");
            }

            var kind = (CommandKind)(int)command.Type;
            byte[] payloadBytes = command.CanonicalPayload.ToByteArray();
            // D-1-07: the negotiated limit is executed on the ingress path — never the frozen
            // default inside the mapper.
            int maxCommandPayloadBytes = Math.Max(1, options.MaxCommandPayloadBytes);
            if (payloadBytes.Length > maxCommandPayloadBytes)
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.MessageTooLarge, "command.canonical_payload", "PAYLOAD_TOO_LARGE");
            }

            BusinessPayload payload;
            try
            {
                payload = BusinessPayload.Retain(payloadBytes);
            }
            catch (ArgumentException)
            {
                return new TableCommandRoute.PreActorRejection(
                    ProtocolErrorCode.MalformedMessage, "command.canonical_payload", "PAYLOAD_EMPTY");
            }

            try
            {
                CommandPayloadValidator.Validate(kind, payload);
            }
            catch (CommandValidationException ex)
            {
                return new TableCommandRoute.PreActorRejection(
                    ex.Code, "command.canonical_payload", "PAYLOAD_INVALID");
            }

            var expectedStateVersion = new StateVersion(command.ExpectedStateVersion);
            var ownerEpoch = new OwnerEpoch(envelope.OwnerEpoch);
            var controlEpoch = new ControlEpoch(envelope.ControlEpoch);
            var digest = TablePayloadSha256.Compute(payload.AsSpan());

            switch (kind)
            {
                case CommandKind.SubmitAction:
                    return MapSubmitAction(
                        identity, requestId, payload, expectedStateVersion, ownerEpoch, digest,
                        recoveryGeneration, responseEligibilityResolver);

                case CommandKind.SubmitTileMark:
                case CommandKind.SubmitSleeveBloom:
                case CommandKind.SubmitSleeveBloomChoice:
                case CommandKind.SubmitSleeveBloomOffTurn:
                case CommandKind.SubmitSleeveBloomOffTurnChoice:
                case CommandKind.SubmitBeamSwap:
                case CommandKind.SubmitBeamSwapChoice:
                    return MapCheat(
                        identity, kind, requestId, payload, expectedStateVersion, ownerEpoch, controlEpoch, digest,
                        recoveryGeneration, options, authoritativeRoundId, beamSwapSlotResolver);

                case CommandKind.ClaimControl:
                case CommandKind.ReleaseControl:
                    return MapControl(identity, kind, requestId, payload, ownerEpoch);

                case CommandKind.CreateRoom:
                case CommandKind.JoinRoom:
                case CommandKind.ListRooms:
                case CommandKind.ResumeSession:
                case CommandKind.LeaveRoom:
                case CommandKind.GetRoomInfo:
                case CommandKind.SetReady:
                case CommandKind.UpdateConfig:
                case CommandKind.StartMatch:
                case CommandKind.JoinSpectator:
                case CommandKind.NextRound:
                case CommandKind.SubmitShufflePlan:
                case CommandKind.QueryRoundResult:
                case CommandKind.QueryPublicRoundResult:
                case CommandKind.ResumeSpectator:
                case CommandKind.ResumePublic:
                case CommandKind.ResumeCursor:
                    return new TableCommandRoute.PreActorRejection(
                        ProtocolErrorCode.MalformedMessage,
                        "command.type",
                        NotTableScopeDetail);

                default:
                    return new TableCommandRoute.PreActorRejection(
                        ProtocolErrorCode.UnknownMessageType, "command.type", "COMMAND_KIND_UNKNOWN");
            }
        }

        private static TableCommandRoute MapSubmitAction(
            TableConnectionIdentity identity,
            RequestId requestId,
            BusinessPayload payload,
            StateVersion expectedStateVersion,
            OwnerEpoch ownerEpoch,
            TablePayloadSha256 digest,
            long recoveryGeneration,
            Func<int, TileId, ulong, TableCommandRouteResponseEligibility?>? responseEligibilityResolver)
        {
            CanonicalPayloadDocument document = CanonicalPayloadParser.Parse(payload);
            string roundId = RequireStringField(document, CommandPayloadFields.RoundIdField, "roundId");
            string decisionId = RequireStringField(document, CommandPayloadFields.DecisionIdField, "decisionId");
            var controlEpoch = new ControlEpoch(document.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch"));
            uint action = document.RequireUInt32(CommandPayloadFields.ActionType, "actionType");
            IReadOnlyList<uint>? tileIds = document.HasField(CommandPayloadFields.TileIds)
                ? document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds")
                : null;
            uint? meldType = document.OptionalUInt32(CommandPayloadFields.MeldType);

            RoundCommand? roundCommand;
            switch ((ProtocolActionType)action)
            {
                case ProtocolActionType.DrawTile:
                    roundCommand = RoundCommand.Draw(roundId, identity.Seat);
                    break;
                case ProtocolActionType.DiscardTile:
                    TileId discardTile = RequireTile(tileIds, 0);
                    // D-1-01 (b): the response window that a discard can legally open belongs to
                    // the TRUSTED table (the actor's own committed round at the exact expected
                    // state version), never to a client claim. A discard whose trusted
                    // eligibility cannot be computed fails closed at this boundary — it never
                    // silently advances past a window other seats may legitimately need.
                    roundCommand = RoundCommand.Discard(
                        roundId, identity.Seat, discardTile, RequiredEligibleSeats(
                            responseEligibilityResolver, identity.Seat, discardTile, expectedStateVersion.Value));
                    break;
                case ProtocolActionType.DeclareChi:
                    roundCommand = RoundCommand.Chi(
                        roundId, identity.Seat, RequireTile(tileIds, 0), RequireTile(tileIds, 1), RequireTile(tileIds, 2));
                    break;
                case ProtocolActionType.DeclarePon:
                    roundCommand = RoundCommand.Pon(
                        roundId, identity.Seat, RequireTile(tileIds, 0), RequireTile(tileIds, 1), RequireTile(tileIds, 2));
                    break;
                case ProtocolActionType.DeclareKan:
                    roundCommand = meldType switch
                    {
                        (uint)Contracts.Wire.MeldType.OpenKan => RoundCommand.OpenKan(
                            roundId, identity.Seat, RequireTile(tileIds, 0), RequireTile(tileIds, 1),
                            RequireTile(tileIds, 2), RequireTile(tileIds, 3)),
                        (uint)Contracts.Wire.MeldType.ClosedKan => RoundCommand.ClosedKan(
                            roundId, identity.Seat, RequireTile(tileIds, 0), RequireTile(tileIds, 1),
                            RequireTile(tileIds, 2), RequireTile(tileIds, 3)),
                        (uint)Contracts.Wire.MeldType.AddedKan => RoundCommand.AddedKan(
                            roundId, identity.Seat, RequireTile(tileIds, 0)),
                        _ => null
                    };
                    if (roundCommand == null)
                    {
                        return new TableCommandRoute.PreActorRejection(
                            ProtocolErrorCode.MalformedMessage, "command.canonical_payload.meldType", "PAYLOAD_INVALID");
                    }

                    break;
                case ProtocolActionType.DeclareRiichi:
                    TileId riichiDiscardTile = RequireTile(tileIds, 0);
                    // A riichi discard opens the same trusted claim right as any other discard.
                    roundCommand = RoundCommand.Riichi(
                        roundId, identity.Seat, riichiDiscardTile, RequiredEligibleSeats(
                            responseEligibilityResolver, identity.Seat, riichiDiscardTile, expectedStateVersion.Value));
                    break;
                case ProtocolActionType.Pass:
                    roundCommand = RoundCommand.Pass(roundId, identity.Seat);
                    break;

                // Settlement-bearing declarations are admitted only through the actor's
                // settlement authority (SubmitSettlementAsync with a configured facts port).
                // P5.1 configures none; a client never supplies trusted settlement facts.
                case ProtocolActionType.DeclareTsumo:
                case ProtocolActionType.DeclareRon:
                case ProtocolActionType.DeclareAbortiveDraw:
                case ProtocolActionType.DeclareAgariYame:
                    return new TableCommandRoute.PreActorRejection(
                        ProtocolErrorCode.InternalError, "command.type", SettlementAuthorityDetail);

                default:
                    return new TableCommandRoute.PreActorRejection(
                        ProtocolErrorCode.MalformedMessage, "command.canonical_payload.actionType", "PAYLOAD_INVALID");
            }

            var envelope = new TableCommandEnvelope(
                roundCommand,
                requestId,
                expectedStateVersion,
                ownerEpoch,
                controlEpoch,
                new DecisionId(decisionId),
                recoveryGeneration: recoveryGeneration,
                new RequestScopeId(identity.ScopeId),
                "SUBMIT_ACTION",
                digest);
            return new TableCommandRoute.BusinessCommand(envelope);
        }

        private static TableCommandRoute MapCheat(
            TableConnectionIdentity identity,
            CommandKind kind,
            RequestId requestId,
            BusinessPayload payload,
            StateVersion expectedStateVersion,
            OwnerEpoch ownerEpoch,
            ControlEpoch controlEpoch,
            TablePayloadSha256 digest,
            long recoveryGeneration,
            TableServiceOptions options,
            string? authoritativeRoundId,
            Func<uint, TableBeamSwapSlotResolution?>? beamSwapSlotResolver)
        {
            CanonicalPayloadDocument document = CanonicalPayloadParser.Parse(payload);
            var action = kind switch
            {
                CommandKind.SubmitTileMark => RoundCheatAction.SubmitTileMark,
                CommandKind.SubmitSleeveBloom => RoundCheatAction.SubmitSleeveBloom,
                CommandKind.SubmitSleeveBloomChoice => RoundCheatAction.SubmitSleeveBloomChoice,
                CommandKind.SubmitSleeveBloomOffTurn => RoundCheatAction.SubmitSleeveBloomOffTurn,
                CommandKind.SubmitSleeveBloomOffTurnChoice => RoundCheatAction.SubmitSleeveBloomOffTurnChoice,
                CommandKind.SubmitBeamSwap => RoundCheatAction.SubmitBeamSwap,
                _ => RoundCheatAction.SubmitBeamSwapChoice
            };

            // D-1-02: choice commands (SleeveBloomChoice / OffTurnChoice / BeamSwapChoice) carry
            // NO round id in the frozen contract — their field 1 is the offer id. The round id
            // in the mapped envelope is the ACTOR'S authoritative current round, resolved at
            // this trusted boundary, never a client field and never re-parsed from the offer
            // id string.
            bool isOfferChoice =
                action == RoundCheatAction.SubmitSleeveBloomChoice ||
                action == RoundCheatAction.SubmitSleeveBloomOffTurnChoice ||
                action == RoundCheatAction.SubmitBeamSwapChoice;
            string roundId;
            if (isOfferChoice)
            {
                if (string.IsNullOrEmpty(authoritativeRoundId))
                {
                    // Fail closed instead of guessing a round that could silently drift from
                    // the actor's authority.
                    return new TableCommandRoute.PreActorRejection(
                        ProtocolErrorCode.InternalError, "command.round_id", "ROUND_AUTHORITY_UNAVAILABLE");
                }

                roundId = authoritativeRoundId;
            }
            else
            {
                roundId = document.HasField(CommandPayloadFields.RoundIdField)
                    ? RequireStringField(document, CommandPayloadFields.RoundIdField, "roundId")
                    : throw new InvalidOperationException("Validator guarantees a round id for cheat kinds.");
            }

            RoundCheatPayload cheatPayload;
            string? decisionId = null;
            switch (action)
            {
                case RoundCheatAction.SubmitTileMark:
                    decisionId = RequireStringField(document, CommandPayloadFields.DecisionIdField, "decisionId");
                    IReadOnlyList<uint> marked = document.RequireRepeatedUInt32(CommandPayloadFields.TileMarkTileIds, "tileIds");
                    var tiles = new List<TileId>(marked.Count);
                    foreach (uint tile in marked)
                    {
                        tiles.Add(new TileId((int)tile));
                    }

                    cheatPayload = RoundCheatPayload.TileMark(decisionId, tiles);
                    break;
                case RoundCheatAction.SubmitSleeveBloom:
                    decisionId = RequireStringField(document, CommandPayloadFields.DecisionIdField, "decisionId");
                    cheatPayload = RoundCheatPayload.SleeveBloom();
                    break;
                case RoundCheatAction.SubmitSleeveBloomChoice:
                case RoundCheatAction.SubmitSleeveBloomOffTurnChoice:
                    cheatPayload = RoundCheatPayload.SleeveBloomChoice(
                        RequireStringField(document, CommandPayloadFields.OfferId, "offerId"),
                        ParseKeep(document.RequireUInt32(CommandPayloadFields.Choice, "choice")));
                    break;
                case RoundCheatAction.SubmitSleeveBloomOffTurn:
                    decisionId = RequireStringField(document, CommandPayloadFields.DecisionIdField, "decisionId");
                    cheatPayload = RoundCheatPayload.OffTurn(
                        RequireStringField(document, CommandPayloadFields.WindowId, "windowId"));
                    break;
                case RoundCheatAction.SubmitBeamSwap:
                    decisionId = RequireStringField(document, CommandPayloadFields.DecisionIdField, "decisionId");
                    cheatPayload = RoundCheatPayload.BeamSwap();
                    break;
                default:
                {
                    uint choice = document.RequireUInt32(CommandPayloadFields.Choice, "choice");
                    string offerId = RequireStringField(document, CommandPayloadFields.OfferId, "offerId");
                    if (choice == (uint)Contracts.BeamSwapChoice.KeepOriginal)
                    {
                        cheatPayload = RoundCheatPayload.BeamSwapChoice(
                            offerId, BeamSwapAction.KeepOriginal, null, null);
                    }
                    else
                    {
                        // D-1-02: the frozen wire carries the target as one raw numeric slot.
                        // The KIND is resolved at this trusted boundary through the REAL offer
                        // candidate context the actor projected for this connection's owner
                        // stream — the same immutable offer state the Domain re-verifies every
                        // choice against. A slot index the context cannot resolve uniquely
                        // (a dead-wall source and a replacement source share small indices by
                        // design, so a numeric range is NOT a non-overlapping namespace) is
                        // never silently mapped to any one of the colliding entities: every
                        // unclear case fails closed here with an explicit boundary error.
                        uint rawIndex = document.RequireUInt32(CommandPayloadFields.TargetSlot, "targetSlot");
                        if (beamSwapSlotResolver == null)
                        {
                            return new TableCommandRoute.PreActorRejection(
                                ProtocolErrorCode.InternalError,
                                "command.canonical_payload.targetSlot",
                                BeamSwapSlotDetail);
                        }

                        TableBeamSwapSlotResolution? resolution = beamSwapSlotResolver(rawIndex);
                        if (resolution == null || resolution.Status == TableBeamSwapResolutionStatus.Ambiguous)
                        {
                            return new TableCommandRoute.PreActorRejection(
                                ProtocolErrorCode.InternalError,
                                "command.canonical_payload.targetSlot",
                                BeamSwapSlotDetail);
                        }

                        if (resolution.Status == TableBeamSwapResolutionStatus.UnknownIndex)
                        {
                            return new TableCommandRoute.PreActorRejection(
                                ProtocolErrorCode.MalformedMessage,
                                "command.canonical_payload.targetSlot",
                                "BEAM_SWAP_SLOT_UNKNOWN");
                        }

                        TableBeamSwapCandidate candidate = resolution.Candidate!;
                        Mahjong.Domain.Cheat.BeamSwapSlotKind resolvedKind = candidate.Kind;
                        cheatPayload = RoundCheatPayload.BeamSwapChoice(
                            offerId,
                            BeamSwapAction.Swap,
                            new TileId((int)document.RequireUInt32(CommandPayloadFields.HandTileId, "handTileId")),
                            resolvedKind switch
                            {
                                Mahjong.Domain.Cheat.BeamSwapSlotKind.WallSlot => BeamSwapSlot.Wall(candidate.SlotIndex),
                                Mahjong.Domain.Cheat.BeamSwapSlotKind.DeadWallSlot => BeamSwapSlot.DeadWall(candidate.SlotIndex),
                                _ => BeamSwapSlot.Replacement(candidate.SlotIndex)
                            });
                    }

                    break;
                }
            }

            var cheatCommand = new RoundCheatCommand(
                roundId,
                identity.Seat,
                action,
                requestId.Value,
                cheatPayload);
            DecisionId? envelopeDecisionId = decisionId == null ? null : new DecisionId(decisionId);
            var envelope = new TableCheatCommandEnvelope(
                cheatCommand,
                requestId,
                expectedStateVersion,
                ownerEpoch,
                controlEpoch,
                envelopeDecisionId,
                recoveryGeneration: recoveryGeneration,
                new RequestScopeId(identity.ScopeId),
                ApiFor(kind),
                digest);
            return new TableCommandRoute.CheatCommand(envelope);
        }

        private static TableCommandRoute MapControl(
            TableConnectionIdentity identity,
            CommandKind kind,
            RequestId requestId,
            BusinessPayload payload,
            OwnerEpoch ownerEpoch)
        {
            CanonicalPayloadDocument document = CanonicalPayloadParser.Parse(payload);
            var expectedControlEpoch = new ControlEpoch(
                document.RequireUInt64(CommandPayloadFields.ExpectedControlEpoch, "expectedControlEpoch"));
            bool reclaim = kind == CommandKind.ClaimControl;
            return new TableCommandRoute.Control(
                reclaim,
                identity.Seat,
                ownerEpoch,
                expectedControlEpoch,
                reclaim ? "CLAIM_CONTROL" : "RELEASE_CONTROL");
        }

        private static SleeveBloomKeep ParseKeep(uint choice)
        {
            return choice == (uint)SleeveBloomChoice.Second ? SleeveBloomKeep.Second : SleeveBloomKeep.First;
        }

        /// <summary>
        /// Reads a canonical string field and strips its 4-byte little-endian inner length
        /// prefix. CanonicalPayloadWriter.WriteString embeds the inner length inside the
        /// field value, and the frozen CanonicalFieldValue.AsString returns those bytes
        /// verbatim (AsRepeatedUInt32 strips its own count prefix; AsString does not — the
        /// first real string consumers were the frozen validator's non-empty checks, so the
        /// divergence was never observable before this mapper). The prefix bytes are always
        /// ≤ 0x1F for payloads within the 1 MiB cap, so strict UTF-8 decodes them 1:1 and
        /// removing exactly the first four characters is byte-exact. The frozen Contracts
        /// accessor itself is out of this delivery's allowed file set; the divergence is
        /// recorded in V5-P5.1-TABLESERVICE-DELIVERY.md for the caller to arbitrate.
        /// </summary>
        private static string RequireStringField(CanonicalPayloadDocument document, ushort fieldNumber, string fieldName)
        {
            string value = document.RequireString(fieldNumber, fieldName);
            if (value.Length < 4)
            {
                throw new CanonicalEncodingException(
                    "String field " + fieldName + " (" + fieldNumber + ") is shorter than its 4-byte inner length prefix.");
            }

            return value.Substring(4);
        }

        private static string ApiFor(CommandKind kind)
        {
            return kind switch
            {
                CommandKind.SubmitTileMark => "SUBMIT_TILE_MARK",
                CommandKind.SubmitSleeveBloom => "SUBMIT_SLEEVE_BLOOM",
                CommandKind.SubmitSleeveBloomChoice => "SUBMIT_SLEEVE_BLOOM_CHOICE",
                CommandKind.SubmitSleeveBloomOffTurn => "SUBMIT_SLEEVE_BLOOM_OFF_TURN",
                CommandKind.SubmitSleeveBloomOffTurnChoice => "SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE",
                CommandKind.SubmitBeamSwap => "SUBMIT_BEAM_SWAP",
                _ => "SUBMIT_BEAM_SWAP_CHOICE"
            };
        }

        private static TileId RequireTile(IReadOnlyList<uint>? tileIds, int index)
        {
            if (tileIds == null || index >= tileIds.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(tileIds), "The action requires tile id " + index + ".");
            }

            return new TileId((int)tileIds[index]);
        }

        /// <summary>
        /// D-1-01 (b): the response-window eligibility the mapper attaches to a discard/riichi
        /// command is derived by the trusted room runtime at this exact state version. A null
        /// (unusable) trusted result fails closed at the mapper boundary — the rejection is
        /// retryable and carries a distinct boundary detail: the freeze never maps a discard
        /// that quietly skips a legal claim window. An EMPTY set is an honest "no seat can
        /// legitimately claim this tile" — the Domain then auto-advances the next draw.
        /// </summary>
        private static IReadOnlyList<int>? RequiredEligibleSeats(
            Func<int, TileId, ulong, TableCommandRouteResponseEligibility?>? resolver,
            int actingSeat,
            TileId discardedTile,
            ulong expectedStateVersion)
        {
            if (resolver == null)
            {
                throw new CanonicalEncodingException(
                    "A discard/riichi command requires the trusted response-window resolver.");
            }

            TableCommandRouteResponseEligibility? trusted = resolver(actingSeat, discardedTile, expectedStateVersion);
            if (trusted == null)
            {
                throw new ResponseEligibilityUnavailableException();
            }

            return trusted.EligibleSeats;
        }

        /// <summary>Maps one authoritative operation result onto the frozen wire receipt.</summary>
        public static Contracts.Wire.Receipt MapReceipt(TableOperationResult result, string canonicalPayloadSha256Hex)
        {
            var receipt = new Contracts.Wire.Receipt
            {
                Replayed = result.Replayed,
                CanonicalPayloadSha256 = canonicalPayloadSha256Hex
            };

            switch (result.Status)
            {
                case TableOperationStatus.Accepted:
                case TableOperationStatus.Replayed:
                case TableOperationStatus.Rejected when result.Receipt != null:
                    TableCommandReceipt command = result.Receipt!;
                    receipt.Status = command.Status == Contracts.ReceiptStatus.Rejected
                        ? Contracts.Wire.ReceiptStatus.Rejected
                        : Contracts.Wire.ReceiptStatus.Applied;
                    receipt.ReasonCode = command.ReasonCode;
                    receipt.EvaluatedStateVersion = command.EvaluatedStateVersion.Value;
                    if (command.CommandVersion.HasValue)
                    {
                        receipt.CommandVersion = command.CommandVersion.Value.Value;
                    }

                    if (command.LastEventSeq.HasValue)
                    {
                        receipt.EventSeq = command.LastEventSeq.Value.Value;
                    }

                    break;
                case TableOperationStatus.Rejected:
                    // Rejection without a persisted receipt (admission fences): still an
                    // authoritative answer, reported as REJECTED with the actor reason.
                    receipt.Status = Contracts.Wire.ReceiptStatus.Rejected;
                    receipt.ReasonCode = WellFormedOrFallback(result.ReasonCode);
                    receipt.EvaluatedStateVersion = result.EvaluatedStateVersion.Value;
                    break;
                default:
                    throw new InvalidOperationException(
                        "A non-definitive operation status must map to a transport error, not a receipt: " + result.Status);
            }

            return receipt;
        }

        public static string ToHexLowercase(TablePayloadSha256 digest)
        {
            var text = new System.Text.StringBuilder(TablePayloadSha256.SizeBytes * 2);
            ReadOnlySpan<byte> bytes = digest.AsSpan();
            for (int i = 0; i < bytes.Length; i++)
            {
                text.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        private static string WellFormedOrFallback(string reasonCode)
        {
            return ProtocolErrorCode.IsWellFormed(reasonCode) ? reasonCode : ProtocolErrorCode.InternalError;
        }
    }
}
