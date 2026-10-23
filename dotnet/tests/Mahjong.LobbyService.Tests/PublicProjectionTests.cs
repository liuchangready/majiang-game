using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class PublicProjectionTests
    {
        [Fact]
        public async Task CatalogMergesOnlyPublicProjectionFields()
        {
            var projection = new FakeProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("projection-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            projection.Items[room.Room.RoomId] = new PublicRoomProjection(
                room.Room.RoomId,
                "PLAYING",
                3,
                2,
                7,
                1,
                fixture.Clock.UtcNow.AddSeconds(1));

            RoomCatalogView page = LobbyJson.Deserialize<RoomCatalogView>(
                (await fixture.Application.ListRoomsAsync(null, 20, default)).Json);
            RoomSummaryView summary = Assert.Single(page.Items);
            Assert.Equal("IN_GAME", summary.State);
            Assert.Equal(3, summary.SeatsTaken);
            Assert.Equal(1, summary.SeatsAvailable);
            Assert.Equal(2, summary.SpectatorCount);
            Assert.False(summary.ProjectionStale);
        }

        [Fact]
        public async Task MissingProjectionFallsBackToLobbyStateAndMarksStale()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("projection-missing");
            CreateRoomView room = await fixture.CreateRoomAsync(host);

            RoomSummaryView summary = LobbyJson.Deserialize<RoomSummaryView>(
                (await fixture.Application.GetRoomAsync(room.Room.RoomId, default)).Json);
            Assert.True(summary.ProjectionStale);
            Assert.Equal("OPEN", summary.State);
            Assert.Equal(1, summary.SeatsTaken);
        }

        [Fact]
        public async Task ProjectionFailureDegradesToLobbyStateWithoutLeakingPrivateData()
        {
            var projection = new FakeProjectionReader { Failure = new InvalidOperationException("table db down") };
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("projection-failure");
            await fixture.CreateRoomAsync(host);

            LobbyResponse response = await fixture.Application.ListRoomsAsync(null, 20, default);
            Assert.Equal(200, response.StatusCode);
            Assert.DoesNotContain("table_event", response.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hand", response.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seed", response.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cheat", response.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ownerEpoch", response.Json, StringComparison.OrdinalIgnoreCase);
            RoomCatalogView page = LobbyJson.Deserialize<RoomCatalogView>(response.Json);
            Assert.True(Assert.Single(page.Items).ProjectionStale);
        }

        [Fact]
        public async Task ClosedProjectionRemovesRoomFromDirectory()
        {
            var projection = new FakeProjectionReader();
            await using LobbyFixture fixture = LobbyFixture.Create(projections: projection);
            GuestSessionView host = await fixture.CreateGuestAsync("projection-closed");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            projection.Items[room.Room.RoomId] = new PublicRoomProjection(
                room.Room.RoomId,
                "CLOSED",
                0,
                0,
                9,
                1,
                fixture.Clock.UtcNow.AddSeconds(1));

            RoomCatalogView page = LobbyJson.Deserialize<RoomCatalogView>(
                (await fixture.Application.ListRoomsAsync(null, 20, default)).Json);
            Assert.Empty(page.Items);
        }

        [Fact]
        public void PublicProjectionTypeHasNoPrivateDomainFields()
        {
            string[] expected =
            {
                "RoomId", "Lifecycle", "SeatsOccupied", "SpectatorCount", "PublicVersion",
                "RulesetVersion", "UpdatedAt"
            };
            string[] actual = typeof(PublicRoomProjection)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(), actual);
        }

        private sealed class FakeProjectionReader : IRoomProjectionReader
        {
            public Dictionary<string, PublicRoomProjection> Items { get; } =
                new Dictionary<string, PublicRoomProjection>(StringComparer.Ordinal);

            public Exception? Failure { get; set; }

            public Task<PublicRoomProjectionBatch> ReadAsync(
                IReadOnlyList<string> roomIds,
                CancellationToken cancellationToken)
            {
                if (Failure != null)
                {
                    throw Failure;
                }

                var items = roomIds
                    .Where(Items.ContainsKey)
                    .Select(id => Items[id])
                    .ToList();
                return Task.FromResult(new PublicRoomProjectionBatch(items));
            }
        }
    }
}
