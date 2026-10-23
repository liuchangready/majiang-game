using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP33ReviewProbes
    {
        [Fact]
        public void RecoveryElapsedTimeConsumesBaseBeforeReserve()
        {
            var clock = new DecisionClock();
            clock.BeginRound("r", 1000);
            var timing = clock.RestoreDecision(new DecisionKey("r", 0, "d"), 1000, 3000, 5000, 3000);
            Assert.Equal(6000, timing.DeadlineMillis);
            Assert.Equal(0, timing.BaseMillis);
            Assert.Equal(5000, timing.ReserveAtOpenMillis);
        }

        [Fact]
        public async Task RecoveryCannotResetIdleSeatReserve()
        {
            await using var h = P31Fixtures.CreateActor(decisionClockConfig: new DecisionClockConfig(100, 1000));
            var decision = P31Fixtures.BuildDiscardDecision(h);
            await h.OpenDecisionAsync(decision.Pending);
            h.Clock.Now = 1500;
            Assert.Equal(TableOperationStatus.Accepted, (await h.CompleteAiAsync(decision.Ai)).Entries[0].Transition!.Status);
            long before = (await h.GetTimingSnapshotAsync()).Seats[0].RemainingReserveMillis;
            Assert.Equal(600, before);
            var snapshot = await h.GetSnapshotAsync();
            var control = P31Fixtures.ControlSnapshot(Enumerable.Range(0, 4)
                .Select(s => P31Fixtures.Connected(SeatControllerKind.Computer, 0, s)).ToArray());
            await h.BeginRecoveryAsync("same round");
            var currentRound = h.InitialRound.Execute(RoundCommand.Discard(h.InitialRound.RoundId, 0, decision.Chosen.Tile!.Value)).State!;
            await h.CompleteRecoveryAsync(new TableRecoveryBaseline(currentRound, snapshot.StateVersion,
                snapshot.LastEventSeq, snapshot.OwnerEpoch, new TableControlRecoveryBaseline(control, 0), TableRecoveryTiming.None));
            Assert.Equal(before, (await h.GetTimingSnapshotAsync()).Seats[0].RemainingReserveMillis);
        }

        [Fact]
        public async Task OldAiCannotCrossSameDecisionRecovery()
        {
            await using var h = P31Fixtures.CreateActor();
            var decision = P31Fixtures.BuildDiscardDecision(h);
            await h.OpenDecisionAsync(decision.Pending);
            var snapshot = await h.GetSnapshotAsync();
            var control = P31Fixtures.ControlSnapshot(Enumerable.Range(0, 4)
                .Select(s => P31Fixtures.Connected(SeatControllerKind.Computer, 0, s)).ToArray());
            await h.BeginRecoveryAsync("same decision");
            var recovered = new TableRecoveredDecision(decision.Pending.Key, 0, decision.Pending.Candidates,
                decision.Pending.DrawnTile, 1000, 0, 0);
            await h.CompleteRecoveryAsync(new TableRecoveryBaseline(h.InitialRound, snapshot.StateVersion,
                snapshot.LastEventSeq, snapshot.OwnerEpoch, new TableControlRecoveryBaseline(control, 0),
                new TableRecoveryTiming(new[] { recovered })));
            var result = await h.CompleteAiAsync(decision.Ai);
            Assert.DoesNotContain(result.Entries, e => e.Kind == TickEntryKind.AiAccepted);
        }

        [Fact]
        public async Task SlowTickCommitCannotAdmitLaterAiAfterDeadline()
        {
            await using var h = P31Fixtures.CreateActor();
            var round = h.InitialRound;
            var tile = round.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            await h.SubmitAsync(P31Fixtures.Command(RoundCommand.Discard(round.RoundId, 0, tile, new[] { 1, 2, 3 }), 0, "open"));
            var first = P31Fixtures.BuildSingleActionDecision(h, LegalActionKind.Pass, "p1", 1);
            var second = P31Fixtures.BuildSingleActionDecision(h, LegalActionKind.Pass, "p2", 2);
            await h.OpenDecisionAsync(first.Pending);
            await h.OpenDecisionAsync(second.Pending);
            h.Clock.Now = 1500;
            h.CommitPort.BlockNext();
            var running = h.TickAsync(new TableTickRequest(new[] {
                first.Ai.ToDomainSubmission(1500), second.Ai.ToDomainSubmission(1500) })).AsTask();
            await h.CommitPort.Entered;
            h.Clock.Now = 2000;
            h.CommitPort.Release();
            var result = await running;
            Assert.DoesNotContain(result.Entries, e => e.Seat == 2 && e.Kind == TickEntryKind.AiAccepted
                && e.Transition!.Status == TableOperationStatus.Accepted);
        }
    }
}
