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
    /// - a tick asks the Domain <see cref="DeadlineArbiter"/> for authoritative timeouts first
    ///   and only then applies same-tick AI results; at/after the deadline the timeout wins;
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
        private readonly ControlRegistry _control;
        private readonly DecisionClock _decisionClock;
        private readonly DeadlineArbiter _deadlineArbiter;
        private readonly Dictionary<int, PendingDecision> _pending = new Dictionary<int, PendingDecision>();
        private readonly List<TableEventRecord> _recentEvents = new List<TableEventRecord>();

        private RoundStateMachine _round;
        private StateVersion _stateVersion = new StateVersion(0);
        private EventSeq _lastEventSeq = new EventSeq(0);
        private OwnerEpoch _ownerEpoch;
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
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _round = initialRound ?? throw new ArgumentNullException(nameof(initialRound));
            _commitPort = commitPort ?? throw new ArgumentNullException(nameof(commitPort));
            _clockPort = clock ?? throw new ArgumentNullException(nameof(clock));
            _publicationSink = publicationSink;
            _ownerEpoch = options.InitialOwnerEpoch;
            _mailbox = new TableMailbox(options.MailboxCapacity, options.ControlReserve);
            _control = new ControlRegistry(options.DisconnectPolicy, CopyControllers(options.InitialControllers));
            _decisionClock = new DecisionClock(options.DecisionClockConfig);
            _deadlineArbiter = new DeadlineArbiter(_decisionClock, _control);
            _decisionClock.BeginRound(_round.RoundId, _clockPort.MonotonicMillis);
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
        /// Opens one decision. This is a P3.1 seam for the P3.3 control/timing owner; it does not
        /// advance state_version and does not publish.
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
            long now = _clockPort.MonotonicMillis;
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return guard;
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
            long now = _clockPort.MonotonicMillis;
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return TableTickResult.Rejected(guard.Status, guard.ReasonCode, guard.Detail, _stateVersion);
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
            long now = _clockPort.MonotonicMillis;
            return await ProcessTickCoreAsync(request, now).ConfigureAwait(false);
        }

        private async ValueTask<TableTickResult> ProcessTickCoreAsync(TableTickRequest request, long now)
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return TableTickResult.Rejected(guard.Status, guard.ReasonCode, guard.Detail, _stateVersion);
            }

            var pending = new List<PendingDecision>(_pending.Count);
            foreach (KeyValuePair<int, PendingDecision> pair in _pending)
            {
                pending.Add(pair.Value);
            }

            pending.Sort((left, right) => left.Key.Seat.CompareTo(right.Key.Seat));

            TickResult arbitrated;
            try
            {
                var input = new TickInput(_round.RoundId, now, pending, request.AiSubmissions);
                arbitrated = _deadlineArbiter.ProcessTick(input);
            }
            catch (Exception exception)
            {
                return TableTickResult.Rejected(
                    TableOperationStatus.Failed,
                    TableRuntimeReasonCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message,
                    _stateVersion);
            }

            var entries = new List<TableTickEntry>(arbitrated.Entries.Count);
            bool commitAborted = false;

            for (int i = 0; i < arbitrated.Entries.Count; i++)
            {
                TickEntry entry = arbitrated.Entries[i];
                if (entry.Kind == TickEntryKind.AiRejected)
                {
                    entries.Add(new TableTickEntry(
                        entry.Kind,
                        entry.Seat,
                        entry.DecisionId,
                        entry.Action,
                        entry.RejectionCode,
                        entry.Detail ?? string.Empty,
                        null));
                    continue;
                }

                // Timeout and accepted-AI entries consume the decision in the Domain clock.
                _pending.Remove(entry.Seat);

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
                            "An earlier transition in this tick failed to commit.")));
                    continue;
                }

                if (entry.Action == null)
                {
                    entries.Add(new TableTickEntry(
                        entry.Kind,
                        entry.Seat,
                        entry.DecisionId,
                        null,
                        entry.RejectionCode,
                        entry.Detail ?? string.Empty,
                        TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.NoDefaultAction,
                            "No legal timeout/AI action was available.")));
                    continue;
                }

                RoundCommand? command = MapAction(entry.Action);
                if (command == null)
                {
                    entries.Add(new TableTickEntry(
                        entry.Kind,
                        entry.Seat,
                        entry.DecisionId,
                        entry.Action,
                        entry.RejectionCode,
                        entry.Detail ?? string.Empty,
                        TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.UnsupportedAction,
                            "P3.1 supports only Pass/Discard timeout and AI actions; scoring actions are P3.2/P3.3.")));
                    continue;
                }

                TableOperationResult result = await ApplyInternalCommandAsync(
                    command,
                    BuildInternalRequestId(entry),
                    now).ConfigureAwait(false);

                entries.Add(new TableTickEntry(
                    entry.Kind,
                    entry.Seat,
                    entry.DecisionId,
                    entry.Action,
                    entry.RejectionCode,
                    entry.Detail ?? string.Empty,
                    result));

                if (result.Status == TableOperationStatus.Failed)
                {
                    commitAborted = true;
                }
            }

            return TableTickResult.Processed(entries, _stateVersion);
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
                _decisionClock.Complete(pending.Key, now);
                _pending.Remove(pending.Key.Seat);
            }

            if (_round.IsFinished)
            {
                _pending.Clear();
            }

            AppendRecent(records);
            TableSnapshot snapshot = BuildSnapshot();
            Publish(new TablePublication(TablePublicationKind.Applied, snapshot, records, receipt));
            return TableOperationResult.Accepted(receipt, records);
        }

        private TableOperationResult ProcessOpenDecision(PendingDecision decision)
        {
            long now = _clockPort.MonotonicMillis;
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return guard;
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

            _lifecycle = TableActorLifecycle.Recovering;
            _lifecycleReason = reason ?? string.Empty;
            _pending.Clear();
            _decisionClock.Reset();
            return TableOperationResult.Configured(_stateVersion, "Recovery started; new state-changing work is refused.");
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

            long now = _clockPort.MonotonicMillis;
            _round = baseline.Round;
            _stateVersion = baseline.StateVersion;
            _lastEventSeq = baseline.LastEventSeq;
            _ownerEpoch = baseline.OwnerEpoch;
            _pending.Clear();
            _recentEvents.Clear();
            _decisionClock.Reset();
            if (!_round.IsFinished)
            {
                _decisionClock.BeginRound(_round.RoundId, now);
            }

            _lifecycle = TableActorLifecycle.Serving;
            _lifecycleReason = string.Empty;
            TableSnapshot snapshot = BuildSnapshot();
            Publish(new TablePublication(
                TablePublicationKind.Recovered,
                snapshot,
                Array.Empty<TableEventRecord>(),
                null));
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

        private RequestId BuildInternalRequestId(TickEntry entry)
        {
            string id = "tick-" +
                        entry.Seat.ToString(CultureInfo.InvariantCulture) + "-" +
                        _lastEventSeq.Value.ToString(CultureInfo.InvariantCulture) + "-" +
                        entry.DecisionId;
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
