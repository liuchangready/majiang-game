using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round12ReviewProbes
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SaveMustNotEraseRequiredCheatLedgerFromExistingRecoveryPoint(bool nonzero)
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            if (nonzero)
            {
                Assert.Equal(TableOperationStatus.Accepted,
                    (await h.SubmitAsync(h.Envelope(P31Fixtures.DiscardCurrent(h.InitialRound), 0,
                        "round12-discard"))).Status);
            }

            TableCheckpoint good = await h.SaveCheckpointAsync();
            Assert.NotNull(good.CheatPayload);
            var candidate = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq,
                good.StateFingerprint, good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                good.ControlVersion, good.ControlPayload, good.ControlPayloadSha256,
                good.TimingPayload, good.TimingPayloadSha256,
                null, null,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
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
            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);

            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "Save erased required cheat ledger at stateVersion=" + good.StateVersion +
                "; disposition=" + saved.Disposition + "; recovery=" + recovered.Recovered +
                "/" + recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(good.CheatPayloadSha256, current.CheatPayloadSha256);
            Assert.Equal(good.TimingVersion, current.TimingVersion);
            Assert.Equal(before, after);
            Assert.True(recovered.Recovered, "Rejected save must preserve the previous recovery point.");
        }
    }
}
