using System;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round8ReviewProbes
    {
        [Theory]
        [InlineData("none")]
        [InlineData("empty")]
        [InlineData("fingerprint")]
        public async Task SaveMustValidateIndependentBindingsBeforeReplacingRecoveryPoint(string mutation)
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            var chain = P31Fixtures.BuildDiscardChain(h.InitialRound, 2);
            for (int i = 0; i < chain.Count; i++)
            {
                Assert.Equal(TableOperationStatus.Accepted,
                    (await h.SubmitAsync(h.Envelope(chain[i].Command, i, "save-binding-" + i))).Status);
            }

            TableCheckpoint good = await h.SaveCheckpointAsync();
            var bindings = TableCommandBindingsCodec.Decode(good.CommittedCommandBindingsPayload!).ToArray();
            if (mutation == "empty")
            {
                bindings = Array.Empty<TableCommandBinding>();
            }
            else if (mutation == "fingerprint")
            {
                TableCommandBinding first = bindings[0];
                bindings[0] = new TableCommandBinding(first.CommandVersion, first.EventCount,
                    first.SourceFingerprint ^ 1UL);
            }

            byte[] payload = TableCommandBindingsCodec.Encode(bindings);
            var candidate = new TableCheckpoint(
                good.RoomId, good.StateVersion, good.LastEventSeq,
                good.StateFingerprint, good.RulesetVersion,
                good.StatePayload, good.StatePayloadSha256,
                good.ControlVersion, good.ControlPayload, good.ControlPayloadSha256,
                good.TimingPayload, good.TimingPayloadSha256,
                good.CheatPayload, good.CheatPayloadSha256,
                good.CommittedFactsPayload, good.CommittedFactsPayloadSha256,
                good.CreatedAtUtcMs, good.BootId, good.SchemaVersion,
                good.TimingVersion + 1, good.CapturedMonotonicMillis,
                good.CommittedHistoryPayload, good.CommittedHistoryPayloadSha256,
                payload, TablePayloadSha256.Compute(payload));
            TableDurableCommitResult saved = await h.Store.SaveCheckpointAsync(
                candidate,
                new TableCheckpointFence(DurableHarness.Room, DurableHarness.Owner,
                    new OwnerEpoch(1), good.StateVersion, good.ControlVersion, good.TimingVersion),
                TestContext.Current.CancellationToken);
            TableRecoveryResult recovery = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);

            if (mutation == "none")
            {
                Assert.Equal(TableCommitDisposition.Committed, saved.Disposition);
                Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
                return;
            }

            Assert.True(saved.Disposition == TableCommitDisposition.Failed,
                "Invalid " + mutation + " bindings replaced a valid checkpoint: save=" + saved.Disposition +
                "; recovery=" + recovery.Recovered + "/" + recovery.ReasonCode + ": " + recovery.Detail);
            Assert.True(recovery.Recovered, "Rejected save must preserve the previous recovery point.");
        }
    }
}
