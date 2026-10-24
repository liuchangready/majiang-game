using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round13ReviewProbes
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ControlCheckpointMustUseTheFencedRoomAndPreserveItsLedger(
            bool foreignRoom, bool eraseLedger)
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            TableCheckpoint good = await h.SaveCheckpointAsync();
            long nextControl = good.ControlVersion + 1;
            byte[] control = TableControlCheckpointCodec.Encode(new TableControlCheckpoint(
                TableControlCheckpointCodec.Decode(good.ControlPayload).Snapshot, nextControl));
            var candidate = new TableCheckpoint(
                foreignRoom ? "room-with-no-checkpoint" : good.RoomId,
                good.StateVersion, good.LastEventSeq, good.StateFingerprint, good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                nextControl, control, TablePayloadSha256.Compute(control),
                good.TimingPayload, good.TimingPayloadSha256,
                eraseLedger ? null : good.CheatPayload,
                eraseLedger ? null : good.CheatPayloadSha256,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs, good.BootId, good.SchemaVersion,
                good.TimingVersion + 1, good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload, good.CommittedHistoryPayloadSha256,
                good.CommittedCommandBindingsPayload, good.CommittedCommandBindingsPayloadSha256);
            var change = new TableControlEvent(
                nextControl, h.InitialRound.RoundId, -1, TableControlChangeKind.PolicyConfigured,
                null, null, null, null, null, null, null,
                h.Clock.MonotonicMillis, h.Clock.UtcNowMs, "review-policy");
            var request = new TableControlCommitRequest(
                DurableHarness.Room, new OwnerEpoch(1), good.ControlVersion, nextControl,
                good.StateVersion, h.InitialRound.RoundId, change, DurableHarness.Owner);
            int before = h.Store.CommitCount;
            TableDurableCommitResult saved = await h.Store.CommitControlAsync(
                request, candidate, TestContext.Current.CancellationToken);
            TableCheckpoint current = (await h.Store.LoadCheckpointAsync(
                DurableHarness.Room, TestContext.Current.CancellationToken))!;
            int after = h.Store.CommitCount;
            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            if (!foreignRoom && !eraseLedger)
            {
                Assert.Equal(TableCommitDisposition.Committed, saved.Disposition);
                Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
                return;
            }

            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "Control commit accepted foreignRoom=" + foreignRoom + ", eraseLedger=" + eraseLedger +
                "; saved=" + saved.Disposition + "; installedRoom=" + current.RoomId +
                "; recovery=" + recovered.Recovered + "/" + recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(good.RoomId, current.RoomId);
            Assert.Equal(good.CheatPayloadSha256, current.CheatPayloadSha256);
            Assert.Equal(good.ControlVersion, current.ControlVersion);
            Assert.Equal(good.TimingVersion, current.TimingVersion);
            Assert.Equal(before, after);
            Assert.True(recovered.Recovered, "Invalid control save must preserve the old recovery point.");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SaveMustRejectTimingEnvelopeThatDisagreesWithPayload(bool corruptOrigin)
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            TableCheckpoint good = await h.SaveCheckpointAsync();
            var candidate = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq,
                good.StateFingerprint, good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                good.ControlVersion, good.ControlPayload, good.ControlPayloadSha256,
                good.TimingPayload, good.TimingPayloadSha256,
                good.CheatPayload, good.CheatPayloadSha256,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs, good.BootId, good.SchemaVersion,
                good.TimingVersion + 1,
                good.CapturedMonotonicMillis + (corruptOrigin ? 60000 : 0),
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
            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            if (!corruptOrigin)
            {
                Assert.Equal(TableCommitDisposition.Committed, saved.Disposition);
                Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
                return;
            }

            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "Save accepted a future envelope origin inconsistent with its timing payload: " +
                saved.Disposition + "; recovery=" + recovered.Recovered + "/" +
                recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(good.TimingVersion, current.TimingVersion);
            Assert.Equal(good.CapturedMonotonicMillis, current.CapturedMonotonicMillis);
            Assert.Equal(before, after);
            Assert.True(recovered.Recovered, "Rejected timing save must preserve the old recovery point.");
        }
    }
}
