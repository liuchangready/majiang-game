using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;

namespace Mahjong.Unity.TableModel.Tests
{
    internal sealed class ManualTableClock : ITableClientClock
    {
        private long _milliseconds;
        private DateTimeOffset _utc = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

        public long MonotonicMilliseconds => _milliseconds;

        public DateTimeOffset UtcNow => _utc;

        public void Advance(long milliseconds)
        {
            if (milliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(milliseconds));
            }

            _milliseconds += milliseconds;
            _utc = _utc.AddMilliseconds(milliseconds);
        }
    }

    internal sealed class SentEnvelopeRecord
    {
        public SentEnvelopeRecord(Envelope envelope, byte[] bytes)
        {
            Envelope = envelope;
            Bytes = bytes;
        }

        public Envelope Envelope { get; }

        public byte[] Bytes { get; }
    }

    internal sealed class ScriptedTableChannel : ITableChannel, IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<SentEnvelopeRecord> _sent = new List<SentEnvelopeRecord>();
        private Channel<byte[]> _inbound = NewChannel();
        private bool _open;

        public bool AutoHandshake { get; set; } = true;

        public Func<Envelope, bool>? OnSend { get; set; }

        public bool IsOpen
        {
            get
            {
                lock (_gate)
                {
                    return _open;
                }
            }
        }

        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            Channel<byte[]> previous;
            lock (_gate)
            {
                previous = _inbound;
                _inbound = NewChannel();
                _open = true;
            }

            previous.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] copy = wireEnvelope.ToArray();
            Envelope envelope = P43TestData.Decode(copy);
            Channel<byte[]> inbound;
            lock (_gate)
            {
                if (!_open)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
                }

                _sent.Add(new SentEnvelopeRecord(envelope, copy));
                inbound = _inbound;
            }

            OnSend?.Invoke(envelope);

            if (!AutoHandshake)
            {
                return Task.CompletedTask;
            }

            if (envelope.BodyCase == Envelope.BodyOneofCase.Hello)
            {
                inbound.Writer.TryWrite(P43TestData.Encode(P43TestData.Welcome()));
            }
            else if (envelope.BodyCase == Envelope.BodyOneofCase.SyncRequest)
            {
                inbound.Writer.TryWrite(P43TestData.Encode(P43TestData.SyncResponse(envelope.CorrelationId)));
            }

            return Task.CompletedTask;
        }

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            Channel<byte[]> inbound;
            lock (_gate)
            {
                inbound = _inbound;
            }

            while (true)
            {
                bool available = await inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                if (!available)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
                }

                if (inbound.Reader.TryRead(out byte[]? frame) && frame != null)
                {
                    return frame;
                }
            }
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            Channel<byte[]> inbound;
            lock (_gate)
            {
                _open = false;
                inbound = _inbound;
            }

            inbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public IReadOnlyList<SentEnvelopeRecord> Sent
        {
            get
            {
                lock (_gate)
                {
                    return _sent.ToArray();
                }
            }
        }

        public int CountSent(MessageType type)
        {
            int count = 0;
            IReadOnlyList<SentEnvelopeRecord> sent = Sent;
            for (int i = 0; i < sent.Count; i++)
            {
                if (sent[i].Envelope.MessageType == type)
                {
                    count++;
                }
            }

            return count;
        }

        public Envelope? LastCommand(string requestId)
        {
            IReadOnlyList<SentEnvelopeRecord> sent = Sent;
            for (int i = sent.Count - 1; i >= 0; i--)
            {
                if (sent[i].Envelope.BodyCase == Envelope.BodyOneofCase.Command &&
                    string.Equals(sent[i].Envelope.RequestId, requestId, StringComparison.Ordinal))
                {
                    return sent[i].Envelope;
                }
            }

            return null;
        }

        public void Dispose()
        {
            Channel<byte[]> inbound;
            lock (_gate)
            {
                _open = false;
                inbound = _inbound;
            }

            inbound.Writer.TryComplete();
        }

        private static Channel<byte[]> NewChannel()
        {
            return Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }
    }
}
