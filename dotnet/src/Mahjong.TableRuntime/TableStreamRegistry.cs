using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;

namespace Mahjong.TableRuntime
{
    internal enum TableStreamEnqueueStatus : byte
    {
        Enqueued = 0,
        Closed = 1,
        Overflow = 2
    }

    /// <summary>
    /// Per-stream identity high-water marks. ProjectionSeq and StreamVersion belong to the
    /// logical (session, audience) identity, not to the table event sequence or the control
    /// version. A new sync epoch resets the projection sequence; a rebind in the same sync
    /// epoch continues it so an old frame can never look newer than the current baseline.
    /// </summary>
    internal sealed class TableStreamIdentityState
    {
        public TableStreamIdentityState(TableStreamIdentity identity)
        {
            Identity = identity;
        }

        public TableStreamIdentity Identity { get; }

        public ulong HighestBindingEpoch { get; set; }

        public ulong HighestSyncEpoch { get; set; }

        public ProjectionSeq ProjectionSeq { get; set; } = new ProjectionSeq(0);

        public StreamCursor Cursor { get; set; } = StreamCursor.Empty;

        public ulong? CurrentHandleToken { get; set; }
    }

    /// <summary>
    /// One current subscription. It is created and removed only on the actor consumer; the
    /// channel it owns is the only object touched by the delivery pump.
    /// </summary>
    internal sealed class TableStreamRegistration
    {
        public TableStreamRegistration(
            TableStreamHandle handle,
            TableStreamIdentityState state,
            TableStreamChannel channel,
            OwnerEpoch ownerEpoch,
            ControlEpoch? boundControlEpoch,
            long recoveryGeneration,
            string roundId,
            long controlVersion)
        {
            Handle = handle;
            State = state;
            Channel = channel;
            OwnerEpoch = ownerEpoch;
            BoundControlEpoch = boundControlEpoch;
            RecoveryGeneration = recoveryGeneration;
            RoundId = roundId;
            ControlVersion = controlVersion;
        }

        public TableStreamHandle Handle { get; }

        public TableStreamIdentity Identity => Handle.Identity;

        public TableStreamIdentityState State { get; }

        public TableStreamChannel Channel { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public ControlEpoch? BoundControlEpoch { get; }

        public long RecoveryGeneration { get; set; }

        public string RoundId { get; set; }

        /// <summary>Control version represented by the last emitted baseline/refresh.</summary>
        public long ControlVersion { get; set; }

        /// <summary>
        /// True after a recovery baseline until the host explicitly syncs to it. Incremental
        /// frames are withheld so a recovered stream cannot silently skip the rebuilt baseline.
        /// </summary>
        public bool NeedsSync { get; set; }
    }

    /// <summary>
    /// Bounded FIFO outbox for one stream plus its single delivery loop. The channel never calls
    /// the actor and never blocks it: the actor only enqueues. A slow/unresponsive sink can fill
    /// this queue, after which the channel is explicitly faulted and every later frame is
    /// refused. A fault is observable through <see cref="ITableStreamTerminationSink"/> and a
    /// later Sync returns Closed.
    /// </summary>
    internal sealed class TableStreamChannel
    {
        private readonly object _gate = new object();
        private readonly Queue<TableStreamFrame> _queue = new Queue<TableStreamFrame>();
        private readonly TableStreamHandle _handle;
        private readonly ITableStreamSink _sink;
        private readonly ITableStreamTerminationSink? _terminationSink;
        private readonly int _maxFrames;
        private readonly int _maxBytes;
        private readonly bool _enforceBytes;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        private int _queuedBytes;
        private bool _pumping;
        private bool _closed;
        private bool _terminationSent;
        private TableStreamTermination? _termination;

        public TableStreamChannel(
            TableStreamHandle handle,
            ITableStreamSink sink,
            int maxFrames,
            int maxBytes,
            bool enforceBytes,
            ITableStreamTerminationSink? terminationSink)
        {
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _maxFrames = maxFrames;
            _maxBytes = maxBytes;
            _enforceBytes = enforceBytes;
            _terminationSink = terminationSink;
        }

        public bool IsClosed
        {
            get
            {
                lock (_gate)
                {
                    return _closed;
                }
            }
        }

        public TableStreamTermination? Termination
        {
            get
            {
                lock (_gate)
                {
                    return _termination;
                }
            }
        }

        public TableStreamEnqueueStatus TryEnqueue(TableStreamFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            TableStreamTermination? termination = null;
            bool start = false;
            TableStreamEnqueueStatus status;
            lock (_gate)
            {
                if (_closed)
                {
                    return TableStreamEnqueueStatus.Closed;
                }

                int size = frame.PayloadSize;
                if (_enforceBytes && size > _maxBytes)
                {
                    termination = CloseLocked(
                        TableStreamTerminationKind.MessageTooLarge,
                        ProtocolErrorCode.MessageTooLarge,
                        "Encoded frame is " + size + " bytes, above the " + _maxBytes + " byte stream limit.");
                    status = TableStreamEnqueueStatus.Overflow;
                }
                else if (_queue.Count >= _maxFrames ||
                         (_enforceBytes && _queuedBytes + size > _maxBytes))
                {
                    termination = CloseLocked(
                        TableStreamTerminationKind.SlowConsumer,
                        ProtocolErrorCode.SlowConsumer,
                        "The bounded stream queue overflowed; the slow consumer was disconnected.");
                    status = TableStreamEnqueueStatus.Overflow;
                }
                else
                {
                    _queue.Enqueue(frame);
                    _queuedBytes += size;
                    if (!_pumping)
                    {
                        _pumping = true;
                        start = true;
                    }

                    status = TableStreamEnqueueStatus.Enqueued;
                }
            }

            if (termination != null)
            {
                Notify(termination);
            }

            if (start)
            {
                _ = Task.Run(PumpAsync);
            }

            return status;
        }

        /// <summary>Stops future delivery and drops queued frames. The in-flight sink call is cancelled but never awaited by the actor.</summary>
        public void Close(TableStreamTerminationKind kind, string reasonCode, string detail)
        {
            TableStreamTermination? termination;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                termination = CloseLocked(kind, reasonCode, detail);
            }

            Notify(termination);
        }

        private TableStreamTermination CloseLocked(TableStreamTerminationKind kind, string reasonCode, string detail)
        {
            _closed = true;
            _queue.Clear();
            _queuedBytes = 0;
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The pump already observed the close; there is nothing left to cancel.
            }

            _termination = new TableStreamTermination(kind, _handle, reasonCode, detail);
            return _termination;
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                TableStreamFrame? frame = null;
                lock (_gate)
                {
                    if (_closed || _queue.Count == 0)
                    {
                        _pumping = false;
                        return;
                    }

                    frame = _queue.Dequeue();
                    _queuedBytes -= frame.PayloadSize;
                }

                try
                {
                    await _sink.PublishAsync(frame, _cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _pumping = false;
                    }

                    return;
                }
                catch (Exception exception)
                {
                    TableStreamTermination? termination;
                    lock (_gate)
                    {
                        if (_closed)
                        {
                            _pumping = false;
                            return;
                        }

                        termination = CloseLocked(
                            TableStreamTerminationKind.ActorFaulted,
                            ProtocolErrorCode.InternalError,
                            exception.GetType().Name + ": " + exception.Message);
                    }

                    Notify(termination);
                    return;
                }
            }
        }

        private void Notify(TableStreamTermination termination)
        {
            if (_terminationSink == null)
            {
                return;
            }

            bool send;
            lock (_gate)
            {
                send = !_terminationSent;
                _terminationSent = true;
            }

            if (!send)
            {
                return;
            }

            try
            {
                _terminationSink.OnStreamTerminated(termination);
            }
            catch (Exception)
            {
                // A diagnostic observer cannot re-enter or fail the table actor.
            }
        }
    }
}
