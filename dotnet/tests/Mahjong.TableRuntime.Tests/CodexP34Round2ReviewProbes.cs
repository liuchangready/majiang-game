using System;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round2ReviewProbes
    {
        private sealed class CallbackRecoverySink : ITableRecoveryStateSink
        {
            private readonly Action _install;
            public CallbackRecoverySink(Action install) => _install = install;
            public void Install(TableRecoveryAuxState state) => _install();
        }

        [Fact]
        public async Task CrossBootRecoveryWithOpenDecisionMustInstallExplicitGrace()
        {
            await using DurableHarness first = await DurableHarness.CreateAsync();
            var decision = new PendingDecision(
                new DecisionKey(first.InitialRound.RoundId, 0, "cross-boot-install"), 0L,
                first.InitialRound.Tiles.TilesInZone(TileZone.Hand, 0)
                    .Select(tile => new LegalActionOption(LegalActionKind.Discard, 0, tile)).ToArray(), null);
            Assert.Equal(TableOperationStatus.Configured,
                (await first.Actor.OpenDecisionAsync(decision, TestContext.Current.CancellationToken)).Status);
            await first.SaveCheckpointAsync();
            first.Clock.Now += 60_000L;

            var durable = new TableDurableContext(first.Store, first.Store, first.Store, first.Store,
                first.Scopes, DurableHarness.Owner, TimeSpan.FromMinutes(10), "boot-2", 1, DurableHarness.Room)
            {
                RecoveryStateSink = new RecordingRecoveryStateSink()
            };
            var options = new TableActorOptions
            {
                RoomId = DurableHarness.Room,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman, SeatControllerKind.RemoteHuman, SeatControllerKind.RemoteHuman),
                InitialOwnerEpoch = new OwnerEpoch(1)
            };
            var legacy = new RecordingCommitPort();
            await using var actor = new TableActor(options, first.InitialRound, legacy, first.Clock,
                new RecordingPublicationSink(legacy), null, null, null, durable);

            TableRecoveryResult recovered = await actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(TableActorLifecycle.Serving,
                (await actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
            TableTimingSnapshot timing = await actor.GetTimingSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(first.Clock.Now + 6000L, timing.Seats[0].Timing!.DeadlineMillis);
        }

        [Fact]
        public async Task LeaseExpiringDuringHostInstallMustNotEnterServing()
        {
            await using DurableHarness h = await DurableHarness.CreateAsync();
            h.Durable.RecoveryStateSink = new CallbackRecoverySink(() => h.Clock.Now += 600_001L);

            TableRecoveryResult recovered = await h.Actor.RecoverFromStoreAsync(TestContext.Current.CancellationToken);
            TableLeaseSnapshot lease = (await h.Store.GetAsync(DurableHarness.Room,
                TestContext.Current.CancellationToken))!;
            Assert.True(lease.IsExpiredAt((ulong)h.Clock.UtcNowMs));
            Assert.False(recovered.Recovered, "Recovery returned success after the lease expired inside host Install.");
            Assert.NotEqual(TableActorLifecycle.Serving,
                (await h.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).Lifecycle);
        }
    }
}
