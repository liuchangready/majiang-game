using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;

namespace Mahjong.Infrastructure.Tests
{
    internal sealed class ManualTableClock : ITableClock
    {
        internal long Now { get; set; }

        internal ulong UtcBase { get; set; } = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public long MonotonicMillis => Now;

        public long UtcNowMs => checked((long)UtcBase + Now);
    }

    internal sealed class RecordingPublicationSink : ITablePublicationSink
    {
        private readonly List<TablePublication> _publications = new List<TablePublication>();

        internal IReadOnlyList<TablePublication> Publications => _publications;

        public void Publish(TablePublication publication)
        {
            _publications.Add(publication);
        }
    }

    internal sealed class DirectCommitCandidate
    {
        internal DirectCommitCandidate(
            TableDurableCommitRequest request,
            RoundStateMachine nextRound,
            EventSeq nextLastSeq,
            int eventCount)
        {
            Request = request;
            NextRound = nextRound;
            NextLastSeq = nextLastSeq;
            EventCount = eventCount;
        }

        internal TableDurableCommitRequest Request { get; }

        internal RoundStateMachine NextRound { get; }

        internal EventSeq NextLastSeq { get; }

        internal int EventCount { get; }
    }

    internal sealed class P52Fixture : IAsyncDisposable
    {
        internal const string Room = "p52-room-1";
        internal const string Owner = "p52-owner-1";
        internal const string Boot = "p52-boot-1";
        internal const string Account = "p52-acct-1";

        private P52Fixture(
            PostgresTableStore store,
            ManualTableClock clock,
            RoundStateMachine round,
            TableCheckpoint initialCheckpoint,
            TableScopeRecord scope,
            TableLeaseSnapshot lease,
            string owner)
        {
            Store = store;
            Clock = clock;
            Round = round;
            InitialCheckpoint = initialCheckpoint;
            Scope = scope;
            Lease = lease;
            OwnerInstance = owner;
        }

        internal PostgresTableStore Store { get; }

        internal ManualTableClock Clock { get; }

        internal RoundStateMachine Round { get; }

        internal TableCheckpoint InitialCheckpoint { get; }

        internal TableScopeRecord Scope { get; }

        internal TableLeaseSnapshot Lease { get; }

        internal string OwnerInstance { get; }

        internal static RoundStateMachine Dealt(string roundId)
        {
            var spec = new RoundSpec(
                roundId,
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            TilePositionMap dealt = TilePositionMap.CreateDealt(
                RecordedRandomDraw.Standard("p52-dealt-" + roundId),
                0);
            return RoundStateMachine.CreateDealt(spec, dealt, RoundPhase.CheatWindow);
        }

        internal static TableTimingCheckpoint Timing(
            string roundId,
            string boot,
            ulong capturedAtUtcMs,
            long monotonicMillis = 0L)
        {
            var clock = new DecisionClock(new DecisionClockConfig(1000L, 0L, ReserveScope.Round));
            clock.BeginRound(roundId, monotonicMillis);
            return TableCheckpointFactory.CaptureTiming(
                clock,
                Array.Empty<PendingDecision>(),
                monotonicMillis,
                capturedAtUtcMs,
                boot);
        }

        internal static TableControlCheckpoint Control(long controlVersion = 0L)
        {
            var registry = new ControlRegistry(
                new DisconnectPolicy(),
                new Dictionary<int, SeatControllerKind>
                {
                    [0] = SeatControllerKind.RemoteHuman,
                    [1] = SeatControllerKind.RemoteHuman,
                    [2] = SeatControllerKind.RemoteHuman,
                    [3] = SeatControllerKind.RemoteHuman
                });
            return new TableControlCheckpoint(registry.Capture(), controlVersion);
        }

        internal static TableCheckpoint InitialCheckpointFor(RoundStateMachine round, ulong utcNowMs, string boot)
        {
            TableTimingCheckpoint timing = Timing(round.RoundId, boot, utcNowMs);
            TableControlCheckpoint control = Control();
            return TableCheckpointFactory.Create(
                Room,
                round,
                new StateVersion(0),
                new EventSeq(0),
                control,
                timing,
                1,
                boot,
                utcNowMs,
                cheat: null,
                committedFactsPayload: null,
                timingVersion: 1L,
                capturedMonotonicMillis: 0L,
                committedHistory: null);
        }

        internal static async Task<P52Fixture> CreateAsync(
            CancellationToken cancellationToken = default,
            string owner = Owner,
            TableScopeRole role = TableScopeRole.Player,
            long? scopeIssuedUtcMs = null,
            long? scopeExpiresUtcMs = null,
            bool acquireLease = true,
            int commandTimeoutSeconds = 15,
            TableCheckpoint? initialCheckpoint = null,
            RoundStateMachine? initialRound = null)
        {
            await PostgresTableTestEnvironment.ResetAsync(cancellationToken).ConfigureAwait(false);
            var clock = new ManualTableClock { Now = 1000L };
            ulong nowUtc = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RoundStateMachine round = initialRound ?? Dealt("round-p52");
            TableCheckpoint checkpoint = initialCheckpoint ?? InitialCheckpointFor(round, nowUtc, Boot);
            var options = PostgresTableTestEnvironment.NewOptions(commandTimeoutSeconds);
            var store = new PostgresTableStore(options);
            await store.CreateRoomAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            long issued = scopeIssuedUtcMs ?? checked((long)nowUtc);
            long expires = scopeExpiresUtcMs ?? checked((long)nowUtc + 3_600_000L);
            TableScopeRecord scope = await store.IssueScopeAsync(
                new TableScopeRecord(
                    new RequestScopeId("p52-scope-" + Guid.NewGuid().ToString("N")),
                    Account,
                    role,
                    role == TableScopeRole.Lobby ? null : Room,
                    checked((ulong)issued),
                    checked((ulong)expires)),
                cancellationToken).ConfigureAwait(false);
            TableLeaseSnapshot? lease = null;
            if (acquireLease)
            {
                TableLeaseAcquireResult acquired = await store.AcquireAsync(
                    Room,
                    owner,
                    TimeSpan.FromMinutes(10),
                    cancellationToken).ConfigureAwait(false);
                if (!acquired.Acquired || acquired.Lease == null)
                {
                    throw new InvalidOperationException("Could not acquire the fixture lease: " + acquired.Detail);
                }

                lease = acquired.Lease;
            }

            return new P52Fixture(store, clock, round, checkpoint, scope, lease!, owner);
        }

        internal TableDurableContext DurableContext(string boot = Boot, string? matchId = null)
        {
            var context = new TableDurableContext(
                Store,
                Store,
                Store,
                Store,
                Store,
                OwnerInstance,
                TimeSpan.FromMinutes(10),
                boot,
                1,
                matchId);
            context.RequireCommittedFacts = false;
            return context;
        }

        internal TableActor CreateActor(
            ITablePublicationSink? sink = null,
            TableDurableContext? durable = null,
            ManualTableClock? clock = null,
            string owner = Owner)
        {
            var options = new TableActorOptions
            {
                RoomId = Room,
                MailboxCapacity = 16,
                ControlReserve = 8,
                SnapshotEventWindow = 64,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = new Dictionary<int, SeatControllerKind>
                {
                    [0] = SeatControllerKind.RemoteHuman,
                    [1] = SeatControllerKind.RemoteHuman,
                    [2] = SeatControllerKind.RemoteHuman,
                    [3] = SeatControllerKind.RemoteHuman
                },
                InitialOwnerEpoch = Lease.OwnerEpoch
            };
            return new TableActor(
                options,
                Round,
                new ThrowingCommitPort(),
                clock ?? Clock,
                sink,
                null,
                null,
                null,
                durable ?? DurableContext());
        }

        internal TableCommandEnvelope DiscardEnvelope(
            string requestId,
            string api = "DISCARD",
            string? payload = null,
            TableScopeRecord? scope = null,
            StateVersion? expected = null,
            OwnerEpoch? ownerEpoch = null,
            ControlEpoch? controlEpoch = null,
            RoundCommand? command = null)
        {
            RoundCommand actual = command ?? DiscardCommand(Round);
            TableScopeRecord actualScope = scope ?? Scope;
            byte[] bytes = Encoding.UTF8.GetBytes(
                payload ?? (api + "|" + requestId + "|" + actual.RoundId + "|" + actual.Action + "|" + actual.Seat));
            return new TableCommandEnvelope(
                actual,
                new RequestId(requestId),
                expected ?? new StateVersion(0),
                ownerEpoch ?? Lease.OwnerEpoch,
                controlEpoch ?? new ControlEpoch(0),
                null,
                0,
                actualScope.ScopeId,
                api,
                TablePayloadSha256.Compute(bytes));
        }

        internal static RoundCommand DiscardCommand(RoundStateMachine round)
        {
            int seat = round.CurrentSeat;
            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
            return RoundCommand.Discard(round.RoundId, seat, tile);
        }

        internal DirectCommitCandidate BuildCommit(
            RoundStateMachine state,
            StateVersion expectedStateVersion,
            EventSeq lastEventSeq,
            string requestId,
            TableScopeRecord? scope = null,
            string api = "P52_COMMIT",
            string? businessPayload = null,
            bool terminal = false,
            string? outboxMatchId = null,
            int outboxResultVersion = 1,
            string? outboxPayload = null,
            string? committedFacts = null,
            long? timingVersion = null,
            Receipt? receiptOverride = null,
            ulong? committedAtMs = null,
            PayloadDigest? canonicalDigest = null,
            ReceiptStatus receiptStatus = ReceiptStatus.Applied,
            RoundCommand? command = null,
            byte[]? outboxPayloadBytes = null,
            byte[]? committedFactsPayload = null,
            long nextCheatVersion = 0L,
            ulong nextCheatFingerprint = 0UL)
        {
            TableScopeRecord actualScope = scope ?? Scope;
            RoundCommand actualCommand = command ?? DiscardCommand(state);
            RoundTransition transition = state.Execute(actualCommand);
            if (!transition.IsAccepted || transition.State == null)
            {
                throw new InvalidOperationException("The direct fixture command was not accepted.");
            }

            RoundStateMachine next = transition.State;
            StateVersion commandVersion = expectedStateVersion.Next();
            var events = new List<TableEventRecord>(transition.Events.Count);
            ulong seq = lastEventSeq.Value;
            for (int i = 0; i < transition.Events.Count; i++)
            {
                seq++;
                RoundEvent domainEvent = transition.Events[i];
                events.Add(new TableEventRecord(
                    new EventSeq(seq),
                    commandVersion,
                    i,
                    domainEvent.Type,
                    domainEvent.Seat,
                    domainEvent.Tile,
                    domainEvent.Detail,
                    1,
                    TableEventFactCodec.Encode(commandVersion, i, next.SourceFingerprint)));
            }

            byte[] statePayload = RoundRecoveryCodec.Encode(RoundRecovery.Capture(next));
            byte[] commandPayload = RoundCommandCodec.Encode(actualCommand);
            byte[] businessBytes = Encoding.UTF8.GetBytes(
                businessPayload ?? (api + "|" + requestId));
            TablePayloadSha256 digest = TablePayloadSha256.Compute(businessBytes);
            ulong nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Receipt receiptValue = receiptOverride ??
                (receiptStatus == ReceiptStatus.Recorded
                    ? Receipt.Recorded(
                        expectedStateVersion,
                        commandVersion,
                        new EventSeq(seq),
                        null,
                        committedAtMs,
                        canonicalDigest)
                    : Receipt.Applied(
                        expectedStateVersion,
                        commandVersion,
                        new EventSeq(seq),
                        null,
                        committedAtMs,
                        canonicalDigest));
            TableDurableReceipt receipt = new TableDurableReceipt(
                actualScope.AccountId,
                actualScope.ScopeId,
                new RequestId(requestId),
                api,
                digest,
                receiptValue,
                string.Empty,
                nowMs,
                nowMs + 3_600_000UL);
            var journal = new TableCommandJournalRecord(
                commandVersion,
                expectedStateVersion,
                new RequestId(requestId),
                actualScope.AccountId,
                actualScope.ScopeId,
                api,
                digest,
                commandPayload,
                RoundCommandCodec.FormatVersion);

            var outbox = new List<TableSettlementOutboxRecord>();
            if (terminal && outboxMatchId != null)
            {
                byte[] payload = outboxPayloadBytes ??
                    Encoding.UTF8.GetBytes(outboxPayload ?? ("OUTBOX|" + outboxMatchId + "|" + outboxResultVersion));
                outbox.Add(new TableSettlementOutboxRecord(
                    outboxMatchId,
                    outboxResultVersion,
                    payload,
                    TablePayloadSha256.Compute(payload),
                    nowMs));
            }

            byte[]? facts = committedFactsPayload ??
                (committedFacts == null ? null : Encoding.UTF8.GetBytes(committedFacts));
            TableTimingCheckpoint timing = Timing(next.RoundId, Boot, nowMs, Clock.Now);
            long actualTimingVersion = timingVersion ?? 2L;
            var request = new TableDurableCommitRequest(
                Room,
                OwnerInstance,
                Lease.OwnerEpoch,
                expectedStateVersion,
                receipt,
                false,
                events,
                journal,
                outbox,
                null,
                statePayload,
                next.SourceFingerprint,
                terminal,
                timing,
                actualTimingVersion,
                facts,
                facts == null ? (TablePayloadSha256?)null : TablePayloadSha256.Compute(facts),
                nextCheatVersion,
                nextCheatFingerprint);
            return new DirectCommitCandidate(request, next, new EventSeq(seq), events.Count);
        }

        internal TableDurableCommitRequest BuildRejectedCommit(
            StateVersion expectedStateVersion,
            string requestId,
            Receipt receipt,
            TableScopeRecord? scope = null,
            string api = "P52_REJECT")
        {
            TableScopeRecord actualScope = scope ?? Scope;
            ulong nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var durable = new TableDurableReceipt(
                actualScope.AccountId,
                actualScope.ScopeId,
                new RequestId(requestId),
                api,
                TablePayloadSha256.Compute(Encoding.UTF8.GetBytes("reject|" + requestId)),
                receipt,
                string.Empty,
                nowMs,
                nowMs + 3_600_000UL);
            return new TableDurableCommitRequest(
                Room,
                OwnerInstance,
                Lease.OwnerEpoch,
                expectedStateVersion,
                durable,
                false,
                Array.Empty<TableEventRecord>(),
                null,
                Array.Empty<TableSettlementOutboxRecord>(),
                null,
                null,
                0UL,
                false);
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal sealed class ThrowingCommitPort : ITableCommitPort
    {
        public ValueTask<TableCommitResult> CommitAsync(
            TableCommitRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The legacy commit port must not be used by a durable actor.");
        }
    }
}
