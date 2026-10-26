using System;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round6ReviewProbes
    {
        [Theory]
        [InlineData("none")]
        [InlineData("fingerprint")]
        [InlineData("regroup")]
        public async Task CheckpointHistoryMustRetainCommittedCommandBindings(string mutation)
        {
            var sink = new RecordingRecoveryStateSink();
            await using DurableHarness h = await DurableHarness.CreateAsync(recoverySink: sink);
            var chain = P31Fixtures.BuildDiscardChain(h.InitialRound, 2);
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(TableOperationStatus.Accepted,
                    (await h.SubmitAsync(h.Envelope(chain[i].Command, i, "binding-" + i))).Status);
            }

            TableCheckpoint checkpoint = await h.SaveCheckpointAsync();
            TableEventRecord[] history = TableCommittedHistoryCodec.Decode(checkpoint.CommittedHistoryPayload!).ToArray();
            if (mutation != "none")
            {
                int boundary = Array.FindIndex(history, value => value.CommandVersion.Value == 2);
                Assert.True(boundary > 1);
                int movedBoundary = boundary - 1;
                for (int i = 0; i < history.Length; i++)
                {
                    TableEventRecord original = history[i];
                    TableEventFact binding = TableEventFactCodec.Decode(original.Payload!);
                    StateVersion version = mutation == "regroup"
                        ? new StateVersion(i < movedBoundary ? 1UL : 2UL) : original.CommandVersion;
                    int ordinal = mutation == "regroup"
                        ? (i < movedBoundary ? i : i - movedBoundary) : original.Ordinal;
                    ulong fingerprint = mutation == "fingerprint" ? binding.SourceFingerprint ^ 1UL : binding.SourceFingerprint;
                    history[i] = new TableEventRecord(original.Seq, version, ordinal, original.Type,
                        original.Seat, original.Tile, original.Detail, original.EventSchema,
                        TableEventFactCodec.Encode(version, ordinal, fingerprint));
                }

                if (mutation == "regroup")
                {
                    TableCommittedFactsSnapshot stored = (await h.Store.LoadCommittedFactsAsync(
                        DurableHarness.Room, TestContext.Current.CancellationToken))!;
                    TableProjectionContextData good = TableProjectionFactsCodec.Decode(
                        stored.Payload, h.InitialRound.RoundId, checkpoint.StateVersion);
                    var facts = good.EventFacts.Select(fact =>
                    {
                        TableEventRecord record = history.Single(value => value.Seq == fact.EventSeq);
                        return new TableProjectionEventFact(fact.Type, fact.EventSeq,
                            record.CommandVersion, (uint)record.Ordinal, fact.Facts);
                    }).ToArray();
                    byte[] factsPayload = TableProjectionFactsCodec.Encode(new TableProjectionContextData(
                        good.RoundId, good.StateVersion, good.SessionVersion, good.Cheat, good.PublicFacts,
                        good.PrivateFacts, good.Session, good.Policy, facts, good.ConcealedMeldFacts));
                    h.Store.SetCommittedFactsForTest(DurableHarness.Room, factsPayload, TablePayloadSha256.Compute(factsPayload));
                }

                byte[] payload = TableCommittedHistoryCodec.Encode(history);
                h.Store.ReplaceCheckpointForTest(DurableHarness.Room, new TableCheckpoint(
                    checkpoint.RoomId, checkpoint.StateVersion, checkpoint.LastEventSeq,
                    checkpoint.StateFingerprint, checkpoint.RulesetVersion,
                    checkpoint.StatePayload, checkpoint.StatePayloadSha256,
                    checkpoint.ControlVersion, checkpoint.ControlPayload, checkpoint.ControlPayloadSha256,
                    checkpoint.TimingPayload, checkpoint.TimingPayloadSha256,
                    checkpoint.CheatPayload, checkpoint.CheatPayloadSha256,
                    checkpoint.CommittedFactsPayload, checkpoint.CommittedFactsPayloadSha256,
                    checkpoint.CreatedAtUtcMs, checkpoint.BootId, checkpoint.SchemaVersion,
                    checkpoint.TimingVersion, checkpoint.CapturedMonotonicMillis,
                    payload, TablePayloadSha256.Compute(payload),
                    checkpoint.CommittedCommandBindingsPayload,
                    checkpoint.CommittedCommandBindingsPayloadSha256));
            }

            TableRecoveryResult result = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            if (mutation == "none")
            {
                Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
                return;
            }

            Assert.False(result.Recovered, "Checkpoint history accepted forged command binding: " + mutation);
            Assert.Null(sink.Installed);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
        }
    }
}
