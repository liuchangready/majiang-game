using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Mahjong.Contracts
{
    /// <summary>
    /// Canonical command-payload encoding (format MJC1, version 1).
    ///
    /// This is deliberately NOT Protobuf. Protobuf serialization is not a
    /// canonical representation: field order, unknown fields and alternate
    /// varint encodings can all change the bytes for the same business intent.
    /// The idempotency digest is therefore computed over this explicit
    /// little-endian, length-prefixed, strictly-ascending field encoding.
    ///
    /// Layout:
    ///   bytes[0..3]  "MJC1"
    ///   u16          format version (1)
    ///   u16          command kind
    ///   repeated:
    ///     u16        field number (strictly ascending, never 0)
    ///     u8         value type (see CanonicalValueType)
    ///     u32        payload length
    ///     bytes      payload
    ///
    /// Fixed-width integers are little-endian two's complement. Strings are
    /// strict UTF-8 with a byte length. Repeated uint32 is u32 count followed
    /// by count little-endian u32 values. No maps, floats or implicit
    /// normalization are permitted.
    /// </summary>
    public static class CanonicalPayloadDigest
    {
        public const ushort FormatVersion = 1;
        public const int MaxPayloadBytes = 1024 * 1024;
        private const int HeaderBytes = 8;
        private const int MaxFields = 1024;

        private static readonly byte[] MagicBytes = { (byte)'M', (byte)'J', (byte)'C', (byte)'1' };
        private static readonly byte[] DomainPrefix = Encoding.UTF8.GetBytes("MAHJONG-V5-CANONICAL-COMMAND\0");

        internal static ReadOnlySpan<byte> Magic => MagicBytes;

        public static PayloadDigest Compute(ReadOnlySpan<byte> canonicalPayload)
        {
            if (canonicalPayload.Length == 0)
            {
                throw new ArgumentException("Canonical payload must not be empty.", nameof(canonicalPayload));
            }

            if (canonicalPayload.Length > MaxPayloadBytes)
            {
                throw new ArgumentException("Canonical payload exceeds the 1 MiB cap.", nameof(canonicalPayload));
            }

            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                hash.AppendData(DomainPrefix);
                Span<byte> length = stackalloc byte[4];
                WriteUInt32(length, (uint)canonicalPayload.Length);
                hash.AppendData(length);
                hash.AppendData(canonicalPayload);
                return new PayloadDigest(hash.GetHashAndReset());
            }
        }

        public static PayloadDigest Compute(BusinessPayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            return Compute(payload.AsSpan());
        }

        public static bool Verify(ReadOnlySpan<byte> canonicalPayload, PayloadDigest expected)
        {
            PayloadDigest actual = Compute(canonicalPayload);
            return actual == expected;
        }

        internal static void WriteUInt16(Span<byte> destination, ushort value)
        {
            destination[0] = (byte)value;
            destination[1] = (byte)(value >> 8);
        }

        internal static void WriteUInt32(Span<byte> destination, uint value)
        {
            destination[0] = (byte)value;
            destination[1] = (byte)(value >> 8);
            destination[2] = (byte)(value >> 16);
            destination[3] = (byte)(value >> 24);
        }

        internal static void WriteUInt64(Span<byte> destination, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                destination[i] = (byte)(value >> (8 * i));
            }
        }

        internal static ushort ReadUInt16(ReadOnlySpan<byte> source)
        {
            return (ushort)(source[0] | (source[1] << 8));
        }

        internal static uint ReadUInt32(ReadOnlySpan<byte> source)
        {
            return (uint)(source[0] | (source[1] << 8) | (source[2] << 16) | (source[3] << 24));
        }

        internal static ulong ReadUInt64(ReadOnlySpan<byte> source)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value |= (ulong)source[i] << (8 * i);
            }

            return value;
        }
    }

    public enum CanonicalValueType : byte
    {
        Unspecified = 0,
        Bool = 1,
        UInt32 = 2,
        UInt64 = 3,
        Int64 = 4,
        String = 5,
        Bytes = 6,
        RepeatedUInt32 = 7
    }

    public sealed class CanonicalEncodingException : Exception
    {
        public CanonicalEncodingException(string message) : base(message)
        {
        }

        public CanonicalEncodingException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    /// <summary>Builder for the MJC1 canonical business encoding.</summary>
    public sealed class CanonicalPayloadWriter
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly MemoryStream _buffer = new MemoryStream();
        private readonly ushort _commandKind;
        private ushort _lastFieldNumber;
        private long _lengthPosition;
        private bool _sealed;

        public CanonicalPayloadWriter(ushort commandKind)
        {
            if (commandKind == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandKind), commandKind, "Command kind must be explicit.");
            }

            _commandKind = commandKind;
            _buffer.Write(CanonicalPayloadDigest.Magic);
            WriteUInt16(CanonicalPayloadDigest.FormatVersion);
            WriteUInt16(commandKind);
        }

        public CanonicalPayloadWriter WriteBool(ushort fieldNumber, bool value)
        {
            BeginField(fieldNumber, CanonicalValueType.Bool);
            _buffer.WriteByte(value ? (byte)1 : (byte)0);
            EndField();
            return this;
        }

        public CanonicalPayloadWriter WriteUInt32(ushort fieldNumber, uint value)
        {
            BeginField(fieldNumber, CanonicalValueType.UInt32);
            WriteUInt32(value);
            EndField();
            return this;
        }

        public CanonicalPayloadWriter WriteUInt64(ushort fieldNumber, ulong value)
        {
            BeginField(fieldNumber, CanonicalValueType.UInt64);
            WriteUInt64(value);
            EndField();
            return this;
        }

        public CanonicalPayloadWriter WriteInt64(ushort fieldNumber, long value)
        {
            BeginField(fieldNumber, CanonicalValueType.Int64);
            WriteUInt64(unchecked((ulong)value));
            EndField();
            return this;
        }

        public CanonicalPayloadWriter WriteString(ushort fieldNumber, string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            byte[] encoded;
            try
            {
                encoded = StrictUtf8.GetBytes(value);
            }
            catch (EncoderFallbackException ex)
            {
                throw new CanonicalEncodingException("String is not valid UTF-16/UTF-8.", ex);
            }

            BeginField(fieldNumber, CanonicalValueType.String);
            WriteUInt32(checked((uint)encoded.Length));
            _buffer.Write(encoded, 0, encoded.Length);
            EndField();
            return this;
        }

        public CanonicalPayloadWriter WriteBytes(ushort fieldNumber, ReadOnlySpan<byte> value)
        {
            BeginField(fieldNumber, CanonicalValueType.Bytes);
            WriteUInt32(checked((uint)value.Length));
            _buffer.Write(value);
            EndField();
            return this;
        }

        public CanonicalPayloadWriter WriteRepeatedUInt32(ushort fieldNumber, IReadOnlyList<uint> values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            BeginField(fieldNumber, CanonicalValueType.RepeatedUInt32);
            WriteUInt32(checked((uint)values.Count));
            for (int i = 0; i < values.Count; i++)
            {
                WriteUInt32(values[i]);
            }

            EndField();
            return this;
        }

        public BusinessPayload Build()
        {
            if (_sealed)
            {
                throw new InvalidOperationException("Canonical payload was already built.");
            }

            _sealed = true;
            byte[] bytes = _buffer.ToArray();
            if (bytes.Length > CanonicalPayloadDigest.MaxPayloadBytes)
            {
                throw new CanonicalEncodingException("Canonical payload exceeds the 1 MiB cap.");
            }

            return BusinessPayload.Retain(bytes);
        }

        private void BeginField(ushort fieldNumber, CanonicalValueType type)
        {
            if (_sealed)
            {
                throw new InvalidOperationException("Canonical payload was already built.");
            }

            if (fieldNumber == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fieldNumber), fieldNumber, "Field number 0 is reserved.");
            }

            if (fieldNumber <= _lastFieldNumber)
            {
                throw new CanonicalEncodingException(
                    "Field numbers must be strictly ascending; got " + fieldNumber + " after " + _lastFieldNumber + ".");
            }

            _lastFieldNumber = fieldNumber;
            WriteUInt16(fieldNumber);
            _buffer.WriteByte((byte)type);
            _lengthPosition = _buffer.Position;
            _buffer.WriteByte(0);
            _buffer.WriteByte(0);
            _buffer.WriteByte(0);
            _buffer.WriteByte(0);
        }

        private void EndField()
        {
            long end = _buffer.Position;
            long payloadLength = end - _lengthPosition - 4;
            _buffer.Position = _lengthPosition;
            WriteUInt32(checked((uint)payloadLength));
            _buffer.Position = end;

            if (_buffer.Length > CanonicalPayloadDigest.MaxPayloadBytes)
            {
                throw new CanonicalEncodingException("Canonical payload exceeds the 1 MiB cap.");
            }
        }

        private void WriteUInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            CanonicalPayloadDigest.WriteUInt16(bytes, value);
            _buffer.Write(bytes);
        }

        private void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            CanonicalPayloadDigest.WriteUInt32(bytes, value);
            _buffer.Write(bytes);
        }

        private void WriteUInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            CanonicalPayloadDigest.WriteUInt64(bytes, value);
            _buffer.Write(bytes);
        }
    }

    /// <summary>Immutable exact business bytes. Retries reuse these bytes unchanged.</summary>
    public sealed class BusinessPayload : IEquatable<BusinessPayload>
    {
        private readonly byte[] _bytes;

        private BusinessPayload(byte[] ownedBytes)
        {
            _bytes = ownedBytes;
            Digest = CanonicalPayloadDigest.Compute(ownedBytes);
        }

        public int Length => _bytes.Length;

        public PayloadDigest Digest { get; }

        public static BusinessPayload Retain(ReadOnlySpan<byte> original)
        {
            if (original.Length == 0)
            {
                throw new ArgumentException("Business payload must not be empty.", nameof(original));
            }

            byte[] copy = new byte[original.Length];
            original.CopyTo(copy);
            return new BusinessPayload(copy);
        }

        public static BusinessPayload Retain(byte[]? original)
        {
            if (original == null)
            {
                throw new ArgumentNullException(nameof(original));
            }

            return Retain(new ReadOnlySpan<byte>(original));
        }

        public ReadOnlySpan<byte> AsSpan()
        {
            return new ReadOnlySpan<byte>(_bytes);
        }

        public byte[] CopyBytes()
        {
            byte[] copy = new byte[_bytes.Length];
            Array.Copy(_bytes, copy, _bytes.Length);
            return copy;
        }

        public bool Equals(BusinessPayload? other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other == null || other._bytes.Length != _bytes.Length)
            {
                return false;
            }

            return new ReadOnlySpan<byte>(_bytes).SequenceEqual(new ReadOnlySpan<byte>(other._bytes));
        }

        public override bool Equals(object? obj) => obj is BusinessPayload other && Equals(other);

        public override int GetHashCode() => Digest.GetHashCode();

        public override string ToString() => "business-payload[len=" + _bytes.Length + "]";
    }

    public readonly struct PayloadDigest : IEquatable<PayloadDigest>
    {
        public const int SizeBytes = 32;
        private readonly byte[]? _bytes;

        internal PayloadDigest(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != SizeBytes)
            {
                throw new ArgumentException("SHA-256 digest must be 32 bytes.", nameof(bytes));
            }

            _bytes = bytes;
        }

        public bool IsEmpty => _bytes == null;

        public ReadOnlySpan<byte> AsSpan() => _bytes == null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_bytes);

        public string ToHex()
        {
            if (_bytes == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(SizeBytes * 2);
            for (int i = 0; i < _bytes.Length; i++)
            {
                builder.Append(_bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        public bool Equals(PayloadDigest other)
        {
            if (_bytes == null || other._bytes == null)
            {
                return _bytes == null && other._bytes == null;
            }

            int diff = 0;
            for (int i = 0; i < SizeBytes; i++)
            {
                diff |= _bytes[i] ^ other._bytes[i];
            }

            return diff == 0;
        }

        public override bool Equals(object? obj) => obj is PayloadDigest other && Equals(other);

        public override int GetHashCode()
        {
            if (_bytes == null)
            {
                return 0;
            }

            unchecked
            {
                int hash = 17;
                for (int i = 0; i < 8; i++)
                {
                    hash = (hash * 31) + _bytes[i];
                }

                return hash;
            }
        }

        public override string ToString() => ToHex();

        public static bool operator ==(PayloadDigest left, PayloadDigest right) => left.Equals(right);
        public static bool operator !=(PayloadDigest left, PayloadDigest right) => !left.Equals(right);
    }

    /// <summary>Parsed canonical payload with strict structural validation.</summary>
    public sealed class CanonicalPayloadDocument
    {
        private readonly Dictionary<ushort, CanonicalFieldValue> _fields;

        internal CanonicalPayloadDocument(ushort commandKind, Dictionary<ushort, CanonicalFieldValue> fields)
        {
            CommandKind = commandKind;
            _fields = fields;
        }

        public ushort CommandKind { get; }

        public int FieldCount => _fields.Count;

        public bool HasField(ushort fieldNumber) => _fields.ContainsKey(fieldNumber);

        public CanonicalFieldValue Require(ushort fieldNumber, string fieldName)
        {
            if (!_fields.TryGetValue(fieldNumber, out CanonicalFieldValue? value))
            {
                throw new CanonicalEncodingException("Missing required field " + fieldName + " (" + fieldNumber + ").");
            }

            return value;
        }

        public uint RequireUInt32(ushort fieldNumber, string fieldName)
        {
            return Require(fieldNumber, fieldName).AsUInt32(fieldName);
        }

        public ulong RequireUInt64(ushort fieldNumber, string fieldName)
        {
            return Require(fieldNumber, fieldName).AsUInt64(fieldName);
        }

        public long RequireInt64(ushort fieldNumber, string fieldName)
        {
            return Require(fieldNumber, fieldName).AsInt64(fieldName);
        }

        public bool RequireBool(ushort fieldNumber, string fieldName)
        {
            return Require(fieldNumber, fieldName).AsBool(fieldName);
        }

        public string RequireString(ushort fieldNumber, string fieldName)
        {
            return Require(fieldNumber, fieldName).AsString(fieldName);
        }

        public IReadOnlyList<uint> RequireRepeatedUInt32(ushort fieldNumber, string fieldName)
        {
            return Require(fieldNumber, fieldName).AsRepeatedUInt32(fieldName);
        }

        public uint? OptionalUInt32(ushort fieldNumber)
        {
            return _fields.TryGetValue(fieldNumber, out CanonicalFieldValue? value) ? value.AsUInt32(fieldNumber.ToString()) : (uint?)null;
        }
    }

    public sealed class CanonicalFieldValue
    {
        private readonly byte[] _raw;

        internal CanonicalFieldValue(ushort fieldNumber, CanonicalValueType type, byte[] raw)
        {
            FieldNumber = fieldNumber;
            Type = type;
            _raw = raw;
        }

        public ushort FieldNumber { get; }

        public CanonicalValueType Type { get; }

        public uint AsUInt32(string fieldName)
        {
            RequireType(CanonicalValueType.UInt32, fieldName);
            return CanonicalPayloadDigest.ReadUInt32(_raw);
        }

        public ulong AsUInt64(string fieldName)
        {
            RequireType(CanonicalValueType.UInt64, fieldName);
            return CanonicalPayloadDigest.ReadUInt64(_raw);
        }

        public long AsInt64(string fieldName)
        {
            RequireType(CanonicalValueType.Int64, fieldName);
            return unchecked((long)CanonicalPayloadDigest.ReadUInt64(_raw));
        }

        public bool AsBool(string fieldName)
        {
            RequireType(CanonicalValueType.Bool, fieldName);
            return _raw[0] != 0;
        }

        public string AsString(string fieldName)
        {
            RequireType(CanonicalValueType.String, fieldName);
            try
            {
                return new UTF8Encoding(false, true).GetString(_raw);
            }
            catch (DecoderFallbackException ex)
            {
                throw new CanonicalEncodingException("Field " + fieldName + " is not valid UTF-8.", ex);
            }
        }

        public IReadOnlyList<uint> AsRepeatedUInt32(string fieldName)
        {
            RequireType(CanonicalValueType.RepeatedUInt32, fieldName);
            int count = checked((int)CanonicalPayloadDigest.ReadUInt32(_raw));
            if (count < 0 || _raw.Length != 4 + (count * 4))
            {
                throw new CanonicalEncodingException("Field " + fieldName + " has an invalid repeated length.");
            }

            var values = new uint[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = CanonicalPayloadDigest.ReadUInt32(new ReadOnlySpan<byte>(_raw, 4 + (i * 4), 4));
            }

            return Array.AsReadOnly(values);
        }

        private void RequireType(CanonicalValueType expected, string fieldName)
        {
            if (Type != expected)
            {
                throw new CanonicalEncodingException(
                    "Field " + fieldName + " has type " + Type + " but " + expected + " was required.");
            }
        }
    }

    public static class CanonicalPayloadParser
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static CanonicalPayloadDocument Parse(BusinessPayload payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            return Parse(payload.AsSpan());
        }

        public static CanonicalPayloadDocument Parse(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 8)
            {
                throw new CanonicalEncodingException("Canonical payload is shorter than the 8-byte header.");
            }

            if (!bytes.Slice(0, 4).SequenceEqual(CanonicalPayloadDigest.Magic))
            {
                throw new CanonicalEncodingException("Canonical payload magic is not MJC1.");
            }

            ushort formatVersion = CanonicalPayloadDigest.ReadUInt16(bytes.Slice(4, 2));
            if (formatVersion != CanonicalPayloadDigest.FormatVersion)
            {
                throw new CanonicalEncodingException("Unsupported canonical format version " + formatVersion + ".");
            }

            ushort commandKind = CanonicalPayloadDigest.ReadUInt16(bytes.Slice(6, 2));
            if (commandKind == 0)
            {
                throw new CanonicalEncodingException("Canonical command kind must be explicit.");
            }

            var fields = new Dictionary<ushort, CanonicalFieldValue>();
            int offset = 8;
            ushort lastField = 0;

            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 7)
                {
                    throw new CanonicalEncodingException("Truncated canonical field header.");
                }

                ushort fieldNumber = CanonicalPayloadDigest.ReadUInt16(bytes.Slice(offset, 2));
                CanonicalValueType type = (CanonicalValueType)bytes[offset + 2];
                uint length = CanonicalPayloadDigest.ReadUInt32(bytes.Slice(offset + 3, 4));
                offset += 7;

                if (fieldNumber == 0)
                {
                    throw new CanonicalEncodingException("Field number 0 is reserved.");
                }

                if (fieldNumber <= lastField)
                {
                    throw new CanonicalEncodingException("Field numbers are not strictly ascending at " + fieldNumber + ".");
                }

                if (type < CanonicalValueType.Bool || type > CanonicalValueType.RepeatedUInt32)
                {
                    throw new CanonicalEncodingException("Unknown canonical value type " + (byte)type + ".");
                }

                if (length > int.MaxValue || bytes.Length - offset < (int)length)
                {
                    throw new CanonicalEncodingException("Canonical field " + fieldNumber + " is truncated.");
                }

                int fieldLength = (int)length;
                byte[] raw = new byte[fieldLength];
                bytes.Slice(offset, fieldLength).CopyTo(raw);
                offset += fieldLength;
                lastField = fieldNumber;

                ValidateValue(fieldNumber, type, raw);
                fields.Add(fieldNumber, new CanonicalFieldValue(fieldNumber, type, raw));

                if (fields.Count > 1024)
                {
                    throw new CanonicalEncodingException("Canonical payload exceeds the field-count cap.");
                }
            }

            return new CanonicalPayloadDocument(commandKind, fields);
        }

        private static void ValidateValue(ushort fieldNumber, CanonicalValueType type, byte[] raw)
        {
            switch (type)
            {
                case CanonicalValueType.Bool:
                    if (raw.Length != 1 || (raw[0] != 0 && raw[0] != 1))
                    {
                        throw new CanonicalEncodingException("Bool field " + fieldNumber + " must be a single 0/1 byte.");
                    }

                    break;
                case CanonicalValueType.UInt32:
                    if (raw.Length != 4)
                    {
                        throw new CanonicalEncodingException("UInt32 field " + fieldNumber + " must be 4 bytes.");
                    }

                    break;
                case CanonicalValueType.UInt64:
                case CanonicalValueType.Int64:
                    if (raw.Length != 8)
                    {
                        throw new CanonicalEncodingException("64-bit field " + fieldNumber + " must be 8 bytes.");
                    }

                    break;
                case CanonicalValueType.String:
                    try
                    {
                        StrictUtf8.GetString(raw);
                    }
                    catch (DecoderFallbackException ex)
                    {
                        throw new CanonicalEncodingException("String field " + fieldNumber + " is not valid UTF-8.", ex);
                    }

                    break;
                case CanonicalValueType.Bytes:
                    break;
                case CanonicalValueType.RepeatedUInt32:
                    if (raw.Length < 4)
                    {
                        throw new CanonicalEncodingException("Repeated field " + fieldNumber + " is missing its count.");
                    }

                    uint count = CanonicalPayloadDigest.ReadUInt32(raw);
                    if (count > int.MaxValue || raw.Length != 4 + (count * 4))
                    {
                        throw new CanonicalEncodingException("Repeated field " + fieldNumber + " has an invalid size.");
                    }

                    break;
                default:
                    throw new CanonicalEncodingException("Unknown canonical value type for field " + fieldNumber + ".");
            }
        }
    }
}
