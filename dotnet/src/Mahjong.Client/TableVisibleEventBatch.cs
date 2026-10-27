using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using ContractBreakdown = Mahjong.Contracts.ScoreBreakdownView;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using ContractMeld = Mahjong.Contracts.MeldView;
using ContractPayment = Mahjong.Contracts.PaymentView;
using ContractScore = Mahjong.Contracts.ScoreView;
using ContractYaku = Mahjong.Contracts.YakuView;
using WireBreakdown = Mahjong.Contracts.Wire.ScoreBreakdownView;
using WireMeld = Mahjong.Contracts.Wire.MeldView;
using WirePayment = Mahjong.Contracts.Wire.PaymentView;
using WireScore = Mahjong.Contracts.Wire.ScoreView;
using WireYaku = Mahjong.Contracts.Wire.YakuView;

namespace Mahjong.Client
{
    /// <summary>
    /// How an accepted snapshot publication relates to the visible-event history.
    ///
    /// <see cref="Baseline"/> is an accepted full snapshot/reset. Its events, if
    /// any, belong to the exact accepted publication; they are not a replay of
    /// an earlier batch and must not be treated as newly actionable animation.
    /// <see cref="Incremental"/> is an accepted ordered stream advance and is the
    /// only batch kind that may feed live visible-event animation.
    /// </summary>
    public enum TableVisibleEventBatchKind
    {
        None = 0,
        Baseline = 1,
        Incremental = 2
    }

    /// <summary>
    /// Immutable, typed visible event owned by this client. It copies the wire
    /// envelope and its typed Contracts projection body; it never exposes a raw
    /// Envelope, an opaque payload, JSON or shared mutable wire state.
    /// </summary>
    public sealed class TableVisibleEvent
    {
        private readonly ReadOnlyCollection<uint> _tileIds;

        internal TableVisibleEvent(
            uint eventSchemaVersion,
            ulong eventSeq,
            ulong commandVersion,
            uint eventOrdinal,
            ProtocolEventVisibility visibility,
            int ownerSeat,
            ProjectedEventPayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            EventSchemaVersion = eventSchemaVersion;
            EventSeq = eventSeq;
            CommandVersion = commandVersion;
            EventOrdinal = eventOrdinal;
            Visibility = visibility;
            OwnerSeat = ownerSeat;
            Payload = payload;
            var tileCopy = new uint[payload.TileIds.Count];
            for (int i = 0; i < payload.TileIds.Count; i++)
            {
                tileCopy[i] = payload.TileIds[i];
            }

            _tileIds = Array.AsReadOnly(tileCopy);
        }

        public uint EventSchemaVersion { get; }

        public ulong EventSeq { get; }

        /// <summary>State version of the accepted command that produced this event.</summary>
        public ulong CommandVersion { get; }

        /// <summary>0..N-1 index inside the command identified by <see cref="CommandVersion"/>.</summary>
        public uint EventOrdinal { get; }

        public ProtocolEventType Type => Payload.Type;

        public ProtocolEventVisibility Visibility { get; }

        /// <summary>
        /// Owner seat for a private event; -1 for a public event. A private
        /// event can never be delivered with another seat's owner identity.
        /// </summary>
        public int OwnerSeat { get; }

        /// <summary>Acting seat when the event owns one; -1 otherwise.</summary>
        public int Seat => Payload.Seat;

        public uint TurnNumber => Payload.TurnNumber;

        public IReadOnlyList<uint> TileIds => _tileIds;

        /// <summary>
        /// Primary physical tile carried by the event, when it has one. Meld
        /// events yield their first physical tile; spread/settlement events
        /// return null. This is a convenience only; the full body is in
        /// <see cref="Payload"/>.
        /// </summary>
        public uint? TileId
        {
            get
            {
                if (_tileIds.Count > 0)
                {
                    return _tileIds[0];
                }

                if (Payload.DiscardTileId.HasValue)
                {
                    return Payload.DiscardTileId.Value;
                }

                if (Payload.ResultTileId.HasValue)
                {
                    return Payload.ResultTileId.Value;
                }

                if (Payload.HandTileId.HasValue)
                {
                    return Payload.HandTileId.Value;
                }

                return null;
            }
        }

        public bool IsPrivate => Visibility == ProtocolEventVisibility.Private;

        public bool IsSettlementPublic => Visibility == ProtocolEventVisibility.SettlementPublic;

        /// <summary>Immutable typed body; never a wire message or opaque payload.</summary>
        public ProjectedEventPayload Payload { get; }
    }

    /// <summary>
    /// One atomic, already-confirmed visible-event batch. Its identity is the
    /// accepted snapshot/cursor that produced it: session, room, role, seat,
    /// scope, table, binding/connection/owner epochs, round, state version,
    /// event high watermark and stream cursor are copied from that same
    /// acceptance, never from a later mutable field.
    ///
    /// The list is a defensive, read-only copy, so a subscriber cannot mutate
    /// the session cache or any other subscriber's confirmed data.
    /// </summary>
    public sealed class TableVisibleEventBatch
    {
        private static readonly TableVisibleEvent[] NoEventArray = Array.Empty<TableVisibleEvent>();
        private static readonly ReadOnlyCollection<TableVisibleEvent> NoEvents =
            new ReadOnlyCollection<TableVisibleEvent>(NoEventArray);

        private readonly ReadOnlyCollection<TableVisibleEvent> _events;

        private TableVisibleEventBatch()
        {
            Kind = TableVisibleEventBatchKind.None;
            SessionId = string.Empty;
            RoomId = string.Empty;
            TableId = string.Empty;
            RoundId = string.Empty;
            ScopeId = string.Empty;
            Role = ProtocolRole.Unspecified;
            Seat = ProtocolConstants.NoSeat;
            Cursor = ContractCursor.Empty;
            _events = NoEvents;
            PlayableEvents = NoEvents;
        }

        private TableVisibleEventBatch(
            TableSessionBinding binding,
            ContractCursor cursor,
            bool isFullBaseline,
            ulong stateVersion,
            ulong eventSeq,
            string roundId,
            List<TableVisibleEvent> events)
        {
            if (binding == null)
            {
                throw new ArgumentNullException(nameof(binding));
            }

            Kind = isFullBaseline ? TableVisibleEventBatchKind.Baseline : TableVisibleEventBatchKind.Incremental;
            SessionId = binding.SessionId.Value;
            RoomId = binding.RoomId.Value;
            TableId = binding.TableId ?? string.Empty;
            RoundId = roundId ?? string.Empty;
            ScopeId = binding.Scope.ScopeId.Value;
            Role = binding.Role;
            Seat = binding.SeatId;
            BindingEpoch = binding.BindingEpoch.Value;
            ConnectionEpoch = binding.ConnectionEpoch.Value;
            OwnerEpoch = binding.OwnerEpoch.Value;
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            Cursor = cursor;
            IsFullBaseline = isFullBaseline;
            _events = new ReadOnlyCollection<TableVisibleEvent>(events);
            PlayableEvents = isFullBaseline ? NoEvents : _events;
        }

        /// <summary>Empty sentinel used only by the legacy constructor/fake sessions.</summary>
        public static TableVisibleEventBatch Empty { get; } = new TableVisibleEventBatch();

        public TableVisibleEventBatchKind Kind { get; }

        public bool IsFullBaseline { get; }

        public bool HasEvents => _events.Count > 0;

        public int Count => _events.Count;

        public string SessionId { get; }

        public string RoomId { get; }

        public string TableId { get; }

        public string RoundId { get; }

        public string ScopeId { get; }

        public ProtocolRole Role { get; }

        public int Seat { get; }

        public ulong BindingEpoch { get; }

        public ulong ConnectionEpoch { get; }

        public ulong OwnerEpoch { get; }

        /// <summary>Authoritative table state version of the accepted snapshot.</summary>
        public ulong StateVersion { get; }

        /// <summary>Authoritative event-log high watermark of the accepted snapshot.</summary>
        public ulong EventSeq { get; }

        public ContractCursor Cursor { get; }

        public ulong StreamVersion => Cursor.StreamVersion.Value;

        public ulong ProjectionSeq => Cursor.ProjectionSeq.Value;

        /// <summary>
        /// Every typed event delivered by the exact accepted frame. On a
        /// baseline this is reconciliation data, not an incremental action.
        /// </summary>
        public IReadOnlyList<TableVisibleEvent> Events => _events;

        /// <summary>
        /// Incremental events only. It is deliberately empty for a baseline or
        /// the legacy empty batch, so a B2b consumer cannot replay old history
        /// as a fresh action by using this entry point.
        /// </summary>
        public IReadOnlyList<TableVisibleEvent> PlayableEvents { get; }

        internal static TableVisibleEventBatch ProjectPlayer(
            TableSessionBinding binding,
            ContractCursor cursor,
            bool isFullBaseline,
            ulong stateVersion,
            ulong eventSeq,
            string roundId,
            IReadOnlyList<EventEnvelope> wireEvents,
            TableVisibleEventFence fence)
        {
            if (binding == null)
            {
                throw new ArgumentNullException(nameof(binding));
            }

            if (wireEvents == null)
            {
                throw new ArgumentNullException(nameof(wireEvents));
            }

            if (binding.Role != ProtocolRole.Player)
            {
                throw Invalid(ProtocolErrorCode.PrivateDataInPublicStream, "A player visible-event batch requires a player binding.");
            }

            var events = new List<TableVisibleEvent>(wireEvents.Count);
            ulong lastSeq = 0;
            uint lastOrdinal = 0;
            bool hasLast = false;
            for (int i = 0; i < wireEvents.Count; i++)
            {
                EventEnvelope wire = wireEvents[i];
                if (wire == null)
                {
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "A player update contains a null visible event.");
                }

                ValidateCommon(wire.EventSchemaVersion, wire.EventSeq, wire.CommandVersion, wire.EventType, stateVersion, eventSeq);
                ProtocolEventVisibility visibility = MapVisibility(wire.Visibility);
                bool isPrivate = ProtocolEventPolicy.IsPrivateEventType((ProtocolEventType)(uint)wire.EventType);
                int ownerSeat = ValidatePlayerAudience(wire, binding, isPrivate, visibility);

                ProjectedEventPayload payload;
                try
                {
                    payload = MapPlayerPayload(wire);
                }
                catch (TableVisibleEventProjectionException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is ProjectionPayloadException)
                {
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible event body is not a valid typed projection: " + ex.Message);
                }

                if (payload.Type != (ProtocolEventType)(uint)wire.EventType)
                {
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible event type and body disagree.");
                }

                if (hasLast && wire.EventSeq < lastSeq)
                {
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible events are not ordered by event_seq.");
                }

                if (hasLast && wire.EventSeq == lastSeq)
                {
                    if (wire.EventOrdinal == lastOrdinal)
                    {
                        // A repeated event inside one frame is the same
                        // confirmed fact; never notify twice.
                        continue;
                    }

                    if (wire.EventOrdinal < lastOrdinal)
                    {
                        throw Invalid(
                            ProtocolErrorCode.MalformedMessage,
                            "Visible event ordinals regress inside one command batch.");
                    }
                }

                if (fence.IsStale(wire.EventSeq, wire.EventOrdinal))
                {
                    // An already-delivered event may be repeated by a refresh
                    // frame; the snapshot advances, the visible-event history
                    // does not become new again.
                    continue;
                }

                events.Add(new TableVisibleEvent(
                    wire.EventSchemaVersion,
                    wire.EventSeq,
                    wire.CommandVersion,
                    wire.EventOrdinal,
                    visibility,
                    ownerSeat,
                    payload));
                lastSeq = wire.EventSeq;
                lastOrdinal = wire.EventOrdinal;
                hasLast = true;
            }

            return new TableVisibleEventBatch(binding, cursor, isFullBaseline, stateVersion, eventSeq, roundId, events);
        }

        internal static TableVisibleEventBatch ProjectSpectator(
            TableSessionBinding binding,
            ContractCursor cursor,
            bool isFullBaseline,
            ulong stateVersion,
            ulong eventSeq,
            string roundId,
            IReadOnlyList<PublicEventEnvelope> wireEvents,
            TableVisibleEventFence fence)
        {
            if (binding == null)
            {
                throw new ArgumentNullException(nameof(binding));
            }

            if (wireEvents == null)
            {
                throw new ArgumentNullException(nameof(wireEvents));
            }

            if (binding.Role != ProtocolRole.Spectator)
            {
                throw Invalid(ProtocolErrorCode.PrivateDataInPublicStream, "A spectator visible-event batch requires a spectator binding.");
            }

            var events = new List<TableVisibleEvent>(wireEvents.Count);
            ulong lastSeq = 0;
            uint lastOrdinal = 0;
            bool hasLast = false;
            for (int i = 0; i < wireEvents.Count; i++)
            {
                PublicEventEnvelope wire = wireEvents[i];
                if (wire == null)
                {
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "A public update contains a null visible event.");
                }

                ValidateCommon(wire.EventSchemaVersion, wire.EventSeq, wire.CommandVersion, wire.EventType, stateVersion, eventSeq);
                ProtocolEventType declared = (ProtocolEventType)(uint)wire.EventType;
                if (ProtocolEventPolicy.IsPrivateEventType(declared))
                {
                    throw Invalid(ProtocolErrorCode.PrivateDataInPublicStream, "A private event type cannot enter a spectator stream.");
                }

                ProtocolEventVisibility visibility = MapVisibility(wire.Visibility);
                if (visibility != ProtocolEventVisibility.Public && visibility != ProtocolEventVisibility.SettlementPublic)
                {
                    throw Invalid(ProtocolErrorCode.PrivateDataInPublicStream, "A public event must be explicitly public or settlement-public.");
                }

                if ((declared == ProtocolEventType.TsumoWin || declared == ProtocolEventType.RonWin) &&
                    visibility != ProtocolEventVisibility.SettlementPublic)
                {
                    throw Invalid(ProtocolErrorCode.PrivateDataInPublicStream, "A win event requires settlement-public visibility.");
                }

                ProjectedEventPayload payload;
                try
                {
                    payload = MapPublicPayload(wire);
                }
                catch (TableVisibleEventProjectionException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is ProjectionPayloadException)
                {
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Public visible event body is not a valid typed projection: " + ex.Message);
                }

                if (payload.Type != declared)
                {
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Public visible event type and body disagree.");
                }

                if (hasLast && wire.EventSeq < lastSeq)
                {
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Public visible events are not ordered by event_seq.");
                }

                if (hasLast && wire.EventSeq == lastSeq)
                {
                    if (wire.EventOrdinal == lastOrdinal)
                    {
                        continue;
                    }

                    if (wire.EventOrdinal < lastOrdinal)
                    {
                        throw Invalid(
                            ProtocolErrorCode.MalformedMessage,
                            "Public visible event ordinals regress inside one command batch.");
                    }
                }

                if (fence.IsStale(wire.EventSeq, wire.EventOrdinal))
                {
                    continue;
                }

                events.Add(new TableVisibleEvent(
                    wire.EventSchemaVersion,
                    wire.EventSeq,
                    wire.CommandVersion,
                    wire.EventOrdinal,
                    visibility,
                    ProtocolConstants.NoSeat,
                    payload));
                lastSeq = wire.EventSeq;
                lastOrdinal = wire.EventOrdinal;
                hasLast = true;
            }

            return new TableVisibleEventBatch(binding, cursor, isFullBaseline, stateVersion, eventSeq, roundId, events);
        }

        internal static TableVisibleEventBatch Baseline(
            TableSessionBinding binding,
            ContractCursor cursor,
            ulong stateVersion,
            ulong eventSeq,
            string roundId)
        {
            return new TableVisibleEventBatch(
                binding,
                cursor,
                true,
                stateVersion,
                eventSeq,
                roundId,
                new List<TableVisibleEvent>());
        }

        private static void ValidateCommon(
            uint eventSchemaVersion,
            ulong eventSeq,
            ulong commandVersion,
            EventType eventType,
            ulong stateVersion,
            ulong snapshotEventSeq)
        {
            if (eventSchemaVersion != ProtocolConstants.EventSchemaVersion)
            {
                throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible event schema version is not the frozen version.");
            }

            if (eventSeq == 0)
            {
                throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible event sequence must be positive.");
            }

            if (commandVersion == 0)
            {
                throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible event command version must be positive.");
            }

            if (!ProtocolEventPolicy.IsKnownEventType((uint)eventType))
            {
                throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible event type is unknown.");
            }

            if (commandVersion > stateVersion)
            {
                throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible event command version exceeds the accepted snapshot state version.");
            }

            if (eventSeq > snapshotEventSeq)
            {
                throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible event sequence exceeds the accepted snapshot event watermark.");
            }
        }

        private static int ValidatePlayerAudience(
            EventEnvelope wire,
            TableSessionBinding binding,
            bool isPrivate,
            ProtocolEventVisibility visibility)
        {
            if (isPrivate)
            {
                if (visibility != ProtocolEventVisibility.Private)
                {
                    throw Invalid(ProtocolErrorCode.PrivateDataInPublicStream, "A private event must carry private visibility.");
                }

                int ownerSeat = PrivateOwnerSeat(wire);
                if (ownerSeat != binding.SeatId)
                {
                    throw Invalid(
                        ProtocolErrorCode.PrivateDataInPublicStream,
                        "Private event owner seat " + ownerSeat + " does not match bound seat " + binding.SeatId + ".");
                }

                return ownerSeat;
            }

            if (visibility != ProtocolEventVisibility.Public &&
                visibility != ProtocolEventVisibility.SettlementPublic)
            {
                throw Invalid(ProtocolErrorCode.PrivateDataInPublicStream, "A public event must be explicitly public or settlement-public.");
            }

            ProtocolEventType type = (ProtocolEventType)(uint)wire.EventType;
            if ((type == ProtocolEventType.TsumoWin || type == ProtocolEventType.RonWin) &&
                visibility != ProtocolEventVisibility.SettlementPublic)
            {
                throw Invalid(ProtocolErrorCode.PrivateDataInPublicStream, "A win event requires settlement-public visibility.");
            }

            if (type == ProtocolEventType.KanDeclared &&
                wire.KanDeclared.KanType == KanType.Closed &&
                wire.KanDeclared.Meld != null &&
                wire.KanDeclared.Meld.TileIds.Count > 0 &&
                wire.KanDeclared.Player != binding.SeatId)
            {
                throw Invalid(
                    ProtocolErrorCode.PrivateDataInPublicStream,
                    "Closed-kan identities may be delivered only to their owner seat.");
            }

            return ProtocolConstants.NoSeat;
        }

        private static int PrivateOwnerSeat(EventEnvelope wire)
        {
            switch (wire.EventCase)
            {
                case EventEnvelope.EventOneofCase.TileDealt:
                    return wire.TileDealt.Player;
                case EventEnvelope.EventOneofCase.TileDrawn:
                    return wire.TileDrawn.Player;
                case EventEnvelope.EventOneofCase.RinshanDraw:
                    return wire.RinshanDraw.Player;
                case EventEnvelope.EventOneofCase.CheatPointsChanged:
                    return wire.CheatPointsChanged.Seat;
                case EventEnvelope.EventOneofCase.SleeveBloomResolved:
                    return wire.SleeveBloomResolved.HasOwnerSeat
                        ? wire.SleeveBloomResolved.OwnerSeat
                        : ProtocolConstants.NoSeat;
                case EventEnvelope.EventOneofCase.BeamSwapResolved:
                    return wire.BeamSwapResolved.HasOwnerSeat
                        ? wire.BeamSwapResolved.OwnerSeat
                        : ProtocolConstants.NoSeat;
                default:
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "A private event has no owner-bearing typed body.");
            }
        }

        private static ProtocolEventVisibility MapVisibility(EventVisibility visibility)
        {
            switch (visibility)
            {
                case EventVisibility.Private:
                    return ProtocolEventVisibility.Private;
                case EventVisibility.Public:
                    return ProtocolEventVisibility.Public;
                case EventVisibility.SettlementPublic:
                    return ProtocolEventVisibility.SettlementPublic;
                default:
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Visible event visibility is unspecified or unknown.");
            }
        }

        private static ProjectedEventPayload MapPlayerPayload(EventEnvelope wire)
        {
            switch (wire.EventCase)
            {
                case EventEnvelope.EventOneofCase.StackingStarted:
                    return ProjectedEventPayload.StackingStarted(wire.StackingStarted.TurnNumber);
                case EventEnvelope.EventOneofCase.WallBuilt:
                    return ProjectedEventPayload.WallBuilt(wire.WallBuilt.TurnNumber, wire.WallBuilt.WallSize);
                case EventEnvelope.EventOneofCase.TileDealt:
                    return ProjectedEventPayload.TileDealt(wire.TileDealt.TurnNumber, wire.TileDealt.Player, wire.TileDealt.TileId);
                case EventEnvelope.EventOneofCase.DealingComplete:
                    return ProjectedEventPayload.DealingComplete(wire.DealingComplete.TurnNumber);
                case EventEnvelope.EventOneofCase.TileDrawn:
                    return ProjectedEventPayload.TileDrawn(
                        wire.TileDrawn.TurnNumber,
                        wire.TileDrawn.Player,
                        wire.TileDrawn.TileId,
                        wire.TileDrawn.WallRemaining);
                case EventEnvelope.EventOneofCase.TileDiscarded:
                    return ProjectedEventPayload.TileDiscarded(
                        wire.TileDiscarded.TurnNumber,
                        wire.TileDiscarded.Player,
                        wire.TileDiscarded.TileId,
                        wire.TileDiscarded.RiichiDiscard,
                        wire.TileDiscarded.Tsumogiri);
                case EventEnvelope.EventOneofCase.ChiDeclared:
                    return ProjectedEventPayload.ChiDeclared(
                        wire.ChiDeclared.TurnNumber,
                        wire.ChiDeclared.Player,
                        MapMeld(wire.ChiDeclared.Meld),
                        wire.ChiDeclared.FromPlayer);
                case EventEnvelope.EventOneofCase.PonDeclared:
                    return ProjectedEventPayload.PonDeclared(
                        wire.PonDeclared.TurnNumber,
                        wire.PonDeclared.Player,
                        MapMeld(wire.PonDeclared.Meld),
                        wire.PonDeclared.FromPlayer);
                case EventEnvelope.EventOneofCase.KanDeclared:
                    return ProjectedEventPayload.KanDeclared(
                        wire.KanDeclared.TurnNumber,
                        wire.KanDeclared.Player,
                        wire.KanDeclared.Meld == null ? null : MapMeld(wire.KanDeclared.Meld),
                        MapKanType(wire.KanDeclared.KanType));
                case EventEnvelope.EventOneofCase.RinshanDraw:
                    return ProjectedEventPayload.RinshanDraw(
                        wire.RinshanDraw.TurnNumber,
                        wire.RinshanDraw.Player,
                        wire.RinshanDraw.TileId);
                case EventEnvelope.EventOneofCase.RiichiDeclared:
                    return ProjectedEventPayload.RiichiDeclared(
                        wire.RiichiDeclared.TurnNumber,
                        wire.RiichiDeclared.Player,
                        wire.RiichiDeclared.DiscardTileId,
                        wire.RiichiDeclared.DoubleRiichi);
                case EventEnvelope.EventOneofCase.TsumoWin:
                    return ProjectedEventPayload.TsumoWin(
                        wire.TsumoWin.TurnNumber,
                        wire.TsumoWin.Player,
                        wire.TsumoWin.WinTileId,
                        MapYaku(wire.TsumoWin.Yaku),
                        wire.TsumoWin.Han,
                        wire.TsumoWin.Fu,
                        wire.TsumoWin.Score,
                        wire.TsumoWin.Breakdown == null ? null : MapBreakdown(wire.TsumoWin.Breakdown));
                case EventEnvelope.EventOneofCase.RonWin:
                    return ProjectedEventPayload.RonWin(
                        wire.RonWin.TurnNumber,
                        wire.RonWin.Winner,
                        wire.RonWin.Loser,
                        wire.RonWin.WinTileId,
                        MapYaku(wire.RonWin.Yaku),
                        wire.RonWin.Han,
                        wire.RonWin.Fu,
                        wire.RonWin.Score,
                        wire.RonWin.Breakdown == null ? null : MapBreakdown(wire.RonWin.Breakdown),
                        wire.RonWin.IsChankan);
                case EventEnvelope.EventOneofCase.DoraRevealed:
                    return ProjectedEventPayload.DoraRevealed(
                        wire.DoraRevealed.TurnNumber,
                        wire.DoraRevealed.IndicatorTileId,
                        wire.DoraRevealed.IsUraDora,
                        wire.DoraRevealed.UraAuthorized);
                case EventEnvelope.EventOneofCase.CheatPointsChanged:
                    return ProjectedEventPayload.CheatPointsChanged(
                        wire.CheatPointsChanged.TurnNumber,
                        wire.CheatPointsChanged.Seat,
                        wire.CheatPointsChanged.Amount,
                        wire.CheatPointsChanged.NewBalance);
                case EventEnvelope.EventOneofCase.DrawGame:
                    return ProjectedEventPayload.DrawGame(
                        wire.DrawGame.TurnNumber,
                        wire.DrawGame.Reason,
                        CopyUIntsAsSeats(wire.DrawGame.TenpaiPlayers));
                case EventEnvelope.EventOneofCase.ScoreSettled:
                    return ProjectedEventPayload.ScoreSettled(
                        wire.ScoreSettled.TurnNumber,
                        MapPayments(wire.ScoreSettled.Payments),
                        MapScores(wire.ScoreSettled.NewScores));
                case EventEnvelope.EventOneofCase.RoundEnded:
                    return ProjectedEventPayload.RoundEnded(
                        wire.RoundEnded.TurnNumber,
                        wire.RoundEnded.RoundWind,
                        wire.RoundEnded.RoundNumber);
                case EventEnvelope.EventOneofCase.MatchEnded:
                    return ProjectedEventPayload.MatchEnded(
                        wire.MatchEnded.TurnNumber,
                        MapScores(wire.MatchEnded.FinalScores),
                        wire.MatchEnded.Winner);
                case EventEnvelope.EventOneofCase.SleeveBloomResolved:
                    if (!wire.SleeveBloomResolved.HasOwnerSeat)
                    {
                        throw Invalid(ProtocolErrorCode.MalformedMessage, "SleeveBloomResolved is missing owner provenance.");
                    }

                    return ProjectedEventPayload.SleeveBloomResolved(
                        wire.SleeveBloomResolved.OwnerSeat,
                        wire.SleeveBloomResolved.OfferId,
                        wire.SleeveBloomResolved.KeptFirst,
                        wire.SleeveBloomResolved.HasResultTileId ? wire.SleeveBloomResolved.ResultTileId : (uint?)null);
                case EventEnvelope.EventOneofCase.BeamSwapResolved:
                    if (!wire.BeamSwapResolved.HasOwnerSeat)
                    {
                        throw Invalid(ProtocolErrorCode.MalformedMessage, "BeamSwapResolved is missing owner provenance.");
                    }

                    return ProjectedEventPayload.BeamSwapResolved(
                        wire.BeamSwapResolved.OwnerSeat,
                        wire.BeamSwapResolved.OfferId,
                        MapBeamResolution(wire.BeamSwapResolved.Action),
                        wire.BeamSwapResolved.HasHandTileId ? wire.BeamSwapResolved.HandTileId : (uint?)null,
                        wire.BeamSwapResolved.HasTargetSlot ? wire.BeamSwapResolved.TargetSlot : (uint?)null);
                default:
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Player visible event has no typed body.");
            }
        }

        private static ProjectedEventPayload MapPublicPayload(PublicEventEnvelope wire)
        {
            switch (wire.EventCase)
            {
                case PublicEventEnvelope.EventOneofCase.StackingStarted:
                    return ProjectedEventPayload.StackingStarted(wire.StackingStarted.TurnNumber);
                case PublicEventEnvelope.EventOneofCase.WallBuilt:
                    return ProjectedEventPayload.WallBuilt(wire.WallBuilt.TurnNumber, wire.WallBuilt.WallSize);
                case PublicEventEnvelope.EventOneofCase.DealingComplete:
                    return ProjectedEventPayload.DealingComplete(wire.DealingComplete.TurnNumber);
                case PublicEventEnvelope.EventOneofCase.TileDiscarded:
                    return ProjectedEventPayload.TileDiscarded(
                        wire.TileDiscarded.TurnNumber,
                        wire.TileDiscarded.Player,
                        wire.TileDiscarded.TileId,
                        wire.TileDiscarded.RiichiDiscard,
                        wire.TileDiscarded.Tsumogiri);
                case PublicEventEnvelope.EventOneofCase.ChiDeclared:
                    return ProjectedEventPayload.ChiDeclared(
                        wire.ChiDeclared.TurnNumber,
                        wire.ChiDeclared.Player,
                        MapMeld(wire.ChiDeclared.Meld),
                        wire.ChiDeclared.FromPlayer);
                case PublicEventEnvelope.EventOneofCase.PonDeclared:
                    return ProjectedEventPayload.PonDeclared(
                        wire.PonDeclared.TurnNumber,
                        wire.PonDeclared.Player,
                        MapMeld(wire.PonDeclared.Meld),
                        wire.PonDeclared.FromPlayer);
                case PublicEventEnvelope.EventOneofCase.KanDeclared:
                    return ProjectedEventPayload.KanDeclared(
                        wire.KanDeclared.TurnNumber,
                        wire.KanDeclared.Player,
                        wire.KanDeclared.Meld == null ? null : MapMeld(wire.KanDeclared.Meld),
                        MapKanType(wire.KanDeclared.KanType));
                case PublicEventEnvelope.EventOneofCase.RiichiDeclared:
                    return ProjectedEventPayload.RiichiDeclared(
                        wire.RiichiDeclared.TurnNumber,
                        wire.RiichiDeclared.Player,
                        wire.RiichiDeclared.DiscardTileId,
                        wire.RiichiDeclared.DoubleRiichi);
                case PublicEventEnvelope.EventOneofCase.TsumoWin:
                    return ProjectedEventPayload.TsumoWin(
                        wire.TsumoWin.TurnNumber,
                        wire.TsumoWin.Player,
                        wire.TsumoWin.WinTileId,
                        MapYaku(wire.TsumoWin.Yaku),
                        wire.TsumoWin.Han,
                        wire.TsumoWin.Fu,
                        wire.TsumoWin.Score,
                        wire.TsumoWin.Breakdown == null ? null : MapBreakdown(wire.TsumoWin.Breakdown));
                case PublicEventEnvelope.EventOneofCase.RonWin:
                    return ProjectedEventPayload.RonWin(
                        wire.RonWin.TurnNumber,
                        wire.RonWin.Winner,
                        wire.RonWin.Loser,
                        wire.RonWin.WinTileId,
                        MapYaku(wire.RonWin.Yaku),
                        wire.RonWin.Han,
                        wire.RonWin.Fu,
                        wire.RonWin.Score,
                        wire.RonWin.Breakdown == null ? null : MapBreakdown(wire.RonWin.Breakdown),
                        wire.RonWin.IsChankan);
                case PublicEventEnvelope.EventOneofCase.DoraRevealed:
                    return ProjectedEventPayload.DoraRevealed(
                        wire.DoraRevealed.TurnNumber,
                        wire.DoraRevealed.IndicatorTileId,
                        wire.DoraRevealed.IsUraDora,
                        wire.DoraRevealed.UraAuthorized);
                case PublicEventEnvelope.EventOneofCase.DrawGame:
                    return ProjectedEventPayload.DrawGame(
                        wire.DrawGame.TurnNumber,
                        wire.DrawGame.Reason,
                        CopyUIntsAsSeats(wire.DrawGame.TenpaiPlayers));
                case PublicEventEnvelope.EventOneofCase.ScoreSettled:
                    return ProjectedEventPayload.ScoreSettled(
                        wire.ScoreSettled.TurnNumber,
                        MapPayments(wire.ScoreSettled.Payments),
                        MapScores(wire.ScoreSettled.NewScores));
                case PublicEventEnvelope.EventOneofCase.RoundEnded:
                    return ProjectedEventPayload.RoundEnded(
                        wire.RoundEnded.TurnNumber,
                        wire.RoundEnded.RoundWind,
                        wire.RoundEnded.RoundNumber);
                case PublicEventEnvelope.EventOneofCase.MatchEnded:
                    return ProjectedEventPayload.MatchEnded(
                        wire.MatchEnded.TurnNumber,
                        MapScores(wire.MatchEnded.FinalScores),
                        wire.MatchEnded.Winner);
                default:
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Public visible event has no typed body.");
            }
        }

        private static ContractMeld MapMeld(WireMeld wire)
        {
            if (wire == null)
            {
                throw Invalid(ProtocolErrorCode.MalformedMessage, "Meld body is missing.");
            }

            var tiles = new List<uint>(wire.TileIds.Count);
            for (int i = 0; i < wire.TileIds.Count; i++)
            {
                tiles.Add(wire.TileIds[i]);
            }

            return new ContractMeld(
                MapMeldType(wire.Type),
                tiles,
                wire.HasCalledTileId ? wire.CalledTileId : (uint?)null,
                wire.FromSeat);
        }

        private static ProtocolMeldType MapMeldType(MeldType type)
        {
            switch (type)
            {
                case MeldType.Chi:
                    return ProtocolMeldType.Chi;
                case MeldType.Pon:
                    return ProtocolMeldType.Pon;
                case MeldType.OpenKan:
                    return ProtocolMeldType.OpenKan;
                case MeldType.ClosedKan:
                    return ProtocolMeldType.ClosedKan;
                case MeldType.AddedKan:
                    return ProtocolMeldType.AddedKan;
                default:
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Meld type is unspecified or unknown.");
            }
        }

        private static ProtocolMeldType MapKanType(KanType type)
        {
            switch (type)
            {
                case KanType.Closed:
                    return ProtocolMeldType.ClosedKan;
                case KanType.Added:
                    return ProtocolMeldType.AddedKan;
                case KanType.Open:
                    return ProtocolMeldType.OpenKan;
                default:
                    throw Invalid(ProtocolErrorCode.MalformedMessage, "Kan type is unspecified or unknown.");
            }
        }

        private static ProjectionBeamSwapResolution MapBeamResolution(string action)
        {
            if (string.Equals(action, "SWAP", StringComparison.Ordinal))
            {
                return ProjectionBeamSwapResolution.Swap;
            }

            if (string.Equals(action, "KEEP_ORIGINAL", StringComparison.Ordinal))
            {
                return ProjectionBeamSwapResolution.KeepOriginal;
            }

            throw Invalid(ProtocolErrorCode.MalformedMessage, "BeamSwapResolved action is not SWAP or KEEP_ORIGINAL.");
        }

        private static List<ContractYaku> MapYaku(IEnumerable<WireYaku> source)
        {
            var result = new List<ContractYaku>();
            foreach (WireYaku yaku in source)
            {
                result.Add(new ContractYaku(yaku.Code, yaku.Han));
            }

            return result;
        }

        private static ContractBreakdown MapBreakdown(WireBreakdown wire)
        {
            return new ContractBreakdown(
                wire.Han,
                wire.Fu,
                wire.TotalPoints,
                MapYaku(wire.Yaku),
                wire.DoraCount,
                wire.UraCount,
                CopyUInts(wire.UraTiles),
                CopyUInts(wire.UraHits),
                wire.UraAuthorized);
        }

        private static List<ContractPayment> MapPayments(IEnumerable<WirePayment> source)
        {
            var result = new List<ContractPayment>();
            foreach (WirePayment payment in source)
            {
                result.Add(new ContractPayment(payment.FromSeat, payment.ToSeat, payment.Amount));
            }

            return result;
        }

        private static List<ContractScore> MapScores(IEnumerable<WireScore> source)
        {
            var result = new List<ContractScore>();
            foreach (WireScore score in source)
            {
                result.Add(new ContractScore(score.Seat, score.Score));
            }

            return result;
        }

        private static List<uint> CopyUInts(IEnumerable<uint> source)
        {
            return new List<uint>(source);
        }

        private static List<int> CopyUIntsAsSeats(IEnumerable<int> source)
        {
            return new List<int>(source);
        }

        private static TableVisibleEventProjectionException Invalid(string code, string message)
        {
            return new TableVisibleEventProjectionException(code, message);
        }
    }

    /// <summary>
    /// Per-binding delivered-event fence. A refresh may repeat already-seen
    /// events; only a strictly newer (event_seq, event_ordinal) pair is a new
    /// notification. It is reset when a new binding is established.
    /// </summary>
    internal sealed class TableVisibleEventFence
    {
        private bool _hasValue;
        private ulong _eventSeq;
        private uint _eventOrdinal;

        public void Reset()
        {
            _hasValue = false;
            _eventSeq = 0;
            _eventOrdinal = 0;
        }

        public bool IsStale(ulong eventSeq, uint eventOrdinal)
        {
            if (!_hasValue)
            {
                return false;
            }

            return eventSeq < _eventSeq ||
                   (eventSeq == _eventSeq && eventOrdinal <= _eventOrdinal);
        }

        public void Advance(ulong eventSeq, uint eventOrdinal)
        {
            if (!_hasValue ||
                eventSeq > _eventSeq ||
                (eventSeq == _eventSeq && eventOrdinal > _eventOrdinal))
            {
                _hasValue = true;
                _eventSeq = eventSeq;
                _eventOrdinal = eventOrdinal;
            }
        }
    }

    internal sealed class TableVisibleEventProjectionException : Exception
    {
        public TableVisibleEventProjectionException(string code, string message)
            : base(message)
        {
            Code = string.IsNullOrEmpty(code) ? ProtocolErrorCode.MalformedMessage : code;
        }

        public string Code { get; }
    }
}
