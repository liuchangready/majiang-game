using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.TableRuntime
{
    /// <summary>Which stream an event is being projected for.</summary>
    public enum TableProjectionAudienceKind : byte
    {
        Spectator = 0,
        Seat = 1
    }

    /// <summary>A spectator audience or one explicit seat audience. There is no "public claim" input.</summary>
    public readonly struct TableProjectionAudience : IEquatable<TableProjectionAudience>
    {
        private TableProjectionAudience(TableProjectionAudienceKind kind, int seat)
        {
            Kind = kind;
            Seat = seat;
        }

        public TableProjectionAudienceKind Kind { get; }

        public int Seat { get; }

        public bool IsSpectator => Kind == TableProjectionAudienceKind.Spectator;

        public static TableProjectionAudience Spectator => new TableProjectionAudience(TableProjectionAudienceKind.Spectator, ProtocolConstants.NoSeat);

        public static TableProjectionAudience ForSeat(int seat)
        {
            TableProjectionCopy.Seat(seat, nameof(seat));
            return new TableProjectionAudience(TableProjectionAudienceKind.Seat, seat);
        }

        public bool Equals(TableProjectionAudience other) => Kind == other.Kind && Seat == other.Seat;

        public override bool Equals(object? obj) => obj is TableProjectionAudience other && Equals(other);

        public override int GetHashCode() => (int)Kind * 31 + Seat;

        public override string ToString() => IsSpectator ? "spectator" : "seat" + Seat;

        public static bool operator ==(TableProjectionAudience left, TableProjectionAudience right) => left.Equals(right);

        public static bool operator !=(TableProjectionAudience left, TableProjectionAudience right) => !left.Equals(right);
    }

    /// <summary>
    /// One committed event fact supplied to the projector. The visibility is not a field: it
    /// is derived from <see cref="Type"/> by the frozen whitelist, so a caller cannot promote
    /// an event by claiming PUBLIC. Sequence/version/ordinal are copied unchanged from the
    /// source command; this type never allocates a stream cursor.
    /// </summary>
    public sealed class TableEventProjectionInput
    {
        private readonly ReadOnlyCollection<uint> _tileIds;

        public TableEventProjectionInput(
            ProtocolEventType type,
            EventSeq eventSeq,
            StateVersion commandVersion,
            uint eventOrdinal,
            int seat = ProtocolConstants.NoSeat,
            IReadOnlyList<uint>? tileIds = null,
            bool isUraDora = false,
            bool isClosedKan = false,
            bool isChankan = false,
            long? cheatAmount = null,
            long? cheatBalance = null,
            string? publicReasonCode = null,
            string? cheatOfferId = null,
            bool? cheatKeptFirst = null,
            ProjectionBeamSwapResolution? cheatBeamSwapResolution = null,
            uint? cheatResultTileId = null,
            uint? cheatHandTileId = null,
            int? cheatTargetSlot = null)
        {
            if (type == ProtocolEventType.Unspecified || !ProtocolEventPolicy.IsKnownEventType((uint)type))
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.UnknownEventType,
                    "Event type " + (uint)type + " is not a frozen V5 event type.");
            }

            if (eventSeq.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(eventSeq), "A committed event sequence starts at 1.");
            }

            if (commandVersion.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandVersion), "A committed command version starts at 1.");
            }

            TableProjectionCopy.SeatOrNoSeat(seat, nameof(seat));
            _tileIds = TableProjectionCopy.Tiles(tileIds, nameof(tileIds), 4);
            if (isUraDora && type != ProtocolEventType.DoraRevealed)
            {
                throw new ArgumentException("Only DoraRevealed can carry the ura flag.", nameof(isUraDora));
            }

            if (isClosedKan && type != ProtocolEventType.KanDeclared)
            {
                throw new ArgumentException("Only KanDeclared can carry the closed-kan flag.", nameof(isClosedKan));
            }

            if (isClosedKan && _tileIds.Count != 4)
            {
                throw new ArgumentException("A closed kan requires all four owner-only tile identities.", nameof(tileIds));
            }

            if (isChankan && type != ProtocolEventType.RonWin)
            {
                throw new ArgumentException("Only RonWin can carry the chankan flag.", nameof(isChankan));
            }

            if ((cheatAmount.HasValue || cheatBalance.HasValue) && type != ProtocolEventType.CheatPointsChanged)
            {
                throw new ArgumentException("Only CheatPointsChanged can carry private cheat amounts.", nameof(cheatAmount));
            }

            if (type == ProtocolEventType.CheatPointsChanged &&
                (!cheatAmount.HasValue || !cheatBalance.HasValue))
            {
                throw new ArgumentException(
                    "CheatPointsChanged requires both amount and resulting balance.",
                    nameof(cheatAmount));
            }

            if (cheatBalance.HasValue && cheatBalance.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cheatBalance), "A cheat balance cannot be negative.");
            }

            bool hasResolutionFacts = cheatOfferId != null || cheatKeptFirst.HasValue ||
                                      cheatBeamSwapResolution.HasValue || cheatResultTileId.HasValue ||
                                      cheatHandTileId.HasValue || cheatTargetSlot.HasValue;
            if (hasResolutionFacts &&
                type != ProtocolEventType.SleeveBloomResolved &&
                type != ProtocolEventType.BeamSwapResolved)
            {
                throw new ArgumentException(
                    "Offer/resolution facts are only valid for SleeveBloomResolved/BeamSwapResolved.",
                    nameof(cheatOfferId));
            }

            if (type == ProtocolEventType.SleeveBloomResolved &&
                cheatBeamSwapResolution.HasValue)
            {
                throw new ArgumentException(
                    "SleeveBloomResolved cannot carry a beam swap resolution.",
                    nameof(cheatBeamSwapResolution));
            }

            if (type == ProtocolEventType.BeamSwapResolved &&
                (cheatKeptFirst.HasValue || cheatResultTileId.HasValue))
            {
                throw new ArgumentException(
                    "BeamSwapResolved cannot carry sleeve bloom keep/tile facts.",
                    nameof(cheatKeptFirst));
            }

            string reason = publicReasonCode ?? string.Empty;
            if (reason.Length > 0)
            {
                if (type != ProtocolEventType.DrawGame)
                {
                    throw new ArgumentException("Only DrawGame can carry a public reason code.", nameof(publicReasonCode));
                }

                if (!ProtocolErrorCode.IsWellFormed(reason))
                {
                    throw new ArgumentException("Public reason must be a stable uppercase code.", nameof(publicReasonCode));
                }
            }

            if (IsPrivateType(type) && seat == ProtocolConstants.NoSeat)
            {
                throw new ArgumentException("A private event requires an owner seat.", nameof(seat));
            }

            if (IsPrivateType(type) && _tileIds.Count > 0 && !IsPrivateTileEvent(type))
            {
                throw new ArgumentException("This private event cannot carry tile identities.", nameof(tileIds));
            }

            ValidateTilePayload(type, _tileIds.Count);

            Type = type;
            EventSeq = eventSeq;
            CommandVersion = commandVersion;
            EventOrdinal = eventOrdinal;
            Seat = seat;
            IsUraDora = isUraDora;
            IsClosedKan = isClosedKan;
            IsChankan = isChankan;
            CheatAmount = cheatAmount;
            CheatBalance = cheatBalance;
            PublicReasonCode = reason;
            CheatOfferId = cheatOfferId;
            CheatKeptFirst = cheatKeptFirst;
            CheatBeamSwapResolution = cheatBeamSwapResolution;
            CheatResultTileId = cheatResultTileId;
            CheatHandTileId = cheatHandTileId;
            CheatTargetSlot = cheatTargetSlot;
        }

        public ProtocolEventType Type { get; }

        public EventSeq EventSeq { get; }

        public StateVersion CommandVersion { get; }

        public uint EventOrdinal { get; }

        public int Seat { get; }

        public IReadOnlyList<uint> TileIds => _tileIds;

        public bool IsUraDora { get; }

        public bool IsClosedKan { get; }

        public bool IsChankan { get; }

        public long? CheatAmount { get; }

        public long? CheatBalance { get; }

        public string PublicReasonCode { get; }

        public string? CheatOfferId { get; }

        public bool? CheatKeptFirst { get; }

        public ProjectionBeamSwapResolution? CheatBeamSwapResolution { get; }

        public uint? CheatResultTileId { get; }

        public uint? CheatHandTileId { get; }

        public int? CheatTargetSlot { get; }

        internal static bool IsPrivateType(ProtocolEventType type) => ProtocolEventPolicy.IsPrivateEventType(type);

        internal static bool IsPrivateTileEvent(ProtocolEventType type) =>
            type == ProtocolEventType.TileDealt ||
            type == ProtocolEventType.TileDrawn ||
            type == ProtocolEventType.RinshanDraw;

        private static void ValidateTilePayload(ProtocolEventType type, int tileCount)
        {
            switch (type)
            {
                case ProtocolEventType.TileDealt:
                case ProtocolEventType.TileDrawn:
                case ProtocolEventType.RinshanDraw:
                case ProtocolEventType.TileDiscarded:
                case ProtocolEventType.DoraRevealed:
                case ProtocolEventType.TsumoWin:
                case ProtocolEventType.RonWin:
                    if (tileCount != 1)
                    {
                        throw new ArgumentException(type + " requires exactly one tile identity.", nameof(tileCount));
                    }

                    break;
                case ProtocolEventType.ChiDeclared:
                case ProtocolEventType.PonDeclared:
                    // RoundEvent records the called physical tile only. The immutable
                    // event facts carry the complete historical meld and the contract
                    // mapper verifies that the called tile belongs to it before emitting
                    // the public three-tile payload.
                    if (tileCount != 1 && tileCount != 3)
                    {
                        throw new ArgumentException(type + " requires one source called tile or three verified meld tile identities.", nameof(tileCount));
                    }

                    break;
                case ProtocolEventType.KanDeclared:
                    // The current Domain event carries the called tile only; an explicit
                    // four-tile meld is also accepted. Other counts are rejected.
                    if (tileCount != 1 && tileCount != 4)
                    {
                        throw new ArgumentException("KanDeclared requires one called tile or four meld tiles.", nameof(tileCount));
                    }

                    break;
                default:
                    if (tileCount != 0)
                    {
                        throw new ArgumentException(type + " cannot carry tile identities.", nameof(tileCount));
                    }

                    break;
            }
        }

        internal static bool IsSettlementType(ProtocolEventType type) =>
            type == ProtocolEventType.TsumoWin ||
            type == ProtocolEventType.RonWin ||
            type == ProtocolEventType.DrawGame ||
            type == ProtocolEventType.ScoreSettled ||
            type == ProtocolEventType.RoundEnded ||
            type == ProtocolEventType.MatchEnded;
    }

    /// <summary>
    /// The explicit whitelist result for one audience. Collections are copied; the source
    /// <see cref="TableEventProjectionInput"/>, Domain tile ids and cheat state are not reachable.
    /// </summary>
    public sealed class TableProjectedEvent
    {
        private const byte BodyVersion = 1;
        private static readonly byte[] BodyMagic = { (byte)'P', (byte)'3', (byte)'2', (byte)'A' };

        private readonly ReadOnlyCollection<uint> _tileIds;

        internal TableProjectedEvent(
            ProtocolEventType type,
            ProtocolEventVisibility visibility,
            EventSeq eventSeq,
            StateVersion commandVersion,
            uint eventOrdinal,
            int seat,
            IReadOnlyList<uint> tileIds,
            bool isUraDora,
            bool isClosedKan,
            bool isChankan,
            long? cheatAmount,
            long? cheatBalance,
            string publicReasonCode,
            string? cheatOfferId = null,
            bool? cheatKeptFirst = null,
            ProjectionBeamSwapResolution? cheatBeamSwapResolution = null,
            uint? cheatResultTileId = null,
            uint? cheatHandTileId = null,
            int? cheatTargetSlot = null)
        {
            Type = type;
            Visibility = visibility;
            EventSeq = eventSeq;
            CommandVersion = commandVersion;
            EventOrdinal = eventOrdinal;
            Seat = seat;
            _tileIds = new ReadOnlyCollection<uint>(new List<uint>(tileIds));
            IsUraDora = isUraDora;
            IsClosedKan = isClosedKan;
            IsChankan = isChankan;
            CheatAmount = cheatAmount;
            CheatBalance = cheatBalance;
            PublicReasonCode = publicReasonCode ?? string.Empty;
            CheatOfferId = cheatOfferId;
            CheatKeptFirst = cheatKeptFirst;
            CheatBeamSwapResolution = cheatBeamSwapResolution;
            CheatResultTileId = cheatResultTileId;
            CheatHandTileId = cheatHandTileId;
            CheatTargetSlot = cheatTargetSlot;
        }

        public ProtocolEventType Type { get; }

        /// <summary>Policy-derived effective visibility; never the caller's claim.</summary>
        public ProtocolEventVisibility Visibility { get; }

        public EventSeq EventSeq { get; }

        public StateVersion CommandVersion { get; }

        public uint EventOrdinal { get; }

        public int Seat { get; }

        /// <summary>Only the identity fields this audience may see; empty for redacted events.</summary>
        public IReadOnlyList<uint> TileIds => _tileIds;

        public bool IsUraDora { get; }

        public bool IsClosedKan { get; }

        public bool IsChankan { get; }

        public long? CheatAmount { get; }

        public long? CheatBalance { get; }

        public string PublicReasonCode { get; }

        public string? CheatOfferId { get; }

        public bool? CheatKeptFirst { get; }

        public ProjectionBeamSwapResolution? CheatBeamSwapResolution { get; }

        public uint? CheatResultTileId { get; }

        public uint? CheatHandTileId { get; }

        public int? CheatTargetSlot { get; }

        /// <summary>
        /// A deterministic projection-body encoding for verification and the P3.2b adapter.
        /// It is intentionally NOT a Contracts.Wire protobuf and must not be transported as one.
        /// The body contains only fields already selected for this audience.
        /// </summary>
        public byte[] EncodeBody()
        {
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(BodyMagic);
                writer.Write(BodyVersion);
                byte flags = 0;
                if (IsUraDora)
                {
                    flags |= 1;
                }

                if (IsClosedKan)
                {
                    flags |= 2;
                }

                if (IsChankan)
                {
                    flags |= 4;
                }

                writer.Write(flags);
                writer.Write((byte)Type);
                writer.Write(unchecked((sbyte)Seat));
                writer.Write(EventOrdinal);
                writer.Write(CommandVersion.Value);
                writer.Write(EventSeq.Value);
                writer.Write((byte)_tileIds.Count);
                for (int i = 0; i < _tileIds.Count; i++)
                {
                    writer.Write((byte)_tileIds[i]);
                }

                byte cheatFlags = 0;
                if (CheatAmount.HasValue)
                {
                    cheatFlags |= 1;
                }

                if (CheatBalance.HasValue)
                {
                    cheatFlags |= 2;
                }

                writer.Write(cheatFlags);
                if (CheatAmount.HasValue)
                {
                    writer.Write(CheatAmount.Value);
                }

                if (CheatBalance.HasValue)
                {
                    writer.Write(CheatBalance.Value);
                }

                byte resolutionFlags = 0;
                if (CheatOfferId != null)
                {
                    resolutionFlags |= 1;
                }

                if (CheatKeptFirst.HasValue)
                {
                    resolutionFlags |= 2;
                }

                if (CheatBeamSwapResolution.HasValue)
                {
                    resolutionFlags |= 4;
                }

                if (CheatResultTileId.HasValue)
                {
                    resolutionFlags |= 8;
                }

                if (CheatHandTileId.HasValue)
                {
                    resolutionFlags |= 16;
                }

                if (CheatTargetSlot.HasValue)
                {
                    resolutionFlags |= 32;
                }

                writer.Write(resolutionFlags);
                if (CheatOfferId != null)
                {
                    writer.Write(CheatOfferId);
                }

                if (CheatKeptFirst.HasValue)
                {
                    writer.Write(CheatKeptFirst.Value);
                }

                if (CheatBeamSwapResolution.HasValue)
                {
                    writer.Write((byte)CheatBeamSwapResolution.Value);
                }

                if (CheatResultTileId.HasValue)
                {
                    writer.Write(CheatResultTileId.Value);
                }

                if (CheatHandTileId.HasValue)
                {
                    writer.Write(CheatHandTileId.Value);
                }

                if (CheatTargetSlot.HasValue)
                {
                    writer.Write(CheatTargetSlot.Value);
                }

                byte[] reason = Encoding.ASCII.GetBytes(PublicReasonCode);
                writer.Write((byte)reason.Length);
                writer.Write(reason);
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Metadata-only Contracts adapter. The body is intentionally empty in P3.2a: the
        /// typed <see cref="TableProjectedEvent"/> is the source for the P3.2b Wire mapper.
        /// Use <see cref="EncodeBody"/> to inspect the complete projected body.
        /// </summary>
        public ProtocolEventRecord ToProtocolEventRecord()
        {
            return ProtocolEventRecord.Known(
                Type,
                Visibility,
                ProtocolConstants.EventSchemaVersion,
                EventSeq,
                CommandVersion,
                EventOrdinal,
                Array.Empty<byte>());
        }
    }

    /// <summary>
    /// Pure P3.2a event policy. Each event type is classified explicitly; anything unknown is
    /// rejected rather than guessed public. Hidden events are omitted, so no public cursor gap
    /// is created by this component (cursor allocation belongs to P3.2b).
    /// </summary>
    public static class TableEventProjection
    {
        public static TableProjectedEvent? Project(
            TableEventProjectionInput input,
            TableProjectionAudience audience,
            TableProjectionPolicy policy)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            if (input.Type == ProtocolEventType.Unspecified ||
                !ProtocolEventPolicy.IsKnownEventType((uint)input.Type))
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.UnknownEventType,
                    "Event type " + (uint)input.Type + " is not a frozen V5 event type.");
            }

            if (!audience.IsSpectator)
            {
                TableProjectionCopy.Seat(audience.Seat, nameof(audience));
            }

            if (input.IsUraDora &&
                (!policy.SettlementBatch || !policy.RevealSettlementUra))
            {
                return null;
            }

            if (TableEventProjectionInput.IsPrivateType(input.Type))
            {
                if (audience.IsSpectator || audience.Seat != input.Seat)
                {
                    return null;
                }

                return Build(
                    input,
                    ProtocolEventVisibility.Private,
                    input.TileIds,
                    input.IsClosedKan,
                    input.CheatAmount,
                    input.CheatBalance,
                    input.CheatOfferId,
                    input.CheatKeptFirst,
                    input.CheatBeamSwapResolution,
                    input.CheatResultTileId,
                    input.CheatHandTileId,
                    input.CheatTargetSlot);
            }

            ProtocolEventVisibility visibility = BaseVisibility(input.Type);
            if (audience.IsSpectator)
            {
                if (visibility == ProtocolEventVisibility.SettlementPublic && !policy.SettlementBatch)
                {
                    return null;
                }

                IReadOnlyList<uint> publicTiles = RedactTiles(input, audience);
                return Build(
                    input,
                    input.IsUraDora ? ProtocolEventVisibility.SettlementPublic : visibility,
                    publicTiles,
                    input.IsClosedKan,
                    null,
                    null);
            }

            IReadOnlyList<uint> seatTiles = RedactTiles(input, audience);
            return Build(
                input,
                input.IsUraDora ? ProtocolEventVisibility.SettlementPublic : visibility,
                seatTiles,
                input.IsClosedKan,
                null,
                null);
        }

        public static IReadOnlyList<TableProjectedEvent> ProjectMany(
            IEnumerable<TableEventProjectionInput> inputs,
            TableProjectionAudience audience,
            TableProjectionPolicy policy)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            var result = new List<TableProjectedEvent>();
            foreach (TableEventProjectionInput input in inputs)
            {
                TableProjectedEvent? projected = Project(input, audience, policy);
                if (projected != null)
                {
                    result.Add(projected);
                }
            }

            return new ReadOnlyCollection<TableProjectedEvent>(result);
        }

        /// <summary>
        /// Contracts-facing convenience: null when the event is hidden from this audience.
        /// The record carries the policy-derived visibility and source versions; its payload
        /// is intentionally empty until the P3.2b Wire mapper consumes the typed event.
        /// </summary>
        public static ProtocolEventRecord? ProjectToContract(
            TableEventProjectionInput input,
            TableProjectionAudience audience,
            TableProjectionPolicy policy)
        {
            return Project(input, audience, policy)?.ToProtocolEventRecord();
        }

        public static IReadOnlyList<ProtocolEventRecord> ProjectManyToContract(
            IEnumerable<TableEventProjectionInput> inputs,
            TableProjectionAudience audience,
            TableProjectionPolicy policy)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            var result = new List<ProtocolEventRecord>();
            foreach (TableEventProjectionInput input in inputs)
            {
                ProtocolEventRecord? projected = ProjectToContract(input, audience, policy);
                if (projected != null)
                {
                    result.Add(projected);
                }
            }

            return new ReadOnlyCollection<ProtocolEventRecord>(result);
        }

        /// <summary>
        /// Explicit mapping of the current Domain round-event vocabulary. Events that have no
        /// frozen protocol type (round start, response-window internals, wall-exhausted marker)
        /// return null and are never guessed into a different public type.
        /// </summary>
        public static TableEventProjectionInput? FromRoundEvent(TableEventRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            if (record.Cheat != null)
            {
                return FromCheatEvent(
                    record.Cheat.Event,
                    record.Seq,
                    record.CommandVersion,
                    (uint)record.Ordinal,
                    record.Cheat);
            }

            switch (record.Type)
            {
                case RoundEventType.CheatApplied:
                    // The single bridge marker binds the command version/event ordinal for a
                    // cheat command; it has no frozen protocol type and must not be renamed.
                    return null;
                case RoundEventType.TilesDealt:
                    return new TableEventProjectionInput(
                        ProtocolEventType.DealingComplete,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat);
                case RoundEventType.DoraRevealed:
                    return new TableEventProjectionInput(
                        ProtocolEventType.DoraRevealed,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat,
                        new[] { RequireTile(record) });
                case RoundEventType.TileDrawn:
                    return new TableEventProjectionInput(
                        ProtocolEventType.TileDrawn,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat,
                        new[] { RequireTile(record) });
                case RoundEventType.RinshanDrawn:
                    return new TableEventProjectionInput(
                        ProtocolEventType.RinshanDraw,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat,
                        new[] { RequireTile(record) });
                case RoundEventType.TileDiscarded:
                    return new TableEventProjectionInput(
                        ProtocolEventType.TileDiscarded,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat,
                        new[] { RequireTile(record) });
                case RoundEventType.PonDeclared:
                    return new TableEventProjectionInput(
                        ProtocolEventType.PonDeclared,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat,
                        new[] { RequireTile(record) });
                case RoundEventType.ChiDeclared:
                    return new TableEventProjectionInput(
                        ProtocolEventType.ChiDeclared,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat,
                        new[] { RequireTile(record) });
                case RoundEventType.RiichiDeclared:
                    return new TableEventProjectionInput(
                        ProtocolEventType.RiichiDeclared,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat);
                case RoundEventType.KanDeclared:
                    return new TableEventProjectionInput(
                        ProtocolEventType.KanDeclared,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat,
                        new[] { RequireTile(record) });
                case RoundEventType.TsumoWin:
                    return new TableEventProjectionInput(
                        ProtocolEventType.TsumoWin,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat,
                        new[] { RequireTile(record) });
                case RoundEventType.RonWin:
                    return new TableEventProjectionInput(
                        ProtocolEventType.RonWin,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        record.Seat,
                        new[] { RequireTile(record) },
                        isChankan: string.Equals(record.Detail, "CHANKAN", StringComparison.Ordinal));
                case RoundEventType.ExhaustiveDraw:
                    return new TableEventProjectionInput(
                        ProtocolEventType.DrawGame,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        ProtocolConstants.NoSeat,
                        publicReasonCode: "EXHAUSTIVE_DRAW");
                case RoundEventType.AbortiveDraw:
                    return new TableEventProjectionInput(
                        ProtocolEventType.DrawGame,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        ProtocolConstants.NoSeat,
                        publicReasonCode: "ABORTIVE_DRAW");
                case RoundEventType.RoundEnded:
                    return new TableEventProjectionInput(
                        ProtocolEventType.RoundEnded,
                        record.Seq,
                        record.CommandVersion,
                        (uint)record.Ordinal,
                        ProtocolConstants.NoSeat);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Maps only cheat events that have a frozen protocol type. TileMark/offer-cleared
        /// notices remain owner snapshot state and are not renamed into an unrelated event.
        /// </summary>
        public static TableEventProjectionInput? FromCheatEvent(
            CheatEvent cheatEvent,
            EventSeq eventSeq,
            StateVersion commandVersion,
            uint eventOrdinal,
            TableCheatEventRecord? metadata = null)
        {
            if (cheatEvent == null)
            {
                throw new ArgumentNullException(nameof(cheatEvent));
            }

            switch (cheatEvent.Kind)
            {
                case CheatEventKind.CheatPointsChanged:
                    if (cheatEvent.NewBalance < 0)
                    {
                        throw new TableProjectionException(
                            TableProjectionErrorCode.InvalidPayload,
                            "CheatPointsChanged carried no resulting balance.");
                    }

                    return new TableEventProjectionInput(
                        ProtocolEventType.CheatPointsChanged,
                        eventSeq,
                        commandVersion,
                        eventOrdinal,
                        cheatEvent.Seat,
                        cheatAmount: cheatEvent.Amount,
                        cheatBalance: cheatEvent.NewBalance);
                case CheatEventKind.SleeveBloomResolved:
                    return new TableEventProjectionInput(
                        ProtocolEventType.SleeveBloomResolved,
                        eventSeq,
                        commandVersion,
                        eventOrdinal,
                        cheatEvent.Seat,
                        cheatOfferId: metadata?.OfferId ?? cheatEvent.OfferId,
                        cheatKeptFirst: metadata?.Keep.HasValue == true
                            ? metadata.Keep.Value == SleeveBloomKeep.First
                            : (bool?)null,
                        cheatResultTileId: metadata?.ResultTile.HasValue == true
                            ? unchecked((uint)metadata.ResultTile.Value.Value)
                            : (uint?)null);
                case CheatEventKind.BeamSwapResolved:
                    ProjectionBeamSwapResolution? resolution = metadata?.Action.HasValue == true
                        ? (metadata.Action.Value == BeamSwapAction.Swap
                            ? ProjectionBeamSwapResolution.Swap
                            : ProjectionBeamSwapResolution.KeepOriginal)
                        : (ProjectionBeamSwapResolution?)null;
                    return new TableEventProjectionInput(
                        ProtocolEventType.BeamSwapResolved,
                        eventSeq,
                        commandVersion,
                        eventOrdinal,
                        cheatEvent.Seat,
                        cheatOfferId: metadata?.OfferId ?? cheatEvent.OfferId,
                        cheatBeamSwapResolution: resolution,
                        cheatHandTileId: metadata?.HandTile.HasValue == true
                            ? unchecked((uint)metadata.HandTile.Value.Value)
                            : (uint?)null,
                        cheatTargetSlot: metadata?.TargetSlot.HasValue == true
                            ? metadata.TargetSlot.Value.Index
                            : (int?)null);
                default:
                    return null;
            }
        }

        private static ProtocolEventVisibility BaseVisibility(ProtocolEventType type)
        {
            switch (type)
            {
                case ProtocolEventType.StackingStarted:
                case ProtocolEventType.WallBuilt:
                case ProtocolEventType.DealingComplete:
                case ProtocolEventType.TileDiscarded:
                case ProtocolEventType.ChiDeclared:
                case ProtocolEventType.PonDeclared:
                case ProtocolEventType.KanDeclared:
                case ProtocolEventType.RiichiDeclared:
                case ProtocolEventType.DoraRevealed:
                case ProtocolEventType.DrawGame:
                case ProtocolEventType.ScoreSettled:
                case ProtocolEventType.RoundEnded:
                case ProtocolEventType.MatchEnded:
                    return ProtocolEventVisibility.Public;
                case ProtocolEventType.TsumoWin:
                case ProtocolEventType.RonWin:
                    return ProtocolEventVisibility.SettlementPublic;
                default:
                    throw new TableProjectionException(
                        TableProjectionErrorCode.UnsupportedEvent,
                        "Event type " + type + " has no public whitelist entry.");
            }
        }

        private static IReadOnlyList<uint> RedactTiles(TableEventProjectionInput input, TableProjectionAudience audience)
        {
            if (input.TileIds.Count == 0)
            {
                return Array.Empty<uint>();
            }

            // A closed kan is public as an event, but its four physical identities are owner-only.
            if (input.Type == ProtocolEventType.KanDeclared && input.IsClosedKan)
            {
                if (!audience.IsSpectator && audience.Seat == input.Seat)
                {
                    return input.TileIds;
                }

                return Array.Empty<uint>();
            }

            return input.TileIds;
        }

        private static TableProjectedEvent Build(
            TableEventProjectionInput input,
            ProtocolEventVisibility visibility,
            IReadOnlyList<uint> tileIds,
            bool isClosedKan,
            long? cheatAmount,
            long? cheatBalance,
            string? cheatOfferId = null,
            bool? cheatKeptFirst = null,
            ProjectionBeamSwapResolution? cheatBeamSwapResolution = null,
            uint? cheatResultTileId = null,
            uint? cheatHandTileId = null,
            int? cheatTargetSlot = null)
        {
            return new TableProjectedEvent(
                input.Type,
                ProtocolEventPolicy.EffectiveVisibility(visibility),
                input.EventSeq,
                input.CommandVersion,
                input.EventOrdinal,
                input.Seat,
                tileIds,
                input.IsUraDora,
                isClosedKan,
                input.IsChankan,
                cheatAmount,
                cheatBalance,
                input.PublicReasonCode,
                cheatOfferId,
                cheatKeptFirst,
                cheatBeamSwapResolution,
                cheatResultTileId,
                cheatHandTileId,
                cheatTargetSlot);
        }

        private static uint RequireTile(TableEventRecord record)
        {
            if (!record.Tile.HasValue)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidPayload,
                    "Round event " + record.Type + " requires a source tile id.");
            }

            return unchecked((uint)record.Tile.Value.Value);
        }
    }
}
