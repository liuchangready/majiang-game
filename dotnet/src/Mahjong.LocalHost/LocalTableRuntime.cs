using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.Domain.Rules;
using Mahjong.TableRuntime;
using ContractRole = Mahjong.Contracts.ProtocolRole;
using WireRole = Mahjong.Contracts.Wire.Role;
using WireScope = Mahjong.Contracts.Wire.RequestScope;
using WireReceipt = Mahjong.Contracts.Wire.Receipt;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;
using WireStreamBaseline = Mahjong.Contracts.Wire.StreamBaseline;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// One opened local runtime: the durable store, the single authoritative actor, and the
    /// loopback wire bridge. It is created only by a channel's first OpenAsync.
    /// </summary>
    internal sealed class LocalTableRuntime : IAsyncDisposable
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, WireReceipt> _controlReceipts = new Dictionary<string, WireReceipt>(StringComparer.Ordinal);
        private readonly LocalTableHostOptions _options;
        private readonly LocalHostEnvironment _environment;
        private readonly LocalTableStore _store;
        private readonly TableActor _actor;
        private readonly LocalSnapshotFacts _facts;
        private readonly LocalTableDriver _driver;
        private readonly AsyncFrameQueue _outbound = new AsyncFrameQueue();

        private string _sessionId = string.Empty;
        private ulong _bindingEpoch;
        private long _syncEpoch;
        private long _streamGeneration;
        private RequestScopeId? _connectionScopeId;
        private string _connectionAccount = string.Empty;
        private int _seat;
        private TableStreamHandle? _stream;
        private TableStreamSink? _sink;
        private string? _pendingSyncCorrelation;
        private bool _sessionOpen;
        private bool _disposed;

        private LocalTableRuntime(
            LocalTableHostOptions options,
            LocalHostEnvironment environment,
            LocalTableStore store,
            TableActor actor,
            LocalSnapshotFacts facts)
        {
            _options = options;
            _environment = environment;
            _store = store;
            _actor = actor;
            _facts = facts;
            var scopes = new RequestScopeId[4];
            ulong now = (ulong)Math.Max(0L, environment.ResolveClock().UtcNowMs);
            ulong expires = now + (ulong)TimeSpan.FromHours(24).TotalMilliseconds;
            for (int seat = 1; seat <= 3; seat++)
            {
                scopes[seat] = store.IssueScope(
                    "local-" + options.RoomId + "-seat" + seat,
                    TableScopeRole.Player,
                    options.RoomId,
                    now,
                    expires,
                    environment.ResolveId()).ScopeId;
            }

            _driver = new LocalTableDriver(actor, options, store, scopes, () => _sessionOpen);
            _driver.Start();
        }

        public static async ValueTask<LocalTableRuntime> OpenAsync(
            LocalTableHostOptions options,
            CancellationToken cancellationToken)
        {
            LocalHostEnvironment environment = options.Environment ?? LocalHostEnvironment.System;
            ITableClock clock = environment.ResolveClock();
            LocalTableStore store = await LocalTableStore.OpenAsync(options, clock, options.FileSystemForTest, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var facts = new LocalSnapshotFacts(environment, clock);
                TableActor actor;
                if (store.Exists)
                {
                    TableCheckpoint checkpoint = await store.LoadCheckpointAsync(options.RoomId, cancellationToken)
                        .ConfigureAwait(false) ?? throw new LocalTableException(
                            LocalTableErrorKind.CorruptState,
                            "The local save has no base checkpoint.");
                    RoundStateMachine round = RestoreRound(checkpoint);
                    facts.RecoveredFacts = checkpoint.CommittedFactsPayload == null
                        ? null
                        : TableProjectionFactsCodec.Decode(checkpoint.CommittedFactsPayload, round.RoundId, checkpoint.StateVersion);
                    actor = CreateActor(options, environment, clock, store, facts, round, checkpoint, recover: true);
                    facts.SettlementProvider = () => actor.LastSettlementCalculation;
                    TableRecoveryResult recovered = await actor.RecoverFromStoreAsync(cancellationToken).ConfigureAwait(false);
                    if (!recovered.Recovered)
                    {
                        await actor.DisposeAsync().ConfigureAwait(false);
                        throw new LocalTableException(
                            LocalTableErrorKind.CorruptState,
                            "Cold recovery failed: " + recovered.ReasonCode + " " + recovered.Detail);
                    }
                }
                else
                {
                    RoundStateMachine round = options.InitialRound ?? facts.CreateInitialRound(options);
                    TableCheckpoint checkpoint = facts.CreateInitialCheckpoint(options, round);
                    await store.CreateRoomAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                    actor = CreateActor(options, environment, clock, store, facts, round, checkpoint, recover: false);
                    facts.SettlementProvider = () => actor.LastSettlementCalculation;
                }

                var runtime = new LocalTableRuntime(options, environment, store, actor, facts);
                return runtime;
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        private static RoundStateMachine RestoreRound(TableCheckpoint checkpoint)
        {
            try
            {
                RoundRecoveryState captured = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
                return RoundRecovery.Restore(captured);
            }
            catch (Exception exception)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.CorruptState,
                    "The checkpoint round could not be restored: " + exception.Message,
                    exception);
            }
        }

        private static TableActor CreateActor(
            LocalTableHostOptions options,
            LocalHostEnvironment environment,
            ITableClock clock,
            LocalTableStore store,
            LocalSnapshotFacts facts,
            RoundStateMachine round,
            TableCheckpoint checkpoint,
            bool recover)
        {
            var controllers = new Dictionary<int, SeatControllerKind>
            {
                { 0, SeatControllerKind.LocalHuman },
                { 1, SeatControllerKind.Computer },
                { 2, SeatControllerKind.Computer },
                { 3, SeatControllerKind.Computer }
            };
            var actorOptions = new TableActorOptions
            {
                RoomId = options.RoomId,
                MailboxCapacity = 128,
                ControlReserve = 32,
                SnapshotEventWindow = 128,
                InitialOwnerEpoch = store.LeaseEpoch,
                InitialTimingVersion = checkpoint.TimingVersion,
                InitialControllers = controllers,
                DecisionClockConfig = new DecisionClockConfig(20_000L, 3_000L, ReserveScope.Round),
                CheatEconomy = LocalSnapshotFacts.CheatEconomy,
                SleeveBloomConfig = LocalSnapshotFacts.SleeveBloom,
                InitialCheatState = LocalSnapshotFacts.InitialCheatState
            };

            var durable = new TableDurableContext(
                store,
                store,
                store,
                store,
                store.Scopes,
                options.OwnerInstanceId,
                options.LeaseDuration,
                environment.ResolveId(),
                options.RulesetVersion,
                options.RoomId);
            durable.CheatStateProvider = () => LocalSnapshotFacts.InitialCheatState;
            durable.CheatLedgerProvider = () => LocalSnapshotFacts.InitialCheatState.Ledger;
            durable.ProjectionFactsProvider = facts.Capture;
            durable.RecoveryStateSink = new LocalRecoverySink(options.RoomId);
            durable.RequireCommittedFacts = true;
            durable.ReceiptRetention = TimeSpan.FromHours(24);

            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(new LocalStreamContractCodec()),
                MaxQueuedFrames = 128
            };
            var settlementFacts = new LocalSettlementFactsPort();
            var actor = new TableActor(
                actorOptions,
                round,
                new NoopCommitPort(),
                clock,
                new NoopPublicationSink(),
                null,
                facts,
                streamOptions,
                durable,
                settlementFacts);
            settlementFacts.RoundProvider = () => actor.GetRoundStateForHost();
            return actor;
        }

        internal Mahjong.Domain.RoundResult? CurrentRoundResult => _actor.GetRoundResult();

        internal bool DriverRunning => _driver.IsRunning;

        public async Task ReconnectAsync(CancellationToken cancellationToken)
        {
            TableStreamHandle? previous;
            lock (_gate)
            {
                previous = _stream;
                _sessionOpen = true;
                _streamGeneration++;
                _outbound.Reset();
                _pendingSyncCorrelation = null;
                _stream = null;
                _sink = null;
            }

            if (previous != null)
            {
                try
                {
                    await _actor.UnsubscribeStreamAsync(previous, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The generation fence prevents a late frame from the old subscription.
                }
            }
        }

        public void CloseSession()
        {
            lock (_gate)
            {
                _sessionOpen = false;
                _streamGeneration++;
                _outbound.Fail(new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false));
                _pendingSyncCorrelation = null;
                _stream = null;
                _sink = null;
            }
        }

        public async Task CloseSessionAsync(CancellationToken cancellationToken)
        {
            TableStreamHandle? stream;
            lock (_gate)
            {
                _sessionOpen = false;
                _streamGeneration++;
                _outbound.Fail(new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false));
                _pendingSyncCorrelation = null;
                stream = _stream;
                _stream = null;
                _sink = null;
            }

            if (stream != null)
            {
                try
                {
                    await _actor.UnsubscribeStreamAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The actor may already be closed; the generation fence drops late frames.
                }
            }
        }

        public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = wireEnvelope.ToArray();
            WireDecodeResult decoded = WireCodec.Decode(bytes);
            if (!decoded.IsOk || decoded.Envelope == null)
            {
                Enqueue(ErrorEnvelope(decoded.ErrorCode, "Loopback decode rejected the frame: " + decoded.Status));
                return;
            }

            Envelope envelope = decoded.Envelope;
            try
            {
                switch (envelope.BodyCase)
                {
                    case Envelope.BodyOneofCase.Hello:
                        await HandleHelloAsync(envelope, cancellationToken).ConfigureAwait(false);
                        break;
                    case Envelope.BodyOneofCase.SyncRequest:
                        await HandleSyncAsync(envelope, cancellationToken).ConfigureAwait(false);
                        break;
                    case Envelope.BodyOneofCase.Command:
                        await HandleCommandAsync(envelope, cancellationToken).ConfigureAwait(false);
                        break;
                    case Envelope.BodyOneofCase.ReceiptQuery:
                        await HandleReceiptQueryAsync(envelope, cancellationToken).ConfigureAwait(false);
                        break;
                    case Envelope.BodyOneofCase.Heartbeat:
                        if (envelope.Heartbeat != null && envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Ping)
                        {
                            Enqueue(PongEnvelope(envelope));
                        }

                        break;
                    case Envelope.BodyOneofCase.Close:
                        CloseSession();
                        break;
                    default:
                        Enqueue(ErrorEnvelope(ProtocolErrorCode.MalformedMessage, "Unsupported loopback message."));
                        break;
                }
            }
            catch (LocalTableException exception)
            {
                Enqueue(ErrorEnvelope(
                    exception.ErrorKind == LocalTableErrorKind.Unavailable
                        ? ProtocolErrorCode.InternalError
                        : ProtocolErrorCode.MalformedMessage,
                    exception.Message));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.InternalError, exception.GetType().Name + ": " + exception.Message));
            }
        }

        public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            return ReceiveCoreAsync(cancellationToken);
        }

        private async Task<ReadOnlyMemory<byte>> ReceiveCoreAsync(CancellationToken cancellationToken)
        {
            byte[] frame = await _outbound.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            return new ReadOnlyMemory<byte>(frame);
        }

        private async Task HandleHelloAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            Hello hello = envelope.Hello!;
            if (hello.ProtocolMajor != ProtocolConstants.Major || hello.ProtocolMinor > ProtocolConstants.Minor)
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.ProtocolMajorUnsupported, "Protocol versions do not match."));
                return;
            }

            if (!LocalTicketIssuer.TryConsume(hello.Ticket, _options.RoomId))
            {
                Enqueue(ErrorEnvelope(
                    ProtocolErrorCode.Unauthenticated,
                    "The local ticket is missing, expired, already used or bound to another room."));
                return;
            }

            string account = "local-" + _options.RoomId + "-seat0";
            ulong now = (ulong)Math.Max(0L, _environment.ResolveClock().UtcNowMs);
            ulong expires = now + (ulong)TimeSpan.FromHours(24).TotalMilliseconds;
            string scopeId = _environment.ResolveId();

            TableScopeRecord scope;
            string sessionId;
            ulong bindingEpoch;
            lock (_gate)
            {
                sessionId = !string.IsNullOrEmpty(hello.ResumeSessionId) && hello.ResumeRequested
                    ? hello.ResumeSessionId!
                    : _environment.ResolveId();
                _bindingEpoch = _bindingEpoch == 0UL ? 1UL : _bindingEpoch + 1UL;
                bindingEpoch = _bindingEpoch;
                _sessionId = sessionId;
                _seat = 0;
                _connectionAccount = account;
                _sessionOpen = true;
                scope = _store.IssueScope(account, TableScopeRole.Player, _options.RoomId, now, expires, scopeId);
                _connectionScopeId = scope.ScopeId;
                _outbound.Reset();
            }

            Envelope welcome = BuildWelcome(sessionId, bindingEpoch, scope, now, expires);
            Enqueue(welcome);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private Envelope BuildWelcome(
            string sessionId,
            ulong bindingEpoch,
            TableScopeRecord scope,
            ulong issuedAt,
            ulong expiresAt)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Welcome,
                SessionId = sessionId,
                RoomId = _options.RoomId,
                SeatId = _seat,
                BindingEpoch = bindingEpoch,
                Welcome = new Welcome
                {
                    SessionId = sessionId,
                    RoomId = _options.RoomId,
                    TableId = _options.RoomId,
                    Role = WireRole.Player,
                    SeatId = _seat,
                    ConnectionEpoch = 1,
                    OwnerEpoch = _actorLeaseEpoch(),
                    BindingEpoch = bindingEpoch,
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    Baseline = new WireStreamBaseline
                    {
                        StateVersion = 0,
                        EventSeq = 0,
                        Cursor = new WireCursor { StreamVersion = 0, ProjectionSeq = 0 }
                    },
                    Scope = new WireScope
                    {
                        ScopeId = scope.ScopeId.Value,
                        AccountId = scope.AccountId,
                        Role = WireRole.Player,
                        RoomId = _options.RoomId,
                        IssuedAtMs = issuedAt,
                        ExpiresAtMs = expiresAt
                    },
                    Limits = new ServerLimits
                    {
                        MaxInboundMessageBytes = WireLimits.MaxInboundMessageBytes,
                        MaxCommandPayloadBytes = WireLimits.MaxCommandPayloadBytes,
                        MaxSnapshotBytes = WireLimits.MaxSnapshotBytes,
                        MaxInFlightRequests = 64,
                        HeartbeatIntervalMs = 10_000,
                        IdleDeadMs = 30_000,
                        MaxEarlyBufferItems = 64,
                        MaxEarlyBufferBytes = 4 * 1024 * 1024
                    }
                }
            };
        }

        private async Task HandleSyncAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            if (!IsCurrentBinding(envelope))
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.Unauthenticated, "The sync request does not match the open binding."));
                return;
            }

            await EnsureInitialHumanDecisionAsync().ConfigureAwait(false);
            TableStreamSink sink;
            TableStreamIdentity identity;
            lock (_gate)
            {
                _syncEpoch++;
                identity = new TableStreamIdentity(
                    new SessionId(_sessionId),
                    TableStreamAudience.ForSeat(_seat),
                    new BindingEpoch(_bindingEpoch),
                    new SyncEpoch((ulong)_syncEpoch));
                sink = new TableStreamSink(this, _streamGeneration);
                _sink = sink;
                _pendingSyncCorrelation = envelope.CorrelationId;
            }

            var subscribe = new TableStreamSubscribeRequest(
                identity,
                _store.LeaseEpoch,
                _actor.RecoveryGeneration,
                sink);
            TableStreamControlResult result = await _actor.SubscribeStreamAsync(subscribe, cancellationToken).ConfigureAwait(false);
            if (!result.IsAccepted || result.Handle == null)
            {
                lock (_gate)
                {
                    _pendingSyncCorrelation = null;
                }

                Enqueue(ErrorEnvelope(
                    ProtocolErrorCode.IsWellFormed(result.ReasonCode) ? result.ReasonCode : ProtocolErrorCode.InternalError,
                    "Stream subscribe failed: " + result.Detail));
                return;
            }

            lock (_gate)
            {
                _stream = result.Handle;
            }

            if (result.Status == TableStreamControlStatus.InSync)
            {
                TableStreamFrame? last = sink.LastFrame;
                if (last != null)
                {
                    Enqueue(BuildSyncResponse(last, envelope.CorrelationId));
                }
                else
                {
                    lock (_gate)
                    {
                        _pendingSyncCorrelation = null;
                    }

                    Enqueue(ErrorEnvelope(ProtocolErrorCode.InternalError, "The local stream has no baseline."));
                }
            }
        }
        private async Task EnsureInitialHumanDecisionAsync()
        {
            if (_seat != 0 || _actor.GetPendingDecisionId(0) != null)
            {
                return;
            }

            TableSnapshot snapshot = await _actor.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            if (snapshot.Phase != RoundPhase.CheatWindow && snapshot.Phase != RoundPhase.Discard)
            {
                return;
            }

            IReadOnlyList<TileId> hand = _actor.GetSeatHandTileIds(0);
            if (hand.Count == 0)
            {
                return;
            }

            var candidates = new List<LegalActionOption>(hand.Count);
            for (int i = 0; i < hand.Count; i++)
            {
                candidates.Add(new LegalActionOption(LegalActionKind.Discard, 0, hand[i]));
            }

            var pending = new PendingDecision(
                new DecisionKey(snapshot.RoundId, 0, "local-human-initial"),
                0L,
                candidates,
                hand[hand.Count - 1]);
            await _actor.OpenDecisionAsync(pending, CancellationToken.None).ConfigureAwait(false);
        }


        internal void OnFrame(TableStreamFrame frame, long generation)
        {
            lock (_gate)
            {
                if (!_sessionOpen || generation != _streamGeneration ||
                    frame.Identity.SessionId.Value != _sessionId)
                {
                    return;
                }

                if (frame.IsFullSnapshot && !string.IsNullOrEmpty(_pendingSyncCorrelation))
                {
                    string correlation = _pendingSyncCorrelation!;
                    _pendingSyncCorrelation = null;
                    Enqueue(BuildSyncResponse(frame, correlation));
                    return;
                }

                Envelope update = BuildPlayerUpdate(frame);
                Enqueue(update);
            }
        }

        private Envelope BuildSyncResponse(TableStreamFrame frame, string correlationId)
        {
            TableStreamContractFrame contract = frame.Contract ??
                throw new InvalidOperationException("The local loopback stream has no contract projection.");
            PlayerSnapshot player = ProjectionWireMapper.MapPlayerSnapshot(contract.Player!);
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.SyncResponse,
                SessionId = contract.SessionId.Value,
                RoomId = contract.RoomId,
                SeatId = contract.Audience.Seat,
                BindingEpoch = contract.BindingEpoch.Value,
                CorrelationId = correlationId,
                Cursor = ProjectionWireMapper.MapCursor(contract.Cursor),
                SyncResponse = new SyncResponse
                {
                    Target = SyncTarget.PlayerStream,
                    IsFullSnapshot = true,
                    Player = player,
                    StateVersion = contract.StateVersion.Value,
                    EventSeq = contract.EventSeq.Value
                }
            };
        }

        private Envelope BuildPlayerUpdate(TableStreamFrame frame)
        {
            TableStreamContractFrame contract = frame.Contract ??
                throw new InvalidOperationException("The local loopback stream has no contract projection.");
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.PlayerUpdate,
                SessionId = contract.SessionId.Value,
                RoomId = contract.RoomId,
                SeatId = contract.Audience.Seat,
                BindingEpoch = contract.BindingEpoch.Value,
                Cursor = ProjectionWireMapper.MapCursor(contract.Cursor),
                PlayerUpdate = ProjectionWireMapper.MapPlayerUpdate(
                    contract.Player!,
                    contract.Events,
                    contract.FromCursor,
                    contract.IsFullSnapshot)
            };
        }

        private async Task HandleCommandAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            if (!IsCurrentBinding(envelope) || envelope.Scope == null || envelope.Command == null)
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.MalformedMessage, "The command is missing its binding/scope/body."));
                return;
            }

            if (envelope.Scope.ScopeId != _connectionScopeId?.Value ||
                !string.Equals(envelope.Scope.AccountId, _connectionAccount, StringComparison.Ordinal))
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.Unauthenticated, "The command scope is not the issued local scope."));
                return;
            }

            byte[] canonical = envelope.Command.CanonicalPayload.ToByteArray();
            if (canonical.Length == 0)
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.MalformedMessage, "The command has no canonical payload."));
                return;
            }

            BusinessPayload payload = BusinessPayload.Retain(canonical);
            LocalCommandPayload document;
            try
            {
                document = LocalCommandPayload.Parse(canonical);
            }
            catch (CanonicalEncodingException exception)
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.MalformedMessage, exception.Message));
                return;
            }

            CommandKind kind = (CommandKind)document.CommandKind;
            RequestId requestId = new RequestId(envelope.RequestId);
            if (requestId.IsEmpty)
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.MalformedMessage, "The command has no request id."));
                return;
            }

            TablePayloadSha256 digest = TablePayloadSha256.Compute(canonical);
            OwnerEpoch owner = new OwnerEpoch(envelope.OwnerEpoch == 0 ? _actorLeaseEpoch() : envelope.OwnerEpoch);
            long controlEpoch = envelope.ControlEpoch == 0 ? 0L : unchecked((long)envelope.ControlEpoch);
            string? pendingDecision = _actor.GetPendingDecisionId(_seat);
            string? suppliedDecision = envelope.Command.ExpectedDecisionId;
            if (!string.IsNullOrEmpty(suppliedDecision) &&
                !string.Equals(suppliedDecision, pendingDecision, StringComparison.Ordinal))
            {
                throw new LocalCommandException(
                    ProtocolErrorCode.DecisionMismatch,
                    "The command decision id is not the current pending decision.");
            }

            DecisionId? decisionId = string.IsNullOrEmpty(pendingDecision)
                ? (DecisionId?)null
                : new DecisionId(pendingDecision);
            StateVersion expected = new StateVersion(envelope.Command.ExpectedStateVersion);
            string api = ApiName(kind);
            if (kind == CommandKind.ClaimControl || kind == CommandKind.ReleaseControl)
            {
                await HandleControlCommandAsync(envelope, document, payload, kind, requestId, owner, expected, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_connectionScopeId.HasValue)
            {
                _store.PrepareReceiptDigest(
                    _connectionAccount,
                    _connectionScopeId.Value,
                    requestId,
                    CanonicalPayloadDigest.Compute(payload).ToHex());
            }


            try
            {
                TableOperationResult result;
                if (IsCheatKind(kind))
                {
                    result = await SubmitCheatAsync(
                        envelope,
                        document,
                        payload,
                        kind,
                        requestId,
                        expected,
                        owner,
                        controlEpoch,
                        decisionId,
                        digest,
                        api,
                        cancellationToken).ConfigureAwait(false);
                    if (result.Status == TableOperationStatus.Rejected &&
                        string.Equals(result.ReasonCode, TableRuntimeReasonCode.DecisionMismatch, StringComparison.Ordinal))
                    {
                        string? latePendingId = _actor.GetPendingDecisionId(_seat);
                        if (!string.IsNullOrEmpty(latePendingId))
                        {
                            result = await SubmitCheatAsync(
                                envelope,
                                document,
                                payload,
                                kind,
                                requestId,
                                expected,
                                owner,
                                controlEpoch,
                                new DecisionId(latePendingId!),
                                digest,
                                api,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                else if (TableSettlementIntent.IsSettlementAction(MapActionKind(document)))
                {
                    result = await SubmitSettlementAsync(
                        envelope,
                        document,
                        payload,
                        requestId,
                        expected,
                        owner,
                        controlEpoch,
                        decisionId,
                        digest,
                        api,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    RoundCommand command = BuildRoundCommand(envelope, document);
                    var request = new TableCommandEnvelope(
                        command,
                        requestId,
                        expected,
                        owner,
                        new ControlEpoch(unchecked((ulong)controlEpoch)),
                        decisionId,
                        _actor.RecoveryGeneration,
                        new RequestScopeId(_connectionScopeId!.Value.Value),
                        api,
                        digest);
                    result = await _actor.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
                }

                await PublishResultAsync(envelope, result, requestId, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (LocalCommandException exception)
            {
                Enqueue(RejectedReceipt(envelope, requestId, expected, exception.Code, exception.Message, payload));
            }
        }

        private static bool IsCheatKind(CommandKind kind)
        {
            return kind == CommandKind.SubmitTileMark ||
                   kind == CommandKind.SubmitSleeveBloom ||
                   kind == CommandKind.SubmitSleeveBloomChoice ||
                   kind == CommandKind.SubmitSleeveBloomOffTurn ||
                   kind == CommandKind.SubmitSleeveBloomOffTurnChoice ||
                   kind == CommandKind.SubmitBeamSwap ||
                   kind == CommandKind.SubmitBeamSwapChoice;
        }

        private async Task<TableOperationResult> SubmitCheatAsync(
            Envelope envelope,
            LocalCommandPayload document,
            BusinessPayload payload,
            CommandKind kind,
            RequestId requestId,
            StateVersion expected,
            OwnerEpoch owner,
            long controlEpoch,
            DecisionId? decisionId,
            TablePayloadSha256 digest,
            string api,
            CancellationToken cancellationToken)
        {
            string roundId = kind != CommandKind.SubmitSleeveBloomChoice &&
                             kind != CommandKind.SubmitSleeveBloomOffTurnChoice &&
                             kind != CommandKind.SubmitBeamSwapChoice &&
                             document.HasField(CommandPayloadFields.RoundIdField)
                ? document.RequireString(CommandPayloadFields.RoundIdField, "roundId")
                : envelope.Command!.ExpectedRoundId;
            if (string.IsNullOrEmpty(roundId))
            {
                throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "The cheat command has no round id.");
            }

            RoundCheatAction action;
            RoundCheatPayload cheatPayload;
            switch (kind)
            {
                case CommandKind.SubmitTileMark:
                    action = RoundCheatAction.SubmitTileMark;
                    IReadOnlyList<uint> tiles = document.RequireRepeatedUInt32(
                        CommandPayloadFields.TileMarkTileIds,
                        "tileIds");
                    var physical = new List<TileId>(tiles.Count);
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        physical.Add(new TileId(unchecked((int)tiles[i])));
                    }

                    cheatPayload = RoundCheatPayload.TileMark("grp-" + requestId.Value, physical);
                    break;
                case CommandKind.SubmitSleeveBloom:
                    action = RoundCheatAction.SubmitSleeveBloom;
                    cheatPayload = RoundCheatPayload.SleeveBloom();
                    break;
                case CommandKind.SubmitSleeveBloomChoice:
                    action = RoundCheatAction.SubmitSleeveBloomChoice;
                    cheatPayload = RoundCheatPayload.SleeveBloomChoice(
                        document.RequireString(CommandPayloadFields.OfferId, "offerId"),
                        MapKeep(document.RequireUInt32(CommandPayloadFields.Choice, "choice")));
                    break;
                case CommandKind.SubmitSleeveBloomOffTurn:
                    action = RoundCheatAction.SubmitSleeveBloomOffTurn;
                    cheatPayload = RoundCheatPayload.OffTurn(
                        document.RequireString(CommandPayloadFields.WindowId, "windowId"));
                    break;
                case CommandKind.SubmitSleeveBloomOffTurnChoice:
                    action = RoundCheatAction.SubmitSleeveBloomOffTurnChoice;
                    cheatPayload = RoundCheatPayload.OffTurnChoice(
                        document.RequireString(CommandPayloadFields.OfferId, "offerId"),
                        MapKeep(document.RequireUInt32(CommandPayloadFields.Choice, "choice")));
                    break;
                case CommandKind.SubmitBeamSwap:
                    action = RoundCheatAction.SubmitBeamSwap;
                    cheatPayload = RoundCheatPayload.BeamSwap();
                    break;
                case CommandKind.SubmitBeamSwapChoice:
                    action = RoundCheatAction.SubmitBeamSwapChoice;
                    uint choice = document.RequireUInt32(CommandPayloadFields.Choice, "choice");
                    BeamSwapAction beamAction = choice == (uint)BeamSwapChoice.Swap
                        ? BeamSwapAction.Swap
                        : BeamSwapAction.KeepOriginal;
                    TileId? handTile = document.HasField(CommandPayloadFields.HandTileId)
                        ? new TileId(unchecked((int)document.RequireUInt32(CommandPayloadFields.HandTileId, "handTileId")))
                        : (TileId?)null;
                    BeamSwapSlot? slot = null;
                    if (document.HasField(CommandPayloadFields.TargetSlot))
                    {
                        slot = BeamSwapSlot.Wall(unchecked((int)document.RequireUInt32(CommandPayloadFields.TargetSlot, "targetSlot")));
                    }

                    cheatPayload = RoundCheatPayload.BeamSwapChoice(
                        document.RequireString(CommandPayloadFields.OfferId, "offerId"),
                        beamAction,
                        handTile,
                        slot);
                    break;
                default:
                    throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "Unsupported cheat command.");
            }

            var cheat = new RoundCheatCommand(roundId, _seat, action, requestId.Value, cheatPayload);
            var request = new TableCheatCommandEnvelope(
                cheat,
                requestId,
                expected,
                owner,
                new ControlEpoch(unchecked((ulong)controlEpoch)),
                decisionId,
                _actor.RecoveryGeneration,
                new RequestScopeId(_connectionScopeId!.Value.Value),
                api,
                digest);
            return await _actor.SubmitCheatAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private static SleeveBloomKeep MapKeep(uint choice)
        {
            return choice == (uint)SleeveBloomChoice.First ? SleeveBloomKeep.First : SleeveBloomKeep.Second;
        }

        private async Task<TableOperationResult> SubmitSettlementAsync(
            Envelope envelope,
            LocalCommandPayload document,
            BusinessPayload payload,
            RequestId requestId,
            StateVersion expected,
            OwnerEpoch owner,
            long controlEpoch,
            DecisionId? decisionId,
            TablePayloadSha256 digest,
            string api,
            CancellationToken cancellationToken)
        {
            RoundActionKind action = MapActionKind(document);
            TableSettlementAction settlementAction;
            switch (action)
            {
                case RoundActionKind.DeclareTsumo:
                    settlementAction = TableSettlementAction.Tsumo;
                    break;
                case RoundActionKind.DeclareRon:
                    settlementAction = TableSettlementAction.Ron;
                    break;
                case RoundActionKind.DeclareExhaustiveDraw:
                    settlementAction = TableSettlementAction.ExhaustiveDraw;
                    break;
                case RoundActionKind.DeclareAbortiveDraw:
                    settlementAction = TableSettlementAction.AbortiveDraw;
                    break;
                default:
                    throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "Unsupported settlement action.");
            }

            string roundId = document.HasField(CommandPayloadFields.RoundIdField)
                ? document.RequireString(CommandPayloadFields.RoundIdField, "roundId")
                : envelope.Command.ExpectedRoundId;
            if (string.IsNullOrEmpty(roundId))
            {
                throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "The settlement has no round id.");
            }

            SettlementAbortiveDrawReason? abortive = null;
            if (settlementAction == TableSettlementAction.AbortiveDraw)
            {
                abortive = SettlementAbortiveDrawReason.FourWinds;
            }

            var intent = new TableSettlementIntent(roundId, settlementAction, _seat, abortive);
            var request = new TableSettlementRequest(
                intent,
                requestId,
                expected,
                owner,
                new ControlEpoch(unchecked((ulong)controlEpoch)),
                decisionId,
                _actor.RecoveryGeneration,
                new RequestScopeId(_connectionScopeId!.Value.Value),
                api,
                digest);
            return await _actor.SubmitSettlementAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private async Task HandleControlCommandAsync(
            Envelope envelope,
            LocalCommandPayload document,
            BusinessPayload payload,
            CommandKind kind,
            RequestId requestId,
            OwnerEpoch owner,
            StateVersion expected,
            CancellationToken cancellationToken)
        {
            string requestKey = requestId.Value;
            string canonical = CanonicalPayloadDigest.Compute(payload).ToHex();
            if (_connectionScopeId.HasValue)
            {
                LocalControlRecord? prior = _store.GetControlResult(
                    _connectionAccount,
                    _connectionScopeId.Value,
                    requestId);
                if (prior != null)
                {
                    if (!string.Equals(prior.Digest, canonical, StringComparison.Ordinal) ||
                        !string.Equals(prior.Kind, kind.ToString(), StringComparison.Ordinal))
                    {
                        Enqueue(RejectedReceipt(
                            envelope,
                            requestId,
                            expected,
                            ProtocolErrorCode.RequestIdConflict,
                            "The same control request id was used with different bytes or kind.",
                            payload));
                        return;
                    }

                    Enqueue(ReceiptEnvelope(
                        envelope,
                        requestId,
                        new WireReceipt
                        {
                            Status = WireReceiptStatus.Recorded,
                            ReasonCode = "LOCAL_CONTROL",
                            EvaluatedStateVersion = prior.EvaluatedStateVersion,
                            CommandVersion = 0UL,
                            EventSeq = 0UL,
                            CanonicalPayloadSha256 = prior.Digest
                        },
                        payload));
                    return;
                }
            }
            if (!document.HasField(CommandPayloadFields.ExpectedControlEpochV2))
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.MalformedMessage, "The control command has no expected epoch."));
                return;
            }

            ulong epoch = document.RequireUInt64(CommandPayloadFields.ExpectedControlEpochV2, "expectedControlEpoch");
            if (_connectionScopeId.HasValue)
            {
                _store.PrepareControlResult(
                    _connectionAccount,
                    _connectionScopeId.Value,
                    requestId,
                    canonical,
                    kind.ToString());
            }

            TableControlResult control = kind == CommandKind.ClaimControl
                ? await _actor.ReclaimControlAsync(
                    _seat,
                    owner,
                    new ControlEpoch(epoch),
                    "LOCAL_CLAIM",
                    cancellationToken).ConfigureAwait(false)
                : await _actor.RequestTakeoverAsync(
                    _seat,
                    owner,
                    new ControlEpoch(epoch),
                    "LOCAL_RELEASE",
                    cancellationToken).ConfigureAwait(false);
            if (control.Status != TableControlStatus.Changed && control.Status != TableControlStatus.Unchanged)
            {
                Enqueue(RejectedReceipt(
                    envelope,
                    requestId,
                    expected,
                    string.IsNullOrEmpty(control.ReasonCode) ? "CONTROL_REJECTED" : control.ReasonCode,
                    control.Detail,
                    payload));
                _store.ClearPreparedControlResult();
                return;
            }
            if (control.Status == TableControlStatus.Unchanged)
            {
                try
                {
                    _store.CommitPreparedControlResultOnly();
                }
                catch (LocalTableException)
                {
                    Enqueue(RejectedReceipt(
                        envelope,
                        requestId,
                        expected,
                        ProtocolErrorCode.InternalError,
                        "The unchanged control result could not be durably recorded.",
                        payload));
                    return;
                }
            }


            var receipt = new WireReceipt
            {
                Status = WireReceiptStatus.Recorded,
                ReasonCode = "LOCAL_CONTROL",
                EvaluatedStateVersion = control.EvaluatedStateVersion.Value,
                CommandVersion = 0UL,
                EventSeq = 0UL,
                CanonicalPayloadSha256 = canonical
            };

            Enqueue(ReceiptEnvelope(envelope, requestId, receipt, payload));
        }

        private async Task PublishResultAsync(
            Envelope source,
            TableOperationResult result,
            RequestId requestId,
            BusinessPayload payload,
            CancellationToken cancellationToken)
        {
            if (result.Status == TableOperationStatus.Accepted || result.Status == TableOperationStatus.Replayed)
            {
                Enqueue(AppliedReceipt(source, requestId, result, payload));
                return;
            }

            if (result.Status == TableOperationStatus.Rejected)
            {
                Enqueue(RejectedReceipt(source, requestId, result.EvaluatedStateVersion, result.ReasonCode, result.Detail, payload));
                return;
            }

            Enqueue(ErrorEnvelope(
                string.IsNullOrEmpty(result.ReasonCode) ? ProtocolErrorCode.InternalError : result.ReasonCode,
                "The local actor refused the command: " + result.Detail));
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private Envelope AppliedReceipt(
            Envelope source,
            RequestId requestId,
            TableOperationResult result,
            BusinessPayload payload)
        {
            TableCommandReceipt? receipt = result.Receipt;
            if (receipt == null)
            {
                return RejectedReceipt(
                    source,
                    requestId,
                    result.EvaluatedStateVersion,
                    ProtocolErrorCode.ProtocolViolation,
                    "The accepted result has no command receipt.",
                    payload);
            }

            return ReceiptEnvelope(
                source,
                requestId,
                new WireReceipt
                {
                    Status = WireReceiptStatus.Applied,
                    ReasonCode = receipt.ReasonCode,
                    EvaluatedStateVersion = receipt.EvaluatedStateVersion.Value,
                    CommandVersion = receipt.CommandVersion.HasValue
                        ? receipt.CommandVersion.Value.Value
                        : 0UL,
                    EventSeq = receipt.LastEventSeq.HasValue
                        ? receipt.LastEventSeq.Value.Value
                        : 0UL,
                    Replayed = result.Replayed,
                    CanonicalPayloadSha256 = CanonicalPayloadDigest.Compute(payload).ToHex()
                },
                payload);
        }

        private Envelope RejectedReceipt(
            Envelope source,
            RequestId requestId,
            StateVersion evaluated,
            string reasonCode,
            string detail,
            BusinessPayload payload)
        {
            return ReceiptEnvelope(
                source,
                requestId,
                new WireReceipt
                {
                    Status = WireReceiptStatus.Rejected,
                    ReasonCode = string.IsNullOrEmpty(reasonCode) ? ProtocolErrorCode.ProtocolViolation : reasonCode,
                    EvaluatedStateVersion = evaluated.Value,
                    CanonicalPayloadSha256 = CanonicalPayloadDigest.Compute(payload).ToHex()
                },
                payload);
        }

        private Envelope ReceiptEnvelope(Envelope source, RequestId requestId, WireReceipt receipt, BusinessPayload payload)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Receipt,
                SessionId = _sessionId,
                RoomId = _options.RoomId,
                SeatId = _seat,
                BindingEpoch = _bindingEpoch,
                RequestId = requestId.Value,
                Scope = source.Scope,
                Receipt = receipt
            };
        }

        private async Task HandleReceiptQueryAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            if (envelope.ReceiptQuery == null ||
                string.IsNullOrEmpty(envelope.CorrelationId) ||
                string.IsNullOrEmpty(envelope.ReceiptQuery.RequestScopeId) ||
                string.IsNullOrEmpty(envelope.ReceiptQuery.RequestId))
            {
                Enqueue(ErrorEnvelope(ProtocolErrorCode.MalformedMessage, "The receipt query is malformed."));
                return;
            }

            if (!string.IsNullOrEmpty(envelope.SessionId) && !IsCurrentBinding(envelope))
            {
                return;
            }

            RequestScopeId queryScopeId = new RequestScopeId(envelope.ReceiptQuery.RequestScopeId);
            TableDurableReceipt? receipt;
            try
            {
                receipt = await _store.TryGetAsync(
                    _connectionAccount,
                    queryScopeId,
                    new RequestId(envelope.ReceiptQuery.RequestId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LocalTableException)
            {
                Enqueue(ReceiptLookupEnvelope(
                    envelope.CorrelationId,
                    new ReceiptLookup { Status = LookupStatus.Unknown, CanonicalPayloadSha256 = string.Empty }));
                return;
            }

            string canonicalDigest = receipt == null
                ? string.Empty
                : (_store.GetReceiptDigest(
                    _connectionAccount,
                    queryScopeId,
                    new RequestId(envelope.ReceiptQuery.RequestId)) ?? string.Empty);
            if (receipt == null && _connectionScopeId.HasValue)
            {
                LocalControlRecord? control = _store.GetControlResult(
                    _connectionAccount,
                    queryScopeId,
                    new RequestId(envelope.ReceiptQuery.RequestId));
                if (control != null)
                {
                    Enqueue(ReceiptLookupEnvelope(
                        envelope.CorrelationId,
                        new ReceiptLookup
                        {
                            Status = LookupStatus.Found,
                            CanonicalPayloadSha256 = control.Digest,
                            Receipt = new WireReceipt
                            {
                                Status = WireReceiptStatus.Recorded,
                                ReasonCode = "LOCAL_CONTROL",
                                EvaluatedStateVersion = control.EvaluatedStateVersion,
                                CommandVersion = 0UL,
                                EventSeq = 0UL,
                                CanonicalPayloadSha256 = control.Digest
                            }
                        }));
                    return;
                }
            }

            var lookup = new ReceiptLookup
            {
                Status = receipt == null ? LookupStatus.NotFound : LookupStatus.Found,
                CanonicalPayloadSha256 = canonicalDigest
            };
            if (receipt != null)
            {
                lookup.Receipt = new WireReceipt
                {
                    Status = receipt.Receipt.Status == Mahjong.Contracts.ReceiptStatus.Rejected
                        ? WireReceiptStatus.Rejected
                        : WireReceiptStatus.Applied,
                    ReasonCode = receipt.Receipt.ReasonCode,
                    EvaluatedStateVersion = receipt.Receipt.EvaluatedStateVersion.Value,
                    CommandVersion = receipt.Receipt.CommandVersion.HasValue
                        ? receipt.Receipt.CommandVersion.Value.Value
                        : 0UL,
                    EventSeq = receipt.Receipt.EventSeq.HasValue
                        ? receipt.Receipt.EventSeq.Value.Value
                        : 0UL,
                    CanonicalPayloadSha256 = canonicalDigest
                };
            }

            Enqueue(ReceiptLookupEnvelope(envelope.CorrelationId, lookup));
        }

        private Envelope ReceiptLookupEnvelope(string correlationId, ReceiptLookup lookup)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.ReceiptLookup,
                SessionId = _sessionId,
                RoomId = _options.RoomId,
                SeatId = _seat,
                BindingEpoch = _bindingEpoch,
                CorrelationId = correlationId,
                ReceiptLookup = lookup
            };
        }

        private RoundCommand BuildRoundCommand(Envelope envelope, LocalCommandPayload document)
        {
            RoundActionKind action = MapActionKind(document);
            string roundId = document.HasField(CommandPayloadFields.RoundIdField)
                ? document.RequireString(CommandPayloadFields.RoundIdField, "roundId")
                : envelope.Command!.ExpectedRoundId;
            if (string.IsNullOrEmpty(roundId))
            {
                throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "The action has no round id.");
            }


            switch (action)
            {
                case RoundActionKind.DrawTile:
                    return RoundCommand.Draw(roundId, _seat);
                case RoundActionKind.DiscardTile:
                    if (!document.HasField(CommandPayloadFields.TileIds))
                    {
                        throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "Discard requires a tile id.");
                    }

                    IReadOnlyList<uint> tiles = document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds");
                    if (tiles.Count != 1)
                    {
                        throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "Discard requires exactly one tile id.");
                    }

                    return RoundCommand.Discard(roundId, _seat, new TileId(unchecked((int)tiles[0])));
                case RoundActionKind.Pass:
                    return RoundCommand.Pass(roundId, _seat);
                case RoundActionKind.DeclareRiichi:
                    if (!document.HasField(CommandPayloadFields.TileIds))
                    {
                        throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "Riichi requires its discard tile.");
                    }

                    IReadOnlyList<uint> riichiTiles = document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds");
                    if (riichiTiles.Count != 1)
                    {
                        throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "Riichi requires exactly one discard tile.");
                    }

                    return RoundCommand.Riichi(roundId, _seat, new TileId(unchecked((int)riichiTiles[0])), null);
                case RoundActionKind.DeclareChi:
                {
                    IReadOnlyList<uint> meld = RequireMeldTiles(document, "Chi", 3);
                    return RoundCommand.Chi(
                        roundId,
                        _seat,
                        new TileId(unchecked((int)meld[0])),
                        new TileId(unchecked((int)meld[1])),
                        new TileId(unchecked((int)meld[2])));
                }

                case RoundActionKind.DeclarePon:
                {
                    IReadOnlyList<uint> meld = RequireMeldTiles(document, "Pon", 3);
                    return RoundCommand.Pon(
                        roundId,
                        _seat,
                        new TileId(unchecked((int)meld[0])),
                        new TileId(unchecked((int)meld[1])),
                        new TileId(unchecked((int)meld[2])));
                }

                case RoundActionKind.DeclareOpenKan:
                case RoundActionKind.DeclareClosedKan:
                case RoundActionKind.DeclareAddedKan:
                {
                    uint meldType = document.HasField(CommandPayloadFields.MeldType)
                        ? document.RequireUInt32(CommandPayloadFields.MeldType, "meldType")
                        : 0U;
                    if (action == RoundActionKind.DeclareClosedKan || meldType == 4U)
                    {
                        IReadOnlyList<uint> kan = RequireMeldTiles(document, "ClosedKan", 4);
                        return RoundCommand.ClosedKan(
                            roundId,
                            _seat,
                            new TileId(unchecked((int)kan[0])),
                            new TileId(unchecked((int)kan[1])),
                            new TileId(unchecked((int)kan[2])),
                            new TileId(unchecked((int)kan[3])));
                    }

                    if (action == RoundActionKind.DeclareAddedKan || meldType == 5U)
                    {
                        IReadOnlyList<uint> added = RequireMeldTiles(document, "AddedKan", 1);
                        return RoundCommand.AddedKan(roundId, _seat, new TileId(unchecked((int)added[0])));
                    }

                    IReadOnlyList<uint> open = RequireMeldTiles(document, "OpenKan", 4);
                    return RoundCommand.OpenKan(

                        roundId,
                        _seat,
                        new TileId(unchecked((int)open[0])),
                        new TileId(unchecked((int)open[1])),
                        new TileId(unchecked((int)open[2])),
                        new TileId(unchecked((int)open[3])));
                }

                default:
                    throw new LocalCommandException(
                        ProtocolErrorCode.MalformedMessage,
                        "The local host does not accept action " + action + " as a plain round command.");
            }
        }

        private static IReadOnlyList<uint> RequireMeldTiles(
            LocalCommandPayload document,
            string name,
            int count)
        {
            if (!document.HasField(CommandPayloadFields.TileIds))
            {
                throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, name + " requires tile ids.");
            }

            IReadOnlyList<uint> tiles = document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds");
            if (tiles.Count != count)
            {
                throw new LocalCommandException(
                    ProtocolErrorCode.MalformedMessage,
                    name + " requires exactly " + count + " tile ids.");
            }

            return tiles;
        }

        private static RoundActionKind MapActionKind(LocalCommandPayload document)
        {
            uint action = document.HasField(CommandPayloadFields.ActionType)
                ? document.RequireUInt32(CommandPayloadFields.ActionType, "actionType")
                : 0U;
            switch ((ProtocolActionType)action)
            {
                case ProtocolActionType.DrawTile:
                    return RoundActionKind.DrawTile;
                case ProtocolActionType.DiscardTile:
                    return RoundActionKind.DiscardTile;
                case ProtocolActionType.Pass:
                    return RoundActionKind.Pass;
                case ProtocolActionType.DeclareTsumo:
                    return RoundActionKind.DeclareTsumo;
                case ProtocolActionType.DeclareRon:
                    return RoundActionKind.DeclareRon;
                case ProtocolActionType.DeclareRiichi:
                    return RoundActionKind.DeclareRiichi;
                case ProtocolActionType.DeclareChi:
                    return RoundActionKind.DeclareChi;
                case ProtocolActionType.DeclarePon:
                    return RoundActionKind.DeclarePon;
                case ProtocolActionType.DeclareKan:
                    return RoundActionKind.DeclareOpenKan;
                case ProtocolActionType.DeclareAbortiveDraw:
                    return RoundActionKind.DeclareAbortiveDraw;
                default:
                    throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "Unknown action type.");
            }
        }

        private static string ApiName(CommandKind kind)
        {
            switch (kind)
            {
                case CommandKind.SubmitAction: return "SUBMIT_ACTION";
                case CommandKind.SubmitTileMark: return "SUBMIT_TILE_MARK";
                case CommandKind.SubmitSleeveBloom: return "SUBMIT_SLEEVE_BLOOM";
                case CommandKind.SubmitSleeveBloomChoice: return "SUBMIT_SLEEVE_BLOOM_CHOICE";
                case CommandKind.SubmitSleeveBloomOffTurn: return "SUBMIT_SLEEVE_BLOOM_OFFTURN";
                case CommandKind.SubmitSleeveBloomOffTurnChoice: return "SUBMIT_SLEEVE_BLOOM_OFFTURN_CHOICE";
                case CommandKind.SubmitBeamSwap: return "SUBMIT_BEAM_SWAP";
                case CommandKind.SubmitBeamSwapChoice: return "SUBMIT_BEAM_SWAP_CHOICE";
                default: return "SUBMIT_ACTION";
            }
        }

        private bool IsCurrentBinding(Envelope envelope)
        {
            lock (_gate)
            {
                return _sessionOpen &&
                       envelope.SessionId == _sessionId &&
                       envelope.RoomId == _options.RoomId &&
                       envelope.SeatId == _seat &&
                       envelope.BindingEpoch == _bindingEpoch;
            }
        }

        private ulong _actorLeaseEpoch()
        {
            return _store.LeaseEpoch.Value;
        }

        private void Enqueue(Envelope envelope)
        {
            try
            {
                WireCodec.EnsureStrict(envelope);
                byte[] bytes = WireCodec.Encode(envelope);
                _outbound.Enqueue(bytes);
            }
            catch (Exception exception)
            {
                byte[] fallback = WireCodec.Encode(ErrorEnvelope(
                    ProtocolErrorCode.InternalError,
                    "The local loopback response was rejected: " + exception.Message));
                _outbound.Enqueue(fallback);
            }
        }

        private Envelope ErrorEnvelope(string code, string detail)
        {
            string stable = ProtocolErrorCode.IsWellFormed(code) ? code : ProtocolErrorCode.InternalError;
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Error,
                SessionId = _sessionId,
                RoomId = _options.RoomId,
                SeatId = _seat,
                BindingEpoch = _bindingEpoch,
                Error = new ProtocolError
                {
                    Code = stable,
                    Retryable = false,
                    DetailCode = detail.Length > 128 ? detail.Substring(0, 128) : detail
                }
            };
        }

        private Envelope PongEnvelope(Envelope ping)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Heartbeat,
                SessionId = _sessionId,
                RoomId = _options.RoomId,
                SeatId = _seat,
                BindingEpoch = _bindingEpoch,
                Heartbeat = new Heartbeat { Pong = new Pong { Nonce = ping.Heartbeat.Ping.Nonce } }
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _driver.StopAsync().ConfigureAwait(false);
            await CloseSessionAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _actor.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Dispose still releases the durable lock.
            }

            await _actor.DisposeAsync().ConfigureAwait(false);
            _store.Dispose();
        }

        private sealed class LocalCommandException : Exception
        {
            public LocalCommandException(string code, string message) : base(message)
            {
                Code = code;
            }

            public string Code { get; }
        }

        private sealed class NoopCommitPort : ITableCommitPort
        {
            public ValueTask<TableCommitResult> CommitAsync(TableCommitRequest request, CancellationToken cancellationToken) =>
                new ValueTask<TableCommitResult>(TableCommitResult.Failed("LOCAL_NO_LEGACY_PORT", "The local host uses the durable port."));
        }

        private sealed class NoopPublicationSink : ITablePublicationSink
        {
            public void Publish(TablePublication publication)
            {
            }
        }

        private sealed class LocalRecoverySink : ITableRecoveryStateSink
        {
            private readonly string _roomId;

            public LocalRecoverySink(string roomId)
            {
                _roomId = roomId;
            }

            public TableRecoveryAuxState? Last { get; private set; }

            public void Install(TableRecoveryAuxState state)
            {
                if (state == null)
                {
                    throw new ArgumentNullException(nameof(state));
                }

                if (!string.Equals(state.RoomId, _roomId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Recovery state belongs to another room.");
                }

                Last = state;
            }
        }

        private sealed class TableStreamSink : ITableStreamSink
        {
            private readonly LocalTableRuntime _runtime;
            private readonly long _generation;

            public TableStreamSink(LocalTableRuntime runtime, long generation)
            {
                _runtime = runtime;
                _generation = generation;
            }

            public TableStreamFrame? LastFrame { get; private set; }

            public ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
            {
                LastFrame = frame;
                _runtime.OnFrame(frame, _generation);
                return default;
            }
        }
    }

    /// <summary>Frozen Contracts -> strict wire codec used only by the loopback bridge.</summary>
    internal sealed class LocalStreamContractCodec : ITableStreamContractCodec
    {
        public byte[] Encode(TableStreamContractFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            Envelope envelope = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.PlayerUpdate,
                SessionId = frame.SessionId.Value,
                RoomId = frame.RoomId,
                SeatId = frame.Audience.Seat,
                BindingEpoch = frame.BindingEpoch.Value,
                Cursor = ProjectionWireMapper.MapCursor(frame.Cursor),
                PlayerUpdate = ProjectionWireMapper.MapPlayerUpdate(
                    frame.Player!,
                    frame.Events,
                    frame.FromCursor,
                    frame.IsFullSnapshot)
            };
            WireCodec.EnsureStrict(envelope);
            byte[] bytes = WireCodec.Encode(envelope);
            WireDecodeResult decoded = WireCodec.Decode(bytes);
            if (!decoded.IsOk || decoded.Envelope == null)
            {
                throw new WireProtocolException(
                    "The local codec could not decode its own output: " + decoded.ErrorCode,
                    decoded.ErrorCode);
            }

            return bytes;
        }
    }

    /// <summary>Async single-consumer frame queue with independent waiter cancellation.</summary>
    internal sealed class AsyncFrameQueue
    {
        private readonly object _gate = new object();
        private readonly Queue<byte[]> _frames = new Queue<byte[]>();
        private readonly LinkedList<Waiter> _waiters = new LinkedList<Waiter>();
        private Exception? _failure;
        private readonly int _maxFrames;
        private readonly int _maxBytes;
        private long _bytes;

        public AsyncFrameQueue(int maxFrames = 256, int maxBytes = 1024 * 1024)
        {
            if (maxFrames < 1 || maxBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFrames));
            }

            _maxFrames = maxFrames;
            _maxBytes = maxBytes;
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _frames.Count;
                }
            }
        }

        public void Enqueue(byte[] frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            LinkedListNode<Waiter>? waiter = null;
            lock (_gate)
            {
                if (_failure != null)
                {
                    throw new TableChannelClosedException(ProtocolErrorCode.LocalClosed, true, _failure);
                }

                if (_waiters.Count > 0)
                {
                    waiter = _waiters.First;
                    _waiters.RemoveFirst();
                }
                else
                {
                    if (_frames.Count >= _maxFrames || _bytes + frame.Length > _maxBytes)
                    {
                        _failure = new TableChannelClosedException(
                            "LOCAL_LOOPBACK_OVERFLOW",
                            false);
                        _frames.Clear();
                        _bytes = 0;
                        return;
                    }

                    _frames.Enqueue(frame);
                    _bytes += frame.Length;
                    return;
                }
            }

            waiter!.Value.Completion.TrySetResult(frame);
        }

        public void Fail(Exception exception)
        {
            List<Waiter> waiters;
            lock (_gate)
            {
                _failure = exception;
                waiters = new List<Waiter>(_waiters);
                _waiters.Clear();
                _frames.Clear();
                _bytes = 0;
            }

            for (int i = 0; i < waiters.Count; i++)
            {
                waiters[i].Completion.TrySetException(exception);
            }
        }

        public void Reset()
        {
            List<Waiter> waiters;
            lock (_gate)
            {
                _failure = null;
                _frames.Clear();
                waiters = new List<Waiter>(_waiters);
                _waiters.Clear();
            }

            for (int i = 0; i < waiters.Count; i++)
            {
                waiters[i].Completion.TrySetException(
                    new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false));
            }
        }

        public Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinkedListNode<Waiter> node;
            var waiter = new Waiter();
            lock (_gate)
            {
                if (_frames.Count > 0)
                {
                    return Task.FromResult(_frames.Dequeue());
                }

                if (_failure != null)
                {
                    return Task.FromException<byte[]>(
                        new TableChannelClosedException(ProtocolErrorCode.LocalClosed, false, _failure));
                }

                node = _waiters.AddLast(waiter);
            }

            if (cancellationToken.CanBeCanceled)
            {
                CancellationTokenRegistration registration = cancellationToken.Register(
                    () =>
                    {
                        bool removed;
                        lock (_gate)
                        {
                            removed = node.List != null;
                            if (removed)
                            {
                                _waiters.Remove(node);
                            }
                        }

                        if (removed)
                        {
                            waiter.Completion.TrySetCanceled(cancellationToken);
                        }
                    });
                waiter.Registration = registration;
            }

            return AwaitAsync(waiter);
        }

        private static async Task<byte[]> AwaitAsync(Waiter waiter)
        {
            try
            {
                return await waiter.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                waiter.Registration.Dispose();
            }
        }

        private sealed class Waiter
        {
            public TaskCompletionSource<byte[]> Completion { get; } =
                new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            public CancellationTokenRegistration Registration { get; set; }
        }
    }
}
