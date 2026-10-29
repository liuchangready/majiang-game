using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Infrastructure;
using Mahjong.TableRuntime;
using Mahjong.TableService;
using Mahjong.TableService.Tests;
using Xunit;

namespace Mahjong.TableService.DurableHost.Tests
{
    /// <summary>
    /// P5.4b-A acceptance against a REAL PostgreSQL table_store database and the REAL
    /// production composition root (<see cref="TableServiceWebHost.Build"/>): the durable
    /// registry wires the same store to the actors' durable context and scope issuance,
    /// every fail-closed refusal refuses (bad connection string, missing key, missing
    /// schema, migration failure, tampered event, event gap, foreign live lease), cold
    /// recovery serves the verified baseline without reapplying anything, and a graceful
    /// stop leaves no admissible owner behind. There is no in-memory substitute anywhere
    /// in this suite and no skip when the database is absent — the run FAILS instead.
    /// </summary>
    [Collection(DurableHostCollection.Name)]
    public sealed class PostgresDurableTableHostTests
    {
        private const string RoomBase = "p54b-dh";

        // ------------------------------------------------------------------
        // Fail-closed build refusals
        // ------------------------------------------------------------------

        [Fact]
        public void Build_UnreachableStore_RefusesToStartWithoutListener()
        {
            var options = PostgresOptions(o =>
            {
                // A loopback port with nothing listening: the store is UNREACHABLE.
                o.PostgresConnectionString =
                    "Host=127.0.0.1;Port=59999;Database=mahjong_p54b_refused;Username=mahjong_pg;Timeout=1;Pooling=false";
            });

            Exception? failure = Record.Exception(() =>
                TableServiceWebHost.Build(options, Keys(new TableTestSigner("build-refused-key"))));
            Assert.NotNull(failure);
        }

        [Fact]
        public async Task Build_MigrationFailure_RefusesToStart()
        {
            await DurableTableHostEnvironment.EnsureSchemaAsync();

            // Provision the exact separation the production deployment has (an application
            // runtime role with DML but NO schema DDL): a migration attempt through it must
            // refuse the build, never degrade to an un-migrated or in-memory host.
            const string probeRole = "p54b_dh_migration_probe";
            const string probePassword = "p54b-dh-probe-pw";
            await DurableTableHostEnvironment.ExecuteAsync($@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{probeRole}') THEN
        CREATE ROLE {probeRole} LOGIN PASSWORD '{probePassword}';
    ELSE
        ALTER ROLE {probeRole} LOGIN PASSWORD '{probePassword}';
    END IF;
END $$;
GRANT USAGE ON SCHEMA table_store TO {probeRole};
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA table_store TO {probeRole};");

            string admin = DurableTableHostEnvironment.AdminConnectionString;
            // Splice the restricted role in place of whatever admin username the
            // environment supplies (postgres, mahjong_pg, ...); never assume one.
            Match credential = Regex.Match(admin, @"Username=(?<name>[^;]+)", RegexOptions.IgnoreCase);
            Assert.True(credential.Success, "the admin connection string must expose its Username token");
            string restricted = admin.Substring(0, credential.Index) +
                                "Username=" + probeRole + ";Password=" + probePassword +
                                admin.Substring(credential.Index + credential.Length);

            var options = PostgresOptions(o =>
            {
                o.PostgresConnectionString = restricted;
                o.PostgresApplyMigrations = true;
            });

            Exception? failure = Record.Exception(() =>
                TableServiceWebHost.Build(options, Keys(new TableTestSigner("build-migration-key"))));
            Assert.NotNull(failure);
        }

        [Fact]
        public void Build_MissingPayloadKey_RefusesToStart()
        {
            string? saved = Environment.GetEnvironmentVariable("MAHJONG_TABLE_PG_PAYLOAD_KEY");
            try
            {
                Environment.SetEnvironmentVariable("MAHJONG_TABLE_PG_PAYLOAD_KEY", null);
                var options = PostgresOptions(o => o.PostgresPayloadKeyBase64 = string.Empty);
                Assert.Throws<PostgresTableConfigurationException>(() =>
                    TableServiceWebHost.Build(options, Keys(new TableTestSigner("build-key-key"))));
            }
            finally
            {
                Environment.SetEnvironmentVariable("MAHJONG_TABLE_PG_PAYLOAD_KEY", saved);
            }
        }

        [Fact]
        public void Build_SchemaNotReady_RefusesToStart()
        {
            var options = PostgresOptions(o =>
            {
                // A real but NEVER-migrated database: readiness is the gate.
                o.PostgresConnectionString = DurableTableHostEnvironment.EmptyDatabaseConnectionString;
            });

            TableServiceCapacityException failure = Assert.Throws<TableServiceCapacityException>(() =>
                TableServiceWebHost.Build(options, Keys(new TableTestSigner("build-notready-key"))));
            Assert.Equal("PERSISTENT_STORE_NOT_READY", failure.Code);
        }

        // ------------------------------------------------------------------
        // Bootstrap + durable persistence
        // ------------------------------------------------------------------

        [Fact]
        public async Task Bootstrap_AdmissionAndCommand_PersistEveryDurableRow()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("bootstrap-key");
            string room = RoomBase + "-boot";
            TableServiceHostRuntime runtime = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            Assert.IsAssignableFrom<PostgresTableHostRegistry>(runtime.HostRegistry);

            await using TableTestClient client = await ConnectPlayerAsync(runtime, signer, room, "acct-boot-0", 0)
                ;
            AppliedAction applied = await ApplyLegalActionAsync(client);
            Assert.Equal(ActionOutcome.Applied, applied.Outcome);

            long states = await DurableTableHostEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_state WHERE room_id = @room", ("room", room));
            long checkpoints = await DurableTableHostEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_checkpoint WHERE room_id = @room", ("room", room));
            long events = await DurableTableHostEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event WHERE room_id = @room", ("room", room));
            long receipts = await DurableTableHostEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt WHERE room_id = @room AND status = 'APPLIED'",
                ("room", room));
            long scopes = await DurableTableHostEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_scope WHERE room_id = @room", ("room", room));
            long leases = await DurableTableHostEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_lease WHERE room_id = @room", ("room", room));
            long leaseOwner = await DurableTableHostEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_lease WHERE room_id = @room AND owner_instance = @owner",
                ("room", room),
                ("owner", Assert.IsAssignableFrom<PostgresTableHostRegistry>(runtime.HostRegistry).OwnerInstance))
                ;

            Assert.Equal(1, states);
            Assert.True(checkpoints >= 1, "bootstrap must write its checkpoint-0 row");
            Assert.True(events >= 1, "an applied command must append event rows");
            Assert.Equal(1, receipts);
            Assert.True(scopes >= 2, "the service scope AND the player scope must be durable rows");
            Assert.Equal(1, leases);
            Assert.Equal(1, leaseOwner);

            DurableTableHostEnvironment.AppendEvidence(
                "bootstrap-rows.json",
                "{\"room\":\"" + room + "\",\"events\":" + events + ",\"receipts\":" + receipts +
                ",\"scopes\":" + scopes + ",\"checkpoints\":" + checkpoints + "}");

            await runtime.StopAsync();
        }

        [Fact]
        public async Task SameKeyReplay_NoDuplicateDurableSideEffect()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("replay-key");
            string room = RoomBase + "-replay";
            TableServiceHostRuntime runtime = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());

            await using TableTestClient client = await ConnectPlayerAsync(runtime, signer, room, "acct-replay-0", 0)
                ;
            AppliedAction first = await ApplyLegalActionAsync(client);
            long eventsAfterFirst = await CountRoomEventsAsync(room);

            await client.SendAsync(TableWire.Command(
                client.Welcome!,
                first.RequestId,
                Contracts.CommandKind.SubmitAction,
                first.Payload,
                expectedStateVersion: first.BaselineVersion));
            Envelope replay = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Receipt &&
                            envelope.RequestId == first.RequestId,
                TimeSpan.FromSeconds(30));

            Assert.True(replay.Receipt.Replayed, "an exact same-key retry must be a replayed receipt");
            Assert.Equal(first.CommandVersion, replay.Receipt.CommandVersion);
            long eventsAfterReplay = await CountRoomEventsAsync(room);
            Assert.Equal(eventsAfterFirst, eventsAfterReplay);
            long receiptRows = await DurableTableHostEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.request_receipt WHERE room_id = @room",
                ("room", room));
            Assert.Equal(1, receiptRows);

            await runtime.StopAsync();
        }

        // ------------------------------------------------------------------
        // Cold recovery
        // ------------------------------------------------------------------

        [Fact]
        public async Task ColdRecovery_ServesVerifiedBaselineWithoutReapplying()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("recovery-key");
            string room = RoomBase + "-cold";

            string requestId;
            string scopeId;
            ulong commandVersion;
            byte[] payload;
            {
                TableServiceHostRuntime first = await StartHostAsync(
                    PostgresOptions(), signer, ClaimFreeRoundSource());
                await using TableTestClient client = await ConnectPlayerAsync(first, signer, room, "acct-cold-0", 0)
                    ;
                AppliedAction applied = await ApplyLegalActionAsync(client);
                requestId = applied.RequestId;
                scopeId = applied.ScopeId;
                commandVersion = applied.CommandVersion;
                payload = applied.Payload;
                // Graceful stop disposes the owned actors and the store: the process's
                // committed rows stay, exactly like a clean process exit.
                await first.StopAsync();
            }

            // The abandoned lease must age out before a successor can own the room.
            await Task.Delay(TimeSpan.FromSeconds(6));

            TableServiceHostRuntime second = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            await using TableTestClient recovered = await ConnectPlayerAsync(
                second, signer, room, "acct-cold-0", 0);

            // WELCOME only exists after RecoverFromStoreAsync installed the verified baseline.
            ulong baseline = recovered.Welcome!.Baseline.StateVersion;
            Assert.True(
                baseline >= commandVersion,
                "the recovered baseline (version " + baseline + ") lost the applied commit (version " +
                commandVersion + ")");

            await recovered.SendAsync(TableWire.ReceiptQuery(recovered.Welcome!, scopeId, requestId))
                ;
            Envelope lookup = await recovered.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(30));
            Assert.Equal(Contracts.Wire.LookupStatus.Found, lookup.ReceiptLookup.Status);
            Assert.Equal(commandVersion, lookup.ReceiptLookup.Receipt.CommandVersion);

            // The SAME OLD command key through a REAL command envelope. The accepted
            // D-1-01(b) boundary derives a discard's response eligibility from the room's
            // own decision state AT the command's expected version; a fresh process holds
            // no decision record for the pre-crash version, so the stale retry fails
            // CLOSED at that trusted boundary — an ERROR reply, never a second execution.
            // Together with the unchanged event journal below this is the
            // no-duplicate-side-effect guarantee across a cold boot; the historical
            // receipt itself stays reachable through the durable query above.
            await recovered.SendAsync(TableWire.Command(
                recovered.Welcome!,
                requestId,
                Contracts.CommandKind.SubmitAction,
                payload,
                expectedStateVersion: baseline));
            Envelope refused = await recovered.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error,
                TimeSpan.FromSeconds(30));
            Assert.Equal(
                TableCommandMapper.ResponseEligibilityDetail,
                refused.Error.DetailCode);
            Assert.True(refused.Error.Retryable, "the stale-retry refusal is retryable under the new scope");

            // The refusal fired at the trusted boundary BEFORE any Domain evaluation: the
            // durable receipt ledger still binds the OLD key to its ORIGINAL commit — one
            // APPLIED row at the phase-1 command version, never rewritten, never duplicated.
            // The room's own auto-draw advances under the service account, which this count
            // deliberately excludes.
            long ledgerRows = await DurableTableHostEnvironment.CountAsync(@"
SELECT count(*) FROM table_store.request_receipt
 WHERE account_id = @account
   AND scope_id = @scope
   AND request_id = @request
   AND status = 'APPLIED'
   AND command_version = @version",
                ("account", "acct-cold-0"),
                ("scope", scopeId),
                ("request", requestId),
                ("version", (long)commandVersion));
            Assert.Equal(1, ledgerRows);

            await second.StopAsync();
        }

        // ------------------------------------------------------------------
        // Dual-host fencing
        // ------------------------------------------------------------------

        [Fact]
        public async Task DualHost_SecondHostWithForeignLiveLease_FailsClosed()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("dual-key");
            string room = RoomBase + "-dual";

            TableServiceHostRuntime first = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            await using TableTestClient owner = await ConnectPlayerAsync(first, signer, room, "acct-dual-0", 0)
                ;
            await ApplyLegalActionAsync(owner);

            // A SECOND host over the SAME database: the room exists durably, its lease is
            // LIVE and foreign — recovery must refuse and the admission must never welcome.
            TableServiceHostRuntime second = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            await using TableTestClient probe = await TableTestClient
                .ConnectAsync(WsUrl(second, room));
            await probe.SendAsync(TableWire.Hello(signer.Sign(
                new TableTestTicket("acct-dual-0", room, TableAdmissionRole.Player, 0))));
            TableWireMessage message = await probe.ReceiveAsync(TimeSpan.FromSeconds(15));
            Assert.True(
                message.Envelope == null || message.Envelope.BodyCase != Envelope.BodyOneofCase.Welcome,
                "a foreign-lease host must never welcome an admission for a room it refuses to own");

            // The FIRST host is still the serving owner: a second seat admits there.
            await using TableTestClient seat1 = await ConnectPlayerAsync(first, signer, room, "acct-dual-1", 1)
                ;
            Assert.NotNull(seat1.Welcome);

            await second.StopAsync();
            await first.StopAsync();
        }

        // ------------------------------------------------------------------
        // Corrupted-journal fail-closed recovery
        // ------------------------------------------------------------------

        [Fact]
        public async Task TamperedEventHash_RecoveryRefused_AndReadinessReportsKnownNotServing()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("tamper-hash-key");
            string room = RoomBase + "-hash";

            {
                TableServiceHostRuntime first = await StartHostAsync(
                    PostgresOptions(), signer, ClaimFreeRoundSource());
                await using TableTestClient client = await ConnectPlayerAsync(first, signer, room, "acct-hash-0", 0)
                    ;
                await ApplyLegalActionAsync(client);
                await first.StopAsync();
            }

            await Task.Delay(TimeSpan.FromSeconds(6));
            // The recovery reads table_event rows only ABOVE the checkpoint's LastEventSeq;
            // everything at or below it lives inside the checkpoint's hash-verified
            // committed-history payload. The integrity probe therefore strikes the payload
            // the recovery actually verifies: one flipped byte in history_blob must flip
            // the verdict to RECOVERY_STATE_HASH_MISMATCH and leave the room
            // known-but-not-serving.
            int tampered = await DurableTableHostEnvironment.ExecuteAsync(@"
UPDATE table_store.table_checkpoint
   SET history_blob = set_byte(history_blob, 0, (get_byte(history_blob, 0) + 1) % 256)
 WHERE room_id = @room
   AND state_version = (SELECT max(state_version) FROM table_store.table_checkpoint WHERE room_id = @room)
   AND history_blob IS NOT NULL",
                new[] { ("room", (object?)room) });
            Assert.Equal(1, tampered);

            TableServiceHostRuntime second = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            await using TableTestClient probe = await TableTestClient
                .ConnectAsync(WsUrl(second, room));
            await probe.SendAsync(TableWire.Hello(signer.Sign(
                new TableTestTicket("acct-hash-0", room, TableAdmissionRole.Player, 0))));
            TableWireMessage message = await probe.ReceiveAsync(TimeSpan.FromSeconds(15));
            Assert.True(
                message.Envelope == null || message.Envelope.BodyCase != Envelope.BodyOneofCase.Welcome,
                "a hash-corrupt journal must never be served");

            var registry = Assert.IsAssignableFrom<PostgresTableHostRegistry>(second.HostRegistry);
            Assert.True(
                registry.FailedRooms.ContainsKey(room),
                "the refused room must be recorded as known-and-not-serving");

            using var http = new HttpClient();
            using HttpResponseMessage ready = await http
                .GetAsync("http://127.0.0.1:" + second.GetBoundPort() + TableServiceWebHost.ReadyPath)
                ;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

            await second.StopAsync();
        }

        [Fact]
        public async Task EventGap_RecoveryRefused()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("tamper-gap-key");
            string room = RoomBase + "-gap";

            {
                TableServiceHostRuntime first = await StartHostAsync(
                    PostgresOptions(), signer, ClaimFreeRoundSource());
                await using TableTestClient client = await ConnectPlayerAsync(first, signer, room, "acct-gap-0", 0)
                    ;
                await ApplyLegalActionAsync(client);
                await first.StopAsync();
            }

            await Task.Delay(TimeSpan.FromSeconds(6));
            // Roll the room back to its bootstrap checkpoint: recovery then re-reads EVERY
            // committed table_event row above seq 0 and enforces sequence continuity on the
            // real journal. With the caught-up checkpoint in place the same deletion would
            // sit below the recovery's read horizon — the accepted contract carries that
            // history inside the hash-verified checkpoint payload instead.
            int rolledBack = await DurableTableHostEnvironment.ExecuteAsync(@"
DELETE FROM table_store.table_checkpoint
 WHERE room_id = @room
   AND state_version = (SELECT max(state_version) FROM table_store.table_checkpoint WHERE room_id = @room)",
                new[] { ("room", (object?)room) });
            Assert.Equal(1, rolledBack);
            int deleted = await DurableTableHostEnvironment.ExecuteAsync(@"
DELETE FROM table_store.table_event
 WHERE room_id = @room
   AND seq = GREATEST((SELECT max(seq) FROM table_store.table_event WHERE room_id = @room) - 1, 1)",
                new[] { ("room", (object?)room) });
            Assert.Equal(1, deleted);

            TableServiceHostRuntime second = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            await using TableTestClient probe = await TableTestClient
                .ConnectAsync(WsUrl(second, room));
            await probe.SendAsync(TableWire.Hello(signer.Sign(
                new TableTestTicket("acct-gap-0", room, TableAdmissionRole.Player, 0))));
            TableWireMessage message = await probe.ReceiveAsync(TimeSpan.FromSeconds(15));
            Assert.True(
                message.Envelope == null || message.Envelope.BodyCase != Envelope.BodyOneofCase.Welcome,
                "a journal with a missing committed event must never be served");

            var registry = Assert.IsAssignableFrom<PostgresTableHostRegistry>(second.HostRegistry);
            Assert.True(registry.FailedRooms.ContainsKey(room));

            await second.StopAsync();
        }

        // ------------------------------------------------------------------
        // Scope lifecycle
        // ------------------------------------------------------------------

        [Fact]
        public async Task RevokedScope_ReceiptQueryable_AndUnknownRequestInert()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("scope-key");
            string room = RoomBase + "-scope";

            string requestId;
            string scopeId;
            ulong commandVersion;
            {
                TableServiceHostRuntime runtime = await StartHostAsync(
                    PostgresOptions(), signer, ClaimFreeRoundSource());
                await using TableTestClient client = await ConnectPlayerAsync(runtime, signer, room, "acct-scope-0", 0)
                    ;
                AppliedAction applied = await ApplyLegalActionAsync(client);
                requestId = applied.RequestId;
                scopeId = applied.ScopeId;
                commandVersion = applied.CommandVersion;

                // Revoke the OLD scope through the SAME durable port family: recorded
                // receipts outlive their scope, unknown requests gain nothing.
                var storeOptions = new PostgresTableOptions(
                    DurableTableHostEnvironment.RuntimeConnectionString,
                    DurableTableHostEnvironment.PayloadKey);
                await using var store = new PostgresTableStore(storeOptions);
                Assert.True(await store.RevokeScopeAsync(
                    new RequestScopeId(scopeId),
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

                await runtime.StopAsync();
            }

            // The lease contract has no release port: the stopped host's lease stays on
            // record until its TTL passes, and a successor must never fence past a LIVE
            // foreign owner. Let it age out before the second runtime owns the room.
            await Task.Delay(TimeSpan.FromSeconds(6));

            TableServiceHostRuntime runtime2 = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            await using TableTestClient fresh = await ConnectPlayerAsync(
                runtime2, signer, room, "acct-scope-0", 0);

            // NOTE: the fresh admission may cold-recover the room if the previous lease
            // expired — either way the room serves from the durable rows.
            await fresh.SendAsync(TableWire.ReceiptQuery(fresh.Welcome!, scopeId, requestId));
            Envelope lookup = await fresh.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(30));
            Assert.Equal(Contracts.Wire.LookupStatus.Found, lookup.ReceiptLookup.Status);
            Assert.Equal(commandVersion, lookup.ReceiptLookup.Receipt.CommandVersion);

            string unknownRequest = TableWire.RandomId("req");
            await fresh.SendAsync(TableWire.ReceiptQuery(
                fresh.Welcome!, fresh.Welcome!.Scope.ScopeId, unknownRequest));
            Envelope unknown = await fresh.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(30));
            Assert.NotEqual(Contracts.Wire.LookupStatus.Found, unknown.ReceiptLookup.Status);

            // Both queries were pure reads: the test account's durable receipt ledger is
            // unchanged — the historical receipt was neither rewritten nor duplicated by a
            // second commit under the same key. The room's own auto-draw advances under the
            // service account, which this count deliberately excludes.
            long ledgerRows = await DurableTableHostEnvironment.CountAsync(@"
SELECT count(*) FROM table_store.request_receipt
 WHERE account_id = @account
   AND scope_id = @scope
   AND request_id = @request
   AND status = 'APPLIED'
   AND command_version = @version",
                ("account", "acct-scope-0"),
                ("scope", scopeId),
                ("request", requestId),
                ("version", (long)commandVersion));
            Assert.Equal(1, ledgerRows);

            await runtime2.StopAsync();
        }

        // ------------------------------------------------------------------
        // Graceful stop
        // ------------------------------------------------------------------

        [Fact]
        public async Task SameBootRecovery_PendingDisconnectGrace_IsTrustedInProcess()
        {
            // A clean client exit drives the host's disconnect-control commit: the last
            // durable checkpoint then carries the seat Disconnected with a STILL-PENDING
            // monotonic grace deadline. The accepted recovery contract trusts such a
            // deadline only inside the monotonic clock domain it was written in — a
            // successor registry IN THE SAME PROCESS (same boot id) must recover the
            // room and welcome the seat back, while a fresh process refuses the same
            // rows (cross-boot). The kill-9 crash shape the runner exercises never
            // produces such a deadline in the first place.
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("probe-key");
            string room = RoomBase + "-probe";
            ulong commandVersion;
            TableServiceHostRuntime first = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            {
                await using TableTestClient client = await ConnectPlayerAsync(first, signer, room, "acct-probe-0", 0);
                AppliedAction applied = await ApplyLegalActionAsync(client);
                commandVersion = applied.CommandVersion;
                // The clean client disposal (at the block's close) commits the disconnect
                // control; the graceful stop below persists it with the grace deadline
                // still pending. The lease contract has no release port — a successor
                // must never fence past a LIVE foreign owner, so the first host has to
                // actually stop renewing before the room can change hands.
            }
            await first.StopAsync();

            // The abandoned lease must age out before a successor can own the room.
            await Task.Delay(TimeSpan.FromSeconds(6));

            TableServiceHostRuntime second = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            await using TableTestClient recovered = await ConnectPlayerAsync(
                second, signer, room, "acct-probe-0", 0);
            Assert.True(
                recovered.Welcome!.Baseline.StateVersion >= commandVersion,
                "the same-boot recovered baseline (version " +
                recovered.Welcome.Baseline.StateVersion + ") lost the applied commit (version " +
                commandVersion + ")");
            await second.StopAsync();
        }

        [Fact]
        public async Task GracefulStop_NoNewAdmissionIsWelcomed()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("stop-key");
            string room = RoomBase + "-stop";
            TableServiceHostRuntime runtime = await StartHostAsync(
                PostgresOptions(), signer, ClaimFreeRoundSource());
            await using (TableTestClient client = await ConnectPlayerAsync(runtime, signer, room, "acct-stop-0", 0)
                )
            {
                await ApplyLegalActionAsync(client);
            }

            string probeUrl = WsUrl(runtime, room);
            await runtime.StopAsync();

            // Every owned actor is disposed and the graceful stop closed the listener:
            // nobody can even reach the host, and IF a socket were still reachable it must
            // never answer a HELLO with a WELCOME.
            try
            {
                await using TableTestClient probe = await TableTestClient
                    .ConnectAsync(probeUrl);
                await probe.SendAsync(TableWire.Hello(signer.Sign(
                    new TableTestTicket("acct-stop-0", room, TableAdmissionRole.Player, 0))));
                TableWireMessage message = await probe.ReceiveAsync(TimeSpan.FromSeconds(15));
                Assert.True(
                    message.Envelope == null || message.Envelope.BodyCase != Envelope.BodyOneofCase.Welcome,
                    "a stopped host must not welcome a new admission");
            }
            catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException ||
                                              exception is System.Net.Http.HttpRequestException)
            {
                // The listener is closed: a stopped host serves nobody, which is exactly the
                // graceful-stop contract this probe checks.
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static TableServiceOptions PostgresOptions(Action<TableServiceOptions>? configure = null)
        {
            var options = new TableServiceOptions
            {
                StoreMode = TableHostStoreMode.Postgres,
                PostgresConnectionString = DurableTableHostEnvironment.RuntimeConnectionString,
                PostgresPayloadKeyBase64 = DurableTableHostEnvironment.PayloadKey,
                PostgresApplyMigrations = false,
                HandshakeTimeoutMs = 2000,
                HeartbeatIntervalMs = 500,
                IdleDeadMs = 60_000,
                RoomTickIntervalMs = 50,
                LeaseTtlSeconds = 5
            };
            configure?.Invoke(options);
            return options;
        }

        private static StaticTableTicketKeyProvider Keys(TableTestSigner signer)
        {
            return new StaticTableTicketKeyProvider(
                new[] { (signer.PublicKeyPem, (string?)signer.Kid) },
                DateTimeOffset.UtcNow);
        }

        private static async Task<TableServiceHostRuntime> StartHostAsync(
            TableServiceOptions options,
            TableTestSigner signer,
            ITableRoundSource roundSource)
        {
            TableServiceHostRuntime runtime =
                TableServiceWebHost.Build(options, Keys(signer), IPAddress.Loopback, 0, roundSource);
            await runtime.StartAsync();
            return runtime;
        }

        private static string WsUrl(TableServiceHostRuntime runtime, string room)
        {
            return "ws://127.0.0.1:" + runtime.GetBoundPort() + TableServiceWebHost.TablePath +
                   "?room=" + Uri.EscapeDataString(room);
        }

        private static async Task<TableTestClient> ConnectPlayerAsync(
            TableServiceHostRuntime runtime,
            TableTestSigner signer,
            string room,
            string account,
            int seat)
        {
            TableTestClient client = await TableTestClient
                .ConnectAsync(WsUrl(runtime, room))
                ;
            await client.AdmitAsync(signer.Sign(new TableTestTicket(account, room, TableAdmissionRole.Player, seat)))
                ;
            return client;
        }

        private static ITableRoundSource ClaimFreeRoundSource()
        {
            // The D-5-01 rotation fixture: seat 0 opens with thirteen honour tiles (first
            // discard = East) and the filler leaves seats 1-3 all-number, so the first
            // discard cannot open a response window — the applied action is deterministic.
            return new FixedRoundSource(roomId =>
            {
                var fixture = new N1aTable { Dealer = 0 };
                fixture.Hand(0, new[]
                {
                    new TileId(108), new TileId(109), new TileId(110), new TileId(111),
                    new TileId(112), new TileId(113), new TileId(114), new TileId(115),
                    new TileId(116), new TileId(117), new TileId(118), new TileId(119),
                    new TileId(120)
                });
                fixture.Drawn(0, new TileId(127));
                return fixture.Build(roomId + "-r1");
            });
        }

        private static async Task<long> CountRoomEventsAsync(string room)
        {
            return await DurableTableHostEnvironment.CountAsync(
                "SELECT count(*) FROM table_store.table_event WHERE room_id = @room",
                ("room", room));
        }

        private enum ActionOutcome
        {
            Applied,
            Replayed
        }

        private sealed record AppliedAction(
            string RequestId,
            string ScopeId,
            ulong CommandVersion,
            ulong BaselineVersion,
            byte[] Payload,
            ActionOutcome Outcome);

        /// <summary>
        /// Waits for the seat's real open decision and applies its FIRST legal action —
        /// the exact accepted legal-action recipe, mapped faithfully from the published
        /// decision (action type included) so any legitimately opened decision works.
        /// </summary>
        private static async Task<AppliedAction> ApplyLegalActionAsync(TableTestClient client)
        {
            Envelope envelope = await client.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot?.PrivateView?.Decision != null,
                TimeSpan.FromSeconds(30));
            Contracts.Wire.PlayerSnapshot snapshot = envelope.PlayerUpdate!.Snapshot;
            Contracts.Wire.DecisionView decision = snapshot.PrivateView!.Decision!;
            Assert.NotEmpty(decision.Actions);
            Contracts.Wire.LegalActionView action = decision.Actions[0];

            string requestId = TableWire.RandomId("req");
            byte[] payload = TableWire.SubmitActionPayload(
                snapshot.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                action.ActionType,
                action.TileIds.ToArray());
            await client.SendAsync(TableWire.Command(
                client.Welcome!,
                requestId,
                Contracts.CommandKind.SubmitAction,
                payload,
                expectedStateVersion: snapshot.StateVersion));
            Envelope receipt = await client.ReadUntilAsync(
                candidate => candidate.MessageType == MessageType.Receipt &&
                             candidate.RequestId == requestId,
                TimeSpan.FromSeconds(30));

            var outcome = receipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Applied && !receipt.Receipt.Replayed
                ? ActionOutcome.Applied
                : ActionOutcome.Replayed;
            return new AppliedAction(
                requestId,
                client.Welcome!.Scope.ScopeId,
                receipt.Receipt.CommandVersion,
                snapshot.StateVersion,
                payload,
                outcome);
        }
    }
}
