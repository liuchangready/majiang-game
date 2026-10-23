using System.Linq;
using System.Threading.Tasks;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP33Round2ReviewProbes
    {
        [Fact]
        public async Task OldAiCannotBypassRecoveryFenceThroughPublicTickBatch()
        {
            await using var h = P31Fixtures.CreateActor();
            var decision = P31Fixtures.BuildDiscardDecision(h);
            Assert.Equal(TableOperationStatus.Configured, (await h.OpenDecisionAsync(decision.Pending)).Status);
            var oldSubmission = decision.Ai.ToDomainSubmission(1000);
            var snapshot = await h.GetSnapshotAsync();
            var control = P31Fixtures.ControlSnapshot(Enumerable.Range(0, 4)
                .Select(s => P31Fixtures.Connected(SeatControllerKind.Computer, 0, s)).ToArray());
            Assert.Equal(TableOperationStatus.Configured, (await h.BeginRecoveryAsync("same decision")).Status);
            var recovered = new TableRecoveredDecision(decision.Pending.Key, 0, decision.Pending.Candidates,
                decision.Pending.DrawnTile, 1000, 0, 0);
            Assert.Equal(TableOperationStatus.Configured, (await h.CompleteRecoveryAsync(new TableRecoveryBaseline(
                h.InitialRound, snapshot.StateVersion, snapshot.LastEventSeq, snapshot.OwnerEpoch,
                new TableControlRecoveryBaseline(control, 0), new TableRecoveryTiming(new[] { recovered })))).Status);

            h.Clock.Now = 1500;
            var result = await h.TickAsync(new TableTickRequest(new[] { oldSubmission }));
            Assert.DoesNotContain(result.Entries, e => e.Kind == TickEntryKind.AiAccepted
                && e.Transition?.Status == TableOperationStatus.Accepted);
            Assert.Equal(snapshot.StateVersion, (await h.GetSnapshotAsync()).StateVersion);
        }
    }
}
