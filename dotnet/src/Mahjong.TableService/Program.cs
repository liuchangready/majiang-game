using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Mahjong.TableService;

namespace Mahjong.TableService
{
    /// <summary>
    /// Production entry point. Configuration comes from TABLESERVICE_* environment variables
    /// (or appsettings.json); every value is validated fail-closed. The process REFUSES to
    /// start without at least one ticket validation key — there is no accept-all admission
    /// mode anywhere in TableService.
    /// </summary>
    public static class Program
    {
        public const int ExitBadConfig = 2;

        public const int ExitNoValidationKeys = 3;

        public static int Main(string[] args)
        {
            TableServiceOptions options;
            ITableTicketKeyProvider keys;
            try
            {
                options = LoadOptions();
                options.Validate();
                keys = LoadTicketKeys();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("tableservice: configuration refused: " + ex.Message);
                return ex is TableKeyConfigurationException ? ExitNoValidationKeys : ExitBadConfig;
            }

            TableServiceHostRuntime runtime;
            try
            {
                runtime = TableServiceWebHost.Build(options, keys, IPAddress.Loopback, options.HttpPort);
            }
            catch (Exception ex)
            {
                // A persistent host with a bad connection string, payload key or schema
                // refuses to start — it never boots into an in-memory fallback.
                Console.Error.WriteLine("tableservice: host build refused: " + ex.Message);
                return ExitBadConfig;
            }

            runtime.StartAsync().GetAwaiter().GetResult();
            Console.WriteLine("tableservice: listening on " + runtime.BoundAddress + " (ctrl+c to stop)");
            using ManualResetEventSlim done = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                done.Set();
            };
            done.Wait();
            runtime.StopAsync().GetAwaiter().GetResult();
            return 0;
        }

        public static TableServiceOptions LoadOptions()
        {
            var options = new TableServiceOptions();
            options.TicketIssuer = RequireString("TICKET_ISSUER", options.TicketIssuer);
            options.TicketAudience = RequireString("TICKET_AUDIENCE", options.TicketAudience);
            options.ClockSkewSeconds = RequireInt("CLOCK_SKEW_SECONDS", options.ClockSkewSeconds);
            options.TicketMaxTtlSeconds = RequireInt("TICKET_MAX_TTL_SECONDS", options.TicketMaxTtlSeconds);
            options.MaxInboundMessageBytes = RequireInt("MAX_INBOUND_MESSAGE_BYTES", options.MaxInboundMessageBytes);
            options.MaxCommandPayloadBytes = RequireInt("MAX_COMMAND_PAYLOAD_BYTES", options.MaxCommandPayloadBytes);
            options.MaxSnapshotBytes = RequireInt("MAX_SNAPSHOT_BYTES", options.MaxSnapshotBytes);
            options.MaxInFlightRequests = RequireInt("MAX_IN_FLIGHT_REQUESTS", options.MaxInFlightRequests);
            options.HeartbeatIntervalMs = RequireInt("HEARTBEAT_INTERVAL_MS", options.HeartbeatIntervalMs);
            options.IdleDeadMs = RequireInt("IDLE_DEAD_MS", options.IdleDeadMs);
            options.HandshakeTimeoutMs = RequireInt("HANDSHAKE_TIMEOUT_MS", options.HandshakeTimeoutMs);
            options.CloseGraceMs = RequireInt("CLOSE_GRACE_MS", options.CloseGraceMs);
            options.MaxEarlyBufferItems = RequireInt("MAX_EARLY_BUFFER_ITEMS", options.MaxEarlyBufferItems);
            options.MaxEarlyBufferBytes = RequireInt("MAX_EARLY_BUFFER_BYTES", options.MaxEarlyBufferBytes);
            options.CommandRatePerSecond = RequireInt("COMMAND_RATE_PER_SECOND", options.CommandRatePerSecond);
            options.CommandBurstCapacity = RequireInt("COMMAND_BURST_CAPACITY", options.CommandBurstCapacity);
            options.MaxRooms = RequireInt("MAX_ROOMS", options.MaxRooms);
            options.MaxSpectatorsPerRoom = RequireInt("MAX_SPECTATORS_PER_ROOM", options.MaxSpectatorsPerRoom);
            options.MaxStreamsPerRoom = RequireInt("MAX_STREAMS_PER_ROOM", options.MaxStreamsPerRoom);
            options.MaxRetainedConsumedTickets = RequireInt("MAX_RETAINED_CONSUMED_TICKETS", options.MaxRetainedConsumedTickets);
            options.RoomTickIntervalMs = RequireInt("ROOM_TICK_INTERVAL_MS", options.RoomTickIntervalMs);
            options.LeaseTtlSeconds = RequireInt("LEASE_TTL_SECONDS", options.LeaseTtlSeconds);
            options.HttpPort = RequireInt("HTTP_PORT", options.HttpPort);
            options.AdminDrainToken = OptionalString("ADMIN_DRAIN_TOKEN");
            options.ServerCertificate = LoadServerCertificate();
            options.StoreMode = RequireStoreMode();
            if (options.StoreMode == TableHostStoreMode.Postgres)
            {
                options.PostgresConnectionString = RequireString(
                    "PG_CONNECTION",
                    options.PostgresConnectionString ?? string.Empty);
                options.PostgresPayloadKeyBase64 = OptionalString("PG_PAYLOAD_KEY");
                options.PostgresCommandTimeoutSeconds = RequireInt(
                    "PG_COMMAND_TIMEOUT_SECONDS",
                    options.PostgresCommandTimeoutSeconds);
                options.PostgresMaxPoolSize = RequireInt("PG_MAX_POOL_SIZE", options.PostgresMaxPoolSize);
                options.PostgresApplyMigrations = RequireBool(
                    "PG_APPLY_MIGRATIONS",
                    options.PostgresApplyMigrations);
            }

            return options;
        }

        /// <summary>
        /// TABLESERVICE_STORE selects the room store: "memory" (accepted P3.4/P5.1 mode) or
        /// "postgres" (P5.4b-A durable table_store mode). An unknown value refuses the
        /// start — the mode is never guessed.
        /// </summary>
        private static TableHostStoreMode RequireStoreMode()
        {
            string? value = Environment.GetEnvironmentVariable("TABLESERVICE_STORE");
            if (string.IsNullOrWhiteSpace(value))
            {
                return TableHostStoreMode.Memory;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "memory" => TableHostStoreMode.Memory,
                "postgres" => TableHostStoreMode.Postgres,
                _ => throw new ArgumentException(
                    "TABLESERVICE_STORE must be 'memory' or 'postgres': " + value)
            };
        }

        private static bool RequireBool(string suffix, bool fallback)
        {
            string? value = Environment.GetEnvironmentVariable("TABLESERVICE_" + suffix);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (!bool.TryParse(value, out bool parsed))
            {
                throw new ArgumentException("TABLESERVICE_" + suffix + " is not a boolean: " + value);
            }

            return parsed;
        }

        /// <summary>
        /// Loads read-only ticket validation keys. TABLESERVICE_TICKET_KEYS_FILE names a file
        /// of concatenated PEM PUBLIC KEY blocks (kid=JWK thumbprint), or
        /// TABLESERVICE_TICKET_KEYS_PEM holds PEM blocks separated by '§' so operators never
        /// fight shell newline escaping. No key source means the process refuses to start.
        /// </summary>
        public static ITableTicketKeyProvider LoadTicketKeys()
        {
            string? file = Environment.GetEnvironmentVariable("TABLESERVICE_TICKET_KEYS_FILE");
            string? inline = Environment.GetEnvironmentVariable("TABLESERVICE_TICKET_KEYS_PEM");
            var pems = new List<string>();
            if (!string.IsNullOrWhiteSpace(file))
            {
                if (!File.Exists(file))
                {
                    throw new TableKeyConfigurationException("Ticket key file does not exist: " + file);
                }

                foreach (string pem in SplitPems(File.ReadAllText(file)))
                {
                    pems.Add(pem);
                }
            }

            if (!string.IsNullOrWhiteSpace(inline))
            {
                foreach (string pem in inline.Split('§'))
                {
                    if (!string.IsNullOrWhiteSpace(pem))
                    {
                        pems.Add(pem.Trim());
                    }
                }
            }

            if (pems.Count == 0)
            {
                throw new TableKeyConfigurationException(
                    "No ticket validation keys configured (TABLESERVICE_TICKET_KEYS_FILE or TABLESERVICE_TICKET_KEYS_PEM); " +
                    "a table host without validation keys must not admit connections.");
            }

            var tuples = new List<(string Pem, string? Kid)>(pems.Count);
            foreach (string pem in pems)
            {
                // kid=null: the key is identified by its RFC 7638 JWK thumbprint.
                tuples.Add((pem, null));
            }

            return new StaticTableTicketKeyProvider(tuples, DateTimeOffset.UtcNow);
        }

        public static IEnumerable<string> SplitPems(string text)
        {
            var current = new List<string>();
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                current.Add(trimmed);
                if (trimmed.EndsWith("PUBLIC KEY-----", StringComparison.Ordinal) && current.Count > 1)
                {
                    yield return string.Join("\n", current);
                    current.Clear();
                }
            }

            if (current.Count > 0)
            {
                throw new FormatException("The ticket key file contains an incomplete PEM block.");
            }
        }

        private static System.Security.Cryptography.X509Certificates.X509Certificate2? LoadServerCertificate()
        {
            string? pfx = Environment.GetEnvironmentVariable("TABLESERVICE_TLS_PFX");
            if (string.IsNullOrWhiteSpace(pfx))
            {
                return null;
            }

            string? password = Environment.GetEnvironmentVariable("TABLESERVICE_TLS_PFX_PASSWORD");
            return new System.Security.Cryptography.X509Certificates.X509Certificate2(
                pfx,
                password ?? string.Empty);
        }

        private static string RequireString(string suffix, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable("TABLESERVICE_" + suffix);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string? OptionalString(string suffix)
        {
            return Environment.GetEnvironmentVariable("TABLESERVICE_" + suffix);
        }

        private static int RequireInt(string suffix, int fallback)
        {
            string? value = Environment.GetEnvironmentVariable("TABLESERVICE_" + suffix);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                throw new ArgumentException("TABLESERVICE_" + suffix + " is not an integer: " + value);
            }

            return parsed;
        }
    }

    /// <summary>Distinguishes "refuse to start: no validation keys" from other config errors.</summary>
    public sealed class TableKeyConfigurationException : Exception
    {
        public TableKeyConfigurationException(string message) : base(message)
        {
        }
    }
}
