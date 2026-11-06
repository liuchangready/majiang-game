using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// A real production <see cref="TableSession"/> over a real WSS socket,
    /// driven through the production <see cref="TableSessionOperationQueue"/>.
    /// The queue serializes Open/Submit/Reconcile with the parked receive, so
    /// the worker never calls the session concurrently from two threads.
    /// </summary>
    public sealed class ClientHandle : IAsyncDisposable
    {
        private readonly TableSession _session;
        private readonly TableSessionOperationQueue _operations;
        private readonly HeartbeatBindingAuditChannel _heartbeatAudit;
        private readonly HeartbeatTableChannel _heartbeat;
        private readonly ReceiptLossChannel _loss;
        private readonly RealWssTableChannel _transport;
        private readonly CancellationTokenSource _pumpCancellation = new CancellationTokenSource();
        private readonly PrivacyAuditState _privacyAudit = new PrivacyAuditState();
        private readonly object _snapshotGate = new object();
        private Task? _pumpTask;
        private PlayerSnapshot? _latestPlayer;
        private PublicSnapshot? _latestPublic;
        private RoomUpdate? _latestRoom;
        private long _baselineCount;
        private long _lastBaselineVersion;
        private Mahjong.Contracts.StreamCursor? _confirmedCursor;
        private TableVisibleEventBatch _lastVisibleEvents = TableVisibleEventBatch.Empty;
        private TableReceiptResolvedEventArgs? _lastResolution;

        public ClientHandle(
            Uri endpoint,
            System.Security.Cryptography.X509Certificates.X509Certificate2 trustedRoot,
            string outboxDirectory,
            string? lossRequestId = null,
            bool dropTargetReceipts = false)
        {
            _transport = new RealWssTableChannel(endpoint, trustedRoot);
            _loss = new ReceiptLossChannel(_transport)
            {
                TargetRequestId = lossRequestId,
                DropTargetReceipts = dropTargetReceipts
            };
            _heartbeatAudit = new HeartbeatBindingAuditChannel(_loss);
            _heartbeat = new HeartbeatTableChannel(_heartbeatAudit);
            var outbox = new FileOutboxStore(outboxDirectory);
            _session = new TableSession(_heartbeat, outbox);
            _operations = new TableSessionOperationQueue(_session);
            _session.SnapshotChanged += OnSnapshotChanged;
            _session.ReceiptResolved += OnReceiptResolved;
        }

        public string Role { get; set; } = "PLAYER";

        public string AccountId { get; set; } = string.Empty;

        public string AssignmentId { get; set; } = string.Empty;

        public int Seat { get; set; } = -1;

        public ReceiptLossChannel Loss => _loss;

        public HeartbeatBindingAuditChannel HeartbeatAudit => _heartbeatAudit;

        /// <summary>The configured WELCOME heartbeat interval (not the /2 send period).</summary>
        public int NegotiatedHeartbeatIntervalMs => _heartbeat.Snapshot?.NegotiatedIntervalMs ?? 0;

        public RealWssTableChannel Transport => _transport;

        public TableConnectionState State => _operations.State;

        public TableSessionBinding? Binding => _operations.Binding;

        public Mahjong.Contracts.StreamCursor? ConfirmedCursor
        {
            get
            {
                lock (_snapshotGate)
                {
                    return _confirmedCursor;
                }
            }
        }

        public string? LastError => _operations.LastError;

        public TableVisibleEventBatch LastVisibleEvents
        {
            get
            {
                lock (_snapshotGate)
                {
                    return _lastVisibleEvents;
                }
            }
        }

        public bool PrivacyViolated => _privacyAudit.Violated;

        public string? PrivacyReason => _privacyAudit.Reason;

        public long PrivacyBatchesObserved => _privacyAudit.BatchesObserved;

        public long BaselineCount
        {
            get
            {
                lock (_snapshotGate)
                {
                    return _baselineCount;
                }
            }
        }

        public long LastBaselineVersion
        {
            get
            {
                lock (_snapshotGate)
                {
                    return _lastBaselineVersion;
                }
            }
        }

        public PlayerSnapshot? LatestPlayer
        {
            get
            {
                lock (_snapshotGate)
                {
                    return _latestPlayer?.Clone();
                }
            }
        }

        public PublicSnapshot? LatestPublic
        {
            get
            {
                lock (_snapshotGate)
                {
                    return _latestPublic?.Clone();
                }
            }
        }

        public RoomUpdate? LatestRoom
        {
            get
            {
                lock (_snapshotGate)
                {
                    return _latestRoom?.Clone();
                }
            }
        }

        public TableReceiptResolvedEventArgs? LastResolution
        {
            get
            {
                lock (_snapshotGate)
                {
                    return _lastResolution;
                }
            }
        }

        public async Task OpenAsync(string ticket, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await _operations.OpenAsync(
                new TableSessionOpenRequest(ticket, "p54b-b1-worker"),
                cancellationToken).ConfigureAwait(false);
            _pumpTask = Task.Run(() => PumpLoopAsync(_pumpCancellation.Token), CancellationToken.None);
            try
            {
                await WaitForAsync(
                    () => _operations.State == TableConnectionState.Ready,
                    timeout,
                    "the real Client baseline did not reach Ready").ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    "The real Client baseline did not reach Ready (state=" + State +
                    ", lastError=" + Program.Redact(LastError) +
                    ", inbound=" + Loss.InboundTrace + ").");
            }
        }

        private async Task PumpLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _operations.PumpOnceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    if (cancellationToken.IsCancellationRequested ||
                        _operations.State == TableConnectionState.Closed ||
                        _operations.State == TableConnectionState.Faulted)
                    {
                        return;
                    }

                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public Task<CommandSubmission> SubmitAsync(
            ProtocolCommand command,
            CancellationToken cancellationToken)
        {
            return _operations.SubmitAsync(command, cancellationToken);
        }

        public Task ReconcileAsync(CancellationToken cancellationToken)
        {
            return _operations.ReconcilePendingAsync(cancellationToken);
        }

        public Task<TableSessionQueueSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            return _operations.CaptureAsync(cancellationToken);
        }

        public async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, string because)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            if (!predicate())
            {
                throw new TimeoutException("Timed out waiting for " + because + ".");
            }
        }

        public async Task<IReadOnlyList<PendingCommandSnapshot>> PendingAsync(CancellationToken cancellationToken)
        {
            TableSessionQueueSnapshot snapshot = await CaptureAsync(cancellationToken).ConfigureAwait(false);
            return snapshot.PendingCommands;
        }

        private void OnSnapshotChanged(object? sender, TableSnapshotChangedEventArgs args)
        {
            TableSessionBinding? binding = _operations.Binding;
            if (binding != null)
            {
                _privacyAudit.Observe(
                    binding.SessionId.Value,
                    binding.RoomId.Value,
                    Role,
                    Seat,
                    args.PlayerSnapshot,
                    args.PublicSnapshot,
                    args.VisibleEventBatch);
            }

            lock (_snapshotGate)
            {
                if (args.PlayerSnapshot != null)
                {
                    _latestPlayer = args.PlayerSnapshot.Clone();
                }

                if (args.PublicSnapshot != null)
                {
                    _latestPublic = args.PublicSnapshot.Clone();
                }

                if (args.RoomSnapshot != null)
                {
                    _latestRoom = args.RoomSnapshot.Clone();
                }

                if (args.IsFullBaseline)
                {
                    _baselineCount++;
                    _confirmedCursor = args.Cursor;
                    if (args.PlayerSnapshot != null)
                    {
                        _lastBaselineVersion = (long)args.PlayerSnapshot.StateVersion;
                    }
                    else if (args.PublicSnapshot != null)
                    {
                        _lastBaselineVersion = (long)args.PublicSnapshot.StateVersion;
                    }
                }

                _lastVisibleEvents = args.VisibleEventBatch;
            }
        }

        private void OnReceiptResolved(object? sender, TableReceiptResolvedEventArgs args)
        {
            lock (_snapshotGate)
            {
                _lastResolution = args;
            }
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            try
            {
                _pumpCancellation.Cancel();
                await _operations.CloseAsync("b1-test-close", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort in test teardown.
            }

            if (_pumpTask != null)
            {
                try
                {
                    await _pumpTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _pumpCancellation.Cancel();
                await _operations.CloseAsync("b1-worker-dispose", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            _session.SnapshotChanged -= OnSnapshotChanged;
            _session.ReceiptResolved -= OnReceiptResolved;
            _heartbeat.Dispose();
            _transport.Dispose();
            _pumpCancellation.Dispose();
        }
    }
}
