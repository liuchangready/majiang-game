using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class CodexP32bRound2ReviewProbes
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OldGenerationSinkFailureCannotDiscardRecoveredBaseline(bool cancelled)
        {
            await using var harness = StreamHarness.Create();
            var sink = new FailOldGenerationSink(cancelled);
            try
            {
                var subscription = await harness.SubscribeSpectatorAsync("local", 1, 1, sink);
                Assert.Equal(TableStreamControlStatus.Subscribed, subscription.Status);
                await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Configured,
                    (await harness.Actor.BeginRecoveryAsync("round2 stale sink failure", TestContext.Current.CancellationToken)).Status);
                Assert.Equal(TableOperationStatus.Configured,
                    (await harness.Actor.CompleteRecoveryAsync(new TableRecoveryBaseline(
                        harness.InitialRound, new StateVersion(0), new EventSeq(0), new OwnerEpoch(1)),
                        TestContext.Current.CancellationToken)).Status);

                // The recovered generation's full baseline is queued before the old sink
                // invocation completes. That stale completion must not close its outbox.
                sink.Release.TrySetResult(true);
                var frames = await sink.Current.WaitForCountAsync(1, 1500);
                Assert.True(frames[0].IsFullSnapshot);
                Assert.Equal(1L, frames[0].RecoveryGeneration);
                Assert.Empty(harness.Terminations.Terminations);
                Assert.Equal(TableStreamControlStatus.BaselineQueued,
                    (await harness.SyncAsync(subscription.Handle!, StreamCursor.Empty)).Status);
            }
            finally
            {
                sink.Release.TrySetResult(true);
            }
        }

        private sealed class FailOldGenerationSink : ITableStreamSink
        {
            private readonly bool _cancelled;
            public FailOldGenerationSink(bool cancelled) => _cancelled = cancelled;
            public TaskCompletionSource<bool> Entered { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public RecordingStreamSink Current { get; } = new RecordingStreamSink();

            public async ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
            {
                if (frame.RecoveryGeneration == 0)
                {
                    Entered.TrySetResult(true);
                    await Release.Task;
                    if (_cancelled) throw new OperationCanceledException("stale generation sink cancelled");
                    throw new IOException("stale generation sink failed");
                }
                await Current.PublishAsync(frame, cancellationToken);
            }
        }
    }
}
