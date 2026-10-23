using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP32bReviewProbes
    {
        [Fact]
        public async Task ForeignActorHandleCannotUnsubscribeLocalStream()
        {
            await using var first = StreamHarness.Create();
            await using var second = StreamHarness.Create();
            var foreign = await first.SubscribeSeatAsync(0, "foreign", 1, 1, new RecordingStreamSink());
            var local = await second.SubscribeSpectatorAsync("local", 1, 1, new RecordingStreamSink());
            var result = await second.UnsubscribeAsync(foreign.Handle!);
            Assert.Equal(TableStreamControlStatus.Rejected, result.Status);
            Assert.Equal(TableStreamControlStatus.InSync,
                (await second.SyncAsync(local.Handle!, local.Cursor!.Value)).Status);
        }

        [Fact]
        public async Task ForeignSeatHandleCannotSyncSpectatorIntoPrivateFrame()
        {
            await using var first = StreamHarness.Create();
            await using var second = StreamHarness.Create();
            var foreign = await first.SubscribeSeatAsync(0, "foreign", 1, 1, new RecordingStreamSink());
            var sink = new RecordingStreamSink();
            await second.SubscribeSpectatorAsync("local", 1, 1, sink);
            await sink.WaitForCountAsync(1);
            var result = await second.SyncAsync(foreign.Handle!, StreamCursor.Empty);
            Assert.Equal(TableStreamControlStatus.Rejected, result.Status);
            Assert.All(sink.Frames, frame => Assert.Null(frame.Player));
        }

        [Fact]
        public async Task RecoveryDropsQueuedOldGenerationBeforeNewBaseline()
        {
            await using var harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();
            sink.BlockNext();
            try
            {
                await harness.SubscribeSpectatorAsync("local", 1, 1, sink);
                await sink.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                var command = harness.DiscardChain(1)[0];
                Assert.Equal(TableOperationStatus.Accepted,
                    (await harness.Actor.SubmitAsync(command, TestContext.Current.CancellationToken)).Status);
                var transition = harness.InitialRound.Execute(command.Command);
                await harness.Actor.BeginRecoveryAsync("caller probe", TestContext.Current.CancellationToken);
                await harness.Actor.CompleteRecoveryAsync(new TableRecoveryBaseline(
                    transition.State!, new StateVersion(1),
                    new EventSeq((ulong)transition.Events.Count), new OwnerEpoch(1)), TestContext.Current.CancellationToken);
                sink.Release();
                var frames = await sink.WaitForCountAsync(2);
                // The call already in flight cannot be recalled. The next call must be the
                // recovered baseline, never an old update still sitting in the outbox.
                Assert.True(frames[1].IsFullSnapshot);
                Assert.Equal(1L, frames[1].RecoveryGeneration);
            }
            finally
            {
                sink.Release();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DownstreamTerminationAndCancellationCallbacksCannotBlockActor(bool cancellationCallback)
        {
            using var release = new ManualResetEventSlim(false);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = new TableStreamOptions();
            if (!cancellationCallback)
                options.TerminationSink = new BlockingTermination(entered, release);
            await using var harness = StreamHarness.Create(options);
            var sink = cancellationCallback
                ? (ITableStreamSink)new BlockingCancellation(entered, release)
                : new RecordingStreamSink();
            var subscription = await harness.SubscribeSpectatorAsync("local", 1, 1, sink);
            if (sink is BlockingCancellation blocking)
                await blocking.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task<TableStreamControlResult>? unsubscribe = null;
            bool actorResponded;
            try
            {
                unsubscribe = harness.UnsubscribeAsync(subscription.Handle!).AsTask();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                var snapshot = harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken).AsTask();
                actorResponded = await Task.WhenAny(snapshot, Task.Delay(500, TestContext.Current.CancellationToken)) == snapshot;
            }
            finally
            {
                release.Set();
                if (unsubscribe != null) await unsubscribe.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            Assert.True(actorResponded, "A downstream callback blocked the actor mailbox consumer.");
        }

        private sealed class BlockingTermination : ITableStreamTerminationSink
        {
            private readonly TaskCompletionSource<bool> _entered;
            private readonly ManualResetEventSlim _release;
            public BlockingTermination(TaskCompletionSource<bool> entered, ManualResetEventSlim release)
            { _entered = entered; _release = release; }
            public void OnStreamTerminated(TableStreamTermination termination)
            { _entered.TrySetResult(true); _release.Wait(); }
        }

        private sealed class BlockingCancellation : ITableStreamSink
        {
            private readonly TaskCompletionSource<bool> _entered;
            private readonly ManualResetEventSlim _release;
            public TaskCompletionSource<bool> Ready { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public BlockingCancellation(TaskCompletionSource<bool> entered, ManualResetEventSlim release)
            { _entered = entered; _release = release; }
            public async ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
            {
                using var registration = cancellationToken.Register(() =>
                { _entered.TrySetResult(true); _release.Wait(); });
                Ready.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
}
