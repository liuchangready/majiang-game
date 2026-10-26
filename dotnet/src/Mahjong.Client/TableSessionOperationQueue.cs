using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using ContractCursor = Mahjong.Contracts.StreamCursor;

namespace Mahjong.Client
{
    /// <summary>
    /// Kind of owner operation queued by <see cref="TableSessionOperationQueue"/>.
    /// </summary>
    public enum TableSessionOperationKind
    {
        Open = 0,
        Pump = 1,
        ApplyFrame = 2,
        Submit = 3,
        Reconcile = 4,
        Retry = 5,
        Disconnect = 6,
        Close = 7,
        Capture = 8
    }

    /// <summary>
    /// Observable lifecycle of one owner operation.
    ///
    /// Queued -> Running -> Completed | Faulted, or Queued -> Canceled.
    /// A pre-cancelled call never becomes Queued (its Sequence is -1). Once an
    /// operation is Running it is never interleaved with another owner
    /// operation; cancellation only takes effect wherever the session method
    /// honours its token.
    /// </summary>
    public enum TableSessionOperationState
    {
        Queued = 0,
        Running = 1,
        Completed = 2,
        Canceled = 3,
        Faulted = 4
    }

    /// <summary>
    /// Optional split pump ingress implemented by <see cref="TableSession"/>.
    ///
    /// <see cref="ReceiveFrameAsync"/> performs the transport wait only and
    /// never mutates session state. <see cref="ApplyFrameAsync"/> is the
    /// state-mutating half and must be called from the single owner context
    /// (the operation queue). <see cref="ApplyReceiveFaultAsync"/> routes a
    /// transport fault onto the same owner. A session that does not implement
    /// this interface is still usable through the queue, but its combined
    /// PumpOnceAsync would have to hold the owner during ReceiveAsync.
    /// </summary>
    public interface ITableSessionFrameIngress
    {
        Task<ReadOnlyMemory<byte>> ReceiveFrameAsync(CancellationToken cancellationToken);

        Task ApplyFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

        Task ApplyReceiveFaultAsync(Exception exception, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Handle for a queued owner operation. Completion carries the operation's
    /// result (or its cancellation/fault); State is safe to poll from any thread.
    /// </summary>
    public class TableSessionOperation
    {
        private readonly TaskCompletionSource<bool> _completion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _stateGate = new object();
        private TableSessionOperationState _state = TableSessionOperationState.Queued;

        internal TableSessionOperation(TableSessionOperationKind kind, CancellationToken token)
        {
            Kind = kind;
            Token = token;
            Sequence = -1;
        }

        /// <summary>Monotonic queue position; -1 when the call was pre-cancelled or rejected.</summary>
        public long Sequence { get; internal set; }

        public TableSessionOperationKind Kind { get; }

        public TableSessionOperationState State
        {
            get
            {
                lock (_stateGate)
                {
                    return _state;
                }
            }
        }

        public Task Completion => _completion.Task;

        internal CancellationToken Token { get; }

        internal bool IsTerminal
        {
            get
            {
                lock (_stateGate)
                {
                    return IsTerminalNoLock();
                }
            }
        }

        internal void MarkRunning()
        {
            lock (_stateGate)
            {
                if (_state == TableSessionOperationState.Queued)
                {
                    _state = TableSessionOperationState.Running;
                }
            }
        }

        internal virtual bool Complete()
        {
            if (!TryMarkCompleted())
            {
                return false;
            }

            _completion.TrySetResult(true);
            return true;
        }

        internal virtual bool Cancel(CancellationToken cancellationToken)
        {
            if (!TryMarkCanceled())
            {
                return false;
            }

            _completion.TrySetCanceled(cancellationToken);
            return true;
        }

        internal virtual bool Fail(Exception exception)
        {
            if (!TryMarkFaulted())
            {
                return false;
            }

            _completion.TrySetException(exception);
            return true;
        }

        internal virtual bool CancelIfQueued(CancellationToken cancellationToken)
        {
            if (!TryMarkQueuedCanceled())
            {
                return false;
            }

            _completion.TrySetCanceled(cancellationToken);
            return true;
        }

        protected bool TryMarkCompleted()
        {
            lock (_stateGate)
            {
                if (_state != TableSessionOperationState.Queued &&
                    _state != TableSessionOperationState.Running)
                {
                    return false;
                }

                _state = TableSessionOperationState.Completed;
                return true;
            }
        }

        protected bool TryMarkCanceled()
        {
            lock (_stateGate)
            {
                if (_state != TableSessionOperationState.Queued &&
                    _state != TableSessionOperationState.Running)
                {
                    return false;
                }

                _state = TableSessionOperationState.Canceled;
                return true;
            }
        }

        protected bool TryMarkFaulted()
        {
            lock (_stateGate)
            {
                if (_state != TableSessionOperationState.Queued &&
                    _state != TableSessionOperationState.Running)
                {
                    return false;
                }

                _state = TableSessionOperationState.Faulted;
                return true;
            }
        }

        protected bool TryMarkQueuedCanceled()
        {
            lock (_stateGate)
            {
                if (_state != TableSessionOperationState.Queued)
                {
                    return false;
                }

                _state = TableSessionOperationState.Canceled;
                return true;
            }
        }

        private bool IsTerminalNoLock()
        {
            return _state == TableSessionOperationState.Completed ||
                   _state == TableSessionOperationState.Canceled ||
                   _state == TableSessionOperationState.Faulted;
        }
    }

    /// <summary>Typed handle for an operation that returns a value.</summary>
    public sealed class TableSessionOperation<T> : TableSessionOperation
    {
        private readonly TaskCompletionSource<T> _typedCompletion =
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TableSessionOperation(TableSessionOperationKind kind, CancellationToken token)
            : base(kind, token)
        {
        }

        public new Task<T> Completion => _typedCompletion.Task;

        internal void Complete(T value)
        {
            if (base.Complete())
            {
                _typedCompletion.TrySetResult(value);
            }
        }

        internal override bool Cancel(CancellationToken cancellationToken)
        {
            if (!base.Cancel(cancellationToken))
            {
                return false;
            }

            _typedCompletion.TrySetCanceled(cancellationToken);
            return true;
        }

        internal override bool Fail(Exception exception)
        {
            if (!base.Fail(exception))
            {
                return false;
            }

            _typedCompletion.TrySetException(exception);
            return true;
        }

        internal override bool CancelIfQueued(CancellationToken cancellationToken)
        {
            if (!base.CancelIfQueued(cancellationToken))
            {
                return false;
            }

            _typedCompletion.TrySetCanceled(cancellationToken);
            return true;
        }
    }

    /// <summary>
    /// Immutable owner-captured view. Pending commands are copied snapshots and
    /// never expose the live session dictionary. Snapshot messages are
    /// intentionally excluded; the platform remains responsible for cloning
    /// Wire messages when it builds its own UI state.
    /// </summary>
    public sealed class TableSessionQueueSnapshot
    {
        public TableSessionQueueSnapshot(
            TableConnectionState state,
            TableSessionBinding? binding,
            ContractCursor? confirmedCursor,
            IReadOnlyList<PendingCommandSnapshot> pendingCommands,
            string? lastError,
            long earlyBufferOverflowCount)
        {
            State = state;
            Binding = binding;
            ConfirmedCursor = confirmedCursor;
            PendingCommands = pendingCommands ?? throw new ArgumentNullException(nameof(pendingCommands));
            LastError = lastError;
            EarlyBufferOverflowCount = earlyBufferOverflowCount;
        }

        public TableConnectionState State { get; }

        public TableSessionBinding? Binding { get; }

        public ContractCursor? ConfirmedCursor { get; }

        public IReadOnlyList<PendingCommandSnapshot> PendingCommands { get; }

        public string? LastError { get; }

        public long EarlyBufferOverflowCount { get; }
    }

    /// <summary>
    /// Single-owner operation queue for an <see cref="IGameSession"/>.
    ///
    /// The queue serializes Open, inbound frame application, Submit, Reconcile,
    /// Retry, Disconnect and Close. The transport receive wait is deliberately
    /// outside the owner: when the session implements
    /// <see cref="ITableSessionFrameIngress"/>, PumpOnceAsync awaits
    /// ReceiveFrameAsync before enqueueing ApplyFrameAsync. A command submitted
    /// while the receive is parked therefore persists and sends without
    /// cancelling or closing the channel.
    ///
    /// Owner operations are executed one at a time in FIFO sequence order. For
    /// every operation the order is: run, collect the result/exception, read
    /// and publish State/Binding/LastError on this owner, then complete (or
    /// cancel/fault) the handle. No next owner mutation starts before that
    /// publication. The queue never holds a monitor while running a session
    /// method or raising a session event. Event handlers run synchronously on
    /// the owner and must
    /// not block on the queue (for example with GetAwaiter().GetResult());
    /// enqueueing from a handler, including an async void handler, is safe and
    /// runs after the current operation. Main-thread marshalling remains the
    /// platform's responsibility; this assembly has no Unity dependency.
    ///
    /// Closing stops new work, cancels queued operations, ends the logical pump
    /// and then runs the session Close on the owner. Open and Disconnect fence
    /// any receive already admitted before they execute, so a queued lifecycle
    /// operation cannot race a parked Receive. A channel that ignores
    /// cancellation cannot keep the logical pump alive: Close/lifecycle
    /// completion still returns, and any late frame/fault is observed and
    /// fenced so it never touches the current owner or a new binding. An
    /// unfinished transport receive is retained per binding (hard budget one);
    /// same-binding pump admission is rejected until it completes or Open
    /// establishes a new generation.
    ///
    /// Do not call the underlying session directly while a queue is in use.
    /// </summary>
    public sealed class TableSessionOperationQueue
    {
        private readonly IGameSession _session;
        private readonly ITableSessionFrameIngress? _ingress;
        private readonly object _gate = new object();
        private readonly Queue<Operation> _pending = new Queue<Operation>();
        private readonly CancellationTokenSource _receiveCts = new CancellationTokenSource();
        private bool _draining;
        private bool _closing;
        private bool _closed;
        private bool _lifecycleInProgress;
        private long _nextSequence;
        private TableConnectionState _observedState;
        private TableSessionBinding? _observedBinding;
        private string? _observedLastError;
        private Task<ReadOnlyMemory<byte>>? _unfinishedReceive;
        private string? _unfinishedReceiveBinding;
        private TableSessionOperation? _closeOperation;
        private ReceiveAttempt? _activeReceive;
        private long _receiveEpoch;

        public TableSessionOperationQueue(IGameSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _ingress = session as ITableSessionFrameIngress;
            _observedState = session.State;
            _observedBinding = session.Binding;
            _observedLastError = session.LastError;
        }

        public IGameSession Session => _session;

        /// <summary>True when the receive wait can be separated from the state owner.</summary>
        public bool SupportsFrameIngress => _ingress != null;

        /// <summary>Last owner-observed connection state; not a live session read.</summary>
        public TableConnectionState State
        {
            get
            {
                lock (_gate)
                {
                    return _observedState;
                }
            }
        }

        /// <summary>Last owner-observed binding; not a live session read.</summary>
        public TableSessionBinding? Binding
        {
            get
            {
                lock (_gate)
                {
                    return _observedBinding;
                }
            }
        }

        /// <summary>Last owner-observed error; not a live session read.</summary>
        public string? LastError
        {
            get
            {
                lock (_gate)
                {
                    return _observedLastError;
                }
            }
        }

        public bool IsClosing
        {
            get
            {
                lock (_gate)
                {
                    return _closing;
                }
            }
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

        /// <summary>Operations still waiting behind the running operation (including cancelled ones not yet skipped).</summary>
        public int PendingOperationCount
        {
            get
            {
                lock (_gate)
                {
                    return _pending.Count;
                }
            }
        }

        public Task OpenAsync(TableSessionOpenRequest request, CancellationToken cancellationToken)
        {
            return EnqueueOpen(request, cancellationToken).Completion;
        }

        public TableSessionOperation EnqueueOpen(TableSessionOpenRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            return EnqueueVoid(
                TableSessionOperationKind.Open,
                async token =>
                {
                    BeginLifecycleOperation();
                    try
                    {
                        await FenceReceiveForLifecycleAsync(token).ConfigureAwait(false);
                        await _session.OpenAsync(request, token).ConfigureAwait(false);
                        // A successful Open is a new transport generation: an
                        // old abandoned receive is fenced by binding identity
                        // and must not block the new generation's receive.
                        ResetUnfinishedReceiveForNewBinding();
                    }
                    finally
                    {
                        EndLifecycleOperation();
                    }
                },
                cancellationToken);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            return EnqueueDisconnect(cancellationToken).Completion;
        }

        public TableSessionOperation EnqueueDisconnect(CancellationToken cancellationToken)
        {
            return EnqueueVoid(
                TableSessionOperationKind.Disconnect,
                async token =>
                {
                    BeginLifecycleOperation();
                    try
                    {
                        await FenceReceiveForLifecycleAsync(token).ConfigureAwait(false);
                        await _session.DisconnectAsync(token).ConfigureAwait(false);
                    }
                    finally
                    {
                        EndLifecycleOperation();
                    }
                },
                cancellationToken);
        }

        public Task<CommandSubmission> SubmitAsync(ProtocolCommand command, CancellationToken cancellationToken)
        {
            return EnqueueSubmit(command, cancellationToken).Completion;
        }

        public TableSessionOperation<CommandSubmission> EnqueueSubmit(
            ProtocolCommand command,
            CancellationToken cancellationToken)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            return Enqueue(
                TableSessionOperationKind.Submit,
                token => _session.SubmitAsync(command, token),
                cancellationToken);
        }

        public Task ReconcilePendingAsync(CancellationToken cancellationToken)
        {
            return EnqueueReconcilePending(cancellationToken).Completion;
        }

        public TableSessionOperation EnqueueReconcilePending(CancellationToken cancellationToken)
        {
            return EnqueueVoid(
                TableSessionOperationKind.Reconcile,
                token => _session.ReconcilePendingAsync(token),
                cancellationToken);
        }

        public Task<CommandSubmission> RetryPendingAsync(IdempotencyKey key, CancellationToken cancellationToken)
        {
            return EnqueueRetryPending(key, cancellationToken).Completion;
        }

        public TableSessionOperation<CommandSubmission> EnqueueRetryPending(
            IdempotencyKey key,
            CancellationToken cancellationToken)
        {
            return Enqueue(
                TableSessionOperationKind.Retry,
                token => _session.RetryPendingAsync(key, token),
                cancellationToken);
        }

        public Task CloseAsync(string reasonCode, CancellationToken cancellationToken)
        {
            return EnqueueClose(reasonCode, cancellationToken).Completion;
        }

        /// <summary>
        /// Enqueues the terminal close. Once accepted, close is not cancellable
        /// by the caller token (a half-closed session is worse); a pre-cancelled
        /// token has no effect at all. Repeating Close returns the first handle.
        /// </summary>
        public TableSessionOperation EnqueueClose(string reasonCode, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var preCancelled = new TableSessionOperation(TableSessionOperationKind.Close, cancellationToken);
                preCancelled.Cancel(cancellationToken);
                return preCancelled;
            }

            TableSessionOperation closeHandle;
            Operation closeOperation;
            var queued = new List<Operation>();
            lock (_gate)
            {
                if (_closeOperation != null)
                {
                    return _closeOperation;
                }

                _closing = true;
                closeHandle = new TableSessionOperation(TableSessionOperationKind.Close, CancellationToken.None);
                closeHandle.Sequence = ++_nextSequence;
                closeOperation = new VoidOperation(
                    closeHandle,
                    token => CloseOnOwnerAsync(reasonCode, token),
                    CancellationToken.None);
                _closeOperation = closeHandle;
                while (_pending.Count > 0)
                {
                    queued.Add(_pending.Dequeue());
                }
            }

            // Cancel the parked receive and every queued operation outside the
            // gate: a channel's cancellation callback must never re-enter us
            // while the lock is held. A misbehaving cancellation callback must
            // not prevent the terminal close from being enqueued.
            try
            {
                _receiveCts.Cancel();
            }
            catch (Exception)
            {
            }

            _ = AbortActiveReceiveAsync(ReceiveAbortReason.Close, cancelChannel: true);

            for (int i = 0; i < queued.Count; i++)
            {
                queued[i].Cancel(CancellationToken.None);
                queued[i].Registration.Dispose();
            }

            bool startDrain = false;
            lock (_gate)
            {
                _pending.Enqueue(closeOperation);
                if (!_draining)
                {
                    _draining = true;
                    startDrain = true;
                }
            }

            if (startDrain)
            {
                StartDrain();
            }

            return closeHandle;
        }

        public Task<TableSessionQueueSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            return EnqueueCapture(cancellationToken).Completion;
        }

        public TableSessionOperation<TableSessionQueueSnapshot> EnqueueCapture(CancellationToken cancellationToken)
        {
            return Enqueue(
                TableSessionOperationKind.Capture,
                _ => Task.FromResult(CaptureOnOwner()),
                cancellationToken);
        }

        /// <summary>
        /// Receives at most one frame and applies it through the owner queue.
        /// The receive wait itself never holds the owner. Only one logical
        /// receive may be in flight at a time. The logical pump can be aborted
        /// without waiting for a channel that ignores its cancellation token;
        /// a late frame/fault is observed and fenced, never applied.
        ///
        /// At most one unfinished transport receive is retained per binding.
        /// If caller cancellation/fencing leaves that receive pending, a new
        /// pump on the same binding is rejected with
        /// <see cref="OperationCanceledException"/> until it returns or Open
        /// establishes a new transport generation. This is a deliberate hard
        /// budget of one, not an unbounded orphan stream.
        /// </summary>
        public async Task PumpOnceAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (_ingress == null)
            {
                if (IsClosed)
                {
                    throw new InvalidOperationException("The session operation queue is closed.");
                }

                if (IsClosing)
                {
                    throw new OperationCanceledException("The session operation queue is closing.");
                }

                TableSessionOperation fallback = EnqueueVoid(
                    TableSessionOperationKind.Pump,
                    token => _session.PumpOnceAsync(token),
                    cancellationToken);
                await AwaitPumpOperationAsync(fallback, cancellationToken).ConfigureAwait(false);
                return;
            }

            ReceiveAttempt attempt;
            lock (_gate)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("The session operation queue is closed.");
                }

                if (_closing || _lifecycleInProgress)
                {
                    throw new OperationCanceledException(
                        "The session operation queue is applying a lifecycle operation.");
                }

                if (_activeReceive != null)
                {
                    throw new InvalidOperationException("A pump receive is already in flight.");
                }

                if (_unfinishedReceive != null &&
                    !_unfinishedReceive.IsCompleted &&
                    string.Equals(
                        _unfinishedReceiveBinding,
                        BindingKey(_observedBinding),
                        StringComparison.Ordinal))
                {
                    throw new OperationCanceledException(
                        "A previous transport receive is still unfinished on this binding; " +
                        "pump admission is fenced until it returns or the binding changes.");
                }

                attempt = new ReceiveAttempt(
                    ++_receiveEpoch,
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _receiveCts.Token));
                _activeReceive = attempt;
            }

            CancellationTokenRegistration callerRegistration = default;
            CancellationTokenRegistration closeRegistration = default;
            try
            {
                callerRegistration = cancellationToken.Register(
                    () => SignalReceiveAbort(attempt, ReceiveAbortReason.CallerCanceled));
                closeRegistration = _receiveCts.Token.Register(
                    () => SignalReceiveAbort(attempt, ReceiveAbortReason.Close));

                if (cancellationToken.IsCancellationRequested)
                {
                    SignalReceiveAbort(attempt, ReceiveAbortReason.CallerCanceled);
                }
                else if (_receiveCts.IsCancellationRequested)
                {
                    SignalReceiveAbort(attempt, ReceiveAbortReason.Close);
                }

                Task<ReadOnlyMemory<byte>> receiveTask;
                try
                {
                    receiveTask = _ingress.ReceiveFrameAsync(attempt.Cancellation.Token);
                }
                catch (Exception exception)
                {
                    if (IsAborted(attempt))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        return;
                    }

                    await ApplyReceiveFaultAsync(attempt, exception).ConfigureAwait(false);
                    return;
                }

                Task finished = await Task.WhenAny(receiveTask, attempt.AbortSignal.Task).ConfigureAwait(false);
                if (!ReferenceEquals(finished, receiveTask))
                {
                    ObserveOrphanedReceive(receiveTask, attempt);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    return;
                }

                ReadOnlyMemory<byte> frame;
                try
                {
                    frame = await receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    if (IsAborted(attempt) || IsClosing || IsClosed)
                    {
                        return;
                    }

                    throw;
                }
                catch (TableChannelClosedException exception)
                {
                    if (IsAborted(attempt) || IsClosing || IsClosed)
                    {
                        return;
                    }

                    await ApplyReceiveFaultAsync(attempt, exception).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception)
                {
                    if (IsAborted(attempt) || IsClosing || IsClosed)
                    {
                        return;
                    }

                    await ApplyReceiveFaultAsync(attempt, exception).ConfigureAwait(false);
                    return;
                }

                if (IsAborted(attempt) || IsClosing || IsClosed)
                {
                    return;
                }

                TableSessionOperation apply = TryEnqueuePumpWork(
                    attempt,
                    TableSessionOperationKind.ApplyFrame,
                    token => ApplyFrameIfCurrentAsync(attempt, frame, token),
                    cancellationToken);
                try
                {
                    await AwaitPumpOperationAsync(apply, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    (IsAborted(attempt) || IsLifecycleInProgress()) &&
                    !cancellationToken.IsCancellationRequested)
                {
                    // A lifecycle operation cancelled the queued apply as part
                    // of its receive fence; the pump ends without surfacing a
                    // spurious caller cancellation.
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeReceive, attempt))
                    {
                        _activeReceive = null;
                    }
                }

                attempt.PumpCompleted.TrySetResult(true);
                callerRegistration.Dispose();
                closeRegistration.Dispose();
                if (!attempt.Orphaned)
                {
                    attempt.Cancellation.Dispose();
                }
            }
        }

        private async Task ApplyFrameIfCurrentAsync(
            ReceiveAttempt attempt,
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken)
        {
            bool current;
            lock (_gate)
            {
                current = ReferenceEquals(_activeReceive, attempt) &&
                          !attempt.Aborted &&
                          !_closing &&
                          !_closed;
            }

            if (!current)
            {
                return;
            }

            await _ingress!.ApplyFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        private bool IsAborted(ReceiveAttempt attempt)
        {
            lock (_gate)
            {
                return attempt.Aborted;
            }
        }

        private bool IsLifecycleInProgress()
        {
            lock (_gate)
            {
                return _lifecycleInProgress;
            }
        }

        private void SignalReceiveAbort(ReceiveAttempt attempt, ReceiveAbortReason reason)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_activeReceive, attempt))
                {
                    return;
                }

                if (!attempt.Aborted)
                {
                    attempt.Aborted = true;
                    attempt.AbortReason = reason;
                }
            }

            attempt.AbortSignal.TrySetResult(true);
        }

        private Task AbortActiveReceiveAsync(ReceiveAbortReason reason, bool cancelChannel)
        {
            ReceiveAttempt? attempt;
            lock (_gate)
            {
                attempt = _activeReceive;
                if (attempt == null)
                {
                    return Task.CompletedTask;
                }

                if (!attempt.Aborted)
                {
                    attempt.Aborted = true;
                    attempt.AbortReason = reason;
                }
            }

            // A frame that was already handed to the owner but not yet applied
            // is behind this lifecycle operation; cancel it so the pump can
            // complete and the lifecycle can continue without a FIFO deadlock.
            CancelPendingFrames(attempt);
            attempt.AbortSignal.TrySetResult(true);
            if (cancelChannel)
            {
                try
                {
                    attempt.Cancellation.Cancel();
                }
                catch (Exception)
                {
                    // The logical abort below is authoritative even when the
                    // channel's cancellation callback is broken.
                }
            }

            return attempt.PumpCompleted.Task;
        }

        private void CancelPendingFrames(ReceiveAttempt attempt)
        {
            var canceled = new List<Operation>();
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                var retained = new Queue<Operation>(_pending.Count);
                while (_pending.Count > 0)
                {
                    Operation operation = _pending.Dequeue();
                    if (ReferenceEquals(operation.Attempt, attempt))
                    {
                        canceled.Add(operation);
                    }
                    else
                    {
                        retained.Enqueue(operation);
                    }
                }

                while (retained.Count > 0)
                {
                    _pending.Enqueue(retained.Dequeue());
                }
            }

            for (int i = 0; i < canceled.Count; i++)
            {
                canceled[i].Cancel(CancellationToken.None);
                canceled[i].Registration.Dispose();
            }
        }

        private void ResetUnfinishedReceiveForNewBinding()
        {
            lock (_gate)
            {
                _unfinishedReceive = null;
                _unfinishedReceiveBinding = null;
            }
        }

        private void BeginLifecycleOperation()
        {
            lock (_gate)
            {
                _lifecycleInProgress = true;
            }
        }

        private void EndLifecycleOperation()
        {
            lock (_gate)
            {
                _lifecycleInProgress = false;
            }
        }

        private async Task FenceReceiveForLifecycleAsync(CancellationToken cancellationToken)
        {
            Task pump = AbortActiveReceiveAsync(ReceiveAbortReason.Lifecycle, cancelChannel: true);
            await pump.ConfigureAwait(false);
        }

        private void ObserveOrphanedReceive(
            Task<ReadOnlyMemory<byte>> receiveTask,
            ReceiveAttempt attempt)
        {
            attempt.Orphaned = true;
            string bindingKey = BindingKey(_observedBinding);
            lock (_gate)
            {
                _unfinishedReceive = receiveTask;
                _unfinishedReceiveBinding = bindingKey;
            }

            _ = receiveTask.ContinueWith(
                completed =>
                {
                    // Observe faults and drop any late frame. It must never
                    // reach the current owner or a new binding.
                    _ = completed.Exception;
                    attempt.Cancellation.Dispose();
                    lock (_gate)
                    {
                        if (ReferenceEquals(_unfinishedReceive, receiveTask))
                        {
                            _unfinishedReceive = null;
                            _unfinishedReceiveBinding = null;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task ApplyReceiveFaultAsync(ReceiveAttempt attempt, Exception exception)
        {
            if (IsClosing || IsClosed)
            {
                return;
            }

            TableSessionOperation operation = TryEnqueuePumpWork(
                attempt,
                TableSessionOperationKind.ApplyFrame,
                token => ApplyReceiveFaultIfCurrentAsync(attempt, exception, token),
                CancellationToken.None);
            try
            {
                await AwaitPumpOperationAsync(operation, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (IsAborted(attempt) || IsLifecycleInProgress() || IsClosing || IsClosed)
            {
                // A lifecycle/close fence removed the queued fault; the pump
                // must not keep waiting for an owner operation behind it.
            }
        }

        private async Task ApplyReceiveFaultIfCurrentAsync(
            ReceiveAttempt attempt,
            Exception exception,
            CancellationToken cancellationToken)
        {
            bool current;
            lock (_gate)
            {
                current = ReferenceEquals(_activeReceive, attempt) &&
                          !attempt.Aborted &&
                          !_closing &&
                          !_closed &&
                          !_lifecycleInProgress;
            }

            if (!current)
            {
                return;
            }

            await _ingress!.ApplyReceiveFaultAsync(exception, cancellationToken).ConfigureAwait(false);
        }

        private TableSessionOperation TryEnqueuePumpWork(
            ReceiveAttempt attempt,
            TableSessionOperationKind kind,
            Func<CancellationToken, Task> body,
            CancellationToken cancellationToken)
        {
            var handle = new TableSessionOperation(kind, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                handle.Cancel(cancellationToken);
                return handle;
            }

            var operation = new VoidOperation(handle, body, cancellationToken, attempt);
            operation.Registration = operation.Token.Register(
                () => operation.Handle.CancelIfQueued(operation.Token));
            bool rejected = false;
            bool startDrain = false;
            lock (_gate)
            {
                if (!ReferenceEquals(_activeReceive, attempt) ||
                    attempt.Aborted ||
                    _closing ||
                    _closed ||
                    _lifecycleInProgress)
                {
                    rejected = true;
                }
                else
                {
                    operation.Handle.Sequence = ++_nextSequence;
                    _pending.Enqueue(operation);
                    if (!_draining)
                    {
                        _draining = true;
                        startDrain = true;
                    }
                }
            }

            if (rejected)
            {
                operation.Registration.Dispose();
                operation.Cancel(cancellationToken);
                return handle;
            }

            if (startDrain)
            {
                StartDrain();
            }

            return handle;
        }

        private async Task AwaitPumpOperationAsync(
            TableSessionOperation operation,
            CancellationToken callerToken)
        {
            try
            {
                await operation.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested && (IsClosing || IsClosed))
            {
                // Close cancelled the queued apply/fallback pump after the
                // caller's receive already returned; that is a fenced stop,
                // not a caller cancellation.
            }
        }

        private TableSessionQueueSnapshot CaptureOnOwner()
        {
            var pending = new List<PendingCommandSnapshot>(_session.PendingCommands);
            return new TableSessionQueueSnapshot(
                _session.State,
                _session.Binding,
                _session.ConfirmedCursor,
                new ReadOnlyCollection<PendingCommandSnapshot>(pending),
                _session.LastError,
                _session.EarlyBufferOverflowCount);
        }

        private async Task CloseOnOwnerAsync(string reasonCode, CancellationToken cancellationToken)
        {
            await FenceReceiveForLifecycleAsync(CancellationToken.None).ConfigureAwait(false);
            await _session.CloseAsync(reasonCode, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _closed = true;
            }
        }

        private TableSessionOperation<T> Enqueue<T>(
            TableSessionOperationKind kind,
            Func<CancellationToken, Task<T>> body,
            CancellationToken cancellationToken)
        {
            var handle = new TableSessionOperation<T>(kind, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                handle.Cancel(cancellationToken);
                return handle;
            }

            var operation = new Operation<T>(handle, body, cancellationToken);
            EnqueueCore(operation);
            return handle;
        }

        private TableSessionOperation EnqueueVoid(
            TableSessionOperationKind kind,
            Func<CancellationToken, Task> body,
            CancellationToken cancellationToken,
            ReceiveAttempt? attempt = null)
        {
            var handle = new TableSessionOperation(kind, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                handle.Cancel(cancellationToken);
                return handle;
            }

            var operation = new VoidOperation(handle, body, cancellationToken, attempt);
            EnqueueCore(operation);
            return handle;
        }

        private void EnqueueCore(Operation operation)
        {
            operation.Registration = operation.Token.Register(
                () => operation.Handle.CancelIfQueued(operation.Token));

            bool rejected = false;
            bool startDrain = false;
            lock (_gate)
            {
                if (_closing || _closed)
                {
                    rejected = true;
                }
                else
                {
                    operation.Handle.Sequence = ++_nextSequence;
                    _pending.Enqueue(operation);
                    if (!_draining)
                    {
                        _draining = true;
                        startDrain = true;
                    }
                }
            }

            if (rejected)
            {
                operation.Registration.Dispose();
                operation.Cancel(operation.Token);
                return;
            }

            if (startDrain)
            {
                StartDrain();
            }
        }

        private void StartDrain()
        {
            // Never run owner work inline on the enqueueing caller. In
            // particular, a State/Binding/LastError getter may block while the
            // owner publishes; the enqueue call must still return its handle
            // so the caller can observe that ordering.
            ThreadPool.QueueUserWorkItem(_ => { _ = DrainAsync(); });
        }

        private async Task DrainAsync()
        {
            while (true)
            {
                Operation? operation;
                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        _draining = false;
                        return;
                    }

                    operation = _pending.Dequeue();
                    if (!operation.Handle.IsTerminal)
                    {
                        operation.Handle.MarkRunning();
                    }
                }

                if (operation.Handle.IsTerminal)
                {
                    operation.Registration.Dispose();
                    continue;
                }

                try
                {
                    await operation.ExecuteAsync().ConfigureAwait(false);
                    // Single owner order, for every IGameSession: collect the
                    // result, read and publish State/Binding/LastError on this
                    // owner, then release the completion. The next queued
                    // mutation cannot start before that refresh returns.
                    Exception? refreshError = TryRefreshObservedState();
                    if (refreshError != null)
                    {
                        operation.CompleteFault(refreshError);
                    }
                    else
                    {
                        operation.CompleteSuccess();
                    }
                }
                catch (OperationCanceledException exception)
                {
                    TryRefreshObservedState();
                    operation.CompleteCanceled(
                        exception.CancellationToken.CanBeCanceled
                            ? exception.CancellationToken
                            : operation.Token);
                }
                catch (Exception exception)
                {
                    TryRefreshObservedState();
                    operation.CompleteFault(exception);
                }
                finally
                {
                    operation.Registration.Dispose();
                }
            }
        }

        private void RefreshObservedState()
        {
            TableConnectionState state = _session.State;
            TableSessionBinding? binding = _session.Binding;
            string? error = _session.LastError;
            string bindingKey = BindingKey(binding);
            lock (_gate)
            {
                _observedState = state;
                _observedBinding = binding;
                _observedLastError = error;
                if (_unfinishedReceive != null &&
                    !string.Equals(_unfinishedReceiveBinding, bindingKey, StringComparison.Ordinal))
                {
                    // The old transport generation is fenced by the binding
                    // change; a new binding may open its own receive.
                    _unfinishedReceive = null;
                    _unfinishedReceiveBinding = null;
                }
            }
        }

        private Exception? TryRefreshObservedState()
        {
            try
            {
                RefreshObservedState();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static string BindingKey(TableSessionBinding? binding)
        {
            if (binding == null)
            {
                return "unbound";
            }

            return binding.SessionId.Value + ":" + binding.BindingEpoch.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        }

        private enum ReceiveAbortReason
        {
            None = 0,
            CallerCanceled = 1,
            Lifecycle = 2,
            Close = 3
        }

        private sealed class ReceiveAttempt
        {
            public ReceiveAttempt(long epoch, CancellationTokenSource cancellation)
            {
                Epoch = epoch;
                Cancellation = cancellation;
            }

            public long Epoch { get; }

            public CancellationTokenSource Cancellation { get; }

            public TaskCompletionSource<bool> AbortSignal { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> PumpCompleted { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool Aborted { get; set; }

            public ReceiveAbortReason AbortReason { get; set; }

            public volatile bool Orphaned;
        }

        private abstract class Operation
        {
            protected Operation(
                TableSessionOperation handle,
                CancellationToken token,
                ReceiveAttempt? attempt)
            {
                Handle = handle ?? throw new ArgumentNullException(nameof(handle));
                Token = token;
                Attempt = attempt;
            }

            public TableSessionOperation Handle { get; }

            public CancellationToken Token { get; }

            public ReceiveAttempt? Attempt { get; }

            public CancellationTokenRegistration Registration { get; set; }

            public abstract Task ExecuteAsync();

            public abstract void CompleteSuccess();

            public abstract void CompleteCanceled(CancellationToken cancellationToken);

            public abstract void CompleteFault(Exception exception);

            public abstract void Cancel(CancellationToken cancellationToken);

            public abstract void Fail(Exception exception);
        }

        private sealed class Operation<T> : Operation
        {
            private readonly Func<CancellationToken, Task<T>> _body;
            private T? _result;

            public Operation(
                TableSessionOperation<T> handle,
                Func<CancellationToken, Task<T>> body,
                CancellationToken token)
                : base(handle, token, null)
            {
                _body = body ?? throw new ArgumentNullException(nameof(body));
                TypedHandle = handle;
            }

            private TableSessionOperation<T> TypedHandle { get; }

            public override async Task ExecuteAsync()
            {
                _result = await _body(Token).ConfigureAwait(false);
            }

            public override void CompleteSuccess()
            {
                TypedHandle.Complete(_result!);
            }

            public override void CompleteCanceled(CancellationToken cancellationToken)
            {
                TypedHandle.Cancel(cancellationToken);
            }

            public override void CompleteFault(Exception exception)
            {
                TypedHandle.Fail(exception);
            }

            public override void Cancel(CancellationToken cancellationToken)
            {
                TypedHandle.Cancel(cancellationToken);
            }

            public override void Fail(Exception exception)
            {
                TypedHandle.Fail(exception);
            }
        }

        private sealed class VoidOperation : Operation
        {
            private readonly Func<CancellationToken, Task> _body;

            public VoidOperation(
                TableSessionOperation handle,
                Func<CancellationToken, Task> body,
                CancellationToken token,
                ReceiveAttempt? attempt = null)
                : base(handle, token, attempt)
            {
                _body = body ?? throw new ArgumentNullException(nameof(body));
            }

            public override async Task ExecuteAsync()
            {
                await _body(Token).ConfigureAwait(false);
            }

            public override void CompleteSuccess()
            {
                Handle.Complete();
            }

            public override void CompleteCanceled(CancellationToken cancellationToken)
            {
                Handle.Cancel(cancellationToken);
            }

            public override void CompleteFault(Exception exception)
            {
                Handle.Fail(exception);
            }

            public override void Cancel(CancellationToken cancellationToken)
            {
                Handle.Cancel(cancellationToken);
            }

            public override void Fail(Exception exception)
            {
                Handle.Fail(exception);
            }
        }
    }
}
