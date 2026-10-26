using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
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
    /// </summary>
    public sealed class WireTableStreamSink : ITableStreamSink
    {
        private readonly Channel<byte[]> _outbound;
        private TableStreamHandle? _handle;
        private volatile TableStreamTermination? _termination;
        private readonly object _captureGate = new object();
        private bool _captureNextFullSnapshot;
        private TaskCompletionSource<TableStreamFrame>? _captureSource;
        private TableStreamFrame? _capturedFullSnapshot;

        public WireTableStreamSink(int capacity)
        {
            _outbound = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(Math.Max(1, capacity))
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait
                });
        }

        public TableStreamHandle? Handle => _handle;

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

        public TableStreamTermination? Termination => _termination;

        /// <summary>Raised once from <see cref="NotifyTerminated"/> (actor pump thread).</summary>
        public Action<TableStreamTermination>? Terminated { get; set; }

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

            // Observe-for-sync runs first; it never diverts the frame (always false today,
            // kept as the seam so the bounded write below remains the only delivery path).
            TryCapture(frame);

            return _outbound.Writer.WriteAsync(frame.EncodedPayload ??
                throw new InvalidOperationException("A stream frame arrived without an encoded payload."),
                cancellationToken);
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
            _termination = termination;
            _outbound.Writer.TryComplete();
            Terminated?.Invoke(termination);
        }

        public ChannelReader<byte[]> Reader => _outbound.Reader;
    }
}
