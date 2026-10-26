using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP34Round24ReviewProbes
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FenceExpiryClosesStreamsAndPreservesPreviouslyCommittedReceipt(bool rejected)
        {
            var ct = TestContext.Current.CancellationToken;
            await using DurableHarness h = await DurableHarness.CreateAsync(streamOptions: new TableStreamOptions());
            var chain = P31Fixtures.BuildDiscardChain(h.InitialRound, 2).ToList();
            TableCommandEnvelope original = h.Envelope(chain[0].Command, 0, "committed-before-expiry");
            TableOperationResult committed = await h.SubmitAsync(original);
            Assert.Equal(TableOperationStatus.Accepted, committed.Status);

            var streams = new[] { new RecordingStreamSink(), new RecordingStreamSink() };
            var subscriptions = new TableStreamControlResult[2];
            for (int i = 0; i < 2; i++)
            {
                subscriptions[i] = await h.Actor.SubscribeStreamAsync(new TableStreamSubscribeRequest(
                    new TableStreamIdentity(new SessionId("fence-expiry-" + i),
                        i == 0 ? TableStreamAudience.ForSeat(0) : TableStreamAudience.Spectator,
                        new BindingEpoch(1), new SyncEpoch(1)),
                    new OwnerEpoch(1), 0L, streams[i], new RoundId(h.InitialRound.RoundId),
                    i == 0 ? new ControlEpoch(0) : (ControlEpoch?)null), ct);
                Assert.NotNull(subscriptions[i].Handle);
                await streams[i].WaitForCountAsync(1);
            }

            int events = h.Store.EventCount;
            int receipts = h.Store.ReceiptCount;
            int commits = h.Store.CommitCount;
            var next = rejected ? chain[0].Command : chain[1].Command;
            h.Store.AfterScopeCheckForTest = () => h.Clock.Now += 600_001L;
            TableOperationResult expired = await h.SubmitAsync(h.Envelope(next, 1, "expired-at-fence"));
            h.Store.AfterScopeCheckForTest = null;
            Assert.Equal(TableOperationStatus.Failed, expired.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, expired.ReasonCode);
            Assert.Equal(TableActorLifecycle.LeaseLost, (await h.Actor.GetSnapshotAsync(ct)).Lifecycle);

            for (int i = 0; i < 2; i++)
            {
                TableStreamControlResult sync = await h.Actor.SyncStreamAsync(
                    new TableStreamSyncRequest(subscriptions[i].Handle!, subscriptions[i].Cursor!.Value), ct);
                Assert.Equal(TableStreamControlStatus.Closed, sync.Status);
                Assert.Equal(1, streams[i].FrameCount);
            }

            TableOperationResult blocked = await h.SubmitAsync(h.Envelope(next, 1, "after-lease-loss"));
            Assert.Equal(TableOperationStatus.Rejected, blocked.Status);
            Assert.Equal(TableRuntimeReasonCode.LeaseLost, blocked.ReasonCode);
            Assert.Equal(events, h.Store.EventCount);
            Assert.Equal(receipts, h.Store.ReceiptCount);
            Assert.Equal(commits, h.Store.CommitCount);
            Assert.Single(h.Sink.Publications, value => value.Kind == TablePublicationKind.LeaseLost);
            Assert.Single(h.Sink.Publications, value => value.Kind == TablePublicationKind.Applied);

            ReceiptLookupResult lookup = await h.Actor.QueryReceiptAsync(
                DurableHarness.Account, h.Scope.ScopeId, original.RequestId, ct);
            Assert.True(lookup.IsDefinitive);
            TableOperationResult replay = await h.SubmitAsync(original);
            Assert.Equal(TableOperationStatus.Replayed, replay.Status);
            Assert.NotNull(replay.DurableReceipt);
            Assert.Equal(committed.DurableReceipt!.Receipt.Status, replay.DurableReceipt!.Receipt.Status);
            Assert.Equal(committed.CommandVersion, replay.CommandVersion);
            Assert.Equal(committed.DurableReceipt.PayloadSha256, replay.DurableReceipt.PayloadSha256);
            Assert.Equal(committed.DurableReceipt.CreatedAtUtcMs, replay.DurableReceipt.CreatedAtUtcMs);
            Assert.Equal(events, h.Store.EventCount);
            Assert.Equal(receipts, h.Store.ReceiptCount);
            Assert.Equal(commits, h.Store.CommitCount);
        }
    }
}
