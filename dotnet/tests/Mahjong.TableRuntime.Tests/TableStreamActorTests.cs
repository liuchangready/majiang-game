using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    public sealed class TableStreamActorTests
    {
        [Fact]
        public async Task SpectatorBaselineIsExplicitPublicOnlyAndSyncIsInSync()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();

            TableStreamControlResult result = await harness.SubscribeSpectatorAsync("s1", 1UL, 1UL, sink);

            Assert.Equal(TableStreamControlStatus.Subscribed, result.Status);
            Assert.True(result.FullBaseline);
            Assert.NotNull(result.Handle);
            IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(1);
            TableStreamFrame baseline = frames[0];
            StreamAssert.Baseline(baseline, 0UL, 1UL);
            Assert.NotNull(baseline.Public);
            Assert.Null(baseline.Player);
            Assert.Null(baseline.OwnerPrivate);
            Assert.Empty(baseline.Events);
            Assert.Equal(0L, baseline.ControlVersion);
            Assert.Equal(new OwnerEpoch(1), baseline.OwnerEpoch);

            TableStreamControlResult sync = await harness.SyncAsync(result.Handle!, baseline.Cursor);
            Assert.Equal(TableStreamControlStatus.InSync, sync.Status);
            Assert.False(sync.FullBaseline);
        }

        [Fact]
        public async Task FourSeatsAndSpectatorsSeeOnlyTheirOwnPrivateEventAndPayload()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var seats = new RecordingStreamSink[4];
            var handles = new TableStreamHandle[4];
            for (int seat = 0; seat < 4; seat++)
            {
                seats[seat] = new RecordingStreamSink();
                TableStreamControlResult subscribed =
                    await harness.SubscribeSeatAsync(seat, "seat" + seat, 1UL, 1UL, seats[seat]);
                Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
                handles[seat] = subscribed.Handle!;
            }

            var spectatorA = new RecordingStreamSink();
            var spectatorB = new RecordingStreamSink();
            var encoder = new HashingStreamEncoder();
            harness.StreamOptions.Encoder = encoder;
            Assert.Equal(
                TableStreamControlStatus.Subscribed,
                (await harness.SubscribeSpectatorAsync("spec-a", 1UL, 1UL, spectatorA)).Status);
            Assert.Equal(
                TableStreamControlStatus.Subscribed,
                (await harness.SubscribeSpectatorAsync("spec-b", 1UL, 1UL, spectatorB)).Status);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            TableOperationResult applied =
                await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, applied.Status);
            Assert.Equal(new StateVersion(1), applied.CommandVersion);

            for (int seat = 0; seat < 4; seat++)
            {
                IReadOnlyList<TableStreamFrame> frames = await seats[seat].WaitForCountAsync(2);
                TableStreamFrame update = frames[1];
                StreamAssert.Update(update, 1UL, 2UL);
                Assert.NotNull(update.Player);
                Assert.NotNull(update.OwnerPrivate);
                Assert.Equal(seat, update.Player!.Seat);
                Assert.Null(update.Public);
                Assert.Contains(update.Events, item => item.Type == ProtocolEventType.TileDiscarded);
            }

            IReadOnlyList<TableStreamFrame> seat0Frames = seats[0].Frames;
            IReadOnlyList<TableStreamFrame> seat1Frames = seats[1].Frames;
            Assert.DoesNotContain(seat0Frames[1].Events,
                item => item.Type == ProtocolEventType.TileDrawn && item.Seat == 1);
            Assert.Contains(seat1Frames[1].Events,
                item => item.Type == ProtocolEventType.TileDrawn && item.Seat == 1);
            Assert.NotEmpty(seat0Frames[1].Player!.Private.ClosedTiles);
            Assert.NotEmpty(seat1Frames[1].Player!.Private.ClosedTiles);

            IReadOnlyList<TableStreamFrame> spectatorFrames = await spectatorA.WaitForCountAsync(2);
            TableStreamFrame spectatorUpdate = spectatorFrames[1];
            Assert.NotNull(spectatorUpdate.Public);
            Assert.Null(spectatorUpdate.Player);
            Assert.Null(spectatorUpdate.OwnerPrivate);
            Assert.DoesNotContain(spectatorUpdate.Events,
                item => item.Type == ProtocolEventType.TileDrawn);

            IReadOnlyList<TableStreamFrame> spectatorBFrames = await spectatorB.WaitForCountAsync(2);
            byte[] aBytes = spectatorUpdate.EncodedPayload!;
            byte[] bBytes = spectatorBFrames[1].EncodedPayload!;
            Assert.Equal(aBytes, bBytes);
            Assert.Equal(spectatorUpdate.StreamVersion, spectatorBFrames[1].StreamVersion);
            Assert.Equal(spectatorUpdate.ProjectionSeq, spectatorBFrames[1].ProjectionSeq);

            string publicText = Encoding.UTF8.GetString(aBytes);
            Assert.DoesNotContain("ClosedTiles", publicText, StringComparison.Ordinal);
            Assert.DoesNotContain("SleeveTiles", publicText, StringComparison.Ordinal);
            Assert.DoesNotContain("StateFingerprint", publicText, StringComparison.Ordinal);
            Assert.DoesNotContain("SourceFingerprint", publicText, StringComparison.Ordinal);

            string seatText = Encoding.UTF8.GetString(seat0Frames[1].EncodedPayload!);
            Assert.Contains("ClosedTiles", seatText, StringComparison.Ordinal);
            Assert.Contains("SleeveTiles", seatText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SubscriptionQueuedBehindSlowCommitGetsPostCommitBaselineAndNoLostEvent()
        {
            await using StreamHarness harness = StreamHarness.Create();
            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            harness.CommitPort.BlockNext();

            Task<TableOperationResult> submit =
                harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken).AsTask();
            await harness.CommitPort.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var sink = new RecordingStreamSink();
            Task<TableStreamControlResult> subscribe =
                harness.SubscribeSpectatorAsync("late", 1UL, 1UL, sink).AsTask();

            harness.CommitPort.Release();
            TableOperationResult applied = await submit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            TableStreamControlResult result = await subscribe.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, applied.Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, result.Status);

            IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(1);
            Assert.Single(frames);
            TableStreamFrame baseline = frames[0];
            StreamAssert.Baseline(baseline, 1UL, 1UL);
            Assert.NotNull(baseline.Public);
            Assert.Single(baseline.Public!.View.Board!.Players[0].Discards);
        }

        [Fact]
        public async Task SubscriptionBeforeCommitReceivesBaselineThenVisibleUpdate()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("early", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            TableOperationResult applied =
                await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, applied.Status);

            IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(2);
            StreamAssert.Baseline(frames[0], 0UL, 1UL);
            StreamAssert.Update(frames[1], 1UL, 2UL);
            Assert.Contains(frames[1].Events, item => item.Type == ProtocolEventType.TileDiscarded);
        }

        [Fact]
        public async Task FailedCommitPublishesNoStreamFrame()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("s1", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            harness.CommitPort.FailNext();
            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            TableOperationResult failed =
                await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(1, sink.FrameCount);
            Assert.Empty(harness.Terminations.Terminations);
            Assert.Equal(new StateVersion(0), (await harness.Actor.GetSnapshotAsync(
                TestContext.Current.CancellationToken)).StateVersion);
        }

        [Fact]
        public async Task ProjectionFailureAfterCommitKeepsPersistedStateAndIsolatesTheStream()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("s1", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            harness.ProjectionSource.ThrowOnCall = 2;
            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            TableOperationResult applied =
                await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, applied.Status);
            Assert.Single(harness.CommitPort.CommittedRequests);
            Assert.Equal(new StateVersion(1), applied.CommandVersion);

            TableStreamTermination termination = await harness.Terminations.WaitForAsync(
                item => item.Identity.SessionId.Value == "s1");
            Assert.Equal(TableStreamTerminationKind.ProjectionFailed, termination.Kind);
            Assert.False(string.IsNullOrWhiteSpace(harness.Actor.PublicationFault));
            Assert.Equal(1, sink.FrameCount);

            TableSnapshot snapshot =
                await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(1), snapshot.StateVersion);
            Assert.Equal(TableActorLifecycle.Serving, snapshot.Lifecycle);

            harness.ProjectionSource.ThrowOnCall = 0;
            var replacement = new RecordingStreamSink();
            TableStreamControlResult resubscribed =
                await harness.SubscribeSpectatorAsync("s1", 2UL, 1UL, replacement);
            Assert.Equal(TableStreamControlStatus.Subscribed, resubscribed.Status);
            IReadOnlyList<TableStreamFrame> replacementFrames = await replacement.WaitForCountAsync(1);
            StreamAssert.Baseline(replacementFrames[0], 1UL, 2UL);
        }

        [Fact]
        public async Task RecoveryGatesSyncAndReestablishesFullBaseline()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("s1", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            IReadOnlyList<TableStreamFrame> initial = await sink.WaitForCountAsync(1);

            TableOperationResult begin = await harness.Actor.BeginRecoveryAsync(
                "test recovery",
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Configured, begin.Status);

            TableStreamControlResult gated = await harness.SyncAsync(subscribed.Handle!, initial[0].Cursor);
            Assert.Equal(TableStreamControlStatus.Rejected, gated.Status);
            var lateSink = new RecordingStreamSink();
            TableStreamControlResult late =
                await harness.SubscribeSpectatorAsync("late", 1UL, 1UL, lateSink);
            Assert.Equal(TableStreamControlStatus.Rejected, late.Status);

            TableOperationResult complete = await harness.Actor.CompleteRecoveryAsync(
                new TableRecoveryBaseline(
                    harness.InitialRound,
                    new StateVersion(0),
                    new EventSeq(0),
                    new OwnerEpoch(1)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Configured, complete.Status);

            IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(2);
            TableStreamFrame recoveredBaseline = frames[1];
            StreamAssert.Baseline(recoveredBaseline, 0UL, 2UL);
            Assert.Equal(1L, recoveredBaseline.RecoveryGeneration);
            Assert.NotNull(recoveredBaseline.Public);
            Assert.Empty(recoveredBaseline.Events);

            TableStreamControlResult gatedSync = await harness.SyncAsync(
                subscribed.Handle!,
                recoveredBaseline.Cursor);
            Assert.Equal(TableStreamControlStatus.BaselineQueued, gatedSync.Status);
            frames = await sink.WaitForCountAsync(3);
            StreamAssert.Baseline(frames[2], 0UL, 3UL);

            TableStreamControlResult inSync = await harness.SyncAsync(
                subscribed.Handle!,
                frames[2].Cursor);
            Assert.Equal(TableStreamControlStatus.InSync, inSync.Status);
        }

        [Fact]
        public async Task RecoveryAfterCommitRebaselinesTheFinalCommittedState()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("s1", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            TableOperationResult applied =
                await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, applied.Status);
            RoundTransition transition = harness.InitialRound.Execute(chain[0].Command);
            Assert.True(transition.IsAccepted);
            RoundStateMachine post = transition.State!;

            // Delivered-before-recovery ordering: the committed update reaches the sink before
            // BeginRecovery fences the channel. Waiting for the sink is the deterministic
            // synchronization seam; the queued-before-fence ordering is covered by
            // RecoveryDropsQueuedOldUpdateBeforeFenceAndRebaselinesCommittedState below.
            IReadOnlyList<TableStreamFrame> committed = await sink.WaitForCountAsync(2);
            StreamAssert.Update(committed[1], 1UL, 2UL);
            Assert.Single(committed[1].Public!.View.Board!.Players[0].Discards);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.BeginRecoveryAsync(
                    "post-commit",
                    TestContext.Current.CancellationToken)).Status);
            TableOperationResult complete = await harness.Actor.CompleteRecoveryAsync(
                new TableRecoveryBaseline(
                    post,
                    new StateVersion(1),
                    new EventSeq(unchecked((ulong)transition.Events.Count)),
                    new OwnerEpoch(1)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Configured, complete.Status);

            IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(3);
            TableStreamFrame recovered = frames[2];
            StreamAssert.Baseline(recovered, 1UL, 3UL);
            Assert.Single(recovered.Public!.View.Board!.Players[0].Discards);
            Assert.Equal(1L, recovered.RecoveryGeneration);
            Assert.Equal(new StateVersion(1), recovered.StateVersion);
            Assert.Equal(new ProjectionSeq(3), recovered.Cursor.ProjectionSeq);

            // The recovered baseline is deliberately gated until the host syncs. The sync
            // baseline must still carry the final committed state, not an empty update.
            TableStreamControlResult needsSync = await harness.SyncAsync(subscribed.Handle!, recovered.Cursor);
            Assert.Equal(TableStreamControlStatus.BaselineQueued, needsSync.Status);
            IReadOnlyList<TableStreamFrame> synced = await sink.WaitForCountAsync(4);
            StreamAssert.Baseline(synced[3], 1UL, 4UL);
            Assert.Single(synced[3].Public!.View.Board!.Players[0].Discards);
            Assert.Equal(
                TableStreamControlStatus.InSync,
                (await harness.SyncAsync(subscribed.Handle!, synced[3].Cursor)).Status);
        }

        [Fact]
        public async Task RecoveryDropsQueuedOldUpdateBeforeFenceAndRebaselinesCommittedState()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TableStreamChannel? channel = null;
            int armed = 0;
            int blocked = 0;
            var options = new TableStreamOptions
            {
                ObservePumpExit = true,
                ChannelCreated = created => channel ??= created,
                BeforeDispatchClaim = () =>
                {
                    if (Volatile.Read(ref armed) == 1 &&
                        Interlocked.Exchange(ref blocked, 1) == 0)
                    {
                        entered.TrySetResult(true);
                        release.Task.GetAwaiter().GetResult();
                    }
                }
            };
            await using StreamHarness harness = StreamHarness.Create(options);
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("s1", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            IReadOnlyList<TableStreamFrame> initial = await sink.WaitForCountAsync(1);
            StreamAssert.Baseline(initial[0], 0UL, 1UL);

            try
            {
                Volatile.Write(ref armed, 1);
                IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
                TableOperationResult applied =
                    await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Accepted, applied.Status);
                RoundTransition transition = harness.InitialRound.Execute(chain[0].Command);
                Assert.True(transition.IsAccepted);

                // The old update has been dequeued but has not been claimed for the sink. Recovery
                // must fence it so it can never be observed after the new generation baseline.
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(1, sink.FrameCount);
                Assert.NotNull(channel);
                Task pumpExit = channel!.PumpExit;
                Assert.False(pumpExit.IsCompleted, "The old pump must still be paused before its dispatch claim.");

                Assert.Equal(
                    TableOperationStatus.Configured,
                    (await harness.Actor.BeginRecoveryAsync(
                        "queued update fence",
                        TestContext.Current.CancellationToken)).Status);
                Assert.Equal(
                    TableOperationStatus.Configured,
                    (await harness.Actor.CompleteRecoveryAsync(
                        new TableRecoveryBaseline(
                            transition.State!,
                            new StateVersion(1),
                            new EventSeq(unchecked((ulong)transition.Events.Count)),
                            new OwnerEpoch(1)),
                        TestContext.Current.CancellationToken)).Status);

                release.TrySetResult(true);
                // The old pump has now passed TryClaimDispatch (where the fence rejects the
                // stale update) and exited. Only after that barrier may we assert that the sink
                // never observed the old frame and observe the recovered baseline.
                await pumpExit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(2);
                TableStreamFrame recovered = frames[1];
                StreamAssert.Baseline(recovered, 1UL, 3UL);
                Assert.Equal(1L, recovered.RecoveryGeneration);
                Assert.Equal(new StateVersion(1), recovered.StateVersion);
                Assert.Single(recovered.Public!.View.Board!.Players[0].Discards);
                Assert.Equal(2, sink.FrameCount);
            }
            finally
            {
                release.TrySetResult(true);
            }
        }

        [Fact]
        public async Task LeaseLossClosesEveryStreamAndPublishesNoMoreContent()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("s1", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            TableOperationResult leaseLost = await harness.Actor.ReportLeaseLostAsync(
                new OwnerEpoch(1),
                "test lease loss",
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Configured, leaseLost.Status);

            TableStreamTermination termination =
                await harness.Terminations.WaitForAsync(item => item.Kind == TableStreamTerminationKind.LeaseLost);
            Assert.Equal("LEASE_LOST", termination.ReasonCode);
            TableStreamControlResult sync = await harness.SyncAsync(subscribed.Handle!, subscribed.Cursor!.Value);
            Assert.Equal(TableStreamControlStatus.Closed, sync.Status);
            Assert.Equal(1, sink.FrameCount);
        }

        [Fact]
        public async Task NewBindingReplacesOldHandleAndOldHandleCannotCancelOrSyncNewStream()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var firstSink = new RecordingStreamSink();
            var secondSink = new RecordingStreamSink();
            TableStreamControlResult first =
                await harness.SubscribeSeatAsync(0, "s1", 1UL, 1UL, firstSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, first.Status);
            IReadOnlyList<TableStreamFrame> firstFrames = await firstSink.WaitForCountAsync(1);
            StreamAssert.Baseline(firstFrames[0], 0UL, 1UL);

            // Delivered-before-replace ordering: the old baseline is already on the first sink,
            // so the new binding can only displace the identity, never the committed state.
            // The replace-before-old-send ordering is covered by
            // NewBindingBeforeOldBaselineDispatchDropsOldFrameAndNewStreamContinues below.
            TableStreamControlResult second =
                await harness.SubscribeSeatAsync(0, "s1", 2UL, 1UL, secondSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, second.Status);
            IReadOnlyList<TableStreamFrame> secondFrames = await secondSink.WaitForCountAsync(1);
            StreamAssert.Baseline(secondFrames[0], 0UL, 2UL);
            Assert.Equal(
                TableStreamControlStatus.InSync,
                (await harness.SyncAsync(second.Handle!, secondFrames[0].Cursor)).Status);
            Assert.Equal(1, firstSink.FrameCount);

            TableStreamTermination displaced = await harness.Terminations.WaitForAsync(
                item => item.Kind == TableStreamTerminationKind.Displaced);
            Assert.Equal(first.Handle, displaced.Handle);

            TableStreamControlResult oldUnsubscribe = await harness.UnsubscribeAsync(first.Handle!);
            Assert.Equal(TableStreamControlStatus.Rejected, oldUnsubscribe.Status);
            TableStreamControlResult oldSync = await harness.SyncAsync(first.Handle!, first.Cursor!.Value);
            Assert.Equal(TableStreamControlStatus.Rejected, oldSync.Status);

            TableStreamControlResult staleBinding =
                await harness.SubscribeSeatAsync(0, "s1", 1UL, 1UL, new RecordingStreamSink());
            Assert.Equal(TableStreamControlStatus.Rejected, staleBinding.Status);
            Assert.Equal("STALE_STREAM_VERSION", staleBinding.ReasonCode);

            // The old handle was rejected above, so the new stream must still advance and carry
            // the committed state. A stale handle must never cancel or redirect this stream.
            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
            IReadOnlyList<TableStreamFrame> updates = await secondSink.WaitForCountAsync(2);
            StreamAssert.Update(updates[1], 1UL, 3UL);
            Assert.Single(updates[1].Player!.Public.Board!.Players[0].Discards);
            Assert.Equal(
                TableStreamControlStatus.InSync,
                (await harness.SyncAsync(second.Handle!, updates[1].Cursor)).Status);
            Assert.Equal(1, firstSink.FrameCount);

            TableStreamControlResult newUnsubscribe = await harness.UnsubscribeAsync(second.Handle!);
            Assert.Equal(TableStreamControlStatus.Unsubscribed, newUnsubscribe.Status);
            TableStreamControlResult afterUnsubscribe = await harness.SyncAsync(second.Handle!, second.Cursor!.Value);
            Assert.Equal(TableStreamControlStatus.Rejected, afterUnsubscribe.Status);
        }

        [Fact]
        public async Task NewBindingBeforeOldBaselineDispatchDropsOldFrameAndNewStreamContinues()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TableStreamChannel? oldChannel = null;
            int armed = 0;
            int blocked = 0;
            var options = new TableStreamOptions
            {
                ObservePumpExit = true,
                ChannelCreated = created => oldChannel ??= created,
                BeforeDispatchClaim = () =>
                {
                    if (Volatile.Read(ref armed) == 1 &&
                        Interlocked.Exchange(ref blocked, 1) == 0)
                    {
                        entered.TrySetResult(true);
                        release.Task.GetAwaiter().GetResult();
                    }
                }
            };
            await using StreamHarness harness = StreamHarness.Create(options);
            var firstSink = new RecordingStreamSink();
            var secondSink = new RecordingStreamSink();

            try
            {
                Volatile.Write(ref armed, 1);
                TableStreamControlResult first =
                    await harness.SubscribeSeatAsync(0, "s1", 1UL, 1UL, firstSink);
                Assert.Equal(TableStreamControlStatus.Subscribed, first.Status);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(0, firstSink.FrameCount);
                Assert.NotNull(oldChannel);
                Task oldPumpExit = oldChannel!.PumpExit;
                Assert.False(oldPumpExit.IsCompleted, "The old pump must still be paused before its dispatch claim.");

                // The new binding closes the old channel while its baseline is dequeued but
                // before the dispatch claim. The old frame must be dropped, not delivered after
                // replacement.
                TableStreamControlResult second =
                    await harness.SubscribeSeatAsync(0, "s1", 2UL, 1UL, secondSink);
                Assert.Equal(TableStreamControlStatus.Subscribed, second.Status);

                release.TrySetResult(true);
                // Wait until the old pump has passed TryClaimDispatch and exited. Displaced was
                // raised by Close synchronously and does not prove that the old pump ran.
                await oldPumpExit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(0, firstSink.FrameCount);

                IReadOnlyList<TableStreamFrame> secondFrames = await secondSink.WaitForCountAsync(1);
                StreamAssert.Baseline(secondFrames[0], 0UL, 2UL);
                Assert.Equal(
                    TableStreamControlStatus.InSync,
                    (await harness.SyncAsync(second.Handle!, secondFrames[0].Cursor)).Status);

                TableStreamTermination displaced = await harness.Terminations.WaitForAsync(
                    item => item.Kind == TableStreamTerminationKind.Displaced);
                Assert.Equal(first.Handle, displaced.Handle);
                Assert.Equal(
                    TableStreamControlStatus.Rejected,
                    (await harness.UnsubscribeAsync(first.Handle!)).Status);
                Assert.Equal(
                    TableStreamControlStatus.Rejected,
                    (await harness.SyncAsync(first.Handle!, first.Cursor!.Value)).Status);

                IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
                Assert.Equal(
                    TableOperationStatus.Accepted,
                    (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
                IReadOnlyList<TableStreamFrame> updates = await secondSink.WaitForCountAsync(2);
                StreamAssert.Update(updates[1], 1UL, 3UL);
                Assert.Single(updates[1].Player!.Public.Board!.Players[0].Discards);
                Assert.Equal(
                    TableStreamControlStatus.InSync,
                    (await harness.SyncAsync(second.Handle!, updates[1].Cursor)).Status);
                Assert.Equal(0, firstSink.FrameCount);
            }
            finally
            {
                release.TrySetResult(true);
            }
        }

        [Fact]
        public async Task CursorGapAndRoundMismatchQueueFreshBaselineAndStaleSyncIsRejected()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("s1", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
            IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(2);
            TableStreamFrame update = frames[1];
            StreamAssert.Update(update, 1UL, 2UL);

            TableStreamControlResult gap = await harness.SyncAsync(
                subscribed.Handle!,
                new StreamCursor(new StreamVersion(0), new ProjectionSeq(0)));
            Assert.Equal(TableStreamControlStatus.BaselineQueued, gap.Status);
            Assert.True(gap.FullBaseline);
            Assert.Equal("STREAM_GAP", gap.ReasonCode);

            frames = await sink.WaitForCountAsync(3);
            StreamAssert.Baseline(frames[2], 1UL, 3UL);

            TableStreamControlResult wrongRound = await harness.SyncAsync(
                subscribed.Handle!,
                frames[2].Cursor,
                new RoundId("another-round"));
            Assert.Equal(TableStreamControlStatus.BaselineQueued, wrongRound.Status);
            frames = await sink.WaitForCountAsync(4);
            StreamAssert.Baseline(frames[3], 1UL, 4UL);

            TableStreamControlResult wrongSync = await harness.SyncAsync(
                subscribed.Handle!,
                frames[3].Cursor,
                null,
                new SyncEpoch(99UL));
            Assert.Equal(TableStreamControlStatus.Rejected, wrongSync.Status);
        }

        [Fact]
        public async Task PlayerToSpectatorSwitchKeepsSeparateIdentityAndNoPrivateLeak()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var playerSink = new RecordingStreamSink();
            var spectatorSink = new RecordingStreamSink();
            TableStreamControlResult player =
                await harness.SubscribeSeatAsync(2, "same-session", 1UL, 1UL, playerSink);
            TableStreamControlResult spectator =
                await harness.SubscribeSpectatorAsync("same-session", 2UL, 2UL, spectatorSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, player.Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, spectator.Status);
            await playerSink.WaitForCountAsync(1);
            await spectatorSink.WaitForCountAsync(1);

            Assert.Equal(
                TableStreamControlStatus.Unsubscribed,
                (await harness.UnsubscribeAsync(player.Handle!)).Status);
            Assert.Equal(
                TableStreamControlStatus.Rejected,
                (await harness.UnsubscribeAsync(player.Handle!)).Status);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);

            IReadOnlyList<TableStreamFrame> frames = await spectatorSink.WaitForCountAsync(2);
            Assert.Null(frames[1].Player);
            Assert.NotNull(frames[1].Public);
            Assert.Equal(1, playerSink.FrameCount);
            TableStreamControlResult sync = await harness.SyncAsync(spectator.Handle!, frames[1].Cursor);
            Assert.Equal(TableStreamControlStatus.InSync, sync.Status);
        }

        [Fact]
        public async Task SlowSinkDoesNotBlockActorAndOverflowClosesOnlyThatStream()
        {
            var channels = new List<TableStreamChannel>();
            var options = new TableStreamOptions
            {
                MaxQueuedFrames = 2,
                ObservePumpExit = true,
                ChannelCreated = channels.Add
            };
            await using StreamHarness harness = StreamHarness.Create(options);
            var fastSink = new RecordingStreamSink();
            var slowSink = new RecordingStreamSink();
            Assert.Equal(
                TableStreamControlStatus.Subscribed,
                (await harness.SubscribeSpectatorAsync("fast", 1UL, 1UL, fastSink)).Status);
            Assert.Equal(
                TableStreamControlStatus.Subscribed,
                (await harness.SubscribeSpectatorAsync("slow", 1UL, 1UL, slowSink)).Status);
            await fastSink.WaitForCountAsync(1);
            await slowSink.WaitForCountAsync(1);
            Assert.Equal(2, channels.Count);
            TableStreamChannel slowChannel = channels[1];

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(4);

            // Establish the slow sink's blocked dispatch before adding queue pressure. With the
            // only in-flight frame parked inside the sink, the following submissions deterministically
            // fill exactly two queued frames and the fourth closes only the slow stream.
            slowSink.BlockNext();
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
            await slowSink.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task slowPumpExit = slowChannel.PumpExit;
            Assert.False(slowPumpExit.IsCompleted, "The slow pump must still be parked inside the sink.");
            await fastSink.WaitForCountAsync(2);

            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[1], TestContext.Current.CancellationToken)).Status);
            await fastSink.WaitForCountAsync(3);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[2], TestContext.Current.CancellationToken)).Status);
            await fastSink.WaitForCountAsync(4);

            // Queue is now at its two-frame bound; this submission must overflow the slow stream.
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[3], TestContext.Current.CancellationToken)).Status);
            await fastSink.WaitForCountAsync(5);
            Assert.Equal(5, fastSink.FrameCount);

            TableStreamTermination termination = await harness.Terminations.WaitForAsync(
                item => item.Kind == TableStreamTerminationKind.SlowConsumer);
            Assert.Equal(ProtocolErrorCode.SlowConsumer, termination.ReasonCode);
            Assert.Equal(1, slowSink.FrameCount);

            // Release the parked first update. The channel was closed with its queued frames
            // dropped, so only the already-claimed sink call may complete; the pump must then exit.
            slowSink.Release();
            await slowPumpExit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(2, slowSink.FrameCount);
            Assert.Equal(5, fastSink.FrameCount);
        }

        [Fact]
        public async Task ZeroLyingAndOversizedEncodedFramesAreRejectedBeforeRegistration()
        {
            var zeroEncoder = new HashingStreamEncoder
            {
                Override = _ => new TableEncodedFrame(Array.Empty<byte>(), 0)
            };
            await using (StreamHarness zeroHarness = StreamHarness.Create(
                new TableStreamOptions { Encoder = zeroEncoder }))
            {
                var sink = new RecordingStreamSink();
                TableStreamControlResult result =
                    await zeroHarness.SubscribeSpectatorAsync("zero", 1UL, 1UL, sink);
                Assert.Equal(TableStreamControlStatus.Failed, result.Status);
                Assert.Equal(ProtocolErrorCode.MalformedMessage, result.ReasonCode);
                Assert.Equal(0, sink.FrameCount);
            }

            var lyingEncoder = new HashingStreamEncoder
            {
                Override = _ => new TableEncodedFrame(new byte[] { 1, 2, 3, 4 }, 9)
            };
            await using (StreamHarness lyingHarness = StreamHarness.Create(
                new TableStreamOptions { Encoder = lyingEncoder }))
            {
                var sink = new RecordingStreamSink();
                TableStreamControlResult result =
                    await lyingHarness.SubscribeSpectatorAsync("lying", 1UL, 1UL, sink);
                Assert.Equal(TableStreamControlStatus.Failed, result.Status);
                Assert.Equal(ProtocolErrorCode.MalformedMessage, result.ReasonCode);
                Assert.Equal(0, sink.FrameCount);
            }

            var oversizedEncoder = new HashingStreamEncoder
            {
                Override = _ => new TableEncodedFrame(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 8)
            };
            await using (StreamHarness oversizedHarness = StreamHarness.Create(
                new TableStreamOptions { Encoder = oversizedEncoder, MaxQueuedBytes = 4 }))
            {
                var sink = new RecordingStreamSink();
                TableStreamControlResult result =
                    await oversizedHarness.SubscribeSpectatorAsync("big", 1UL, 1UL, sink);
                Assert.Equal(TableStreamControlStatus.Failed, result.Status);
                Assert.Equal(ProtocolErrorCode.MessageTooLarge, result.ReasonCode);
                Assert.Equal(0, sink.FrameCount);
            }

            var throwingEncoder = new HashingStreamEncoder
            {
                Override = _ => throw new InvalidOperationException("injected encoder failure")
            };
            await using (StreamHarness throwingHarness = StreamHarness.Create(
                new TableStreamOptions { Encoder = throwingEncoder }))
            {
                var sink = new RecordingStreamSink();
                TableStreamControlResult result =
                    await throwingHarness.SubscribeSpectatorAsync("throw", 1UL, 1UL, sink);
                Assert.Equal(TableStreamControlStatus.Failed, result.Status);
                Assert.Equal(ProtocolErrorCode.InternalError, result.ReasonCode);
                Assert.Equal(0, sink.FrameCount);
            }
        }

        [Fact]
        public async Task ControlChangeRefreshesSnapshotWithoutChangingStateVersion()
        {
            await using StreamHarness harness = StreamHarness.Create(withControlCommit: true);
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("s1", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            TableControlResult changed = await harness.Actor.ConfigureControlPolicyAsync(
                new DisconnectPolicy(DisconnectMode.TimeoutOnly, 0L),
                new OwnerEpoch(1),
                "test policy",
                TestContext.Current.CancellationToken);
            Assert.Equal(TableControlStatus.Changed, changed.Status);

            IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(2);
            TableStreamFrame refresh = frames[1];
            Assert.False(refresh.IsFullSnapshot);
            Assert.Equal(new StreamVersion(0), refresh.StreamVersion);
            Assert.Equal(new StateVersion(0), refresh.StateVersion);
            Assert.Equal(1L, refresh.ControlVersion);
            Assert.NotNull(refresh.Public);
            Assert.Empty(refresh.Events);
            Assert.Equal(new OwnerEpoch(1), refresh.OwnerEpoch);
        }

        [Fact]
        public async Task ControlEpochChangeClosesTheOldSeatStreamButRefreshesSpectator()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var seatSink = new RecordingStreamSink();
            var spectatorSink = new RecordingStreamSink();
            TableStreamControlResult seat =
                await harness.SubscribeSeatAsync(0, "seat0", 1UL, 1UL, seatSink, 0UL);
            TableStreamControlResult spectator =
                await harness.SubscribeSpectatorAsync("spec", 1UL, 1UL, spectatorSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, seat.Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, spectator.Status);
            await seatSink.WaitForCountAsync(1);
            await spectatorSink.WaitForCountAsync(1);

            TableControlResult takeover = await harness.Actor.RequestTakeoverAsync(
                0,
                new OwnerEpoch(1),
                new ControlEpoch(0),
                "test takeover",
                TestContext.Current.CancellationToken);
            Assert.Equal(TableControlStatus.Changed, takeover.Status);

            TableStreamTermination termination = await harness.Terminations.WaitForAsync(
                item => item.Identity.SessionId.Value == "seat0");
            Assert.Equal(TableStreamTerminationKind.StaleControlEpoch, termination.Kind);

            IReadOnlyList<TableStreamFrame> spectatorFrames = await spectatorSink.WaitForCountAsync(2);
            Assert.False(spectatorFrames[1].IsFullSnapshot);
            Assert.Equal(new StreamVersion(0), spectatorFrames[1].StreamVersion);
            Assert.Equal(1, seatSink.FrameCount);

            var replacementSink = new RecordingStreamSink();
            TableStreamControlResult replacement =
                await harness.SubscribeSeatAsync(0, "seat0", 2UL, 2UL, replacementSink, 1UL);
            Assert.Equal(TableStreamControlStatus.Subscribed, replacement.Status);
            IReadOnlyList<TableStreamFrame> replacementFrames = await replacementSink.WaitForCountAsync(1);
            Assert.True(replacementFrames[0].IsFullSnapshot);
            Assert.Equal(new ProjectionSeq(1), replacementFrames[0].ProjectionSeq);
        }

        [Fact]
        public async Task LateOldSinkCompletionCannotPolluteTheNewRegistration()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var oldSink = new RecordingStreamSink();
            TableStreamControlResult first =
                await harness.SubscribeSeatAsync(0, "s1", 1UL, 1UL, oldSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, first.Status);
            await oldSink.WaitForCountAsync(1);

            oldSink.BlockNext();
            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
            await oldSink.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var newSink = new RecordingStreamSink();
            TableStreamControlResult second =
                await harness.SubscribeSeatAsync(0, "s1", 2UL, 1UL, newSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, second.Status);
            IReadOnlyList<TableStreamFrame> newFrames = await newSink.WaitForCountAsync(1);
            StreamAssert.Baseline(newFrames[0], 1UL, 3UL);

            oldSink.Release();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(1, newSink.FrameCount);
            Assert.Equal(2, oldSink.FrameCount);
        }

        [Fact]
        public async Task ForeignActorHandlesAreRejectedAndLocalStreamKeepsWorking()
        {
            await using StreamHarness first = StreamHarness.Create();
            await using StreamHarness second = StreamHarness.Create();
            var foreign = await first.SubscribeSeatAsync(0, "foreign", 1UL, 1UL, new RecordingStreamSink());
            Assert.Equal(TableStreamControlStatus.Subscribed, foreign.Status);

            var localSink = new RecordingStreamSink();
            TableStreamControlResult local =
                await second.SubscribeSpectatorAsync("local", 1UL, 1UL, localSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, local.Status);
            await localSink.WaitForCountAsync(1);

            TableStreamControlResult foreignUnsubscribe =
                await second.UnsubscribeAsync(foreign.Handle!);
            Assert.Equal(TableStreamControlStatus.Rejected, foreignUnsubscribe.Status);
            TableStreamControlResult foreignSync =
                await second.SyncAsync(foreign.Handle!, StreamCursor.Empty);
            Assert.Equal(TableStreamControlStatus.Rejected, foreignSync.Status);

            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Single(localSink.Frames);
            Assert.Null(localSink.Frames[0].Player);
            Assert.Null(localSink.Frames[0].OwnerPrivate);

            TableStreamControlResult localSync =
                await second.SyncAsync(local.Handle!, local.Cursor!.Value);
            Assert.Equal(TableStreamControlStatus.InSync, localSync.Status);

            IReadOnlyList<TableCommandEnvelope> chain = second.DiscardChain(1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await second.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
            IReadOnlyList<TableStreamFrame> frames = await localSink.WaitForCountAsync(2);
            Assert.NotNull(frames[1].Public);
            Assert.Null(frames[1].Player);
        }

        [Fact]
        public async Task RecoveryPurgesQueuedOldGenerationAndFirstNewDeliveryIsBaseline()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new RecordingStreamSink();
            sink.BlockNext();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("local", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
            RoundTransition transition = harness.InitialRound.Execute(chain[0].Command);
            Assert.True(transition.IsAccepted);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.BeginRecoveryAsync(
                    "recovery queue probe",
                    TestContext.Current.CancellationToken)).Status);

            sink.Release();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(1, sink.FrameCount);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.CompleteRecoveryAsync(
                    new TableRecoveryBaseline(
                        transition.State!,
                        new StateVersion(1),
                        new EventSeq(unchecked((ulong)transition.Events.Count)),
                        new OwnerEpoch(1)),
                    TestContext.Current.CancellationToken)).Status);

            IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(2);
            Assert.True(frames[1].IsFullSnapshot);
            Assert.Equal(new StreamVersion(1), frames[1].StreamVersion);
            // The dropped old update had already consumed seq 2 locally. A full baseline is a
            // reset, so the next emitted baseline may be seq 3; this is not an update gap.
            Assert.Equal(new ProjectionSeq(3), frames[1].ProjectionSeq);
            Assert.Equal(1L, frames[1].RecoveryGeneration);
        }

        [Fact]
        public async Task BlockingTerminationSinkCannotStallLeaseLossOrSnapshot()
        {
            using var release = new ManualResetEventSlim(false);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = new TableStreamOptions
            {
                TerminationSink = new BlockingTerminationSink(entered, release)
            };
            await using StreamHarness harness = StreamHarness.Create(options);
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("local", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            try
            {
                Task<TableOperationResult> leaseLoss = harness.Actor.ReportLeaseLostAsync(
                    new OwnerEpoch(1),
                    "lease termination probe",
                    TestContext.Current.CancellationToken).AsTask();
                Task completed = await Task.WhenAny(
                    leaseLoss,
                    Task.Delay(500, TestContext.Current.CancellationToken));
                Assert.Same(leaseLoss, completed);
                Assert.Equal(TableOperationStatus.Configured, (await leaseLoss).Status);

                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Task<TableSnapshot> snapshot = harness.Actor.GetSnapshotAsync(
                    TestContext.Current.CancellationToken).AsTask();
                Task snapshotCompleted = await Task.WhenAny(
                    snapshot,
                    Task.Delay(500, TestContext.Current.CancellationToken));
                Assert.Same(snapshot, snapshotCompleted);
                Assert.NotNull(await snapshot);
            }
            finally
            {
                release.Set();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DispatchClaimLosesToFenceOrCloseBeforeSink(bool close)
        {
            using var release = new ManualResetEventSlim(false);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int armed = 0;
            int blocked = 0;
            var options = new TableStreamOptions
            {
                BeforeDispatchClaim = () =>
                {
                    if (Volatile.Read(ref armed) == 1 &&
                        Interlocked.Exchange(ref blocked, 1) == 0)
                    {
                        entered.TrySetResult(true);
                        release.Wait();
                    }
                }
            };
            await using StreamHarness harness = StreamHarness.Create(options);
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("local", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            Volatile.Write(ref armed, 1);
            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
            RoundTransition transition = harness.InitialRound.Execute(chain[0].Command);
            Assert.True(transition.IsAccepted);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            if (close)
            {
                await harness.Actor.CloseAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                Assert.Equal(
                    TableOperationStatus.Configured,
                    (await harness.Actor.BeginRecoveryAsync(
                        "dispatch fence probe",
                        TestContext.Current.CancellationToken)).Status);
                Assert.Equal(
                    TableOperationStatus.Configured,
                    (await harness.Actor.CompleteRecoveryAsync(
                        new TableRecoveryBaseline(
                            transition.State!,
                            new StateVersion(1),
                            new EventSeq(unchecked((ulong)transition.Events.Count)),
                            new OwnerEpoch(1)),
                        TestContext.Current.CancellationToken)).Status);
            }

            try
            {
                release.Set();
                if (close)
                {
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                    Assert.Equal(1, sink.FrameCount);
                }
                else
                {
                    IReadOnlyList<TableStreamFrame> frames = await sink.WaitForCountAsync(2);
                    Assert.True(frames[1].IsFullSnapshot);
                    Assert.Equal(1L, frames[1].RecoveryGeneration);
                }
            }
            finally
            {
                release.Set();
            }
        }

        [Fact]
        public async Task OldGenerationCompletionDuringRecoveryAndMultipleRecoveriesRebaseline()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new OldGenerationFailingSink(cancelled: false);
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("local", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.BeginRecoveryAsync(
                    "old completion during recovery",
                    TestContext.Current.CancellationToken)).Status);
            sink.Release.TrySetResult(true);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Empty(harness.Terminations.Terminations);
            Assert.Equal(0, sink.Current.FrameCount);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.CompleteRecoveryAsync(
                    new TableRecoveryBaseline(
                        harness.InitialRound,
                        new StateVersion(0),
                        new EventSeq(0),
                        new OwnerEpoch(1)),
                    TestContext.Current.CancellationToken)).Status);
            IReadOnlyList<TableStreamFrame> first = await sink.Current.WaitForCountAsync(1);
            Assert.True(first[0].IsFullSnapshot);
            Assert.Equal(1L, first[0].RecoveryGeneration);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.BeginRecoveryAsync(
                    "second recovery",
                    TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.CompleteRecoveryAsync(
                    new TableRecoveryBaseline(
                        harness.InitialRound,
                        new StateVersion(0),
                        new EventSeq(0),
                        new OwnerEpoch(1)),
                    TestContext.Current.CancellationToken)).Status);
            IReadOnlyList<TableStreamFrame> second = await sink.Current.WaitForCountAsync(2);
            Assert.True(second[1].IsFullSnapshot);
            Assert.Equal(2L, second[1].RecoveryGeneration);

            TableStreamControlResult sync = await harness.SyncAsync(subscribed.Handle!, second[1].Cursor);
            Assert.Equal(TableStreamControlStatus.BaselineQueued, sync.Status);
            IReadOnlyList<TableStreamFrame> synced = await sink.Current.WaitForCountAsync(3);
            Assert.True(synced[2].IsFullSnapshot);

            TableStreamControlResult inSync = await harness.SyncAsync(subscribed.Handle!, synced[2].Cursor);
            Assert.Equal(TableStreamControlStatus.InSync, inSync.Status);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
            IReadOnlyList<TableStreamFrame> update = await sink.Current.WaitForCountAsync(4);
            Assert.False(update[3].IsFullSnapshot);
            Assert.Equal(2L, update[3].RecoveryGeneration);
        }

        [Fact]
        public async Task CurrentGenerationSinkFailureStillClosesTheStream()
        {
            await using StreamHarness harness = StreamHarness.Create();
            var sink = new CurrentFailureSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSpectatorAsync("local", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.Baseline.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);

            TableStreamTermination termination = await harness.Terminations.WaitForAsync(
                item => item.Identity.SessionId.Value == "local");
            Assert.Equal(TableStreamTerminationKind.ActorFaulted, termination.Kind);
            Assert.Equal(ProtocolErrorCode.InternalError, termination.ReasonCode);
        }

        [Fact]
        public async Task OpeningDecisionRefreshesSeatPrivatelyAndPublishesOnlyPublicClock()
        {
            await using StreamHarness harness = StreamHarness.Create();
            int ownerSeat = harness.InitialRound.CurrentSeat;
            int otherSeat = (ownerSeat + 1) % 4;
            var ownerSink = new RecordingStreamSink();
            var otherSink = new RecordingStreamSink();
            var spectatorSink = new RecordingStreamSink();
            Assert.Equal(
                TableStreamControlStatus.Subscribed,
                (await harness.SubscribeSeatAsync(ownerSeat, "owner", 1UL, 1UL, ownerSink)).Status);
            Assert.Equal(
                TableStreamControlStatus.Subscribed,
                (await harness.SubscribeSeatAsync(otherSeat, "other", 1UL, 1UL, otherSink)).Status);
            Assert.Equal(
                TableStreamControlStatus.Subscribed,
                (await harness.SubscribeSpectatorAsync("spectator", 1UL, 1UL, spectatorSink)).Status);
            await ownerSink.WaitForCountAsync(1);
            await otherSink.WaitForCountAsync(1);
            await spectatorSink.WaitForCountAsync(1);

            PendingDecision decision = DecisionForSeat(harness, ownerSeat, "open-decision");
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(
                    decision,
                    TestContext.Current.CancellationToken)).Status);

            IReadOnlyList<TableStreamFrame> ownerFrames = await ownerSink.WaitForCountAsync(2);
            IReadOnlyList<TableStreamFrame> otherFrames = await otherSink.WaitForCountAsync(2);
            IReadOnlyList<TableStreamFrame> spectatorFrames = await spectatorSink.WaitForCountAsync(2);

            TableStreamFrame owner = ownerFrames[1];
            Assert.False(owner.IsFullSnapshot);
            Assert.Equal(new StateVersion(0), owner.StateVersion);
            Assert.Equal(0L, owner.ControlVersion);
            Assert.Equal(new ProjectionSeq(2), owner.ProjectionSeq);
            Assert.Empty(owner.Events);
            Assert.NotNull(owner.Player!.Private.Decision);
            Assert.Equal("open-decision", owner.Player.Private.Decision!.DecisionId.Value);
            Assert.True(owner.Player.Private.Decision.DeadlineRemainingMs > 0);

            Assert.Null(otherFrames[1].Player!.Private.Decision);
            Assert.Contains(otherFrames[1].Player!.Public.Clocks,
                clock => clock.DecisionSeat == ownerSeat && clock.DeadlineRemainingMs > 0);
            Assert.Null(spectatorFrames[1].Player);
            Assert.Contains(spectatorFrames[1].Public!.View.Clocks,
                clock => clock.DecisionSeat == ownerSeat && clock.DeadlineRemainingMs > 0);
        }

        [Fact]
        public async Task RejectedDecisionOpenPublishesNothingAndSyncRebuildsMissingDecision()
        {
            await using StreamHarness harness = StreamHarness.Create();
            int seat = harness.InitialRound.CurrentSeat;
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSeatAsync(seat, "owner", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            IReadOnlyList<TableStreamFrame> baseline = await sink.WaitForCountAsync(1);

            PendingDecision invalid = DecisionForSeat(harness, seat, "bad-decision", controlEpoch: 99L);
            Assert.Equal(
                TableOperationStatus.Rejected,
                (await harness.Actor.OpenDecisionAsync(
                    invalid,
                    TestContext.Current.CancellationToken)).Status);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(1, sink.FrameCount);

            PendingDecision valid = DecisionForSeat(harness, seat, "good-decision");
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(
                    valid,
                    TestContext.Current.CancellationToken)).Status);
            IReadOnlyList<TableStreamFrame> refreshed = await sink.WaitForCountAsync(2);
            Assert.NotNull(refreshed[1].Player!.Private.Decision);

            TableStreamControlResult repair = await harness.SyncAsync(
                subscribed.Handle!,
                baseline[0].Cursor);
            Assert.Equal(TableStreamControlStatus.BaselineQueued, repair.Status);
            IReadOnlyList<TableStreamFrame> repaired = await sink.WaitForCountAsync(3);
            Assert.True(repaired[2].IsFullSnapshot);
            Assert.NotNull(repaired[2].Player!.Private.Decision);

            Assert.Equal(
                TableOperationStatus.Rejected,
                (await harness.Actor.OpenDecisionAsync(
                    valid,
                    TestContext.Current.CancellationToken)).Status);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(3, sink.FrameCount);

            TableStreamControlResult inSync = await harness.SyncAsync(
                subscribed.Handle!,
                repaired[2].Cursor);
            Assert.Equal(TableStreamControlStatus.InSync, inSync.Status);
        }

        [Fact]
        public async Task DecisionOpenedDuringRecoveryNeedsSyncBeforePrivateRefresh()
        {
            await using StreamHarness harness = StreamHarness.Create();
            int seat = harness.InitialRound.CurrentSeat;
            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed =
                await harness.SubscribeSeatAsync(seat, "owner", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.BeginRecoveryAsync(
                    "decision recovery gate",
                    TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.CompleteRecoveryAsync(
                    new TableRecoveryBaseline(
                        harness.InitialRound,
                        new StateVersion(0),
                        new EventSeq(0),
                        new OwnerEpoch(1)),
                    TestContext.Current.CancellationToken)).Status);
            IReadOnlyList<TableStreamFrame> recovered = await sink.WaitForCountAsync(2);
            Assert.True(recovered[1].IsFullSnapshot);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(
                    DecisionForSeat(harness, seat, "recovery-decision"),
                    TestContext.Current.CancellationToken)).Status);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(2, sink.FrameCount);

            TableStreamControlResult sync = await harness.SyncAsync(
                subscribed.Handle!,
                recovered[1].Cursor);
            Assert.Equal(TableStreamControlStatus.BaselineQueued, sync.Status);
            IReadOnlyList<TableStreamFrame> afterSync = await sink.WaitForCountAsync(3);
            Assert.True(afterSync[2].IsFullSnapshot);
            Assert.NotNull(afterSync[2].Player!.Private.Decision);

            Assert.Equal(
                TableStreamControlStatus.InSync,
                (await harness.SyncAsync(subscribed.Handle!, afterSync[2].Cursor)).Status);
        }

        [Fact]
        public async Task StreamCapacityRejectsWhenFullAndReclaimsAfterUnsubscribe()
        {
            var options = new TableStreamOptions { MaxStreams = 1 };
            await using StreamHarness harness = StreamHarness.Create(options);
            var firstSink = new RecordingStreamSink();
            TableStreamControlResult first = await harness.SubscribeSpectatorAsync(
                "first", 1UL, 1UL, firstSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, first.Status);
            await firstSink.WaitForCountAsync(1);

            TableStreamControlResult full = await harness.SubscribeSpectatorAsync(
                "second", 1UL, 1UL, new RecordingStreamSink());
            Assert.Equal(TableStreamControlStatus.Rejected, full.Status);
            Assert.Equal(ProtocolErrorCode.ServerFull, full.ReasonCode);

            Assert.Equal(
                TableStreamControlStatus.Unsubscribed,
                (await harness.UnsubscribeAsync(first.Handle!)).Status);
            var secondSink = new RecordingStreamSink();
            TableStreamControlResult second = await harness.SubscribeSpectatorAsync(
                "second", 1UL, 1UL, secondSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, second.Status);
            await secondSink.WaitForCountAsync(1);
        }

        [Fact]
        public async Task FailedStreamIsReclaimedBeforeNextSubscribeAndOldIdentityReplayIsRejected()
        {
            var options = new TableStreamOptions { MaxStreams = 1 };
            await using StreamHarness harness = StreamHarness.Create(options);
            var failed = await harness.SubscribeSpectatorAsync(
                "failed-session",
                1UL,
                1UL,
                new AlwaysFailingSink());
            Assert.Equal(TableStreamControlStatus.Subscribed, failed.Status);
            await harness.Terminations.WaitForAsync(
                item => item.Identity.SessionId.Value == "failed-session");

            var replacementSink = new RecordingStreamSink();
            TableStreamControlResult replacement =
                await harness.SubscribeSpectatorAsync("replacement", 1UL, 1UL, replacementSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, replacement.Status);
            await replacementSink.WaitForCountAsync(1);

            Assert.Equal(
                TableStreamControlStatus.Rejected,
                (await harness.UnsubscribeAsync(failed.Handle!)).Status);
            Assert.Equal(
                TableStreamControlStatus.Rejected,
                (await harness.SyncAsync(failed.Handle!, StreamCursor.Empty)).Status);
            Assert.Equal(
                TableStreamControlStatus.Rejected,
                (await harness.SubscribeSpectatorAsync(
                    "failed-session",
                    1UL,
                    1UL,
                    new RecordingStreamSink())).Status);
        }

        private static PendingDecision DecisionForSeat(
            StreamHarness harness,
            int seat,
            string decisionId,
            long controlEpoch = 0L)
        {
            TileId tile = harness.InitialRound.Tiles.TilesInZone(TileZone.Hand, seat)[0];
            return new PendingDecision(
                new DecisionKey(harness.InitialRound.RoundId, seat, decisionId),
                controlEpoch,
                new[] { new LegalActionOption(LegalActionKind.Discard, seat, tile) });
        }

        private sealed class BlockingTerminationSink : ITableStreamTerminationSink
        {
            private readonly TaskCompletionSource<bool> _entered;
            private readonly ManualResetEventSlim _release;

            public BlockingTerminationSink(TaskCompletionSource<bool> entered, ManualResetEventSlim release)
            {
                _entered = entered;
                _release = release;
            }

            public void OnStreamTerminated(TableStreamTermination termination)
            {
                _entered.TrySetResult(true);
                _release.Wait();
            }
        }

        private sealed class OldGenerationFailingSink : ITableStreamSink
        {
            private readonly bool _cancelled;

            public OldGenerationFailingSink(bool cancelled)
            {
                _cancelled = cancelled;
            }

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
                    await Release.Task.ConfigureAwait(false);
                    if (_cancelled)
                    {
                        throw new OperationCanceledException("stale generation sink cancelled");
                    }

                    throw new IOException("stale generation sink failed");
                }

                await Current.PublishAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class CurrentFailureSink : ITableStreamSink
        {
            public TaskCompletionSource<bool> Baseline { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
            {
                if (frame.IsFullSnapshot)
                {
                    Baseline.TrySetResult(true);
                    return default;
                }

                throw new IOException("current generation sink failed");
            }
        }

        private sealed class AlwaysFailingSink : ITableStreamSink
        {
            public ValueTask PublishAsync(TableStreamFrame frame, CancellationToken cancellationToken)
            {
                throw new IOException("controlled first-baseline sink failure");
            }
        }
    }
}
