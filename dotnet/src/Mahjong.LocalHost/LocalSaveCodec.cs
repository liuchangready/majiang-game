using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Explicit binary codec for the local envelope, checkpoint and replay operations. There is
    /// no reflection and no policy-free serializer: every field is written and validated in a
    /// fixed order, and the file carries a whole-payload SHA-256 to reject truncation/tamper.
    /// </summary>
    internal static class LocalSaveCodec
    {
        private const int SaveMagic = 0x4C483450; // "LH4P"
        private const int SaveEnvelopeVersion = 1;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] EncodeEnvelope(LocalSave save)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }

            byte[] payload = EncodeSave(save);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                writer.Write(SaveMagic);
                writer.Write(SaveEnvelopeVersion);
                writer.Write(payload.Length);
                writer.Write(TablePayloadSha256.Compute(payload).ToArray());
                writer.Write(payload);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static LocalSave DecodeEnvelope(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 48)
            {
                throw new InvalidDataException("Local save is truncated.");
            }

            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, Utf8, true))
                {
                    if (reader.ReadInt32() != SaveMagic)
                    {
                        throw new InvalidDataException("Local save magic is not recognized.");
                    }

                    int envelopeVersion = reader.ReadInt32();
                    if (envelopeVersion != SaveEnvelopeVersion)
                    {
                        throw new InvalidDataException(
                            "Local save envelope version " + envelopeVersion.ToString(CultureInfo.InvariantCulture) + " is not supported.");
                    }

                    int length = reader.ReadInt32();
                    if (length <= 0 || length > bytes.Length - 44)
                    {
                        throw new InvalidDataException("Local save payload length is invalid.");
                    }

                    byte[] expected = reader.ReadBytes(TablePayloadSha256.SizeBytes);
                    byte[] payload = reader.ReadBytes(length);
                    if (payload.Length != length)
                    {
                        throw new InvalidDataException("Local save payload is truncated.");
                    }

                    if (TablePayloadSha256.Compute(payload).ToArray().AsSpan().SequenceEqual(expected) == false)
                    {
                        throw new InvalidDataException("Local save payload SHA-256 does not match.");
                    }

                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("Local save has trailing bytes.");
                    }

                    return DecodeSave(payload);
                }
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("Local save is truncated.", exception);
            }
        }

        private static byte[] EncodeSave(LocalSave save)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                writer.Write(save.FileFormatVersion);
                WriteString(writer, save.RoomId);
                writer.Write((byte)save.Origin);
                writer.Write(save.RulesetVersion);

                writer.Write(save.CurrentState.StateVersion.Value);
                writer.Write(save.CurrentState.LastEventSeq.Value);
                writer.Write(save.CurrentState.StateFingerprint);
                WriteHash(writer, save.CurrentState.StateHash);
                writer.Write(save.CurrentState.ControlVersion);
                writer.Write(save.CurrentState.ControlEpoch);
                writer.Write(save.CurrentState.TimingVersion);
                writer.Write(save.CurrentState.CheatVersion);
                writer.Write(save.CurrentState.CheatFingerprint);
                writer.Write(save.CurrentState.CheatBound);

                WriteBytes(writer, save.BaseCheckpointBlob);
                WriteHash(writer, save.CheckpointSha256);
                WriteBytes(writer, save.LatestTimingPayload);
                WriteHash(writer, save.LatestTimingPayloadSha256);
                WriteOptionalBytes(writer, save.LatestCommittedFactsPayload);
                WriteOptionalHash(writer, save.LatestCommittedFactsPayloadSha256);
                WriteOptionalBytes(writer, save.CurrentControlSnapshotPayload);
                WriteOptionalHash(writer, save.CurrentControlSnapshotSha256);

                writer.Write(save.CommandJournals.Count);
                for (int i = 0; i < save.CommandJournals.Count; i++)
                {
                    WriteJournal(writer, save.CommandJournals[i]);
                }

                writer.Write(save.EventJournals.Count);
                for (int i = 0; i < save.EventJournals.Count; i++)
                {
                    WriteEvent(writer, save.EventJournals[i]);
                }

                writer.Write(save.Receipts.Count);
                foreach (KeyValuePair<string, TableDurableReceipt> pair in save.Receipts)
                {
                    WriteString(writer, pair.Key);
                    WriteReceipt(writer, pair.Value);
                }

                writer.Write(save.Outbox.Count);
                foreach (KeyValuePair<string, TableSettlementOutboxRecord> pair in save.Outbox)
                {
                    WriteString(writer, pair.Key);
                    WriteOutbox(writer, pair.Value);
                }

                writer.Write((byte)save.TerminalState);
                WriteNullableString(writer, save.TerminalResultId);
                writer.Write(save.TerminalResultVersion.HasValue);
                if (save.TerminalResultVersion.HasValue)
                {
                    writer.Write(save.TerminalResultVersion.Value);
                }

                writer.Write(save.Operations.Count);
                for (int i = 0; i < save.Operations.Count; i++)
                {
                    WriteOperation(writer, save.Operations[i]);
                }

                writer.Write(save.RecoveryAudits.Count);
                for (int i = 0; i < save.RecoveryAudits.Count; i++)
                {
                    WriteAudit(writer, save.RecoveryAudits[i]);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static LocalSave DecodeSave(byte[] payload)
        {
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream, Utf8, true))
            {
                var save = new LocalSave();
                save.FileFormatVersion = reader.ReadInt32();
                save.RoomId = ReadString(reader);
                save.Origin = (LocalSaveOrigin)reader.ReadByte();
                save.RulesetVersion = reader.ReadInt32();

                var state = new LocalSaveState
                {
                    StateVersion = new StateVersion(reader.ReadUInt64()),
                    LastEventSeq = new EventSeq(reader.ReadUInt64()),
                    StateFingerprint = reader.ReadUInt64(),
                    StateHash = ReadHash(reader),
                    ControlVersion = reader.ReadInt64(),
                    ControlEpoch = reader.ReadUInt64(),
                    TimingVersion = reader.ReadInt64(),
                    CheatVersion = reader.ReadInt64(),
                    CheatFingerprint = reader.ReadUInt64(),
                    CheatBound = reader.ReadBoolean()
                };
                save.CurrentState = state;

                save.BaseCheckpointBlob = ReadBytes(reader);
                save.CheckpointSha256 = ReadHash(reader);
                save.LatestTimingPayload = ReadBytes(reader);
                save.LatestTimingPayloadSha256 = ReadHash(reader);
                save.LatestCommittedFactsPayload = ReadOptionalBytes(reader);
                save.LatestCommittedFactsPayloadSha256 = ReadOptionalHash(reader);
                save.CurrentControlSnapshotPayload = ReadOptionalBytes(reader);
                save.CurrentControlSnapshotSha256 = ReadOptionalHash(reader);

                int journalCount = ReadCount(reader, "command journals");
                for (int i = 0; i < journalCount; i++)
                {
                    save.CommandJournals.Add(ReadJournal(reader));
                }

                int eventCount = ReadCount(reader, "event journals");
                for (int i = 0; i < eventCount; i++)
                {
                    save.EventJournals.Add(ReadEvent(reader));
                }

                int receiptCount = ReadCount(reader, "receipts");
                for (int i = 0; i < receiptCount; i++)
                {
                    string key = ReadString(reader);
                    save.Receipts.Add(key, ReadReceipt(reader));
                }

                int outboxCount = ReadCount(reader, "outbox");
                for (int i = 0; i < outboxCount; i++)
                {
                    string key = ReadString(reader);
                    save.Outbox.Add(key, ReadOutbox(reader));
                }

                save.TerminalState = (LocalSaveTerminalState)reader.ReadByte();
                save.TerminalResultId = ReadNullableString(reader);
                save.TerminalResultVersion = reader.ReadBoolean() ? reader.ReadInt32() : (int?)null;

                int operationCount = ReadCount(reader, "operations");
                for (int i = 0; i < operationCount; i++)
                {
                    save.Operations.Add(ReadOperation(reader));
                }

                int auditCount = ReadCount(reader, "recovery audits");
                for (int i = 0; i < auditCount; i++)
                {
                    save.RecoveryAudits.Add(ReadAudit(reader));
                }

                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException("Local save payload has trailing bytes.");
                }

                ValidateLogicalShape(save);
                return save;
            }
        }

        private static void ValidateLogicalShape(LocalSave save)
        {
            if (save.FileFormatVersion != 1)
            {
                throw new InvalidDataException(
                    "Local save file format version " + save.FileFormatVersion.ToString(CultureInfo.InvariantCulture) + " is not supported.");
            }

            if (string.IsNullOrWhiteSpace(save.RoomId))
            {
                throw new InvalidDataException("Local save room id is blank.");
            }

            if (save.Origin != LocalSaveOrigin.Local)
            {
                throw new InvalidDataException("A local save must have origin LOCAL.");
            }

            if (save.RulesetVersion < 1)
            {
                throw new InvalidDataException("Local save ruleset version is invalid.");
            }

            if (save.BaseCheckpointBlob.Length == 0 || save.CheckpointSha256.IsEmpty ||
                TablePayloadSha256.Compute(save.BaseCheckpointBlob) != save.CheckpointSha256)
            {
                throw new InvalidDataException("Local save base checkpoint digest does not match.");
            }

            if (save.LatestTimingPayload.Length == 0 || save.LatestTimingPayloadSha256.IsEmpty ||
                TablePayloadSha256.Compute(save.LatestTimingPayload) != save.LatestTimingPayloadSha256)
            {
                throw new InvalidDataException("Local save timing digest does not match.");
            }

            if (save.LatestCommittedFactsPayload != null &&
                (!save.LatestCommittedFactsPayloadSha256.HasValue ||
                 TablePayloadSha256.Compute(save.LatestCommittedFactsPayload) != save.LatestCommittedFactsPayloadSha256.Value))
            {
                throw new InvalidDataException("Local save committed facts digest does not match.");
            }

            if (save.CurrentControlSnapshotPayload != null &&
                (!save.CurrentControlSnapshotSha256.HasValue ||
                 TablePayloadSha256.Compute(save.CurrentControlSnapshotPayload) != save.CurrentControlSnapshotSha256.Value))
            {
                throw new InvalidDataException("Local save control snapshot digest does not match.");
            }

            if (save.CommandJournals.Count > save.Operations.Count)
            {
                // Every command journal is also a replayable operation. Timing/control/audit
                // operations have no Domain command journal, so equality is not required.
                throw new InvalidDataException("Local save has more journals than replay operations.");
            }
        }

        public static byte[] EncodeCheckpoint(TableCheckpoint checkpoint)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                WriteString(writer, checkpoint.RoomId);
                writer.Write(checkpoint.StateVersion.Value);
                writer.Write(checkpoint.LastEventSeq.Value);
                writer.Write(checkpoint.StateFingerprint);
                writer.Write(checkpoint.RulesetVersion);
                writer.Write(checkpoint.ControlVersion);
                writer.Write(checkpoint.SchemaVersion);
                writer.Write(checkpoint.TimingVersion);
                writer.Write(checkpoint.CapturedMonotonicMillis);
                writer.Write(checkpoint.CreatedAtUtcMs);
                WriteString(writer, checkpoint.BootId);

                WriteHashPayload(writer, checkpoint.StatePayload, checkpoint.StatePayloadSha256);
                WriteHashPayload(writer, checkpoint.ControlPayload, checkpoint.ControlPayloadSha256);
                WriteHashPayload(writer, checkpoint.TimingPayload, checkpoint.TimingPayloadSha256);
                WriteOptionalHashPayload(writer, checkpoint.CheatPayload, checkpoint.CheatPayloadSha256);
                WriteOptionalHashPayload(writer, checkpoint.CommittedFactsPayload, checkpoint.CommittedFactsPayloadSha256);
                WriteOptionalHashPayload(writer, checkpoint.CommittedHistoryPayload, checkpoint.CommittedHistoryPayloadSha256);
                WriteOptionalHashPayload(
                    writer,
                    checkpoint.CommittedCommandBindingsPayload,
                    checkpoint.CommittedCommandBindingsPayloadSha256);

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static TableCheckpoint DecodeCheckpoint(byte[] blob)
        {
            if (blob == null || blob.Length == 0)
            {
                throw new InvalidDataException("Checkpoint blob is empty.");
            }

            try
            {
                using (var stream = new MemoryStream(blob, false))
                using (var reader = new BinaryReader(stream, Utf8, true))
                {
                    string roomId = ReadString(reader);
                    ulong stateVersion = reader.ReadUInt64();
                    ulong lastEventSeq = reader.ReadUInt64();
                    ulong fingerprint = reader.ReadUInt64();
                    int ruleset = reader.ReadInt32();
                    long controlVersion = reader.ReadInt64();
                    int schema = reader.ReadInt32();
                    long timingVersion = reader.ReadInt64();
                    long capturedMonotonic = reader.ReadInt64();
                    ulong createdAtUtc = reader.ReadUInt64();
                    string bootId = ReadString(reader);

                    byte[] statePayload = ReadHashPayload(reader, out TablePayloadSha256 stateHash);
                    byte[] controlPayload = ReadHashPayload(reader, out TablePayloadSha256 controlHash);
                    byte[] timingPayload = ReadHashPayload(reader, out TablePayloadSha256 timingHash);
                    byte[]? cheatPayload = ReadOptionalHashPayload(reader, out TablePayloadSha256? cheatHash);
                    byte[]? factsPayload = ReadOptionalHashPayload(reader, out TablePayloadSha256? factsHash);
                    byte[]? historyPayload = ReadOptionalHashPayload(reader, out TablePayloadSha256? historyHash);
                    byte[]? bindingsPayload = ReadOptionalHashPayload(reader, out TablePayloadSha256? bindingsHash);

                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("Checkpoint blob has trailing bytes.");
                    }

                    return new TableCheckpoint(
                        roomId,
                        new StateVersion(stateVersion),
                        new EventSeq(lastEventSeq),
                        fingerprint,
                        ruleset,
                        statePayload,
                        stateHash,
                        controlVersion,
                        controlPayload,
                        controlHash,
                        timingPayload,
                        timingHash,
                        cheatPayload,
                        cheatHash,
                        factsPayload,
                        factsHash,
                        createdAtUtc,
                        bootId,
                        schema,
                        timingVersion,
                        capturedMonotonic,
                        historyPayload,
                        historyHash,
                        bindingsPayload,
                        bindingsHash);
                }
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("Checkpoint blob is truncated.", exception);
            }
        }

        private static void WriteOperation(BinaryWriter writer, LocalDurableOperation operation)
        {
            writer.Write((byte)operation.Kind);
            WriteString(writer, operation.OwnerInstance);
            writer.Write(operation.ExpectedOwnerEpoch);
            writer.Write(operation.ExpectedStateVersion);
            writer.Write(operation.ExpectedControlVersion);
            writer.Write(operation.ExpectedTimingVersion);

            writer.Write(operation.Receipt != null);
            if (operation.Receipt != null)
            {
                WriteReceipt(writer, operation.Receipt);
            }

            writer.Write(operation.InternalCommand);
            writer.Write(operation.Events.Count);
            for (int i = 0; i < operation.Events.Count; i++)
            {
                WriteEvent(writer, operation.Events[i]);
            }

            writer.Write(operation.Journal != null);
            if (operation.Journal != null)
            {
                WriteJournal(writer, operation.Journal);
            }

            writer.Write(operation.Outbox.Count);
            for (int i = 0; i < operation.Outbox.Count; i++)
            {
                WriteOutbox(writer, operation.Outbox[i]);
            }

            WriteOptionalBytes(writer, operation.CheckpointBlob);
            WriteOptionalBytes(writer, operation.NextStatePayload);
            writer.Write(operation.NextStateFingerprint);
            writer.Write(operation.Terminal);
            WriteOptionalBytes(writer, operation.TimingPayload);
            writer.Write(operation.TimingVersion);
            WriteOptionalBytes(writer, operation.CommittedFactsPayload);
            WriteOptionalHash(writer, operation.CommittedFactsPayloadSha256);
            writer.Write(operation.NextCheatVersion);
            writer.Write(operation.NextCheatFingerprint);

            WriteNullableString(writer, operation.RoundId);
            writer.Write(operation.ControlChange != null);
            if (operation.ControlChange != null)
            {
                WriteControlEvent(writer, operation.ControlChange);
            }

            writer.Write(operation.Fence != null);
            if (operation.Fence != null)
            {
                WriteString(writer, operation.Fence.RoomId);
                WriteString(writer, operation.Fence.OwnerInstance);
                writer.Write(operation.Fence.ExpectedOwnerEpoch.Value);
                writer.Write(operation.Fence.ExpectedStateVersion.Value);
                writer.Write(operation.Fence.ExpectedControlVersion);
                writer.Write(operation.Fence.ExpectedTimingVersion);
            }

            writer.Write(operation.TimingRecord != null);
            if (operation.TimingRecord != null)
            {
                WriteTimingRecord(writer, operation.TimingRecord);
            }

            writer.Write(operation.Audit != null);
            if (operation.Audit != null)
            {
                WriteAudit(writer, operation.Audit);
            }
        }

        private static LocalDurableOperation ReadOperation(BinaryReader reader)
        {
            var operation = new LocalDurableOperation
            {
                Kind = (LocalOperationKind)reader.ReadByte(),
                OwnerInstance = ReadString(reader),
                ExpectedOwnerEpoch = reader.ReadUInt64(),
                ExpectedStateVersion = reader.ReadUInt64(),
                ExpectedControlVersion = reader.ReadInt64(),
                ExpectedTimingVersion = reader.ReadInt64()
            };

            if (reader.ReadBoolean())
            {
                operation.Receipt = ReadReceipt(reader);
            }

            operation.InternalCommand = reader.ReadBoolean();
            int eventCount = ReadCount(reader, "operation events");
            for (int i = 0; i < eventCount; i++)
            {
                operation.Events.Add(ReadEvent(reader));
            }

            if (reader.ReadBoolean())
            {
                operation.Journal = ReadJournal(reader);
            }

            int outboxCount = ReadCount(reader, "operation outbox");
            for (int i = 0; i < outboxCount; i++)
            {
                operation.Outbox.Add(ReadOutbox(reader));
            }

            operation.CheckpointBlob = ReadOptionalBytes(reader);
            operation.NextStatePayload = ReadOptionalBytes(reader);
            operation.NextStateFingerprint = reader.ReadUInt64();
            operation.Terminal = reader.ReadBoolean();
            operation.TimingPayload = ReadOptionalBytes(reader);
            operation.TimingVersion = reader.ReadInt64();
            operation.CommittedFactsPayload = ReadOptionalBytes(reader);
            operation.CommittedFactsPayloadSha256 = ReadOptionalHash(reader);
            operation.NextCheatVersion = reader.ReadInt64();
            operation.NextCheatFingerprint = reader.ReadUInt64();

            operation.RoundId = ReadNullableString(reader);
            if (reader.ReadBoolean())
            {
                operation.ControlChange = ReadControlEvent(reader);
            }

            if (reader.ReadBoolean())
            {
                operation.Fence = new TableCheckpointFence(
                    ReadString(reader),
                    ReadString(reader),
                    new OwnerEpoch(reader.ReadUInt64()),
                    new StateVersion(reader.ReadUInt64()),
                    reader.ReadInt64(),
                    reader.ReadInt64());
            }

            if (reader.ReadBoolean())
            {
                operation.TimingRecord = ReadTimingRecord(reader);
            }

            if (reader.ReadBoolean())
            {
                operation.Audit = ReadAudit(reader);
            }

            return operation;
        }

        private static void WriteJournal(BinaryWriter writer, TableCommandJournalRecord journal)
        {
            writer.Write(journal.CommandVersion.Value);
            writer.Write(journal.EvaluatedStateVersion.Value);
            WriteString(writer, journal.RequestId.Value);
            WriteString(writer, journal.AccountId);
            WriteString(writer, journal.ScopeId.Value);
            WriteString(writer, journal.Api);
            WriteHash(writer, journal.PayloadSha256);
            WriteBytes(writer, journal.CommandPayload);
            writer.Write(journal.SchemaVersion);
        }

        private static TableCommandJournalRecord ReadJournal(BinaryReader reader)
        {
            ulong commandVersion = reader.ReadUInt64();
            ulong evaluated = reader.ReadUInt64();
            string requestId = ReadString(reader);
            string account = ReadString(reader);
            string scopeId = ReadString(reader);
            string api = ReadString(reader);
            TablePayloadSha256 digest = ReadHash(reader);
            byte[] payload = ReadBytes(reader);
            int schema = reader.ReadInt32();
            return new TableCommandJournalRecord(
                new StateVersion(commandVersion),
                new StateVersion(evaluated),
                new RequestId(requestId),
                account,
                new RequestScopeId(scopeId),
                api,
                digest,
                payload,
                schema);
        }

        private static void WriteEvent(BinaryWriter writer, TableEventRecord value)
        {
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

            WriteNullableString(writer, value.Detail);
            writer.Write(value.EventSchema);
            WriteOptionalBytes(writer, value.Payload);
            writer.Write(value.Cheat != null);
            if (value.Cheat != null)
            {
                TableCheatEventRecord cheat = value.Cheat;
                writer.Write((byte)cheat.Event.Kind);
                writer.Write(cheat.Event.Seat);
                WriteNullableString(writer, cheat.Event.EntryId);
                writer.Write(cheat.Event.Amount);
                writer.Write(cheat.Event.NewBalance);
                WriteNullableString(writer, cheat.Event.OfferId);
                WriteNullableString(writer, cheat.Event.GroupId);
                writer.Write(cheat.Event.Keep.HasValue);
                if (cheat.Event.Keep.HasValue)
                {
                    writer.Write((byte)cheat.Event.Keep.Value);
                }

                writer.Write(cheat.Event.Action.HasValue);
                if (cheat.Event.Action.HasValue)
                {
                    writer.Write((byte)cheat.Event.Action.Value);
                }

                WriteNullableString(writer, cheat.Event.Reason);
                WriteNullableString(writer, cheat.OfferId);
                writer.Write(cheat.Keep.HasValue);
                if (cheat.Keep.HasValue)
                {
                    writer.Write((byte)cheat.Keep.Value);
                }

                writer.Write(cheat.Action.HasValue);
                if (cheat.Action.HasValue)
                {
                    writer.Write((byte)cheat.Action.Value);
                }

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

                writer.Write(cheat.TurnNumber);
            }
        }

        private static TableEventRecord ReadEvent(BinaryReader reader)
        {
            ulong seq = reader.ReadUInt64();
            ulong commandVersion = reader.ReadUInt64();
            int ordinal = reader.ReadInt32();
            var type = (RoundEventType)reader.ReadByte();
            int seat = reader.ReadInt32();
            TileId? tile = reader.ReadBoolean() ? new TileId(unchecked((int)reader.ReadUInt32())) : (TileId?)null;
            string? detail = ReadNullableString(reader);
            int schema = reader.ReadInt32();
            byte[]? payload = ReadOptionalBytes(reader);

            TableCheatEventRecord? cheat = null;
            if (reader.ReadBoolean())
            {
                var kind = (CheatEventKind)reader.ReadByte();
                int cheatSeat = reader.ReadInt32();
                string? entryId = ReadNullableString(reader);
                int amount = reader.ReadInt32();
                int newBalance = reader.ReadInt32();
                string? offerId = ReadNullableString(reader);
                string? groupId = ReadNullableString(reader);
                SleeveBloomKeep? keep = reader.ReadBoolean() ? (SleeveBloomKeep)reader.ReadByte() : (SleeveBloomKeep?)null;
                BeamSwapAction? action = reader.ReadBoolean() ? (BeamSwapAction)reader.ReadByte() : (BeamSwapAction?)null;
                string? reason = ReadNullableString(reader);
                CheatEvent cheatEvent = CheatEvent.Restore(
                    kind,
                    cheatSeat,
                    entryId,
                    amount,
                    newBalance,
                    offerId,
                    groupId,
                    keep,
                    action,
                    reason);
                string? boundOfferId = ReadNullableString(reader);
                SleeveBloomKeep? boundKeep = reader.ReadBoolean() ? (SleeveBloomKeep)reader.ReadByte() : (SleeveBloomKeep?)null;
                BeamSwapAction? boundAction = reader.ReadBoolean() ? (BeamSwapAction)reader.ReadByte() : (BeamSwapAction?)null;
                TileId? resultTile = reader.ReadBoolean() ? new TileId(unchecked((int)reader.ReadUInt32())) : (TileId?)null;
                TileId? handTile = reader.ReadBoolean() ? new TileId(unchecked((int)reader.ReadUInt32())) : (TileId?)null;
                BeamSwapSlot? slot = null;
                if (reader.ReadBoolean())
                {
                    var slotKind = (BeamSwapSlotKind)reader.ReadByte();
                    int index = reader.ReadInt32();
                    slot = slotKind == BeamSwapSlotKind.WallSlot
                        ? BeamSwapSlot.Wall(index)
                        : slotKind == BeamSwapSlotKind.DeadWallSlot
                            ? BeamSwapSlot.DeadWall(index)
                            : BeamSwapSlot.Replacement(index);
                }

                int turn = reader.ReadInt32();
                cheat = new TableCheatEventRecord(
                    cheatEvent,
                    boundOfferId,
                    boundKeep,
                    boundAction,
                    resultTile,
                    handTile,
                    slot,
                    turn);
            }

            return new TableEventRecord(
                new EventSeq(seq),
                new StateVersion(commandVersion),
                ordinal,
                type,
                seat,
                tile,
                detail,
                schema,
                payload,
                cheat);
        }

        private static void WriteReceipt(BinaryWriter writer, TableDurableReceipt value)
        {
            WriteString(writer, value.AccountId);
            WriteString(writer, value.ScopeId.Value);
            WriteString(writer, value.RequestId.Value);
            WriteString(writer, value.Api);
            WriteHash(writer, value.PayloadSha256);

            Receipt receipt = value.Receipt;
            writer.Write((byte)receipt.Status);
            WriteString(writer, receipt.ReasonCode);
            writer.Write(receipt.EvaluatedStateVersion.Value);
            writer.Write(receipt.CommandVersion.HasValue);
            if (receipt.CommandVersion.HasValue)
            {
                writer.Write(receipt.CommandVersion.Value.Value);
            }

            writer.Write(receipt.EventSeq.HasValue);
            if (receipt.EventSeq.HasValue)
            {
                writer.Write(receipt.EventSeq.Value.Value);
            }

            writer.Write(receipt.CommittedAtMs.HasValue);
            if (receipt.CommittedAtMs.HasValue)
            {
                writer.Write(receipt.CommittedAtMs.Value);
            }

            writer.Write(receipt.Replayed);
            WriteString(writer, value.Detail);
            writer.Write(value.CreatedAtUtcMs);
            writer.Write(value.ExpiresAtUtcMs);
        }

        private static TableDurableReceipt ReadReceipt(BinaryReader reader)
        {
            string account = ReadString(reader);
            string scope = ReadString(reader);
            string request = ReadString(reader);
            string api = ReadString(reader);
            TablePayloadSha256 digest = ReadHash(reader);
            var status = (ReceiptStatus)reader.ReadByte();
            string reason = ReadString(reader);
            ulong evaluated = reader.ReadUInt64();
            StateVersion? commandVersion = reader.ReadBoolean() ? new StateVersion(reader.ReadUInt64()) : (StateVersion?)null;
            EventSeq? eventSeq = reader.ReadBoolean() ? new EventSeq(reader.ReadUInt64()) : (EventSeq?)null;
            ulong? committedAt = reader.ReadBoolean() ? reader.ReadUInt64() : (ulong?)null;
            bool replayed = reader.ReadBoolean();
            string detail = ReadString(reader);
            ulong createdAt = reader.ReadUInt64();
            ulong expiresAt = reader.ReadUInt64();

            var receipt = new Receipt(
                status,
                reason,
                new StateVersion(evaluated),
                commandVersion,
                eventSeq,
                committedAt,
                null,
                replayed);
            return new TableDurableReceipt(
                account,
                new RequestScopeId(scope),
                new RequestId(request),
                api,
                digest,
                receipt,
                detail,
                createdAt,
                expiresAt);
        }

        private static void WriteOutbox(BinaryWriter writer, TableSettlementOutboxRecord value)
        {
            WriteString(writer, value.MatchId);
            writer.Write(value.ResultVersion);
            WriteBytes(writer, value.Payload);
            WriteHash(writer, value.PayloadSha256);
            writer.Write(value.CreatedAtUtcMs);
            writer.Write(value.PublishedAtUtcMs.HasValue);
            if (value.PublishedAtUtcMs.HasValue)
            {
                writer.Write(value.PublishedAtUtcMs.Value);
            }
        }

        private static TableSettlementOutboxRecord ReadOutbox(BinaryReader reader)
        {
            string matchId = ReadString(reader);
            int resultVersion = reader.ReadInt32();
            byte[] payload = ReadBytes(reader);
            TablePayloadSha256 hash = ReadHash(reader);
            ulong created = reader.ReadUInt64();
            ulong? published = reader.ReadBoolean() ? reader.ReadUInt64() : (ulong?)null;
            return new TableSettlementOutboxRecord(matchId, resultVersion, payload, hash, created, published);
        }

        private static void WriteControlEvent(BinaryWriter writer, TableControlEvent value)
        {
            writer.Write(value.ControlVersion);
            WriteString(writer, value.RoundId);
            writer.Write(value.Seat);
            writer.Write((byte)value.Kind);
            WriteOptionalInt64(writer, value.PreviousEpoch);
            WriteOptionalInt64(writer, value.NewEpoch);
            WriteOptionalByte(writer, value.PreviousKind.HasValue, value.PreviousKind.HasValue ? (byte)value.PreviousKind.Value : (byte)0);
            WriteOptionalByte(writer, value.NewKind.HasValue, value.NewKind.HasValue ? (byte)value.NewKind.Value : (byte)0);
            WriteOptionalByte(writer, value.PreviousStatus.HasValue, value.PreviousStatus.HasValue ? (byte)value.PreviousStatus.Value : (byte)0);
            WriteOptionalByte(writer, value.NewStatus.HasValue, value.NewStatus.HasValue ? (byte)value.NewStatus.Value : (byte)0);
            WriteOptionalInt64(writer, value.GraceDeadlineMillis);
            writer.Write(value.AtMonotonicMillis);
            writer.Write(value.AtUtcMillis);
            WriteString(writer, value.Reason);
            WriteOptionalInt64(writer, value.DisconnectedAtMillis);
            WriteNullableString(writer, value.AiProfileId);
            WriteOptionalByte(writer, value.PolicyMode.HasValue, value.PolicyMode.HasValue ? (byte)value.PolicyMode.Value : (byte)0);
            WriteOptionalInt64(writer, value.PolicyGraceMillis);
            WriteNullableString(writer, value.PolicyAiProfileId);
        }

        private static TableControlEvent ReadControlEvent(BinaryReader reader)
        {
            long controlVersion = reader.ReadInt64();
            string roundId = ReadString(reader);
            int seat = reader.ReadInt32();
            var kind = (TableControlChangeKind)reader.ReadByte();
            long? previousEpoch = ReadOptionalInt64(reader);
            long? newEpoch = ReadOptionalInt64(reader);
            SeatControllerKind? previousKind = ReadOptionalByte(reader) is byte pk ? (SeatControllerKind)pk : (SeatControllerKind?)null;
            SeatControllerKind? newKind = ReadOptionalByte(reader) is byte nk ? (SeatControllerKind)nk : (SeatControllerKind?)null;
            ConnectionStatus? previousStatus = ReadOptionalByte(reader) is byte ps ? (ConnectionStatus)ps : (ConnectionStatus?)null;
            ConnectionStatus? newStatus = ReadOptionalByte(reader) is byte ns ? (ConnectionStatus)ns : (ConnectionStatus?)null;
            long? grace = ReadOptionalInt64(reader);
            long atMonotonic = reader.ReadInt64();
            long atUtc = reader.ReadInt64();
            string reason = ReadString(reader);
            long? disconnected = ReadOptionalInt64(reader);
            string? aiProfile = ReadNullableString(reader);
            byte? policyMode = ReadOptionalByte(reader);
            long? policyGrace = ReadOptionalInt64(reader);
            string? policyAiProfile = ReadNullableString(reader);
            return new TableControlEvent(
                controlVersion,
                roundId,
                seat,
                kind,
                previousEpoch,
                newEpoch,
                previousKind,
                newKind,
                previousStatus,
                newStatus,
                grace,
                atMonotonic,
                atUtc,
                reason,
                disconnected,
                aiProfile,
                policyMode.HasValue ? (DisconnectMode)policyMode.Value : (DisconnectMode?)null,
                policyGrace,
                policyAiProfile);
        }

        private static void WriteTimingRecord(BinaryWriter writer, TableTimingJournalRecord value)
        {
            WriteString(writer, value.RoomId);
            writer.Write(value.TimingVersion);
            writer.Write(value.StateVersion.Value);
            writer.Write(value.ControlVersion);
            WriteString(writer, value.BootId);
            writer.Write(value.CapturedAtUtcMs);
            writer.Write(value.CapturedMonotonicMillis);
            WriteBytes(writer, TableTimingCheckpointCodec.Encode(value.Timing));
        }

        private static TableTimingJournalRecord ReadTimingRecord(BinaryReader reader)
        {
            string roomId = ReadString(reader);
            long timingVersion = reader.ReadInt64();
            ulong stateVersion = reader.ReadUInt64();
            long controlVersion = reader.ReadInt64();
            string bootId = ReadString(reader);
            ulong capturedAt = reader.ReadUInt64();
            long capturedMonotonic = reader.ReadInt64();
            TableTimingCheckpoint timing = TableTimingCheckpointCodec.Decode(ReadBytes(reader));
            return new TableTimingJournalRecord(
                roomId,
                timingVersion,
                new StateVersion(stateVersion),
                controlVersion,
                bootId,
                capturedAt,
                capturedMonotonic,
                timing);
        }

        private static void WriteAudit(BinaryWriter writer, TableRecoveryAuditRecord value)
        {
            WriteString(writer, value.RoomId);
            writer.Write((byte)value.Kind);
            writer.Write(value.RecordedAtUtcMs);
            WriteString(writer, value.PreviousBootId);
            WriteString(writer, value.CurrentBootId);
            writer.Write(value.DowntimeMillis);
            writer.Write(value.GraceMillis);
            WriteString(writer, value.PreviousOwnerInstance);
            writer.Write(value.PreviousOwnerEpoch.Value);
            writer.Write(value.CurrentOwnerEpoch.Value);
            WriteString(writer, value.Detail);
        }

        private static TableRecoveryAuditRecord ReadAudit(BinaryReader reader)
        {
            return new TableRecoveryAuditRecord(
                ReadString(reader),
                (TableRecoveryAuditKind)reader.ReadByte(),
                reader.ReadUInt64(),
                ReadString(reader),
                ReadString(reader),
                reader.ReadInt64(),
                reader.ReadInt64(),
                ReadString(reader),
                new OwnerEpoch(reader.ReadUInt64()),
                new OwnerEpoch(reader.ReadUInt64()),
                ReadString(reader));
        }

        private static void WriteHash(BinaryWriter writer, TablePayloadSha256 value)
        {
            byte[] bytes = value.ToArray();
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static TablePayloadSha256 ReadHash(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length != TablePayloadSha256.SizeBytes)
            {
                throw new InvalidDataException("Local payload hash length is invalid.");
            }

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new InvalidDataException("Local payload hash is truncated.");
            }

            return new TablePayloadSha256(bytes);
        }

        private static void WriteOptionalHash(BinaryWriter writer, TablePayloadSha256? value)
        {
            writer.Write(value.HasValue && !value.Value.IsEmpty);
            if (value.HasValue && !value.Value.IsEmpty)
            {
                WriteHash(writer, value.Value);
            }
        }

        private static TablePayloadSha256? ReadOptionalHash(BinaryReader reader)
        {
            return reader.ReadBoolean() ? ReadHash(reader) : (TablePayloadSha256?)null;
        }

        private static void WriteHashPayload(BinaryWriter writer, byte[] payload, TablePayloadSha256 hash)
        {
            WriteBytes(writer, payload);
            WriteHash(writer, hash);
        }

        private static void WriteOptionalHashPayload(BinaryWriter writer, byte[]? payload, TablePayloadSha256? hash)
        {
            writer.Write(payload != null);
            if (payload != null)
            {
                WriteBytes(writer, payload);
                WriteHash(writer, hash ?? throw new InvalidDataException("Optional checkpoint payload has no hash."));
            }
        }

        private static byte[] ReadHashPayload(BinaryReader reader, out TablePayloadSha256 hash)
        {
            byte[] payload = ReadBytes(reader);
            hash = ReadHash(reader);
            if (TablePayloadSha256.Compute(payload) != hash)
            {
                throw new InvalidDataException("Checkpoint payload hash does not match.");
            }

            return payload;
        }

        private static byte[]? ReadOptionalHashPayload(BinaryReader reader, out TablePayloadSha256? hash)
        {
            if (!reader.ReadBoolean())
            {
                hash = null;
                return null;
            }

            byte[] payload = ReadBytes(reader);
            hash = ReadHash(reader);
            if (TablePayloadSha256.Compute(payload) != hash.Value)
            {
                throw new InvalidDataException("Optional checkpoint payload hash does not match.");
            }

            return payload;
        }

        private static void WriteBytes(BinaryWriter writer, byte[] value)
        {
            writer.Write(value.Length);
            writer.Write(value);
        }

        private static byte[] ReadBytes(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 16 * 1024 * 1024)
            {
                throw new InvalidDataException("Local byte payload length is invalid.");
            }

            byte[] value = reader.ReadBytes(length);
            if (value.Length != length)
            {
                throw new InvalidDataException("Local byte payload is truncated.");
            }

            return value;
        }

        private static void WriteOptionalBytes(BinaryWriter writer, byte[]? value)
        {
            writer.Write(value != null);
            if (value != null)
            {
                WriteBytes(writer, value);
            }
        }

        private static byte[]? ReadOptionalBytes(BinaryReader reader)
        {
            return reader.ReadBoolean() ? ReadBytes(reader) : null;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 1024 * 1024)
            {
                throw new InvalidDataException("Local string length is invalid.");
            }

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new InvalidDataException("Local string is truncated.");
            }

            return Utf8.GetString(bytes);
        }

        private static void WriteNullableString(BinaryWriter writer, string? value)
        {
            writer.Write(value != null);
            if (value != null)
            {
                WriteString(writer, value);
            }
        }

        private static string? ReadNullableString(BinaryReader reader)
        {
            return reader.ReadBoolean() ? ReadString(reader) : null;
        }

        private static int ReadCount(BinaryReader reader, string name)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 1_000_000)
            {
                throw new InvalidDataException("Local " + name + " count is invalid.");
            }

            return count;
        }

        private static void WriteOptionalInt64(BinaryWriter writer, long? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue)
            {
                writer.Write(value.Value);
            }
        }

        private static long? ReadOptionalInt64(BinaryReader reader)
        {
            return reader.ReadBoolean() ? reader.ReadInt64() : (long?)null;
        }

        private static void WriteOptionalByte(BinaryWriter writer, bool present, byte value)
        {
            writer.Write(present);
            if (present)
            {
                writer.Write(value);
            }
        }

        private static byte? ReadOptionalByte(BinaryReader reader)
        {
            return reader.ReadBoolean() ? reader.ReadByte() : (byte?)null;
        }
    }
}
