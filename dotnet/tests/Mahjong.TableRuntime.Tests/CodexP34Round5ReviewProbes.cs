using System;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round5ReviewProbes
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CheckpointHistoryMustMatchCapturedDomainEvents(bool corrupt)
        {
            var sink = new RecordingRecoveryStateSink();
            await using DurableHarness h = await DurableHarness.CreateAsync(recoverySink: sink);
            var chain = P31Fixtures.BuildDiscardChain(h.InitialRound, 2);
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.SubmitAsync(h.Envelope(chain[0].Command, 0, "prefix"))).Status);
            TableCheckpoint checkpoint = await h.SaveCheckpointAsync();
            Assert.Equal(1UL, checkpoint.StateVersion.Value);
            Assert.Equal(TableOperationStatus.Accepted,
                (await h.SubmitAsync(h.Envelope(chain[1].Command, 1, "tail"))).Status);

            if (corrupt)
            {
                TableEventRecord[] history = TableCommittedHistoryCodec.Decode(
                    checkpoint.CommittedHistoryPayload!).ToArray();
                int index = Array.FindIndex(history, e => e.Type == RoundEventType.TileDiscarded);
                Assert.True(index >= 0);
                TableEventRecord original = history[index];
                Assert.True(original.Tile.HasValue);
                history[index] = new TableEventRecord(original.Seq, original.CommandVersion,
                    original.Ordinal, original.Type, original.Seat,
                    new TileId((original.Tile!.Value.Value + 1) % 136), original.Detail,
                    original.EventSchema, original.Payload);
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
            if (!corrupt)
            {
                Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
                return;
            }

            Assert.False(result.Recovered, "Checkpoint's fake discard tile reached Serving despite the intact Domain event/hash.");
            Assert.Null(sink.Installed);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AbortiveDrawTenpaiFactsMustMatchCommittedOutcome(bool corrupt)
        {
            var sink = new RecordingRecoveryStateSink();
            await using DurableHarness h = await DurableHarness.CreateAsync(
                projectionSource: DurableHarness.TerminalFactsSource(), recoverySink: sink);
            RoundStateMachine round = h.InitialRound;
            var settlement = new RoundSettlement("abort-result", RoundOutcome.AbortiveDraw("NINE_TERMINALS"),
                new long[] { 25000, 25000, 25000, 25000 }, 0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)), 1234L);
            Assert.Equal(TableOperationStatus.Accepted, (await h.SubmitAsync(h.Envelope(
                RoundCommand.AbortiveDraw(round.RoundId, settlement), 0, "abort", api: "ABORTIVE_DRAW"))).Status);

            if (corrupt)
            {
                TableCommittedFactsSnapshot stored = (await h.Store.LoadCommittedFactsAsync(
                    DurableHarness.Room, TestContext.Current.CancellationToken))!;
                TableProjectionContextData good = TableProjectionFactsCodec.Decode(
                    stored.Payload, round.RoundId, new StateVersion(1));
                TableProjectionEventFact[] facts = good.EventFacts.ToArray();
                int index = Array.FindIndex(facts, value => value.Type == ProtocolEventType.DrawGame);
                Assert.True(index >= 0);
                TableProjectionEventFact original = facts[index];
                Assert.Empty(original.Facts.TenpaiPlayers!);
                facts[index] = new TableProjectionEventFact(original.Type, original.EventSeq,
                    original.CommandVersion, original.EventOrdinal,
                    new TableProjectionEventFacts(turnNumber: original.Facts.TurnNumber,
                        tenpaiPlayers: new[] { 0 }));
                byte[] payload = TableProjectionFactsCodec.Encode(new TableProjectionContextData(
                    good.RoundId, good.StateVersion, good.SessionVersion, good.Cheat,
                    good.PublicFacts, good.PrivateFacts, good.Session, good.Policy,
                    facts, good.ConcealedMeldFacts));
                h.Store.SetCommittedFactsForTest(DurableHarness.Room, payload, TablePayloadSha256.Compute(payload));
            }

            TableRecoveryResult result = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            if (!corrupt)
            {
                Assert.True(result.Recovered, result.ReasonCode + ": " + result.Detail);
                return;
            }

            Assert.False(result.Recovered, "Abortive draw with contradictory tenpai facts reached Serving.");
            Assert.Null(sink.Installed);
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
        }
    }
}
