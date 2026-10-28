using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.Infrastructure;
using Mahjong.TableRuntime;

namespace Mahjong.TableService
{
    /// <summary>
    /// The durable room registry (P5.4b-A): every room's events, receipts, checkpoints,
    /// request scopes, lease and settlement outbox live in the P5.2 <c>table_store</c>
    /// PostgreSQL database through the SAME accepted port family the actors commit
    /// through — there is no memory shadow and no hybrid wiring.
    ///
    /// Room lifecycle:
    /// - A room with no <c>table_state</c> row is bootstrapped atomically: service scope
    ///   row, checkpoint-0 row (INSERT fails closed on a concurrent creator), then the
    ///   lease acquire that decides ownership.
    /// - A room WITH state is cold-recovered through the accepted TableRuntime API —
    ///   <see cref="TableActor.RecoverFromStoreAsync"/> — which acquires a fresh owner
    ///   epoch, replays the checkpoint + command journal through the real Domain, verifies
    ///   hashes/fingerprints/136 tiles/sequence continuity, and installs the baseline. No
    ///   replay engine is written here and no WELCOME, playable baseline or command is
    ///   served before that recovery completes: the host only becomes visible after
    ///   <c>GetOrAddAsync</c> returns.
    ///
    /// Failure discipline: a bad configuration, an unreachable database, a failed
    /// migration check, a lease held by another live owner, or any recovery failure throws
    /// <see cref="TableServiceCapacityException"/> — the admission path maps that to a
    /// clean SERVER_BUSY refusal. This registry NEVER falls back to the in-memory
    /// implementation.
    ///
    /// Concurrency: <see cref="GetOrAddAsync"/> serializes creation per process under one
    /// ASYNC gate (a <c>SemaphoreSlim</c>, never a monitor lock): bootstrap/recovery does
    /// real database I/O as awaited work, because blocking <c>GetResult()</c> on the
    /// session's receive chain deadlocks — the awaited continuation can require the very
    /// thread the blocking call parked. An actor's per-command durable commits stay fully
    /// asynchronous inside the actor's own serial pipeline. The synchronous
    /// <see cref="ITableHostRegistry.GetOrAdd"/> exists for the accepted in-memory
    /// contract only; this durable registry fails closed if it is ever called.
    /// Cross-process races are decided by the store: the checkpoint insert and the lease
    /// upsert each have exactly one winner, and every loser fails closed instead of
    /// duplicating or overwriting a wall, ledger or receipt.
    /// </summary>
    public sealed class PostgresTableHostRegistry : ITableHostRegistry
    {
        private readonly ConcurrentDictionary<string, RoomHost> _hosts =
            new ConcurrentDictionary<string, RoomHost>(StringComparer.Ordinal);

        // Async gate, never a monitor lock: creation/recovery performs real database I/O
        // and must park the caller with await, not with a blocking GetResult (sync-over-
        // async on the session's receive chain deadlocks the admission).
        private readonly SemaphoreSlim _createGate = new SemaphoreSlim(1, 1);

        private readonly TableServiceOptions _options;
        private readonly ITableStreamContractCodec _codec;
        private readonly ITableClock _clock;
        private readonly ITableRoundSource? _roundSource;
        private readonly PostgresTableStore _store;
        private readonly ITableHostScopeAuthority _scopes;
        private readonly string _ownerInstance;
        private readonly TimeSpan _leaseTtl;

        private readonly ConcurrentDictionary<string, string> _failedRooms =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public PostgresTableHostRegistry(
            TableServiceOptions options,
            ITableStreamContractCodec codec,
            ITableClock clock,
            ITableRoundSource? roundSource,
            PostgresTableStore store,
            ITableHostScopeAuthority scopes,
            string ownerInstance)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _roundSource = roundSource;
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
            if (string.IsNullOrWhiteSpace(ownerInstance))
            {
                throw new ArgumentException("Owner instance cannot be blank.", nameof(ownerInstance));
            }

            _ownerInstance = ownerInstance;
            _leaseTtl = TimeSpan.FromSeconds(_options.LeaseTtlSeconds);
        }

        public IEnumerable<RoomHost> Hosts => _hosts.Values;

        public string OwnerInstance => _ownerInstance;

        private static readonly string PerProcessBootId = Guid.NewGuid().ToString("N");

        /// <summary>
        /// The PROCESS boot identity, shared by every registry this process ever builds: the
        /// recovery contract distinguishes same-boot recovery (a trusted monotonic clock
        /// domain — a runtime rebuilt inside the living process) from cross-boot recovery (a
        /// fresh process, where persisted monotonic deadlines are untrustworthy). A new Guid
        /// per registry instance would mislabel an in-process owner handover as a reboot.
        /// </summary>
        public string ProcessBootId { get; } = PerProcessBootId;

        /// <summary>
        /// Rooms that exist durably but could not be served by THIS process (recovery
        /// failure, foreign live lease). Readiness must count them: a known-but-unservable
        /// room is not a healthy registry, and a silent "looks empty" would green-light a
        /// host that refuses every admission for that room.
        /// </summary>
        public IReadOnlyDictionary<string, string> FailedRooms => _failedRooms;

        public bool TryGet(string roomId, out RoomHost? host)
        {
            host = _hosts.TryGetValue(roomId, out RoomHost? found) ? found : null;
            return host != null;
        }

        /// <summary>
        /// The accepted in-memory contract's synchronous admission. A durable registry
        /// cannot honor it — its bootstrap/recovery is real database I/O and blocking the
        /// session's receive chain on it deadlocks — so this fails closed rather than
        /// pretending.
        /// </summary>
        RoomHost ITableHostRegistry.GetOrAdd(string roomId)
        {
            throw new InvalidOperationException(
                "The durable table host registry admits only through GetOrAddAsync; " +
                "synchronous admission would block real database I/O on the receive chain.");
        }

        public async Task<RoomHost> GetOrAddAsync(string roomId)
        {
            if (_hosts.TryGetValue(roomId, out RoomHost? existing))
            {
                return existing;
            }

            await _createGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_hosts.TryGetValue(roomId, out RoomHost? found))
                {
                    return found;
                }

                RoomHost created;
                try
                {
                    created = await CreateHostAsync(roomId).ConfigureAwait(false);
                }
                catch (TableServiceCapacityException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Fail closed, never fall back to memory. The wrapped message carries a
                    // redacted exception identity only — never a connection string, key or
                    // private payload.
                    _failedRooms[roomId] = "HOST_CREATE_FAILED";
                    Console.Error.WriteLine(
                        "tablesvc: room host create refused room=" + roomId +
                        " detail=" + Redact(exception));
                    throw new TableServiceCapacityException(
                        "PERSISTENT_HOST_UNAVAILABLE",
                        Redact(exception));
                }

                _failedRooms.TryRemove(roomId, out _);
                _hosts[roomId] = created;
                return created;
            }
            finally
            {
                _createGate.Release();
            }
        }

        private async Task<RoomHost> CreateHostAsync(string roomId)
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

            TableStateRow? state = await _store
                .LoadStateAsync(roomId, CancellationToken.None)
                .ConfigureAwait(false);
            return state == null
                ? await BootstrapNewRoomAsync(roomId).ConfigureAwait(false)
                : await RecoverExistingRoomAsync(roomId).ConfigureAwait(false);
        }

        /// <summary>
        /// Fresh-room bootstrap: the same accepted sequence as the in-memory registry —
        /// service scope, checkpoint-0, lease — with every write landing in table_store.
        /// A concurrent creator loses at the checkpoint insert ("Room already exists.")
        /// and fails closed; the winner's lease decides ownership.
        /// </summary>
        private async Task<RoomHost> BootstrapNewRoomAsync(string roomId)
        {
            RoundStateMachine round = RoomHost.CreateInitialRound(roomId, _roundSource!);
            CheatLedger ledger = CheatLedger.Initialize(TableProjectionContextSource.DefaultEconomy);
            ulong nowMs = (ulong)_clock.UtcNowMs;

            TableScopeRecord serviceScope = await IssueServiceScopeAsync(roomId, nowMs).ConfigureAwait(false);

            try
            {
                await _store.CreateRoomAsync(
                        RoomHost.CreateInitialCheckpoint(
                            roomId,
                            round,
                            ledger,
                            new OwnerEpoch(RoomHost.InitialOwnerEpochValue),
                            ProcessBootId,
                            nowMs),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("Room already exists", StringComparison.Ordinal))
            {
                // Another process created this room between our presence probe and the
                // insert. We own nothing of it: fail closed and let the next admission take
                // the recovery path against the real owner's rows.
                throw new TableServiceCapacityException(
                    "ROOM_ALREADY_EXISTS",
                    "Another host created this room during bootstrap.");
            }

            TableLeaseAcquireResult lease = await _store
                .AcquireAsync(roomId, _ownerInstance, _leaseTtl, CancellationToken.None)
                .ConfigureAwait(false);
            if (!lease.Acquired || lease.Lease == null)
            {
                _failedRooms[roomId] = "LEASE_UNAVAILABLE";
                Console.Error.WriteLine(
                    "tablesvc: room lease unavailable room=" + roomId +
                    " detail=" + (lease.Detail ?? string.Empty));
                throw new TableServiceCapacityException(
                    "LEASE_UNAVAILABLE",
                    lease.Detail ?? "The room lease is held by another live owner.");
            }

            OwnerEpoch initialEpoch = lease.Lease.OwnerEpoch;
            TableServiceCommitPort commitPort = new TableServiceCommitPort(initialEpoch);
            TableDurableContext durable = BuildDurableContext(roomId, ledger);
            var decisionState = new TableRoomDecisionState();
            return new RoomHost(
                roomId,
                _options,
                RoomHost.CreateActor(
                    roomId,
                    _options,
                    commitPort,
                    _clock,
                    null,
                    RoomHost.CreateStreamOptions(_options, _codec, new LazyRoomTerminationShim(this, roomId)),
                    durable,
                    initialEpoch,
                    round,
                    decisionState),
                _scopes,
                _store,
                commitPort,
                durable,
                initialEpoch,
                decisionState,
                serviceScope);
        }

        /// <summary>
        /// Cold recovery of an EXISTING room through the accepted TableRuntime API. The
        /// actor is constructed with a live initial round exactly like the accepted
        /// TableRuntime harness; <see cref="TableActor.RecoverFromStoreAsync"/> replaces it
        /// with the verified baseline. Until that call returns, the host is not visible to
        /// any session: no WELCOME, no playable baseline, no command.
        /// </summary>
        private async Task<RoomHost> RecoverExistingRoomAsync(string roomId)
        {
            RoundStateMachine round = RoomHost.CreateInitialRound(roomId, _roundSource!);
            CheatLedger ledger = CheatLedger.Initialize(TableProjectionContextSource.DefaultEconomy);
            TableServiceCommitPort commitPort = new TableServiceCommitPort(
                new OwnerEpoch(RoomHost.InitialOwnerEpochValue));
            TableDurableContext durable = BuildDurableContext(roomId, ledger);
            var decisionState = new TableRoomDecisionState();
            TableActor actor = RoomHost.CreateActor(
                roomId,
                _options,
                commitPort,
                _clock,
                null,
                RoomHost.CreateStreamOptions(_options, _codec, new LazyRoomTerminationShim(this, roomId)),
                durable,
                new OwnerEpoch(RoomHost.InitialOwnerEpochValue),
                round,
                decisionState);

            TableRecoveryResult recovery = await actor
                .RecoverFromStoreAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (!recovery.Recovered || recovery.Baseline == null)
            {
                try
                {
                    await actor.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The refused actor's disposal fault must not mask the recovery failure.
                }

                _failedRooms[roomId] = recovery.ReasonCode;
                Console.Error.WriteLine(
                    "tablesvc: cold recovery refused room=" + roomId +
                    " code=" + recovery.ReasonCode +
                    " detail=" + (recovery.Detail ?? string.Empty));
                throw new TableServiceCapacityException(
                    "RECOVERY_FAILED",
                    recovery.ReasonCode + ": " + recovery.Detail);
            }

            // The recovered owner epoch is the lease the recovery service acquired; the
            // commit port is inert while the durable context owns commits, but it carries
            // the same epoch for coherence. The recovery generation comes from the actor's
            // own control snapshot: every later envelope (subscribe, command, auto-draw)
            // must carry it or the strict recovery fence refuses the actor.
            OwnerEpoch ownerEpoch = recovery.Baseline.OwnerEpoch;
            TableControlSnapshot control = await actor
                .GetControlSnapshotAsync(CancellationToken.None)
                .ConfigureAwait(false);
            TableScopeRecord serviceScope = await IssueServiceScopeAsync(roomId, (ulong)_clock.UtcNowMs).ConfigureAwait(false);
            return new RoomHost(
                roomId,
                _options,
                actor,
                _scopes,
                _store,
                commitPort,
                durable,
                ownerEpoch,
                decisionState,
                serviceScope,
                control.RecoveryGeneration);
        }

        /// <summary>
        /// The room's own trusted service scope (D-1-01 round 3), issued as a real
        /// table_store row so the auto-draw envelope's durable scope check reads the SAME
        /// database every player scope lives in.
        /// </summary>
        private async Task<TableScopeRecord> IssueServiceScopeAsync(string roomId, ulong nowMs)
        {
            var scope = new TableScopeRecord(
                new RequestScopeId(Guid.NewGuid().ToString("N")),
                TableRoomHostAutoDraw.ServiceAccountId,
                TableScopeRole.Player,
                roomId,
                nowMs,
                nowMs + (ulong)TableRoomHostAutoDraw.ServiceScopeLifetimeMs);
            try
            {
                return await _scopes.IssueScopeAsync(scope, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _failedRooms[roomId] = "SERVICE_SCOPE_UNAVAILABLE";
                throw new TableServiceCapacityException(
                    "SERVICE_SCOPE_UNAVAILABLE",
                    "The room's own service scope could not be issued: " + Redact(exception));
            }
        }

        private TableDurableContext BuildDurableContext(string roomId, CheatLedger ledger)
        {
            var cheatHolder = new RoomCheatStateHolder(ledger);
            var durable = new TableDurableContext(
                _store,
                _store,
                _store,
                _store,
                _scopes,
                _ownerInstance,
                _leaseTtl,
                ProcessBootId,
                rulesetVersion: (int)ProtocolConstants.RulesetVersion);
            durable.CheatLedgerProvider = () => cheatHolder.Current.Ledger;
            durable.CheatStateProvider = () => cheatHolder.Current;
            // The accepted recovery contract REQUIRES a sink whenever the verified baseline
            // carries cheat/private/facts state: without it the actor must not enter Serving.
            // The install swaps this room's providers onto the recovered aggregate, so every
            // later commit binds to the SAME verified state the actor itself adopted — never
            // to the freshly-initialized placeholder this process started with.
            durable.RecoveryStateSink = new RecoveryCheatStateSink(cheatHolder, roomId);
            return durable;
        }

        /// <summary>
        /// The room's live cheat state, behind one mutable holder so cold recovery can swap
        /// in the verified aggregate. Access is confined to the actor's serial pipeline
        /// (providers during commits, install during recovery) — there is no concurrent
        /// reader to synchronize against.
        /// </summary>
        private sealed class RoomCheatStateHolder
        {
            private CheatState _current;

            public RoomCheatStateHolder(CheatLedger initialLedger)
            {
                _current = new CheatState(
                    initialLedger,
                    new TileMarkState(),
                    new SleeveBloomState(),
                    new BeamSwapState());
            }

            public CheatState Current => _current;

            public void InstallVerified(CheatState verified)
            {
                _current = verified;
            }
        }

        /// <summary>
        /// Installs the verified cold-recovery cheat aggregate into the room's providers.
        /// Per the accepted sink contract it THROWS when the exact state cannot be installed,
        /// which leaves the actor non-Serving instead of serving a fabricated economy.
        /// </summary>
        private sealed class RecoveryCheatStateSink : ITableRecoveryStateSink
        {
            private readonly RoomCheatStateHolder _holder;
            private readonly string _roomId;

            public RecoveryCheatStateSink(RoomCheatStateHolder holder, string roomId)
            {
                _holder = holder;
                _roomId = roomId;
            }

            public void Install(TableRecoveryAuxState state)
            {
                if (state.Cheat == null)
                {
                    throw new InvalidOperationException(
                        "The verified recovery baseline carries no cheat aggregate to install.");
                }

                if (!string.Equals(state.RoomId, _roomId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The verified recovery baseline belongs to another room.");
                }

                _holder.InstallVerified(state.Cheat);
            }
        }

        private static string Redact(Exception exception)
        {
            string message = exception.Message ?? string.Empty;
            if (message.Length > 200)
            {
                message = message.Substring(0, 200);
            }

            return exception.GetType().Name + ": " + message;
        }

        /// <summary>
        /// Stops every owned actor exactly once (server shutdown / runtime stop): the
        /// actor's pump, its streams and its timers never outlive the host (D-1-05). The
        /// PostgreSQL store itself is process-wide and is disposed by the host runtime,
        /// after every actor is gone.
        /// </summary>
        internal async Task DisposeHostsAsync()
        {
            // Hold the gate only for the admission sweep: no create/recover may publish a
            // host while shutdown is stopping admissions. The disposal loop itself runs
            // outside the gate (listeners are already stopped, nothing can admit).
            await _createGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (RoomHost host in _hosts.Values)
                {
                    host.StopAdmission("SERVER_STOPPING");
                }
            }
            finally
            {
                _createGate.Release();
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

        private sealed class LazyRoomTerminationShim : ITableStreamTerminationSink
        {
            private readonly PostgresTableHostRegistry _registry;
            private readonly string _roomId;

            public LazyRoomTerminationShim(PostgresTableHostRegistry registry, string roomId)
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
}
