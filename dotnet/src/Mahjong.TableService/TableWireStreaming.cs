using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;

namespace Mahjong.TableService
{
    /// <summary>
    /// Host-side strict codec: turns an actor-frozen Contracts projection frame into exactly
    /// one wire Envelope and its protobuf bytes through ProjectionWireMapper + WireCodec only.
    /// It performs no Domain reads, no reflection and no serialization-then-strip: the actor
    /// already produced the audience-correct snapshot (public for spectators, public+private
    /// for the seat), and unknown fields are preserved by the protobuf parser rather than
    /// rejected.
    /// </summary>
    public sealed class WireTableStreamContractCodec : ITableStreamContractCodec
    {
        public byte[] Encode(TableStreamContractFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            var envelope = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = frame.Public != null ? MessageType.PublicUpdate : MessageType.PlayerUpdate,
                SessionId = frame.SessionId.Value,
                RoomId = frame.RoomId,
                SeatId = frame.Audience.Seat,
                BindingEpoch = frame.BindingEpoch.Value,
                Cursor = ProjectionWireMapper.MapCursor(frame.Cursor)
            };

            if (frame.Public != null)
            {
                envelope.PublicUpdate = ProjectionWireMapper.MapPublicUpdate(
                    frame.Public,
                    frame.Events,
                    frame.FromCursor,
                    frame.IsFullSnapshot);
            }
            else
            {
                envelope.PlayerUpdate = ProjectionWireMapper.MapPlayerUpdate(
                    frame.Player!,
                    frame.Events,
                    frame.FromCursor,
                    frame.IsFullSnapshot);
            }

            WireCodec.EnsureStrict(envelope);
            return WireCodec.Encode(envelope);
        }
    }

    /// <summary>One leased stream frame whose bytes are still inside the sink budget.</summary>
    public sealed class WireStreamFrameLease
    {
        private readonly WireTableStreamSink _sink;
        private int _released;

        internal WireStreamFrameLease(WireTableStreamSink sink, byte[] payload)
        {
            _sink = sink;
            Payload = payload;
        }

        public byte[] Payload { get; }

        public int Length => Payload.Length;

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        /// <summary>
        /// Releases the frame's reserved bytes back to the sink budget. Exactly once: send
        /// completion, a send fault and cancellation all funnel through this single release.
        /// </summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _sink.ReleaseInflight(Payload.Length);
            }
        }
    }

    /// <summary>
    /// One connection's outbound stream boundary. The actor calls <see cref="PublishAsync"/>
    /// from its per-stream pump with already-encoded frames; the sink forwards the exact bytes
    /// into a bounded channel drained by the connection's WebSocket send loop. A client that
    /// stops reading backpressures this channel, then the actor's bounded queue, and finally
    /// terminates the stream as a slow consumer — the actor-side accounting stays the single
    /// trusted bound. The actor's pump starts inside SubscribeStreamAsync, so the initial
    /// snapshot can arrive before the host binds the returned handle; pre-bind frames are the
    /// subscription's own frames by construction and only post-bind mismatches fault. Nothing
    /// here mutates Domain state and no frame is queued before the actor encoded it.
    ///
    /// Byte accounting (D-1-07): the sink owns TWO concatenated ledgers for its whole budget —
    /// bytes queued in this host channel plus bytes takeIN by the connection's send path that
    /// have NOT yet been handed to the actual transport. A dequeue therefore does NOT free the
    /// budget: it MOVES the reservation from the queued ledger into the in-flight ledger, and
    /// the in-flight reservation is released only when the producer-side send path actually
    /// completed (success, fault or cancellation) through <see cref="WireStreamFrameLease"/>.
    /// A sink that would exceed its queued-byte budget — with in-flight bytes counted — or a
    /// full snapshot above the negotiated snapshot limit terminates the stream instead of
    /// queuing silently.
    ///
    /// D-1-02: every owner stream frame also feeds the trusted beam-swap offer context — the
    /// candidates come from the actor's own owner projection (the same immutable offer state
    /// the Domain validates every choice against), so the mapper resolves a raw target slot
    /// through a REAL offer context instead of guessing a slot kind by numeric range.
    /// </summary>
    public sealed class WireTableStreamSink : ITableStreamSink
    {
        private readonly Channel<byte[]> _outbound;
        private readonly int _maxQueuedBytes;
        private readonly int _maxSnapshotBytes;
        private TableStreamHandle? _handle;
        private volatile TableStreamTermination? _termination;
        private readonly object _captureGate = new object();
        private readonly object _terminationGate = new object();
        private bool _captureNextFullSnapshot;
        private TaskCompletionSource<TableStreamFrame>? _captureSource;
        private TableStreamFrame? _capturedFullSnapshot;
        private bool _terminating;

        /// <summary>Mutable bytes still held in this host channel; only advanced under the gate.</summary>
        private int _queuedBytes;

        /// <summary>Mutable bytes taken by the send path but not yet handed to the transport (D-1-07).</summary>
        private int _inflightBytes;

        /// <summary>Mutable bytes sent and accounted; subtracted from this ledger.</summary>
        private long _sentBytes;

        public WireTableStreamSink(int capacity, int maxQueuedBytes, int maxSnapshotBytes)
        {
            _outbound = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(Math.Max(1, capacity))
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait
                });
            if (maxQueuedBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));
            }

            if (maxSnapshotBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes));
            }

            _maxQueuedBytes = maxQueuedBytes;
            _maxSnapshotBytes = maxSnapshotBytes;
        }

        public TableStreamHandle? Handle => _handle;

        /// <summary>Total un-released bytes under this sink's budget (queued + in flight).</summary>
        public int QueuedBytes => Volatile.Read(ref _queuedBytes) + Volatile.Read(ref _inflightBytes);

        public int MaxQueuedBytes => _maxQueuedBytes;

        public int MaxSnapshotBytes => _maxSnapshotBytes;

        public long SentBytes => Volatile.Read(ref _sentBytes);

        public TableStreamTermination? Termination => _termination;

        /// <summary>Raised once from <see cref="NotifyTerminated"/> (actor pump thread).</summary>
        public Action<TableStreamTermination>? Terminated { get; set; }

        /// <summary>Called exactly once, with the handle returned by SubscribeStreamAsync.</summary>
        public void Bind(TableStreamHandle handle)
        {
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            if (Interlocked.CompareExchange(ref _handle, handle, null) != null)
            {
                throw new InvalidOperationException("A stream sink was bound twice.");
            }
        }

        public bool Owns(TableStreamHandle handle) =>
            handle != null && _handle != null && _handle.Equals(handle);

        public ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            // The actor's pump starts inside SubscribeStreamAsync, so the initial snapshot can
            // be published before the host binds the returned handle; a null _handle is that
            // legitimate pre-bind window (the actor only ever pumps this subscription's own
            // frames into this sink). After binding, any identity mismatch is a host wiring
            // bug and must fail loudly instead of crossing seats; it is never client-reachable.
            TableStreamHandle? bound = _handle;
            if (bound != null && !frame.Handle.Equals(bound))
            {
                throw new InvalidOperationException("A stream frame arrived for a different identity.");
            }

            // D-1-02: the trusted offer candidate context follows the actor's own owner
            // projection; it rides only THIS connection's frames (the identity check above
            // proves the frame belongs to this connection's seat/audience).
            if (frame.Player != null && frame.OwnerPrivate != null && frame.OwnerPrivate.Cheat.BeamSwapOffer != null)
            {
                NoteBeamSwapOffer(frame.OwnerPrivate.Cheat.BeamSwapOffer);
            }

            // Observe-for-sync runs first; it never diverts the frame (always false today,
            // kept as the seam so the bounded write below remains the only delivery path).
            TryCapture(frame);

            lock (this)
            {
                if (_terminating)
                {
                    // The stream is already closed host-side (over budget / oversize snapshot);
                    // the actor's pump drains its own outbox, but nothing more is queued here.
                    return ValueTask.CompletedTask;
                }
            }

            byte[] payload = frame.EncodedPayload ??
                throw new InvalidOperationException("A stream frame arrived without an encoded payload.");

            // D-1-07: the negotiated snapshot limit is executed on the hot path — an encoded
            // frame that carries the complete authoritative snapshot is refused with a mapped
            // termination, never silently truncated or sent in an envelope the negotiated
            // limit says cannot appear on this channel. Round 3: the frozen P3.2 stream
            // contract is "PUSH always contains the complete snapshot plus its visible
            // events"; every actor frame therefore carries the full state, and the limit is
            // enforced for EVERY frame — the initial baseline AND every non-baseline update
            // (a mid-stream update frame is exactly as oversized as its baseline twin). The
            // per-frame limit stays separate from the accumulated queued+in-flight budget:
            // one legitimate big frame above the negotiated per-frame cap is MessageTooLarge,
            // while budget exhaustion (many small frames) stays SlowConsumer.
            if (payload.Length > _maxSnapshotBytes)
            {
                Terminate(
                    TableStreamTerminationKind.MessageTooLarge,
                    ProtocolErrorCode.MessageTooLarge,
                    "an encoded stream frame is " + payload.Length + " bytes, above the negotiated " +
                    _maxSnapshotBytes + " byte snapshot limit");
                return ValueTask.CompletedTask;
            }

            int length = payload.Length;
            lock (this)
            {
                if (_terminating)
                {
                    return ValueTask.CompletedTask;
                }

                if (_queuedBytes + Volatile.Read(ref _inflightBytes) + length > _maxQueuedBytes)
                {
                    // Budget exceeded WITH in-flight bytes counted: terminate as a slow
                    // consumer before queuing anything more. The accounting stays exact
                    // because the reservation was never made.
                    Terminate(
                        TableStreamTerminationKind.SlowConsumer,
                        ProtocolErrorCode.SlowConsumer,
                        "the host stream channel would exceed its " + _maxQueuedBytes +
                        " byte budget (" + _queuedBytes + " queued + " + Volatile.Read(ref _inflightBytes) +
                        " in flight + " + length + " framed)");
                    return ValueTask.CompletedTask;
                }

                // Reserve before the async write so the budget can never cover more bytes
                // than the sink actually holds.
                _queuedBytes += length;
            }

            return new ValueTask(WriteAndReserveAsync(payload, length, cancellationToken));
        }

        private async Task WriteAndReserveAsync(
            byte[] payload,
            int length,
            CancellationToken cancellationToken)
        {
            try
            {
                await _outbound.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The write did not land (cancelled/closed channel): the reservation is void.
                ReleaseQueued(length);
                throw;
            }
        }

        private void ReleaseQueued(int length)
        {
            if (length == 0)
            {
                return;
            }

            lock (this)
            {
                if (_queuedBytes >= length)
                {
                    _queuedBytes -= length;
                }
            }
        }

        /// <summary>Moves a dequeued frame's reservation from the queued ledger to in flight.</summary>
        private void MoveToInflight(int length)
        {
            lock (this)
            {
                if (_queuedBytes >= length)
                {
                    _queuedBytes -= length;
                    _inflightBytes += length;
                }
                else
                {
                    _inflightBytes += length;
                }
            }
        }

        /// <summary>Frees an in-flight reservation once the send path finished the frame.</summary>
        internal void ReleaseInflight(int length)
        {
            lock (this)
            {
                if (_inflightBytes >= length)
                {
                    _inflightBytes -= length;
                }

                _sentBytes += length;
            }
        }

        /// <summary>
        /// Test/seam variant: installs an offer context directly (test-only wiring surface).
        /// </summary>
        public void NoteBeamSwapOfferContext(TableBeamSwapOfferContext offer)
        {
            Volatile.Write(ref _beamOffer, offer);
        }

        public void NoteBeamSwapOffer(TableOwnerBeamSwapOfferView offer)
        {
            var candidates = new List<TableBeamSwapCandidate>(offer.Candidates.Count);
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                TableOwnerBeamSwapCandidateView candidate = offer.Candidates[i];
                candidates.Add(new TableBeamSwapCandidate(candidate.SlotKind, candidate.SlotIndex));
            }

            Volatile.Write(
                ref _beamOffer,
                new TableBeamSwapOfferContext(offer.OfferId, offer.Window, candidates));
        }

        private TableBeamSwapOfferContext? _beamOffer;

        /// <summary>
        /// D-1-02: resolves a raw wire slot index against the trusted actor's pending beam
        /// swap offer. Exactly one candidate with that index resolves to its REAL kind; an
        /// index whose namespace crosses several candidate kinds (dead wall source and
        /// replacement source share small indices by design) is NEVER silently mapped to any
        /// one of them — it is refused with the explicit boundary error. Unknown indices are
        /// also refused; the Domain re-verifies whatever this returns.
        /// </summary>
        public TableBeamSwapSlotResolution? ResolveBeamSwapTargetSlot(uint rawIndex)
        {
            TableBeamSwapOfferContext? offer = Volatile.Read(ref _beamOffer);
            if (offer == null || offer.Candidates.Count == 0)
            {
                return null;
            }

            int matches = 0;
            TableBeamSwapCandidate? single = null;
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                TableBeamSwapCandidate candidate = offer.Candidates[i];
                if (candidate.SlotIndex == rawIndex)
                {
                    matches++;
                    single = candidate;
                    if (matches > 1)
                    {
                        return new TableBeamSwapSlotResolution(TableBeamSwapResolutionStatus.Ambiguous, null);
                    }
                }
            }

            if (matches == 0)
            {
                return new TableBeamSwapSlotResolution(TableBeamSwapResolutionStatus.UnknownIndex, null);
            }

            return new TableBeamSwapSlotResolution(TableBeamSwapResolutionStatus.Resolved, single);
        }

        private void Terminate(TableStreamTerminationKind kind, string reasonCode, string detail)
        {
            TableStreamTermination termination;
            bool first;
            lock (this)
            {
                first = !_terminating;
                _terminating = true;
            }

            try
            {
                _outbound.Writer.TryComplete();
            }
            catch (Exception)
            {
                // A completed channel is not a fault; the stream simply has no more frames.
            }

            lock (_captureGate)
            {
                _captureNextFullSnapshot = false;
            }

            if (!first)
            {
                return;
            }

            termination = new TableStreamTermination(
                kind,
                _handle ?? throw new InvalidOperationException("A stream terminated before its handle was bound."),
                reasonCode,
                detail);
            _termination = termination;
            Terminated?.Invoke(termination);
        }

        /// <summary>
        /// Arms a one-shot observation of the next full-snapshot frame. The frozen wire
        /// validator requires every SYNC_RESPONSE to carry the authoritative baseline itself
        /// (is_full_snapshot=true plus the matching baseline oneof), and the only honest
        /// source of that baseline is the actor's own stream frame — the host never invents
        /// projections. The sync path arms this before SyncStreamAsync, lifts the observed
        /// frame's snapshot into the SYNC_RESPONSE, and enqueues the reply. Observation never
        /// swallows the frame: it still flows through the bounded sink so a stalled reader
        /// hits the actor's own slow-consumer fence instead of a host-side detour around it.
        /// </summary>
        public void ArmFullSnapshotCapture()
        {
            lock (_captureGate)
            {
                _capturedFullSnapshot = null;
                _captureNextFullSnapshot = true;
                _captureSource = new TaskCompletionSource<TableStreamFrame>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>
        /// Waits for the armed capture. The actor enqueues the baseline into its own
        /// registration channel and its pump forwards it into this sink asynchronously, so
        /// SyncStreamAsync returning does not imply the frame has arrived yet. Returns null
        /// on timeout or if the capture was disarmed without a frame.
        /// </summary>
        public async Task<TableStreamFrame?> WaitForCapturedFullSnapshotAsync(TimeSpan timeout)
        {
            TaskCompletionSource<TableStreamFrame>? source;
            lock (_captureGate)
            {
                if (_capturedFullSnapshot != null)
                {
                    // The channel pump dispatched the frame before the waiter arrived; the
                    // source is already consumed.
                    return _capturedFullSnapshot;
                }

                source = _captureSource;
            }

            if (source == null)
            {
                return null;
            }

            await Task.WhenAny(source.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (!source.Task.IsCompleted)
            {
                return null;
            }

            try
            {
                return await source.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Disarms the observation. With a non-withholding capture nothing needs to be
        /// resumed on failure exits: the observed frame already went to the outbound path.
        /// </summary>
        public void DisarmFullSnapshotCapture()
        {
            lock (_captureGate)
            {
                _captureNextFullSnapshot = false;
                _captureSource?.TrySetCanceled();
                _captureSource = null;
                _capturedFullSnapshot = null;
            }
        }

        private bool TryCapture(TableStreamFrame frame)
        {
            TaskCompletionSource<TableStreamFrame>? source;
            lock (_captureGate)
            {
                if (!_captureNextFullSnapshot || !frame.IsFullSnapshot)
                {
                    return false;
                }

                _capturedFullSnapshot = frame;
                _captureNextFullSnapshot = false;
                source = _captureSource;
                _captureSource = null;
            }

            source?.TrySetResult(frame);
            // Observe, never swallow: the frame continues through the bounded sink so the
            // actor-side outbox bound (and its slow-consumer termination) stays the single
            // trusted backpressure authority.
            return false;
        }

        public void NotifyTerminated(TableStreamTermination termination)
        {
            lock (this)
            {
                if (_terminating)
                {
                    return;
                }

                _terminating = true;
            }

            try
            {
                _outbound.Writer.TryComplete();
            }
            catch (Exception)
            {
            }

            lock (_captureGate)
            {
                _captureNextFullSnapshot = false;
            }

            _termination = termination;
            Terminated?.Invoke(termination);
        }

        /// <summary>
        /// Leasing reader (D-1-07): every read MOVES the frame's bytes from the queued ledger
        /// into the in-flight ledger; the consumer releases them through
        /// <see cref="WireStreamFrameLease.Release"/> once the actual WebSocket send finished.
        /// </summary>
        public ChannelReader<WireStreamFrameLease> Reader => new LeasingReader(_outbound.Reader, this);

        private sealed class LeasingReader : ChannelReader<WireStreamFrameLease>
        {
            private readonly ChannelReader<byte[]> _inner;
            private readonly WireTableStreamSink _sink;

            public LeasingReader(ChannelReader<byte[]> inner, WireTableStreamSink sink)
            {
                _inner = inner;
                _sink = sink;
            }

            public override bool TryRead(out WireStreamFrameLease item)
            {
                if (!_inner.TryRead(out byte[]? payload) || payload is null)
                {
                    item = null!;
                    return false;
                }

                _sink.MoveToInflight(payload.Length);
                item = new WireStreamFrameLease(_sink, payload);
                return true;
            }

            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
                _inner.WaitToReadAsync(cancellationToken);
        }
    }

    public enum TableBeamSwapResolutionStatus : byte
    {
        Resolved = 0,
        Ambiguous = 1,
        UnknownIndex = 2
    }

    /// <summary>One trusted beam swap candidate, projected by the actor for the owner.</summary>
    public sealed class TableBeamSwapCandidate
    {
        public TableBeamSwapCandidate(Mahjong.Domain.Cheat.BeamSwapSlotKind kind, int slotIndex)
        {
            Kind = kind;
            SlotIndex = slotIndex;
        }

        public Mahjong.Domain.Cheat.BeamSwapSlotKind Kind { get; }

        public int SlotIndex { get; }
    }

    public sealed class TableBeamSwapOfferContext
    {
        public TableBeamSwapOfferContext(
            string offerId,
            BeamSwapWindow window,
            IReadOnlyList<TableBeamSwapCandidate> candidates)
        {
            OfferId = offerId;
            Window = window;
            Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        }

        public string OfferId { get; }

        public BeamSwapWindow Window { get; }

        public IReadOnlyList<TableBeamSwapCandidate> Candidates { get; }
    }

    /// <summary>Outcome of resolving one raw wire target slot against the trusted offer context.</summary>
    public sealed class TableBeamSwapSlotResolution
    {
        public TableBeamSwapSlotResolution(TableBeamSwapResolutionStatus status, TableBeamSwapCandidate? candidate)
        {
            Status = status;
            Candidate = candidate;
        }

        public TableBeamSwapResolutionStatus Status { get; }

        public TableBeamSwapCandidate? Candidate { get; }
    }
}
