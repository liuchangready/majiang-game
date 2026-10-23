using System.Linq;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP33Round3ReviewProbes
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task InferenceStartedInPreviousGenerationCannotBeAdaptedAfterRecovery(bool raw)
        {
            await using var h = P31Fixtures.CreateActor();
            var d = P31Fixtures.BuildDiscardDecision(h);
            Assert.Equal(TableOperationStatus.Configured, (await h.OpenDecisionAsync(d.Pending)).Status);
            await RecoverAsync(h, d);
            var old = await CaptureAsync(h, d);
            await RecoverAsync(h, d);
            // Conversion happens only AFTER the second recovery, as with late inference.
            h.Clock.Now = 1500;
            var request = raw
                ? new TableTickRequest(new[] { old.ToDomainSubmission(h.Clock.Now) })
                : TableTickRequest.WithAiDecisions(new[] { old });
            var result = await h.TickAsync(request);
            Assert.Contains(result.Entries, e => e.Kind == TickEntryKind.AiRejected &&
                e.RejectionCode == TickRejectionCode.StaleRecoveryGeneration);
            Assert.Equal(0UL, (await h.GetSnapshotAsync()).StateVersion.Value);
            var fresh = await CaptureAsync(h, d);
            var accepted = await h.TickAsync(TableTickRequest.WithAiDecisions(new[] { fresh }));
            Assert.Contains(accepted.Entries, e => e.Kind == TickEntryKind.AiAccepted &&
                e.Transition?.Status == TableOperationStatus.Accepted);
        }

        [Fact]
        public async Task ObservationIssuedByAnotherActorWithSameIdentityCannotAuthorizeRecoveredActor()
        {
            await using var first = P31Fixtures.CreateActor();
            await using var second = P31Fixtures.CreateActor();
            var a = P31Fixtures.BuildDiscardDecision(first);
            var b = P31Fixtures.BuildDiscardDecision(second);
            Assert.Equal(a.Pending.Key, b.Pending.Key);
            Assert.Equal(TableOperationStatus.Configured, (await first.OpenDecisionAsync(a.Pending)).Status);
            Assert.Equal(TableOperationStatus.Configured, (await second.OpenDecisionAsync(b.Pending)).Status);
            await RecoverAsync(first, a);
            await RecoverAsync(second, b);
            var foreign = await CaptureAsync(first, a);
            var rejected = await second.TickAsync(TableTickRequest.WithAiDecisions(new[] { foreign }));
            Assert.Contains(rejected.Entries, e => e.RejectionCode == TickRejectionCode.StaleRecoveryGeneration);
            Assert.Equal(0UL, (await second.GetSnapshotAsync()).StateVersion.Value);
            var local = await CaptureAsync(second, b);
            var accepted = await second.CompleteAiAsync(local);
            Assert.Contains(accepted.Entries, e => e.Transition?.Status == TableOperationStatus.Accepted);
        }

        private static async Task<AiDecision> CaptureAsync(ActorHarness h, DiscardDecisionFixture d)
        {
            var capture = await h.CaptureAiObservationAsync(0);
            Assert.True(capture.IsCaptured);
            return new AiDecision(capture.Observation!, new CommandIntent(capture.Observation!, d.Chosen, "review", 1), 0);
        }

        private static async Task RecoverAsync(ActorHarness h, DiscardDecisionFixture d)
        {
            var snapshot = await h.GetSnapshotAsync();
            var control = P31Fixtures.ControlSnapshot(Enumerable.Range(0, 4)
                .Select(s => P31Fixtures.Connected(SeatControllerKind.Computer, 0, s)).ToArray());
            Assert.Equal(TableOperationStatus.Configured, (await h.BeginRecoveryAsync("independent round 3 probe")).Status);
            var recovered = new TableRecoveredDecision(d.Pending.Key, 0, d.Pending.Candidates,
                d.Pending.DrawnTile, 1000, 0, 0);
            Assert.Equal(TableOperationStatus.Configured, (await h.CompleteRecoveryAsync(new TableRecoveryBaseline(
                h.InitialRound, snapshot.StateVersion, snapshot.LastEventSeq, snapshot.OwnerEpoch,
                new TableControlRecoveryBaseline(control, 0), new TableRecoveryTiming(new[] { recovered })))).Status);
        }
    }
}
