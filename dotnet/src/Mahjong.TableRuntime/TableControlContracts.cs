using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    /// <summary>Outcome of a control message processed on the table's single consumer.</summary>
    public enum TableControlStatus : byte
    {
        Changed = 0,
        Unchanged = 1,
        Rejected = 2,
        Failed = 3,
        MailboxFull = 4,
        Closed = 5
    }

    public enum TableControlChangeKind : byte
    {
        Disconnect = 0,
        Reconnect = 1,
        GraceExpired = 2,
        ExplicitTakeover = 3,
        Reclaimed = 4,
        PolicyConfigured = 5
    }

    /// <summary>
    /// Read-only seat control state for P3.2 projections and the transport layer. This is a
    /// value copy: it can be published or handed to a projection builder without exposing the
    /// live <see cref="ControlRegistry"/>.
    /// </summary>
    public sealed class TableSeatControlView
    {
        public TableSeatControlView(
            int seat,
            ConnectionStatus status,
            SeatControllerKind kind,
            long controlEpoch,
            long? disconnectedAtMillis,
            long? graceDeadlineMillis,
            string? aiProfileId)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (controlEpoch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlEpoch), controlEpoch, "controlEpoch must be non-negative.");
            }

            Seat = seat;
            Status = status;
            Kind = kind;
            ControlEpoch = controlEpoch;
            DisconnectedAtMillis = disconnectedAtMillis;
            GraceDeadlineMillis = graceDeadlineMillis;
            AiProfileId = aiProfileId;
        }

        public int Seat { get; }

        public ConnectionStatus Status { get; }

        public SeatControllerKind Kind { get; }

        public long ControlEpoch { get; }

        public long? DisconnectedAtMillis { get; }

        public long? GraceDeadlineMillis { get; }

        public string? AiProfileId { get; }

        public bool IsConnected => Status == ConnectionStatus.Connected;

        public bool IsDisconnected => Status == ConnectionStatus.Disconnected;

        public bool IsHuman => Kind == SeatControllerKind.LocalHuman || Kind == SeatControllerKind.RemoteHuman;

        public bool IsAutomated => Kind == SeatControllerKind.Computer || Kind == SeatControllerKind.Takeover;

        public bool IsTakenOver => Kind == SeatControllerKind.Takeover;
    }

    /// <summary>
    /// Immutable control authority view. <see cref="ControlVersion"/> is the independent
    /// control-event sequence (it never advances <c>state_version</c> or <c>event_seq</c>).
    /// <see cref="RecoveryGeneration"/> fences timer callbacks captured before a recovery.
    /// </summary>
    public sealed class TableControlSnapshot
    {
        private readonly ReadOnlyCollection<TableSeatControlView> _seats;

        public TableControlSnapshot(
            string roomId,
            string roundId,
            OwnerEpoch ownerEpoch,
            StateVersion stateVersion,
            long controlVersion,
            long recoveryGeneration,
            DisconnectPolicy policy,
            IReadOnlyList<TableSeatControlView> seats)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
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

            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            if (seats == null || seats.Count != 4)
            {
                throw new ArgumentException("A control snapshot must contain exactly four seats.", nameof(seats));
            }

            var copy = new TableSeatControlView[4];
            for (int seat = 0; seat < 4; seat++)
            {
                TableSeatControlView view = seats[seat] ??
                    throw new ArgumentException("Seat control view cannot be null.", nameof(seats));
                if (view.Seat != seat)
                {
                    throw new ArgumentException("Seat control views must be ordered 0..3.", nameof(seats));
                }

                copy[seat] = view;
            }

            RoomId = roomId;
            RoundId = roundId;
            OwnerEpoch = ownerEpoch;
            StateVersion = stateVersion;
            ControlVersion = controlVersion;
            RecoveryGeneration = recoveryGeneration;
            Policy = policy;
            _seats = new ReadOnlyCollection<TableSeatControlView>(copy);
        }

        public string RoomId { get; }

        public string RoundId { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public StateVersion StateVersion { get; }

        public long ControlVersion { get; }

        public long RecoveryGeneration { get; }

        public DisconnectPolicy Policy { get; }

        public IReadOnlyList<TableSeatControlView> Seats => _seats;
    }

    /// <summary>
    /// One control-authority change. It is deliberately separate from Domain events: a control
    /// change carries its own <see cref="ControlVersion"/> and never increments state_version or
    /// event_seq. The optional control commit port decides whether this record is durable before
    /// the actor installs/publishes it; P3.4 owns the real journal/checkpoint implementation.
    /// </summary>
    public sealed class TableControlEvent
    {
        public TableControlEvent(
            long controlVersion,
            string roundId,
            int seat,
            TableControlChangeKind kind,
            long? previousEpoch,
            long? newEpoch,
            SeatControllerKind? previousKind,
            SeatControllerKind? newKind,
            ConnectionStatus? previousStatus,
            ConnectionStatus? newStatus,
            long? graceDeadlineMillis,
            long atMonotonicMillis,
            long atUtcMillis,
            string reason,
            long? disconnectedAtMillis = null,
            string? aiProfileId = null,
            DisconnectMode? policyMode = null,
            long? policyGraceMillis = null,
            string? policyAiProfileId = null)
        {
            if (controlVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(controlVersion), controlVersion, "Control version starts at 1.");
            }

            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            if (seat < -1 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be -1 or 0..3.");
            }

            if (atMonotonicMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(atMonotonicMillis), atMonotonicMillis, "Time must be non-negative.");
            }

            if (atUtcMillis < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(atUtcMillis), atUtcMillis, "UTC time must be non-negative.");
            }

            if (previousEpoch.HasValue && previousEpoch.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(previousEpoch), previousEpoch, "Epoch must be non-negative.");
            }

            if (newEpoch.HasValue && newEpoch.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newEpoch), newEpoch, "Epoch must be non-negative.");
            }

            if (disconnectedAtMillis.HasValue && disconnectedAtMillis.Value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(disconnectedAtMillis),
                    disconnectedAtMillis,
                    "Disconnect time must be non-negative.");
            }

            if (policyGraceMillis.HasValue && policyGraceMillis.Value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(policyGraceMillis),
                    policyGraceMillis,
                    "Policy grace must be non-negative.");
            }

            ControlVersion = controlVersion;
            RoundId = roundId;
            Seat = seat;
            Kind = kind;
            PreviousEpoch = previousEpoch;
            NewEpoch = newEpoch;
            PreviousKind = previousKind;
            NewKind = newKind;
            PreviousStatus = previousStatus;
            NewStatus = newStatus;
            GraceDeadlineMillis = graceDeadlineMillis;
            AtMonotonicMillis = atMonotonicMillis;
            AtUtcMillis = atUtcMillis;
            Reason = reason ?? string.Empty;
            DisconnectedAtMillis = disconnectedAtMillis;
            AiProfileId = aiProfileId;
            PolicyMode = policyMode;
            PolicyGraceMillis = policyGraceMillis;
            PolicyAiProfileId = policyAiProfileId;
        }

        public long ControlVersion { get; }

        public string RoundId { get; }

        public int Seat { get; }

        public TableControlChangeKind Kind { get; }

        public long? PreviousEpoch { get; }

        public long? NewEpoch { get; }

        public SeatControllerKind? PreviousKind { get; }

        public SeatControllerKind? NewKind { get; }

        public ConnectionStatus? PreviousStatus { get; }

        public ConnectionStatus? NewStatus { get; }

        public long? GraceDeadlineMillis { get; }

        public long AtMonotonicMillis { get; }

        public long AtUtcMillis { get; }

        public string Reason { get; }

        public long? DisconnectedAtMillis { get; }

        public string? AiProfileId { get; }

        public DisconnectMode? PolicyMode { get; }

        public long? PolicyGraceMillis { get; }

        public string? PolicyAiProfileId { get; }
    }

    /// <summary>Result of one admitted control message.</summary>
    public sealed class TableControlResult
    {
        private TableControlResult(
            TableControlStatus status,
            string reasonCode,
            string detail,
            bool admitted,
            StateVersion evaluatedStateVersion,
            long controlVersion,
            TableControlSnapshot? snapshot,
            TableControlEvent? change)
        {
            if (status == TableControlStatus.Changed && (change == null || snapshot == null))
            {
                throw new ArgumentException("A changed control operation requires an event and snapshot.", nameof(change));
            }

            if (status != TableControlStatus.Changed && change != null)
            {
                throw new ArgumentException("Only a changed control operation carries an event.", nameof(change));
            }

            Status = status;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
            Admitted = admitted;
            EvaluatedStateVersion = evaluatedStateVersion;
            ControlVersion = controlVersion;
            Snapshot = snapshot;
            Change = change;
        }

        public TableControlStatus Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public bool Admitted { get; }

        public StateVersion EvaluatedStateVersion { get; }

        public long ControlVersion { get; }

        public TableControlSnapshot? Snapshot { get; }

        public TableControlEvent? Change { get; }

        public bool IsChanged => Status == TableControlStatus.Changed;

        public static TableControlResult Changed(
            TableControlSnapshot snapshot,
            TableControlEvent change,
            StateVersion evaluatedStateVersion)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (change == null)
            {
                throw new ArgumentNullException(nameof(change));
            }

            return new TableControlResult(
                TableControlStatus.Changed,
                string.Empty,
                string.Empty,
                true,
                evaluatedStateVersion,
                snapshot.ControlVersion,
                snapshot,
                change);
        }

        public static TableControlResult Unchanged(
            TableControlSnapshot snapshot,
            StateVersion evaluatedStateVersion,
            string detail)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            return new TableControlResult(
                TableControlStatus.Unchanged,
                string.Empty,
                detail,
                true,
                evaluatedStateVersion,
                snapshot.ControlVersion,
                snapshot,
                null);
        }

        public static TableControlResult Rejected(
            StateVersion evaluatedStateVersion,
            long controlVersion,
            string reasonCode,
            string detail,
            TableControlSnapshot? snapshot = null)
        {
            return new TableControlResult(
                TableControlStatus.Rejected,
                reasonCode,
                detail,
                true,
                evaluatedStateVersion,
                controlVersion,
                snapshot,
                null);
        }

        public static TableControlResult Failed(
            StateVersion evaluatedStateVersion,
            long controlVersion,
            string reasonCode,
            string detail,
            TableControlSnapshot? snapshot = null)
        {
            return new TableControlResult(
                TableControlStatus.Failed,
                reasonCode,
                detail,
                true,
                evaluatedStateVersion,
                controlVersion,
                snapshot,
                null);
        }

        internal static TableControlResult AdmissionFailure(
            TableControlStatus status,
            string reasonCode,
            string detail)
        {
            if (status != TableControlStatus.MailboxFull && status != TableControlStatus.Closed)
            {
                throw new ArgumentOutOfRangeException(nameof(status), status, "Not an admission failure.");
            }

            return new TableControlResult(
                status,
                reasonCode,
                detail,
                false,
                new StateVersion(0),
                0L,
                null,
                null);
        }
    }

    /// <summary>
    /// Identity fence for a grace-expiry timer callback. A late callback cannot take over a
    /// different controller, round or post-recovery table: every field is compared on the
    /// actor before any mutation.
    /// </summary>
    public sealed class TableGraceExpiryFence
    {
        public TableGraceExpiryFence(
            int seat,
            string roundId,
            OwnerEpoch ownerEpoch,
            ControlEpoch expectedControlEpoch,
            long expectedGraceDeadlineMillis,
            long expectedRecoveryGeneration)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            if (expectedControlEpoch.Value > long.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedControlEpoch),
                    expectedControlEpoch,
                    "Control epoch exceeds the Domain range.");
            }

            if (expectedGraceDeadlineMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedGraceDeadlineMillis),
                    expectedGraceDeadlineMillis,
                    "Grace deadline must be non-negative.");
            }

            if (expectedRecoveryGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedRecoveryGeneration),
                    expectedRecoveryGeneration,
                    "Recovery generation must be non-negative.");
            }

            Seat = seat;
            RoundId = roundId;
            OwnerEpoch = ownerEpoch;
            ExpectedControlEpoch = expectedControlEpoch;
            ExpectedGraceDeadlineMillis = expectedGraceDeadlineMillis;
            ExpectedRecoveryGeneration = expectedRecoveryGeneration;
        }

        public int Seat { get; }

        public string RoundId { get; }

        public OwnerEpoch OwnerEpoch { get; }

        public ControlEpoch ExpectedControlEpoch { get; }

        public long ExpectedGraceDeadlineMillis { get; }

        public long ExpectedRecoveryGeneration { get; }
    }

    /// <summary>Result of one actor-captured AI observation.</summary>
    public enum TableAiObservationStatus : byte
    {
        Captured = 0,
        Rejected = 1,
        Failed = 2,
        MailboxFull = 3,
        Closed = 4
    }

    /// <summary>
    /// Immutable per-seat AI input. It contains only the viewer's own hand, public melds,
    /// discards and dora plus the frozen candidate list; there is no wall order, seed,
    /// opponent closed hand or complete Domain state. Reasoning over it must happen outside
    /// the actor; <see cref="TableActor.CompleteAiAsync"/> revalidates the result.
    /// </summary>
    public sealed class TableAiObservationResult
    {
        public TableAiObservationResult(
            TableAiObservationStatus status,
            string reasonCode,
            string detail,
            bool admitted,
            AiObservation? observation)
        {
            Status = status;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
            Admitted = admitted;
            Observation = observation;
        }

        public TableAiObservationStatus Status { get; }

        public string ReasonCode { get; }

        public string Detail { get; }

        public bool Admitted { get; }

        public AiObservation? Observation { get; }

        public bool IsCaptured => Status == TableAiObservationStatus.Captured;
    }

    /// <summary>Read-only per-seat timing view for P3.2/projection and recovery verification.</summary>
    public sealed class TableSeatTimingView
    {
        public TableSeatTimingView(
            int seat,
            DecisionTiming? timing,
            long remainingReserveMillis,
            DecisionKey? pendingKey)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }

            Seat = seat;
            Timing = timing;
            RemainingReserveMillis = remainingReserveMillis;
            PendingKey = pendingKey;
        }

        public int Seat { get; }

        public DecisionTiming? Timing { get; }

        public long RemainingReserveMillis { get; }

        public DecisionKey? PendingKey { get; }
    }

    public sealed class TableTimingSnapshot
    {
        private readonly ReadOnlyCollection<TableSeatTimingView> _seats;

        public TableTimingSnapshot(
            string roomId,
            string roundId,
            StateVersion stateVersion,
            long recoveryGeneration,
            bool isPaused,
            IReadOnlyList<TableSeatTimingView> seats)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("RoomId cannot be blank.", nameof(roomId));
            }

            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            if (recoveryGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recoveryGeneration),
                    recoveryGeneration,
                    "Recovery generation must be non-negative.");
            }

            if (seats == null || seats.Count != 4)
            {
                throw new ArgumentException("A timing snapshot must contain exactly four seats.", nameof(seats));
            }

            var copy = new TableSeatTimingView[4];
            for (int seat = 0; seat < 4; seat++)
            {
                TableSeatTimingView view = seats[seat] ??
                    throw new ArgumentException("Seat timing view cannot be null.", nameof(seats));
                if (view.Seat != seat)
                {
                    throw new ArgumentException("Seat timing views must be ordered 0..3.", nameof(seats));
                }

                copy[seat] = view;
            }

            RoomId = roomId;
            RoundId = roundId;
            StateVersion = stateVersion;
            RecoveryGeneration = recoveryGeneration;
            IsPaused = isPaused;
            _seats = new ReadOnlyCollection<TableSeatTimingView>(copy);
        }

        public string RoomId { get; }

        public string RoundId { get; }

        public StateVersion StateVersion { get; }

        public long RecoveryGeneration { get; }

        public bool IsPaused { get; }

        public IReadOnlyList<TableSeatTimingView> Seats => _seats;
    }

    /// <summary>
    /// One decision explicitly restored during recovery. Remaining base/reserve and overdue
    /// time are durable inputs; the actor never infers them from the local UTC clock or from a
    /// persisted monotonic value produced by another process.
    /// </summary>
    public sealed class TableRecoveredDecision
    {
        private readonly ReadOnlyCollection<LegalActionOption> _candidates;

        public TableRecoveredDecision(
            DecisionKey key,
            long controlEpoch,
            IReadOnlyList<LegalActionOption> candidates,
            TileId? drawnTile,
            long remainingBaseMillis,
            long remainingReserveMillis,
            long overdueMillis)
        {
            if (controlEpoch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlEpoch), controlEpoch, "controlEpoch must be non-negative.");
            }

            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            if (remainingBaseMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingBaseMillis),
                    remainingBaseMillis,
                    "Remaining base must be non-negative.");
            }

            if (remainingReserveMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingReserveMillis),
                    remainingReserveMillis,
                    "Remaining reserve must be non-negative.");
            }

            if (overdueMillis < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(overdueMillis),
                    overdueMillis,
                    "Overdue time must be non-negative.");
            }

            var copy = new List<LegalActionOption>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                LegalActionOption action = candidates[i] ??
                    throw new ArgumentException("Candidate cannot be null.", nameof(candidates));
                if (action.Seat != key.Seat)
                {
                    throw new ArgumentException("Candidate seat must match the decision seat.", nameof(candidates));
                }

                copy.Add(action);
            }

            Key = key;
            ControlEpoch = controlEpoch;
            _candidates = new ReadOnlyCollection<LegalActionOption>(copy);
            DrawnTile = drawnTile;
            RemainingBaseMillis = remainingBaseMillis;
            RemainingReserveMillis = remainingReserveMillis;
            OverdueMillis = overdueMillis;
        }

        public DecisionKey Key { get; }

        public long ControlEpoch { get; }

        public IReadOnlyList<LegalActionOption> Candidates => _candidates;

        public TileId? DrawnTile { get; }

        public long RemainingBaseMillis { get; }

        public long RemainingReserveMillis { get; }

        public long OverdueMillis { get; }
    }

    /// <summary>Explicit timing reconstruction input for recovery. Empty means "restore none".</summary>
    public sealed class TableRecoveryTiming
    {
        private readonly ReadOnlyCollection<TableRecoveredDecision> _decisions;

        public TableRecoveryTiming(IReadOnlyList<TableRecoveredDecision>? decisions)
        {
            if (decisions == null)
            {
                _decisions = new ReadOnlyCollection<TableRecoveredDecision>(new List<TableRecoveredDecision>());
                return;
            }

            var copy = new List<TableRecoveredDecision>(decisions.Count);
            for (int i = 0; i < decisions.Count; i++)
            {
                copy.Add(decisions[i] ??
                    throw new ArgumentException("Recovered decision cannot be null.", nameof(decisions)));
            }

            _decisions = new ReadOnlyCollection<TableRecoveredDecision>(copy);
        }

        public IReadOnlyList<TableRecoveredDecision> Decisions => _decisions;

        public static TableRecoveryTiming None { get; } = new TableRecoveryTiming(null);
    }

    /// <summary>
    /// Control authority included in a recovery baseline. The control version and exact seat
    /// epochs must come from the durable checkpoint; installing only controller kinds would
    /// re-enable stale AI/player results.
    /// </summary>
    public sealed class TableControlRecoveryBaseline
    {
        public TableControlRecoveryBaseline(ControlRegistrySnapshot state, long controlVersion)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            if (controlVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(controlVersion), controlVersion, "Control version must be non-negative.");
            }

            ControlVersion = controlVersion;
        }

        public ControlRegistrySnapshot State { get; }

        public long ControlVersion { get; }
    }
}
