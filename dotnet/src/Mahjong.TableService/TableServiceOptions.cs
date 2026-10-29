using System;
using System.Collections.Generic;
using Mahjong.Contracts.Wire;

namespace Mahjong.TableService
{
    /// <summary>Admission role carried by a table ticket and a bound connection.</summary>
    public enum TableAdmissionRole : byte
    {
        Player = 0,
        Spectator = 1
    }

    /// <summary>
    /// Where a host's rooms live (P5.4b-A). <see cref="Memory"/> is the accepted P3.4/P5.1
    /// in-process mode; <see cref="Postgres"/> binds every room's events, receipts,
    /// checkpoints, scopes, lease and settlement outbox to the P5.2 table_store database.
    /// A misconfigured persistent host refuses to start — it never silently falls back to
    /// memory.
    /// </summary>
    public enum TableHostStoreMode : byte
    {
        Memory = 0,
        Postgres = 1
    }

    /// <summary>
    /// Frozen V5 /v1/table host configuration. Every value is explicit; defaults mirror the
    /// frozen wire limits and the V5 heartbeat design values recorded in the protocol
    /// decisions. <see cref="Validate"/> is fail-closed: a production host without validation
    /// keys refuses to start and never falls back to accept-all admission.
    /// </summary>
    public sealed class TableServiceOptions
    {
        // Ticket contract (frozen Lobby ticket format: issuer/audience exact match, TTL cap,
        // clock skew). The TableService validator is read-only; it never signs tickets.
        public string TicketIssuer { get; set; } = "mahjong-lobby";

        public string TicketAudience { get; set; } = "mahjong-table";

        public int ClockSkewSeconds { get; set; } = 5;

        public int TicketMaxTtlSeconds { get; set; } = 300;

        // Wire envelope limits (frozen initial values; entry values pending real measurement).
        public int MaxInboundMessageBytes { get; set; } = WireLimits.MaxInboundMessageBytes;

        public int MaxCommandPayloadBytes { get; set; } = WireLimits.MaxCommandPayloadBytes;

        public int MaxSnapshotBytes { get; set; } = WireLimits.MaxSnapshotBytes;

        public int MaxInFlightRequests { get; set; } = 8;

        // Heartbeat / liveness (V5 contract; NOT the V4 MJTC 12-byte header).
        public int HeartbeatIntervalMs { get; set; } = 10_000;

        public int IdleDeadMs { get; set; } = 30_000;

        public int HandshakeTimeoutMs { get; set; } = 10_000;

        // Close handshake grace: one shared budget for the terminal CLOSE envelope and the
        // transport CloseOutput. Sized for a draining peer — a trickle reader reopens the
        // TCP window only at its read rate, and a too-short cap silently loses the close
        // handshake, leaving the client an abrupt transport reset instead of the mapped
        // close code. A peer that never drains still ends at this budget.
        public int CloseGraceMs { get; set; } = 30_000;

        // Early (pre-WELCOME) inbound bounding: an unauthenticated connection may hold at
        // most one partial HELLO-sized message; extra buffered messages are refused.
        public int MaxEarlyBufferItems { get; set; } = 2;

        public int MaxEarlyBufferBytes { get; set; } = 64 * 1024;

        // Ingress command rate (token bucket per connection; sustained floods are closed).
        public int CommandRatePerSecond { get; set; } = 30;

        public int CommandBurstCapacity { get; set; } = 60;

        // Registry bounds (per-table isolation; every room gets its own actor + store).
        public int MaxRooms { get; set; } = 64;

        public int MaxSpectatorsPerRoom { get; set; } = 32;

        public int MaxStreamsPerRoom { get; set; } = 64;

        /// <summary>
        /// Upper bound on retained one-time-ticket records (D-1-06). Consumed jti and
        /// assignment ids are kept exactly until their ticket's expiry + clock skew and are
        /// never evicted earlier, so a still-valid ticket can never replay. When the bound
        /// would be exceeded after expiry purging, admission fails closed instead of growing
        /// the store without a limit.
        /// </summary>
        public int MaxRetainedConsumedTickets { get; set; } = 65_536;

        // Actor tick cadence. The platform tick driver never runs on a WebSocket thread, so
        // transport heartbeats cannot block actor rule deadlines.
        public int RoomTickIntervalMs { get; set; } = 250;

        // Fixed HTTP listener port for production deployments (0 = bind an ephemeral port,
        // which is what unit tests and bare local runs use). A deployed host must be
        // reachable at a known address, so the entry point passes this to Kestrel.
        public int HttpPort { get; set; }

        // Durable port wiring (accepted P3.4 in-memory implementations; PG arrives with P5.2).
        public int LeaseTtlSeconds { get; set; } = 90;

        // P5.4b-A durable host assembly. In Postgres mode the SAME P5.2 store backs the
        // actors' durable context AND the host's scope issuance — there is no hybrid mode
        // and no in-memory fallback; a bad connection string, key or schema refuses the
        // build. The payload key may be blank here, in which case PostgresTableOptions
        // reads MAHJONG_TABLE_PG_PAYLOAD_KEY itself (and refuses to start without it).
        public TableHostStoreMode StoreMode { get; set; } = TableHostStoreMode.Memory;

        public string? PostgresConnectionString { get; set; }

        public string? PostgresPayloadKeyBase64 { get; set; }

        public int PostgresCommandTimeoutSeconds { get; set; } = 15;

        public int PostgresMaxPoolSize { get; set; } = 20;

        /// <summary>
        /// Apply the idempotent table_store migration at startup before serving. An operator
        /// who pre-applies the schema may leave this off; readiness is still gated on a
        /// verified schema either way.
        /// </summary>
        public bool PostgresApplyMigrations { get; set; }

        // Operator drain endpoint. Disabled (404) unless a token is configured.
        public string? AdminDrainToken { get; set; }

        /// <summary>
        /// Queued-byte cap for one connection's reply channel (D-1-07). The reply path carries
        /// receipts, errors, heartbeat echoes and SYNC_RESPONSE envelopes whose largest
        /// legitimate member is one baseline snapshot, so the negotiated snapshot limit is the
        /// honest upper bound: when queued reply bytes exceed it, the connection is closed as
        /// a slow consumer instead of growing without a byte ledger.
        /// </summary>
        public int MaxReplyQueuedBytes => MaxSnapshotBytes;

        // Optional loopback WSS termination for controlled-ingress tests. Production TLS
        // terminates at a controlled ingress; this certificate is never a trust bypass — the
        // peer must still validate the served chain against its own trusted roots.
        public System.Security.Cryptography.X509Certificates.X509Certificate2? ServerCertificate { get; set; }

        public void Validate()
        {
            RequireNotBlank(TicketIssuer, nameof(TicketIssuer));
            RequireNotBlank(TicketAudience, nameof(TicketAudience));
            RequireRange(ClockSkewSeconds, 0, 300, nameof(ClockSkewSeconds));
            RequireRange(TicketMaxTtlSeconds, 1, 86_400, nameof(TicketMaxTtlSeconds));
            RequireRange(MaxInboundMessageBytes, 1024, WireLimits.MaxInboundMessageBytes, nameof(MaxInboundMessageBytes));
            RequireRange(MaxCommandPayloadBytes, 256, WireLimits.MaxCommandPayloadBytes, nameof(MaxCommandPayloadBytes));
            RequireRange(MaxSnapshotBytes, 1024, WireLimits.MaxSnapshotBytes, nameof(MaxSnapshotBytes));
            RequireRange(MaxInFlightRequests, 1, 1024, nameof(MaxInFlightRequests));
            RequireRange(HeartbeatIntervalMs, 500, 600_000, nameof(HeartbeatIntervalMs));
            RequireRange(IdleDeadMs, HeartbeatIntervalMs * 2, 3_600_000, nameof(IdleDeadMs));
            RequireRange(HandshakeTimeoutMs, 100, 600_000, nameof(HandshakeTimeoutMs));
            RequireRange(CloseGraceMs, 500, 120_000, nameof(CloseGraceMs));
            RequireRange(MaxEarlyBufferItems, 1, 64, nameof(MaxEarlyBufferItems));
            RequireRange(MaxEarlyBufferBytes, 1024, MaxInboundMessageBytes, nameof(MaxEarlyBufferBytes));
            RequireRange(CommandRatePerSecond, 1, 10_000, nameof(CommandRatePerSecond));
            RequireRange(CommandBurstCapacity, 1, 100_000, nameof(CommandBurstCapacity));
            RequireRange(MaxRooms, 1, 4096, nameof(MaxRooms));
            RequireRange(MaxSpectatorsPerRoom, 0, 1024, nameof(MaxSpectatorsPerRoom));
            RequireRange(MaxStreamsPerRoom, 4, 4096, nameof(MaxStreamsPerRoom));
            RequireRange(MaxRetainedConsumedTickets, 1024, 16_777_216, nameof(MaxRetainedConsumedTickets));
            RequireRange(RoomTickIntervalMs, 10, 60_000, nameof(RoomTickIntervalMs));
            RequireRange(LeaseTtlSeconds, 5, 3_600, nameof(LeaseTtlSeconds));
            RequireRange(HttpPort, 0, 65_535, nameof(HttpPort));

            // P5.4b-A: a Postgres-mode host REQUIRES a connection string here and a payload
            // key from this field or MAHJONG_TABLE_PG_PAYLOAD_KEY (refused inside
            // PostgresTableOptions). The key itself is never logged or rendered.
            if (StoreMode == TableHostStoreMode.Postgres)
            {
                RequireNotBlank(PostgresConnectionString!, nameof(PostgresConnectionString));
                RequireRange(PostgresCommandTimeoutSeconds, 1, 600, nameof(PostgresCommandTimeoutSeconds));
                RequireRange(PostgresMaxPoolSize, 1, 200, nameof(PostgresMaxPoolSize));
            }
        }

        private static void RequireNotBlank(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(name + " must not be blank.", name);
            }
        }

        private static void RequireRange(int value, int min, int max, string name)
        {
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(name, value, name + " must be " + min + ".." + max + ".");
            }
        }
    }

    /// <summary>
    /// Process-wide drain gate. While draining, new admissions are refused (HTTP 503 at the
    /// WebSocket handshake and readiness reports unhealthy). An established session keeps its
    /// stream and liveness answering, but it is NOT allowed to start new side effects: a
    /// COMMAND arrives while draining is refused with the retryable SERVER_BUSY(DRAINING)
    /// session error, and SYNC/queries are still answered from read-only paths.
    /// </summary>
    public sealed class DrainState
    {
        private volatile bool _draining;

        public bool IsDraining => _draining;

        public void Set(bool draining)
        {
            _draining = draining;
        }
    }
}
