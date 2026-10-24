using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// One committed command's event-group boundary and the Domain fingerprint after the
    /// command. It is independent of the mutable history payload and lets recovery prove that
    /// commandVersion/ordinal grouping and each group's SourceFingerprint were not rewritten.
    /// </summary>
    public sealed class TableCommandBinding
    {
        public TableCommandBinding(ulong commandVersion, int eventCount, ulong sourceFingerprint)
        {
            if (commandVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandVersion), "Command version starts at 1.");
            }

            if (eventCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(eventCount), "A committed command must emit at least one event.");
            }

            if (sourceFingerprint == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceFingerprint), "Source fingerprint must be non-zero.");
            }

            CommandVersion = commandVersion;
            EventCount = eventCount;
            SourceFingerprint = sourceFingerprint;
        }

        public ulong CommandVersion { get; }

        public int EventCount { get; }

        public ulong SourceFingerprint { get; }
    }

    public static class TableCommandBindingsCodec
    {
        public const int FormatVersion = 1;
        private const int Magic = 0x4D4A4342; // MJCB
        private const int MaxCommands = 1_000_000;

        public static byte[] Encode(IReadOnlyList<TableCommandBinding> bindings)
        {
            if (bindings == null)
            {
                throw new ArgumentNullException(nameof(bindings));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(bindings.Count);
                for (int i = 0; i < bindings.Count; i++)
                {
                    TableCommandBinding binding = bindings[i];
                    if (binding == null)
                    {
                        throw new ArgumentException("Command bindings contain null.", nameof(bindings));
                    }

                    writer.Write(binding.CommandVersion);
                    writer.Write(binding.EventCount);
                    writer.Write(binding.SourceFingerprint);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static IReadOnlyList<TableCommandBinding> Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Command binding payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Command binding magic is invalid.");
                }

                if (reader.ReadInt32() != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported command binding schema.");
                }

                int count = reader.ReadInt32();
                if (count < 0 || count > MaxCommands)
                {
                    throw new InvalidDataException("Command binding count is invalid.");
                }

                var bindings = new List<TableCommandBinding>(count);
                for (int i = 0; i < count; i++)
                {
                    ulong version = reader.ReadUInt64();
                    int eventCount = reader.ReadInt32();
                    ulong fingerprint = reader.ReadUInt64();
                    if (version != (ulong)(i + 1))
                    {
                        throw new InvalidDataException("Command binding versions must be contiguous from 1.");
                    }

                    bindings.Add(new TableCommandBinding(version, eventCount, fingerprint));
                }

                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Command binding payload has trailing bytes.");
                }

                return bindings;
            }
        }

        public static IReadOnlyList<TableCommandBinding> Build(IReadOnlyList<TableEventRecord> history)
        {
            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            var bindings = new List<TableCommandBinding>();
            int index = 0;
            ulong version = 1;
            while (index < history.Count)
            {
                TableEventRecord first = history[index];
                if (first.CommandVersion.Value != version)
                {
                    throw new InvalidOperationException(
                        "Committed history command versions must be contiguous from 1.");
                }

                ulong fingerprint = 0;
                int count = 0;
                while (index < history.Count && history[index].CommandVersion.Value == version)
                {
                    TableEventRecord record = history[index];
                    if (record.Ordinal != count)
                    {
                        throw new InvalidOperationException(
                            "Committed history ordinals must be contiguous within a command.");
                    }

                    if (record.Payload == null)
                    {
                        throw new InvalidOperationException(
                            "Committed history event lacks its fact binding payload.");
                    }

                    TableEventFact fact = TableEventFactCodec.Decode(record.Payload);
                    if (fact.CommandVersion != record.CommandVersion || fact.Ordinal != record.Ordinal)
                    {
                        throw new InvalidOperationException(
                            "Committed history payload does not bind its command/ordinal.");
                    }

                    if (fingerprint == 0)
                    {
                        fingerprint = fact.SourceFingerprint;
                    }
                    else if (fingerprint != fact.SourceFingerprint)
                    {
                        throw new InvalidOperationException(
                            "Committed events in one command disagree on SourceFingerprint.");
                    }

                    count++;
                    index++;
                }

                bindings.Add(new TableCommandBinding(version, count, fingerprint));
                version++;
            }

            return bindings;
        }
    }
}
