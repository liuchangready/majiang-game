using System;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// Explicit temporary product limits. P5.3 does not decide guest ranked
    /// play, spectator delay, matchmaking policy, or account retention; these
    /// values only keep the launch surface closed until those decisions exist.
    /// </summary>
    public sealed class LobbyOptions
    {
        public string StorageProvider { get; set; } = "Memory";

        public string? PostgresConnectionString { get; set; }

        public bool AutoMigrate { get; set; }

        public bool AllowInMemoryStorageInProduction { get; set; }

        public int SessionTtlHours { get; set; } = 24;

        public int GuestAccountRetentionDays { get; set; } = 7;

        public int ReceiptRetentionDays { get; set; } = 7;

        public int TicketTtlSeconds { get; set; } = 60;

        public int TicketMaxTtlSeconds { get; set; } = 300;

        public int ClockSkewSeconds { get; set; } = 5;

        public int ReservationTtlSeconds { get; set; } = 15;

        public int QueueTtlSeconds { get; set; } = 120;

        public int DefaultPageSize { get; set; } = 20;

        public int MaxPageSize { get; set; } = 100;

        public int MaintenanceIntervalSeconds { get; set; } = 1;

        /// <summary>Temporary safety cap; spectator product limits are not decided.</summary>
        public int MaxSpectatorsPerRoom { get; set; } = 32;

        public bool MatchingEnabled { get; set; } = true;

        public bool AllowGuestRankedMatch { get; set; }

        public bool ProductDecisionAcknowledged { get; set; }

        public string MatchRule { get; set; } = "FIFO_TABLE_FILL_V1_TEMPORARY";

        public int SpectatorDelaySeconds { get; set; }

        public string ProjectionSchema { get; set; } = "table_runtime";

        public bool OidcEnabled { get; set; }

        public string TicketIssuer { get; set; } = "mahjong-lobby";

        public string TicketAudience { get; set; } = "mahjong-table";

        public string? TicketPrivateKeyPem { get; set; }

        public string? TicketKeyId { get; set; }

        public string[] AdditionalValidationPublicKeysPem { get; set; } = Array.Empty<string>();

        public void Validate(string environmentName)
        {
            if (!string.Equals(StorageProvider, "Memory", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(StorageProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
            {
                throw new LobbyOptionsException("Lobby:StorageProvider must be Memory or Postgres.");
            }

            bool production = string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(StorageProvider, "Postgres", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(PostgresConnectionString))
            {
                throw new LobbyOptionsException("Lobby:PostgresConnectionString is required for Postgres storage.");
            }

            if (string.Equals(StorageProvider, "Memory", StringComparison.OrdinalIgnoreCase) &&
                production &&
                !AllowInMemoryStorageInProduction)
            {
                throw new LobbyOptionsException(
                    "In-memory lobby storage is not durable and is refused in Production. Configure PostgreSQL.");
            }

            if (OidcEnabled)
            {
                throw new LobbyOptionsException(
                    "OIDC is not implemented in P5.3. Do not enable it and do not treat guest sessions as accounts.");
            }

            if (SessionTtlHours < 1 || SessionTtlHours > 24 * 30)
            {
                throw new LobbyOptionsException("Lobby:SessionTtlHours must be between 1 and 720.");
            }

            if (GuestAccountRetentionDays < 1 || GuestAccountRetentionDays > 3650)
            {
                throw new LobbyOptionsException("Lobby:GuestAccountRetentionDays must be between 1 and 3650.");
            }

            if (ReceiptRetentionDays < 1 || ReceiptRetentionDays > 3650)
            {
                throw new LobbyOptionsException("Lobby:ReceiptRetentionDays must be between 1 and 3650.");
            }

            if (TicketTtlSeconds < 5 || TicketTtlSeconds > 300)
            {
                throw new LobbyOptionsException("Lobby:TicketTtlSeconds must be between 5 and 300.");
            }

            if (TicketMaxTtlSeconds < TicketTtlSeconds || TicketMaxTtlSeconds > 600)
            {
                throw new LobbyOptionsException("Lobby:TicketMaxTtlSeconds must be >= TTL and <= 600.");
            }

            if (ClockSkewSeconds < 0 || ClockSkewSeconds > 60)
            {
                throw new LobbyOptionsException("Lobby:ClockSkewSeconds must be between 0 and 60.");
            }

            if (ReservationTtlSeconds < 5 || ReservationTtlSeconds > 60)
            {
                throw new LobbyOptionsException("Lobby:ReservationTtlSeconds must be between 5 and 60.");
            }

            if (QueueTtlSeconds < 30 || QueueTtlSeconds > 600)
            {
                throw new LobbyOptionsException("Lobby:QueueTtlSeconds must be between 30 and 600.");
            }

            if (DefaultPageSize < 1 || DefaultPageSize > MaxPageSize || MaxPageSize > 200)
            {
                throw new LobbyOptionsException("Lobby page sizes are invalid.");
            }

            if (MaintenanceIntervalSeconds < 1 || MaintenanceIntervalSeconds > 60)
            {
                throw new LobbyOptionsException("Lobby:MaintenanceIntervalSeconds must be between 1 and 60.");
            }

            if (MaxSpectatorsPerRoom < 0 || MaxSpectatorsPerRoom > 1024)
            {
                throw new LobbyOptionsException("Lobby:MaxSpectatorsPerRoom must be between 0 and 1024.");
            }

            if (!string.Equals(MatchRule, "FIFO_TABLE_FILL_V1_TEMPORARY", StringComparison.Ordinal))
            {
                throw new LobbyOptionsException(
                    "Only the explicit temporary FIFO_TABLE_FILL_V1_TEMPORARY match rule is implemented. Product policy is pending.");
            }

            if (SpectatorDelaySeconds != 0)
            {
                throw new LobbyOptionsException(
                    "Spectator delay is a pending product decision and is not implemented. Leave Lobby:SpectatorDelaySeconds at 0.");
            }

            if (AllowGuestRankedMatch && !ProductDecisionAcknowledged)
            {
                throw new LobbyOptionsException(
                    "Guest ranked match is a pending product decision. It requires an explicit product acknowledgement and is not enabled by code default.");
            }

            if (string.IsNullOrWhiteSpace(TicketIssuer) || TicketIssuer.Length > 128)
            {
                throw new LobbyOptionsException("Lobby:TicketIssuer must be a non-empty string of at most 128 characters.");
            }

            if (string.IsNullOrWhiteSpace(TicketAudience) || TicketAudience.Length > 128)
            {
                throw new LobbyOptionsException("Lobby:TicketAudience must be a non-empty string of at most 128 characters.");
            }

            if (!IsSafeSqlIdentifier(ProjectionSchema))
            {
                throw new LobbyOptionsException("Lobby:ProjectionSchema must be a lowercase SQL identifier.");
            }
        }

        public bool UsesPostgres =>
            string.Equals(StorageProvider, "Postgres", StringComparison.OrdinalIgnoreCase);

        private static bool IsSafeSqlIdentifier(string value)
        {
            if (value.Length == 0 || value.Length > 32 || value[0] < 'a' || value[0] > 'z')
            {
                return false;
            }

            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
