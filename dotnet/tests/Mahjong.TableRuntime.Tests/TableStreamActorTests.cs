using System;
using System.Collections.Generic;
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

            await harness.Actor.BeginRecoveryAsync("post-commit", TestContext.Current.CancellationToken);
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
            TableStreamControlResult second =
                await harness.SubscribeSeatAsync(0, "s1", 2UL, 1UL, secondSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, first.Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, second.Status);
            await firstSink.WaitForCountAsync(1);
            IReadOnlyList<TableStreamFrame> secondFrames = await secondSink.WaitForCountAsync(1);
            Assert.Equal(2UL, secondFrames[0].ProjectionSeq.Value);

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

            TableStreamControlResult newUnsubscribe = await harness.UnsubscribeAsync(second.Handle!);
            Assert.Equal(TableStreamControlStatus.Unsubscribed, newUnsubscribe.Status);
            TableStreamControlResult afterUnsubscribe = await harness.SyncAsync(second.Handle!, second.Cursor!.Value);
            Assert.Equal(TableStreamControlStatus.Rejected, afterUnsubscribe.Status);
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
            var options = new TableStreamOptions { MaxQueuedFrames = 2 };
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

            slowSink.BlockNext();
            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(4);
            for (int i = 0; i < chain.Count; i++)
            {
                TableOperationResult accepted =
                    await harness.Actor.SubmitAsync(chain[i], TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            }

            await slowSink.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            IReadOnlyList<TableStreamFrame> fastFrames = await fastSink.WaitForCountAsync(5);
            Assert.Equal(5, fastFrames.Count);
            TableStreamTermination termination = await harness.Terminations.WaitForAsync(
                item => item.Kind == TableStreamTerminationKind.SlowConsumer);
            Assert.Equal(ProtocolErrorCode.SlowConsumer, termination.ReasonCode);
            Assert.Equal(1, slowSink.FrameCount);

            slowSink.Release();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(2, slowSink.FrameCount);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(2, slowSink.FrameCount);
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
    }
}
