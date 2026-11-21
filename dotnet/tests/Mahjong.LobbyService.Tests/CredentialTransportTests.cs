using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class CredentialTransportTests
    {
        [Fact]
        public async Task ProjectionAndRetirementRedirectResponsesFailClosedWithoutFollowingLocation()
        {
            var handler = new RedirectHandler();
            await using var reader = new HttpRoomProjectionReader(
                "https://table.internal:5443",
                serviceKey: new string('k', 48),
                handler: handler);

            await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync(new[] { "room-a" }, default));
            Assert.Equal(1, handler.RequestCount);

            await Assert.ThrowsAsync<HttpRequestException>(() => reader.RetireFinishedRoomAsync("room-a", default));
            Assert.Equal(2, handler.RequestCount);
        }

        private sealed class RedirectHandler : HttpMessageHandler
        {
            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestCount++;
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    RequestMessage = request,
                    Headers = { Location = new Uri("https://attacker.invalid/collect") }
                };
                return Task.FromResult(response);
            }
        }
    }
}
