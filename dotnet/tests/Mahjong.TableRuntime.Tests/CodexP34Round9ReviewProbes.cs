using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round9ReviewProbes
    {
        [Theory]
        [InlineData("none")]
        [InlineData("schema")]
        [InlineData("ruleset")]
        public async Task SaveMustRejectUnreadableVersionBeforeReplacingRecoveryPoint(string mutation)
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.SubmitAsync(h.Envelope(P31Fixtures.DiscardCurrent(h.InitialRound), 0, "version-save"))).Status);
            TableCheckpoint good = await h.SaveCheckpointAsync();
            var candidate = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq,
                good.StateFingerprint, mutation == "ruleset" ? good.RulesetVersion + 1 : good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                good.ControlVersion, good.ControlPayload, good.ControlPayloadSha256,
                good.TimingPayload, good.TimingPayloadSha256,
                good.CheatPayload, good.CheatPayloadSha256,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs, good.BootId,
                mutation == "schema" ? good.SchemaVersion + 1 : good.SchemaVersion,
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
            int commitsAfterSave = h.Store.CommitCount;
            TableRecoveryResult recovery = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            if (mutation == "none")
            {
                Assert.Equal(TableCommitDisposition.Committed, saved.Disposition);
                Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
                return;
            }

            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "Unreadable " + mutation + " replaced valid checkpoint: save=" + saved.Disposition +
                "; recovery=" + recovery.Recovered + "/" + recovery.ReasonCode + ": " + recovery.Detail);
            Assert.Equal(good.SchemaVersion, current.SchemaVersion);
            Assert.Equal(good.RulesetVersion, current.RulesetVersion);
            Assert.Equal(good.TimingVersion, current.TimingVersion);
            Assert.Equal(before, commitsAfterSave);
            Assert.True(recovery.Recovered, "Rejected save must preserve the previous recovery point.");
        }

        [Fact]
        public async Task LostResponseCanRecoverSaveRecoverAndReplayExactReceipt()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            TableCommandEnvelope command = h.Envelope(P31Fixtures.DiscardCurrent(h.InitialRound), 0, "lost-save-recover");
            h.Store.ArmedFaultPoint = TableStoreFaultPoint.AfterApplyBeforeReturn;
            Assert.Equal(TableRuntimeReasonCode.CommitOutcomeUnknown, (await h.SubmitAsync(command)).ReasonCode);
            TableRecoveryResult first = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(first.Recovered, first.ReasonCode + ": " + first.Detail);
            await h.SaveCheckpointAsync();
            TableRecoveryResult second = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(second.Recovered, second.ReasonCode + ": " + second.Detail);
            int receipts = h.Store.ReceiptCount;
            int events = h.Store.EventCount;
            TableOperationResult replay = await h.SubmitAsync(command);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.Equal(1UL, replay.CommandVersion!.Value.Value);
            Assert.Equal(receipts, h.Store.ReceiptCount);
            Assert.Equal(events, h.Store.EventCount);
        }
    }
}
