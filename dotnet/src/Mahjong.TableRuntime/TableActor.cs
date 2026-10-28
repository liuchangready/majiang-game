using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.Domain.Rules;

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
        private readonly TableDurableContext? _durable;
        private readonly TableRecoveryService? _recoveryService;
        private readonly ITableSettlementFactsPort? _settlementFactsPort;
        private readonly DecisionClockConfig _decisionClockConfig;
        private ControlRegistry _control;
        private DecisionClock _decisionClock;
        private readonly Dictionary<int, PendingDecision> _pending = new Dictionary<int, PendingDecision>();
        private readonly ConditionalWeakTable<AiObservation, ObservationGeneration> _aiObservationGenerations =
            new ConditionalWeakTable<AiObservation, ObservationGeneration>();
        private readonly List<TableEventRecord> _recentEvents = new List<TableEventRecord>();

        /// <summary>
        /// Complete committed event log for the current round. It is deliberately independent of
        /// <c>SnapshotEventWindow</c>: the snapshot window is diagnostic and may be 0 or 1, while
        /// Wire facts, typed settlement payloads and provenance need every event of the current
        /// committed turn/round. It is cleared only when an authoritative recovery baseline
        /// replaces the round; recovered history is never fabricated.
        /// </summary>
        private readonly List<TableEventRecord> _committedEventLog = new List<TableEventRecord>();
        private readonly List<TableCommandBinding> _commandBindings = new List<TableCommandBinding>();
        private readonly ITableProjectionContextSource? _projectionContextSource;
        private readonly TableStreamOptions _streamOptions;
        private readonly Dictionary<ulong, TableStreamRegistration> _streams =
            new Dictionary<ulong, TableStreamRegistration>();
        private readonly Dictionary<string, TableStreamIdentityState> _streamIdentities =
            new Dictionary<string, TableStreamIdentityState>(StringComparer.Ordinal);
        private readonly object _streamSource = new object();
        private ulong _nextStreamHandle;

        private RoundStateMachine _round;
        private CheatState? _cheat;
        private readonly CheatEconomyConfig? _cheatEconomy;
        private readonly SleeveBloomConfig? _sleeveConfig;

        private readonly Dictionary<string, CheatRequestBinding> _cheatRequestBindings =
            new Dictionary<string, CheatRequestBinding>(StringComparer.Ordinal);
        private StateVersion _stateVersion = new StateVersion(0);
        private EventSeq _lastEventSeq = new EventSeq(0);
        private OwnerEpoch _ownerEpoch;
        private long _controlVersion;
        private long _timingVersion;
        private long _recoveryGeneration;
        private long _lastMonotonicMillis;
        private bool _strictRecoveryFence;
        private TableActorLifecycle _lifecycle = TableActorLifecycle.Serving;
        private string _lifecycleReason = string.Empty;
        private string _publicationFault = string.Empty;
        private Mahjong.Domain.Rules.RoundSettlementCalculation? _lastSettlementCalculation;
        private TaskCompletionSource<bool>? _closeCompletion;
        private int _pumpRunning;

        public TableActor(
            TableActorOptions options,
            RoundStateMachine initialRound,
            ITableCommitPort commitPort,
            ITableClock clock,
            ITablePublicationSink? publicationSink = null)
            : this(options, initialRound, commitPort, clock, publicationSink, null, null, null)
        {
        }

        public TableActor(
            TableActorOptions options,
            RoundStateMachine initialRound,
            ITableCommitPort commitPort,
            ITableClock clock,
            ITablePublicationSink? publicationSink,
            ITableControlCommitPort? controlCommitPort)
            : this(options, initialRound, commitPort, clock, publicationSink, controlCommitPort, null, null)
        {
        }

        public TableActor(
            TableActorOptions options,
            RoundStateMachine initialRound,
            ITableCommitPort commitPort,
            ITableClock clock,
            ITablePublicationSink? publicationSink,
            ITableControlCommitPort? controlCommitPort,
            ITableProjectionContextSource? projectionContextSource,
            TableStreamOptions? streamOptions,
            TableDurableContext? durableContext = null)
            : this(
                options,
                initialRound,
                commitPort,
                clock,
                publicationSink,
                controlCommitPort,
                projectionContextSource,
                streamOptions,
                durableContext,
                null)
        {
        }

        /// <summary>
        /// Adds the N-2 host settlement fact boundary to the normal actor dependencies. The port
        /// is a composition-root dependency; when it is present the actor is the only settlement
        /// authority and raw settlement-bearing envelopes are refused. Real P4.1/P5.1 hosts are
        /// later-stage work; this constructor is the testable G1 boundary.
        /// </summary>
        public TableActor(
            TableActorOptions options,
            RoundStateMachine initialRound,
            ITableCommitPort commitPort,
            ITableClock clock,
            ITablePublicationSink? publicationSink,
            ITableControlCommitPort? controlCommitPort,
            ITableProjectionContextSource? projectionContextSource,
            TableStreamOptions? streamOptions,
            TableDurableContext? durableContext,
            ITableSettlementFactsPort? settlementFactsPort)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _round = initialRound ?? throw new ArgumentNullException(nameof(initialRound));
            _cheatEconomy = options.CheatEconomy;
            _sleeveConfig = options.SleeveBloomConfig;
            if (_cheatEconomy != null)
            {
                _cheat = options.InitialCheatState ?? CheatState.Initialize(_cheatEconomy);
                TileMarkTransition registration = _cheat.TileMarks.RegisterDealtRound(_round.RoundId);
                if (!registration.Success)
                {
                    throw new ArgumentException(
                        "The initial cheat state cannot register the initial round: " + registration.Error + ".",
                        nameof(options));
                }

                _cheat = _cheat.WithTileMarks(registration.State);
            }
            _commitPort = commitPort ?? throw new ArgumentNullException(nameof(commitPort));
            _clockPort = clock ?? throw new ArgumentNullException(nameof(clock));
            _publicationSink = publicationSink;
            _controlCommitPort = controlCommitPort;
            _projectionContextSource = projectionContextSource;
            _streamOptions = streamOptions ?? new TableStreamOptions();
            _streamOptions.Validate();
            _durable = durableContext;
            _settlementFactsPort = settlementFactsPort;
            _recoveryService = durableContext == null
                ? null
                : new TableRecoveryService(
                    durableContext.Store,
                    durableContext.Lease,
                    durableContext.Outbox,
                    clock);
            _ownerEpoch = options.InitialOwnerEpoch;
            _timingVersion = options.InitialTimingVersion;
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
        /// Current recovery generation for host composition (stream subscription/command
        /// admission). It is read-only and never authorizes a mutation by itself.
        /// </summary>
        public long RecoveryGeneration => Volatile.Read(ref _recoveryGeneration);

        /// <summary>Host-only read of the committed round result (immutable).</summary>
        public Mahjong.Domain.RoundResult? GetRoundResult()
        {
            return _round.GetResult();
        }

        /// <summary>
        /// Host-only read of one seat's physical hand for local AI candidate construction. The
        /// returned tile map is immutable; the AI still reasons outside the actor and its result
        /// is re-validated through the normal decision path.
        /// </summary>
        /// <summary>Host-only immutable round state for trusted settlement/private facts.</summary>
        public Mahjong.Domain.RoundStateMachine GetRoundStateForHost()
        {
            return _round;
        }


        public IReadOnlyList<TileId> GetSeatHandTileIds(int seat)
        {
            CheckSeat(seat);
            return _round.Tiles.TilesInZone(TileZone.Hand, seat);
        }

        /// <summary>Host-only current pending decision id for one seat, or null.</summary>
        public string? GetPendingDecisionId(int seat)
        {
            CheckSeat(seat);
            return _pending.TryGetValue(seat, out PendingDecision? pending) ? pending.Key.DecisionId : null;
        }

        /// <summary>Host-only current legal candidates for one pending decision.</summary>
        public IReadOnlyList<LegalActionOption> GetPendingDecisionCandidates(int seat)
        {
            CheckSeat(seat);
            return _pending.TryGetValue(seat, out PendingDecision? pending)
                ? pending.Candidates
                : Array.Empty<LegalActionOption>();
        }

        public bool HasActiveCheatOffer(int seat)
        {
            CheckSeat(seat);
            return _cheat != null &&
                   (_cheat.SleeveBloom.ImmediateOffer?.Seat == seat ||
                    _cheat.BeamSwap.Offer?.Seat == seat);
        }

        /// <summary>Host-only last accepted N-2 calculation; immutable authority evidence.</summary>
        public Mahjong.Domain.Rules.RoundSettlementCalculation? LastSettlementCalculation =>
            _lastSettlementCalculation;

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
        /// Admits one typed cheat command to the same serial business mailbox as normal table
        /// commands. It is never a side channel: the actor rebuilds the turn context from its
        /// authoritative round/cheat state and applies the same scope/receipt/owner/control/
        /// decision/version/recovery guards before any P1.4 transition runs.
        /// </summary>
        public ValueTask<TableOperationResult> SubmitCheatAsync(
            TableCheatCommandEnvelope command,
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
        /// Admits one typed settlement intent to the same serial business mailbox. The actor
        /// reads the then-current round, derives every provable fact, asks the configured host
        /// fact port only for the model-missing facts, calls the frozen
        /// <c>RoundSettlementCalculator</c>, re-verifies the exact source, and only then uses the
        /// normal Prepare/Install + durable receipt/event projection commit path. The request
        /// carries no settlement scores, han, fu or payments.
        ///
        /// This is the only settlement entry when the N-2 authority is configured; a raw
        /// settlement-bearing <see cref="SubmitAsync"/> envelope is refused in that mode.
        /// </summary>
        public ValueTask<TableOperationResult> SubmitSettlementAsync(
            TableSettlementRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new SettlementMailboxMessage(request);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueue(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableOperationResult>(AdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableOperationResult>(message.Completion.Task);
        }

        /// <summary>
        /// Receipt query by an authenticated account. A query for another account's scope
        /// returns NOT_FOUND and never leaks the receipt. It is safe to call while recovering or
        /// faulted because it only reads the durable store and never mutates Domain state.
        /// </summary>
        public async ValueTask<ReceiptLookupResult> QueryReceiptAsync(
            string accountId,
            RequestScopeId scopeId,
            RequestId requestId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(accountId) || scopeId.IsEmpty || requestId.IsEmpty)
            {
                return ReceiptLookupResult.NotFound();
            }

            if (_durable == null)
            {
                return ReceiptLookupResult.NotFound();
            }

            TableScopeLookup scope = await _durable.Scopes.LookupAsync(scopeId, cancellationToken).ConfigureAwait(false);
            if (!scope.Found ||
                scope.Scope == null ||
                !string.Equals(scope.Scope.AccountId, accountId, StringComparison.Ordinal))
            {
                return ReceiptLookupResult.NotFound();
            }

            TableDurableReceipt? receipt = await _durable.Receipts
                .TryGetAsync(accountId, scopeId, requestId, cancellationToken)
                .ConfigureAwait(false);
            return receipt == null
                ? ReceiptLookupResult.NotFound()
                : ReceiptLookupResult.Found(receipt.Receipt.AsReplay());
        }

        /// <summary>
        /// Explicit cold recovery from the durable checkpoint/event/command journal. It runs on
        /// the actor consumer, enters RECOVERING first, refuses player/AI/tick work until it
        /// finishes, and leaves the actor non-Serving on any failure.
        /// </summary>
        public ValueTask<TableRecoveryResult> RecoverFromStoreAsync(CancellationToken cancellationToken = default)
        {
            if (_durable == null)
            {
                return new ValueTask<TableRecoveryResult>(
                    TableRecoveryResult.Failed(
                        TableRuntimeReasonCode.DurableNotConfigured,
                        "No durable context is wired into this actor."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new DurableRecoveryMailboxMessage();
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableRecoveryResult>(
                    TableRecoveryResult.Failed(TableRuntimeReasonCode.MailboxFull, "Recovery could not enter the mailbox."));
            }

            EnsurePump();
            return new ValueTask<TableRecoveryResult>(message.Completion.Task);
        }

        /// <summary>
        /// Renews the current lease on the actor consumer. A stale/expired renewal immediately
        /// enters LEASE_LOST and stops authoritative pushes; it is never silently retried.
        /// </summary>
        public ValueTask<TableLeaseRenewResult> RenewLeaseAsync(CancellationToken cancellationToken = default)
        {
            if (_durable == null)
            {
                return new ValueTask<TableLeaseRenewResult>(
                    TableLeaseRenewResult.Failed("No durable context is wired into this actor."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new LeaseRenewMailboxMessage();
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableLeaseRenewResult>(
                    TableLeaseRenewResult.Failed("Lease renewal could not enter the mailbox."));
            }

            EnsurePump();
            return new ValueTask<TableLeaseRenewResult>(message.Completion.Task);
        }

        /// <summary>
        /// Writes a checkpoint of the current committed state. P5.2 may call this on a round
        /// boundary or event-count threshold; terminal commits include their checkpoint atomically.
        /// </summary>
        public ValueTask<TableOperationResult> SaveCheckpointAsync(CancellationToken cancellationToken = default)
        {
            if (_durable == null)
            {
                return new ValueTask<TableOperationResult>(
                    TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.DurableNotConfigured,
                        "No durable context is wired into this actor."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new CheckpointMailboxMessage();
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
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
        /// Registers one trusted stream and queues its first full baseline in the same consumer
        /// turn. No public snapshot is read outside the actor and no subscription becomes
        /// visible after the baseline, so a commit can never land between "read state" and
        /// "register".
        /// </summary>
        public ValueTask<TableStreamControlResult> SubscribeStreamAsync(
            TableStreamSubscribeRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new SubscribeStreamMailboxMessage(request);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableStreamControlResult>(StreamAdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableStreamControlResult>(message.Completion.Task);
        }

        /// <summary>
        /// Removes exactly the current registration for a handle. A stale handle is rejected and
        /// can neither stop a newer binding nor expose the new stream's private content.
        /// </summary>
        public ValueTask<TableStreamControlResult> UnsubscribeStreamAsync(
            TableStreamHandle handle,
            CancellationToken cancellationToken = default)
        {
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new UnsubscribeStreamMailboxMessage(handle);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableStreamControlResult>(StreamAdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableStreamControlResult>(message.Completion.Task);
        }

        /// <summary>
        /// Reconciles a stream cursor. A gap/future cursor, a stale round or a recovery-generation
        /// change gets a fresh full baseline; a stale binding/sync identity or owner is rejected
        /// instead of being silently rebound.
        /// </summary>
        public ValueTask<TableStreamControlResult> SyncStreamAsync(
            TableStreamSyncRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var message = new SyncStreamMailboxMessage(request);
            TableMailboxEnqueueStatus enqueue = _mailbox.TryEnqueueControl(message);
            if (enqueue != TableMailboxEnqueueStatus.Enqueued)
            {
                return new ValueTask<TableStreamControlResult>(StreamAdmissionFailure(enqueue));
            }

            EnsurePump();
            return new ValueTask<TableStreamControlResult>(message.Completion.Task);
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
                            command.CheatCommand != null
                                ? await ProcessCheatCommandAsync(command.CheatCommand).ConfigureAwait(false)
                                : await ProcessCommandAsync(command.Command!).ConfigureAwait(false));
                        break;
                    case SettlementMailboxMessage settlementMessage:
                        settlementMessage.Completion.TrySetResult(
                            await ProcessSettlementCommandAsync(settlementMessage.Request).ConfigureAwait(false));
                        break;
                    case OpenDecisionMailboxMessage open:
                        TableOperationResult openResult = await ProcessOpenDecisionAsync(open.Decision).ConfigureAwait(false);
                        if (openResult.Status == TableOperationStatus.Configured)
                        {
                            // Opening a decision is an authority change even though it is not a
                            // Domain state transition: emit a same-state refresh so already
                            // subscribed players learn the decision id/deadline immediately.
                            PublishDecisionRefreshToStreams();
                        }

                        open.Completion.TrySetResult(openResult);
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
                        complete.Completion.TrySetResult(
                            await ProcessCompleteRecoveryAsync(complete.Baseline).ConfigureAwait(false));
                        break;
                    case LeaseLostMailboxMessage leaseLost:
                        leaseLost.Completion.TrySetResult(
                            ProcessLeaseLost(leaseLost.OwnerEpoch, leaseLost.Reason));
                        break;
                    case SnapshotMailboxMessage snapshot:
                        snapshot.Completion.TrySetResult(BuildSnapshot());
                        break;
                    case SubscribeStreamMailboxMessage subscribe:
                        subscribe.Completion.TrySetResult(ProcessSubscribeStream(subscribe.Request));
                        break;
                    case UnsubscribeStreamMailboxMessage unsubscribe:
                        unsubscribe.Completion.TrySetResult(ProcessUnsubscribeStream(unsubscribe.Handle));
                        break;
                    case SyncStreamMailboxMessage sync:
                        sync.Completion.TrySetResult(ProcessSyncStream(sync.Request));
                        break;
                    case CloseMailboxMessage close:
                        _pending.Clear();
                        _decisionClock.Reset();
                        CloseAllStreams(TableStreamTerminationKind.ActorClosed, ProtocolErrorCode.LocalClosed, "actor closed");
                        _lifecycle = TableActorLifecycle.Closed;
                        _lifecycleReason = "actor closed";
                        close.Completion.TrySetResult(true);
                        break;
                    case DurableRecoveryMailboxMessage recovery:
                        recovery.Completion.TrySetResult(
                            await ProcessDurableRecoveryAsync().ConfigureAwait(false));
                        break;
                    case LeaseRenewMailboxMessage renew:
                        renew.Completion.TrySetResult(
                            await ProcessLeaseRenewAsync().ConfigureAwait(false));
                        break;
                    case CheckpointMailboxMessage checkpoint:
                        checkpoint.Completion.TrySetResult(
                            await ProcessCheckpointAsync().ConfigureAwait(false));
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

        private ValueTask<TableOperationResult> ProcessCommandAsync(TableCommandEnvelope command)
        {
            return ProcessAdmittedCommandAsync(command, () => ProcessCommandCoreAsync(command));
        }

        private ValueTask<TableOperationResult> ProcessSettlementCommandAsync(TableSettlementRequest request)
        {
            return ProcessAdmittedCommandAsync(
                request.Envelope,
                () => ProcessSettlementCoreAsync(request));
        }

        private async ValueTask<TableOperationResult> ProcessAdmittedCommandAsync(
            TableCommandEnvelope command,
            Func<ValueTask<TableOperationResult>> core)
        {
            if (_durable == null)
            {
                return await core().ConfigureAwait(false);
            }

            if (!command.HasDurableIdentity)
            {
                // Not admitted: a command without host-computed scope/API/digest cannot be
                // replayed or fenced, so it must not create a receipt or touch Domain state.
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.PayloadIdentityRequired,
                    "A durable table command requires scopeId, api and payloadSha256.");
            }

            RequestScopeId scopeId = command.ScopeId!.Value;
            TableScopeLookup lookup = await _durable.Scopes
                .LookupAsync(scopeId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!lookup.Found || lookup.Scope == null)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    ProtocolErrorCode.Unauthenticated,
                    "The host-issued request scope does not exist.");
            }

            TableScopeRecord scope = lookup.Scope;

            // Receipt-first: a committed receipt (including REJECTED) is returned exactly even
            // when the scope was revoked/expired or the owner lease was lost afterwards. No
            // Domain evaluation happens here.
            TableDurableReceipt? existing = await _durable.Receipts
                .TryGetAsync(scope.AccountId, scopeId, command.RequestId, CancellationToken.None)
                .ConfigureAwait(false);
            if (existing != null)
            {
                if (existing.Matches(command.Api!, command.PayloadSha256!.Value))
                {
                    if (_lifecycle == TableActorLifecycle.Serving ||
                        _lifecycle == TableActorLifecycle.LeaseLost)
                    {
                        return TableOperationResult.FromReplay(existing);
                    }

                    TableOperationResult? replayGuard = GuardServing();
                    return replayGuard ?? TableOperationResult.FromReplay(existing);
                }

                return TableOperationResult.Conflict(
                    "The same (account, scope, request) key was used with a different API or payload.");
            }

            // New work is never admitted after lease loss or any other non-serving lifecycle.
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return guard;
            }

            TableOperationResult? scopeError = ValidateScope(scope);
            if (scopeError != null)
            {
                // Scope/authentication failures are not admitted commands: no receipt, no event
                // and no state change.
                return scopeError;
            }

            TableOperationResult result = await core().ConfigureAwait(false);
            if (result.Status != TableOperationStatus.Rejected || !ShouldPersistRejection(result.ReasonCode))
            {
                return result;
            }

            return await CommitRejectedReceiptAsync(
                scope,
                command.RequestId,
                command.Api!,
                command.PayloadSha256!.Value,
                result).ConfigureAwait(false);
        }

        private static bool ShouldPersistRejection(string reasonCode)
        {
            // Fence/admission failures are not admitted business rejections: writing a receipt
            // for them would let an old owner/generation create a durable side effect.
            return !string.Equals(reasonCode, TableRuntimeReasonCode.StaleOwnerEpoch, StringComparison.Ordinal) &&
                   !string.Equals(reasonCode, TableRuntimeReasonCode.StaleStateVersion, StringComparison.Ordinal) &&
                   !string.Equals(reasonCode, TableRuntimeReasonCode.StaleRecoveryGeneration, StringComparison.Ordinal) &&
                   !string.Equals(reasonCode, TableRuntimeReasonCode.LeaseLost, StringComparison.Ordinal) &&
                   !string.Equals(reasonCode, TableRuntimeReasonCode.Recovering, StringComparison.Ordinal) &&
                   !string.Equals(reasonCode, TableRuntimeReasonCode.ActorClosed, StringComparison.Ordinal);
        }

        private TableOperationResult? ValidateScope(TableScopeRecord scope)
        {
            if (scope.Role != TableScopeRole.Player)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.ScopeRoleMismatch,
                    "Only a player scope can submit a table command.");
            }

            if (!scope.MatchesRoom(_options.RoomId))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.ScopeRoomMismatch,
                    "The request scope is bound to another room.");
            }

            if (scope.IsRevoked)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    ProtocolErrorCode.RequestScopeExpired,
                    "The request scope was revoked.");
            }

            long utc = _clockPort.UtcNowMs;
            ulong now = utc < 0 ? 0UL : (ulong)utc;
            if (scope.IsExpiredAt(now))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    ProtocolErrorCode.RequestScopeExpired,
                    "The request scope expired.");
            }

            return null;
        }

        private async ValueTask<TableOperationResult> ProcessCommandCoreAsync(TableCommandEnvelope command)
        {
            if (TableSettlementIntent.IsSettlementAction(command.Command.Action) &&
                _settlementFactsPort != null)
            {
                // In N-2 authority mode every Tsumo/Ron/draw must come through the typed intent
                // path. A raw settlement-bearing envelope is not an authenticated production
                // entry: refuse it before reading any caller-supplied score/han/fu/payment.
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableSettlementReasonCode.RawEntryRejected,
                    "Settlement actions require SubmitSettlementAsync when the N-2 authority is configured.");
            }

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

            PendingDecision? pending;
            TableOperationResult? identityError = ValidateAuthorityIdentity(
                command.Command.RoundId,
                command.Command.Seat,
                command.ExpectedStateVersion,
                command.OwnerEpoch,
                command.ControlEpoch,
                command.DecisionId,
                command.RecoveryGeneration,
                now,
                out pending);
            if (identityError != null)
            {
                return identityError;
            }

            if (command.Command.Action != RoundActionKind.DiscardTile &&
                command.Command.Action != RoundActionKind.DrawTile)
            {
                RoundCheatPreparation? settlement = TrySettleOfferForCommand(
                    command.Command,
                    now,
                    openDiscardWindow: false,
                    deliverDeferred: command.Command.Action == RoundActionKind.Pass ||
                                     command.Command.Action == RoundActionKind.DeclareRiichi);
                if (settlement != null)
                {
                    if (!settlement.IsReady)
                    {
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.FromCheat(settlement.Error, settlement.CheatError),
                            settlement.Message);
                    }

                    return await CommitCheatAndInstallAsync(
                        settlement.Change!,
                        command,
                        cheatEnvelope: null,
                        command.Command,
                        cheatCommand: null,
                        systemJournal: null,
                        BuildCheatContext(now),
                        pending,
                        command.DecisionId,
                        now).ConfigureAwait(false);
                }
            }

            if ((command.Command.Action == RoundActionKind.Pass ||
                 command.Command.Action == RoundActionKind.DeclareRiichi) &&
                _cheat != null &&
                _cheatEconomy != null &&
                _sleeveConfig != null)
            {
                Func<int, bool>? defer = CheatDeferDrawPredicate();
                if (defer != null)
                {
                    RoundPreparation deferred = _round.Prepare(
                        command.Command,
                        CheatProtectionPredicate(),
                        defer);
                    if (deferred.IsReady && deferred.Change!.NextState.Phase == RoundPhase.Draw)
                    {
                        RoundCheatPreparation delivery = RoundCheatTransition.PrepareDeferredDrawDelivery(
                            _round,
                            deferred.Change,
                            _cheat,
                            now);
                        if (!delivery.IsReady)
                        {
                            return TableOperationResult.Rejected(
                                _stateVersion,
                                TableRuntimeReasonCode.FromCheat(delivery.Error, delivery.CheatError),
                                delivery.Message);
                        }

                        return await CommitCheatAndInstallAsync(
                            delivery.Change!,
                            command,
                            cheatEnvelope: null,
                            command.Command,
                            cheatCommand: null,
                            systemJournal: null,
                            BuildCheatContext(now),
                            pending,
                            command.DecisionId,
                            now).ConfigureAwait(false);
                    }
                }
            }

            if (command.Command.Action == RoundActionKind.DiscardTile &&
                _cheat != null &&
                _cheatEconomy != null &&
                _sleeveConfig != null &&
                (command.Command.EligibleSeats == null || command.Command.EligibleSeats.Count == 0))
            {
                int nextSeat = (command.Command.Seat + 1) % 4;
                if (_cheat.SleeveBloom.ReservationFor(nextSeat) != null ||
                    _cheat.SleeveBloom.OffTurnOfferFor(nextSeat) != null)
                {
                    CheatTurnContext discardContext = BuildCheatContext(now);
                    RoundCheatPreparation delivery;
                    try
                    {
                        delivery = RoundCheatTransition.PrepareDiscardDelivery(
                            _round,
                            _cheat,
                            command.Command,
                            now);
                    }
                    catch (Exception exception)
                    {
                        return TableOperationResult.Failed(
                            _stateVersion,
                            TableRuntimeReasonCode.InternalError,
                            exception.GetType().Name + ": " + exception.Message);
                    }

                    if (!delivery.IsReady)
                    {
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.FromCheat(delivery.Error, delivery.CheatError),
                            delivery.Message);
                    }

                    return await CommitCheatAndInstallAsync(
                        delivery.Change!,
                        command,
                        cheatEnvelope: null,
                        command.Command,
                        cheatCommand: null,
                        systemJournal: null,
                        discardContext,
                        pending,
                        command.DecisionId,
                        now).ConfigureAwait(false);
                }
            }

            if (command.Command.Action == RoundActionKind.DiscardTile)
            {
                RoundCheatPreparation? settlement = TrySettleOfferForCommand(
                    command.Command,
                    now,
                    openDiscardWindow: true);
                if (settlement != null)
                {
                    if (!settlement.IsReady)
                    {
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.FromCheat(settlement.Error, settlement.CheatError),
                            settlement.Message);
                    }

                    return await CommitCheatAndInstallAsync(
                        settlement.Change!,
                        command,
                        cheatEnvelope: null,
                        command.Command,
                        cheatCommand: null,
                        systemJournal: null,
                        BuildCheatContext(now),
                        pending,
                        command.DecisionId,
                        now).ConfigureAwait(false);
                }
            }

            if (command.Command.Action == RoundActionKind.DrawTile &&
                _cheat != null &&
                _cheatEconomy != null &&
                _sleeveConfig != null)
            {
                CheatTurnContext drawContext = BuildCheatContext(now);
                RoundCheatPreparation delivery;
                try
                {
                    delivery = RoundCheatTransition.PrepareReservationDraw(
                        _round,
                        _cheat,
                        drawContext,
                        command.Command.Seat);
                }
                catch (Exception exception)
                {
                    return TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.InternalError,
                        exception.GetType().Name + ": " + exception.Message);
                }

                if (delivery.IsReady)
                {
                    return await CommitCheatAndInstallAsync(
                        delivery.Change!,
                        command,
                        cheatEnvelope: null,
                        command.Command,
                        cheatCommand: null,
                        systemJournal: null,
                        drawContext,
                        pending,
                        command.DecisionId,
                        now).ConfigureAwait(false);
                }

                RoundCheatPreparation? settlement = TrySettleOfferForCommand(
                    command.Command,
                    now,
                    openDiscardWindow: false);
                if (settlement != null)
                {
                    if (!settlement.IsReady)
                    {
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.FromCheat(settlement.Error, settlement.CheatError),
                            settlement.Message);
                    }

                    return await CommitCheatAndInstallAsync(
                        settlement.Change!,
                        command,
                        cheatEnvelope: null,
                        command.Command,
                        cheatCommand: null,
                        systemJournal: null,
                        drawContext,
                        pending,
                        command.DecisionId,
                        now).ConfigureAwait(false);
                }

                if (HasProtectedWallTiles(_cheat))
                {
                    RoundTransition projected;
                    try
                    {
                        RoundPreparation prepared = _round.PrepareDrawProtected(
                            command.Command,
                            tile => _cheat.SleeveBloom.IsProtectedTile(tile));
                        if (!prepared.IsReady)
                        {
                            return TableOperationResult.Rejected(
                                _stateVersion,
                                TableRuntimeReasonCode.FromDomain(prepared.Error!.Code),
                                prepared.Error.Message);
                        }

                        projected = _round.Install(prepared.Change!);
                    }
                    catch (Exception exception)
                    {
                        return TableOperationResult.Failed(
                            _stateVersion,
                            TableRuntimeReasonCode.InternalError,
                            exception.GetType().Name + ": " + exception.Message);
                    }

                    TableOperationResult? projectedError = ValidateTransition(projected);
                    if (projectedError != null)
                    {
                        return projectedError;
                    }

                    return await CommitAndInstallAsync(
                        projected,
                        command.RequestId,
                        pending,
                        command.DecisionId,
                        now,
                        command,
                        command.Command,
                        TimingConsumption.Complete).ConfigureAwait(false);
                }
            }

            if (command.Command.Action == RoundActionKind.DiscardTile &&
                _cheat != null &&
                (command.Command.EligibleSeats == null || command.Command.EligibleSeats.Count == 0) &&
                HasProtectedWallTiles(_cheat))
            {
                CheatState protectedCheat = _cheat;
                RoundTransition projected;
                try
                {
                    RoundPreparation prepared = _round.PrepareDiscardProtected(
                        command.Command,
                        tile => protectedCheat.SleeveBloom.IsProtectedTile(tile));
                    if (!prepared.IsReady)
                    {
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.FromDomain(prepared.Error!.Code),
                            prepared.Error.Message);
                    }

                    projected = _round.Install(prepared.Change!);
                }
                catch (Exception exception)
                {
                    return TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.InternalError,
                        exception.GetType().Name + ": " + exception.Message);
                }

                TableOperationResult? projectedError = ValidateTransition(projected);
                if (projectedError != null)
                {
                    return projectedError;
                }

                SleeveBloomState opened = protectedCheat.SleeveBloom.WithOpenedWindow(
                    command.Command.Seat,
                    CountDrawTurns(_round),
                    out _);
                return await CommitAndInstallAsync(
                    projected,
                    command.RequestId,
                    pending,
                    command.DecisionId,
                    now,
                    command,
                    command.Command,
                    TimingConsumption.Complete,
                    protectedCheat.WithSleeveBloom(opened)).ConfigureAwait(false);
            }

            RoundTransition transition;
            try
            {
                transition = _round.Execute(command.Command, CheatProtectionPredicate());
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

            CheatState? nextCheat = null;
            if (command.Command.Action == RoundActionKind.DiscardTile && _cheat != null)
            {
                SleeveBloomState opened = _cheat.SleeveBloom.WithOpenedWindow(
                    command.Command.Seat,
                    CountDrawTurns(_round),
                    out _);
                nextCheat = _cheat.WithSleeveBloom(opened);
            }

            return await CommitAndInstallAsync(
                transition,
                command.RequestId,
                pending,
                command.DecisionId,
                now,
                command,
                command.Command,
                TimingConsumption.Complete,
                nextCheat).ConfigureAwait(false);
        }

        /// <summary>
        /// Binds a typed settlement intent to the authoritative seat before any identity or
        /// scoring work. Tsumo/Ron use the winner seat; an exhaustive or abortive draw is a
        /// host-authority action and must still name the current authoritative seat, so it
        /// cannot borrow another seat's control/decision identity.
        /// </summary>
        private TableOperationResult? ValidateSettlementIntentSeat(
            TableSettlementIntent intent,
            out int authoritySeat)
        {
            authoritySeat = intent.Seat;
            if (intent.Action == TableSettlementAction.Tsumo)
            {
                if (intent.Seat != _round.CurrentSeat)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableSettlementReasonCode.FromRejection(RoundSettlementRejectionCode.WinnerNotCurrent),
                        "Tsumo intent seat " + intent.Seat + " is not the current authoritative seat " +
                        _round.CurrentSeat + ".");
                }

                DrawnTileEvidence? drawn = _round.Tiles.DrawnTile;
                if (!drawn.HasValue)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableSettlementReasonCode.FromRejection(RoundSettlementRejectionCode.NoDrawnTile),
                        "Tsumo intent seat " + intent.Seat + " has no authoritative drawn tile.");
                }

                if (drawn.Value.Seat != intent.Seat)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableSettlementReasonCode.FromRejection(RoundSettlementRejectionCode.DrawnTileMismatch),
                        "The authoritative drawn tile belongs to seat " + drawn.Value.Seat +
                        ", not tsumo intent seat " + intent.Seat + ".");
                }

                return null;
            }

            if (intent.Action == TableSettlementAction.Ron)
            {
                return null;
            }

            if (intent.Seat != _round.CurrentSeat)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableSettlementReasonCode.SeatMismatch,
                    "Draw intent seat " + intent.Seat + " is not the current authoritative seat " +
                    _round.CurrentSeat + ".");
            }

            return null;
        }

        private async ValueTask<TableOperationResult> ProcessSettlementCoreAsync(
            TableSettlementRequest request)
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return guard;
            }

            if (_strictRecoveryFence &&
                request.Envelope.RecoveryGeneration != _recoveryGeneration)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleRecoveryGeneration,
                    "Settlement recovery generation " + request.Envelope.RecoveryGeneration +
                    " is not current generation " + _recoveryGeneration + ".");
            }

            if (!TryReadClock(out long now, out long utcMillis, out string clockFailure))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    clockFailure,
                    "The monotonic clock is not safe for settlement authority.");
            }

            if (_settlementFactsPort == null)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableSettlementReasonCode.AuthorityUnavailable,
                    "No N-2 settlement host fact port is configured on this table.");
            }

            PendingDecision? pending;
            TableOperationResult? seatError = ValidateSettlementIntentSeat(
                request.Intent,
                out int authoritySeat);
            if (seatError != null)
            {
                return seatError;
            }

            TableOperationResult? identityError = ValidateAuthorityIdentity(
                request.Intent.RoundId,
                authoritySeat,
                request.Envelope.ExpectedStateVersion,
                request.Envelope.OwnerEpoch,
                request.Envelope.ControlEpoch,
                request.Envelope.DecisionId,
                request.Envelope.RecoveryGeneration,
                now,
                out pending);
            if (identityError != null)
            {
                return identityError;
            }

            var source = new TableSettlementSourceRef(
                _round.RoundId,
                _round.Version,
                _round.SourceFingerprint);
            TableSettlementHostFacts hostFacts;
            try
            {
                hostFacts = _settlementFactsPort.Resolve(source, request.Intent);
            }
            catch (TableSettlementHostRejectedException rejected)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    rejected.ReasonCode,
                    rejected.Message);
            }
            catch (Exception exception)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableSettlementReasonCode.FactsUnavailable,
                    "The settlement fact port failed: " + exception.GetType().Name + ": " + exception.Message);
            }

            string resultId = "settlement:" + _options.RoomId + ":" + _round.RoundId + ":" +
                              _round.Version.ToString(CultureInfo.InvariantCulture) + ":" +
                              request.Intent.Action + ":" +
                              request.Intent.Seat.ToString(CultureInfo.InvariantCulture);

            RoundSettlementFacts facts;
            try
            {
                facts = TableSettlementFactBuilder.Build(
                    _round,
                    request.Intent,
                    hostFacts,
                    resultId,
                    utcMillis,
                    now);
            }
            catch (TableSettlementHostRejectedException rejected)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    rejected.ReasonCode,
                    rejected.Message);
            }
            catch (ArgumentOutOfRangeException invalid)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.ClockInvalid,
                    "The settlement UTC instant is outside the persistable range: " + invalid.Message);
            }
            catch (ArgumentException invalid)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableSettlementReasonCode.HostFactsInvalid,
                    invalid.Message);
            }
            catch (Exception exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.InternalError,
                    "SettlementFacts " + exception.GetType().Name + ": " + exception.Message);
            }

            RoundSettlementCalculation calculation;
            try
            {
                switch (request.Intent.Action)
                {
                    case TableSettlementAction.Tsumo:
                        calculation = RoundSettlementCalculator.CalculateTsumo(_round, facts);
                        break;
                    case TableSettlementAction.Ron:
                        calculation = RoundSettlementCalculator.CalculateRon(_round, request.Intent.Seat, facts);
                        break;
                    case TableSettlementAction.ExhaustiveDraw:
                        calculation = RoundSettlementCalculator.CalculateExhaustiveDraw(_round, facts);
                        break;
                    case TableSettlementAction.AbortiveDraw:
                        calculation = RoundSettlementCalculator.CalculateAbortiveDraw(_round, facts);
                        break;
                    default:
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableSettlementReasonCode.IntentInvalid,
                            "Unknown settlement action " + request.Intent.Action + ".");
                }
            }
            catch (RoundSettlementRejectedException rejected)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableSettlementReasonCode.FromRejection(rejected.Code),
                    rejected.Message);
            }
            catch (ArgumentException invalid)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableSettlementReasonCode.IntentInvalid,
                    invalid.Message);
            }
            catch (Exception exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message);
            }

            try
            {
                calculation.VerifySource(_round);
                _lastSettlementCalculation = calculation;
            }
            catch (RoundSettlementRejectedException rejected)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableSettlementReasonCode.FromRejection(rejected.Code),
                    rejected.Message);
            }

            RoundCheatPreparation? offerSettlement = TrySettleOfferForCommand(
                calculation.Command,
                now,
                openDiscardWindow: false);
            if (offerSettlement != null)
            {
                if (!offerSettlement.IsReady)
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.FromCheat(
                            offerSettlement.Error,
                            offerSettlement.CheatError),
                        offerSettlement.Message);
                }

                // The accepted N-1b offer expiry and the terminal settlement are installed as
                // one RoundCheatChange through the original cheat commit path. The typed fact
                // source was already verified above; the journal stores the real command, never
                // the request placeholder.
                return await CommitCheatAndInstallAsync(
                    offerSettlement.Change!,
                    request.Envelope,
                    cheatEnvelope: null,
                    calculation.Command,
                    cheatCommand: null,
                    systemJournal: null,
                    BuildCheatContext(now),
                    pending,
                    request.Envelope.DecisionId,
                    now,
                    _durable != null ? TimingConsumption.Complete : TimingConsumption.None)
                    .ConfigureAwait(false);
            }

            RoundTransition transition;
            try
            {
                // The original Prepare/Install path is kept: Execute() prepares a candidate from
                // this exact round and installs it only when its source binding still matches.
                transition = _round.Execute(calculation.Command, CheatProtectionPredicate());
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
                request.Envelope.RequestId,
                pending,
                request.Envelope.DecisionId,
                now,
                request.Envelope,
                calculation.Command,
                _durable != null ? TimingConsumption.Complete : TimingConsumption.None)
                .ConfigureAwait(false);
        }

        private async ValueTask<TableOperationResult> ProcessCheatCommandAsync(TableCheatCommandEnvelope command)
        {
            if (!string.Equals(
                    command.CheatCommand.RequestId,
                    command.RequestId.Value,
                    StringComparison.Ordinal))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.CheatRequestIdMismatch,
                    "The inner domain request id must equal the host envelope request id.");
            }

            string requestKey = command.RequestId.Value;
            if (_cheatRequestBindings.TryGetValue(requestKey, out CheatRequestBinding? prior))
            {
                if (!Nullable.Equals(prior.ScopeId, command.ScopeId))
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.CheatRequestIdConflict,
                        "The request id is already bound to another scope.");
                }

                if (!string.Equals(prior.Api, command.Api, StringComparison.Ordinal) ||
                    prior.Payload != command.PayloadSha256)
                {
                    return TableOperationResult.Conflict(
                        "The same request id was used with a different API or payload digest.");
                }

                if (prior.Result != null &&
                    (_durable == null || prior.Result.Status == TableOperationStatus.Rejected))
                {
                    return prior.Result;
                }
            }

            if (prior == null &&
                IsOfferChoice(command.CheatCommand.Action) &&
                ChoiceTargetsForeignBootOffer(command.CheatCommand))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.CheatBootReconciliationRequired,
                    "The targeted cheat offer belongs to a previous boot/clock domain and cannot be selected directly.");
            }

            if (prior == null &&
                _cheatRequestBindings.Count >= _options.CheatRequestBindingCapacity)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.CheatReceiptCapacityExceeded,
                    "The actor cheat receipt binding table is full; refusing a new request rather than evicting an authority receipt.");
            }

            TableOperationResult result = await ProcessCheatCommandInternalAsync(command).ConfigureAwait(false);
            bool persist = result.Status == TableOperationStatus.Accepted ||
                           result.Status == TableOperationStatus.Replayed ||
                           (result.Status == TableOperationStatus.Rejected &&
                            ShouldPersistRejection(result.ReasonCode));
            if (persist &&
                command.ScopeId.HasValue &&
                command.Api != null &&
                command.PayloadSha256.HasValue)
            {
                _cheatRequestBindings[requestKey] = new CheatRequestBinding(
                    command.ScopeId.Value,
                    command.Api,
                    command.PayloadSha256.Value,
                    result);
            }

            return result;
        }

        private async ValueTask<TableOperationResult> ProcessCheatCommandInternalAsync(TableCheatCommandEnvelope command)
        {
            if (_durable == null)
            {
                return await ProcessCheatCommandCoreAsync(command).ConfigureAwait(false);
            }

            if (!command.HasDurableIdentity)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.PayloadIdentityRequired,
                    "A durable cheat command requires scopeId, api and payloadSha256.");
            }

            RequestScopeId scopeId = command.ScopeId!.Value;
            TableScopeLookup lookup = await _durable.Scopes
                .LookupAsync(scopeId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!lookup.Found || lookup.Scope == null)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    ProtocolErrorCode.Unauthenticated,
                    "The host-issued request scope does not exist.");
            }

            TableScopeRecord scope = lookup.Scope;
            TableDurableReceipt? existing = await _durable.Receipts
                .TryGetAsync(scope.AccountId, scopeId, command.RequestId, CancellationToken.None)
                .ConfigureAwait(false);
            if (existing != null)
            {
                if (existing.Matches(command.Api!, command.PayloadSha256!.Value))
                {
                    if (_lifecycle == TableActorLifecycle.Serving ||
                        _lifecycle == TableActorLifecycle.LeaseLost)
                    {
                        return TableOperationResult.FromReplay(existing);
                    }

                    TableOperationResult? replayGuard = GuardServing();
                    return replayGuard ?? TableOperationResult.FromReplay(existing);
                }

                return TableOperationResult.Conflict(
                    "The same (account, scope, request) key was used with a different API or payload.");
            }

            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return guard;
            }

            TableOperationResult? scopeError = ValidateScope(scope);
            if (scopeError != null)
            {
                return scopeError;
            }

            TableOperationResult result = await ProcessCheatCommandCoreAsync(command).ConfigureAwait(false);
            if (result.Status != TableOperationStatus.Rejected || !ShouldPersistRejection(result.ReasonCode))
            {
                return result;
            }

            return await CommitRejectedReceiptAsync(
                scope,
                command.RequestId,
                command.Api!,
                command.PayloadSha256!.Value,
                result).ConfigureAwait(false);
        }

        private async ValueTask<TableOperationResult> ProcessCheatCommandCoreAsync(TableCheatCommandEnvelope envelope)
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return guard;
            }

            if (!TryReadClock(out long now, out _, out string clockFailure))
            {
                return TableOperationResult.Rejected(_stateVersion, clockFailure, "The monotonic clock is not safe for authority.");
            }

            RoundCheatCommand command = envelope.CheatCommand;
            if (!envelope.HasDurableIdentity)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.PayloadIdentityRequired,
                    "A cheat command requires host-issued scopeId, api and the original business payload SHA-256.");
            }

            PendingDecision? pending;
            TableOperationResult? identityError = ValidateAuthorityIdentity(
                command.RoundId,
                command.Seat,
                envelope.ExpectedStateVersion,
                envelope.OwnerEpoch,
                envelope.ControlEpoch,
                envelope.DecisionId,
                envelope.RecoveryGeneration,
                now,
                out pending);
            if (identityError != null)
            {
                return identityError;
            }

            if (_cheat == null || _cheatEconomy == null || _sleeveConfig == null)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.CheatNotConfigured,
                    "No authoritative cheat aggregate/economy/sleeve configuration is wired into this actor.");
            }

            CheatTurnContext context = BuildCheatContext(now);
            RoundCheatPreparation preparation;
            try
            {
                preparation = RoundCheatTransition.Prepare(
                    _round,
                    _cheat,
                    command,
                    context,
                    _cheatEconomy,
                    _sleeveConfig);
            }
            catch (Exception exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message);
            }

            if (!preparation.IsReady)
            {
                if (string.Equals(preparation.Message, "CHEAT_REPLAYED", StringComparison.Ordinal))
                {
                    return TableOperationResult.Rejected(
                        _stateVersion,
                        TableRuntimeReasonCode.CheatReplayed,
                        "The cheat request was already applied; no version, charge or physical move was repeated.");
                }

                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.FromCheat(preparation.Error, preparation.CheatError),
                    preparation.Message);
            }

            RoundCheatChange change = preparation.Change!;
            return await CommitCheatAndInstallAsync(
                change,
                roundEnvelope: null,
                envelope,
                roundCommand: null,
                command,
                systemJournal: null,
                context,
                pending: null,
                decisionId: null,
                now).ConfigureAwait(false);
        }

        private CheatTurnContext BuildCheatContext(long now)
        {
            if (_cheat == null)
            {
                throw new InvalidOperationException("No cheat aggregate is configured.");
            }

            return new CheatTurnContext(
                _round.CurrentSeat,
                _round.Phase,
                _round.Window != null,
                _round.RiichiDeclared,
                CountDrawTurns(_round),
                _round.RoundId,
                _cheat.TileMarks.DealtRoundOrdinal,
                DrawKindOf(_round.Tiles),
                now,
                _durable?.BootId ?? string.Empty);
        }

        private static int CountDrawTurns(RoundStateMachine round)
        {
            int turns = 0;
            for (int i = 0; i < round.Events.Count; i++)
            {
                RoundEventType type = round.Events[i].Type;
                if (type == RoundEventType.TileDrawn || type == RoundEventType.RinshanDrawn)
                {
                    turns++;
                }
            }

            return turns;
        }

        private RoundCheatPreparation? TrySettleOfferForCommand(
            RoundCommand command,
            long now,
            bool openDiscardWindow,
            bool deliverDeferred = false)
        {
            CheatState? cheat = _cheat;
            if (cheat == null || _sleeveConfig == null)
            {
                return null;
            }

            bool immediate = cheat.SleeveBloom.ImmediateOffer?.Seat == command.Seat;
            bool beam = cheat.BeamSwap.Offer?.Seat == command.Seat;
            if (!immediate && !beam)
            {
                return null;
            }

            CheatTurnContext context = BuildCheatContext(now);
            return RoundCheatTransition.PrepareCommandWithOfferSettlement(
                _round,
                cheat,
                command,
                context,
                CheatProtectionPredicate(),
                openDiscardWindow && (command.Action == RoundActionKind.DiscardTile ||
                                      command.Action == RoundActionKind.DeclareRiichi),
                deliverDeferred ? CheatDeferDrawPredicate() : null);
        }

        private bool ChoiceTargetsForeignBootOffer(RoundCheatCommand command)
        {
            CheatState? cheat = _cheat;
            if (cheat == null)
            {
                return false;
            }

            SleeveBloomOffer? offer = command.Action == RoundCheatAction.SubmitSleeveBloomChoice
                ? cheat.SleeveBloom.ImmediateOffer
                : command.Action == RoundCheatAction.SubmitSleeveBloomOffTurnChoice
                    ? cheat.SleeveBloom.OffTurnOfferFor(command.Seat)
                    : null;
            return offer != null &&
                   !string.Equals(
                       offer.BootId,
                       _durable?.BootId ?? string.Empty,
                       StringComparison.Ordinal);
        }

        private static bool IsOfferChoice(RoundCheatAction action) =>
            action == RoundCheatAction.SubmitSleeveBloomOffTurnChoice ||
            action == RoundCheatAction.SubmitSleeveBloomChoice ||
            action == RoundCheatAction.SubmitBeamSwapChoice;

        private Func<int, bool>? CheatDeferDrawPredicate()
        {
            CheatState? cheat = _cheat;
            if (cheat == null)
            {
                return null;
            }

            return seat => cheat.SleeveBloom.ReservationFor(seat) != null ||
                           cheat.SleeveBloom.OffTurnOfferFor(seat) != null;
        }

        private Func<TileId, bool>? CheatProtectionPredicate()
        {
            CheatState? cheat = _cheat;
            if (cheat == null || !HasProtectedWallTiles(cheat))
            {
                return null;
            }

            return tile => cheat.SleeveBloom.IsProtectedTile(tile);
        }

        private CheatState? OpenDiscardWindow(CheatState? state, int seat)
        {
            if (state == null)
            {
                return null;
            }

            SleeveBloomState opened = state.SleeveBloom.WithOpenedWindow(
                seat,
                CountDrawTurns(_round),
                out _);
            return state.WithSleeveBloom(opened);
        }

        private static bool HasProtectedWallTiles(CheatState cheat)
        {
            for (int value = 0; value < TileId.Count; value++)
            {
                if (cheat.SleeveBloom.IsProtectedTile(new TileId(value)))
                {
                    return true;
                }
            }

            return false;
        }

        private static SleeveBloomDrawKind DrawKindOf(TilePositionMap map)
        {
            if (!map.DrawnTile.HasValue)
            {
                return SleeveBloomDrawKind.DealExtra;
            }

            switch (map.DrawnTile.Value.Source)
            {
                case DrawSource.Wall:
                    return SleeveBloomDrawKind.Normal;
                case DrawSource.Rinshan:
                    return SleeveBloomDrawKind.Rinshan;
                default:
                    return SleeveBloomDrawKind.DealExtra;
            }
        }
        private TableOperationResult? ValidateAuthorityIdentity(
            string roundId,
            int seat,
            StateVersion expectedStateVersion,
            OwnerEpoch ownerEpoch,
            ControlEpoch controlEpoch,
            DecisionId? decisionId,
            long recoveryGeneration,
            long now,
            out PendingDecision? pending)
        {
            pending = null;
            if (_strictRecoveryFence && recoveryGeneration != _recoveryGeneration)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleRecoveryGeneration,
                    "Command recovery generation " + recoveryGeneration +
                    " is not current generation " + _recoveryGeneration + ".");
            }

            if (ownerEpoch != _ownerEpoch)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleOwnerEpoch,
                    "Command owner epoch " + ownerEpoch + " is not the table lease epoch " + _ownerEpoch + ".");
            }

            if (!string.Equals(roundId, _round.RoundId, StringComparison.Ordinal))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.RoundMismatch,
                    "Command round '" + roundId + "' does not match '" + _round.RoundId + "'.");
            }

            if (!TryToDomainEpoch(controlEpoch, out long controlEpochValue))
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "Control epoch exceeds the Domain range.");
            }

            if (_pending.TryGetValue(seat, out PendingDecision foundPending))
            {
                pending = foundPending;
            }

            if (pending != null)
            {
                if (!decisionId.HasValue ||
                    decisionId.Value.IsEmpty ||
                    !string.Equals(decisionId.Value.Value, pending.Key.DecisionId, StringComparison.Ordinal))
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
            else if (decisionId.HasValue && !decisionId.Value.IsEmpty)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.DecisionMismatch,
                    "The command refers to a decision that is not open for this seat.");
            }

            if (expectedStateVersion != _stateVersion)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleStateVersion,
                    "Expected state version " + expectedStateVersion + ", actual " + _stateVersion + ".");
            }

            if (_control.GetEpoch(seat) != controlEpochValue)
            {
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleControlEpoch,
                    "Control epoch " + controlEpochValue + " is not current for seat " + seat + ".");
            }

            return null;
        }

        private bool IsCurrentObservation(AiObservation observation)
        {
            if (_recoveryGeneration == 0)
            {
                return true;
            }

            return _aiObservationGenerations.TryGetValue(observation, out ObservationGeneration? holder) &&
                   holder.Generation == _recoveryGeneration;
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
                    !IsCurrentObservation(decision.Observation))
                {
                    return TableTickResult.Rejected(
                        TableOperationStatus.Rejected,
                        TableRuntimeReasonCode.StaleRecoveryGeneration,
                        "The AI observation was not captured in the current recovery generation.",
                        _stateVersion);
                }
            }

            var request = TableTickRequest.WithAiDecisions(new[] { decision });
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
                TableOperationResult? cheatSweep = await SweepExpiredCheatOffersAsync(now).ConfigureAwait(false);
                if (cheatSweep != null && cheatSweep.Status == TableOperationStatus.Failed)
                {
                    return TableTickResult.Rejected(
                        cheatSweep.Status,
                        cheatSweep.ReasonCode,
                        cheatSweep.Detail,
                        _stateVersion);
                }

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
                    actionNow,
                    aiPending,
                    TimingConsumption.Complete).ConfigureAwait(false);

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

                    bool completed = _durable == null
                        ? _decisionClock.Complete(aiTiming.Key, actionNow)
                        : true;
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

        private async ValueTask<TableOperationResult?> SweepExpiredCheatOffersAsync(long now)
        {
            if (_cheat == null || _cheatEconomy == null || _sleeveConfig == null || _round.IsFinished)
            {
                return null;
            }

            CheatTurnContext context = BuildCheatContext(now);
            string currentBoot = _durable?.BootId ?? string.Empty;
            RoundCheatPreparation preparation = RoundCheatTransition.ExpireOffTurn(
                _round,
                _cheat,
                context,
                offer => !string.Equals(offer.BootId, currentBoot, StringComparison.Ordinal));
            if (!preparation.IsReady)
            {
                return null;
            }

            var journal = new TableCheatSystemJournal(
                RoundCheatSystemAction.OffTurnExpiry,
                -1,
                context.TurnNumber,
                context.NowMillis,
                context.RoundOrdinal,
                context.LastDrawKind,
                context.BootId);
            return await CommitCheatAndInstallAsync(
                preparation.Change!,
                roundEnvelope: null,
                cheatEnvelope: null,
                roundCommand: null,
                cheatCommand: null,
                journal,
                context,
                pending: null,
                decisionId: null,
                now).ConfigureAwait(false);
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

            if (_cheat != null && _cheatEconomy != null && _sleeveConfig != null)
            {
                CheatTurnContext cheatContext = BuildCheatContext(now);
                RoundCheatPreparation? cheatExpiry = null;
                RoundCheatSystemAction systemAction = RoundCheatSystemAction.OffTurnExpiry;
                if (_cheat.SleeveBloom.ImmediateOffer?.Seat == seat)
                {
                    cheatExpiry = RoundCheatTransition.ExpireImmediate(_round, _cheat, cheatContext, seat);
                    systemAction = RoundCheatSystemAction.ImmediateExpiry;
                }
                else if (_cheat.BeamSwap.Offer?.Seat == seat)
                {
                    cheatExpiry = RoundCheatTransition.ExpireBeamSwap(_round, _cheat, cheatContext, seat);
                    systemAction = RoundCheatSystemAction.BeamExpiry;
                }

                if (cheatExpiry != null && cheatExpiry.IsReady)
                {
                    var journal = new TableCheatSystemJournal(
                        systemAction,
                        seat,
                        cheatContext.TurnNumber,
                        cheatContext.NowMillis,
                        cheatContext.RoundOrdinal,
                        cheatContext.LastDrawKind,
                        cheatContext.BootId);
                    TableOperationResult applied = await CommitCheatAndInstallAsync(
                        cheatExpiry.Change!,
                        roundEnvelope: null,
                        cheatEnvelope: null,
                        roundCommand: null,
                        cheatCommand: null,
                        journal,
                        cheatContext,
                        pending,
                        new DecisionId(decisionId),
                        now,
                        TimingConsumption.Expire).ConfigureAwait(false);
                    return new TableTickEntry(
                        TickEntryKind.Timeout,
                        seat,
                        decisionId,
                        null,
                        TickRejectionCode.None,
                        "Authoritative deadline reached; the pending cheat offer settled by forced fallback.",
                        applied);
                }
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
                now,
                pending,
                TimingConsumption.Expire).ConfigureAwait(false);

            if (result.Status == TableOperationStatus.Accepted && !_round.IsFinished && _durable == null)
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

            var aiInputs = new List<AiPlanInput>(request.AiSubmissions.Count + request.AiDecisions.Count);
            for (int i = 0; i < request.AiSubmissions.Count; i++)
            {
                AiDecisionSubmission raw = request.AiSubmissions[i] ??
                    throw new ArgumentException("AI submission cannot be null.", nameof(request));
                aiInputs.Add(new AiPlanInput(raw, null));
            }

            for (int i = 0; i < request.AiDecisions.Count; i++)
            {
                AiDecision decision = request.AiDecisions[i] ??
                    throw new ArgumentException("AI decision cannot be null.", nameof(request));
                AiDecisionSubmission submission;
                try
                {
                    submission = decision.ToDomainSubmission(now);
                }
                catch (Exception exception)
                {
                    planned.Add(new PlannedTickEntry(
                        TickEntryKind.AiRejected,
                        decision.Intent.DecisionKey.Seat,
                        decision.Intent.DecisionKey.DecisionId,
                        decision.Intent.Action,
                        TickRejectionCode.InvalidSubmission,
                        exception.Message,
                        null));
                    continue;
                }

                aiInputs.Add(new AiPlanInput(submission, decision));
            }

            for (int i = 0; i < aiInputs.Count; i++)
            {
                AiDecisionSubmission submission = aiInputs[i].Submission;
                int seat = submission.Key.Seat;

                if (!string.Equals(submission.Key.RoundId, _round.RoundId, StringComparison.Ordinal) ||
                    !clockForThisRound)
                {
                    planned.Add(RejectAi(submission, TickRejectionCode.StaleRound, "Submission belongs to another round."));
                    continue;
                }

                if (_recoveryGeneration > 0 && aiInputs[i].Decision == null)
                {
                    planned.Add(RejectAi(
                        submission,
                        TickRejectionCode.StaleRecoveryGeneration,
                        "Raw AI submissions are not accepted after a recovery; submit an actor-captured observation."));
                    continue;
                }

                if (_recoveryGeneration > 0 &&
                    aiInputs[i].Decision != null &&
                    !IsCurrentObservation(aiInputs[i].Decision!.Observation))
                {
                    planned.Add(RejectAi(
                        submission,
                        TickRejectionCode.StaleRecoveryGeneration,
                        "The AI observation was not captured in the current recovery generation."));
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

        private sealed class AiPlanInput
        {
            public AiPlanInput(AiDecisionSubmission submission, AiDecision? decision)
            {
                Submission = submission;
                Decision = decision;
            }

            public AiDecisionSubmission Submission { get; }

            public AiDecision? Decision { get; }
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

        private sealed class ObservationGeneration
        {
            public ObservationGeneration(long generation)
            {
                Generation = generation;
            }

            public long Generation { get; }
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

        private enum TimingConsumption : byte
        {
            None = 0,
            Complete = 1,
            Expire = 2
        }

        private async ValueTask<TableOperationResult> ApplyInternalCommandAsync(
            RoundCommand command,
            RequestId requestId,
            long now,
            PendingDecision? pending,
            TimingConsumption timingConsumption)
        {
            if (TableSettlementIntent.IsSettlementAction(command.Action) &&
                _settlementFactsPort != null)
            {
                // The AI/timeout adapter has no typed settlement intent and no host fact
                // binding. Refuse it rather than silently falling back to a caller-supplied
                // settlement when the N-2 authority is configured.
                return TableOperationResult.Rejected(
                    _stateVersion,
                    TableSettlementReasonCode.RawEntryRejected,
                    "The internal AI/timeout path cannot submit a settlement command under the N-2 authority.");
            }

            DecisionId? timingDecision = pending != null
                ? (DecisionId?)new DecisionId(pending.Key.DecisionId)
                : null;

            if (_cheat != null && _cheatEconomy != null && _sleeveConfig != null)
            {
                CheatTurnContext context = BuildCheatContext(now);
                if (command.Action == RoundActionKind.DrawTile)
                {
                    RoundCheatPreparation delivery = RoundCheatTransition.PrepareReservationDraw(
                        _round,
                        _cheat,
                        context,
                        command.Seat);
                    if (delivery.IsReady)
                    {
                        return await CommitCheatAndInstallAsync(
                            delivery.Change!,
                            roundEnvelope: null,
                            cheatEnvelope: null,
                            command,
                            cheatCommand: null,
                            systemJournal: null,
                            context,
                            pending,
                            timingDecision,
                            now,
                            timingConsumption).ConfigureAwait(false);
                    }
                }
                else if (command.Action == RoundActionKind.DiscardTile &&
                         (command.EligibleSeats == null || command.EligibleSeats.Count == 0))
                {
                    int nextSeat = (command.Seat + 1) % 4;
                    if (_cheat.SleeveBloom.ReservationFor(nextSeat) != null ||
                        _cheat.SleeveBloom.OffTurnOfferFor(nextSeat) != null)
                    {
                        RoundCheatPreparation delivery = RoundCheatTransition.PrepareDiscardDelivery(
                            _round,
                            _cheat,
                            command,
                            now);
                        if (!delivery.IsReady)
                        {
                            return TableOperationResult.Rejected(
                                _stateVersion,
                                TableRuntimeReasonCode.FromCheat(delivery.Error, delivery.CheatError),
                                delivery.Message);
                        }

                        return await CommitCheatAndInstallAsync(
                            delivery.Change!,
                            roundEnvelope: null,
                            cheatEnvelope: null,
                            command,
                            cheatCommand: null,
                            systemJournal: null,
                            context,
                            pending,
                            timingDecision,
                            now,
                            timingConsumption).ConfigureAwait(false);
                    }
                }
            }

            if (_cheat != null && _cheatEconomy != null && _sleeveConfig != null)
            {
                RoundCheatPreparation? settlement = TrySettleOfferForCommand(
                    command,
                    now,
                    command.Action == RoundActionKind.DiscardTile ||
                    command.Action == RoundActionKind.DeclareRiichi,
                    deliverDeferred: command.Action == RoundActionKind.Pass ||
                                     command.Action == RoundActionKind.DeclareRiichi);
                if (settlement != null)
                {
                    if (!settlement.IsReady)
                    {
                        return TableOperationResult.Rejected(
                            _stateVersion,
                            TableRuntimeReasonCode.FromCheat(settlement.Error, settlement.CheatError),
                            settlement.Message);
                    }

                    return await CommitCheatAndInstallAsync(
                        settlement.Change!,
                        roundEnvelope: null,
                        cheatEnvelope: null,
                        command,
                        cheatCommand: null,
                        systemJournal: null,
                        BuildCheatContext(now),
                        pending,
                        timingDecision,
                        now,
                        timingConsumption).ConfigureAwait(false);
                }
            }

            if ((command.Action == RoundActionKind.Pass ||
                 command.Action == RoundActionKind.DeclareRiichi) &&
                _cheat != null)
            {
                Func<int, bool>? defer = CheatDeferDrawPredicate();
                if (defer != null)
                {
                    RoundPreparation deferred = _round.Prepare(
                        command,
                        CheatProtectionPredicate(),
                        defer);
                    if (deferred.IsReady && deferred.Change!.NextState.Phase == RoundPhase.Draw)
                    {
                        RoundCheatPreparation delivery = RoundCheatTransition.PrepareDeferredDrawDelivery(
                            _round,
                            deferred.Change,
                            _cheat,
                            now);
                        if (!delivery.IsReady)
                        {
                            return TableOperationResult.Rejected(
                                _stateVersion,
                                TableRuntimeReasonCode.FromCheat(delivery.Error, delivery.CheatError),
                                delivery.Message);
                        }

                        return await CommitCheatAndInstallAsync(
                            delivery.Change!,
                            roundEnvelope: null,
                            cheatEnvelope: null,
                            command,
                            cheatCommand: null,
                            systemJournal: null,
                            BuildCheatContext(now),
                            pending,
                            timingDecision,
                            now,
                            timingConsumption).ConfigureAwait(false);
                    }
                }
            }

            RoundTransition transition;
            try
            {
                transition = _round.Execute(command, CheatProtectionPredicate());
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

            CheatState? nextCheat = command.Action == RoundActionKind.DiscardTile
                ? OpenDiscardWindow(_cheat, command.Seat)
                : null;
            PendingDecision? timingPending = _durable != null ? pending : null;
            DecisionId? durableTimingDecision = _durable != null ? timingDecision : null;
            return await CommitAndInstallAsync(
                transition,
                requestId,
                timingPending,
                durableTimingDecision,
                now,
                null,
                command,
                _durable != null ? timingConsumption : TimingConsumption.None,
                nextCheat).ConfigureAwait(false);
        }

        private async ValueTask<TableOperationResult> CommitAndInstallAsync(
            RoundTransition transition,
            RequestId requestId,
            PendingDecision? pending,
            DecisionId? decisionId,
            long now,
            TableCommandEnvelope? envelope = null,
            RoundCommand? commandForJournal = null,
            TimingConsumption timingConsumption = TimingConsumption.None,
            CheatState? nextCheat = null)
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

            List<TableEventRecord> records = BuildEventRecords(
                commandVersion,
                domainEvents,
                transition.State!.SourceFingerprint);
            TableCommandReceipt receipt = TableCommandReceipt.Applied(
                evaluated,
                commandVersion,
                lastEventSeq,
                requestId);

            RoundStateMachine nextState = transition.State!;
            if (_durable != null)
            {
                try
                {
                    return await CommitDurableAcceptedAsync(
                        nextState,
                        evaluated,
                        commandVersion,
                        lastEventSeq,
                        records,
                        receipt,
                        envelope,
                        pending,
                        decisionId,
                        now,
                        commandForJournal,
                        timingConsumption,
                        nextCheat).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.InternalError,
                        "DurableCommit " + exception.GetType().Name + ": " + exception.Message);
                }
            }

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
            if (nextCheat != null)
            {
                _cheat = nextCheat;
            }

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
            _commandBindings.Add(new TableCommandBinding(
                commandVersion.Value,
                records.Count,
                nextState.SourceFingerprint));
            TableSnapshot snapshot = BuildSnapshot();
            Publish(new TablePublication(TablePublicationKind.Applied, snapshot, records, receipt));
            return TableOperationResult.Accepted(receipt, records);
        }

        private async ValueTask<TableOperationResult> CommitDurableAcceptedAsync(
            RoundStateMachine nextState,
            StateVersion evaluated,
            StateVersion commandVersion,
            EventSeq lastEventSeq,
            IReadOnlyList<TableEventRecord> records,
            TableCommandReceipt receipt,
            TableCommandEnvelope? envelope,
            PendingDecision? pending,
            DecisionId? decisionId,
            long now,
            RoundCommand? commandForJournal = null,
            TimingConsumption timingConsumption = TimingConsumption.None,
            CheatState? nextCheat = null)
        {
            TableDurableContext durable = _durable!;
            ulong createdUtc = NowUtc();
            string accountId = "system";
            RequestScopeId scopeId = new RequestScopeId("system");
            string api = "INTERNAL_COMMAND";
            TablePayloadSha256 digest;
            TableDurableReceipt? durableReceipt = null;

            byte[] commandPayload = RoundCommandCodec.Encode(
                commandForJournal ?? envelope!.Command);
            if (envelope != null)
            {
                TableScopeLookup lookup = await durable.Scopes
                    .LookupAsync(envelope.ScopeId!.Value, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!lookup.Found || lookup.Scope == null)
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        ProtocolErrorCode.Unauthenticated,
                        "The host-issued scope disappeared before commit.");
                }

                accountId = lookup.Scope.AccountId;
                scopeId = envelope.ScopeId.Value;
                api = envelope.Api!;
                digest = envelope.PayloadSha256!.Value;
                durableReceipt = new TableDurableReceipt(
                    accountId,
                    scopeId,
                    envelope.RequestId,
                    api,
                    digest,
                    Receipt.Applied(
                        evaluated,
                        commandVersion,
                        lastEventSeq,
                        null,
                        createdUtc == 0 ? (ulong?)null : createdUtc),
                    string.Empty,
                    createdUtc,
                    ReceiptExpiry(createdUtc, durable));
            }
            else
            {
                digest = TablePayloadSha256.Compute(commandPayload);
            }

            var journal = new TableCommandJournalRecord(
                commandVersion,
                evaluated,
                envelope != null ? envelope.RequestId : new RequestId("internal-" + commandVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                accountId,
                scopeId,
                api,
                digest,
                commandPayload);

            TableCheckpoint? checkpoint = null;
            IReadOnlyList<TableSettlementOutboxRecord> outbox = Array.Empty<TableSettlementOutboxRecord>();

            DecisionClock candidateClock = _decisionClock.Clone();
            int consumedSeat = -1;
            if (pending != null &&
                decisionId.HasValue &&
                string.Equals(decisionId.Value.Value, pending.Key.DecisionId, StringComparison.Ordinal))
            {
                DecisionTiming? timing = candidateClock.Timing(pending.Key.Seat);
                if (timing == null || timing.Key != pending.Key)
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.DecisionMismatch,
                        "The open timing no longer matches the command's decision.");
                }

                bool closed = timingConsumption == TimingConsumption.Expire
                    ? candidateClock.Expire(pending.Key, timing.Generation, now)
                    : candidateClock.Complete(pending.Key, now);
                if (!closed)
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.DecisionMismatch,
                        "The candidate timing could not close the committed decision.");
                }

                consumedSeat = pending.Key.Seat;
            }

            var pendingAfter = new List<PendingDecision>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                if (seat == consumedSeat)
                {
                    continue;
                }

                if (_pending.TryGetValue(seat, out PendingDecision? value))
                {
                    pendingAfter.Add(value);
                }
            }

            if (nextState.IsFinished)
            {
                candidateClock.Reset();
                pendingAfter.Clear();
            }

            long nextTimingVersion = checked(_timingVersion + 1L);
            TableTimingCheckpoint timingSnapshot = TableCheckpointFactory.CaptureTiming(
                candidateClock,
                pendingAfter,
                now,
                createdUtc,
                durable.BootId);

            if (nextState.IsFinished)
            {
                if (nextState.Result == null)
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                        "A finished round has no result.");
                }

                checkpoint = BuildCheckpoint(
                    nextState,
                    commandVersion,
                    lastEventSeq,
                    terminal: true,
                    now,
                    timingVersionOverride: nextTimingVersion,
                    extraHistory: records,
                    timingOverride: timingSnapshot);
                byte[] payload = TableSettlementPayloadCodec.Encode(_options.RoomId, nextState.Result);
                outbox = new[]
                {
                    new TableSettlementOutboxRecord(
                        durable.MatchId ?? _options.RoomId,
                        checked((int)commandVersion.Value),
                        payload,
                        TablePayloadSha256.Compute(payload),
                        createdUtc)
                };
            }

            byte[] nextStatePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(nextState));
            TableProjectionContextData? projectionFacts = CaptureProjectionFacts(
                nextState,
                commandVersion,
                lastEventSeq,
                records,
                now,
                createdUtc);
            byte[]? factsPayload = projectionFacts == null
                ? null
                : TableProjectionFactsCodec.Encode(projectionFacts);
            TablePayloadSha256? factsHash = factsPayload == null
                ? (TablePayloadSha256?)null
                : TablePayloadSha256.Compute(factsPayload);
            var request = new TableDurableCommitRequest(
                _options.RoomId,
                durable.OwnerInstance,
                _ownerEpoch,
                evaluated,
                durableReceipt,
                envelope == null,
                records,
                journal,
                outbox,
                checkpoint,
                nextStatePayload,
                nextState.SourceFingerprint,
                nextState.IsFinished,
                timingSnapshot,
                nextTimingVersion,
                factsPayload,
                factsHash,
                (nextCheat ?? _cheat)?.Version ?? 0L,
                (nextCheat ?? _cheat)?.Fingerprint ?? 0UL);

            TableDurableCommitResult commit;
            try
            {
                commit = await durable.Store.CommitAsync(request, CancellationToken.None).ConfigureAwait(false);
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
                EnterFaulted(TableRuntimeReasonCode.CommitFailed, exception.GetType().Name + ": " + exception.Message);
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.CommitFailed,
                    exception.GetType().Name + ": " + exception.Message);
            }

            switch (commit.Disposition)
            {
                case TableCommitDisposition.Replayed:
                    return commit.Receipt == null
                        ? TableOperationResult.Failed(
                            evaluated,
                            TableRuntimeReasonCode.CommitFailed,
                            "A replayed commit result had no receipt.")
                        : TableOperationResult.FromReplay(commit.Receipt);
                case TableCommitDisposition.Conflict:
                    return TableOperationResult.Conflict(commit.Detail);
                case TableCommitDisposition.ScopeRejected:
                    return TableOperationResult.Rejected(evaluated, commit.ReasonCode, commit.Detail);
                case TableCommitDisposition.StaleOwner:
                    EnterLeaseLost(commit.Detail);
                    return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.StaleOwnerEpoch, commit.Detail);
                case TableCommitDisposition.StaleState:
                    EnterFaulted(TableRuntimeReasonCode.StaleStateVersion, commit.Detail);
                    return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.StaleStateVersion, commit.Detail);
                case TableCommitDisposition.OutcomeUnknown:
                    EnterFaulted(TableRuntimeReasonCode.CommitOutcomeUnknown, commit.Detail);
                    return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.CommitOutcomeUnknown, commit.Detail);
                case TableCommitDisposition.Cancelled:
                    return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.CommitCancelled, commit.Detail);
                case TableCommitDisposition.Failed:
                    return TableOperationResult.Failed(
                        evaluated,
                        string.IsNullOrEmpty(commit.ReasonCode) ? TableRuntimeReasonCode.CommitFailed : commit.ReasonCode,
                        commit.Detail);
            }

            // The commit is durable. The candidate timing was persisted in the same transaction,
            // so installing it cannot drift from the recovered reserve/decision state.
            _round = nextState;
            if (nextCheat != null)
            {
                _cheat = nextCheat;
            }

            _stateVersion = commandVersion;
            _lastEventSeq = lastEventSeq;
            _decisionClock = candidateClock;
            _timingVersion = nextTimingVersion;
            _pending.Clear();
            for (int i = 0; i < pendingAfter.Count; i++)
            {
                PendingDecision value = pendingAfter[i];
                _pending[value.Key.Seat] = value;
            }

            TableCommandReceipt durableCommandReceipt = durableReceipt?.ToCommandReceipt() ?? receipt;
            AppendRecent(records);
            _commandBindings.Add(new TableCommandBinding(
                commandVersion.Value,
                records.Count,
                nextState.SourceFingerprint));
            TableSnapshot snapshot = BuildSnapshot();
            Publish(new TablePublication(TablePublicationKind.Applied, snapshot, records, durableCommandReceipt));
            return TableOperationResult.Accepted(durableCommandReceipt, records, durableReceipt);
        }

        private async ValueTask<TableOperationResult> CommitCheatAndInstallAsync(
            RoundCheatChange change,
            TableCommandEnvelope? roundEnvelope,
            TableCheatCommandEnvelope? cheatEnvelope,
            RoundCommand? roundCommand,
            RoundCheatCommand? cheatCommand,
            TableCheatSystemJournal? systemJournal,
            CheatTurnContext context,
            PendingDecision? pending,
            DecisionId? decisionId,
            long now,
            TimingConsumption timingConsumption = TimingConsumption.Complete)
        {
            StateVersion evaluated = _stateVersion;
            StateVersion commandVersion;
            try
            {
                commandVersion = evaluated.Next();
            }
            catch (OverflowException)
            {
                return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.InternalError, "State version overflow.");
            }

            RoundTransition installed;
            try
            {
                installed = _round.Install(change.RoundChange);
            }
            catch (Exception exception)
            {
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message);
            }

            if (!installed.IsAccepted || installed.State == null)
            {
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.InternalError,
                    "The prepared cheat candidate did not install: " +
                    (installed.Error == null ? "unknown" : installed.Error.ToString()));
            }

            RoundStateMachine nextState = installed.State;
            List<TableEventRecord> records = BuildCheatEventRecords(
                commandVersion,
                new EventSeq(_lastEventSeq.Value + 1),
                installed.Events,
                change.Events,
                nextState.SourceFingerprint);
            EventSeq lastEventSeq = records[records.Count - 1].Seq;

            string reasonCode = change.Accepted
                ? string.Empty
                : TableRuntimeReasonCode.FromCheat(RoundCheatErrorCode.CheatRejected, change.Error);
            RequestId requestId = roundEnvelope?.RequestId ??
                                  cheatEnvelope?.RequestId ??
                                  new RequestId("internal-" + commandVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            TableCommandReceipt receipt = change.Accepted
                ? TableCommandReceipt.Applied(evaluated, commandVersion, lastEventSeq, requestId)
                : TableCommandReceipt.Rejected(evaluated, reasonCode, requestId);

            if (_durable == null)
            {
                var request = new TableCommitRequest(
                    _options.RoomId,
                    _ownerEpoch,
                    evaluated,
                    commandVersion,
                    lastEventSeq,
                    nextState.SourceFingerprint,
                    records,
                    receipt,
                    rejectedWithState: !change.Accepted);
                TableCommitResult commit;
                try
                {
                    commit = await _commitPort.CommitAsync(request, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.CommitCancelled, exception.Message);
                }
                catch (Exception exception)
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.CommitFailed,
                        exception.GetType().Name + ": " + exception.Message);
                }

                if (commit == null || !commit.IsCommitted)
                {
                    return commit == null
                        ? TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.CommitFailed, "The commit port returned no result.")
                        : CommitFailure(evaluated, commit);
                }

                InstallCommittedCheat(nextState, change.CheatState, commandVersion, lastEventSeq, records, pending, decisionId, now, receipt, timingConsumption);
                TableOperationResult result = change.Accepted
                    ? TableOperationResult.Accepted(receipt, records)
                    : TableOperationResult.RejectedWithState(receipt, records);
                return result;
            }

            TableDurableContext durable = _durable;
            ulong createdUtc = NowUtc();
            string accountId = "system";
            RequestScopeId scopeId = new RequestScopeId("system");
            string api = "INTERNAL_CHEAT_SYSTEM";
            TablePayloadSha256 digest;
            TableDurableReceipt? durableReceipt = null;
            byte[] commandPayload;
            int journalSchema;
            if (roundEnvelope != null || cheatEnvelope != null)
            {
                RequestScopeId requestScope = roundEnvelope != null ? roundEnvelope.ScopeId!.Value : cheatEnvelope!.ScopeId!.Value;
                string requestApi = roundEnvelope != null ? roundEnvelope.Api! : cheatEnvelope!.Api!;
                TablePayloadSha256 requestDigest = roundEnvelope != null
                    ? roundEnvelope.PayloadSha256!.Value
                    : cheatEnvelope!.PayloadSha256!.Value;
                RequestId requestRequestId = roundEnvelope != null ? roundEnvelope.RequestId : cheatEnvelope!.RequestId;
                TableScopeLookup lookup = await durable.Scopes
                    .LookupAsync(requestScope, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!lookup.Found || lookup.Scope == null)
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        ProtocolErrorCode.Unauthenticated,
                        "The host-issued scope disappeared before cheat commit.");
                }

                accountId = lookup.Scope.AccountId;
                scopeId = requestScope;
                api = requestApi;
                digest = requestDigest;
                if (roundEnvelope != null)
                {
                    commandPayload = RoundCommandCodec.Encode(roundCommand!);
                    journalSchema = RoundCommandCodec.FormatVersion;
                }
                else
                {
                    commandPayload = TableCheatCommandCodec.Encode(new RoundCheatJournal(
                        cheatCommand!,
                        context.TurnNumber,
                        context.NowMillis,
                        context.RoundOrdinal,
                        context.LastDrawKind,
                        context.BootId));
                    journalSchema = 2;
                }

                durableReceipt = new TableDurableReceipt(
                    accountId,
                    scopeId,
                    requestRequestId,
                    api,
                    digest,
                    change.Accepted
                        ? Receipt.Applied(evaluated, commandVersion, lastEventSeq, null, createdUtc == 0 ? (ulong?)null : createdUtc)
                        : Receipt.Rejected(evaluated, reasonCode),
                    string.Empty,
                    createdUtc,
                    ReceiptExpiry(createdUtc, durable));
            }
            else
            {
                commandPayload = TableCheatSystemCodec.Encode(systemJournal!);
                journalSchema = 3;
                digest = TablePayloadSha256.Compute(commandPayload);
            }

            var journal = new TableCommandJournalRecord(
                commandVersion,
                evaluated,
                roundEnvelope?.RequestId ??
                cheatEnvelope?.RequestId ??
                new RequestId("internal-" + commandVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                accountId,
                scopeId,
                api,
                digest,
                commandPayload,
                journalSchema);

            DecisionClock candidateClock = _decisionClock.Clone();
            int consumedSeat = -1;
            if (change.Accepted &&
                pending != null &&
                decisionId.HasValue &&
                string.Equals(decisionId.Value.Value, pending.Key.DecisionId, StringComparison.Ordinal))
            {
                DecisionTiming? timing = candidateClock.Timing(pending.Key.Seat);
                if (timing == null || timing.Key != pending.Key)
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.DecisionMismatch,
                        "The open timing no longer matches the cheat command's decision.");
                }

                if (!(timingConsumption == TimingConsumption.Expire
                        ? candidateClock.Expire(pending.Key, timing.Generation, now)
                        : candidateClock.Complete(pending.Key, now)))
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.DecisionMismatch,
                        "The candidate timing could not close the committed cheat decision.");
                }

                consumedSeat = pending.Key.Seat;
            }

            var pendingAfter = new List<PendingDecision>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                if (seat == consumedSeat)
                {
                    continue;
                }

                if (_pending.TryGetValue(seat, out PendingDecision? value))
                {
                    pendingAfter.Add(value);
                }
            }

            long nextTimingVersion = checked(_timingVersion + 1L);
            TableTimingCheckpoint timingSnapshot = TableCheckpointFactory.CaptureTiming(
                candidateClock,
                pendingAfter,
                now,
                createdUtc,
                durable.BootId);

            byte[] nextStatePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(nextState));
            TableProjectionContextData? projectionFacts = CaptureProjectionFacts(
                nextState,
                commandVersion,
                lastEventSeq,
                records,
                now,
                createdUtc);
            byte[]? factsPayload = projectionFacts == null
                ? null
                : TableProjectionFactsCodec.Encode(projectionFacts);
            TablePayloadSha256? factsHash = factsPayload == null
                ? null
                : TablePayloadSha256.Compute(factsPayload);

            TableCheckpoint? checkpoint = null;
            IReadOnlyList<TableSettlementOutboxRecord> outbox = Array.Empty<TableSettlementOutboxRecord>();
            if (nextState.IsFinished)
            {
                if (nextState.Result == null)
                {
                    return TableOperationResult.Failed(
                        evaluated,
                        TableRuntimeReasonCode.RecoveryReplayMismatch,
                        "A finished cheated round has no result.");
                }

                checkpoint = BuildCheckpoint(
                    nextState,
                    commandVersion,
                    lastEventSeq,
                    terminal: true,
                    now,
                    timingVersionOverride: nextTimingVersion,
                    extraHistory: records,
                    timingOverride: timingSnapshot,
                    cheatOverride: change.CheatState);
                byte[] settlementPayload = TableSettlementPayloadCodec.Encode(_options.RoomId, nextState.Result);
                outbox = new[]
                {
                    new TableSettlementOutboxRecord(
                        durable.MatchId ?? _options.RoomId,
                        checked((int)commandVersion.Value),
                        settlementPayload,
                        TablePayloadSha256.Compute(settlementPayload),
                        createdUtc)
                };
            }

            var requestDurable = new TableDurableCommitRequest(
                _options.RoomId,
                durable.OwnerInstance,
                _ownerEpoch,
                evaluated,
                durableReceipt,
                internalCommand: roundEnvelope == null && cheatEnvelope == null,
                events: records,
                commandJournal: journal,
                outbox: outbox,
                checkpoint: checkpoint,
                nextStatePayload: nextStatePayload,
                nextStateFingerprint: nextState.SourceFingerprint,
                terminal: nextState.IsFinished,
                timing: timingSnapshot,
                timingVersion: nextTimingVersion,
                committedFactsPayload: factsPayload,
                committedFactsPayloadSha256: factsHash,
                nextCheatVersion: change.CheatState.Version,
                nextCheatFingerprint: change.CheatState.Fingerprint);

            TableDurableCommitResult durableCommit;
            try
            {
                durableCommit = await durable.Store.CommitAsync(requestDurable, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.CommitCancelled, exception.Message);
            }
            catch (Exception exception)
            {
                EnterFaulted(TableRuntimeReasonCode.CommitFailed, exception.GetType().Name + ": " + exception.Message);
                return TableOperationResult.Failed(
                    evaluated,
                    TableRuntimeReasonCode.CommitFailed,
                    exception.GetType().Name + ": " + exception.Message);
            }

            switch (durableCommit.Disposition)
            {
                case TableCommitDisposition.Replayed:
                    return durableCommit.Receipt == null
                        ? TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.CommitFailed, "A replayed commit result had no receipt.")
                        : TableOperationResult.FromReplay(durableCommit.Receipt);
                case TableCommitDisposition.Conflict:
                    return TableOperationResult.Conflict(durableCommit.Detail);
                case TableCommitDisposition.ScopeRejected:
                    return TableOperationResult.Rejected(evaluated, durableCommit.ReasonCode, durableCommit.Detail);
                case TableCommitDisposition.StaleOwner:
                    EnterLeaseLost(durableCommit.Detail);
                    return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.StaleOwnerEpoch, durableCommit.Detail);
                case TableCommitDisposition.StaleState:
                    EnterFaulted(TableRuntimeReasonCode.StaleStateVersion, durableCommit.Detail);
                    return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.StaleStateVersion, durableCommit.Detail);
                case TableCommitDisposition.OutcomeUnknown:
                    EnterFaulted(TableRuntimeReasonCode.CommitOutcomeUnknown, durableCommit.Detail);
                    return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.CommitOutcomeUnknown, durableCommit.Detail);
                case TableCommitDisposition.Cancelled:
                    return TableOperationResult.Failed(evaluated, TableRuntimeReasonCode.CommitCancelled, durableCommit.Detail);
                case TableCommitDisposition.Failed:
                    return TableOperationResult.Failed(
                        evaluated,
                        string.IsNullOrEmpty(durableCommit.ReasonCode) ? TableRuntimeReasonCode.CommitFailed : durableCommit.ReasonCode,
                        durableCommit.Detail);
            }

            _round = nextState;
            _cheat = change.CheatState;
            _stateVersion = commandVersion;
            _lastEventSeq = lastEventSeq;
            _decisionClock = candidateClock;
            _timingVersion = nextTimingVersion;
            _pending.Clear();
            for (int i = 0; i < pendingAfter.Count; i++)
            {
                PendingDecision value = pendingAfter[i];
                _pending[value.Key.Seat] = value;
            }

            TableCommandReceipt durableCommandReceipt = durableReceipt?.ToCommandReceipt() ?? receipt;
            AppendRecent(records);
            _commandBindings.Add(new TableCommandBinding(
                commandVersion.Value,
                records.Count,
                nextState.SourceFingerprint));
            TableSnapshot snapshot = BuildSnapshot();
            Publish(new TablePublication(TablePublicationKind.Applied, snapshot, records, durableCommandReceipt));
            return change.Accepted
                ? TableOperationResult.Accepted(durableCommandReceipt, records, durableReceipt)
                : TableOperationResult.RejectedCommitted(durableReceipt!);
        }

        private void InstallCommittedCheat(
            RoundStateMachine nextState,
            CheatState nextCheat,
            StateVersion commandVersion,
            EventSeq lastEventSeq,
            IReadOnlyList<TableEventRecord> records,
            PendingDecision? pending,
            DecisionId? decisionId,
            long now,
            TableCommandReceipt receipt,
            TimingConsumption timingConsumption)
        {
            _round = nextState;
            _cheat = nextCheat;
            _stateVersion = commandVersion;
            _lastEventSeq = lastEventSeq;
            if (pending != null &&
                decisionId.HasValue &&
                string.Equals(decisionId.Value.Value, pending.Key.DecisionId, StringComparison.Ordinal))
            {
                DecisionTiming? timing = _decisionClock.Timing(pending.Key.Seat);
                bool closed = timing != null &&
                              (timingConsumption == TimingConsumption.Expire
                                  ? _decisionClock.Expire(pending.Key, timing.Generation, now)
                                  : _decisionClock.Complete(pending.Key, now));
                if (!closed)
                {
                    SetFaulted(new InvalidOperationException("The decision clock could not close the committed cheat command."));
                }

                _pending.Remove(pending.Key.Seat);
            }

            AppendRecent(records);
            _commandBindings.Add(new TableCommandBinding(
                commandVersion.Value,
                records.Count,
                nextState.SourceFingerprint));
            TableSnapshot snapshot = BuildSnapshot();
            Publish(new TablePublication(TablePublicationKind.Applied, snapshot, records, receipt));
        }

        private static List<TableEventRecord> BuildCheatEventRecords(
            StateVersion commandVersion,
            EventSeq firstSeq,
            IReadOnlyList<RoundEvent> roundEvents,
            IReadOnlyList<RoundCheatEvent> cheatEvents,
            ulong sourceFingerprint)
        {
            var records = new List<TableEventRecord>(roundEvents.Count + cheatEvents.Count);
            int ordinal = 0;
            ulong seq = firstSeq.Value;
            for (int i = 0; i < roundEvents.Count; i++)
            {
                RoundEvent value = roundEvents[i];
                records.Add(new TableEventRecord(
                    new EventSeq(seq++),
                    commandVersion,
                    ordinal,
                    value.Type,
                    value.Seat,
                    value.Tile,
                    value.Detail,
                    1,
                    TableEventFactCodec.Encode(commandVersion, ordinal, sourceFingerprint)));
                ordinal++;
            }

            for (int i = 0; i < cheatEvents.Count; i++)
            {
                RoundCheatEvent value = cheatEvents[i];
                records.Add(new TableEventRecord(
                    new EventSeq(seq++),
                    commandVersion,
                    ordinal,
                    RoundEventType.CheatApplied,
                    value.Value.Seat,
                    null,
                    null,
                    1,
                    TableEventFactCodec.Encode(commandVersion, ordinal, sourceFingerprint),
                    new TableCheatEventRecord(
                        value.Value,
                        value.OfferId,
                        value.Keep,
                        value.Action,
                        value.ResultTile,
                        value.HandTile,
                        value.TargetSlot,
                        value.TurnNumber)));
                ordinal++;
            }

            return records;
        }

        private async ValueTask<TableOperationResult> CommitRejectedReceiptAsync(
            TableScopeRecord scope,
            RequestId requestId,
            string api,
            TablePayloadSha256 payloadSha256,
            TableOperationResult rejection)
        {
            TableDurableContext durable = _durable!;
            ulong createdUtc = NowUtc();
            var receipt = new TableDurableReceipt(
                scope.AccountId,
                scope.ScopeId,
                requestId,
                api,
                payloadSha256,
                Receipt.Rejected(rejection.EvaluatedStateVersion, rejection.ReasonCode),
                rejection.Detail,
                createdUtc,
                ReceiptExpiry(createdUtc, durable));

            var request = new TableDurableCommitRequest(
                _options.RoomId,
                durable.OwnerInstance,
                _ownerEpoch,
                _stateVersion,
                receipt,
                internalCommand: false,
                events: null,
                commandJournal: null,
                outbox: null,
                checkpoint: null,
                nextStatePayload: null,
                nextStateFingerprint: 0UL,
                terminal: false);

            TableDurableCommitResult commit;
            try
            {
                commit = await durable.Store.CommitAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.CommitCancelled,
                    exception.Message);
            }
            catch (Exception exception)
            {
                EnterFaulted(TableRuntimeReasonCode.CommitFailed, exception.GetType().Name + ": " + exception.Message);
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.CommitFailed,
                    exception.GetType().Name + ": " + exception.Message);
            }

            switch (commit.Disposition)
            {
                case TableCommitDisposition.Committed:
                    return TableOperationResult.RejectedCommitted(commit.Receipt ?? receipt);
                case TableCommitDisposition.Replayed:
                    return commit.Receipt == null
                        ? TableOperationResult.Failed(
                            _stateVersion,
                            TableRuntimeReasonCode.CommitFailed,
                            "A replayed rejection had no receipt.")
                        : TableOperationResult.FromReplay(commit.Receipt);
                case TableCommitDisposition.Conflict:
                    return TableOperationResult.Conflict(commit.Detail);
                case TableCommitDisposition.ScopeRejected:
                    return TableOperationResult.Rejected(_stateVersion, commit.ReasonCode, commit.Detail);
                case TableCommitDisposition.StaleOwner:
                    EnterLeaseLost(commit.Detail);
                    return TableOperationResult.Failed(_stateVersion, TableRuntimeReasonCode.StaleOwnerEpoch, commit.Detail);
                case TableCommitDisposition.StaleState:
                    EnterFaulted(TableRuntimeReasonCode.StaleStateVersion, commit.Detail);
                    return TableOperationResult.Failed(_stateVersion, TableRuntimeReasonCode.StaleStateVersion, commit.Detail);
                case TableCommitDisposition.OutcomeUnknown:
                    EnterFaulted(TableRuntimeReasonCode.CommitOutcomeUnknown, commit.Detail);
                    return TableOperationResult.Failed(_stateVersion, TableRuntimeReasonCode.CommitOutcomeUnknown, commit.Detail);
                default:
                    return TableOperationResult.Failed(
                        _stateVersion,
                        string.IsNullOrEmpty(commit.ReasonCode) ? TableRuntimeReasonCode.CommitFailed : commit.ReasonCode,
                        commit.Detail);
            }
        }

        private TableProjectionContextData? CaptureProjectionFacts(
            RoundStateMachine round,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            IReadOnlyList<TableEventRecord>? extraEvents,
            long now,
            ulong utcNow)
        {
            if (_durable == null ||
                (_durable.ProjectionFactsProvider == null && _projectionContextSource == null))
            {
                return null;
            }

            var events = new List<TableEventRecord>(_committedEventLog);
            if (extraEvents != null)
            {
                for (int i = 0; i < extraEvents.Count; i++)
                {
                    events.Add(extraEvents[i]);
                }
            }

            var request = new TableProjectionContextRequest(
                _options.RoomId,
                round,
                stateVersion,
                lastEventSeq,
                _ownerEpoch,
                _controlVersion,
                _recoveryGeneration,
                now,
                (long)utcNow,
                events);
            TableProjectionContextData? facts = _durable.ProjectionFactsProvider != null
                ? _durable.ProjectionFactsProvider(request)
                : _projectionContextSource!.Capture(request);
            if (facts == null ||
                !string.Equals(facts.RoundId, round.RoundId, StringComparison.Ordinal) ||
                facts.StateVersion != stateVersion)
            {
                throw new InvalidOperationException(
                    "Projection facts are missing or do not match the committed round/state.");
            }

            if (_cheat != null)
            {
                facts = OverlayCheat(facts, _cheat);
            }

            IReadOnlyList<TableProjectionEventFact> mergedFacts = MergeCheatFacts(facts.EventFacts, events);
            if (!ReferenceEquals(mergedFacts, facts.EventFacts))
            {
                facts = new TableProjectionContextData(
                    facts.RoundId,
                    facts.StateVersion,
                    facts.SessionVersion,
                    facts.Cheat,
                    facts.PublicFacts,
                    facts.PrivateFacts,
                    facts.Session,
                    facts.Policy,
                    mergedFacts,
                    facts.ConcealedMeldFacts);
            }

            return facts;
        }

        private static TableProjectionContextData OverlayCheat(
            TableProjectionContextData data,
            CheatState cheat)
        {
            return new TableProjectionContextData(
                data.RoundId,
                data.StateVersion,
                data.SessionVersion,
                cheat,
                data.PublicFacts,
                data.PrivateFacts,
                data.Session,
                data.Policy,
                data.EventFacts,
                data.ConcealedMeldFacts);
        }

        private TableCheckpoint BuildCheckpoint(
            RoundStateMachine round,
            StateVersion stateVersion,
            EventSeq lastEventSeq,
            bool terminal,
            long now,
            ControlRegistrySnapshot? controlOverride = null,
            long? controlVersionOverride = null,
            TableControlEvent? controlChange = null,
            long? timingVersionOverride = null,
            IReadOnlyList<TableEventRecord>? extraHistory = null,
            TableTimingCheckpoint? timingOverride = null,
            CheatState? cheatOverride = null)
        {
            TableDurableContext durable = _durable!;
            ControlRegistrySnapshot control = controlOverride ?? _control.Capture();
            long controlVersion = controlVersionOverride ?? _controlVersion;
            ulong utcNow = NowUtc();
            TableTimingCheckpoint timing;
            if (timingOverride != null)
            {
                timing = timingOverride;
            }
            else if (terminal)
            {
                timing = new TableTimingCheckpoint(
                    round.RoundId,
                    _decisionClock.RemainingReserves(),
                    Array.Empty<TableRecoveredDecision>(),
                    utcNow,
                    now,
                    durable.BootId);
            }
            else
            {
                var pending = new List<PendingDecision>(4);
                for (int seat = 0; seat < 4; seat++)
                {
                    if (_pending.TryGetValue(seat, out PendingDecision? value))
                    {
                        long epoch = controlChange != null &&
                                     controlChange.Seat == seat &&
                                     controlChange.NewEpoch.HasValue
                            ? controlChange.NewEpoch.Value
                            : value.ControlEpoch;
                        pending.Add(new PendingDecision(
                            value.Key,
                            epoch,
                            value.Candidates,
                            value.DrawnTile));
                    }
                }

                timing = TableCheckpointFactory.CaptureTiming(
                    _decisionClock,
                    pending,
                    now,
                    utcNow,
                    durable.BootId);
            }

            var history = new List<TableEventRecord>(_committedEventLog);
            var commandBindings = new List<TableCommandBinding>(_commandBindings);
            if (extraHistory != null)
            {
                for (int i = 0; i < extraHistory.Count; i++)
                {
                    history.Add(extraHistory[i]);
                }

                IReadOnlyList<TableCommandBinding> extraBindings =
                    extraHistory.Count == 0
                        ? Array.Empty<TableCommandBinding>()
                        : new[] { new TableCommandBinding(
                            stateVersion.Value,
                            extraHistory.Count,
                            round.SourceFingerprint) };
                for (int i = 0; i < extraBindings.Count; i++)
                {
                    commandBindings.Add(extraBindings[i]);
                }
            }

            CheatState? cheatState = cheatOverride ?? _cheat ?? durable.CheatStateProvider?.Invoke();
            CheatLedger? cheat = cheatState?.Ledger ?? durable.CheatLedgerProvider?.Invoke();
            TableCheatLedgerCheckpoint? cheatCheckpoint = cheatState != null
                ? TableCheatLedgerCheckpoint.FromCheatState(cheatState)
                : cheat == null
                    ? null
                    : TableCheatLedgerCheckpoint.FromLedger(cheat);
            TableProjectionContextData? projectionFacts = CaptureProjectionFacts(
                round,
                stateVersion,
                lastEventSeq,
                extraHistory,
                now,
                utcNow);
            byte[]? factsPayload = projectionFacts == null
                ? null
                : TableProjectionFactsCodec.Encode(projectionFacts);
            return TableCheckpointFactory.Create(
                _options.RoomId,
                round,
                stateVersion,
                lastEventSeq,
                new TableControlCheckpoint(control, controlVersion),
                timing,
                durable.RulesetVersion,
                durable.BootId,
                utcNow,
                cheatCheckpoint,
                factsPayload,
                timingVersionOverride ?? _timingVersion + 1L,
                now,
                history,
                commandBindings);
        }

        private static ulong ReceiptExpiry(ulong createdUtcMs, TableDurableContext durable)
        {
            double retention = durable.ReceiptRetention.TotalMilliseconds;
            if (retention < 1)
            {
                retention = 1;
            }

            ulong amount = retention >= ulong.MaxValue ? ulong.MaxValue : (ulong)retention;
            return createdUtcMs > ulong.MaxValue - amount ? ulong.MaxValue : createdUtcMs + amount;
        }

        private ulong NowUtc()
        {
            long value = _clockPort.UtcNowMs;
            return value < 0 ? 0UL : (ulong)value;
        }

        private void EnterLeaseLost(string detail)
        {
            if (_lifecycle == TableActorLifecycle.Closed || _lifecycle == TableActorLifecycle.Faulted)
            {
                return;
            }

            _lifecycle = TableActorLifecycle.LeaseLost;
            _lifecycleReason = detail ?? "lease lost";
            _pending.Clear();
            _decisionClock.Reset();
            CloseAllStreams(TableStreamTerminationKind.LeaseLost, TableRuntimeReasonCode.LeaseLost, _lifecycleReason);
            Publish(new TablePublication(
                TablePublicationKind.LeaseLost,
                BuildSnapshot(),
                Array.Empty<TableEventRecord>(),
                null));
        }

        private void EnterFaulted(string reasonCode, string detail)
        {
            if (_lifecycle == TableActorLifecycle.Closed || _lifecycle == TableActorLifecycle.Faulted)
            {
                return;
            }

            _lifecycle = TableActorLifecycle.Faulted;
            _lifecycleReason = reasonCode + ": " + (detail ?? string.Empty);
            _pending.Clear();
            _decisionClock.Reset();
            CloseAllStreams(TableStreamTerminationKind.ActorFaulted, reasonCode, _lifecycleReason);
            Publish(new TablePublication(
                TablePublicationKind.LeaseLost,
                BuildSnapshot(),
                Array.Empty<TableEventRecord>(),
                null));
        }

        private async ValueTask<TableOperationResult> ProcessCheckpointAsync()
        {
            TableOperationResult? guard = GuardServing();
            if (guard != null)
            {
                return guard;
            }

            if (!TryReadClock(out long now, out _, out string clockFailure))
            {
                return TableOperationResult.Failed(_stateVersion, clockFailure, "The clock is not safe for a checkpoint.");
            }

            if (_durable == null)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.DurableNotConfigured,
                    "No durable context is wired into this actor.");
            }

            TableCheckpoint checkpoint;
            try
            {
                checkpoint = BuildCheckpoint(_round, _stateVersion, _lastEventSeq, _round.IsFinished, now);
            }
            catch (Exception exception)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.CheckpointInvalid,
                    exception.GetType().Name + ": " + exception.Message);
            }
            TableDurableCommitResult saved = await _durable.Store.SaveCheckpointAsync(
                checkpoint,
                new TableCheckpointFence(
                    _options.RoomId,
                    _durable.OwnerInstance,
                    _ownerEpoch,
                    _stateVersion,
                    _controlVersion,
                    _timingVersion),
                CancellationToken.None).ConfigureAwait(false);
            switch (saved.Disposition)
            {
                case TableCommitDisposition.Committed:
                    _timingVersion = checkpoint.TimingVersion;
                    return TableOperationResult.Configured(
                        _stateVersion,
                        "Checkpoint written at version " + _stateVersion + ".");
                case TableCommitDisposition.StaleOwner:
                    EnterLeaseLost(saved.Detail);
                    return TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.StaleOwnerEpoch,
                        saved.Detail);
                case TableCommitDisposition.StaleState:
                    EnterFaulted(TableRuntimeReasonCode.StaleStateVersion, saved.Detail);
                    return TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.StaleStateVersion,
                        saved.Detail);
                default:
                    if (string.Equals(saved.ReasonCode, TableRuntimeReasonCode.StaleControlVersion, StringComparison.Ordinal))
                    {
                        EnterFaulted(saved.ReasonCode, saved.Detail);
                    }

                    return TableOperationResult.Failed(
                        _stateVersion,
                        string.IsNullOrEmpty(saved.ReasonCode)
                            ? TableRuntimeReasonCode.CheckpointInvalid
                            : saved.ReasonCode,
                        saved.Detail);
            }
        }

        private async ValueTask<TableLeaseRenewResult> ProcessLeaseRenewAsync()
        {
            if (_durable == null)
            {
                return TableLeaseRenewResult.Failed("No durable context is wired into this actor.");
            }

            if (_lifecycle == TableActorLifecycle.Closed || _lifecycle == TableActorLifecycle.Faulted)
            {
                return TableLeaseRenewResult.Failed("The actor is not serving.");
            }

            TableLeaseRenewResult result = await _durable.Lease
                .RenewAsync(
                    _options.RoomId,
                    _durable.OwnerInstance,
                    _ownerEpoch,
                    _durable.LeaseTtl,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!result.Renewed)
            {
                EnterLeaseLost(result.Detail);
            }

            return result;
        }

        private async ValueTask<TableRecoveryResult> ProcessDurableRecoveryAsync()
        {
            if (_durable == null || _recoveryService == null)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.DurableNotConfigured,
                    "No durable context is wired into this actor.");
            }

            if (_lifecycle == TableActorLifecycle.Closed)
            {
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.ActorClosed,
                    "The actor is closed.");
            }

            if (_lifecycle == TableActorLifecycle.Faulted || _lifecycle == TableActorLifecycle.LeaseLost)
            {
                // Explicit cold recovery is the only path allowed to leave these states.
                _lifecycle = TableActorLifecycle.Serving;
                _lifecycleReason = string.Empty;
            }

            TableOperationResult began = ProcessBeginRecovery("durable cold recovery");
            if (began.Status != TableOperationStatus.Configured)
            {
                return TableRecoveryResult.Failed(
                    began.ReasonCode,
                    "Recovery could not enter RECOVERING: " + began.Detail);
            }

            var options = new TableRecoveryOptions(
                _durable.OwnerInstance,
                _durable.BootId,
                _durable.RulesetVersion,
                _durable.LeaseTtl,
                matchId: _durable.MatchId,
                requireCommittedFacts: _durable.RequireCommittedFacts,
                cheatEconomy: _cheatEconomy,
                sleeveBloomConfig: _sleeveConfig);
            TableRecoveryResult recovered = await _recoveryService
                .RecoverAsync(_options.RoomId, options, CancellationToken.None)
                .ConfigureAwait(false);
            if (!recovered.Recovered || recovered.Baseline == null)
            {
                EnterFaulted(recovered.ReasonCode, recovered.Detail);
                return recovered;
            }

            TableLeaseSnapshot? finalLease = await _durable.Lease
                .GetAsync(_options.RoomId, CancellationToken.None)
                .ConfigureAwait(false);
            TableStateRow? finalState = await _durable.Store
                .LoadStateAsync(_options.RoomId, CancellationToken.None)
                .ConfigureAwait(false);
            ulong finalUtc = NowUtc();
            if (finalLease == null ||
                !string.Equals(finalLease.OwnerInstance, _durable.OwnerInstance, StringComparison.Ordinal) ||
                finalLease.OwnerEpoch != recovered.Baseline.OwnerEpoch ||
                finalLease.IsExpiredAt(finalUtc) ||
                finalState == null ||
                finalState.StateVersion != recovered.Baseline.StateVersion ||
                finalState.LastEventSeq != recovered.Baseline.LastEventSeq ||
                finalState.TimingVersion != recovered.Baseline.TimingVersion ||
                finalState.CheatBound != recovered.Baseline.CheatBound ||
                finalState.CheatVersion != recovered.Baseline.CheatVersion ||
                finalState.CheatFingerprint != recovered.Baseline.CheatFingerprint)
            {
                EnterFaulted(
                    TableRuntimeReasonCode.RecoveryStateChanged,
                    "Lease/state changed between verification and install.");
                return TableRecoveryResult.Failed(
                    TableRuntimeReasonCode.RecoveryStateChanged,
                    "Lease/state changed between verification and install.");
            }

            TableOperationResult installed = await ProcessCompleteRecoveryAsync(recovered.Baseline)
                .ConfigureAwait(false);
            if (installed.Status != TableOperationStatus.Configured)
            {
                EnterFaulted(installed.ReasonCode, installed.Detail);
                return TableRecoveryResult.Failed(
                    installed.ReasonCode,
                    "The verified recovery baseline could not be installed: " + installed.Detail);
            }

            return recovered;
        }

        private async ValueTask<TableOperationResult?> RevalidateRecoveryInstallAsync(
            TableRecoveryBaseline baseline)
        {
            if (_durable == null)
            {
                return null;
            }

            TableLeaseSnapshot? lease = await _durable.Lease
                .GetAsync(_options.RoomId, CancellationToken.None)
                .ConfigureAwait(false);
            TableStateRow? state = await _durable.Store
                .LoadStateAsync(_options.RoomId, CancellationToken.None)
                .ConfigureAwait(false);
            TableCheckpoint? checkpoint = await _durable.Store
                .LoadCheckpointAsync(_options.RoomId, CancellationToken.None)
                .ConfigureAwait(false);
            TableTimingJournalRecord? timing = await _durable.Store
                .LoadTimingAsync(_options.RoomId, CancellationToken.None)
                .ConfigureAwait(false);
            ulong nowUtc = NowUtc();
            if (lease == null ||
                !string.Equals(lease.OwnerInstance, _durable.OwnerInstance, StringComparison.Ordinal) ||
                lease.OwnerEpoch != baseline.OwnerEpoch ||
                lease.IsExpiredAt(nowUtc))
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.StaleOwnerEpoch,
                    "The lease expired or moved during host installation.");
            }

            if (state == null ||
                state.StateVersion != baseline.StateVersion ||
                state.LastEventSeq != baseline.LastEventSeq ||
                state.StateFingerprint != baseline.Round.SourceFingerprint ||
                state.ControlVersion != (baseline.Control?.ControlVersion ?? state.ControlVersion) ||
                state.TimingVersion != baseline.TimingVersion ||
                state.CheatBound != baseline.CheatBound ||
                state.CheatVersion != baseline.CheatVersion ||
                state.CheatFingerprint != baseline.CheatFingerprint)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.RecoveryStateChanged,
                    "table_state changed during host installation.");
            }

            if (checkpoint == null ||
                checkpoint.StateVersion.CompareTo(state.StateVersion) > 0 ||
                checkpoint.LastEventSeq.CompareTo(state.LastEventSeq) > 0 ||
                checkpoint.ControlVersion > state.ControlVersion ||
                checkpoint.TimingVersion > state.TimingVersion)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.RecoveryStateChanged,
                    "checkpoint changed during host installation.");
            }

            if (timing == null ||
                timing.TimingVersion != baseline.TimingVersion ||
                timing.StateVersion != state.StateVersion ||
                timing.ControlVersion != state.ControlVersion)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.RecoveryTimingInvalid,
                    "timing journal changed during host installation.");
            }

            return null;
        }

        private async ValueTask<TableOperationResult> ProcessOpenDecisionAsync(PendingDecision decision)
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

            DecisionClock candidate = _decisionClock.Clone();
            candidate.BeginRound(_round.RoundId, now);
            DecisionTiming timing;
            try
            {
                timing = candidate.Open(decision.Key, now);
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

            if (_durable != null)
            {
                var nextPending = new List<PendingDecision>(5);
                for (int seat = 0; seat < 4; seat++)
                {
                    if (_pending.TryGetValue(seat, out PendingDecision? existing))
                    {
                        nextPending.Add(existing);
                    }
                }

                nextPending.Add(decision);
                long nextTimingVersion = checked(_timingVersion + 1L);
                TableTimingCheckpoint timingSnapshot = TableCheckpointFactory.CaptureTiming(
                    candidate,
                    nextPending,
                    now,
                    NowUtc(),
                    _durable.BootId);
                TableDurableCommitResult appended = await _durable.Store.AppendTimingAsync(
                    new TableTimingAppendRequest(
                        _options.RoomId,
                        _durable.OwnerInstance,
                        _ownerEpoch,
                        _stateVersion,
                        _controlVersion,
                        _timingVersion,
                        timingSnapshot),
                    CancellationToken.None).ConfigureAwait(false);
                switch (appended.Disposition)
                {
                    case TableCommitDisposition.Committed:
                        break;
                    case TableCommitDisposition.StaleOwner:
                        EnterLeaseLost(appended.Detail);
                        return TableOperationResult.Failed(
                            _stateVersion,
                            TableRuntimeReasonCode.StaleOwnerEpoch,
                            appended.Detail);
                    case TableCommitDisposition.StaleState:
                        EnterFaulted(TableRuntimeReasonCode.StaleStateVersion, appended.Detail);
                        return TableOperationResult.Failed(
                            _stateVersion,
                            TableRuntimeReasonCode.StaleStateVersion,
                            appended.Detail);
                    default:
                        return TableOperationResult.Failed(
                            _stateVersion,
                            string.IsNullOrEmpty(appended.ReasonCode)
                                ? TableRuntimeReasonCode.CommitFailed
                                : appended.ReasonCode,
                            appended.Detail);
                }

                _timingVersion = nextTimingVersion;
            }

            _decisionClock = candidate;
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
            // Fence every existing outbox to the new generation before any further work can be
            // processed. The in-flight sink call (if any) cannot be recalled, but queued old
            // update frames are dropped and can never be dispatched after this point.
            FenceAndPurgeStreamsForRecovery(_recoveryGeneration);
            // Weakly-held observation generations from earlier recovery generations remain
            // collectible; IsCurrentObservation checks the current generation on submission.
            // Keep the reserve ledger and active round until the baseline proves whether they
            // are still valid. Open timings are removed; tick/AI are refused while recovering.
            _decisionClock.ClearTimings();
            return TableOperationResult.Configured(
                _stateVersion,
                "Recovery started; new state-changing and control work is refused.");
        }

        private async ValueTask<TableOperationResult> ProcessCompleteRecoveryAsync(TableRecoveryBaseline baseline)
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
                        Math.Max(0L, recovered.RemainingBaseMillis - recovered.RecoveryGraceMillis),
                        recovered.RemainingReserveMillis,
                        recovered.OverdueMillis,
                        recovered.RecoveryGraceMillis,
                        recovered.RecoveryGraceApplied);
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

            if (baseline.StrictHistory &&
                (baseline.CommittedEvents.Count == 0
                    ? baseline.LastEventSeq.Value > 0
                    : baseline.CommittedEvents[baseline.CommittedEvents.Count - 1].Seq != baseline.LastEventSeq))
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.RecoveryEventGap,
                    "Strict recovery history is empty or does not end at the baseline sequence.");
            }

            if ((baseline.Cheat != null || baseline.ProjectionFacts != null) &&
                _durable?.RecoveryStateSink == null)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.RecoveryStateSinkRequired,
                    "Recovery carries cheat/private/facts state but no install sink is configured.");
            }

            if (_cheatEconomy != null && baseline.Cheat == null)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.CheatNotConfigured,
                    "Cheat execution is configured but the recovery baseline carries no cheat aggregate.");
            }

            if (_durable?.RecoveryStateSink != null)
            {
                try
                {
                    _durable.RecoveryStateSink.Install(new TableRecoveryAuxState(
                        _options.RoomId,
                        baseline.StateVersion,
                        baseline.TimingVersion,
                        baseline.Control?.ControlVersion ?? _controlVersion,
                        baseline.Cheat,
                        baseline.ProjectionFacts,
                        baseline.CommittedEvents));
                }
                catch (Exception exception)
                {
                    return TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryStateSinkFailed,
                        exception.GetType().Name + ": " + exception.Message);
                }
            }

            if (baseline.CommittedEvents.Count > 0 &&
                baseline.CommittedEvents[baseline.CommittedEvents.Count - 1].Seq != baseline.LastEventSeq)
            {
                return TableOperationResult.Failed(
                    _stateVersion,
                    TableRuntimeReasonCode.RecoveryEventGap,
                    "Recovery history does not end at the baseline event sequence.");
            }

            // The host install may be slow and may advance clocks or external state. Re-confirm
            // the complete fence after Install and immediately before the first successful
            // publication/Serving transition. A failure leaves the actor non-Serving.
            TableOperationResult? finalFence = await RevalidateRecoveryInstallAsync(baseline).ConfigureAwait(false);
            if (finalFence != null)
            {
                EnterFaulted(finalFence.ReasonCode, finalFence.Detail);
                return finalFence;
            }

            _round = baseline.Round;
            if (baseline.Cheat != null)
            {
                _cheat = baseline.Cheat;
            }

            _stateVersion = baseline.StateVersion;
            _lastEventSeq = baseline.LastEventSeq;
            _ownerEpoch = baseline.OwnerEpoch;
            _control = candidateControl;
            _controlVersion = candidateControlVersion;
            _decisionClock = candidateClock;
            _timingVersion = baseline.TimingVersion;
            _pending.Clear();
            foreach (KeyValuePair<int, PendingDecision> entry in candidatePending)
            {
                _pending[entry.Key] = entry.Value;
            }

            _recentEvents.Clear();
            _committedEventLog.Clear();
            for (int i = 0; i < baseline.CommittedEvents.Count; i++)
            {
                _committedEventLog.Add(baseline.CommittedEvents[i]);
            }

            _commandBindings.Clear();
            for (int i = 0; i < baseline.CommandBindings.Count; i++)
            {
                _commandBindings.Add(baseline.CommandBindings[i]);
            }

            if (_durable != null)
            {
                try
                {
                    IReadOnlyList<TableCommandJournalRecord> allCommands = await _durable.Store
                        .LoadCommandsAfterAsync(_options.RoomId, new StateVersion(0), CancellationToken.None)
                        .ConfigureAwait(false);
                    for (int i = 0; i < allCommands.Count; i++)
                    {
                        TableCommandJournalRecord journal = allCommands[i];
                        if (journal.SchemaVersion != 2)
                        {
                            continue;
                        }

                        RoundCheatJournal cheatJournal = TableCheatCommandCodec.Decode(journal.CommandPayload);
                        string key = journal.RequestId.Value;
                        if (!_cheatRequestBindings.ContainsKey(key))
                        {
                            _cheatRequestBindings[key] = new CheatRequestBinding(
                                journal.ScopeId,
                                journal.Api,
                                journal.PayloadSha256,
                                null);
                        }
                    }
                }
                catch (Exception exception)
                {
                    EnterFaulted(
                        TableRuntimeReasonCode.RecoveryStateChanged,
                        "Cheat receipt identity reconstruction failed: " +
                        exception.GetType().Name + ": " + exception.Message);
                    return TableOperationResult.Failed(
                        _stateVersion,
                        TableRuntimeReasonCode.RecoveryStateChanged,
                        "Cheat receipt identity reconstruction failed.");
                }
            }

            int recentWindow = _options.SnapshotEventWindow;
            if (recentWindow > 0 && _committedEventLog.Count > 0)
            {
                for (int i = _committedEventLog.Count - 1;
                     i >= 0 && _recentEvents.Count < recentWindow;
                     i--)
                {
                    if (_committedEventLog[i].Cheat == null)
                    {
                        _recentEvents.Insert(0, _committedEventLog[i]);
                    }
                }
            }
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
            TableControlResult result = await CommitControlChangeAsync(candidate.Capture(), change).ConfigureAwait(false);

            return result;
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
            TableControlResult result = await CommitControlChangeAsync(candidate.Capture(), change).ConfigureAwait(false);

            return result;
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
            if (_durable != null)
            {
                if (!TryReadClock(out long now, out _, out string clockFailure))
                {
                    return ControlFailed(clockFailure, "The clock is not safe for a durable control change.");
                }

                TableCheckpoint checkpoint;
                try
                {
                    checkpoint = BuildCheckpoint(
                        _round,
                        _stateVersion,
                        _lastEventSeq,
                        _round.IsFinished,
                        now,
                        next,
                        change.ControlVersion,
                        change);
                }
                catch (Exception exception)
                {
                    return ControlFailed(
                        TableRuntimeReasonCode.CheckpointInvalid,
                        exception.GetType().Name + ": " + exception.Message);
                }

                var durableRequest = new TableControlCommitRequest(
                    _options.RoomId,
                    _ownerEpoch,
                    _controlVersion,
                    change.ControlVersion,
                    _stateVersion,
                    _round.RoundId,
                    change,
                    _durable.OwnerInstance);
                TableDurableCommitResult commit = await _durable.Store
                    .CommitControlAsync(durableRequest, checkpoint, CancellationToken.None)
                    .ConfigureAwait(false);
                switch (commit.Disposition)
                {
                    case TableCommitDisposition.Committed:
                        _timingVersion = checkpoint.TimingVersion;
                        break;
                    case TableCommitDisposition.StaleOwner:
                        EnterLeaseLost(commit.Detail);
                        return ControlFailed(TableRuntimeReasonCode.StaleOwnerEpoch, commit.Detail);
                    case TableCommitDisposition.StaleState:
                        EnterFaulted(TableRuntimeReasonCode.StaleStateVersion, commit.Detail);
                        return ControlFailed(TableRuntimeReasonCode.StaleStateVersion, commit.Detail);
                    case TableCommitDisposition.OutcomeUnknown:
                        EnterFaulted(TableRuntimeReasonCode.CommitOutcomeUnknown, commit.Detail);
                        return ControlFailed(TableRuntimeReasonCode.CommitOutcomeUnknown, commit.Detail);
                    default:
                        return ControlFailed(
                            string.IsNullOrEmpty(commit.ReasonCode)
                                ? TableRuntimeReasonCode.ControlCommitFailed
                                : commit.ReasonCode,
                            commit.Detail);
                }
            }

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
                if (_recoveryGeneration > 0)
                {
                    _aiObservationGenerations.Add(observation, new ObservationGeneration(_recoveryGeneration));
                }
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
            IReadOnlyList<RoundEvent> domainEvents,
            ulong sourceFingerprint)
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
                    domainEvent.Detail,
                    1,
                    TableEventFactCodec.Encode(commandVersion, i, sourceFingerprint)));
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

        private TableStreamControlResult StreamAdmissionFailure(TableMailboxEnqueueStatus enqueue)
        {
            // The caller thread must not read pump-owned state. 0 is the explicit "not
            // evaluated" sentinel for an operation that never entered the mailbox.
            var notEvaluated = new StateVersion(0);
            return enqueue == TableMailboxEnqueueStatus.Full
                ? TableStreamControlResult.Failure(
                    TableStreamControlStatus.MailboxFull,
                    ProtocolErrorCode.ServerBusy,
                    "The table mailbox is full; the stream operation was not admitted.",
                    null,
                    notEvaluated,
                    0L)
                : TableStreamControlResult.Failure(
                    TableStreamControlStatus.Closed,
                    ProtocolErrorCode.LocalClosed,
                    "The table actor is closing/closed; the stream operation was not admitted.",
                    null,
                    notEvaluated,
                    0L);
        }

        private TableStreamControlResult? GuardStreamControl()
        {
            switch (_lifecycle)
            {
                case TableActorLifecycle.Serving:
                    return null;
                case TableActorLifecycle.Recovering:
                    return TableStreamControlResult.Failure(
                        TableStreamControlStatus.Rejected,
                        ProtocolErrorCode.ServerBusy,
                        "The table is recovering; streams are gated until the full baseline is installed.",
                        null,
                        _stateVersion,
                        _controlVersion);
                case TableActorLifecycle.LeaseLost:
                    return TableStreamControlResult.Failure(
                        TableStreamControlStatus.Closed,
                        ProtocolErrorCode.LeaseLost,
                        "The table lease was lost; the stream is closed.",
                        null,
                        _stateVersion,
                        _controlVersion);
                case TableActorLifecycle.Closed:
                    return TableStreamControlResult.Failure(
                        TableStreamControlStatus.Closed,
                        ProtocolErrorCode.LocalClosed,
                        "The table actor is closed.",
                        null,
                        _stateVersion,
                        _controlVersion);
                default:
                    return TableStreamControlResult.Failure(
                        TableStreamControlStatus.Failed,
                        ProtocolErrorCode.InternalError,
                        _lifecycleReason,
                        null,
                        _stateVersion,
                        _controlVersion);
            }
        }

        private TableStreamControlResult ProcessSubscribeStream(TableStreamSubscribeRequest request)
        {
            TableStreamControlResult? guard = GuardStreamControl();
            if (guard != null)
            {
                return guard;
            }

            if (request.OwnerEpoch != _ownerEpoch)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    TableRuntimeReasonCode.StaleOwnerEpoch,
                    "Stream owner epoch " + request.OwnerEpoch + " is not the table lease epoch " + _ownerEpoch + ".",
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            if (request.RecoveryGeneration != _recoveryGeneration)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    TableRuntimeReasonCode.StaleRecoveryGeneration,
                    "Stream recovery generation " + request.RecoveryGeneration +
                    " is not current generation " + _recoveryGeneration + ".",
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            if (request.ExpectedRoundId.HasValue &&
                !string.Equals(request.ExpectedRoundId.Value.Value, _round.RoundId, StringComparison.Ordinal))
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    TableRuntimeReasonCode.RoundMismatch,
                    "Stream expected round '" + request.ExpectedRoundId.Value.Value +
                    "' but the table is in '" + _round.RoundId + "'.",
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            // A failed/closed consumer must not hold capacity until some unrelated publication
            // happens. Reap closed registrations inside this actor turn before capacity checks.
            RemoveFaultedStreams();

            TableStreamIdentity identity = request.Identity;
            ControlEpoch? boundControl = null;
            if (identity.Audience.IsSeat)
            {
                long controlEpoch = _control.GetEpoch(identity.Audience.Seat);
                if (request.ExpectedControlEpoch.HasValue)
                {
                    if (!TryToDomainEpoch(request.ExpectedControlEpoch.Value, out long expected) ||
                        expected != controlEpoch)
                    {
                        return TableStreamControlResult.Failure(
                            TableStreamControlStatus.Rejected,
                            TableRuntimeReasonCode.StaleControlEpoch,
                            "Stream controller epoch is not current for seat " + identity.Audience.Seat + ".",
                            null,
                            _stateVersion,
                            _controlVersion);
                    }
                }

                boundControl = new ControlEpoch(unchecked((ulong)controlEpoch));
            }
            else if (request.ExpectedControlEpoch.HasValue)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.MalformedMessage,
                    "A spectator stream cannot claim a seat controller epoch.",
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            _streamIdentities.TryGetValue(identity.Key, out TableStreamIdentityState? state);
            bool isNewIdentity = state == null;
            state ??= new TableStreamIdentityState(identity);
            if (isNewIdentity && _streams.Count >= _streamOptions.MaxStreams)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.ServerFull,
                    "The table stream limit of " + _streamOptions.MaxStreams + " was reached.",
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            ulong binding = identity.BindingEpoch.Value;
            ulong sync = identity.SyncEpoch.Value;
            if (binding < state.HighestBindingEpoch)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.StaleStreamVersion,
                    "Binding epoch " + binding + " is older than the accepted binding epoch " +
                    state.HighestBindingEpoch + ".",
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            if (sync < state.HighestSyncEpoch)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.StaleStreamVersion,
                    "Sync epoch " + sync + " is older than the accepted sync epoch " +
                    state.HighestSyncEpoch + ".",
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            TableStreamRegistration? previous = null;
            if (state.CurrentHandleToken.HasValue)
            {
                _streams.TryGetValue(state.CurrentHandleToken.Value, out previous);
                if (previous != null)
                {
                    if (binding < previous.Identity.BindingEpoch.Value)
                    {
                        return TableStreamControlResult.Failure(
                            TableStreamControlStatus.Rejected,
                            ProtocolErrorCode.StaleStreamVersion,
                            "Binding epoch " + binding + " is older than the active binding epoch " +
                            previous.Identity.BindingEpoch.Value + ".",
                            null,
                            _stateVersion,
                            _controlVersion);
                    }

                    if (binding == previous.Identity.BindingEpoch.Value &&
                        sync == previous.Identity.SyncEpoch.Value)
                    {
                        return TableStreamControlResult.Failure(
                            TableStreamControlStatus.Rejected,
                            ProtocolErrorCode.AlreadyBound,
                            "This stream identity already has an active registration.",
                            null,
                            _stateVersion,
                            _controlVersion);
                    }

                    if (sync < previous.Identity.SyncEpoch.Value)
                    {
                        return TableStreamControlResult.Failure(
                            TableStreamControlStatus.Rejected,
                            ProtocolErrorCode.StaleStreamVersion,
                            "Sync epoch " + sync + " is older than the active sync epoch " +
                            previous.Identity.SyncEpoch.Value + ".",
                            null,
                            _stateVersion,
                            _controlVersion);
                    }
                }
            }

            if (previous == null &&
                state.HighestBindingEpoch != 0 &&
                binding == state.HighestBindingEpoch &&
                sync == state.HighestSyncEpoch)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.StaleStreamVersion,
                    "Binding/sync epoch " + binding + "/" + sync +
                    " was already consumed by this identity; a new host-issued generation is required.",
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            if (previous == null && _streams.Count >= _streamOptions.MaxStreams)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.ServerFull,
                    "The table stream limit of " + _streamOptions.MaxStreams + " was reached.",
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            bool resetProjection = sync > state.HighestSyncEpoch;
            ProjectionSeq baseSeq = resetProjection ? new ProjectionSeq(0) : state.ProjectionSeq;
            ProjectionSeq nextSeq;
            try
            {
                nextSeq = baseSeq.Next();
            }
            catch (OverflowException exception)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Failed,
                    ProtocolErrorCode.InternalError,
                    "Projection sequence overflow: " + exception.Message,
                    null,
                    _stateVersion,
                    _controlVersion);
            }

            TableStreamHandle? handle = null;
            TableStreamChannel? channel = null;
            try
            {
                handle = CreateStreamHandle(identity);
                StreamProjectionCapture capture = CaptureStreamProjection();
                var cursor = new StreamCursor(new StreamVersion(_stateVersion.Value), nextSeq);
                TableStreamFrame frame = BuildStreamFrame(handle, capture, true, StreamCursor.Empty, cursor, null);
                channel = CreateStreamChannel(handle, request.Sink);
                TableStreamEnqueueStatus enqueue = channel.TryEnqueue(frame);
                if (enqueue != TableStreamEnqueueStatus.Enqueued)
                {
                    return TableStreamControlResult.Failure(
                        TableStreamControlStatus.Overflow,
                        ProtocolErrorCode.SlowConsumer,
                        "The stream baseline could not be queued without exceeding its bound.",
                        null,
                        _stateVersion,
                        _controlVersion);
                }

                if (previous != null)
                {
                    RemoveRegistration(previous);
                    previous.Channel.Close(
                        TableStreamTerminationKind.Displaced,
                        ProtocolErrorCode.DisplacedByNewConnection,
                        "A newer trusted binding replaced this stream.");
                }

                state.HighestBindingEpoch = binding;
                state.HighestSyncEpoch = sync;
                state.ProjectionSeq = nextSeq;
                state.Cursor = cursor;
                state.CurrentHandleToken = handle.Token;
                _streamIdentities[identity.Key] = state;
                _streams.Add(handle.Token, new TableStreamRegistration(
                    handle,
                    state,
                    channel,
                    _ownerEpoch,
                    boundControl,
                    _recoveryGeneration,
                    _round.RoundId,
                    _controlVersion));
                return TableStreamControlResult.Subscribed(handle, cursor, _stateVersion, _controlVersion);
            }
            catch (StreamFrameException exception)
            {
                channel?.Close(exception.Kind, exception.ReasonCode, exception.Message);
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Failed,
                    exception.ReasonCode,
                    exception.Message,
                    null,
                    _stateVersion,
                    _controlVersion);
            }
            catch (Exception exception) when (
                exception is TableProjectionException ||
                exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                channel?.Close(
                    TableStreamTerminationKind.ProjectionFailed,
                    ProtocolErrorCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message);
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Failed,
                    ProtocolErrorCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message,
                    null,
                    _stateVersion,
                    _controlVersion);
            }
        }

        private TableStreamControlResult ProcessUnsubscribeStream(TableStreamHandle handle)
        {
            if (_lifecycle == TableActorLifecycle.Closed)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Closed,
                    ProtocolErrorCode.LocalClosed,
                    "The table actor is closed.",
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            if (_lifecycle == TableActorLifecycle.Faulted)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Failed,
                    ProtocolErrorCode.InternalError,
                    _lifecycleReason,
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            TableStreamRegistration? registration = FindCurrentRegistration(handle);
            if (registration == null)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.StaleStreamVersion,
                    "The stream handle is not the current registration for its identity.",
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            bool wasClosed = registration.Channel.IsClosed;
            RemoveRegistration(registration);
            registration.Channel.Close(
                TableStreamTerminationKind.Unsubscribed,
                string.Empty,
                "The trusted host unsubscribed the stream.");
            return TableStreamControlResult.Unsubscribed(
                handle,
                registration.State.Cursor,
                _stateVersion,
                _controlVersion,
                wasClosed ? "The stream had already closed." : "The stream was unsubscribed.");
        }

        private TableStreamControlResult ProcessSyncStream(TableStreamSyncRequest request)
        {
            TableStreamControlResult? guard = GuardStreamControl();
            if (guard != null)
            {
                return guard;
            }

            TableStreamHandle handle = request.Handle;
            TableStreamRegistration? registration = FindCurrentRegistration(handle);
            if (registration == null)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.StaleStreamVersion,
                    "The stream handle is not the current registration for its identity.",
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            TableStreamIdentity identity = handle.Identity;
            if (request.ExpectedSyncEpoch.HasValue &&
                request.ExpectedSyncEpoch.Value != identity.SyncEpoch)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.StaleStreamVersion,
                    "The sync epoch does not match the registered stream identity.",
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            if (request.ExpectedBindingEpoch.HasValue &&
                request.ExpectedBindingEpoch.Value != identity.BindingEpoch)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    ProtocolErrorCode.StaleStreamVersion,
                    "The binding epoch does not match the registered stream identity.",
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            if (request.ExpectedOwnerEpoch.HasValue &&
                request.ExpectedOwnerEpoch.Value != _ownerEpoch)
            {
                CloseStream(registration, TableStreamTerminationKind.StaleOwner, ProtocolErrorCode.LeaseLost,
                    "The stream belongs to an older table owner epoch.");
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Closed,
                    ProtocolErrorCode.LeaseLost,
                    "The stream belongs to an older table owner epoch.",
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            if (registration.OwnerEpoch != _ownerEpoch)
            {
                CloseStream(registration, TableStreamTerminationKind.StaleOwner, ProtocolErrorCode.LeaseLost,
                    "The stream belongs to an older table owner epoch.");
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Closed,
                    ProtocolErrorCode.LeaseLost,
                    "The stream belongs to an older table owner epoch.",
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            if (request.ExpectedRecoveryGeneration.HasValue &&
                request.ExpectedRecoveryGeneration.Value != registration.RecoveryGeneration)
            {
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Rejected,
                    TableRuntimeReasonCode.StaleRecoveryGeneration,
                    "The sync request belongs to an older recovery generation.",
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            if (registration.Channel.IsClosed)
            {
                TableStreamTermination? closed = registration.Channel.Termination;
                RemoveRegistration(registration);
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Closed,
                    closed == null || string.IsNullOrEmpty(closed.ReasonCode)
                        ? ProtocolErrorCode.SlowConsumer
                        : closed.ReasonCode,
                    closed == null || string.IsNullOrEmpty(closed.Detail)
                        ? "The stream channel is closed; a new subscription is required."
                        : closed.Detail,
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            if (identity.Audience.IsSeat)
            {
                long currentControlEpoch = _control.GetEpoch(identity.Audience.Seat);
                if (!registration.BoundControlEpoch.HasValue ||
                    registration.BoundControlEpoch.Value.Value != unchecked((ulong)currentControlEpoch))
                {
                    CloseStream(registration, TableStreamTerminationKind.StaleControlEpoch,
                        TableRuntimeReasonCode.StaleControlEpoch,
                        "The seat controller changed; the private stream must be registered again.");
                    return TableStreamControlResult.Failure(
                        TableStreamControlStatus.Closed,
                        TableRuntimeReasonCode.StaleControlEpoch,
                        "The seat controller changed; the private stream must be registered again.",
                        handle,
                        _stateVersion,
                        _controlVersion);
                }
            }

            bool roundMismatch = !string.Equals(registration.RoundId, _round.RoundId, StringComparison.Ordinal) ||
                                 (request.ExpectedRoundId.HasValue &&
                                  !string.Equals(request.ExpectedRoundId.Value.Value, _round.RoundId, StringComparison.Ordinal));
            bool cursorMismatch = request.Cursor != registration.State.Cursor ||
                                  registration.State.Cursor.StreamVersion.Value != _stateVersion.Value;
            bool recoveryMismatch = registration.RecoveryGeneration != _recoveryGeneration;
            bool controlMismatch = registration.ControlVersion != _controlVersion;
            if (!registration.NeedsSync &&
                !roundMismatch &&
                !cursorMismatch &&
                !recoveryMismatch &&
                !controlMismatch)
            {
                return TableStreamControlResult.InSync(
                    registration.Handle,
                    registration.State.Cursor,
                    _stateVersion,
                    _controlVersion);
            }

            ProjectionSeq nextSeq;
            try
            {
                nextSeq = registration.State.ProjectionSeq.Next();
            }
            catch (OverflowException exception)
            {
                CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                    ProtocolErrorCode.InternalError, "Projection sequence overflow: " + exception.Message);
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Failed,
                    ProtocolErrorCode.InternalError,
                    "Projection sequence overflow: " + exception.Message,
                    handle,
                    _stateVersion,
                    _controlVersion);
            }

            try
            {
                StreamProjectionCapture capture = CaptureStreamProjection();
                var cursor = new StreamCursor(new StreamVersion(_stateVersion.Value), nextSeq);
                TableStreamFrame frame = BuildStreamFrame(
                    registration.Handle,
                    capture,
                    true,
                    registration.State.Cursor,
                    cursor,
                    null);
                TableStreamEnqueueStatus enqueue = registration.Channel.TryEnqueue(frame);
                if (enqueue != TableStreamEnqueueStatus.Enqueued)
                {
                    RemoveRegistration(registration);
                    return TableStreamControlResult.Failure(
                        TableStreamControlStatus.Overflow,
                        ProtocolErrorCode.SlowConsumer,
                        "The full baseline could not be queued without exceeding the stream bound.",
                        registration.Handle,
                        _stateVersion,
                        _controlVersion);
                }

                registration.State.ProjectionSeq = nextSeq;
                registration.State.Cursor = cursor;
                registration.RecoveryGeneration = _recoveryGeneration;
                registration.RoundId = _round.RoundId;
                registration.ControlVersion = _controlVersion;
                registration.NeedsSync = false;
                return TableStreamControlResult.BaselineQueued(
                    registration.Handle,
                    cursor,
                    _stateVersion,
                    _controlVersion,
                    ProtocolErrorCode.StreamGap,
                    recoveryMismatch
                        ? "The recovery generation changed; a full baseline was queued."
                        : roundMismatch
                            ? "The stream is on another round; a full baseline was queued."
                            : "The stream cursor is not current; a full baseline was queued.");
            }
            catch (StreamFrameException exception)
            {
                CloseStream(registration, exception.Kind, exception.ReasonCode, exception.Message);
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Failed,
                    exception.ReasonCode,
                    exception.Message,
                    handle,
                    _stateVersion,
                    _controlVersion);
            }
            catch (Exception exception) when (
                exception is TableProjectionException ||
                exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                    ProtocolErrorCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message);
                return TableStreamControlResult.Failure(
                    TableStreamControlStatus.Failed,
                    ProtocolErrorCode.InternalError,
                    exception.GetType().Name + ": " + exception.Message,
                    handle,
                    _stateVersion,
                    _controlVersion);
            }
        }

        private TableStreamHandle CreateStreamHandle(TableStreamIdentity identity)
        {
            ulong token;
            try
            {
                token = checked(_nextStreamHandle + 1UL);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException("Stream handle overflow.", exception);
            }

            _nextStreamHandle = token;
            return new TableStreamHandle(token, _streamSource, identity);
        }

        private TableStreamChannel CreateStreamChannel(TableStreamHandle handle, ITableStreamSink sink)
        {
            var channel = new TableStreamChannel(
                handle,
                sink,
                _streamOptions.MaxQueuedFrames,
                _streamOptions.MaxQueuedBytes,
                _streamOptions.Encoder != null,
                _streamOptions.TerminationSink,
                _recoveryGeneration,
                _streamOptions.BeforeDispatchClaim,
                _streamOptions.ObservePumpExit);
            _streamOptions.ChannelCreated?.Invoke(channel);
            return channel;
        }

        private TableStreamRegistration? FindCurrentRegistration(TableStreamHandle handle)
        {
            // A token is only unique inside one actor. Reject handles minted by another actor
            // before touching the local token table; also require the exact current object so a
            // stale handle from this actor cannot alias a replacement with the same token.
            if (!handle.IsOwnedBy(_streamSource))
            {
                return null;
            }

            if (!_streams.TryGetValue(handle.Token, out TableStreamRegistration? registration))
            {
                return null;
            }

            if (!ReferenceEquals(registration.Handle, handle))
            {
                return null;
            }

            if (!registration.State.CurrentHandleToken.HasValue ||
                registration.State.CurrentHandleToken.Value != handle.Token)
            {
                return null;
            }

            return registration;
        }

        private void RemoveRegistration(TableStreamRegistration registration)
        {
            if (_streams.TryGetValue(registration.Handle.Token, out TableStreamRegistration? current) &&
                ReferenceEquals(current, registration))
            {
                _streams.Remove(registration.Handle.Token);
            }

            if (registration.State.CurrentHandleToken.HasValue &&
                registration.State.CurrentHandleToken.Value == registration.Handle.Token)
            {
                registration.State.CurrentHandleToken = null;
            }
        }

        private void CloseStream(
            TableStreamRegistration registration,
            TableStreamTerminationKind kind,
            string reasonCode,
            string detail)
        {
            RemoveRegistration(registration);
            registration.Channel.Close(kind, reasonCode, detail);
        }

        private void FenceAndPurgeStreamsForRecovery(long generation)
        {
            foreach (KeyValuePair<ulong, TableStreamRegistration> entry in _streams)
            {
                entry.Value.Channel.PurgeAndFence(generation);
            }
        }

        private void CloseAllStreams(TableStreamTerminationKind kind, string reasonCode, string detail)
        {
            if (_streams.Count == 0)
            {
                return;
            }

            var registrations = new List<TableStreamRegistration>(_streams.Values);
            _streams.Clear();
            for (int i = 0; i < registrations.Count; i++)
            {
                registrations[i].State.CurrentHandleToken = null;
                registrations[i].Channel.Close(kind, reasonCode, detail);
            }
        }

        private StreamProjectionCapture CaptureStreamProjection()
        {
            if (_projectionContextSource == null)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "No trusted projection context source is configured for this table.");
            }

            if (!TryReadClock(out long now, out long utc, out string clockFailure))
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "The projection clock is not safe: " + clockFailure);
            }

            IReadOnlyList<TableEventRecord> committedEvents = CopyCommittedEventLog();
            if (committedEvents.Count > 0 &&
                committedEvents[committedEvents.Count - 1].Seq != _lastEventSeq)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "The committed event log does not end at the installed event sequence.");
            }

            if (_round.IsFinished && _round.Events.Count > 0 && committedEvents.Count == 0)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "A terminal round has no authoritative committed event history; settlement facts cannot be fabricated.");
            }

            var request = new TableProjectionContextRequest(
                _options.RoomId,
                _round,
                _stateVersion,
                _lastEventSeq,
                _ownerEpoch,
                _controlVersion,
                _recoveryGeneration,
                now,
                utc,
                committedEvents);
            TableProjectionContextData data = _projectionContextSource.Capture(request) ??
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "The projection context source returned no data.");
            if (!string.Equals(data.RoundId, _round.RoundId, StringComparison.Ordinal))
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.RoundIdMismatch,
                    "Projection context round '" + data.RoundId + "' is not the committed round '" + _round.RoundId + "'.");
            }

            if (data.StateVersion != _stateVersion)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.StaleStateVersion,
                    "Projection context version " + data.StateVersion + " is not the committed version " + _stateVersion + ".");
            }

            if (_cheat != null)
            {
                data = OverlayCheat(data, _cheat);
            }

            var eventInputs = new List<TableEventProjectionInput>(Math.Min(committedEvents.Count, 4096));
            int start = Math.Max(0, committedEvents.Count - 4096);
            for (int i = start; i < committedEvents.Count; i++)
            {
                TableEventProjectionInput? input = TableEventProjection.FromRoundEvent(committedEvents[i]);
                if (input != null)
                {
                    eventInputs.Add(input);
                }
            }

            IReadOnlyDictionary<ulong, TableProjectionEventFact> factsBySeq = ValidateEventFacts(
                MergeCheatFacts(data.EventFacts, committedEvents),
                eventInputs,
                committedEvents);

            return new StreamProjectionCapture(
                data,
                BuildStreamClocks(now, utc),
                BuildStreamDecisions(now),
                eventInputs,
                factsBySeq,
                now);
        }

        private IReadOnlyList<TableEventRecord> CopyCommittedEventLog()
        {
            var copy = new TableEventRecord[_committedEventLog.Count];
            for (int i = 0; i < _committedEventLog.Count; i++)
            {
                copy[i] = _committedEventLog[i];
            }

            return copy;
        }

        /// <summary>
        /// Binds every explicit event fact to the same committed capture. A fact for an event
        /// that is absent from this capture, a duplicate sequence, or a type/version/ordinal
        /// mismatch is a projection rejection. Missing facts are left to the Contracts mapper,
        /// which fails closed only when the event actually needs that fact.
        /// </summary>
        private static IReadOnlyDictionary<ulong, TableProjectionEventFact> ValidateEventFacts(
            IReadOnlyList<TableProjectionEventFact> facts,
            IReadOnlyList<TableEventProjectionInput> eventInputs,
            IReadOnlyList<TableEventRecord> committedEvents)
        {
            var result = new Dictionary<ulong, TableProjectionEventFact>(facts.Count);
            if (facts.Count == 0)
            {
                return result;
            }

            var inputs = new Dictionary<ulong, TableEventProjectionInput>(eventInputs.Count);
            for (int i = 0; i < eventInputs.Count; i++)
            {
                inputs[eventInputs[i].EventSeq.Value] = eventInputs[i];
            }

            for (int i = 0; i < facts.Count; i++)
            {
                TableProjectionEventFact fact = facts[i];
                if (!inputs.TryGetValue(fact.EventSeq.Value, out TableEventProjectionInput? input))
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Event fact " + fact.EventSeq + " does not belong to the committed event capture.");
                }

                if (input.Type != fact.Type ||
                    input.CommandVersion != fact.CommandVersion ||
                    input.EventOrdinal != fact.EventOrdinal)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventVersionMismatch,
                        "Event fact " + fact.EventSeq + " does not match the committed event type/version/ordinal.");
                }

                if (result.ContainsKey(fact.EventSeq.Value))
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventOrderMismatch,
                        "Event facts repeat event_seq " + fact.EventSeq.Value + ".");
                }

                result.Add(fact.EventSeq.Value, fact);
            }

            // The provided event window is the only admissible provenance domain; a fact whose
            // sequence is outside it has already been rejected above.
            if (committedEvents.Count == 0 && result.Count > 0)
            {
                throw new TableProjectionException(
                    TableProjectionErrorCode.InvalidContext,
                    "Event facts were supplied without committed events.");
            }

            return result;
        }

        /// <summary>
        /// Cheat events are actual committed facts, not host-supplied guesses: their protocol
        /// 20/21 facts are derived from the persisted typed event record and merged with the
        /// host's round facts. This keeps owner offerId/keep/action/tile/target exact on live
        /// streams and after cold recovery without a second projection store.
        /// </summary>
        private static IReadOnlyList<TableProjectionEventFact> MergeCheatFacts(
            IReadOnlyList<TableProjectionEventFact> hostFacts,
            IReadOnlyList<TableEventRecord> committedEvents)
        {
            var merged = new List<TableProjectionEventFact>(hostFacts.Count + committedEvents.Count);
            var replaced = new HashSet<ulong>();
            for (int i = 0; i < committedEvents.Count; i++)
            {
                TableEventRecord record = committedEvents[i];
                if (record.Cheat == null)
                {
                    continue;
                }

                TableProjectionEventFacts? facts = CheatFacts(record.Cheat);
                if (facts == null)
                {
                    continue;
                }

                TableEventProjectionInput? input = TableEventProjection.FromRoundEvent(record);
                if (input == null)
                {
                    continue;
                }

                merged.Add(new TableProjectionEventFact(
                    input.Type,
                    record.Seq,
                    record.CommandVersion,
                    unchecked((uint)record.Ordinal),
                    facts));
                replaced.Add(record.Seq.Value);
            }

            for (int i = 0; i < hostFacts.Count; i++)
            {
                if (!replaced.Contains(hostFacts[i].EventSeq.Value))
                {
                    merged.Add(hostFacts[i]);
                }
            }

            return merged;
        }

        private static TableProjectionEventFacts? CheatFacts(TableCheatEventRecord cheat)
        {
            switch (cheat.Event.Kind)
            {
                case CheatEventKind.CheatPointsChanged:
                    return new TableProjectionEventFacts(turnNumber: unchecked((uint)cheat.TurnNumber));
                case CheatEventKind.SleeveBloomResolved:
                    return new TableProjectionEventFacts(
                        turnNumber: unchecked((uint)cheat.TurnNumber),
                        offerId: cheat.OfferId,
                        keptFirst: cheat.Keep.HasValue && cheat.Keep.Value == SleeveBloomKeep.First,
                        resultTileId: cheat.ResultTile.HasValue
                            ? unchecked((uint)cheat.ResultTile.Value.Value)
                            : (uint?)null);
                case CheatEventKind.BeamSwapResolved:
                    return new TableProjectionEventFacts(
                        turnNumber: unchecked((uint)cheat.TurnNumber),
                        offerId: cheat.OfferId,
                        beamSwapResolution: cheat.Action.HasValue
                            ? (cheat.Action.Value == BeamSwapAction.Swap
                                ? ProjectionBeamSwapResolution.Swap
                                : ProjectionBeamSwapResolution.KeepOriginal)
                            : (ProjectionBeamSwapResolution?)null,
                        handTileId: cheat.HandTile.HasValue
                            ? unchecked((uint)cheat.HandTile.Value.Value)
                            : (uint?)null,
                        targetSlot: cheat.TargetSlot.HasValue
                            ? unchecked((uint)cheat.TargetSlot.Value.Index)
                            : (uint?)null);
                default:
                    return null;
            }
        }
        private IReadOnlyList<ClockView> BuildStreamClocks(long now, long utc)
        {
            var clocks = new List<ClockView>(4);
            for (int seat = 0; seat < 4; seat++)
            {
                DecisionTiming? timing = _decisionClock.Timing(seat);
                long remaining = 0L;
                int decisionSeat = ProtocolConstants.NoSeat;
                if (timing != null)
                {
                    remaining = Math.Max(0L, timing.DeadlineMillis - now);
                    decisionSeat = seat;
                }

                clocks.Add(new ClockView(utc, remaining, _decisionClock.RemainingReserve(seat), decisionSeat));
            }

            return clocks;
        }

        private IReadOnlyList<TableSeatDecision> BuildStreamDecisions(long now)
        {
            var decisions = new List<TableSeatDecision>(_pending.Count);
            foreach (KeyValuePair<int, PendingDecision> entry in _pending)
            {
                PendingDecision pending = entry.Value;
                DecisionTiming? timing = _decisionClock.Timing(entry.Key);
                if (timing == null || now >= timing.DeadlineMillis)
                {
                    continue;
                }

                var actions = new List<LegalActionView>(pending.Candidates.Count);
                for (int i = 0; i < pending.Candidates.Count; i++)
                {
                    LegalActionOption candidate = pending.Candidates[i];
                    ProtocolActionType actionType = MapProtocolAction(candidate.Kind);
                    if (actionType == ProtocolActionType.Unspecified)
                    {
                        // The frozen Contracts action vocabulary has no value for this future
                        // P1.4/P3.3 action. Do not invent one; P3.2c must extend the mapping.
                        continue;
                    }

                    uint[] tiles = BuildCandidateTiles(candidate);
                    actions.Add(new LegalActionView(
                        actionType,
                        candidate.OptionId ?? string.Empty,
                        tiles));
                }

                long remaining = Math.Max(0L, timing.DeadlineMillis - now);
                decisions.Add(new TableSeatDecision(
                    entry.Key,
                    new DecisionView(
                        new DecisionId(pending.Key.DecisionId),
                        new ControlEpoch(unchecked((ulong)pending.ControlEpoch)),
                        remaining,
                        actions)));
            }

            return decisions;
        }

        private static uint[] BuildCandidateTiles(LegalActionOption candidate)
        {
            if (candidate.Tiles != null && candidate.Tiles.Count > 0)
            {
                var tiles = new uint[candidate.Tiles.Count];
                for (int t = 0; t < candidate.Tiles.Count; t++)
                {
                    tiles[t] = unchecked((uint)candidate.Tiles[t].Value);
                }

                return tiles;
            }

            if (candidate.Tile.HasValue)
            {
                return new[] { unchecked((uint)candidate.Tile.Value.Value) };
            }

            return Array.Empty<uint>();
        }

        private static ProtocolActionType MapProtocolAction(LegalActionKind kind)
        {
            switch (kind)
            {
                case LegalActionKind.Pass:
                    return ProtocolActionType.Pass;
                case LegalActionKind.Discard:
                    return ProtocolActionType.DiscardTile;
                case LegalActionKind.Tsumo:
                    return ProtocolActionType.DeclareTsumo;
                case LegalActionKind.Ron:
                    return ProtocolActionType.DeclareRon;
                case LegalActionKind.Chi:
                    return ProtocolActionType.DeclareChi;
                case LegalActionKind.Pon:
                    return ProtocolActionType.DeclarePon;
                case LegalActionKind.Kan:
                    return ProtocolActionType.DeclareKan;
                case LegalActionKind.Riichi:
                    return ProtocolActionType.DeclareRiichi;
                default:
                    return ProtocolActionType.Unspecified;
            }
        }

        /// <summary>
        /// A committed terminal win/draw is an authoritative settlement batch. The actor may
        /// therefore promote only the batch gate from the trusted policy; it never changes
        /// paused/ura authorization and it does not alter event visibility rules. This keeps a
        /// later baseline/Sync/refresh able to carry the full typed settlement payload even when
        /// the diagnostic recent-event window is 0 or 1.
        /// </summary>
        private static TableProjectionPolicy EffectiveProjectionPolicy(
            TableProjectionPolicy policy,
            IReadOnlyList<TableEventProjectionInput> events)
        {
            if (policy.SettlementBatch)
            {
                return policy;
            }

            for (int i = 0; i < events.Count; i++)
            {
                TableEventProjectionInput input = events[i];
                if (TableEventProjectionInput.IsSettlementType(input.Type) ||
                    (input.Type == ProtocolEventType.DoraRevealed && input.IsUraDora))
                {
                    return new TableProjectionPolicy(policy.Paused, true, policy.RevealSettlementUra);
                }
            }

            return policy;
        }

        private TableStreamFrame BuildStreamFrame(
            TableStreamHandle handle,
            StreamProjectionCapture capture,
            bool isFullSnapshot,
            StreamCursor fromCursor,
            StreamCursor cursor,
            IReadOnlyList<TableEventRecord>? committedEvents)
        {
            TableStreamIdentity identity = handle.Identity;
            TableProjectionContextData data = capture.Data;
            TableProjectionPolicy policy = EffectiveProjectionPolicy(data.Policy, capture.EventInputs);
            var context = new TableProjectionContext(
                new TableProjectionIdentity(
                    identity.SessionId,
                    new RoundId(_round.RoundId),
                    _stateVersion,
                    _lastEventSeq,
                    cursor,
                    new SessionVersion(data.SessionVersion)),
                policy,
                capture.Clocks,
                data.PublicFacts,
                data.PrivateFacts,
                capture.Decisions,
                capture.EventInputs,
                data.Session);

            IReadOnlyList<TableProjectedEvent> events = committedEvents == null
                ? Array.Empty<TableProjectedEvent>()
                : ProjectStreamEvents(committedEvents, identity.Audience, policy);
            bool contractAware = _streamOptions.Encoder is IContractProjectionStreamFrameEncoder;
            IReadOnlyList<ProtocolEventRecord> typedEvents = contractAware
                ? MapContractEvents(events, capture)
                : Array.Empty<ProtocolEventRecord>();

            TableStreamFrame frame;
            if (identity.Audience.IsSpectator)
            {
                PublicSnapshotView publicView = TableVisibilityProjection.ProjectPublicSnapshot(_round, context);
                TableStreamContractFrame? contract = null;
                if (contractAware)
                {
                    IReadOnlyList<ProtocolEventRecord> typedSettlement = MapContractSettlementEvents(
                        capture,
                        publicView.View.SettlementEvents,
                        TableProjectionAudience.Spectator,
                        policy);
                    PublicSnapshotView mapped = TableProjectionContractMapper.MapPublicSnapshot(
                        publicView,
                        typedSettlement,
                        data.ConcealedMeldFacts);
                    contract = new TableStreamContractFrame(
                        identity.SessionId,
                        _options.RoomId,
                        identity.Audience,
                        identity.BindingEpoch,
                        identity.SyncEpoch,
                        _ownerEpoch,
                        _recoveryGeneration,
                        _round.RoundId,
                        _stateVersion,
                        _controlVersion,
                        _lastEventSeq,
                        fromCursor,
                        cursor,
                        isFullSnapshot,
                        mapped,
                        null,
                        typedEvents);
                }

                frame = new TableStreamFrame(
                    handle,
                    isFullSnapshot,
                    fromCursor,
                    cursor,
                    _stateVersion,
                    _lastEventSeq,
                    _ownerEpoch,
                    _controlVersion,
                    _recoveryGeneration,
                    _round.RoundId,
                    publicView,
                    null,
                    null,
                    events,
                    contract);
            }
            else
            {
                if (data.Cheat == null)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "A seat stream requires a trusted cheat projection context.");
                }

                PlayerSnapshotView player = TableVisibilityProjection.ProjectPlayerSnapshot(
                    _round,
                    data.Cheat,
                    identity.Audience.Seat,
                    context);
                TableOwnerPrivateView owner = TableVisibilityProjection.ProjectOwnerPrivate(
                    _round,
                    data.Cheat,
                    identity.Audience.Seat,
                    context);
                TableStreamContractFrame? contract = null;
                if (contractAware)
                {
                    IReadOnlyList<ProtocolEventRecord> typedSettlement = MapContractSettlementEvents(
                        capture,
                        player.Public.SettlementEvents,
                        TableProjectionAudience.ForSeat(identity.Audience.Seat),
                        policy);
                    PlayerSnapshotView mapped = TableProjectionContractMapper.MapPlayerSnapshot(
                        player,
                        owner,
                        new TableProjectionTimeContext(
                            capture.MonotonicMillis,
                            currentBootId: _durable?.BootId ?? string.Empty),
                        typedSettlement,
                        data.ConcealedMeldFacts);
                    contract = new TableStreamContractFrame(
                        identity.SessionId,
                        _options.RoomId,
                        identity.Audience,
                        identity.BindingEpoch,
                        identity.SyncEpoch,
                        _ownerEpoch,
                        _recoveryGeneration,
                        _round.RoundId,
                        _stateVersion,
                        _controlVersion,
                        _lastEventSeq,
                        fromCursor,
                        cursor,
                        isFullSnapshot,
                        null,
                        mapped,
                        typedEvents);
                }

                frame = new TableStreamFrame(
                    handle,
                    isFullSnapshot,
                    fromCursor,
                    cursor,
                    _stateVersion,
                    _lastEventSeq,
                    _ownerEpoch,
                    _controlVersion,
                    _recoveryGeneration,
                    _round.RoundId,
                    null,
                    player,
                    owner,
                    events,
                    contract);
            }

            EncodeStreamFrame(frame);
            return frame;
        }

        private IReadOnlyList<ProtocolEventRecord> MapContractEvents(
            IReadOnlyList<TableProjectedEvent> events,
            StreamProjectionCapture capture)
        {
            if (events.Count == 0)
            {
                return Array.Empty<ProtocolEventRecord>();
            }

            var records = new List<ProtocolEventRecord>(events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                records.Add(MapContractEvent(events[i], capture));
            }

            return records;
        }

        private IReadOnlyList<ProtocolEventRecord> MapContractSettlementEvents(
            StreamProjectionCapture capture,
            IReadOnlyList<ProtocolEventRecord> settlementEvents,
            TableProjectionAudience audience,
            TableProjectionPolicy policy)
        {
            if (settlementEvents.Count == 0)
            {
                return Array.Empty<ProtocolEventRecord>();
            }

            var records = new List<ProtocolEventRecord>(settlementEvents.Count);
            for (int i = 0; i < settlementEvents.Count; i++)
            {
                ProtocolEventRecord source = settlementEvents[i];
                TableEventProjectionInput input = capture.InputFor(source.EventSeq) ??
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "Settlement event " + source.EventSeq + " is not in the frozen committed capture.");
                if (input.Type != source.Type ||
                    input.CommandVersion != source.CommandVersion ||
                    input.EventOrdinal != source.EventOrdinal)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventVersionMismatch,
                        "Settlement event " + source.EventSeq + " does not match the committed event identity.");
                }

                TableProjectedEvent projected = TableEventProjection.Project(input, audience, policy) ??
                    throw new TableProjectionException(
                        TableProjectionErrorCode.InvalidContext,
                        "A settlement event in the snapshot is not visible to its own audience.");
                if (projected.Type != source.Type ||
                    projected.CommandVersion != source.CommandVersion ||
                    projected.EventOrdinal != source.EventOrdinal)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventVersionMismatch,
                        "The settlement event projection does not preserve its source identity.");
                }

                records.Add(MapContractEvent(projected, capture));
            }

            return records;
        }

        private static ProtocolEventRecord MapContractEvent(
            TableProjectedEvent value,
            StreamProjectionCapture capture)
        {
            TableProjectionEventFacts? facts = capture.FactsFor(value);
            if (value.IsClosedKan && value.TileIds.Count == 0 && facts != null)
            {
                // The public closed-kan body is identity-free by policy. Never let a trusted
                // owner fact reintroduce the four physical tiles into a redacted payload.
                facts = facts.WithoutMeld();
            }

            return TableProjectionContractMapper.MapEvent(value, facts);
        }

        private IReadOnlyList<TableProjectedEvent> ProjectStreamEvents(
            IReadOnlyList<TableEventRecord> records,
            TableStreamAudience audience,
            TableProjectionPolicy policy)
        {
            TableProjectionAudience projectionAudience = audience.IsSpectator
                ? TableProjectionAudience.Spectator
                : TableProjectionAudience.ForSeat(audience.Seat);
            var events = new List<TableProjectedEvent>(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                TableEventProjectionInput? input = TableEventProjection.FromRoundEvent(records[i]);
                if (input == null)
                {
                    continue;
                }

                TableProjectedEvent? visible = TableEventProjection.Project(input, projectionAudience, policy);
                if (visible != null)
                {
                    events.Add(visible);
                }
            }

            return events;
        }

        private void EncodeStreamFrame(TableStreamFrame frame)
        {
            ITableStreamFrameEncoder? encoder = _streamOptions.Encoder;
            if (encoder == null)
            {
                if (frame.Public == null && frame.Player == null && frame.OwnerPrivate == null && frame.Events.Count == 0)
                {
                    throw new StreamFrameException(
                        TableStreamTerminationKind.ProjectionFailed,
                        ProtocolErrorCode.InternalError,
                        "A stream frame cannot have an empty payload.");
                }

                return;
            }

            TableEncodedFrame encoded;
            try
            {
                encoded = encoder.Encode(frame);
            }
            catch (Exception exception)
            {
                throw new StreamFrameException(
                    TableStreamTerminationKind.EncoderFailed,
                    ProtocolErrorCode.InternalError,
                    "The stream encoder failed: " + exception.GetType().Name + ": " + exception.Message);
            }

            if (encoded.Length == 0)
            {
                throw new StreamFrameException(
                    TableStreamTerminationKind.EncoderFailed,
                    ProtocolErrorCode.MalformedMessage,
                    "The stream encoder returned an empty payload.");
            }

            if (encoded.DeclaredSize != encoded.Length)
            {
                throw new StreamFrameException(
                    TableStreamTerminationKind.EncoderFailed,
                    ProtocolErrorCode.MalformedMessage,
                    "The stream encoder declared " + encoded.DeclaredSize +
                    " bytes but produced " + encoded.Length + ".");
            }

            if (encoded.Length > _streamOptions.MaxQueuedBytes)
            {
                throw new StreamFrameException(
                    TableStreamTerminationKind.MessageTooLarge,
                    ProtocolErrorCode.MessageTooLarge,
                    "The encoded stream frame is " + encoded.Length +
                    " bytes, above the configured " + _streamOptions.MaxQueuedBytes + " byte limit.");
            }

            frame.AttachEncodedPayload(encoded.CopyBytes());
        }

        private void PublishToStreams(TablePublication publication)
        {
            if (_streams.Count == 0)
            {
                return;
            }

            try
            {
                switch (publication.Kind)
                {
                    case TablePublicationKind.Applied:
                        PublishAppliedToStreams(publication);
                        break;
                    case TablePublicationKind.ControlChanged:
                        PublishControlToStreams(publication);
                        break;
                    case TablePublicationKind.Recovered:
                        RebaselineStreams();
                        break;
                    case TablePublicationKind.LeaseLost:
                        CloseAllStreams(
                            TableStreamTerminationKind.LeaseLost,
                            ProtocolErrorCode.LeaseLost,
                            "The table lease was lost; no stream content is published.");
                        break;
                }
            }
            catch (Exception exception)
            {
                _publicationFault = exception.GetType().Name + ": " + exception.Message;
                CloseAllStreams(
                    TableStreamTerminationKind.ProjectionFailed,
                    ProtocolErrorCode.InternalError,
                    _publicationFault);
            }
        }

        private void PublishAppliedToStreams(TablePublication publication)
        {
            RemoveFaultedStreams();
            if (_streams.Count == 0)
            {
                return;
            }

            StreamProjectionCapture capture;
            try
            {
                capture = CaptureStreamProjection();
            }
            catch (Exception exception)
            {
                _publicationFault = exception.GetType().Name + ": " + exception.Message;
                CloseAllStreams(
                    TableStreamTerminationKind.ProjectionFailed,
                    ProtocolErrorCode.InternalError,
                    _publicationFault);
                return;
            }

            var registrations = new List<TableStreamRegistration>(_streams.Values);
            for (int i = 0; i < registrations.Count; i++)
            {
                TableStreamRegistration registration = registrations[i];
                if (registration.Channel.IsClosed)
                {
                    RemoveRegistration(registration);
                    continue;
                }

                if (registration.NeedsSync)
                {
                    // Recovery baseline is installed but not acknowledged; withhold increments
                    // so Sync must rebuild a current full baseline before updates resume.
                    continue;
                }

                ProjectionSeq nextSeq;
                try
                {
                    nextSeq = registration.State.ProjectionSeq.Next();
                }
                catch (OverflowException exception)
                {
                    CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                        ProtocolErrorCode.InternalError, "Projection sequence overflow: " + exception.Message);
                    continue;
                }

                var cursor = new StreamCursor(new StreamVersion(_stateVersion.Value), nextSeq);
                TableStreamFrame frame;
                try
                {
                    frame = BuildStreamFrame(
                        registration.Handle,
                        capture,
                        false,
                        registration.State.Cursor,
                        cursor,
                        publication.Events);
                }
                catch (StreamFrameException exception)
                {
                    CloseStream(registration, exception.Kind, exception.ReasonCode, exception.Message);
                    continue;
                }
                catch (Exception exception) when (
                    exception is TableProjectionException ||
                    exception is ArgumentException ||
                    exception is InvalidOperationException)
                {
                    _publicationFault = exception.GetType().Name + ": " + exception.Message;
                    CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                        ProtocolErrorCode.InternalError, _publicationFault);
                    continue;
                }

                TableStreamEnqueueStatus enqueue = registration.Channel.TryEnqueue(frame);
                if (enqueue != TableStreamEnqueueStatus.Enqueued)
                {
                    RemoveRegistration(registration);
                    continue;
                }

                registration.State.ProjectionSeq = nextSeq;
                registration.State.Cursor = cursor;
                registration.ControlVersion = _controlVersion;
            }
        }

        private void PublishControlToStreams(TablePublication publication)
        {
            RemoveFaultedStreams();
            if (_streams.Count == 0)
            {
                return;
            }

            var changedEpochs = new Dictionary<int, long>();
            for (int i = 0; i < publication.ControlEvents.Count; i++)
            {
                TableControlEvent change = publication.ControlEvents[i];
                if (change.Seat >= 0 && change.Seat <= 3 && change.NewEpoch.HasValue)
                {
                    changedEpochs[change.Seat] = change.NewEpoch.Value;
                }
            }

            var registrations = new List<TableStreamRegistration>(_streams.Values);
            for (int i = 0; i < registrations.Count; i++)
            {
                TableStreamRegistration registration = registrations[i];
                if (registration.Handle.Identity.Audience.IsSeat &&
                    changedEpochs.TryGetValue(registration.Handle.Identity.Audience.Seat, out long newEpoch))
                {
                    if (!registration.BoundControlEpoch.HasValue ||
                        registration.BoundControlEpoch.Value.Value != unchecked((ulong)newEpoch))
                    {
                        CloseStream(registration, TableStreamTerminationKind.StaleControlEpoch,
                            TableRuntimeReasonCode.StaleControlEpoch,
                            "The seat controller changed; the old private stream was closed.");
                        continue;
                    }
                }
            }

            RemoveFaultedStreams();
            if (_streams.Count == 0)
            {
                return;
            }

            StreamProjectionCapture capture;
            try
            {
                capture = CaptureStreamProjection();
            }
            catch (Exception exception)
            {
                _publicationFault = exception.GetType().Name + ": " + exception.Message;
                CloseAllStreams(
                    TableStreamTerminationKind.ProjectionFailed,
                    ProtocolErrorCode.InternalError,
                    _publicationFault);
                return;
            }

            var remaining = new List<TableStreamRegistration>(_streams.Values);
            for (int i = 0; i < remaining.Count; i++)
            {
                TableStreamRegistration registration = remaining[i];
                if (registration.Channel.IsClosed)
                {
                    RemoveRegistration(registration);
                    continue;
                }

                if (registration.NeedsSync)
                {
                    // The recovery baseline is still awaiting an explicit Sync. A later Sync
                    // sees the changed control version and queues a fresh full baseline.
                    continue;
                }

                ProjectionSeq nextSeq;
                try
                {
                    nextSeq = registration.State.ProjectionSeq.Next();
                }
                catch (OverflowException exception)
                {
                    CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                        ProtocolErrorCode.InternalError, "Projection sequence overflow: " + exception.Message);
                    continue;
                }

                var cursor = new StreamCursor(new StreamVersion(_stateVersion.Value), nextSeq);
                TableStreamFrame frame;
                try
                {
                    frame = BuildStreamFrame(
                        registration.Handle,
                        capture,
                        false,
                        registration.State.Cursor,
                        cursor,
                        null);
                }
                catch (StreamFrameException exception)
                {
                    CloseStream(registration, exception.Kind, exception.ReasonCode, exception.Message);
                    continue;
                }
                catch (Exception exception) when (
                    exception is TableProjectionException ||
                    exception is ArgumentException ||
                    exception is InvalidOperationException)
                {
                    _publicationFault = exception.GetType().Name + ": " + exception.Message;
                    CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                        ProtocolErrorCode.InternalError, _publicationFault);
                    continue;
                }

                TableStreamEnqueueStatus enqueue = registration.Channel.TryEnqueue(frame);
                if (enqueue != TableStreamEnqueueStatus.Enqueued)
                {
                    RemoveRegistration(registration);
                    continue;
                }

                registration.State.ProjectionSeq = nextSeq;
                registration.State.Cursor = cursor;
                registration.ControlVersion = _controlVersion;
            }
        }

        private void PublishDecisionRefreshToStreams()
        {
            RemoveFaultedStreams();
            if (_streams.Count == 0)
            {
                return;
            }

            StreamProjectionCapture capture;
            try
            {
                capture = CaptureStreamProjection();
            }
            catch (Exception exception)
            {
                _publicationFault = exception.GetType().Name + ": " + exception.Message;
                CloseAllStreams(
                    TableStreamTerminationKind.ProjectionFailed,
                    ProtocolErrorCode.InternalError,
                    _publicationFault);
                return;
            }

            var registrations = new List<TableStreamRegistration>(_streams.Values);
            for (int i = 0; i < registrations.Count; i++)
            {
                TableStreamRegistration registration = registrations[i];
                if (registration.Channel.IsClosed)
                {
                    RemoveRegistration(registration);
                    continue;
                }

                if (registration.NeedsSync)
                {
                    // Recovery baseline is not acknowledged yet. Sync will rebuild a current
                    // full baseline, which includes the newly opened decision.
                    continue;
                }

                ProjectionSeq nextSeq;
                try
                {
                    nextSeq = registration.State.ProjectionSeq.Next();
                }
                catch (OverflowException exception)
                {
                    CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                        ProtocolErrorCode.InternalError, "Projection sequence overflow: " + exception.Message);
                    continue;
                }

                var cursor = new StreamCursor(new StreamVersion(_stateVersion.Value), nextSeq);
                TableStreamFrame frame;
                try
                {
                    frame = BuildStreamFrame(
                        registration.Handle,
                        capture,
                        false,
                        registration.State.Cursor,
                        cursor,
                        null);
                }
                catch (StreamFrameException exception)
                {
                    CloseStream(registration, exception.Kind, exception.ReasonCode, exception.Message);
                    continue;
                }
                catch (Exception exception) when (
                    exception is TableProjectionException ||
                    exception is ArgumentException ||
                    exception is InvalidOperationException)
                {
                    _publicationFault = exception.GetType().Name + ": " + exception.Message;
                    CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                        ProtocolErrorCode.InternalError, _publicationFault);
                    continue;
                }

                TableStreamEnqueueStatus enqueue = registration.Channel.TryEnqueue(frame);
                if (enqueue != TableStreamEnqueueStatus.Enqueued)
                {
                    RemoveRegistration(registration);
                    continue;
                }

                registration.State.ProjectionSeq = nextSeq;
                registration.State.Cursor = cursor;
                registration.ControlVersion = _controlVersion;
            }
        }

        private void RebaselineStreams()
        {
            RemoveFaultedStreams();
            if (_streams.Count == 0)
            {
                return;
            }

            StreamProjectionCapture capture;
            try
            {
                capture = CaptureStreamProjection();
            }
            catch (Exception exception)
            {
                _publicationFault = exception.GetType().Name + ": " + exception.Message;
                CloseAllStreams(
                    TableStreamTerminationKind.ProjectionFailed,
                    ProtocolErrorCode.InternalError,
                    _publicationFault);
                return;
            }

            var registrations = new List<TableStreamRegistration>(_streams.Values);
            for (int i = 0; i < registrations.Count; i++)
            {
                TableStreamRegistration registration = registrations[i];
                if (registration.OwnerEpoch != _ownerEpoch)
                {
                    CloseStream(registration, TableStreamTerminationKind.StaleOwner,
                        ProtocolErrorCode.LeaseLost,
                        "The recovery baseline belongs to a newer table owner epoch.");
                    continue;
                }

                if (registration.Handle.Identity.Audience.IsSeat)
                {
                    long currentControlEpoch = _control.GetEpoch(registration.Handle.Identity.Audience.Seat);
                    if (!registration.BoundControlEpoch.HasValue ||
                        registration.BoundControlEpoch.Value.Value != unchecked((ulong)currentControlEpoch))
                    {
                        CloseStream(registration, TableStreamTerminationKind.StaleControlEpoch,
                            TableRuntimeReasonCode.StaleControlEpoch,
                            "The recovered controller changed; the old private stream was closed.");
                        continue;
                    }
                }

                ProjectionSeq nextSeq;
                try
                {
                    nextSeq = registration.State.ProjectionSeq.Next();
                }
                catch (OverflowException exception)
                {
                    CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                        ProtocolErrorCode.InternalError, "Projection sequence overflow: " + exception.Message);
                    continue;
                }

                var cursor = new StreamCursor(new StreamVersion(_stateVersion.Value), nextSeq);
                TableStreamFrame frame;
                try
                {
                    frame = BuildStreamFrame(
                        registration.Handle,
                        capture,
                        true,
                        registration.State.Cursor,
                        cursor,
                        null);
                }
                catch (StreamFrameException exception)
                {
                    CloseStream(registration, exception.Kind, exception.ReasonCode, exception.Message);
                    continue;
                }
                catch (Exception exception) when (
                    exception is TableProjectionException ||
                    exception is ArgumentException ||
                    exception is InvalidOperationException)
                {
                    _publicationFault = exception.GetType().Name + ": " + exception.Message;
                    CloseStream(registration, TableStreamTerminationKind.ProjectionFailed,
                        ProtocolErrorCode.InternalError, _publicationFault);
                    continue;
                }

                TableStreamEnqueueStatus enqueue = registration.Channel.TryEnqueue(frame);
                if (enqueue != TableStreamEnqueueStatus.Enqueued)
                {
                    RemoveRegistration(registration);
                    continue;
                }

                registration.State.ProjectionSeq = nextSeq;
                registration.State.Cursor = cursor;
                registration.RecoveryGeneration = _recoveryGeneration;
                registration.RoundId = _round.RoundId;
                registration.ControlVersion = _controlVersion;
                registration.NeedsSync = true;
            }
        }

        private void RemoveFaultedStreams()
        {
            if (_streams.Count == 0)
            {
                return;
            }

            var registrations = new List<TableStreamRegistration>(_streams.Values);
            for (int i = 0; i < registrations.Count; i++)
            {
                if (registrations[i].Channel.IsClosed)
                {
                    RemoveRegistration(registrations[i]);
                }
            }
        }

        private sealed class StreamProjectionCapture
        {
            private readonly Dictionary<ulong, TableEventProjectionInput> _inputs;
            private readonly IReadOnlyDictionary<ulong, TableProjectionEventFact> _facts;

            public StreamProjectionCapture(
                TableProjectionContextData data,
                IReadOnlyList<ClockView> clocks,
                IReadOnlyList<TableSeatDecision> decisions,
                IReadOnlyList<TableEventProjectionInput> eventInputs,
                IReadOnlyDictionary<ulong, TableProjectionEventFact> facts,
                long monotonicMillis)
            {
                Data = data;
                Clocks = clocks;
                Decisions = decisions;
                EventInputs = eventInputs;
                _facts = facts;
                MonotonicMillis = monotonicMillis;
                _inputs = new Dictionary<ulong, TableEventProjectionInput>(eventInputs.Count);
                for (int i = 0; i < eventInputs.Count; i++)
                {
                    _inputs[eventInputs[i].EventSeq.Value] = eventInputs[i];
                }
            }

            public TableProjectionContextData Data { get; }

            public IReadOnlyList<ClockView> Clocks { get; }

            public IReadOnlyList<TableSeatDecision> Decisions { get; }

            public IReadOnlyList<TableEventProjectionInput> EventInputs { get; }

            public long MonotonicMillis { get; }

            public TableProjectionEventFacts? FactsFor(TableProjectedEvent value)
            {
                if (!_facts.TryGetValue(value.EventSeq.Value, out TableProjectionEventFact? fact))
                {
                    return null;
                }

                if (fact.Type != value.Type ||
                    fact.CommandVersion != value.CommandVersion ||
                    fact.EventOrdinal != value.EventOrdinal)
                {
                    throw new TableProjectionException(
                        TableProjectionErrorCode.EventVersionMismatch,
                        "Event fact " + value.EventSeq + " does not match the projected event identity.");
                }

                return fact.Facts;
            }

            public TableEventProjectionInput? InputFor(EventSeq eventSeq)
            {
                return _inputs.TryGetValue(eventSeq.Value, out TableEventProjectionInput? input) ? input : null;
            }
        }

        private sealed class CheatRequestBinding
        {
            public CheatRequestBinding(
                RequestScopeId scopeId,
                string api,
                TablePayloadSha256 payload,
                TableOperationResult? result)
            {
                ScopeId = scopeId;
                Api = api;
                Payload = payload;
                Result = result;
            }

            public RequestScopeId ScopeId { get; }

            public string Api { get; }

            public TablePayloadSha256 Payload { get; }

            public TableOperationResult? Result { get; }
        }

        private sealed class StreamFrameException : Exception
        {
            public StreamFrameException(TableStreamTerminationKind kind, string reasonCode, string detail)
                : base(detail)
            {
                Kind = kind;
                ReasonCode = reasonCode;
            }

            public TableStreamTerminationKind Kind { get; }

            public string ReasonCode { get; }
        }

        private void AppendRecent(IReadOnlyList<TableEventRecord> records)
        {
            for (int i = 0; i < records.Count; i++)
            {
                TableEventRecord record = records[i];
                _committedEventLog.Add(record);
                if (record.Cheat == null)
                {
                    // The authority snapshot's recent-event window is public/round-shaped.
                    // Owner-scoped cheat events stay in the committed log used by the seat
                    // projection; they must never leak through TableSnapshot.
                    _recentEvents.Add(record);
                }
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
            // Streams are enqueued first and never await a sink here. The legacy publication
            // sink is still synchronous for P3.1 compatibility, but a slow stream consumer
            // cannot delay the actor or the other streams.
            PublishToStreams(publication);
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

            CloseAllStreams(
                TableStreamTerminationKind.ActorFaulted,
                ProtocolErrorCode.InternalError,
                exception.GetType().Name + ": " + exception.Message);
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
                case SubscribeStreamMailboxMessage subscribe:
                    subscribe.Completion.TrySetResult(TableStreamControlResult.Failure(
                        TableStreamControlStatus.Failed,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        null,
                        _stateVersion,
                        _controlVersion));
                    break;
                case UnsubscribeStreamMailboxMessage unsubscribe:
                    unsubscribe.Completion.TrySetResult(TableStreamControlResult.Failure(
                        TableStreamControlStatus.Failed,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        unsubscribe.Handle,
                        _stateVersion,
                        _controlVersion));
                    break;
                case SyncStreamMailboxMessage sync:
                    sync.Completion.TrySetResult(TableStreamControlResult.Failure(
                        TableStreamControlStatus.Failed,
                        TableRuntimeReasonCode.InternalError,
                        detail,
                        sync.Request.Handle,
                        _stateVersion,
                        _controlVersion));
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
