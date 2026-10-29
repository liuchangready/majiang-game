using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    public static class B1Flows
    {
        public static async Task<GuestState> CreateGuestAsync(
            B1LobbyClient lobby,
            string displayName,
            CancellationToken cancellationToken)
        {
            string deviceId = "dev-" + Guid.NewGuid().ToString("N");
            string bootstrap = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string key = "auth-" + Guid.NewGuid().ToString("N");
            B1LobbyClient.LobbyHttpResult response = await lobby.PostAsync(
                "/v1/auth/session",
                new { deviceId, displayName, authMode = "GUEST", bootstrapSecret = bootstrap },
                null,
                key,
                cancellationToken).ConfigureAwait(false);
            RequireStatus(response, 201, "guest session");
            JsonElement json = response.Json;
            var guest = new GuestState
            {
                AccountId = RequireString(json, "accountId"),
                DisplayName = displayName,
                SessionToken = RequireString(json, "sessionToken"),
                DeviceId = deviceId,
                BootstrapSecret = bootstrap
            };
            if (guest.SessionToken.Length < 32)
            {
                throw new InvalidOperationException("The lobby session token is unexpectedly short.");
            }

            return guest;
        }

        public static async Task<(string RoomId, string AssignmentId, int Seat)> CreateRoomAsync(
            B1LobbyClient lobby,
            GuestState host,
            string title,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            B1LobbyClient.LobbyHttpResult response = await lobby.PostAsync(
                "/v1/rooms",
                new { title, maxPlayers = 4, visibility = "PUBLIC", allowSpectators = true },
                host.SessionToken,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            RequireStatus(response, 201, "create room");
            JsonElement json = response.Json;
            string roomId = RequireString(json.GetProperty("room"), "roomId");
            JsonElement assignment = json.GetProperty("assignment");
            string assignmentId = RequireString(assignment, "assignmentId");
            int seat = RequireInt(assignment, "seatId");
            host.RoomId = roomId;
            host.AssignmentId = assignmentId;
            host.Seat = seat;
            host.Role = "PLAYER";
            return (roomId, assignmentId, seat);
        }

        public static async Task<(string AssignmentId, int Seat)> JoinRoomAsync(
            B1LobbyClient lobby,
            GuestState guest,
            string roomId,
            int preferSeat,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            B1LobbyClient.LobbyHttpResult response = await lobby.PostAsync(
                "/v1/rooms/" + Uri.EscapeDataString(roomId) + "/join",
                new { preferSeat },
                guest.SessionToken,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            RequireStatus(response, 201, "join room");
            JsonElement assignment = response.Json.GetProperty("assignment");
            string assignmentId = RequireString(assignment, "assignmentId");
            int seat = RequireInt(assignment, "seatId");
            guest.RoomId = roomId;
            guest.AssignmentId = assignmentId;
            guest.Seat = seat;
            guest.Role = "PLAYER";
            return (assignmentId, seat);
        }

        public static async Task<string> SpectateAsync(
            B1LobbyClient lobby,
            GuestState guest,
            string roomId,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            B1LobbyClient.LobbyHttpResult response = await lobby.PostAsync(
                "/v1/rooms/" + Uri.EscapeDataString(roomId) + "/spectate",
                null,
                guest.SessionToken,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            RequireStatus(response, 201, "spectate room");
            JsonElement assignment = response.Json.GetProperty("assignment");
            string assignmentId = RequireString(assignment, "assignmentId");
            guest.RoomId = roomId;
            guest.AssignmentId = assignmentId;
            guest.Seat = -1;
            guest.Role = "SPECTATOR";
            return assignmentId;
        }

        public static async Task<IssuedTicket> IssueTicketAsync(
            B1LobbyClient lobby,
            GuestState guest,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(guest.AssignmentId))
            {
                throw new InvalidOperationException("The guest has no assignment before ticket issuance.");
            }

            B1LobbyClient.LobbyHttpResult response = await lobby.PostAsync(
                "/v1/table-tickets",
                new { assignmentId = guest.AssignmentId },
                guest.SessionToken,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            RequireStatus(response, 201, "table ticket");
            JsonElement json = response.Json;
            string ticket = RequireString(json, "ticket");
            if (!ticket.StartsWith("eyJ", StringComparison.Ordinal) || ticket.Length > 4096)
            {
                throw new InvalidOperationException("The lobby issued an unexpected compact ticket shape.");
            }

            return new IssuedTicket(
                ticket,
                RequireString(json, "jti"),
                RequireString(json, "kid"),
                RequireString(json, "roomId"),
                RequireString(json, "assignmentId"),
                RequireString(json, "role"),
                json.TryGetProperty("seatId", out JsonElement seatElement) && seatElement.ValueKind != JsonValueKind.Null
                    ? seatElement.GetInt32()
                    : -1,
                RequireString(json, "expiresAt"));
        }

        public static void RequireStatus(B1LobbyClient.LobbyHttpResult response, int expected, string what)
        {
            if (response.StatusCode != expected)
            {
                throw new InvalidOperationException(
                    what + " expected HTTP " + expected + " but observed " + response.StatusCode +
                    ": " + Program.Redact(response.Body));
            }
        }

        public static string RequireString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(value.GetString()))
            {
                throw new InvalidOperationException("Missing JSON string field " + name + ".");
            }

            return value.GetString()!;
        }

        public static int RequireInt(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidOperationException("Missing JSON number field " + name + ".");
            }

            return value.GetInt32();
        }

        public static string? OptionalString(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            return null;
        }

        public sealed class IssuedTicket
        {
            public IssuedTicket(
                string ticket,
                string jti,
                string kid,
                string roomId,
                string assignmentId,
                string role,
                int seatId,
                string expiresAt)
            {
                Ticket = ticket;
                Jti = jti;
                Kid = kid;
                RoomId = roomId;
                AssignmentId = assignmentId;
                Role = role;
                SeatId = seatId;
                ExpiresAt = expiresAt;
            }

            public string Ticket { get; }

            public string Jti { get; }

            public string Kid { get; }

            public string RoomId { get; }

            public string AssignmentId { get; }

            public string Role { get; }

            public int SeatId { get; }

            public string ExpiresAt { get; }
        }
    }
}
