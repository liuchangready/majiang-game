using System;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round7ReviewProbes
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task ReplayEvidenceMustSurviveSaveAndSecondRecovery(int checkpointVersion)
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            var chain = P31Fixtures.BuildDiscardChain(h.InitialRound, 2);
            for (int i = 0; i < 2; i++)
            {
                TableOperationResult accepted = await h.SubmitAsync(h.Envelope(chain[i].Command, i, "repeat-" + i));
                Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
                if (i + 1 == checkpointVersion)
                {
                    await h.SaveCheckpointAsync();
                }
            }

            TableRecoveryResult first = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(first.Recovered, first.ReasonCode + ": " + first.Detail);
            Assert.Equal(2UL, first.Baseline!.StateVersion.Value);
            await h.SaveCheckpointAsync();
            TableRecoveryResult second = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(second.Recovered, "Second recovery lost replay evidence: " + second.ReasonCode + ": " + second.Detail);
            Assert.Equal(2, second.Baseline!.CommandBindings.Count);
        }

        [Fact]
        public async Task TerminalCommandAfterEarlierCommitMustCreateAtomicCheckpointAndOutbox()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync(
                projectionSource: DurableHarness.TerminalFactsSource());
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.SubmitAsync(h.Envelope(P31Fixtures.DiscardCurrent(h.InitialRound), 0, "before-terminal"))).Status);
            var settlement = new RoundSettlement(
                "later-terminal", RoundOutcome.AbortiveDraw("test"),
                new long[] { 25000, 25000, 25000, 25000 }, 0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)), 1234L);
            RoundCommand command = RoundCommand.AbortiveDraw(h.InitialRound.RoundId, settlement);
            RoundStateMachine afterDiscard = h.InitialRound.Execute(P31Fixtures.DiscardCurrent(h.InitialRound)).State!;
            Assert.True(afterDiscard.Execute(command).IsAccepted);
            TableOperationResult terminal = await h.SubmitAsync(h.Envelope(command, 1, "later-terminal", api: "ABORTIVE_DRAW"));
            Assert.True(terminal.Status == TableOperationStatus.Accepted,
                "Terminal command after v1 failed: " + terminal.Status + "/" + terminal.ReasonCode + ": " + terminal.Detail);
            Assert.Equal(1, h.Store.OutboxCount);
            Assert.Equal(2, h.Store.ReceiptCount);
            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            Assert.True(recovered.Report!.Terminal);
        }
    }
}
