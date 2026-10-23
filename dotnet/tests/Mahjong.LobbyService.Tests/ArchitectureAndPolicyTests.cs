using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Xunit;

namespace Mahjong.LobbyService.Tests
{
    public sealed class ArchitectureAndPolicyTests
    {
        [Fact]
        public void LobbyAssemblyDoesNotReferenceDomainOrTableRuntime()
        {
            string[] references = typeof(LobbyApplication).Assembly
                .GetReferencedAssemblies()
                .Select(name => name.Name ?? string.Empty)
                .ToArray();
            Assert.DoesNotContain(references, name => name.Contains("Mahjong.Domain", StringComparison.Ordinal));
            Assert.DoesNotContain(references, name => name.Contains("Mahjong.TableRuntime", StringComparison.Ordinal));
            Assert.DoesNotContain(references, name => name.Contains("Mahjong.LocalHost", StringComparison.Ordinal));
            Assert.DoesNotContain(references, name => name.Contains("Mahjong.Client", StringComparison.Ordinal));
            Assert.DoesNotContain(references, name => name.Contains("Mahjong.TableService", StringComparison.Ordinal));
        }

        [Fact]
        public void GuestRankedMatchRequiresAnExplicitProductAcknowledgement()
        {
            var options = new LobbyOptions { AllowGuestRankedMatch = true };
            Assert.Throws<LobbyOptionsException>(() => options.Validate("Development"));

            var acknowledged = new LobbyOptions
            {
                AllowGuestRankedMatch = true,
                ProductDecisionAcknowledged = true
            };
            acknowledged.Validate("Development");
        }

        [Fact]
        public void SpectatorDelayIsNotSilentlyFaked()
        {
            var options = new LobbyOptions { SpectatorDelaySeconds = 30 };
            Assert.Throws<LobbyOptionsException>(() => options.Validate("Development"));
        }

        [Fact]
        public async Task RankedGuestMatchIsDisabledAtRuntimeByDefault()
        {
            await using LobbyFixture fixture = LobbyFixture.Create();
            GuestSessionView guest = await fixture.CreateGuestAsync("policy-ranked");
            LobbySessionInfo session = await fixture.AuthenticateAsync(guest);
            LobbyException error = await Assert.ThrowsAsync<LobbyException>(
                () => fixture.Application.CreateMatchTicketAsync(
                    session.Account,
                    new CreateMatchCommand(LobbyMatchMode.Ranked, 1, "test"),
                    LobbyFixture.NewRequestId(),
                    default));
            Assert.Equal("RANKED_MATCH_DISABLED", error.Code);
        }

        [Fact]
        public async Task AcknowledgedTemporaryRankedPolicyCanMatchWithTheExplicitFifoRule()
        {
            await using LobbyFixture fixture = LobbyFixture.Create(options =>
            {
                options.AllowGuestRankedMatch = true;
                options.ProductDecisionAcknowledged = true;
            });
            var guests = new System.Collections.Generic.List<GuestSessionView>();
            var tickets = new System.Collections.Generic.List<MatchTicketView>();
            for (int i = 0; i < 4; i++)
            {
                GuestSessionView guest = await fixture.CreateGuestAsync("policy-ranked-ack-" + i);
                guests.Add(guest);
                tickets.Add(await fixture.CreateMatchAsync(guest, LobbyMatchMode.Ranked, "ranked-test"));
            }

            for (int i = 0; i < guests.Count; i++)
            {
                MatchTicketView current = await fixture.GetMatchAsync(guests[i], tickets[i].MatchTicketId);
                Assert.Equal("RESERVED", current.Status);
            }
        }

        [Fact]
        public void InMemoryStorageIsRefusedInProduction()
        {
            var options = new LobbyOptions { StorageProvider = "Memory" };
            Assert.Throws<LobbyOptionsException>(() => options.Validate("Production"));
        }

        [Fact]
        public void PostgresStorageRequiresAConnectionString()
        {
            var options = new LobbyOptions { StorageProvider = "Postgres" };
            Assert.Throws<LobbyOptionsException>(() => options.Validate("Development"));
        }
    }
}
