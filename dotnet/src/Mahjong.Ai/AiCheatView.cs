using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Seat-scoped, immutable private facts an AI seat is authorized to see (the safe V5
    /// replacement for V4 <c>com.majiang.ai.AiCheatView</c>). V4 wrapped live projection
    /// view objects (<c>ShufflePreparationView</c>, <c>TileMarkView</c>,
    /// <c>SleeveBloomView</c>, <c>BeamSwapView</c>) and relied on <c>deepCopy()</c> at the
    /// boundary. This port carries only the fields an AI decision actually consumes, in
    /// small read-only fact types defined in this file:
    /// <list type="bullet">
    /// <item><description><see cref="AiShufflePreparationFacts"/>: the off-turn stacking
    /// preparation panel (target, see-through, reservation, timestamps);</description></item>
    /// <item><description><see cref="AiTileMarkFacts"/>: owner-visible mark groups and the
    /// currently selectable own-hand physical tiles;</description></item>
    /// <item><description><see cref="AiSleeveBloomFacts"/>: own immediate/off-turn
    /// eligibility, the frozen two-tile offer and an already reserved tile;</description></item>
    /// <item><description><see cref="AiBeamSwapFacts"/>: own beam-swap eligibility and the
    /// frozen offer whose slot candidates are already seat-filtered.</description></item>
    /// </list>
    ///
    /// Deliberately absent: <c>GameState</c>, <c>PlayerView</c>, <c>PlayerDecision</c>,
    /// <c>TilePositionMap</c>, wall order/composition, another seat's full closed hand,
    /// seeds, authority references, callbacks or submit methods. The table runtime's P3.2
    /// projection remains responsible for populating these facts from authoritative state;
    /// this type cannot reach that state and copies every supplied collection, including
    /// every nested list, so a later mutation of a caller-owned list or dictionary cannot
    /// change a frozen snapshot.
    ///
    /// V5 safety tightening (documented as a non-equivalence where observable): the V4
    /// views accepted several structurally inconsistent values (for example a beam candidate
    /// marked without a tile, a discard-area mark without a river position, duplicate
    /// physical tiles in a selectable set, or a negative <c>serverNowMillis</c>). Those are
    /// rejected here. Valid V4 values remain representable.
    /// </summary>
    public sealed class AiCheatView
    {
        /// <summary>Creates the empty private view; this is the V4 <c>AiCheatView()</c> default.</summary>
        public AiCheatView(
            int cheatPoints = 0,
            AiShufflePreparationFacts? preparation = null,
            AiTileMarkFacts? tileMark = null,
            AiSleeveBloomFacts? sleeveBloom = null,
            AiBeamSwapFacts? beamSwap = null)
        {
            AiFrozenGuard.CheckNonNegative(cheatPoints, nameof(cheatPoints));
            CheatPoints = cheatPoints;
            Preparation = preparation?.DeepCopy();
            TileMark = tileMark?.DeepCopy();
            SleeveBloom = sleeveBloom?.DeepCopy();
            BeamSwap = beamSwap?.DeepCopy();
        }

        /// <summary>Own cheat-point balance; V4 required non-negative.</summary>
        public int CheatPoints { get; }

        /// <summary>Off-turn stacking preparation facts, or null when the panel is absent.</summary>
        public AiShufflePreparationFacts? Preparation { get; }

        /// <summary>Owner-visible tile-mark facts, or null when tile mark is disabled.</summary>
        public AiTileMarkFacts? TileMark { get; }

        /// <summary>Owner-visible sleeve-bloom facts, or null when disabled.</summary>
        public AiSleeveBloomFacts? SleeveBloom { get; }

        /// <summary>Owner-visible beam-swap facts, or null when disabled.</summary>
        public AiBeamSwapFacts? BeamSwap { get; }

        /// <summary>True when at least one private technique fact is attached.</summary>
        public bool HasPrivateFacts =>
            Preparation != null || TileMark != null || SleeveBloom != null || BeamSwap != null;

        /// <summary>
        /// Independent copy. Every nested fact type is immutable and already owns its
        /// collections, so the copy is constructed through the same validating constructors
        /// and no writable collection or caller-owned instance is shared.
        /// </summary>
        public AiCheatView DeepCopy()
        {
            return new AiCheatView(CheatPoints, Preparation, TileMark, SleeveBloom, BeamSwap);
        }
    }

    /// <summary>V4 <c>ShufflePreparationStatus</c>: selecting a plan or generating the wall.</summary>
    public enum AiShufflePreparationStatus : byte
    {
        Selecting = 0,
        Generating = 1
    }

    /// <summary>V4 <c>ShuffleTarget</c>: the six supported unified stacking targets.</summary>
    public enum AiShuffleTarget : byte
    {
        ChinitsuMan = 0,
        ChinitsuSou = 1,
        ChinitsuPin = 2,
        Daisangen = 3,
        Kokushi = 4,
        Daisuushii = 5
    }

    /// <summary>
    /// V4 <c>ShuffleSelection</c> reduced to pure facts. The target enum and see-through
    /// flag are the complete payload; cost and activity are derived exactly as V4 does
    /// (target 30 + see-through 10, nullable target means "skip").
    /// </summary>
    public sealed class AiShuffleSelectionFacts
    {
        public AiShuffleSelectionFacts(AiShuffleTarget? target = null, bool seeThrough = false)
        {
            if (target.HasValue)
            {
                AiFrozenGuard.CheckDefined(target.Value, nameof(target));
            }

            Target = target;
            SeeThrough = seeThrough;
        }

        public AiShuffleTarget? Target { get; }

        public bool SeeThrough { get; }

        public bool IsActive => Target.HasValue || SeeThrough;

        public int Cost => (Target.HasValue ? 30 : 0) + (SeeThrough ? 10 : 0);

        public AiShuffleSelectionFacts DeepCopy()
        {
            return new AiShuffleSelectionFacts(Target, SeeThrough);
        }
    }

    /// <summary>
    /// V4 <c>ShufflePreparationView</c> as immutable facts. V4 validated a non-blank round
    /// id, non-negative open time, deadline &gt;= open time, non-negative reserved/available
    /// points and a fixed see-through cost of 10. The same rules are enforced here; the
    /// additional non-negative server-now check is a V5 tightening that removes a negative
    /// display timestamp V4 tolerated.
    /// </summary>
    public sealed class AiShufflePreparationFacts
    {
        public AiShufflePreparationFacts(
            string roundId,
            AiShufflePreparationStatus status,
            long opensAtMillis,
            long deadlineMillis,
            long serverNowMillis,
            bool confirmed,
            AiShuffleSelectionFacts? selection,
            int reservedPoints,
            int availablePoints,
            bool seeThroughEnabled = false,
            int seeThroughCost = 10)
        {
            AiFrozenGuard.RequireText(roundId, nameof(roundId));
            AiFrozenGuard.CheckDefined(status, nameof(status));
            AiFrozenGuard.CheckNonNegative(opensAtMillis, nameof(opensAtMillis));
            if (deadlineMillis < opensAtMillis)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(deadlineMillis),
                    deadlineMillis,
                    "deadlineMillis must be >= opensAtMillis.");
            }

            AiFrozenGuard.CheckNonNegative(serverNowMillis, nameof(serverNowMillis));
            AiFrozenGuard.CheckNonNegative(reservedPoints, nameof(reservedPoints));
            AiFrozenGuard.CheckNonNegative(availablePoints, nameof(availablePoints));
            if (seeThroughCost != 10)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(seeThroughCost),
                    seeThroughCost,
                    "seeThroughCost must be 10.");
            }

            RoundId = roundId;
            Status = status;
            OpensAtMillis = opensAtMillis;
            DeadlineMillis = deadlineMillis;
            ServerNowMillis = serverNowMillis;
            Confirmed = confirmed;
            Selection = selection?.DeepCopy();
            ReservedPoints = reservedPoints;
            AvailablePoints = availablePoints;
            SeeThroughEnabled = seeThroughEnabled;
            SeeThroughCost = seeThroughCost;
        }

        public string RoundId { get; }

        public AiShufflePreparationStatus Status { get; }

        public long OpensAtMillis { get; }

        public long DeadlineMillis { get; }

        public long ServerNowMillis { get; }

        public bool Confirmed { get; }

        public AiShuffleSelectionFacts? Selection { get; }

        public int ReservedPoints { get; }

        public int AvailablePoints { get; }

        public bool SeeThroughEnabled { get; }

        public int SeeThroughCost { get; }

        public AiShufflePreparationFacts DeepCopy()
        {
            return new AiShufflePreparationFacts(
                RoundId,
                Status,
                OpensAtMillis,
                DeadlineMillis,
                ServerNowMillis,
                Confirmed,
                Selection,
                ReservedPoints,
                AvailablePoints,
                SeeThroughEnabled,
                SeeThroughCost);
        }
    }

    /// <summary>V4 <c>TileMarkArea</c>: where the owner is authorized to see a marked tile.</summary>
    public enum AiTileMarkArea : byte
    {
        OwnHand = 0,
        OpponentHand = 1,
        Discard = 2,
        ExposedMeld = 3,
        ExposedIndicator = 4,
        NotVisible = 5
    }

    /// <summary>V4 <c>RiverPosition</c>: round id, river seat and the discard's index.</summary>
    public sealed class AiRiverPositionFacts
    {
        public AiRiverPositionFacts(string roundId, int seat, int discardIndex)
        {
            AiFrozenGuard.RequireText(roundId, nameof(roundId));
            AiFrozenGuard.CheckSeat(seat, nameof(seat));
            AiFrozenGuard.CheckNonNegative(discardIndex, nameof(discardIndex));
            RoundId = roundId;
            Seat = seat;
            DiscardIndex = discardIndex;
        }

        public string RoundId { get; }

        public int Seat { get; }

        public int DiscardIndex { get; }

        public AiRiverPositionFacts DeepCopy()
        {
            return new AiRiverPositionFacts(RoundId, Seat, DiscardIndex);
        }
    }

    /// <summary>
    /// V4 <c>MarkedTileView</c> as pure facts. The area/seat/river invariants are ported
    /// exactly: DISCARD requires a matching river position; own/opponent hand and exposed
    /// meld require a seat; exposed-indicator and not-visible require no seat; every other
    /// area forbids a river position. Only the physical tile id is carried because the AI
    /// already has <see cref="TileId"/>.
    /// </summary>
    public sealed class AiMarkedTileFacts
    {
        public AiMarkedTileFacts(
            int tileId,
            AiTileMarkArea area,
            int? seat = null,
            AiRiverPositionFacts? riverPosition = null)
        {
            if (tileId < 0 || tileId > Mahjong.Domain.TileId.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(tileId), tileId, "tileId must be in 0..135.");
            }

            AiFrozenGuard.CheckDefined(area, nameof(area));
            if (area == AiTileMarkArea.Discard)
            {
                if (!seat.HasValue)
                {
                    throw new ArgumentException("seat is required for DISCARD.", nameof(seat));
                }

                AiFrozenGuard.CheckSeat(seat.Value, nameof(seat));
                if (riverPosition == null)
                {
                    throw new ArgumentNullException(nameof(riverPosition));
                }

                if (riverPosition.Seat != seat.Value)
                {
                    throw new ArgumentException(
                        "riverPosition.Seat must match seat.",
                        nameof(riverPosition));
                }
            }
            else
            {
                if (riverPosition != null)
                {
                    throw new ArgumentException(
                        "riverPosition must be null when area is not DISCARD.",
                        nameof(riverPosition));
                }

                bool needsSeat = area == AiTileMarkArea.OwnHand ||
                                 area == AiTileMarkArea.OpponentHand ||
                                 area == AiTileMarkArea.ExposedMeld;
                if (needsSeat)
                {
                    if (!seat.HasValue)
                    {
                        throw new ArgumentException("seat is required for " + area + ".", nameof(seat));
                    }

                    AiFrozenGuard.CheckSeat(seat.Value, nameof(seat));
                }
                else if (seat.HasValue)
                {
                    throw new ArgumentException("seat must be null for " + area + ".", nameof(seat));
                }
            }

            TileId = tileId;
            Area = area;
            Seat = seat;
            RiverPosition = riverPosition?.DeepCopy();
        }

        public int TileId { get; }

        public AiTileMarkArea Area { get; }

        public int? Seat { get; }

        public AiRiverPositionFacts? RiverPosition { get; }

        public AiMarkedTileFacts DeepCopy()
        {
            return new AiMarkedTileFacts(TileId, Area, Seat, RiverPosition);
        }
    }

    /// <summary>
    /// V4 <c>TileMarkGroupView</c> as immutable facts: exactly four marked physical tiles,
    /// created at a dealt-round ordinal and effective through ordinal + 3. V5 additionally
    /// rejects duplicate physical tiles inside the group (the authoritative group already
    /// guarantees this; accepting duplicates would only make a malformed projection look
    /// valid).
    /// </summary>
    public sealed class AiTileMarkGroupFacts
    {
        private readonly ReadOnlyCollection<AiMarkedTileFacts> _tiles;

        public AiTileMarkGroupFacts(
            string groupId,
            string createdRoundId,
            int createdRoundOrdinal,
            int expiresAfterRoundOrdinal,
            int futureRoundsRemaining,
            bool isLastEffectiveRound,
            IReadOnlyList<AiMarkedTileFacts> tiles)
        {
            AiFrozenGuard.RequireText(groupId, nameof(groupId));
            AiFrozenGuard.RequireText(createdRoundId, nameof(createdRoundId));
            if (createdRoundOrdinal < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(createdRoundOrdinal),
                    createdRoundOrdinal,
                    "createdRoundOrdinal must be >= 1.");
            }

            if (expiresAfterRoundOrdinal != createdRoundOrdinal + 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expiresAfterRoundOrdinal),
                    expiresAfterRoundOrdinal,
                    "expiresAfterRoundOrdinal must equal createdRoundOrdinal + 3.");
            }

            AiFrozenGuard.CheckNonNegative(futureRoundsRemaining, nameof(futureRoundsRemaining));
            if (tiles == null)
            {
                throw new ArgumentNullException(nameof(tiles));
            }

            if (tiles.Count != 4)
            {
                throw new ArgumentException("A tile-mark group must contain exactly four tiles.", nameof(tiles));
            }

            var copy = new AiMarkedTileFacts[4];
            var seen = new HashSet<int>();
            for (int i = 0; i < 4; i++)
            {
                AiMarkedTileFacts tile = tiles[i] ??
                    throw new ArgumentException("Tile-mark group cannot contain null.", nameof(tiles));
                if (!seen.Add(tile.TileId))
                {
                    throw new ArgumentException("A tile-mark group cannot repeat a physical tile.", nameof(tiles));
                }

                copy[i] = tile.DeepCopy();
            }

            GroupId = groupId;
            CreatedRoundId = createdRoundId;
            CreatedRoundOrdinal = createdRoundOrdinal;
            ExpiresAfterRoundOrdinal = expiresAfterRoundOrdinal;
            FutureRoundsRemaining = futureRoundsRemaining;
            IsLastEffectiveRound = isLastEffectiveRound;
            _tiles = new ReadOnlyCollection<AiMarkedTileFacts>(copy);
        }

        public string GroupId { get; }

        public string CreatedRoundId { get; }

        public int CreatedRoundOrdinal { get; }

        public int ExpiresAfterRoundOrdinal { get; }

        public int FutureRoundsRemaining { get; }

        public bool IsLastEffectiveRound { get; }

        public IReadOnlyList<AiMarkedTileFacts> Tiles => _tiles;

        public AiTileMarkGroupFacts DeepCopy()
        {
            return new AiTileMarkGroupFacts(
                GroupId,
                CreatedRoundId,
                CreatedRoundOrdinal,
                ExpiresAfterRoundOrdinal,
                FutureRoundsRemaining,
                IsLastEffectiveRound,
                _tiles);
        }
    }

    /// <summary>
    /// V4 <c>TileMarkView</c> as immutable facts. It carries the owner's current
    /// eligibility, cost, active-mark budget, selectable own-hand physical tiles, and the
    /// (at most three) active groups. The decision/control/state identity fields are kept so
    /// <see cref="AiSeatSnapshot"/> can reject a stale projection instead of trusting it.
    /// </summary>
    public sealed class AiTileMarkFacts
    {
        private readonly ReadOnlyCollection<int> _selectableTileIds;
        private readonly ReadOnlyCollection<AiTileMarkGroupFacts> _activeGroups;

        public AiTileMarkFacts(
            bool canApply,
            string? disabledReason,
            int cost,
            int activeTileCount,
            int maxActiveTiles,
            IReadOnlyList<int> selectableTileIds,
            string decisionId,
            long controlEpoch,
            long stateVersion,
            IReadOnlyList<AiTileMarkGroupFacts> activeGroups)
        {
            if (cost <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cost), cost, "cost must be positive.");
            }

            if (maxActiveTiles != 12)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxActiveTiles),
                    maxActiveTiles,
                    "maxActiveTiles must be 12.");
            }

            if (activeTileCount < 0 || activeTileCount > 12)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(activeTileCount),
                    activeTileCount,
                    "activeTileCount must be in 0..12.");
            }

            AiFrozenGuard.RequireText(decisionId, nameof(decisionId));
            AiFrozenGuard.CheckNonNegative(controlEpoch, nameof(controlEpoch));
            AiFrozenGuard.CheckNonNegative(stateVersion, nameof(stateVersion));
            if (selectableTileIds == null)
            {
                throw new ArgumentNullException(nameof(selectableTileIds));
            }

            var selectable = new int[selectableTileIds.Count];
            var selectableSeen = new HashSet<int>();
            for (int i = 0; i < selectableTileIds.Count; i++)
            {
                int tileId = selectableTileIds[i];
                if (tileId < 0 || tileId > Mahjong.Domain.TileId.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(selectableTileIds),
                        tileId,
                        "selectableTileIds must contain physical ids in 0..135.");
                }

                if (!selectableSeen.Add(tileId))
                {
                    throw new ArgumentException(
                        "selectableTileIds cannot repeat a physical tile.",
                        nameof(selectableTileIds));
                }

                selectable[i] = tileId;
            }

            if (activeGroups == null)
            {
                throw new ArgumentNullException(nameof(activeGroups));
            }

            if (activeGroups.Count > 3)
            {
                throw new ArgumentException("At most three tile-mark groups are active.", nameof(activeGroups));
            }

            var groups = new AiTileMarkGroupFacts[activeGroups.Count];
            var groupTileIds = new HashSet<int>();
            for (int i = 0; i < activeGroups.Count; i++)
            {
                AiTileMarkGroupFacts group = activeGroups[i]?.DeepCopy() ??
                    throw new ArgumentException("activeGroups cannot contain null.", nameof(activeGroups));
                for (int t = 0; t < group.Tiles.Count; t++)
                {
                    if (!groupTileIds.Add(group.Tiles[t].TileId))
                    {
                        throw new ArgumentException(
                            "Active tile-mark groups cannot repeat a physical tile.",
                            nameof(activeGroups));
                    }
                }

                groups[i] = group;
            }

            if (activeTileCount != groups.Length * 4)
            {
                throw new ArgumentException(
                    "activeTileCount must equal four times the active group count.",
                    nameof(activeTileCount));
            }

            for (int i = 0; i < selectable.Length; i++)
            {
                if (groupTileIds.Contains(selectable[i]))
                {
                    throw new ArgumentException(
                        "selectableTileIds cannot contain an already marked physical tile.",
                        nameof(selectableTileIds));
                }
            }

            CanApply = canApply;
            DisabledReason = disabledReason;
            Cost = cost;
            ActiveTileCount = activeTileCount;
            MaxActiveTiles = maxActiveTiles;
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            StateVersion = stateVersion;
            _selectableTileIds = new ReadOnlyCollection<int>(selectable);
            _activeGroups = new ReadOnlyCollection<AiTileMarkGroupFacts>(groups);
        }

        public bool CanApply { get; }

        public string? DisabledReason { get; }

        public int Cost { get; }

        public int ActiveTileCount { get; }

        public int MaxActiveTiles { get; }

        public IReadOnlyList<int> SelectableTileIds => _selectableTileIds;

        public string DecisionId { get; }

        public long ControlEpoch { get; }

        public long StateVersion { get; }

        public IReadOnlyList<AiTileMarkGroupFacts> ActiveGroups => _activeGroups;

        public AiTileMarkFacts DeepCopy()
        {
            return new AiTileMarkFacts(
                CanApply,
                DisabledReason,
                Cost,
                ActiveTileCount,
                MaxActiveTiles,
                _selectableTileIds,
                DecisionId,
                ControlEpoch,
                StateVersion,
                _activeGroups);
        }
    }

    /// <summary>V4 <c>SleeveBloomDrawKind</c>.</summary>
    public enum AiSleeveBloomDrawKind : byte
    {
        DealExtra = 0,
        Normal = 1,
        Rinshan = 2
    }

    /// <summary>V4 <c>SleeveBloomOfferMode</c>.</summary>
    public enum AiSleeveBloomOfferMode : byte
    {
        Immediate = 0,
        OffTurn = 1
    }

    /// <summary>
    /// V4 <c>SleeveBloomOfferView</c> as immutable facts. It carries only the two frozen
    /// candidates, their drawer, deadline, identity and preview flags. V5 rejects the same
    /// structural errors as the V4 view (deal-extra pending offer; off-turn without a window
    /// or with a non-normal draw) and additionally requires the two candidates to be
    /// distinct physical tiles, which the underlying V4 offer already guaranteed.
    /// </summary>
    public sealed class AiSleeveBloomOfferFacts
    {
        public AiSleeveBloomOfferFacts(
            string offerId,
            int firstTileId,
            int secondTileId,
            AiSleeveBloomDrawKind drawKind,
            long deadlineMillis,
            string decisionId,
            long controlEpoch,
            AiSleeveBloomOfferMode mode = AiSleeveBloomOfferMode.Immediate,
            string? windowId = null,
            bool firstCanTsumo = false,
            bool secondCanTsumo = false,
            bool firstCanKan = false,
            bool secondCanKan = false)
        {
            AiFrozenGuard.RequireText(offerId, nameof(offerId));
            AiFrozenGuard.CheckTileId(firstTileId, nameof(firstTileId));
            AiFrozenGuard.CheckTileId(secondTileId, nameof(secondTileId));
            if (firstTileId == secondTileId)
            {
                throw new ArgumentException("Sleeve-bloom candidates must be distinct tiles.", nameof(secondTileId));
            }

            AiFrozenGuard.CheckDefined(drawKind, nameof(drawKind));
            AiFrozenGuard.CheckDefined(mode, nameof(mode));
            AiFrozenGuard.CheckNonNegative(deadlineMillis, nameof(deadlineMillis));
            AiFrozenGuard.RequireText(decisionId, nameof(decisionId));
            AiFrozenGuard.CheckNonNegative(controlEpoch, nameof(controlEpoch));
            if (drawKind == AiSleeveBloomDrawKind.DealExtra)
            {
                throw new ArgumentException("A pending offer cannot ride on the deal-extra draw.", nameof(drawKind));
            }

            if (mode == AiSleeveBloomOfferMode.OffTurn)
            {
                if (drawKind != AiSleeveBloomDrawKind.Normal)
                {
                    throw new ArgumentException("An off-turn offer must ride on a normal draw.", nameof(drawKind));
                }

                AiFrozenGuard.RequireText(windowId, nameof(windowId));
            }
            else if (windowId != null)
            {
                throw new ArgumentException("An immediate offer has no window id.", nameof(windowId));
            }

            OfferId = offerId;
            FirstTileId = firstTileId;
            SecondTileId = secondTileId;
            DrawKind = drawKind;
            DeadlineMillis = deadlineMillis;
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            Mode = mode;
            WindowId = windowId;
            FirstCanTsumo = firstCanTsumo;
            SecondCanTsumo = secondCanTsumo;
            FirstCanKan = firstCanKan;
            SecondCanKan = secondCanKan;
        }

        public string OfferId { get; }

        public int FirstTileId { get; }

        public int SecondTileId { get; }

        public AiSleeveBloomDrawKind DrawKind { get; }

        public long DeadlineMillis { get; }

        public string DecisionId { get; }

        public long ControlEpoch { get; }

        public AiSleeveBloomOfferMode Mode { get; }

        public string? WindowId { get; }

        public bool FirstCanTsumo { get; }

        public bool SecondCanTsumo { get; }

        public bool FirstCanKan { get; }

        public bool SecondCanKan { get; }

        public AiSleeveBloomOfferFacts DeepCopy()
        {
            return new AiSleeveBloomOfferFacts(
                OfferId,
                FirstTileId,
                SecondTileId,
                DrawKind,
                DeadlineMillis,
                DecisionId,
                ControlEpoch,
                Mode,
                WindowId,
                FirstCanTsumo,
                SecondCanTsumo,
                FirstCanKan,
                SecondCanKan);
        }
    }

    /// <summary>V4 <c>SleeveBloomReservationView</c>: the locked tile awaiting the next normal draw.</summary>
    public sealed class AiSleeveBloomReservationFacts
    {
        public AiSleeveBloomReservationFacts(int tileId, string offerId, string? windowId)
        {
            AiFrozenGuard.CheckTileId(tileId, nameof(tileId));
            AiFrozenGuard.RequireText(offerId, nameof(offerId));
            TileId = tileId;
            OfferId = offerId;
            WindowId = windowId;
        }

        public int TileId { get; }

        public string OfferId { get; }

        public string? WindowId { get; }

        public AiSleeveBloomReservationFacts DeepCopy()
        {
            return new AiSleeveBloomReservationFacts(TileId, OfferId, WindowId);
        }
    }

    /// <summary>
    /// V4 <c>SleeveBloomView</c> as immutable facts. V5 enforces the V4 view's cost and the
    /// pending-offer identity/draw binding that V4 only relied on the projection to
    /// maintain; the additional off-turn-window equality is applied only when both sides
    /// provide a value.
    /// </summary>
    public sealed class AiSleeveBloomFacts
    {
        public AiSleeveBloomFacts(
            bool canApply,
            string? disabledReason,
            int cost,
            AiSleeveBloomDrawKind? drawKind,
            string decisionId,
            long controlEpoch,
            long stateVersion,
            AiSleeveBloomOfferFacts? pendingOffer = null,
            bool canApplyOffTurn = false,
            string? offTurnDisabledReason = null,
            string? offTurnWindowId = null,
            AiSleeveBloomReservationFacts? reservation = null)
        {
            if (cost != 10)
            {
                throw new ArgumentOutOfRangeException(nameof(cost), cost, "cost must be 10.");
            }

            if (drawKind.HasValue)
            {
                AiFrozenGuard.CheckDefined(drawKind.Value, nameof(drawKind));
            }

            AiFrozenGuard.RequireText(decisionId, nameof(decisionId));
            AiFrozenGuard.CheckNonNegative(controlEpoch, nameof(controlEpoch));
            AiFrozenGuard.CheckNonNegative(stateVersion, nameof(stateVersion));
            AiSleeveBloomOfferFacts? offer = pendingOffer?.DeepCopy();
            if (offer != null)
            {
                if (!string.Equals(offer.DecisionId, decisionId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "pendingOffer.DecisionId must match the view decisionId.",
                        nameof(pendingOffer));
                }

                if (offer.ControlEpoch != controlEpoch)
                {
                    throw new ArgumentException(
                        "pendingOffer.ControlEpoch must match the view controlEpoch.",
                        nameof(pendingOffer));
                }

                if (drawKind.HasValue && offer.DrawKind != drawKind.Value)
                {
                    throw new ArgumentException(
                        "pendingOffer.DrawKind must match the view drawKind.",
                        nameof(pendingOffer));
                }

                if (offer.Mode == AiSleeveBloomOfferMode.OffTurn &&
                    offTurnWindowId != null &&
                    !string.Equals(offer.WindowId, offTurnWindowId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "pendingOffer.WindowId must match offTurnWindowId.",
                        nameof(pendingOffer));
                }
            }

            CanApply = canApply;
            DisabledReason = disabledReason;
            Cost = cost;
            DrawKind = drawKind;
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            StateVersion = stateVersion;
            PendingOffer = offer;
            CanApplyOffTurn = canApplyOffTurn;
            OffTurnDisabledReason = offTurnDisabledReason;
            OffTurnWindowId = offTurnWindowId;
            Reservation = reservation?.DeepCopy();
        }

        public bool CanApply { get; }

        public string? DisabledReason { get; }

        public int Cost { get; }

        public AiSleeveBloomDrawKind? DrawKind { get; }

        public string DecisionId { get; }

        public long ControlEpoch { get; }

        public long StateVersion { get; }

        public AiSleeveBloomOfferFacts? PendingOffer { get; }

        public bool CanApplyOffTurn { get; }

        public string? OffTurnDisabledReason { get; }

        public string? OffTurnWindowId { get; }

        public AiSleeveBloomReservationFacts? Reservation { get; }

        public AiSleeveBloomFacts DeepCopy()
        {
            return new AiSleeveBloomFacts(
                CanApply,
                DisabledReason,
                Cost,
                DrawKind,
                DecisionId,
                ControlEpoch,
                StateVersion,
                PendingOffer,
                CanApplyOffTurn,
                OffTurnDisabledReason,
                OffTurnWindowId,
                Reservation);
        }
    }

    /// <summary>V4 <c>BeamSwapWindow</c>: normal-draw live wall versus dead-wall replacement.</summary>
    public enum AiBeamSwapWindow : byte
    {
        Wall = 0,
        DeadWall = 1
    }

    /// <summary>V4 <c>BeamSwapSlot</c> kinds.</summary>
    public enum AiBeamSwapSlotKind : byte
    {
        WallSlot = 0,
        DeadWallSlot = 1,
        ReplacementSlot = 2
    }

    /// <summary>One frozen beam-swap target slot; the index is always non-negative, as in V4.</summary>
    public readonly struct AiBeamSwapSlotFacts : IEquatable<AiBeamSwapSlotFacts>
    {
        public AiBeamSwapSlotFacts(AiBeamSwapSlotKind kind, int index)
        {
            AiFrozenGuard.CheckDefined(kind, nameof(kind));
            AiFrozenGuard.CheckNonNegative(index, nameof(index));
            Kind = kind;
            Index = index;
        }

        public AiBeamSwapSlotKind Kind { get; }

        public int Index { get; }

        public bool Equals(AiBeamSwapSlotFacts other)
        {
            return Kind == other.Kind && Index == other.Index;
        }

        public override bool Equals(object? obj)
        {
            return obj is AiBeamSwapSlotFacts other && Equals(other);
        }

        public override int GetHashCode()
        {
            return ((int)Kind * 397) ^ Index;
        }

        public override string ToString()
        {
            return Kind + "(" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        public static bool operator ==(AiBeamSwapSlotFacts left, AiBeamSwapSlotFacts right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(AiBeamSwapSlotFacts left, AiBeamSwapSlotFacts right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// V4 <c>BeamSwapCandidateView</c>: a target slot, whether the viewer's own mark
    /// revealed the target tile, and the tile id only when marked. V4 required exactly this
    /// presence rule; V5 keeps it.
    /// </summary>
    public sealed class AiBeamSwapCandidateFacts
    {
        public AiBeamSwapCandidateFacts(AiBeamSwapSlotFacts slot, bool marked, int? tileId = null)
        {
            if (marked)
            {
                if (!tileId.HasValue)
                {
                    throw new ArgumentException("tileId is required when marked is true.", nameof(tileId));
                }

                AiFrozenGuard.CheckTileId(tileId.Value, nameof(tileId));
            }
            else if (tileId.HasValue)
            {
                throw new ArgumentException("tileId must be null when marked is false.", nameof(tileId));
            }

            Slot = slot;
            Marked = marked;
            TileId = tileId;
        }

        public AiBeamSwapSlotFacts Slot { get; }

        public bool Marked { get; }

        public int? TileId { get; }

        public AiBeamSwapCandidateFacts DeepCopy()
        {
            return new AiBeamSwapCandidateFacts(Slot, Marked, TileId);
        }
    }

    /// <summary>
    /// V4 <c>BeamSwapOfferView</c> as immutable facts: frozen candidate slots, selectable
    /// own-hand tiles, deadline and identity. V5 rejects empty/duplicate slots or hand tiles
    /// and duplicate revealed tiles; the V4 projection already produced distinct physical
    /// candidates, so valid inputs are unchanged.
    /// </summary>
    public sealed class AiBeamSwapOfferFacts
    {
        private readonly ReadOnlyCollection<AiBeamSwapCandidateFacts> _candidates;
        private readonly ReadOnlyCollection<int> _selectableHandTileIds;

        public AiBeamSwapOfferFacts(
            string offerId,
            AiBeamSwapWindow window,
            IReadOnlyList<AiBeamSwapCandidateFacts> candidates,
            IReadOnlyList<int> selectableHandTileIds,
            long deadlineMillis,
            string decisionId,
            long controlEpoch)
        {
            AiFrozenGuard.RequireText(offerId, nameof(offerId));
            AiFrozenGuard.CheckDefined(window, nameof(window));
            AiFrozenGuard.CheckNonNegative(deadlineMillis, nameof(deadlineMillis));
            AiFrozenGuard.RequireText(decisionId, nameof(decisionId));
            AiFrozenGuard.CheckNonNegative(controlEpoch, nameof(controlEpoch));
            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            if (candidates.Count == 0)
            {
                throw new ArgumentException("A beam-swap offer requires at least one candidate.", nameof(candidates));
            }

            var candidateCopy = new AiBeamSwapCandidateFacts[candidates.Count];
            var slots = new HashSet<AiBeamSwapSlotFacts>();
            var revealed = new HashSet<int>();
            for (int i = 0; i < candidates.Count; i++)
            {
                AiBeamSwapCandidateFacts candidate = candidates[i] ??
                    throw new ArgumentException("Candidates cannot contain null.", nameof(candidates));
                if (!slots.Add(candidate.Slot))
                {
                    throw new ArgumentException("Beam-swap candidates must have distinct slots.", nameof(candidates));
                }

                if (candidate.TileId.HasValue && !revealed.Add(candidate.TileId.Value))
                {
                    throw new ArgumentException("A revealed physical tile can appear only once.", nameof(candidates));
                }

                candidateCopy[i] = candidate.DeepCopy();
            }

            if (selectableHandTileIds == null)
            {
                throw new ArgumentNullException(nameof(selectableHandTileIds));
            }

            if (selectableHandTileIds.Count == 0)
            {
                throw new ArgumentException(
                    "A beam-swap offer requires at least one selectable hand tile.",
                    nameof(selectableHandTileIds));
            }

            var handCopy = new int[selectableHandTileIds.Count];
            var handSeen = new HashSet<int>();
            for (int i = 0; i < selectableHandTileIds.Count; i++)
            {
                int tileId = selectableHandTileIds[i];
                AiFrozenGuard.CheckTileId(tileId, nameof(selectableHandTileIds));
                if (!handSeen.Add(tileId))
                {
                    throw new ArgumentException(
                        "selectableHandTileIds cannot repeat a physical tile.",
                        nameof(selectableHandTileIds));
                }

                handCopy[i] = tileId;
            }

            OfferId = offerId;
            Window = window;
            _candidates = new ReadOnlyCollection<AiBeamSwapCandidateFacts>(candidateCopy);
            _selectableHandTileIds = new ReadOnlyCollection<int>(handCopy);
            DeadlineMillis = deadlineMillis;
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
        }

        public string OfferId { get; }

        public AiBeamSwapWindow Window { get; }

        public IReadOnlyList<AiBeamSwapCandidateFacts> Candidates => _candidates;

        public IReadOnlyList<int> SelectableHandTileIds => _selectableHandTileIds;

        public long DeadlineMillis { get; }

        public string DecisionId { get; }

        public long ControlEpoch { get; }

        public AiBeamSwapOfferFacts DeepCopy()
        {
            return new AiBeamSwapOfferFacts(
                OfferId,
                Window,
                _candidates,
                _selectableHandTileIds,
                DeadlineMillis,
                DecisionId,
                ControlEpoch);
        }
    }

    /// <summary>
    /// V4 <c>BeamSwapView</c> as immutable facts. V5 enforces the fixed cost of 10 and, when
    /// a pending offer is present, binds its decision/control identity and (when both are
    /// known) its window to the view.
    /// </summary>
    public sealed class AiBeamSwapFacts
    {
        public AiBeamSwapFacts(
            bool canApply,
            string? disabledReason,
            int cost,
            AiBeamSwapWindow? window,
            string decisionId,
            long controlEpoch,
            long stateVersion,
            AiBeamSwapOfferFacts? pendingOffer = null)
        {
            if (cost != 10)
            {
                throw new ArgumentOutOfRangeException(nameof(cost), cost, "cost must be 10.");
            }

            if (window.HasValue)
            {
                AiFrozenGuard.CheckDefined(window.Value, nameof(window));
            }

            AiFrozenGuard.RequireText(decisionId, nameof(decisionId));
            AiFrozenGuard.CheckNonNegative(controlEpoch, nameof(controlEpoch));
            AiFrozenGuard.CheckNonNegative(stateVersion, nameof(stateVersion));
            AiBeamSwapOfferFacts? offer = pendingOffer?.DeepCopy();
            if (offer != null)
            {
                if (!string.Equals(offer.DecisionId, decisionId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "pendingOffer.DecisionId must match the view decisionId.",
                        nameof(pendingOffer));
                }

                if (offer.ControlEpoch != controlEpoch)
                {
                    throw new ArgumentException(
                        "pendingOffer.ControlEpoch must match the view controlEpoch.",
                        nameof(pendingOffer));
                }

                if (window.HasValue && offer.Window != window.Value)
                {
                    throw new ArgumentException(
                        "pendingOffer.Window must match the view window.",
                        nameof(pendingOffer));
                }
            }

            CanApply = canApply;
            DisabledReason = disabledReason;
            Cost = cost;
            Window = window;
            DecisionId = decisionId;
            ControlEpoch = controlEpoch;
            StateVersion = stateVersion;
            PendingOffer = offer;
        }

        public bool CanApply { get; }

        public string? DisabledReason { get; }

        public int Cost { get; }

        public AiBeamSwapWindow? Window { get; }

        public string DecisionId { get; }

        public long ControlEpoch { get; }

        public long StateVersion { get; }

        public AiBeamSwapOfferFacts? PendingOffer { get; }

        public AiBeamSwapFacts DeepCopy()
        {
            return new AiBeamSwapFacts(
                CanApply,
                DisabledReason,
                Cost,
                Window,
                DecisionId,
                ControlEpoch,
                StateVersion,
                PendingOffer);
        }
    }

    /// <summary>
    /// Small validation helpers shared by the frozen AI context types. These are
    /// deliberately internal: they enforce the V4 constructor/precondition order and the V5
    /// structural copy rules without widening the public contract.
    /// </summary>
    internal static class AiFrozenGuard
    {
        internal static string RequireText(string? value, string name)
        {
            if (value == null || KotlinSemantics.IsKotlinBlank(value))
            {
                throw new ArgumentException(name + " cannot be blank.", name);
            }

            return value;
        }

        internal static void CheckSeat(int seat, string name)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(name, seat, "Seat must be 0..3.");
            }
        }

        internal static void CheckTileId(int tileId, string name)
        {
            if (tileId < 0 || tileId > Mahjong.Domain.TileId.MaxValue)
            {
                throw new ArgumentOutOfRangeException(name, tileId, "Tile id must be in 0..135.");
            }
        }

        internal static void CheckNonNegative(int value, string name)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(name, value, name + " must be non-negative.");
            }
        }

        internal static void CheckNonNegative(long value, string name)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(name, value, name + " must be non-negative.");
            }
        }

        internal static void CheckDefined<T>(T value, string name)
            where T : struct, Enum
        {
            if (!Enum.IsDefined(typeof(T), value))
            {
                throw new ArgumentOutOfRangeException(name, value, name + " is not a defined value.");
            }
        }
    }
}
