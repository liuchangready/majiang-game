using System;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class PostgresIntegrationTests
    {
        [Fact]
        public async Task PostgresLobbyRoundTripWhenARealDatabaseIsConfigured()
        {
            string? connectionString = Environment.GetEnvironmentVariable("MAHJONG_LOBBY_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Assert.Skip(
                    "NOT_RUN: no PostgreSQL test database. Set MAHJONG_LOBBY_TEST_POSTGRES to run the transactional store test.");
                return;
            }

            var options = new LobbyOptions
            {
                StorageProvider = "Postgres",
                PostgresConnectionString = connectionString,
                AutoMigrate = true,
                TicketTtlSeconds = 60,
                TicketMaxTtlSeconds = 300,
                ReservationTtlSeconds = 15,
                QueueTtlSeconds = 120
            };
            options.Validate("Development");
            var clock = new TestClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
            var store = new PostgresLobbyStore(connectionString, options);
            await store.InitializeAsync(default);
            await using LobbyFixture fixture = LobbyFixture.CreateWithStore(store, options, clock);

            GuestSessionView host = await fixture.CreateGuestAsync("pg-host-" + Guid.NewGuid().ToString("N"));
            CreateRoomView room = await fixture.CreateRoomAsync(host, "Postgres Room");
            TableTicketView ticket = await fixture.IssueTicketAsync(host, room.Assignment.AssignmentId);
            TicketConsumptionResult consumed = await fixture.ConsumeAsync(ticket, host.AccountId);

            Assert.Equal(room.Assignment.AssignmentId, consumed.AssignmentId);
            Assert.Equal(0, consumed.SeatId);
        }
    }
}
