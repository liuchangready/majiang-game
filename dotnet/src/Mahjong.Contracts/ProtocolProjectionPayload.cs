using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Contracts
{
    /// <summary>
    /// A typed, wire-independent projection body. It carries only facts that already belong to
    /// one explicit audience: the public settlement event surface or one owner's private view.
    /// It is not a Domain object, not a serializer and never a container for arbitrary bytes.
    /// </summary>
    public sealed class ProjectionPayloadException : Exception
    {
        public ProjectionPayloadException(string message, string code = "PROJECTION_PAYLOAD_INVALID")
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    public enum ProjectionSleeveBloomMode
    {
        Unspecified = 0,
        Immediate = 1,
        OffTurn = 2
    }

    public enum ProjectionSleeveBloomDrawKind
    {
        Unspecified = 0,
        DealExtra = 1,
        Normal = 2,
        Rinshan = 3
    }

    public enum ProjectionBeamSwapSlotKind
    {
        Unspecified = 0,
        Wall = 1,
        DeadWall = 2,
        Replacement = 3
    }

    public enum ProjectionBeamSwapResolution
    {
        Unspecified = 0,
        Swap = 1,
        KeepOriginal = 2
    }

    /// <summary>
    /// A public closed-kan placeholder. It proves that a concealed meld exists and how many
    /// tiles it occupies without carrying any tile identity. Public melds must use this type
    /// instead of a MeldView for MELD_TYPE_CLOSED_KAN.
    /// </summary>
    public sealed class ConcealedMeldView
    {
        public ConcealedMeldView(ProtocolMeldType type, uint tileCount, int fromSeat)
        {
            if (type != ProtocolMeldType.ClosedKan)
            {
                throw new ArgumentException("A concealed meld placeholder must be a closed kan.", nameof(type));
            }

            if (tileCount != 4)
            {
                throw new ArgumentOutOfRangeException(nameof(tileCount), tileCount, "A closed kan occupies exactly four tiles.");
            }

            ProjectionPayloadValidation.SeatOrNoSeat(fromSeat, nameof(fromSeat));
            Type = type;
            TileCount = tileCount;
            FromSeat = fromSeat;
        }

        public ProtocolMeldType Type { get; }

        public uint TileCount { get; }

        public int FromSeat { get; }
    }

    public sealed class YakuView
    {
        public YakuView(string code, uint han)
        {
            Code = ProtocolText.RequireId(code, nameof(code));
            if (han == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(han), han, "A yaku must carry at least one han.");
            }

            Han = han;
        }

        public string Code { get; }

        public uint Han { get; }
    }

    public sealed class PaymentView
    {
        public PaymentView(int fromSeat, int toSeat, long amount)
        {
            ProjectionPayloadValidation.Seat(fromSeat, nameof(fromSeat));
            ProjectionPayloadValidation.Seat(toSeat, nameof(toSeat));
            if (fromSeat == toSeat)
            {
                throw new ArgumentException("A payment needs two distinct seats.", nameof(toSeat));
            }

            FromSeat = fromSeat;
            ToSeat = toSeat;
            Amount = amount;
        }

        public int FromSeat { get; }

        public int ToSeat { get; }

        public long Amount { get; }
    }

    public sealed class ScoreBreakdownView
    {
        private readonly ReadOnlyCollection<YakuView> _yaku;
        private readonly ReadOnlyCollection<uint> _uraTiles;
        private readonly ReadOnlyCollection<uint> _uraHits;

        public ScoreBreakdownView(
            uint han,
            uint fu,
            long totalPoints,
            IReadOnlyList<YakuView>? yaku,
            uint doraCount,
            uint uraCount,
            IReadOnlyList<uint>? uraTiles,
            IReadOnlyList<uint>? uraHits,
            bool uraAuthorized)
        {
            _yaku = ProjectionPayloadValidation.Items(yaku, nameof(yaku), 64);
            for (int i = 0; i < _yaku.Count; i++)
            {
                if (_yaku[i] == null)
                {
                    throw new ArgumentException("Yaku cannot contain null.", nameof(yaku));
                }
            }

            _uraTiles = ProjectionPayloadValidation.Tiles(uraTiles, nameof(uraTiles), 5, allowEmpty: true);
            _uraHits = ProjectionPayloadValidation.Tiles(uraHits, nameof(uraHits), 14, allowEmpty: true);
            if ((_uraTiles.Count > 0 || _uraHits.Count > 0) && !uraAuthorized)
            {
                throw new ArgumentException(
                    "Ura tile identities require explicit settlement authorization.",
                    nameof(uraAuthorized));
            }

            Han = han;
            Fu = fu;
            TotalPoints = totalPoints;
            DoraCount = doraCount;
            UraCount = uraCount;
            UraAuthorized = uraAuthorized;
        }

        public uint Han { get; }

        public uint Fu { get; }

        public long TotalPoints { get; }

        public IReadOnlyList<YakuView> Yaku => _yaku;

        public uint DoraCount { get; }

        public uint UraCount { get; }

        public IReadOnlyList<uint> UraTiles => _uraTiles;

        public IReadOnlyList<uint> UraHits => _uraHits;

        public bool UraAuthorized { get; }
    }

    /// <summary>One sleeve-bloom offer as it may be shown to its owner.</summary>
    public sealed class SleeveBloomView
    {
        public SleeveBloomView(
            string offerId,
            ProjectionSleeveBloomMode mode,
            uint firstTileId,
            uint secondTileId,
            bool hasDeadline,
            long deadlineRemainingMs,
            string? windowId,
            uint? reservedTileId,
            uint createdTurnNumber,
            ProjectionSleeveBloomDrawKind drawKind)
        {
            OfferId = ProtocolText.RequireId(offerId, nameof(offerId));
            if (mode == ProjectionSleeveBloomMode.Unspecified)
            {
                throw new ArgumentException("Sleeve-bloom mode must be explicit.", nameof(mode));
            }

            if (drawKind == ProjectionSleeveBloomDrawKind.Unspecified)
            {
                throw new ArgumentException("Sleeve-bloom draw kind must be explicit.", nameof(drawKind));
            }

            FirstTileId = ProjectionPayloadValidation.Tile(firstTileId, nameof(firstTileId));
            SecondTileId = ProjectionPayloadValidation.Tile(secondTileId, nameof(secondTileId));
            if (FirstTileId == SecondTileId)
            {
                throw new ArgumentException("Sleeve-bloom candidates must be distinct.", nameof(secondTileId));
            }

            if (deadlineRemainingMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deadlineRemainingMs), deadlineRemainingMs, "A remaining deadline cannot be negative.");
            }

            if (!hasDeadline && deadlineRemainingMs != 0)
            {
                throw new ArgumentException("A remaining deadline requires HasDeadline.", nameof(deadlineRemainingMs));
            }

            if (mode == ProjectionSleeveBloomMode.Immediate && hasDeadline)
            {
                throw new ArgumentException("An immediate sleeve-bloom offer has no deadline.", nameof(hasDeadline));
            }

            string window = ProjectionPayloadValidation.OptionalId(windowId);
            if (mode == ProjectionSleeveBloomMode.OffTurn && window.Length == 0)
            {
                throw new ArgumentException("An off-turn offer requires a window id.", nameof(windowId));
            }

            if (mode == ProjectionSleeveBloomMode.Immediate && window.Length != 0)
            {
                throw new ArgumentException("An immediate offer has no off-turn window.", nameof(windowId));
            }

            if (reservedTileId.HasValue)
            {
                ProjectionPayloadValidation.Tile(reservedTileId.Value, nameof(reservedTileId));
            }

            Mode = mode;
            HasDeadline = hasDeadline;
            DeadlineRemainingMs = deadlineRemainingMs;
            WindowId = window;
            ReservedTileId = reservedTileId;
            CreatedTurnNumber = createdTurnNumber;
            DrawKind = drawKind;
        }

        public string OfferId { get; }

        public ProjectionSleeveBloomMode Mode { get; }

        public uint FirstTileId { get; }

        public uint SecondTileId { get; }

        /// <summary>False when the source offer has no deadline (an immediate offer).</summary>
        public bool HasDeadline { get; }

        /// <summary>Relative milliseconds only; absolute monotonic values never cross the wire.</summary>
        public long DeadlineRemainingMs { get; }

        public string WindowId { get; }

        public uint? ReservedTileId { get; }

        public uint CreatedTurnNumber { get; }

        public ProjectionSleeveBloomDrawKind DrawKind { get; }
    }

    public sealed class SleeveBloomReservationView
    {
        public SleeveBloomReservationView(string offerId, uint tileId, string? windowId)
        {
            OfferId = ProtocolText.RequireId(offerId, nameof(offerId));
            TileId = ProjectionPayloadValidation.Tile(tileId, nameof(tileId));
            WindowId = ProjectionPayloadValidation.OptionalId(windowId);
        }

        public string OfferId { get; }

        public uint TileId { get; }

        public string WindowId { get; }
    }

    public sealed class MarkedTileView
    {
        public MarkedTileView(uint tileId, string? area, int seat, uint riverPosition)
        {
            TileId = ProjectionPayloadValidation.Tile(tileId, nameof(tileId));
            Area = area ?? string.Empty;
            ProjectionPayloadValidation.SeatOrNoSeat(seat, nameof(seat));
            Seat = seat;
            RiverPosition = riverPosition;
        }

        public uint TileId { get; }

        /// <summary>Empty when the source projection did not carry a stable area code.</summary>
        public string Area { get; }

        /// <summary>NoSeat when the source projection did not carry a seat.</summary>
        public int Seat { get; }

        public uint RiverPosition { get; }
    }

    public sealed class TileMarkGroupView
    {
        private readonly ReadOnlyCollection<MarkedTileView> _tiles;

        public TileMarkGroupView(
            string groupId,
            IReadOnlyList<MarkedTileView>? tiles,
            string createdRoundId,
            int createdRoundOrdinal,
            int expiresAfterRoundOrdinal)
        {
            GroupId = ProtocolText.RequireId(groupId, nameof(groupId));
            _tiles = ProjectionPayloadValidation.Items(tiles, nameof(tiles), 12);
            for (int i = 0; i < _tiles.Count; i++)
            {
                if (_tiles[i] == null)
                {
                    throw new ArgumentException("Marked tiles cannot contain null.", nameof(tiles));
                }
            }

            CreatedRoundId = ProtocolText.RequireId(createdRoundId, nameof(createdRoundId));
            CreatedRoundOrdinal = createdRoundOrdinal;
            ExpiresAfterRoundOrdinal = expiresAfterRoundOrdinal;
        }

        public string GroupId { get; }

        public IReadOnlyList<MarkedTileView> Tiles => _tiles;

        public string CreatedRoundId { get; }

        public int CreatedRoundOrdinal { get; }

        public int ExpiresAfterRoundOrdinal { get; }
    }

    public sealed class TileMarkView
    {
        private readonly ReadOnlyCollection<uint> _selectableTileIds;
        private readonly ReadOnlyCollection<TileMarkGroupView> _groups;

        public TileMarkView(
            IReadOnlyList<uint>? selectableTileIds,
            IReadOnlyList<TileMarkGroupView>? groups)
        {
            _selectableTileIds = ProjectionPayloadValidation.Tiles(selectableTileIds, nameof(selectableTileIds), 14, allowEmpty: true);
            _groups = ProjectionPayloadValidation.Items(groups, nameof(groups), 12);
            for (int i = 0; i < _groups.Count; i++)
            {
                if (_groups[i] == null)
                {
                    throw new ArgumentException("Mark groups cannot contain null.", nameof(groups));
                }
            }
        }

        public IReadOnlyList<uint> SelectableTileIds => _selectableTileIds;

        public IReadOnlyList<TileMarkGroupView> Groups => _groups;
    }

    public sealed class BeamSwapCandidateView
    {
        public BeamSwapCandidateView(uint slot, ProjectionBeamSwapSlotKind slotKind, uint slotIndex, uint? tileId)
        {
            if (slotKind == ProjectionBeamSwapSlotKind.Unspecified)
            {
                throw new ArgumentException("Beam slot kind must be explicit.", nameof(slotKind));
            }

            Slot = slot;
            SlotKind = slotKind;
            SlotIndex = slotIndex;
            TileId = tileId.HasValue ? ProjectionPayloadValidation.Tile(tileId.Value, nameof(tileId)) : (uint?)null;
        }

        /// <summary>Legacy numeric slot field; it carries <see cref="SlotIndex"/>.</summary>
        public uint Slot { get; }

        public ProjectionBeamSwapSlotKind SlotKind { get; }

        public uint SlotIndex { get; }

        public uint? TileId { get; }
    }

    public sealed class BeamSwapView
    {
        private readonly ReadOnlyCollection<BeamSwapCandidateView> _candidates;
        private readonly ReadOnlyCollection<uint> _selectableHandTileIds;

        public BeamSwapView(
            string offerId,
            IReadOnlyList<BeamSwapCandidateView>? candidates,
            IReadOnlyList<uint>? selectableHandTileIds,
            bool hasDeadline,
            long deadlineRemainingMs,
            uint drawnTileId)
        {
            OfferId = ProtocolText.RequireId(offerId, nameof(offerId));
            _candidates = ProjectionPayloadValidation.Items(candidates, nameof(candidates), 16);
            if (_candidates.Count == 0)
            {
                throw new ArgumentException("A beam-swap offer requires at least one candidate.", nameof(candidates));
            }

            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i] == null)
                {
                    throw new ArgumentException("Beam candidates cannot contain null.", nameof(candidates));
                }
            }

            _selectableHandTileIds = ProjectionPayloadValidation.Tiles(selectableHandTileIds, nameof(selectableHandTileIds), 14, allowEmpty: true);
            if (deadlineRemainingMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deadlineRemainingMs), deadlineRemainingMs, "A remaining deadline cannot be negative.");
            }

            if (!hasDeadline && deadlineRemainingMs != 0)
            {
                throw new ArgumentException("A remaining deadline requires HasDeadline.", nameof(deadlineRemainingMs));
            }

            HasDeadline = hasDeadline;
            DeadlineRemainingMs = deadlineRemainingMs;
            DrawnTileId = ProjectionPayloadValidation.Tile(drawnTileId, nameof(drawnTileId));
        }

        public string OfferId { get; }

        public IReadOnlyList<BeamSwapCandidateView> Candidates => _candidates;

        public IReadOnlyList<uint> SelectableHandTileIds => _selectableHandTileIds;

        /// <summary>False when the source offer carries no deadline.</summary>
        public bool HasDeadline { get; }

        public long DeadlineRemainingMs { get; }

        public uint DrawnTileId { get; }
    }

    /// <summary>
    /// Preparation/see-through detail. The P3.2a owner projection carries only a boolean, so
    /// the contract mapper must leave this null rather than invent target/cost values.
    /// </summary>
    public sealed class ShufflePreparationView
    {
        public ShufflePreparationView(
            uint target,
            bool seeThrough,
            bool confirmed,
            uint reservedPoints,
            uint availablePoints,
            bool seeThroughEnabled,
            uint seeThroughCost,
            long deadlineRemainingMs)
        {
            if (deadlineRemainingMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deadlineRemainingMs), deadlineRemainingMs, "A remaining deadline cannot be negative.");
            }

            Target = target;
            SeeThrough = seeThrough;
            Confirmed = confirmed;
            ReservedPoints = reservedPoints;
            AvailablePoints = availablePoints;
            SeeThroughEnabled = seeThroughEnabled;
            SeeThroughCost = seeThroughCost;
            DeadlineRemainingMs = deadlineRemainingMs;
        }

        public uint Target { get; }

        public bool SeeThrough { get; }

        public bool Confirmed { get; }

        public uint ReservedPoints { get; }

        public uint AvailablePoints { get; }

        public bool SeeThroughEnabled { get; }

        public uint SeeThroughCost { get; }

        public long DeadlineRemainingMs { get; }
    }

    /// <summary>
    /// The complete type-specific body of one projected event. Instances are created only
    /// through the per-type factories, so a field that a type does not own cannot be smuggled
    /// in and a required field cannot silently fall back to 0/empty.
    /// </summary>
    public sealed class ProjectedEventPayload
    {
        private readonly ReadOnlyCollection<uint> _tileIds;
        private readonly ReadOnlyCollection<YakuView> _yaku;
        private readonly ReadOnlyCollection<int> _tenpaiPlayers;
        private readonly ReadOnlyCollection<PaymentView> _payments;
        private readonly ReadOnlyCollection<ScoreView> _newScores;
        private readonly ReadOnlyCollection<ScoreView> _finalScores;

        private ProjectedEventPayload(
            ProtocolEventType type,
            uint turnNumber,
            int seat,
            IReadOnlyList<uint>? tileIds,
            MeldView? meld,
            ProtocolMeldType? kanType,
            int fromSeat,
            bool riichiDiscard,
            bool tsumogiri,
            uint wallRemaining,
            uint? discardTileId,
            bool doubleRiichi,
            int? loserSeat,
            bool isChankan,
            IReadOnlyList<YakuView>? yaku,
            uint han,
            uint fu,
            long score,
            ScoreBreakdownView? breakdown,
            bool isUraDora,
            bool uraAuthorized,
            long? cheatAmount,
            long? cheatBalance,
            string? reasonCode,
            IReadOnlyList<int>? tenpaiPlayers,
            IReadOnlyList<PaymentView>? payments,
            IReadOnlyList<ScoreView>? newScores,
            IReadOnlyList<ScoreView>? finalScores,
            int? winnerSeat,
            uint roundWind,
            uint roundNumber,
            string? offerId,
            bool keptFirst,
            uint? resultTileId,
            ProjectionBeamSwapResolution? beamSwapResolution,
            uint? handTileId,
            uint? targetSlot,
            uint wallSize)
        {
            Type = type;
            TurnNumber = turnNumber;
            Seat = seat;
            _tileIds = ProjectionPayloadValidation.Tiles(tileIds, nameof(tileIds), 4, allowEmpty: true);
            Meld = meld;
            KanType = kanType;
            FromSeat = fromSeat;
            RiichiDiscard = riichiDiscard;
            Tsumogiri = tsumogiri;
            WallRemaining = wallRemaining;
            DiscardTileId = discardTileId;
            DoubleRiichi = doubleRiichi;
            LoserSeat = loserSeat;
            IsChankan = isChankan;
            _yaku = ProjectionPayloadValidation.Items(yaku, nameof(yaku), 64);
            for (int i = 0; i < _yaku.Count; i++)
            {
                if (_yaku[i] == null)
                {
                    throw new ArgumentException("Yaku cannot contain null.", nameof(yaku));
                }
            }

            Han = han;
            Fu = fu;
            Score = score;
            Breakdown = breakdown;
            IsUraDora = isUraDora;
            UraAuthorized = uraAuthorized;
            CheatAmount = cheatAmount;
            CheatBalance = cheatBalance;
            ReasonCode = reasonCode ?? string.Empty;
            _tenpaiPlayers = ProjectionPayloadValidation.Seats(tenpaiPlayers, nameof(tenpaiPlayers), 4);
            _payments = ProjectionPayloadValidation.Items(payments, nameof(payments), 8);
            for (int i = 0; i < _payments.Count; i++)
            {
                if (_payments[i] == null)
                {
                    throw new ArgumentException("Payments cannot contain null.", nameof(payments));
                }
            }

            _newScores = ProjectionPayloadValidation.ScoreRows(newScores, nameof(newScores));
            _finalScores = ProjectionPayloadValidation.ScoreRows(finalScores, nameof(finalScores));
            WinnerSeat = winnerSeat;
            RoundWind = roundWind;
            RoundNumber = roundNumber;
            OfferId = ProjectionPayloadValidation.OptionalId(offerId);
            KeptFirst = keptFirst;
            ResultTileId = resultTileId;
            BeamSwapResolution = beamSwapResolution;
            HandTileId = handTileId;
            TargetSlot = targetSlot;
            WallSize = wallSize;
        }

        public ProtocolEventType Type { get; }

        public uint TurnNumber { get; }

        public int Seat { get; }

        public IReadOnlyList<uint> TileIds => _tileIds;

        public MeldView? Meld { get; }

        /// <summary>Explicit kan type when the meld body is redacted on a public stream.</summary>
        public ProtocolMeldType? KanType { get; }

        public int FromSeat { get; }

        public bool RiichiDiscard { get; }

        public bool Tsumogiri { get; }

        public uint WallRemaining { get; }

        public uint? DiscardTileId { get; }

        public bool DoubleRiichi { get; }

        public int? LoserSeat { get; }

        public bool IsChankan { get; }

        public IReadOnlyList<YakuView> Yaku => _yaku;

        public uint Han { get; }

        public uint Fu { get; }

        public long Score { get; }

        public ScoreBreakdownView? Breakdown { get; }

        public bool IsUraDora { get; }

        public bool UraAuthorized { get; }

        public long? CheatAmount { get; }

        public long? CheatBalance { get; }

        public string ReasonCode { get; }

        public IReadOnlyList<int> TenpaiPlayers => _tenpaiPlayers;

        public IReadOnlyList<PaymentView> Payments => _payments;

        public IReadOnlyList<ScoreView> NewScores => _newScores;

        public IReadOnlyList<ScoreView> FinalScores => _finalScores;

        public int? WinnerSeat { get; }

        public uint RoundWind { get; }

        public uint RoundNumber { get; }

        public string OfferId { get; }

        public bool KeptFirst { get; }

        public uint? ResultTileId { get; }

        public ProjectionBeamSwapResolution? BeamSwapResolution { get; }

        public uint? HandTileId { get; }

        public uint? TargetSlot { get; }

        public uint WallSize { get; }

        public static ProjectedEventPayload StackingStarted(uint turnNumber)
        {
            return New(ProtocolEventType.StackingStarted, turnNumber);
        }

        public static ProjectedEventPayload WallBuilt(uint turnNumber, uint wallSize)
        {
            return New(ProtocolEventType.WallBuilt, turnNumber, wallSize: wallSize);
        }

        public static ProjectedEventPayload TileDealt(uint turnNumber, int player, uint tileId)
        {
            return New(ProtocolEventType.TileDealt, turnNumber, player, new[] { ProjectionPayloadValidation.Tile(tileId, nameof(tileId)) });
        }

        public static ProjectedEventPayload DealingComplete(uint turnNumber)
        {
            return New(ProtocolEventType.DealingComplete, turnNumber);
        }

        public static ProjectedEventPayload TileDrawn(uint turnNumber, int player, uint tileId, uint wallRemaining)
        {
            return New(
                ProtocolEventType.TileDrawn,
                turnNumber,
                player,
                new[] { ProjectionPayloadValidation.Tile(tileId, nameof(tileId)) },
                wallRemaining: wallRemaining);
        }

        public static ProjectedEventPayload TileDiscarded(
            uint turnNumber,
            int player,
            uint tileId,
            bool riichiDiscard,
            bool tsumogiri)
        {
            return New(
                ProtocolEventType.TileDiscarded,
                turnNumber,
                player,
                new[] { ProjectionPayloadValidation.Tile(tileId, nameof(tileId)) },
                riichiDiscard: riichiDiscard,
                tsumogiri: tsumogiri);
        }

        public static ProjectedEventPayload ChiDeclared(uint turnNumber, int player, MeldView meld, int fromPlayer)
        {
            return MeldEvent(ProtocolEventType.ChiDeclared, turnNumber, player, meld, fromPlayer);
        }

        public static ProjectedEventPayload PonDeclared(uint turnNumber, int player, MeldView meld, int fromPlayer)
        {
            return MeldEvent(ProtocolEventType.PonDeclared, turnNumber, player, meld, fromPlayer);
        }

        public static ProjectedEventPayload KanDeclared(
            uint turnNumber,
            int player,
            MeldView? meld,
            ProtocolMeldType kanType)
        {
            if (kanType != ProtocolMeldType.OpenKan &&
                kanType != ProtocolMeldType.ClosedKan &&
                kanType != ProtocolMeldType.AddedKan)
            {
                throw new ArgumentException("KanDeclared requires an open, closed or added kan type.", nameof(kanType));
            }

            if (meld != null && meld.Type != kanType)
            {
                throw new ArgumentException("The meld type must match the explicit kan type.", nameof(meld));
            }

            ProjectionPayloadValidation.Seat(player, nameof(player));
            return New(
                ProtocolEventType.KanDeclared,
                turnNumber,
                player,
                meld == null ? null : meld.TileIds,
                meld: meld,
                kanType: kanType,
                fromSeat: ProtocolConstants.NoSeat);
        }

        public static ProjectedEventPayload RinshanDraw(uint turnNumber, int player, uint tileId)
        {
            return New(
                ProtocolEventType.RinshanDraw,
                turnNumber,
                player,
                new[] { ProjectionPayloadValidation.Tile(tileId, nameof(tileId)) });
        }

        public static ProjectedEventPayload RiichiDeclared(
            uint turnNumber,
            int player,
            uint discardTileId,
            bool doubleRiichi)
        {
            ProjectionPayloadValidation.Seat(player, nameof(player));
            return New(
                ProtocolEventType.RiichiDeclared,
                turnNumber,
                player,
                discardTileId: ProjectionPayloadValidation.Tile(discardTileId, nameof(discardTileId)),
                doubleRiichi: doubleRiichi);
        }

        public static ProjectedEventPayload TsumoWin(
            uint turnNumber,
            int player,
            uint winTileId,
            IReadOnlyList<YakuView>? yaku,
            uint han,
            uint fu,
            long score,
            ScoreBreakdownView? breakdown)
        {
            ProjectionPayloadValidation.Seat(player, nameof(player));
            return New(
                ProtocolEventType.TsumoWin,
                turnNumber,
                player,
                new[] { ProjectionPayloadValidation.Tile(winTileId, nameof(winTileId)) },
                yaku: yaku,
                han: han,
                fu: fu,
                score: score,
                breakdown: breakdown);
        }

        public static ProjectedEventPayload RonWin(
            uint turnNumber,
            int winner,
            int loser,
            uint winTileId,
            IReadOnlyList<YakuView>? yaku,
            uint han,
            uint fu,
            long score,
            ScoreBreakdownView? breakdown,
            bool isChankan)
        {
            ProjectionPayloadValidation.Seat(winner, nameof(winner));
            ProjectionPayloadValidation.Seat(loser, nameof(loser));
            if (winner == loser)
            {
                throw new ArgumentException("A ron winner and loser must be different seats.", nameof(loser));
            }

            return New(
                ProtocolEventType.RonWin,
                turnNumber,
                winner,
                new[] { ProjectionPayloadValidation.Tile(winTileId, nameof(winTileId)) },
                loserSeat: loser,
                isChankan: isChankan,
                yaku: yaku,
                han: han,
                fu: fu,
                score: score,
                breakdown: breakdown);
        }

        public static ProjectedEventPayload DoraRevealed(
            uint turnNumber,
            uint indicatorTileId,
            bool isUraDora,
            bool uraAuthorized)
        {
            if (uraAuthorized && !isUraDora)
            {
                throw new ArgumentException("Only an ura dora reveal can be ura-authorized.", nameof(uraAuthorized));
            }

            return New(
                ProtocolEventType.DoraRevealed,
                turnNumber,
                ProtocolConstants.NoSeat,
                new[] { ProjectionPayloadValidation.Tile(indicatorTileId, nameof(indicatorTileId)) },
                isUraDora: isUraDora,
                uraAuthorized: uraAuthorized);
        }

        public static ProjectedEventPayload CheatPointsChanged(
            uint turnNumber,
            int seat,
            long amount,
            long newBalance)
        {
            ProjectionPayloadValidation.Seat(seat, nameof(seat));
            if (newBalance < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newBalance), newBalance, "A cheat balance cannot be negative.");
            }

            return New(
                ProtocolEventType.CheatPointsChanged,
                turnNumber,
                seat,
                cheatAmount: amount,
                cheatBalance: newBalance);
        }

        public static ProjectedEventPayload DrawGame(
            uint turnNumber,
            string reason,
            IReadOnlyList<int>? tenpaiPlayers)
        {
            if (!ProtocolErrorCode.IsWellFormed(reason))
            {
                throw new ArgumentException("Draw reason must be a stable uppercase code.", nameof(reason));
            }

            return New(
                ProtocolEventType.DrawGame,
                turnNumber,
                ProtocolConstants.NoSeat,
                reasonCode: reason,
                tenpaiPlayers: tenpaiPlayers);
        }

        public static ProjectedEventPayload ScoreSettled(
            uint turnNumber,
            IReadOnlyList<PaymentView> payments,
            IReadOnlyList<ScoreView> newScores)
        {
            if (payments == null)
            {
                throw new ArgumentNullException(nameof(payments));
            }

            if (newScores == null)
            {
                throw new ArgumentNullException(nameof(newScores));
            }

            return New(
                ProtocolEventType.ScoreSettled,
                turnNumber,
                ProtocolConstants.NoSeat,
                payments: payments,
                newScores: newScores);
        }

        public static ProjectedEventPayload RoundEnded(uint turnNumber, uint roundWind, uint roundNumber)
        {
            return New(
                ProtocolEventType.RoundEnded,
                turnNumber,
                ProtocolConstants.NoSeat,
                roundWind: roundWind,
                roundNumber: roundNumber);
        }

        public static ProjectedEventPayload MatchEnded(
            uint turnNumber,
            IReadOnlyList<ScoreView> finalScores,
            int winner)
        {
            if (finalScores == null)
            {
                throw new ArgumentNullException(nameof(finalScores));
            }

            if (finalScores.Count != 0 && finalScores.Count != 4)
            {
                throw new ArgumentException("Final scores must be empty or four rows.", nameof(finalScores));
            }

            ProjectionPayloadValidation.Seat(winner, nameof(winner));
            return New(
                ProtocolEventType.MatchEnded,
                turnNumber,
                ProtocolConstants.NoSeat,
                finalScores: finalScores,
                winnerSeat: winner);
        }

        public static ProjectedEventPayload SleeveBloomResolved(
            int ownerSeat,
            string offerId,
            bool keptFirst,
            uint? resultTileId)
        {
            ProjectionPayloadValidation.Seat(ownerSeat, nameof(ownerSeat));
            string id = ProtocolText.RequireId(offerId, nameof(offerId));
            if (resultTileId.HasValue)
            {
                ProjectionPayloadValidation.Tile(resultTileId.Value, nameof(resultTileId));
            }

            return New(
                ProtocolEventType.SleeveBloomResolved,
                0,
                ownerSeat,
                offerId: id,
                keptFirst: keptFirst,
                resultTileId: resultTileId);
        }

        public static ProjectedEventPayload BeamSwapResolved(
            int ownerSeat,
            string offerId,
            ProjectionBeamSwapResolution resolution,
            uint? handTileId,
            uint? targetSlot)
        {
            if (resolution == ProjectionBeamSwapResolution.Unspecified)
            {
                throw new ArgumentException("Beam-swap resolution must be explicit.", nameof(resolution));
            }

            ProjectionPayloadValidation.Seat(ownerSeat, nameof(ownerSeat));
            string id = ProtocolText.RequireId(offerId, nameof(offerId));
            if (handTileId.HasValue)
            {
                ProjectionPayloadValidation.Tile(handTileId.Value, nameof(handTileId));
            }

            return New(
                ProtocolEventType.BeamSwapResolved,
                0,
                ownerSeat,
                offerId: id,
                beamSwapResolution: resolution,
                handTileId: handTileId,
                targetSlot: targetSlot);
        }

        private static ProjectedEventPayload MeldEvent(
            ProtocolEventType type,
            uint turnNumber,
            int player,
            MeldView meld,
            int fromPlayer)
        {
            if (meld == null)
            {
                throw new ArgumentNullException(nameof(meld));
            }

            ProjectionPayloadValidation.Seat(player, nameof(player));
            ProjectionPayloadValidation.SeatOrNoSeat(fromPlayer, nameof(fromPlayer));
            if (meld.TileIds.Count != 3)
            {
                throw new ArgumentException("Chi/Pon requires exactly three meld tiles.", nameof(meld));
            }

            return New(
                type,
                turnNumber,
                player,
                meld.TileIds,
                meld: meld,
                fromSeat: fromPlayer);
        }

        private static ProjectedEventPayload New(
            ProtocolEventType type,
            uint turnNumber,
            int seat = ProtocolConstants.NoSeat,
            IReadOnlyList<uint>? tileIds = null,
            MeldView? meld = null,
            ProtocolMeldType? kanType = null,
            int fromSeat = ProtocolConstants.NoSeat,
            bool riichiDiscard = false,
            bool tsumogiri = false,
            uint wallRemaining = 0,
            uint? discardTileId = null,
            bool doubleRiichi = false,
            int? loserSeat = null,
            bool isChankan = false,
            IReadOnlyList<YakuView>? yaku = null,
            uint han = 0,
            uint fu = 0,
            long score = 0,
            ScoreBreakdownView? breakdown = null,
            bool isUraDora = false,
            bool uraAuthorized = false,
            long? cheatAmount = null,
            long? cheatBalance = null,
            string? reasonCode = null,
            IReadOnlyList<int>? tenpaiPlayers = null,
            IReadOnlyList<PaymentView>? payments = null,
            IReadOnlyList<ScoreView>? newScores = null,
            IReadOnlyList<ScoreView>? finalScores = null,
            int? winnerSeat = null,
            uint roundWind = 0,
            uint roundNumber = 0,
            string? offerId = null,
            bool keptFirst = false,
            uint? resultTileId = null,
            ProjectionBeamSwapResolution? beamSwapResolution = null,
            uint? handTileId = null,
            uint? targetSlot = null,
            uint wallSize = 0)
        {
            if (type == ProtocolEventType.Unspecified || !ProtocolEventPolicy.IsKnownEventType((uint)type))
            {
                throw new ProjectionPayloadException("Event type " + (uint)type + " is not a frozen V5 event type.");
            }

            if (seat != ProtocolConstants.NoSeat)
            {
                ProjectionPayloadValidation.Seat(seat, nameof(seat));
            }

            if (loserSeat.HasValue)
            {
                ProjectionPayloadValidation.Seat(loserSeat.Value, nameof(loserSeat));
            }

            if (winnerSeat.HasValue)
            {
                ProjectionPayloadValidation.Seat(winnerSeat.Value, nameof(winnerSeat));
            }

            if (cheatBalance.HasValue && cheatBalance.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cheatBalance), cheatBalance.Value, "A cheat balance cannot be negative.");
            }

            return new ProjectedEventPayload(
                type,
                turnNumber,
                seat,
                tileIds,
                meld,
                kanType,
                fromSeat,
                riichiDiscard,
                tsumogiri,
                wallRemaining,
                discardTileId,
                doubleRiichi,
                loserSeat,
                isChankan,
                yaku,
                han,
                fu,
                score,
                breakdown,
                isUraDora,
                uraAuthorized,
                cheatAmount,
                cheatBalance,
                reasonCode,
                tenpaiPlayers,
                payments,
                newScores,
                finalScores,
                winnerSeat,
                roundWind,
                roundNumber,
                offerId,
                keptFirst,
                resultTileId,
                beamSwapResolution,
                handTileId,
                targetSlot,
                wallSize);
        }
    }

    internal static class ProjectionPayloadValidation
    {
        internal static uint Tile(uint value, string name)
        {
            if (value > 135)
            {
                throw new ArgumentOutOfRangeException(name, value, "Tile id must be 0..135.");
            }

            return value;
        }

        internal static int Seat(int value, string name)
        {
            if (value < ProtocolConstants.MinSeat || value > ProtocolConstants.MaxSeat)
            {
                throw new ArgumentOutOfRangeException(name, value, "Seat must be 0..3.");
            }

            return value;
        }

        internal static int SeatOrNoSeat(int value, string name)
        {
            if (value != ProtocolConstants.NoSeat)
            {
                Seat(value, name);
            }

            return value;
        }

        internal static string OptionalId(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return ProtocolText.RequireId(value, nameof(value));
        }

        internal static ReadOnlyCollection<T> Items<T>(IReadOnlyList<T>? values, string name, int maxCount)
            where T : class
        {
            if (values == null)
            {
                return new ReadOnlyCollection<T>(new List<T>());
            }

            if (values.Count > maxCount)
            {
                throw new ArgumentException(name + " exceeds " + maxCount + " entries.", name);
            }

            var copy = new List<T>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                copy.Add(values[i]);
            }

            return new ReadOnlyCollection<T>(copy);
        }

        internal static ReadOnlyCollection<uint> Tiles(IReadOnlyList<uint>? values, string name, int maxCount, bool allowEmpty)
        {
            if (values == null)
            {
                if (!allowEmpty)
                {
                    throw new ArgumentNullException(name);
                }

                return new ReadOnlyCollection<uint>(new List<uint>());
            }

            if (values.Count > maxCount)
            {
                throw new ArgumentException(name + " exceeds " + maxCount + " entries.", name);
            }

            var copy = new List<uint>(values.Count);
            var seen = new HashSet<uint>();
            for (int i = 0; i < values.Count; i++)
            {
                uint tile = Tile(values[i], name);
                if (!seen.Add(tile))
                {
                    throw new ArgumentException(name + " contains duplicate tile ids.", name);
                }

                copy.Add(tile);
            }

            return new ReadOnlyCollection<uint>(copy);
        }

        internal static ReadOnlyCollection<int> Seats(IReadOnlyList<int>? values, string name, int maxCount)
        {
            if (values == null)
            {
                return new ReadOnlyCollection<int>(new List<int>());
            }

            if (values.Count > maxCount)
            {
                throw new ArgumentException(name + " exceeds " + maxCount + " entries.", name);
            }

            var copy = new List<int>(values.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < values.Count; i++)
            {
                int seat = Seat(values[i], name);
                if (!seen.Add(seat))
                {
                    throw new ArgumentException(name + " contains a duplicate seat.", name);
                }

                copy.Add(seat);
            }

            return new ReadOnlyCollection<int>(copy);
        }

        internal static ReadOnlyCollection<ScoreView> ScoreRows(IReadOnlyList<ScoreView>? values, string name)
        {
            if (values == null)
            {
                return new ReadOnlyCollection<ScoreView>(new List<ScoreView>());
            }

            if (values.Count != 0 && values.Count != 4)
            {
                throw new ArgumentException(name + " must contain zero or four rows.", name);
            }

            var copy = new List<ScoreView>(values.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < values.Count; i++)
            {
                ScoreView row = values[i] ?? throw new ArgumentException(name + " cannot contain null.", name);
                if (!seen.Add(row.Seat))
                {
                    throw new ArgumentException(name + " contains a duplicate seat.", name);
                }

                copy.Add(row);
            }

            return new ReadOnlyCollection<ScoreView>(copy);
        }
    }
}
