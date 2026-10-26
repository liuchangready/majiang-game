using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;
using ContractCursor = Mahjong.Contracts.StreamCursor;

// Deterministic barrier/TCS tests pass explicit scenario tokens (including
// deliberately cancelled ones), not the xUnit test token.
#pragma warning disable xUnit1051

namespace Mahjong.Client.Tests
{
    /// <summary>
    /// Deterministic transport fake. Receive callers park on a TCS; the test
    /// releases them explicitly. There is no sleep-based polling.
    /// </summary>
    internal sealed class DeterministicTableChannel : ITableChannel
    {
        private readonly object _gate = new object();
        private readonly Queue<byte[]> _inbound = new Queue<byte[]>();
        private readonly List<byte[]> _sent = new List<byte[]>();
        private readonly List<string> _timeline = new List<string>();
        private TaskCompletionSource<ReadOnlyMemory<byte>>? _pendingReceive;
        private TaskCompletionSource<bool> _receiveSignal =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _sendSignal =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? _sendGate;
        private TaskCompletionSource<bool>? _parkedWait;
        private int _parkedReceiveCount;
        private int _parkedTarget = int.MaxValue;
        private bool _open;

        public bool IgnoreReceiveCancellation { get; set; }

        public bool CloseCompletesReceive { get; set; } = true;

        public bool ThrowClosedOnNextReceive { get; set; }

        public bool FailNextSend { get; set; }

        public bool AutoRespondToHandshake { get; set; }

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

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

        public Task ReceiveSignal => Volatile.Read(ref _receiveSignal).Task;

        public Task SendSignal => Volatile.Read(ref _sendSignal).Task;

        public bool HasPendingReceive
        {
            get
            {
                lock (_gate)
                {
                    return _pendingReceive != null;
                }
            }
        }

        public int ParkedReceiveCount
        {
            get
            {
                lock (_gate)
                {
                    return _parkedReceiveCount;
                }
            }
        }

        public IReadOnlyList<string> Timeline
        {
            get
            {
                lock (_gate)
                {
                    return new List<string>(_timeline);
                }
            }
        }

        public event Action<MessageType>? MessageSent;

        public void ResetReceiveSignal()
        {
            Interlocked.Exchange(
                ref _receiveSignal,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public Task WaitForParkedReceivesAsync(int target)
        {
            lock (_gate)
            {
                if (_parkedReceiveCount >= target)
                {
                    return Task.CompletedTask;
                }

                _parkedTarget = target;
                if (_parkedWait == null || _parkedWait.Task.IsCompleted)
                {
                    _parkedWait = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return _parkedWait.Task;
            }
        }

        public void BlockSends()
        {
            lock (_gate)
            {
                _sendGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _sendSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseSends()
        {
            TaskCompletionSource<bool>? gate;
            lock (_gate)
            {
                gate = _sendGate;
                _sendGate = null;
            }

            gate?.TrySetResult(true);
        }

        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                OpenCount++;
                _open = true;
                _timeline.Add("open");
            }

            return Task.CompletedTask;
        }

        public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool>? gate;
            lock (_gate)
            {
                gate = _sendGate;
                _sendSignal.TrySetResult(true);
                _timeline.Add("send.enter");
            }

            if (gate != null)
            {
                using (cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken)))
                {
                    await gate.Task.ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = wireEnvelope.ToArray();
            WireDecodeResult decoded = WireCodec.Decode(bytes);
            if (!decoded.IsOk)
            {
                throw new InvalidOperationException("The test sent an undecodable envelope.");
            }

            Envelope envelope = decoded.Envelope!;
            lock (_gate)
            {
                if (!_open)
                {
                    throw new TableChannelClosedException("TEST_CHANNEL_CLOSED", false);
                }

                if (FailNextSend)
                {
                    FailNextSend = false;
                    throw new TableChannelClosedException("TEST_SEND_FAILED", false);
                }

                _sent.Add(bytes);
                _timeline.Add("send:" + envelope.MessageType);
            }

            MessageSent?.Invoke(envelope.MessageType);
            if (AutoRespondToHandshake)
            {
                if (envelope.MessageType == MessageType.Hello)
                {
                    Deliver(TestWire.Welcome(11, "scope-1", 5, 10));
                }
                else if (envelope.MessageType == MessageType.SyncRequest)
                {
                    Deliver(TestWire.PlayerSync(
                        11,
                        5,
                        10,
                        seatId: 0,
                        correlationId: envelope.CorrelationId));
                }
            }
        }

        public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<ReadOnlyMemory<byte>> completion;
            lock (_gate)
            {
                if (ThrowClosedOnNextReceive)
                {
                    ThrowClosedOnNextReceive = false;
                    return Task.FromException<ReadOnlyMemory<byte>>(
                        new TableChannelClosedException("TEST_RECEIVE_CLOSED", false));
                }

                if (_inbound.Count > 0)
                {
                    _receiveSignal.TrySetResult(true);
                    return Task.FromResult<ReadOnlyMemory<byte>>(_inbound.Dequeue());
                }

                completion = new TaskCompletionSource<ReadOnlyMemory<byte>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingReceive = completion;
                _timeline.Add("receive.park");
                _receiveSignal.TrySetResult(true);
                _parkedReceiveCount++;
                if (_parkedWait != null && _parkedReceiveCount >= _parkedTarget)
                {
                    _parkedWait.TrySetResult(true);
                }
            }

            if (!IgnoreReceiveCancellation)
            {
                CancellationTokenRegistration registration = cancellationToken.Register(
                    () =>
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_pendingReceive, completion))
                            {
                                _pendingReceive = null;
                            }
                        }

                        completion.TrySetCanceled(cancellationToken);
                    });

                _ = completion.Task.ContinueWith(
                    _ => registration.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return completion.Task;
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            TaskCompletionSource<ReadOnlyMemory<byte>>? pending;
            lock (_gate)
            {
                CloseCount++;
                _open = false;
                _timeline.Add("close:" + reason.Code);
                pending = _pendingReceive;
                if (CloseCompletesReceive)
                {
                    _pendingReceive = null;
                }
            }

            if (CloseCompletesReceive && pending != null)
            {
                pending.TrySetException(new TableChannelClosedException("TEST_CLOSE", true));
            }

            return Task.CompletedTask;
        }

        public void FaultPendingReceive(Exception exception)
        {
            TaskCompletionSource<ReadOnlyMemory<byte>>? pending;
            lock (_gate)
            {
                pending = _pendingReceive;
                _pendingReceive = null;
            }

            pending?.TrySetException(exception);
        }

        public void Deliver(Envelope envelope)
        {
            byte[] bytes = WireCodec.Encode(envelope);
            TaskCompletionSource<ReadOnlyMemory<byte>>? pending;
            lock (_gate)
            {
                pending = _pendingReceive;
                if (pending != null)
                {
                    _pendingReceive = null;
                }
                else
                {
                    _inbound.Enqueue(bytes);
                }
            }

            pending?.TrySetResult(bytes);
        }

        public IReadOnlyList<Envelope> SentEnvelopes()
        {
            List<byte[]> copy;
            lock (_gate)
            {
                copy = new List<byte[]>(_sent);
            }

            var result = new List<Envelope>(copy.Count);
            for (int i = 0; i < copy.Count; i++)
            {
                WireDecodeResult decoded = WireCodec.Decode(copy[i]);
                Assert.True(decoded.IsOk);
                result.Add(decoded.Envelope!);
            }

            return result;
        }

        public int CountSent(MessageType type)
        {
            return SentEnvelopes().Count(e => e.MessageType == type);
        }

        public Envelope LastSent(MessageType type)
        {
            return SentEnvelopes().Last(e => e.MessageType == type);
        }
    }

    /// <summary>
    /// Minimal controllable session for the owner-publication ordering test.
    /// It is not a queue mirror: the queue drives it through the same
    /// IGameSession/ITableSessionFrameIngress contracts as TableSession.
    /// </summary>
    internal sealed class PublicationSession : IGameSession, ITableSessionFrameIngress
    {
        private bool _blockState;
        private TableConnectionState _state = TableConnectionState.Ready;

        public TaskCompletionSource<bool> RefreshEntered { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim ReleaseRefresh { get; } = new ManualResetEventSlim(false);

        public TaskCompletionSource<CommandSubmission> SubmitGate { get; } =
            new TaskCompletionSource<CommandSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> MutationEntered { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<TaskCompletionSource<ReadOnlyMemory<byte>>> Receives { get; } =
            new List<TaskCompletionSource<ReadOnlyMemory<byte>>>();

        public TaskCompletionSource<ReadOnlyMemory<byte>> Frame { get; } =
            new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);

        public TableConnectionState State
        {
            get
            {
                if (_blockState)
                {
                    RefreshEntered.TrySetResult(true);
                    ReleaseRefresh.Wait(TimeSpan.FromSeconds(10));
                }

                return _state;
            }
        }

        public Func<CancellationToken, bool>? BaselineAdmissionCheck { get; set; }

        public TableSessionBinding? Binding => null;

        public Mahjong.Contracts.StreamCursor? ConfirmedCursor => null;

        public PlayerSnapshot? PlayerSnapshot => null;

        public PublicSnapshot? PublicSnapshot => null;

        public RoomUpdate? RoomSnapshot => null;

        public IReadOnlyList<PendingCommandSnapshot> PendingCommands => Array.Empty<PendingCommandSnapshot>();

        public int EarlyBufferItemCount => 0;

        public long EarlyBufferByteCount => 0;

        public long EarlyBufferOverflowCount => 0;

        public string? LastError => null;

        public event EventHandler<TableSnapshotChangedEventArgs>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved
        {
            add { }
            remove { }
        }

        public Task OpenAsync(TableSessionOpenRequest request, CancellationToken cancellationToken)
        {
            _state = TableConnectionState.Handshaking;
            _blockState = true;
            return Task.CompletedTask;
        }

        public Task PumpOnceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CommandSubmission> SubmitAsync(ProtocolCommand command, CancellationToken cancellationToken)
        {
            MutationEntered.TrySetResult(true);
            return SubmitGate.Task;
        }

        public Task ReconcilePendingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CommandSubmission> RetryPendingAsync(IdempotencyKey key, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            _state = TableConnectionState.Reconnecting;
            _blockState = true;
            return Task.CompletedTask;
        }

        public Task CloseAsync(string reasonCode, CancellationToken cancellationToken)
        {
            _state = TableConnectionState.Closed;
            _blockState = true;
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>> ReceiveFrameAsync(CancellationToken cancellationToken)
        {
            Receives.Add(Frame);
            return Frame.Task;
        }

        public Task ApplyFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            _state = TableConnectionState.Ready;
            _blockState = true;
            return Task.CompletedTask;
        }

        public Task ApplyReceiveFaultAsync(Exception exception, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class TableSessionOperationQueueTests
    {
        private const int BindingEpoch = 11;
        private const string ScopeId = "scope-1";

        [Fact]
        public async Task ParkedReceiveDoesNotBlockSubmitAndCloseFencesLateFrame()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            int snapshotEvents = 0;
            int receiptEvents = 0;
            session.SnapshotChanged += (_, __) => snapshotEvents++;
            session.ReceiptResolved += (_, __) => receiptEvents++;

            channel.ResetReceiveSignal();
            Task parkedPump = queue.PumpOnceAsync(CancellationToken.None);
            await channel.ReceiveSignal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableConnectionState.Ready, queue.State);
            Assert.False(parkedPump.IsCompleted);

            ProtocolCommand command = TestWire.Command("request-parked");
            int saveBefore = store.SaveCount;
            CommandSubmission submission = await queue
                .SubmitAsync(command, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(submission.Sent);
            Assert.Equal(saveBefore + 1, store.SaveCount);
            Assert.Equal(1, channel.CountSent(MessageType.Command));
            Assert.Single(session.PendingCommands);
            Assert.False(parkedPump.IsCompleted);

            channel.Deliver(TestWire.AppliedReceipt(
                BindingEpoch,
                ScopeId,
                "request-parked",
                submission.Command.DigestSha256));
            await parkedPump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(session.PendingCommands);
            Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
            Assert.Equal(1, receiptEvents);

            using (var preCancelledClose = new CancellationTokenSource())
            {
                preCancelledClose.Cancel();
                TableSessionOperation rejected = queue.EnqueueClose("PRE_CANCELLED", preCancelledClose.Token);
                Assert.Equal(TableSessionOperationState.Canceled, rejected.State);
                Assert.Equal(0, channel.CloseCount);
            }

            channel.IgnoreReceiveCancellation = true;
            channel.CloseCompletesReceive = false;
            channel.ResetReceiveSignal();
            Task stuckPump = queue.PumpOnceAsync(CancellationToken.None);
            await channel.ReceiveSignal.WaitAsync(TimeSpan.FromSeconds(5));

            TableSessionOperation close = queue.EnqueueClose("TEST_CLOSE", CancellationToken.None);
            await close.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(queue.IsClosed);
            Assert.Equal(TableSessionOperationState.Completed, close.State);
            Assert.Equal(1, channel.CloseCount);
            Assert.True(stuckPump.IsCompleted);

            ContractCursor? cursorAtClose = session.ConfirmedCursor;
            int snapshotsAtClose = snapshotEvents;
            channel.Deliver(TestWire.PlayerPush(BindingEpoch, 5, 10, 6, 11));
            await stuckPump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cursorAtClose!.Value, session.ConfirmedCursor!.Value);
            Assert.Equal(snapshotsAtClose, snapshotEvents);

            TableSessionOperation duplicate = queue.EnqueueClose("TEST_CLOSE_2", CancellationToken.None);
            await duplicate.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, channel.CloseCount);
        }

        [Fact]
        public async Task CallerCancellationEndsIgnoringReceivePumpWithoutApplyingLateFrame()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            channel.IgnoreReceiveCancellation = true;
            channel.CloseCompletesReceive = false;
            channel.ResetReceiveSignal();
            using var cancellation = new CancellationTokenSource();
            Task pump = queue.PumpOnceAsync(cancellation.Token);
            await channel.ReceiveSignal.WaitAsync(TimeSpan.FromSeconds(5));
            ContractCursor? cursorBefore = session.ConfirmedCursor;
            int snapshotsBefore = 0;
            session.SnapshotChanged += (_, __) => snapshotsBefore++;

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            Assert.True(pump.IsCompleted);

            // A late fault from the abandoned receive must be observed and
            // must not reach the owner.
            channel.FaultPendingReceive(new TableChannelClosedException("LATE_RECEIVE_FAULT", false));
            Assert.Equal(cursorBefore!.Value, session.ConfirmedCursor!.Value);
            Assert.Equal(0, snapshotsBefore);
            Assert.Equal(TableConnectionState.Ready, queue.State);
        }

        [Fact]
        public async Task QueuedDisconnectFencesRunningReceiveAndNextPumpCanContinue()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            channel.IgnoreReceiveCancellation = true;
            channel.CloseCompletesReceive = false;
            channel.BlockSends();
            Task<CommandSubmission> submit = queue.SubmitAsync(
                TestWire.Command("queued-disconnect"),
                CancellationToken.None);
            await channel.SendSignal.WaitAsync(TimeSpan.FromSeconds(5));
            Task disconnect = queue.DisconnectAsync(CancellationToken.None);
            channel.ResetReceiveSignal();
            Task pump = queue.PumpOnceAsync(CancellationToken.None);
            await channel.ReceiveSignal.WaitAsync(TimeSpan.FromSeconds(5));
            ContractCursor cursorBefore = session.ConfirmedCursor!.Value;

            channel.ReleaseSends();
            await submit.WaitAsync(TimeSpan.FromSeconds(5));
            await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(pump.IsCompleted);

            channel.Deliver(TestWire.PlayerPush(BindingEpoch, 5, 10, 6, 11));
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cursorBefore, session.ConfirmedCursor!.Value);

            // The abandoned receive must not block a new connection cycle.
            await queue.OpenAsync(
                new TableSessionOpenRequest("ticket-reconnect", "client-build-1"),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            channel.ResetReceiveSignal();
            Task welcomePump = queue.PumpOnceAsync(CancellationToken.None);
            await channel.ReceiveSignal.WaitAsync(TimeSpan.FromSeconds(5));
            channel.Deliver(TestWire.Welcome(BindingEpoch + 1, ScopeId, 5, 10));
            await welcomePump.WaitAsync(TimeSpan.FromSeconds(5));

            channel.Deliver(TestWire.PlayerSync(
                BindingEpoch + 1,
                5,
                10,
                seatId: 0,
                correlationId: channel.LastSent(MessageType.SyncRequest).CorrelationId));
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableConnectionState.Ready, queue.State);
        }

        [Fact]
        public async Task OpenCompletionPublishesHandshakingBeforeCallerResumes()
        {
            var fake = new PublicationSession();
            var queue = new TableSessionOperationQueue(fake);
            Task open = queue.OpenAsync(
                new TableSessionOpenRequest("publication-open", "client-build-1"),
                CancellationToken.None);
            await fake.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(open.IsCompleted);

            fake.ReleaseRefresh.Set();
            await open.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableConnectionState.Handshaking, queue.State);
        }

        [Fact]
        public async Task ApplyCompletionPublishesOwnerStateBeforeCallerResumes()
        {
            var fake = new PublicationSession();
            var queue = new TableSessionOperationQueue(fake);
            Task pump = queue.PumpOnceAsync(CancellationToken.None);
            fake.Frame.SetResult(ReadOnlyMemory<byte>.Empty);
            await fake.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pump.IsCompleted);

            fake.ReleaseRefresh.Set();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableConnectionState.Ready, queue.State);
        }

        [Fact]
        public async Task CloseCompletionPublishesClosedBeforeCallerResumes()
        {
            var fake = new PublicationSession();
            var queue = new TableSessionOperationQueue(fake);
            Task close = queue.CloseAsync("PUBLICATION_CLOSE", CancellationToken.None);
            await fake.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(close.IsCompleted);

            fake.ReleaseRefresh.Set();
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableConnectionState.Closed, queue.State);
        }

        [Fact]
        public async Task DisconnectCompletionPublishesReconnectingBeforeCallerResumes()
        {
            var fake = new PublicationSession();
            var queue = new TableSessionOperationQueue(fake);
            Task disconnect = queue.DisconnectAsync(CancellationToken.None);

            await fake.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disconnect.IsCompleted);
            TableSessionOperation<CommandSubmission> next = queue.EnqueueSubmit(
                TestWire.Command("publication-next"),
                CancellationToken.None);
            Assert.False(fake.MutationEntered.Task.IsCompleted);

            fake.ReleaseRefresh.Set();
            fake.SubmitGate.SetResult(null!);
            await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
            await next.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TableConnectionState.Reconnecting, queue.State);
        }

        [Fact]
        public async Task CloseEndsParkedReceiveWhenChannelHonorsCancellation()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            channel.ResetReceiveSignal();
            Task parkedPump = queue.PumpOnceAsync(CancellationToken.None);
            await channel.ReceiveSignal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(parkedPump.IsCompleted);

            TableSessionOperation close = queue.EnqueueClose("TEST_CLOSE", CancellationToken.None);
            await close.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await parkedPump.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(queue.IsClosed);
            Assert.Equal(TableConnectionState.Closed, queue.State);
            Assert.Equal(TableSessionOperationState.Completed, close.State);
            Assert.Equal(1, channel.CloseCount);
            Assert.Empty(session.PendingCommands);
        }

        [Fact]
        public async Task ThousandSubmitReceiptAndPushInterleavingsKeepOneConsistentBaseline()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            int snapshotEvents = 1;
            int receiptEvents = 0;
            session.SnapshotChanged += (_, __) => snapshotEvents++;
            session.ReceiptResolved += (_, __) => receiptEvents++;
            int expectedSnapshotEvents = 1;
            ulong stream = 5;
            ulong projection = 10;
            var digests = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = 0; i < 1000; i++)
            {
                string requestId = "request-" + i;
                ProtocolCommand command = TestWire.Command(requestId);
                int savesBefore = store.SaveCount;
                CommandSubmission submission = await queue
                    .SubmitAsync(command, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(submission.Sent);
                Assert.Equal(requestId, submission.Command.RequestId);
                Assert.Equal(ScopeId, submission.Command.ScopeId);
                digests.Add(requestId, submission.Command.DigestSha256);
                Assert.Equal(savesBefore + 1, store.SaveCount);
                PendingCommandSnapshot pendingNow = Assert.Single(session.PendingCommands);
                Assert.Equal(requestId, pendingNow.RequestId);

                if (i % 13 == 0)
                {
                    CommandSubmission duplicate = await queue
                        .SubmitAsync(command, CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.True(duplicate.WasDuplicate);
                    Assert.False(duplicate.Sent);
                    Assert.Equal(savesBefore + 1, store.SaveCount);
                }

                if (i >= 2 && i % 7 == 0)
                {
                    // A receipt for an id that is not tracked yet is ignored,
                    // not applied optimistically.
                    channel.Deliver(TestWire.AppliedReceipt(
                        BindingEpoch,
                        ScopeId,
                        "request-" + (i + 2),
                        "future-digest"));
                    await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Single(session.PendingCommands);
                }

                if (i > 0 && i % 5 == 0)
                {
                    string previous = "request-" + (i - 1);
                    channel.Deliver(TestWire.AppliedReceipt(
                        BindingEpoch,
                        ScopeId,
                        previous,
                        digests[previous]));
                    await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Single(session.PendingCommands);
                }

                if (i % 3 == 0)
                {
                    channel.Deliver(TestWire.PlayerPush(BindingEpoch - 1, stream, projection, stream + 1, projection + 1));
                    await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(stream, session.ConfirmedCursor!.Value.StreamVersion.Value);
                    Assert.Equal(projection, session.ConfirmedCursor.Value.ProjectionSeq.Value);
                    Assert.Equal(expectedSnapshotEvents, snapshotEvents);
                }

                if (i % 11 == 0)
                {
                    stream++;
                    projection++;
                    expectedSnapshotEvents++;
                    channel.Deliver(TestWire.PlayerPush(BindingEpoch, stream - 1, projection - 1, stream, projection));
                    await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(stream, session.ConfirmedCursor!.Value.StreamVersion.Value);
                    Assert.Equal(projection, session.ConfirmedCursor.Value.ProjectionSeq.Value);
                    Assert.Equal(expectedSnapshotEvents, snapshotEvents);
                }

                int receiptsBefore = receiptEvents;
                channel.Deliver(TestWire.AppliedReceipt(
                    BindingEpoch,
                    ScopeId,
                    requestId,
                    submission.Command.DigestSha256));
                await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Empty(session.PendingCommands);
                Assert.Equal(receiptsBefore + 1, receiptEvents);

                if (i % 17 == 0)
                {
                    channel.Deliver(TestWire.AppliedReceipt(
                        BindingEpoch,
                        ScopeId,
                        requestId,
                        submission.Command.DigestSha256));
                    await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Empty(session.PendingCommands);
                    Assert.Equal(receiptsBefore + 1, receiptEvents);
                }
            }

            Assert.Empty(session.PendingCommands);
            Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
            Assert.Equal(TableConnectionState.Ready, queue.State);
            Assert.Equal(stream, session.ConfirmedCursor!.Value.StreamVersion.Value);
            Assert.Equal(projection, session.ConfirmedCursor.Value.ProjectionSeq.Value);
            Assert.Equal(expectedSnapshotEvents, snapshotEvents);
            Assert.Equal(1000, receiptEvents);
        }

        [Fact]
        public async Task ConcurrentSubmitsExecuteInSequenceOrderAndExposeLifecycle()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            const int count = 64;
            channel.BlockSends();
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestIds = new string[count];
            Task<TableSessionOperation<CommandSubmission>>[] enqueueTasks = Enumerable
                .Range(0, count)
                .Select(index => Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    string requestId = "concurrent-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    TableSessionOperation<CommandSubmission> enqueued = queue.EnqueueSubmit(
                        TestWire.Command(requestId),
                        CancellationToken.None);
                    requestIds[index] = requestId;
                    return enqueued;
                }))
                .ToArray();

            start.SetResult(true);
            TableSessionOperation<CommandSubmission>[] handles = await Task
                .WhenAll(enqueueTasks)
                .WaitAsync(TimeSpan.FromSeconds(10));
            var requestIdByHandle = new Dictionary<TableSessionOperation<CommandSubmission>, string>();
            for (int i = 0; i < handles.Length; i++)
            {
                requestIdByHandle.Add(handles[i], requestIds[i]);
            }
            await channel.SendSignal.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(count, handles.Select(h => h.Sequence).Distinct().Count());
            TableSessionOperation<CommandSubmission>[] ordered = handles.OrderBy(h => h.Sequence).ToArray();
            Assert.Equal(TableSessionOperationState.Running, ordered[0].State);
            for (int i = 1; i < ordered.Length; i++)
            {
                Assert.Equal(TableSessionOperationState.Queued, ordered[i].State);
            }

            using (var queuedCancellation = new CancellationTokenSource())
            {
                TableSessionOperation<CommandSubmission> queuedCancelled = queue.EnqueueSubmit(
                    TestWire.Command("queued-cancel"),
                    queuedCancellation.Token);
                Assert.Equal(TableSessionOperationState.Queued, queuedCancelled.State);
                queuedCancellation.Cancel();
                Assert.Equal(TableSessionOperationState.Canceled, queuedCancelled.State);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await queuedCancelled.Completion.ConfigureAwait(false));
            }

            channel.ReleaseSends();
            await Task.WhenAll(handles.Select(h => h.Completion))
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(handles, h => Assert.Equal(TableSessionOperationState.Completed, h.State));

            List<string> sentIds = channel
                .SentEnvelopes()
                .Where(e => e.MessageType == MessageType.Command)
                .Select(e => e.RequestId)
                .ToList();
            List<string> expectedIds = ordered
                .Select(h => requestIdByHandle[h])
                .ToList();
            Assert.Equal(expectedIds, sentIds);
            Assert.Equal(count, store.SaveCount);

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                int savesBefore = store.SaveCount;
                int sendsBefore = channel.CountSent(MessageType.Command);
                TableSessionOperation<CommandSubmission> preCancelled = queue.EnqueueSubmit(
                    TestWire.Command("pre-cancelled"),
                    cancelled.Token);
                Assert.Equal(TableSessionOperationState.Canceled, preCancelled.State);
                Assert.True(preCancelled.Completion.IsCanceled);
                Assert.Equal(-1, preCancelled.Sequence);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await preCancelled.Completion.ConfigureAwait(false));
                Assert.Equal(savesBefore, store.SaveCount);
                Assert.Equal(sendsBefore, channel.CountSent(MessageType.Command));
            }
        }

        [Fact]
        public async Task CallbackReentrancyAndOperationFaultsDoNotPoisonTheOwnerQueue()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            var callbackSubmit = new TaskCompletionSource<CommandSubmission>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int receiptEvents = 0;
            session.ReceiptResolved += async (_, __) =>
            {
                if (Interlocked.Increment(ref receiptEvents) != 1)
                {
                    return;
                }

                try
                {
                    CommandSubmission nested = await queue
                        .SubmitAsync(TestWire.Command("from-callback"), CancellationToken.None)
                        .ConfigureAwait(false);
                    callbackSubmit.TrySetResult(nested);
                }
                catch (Exception exception)
                {
                    callbackSubmit.TrySetException(exception);
                }
            };

            CommandSubmission first = await queue
                .SubmitAsync(TestWire.Command("callback-trigger"), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            channel.Deliver(TestWire.AppliedReceipt(
                BindingEpoch,
                ScopeId,
                "callback-trigger",
                first.Command.DigestSha256));
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            CommandSubmission nested = await callbackSubmit.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(nested.Sent);
            Assert.Equal(2, channel.CountSent(MessageType.Command));
            Assert.Single(session.PendingCommands);
            Assert.Equal("from-callback", session.PendingCommands[0].RequestId);

            TableSessionQueueSnapshot captured = await queue
                .CaptureAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableConnectionState.Ready, captured.State);
            Assert.Single(captured.PendingCommands);
            Assert.Equal("from-callback", captured.PendingCommands[0].RequestId);
            Assert.Equal(session.ConfirmedCursor, captured.ConfirmedCursor);

            store.ThrowOnSave = true;
            TableSessionOperation<CommandSubmission> faulted = queue.EnqueueSubmit(
                TestWire.Command("store-fault"),
                CancellationToken.None);
            await Assert.ThrowsAsync<System.IO.IOException>(
                async () => await faulted.Completion.ConfigureAwait(false));
            Assert.Equal(TableSessionOperationState.Faulted, faulted.State);
            Assert.Equal(TableConnectionState.Ready, queue.State);

            store.ThrowOnSave = false;
            CommandSubmission recovered = await queue
                .SubmitAsync(TestWire.Command("after-fault"), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(recovered.Sent);

            channel.ThrowClosedOnNextReceive = true;
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableConnectionState.Reconnecting, queue.State);

            await queue.OpenAsync(
                new TableSessionOpenRequest("ticket-recovery", "client-build-1"),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            channel.Deliver(TestWire.Welcome(BindingEpoch, ScopeId, 5, 10));
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            channel.Deliver(TestWire.PlayerSync(
                BindingEpoch,
                5,
                10,
                seatId: 0,
                correlationId: channel.LastSent(MessageType.SyncRequest).CorrelationId));
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TableConnectionState.Ready, queue.State);
            CommandSubmission afterReconnect = await queue
                .SubmitAsync(TestWire.Command("after-reconnect"), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(afterReconnect.Sent);
        }

        [Fact]
        public async Task InboundApplyWaitsForRunningSubmitAndThenAdvancesInOrder()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            int snapshotEvents = 0;
            session.SnapshotChanged += (_, __) => snapshotEvents++;
            channel.BlockSends();
            Task<CommandSubmission> submit = queue.SubmitAsync(
                TestWire.Command("ordered-submit"),
                CancellationToken.None);
            await channel.SendSignal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableConnectionState.Ready, queue.State);

            channel.Deliver(TestWire.PlayerPush(BindingEpoch, 5, 10, 6, 11));
            Task pump = queue.PumpOnceAsync(CancellationToken.None);
            Assert.False(pump.IsCompleted);
            Assert.Equal(5UL, session.ConfirmedCursor!.Value.StreamVersion.Value);
            Assert.Equal(0, snapshotEvents);

            channel.ReleaseSends();
            await submit.WaitAsync(TimeSpan.FromSeconds(5));
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(6UL, session.ConfirmedCursor!.Value.StreamVersion.Value);
            Assert.Equal(11UL, session.ConfirmedCursor.Value.ProjectionSeq.Value);
            Assert.Equal(1, snapshotEvents);
            Assert.Equal(1, store.SaveCount);
            Assert.Equal(1, channel.CountSent(MessageType.Command));
        }

        [Fact]
        public async Task CancellationAfterDurableSaveKeepsIdentityAndReconcilesById()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            int receiptEvents = 0;
            bool fromLookup = false;
            session.ReceiptResolved += (_, args) =>
            {
                receiptEvents++;
                fromLookup = args.Resolution.FromReceiptLookup;
            };

            channel.BlockSends();
            using var cancellation = new CancellationTokenSource();
            Task<CommandSubmission> submit = queue.SubmitAsync(
                TestWire.Command("request-cancelled-after-save"),
                cancellation.Token);
            await channel.SendSignal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, store.SaveCount);
            PendingCommandSnapshot pendingBefore = Assert.Single(session.PendingCommands);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await submit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));

            PendingCommandSnapshot pendingAfter = Assert.Single(session.PendingCommands);
            Assert.Equal("request-cancelled-after-save", pendingAfter.RequestId);
            Assert.Equal(pendingBefore.ScopeId, pendingAfter.ScopeId);
            Assert.Equal(pendingBefore.DigestSha256, pendingAfter.DigestSha256);
            Assert.True(pendingAfter.DeliveryUnknown);
            Assert.True(pendingAfter.NeedsReconciliation);
            Assert.Single(await store.LoadUnresolvedAsync(CancellationToken.None));

            channel.ReleaseSends();
            await queue.ReconcilePendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, channel.CountSent(MessageType.ReceiptQuery));
            Assert.Equal(0, channel.CountSent(MessageType.Command));
            Envelope query = channel.LastSent(MessageType.ReceiptQuery);
            Assert.Equal("request-cancelled-after-save", query.ReceiptQuery.RequestId);
            Assert.Equal(pendingAfter.ScopeId, query.ReceiptQuery.RequestScopeId);

            Envelope applied = TestWire.AppliedReceipt(
                BindingEpoch,
                pendingAfter.ScopeId,
                pendingAfter.RequestId,
                pendingAfter.DigestSha256);
            channel.Deliver(TestWire.ReceiptLookup(
                query.CorrelationId,
                LookupStatus.Found,
                applied.Receipt,
                pendingAfter.DigestSha256));
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(session.PendingCommands);
            Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
            Assert.Equal(1, receiptEvents);
            Assert.True(fromLookup);
        }

        [Fact]
        public async Task ReconnectAppliesBaselineBeforeQueryingAndNeverResendsBlindly()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            var order = new List<string>();
            session.SnapshotChanged += (_, args) =>
            {
                if (args.IsFullBaseline)
                {
                    order.Add("baseline");
                }
            };
            channel.MessageSent += type =>
            {
                if (type == MessageType.ReceiptQuery)
                {
                    order.Add("receipt-query");
                }
            };

            channel.FailNextSend = true;
            CommandSubmission failed = await queue
                .SubmitAsync(TestWire.Command("request-unknown"), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(failed.Sent);
            PendingCommandSnapshot unknown = Assert.Single(session.PendingCommands);
            Assert.True(unknown.DeliveryUnknown);
            Assert.True(unknown.NeedsReconciliation);
            Assert.Equal(TableConnectionState.Reconnecting, queue.State);
            Assert.Equal(0, channel.CountSent(MessageType.Command));

            await queue.OpenAsync(
                new TableSessionOpenRequest("ticket-reconnect", "client-build-1"),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            channel.Deliver(TestWire.Welcome(BindingEpoch + 1, ScopeId, 5, 10));
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            channel.Deliver(TestWire.PlayerSync(
                BindingEpoch + 1,
                5,
                10,
                seatId: 0,
                correlationId: channel.LastSent(MessageType.SyncRequest).CorrelationId));
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(TableConnectionState.Ready, queue.State);
            Assert.Equal(1, channel.CountSent(MessageType.ReceiptQuery));
            Assert.Equal(0, channel.CountSent(MessageType.Command));
            Assert.Equal(new[] { "baseline", "receipt-query" }, order);
            PendingCommandSnapshot queried = Assert.Single(session.PendingCommands);
            Assert.Equal(unknown.RequestId, queried.RequestId);
            Assert.Equal(unknown.ScopeId, queried.ScopeId);
            Assert.Equal(unknown.DigestSha256, queried.DigestSha256);

            Envelope query = channel.LastSent(MessageType.ReceiptQuery);
            Assert.Equal(unknown.RequestId, query.ReceiptQuery.RequestId);
            Assert.Equal(unknown.ScopeId, query.ReceiptQuery.RequestScopeId);

            channel.Deliver(TestWire.ReceiptLookup(
                query.CorrelationId,
                LookupStatus.NotFound,
                null,
                unknown.DigestSha256));
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(session.PendingCommands);
            Assert.Equal(0, channel.CountSent(MessageType.Command));

            CommandSubmission retried = await queue
                .RetryPendingAsync(queried.Key, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(retried.Sent);
            Assert.Equal(unknown.RequestId, retried.Command.RequestId);
            Assert.Equal(unknown.ScopeId, retried.Command.ScopeId);
            Assert.Equal(unknown.DigestSha256, retried.Command.DigestSha256);
            Envelope resent = channel.LastSent(MessageType.Command);
            Assert.Equal(unknown.RequestId, resent.RequestId);
            Assert.Equal(unknown.ScopeId, resent.Scope.ScopeId);
            Assert.Equal(
                unknown.DigestSha256,
                CanonicalPayloadDigest.Compute(resent.Command.CanonicalPayload.Span).ToHex());
            Assert.Single(await store.LoadUnresolvedAsync(CancellationToken.None));

            channel.Deliver(TestWire.AppliedReceipt(
                BindingEpoch + 1,
                unknown.ScopeId,
                unknown.RequestId,
                unknown.DigestSha256));
            await queue.PumpOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(session.PendingCommands);
            Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));
        }

        [Fact]
        public async Task QueuedDisconnectFencesReceiveFaultWithoutDeadlock()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            channel.BlockSends();
            Task<CommandSubmission> submit = queue.SubmitAsync(
                TestWire.Command("fault-fence"),
                CancellationToken.None);
            await channel.SendSignal.WaitAsync(TimeSpan.FromSeconds(5));
            Task disconnect = queue.DisconnectAsync(CancellationToken.None);
            channel.ThrowClosedOnNextReceive = true;
            Task pump = queue.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(2, queue.PendingOperationCount);

            channel.ReleaseSends();
            await submit.WaitAsync(TimeSpan.FromSeconds(5));
            await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
            await pump.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(pump.IsCompleted);
            Assert.Equal(TableConnectionState.Reconnecting, queue.State);
        }

        [Fact]
        public async Task CanceledPumpsRetainAtMostOneUnfinishedReceivePerBinding()
        {
            var fake = new PublicationSession();
            var queue = new TableSessionOperationQueue(fake);
            try
            {
                for (int i = 0; i < 32; i++)
                {
                    using var cancellation = new CancellationTokenSource();
                    Task pump = queue.PumpOnceAsync(cancellation.Token);
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        async () => await pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
                }

                Assert.True(
                    fake.Receives.Count(receive => !receive.Task.IsCompleted) <= 1,
                    "Canceled pumps must retain at most one unfinished transport receive for one binding.");
            }
            finally
            {
                foreach (TaskCompletionSource<ReadOnlyMemory<byte>> receive in fake.Receives)
                {
                    receive.TrySetResult(ReadOnlyMemory<byte>.Empty);
                }
            }
        }

        [Fact]
        public async Task RunnerDrivesSplitIngressAndAcceptsSubmitWhileReceiveIsParked()
        {
            var channel = new DeterministicTableChannel { AutoRespondToHandshake = true };
            var store = new RecordingOutboxStore();
            var session = new TableSession(channel, store);
            var queue = new TableSessionOperationQueue(session);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.SnapshotChanged += (_, args) =>
            {
                if (args.IsFullBaseline)
                {
                    ready.TrySetResult(true);
                }
            };

            var runner = new TableSessionRunner(
                session,
                new DelegateTableTicketProvider((_, __) => Task.FromResult("runner-ticket")),
                new TableSessionRunnerOptions
                {
                    InitialBackoff = TimeSpan.FromMilliseconds(10),
                    MaxBackoff = TimeSpan.FromMilliseconds(50),
                    MaxReconnectAttempts = 0,
                    HandshakeTimeout = TimeSpan.FromSeconds(5)
                },
                queue);
            using var cancellation = new CancellationTokenSource();
            Task<TableSessionRunResult> running = runner.RunAsync(cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!channel.HasPendingReceive)
            {
                int target = channel.ParkedReceiveCount + 1;
                await channel.WaitForParkedReceivesAsync(target).WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.Equal(TableConnectionState.Ready, queue.State);

            CommandSubmission submission = await queue
                .SubmitAsync(TestWire.Command("runner-while-parked"), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(submission.Sent);
            Assert.Equal(1, channel.CountSent(MessageType.Command));
            Assert.False(running.IsCompleted);

            // The channel deliberately ignores cancellation. The logical pump
            // must still end without a Deliver, or RunAsync can never return.
            channel.IgnoreReceiveCancellation = true;
            cancellation.Cancel();
            TableSessionRunResult result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableSessionRunStop.Canceled, result.Stop);
        }

        [Fact]
        public async Task RunnerStopsWhenQueueClosesAnIgnoringReceive()
        {
            var channel = new DeterministicTableChannel { AutoRespondToHandshake = true };
            var store = new RecordingOutboxStore();
            var session = new TableSession(channel, store);
            var queue = new TableSessionOperationQueue(session);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.SnapshotChanged += (_, args) =>
            {
                if (args.IsFullBaseline)
                {
                    ready.TrySetResult(true);
                }
            };

            var runner = new TableSessionRunner(
                session,
                new DelegateTableTicketProvider((_, __) => Task.FromResult("runner-ticket")),
                new TableSessionRunnerOptions
                {
                    InitialBackoff = TimeSpan.FromMilliseconds(10),
                    MaxBackoff = TimeSpan.FromMilliseconds(50),
                    MaxReconnectAttempts = 0,
                    HandshakeTimeout = TimeSpan.FromSeconds(5)
                },
                queue);
            Task<TableSessionRunResult> running = runner.RunAsync(CancellationToken.None);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!channel.HasPendingReceive)
            {
                int target = channel.ParkedReceiveCount + 1;
                await channel.WaitForParkedReceivesAsync(target).WaitAsync(TimeSpan.FromSeconds(5));
            }

            channel.IgnoreReceiveCancellation = true;
            channel.CloseCompletesReceive = false;
            await queue.CloseAsync("RUNNER_CLOSE", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            TableSessionRunResult result = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TableSessionRunStop.Closed, result.Stop);
        }

        [Fact]
        public async Task ParkedReceiveIsFencedByDisconnect()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            channel.IgnoreReceiveCancellation = true;
            channel.CloseCompletesReceive = false;
            channel.ResetReceiveSignal();
            Task pump = queue.PumpOnceAsync(CancellationToken.None);
            await channel.ReceiveSignal.WaitAsync(TimeSpan.FromSeconds(5));
            ContractCursor cursorBefore = session.ConfirmedCursor!.Value;

            await queue.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(pump.IsCompleted);
            channel.Deliver(TestWire.PlayerPush(BindingEpoch, 5, 10, 6, 11));
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cursorBefore, session.ConfirmedCursor!.Value);
        }

        [Fact]
        public async Task SpectatorAdmissionFailsBeforePersistenceAndSend()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectSpectatorAsync();
            int savesBefore = store.SaveCount;
            int sendsBefore = channel.CountSent(MessageType.Command);

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await queue
                    .SubmitAsync(TestWire.Command("spectator-seat-action"), CancellationToken.None)
                    .ConfigureAwait(false));

            Assert.Equal(savesBefore, store.SaveCount);
            Assert.Equal(sendsBefore, channel.CountSent(MessageType.Command));
            Assert.Empty(session.PendingCommands);
            Assert.Empty(await store.LoadUnresolvedAsync(CancellationToken.None));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            TableSessionOperation<CommandSubmission> preCancelled = queue.EnqueueSubmit(
                TestWire.Command("spectator-pre-cancelled"),
                cancelled.Token);
            Assert.Equal(TableSessionOperationState.Canceled, preCancelled.State);
            Assert.Equal(savesBefore, store.SaveCount);
            Assert.Equal(sendsBefore, channel.CountSent(MessageType.Command));
        }

        [Fact]
        public async Task CloseCancelsQueuedSubmitsAndIsIdempotent()
        {
            (TableSession session, TableSessionOperationQueue queue, DeterministicTableChannel channel, RecordingOutboxStore store) =
                await ConnectAsync();
            channel.BlockSends();
            int savesBefore = store.SaveCount;
            Task<CommandSubmission> running = queue.SubmitAsync(
                TestWire.Command("close-running"),
                CancellationToken.None);
            await channel.SendSignal.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = new List<TableSessionOperation<CommandSubmission>>();
            for (int i = 0; i < 8; i++)
            {
                queued.Add(queue.EnqueueSubmit(
                    TestWire.Command("close-queued-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    CancellationToken.None));
            }

            TableSessionOperation close = queue.EnqueueClose("TEST_CLOSE", CancellationToken.None);
            Assert.True(queue.IsClosing);
            Assert.False(close.Completion.IsCompleted);
            Assert.All(queued, operation => Assert.Equal(TableSessionOperationState.Canceled, operation.State));

            channel.ReleaseSends();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            await close.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(queue.IsClosed);
            Assert.Equal(savesBefore + 1, store.SaveCount);
            Assert.Equal(1, channel.CountSent(MessageType.Command));
            Assert.Equal(1, channel.CloseCount);
            Assert.Single(session.PendingCommands);

            TableSessionOperation duplicate = queue.EnqueueClose("TEST_CLOSE_2", CancellationToken.None);
            Assert.Same(close, duplicate);
            await duplicate.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, channel.CloseCount);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            TableSessionOperation preCancelled = queue.EnqueueClose("TEST_CLOSE_3", cancelled.Token);
            Assert.Equal(TableSessionOperationState.Canceled, preCancelled.State);
            Assert.Equal(1, channel.CloseCount);
        }

        private static async Task<(TableSession, TableSessionOperationQueue, DeterministicTableChannel, RecordingOutboxStore)> ConnectAsync()
        {
            var channel = new DeterministicTableChannel();
            var store = new RecordingOutboxStore();
            var session = new TableSession(channel, store);
            var queue = new TableSessionOperationQueue(session);

            await queue.OpenAsync(
                new TableSessionOpenRequest("ticket-1", "client-build-1"),
                CancellationToken.None).ConfigureAwait(false);
            channel.Deliver(TestWire.Welcome(BindingEpoch, ScopeId, 5, 10));
            await queue.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            channel.Deliver(TestWire.PlayerSync(
                BindingEpoch,
                5,
                10,
                seatId: 0,
                correlationId: channel.LastSent(MessageType.SyncRequest).CorrelationId));
            await queue.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.Equal(TableConnectionState.Ready, queue.State);
            return (session, queue, channel, store);
        }

        private static async Task<(TableSession, TableSessionOperationQueue, DeterministicTableChannel, RecordingOutboxStore)> ConnectSpectatorAsync()
        {
            var channel = new DeterministicTableChannel();
            var store = new RecordingOutboxStore();
            var session = new TableSession(channel, store);
            var queue = new TableSessionOperationQueue(session);

            await queue.OpenAsync(
                new TableSessionOpenRequest("ticket-1", "client-build-1"),
                CancellationToken.None).ConfigureAwait(false);
            channel.Deliver(TestWire.Welcome(BindingEpoch, ScopeId, 5, 10, seatId: -1, role: Role.Spectator));
            await queue.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            channel.Deliver(TestWire.PublicSync(
                BindingEpoch,
                5,
                10,
                channel.LastSent(MessageType.SyncRequest).CorrelationId));
            await queue.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.Equal(TableConnectionState.Ready, queue.State);
            return (session, queue, channel, store);
        }
    }
}
