using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Npgsql;

namespace Mahjong.Infrastructure.Tests
{
    /// <summary>
    /// Failure-only diagnostics for the P5.2 real-PostgreSQL harness. These files are
    /// written under MAHJONG_P52_PG_EVIDENCE and never contain credentials, payload
    /// keys or business payload bytes.
    /// </summary>
    internal static class P52Diagnostics
    {
        internal static void RecordFixtureFailure(string phase, TimeSpan elapsed, Exception exception)
        {
            Record(
                "fixture-failure",
                "phase=" + phase + Environment.NewLine +
                "elapsedMs=" + ((long)elapsed.TotalMilliseconds) + Environment.NewLine +
                "exception=" + exception.GetType().FullName + Environment.NewLine +
                "message=" + Sanitize(exception.Message) + Environment.NewLine);
        }

        internal static async Task RecordActorFailureAsync(
            string label,
            TableOperationResult result,
            TimeSpan elapsed,
            CancellationToken cancellationToken)
        {
            var text = new StringBuilder();
            text.AppendLine("label=" + label);
            text.AppendLine("elapsedMs=" + ((long)elapsed.TotalMilliseconds));
            text.AppendLine("status=" + result.Status);
            text.AppendLine("reasonCode=" + result.ReasonCode);
            text.AppendLine("detail=" + Sanitize(result.Detail));
            text.AppendLine("evaluatedStateVersion=" + result.EvaluatedStateVersion.Value);
            text.AppendLine("probe=" + await ProbeAsync(cancellationToken).ConfigureAwait(false));
            Record("actor-failure", text.ToString());
        }

        internal static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return Regex.Replace(
                value,
                @"(?i)(password|pwd)\s*=\s*[^;]*",
                "$1=<redacted>").Replace("\r", " ").Replace("\n", " ");
        }

        private static async Task<string> ProbeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(
                    PostgresTableTestEnvironment.AdminConnectionString)
                {
                    Timeout = 3,
                    CommandTimeout = 3,
                    Pooling = false
                };
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = new NpgsqlCommand(
                    @"SELECT now()::text,
                             current_setting('max_connections'),
                             (SELECT count(*) FROM pg_stat_activity),
                             (SELECT count(*) FROM pg_stat_activity
                               WHERE application_name = 'mahjong-table-p52'),
                             pg_postmaster_start_time()::text",
                    connection)
                {
                    CommandTimeout = 3
                };
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return "probe_empty";
                }

                return "dbNow=" + reader.GetString(0) +
                       "; maxConnections=" + reader.GetString(1) +
                       "; allConnections=" + reader.GetInt64(2) +
                       "; p52Connections=" + reader.GetInt64(3) +
                       "; postmasterStart=" + reader.GetString(4);
            }
            catch (Exception exception)
            {
                return "probeError=" + exception.GetType().FullName + ": " + Sanitize(exception.Message);
            }
        }

        private static void Record(string name, string content)
        {
            try
            {
                PostgresTableTestEnvironment.AppendEvidence(
                    name + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".txt",
                    content);
            }
            catch
            {
                // Diagnostics must never replace the original test failure.
            }
        }
    }

    /// <summary>
    /// Real database-clock scope boundary helper. It identifies the blocked P52
    /// transaction, derives an expiry strictly between that transaction's
    /// xact_start and the database clock observed just before releasing the row
    /// lock, and only then releases it. A transaction-start/cached clock mutant
    /// therefore sees a scope that is still valid at xact_start and accepts it,
    /// while the real clock_timestamp implementation rejects it.
    /// </summary>
    internal static class P52ScopeBoundary
    {
        internal static async Task<TableDurableCommitResult> CommitAfterExpiryCrossesTransactionStartAsync(
            P52Fixture fixture,
            DirectCommitCandidate candidate,
            CancellationToken cancellationToken)
        {
            await using var blocker = new NpgsqlConnection(PostgresTableTestEnvironment.AdminConnectionString);
            await blocker.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlTransaction hold = await blocker.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT scope_id FROM table_store.request_scope WHERE scope_id = @scope FOR UPDATE",
                blocker,
                hold))
            {
                lockCommand.Parameters.AddWithValue("scope", fixture.Scope.ScopeId.Value);
                object? locked = await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(locked as string, fixture.Scope.ScopeId.Value, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The fixture scope row was not locked.");
                }
            }

            Task<TableDurableCommitResult> commit = fixture.Store.CommitAsync(
                candidate.Request,
                cancellationToken).AsTask();
            await WaitForRowLockWaitAsync(cancellationToken).ConfigureAwait(false);
            if (commit.IsCompleted)
            {
                throw new InvalidOperationException("The commit completed before the scope row lock was observed.");
            }

            ScopeBoundaryObservation observation = await ObserveBlockedCommitAsync(cancellationToken)
                .ConfigureAwait(false);
            long issued = checked((long)fixture.Scope.IssuedAtUtcMs);
            long transactionStart = observation.TransactionStartUtcMs;
            long expiry = Math.Max(transactionStart + 1L, checked(issued + 1L));
            if (expiry <= transactionStart)
            {
                throw new InvalidOperationException(
                    "Could not place the expiry after the blocked transaction start: txnStart=" +
                    transactionStart + "; expiry=" + expiry);
            }

            if (issued >= expiry)
            {
                throw new InvalidOperationException(
                    "The scope issue time is not before the constructed expiry: issued=" +
                    issued + "; expiry=" + expiry);
            }

            await using (var expire = new NpgsqlCommand(
                @"UPDATE table_store.request_scope
                     SET expires_at_utc_ms = @expiry
                   WHERE scope_id = @scope",
                blocker,
                hold))
            {
                expire.Parameters.AddWithValue("expiry", expiry);
                expire.Parameters.AddWithValue("scope", fixture.Scope.ScopeId.Value);
                int updated = await expire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (updated != 1)
                {
                    throw new InvalidOperationException("The scope expiry update did not affect exactly one row.");
                }
            }

            long releaseDbNow = await DbNowMsAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (releaseDbNow < expiry && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                releaseDbNow = await DbNowMsAsync(cancellationToken).ConfigureAwait(false);
            }

            if (releaseDbNow < expiry)
            {
                throw new InvalidOperationException(
                    "The database clock did not cross the constructed expiry within 5s: expiry=" +
                    expiry + "; releaseDbNow=" + releaseDbNow);
            }

            if (transactionStart >= expiry || expiry > releaseDbNow)
            {
                throw new InvalidOperationException(
                    "The expiry is not strictly between the blocked transaction start and the release clock: " +
                    "txnStart=" + transactionStart + "; expiry=" + expiry + "; releaseDbNow=" + releaseDbNow);
            }

            PostgresTableTestEnvironment.AppendEvidence(
                "scope-expiry-boundary-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".txt",
                "pid=" + observation.Pid + Environment.NewLine +
                "issuedUtcMs=" + issued + Environment.NewLine +
                "transactionStartUtcMs=" + transactionStart + Environment.NewLine +
                "expiryUtcMs=" + expiry + Environment.NewLine +
                "observedDbNowUtcMs=" + observation.DbNowUtcMs + Environment.NewLine +
                "releaseDbNowUtcMs=" + releaseDbNow + Environment.NewLine);

            if (commit.IsCompleted)
            {
                throw new InvalidOperationException("The commit completed while the expiry was being written.");
            }

            await hold.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await commit.ConfigureAwait(false);
        }

        private sealed class ScopeBoundaryObservation
        {
            internal ScopeBoundaryObservation(int pid, long transactionStartUtcMs, long dbNowUtcMs)
            {
                Pid = pid;
                TransactionStartUtcMs = transactionStartUtcMs;
                DbNowUtcMs = dbNowUtcMs;
            }

            internal int Pid { get; }

            internal long TransactionStartUtcMs { get; }

            internal long DbNowUtcMs { get; }
        }

        private static async Task<ScopeBoundaryObservation> ObserveBlockedCommitAsync(
            CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(PostgresTableTestEnvironment.AdminConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                @"SELECT pid,
                         (EXTRACT(EPOCH FROM xact_start) * 1000)::bigint,
                         (EXTRACT(EPOCH FROM clock_timestamp()) * 1000)::bigint
                    FROM pg_stat_activity
                   WHERE application_name = 'mahjong-table-p52'
                     AND wait_event_type = 'Lock'
                     AND query LIKE '%request_scope%'
                     AND xact_start IS NOT NULL",
                connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = new List<ScopeBoundaryObservation>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ScopeBoundaryObservation(
                    reader.GetInt32(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2)));
            }

            if (rows.Count != 1)
            {
                throw new InvalidOperationException(
                    "Expected exactly one blocked P52 scope transaction, observed " + rows.Count + ".");
            }

            return rows[0];
        }

        private static async Task<long> DbNowMsAsync(CancellationToken cancellationToken)
        {
            object? value = await PostgresTableTestEnvironment.ScalarAsync(
                "SELECT (EXTRACT(EPOCH FROM clock_timestamp()) * 1000)::bigint",
                cancellationToken).ConfigureAwait(false);
            if (value == null || value is DBNull)
            {
                throw new InvalidOperationException("The database clock returned no value.");
            }

            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static async Task WaitForRowLockWaitAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                long waiting = await PostgresTableTestEnvironment.CountAsync(
                    @"SELECT count(*) FROM pg_stat_activity
                       WHERE application_name = 'mahjong-table-p52'
                         AND wait_event_type = 'Lock'
                         AND query LIKE '%request_scope%'",
                    cancellationToken).ConfigureAwait(false);
                if (waiting == 1)
                {
                    return;
                }

                if (waiting > 1)
                {
                    throw new InvalidOperationException(
                        "Expected one blocked P52 scope transaction, observed " + waiting + ".");
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("The committing transaction never blocked on the scope row lock.");
        }
    }

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
            string owner,
            ulong scopePreparationCompletedAtUtcMs,
            TimeSpan scopePreparationElapsed,
            TimeSpan afterScopeIssueElapsed)
        {
            Store = store;
            Clock = clock;
            Round = round;
            InitialCheckpoint = initialCheckpoint;
            Scope = scope;
            Lease = lease;
            OwnerInstance = owner;
            ScopePreparationCompletedAtUtcMs = scopePreparationCompletedAtUtcMs;
            ScopePreparationElapsed = scopePreparationElapsed;
            AfterScopeIssueElapsed = afterScopeIssueElapsed;
        }

        internal PostgresTableStore Store { get; }

        internal ManualTableClock Clock { get; }

        internal RoundStateMachine Round { get; }

        internal TableCheckpoint InitialCheckpoint { get; }

        internal TableScopeRecord Scope { get; }

        internal TableLeaseSnapshot Lease { get; }

        internal string OwnerInstance { get; }

        /// <summary>The database/wall clock instant immediately after room preparation.</summary>
        internal ulong ScopePreparationCompletedAtUtcMs { get; }

        /// <summary>The measured room-preparation delay, independent of later scheduling.</summary>
        internal TimeSpan ScopePreparationElapsed { get; }

        /// <summary>The measured scheduling delay after scope issue.</summary>
        internal TimeSpan AfterScopeIssueElapsed { get; }

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

        /// <summary>
        /// Waits until a monotonic stopwatch has observed at least <paramref name="minimum"/>.
        /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> is a timer notification, not
        /// an exact-duration guarantee: it can complete a fraction of a millisecond before the
        /// requested span. Continuing to await the remaining monotonic time keeps the fixture's
        /// "actually delayed" assertions exact without weakening them to "about 2 seconds".
        /// Every slice is cancellable and never longer than the remaining minimum.
        /// </summary>
        internal static async Task DelayAtLeastAsync(
            TimeSpan minimum,
            CancellationToken cancellationToken,
            Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        {
            if (minimum <= TimeSpan.Zero)
            {
                return;
            }

            Func<TimeSpan, CancellationToken, Task> wait = delayAsync ?? Task.Delay;
            var watch = Stopwatch.StartNew();
            while (true)
            {
                TimeSpan remaining = minimum - watch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }

                await wait(remaining, cancellationToken).ConfigureAwait(false);
            }
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
            RoundStateMachine? initialRound = null,
            TimeSpan scopePreparationDelay = default,
            TimeSpan afterScopeIssueDelay = default,
            Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        {
            if (scopePreparationDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(scopePreparationDelay), "The preparation delay cannot be negative.");
            }

            if (afterScopeIssueDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(afterScopeIssueDelay), "The post-issue delay cannot be negative.");
            }

            var elapsed = Stopwatch.StartNew();
            string phase = "reset";
            PostgresTableStore? store = null;
            try
            {
                await PostgresTableTestEnvironment.ResetAsync(cancellationToken).ConfigureAwait(false);
                phase = "prepare";
                var clock = new ManualTableClock { Now = 1000L };
                ulong nowUtc = checked((ulong)clock.UtcNowMs);
                RoundStateMachine round = initialRound ?? Dealt("round-p52");
                TableCheckpoint checkpoint = initialCheckpoint ?? InitialCheckpointFor(round, nowUtc, Boot);
                var options = PostgresTableTestEnvironment.NewOptions(commandTimeoutSeconds);
                store = new PostgresTableStore(options);

                phase = "create-room";
                await store.CreateRoomAsync(checkpoint, cancellationToken).ConfigureAwait(false);

                // A caller may deliberately model a slow deployment/room preparation.
                // The scope clock starts only after that preparation completes. The
                // completion timestamp is exposed so tests can assert the causal
                // order without bounding later legitimate scheduling.
                TimeSpan preparationElapsed = TimeSpan.Zero;
                TimeSpan afterIssueElapsed = TimeSpan.Zero;
                if (scopePreparationDelay > TimeSpan.Zero)
                {
                    phase = "scope-preparation";
                    var preparationWatch = Stopwatch.StartNew();
                    await DelayAtLeastAsync(
                        scopePreparationDelay,
                        cancellationToken,
                        delayAsync).ConfigureAwait(false);
                    preparationWatch.Stop();
                    preparationElapsed = preparationWatch.Elapsed;
                }

                ulong preparationCompletedAtUtcMs = checked(
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                phase = "issue-scope";
                // Scope issue/expiry/revocation are database-clock lifecycle checks,
                // not durable recovery timestamps. Keep them on wall time so an
                // injected-clock origin can never place issue time after revocation.
                long issued = scopeIssuedUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long expires = scopeExpiresUtcMs ?? checked(issued + 3_600_000L);
                if (expires <= issued)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scopeExpiresUtcMs),
                        "The scope expiry must follow its issue time.");
                }

                TableScopeRecord scope = await store.IssueScopeAsync(
                    new TableScopeRecord(
                        new RequestScopeId("p52-scope-" + Guid.NewGuid().ToString("N")),
                        Account,
                        role,
                        role == TableScopeRole.Lobby ? null : Room,
                        checked((ulong)issued),
                        checked((ulong)expires)),
                    cancellationToken).ConfigureAwait(false);
                if (afterScopeIssueDelay > TimeSpan.Zero)
                {
                    phase = "post-scope-issue";
                    var afterIssueWatch = Stopwatch.StartNew();
                    await DelayAtLeastAsync(
                        afterScopeIssueDelay,
                        cancellationToken,
                        delayAsync).ConfigureAwait(false);
                    afterIssueWatch.Stop();
                    afterIssueElapsed = afterIssueWatch.Elapsed;
                }

                TableLeaseSnapshot? lease = null;
                if (acquireLease)
                {
                    phase = "acquire-lease";
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

                return new P52Fixture(
                    store,
                    clock,
                    round,
                    checkpoint,
                    scope,
                    lease!,
                    owner,
                    preparationCompletedAtUtcMs,
                    preparationElapsed,
                    afterIssueElapsed);
            }
            catch (Exception exception)
            {
                if (store != null)
                {
                    try
                    {
                        await store.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the original fixture failure.
                    }
                }

                P52Diagnostics.RecordFixtureFailure(phase, elapsed.Elapsed, exception);
                throw;
            }
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
            ulong nowMs = checked((ulong)Clock.UtcNowMs);
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
            ulong nowMs = checked((ulong)Clock.UtcNowMs);
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
