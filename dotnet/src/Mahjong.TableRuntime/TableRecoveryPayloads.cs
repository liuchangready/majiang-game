using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.TableRuntime
{
    /// <summary>Builds and validates self-contained checkpoints without sharing live objects.</summary>
    public static class TableCheckpointFactory
    {
        /// <summary>
        /// Schema 2 carries N-1a Domain decision state (current scores, riichi flags/sticks,
        /// meld records, pending kan dora) inside the recovery payload. Schema 1 checkpoints are
        /// rejected rather than silently accepted with the new fields defaulted.
        /// </summary>
        public const int SchemaVersion = 2;

        public static TableCheckpoint Create(
            string roomId,
            RoundStateMachine round,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            TableControlCheckpoint control,
            TableTimingCheckpoint timing,
            int rulesetVersion,
            string bootId,
            ulong utcNowMs,
            TableCheatLedgerCheckpoint? cheat = null,
            byte[]? committedFactsPayload = null,
            long timingVersion = 1L,
            long capturedMonotonicMillis = 0L,
            IReadOnlyList<TableEventRecord>? committedHistory = null,
            IReadOnlyList<TableCommandBinding>? committedCommandBindings = null)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            if (control == null)
            {
                throw new ArgumentNullException(nameof(control));
            }

            if (timing == null)
            {
                throw new ArgumentNullException(nameof(timing));
            }

            byte[] state = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round));
            TablePayloadSha256 stateHash = TablePayloadSha256.Compute(state);
            byte[] controlBytes = TableControlCheckpointCodec.Encode(control);
            TablePayloadSha256 controlHash = TablePayloadSha256.Compute(controlBytes);
            byte[] timingBytes = TableTimingCheckpointCodec.Encode(timing);
            TablePayloadSha256 timingHash = TablePayloadSha256.Compute(timingBytes);
            byte[]? cheatBytes = cheat == null ? null : TableCheatLedgerCheckpointCodec.Encode(cheat);
            TablePayloadSha256? cheatHash = cheatBytes == null ? (TablePayloadSha256?)null : TablePayloadSha256.Compute(cheatBytes);
            TablePayloadSha256? factsHash = committedFactsPayload == null
                ? (TablePayloadSha256?)null
                : TablePayloadSha256.Compute(committedFactsPayload);
            byte[]? historyBytes = committedHistory == null
                ? null
                : TableCommittedHistoryCodec.Encode(committedHistory);
            TablePayloadSha256? historyHash = historyBytes == null
                ? (TablePayloadSha256?)null
                : TablePayloadSha256.Compute(historyBytes);
            byte[]? bindingBytes = committedHistory == null || committedHistory.Count == 0
                ? null
                : TableCommandBindingsCodec.Encode(
                    committedCommandBindings ??
                    throw new ArgumentException(
                        "Committed history requires independent command binding evidence.",
                        nameof(committedCommandBindings)));
            TablePayloadSha256? bindingHash = bindingBytes == null
                ? (TablePayloadSha256?)null
                : TablePayloadSha256.Compute(bindingBytes);

            return new TableCheckpoint(
                roomId,
                stateVersion,
                lastEventSeq,
                round.SourceFingerprint,
                rulesetVersion,
                state,
                stateHash,
                control.ControlVersion,
                controlBytes,
                controlHash,
                timingBytes,
                timingHash,
                cheatBytes,
                cheatHash,
                committedFactsPayload,
                factsHash,
                utcNowMs,
                bootId,
                SchemaVersion,
                timingVersion,
                capturedMonotonicMillis,
                historyBytes,
                historyHash,
                bindingBytes,
                bindingHash);
        }

        /// <summary>
        /// Captures the decision clock at a checkpoint without persisting any absolute local
        /// monotonic origin. Remaining base/reserve/overdue are relative and are the only values
        /// recovery consumes.
        /// </summary>
        public static TableTimingCheckpoint CaptureTiming(
            DecisionClock clock,
            IEnumerable<PendingDecision> pending,
            long nowMillis,
            ulong utcNowMs = 0UL,
            string bootId = "")
        {
            if (clock == null)
            {
                throw new ArgumentNullException(nameof(clock));
            }

            if (pending == null)
            {
                throw new ArgumentNullException(nameof(pending));
            }

            if (nowMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMillis), nowMillis, "Monotonic time must be non-negative.");
            }

            IReadOnlyList<long> reserves = clock.RemainingReserves();
            var decisions = new List<TableRecoveredDecision>();
            foreach (PendingDecision decision in pending)
            {
                DecisionTiming? timing = clock.Timing(decision.Key.Seat);
                if (timing == null || timing.Key != decision.Key)
                {
                    throw new InvalidOperationException(
                        "Pending decision " + decision.Key + " has no matching open timing.");
                }

                long baseEnd = timing.OpensAtMillis + timing.BaseMillis;
                long graceEnd = baseEnd + timing.RecoveryGraceMillis;
                long remainingBase = Math.Max(0L, baseEnd - nowMillis);
                long remainingGrace = Math.Max(0L, graceEnd - Math.Max(nowMillis, baseEnd));
                long remainingReserve = Math.Max(0L, timing.DeadlineMillis - Math.Max(nowMillis, graceEnd));
                long overdue = Math.Max(0L, nowMillis - timing.DeadlineMillis);
                decisions.Add(new TableRecoveredDecision(
                    decision.Key,
                    decision.ControlEpoch,
                    decision.Candidates,
                    decision.DrawnTile,
                    checked(remainingBase + remainingGrace),
                    remainingReserve,
                    overdue,
                    remainingGrace,
                    timing.RecoveryGraceApplied));
            }

            return new TableTimingCheckpoint(
                clock.ActiveRoundId ?? string.Empty,
                reserves,
                decisions,
                utcNowMs,
                nowMillis,
                bootId);
        }
    }

    /// <summary>
    /// Strict event binding payload. It is not a whole candidate state; it binds one committed
    /// event to its command version, ordinal and source-round fingerprint so replay can reject
    /// a forged or reordered fact even if the compact fields happen to match.
    /// </summary>
    public static class TableEventFactCodec
    {
        public const int FormatVersion = 1;
        public const int PayloadBytes = 4 + 4 + 8 + 8;

        public static byte[] Encode(StateVersion commandVersion, int ordinal, ulong sourceFingerprint)
        {
            if (ordinal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Ordinal must be non-negative.");
            }

            byte[] payload = new byte[PayloadBytes];
            WriteInt32(payload, 0, 0x4D4A4546); // MJEF
            WriteInt32(payload, 4, ordinal);
            WriteUInt64(payload, 8, commandVersion.Value);
            WriteUInt64(payload, 16, sourceFingerprint);
            return payload;
        }

        public static TableEventFact Decode(byte[] payload)
        {
            if (payload == null || payload.Length != PayloadBytes)
            {
                throw new InvalidDataException("Event fact payload length is invalid.");
            }

            if (ReadInt32(payload, 0) != 0x4D4A4546)
            {
                throw new InvalidDataException("Event fact magic is invalid.");
            }

            int ordinal = ReadInt32(payload, 4);
            ulong version = ReadUInt64(payload, 8);
            ulong fingerprint = ReadUInt64(payload, 16);
            if (ordinal < 0 || version == 0 || fingerprint == 0)
            {
                throw new InvalidDataException("Event fact fields are invalid.");
            }

            return new TableEventFact(new StateVersion(version), ordinal, fingerprint);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static int ReadInt32(byte[] buffer, int offset) =>
            buffer[offset] |
            (buffer[offset + 1] << 8) |
            (buffer[offset + 2] << 16) |
            (buffer[offset + 3] << 24);

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)(value >> (8 * i));
            }
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value |= (ulong)buffer[offset + i] << (8 * i);
            }

            return value;
        }
    }

    public readonly struct TableEventFact
    {
        public TableEventFact(StateVersion commandVersion, int ordinal, ulong sourceFingerprint)
        {
            CommandVersion = commandVersion;
            Ordinal = ordinal;
            SourceFingerprint = sourceFingerprint;
        }

        public StateVersion CommandVersion { get; }

        public int Ordinal { get; }

        public ulong SourceFingerprint { get; }
    }

    /// <summary>
    /// Full committed history kept by a checkpoint. It is deliberately not the diagnostic
    /// SnapshotEventWindow: recovery installs this list into the actor's committed history so
    /// P3.2 settlement/event facts do not depend on a truncated window.
    /// </summary>
    public static class TableCommittedHistoryCodec
    {
        public const int FormatVersion = 2;
        private const int Magic = 0x4D4A4843; // MJHC
        private const int MaxEvents = 1_000_000;

        public static byte[] Encode(IReadOnlyList<TableEventRecord> events)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (events.Count > MaxEvents)
            {
                throw new ArgumentException("Committed history exceeds the event cap.", nameof(events));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(events.Count);
                for (int i = 0; i < events.Count; i++)
                {
                    TableEventRecord value = events[i] ??
                        throw new ArgumentException("Committed history contains null.", nameof(events));
                    writer.Write(value.Seq.Value);
                    writer.Write(value.CommandVersion.Value);
                    writer.Write(value.Ordinal);
                    writer.Write((byte)value.Type);
                    writer.Write(value.Seat);
                    writer.Write(value.Tile.HasValue);
                    if (value.Tile.HasValue)
                    {
                        writer.Write(value.Tile.Value.Value);
                    }

                    writer.Write(value.Detail != null);
                    if (value.Detail != null)
                    {
                        writer.Write(value.Detail);
                    }

                    writer.Write(value.EventSchema);
                    byte[]? payload = value.Payload;
                    writer.Write(payload != null);
                    if (payload != null)
                    {
                        writer.Write(payload.Length);
                        writer.Write(payload);
                    }

                    WriteCheat(writer, value.Cheat);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static IReadOnlyList<TableEventRecord> Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Committed history payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Committed history magic is invalid.");
                }

                int schema = reader.ReadInt32();
                if (schema != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported committed history schema " + schema + ".");
                }

                int count = reader.ReadInt32();
                if (count < 0 || count > MaxEvents)
                {
                    throw new InvalidDataException("Committed history count is invalid.");
                }

                var events = new List<TableEventRecord>(count);
                for (int i = 0; i < count; i++)
                {
                    ulong seq = reader.ReadUInt64();
                    ulong version = reader.ReadUInt64();
                    int ordinal = reader.ReadInt32();
                    byte type = reader.ReadByte();
                    if (!Enum.IsDefined(typeof(RoundEventType), type))
                    {
                        throw new InvalidDataException("Committed history event type is invalid.");
                    }

                    int seat = reader.ReadInt32();
                    TileId? tile = reader.ReadBoolean() ? new TileId(reader.ReadInt32()) : (TileId?)null;
                    string? detail = reader.ReadBoolean() ? reader.ReadString() : null;
                    int eventSchema = reader.ReadInt32();
                    byte[]? eventPayload = null;
                    if (reader.ReadBoolean())
                    {
                        int length = reader.ReadInt32();
                        if (length <= 0 || length > 1024 * 1024)
                        {
                            throw new InvalidDataException("Committed history event payload length is invalid.");
                        }

                        eventPayload = reader.ReadBytes(length);
                        if (eventPayload.Length != length)
                        {
                            throw new InvalidDataException("Committed history event payload is truncated.");
                        }
                    }

                    TableCheatEventRecord? cheat = ReadCheat(reader);
                    events.Add(new TableEventRecord(
                        new EventSeq(seq),
                        new StateVersion(version),
                        ordinal,
                        (RoundEventType)type,
                        seat,
                        tile,
                        detail,
                        eventSchema,
                        eventPayload,
                        cheat));
                }

                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Committed history payload has trailing bytes.");
                }

                return events;
            }
        }

        private static void WriteCheat(BinaryWriter writer, TableCheatEventRecord? cheat)
        {
            writer.Write(cheat != null);
            if (cheat == null)
            {
                return;
            }

            CheatEvent value = cheat.Event;
            writer.Write((byte)value.Kind);
            writer.Write(value.Seat);
            WriteNullableString(writer, value.EntryId);
            writer.Write(value.Amount);
            writer.Write(value.NewBalance);
            WriteNullableString(writer, value.OfferId);
            WriteNullableString(writer, value.GroupId);
            writer.Write(value.Keep.HasValue);
            if (value.Keep.HasValue)
            {
                writer.Write((byte)value.Keep.Value);
            }

            writer.Write(value.Action.HasValue);
            if (value.Action.HasValue)
            {
                writer.Write((byte)value.Action.Value);
            }

            WriteNullableString(writer, value.Reason);
            writer.Write(cheat.TurnNumber);
            writer.Write(cheat.ResultTile.HasValue);
            if (cheat.ResultTile.HasValue)
            {
                writer.Write(cheat.ResultTile.Value.Value);
            }

            writer.Write(cheat.HandTile.HasValue);
            if (cheat.HandTile.HasValue)
            {
                writer.Write(cheat.HandTile.Value.Value);
            }

            writer.Write(cheat.TargetSlot.HasValue);
            if (cheat.TargetSlot.HasValue)
            {
                writer.Write((byte)cheat.TargetSlot.Value.Kind);
                writer.Write(cheat.TargetSlot.Value.Index);
            }
        }

        private static TableCheatEventRecord? ReadCheat(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            byte kind = reader.ReadByte();
            if (!Enum.IsDefined(typeof(CheatEventKind), kind))
            {
                throw new InvalidDataException("Committed cheat event kind is invalid.");
            }

            int seat = reader.ReadInt32();
            string? entryId = ReadNullableString(reader);
            int amount = reader.ReadInt32();
            int balance = reader.ReadInt32();
            string? offerId = ReadNullableString(reader);
            string? groupId = ReadNullableString(reader);
            SleeveBloomKeep? keep = reader.ReadBoolean()
                ? ReadKeep(reader)
                : (SleeveBloomKeep?)null;
            BeamSwapAction? action = reader.ReadBoolean()
                ? ReadBeamAction(reader)
                : (BeamSwapAction?)null;
            string? reason = ReadNullableString(reader);
            int turn = reader.ReadInt32();
            TileId? resultTile = reader.ReadBoolean() ? new TileId(reader.ReadInt32()) : (TileId?)null;
            TileId? handTile = reader.ReadBoolean() ? new TileId(reader.ReadInt32()) : (TileId?)null;
            BeamSwapSlot? targetSlot = null;
            if (reader.ReadBoolean())
            {
                byte slotKind = reader.ReadByte();
                int index = reader.ReadInt32();
                if (!Enum.IsDefined(typeof(BeamSwapSlotKind), slotKind))
                {
                    throw new InvalidDataException("Committed cheat target slot kind is invalid.");
                }

                targetSlot = SlotOf((BeamSwapSlotKind)slotKind, index);
            }

            CheatEvent value = CheatEvent.Restore(
                (CheatEventKind)kind,
                seat,
                entryId,
                amount,
                balance,
                offerId,
                groupId,
                keep,
                action,
                reason);
            return new TableCheatEventRecord(
                value,
                offerId,
                keep,
                action,
                resultTile,
                handTile,
                targetSlot,
                turn);
        }

        private static SleeveBloomKeep ReadKeep(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (!Enum.IsDefined(typeof(SleeveBloomKeep), value))
            {
                throw new InvalidDataException("Committed cheat keep value is invalid.");
            }

            return (SleeveBloomKeep)value;
        }

        private static BeamSwapAction ReadBeamAction(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (!Enum.IsDefined(typeof(BeamSwapAction), value))
            {
                throw new InvalidDataException("Committed cheat beam action is invalid.");
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
                    throw new InvalidDataException("Committed cheat target slot kind is invalid.");
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

    /// <summary>Deterministic terminal result payload for the unique settlement outbox.</summary>
    public static class TableSettlementPayloadCodec
    {
        public const int FormatVersion = 1;

        public static byte[] Encode(string roomId, RoundResult result)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(FormatVersion);
                writer.Write(roomId);
                writer.Write(result.ResultId);
                writer.Write(result.RoundId);
                writer.Write((byte)result.Outcome.Kind);
                writer.Write(result.Outcome.WinnerSeat);
                writer.Write(result.Outcome.LoserSeat);
                writer.Write(result.Outcome.TenpaiSeats.Count);
                for (int i = 0; i < result.Outcome.TenpaiSeats.Count; i++)
                {
                    writer.Write(result.Outcome.TenpaiSeats[i]);
                }

                writer.Write(result.Outcome.Reason);
                for (int i = 0; i < 4; i++)
                {
                    writer.Write(result.FinalScores[i]);
                    writer.Write(result.TotalDeltas[i]);
                }

                writer.Write(result.RemainingRiichiSticks);
                writer.Write((byte)result.EndReason);
                writer.Write(result.CompletedAtUtc.Value.UtcDateTime.Ticks);
                writer.Write(result.CompletedAtMonotonicMillis);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
