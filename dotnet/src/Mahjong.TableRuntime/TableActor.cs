using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Bounded single-consumer table actor.
    ///
    /// Ordering:
    /// - all player commands, deadline ticks, AI completions, recovery and lease-loss messages
    ///   enter one FIFO mailbox and are processed by exactly one drain task at a time;
    /// - a tick uses a pure candidate plan over the current timing/pending snapshot: timeouts
    ///   first, then same-tick AI results; at/after the deadline the timeout wins. The real
    ///   DecisionClock/pending entries are mutated only after the corresponding Domain transition
    ///   commits, so a failed/cancelled/rejected item cannot consume a decision or reserve;
    /// - AI reasoning happens outside this actor. A completed <see cref="AiDecision"/> is
    ///   re-validated here against roundId, decisionId and controlEpoch (and the current
    ///   candidate list/deadline) before any state is built.
    ///
    /// Versioning:
    /// - one accepted state-changing command advances <c>state_version</c> exactly once and
    ///   must produce at least one Domain event; every event of that command shares the new
    ///   command_version and has contiguous ordinal 0..N-1;
    /// - a rejection or failed commit advances nothing and publishes nothing;
    /// - the candidate state is installed, the success receipt is returned and the snapshot/
    ///   events are published only after <see cref="ITableCommitPort"/> reports committed.
    ///
    /// Concurrency:
    /// - the actor holds no lock while awaiting the commit port and never starts a second
    ///   drain task for the same table, so same-table messages cannot execute in parallel;
    /// - the consumer is a short drain task on the shared thread pool, not a per-table thread
    ///   and not an unbounded <c>Task.Run</c> loop.
    /// </summary>
    public sealed class TableActor : IAsyncDisposable
    {
        private readonly object _closeGate = new object();
        private readonly TableActorOptions _options;
        private readonly TableMailbox _mailbox;
        private readonly ITableCommitPort _commitPort;
        private readonly ITableClock _clockPort;
        private readonly ITablePublicationSink? _publicationSink;
        private readonly ITableControlCommitPort? _controlCommitPort;
        private readonly DecisionClockConfig _decisionClockConfig;
        private ControlRegistry _control;
        private DecisionClock _decisionClock;
        private readonly Dictionary<int, PendingDecision> _pending = new Dictionary<int, PendingDecision>();
        private readonly Dictionary<AiObservation, long> _aiObservationGenerations =
            new Dictionary<AiObservation, long>();
        private readonly List<TableEventRecord> _recentEvents = new List<TableEventRecord>();

        private RoundStateMachine _round;
        private StateVersion _stateVersion = new StateVersion(0);
        private EventSeq _lastEventSeq = new EventSeq(0);
        private OwnerEpoch _ownerEpoch;
        private long _controlVersion;
        private long _recoveryGeneration;
        private long _lastMonotonicMillis;
        private bool _strictRecoveryFence;
        private TableActorLifecycle _lifecycle = TableActorLifecycle.Serving;
        private string _lifecycleReason = string.Empty;
        private string _publicationFault = string.Empty;
        private TaskCompletionSource<bool>? _closeCompletion;
        private int _pumpRunning;

        public TableActor(
            TableActorOptions options,
            RoundStateMachine initialRound,
            ITableCommitPort commitPort,
            ITableClock clock,
            ITablePublicationSink? publicationSink = null)
            : this(options, initialRound, commitPort, clock, publicationSink, null)
        {
        }

        public TableActor(
            TableActorOptions options,
            RoundStateMachine initialRound,
            ITableCommitPort commitPort,
            ITableClock clock,
            ITablePublicationSink? publicationSink,
            ITableControlCommitPort? controlCommitPort)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _round = initialRound ?? throw new ArgumentNullException(nameof(initialRound));
            _commitPort = commitPort ?? throw new ArgumentNullException(nameof(commitPort));
            _clockPort = clock ?? throw new ArgumentNullException(nameof(clock));
            _publicationSink = publicationSink;
            _controlCommitPort = controlCommitPort;
            _ownerEpoch = options.InitialOwnerEpoch;
            _mailbox = new TableMailbox(options.MailboxCapacity, options.ControlReserve);
            _control = new ControlRegistry(options.DisconnectPolicy, CopyControllers(options.InitialControllers));
            _decisionClockConfig = options.DecisionClockConfig ?? new DecisionClockConfig();
            _decisionClock = new DecisionClock(_decisionClockConfig);

            long initialMonotonic = _clockPort.MonotonicMillis;
            long initialUtc = _clockPort.UtcNowMs;
            if (initialMonotonic < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clock), initialMonotonic, "Monotonic time must be non-negative.");
            }

            if (initialUtc < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clock), initialUtc, "UTC time must be non-negative.");
            }

            _lastMonotonicMillis = initialMonotonic;
            _decisionClock.BeginRound(_round.RoundId, initialMonotonic);
        }

        public string PublicationFault => _publicationFault;

        /// <summary>
        /// Admits a player command to the business mailbox. If the mailbox is full or closing,
        /// the command is not admitted and no version/commit/publication occurs. Once admitted it
        /// is never dropped: cancellation of the caller token after admission does not cancel the
        /// table operation; the returned task completes with the authoritative outcome.
        /// </summary>
        public ValueTask<TableOperationResult> SubmitAsync(
            TableCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new CommandMailboxMessage(command);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueue(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableOperationResult>(AdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableOperationResult>(message.Completion.Task);
        }

        /// <summary>
        /// Opens one decision. This is the P3.3 timing seam: it does not advance state_version,
        /// event_seq or control_version and does not publish a Domain/control event. Its
        /// <see cref="TableOperationStatus.Configured"/> result is therefore an in-memory
        /// authority change, not a durability claim; P3.4 must checkpoint the decision clock.
        /// </summary>
        public ValueTask<TableOperationResult> OpenDecisionAsync(
            PendingDecision decision,
            CancellationToken cancellationToken = default)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new OpenDecisionMailboxMessage(decision);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableOperationResult>(AdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableOperationResult>(message.Completion.Task);
        }

        /// <summary>
        /// Authoritative deadline tick. The actor clock supplies the time. Any AI submissions are
        /// arbitrated by Domain with timeouts first. A tick uses the control reserve so a full
        /// business mailbox cannot suppress deadline enforcement.
        /// </summary>
        public ValueTask<TableTickResult> TickAsync(
            TableTickRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new TickMailboxMessage(request ?? TableTickRequest.Empty);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableTickResult>(TickAdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableTickResult>(message.Completion.Task);
        }

        /// <summary>
        /// Admits a completed AI decision. Reasoning itself must have happened outside the actor.
        /// The result is re-checked here inside the mailbox; stale/illegal results change nothing.
        /// </summary>
        public ValueTask<TableTickResult> CompleteAiAsync(
            AiDecision decision,
            CancellationToken cancellationToken = default)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new AiMailboxMessage(decision);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueue(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableTickResult>(TickAdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableTickResult>(message.Completion.Task);
        }

        /// <summary>
        /// Captures an immutable per-seat AI observation inside the authority queue. Reasoning
        /// must run outside the actor over this value; the actor is never locked during it.
        /// </summary>
        public ValueTask<TableAiObservationResult> CaptureAiObservationAsync(
            int seat,
            CancellationToken cancellationToken = default)
        {
            CheckSeat(seat);
            cancellationToken.ThrowIfCancellationRequested();
            var message = new AiObservationMailboxMessage(seat);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableAiObservationResult>(AiObservationAdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableAiObservationResult>(message.Completion.Task);
        }

        /// <summary>
        /// Disconnect is an authority message: the connection status, grace deadline and any
        /// immediate takeover are applied on the actor, using the actor monotonic clock.
        /// </summary>
        public ValueTask<TableControlResult> DisconnectAsync(
            int seat,
            OwnerEpoch ownerEpoch,
            ControlEpoch expectedControlEpoch,
            string reason,
            CancellationToken cancellationToken = default)
        {
            CheckSeat(seat);
            cancellationToken.ThrowIfCancellationRequested();
            var message = new DisconnectMailboxMessage(seat, ownerEpoch, expectedControlEpoch, reason);
            return EnqueueControlMessage(message);
        }

        /// <summary>
        /// Reconnect only restores transport presence. It never reclaims a seat from takeover;
        /// <see cref="ReclaimControlAsync"/> is the explicit control-taking step.
        /// </summary>
        public ValueTask<TableControlResult> ReconnectAsync(
            int seat,
            OwnerEpoch ownerEpoch,
            ControlEpoch expectedControlEpoch,
            string reason,
            CancellationToken cancellationToken = default)
        {
            CheckSeat(seat);
            cancellationToken.ThrowIfCancellationRequested();
            var message = new ReconnectMailboxMessage(seat, ownerEpoch, expectedControlEpoch, reason);
            return EnqueueControlMessage(message);
        }

        /// <summary>
        /// Late grace-expiry timer callback. The fence must match the current round, owner,
        /// control epoch, grace deadline and recovery generation or the call has no effect.
        /// </summary>
        public ValueTask<TableControlResult> ExpireGraceAsync(
            TableGraceExpiryFence fence,
            CancellationToken cancellationToken = default)
        {
            if (fence == null)
            {
                throw new ArgumentNullException(nameof(fence));
            }

            CheckSeat(fence.Seat);
            cancellationToken.ThrowIfCancellationRequested();
            var message = new GraceExpiryMailboxMessage(fence);
            return EnqueueControlMessage(message);
        }

        /// <summary>Explicit takeover request; bumps controlEpoch on success.</summary>
        public ValueTask<TableControlResult> RequestTakeoverAsync(
            int seat,
            OwnerEpoch ownerEpoch,
            ControlEpoch expectedControlEpoch,
            string reason,
            CancellationToken cancellationToken = default)
        {
            CheckSeat(seat);
            cancellationToken.ThrowIfCancellationRequested();
            var message = new TakeoverMailboxMessage(seat, ownerEpoch, expectedControlEpoch, reason);
            return EnqueueControlMessage(message);
        }

        /// <summary>Explicit human reclaim; bumps controlEpoch and invalidates the takeover epoch.</summary>
        public ValueTask<TableControlResult> ReclaimControlAsync(
            int seat,
            OwnerEpoch ownerEpoch,
            ControlEpoch expectedControlEpoch,
            string reason,
            CancellationToken cancellationToken = default)
        {
            CheckSeat(seat);
            cancellationToken.ThrowIfCancellationRequested();
            var message = new ReclaimMailboxMessage(seat, ownerEpoch, expectedControlEpoch, reason);
            return EnqueueControlMessage(message);
        }

        /// <summary>
        /// Changes only the future-disconnect policy. It is refused while any seat is
        /// disconnected so an in-flight grace deadline cannot change meaning mid-flight.
        /// </summary>
        public ValueTask<TableControlResult> ConfigureControlPolicyAsync(
            DisconnectPolicy policy,
            OwnerEpoch ownerEpoch,
            string reason,
            CancellationToken cancellationToken = default)
        {
            if (policy == null)
            {
                throw new ArgumentNullException(nameof(policy));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new ConfigurePolicyMailboxMessage(policy, ownerEpoch, reason);
            return EnqueueControlMessage(message);
        }

        /// <summary>Read-only control snapshot for P3.2 and transport adapters.</summary>
        public ValueTask<TableControlSnapshot> GetControlSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new ControlSnapshotMailboxMessage();
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                Exception error = enqueue == TableMailboxEnqueueStatus.Closed
                    ? new ObjectDisposedException(nameof(TableActor))
                    : new InvalidOperationException("The table control reserve is full.");
                return new ValueTask<TableControlSnapshot>(Task.FromException<TableControlSnapshot>(error));
            }

            EnsurePump();
            return new ValueTask<TableControlSnapshot>(message.Completion.Task);
        }

        /// <summary>Read-only rule-deadline/reserve snapshot for P3.2 and recovery verification.</summary>
        public ValueTask<TableTimingSnapshot> GetTimingSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new TimingSnapshotMailboxMessage();
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                Exception error = enqueue == TableMailboxEnqueueStatus.Closed
                    ? new ObjectDisposedException(nameof(TableActor))
                    : new InvalidOperationException("The table control reserve is full.");
                return new ValueTask<TableTimingSnapshot>(Task.FromException<TableTimingSnapshot>(error));
            }

            EnsurePump();
            return new ValueTask<TableTimingSnapshot>(message.Completion.Task);
        }

        public ValueTask<TableOperationResult> BeginRecoveryAsync(
            string reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new BeginRecoveryMailboxMessage(reason);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableOperationResult>(AdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableOperationResult>(message.Completion.Task);
        }

        /// <summary>
        /// Installs a baseline that was already verified and made durable by the recovery owner.
        /// P3.4 owns checkpoint/replay; P3.1 only guarantees that installation is serialized and
        /// that the version/event sequence never move backwards.
        /// </summary>
        public ValueTask<TableOperationResult> CompleteRecoveryAsync(
            TableRecoveryBaseline baseline,
            CancellationToken cancellationToken = default)
        {
            if (baseline == null)
            {
                throw new ArgumentNullException(nameof(baseline));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new CompleteRecoveryMailboxMessage(baseline);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableOperationResult>(AdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableOperationResult>(message.Completion.Task);
        }

        /// <summary>
        /// Reports loss of the table lease. Loss is an authority control message: it uses the
        /// control reserve, is ordered against already-admitted work, and permanently stops
        /// version advancement until a new actor/baseline is installed (P3.4).
        /// </summary>
        public ValueTask<TableOperationResult> ReportLeaseLostAsync(
            OwnerEpoch ownerEpoch,
            string reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new LeaseLostMailboxMessage(ownerEpoch, reason);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableOperationResult>(AdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableOperationResult>(message.Completion.Task);
        }

        /// <summary>Serialized authority snapshot. Per-seat/public projections are P3.2.</summary>
        public ValueTask<TableSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new SnapshotMailboxMessage();
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                Exception error = enqueue == TableMailboxEnqueueStatus.Closed
                    ? new ObjectDisposedException(nameof(TableActor))
                    : new InvalidOperationException("The table control reserve is full.");
                return new ValueTask<TableSnapshot>(Task.FromException<TableSnapshot>(error));
            }

            EnsurePump();
            return new ValueTask<TableSnapshot>(message.Completion.Task);
        }

        /// <summary>
        /// Gracefully closes the actor. New business/control submissions are refused immediately;
        /// previously admitted messages are ahead of the close marker and are still processed.
        /// </summary>
        public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task completion;
            bool startPump = false;
            lock (_closeGate)
            {
                if (_closeCompletion == null)
                {
                    var message = new CloseMailboxMessage();
                    TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueClose(message);
                    if (enqueue == TableMailboxEnqueueStatus.Enqueued)
                    {
                        _closeCompletion = message.Completion;
                        startPump = true;
                    }
                    else
                    {
                        _closeCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _closeCompletion.TrySetResult(true);
                    }
                }

                completion = _closeCompletion.Task;
            }

            if (startPump)
            {
                EnsurePump();
            }

            await completion.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
        }

        private void EnsurePump()
        {
            if (Interlocked.CompareExchange(ref _pumpRunning, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                try
                {
                    while (_mailbox.TryDequeue(out TableMailboxMessage message))
                    {
                        await ProcessMessageAsync(message).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    SetFaulted(exception);
                    DrainAsFaulted(exception);
                    Interlocked.Exchange(ref _pumpRunning, 0);
                    return;
                }

                Interlocked.Exchange(ref _pumpRunning, 0);

                if (!_mailbox.HasItems)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _pumpRunning, 1, 0) != 0)
                {
                    return;
                }
            }
        }

        private async Task ProcessMessageAsync(TableMailboxMessage message)
        {
            try
            {
                switch (message)
                {
                    case CommandMailboxMessage command:
                        command.Completion.TrySetResult(
                            await ProcessCommandAsync(command.Command).ConfigureAwait(false));
                        break;
                    case OpenDecisionMailboxMessage open:
                        open.Completion.TrySetResult(ProcessOpenDecision(open.Decision));
                        break;
                    case TickMailboxMessage tick:
                        tick.Completion.TrySetResult(
                            await ProcessTickAsync(tick.Request).ConfigureAwait(false));
                        break;
                    case AiMailboxMessage ai:
                        ai.Completion.TrySetResult(
                            await ProcessAiAsync(ai.Decision).ConfigureAwait(false));
                        break;
                    case DisconnectMailboxMessage disconnect:
                        disconnect.Completion.TrySetResult(
                            await ProcessDisconnectAsync(disconnect).ConfigureAwait(false));
                        break;
                    case ReconnectMailboxMessage reconnect:
                        reconnect.Completion.TrySetResult(
                            await ProcessReconnectAsync(reconnect).ConfigureAwait(false));
                        break;
                    case GraceExpiryMailboxMessage grace:
                        grace.Completion.TrySetResult(
                            await ProcessGraceExpiryAsync(grace.Fence).ConfigureAwait(false));
                        break;
                    case TakeoverMailboxMessage takeover:
                        takeover.Completion.TrySetResult(
                            await ProcessTakeoverAsync(takeover).ConfigureAwait(false));
                        break;
                    case ReclaimMailboxMessage reclaim:
                        reclaim.Completion.TrySetResult(
                            await ProcessReclaimAsync(reclaim).ConfigureAwait(false));
                        break;
                    case ConfigurePolicyMailboxMessage policy:
                        policy.Completion.TrySetResult(
                            await ProcessConfigurePolicyAsync(policy).ConfigureAwait(false));
                        break;
                    case AiObservationMailboxMessage aiObservation:
                        aiObservation.Completion.TrySetResult(ProcessAiObservation(aiObservation.Seat));
                        break;
                    case ControlSnapshotMailboxMessage controlSnapshot:
                        controlSnapshot.Completion.TrySetResult(BuildControlSnapshot());
                        break;
                    case TimingSnapshotMailboxMessage timingSnapshot:
                        timingSnapshot.Completion.TrySetResult(BuildTimingSnapshot());
                        break;
                    case BeginRecoveryMailboxMessage begin:
                        begin.Completion.TrySetResult(ProcessBeginRecovery(begin.Reason));
                        break;
                    case CompleteRecoveryMailboxMessage complete:
                        complete.Completion.TrySetResult(ProcessCompleteRecovery(complete.Baseline));
                        break;
                    case LeaseLostMailboxMessage leaseLost:
                        leaseLost.Completion.TrySetResult(
                            ProcessLeaseLost(leaseLost.OwnerEpoch, leaseLost.Reason));
                        break;
                    case SnapshotMailboxMessage snapshot:
                        snapshot.Completion.TrySetResult(BuildSnapshot());
                        break;
                    case CloseMailboxMessage close:
                        _pending.Clear();
                        _decisionClock.Reset();
                        _lifecycle = TableActorLifecycle.Closed;
                        _lifecycleReason = "actor closed";
                        close.Completion.TrySetResult(true);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown table mailbox message.");
                }
            }
            catch (Exception exception)
            {
                CompleteFault(message, exception);
                SetFaulted(exception);
            }
        }

        private async ValueTask<TableOperationResult> ProcessCommandAsync(TableCommandEnvelope command)
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return guard;
            }

            if (_strictRecoveryFence && command.RecoveryGeneration != _recoveryGeneration)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleRecoveryGeneration,
                    "Command recovery generation " + command.RecoveryGeneration +
                    " is not current generation " + _recoveryGeneration + ".");
            }

            if (!TryReadClock(out long now, out _, out string clockFailure))
            {
                return TableOperationResult.Rejected(_stateVersion, clockFailure, "The monotonic clock is not safe for authority.");
            }

            if (command.OwnerEpoch != _ownerEpoch)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleOwnerEpoch,
                    "Command owner epoch " + command.OwnerEpoch + " is not the table lease epoch " + _ownerEpoch + ".");
            }

            if (!string.Equals(command.Command.RoundId, _round.RoundId, StringComparison.Ordinal))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.RoundMismatch,
                    "Command round '" + command.Command.RoundId + "' does not match '" + _round.RoundId + "'.");
            }

            int seat = command.Command.Seat;
            if (!TryToDomainEpoch(command.ControlEpoch, out long controlEpoch))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "Control epoch exceeds the Domain range.");
            }

            PendingDecision? pending = null;
            if (_pending.TryGetValue(seat, out PendingDecision foundPending))
            {
                pending = foundPending;
            }

            if (pending != null)
            {
                if (!command.DecisionId.HasValue ||
                    command.DecisionId.Value.IsEmpty ||
                    !string.Equals(
                        command.DecisionId.Value.Value,
                        pending.Key.DecisionId,
                        StringComparison.Ordinal))
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.DecisionMismatch,
                        "The command does not carry the active decision id.");
                }

                DecisionTiming? timing = _decisionClock.Timing(seat);
                if (timing == null || timing.Key != pending.Key)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.DecisionMismatch,
                        "The active decision is not open in the clock.");
                }

                if (now >= timing.DeadlineMillis)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.DeadlineExpired,
                        "The player command arrived at or after the authoritative deadline.");
                }
            }
            else if (command.DecisionId.HasValue && !command.DecisionId.Value.IsEmpty)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.DecisionMismatch,
                    "The command refers to a decision that is not open for this seat.");
            }

            if (command.ExpectedStateVersion != _stateVersion)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleStateVersion,
                    "Expected state version " + command.ExpectedStateVersion + ", actual " + _stateVersion + ".");
            }

            if (_control.GetEpoch(seat) != controlEpoch)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "Control epoch " + controlEpoch + " is not current for seat " + seat + ".");
            }

            RoundTransition transition;
            try
            {
                transition = _round.Execute(command.Command);
            }
            catch (Exception exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message);
            }

            TableOperationResult? transitionError = ValidateTransition(transition);
            if (transitionError != null)
            {
                return transitionError;
            }

            return await CommitAndInstallAsync(
                transition,
                command.RequestId,
                pending,
                command.DecisionId,
                now).ConfigureAwait(false);
        }

        private async ValueTask<TableTickResult> ProcessAiAsync(AiDecision decision)
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return TableTickResult.Rejected(guard.Status, guard.ReasonCode, guard.Detail, _stateVersion);
            }

            if (!TryReadClock(out long now, out _, out string clockFailure))
            {
                return TableTickResult.Rejected(
                    TableOperationStatus.Failed,
                    clockFailure,
                    "The monotonic clock is not safe for authority.",
                    _stateVersion);
            }

            if (_recoveryGeneration > 0)
            {
                DecisionKey key = decision.Intent.DecisionKey;
                bool addressesCurrentDecision =
                    _pending.TryGetValue(key.Seat, out PendingDecision? current) &&
                    current.Key == key &&
                    current.ControlEpoch == decision.Intent.ControlEpoch;
                if (addressesCurrentDecision &&
                    (!_aiObservationGenerations.TryGetValue(decision.Observation, out long observationGeneration) ||
                     observationGeneration != _recoveryGeneration))
                {
                    return TableTickResult.Rejected(
                        TableOperationStatus.Rejected,
                        TableRuntimeReasonCode.StaleRecoveryGeneration,
                        "The AI observation was not captured in the current recovery generation.",
                        _stateVersion);
                }
            }

            AiDecisionSubmission submission;
            try
            {
                submission = decision.ToDomainSubmission(now);
            }
            catch (Exception exception)
            {
                return TableTickResult.Rejected(
                    TableOperationStatus.Rejected,
                    TableRuntimeReasonCode.InvalidSubmission,
                    exception.Message,
                    _stateVersion);
            }

            var request = new TableTickRequest(new[] { submission });
            return await ProcessTickCoreAsync(request, now).ConfigureAwait(false);
        }

        private async ValueTask<TableTickResult> ProcessTickAsync(TableTickRequest request)
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return TableTickResult.Rejected(guard.Status, guard.ReasonCode, guard.Detail, _stateVersion);
            }

            if (!TryReadClock(out long now, out _, out string clockFailure))
            {
                return TableTickResult.Rejected(
                    TableOperationStatus.Failed,
                    clockFailure,
                    "The monotonic clock is not safe for authority.",
                    _stateVersion);
            }

            return await ProcessTickCoreAsync(request, now).ConfigureAwait(false);
        }

        private async ValueTask<TableTickResult> ProcessTickCoreAsync(TableTickRequest request, long now)
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return TableTickResult.Rejected(guard.Status, guard.ReasonCode, guard.Detail, _stateVersion);
            }

            List<PlannedTickEntry> planned;
            try
            {
                planned = PlanTick(request, now);
            }
            catch (Exception exception)
            {
                return TableTickResult.Rejected(
                    TableOperationStatus.Failed,
                    TableRuntimeReasonCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message,
                    _stateVersion);
            }

            var entries = new List<TableTickEntry>(planned.Count);
            bool commitAborted = false;
            bool roundFinished = false;

            for (int i = 0; i < planned.Count; i++)
            {
                PlannedTickEntry entry = planned[i];
                if (entry.Kind == TickEntryKind.AiRejected)
                {
                    entries.Add(new TableTickEntry(
                        entry.Kind,
                        entry.Seat,
                        entry.DecisionId,
                        entry.Action,
                        entry.RejectionCode,
                        entry.Detail,
                        null));
                    continue;
                }

                if (roundFinished)
                {
                    entries.Add(new TableTickEntry(
                        entry.Kind,
                        entry.Seat,
                        entry.DecisionId,
                        entry.Action,
                        entry.RejectionCode,
                        "Skipped because a previous transition ended the round.",
                        TableOperationResult.Rejected(
                            _stateVersion,
                            "ROUND_FINISHED",
                            "The round ended before this planned entry could run.")));
                    continue;
                }

                if (commitAborted)
                {
                    entries.Add(new TableTickEntry(
                        entry.Kind,
                        entry.Seat,
                        entry.DecisionId,
                        entry.Action,
                        entry.RejectionCode,
                        "Skipped because an earlier commit in this tick failed.",
                        TableOperationResult.Failed(
                            _stateVersion,
                            TableRuntimeReasonCode.CommitFailed,
                            "An earlier transition in this tick failed to commit; the decision was not consumed.")));
                    continue;
                }

                // Re-read the clock and revalidate the identity immediately before this item's
                // own authoritative transition. A slow earlier commit may have moved time past
                // this item's deadline; the earlier item itself remains valid because it was
                // adjudicated and started before its deadline.
                if (!TryReadClock(out long actionNow, out _, out string actionClockFailure))
                {
                    entries.Add(new TableTickEntry(
                        entry.Kind,
                        entry.Seat,
                        entry.DecisionId,
                        entry.Action,
                        entry.RejectionCode,
                        "Skipped because the monotonic clock is not safe.",
                        TableOperationResult.Failed(
                            _stateVersion,
                            actionClockFailure,
                            "The monotonic clock regressed before this planned action could execute.")));
                    commitAborted = true;
                    continue;
                }

                if (entry.Kind == TickEntryKind.Timeout)
                {
                    TableTickEntry timeoutEntry = await ExecuteDueTimeoutAsync(
                        entry.Seat,
                        entry.DecisionId,
                        actionNow).ConfigureAwait(false);
                    entries.Add(timeoutEntry);
                    if (timeoutEntry.Transition != null &&
                        timeoutEntry.Transition.Status == TableOperationStatus.Failed)
                    {
                        commitAborted = true;
                    }

                    if (_round.IsFinished)
                    {
                        roundFinished = true;
                    }

                    continue;
                }

                PendingDecision? aiPending;
                DecisionTiming? aiTiming;
                TableTickEntry? aiRejection = RevalidateAiEntry(
                    entry,
                    out aiPending,
                    out aiTiming);
                if (aiRejection != null)
                {
                    entries.Add(aiRejection);
                    continue;
                }

                if (actionNow >= aiTiming!.DeadlineMillis)
                {
                    // The AI was planned before the deadline but an earlier slow commit pushed
                    // its actual execution to/past the deadline. Timeout wins now; the AI result
                    // is reported late and never applied.
                    TableTickEntry timeoutEntry = await ExecuteDueTimeoutAsync(
                        entry.Seat,
                        entry.DecisionId,
                        actionNow).ConfigureAwait(false);
                    entries.Add(timeoutEntry);
                    if (timeoutEntry.Transition != null &&
                        timeoutEntry.Transition.Status == TableOperationStatus.Failed)
                    {
                        commitAborted = true;
                    }

                    entries.Add(new TableTickEntry(
                        TickEntryKind.AiRejected,
                        entry.Seat,
                        entry.DecisionId,
                        entry.Action,
                        TickRejectionCode.DeadlineExpired,
                        "The AI result became late while an earlier transition was committing.",
                        null));

                    if (_round.IsFinished)
                    {
                        roundFinished = true;
                    }

                    continue;
                }

                if (entry.Action == null || !aiPending!.Contains(entry.Action))
                {
                    entries.Add(new TableTickEntry(
                        TickEntryKind.AiRejected,
                        entry.Seat,
                        entry.DecisionId,
                        entry.Action,
                        TickRejectionCode.IllegalAction,
                        "The submitted action is no longer a legal candidate.",
                        null));
                    continue;
                }

                RoundCommand? command = MapAction(entry.Action);
                if (command == null)
                {
                    // Unsupported action: keep the exact timing/pending state so a later owner
                    // (or a corrected adapter) can still resolve the same decision.
                    entries.Add(new TableTickEntry(
                        TickEntryKind.AiAccepted,
                        entry.Seat,
                        aiPending.Key.DecisionId,
                        entry.Action,
                        entry.RejectionCode,
                        entry.Detail,
                        TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.UnsupportedAction,
                            "P3.1 supports only Pass/Discard timeout and AI actions; the decision was not consumed.")));
                    continue;
                }

                TableOperationResult result = await ApplyInternalCommandAsync(
                    command,
                    BuildInternalRequestId(entry.Seat, aiPending.Key.DecisionId),
                    actionNow).ConfigureAwait(false);

                entries.Add(new TableTickEntry(
                    TickEntryKind.AiAccepted,
                    entry.Seat,
                    aiPending.Key.DecisionId,
                    entry.Action,
                    entry.RejectionCode,
                    entry.Detail,
                    result));

                if (result.Status == TableOperationStatus.Accepted)
                {
                    if (_round.IsFinished)
                    {
                        // CommitAndInstall already cancelled the round's pending/timing state.
                        roundFinished = true;
                        continue;
                    }

                    bool completed = _decisionClock.Complete(aiTiming.Key, actionNow);
                    if (!completed)
                    {
                        SetFaulted(new InvalidOperationException(
                            "The decision clock no longer matched the committed AI transition."));
                    }

                    _pending.Remove(entry.Seat);
                }
                else if (result.Status == TableOperationStatus.Failed)
                {
                    // Infrastructure/commit failure: nothing was installed. Keep this and every
                    // later planned decision untouched so a retry can still resolve them.
                    commitAborted = true;
                }
            }

            return TableTickResult.Processed(entries, _stateVersion);
        }

        private TableTickEntry? RevalidateAiEntry(
            PlannedTickEntry entry,
            out PendingDecision? pending,
            out DecisionTiming? timing)
        {
            pending = null;
            timing = null;
            if (!_pending.TryGetValue(entry.Seat, out PendingDecision? current))
            {
                return new TableTickEntry(
                    TickEntryKind.AiRejected,
                    entry.Seat,
                    entry.DecisionId,
                    entry.Action,
                    TickRejectionCode.StaleDecision,
                    "No matching active decision at execution time.",
                    null);
            }

            timing = _decisionClock.Timing(entry.Seat);
            if (timing == null ||
                timing.Key != current.Key ||
                !string.Equals(current.Key.DecisionId, entry.DecisionId, StringComparison.Ordinal))
            {
                timing = null;
                return new TableTickEntry(
                    TickEntryKind.AiRejected,
                    entry.Seat,
                    entry.DecisionId,
                    entry.Action,
                    TickRejectionCode.StaleDecision,
                    "The decision identity changed before execution.",
                    null);
            }

            ControlValidation control = _control.ValidateAutomatedAction(entry.Seat, current.ControlEpoch);
            if (!control.IsValid)
            {
                timing = null;
                return new TableTickEntry(
                    TickEntryKind.AiRejected,
                    entry.Seat,
                    entry.DecisionId,
                    entry.Action,
                    control.Code == ControlValidationCode.EpochMismatch
                        ? TickRejectionCode.StaleEpoch
                        : TickRejectionCode.SeatNotAutomated,
                    control.Message,
                    null);
            }

            pending = current;
            return null;
        }

        private async ValueTask<TableTickEntry> ExecuteDueTimeoutAsync(
            int seat,
            string decisionId,
            long now)
        {
            if (!_pending.TryGetValue(seat, out PendingDecision? pending) ||
                !string.Equals(pending.Key.DecisionId, decisionId, StringComparison.Ordinal))
            {
                return new TableTickEntry(
                    TickEntryKind.Timeout,
                    seat,
                    decisionId,
                    null,
                    TickRejectionCode.StaleDecision,
                    "No matching active decision at timeout execution time.",
                    TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.StaleDecision,
                        "The decision was already closed or replaced; the timeout was not consumed."));
            }

            DecisionTiming? timing = _decisionClock.Timing(seat);
            if (timing == null || timing.Key != pending.Key)
            {
                return new TableTickEntry(
                    TickEntryKind.Timeout,
                    seat,
                    decisionId,
                    null,
                    TickRejectionCode.StaleDecision,
                    "The decision clock no longer matched the pending decision.",
                    TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.StaleDecision,
                        "The decision clock no longer matched the pending decision."));
            }

            if (now < timing.DeadlineMillis)
            {
                return new TableTickEntry(
                    TickEntryKind.Timeout,
                    seat,
                    decisionId,
                    null,
                    TickRejectionCode.None,
                    "The deadline is not due at execution time.",
                    TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.DeadlineNotDue,
                        "The deadline is not due; the timeout was not applied."));
            }

            LegalActionOption? fallback = TimeoutPolicy.Choose(pending);
            if (fallback == null)
            {
                return new TableTickEntry(
                    TickEntryKind.Timeout,
                    seat,
                    decisionId,
                    null,
                    TickRejectionCode.NoDefaultAction,
                    "No legal timeout default; the decision remains due.",
                    TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.NoDefaultAction,
                        "No legal timeout/AI action was available; the decision remains due."));
            }

            RoundCommand? command = MapAction(fallback);
            if (command == null)
            {
                return new TableTickEntry(
                    TickEntryKind.Timeout,
                    seat,
                    decisionId,
                    fallback,
                    TickRejectionCode.None,
                    "The timeout action is not supported by the current adapter.",
                    TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.UnsupportedAction,
                        "The timeout action is not supported; the decision was not consumed."));
            }

            TableOperationResult result = await ApplyInternalCommandAsync(
                command,
                BuildInternalRequestId(seat, pending.Key.DecisionId),
                now).ConfigureAwait(false);

            if (result.Status == TableOperationStatus.Accepted && !_round.IsFinished)
            {
                bool expired = _decisionClock.Expire(timing.Key, timing.Generation, now);
                if (!expired)
                {
                    SetFaulted(new InvalidOperationException(
                        "The decision clock no longer matched the committed timeout transition."));
                }

                _pending.Remove(seat);
            }

            return new TableTickEntry(
                TickEntryKind.Timeout,
                seat,
                pending.Key.DecisionId,
                fallback,
                TickRejectionCode.None,
                "Authoritative deadline reached; timeout action planned at execution time.",
                result);
        }

        /// <summary>
        /// Builds a pure candidate plan from the current timing/pending snapshot. This method
        /// never calls DecisionClock.Expire/Complete and never removes a pending decision.
        /// It mirrors Domain DeadlineArbiter ordering: all due timeouts first, then AI results.
        /// </summary>
        private List<PlannedTickEntry> PlanTick(TableTickRequest request, long now)
        {
            var pendingBySeat = new PendingDecision?[4];
            foreach (KeyValuePair<int, PendingDecision> pair in _pending)
            {
                pendingBySeat[pair.Key] = pair.Value;
            }

            var planned = new List<PlannedTickEntry>();
            var expiredSeats = new bool[4];
            var consumedSeats = new bool[4];
            bool clockForThisRound = string.Equals(
                _decisionClock.ActiveRoundId,
                _round.RoundId,
                StringComparison.Ordinal);

            if (clockForThisRound && !_decisionClock.IsPausedState)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    DecisionTiming? timing = _decisionClock.Timing(seat);
                    if (timing == null ||
                        !string.Equals(timing.RoundId, _round.RoundId, StringComparison.Ordinal) ||
                        now < timing.DeadlineMillis)
                    {
                        continue;
                    }

                    PendingDecision? pending = pendingBySeat[seat];
                    LegalActionOption? fallback = pending == null ? null : TimeoutPolicy.Choose(pending);
                    planned.Add(new PlannedTickEntry(
                        TickEntryKind.Timeout,
                        seat,
                        timing.DecisionId,
                        fallback,
                        fallback == null ? TickRejectionCode.NoDefaultAction : TickRejectionCode.None,
                        fallback == null
                            ? "No legal timeout default; the decision remains due."
                            : "Authoritative deadline reached; timeout action planned.",
                        timing));
                    expiredSeats[seat] = true;
                    consumedSeats[seat] = true;
                }
            }

            for (int i = 0; i < request.AiSubmissions.Count; i++)
            {
                AiDecisionSubmission submission = request.AiSubmissions[i];
                int seat = submission.Key.Seat;

                if (!string.Equals(submission.Key.RoundId, _round.RoundId, StringComparison.Ordinal) ||
                    !clockForThisRound)
                {
                    planned.Add(RejectAi(submission, TickRejectionCode.StaleRound, "Submission belongs to another round."));
                    continue;
                }

                ControlValidation control = _control.ValidateAutomatedAction(seat, submission.ControlEpoch);
                if (!control.IsValid)
                {
                    TickRejectionCode code = control.Code == ControlValidationCode.EpochMismatch
                        ? TickRejectionCode.StaleEpoch
                        : TickRejectionCode.SeatNotAutomated;
                    planned.Add(RejectAi(submission, code, control.Message));
                    continue;
                }

                if (_decisionClock.IsPausedState)
                {
                    planned.Add(RejectAi(submission, TickRejectionCode.ClockPaused, "Clock is paused."));
                    continue;
                }

                if (expiredSeats[seat])
                {
                    planned.Add(RejectAi(
                        submission,
                        TickRejectionCode.DeadlineExpired,
                        "The seat's deadline was already expired earlier in this tick."));
                    continue;
                }

                DecisionTiming? timing = _decisionClock.Timing(seat);
                if (consumedSeats[seat] || timing == null || timing.Key != submission.Key)
                {
                    planned.Add(RejectAi(
                        submission,
                        TickRejectionCode.StaleDecision,
                        "No matching active decision."));
                    continue;
                }

                if (now >= timing.DeadlineMillis)
                {
                    planned.Add(RejectAi(
                        submission,
                        TickRejectionCode.DeadlineExpired,
                        "Submission arrived at or after the authoritative deadline."));
                    continue;
                }

                PendingDecision? pending = pendingBySeat[seat];
                if (pending == null ||
                    pending.Key != submission.Key ||
                    !pending.Contains(submission.Action))
                {
                    planned.Add(RejectAi(
                        submission,
                        TickRejectionCode.IllegalAction,
                        "Submitted action is not a legal candidate."));
                    continue;
                }

                planned.Add(new PlannedTickEntry(
                    TickEntryKind.AiAccepted,
                    seat,
                    submission.Key.DecisionId,
                    submission.Action,
                    TickRejectionCode.None,
                    "AI result accepted before its deadline.",
                    timing));
                consumedSeats[seat] = true;
            }

            return planned;
        }

        private static PlannedTickEntry RejectAi(
            AiDecisionSubmission submission,
            TickRejectionCode code,
            string detail)
        {
            return new PlannedTickEntry(
                TickEntryKind.AiRejected,
                submission.Key.Seat,
                submission.Key.DecisionId,
                submission.Action,
                code,
                detail,
                null);
        }

        private sealed class PlannedTickEntry
        {
            public PlannedTickEntry(
                TickEntryKind kind,
                int seat,
                string decisionId,
                LegalActionOption? action,
                TickRejectionCode rejectionCode,
                string detail,
                DecisionTiming? timing)
            {
                Kind = kind;
                Seat = seat;
                DecisionId = decisionId ?? string.Empty;
                Action = action;
                RejectionCode = rejectionCode;
                Detail = detail ?? string.Empty;
                Timing = timing;
            }

            public TickEntryKind Kind { get; }

            public int Seat { get; }

            public string DecisionId { get; }

            public LegalActionOption? Action { get; }

            public TickRejectionCode RejectionCode { get; }

            public string Detail { get; }

            public DecisionTiming? Timing { get; }
        }

        private async ValueTask<TableOperationResult> ApplyInternalCommandAsync(
            RoundCommand command,
            RequestId requestId,
            long now)
        {
            RoundTransition transition;
            try
            {
                transition = _round.Execute(command);
            }
            catch (Exception exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message);
            }

            TableOperationResult? transitionError = ValidateTransition(transition);
            if (transitionError != null)
            {
                return transitionError;
            }

            return await CommitAndInstallAsync(transition, requestId, null, null, now).ConfigureAwait(false);
        }

        private async ValueTask<TableOperationResult> CommitAndInstallAsync(
            RoundTransition transition,
            RequestId requestId,
            PendingDecision? pending,
            DecisionId? decisionId,
            long now)
        {
            StateVersion evaluated = _stateVersion;
            StateVersion commandVersion;
            try
            {
                commandVersion = evaluated.Next();
            }
            catch (OverflowException)
            {
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.InternalError,
                    "State version overflow.");
            }

            IReadOnlyList<RoundEvent> domainEvents = transition.Events;
            if (domainEvents == null || domainEvents.Count == 0)
            {
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.StateEventMissing,
                    "The Domain accepted a state change without any event.");
            }

            EventSeq lastEventSeq;
            try
            {
                lastEventSeq = new EventSeq(checked(_lastEventSeq.Value + (ulong)domainEvents.Count));
            }
            catch (OverflowException)
            {
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.InternalError,
                    "Event sequence overflow.");
            }

            List<TableEventRecord> records = BuildEventRecords(commandVersion, domainEvents);
            TableCommandReceipt receipt = TableCommandReceipt.Applied(
                evaluated,
                commandVersion,
                lastEventSeq,
                requestId);

            RoundStateMachine nextState = transition.State!;
            var request = new TableCommitRequest(
                _options.RoomId,
                _ownerEpoch,
                evaluated,
                commandVersion,
                lastEventSeq,
                nextState.SourceFingerprint,
                records,
                receipt);

            TableCommitResult commit;
            try
            {
                commit = await _commitPort.CommitAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.CommitCancelled,
                    exception.Message);
            }
            catch (Exception exception)
            {
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.CommitFailed,
                    exception.GetType().Name + ": " + exception.Message);
            }

            if (commit == null)
            {
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.CommitFailed,
                    "The commit port returned no result.");
            }

            if (!commit.IsCommitted)
            {
                return CommitFailure(evaluated, commit);
            }

            // The commit is durable. From this point the in-memory state/receipt/publication
            // must follow it; no failure below may report a business rejection.
            _round = nextState;
            _stateVersion = commandVersion;
            _lastEventSeq = lastEventSeq;

            if (pending != null &&
                decisionId.HasValue &&
                string.Equals(decisionId.Value.Value, pending.Key.DecisionId, StringComparison.Ordinal))
            {
                bool closed = _decisionClock.Complete(pending.Key, now);
                if (!closed)
                {
                    SetFaulted(new InvalidOperationException(
                        "The decision clock could not close the committed player command."));
                }

                _pending.Remove(pending.Key.Seat);
            }

            if (_round.IsFinished)
            {
                _pending.Clear();
                _decisionClock.Reset();
            }

            AppendRecent(records);
            TableSnapshot snapshot = BuildSnapshot();
            Publish(new TablePublication(TablePublicationKind.Applied, snapshot, records, receipt));
            return TableOperationResult.Accepted(receipt, records);
        }

        private TableOperationResult ProcessOpenDecision(PendingDecision decision)
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return guard;
            }

            if (!TryReadClock(out long now, out _, out string clockFailure))
            {
                return TableOperationResult.Failed(_stateVersion, clockFailure, "The monotonic clock is not safe for authority.");
            }

            if (!string.Equals(decision.Key.RoundId, _round.RoundId, StringComparison.Ordinal))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.RoundMismatch,
                    "The decision belongs to another round.");
            }

            if (decision.ControlEpoch != _control.GetEpoch(decision.Key.Seat))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "The decision carries a stale control epoch.");
            }

            if (_round.IsFinished)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    "ROUND_FINISHED",
                    "Cannot open a decision on a finished round.");
            }

            if (_pending.ContainsKey(decision.Key.Seat))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.DecisionAlreadyOpen,
                    "Seat " + decision.Key.Seat + " already has an open decision.");
            }

            _decisionClock.BeginRound(_round.RoundId, now);
            DecisionTiming timing;
            try
            {
                timing = _decisionClock.Open(decision.Key, now);
            }
            catch (InvalidOperationException exception)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.DecisionAlreadyOpen,
                    exception.Message);
            }
            catch (OverflowException exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.DeadlineOverflow,
                    exception.Message);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.DeadlineOverflow,
                    exception.Message);
            }

            _pending[decision.Key.Seat] = decision;
            return TableOperationResult.Configured(
                _stateVersion,
                "Decision " + decision.Key.DecisionId + " opened with deadline " + timing.DeadlineMillis + ".");
        }

        private TableOperationResult ProcessBeginRecovery(string reason)
        {
            switch (_lifecycle)
            {
                case TableActorLifecycle.Closed:
                    return TableOperationResult.Rejected(_stateVersion, TableRuntimeReasonCode.ActorClosed, "Actor is closed.");
                case TableActorLifecycle.LeaseLost:
                    return TableOperationResult.Rejected(_stateVersion, TableRuntimeReasonCode.LeaseLost, "The table lease was lost.");
                case TableActorLifecycle.Faulted:
                    return TableOperationResult.Failed(_stateVersion, TableRuntimeReasonCode.InternalError, _lifecycleReason);
                case TableActorLifecycle.Recovering:
                    return TableOperationResult.Configured(_stateVersion, "Recovery is already in progress.");
            }

            long nextGeneration;
            try
            {
                nextGeneration = checked(_recoveryGeneration + 1L);
            }
            catch (OverflowException exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.RecoveryGenerationOverflow,
                    exception.Message);
            }

            _recoveryGeneration = nextGeneration;
            _lifecycle = TableActorLifecycle.Recovering;
            _lifecycleReason = reason ?? string.Empty;
            _pending.Clear();
            _aiObservationGenerations.Clear();
            // Keep the reserve ledger and active round until the baseline proves whether they
            // are still valid. Open timings are removed; tick/AI are refused while recovering.
            _decisionClock.ClearTimings();
            return TableOperationResult.Configured(
                _stateVersion,
                "Recovery started; new state-changing and control work is refused.");
        }

        private TableOperationResult ProcessCompleteRecovery(TableRecoveryBaseline baseline)
        {
            if (_lifecycle != TableActorLifecycle.Recovering)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.NotRecovering,
                    "No recovery is in progress.");
            }

            if (baseline.OwnerEpoch.Value < _ownerEpoch.Value)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleOwnerEpoch,
                    "The recovery baseline belongs to an older owner epoch.");
            }

            if (baseline.StateVersion.CompareTo(_stateVersion) < 0)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleStateVersion,
                    "The recovery baseline is older than the current state version.");
            }

            if (baseline.LastEventSeq.CompareTo(_lastEventSeq) < 0)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleEventSeq,
                    "The recovery baseline event sequence is older than the current event sequence.");
            }

            if (!TryReadClock(out long now, out _, out string clockFailure))
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    clockFailure,
                    "The monotonic clock is not safe for recovery installation.");
            }

            ControlRegistry candidateControl = _control;
            long candidateControlVersion = _controlVersion;
            if (baseline.Control != null)
            {
                if (baseline.Control.ControlVersion < _controlVersion)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.StaleControlVersion,
                        "The recovery control baseline is older than the current control version.");
                }

                for (int seat = 0; seat < 4; seat++)
                {
                    SeatControlState baselineSeat = baseline.Control.State.Seats[seat];
                    SeatControlState currentSeat = _control.GetState(seat);
                    if (baselineSeat.ControlEpoch < currentSeat.ControlEpoch)
                    {
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.RecoveryControlInvalid,
                            "Seat " + seat + " control epoch would move backwards during recovery.");
                    }

                    if (baselineSeat.ControlEpoch == currentSeat.ControlEpoch &&
                        baselineSeat.Kind != currentSeat.Kind)
                    {
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.RecoveryControlInvalid,
                            "Seat " + seat + " changes controller without advancing controlEpoch.");
                    }
                }

                TableOperationResult? graceError = TryBuildRecoveredControlState(
                    baseline.Control,
                    now,
                    out ControlRegistrySnapshot recoveredControl);
                if (graceError != null)
                {
                    return graceError;
                }

                candidateControl = ControlRegistry.Restore(recoveredControl);
                candidateControlVersion = baseline.Control.ControlVersion;
            }

            if (baseline.Timing.Decisions.Count > 0 && baseline.Control == null)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.RecoveryControlRequired,
                    "Restoring decision timing requires an exact control baseline.");
            }

            bool sameState = string.Equals(_round.RoundId, baseline.Round.RoundId, StringComparison.Ordinal) &&
                             _stateVersion == baseline.StateVersion &&
                             _lastEventSeq == baseline.LastEventSeq;
            IReadOnlyList<long>? recoveredReserves = baseline.Timing.SeatReservesMillis;
            if (recoveredReserves == null)
            {
                if (sameState)
                {
                    recoveredReserves = _decisionClock.RemainingReserves();
                }
                else if (baseline.Control != null)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "A cross-state/control recovery must supply all four idle-seat reserves explicitly.");
                }
            }

            var candidateClock = new DecisionClock(_decisionClockConfig);
            var candidatePending = new Dictionary<int, PendingDecision>();
            if (!baseline.Round.IsFinished)
            {
                candidateClock.BeginRound(baseline.Round.RoundId, now);
            }

            if (recoveredReserves != null)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    try
                    {
                        candidateClock.RestoreReserve(seat, recoveredReserves[seat]);
                    }
                    catch (ArgumentOutOfRangeException exception)
                    {
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.RecoveryTimingInvalid,
                            "Seat " + seat + " reserve is invalid: " + exception.Message);
                    }
                }
            }

            for (int i = 0; i < baseline.Timing.Decisions.Count; i++)
            {
                TableRecoveredDecision recovered = baseline.Timing.Decisions[i];
                if (baseline.Round.IsFinished)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "A finished round cannot restore an open decision.");
                }

                if (!string.Equals(recovered.Key.RoundId, baseline.Round.RoundId, StringComparison.Ordinal))
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "A restored decision belongs to another round.");
                }

                if (candidatePending.ContainsKey(recovered.Key.Seat))
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "A seat cannot have more than one restored decision.");
                }

                if (candidateControl.GetEpoch(recovered.Key.Seat) != recovered.ControlEpoch)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "A restored decision carries a control epoch that the control baseline does not own.");
                }

                try
                {
                    candidateClock.RestoreDecision(
                        recovered.Key,
                        now,
                        recovered.RemainingBaseMillis,
                        recovered.RemainingReserveMillis,
                        recovered.OverdueMillis);
                }
                catch (OverflowException exception)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        "Restored deadline overflows: " + exception.Message);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException ||
                    exception is ArgumentOutOfRangeException ||
                    exception is ArgumentException)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryTimingInvalid,
                        exception.Message);
                }

                candidatePending[recovered.Key.Seat] = new PendingDecision(
                    recovered.Key,
                    recovered.ControlEpoch,
                    recovered.Candidates,
                    recovered.DrawnTile);
            }

            _round = baseline.Round;
            _stateVersion = baseline.StateVersion;
            _lastEventSeq = baseline.LastEventSeq;
            _ownerEpoch = baseline.OwnerEpoch;
            _control = candidateControl;
            _controlVersion = candidateControlVersion;
            _decisionClock = candidateClock;
            _pending.Clear();
            foreach (KeyValuePair<int, PendingDecision> entry in candidatePending)
            {
                _pending[entry.Key] = entry.Value;
            }

            _recentEvents.Clear();
            _aiObservationGenerations.Clear();
            if (baseline.Control != null)
            {
                _strictRecoveryFence = true;
            }

            _lifecycle = TableActorLifecycle.Serving;
            _lifecycleReason = string.Empty;
            TableSnapshot snapshot = BuildSnapshot();
            Publish(new TablePublication(
                TablePublicationKind.Recovered,
                snapshot,
                Array.Empty<TableEventRecord>(),
                null,
                BuildControlSnapshot(),
                Array.Empty<TableControlEvent>()));
            return TableOperationResult.Configured(
                _stateVersion,
                "Recovery baseline installed at version " + _stateVersion + ".");
        }

        private TableOperationResult ProcessLeaseLost(OwnerEpoch ownerEpoch, string reason)
        {
            if (_lifecycle == TableActorLifecycle.Closed)
            {
                return TableOperationResult.Rejected(_stateVersion, TableRuntimeReasonCode.ActorClosed, "Actor is closed.");
            }

            if (_lifecycle == TableActorLifecycle.Faulted)
            {
                return TableOperationResult.Failed(_stateVersion, TableRuntimeReasonCode.InternalError, _lifecycleReason);
            }

            if (ownerEpoch != _ownerEpoch)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleOwnerEpoch,
                    "Lease-loss epoch " + ownerEpoch + " is not the current owner epoch " + _ownerEpoch + ".");
            }

            if (_lifecycle == TableActorLifecycle.LeaseLost)
            {
                return TableOperationResult.Configured(_stateVersion, "Lease was already lost.");
            }

            _lifecycle = TableActorLifecycle.LeaseLost;
            _lifecycleReason = reason ?? string.Empty;
            _pending.Clear();
            _decisionClock.Reset();
            TableSnapshot snapshot = BuildSnapshot();
            Publish(new TablePublication(
                TablePublicationKind.LeaseLost,
                snapshot,
                Array.Empty<TableEventRecord>(),
                null));
            return TableOperationResult.Configured(_stateVersion, "Lease lost; the actor no longer advances state.");
        }

        private async ValueTask<TableControlResult> ProcessDisconnectAsync(DisconnectMailboxMessage message)
        {
            TableControlResult? guard = GuardControlServing();
            if (guard != null)
            {
                return guard;
            }

            TableControlResult? owner = GuardControlOwner(message.OwnerEpoch);
            if (owner != null)
            {
                return owner;
            }

            if (!TryToDomainEpoch(message.ExpectedControlEpoch, out long expectedEpoch))
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "The expected control epoch exceeds the Domain range.");
            }

            if (_control.GetEpoch(message.Seat) != expectedEpoch)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "Expected control epoch " + expectedEpoch + " is not current for seat " + message.Seat + ".");
            }

            if (!TryReadClock(out long now, out long utc, out string clockFailure))
            {
                return ControlFailed(clockFailure, "The monotonic clock is not safe for control.");
            }

            ControlRegistry candidate = ControlRegistry.Restore(_control.Capture());
            SeatControlState before = candidate.GetState(message.Seat);
            SeatControlState after;
            try
            {
                after = candidate.Disconnect(message.Seat, now);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlTimeOverflow, exception.Message);
            }

            if (SameSeatControl(before, after))
            {
                return ControlUnchanged("Seat " + message.Seat + " is already disconnected.");
            }

            long nextVersion;
            try
            {
                nextVersion = checked(_controlVersion + 1L);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlVersionOverflow, exception.Message);
            }

            var change = CreateSeatControlEvent(
                nextVersion,
                TableControlChangeKind.Disconnect,
                message.Seat,
                before,
                after,
                now,
                utc,
                message.Reason);
            return await CommitControlChangeAsync(candidate.Capture(), change).ConfigureAwait(false);
        }

        private async ValueTask<TableControlResult> ProcessReconnectAsync(ReconnectMailboxMessage message)
        {
            TableControlResult? guard = GuardControlServing();
            if (guard != null)
            {
                return guard;
            }

            TableControlResult? owner = GuardControlOwner(message.OwnerEpoch);
            if (owner != null)
            {
                return owner;
            }

            if (!TryToDomainEpoch(message.ExpectedControlEpoch, out long expectedEpoch))
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "The expected control epoch exceeds the Domain range.");
            }

            if (_control.GetEpoch(message.Seat) != expectedEpoch)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "Expected control epoch " + expectedEpoch + " is not current for seat " + message.Seat + ".");
            }

            if (!TryReadClock(out long now, out long utc, out string clockFailure))
            {
                return ControlFailed(clockFailure, "The monotonic clock is not safe for control.");
            }

            ControlRegistry candidate = ControlRegistry.Restore(_control.Capture());
            SeatControlState before = candidate.GetState(message.Seat);
            SeatControlState after;
            try
            {
                after = candidate.Reconnect(message.Seat, now, false);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlTimeOverflow, exception.Message);
            }

            if (SameSeatControl(before, after))
            {
                return ControlUnchanged("Seat " + message.Seat + " is already connected and control was not reclaimed.");
            }

            long nextVersion;
            try
            {
                nextVersion = checked(_controlVersion + 1L);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlVersionOverflow, exception.Message);
            }

            var change = CreateSeatControlEvent(
                nextVersion,
                TableControlChangeKind.Reconnect,
                message.Seat,
                before,
                after,
                now,
                utc,
                message.Reason);
            return await CommitControlChangeAsync(candidate.Capture(), change).ConfigureAwait(false);
        }

        private async ValueTask<TableControlResult> ProcessGraceExpiryAsync(TableGraceExpiryFence fence)
        {
            TableControlResult? guard = GuardControlServing();
            if (guard != null)
            {
                return guard;
            }

            TableControlResult? owner = GuardControlOwner(fence.OwnerEpoch);
            if (owner != null)
            {
                return owner;
            }

            if (!string.Equals(fence.RoundId, _round.RoundId, StringComparison.Ordinal))
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleGrace,
                    "The grace callback belongs to round '" + fence.RoundId + "', current round is '" + _round.RoundId + "'.");
            }

            if (fence.ExpectedRecoveryGeneration != _recoveryGeneration)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleGrace,
                    "The grace callback belongs to recovery generation " + fence.ExpectedRecoveryGeneration +
                    ", current generation is " + _recoveryGeneration + ".");
            }

            if (!TryToDomainEpoch(fence.ExpectedControlEpoch, out long expectedEpoch))
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "The expected control epoch exceeds the Domain range.");
            }

            SeatControlState current = _control.GetState(fence.Seat);
            if (current.ControlEpoch != expectedEpoch)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "The grace callback carries control epoch " + expectedEpoch +
                    ", current is " + current.ControlEpoch + ".");
            }

            if (!current.GraceDeadlineMillis.HasValue)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.NoGraceDeadline,
                    "Seat " + fence.Seat + " has no grace deadline.");
            }

            if (current.GraceDeadlineMillis.Value != fence.ExpectedGraceDeadlineMillis)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleGrace,
                    "The grace callback deadline does not match the current grace deadline.");
            }

            if (!TryReadClock(out long now, out long utc, out string clockFailure))
            {
                return ControlFailed(clockFailure, "The monotonic clock is not safe for control.");
            }

            if (now < current.GraceDeadlineMillis.Value)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.GraceNotDue,
                    "The grace deadline has not been reached.");
            }

            ControlRegistry candidate = ControlRegistry.Restore(_control.Capture());
            SeatControlState before = candidate.GetState(fence.Seat);
            bool expired;
            try
            {
                expired = candidate.CheckGraceExpiration(fence.Seat, now, expectedEpoch);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlEpochOverflow, exception.Message);
            }

            if (!expired)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.GraceNotApplicable,
                    "The seat is no longer an eligible disconnected human seat.");
            }

            SeatControlState after = candidate.GetState(fence.Seat);
            long nextVersion;
            try
            {
                nextVersion = checked(_controlVersion + 1L);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlVersionOverflow, exception.Message);
            }

            var change = CreateSeatControlEvent(
                nextVersion,
                TableControlChangeKind.GraceExpired,
                fence.Seat,
                before,
                after,
                now,
                utc,
                "grace deadline reached");
            return await CommitControlChangeAsync(candidate.Capture(), change).ConfigureAwait(false);
        }

        private async ValueTask<TableControlResult> ProcessTakeoverAsync(TakeoverMailboxMessage message)
        {
            TableControlResult? guard = GuardControlServing();
            if (guard != null)
            {
                return guard;
            }

            TableControlResult? owner = GuardControlOwner(message.OwnerEpoch);
            if (owner != null)
            {
                return owner;
            }

            if (!TryToDomainEpoch(message.ExpectedControlEpoch, out long expectedEpoch))
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "The expected control epoch exceeds the Domain range.");
            }

            if (_control.GetEpoch(message.Seat) != expectedEpoch)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "Expected control epoch " + expectedEpoch + " is not current for seat " + message.Seat + ".");
            }

            if (!TryReadClock(out long now, out long utc, out string clockFailure))
            {
                return ControlFailed(clockFailure, "The monotonic clock is not safe for control.");
            }

            ControlRegistry candidate = ControlRegistry.Restore(_control.Capture());
            SeatControlState before = candidate.GetState(message.Seat);
            SeatControlState after;
            try
            {
                after = candidate.RequestTakeover(message.Seat);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlEpochOverflow, exception.Message);
            }

            if (SameSeatControl(before, after))
            {
                return ControlUnchanged("Seat " + message.Seat + " is already under takeover.");
            }

            long nextVersion;
            try
            {
                nextVersion = checked(_controlVersion + 1L);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlVersionOverflow, exception.Message);
            }

            var change = CreateSeatControlEvent(
                nextVersion,
                TableControlChangeKind.ExplicitTakeover,
                message.Seat,
                before,
                after,
                now,
                utc,
                message.Reason);
            return await CommitControlChangeAsync(candidate.Capture(), change).ConfigureAwait(false);
        }

        private async ValueTask<TableControlResult> ProcessReclaimAsync(ReclaimMailboxMessage message)
        {
            TableControlResult? guard = GuardControlServing();
            if (guard != null)
            {
                return guard;
            }

            TableControlResult? owner = GuardControlOwner(message.OwnerEpoch);
            if (owner != null)
            {
                return owner;
            }

            if (!TryToDomainEpoch(message.ExpectedControlEpoch, out long expectedEpoch))
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "The expected control epoch exceeds the Domain range.");
            }

            if (_control.GetEpoch(message.Seat) != expectedEpoch)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "Expected control epoch " + expectedEpoch + " is not current for seat " + message.Seat + ".");
            }

            if (!TryReadClock(out long now, out long utc, out string clockFailure))
            {
                return ControlFailed(clockFailure, "The monotonic clock is not safe for control.");
            }

            ControlRegistry candidate = ControlRegistry.Restore(_control.Capture());
            SeatControlState before = candidate.GetState(message.Seat);
            SeatControlState after;
            try
            {
                after = candidate.ReclaimControl(message.Seat);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlEpochOverflow, exception.Message);
            }

            if (SameSeatControl(before, after))
            {
                return ControlUnchanged("Seat " + message.Seat + " is already human-controlled.");
            }

            long nextVersion;
            try
            {
                nextVersion = checked(_controlVersion + 1L);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlVersionOverflow, exception.Message);
            }

            var change = CreateSeatControlEvent(
                nextVersion,
                TableControlChangeKind.Reclaimed,
                message.Seat,
                before,
                after,
                now,
                utc,
                message.Reason);
            return await CommitControlChangeAsync(candidate.Capture(), change).ConfigureAwait(false);
        }

        private async ValueTask<TableControlResult> ProcessConfigurePolicyAsync(ConfigurePolicyMailboxMessage message)
        {
            TableControlResult? guard = GuardControlServing();
            if (guard != null)
            {
                return guard;
            }

            TableControlResult? owner = GuardControlOwner(message.OwnerEpoch);
            if (owner != null)
            {
                return owner;
            }

            if (SamePolicy(_control.Policy, message.Policy))
            {
                return ControlUnchanged("The disconnect policy is unchanged.");
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (_control.GetState(seat).IsDisconnected)
                {
                    return ControlRejected(
                        TableRuntimeReasonCode.ControlPolicyBusy,
                        "Cannot change the disconnect policy while seat " + seat + " is disconnected.");
                }
            }

            if (!TryReadClock(out long now, out long utc, out string clockFailure))
            {
                return ControlFailed(clockFailure, "The monotonic clock is not safe for control.");
            }

            ControlRegistrySnapshot next = _control.Capture().WithPolicy(message.Policy);
            ControlRegistry candidate = ControlRegistry.Restore(next);
            long nextVersion;
            try
            {
                nextVersion = checked(_controlVersion + 1L);
            }
            catch (OverflowException exception)
            {
                return ControlFailed(TableRuntimeReasonCode.ControlVersionOverflow, exception.Message);
            }

            var change = new TableControlEvent(
                nextVersion,
                _round.RoundId,
                -1,
                TableControlChangeKind.PolicyConfigured,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                now,
                utc,
                message.Reason,
                null,
                null,
                message.Policy.Mode,
                message.Policy.GraceMillis,
                message.Policy.AiProfileId);
            return await CommitControlChangeAsync(candidate.Capture(), change).ConfigureAwait(false);
        }

        private async ValueTask<TableControlResult> CommitControlChangeAsync(
            ControlRegistrySnapshot next,
            TableControlEvent change)
        {
            if (_controlCommitPort != null)
            {
                var request = new TableControlCommitRequest(
                    _options.RoomId,
                    _ownerEpoch,
                    _controlVersion,
                    change.ControlVersion,
                    _stateVersion,
                    _round.RoundId,
                    change);
                TableControlCommitResult commit;
                try
                {
                    commit = await _controlCommitPort
                        .CommitControlAsync(request, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    return ControlFailed(TableRuntimeReasonCode.ControlCommitCancelled, exception.Message);
                }
                catch (Exception exception)
                {
                    return ControlFailed(
                        TableRuntimeReasonCode.ControlCommitFailed,
                        exception.GetType().Name + ": " + exception.Message);
                }

                if (commit == null)
                {
                    return ControlFailed(
                        TableRuntimeReasonCode.ControlCommitFailed,
                        "The control commit port returned no result.");
                }

                if (!commit.IsCommitted)
                {
                    return ControlCommitFailure(commit);
                }
            }

            _control = ControlRegistry.Restore(next);
            _controlVersion = change.ControlVersion;
            UpdatePendingControlEpoch(change.Seat, change.NewEpoch);
            TableControlSnapshot snapshot = BuildControlSnapshot();
            Publish(new TablePublication(
                TablePublicationKind.ControlChanged,
                BuildSnapshot(),
                null,
                null,
                snapshot,
                new[] { change }));
            return TableControlResult.Changed(snapshot, change, _stateVersion);
        }

        private TableAiObservationResult ProcessAiObservation(int seat)
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return AiObservationRejected(guard.ReasonCode, guard.Detail);
            }

            if (!TryReadClock(out long now, out _, out string clockFailure))
            {
                return AiObservationFailed(clockFailure, "The monotonic clock is not safe for AI capture.");
            }

            if (!_pending.TryGetValue(seat, out PendingDecision? pending))
            {
                return AiObservationRejected(
                    TableRuntimeReasonCode.StaleDecision,
                    "Seat " + seat + " has no open decision.");
            }

            DecisionTiming? timing = _decisionClock.Timing(seat);
            if (timing == null || timing.Key != pending.Key)
            {
                return AiObservationRejected(
                    TableRuntimeReasonCode.StaleDecision,
                    "The open decision is not bound to an active timing.");
            }

            if (now >= timing.DeadlineMillis)
            {
                return AiObservationRejected(
                    TableRuntimeReasonCode.DeadlineExpired,
                    "The authoritative deadline has already been reached.");
            }

            ControlValidation control = _control.ValidateAutomatedAction(seat, pending.ControlEpoch);
            if (!control.IsValid)
            {
                return AiObservationRejected(
                    control.Code == ControlValidationCode.EpochMismatch
                        ? TableRuntimeReasonCode.StaleControlEpoch
                        : TableRuntimeReasonCode.SeatNotAutomated,
                    control.Message);
            }

            if (pending.Candidates.Count == 0)
            {
                return AiObservationRejected(
                    TableRuntimeReasonCode.AiObservationUnavailable,
                    "The pending decision has no legal candidate for AI reasoning.");
            }

            if (_stateVersion.Value > long.MaxValue)
            {
                return AiObservationRejected(
                    TableRuntimeReasonCode.InternalError,
                    "State version exceeds the AI observation range.");
            }

            try
            {
                var observation = new AiObservation(
                    _options.RoomId,
                    pending.Key,
                    pending.ControlEpoch,
                    (long)_stateVersion.Value,
                    timing.DeadlineMillis,
                    _round.Phase,
                    _round.DealerSeat,
                    _round.RoundWind,
                    _round.RoundNumber,
                    _round.HonbaSticks,
                    _round.RiichiSticks,
                    _round.Tiles.WallRemaining,
                    _round.Tiles.TilesInZone(TileZone.Hand, seat),
                    DrawnTileFor(seat),
                    BuildVisibleMelds(seat),
                    BuildVisibleMelds(null),
                    BuildVisibleDiscards(),
                    _round.Tiles.DoraIndicators,
                    _round.Scores,
                    pending.Candidates);
                _aiObservationGenerations[observation] = _recoveryGeneration;
                return new TableAiObservationResult(
                    TableAiObservationStatus.Captured,
                    string.Empty,
                    string.Empty,
                    true,
                    observation);
            }
            catch (Exception exception)
            {
                return AiObservationFailed(
                    TableRuntimeReasonCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        private TileId? DrawnTileFor(int seat)
        {
            DrawnTileEvidence? evidence = _round.Tiles.DrawnTile;
            if (evidence.HasValue && evidence.Value.Seat == seat)
            {
                return evidence.Value.Tile;
            }

            return null;
        }

        private IReadOnlyList<AiVisibleMeld> BuildVisibleMelds(int? onlySeat)
        {
            var melds = new List<AiVisibleMeld>();
            for (int seat = 0; seat < 4; seat++)
            {
                if (onlySeat.HasValue && onlySeat.Value != seat)
                {
                    continue;
                }

                IReadOnlyList<TileId> tiles = _round.Tiles.TilesInZone(TileZone.Meld, seat);
                var groups = new List<TileId>[4];
                for (int i = 0; i < tiles.Count; i++)
                {
                    TileId tile = tiles[i];
                    int group = _round.Tiles[tile].Index / TilePositionMap.MeldGroupSize;
                    if (group < 0 || group >= groups.Length)
                    {
                        continue;
                    }

                    groups[group] ??= new List<TileId>();
                    groups[group].Add(tile);
                }

                for (int group = 0; group < groups.Length; group++)
                {
                    if (groups[group] != null && groups[group].Count >= 3)
                    {
                        melds.Add(new AiVisibleMeld(seat, groups[group]));
                    }
                }
            }

            return melds;
        }

        private IReadOnlyList<AiVisibleDiscard> BuildVisibleDiscards()
        {
            var discards = new List<AiVisibleDiscard>();
            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TileId> tiles = _round.Tiles.TilesInZone(TileZone.Discard, seat);
                for (int i = 0; i < tiles.Count; i++)
                {
                    discards.Add(new AiVisibleDiscard(seat, tiles[i]));
                }
            }

            return discards;
        }

        /// <summary>
        /// Rebuilds every disconnected seat's timestamps from explicit remaining/overdue grace
        /// at the local monotonic <paramref name="now"/>. Persisted absolute monotonic values
        /// from another process are never copied into the local control registry.
        /// </summary>
        private TableOperationResult? TryBuildRecoveredControlState(
            TableControlRecoveryBaseline baseline,
            long now,
            out ControlRegistrySnapshot recovered)
        {
            ControlRegistrySnapshot state = baseline.State;
            var graceBySeat = new TableRecoveredGrace?[4];
            if (baseline.GraceTiming != null)
            {
                for (int i = 0; i < baseline.GraceTiming.Seats.Count; i++)
                {
                    TableRecoveredGrace grace = baseline.GraceTiming.Seats[i];
                    graceBySeat[grace.Seat] = grace;
                }
            }

            var seats = new SeatControlState[4];
            for (int seat = 0; seat < 4; seat++)
            {
                SeatControlState persisted = state.Seats[seat];
                if (persisted.Status == ConnectionStatus.Connected)
                {
                    if (graceBySeat[seat] != null)
                    {
                        recovered = state;
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.RecoveryGraceTimingInvalid,
                            "Seat " + seat + " is connected but carries recovered grace timing.");
                    }

                    seats[seat] = persisted;
                    continue;
                }

                long disconnectedAt = now;
                long? graceDeadline = null;
                if (persisted.GraceDeadlineMillis.HasValue)
                {
                    TableRecoveredGrace? grace = graceBySeat[seat];
                    if (grace == null)
                    {
                        recovered = state;
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.RecoveryGraceTimingRequired,
                            "Disconnected seat " + seat +
                            " requires explicit remaining/overdue grace; persisted absolute timestamps are not portable.");
                    }

                    try
                    {
                        graceDeadline = grace.OverdueMillis > 0
                            ? now
                            : checked(now + grace.RemainingGraceMillis);
                    }
                    catch (OverflowException exception)
                    {
                        recovered = state;
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.RecoveryGraceTimingInvalid,
                            "Seat " + seat + " grace deadline overflows: " + exception.Message);
                    }
                }
                else if (graceBySeat[seat] != null)
                {
                    recovered = state;
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryGraceTimingInvalid,
                        "Seat " + seat + " has recovered grace timing but no persisted grace deadline.");
                }

                seats[seat] = new SeatControlState(
                    seat,
                    persisted.Status,
                    persisted.Kind,
                    persisted.ControlEpoch,
                    disconnectedAt,
                    graceDeadline,
                    persisted.AiProfileId);
            }

            recovered = new ControlRegistrySnapshot(state.Policy, seats, state.PreferredHumanKinds);
            return null;
        }

        private TableControlSnapshot BuildControlSnapshot()
        {
            var seats = new TableSeatControlView[4];
            for (int seat = 0; seat < 4; seat++)
            {
                SeatControlState state = _control.GetState(seat);
                seats[seat] = new TableSeatControlView(
                    state.Seat,
                    state.Status,
                    state.Kind,
                    state.ControlEpoch,
                    state.DisconnectedAtMillis,
                    state.GraceDeadlineMillis,
                    state.AiProfileId);
            }

            return new TableControlSnapshot(
                _options.RoomId,
                _round.RoundId,
                _ownerEpoch,
                _stateVersion,
                _controlVersion,
                _recoveryGeneration,
                _control.Policy,
                seats);
        }

        private TableTimingSnapshot BuildTimingSnapshot()
        {
            var seats = new TableSeatTimingView[4];
            for (int seat = 0; seat < 4; seat++)
            {
                DecisionKey? pendingKey = null;
                if (_pending.TryGetValue(seat, out PendingDecision? pending))
                {
                    pendingKey = pending.Key;
                }

                seats[seat] = new TableSeatTimingView(
                    seat,
                    _decisionClock.Timing(seat),
                    _decisionClock.RemainingReserve(seat),
                    pendingKey);
            }

            return new TableTimingSnapshot(
                _options.RoomId,
                _round.RoundId,
                _stateVersion,
                _recoveryGeneration,
                _decisionClock.IsPausedState,
                seats);
        }

        private TableControlEvent CreateSeatControlEvent(
            long controlVersion,
            TableControlChangeKind kind,
            int seat,
            SeatControlState before,
            SeatControlState after,
            long atMonotonicMillis,
            long atUtcMillis,
            string reason)
        {
            return new TableControlEvent(
                controlVersion,
                _round.RoundId,
                seat,
                kind,
                before.ControlEpoch,
                after.ControlEpoch,
                before.Kind,
                after.Kind,
                before.Status,
                after.Status,
                after.GraceDeadlineMillis,
                atMonotonicMillis,
                atUtcMillis,
                reason,
                after.DisconnectedAtMillis,
                after.AiProfileId);
        }

        private void UpdatePendingControlEpoch(int seat, long? newEpoch)
        {
            if (seat < 0 || !newEpoch.HasValue)
            {
                return;
            }

            if (!_pending.TryGetValue(seat, out PendingDecision? pending) ||
                pending.ControlEpoch == newEpoch.Value)
            {
                return;
            }

            _pending[seat] = new PendingDecision(
                pending.Key,
                newEpoch.Value,
                pending.Candidates,
                pending.DrawnTile);
        }

        private TableControlResult? GuardControlServing()
        {
            switch (_lifecycle)
            {
                case TableActorLifecycle.Serving:
                    return null;
                case TableActorLifecycle.Recovering:
                    return ControlRejected(
                        TableRuntimeReasonCode.Recovering,
                        "The table is recovering and refuses control side effects.");
                case TableActorLifecycle.LeaseLost:
                    return ControlRejected(
                        TableRuntimeReasonCode.LeaseLost,
                        "The table lease was lost.");
                case TableActorLifecycle.Closed:
                    return ControlRejected(
                        TableRuntimeReasonCode.ActorClosed,
                        "The actor is closed.");
                default:
                    return ControlFailed(
                        TableRuntimeReasonCode.InternalError,
                        _lifecycleReason);
            }
        }

        private TableControlResult? GuardControlOwner(OwnerEpoch ownerEpoch)
        {
            if (ownerEpoch != _ownerEpoch)
            {
                return ControlRejected(
                    TableRuntimeReasonCode.StaleOwnerEpoch,
                    "Control owner epoch " + ownerEpoch + " is not the current lease epoch " + _ownerEpoch + ".");
            }

            return null;
        }

        private TableControlResult ControlRejected(string reasonCode, string detail)
        {
            return TableControlResult.Rejected(
                _stateVersion,
                _controlVersion,
                reasonCode,
                detail,
                BuildControlSnapshot());
        }

        private TableControlResult ControlFailed(string reasonCode, string detail)
        {
            return TableControlResult.Failed(
                _stateVersion,
                _controlVersion,
                reasonCode,
                detail,
                BuildControlSnapshot());
        }

        private TableControlResult ControlUnchanged(string detail)
        {
            return TableControlResult.Unchanged(BuildControlSnapshot(), _stateVersion, detail);
        }

        private TableControlResult ControlCommitFailure(TableControlCommitResult commit)
        {
            switch (commit.Status)
            {
                case TableControlCommitStatus.StaleOwner:
                    return ControlFailed(TableRuntimeReasonCode.StaleOwnerEpoch, commit.Detail);
                case TableControlCommitStatus.StaleControl:
                    return ControlFailed(TableRuntimeReasonCode.StaleControlVersion, commit.Detail);
                case TableControlCommitStatus.Cancelled:
                    return ControlFailed(TableRuntimeReasonCode.ControlCommitCancelled, commit.Detail);
                default:
                    return ControlFailed(
                        string.IsNullOrWhiteSpace(commit.ReasonCode)
                            ? TableRuntimeReasonCode.ControlCommitFailed
                            : commit.ReasonCode,
                        commit.Detail);
            }
        }

        private static bool SameSeatControl(SeatControlState left, SeatControlState right)
        {
            return left.Status == right.Status &&
                   left.Kind == right.Kind &&
                   left.ControlEpoch == right.ControlEpoch &&
                   left.DisconnectedAtMillis == right.DisconnectedAtMillis &&
                   left.GraceDeadlineMillis == right.GraceDeadlineMillis &&
                   string.Equals(left.AiProfileId, right.AiProfileId, StringComparison.Ordinal);
        }

        private static bool SamePolicy(DisconnectPolicy left, DisconnectPolicy right)
        {
            return left.Mode == right.Mode &&
                   left.GraceMillis == right.GraceMillis &&
                   string.Equals(left.AiProfileId, right.AiProfileId, StringComparison.Ordinal);
        }

        private bool TryReadClock(out long monotonicMillis, out long utcMillis, out string failure)
        {
            long monotonic = _clockPort.MonotonicMillis;
            long utc = _clockPort.UtcNowMs;
            if (monotonic < 0 || utc < 0)
            {
                monotonicMillis = 0L;
                utcMillis = 0L;
                failure = TableRuntimeReasonCode.ClockInvalid;
                return false;
            }

            if (monotonic < _lastMonotonicMillis)
            {
                monotonicMillis = 0L;
                utcMillis = 0L;
                failure = TableRuntimeReasonCode.ClockRegression;
                return false;
            }

            _lastMonotonicMillis = monotonic;
            monotonicMillis = monotonic;
            utcMillis = utc;
            failure = string.Empty;
            return true;
        }

        private static void CheckSeat(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }
        }

        private TableAiObservationResult AiObservationRejected(string reasonCode, string detail)
        {
            return new TableAiObservationResult(
                TableAiObservationStatus.Rejected,
                reasonCode,
                detail,
                true,
                null);
        }

        private TableAiObservationResult AiObservationFailed(string reasonCode, string detail)
        {
            return new TableAiObservationResult(
                TableAiObservationStatus.Failed,
                reasonCode,
                detail,
                true,
                null);
        }

        private static TableAiObservationResult AiObservationAdmissionFailure(TableMailboxEnqueueStatus enqueue)
        {
            return enqueue == TableMailboxEnqueueStatus.Full
                ? new TableAiObservationResult(
                    TableAiObservationStatus.MailboxFull,
                    TableRuntimeReasonCode.MailboxFull,
                    "The table mailbox is full; the AI capture was not admitted.",
                    false,
                    null)
                : new TableAiObservationResult(
                    TableAiObservationStatus.Closed,
                    TableRuntimeReasonCode.ActorClosed,
                    "The table actor is closing/closed; the AI capture was not admitted.",
                    false,
                    null);
        }

        private static TableControlResult ControlAdmissionFailure(TableMailboxEnqueueStatus enqueue)
        {
            return enqueue == TableMailboxEnqueueStatus.Full
                ? TableControlResult.AdmissionFailure(
                    TableControlStatus.MailboxFull,
                    TableRuntimeReasonCode.MailboxFull,
                    "The table mailbox is full; the control message was not admitted.")
                : TableControlResult.AdmissionFailure(
                    TableControlStatus.Closed,
                    TableRuntimeReasonCode.ActorClosed,
                    "The table actor is closing/closed; the control message was not admitted.");
        }

        private ValueTask<TableControlResult> EnqueueControlMessage(TableMailboxMessage message)
        {
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableControlResult>(ControlAdmissionFailure(enqueue));
            }

            EnsurePump();
            switch (message)
            {
                case DisconnectMailboxMessage disconnect:
                    return new ValueTask<TableControlResult>(disconnect.Completion.Task);
                case ReconnectMailboxMessage reconnect:
                    return new ValueTask<TableControlResult>(reconnect.Completion.Task);
                case GraceExpiryMailboxMessage grace:
                    return new ValueTask<TableControlResult>(grace.Completion.Task);
                case TakeoverMailboxMessage takeover:
                    return new ValueTask<TableControlResult>(takeover.Completion.Task);
                case ReclaimMailboxMessage reclaim:
                    return new ValueTask<TableControlResult>(reclaim.Completion.Task);
                case ConfigurePolicyMailboxMessage policy:
                    return new ValueTask<TableControlResult>(policy.Completion.Task);
                default:
                    throw new InvalidOperationException("Not a control mailbox message.");
            }
        }

        private TableOperationResult? GuardServing()
        {
            switch (_lifecycle)
            {
                case TableActorLifecycle.Serving:
                    return null;
                case TableActorLifecycle.Recovering:
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.Recovering,
                        "The table is recovering and refuses state-changing work.");
                case TableActorLifecycle.LeaseLost:
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.LeaseLost,
                        "The table lease was lost.");
                case TableActorLifecycle.Closed:
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.ActorClosed,
                        "The actor is closed.");
                default:
                    return TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.InternalError,
                        _lifecycleReason);
            }
        }

        private TableOperationResult? ValidateTransition(RoundTransition transition)
        {
            if (!transition.IsAccepted)
            {
                RoundTransitionError? error = transition.Error;
                if (error == null)
                {
                    return TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.InternalError,
                        "The Domain rejected the command without a reason.");
                }

                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.FromDomain(error.Code),
                    error.Message);
            }

            if (transition.State == null)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.InternalError,
                    "The Domain accepted the command without a candidate state.");
            }

            long expectedDomainVersion;
            try
            {
                expectedDomainVersion = checked(_round.Version + 1L);
            }
            catch (OverflowException)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.DomainVersionMismatch,
                    "The Domain version would overflow.");
            }

            if (transition.State.Version != expectedDomainVersion)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.DomainVersionMismatch,
                    "Expected Domain version " + expectedDomainVersion + ", got " + transition.State.Version + ".");
            }

            if (transition.Events == null || transition.Events.Count == 0)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.StateEventMissing,
                    "The Domain accepted a state change without any event.");
            }

            return null;
        }

        private List<TableEventRecord> BuildEventRecords(
            StateVersion commandVersion,
            IReadOnlyList<RoundEvent> domainEvents)
        {
            var records = new List<TableEventRecord>(domainEvents.Count);
            ulong seq = _lastEventSeq.Value;
            for (int i = 0; i < domainEvents.Count; i++)
            {
                seq++;
                RoundEvent domainEvent = domainEvents[i];
                records.Add(new TableEventRecord(
                    new EventSeq(seq),
                    commandVersion,
                    i,
                    domainEvent.Type,
                    domainEvent.Seat,
                    domainEvent.Tile,
                    domainEvent.Detail));
            }

            return records;
        }

        private TableOperationResult CommitFailure(StateVersion evaluated, TableCommitResult commit)
        {
            switch (commit.Status)
            {
                case TableCommitStatus.StaleOwner:
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.StaleOwnerEpoch,
                        commit.Detail);
                case TableCommitStatus.StaleState:
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.StaleStateVersion,
                        commit.Detail);
                case TableCommitStatus.Cancelled:
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.CommitCancelled,
                        commit.Detail);
                default:
                    return TableOperationResult.Failed(
                        evaluated,
                        string.IsNullOrWhiteSpace(commit.ReasonCode)
                            ? TableRuntimeReasonCode.CommitFailed
                            : commit.ReasonCode,
                        commit.Detail);
            }
        }

        private RoundCommand? MapAction(LegalActionOption action)
        {
            switch (action.Kind)
            {
                case LegalActionKind.Pass:
                    return RoundCommand.Pass(_round.RoundId, action.Seat);
                case LegalActionKind.Discard:
                    return action.Tile.HasValue
                        ? RoundCommand.Discard(_round.RoundId, action.Seat, action.Tile.Value)
                        : null;
                default:
                    // Scoring/call/cheat actions need the P3.2/P3.3 adapters. P3.1 must not
                    // invent a settlement or a meld.
                    return null;
            }
        }

        private RequestId BuildInternalRequestId(int seat, string decisionId)
        {
            string id = "tick-" +
                        seat.ToString(CultureInfo.InvariantCulture) + "-" +
                        _lastEventSeq.Value.ToString(CultureInfo.InvariantCulture) + "-" +
                        decisionId;
            return new RequestId(id);
        }

        private void AppendRecent(IReadOnlyList<TableEventRecord> records)
        {
            for (int i = 0; i < records.Count; i++)
            {
                _recentEvents.Add(records[i]);
            }

            int window = _options.SnapshotEventWindow;
            if (window == 0)
            {
                _recentEvents.Clear();
            }
            else if (_recentEvents.Count > window)
            {
                _recentEvents.RemoveRange(0, _recentEvents.Count - window);
            }
        }

        private void Publish(TablePublication publication)
        {
            if (_publicationSink == null)
            {
                return;
            }

            try
            {
                _publicationSink.Publish(publication);
            }
            catch (Exception exception)
            {
                _publicationFault = exception.GetType().Name + ": " + exception.Message;
            }
        }

        private TableSnapshot BuildSnapshot()
        {
            var recent = new TableEventRecord[_recentEvents.Count];
            _recentEvents.CopyTo(recent);
            return new TableSnapshot(
                _options.RoomId,
                _lifecycle,
                _lifecycleReason,
                _stateVersion,
                _lastEventSeq,
                _ownerEpoch,
                _round.RoundId,
                _round.Phase,
                _round.CurrentSeat,
                _round.IsFinished,
                _round.Scores,
                _round.SourceFingerprint,
                recent);
        }

        private TableOperationResult AdmissionFailure(TableMailboxEnqueueStatus enqueue)
        {
            switch (enqueue)
            {
                case TableMailboxEnqueueStatus.Full:
                    return TableOperationResult.AdmissionFailure(
                        TableOperationStatus.MailboxFull,
                        TableRuntimeReasonCode.MailboxFull,
                        "The table mailbox is full; the command was not admitted.");
                case TableMailboxEnqueueStatus.Closed:
                    return TableOperationResult.AdmissionFailure(
                        TableOperationStatus.Closed,
                        TableRuntimeReasonCode.ActorClosed,
                        "The table actor is closing/closed; the command was not admitted.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(enqueue), enqueue, "Unexpected enqueue status.");
            }
        }

        private TableTickResult TickAdmissionFailure(TableMailboxEnqueueStatus enqueue)
        {
            // The caller thread must not read the pump-owned _stateVersion. An un-admitted
            // operation has no evaluated version; 0 is the explicit "not evaluated" sentinel.
            var notEvaluated = new StateVersion(0);
            if (enqueue == TableMailboxEnqueueStatus.Full)
            {
                return TableTickResult.Rejected(
                    TableOperationStatus.MailboxFull,
                    TableRuntimeReasonCode.MailboxFull,
                    "The table mailbox is full; the tick/AI result was not admitted.",
                    notEvaluated);
            }

            return TableTickResult.Rejected(
                TableOperationStatus.Closed,
                TableRuntimeReasonCode.ActorClosed,
                "The table actor is closing/closed; the tick/AI result was not admitted.",
                notEvaluated);
        }

        private void SetFaulted(Exception exception)
        {
            if (_lifecycle != TableActorLifecycle.Closed)
            {
                _lifecycle = TableActorLifecycle.Faulted;
                _lifecycleReason = exception.GetType().Name + ": " + exception.Message;
            }

            _mailbox.StopAccepting();
        }

        private void DrainAsFaulted(Exception exception)
        {
            while (_mailbox.TryDequeue(out TableMailboxMessage message))
            {
                CompleteFault(message, exception);
            }
        }

        private void CompleteFault(TableMailboxMessage message, Exception exception)
        {
            string detail = exception.GetType().Name + ": " + exception.Message;
            switch (message)
            {
                case CommandMailboxMessage command:
                    command.Completion.TrySetResult(TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail));
                    break;
                case OpenDecisionMailboxMessage open:
                    open.Completion.TrySetResult(TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail));
                    break;
                case TickMailboxMessage tick:
                    tick.Completion.TrySetResult(TableTickResult.Rejected(
                        TableOperationStatus.Failed,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        _stateVersion));
                    break;
                case AiMailboxMessage ai:
                    ai.Completion.TrySetResult(TableTickResult.Rejected(
                        TableOperationStatus.Failed,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        _stateVersion));
                    break;
                case DisconnectMailboxMessage disconnect:
                    disconnect.Completion.TrySetResult(TableControlResult.Failed(
                        _stateVersion,
                        _controlVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        BuildControlSnapshot()));
                    break;
                case ReconnectMailboxMessage reconnect:
                    reconnect.Completion.TrySetResult(TableControlResult.Failed(
                        _stateVersion,
                        _controlVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        BuildControlSnapshot()));
                    break;
                case GraceExpiryMailboxMessage grace:
                    grace.Completion.TrySetResult(TableControlResult.Failed(
                        _stateVersion,
                        _controlVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        BuildControlSnapshot()));
                    break;
                case TakeoverMailboxMessage takeover:
                    takeover.Completion.TrySetResult(TableControlResult.Failed(
                        _stateVersion,
                        _controlVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        BuildControlSnapshot()));
                    break;
                case ReclaimMailboxMessage reclaim:
                    reclaim.Completion.TrySetResult(TableControlResult.Failed(
                        _stateVersion,
                        _controlVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        BuildControlSnapshot()));
                    break;
                case ConfigurePolicyMailboxMessage policy:
                    policy.Completion.TrySetResult(TableControlResult.Failed(
                        _stateVersion,
                        _controlVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        BuildControlSnapshot()));
                    break;
                case AiObservationMailboxMessage aiObservation:
                    aiObservation.Completion.TrySetResult(new TableAiObservationResult(
                        TableAiObservationStatus.Failed,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        true,
                        null));
                    break;
                case ControlSnapshotMailboxMessage controlSnapshot:
                    controlSnapshot.Completion.TrySetException(exception);
                    break;
                case TimingSnapshotMailboxMessage timingSnapshot:
                    timingSnapshot.Completion.TrySetException(exception);
                    break;
                case BeginRecoveryMailboxMessage begin:
                    begin.Completion.TrySetResult(TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail));
                    break;
                case CompleteRecoveryMailboxMessage complete:
                    complete.Completion.TrySetResult(TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail));
                    break;
                case LeaseLostMailboxMessage leaseLost:
                    leaseLost.Completion.TrySetResult(TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.InternalError,
                        detail));
                    break;
                case SnapshotMailboxMessage snapshot:
                    snapshot.Completion.TrySetException(exception);
                    break;
                case CloseMailboxMessage close:
                    close.Completion.TrySetResult(true);
                    break;
            }
        }

        private static IReadOnlyDictionary<int, SeatControllerKind>? CopyControllers(
            IReadOnlyDictionary<int, SeatControllerKind>? controllers)
        {
            if (controllers == null)
            {
                return null;
            }

            var copy = new Dictionary<int, SeatControllerKind>(controllers.Count);
            foreach (KeyValuePair<int, SeatControllerKind> entry in controllers)
            {
                copy[entry.Key] = entry.Value;
            }

            return copy;
        }

        private static bool TryToDomainEpoch(ControlEpoch epoch, out long value)
        {
            if (epoch.Value > long.MaxValue)
            {
                value = 0;
                return false;
            }

            value = (long)epoch.Value;
            return true;
        }
    }
}
