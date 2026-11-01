using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
        private readonly SemaphoreSlim _controlGate = new SemaphoreSlim(1, 1);
        private readonly bool[] _temporaryFuriten;
        private readonly bool[] _riichiFuriten;
        private readonly LocalTableHostOptions _options;
        private readonly LocalHostEnvironment _environment;
        private readonly LocalTableStore _store;
        private readonly TableActor _actor;
        private readonly LocalSnapshotFacts _facts;
        private readonly LocalTableDriver _driver;
        private readonly AsyncFrameQueue _outbound = new AsyncFrameQueue();

        // Diagnostic-only view of the already-committed actor aggregate. TableActor does not
        // expose the private cheat state publicly; the recovery probe needs the same committed
        // aggregate as the live authority, not the older base checkpoint. It is read only by the
        // opt-in capture path and never by normal gameplay.
        private static readonly FieldInfo? ActorCheatField =
            typeof(TableActor).GetField("_cheat", BindingFlags.Instance | BindingFlags.NonPublic);

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
            LocalSnapshotFacts facts,
            bool[] temporaryFuriten,
            bool[] riichiFuriten,
            bool deferDriver)
        {
            _options = options;
            _environment = environment;
            _store = store;
            _actor = actor;
            _facts = facts;
            _temporaryFuriten = temporaryFuriten;
            _riichiFuriten = riichiFuriten;

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

            _driver = new LocalTableDriver(actor, options, store, scopes, () => _sessionOpen, _temporaryFuriten, _riichiFuriten);
            if (options.AutoPlay && !deferDriver)
            {
                _driver.Start();
            }
        }



        public static async ValueTask<LocalTableRuntime> OpenAsync(
            LocalTableHostOptions options,
            CancellationToken cancellationToken)
        {
            LocalHostEnvironment environment = options.Environment ?? LocalHostEnvironment.System;
            ITableClock clock = environment.ResolveClock();
            LocalTableStore store = await LocalTableStore.OpenAsync(options, clock, options.FileSystemForTest, cancellationToken)
                .ConfigureAwait(false);
            bool[] temporaryFuriten = store.LoadTemporaryFuriten();
            bool[] riichiFuriten = store.LoadRiichiFuriten();
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
                    actor = CreateActor(options, environment, clock, store, facts, round, checkpoint, recover: true, temporaryFuriten, riichiFuriten);
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
                    actor = CreateActor(options, environment, clock, store, facts, round, checkpoint, recover: false, temporaryFuriten, riichiFuriten);
                    facts.SettlementProvider = () => actor.LastSettlementCalculation;
                }

                // The actor is the authority for furiten. A recovered actor has already installed
                // the last committed snapshot; mirror it once here so candidate derivation and the
                // settlement fact port start from exactly the durable facts.
                TableFuritenSnapshot committedFuriten = actor.FuritenForHost;
                for (int seat = 0; seat < 4; seat++)
                {
                    temporaryFuriten[seat] = committedFuriten.IsTemporary(seat);
                    riichiFuriten[seat] = committedFuriten.IsRiichi(seat);
                }

                string diagnosticDirectory = LocalTableHostPaths.ResolveSaveDirectory(options.SaveDirectory);
                bool pendingCapture = LocalTableDiagnosticRegistry.HasPendingCapture(diagnosticDirectory);
                var runtime = new LocalTableRuntime(
                    options,
                    environment,
                    store,
                    actor,
                    facts,
                    temporaryFuriten,
                    riichiFuriten,
                    pendingCapture);
                if (pendingCapture)
                {
                    try
                    {
                        LocalTableRecoveryCapture capture = await runtime.CaptureRecoveryAsync(cancellationToken).ConfigureAwait(false);
                        LocalTableDiagnosticRegistry.CompletePendingCapture(diagnosticDirectory, capture);
                    }
                    catch (Exception exception)
                    {
                        LocalTableDiagnosticRegistry.FailPendingCapture(diagnosticDirectory, exception);
                    }

                    // The recovery probe is read-only: keeping the autonomous driver deferred
                    // prevents a post-capture AI command from changing the baseline before the
                    // process exits. Production runs never enter this branch.
                }

                LocalTableDiagnosticRegistry.Register(runtime.DiagnosticSaveDirectory, runtime);
                return runtime;
            }
            catch
            {
                store.Dispose();
                if (LocalTableDiagnosticRegistry.HasPendingCapture(LocalTableHostPaths.ResolveSaveDirectory(options.SaveDirectory)))
                {
                    // The capture task would otherwise wait forever after a failed open.
                    LocalTableDiagnosticRegistry.FailPendingCapture(
                        LocalTableHostPaths.ResolveSaveDirectory(options.SaveDirectory),
                        new LocalTableException(LocalTableErrorKind.CorruptState, "The local host did not open for the prepared capture."));
                }

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
            bool recover,
            bool[] temporaryFuriten,
            bool[] riichiFuriten)
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
                InitialCheatState = LocalSnapshotFacts.InitialCheatState,
                FuritenCommitted = snapshot =>
                {
                    for (int seat = 0; seat < 4; seat++)
                    {
                        temporaryFuriten[seat] = snapshot.IsTemporary(seat);
                        riichiFuriten[seat] = snapshot.IsRiichi(seat);
                    }
                }
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
            durable.ReceiptRetention = RejectedReceiptRetention;

            var streamOptions = new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(new LocalStreamContractCodec()),
                MaxQueuedFrames = 128,
                TerminationSink = new DebugTerminationSink()
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
            settlementFacts.TemporaryFuritenProvider = seat => temporaryFuriten![seat];
            settlementFacts.RiichiFuritenProvider = seat => riichiFuriten![seat];
            facts.FuritenProvider = seat => temporaryFuriten![seat] || riichiFuriten![seat];
            facts.TemporaryFuritenProvider = seat => temporaryFuriten![seat];
            facts.RiichiFuritenProvider = seat => riichiFuriten![seat];
            return actor;
        }

        internal Mahjong.Domain.RoundResult? CurrentRoundResult => _actor.GetRoundResult();
        internal TableActor ActorForTest => _actor;

        internal LocalTableStore StoreForTest => _store;

        internal bool DriverRunning => _driver.IsRunning;

        internal LocalTableDriver DriverForTest => _driver;

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
                LastCommandException = exception;
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

            RoundStateMachine round = _actor.GetRoundStateForHost();
            if (round.IsFinished)
            {
                return;
            }

            TableSnapshot snapshot = await _actor.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            if (snapshot.Phase == RoundPhase.CheatWindow || snapshot.Phase == RoundPhase.Discard)
            {
                if (snapshot.CurrentSeat != 0)
                {
                    return;
                }

                IReadOnlyList<LegalActionOption> candidates = LocalCandidateProvider.DeriveTurnCandidates(
                    round,
                    0,
                    _temporaryFuriten,
                    _riichiFuriten);
                if (candidates.Count == 0)
                {
                    return;
                }

                TileId? drawn = round.Tiles.DrawnTile?.Seat == 0 ? round.Tiles.DrawnTile?.Tile : null;
                var pending = new PendingDecision(
                    new DecisionKey(snapshot.RoundId, 0, "local-human-initial"),
                    0L,
                    candidates,
                    drawn);
                await _actor.OpenDecisionAsync(pending, CancellationToken.None).ConfigureAwait(false);
            }
            else if (snapshot.Phase == RoundPhase.ResponseWindow || snapshot.Phase == RoundPhase.ChankanWindow)
            {
                RoundResponseWindow? window = round.Window;
                if (window != null && window.EligibleSeats.Contains(0) && !window.HasResponded(0))
                {
                    IReadOnlyList<LegalActionOption> candidates = LocalCandidateProvider.DeriveResponseCandidates(
                        round,
                        0,
                        window,
                        _temporaryFuriten,
                        _riichiFuriten);
                    if (candidates.Count == 0)
                    {
                        return;
                    }

                    var pending = new PendingDecision(
                        new DecisionKey(snapshot.RoundId, 0, "local-human-initial-response"),
                        0L,
                        candidates,
                        null);
                    await _actor.OpenDecisionAsync(pending, CancellationToken.None).ConfigureAwait(false);
                }
            }
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
            StateVersion expected = new StateVersion(envelope.Command.ExpectedStateVersion);

            // Control commands must consult their durable idempotency record before any
            // validation that depends on mutable authority state (the current decision id).
            // Otherwise an exact replay of a committed request changes from the original
            // RECORDED receipt to DECISION_MISMATCH once the decision retires.
            if (kind == CommandKind.ClaimControl || kind == CommandKind.ReleaseControl)
            {
                await HandleControlCommandAsync(envelope, document, payload, kind, requestId, owner, expected, cancellationToken).ConfigureAwait(false);
                return;
            }

            string? pendingDecision = _actor.GetPendingDecisionId(_seat);
            if (await RejectStaleDecisionAsync(
                    envelope,
                    kind,
                    requestId,
                    expected,
                    payload,
                    pendingDecision,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            string suppliedDecision = envelope.Command?.ExpectedDecisionId ?? string.Empty;
            DecisionId? decisionId = string.IsNullOrEmpty(pendingDecision)
                ? (DecisionId?)null
                : new DecisionId(pendingDecision);
            string api = ApiName(kind);

            if (_connectionScopeId.HasValue)
            {
                _store.PrepareReceiptDigest(
                    _connectionAccount,
                    _connectionScopeId.Value,
                    requestId,
                    CanonicalPayloadDigest.Compute(payload).ToHex());
            }


            RoundActionKind hostAction = kind == CommandKind.SubmitAction ? MapActionKind(document) : RoundActionKind.Pass;
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
                        string.Equals(result.ReasonCode, TableRuntimeReasonCode.DecisionMismatch, StringComparison.Ordinal) &&
                        string.IsNullOrEmpty(suppliedDecision))
                    {
                        string? latePendingId = _actor.GetPendingDecisionId(_seat);
                        result = await SubmitCheatAsync(
                            envelope,
                            document,
                            payload,
                            kind,
                            new RequestId(requestId.Value + "-retry"),
                            expected,
                            owner,
                            controlEpoch,
                            string.IsNullOrEmpty(latePendingId) ? null : new DecisionId(latePendingId!),
                            digest,
                            api,
                            cancellationToken).ConfigureAwait(false);
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
                else if (hostAction == RoundActionKind.DrawTile && _actor.GetRoundStateForHost().Tiles.WallRemaining == 0)
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
                        "SUBMIT_EXHAUSTIVE_DRAW",
                        cancellationToken,
                        overrideSettlementAction: TableSettlementAction.ExhaustiveDraw).ConfigureAwait(false);
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
                    if (result.Status == TableOperationStatus.Rejected &&
                        string.Equals(result.ReasonCode, TableRuntimeReasonCode.DecisionMismatch, StringComparison.Ordinal) &&
                        string.IsNullOrEmpty(suppliedDecision))
                    {
                        string? latePendingId = _actor.GetPendingDecisionId(_seat);
                        var retryRequest = new TableCommandEnvelope(
                            command,
                            new RequestId(requestId.Value + "-retry"),
                            expected,
                            owner,
                            new ControlEpoch(unchecked((ulong)controlEpoch)),
                            string.IsNullOrEmpty(latePendingId) ? null : new DecisionId(latePendingId!),
                            _actor.RecoveryGeneration,
                            new RequestScopeId(_connectionScopeId!.Value.Value),
                            api,
                            digest);
                        result = await _actor.SubmitAsync(retryRequest, cancellationToken).ConfigureAwait(false);
                    }
                }

                LastSettlementResult = result;
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
            CancellationToken cancellationToken,
            TableSettlementAction? overrideSettlementAction = null)
        {
            TableSettlementAction settlementAction;
            if (overrideSettlementAction.HasValue)
            {
                settlementAction = overrideSettlementAction.Value;
            }
            else
            {
                RoundActionKind action = MapActionKind(document);
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

        private async Task<bool> RejectStaleDecisionAsync(
            Envelope envelope,
            CommandKind kind,
            RequestId requestId,
            StateVersion expected,
            BusinessPayload payload,
            string? pendingDecision,
            CancellationToken cancellationToken)
        {
            string suppliedDecision = envelope.Command?.ExpectedDecisionId ?? string.Empty;
            if (string.IsNullOrEmpty(suppliedDecision) ||
                string.Equals(suppliedDecision, pendingDecision, StringComparison.Ordinal))
            {
                return false;
            }

            // A stale decision identity is a definitive rejection of this request,
            // not a transport fault: the outbox entry must receive a RECEIPT and be
            // resolved exactly like any other rejected side effect. The ordering
            // mirrors the control path: an already-committed record under this
            // idempotency key is decisive first (exact replay replays, different
            // bytes or kind conflict)...
            if (await RejectFromDurableReceiptAsync(
                    envelope,
                    kind,
                    requestId,
                    expected,
                    payload,
                    _connectionScopeId ?? default,
                    cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            // ...then a genuinely new request must be authorized before any durable
            // rejection is written: an expired or revoked scope must not borrow the
            // rejection path to gain a persisted record, so its answer stays
            // transport-only.
            LocalCommandException? scopeError = await ValidateControlScopeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (scopeError != null)
            {
                Enqueue(RejectedReceipt(
                    envelope,
                    requestId,
                    expected,
                    scopeError.Code,
                    scopeError.Message,
                    payload));
                return true;
            }

            // The rejection is durably committed first so a lost reply packet still
            // reconciles by receipt query.
            RejectDurably(
                envelope,
                kind,
                requestId,
                expected,
                ProtocolErrorCode.DecisionMismatch,
                "The command decision id is not the current pending decision.",
                payload);
            return true;
        }

        /// <summary>
        /// Persists a definitive rejection under the request idempotency key, then
        /// publishes the rejection receipt. An already-committed record under the
        /// same key is replayed when the bytes and kind match and conflicts
        /// otherwise; it is never overwritten. A failed commit publishes a
        /// transport error instead and leaves the outcome to explicit
        /// reconciliation — the rejection never claims a durability it does not
        /// have.
        /// </summary>
        private void RejectDurably(
            Envelope envelope,
            CommandKind kind,
            RequestId requestId,
            StateVersion expected,
            string reasonCode,
            string detail,
            BusinessPayload payload)
        {
            string canonicalHex = CanonicalPayloadDigest.Compute(payload).ToHex();
            if (!_connectionScopeId.HasValue)
            {
                // Without an issued scope there is no idempotency key to persist
                // under; the rejection stays transport-only.
                Enqueue(RejectedReceipt(envelope, requestId, expected, reasonCode, detail, payload));
                return;
            }

            RequestScopeId scopeId = new RequestScopeId(_connectionScopeId.Value.Value);
            ulong now = (ulong)Math.Max(0L, _environment.ResolveClock().UtcNowMs);
            TablePayloadSha256 payloadSha256 = TablePayloadSha256.Compute(payload.CopyBytes());
            TableDurableReceipt? existing;
            try
            {
                existing = _store.TryCommitRejectedReceipt(
                    new TableDurableReceipt(
                        _connectionAccount,
                        scopeId,
                        requestId,
                        DurableReceiptApi(kind),
                        payloadSha256,
                        Mahjong.Contracts.Receipt.Rejected(expected, reasonCode),
                        detail,
                        now,
                        now + (ulong)RejectedReceiptRetention.TotalMilliseconds),
                    canonicalHex);
            }
            catch (LocalTableException)
            {
                Enqueue(ErrorEnvelope(
                    ProtocolErrorCode.InternalError,
                    "The rejection could not be durably committed."));
                return;
            }

            if (existing != null)
            {
                Enqueue(ReplayOrConflictReceipt(
                    envelope,
                    requestId,
                    expected,
                    existing,
                    DurableReceiptApi(kind),
                    payloadSha256,
                    canonicalHex,
                    payload));
                return;
            }

            Enqueue(RejectedReceipt(envelope, requestId, expected, reasonCode, detail, payload));
        }

        /// <summary>
        /// Control-path guard: a durable receipt under this idempotency key is
        /// decisive before any authority-state validation runs. An exact replay
        /// replays the stored outcome; different bytes or kind conflict. Neither
        /// overwrites the record nor re-judges a committed request against a
        /// retired decision.
        /// </summary>
        private async Task<bool> RejectFromDurableReceiptAsync(
            Envelope envelope,
            CommandKind kind,
            RequestId requestId,
            StateVersion expected,
            BusinessPayload payload,
            RequestScopeId scopeId,
            CancellationToken cancellationToken)
        {
            if (!_connectionScopeId.HasValue)
            {
                return false;
            }

            TableDurableReceipt? existing;
            try
            {
                existing = await _store.TryGetAsync(
                    _connectionAccount,
                    scopeId,
                    requestId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LocalTableException)
            {
                // Durable evidence is unreadable: do not guess and do not write.
                // The transport error keeps the outbox entry unresolved for
                // explicit reconciliation.
                Enqueue(ErrorEnvelope(
                    ProtocolErrorCode.InternalError,
                    "The durable receipt evidence could not be read."));
                return true;
            }

            if (existing == null)
            {
                return false;
            }

            Enqueue(ReplayOrConflictReceipt(
                envelope,
                requestId,
                expected,
                existing,
                DurableReceiptApi(kind),
                TablePayloadSha256.Compute(payload.CopyBytes()),
                CanonicalPayloadDigest.Compute(payload).ToHex(),
                payload));
            return true;
        }

        /// <summary>
        /// Maps a durable receipt under the request key to its wire answer: the
        /// stored outcome when the API and payload digest match the original
        /// commit, a request-id conflict otherwise.
        /// </summary>
        private Envelope ReplayOrConflictReceipt(
            Envelope source,
            RequestId requestId,
            StateVersion expected,
            TableDurableReceipt existing,
            string api,
            TablePayloadSha256 payloadSha256,
            string canonicalHex,
            BusinessPayload payload)
        {
            if (!existing.Matches(api, payloadSha256))
            {
                return RejectedReceipt(
                    source,
                    requestId,
                    expected,
                    ProtocolErrorCode.RequestIdConflict,
                    "The same request id was used with different bytes or kind.",
                    payload);
            }

            return ReceiptEnvelope(
                source,
                requestId,
                new WireReceipt
                {
                    Status = existing.Receipt.Status == Mahjong.Contracts.ReceiptStatus.Recorded
                        ? WireReceiptStatus.Recorded
                        : existing.Receipt.Status == Mahjong.Contracts.ReceiptStatus.Rejected
                            ? WireReceiptStatus.Rejected
                            : WireReceiptStatus.Applied,
                    ReasonCode = existing.Receipt.ReasonCode,
                    EvaluatedStateVersion = existing.Receipt.EvaluatedStateVersion.Value,
                    CommandVersion = existing.Receipt.CommandVersion.HasValue
                        ? existing.Receipt.CommandVersion.Value.Value
                        : 0UL,
                    EventSeq = existing.Receipt.EventSeq.HasValue
                        ? existing.Receipt.EventSeq.Value.Value
                        : 0UL,
                    Replayed = true,
                    CanonicalPayloadSha256 = canonicalHex
                },
                payload);
        }

        private static string DurableReceiptApi(CommandKind kind)
        {
            return kind switch
            {
                CommandKind.ClaimControl => "CLAIM_CONTROL",
                CommandKind.ReleaseControl => "RELEASE_CONTROL",
                _ => ApiName(kind)
            };
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
            // Control commands are serialized so a prepare/commit pair can never observe or
            // consume another in-flight control request's durable record.
            await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string canonical = CanonicalPayloadDigest.Compute(payload).ToHex();
                RequestScopeId scopeId = _connectionScopeId ?? default;
                LocalControlRecord? prior = _connectionScopeId.HasValue
                    ? _store.GetControlResult(_connectionAccount, scopeId, requestId)
                    : null;
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
                            Replayed = true,
                            CanonicalPayloadSha256 = prior.Digest
                        },
                        payload));
                    return;
                }

                // A durable receipt under this idempotency key is decisive: an exact
                // replay replays the stored outcome, different bytes or kind conflict,
                // and neither may overwrite the record nor re-judge a committed
                // request against a retired decision.
                if (await RejectFromDurableReceiptAsync(
                        envelope,
                        kind,
                        requestId,
                        expected,
                        payload,
                        scopeId,
                        cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                LocalCommandException? scopeError = await ValidateControlScopeAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (scopeError != null)
                {
                    // An expired or revoked scope stays transport-only: it must not
                    // borrow the durable rejection path to gain a persisted record.
                    Enqueue(RejectedReceipt(
                        envelope,
                        requestId,
                        expected,
                        scopeError.Code,
                        scopeError.Message,
                        payload));
                    return;
                }

                // Only a genuinely new, authorized control request is checked against
                // the current decision; its rejection is durably committed before the
                // receipt is published so a lost reply still reconciles by query. The
                // stale-decision rejection re-derives the receipt guard and scope
                // authorization itself, so both command paths share one ordering.
                string? pendingDecision = _actor.GetPendingDecisionId(_seat);
                if (await RejectStaleDecisionAsync(
                        envelope,
                        kind,
                        requestId,
                        expected,
                        payload,
                        pendingDecision,
                        cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (!document.HasField(CommandPayloadFields.ExpectedControlEpochV2))
                {
                    Enqueue(ErrorEnvelope(ProtocolErrorCode.MalformedMessage, "The control command has no expected epoch."));
                    return;
                }

                ulong epoch = document.RequireUInt64(CommandPayloadFields.ExpectedControlEpochV2, "expectedControlEpoch");
                if (epoch > long.MaxValue)
                {
                    Enqueue(RejectedReceipt(
                        envelope,
                        requestId,
                        expected,
                        TableRuntimeReasonCode.StaleControlEpoch,
                        "The expected control epoch exceeds the domain range.",
                        payload));
                    return;
                }

                long expectedEpoch = unchecked((long)epoch);
                TableControlChangeKind changeKind = kind == CommandKind.ClaimControl
                    ? TableControlChangeKind.Reclaimed
                    : TableControlChangeKind.ExplicitTakeover;
                _store.PrepareControlResult(
                    _connectionAccount,
                    scopeId,
                    requestId,
                    canonical,
                    kind.ToString(),
                    _seat,
                    expectedEpoch,
                    changeKind);

                bool durablyRecorded = false;
                try
                {
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
                        return;
                    }

                    if (control.Status == TableControlStatus.Unchanged)
                    {
                        try
                        {
                            _store.CommitPreparedControlResultOnly(
                                _connectionAccount,
                                scopeId,
                                requestId);
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

                    // The receipt is emitted only after the durable record is readable. A
                    // Changed result is recorded inside the same atomic control commit; an
                    // Unchanged result was just written above.
                    LocalControlRecord? recorded = _store.GetControlResult(
                        _connectionAccount,
                        scopeId,
                        requestId);
                    if (recorded == null ||
                        !string.Equals(recorded.Digest, canonical, StringComparison.Ordinal) ||
                        !string.Equals(recorded.Kind, kind.ToString(), StringComparison.Ordinal))
                    {
                        Enqueue(RejectedReceipt(
                            envelope,
                            requestId,
                            expected,
                            ProtocolErrorCode.InternalError,
                            "The control result was not durably recorded.",
                            payload));
                        return;
                    }

                    durablyRecorded = true;
                    Enqueue(ReceiptEnvelope(
                        envelope,
                        requestId,
                        new WireReceipt
                        {
                            Status = WireReceiptStatus.Recorded,
                            ReasonCode = "LOCAL_CONTROL",
                            EvaluatedStateVersion = recorded.EvaluatedStateVersion,
                            CommandVersion = 0UL,
                            EventSeq = 0UL,
                            CanonicalPayloadSha256 = recorded.Digest
                        },
                        payload));
                }
                finally
                {
                    if (!durablyRecorded)
                    {
                        _store.ClearPreparedControlResult(_connectionAccount, scopeId, requestId);
                    }
                }
            }
            finally
            {
                _controlGate.Release();
            }
        }

        private async ValueTask<LocalCommandException?> ValidateControlScopeAsync(CancellationToken cancellationToken)
        {
            if (!_connectionScopeId.HasValue)
            {
                return new LocalCommandException(
                    ProtocolErrorCode.Unauthenticated,
                    "The control command has no issued request scope.");
            }

            TableScopeLookup lookup;
            try
            {
                lookup = await _store.Scopes.LookupAsync(_connectionScopeId.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new LocalCommandException(
                    ProtocolErrorCode.InternalError,
                    "The request scope could not be read: " + exception.Message);
            }

            if (!lookup.Found || lookup.Scope == null)
            {
                return new LocalCommandException(
                    ProtocolErrorCode.Unauthenticated,
                    "The issued request scope is not known.");
            }

            TableScopeRecord scope = lookup.Scope;
            if (scope.Role != TableScopeRole.Player ||
                !string.Equals(scope.AccountId, _connectionAccount, StringComparison.Ordinal) ||
                !string.Equals(scope.RoomId, _options.RoomId, StringComparison.Ordinal))
            {
                return new LocalCommandException(
                    ProtocolErrorCode.Unauthenticated,
                    "The request scope does not belong to this connection.");
            }

            ulong now = (ulong)Math.Max(0L, _environment.ResolveClock().UtcNowMs);
            if (scope.IsRevoked)
            {
                return new LocalCommandException(
                    ProtocolErrorCode.RequestScopeExpired,
                    "The request scope was revoked.");
            }

            if (scope.IsExpiredAt(now))
            {
                return new LocalCommandException(
                    ProtocolErrorCode.RequestScopeExpired,
                    "The request scope expired.");
            }

            return null;
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

            // A receipt lookup is a read of an already committed outcome, so it is bound to
            // the current transport session but may carry a scope issued to an earlier
            // connection (including one from before a cold process restart).
            if (!IsCurrentBinding(envelope))
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
            if (receipt == null)
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

                    var discardTile = new TileId(unchecked((int)tiles[0]));
                    return RoundCommand.Discard(roundId, _seat, discardTile);
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

                    var riichiTile = new TileId(unchecked((int)riichiTiles[0]));
                    return RoundCommand.Riichi(roundId, _seat, riichiTile, null);
                case RoundActionKind.DeclareChi:
                {
                    RoundStateMachine round = _actor.GetRoundStateForHost();
                    RoundResponseWindow? window = round.Window;
                    IReadOnlyList<uint> meld = document.HasField(CommandPayloadFields.TileIds)
                        ? document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds")
                        : Array.Empty<uint>();
                    TileId calledTile;
                    TileId handA;
                    TileId handB;
                    if (meld.Count == 3)
                    {
                        if (window != null && window.TargetTile.Value == (int)meld[0])
                        {
                            calledTile = window.TargetTile;
                            handA = new TileId(unchecked((int)meld[1]));
                            handB = new TileId(unchecked((int)meld[2]));
                        }
                        else if (window != null && window.TargetTile.Value == (int)meld[2])
                        {
                            calledTile = window.TargetTile;
                            handA = new TileId(unchecked((int)meld[0]));
                            handB = new TileId(unchecked((int)meld[1]));
                        }
                        else if (window != null && window.TargetTile.Value == (int)meld[1])
                        {
                            calledTile = window.TargetTile;
                            handA = new TileId(unchecked((int)meld[0]));
                            handB = new TileId(unchecked((int)meld[2]));
                        }
                        else
                        {
                            calledTile = new TileId(unchecked((int)meld[0]));
                            handA = new TileId(unchecked((int)meld[1]));
                            handB = new TileId(unchecked((int)meld[2]));
                        }
                    }
                    else if (meld.Count == 2 && window != null)
                    {
                        calledTile = window.TargetTile;
                        handA = new TileId(unchecked((int)meld[0]));
                        handB = new TileId(unchecked((int)meld[1]));
                    }
                    else if (window != null)
                    {
                        (TileId a, TileId b)? found = LocalCandidateProvider.FindChiTiles(
                            round.Tiles.TilesInZone(TileZone.Hand, _seat),
                            window.TargetTile);
                        if (found == null)
                        {
                            throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "No valid chi hand tiles found.");
                        }

                        calledTile = window.TargetTile;
                        handA = found.Value.a;
                        handB = found.Value.b;
                    }
                    else
                    {
                        throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "Chi requires tile ids.");
                    }

                    return RoundCommand.Chi(roundId, _seat, calledTile, handA, handB);
                }

                case RoundActionKind.DeclarePon:
                {
                    RoundStateMachine round = _actor.GetRoundStateForHost();
                    RoundResponseWindow? window = round.Window;
                    IReadOnlyList<uint> meld = document.HasField(CommandPayloadFields.TileIds)
                        ? document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds")
                        : Array.Empty<uint>();
                    TileId calledTile;
                    TileId handA;
                    TileId handB;
                    if (meld.Count == 3)
                    {
                        uint targetVal = window != null ? unchecked((uint)window.TargetTile.Value) : meld[0];
                        var handList = new List<TileId>(2);
                        bool targetSkipped = false;
                        for (int i = 0; i < 3; i++)
                        {
                            if (!targetSkipped && meld[i] == targetVal)
                            {
                                targetSkipped = true;
                            }
                            else
                            {
                                handList.Add(new TileId(unchecked((int)meld[i])));
                            }
                        }

                        calledTile = new TileId(unchecked((int)targetVal));
                        handA = handList[0];
                        handB = handList[1];
                    }
                    else if (meld.Count == 2 && window != null)
                    {
                        calledTile = window.TargetTile;
                        handA = new TileId(unchecked((int)meld[0]));
                        handB = new TileId(unchecked((int)meld[1]));
                    }
                    else if (window != null)
                    {
                        IReadOnlyList<TileId> handTiles = round.Tiles.TilesInZone(TileZone.Hand, _seat);
                        var matches = handTiles.Where(t => t.TypeIndex == window.TargetTile.TypeIndex).Take(2).ToList();
                        if (matches.Count < 2)
                        {
                            throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "No matching tiles in hand for Pon.");
                        }

                        calledTile = window.TargetTile;
                        handA = matches[0];
                        handB = matches[1];
                    }
                    else
                    {
                        throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "Pon requires tile ids.");
                    }

                    return RoundCommand.Pon(roundId, _seat, calledTile, handA, handB);
                }

                case RoundActionKind.DeclareOpenKan:
                case RoundActionKind.DeclareClosedKan:
                case RoundActionKind.DeclareAddedKan:
                {
                    uint meldType = document.HasField(CommandPayloadFields.MeldType)
                        ? document.RequireUInt32(CommandPayloadFields.MeldType, "meldType")
                        : 0U;
                    RoundStateMachine round = _actor.GetRoundStateForHost();
                    string? optionId = document.HasField(CommandPayloadFields.OptionId)
                        ? document.RequireString(CommandPayloadFields.OptionId, "optionId")
                        : null;
                    bool isClosed = action == RoundActionKind.DeclareClosedKan ||
                                    meldType == 4U ||
                                    (optionId != null && optionId.StartsWith("closed-kan", StringComparison.Ordinal));
                    bool isAdded = action == RoundActionKind.DeclareAddedKan ||
                                   meldType == 5U ||
                                   (optionId != null && optionId.StartsWith("added-kan", StringComparison.Ordinal));
                    if (isClosed)
                    {
                        IReadOnlyList<uint> kan = document.HasField(CommandPayloadFields.TileIds)
                            ? document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds")
                            : Array.Empty<uint>();
                        if (kan.Count == 4)
                        {
                            return RoundCommand.ClosedKan(
                                roundId,
                                _seat,
                                new TileId(unchecked((int)kan[0])),
                                new TileId(unchecked((int)kan[1])),
                                new TileId(unchecked((int)kan[2])),
                                new TileId(unchecked((int)kan[3])));
                        }

                        if (kan.Count == 1)
                        {
                            var t = new TileId(unchecked((int)kan[0]));
                            var matches = round.Tiles.TilesInZone(TileZone.Hand, _seat).Where(tile => tile.TypeIndex == t.TypeIndex).Take(4).ToList();
                            if (matches.Count == 4)
                            {
                                return RoundCommand.ClosedKan(roundId, _seat, matches[0], matches[1], matches[2], matches[3]);
                            }
                        }

                        throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "ClosedKan requires 4 tile ids.");
                    }

                    if (isAdded)
                    {
                        IReadOnlyList<uint> added = document.HasField(CommandPayloadFields.TileIds)
                            ? document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds")
                            : Array.Empty<uint>();
                        if (added.Count == 0)
                        {
                            throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "AddedKan requires at least 1 tile id.");
                        }

                        var addedTile = new TileId(unchecked((int)added[0]));
                        IReadOnlyList<int> chankanEligible = LocalCandidateProvider.DeriveEligibleSeatsForAddedKan(
                            round,
                            _seat,
                            addedTile,
                            _temporaryFuriten,
                            _riichiFuriten);
                        return RoundCommand.AddedKan(roundId, _seat, addedTile, chankanEligible);
                    }

                    RoundResponseWindow? window = round.Window;
                    IReadOnlyList<uint> open = document.HasField(CommandPayloadFields.TileIds)
                        ? document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds")
                        : Array.Empty<uint>();
                    TileId openCalledTile;
                    TileId openHandA;
                    TileId openHandB;
                    TileId openHandC;
                    if (open.Count == 4)
                    {
                        uint targetVal = window != null ? unchecked((uint)window.TargetTile.Value) : open[0];
                        var handList = new List<TileId>(3);
                        bool targetSkipped = false;
                        for (int i = 0; i < 4; i++)
                        {
                            if (!targetSkipped && open[i] == targetVal)
                            {
                                targetSkipped = true;
                            }
                            else
                            {
                                handList.Add(new TileId(unchecked((int)open[i])));
                            }
                        }

                        openCalledTile = new TileId(unchecked((int)targetVal));
                        openHandA = handList[0];
                        openHandB = handList[1];
                        openHandC = handList[2];
                    }
                    else if (open.Count == 3 && window != null)
                    {
                        openCalledTile = window.TargetTile;
                        openHandA = new TileId(unchecked((int)open[0]));
                        openHandB = new TileId(unchecked((int)open[1]));
                        openHandC = new TileId(unchecked((int)open[2]));
                    }
                    else if (window != null)
                    {
                        IReadOnlyList<TileId> handTiles = round.Tiles.TilesInZone(TileZone.Hand, _seat);
                        var matches = handTiles.Where(t => t.TypeIndex == window.TargetTile.TypeIndex).Take(3).ToList();
                        if (matches.Count < 3)
                        {
                            throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "No matching tiles in hand for OpenKan.");
                        }

                        openCalledTile = window.TargetTile;
                        openHandA = matches[0];
                        openHandB = matches[1];
                        openHandC = matches[2];
                    }
                    else
                    {
                        throw new LocalCommandException(ProtocolErrorCode.MalformedMessage, "OpenKan requires tile ids.");
                    }

                    return RoundCommand.OpenKan(
                        roundId,
                        _seat,
                        openCalledTile,
                        openHandA,
                        openHandB,
                        openHandC);
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

        internal static Exception? LastEnqueueException;
        internal static TableOperationResult? LastSettlementResult;
        internal static Exception? LastCommandException;

        /// <summary>
        /// How long a rejection receipt stays queryable. Shared by the actor commit
        /// path and the early-exit rejection path so both horizons stay identical.
        /// </summary>
        internal static readonly TimeSpan RejectedReceiptRetention = TimeSpan.FromHours(24);

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
                LastEnqueueException = exception;
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

        internal string DiagnosticSaveDirectory =>
            LocalTableHostPaths.ResolveSaveDirectory(_options.SaveDirectory);

        /// <summary>
        /// Captures one exact committed persisted version. The in-memory actor snapshot is used
        /// only as a fence: the facts come from the primary envelope bytes, and the capture is
        /// rejected until the decoded version/round/event/control generation match that snapshot.
        /// No command is submitted and no file is written here.
        /// </summary>
        internal async Task<LocalTableRecoveryCapture> CaptureRecoveryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string primary = Path.Combine(DiagnosticSaveDirectory, _options.RoomId + ".save");
            for (int attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TableSnapshot snapshot = await _actor.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                TableControlSnapshot control = await _actor.GetControlSnapshotAsync(cancellationToken).ConfigureAwait(false);
                byte[] bytes;
                try
                {
                    bytes = await Task.Run(() => File.ReadAllBytes(primary), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                CheatState? currentCheat = ActorCheatField?.GetValue(_actor) as CheatState;
                if (currentCheat == null)
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.Unavailable,
                        "The current committed cheat aggregate is not observable for the recovery capture.");
                }

                byte[] cheatPayload;
                try
                {
                    cheatPayload = TableCheatLedgerCheckpointCodec.Encode(new TableCheatLedgerCheckpoint(currentCheat));
                }
                catch (Exception exception)
                {
                    throw new LocalTableException(
                        LocalTableErrorKind.CorruptState,
                        "The current cheat aggregate could not be encoded: " + exception.Message,
                        exception);
                }

                LocalTableRecoveryFacts facts = LocalTableRecoveryFactsReader.ReadPersistedFacts(bytes, cheatPayload);
                if (!string.Equals(facts.CheatVersion, currentCheat.Version.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !string.Equals(facts.CheatFingerprint, currentCheat.Fingerprint.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(facts.RoundId, snapshot.RoundId, StringComparison.Ordinal) ||
                    !string.Equals(facts.StateVersion, snapshot.StateVersion.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !string.Equals(facts.EventSeq, snapshot.LastEventSeq.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !string.Equals(facts.StateFingerprint, snapshot.StateFingerprint.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !string.Equals(facts.ControlVersion, control.ControlVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                bool controlMatches = true;
                for (int seat = 0; seat < 4; seat++)
                {
                    if (facts.ControlEpochs[seat] != control.Seats[seat].ControlEpoch)
                    {
                        controlMatches = false;
                        break;
                    }
                }

                if (!controlMatches)
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return new LocalTableRecoveryCapture(bytes, cheatPayload, facts);
            }

            throw new LocalTableException(
                LocalTableErrorKind.CorruptState,
                "No stable persisted authority version matched the open runtime snapshot; refusing to stitch fields from different time points.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            LocalTableDiagnosticRegistry.Unregister(DiagnosticSaveDirectory, this);
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

    internal sealed class DebugTerminationSink : ITableStreamTerminationSink
    {
        public static TableStreamTermination? LastTermination;

        public void OnStreamTerminated(TableStreamTermination termination)
        {
            LastTermination = termination;
        }
    }

    /// <summary>Frozen Contracts -> strict wire codec used only by the loopback bridge.</summary>
    internal sealed class LocalStreamContractCodec : ITableStreamContractCodec
    {
        public static Exception? LastEncodeException;

        public byte[] Encode(TableStreamContractFrame frame)
        {
            try
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
            catch (Exception ex)
            {
                LastEncodeException = ex;
                throw;
            }
        }
    }

    /// <summary>Async single-consumer frame queue with independent waiter cancellation.</summary>
    internal sealed class FuritenState
    {
        public bool[] Temporary { get; } = new bool[4];
        public bool[] Riichi { get; } = new bool[4];
    }

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
