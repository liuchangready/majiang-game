using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round10ReviewProbes
    {
        [Theory]
        [InlineData("none")]
        [InlineData("control-version")]
        [InlineData("cheat-payload")]
        [InlineData("facts-payload")]
        public async Task SaveMustValidateSidePayloadsBeforeReplacingRecoveryPoint(string mutation)
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.SubmitAsync(h.Envelope(P31Fixtures.DiscardCurrent(h.InitialRound), 0, "side-save"))).Status);
            TableCheckpoint good = await h.SaveCheckpointAsync();
            byte[] control = good.ControlPayload;
            byte[] cheat = good.CheatPayload!;
            byte[] facts = good.CommittedFactsPayload!;
            if (mutation == "control-version")
            {
                TableControlCheckpoint decoded = TableControlCheckpointCodec.Decode(control);
                control = TableControlCheckpointCodec.Encode(new TableControlCheckpoint(
                    decoded.Snapshot, good.ControlVersion + 1));
            }
            if (mutation == "cheat-payload") cheat = new byte[] { 1, 2, 3 };
            if (mutation == "facts-payload") facts = new byte[] { 1, 2, 3 };
            var candidate = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq,
                good.StateFingerprint, good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                good.ControlVersion, control, TablePayloadSha256.Compute(control),
                good.TimingPayload, good.TimingPayloadSha256,
                cheat, TablePayloadSha256.Compute(cheat),
                facts, TablePayloadSha256.Compute(facts),
                good.CreatedAtUtcMs, good.BootId, good.SchemaVersion,
                good.TimingVersion + 1, good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload, good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload, good.CommittedCommandBindingsPayloadSha256);
            int before = h.Store.CommitCount;
            TableDurableCommitResult saved = await h.Store.SaveCheckpointAsync(candidate,
                new TableCheckpointFence(DurableHarness.Room, DurableHarness.Owner,
                    new OwnerEpoch(1), good.StateVersion, good.ControlVersion, good.TimingVersion),
                TestContext.Current.CancellationToken);
            TableCheckpoint current = (await h.Store.LoadCheckpointAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken))!;
            int after = h.Store.CommitCount;
            TableRecoveryResult recovery = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            if (mutation == "none")
            {
                Assert.Equal(TableCommitDisposition.Committed, saved.Disposition);
                Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
                return;
            }
            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "Invalid " + mutation + " replaced valid checkpoint: save=" + saved.Disposition +
                "; recovery=" + recovery.Recovered + "/" + recovery.ReasonCode + ": " + recovery.Detail);
            Assert.Equal(good.ControlPayloadSha256, current.ControlPayloadSha256);
            Assert.Equal(good.CheatPayloadSha256, current.CheatPayloadSha256);
            Assert.Equal(good.CommittedFactsPayloadSha256, current.CommittedFactsPayloadSha256);
            Assert.Equal(good.TimingVersion, current.TimingVersion);
            Assert.Equal(before, after);
            Assert.True(recovery.Recovered, "Rejected save must preserve the previous recovery point.");
        }
    }
}
