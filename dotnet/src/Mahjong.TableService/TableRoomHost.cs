using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
    /// Trusted room-initialization seam (D-1-01). Production binds the cryptographically
    /// random source below; a test assembly may inject its own trusted real-Domain round for
    /// a deterministic scenario, and a host registry without any source is fail-closed: it
    /// refuses to serve the table rather than falling back to a known fixed wall order, a
    /// fabricated projection or any fixture wall as a playable room.
    /// </summary>
    public interface ITableRoundSource
    {
        /// <summary>
        /// Returns a freshly dealt real Domain round (RoundStateMachine.CreateDealt over a
        /// complete 136-tile permutation from a trusted shuffle authority). The wall order of
        /// the returned round must never be derivable from the room id or any other
        /// client-visible value: a fixed/predictable order makes every room's concealed tiles
        /// predictable.
        /// </summary>
        RoundStateMachine CreateRound(string roomId);
    }

    /// <summary>
    /// The safe real local initialization available on the accepted Domain ports (D-1-01): a
    /// complete 136-tile physical permutation produced by the process cryptographic RNG 鈥?    /// never from an id/seed string 鈥?and then the Domain's own dealt-round construction. No
    /// schema or Domain rule changes; the recorded-draw pipeline stays an explicit caller
    /// input and a room name can no longer predict any concealed tile.
    /// </summary>
    public sealed class CryptographicTableRoundSource : ITableRoundSource
    {
        public static readonly CryptographicTableRoundSource Instance = new CryptographicTableRoundSource();

        public RoundStateMachine CreateRound(string roomId)
        {
            var order = new TileId[TileId.Count];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = new TileId(i);
            }

            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(0, i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            var spec = new RoundSpec(
                roomId + "-r1",
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            TilePositionMap dealt = TilePositionMap.CreateDealt(
                RecordedRandomDraw.Create(roomId + "-wall", order),
                0);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }
    }

    /// <summary>
    /// One isolated table: its own actor, its own in-memory store/scope authority (accepted
    /// P3.4 ports), its own commit port and its own wire codec. Rooms never share state.
    /// </summary>
    public sealed class RoomHost : ITableStreamTerminationSink
    {
        public const long InitialOwnerEpochValue = 1;

        /// <summary>
        /// The service domain's authoritative decision clock (D-1-01): one RoomHost's real
        /// decision deadlines come from the platform's V4 timing contract (3s of per-turn
        /// thinking over a 20s round reserve). The bootstrap checkpoint and the tick driver
        /// must use the SAME config so a persisted checkpoint never modelled different deadline
        /// semantics than a live decision actually reports.
        /// </summary>
        public static DecisionClockConfig ServiceDecisionClockConfig { get; } = new DecisionClockConfig();

        private readonly List<WeakReference<WireTableStreamSink>> _liveSinks = new List<WeakReference<WireTableStreamSink>>();
        private readonly object _sinkGate = new object();

        public RoomHost(
            string roomId,
            TableServiceOptions options,
            TableActor actor,
            ITableHostScopeAuthority scopes,
            ITableStore store,
            TableServiceCommitPort commitPort,
            TableDurableContext durable,
            OwnerEpoch ownerEpoch,
            TableRoomDecisionState decisionState,
            TableScopeRecord serviceScope,
            long recoveryGeneration = 0L)
        {
            RoomId = roomId;
            Options = options;
            Actor = actor;
            Scopes = scopes;
            Store = store;
            CommitPort = commitPort;
            Durable = durable;
            OwnerEpoch = ownerEpoch;
            DecisionState = decisionState ?? throw new ArgumentNullException(nameof(decisionState));
            ServiceScope = serviceScope;
            RecoveryGeneration = recoveryGeneration;
        }

        public string RoomId { get; }

        public TableServiceOptions Options { get; }

        public TableActor Actor { get; }

        /// <summary>
        /// D-1-01 (round 3): the room's own trusted service scope the auto-draw envelope rides
        /// through the actor's durable pipeline. It is issued at room creation with the room's
        /// lifetime, belongs to the service authority itself (never a player ticket account),
        /// and only ever carries the service's own RotateCommand — a player's scope remains the
        /// only real client identity surface.
        /// </summary>
        public TableScopeRecord ServiceScope { get; }

        /// <summary>
        /// P5.4b-A: the room's scope authority through the narrow host port — the in-memory
        /// registry binds the accepted P3.4 authority, the PostgreSQL registry binds the
        /// table_store-backed one. Admission, rollback revocation and receipt reconciliation
        /// authorize here; there is no second, diverging scope source.
        /// </summary>
        public ITableHostScopeAuthority Scopes { get; }

        /// <summary>The room's durable store (memory-mode or PostgreSQL — same frozen ports).</summary>
        public ITableStore Store { get; }

        public TableServiceCommitPort CommitPort { get; }

        public TableDurableContext Durable { get; }

        public OwnerEpoch OwnerEpoch { get; }

        /// <summary>
        /// The actor's CURRENT recovery generation, read through the accepted
        /// <c>GetControlSnapshotAsync</c> API right after a cold recovery (0 for a freshly
        /// bootstrapped room). Every actor envelope this host issues — stream subscribe,
        /// client command, cheat command, auto-draw — must carry this exact generation: a
        /// recovered actor runs the strict recovery fence and rejects stale generations.
        /// </summary>
        public long RecoveryGeneration { get; }

        /// <summary>
        /// D-1-01: the room's decision runtime state 鈥?the trusted opening seam that derives a
        /// real Domain-backed decision for the currently acting seat and projects it honestly
        /// (decision id, epoch, candidates, deadline) through the trusted context source.
        /// </summary>
        public TableRoomDecisionState DecisionState { get; }

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

        /// <summary>
        /// D-1-01: after the drive opened a decision, every LIVE subscribed stream gets one
        /// authoritative re-baseline so the seat's frozen DecisionView (decision id, control
        /// epoch, deadline, legal actions) reaches the client on the wire — the actor publishes
        /// frames only on commits, and an OPEN decision is not a commit; without this refresh
        /// a client would have no honest way to learn the decision id it must act with.
        /// Fired through the actor's own sync path, so the frame is still actor-encoded truth.
        /// </summary>
        public void PublishDecisionViewsToLiveStreams()
        {
            List<TableStreamHandle> handles = new List<TableStreamHandle>();
            lock (_sinkGate)
            {
                foreach (WeakReference<WireTableStreamSink> reference in _liveSinks)
                {
                    if (reference.TryGetTarget(out WireTableStreamSink? candidate) &&
                        candidate.Handle != null &&
                        candidate.Termination == null)
                    {
                        handles.Add(candidate.Handle);
                    }
                }
            }

            foreach (TableStreamHandle handle in handles)
            {
                try
                {
                    _ = Actor.SyncStreamAsync(new TableStreamSyncRequest(
                        handle,
                        new Contracts.StreamCursor(new StreamVersion(0), new ProjectionSeq(0))));
                }
                catch (Exception)
                {
                    // A dead stream simply does not receive the refresh; its termination path
                    // governs.
                }
            }
        }

        public void StopAdmission(string reason)
        {
            AdmissionStopped = true;
            AdmissionsStoppedReason = reason;
        }

        /// <summary>Redacted reason recorded when the admission gate closed.</summary>
        public string AdmissionsStoppedReason { get; private set; } = string.Empty;

        /// <summary>
        /// Trusted initial-round creation seam. The production default is the cryptographic
        /// random source above; a test assembly injects its own source through the registry.
        /// There is no fixed-wall fallback anywhere in this component.
        /// </summary>
        public static RoundStateMachine CreateInitialRound(
            string roomId,
            ITableRoundSource roundSource)
        {
            if (roundSource == null)
            {
                throw new ArgumentNullException(nameof(roundSource));
            }

            return roundSource.CreateRound(roomId);
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
            RoundStateMachine round,
            TableRoomDecisionState decisionState,
            TableProjectionContextSource? projectionContextSource = null)
        {
            var actorOptions = new TableActorOptions
            {
                RoomId = roomId,
                InitialControllers = InitialControllers(),
                InitialOwnerEpoch = initialOwnerEpoch,
                DecisionClockConfig = ServiceDecisionClockConfig,
                CheatEconomy = TableProjectionContextSource.DefaultEconomy,
                SleeveBloomConfig = TableProjectionContextSource.DefaultSleeveConfig
            };
            // P5.4b-B2-T: the production settlement authority. The port resolves only the
            // facts the frozen round cannot prove (furiten from the actor's committed
            // snapshot, ippatsu from the committed event chain); the actor still derives
            // every scoring input and calls the frozen calculator itself.
            var settlementFactsPort = new TableServiceSettlementFactsPort();
            TableProjectionContextSource source =
                projectionContextSource ?? new TableProjectionContextSource(decisionState, round);
            TableActor actor = new TableActor(
                actorOptions,
                round,
                commitPort,
                clock,
                publicationSink,
                controlCommitPort: null,
                // D-R1-01: cold recovery injects a source anchored on the room's own verified
                // checkpoint so tsumogiri derivation never consults this process's freshly
                // drawn placeholder round; every other path keeps the bootstrap anchor.
                projectionContextSource: source,
                streamOptions,
                durable,
                settlementFactsPort: settlementFactsPort);
            settlementFactsPort.Bind(
                () => actor.GetRoundStateForHost(),
                () => actor.FuritenForHost);
            // Live projection captures do not carry the furiten snapshot; bind the actor's
            // committed bits for candidate eligibility (still re-validated by the actor).
            decisionState.BindFuriten(() => actor.FuritenForHost);
            // The terminal win projection needs the exact calculation the actor just
            // accepted (yaku/han/fu/score). This is read-only actor evidence; the projection
            // verifies the source/result identity before publishing it.
            source.SettlementProvider = () => actor.LastSettlementCalculation;
            return actor;
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
            var clockConfig = ServiceDecisionClockConfig;
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
            // Checkpoint-0 must carry the FULL initial private aggregate — including the
            // dealt-round registration the actor itself applies to its initial state at
            // construction (TableActor registers the round on TileMarks before serving) —
            // so a cold recovery that has to start from this row rebuilds exactly the
            // cheat version/fingerprint the first commit binds to. A ledger-only blob
            // cannot model that registration (journal replay opens bloom windows but
            // never re-registers the dealt round), and recovery would fail closed with
            // RECOVERY_STATE_HASH_MISMATCH on every room whose first cheat-advancing
            // commit never reached a later checkpoint.
            var initialCheat = new CheatState(
                ledger,
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
            TileMarkTransition dealt = initialCheat.TileMarks.RegisterDealtRound(round.RoundId);
            if (!dealt.Success)
            {
                throw new InvalidOperationException(
                    "The initial cheat state cannot register the initial round: " + dealt.Error + ".");
            }

            byte[] cheatBytes = TableCheatLedgerCheckpointCodec.Encode(
                TableCheatLedgerCheckpoint.FromCheatState(initialCheat.WithTileMarks(dealt.State)));
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

        /// <summary>
        /// Async admission create/recover. The default wraps the synchronous in-memory
        /// factory; a DURABLE implementation must override it: its bootstrap and cold
        /// recovery perform real database I/O and must run as true async work — calling
        /// blocking <c>GetResult()</c> on the session's receive chain deadlocks (the
        /// awaited continuation can require the very thread the blocking call parked).
        /// </summary>
        Task<RoomHost> GetOrAddAsync(string roomId) => Task.FromResult(GetOrAdd(roomId));

        bool TryGet(string roomId, out RoomHost? host);

        IEnumerable<RoomHost> Hosts { get; }
    }

    public sealed class InMemoryTableHostRegistry : ITableHostRegistry
    {
        private readonly ConcurrentDictionary<string, RoomHost> _hosts =
            new ConcurrentDictionary<string, RoomHost>(StringComparer.Ordinal);

        /// <summary>
        /// Create/reserve gate (D-1-06): the room-capacity check and the per-room creation run
        /// under one lock, so a barrier of concurrent GetOrAdd calls can never exceed
        /// MaxRooms, can never run the same room's factory twice and can never create an
        /// orphan actor for a room whose dictionary slot was then handed to another host. The
        /// store/lease bootstrap inside the lock is in-memory, short and synchronous.
        /// </summary>
        private readonly object _createGate = new object();

        private readonly TableServiceOptions _options;
        private readonly ITableStreamContractCodec _codec;
        private readonly ITableClock _clock;
        private readonly ITableRoundSource? _roundSource;

        public InMemoryTableHostRegistry(
            TableServiceOptions options,
            ITableStreamContractCodec codec,
            ITableClock clock,
            ITableRoundSource? roundSource = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _roundSource = roundSource;
        }

        public IEnumerable<RoomHost> Hosts => _hosts.Values;

        public bool TryGet(string roomId, out RoomHost? host)
        {
            host = _hosts.TryGetValue(roomId, out RoomHost? found) ? found : null;
            return host != null;
        }

        /// <summary>True when at least one room can still admit (health/readiness input).</summary>
        public bool HasServingRoom
        {
            get
            {
                foreach (RoomHost host in _hosts.Values)
                {
                    if (!host.AdmissionStopped)
                    {
                        return true;
                    }
                }

                return false;
            }
        }


        /// <summary>
        /// D-1-10 readiness input: an EMPTY registry is genuinely able to admit (its runtime
        /// source, room capacity and lease machinery all exist); only a registry whose every
        /// known room stopped admitting is unhealthy.
        /// </summary>
        public bool AdmittingRoomsCountableOrEmpty => _hosts.Count == 0 || HasServingRoom;

        public RoomHost GetOrAdd(string roomId)
        {
            if (_hosts.TryGetValue(roomId, out RoomHost? existing))
            {
                return existing;
            }

            lock (_createGate)
            {
                if (_hosts.TryGetValue(roomId, out RoomHost? found))
                {
                    return found;
                }

                RoomHost created = CreateHostLocked(roomId);
                _hosts[roomId] = created;
                return created;
            }
        }

        private RoomHost CreateHostLocked(string roomId)
        {
            if (_hosts.Count >= _options.MaxRooms)
            {
                throw new TableServiceCapacityException("ROOM_CAPACITY", "The host registry is at its room capacity.");
            }

            if (_roundSource == null)
            {
                // D-1-01 fail-closed: no trusted round source is bound, the registry never
                // materializes a table from a fabricated wall/projection.
                throw new TableServiceCapacityException(
                    "RUNTIME_SOURCE_UNAVAILABLE",
                    "No trusted room runtime source is configured; the table cannot be served.");
            }

            var scopes = new InMemoryRequestScopeAuthority();
            var store = new InMemoryTableStore(_clock, scopes);
            var decisionState = new TableRoomDecisionState();
            string ownerInstance = "tablesvc-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            // The room's checkpoint-0 row must exist before the first accepted commit (the
            // store answers CHECKPOINT_MISSING "Room is not initialized." otherwise), and the
            // lease must exist before the actor serves: the first tick renewal fails as stale
            // against a missing row and the actor would enter LEASE_LOST (accepted P3.4
            // harness creates the room and acquires the lease before constructing the actor
            // for exactly these reasons).
            RoundStateMachine round = RoomHost.CreateInitialRound(roomId, _roundSource);
            CheatLedger ledger = CheatLedger.Initialize(TableProjectionContextSource.DefaultEconomy);
            TableScopeRecord serviceScope;
            try
            {
                // D-1-01 (round 3): the room-level trusted service scope for the auto-draw
                // pipeline (a draw turn advances through SubmitAsync like any other durable
                // command; its identity is the ROOM's own authority, never a client ticket).
                serviceScope = scopes.Issue(
                    TableRoomHostAutoDraw.ServiceAccountId,
                    TableScopeRole.Player,
                    roomId,
                    (ulong)_clock.UtcNowMs,
                    (ulong)(_clock.UtcNowMs + TableRoomHostAutoDraw.ServiceScopeLifetimeMs));
            }
            catch (Exception exception)
            {
                throw new TableServiceCapacityException(
                    "SERVICE_SCOPE_UNAVAILABLE",
                    "The room's own service scope could not be issued: " + exception.Message);
            }

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
            return new RoomHost(
                roomId,
                _options,
                RoomHost.CreateActor(roomId, _options, commitPort, _clock, null, CreateStreamOptionsLazy(roomId), durable, initialEpoch, round, decisionState),
                new InMemoryTableHostScopeAuthority(scopes),
                store,
                commitPort,
                durable,
                initialEpoch,
                decisionState,
                serviceScope);
        }

        /// <summary>
        /// Stops every owned actor exactly once (server shutdown / runtime stop): the actor's
        /// pump, its streams and its timers never outlive the host (D-1-05).
        /// </summary>
        internal async Task DisposeHostsAsync()
        {
            lock (_createGate)
            {
                foreach (RoomHost host in _hosts.Values)
                {
                    host.StopAdmission("SERVER_STOPPING");
                }
            }

            foreach (RoomHost host in _hosts.Values)
            {
                try
                {
                    await host.Actor.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutdown never fails because of one actor's disposal fault.
                }
            }
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
    ///
    /// D-1-10: the tick AND lease-renewal RESULTS are authoritative entry points 鈥?a failed
    /// renewal, a terminal tick status or a lease-lost/faulted actor lifecycle stops that
    /// table's admission immediately while every other table keeps serving. The Domain
    /// deadlines (player decision deadlines, cheat-offer expiry sweeps) are driven exactly
    /// here on every tick; decision OPENING stays the acting owner's duty (P3.3 timing seam),
    /// so the driver never fabricates a candidate that the Domain has not created.
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
                        TableTickResult tick = await host.Actor
                            .TickAsync(request: null, cancellationToken: stoppingToken)
                            .ConfigureAwait(false);
                        if (tick.Status == TableOperationStatus.Closed)
                        {
                            // The actor is terminal (closed): stop this table's admission
                            // without touching any other table.
                            host.StopAdmission("TABLE_CLOSED");
                            continue;
                        }

                        if (tick.Status == TableOperationStatus.Rejected &&
                            (string.Equals(tick.ReasonCode, TableRuntimeReasonCode.LeaseLost, StringComparison.Ordinal)))
                        {
                            // The tick itself reported lease loss: stop accepting new
                            // connections before they can be consumed into the dead table.
                            host.StopAdmission("LEASE_LOST");
                            continue;
                        }

                        // D-1-10: a returned tick FAILURE (not just a thrown exception) is
                        // authoritative state: stop admission into a dead table.
                        if (tick.Status == TableOperationStatus.Failed &&
                            string.Equals(tick.ReasonCode, TableRuntimeReasonCode.InternalError, StringComparison.Ordinal))
                        {
                            host.StopAdmission("ACTOR_FAULT");
                            continue;
                        }

                        // D-1-01: the acting seat the last committed Domain state requires a
                        // decision for gets its REAL decision opened here (the platform opening
                        // seam), so a legal client action always points at a truly open,
                        // Domain-derived decision instead of a fabricated projection. Round 3:
                        // the drive also owns the phase-DRAW trusted auto-draw (no decision
                        // fabricature for a draw turn, ever).
                        int opened = await host.DecisionState.DriveAsync(host).ConfigureAwait(false);
                        if (opened > 0)
                        {
                            host.PublishDecisionViewsToLiveStreams();
                        }

                        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        long elapsed = nowMs - Interlocked.Read(ref host.LastRenewUtcMs);
                        if (elapsed > _options.LeaseTtlSeconds * 500L)
                        {
                            TableLeaseRenewResult renew = await host.Actor
                                .RenewLeaseAsync(stoppingToken)
                                .ConfigureAwait(false);
                            if (!renew.Renewed)
                            {
                                // Renewal returned a failure (not just a thrown exception): the
                                // lease authority is gone; admission stops before the next
                                // connection can be consumed into a dead table.
                                host.StopAdmission("LEASE_LOST");
                                continue;
                            }

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
