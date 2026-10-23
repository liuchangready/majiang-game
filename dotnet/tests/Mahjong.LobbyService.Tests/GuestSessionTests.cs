using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class GuestSessionTests
    {
        [Fact]
        public async Task GuestSessionIsExplicitAndDoesNotImpersonateAnAccountSystem()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView guest = await fixture.CreateGuestAsync("device-a", "Alice");

            Assert.True(guest.Guest);
            Assert.StartsWith("guest_", guest.AccountId, StringComparison.Ordinal);
            Assert.Equal("GUEST_ONLY", guest.Capabilities.AuthMode);
            Assert.True(guest.Capabilities.GuestOnly);
            Assert.False(guest.Capabilities.RankedMatchAllowed);
            Assert.Equal("PENDING", guest.Capabilities.ProductDecision);
            Assert.Contains("OIDC_ACCOUNTS", guest.Capabilities.LaunchBlocked);
            Assert.Contains("RANKED_MATCH", guest.Capabilities.LaunchBlocked);
            Assert.NotEmpty(guest.SessionToken);
        }

        [Fact]
        public async Task OidcCannotBeEnabledByAccident()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            var options = new LobbyOptions { OidcEnabled = true };
            Assert.Throws<LobbyOptionsException>(() => options.Validate("Development"));
        }

        [Fact]
        public async Task ConcurrentSameDeviceAndSameIdempotencyKeyCreatesOneGuestAndOneToken()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            string requestId = LobbyFixture.NewRequestId();
            var command = new GuestSessionCommand("device-race", "Racer");
            var tasks = new List<Task<LobbyResponse>>();
            for (int i = 0; i < 8; i++)
            {
                tasks.Add(fixture.Application.CreateGuestSessionAsync(command, requestId, default));
            }

            LobbyResponse[] responses = await Task.WhenAll(tasks);
            GuestSessionView[] views = responses
                .Select(response => LobbyJson.Deserialize<GuestSessionView>(response.Json))
                .ToArray();

            Assert.Single(views.Select(view => view.AccountId).Distinct(StringComparer.Ordinal));
            Assert.Single(views.Select(view => view.SessionToken).Distinct(StringComparer.Ordinal));
            Assert.All(views, view => Assert.Equal(views[0].AccountId, view.AccountId));
            Assert.Equal(1, responses.Count(response => !response.Replayed));
        }

        [Fact]
        public async Task DifferentDevicesGetDifferentGuestAccounts()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView first = await fixture.CreateGuestAsync("device-one");
            GuestSessionView second = await fixture.CreateGuestAsync("device-two");
            Assert.NotEqual(first.AccountId, second.AccountId);
        }

        [Fact]
        public async Task ExpiredGuestSessionIsRejected()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView guest = await fixture.CreateGuestAsync("device-expiry");
            fixture.Clock.Advance(TimeSpan.FromDays(8));

            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.AuthenticateAsync(guest.SessionToken, default));
            Assert.Equal(401, error.StatusCode);
            Assert.Equal("UNAUTHENTICATED", error.Code);
        }
    }
}
