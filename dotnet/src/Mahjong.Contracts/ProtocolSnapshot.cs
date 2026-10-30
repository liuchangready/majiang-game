using System;
using System.Collections.Generic;

namespace Mahjong.Contracts
{
    /// <summary>Wire-independent public/private read models. The split is the whitelist.</summary>
    public enum ProtocolGamePhase
    {
        Unspecified = 0,
        Stacking = 1,
        Dealing = 2,
        Draw = 3,
        CheatWindow = 4,
        Discard = 5,
        ResponseWindow = 6,
        ChankanWindow = 7,
        KanDraw = 8,
        RoundEnd = 9,
        MatchEnd = 10
    }

    public enum ProtocolMeldType
    {
        Unspecified = 0,
        Chi = 1,
        Pon = 2,
        OpenKan = 3,
        ClosedKan = 4,
        AddedKan = 5
    }

    public enum ProtocolEventType
    {
        Unspecified = 0,
        StackingStarted = 1,
        WallBuilt = 2,
        TileDealt = 3,
        DealingComplete = 4,
        TileDrawn = 5,
        TileDiscarded = 6,
        ChiDeclared = 7,
        PonDeclared = 8,
        KanDeclared = 9,
        RinshanDraw = 10,
        RiichiDeclared = 11,
        TsumoWin = 12,
        RonWin = 13,
        DoraRevealed = 14,
        CheatPointsChanged = 15,
        DrawGame = 16,
        ScoreSettled = 17,
        RoundEnded = 18,
        MatchEnded = 19,
        SleeveBloomResolved = 20,
        BeamSwapResolved = 21
    }

    public enum ProtocolEventVisibility
    {
        /// <summary>Treated as PRIVATE by policy. New events must be explicit.</summary>
        Unspecified = 0,
        Private = 1,
        Public = 2,
        SettlementPublic = 3
    }

    public static class ProtocolEventPolicy
    {
        private static readonly HashSet<ProtocolEventType> PrivateTypes = new HashSet<ProtocolEventType>
        {
            ProtocolEventType.TileDealt,
            ProtocolEventType.TileDrawn,
            ProtocolEventType.RinshanDraw,
            ProtocolEventType.CheatPointsChanged,
            ProtocolEventType.SleeveBloomResolved,
            ProtocolEventType.BeamSwapResolved
        };

        public static bool IsKnownEventType(uint value)
        {
            return value >= (uint)ProtocolEventType.StackingStarted &&
                   value <= (uint)ProtocolEventType.BeamSwapResolved;
        }

        /// <summary>
        /// These event types are private regardless of a claimed visibility
        /// field. A producer cannot promote them by setting PUBLIC.
        /// </summary>
        public static bool IsPrivateEventType(ProtocolEventType type)
        {
            return PrivateTypes.Contains(type);
        }

        public static ProtocolEventVisibility EffectiveVisibility(ProtocolEventVisibility visibility)
        {
            return visibility == ProtocolEventVisibility.Unspecified
                ? ProtocolEventVisibility.Private
                : visibility;
        }

        public static bool IsPublicStreamVisible(ProtocolEventVisibility visibility)
        {
            ProtocolEventVisibility effective = EffectiveVisibility(visibility);
            return effective == ProtocolEventVisibility.Public ||
                   effective == ProtocolEventVisibility.SettlementPublic;
        }
    }

    /// <summary>A raw unknown event. It is preserved for diagnostics only and is never a domain fact.</summary>
    public sealed class UnknownEventRecord
    {
        private readonly byte[] _rawPayload;

        public UnknownEventRecord(
            uint originalEventType,
            uint eventSchemaVersion,
            byte[] rawPayload,
            EventSeq? eventSeq = null,
            StateVersion? commandVersion = null,
            uint eventOrdinal = 0)
        {
            if (rawPayload == null)
            {
                throw new ArgumentNullException(nameof(rawPayload));
            }

            _rawPayload = new byte[rawPayload.Length];
            Array.Copy(rawPayload, _rawPayload, rawPayload.Length);
            OriginalEventType = originalEventType;
            EventSchemaVersion = eventSchemaVersion;
            EventSeq = eventSeq;
            CommandVersion = commandVersion;
            EventOrdinal = eventOrdinal;
        }

        public uint OriginalEventType { get; }

        public uint EventSchemaVersion { get; }

        public EventSeq? EventSeq { get; }

        public StateVersion? CommandVersion { get; }

        public uint EventOrdinal { get; }

        public bool IsDomainFact => false;

        public bool RequiresFullBaseline => true;

        public byte[] CopyRawPayload()
        {
            byte[] copy = new byte[_rawPayload.Length];
            Array.Copy(_rawPayload, copy, _rawPayload.Length);
            return copy;
        }
    }

    /// <summary>
    /// A decoded event. Unknown event types are represented by
    /// <see cref="Unknown"/> and can never be applied to domain state.
    /// </summary>
    public sealed class ProtocolEventRecord
    {
        private readonly byte[]? _wirePayload;

        private ProtocolEventRecord(
            ProtocolEventType type,
            ProtocolEventVisibility visibility,
            uint eventSchemaVersion,
            EventSeq eventSeq,
            StateVersion commandVersion,
            uint eventOrdinal,
            byte[]? wirePayload,
            bool hasUnknownFields,
            ProjectedEventPayload? projectionPayload)
        {
            Type = type;
            Visibility = visibility;
            EventSchemaVersion = eventSchemaVersion;
            EventSeq = eventSeq;
            CommandVersion = commandVersion;
            EventOrdinal = eventOrdinal;
            _wirePayload = wirePayload;
            HasUnknownFields = hasUnknownFields;
            ProjectionPayload = projectionPayload;
            Unknown = null;
        }

        private ProtocolEventRecord(UnknownEventRecord unknown)
        {
            Type = ProtocolEventType.Unspecified;
            Visibility = ProtocolEventVisibility.Private;
            EventSchemaVersion = unknown.EventSchemaVersion;
            EventSeq = unknown.EventSeq ?? new EventSeq(0);
            CommandVersion = unknown.CommandVersion ?? new StateVersion(0);
            EventOrdinal = unknown.EventOrdinal;
            _wirePayload = null;
            HasUnknownFields = true;
            ProjectionPayload = null;
            Unknown = unknown;
        }

        public ProtocolEventType Type { get; }

        public ProtocolEventVisibility Visibility { get; }

        public uint EventSchemaVersion { get; }

        public EventSeq EventSeq { get; }

        public StateVersion CommandVersion { get; }

        public uint EventOrdinal { get; }

        /// <summary>Opaque wire bytes of the known event body; not a canonical command digest.</summary>
        public bool HasPayload => _wirePayload != null;

        public bool HasUnknownFields { get; }

        public UnknownEventRecord? Unknown { get; }

        /// <summary>
        /// Optional typed, already-projected body. A metadata-only record from a decoder has
        /// no projection body; a producer must never use an empty body as a substitute.
        /// </summary>
        public ProjectedEventPayload? ProjectionPayload { get; }

        public bool IsKnown => Unknown == null && Type != ProtocolEventType.Unspecified;

        public bool IsDomainFact => IsKnown;

        public ProtocolEventVisibility EffectiveVisibility => ProtocolEventPolicy.EffectiveVisibility(Visibility);

        public byte[] CopyWirePayload()
        {
            if (_wirePayload == null)
            {
                return Array.Empty<byte>();
            }

            byte[] copy = new byte[_wirePayload.Length];
            Array.Copy(_wirePayload, copy, _wirePayload.Length);
            return copy;
        }

        public static ProtocolEventRecord Known(
            ProtocolEventType type,
            ProtocolEventVisibility visibility,
            uint eventSchemaVersion,
            EventSeq eventSeq,
            StateVersion commandVersion,
            uint eventOrdinal,
            byte[] wirePayload,
            bool hasUnknownFields = false)
        {
            if (type == ProtocolEventType.Unspecified || !ProtocolEventPolicy.IsKnownEventType((uint)type))
            {
                throw new ArgumentException("Known event requires a frozen event type.", nameof(type));
            }

            if (wirePayload == null)
            {
                throw new ArgumentNullException(nameof(wirePayload));
            }

            byte[] copy = new byte[wirePayload.Length];
            Array.Copy(wirePayload, copy, wirePayload.Length);
            return new ProtocolEventRecord(
                type,
                visibility,
                eventSchemaVersion,
                eventSeq,
                commandVersion,
                eventOrdinal,
                copy,
                hasUnknownFields,
                null);
        }

        /// <summary>
        /// Creates a typed projected event. The body is a wire-independent Contracts payload;
        /// it is not an internal runtime encoding and not opaque Protobuf bytes. A producer that
        /// has no typed facts must fail instead of sending an empty body.
        /// </summary>
        public static ProtocolEventRecord KnownTyped(
            ProtocolEventType type,
            ProtocolEventVisibility visibility,
            uint eventSchemaVersion,
            EventSeq eventSeq,
            StateVersion commandVersion,
            uint eventOrdinal,
            ProjectedEventPayload projectionPayload,
            bool hasUnknownFields = false)
        {
            if (type == ProtocolEventType.Unspecified || !ProtocolEventPolicy.IsKnownEventType((uint)type))
            {
                throw new ArgumentException("Known event requires a frozen event type.", nameof(type));
            }

            if (projectionPayload == null)
            {
                throw new ArgumentNullException(nameof(projectionPayload));
            }

            if (projectionPayload.Type != type)
            {
                throw new ArgumentException(
                    "The typed payload belongs to " + projectionPayload.Type + ", not " + type + ".",
                    nameof(projectionPayload));
            }

            return new ProtocolEventRecord(
                type,
                visibility,
                eventSchemaVersion,
                eventSeq,
                commandVersion,
                eventOrdinal,
                Array.Empty<byte>(),
                hasUnknownFields,
                projectionPayload);
        }

        public static ProtocolEventRecord UnknownEvent(UnknownEventRecord unknown)
        {
            if (unknown == null)
            {
                throw new ArgumentNullException(nameof(unknown));
            }

            return new ProtocolEventRecord(unknown);
        }
    }

    public sealed class PublicProjectionException : Exception
    {
        public PublicProjectionException(string message, string code) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// Runtime guard for the public whitelist. The DTO types already have no
    /// private fields; this guard additionally rejects private/unknown events
    /// and enforces settlement-only visibility.
    /// </summary>
    public static class PublicProjectionGuard
    {
        private static readonly string[] ForbiddenPublicFieldNames =
        {
            "closed_tiles", "drawn_tile_id", "discard_history", "legal_actions", "decision",
            "cheat_points", "ledger_entries", "tile_mark", "sleeve_bloom", "beam_swap",
            "preparation", "wall", "dead_wall", "seed"
        };

        public static IReadOnlyList<string> ForbiddenFieldNames => Array.AsReadOnly(ForbiddenPublicFieldNames);

        public static void ValidateEvent(ProtocolEventRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            if (!record.IsKnown)
            {
                throw new PublicProjectionException(
                    "Unknown event type cannot enter a public stream; request a full baseline.",
                    ProtocolErrorCode.StreamGap);
            }

            if (record.ProjectionPayload != null && record.ProjectionPayload.Type != record.Type)
            {
                throw new PublicProjectionException(
                    "Typed event payload type does not match the record type.",
                    ProtocolErrorCode.MalformedMessage);
            }

            if (ProtocolEventPolicy.IsPrivateEventType(record.Type))
            {
                throw new PublicProjectionException(
                    "Private event " + record.Type + " cannot enter a public stream even when it claims public visibility.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }

            if (!ProtocolEventPolicy.IsPublicStreamVisible(record.Visibility))
            {
                throw new PublicProjectionException(
                    "Private event " + record.Type + " cannot enter a public stream.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }
        }

        public static void ValidateMatchView(PublicMatchView view)
        {
            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            for (int i = 0; i < view.SettlementEvents.Count; i++)
            {
                ValidateEvent(view.SettlementEvents[i]);
            }
        }

        public static void ValidatePublicSnapshot(PublicSnapshotView snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            ValidateMatchView(snapshot.View);
        }

        public static void ValidatePlayerSnapshot(PlayerSnapshotView snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            ValidateMatchView(snapshot.Public);
            if (snapshot.Private.Seat != snapshot.Seat)
            {
                throw new PublicProjectionException(
                    "Player snapshot private part belongs to a different seat.",
                    ProtocolErrorCode.MalformedMessage);
            }
        }
    }

    public sealed class MeldView
    {
        private readonly IReadOnlyList<uint> _tileIds;

        public MeldView(ProtocolMeldType type, IReadOnlyList<uint> tileIds, uint? calledTileId, int fromSeat)
        {
            if (type == ProtocolMeldType.Unspecified)
            {
                throw new ArgumentException("Meld type must be explicit.", nameof(type));
            }

            if (tileIds == null)
            {
                throw new ArgumentNullException(nameof(tileIds));
            }

            if (tileIds.Count != 3 && tileIds.Count != 4)
            {
                throw new ArgumentException("A meld has 3 or 4 tiles.", nameof(tileIds));
            }

            var copy = new uint[tileIds.Count];
            var seen = new HashSet<uint>();
            for (int i = 0; i < tileIds.Count; i++)
            {
                copy[i] = SnapshotValidation.TileId(tileIds[i], nameof(tileIds));
                if (!seen.Add(copy[i]))
                {
                    throw new ArgumentException("A meld cannot repeat a physical tile id.", nameof(tileIds));
                }
            }

            SnapshotValidation.SeatOrNoSeat(fromSeat, nameof(fromSeat));
            if (calledTileId.HasValue)
            {
                SnapshotValidation.TileId(calledTileId.Value, nameof(calledTileId));
            }

            _tileIds = Array.AsReadOnly(copy);
            Type = type;
            CalledTileId = calledTileId;
            FromSeat = fromSeat;
        }

        public ProtocolMeldType Type { get; }

        public IReadOnlyList<uint> TileIds => _tileIds;

        public uint? CalledTileId { get; }

        public int FromSeat { get; }
    }

    public sealed class DiscardView
    {
        public DiscardView(uint tileId, uint turnNumber, bool riichiDiscard, bool tsumogiri)
        {
            TileId = SnapshotValidation.TileId(tileId, nameof(tileId));
            TurnNumber = turnNumber;
            RiichiDiscard = riichiDiscard;
            Tsumogiri = tsumogiri;
        }

        public uint TileId { get; }

        public uint TurnNumber { get; }

        public bool RiichiDiscard { get; }

        public bool Tsumogiri { get; }
    }

    public sealed class PlayerPublicView
    {
        private readonly IReadOnlyList<MeldView> _melds;
        private readonly IReadOnlyList<DiscardView> _discards;
        private readonly IReadOnlyList<ConcealedMeldView> _concealedMelds;

        public PlayerPublicView(
            int seat,
            uint handCount,
            IReadOnlyList<MeldView> melds,
            IReadOnlyList<DiscardView> discards,
            bool riichi)
            : this(seat, handCount, melds, discards, riichi, null)
        {
        }

        public PlayerPublicView(
            int seat,
            uint handCount,
            IReadOnlyList<MeldView> melds,
            IReadOnlyList<DiscardView> discards,
            bool riichi,
            IReadOnlyList<ConcealedMeldView>? concealedMelds)
        {
            SnapshotValidation.Seat(seat, nameof(seat));
            if (handCount > 14)
            {
                throw new ArgumentOutOfRangeException(nameof(handCount), handCount, "A hand count cannot exceed 14.");
            }

            _melds = SnapshotValidation.Copy(melds, nameof(melds));
            for (int i = 0; i < _melds.Count; i++)
            {
                if (_melds[i] == null)
                {
                    throw new ArgumentException("Melds cannot contain null.", nameof(melds));
                }

                if (_melds[i].Type == ProtocolMeldType.ClosedKan)
                {
                    throw new ArgumentException(
                        "A public meld must not carry closed-kan tile identities; use ConcealedMelds.",
                        nameof(melds));
                }
            }

            _discards = SnapshotValidation.Copy(discards, nameof(discards));
            _concealedMelds = SnapshotValidation.CopyOptional(concealedMelds, nameof(concealedMelds));
            for (int i = 0; i < _concealedMelds.Count; i++)
            {
                if (_concealedMelds[i] == null)
                {
                    throw new ArgumentException("Concealed melds cannot contain null.", nameof(concealedMelds));
                }

                if (_concealedMelds[i].FromSeat != ProtocolConstants.NoSeat)
                {
                    throw new ArgumentException("A concealed meld placeholder has no public source seat.", nameof(concealedMelds));
                }
            }

            Seat = seat;
            HandCount = handCount;
            Riichi = riichi;
        }

        public int Seat { get; }

        /// <summary>Count only. Opponent tile faces are structurally absent.</summary>
        public uint HandCount { get; }

        public IReadOnlyList<MeldView> Melds => _melds;

        public IReadOnlyList<DiscardView> Discards => _discards;

        /// <summary>
        /// Public closed-kan placeholders. They carry only the fact and the tile count, never
        /// the four physical identities, so a table can account for the meld without seeing it.
        /// </summary>
        public IReadOnlyList<ConcealedMeldView> ConcealedMelds => _concealedMelds;

        public bool Riichi { get; }
    }

    public sealed class ScoreView
    {
        public ScoreView(int seat, long score)
        {
            SnapshotValidation.Seat(seat, nameof(seat));
            Seat = seat;
            Score = score;
        }

        public int Seat { get; }

        public long Score { get; }
    }

    public sealed class ClockView
    {
        public ClockView(long serverTimeMs, long deadlineRemainingMs, long reserveRemainingMs, int decisionSeat)
        {
            if (serverTimeMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(serverTimeMs), "Server time must not be negative.");
            }

            if (decisionSeat != ProtocolConstants.NoSeat)
            {
                SnapshotValidation.Seat(decisionSeat, nameof(decisionSeat));
            }

            ServerTimeMs = serverTimeMs;
            DeadlineRemainingMs = deadlineRemainingMs;
            ReserveRemainingMs = reserveRemainingMs;
            DecisionSeat = decisionSeat;
        }

        public long ServerTimeMs { get; }

        public long DeadlineRemainingMs { get; }

        public long ReserveRemainingMs { get; }

        public int DecisionSeat { get; }
    }

    public sealed class PublicBoardView
    {
        private readonly IReadOnlyList<uint> _doraIndicators;
        private readonly IReadOnlyList<ScoreView> _scores;
        private readonly IReadOnlyList<PlayerPublicView> _players;

        public PublicBoardView(
            ProtocolGamePhase phase,
            uint roundWind,
            uint roundNumber,
            uint honba,
            uint riichiSticks,
            int dealerSeat,
            int currentSeat,
            uint wallRemaining,
            IReadOnlyList<uint> doraIndicators,
            IReadOnlyList<ScoreView> scores,
            IReadOnlyList<PlayerPublicView> players,
            DiscardView? lastDiscard,
            bool paused)
        {
            SnapshotValidation.Seat(dealerSeat, nameof(dealerSeat));
            SnapshotValidation.Seat(currentSeat, nameof(currentSeat));
            if (doraIndicators == null || doraIndicators.Count > 5)
            {
                throw new ArgumentException("At most five dora indicators are public.", nameof(doraIndicators));
            }

            var dora = new uint[doraIndicators.Count];
            for (int i = 0; i < doraIndicators.Count; i++)
            {
                dora[i] = SnapshotValidation.TileId(doraIndicators[i], nameof(doraIndicators));
            }

            _doraIndicators = Array.AsReadOnly(dora);
            _scores = SnapshotValidation.Copy(scores, nameof(scores));
            _players = SnapshotValidation.Copy(players, nameof(players));
            if (_scores.Count != 4 || _players.Count != 4)
            {
                throw new ArgumentException("A public board requires four score rows and four player views.");
            }

            Phase = phase;
            RoundWind = roundWind;
            RoundNumber = roundNumber;
            Honba = honba;
            RiichiSticks = riichiSticks;
            DealerSeat = dealerSeat;
            CurrentSeat = currentSeat;
            WallRemaining = wallRemaining;
            LastDiscard = lastDiscard;
            Paused = paused;
        }

        public ProtocolGamePhase Phase { get; }

        public uint RoundWind { get; }

        public uint RoundNumber { get; }

        public uint Honba { get; }

        public uint RiichiSticks { get; }

        public int DealerSeat { get; }

        public int CurrentSeat { get; }

        public uint WallRemaining { get; }

        public IReadOnlyList<uint> DoraIndicators => _doraIndicators;

        public IReadOnlyList<ScoreView> Scores => _scores;

        public IReadOnlyList<PlayerPublicView> Players => _players;

        public DiscardView? LastDiscard { get; }

        public bool Paused { get; }
    }

    public sealed class PublicSessionSummary
    {
        private readonly IReadOnlyList<ScoreView> _finalScores;
        private readonly IReadOnlyList<int> _rankings;

        public PublicSessionSummary(
            bool finished,
            string finishReason,
            IReadOnlyList<ScoreView> finalScores,
            IReadOnlyList<int> rankings)
        {
            _finalScores = SnapshotValidation.Copy(finalScores, nameof(finalScores));
            if (_finalScores.Count != 0 && _finalScores.Count != 4)
            {
                throw new ArgumentException("Final scores must be empty or four rows.", nameof(finalScores));
            }

            if (rankings == null || (rankings.Count != 0 && rankings.Count != 4))
            {
                throw new ArgumentException("Rankings must be empty or four seats.", nameof(rankings));
            }

            var rankingCopy = new int[rankings.Count];
            for (int i = 0; i < rankings.Count; i++)
            {
                rankingCopy[i] = SnapshotValidation.Seat(rankings[i], nameof(rankings));
            }

            _rankings = Array.AsReadOnly(rankingCopy);
            Finished = finished;
            FinishReason = finishReason ?? string.Empty;
        }

        public bool Finished { get; }

        public string FinishReason { get; }

        public IReadOnlyList<ScoreView> FinalScores => _finalScores;

        public IReadOnlyList<int> Rankings => _rankings;
    }

    public sealed class PublicMatchView
    {
        private readonly IReadOnlyList<ClockView> _clocks;
        private readonly IReadOnlyList<ProtocolEventRecord> _settlementEvents;

        public PublicMatchView(
            PublicBoardView? board,
            PublicSessionSummary session,
            IReadOnlyList<ClockView> clocks,
            bool paused,
            string settlementRoundId,
            IReadOnlyList<ProtocolEventRecord> settlementEvents)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            _clocks = SnapshotValidation.Copy(clocks, nameof(clocks));
            if (_clocks.Count > 4)
            {
                throw new ArgumentException("At most four clocks are public.", nameof(clocks));
            }

            _settlementEvents = SnapshotValidation.Copy(settlementEvents, nameof(settlementEvents));
            for (int i = 0; i < _settlementEvents.Count; i++)
            {
                PublicProjectionGuard.ValidateEvent(_settlementEvents[i]);
            }

            Board = board;
            Session = session;
            Paused = paused;
            SettlementRoundId = settlementRoundId ?? string.Empty;
        }

        public PublicBoardView? Board { get; }

        public PublicSessionSummary Session { get; }

        public IReadOnlyList<ClockView> Clocks => _clocks;

        public bool Paused { get; }

        public string SettlementRoundId { get; }

        public IReadOnlyList<ProtocolEventRecord> SettlementEvents => _settlementEvents;
    }

    public sealed class PublicSnapshotView
    {
        public PublicSnapshotView(
            SessionId sessionId,
            RoundId? roundId,
            StreamCursor cursor,
            StateVersion stateVersion,
            EventSeq eventSeq,
            SessionVersion sessionVersion,
            RoundVersion roundVersion,
            PublicMatchView view)
        {
            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            PublicProjectionGuard.ValidateMatchView(view);
            SessionId = sessionId;
            RoundId = roundId;
            Cursor = cursor;
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            SessionVersion = sessionVersion;
            RoundVersion = roundVersion;
            View = view;
        }

        public SessionId SessionId { get; }

        public RoundId? RoundId { get; }

        public StreamCursor Cursor { get; }

        public StateVersion StateVersion { get; }

        public EventSeq EventSeq { get; }

        public SessionVersion SessionVersion { get; }

        public RoundVersion RoundVersion { get; }

        public PublicMatchView View { get; }
    }

    public sealed class LegalActionView
    {
        private readonly IReadOnlyList<uint> _tileIds;

        public LegalActionView(ProtocolActionType actionType, string? optionId, IReadOnlyList<uint> tileIds)
        {
            if (actionType == ProtocolActionType.Unspecified)
            {
                throw new ArgumentException("Legal action type must be explicit.", nameof(actionType));
            }

            if (tileIds == null)
            {
                throw new ArgumentNullException(nameof(tileIds));
            }

            var copy = new uint[tileIds.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = SnapshotValidation.TileId(tileIds[i], nameof(tileIds));
            }

            _tileIds = Array.AsReadOnly(copy);
            ActionType = actionType;
            OptionId = optionId ?? string.Empty;
        }

        public ProtocolActionType ActionType { get; }

        public string OptionId { get; }

        public IReadOnlyList<uint> TileIds => _tileIds;
    }

    public sealed class DecisionView
    {
        private readonly IReadOnlyList<LegalActionView> _actions;

        public DecisionView(DecisionId decisionId, ControlEpoch controlEpoch, long deadlineRemainingMs, IReadOnlyList<LegalActionView> actions)
        {
            if (decisionId.IsEmpty)
            {
                throw new ArgumentException("Decision id must not be empty.", nameof(decisionId));
            }

            _actions = SnapshotValidation.Copy(actions, nameof(actions));
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            DeadlineRemainingMs = deadlineRemainingMs;
        }

        public DecisionId DecisionId { get; }

        public ControlEpoch ControlEpoch { get; }

        public long DeadlineRemainingMs { get; }

        public IReadOnlyList<LegalActionView> Actions => _actions;
    }

    public sealed class CheatLedgerEntryView
    {
        public CheatLedgerEntryView(string entryId, long amount, long balanceAfter, string reasonCode)
        {
            EntryId = ProtocolText.RequireId(entryId, nameof(entryId));
            if (balanceAfter < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(balanceAfter), balanceAfter, "Cheat balance cannot be negative.");
            }

            if (!ProtocolErrorCode.IsWellFormed(reasonCode))
            {
                throw new ArgumentException("Cheat reason must be a stable uppercase code.", nameof(reasonCode));
            }

            Amount = amount;
            BalanceAfter = balanceAfter;
            ReasonCode = reasonCode;
        }

        public string EntryId { get; }

        public long Amount { get; }

        public long BalanceAfter { get; }

        public string ReasonCode { get; }
    }

    public sealed class PrivateCheatView
    {
        private readonly IReadOnlyList<CheatLedgerEntryView> _ledger;

        public PrivateCheatView(
            long cheatPoints,
            IReadOnlyList<CheatLedgerEntryView> ledger,
            bool hasTileMark,
            bool hasSleeveBloom,
            bool hasBeamSwap,
            bool hasPreparation)
            : this(
                cheatPoints,
                ledger,
                hasTileMark,
                hasSleeveBloom,
                hasBeamSwap,
                hasPreparation,
                null,
                null,
                null,
                null,
                null,
                null,
                null)
        {
        }

        public PrivateCheatView(
            long cheatPoints,
            IReadOnlyList<CheatLedgerEntryView> ledger,
            bool hasTileMark,
            bool hasSleeveBloom,
            bool hasBeamSwap,
            bool hasPreparation,
            TileMarkView? tileMark,
            SleeveBloomView? immediateOffer,
            SleeveBloomView? offTurnOffer,
            SleeveBloomReservationView? reservation,
            string? windowId,
            BeamSwapView? beamSwap,
            ShufflePreparationView? preparation)
        {
            if (cheatPoints < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cheatPoints), cheatPoints, "Cheat points cannot be negative.");
            }

            _ledger = SnapshotValidation.Copy(ledger, nameof(ledger));
            for (int i = 0; i < _ledger.Count; i++)
            {
                if (_ledger[i] == null)
                {
                    throw new ArgumentException("A private ledger cannot contain null.", nameof(ledger));
                }
            }

            if (tileMark != null && !hasTileMark)
            {
                throw new ArgumentException("A typed tile-mark body requires HasTileMark.", nameof(hasTileMark));
            }

            if ((immediateOffer != null || offTurnOffer != null || reservation != null || !string.IsNullOrEmpty(windowId)) &&
                !hasSleeveBloom)
            {
                throw new ArgumentException("A typed sleeve-bloom body requires HasSleeveBloom.", nameof(hasSleeveBloom));
            }

            if (beamSwap != null && !hasBeamSwap)
            {
                throw new ArgumentException("A typed beam-swap body requires HasBeamSwap.", nameof(hasBeamSwap));
            }

            if (preparation != null && !hasPreparation)
            {
                throw new ArgumentException("A typed preparation body requires HasPreparation.", nameof(hasPreparation));
            }

            if (immediateOffer != null && immediateOffer.Mode != ProjectionSleeveBloomMode.Immediate)
            {
                throw new ArgumentException("The immediate offer slot requires an immediate offer.", nameof(immediateOffer));
            }

            if (offTurnOffer != null && offTurnOffer.Mode != ProjectionSleeveBloomMode.OffTurn)
            {
                throw new ArgumentException("The off-turn offer slot requires an off-turn offer.", nameof(offTurnOffer));
            }

            CheatPoints = cheatPoints;
            HasTileMark = hasTileMark;
            HasSleeveBloom = hasSleeveBloom;
            HasBeamSwap = hasBeamSwap;
            HasPreparation = hasPreparation;
            OwnerTileMark = tileMark;
            ImmediateSleeveOffer = immediateOffer;
            OffTurnSleeveOffer = offTurnOffer;
            SleeveReservation = reservation;
            WindowId = SnapshotValidation.OptionalString(windowId);
            OwnerBeamSwap = beamSwap;
            Preparation = preparation;
        }

        public long CheatPoints { get; }

        /// <summary>Owner-only ledger; public types never reference it.</summary>
        public IReadOnlyList<CheatLedgerEntryView> Ledger => _ledger;

        public bool HasTileMark { get; }

        public bool HasSleeveBloom { get; }

        public bool HasBeamSwap { get; }

        public bool HasPreparation { get; }

        /// <summary>Owner-only mark detail; absent when the legacy/projection source had only a flag.</summary>
        public TileMarkView? OwnerTileMark { get; }

        /// <summary>The owner's own-turn frozen offer, if any.</summary>
        public SleeveBloomView? ImmediateSleeveOffer { get; }

        /// <summary>The owner's off-turn offer, if any. It is separate from the immediate offer.</summary>
        public SleeveBloomView? OffTurnSleeveOffer { get; }

        public SleeveBloomReservationView? SleeveReservation { get; }

        public string WindowId { get; }

        public BeamSwapView? OwnerBeamSwap { get; }

        /// <summary>Null when the source projection does not carry preparation detail.</summary>
        public ShufflePreparationView? Preparation { get; }
    }

    /// <summary>
    /// Minimal owner-private seat control authority. The field is optional:
    /// absence means the authoritative control state is unknown and callers must
    /// fail closed rather than treat it as epoch 0 or guess a controller.
    /// </summary>
    public sealed class PlayerControlView
    {
        public PlayerControlView(ControlEpoch controlEpoch, bool controlledByComputer, long controlVersion)
        {
            if (controlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlVersion), controlVersion, "Control version must be non-negative.");
            }

            ControlEpoch = controlEpoch;
            ControlledByComputer = controlledByComputer;
            ControlVersion = controlVersion;
        }

        public ControlEpoch ControlEpoch { get; }

        /// <summary>True for the automated (Computer/Takeover) controller kinds.</summary>
        public bool ControlledByComputer { get; }

        /// <summary>Independent control-event sequence captured with this snapshot.</summary>
        public long ControlVersion { get; }
    }

    public sealed class PrivatePlayerView
    {
        private readonly IReadOnlyList<uint> _closedTiles;
        private readonly IReadOnlyList<uint> _discardHistory;
        private readonly IReadOnlyList<uint> _sleeveTiles;
        private readonly IReadOnlyList<MeldView> _ownerMelds;

        public PrivatePlayerView(
            int seat,
            IReadOnlyList<uint> closedTiles,
            uint? drawnTileId,
            bool furiten,
            IReadOnlyList<uint> discardHistory,
            DecisionView? decision,
            PrivateCheatView? cheat)
            : this(
                seat,
                closedTiles,
                drawnTileId,
                furiten,
                discardHistory,
                decision,
                cheat,
                Array.Empty<uint>(),
                Array.Empty<MeldView>(),
                null)
        {
        }

        public PrivatePlayerView(
            int seat,
            IReadOnlyList<uint> closedTiles,
            uint? drawnTileId,
            bool furiten,
            IReadOnlyList<uint> discardHistory,
            DecisionView? decision,
            PrivateCheatView? cheat,
            IReadOnlyList<uint>? sleeveTiles,
            IReadOnlyList<MeldView>? ownerMelds,
            PlayerControlView? control = null)
        {
            SnapshotValidation.Seat(seat, nameof(seat));
            _closedTiles = SnapshotValidation.CopyTileIds(closedTiles, nameof(closedTiles), 14);
            _discardHistory = SnapshotValidation.CopyTileIds(discardHistory, nameof(discardHistory), 200);
            if (drawnTileId.HasValue)
            {
                SnapshotValidation.TileId(drawnTileId.Value, nameof(drawnTileId));
                for (int i = 0; i < _closedTiles.Count; i++)
                {
                    if (_closedTiles[i] == drawnTileId.Value)
                    {
                        throw new ArgumentException("Drawn tile must not duplicate a closed hand tile.", nameof(drawnTileId));
                    }
                }
            }

            _sleeveTiles = SnapshotValidation.CopyTileIds(sleeveTiles ?? Array.Empty<uint>(), nameof(sleeveTiles), 14);
            for (int i = 0; i < _sleeveTiles.Count; i++)
            {
                if (Contains(_closedTiles, _sleeveTiles[i]) ||
                    (drawnTileId.HasValue && drawnTileId.Value == _sleeveTiles[i]))
                {
                    throw new ArgumentException("A sleeve tile cannot also be in the owner's closed hand.", nameof(sleeveTiles));
                }
            }

            _ownerMelds = SnapshotValidation.Copy(ownerMelds ?? Array.Empty<MeldView>(), nameof(ownerMelds));
            var meldTiles = new HashSet<uint>();
            for (int i = 0; i < _ownerMelds.Count; i++)
            {
                MeldView meld = _ownerMelds[i] ?? throw new ArgumentException("Owner melds cannot contain null.", nameof(ownerMelds));
                for (int t = 0; t < meld.TileIds.Count; t++)
                {
                    uint tile = meld.TileIds[t];
                    if (!meldTiles.Add(tile))
                    {
                        throw new ArgumentException("Owner melds repeat a physical tile.", nameof(ownerMelds));
                    }

                    if (Contains(_closedTiles, tile) || Contains(_sleeveTiles, tile) ||
                        (drawnTileId.HasValue && drawnTileId.Value == tile))
                    {
                        throw new ArgumentException("An owner meld tile cannot also be in the hand or sleeve.", nameof(ownerMelds));
                    }
                }
            }

            Seat = seat;
            DrawnTileId = drawnTileId;
            Furiten = furiten;
            Decision = decision;
            Cheat = cheat;
            Control = control;
        }

        public int Seat { get; }

        /// <summary>Owner-only closed hand. No public type can reach this field.</summary>
        public IReadOnlyList<uint> ClosedTiles => _closedTiles;

        public uint? DrawnTileId { get; }

        public bool Furiten { get; }

        public IReadOnlyList<uint> DiscardHistory => _discardHistory;

        public DecisionView? Decision { get; }

        public PrivateCheatView? Cheat { get; }

        /// <summary>
        /// Owner-private control authority at the same committed capture as this
        /// snapshot; null means unavailable. It is never part of a public board.
        /// </summary>
        public PlayerControlView? Control { get; }

        /// <summary>Owner-only sleeve-zone identities; never part of a public board.</summary>
        public IReadOnlyList<uint> SleeveTiles => _sleeveTiles;

        /// <summary>
        /// Owner-only melds, including a closed kan's four identities. Public melds use
        /// PlayerPublicView.Melds plus ConcealedMelds and never reach this list.
        /// </summary>
        public IReadOnlyList<MeldView> OwnerMelds => _ownerMelds;

        private static bool Contains(IReadOnlyList<uint> values, uint tile)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == tile)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class PlayerSnapshotView
    {
        public PlayerSnapshotView(
            SessionId sessionId,
            RoundId? roundId,
            int seat,
            StreamCursor cursor,
            StateVersion stateVersion,
            EventSeq eventSeq,
            SessionVersion sessionVersion,
            RoundVersion roundVersion,
            PublicMatchView publicView,
            PrivatePlayerView privateView)
        {
            if (publicView == null)
            {
                throw new ArgumentNullException(nameof(publicView));
            }

            if (privateView == null)
            {
                throw new ArgumentNullException(nameof(privateView));
            }

            SnapshotValidation.Seat(seat, nameof(seat));
            if (privateView.Seat != seat)
            {
                throw new ArgumentException("Private view must belong to the snapshot seat.", nameof(privateView));
            }

            PublicProjectionGuard.ValidateMatchView(publicView);
            SessionId = sessionId;
            RoundId = roundId;
            Seat = seat;
            Cursor = cursor;
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            SessionVersion = sessionVersion;
            RoundVersion = roundVersion;
            Public = publicView;
            Private = privateView;
        }

        public SessionId SessionId { get; }

        public RoundId? RoundId { get; }

        public int Seat { get; }

        public StreamCursor Cursor { get; }

        public StateVersion StateVersion { get; }

        public EventSeq EventSeq { get; }

        public SessionVersion SessionVersion { get; }

        public RoundVersion RoundVersion { get; }

        public PublicMatchView Public { get; }

        public PrivatePlayerView Private { get; }
    }

    internal static class SnapshotValidation
    {
        public static int Seat(int value, string name)
        {
            if (value < ProtocolConstants.MinSeat || value > ProtocolConstants.MaxSeat)
            {
                throw new ArgumentOutOfRangeException(name, value, "Seat must be 0..3.");
            }

            return value;
        }

        public static int SeatOrNoSeat(int value, string name)
        {
            if (value == ProtocolConstants.NoSeat)
            {
                return value;
            }

            return Seat(value, name);
        }

        public static uint TileId(uint value, string name)
        {
            if (value > 135)
            {
                throw new ArgumentOutOfRangeException(name, value, "Tile id must be 0..135.");
            }

            return value;
        }

        public static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? values, string name)
        {
            if (values == null)
            {
                throw new ArgumentNullException(name);
            }

            var copy = new T[values.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = values[i];
            }

            return Array.AsReadOnly(copy);
        }

        public static IReadOnlyList<T> CopyOptional<T>(IReadOnlyList<T>? values, string name)
        {
            if (values == null)
            {
                return Array.Empty<T>();
            }

            return Copy(values, name);
        }

        public static string OptionalString(string? value)
        {
            return value ?? string.Empty;
        }

        public static IReadOnlyList<uint> CopyTileIds(IReadOnlyList<uint>? values, string name, int maxCount)
        {
            if (values == null)
            {
                throw new ArgumentNullException(name);
            }

            if (values.Count > maxCount)
            {
                throw new ArgumentException(name + " exceeds " + maxCount + " entries.", name);
            }

            var copy = new uint[values.Count];
            var seen = new HashSet<uint>();
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = TileId(values[i], name);
                if (!seen.Add(copy[i]))
                {
                    throw new ArgumentException(name + " contains duplicate tile ids.", name);
                }
            }

            return Array.AsReadOnly(copy);
        }
    }
}
