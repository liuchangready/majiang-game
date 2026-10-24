using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    internal sealed class RecordingRecoveryStateSink : ITableRecoveryStateSink
    {
        public TableRecoveryAuxState? Installed { get; private set; }

        public void Install(TableRecoveryAuxState state)
        {
            Installed = state;
        }
    }

    internal sealed class DurableHarness : IAsyncDisposable
    {
        public const string Room = "room-1";
        public const string Owner = "owner-1";
        public const string Boot = "boot-1";
        public const string Account = "acct-1";
        public const long UtcBase = 1700000000000L;

        private DurableHarness(
            ManualTableClock clock,
            InMemoryRequestScopeAuthority scopes,
            InMemoryTableStore store,
            RecordingCommitPort legacy,
            RecordingPublicationSink sink,
            TableActor actor,
            RoundStateMachine initialRound,
            TableScopeRecord scope,
            CheatLedger ledger,
            TableDurableContext durable,
            ITableProjectionContextSource? projectionSource,
            TableStreamOptions? streamOptions)
        {
            Clock = clock;
            Scopes = scopes;
            Store = store;
            Legacy = legacy;
            Sink = sink;
            Actor = actor;
            InitialRound = initialRound;
            Scope = scope;
            Ledger = ledger;
            Durable = durable;
            ProjectionSource = projectionSource;
            StreamOptions = streamOptions;
        }

        public ManualTableClock Clock { get; }

        public InMemoryRequestScopeAuthority Scopes { get; }

        public InMemoryTableStore Store { get; }

        public RecordingCommitPort Legacy { get; }

        public RecordingPublicationSink Sink { get; }

        public TableActor Actor { get; }

        public RoundStateMachine InitialRound { get; }

        public TableScopeRecord Scope { get; }

        public CheatLedger Ledger { get; }

        public TableDurableContext Durable { get; }

        public ITableProjectionContextSource? ProjectionSource { get; }

        public TableStreamOptions? StreamOptions { get; }

        public static TableCheckpoint InitialCheckpoint(
            RoundStateMachine round,
            CheatLedger ledger,
            TableControlCheckpoint? control = null,
            TableTimingCheckpoint? timing = null,
            string boot = Boot,
            ulong utc = (ulong)UtcBase,
            int schemaVersion = TableCheckpointFactory.SchemaVersion)
        {
            var clock = new DecisionClock(new DecisionClockConfig(1000L, 0L, ReserveScope.Round));
            clock.BeginRound(round.RoundId, 0L);
            TableTimingCheckpoint actualTiming = timing ??
                TableCheckpointFactory.CaptureTiming(
                    clock,
                    Array.Empty<PendingDecision>(),
                    0L,
                    utc,
                    boot);
            TableControlCheckpoint actualControl = control ??
                new TableControlCheckpoint(DefaultControl(), 0L);
            byte[] state = RoundRecoveryCodec.Encode(RoundRecovery.Capture(round));
            byte[] controlBytes = TableControlCheckpointCodec.Encode(actualControl);
            byte[] timingBytes = TableTimingCheckpointCodec.Encode(actualTiming);
            byte[] cheatBytes = TableCheatLedgerCheckpointCodec.Encode(TableCheatLedgerCheckpoint.FromLedger(ledger));
            byte[] facts = TableCommittedFactsCodec.Encode(
                round.RoundId,
                new StateVersion(0),
                Encoding.UTF8.GetBytes(Room + ":facts"));
            byte[] history = TableCommittedHistoryCodec.Encode(Array.Empty<TableEventRecord>());
            return new TableCheckpoint(
                Room,
                new StateVersion(0),
                new EventSeq(0),
                round.SourceFingerprint,
                1,
                state,
                TablePayloadSha256.Compute(state),
                actualControl.ControlVersion,
                controlBytes,
                TablePayloadSha256.Compute(controlBytes),
                timingBytes,
                TablePayloadSha256.Compute(timingBytes),
                cheatBytes,
                TablePayloadSha256.Compute(cheatBytes),
                facts,
                TablePayloadSha256.Compute(facts),
                utc,
                boot,
                schemaVersion,
                1L,
                0L,
                history,
                TablePayloadSha256.Compute(history));
        }

        public static ControlRegistrySnapshot DefaultControl()
        {
            return new ControlRegistry(
                new DisconnectPolicy(),
                P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman)).Capture();
        }

        public static CheatLedger NewLedger()
        {
            return CheatLedger.Initialize(new CheatEconomyConfig(
                true,
                40,
                new Dictionary<string, int>()));
        }

        public static async Task<DurableHarness> CreateAsync(
            string owner = Owner,
            ulong ownerEpoch = 1UL,
            string boot = Boot,
            string account = Account,
            CheatLedger? ledger = null,
            TableCheckpoint? initialCheckpoint = null,
            TableScopeRole role = TableScopeRole.Player,
            string? scopeRoom = Room,
            bool acquireLease = true,
            Action<TableActorOptions>? configure = null,
            ITableProjectionContextSource? projectionSource = null,
            TableStreamOptions? streamOptions = null,
            ITableRecoveryStateSink? recoverySink = null)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var scopes = new InMemoryRequestScopeAuthority();
            TableScopeRecord scope = scopes.Issue(
                account,
                role,
                scopeRoom,
                (ulong)UtcBase,
                (ulong)UtcBase + 3_600_000UL);
            var store = new InMemoryTableStore(clock, scopes);
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatLedger actualLedger = ledger ?? NewLedger();
            TableCheckpoint checkpoint = initialCheckpoint ?? InitialCheckpoint(round, actualLedger);
            await store.CreateRoomAsync(checkpoint, CancellationToken.None);
            if (acquireLease)
            {
                TableLeaseAcquireResult lease = await store.AcquireAsync(
                    Room,
                    owner,
                    TimeSpan.FromMinutes(10),
                    CancellationToken.None);
                Assert.True(lease.Acquired, lease.Detail);
            }

            var durable = new TableDurableContext(
                store,
                store,
                store,
                store,
                scopes,
                owner,
                TimeSpan.FromMinutes(10),
                boot,
                1,
                Room);
            durable.CheatLedgerProvider = () => actualLedger;
            durable.CheatStateProvider = () => new CheatState(
                actualLedger,
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
            durable.CommittedFactsProvider = () => Encoding.UTF8.GetBytes(Room + ":facts");
            durable.RecoveryStateSink = recoverySink ?? new RecordingRecoveryStateSink();

            var legacy = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(legacy);
            var options = new TableActorOptions
            {
                RoomId = Room,
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                InitialOwnerEpoch = new OwnerEpoch(ownerEpoch)
            };
            configure?.Invoke(options);
            var actor = new TableActor(
                options,
                round,
                legacy,
                clock,
                sink,
                null,
                projectionSource,
                streamOptions,
                durable);
            return new DurableHarness(
                clock,
                scopes,
                store,
                legacy,
                sink,
                actor,
                round,
                scope,
                actualLedger,
                durable,
                projectionSource,
                streamOptions);
        }

        /// <summary>Wires another actor to the same store/clock/scope for stale-owner and dual-writer tests.</summary>
        public static DurableHarness Attach(
            DurableHarness source,
            string owner,
            ulong ownerEpoch,
            ITableRecoveryStateSink? recoverySink = null)
        {
            var durable = new TableDurableContext(
                source.Store,
                source.Store,
                source.Store,
                source.Store,
                source.Scopes,
                owner,
                TimeSpan.FromMinutes(10),
                Boot,
                1,
                Room);
            durable.CheatLedgerProvider = () => source.Ledger;
            durable.CheatStateProvider = () => new CheatState(
                source.Ledger,
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
            durable.CommittedFactsProvider = () => Encoding.UTF8.GetBytes(Room + ":facts");
            durable.RecoveryStateSink = recoverySink ?? new RecordingRecoveryStateSink();
            var legacy = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(legacy);
            var options = new TableActorOptions
            {
                RoomId = Room,
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                InitialOwnerEpoch = new OwnerEpoch(ownerEpoch)
            };
            var actor = new TableActor(
                options,
                source.InitialRound,
                legacy,
                source.Clock,
                sink,
                null,
                source.ProjectionSource,
                source.StreamOptions,
                durable);
            return new DurableHarness(
                source.Clock,
                source.Scopes,
                source.Store,
                legacy,
                sink,
                actor,
                source.InitialRound,
                source.Scope,
                source.Ledger,
                durable,
                source.ProjectionSource,
                source.StreamOptions);
        }

        public TableCommandEnvelope Envelope(
            RoundCommand command,
            int expectedVersion,
            string requestId,
            string api = "DISCARD",
            string? payloadText = null,
            TableScopeRecord? scope = null,
            ulong ownerEpoch = 1UL,
            ulong controlEpoch = 0UL,
            long recoveryGeneration = 0L,
            string? decisionId = null)
        {
            TableScopeRecord actual = scope ?? Scope;
            byte[] bytes = Encoding.UTF8.GetBytes(
                payloadText ?? (api + "|" + requestId + "|" + command.RoundId + "|" + command.Action + "|" + command.Seat));
            return new TableCommandEnvelope(
                command,
                new RequestId(requestId),
                new StateVersion((ulong)expectedVersion),
                new OwnerEpoch(ownerEpoch),
                new ControlEpoch(controlEpoch),
                decisionId == null ? (DecisionId?)null : new DecisionId(decisionId),
                recoveryGeneration,
                actual.ScopeId,
                api,
                TablePayloadSha256.Compute(bytes));
        }

        public ValueTask<TableOperationResult> SubmitAsync(TableCommandEnvelope command)
        {
            return Actor.SubmitAsync(command, TestContext.Current.CancellationToken);
        }

        public async Task<TableCheckpoint> SaveCheckpointAsync()
        {
            TableOperationResult result = await Actor.SaveCheckpointAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Configured, result.Status);
            TableCheckpoint? checkpoint = await Store.LoadCheckpointAsync(Room, TestContext.Current.CancellationToken);
            Assert.NotNull(checkpoint);
            return checkpoint!;
        }

        public async Task CloseAsync()
        {
            await Actor.CloseAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Actor.DisposeAsync().ConfigureAwait(false);
        }
    }
}
