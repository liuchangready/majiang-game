#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// One-command execution boundary shared by the production full-match driver
    /// and the PlayMode regression fixtures. It owns planning, the single canonical
    /// submit, the bounded submit/confirmation waits, the strict visible-event
    /// confirmation and the request ledger. It contains no Unity API so the exact
    /// production control flow can be compiled and driven deterministically by the
    /// standalone table-model tests.
    /// </summary>
    public enum FullMatchExecutionStatus
    {
        Waiting = 0,
        Confirmed = 1,
        Replanned = 2,
        RoundComplete = 3,
        Failed = 4
    }

    /// <summary>
    /// Where a failure originated. Planning failures keep the driver's historical
    /// no-decision exit status; submit/confirmation failures keep their existing
    /// action-unconfirmed / missing-event statuses.
    /// </summary>
    public enum FullMatchExecutionFailureStage
    {
        None = 0,
        Planning = 1,
        Submission = 2,
        Confirmation = 3
    }

    public enum FullMatchExecutionNoticeKind
    {
        PlanRecorded = 0,
        ActionConfirmed = 1,
        ReceiptObserved = 2,
        ActionReplanned = 3
    }

    /// <summary>Production fact emitted by the boundary so the caller only formats it.</summary>
    public sealed class FullMatchExecutionNotice
    {
        internal FullMatchExecutionNotice(
            FullMatchExecutionNoticeKind kind,
            FullMatchActionPlan plan,
            bool visibleEvent,
            ReceiptStatus? receiptStatus,
            string receiptReason,
            ulong evaluatedStateVersion,
            ulong stateVersion,
            string candidates)
        {
            Kind = kind;
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            VisibleEvent = visibleEvent;
            ReceiptStatus = receiptStatus;
            ReceiptReason = receiptReason ?? string.Empty;
            EvaluatedStateVersion = evaluatedStateVersion;
            StateVersion = stateVersion;
            Candidates = candidates ?? string.Empty;
        }

        public FullMatchExecutionNoticeKind Kind { get; }

        public FullMatchActionPlan Plan { get; }

        public bool VisibleEvent { get; }

        public ReceiptStatus? ReceiptStatus { get; }

        public string ReceiptReason { get; }

        public ulong EvaluatedStateVersion { get; }

        public ulong StateVersion { get; }

        public string Candidates { get; }
    }

    /// <summary>
    /// Bounded waits are explicit so a fixture can inject a short, real budget
    /// instead of sleeping. The production values are the accepted driver waits
    /// (20s submit / 30s confirmation / 60s decision); the decision wait only
    /// applies to this seat's own DISCARD phase.
    /// </summary>
    public sealed class FullMatchExecutionBudget
    {
        public FullMatchExecutionBudget(
            long submitTimeoutMs,
            long confirmationTimeoutMs,
            long decisionWaitTimeoutMs)
        {
            if (submitTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(submitTimeoutMs));
            if (confirmationTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(confirmationTimeoutMs));
            if (decisionWaitTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(decisionWaitTimeoutMs));
            SubmitTimeoutMs = submitTimeoutMs;
            ConfirmationTimeoutMs = confirmationTimeoutMs;
            DecisionWaitTimeoutMs = decisionWaitTimeoutMs;
        }

        public long SubmitTimeoutMs { get; }

        public long ConfirmationTimeoutMs { get; }

        public long DecisionWaitTimeoutMs { get; }

        /// <summary>The accepted production driver waits (20s / 30s / 60s).</summary>
        public static FullMatchExecutionBudget Production { get; } =
            new FullMatchExecutionBudget(20_000L, 30_000L, 60_000L);
    }

    public sealed class FullMatchExecutionOutcome
    {
        internal FullMatchExecutionOutcome(
            FullMatchExecutionStatus status,
            FullMatchActionPlan? plan,
            string code,
            string detail,
            bool confirmedByVisibleEvent,
            ReceiptStatus? receiptStatus,
            string receiptReason,
            FullMatchExecutionFailureStage failureStage)
        {
            Status = status;
            Plan = plan;
            Code = code ?? string.Empty;
            Detail = detail ?? string.Empty;
            ConfirmedByVisibleEvent = confirmedByVisibleEvent;
            ReceiptStatus = receiptStatus;
            ReceiptReason = receiptReason ?? string.Empty;
            FailureStage = failureStage;
        }

        public FullMatchExecutionStatus Status { get; }

        public FullMatchActionPlan? Plan { get; }

        public string Code { get; }

        public string Detail { get; }

        public bool ConfirmedByVisibleEvent { get; }

        public ReceiptStatus? ReceiptStatus { get; }

        public string ReceiptReason { get; }

        public FullMatchExecutionFailureStage FailureStage { get; }

        internal static FullMatchExecutionOutcome Waiting(string detail) =>
            new FullMatchExecutionOutcome(
                FullMatchExecutionStatus.Waiting, null, string.Empty, detail, false, null,
                string.Empty, FullMatchExecutionFailureStage.None);

        internal static FullMatchExecutionOutcome Confirmed(
            FullMatchActionPlan plan,
            bool visibleEvent) =>
            new FullMatchExecutionOutcome(
                FullMatchExecutionStatus.Confirmed, plan, string.Empty, string.Empty, visibleEvent, null,
                string.Empty, FullMatchExecutionFailureStage.None);

        internal static FullMatchExecutionOutcome Replanned(FullMatchActionPlan plan, string reason) =>
            new FullMatchExecutionOutcome(
                FullMatchExecutionStatus.Replanned, plan, string.Empty, reason, false, null,
                reason, FullMatchExecutionFailureStage.None);

        internal static FullMatchExecutionOutcome RoundComplete(string detail) =>
            new FullMatchExecutionOutcome(
                FullMatchExecutionStatus.RoundComplete, null, string.Empty, detail, false, null,
                string.Empty, FullMatchExecutionFailureStage.None);

        internal static FullMatchExecutionOutcome Failed(
            FullMatchActionPlan? plan,
            string code,
            string detail,
            ReceiptStatus? receiptStatus,
            string receiptReason,
            FullMatchExecutionFailureStage failureStage) =>
            new FullMatchExecutionOutcome(
                FullMatchExecutionStatus.Failed, plan, code, detail, false, receiptStatus, receiptReason,
                failureStage);
    }

    /// <summary>
    /// The transport seam. Production supplies the real Client session; fixtures
    /// supply the same shape with a counting command sink and a scripted view.
    /// </summary>
    public interface IFullMatchExecutionTransport
    {
        ISessionView View { get; }

        Task<CommandSubmission> SubmitAsync(ProtocolCommand command, CancellationToken cancellationToken);

        event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved;
    }

    public sealed class FullMatchExecutionBoundary : IDisposable
    {
        private readonly IFullMatchExecutionTransport _transport;
        private readonly FullMatchRequestLedger _ledger;
        private readonly FullMatchExecutionBudget _budget;
        private readonly Action<FullMatchExecutionNotice>? _onNotice;
        private readonly Action<string, string>? _onDiagnostic;
        private readonly object _gate = new object();

        private string _lastCompletedDecisionKey = string.Empty;
        private string _lastCompletedDrawKey = string.Empty;
        private string _decisionWaitRoundId = string.Empty;
        private long _decisionWaitStartedAt = -1L;

        private FullMatchActionPlan? _activePlan;
        private ConfirmedVisibleEventObserver? _observer;
        private EventHandler<TableReceiptResolvedEventArgs>? _receiptHandler;
        private EventHandler<TableReceiptResolvedEventArgs>? _receiptDiagnosticHandler;
        private TableReceiptResolvedEventArgs? _matchingReceipt;
        private Task<CommandSubmission>? _submit;
        private bool _submitObserved;
        private string _lastPendingDiagnostic = string.Empty;
        private long _submitDeadline;
        private long _confirmDeadline;
        private FullMatchExecutionOutcome? _terminalOutcome;
        private bool _disposed;

        public FullMatchExecutionBoundary(
            IFullMatchExecutionTransport transport,
            FullMatchRequestLedger ledger,
            FullMatchExecutionBudget budget,
            Action<FullMatchExecutionNotice>? onNotice = null,
            Action<string, string>? onDiagnostic = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
            _onNotice = onNotice;
            _onDiagnostic = onDiagnostic;
        }

        public bool HasActiveRequest
        {
            get
            {
                lock (_gate)
                {
                    return _activePlan != null;
                }
            }
        }

        public FullMatchRequestLedger Ledger => _ledger;

        /// <summary>
        /// Advances at most to the next externally visible boundary. A completed
        /// submission may confirm in the same call when the receipt and the action
        /// publication are already present; otherwise it returns Waiting and the
        /// caller yields one frame before polling again.
        /// </summary>
        public FullMatchExecutionOutcome Poll(
            SessionViewState session,
            PlayerTableViewState view,
            TableCommandIntentFactory factory,
            long nowMs)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FullMatchExecutionBoundary));
                }

                if (_terminalOutcome != null)
                {
                    return _terminalOutcome;
                }

                if (_activePlan != null)
                {
                    return Advance(nowMs, view);
                }

                if (!string.Equals(_decisionWaitRoundId, view.Identity.RoundId, StringComparison.Ordinal))
                {
                    _decisionWaitRoundId = view.Identity.RoundId;
                    _decisionWaitStartedAt = -1L;
                }

                FullMatchStep step = PlanStep(session, view, factory);
                switch (step.Kind)
                {
                    case FullMatchStepKind.Wait:
                        return EvaluateDecisionWait(view, nowMs);
                    case FullMatchStepKind.Failed:
                        return Halt(FullMatchExecutionOutcome.Failed(
                            null, step.Code, step.Detail, null, string.Empty,
                            FullMatchExecutionFailureStage.Planning));
                    case FullMatchStepKind.CompleteRound:
                    case FullMatchStepKind.Finished:
                        return FullMatchExecutionOutcome.RoundComplete(step.Detail);
                    case FullMatchStepKind.SubmitAction:
                    case FullMatchStepKind.SubmitMandatoryDraw:
                        return Begin(step.Plan!, session, view, nowMs);
                    default:
                        return FullMatchExecutionOutcome.Waiting("unknown plan step");
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                // Disposal is not a confirmation. An in-flight request whose
                // outcome is still unknown keeps its original id/bytes fence as
                // unresolved; it must never become freely re-issuable under a
                // replacement id.
                if (_activePlan != null)
                {
                    _ledger.MarkUnresolved(_activePlan);
                    Diag(
                        "boundary_disposed_unresolved",
                        "\"requestId\":\"" + _activePlan.RequestId + "\"" +
                        ",\"roundId\":\"" + _activePlan.RoundId + "\"" +
                        ",\"action\":\"" + _activePlan.Action + "\"");
                }

                CleanupActive();
            }
        }

        private FullMatchExecutionOutcome Begin(
            FullMatchActionPlan plan,
            SessionViewState session,
            PlayerTableViewState view,
            long nowMs)
        {
            if (!_ledger.Record(plan, out string existing))
            {
                return Halt(FullMatchExecutionOutcome.Failed(
                    plan,
                    "REQUEST_ID_CONFLICT",
                    "The same logical action was issued two different request ids ('" +
                    existing + "' vs '" + plan.RequestId + "').",
                    null,
                    string.Empty,
                    FullMatchExecutionFailureStage.Submission));
            }

            _activePlan = plan;
            _matchingReceipt = null;
            _submitObserved = false;
            _lastPendingDiagnostic = string.Empty;
            _decisionWaitStartedAt = -1L;
            _observer = new ConfirmedVisibleEventObserver(_transport.View);
            _observer.BeginAction(session, plan.RequestId, plan.ScopeId, plan.Action);
            _receiptHandler = (_, args) =>
            {
                if (string.Equals(args.Command.RequestId, plan.RequestId, StringComparison.Ordinal) &&
                    string.Equals(args.Command.ScopeId, plan.ScopeId, StringComparison.Ordinal))
                {
                    // Receipts may resolve on the transport's thread while Poll runs
                    // on the main thread; keep the single in-flight receipt under the
                    // same gate as Advance/Cleanup and ignore a late receipt after
                    // this action was cleaned up.
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activePlan, plan))
                        {
                            _matchingReceipt = args;
                        }
                    }
                }
            };
            _receiptDiagnosticHandler = (_, args) =>
            {
                // Log every transport receipt, before the plan/scope filter, so
                // evidence distinguishes "no resolution reached the boundary" from
                // "a mismatched resolution was rejected by the strict fence".
                bool matches =
                    string.Equals(args.Command.RequestId, plan.RequestId, StringComparison.Ordinal) &&
                    string.Equals(args.Command.ScopeId, plan.ScopeId, StringComparison.Ordinal);
                Diag(
                    "client_receipt_seen",
                    "\"requestId\":\"" + args.Command.RequestId + "\"" +
                    ",\"scopeId\":\"" + args.Command.ScopeId + "\"" +
                    ",\"matchesPlan\":" + Bool(matches) +
                    ",\"disposition\":\"" + args.Resolution.Disposition + "\"" +
                    ",\"status\":\"" + args.Resolution.ReceiptStatus + "\"" +
                    ",\"reason\":\"" + args.Resolution.ReasonCode + "\"" +
                    ",\"commandVersion\":" + (args.Resolution.CommandVersion.HasValue
                        ? args.Resolution.CommandVersion.Value.Value.ToString(CultureInfo.InvariantCulture)
                        : "null") +
                    ",\"evaluatedStateVersion\":" + args.Resolution.EvaluatedStateVersion.Value.ToString(CultureInfo.InvariantCulture) +
                    ",\"eventSeq\":" + (args.Resolution.EventSeq.HasValue
                        ? args.Resolution.EventSeq.Value.Value.ToString(CultureInfo.InvariantCulture)
                        : "null") +
                    ",\"fromLookup\":" + Bool(args.Resolution.FromReceiptLookup));
            };
            _transport.ReceiptResolved += _receiptHandler;
            _transport.ReceiptResolved += _observer.OnReceiptResolved;
            _transport.ReceiptResolved += _receiptDiagnosticHandler;
            _submitDeadline = Add(nowMs, _budget.SubmitTimeoutMs);
            _confirmDeadline = Add(nowMs, _budget.ConfirmationTimeoutMs);

            Notice(new FullMatchExecutionNotice(
                FullMatchExecutionNoticeKind.PlanRecorded,
                plan,
                false,
                null,
                string.Empty,
                0UL,
                view.Identity.StateVersion,
                CandidateActions(view)));

            // Exactly one submit per logical action. The original bytes are kept
            // for any reconciliation; a failure never mints a replacement id.
            try
            {
                _submit = _transport.SubmitAsync(plan.Command, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _ledger.MarkUnresolved(plan);
                return FailActive(
                    "SUBMIT_NOT_SENT",
                    exception.GetType().Name + ": " + exception.Message,
                    FullMatchExecutionFailureStage.Submission);
            }

            ObserveSubmission(plan);
            return Advance(nowMs, view);
        }

        private FullMatchExecutionOutcome Advance(long nowMs, PlayerTableViewState view)
        {
            FullMatchActionPlan plan = _activePlan!;
            Task<CommandSubmission> submit = _submit!;
            if (!submit.IsCompleted)
            {
                if (nowMs >= _submitDeadline)
                {
                    _ledger.MarkUnresolved(plan);
                    return FailActive(
                        "SUBMIT_TIMEOUT",
                        "The original request '" + plan.RequestId +
                        "' did not resolve within " + _budget.SubmitTimeoutMs +
                        "ms; it will not be retried under a new id.",
                        FullMatchExecutionFailureStage.Submission);
                }

                return FullMatchExecutionOutcome.Waiting("submit in flight");
            }

            ObserveSubmission(plan);

            if (submit.IsFaulted || submit.IsCanceled)
            {
                _ledger.MarkUnresolved(plan);
                return FailActive(
                    "SUBMIT_NOT_SENT",
                    DescribeTaskFailure(submit),
                    FullMatchExecutionFailureStage.Submission);
            }

            if (!submit.Result.Sent)
            {
                _ledger.MarkUnresolved(plan);
                return FailActive(
                    "SUBMIT_NOT_SENT",
                    "The submission was not handed to the transport; request '" +
                    plan.RequestId + "' stays unresolved.",
                    FullMatchExecutionFailureStage.Submission);
            }

            ConfirmedVisibleActionEvent? confirmedEvent = _observer!.TryGetConfirmation();
            if (confirmedEvent != null &&
                LocalFullMatchPolicy.IsOwnActionEvent(
                    plan,
                    confirmedEvent.Batch,
                    confirmedEvent.Event,
                    confirmedEvent.CommandVersion))
            {
                return Confirm(plan, visibleEvent: true);
            }

            // The mandatory draw at an exhausted wall has no TileDrawn event; the
            // authoritative RoundEnded settlement itself is the confirmation. The
            // observer already preserves every immutable accepted batch, so a later
            // empty/different publication cannot erase a terminal observed before
            // the Applied receipt arrived. Only the exact Applied receipt version and
            // the existing identity/round/role/scope/version fence may pair a batch.
            if (plan.Action == PlayerActionKind.DrawTile &&
                _matchingReceipt != null &&
                _matchingReceipt.Resolution.Disposition == OutboxDisposition.Applied &&
                _matchingReceipt.Resolution.ReceiptStatus == ReceiptStatus.Applied &&
                _matchingReceipt.Resolution.CommandVersion != null)
            {
                ulong appliedVersion = _matchingReceipt.Resolution.CommandVersion.Value.Value;
                IReadOnlyList<ObservedVisibleEventBatch> observedBatches = _observer!.Batches;
                for (int b = 0; b < observedBatches.Count; b++)
                {
                    ObservedVisibleEventBatch observedBatch = observedBatches[b];
                    for (int i = 0; i < observedBatch.Events.Count; i++)
                    {
                        ObservedVisibleEvent observedEvent = observedBatch.Events[i];
                        if (LocalFullMatchPolicy.IsOwnActionEvent(
                            plan,
                            observedBatch,
                            observedEvent,
                            appliedVersion))
                        {
                            return Confirm(plan, visibleEvent: true);
                        }
                    }
                }
            }

            if (_observer.HasAppliedReceipt && !plan.RequiresVisibleEvent)
            {
                // The only legal state-only action is a response Pass.
                return Confirm(plan, visibleEvent: false);
            }

            ObservePending(plan);

            if (nowMs < _confirmDeadline)
            {
                return FullMatchExecutionOutcome.Waiting("confirmation in flight");
            }

            TableReceiptResolvedEventArgs? receipt = _matchingReceipt;
            if (receipt != null)
            {
                Notice(new FullMatchExecutionNotice(
                    FullMatchExecutionNoticeKind.ReceiptObserved,
                    plan,
                    false,
                    receipt.Resolution.ReceiptStatus,
                    receipt.Resolution.ReasonCode,
                    receipt.Resolution.EvaluatedStateVersion.Value,
                    _transport.View.Current.PlayerSnapshot?.StateVersion ?? 0UL,
                    string.Empty));
            }

            bool terminalNoOp = view.Board.Phase == PlayerGamePhase.RoundEnd ||
                                view.Board.WallRemaining == 0U;
            if (receipt != null &&
                receipt.Resolution.ReceiptStatus == ReceiptStatus.Rejected &&
                (IsReplanableRejection(receipt.Resolution.ReasonCode) || terminalNoOp))
            {
                // A rejected request is a definitive no-op. The server decision
                // that produced it cannot be submitted again under the same id;
                // wait for the next confirmed decision/state. A rejection observed
                // after the round terminal/empty wall is the same no-op: the
                // authoritative settlement is the only remaining fact.
                _ledger.MarkResolved(plan);
                RecordCompleted(plan);
                Notice(new FullMatchExecutionNotice(
                    FullMatchExecutionNoticeKind.ActionReplanned,
                    plan,
                    false,
                    receipt.Resolution.ReceiptStatus,
                    receipt.Resolution.ReasonCode,
                    receipt.Resolution.EvaluatedStateVersion.Value,
                    _transport.View.Current.PlayerSnapshot?.StateVersion ?? 0UL,
                    string.Empty));
                CleanupActive();
                return FullMatchExecutionOutcome.Replanned(plan, receipt.Resolution.ReasonCode);
            }

            if (receipt != null && receipt.Resolution.ReceiptStatus != ReceiptStatus.Applied)
            {
                _ledger.MarkResolved(plan);
                return FailActive(
                    "ACTION_REJECTED",
                    "Request '" + plan.RequestId + "' action=" + plan.Action +
                    " was rejected: status=" + receipt.Resolution.ReceiptStatus +
                    " reason=" + receipt.Resolution.ReasonCode + ".",
                    FullMatchExecutionFailureStage.Confirmation);
            }

            if (_observer.HasAppliedReceipt)
            {
                _ledger.MarkUnresolved(plan);
                return FailActive(
                    "VISIBLE_EVENT_MISSING",
                    "Applied request '" + plan.RequestId + "' action=" + plan.Action +
                    " has no matching visible event; " + _observer.DescribeAction() +
                    " " + _observer.DescribeBatches(),
                    FullMatchExecutionFailureStage.Confirmation);
            }

            _ledger.MarkUnresolved(plan);
            return FailActive(
                "RECEIPT_UNRESOLVED",
                "No definitive Applied/rejected receipt arrived for request '" +
                plan.RequestId + "' action=" + plan.Action +
                " state=" + _transport.View.Current.State +
                " lastError=" + (_transport.View.Current.LastError ?? "<none>") +
                " pending=" + _transport.View.Current.PendingCommands.Count +
                "; " + _observer.DescribeAction(),
                FullMatchExecutionFailureStage.Confirmation);
        }

        private void ObserveSubmission(FullMatchActionPlan plan)
        {
            if (_submitObserved)
            {
                return;
            }

            Task<CommandSubmission> submit = _submit!;
            if (!submit.IsCompleted)
            {
                return;
            }

            _submitObserved = true;
            if (submit.IsFaulted || submit.IsCanceled)
            {
                Diag(
                    "command_submission",
                    "\"requestId\":\"" + plan.RequestId + "\"" +
                    ",\"scopeId\":\"" + plan.ScopeId + "\"" +
                    ",\"completed\":true" +
                    ",\"sent\":false" +
                    ",\"failure\":\"" + DescribeTaskFailure(submit) + "\"");
                return;
            }

            CommandSubmission submission = submit.Result;
            PendingCommandSnapshot pending = submission.Command;
            Diag(
                "command_submission",
                "\"requestId\":\"" + plan.RequestId + "\"" +
                ",\"scopeId\":\"" + plan.ScopeId + "\"" +
                ",\"kind\":\"" + plan.Command.Kind + "\"" +
                ",\"roundId\":\"" + plan.RoundId + "\"" +
                ",\"expectedStateVersion\":" + plan.Command.ExpectedStateVersion.Value.ToString(CultureInfo.InvariantCulture) +
                ",\"payloadBytes\":" + plan.Command.Payload.Length.ToString(CultureInfo.InvariantCulture) +
                ",\"payloadDigest\":\"" + plan.Command.CanonicalDigest.ToString() + "\"" +
                ",\"sent\":" + Bool(submission.Sent) +
                ",\"wasDuplicate\":" + Bool(submission.WasDuplicate) +
                ",\"handedToTransport\":" + Bool(pending.HandedToTransport) +
                ",\"deliveryUnknown\":" + Bool(pending.DeliveryUnknown) +
                ",\"needsReconciliation\":" + Bool(pending.NeedsReconciliation) +
                ",\"lastLookup\":" + (pending.LastLookup.HasValue ? "\"" + pending.LastLookup.Value + "\"" : "null") +
                ",\"lastReceipt\":" + (pending.LastReceipt.HasValue ? "\"" + pending.LastReceipt.Value + "\"" : "null") +
                ",\"queryInFlight\":" + Bool(pending.QueryInFlight));
        }

        private void ObservePending(FullMatchActionPlan plan)
        {
            SessionViewState state = _transport.View.Current;
            PendingCommandSnapshot? pending = null;
            for (int i = 0; i < state.PendingCommands.Count; i++)
            {
                PendingCommandSnapshot candidate = state.PendingCommands[i];
                if (string.Equals(candidate.RequestId, plan.RequestId, StringComparison.Ordinal) &&
                    string.Equals(candidate.ScopeId, plan.ScopeId, StringComparison.Ordinal))
                {
                    pending = candidate;
                    break;
                }
            }

            string key = pending == null
                ? "absent"
                : pending.NeedsReconciliation + "|" + pending.LastReceipt + "|" +
                  pending.QueryInFlight + "|" + pending.LastLookup + "|" + pending.DeliveryUnknown;
            if (string.Equals(key, _lastPendingDiagnostic, StringComparison.Ordinal))
            {
                return;
            }

            _lastPendingDiagnostic = key;
            Diag(
                "client_pending",
                "\"requestId\":\"" + plan.RequestId + "\"" +
                ",\"present\":" + Bool(pending != null) +
                ",\"pendingCount\":" + state.PendingCommands.Count.ToString(CultureInfo.InvariantCulture) +
                ",\"state\":\"" + state.State + "\"" +
                ",\"lastError\":\"" + (state.LastError ?? string.Empty) + "\"" +
                (pending == null
                    ? string.Empty
                    : ",\"needsReconciliation\":" + Bool(pending.NeedsReconciliation) +
                      ",\"deliveryUnknown\":" + Bool(pending.DeliveryUnknown) +
                      ",\"queryInFlight\":" + Bool(pending.QueryInFlight) +
                      ",\"lastReceipt\":" + (pending.LastReceipt.HasValue ? "\"" + pending.LastReceipt.Value + "\"" : "null") +
                      ",\"lastLookup\":" + (pending.LastLookup.HasValue ? "\"" + pending.LastLookup.Value + "\"" : "null")));
        }

        private void Diag(string kind, string fields)
        {
            if (_onDiagnostic != null)
            {
                _onDiagnostic(kind, fields);
            }
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private FullMatchExecutionOutcome Confirm(FullMatchActionPlan plan, bool visibleEvent)
        {
            _ledger.MarkResolved(plan);
            RecordCompleted(plan);
            Notice(new FullMatchExecutionNotice(
                FullMatchExecutionNoticeKind.ActionConfirmed,
                plan,
                visibleEvent,
                null,
                string.Empty,
                0UL,
                _transport.View.Current.PlayerSnapshot?.StateVersion ?? 0UL,
                string.Empty));
            CleanupActive();
            return FullMatchExecutionOutcome.Confirmed(plan, visibleEvent);
        }

        private FullMatchExecutionOutcome FailActive(
            string code,
            string detail,
            FullMatchExecutionFailureStage stage)
        {
            FullMatchActionPlan plan = _activePlan!;
            TableReceiptResolvedEventArgs? receipt = _matchingReceipt;
            FullMatchExecutionOutcome outcome = FullMatchExecutionOutcome.Failed(
                plan,
                code,
                detail,
                receipt?.Resolution.ReceiptStatus,
                receipt?.Resolution.ReasonCode ?? string.Empty,
                stage);
            CleanupActive();
            return Halt(outcome);
        }

        /// <summary>
        /// An unresolved or definitive failure halts this boundary. The caller
        /// terminates; a later poll can never resend the same bytes under the same
        /// id or mint a replacement request.
        /// </summary>
        private FullMatchExecutionOutcome Halt(FullMatchExecutionOutcome outcome)
        {
            _terminalOutcome = outcome;
            return outcome;
        }

        private void CleanupActive()
        {
            if (_receiptHandler != null)
            {
                _transport.ReceiptResolved -= _receiptHandler;
                _receiptHandler = null;
            }

            if (_receiptDiagnosticHandler != null)
            {
                _transport.ReceiptResolved -= _receiptDiagnosticHandler;
                _receiptDiagnosticHandler = null;
            }

            if (_observer != null)
            {
                _transport.ReceiptResolved -= _observer.OnReceiptResolved;
                _observer.Dispose();
                _observer = null;
            }

            _activePlan = null;
            _matchingReceipt = null;
            _submit = null;
            _submitObserved = false;
            _lastPendingDiagnostic = string.Empty;
        }

        private void RecordCompleted(FullMatchActionPlan plan)
        {
            if (plan.DecisionId.Length > 0)
            {
                _lastCompletedDecisionKey = plan.RoundId + "|" + plan.DecisionId;
            }
            else
            {
                _lastCompletedDrawKey = plan.RoundId + "|" +
                                        plan.BaselineStateVersion.ToString(CultureInfo.InvariantCulture);
            }
        }

        private FullMatchExecutionOutcome EvaluateDecisionWait(
            PlayerTableViewState view,
            long nowMs)
        {
            if (!IsOwnDecisionExpected(view))
            {
                _decisionWaitStartedAt = -1L;
                return FullMatchExecutionOutcome.Waiting("no own decision");
            }

            if (_decisionWaitStartedAt < 0L)
            {
                _decisionWaitStartedAt = nowMs;
                return FullMatchExecutionOutcome.Waiting("waiting for own decision");
            }

            long elapsed = nowMs - _decisionWaitStartedAt;
            if (elapsed >= _budget.DecisionWaitTimeoutMs)
            {
                return Halt(FullMatchExecutionOutcome.Failed(
                    null,
                    "DECISION_WAIT_EXPIRED",
                    "This seat's DISCARD phase produced no server decision within the production bounded wait of " +
                    _budget.DecisionWaitTimeoutMs + "ms (round '" + view.Identity.RoundId +
                    "' stateVersion=" + view.Identity.StateVersion + ").",
                    null,
                    string.Empty,
                    FullMatchExecutionFailureStage.Planning));
            }

            return FullMatchExecutionOutcome.Waiting("waiting for own decision");
        }

        private static bool IsOwnDecisionExpected(PlayerTableViewState view)
        {
            return view.ConnectionState == TableConnectionState.Ready &&
                   view.Identity.IsPrivateAuthorized &&
                   view.Own != null &&
                   view.Own.Decision == null &&
                   view.Own.Control != null &&
                   !view.Own.Control.ControlledByComputer &&
                   view.Board.Phase == PlayerGamePhase.Discard &&
                   view.Board.CurrentSeat == view.Identity.Seat;
        }

        private FullMatchStep PlanStep(
            SessionViewState session,
            PlayerTableViewState view,
            TableCommandIntentFactory factory)
        {
            if (view.ConnectionState != TableConnectionState.Ready)
            {
                return FullMatchStep.Wait("session not Ready");
            }

            if (view.Board.Phase == PlayerGamePhase.RoundEnd)
            {
                return FullMatchStep.CompleteRound("confirmed ROUND_END");
            }

            if (view.Own?.Control?.ControlledByComputer == true)
            {
                return FullMatchStep.Wait("seat is under authoritative computer control");
            }

            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision != null)
            {
                string decisionKey = view.Identity.RoundId + "|" + decision.DecisionId;
                if (string.Equals(decisionKey, _lastCompletedDecisionKey, StringComparison.Ordinal))
                {
                    return FullMatchStep.Wait("the same server decision was already committed");
                }

                if (decision.Countdown.IsExpired(factory.NowMonotonicMilliseconds))
                {
                    return FullMatchStep.Failed(
                        "DEADLINE_EXPIRED",
                        "The server decision deadline expired before a legal action was submitted.");
                }

                if (LocalFullMatchPolicy.TryCreateActionPlan(
                        session,
                        view,
                        factory,
                        factory.NowMonotonicMilliseconds,
                        out FullMatchActionPlan? plan,
                        out string code,
                        out string detail))
                {
                    return FullMatchStep.SubmitAction(plan!);
                }

                return FullMatchStep.Failed(code, detail);
            }

            if ((view.Board.Phase == PlayerGamePhase.Draw || view.Board.Phase == PlayerGamePhase.KanDraw) &&
                view.Board.CurrentSeat == view.Identity.Seat)
            {
                string drawKey = view.Identity.RoundId + "|" +
                                 view.Identity.StateVersion.ToString(CultureInfo.InvariantCulture);
                if (string.Equals(drawKey, _lastCompletedDrawKey, StringComparison.Ordinal))
                {
                    return FullMatchStep.Wait("the same mandatory draw was already committed");
                }

                if (LocalFullMatchPolicy.TryCreateMandatoryDrawPlan(
                        session,
                        view,
                        factory.NowMonotonicMilliseconds,
                        out FullMatchActionPlan? draw,
                        out string drawCode,
                        out string drawDetail))
                {
                    return FullMatchStep.SubmitMandatoryDraw(draw!);
                }

                return FullMatchStep.Failed(drawCode, drawDetail);
            }

            return FullMatchStep.Wait("no own decision");
        }

        private void Notice(FullMatchExecutionNotice notice)
        {
            if (_onNotice != null)
            {
                _onNotice(notice);
            }
        }

        private static long Add(long nowMs, long timeoutMs)
        {
            return nowMs > long.MaxValue - timeoutMs ? long.MaxValue : nowMs + timeoutMs;
        }

        private static string DescribeTaskFailure(Task task)
        {
            if (task.IsCanceled)
            {
                return "task cancelled";
            }

            if (task.IsFaulted && task.Exception != null)
            {
                return task.Exception.GetBaseException().GetType().Name + ": " +
                       task.Exception.GetBaseException().Message;
            }

            return task.IsCompleted ? "task incomplete" : "task did not complete";
        }

        private static bool IsReplanableRejection(string reasonCode)
        {
            return string.Equals(reasonCode, "STALE_STATE_VERSION", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "STALE_DECISION", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "DECISION_MISMATCH", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "ROUND_MISMATCH", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "DEADLINE_EXPIRED", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "NOT_YOUR_TURN", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "SEAT_NOT_ELIGIBLE", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "ROUND_FINISHED", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "DECISION_ALREADY_OPEN", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "ACTION_NOT_AVAILABLE", StringComparison.Ordinal) ||
                   string.Equals(reasonCode, "ILLEGAL_ACTION", StringComparison.Ordinal);
        }

        private static string CandidateActions(PlayerTableViewState view)
        {
            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(decision.Candidates[i].Action).Append('"');
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Outcome of the production terminal/commit gate. A terminal snapshot may be
    /// captured and logged, but the caller may commit/close/open the next round only
    /// when this gate says the own in-flight request is resolved (or there is none).
    /// </summary>
    public enum FullMatchTerminalGateStatus
    {
        NoActiveRequest = 0,
        Waiting = 1,
        Confirmed = 2,
        Replanned = 3,
        Failed = 4
    }

    public sealed class FullMatchTerminalGateResult
    {
        internal FullMatchTerminalGateResult(
            FullMatchTerminalGateStatus status,
            FullMatchExecutionOutcome? outcome)
        {
            Status = status;
            Outcome = outcome;
        }

        public FullMatchTerminalGateStatus Status { get; }

        public FullMatchExecutionOutcome? Outcome { get; }

        public bool MayCommit =>
            Status == FullMatchTerminalGateStatus.NoActiveRequest ||
            Status == FullMatchTerminalGateStatus.Confirmed ||
            Status == FullMatchTerminalGateStatus.Replanned;

        public bool MustFail => Status == FullMatchTerminalGateStatus.Failed;

        public string Code => Outcome?.Code ?? string.Empty;

        public string Detail => Outcome?.Detail ?? string.Empty;
    }

    /// <summary>
    /// Production terminal/commit fence. It never treats the terminal phase itself
    /// as confirmation: when this round still has an own in-flight request, the
    /// same boundary must confirm it (Applied receipt + same-round event), resolve
    /// it as a definitive no-op, or fail closed with the original request id still
    /// unresolved. A null boundary or an already-resolved one has no active request
    /// and may commit the authoritative terminal normally.
    /// </summary>
    public static class FullMatchTerminalCommitGate
    {
        public static FullMatchTerminalGateResult Poll(
            FullMatchExecutionBoundary? boundary,
            SessionViewState session,
            PlayerTableViewState view,
            TableCommandIntentFactory factory,
            long nowMs)
        {
            if (boundary == null)
            {
                return new FullMatchTerminalGateResult(
                    FullMatchTerminalGateStatus.NoActiveRequest,
                    null);
            }

            if (session == null) throw new ArgumentNullException(nameof(session));
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            if (!boundary.HasActiveRequest)
            {
                return new FullMatchTerminalGateResult(
                    FullMatchTerminalGateStatus.NoActiveRequest,
                    null);
            }

            FullMatchExecutionOutcome outcome = boundary.Poll(session, view, factory, nowMs);
            switch (outcome.Status)
            {
                case FullMatchExecutionStatus.Confirmed:
                    return new FullMatchTerminalGateResult(
                        FullMatchTerminalGateStatus.Confirmed,
                        outcome);
                case FullMatchExecutionStatus.Replanned:
                    return new FullMatchTerminalGateResult(
                        FullMatchTerminalGateStatus.Replanned,
                        outcome);
                case FullMatchExecutionStatus.Failed:
                    return new FullMatchTerminalGateResult(
                        FullMatchTerminalGateStatus.Failed,
                        outcome);
                default:
                    return new FullMatchTerminalGateResult(
                        FullMatchTerminalGateStatus.Waiting,
                        outcome);
            }
        }
    }
}
