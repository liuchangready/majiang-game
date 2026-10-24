using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Mahjong.Domain
{
    public enum ReserveScope : byte
    {
        Round = 0,
        Session = 1
    }

    /// <summary>
    /// V4 time control: 3,000 ms free time per decision plus a 20,000 ms reserve, reset per
    /// round by default. All inputs are explicit monotonic milliseconds; this type never
    /// reads a wall clock, so a persisted UTC instant cannot leak into deadline arithmetic.
    /// </summary>
    public sealed class DecisionClockConfig
    {
        public DecisionClockConfig(
            long baseMillis = 3000L,
            long reserveMillis = 20000L,
            ReserveScope reserveScope = ReserveScope.Round)
        {
            if (baseMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(baseMillis), baseMillis, "baseMillis must be non-negative.");
            }

            if (reserveMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(reserveMillis), reserveMillis, "reserveMillis must be non-negative.");
            }

            try
            {
                _ = checked(baseMillis + reserveMillis);
            }
            catch (OverflowException ex)
            {
                throw new ArgumentException("baseMillis + reserveMillis overflows Int64.", ex);
            }

            BaseMillis = baseMillis;
            ReserveMillis = reserveMillis;
            ReserveScope = reserveScope;
        }

        public long BaseMillis { get; }

        public long ReserveMillis { get; }

        public ReserveScope ReserveScope { get; }
    }

    public readonly struct DecisionKey : IEquatable<DecisionKey>
    {
        public DecisionKey(string roundId, int seat, string decisionId)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("roundId cannot be blank.", nameof(roundId));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (string.IsNullOrWhiteSpace(decisionId))
            {
                throw new ArgumentException("decisionId cannot be blank.", nameof(decisionId));
            }

            RoundId = roundId;
            Seat = seat;
            DecisionId = decisionId;
        }

        public string RoundId { get; }

        public int Seat { get; }

        public string DecisionId { get; }

        public bool Equals(DecisionKey other) =>
            Seat == other.Seat &&
            string.Equals(RoundId, other.RoundId, StringComparison.Ordinal) &&
            string.Equals(DecisionId, other.DecisionId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is DecisionKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Seat;
                hash = (hash * 397) ^ (RoundId == null ? 0 : RoundId.GetHashCode());
                hash = (hash * 397) ^ (DecisionId == null ? 0 : DecisionId.GetHashCode());
                return hash;
            }
        }

        public override string ToString() => RoundId + "/" + Seat.ToString(CultureInfo.InvariantCulture) + "/" + DecisionId;

        public static bool operator ==(DecisionKey left, DecisionKey right) => left.Equals(right);

        public static bool operator !=(DecisionKey left, DecisionKey right) => !left.Equals(right);
    }

    public enum DecisionTimingStatus : byte
    {
        Scheduled = 0,
        Active = 1,
        Closed = 2,
        Paused = 3
    }

    public sealed class DecisionTiming
    {
        public DecisionTiming(
            DecisionKey key,
            long generation,
            long opensAtMillis,
            long deadlineMillis,
            long reserveAtOpenMillis,
            long baseMillis,
            DecisionTimingStatus status)
        {
            if (generation < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(generation), generation, "Generation must be positive.");
            }

            if (opensAtMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(opensAtMillis), opensAtMillis, "opensAt must be non-negative.");
            }

            if (deadlineMillis < opensAtMillis)
            {
                throw new ArgumentOutOfRangeException(nameof(deadlineMillis), deadlineMillis, "deadline cannot precede opensAt.");
            }

            if (reserveAtOpenMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(reserveAtOpenMillis), reserveAtOpenMillis, "Reserve must be non-negative.");
            }

            if (baseMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(baseMillis), baseMillis, "Base must be non-negative.");
            }

            _ = checked(baseMillis + reserveAtOpenMillis);
            Key = key;
            Generation = generation;
            OpensAtMillis = opensAtMillis;
            DeadlineMillis = deadlineMillis;
            ReserveAtOpenMillis = reserveAtOpenMillis;
            BaseMillis = baseMillis;
            Status = status;
        }

        public DecisionKey Key { get; }

        public long Generation { get; }

        public long OpensAtMillis { get; }

        public long DeadlineMillis { get; }

        public long ReserveAtOpenMillis { get; }

        public long BaseMillis { get; }

        public DecisionTimingStatus Status { get; }

        public string RoundId => Key.RoundId;

        public int Seat => Key.Seat;

        public string DecisionId => Key.DecisionId;

        public bool IsExpired(long nowMillis) => nowMillis >= DeadlineMillis;

        public long CalculateReserveSpent(long atMillis)
        {
            if (atMillis <= OpensAtMillis)
            {
                return 0L;
            }

            long elapsed = checked(atMillis - OpensAtMillis);
            long overBase = Math.Max(0L, checked(elapsed - BaseMillis));
            return Math.Min(ReserveAtOpenMillis, overBase);
        }

        public long CalculateRemainingReserve(long atMillis) =>
            ReserveAtOpenMillis - CalculateReserveSpent(atMillis);

        internal DecisionTiming With(DecisionKey? key = null, DecisionTimingStatus? status = null)
        {
            return new DecisionTiming(
                key ?? Key,
                Generation,
                OpensAtMillis,
                DeadlineMillis,
                ReserveAtOpenMillis,
                BaseMillis,
                status ?? Status);
        }

        internal DecisionTiming Shifted(long deltaMillis)
        {
            return new DecisionTiming(
                Key,
                Generation,
                checked(OpensAtMillis + deltaMillis),
                checked(DeadlineMillis + deltaMillis),
                ReserveAtOpenMillis,
                BaseMillis,
                Status);
        }
    }

    /// <summary>
    /// Mutable authority-side clock with V4 semantics. Deliberately not a snapshot type: the
    /// table runtime serializes calls on one consumer. Every method takes the monotonic time.
    /// </summary>
    public sealed class DecisionClock
    {
        private readonly DecisionTiming?[] _timings = new DecisionTiming?[4];
        private readonly long[] _reserves = new long[4];
        private long _generationCounter;

        public DecisionClock(DecisionClockConfig? config = null)
        {
            Config = config ?? new DecisionClockConfig();
            for (int seat = 0; seat < 4; seat++)
            {
                _reserves[seat] = Config.ReserveMillis;
            }
        }

        public DecisionClockConfig Config { get; }

        /// <summary>
        /// Exact copy for candidate timing transactions. The actor mutates the clone, persists
        /// the snapshot, and only then installs the clone as live authority.
        /// </summary>
        public DecisionClock Clone()
        {
            var copy = new DecisionClock(Config)
            {
                ActiveRoundId = ActiveRoundId,
                IsPausedState = IsPausedState,
                _pausedAt = _pausedAt,
                _generationCounter = _generationCounter
            };
            for (int seat = 0; seat < 4; seat++)
            {
                copy._timings[seat] = _timings[seat];
                copy._reserves[seat] = _reserves[seat];
            }

            return copy;
        }

        public string? ActiveRoundId { get; private set; }

        public bool IsPausedState { get; private set; }

        private static void CheckSeat(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }
        }

        public void BeginRound(string roundId, long nowMillis)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("roundId cannot be blank.", nameof(roundId));
            }

            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Time must be non-negative.");
            }

            if (string.Equals(ActiveRoundId, roundId, StringComparison.Ordinal))
            {
                return;
            }

            long effectiveNow = IsPausedState && _pausedAt.HasValue ? _pausedAt.Value : nowMillis;
            for (int seat = 0; seat < 4; seat++)
            {
                DecisionTiming? timing = _timings[seat];
                if (timing == null)
                {
                    continue;
                }

                long effectiveAt = timing.Status == DecisionTimingStatus.Paused && _pausedAt.HasValue
                    ? Math.Min(effectiveNow, _pausedAt.Value)
                    : effectiveNow;
                SettleIntoReserve(timing, effectiveAt);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                _timings[seat] = null;
            }

            IsPausedState = false;
            _pausedAt = null;

            if (Config.ReserveScope == ReserveScope.Round)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    _reserves[seat] = Config.ReserveMillis;
                }
            }

            ActiveRoundId = roundId;
        }

        /// <summary>
        /// Opens a decision. Reopening the same key returns the same timing without refreshing;
        /// opening a different key on the same seat throws and has no side effect.
        /// </summary>
        public DecisionTiming Open(DecisionKey key, long opensAtMillis)
        {
            CheckSeat(key.Seat);
            if (opensAtMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(opensAtMillis), opensAtMillis, "Time must be non-negative.");
            }

            if (ActiveRoundId == null)
            {
                ActiveRoundId = key.RoundId;
            }
            else if (!string.Equals(key.RoundId, ActiveRoundId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Decision roundId '" + key.RoundId + "' does not match active roundId '" + ActiveRoundId + "'.");
            }

            DecisionTiming? existing = _timings[key.Seat];
            if (existing != null)
            {
                if (existing.Key == key)
                {
                    return existing;
                }

                throw new InvalidOperationException(
                    "Seat " + key.Seat + " already has decision [" + existing.Key + "], cannot open [" + key + "].");
            }

            long reserveAtOpen = RemainingReserve(key.Seat);
            long totalBudget = checked(Config.BaseMillis + reserveAtOpen);
            long deadline = checked(opensAtMillis + totalBudget);
            _generationCounter = checked(_generationCounter + 1L);
            var timing = new DecisionTiming(
                key,
                _generationCounter,
                opensAtMillis,
                deadline,
                reserveAtOpen,
                Config.BaseMillis,
                IsPausedState ? DecisionTimingStatus.Paused : DecisionTimingStatus.Active);
            _timings[key.Seat] = timing;
            return timing;
        }

        /// <summary>
        /// Reinstalls one decision from an explicit recovery record. The caller supplies the
        /// remaining base/reserve budget and the already-elapsed overdue amount; this method
        /// never derives elapsed time from a persisted UTC instant or from another clock.
        ///
        /// Elapsed time consumes the base allowance first and only then the reserve. The
        /// restored deadline is <c>now + baseRemaining + reserveRemaining</c>. If the overdue
        /// amount consumes the whole remaining budget the decision is due immediately and the
        /// next authoritative tick must arbitrate its timeout.
        /// </summary>
        public DecisionTiming RestoreDecision(
            DecisionKey key,
            long nowMillis,
            long remainingBaseMillis,
            long remainingReserveMillis,
            long overdueMillis)
        {
            CheckSeat(key.Seat);
            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Time must be non-negative.");
            }

            if (remainingBaseMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingBaseMillis),
                    remainingBaseMillis,
                    "Remaining base must be non-negative.");
            }

            if (remainingReserveMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingReserveMillis),
                    remainingReserveMillis,
                    "Remaining reserve must be non-negative.");
            }

            if (overdueMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(overdueMillis),
                    overdueMillis,
                    "Overdue time must be non-negative.");
            }

            if (remainingBaseMillis > Config.BaseMillis)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingBaseMillis),
                    remainingBaseMillis,
                    "Remaining base cannot exceed the configured base allowance.");
            }

            if (remainingReserveMillis > Config.ReserveMillis)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingReserveMillis),
                    remainingReserveMillis,
                    "Remaining reserve cannot exceed the configured reserve.");
            }

            if (ActiveRoundId == null)
            {
                ActiveRoundId = key.RoundId;
            }
            else if (!string.Equals(key.RoundId, ActiveRoundId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Decision roundId '" + key.RoundId + "' does not match active roundId '" + ActiveRoundId + "'.");
            }

            if (_timings[key.Seat] != null)
            {
                throw new InvalidOperationException(
                    "Seat " + key.Seat + " already has a timing; recovery cannot overwrite it.");
            }

            // Base is consumed before reserve. Subtracting from the combined budget and then
            // re-granting part of it as base would hand the seat free base time again.
            long baseRemaining = remainingBaseMillis > overdueMillis
                ? remainingBaseMillis - overdueMillis
                : 0L;
            long reserveOverdue = overdueMillis > remainingBaseMillis
                ? overdueMillis - remainingBaseMillis
                : 0L;
            long reserveRemaining = remainingReserveMillis > reserveOverdue
                ? remainingReserveMillis - reserveOverdue
                : 0L;
            long deadline = checked(nowMillis + checked(baseRemaining + reserveRemaining));
            if (deadline < nowMillis)
            {
                throw new OverflowException("The restored deadline overflowed Int64.");
            }

            _generationCounter = checked(_generationCounter + 1L);
            var timing = new DecisionTiming(
                key,
                _generationCounter,
                nowMillis,
                deadline,
                reserveRemaining,
                baseRemaining,
                IsPausedState ? DecisionTimingStatus.Paused : DecisionTimingStatus.Active);
            _timings[key.Seat] = timing;
            _reserves[key.Seat] = reserveRemaining;
            return timing;
        }

        public DecisionTiming? Timing(int seat)
        {
            CheckSeat(seat);
            return _timings[seat];
        }

        public long RemainingReserve(int seat)
        {
            CheckSeat(seat);
            return _reserves[seat];
        }

        /// <summary>Snapshot of all four remaining reserves; recovery must carry these explicitly.</summary>
        public IReadOnlyList<long> RemainingReserves()
        {
            var copy = new long[4];
            Array.Copy(_reserves, copy, 4);
            return new ReadOnlyCollection<long>(copy);
        }

        /// <summary>
        /// Installs an explicit recovery reserve for one seat without opening a decision. The
        /// value may not exceed the configured per-round/session reserve.
        /// </summary>
        public void RestoreReserve(int seat, long reserveMillis)
        {
            CheckSeat(seat);
            if (reserveMillis < 0 || reserveMillis > Config.ReserveMillis)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reserveMillis),
                    reserveMillis,
                    "A restored reserve must be in 0..configured reserve.");
            }

            _reserves[seat] = reserveMillis;
        }

        /// <summary>
        /// Clears open timings while preserving the reserve ledger and active round id. Used
        /// when entering recovery; the reserve must survive until the baseline proves whether
        /// it is still valid.
        /// </summary>
        public void ClearTimings()
        {
            for (int seat = 0; seat < 4; seat++)
            {
                _timings[seat] = null;
            }

            IsPausedState = false;
            _pausedAt = null;
        }

        /// <summary>Completes strictly before the deadline; exactly at the deadline is late.</summary>
        public bool Complete(DecisionKey key, long atMillis)
        {
            CheckSeat(key.Seat);
            if (atMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(atMillis), atMillis, "Time must be non-negative.");
            }

            if (IsPausedState)
            {
                return false;
            }

            DecisionTiming? current = _timings[key.Seat];
            if (current == null || current.Key != key || current.Status != DecisionTimingStatus.Active)
            {
                return false;
            }

            if (atMillis >= current.DeadlineMillis)
            {
                return false;
            }

            SettleIntoReserve(current, atMillis);
            _timings[key.Seat] = null;
            return true;
        }

        /// <summary>Pure read: returns due active decisions without consuming them.</summary>
        public IReadOnlyList<DecisionTiming> Expired(long nowMillis)
        {
            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Time must be non-negative.");
            }

            var result = new List<DecisionTiming>();
            if (IsPausedState)
            {
                return new ReadOnlyCollection<DecisionTiming>(result);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                DecisionTiming? timing = _timings[seat];
                if (timing != null &&
                    timing.Status == DecisionTimingStatus.Active &&
                    nowMillis >= timing.DeadlineMillis)
                {
                    result.Add(timing);
                }
            }

            return new ReadOnlyCollection<DecisionTiming>(result);
        }

        /// <summary>Expires one exact generation at/after its deadline; otherwise no side effect.</summary>
        public bool Expire(DecisionKey key, long generation, long nowMillis)
        {
            CheckSeat(key.Seat);
            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Time must be non-negative.");
            }

            if (IsPausedState)
            {
                return false;
            }

            DecisionTiming? current = _timings[key.Seat];
            if (current == null ||
                current.Key != key ||
                current.Generation != generation ||
                current.Status != DecisionTimingStatus.Active ||
                nowMillis < current.DeadlineMillis)
            {
                return false;
            }

            _reserves[key.Seat] = 0L;
            _timings[key.Seat] = null;
            return true;
        }

        /// <summary>Cancels and settles elapsed reserve; pause time is not charged.</summary>
        public bool Cancel(DecisionKey key, long atMillis)
        {
            CheckSeat(key.Seat);
            if (atMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(atMillis), atMillis, "Time must be non-negative.");
            }

            DecisionTiming? current = _timings[key.Seat];
            if (current == null || current.Key != key || current.Status == DecisionTimingStatus.Closed)
            {
                return false;
            }

            long effectiveAt = (IsPausedState || current.Status == DecisionTimingStatus.Paused) && _pausedAt.HasValue
                ? Math.Min(atMillis, _pausedAt.Value)
                : atMillis;
            SettleIntoReserve(current, effectiveAt);
            _timings[key.Seat] = null;
            return true;
        }

        /// <summary>Atomically re-keys the active decision while preserving deadline/generation.</summary>
        public bool Rekey(DecisionKey oldKey, DecisionKey newKey)
        {
            CheckSeat(oldKey.Seat);
            CheckSeat(newKey.Seat);
            if (oldKey.Seat != newKey.Seat)
            {
                throw new ArgumentException("Rekey must keep the same seat.");
            }

            DecisionTiming? current = _timings[oldKey.Seat];
            if (current == null || current.Key != oldKey)
            {
                return false;
            }

            if (oldKey == newKey)
            {
                return true;
            }

            _timings[oldKey.Seat] = current.With(key: newKey);
            return true;
        }

        public void Pause(long nowMillis)
        {
            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Time must be non-negative.");
            }

            if (IsPausedState)
            {
                return;
            }

            IsPausedState = true;
            _pausedAt = nowMillis;
            for (int seat = 0; seat < 4; seat++)
            {
                DecisionTiming? timing = _timings[seat];
                if (timing != null && timing.Status == DecisionTimingStatus.Active)
                {
                    _timings[seat] = timing.With(status: DecisionTimingStatus.Paused);
                }
            }
        }

        public void Resume(long nowMillis)
        {
            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Time must be non-negative.");
            }

            if (!IsPausedState)
            {
                return;
            }

            long pausedDuration = _pausedAt.HasValue && nowMillis > _pausedAt.Value
                ? checked(nowMillis - _pausedAt.Value)
                : 0L;
            for (int seat = 0; seat < 4; seat++)
            {
                DecisionTiming? timing = _timings[seat];
                if (timing != null && timing.Status == DecisionTimingStatus.Paused)
                {
                    _timings[seat] = timing.Shifted(pausedDuration).With(status: DecisionTimingStatus.Active);
                }
            }

            IsPausedState = false;
            _pausedAt = null;
        }

        /// <summary>Clears authority state but never rewinds the generation counter.</summary>
        public void Reset()
        {
            for (int seat = 0; seat < 4; seat++)
            {
                _timings[seat] = null;
                _reserves[seat] = Config.ReserveMillis;
            }

            ActiveRoundId = null;
            IsPausedState = false;
            _pausedAt = null;
        }

        private long? _pausedAt;

        private void SettleIntoReserve(DecisionTiming timing, long effectiveAtMillis)
        {
            if (effectiveAtMillis <= timing.OpensAtMillis)
            {
                _reserves[timing.Seat] = timing.ReserveAtOpenMillis;
                return;
            }

            long spent = timing.CalculateReserveSpent(effectiveAtMillis);
            _reserves[timing.Seat] = timing.ReserveAtOpenMillis - spent;
        }
    }
}
