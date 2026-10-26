using System;
using System.Collections.Generic;
using System.Text;
using Mahjong.Contracts;
using Mahjong.Unity.Table;

namespace Mahjong.Unity.TableModel.Tests
{
    /// <summary>
    /// Test-only logical reader for the frozen canonical payload. The official
    /// CommandPayloadValidator is still invoked, but Contracts 1's
    /// CanonicalPayloadDocument.AsString currently returns the four-byte string
    /// length prefix together with the text. That reader defect is outside this
    /// sub-range; this helper strips the writer's length prefix so each logical
    /// business field can be compared exactly. It does not alter production
    /// bytes or the official validator.
    /// </summary>
    internal sealed class TestPayload
    {
        private readonly Dictionary<ushort, Field> _fields = new Dictionary<ushort, Field>();

        public TestPayload(TableCommandIntent intent)
        {
            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            CommandPayloadValidator.Validate(intent.Kind, intent.Command.Payload);
            Parse(intent.Command.Payload.CopyBytes());
        }

        public TestPayload(byte[] canonicalBytes)
        {
            if (canonicalBytes == null)
            {
                throw new ArgumentNullException(nameof(canonicalBytes));
            }

            Parse(canonicalBytes);
        }

        public bool HasField(ushort fieldNumber)
        {
            return _fields.ContainsKey(fieldNumber);
        }

        public string RequireString(ushort fieldNumber, string fieldName)
        {
            Field field = Require(fieldNumber, fieldName);
            if (field.Type != CanonicalValueType.String)
            {
                throw new InvalidOperationException(fieldName + " is not a string field.");
            }

            if (field.Raw.Length < 4)
            {
                throw new InvalidOperationException(fieldName + " string field is truncated.");
            }

            uint length = ReadUInt32(field.Raw, 0);
            if (length > field.Raw.Length - 4)
            {
                throw new InvalidOperationException(fieldName + " string length exceeds its field.");
            }

            return new UTF8Encoding(false, true).GetString(field.Raw, 4, (int)length);
        }

        public uint RequireUInt32(ushort fieldNumber, string fieldName)
        {
            Field field = Require(fieldNumber, fieldName);
            if (field.Type != CanonicalValueType.UInt32 || field.Raw.Length != 4)
            {
                throw new InvalidOperationException(fieldName + " is not a uint32 field.");
            }

            return ReadUInt32(field.Raw, 0);
        }

        public ulong RequireUInt64(ushort fieldNumber, string fieldName)
        {
            Field field = Require(fieldNumber, fieldName);
            if (field.Type != CanonicalValueType.UInt64 || field.Raw.Length != 8)
            {
                throw new InvalidOperationException(fieldName + " is not a uint64 field.");
            }

            return ReadUInt64(field.Raw, 0);
        }

        public bool RequireBool(ushort fieldNumber, string fieldName)
        {
            Field field = Require(fieldNumber, fieldName);
            if (field.Type != CanonicalValueType.Bool || field.Raw.Length != 1)
            {
                throw new InvalidOperationException(fieldName + " is not a bool field.");
            }

            return field.Raw[0] != 0;
        }

        public IReadOnlyList<uint> RequireRepeatedUInt32(ushort fieldNumber, string fieldName)
        {
            Field field = Require(fieldNumber, fieldName);
            if (field.Type != CanonicalValueType.RepeatedUInt32 || field.Raw.Length < 4)
            {
                throw new InvalidOperationException(fieldName + " is not a repeated uint32 field.");
            }

            uint count = ReadUInt32(field.Raw, 0);
            if (count > (field.Raw.Length - 4) / 4)
            {
                throw new InvalidOperationException(fieldName + " repeated count exceeds its field.");
            }

            var values = new uint[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = ReadUInt32(field.Raw, 4 + (i * 4));
            }

            return values;
        }

        private Field Require(ushort fieldNumber, string fieldName)
        {
            if (!_fields.TryGetValue(fieldNumber, out Field? field))
            {
                throw new InvalidOperationException("Missing canonical field " + fieldName + " (" + fieldNumber + ").");
            }

            return field;
        }

        private void Parse(byte[] bytes)
        {
            if (bytes.Length < 8)
            {
                throw new InvalidOperationException("Canonical payload is shorter than its header.");
            }

            int offset = 8;
            ushort lastField = 0;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 7)
                {
                    throw new InvalidOperationException("Truncated canonical field header.");
                }

                ushort fieldNumber = ReadUInt16(bytes, offset);
                CanonicalValueType type = (CanonicalValueType)bytes[offset + 2];
                uint length = ReadUInt32(bytes, offset + 3);
                offset += 7;
                if (fieldNumber <= lastField || length > int.MaxValue || bytes.Length - offset < (int)length)
                {
                    throw new InvalidOperationException("Invalid canonical field " + fieldNumber + ".");
                }

                byte[] raw = new byte[(int)length];
                Array.Copy(bytes, offset, raw, 0, raw.Length);
                offset += (int)length;
                lastField = fieldNumber;
                _fields.Add(fieldNumber, new Field(type, raw));
            }
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)bytes[offset] |
                   ((uint)bytes[offset + 1] << 8) |
                   ((uint)bytes[offset + 2] << 16) |
                   ((uint)bytes[offset + 3] << 24);
        }

        private static ulong ReadUInt64(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int i = 7; i >= 0; i--)
            {
                value = (value << 8) | bytes[offset + i];
            }

            return value;
        }

        private sealed class Field
        {
            public Field(CanonicalValueType type, byte[] raw)
            {
                Type = type;
                Raw = raw;
            }

            public CanonicalValueType Type { get; }

            public byte[] Raw { get; }
        }
    }
}
