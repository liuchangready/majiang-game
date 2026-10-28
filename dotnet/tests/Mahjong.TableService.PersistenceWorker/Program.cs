using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Infrastructure;
using Mahjong.TableService.Tests;
using Npgsql;

namespace Mahjong.TableService.PersistenceWorker
{
    /// <summary>
    /// Independent P5.4b-A verification worker (exit-code contract, JSON evidence):
    ///   keygen              write the run's ticket signing keypair (private.pem/public.pem/kid.txt)
    ///   migrate             apply the idempotent table_store migration twice, assert ready
    ///   reset               truncate every table_store table (admin DDL owner connection)
    ///   phase1              admit through a REAL host, apply one legal action, same-key replay, record state
    ///   phase2              re-admit after cold recovery, explicit receipt query + same-key replay
    ///   admitprobe          attempt one admission and report the exact refusal shape (expect NO welcome)
    ///   crossaccount        a foreign account's receipt query is refused; an unknown request has no side effect
    ///   tamper-event-hash   corrupt one committed event blob byte (fail-closed recovery probe)
    ///   tamper-event-gap    delete one committed event row (fail-closed recovery probe)
    ///   tamper-scope-revoke mark the phase-1 request scope revoked directly in the database
    ///   counts              dump per-room durable row counts as JSON evidence
    ///
    /// Database connections arrive ONLY through the MAHJONG_P54B_TEST_PG (runtime DML role)
    /// and MAHJONG_P54B_TEST_PG_ADMIN (migration/reset) environment variables; the payload key
    /// travels through MAHJONG_P54B_TEST_PG_KEY. Nothing here logs a connection string, key,
    /// ticket or private payload. Fault injection lives only in this test worker — the
    /// production service carries no failpoint.
    /// </summary>
    public static class Program
    {
        public const int ExitOk = 0;

        public const int ExitFailed = 1;

        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: <mode> --key value ...");
                return ExitFailed;
            }

            try
            {
                var options = ParseOptions(args.Skip(1));
                switch (args[0].ToLowerInvariant())
                {
                    case "keygen":
                        return await RunKeygenAsync(options).ConfigureAwait(false);
                    case "migrate":
                        return await RunMigrateAsync(options).ConfigureAwait(false);
                    case "reset":
                        return await RunResetAsync(options).ConfigureAwait(false);
                    case "phase1":
                        return await RunPhase1Async(options).ConfigureAwait(false);
                    case "phase2":
                        return await RunPhase2Async(options).ConfigureAwait(false);
                    case "admitprobe":
                        return await RunAdmitProbeAsync(options).ConfigureAwait(false);
                    case "crossaccount":
                        return await RunCrossAccountAsync(options).ConfigureAwait(false);
                    case "tamper-event-hash":
                        return await RunTamperEventHashAsync(options).ConfigureAwait(false);
                    case "tamper-event-gap":
                        return await RunTamperEventGapAsync(options).ConfigureAwait(false);
                    case "tamper-scope-revoke":
                        return await RunTamperScopeRevokeAsync(options).ConfigureAwait(false);
                    case "counts":
                        return await RunCountsAsync(options).ConfigureAwait(false);
                    default:
                        Console.Error.WriteLine("unknown mode: " + args[0]);
                        return ExitFailed;
                }
            }
            catch (Exception exception)
            {
                // Every failure is a real exit-code failure; the message is a redacted
                // identity only (exception type + message), never a connection string.
                Console.Error.WriteLine("worker failed: " + exception.GetType().Name + ": " + exception.Message);
                return ExitFailed;
            }
        }

        private static Dictionary<string, string> ParseOptions(IEnumerable<string> arguments)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            string? current = null;
            foreach (string argument in arguments)
            {
                if (argument.StartsWith("--", StringComparison.Ordinal))
                {
                    current = argument.Substring(2);
                    options[current] = string.Empty;
                }
                else if (current != null)
                {
                    options[current] = argument;
                    current = null;
                }
            }

            return options;
        }

        private static string Require(Dictionary<string, string> options, string name)
        {
            if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("missing required option --" + name);
            }

            return value;
        }

        private static string? Optional(Dictionary<string, string> options, string name)
        {
            return options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }

        // ------------------------------------------------------------------
        // Shared evidence helpers
        // ------------------------------------------------------------------

        private static async Task WriteJsonAsync(string path, object value)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            Console.WriteLine("wrote " + Path.GetFileName(path));
        }

        private static async Task<JsonElement> ReadJsonAsync(string path)
        {
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static string RequireRuntimeConnection()
        {
            return RequireEnvironment("MAHJONG_P54B_TEST_PG");
        }

        private static string RequireAdminConnection()
        {
            return RequireEnvironment("MAHJONG_P54B_TEST_PG_ADMIN");
        }

        private static string RequirePayloadKey()
        {
            return RequireEnvironment("MAHJONG_P54B_TEST_PG_KEY");
        }

        private static string RequireEnvironment(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("BLOCKED: environment variable " + name + " is required.");
            }

            return value;
        }

        private static async Task<int> ScalarCountAsync(string connectionString, string sql, string? roomId)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            if (roomId != null)
            {
                command.Parameters.AddWithValue("room_id", roomId);
            }

            object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return result == null || result is DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }

        private static async Task<int> ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task<int> CountRoomEventsAsync(string roomId)
        {
            return await ScalarCountAsync(
                RequireRuntimeConnection(),
                "SELECT count(*) FROM table_store.table_event WHERE room_id = @room_id",
                roomId).ConfigureAwait(false);
        }

        /// <summary>Counts the durable APPLIED receipt rows bound to one exact historical
        /// (account, scope, request) key at one exact command version — the auto-draw-immune
        /// side-effect ledger for the crash-replay contract.</summary>
        private static async Task<int> CountLedgerRowAsync(string requestId, string scopeId, long commandVersion)
        {
            await using var connection = new NpgsqlConnection(RequireRuntimeConnection());
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(@"
SELECT count(*) FROM table_store.request_receipt
 WHERE scope_id = @scope_id AND request_id = @request_id
   AND status = 'APPLIED' AND command_version = @command_version", connection);
            command.Parameters.AddWithValue("scope_id", scopeId);
            command.Parameters.AddWithValue("request_id", requestId);
            command.Parameters.AddWithValue("command_version", commandVersion);
            object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return result == null || result is DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        // keygen / migrate / reset
        // ------------------------------------------------------------------

        private static async Task<int> RunKeygenAsync(Dictionary<string, string> options)
        {
            string directory = Require(options, "dir");
            Directory.CreateDirectory(directory);
            var signer = WorkerTicketSigner.Create();
            await File.WriteAllTextAsync(Path.Combine(directory, "private.pem"), signer.PrivatePem).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "public.pem"), signer.PublicPem).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "kid.txt"), signer.Kid).ConfigureAwait(false);
            Console.WriteLine("keygen ok kid=" + signer.Kid);
            return ExitOk;
        }

        private static async Task<int> RunMigrateAsync(Dictionary<string, string> options)
        {
            string admin = Optional(options, "admin") ?? RequireAdminConnection();
            // Two identical runs prove the accepted migration stays idempotent.
            await PostgresTableMigrator.ApplyAsync(admin).ConfigureAwait(false);
            await PostgresTableMigrator.ApplyAsync(admin).ConfigureAwait(false);
            if (!await PostgresTableMigrator.IsReadyAsync(admin).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The table_store schema is not ready after two migrations.");
            }

            Console.WriteLine("migrate ok (schema ready, two idempotent passes)");
            return ExitOk;
        }

        private static async Task<int> RunResetAsync(Dictionary<string, string> options)
        {
            string admin = Optional(options, "admin") ?? RequireAdminConnection();
            const string sql = @"
TRUNCATE table_store.table_recovery_audit,
         table_store.settlement_outbox,
         table_store.table_committed_facts,
         table_store.table_control_journal,
         table_store.table_command_journal,
         table_store.table_timing_journal,
         table_store.table_checkpoint,
         table_store.table_event,
         table_store.request_receipt,
         table_store.request_scope,
         table_store.table_lease,
         table_store.table_state
CASCADE";
            await ExecuteAsync(admin, sql).ConfigureAwait(false);
            Console.WriteLine("reset ok");
            return ExitOk;
        }

        // ------------------------------------------------------------------
        // phase1: real admission + one legal applied action + same-key replay
        // ------------------------------------------------------------------

        private static async Task<int> RunPhase1Async(Dictionary<string, string> options)
        {
            string url = Require(options, "url");
            string keysDirectory = Require(options, "keys");
            string room = Require(options, "room");
            string account = Require(options, "account");
            int seat = int.Parse(Require(options, "seat"), CultureInfo.InvariantCulture);
            string outputPath = Require(options, "out");

            WorkerTicketSigner signer = await WorkerTicketSigner.LoadAsync(keysDirectory).ConfigureAwait(false);
            await using TableTestClient client = await TableTestClient
                .ConnectAsync(url)
                .ConfigureAwait(false);
            Welcome welcome = await client
                .AdmitAsync(signer.SignTicket(account, room, seat))
                .ConfigureAwait(false);
            string scopeId = welcome.Scope.ScopeId;
            if (string.IsNullOrWhiteSpace(scopeId))
            {
                throw new InvalidOperationException("WELCOME carried no request scope id.");
            }

            Contracts.Wire.PlayerSnapshot snapshot = await WaitPlayerDecisionAsync(client).ConfigureAwait(false);
            Contracts.Wire.DecisionView decision = snapshot.PrivateView!.Decision!;
            if (decision.Actions.Count == 0)
            {
                throw new InvalidOperationException("The opened decision has no candidate actions.");
            }

            Contracts.Wire.LegalActionView action = decision.Actions[0];
            ulong baselineStateVersion = snapshot.StateVersion;
            string requestId = TableWire.RandomId("req");
            byte[] payload = TableWire.SubmitActionPayload(
                snapshot.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                action.ActionType,
                action.TileIds.ToArray());

            Envelope receipt = await SendAndAwaitReceiptAsync(client, welcome, requestId, payload, baselineStateVersion)
                .ConfigureAwait(false);
            bool applied = receipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Applied;
            if (!applied || receipt.Receipt.Replayed)
            {
                throw new InvalidOperationException(
                    "phase1 first command was not a fresh APPLIED receipt (status " +
                    receipt.Receipt.Status + " replayed " + receipt.Receipt.Replayed + ").");
            }

            ulong commandVersion = receipt.Receipt.CommandVersion;

            // Same-key retry: answered from the durable receipt ledger, no second application.
            Envelope replay = await SendAndAwaitReceiptAsync(client, welcome, requestId, payload, baselineStateVersion)
                .ConfigureAwait(false);
            if (!replay.Receipt.Replayed || replay.Receipt.CommandVersion != commandVersion)
            {
                throw new InvalidOperationException(
                    "phase1 same-key retry was not an identical replayed receipt (replayed " +
                    replay.Receipt.Replayed + " version " + replay.Receipt.CommandVersion + ").");
            }

            var evidence = new Dictionary<string, object>
            {
                ["room"] = room,
                ["account"] = account,
                ["seat"] = seat,
                ["requestId"] = requestId,
                ["scopeId"] = scopeId,
                ["roundId"] = snapshot.RoundId,
                ["baselineStateVersion"] = (long)baselineStateVersion,
                ["commandVersion"] = (long)commandVersion,
                ["replayed"] = true,
                ["payloadBase64"] = Convert.ToBase64String(payload)
            };

            if (options.ContainsKey("hold-open"))
            {
                // Crash shape (test worker only): the evidence is flushed while the ticketed
                // session STAYS LIVE, and this process then blocks forever. The runner kills
                // the HOST first — with a connected client and no graceful teardown — and
                // only then this worker. A clean client exit would drive the host's
                // disconnect-control commit (seat Disconnected + a persisted monotonic grace
                // deadline), which the accepted cross-boot recovery contract refuses to
                // trust; the kill-9 shape is what a successor process must recover from.
                await WriteJsonAsync(outputPath, evidence).ConfigureAwait(false);
                Console.WriteLine(
                    "phase1 ok appliedVersion=" + commandVersion.ToString(CultureInfo.InvariantCulture) +
                    " holding-session-open");
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return ExitOk;
            }

            await client.CloseCleanAsync().ConfigureAwait(false);

            await WriteJsonAsync(outputPath, evidence).ConfigureAwait(false);
            Console.WriteLine(
                "phase1 ok appliedVersion=" + commandVersion.ToString(CultureInfo.InvariantCulture));
            return ExitOk;
        }

        private static async Task<Envelope> SendAndAwaitReceiptAsync(
            TableTestClient client,
            Welcome welcome,
            string requestId,
            byte[] payload,
            ulong expectedStateVersion)
        {
            await client.SendAsync(TableWire.Command(
                welcome,
                requestId,
                Contracts.CommandKind.SubmitAction,
                payload,
                expectedStateVersion: expectedStateVersion)).ConfigureAwait(false);
            return await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Receipt &&
                            envelope.RequestId == requestId,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }

        private static async Task<Contracts.Wire.PlayerSnapshot> WaitPlayerDecisionAsync(TableTestClient client)
        {
            Envelope envelope = await client.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot?.PrivateView?.Decision != null,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return envelope.PlayerUpdate!.Snapshot;
        }

        // ------------------------------------------------------------------
        // phase2: cold-recovery admission + explicit receipt reconciliation
        // ------------------------------------------------------------------

        private static async Task<int> RunPhase2Async(Dictionary<string, string> options)
        {
            string url = Require(options, "url");
            string keysDirectory = Require(options, "keys");
            string statePath = Require(options, "state");
            string outputPath = Require(options, "out");
            bool expectRevokedScope = options.ContainsKey("expect-revoked-scope");

            JsonElement state = await ReadJsonAsync(statePath).ConfigureAwait(false);
            string room = state.GetProperty("room").GetString()!;
            string account = state.GetProperty("account").GetString()!;
            int seat = state.GetProperty("seat").GetInt32();
            string requestId = state.GetProperty("requestId").GetString()!;
            string scopeId = state.GetProperty("scopeId").GetString()!;
            ulong commandVersion = (ulong)state.GetProperty("commandVersion").GetInt64();
            byte[] payload = Convert.FromBase64String(state.GetProperty("payloadBase64").GetString()!);

            WorkerTicketSigner signer = await WorkerTicketSigner.LoadAsync(keysDirectory).ConfigureAwait(false);
            await using TableTestClient client = await TableTestClient
                .ConnectAsync(url)
                .ConfigureAwait(false);
            Welcome welcome = await client
                .AdmitAsync(signer.SignTicket(account, room, seat))
                .ConfigureAwait(false);

            // WELCOME only exists after the recovered baseline was installed, so this
            // assertion is the cold-recovery acceptance itself.
            ulong recoveredVersion = welcome.Baseline.StateVersion;
            if (recoveredVersion < commandVersion)
            {
                throw new InvalidOperationException(
                    "The recovered baseline (version " + recoveredVersion +
                    ") is OLDER than the applied pre-crash commit (version " + commandVersion + ").");
            }

            string freshScopeId = welcome.Scope.ScopeId;
            if (string.Equals(freshScopeId, scopeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A fresh admission reissued the historical scope id.");
            }

            int eventsBefore = await CountRoomEventsAsync(room).ConfigureAwait(false);

            // 1) Explicit reconciliation of the OLD scope + OLD request: the recorded
            //    receipt must remain queryable, byte-identical, with no new application.
            await client.SendAsync(TableWire.ReceiptQuery(welcome, scopeId, requestId)).ConfigureAwait(false);
            Envelope lookup = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (lookup.ReceiptLookup.Status != Contracts.Wire.LookupStatus.Found)
            {
                throw new InvalidOperationException(
                    "The historical receipt was not found (status " + lookup.ReceiptLookup.Status + ").");
            }

            if (lookup.ReceiptLookup.Receipt.CommandVersion != commandVersion)
            {
                throw new InvalidOperationException(
                    "The historical receipt changed identity (version " +
                    lookup.ReceiptLookup.Receipt.CommandVersion + " expected " + commandVersion + ").");
            }

            // 2) The SAME OLD command key re-sent through a REAL command envelope. The
            //    accepted D-1-01(b) boundary derives a discard's response eligibility from
            //    the room's own decision state AT the command's expected version; a fresh
            //    process holds no decision record for the pre-crash version, so the stale
            //    retry fails CLOSED there — an ERROR reply, never a second application.
            await client.SendAsync(TableWire.Command(
                welcome, requestId, Contracts.CommandKind.SubmitAction, payload,
                expectedStateVersion: welcome.Baseline.StateVersion)).ConfigureAwait(false);
            Envelope refused = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (refused.Error.DetailCode != "RESPONSE_WINDOW_AUTHORITY_UNAVAILABLE" || !refused.Error.Retryable)
            {
                throw new InvalidOperationException(
                    "The post-crash same-key retry was not the accepted fail-closed refusal (detail " +
                    refused.Error.DetailCode + " retryable " + refused.Error.Retryable + ").");
            }

            // 3) The durable ledger: the OLD key still binds exactly its ORIGINAL commit —
            //    one APPLIED row at the phase-1 command version, never rewritten. The
            //    room's own auto-draw advances under the service account, which this
            //    count deliberately excludes.
            int ledgerRows = await CountLedgerRowAsync(requestId, scopeId, (long)commandVersion).ConfigureAwait(false);
            if (ledgerRows != 1)
            {
                throw new InvalidOperationException(
                    "The historical receipt ledger row was rewritten or duplicated (rows " + ledgerRows + ").");
            }

            await client.CloseCleanAsync().ConfigureAwait(false);
            await WriteJsonAsync(outputPath, new Dictionary<string, object>
            {
                ["room"] = room,
                ["welcomedAfterRecovery"] = true,
                ["recoveredBaselineStateVersion"] = (long)recoveredVersion,
                ["freshScopeId"] = freshScopeId,
                ["historicalScopeStillAuthorized"] = true,
                ["receiptFound"] = true,
                ["receiptCommandVersion"] = (long)commandVersion,
                ["sameKeyRetryFailedClosed"] = true,
                ["ledgerRows"] = ledgerRows,
                ["eventCountBefore"] = eventsBefore,
                ["expectRevokedScope"] = expectRevokedScope
            }).ConfigureAwait(false);
            Console.WriteLine(
                "phase2 ok recoveredVersion=" + recoveredVersion.ToString(CultureInfo.InvariantCulture) +
                " ledgerRows=" + ledgerRows.ToString(CultureInfo.InvariantCulture));
            return ExitOk;
        }

        // ------------------------------------------------------------------
        // admitprobe: record the exact admission refusal shape (no welcome)
        // ------------------------------------------------------------------

        private static async Task<int> RunAdmitProbeAsync(Dictionary<string, string> options)
        {
            string url = Require(options, "url");
            string keysDirectory = Require(options, "keys");
            string room = Require(options, "room");
            string account = Require(options, "account");
            int seat = int.Parse(Require(options, "seat"), CultureInfo.InvariantCulture);
            string outputPath = Require(options, "out");

            WorkerTicketSigner signer = await WorkerTicketSigner.LoadAsync(keysDirectory).ConfigureAwait(false);
            string ticket = signer.SignTicket(account, room, seat);
            bool welcomed = false;
            int closeCode = 0;
            string closeReason = string.Empty;
            string errorDetail = string.Empty;
            await using TableTestClient client = await TableTestClient
                .ConnectAsync(url)
                .ConfigureAwait(false);
            await client.SendAsync(TableWire.Hello(ticket)).ConfigureAwait(false);
            TableWireMessage message = await client.ReceiveAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (message.Envelope != null && message.Envelope.BodyCase == Envelope.BodyOneofCase.Welcome)
            {
                welcomed = true;
            }
            else if (message.Envelope != null && message.Envelope.MessageType == MessageType.Error)
            {
                errorDetail = message.Envelope.Error.DetailCode;
            }
            else
            {
                closeCode = message.WsCloseCode ?? 0;
                closeReason = message.WsCloseReason ?? string.Empty;
            }

            await WriteJsonAsync(outputPath, new Dictionary<string, object>
            {
                ["room"] = room,
                ["welcomed"] = welcomed,
                ["closeCode"] = closeCode,
                ["closeReason"] = closeReason,
                ["errorDetail"] = errorDetail
            }).ConfigureAwait(false);
            Console.WriteLine("admitprobe welcomed=" + welcomed);
            return ExitOk;
        }

        // ------------------------------------------------------------------
        // crossaccount: foreign receipt query refused; unknown request inert
        // ------------------------------------------------------------------

        private static async Task<int> RunCrossAccountAsync(Dictionary<string, string> options)
        {
            string url = Require(options, "url");
            string keysDirectory = Require(options, "keys");
            string statePath = Require(options, "state");
            string outputPath = Require(options, "out");
            string accountB = Require(options, "account-b");

            JsonElement state = await ReadJsonAsync(statePath).ConfigureAwait(false);
            string room = state.GetProperty("room").GetString()!;
            string accountA = state.GetProperty("account").GetString()!;
            int seatB = 1;
            string requestIdA = state.GetProperty("requestId").GetString()!;
            string scopeIdA = state.GetProperty("scopeId").GetString()!;
            long commandVersionA = state.GetProperty("commandVersion").GetInt64();

            WorkerTicketSigner signer = await WorkerTicketSigner.LoadAsync(keysDirectory).ConfigureAwait(false);
            await using TableTestClient clientB = await TableTestClient
                .ConnectAsync(url)
                .ConfigureAwait(false);
            Welcome welcomeB = await clientB
                .AdmitAsync(signer.SignTicket(accountB, room, seatB))
                .ConfigureAwait(false);

            // A foreign account presenting account A's scope+request is refused outright.
            await clientB.SendAsync(TableWire.ReceiptQuery(welcomeB, scopeIdA, requestIdA)).ConfigureAwait(false);
            Envelope foreign = await clientB.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error ||
                            envelope.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (foreign.MessageType != MessageType.Error)
            {
                throw new InvalidOperationException(
                    "A cross-account receipt query was answered with " + foreign.MessageType +
                    " instead of a SCOPE_MISMATCH refusal.");
            }

            if (foreign.Error.DetailCode != "SCOPE_MISMATCH")
            {
                throw new InvalidOperationException(
                    "The cross-account refusal carried detail " + foreign.Error.DetailCode +
                    " instead of SCOPE_MISMATCH.");
            }

            // The foreign account's own unknown request stays an UNKNOWN-RESULT answer:
            // never a receipt, never an executed side effect.
            string unknownRequest = TableWire.RandomId("req");
            await clientB.SendAsync(TableWire.ReceiptQuery(welcomeB, welcomeB.Scope.ScopeId, unknownRequest))
                .ConfigureAwait(false);
            Envelope unknown = await clientB.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (unknown.ReceiptLookup.Status == Contracts.Wire.LookupStatus.Found)
            {
                throw new InvalidOperationException("An unknown request id answered with a FOUND receipt.");
            }

            // Both queries were pure reads: account A's durable ledger still binds its
            // ORIGINAL commit exactly once. The room's own auto-draw advances under the
            // service account, which this count deliberately excludes.
            int ledgerRows = await CountLedgerRowAsync(requestIdA, scopeIdA, commandVersionA).ConfigureAwait(false);
            if (ledgerRows != 1)
            {
                throw new InvalidOperationException(
                    "The queried historical receipt ledger changed (rows " + ledgerRows + ").");
            }

            await clientB.CloseCleanAsync().ConfigureAwait(false);
            await WriteJsonAsync(outputPath, new Dictionary<string, object>
            {
                ["room"] = room,
                ["accountA"] = accountA,
                ["accountB"] = accountB,
                ["foreignQueryRefused"] = true,
                ["foreignDetail"] = foreign.Error.DetailCode,
                ["unknownRequestFound"] = false,
                ["ledgerRows"] = ledgerRows
            }).ConfigureAwait(false);
            Console.WriteLine("crossaccount ok foreignRefused=true sideEffects=0");
            return ExitOk;
        }

        // ------------------------------------------------------------------
        // Fault injection (test worker only)
        // ------------------------------------------------------------------

        private static async Task<int> RunTamperEventHashAsync(Dictionary<string, string> options)
        {
            string room = Require(options, "room");
            string connection = RequireRuntimeConnection();
            // Flip one byte inside the newest checkpoint's committed-history payload.
            // Recovery verifies this payload's sha256 even for history AT or BELOW the
            // checkpoint watermark (those event rows are never re-read individually), so
            // this is the corruption surface the recovery contract actually guards; a
            // flipped event blob under the watermark would be silently invisible.
            const string sql = @"
UPDATE table_store.table_checkpoint
   SET history_blob = set_byte(history_blob, 0, (get_byte(history_blob, 0) + 1) % 256)
 WHERE room_id = @room_id
   AND state_version = (SELECT max(state_version) FROM table_store.table_checkpoint WHERE room_id = @room_id)
   AND history_blob IS NOT NULL";
            int rows = await ExecuteAsync(connection, sql, ("room_id", room)).ConfigureAwait(false);
            if (rows != 1)
            {
                throw new InvalidOperationException("No checkpoint history payload was tampered (rows " + rows + ").");
            }

            Console.WriteLine("tamper-event-hash ok (1 checkpoint history byte flipped)");
            return ExitOk;
        }

        private static async Task<int> RunTamperEventGapAsync(Dictionary<string, string> options)
        {
            string room = Require(options, "room");
            string connection = RequireRuntimeConnection();
            int total = await ScalarCountAsync(
                connection,
                "SELECT count(*) FROM table_store.table_event WHERE room_id = @room_id",
                room).ConfigureAwait(false);
            if (total < 1)
            {
                throw new InvalidOperationException("The room has no committed events to gap.");
            }

            // Roll the checkpoint back to its bootstrap row first: recovery only reads
            // event rows ABOVE the checkpoint watermark, and the caught-up checkpoint
            // would otherwise keep the deleted row below that horizon (invisible). With
            // only the bootstrap checkpoint left, the whole journal re-enters the read
            // horizon and the surviving rows are provably non-contiguous.
            const string rollbackSql = @"
DELETE FROM table_store.table_checkpoint
 WHERE room_id = @room_id
   AND state_version = (SELECT max(state_version) FROM table_store.table_checkpoint WHERE room_id = @room_id)";
            int rolledBack = await ExecuteAsync(connection, rollbackSql, ("room_id", room)).ConfigureAwait(false);
            if (rolledBack != 1)
            {
                throw new InvalidOperationException("No checkpoint row was rolled back (rows " + rolledBack + ").");
            }

            // Remove a committed row strictly below the top so the surviving journal is
            // provably non-contiguous against the committed command coverage.
            const string sql = @"
DELETE FROM table_store.table_event
 WHERE room_id = @room_id
   AND seq = GREATEST((SELECT max(seq) FROM table_store.table_event WHERE room_id = @room_id) - 1, 1)";
            int rows = await ExecuteAsync(connection, sql, ("room_id", room)).ConfigureAwait(false);
            if (rows != 1)
            {
                throw new InvalidOperationException("No event row was deleted (rows " + rows + ").");
            }

            Console.WriteLine("tamper-event-gap ok (rows=" + total.ToString(CultureInfo.InvariantCulture) + ")");
            return ExitOk;
        }

        private static async Task<int> RunTamperScopeRevokeAsync(Dictionary<string, string> options)
        {
            string statePath = Require(options, "state");
            JsonElement state = await ReadJsonAsync(statePath).ConfigureAwait(false);
            string scopeId = state.GetProperty("scopeId").GetString()!;
            string connection = RequireRuntimeConnection();
            ulong nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int rows = await ExecuteAsync(
                connection,
                @"UPDATE table_store.request_scope
                     SET revoked_at_utc_ms = @revoked_at
                   WHERE scope_id = @scope_id AND revoked_at_utc_ms IS NULL",
                ("revoked_at", (long)nowMs),
                ("scope_id", scopeId)).ConfigureAwait(false);
            if (rows != 1)
            {
                throw new InvalidOperationException("The phase-1 scope was not revoked (rows " + rows + ").");
            }

            Console.WriteLine("tamper-scope-revoke ok");
            return ExitOk;
        }

        private static async Task<int> RunCountsAsync(Dictionary<string, string> options)
        {
            string room = Require(options, "room");
            string outputPath = Require(options, "out");
            string connection = RequireRuntimeConnection();
            var counts = new Dictionary<string, int>
            {
                ["events"] = await ScalarCountAsync(connection, "SELECT count(*) FROM table_store.table_event WHERE room_id = @room_id", room).ConfigureAwait(false),
                ["receipts"] = await ScalarCountAsync(connection, "SELECT count(*) FROM table_store.request_receipt WHERE room_id = @room_id", room).ConfigureAwait(false),
                ["scopes"] = await ScalarCountAsync(connection, "SELECT count(*) FROM table_store.request_scope WHERE room_id = @room_id", room).ConfigureAwait(false),
                ["leases"] = await ScalarCountAsync(connection, "SELECT count(*) FROM table_store.table_lease WHERE room_id = @room_id", room).ConfigureAwait(false),
                ["checkpoints"] = await ScalarCountAsync(connection, "SELECT count(*) FROM table_store.table_checkpoint WHERE room_id = @room_id", room).ConfigureAwait(false)
            };
            await WriteJsonAsync(outputPath, counts).ConfigureAwait(false);
            Console.WriteLine("counts ok events=" + counts["events"].ToString(CultureInfo.InvariantCulture));
            return ExitOk;
        }
    }
}
