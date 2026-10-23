using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// Read-only public room_projection reader. It selects only the public
    /// lifecycle/count/version columns and never table_event or private state.
    /// The table is owned by TableService/P5.1, so P5.3 does not create it.
    /// </summary>
    public sealed class PostgresRoomProjectionReader : IRoomProjectionReader, IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly string _schema;

        public PostgresRoomProjectionReader(string connectionString, string schema)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
            }

            if (string.IsNullOrWhiteSpace(schema) || schema.Length > 32)
            {
                throw new ArgumentException("A safe projection schema is required.", nameof(schema));
            }

            for (int i = 0; i < schema.Length; i++)
            {
                char c = schema[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!valid || (i == 0 && c >= '0' && c <= '9'))
                {
                    throw new ArgumentException("Projection schema must be a lowercase SQL identifier.", nameof(schema));
                }
            }

            _schema = schema;
            _dataSource = NpgsqlDataSource.Create(connectionString);
        }

        public async Task<PublicRoomProjectionBatch> ReadAsync(
            IReadOnlyList<string> roomIds,
            CancellationToken cancellationToken)
        {
            if (roomIds.Count == 0)
            {
                return PublicRoomProjectionBatch.Empty;
            }

            string[] ids = new string[roomIds.Count];
            for (int i = 0; i < roomIds.Count; i++)
            {
                ids[i] = roomIds[i];
            }

            string sql =
                "SELECT room_id, lifecycle, seats_occupied, spectator_count, public_version, ruleset_version, updated_at " +
                "FROM " + _schema + ".room_projection WHERE room_id = ANY(@room_ids)";

            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.Add(new NpgsqlParameter("room_ids", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = ids
            });

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var items = new List<PublicRoomProjection>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new PublicRoomProjection(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    (ulong)reader.GetInt64(4),
                    (uint)reader.GetInt64(5),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc))));
            }

            return new PublicRoomProjectionBatch(items);
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }
}
