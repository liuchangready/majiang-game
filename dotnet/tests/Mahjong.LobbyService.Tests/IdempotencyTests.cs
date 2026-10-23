using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class IdempotencyTests
    {
        [Fact]
        public async Task CreateRoomReplayReturnsTheOriginalRoomWithoutADuplicate()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("idem-host");
            string requestId = LobbyFixture.NewRequestId();

            CreateRoomView first = await fixture.CreateRoomAsync(host, "Idempotent", requestId: requestId);
            CreateRoomView replay = await fixture.CreateRoomAsync(host, "Idempotent", requestId: requestId);

            Assert.Equal(first.Room.RoomId, replay.Room.RoomId);
            Assert.Equal(first.Assignment.AssignmentId, replay.Assignment.AssignmentId);
            RoomCatalogView catalog = LobbyJson.Deserialize<RoomCatalogView>(
                (await fixture.Application.ListRoomsAsync(null, 20, default)).Json);
            Assert.Single(catalog.Items);
        }

        [Fact]
        public async Task SameIdempotencyKeyWithDifferentPayloadIsAConflict()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("idem-conflict");
            string requestId = LobbyFixture.NewRequestId();
            await fixture.CreateRoomAsync(host, "Original", requestId: requestId);

            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.CreateRoomAsync(host, "Changed", requestId: requestId));
            Assert.Equal(409, error.StatusCode);
            Assert.Equal("REQUEST_ID_CONFLICT", error.Code);
        }

        [Fact]
        public async Task ConcurrentSameKeyCreateRoomHasOneSideEffect()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("idem-race");
            LobbySessionInfo session = await fixture.AuthenticateAsync(host);
            string requestId = LobbyFixture.NewRequestId();
            var command = new CreateRoomCommand("Race", 4, LobbyRoomVisibility.Public, true);

            var tasks = new List<Task<LobbyResponse>>();
            for (int i = 0; i < 8; i++)
            {
                tasks.Add(fixture.Application.CreateRoomAsync(
                    session.Account,
                    command,
                    requestId,
                    default));
            }

            LobbyResponse[] responses = await Task.WhenAll(tasks);
            CreateRoomView[] views = responses
                .Select(response => LobbyJson.Deserialize<CreateRoomView>(response.Json))
                .ToArray();
            Assert.Single(views.Select(view => view.Room.RoomId).Distinct(StringComparer.Ordinal));
            Assert.Equal(1, responses.Count(response => !response.Replayed));

            RoomCatalogView catalog = LobbyJson.Deserialize<RoomCatalogView>(
                (await fixture.Application.ListRoomsAsync(null, 20, default)).Json);
            Assert.Single(catalog.Items);
        }

        [Fact]
        public async Task JoinReplayReturnsTheOriginalAssignment()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("idem-join-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView guest = await fixture.CreateGuestAsync("idem-join-guest");
            string requestId = LobbyFixture.NewRequestId();

            JoinRoomView first = await fixture.JoinAsync(guest, room.Room.RoomId, requestId: requestId);
            JoinRoomView replay = await fixture.JoinAsync(guest, room.Room.RoomId, requestId: requestId);
            Assert.Equal(first.Assignment.AssignmentId, replay.Assignment.AssignmentId);
            Assert.Equal(first.Assignment.SeatId, replay.Assignment.SeatId);
        }
    }
}
