using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class MatchmakingTests
    {
        [Fact]
        public async Task FourQueuedGuestsGetUniqueReservedSeatsInOneRoom()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            var guests = new List<GuestSessionView>();
            for (int i = 0; i < 4; i++)
            {
                guests.Add(await fixture.CreateGuestAsync("match-seat-" + i));
            }

            var tickets = new List<MatchTicketView>();
            foreach (GuestSessionView guest in guests)
            {
                tickets.Add(await fixture.CreateMatchAsync(guest, region: "region-a"));
            }

            var views = new List<MatchTicketView>();
            foreach (GuestSessionView guest in guests)
            {
                views.Add(await fixture.GetMatchAsync(guest, tickets[guests.IndexOf(guest)].MatchTicketId));
            }

            Assert.All(views, view => Assert.Equal("RESERVED", view.Status));
            Assert.Single(views.Select(view => view.RoomId).Distinct(StringComparer.Ordinal));
            Assert.Equal(4, views.Select(view => view.AssignmentId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(4, views.Select(view => view.AssignmentId).Count());

            LobbyResponse catalog = await fixture.Application.ListRoomsAsync(null, 20, default);
            RoomCatalogView page = LobbyJson.Deserialize<RoomCatalogView>(catalog.Json);
            RoomSummaryView room = Assert.Single(page.Items);
            Assert.Equal("FULL", room.State);
            Assert.Equal(4, room.SeatsTaken);
        }

        [Fact]
        public async Task DuplicateMatchTicketForOneAccountIsRejected()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView guest = await fixture.CreateGuestAsync("match-duplicate");
            await fixture.CreateMatchAsync(guest);

            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.CreateMatchAsync(guest));
            Assert.Equal("ALREADY_QUEUED", error.Code);
        }

        [Fact]
        public async Task OnlyFourOfFiveQueueingGuestsAreMatched()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            var guests = new List<GuestSessionView>();
            var tickets = new List<MatchTicketView>();
            for (int i = 0; i < 5; i++)
            {
                GuestSessionView guest = await fixture.CreateGuestAsync("match-five-" + i);
                guests.Add(guest);
                tickets.Add(await fixture.CreateMatchAsync(guest, region: "region-five"));
            }

            int reserved = 0;
            int queued = 0;
            foreach (GuestSessionView guest in guests)
            {
                LobbySessionInfo session = await fixture.AuthenticateAsync(guest);
                LobbyResponse response = await fixture.Application.GetMatchTicketAsync(
                    session.Account,
                    tickets[guests.IndexOf(guest)].MatchTicketId,
                    default);
                MatchTicketView view = LobbyJson.Deserialize<MatchTicketView>(response.Json);
                if (view.Status == "RESERVED")
                {
                    reserved++;
                }
                else if (view.Status == "QUEUED")
                {
                    queued++;
                }
            }

            Assert.Equal(4, reserved);
            Assert.Equal(1, queued);
        }

        [Fact]
        public async Task ReservationExpiryReleasesSeatAndMarksTicketExpired()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            var guests = new List<GuestSessionView>();
            var tickets = new List<MatchTicketView>();
            for (int i = 0; i < 4; i++)
            {
                GuestSessionView guest = await fixture.CreateGuestAsync("match-expire-" + i);
                guests.Add(guest);
                tickets.Add(await fixture.CreateMatchAsync(guest, region: "region-expire"));
            }

            MatchTicketView reserved = await fixture.GetMatchAsync(guests[0], tickets[0].MatchTicketId);
            string roomId = reserved.RoomId!;
            fixture.Clock.Advance(TimeSpan.FromSeconds(16));
            await fixture.Application.ExpireOnceAsync(default);

            MatchTicketView expired = await fixture.GetMatchAsync(guests[0], tickets[0].MatchTicketId);
            Assert.Equal("EXPIRED", expired.Status);

            RoomSummaryView room = LobbyJson.Deserialize<RoomSummaryView>(
                (await fixture.Application.GetRoomAsync(roomId, default)).Json);
            Assert.Equal(0, room.SeatsTaken);
            Assert.Equal("OPEN", room.State);
        }

        [Fact]
        public async Task CancelAndExpiryRaceLeavesNoActiveAssignment()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            var guests = new List<GuestSessionView>();
            var tickets = new List<MatchTicketView>();
            for (int i = 0; i < 4; i++)
            {
                GuestSessionView guest = await fixture.CreateGuestAsync("match-race-" + i);
                guests.Add(guest);
                tickets.Add(await fixture.CreateMatchAsync(guest, region: "region-race"));
            }

            fixture.Clock.Advance(TimeSpan.FromSeconds(16));
            LobbySessionInfo session = await fixture.AuthenticateAsync(guests[0]);
            Task<LobbyResponse> cancel = fixture.Application.CancelMatchTicketAsync(
                session.Account,
                tickets[0].MatchTicketId,
                LobbyFixture.NewRequestId(),
                default);
            Task<int> expire = fixture.Application.ExpireOnceAsync(default);
            LobbyResponse cancelledResponse = await cancel;
            await expire;

            MatchTicketView cancelled = LobbyJson.Deserialize<MatchTicketView>(cancelledResponse.Json);
            Assert.Contains(cancelled.Status, new[] { "CANCELLED", "EXPIRED" });

            MatchTicketView current = LobbyJson.Deserialize<MatchTicketView>(
                (await fixture.Application.GetMatchTicketAsync(
                    session.Account,
                    tickets[0].MatchTicketId,
                    default)).Json);
            Assert.Contains(current.Status, new[] { "CANCELLED", "EXPIRED" });
            Assert.NotEqual("RESERVED", current.Status);
        }

        [Fact]
        public async Task TicketConsumptionConfirmsMatchAndPreventsCancellation()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            var guests = new List<GuestSessionView>();
            var tickets = new List<MatchTicketView>();
            for (int i = 0; i < 4; i++)
            {
                GuestSessionView guest = await fixture.CreateGuestAsync("match-confirm-" + i);
                guests.Add(guest);
                tickets.Add(await fixture.CreateMatchAsync(guest, region: "region-confirm"));
            }

            MatchTicketView reserved = await fixture.GetMatchAsync(guests[0], tickets[0].MatchTicketId);
            TableTicketView tableTicket = await fixture.IssueTicketAsync(guests[0], reserved.AssignmentId!);
            await fixture.ConsumeAsync(tableTicket, guests[0].AccountId);

            LobbySessionInfo session = await fixture.AuthenticateAsync(guests[0]);
            MatchTicketView confirmed = await fixture.GetMatchAsync(guests[0], tickets[0].MatchTicketId);
            Assert.Equal("CONFIRMED", confirmed.Status);

            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.CancelMatchTicketAsync(
                    session.Account,
                    tickets[0].MatchTicketId,
                    LobbyFixture.NewRequestId(),
                    default));
            Assert.Equal("MATCH_TICKET_CONFIRMED", error.Code);
        }
    }
}
