using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Microsoft.Extensions.Hosting;

namespace Mahjong.TableService
{
    /// <summary>Real wall-clock port for the actor (host-driven; never a WebSocket thread).</summary>
    public sealed class SystemTableClock : ITableClock
    {
        private static readonly long StopwatchStart = Stopwatch.GetTimestamp();

        public long MonotonicMillis =>
            (long)((Stopwatch.GetTimestamp() - StopwatchStart) * 1000.0 / Stopwatch.Frequency);

        public long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static readonly SystemTableClock Instance = new SystemTableClock();
    }

    /// <summary>
    /// The host-side commit port: the single state-version/event-seq authority for one table.
    /// It enforces the same invariants the accepted tests pin (owner fence, immediate
    /// successor version, contiguous event ordinals) and never invents Domain facts. Durable
    /// receipts are written by the actor through the durable context, not here.
    /// </summary>
    public sealed class TableServiceCommitPort : ITableCommitPort
    {
        private readonly object _gate = new object();
        private StateVersion _version = new StateVersion(0);
        private EventSeq _lastSeq = new EventSeq(0);
        private readonly OwnerEpoch _owner;

        public TableServiceCommitPort(OwnerEpoch initialOwnerEpoch)
        {
            _owner = initialOwnerEpoch;
        }

        public StateVersion CurrentVersion
        {
            get
            {
                lock (_gate)
                {
                    return _version;
                }
            }
        }

        public ValueTask<TableCommitResult> CommitAsync(
            TableCommitRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (request.OwnerEpoch != _owner)
                {
                    return new ValueTask<TableCommitResult>(TableCommitResult.StaleOwner("owner epoch mismatch"));
                }

                if (request.ExpectedStateVersion != _version)
                {
                    return new ValueTask<TableCommitResult>(TableCommitResult.StaleState(
                        "expected " + request.ExpectedStateVersion.Value +
                        ", actual " + _version.Value));
                }

                if (request.Events.Count == 0)
                {
                    return new ValueTask<TableCommitResult>(TableCommitResult.Failed(
                        "COMMIT_FAILED",
                        "accepted command without events"));
                }

                if (request.CommandVersion != _version.Next())
                {
                    return new ValueTask<TableCommitResult>(TableCommitResult.StaleState(
                        "command version is not the immediate successor"));
                }

                ulong expectedSeq = _lastSeq.Value + 1;
                for (int i = 0; i < request.Events.Count; i++)
                {
                    TableEventRecord record = request.Events[i];
                    if (record.Ordinal != i ||
                        record.CommandVersion != request.CommandVersion ||
                        record.Seq.Value != expectedSeq + (ulong)i)
                    {
                        return new ValueTask<TableCommitResult>(TableCommitResult.Failed(
                            "COMMIT_FAILED",
                            "event ordering is not contiguous"));
                    }
                }

                if (request.LastEventSeq.Value != expectedSeq + (ulong)request.Events.Count - 1)
                {
                    return new ValueTask<TableCommitResult>(TableCommitResult.Failed(
                        "COMMIT_FAILED",
                        "last event sequence mismatch"));
                }

                _version = request.CommandVersion;
                _lastSeq = request.LastEventSeq;
                return new ValueTask<TableCommitResult>(TableCommitResult.Committed());
            }
        }
    }

    /// <summary>
    /// One isolated table: its own actor, its own in-memory store/scope authority (accepted
    /// P3.4 ports), its own commit port and its own wire codec. Rooms never share state.
    /// </summary>
    public sealed class RoomHost : ITableStreamTerminationSink
    {
        public const long InitialOwnerEpochValue = 1;

        private readonly List<WeakReference<WireTableStreamSink>> _liveSinks = new List<WeakReference<WireTableStreamSink>>();
        private readonly object _sinkGate = new object();

        public RoomHost(
            string roomId,
            TableServiceOptions options,
            TableActor actor,
            InMemoryRequestScopeAuthority scopes,
            InMemoryTableStore store,
            TableServiceCommitPort commitPort,
            TableDurableContext durable,
            OwnerEpoch ownerEpoch)
        {
            RoomId = roomId;
            Options = options;
            Actor = actor;
            Scopes = scopes;
            Store = store;
            CommitPort = commitPort;
            Durable = durable;
            OwnerEpoch = ownerEpoch;
        }

        public string RoomId { get; }

        public TableServiceOptions Options { get; }

        public TableActor Actor { get; }

        public InMemoryRequestScopeAuthority Scopes { get; }

        public InMemoryTableStore Store { get; }

        public TableServiceCommitPort CommitPort { get; }

        public TableDurableContext Durable { get; }

        public OwnerEpoch OwnerEpoch { get; }

        /// <summary>Set when the table lease is lost: admission stops, existing streams keep draining.</summary>
        public volatile bool AdmissionStopped;

        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

        public long LastRenewUtcMs;

        internal void TrackSink(WireTableStreamSink sink)
        {
            lock (_sinkGate)
            {
                for (int i = _liveSinks.Count - 1; i >= 0; i--)
                {
                    if (!_liveSinks[i].TryGetTarget(out _))
                    {
                        _liveSinks.RemoveAt(i);
                    }
                }

                _liveSinks.Add(new WeakReference<WireTableStreamSink>(sink));
            }
        }

        /// <summary>Actor callback: a stream died (slow consumer, oversize, stale fences, actor fault).</summary>
        public void OnStreamTerminated(TableStreamTermination termination)
        {
            WireTableStreamSink? sink = null;
            lock (_sinkGate)
            {
                foreach (WeakReference<WireTableStreamSink> reference in _liveSinks)
                {
                    if (reference.TryGetTarget(out WireTableStreamSink? candidate) &&
                        candidate.Owns(termination.Handle))
                    {
                        sink = candidate;
                        break;
                    }
                }
            }

            sink?.NotifyTerminated(termination);
        }

        public void StopAdmission(string reason)
        {
            AdmissionStopped = true;
        }

        /// <summary>Deterministic initial round for the P5.1 host: a dealt CheatWindow round
        /// from the Domain's own recorded-draw path. Match orchestration (P5.3) replaces this.</summary>
        public static RoundStateMachine CreateInitialRound(string roomId)
        {
            var spec = new RoundSpec(
                roomId + "-r1",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            TilePositionMap dealt = TilePositionMap.CreateDealt(
                RecordedRandomDraw.Standard("tablesvc-" + roomId),
                0);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }

        public static TableActor CreateActor(
            string roomId,
            TableServiceOptions options,
            ITableCommitPort commitPort,
            ITableClock clock,
            ITablePublicationSink? publicationSink,
            TableStreamOptions streamOptions,
            TableDurableContext? durable,
            OwnerEpoch initialOwnerEpoch,
            RoundStateMachine round)
        {
            var actorOptions = new TableActorOptions
            {
                RoomId = roomId,
                InitialControllers = InitialControllers(),
                InitialOwnerEpoch = initialOwnerEpoch,
                CheatEconomy = TableProjectionContextSource.DefaultEconomy,
                SleeveBloomConfig = TableProjectionContextSource.DefaultSleeveConfig
            };
            return new TableActor(
                actorOptions,
                round,
                commitPort,
                clock,
                publicationSink,
                controlCommitPort: null,
                projectionContextSource: new TableProjectionContextSource(),
                streamOptions,
                durable,
                settlementFactsPort: null);
        }

        /// <summary>
        /// The room's checkpoint-0: the durable row every later commit is validated against
        /// (a commit without it fails with CHECKPOINT_MISSING "Room is not initialized.").
        /// Mirrors the accepted TableRuntime harness bootstrap: the same round instance the
        /// actor is constructed with, four remote-human controllers, the service economy's
        /// initialized ledger, and this projection source's baseline facts. Match
        /// orchestration (P5.3) replaces this bootstrap.
        /// </summary>
        public static TableCheckpoint CreateInitialCheckpoint(
            string roomId,
            RoundStateMachine round,
            CheatLedger ledger,
            OwnerEpoch ownerEpoch,
            string bootId,
            ulong utcMs)
        {
            var clockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round);
            var decisionClock = new DecisionClock(clockConfig);
            decisionClock.BeginRound(round.RoundId, 0L);
            TableTimingCheckpoint timing = TableCheckpointFactory.CaptureTiming(
                decisionClock,
                Array.Empty<PendingDecision>(),
                0L,
                utcMs,
                bootId);
            var control = new TableControlCheckpoint(
                new ControlRegistry(new DisconnectPolicy(), InitialControllers()).Capture(),
                0L);

            byte[] state = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round));
            byte[] controlBytes = TableControlCheckpointCodec.Encode(control);
            byte[] timingBytes = TableTimingCheckpointCodec.Encode(timing);
            byte[] cheatBytes = TableCheatLedgerCheckpointCodec.Encode(TableCheatLedgerCheckpoint.FromLedger(ledger));
            byte[] facts = TableProjectionFactsCodec.Encode(
                new TableProjectionContextSource().Capture(new TableProjectionContextRequest(
                    roomId,
                    round,
                    new StateVersion(0),
                    new EventSeq(0),
                    ownerEpoch,
                    0L,
                    0L,
                    0L,
                    (long)utcMs,
                    Array.Empty<TableEventRecord>())));
            byte[] history = TableCommittedHistoryCodec.Encode(Array.Empty<TableEventRecord>());
            return new TableCheckpoint(
                roomId,
                new StateVersion(0),
                new EventSeq(0),
                round.SourceFingerprint,
                (int)ProtocolConstants.RulesetVersion,
                state,
                TablePayloadSha256.Compute(state),
                control.ControlVersion,
                controlBytes,
                TablePayloadSha256.Compute(controlBytes),
                timingBytes,
                TablePayloadSha256.Compute(timingBytes),
                cheatBytes,
                TablePayloadSha256.Compute(cheatBytes),
                facts,
                TablePayloadSha256.Compute(facts),
                utcMs,
                bootId,
                TableCheckpointFactory.SchemaVersion,
                1L,
                0L,
                history,
                TablePayloadSha256.Compute(history));
        }

        private static Dictionary<int, SeatControllerKind> InitialControllers()
        {
            return new Dictionary<int, SeatControllerKind>
            {
                { 0, SeatControllerKind.RemoteHuman },
                { 1, SeatControllerKind.RemoteHuman },
                { 2, SeatControllerKind.RemoteHuman },
                { 3, SeatControllerKind.RemoteHuman }
            };
        }

        public static TableStreamOptions CreateStreamOptions(
            TableServiceOptions options,
            ITableStreamContractCodec codec,
            ITableStreamTerminationSink terminationSink)
        {
            return new TableStreamOptions
            {
                MaxQueuedFrames = 64,
                MaxQueuedBytes = 4 * 1024 * 1024,
                MaxStreams = options.MaxStreamsPerRoom,
                Encoder = new ContractsStreamFrameEncoder(codec),
                TerminationSink = terminationSink
            };
        }
    }

    /// <summary>
    /// Per-table actor registry port. The default implementation is bounded and lazy: a room
    /// host is created on first admission and never re-created; a full registry or an
    /// admission-stopped (owner-lost) host refuses new connections.
    /// </summary>
    public interface ITableHostRegistry
    {
        RoomHost GetOrAdd(string roomId);

        bool TryGet(string roomId, out RoomHost? host);

        IEnumerable<RoomHost> Hosts { get; }
    }

    public sealed class InMemoryTableHostRegistry : ITableHostRegistry
    {
        private readonly ConcurrentDictionary<string, RoomHost> _hosts =
            new ConcurrentDictionary<string, RoomHost>(StringComparer.Ordinal);

        private readonly TableServiceOptions _options;
        private readonly ITableStreamContractCodec _codec;
        private readonly ITableClock _clock;

        public InMemoryTableHostRegistry(TableServiceOptions options, ITableStreamContractCodec codec, ITableClock clock)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public IEnumerable<RoomHost> Hosts => _hosts.Values;

        public bool TryGet(string roomId, out RoomHost? host)
        {
            host = _hosts.TryGetValue(roomId, out RoomHost? found) ? found : null;
            return host != null;
        }

        public RoomHost GetOrAdd(string roomId)
        {
            return _hosts.GetOrAdd(roomId, CreateHost);
        }

        private RoomHost CreateHost(string roomId)
        {
            if (_hosts.Count >= _options.MaxRooms)
            {
                throw new TableServiceCapacityException("ROOM_CAPACITY", "The host registry is at its room capacity.");
            }

            var scopes = new InMemoryRequestScopeAuthority();
            var store = new InMemoryTableStore(_clock, scopes);
            string ownerInstance = "tablesvc-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            // The room's checkpoint-0 row must exist before the first accepted commit (the
            // store answers CHECKPOINT_MISSING "Room is not initialized." otherwise), and the
            // lease must exist before the actor serves: the first tick renewal fails as stale
            // against a missing row and the actor would enter LEASE_LOST (accepted P3.4
            // harness creates the room and acquires the lease before constructing the actor
            // for exactly these reasons).
            RoundStateMachine round = RoomHost.CreateInitialRound(roomId);
            CheatLedger ledger = CheatLedger.Initialize(TableProjectionContextSource.DefaultEconomy);
            store.CreateRoomAsync(
                    RoomHost.CreateInitialCheckpoint(
                        roomId,
                        round,
                        ledger,
                        new OwnerEpoch(1),
                        ProcessBootId,
                        (ulong)_clock.UtcNowMs),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            TimeSpan ttl = TimeSpan.FromSeconds(_options.LeaseTtlSeconds);
            TableLeaseAcquireResult lease = store
                .AcquireAsync(roomId, ownerInstance, ttl, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!lease.Acquired || lease.Lease == null)
            {
                throw new TableServiceCapacityException("LEASE_UNAVAILABLE", lease.Detail);
            }

            OwnerEpoch initialEpoch = lease.Lease.OwnerEpoch;
            var commitPort = new TableServiceCommitPort(initialEpoch);
            var durable = new TableDurableContext(
                store,
                store,
                store,
                store,
                scopes,
                ownerInstance,
                ttl,
                ProcessBootId,
                rulesetVersion: (int)ProtocolConstants.RulesetVersion);
            durable.CheatLedgerProvider = () => ledger;
            durable.CheatStateProvider = () => new CheatState(
                ledger,
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
            RoomHost host = new RoomHost(
                roomId,
                _options,
                RoomHost.CreateActor(roomId, _options, commitPort, _clock, null, CreateStreamOptionsLazy(roomId), durable, initialEpoch, round),
                scopes,
                store,
                commitPort,
                durable,
                initialEpoch);
            return host;
        }

        private TableStreamOptions CreateStreamOptionsLazy(string roomId)
        {
            // Each room gets its own codec instance; the codec is stateless so this is cheap.
            return RoomHost.CreateStreamOptions(_options, _codec, RequireTerminationSink(roomId));
        }

        private ITableStreamTerminationSink RequireTerminationSink(string roomId)
        {
            // The sink is wired after host construction (the termination sink needs the host);
            // create a forwarding shim that resolves the host lazily.
            return new LazyRoomTerminationShim(this, roomId);
        }

        public string ProcessBootId { get; } = Guid.NewGuid().ToString("N");

        private sealed class LazyRoomTerminationShim : ITableStreamTerminationSink
        {
            private readonly InMemoryTableHostRegistry _registry;
            private readonly string _roomId;

            public LazyRoomTerminationShim(InMemoryTableHostRegistry registry, string roomId)
            {
                _registry = registry;
                _roomId = roomId;
            }

            public void OnStreamTerminated(TableStreamTermination termination)
            {
                if (_registry.TryGet(_roomId, out RoomHost? host))
                {
                    host!.OnStreamTerminated(termination);
                }
            }
        }
    }

    /// <summary>Capacity refusals surface as SERVER_BUSY, never as silent queueing.</summary>
    public sealed class TableServiceCapacityException : Exception
    {
        public TableServiceCapacityException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// Platform tick driver. It advances every live table actor from a single background loop
    /// that is fully independent of WebSocket traffic, so transport heartbeats can never
    /// starve actor decision/rule deadlines. Ticks are mailbox-serialized inside the actor.
    /// </summary>
    public sealed class TableTickDriver : BackgroundService
    {
        private readonly ITableHostRegistry _registry;
        private readonly TableServiceOptions _options;

        public TableTickDriver(ITableHostRegistry registry, TableServiceOptions options)
        {
            _registry = registry;
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.RoomTickIntervalMs));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (RoomHost host in _registry.Hosts)
                {
                    if (host.AdmissionStopped)
                    {
                        continue;
                    }

                    try
                    {
                        await host.Actor.TickAsync(request: null, cancellationToken: stoppingToken).ConfigureAwait(false);
                        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        long elapsed = nowMs - Interlocked.Read(ref host.LastRenewUtcMs);
                        if (elapsed > _options.LeaseTtlSeconds * 500L)
                        {
                            await host.Actor.RenewLeaseAsync(stoppingToken).ConfigureAwait(false);
                            Interlocked.Exchange(ref host.LastRenewUtcMs, nowMs);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // A faulted tick must never kill the driver loop; the actor surfaces its
                        // own lifecycle through operation results and stream terminations.
                    }
                }
            }
        }
    }
}
