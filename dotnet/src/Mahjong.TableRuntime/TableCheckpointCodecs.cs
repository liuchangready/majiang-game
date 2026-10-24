using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.TableRuntime
{
    /// <summary>Durable control-authority snapshot (policy, seat epochs/status, reclaim preference).</summary>
    public sealed class TableControlCheckpoint
    {
        public TableControlCheckpoint(ControlRegistrySnapshot snapshot, long controlVersion)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            if (controlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlVersion), controlVersion, "Control version must be non-negative.");
            }

            ControlVersion = controlVersion;
        }

        public ControlRegistrySnapshot Snapshot { get; }

        public long ControlVersion { get; }
    }

    public static class TableControlCheckpointCodec
    {
        public const int FormatVersion = 1;
        private const int Magic = 0x4D4A4343; // MJCC

        public static byte[] Encode(TableControlCheckpoint checkpoint)
        {
            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(checkpoint.ControlVersion);
                DisconnectPolicy policy = checkpoint.Snapshot.Policy;
                writer.Write((byte)policy.Mode);
                writer.Write(policy.GraceMillis);
                WriteNullableString(writer, policy.AiProfileId);
                for (int seat = 0; seat < 4; seat++)
                {
                    SeatControlState state = checkpoint.Snapshot.Seats[seat];
                    writer.Write(state.Seat);
                    writer.Write((byte)state.Status);
                    writer.Write((byte)state.Kind);
                    writer.Write(state.ControlEpoch);
                    writer.Write(state.DisconnectedAtMillis.HasValue);
                    if (state.DisconnectedAtMillis.HasValue)
                    {
                        writer.Write(state.DisconnectedAtMillis.Value);
                    }

                    writer.Write(state.GraceDeadlineMillis.HasValue);
                    if (state.GraceDeadlineMillis.HasValue)
                    {
                        writer.Write(state.GraceDeadlineMillis.Value);
                    }

                    WriteNullableString(writer, state.AiProfileId);
                }

                for (int seat = 0; seat < 4; seat++)
                {
                    writer.Write((byte)checkpoint.Snapshot.PreferredHumanKinds[seat]);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static TableControlCheckpoint Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Control checkpoint payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Control checkpoint magic is invalid.");
                }

                int schema = reader.ReadInt32();
                if (schema != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported control checkpoint schema " + schema + ".");
                }

                long controlVersion = reader.ReadInt64();
                byte mode = reader.ReadByte();
                if (!Enum.IsDefined(typeof(DisconnectMode), mode))
                {
                    throw new InvalidDataException("Control checkpoint policy mode is invalid.");
                }

                long grace = reader.ReadInt64();
                string? aiProfile = ReadNullableString(reader);
                var policy = new DisconnectPolicy((DisconnectMode)mode, grace, aiProfile);
                var seats = new SeatControlState[4];
                for (int seat = 0; seat < 4; seat++)
                {
                    int value = reader.ReadInt32();
                    byte status = reader.ReadByte();
                    byte kind = reader.ReadByte();
                    long epoch = reader.ReadInt64();
                    long? disconnected = reader.ReadBoolean() ? reader.ReadInt64() : (long?)null;
                    long? deadline = reader.ReadBoolean() ? reader.ReadInt64() : (long?)null;
                    string? profile = ReadNullableString(reader);
                    if (!Enum.IsDefined(typeof(ConnectionStatus), status) ||
                        !Enum.IsDefined(typeof(SeatControllerKind), kind))
                    {
                        throw new InvalidDataException("Control checkpoint seat enum is invalid.");
                    }

                    seats[seat] = new SeatControlState(
                        value,
                        (ConnectionStatus)status,
                        (SeatControllerKind)kind,
                        epoch,
                        disconnected,
                        deadline,
                        profile);
                }

                var preferred = new SeatControllerKind[4];
                for (int seat = 0; seat < 4; seat++)
                {
                    byte kind = reader.ReadByte();
                    if (!Enum.IsDefined(typeof(SeatControllerKind), kind))
                    {
                        throw new InvalidDataException("Control checkpoint preferred kind is invalid.");
                    }

                    preferred[seat] = (SeatControllerKind)kind;
                }

                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Control checkpoint payload has trailing bytes.");
                }

                return new TableControlCheckpoint(new ControlRegistrySnapshot(policy, seats, preferred), controlVersion);
            }
        }

        private static void WriteNullableString(BinaryWriter writer, string? value)
        {
            writer.Write(value != null);
            if (value != null)
            {
                writer.Write(value);
            }
        }

        private static string? ReadNullableString(BinaryReader reader) =>
            reader.ReadBoolean() ? reader.ReadString() : null;
    }

    /// <summary>Durable decision-clock checkpoint: all four reserves and every open decision.</summary>
    public sealed class TableTimingCheckpoint
    {
        private readonly ReadOnlyCollection<long> _reserves;
        private readonly ReadOnlyCollection<TableRecoveredDecision> _decisions;

        public TableTimingCheckpoint(
            string roundId,
            IReadOnlyList<long> seatReservesMillis,
            IReadOnlyList<TableRecoveredDecision> decisions,
            ulong capturedAtUtcMs = 0UL,
            long capturedMonotonicMillis = 0L,
            string bootId = "")
        {
            RoundId = roundId ?? string.Empty;
            if (capturedMonotonicMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capturedMonotonicMillis),
                    capturedMonotonicMillis,
                    "Captured monotonic time must be non-negative.");
            }

            BootId = bootId ?? string.Empty;
            CapturedAtUtcMs = capturedAtUtcMs;
            CapturedMonotonicMillis = capturedMonotonicMillis;
            if (seatReservesMillis == null || seatReservesMillis.Count != 4)
            {
                throw new ArgumentException("Timing checkpoint requires four seat reserves.", nameof(seatReservesMillis));
            }

            var reserves = new long[4];
            for (int seat = 0; seat < 4; seat++)
            {
                if (seatReservesMillis[seat] < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(seatReservesMillis), "Reserve cannot be negative.");
                }

                reserves[seat] = seatReservesMillis[seat];
            }

            var copy = new List<TableRecoveredDecision>(decisions == null ? 0 : decisions.Count);
            if (decisions != null)
            {
                for (int i = 0; i < decisions.Count; i++)
                {
                    copy.Add(decisions[i] ?? throw new ArgumentException("Decision cannot be null.", nameof(decisions)));
                }
            }

            _reserves = new ReadOnlyCollection<long>(reserves);
            _decisions = new ReadOnlyCollection<TableRecoveredDecision>(copy);
        }

        public string RoundId { get; }

        public IReadOnlyList<long> SeatReservesMillis => _reserves;

        public IReadOnlyList<TableRecoveredDecision> Decisions => _decisions;

        public ulong CapturedAtUtcMs { get; }

        public long CapturedMonotonicMillis { get; }

        public string BootId { get; }

        public TableRecoveryTiming ToRecoveryTiming() =>
            new TableRecoveryTiming(_decisions, _reserves);
    }

    public static class TableTimingCheckpointCodec
    {
        public const int FormatVersion = 2;
        private const int Magic = 0x4D4A5443; // MJTC

        public static byte[] Encode(TableTimingCheckpoint checkpoint)
        {
            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(checkpoint.RoundId);
                writer.Write(checkpoint.CapturedAtUtcMs);
                writer.Write(checkpoint.CapturedMonotonicMillis);
                writer.Write(checkpoint.BootId);
                for (int seat = 0; seat < 4; seat++)
                {
                    writer.Write(checkpoint.SeatReservesMillis[seat]);
                }

                writer.Write(checkpoint.Decisions.Count);
                for (int i = 0; i < checkpoint.Decisions.Count; i++)
                {
                    TableRecoveredDecision decision = checkpoint.Decisions[i];
                    writer.Write(decision.Key.RoundId);
                    writer.Write(decision.Key.Seat);
                    writer.Write(decision.Key.DecisionId);
                    writer.Write(decision.ControlEpoch);
                    writer.Write(decision.DrawnTile.HasValue);
                    if (decision.DrawnTile.HasValue)
                    {
                        writer.Write(decision.DrawnTile.Value.Value);
                    }

                    writer.Write(decision.RemainingBaseMillis);
                    writer.Write(decision.RemainingReserveMillis);
                    writer.Write(decision.OverdueMillis);
                    writer.Write(decision.Candidates.Count);
                    for (int c = 0; c < decision.Candidates.Count; c++)
                    {
                        LegalActionOption option = decision.Candidates[c];
                        writer.Write((byte)option.Kind);
                        writer.Write(option.Seat);
                        writer.Write(option.Tile.HasValue);
                        if (option.Tile.HasValue)
                        {
                            writer.Write(option.Tile.Value.Value);
                        }

                        writer.Write(option.OptionId != null);
                        if (option.OptionId != null)
                        {
                            writer.Write(option.OptionId);
                        }
                    }
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static TableTimingCheckpoint Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Timing checkpoint payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Timing checkpoint magic is invalid.");
                }

                int schema = reader.ReadInt32();
                if (schema != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported timing checkpoint schema " + schema + ".");
                }

                string roundId = reader.ReadString();
                ulong capturedUtc = reader.ReadUInt64();
                long capturedMonotonic = reader.ReadInt64();
                string bootId = reader.ReadString();
                var reserves = new long[4];
                for (int seat = 0; seat < 4; seat++)
                {
                    reserves[seat] = reader.ReadInt64();
                    if (reserves[seat] < 0)
                    {
                        throw new InvalidDataException("Timing checkpoint reserve is negative.");
                    }
                }

                int count = reader.ReadInt32();
                if (count < 0 || count > 4)
                {
                    throw new InvalidDataException("Timing checkpoint decision count is invalid.");
                }

                var decisions = new List<TableRecoveredDecision>(count);
                for (int i = 0; i < count; i++)
                {
                    var key = new DecisionKey(reader.ReadString(), reader.ReadInt32(), reader.ReadString());
                    long controlEpoch = reader.ReadInt64();
                    TileId? drawn = reader.ReadBoolean() ? new TileId(reader.ReadInt32()) : (TileId?)null;
                    long baseRemaining = reader.ReadInt64();
                    long reserveRemaining = reader.ReadInt64();
                    long overdue = reader.ReadInt64();
                    int optionCount = reader.ReadInt32();
                    if (optionCount < 0 || optionCount > 64)
                    {
                        throw new InvalidDataException("Timing checkpoint candidate count is invalid.");
                    }

                    var candidates = new List<LegalActionOption>(optionCount);
                    for (int c = 0; c < optionCount; c++)
                    {
                        byte kind = reader.ReadByte();
                        int seat = reader.ReadInt32();
                        TileId? tile = reader.ReadBoolean() ? new TileId(reader.ReadInt32()) : (TileId?)null;
                        string? optionId = reader.ReadBoolean() ? reader.ReadString() : null;
                        if (!Enum.IsDefined(typeof(LegalActionKind), kind))
                        {
                            throw new InvalidDataException("Timing checkpoint candidate kind is invalid.");
                        }

                        candidates.Add(new LegalActionOption((LegalActionKind)kind, seat, tile, optionId));
                    }

                    decisions.Add(new TableRecoveredDecision(
                        key,
                        controlEpoch,
                        candidates,
                        drawn,
                        baseRemaining,
                        reserveRemaining,
                        overdue));
                }

                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Timing checkpoint payload has trailing bytes.");
                }

                return new TableTimingCheckpoint(
                    roundId,
                    reserves,
                    decisions,
                    capturedUtc,
                    capturedMonotonic,
                    bootId);
            }
        }
    }

    /// <summary>Strict cheat-ledger checkpoint. It is real Domain ledger data, not a summary flag.</summary>
    public sealed class TableCheatLedgerCheckpoint
    {
        private readonly ReadOnlyCollection<int> _balances;
        private readonly ReadOnlyCollection<CheatLedgerEntry> _entries;

        public TableCheatLedgerCheckpoint(
            int initialPoints,
            CheatLedger ledger,
            long totalVersion = -1L,
            ulong fingerprint = 0UL)
        {
            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }

            if (initialPoints < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialPoints), initialPoints, "Initial points must be non-negative.");
            }

            var emptyState = new CheatState(ledger, new TileMarkState(), new SleeveBloomState(), new BeamSwapState());
            TotalVersion = totalVersion < 0 ? emptyState.Version : totalVersion;
            Fingerprint = fingerprint == 0 ? emptyState.Fingerprint : fingerprint;

            var balances = new int[4];
            for (int seat = 0; seat < 4; seat++)
            {
                balances[seat] = ledger.BalanceOf(seat);
            }

            var entries = new CheatLedgerEntry[ledger.Entries.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = ledger.Entries[i];
            }

            InitialPoints = initialPoints;
            _balances = new ReadOnlyCollection<int>(balances);
            _entries = new ReadOnlyCollection<CheatLedgerEntry>(entries);
        }

        public int InitialPoints { get; }

        public IReadOnlyList<int> Balances => _balances;

        public IReadOnlyList<CheatLedgerEntry> Entries => _entries;

        /// <summary>Total CheatState version (ledger + private substates).</summary>
        public long TotalVersion { get; }

        /// <summary>CheatState fingerprint for the modeled aggregate.</summary>
        public ulong Fingerprint { get; }

        /// <summary>
        /// Rebuilds the real CheatState. If the persisted total version exceeds the ledger
        /// version, private substate content exists that this task does not model; recovery
        /// fails closed instead of silently replacing it with empty substates.
        /// </summary>
        public CheatState RestoreCheatState()
        {
            CheatLedger ledger = CheatLedger.FromSnapshot(InitialPoints, _balances, _entries);
            if (TotalVersion != ledger.Version)
            {
                throw new InvalidOperationException(
                    "Cheat private substate version " + TotalVersion +
                    " is not modeled; ledger version is " + ledger.Version + ".");
            }

            var state = new CheatState(ledger, new TileMarkState(), new SleeveBloomState(), new BeamSwapState());
            if (state.Fingerprint != Fingerprint)
            {
                throw new InvalidOperationException("Recovered cheat aggregate fingerprint does not match.");
            }

            return state;
        }

        /// <summary>
        /// Derives the initial points from the real ledger history. With no entries, all four
        /// balances must already be equal; with entries, each seat's first entry must derive the
        /// same initial value. Ambiguity fails closed rather than guessing.
        /// </summary>
        public static TableCheatLedgerCheckpoint FromLedger(CheatLedger ledger)
        {
            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }

            int? initial = null;
            var firstSeenSeat = new bool[4];
            for (int i = 0; i < ledger.Entries.Count; i++)
            {
                CheatLedgerEntry entry = ledger.Entries[i];
                if (firstSeenSeat[entry.Seat])
                {
                    continue;
                }

                firstSeenSeat[entry.Seat] = true;
                int candidate = checked(entry.BalanceAfter - entry.Amount);
                if (candidate < 0 || (initial.HasValue && initial.Value != candidate))
                {
                    throw new InvalidOperationException("Cheat ledger entries do not share one initial point value.");
                }

                initial = candidate;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (!firstSeenSeat[seat])
                {
                    if (initial.HasValue && initial.Value != ledger.BalanceOf(seat))
                    {
                        throw new InvalidOperationException("An untouched cheat seat disagrees with the derived initial points.");
                    }

                    initial = ledger.BalanceOf(seat);
                }
            }

            return new TableCheatLedgerCheckpoint(initial ?? 0, ledger);
        }

        public static TableCheatLedgerCheckpoint FromCheatState(CheatState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var ledgerCheckpoint = FromLedger(state.Ledger);
            return new TableCheatLedgerCheckpoint(
                ledgerCheckpoint.InitialPoints,
                state.Ledger,
                state.Version,
                state.Fingerprint);
        }

        public CheatLedger Restore() => CheatLedger.FromSnapshot(InitialPoints, _balances, _entries);
    }

    public static class TableCheatLedgerCheckpointCodec
    {
        public const int FormatVersion = 2;
        private const int Magic = 0x4D4A4C43; // MJLC

        public static byte[] Encode(TableCheatLedgerCheckpoint checkpoint)
        {
            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(checkpoint.TotalVersion);
                writer.Write(checkpoint.Fingerprint);
                writer.Write(checkpoint.InitialPoints);
                for (int seat = 0; seat < 4; seat++)
                {
                    writer.Write(checkpoint.Balances[seat]);
                }

                writer.Write(checkpoint.Entries.Count);
                for (int i = 0; i < checkpoint.Entries.Count; i++)
                {
                    CheatLedgerEntry entry = checkpoint.Entries[i];
                    writer.Write(entry.EntryId);
                    writer.Write(entry.Seat);
                    writer.Write(entry.Amount);
                    writer.Write(entry.BalanceAfter);
                    writer.Write(entry.Reason);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static TableCheatLedgerCheckpoint Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Cheat checkpoint payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Cheat checkpoint magic is invalid.");
                }

                int schema = reader.ReadInt32();
                if (schema != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported cheat checkpoint schema " + schema + ".");
                }

                long totalVersion = reader.ReadInt64();
                ulong fingerprint = reader.ReadUInt64();
                int initial = reader.ReadInt32();
                var balances = new int[4];
                for (int seat = 0; seat < 4; seat++)
                {
                    balances[seat] = reader.ReadInt32();
                }

                int count = reader.ReadInt32();
                if (count < 0 || count > 100000)
                {
                    throw new InvalidDataException("Cheat checkpoint entry count is invalid.");
                }

                var entries = new CheatLedgerEntry[count];
                for (int i = 0; i < count; i++)
                {
                    entries[i] = new CheatLedgerEntry(
                        reader.ReadString(),
                        reader.ReadInt32(),
                        reader.ReadInt32(),
                        reader.ReadInt32(),
                        reader.ReadString());
                }

                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Cheat checkpoint payload has trailing bytes.");
                }

                // Replays the real ledger entries and verifies balances before returning.
                CheatLedger ledger = CheatLedger.FromSnapshot(initial, balances, entries);
                return new TableCheatLedgerCheckpoint(initial, ledger, totalVersion, fingerprint);
            }
        }
    }
}
