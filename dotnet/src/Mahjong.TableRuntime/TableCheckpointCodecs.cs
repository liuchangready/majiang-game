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
                    writer.Write(decision.RecoveryGraceMillis);
                    writer.Write(decision.RecoveryGraceApplied);
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
                    long recoveryGrace = reader.ReadInt64();
                    bool recoveryGraceApplied = reader.ReadBoolean();
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
                        overdue,
                        recoveryGrace,
                        recoveryGraceApplied));
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

    /// <summary>
    /// Strict cheat checkpoint. It carries the real Domain ledger plus the complete private
    /// aggregate (tile marks, sleeve bloom offers/reservations/windows and beam swap offer).
    /// A ledger-only constructor remains for callers that explicitly have no private substate;
    /// such a payload restores only when its total version/fingerprint prove the substates are
    /// empty. A malformed or unmodelled substate is refused, never replaced with an empty one.
    /// </summary>
    public sealed class TableCheatLedgerCheckpoint
    {
        private readonly ReadOnlyCollection<int> _balances;
        private readonly ReadOnlyCollection<CheatLedgerEntry> _entries;
        private readonly CheatState? _state;

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

        public TableCheatLedgerCheckpoint(CheatState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            TableCheatLedgerCheckpoint ledgerCheckpoint = FromLedger(state.Ledger);
            InitialPoints = ledgerCheckpoint.InitialPoints;
            _balances = ledgerCheckpoint._balances;
            _entries = ledgerCheckpoint._entries;
            TotalVersion = state.Version;
            Fingerprint = state.Fingerprint;
            _state = state;
        }

        public int InitialPoints { get; }

        public IReadOnlyList<int> Balances => _balances;

        public IReadOnlyList<CheatLedgerEntry> Entries => _entries;

        /// <summary>Total CheatState version (ledger + private substates).</summary>
        public long TotalVersion { get; }

        /// <summary>CheatState fingerprint for the modeled aggregate.</summary>
        public ulong Fingerprint { get; }

        internal CheatState? FullState => _state;

        /// <summary>
        /// Rebuilds the real CheatState, or fails closed when the persisted total version /
        /// fingerprint cannot be produced by the modeled substate.
        /// </summary>
        public CheatState RestoreCheatState()
        {
            if (_state != null)
            {
                if (_state.Version != TotalVersion || _state.Fingerprint != Fingerprint)
                {
                    throw new InvalidOperationException("Recovered cheat aggregate metadata does not match its full state.");
                }

                return _state;
            }

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

            return new TableCheatLedgerCheckpoint(state);
        }

        public CheatLedger Restore() => CheatLedger.FromSnapshot(InitialPoints, _balances, _entries);
    }

    public static class TableCheatLedgerCheckpointCodec
    {
        public const int FormatVersion = 4;
        private const int Magic = 0x4D4A4C43; // MJLC

        public static byte[] Encode(TableCheatLedgerCheckpoint checkpoint)
        {
            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            CheatState state = checkpoint.FullState ??
                new CheatState(
                    CheatLedger.FromSnapshot(checkpoint.InitialPoints, checkpoint.Balances, checkpoint.Entries),
                    new TileMarkState(),
                    new SleeveBloomState(),
                    new BeamSwapState());

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

                WriteTileMarks(writer, state.TileMarks);
                WriteSleeveBloom(writer, state.SleeveBloom);
                WriteBeamSwap(writer, state.BeamSwap);

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

                // Replays the real ledger entries and verifies balances before returning.
                CheatLedger ledger = CheatLedger.FromSnapshot(initial, balances, entries);
                TileMarkState tileMarks = ReadTileMarks(reader);
                SleeveBloomState sleeve = ReadSleeveBloom(reader);
                BeamSwapState beam = ReadBeamSwap(reader);

                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Cheat checkpoint payload has trailing bytes.");
                }

                var state = new CheatState(ledger, tileMarks, sleeve, beam);
                if (state.Version != totalVersion || state.Fingerprint != fingerprint)
                {
                    throw new InvalidDataException(
                        "Cheat checkpoint version/fingerprint does not match its full private state.");
                }

                return new TableCheatLedgerCheckpoint(state);
            }
        }

        private static void WriteTileMarks(BinaryWriter writer, TileMarkState state)
        {
            writer.Write(state.Version);
            writer.Write(state.DealtRoundOrdinal);
            writer.Write(state.DealtRoundIds.Count);
            for (int i = 0; i < state.DealtRoundIds.Count; i++)
            {
                writer.Write(state.DealtRoundIds[i]);
            }

            writer.Write(state.SettledRoundIds.Count);
            for (int i = 0; i < state.SettledRoundIds.Count; i++)
            {
                writer.Write(state.SettledRoundIds[i]);
            }

            writer.Write(state.Groups.Count);
            for (int i = 0; i < state.Groups.Count; i++)
            {
                TileMarkGroup group = state.Groups[i];
                writer.Write(group.GroupId);
                writer.Write(group.OwnerSeat);
                writer.Write(group.TileIds.Count);
                for (int t = 0; t < group.TileIds.Count; t++)
                {
                    writer.Write(group.TileIds[t].Value);
                }

                writer.Write(group.CreatedRoundId);
                writer.Write(group.CreatedRoundOrdinal);
            }
        }

        private static TileMarkState ReadTileMarks(BinaryReader reader)
        {
            long version = reader.ReadInt64();
            int dealtOrdinal = reader.ReadInt32();
            int dealtCount = ReadCount(reader, 10000, "dealt round");
            var dealt = new string[dealtCount];
            for (int i = 0; i < dealtCount; i++)
            {
                dealt[i] = reader.ReadString();
            }

            int settledCount = ReadCount(reader, 10000, "settled round");
            var settled = new string[settledCount];
            for (int i = 0; i < settledCount; i++)
            {
                settled[i] = reader.ReadString();
            }

            int groupCount = ReadCount(reader, 12, "tile mark group");
            var groups = new TileMarkGroup[groupCount];
            for (int i = 0; i < groupCount; i++)
            {
                string groupId = reader.ReadString();
                int owner = reader.ReadInt32();
                int tileCount = ReadCount(reader, 4, "tile mark tile");
                var tiles = new TileId[tileCount];
                for (int t = 0; t < tileCount; t++)
                {
                    tiles[t] = new TileId(reader.ReadInt32());
                }

                string createdRoundId = reader.ReadString();
                int createdOrdinal = reader.ReadInt32();
                groups[i] = new TileMarkGroup(groupId, owner, tiles, createdRoundId, createdOrdinal);
            }

            return TileMarkState.Restore(version, dealtOrdinal, dealt, settled, groups);
        }

        private static void WriteSleeveBloom(BinaryWriter writer, SleeveBloomState state)
        {
            writer.Write(state.Version);
            writer.Write(state.WindowSeq);
            writer.Write(state.UsedOnTurn);
            WriteOffer(writer, state.ImmediateOffer);
            writer.Write(state.OffTurnOffers.Count);
            for (int i = 0; i < state.OffTurnOffers.Count; i++)
            {
                WriteOffer(writer, state.OffTurnOffers[i]);
            }

            writer.Write(state.Reservations.Count);
            for (int i = 0; i < state.Reservations.Count; i++)
            {
                SleeveBloomReservation reservation = state.Reservations[i];
                writer.Write(reservation.Seat);
                writer.Write(reservation.OfferId);
                writer.Write(reservation.Tile.Value);
                WriteNullableString(writer, reservation.WindowId);
                writer.Write(reservation.CreatedAtMillis);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                WriteNullableString(writer, state.WindowIdFor(seat));
            }
        }

        private static SleeveBloomState ReadSleeveBloom(BinaryReader reader)
        {
            long version = reader.ReadInt64();
            int windowSeq = reader.ReadInt32();
            int usedOnTurn = reader.ReadInt32();
            SleeveBloomOffer? immediate = ReadOffer(reader);
            int offerCount = ReadCount(reader, 4, "off-turn offer");
            var offers = new SleeveBloomOffer[offerCount];
            for (int i = 0; i < offerCount; i++)
            {
                offers[i] = ReadOffer(reader) ??
                    throw new InvalidDataException("An off-turn offer slot cannot be empty.");
            }

            int reservationCount = ReadCount(reader, 4, "reservation");
            var reservations = new SleeveBloomReservation[reservationCount];
            for (int i = 0; i < reservationCount; i++)
            {
                reservations[i] = new SleeveBloomReservation(
                    reader.ReadInt32(),
                    reader.ReadString(),
                    new TileId(reader.ReadInt32()),
                    ReadNullableString(reader),
                    reader.ReadInt64());
            }

            var windows = new string?[4];
            for (int seat = 0; seat < 4; seat++)
            {
                windows[seat] = ReadNullableString(reader);
            }

            return SleeveBloomState.Restore(immediate, offers, reservations, windows, windowSeq, usedOnTurn, version);
        }

        private static void WriteOffer(BinaryWriter writer, SleeveBloomOffer? offer)
        {
            writer.Write(offer != null);
            if (offer == null)
            {
                return;
            }

            writer.Write(offer.OfferId);
            writer.Write(offer.Seat);
            writer.Write((byte)offer.DrawKind);
            writer.Write(offer.FirstTile.Value);
            writer.Write(offer.SecondTile.Value);
            writer.Write(offer.CreatedTurnNumber);
            writer.Write((byte)offer.Mode);
            WriteNullableString(writer, offer.WindowId);
            writer.Write(offer.DeadlineMillis);
            writer.Write(offer.CreatedAtMillis);
            writer.Write(offer.BootId);
        }

        private static SleeveBloomOffer? ReadOffer(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            string offerId = reader.ReadString();
            int seat = reader.ReadInt32();
            byte drawKind = reader.ReadByte();
            if (!Enum.IsDefined(typeof(SleeveBloomDrawKind), drawKind))
            {
                throw new InvalidDataException("Sleeve bloom draw kind is invalid.");
            }

            TileId first = new TileId(reader.ReadInt32());
            TileId second = new TileId(reader.ReadInt32());
            int createdTurn = reader.ReadInt32();
            byte mode = reader.ReadByte();
            if (!Enum.IsDefined(typeof(SleeveBloomOfferMode), mode))
            {
                throw new InvalidDataException("Sleeve bloom offer mode is invalid.");
            }

            string? windowId = ReadNullableString(reader);
            long deadline = reader.ReadInt64();
            long createdAt = reader.ReadInt64();
            string bootId = reader.ReadString();
            return new SleeveBloomOffer(
                offerId,
                seat,
                (SleeveBloomDrawKind)drawKind,
                first,
                second,
                createdTurn,
                (SleeveBloomOfferMode)mode,
                windowId,
                deadline,
                createdAt,
                bootId);
        }

        private static void WriteBeamSwap(BinaryWriter writer, BeamSwapState state)
        {
            writer.Write(state.Version);
            writer.Write(state.UsedOnTurn);
            BeamSwapOffer? offer = state.Offer;
            writer.Write(offer != null);
            if (offer == null)
            {
                return;
            }

            writer.Write(offer.OfferId);
            writer.Write(offer.Seat);
            writer.Write((byte)offer.Window);
            writer.Write(offer.DrawnTileId.Value);
            writer.Write(offer.CreatedTurnNumber);
            writer.Write(offer.Candidates.Count);
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                BeamSwapCandidate candidate = offer.Candidates[i];
                writer.Write(candidate.Tile.Value);
                writer.Write((byte)candidate.Slot.Kind);
                writer.Write(candidate.Slot.Index);
            }

            writer.Write(offer.EligibleHandTileIds.Count);
            for (int i = 0; i < offer.EligibleHandTileIds.Count; i++)
            {
                writer.Write(offer.EligibleHandTileIds[i].Value);
            }
        }

        private static BeamSwapState ReadBeamSwap(BinaryReader reader)
        {
            long version = reader.ReadInt64();
            int usedOnTurn = reader.ReadInt32();
            BeamSwapOffer? offer = null;
            if (reader.ReadBoolean())
            {
                string offerId = reader.ReadString();
                int seat = reader.ReadInt32();
                byte window = reader.ReadByte();
                if (!Enum.IsDefined(typeof(BeamSwapWindow), window))
                {
                    throw new InvalidDataException("Beam swap window is invalid.");
                }

                TileId drawn = new TileId(reader.ReadInt32());
                int createdTurn = reader.ReadInt32();
                int candidateCount = ReadCount(reader, 256, "beam candidate");
                var candidates = new BeamSwapCandidate[candidateCount];
                for (int i = 0; i < candidateCount; i++)
                {
                    TileId tile = new TileId(reader.ReadInt32());
                    byte kind = reader.ReadByte();
                    int index = reader.ReadInt32();
                    candidates[i] = new BeamSwapCandidate(tile, SlotOf(kind, index));
                }

                int handCount = ReadCount(reader, 14, "beam eligible hand tile");
                var hand = new TileId[handCount];
                for (int i = 0; i < handCount; i++)
                {
                    hand[i] = new TileId(reader.ReadInt32());
                }

                offer = new BeamSwapOffer(
                    offerId,
                    seat,
                    (BeamSwapWindow)window,
                    candidates,
                    hand,
                    drawn,
                    createdTurn);
            }

            return BeamSwapState.Restore(offer, usedOnTurn, version);
        }

        private static BeamSwapSlot SlotOf(byte kind, int index)
        {
            if (!Enum.IsDefined(typeof(BeamSwapSlotKind), kind))
            {
                throw new InvalidDataException("Beam swap slot kind is invalid.");
            }

            switch ((BeamSwapSlotKind)kind)
            {
                case BeamSwapSlotKind.WallSlot:
                    return BeamSwapSlot.Wall(index);
                case BeamSwapSlotKind.DeadWallSlot:
                    return BeamSwapSlot.DeadWall(index);
                case BeamSwapSlotKind.ReplacementSlot:
                    return BeamSwapSlot.Replacement(index);
                default:
                    throw new InvalidDataException("Beam swap slot kind is invalid.");
            }
        }

        private static int ReadCount(BinaryReader reader, int max, string name)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > max)
            {
                throw new InvalidDataException("Cheat checkpoint " + name + " count is invalid.");
            }

            return count;
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

    /// <summary>
    /// Durable cheat command journal. It stores the exact typed request plus the authoritative
    /// context values (turn, injected monotonic milliseconds, round ordinal, draw kind) that
    /// the actor used. Cold recovery re-executes this through the same pure Domain bridge and
    /// never re-reads the wall clock or allocates a new request id.
    /// </summary>
    public static class TableCheatCommandCodec
    {
        public const int FormatVersion = 1;
        private const int Magic = 0x4D4A4348; // MJCH

        public static byte[] Encode(RoundCheatJournal journal)
        {
            if (journal == null)
            {
                throw new ArgumentNullException(nameof(journal));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                RoundCheatCommand command = journal.Command;
                writer.Write(command.RoundId);
                writer.Write(command.Seat);
                writer.Write((byte)command.Action);
                writer.Write(command.RequestId);
                WritePayload(writer, command.Payload);
                writer.Write(journal.TurnNumber);
                writer.Write(journal.NowMillis);
                writer.Write(journal.RoundOrdinal);
                writer.Write((byte)journal.LastDrawKind);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static RoundCheatJournal Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Cheat command journal payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Cheat command journal magic is invalid.");
                }

                int schema = reader.ReadInt32();
                if (schema != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported cheat command journal schema " + schema + ".");
                }

                string roundId = reader.ReadString();
                int seat = reader.ReadInt32();
                byte action = reader.ReadByte();
                if (!Enum.IsDefined(typeof(RoundCheatAction), action))
                {
                    throw new InvalidDataException("Cheat command action is invalid.");
                }

                string requestId = reader.ReadString();
                RoundCheatPayload request = ReadPayload(reader, (RoundCheatAction)action);
                int turn = reader.ReadInt32();
                long now = reader.ReadInt64();
                int ordinal = reader.ReadInt32();
                byte drawKind = reader.ReadByte();
                if (!Enum.IsDefined(typeof(SleeveBloomDrawKind), drawKind))
                {
                    throw new InvalidDataException("Cheat command draw kind is invalid.");
                }

                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Cheat command journal payload has trailing bytes.");
                }

                return new RoundCheatJournal(
                    new RoundCheatCommand(roundId, seat, (RoundCheatAction)action, requestId, request),
                    turn,
                    now,
                    ordinal,
                    (SleeveBloomDrawKind)drawKind);
            }
        }

        private static void WritePayload(BinaryWriter writer, RoundCheatPayload payload)
        {
            WriteNullableString(writer, payload.GroupId);
            writer.Write(payload.Tiles.Count);
            for (int i = 0; i < payload.Tiles.Count; i++)
            {
                writer.Write(payload.Tiles[i].Value);
            }

            WriteNullableString(writer, payload.OfferId);
            writer.Write(payload.Keep.HasValue);
            if (payload.Keep.HasValue)
            {
                writer.Write((byte)payload.Keep.Value);
            }

            WriteNullableString(writer, payload.WindowId);
            writer.Write(payload.Action.HasValue);
            if (payload.Action.HasValue)
            {
                writer.Write((byte)payload.Action.Value);
            }

            writer.Write(payload.HandTile.HasValue);
            if (payload.HandTile.HasValue)
            {
                writer.Write(payload.HandTile.Value.Value);
            }

            writer.Write(payload.TargetSlot.HasValue);
            if (payload.TargetSlot.HasValue)
            {
                writer.Write((byte)payload.TargetSlot.Value.Kind);
                writer.Write(payload.TargetSlot.Value.Index);
            }
        }

        private static RoundCheatPayload ReadPayload(BinaryReader reader, RoundCheatAction action)
        {
            string? groupId = ReadNullableString(reader);
            int tileCount = reader.ReadInt32();
            if (tileCount < 0 || tileCount > 4)
            {
                throw new InvalidDataException("Cheat command tile count is invalid.");
            }

            var tiles = new TileId[tileCount];
            for (int i = 0; i < tileCount; i++)
            {
                tiles[i] = new TileId(reader.ReadInt32());
            }

            string? offerId = ReadNullableString(reader);
            SleeveBloomKeep? keep = reader.ReadBoolean()
                ? ReadKeep(reader)
                : (SleeveBloomKeep?)null;
            string? windowId = ReadNullableString(reader);
            BeamSwapAction? swapAction = reader.ReadBoolean()
                ? ReadBeamAction(reader)
                : (BeamSwapAction?)null;
            TileId? handTile = reader.ReadBoolean() ? new TileId(reader.ReadInt32()) : (TileId?)null;
            BeamSwapSlot? targetSlot = null;
            if (reader.ReadBoolean())
            {
                byte kind = reader.ReadByte();
                int index = reader.ReadInt32();
                if (!Enum.IsDefined(typeof(BeamSwapSlotKind), kind))
                {
                    throw new InvalidDataException("Cheat command target slot kind is invalid.");
                }

                targetSlot = SlotOf((BeamSwapSlotKind)kind, index);
            }

            switch (action)
            {
                case RoundCheatAction.SubmitTileMark:
                    if (groupId == null || tileCount != 4)
                    {
                        throw new InvalidDataException("Tile mark journal payload is incomplete.");
                    }

                    return RoundCheatPayload.TileMark(groupId, tiles);
                case RoundCheatAction.SubmitSleeveBloom:
                    return RoundCheatPayload.SleeveBloom();
                case RoundCheatAction.SubmitSleeveBloomChoice:
                    if (offerId == null || !keep.HasValue)
                    {
                        throw new InvalidDataException("Sleeve bloom choice journal payload is incomplete.");
                    }

                    return RoundCheatPayload.SleeveBloomChoice(offerId, keep.Value);
                case RoundCheatAction.SubmitSleeveBloomOffTurn:
                    if (windowId == null)
                    {
                        throw new InvalidDataException("Off-turn sleeve bloom journal payload is incomplete.");
                    }

                    return RoundCheatPayload.OffTurn(windowId);
                case RoundCheatAction.SubmitSleeveBloomOffTurnChoice:
                    if (offerId == null || !keep.HasValue)
                    {
                        throw new InvalidDataException("Off-turn sleeve bloom choice journal payload is incomplete.");
                    }

                    return RoundCheatPayload.OffTurnChoice(offerId, keep.Value);
                case RoundCheatAction.SubmitBeamSwap:
                    return RoundCheatPayload.BeamSwap();
                case RoundCheatAction.SubmitBeamSwapChoice:
                    if (offerId == null || !swapAction.HasValue)
                    {
                        throw new InvalidDataException("Beam swap choice journal payload is incomplete.");
                    }

                    return RoundCheatPayload.BeamSwapChoice(offerId, swapAction.Value, handTile, targetSlot);
                default:
                    throw new InvalidDataException("Cheat command action is invalid.");
            }
        }

        private static SleeveBloomKeep ReadKeep(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (!Enum.IsDefined(typeof(SleeveBloomKeep), value))
            {
                throw new InvalidDataException("Cheat command keep value is invalid.");
            }

            return (SleeveBloomKeep)value;
        }

        private static BeamSwapAction ReadBeamAction(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (!Enum.IsDefined(typeof(BeamSwapAction), value))
            {
                throw new InvalidDataException("Cheat command beam action is invalid.");
            }

            return (BeamSwapAction)value;
        }

        private static BeamSwapSlot SlotOf(BeamSwapSlotKind kind, int index)
        {
            switch (kind)
            {
                case BeamSwapSlotKind.WallSlot:
                    return BeamSwapSlot.Wall(index);
                case BeamSwapSlotKind.DeadWallSlot:
                    return BeamSwapSlot.DeadWall(index);
                case BeamSwapSlotKind.ReplacementSlot:
                    return BeamSwapSlot.Replacement(index);
                default:
                    throw new InvalidDataException("Cheat command target slot kind is invalid.");
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

    /// <summary>Durable journal for an authority cheat sweep/timeout (no client request id).</summary>
    public static class TableCheatSystemCodec
    {
        public const int FormatVersion = 1;
        private const int Magic = 0x4D4A4353; // MJCS

        public static byte[] Encode(TableCheatSystemJournal journal)
        {
            if (journal == null)
            {
                throw new ArgumentNullException(nameof(journal));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write((byte)journal.Action);
                writer.Write(journal.Seat);
                writer.Write(journal.TurnNumber);
                writer.Write(journal.NowMillis);
                writer.Write(journal.RoundOrdinal);
                writer.Write((byte)journal.LastDrawKind);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static TableCheatSystemJournal Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Cheat system journal payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Cheat system journal magic is invalid.");
                }

                int schema = reader.ReadInt32();
                if (schema != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported cheat system journal schema " + schema + ".");
                }

                byte action = reader.ReadByte();
                if (!Enum.IsDefined(typeof(RoundCheatSystemAction), action))
                {
                    throw new InvalidDataException("Cheat system action is invalid.");
                }

                int seat = reader.ReadInt32();
                int turn = reader.ReadInt32();
                long now = reader.ReadInt64();
                int ordinal = reader.ReadInt32();
                byte drawKind = reader.ReadByte();
                if (!Enum.IsDefined(typeof(SleeveBloomDrawKind), drawKind))
                {
                    throw new InvalidDataException("Cheat system draw kind is invalid.");
                }

                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Cheat system journal payload has trailing bytes.");
                }

                return new TableCheatSystemJournal(
                    (RoundCheatSystemAction)action,
                    seat,
                    turn,
                    now,
                    ordinal,
                    (SleeveBloomDrawKind)drawKind);
            }
        }
    }
}
