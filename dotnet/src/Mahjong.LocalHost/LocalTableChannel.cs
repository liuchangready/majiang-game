using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;

namespace Mahjong.LocalHost
{
    internal enum LocalChannelState : byte
    {
        New = 0,
        Opening = 1,
        Open = 2,
        TransientClosed = 3,
        ClosingPermanent = 4,
        PermanentlyClosed = 5
    }

    /// <summary>
    /// One local table channel. It implements exactly the accepted ITableChannel surface and
    /// owns the local runtime only after the first successful open. Permanent closure is an
    /// atomic, single-flight cleanup that later callers await rather than duplicate.
    /// </summary>
    internal sealed class LocalTableChannel : ITableChannel
    {
        private readonly object _gate = new object();
        private readonly LocalTableHostOptions _options;
        private readonly CancellationTokenSource _openCts = new CancellationTokenSource();

        private LocalChannelState _state = LocalChannelState.New;
        private LocalTableRuntime? _runtime;
        private Task? _openTask;
        private Task? _reopenTask;
        private Task? _transientCloseTask;
        private Task? _permanentCloseTask;
        private int _openWaiters;
        private int _reopenWaiters;
        private long _reopenGeneration;
        private string _closeReason = ProtocolErrorCode.LocalClosed;

        public LocalTableChannel(LocalTableHostOptions options)
        {
            _options = options;
        }

        /// <summary>Test seam: lets a test hold a transient reopen open while it cancels waiters.</summary>
        internal LocalTableRuntime? RuntimeForMatch => _runtime;

        internal LocalChannelState StateForTest
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        internal Func<CancellationToken, Task>? ReopenHookForTest { get; set; }

        /// <summary>Test seam: gates initial resource acquisition after waiter registration.</summary>
        internal Func<CancellationToken, Task>? OpenHookForTest { get; set; }

        public bool IsOpen
        {
            get
            {
                lock (_gate)
                {
                    return _state == LocalChannelState.Open;
                }
            }
        }

        public async Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_state == LocalChannelState.New && _openTask == null)
                    {
                        _state = LocalChannelState.PermanentlyClosed;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            Task task;
            bool initial;
            lock (_gate)
            {
                if (_state == LocalChannelState.PermanentlyClosed || _state == LocalChannelState.ClosingPermanent)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, true);
                }

                if (_state == LocalChannelState.Open)
                {
                    return;
                }

                initial = _state == LocalChannelState.New || _state == LocalChannelState.Opening;
                if (initial)
                {
                    if (_openTask == null)
                    {
                        _state = LocalChannelState.Opening;
                        _openTask = OpenInitialCoreAsync(request, _openCts.Token);
                    }

                    _openWaiters++;
                    task = _openTask;
                }
                else
                {
                    if (_reopenTask == null)
                    {
                        _reopenTask = ReopenCoreAsync(request, _openCts.Token);
                    }

                    _reopenWaiters++;
                    task = _reopenTask;
                }
            }

            bool canceled = false;
            try
            {
                await WaitWithCancellationAsync(task, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                throw;
            }
            finally
            {
                bool cancelShared = false;
                if (initial)
                {
                    lock (_gate)
                    {
                        if (_openWaiters > 0)
                        {
                            _openWaiters--;
                        }

                        cancelShared = canceled && _openWaiters == 0 && _state == LocalChannelState.Opening;
                    }
                }
                else
                {
                    lock (_gate)
                    {
                        if (_reopenWaiters > 0)
                        {
                            _reopenWaiters--;
                        }

                        cancelShared = canceled && _reopenWaiters == 0;
                    }
                }

                if (cancelShared)
                {
                    if (initial)
                    {
                        TryCancelOpen();
                    }
                    else
                    {
                        await AbortReopenAsync().ConfigureAwait(false);
                    }
                }
            }

            // Propagate the shared task's real failure after this waiter accounted for itself.
            await task.ConfigureAwait(false);
        }

        private async Task OpenInitialCoreAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            LocalTableRuntime? runtime = null;
            try
            {
                if (string.IsNullOrEmpty(request.Ticket))
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.Unavailable,
                        "A local connection ticket is required.");
                }

                if (OpenHookForTest != null)
                {
                    await OpenHookForTest(cancellationToken).ConfigureAwait(false);
                }

                runtime = await LocalTableRuntime.OpenAsync(_options, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                    throw new OperationCanceledException(cancellationToken);
                }

                lock (_gate)
                {
                    if (_state == LocalChannelState.ClosingPermanent)
                    {
                        // Permanent closure won the race; cleanup below releases the freshly
                        // created runtime and the permanent path normalizes the final state.
                        throw new OperationCanceledException(cancellationToken);
                    }

                    _runtime = runtime;
                    _state = LocalChannelState.Open;
                }
            }
            catch (OperationCanceledException)
            {
                if (runtime != null)
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _state = LocalChannelState.PermanentlyClosed;
                }

                throw;
            }
            catch (Exception exception)
            {
                if (runtime != null)
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _state = LocalChannelState.PermanentlyClosed;
                }

                if (exception is LocalTableException)
                {
                    throw;
                }

                throw new LocalTableException(
                    LocalTableErrorKind.Unavailable,
                    "The local host could not open: " + exception.Message,
                    exception);
            }
        }

        private async Task ReopenCoreAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            long generation;
            lock (_gate)
            {
                generation = ++_reopenGeneration;
            }

            if (string.IsNullOrEmpty(request.Ticket))
            {
                throw new LocalTableException(
                    LocalTableErrorKind.Unavailable,
                    "A local connection ticket is required.");
            }

            if (ReopenHookForTest != null)
            {
                await ReopenHookForTest(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            LocalTableRuntime runtime;
            lock (_gate)
            {
                if (generation != _reopenGeneration ||
                    _runtime == null ||
                    _state != LocalChannelState.TransientClosed)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
                }

                runtime = _runtime;
            }

            await runtime.ReconnectAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _reopenGeneration || _state != LocalChannelState.TransientClosed)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false);
                }

                _state = LocalChannelState.Open;
            }
        }

        private async Task AbortReopenAsync()
        {
            lock (_gate)
            {
                _reopenGeneration++;
                _reopenTask = null;
                if (_runtime != null)
                {
                    _runtime.CloseSession();
                }

                if (_state == LocalChannelState.Open || _state == LocalChannelState.TransientClosed)
                {
                    _state = LocalChannelState.TransientClosed;
                }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            LocalTableRuntime runtime;
            lock (_gate)
            {
                ThrowIfNotOpen();
                runtime = _runtime!;
            }

            await runtime.SendAsync(wireEnvelope, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            LocalTableRuntime runtime;
            lock (_gate)
            {
                ThrowIfNotOpen();
                runtime = _runtime!;
            }

            return await runtime.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            if (reason == null)
            {
                throw new ArgumentNullException(nameof(reason));
            }

            _closeReason = reason.Code;
            if (reason.Permanent)
            {
                Task cleanup;
                lock (_gate)
                {
                    if (_state == LocalChannelState.PermanentlyClosed)
                    {
                        cleanup = _permanentCloseTask ?? Task.CompletedTask;
                    }
                    else
                    {
                        if (_permanentCloseTask == null)
                        {
                            bool wasOpening = _state == LocalChannelState.Opening;
                            _state = LocalChannelState.ClosingPermanent;
                            if (wasOpening)
                            {
                                TryCancelOpen();
                            }

                            _permanentCloseTask = PermanentCloseCoreAsync(reason.Code);
                            cleanup = _permanentCloseTask;
                        }
                        else
                        {
                            cleanup = _permanentCloseTask;
                        }
                    }
                }

                try
                {
                    await WaitWithCancellationAsync(cleanup, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The cleanup task continues unconditionally in the background; a later
                    // close awaits the same task and therefore returns only after the lock release.
                    throw;
                }

                return;
            }

            bool canceled = cancellationToken.IsCancellationRequested;
            Task? open;
            bool opening;
            lock (_gate)
            {
                if (_state == LocalChannelState.New || _state == LocalChannelState.TransientClosed ||
                    _state == LocalChannelState.PermanentlyClosed)
                {
                    return;
                }

                open = _openTask;
                opening = _state == LocalChannelState.Opening;
            }

            if (opening && open != null)
            {
                try
                {
                    await open.ConfigureAwait(false);
                }
                catch
                {
                    // A failed open already normalized the channel to permanently closed.
                }
            }

            LocalTableRuntime? runtime = null;
            lock (_gate)
            {
                if (_state == LocalChannelState.ClosingPermanent || _state == LocalChannelState.PermanentlyClosed)
                {
                    runtime = null;
                }
                else
                {
                    _state = LocalChannelState.TransientClosed;
                    _openTask = null;
                    _reopenTask = null;
                    _transientCloseTask = null;
                    runtime = _runtime;
                }
            }

            if (runtime != null)
            {
                await runtime.CloseSessionAsync(CancellationToken.None).ConfigureAwait(false);
            }

            Task? permanent;
            lock (_gate)
            {
                permanent = _state == LocalChannelState.ClosingPermanent || _state == LocalChannelState.PermanentlyClosed
                    ? _permanentCloseTask
                    : null;
            }

            if (permanent != null)
            {
                await WaitWithCancellationAsync(permanent, cancellationToken).ConfigureAwait(false);
            }

            if (canceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

        }
        private async Task TransientCloseCoreAsync(string reason)
        {
            Task? open;
            lock (_gate)
            {
                open = _openTask;
            }

            if (open != null)
            {
                try
                {
                    await open.ConfigureAwait(false);
                }
                catch
                {
                    // A failed initial open already normalized the channel to permanently closed.
                }
            }

            lock (_gate)
            {
                if (_state == LocalChannelState.PermanentlyClosed || _state == LocalChannelState.ClosingPermanent)
                {
                    return;
                }

                _state = LocalChannelState.TransientClosed;
                _openTask = null;
                _reopenTask = null;
                _transientCloseTask = null;
            }

            if (_runtime != null)
            {
                await _runtime.CloseSessionAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task PermanentCloseCoreAsync(string reason)
        {
            Task? open;
            Task? transient;
            Task? reopen;
            lock (_gate)
            {
                open = _openTask;
                transient = _transientCloseTask;
                reopen = _reopenTask;
            }

            if (open != null)
            {
                try
                {
                    await open.ConfigureAwait(false);
                }
                catch
                {
                    // Open failed/cancelled; cleanup below still owns any runtime it published.
                }
            }

            if (reopen != null)
            {
                try
                {
                    await reopen.ConfigureAwait(false);
                }
                catch
                {
                    // A cancelled reopen leaves the existing actor/lock in place.
                }
            }

            if (transient != null)
            {
                try
                {
                    await transient.ConfigureAwait(false);
                }
                catch
                {
                    // Transient close cannot fail cleanup; continue with permanent release.
                }
            }

            LocalTableRuntime? runtime;
            lock (_gate)
            {
                runtime = _runtime;
                _runtime = null;
            }

            if (runtime != null)
            {
                try
                {
                    runtime.CloseSession();
                }
                catch
                {
                    // Dispose still releases the file lock.
                }

                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            lock (_gate)
            {
                _state = LocalChannelState.PermanentlyClosed;
                _openTask = null;
                _reopenTask = null;
                _transientCloseTask = null;
            }
        }

        private void TryCancelOpen()
        {
            try
            {
                _openCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The open already completed.
            }
        }

        private void ThrowIfNotOpen()
        {
            switch (_state)
            {
                case LocalChannelState.Open:
                    return;
                case LocalChannelState.PermanentlyClosed:
                case LocalChannelState.ClosingPermanent:
                    throw new TableChannelClosedException(_closeReason, true);
                default:
                    throw new TableChannelClosedException(_closeReason, false);
            }
        }

        private static async Task WaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                await task.ConfigureAwait(false);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                       static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                       completion))
            {
                Task finished = await Task.WhenAny(task, completion.Task).ConfigureAwait(false);
                if (finished == task)
                {
                    await task.ConfigureAwait(false);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        internal int WaiterCount
        {
            get
            {
                lock (_gate)
                {
                    return _openWaiters + _reopenWaiters;
                }
            }
        }
    }
}
