using System;
using System.IO;
using System.Reflection;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class MigrationSchemaTests
    {
        [Fact]
        public void MigrationDeclaresTheNoDoubleBookingAndReplayFences()
        {
            string sql = ReadMigration();
            Assert.Contains("CREATE SCHEMA IF NOT EXISTS lobby", sql, StringComparison.Ordinal);
            Assert.Contains("uq_account_bootstrap", sql, StringComparison.Ordinal);
            Assert.Contains("bootstrap_hash", sql, StringComparison.Ordinal);
            Assert.Contains("uq_assignment_active_seat", sql, StringComparison.Ordinal);
            Assert.Contains("ON lobby.assignment (account_id)", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("ON lobby.assignment (room_id, account_id)", sql, StringComparison.Ordinal);
            Assert.Contains("uq_assignment_active_account", sql, StringComparison.Ordinal);
            Assert.Contains("uq_match_active_account", sql, StringComparison.Ordinal);
            Assert.Contains("uq_table_ticket_active_assignment", sql, StringComparison.Ordinal);
            Assert.Contains("table_ticket_consumption", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS lobby.table_event", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("owned_epoch", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("owner_epoch", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void MigrationDoesNotCreateTheTableOwnedRoomProjection()
        {
            string sql = ReadMigration();
            Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS table_runtime.room_projection", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS lobby.room_projection", sql, StringComparison.Ordinal);
        }

        private static string ReadMigration()
        {
            Assembly assembly = typeof(PostgresLobbyStore).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(
                "Mahjong.LobbyService.Migrations.001_lobby.sql");
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            return reader.ReadToEnd();
        }
    }
}
