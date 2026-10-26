using System;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34ReviewProbes
    {
        private static PendingDecision Decision(DurableHarness h) => new PendingDecision(
            new DecisionKey(h.InitialRound.RoundId, 0, "review-decision"), 0L,
            h.InitialRound.Tiles.TilesInZone(TileZone.Hand, 0)
                .Select(tile => new LegalActionOption(LegalActionKind.Discard, 0, tile)).ToArray(), null);

        [Fact]
        public async Task RelativeRemainingTimeMustNotGainDowntimeTwice()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h), TestContext.Current.CancellationToken)).Status);
            TableCheckpoint checkpoint = await h.SaveCheckpointAsync();
            long remaining = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload)
                .Decisions[0].RemainingBaseMillis;
            h.Clock.Now += 60_000L;
            var service = new TableRecoveryService(h.Store, h.Store, h.Store, h.Clock);
            TableRecoveryResult recovered = await service.RecoverAsync(DurableHarness.Room,
                new TableRecoveryOptions(DurableHarness.Owner, "review-boot", 1,
                    TimeSpan.FromMinutes(10), graceMillis: 500L), TestContext.Current.CancellationToken);
            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(remaining + 500L, Assert.Single(recovered.Baseline!.Timing.Decisions).RemainingBaseMillis);
        }

        [Fact]
        public async Task ReplayMustKeepReserveConsumedAfterCheckpoint()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync(configure: options =>
                options.DecisionClockConfig = new DecisionClockConfig(100L, 1000L, ReserveScope.Round));
            Assert.Equal(TableOperationStatus.Configured,
                (await h.Actor.OpenDecisionAsync(Decision(h), TestContext.Current.CancellationToken)).Status);
            await h.SaveCheckpointAsync();
            h.Clock.Now += 500L;
            Assert.Equal(TableOperationStatus.Accepted, (await h.SubmitAsync(h.Envelope(
                P31Fixtures.DiscardCurrent(h.InitialRound), 0, "review-reserve",
                decisionId: "review-decision"))).Status);
            long expected = (await h.Actor.GetTimingSnapshotAsync(TestContext.Current.CancellationToken)).Seats[0].RemainingReserveMillis;
            Assert.Equal(600L, expected);
            var service = new TableRecoveryService(h.Store, h.Store, h.Store, h.Clock);
            TableRecoveryResult recovered = await service.RecoverAsync(DurableHarness.Room,
                new TableRecoveryOptions(DurableHarness.Owner, "review-boot", 1,
                    TimeSpan.FromMinutes(10), graceMillis: 0L), TestContext.Current.CancellationToken);
            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(expected, recovered.Baseline!.Timing.SeatReservesMillis![0]);
        }

        [Fact]
        public async Task OldOwnerMustNotOverwriteNewOwnersControlCheckpoint()
        {
            await using DurableHarness old = await DurableHarness.CreateAsync();
            await using DurableHarness current = DurableHarness.Attach(old, DurableHarness.Owner, 1UL);
            Assert.True((await current.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken)).Recovered);
            TableControlResult changed = await current.Actor.RequestTakeoverAsync(
                1, new OwnerEpoch(2), new ControlEpoch(0), "review", TestContext.Current.CancellationToken);
            Assert.Equal(TableControlStatus.Changed, changed.Status);
            Assert.Equal(1L, (await old.Store.LoadCheckpointAsync(DurableHarness.Room,
                TestContext.Current.CancellationToken))!.ControlVersion);
            await old.Actor.SaveCheckpointAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1L, (await old.Store.LoadCheckpointAsync(DurableHarness.Room,
                TestContext.Current.CancellationToken))!.ControlVersion);
        }
    }
}
