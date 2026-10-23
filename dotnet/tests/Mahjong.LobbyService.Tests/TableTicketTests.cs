using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class TableTicketTests
    {
        [Fact]
        public async Task TicketContainsAssignmentIdAndNeverATableOwnerEpoch()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("ticket-claims-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            TableTicketView ticket = await fixture.IssueTicketAsync(host, room.Assignment.AssignmentId);

            using JsonDocument document = JsonDocument.Parse(DecodePayload(ticket.Ticket));
            JsonElement payload = document.RootElement;
            Assert.Equal(room.Assignment.AssignmentId, payload.GetProperty("assignment_id").GetString());
            Assert.Equal(room.Room.RoomId, payload.GetProperty("room_id").GetString());
            Assert.Equal("mahjong-table", payload.GetProperty("aud").GetString());
            Assert.Equal("mahjong-lobby", payload.GetProperty("iss").GetString());
            Assert.Equal("PLAYER", payload.GetProperty("role").GetString());
            Assert.Equal(0, payload.GetProperty("seat_id").GetInt32());
            Assert.Equal(1, payload.GetProperty("protocol_major").GetInt32());
            Assert.False(payload.TryGetProperty("owner_epoch", out _));
            Assert.False(payload.TryGetProperty("ownerEpoch", out _));
            Assert.DoesNotContain("owner_epoch", Encoding.UTF8.GetString(DecodePayload(ticket.Ticket)), StringComparison.OrdinalIgnoreCase);
            Assert.True(payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64() <= 60);
        }

        [Fact]
        public async Task TicketIsConsumedOnceAndSeatIsConfirmed()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("ticket-once-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            TableTicketView ticket = await fixture.IssueTicketAsync(host, room.Assignment.AssignmentId);

            TicketConsumptionResult consumed = await fixture.ConsumeAsync(ticket, host.AccountId);
            Assert.Equal(room.Assignment.AssignmentId, consumed.AssignmentId);
            Assert.Equal(0, consumed.SeatId);

            LobbyException duplicate = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.ConsumeAsync(ticket, host.AccountId));
            Assert.Equal(409, duplicate.StatusCode);
            Assert.Equal("TICKET_ALREADY_CONSUMED", duplicate.Code);
        }

        [Fact]
        public async Task ReconnectUsesAFreshTicketAndTheOldTicketStaysConsumed()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("ticket-reconnect-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            TableTicketView first = await fixture.IssueTicketAsync(host, room.Assignment.AssignmentId);
            await fixture.ConsumeAsync(first, host.AccountId);

            TableTicketView second = await fixture.IssueTicketAsync(host, room.Assignment.AssignmentId);
            Assert.NotEqual(first.Jti, second.Jti);
            TicketConsumptionResult reconnected = await fixture.ConsumeAsync(second, host.AccountId);
            Assert.Equal(room.Assignment.AssignmentId, reconnected.AssignmentId);

            LobbyException duplicate = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.ConsumeAsync(first, host.AccountId));
            Assert.Equal("TICKET_ALREADY_CONSUMED", duplicate.Code);
        }

        [Fact]
        public async Task ConcurrentDoubleConsumptionHasExactlyOneWinner()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("ticket-race-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            TableTicketView ticket = await fixture.IssueTicketAsync(host, room.Assignment.AssignmentId);

            Task<TicketConsumptionResult> one = fixture.ConsumeAsync(ticket, host.AccountId);
            Task<TicketConsumptionResult> two = fixture.ConsumeAsync(ticket, host.AccountId);
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

            Assert.Single(outcomes.OfType<TicketConsumptionResult>());
            LobbyException rejected = Assert.Single(outcomes.OfType<LobbyException>());
            Assert.Equal("TICKET_ALREADY_CONSUMED", rejected.Code);
        }

        [Fact]
        public async Task SpectatorTicketHasNoSeatAndConsumesAsSpectator()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("ticket-spectator-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView spectator = await fixture.CreateGuestAsync("ticket-spectator");
            JoinRoomView assignment = await fixture.SpectateAsync(spectator, room.Room.RoomId);

            TableTicketView ticket = await fixture.IssueTicketAsync(spectator, assignment.Assignment.AssignmentId);
            using JsonDocument document = JsonDocument.Parse(DecodePayload(ticket.Ticket));
            Assert.False(document.RootElement.TryGetProperty("seat_id", out _));
            Assert.Equal("SPECTATOR", document.RootElement.GetProperty("role").GetString());

            TicketConsumptionResult consumed = await fixture.ConsumeAsync(ticket, spectator.AccountId);
            Assert.Null(consumed.SeatId);
            Assert.Equal(LobbyRole.Spectator, consumed.Role);
        }

        [Fact]
        public async Task StrictAlgorithmAudienceRoleSeatAndClaimChecksRejectForgedOrWrongTickets()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("ticket-strict-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            TableTicketView valid = await fixture.IssueTicketAsync(host, room.Assignment.AssignmentId);
            string payload = Encoding.UTF8.GetString(DecodePayload(valid.Ticket));
            string kid = fixture.KeyRing.ActiveKid(fixture.Clock.UtcNow);

            string noneHeader = "{\"alg\":\"none\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}";
            await AssertTicketCodeAsync(
                "TICKET_ALGORITHM_REJECTED",
                fixture,
                SignRaw(fixture, noneHeader, payload),
                valid,
                host);

            JsonObject role = JsonNode.Parse(payload)!.AsObject();
            role["role"] = "LOBBY";
            await AssertTicketCodeAsync(
                "TICKET_ROLE_INVALID",
                fixture,
                SignRaw(fixture, Header(kid), role.ToJsonString()),
                valid,
                host);

            JsonObject seat = JsonNode.Parse(payload)!.AsObject();
            seat["seat_id"] = 5;
            await AssertTicketCodeAsync(
                "TICKET_SEAT_INVALID",
                fixture,
                SignRaw(fixture, Header(kid), seat.ToJsonString()),
                valid,
                host);

            JsonObject extra = JsonNode.Parse(payload)!.AsObject();
            extra["owner_epoch"] = 7;
            await AssertTicketCodeAsync(
                "TICKET_UNKNOWN_CLAIM",
                fixture,
                SignRaw(fixture, Header(kid), extra.ToJsonString()),
                valid,
                host);

            JsonObject wrongAudience = JsonNode.Parse(payload)!.AsObject();
            wrongAudience["aud"] = "other-table";
            await AssertTicketCodeAsync(
                "TICKET_AUDIENCE_MISMATCH",
                fixture,
                SignRaw(fixture, Header(kid), wrongAudience.ToJsonString()),
                valid,
                host);

            JsonObject wrongIssuer = JsonNode.Parse(payload)!.AsObject();
            wrongIssuer["iss"] = "other-lobby";
            await AssertTicketCodeAsync(
                "TICKET_ISSUER_MISMATCH",
                fixture,
                SignRaw(fixture, Header(kid), wrongIssuer.ToJsonString()),
                valid,
                host);

            string duplicateClaim = payload.Substring(0, payload.Length - 1) + ",\"role\":\"PLAYER\"}";
            await AssertTicketCodeAsync(
                "TICKET_DUPLICATE_CLAIM",
                fixture,
                SignRaw(fixture, Header(kid), duplicateClaim),
                valid,
                host);

            long nowSeconds = fixture.Clock.UtcNow.ToUnixTimeSeconds();
            JsonObject future = JsonNode.Parse(payload)!.AsObject();
            future["iat"] = nowSeconds + 60;
            future["exp"] = nowSeconds + 120;
            await AssertTicketCodeAsync(
                "TICKET_NOT_YET_VALID",
                fixture,
                SignRaw(fixture, Header(kid), future.ToJsonString()),
                valid,
                host);

            JsonObject longTtl = JsonNode.Parse(payload)!.AsObject();
            longTtl["exp"] = nowSeconds + 400;
            await AssertTicketCodeAsync(
                "TICKET_TTL_INVALID",
                fixture,
                SignRaw(fixture, Header(kid), longTtl.ToJsonString()),
                valid,
                host);

            string[] validParts = valid.Ticket.Split('.');
            byte[] signatureBytes = LobbyBase64Url.Decode(validParts[2]);
            signatureBytes[0] ^= 0x01;
            string tampered = validParts[0] + "." + validParts[1] + "." +
                              LobbyBase64Url.Encode(signatureBytes);
            LobbyException signature = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.ConsumeTableTicketAsync(
                    tampered,
                    new TableTicketExpectation(
                        room.Room.RoomId,
                        room.Assignment.AssignmentId,
                        LobbyRole.Player,
                        0,
                        host.AccountId),
                    default));
            Assert.Equal("TICKET_SIGNATURE_INVALID", signature.Code);

            LobbyException wrongRoom = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.ConsumeTableTicketAsync(
                    valid.Ticket,
                    new TableTicketExpectation(
                        "room_other",
                        room.Assignment.AssignmentId,
                        LobbyRole.Player,
                        0,
                        host.AccountId),
                    default));
            Assert.Equal("TICKET_CLAIM_MISMATCH", wrongRoom.Code);

            LobbyException wrongAccount = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.ConsumeTableTicketAsync(
                    valid.Ticket,
                    new TableTicketExpectation(
                        room.Room.RoomId,
                        room.Assignment.AssignmentId,
                        LobbyRole.Player,
                        0,
                        "guest_other"),
                    default));
            Assert.Equal("TICKET_CLAIM_MISMATCH", wrongAccount.Code);

            fixture.Clock.Advance(TimeSpan.FromSeconds(61));
            LobbyException expired = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.ConsumeAsync(valid, host.AccountId));
            Assert.Equal("TICKET_EXPIRED", expired.Code);
        }

        [Fact]
        public async Task KeyRotationKeepsOverlapThenRetirementInvalidatesOldTickets()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("ticket-rotate-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            TableTicketView oldTicket = await fixture.IssueTicketAsync(host, room.Assignment.AssignmentId);
            string oldKid = oldTicket.Kid;

            fixture.Signer.Rotate(fixture.Clock.UtcNow);
            TableTicketView newTicket = await fixture.IssueTicketAsync(host, room.Assignment.AssignmentId);
            Assert.NotEqual(oldKid, newTicket.Kid);

            JwksDocument overlap = fixture.Signer.GetJwks(fixture.Clock.UtcNow);
            Assert.Contains(overlap.Keys, key => key.KeyId == oldKid);
            Assert.Contains(overlap.Keys, key => key.KeyId == newTicket.Kid);
            Assert.NotNull(fixture.Signer.Validate(
                oldTicket.Ticket,
                new TableTicketExpectation(
                    room.Room.RoomId,
                    room.Assignment.AssignmentId,
                    LobbyRole.Player,
                    0,
                    host.AccountId),
                fixture.Clock.UtcNow));

            fixture.KeyRing.Retire(oldKid, fixture.Clock.UtcNow);
            Assert.DoesNotContain(
                fixture.Signer.GetJwks(fixture.Clock.UtcNow).Keys,
                key => key.KeyId == oldKid);
            TicketValidationException retired = Assert.Throws<TicketValidationException>(
                () => fixture.Signer.Validate(
                    oldTicket.Ticket,
                    new TableTicketExpectation(
                        room.Room.RoomId,
                        room.Assignment.AssignmentId,
                        LobbyRole.Player,
                        0,
                        host.AccountId),
                    fixture.Clock.UtcNow));
            Assert.Equal("TICKET_KEY_UNKNOWN", retired.Code);
            Assert.NotNull(fixture.Signer.Validate(
                newTicket.Ticket,
                new TableTicketExpectation(
                    room.Room.RoomId,
                    room.Assignment.AssignmentId,
                    LobbyRole.Player,
                    0,
                    host.AccountId),
                fixture.Clock.UtcNow));
        }

        [Fact]
        public async Task JwksContainsOnlyPublicP256SigningKeys()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            string json = LobbyJson.Serialize(fixture.Signer.GetJwks(fixture.Clock.UtcNow));
            Assert.DoesNotContain("\"d\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"p\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"q\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"dp\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"dq\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"qi\"", json, StringComparison.Ordinal);
            Assert.Contains("\"kty\":\"EC\"", json, StringComparison.Ordinal);
            Assert.Contains("\"crv\":\"P-256\"", json, StringComparison.Ordinal);
            Assert.Contains("\"alg\":\"ES256\"", json, StringComparison.Ordinal);
        }

        [Fact]
        public async Task OneAccountCannotIssueATicketForAnotherAccountsAssignment()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView host = await fixture.CreateGuestAsync("ticket-owner-host");
            CreateRoomView room = await fixture.CreateRoomAsync(host);
            GuestSessionView other = await fixture.CreateGuestAsync("ticket-owner-other");

            LobbySessionInfo otherSession = await fixture.AuthenticateAsync(other);
            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.IssueTableTicketAsync(
                    otherSession.Account,
                    new IssueTableTicketCommand(room.Assignment.AssignmentId),
                    LobbyFixture.NewRequestId(),
                    default));
            Assert.Equal(404, error.StatusCode);
            Assert.Equal("ASSIGNMENT_NOT_FOUND", error.Code);
        }

        private static async Task AssertTicketCodeAsync(
            string expectedCode,
            LobbyFixture fixture,
            string ticket,
            TableTicketView reference,
            GuestSessionView guest)
        {
            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.ConsumeTableTicketAsync(
                    ticket,
                    new TableTicketExpectation(
                        reference.RoomId,
                        reference.AssignmentId,
                        reference.Role == "PLAYER" ? LobbyRole.Player : LobbyRole.Spectator,
                        reference.SeatId,
                        guest.AccountId),
                    default));
            Assert.Equal(expectedCode, error.Code);
        }

        private static string Header(string kid)
        {
            return "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}";
        }

        private static string SignRaw(LobbyFixture fixture, string headerJson, string payloadJson)
        {
            ECParameters? parameters = fixture.KeyRing.FindSigningParameters(
                fixture.Clock.UtcNow,
                out string kid);
            Assert.NotNull(parameters);
            string header = LobbyBase64Url.Encode(Encoding.UTF8.GetBytes(headerJson));
            string payload = LobbyBase64Url.Encode(Encoding.UTF8.GetBytes(payloadJson));
            byte[] input = Encoding.ASCII.GetBytes(header + "." + payload);
            using ECDsa ecdsa = ECDsa.Create(parameters!.Value);
            byte[] signature = ecdsa.SignData(
                input,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return header + "." + payload + "." + LobbyBase64Url.Encode(signature);
        }

        private static byte[] DecodePayload(string ticket)
        {
            string[] parts = ticket.Split('.');
            return LobbyBase64Url.Decode(parts[1]);
        }
    }
}
