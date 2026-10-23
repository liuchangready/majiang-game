using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;

namespace Mahjong.Client.Wss.Tests
{
    internal sealed class SentRecord
    {
        public SentRecord(int connection, byte[] bytes)
        {
            Connection = connection;
            Bytes = bytes;
        }

        public int Connection { get; }

        public byte[] Bytes { get; }

        public Envelope Envelope => TestWire.Decode(Bytes);
    }

    /// <summary>
    /// Deterministic session/runner counterpart. It performs the minimum
    /// server-side handshake (WELCOME on HELLO, correlated SYNC_RESPONSE on
    /// SYNC_REQUEST) so the existing TableSession state machine can be driven
    /// without a real socket.
    /// </summary>
    internal sealed class ScriptedTableChannel : ITableChannel, IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<SentRecord> _sent = new List<SentRecord>();
        private readonly List<string> _tickets = new List<string>();

        private Channel<InboundItem> _inbound = NewChannel();
        private bool _open;
        private int _openCount;

        public bool AutoHandshake { get; set; } = true;

        public bool FailNextOpen { get; set; }

        public bool FailAllOpens { get; set; }

        public bool OpenFailurePermanent { get; set; }

        public string OpenFailureCode { get; set; } = "TEST_OPEN_FAILED";

        public Func<int, Envelope> WelcomeFactory { get; set; } =
            index => TestWire.Welcome(bindingEpoch: 10 + index);

        public Func<int, string, Envelope> SyncFactory { get; set; } =
            (index, correlation) => TestWire.PlayerSync(bindingEpoch: 10 + index, correlationId: correlation);

        public int OpenCount
        {
            get
            {
                lock (_gate)
                {
                    return _openCount;
                }
            }
        }

        public IReadOnlyList<string> Tickets
        {
            get
            {
                lock (_gate)
                {
                    return _tickets.ToArray();
                }
            }
        }

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

            Channel<InboundItem> previous;
            int index;
            lock (_gate)
            {
                index = ++_openCount;
                _tickets.Add(request.Ticket);
                if (FailAllOpens || FailNextOpen)
                {
                    FailNextOpen = false;
                    throw new TableChannelClosedException(OpenFailureCode, OpenFailurePermanent);
                }

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
            Channel<InboundItem> channel;
            int index;
            Envelope envelope = TestWire.Decode(wireEnvelope);
            byte[] copy = wireEnvelope.ToArray();
            lock (_gate)
            {
                if (!_open)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
                }

                index = _openCount;
                _sent.Add(new SentRecord(index, copy));
                channel = _inbound;
            }

            if (AutoHandshake)
            {
                if (envelope.BodyCase == Envelope.BodyOneofCase.Hello)
                {
                    channel.Writer.TryWrite(InboundItem.FromEnvelope(WelcomeFactory(index)));
                }
                else if (envelope.BodyCase == Envelope.BodyOneofCase.SyncRequest)
                {
                    channel.Writer.TryWrite(InboundItem.FromEnvelope(SyncFactory(index, envelope.CorrelationId)));
                }
            }

            return Task.CompletedTask;
        }

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                channel = _inbound;
            }

            while (true)
            {
                bool available;
                try
                {
                    available = await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                if (!available)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
                }

                if (!channel.Reader.TryRead(out InboundItem? item) || item == null)
                {
                    continue;
                }

                if (item.Failure != null)
                {
                    throw item.Failure;
                }

                return item.Bytes!;
            }
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                _open = false;
                channel = _inbound;
            }

            channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public void TriggerTransientFailure(string code = WebSocketChannelReason.TransportLost)
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                channel = _inbound;
            }

            channel.Writer.TryWrite(InboundItem.FromFailure(new TableChannelClosedException(code, false)));
        }

        public void TriggerPermanentFailure(string code = ProtocolErrorCode.ProtocolViolation)
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                channel = _inbound;
            }

            channel.Writer.TryWrite(InboundItem.FromFailure(new TableChannelClosedException(code, true)));
        }

        public void Enqueue(Envelope envelope)
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                channel = _inbound;
            }

            channel.Writer.TryWrite(InboundItem.FromEnvelope(envelope));
        }

        public void EnqueueRaw(byte[] bytes)
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                channel = _inbound;
            }

            channel.Writer.TryWrite(InboundItem.FromBytes(bytes));
        }

        public IReadOnlyList<SentRecord> SentRecords
        {
            get
            {
                lock (_gate)
                {
                    return _sent.ToArray();
                }
            }
        }

        public IReadOnlyList<Envelope> SentEnvelopes()
        {
            return SentRecords.Select(record => record.Envelope).ToArray();
        }

        public int CountSent(MessageType type)
        {
            return SentEnvelopes().Count(envelope => envelope.MessageType == type);
        }

        public async Task WaitForSentAsync(MessageType type, int count, TimeSpan timeout)
        {
            await TestWire.WaitUntilAsync(
                () => CountSent(type) >= count,
                timeout,
                count + " " + type + " envelope(s)").ConfigureAwait(false);
        }

        public void Dispose()
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                _open = false;
                channel = _inbound;
            }

            channel.Writer.TryComplete();
        }

        private static Channel<InboundItem> NewChannel()
        {
            return Channel.CreateUnbounded<InboundItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        private sealed class InboundItem
        {
            private InboundItem(byte[]? bytes, TableChannelClosedException? failure)
            {
                Bytes = bytes;
                Failure = failure;
            }

            public byte[]? Bytes { get; }

            public TableChannelClosedException? Failure { get; }

            public static InboundItem FromEnvelope(Envelope envelope)
            {
                return new InboundItem(TestWire.Encode(envelope), null);
            }

            public static InboundItem FromBytes(byte[] bytes)
            {
                return new InboundItem(bytes, null);
            }

            public static InboundItem FromFailure(TableChannelClosedException failure)
            {
                return new InboundItem(null, failure);
            }
        }
    }

    /// <summary>Minimal in-memory channel used for heartbeat control tests.</summary>
    internal sealed class FakeInnerChannel : ITableChannel, IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<byte[]> _sent = new List<byte[]>();
        private Channel<InboundItem> _inbound = NewChannel();
        private bool _open;

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

        public TableChannelCloseReason? LastCloseReason { get; private set; }

        public int CloseCount { get; private set; }

        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Channel<InboundItem> previous;
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
            lock (_gate)
            {
                if (!_open)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
                }

                _sent.Add(wireEnvelope.ToArray());
            }

            return Task.CompletedTask;
        }

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                channel = _inbound;
            }

            while (true)
            {
                bool available = await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                if (!available)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
                }

                if (channel.Reader.TryRead(out InboundItem? item) && item != null)
                {
                    if (item.Failure != null)
                    {
                        throw item.Failure;
                    }

                    return item.Bytes!;
                }
            }
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                _open = false;
                LastCloseReason = reason;
                CloseCount++;
                channel = _inbound;
            }

            channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public void Enqueue(Envelope envelope)
        {
            EnqueueRaw(TestWire.Encode(envelope));
        }

        public void EnqueueRaw(byte[] bytes)
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                channel = _inbound;
            }

            channel.Writer.TryWrite(InboundItem.FromBytes(bytes));
        }

        public IReadOnlyList<Envelope> SentEnvelopes()
        {
            lock (_gate)
            {
                return _sent.Select(bytes => TestWire.Decode(bytes)).ToArray();
            }
        }

        public void Dispose()
        {
            Channel<InboundItem> channel;
            lock (_gate)
            {
                _open = false;
                channel = _inbound;
            }

            channel.Writer.TryComplete();
        }

        private static Channel<InboundItem> NewChannel()
        {
            return Channel.CreateUnbounded<InboundItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        private sealed class InboundItem
        {
            private InboundItem(byte[]? bytes, TableChannelClosedException? failure)
            {
                Bytes = bytes;
                Failure = failure;
            }

            public byte[]? Bytes { get; }

            public TableChannelClosedException? Failure { get; }

            public static InboundItem FromBytes(byte[] bytes)
            {
                return new InboundItem(bytes, null);
            }

            public static InboundItem FromFailure(TableChannelClosedException failure)
            {
                return new InboundItem(null, failure);
            }
        }
    }

    internal sealed class ManualTableClock : ITableClientClock
    {
        private long _milliseconds;
        private DateTimeOffset _utc = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);

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
}
