using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    public enum TransitionErrorCode
    {
        None = 0,
        InvalidSeat = 1,
        InvalidTile = 2,
        InvalidPosition = 3,
        DuplicateTile = 4,
        MissingTile = 5,
        PositionOccupied = 6,
        SourceMismatch = 7,
        TargetUnassigned = 8,
        DuplicateTarget = 9,
        WallSlotOutOfRange = 10,
        WallExhausted = 11,
        HandFull = 12,
        DeadWallExhausted = 13,
        ReplacementUnavailable = 14,
        NoReplacementTile = 15,
        CalledTileNotLatestDiscard = 16,
        InvalidMeldShape = 17,
        MeldSlotExhausted = 18,
        ConservationViolation = 19,
        InvalidPointer = 20,
        NoMoves = 21,
        RinshanDrawEvidenceMismatch = 22,
        DrawnTileNotClosed = 23
    }

    public sealed class TransitionError
    {
        public TransitionError(TransitionErrorCode code, string message)
        {
            if (code == TransitionErrorCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(code), code, "A failure requires a non-None code.");
            }

            Code = code;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public TransitionErrorCode Code { get; }

        public string Message { get; }

        public override string ToString() => Code.ToString() + ": " + Message;
    }

    /// <summary>
    /// Result of an immutable transition. On failure <see cref="State"/> is null and the
    /// source state is untouched; on success <see cref="State"/> is a new instance.
    /// </summary>
    public readonly struct TransitionResult
    {
        private TransitionResult(
            bool isSuccess,
            TilePositionMap? state,
            TransitionError? error,
            TileId? resultTile)
        {
            IsSuccess = isSuccess;
            State = state;
            Error = error;
            ResultTile = resultTile;
        }

        public bool IsSuccess { get; }

        public TilePositionMap? State { get; }

        public TransitionError? Error { get; }

        public TileId? ResultTile { get; }

        public static TransitionResult Success(TilePositionMap state, TileId? resultTile = null)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return new TransitionResult(true, state, null, resultTile);
        }

        public static TransitionResult Failure(TransitionErrorCode code, string message)
        {
            return new TransitionResult(false, null, new TransitionError(code, message), null);
        }

        public TilePositionMap RequireState()
        {
            if (!IsSuccess || State == null)
            {
                throw new InvalidOperationException(Error == null ? "Transition failed." : Error.ToString());
            }

            return State;
        }
    }

    public readonly struct TileMove
    {
        public TileMove(TileId tile, TilePosition from, TilePosition to)
        {
            Tile = tile;
            From = from;
            To = to;
        }

        public TileId Tile { get; }

        public TilePosition From { get; }

        public TilePosition To { get; }

        public override string ToString() => Tile + ": " + From + " -> " + To;
    }

    public sealed class ConservationReport
    {
        private readonly ReadOnlyCollection<string> _errors;

        internal ConservationReport(
            IReadOnlyList<string> errors,
            int assignedCount,
            int unassignedCount,
            int deadWallCount,
            int replacementCount,
            ulong stateHash)
        {
            _errors = new ReadOnlyCollection<string>(new List<string>(errors));
            AssignedCount = assignedCount;
            UnassignedCount = unassignedCount;
            DeadWallCount = deadWallCount;
            ReplacementCount = replacementCount;
            StateHash = stateHash;
        }

        public bool IsValid => _errors.Count == 0;

        public IReadOnlyList<string> Errors => _errors;

        public int AssignedCount { get; }

        public int UnassignedCount { get; }

        public int DeadWallCount { get; }

        public int ReplacementCount { get; }

        public ulong StateHash { get; }

        public override string ToString()
        {
            if (IsValid)
            {
                return "OK assigned=" + AssignedCount + " unassigned=" + UnassignedCount +
                       " deadWall=" + DeadWallCount + " replacements=" + ReplacementCount +
                       " hash=" + TileStateHash.Format(StateHash);
            }

            return "INVALID: " + string.Join("; ", _errors);
        }
    }

    public sealed class ConservationException : InvalidOperationException
    {
        public ConservationException(ConservationReport report)
            : base(report == null ? "Conservation audit failed." : report.ToString())
        {
            Report = report ?? throw new ArgumentNullException(nameof(report));
        }

        public ConservationReport Report { get; }
    }

    /// <summary>
    /// Immutable physical position map for exactly 136 physical tiles plus live-wall,
    /// dead-wall and rinshan cursors. Every transition returns a new instance; failures
    /// leave the source map and every array untouched.
    ///
    /// V4 references: TilePool/TileLocation for zones, TileMovement for deal/draw/discard/
    /// chi/pon/kan/swap/rinshan, StateInvariants for conservation and dead-wall accounting.
    /// </summary>
    public sealed class TilePositionMap
    {
        public const int TotalTileCount = 136;
        public const int LiveWallSlotCount = 122;
        public const int DeadWallSlotCount = 14;
        public const int InitialDealCount = 53;
        public const int HandSlotCount = 14;
        public const int MeldGroupSize = 4;
        public const int MeldGroupCount = 4;

        private readonly TilePosition[] _positions;
        private readonly LastDiscardEvidence? _lastDiscard;
        private readonly RinshanDrawEvidence? _rinshanDraw;
        private readonly DrawnTileEvidence? _drawnTile;
        private readonly TileId[] _doraIndicators;
        private readonly ReadOnlyCollection<TileId> _readOnlyDoraIndicators;
        private readonly ulong _stateHash;

        private TilePositionMap(
            TilePosition[] positions,
            int wallPointer,
            int wallEndExclusive,
            int rinshanPointer,
            int revision,
            int kanCount,
            LastDiscardEvidence? lastDiscard,
            RinshanDrawEvidence? rinshanDraw,
            DrawnTileEvidence? drawnTile,
            IReadOnlyList<TileId> doraIndicators)
        {
            _positions = positions ?? throw new ArgumentNullException(nameof(positions));
            if (positions.Length != TileId.Count)
            {
                throw new ArgumentException("Position array must contain exactly 136 entries.", nameof(positions));
            }

            if (revision < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(revision), revision, "Revision must be non-negative.");
            }

            if (kanCount < 0 || kanCount > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(kanCount), kanCount, "KanCount must be in 0..4.");
            }

            if (doraIndicators == null)
            {
                throw new ArgumentNullException(nameof(doraIndicators));
            }

            WallPointer = wallPointer;
            WallEndExclusive = wallEndExclusive;
            RinshanPointer = rinshanPointer;
            Revision = revision;
            KanCount = kanCount;
            _lastDiscard = lastDiscard;
            _rinshanDraw = rinshanDraw;
            _drawnTile = drawnTile;
            _doraIndicators = new TileId[doraIndicators.Count];
            for (int i = 0; i < doraIndicators.Count; i++)
            {
                _doraIndicators[i] = doraIndicators[i];
            }

            _readOnlyDoraIndicators = Array.AsReadOnly(_doraIndicators);

            ConservationReport report = Validate(this);
            if (!report.IsValid)
            {
                throw new ConservationException(report);
            }

            _stateHash = report.StateHash;
        }

        public int WallPointer { get; }

        public int WallEndExclusive { get; }

        public int RinshanPointer { get; }

        /// <summary>Number of successful physical transitions applied; excluded from state hash.</summary>
        public int Revision { get; }

        /// <summary>Declared kan count, 0..4, tracked for the rinshan/kan safety checks.</summary>
        public int KanCount { get; }

        public int ReplacementCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _positions.Length; i++)
                {
                    if (_positions[i].Zone == TileZone.DeadWallReplacement)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Physical live-wall tiles currently inside [0, WallEndExclusive).</summary>
        public int WallRemaining
        {
            get
            {
                int count = 0;
                for (int tileValue = 0; tileValue < TileId.Count; tileValue++)
                {
                    TilePosition position = _positions[tileValue];
                    if (position.Zone == TileZone.Wall && position.Index < WallEndExclusive)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public ulong StateHash => _stateHash;

        /// <summary>
        /// The one discard currently open to Chi/Pon/OpenKan. Null means no discard is
        /// callable, including immediately after the open discard was consumed.
        /// </summary>
        public LastDiscardEvidence? LastDiscard => _lastDiscard;

        /// <summary>
        /// Evidence for the most recent rinshan draw. It binds the actual tile, seat and
        /// dead-wall slot; any later discard/draw/swap clears it.
        /// </summary>
        public RinshanDrawEvidence? LastRinshanDraw => _rinshanDraw;

        /// <summary>
        /// The tile currently held as this seat's drawn tile, covering normal draws,
        /// rinshan draws and the dealer's 14th tile. Null when no drawn tile is tracked.
        /// </summary>
        public DrawnTileEvidence? DrawnTile => _drawnTile;

        /// <summary>Revealed dora indicator tiles, all of which remain in the dead wall.</summary>
        public IReadOnlyList<TileId> DoraIndicators => _readOnlyDoraIndicators;

        public TilePosition this[TileId tile] => _positions[tile.Value];

        public TilePosition this[int tileId]
        {
            get
            {
                if (tileId < TileId.MinValue || tileId > TileId.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(tileId), tileId, "Tile id must be in 0..135.");
                }

                return _positions[tileId];
            }
        }

        public static TilePositionMap CreateUnassigned()
        {
            var positions = new TilePosition[TileId.Count];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = TilePosition.Unassigned;
            }

            return new TilePositionMap(positions, 0, 0, 0, 0, 0, null, null, null, Array.Empty<TileId>());
        }

        /// <summary>Builds the pre-deal wall: live slots 0..121, dead wall 0..13.</summary>
        public static TilePositionMap CreateWall(RecordedRandomDraw recorded)
        {
            if (recorded == null)
            {
                throw new ArgumentNullException(nameof(recorded));
            }

            TilePosition[] positions = CreateWallPositions(recorded);
            return new TilePositionMap(
                positions,
                0,
                LiveWallSlotCount,
                0,
                0,
                0,
                null,
                null,
                null,
                Array.Empty<TileId>());
        }

        /// <summary>Builds the opening map with V4's 53-tile deal already applied.</summary>
        public static TilePositionMap CreateDealt(RecordedRandomDraw recorded, int dealerSeat)
        {
            if (recorded == null)
            {
                throw new ArgumentNullException(nameof(recorded));
            }

            if (dealerSeat < 0 || dealerSeat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(dealerSeat), dealerSeat, "Dealer seat must be 0..3.");
            }

            TilePosition[] positions = CreateWallPositions(recorded);
            TilePosition[] dealt = BuildDealtPositions(positions, dealerSeat);
            var dora = new[] { FindTileAt(dealt, TilePosition.DeadWall(4)) };
            var drawn = new DrawnTileEvidence(
                FindTileAt(dealt, TilePosition.Hand(dealerSeat, 13)),
                dealerSeat,
                DrawSource.DealExtra);
            return new TilePositionMap(
                dealt,
                InitialDealCount,
                LiveWallSlotCount,
                0,
                0,
                0,
                null,
                null,
                drawn,
                dora);
        }

        /// <summary>
        /// Rehydrates an already validated position map (snapshot restore/tests). This is
        /// not a command path: command transitions below enforce source, ownership and
        /// pointer rules. Callers restoring an active map must also pass the revealed dora
        /// indicators, kan count, drawn-tile evidence and any pending rinshan-draw evidence;
        /// null dora means none revealed.
        /// </summary>
        public static TilePositionMap FromPlacements(
            IEnumerable<TilePlacement> placements,
            int wallPointer,
            int wallEndExclusive,
            int rinshanPointer,
            LastDiscardEvidence? lastDiscard = null,
            int revision = 0,
            RinshanDrawEvidence? rinshanDraw = null,
            IReadOnlyList<TileId>? doraIndicators = null,
            int kanCount = 0,
            DrawnTileEvidence? drawnTile = null)
        {
            TilePosition[] positions = BuildDense(placements);
            return new TilePositionMap(
                positions,
                wallPointer,
                wallEndExclusive,
                rinshanPointer,
                revision,
                kanCount,
                lastDiscard,
                rinshanDraw,
                drawnTile,
                doraIndicators ?? Array.Empty<TileId>());
        }

        public TilePosition PositionOf(TileId tile) => _positions[tile.Value];

        public bool TryGetTileAt(TilePosition position, out TileId tile)
        {
            if (!position.IsAssigned)
            {
                tile = default;
                return false;
            }

            for (int i = 0; i < _positions.Length; i++)
            {
                if (_positions[i] == position)
                {
                    tile = new TileId(i);
                    return true;
                }
            }

            tile = default;
            return false;
        }

        public TileId TileAt(TilePosition position)
        {
            if (!TryGetTileAt(position, out TileId tile))
            {
                throw new InvalidOperationException("No tile at position " + position + ".");
            }

            return tile;
        }

        public bool IsPositionFree(TilePosition position)
        {
            if (!position.IsAssigned)
            {
                return false;
            }

            for (int i = 0; i < _positions.Length; i++)
            {
                if (_positions[i] == position)
                {
                    return false;
                }
            }

            return true;
        }

        public int CountAt(TileZone zone)
        {
            int count = 0;
            for (int i = 0; i < _positions.Length; i++)
            {
                if (_positions[i].Zone == zone)
                {
                    count++;
                }
            }

            return count;
        }

        public int CountAt(TileZone zone, int seat)
        {
            int count = 0;
            for (int i = 0; i < _positions.Length; i++)
            {
                TilePosition position = _positions[i];
                if (position.Zone == zone && position.Seat == seat)
                {
                    count++;
                }
            }

            return count;
        }

        public IReadOnlyList<TileId> TilesInZone(TileZone zone, int seat = -1)
        {
            var result = new List<TileId>();
            for (int tileValue = 0; tileValue < TileId.Count; tileValue++)
            {
                TilePosition position = _positions[tileValue];
                if (position.Zone != zone)
                {
                    continue;
                }

                if (seat >= 0 && position.Seat != seat)
                {
                    continue;
                }

                result.Add(new TileId(tileValue));
            }

            result.Sort((left, right) =>
            {
                TilePosition leftPosition = _positions[left.Value];
                TilePosition rightPosition = _positions[right.Value];
                int index = leftPosition.Index.CompareTo(rightPosition.Index);
                return index != 0 ? index : left.CompareTo(right);
            });

            return new ReadOnlyCollection<TileId>(result);
        }

        public IReadOnlyList<TilePlacement> AsPlacements()
        {
            var result = new TilePlacement[TileId.Count];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new TilePlacement(new TileId(i), _positions[i]);
            }

            return new ReadOnlyCollection<TilePlacement>(result);
        }

        /// <summary>Applies V4's 53-tile opening deal to a fresh pre-deal wall.</summary>
        public TransitionResult DealInitial(int dealerSeat)
        {
            if (dealerSeat < 0 || dealerSeat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Dealer seat must be 0..3.");
            }

            if (WallPointer != 0 || WallEndExclusive != LiveWallSlotCount ||
                RinshanPointer != 0 || ReplacementCount != 0 ||
                LastDiscard != null || LastRinshanDraw != null || DoraIndicators.Count != 0)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidPointer,
                    "DealInitial requires a fresh pre-deal wall with no open discard or draw evidence.");
            }

            if (CountAt(TileZone.Unassigned) != 0 ||
                CountAt(TileZone.Hand) != 0 ||
                CountAt(TileZone.Discard) != 0 ||
                CountAt(TileZone.Meld) != 0 ||
                CountAt(TileZone.Sleeve) != 0)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidPosition,
                    "DealInitial requires all 136 tiles in the wall/dead wall.");
            }

            try
            {
                TilePosition[] dealt = BuildDealtPositions(_positions, dealerSeat);
                var dora = new[] { FindTileAt(dealt, TilePosition.DeadWall(4)) };
                var drawn = new DrawnTileEvidence(
                    FindTileAt(dealt, TilePosition.Hand(dealerSeat, 13)),
                    dealerSeat,
                    DrawSource.DealExtra);
                var next = new TilePositionMap(
                    dealt,
                    InitialDealCount,
                    LiveWallSlotCount,
                    RinshanPointer,
                    Revision + 1,
                    0,
                    null,
                    null,
                    drawn,
                    dora);
                return TransitionResult.Success(next);
            }
            catch (ConservationException ex)
            {
                return TransitionResult.Failure(TransitionErrorCode.ConservationViolation, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidPosition, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidPosition, ex.Message);
            }
        }

        /// <summary>Draws the first available live-wall tile (V4 scans from slot 0 to re-enter holes).</summary>
        public TransitionResult DrawFromWall(int seat) => DrawFromWall(seat, null);

        /// <summary>
        /// Draws the first live-wall tile not locked by an owner reservation/off-turn offer.
        /// The callback is supplied by the N-1b cheat bridge; a null callback keeps the normal
        /// rule. Skipped protected tiles stay physically in the wall.
        /// </summary>
        public TransitionResult DrawFromWall(int seat, Func<TileId, bool>? isProtected)
        {
            if (seat < 0 || seat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            int wallIndex = FindFirstWallIndex(isProtected);
            if (wallIndex < 0)
            {
                return TransitionResult.Failure(TransitionErrorCode.WallExhausted, "No live-wall tile remains.");
            }

            int handSlot = FindFreeSlot(TileZone.Hand, seat, TileZoneRules.MaxIndex(TileZone.Hand));
            if (handSlot < 0)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.HandFull,
                    "Seat " + seat + " already has 14 hand tiles.");
            }

            TileId tile = TileAt(TilePosition.Wall(wallIndex));
            int nextPointer = wallIndex >= WallPointer ? wallIndex + 1 : WallPointer;
            return Apply(
                new[] { new TileMove(tile, TilePosition.Wall(wallIndex), TilePosition.Hand(seat, handSlot)) },
                nextPointer,
                WallEndExclusive,
                RinshanPointer,
                tile,
                true,
                null,
                replaceDrawnTile: true,
                nextDrawnTile: new DrawnTileEvidence(tile, seat, DrawSource.Wall));
        }

        /// <summary>Draws dead-wall[rinshanPointer] and replaces it from the live wall's tail.</summary>
        public TransitionResult DrawReplacement(int seat) => DrawReplacement(seat, null);

        /// <summary>
        /// Draws the rinshan tile and takes its replacement from the last live-wall tile not
        /// locked by a reservation/offer. The protected tail tile stays in the wall.
        /// </summary>
        public TransitionResult DrawReplacement(int seat, Func<TileId, bool>? isProtected)
        {
            if (seat < 0 || seat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (RinshanPointer < 0 || RinshanPointer >= 4)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.DeadWallExhausted,
                    "RinshanPointer must be in 0..3, was " + RinshanPointer + ".");
            }

            if (!TryGetTileAt(TilePosition.DeadWall(RinshanPointer), out TileId rinshanTile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.DeadWallExhausted,
                    "No dead-wall tile at rinshan slot " + RinshanPointer + ".");
            }

            int replacementSource = FindLastWallIndex(isProtected);
            if (replacementSource < 0)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.NoReplacementTile,
                    "No live-wall tile is available for dead-wall replacement.");
            }

            int replacementSlot = ReplacementCount;
            if (replacementSlot < 0 || replacementSlot > 3)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.ReplacementUnavailable,
                    "No replacement slot remains.");
            }

            int handSlot = FindFreeSlot(TileZone.Hand, seat, TileZoneRules.MaxIndex(TileZone.Hand));
            if (handSlot < 0)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.HandFull,
                    "Seat " + seat + " already has 14 hand tiles.");
            }

            TileId replacementTile = TileAt(TilePosition.Wall(replacementSource));
            int nextWallEnd = WallEndExclusive - 1;
            if (nextWallEnd < 0)
            {
                return TransitionResult.Failure(TransitionErrorCode.WallExhausted, "WallEndExclusive is already 0.");
            }

            // When protected/reserved tail tiles remain above the replacement source, keep the
            // wall end open so those entities stay inside the 136-position live-wall window.
            if (replacementSource < WallEndExclusive - 1)
            {
                nextWallEnd = WallEndExclusive;
            }

            int nextPointer = replacementSource < WallPointer ? WallPointer - 1 : WallPointer;
            if (nextPointer > nextWallEnd)
            {
                nextPointer = nextWallEnd;
            }

            return Apply(
                new[]
                {
                    new TileMove(
                        rinshanTile,
                        TilePosition.DeadWall(RinshanPointer),
                        TilePosition.Hand(seat, handSlot)),
                    new TileMove(
                        replacementTile,
                        TilePosition.Wall(replacementSource),
                        TilePosition.DeadWallReplacement(replacementSlot))
                },
                nextPointer,
                nextWallEnd,
                RinshanPointer + 1,
                rinshanTile,
                true,
                null,
                new RinshanDrawEvidence(rinshanTile, seat, RinshanPointer),
                replaceDrawnTile: true,
                nextDrawnTile: new DrawnTileEvidence(rinshanTile, seat, DrawSource.Rinshan));
        }

        /// <summary>
        /// Reveals the next kan-dora indicator from the dead wall using V4's fixed slots
        /// (initial 4, then 6/8/10/12) and appends it to the immutable indicator list. No tile
        /// moves; the indicator is already a dead-wall entity. The five-indicator and
        /// fourteen-slot limits are enforced here rather than by a caller-side guess.
        /// </summary>
        public TransitionResult RevealDoraIndicator()
        {
            if (DoraIndicators.Count >= 5)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.DeadWallExhausted,
                    "All five dora indicators are already revealed.");
            }

            int index = 4 + (DoraIndicators.Count * 2);
            if (index < 4 || index >= DeadWallSlotCount)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.DeadWallExhausted,
                    "No dead-wall slot remains for dora indicator " + DoraIndicators.Count + ".");
            }

            if (!TryGetTileAt(TilePosition.DeadWall(index), out TileId indicator))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.DeadWallExhausted,
                    "No dead-wall tile exists at indicator slot " + index + ".");
            }

            for (int i = 0; i < DoraIndicators.Count; i++)
            {
                if (DoraIndicators[i] == indicator)
                {
                    return TransitionResult.Failure(
                        TransitionErrorCode.DeadWallExhausted,
                        "Dora indicator " + indicator + " is already revealed.");
                }
            }

            var nextIndicators = new TileId[DoraIndicators.Count + 1];
            for (int i = 0; i < DoraIndicators.Count; i++)
            {
                nextIndicators[i] = DoraIndicators[i];
            }

            nextIndicators[nextIndicators.Length - 1] = indicator;
            var nextPositions = new TilePosition[_positions.Length];
            Array.Copy(_positions, nextPositions, _positions.Length);
            try
            {
                var state = new TilePositionMap(
                    nextPositions,
                    WallPointer,
                    WallEndExclusive,
                    RinshanPointer,
                    Revision + 1,
                    KanCount,
                    _lastDiscard,
                    _rinshanDraw,
                    _drawnTile,
                    nextIndicators);
                return TransitionResult.Success(state, indicator);
            }
            catch (ConservationException ex)
            {
                return TransitionResult.Failure(TransitionErrorCode.ConservationViolation, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return TransitionResult.Failure(TransitionErrorCode.ConservationViolation, ex.Message);
            }
        }

        public TransitionResult Discard(int seat, TileId tile)
        {
            if (seat < 0 || seat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            TilePosition from = _positions[tile.Value];
            if (from.Zone != TileZone.Hand || from.Seat != seat)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.SourceMismatch,
                    "Tile " + tile + " is not in seat " + seat + "'s hand.");
            }

            int discardSlot = FindFreeSlot(TileZone.Discard, seat, TileZoneRules.MaxIndex(TileZone.Discard));
            if (discardSlot < 0)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.PositionOccupied,
                    "No free discard slot for seat " + seat + ".");
            }

            TilePosition discardPosition = TilePosition.Discard(seat, discardSlot);
            return Apply(
                new[] { new TileMove(tile, from, discardPosition) },
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                tile,
                true,
                new LastDiscardEvidence(tile, discardPosition),
                replaceDrawnTile: true);
        }

        public TransitionResult Chi(
            int seat,
            int fromSeat,
            TileId calledTile,
            TileId handTileA,
            TileId handTileB)
        {
            if (seat < 0 || seat > 3 || fromSeat < 0 || fromSeat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (fromSeat != (seat + 3) % 4)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Chi is only allowed from kamicha " + ((seat + 3) % 4) + ".");
            }

            if (!calledTile.IsNumberTile)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Chi requires a number tile, got " + calledTile + ".");
            }

            if (handTileA == handTileB || handTileA == calledTile || handTileB == calledTile)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Chi requires three distinct physical tiles.");
            }

            if (!IsInHand(seat, handTileA) || !IsInHand(seat, handTileB))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Chi hand tiles must belong to seat " + seat + ".");
            }

            TileId[] ordered = { handTileA, handTileB, calledTile };
            Array.Sort(ordered);
            if (ordered[0].Suit != ordered[1].Suit || ordered[1].Suit != ordered[2].Suit ||
                ordered[0].IsHonor ||
                ordered[1].Rank != ordered[0].Rank + 1 ||
                ordered[2].Rank != ordered[1].Rank + 1)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Chi tiles must form one consecutive sequence of the same suit.");
            }

            TransitionError? discardError = ValidateCallableDiscard(
                fromSeat,
                calledTile,
                out TilePosition calledPosition);
            if (discardError != null)
            {
                return TransitionResult.Failure(discardError.Code, discardError.Message);
            }

            int meldBase = FindFreeMeldBase(seat, 3);
            if (meldBase < 0)
            {
                return TransitionResult.Failure(TransitionErrorCode.MeldSlotExhausted, "No free meld group.");
            }

            return Apply(
                new[]
                {
                    new TileMove(calledTile, calledPosition, TilePosition.Meld(seat, meldBase)),
                    new TileMove(handTileA, _positions[handTileA.Value], TilePosition.Meld(seat, meldBase + 1)),
                    new TileMove(handTileB, _positions[handTileB.Value], TilePosition.Meld(seat, meldBase + 2))
                },
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                null,
                true,
                null,
                replaceDrawnTile: true);
        }

        public TransitionResult Pon(
            int seat,
            int fromSeat,
            TileId calledTile,
            TileId handTileA,
            TileId handTileB)
        {
            if (seat < 0 || seat > 3 || fromSeat < 0 || fromSeat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (seat == fromSeat)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Cannot call your own discard.");
            }

            if (handTileA == handTileB || handTileA == calledTile || handTileB == calledTile ||
                !handTileA.SameType(calledTile) || !handTileB.SameType(calledTile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Pon requires two distinct hand copies of the called type.");
            }

            if (!IsInHand(seat, handTileA) || !IsInHand(seat, handTileB))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Pon hand tiles must belong to seat " + seat + ".");
            }

            TransitionError? discardError = ValidateCallableDiscard(
                fromSeat,
                calledTile,
                out TilePosition calledPosition);
            if (discardError != null)
            {
                return TransitionResult.Failure(discardError.Code, discardError.Message);
            }

            int meldBase = FindFreeMeldBase(seat, 3);
            if (meldBase < 0)
            {
                return TransitionResult.Failure(TransitionErrorCode.MeldSlotExhausted, "No free meld group.");
            }

            return Apply(
                new[]
                {
                    new TileMove(calledTile, calledPosition, TilePosition.Meld(seat, meldBase)),
                    new TileMove(handTileA, _positions[handTileA.Value], TilePosition.Meld(seat, meldBase + 1)),
                    new TileMove(handTileB, _positions[handTileB.Value], TilePosition.Meld(seat, meldBase + 2))
                },
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                null,
                true,
                null,
                replaceDrawnTile: true);
        }

        public TransitionResult OpenKan(
            int seat,
            int fromSeat,
            TileId calledTile,
            TileId handTileA,
            TileId handTileB,
            TileId handTileC)
        {
            if (seat < 0 || seat > 3 || fromSeat < 0 || fromSeat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (KanCount >= 4)
            {
                return TransitionResult.Failure(TransitionErrorCode.MeldSlotExhausted, "Four-kan limit reached.");
            }

            if (seat == fromSeat)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Cannot call your own discard.");
            }

            TileId[] handTiles = { handTileA, handTileB, handTileC };
            if (HasDuplicates(handTiles) || Contains(handTiles, calledTile) ||
                !handTileA.SameType(calledTile) || !handTileB.SameType(calledTile) ||
                !handTileC.SameType(calledTile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Open kan requires three distinct hand copies of the called type.");
            }

            for (int i = 0; i < handTiles.Length; i++)
            {
                if (!IsInHand(seat, handTiles[i]))
                {
                    return TransitionResult.Failure(
                        TransitionErrorCode.InvalidMeldShape,
                        "Open kan hand tiles must belong to seat " + seat + ".");
                }
            }

            TransitionError? discardError = ValidateCallableDiscard(
                fromSeat,
                calledTile,
                out TilePosition calledPosition);
            if (discardError != null)
            {
                return TransitionResult.Failure(discardError.Code, discardError.Message);
            }

            int meldBase = FindFreeMeldBase(seat, 4);
            if (meldBase < 0)
            {
                return TransitionResult.Failure(TransitionErrorCode.MeldSlotExhausted, "No free meld group.");
            }

            return Apply(
                new[]
                {
                    new TileMove(calledTile, calledPosition, TilePosition.Meld(seat, meldBase)),
                    new TileMove(handTileA, _positions[handTileA.Value], TilePosition.Meld(seat, meldBase + 1)),
                    new TileMove(handTileB, _positions[handTileB.Value], TilePosition.Meld(seat, meldBase + 2)),
                    new TileMove(handTileC, _positions[handTileC.Value], TilePosition.Meld(seat, meldBase + 3))
                },
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                null,
                true,
                null,
                null,
                KanCount + 1,
                replaceDrawnTile: true);
        }

        public TransitionResult ClosedKan(int seat, TileId tileA, TileId tileB, TileId tileC, TileId tileD)
        {
            if (seat < 0 || seat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (KanCount >= 4)
            {
                return TransitionResult.Failure(TransitionErrorCode.MeldSlotExhausted, "Four-kan limit reached.");
            }

            TileId[] tiles = { tileA, tileB, tileC, tileD };
            if (HasDuplicates(tiles) ||
                !tileA.SameType(tileB) || !tileB.SameType(tileC) || !tileC.SameType(tileD))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Closed kan requires four distinct copies of one type.");
            }

            for (int i = 0; i < tiles.Length; i++)
            {
                if (!IsInHand(seat, tiles[i]))
                {
                    return TransitionResult.Failure(
                        TransitionErrorCode.InvalidMeldShape,
                        "Closed kan tiles must belong to seat " + seat + ".");
                }
            }

            int meldBase = FindFreeMeldBase(seat, 4);
            if (meldBase < 0)
            {
                return TransitionResult.Failure(TransitionErrorCode.MeldSlotExhausted, "No free meld group.");
            }

            var moves = new TileMove[4];
            for (int i = 0; i < tiles.Length; i++)
            {
                moves[i] = new TileMove(
                    tiles[i],
                    _positions[tiles[i].Value],
                    TilePosition.Meld(seat, meldBase + i));
            }

            return Apply(
                moves,
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                nextKanCount: KanCount + 1,
                replaceDrawnTile: true);
        }

        /// <summary>Adds a fourth copy to an existing three-tile same-type meld (V4 added kan).</summary>
        public TransitionResult AddedKan(int seat, TileId tile)
        {
            if (seat < 0 || seat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (KanCount >= 4)
            {
                return TransitionResult.Failure(TransitionErrorCode.MeldSlotExhausted, "Four-kan limit reached.");
            }

            if (!IsInHand(seat, tile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Added-kan tile " + tile + " is not in seat " + seat + "'s hand.");
            }

            int meldBase = FindAddedKanBase(seat, tile);
            if (meldBase < 0)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "No matching three-tile pon exists for added kan " + tile + ".");
            }

            return Apply(
                new[] { new TileMove(tile, _positions[tile.Value], TilePosition.Meld(seat, meldBase + 3)) },
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                nextKanCount: KanCount + 1,
                replaceDrawnTile: true);
        }

        /// <summary>Equally swaps a hand tile with a live-wall slot (SleeveBloom SECOND / BeamSwap WALL).</summary>
        public TransitionResult SwapHandWithWall(int seat, TileId handTile, int wallIndex)
        {
            if (seat < 0 || seat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (wallIndex < 0 || wallIndex >= WallEndExclusive)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.WallSlotOutOfRange,
                    "Wall slot " + wallIndex + " is outside 0.." + (WallEndExclusive - 1) + ".");
            }

            TilePosition handPosition = _positions[handTile.Value];
            if (handPosition.Zone != TileZone.Hand || handPosition.Seat != seat)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.SourceMismatch,
                    "Tile " + handTile + " is not in seat " + seat + "'s hand.");
            }

            if (!TryGetTileAt(TilePosition.Wall(wallIndex), out TileId wallTile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.MissingTile,
                    "No wall tile at slot " + wallIndex + ".");
            }

            if (wallTile == handTile)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidMeldShape,
                    "Cannot swap a tile with itself.");
            }

            bool replacesDrawn = _drawnTile != null && _drawnTile.Value.Tile == handTile;
            DrawnTileEvidence? nextDrawn = replacesDrawn
                ? new DrawnTileEvidence(wallTile, seat, _drawnTile!.Value.Source)
                : _drawnTile;
            return Apply(
                new[]
                {
                    new TileMove(handTile, handPosition, TilePosition.Wall(wallIndex)),
                    new TileMove(wallTile, TilePosition.Wall(wallIndex), handPosition)
                },
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                wallTile,
                replaceDrawnTile: replacesDrawn,
                nextDrawnTile: nextDrawn);
        }

        /// <summary>Equally swaps a hand tile with an unrevealed dead-wall slot (BeamSwap DEAD_WALL).</summary>
        public TransitionResult SwapHandWithDeadWall(int seat, TileId handTile, int deadWallIndex)
        {
            if (seat < 0 || seat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (deadWallIndex < 0 || deadWallIndex >= DeadWallSlotCount)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidPosition,
                    "Dead-wall slot " + deadWallIndex + " is outside 0..13.");
            }

            TilePosition handPosition = _positions[handTile.Value];
            if (handPosition.Zone != TileZone.Hand || handPosition.Seat != seat)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.SourceMismatch,
                    "Tile " + handTile + " is not in seat " + seat + "'s hand.");
            }

            if (IsTrackedDrawnTile(handTile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.DrawnTileNotClosed,
                    "Tile " + handTile + " is the current drawn tile, not a closed hand tile.");
            }

            if (!TryGetTileAt(TilePosition.DeadWall(deadWallIndex), out TileId deadWallTile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.MissingTile,
                    "No dead-wall tile at slot " + deadWallIndex + ".");
            }

            if (deadWallIndex < RinshanPointer)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidPosition,
                    "Dead-wall slot " + deadWallIndex + " has already been consumed.");
            }

            if (Contains(_doraIndicators, deadWallTile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidPosition,
                    "Dead-wall tile " + deadWallTile + " is a revealed dora indicator.");
            }

            return Apply(
                new[]
                {
                    new TileMove(handTile, handPosition, TilePosition.DeadWall(deadWallIndex)),
                    new TileMove(deadWallTile, TilePosition.DeadWall(deadWallIndex), handPosition)
                },
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                deadWallTile);
        }

        /// <summary>Equally swaps a hand tile with a recorded dead-wall replacement slot (BeamSwap REPLACEMENT).</summary>
        public TransitionResult SwapHandWithReplacement(int seat, TileId handTile, int replacementIndex)
        {
            if (seat < 0 || seat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (replacementIndex < 0 || replacementIndex >= ReplacementCount)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.InvalidPosition,
                    "Replacement slot " + replacementIndex + " is outside 0.." + (ReplacementCount - 1) + ".");
            }

            TilePosition handPosition = _positions[handTile.Value];
            if (handPosition.Zone != TileZone.Hand || handPosition.Seat != seat)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.SourceMismatch,
                    "Tile " + handTile + " is not in seat " + seat + "'s hand.");
            }

            if (IsTrackedDrawnTile(handTile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.DrawnTileNotClosed,
                    "Tile " + handTile + " is the current drawn tile, not a closed hand tile.");
            }

            if (!TryGetTileAt(TilePosition.DeadWallReplacement(replacementIndex), out TileId replacementTile))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.MissingTile,
                    "No replacement tile at slot " + replacementIndex + ".");
            }

            return Apply(
                new[]
                {
                    new TileMove(handTile, handPosition, TilePosition.DeadWallReplacement(replacementIndex)),
                    new TileMove(replacementTile, TilePosition.DeadWallReplacement(replacementIndex), handPosition)
                },
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                replacementTile);
        }

        /// <summary>
        /// Swaps the just-drawn rinshan tile with dead-wall[rinshanPointer] (V4 SleeveBloom
        /// kan path). The immutable state carries <see cref="RinshanDrawEvidence"/>; the
        /// caller cannot substitute an arbitrary hand tile. RinshanPointer 1..3 also bounds
        /// the physical kan/rinshan draws: slot 4 is exhausted and has no next slot.
        /// </summary>
        public TransitionResult SwapDrawnWithNextRinshan(int seat, TileId drawnTile)
        {
            if (seat < 0 || seat > 3)
            {
                return TransitionResult.Failure(TransitionErrorCode.InvalidSeat, "Seat must be 0..3.");
            }

            if (RinshanPointer < 1 || RinshanPointer >= 4)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.DeadWallExhausted,
                    "RinshanPointer must be in 1..3 for a next-rinshan swap, was " + RinshanPointer + ".");
            }

            if (KanCount >= 4)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.DeadWallExhausted,
                    "Four-kan limit reached.");
            }

            if (_rinshanDraw == null)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.RinshanDrawEvidenceMismatch,
                    "No rinshan draw is open for a next-rinshan swap.");
            }

            RinshanDrawEvidence evidence = _rinshanDraw.Value;
            if (evidence.Seat != seat || evidence.Tile != drawnTile ||
                evidence.DeadWallIndex != RinshanPointer - 1)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.RinshanDrawEvidenceMismatch,
                    "Tile " + drawnTile + " is not the recorded rinshan draw of seat " + seat + ".");
            }

            TilePosition handPosition = _positions[drawnTile.Value];
            if (handPosition.Zone != TileZone.Hand || handPosition.Seat != seat)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.RinshanDrawEvidenceMismatch,
                    "Recorded rinshan tile " + drawnTile + " is no longer in seat " + seat + "'s hand.");
            }

            if (TryGetTileAt(TilePosition.DeadWall(evidence.DeadWallIndex), out _))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.RinshanDrawEvidenceMismatch,
                    "Recorded rinshan slot " + evidence.DeadWallIndex + " is not empty.");
            }

            if (!TryGetTileAt(TilePosition.DeadWall(RinshanPointer), out TileId nextRinshan))
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.MissingTile,
                    "No next rinshan tile at dead-wall slot " + RinshanPointer + ".");
            }

            return Apply(
                new[]
                {
                    new TileMove(drawnTile, handPosition, TilePosition.DeadWall(RinshanPointer)),
                    new TileMove(nextRinshan, TilePosition.DeadWall(RinshanPointer), handPosition)
                },
                WallPointer,
                WallEndExclusive,
                RinshanPointer,
                nextRinshan,
                replaceDrawnTile: true,
                nextDrawnTile: new DrawnTileEvidence(nextRinshan, seat, DrawSource.Rinshan));
        }

        public override string ToString()
        {
            return "TilePositionMap(wall=" + WallRemaining +
                   ", pointer=" + WallPointer +
                   ", dead=" + CountAt(TileZone.DeadWall) +
                   ", replacements=" + ReplacementCount +
                   ", revision=" + Revision +
                   ", hash=" + TileStateHash.Format(StateHash) + ")";
        }

        /// <summary>Rechecks the 136-tile and V4 dead-wall invariants.</summary>
        public static ConservationReport Validate(TilePositionMap map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            var errors = new List<string>();
            var occupied = new Dictionary<TilePosition, TileId>();
            int assigned = 0;
            int unassigned = 0;
            int deadWall = 0;
            int replacements = 0;

            for (int tileValue = 0; tileValue < TileId.Count; tileValue++)
            {
                var tile = new TileId(tileValue);
                TilePosition position = map._positions[tileValue];
                if (!position.IsAssigned)
                {
                    unassigned++;
                    continue;
                }

                assigned++;
                if (occupied.TryGetValue(position, out TileId existing))
                {
                    errors.Add(
                        "Position " + position + " is occupied by both " + existing + " and " + tile + ".");
                }
                else
                {
                    occupied.Add(position, tile);
                }

                switch (position.Zone)
                {
                    case TileZone.Wall:
                        if (position.Index >= map.WallEndExclusive)
                        {
                            errors.Add(
                                "Wall tile " + tile + " is at slot " + position.Index +
                                " but WallEndExclusive is " + map.WallEndExclusive + ".");
                        }

                        break;
                    case TileZone.DeadWall:
                        deadWall++;
                        if (position.Index < map.RinshanPointer)
                        {
                            errors.Add(
                                "Dead-wall slot " + position.Index + " is before RinshanPointer " +
                                map.RinshanPointer + " but still holds " + tile + ".");
                        }

                        break;
                    case TileZone.DeadWallReplacement:
                        replacements++;
                        if (position.Index >= map.ReplacementCount)
                        {
                            errors.Add(
                                "Replacement slot " + position.Index + " is outside the recorded " +
                                map.ReplacementCount + " replacements.");
                        }

                        break;
                }
            }

            if (map.WallPointer < 0 || map.WallPointer > LiveWallSlotCount)
            {
                errors.Add("WallPointer is outside 0..122: " + map.WallPointer + ".");
            }

            if (map.WallEndExclusive < 0 || map.WallEndExclusive > LiveWallSlotCount)
            {
                errors.Add("WallEndExclusive is outside 0..122: " + map.WallEndExclusive + ".");
            }

            if (map.WallPointer > map.WallEndExclusive)
            {
                errors.Add(
                    "WallPointer " + map.WallPointer + " exceeds WallEndExclusive " +
                    map.WallEndExclusive + ".");
            }

            if (map.RinshanPointer < 0 || map.RinshanPointer > 4)
            {
                errors.Add("RinshanPointer is outside 0..4: " + map.RinshanPointer + ".");
            }

            if (map.KanCount < 0 || map.KanCount > 4)
            {
                errors.Add("KanCount is outside 0..4: " + map.KanCount + ".");
            }

            if (map.ReplacementCount < 0 || map.ReplacementCount > 4)
            {
                errors.Add("ReplacementCount is outside 0..4: " + map.ReplacementCount + ".");
            }

            if (map.ReplacementCount != map.RinshanPointer)
            {
                errors.Add(
                    "ReplacementCount " + map.ReplacementCount +
                    " does not match RinshanPointer " + map.RinshanPointer + ".");
            }

            if (unassigned > 0)
            {
                if (unassigned != TileId.Count)
                {
                    errors.Add(
                        "Partially unassigned table: " + unassigned +
                        " unassigned tiles and " + assigned + " assigned tiles.");
                }

                if (map.WallPointer != 0 || map.WallEndExclusive != 0 ||
                    map.RinshanPointer != 0 || map.ReplacementCount != 0 ||
                    deadWall != 0 || map.LastDiscard != null ||
                    map.LastRinshanDraw != null || map.DrawnTile != null ||
                    map.DoraIndicators.Count != 0)
                {
                    errors.Add("An unassigned standard set must have no wall/dead-wall or draw/discard evidence.");
                }
            }
            else
            {
                int effectiveDeadWall = deadWall + replacements;
                if (effectiveDeadWall != DeadWallSlotCount)
                {
                    errors.Add(
                        "Effective dead wall must contain 14 tiles, got " + effectiveDeadWall +
                        " (dead=" + deadWall + ", replacements=" + replacements + ").");
                }
            }

            LastDiscardEvidence? lastDiscard = map.LastDiscard;
            if (lastDiscard != null)
            {
                LastDiscardEvidence evidence = lastDiscard.Value;
                if (evidence.Position.Zone != TileZone.Discard)
                {
                    errors.Add("Last-discard evidence is not in a discard slot: " + evidence.Position + ".");
                }
                else if (!map.TryGetTileAt(evidence.Position, out TileId actual) || actual != evidence.Tile)
                {
                    errors.Add(
                        "Last-discard evidence for " + evidence.Tile +
                        " does not match the tile at " + evidence.Position + ".");
                }

                if (evidence.Seat < 0 || evidence.Seat > 3)
                {
                    errors.Add("Last-discard evidence has invalid seat " + evidence.Seat + ".");
                }
            }

            if (map._doraIndicators.Length > 5)
            {
                errors.Add("Revealed dora indicators exceed the five-indicator limit.");
            }

            var doraSeen = new HashSet<int>();
            for (int i = 0; i < map._doraIndicators.Length; i++)
            {
                TileId indicator = map._doraIndicators[i];
                TilePosition position = map._positions[indicator.Value];
                if (position.Zone != TileZone.DeadWall || position.Index < 4)
                {
                    errors.Add(
                        "Dora indicator " + indicator + " is not in an unrevealed dead-wall slot.");
                }

                if (!doraSeen.Add(indicator.Value))
                {
                    errors.Add("Dora indicator " + indicator + " is listed more than once.");
                }
            }

            RinshanDrawEvidence? rinshanDraw = map.LastRinshanDraw;
            if (rinshanDraw != null)
            {
                RinshanDrawEvidence evidence = rinshanDraw.Value;
                if (map.RinshanPointer < 1 || map.RinshanPointer > 4)
                {
                    errors.Add("Rinshan draw evidence requires RinshanPointer 1..4.");
                }

                if (evidence.DeadWallIndex != map.RinshanPointer - 1)
                {
                    errors.Add(
                        "Rinshan draw evidence slot " + evidence.DeadWallIndex +
                        " does not match RinshanPointer " + map.RinshanPointer + ".");
                }

                if (evidence.Seat < 0 || evidence.Seat > 3)
                {
                    errors.Add("Rinshan draw evidence has invalid seat " + evidence.Seat + ".");
                }

                TilePosition drawnPosition = map._positions[evidence.Tile.Value];
                if (drawnPosition.Zone != TileZone.Hand || drawnPosition.Seat != evidence.Seat)
                {
                    errors.Add(
                        "Rinshan draw tile " + evidence.Tile +
                        " is not in seat " + evidence.Seat + "'s hand.");
                }

                if (map.TryGetTileAt(TilePosition.DeadWall(evidence.DeadWallIndex), out _))
                {
                    errors.Add(
                        "Rinshan draw slot " + evidence.DeadWallIndex + " is not empty.");
                }
            }

            DrawnTileEvidence? drawnTile = map.DrawnTile;
            if (drawnTile != null)
            {
                DrawnTileEvidence evidence = drawnTile.Value;
                TilePosition position = map._positions[evidence.Tile.Value];
                if (position.Zone != TileZone.Hand || position.Seat != evidence.Seat)
                {
                    errors.Add(
                        "Drawn tile " + evidence.Tile + " is not in seat " + evidence.Seat + "'s hand.");
                }

                if (rinshanDraw != null)
                {
                    RinshanDrawEvidence rinshan = rinshanDraw.Value;
                    if (rinshan.Tile != evidence.Tile || rinshan.Seat != evidence.Seat)
                    {
                        errors.Add("Drawn-tile evidence does not match the pending rinshan draw.");
                    }
                }
            }
            else if (rinshanDraw != null)
            {
                errors.Add("A pending rinshan draw requires matching drawn-tile evidence.");
            }

            ulong hash = TileStateHash.Compute(map);
            return new ConservationReport(
                errors,
                assigned,
                unassigned,
                deadWall,
                replacements,
                hash);
        }

        public static void RequireValid(TilePositionMap map)
        {
            ConservationReport report = Validate(map);
            if (!report.IsValid)
            {
                throw new ConservationException(report);
            }
        }

        private static TilePosition[] BuildDense(IEnumerable<TilePlacement> placements)
        {
            if (placements == null)
            {
                throw new ArgumentNullException(nameof(placements));
            }

            var positions = new TilePosition[TileId.Count];
            var seenTiles = new bool[TileId.Count];
            var seenPositions = new Dictionary<TilePosition, TileId>();
            int count = 0;

            foreach (TilePlacement placement in placements)
            {
                count++;
                int tileValue = placement.Tile.Value;
                if (seenTiles[tileValue])
                {
                    throw new ArgumentException(
                        "Duplicate physical tile " + placement.Tile + " in placement map.",
                        nameof(placements));
                }

                seenTiles[tileValue] = true;
                positions[tileValue] = placement.Position;
                if (placement.Position.IsAssigned)
                {
                    if (seenPositions.TryGetValue(placement.Position, out TileId existing))
                    {
                        throw new ArgumentException(
                            "Position " + placement.Position + " is occupied by both " +
                            existing + " and " + placement.Tile + ".",
                            nameof(placements));
                    }

                    seenPositions.Add(placement.Position, placement.Tile);
                }
            }

            if (count != TileId.Count)
            {
                throw new ArgumentException(
                    "Placement map must contain exactly 136 tiles, got " + count + ".",
                    nameof(placements));
            }

            for (int i = 0; i < seenTiles.Length; i++)
            {
                if (!seenTiles[i])
                {
                    throw new ArgumentException(
                        "Missing physical tile T" + i + " from placement map.",
                        nameof(placements));
                }
            }

            return positions;
        }

        private static TilePosition[] CreateWallPositions(RecordedRandomDraw recorded)
        {
            var positions = new TilePosition[TileId.Count];
            for (int i = 0; i < recorded.WallOrder.Count; i++)
            {
                TileId tile = recorded.WallOrder[i];
                positions[tile.Value] = i < LiveWallSlotCount
                    ? TilePosition.Wall(i)
                    : TilePosition.DeadWall(i - LiveWallSlotCount);
            }

            return positions;
        }

        private static TileId FindTileAt(TilePosition[] positions, TilePosition target)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                if (positions[i] == target)
                {
                    return new TileId(i);
                }
            }

            throw new InvalidOperationException("No tile at position " + target + ".");
        }

        private static TilePosition[] BuildDealtPositions(TilePosition[] preDeal, int dealerSeat)
        {
            var next = new TilePosition[preDeal.Length];
            Array.Copy(preDeal, next, preDeal.Length);
            for (int k = 0; k < 4; k++)
            {
                int seat = (dealerSeat + k) % 4;
                int handSlot = 0;
                for (int round = 0; round < 3; round++)
                {
                    int baseSlot = (round * 16) + (4 * k);
                    for (int i = 0; i < 4; i++)
                    {
                        MoveWallSlot(next, baseSlot + i, TilePosition.Hand(seat, handSlot));
                        handSlot++;
                    }
                }

                MoveWallSlot(next, 48 + k, TilePosition.Hand(seat, handSlot));
                handSlot++;
                if (k == 0)
                {
                    MoveWallSlot(next, 52, TilePosition.Hand(seat, handSlot));
                }
            }

            return next;
        }

        private static void MoveWallSlot(TilePosition[] positions, int wallSlot, TilePosition destination)
        {
            TilePosition source = TilePosition.Wall(wallSlot);
            for (int i = 0; i < positions.Length; i++)
            {
                if (positions[i] == source)
                {
                    positions[i] = destination;
                    return;
                }
            }

            throw new InvalidOperationException("No tile at pre-deal wall slot " + wallSlot + ".");
        }

        private TransitionResult Apply(
            IReadOnlyList<TileMove> moves,
            int wallPointer,
            int wallEndExclusive,
            int rinshanPointer,
            TileId? resultTile = null,
            bool replaceLastDiscard = false,
            LastDiscardEvidence? nextLastDiscard = null,
            RinshanDrawEvidence? nextRinshanDraw = null,
            int? nextKanCount = null,
            bool replaceDrawnTile = false,
            DrawnTileEvidence? nextDrawnTile = null)
        {
            if (moves == null)
            {
                throw new ArgumentNullException(nameof(moves));
            }

            if (moves.Count == 0)
            {
                return TransitionResult.Failure(
                    TransitionErrorCode.NoMoves,
                    "A transition must contain at least one physical move.");
            }

            var next = new TilePosition[_positions.Length];
            Array.Copy(_positions, next, _positions.Length);
            var movingTiles = new HashSet<int>();
            var targets = new HashSet<TilePosition>();

            for (int i = 0; i < moves.Count; i++)
            {
                TileMove move = moves[i];
                if (!move.From.IsAssigned)
                {
                    return TransitionResult.Failure(
                        TransitionErrorCode.InvalidPosition,
                        "Move source must be an assigned position: " + move);
                }

                if (_positions[move.Tile.Value] != move.From)
                {
                    return TransitionResult.Failure(
                        TransitionErrorCode.SourceMismatch,
                        "Tile " + move.Tile + " is at " + _positions[move.Tile.Value] +
                        ", expected " + move.From + ".");
                }

                if (!move.To.IsAssigned)
                {
                    return TransitionResult.Failure(
                        TransitionErrorCode.TargetUnassigned,
                        "Move target must be an assigned position: " + move);
                }

                if (!movingTiles.Add(move.Tile.Value))
                {
                    return TransitionResult.Failure(
                        TransitionErrorCode.DuplicateTile,
                        "Tile " + move.Tile + " is moved more than once.");
                }

                if (!targets.Add(move.To))
                {
                    return TransitionResult.Failure(
                        TransitionErrorCode.DuplicateTarget,
                        "Position " + move.To + " is targeted more than once.");
                }
            }

            for (int tileValue = 0; tileValue < TileId.Count; tileValue++)
            {
                if (movingTiles.Contains(tileValue))
                {
                    continue;
                }

                TilePosition position = _positions[tileValue];
                if (!position.IsAssigned)
                {
                    continue;
                }

                if (targets.Contains(position))
                {
                    return TransitionResult.Failure(
                        TransitionErrorCode.PositionOccupied,
                        "Position " + position + " is occupied by " + new TileId(tileValue) + ".");
                }
            }

            for (int i = 0; i < moves.Count; i++)
            {
                next[moves[i].Tile.Value] = moves[i].To;
            }

            try
            {
                var state = new TilePositionMap(
                    next,
                    wallPointer,
                    wallEndExclusive,
                    rinshanPointer,
                    Revision + 1,
                    nextKanCount ?? KanCount,
                    replaceLastDiscard ? nextLastDiscard : _lastDiscard,
                    nextRinshanDraw,
                    replaceDrawnTile ? nextDrawnTile : _drawnTile,
                    _doraIndicators);
                return TransitionResult.Success(state, resultTile);
            }
            catch (ConservationException ex)
            {
                return TransitionResult.Failure(TransitionErrorCode.ConservationViolation, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return TransitionResult.Failure(TransitionErrorCode.ConservationViolation, ex.Message);
            }
        }

        private TransitionError? ValidateCallableDiscard(
            int fromSeat,
            TileId calledTile,
            out TilePosition position)
        {
            position = TilePosition.Unassigned;
            if (_lastDiscard == null)
            {
                return new TransitionError(
                    TransitionErrorCode.CalledTileNotLatestDiscard,
                    "No discard is currently open to calls.");
            }

            LastDiscardEvidence evidence = _lastDiscard.Value;
            if (evidence.Seat != fromSeat || evidence.Tile != calledTile)
            {
                return new TransitionError(
                    TransitionErrorCode.CalledTileNotLatestDiscard,
                    "Called tile " + calledTile + " is not the open discard of seat " + fromSeat + ".");
            }

            if (_positions[evidence.Tile.Value] != evidence.Position)
            {
                return new TransitionError(
                    TransitionErrorCode.CalledTileNotLatestDiscard,
                    "Last-discard evidence is stale; the tile is no longer at " + evidence.Position + ".");
            }

            if (!TryGetTileAt(evidence.Position, out TileId actual) || actual != evidence.Tile)
            {
                return new TransitionError(
                    TransitionErrorCode.CalledTileNotLatestDiscard,
                    "Last-discard evidence does not match the tile at " + evidence.Position + ".");
            }

            position = evidence.Position;
            return null;
        }

        private int FindFirstWallIndex() => FindFirstWallIndex(null);

        private int FindFirstWallIndex(Func<TileId, bool>? isProtected)
        {
            int best = -1;
            for (int tileValue = 0; tileValue < TileId.Count; tileValue++)
            {
                TilePosition position = _positions[tileValue];
                if (position.Zone != TileZone.Wall || position.Index >= WallEndExclusive)
                {
                    continue;
                }

                if (isProtected != null && isProtected(new TileId(tileValue)))
                {
                    continue;
                }

                if (best < 0 || position.Index < best)
                {
                    best = position.Index;
                }
            }

            return best;
        }

        private int FindLastWallIndex() => FindLastWallIndex(null);

        private int FindLastWallIndex(Func<TileId, bool>? isProtected)
        {
            int best = -1;
            for (int tileValue = 0; tileValue < TileId.Count; tileValue++)
            {
                TilePosition position = _positions[tileValue];
                if (position.Zone != TileZone.Wall || position.Index >= WallEndExclusive)
                {
                    continue;
                }

                if (isProtected != null && isProtected(new TileId(tileValue)))
                {
                    continue;
                }

                if (position.Index > best)
                {
                    best = position.Index;
                }
            }

            return best;
        }

        private int FindFreeSlot(TileZone zone, int seat, int maxIndex)
        {
            for (int index = 0; index <= maxIndex; index++)
            {
                TilePosition position;
                switch (zone)
                {
                    case TileZone.Hand:
                        position = TilePosition.Hand(seat, index);
                        break;
                    case TileZone.Discard:
                        position = TilePosition.Discard(seat, index);
                        break;
                    case TileZone.Meld:
                        position = TilePosition.Meld(seat, index);
                        break;
                    case TileZone.Sleeve:
                        position = TilePosition.Sleeve(seat, index);
                        break;
                    case TileZone.Wall:
                        position = TilePosition.Wall(index);
                        break;
                    case TileZone.DeadWall:
                        position = TilePosition.DeadWall(index);
                        break;
                    case TileZone.DeadWallReplacement:
                        position = TilePosition.DeadWallReplacement(index);
                        break;
                    default:
                        return -1;
                }

                if (IsPositionFree(position))
                {
                    return index;
                }
            }

            return -1;
        }

        private int FindFreeMeldBase(int seat, int count)
        {
            for (int baseSlot = 0; baseSlot < MeldGroupCount * MeldGroupSize; baseSlot += MeldGroupSize)
            {
                bool free = true;
                for (int i = 0; i < count; i++)
                {
                    if (!IsPositionFree(TilePosition.Meld(seat, baseSlot + i)))
                    {
                        free = false;
                        break;
                    }
                }

                if (free)
                {
                    return baseSlot;
                }
            }

            return -1;
        }

        private int FindAddedKanBase(int seat, TileId tile)
        {
            for (int baseSlot = 0; baseSlot < MeldGroupCount * MeldGroupSize; baseSlot += MeldGroupSize)
            {
                if (!IsPositionFree(TilePosition.Meld(seat, baseSlot + 3)))
                {
                    continue;
                }

                bool sameType = true;
                for (int i = 0; i < 3; i++)
                {
                    if (!TryGetTileAt(TilePosition.Meld(seat, baseSlot + i), out TileId existing) ||
                        !existing.SameType(tile) || existing == tile)
                    {
                        sameType = false;
                        break;
                    }
                }

                if (sameType)
                {
                    return baseSlot;
                }
            }

            return -1;
        }

        private bool IsInHand(int seat, TileId tile)
        {
            TilePosition position = _positions[tile.Value];
            return position.Zone == TileZone.Hand && position.Seat == seat;
        }

        private bool IsTrackedDrawnTile(TileId tile)
        {
            if (_drawnTile != null && _drawnTile.Value.Tile == tile)
            {
                return true;
            }

            return _rinshanDraw != null && _rinshanDraw.Value.Tile == tile;
        }

        private static bool HasDuplicates(IReadOnlyList<TileId> tiles)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                for (int j = i + 1; j < tiles.Count; j++)
                {
                    if (tiles[i] == tiles[j])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Contains(IReadOnlyList<TileId> tiles, TileId tile)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == tile)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
