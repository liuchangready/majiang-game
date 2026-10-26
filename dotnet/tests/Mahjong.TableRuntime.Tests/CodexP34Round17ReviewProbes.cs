using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round17ReviewProbes
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task LegitimateRecoveredControlMustRemainCheckpointable(bool disconnected, bool crossBoot)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness source = await DurableHarness.CreateAsync();
            Assert.True((await source.Actor.ConfigureControlPolicyAsync(
                new DisconnectPolicy(DisconnectMode.TimeoutOnly), new OwnerEpoch(1), "probe", ct)).IsChanged);
            if (disconnected)
            {
                Assert.True((await source.Actor.DisconnectAsync(
                    0, new OwnerEpoch(1), new ControlEpoch(0), "probe", ct)).IsChanged);
            }

            TableCheckpoint before = await source.SaveCheckpointAsync();
            source.Clock.Now += 250L;
            await using DurableHarness recovered = DurableHarness.Attach(source, DurableHarness.Owner, 1,
                boot: crossBoot ? "boot-2" : DurableHarness.Boot);
            TableRecoveryResult recovery = await recovered.Actor.RecoverFromStoreAsync(ct);
            Assert.True(recovery.Recovered, recovery.ReasonCode + ": " + recovery.Detail);
            TableOperationResult saved = await recovered.Actor.SaveCheckpointAsync(ct);
            Assert.True(saved.Status == TableOperationStatus.Configured,
                "Legitimate recovered control cannot be saved: disconnected=" + disconnected +
                "; crossBoot=" + crossBoot + "; status=" + saved.Status +
                "; reason=" + saved.ReasonCode + ": " + saved.Detail);
            TableCheckpoint after = (await source.Store.LoadCheckpointAsync(DurableHarness.Room, ct))!;
            Assert.Equal(before.ControlVersion, after.ControlVersion);
            Assert.Equal(TableControlCheckpointCodec.Decode(before.ControlPayload).Snapshot.Seats[0].ControlEpoch,
                TableControlCheckpointCodec.Decode(after.ControlPayload).Snapshot.Seats[0].ControlEpoch);
            Assert.True((await recovered.Actor.RecoverFromStoreAsync(ct)).Recovered);
        }
    }
}
