using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Mahjong.Domain.Cheat
{
    /// <summary>Stable failure codes for ledger applications (V4 CheatLedger exception kinds).</summary>
    public enum CheatLedgerError : byte
    {
        None = 0,
        InvalidSeat = 1,
        InvalidEntryId = 2,
        InvalidReason = 3,
        EntryConflict = 4,
        InsufficientFunds = 5,
        BalanceOverflow = 6,
        InvalidSnapshot = 7
    }

    /// <summary>One immutable ledger movement. Value equality covers the full payload.</summary>
    public sealed class CheatLedgerEntry : IEquatable<CheatLedgerEntry>
    {
        public CheatLedgerEntry(string entryId, int seat, int amount, int balanceAfter, string reason)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                throw new ArgumentException("Entry id cannot be blank.", nameof(entryId));
            }

            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("Reason cannot be blank.", nameof(reason));
            }

            EntryId = entryId;
            Seat = seat;
            Amount = amount;
            BalanceAfter = balanceAfter;
            Reason = reason;
        }

        public string EntryId { get; }

        public int Seat { get; }

        public int Amount { get; }

        public int BalanceAfter { get; }

        public string Reason { get; }

        public bool Equals(CheatLedgerEntry? other) =>
            other != null &&
            EntryId == other.EntryId &&
            Seat == other.Seat &&
            Amount == other.Amount &&
            BalanceAfter == other.BalanceAfter &&
            Reason == other.Reason;

        public override bool Equals(object? obj) => obj is CheatLedgerEntry other && Equals(other);

        public override int GetHashCode() =>
            (EntryId.GetHashCode(StringComparison.Ordinal) * 31 + Seat) * 31 + Amount.GetHashCode();

        public override string ToString() =>
            EntryId + "[seat" + Seat.ToString(CultureInfo.InvariantCulture) +
            " " + Amount.ToString("+0;-0;0", CultureInfo.InvariantCulture) +
            " -> " + BalanceAfter.ToString(CultureInfo.InvariantCulture) + "] " + Reason;
    }

    /// <summary>Result of one ledger application; failures carry no ledger and mutate nothing.</summary>
    public readonly struct CheatLedgerApplyResult
    {
        private CheatLedgerApplyResult(bool success, CheatLedger? ledger, CheatLedgerEntry? entry, CheatLedgerError error, bool replayed)
        {
            Success = success;
            Ledger = ledger;
            Entry = entry;
            Error = error;
            Replayed = replayed;
        }

        public bool Success { get; }

        /// <summary>The ledger after a successful apply; null on failure (zero side effects).</summary>
        public CheatLedger? Ledger { get; }

        /// <summary>The applied entry, or the original entry when an identical payload replayed; null on failure.</summary>
        public CheatLedgerEntry? Entry { get; }

        public CheatLedgerError Error { get; }

        /// <summary>True when the same entry id with an identical payload returned the original entry.</summary>
        public bool Replayed { get; }

        public static CheatLedgerApplyResult Applied(CheatLedger ledger, CheatLedgerEntry entry, bool replayed) =>
            new CheatLedgerApplyResult(true, ledger, entry, CheatLedgerError.None, replayed);

        public static CheatLedgerApplyResult Failed(CheatLedgerError error) =>
            new CheatLedgerApplyResult(false, null, default, error, false);
    }

    /// <summary>Aggregate gain/spend/net over the entry list; independent of score sticks (G-041).</summary>
    public readonly struct CheatLedgerSummary
    {
        public CheatLedgerSummary(int totalGained, int totalSpent, int net)
        {
            TotalGained = totalGained;
            TotalSpent = totalSpent;
            Net = net;
        }

        public int TotalGained { get; }

        public int TotalSpent { get; }

        public int Net { get; }
    }

    /// <summary>
    /// Immutable per-seat cheat-point ledger (V4 <c>CheatLedger</c>).
    ///
    /// <para>Idempotency contract: applying an entry id with an identical (seat, amount, reason)
    /// payload returns the original entry and the unchanged ledger; the same id with a different
    /// payload is a conflict. A negative resulting balance, integer overflow, invalid seat or
    /// blank identifiers are all rejected with the source ledger untouched.</para>
    /// </summary>
    public sealed class CheatLedger
    {
        private readonly ReadOnlyCollection<int> _balances;
        private readonly ReadOnlyCollection<CheatLedgerEntry> _entries;

        private CheatLedger(int[] balances, CheatLedgerEntry[] entries, long version)
        {
            _balances = new ReadOnlyCollection<int>(balances);
            _entries = new ReadOnlyCollection<CheatLedgerEntry>(entries);
            Version = version;
        }

        public long Version { get; }

        public IReadOnlyList<int> Balances => _balances;

        public IReadOnlyList<CheatLedgerEntry> Entries => _entries;

        public int BalanceOf(int seat) => _balances[seat];

        public bool CanAfford(int seat, int amount) => amount >= 0 && _balances[seat] >= amount;

        public static CheatLedger Initialize(CheatEconomyConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            return new CheatLedger(
                new[] { config.InitialPoints, config.InitialPoints, config.InitialPoints, config.InitialPoints },
                Array.Empty<CheatLedgerEntry>(),
                0);
        }

        /// <summary>
        /// Rehydrates a ledger from a snapshot and replays the entries against the declared
        /// initial points. The replay must reproduce the snapshot balances exactly; any drift
        /// or invalid entry rejects the whole snapshot.
        /// </summary>
        public static CheatLedger FromSnapshot(int initialPoints, IReadOnlyList<int> balances, IReadOnlyList<CheatLedgerEntry> entries)
        {
            if (initialPoints < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialPoints), initialPoints, "Initial points must be non-negative.");
            }

            if (balances == null)
            {
                throw new ArgumentNullException(nameof(balances));
            }

            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (balances.Count != 4)
            {
                throw new ArgumentException("Balances must contain exactly four entries.", nameof(balances));
            }

            var running = new[] { initialPoints, initialPoints, initialPoints, initialPoints };
            var replayed = new CheatLedgerEntry[entries.Count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                CheatLedgerEntry entry = entries[i];
                if (entry == null)
                {
                    throw new ArgumentException("Snapshot entries cannot be null.", nameof(entries));
                }

                if (!seen.Add(entry.EntryId))
                {
                    throw new ArgumentException("Snapshot contains duplicate entry id '" + entry.EntryId + "'.", nameof(entries));
                }

                int next = checked(running[entry.Seat] + entry.Amount);
                if (next != entry.BalanceAfter || next < 0)
                {
                    throw new ArgumentException(
                        "Snapshot entry '" + entry.EntryId + "' does not replay onto the declared balances.",
                        nameof(entries));
                }

                running[entry.Seat] = next;
                replayed[i] = entry;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (running[seat] != balances[seat])
                {
                    throw new ArgumentException(
                        "Snapshot balance for seat " + seat + " does not match the replayed entries.",
                        nameof(balances));
                }
            }

            return new CheatLedger(running, replayed, entries.Count);
        }

        /// <summary>
        /// Applies one movement. Identical payload replays return the original entry with an
        /// unchanged ledger; conflicting payloads, insufficient funds, overflow and invalid
        /// input fail with zero side effects.
        /// </summary>
        public CheatLedgerApplyResult Apply(string entryId, int seat, int amount, string reason)
        {
            if (seat < 0 || seat > 3)
            {
                return CheatLedgerApplyResult.Failed(CheatLedgerError.InvalidSeat);
            }

            if (string.IsNullOrWhiteSpace(entryId))
            {
                return CheatLedgerApplyResult.Failed(CheatLedgerError.InvalidEntryId);
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                return CheatLedgerApplyResult.Failed(CheatLedgerError.InvalidReason);
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                CheatLedgerEntry existing = _entries[i];
                if (existing.EntryId != entryId)
                {
                    continue;
                }

                if (existing.Seat == seat && existing.Amount == amount && existing.Reason == reason)
                {
                    return CheatLedgerApplyResult.Applied(this, existing, replayed: true);
                }

                return CheatLedgerApplyResult.Failed(CheatLedgerError.EntryConflict);
            }

            int nextBalance;
            try
            {
                nextBalance = checked(_balances[seat] + amount);
            }
            catch (OverflowException)
            {
                return CheatLedgerApplyResult.Failed(CheatLedgerError.BalanceOverflow);
            }

            if (nextBalance < 0)
            {
                return CheatLedgerApplyResult.Failed(CheatLedgerError.InsufficientFunds);
            }

            var nextBalances = new int[4];
            for (int s = 0; s < 4; s++)
            {
                nextBalances[s] = _balances[s];
            }

            nextBalances[seat] = nextBalance;
            var nextEntries = new CheatLedgerEntry[_entries.Count + 1];
            for (int i = 0; i < _entries.Count; i++)
            {
                nextEntries[i] = _entries[i];
            }

            var applied = new CheatLedgerEntry(entryId, seat, amount, nextBalance, reason);
            nextEntries[_entries.Count] = applied;
            return CheatLedgerApplyResult.Applied(
                new CheatLedger(nextBalances, nextEntries, Version + 1),
                applied,
                replayed: false);
        }

        public CheatLedgerSummary Summarize()
        {
            int gained = 0;
            int spent = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                int amount = _entries[i].Amount;
                if (amount > 0)
                {
                    gained = checked(gained + amount);
                }
                else if (amount < 0)
                {
                    spent = checked(spent - amount);
                }
            }

            return new CheatLedgerSummary(gained, spent, checked(gained - spent));
        }
    }
}
