using System;
using System.Collections.Generic;
using System.Text;
using Mahjong.Contracts;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Strict local reader for the frozen MJC1 canonical command payload. It intentionally does
    /// not depend on the Contracts convenience accessors for strings: the wire/schema contract
    /// is the raw length-prefixed field body, and the local host must interpret exactly those
    /// bytes rather than a rendered convenience value.
    /// </summary>
    internal sealed class LocalCommandPayload
    {
        private const ushort FormatVersion = 1;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private readonly Dictionary<ushort, byte[]> _fields;

        private LocalCommandPayload(ushort commandKind, Dictionary<ushort, byte[]> fields)
        {
            CommandKind = commandKind;
            _fields = fields;
        }

        public ushort CommandKind { get; }

        public bool HasField(ushort field) => _fields.ContainsKey(field);

        public static LocalCommandPayload Parse(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length < 8 ||
                bytes[0] != (byte)'M' || bytes[1] != (byte)'J' ||
                bytes[2] != (byte)'C' || bytes[3] != (byte)'1')
            {
                throw new CanonicalEncodingException("Canonical payload magic/header is invalid.");
            }

            ushort version = ReadUInt16(bytes, 4);
            if (version != FormatVersion)
            {
                throw new CanonicalEncodingException("Unsupported canonical payload version " + version + ".");
            }

            ushort kind = ReadUInt16(bytes, 6);
            if (kind == 0)
            {
                throw new CanonicalEncodingException("Canonical command kind is missing.");
            }

            var fields = new Dictionary<ushort, byte[]>();
            int offset = 8;
            ushort last = 0;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 7)
                {
                    throw new CanonicalEncodingException("Canonical field header is truncated.");
                }

                ushort field = ReadUInt16(bytes, offset);
                byte type = bytes[offset + 2];
                uint length = ReadUInt32(bytes, offset + 3);
                offset += 7;
                if (field == 0 || field <= last)
                {
                    throw new CanonicalEncodingException("Canonical fields are not strictly ascending.");
                }

                if (length > int.MaxValue || bytes.Length - offset < (int)length)
                {
                    throw new CanonicalEncodingException("Canonical field is truncated.");
                }

                byte[] body = new byte[(int)length];
                Array.Copy(bytes, offset, body, 0, body.Length);
                offset += body.Length;
                last = field;
                fields.Add(field, body);
            }

            return new LocalCommandPayload(kind, fields);
        }

        public string RequireString(ushort field, string name)
        {
            byte[] body = Require(field, name);
            if (body.Length < 4)
            {
                throw new CanonicalEncodingException(name + " is missing its UTF-8 length.");
            }

            int length = checked((int)ReadUInt32(body, 0));
            if (length != body.Length - 4)
            {
                throw new CanonicalEncodingException(name + " has an invalid UTF-8 length.");
            }

            try
            {
                return Utf8.GetString(body, 4, length);
            }
            catch (DecoderFallbackException exception)
            {
                throw new CanonicalEncodingException(name + " is not valid UTF-8.", exception);
            }
        }

        public bool TryGetString(ushort field, string name, out string value)
        {
            if (!HasField(field))
            {
                value = string.Empty;
                return false;
            }

            value = RequireString(field, name);
            return true;
        }

        public uint RequireUInt32(ushort field, string name)
        {
            byte[] body = Require(field, name);
            if (body.Length != 4)
            {
                throw new CanonicalEncodingException(name + " must be a 4-byte uint32.");
            }

            return ReadUInt32(body, 0);
        }

        public ulong RequireUInt64(ushort field, string name)
        {
            byte[] body = Require(field, name);
            if (body.Length != 8)
            {
                throw new CanonicalEncodingException(name + " must be an 8-byte uint64.");
            }

            return ((ulong)ReadUInt32(body, 0)) | ((ulong)ReadUInt32(body, 4) << 32);
        }

        public IReadOnlyList<uint> RequireRepeatedUInt32(ushort field, string name)
        {
            byte[] body = Require(field, name);
            if (body.Length < 4)
            {
                throw new CanonicalEncodingException(name + " is missing its count.");
            }

            int count = checked((int)ReadUInt32(body, 0));
            if (body.Length != 4 + (count * 4))
            {
                throw new CanonicalEncodingException(name + " has an invalid repeated size.");
            }

            var values = new uint[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = ReadUInt32(body, 4 + (i * 4));
            }

            return values;
        }

        private byte[] Require(ushort field, string name)
        {
            if (!_fields.TryGetValue(field, out byte[]? body))
            {
                throw new CanonicalEncodingException("Missing field " + name + ".");
            }

            return body;
        }

        private static ushort ReadUInt16(byte[] bytes, int offset) =>
            (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

        private static uint ReadUInt32(byte[] bytes, int offset) =>
            (uint)(bytes[offset] |
                   (bytes[offset + 1] << 8) |
                   (bytes[offset + 2] << 16) |
                   (bytes[offset + 3] << 24));
    }
}
