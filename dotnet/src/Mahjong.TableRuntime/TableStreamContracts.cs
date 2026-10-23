using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Trusted stream audience. A player stream is bound to exactly one seat; a spectator stream
    /// can only ever carry the public projection. The audience is never taken from an untrusted
    /// client field.
    /// </summary>
    public enum TableStreamAudienceKind : byte
    {
        Seat = 0,
        Spectator = 1
    }

    public readonly struct TableStreamAudience : IEquatable<TableStreamAudience>
    {
        private TableStreamAudience(TableStreamAudienceKind kind, int seat)
        {
            Kind = kind;
            Seat = seat;
        }

        public TableStreamAudienceKind Kind { get; }

        /// <summary>0..3 for a player stream, -1 (protocol no-seat) for a spectator.</summary>
        public int Seat { get; }

        public bool IsSeat => Kind == TableStreamAudienceKind.Seat;

        public bool IsSpectator => Kind == TableStreamAudienceKind.Spectator;

        public static TableStreamAudience Spectator =>
            new TableStreamAudience(TableStreamAudienceKind.Spectator, ProtocolConstants.NoSeat);

        public static TableStreamAudience ForSeat(int seat)
        {
            if (seat < ProtocolConstants.MinSeat || seat > ProtocolConstants.MaxSeat)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            return new TableStreamAudience(TableStreamAudienceKind.Seat, seat);
        }

        public bool Equals(TableStreamAudience other) => Kind == other.Kind && Seat == other.Seat;

        public override bool Equals(object? obj) => obj is TableStreamAudience other && Equals(other);

        public override int GetHashCode() => ((int)Kind * 31) + Seat;

        public override string ToString() =>
            IsSpectator ? "spectator" : "seat" + Seat.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static bool operator ==(TableStreamAudience left, TableStreamAudience right) => left.Equals(right);

        public static bool operator !=(TableStreamAudience left, TableStreamAudience right) => !left.Equals(right);
    }

    /// <summary>
    /// Trusted synchronization generation supplied by the host. It is independent from the
    /// table lease (owner), the seat controller (control), and the transport binding. A new
    /// sync generation always starts a fresh projection sequence and a full baseline.
    /// </summary>
    public readonly struct SyncEpoch : IEquatable<SyncEpoch>, IComparable<SyncEpoch>
    {
        public SyncEpoch(ulong value)
        {
            Value = value;
        }

        public ulong Value { get; }

        public SyncEpoch Next() => new SyncEpoch(checked(Value + 1));

        public bool Equals(SyncEpoch other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is SyncEpoch other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public int CompareTo(SyncEpoch other) => Value.CompareTo(other.Value);

        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static bool operator ==(SyncEpoch left, SyncEpoch right) => left.Equals(right);

        public static bool operator !=(SyncEpoch left, SyncEpoch right) => !left.Equals(right);
    }

    /// <summary>
    /// Identity of one logical stream: (session, audience) plus the host-issued binding and sync
    /// generations. A stream handle is only valid for this exact identity.
    /// </summary>
    public sealed class TableStreamIdentity
    {
        public TableStreamIdentity(
            SessionId sessionId,
            TableStreamAudience audience,
            BindingEpoch bindingEpoch,
            SyncEpoch syncEpoch)
        {
            if (sessionId.IsEmpty)
            {
                throw new ArgumentException("A stream requires a session id.", nameof(sessionId));
            }

            if (bindingEpoch.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bindingEpoch), "Binding epoch starts at 1.");
            }

            if (syncEpoch.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(syncEpoch), "Sync epoch starts at 1.");
            }

            SessionId = sessionId;
            Audience = audience;
            BindingEpoch = bindingEpoch;
            SyncEpoch = syncEpoch;
        }

        public SessionId SessionId { get; }

        public TableStreamAudience Audience { get; }

        public BindingEpoch BindingEpoch { get; }

        public SyncEpoch SyncEpoch { get; }

        internal string Key =>
            SessionId.Value + "\u001f" +
            ((int)Audience.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\u001f" +
            Audience.Seat.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string ToString() =>
            SessionId + "/" + Audience + "/b" +
            BindingEpoch.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/s" +
            SyncEpoch.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Opaque registration token. The actor only accepts handle operations while this exact
    /// handle is the current registration for its identity; an old handle can neither read a
    /// newer stream nor unsubscribe it.
    /// </summary>
    public sealed class TableStreamHandle : IEquatable<TableStreamHandle>
    {
        private readonly object _source;

        internal TableStreamHandle(ulong token, object source, TableStreamIdentity identity)
        {
            Token = token;
            _source = source ?? throw new ArgumentNullException(nameof(source));
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        }

        internal ulong Token { get; }

        internal bool IsOwnedBy(object source) => ReferenceEquals(_source, source);

        public TableStreamIdentity Identity { get; }

        public bool Equals(TableStreamHandle? other)
        {
            // Tokens restart at 1 for every actor instance. Equality must include the owning
            // actor identity, otherwise two independent tables with token 1 alias each other.
            return other != null && Token == other.Token && ReferenceEquals(_source, other._source);
        }

        public override bool Equals(object? obj) => obj is TableStreamHandle other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (Token.GetHashCode() * 397) ^ _source.GetHashCode();
            }
        }

        public override string ToString() => Identity + "#" + Token.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// First registration of a trusted stream. The host must supply the session/audience plus
    /// the binding and sync generations it issued; the actor re-checks the table lease, recovery
    /// generation, round and (for a seat) the current controller epoch. Client-local binding
    /// epochs are never an authorization token.
    /// </summary>
    public sealed class TableStreamSubscribeRequest
    {
        public TableStreamSubscribeRequest(
            TableStreamIdentity identity,
            OwnerEpoch ownerEpoch,
            long recoveryGeneration,
            ITableStreamSink sink,
            RoundId? expectedRoundId = null,
            ControlEpoch? expectedControlEpoch = null)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            if (ownerEpoch.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ownerEpoch), "Owner epoch starts at 1.");
            }

            if (recoveryGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recoveryGeneration),
                    recoveryGeneration,
                    "Recovery generation must be non-negative.");
            }

            Sink = sink ?? throw new ArgumentNullException(nameof(sink));
            OwnerEpoch = ownerEpoch;
            RecoveryGeneration = recoveryGeneration;
            ExpectedRoundId = expectedRoundId;
            ExpectedControlEpoch = expectedControlEpoch;
        }

        public TableStreamIdentity Identity { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public long RecoveryGeneration { get; }

        public ITableStreamSink Sink { get; }

        /// <summary>Optional host expectation; a mismatch is rejected instead of silently rebinding.</summary>
        public RoundId? ExpectedRoundId { get; }

        /// <summary>Seat streams only; a mismatch is rejected as a stale controller.</summary>
        public ControlEpoch? ExpectedControlEpoch { get; }
    }

    /// <summary>
    /// Cursor reconciliation for an existing handle. A cursor mismatch is repaired with a full
    /// baseline rather than by inventing empty events. A stale binding/sync identity is rejected
    /// and never rebound.
    /// </summary>
    public sealed class TableStreamSyncRequest
    {
        public TableStreamSyncRequest(
            TableStreamHandle handle,
            StreamCursor cursor,
            RoundId? expectedRoundId = null,
            SyncEpoch? expectedSyncEpoch = null,
            BindingEpoch? expectedBindingEpoch = null,
            OwnerEpoch? expectedOwnerEpoch = null,
            long? expectedRecoveryGeneration = null)
        {
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            if (expectedRecoveryGeneration.HasValue && expectedRecoveryGeneration.Value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedRecoveryGeneration),
                    expectedRecoveryGeneration.Value,
                    "Recovery generation must be non-negative.");
            }

            Cursor = cursor;
            ExpectedRoundId = expectedRoundId;
            ExpectedSyncEpoch = expectedSyncEpoch;
            ExpectedBindingEpoch = expectedBindingEpoch;
            ExpectedOwnerEpoch = expectedOwnerEpoch;
            ExpectedRecoveryGeneration = expectedRecoveryGeneration;
        }

        public TableStreamHandle Handle { get; }

        public StreamCursor Cursor { get; }

        public RoundId? ExpectedRoundId { get; }

        public SyncEpoch? ExpectedSyncEpoch { get; }

        public BindingEpoch? ExpectedBindingEpoch { get; }

        public OwnerEpoch? ExpectedOwnerEpoch { get; }

        public long? ExpectedRecoveryGeneration { get; }
    }

    public enum TableStreamControlStatus : byte
    {
        Subscribed = 0,
        Unsubscribed = 1,
        InSync = 2,
        BaselineQueued = 3,
        Rejected = 4,
        Failed = 5,
        Closed = 6,
        Overflow = 7,
        MailboxFull = 8
    }

    /// <summary>Result of a serialized subscribe/unsubscribe/sync control operation.</summary>
    public sealed class TableStreamControlResult
    {
        private TableStreamControlResult(
            TableStreamControlStatus status,
            string reasonCode,
            string detail,
            TableStreamHandle? handle,
            StreamCursor? cursor,
            bool fullBaseline,
            StateVersion stateVersion,
            long controlVersion)
        {
            if (status != TableStreamControlStatus.Subscribed &&
                status != TableStreamControlStatus.Unsubscribed &&
                status != TableStreamControlStatus.InSync &&
                status != TableStreamControlStatus.BaselineQueued &&
                string.IsNullOrEmpty(reasonCode))
            {
                throw new ArgumentException("A non-success stream control result requires a reason code.", nameof(reasonCode));
            }

            if (reasonCode.Length > 0 && !ProtocolErrorCode.IsWellFormed(reasonCode))
            {
                throw new ArgumentException("Stream reason must be a stable uppercase code.", nameof(reasonCode));
            }

            Status = status;
            ReasonCode = reasonCode;
            Detail = detail ?? string.Empty;
            Handle = handle;
            Cursor = cursor;
            FullBaseline = fullBaseline;
            StateVersion = stateVersion;
            ControlVersion = controlVersion;
        }

        public TableStreamControlStatus Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public TableStreamHandle? Handle { get; }

        public StreamCursor? Cursor { get; }

        /// <summary>True when the actor queued a full baseline for this result.</summary>
        public bool FullBaseline { get; }

        public StateVersion StateVersion { get; }

        public long ControlVersion { get; }

        public bool IsAccepted =>
            Status == TableStreamControlStatus.Subscribed ||
            Status == TableStreamControlStatus.Unsubscribed ||
            Status == TableStreamControlStatus.InSync ||
            Status == TableStreamControlStatus.BaselineQueued;

        internal static TableStreamControlResult Subscribed(
            TableStreamHandle handle,
            StreamCursor cursor,
            StateVersion stateVersion,
            long controlVersion)
        {
            return new TableStreamControlResult(
                TableStreamControlStatus.Subscribed,
                string.Empty,
                string.Empty,
                handle,
                cursor,
                true,
                stateVersion,
                controlVersion);
        }

        internal static TableStreamControlResult Unsubscribed(
            TableStreamHandle handle,
            StreamCursor cursor,
            StateVersion stateVersion,
            long controlVersion,
            string detail)
        {
            return new TableStreamControlResult(
                TableStreamControlStatus.Unsubscribed,
                string.Empty,
                detail,
                handle,
                cursor,
                false,
                stateVersion,
                controlVersion);
        }

        internal static TableStreamControlResult InSync(
            TableStreamHandle handle,
            StreamCursor cursor,
            StateVersion stateVersion,
            long controlVersion)
        {
            return new TableStreamControlResult(
                TableStreamControlStatus.InSync,
                string.Empty,
                "The stream cursor is current.",
                handle,
                cursor,
                false,
                stateVersion,
                controlVersion);
        }

        internal static TableStreamControlResult BaselineQueued(
            TableStreamHandle handle,
            StreamCursor cursor,
            StateVersion stateVersion,
            long controlVersion,
            string reasonCode,
            string detail)
        {
            return new TableStreamControlResult(
                TableStreamControlStatus.BaselineQueued,
                reasonCode,
                detail,
                handle,
                cursor,
                true,
                stateVersion,
                controlVersion);
        }

        internal static TableStreamControlResult Failure(
            TableStreamControlStatus status,
            string reasonCode,
            string detail,
            TableStreamHandle? handle,
            StateVersion stateVersion,
            long controlVersion)
        {
            return new TableStreamControlResult(
                status,
                reasonCode,
                detail,
                handle,
                handle == null ? (StreamCursor?)null : StreamCursor.Empty,
                false,
                stateVersion,
                controlVersion);
        }
    }

    /// <summary>Why a stream channel stopped. The host can close/re-baseline from this event.</summary>
    public enum TableStreamTerminationKind : byte
    {
        Unsubscribed = 0,
        Displaced = 1,
        SlowConsumer = 2,
        MessageTooLarge = 3,
        ProjectionFailed = 4,
        EncoderFailed = 5,
        StaleControlEpoch = 6,
        StaleOwner = 7,
        LeaseLost = 8,
        ActorClosed = 9,
        ActorFaulted = 10
    }

    public sealed class TableStreamTermination
    {
        public TableStreamTermination(
            TableStreamTerminationKind kind,
            TableStreamHandle handle,
            string reasonCode,
            string detail)
        {
            if (reasonCode.Length > 0 && !ProtocolErrorCode.IsWellFormed(reasonCode))
            {
                throw new ArgumentException("Stream reason must be a stable uppercase code.", nameof(reasonCode));
            }

            Kind = kind;
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public TableStreamTerminationKind Kind { get; }

        public TableStreamHandle Handle { get; }

        public TableStreamIdentity Identity => Handle.Identity;

        public string ReasonCode { get; }

        public string Detail { get; }
    }

    /// <summary>
    /// Slow-consumer boundary for one stream. The actor never calls this interface on its own
    /// mailbox thread: a per-stream pump drains a bounded queue. Implementations must not mutate
    /// table state.
    /// </summary>
    public interface ITableStreamSink
    {
        ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken);
    }

    /// <summary>Optional trusted termination notification, raised outside the actor lock.</summary>
    public interface ITableStreamTerminationSink
    {
        void OnStreamTerminated(TableStreamTermination termination);
    }

    /// <summary>
    /// Explicit encoded payload plus the size the encoder claims. The actor requires
    /// <see cref="DeclaredSize"/> to equal <see cref="Length"/> and both to be positive; a lying
    /// or zero-sized codec fails the stream instead of silently producing a frame with no data.
    /// A fake encoder in tests is not real Protobuf/Wire; the caller supplies the P3.2c codec.
    /// </summary>
    public readonly struct TableEncodedFrame
    {
        private readonly byte[]? _bytes;

        public TableEncodedFrame(byte[] bytes, int declaredSize)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            _bytes = new byte[bytes.Length];
            Array.Copy(bytes, _bytes, bytes.Length);
            DeclaredSize = declaredSize;
        }

        public int DeclaredSize { get; }

        public int Length => _bytes == null ? 0 : _bytes.Length;

        public byte[] CopyBytes()
        {
            if (_bytes == null)
            {
                return Array.Empty<byte>();
            }

            byte[] copy = new byte[_bytes.Length];
            Array.Copy(_bytes, copy, _bytes.Length);
            return copy;
        }
    }

    /// <summary>
    /// Trusted payload encoder/size port. The actor calls it before enqueueing a stream frame;
    /// a null result, zero bytes, a size mismatch, an oversized frame or a thrown exception
    /// terminates that stream. This is the only byte accounting the actor trusts; object-size
    /// estimates are not accepted as final Protobuf bytes.
    /// </summary>
    public interface ITableStreamFrameEncoder
    {
        TableEncodedFrame Encode(TableStreamFrame frame);
    }

    /// <summary>
    /// One stream delivery. Every frame carries a complete current snapshot for its audience
    /// (public for a spectator, public plus private/owner data for a seat) and the events that
    /// were actually visible from the publication. <see cref="IsFullSnapshot"/> is the explicit
    /// baseline/reset marker.
    /// </summary>
    public sealed class TableStreamFrame
    {
        private readonly ReadOnlyCollection<TableProjectedEvent> _events;
        private byte[]? _encodedPayload;

        internal TableStreamFrame(
            TableStreamHandle handle,
            bool isFullSnapshot,
            StreamCursor cursor,
            StateVersion stateVersion,
            EventSeq eventSeq,
            OwnerEpoch ownerEpoch,
            long controlVersion,
            long recoveryGeneration,
            string roundId,
            PublicSnapshotView? publicView,
            PlayerSnapshotView? playerView,
            TableOwnerPrivateView? ownerPrivate,
            IReadOnlyList<TableProjectedEvent>? events)
        {
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            if (handle.Identity.Audience.IsSpectator)
            {
                if (publicView == null || playerView != null || ownerPrivate != null)
                {
                    throw new ArgumentException(
                        "A spectator frame carries only the public snapshot.",
                        nameof(publicView));
                }
            }
            else
            {
                if (playerView == null || playerView.Seat != handle.Identity.Audience.Seat)
                {
                    throw new ArgumentException(
                        "A seat frame requires the player snapshot for its own seat.",
                        nameof(playerView));
                }

                if (publicView != null)
                {
                    throw new ArgumentException(
                        "A seat frame carries the public snapshot through PlayerSnapshotView.Public.",
                        nameof(publicView));
                }
            }

            _events = new ReadOnlyCollection<TableProjectedEvent>(
                events == null
                    ? new List<TableProjectedEvent>()
                    : new List<TableProjectedEvent>(events));
            IsFullSnapshot = isFullSnapshot;
            Cursor = cursor;
            StateVersion = stateVersion;
            EventSeq = eventSeq;
            OwnerEpoch = ownerEpoch;
            ControlVersion = controlVersion;
            RecoveryGeneration = recoveryGeneration;
            RoundId = roundId;
            Public = publicView;
            Player = playerView;
            OwnerPrivate = ownerPrivate;
        }

        public TableStreamHandle Handle { get; }

        public TableStreamIdentity Identity => Handle.Identity;

        public bool IsFullSnapshot { get; }

        public StreamCursor Cursor { get; }

        public StreamVersion StreamVersion => Cursor.StreamVersion;

        public ProjectionSeq ProjectionSeq => Cursor.ProjectionSeq;

        /// <summary>Observed committed table state version; control changes do not increment it.</summary>
        public StateVersion StateVersion { get; }

        public EventSeq EventSeq { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public long ControlVersion { get; }

        public long RecoveryGeneration { get; }

        public string RoundId { get; }

        /// <summary>Spectator payload; null for a seat frame.</summary>
        public PublicSnapshotView? Public { get; }

        /// <summary>Seat payload; includes the same public facts plus this seat's private view.</summary>
        public PlayerSnapshotView? Player { get; }

        /// <summary>Owner-only sleeve/mark/offer extension; never present on a spectator frame.</summary>
        public TableOwnerPrivateView? OwnerPrivate { get; }

        public IReadOnlyList<TableProjectedEvent> Events => _events;

        public byte[]? EncodedPayload => _encodedPayload == null ? null : CopyEncodedPayload();

        public int PayloadSize => _encodedPayload == null ? 0 : _encodedPayload.Length;

        internal void AttachEncodedPayload(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            _encodedPayload = new byte[bytes.Length];
            Array.Copy(bytes, _encodedPayload, bytes.Length);
        }

        private byte[] CopyEncodedPayload()
        {
            byte[] copy = new byte[_encodedPayload!.Length];
            Array.Copy(_encodedPayload, copy, _encodedPayload.Length);
            return copy;
        }
    }

    /// <summary>Bounded outbox sizing and trusted codec hooks for all streams of one table.</summary>
    public sealed class TableStreamOptions
    {
        public int MaxQueuedFrames { get; set; } = 64;

        /// <summary>Only enforced when <see cref="Encoder"/> is configured.</summary>
        public int MaxQueuedBytes { get; set; } = 4 * 1024 * 1024;

        public int MaxStreams { get; set; } = 64;

        public ITableStreamFrameEncoder? Encoder { get; set; }

        public ITableStreamTerminationSink? TerminationSink { get; set; }

        /// <summary>
        /// Deterministic test seam: invoked after a frame is dequeued but before the channel
        /// commits its dispatch claim. Production leaves this null. It is internal so only the
        /// friend test assembly can pause the pump between dequeue and the generation check.
        /// </summary>
        internal Action? BeforeDispatchClaim { get; set; }

        /// <summary>
        /// Test-only observation seam: exposes each created channel so the friend test assembly
        /// can wait for that channel's pump to exit. Production leaves this null.
        /// </summary>
        internal Action<TableStreamChannel>? ChannelCreated { get; set; }

        /// <summary>
        /// Test-only: allocate a completion task per pump invocation so a test can prove the
        /// old pump passed the dispatch claim before asserting that it emitted no frame.
        /// Production leaves this false and allocates no observation state.
        /// </summary>
        internal bool ObservePumpExit { get; set; }

        internal void Validate()
        {
            if (MaxQueuedFrames < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxQueuedFrames), MaxQueuedFrames, "Queue must hold at least one frame.");
            }

            if (MaxQueuedBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxQueuedBytes), MaxQueuedBytes, "Byte limit must be positive.");
            }

            if (MaxStreams < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxStreams), MaxStreams, "At least one stream must be allowed.");
            }
        }
    }

    /// <summary>
    /// Trusted projection inputs for facts the Domain does not model. The request binds the
    /// capture to the exact committed round/state; the source must not cache across versions and
    /// must not be filled from a client claim.
    /// </summary>
    public sealed class TableProjectionContextRequest
    {
        public TableProjectionContextRequest(
            string roomId,
            RoundStateMachine round,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            OwnerEpoch ownerEpoch,
            long controlVersion,
            long recoveryGeneration,
            long monotonicMillis,
            long utcMillis)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (controlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlVersion), controlVersion, "Control version must be non-negative.");
            }

            if (recoveryGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recoveryGeneration),
                    recoveryGeneration,
                    "Recovery generation must be non-negative.");
            }

            if (monotonicMillis < 0 || utcMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(monotonicMillis), "Projection time must be non-negative.");
            }

            RoomId = roomId;
            Round = round ?? throw new ArgumentNullException(nameof(round));
            StateVersion = stateVersion;
            LastEventSeq = lastEventSeq;
            OwnerEpoch = ownerEpoch;
            ControlVersion = controlVersion;
            RecoveryGeneration = recoveryGeneration;
            MonotonicMillis = monotonicMillis;
            UtcMillis = utcMillis;
        }

        public string RoomId { get; }

        public RoundStateMachine Round { get; }

        public StateVersion StateVersion { get; }

        public EventSeq LastEventSeq { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public long ControlVersion { get; }

        public long RecoveryGeneration { get; }

        public long MonotonicMillis { get; }

        public long UtcMillis { get; }
    }

    /// <summary>
    /// Explicit values the actor cannot derive from Domain: meld metadata, discard ordering,
    /// riichi/furiten/preparation, the session summary, the settlement policy and the cheat
    /// aggregate. Every collection is copied; the source must bind the values to the request
    /// round/state version or the actor rejects the projection.
    /// </summary>
    public sealed class TableProjectionContextData
    {
        private readonly ReadOnlyCollection<TableSeatPublicFacts> _publicFacts;
        private readonly ReadOnlyCollection<TableSeatPrivateFacts> _privateFacts;

        public TableProjectionContextData(
            string roundId,
            StateVersion stateVersion,
            ulong sessionVersion,
            CheatState? cheat,
            IReadOnlyList<TableSeatPublicFacts>? publicFacts,
            IReadOnlyList<TableSeatPrivateFacts>? privateFacts,
            TableSessionProjection? session,
            TableProjectionPolicy policy)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            RoundId = roundId;
            StateVersion = stateVersion;
            SessionVersion = sessionVersion;
            Cheat = cheat;
            _publicFacts = Copy(publicFacts, nameof(publicFacts));
            _privateFacts = Copy(privateFacts, nameof(privateFacts));
            Session = session;
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        public string RoundId { get; }

        public StateVersion StateVersion { get; }

        public ulong SessionVersion { get; }

        /// <summary>Required for a seat stream; the spectator projection ignores it.</summary>
        public CheatState? Cheat { get; }

        public IReadOnlyList<TableSeatPublicFacts> PublicFacts => _publicFacts;

        public IReadOnlyList<TableSeatPrivateFacts> PrivateFacts => _privateFacts;

        public TableSessionProjection? Session { get; }

        public TableProjectionPolicy Policy { get; }

        private static ReadOnlyCollection<TableSeatPublicFacts> Copy(
            IReadOnlyList<TableSeatPublicFacts>? values,
            string name)
        {
            if (values == null)
            {
                return new ReadOnlyCollection<TableSeatPublicFacts>(new List<TableSeatPublicFacts>());
            }

            var copy = new List<TableSeatPublicFacts>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                copy.Add(values[i] ?? throw new ArgumentException(name + " contains null.", name));
            }

            return new ReadOnlyCollection<TableSeatPublicFacts>(copy);
        }

        private static ReadOnlyCollection<TableSeatPrivateFacts> Copy(
            IReadOnlyList<TableSeatPrivateFacts>? values,
            string name)
        {
            if (values == null)
            {
                return new ReadOnlyCollection<TableSeatPrivateFacts>(new List<TableSeatPrivateFacts>());
            }

            var copy = new List<TableSeatPrivateFacts>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                copy.Add(values[i] ?? throw new ArgumentException(name + " contains null.", name));
            }

            return new ReadOnlyCollection<TableSeatPrivateFacts>(copy);
        }
    }

    /// <summary>
    /// P3.2b trusted context port. P3.2c/caller maps Domain/rules state into facts and the cheat
    /// aggregate; this table runtime never invents meld, riichi, furiten or match facts.
    /// </summary>
    public interface ITableProjectionContextSource
    {
        TableProjectionContextData Capture(TableProjectionContextRequest request);
    }
}
