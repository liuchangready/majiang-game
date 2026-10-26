using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round22ReviewProbes
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CommitMustReadFreshTimeAfterWaitingForScopeBoundary(bool expireLease)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness h = await DurableHarness.CreateAsync();
            TableScopeRecord scope = h.Scopes.Issue(DurableHarness.Account,
                TableScopeRole.Player, DurableHarness.Room, (ulong)h.Clock.UtcNowMs,
                (ulong)h.Clock.UtcNowMs + (expireLease ? 3_600_000UL : 500UL));
            TableStateRow before = (await h.Store.LoadStateAsync(DurableHarness.Room, ct))!;
            int commits = h.Store.CommitCount;
            // Model elapsed time while entering the transactional scope read. Domain admission
            // and the store's first time sample have already happened, but the scope row has
            // not been read and neither scope nor lease validation has linearized yet.
            h.Store.BeforeScopeCheckForTest = () => h.Clock.Now += expireLease ? 600_001L : 501L;
            TableOperationResult result = await h.SubmitAsync(h.Envelope(
                P31Fixtures.DiscardCurrent(h.InitialRound), 0, "fresh-time", scope: scope));
            h.Store.BeforeScopeCheckForTest = null;

            Assert.True(result.Status != TableOperationStatus.Accepted,
                "Commit accepted after " + (expireLease ? "lease" : "scope") +
                " expired before its transactional validation; status=" + result.Status);
            Assert.Equal(0, h.Store.ReceiptCount);
            Assert.Equal(0, h.Store.EventCount);
            Assert.Equal(commits, h.Store.CommitCount);
            Assert.Equal(before.StateVersion,
                (await h.Store.LoadStateAsync(DurableHarness.Room, ct))!.StateVersion);
            // A failed expiration must not publish successful applied/recovered content. A
            // necessary LeaseLost control notification is allowed (and required by R33).
            Assert.DoesNotContain(
                h.Sink.Publications,
                value => value.Kind == TablePublicationKind.Applied ||
                         value.Kind == TablePublicationKind.Recovered);
        }
    }
}
