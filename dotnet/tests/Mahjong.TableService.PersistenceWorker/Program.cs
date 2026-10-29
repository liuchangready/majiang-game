using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    ///   certgen             mint the run's ISOLATED TLS chain (test root + loopback leaf +
    ///                       wrong-name/expired negative leaves + an unrelated root) on disk;
    ///                       the machine trust store is never touched
    ///   phase1              admit through a REAL host, apply one legal action, same-key replay, record state
    ///   phase2              re-admit after cold recovery, explicit receipt query + same-key replay
    ///   claimed-host        deterministic real-WSS host: real discard + Chi/Pon + post-claim
    ///                       checkpoint through its own actor, then hold sessions live for -9
    ///   claimed-recover     NEW process: cold-recover the claimed opening fact/history,
    ///                       reconcile the old receipt, continue one real action, assert 136
    ///   admitprobe          attempt one admission and report the exact refusal shape (expect NO welcome)
    ///   crossaccount        a foreign account's receipt query is refused; an unknown request has no side effect
    ///   tamper-event-hash   corrupt one committed event blob byte (fail-closed recovery probe)
    ///   tamper-event-gap    delete one committed event row (fail-closed recovery probe)
    ///   tamper-scope-revoke mark the phase-1 request scope revoked directly in the database
    ///   counts              dump per-room durable row counts as JSON evidence
    ///   health              one HTTPS health GET validated by the same strict per-connection
    ///                       root trust as the WSS chain; exit 0 iff the status equals --expect
    ///
    /// Database connections arrive ONLY through the MAHJONG_P54B_TEST_PG (runtime DML role)
    /// and MAHJONG_P54B_TEST_PG_ADMIN (migration/reset) environment variables; the payload key
    /// travels through MAHJONG_P54B_TEST_PG_KEY. Nothing here logs a connection string, key,
    /// ticket or private payload. Fault injection lives only in this test worker — the
    /// production service carries no failpoint.
    ///
    /// D-R1-02 transport discipline: every wss:// connect requires an explicit --ca-root
    /// (PEM/DER on disk) and is validated by a per-connection CustomRootTrust callback that
    /// replicates strict validation — chain to the injected root AND name AND freshness —
    /// and never returns true blindly. There is no global trust pivot and no machine-store
    /// dependency anywhere in this process.
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
                    case "certgen":
                        return RunCertgen(options);
                    case "phase1":
                        return await RunPhase1Async(options).ConfigureAwait(false);
                    case "phase2":
                        return await RunPhase2Async(options).ConfigureAwait(false);
                    case "claimed-host":
                        return await ClaimedOpeningWorker.RunHostAsync(options).ConfigureAwait(false);
                    case "claimed-recover":
                        return await ClaimedOpeningWorker.RunRecoverAsync(options).ConfigureAwait(false);
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
                    case "health":
                        return await RunHealthAsync(options).ConfigureAwait(false);
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
                if (Environment.GetEnvironmentVariable("WORKER_DEBUG_STACK") == "1")
                {
                    Console.Error.WriteLine(exception.StackTrace);
                }

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

        internal static string Require(Dictionary<string, string> options, string name)
        {
            if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("missing required option --" + name);
            }

            return value;
        }

        internal static string? Optional(Dictionary<string, string> options, string name)
        {
            return options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }

        // ------------------------------------------------------------------
        // Shared evidence helpers
        // ------------------------------------------------------------------

        internal static async Task WriteJsonAsync(string path, object value)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            Console.WriteLine("wrote " + Path.GetFileName(path));
        }

        internal static async Task<JsonElement> ReadJsonAsync(string path)
        {
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        internal static string RequireRuntimeConnection()
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

        internal static async Task<int> ScalarCountAsync(string connectionString, string sql, string? roomId)
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
        internal static async Task<int> CountLedgerRowAsync(string requestId, string scopeId, long commandVersion)
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
        // D-R1-02: controlled WSS — isolated chain minting, strict per-connection
        // root trust, fail-closed connect helper, validated health probe
        // ------------------------------------------------------------------

        /// <summary>
        /// Mints the run's TLS material on disk with the same isolated-chain recipe the
        /// accepted <c>TlsCertificateTests</c> use: an ECDSA P-256 test root (CA:KEYCERTSIGN)
        /// signs a server-auth leaf carrying SAN DNS "localhost" + IP 127.0.0.1, so a host
        /// bound to the loopback passes strict name validation. The negatives are real
        /// material, not toggles: a leaf naming some other host entirely, a leaf whose
        /// validity window has passed, and a second UNRELATED root for the untrusted-issuer
        /// case. Everything lands under --out; the OS trust store is never read or written.
        /// </summary>
        private static int RunCertgen(Dictionary<string, string> options)
        {
            string outputDirectory = Require(options, "out");
            Directory.CreateDirectory(outputDirectory);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            // The issuer's notBefore bounds its leaves: the root is backdated so the
            // expired negative leaf's long-past validity window can be issued at all
            // (same recipe the accepted TLS tests use). Certificates are deliberately
            // not disposed: this CLI process exits immediately after writing them.
            (X509Certificate2 root, byte[] rootPfx) = CreateTestRoot(now.AddDays(-40), now.AddYears(2));
            (X509Certificate2 leaf, byte[] leafPfx) = CreateLeaf(
                root, "localhost", includeLoopback: true,
                now.AddMinutes(-10), now.AddDays(30), serialTail: 0x02);
            (X509Certificate2 wrongName, byte[] wrongNamePfx) = CreateLeaf(
                root, "real-table-service.example", includeLoopback: false,
                now.AddMinutes(-10), now.AddDays(30), serialTail: 0x01);
            (X509Certificate2 expired, byte[] expiredPfx) = CreateLeaf(
                root, "localhost", includeLoopback: true,
                now.AddDays(-30), now.AddDays(-1), serialTail: 0x03);
            (X509Certificate2 otherRoot, byte[] otherRootPfx) = CreateTestRoot(now.AddMinutes(-10), now.AddYears(2));

            // The PFX bytes are written exactly once, from the ORIGINAL export: a
            // re-imported certificate's key arrives as an EPHEMERAL Windows CNG key,
            // and a second PFX export of an ephemeral key fails with "key not valid
            // for use in specified state" (measured). PEM output is public material
            // and unaffected.
            File.WriteAllBytes(Path.Combine(outputDirectory, "root.pfx"), rootPfx);
            File.WriteAllText(Path.Combine(outputDirectory, "root.pem"), root.ExportCertificatePem());
            File.WriteAllBytes(Path.Combine(outputDirectory, "leaf.pfx"), leafPfx);
            File.WriteAllBytes(Path.Combine(outputDirectory, "leaf-wrongname.pfx"), wrongNamePfx);
            File.WriteAllBytes(Path.Combine(outputDirectory, "leaf-expired.pfx"), expiredPfx);
            File.WriteAllBytes(Path.Combine(outputDirectory, "root-other.pfx"), otherRootPfx);
            File.WriteAllText(Path.Combine(outputDirectory, "root-other.pem"), otherRoot.ExportCertificatePem());

            Console.WriteLine("certgen ok dir=" + outputDirectory);
            return ExitOk;
        }

        /// <summary>The isolated-chain recipe: same shape as the accepted TLS tests.</summary>
        private static (X509Certificate2 Certificate, byte[] Pfx) CreateTestRoot(DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(
                "CN=TableService P5.4b Verification Root",
                key,
                HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using X509Certificate2 selfSigned = request.CreateSelfSigned(notBefore, notAfter);
            // Export once and re-import so the returned certificate owns its key
            // independent of the builder; the SAME bytes go to disk.
            byte[] pfx = selfSigned.Export(X509ContentType.Pfx);
            return (new X509Certificate2(pfx), pfx);
        }

        private static (X509Certificate2 Certificate, byte[] Pfx) CreateLeaf(
            X509Certificate2 issuer,
            string subjectName,
            bool includeLoopback,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter,
            byte serialTail)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(
                "CN=" + subjectName,
                key,
                HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(subjectName);
            if (includeLoopback)
            {
                san.AddIpAddress(System.Net.IPAddress.Loopback);
            }

            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
            // One DISTINCT issuer+serial pair per leaf: duplicate serials from a single
            // issuer are broken chain semantics.
            using X509Certificate2 publicLeaf = request.Create(
                issuer,
                notBefore,
                notAfter,
                new byte[] { 0x50, 0x35, 0x34, 0x62, serialTail });
            using X509Certificate2 withKey = publicLeaf.CopyWithPrivateKey(key);
            byte[] pfx = withKey.Export(X509ContentType.Pfx);
            return (new X509Certificate2(pfx), pfx);
        }

        /// <summary>
        /// The strict per-connection validation every wss:// and https:// probe of this
        /// worker rides. The OS handshake always reports RemoteCertificateChainErrors for
        /// an untrusted test root — that one error is exactly what the CustomRootTrust
        /// build re-validates, so it alone is masked; name mismatch and every other policy
        /// error stay fatal, and the chain build itself re-checks issuer trust AND validity
        /// periods. It never returns true blindly.
        /// </summary>
        private static bool ValidateAgainstTrustedRoot(
            X509Certificate2 trustedRoot,
            X509Certificate2? certificate,
            SslPolicyErrors sslPolicyErrors)
        {
            if (certificate == null)
            {
                return false;
            }

            if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            {
                return false;
            }

            using X509Chain custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.Add(trustedRoot);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return custom.Build(certificate);
        }

        private static RemoteCertificateValidationCallback BuildStrictRootCallback(X509Certificate2 trustedRoot)
        {
            return (sender, certificate, chain, sslPolicyErrors) =>
                ValidateAgainstTrustedRoot(
                    trustedRoot,
                    certificate == null ? null : new X509Certificate2(certificate),
                    sslPolicyErrors);
        }

        private static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>
            BuildStrictHttpRootCallback(X509Certificate2 trustedRoot)
        {
            return (request, certificate, chain, sslPolicyErrors) =>
                ValidateAgainstTrustedRoot(trustedRoot, certificate, sslPolicyErrors);
        }

        /// <summary>Loads the root the worker was told to trust — disk file, explicit, no store.</summary>
        private static X509Certificate2 LoadTrustedRoot(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The trusted root certificate file does not exist.", path);
            }

            // X509Certificate2(string) sniffs PEM and DER alike, so .pem and .pfx both load.
            return new X509Certificate2(path);
        }

        /// <summary>
        /// Fail-closed connect: a wss:// URL without an explicit --ca-root is refused —
        /// the worker never falls back to machine trust. Plain ws:// keeps the prior
        /// behavior so the memory-mode harnesses are untouched.
        /// </summary>
        internal static Task<TableTestClient> ConnectTableClientAsync(Dictionary<string, string> options, string url)
        {
            if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                X509Certificate2 root = LoadTrustedRoot(Require(options, "ca-root"));
                return TableTestClient.ConnectAsync(url, BuildStrictRootCallback(root));
            }

            return TableTestClient.ConnectAsync(url);
        }

        /// <summary>
        /// One health GET through the same strict per-connection trust as the WSS chain.
        /// Exit 0 iff the server answers with exactly --expect; TLS validation failures,
        /// connection failures and status mismatches all fail the run. This keeps the
        /// health surface OUT of any bypass: under the controlled WSS shape the health
        /// endpoints terminate on the same TLS listener and are validated identically.
        /// </summary>
        private static async Task<int> RunHealthAsync(Dictionary<string, string> options)
        {
            string url = Require(options, "url");
            int expected = int.Parse(Require(options, "expect"), CultureInfo.InvariantCulture);

            using var handler = new HttpClientHandler();
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                handler.ServerCertificateCustomValidationCallback =
                    BuildStrictHttpRootCallback(LoadTrustedRoot(Require(options, "ca-root")));
            }

            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using HttpResponseMessage response = await httpClient.GetAsync(url).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            Console.WriteLine("health status=" + status);
            if (status != expected)
            {
                Console.Error.WriteLine("health expected " + expected + " got " + status);
                return ExitFailed;
            }

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
            await using TableTestClient client = await ConnectTableClientAsync(options, url).ConfigureAwait(false);
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

            // D-R1-01: the E2E crash shape needs a PUBLIC discard fact to carry across
            // the forced kill. A discard moves a physical tile into the seat's public
            // pond, which a re-dealt successor room (fresh shuffle) could never
            // reproduce — so phase 2 pins the recovered projection to the PERSISTED
            // journal instead of a re-drawn initial anchor. Prefer the discard
            // candidate; fall back to the first action only if none exists.
            Contracts.Wire.LegalActionView action = decision.Actions.FirstOrDefault(
                candidate => candidate.ActionType == (uint)ProtocolActionType.DiscardTile) ??
                decision.Actions[0];
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

            // The applied command must already be visible in the PUBLIC projection
            // before the kill: the recorded discard tiles are the exact fact phase 2
            // re-reads from the recovered host's projection.
            Envelope discardUpdate = await client.ReadUntilAsync(
                envelope => envelope.PlayerUpdate?.Snapshot is Contracts.Wire.PlayerSnapshot discardSnapshot &&
                            discardSnapshot.PublicView?.Board.Players
                                .Any(player => player.Seat == seat && player.Discards.Count > 0) == true,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Contracts.Wire.PlayerPublicView publicSelf = discardUpdate.PlayerUpdate!.Snapshot.PublicView!
                .Board.Players.First(player => player.Seat == seat);
            string discardTiles = string.Join(
                ',',
                publicSelf.Discards.Select(tile => tile.TileId.ToString(CultureInfo.InvariantCulture)));

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
                ["payloadBase64"] = Convert.ToBase64String(payload),
                ["publicDiscardSeat"] = seat,
                ["publicDiscardCount"] = publicSelf.Discards.Count,
                ["publicDiscardTiles"] = discardTiles,
                ["publicDiscardStateVersion"] = (long)discardUpdate.PlayerUpdate!.Snapshot.StateVersion
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

        internal static async Task<Envelope> SendAndAwaitReceiptAsync(
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
            await using TableTestClient client = await ConnectTableClientAsync(options, url).ConfigureAwait(false);
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

            // D-R1-01: the recovered projection must STILL show the pre-crash public
            // discard fact with the SAME physical tiles. A successor that re-dealt a
            // fresh room would present an empty pond for this seat (or different tile
            // ids), so this equality — read from the LIVE successor process — is the
            // proof the projection was rebuilt from the persisted journal, never from
            // a re-drawn initial anchor. The recorded pond must appear VERBATIM as a
            // prefix: the room's own deadline auto-play may legally append further
            // seat discards between the kill and this read, but it can never rewrite
            // or reorder what the journal already committed.
            string expectedTiles = state.GetProperty("publicDiscardTiles").GetString()!;
            int expectedCount = state.GetProperty("publicDiscardCount").GetInt32();
            Envelope recoveredProjection = await client.ReadUntilAsync(
                envelope => envelope.PlayerUpdate?.Snapshot is Contracts.Wire.PlayerSnapshot recoveredSnapshot &&
                            recoveredSnapshot.StateVersion >= commandVersion &&
                            recoveredSnapshot.PublicView?.Board.Players
                                .Any(player => player.Seat == seat && player.Discards.Count > 0) == true,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Contracts.Wire.PlayerPublicView recoveredSelf = recoveredProjection.PlayerUpdate!.Snapshot.PublicView!
                .Board.Players.First(player => player.Seat == seat);
            string recoveredTiles = string.Join(
                ',',
                recoveredSelf.Discards.Select(tile => tile.TileId.ToString(CultureInfo.InvariantCulture)));
            if (recoveredSelf.Discards.Count < expectedCount ||
                (!string.Equals(recoveredTiles, expectedTiles, StringComparison.Ordinal) &&
                 !recoveredTiles.StartsWith(expectedTiles + ",", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "The recovered public projection lost or rewrote the pre-crash discard fact (seat " +
                    seat + " expected tiles '" + expectedTiles + "' got '" + recoveredTiles + "').");
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
                ["expectRevokedScope"] = expectRevokedScope,
                ["discardsSurvivedRecovery"] = true,
                ["publicDiscardCount"] = recoveredSelf.Discards.Count,
                ["publicDiscardTiles"] = recoveredTiles
            }).ConfigureAwait(false);
            Console.WriteLine(
                "phase2 ok recoveredVersion=" + recoveredVersion.ToString(CultureInfo.InvariantCulture) +
                " ledgerRows=" + ledgerRows.ToString(CultureInfo.InvariantCulture) +
                " discardTiles=" + recoveredTiles);
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
            await using TableTestClient client = await ConnectTableClientAsync(options, url).ConfigureAwait(false);
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
            await using TableTestClient clientB = await ConnectTableClientAsync(options, url).ConfigureAwait(false);
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
