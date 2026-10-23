using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class HttpRouteTests
    {
        [Fact]
        public async Task HttpDeviceReuseWithoutBootstrapCannotObtainVictimSessionOrTicket()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();
            LobbyHttp.UseErrorHandling(app);
            LobbyHttp.Map(app, fixture.Application, fixture.Signer, fixture.Clock);

            await app.StartAsync();
            try
            {
                var server = app.Services.GetRequiredService<IServer>();
                string? address = server.Features
                    .Get<IServerAddressesFeature>()!
                    .Addresses
                    .FirstOrDefault();
                Assert.NotNull(address);
                using var client = new HttpClient { BaseAddress = new Uri(address!) };

                GuestSessionView victim = await CreateSessionAsync(
                    client,
                    "http-known-device",
                    "victim-http-bootstrap-0123456789abcdef012345",
                    "Victim",
                    "http-victim-session");
                CreateRoomView room = await CreateRoomAsync(client, victim, "HTTP Victim Room");

                using HttpResponseMessage noBootstrap = await PostJsonAsync(
                    client,
                    "/v1/auth/session",
                    "http-attacker-no-bootstrap",
                    null,
                    new { deviceId = "http-known-device", displayName = "Attacker" });
                Assert.Equal(HttpStatusCode.BadRequest, noBootstrap.StatusCode);

                GuestSessionView attacker = await CreateSessionAsync(
                    client,
                    "http-known-device",
                    "attacker-http-bootstrap-0123456789abcdef0123",
                    "Attacker",
                    "http-attacker-session");
                Assert.NotEqual(victim.AccountId, attacker.AccountId);

                using HttpResponseMessage ticket = await PostJsonAsync(
                    client,
                    "/v1/table-tickets",
                    "http-attacker-ticket",
                    attacker.SessionToken,
                    new { assignmentId = room.Assignment.AssignmentId });
                Assert.Equal(HttpStatusCode.NotFound, ticket.StatusCode);
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }

        private static async Task<GuestSessionView> CreateSessionAsync(
            HttpClient client,
            string deviceId,
            string bootstrapSecret,
            string displayName,
            string requestId)
        {
            using HttpResponseMessage response = await PostJsonAsync(
                client,
                "/v1/auth/session",
                requestId,
                null,
                new
                {
                    deviceId,
                    bootstrapSecret,
                    displayName
                });
            string json = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return LobbyJson.Deserialize<GuestSessionView>(json);
        }

        private static async Task<CreateRoomView> CreateRoomAsync(
            HttpClient client,
            GuestSessionView guest,
            string title)
        {
            using HttpResponseMessage response = await PostJsonAsync(
                client,
                "/v1/rooms",
                "http-create-room",
                guest.SessionToken,
                new
                {
                    title,
                    maxPlayers = 4,
                    visibility = "PUBLIC",
                    allowSpectators = true
                });
            string json = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return LobbyJson.Deserialize<CreateRoomView>(json);
        }

        private static async Task<HttpResponseMessage> PostJsonAsync(
            HttpClient client,
            string path,
            string requestId,
            string? bearer,
            object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(LobbyJson.Serialize(body), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", requestId);
            if (bearer != null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            }

            return await client.SendAsync(request);
        }
    }
}
