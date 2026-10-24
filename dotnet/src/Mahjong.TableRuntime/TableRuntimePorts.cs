using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Minimal P3.1 clock port. UTC and monotonic time are separate; only monotonic time is
    /// used for deadlines. P3.3 owns the production clock adapter and the full lease/timer set.
    /// </summary>
    public interface ITableClock
    {
        long MonotonicMillis { get; }

        long UtcNowMs { get; }
    }

    /// <summary>
    /// P3.1 publication boundary. It is invoked on the actor's single consumer only after the
    /// commit port reports success, so a slow/failing projection can never move state first.
    /// P3.2 implements per-stream projections behind this port.
    /// </summary>
    public interface ITablePublicationSink
    {
        void Publish(TablePublication publication);
    }

    public enum TableCommitStatus : byte
    {
        Committed = 0,
        Failed = 1,
        StaleOwner = 2,
        StaleState = 3,
        Cancelled = 4
    }

    /// <summary>
    /// The exact proposed transaction handed to the commit port. Event ordinals and sequence
    /// numbers are already assigned; the port must persist the batch atomically or fail.
    /// This is a P3.1 minimal fence: full receipt/lease/checkpoint semantics are P3.4 and the
    /// PostgreSQL implementation is P5.2.
    /// </summary>
    public sealed class TableCommitRequest
    {
        private readonly ReadOnlyCollection<TableEventRecord> _events;

        public TableCommitRequest(
            string roomId,
            OwnerEpoch ownerEpoch,
            StateVersion expectedStateVersion,
            StateVersion commandVersion,
            EventSeq lastEventSeq,
            ulong stateFingerprint,
            IReadOnlyList<TableEventRecord> events,
            TableCommandReceipt receipt)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (events == null || events.Count == 0)
            {
                throw new ArgumentException("An accepted command requires at least one event.", nameof(events));
            }

            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            if (!receipt.IsAccepted)
            {
                throw new ArgumentException("A commit request requires an accepted receipt.", nameof(receipt));
            }

            if (expectedStateVersion.Value == ulong.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedStateVersion), "State version would overflow.");
            }

            if (commandVersion.Value != expectedStateVersion.Value + 1)
            {
                throw new ArgumentException("commandVersion must equal expectedStateVersion + 1.", nameof(commandVersion));
            }

            if (receipt.EvaluatedStateVersion != expectedStateVersion ||
                receipt.CommandVersion != commandVersion ||
                receipt.LastEventSeq != lastEventSeq)
            {
                throw new ArgumentException("Receipt versions must match the proposed transition.");
            }

            if (lastEventSeq.Value < (ulong)events.Count)
            {
                throw new ArgumentException("lastEventSeq cannot precede the batch events.", nameof(lastEventSeq));
            }

            var copy = new List<TableEventRecord>(events.Count);
            ulong expectedSeq = lastEventSeq.Value - (ulong)events.Count + 1;
            for (int i = 0; i < events.Count; i++)
            {
                TableEventRecord record = events[i] ??
                    throw new ArgumentException("Event cannot be null.", nameof(events));
                if (record.Ordinal != i)
                {
                    throw new ArgumentException("Event ordinals must be contiguous from zero.", nameof(events));
                }

                if (record.CommandVersion != commandVersion)
                {
                    throw new ArgumentException("Every event must share the command_version.", nameof(events));
                }

                if (record.Seq.Value != expectedSeq + (ulong)i)
                {
                    throw new ArgumentException("Event sequences must be contiguous and end at lastEventSeq.", nameof(events));
                }

                copy.Add(record);
            }

            RoomId = roomId;
            OwnerEpoch = ownerEpoch;
            ExpectedStateVersion = expectedStateVersion;
            CommandVersion = commandVersion;
            LastEventSeq = lastEventSeq;
            StateFingerprint = stateFingerprint;
            _events = new ReadOnlyCollection<TableEventRecord>(copy);
            Receipt = receipt;
        }

        public string RoomId { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public StateVersion ExpectedStateVersion { get; }

        public StateVersion CommandVersion { get; }

        public EventSeq LastEventSeq { get; }

        public ulong StateFingerprint { get; }

        public IReadOnlyList<TableEventRecord> Events => _events;

        public TableCommandReceipt Receipt { get; }
    }

    public sealed class TableCommitResult
    {
        private TableCommitResult(TableCommitStatus status, string reasonCode, string detail)
        {
            if (status != TableCommitStatus.Committed && string.IsNullOrWhiteSpace(reasonCode))
            {
                throw new ArgumentException("A failed commit requires a reason code.", nameof(reasonCode));
            }

            Status = status;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public TableCommitStatus Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public bool IsCommitted => Status == TableCommitStatus.Committed;

        public static TableCommitResult Committed() =>
            new TableCommitResult(TableCommitStatus.Committed, string.Empty, string.Empty);

        public static TableCommitResult Failed(string reasonCode, string detail) =>
            new TableCommitResult(TableCommitStatus.Failed, reasonCode, detail);

        public static TableCommitResult StaleOwner(string detail) =>
            new TableCommitResult(TableCommitStatus.StaleOwner, TableRuntimeReasonCode.StaleOwnerEpoch, detail);

        public static TableCommitResult StaleState(string detail) =>
            new TableCommitResult(TableCommitStatus.StaleState, TableRuntimeReasonCode.StaleStateVersion, detail);

        public static TableCommitResult Cancelled(string detail) =>
            new TableCommitResult(TableCommitStatus.Cancelled, TableRuntimeReasonCode.CommitCancelled, detail);
    }

    /// <summary>
    /// Minimal P3.1 commit port. The actor awaits exactly one call at a time and installs the
    /// candidate only when <see cref="TableCommitResult.IsCommitted"/> is true. A port that
    /// throws or throws <see cref="OperationCanceledException"/> is treated as a failed commit;
    /// the actor never publishes or advances the version in that case.
    ///
    /// P3.4 replaces/extents this with ITableStore/IReceiptStore/ITableLease/ISettlementOutbox;
    /// P5.2 provides the PostgreSQL implementation. No production file/database adapter exists
    /// in P3.1.
    /// </summary>
    public interface ITableCommitPort
    {
        ValueTask<TableCommitResult> CommitAsync(
            TableCommitRequest request,
            CancellationToken cancellationToken);
    }

    public enum TableControlCommitStatus : byte
    {
        Committed = 0,
        Failed = 1,
        StaleOwner = 2,
        StaleControl = 3,
        Cancelled = 4
    }

    /// <summary>
    /// Minimal control-authority journal request. It is intentionally separate from the Domain
    /// command commit: a control change has its own version and never increments state_version
    /// or event_seq. P3.3 provides only this port and in-memory fakes; the durable journal,
    /// lease and checkpoint are P3.4/P5.2.
    /// </summary>
    public sealed class TableControlCommitRequest
    {
        public TableControlCommitRequest(
            string roomId,
            OwnerEpoch ownerEpoch,
            long expectedControlVersion,
            long controlVersion,
            StateVersion stateVersion,
            string roundId,
            TableControlEvent change,
            string? ownerInstance = null)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            if (expectedControlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedControlVersion),
                    expectedControlVersion,
                    "Expected control version must be non-negative.");
            }

            if (controlVersion != expectedControlVersion + 1L)
            {
                throw new ArgumentException(
                    "controlVersion must equal expectedControlVersion + 1.",
                    nameof(controlVersion));
            }

            if (change == null)
            {
                throw new ArgumentNullException(nameof(change));
            }

            if (change.ControlVersion != controlVersion)
            {
                throw new ArgumentException("The control event must carry the new control version.", nameof(change));
            }

            RoomId = roomId;
            OwnerEpoch = ownerEpoch;
            ExpectedControlVersion = expectedControlVersion;
            ControlVersion = controlVersion;
            StateVersion = stateVersion;
            RoundId = roundId;
            Change = change;
            OwnerInstance = ownerInstance ?? string.Empty;
        }

        public string RoomId { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public long ExpectedControlVersion { get; }

        public long ControlVersion { get; }

        public StateVersion StateVersion { get; }

        public string RoundId { get; }

        public TableControlEvent Change { get; }

        /// <summary>Durable owner instance for the P3.4 control journal; empty on the legacy P3.3 port.</summary>
        public string OwnerInstance { get; }
    }

    public sealed class TableControlCommitResult
    {
        private TableControlCommitResult(TableControlCommitStatus status, string reasonCode, string detail)
        {
            if (status != TableControlCommitStatus.Committed && string.IsNullOrWhiteSpace(reasonCode))
            {
                throw new ArgumentException("A failed control commit requires a reason code.", nameof(reasonCode));
            }

            Status = status;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public TableControlCommitStatus Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public bool IsCommitted => Status == TableControlCommitStatus.Committed;

        public static TableControlCommitResult Committed() =>
            new TableControlCommitResult(TableControlCommitStatus.Committed, string.Empty, string.Empty);

        public static TableControlCommitResult Failed(string reasonCode, string detail) =>
            new TableControlCommitResult(TableControlCommitStatus.Failed, reasonCode, detail);

        public static TableControlCommitResult StaleOwner(string detail) =>
            new TableControlCommitResult(TableControlCommitStatus.StaleOwner, TableRuntimeReasonCode.StaleOwnerEpoch, detail);

        public static TableControlCommitResult StaleControl(string detail) =>
            new TableControlCommitResult(TableControlCommitStatus.StaleControl, TableRuntimeReasonCode.StaleControlVersion, detail);

        public static TableControlCommitResult Cancelled(string detail) =>
            new TableControlCommitResult(TableControlCommitStatus.Cancelled, TableRuntimeReasonCode.ControlCommitCancelled, detail);
    }

    /// <summary>
    /// Optional control-authority journal. When configured, a control change is installed and
    /// published only after this port reports committed; a failure leaves the current registry
    /// untouched and the same message can be retried. When absent, P3.3 keeps the change only
    /// in memory and the handoff records that P3.4 must add the durable journal.
    /// </summary>
    public interface ITableControlCommitPort
    {
        ValueTask<TableControlCommitResult> CommitControlAsync(
            TableControlCommitRequest request,
            CancellationToken cancellationToken);
    }
}
