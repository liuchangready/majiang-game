using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round20ReviewProbes
    {
        [Theory]
        [InlineData(250L, 1L)]
        [InlineData(250L, 10000L)]
        [InlineData(20000L, 1L)]
        public async Task RecoveredGraceCheckpointMustSurviveOrdinaryTimePassing(
            long elapsedBeforeRecovery, long elapsedAfterRecovery)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness source = await DurableHarness.CreateAsync();
            Assert.True((await source.Actor.DisconnectAsync(
                0, new OwnerEpoch(1), new ControlEpoch(0), "review", ct)).IsChanged);
            TableCheckpoint saved = await source.SaveCheckpointAsync();
            source.Clock.Now += elapsedBeforeRecovery;
            await using DurableHarness recovered = DurableHarness.Attach(
                source, DurableHarness.Owner, 1, boot: DurableHarness.Boot);
            TableRecoveryResult recovery = await recovered.Actor.RecoverFromStoreAsync(ct);
            Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);

            // No control transition, tick, or owner change: only ordinary elapsed time.
            source.Clock.Now += elapsedAfterRecovery;
            TableOperationResult checkpoint = await recovered.Actor.SaveCheckpointAsync(ct);
            Assert.True(checkpoint.Status == TableOperationStatus.Configured,
                "A valid recovered grace became uncheckpointable after time passed: before=" +
                elapsedBeforeRecovery + "; after=" + elapsedAfterRecovery +
                "; status=" + checkpoint.Status + "; reason=" + checkpoint.ReasonCode +
                ": " + checkpoint.Detail);

            TableCheckpoint after = (await source.Store.LoadCheckpointAsync(DurableHarness.Room, ct))!;
            Assert.Equal(saved.ControlVersion, after.ControlVersion);
            var seat = TableControlCheckpointCodec.Decode(after.ControlPayload).Snapshot.Seats[0];
            Assert.Equal(0L, seat.ControlEpoch);
            long originalDeadline = TableControlCheckpointCodec.Decode(saved.ControlPayload)
                .Snapshot.Seats[0].GraceDeadlineMillis!.Value;
            if (originalDeadline <= source.Clock.Now)
            {
                Assert.True(seat.GraceDeadlineMillis <= source.Clock.Now);
            }
            else
            {
                Assert.Equal(originalDeadline, seat.GraceDeadlineMillis);
            }
            Assert.True((await recovered.Actor.RecoverFromStoreAsync(ct)).Recovered);
        }
    }
}
