using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round19ReviewProbes
    {
        [Theory]
        [InlineData(250L)]
        [InlineData(20000L)]
        public async Task SameBootDefaultGraceMustRecoverWithoutInventingOrLosingItsDeadline(long elapsed)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness source = await DurableHarness.CreateAsync();
            Assert.True((await source.Actor.DisconnectAsync(
                0, new OwnerEpoch(1), new ControlEpoch(0), "review", ct)).IsChanged);
            TableCheckpoint saved = await source.SaveCheckpointAsync();
            var persistedSeat = TableControlCheckpointCodec.Decode(saved.ControlPayload).Snapshot.Seats[0];
            Assert.True(persistedSeat.GraceDeadlineMillis.HasValue);
            source.Clock.Now += elapsed;

            await using DurableHarness recovered = DurableHarness.Attach(
                source, DurableHarness.Owner, 1, boot: DurableHarness.Boot);
            TableRecoveryResult result = await recovered.Actor.RecoverFromStoreAsync(ct);

            Assert.True(result.Recovered,
                "Trusted same-boot default grace failed recovery: elapsed=" + elapsed +
                "; reason=" + result.ReasonCode + "; detail=" + result.Detail);
            Assert.Equal(TableOperationStatus.Configured,
                (await recovered.Actor.SaveCheckpointAsync(ct)).Status);
            TableCheckpoint after = (await source.Store.LoadCheckpointAsync(DurableHarness.Room, ct))!;
            var restoredSeat = TableControlCheckpointCodec.Decode(after.ControlPayload).Snapshot.Seats[0];
            Assert.Equal(persistedSeat.ControlEpoch, restoredSeat.ControlEpoch);
            Assert.Equal(saved.ControlVersion, after.ControlVersion);
            if (elapsed == 250L)
            {
                Assert.Equal(persistedSeat.GraceDeadlineMillis, restoredSeat.GraceDeadlineMillis);
            }
            else
            {
                Assert.True(restoredSeat.GraceDeadlineMillis <= source.Clock.Now,
                    "An overdue grace must remain overdue after recovery.");
            }
            Assert.True((await recovered.Actor.RecoverFromStoreAsync(ct)).Recovered);
        }
    }
}
