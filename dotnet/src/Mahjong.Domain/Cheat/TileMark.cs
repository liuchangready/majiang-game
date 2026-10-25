using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain.Cheat
{
    /// <summary>Stable failure codes for tile-mark state transitions (V4 TileMarkState rules).</summary>
    public enum TileMarkError : byte
    {
        None = 0,
        NoRoundRegistered = 1,
        RoundAlreadySettled = 2,
        RoundIdMismatch = 3,
        RoundOrdinalMismatch = 4,
        PreviousRoundNotSettled = 5,
        GroupIdConflict = 6,
        GroupLimitExceeded = 7,
        TileLimitExceeded = 8,
        TileOverlap = 9,
        InvalidSeat = 10,
        InvalidKeepSeats = 11,
        InvalidRoundId = 12
    }

    /// <summary>Result of a tile-mark state transition; failures carry no state and mutate nothing.</summary>
    public readonly struct TileMarkTransition
    {
        private TileMarkTransition(bool success, TileMarkState state, TileMarkError error)
        {
            Success = success;
            State = state;
            Error = error;
        }

        public bool Success { get; }

        /// <summary>Next state on success; the unchanged source state for no-op replays.</summary>
        public TileMarkState State { get; }

        public TileMarkError Error { get; }

        public static TileMarkTransition Applied(TileMarkState state) => new TileMarkTransition(true, state, TileMarkError.None);

        public static TileMarkTransition Failed(TileMarkState source, TileMarkError error) =>
            new TileMarkTransition(false, source, error);
    }

    /// <summary>
    /// One immutable cross-round tile-mark group: exactly four distinct physical tiles owned
    /// by one seat, created in a registered round and expiring after three further dealt
    /// rounds (V4 <c>TileMarkGroup</c>, RULE-CONTRACT §7.1).
    /// </summary>
    public sealed class TileMarkGroup : IEquatable<TileMarkGroup>
    {
        private readonly ReadOnlyCollection<TileId> _tileIds;

        public TileMarkGroup(string groupId, int ownerSeat, IReadOnlyList<TileId> tileIds, string createdRoundId, int createdRoundOrdinal)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                throw new ArgumentException("Group id cannot be blank.", nameof(groupId));
            }

            if (ownerSeat < 0 || ownerSeat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(ownerSeat), ownerSeat, "Owner seat must be 0..3.");
            }

            if (tileIds == null || tileIds.Count != 4)
            {
                throw new ArgumentException("A tile mark group holds exactly four tiles.", nameof(tileIds));
            }

            var seen = new bool[TileId.Count];
            var copy = new TileId[4];
            for (int i = 0; i < 4; i++)
            {
                TileId tile = tileIds[i];
                if (seen[tile.Value])
                {
                    throw new ArgumentException("Tile mark group contains duplicate tile " + tile + ".", nameof(tileIds));
                }

                seen[tile.Value] = true;
                copy[i] = tile;
            }

            if (string.IsNullOrWhiteSpace(createdRoundId))
            {
                throw new ArgumentException("Created round id cannot be blank.", nameof(createdRoundId));
            }

            const int maxOrdinal = int.MaxValue - 3;
            if (createdRoundOrdinal < 1 || createdRoundOrdinal > maxOrdinal)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(createdRoundOrdinal),
                    createdRoundOrdinal,
                    "Created round ordinal must be in 1.." + maxOrdinal + ".");
            }

            GroupId = groupId;
            OwnerSeat = ownerSeat;
            _tileIds = new ReadOnlyCollection<TileId>(copy);
            CreatedRoundId = createdRoundId;
            CreatedRoundOrdinal = createdRoundOrdinal;
            ExpiresAfterRoundOrdinal = createdRoundOrdinal + 3;
        }

        public string GroupId { get; }

        public int OwnerSeat { get; }

        public IReadOnlyList<TileId> TileIds => _tileIds;

        public string CreatedRoundId { get; }

        public int CreatedRoundOrdinal { get; }

        /// <summary>The group survives only while the dealt round ordinal is strictly below this value.</summary>
        public int ExpiresAfterRoundOrdinal { get; }

        public bool Equals(TileMarkGroup? other) =>
            other != null &&
            GroupId == other.GroupId &&
            OwnerSeat == other.OwnerSeat &&
            CreatedRoundId == other.CreatedRoundId &&
            CreatedRoundOrdinal == other.CreatedRoundOrdinal &&
            ExpiresAfterRoundOrdinal == other.ExpiresAfterRoundOrdinal &&
            TileIdsEqual(_tileIds, other._tileIds);

        private static bool TileIdsEqual(ReadOnlyCollection<TileId> left, ReadOnlyCollection<TileId> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is TileMarkGroup other && Equals(other);

        public override int GetHashCode()
        {
            int hash = GroupId.GetHashCode(StringComparison.Ordinal);
            hash = hash * 31 + OwnerSeat;
            hash = hash * 31 + CreatedRoundOrdinal;
            for (int i = 0; i < _tileIds.Count; i++)
            {
                hash = hash * 31 + _tileIds[i].Value;
            }

            return hash;
        }

        public override string ToString() =>
            GroupId + "[seat" + OwnerSeat + " n=" + _tileIds.Count + " exp<" + ExpiresAfterRoundOrdinal + "]";
    }

    /// <summary>
    /// Immutable cross-round tile-mark state (V4 <c>TileMarkState</c>): the registry of dealt
    /// rounds plus the currently live mark groups per seat. Every mutation returns a new
    /// instance; rejected mutations return the source instance untouched.
    /// </summary>
    public sealed class TileMarkState
    {
        private readonly ReadOnlyCollection<string> _dealtRoundIds;
        private readonly ReadOnlyCollection<string> _settledRoundIds;
        private readonly ReadOnlyCollection<TileMarkGroup> _groups;

        private TileMarkState(
            long version,
            int dealtRoundOrdinal,
            string[] dealtRoundIds,
            string[] settledRoundIds,
            TileMarkGroup[] groups)
        {
            Version = version;
            DealtRoundOrdinal = dealtRoundOrdinal;
            _dealtRoundIds = new ReadOnlyCollection<string>(dealtRoundIds);
            _settledRoundIds = new ReadOnlyCollection<string>(settledRoundIds);
            _groups = new ReadOnlyCollection<TileMarkGroup>(groups);
        }

        public TileMarkState()
            : this(0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<TileMarkGroup>())
        {
        }

        /// <summary>
        /// Rehydrates an already validated checkpoint aggregate. It is a recovery constructor,
        /// not a command path: every group/ordinal/seat/capacity/overlap invariant is rechecked
        /// and violations throw instead of silently dropping private state.
        /// </summary>
        public static TileMarkState Restore(
            long version,
            int dealtRoundOrdinal,
            IReadOnlyList<string> dealtRoundIds,
            IReadOnlyList<string> settledRoundIds,
            IReadOnlyList<TileMarkGroup> groups)
        {
            if (version < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(version), version, "Tile mark version must be non-negative.");
            }

            if (dealtRoundOrdinal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dealtRoundOrdinal), dealtRoundOrdinal, "Dealt round ordinal must be non-negative.");
            }

            if (dealtRoundIds == null || settledRoundIds == null || groups == null)
            {
                throw new ArgumentNullException(nameof(dealtRoundIds));
            }

            var dealt = new string[dealtRoundIds.Count];
            var seenRounds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < dealtRoundIds.Count; i++)
            {
                string id = dealtRoundIds[i];
                if (string.IsNullOrWhiteSpace(id) || !seenRounds.Add(id))
                {
                    throw new ArgumentException("Dealt round ids must be non-blank and unique.", nameof(dealtRoundIds));
                }

                dealt[i] = id;
            }

            var settled = new string[settledRoundIds.Count];
            var seenSettled = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < settledRoundIds.Count; i++)
            {
                string id = settledRoundIds[i];
                if (string.IsNullOrWhiteSpace(id) || !seenRounds.Contains(id) || !seenSettled.Add(id))
                {
                    throw new ArgumentException("Settled round ids must refer to a unique dealt round.", nameof(settledRoundIds));
                }

                settled[i] = id;
            }

            if (dealtRoundOrdinal != dealt.Length)
            {
                throw new ArgumentException("Dealt round ordinal must equal the dealt round id count.", nameof(dealtRoundOrdinal));
            }

            var groupCopy = new TileMarkGroup[groups.Count];
            var perSeatGroups = new int[4];
            var perSeatTiles = new int[4];
            var marked = new HashSet<int>[4];
            for (int seat = 0; seat < 4; seat++)
            {
                marked[seat] = new HashSet<int>();
            }

            for (int i = 0; i < groups.Count; i++)
            {
                TileMarkGroup group = groups[i] ?? throw new ArgumentException("Mark groups cannot contain null.", nameof(groups));
                if (group.OwnerSeat < 0 || group.OwnerSeat > 3)
                {
                    throw new ArgumentException("Mark group owner seat is invalid.", nameof(groups));
                }

                if (++perSeatGroups[group.OwnerSeat] > 3)
                {
                    throw new ArgumentException("A seat cannot own more than three mark groups.", nameof(groups));
                }

                perSeatTiles[group.OwnerSeat] += group.TileIds.Count;
                if (perSeatTiles[group.OwnerSeat] > 12)
                {
                    throw new ArgumentException("A seat cannot mark more than twelve tiles.", nameof(groups));
                }

                if (!seenRounds.Contains(group.CreatedRoundId))
                {
                    throw new ArgumentException("A mark group refers to an unknown dealt round.", nameof(groups));
                }

                for (int t = 0; t < group.TileIds.Count; t++)
                {
                    if (!marked[group.OwnerSeat].Add(group.TileIds[t].Value))
                    {
                        throw new ArgumentException("A seat cannot mark the same physical tile twice.", nameof(groups));
                    }
                }

                groupCopy[i] = group;
            }

            return new TileMarkState(version, dealtRoundOrdinal, dealt, settled, groupCopy);
        }

        public long Version { get; }

        /// <summary>Count of successfully dealt rounds; the current round ordinal when non-zero.</summary>
        public int DealtRoundOrdinal { get; }

        public IReadOnlyList<string> DealtRoundIds => _dealtRoundIds;

        public IReadOnlyList<string> SettledRoundIds => _settledRoundIds;

        public bool IsSettled(string roundId)
        {
            for (int i = 0; i < _settledRoundIds.Count; i++)
            {
                if (_settledRoundIds[i] == roundId)
                {
                    return true;
                }
            }

            return false;
        }

        public IReadOnlyList<TileMarkGroup> Groups => _groups;

        public IReadOnlyList<TileMarkGroup> GroupsForSeat(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            var matches = new List<TileMarkGroup>();
            for (int i = 0; i < _groups.Count; i++)
            {
                if (_groups[i].OwnerSeat == seat)
                {
                    matches.Add(_groups[i]);
                }
            }

            return new ReadOnlyCollection<TileMarkGroup>(matches);
        }

        /// <summary>Every currently marked tile id for the seat, group order preserved.</summary>
        public IReadOnlyList<TileId> MarkedTilesForSeat(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            var tiles = new List<TileId>();
            for (int i = 0; i < _groups.Count; i++)
            {
                if (_groups[i].OwnerSeat != seat)
                {
                    continue;
                }

                for (int t = 0; t < _groups[i].TileIds.Count; t++)
                {
                    tiles.Add(_groups[i].TileIds[t]);
                }
            }

            return new ReadOnlyCollection<TileId>(tiles);
        }

        public string? CurrentRoundId =>
            _dealtRoundIds.Count == 0 ? null : _dealtRoundIds[_dealtRoundIds.Count - 1];

        /// <summary>
        /// Registers a newly dealt round. The same id replays as the unchanged instance; a new
        /// id requires the previous dealt round to be settled first and advances the ordinal.
        /// </summary>
        public TileMarkTransition RegisterDealtRound(string roundId)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                return TileMarkTransition.Failed(this, TileMarkError.InvalidRoundId);
            }

            if (CurrentRoundId == roundId)
            {
                return TileMarkTransition.Applied(this);
            }

            if (_dealtRoundIds.Count > 0 && !IsSettled(_dealtRoundIds[_dealtRoundIds.Count - 1]))
            {
                return TileMarkTransition.Failed(this, TileMarkError.PreviousRoundNotSettled);
            }

            var nextDealt = new string[_dealtRoundIds.Count + 1];
            for (int i = 0; i < _dealtRoundIds.Count; i++)
            {
                nextDealt[i] = _dealtRoundIds[i];
            }

            nextDealt[_dealtRoundIds.Count] = roundId;
            return TileMarkTransition.Applied(new TileMarkState(
                Version + 1,
                DealtRoundOrdinal + 1,
                nextDealt,
                SnapshotSettled(),
                SnapshotGroups()));
        }

        /// <summary>
        /// Adds a mark group for the current unsettled round. The same group id with identical
        /// content replays as the unchanged instance; a conflicting definition is rejected.
        /// Per seat at most three groups and twelve tiles, and no tile overlap within a seat;
        /// different seats may mark the same physical tile.
        /// </summary>
        public TileMarkTransition AddGroup(TileMarkGroup group)
        {
            if (group == null)
            {
                throw new ArgumentNullException(nameof(group));
            }

            string? current = CurrentRoundId;
            if (current == null)
            {
                return TileMarkTransition.Failed(this, TileMarkError.NoRoundRegistered);
            }

            if (IsSettled(current))
            {
                return TileMarkTransition.Failed(this, TileMarkError.RoundAlreadySettled);
            }

            if (group.CreatedRoundId != current)
            {
                return TileMarkTransition.Failed(this, TileMarkError.RoundIdMismatch);
            }

            if (group.CreatedRoundOrdinal != DealtRoundOrdinal)
            {
                return TileMarkTransition.Failed(this, TileMarkError.RoundOrdinalMismatch);
            }

            int existingIndex = IndexOfGroup(group.GroupId);
            if (existingIndex >= 0)
            {
                return _groups[existingIndex].Equals(group)
                    ? TileMarkTransition.Applied(this)
                    : TileMarkTransition.Failed(this, TileMarkError.GroupIdConflict);
            }

            int seatGroups = 0;
            int seatTiles = 0;
            var marked = new HashSet<int>();
            for (int i = 0; i < _groups.Count; i++)
            {
                TileMarkGroup existing = _groups[i];
                if (existing.OwnerSeat != group.OwnerSeat)
                {
                    continue;
                }

                seatGroups++;
                seatTiles += existing.TileIds.Count;
                for (int t = 0; t < existing.TileIds.Count; t++)
                {
                    marked.Add(existing.TileIds[t].Value);
                }
            }

            if (seatGroups >= 3)
            {
                return TileMarkTransition.Failed(this, TileMarkError.GroupLimitExceeded);
            }

            if (seatTiles + group.TileIds.Count > 12)
            {
                return TileMarkTransition.Failed(this, TileMarkError.TileLimitExceeded);
            }

            for (int t = 0; t < group.TileIds.Count; t++)
            {
                if (marked.Contains(group.TileIds[t].Value))
                {
                    return TileMarkTransition.Failed(this, TileMarkError.TileOverlap);
                }
            }

            var nextGroups = new TileMarkGroup[_groups.Count + 1];
            for (int i = 0; i < _groups.Count; i++)
            {
                nextGroups[i] = _groups[i];
            }

            nextGroups[_groups.Count] = group;
            return TileMarkTransition.Applied(new TileMarkState(
                Version + 1,
                DealtRoundOrdinal,
                SnapshotDealt(),
                SnapshotSettled(),
                nextGroups));
        }

        /// <summary>
        /// Settles the current dealt round and drops every group whose expiry window has
        /// closed (<c>ExpiresAfterRoundOrdinal &lt;= DealtRoundOrdinal</c>). Settling an
        /// already settled id replays as the unchanged instance.
        /// </summary>
        public TileMarkTransition CompleteRound(string roundId)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                return TileMarkTransition.Failed(this, TileMarkError.InvalidRoundId);
            }

            if (IsSettled(roundId))
            {
                return TileMarkTransition.Applied(this);
            }

            if (CurrentRoundId != roundId)
            {
                return TileMarkTransition.Failed(this, TileMarkError.RoundIdMismatch);
            }

            var nextSettled = new string[_settledRoundIds.Count + 1];
            for (int i = 0; i < _settledRoundIds.Count; i++)
            {
                nextSettled[i] = _settledRoundIds[i];
            }

            nextSettled[_settledRoundIds.Count] = roundId;

            int survivors = 0;
            var nextGroups = new TileMarkGroup[_groups.Count];
            for (int i = 0; i < _groups.Count; i++)
            {
                if (_groups[i].ExpiresAfterRoundOrdinal > DealtRoundOrdinal)
                {
                    nextGroups[survivors++] = _groups[i];
                }
            }

            Array.Resize(ref nextGroups, survivors);
            return TileMarkTransition.Applied(new TileMarkState(
                Version + 1,
                DealtRoundOrdinal,
                SnapshotDealt(),
                nextSettled,
                nextGroups));
        }

        /// <summary>Clears every group of one seat; rounds and settlement history are untouched.</summary>
        public TileMarkTransition ClearSeat(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                return TileMarkTransition.Failed(this, TileMarkError.InvalidSeat);
            }

            return ClearExcept(new[] { seat } , invert: true);
        }

        /// <summary>
        /// Keeps only groups owned by <paramref name="keepSeats"/> (see-through settlement);
        /// an empty set clears everything. No change returns the same instance so multiple
        /// concurrent see-through settlements stay deterministic.
        /// </summary>
        public TileMarkTransition ClearExcept(IReadOnlyCollection<int> keepSeats)
        {
            return ClearExcept(keepSeats, invert: false);
        }

        public TileMarkTransition ClearAll()
        {
            if (_groups.Count == 0)
            {
                return TileMarkTransition.Applied(this);
            }

            return TileMarkTransition.Applied(new TileMarkState(
                Version + 1,
                DealtRoundOrdinal,
                SnapshotDealt(),
                SnapshotSettled(),
                Array.Empty<TileMarkGroup>()));
        }

        private TileMarkTransition ClearExcept(IReadOnlyCollection<int> seats, bool invert)
        {
            if (seats == null)
            {
                throw new ArgumentNullException(nameof(seats));
            }

            var keep = new bool[4];
            foreach (int seat in seats)
            {
                if (seat < 0 || seat > 3)
                {
                    return TileMarkTransition.Failed(this, TileMarkError.InvalidSeat);
                }

                keep[seat] = true;
            }

            int survivors = 0;
            var nextGroups = new TileMarkGroup[_groups.Count];
            for (int i = 0; i < _groups.Count; i++)
            {
                bool ownerKept = keep[_groups[i].OwnerSeat];
                if (invert ? !ownerKept : ownerKept)
                {
                    nextGroups[survivors++] = _groups[i];
                }
            }

            if (survivors == _groups.Count)
            {
                return TileMarkTransition.Applied(this);
            }

            Array.Resize(ref nextGroups, survivors);
            return TileMarkTransition.Applied(new TileMarkState(
                Version + 1,
                DealtRoundOrdinal,
                SnapshotDealt(),
                SnapshotSettled(),
                nextGroups));
        }

        private int IndexOfGroup(string groupId)
        {
            for (int i = 0; i < _groups.Count; i++)
            {
                if (_groups[i].GroupId == groupId)
                {
                    return i;
                }
            }

            return -1;
        }

        private string[] SnapshotDealt()
        {
            var copy = new string[_dealtRoundIds.Count];
            for (int i = 0; i < _dealtRoundIds.Count; i++)
            {
                copy[i] = _dealtRoundIds[i];
            }

            return copy;
        }

        private string[] SnapshotSettled()
        {
            var copy = new string[_settledRoundIds.Count];
            for (int i = 0; i < _settledRoundIds.Count; i++)
            {
                copy[i] = _settledRoundIds[i];
            }

            return copy;
        }

        private TileMarkGroup[] SnapshotGroups()
        {
            var copy = new TileMarkGroup[_groups.Count];
            for (int i = 0; i < _groups.Count; i++)
            {
                copy[i] = _groups[i];
            }

            return copy;
        }
    }
}
