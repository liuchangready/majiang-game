using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Contracts;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// One explicit authoritative event-fact record captured together with the committed event.
    /// The type/sequence/version/ordinal tuple is the provenance key: the actor accepts a fact
    /// only when it matches the exact committed event input of the same capture. Facts for
    /// Domain-unmodelled values (turn number, riichi flags, win scoring details, meld metadata)
    /// are supplied by the trusted host; they are never inferred from mutable actor state.
    /// </summary>
    public sealed class TableProjectionEventFact
    {
        public TableProjectionEventFact(
            ProtocolEventType type,
            EventSeq eventSeq,
            StateVersion commandVersion,
            uint eventOrdinal,
            TableProjectionEventFacts facts)
        {
            if (type == ProtocolEventType.Unspecified || !ProtocolEventPolicy.IsKnownEventType((uint)type))
            {
                throw new ArgumentOutOfRangeException(nameof(type), type, "A fact record requires a frozen event type.");
            }

            if (eventSeq.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(eventSeq), "A committed event sequence starts at 1.");
            }

            if (commandVersion.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandVersion), "A committed command version starts at 1.");
            }

            Type = type;
            EventSeq = eventSeq;
            CommandVersion = commandVersion;
            EventOrdinal = eventOrdinal;
            Facts = facts ?? throw new ArgumentNullException(nameof(facts));
        }

        public ProtocolEventType Type { get; }

        public EventSeq EventSeq { get; }

        public StateVersion CommandVersion { get; }

        public uint EventOrdinal { get; }

        public TableProjectionEventFacts Facts { get; }
    }

    /// <summary>
    /// A pure Contracts projection payload for exactly one stream frame. It carries no Domain,
    /// actor, mailbox or mutable table object: the actor freezes this value after commit, and the
    /// host strict codec only turns these DTOs into bytes.
    /// </summary>
    public sealed class TableStreamContractFrame
    {
        private readonly ReadOnlyCollection<ProtocolEventRecord> _events;

        public TableStreamContractFrame(
            SessionId sessionId,
            string roomId,
            TableStreamAudience audience,
            BindingEpoch bindingEpoch,
            SyncEpoch syncEpoch,
            OwnerEpoch ownerEpoch,
            long recoveryGeneration,
            string roundId,
            StateVersion stateVersion,
            long controlVersion,
            EventSeq eventSeq,
            StreamCursor fromCursor,
            StreamCursor cursor,
            bool isFullSnapshot,
            PublicSnapshotView? publicView,
            PlayerSnapshotView? playerView,
            IReadOnlyList<ProtocolEventRecord>? events)
        {
            if (sessionId.IsEmpty)
            {
                throw new ArgumentException("A contract frame requires a session id.", nameof(sessionId));
            }

            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (bindingEpoch.Value == 0 || syncEpoch.Value == 0 || ownerEpoch.Value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bindingEpoch), "Stream binding/sync/owner epochs start at 1.");
            }

            if (recoveryGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(recoveryGeneration), recoveryGeneration, "Recovery generation must be non-negative.");
            }

            if (controlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlVersion), controlVersion, "Control version must be non-negative.");
            }

            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            if (audience.IsSpectator)
            {
                if (publicView == null || playerView != null)
                {
                    throw new ArgumentException("A spectator contract frame carries only the mapped public snapshot.", nameof(publicView));
                }
            }
            else
            {
                if (playerView == null || playerView.Seat != audience.Seat)
                {
                    throw new ArgumentException("A seat contract frame requires the mapped player snapshot for its own seat.", nameof(playerView));
                }

                if (publicView != null)
                {
                    throw new ArgumentException("A seat contract frame carries the public snapshot through PlayerSnapshotView.Public.", nameof(publicView));
                }
            }

            var copy = new List<ProtocolEventRecord>();
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    copy.Add(events[i] ?? throw new ArgumentException("Contract events contain null.", nameof(events)));
                }
            }

            _events = new ReadOnlyCollection<ProtocolEventRecord>(copy);
            SessionId = sessionId;
            RoomId = roomId;
            Audience = audience;
            BindingEpoch = bindingEpoch;
            SyncEpoch = syncEpoch;
            OwnerEpoch = ownerEpoch;
            RecoveryGeneration = recoveryGeneration;
            RoundId = roundId;
            StateVersion = stateVersion;
            ControlVersion = controlVersion;
            EventSeq = eventSeq;
            FromCursor = fromCursor;
            Cursor = cursor;
            IsFullSnapshot = isFullSnapshot;
            Public = publicView;
            Player = playerView;
        }

        public SessionId SessionId { get; }

        public string RoomId { get; }

        public TableStreamAudience Audience { get; }

        public BindingEpoch BindingEpoch { get; }

        public SyncEpoch SyncEpoch { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public long RecoveryGeneration { get; }

        public string RoundId { get; }

        public StateVersion StateVersion { get; }

        public long ControlVersion { get; }

        public EventSeq EventSeq { get; }

        /// <summary>Cursor the client is expected to have acknowledged for this update; empty for a new baseline.</summary>
        public StreamCursor FromCursor { get; }

        public StreamCursor Cursor { get; }

        public bool IsFullSnapshot { get; }

        /// <summary>Mapped public snapshot for a spectator frame; null for a seat frame.</summary>
        public PublicSnapshotView? Public { get; }

        /// <summary>Mapped player snapshot for a seat frame; null for a spectator frame.</summary>
        public PlayerSnapshotView? Player { get; }

        public IReadOnlyList<ProtocolEventRecord> Events => _events;
    }

    /// <summary>
    /// Host strict codec boundary. Implementations must map the supplied Contracts DTOs through
    /// the frozen <c>ProjectionWireMapper</c> and encode only with <c>WireCodec.Encode</c>
    /// (never <c>EncodeCompatibility</c>, reflection or a compatibility shortcut). The runtime
    /// never references <c>Mahjong.Contracts.Wire</c>; this delegate is the only wire hook.
    /// </summary>
    public interface ITableStreamContractCodec
    {
        byte[] Encode(TableStreamContractFrame frame);
    }

    /// <summary>
    /// Marker for an encoder that consumes the actor-frozen Contracts payload. The actor adds
    /// the typed DTOs (and requires the trusted event facts) only for such an encoder, so the
    /// memory-only P3.2b test double does not silently claim Wire coverage.
    /// </summary>
    public interface IContractProjectionStreamFrameEncoder : ITableStreamFrameEncoder
    {
    }

    /// <summary>
    /// Production Contracts/Wire adapter. It performs no state reads and no Domain mapping: the
    /// actor has already frozen the mapped snapshot and typed events, and this adapter only calls
    /// the host-injected strict codec and reports the exact byte length.
    /// </summary>
    public sealed class ContractsStreamFrameEncoder : IContractProjectionStreamFrameEncoder
    {
        private readonly ITableStreamContractCodec _codec;

        public ContractsStreamFrameEncoder(ITableStreamContractCodec codec)
        {
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }

        public TableEncodedFrame Encode(TableStreamFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            TableStreamContractFrame contract = frame.Contract ??
                throw new InvalidOperationException(
                    "The frame has no actor-frozen Contracts projection; the production encoder cannot read actor state.");

            byte[] bytes = _codec.Encode(contract);
            if (bytes == null)
            {
                throw new InvalidOperationException("The strict stream codec returned null bytes.");
            }

            return new TableEncodedFrame(bytes, bytes.Length);
        }
    }
}
