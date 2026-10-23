using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Contracts;

namespace Mahjong.Client
{
    /// <summary>Decoded on-disk snapshot. Entries and resolved-key tombstones are disjoint.</summary>
    internal sealed class FileOutboxSnapshot
    {
        public List<OutboxEntry> Entries { get; } = new List<OutboxEntry>();

        public Dictionary<IdempotencyKey, OutboxResolution> Resolutions { get; } =
            new Dictionary<IdempotencyKey, OutboxResolution>();

        public OutboxEntry? FindEntry(IdempotencyKey key)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Key.Equals(key))
                {
                    return Entries[i];
                }
            }

            return null;
        }

        public OutboxResolution? FindResolution(IdempotencyKey key)
        {
            return Resolutions.TryGetValue(key, out OutboxResolution? resolution) ? resolution : null;
        }
    }

    /// <summary>
    /// Versioned binary format and the single source of truth for field bounds
    /// and semantic invariants. Encoding and decoding deliberately share the
    /// same validators and the same per-field byte limits; a successful write
    /// must therefore be readable by a fresh store instance.
    /// </summary>
    internal static class FileOutboxFormat
    {
        internal const int HeaderBytes = 56;
        internal const uint FormatVersion = 1;

        // 128 UTF-16 chars can occupy at most 512 UTF-8 bytes.
        internal const int MaxIdBytes = 512;

        // Deliberately equal on write and read. 512 ASCII bytes are accepted;
        // 513 bytes, or a multibyte string above the byte bound, are rejected
        // before the atomic commit and leave the previous snapshot untouched.
        internal const int MaxReasonBytes = 512;

        internal const int MaxDigestBytes = 64;

        private const byte EntryRoundFlag = 1;
        private const byte EntryDecisionFlag = 2;
        private const byte EntryControlFlag = 4;
        private const byte EntryOwnerFlag = 8;
        private const byte ResolutionCommandVersionFlag = 1;
        private const byte ResolutionEventSeqFlag = 2;
        private const byte ResolutionCommittedAtFlag = 4;

        private static readonly byte[] MagicBytes = { (byte)'M', (byte)'J', (byte)'O', (byte)'U', (byte)'T', (byte)'B', (byte)'X', (byte)'1' };
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static byte[] Encode(FileOutboxSnapshot snapshot, FileOutboxOptions options, string storePath)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (snapshot.Entries.Count > options.MaxEntries)
            {
                throw new OutboxStoreFullException(storePath, "the unresolved-entry bound was reached.");
            }

            if (snapshot.Resolutions.Count > options.MaxResolutions)
            {
                throw new OutboxStoreFullException(storePath, "the resolved-key bound was reached.");
            }

            var entries = new List<OutboxEntry>(snapshot.Entries);
            entries.Sort((left, right) => CompareKeys(left.Key, right.Key));
            var resolutionKeys = new List<IdempotencyKey>(snapshot.Resolutions.Keys);
            resolutionKeys.Sort(CompareKeys);

            byte[] body;
            using (var bodyStream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bodyStream, StrictUtf8, leaveOpen: true))
                {
                    writer.Write((uint)entries.Count);
                    writer.Write((uint)resolutionKeys.Count);
                    for (int i = 0; i < entries.Count; i++)
                    {
                        ValidateEntryForWrite(entries[i], options);
                        WriteEntry(writer, entries[i]);
                    }

                    for (int i = 0; i < resolutionKeys.Count; i++)
                    {
                        IdempotencyKey key = resolutionKeys[i];
                        OutboxResolution resolution = snapshot.Resolutions[key];
                        ValidateResolutionForWrite(resolution);
                        WriteKey(writer, key);
                        WriteResolution(writer, resolution);
                    }
                }

                body = bodyStream.ToArray();
            }

            if (body.Length > options.MaxStoreBytes - HeaderBytes)
            {
                throw new OutboxStoreFullException(
                    storePath,
                    "the encoded body is " + body.Length + " bytes, above the configured snapshot bound.");
            }

            byte[] checksum = ComputeSha256(body);
            using (var output = new MemoryStream(HeaderBytes + body.Length))
            {
                using (var writer = new BinaryWriter(output, StrictUtf8, leaveOpen: true))
                {
                    writer.Write(MagicBytes);
                    writer.Write(FormatVersion);
                    writer.Write(0u);
                    writer.Write((uint)body.Length);
                    writer.Write(0u);
                    writer.Write(checksum);
                    writer.Write(body);
                }

                return output.ToArray();
            }
        }

        internal static FileOutboxSnapshot Decode(byte[] raw, FileOutboxOptions options, string storePath)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }

            if (raw.Length > options.MaxStoreBytes)
            {
                throw new OutboxStoreCorruptException(
                    storePath,
                    "the committed file is larger than the configured " + options.MaxStoreBytes + "-byte bound.");
            }

            try
            {
                return DecodeCore(raw, options, storePath);
            }
            catch (OutboxStoreCorruptException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is EndOfStreamException ||
                ex is ArgumentException ||
                ex is FormatException ||
                ex is OverflowException ||
                ex is InvalidOperationException ||
                ex is NotSupportedException ||
                ex is CommandValidationException ||
                ex is CanonicalEncodingException)
            {
                throw new OutboxStoreCorruptException(storePath, "the snapshot could not be decoded.", ex);
            }
        }

        internal static void ValidateEntryForWrite(OutboxEntry entry, FileOutboxOptions options)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (entry.PayloadLength <= 0 || entry.PayloadLength > options.MaxPayloadBytes)
            {
                throw new ArgumentException("Outbox payload length is outside the configured bound.", nameof(entry));
            }

            if (entry.PayloadSchemaVersion != CanonicalPayloadDigest.FormatVersion)
            {
                throw new ArgumentException("Only canonical payload schema 1 can be persisted.", nameof(entry));
            }

            if (entry.Digest.IsEmpty || entry.Digest.AsSpan().Length != PayloadDigest.SizeBytes)
            {
                throw new ArgumentException("Outbox entry is missing its canonical digest.", nameof(entry));
            }

            if (entry.OwnerScope == null)
            {
                throw new ArgumentException("Outbox entry is missing its owner scope.", nameof(entry));
            }

            if (!string.Equals(entry.Key.AccountId, entry.OwnerScope.AccountId, StringComparison.Ordinal) ||
                !string.Equals(entry.Key.ScopeId.Value, entry.OwnerScope.ScopeId.Value, StringComparison.Ordinal) ||
                !string.Equals(entry.RoomId.Value, entry.OwnerScope.RoomId.Value, StringComparison.Ordinal) ||
                !string.Equals(entry.ScopeId.Value, entry.OwnerScope.ScopeId.Value, StringComparison.Ordinal))
            {
                throw new ArgumentException("Outbox key, room and owner scope disagree.", nameof(entry));
            }

            ValidateUtf8(entry.Key.AccountId, MaxIdBytes, "entry account");
            ValidateUtf8(entry.Key.ScopeId.Value, MaxIdBytes, "entry scope");
            ValidateUtf8(entry.Key.RequestId.Value, MaxIdBytes, "entry request");
            ValidateUtf8(entry.SessionId.Value, MaxIdBytes, "session id");
            ValidateUtf8(entry.RoomId.Value, MaxIdBytes, "room id");
            ValidateUtf8(entry.OwnerScope.ScopeId.Value, MaxIdBytes, "scope id");
            ValidateUtf8(entry.OwnerScope.AccountId, MaxIdBytes, "scope account");
            ValidateUtf8(entry.OwnerScope.RoomId.Value, MaxIdBytes, "scope room");
            if (entry.RoundId.HasValue)
            {
                ValidateUtf8(entry.RoundId.Value.Value, MaxIdBytes, "round id");
            }

            if (entry.DecisionId.HasValue)
            {
                ValidateUtf8(entry.DecisionId.Value.Value, MaxIdBytes, "decision id");
            }

            if (ContainsResumeToken(entry))
            {
                throw new NotSupportedException(
                    "The plaintext file outbox refuses to persist a resume-token-bearing command. " +
                    "Use a platform secure store for token-bearing recovery commands; the transport ticket is never stored.");
            }
        }

        internal static void ValidateResolutionForWrite(OutboxResolution resolution)
        {
            if (resolution == null)
            {
                throw new ArgumentNullException(nameof(resolution));
            }

            if (!Enum.IsDefined(typeof(OutboxDisposition), (int)resolution.Disposition) ||
                resolution.Disposition == OutboxDisposition.Unspecified)
            {
                throw new ArgumentException("Outbox resolution disposition is not a known definitive value.", nameof(resolution));
            }

            if (!Enum.IsDefined(typeof(ReceiptStatus), (int)resolution.ReceiptStatus) ||
                resolution.ReceiptStatus == ReceiptStatus.Unspecified)
            {
                throw new ArgumentException("Outbox resolution receipt status is not a known definitive value.", nameof(resolution));
            }

            ValidateUtf8(resolution.ReasonCode, MaxReasonBytes, "resolution reason");

            if (!IsHexSha256(resolution.CanonicalPayloadSha256))
            {
                throw new ArgumentException("Outbox resolution digest must be empty or a 64-char SHA-256 hex string.", nameof(resolution));
            }

            if (resolution.CommittedAtMs.HasValue && resolution.CommittedAtMs.Value == 0)
            {
                throw new ArgumentException("Outbox resolution committed time must be positive when present.", nameof(resolution));
            }

            switch (resolution.Disposition)
            {
                case OutboxDisposition.Applied:
                case OutboxDisposition.Recorded:
                {
                    ReceiptStatus expected = resolution.Disposition == OutboxDisposition.Applied
                        ? ReceiptStatus.Applied
                        : ReceiptStatus.Recorded;
                    if (resolution.ReceiptStatus != expected)
                    {
                        throw new ArgumentException("Outbox accepted disposition and receipt status disagree.", nameof(resolution));
                    }

                    if (!resolution.CommandVersion.HasValue)
                    {
                        throw new ArgumentException("An accepted outbox resolution requires command_version.", nameof(resolution));
                    }

                    if (!resolution.EventSeq.HasValue || resolution.EventSeq.Value.Value == 0)
                    {
                        throw new ArgumentException("An accepted outbox resolution requires a positive event_seq.", nameof(resolution));
                    }

                    if (resolution.EvaluatedStateVersion.Value == ulong.MaxValue ||
                        resolution.CommandVersion.Value.Value != resolution.EvaluatedStateVersion.Value + 1)
                    {
                        throw new ArgumentException("command_version must equal evaluated_state_version + 1.", nameof(resolution));
                    }

                    break;
                }

                case OutboxDisposition.Rejected:
                {
                    if (resolution.ReceiptStatus != ReceiptStatus.Rejected)
                    {
                        throw new ArgumentException("A rejected outbox disposition requires REJECTED status.", nameof(resolution));
                    }

                    if (string.IsNullOrEmpty(resolution.ReasonCode))
                    {
                        throw new ArgumentException("A rejected outbox resolution requires a reason code.", nameof(resolution));
                    }

                    if (resolution.CommandVersion.HasValue || resolution.EventSeq.HasValue)
                    {
                        throw new ArgumentException("A rejected outbox resolution must not carry command_version or event_seq.", nameof(resolution));
                    }

                    break;
                }

                case OutboxDisposition.Conflict:
                {
                    if (resolution.ReceiptStatus != ReceiptStatus.Unknown)
                    {
                        throw new ArgumentException("A conflict outbox resolution requires UNKNOWN status.", nameof(resolution));
                    }

                    if (resolution.EvaluatedStateVersion.Value != 0 ||
                        resolution.CommandVersion.HasValue ||
                        resolution.EventSeq.HasValue ||
                        resolution.CommittedAtMs.HasValue ||
                        resolution.FromReceiptLookup)
                    {
                        throw new ArgumentException("A conflict outbox resolution carries fields reserved for a definitive receipt.", nameof(resolution));
                    }

                    break;
                }

                default:
                    throw new ArgumentException("Unknown outbox disposition.", nameof(resolution));
            }
        }

        internal static bool ContainsResumeToken(OutboxEntry entry)
        {
            if (entry.Kind != CommandKind.ResumeSession && entry.Kind != CommandKind.ResumeSpectator)
            {
                return false;
            }

            CanonicalPayloadDocument document = CanonicalPayloadParser.Parse(entry.CanonicalPayload.Span);
            return document.HasField(CommandPayloadFields.ResumeToken);
        }

        private static FileOutboxSnapshot DecodeCore(byte[] raw, FileOutboxOptions options, string storePath)
        {
            if (raw.Length < HeaderBytes)
            {
                throw new OutboxStoreCorruptException(storePath, "the file is shorter than the 56-byte header.");
            }

            using (var stream = new MemoryStream(raw, writable: false))
            using (var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true))
            {
                byte[] magic = ReadExact(reader, MagicBytes.Length, "magic", storePath);
                if (!magic.AsSpan().SequenceEqual(MagicBytes))
                {
                    throw new OutboxStoreCorruptException(storePath, "the magic is not MJOUTBX1.");
                }

                uint version = reader.ReadUInt32();
                if (version != FormatVersion)
                {
                    throw new OutboxStoreCorruptException(
                        storePath,
                        "unsupported format version " + version + "; expected " + FormatVersion + ".");
                }

                uint flags = reader.ReadUInt32();
                if (flags != 0)
                {
                    throw new OutboxStoreCorruptException(
                        storePath,
                        "unknown header flags 0x" + flags.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ".");
                }

                uint bodyLength = reader.ReadUInt32();
                if (bodyLength != raw.Length - HeaderBytes)
                {
                    throw new OutboxStoreCorruptException(storePath, "the body length does not match the file length.");
                }

                uint reserved = reader.ReadUInt32();
                if (reserved != 0)
                {
                    throw new OutboxStoreCorruptException(storePath, "the reserved header word is not zero.");
                }

                byte[] expectedHash = ReadExact(reader, 32, "body checksum", storePath);
                byte[] body = ReadExact(reader, checked((int)bodyLength), "body", storePath);
                byte[] actualHash = ComputeSha256(body);
                if (!FixedTimeEquals(expectedHash, actualHash))
                {
                    throw new OutboxStoreCorruptException(storePath, "the body SHA-256 does not match the header.");
                }

                return DecodeBody(body, options, storePath);
            }
        }

        private static FileOutboxSnapshot DecodeBody(byte[] body, FileOutboxOptions options, string storePath)
        {
            using (var stream = new MemoryStream(body, writable: false))
            using (var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true))
            {
                uint entryCount = reader.ReadUInt32();
                uint resolutionCount = reader.ReadUInt32();
                if (entryCount > (uint)options.MaxEntries)
                {
                    throw new OutboxStoreCorruptException(
                        storePath,
                        "the entry count " + entryCount + " exceeds the configured bound of " + options.MaxEntries + ".");
                }

                if (resolutionCount > (uint)options.MaxResolutions)
                {
                    throw new OutboxStoreCorruptException(
                        storePath,
                        "the resolution count " + resolutionCount + " exceeds the configured bound of " + options.MaxResolutions + ".");
                }

                var snapshot = new FileOutboxSnapshot();
                var keys = new HashSet<IdempotencyKey>();
                for (uint i = 0; i < entryCount; i++)
                {
                    OutboxEntry entry = ReadEntry(reader, options, storePath);
                    if (!keys.Add(entry.Key))
                    {
                        throw new OutboxStoreCorruptException(storePath, "duplicate unresolved outbox key " + entry.Key + ".");
                    }

                    snapshot.Entries.Add(entry);
                }

                var resolutionKeys = new HashSet<IdempotencyKey>();
                for (uint i = 0; i < resolutionCount; i++)
                {
                    IdempotencyKey key = ReadKey(reader, storePath);
                    OutboxResolution resolution = ReadResolution(reader, storePath);
                    if (!resolutionKeys.Add(key))
                    {
                        throw new OutboxStoreCorruptException(storePath, "duplicate resolved outbox key " + key + ".");
                    }

                    if (keys.Contains(key))
                    {
                        throw new OutboxStoreCorruptException(storePath, "outbox key " + key + " is both unresolved and resolved.");
                    }

                    snapshot.Resolutions.Add(key, resolution);
                }

                if (stream.Position != stream.Length)
                {
                    throw new OutboxStoreCorruptException(storePath, "the body has trailing bytes.");
                }

                return snapshot;
            }
        }

        private static OutboxEntry ReadEntry(BinaryReader reader, FileOutboxOptions options, string storePath)
        {
            string keyAccount = ReadString(reader, MaxIdBytes, "entry account", storePath);
            string keyScope = ReadString(reader, MaxIdBytes, "entry scope", storePath);
            string keyRequest = ReadString(reader, MaxIdBytes, "entry request", storePath);
            ushort kindValue = reader.ReadUInt16();
            if (!Enum.IsDefined(typeof(CommandKind), (int)kindValue))
            {
                throw new OutboxStoreCorruptException(storePath, "unknown command kind " + kindValue + ".");
            }

            ushort payloadSchema = reader.ReadUInt16();
            if (payloadSchema != CanonicalPayloadDigest.FormatVersion)
            {
                throw new OutboxStoreCorruptException(storePath, "unsupported canonical payload schema " + payloadSchema + ".");
            }

            ulong expectedState = reader.ReadUInt64();
            byte flags = reader.ReadByte();
            if ((flags & ~(EntryRoundFlag | EntryDecisionFlag | EntryControlFlag | EntryOwnerFlag)) != 0)
            {
                throw new OutboxStoreCorruptException(
                    storePath,
                    "unknown entry metadata flags 0x" + flags.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            RoundId? roundId = (flags & EntryRoundFlag) != 0
                ? new RoundId(ReadString(reader, MaxIdBytes, "round id", storePath))
                : (RoundId?)null;
            DecisionId? decisionId = (flags & EntryDecisionFlag) != 0
                ? new DecisionId(ReadString(reader, MaxIdBytes, "decision id", storePath))
                : (DecisionId?)null;
            ControlEpoch? controlEpoch = (flags & EntryControlFlag) != 0 ? new ControlEpoch(reader.ReadUInt64()) : (ControlEpoch?)null;
            OwnerEpoch? ownerEpoch = (flags & EntryOwnerFlag) != 0 ? new OwnerEpoch(reader.ReadUInt64()) : (OwnerEpoch?)null;

            uint payloadLength = reader.ReadUInt32();
            if (payloadLength == 0 || payloadLength > (uint)options.MaxPayloadBytes)
            {
                throw new OutboxStoreCorruptException(storePath, "payload length " + payloadLength + " is outside the configured bound.");
            }

            byte[] payloadBytes = ReadExact(reader, checked((int)payloadLength), "canonical payload", storePath);
            byte[] storedDigest = ReadExact(reader, PayloadDigest.SizeBytes, "payload digest", storePath);

            string sessionId = ReadString(reader, MaxIdBytes, "session id", storePath);
            string roomId = ReadString(reader, MaxIdBytes, "room id", storePath);

            string scopeId = ReadString(reader, MaxIdBytes, "scope id", storePath);
            string scopeAccount = ReadString(reader, MaxIdBytes, "scope account", storePath);
            byte roleValue = reader.ReadByte();
            if (!Enum.IsDefined(typeof(ProtocolRole), (int)roleValue))
            {
                throw new OutboxStoreCorruptException(storePath, "unknown scope role " + roleValue + ".");
            }

            string scopeRoom = ReadString(reader, MaxIdBytes, "scope room", storePath);
            ulong issuedAt = reader.ReadUInt64();
            ulong expiresAt = reader.ReadUInt64();

            BusinessPayload payload;
            try
            {
                payload = BusinessPayload.Retain(payloadBytes);
            }
            catch (ArgumentException ex)
            {
                throw new OutboxStoreCorruptException(storePath, "the canonical payload is invalid.", ex);
            }

            if (!payload.Digest.AsSpan().SequenceEqual(new ReadOnlySpan<byte>(storedDigest)))
            {
                throw new OutboxStoreCorruptException(storePath, "the stored payload digest does not match the payload bytes.");
            }

            try
            {
                var scope = new RequestScope(
                    new RequestScopeId(scopeId),
                    scopeAccount,
                    (ProtocolRole)roleValue,
                    new RoomId(scopeRoom),
                    issuedAt,
                    expiresAt);
                var command = new ProtocolCommand(
                    (CommandKind)kindValue,
                    payload,
                    new RequestId(keyRequest),
                    new RequestScopeId(keyScope),
                    new StateVersion(expectedState),
                    roundId,
                    decisionId,
                    controlEpoch,
                    ownerEpoch,
                    payloadSchema);
                var entry = new OutboxEntry(command, scope, new SessionId(sessionId), new RoomId(roomId));

                if (!string.Equals(entry.Key.AccountId, keyAccount, StringComparison.Ordinal) ||
                    !string.Equals(entry.Key.ScopeId.Value, keyScope, StringComparison.Ordinal) ||
                    !string.Equals(entry.Key.RequestId.Value, keyRequest, StringComparison.Ordinal) ||
                    !string.Equals(entry.OwnerScope.ScopeId.Value, scopeId, StringComparison.Ordinal) ||
                    !string.Equals(entry.OwnerScope.AccountId, keyAccount, StringComparison.Ordinal) ||
                    !string.Equals(entry.OwnerScope.RoomId.Value, roomId, StringComparison.Ordinal))
                {
                    throw new OutboxStoreCorruptException(storePath, "the entry key and scope metadata disagree.");
                }

                ValidateEntryForWrite(entry, options);
                return entry;
            }
            catch (OutboxStoreCorruptException)
            {
                throw;
            }
            catch (NotSupportedException ex)
            {
                throw new OutboxStoreCorruptException(
                    storePath,
                    "a resume-token-bearing command is not allowed in the plaintext outbox.",
                    ex);
            }
            catch (CommandValidationException ex)
            {
                throw new OutboxStoreCorruptException(storePath, "the stored command payload is invalid.", ex);
            }
            catch (CanonicalEncodingException ex)
            {
                throw new OutboxStoreCorruptException(storePath, "the stored canonical payload is invalid.", ex);
            }
            catch (ArgumentException ex)
            {
                throw new OutboxStoreCorruptException(storePath, "the entry metadata is invalid.", ex);
            }
        }

        private static IdempotencyKey ReadKey(BinaryReader reader, string storePath)
        {
            string account = ReadString(reader, MaxIdBytes, "resolution account", storePath);
            string scope = ReadString(reader, MaxIdBytes, "resolution scope", storePath);
            string request = ReadString(reader, MaxIdBytes, "resolution request", storePath);
            try
            {
                return new IdempotencyKey(account, new RequestScopeId(scope), new RequestId(request));
            }
            catch (ArgumentException ex)
            {
                throw new OutboxStoreCorruptException(storePath, "the resolution key is invalid.", ex);
            }
        }

        private static OutboxResolution ReadResolution(BinaryReader reader, string storePath)
        {
            OutboxDisposition disposition = (OutboxDisposition)reader.ReadByte();
            ReceiptStatus status = (ReceiptStatus)reader.ReadByte();
            if (!Enum.IsDefined(typeof(OutboxDisposition), (int)disposition) ||
                !Enum.IsDefined(typeof(ReceiptStatus), (int)status))
            {
                throw new OutboxStoreCorruptException(storePath, "the resolution has an unknown disposition or receipt status.");
            }

            string reason = ReadString(reader, MaxReasonBytes, "resolution reason", storePath);
            ulong evaluatedValue = reader.ReadUInt64();
            byte flags = reader.ReadByte();
            if ((flags & ~(ResolutionCommandVersionFlag | ResolutionEventSeqFlag | ResolutionCommittedAtFlag)) != 0)
            {
                throw new OutboxStoreCorruptException(
                    storePath,
                    "unknown resolution flags 0x" + flags.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            StateVersion? commandVersion = (flags & ResolutionCommandVersionFlag) != 0
                ? new StateVersion(reader.ReadUInt64())
                : (StateVersion?)null;
            EventSeq? eventSeq = (flags & ResolutionEventSeqFlag) != 0 ? new EventSeq(reader.ReadUInt64()) : (EventSeq?)null;
            ulong? committedAt = (flags & ResolutionCommittedAtFlag) != 0 ? reader.ReadUInt64() : (ulong?)null;
            string sha = ReadString(reader, MaxDigestBytes, "resolution digest", storePath);
            byte fromLookupValue = reader.ReadByte();
            if (fromLookupValue != 0 && fromLookupValue != 1)
            {
                throw new OutboxStoreCorruptException(storePath, "the resolution lookup flag must be 0 or 1.");
            }

            try
            {
                var resolution = new OutboxResolution(
                    disposition,
                    status,
                    reason,
                    new StateVersion(evaluatedValue),
                    commandVersion,
                    eventSeq,
                    committedAt,
                    sha,
                    fromLookupValue == 1);
                ValidateResolutionForWrite(resolution);
                return resolution;
            }
            catch (ArgumentException ex)
            {
                throw new OutboxStoreCorruptException(storePath, "the resolution fields are inconsistent.", ex);
            }
        }

        private static void WriteEntry(BinaryWriter writer, OutboxEntry entry)
        {
            WriteString(writer, entry.Key.AccountId, MaxIdBytes, "entry account");
            WriteString(writer, entry.ScopeId.Value, MaxIdBytes, "entry scope");
            WriteString(writer, entry.RequestId.Value, MaxIdBytes, "entry request");
            writer.Write((ushort)entry.Kind);
            writer.Write(entry.PayloadSchemaVersion);
            writer.Write(entry.ExpectedStateVersion.Value);

            byte flags = 0;
            if (entry.RoundId.HasValue)
            {
                flags |= EntryRoundFlag;
            }

            if (entry.DecisionId.HasValue)
            {
                flags |= EntryDecisionFlag;
            }

            if (entry.ControlEpoch.HasValue)
            {
                flags |= EntryControlFlag;
            }

            if (entry.OwnerEpoch.HasValue)
            {
                flags |= EntryOwnerFlag;
            }

            writer.Write(flags);
            if (entry.RoundId.HasValue)
            {
                WriteString(writer, entry.RoundId.Value.Value, MaxIdBytes, "round id");
            }

            if (entry.DecisionId.HasValue)
            {
                WriteString(writer, entry.DecisionId.Value.Value, MaxIdBytes, "decision id");
            }

            if (entry.ControlEpoch.HasValue)
            {
                writer.Write(entry.ControlEpoch.Value.Value);
            }

            if (entry.OwnerEpoch.HasValue)
            {
                writer.Write(entry.OwnerEpoch.Value.Value);
            }

            byte[] payload = entry.CopyCanonicalPayload();
            writer.Write((uint)payload.Length);
            writer.Write(payload);
            writer.Write(entry.Digest.AsSpan().ToArray());

            WriteString(writer, entry.SessionId.Value, MaxIdBytes, "session id");
            WriteString(writer, entry.RoomId.Value, MaxIdBytes, "room id");

            WriteString(writer, entry.OwnerScope.ScopeId.Value, MaxIdBytes, "scope id");
            WriteString(writer, entry.OwnerScope.AccountId, MaxIdBytes, "scope account");
            writer.Write((byte)entry.OwnerScope.Role);
            WriteString(writer, entry.OwnerScope.RoomId.Value, MaxIdBytes, "scope room");
            writer.Write(entry.OwnerScope.IssuedAtMs);
            writer.Write(entry.OwnerScope.ExpiresAtMs);
        }

        private static void WriteKey(BinaryWriter writer, IdempotencyKey key)
        {
            WriteString(writer, key.AccountId, MaxIdBytes, "resolution account");
            WriteString(writer, key.ScopeId.Value, MaxIdBytes, "resolution scope");
            WriteString(writer, key.RequestId.Value, MaxIdBytes, "resolution request");
        }

        private static void WriteResolution(BinaryWriter writer, OutboxResolution resolution)
        {
            writer.Write((byte)resolution.Disposition);
            writer.Write((byte)resolution.ReceiptStatus);
            WriteString(writer, resolution.ReasonCode, MaxReasonBytes, "resolution reason");
            writer.Write(resolution.EvaluatedStateVersion.Value);

            byte flags = 0;
            if (resolution.CommandVersion.HasValue)
            {
                flags |= ResolutionCommandVersionFlag;
            }

            if (resolution.EventSeq.HasValue)
            {
                flags |= ResolutionEventSeqFlag;
            }

            if (resolution.CommittedAtMs.HasValue)
            {
                flags |= ResolutionCommittedAtFlag;
            }

            writer.Write(flags);
            if (resolution.CommandVersion.HasValue)
            {
                writer.Write(resolution.CommandVersion.Value.Value);
            }

            if (resolution.EventSeq.HasValue)
            {
                writer.Write(resolution.EventSeq.Value.Value);
            }

            if (resolution.CommittedAtMs.HasValue)
            {
                writer.Write(resolution.CommittedAtMs.Value);
            }

            WriteString(writer, resolution.CanonicalPayloadSha256, MaxDigestBytes, "resolution digest");
            writer.Write(resolution.FromReceiptLookup ? (byte)1 : (byte)0);
        }

        private static void WriteString(BinaryWriter writer, string value, int maxBytes, string field)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            int byteCount;
            byte[] bytes;
            try
            {
                byteCount = StrictUtf8.GetByteCount(value);
                if (byteCount > maxBytes)
                {
                    throw new ArgumentException(
                        field + " exceeds the " + maxBytes + "-byte bound.", nameof(value));
                }

                bytes = StrictUtf8.GetBytes(value);
            }
            catch (EncoderFallbackException ex)
            {
                throw new ArgumentException(field + " is not valid UTF-16.", nameof(value), ex);
            }

            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader, int maxBytes, string field, string storePath)
        {
            uint length = reader.ReadUInt32();
            if (length > (uint)maxBytes)
            {
                throw new OutboxStoreCorruptException(storePath, field + " exceeds the " + maxBytes + "-byte bound.");
            }

            byte[] bytes = ReadExact(reader, checked((int)length), field, storePath);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new OutboxStoreCorruptException(storePath, field + " is not valid UTF-8.", ex);
            }
        }

        private static byte[] ReadExact(BinaryReader reader, int length, string field, string storePath)
        {
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new OutboxStoreCorruptException(
                    storePath,
                    "the file ends while reading " + field + " (" + length + " bytes expected).");
            }

            return bytes;
        }

        private static bool IsHexSha256(string value)
        {
            if (value.Length == 0)
            {
                return true;
            }

            if (value.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') ||
                           (c >= 'a' && c <= 'f') ||
                           (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateUtf8(string value, int maxBytes, string field)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            int byteCount;
            try
            {
                byteCount = StrictUtf8.GetByteCount(value);
            }
            catch (EncoderFallbackException ex)
            {
                throw new ArgumentException(field + " is not valid UTF-16.", nameof(value), ex);
            }

            if (byteCount > maxBytes)
            {
                throw new ArgumentException(field + " exceeds the " + maxBytes + "-byte bound.", nameof(value));
            }
        }

        private static int CompareKeys(IdempotencyKey left, IdempotencyKey right)
        {
            int result = string.CompareOrdinal(left.AccountId, right.AccountId);
            if (result != 0)
            {
                return result;
            }

            result = string.CompareOrdinal(left.ScopeId.Value, right.ScopeId.Value);
            return result != 0 ? result : string.CompareOrdinal(left.RequestId.Value, right.RequestId.Value);
        }

        private static byte[] ComputeSha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }

            return diff == 0;
        }
    }
}
