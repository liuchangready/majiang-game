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
            var command = new GuestSessionCommand(
                "device-race",
                LobbyFixture.DefaultBootstrap("device-race"),
                "Racer");
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
        public async Task SameDeviceWithoutTheBootstrapSecretCannotTakeOverAVictimAccount()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            const string device = "known-victim-device";
            const string victimBootstrap = "victim-bootstrap-0123456789abcdef0123456789";
            const string attackerBootstrap = "attacker-bootstrap-0123456789abcdef0123456789";
            GuestSessionView victim = await fixture.CreateGuestAsync(device, "Victim", victimBootstrap);
            CreateRoomView room = await fixture.CreateRoomAsync(victim);

            LobbyException missing = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.CreateGuestSessionAsync(
                    new GuestSessionCommand(device, string.Empty, "Attacker"),
                    LobbyFixture.NewRequestId(),
                    default));
            Assert.Equal("BOOTSTRAP_SECRET_REQUIRED", missing.Code);

            GuestSessionView attacker = await fixture.CreateGuestAsync(device, "Attacker", attackerBootstrap);
            Assert.NotEqual(victim.AccountId, attacker.AccountId);
            Assert.NotEqual(victim.SessionToken, attacker.SessionToken);

            LobbySessionInfo attackerSession = await fixture.AuthenticateAsync(attacker);
            LobbyException ticket = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.IssueTableTicketAsync(
                    attackerSession.Account,
                    new IssueTableTicketCommand(room.Assignment.AssignmentId),
                    LobbyFixture.NewRequestId(),
                    default));
            Assert.Equal(404, ticket.StatusCode);
            Assert.Equal("ASSIGNMENT_NOT_FOUND", ticket.Code);
        }

        [Fact]
        public async Task SameRequestIdWithDifferentBootstrapCannotReplayTheVictimToken()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            const string device = "same-request-device";
            const string victimBootstrap = "victim-request-bootstrap-0123456789abcdef0123";
            const string attackerBootstrap = "attacker-request-bootstrap-0123456789abcdef";
            string requestId = LobbyFixture.NewRequestId();

            GuestSessionView victim = await fixture.CreateGuestAsync(
                device,
                "Victim",
                victimBootstrap,
                requestId);
            GuestSessionView attacker = await fixture.CreateGuestAsync(
                device,
                "Attacker",
                attackerBootstrap,
                requestId);

            Assert.NotEqual(victim.AccountId, attacker.AccountId);
            Assert.NotEqual(victim.SessionToken, attacker.SessionToken);
        }

        [Fact]
        public async Task SameDeviceAndBootstrapIsLegitimateDoubleOpenAndIdempotentReplay()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            const string device = "legit-double-device";
            const string bootstrap = "legit-bootstrap-0123456789abcdef0123456789";
            GuestSessionView first = await fixture.CreateGuestAsync(device, "Same", bootstrap);
            GuestSessionView second = await fixture.CreateGuestAsync(device, "Same", bootstrap);

            Assert.Equal(first.AccountId, second.AccountId);
            Assert.NotEqual(first.SessionToken, second.SessionToken);
            Assert.NotNull(await fixture.Application.AuthenticateAsync(first.SessionToken, default));
            Assert.NotNull(await fixture.Application.AuthenticateAsync(second.SessionToken, default));

            string requestId = LobbyFixture.NewRequestId();
            GuestSessionView replayOne = await fixture.CreateGuestAsync(device, "Same", bootstrap, requestId);
            GuestSessionView replayTwo = await fixture.CreateGuestAsync(device, "Same", bootstrap, requestId);
            Assert.Equal(replayOne.SessionToken, replayTwo.SessionToken);
            Assert.Equal(replayOne.AccountId, replayTwo.AccountId);
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
