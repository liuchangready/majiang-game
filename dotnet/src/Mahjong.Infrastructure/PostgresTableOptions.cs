using System;
using System.Text;
using Mahjong.TableRuntime;

namespace Mahjong.Infrastructure
{
    /// <summary>
    /// Configuration for the P5.2 PostgreSQL table store. The payload key is read
    /// from an external secret source (or the explicitly supplied test/process
    /// input) and is never written to logs, SQL, evidence or exceptions.
    ///
    /// Key rotation is deliberately not implemented in P5.2: one key id/version is
    /// bound to every sealed payload. Rotating the key requires a controlled
    /// re-encryption migration and is therefore an explicit NOT_RUN boundary, not
    /// an implicit fallback.
    /// </summary>
    public sealed class PostgresTableOptions
    {
        public const string PayloadKeyEnvironmentVariable = "MAHJONG_TABLE_PG_PAYLOAD_KEY";
        public const int PayloadKeyBytes = 32;

        private readonly byte[] _payloadKey;

        public PostgresTableOptions(
            string connectionString,
            string payloadKeyBase64,
            int commandTimeoutSeconds = 15,
            int maxPoolSize = 20)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
            }

            if (commandTimeoutSeconds < 1 || commandTimeoutSeconds > 600)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandTimeoutSeconds),
                    commandTimeoutSeconds,
                    "Command timeout must be 1..600 seconds.");
            }

            if (maxPoolSize < 1 || maxPoolSize > 200)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPoolSize), maxPoolSize, "Max pool size must be 1..200.");
            }

            if (string.IsNullOrWhiteSpace(payloadKeyBase64))
            {
                payloadKeyBase64 = Environment.GetEnvironmentVariable(PayloadKeyEnvironmentVariable) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(payloadKeyBase64))
            {
                throw new PostgresTableConfigurationException(
                    "A table payload encryption key is required. Set " + PayloadKeyEnvironmentVariable +
                    " or supply it from the deployment secret store.");
            }

            try
            {
                _payloadKey = Convert.FromBase64String(payloadKeyBase64);
            }
            catch (FormatException)
            {
                throw new PostgresTableConfigurationException("The table payload encryption key is not valid base64.");
            }

            if (_payloadKey.Length != PayloadKeyBytes)
            {
                throw new PostgresTableConfigurationException(
                    "The table payload encryption key must be exactly " + PayloadKeyBytes + " bytes.");
            }

            ConnectionString = connectionString;
            CommandTimeoutSeconds = commandTimeoutSeconds;
            _payloadKey = (byte[])_payloadKey.Clone();
            MaxPoolSize = maxPoolSize;
        }

        public string ConnectionString { get; }

        public int CommandTimeoutSeconds { get; }

        public int MaxPoolSize { get; }

        internal byte[] CopyPayloadKey()
        {
            var copy = new byte[_payloadKey.Length];
            Array.Copy(_payloadKey, copy, copy.Length);
            return copy;
        }

        public override string ToString()
        {
            // Never render the connection string (it may contain a password) or key.
            return "PostgresTableOptions { CommandTimeoutSeconds=" + CommandTimeoutSeconds +
                   ", MaxPoolSize=" + MaxPoolSize + " }";
        }
    }

    public sealed class PostgresTableConfigurationException : Exception
    {
        public PostgresTableConfigurationException(string message)
            : base(message)
        {
        }
    }

    internal static class TablePostgresSchema
    {
        internal const string Name = "table_store";
        internal const string RuntimeRole = "mahjong_table_runtime";
    }
}
