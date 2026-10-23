using System;
using System.Collections.Generic;
using Mahjong.Contracts.Wire;

namespace Mahjong.Client
{
    /// <summary>
    /// Bounded pre-baseline PUSH buffer. It stores whole decoded envelopes plus
    /// their encoded size so one oversized push cannot silently consume the
    /// byte budget. On overflow the session clears the buffer and requests a
    /// fresh full baseline; buffered pushes are never applied speculatively.
    /// </summary>
    internal sealed class EarlyPushBuffer
    {
        private readonly List<Entry> _entries = new List<Entry>();

        public EarlyPushBuffer(int maxItems, long maxBytes)
        {
            if (maxItems <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxItems));
            }

            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            MaxItems = maxItems;
            MaxBytes = maxBytes;
        }

        public int MaxItems { get; }

        public long MaxBytes { get; }

        public int Count => _entries.Count;

        public long ByteCount { get; private set; }

        public bool TryAdd(Envelope envelope, int encodedSize)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (encodedSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(encodedSize));
            }

            if (_entries.Count >= MaxItems || ByteCount + encodedSize > MaxBytes)
            {
                return false;
            }

            _entries.Add(new Entry(envelope, encodedSize));
            ByteCount += encodedSize;
            return true;
        }

        public IReadOnlyList<Entry> Items => _entries;

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _entries.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            ByteCount -= _entries[index].EncodedSize;
            _entries.RemoveAt(index);
        }

        public void Clear()
        {
            _entries.Clear();
            ByteCount = 0;
        }

        internal readonly struct Entry
        {
            public Entry(Envelope envelope, int encodedSize)
            {
                Envelope = envelope;
                EncodedSize = encodedSize;
            }

            public Envelope Envelope { get; }

            public int EncodedSize { get; }
        }
    }
}
