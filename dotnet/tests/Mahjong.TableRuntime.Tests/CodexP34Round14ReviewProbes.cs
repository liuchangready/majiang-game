using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round14ReviewProbes
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AcceptedCheckpointAndSeparateTimingJournalMustHaveTheSameOrigin(bool corruptJournal)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness source = await DurableHarness.CreateAsync();
            TableCheckpoint initial = (await source.Store.LoadCheckpointAsync(DurableHarness.Room, ct))!;
            Assert.Equal(TableOperationStatus.Accepted,
                (await source.SubmitAsync(source.Envelope(
                    P31Fixtures.DiscardCurrent(source.InitialRound), 0, "round14-discard"))).Status);
            TableCheckpoint good = await source.SaveCheckpointAsync();
            var events = await source.Store.LoadEventsAfterAsync(DurableHarness.Room, new EventSeq(0), ct);
            var commands = await source.Store.LoadCommandsAfterAsync(DurableHarness.Room, new StateVersion(0), ct);

            await using DurableHarness target = await DurableHarness.CreateAsync(initialCheckpoint: initial);
            TableStateRow before = (await target.Store.LoadStateAsync(DurableHarness.Room, ct))!;
            long nextTiming = before.TimingVersion + 1;
            var checkpoint = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq,
                good.StateFingerprint, good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                good.ControlVersion, good.ControlPayload, good.ControlPayloadSha256,
                good.TimingPayload, good.TimingPayloadSha256,
                good.CheatPayload, good.CheatPayloadSha256,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs, good.BootId, good.SchemaVersion,
                nextTiming, good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload, good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload, good.CommittedCommandBindingsPayloadSha256);
            TableTimingCheckpoint decoded = TableTimingCheckpointCodec.Decode(good.TimingPayload);
            var timing = new TableTimingCheckpoint(decoded.RoundId, decoded.SeatReservesMillis,
                decoded.Decisions, decoded.CapturedAtUtcMs,
                decoded.CapturedMonotonicMillis + (corruptJournal ? 60000 : 0), decoded.BootId);
            var request = new TableDurableCommitRequest(
                DurableHarness.Room, DurableHarness.Owner, new OwnerEpoch(1), before.StateVersion,
                null, true, events, commands[0], null, checkpoint,
                good.StatePayload, good.StateFingerprint, false, timing, nextTiming,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256);
            int commits = target.Store.CommitCount;
            TableDurableCommitResult saved = await target.Store.CommitAsync(request, ct);
            TableStateRow after = (await target.Store.LoadStateAsync(DurableHarness.Room, ct))!;
            int afterCommits = target.Store.CommitCount;
            TableRecoveryResult recovered = await target.Actor.RecoverFromStoreAsync(ct);
            if (!corruptJournal)
            {
                Assert.Equal(TableCommitDisposition.Committed, saved.Disposition);
                Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
                return;
            }

            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "Accepted batch installed conflicting checkpoint/journal origins: " + saved.Disposition +
                "; recovery=" + recovered.Recovered + "/" + recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(before.StateVersion, after.StateVersion);
            Assert.Equal(before.TimingVersion, after.TimingVersion);
            Assert.Equal(commits, afterCommits);
            Assert.Equal(0, target.Store.EventCount);
            Assert.True(recovered.Recovered, "Rejected batch must preserve the initial recovery point.");
        }
    }
}
