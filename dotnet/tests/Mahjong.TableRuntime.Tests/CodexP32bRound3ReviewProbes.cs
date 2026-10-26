using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP32bRound3ReviewProbes
    {
        [Fact]
        public async Task ClosedSinkDoesNotRetainActiveStreamCapacity()
        {
            await using var harness = StreamHarness.Create(new TableStreamOptions { MaxStreams = 1 });
            var first = await harness.SubscribeSpectatorAsync("failed-session", 1, 1, new FailingSink());
            Assert.Equal(TableStreamControlStatus.Subscribed, first.Status);
            await harness.Terminations.WaitForAsync(item => item.Identity.SessionId.Value == "failed-session");

            // No command/control publication or Sync of the dead handle is required to
            // admit a new independent session after its predecessor has terminated.
            var sink = new RecordingStreamSink();
            var next = await harness.SubscribeSpectatorAsync("replacement-session", 1, 1, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, next.Status);
            await sink.WaitForCountAsync(1);
        }

        [Fact]
        public async Task OpenDecisionRefreshesExistingSeatStream()
        {
            await using var harness = StreamHarness.Create();
            int seat = harness.InitialRound.CurrentSeat;
            var sink = new RecordingStreamSink();
            var subscription = await harness.SubscribeSeatAsync(seat, "owner", 1, 1, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscription.Status);
            var before = await sink.WaitForCountAsync(1);
            Assert.Null(before[0].Player!.Private.Decision);
            Assert.Equal(TableOperationStatus.Configured, (await harness.Actor.OpenDecisionAsync(
                CreateDecision(harness), TestContext.Current.CancellationToken)).Status);

            var after = await sink.WaitForCountAsync(2, 1500);
            Assert.NotNull(after[1].Player!.Private.Decision);
            Assert.Equal("round3-decision", after[1].Player!.Private.Decision!.DecisionId.Value);
            Assert.Equal(before[0].StateVersion, after[1].StateVersion);
            Assert.Equal(before[0].ProjectionSeq.Next(), after[1].ProjectionSeq);
        }

        [Fact]
        public async Task SyncCannotReportInSyncWithMissingDecision()
        {
            await using var harness = StreamHarness.Create();
            int seat = harness.InitialRound.CurrentSeat;
            var sink = new RecordingStreamSink();
            var subscription = await harness.SubscribeSeatAsync(seat, "owner", 1, 1, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscription.Status);
            var before = await sink.WaitForCountAsync(1);
            Assert.Equal(TableOperationStatus.Configured, (await harness.Actor.OpenDecisionAsync(
                CreateDecision(harness), TestContext.Current.CancellationToken)).Status);

            // The old baseline predates the new authoritative decision. It cannot be
            // acknowledged as current solely because state/control versions did not move.
            var sync = await harness.SyncAsync(subscription.Handle!, before[0].Cursor);
            Assert.Equal(TableStreamControlStatus.BaselineQueued, sync.Status);
            var after = await sink.WaitForCountAsync(2, 1500);
            Assert.Contains(after, frame => frame.Player!.Private.Decision != null);
        }

        private static PendingDecision CreateDecision(StreamHarness harness)
        {
            int seat = harness.InitialRound.CurrentSeat;
            var tile = harness.InitialRound.Tiles.TilesInZone(TileZone.Hand, seat)[0];
            return new PendingDecision(
                new DecisionKey(harness.InitialRound.RoundId, seat, "round3-decision"),
                0L, new[] { new LegalActionOption(LegalActionKind.Discard, seat, tile) });
        }

        private sealed class FailingSink : ITableStreamSink
        {
            public ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
                => throw new IOException("controlled current-generation sink failure");
        }
    }
}
