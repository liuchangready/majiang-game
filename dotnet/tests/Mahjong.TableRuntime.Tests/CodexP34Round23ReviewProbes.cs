using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round23ReviewProbes
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ExpiredLeaseAtCommitFenceMustStopServing(bool rejectedCommand, bool afterScope)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness h = await DurableHarness.CreateAsync();
            RoundCommand command = P31Fixtures.DiscardCurrent(h.InitialRound);
            if (rejectedCommand)
            {
                int wrongSeat = (h.InitialRound.CurrentSeat + 1) % 4;
                command = RoundCommand.Discard(h.InitialRound.RoundId, wrongSeat,
                    h.InitialRound.Tiles.TilesInZone(TileZone.Hand, wrongSeat)[0]);
            }

            // Domain admission has completed; expiry is discovered by the transaction fence.
            if (afterScope)
                h.Store.AfterScopeCheckForTest = () => h.Clock.Now += 600_001L;
            else
                h.Store.BeforeScopeCheckForTest = () => h.Clock.Now += 600_001L;

            TableOperationResult result = await h.SubmitAsync(h.Envelope(command, 0, "expired-owner"));
            h.Store.BeforeScopeCheckForTest = null;
            h.Store.AfterScopeCheckForTest = null;

            Assert.Equal(TableOperationStatus.Failed, result.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, result.ReasonCode);
            Assert.Equal(0, h.Store.ReceiptCount);
            Assert.Equal(0, h.Store.EventCount);
            TableSnapshot snapshot = await h.Actor.GetSnapshotAsync(ct);
            Assert.True(snapshot.Lifecycle != TableActorLifecycle.Serving,
                "Expired lease returned Failed but actor still serves; lifecycle=" + snapshot.Lifecycle);
        }
    }
}
