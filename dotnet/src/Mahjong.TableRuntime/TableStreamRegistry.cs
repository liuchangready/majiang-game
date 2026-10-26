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
        private readonly Action? _beforeDispatchClaim;
        private readonly int _maxFrames;
        private readonly int _maxBytes;
        private readonly bool _enforceBytes;
        private readonly bool _observePumpExit;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        private int _queuedBytes;
        private bool _pumping;
        private bool _closed;
        private bool _terminationSent;
        private int _cancelScheduled;
        private long _allowedGeneration;
        private TableStreamTermination? _termination;
        private TaskCompletionSource<bool>? _pumpExit;

        public TableStreamChannel(
            TableStreamHandle handle,
            ITableStreamSink sink,
            int maxFrames,
            int maxBytes,
            bool enforceBytes,
            ITableStreamTerminationSink? terminationSink,
            long allowedGeneration,
            Action? beforeDispatchClaim = null,
            bool observePumpExit = false)
        {
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _maxFrames = maxFrames;
            _maxBytes = maxBytes;
            _enforceBytes = enforceBytes;
            _terminationSink = terminationSink;
            _allowedGeneration = allowedGeneration;
            _beforeDispatchClaim = beforeDispatchClaim;
            _observePumpExit = observePumpExit;
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

        /// <summary>
        /// Test-observable completion for the pump invocation that is currently running. It is
        /// completed in a finally block after the pump has passed its last dispatch claim and is
        /// about to exit. Production never allocates this state unless a test enables it.
        /// </summary>
        internal Task PumpExit
        {
            get
            {
                lock (_gate)
                {
                    return _pumpExit?.Task ?? Task.CompletedTask;
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

                if (frame.RecoveryGeneration != _allowedGeneration)
                {
                    // A queued old-generation frame must never enter a new recovery generation.
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
                        if (_observePumpExit)
                        {
                            _pumpExit = new TaskCompletionSource<bool>(
                                TaskCreationOptions.RunContinuationsAsynchronously);
                        }

                        start = true;
                    }

                    status = TableStreamEnqueueStatus.Enqueued;
                }
            }

            if (termination != null)
            {
                ScheduleCancellation();
                Notify(termination);
            }

            if (start)
            {
                _ = Task.Run(PumpAsync);
            }

            return status;
        }

        /// <summary>
        /// Drops every queued frame from an earlier recovery generation and fences the pump.
        /// A sink call that already started cannot be recalled, but no old queued frame may
        /// reach the sink after this fence. This method is intentionally non-blocking and does
        /// not cancel under the actor thread.
        /// </summary>
        public void PurgeAndFence(long generation)
        {
            lock (_gate)
            {
                _allowedGeneration = generation;
                _queue.Clear();
                _queuedBytes = 0;
            }
        }

        /// <summary>Stops future delivery and drops queued frames. The in-flight sink call is cancelled off-actor.</summary>
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

            ScheduleCancellation();
            Notify(termination);
        }

        private TableStreamTermination CloseLocked(TableStreamTerminationKind kind, string reasonCode, string detail)
        {
            _closed = true;
            _queue.Clear();
            _queuedBytes = 0;
            _termination = new TableStreamTermination(kind, _handle, reasonCode, detail);
            return _termination;
        }

        private void ScheduleCancellation()
        {
            if (Interlocked.CompareExchange(ref _cancelScheduled, 1, 0) != 0)
            {
                return;
            }

            // Cancel() executes user cancellation callbacks synchronously. Never do that on the
            // actor consumer or while holding the channel lock: a blocking/reentrant callback
            // would otherwise stall the table mailbox.
            _ = Task.Run(() =>
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (Exception)
                {
                    // A cancellation callback is downstream code. Its failure/throwing cannot
                    // fail the actor or duplicate termination.
                }
            });
        }

        private async Task PumpAsync()
        {
            TaskCompletionSource<bool>? pumpExit = null;
            if (_observePumpExit)
            {
                lock (_gate)
                {
                    pumpExit = _pumpExit;
                }
            }

            try
            {
                await PumpLoopAsync().ConfigureAwait(false);
            }
            finally
            {
                // Observability only: the pump has passed its last TryClaimDispatch and is
                // leaving. This does not alter close/fence claims, sink calls or exception flow.
                pumpExit?.TrySetResult(true);
            }
        }

        private async Task PumpLoopAsync()
        {
            while (true)
            {
                TableStreamFrame frame;
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

                _beforeDispatchClaim?.Invoke();

                DispatchClaimStatus claim = TryClaimDispatch(frame);
                if (claim == DispatchClaimStatus.Closed)
                {
                    lock (_gate)
                    {
                        _pumping = false;
                    }

                    return;
                }

                if (claim == DispatchClaimStatus.StaleGeneration)
                {
                    // The fence/close won the linearization race before this dispatch was
                    // claimed. Do not consult the old sink; the next frame is the current
                    // generation's full baseline.
                    continue;
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
                    TableStreamTermination? termination = null;
                    bool stop = false;
                    lock (_gate)
                    {
                        if (_closed)
                        {
                            _pumping = false;
                            stop = true;
                        }
                        else if (frame.RecoveryGeneration != _allowedGeneration)
                        {
                            // A completion from an atomically claimed old generation is stale.
                            // Its failure/cancellation must not close or clear the current
                            // generation's outbox (which may already hold the new baseline).
                        }
                        else
                        {
                            termination = CloseLocked(
                                TableStreamTerminationKind.ActorFaulted,
                                ProtocolErrorCode.InternalError,
                                exception.GetType().Name + ": " + exception.Message);
                            stop = true;
                        }
                    }

                    if (termination != null)
                    {
                        ScheduleCancellation();
                        Notify(termination);
                    }

                    if (stop)
                    {
                        return;
                    }

                    continue;
                }
            }
        }

        /// <summary>
        /// Linearization point for dispatch versus recovery/close. The check and the claim are
        /// one lock operation against the current fence. A fence before this call drops the
        /// frame; a fence after it sees one already-claimed in-flight sink call, which cannot
        /// be recalled without waiting for downstream code on the actor thread.
        /// </summary>
        private DispatchClaimStatus TryClaimDispatch(TableStreamFrame frame)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return DispatchClaimStatus.Closed;
                }

                return frame.RecoveryGeneration == _allowedGeneration
                    ? DispatchClaimStatus.Claimed
                    : DispatchClaimStatus.StaleGeneration;
            }
        }

        private enum DispatchClaimStatus : byte
        {
            Claimed = 0,
            StaleGeneration = 1,
            Closed = 2
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

            // The termination observer is downstream code. Dispatch it off the actor thread and
            // off the channel lock; a blocking/reentrant observer can neither stall the table
            // mailbox nor duplicate the one-shot termination notification.
            _ = Task.Run(() =>
            {
                try
                {
                    _terminationSink.OnStreamTerminated(termination);
                }
                catch (Exception)
                {
                    // A diagnostic observer cannot re-enter or fail the table actor.
                }
            });
        }
    }
}
