using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class RoomCatalogTests
    {
        [Fact]
        public async Task HostCreatesRoomAndPublicCatalogShowsOnlyPublicProjectionFields()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("host-1", "Host");
            CreateRoomView created = await fixture.CreateRoomAsync(host, "Lobby Room");

            Assert.Equal(4, created.Room.MaxPlayers);
            Assert.Equal(1, created.Room.SeatsTaken);
            Assert.Equal("OPEN", created.Room.State);
            Assert.Equal("PLAYER", created.Assignment.Role);
            Assert.Equal(0, created.Assignment.SeatId);

            LobbyResponse catalog = await fixture.Application.ListRoomsAsync(null, 20, default);
            Assert.Equal(200, catalog.StatusCode);
            Assert.DoesNotContain("ownerEpoch", catalog.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"hand\"", catalog.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"seed\"", catalog.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cheat", catalog.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("assignmentId", catalog.Json, StringComparison.OrdinalIgnoreCase);

            RoomCatalogView page = LobbyJson.Deserialize<RoomCatalogView>(catalog.Json);
            Assert.Single(page.Items);
            Assert.Equal(created.Room.RoomId, page.Items[0].RoomId);
        }

        [Fact]
        public async Task PrivateRoomIsHiddenFromCatalogButReadableById()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("host-private");
            CreateRoomView created = await fixture.CreateRoomAsync(
                host,
                "Private",
                LobbyRoomVisibility.Private);

            RoomCatalogView page = LobbyJson.Deserialize<RoomCatalogView>(
                (await fixture.Application.ListRoomsAsync(null, 20, default)).Json);
            Assert.Empty(page.Items);

            LobbyResponse detail = await fixture.Application.GetRoomAsync(created.Room.RoomId, default);
            Assert.Equal(200, detail.StatusCode);
            RoomSummaryView summary = LobbyJson.Deserialize<RoomSummaryView>(detail.Json);
            Assert.Equal("PRIVATE", summary.Visibility);
        }

        [Fact]
        public async Task ConcurrentPreferredSeatJoinAllowsExactlyOneWinner()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("host-seat", "Host");
            CreateRoomView created = await fixture.CreateRoomAsync(host);
            GuestSessionView first = await fixture.CreateGuestAsync("guest-seat-1");
            GuestSessionView second = await fixture.CreateGuestAsync("guest-seat-2");

            Task<JoinRoomView> one = fixture.JoinAsync(first, created.Room.RoomId, preferSeat: 1);
            Task<JoinRoomView> two = fixture.JoinAsync(second, created.Room.RoomId, preferSeat: 1);
            var outcomes = new List<object>();
            try
            {
                outcomes.Add(await one);
            }
            catch (LobbyException ex)
            {
                outcomes.Add(ex);
            }

            try
            {
                outcomes.Add(await two);
            }
            catch (LobbyException ex)
            {
                outcomes.Add(ex);
            }

            JoinRoomView[] joins = outcomes.OfType<JoinRoomView>().ToArray();
            Assert.Equal(2, joins.Length);
            Assert.Equal(2, joins.Select(view => view.Assignment.SeatId).Distinct().Count());
            Assert.All(joins, view => Assert.InRange(view.Assignment.SeatId!.Value, 1, 2));
        }

        [Fact]
        public async Task ConcurrentDoubleJoinForOneAccountCreatesOneSeat()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("host-double-join");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView guest = await fixture.CreateGuestAsync("guest-double-join");

            Task<JoinRoomView> first = fixture.JoinAsync(guest, room.Room.RoomId);
            Task<JoinRoomView> second = fixture.JoinAsync(guest, room.Room.RoomId);
            var outcomes = new List<object>();
            try
            {
                outcomes.Add(await first);
            }
            catch (LobbyException ex)
            {
                outcomes.Add(ex);
            }

            try
            {
                outcomes.Add(await second);
            }
            catch (LobbyException ex)
            {
                outcomes.Add(ex);
            }

            JoinRoomView one = Assert.Single(outcomes.OfType<JoinRoomView>());
            LobbyException conflict = Assert.Single(outcomes.OfType<LobbyException>());
            Assert.Equal("ALREADY_ASSIGNED", conflict.Code);
            Assert.Equal(1, one.Assignment.SeatId);

            RoomSummaryView summary = LobbyJson.Deserialize<RoomSummaryView>(
                (await fixture.Application.GetRoomAsync(room.Room.RoomId, default)).Json);
            Assert.Equal(2, summary.SeatsTaken);
        }

        [Fact]
        public async Task FourthPlayerJoinFillsRoomAndFifthIsRejected()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("host-full");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            for (int i = 0; i < 3; i++)
            {
                GuestSessionView guest = await fixture.CreateGuestAsync("guest-full-" + i);
                await fixture.JoinAsync(guest, room.Room.RoomId);
            }

            GuestSessionView fifth = await fixture.CreateGuestAsync("guest-full-fifth");
            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.JoinAsync(fifth, room.Room.RoomId));
            Assert.Equal("ROOM_FULL", error.Code);
        }

        [Fact]
        public async Task PlayerAndSpectatorRolesCannotBeMixedForOneAccountInOneRoom()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("host-role");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView guest = await fixture.CreateGuestAsync("guest-role");
            await fixture.JoinAsync(guest, room.Room.RoomId);

            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.SpectateAsync(guest, room.Room.RoomId));
            Assert.Equal("ALREADY_ASSIGNED", error.Code);

            GuestSessionView spectator = await fixture.CreateGuestAsync("spectator-role");
            JoinRoomView spectated = await fixture.SpectateAsync(spectator, room.Room.RoomId);
            Assert.Equal("SPECTATOR", spectated.Assignment.Role);
            Assert.Null(spectated.Assignment.SeatId);
        }

        [Fact]
        public async Task CatalogPaginationIsStableAndDoesNotDuplicateRooms()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            for (int i = 0; i < 5; i++)
            {
                GuestSessionView host = await fixture.CreateGuestAsync("host-pages-" + i);
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
                await fixture.CreateRoomAsync(host, "Room " + i);
            }

            var seen = new List<string>();
            string? cursor = null;
            for (int page = 0; page < 10; page++)
            {
                LobbyResponse response = await fixture.Application.ListRoomsAsync(cursor, 2, default);
                RoomCatalogView view = LobbyJson.Deserialize<RoomCatalogView>(response.Json);
                seen.AddRange(view.Items.Select(item => item.RoomId));
                cursor = view.NextCursor;
                if (cursor == null)
                {
                    break;
                }
            }

            Assert.Equal(5, seen.Count);
            Assert.Equal(5, seen.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task CreateRoomRejectsNonFourPlayerRuleset()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("host-three");
            LobbySessionInfo session = await fixture.AuthenticateAsync(host);

            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.CreateRoomAsync(
                    session.Account,
                    new CreateRoomCommand("Three", 3, LobbyRoomVisibility.Public, true),
                    LobbyFixture.NewRequestId(),
                    default));
            Assert.Equal("UNSUPPORTED_SEAT_COUNT", error.Code);
        }
    }
}
