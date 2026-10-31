using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Contracts;
using Mahjong.Client;

namespace Mahjong.Unity.Table
{
    /// <summary>
    /// Stable reason codes for facts that the frozen V5 wire projection cannot
    /// represent. They are diagnostics for the table UI; they are never used to
    /// invent a tile, a candidate or an authoritative value.
    /// </summary>
    public static class PlayerTableGapCodes
    {
        public const string SpectatorPlayerSnapshotIgnored = "SPECTATOR_PLAYER_SNAPSHOT_IGNORED";
        public const string PrivateViewNotReady = "PRIVATE_VIEW_NOT_READY";
        public const string PrivateSessionMismatch = "PRIVATE_SESSION_MISMATCH";
        public const string PrivateSeatMismatch = "PRIVATE_SEAT_MISMATCH";
        public const string RoomBindingMismatch = "ROOM_BINDING_MISMATCH";
        public const string PublicSessionMismatch = "PUBLIC_SESSION_MISMATCH";
        public const string PublicRoundMismatch = "PUBLIC_ROUND_MISMATCH";
        public const string PrivateTileDuplicated = "PRIVATE_TILE_DUPLICATED";
        public const string OwnHandCountMismatch = "OWN_HAND_COUNT_MISMATCH";
        public const string OwnerMeldsMissing = "OWNER_MELDS_MISSING_FOR_PUBLIC_MELDS";
        public const string PublicMeldTypeUnknown = "PUBLIC_MELD_TYPE_UNKNOWN";
        public const string MissingSeatProjection = "PUBLIC_SEAT_MISSING";
        public const string DeadWallIdentityNotPublic = "DEAD_WALL_IDENTITY_NOT_PUBLIC";
        public const string ControlStateNotRepresentable = "SEAT_CONTROL_VIEW_NOT_REPRESENTABLE";
        public const string ControlStateUnavailable = "SEAT_CONTROL_VIEW_UNAVAILABLE";
        public const string LegalActionMeldTypeNotRepresentable = "LEGAL_ACTION_MELD_TYPE_NOT_REPRESENTABLE";
        public const string AbsoluteDeadlineNotRepresentable = "ABSOLUTE_DEADLINE_NOT_REPRESENTABLE";
        public const string VisibleEventsNotExposedBySessionView = "VISIBLE_EVENTS_NOT_EXPOSED_BY_SESSION_VIEW";
        public const string ShuffleTargetCandidatesNotRepresentable = "SHUFFLE_TARGET_CANDIDATES_NOT_REPRESENTABLE";
        public const string BeamSlotKindNotInCommand = "BEAM_SLOT_KIND_NOT_IN_COMMAND";
        public const string OwnPublicMeldsSupersededByOwnerMelds = "OWN_PUBLIC_MELDS_SUPERSEDED_BY_OWNER_MELDS";
    }

    public enum PlayerTableRole
    {
        Unknown = 0,
        Player = 1,
        Spectator = 2,
        Lobby = 3
    }

    public enum PlayerGamePhase
    {
        Unknown = 0,
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

    public enum PlayerMeldKind
    {
        Unknown = 0,
        Chi = 1,
        Pon = 2,
        OpenKan = 3,
        ClosedKan = 4,
        AddedKan = 5
    }

    public enum PlayerSleeveBloomDrawKind
    {
        Unknown = 0,
        DealExtra = 1,
        Normal = 2,
        Rinshan = 3
    }

    public enum PlayerBeamSwapSlotKind
    {
        Unknown = 0,
        Wall = 1,
        DeadWall = 2,
        Replacement = 3
    }

    public enum PlayerActionKind
    {
        Unknown = 0,
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

    /// <summary>
    /// A server-relative countdown. Only the remaining milliseconds observed in
    /// the confirmed snapshot are stored; the local monotonic clock is used to
    /// animate the display. This never rewrites the authoritative deadline.
    /// </summary>
    public sealed class PlayerCountdown
    {
        public PlayerCountdown(long serverRemainingMs, long observedAtMonotonicMs)
        {
            if (serverRemainingMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(serverRemainingMs), serverRemainingMs, "A server countdown cannot be negative.");
            }

            if (observedAtMonotonicMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(observedAtMonotonicMs), observedAtMonotonicMs, "A monotonic observation time cannot be negative.");
            }

            ServerRemainingMilliseconds = serverRemainingMs;
            ObservedAtMonotonicMilliseconds = observedAtMonotonicMs;
        }

        public long ServerRemainingMilliseconds { get; }

        public long ObservedAtMonotonicMilliseconds { get; }

        public long RemainingMilliseconds(long nowMonotonicMs)
        {
            long elapsed = nowMonotonicMs > ObservedAtMonotonicMilliseconds
                ? nowMonotonicMs - ObservedAtMonotonicMilliseconds
                : 0L;
            long remaining = ServerRemainingMilliseconds - elapsed;
            return remaining > 0L ? remaining : 0L;
        }

        public bool IsExpired(long nowMonotonicMs)
        {
            return RemainingMilliseconds(nowMonotonicMs) == 0L;
        }
    }

    public sealed class PlayerTableIdentity
    {
        public PlayerTableIdentity(
            long generation,
            string sessionId,
            string roomId,
            string roundId,
            PlayerTableRole role,
            int seat,
            ulong connectionEpoch,
            ulong ownerEpoch,
            ulong bindingEpoch,
            ulong stateVersion,
            ulong eventSeq,
            ulong streamVersion,
            ulong projectionSeq,
            bool isPrivateAuthorized,
            string scopeId)
        {
            if (generation < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(generation));
            }

            if (seat < -1 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "A table seat is -1 or 0..3.");
            }

            Generation = generation;
            SessionId = sessionId ?? string.Empty;
            RoomId = roomId ?? string.Empty;
            RoundId = roundId ?? string.Empty;
            Role = role;
            Seat = seat;
            ConnectionEpoch = connectionEpoch;
            OwnerEpoch = ownerEpoch;
            BindingEpoch = bindingEpoch;
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            StreamVersion = streamVersion;
            ProjectionSeq = projectionSeq;
            IsPrivateAuthorized = isPrivateAuthorized;
            ScopeId = scopeId ?? string.Empty;
        }

        public long Generation { get; }

        public string SessionId { get; }

        public string RoomId { get; }

        public string RoundId { get; }

        public PlayerTableRole Role { get; }

        public int Seat { get; }

        public ulong ConnectionEpoch { get; }

        public ulong OwnerEpoch { get; }

        public ulong BindingEpoch { get; }

        public ulong StateVersion { get; }

        public ulong EventSeq { get; }

        public ulong StreamVersion { get; }

        public ulong ProjectionSeq { get; }

        public bool IsPrivateAuthorized { get; }

        public string ScopeId { get; }

        public bool IsPlayer => Role == PlayerTableRole.Player && Seat >= 0 && Seat <= 3;

        public bool IsSpectator => Role == PlayerTableRole.Spectator;
    }

    public sealed class PlayerTableScore
    {
        public PlayerTableScore(int seat, long score)
        {
            Seat = seat;
            Score = score;
        }

        public int Seat { get; }

        public long Score { get; }
    }

    public sealed class PlayerDiscardState
    {
        public PlayerDiscardState(uint tileId, uint turnNumber, bool riichiDiscard, bool tsumogiri)
        {
            if (tileId > 135)
            {
                throw new ArgumentOutOfRangeException(nameof(tileId));
            }

            TileId = tileId;
            TurnNumber = turnNumber;
            RiichiDiscard = riichiDiscard;
            Tsumogiri = tsumogiri;
        }

        public uint TileId { get; }

        public uint TurnNumber { get; }

        public bool RiichiDiscard { get; }

        public bool Tsumogiri { get; }

        public string EntityKey => TurnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + TileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed class PlayerMeldState
    {
        public PlayerMeldState(
            PlayerMeldKind kind,
            IReadOnlyList<uint> tileIds,
            uint? calledTileId,
            int fromSeat,
            bool isPlaceholder,
            int placeholderOrdinal = 0)
        {
            if (kind == PlayerMeldKind.Unknown)
            {
                throw new ArgumentException("A meld must have a known kind.", nameof(kind));
            }

            if (fromSeat < -1 || fromSeat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(fromSeat));
            }

            if (calledTileId.HasValue && calledTileId.Value > 135)
            {
                throw new ArgumentOutOfRangeException(nameof(calledTileId));
            }

            Kind = kind;
            TileIds = PlayerTableCopy.UInt32List(tileIds);
            CalledTileId = calledTileId;
            FromSeat = fromSeat;
            IsPlaceholder = isPlaceholder;
            PlaceholderOrdinal = placeholderOrdinal;

            if (isPlaceholder && TileIds.Count != 0)
            {
                throw new ArgumentException("A placeholder meld must not carry tile identities.", nameof(tileIds));
            }
        }

        public PlayerMeldKind Kind { get; }

        /// <summary>Empty only for an opponent concealed-kan placeholder.</summary>
        public IReadOnlyList<uint> TileIds { get; }

        public uint? CalledTileId { get; }

        public int FromSeat { get; }

        /// <summary>True for a public closed-kan placeholder; never a fabricated tile list.</summary>
        public bool IsPlaceholder { get; }

        public int PlaceholderOrdinal { get; }

        public bool IsConcealedKan => Kind == PlayerMeldKind.ClosedKan;

        public string EntityKey
        {
            get
            {
                string key = ((int)Kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + FromSeat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                for (int i = 0; i < TileIds.Count; i++)
                {
                    key += "/" + TileIds[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (IsPlaceholder)
                {
                    key += "/p" + PlaceholderOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return key;
            }
        }
    }

    public sealed class PlayerSeatState
    {
        public PlayerSeatState(
            int seat,
            bool hasPublicData,
            uint? handCount,
            bool riichi,
            IReadOnlyList<PlayerDiscardState> discards,
            IReadOnlyList<PlayerMeldState> melds,
            bool meldsAreOwnerOnly)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat));
            }

            if (handCount.HasValue && handCount.Value > 14)
            {
                throw new ArgumentOutOfRangeException(nameof(handCount));
            }

            Seat = seat;
            HasPublicData = hasPublicData;
            HandCount = handCount;
            Riichi = riichi;
            Discards = PlayerTableCopy.ReadOnly(discards);
            Melds = PlayerTableCopy.ReadOnly(melds);
            MeldsAreOwnerOnly = meldsAreOwnerOnly;
        }

        public int Seat { get; }

        /// <summary>False when the confirmed public board did not contain this seat.</summary>
        public bool HasPublicData { get; }

        /// <summary>
        /// Public hand count, including the drawn tile on the owner's seat. It is
        /// a count only and must never be expanded into tile ids.
        /// </summary>
        public uint? HandCount { get; }

        public bool Riichi { get; }

        public IReadOnlyList<PlayerDiscardState> Discards { get; }

        /// <summary>
        /// The only meld list a renderer should use. For the authorized owner it
        /// is the identity-complete owner meld list; for every other seat it is
        /// the public list (open melds plus identity-free closed-kan placeholders).
        /// The own seat's public meld copy is deliberately not rendered again.
        /// </summary>
        public IReadOnlyList<PlayerMeldState> Melds { get; }

        public bool MeldsAreOwnerOnly { get; }
    }

    public sealed class PlayerLegalActionState
    {
        public PlayerLegalActionState(
            int index,
            PlayerActionKind action,
            uint rawActionType,
            string optionId,
            IReadOnlyList<uint> tileIds,
            string roundId,
            string? decisionId,
            ulong? controlEpoch)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            Index = index;
            Action = action;
            RawActionType = rawActionType;
            OptionId = optionId ?? string.Empty;
            TileIds = PlayerTableCopy.UInt32List(tileIds);
            RoundId = roundId ?? string.Empty;
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
        }

        public int Index { get; }

        public PlayerActionKind Action { get; }

        public uint RawActionType { get; }

        public string OptionId { get; }

        public IReadOnlyList<uint> TileIds { get; }

        public string RoundId { get; }

        public string? DecisionId { get; }

        public ulong? ControlEpoch { get; }
    }

    public sealed class PlayerDecisionState
    {
        public PlayerDecisionState(
            string decisionId,
            ulong controlEpoch,
            string roundId,
            PlayerCountdown countdown,
            IReadOnlyList<PlayerLegalActionState> candidates)
        {
            if (string.IsNullOrEmpty(decisionId))
            {
                throw new ArgumentException("A decision needs an id.", nameof(decisionId));
            }

            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            RoundId = roundId ?? string.Empty;
            Countdown = countdown ?? throw new ArgumentNullException(nameof(countdown));
            Candidates = PlayerTableCopy.ReadOnly(candidates);
        }

        public string DecisionId { get; }

        public ulong ControlEpoch { get; }

        public string RoundId { get; }

        public PlayerCountdown Countdown { get; }

        public IReadOnlyList<PlayerLegalActionState> Candidates { get; }
    }

    public sealed class PlayerCheatLedgerEntryState
    {
        public PlayerCheatLedgerEntryState(string entryId, long amount, long balanceAfter, string reasonCode)
        {
            EntryId = entryId ?? string.Empty;
            Amount = amount;
            BalanceAfter = balanceAfter;
            ReasonCode = reasonCode ?? string.Empty;
        }

        public string EntryId { get; }

        public long Amount { get; }

        public long BalanceAfter { get; }

        public string ReasonCode { get; }
    }

    public sealed class PlayerMarkedTileState
    {
        public PlayerMarkedTileState(uint tileId, string area, int seat, uint riverPosition)
        {
            if (tileId > 135)
            {
                throw new ArgumentOutOfRangeException(nameof(tileId));
            }

            TileId = tileId;
            Area = area ?? string.Empty;
            Seat = seat;
            RiverPosition = riverPosition;
        }

        public uint TileId { get; }

        public string Area { get; }

        public int Seat { get; }

        public uint RiverPosition { get; }
    }

    public sealed class PlayerTileMarkGroupState
    {
        public PlayerTileMarkGroupState(
            string groupId,
            IReadOnlyList<PlayerMarkedTileState> tiles,
            string createdRoundId,
            int createdRoundOrdinal,
            int expiresAfterRoundOrdinal)
        {
            GroupId = groupId ?? string.Empty;
            Tiles = PlayerTableCopy.ReadOnly(tiles);
            CreatedRoundId = createdRoundId ?? string.Empty;
            CreatedRoundOrdinal = createdRoundOrdinal;
            ExpiresAfterRoundOrdinal = expiresAfterRoundOrdinal;
        }

        public string GroupId { get; }

        public IReadOnlyList<PlayerMarkedTileState> Tiles { get; }

        public string CreatedRoundId { get; }

        public int CreatedRoundOrdinal { get; }

        public int ExpiresAfterRoundOrdinal { get; }
    }

    public sealed class PlayerTileMarkState
    {
        public PlayerTileMarkState(
            string roundId,
            IReadOnlyList<uint> selectableTileIds,
            IReadOnlyList<PlayerTileMarkGroupState> groups)
        {
            RoundId = roundId ?? string.Empty;
            SelectableTileIds = PlayerTableCopy.UInt32List(selectableTileIds);
            Groups = PlayerTableCopy.ReadOnly(groups);
        }

        public string RoundId { get; }

        public IReadOnlyList<uint> SelectableTileIds { get; }

        public IReadOnlyList<PlayerTileMarkGroupState> Groups { get; }
    }

    public sealed class PlayerSleeveBloomState
    {
        public PlayerSleeveBloomState(
            string roundId,
            string offerId,
            string mode,
            uint? firstTileId,
            uint? secondTileId,
            PlayerCountdown? countdown,
            string windowId,
            uint? reservedTileId,
            uint createdTurnNumber,
            PlayerSleeveBloomDrawKind drawKind,
            bool hasDeadline)
        {
            RoundId = roundId ?? string.Empty;
            OfferId = offerId ?? string.Empty;
            Mode = mode ?? string.Empty;
            FirstTileId = firstTileId;
            SecondTileId = secondTileId;
            Countdown = countdown;
            WindowId = windowId ?? string.Empty;
            ReservedTileId = reservedTileId;
            CreatedTurnNumber = createdTurnNumber;
            DrawKind = drawKind;
            HasDeadline = hasDeadline;
        }

        public string RoundId { get; }

        public string OfferId { get; }

        public string Mode { get; }

        public uint? FirstTileId { get; }

        public uint? SecondTileId { get; }

        public PlayerCountdown? Countdown { get; }

        public string WindowId { get; }

        public uint? ReservedTileId { get; }

        public uint CreatedTurnNumber { get; }

        public PlayerSleeveBloomDrawKind DrawKind { get; }

        public bool HasDeadline { get; }

        public bool IsOffTurn => string.Equals(Mode, "OFF_TURN", StringComparison.Ordinal);
    }

    public sealed class PlayerSleeveBloomReservationState
    {
        public PlayerSleeveBloomReservationState(string roundId, string offerId, uint tileId, string windowId)
        {
            if (tileId > 135)
            {
                throw new ArgumentOutOfRangeException(nameof(tileId));
            }

            RoundId = roundId ?? string.Empty;
            OfferId = offerId ?? string.Empty;
            TileId = tileId;
            WindowId = windowId ?? string.Empty;
        }

        public string RoundId { get; }

        public string OfferId { get; }

        public uint TileId { get; }

        public string WindowId { get; }
    }

    public sealed class PlayerBeamSwapCandidateState
    {
        public PlayerBeamSwapCandidateState(uint slot, uint? tileId, PlayerBeamSwapSlotKind slotKind, uint slotIndex)
        {
            Slot = slot;
            TileId = tileId;
            SlotKind = slotKind;
            SlotIndex = slotIndex;
        }

        public uint Slot { get; }

        public uint? TileId { get; }

        public PlayerBeamSwapSlotKind SlotKind { get; }

        public uint SlotIndex { get; }
    }

    public sealed class PlayerBeamSwapState
    {
        public PlayerBeamSwapState(
            string roundId,
            string offerId,
            IReadOnlyList<PlayerBeamSwapCandidateState> candidates,
            IReadOnlyList<uint> selectableHandTileIds,
            PlayerCountdown? countdown,
            uint? drawnTileId,
            bool hasDeadline)
        {
            RoundId = roundId ?? string.Empty;
            OfferId = offerId ?? string.Empty;
            Candidates = PlayerTableCopy.ReadOnly(candidates);
            SelectableHandTileIds = PlayerTableCopy.UInt32List(selectableHandTileIds);
            Countdown = countdown;
            DrawnTileId = drawnTileId;
            HasDeadline = hasDeadline;
        }

        public string RoundId { get; }

        public string OfferId { get; }

        public IReadOnlyList<PlayerBeamSwapCandidateState> Candidates { get; }

        public IReadOnlyList<uint> SelectableHandTileIds { get; }

        public PlayerCountdown? Countdown { get; }

        public uint? DrawnTileId { get; }

        public bool HasDeadline { get; }
    }

    public sealed class PlayerPreparationState
    {
        public PlayerPreparationState(
            string roundId,
            uint target,
            bool seeThrough,
            bool confirmed,
            uint reservedPoints,
            uint availablePoints,
            bool seeThroughEnabled,
            uint seeThroughCost,
            PlayerCountdown countdown)
        {
            RoundId = roundId ?? string.Empty;
            Target = target;
            SeeThrough = seeThrough;
            Confirmed = confirmed;
            ReservedPoints = reservedPoints;
            AvailablePoints = availablePoints;
            SeeThroughEnabled = seeThroughEnabled;
            SeeThroughCost = seeThroughCost;
            Countdown = countdown ?? throw new ArgumentNullException(nameof(countdown));
        }

        public string RoundId { get; }

        public uint Target { get; }

        public bool SeeThrough { get; }

        public bool Confirmed { get; }

        public uint ReservedPoints { get; }

        public uint AvailablePoints { get; }

        public bool SeeThroughEnabled { get; }

        public uint SeeThroughCost { get; }

        public PlayerCountdown Countdown { get; }
    }

    public sealed class PlayerCheatState
    {
        public PlayerCheatState(
            string roundId,
            long cheatPoints,
            IReadOnlyList<PlayerCheatLedgerEntryState> ledger,
            PlayerTileMarkState? tileMark,
            PlayerSleeveBloomState? immediateSleeveBloom,
            PlayerSleeveBloomState? offTurnSleeveBloom,
            PlayerSleeveBloomReservationState? sleeveBloomReservation,
            PlayerBeamSwapState? beamSwap,
            PlayerPreparationState? preparation,
            string windowId)
        {
            RoundId = roundId ?? string.Empty;
            CheatPoints = cheatPoints;
            Ledger = PlayerTableCopy.ReadOnly(ledger);
            TileMark = tileMark;
            ImmediateSleeveBloom = immediateSleeveBloom;
            OffTurnSleeveBloom = offTurnSleeveBloom;
            SleeveBloomReservation = sleeveBloomReservation;
            BeamSwap = beamSwap;
            Preparation = preparation;
            WindowId = windowId ?? string.Empty;
        }

        public string RoundId { get; }

        public long CheatPoints { get; }

        public IReadOnlyList<PlayerCheatLedgerEntryState> Ledger { get; }

        public PlayerTileMarkState? TileMark { get; }

        public PlayerSleeveBloomState? ImmediateSleeveBloom { get; }

        public PlayerSleeveBloomState? OffTurnSleeveBloom { get; }

        public PlayerSleeveBloomReservationState? SleeveBloomReservation { get; }

        public PlayerBeamSwapState? BeamSwap { get; }

        public PlayerPreparationState? Preparation { get; }

        public string WindowId { get; }
    }

    /// <summary>
    /// Owner-private seat control authority from the confirmed snapshot. It is
    /// independent of any decision window: a takeover may hold the seat with no
    /// decision present. A null value means the authority is unavailable.
    /// </summary>
    public sealed class PlayerControlState
    {
        public PlayerControlState(ulong controlEpoch, bool controlledByComputer, long controlVersion)
        {
            if (controlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlVersion));
            }

            ControlEpoch = controlEpoch;
            ControlledByComputer = controlledByComputer;
            ControlVersion = controlVersion;
        }

        public ulong ControlEpoch { get; }

        public bool ControlledByComputer { get; }

        public long ControlVersion { get; }
    }

    public sealed class PlayerOwnState
    {
        public PlayerOwnState(
            int seat,
            string roundId,
            IReadOnlyList<uint> closedTiles,
            uint? drawnTileId,
            IReadOnlyList<uint> sleeveTiles,
            IReadOnlyList<uint> discardHistory,
            bool furiten,
            PlayerDecisionState? decision,
            IReadOnlyList<PlayerLegalActionState> advisoryLegalActions,
            PlayerCheatState? cheat,
            PlayerControlState? control = null)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat));
            }

            if (drawnTileId.HasValue && drawnTileId.Value > 135)
            {
                throw new ArgumentOutOfRangeException(nameof(drawnTileId));
            }

            Seat = seat;
            RoundId = roundId ?? string.Empty;
            ClosedTiles = PlayerTableCopy.UInt32List(closedTiles);
            DrawnTileId = drawnTileId;
            SleeveTiles = PlayerTableCopy.UInt32List(sleeveTiles);
            DiscardHistory = PlayerTableCopy.UInt32List(discardHistory);
            Furiten = furiten;
            Decision = decision;
            AdvisoryLegalActions = PlayerTableCopy.ReadOnly(advisoryLegalActions);
            Cheat = cheat;
            Control = control;
        }

        public int Seat { get; }

        public string RoundId { get; }

        /// <summary>
        /// Closed hand tiles only. The drawn tile is a separate field; the two
        /// sets are disjoint by contract and must not be concatenated into a
        /// duplicate physical tile list.
        /// </summary>
        public IReadOnlyList<uint> ClosedTiles { get; }

        public uint? DrawnTileId { get; }

        public IReadOnlyList<uint> SleeveTiles { get; }

        public IReadOnlyList<uint> DiscardHistory { get; }

        public bool Furiten { get; }

        /// <summary>The server-issued decision. Null means no submittable candidates.</summary>
        public PlayerDecisionState? Decision { get; }

        /// <summary>
        /// Private legal actions outside a decision. These are advisory and are
        /// never turned into a command without a decision id and control epoch.
        /// </summary>
        public IReadOnlyList<PlayerLegalActionState> AdvisoryLegalActions { get; }

        public PlayerCheatState? Cheat { get; }

        /// <summary>Owner-private control authority; null means fail closed.</summary>
        public PlayerControlState? Control { get; }

        public uint PhysicalHandTileCount => (uint)(ClosedTiles.Count + (DrawnTileId.HasValue ? 1 : 0));
    }

    public sealed class PlayerBoardState
    {
        public PlayerBoardState(
            bool hasData,
            PlayerGamePhase phase,
            uint roundWind,
            uint roundNumber,
            uint honba,
            uint riichiSticks,
            int dealerSeat,
            int currentSeat,
            uint wallRemaining,
            IReadOnlyList<uint> doraIndicators,
            IReadOnlyList<PlayerTableScore> scores,
            PlayerDiscardState? lastDiscard,
            bool paused,
            bool sessionFinished,
            string finishReason,
            IReadOnlyList<PlayerTableScore> finalScores,
            IReadOnlyList<int> rankings,
            string settlementRoundId)
        {
            HasData = hasData;
            Phase = phase;
            RoundWind = roundWind;
            RoundNumber = roundNumber;
            Honba = honba;
            RiichiSticks = riichiSticks;
            DealerSeat = dealerSeat;
            CurrentSeat = currentSeat;
            WallRemaining = wallRemaining;
            DoraIndicators = PlayerTableCopy.UInt32List(doraIndicators);
            Scores = PlayerTableCopy.ReadOnly(scores);
            LastDiscard = lastDiscard;
            Paused = paused;
            SessionFinished = sessionFinished;
            FinishReason = finishReason ?? string.Empty;
            FinalScores = PlayerTableCopy.ReadOnly(finalScores);
            Rankings = PlayerTableCopy.Int32List(rankings);
            SettlementRoundId = settlementRoundId ?? string.Empty;
        }

        public bool HasData { get; }

        public PlayerGamePhase Phase { get; }

        public uint RoundWind { get; }

        public uint RoundNumber { get; }

        public uint Honba { get; }

        public uint RiichiSticks { get; }

        public int DealerSeat { get; }

        public int CurrentSeat { get; }

        /// <summary>Count only. Wall order and dead-wall identities are not public.</summary>
        public uint WallRemaining { get; }

        public IReadOnlyList<uint> DoraIndicators { get; }

        public IReadOnlyList<PlayerTableScore> Scores { get; }

        public PlayerDiscardState? LastDiscard { get; }

        public bool Paused { get; }

        public bool SessionFinished { get; }

        public string FinishReason { get; }

        public IReadOnlyList<PlayerTableScore> FinalScores { get; }

        public IReadOnlyList<int> Rankings { get; }

        public string SettlementRoundId { get; }
    }

    public sealed class PlayerClockState
    {
        public PlayerClockState(
            int seat,
            PlayerCountdown? decisionDeadline,
            long reserveRemainingMilliseconds,
            int decisionSeat,
            long serverTimeMilliseconds)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat));
            }

            Seat = seat;
            DecisionDeadline = decisionDeadline;
            ReserveRemainingMilliseconds = reserveRemainingMilliseconds;
            DecisionSeat = decisionSeat;
            ServerTimeMilliseconds = serverTimeMilliseconds;
        }

        public int Seat { get; }

        public PlayerCountdown? DecisionDeadline { get; }

        public long ReserveRemainingMilliseconds { get; }

        public int DecisionSeat { get; }

        public long ServerTimeMilliseconds { get; }
    }

    /// <summary>
    /// Immutable presentation projection of one confirmed player or public
    /// snapshot. No protobuf message or mutable collection is retained.
    /// </summary>
    public sealed class PlayerTableViewState
    {
        public PlayerTableViewState(
            PlayerTableIdentity identity,
            TableConnectionState connectionState,
            PlayerBoardState board,
            IReadOnlyList<PlayerSeatState> seats,
            PlayerOwnState? own,
            IReadOnlyList<PlayerClockState> clocks,
            bool paused,
            IReadOnlyList<string> gaps)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            ConnectionState = connectionState;
            Board = board ?? throw new ArgumentNullException(nameof(board));
            Seats = PlayerTableCopy.ReadOnly(seats);
            Own = own;
            Clocks = PlayerTableCopy.ReadOnly(clocks);
            Paused = paused;
            Gaps = PlayerTableCopy.ReadOnly(gaps);
        }

        public PlayerTableIdentity Identity { get; }

        public TableConnectionState ConnectionState { get; }

        public PlayerBoardState Board { get; }

        public IReadOnlyList<PlayerSeatState> Seats { get; }

        public PlayerOwnState? Own { get; }

        /// <summary>Confirmed owner control authority; null when unavailable.</summary>
        public PlayerControlState? Control => Own?.Control;

        /// <summary>
        /// The one own-seat meld list a renderer should use. It is the same
        /// immutable list exposed by <see cref="Seat"/> for the owner and
        /// includes a closed kan's four physical identities.
        /// </summary>
        public IReadOnlyList<PlayerMeldState> OwnMelds
        {
            get
            {
                if (Own == null)
                {
                    return PlayerTableCopy.Empty<PlayerMeldState>();
                }

                PlayerSeatState? seat = Seat(Own.Seat);
                return seat == null ? PlayerTableCopy.Empty<PlayerMeldState>() : seat.Melds;
            }
        }

        public IReadOnlyList<PlayerClockState> Clocks { get; }

        public bool Paused { get; }

        public IReadOnlyList<string> Gaps { get; }

        public bool HasGap(string code)
        {
            for (int i = 0; i < Gaps.Count; i++)
            {
                if (string.Equals(Gaps[i], code, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public bool CanSubmit
        {
            get
            {
                return Own != null &&
                       Identity.IsPrivateAuthorized &&
                       ConnectionState == TableConnectionState.Ready &&
                       !Paused &&
                       !Board.SessionFinished;
            }
        }

        public PlayerSeatState? Seat(int seat)
        {
            for (int i = 0; i < Seats.Count; i++)
            {
                if (Seats[i].Seat == seat)
                {
                    return Seats[i];
                }
            }

            return null;
        }

        public PlayerClockState? Clock(int seat)
        {
            for (int i = 0; i < Clocks.Count; i++)
            {
                if (Clocks[i].Seat == seat)
                {
                    return Clocks[i];
                }
            }

            return null;
        }
    }

    internal static class PlayerTableCopy
    {
        public static IReadOnlyList<T> ReadOnly<T>(IReadOnlyList<T>? source)
        {
            if (source == null || source.Count == 0)
            {
                return Empty<T>();
            }

            var copy = new List<T>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                copy.Add(source[i]);
            }

            return new ReadOnlyCollection<T>(copy);
        }

        public static IReadOnlyList<uint> UInt32List(IReadOnlyList<uint>? source)
        {
            if (source == null || source.Count == 0)
            {
                return Empty<uint>();
            }

            var copy = new uint[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] > 135)
                {
                    throw new ArgumentOutOfRangeException(nameof(source), source[i], "A tile id must be 0..135.");
                }

                copy[i] = source[i];
            }

            return Array.AsReadOnly(copy);
        }

        public static IReadOnlyList<int> Int32List(IReadOnlyList<int>? source)
        {
            if (source == null || source.Count == 0)
            {
                return Empty<int>();
            }

            var copy = new int[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                copy[i] = source[i];
            }

            return Array.AsReadOnly(copy);
        }

        public static IReadOnlyList<T> Empty<T>()
        {
            return Array.AsReadOnly(Array.Empty<T>());
        }
    }
}
