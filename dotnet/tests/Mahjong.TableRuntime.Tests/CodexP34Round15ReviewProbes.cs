using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round15ReviewProbes
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AcceptedCheckpointMustPreserveTheFencedControlVersion(bool advanceControl)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness source = await DurableHarness.CreateAsync();
            TableCheckpoint initial = (await source.Store.LoadCheckpointAsync(DurableHarness.Room, ct))!;
            Assert.Equal(TableOperationStatus.Accepted,
                (await source.SubmitAsync(source.Envelope(
                    P31Fixtures.DiscardCurrent(source.InitialRound), 0, "round15-discard"))).Status);
            TableCheckpoint good = await source.SaveCheckpointAsync();
            var events = await source.Store.LoadEventsAfterAsync(DurableHarness.Room, new EventSeq(0), ct);
            var commands = await source.Store.LoadCommandsAfterAsync(DurableHarness.Room, new StateVersion(0), ct);

            await using DurableHarness target = await DurableHarness.CreateAsync(initialCheckpoint: initial);
            TableStateRow before = (await target.Store.LoadStateAsync(DurableHarness.Room, ct))!;
            long nextTiming = before.TimingVersion + 1;
            long controlVersion = good.ControlVersion + (advanceControl ? 1 : 0);
            byte[] control = TableControlCheckpointCodec.Encode(new TableControlCheckpoint(
                TableControlCheckpointCodec.Decode(good.ControlPayload).Snapshot, controlVersion));
            var checkpoint = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq,
                good.StateFingerprint, good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                controlVersion, control, TablePayloadSha256.Compute(control),
                good.TimingPayload, good.TimingPayloadSha256,
                good.CheatPayload, good.CheatPayloadSha256,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs, good.BootId, good.SchemaVersion,
                nextTiming, good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload, good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload, good.CommittedCommandBindingsPayloadSha256);
            var request = new TableDurableCommitRequest(
                DurableHarness.Room, DurableHarness.Owner, new OwnerEpoch(1), before.StateVersion,
                null, true, events, commands[0], null, checkpoint,
                good.StatePayload, good.StateFingerprint, false,
                TableTimingCheckpointCodec.Decode(good.TimingPayload), nextTiming,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256);
            int commits = target.Store.CommitCount;
            TableDurableCommitResult saved = await target.Store.CommitAsync(request, ct);
            TableStateRow after = (await target.Store.LoadStateAsync(DurableHarness.Room, ct))!;
            int afterCommits = target.Store.CommitCount;
            TableRecoveryResult recovered = await target.Actor.RecoverFromStoreAsync(ct);
            if (!advanceControl)
            {
                Assert.Equal(TableCommitDisposition.Committed, saved.Disposition);
                Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
                return;
            }

            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "Accepted commit installed an unfenced control version: " + saved.Disposition +
                "; stateControl=" + after.ControlVersion + "; checkpointControl=" + controlVersion +
                "; recovery=" + recovered.Recovered + "/" + recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(before.StateVersion, after.StateVersion);
            Assert.Equal(before.ControlVersion, after.ControlVersion);
            Assert.Equal(before.TimingVersion, after.TimingVersion);
            Assert.Equal(commits, afterCommits);
            Assert.Equal(0, target.Store.EventCount);
            Assert.True(recovered.Recovered, "Rejected batch must preserve the initial recovery point.");
        }
    }
}
