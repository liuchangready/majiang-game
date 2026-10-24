using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using WireRole = Mahjong.Contracts.Wire.Role;
using WireScope = Mahjong.Contracts.Wire.RequestScope;
using WireStreamBaseline = Mahjong.Contracts.Wire.StreamBaseline;
using WireDecisionView = Mahjong.Contracts.Wire.DecisionView;
using ContractYakuView = Mahjong.Contracts.YakuView;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// P3.2 joint wiring tests: real public TableActor API -> whitelist projection -> explicit
    /// authoritative facts -> Contracts typed payload -> ProjectionWireMapper -> WireCodec.Encode
    /// (strict) -> Decode/re-encode. No fake encoder is used for a claimed Wire path.
    /// </summary>
    public sealed class TableStreamWireIntegrationTests
    {
        private const string JointSession = "session-joint";
        private const string JointRoom = "room-1";

        [Fact]
        public async Task real_actor_four_seats_and_two_spectators_receive_strict_wire_baselines_without_private_leaks()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                CheatOverride = WireCheat.ForAll(round),
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            TableStreamOptions options = WireOptions(codec);
            await using StreamHarness harness = StreamHarness.Create(options, source, initialRound: round);

            var seats = new RecordingStreamSink[4];
            var spectators = new RecordingStreamSink[2];
            for (int seat = 0; seat < 4; seat++)
            {
                seats[seat] = new RecordingStreamSink();
            }

            for (int i = 0; i < spectators.Length; i++)
            {
                spectators[i] = new RecordingStreamSink();
            }

            var subscriptions = new List<Task<TableStreamControlResult>>();
            for (int seat = 0; seat < 4; seat++)
            {
                subscriptions.Add(harness.SubscribeSeatAsync(seat, JointSession, 1UL, 1UL, seats[seat]).AsTask());
            }

            for (int i = 0; i < spectators.Length; i++)
            {
                subscriptions.Add(harness.SubscribeSpectatorAsync(
                    JointSession + "-spec-" + i, 1UL, 1UL, spectators[i]).AsTask());
            }

            TableStreamControlResult[] results = await Task.WhenAll(subscriptions);
            Assert.All(results, result => Assert.Equal(TableStreamControlStatus.Subscribed, result.Status));

            for (int seat = 0; seat < 4; seat++)
            {
                TableStreamFrame frame = (await seats[seat].WaitForCountAsync(1))[0];
                AssertWireFrame(frame, isSpectator: false, seat, isFullSnapshot: true, 0UL, 0UL, 0UL, 1UL);
                Envelope envelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(frame));
                PlayerUpdate update = envelope.PlayerUpdate;
                Assert.True(update.IsFullSnapshot);
                Assert.Equal(0ul, update.FromCursor.StreamVersion);
                Assert.Equal(0ul, update.FromCursor.ProjectionSeq);
                Assert.Equal(seat, update.Snapshot.PrivateView.Seat);
                Assert.Equal(995L, update.Snapshot.PrivateView.Cheat.CheatPoints);
                Assert.Single(update.Snapshot.PrivateView.Cheat.LedgerEntries);
                Assert.Equal("ledger-" + seat, update.Snapshot.PrivateView.Cheat.LedgerEntries[0].EntryId);
                Assert.Equal("offturn-" + seat, update.Snapshot.PrivateView.Cheat.OffTurnSleeveBloom.OfferId);
                Assert.True(update.Snapshot.PrivateView.Cheat.OffTurnSleeveBloom.HasDeadline);
                Assert.Equal(1000L, update.Snapshot.PrivateView.Cheat.OffTurnSleeveBloom.DeadlineRemainingMs);
                Assert.Equal(6u, update.Snapshot.PrivateView.Cheat.SleeveBloomReservation.TileId);
                Assert.NotEmpty(update.Snapshot.PrivateView.Cheat.WindowId);
                Assert.Equal(seat == 0, update.Snapshot.PrivateView.Cheat.SleeveBloom != null);
                Assert.Equal(seat == 0, update.Snapshot.PrivateView.Cheat.BeamSwap != null);
                Assert.Equal("group-" + seat, update.Snapshot.PrivateView.Cheat.TileMark.Groups[0].GroupId);
                AssertWireContractSurface(frame);

                for (int other = 0; other < 4; other++)
                {
                    if (other != seat)
                    {
                        Assert.DoesNotContain(
                            "ledger-" + other,
                            System.Text.Encoding.ASCII.GetString(RequireBytes(frame)));
                    }
                }
            }

            for (int i = 0; i < spectators.Length; i++)
            {
                TableStreamFrame frame = (await spectators[i].WaitForCountAsync(1))[0];
                AssertWireFrame(frame, isSpectator: true, seat: -1, isFullSnapshot: true, 0UL, 0UL, 0UL, 1UL);
                Envelope envelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(frame));
                PublicUpdate update = envelope.PublicUpdate;
                Assert.True(update.IsFullSnapshot);
                Assert.Equal(0ul, update.Snapshot.Cursor.StreamVersion);
                Assert.Equal(1ul, update.Snapshot.Cursor.ProjectionSeq);
                AssertWireContractSurface(frame);
            }

            Assert.Equal(6, codec.EncodeCount);
            Assert.Empty(harness.Terminations.Terminations);
        }

        [Fact]
        public async Task commit_publishes_only_after_success_and_failure_emits_no_bytes_or_cursor()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(WireOptions(codec), source, initialRound: round);

            var seatSink = new RecordingStreamSink();
            var spectatorSink = new RecordingStreamSink();
            TableStreamControlResult seat = await harness.SubscribeSeatAsync(0, JointSession, 1UL, 1UL, seatSink);
            TableStreamControlResult spectator = await harness.SubscribeSpectatorAsync(JointSession, 1UL, 1UL, spectatorSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, seat.Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, spectator.Status);

            IReadOnlyList<TableStreamFrame> seatBaseline = await seatSink.WaitForCountAsync(1);
            IReadOnlyList<TableStreamFrame> spectatorBaseline = await spectatorSink.WaitForCountAsync(1);
            Assert.Equal(new ProjectionSeq(1), seatBaseline[0].ProjectionSeq);
            Assert.Equal(new ProjectionSeq(1), spectatorBaseline[0].ProjectionSeq);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(2);
            harness.CommitPort.BlockNext();
            Task<TableOperationResult> first = harness.Actor.SubmitAsync(
                chain[0], TestContext.Current.CancellationToken).AsTask();
            await harness.CommitPort.Entered;

            // Durable commit has not returned yet: no new frame and no cursor advance.
            Assert.Equal(1, seatSink.FrameCount);
            Assert.Equal(1, spectatorSink.FrameCount);
            Assert.Equal(new ProjectionSeq(1), seatBaseline[0].ProjectionSeq);

            harness.CommitPort.Release();
            TableOperationResult accepted = await first;
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            IReadOnlyList<TableStreamFrame> seatUpdate = await seatSink.WaitForCountAsync(2);
            IReadOnlyList<TableStreamFrame> spectatorUpdate = await spectatorSink.WaitForCountAsync(2);
            AssertWireFrame(seatUpdate[1], isSpectator: false, 0, isFullSnapshot: false, 1UL, 0UL, 1UL, 2UL);
            AssertWireFrame(spectatorUpdate[1], isSpectator: true, -1, isFullSnapshot: false, 1UL, 0UL, 1UL, 2UL);

            harness.CommitPort.FailNext();
            TableOperationResult failed = await harness.Actor.SubmitAsync(
                chain[1], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            Assert.Empty(failed.Events);
            Assert.Equal(2, seatSink.FrameCount);
            Assert.Equal(2, spectatorSink.FrameCount);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(1), snapshot.StateVersion);
        }

        [Fact]
        public async Task successful_commit_emits_typed_events_and_snapshot_with_strict_round_trip()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(WireOptions(codec), source, initialRound: round);

            var seatSink = new RecordingStreamSink();
            var spectatorSink = new RecordingStreamSink();
            await harness.SubscribeSeatAsync(0, JointSession, 1UL, 1UL, seatSink);
            await harness.SubscribeSpectatorAsync(JointSession, 1UL, 1UL, spectatorSink);
            await seatSink.WaitForCountAsync(1);
            await spectatorSink.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            TableOperationResult accepted = await harness.Actor.SubmitAsync(
                chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.True(accepted.Events.Count >= 1);

            TableStreamFrame seatFrame = (await seatSink.WaitForCountAsync(2))[1];
            TableStreamFrame spectatorFrame = (await spectatorSink.WaitForCountAsync(2))[1];
            AssertWireFrame(seatFrame, isSpectator: false, 0, isFullSnapshot: false, 1UL, 0UL, 1UL, 2UL);
            AssertWireFrame(spectatorFrame, isSpectator: true, -1, isFullSnapshot: false, 1UL, 0UL, 1UL, 2UL);

            Envelope seatEnvelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(seatFrame));
            Envelope spectatorEnvelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(spectatorFrame));
            PlayerUpdate playerUpdate = seatEnvelope.PlayerUpdate;
            PublicUpdate publicUpdate = spectatorEnvelope.PublicUpdate;

            Assert.Single(playerUpdate.VisibleEvents);
            Assert.Equal(EventType.TileDiscarded, playerUpdate.VisibleEvents[0].EventType);
            Assert.Equal(1ul, playerUpdate.VisibleEvents[0].EventSeq);
            Assert.Equal(1ul, playerUpdate.VisibleEvents[0].CommandVersion);
            Assert.Equal(0u, playerUpdate.VisibleEvents[0].EventOrdinal);
            Assert.InRange(playerUpdate.VisibleEvents[0].TileDiscarded.TileId, 0u, 135u);
            Assert.Single(publicUpdate.VisibleEvents);
            Assert.Equal(EventType.TileDiscarded, publicUpdate.VisibleEvents[0].EventType);
            Assert.InRange(publicUpdate.VisibleEvents[0].TileDiscarded.TileId, 0u, 135u);
            Assert.Single(playerUpdate.Snapshot.PublicView.Board.Players[0].Discards);
            Assert.Single(publicUpdate.Snapshot.View.Board.Players[0].Discards);

            Assert.Equal(seatFrame.Cursor.StreamVersion.Value, playerUpdate.Cursor.StreamVersion);
            Assert.Equal(seatFrame.Cursor.ProjectionSeq.Value, playerUpdate.Cursor.ProjectionSeq);
            Assert.Equal(seatFrame.FromCursor.StreamVersion.Value, playerUpdate.FromCursor.StreamVersion);
            Assert.Equal(seatFrame.FromCursor.ProjectionSeq.Value, playerUpdate.FromCursor.ProjectionSeq);
            Assert.Equal(seatFrame.PayloadSize, RequireBytes(seatFrame).Length);
            Assert.Equal(spectatorFrame.PayloadSize, RequireBytes(spectatorFrame).Length);
        }

        [Fact]
        public async Task open_decision_refreshes_owner_private_and_public_clock_on_same_state_version()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(WireOptions(codec), source, initialRound: round);

            int seat = round.CurrentSeat;
            int otherSeat = (seat + 1) % 4;
            var ownerSink = new RecordingStreamSink();
            var otherSink = new RecordingStreamSink();
            var spectatorSink = new RecordingStreamSink();
            TableStreamControlResult owner = await harness.SubscribeSeatAsync(seat, "owner", 1UL, 1UL, ownerSink);
            TableStreamControlResult other = await harness.SubscribeSeatAsync(otherSeat, "other", 1UL, 1UL, otherSink);
            TableStreamControlResult spectator =
                await harness.SubscribeSpectatorAsync("spec", 1UL, 1UL, spectatorSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, owner.Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, other.Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, spectator.Status);
            await ownerSink.WaitForCountAsync(1);
            await otherSink.WaitForCountAsync(1);
            await spectatorSink.WaitForCountAsync(1);

            TileId tile = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
            var pending = new PendingDecision(
                new DecisionKey(round.RoundId, seat, "joint-decision"),
                0L,
                new[] { new LegalActionOption(LegalActionKind.Discard, seat, tile) });
            TableOperationResult opened = await harness.Actor.OpenDecisionAsync(
                pending,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);

            TableStreamFrame ownerFrame = (await ownerSink.WaitForCountAsync(2))[1];
            TableStreamFrame otherFrame = (await otherSink.WaitForCountAsync(2))[1];
            TableStreamFrame spectatorFrame = (await spectatorSink.WaitForCountAsync(2))[1];
            Assert.False(ownerFrame.IsFullSnapshot);
            Assert.Equal(new StateVersion(0), ownerFrame.StateVersion);
            Assert.Equal(0L, ownerFrame.ControlVersion);
            Assert.Equal(new ProjectionSeq(2), ownerFrame.ProjectionSeq);
            Assert.Empty(ownerFrame.Events);
            Assert.NotNull(ownerFrame.Player!.Private.Decision);

            Envelope ownerEnvelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(ownerFrame));
            Envelope otherEnvelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(otherFrame));
            Envelope spectatorEnvelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(spectatorFrame));
            WireDecisionView ownerDecision = ownerEnvelope.PlayerUpdate.Snapshot.PrivateView.Decision;
            Assert.Equal("joint-decision", ownerDecision.DecisionId);
            Assert.True(ownerDecision.DeadlineRemainingMs > 0);
            Assert.Single(ownerDecision.Actions);
            Assert.Equal((uint)ProtocolActionType.DiscardTile, ownerDecision.Actions[0].ActionType);
            Assert.Null(otherEnvelope.PlayerUpdate.Snapshot.PrivateView.Decision);
            Assert.Contains(
                otherEnvelope.PlayerUpdate.Snapshot.PublicView.Clocks,
                clock => clock.DecisionSeat == seat && clock.DeadlineRemainingMs > 0);
            Assert.Contains(
                spectatorEnvelope.PublicUpdate.Snapshot.View.Clocks,
                clock => clock.DecisionSeat == seat && clock.DeadlineRemainingMs > 0);
            Assert.All(spectatorEnvelope.PublicUpdate.Snapshot.View.Clocks, clock => Assert.True(clock.ServerTimeMs > 0));

            Assert.Equal(
                TableStreamControlStatus.InSync,
                (await harness.SyncAsync(owner.Handle!, ownerFrame.Cursor)).Status);
        }

        [Fact]
        public async Task control_takeover_and_reclaim_close_old_streams_and_reject_stale_bindings_and_epochs()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(
                WireOptions(codec),
                source,
                withControlCommit: true,
                initialRound: round);

            var firstSink = new RecordingStreamSink();
            TableStreamControlResult first = await harness.SubscribeSeatAsync(
                0, "seat0-a", 1UL, 1UL, firstSink, expectedControlEpoch: 0UL);
            Assert.Equal(TableStreamControlStatus.Subscribed, first.Status);
            await firstSink.WaitForCountAsync(1);

            TableControlResult takeover = await harness.Actor.RequestTakeoverAsync(
                0,
                new OwnerEpoch(1),
                new ControlEpoch(0),
                "joint takeover",
                TestContext.Current.CancellationToken);
            Assert.True(takeover.IsChanged);
            Assert.Equal(1L, takeover.Snapshot!.Seats[0].ControlEpoch);
            Assert.Equal(SeatControllerKind.Takeover, takeover.Snapshot.Seats[0].Kind);

            TableStreamTermination termination = await harness.Terminations.WaitForAsync(
                item => item.Kind == TableStreamTerminationKind.StaleControlEpoch);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, termination.ReasonCode);
            TableStreamControlResult staleSync = await harness.SyncAsync(first.Handle!, first.Cursor!.Value);
            Assert.Equal(TableStreamControlStatus.Rejected, staleSync.Status);
            Assert.Equal(ProtocolErrorCode.StaleStreamVersion, staleSync.ReasonCode);

            var staleEpochSink = new RecordingStreamSink();
            TableStreamControlResult staleEpoch = await harness.SubscribeSeatAsync(
                0, "seat0-b", 2UL, 2UL, staleEpochSink, expectedControlEpoch: 0UL);
            Assert.Equal(TableStreamControlStatus.Rejected, staleEpoch.Status);

            var newSink = new RecordingStreamSink();
            TableStreamControlResult rebound = await harness.SubscribeSeatAsync(
                0, "seat0-b", 2UL, 2UL, newSink, expectedControlEpoch: 1UL);
            Assert.Equal(TableStreamControlStatus.Subscribed, rebound.Status);
            TableStreamFrame reboundFrame = (await newSink.WaitForCountAsync(1))[0];
            Assert.Equal(1L, reboundFrame.ControlVersion);
            StrictWireStreamCodec.DecodeAndStrict(RequireBytes(reboundFrame));

            var staleBindingSink = new RecordingStreamSink();
            TableStreamControlResult staleBinding = await harness.SubscribeSeatAsync(
                0, "seat0-b", 1UL, 1UL, staleBindingSink, expectedControlEpoch: 1UL);
            Assert.Equal(TableStreamControlStatus.Rejected, staleBinding.Status);

            TableControlResult reclaim = await harness.Actor.ReclaimControlAsync(
                0,
                new OwnerEpoch(1),
                new ControlEpoch(1),
                "joint reclaim",
                TestContext.Current.CancellationToken);
            Assert.True(reclaim.IsChanged);
            Assert.Equal(2L, reclaim.Snapshot!.Seats[0].ControlEpoch);
            Assert.Equal(SeatControllerKind.RemoteHuman, reclaim.Snapshot.Seats[0].Kind);
            await harness.Terminations.WaitForAsync(
                item => item.Kind == TableStreamTerminationKind.StaleControlEpoch);

            var reclaimedSink = new RecordingStreamSink();
            TableStreamControlResult reclaimed = await harness.SubscribeSeatAsync(
                0, "seat0-c", 3UL, 3UL, reclaimedSink, expectedControlEpoch: 2UL);
            Assert.Equal(TableStreamControlStatus.Subscribed, reclaimed.Status);
            TableStreamFrame reclaimedFrame = (await reclaimedSink.WaitForCountAsync(1))[0];
            Assert.Equal(2L, reclaimedFrame.ControlVersion);
            StrictWireStreamCodec.DecodeAndStrict(RequireBytes(reclaimedFrame));
        }

        [Fact]
        public async Task recovery_fences_queued_frames_rebaselines_and_sync_gates_updates()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            TableStreamChannel? channel = null;
            TableStreamOptions options = WireOptions(codec);
            options.ObservePumpExit = true;
            options.ChannelCreated = created => channel ??= created;
            await using StreamHarness harness = StreamHarness.Create(options, source, initialRound: round);

            var sink = new RecordingStreamSink();
            sink.BlockNext();
            TableStreamControlResult subscribed = await harness.SubscribeSpectatorAsync("recover", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotNull(channel);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(3);
            RoundTransition transition = harness.InitialRound.Execute(chain[0].Command);
            Assert.True(transition.IsAccepted);
            TableOperationResult applied = await harness.Actor.SubmitAsync(
                chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, applied.Status);
            Assert.Equal(1UL, applied.CommandVersion!.Value.Value);

            TableOperationResult begin = await harness.Actor.BeginRecoveryAsync(
                "joint recovery",
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Configured, begin.Status);
            sink.Release();
            await channel!.PumpExit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, sink.FrameCount);

            TableOperationResult complete = await harness.Actor.CompleteRecoveryAsync(
                new TableRecoveryBaseline(
                    transition.State!,
                    new StateVersion(1),
                    new EventSeq(unchecked((ulong)transition.Events.Count)),
                    new OwnerEpoch(1)),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Configured, complete.Status);
            TableStreamFrame recovered = (await sink.WaitForCountAsync(2))[1];
            Assert.True(recovered.IsFullSnapshot);
            Assert.Equal(new StateVersion(1), recovered.StateVersion);
            Assert.Equal(new ProjectionSeq(3), recovered.ProjectionSeq);
            Assert.Equal(1L, recovered.RecoveryGeneration);
            Envelope recoveredEnvelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(recovered));
            Assert.True(recoveredEnvelope.PublicUpdate.IsFullSnapshot);
            Assert.Single(recoveredEnvelope.PublicUpdate.Snapshot.View.Board.Players[0].Discards);

            TableOperationResult gated = await harness.Actor.SubmitAsync(
                chain[1], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, gated.Status);
            Assert.Equal(2, sink.FrameCount);

            TableStreamControlResult sync = await harness.SyncAsync(subscribed.Handle!, recovered.Cursor);
            Assert.Equal(TableStreamControlStatus.BaselineQueued, sync.Status);
            TableStreamFrame synced = (await sink.WaitForCountAsync(3))[2];
            Assert.True(synced.IsFullSnapshot);
            Assert.Equal(new StateVersion(2), synced.StateVersion);
            Assert.Equal(new ProjectionSeq(4), synced.ProjectionSeq);
            Envelope syncedEnvelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(synced));
            Assert.Equal(2, syncedEnvelope.PublicUpdate.Snapshot.View.Board.Players.Sum(player => player.Discards.Count));

            TableOperationResult afterSync = await harness.Actor.SubmitAsync(
                chain[2], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, afterSync.Status);
            TableStreamFrame update = (await sink.WaitForCountAsync(4))[3];
            Assert.False(update.IsFullSnapshot);
            Assert.Equal(new ProjectionSeq(5), update.ProjectionSeq);
            Assert.Equal(synced.Cursor, update.FromCursor);
            StrictWireStreamCodec.DecodeAndStrict(RequireBytes(update));
        }

        [Fact]
        public async Task actual_encoded_bytes_enforce_frame_and_byte_limits_and_isolate_slow_sink()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            var channels = new List<TableStreamChannel>();
            TableStreamOptions options = WireOptions(codec);
            options.ObservePumpExit = true;
            options.ChannelCreated = channels.Add;
            await using StreamHarness harness = StreamHarness.Create(options, source, initialRound: round);

            var fastSink = new RecordingStreamSink();
            var slowSink = new RecordingStreamSink();
            await harness.SubscribeSpectatorAsync("fast", 1UL, 1UL, fastSink);
            await harness.SubscribeSpectatorAsync("slow", 1UL, 1UL, slowSink);
            await fastSink.WaitForCountAsync(1);
            await slowSink.WaitForCountAsync(1);
            Assert.Equal(2, channels.Count);
            TableStreamChannel slowChannel = channels[1];

            Assert.Equal(64, harness.StreamOptions.MaxQueuedFrames);
            Assert.Equal(4 * 1024 * 1024, harness.StreamOptions.MaxQueuedBytes);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(66);
            slowSink.BlockNext();
            Assert.Equal(
                TableOperationStatus.Accepted,
                (await harness.Actor.SubmitAsync(chain[0], TestContext.Current.CancellationToken)).Status);
            await slowSink.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task slowPumpExit = slowChannel.PumpExit;
            Assert.False(slowPumpExit.IsCompleted, "The slow pump must still be parked inside the first update.");
            await fastSink.WaitForCountAsync(2);

            for (int i = 1; i < chain.Count; i++)
            {
                TableOperationResult result = await harness.Actor.SubmitAsync(
                    chain[i], TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Accepted, result.Status);
                await fastSink.WaitForCountAsync(i + 2);
            }

            TableStreamTermination slow = await harness.Terminations.WaitForAsync(
                item => item.Kind == TableStreamTerminationKind.SlowConsumer);
            Assert.Equal(ProtocolErrorCode.SlowConsumer, slow.ReasonCode);
            Assert.Equal(1, slowSink.FrameCount);
            Assert.Equal(67, fastSink.FrameCount);
            byte[] lastFastFrame = RequireBytes(fastSink.Frames[66]);
            Assert.True(lastFastFrame.Length > 0);
            StrictWireStreamCodec.DecodeAndStrict(lastFastFrame);

            slowSink.Release();
            await slowPumpExit.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(2, slowSink.FrameCount);
            Assert.Equal(67, fastSink.FrameCount);

            var byteCodec = new StrictWireStreamCodec();
            TableStreamOptions byteOptions = WireOptions(byteCodec);
            byteOptions.MaxQueuedBytes = 100;
            await using StreamHarness byteHarness = StreamHarness.Create(
                byteOptions,
                new TestProjectionSource { EventFactsFactory = request => WireEventFacts.Build(request) },
                initialRound: P31Fixtures.Dealt("round-1"));
            var tinySink = new RecordingStreamSink();
            TableStreamControlResult tiny = await byteHarness.SubscribeSpectatorAsync("tiny", 1UL, 1UL, tinySink);
            Assert.Equal(TableStreamControlStatus.Failed, tiny.Status);
            Assert.Equal(ProtocolErrorCode.MessageTooLarge, tiny.ReasonCode);
            Assert.Equal(0, tinySink.FrameCount);
        }

        [Fact]
        public async Task missing_event_facts_fail_closed_after_commit_and_do_not_lose_committed_state()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                OmitEventFacts = true
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(WireOptions(codec), source, initialRound: round);

            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed = await harness.SubscribeSpectatorAsync("facts", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            TableOperationResult accepted = await harness.Actor.SubmitAsync(
                chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            TableStreamTermination termination = await harness.Terminations.WaitForAsync(
                item => item.Kind == TableStreamTerminationKind.ProjectionFailed);
            Assert.Equal(ProtocolErrorCode.InternalError, termination.ReasonCode);
            Assert.Contains("requires an explicit", harness.Actor.PublicationFault);
            Assert.Equal(1, sink.FrameCount);

            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(1), snapshot.StateVersion);

            source.OmitEventFacts = false;
            var repairedSink = new RecordingStreamSink();
            TableStreamControlResult repaired = await harness.SubscribeSpectatorAsync(
                "facts-repaired", 2UL, 2UL, repairedSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, repaired.Status);
            TableStreamFrame baseline = (await repairedSink.WaitForCountAsync(1))[0];
            Envelope envelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(baseline));
            Assert.Single(envelope.PublicUpdate.Snapshot.View.Board.Players[0].Discards);
        }

        [Fact]
        public async Task closed_kan_placeholder_and_owner_identities_stay_separated_and_no_seed_fields()
        {
            RoundStateMachine baseRound = P31Fixtures.Dealt("round-1");
            RoundStateMachine round = V32Fixtures.WithMeld(
                baseRound,
                3,
                4,
                ProtocolMeldType.ClosedKan,
                out TableMeldFact closedKan);
            var publicFacts = V32Fixtures.BuildPublicFacts(
                round,
                new Dictionary<int, IReadOnlyList<TableMeldFact>>
                {
                    [3] = new[] { closedKan }
                });
            var source = new TestProjectionSource
            {
                PublicFactsOverride = publicFacts,
                ConcealedMeldFacts = new[] { new TablePublicConcealedMeldFact(3) },
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(WireOptions(codec), source, initialRound: round);

            var ownerSink = new RecordingStreamSink();
            var otherSink = new RecordingStreamSink();
            var spectatorSink = new RecordingStreamSink();
            await harness.SubscribeSeatAsync(3, "kan-owner", 1UL, 1UL, ownerSink);
            await harness.SubscribeSeatAsync(0, "kan-other", 1UL, 1UL, otherSink);
            await harness.SubscribeSpectatorAsync("kan-spec", 1UL, 1UL, spectatorSink);

            Envelope owner = StrictWireStreamCodec.DecodeAndStrict(RequireBytes((await ownerSink.WaitForCountAsync(1))[0]));
            Envelope other = StrictWireStreamCodec.DecodeAndStrict(RequireBytes((await otherSink.WaitForCountAsync(1))[0]));
            Envelope spectator = StrictWireStreamCodec.DecodeAndStrict(
                RequireBytes((await spectatorSink.WaitForCountAsync(1))[0]));

            var publicPlayer = spectator.PublicUpdate.Snapshot.View.Board.Players[3];
            Assert.Empty(publicPlayer.Melds);
            Assert.Single(publicPlayer.ConcealedMelds);
            Assert.Equal(MeldType.ClosedKan, publicPlayer.ConcealedMelds[0].Type);
            Assert.Equal(4u, publicPlayer.ConcealedMelds[0].TileCount);
            Assert.Equal(-1, publicPlayer.ConcealedMelds[0].FromSeat);

            Assert.Single(owner.PlayerUpdate.Snapshot.PrivateView.OwnerMelds);
            Assert.Equal(MeldType.ClosedKan, owner.PlayerUpdate.Snapshot.PrivateView.OwnerMelds[0].Type);
            Assert.Equal(4, owner.PlayerUpdate.Snapshot.PrivateView.OwnerMelds[0].TileIds.Count);
            Assert.Equal(closedKan.TileIds.ToArray(), owner.PlayerUpdate.Snapshot.PrivateView.OwnerMelds[0].TileIds.ToArray());
            Assert.Empty(other.PlayerUpdate.Snapshot.PrivateView.OwnerMelds);
            Assert.Single(other.PlayerUpdate.Snapshot.PublicView.Board.Players[3].ConcealedMelds);
            Assert.Empty(other.PlayerUpdate.Snapshot.PublicView.Board.Players[3].Melds);
            Assert.Single(owner.PlayerUpdate.Snapshot.PublicView.Board.Players[3].ConcealedMelds);

            var badOwner = other.Clone();
            badOwner.SeatId = 1;
            Assert.Throws<PublicProjectionException>(() => WireCodec.Encode(badOwner));

            TableStreamFrame otherFrame = (await otherSink.WaitForCountAsync(1))[0];
            AssertWireContractSurface(otherFrame);
            Assert.DoesNotContain(
                "P32A",
                System.Text.Encoding.ASCII.GetString(RequireBytes(otherFrame)));
        }

        [Fact]
        public async Task missing_window_for_live_offer_fails_closed_instead_of_fabricating_one()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = WireCheat.ForAll(round);
            CheatState noWindow = cheat.WithSleeveBloom(cheat.SleeveBloom.WithClearedWindow(1));
            var source = new TestProjectionSource
            {
                CheatOverride = noWindow,
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(WireOptions(codec), source, initialRound: round);

            var sink = new RecordingStreamSink();
            TableStreamControlResult result = await harness.SubscribeSeatAsync(1, "no-window", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Failed, result.Status);
            Assert.Equal(0, sink.FrameCount);
            Assert.Contains("window", result.Detail.ToLowerInvariant());
        }

        [Fact]
        public async Task current_client_accepts_actor_wire_update()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(WireOptions(codec), source, initialRound: round);

            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed = await harness.SubscribeSeatAsync(
                0, JointSession, 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            TableStreamFrame baselineFrame = (await sink.WaitForCountAsync(1))[0];
            Envelope baselineEnvelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(baselineFrame));

            var channel = new QueueClientChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            await session.OpenAsync(
                new TableSessionOpenRequest("ticket-joint", "client-joint"),
                TestContext.Current.CancellationToken);
            channel.Enqueue(WireCodec.Encode(BuildWelcome(baselineFrame.Contract!, baselineEnvelope.PlayerUpdate.Snapshot)));
            await session.PumpOnceAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);

            Envelope syncRequest = channel.LastSent(MessageType.SyncRequest);
            Envelope sync = BuildPlayerSync(
                baselineFrame.Contract!,
                baselineEnvelope.PlayerUpdate.Snapshot,
                syncRequest.CorrelationId);
            channel.Enqueue(WireCodec.Encode(sync));
            await session.PumpOnceAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.Equal(new ProjectionSeq(1), new ProjectionSeq(session.ConfirmedCursor!.Value.ProjectionSeq.Value));

            bool changed = false;
            TableSnapshotChangedEventArgs? observed = null;
            session.SnapshotChanged += (_, args) =>
            {
                changed = true;
                observed = args;
            };

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(1);
            TableOperationResult accepted = await harness.Actor.SubmitAsync(
                chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            TableStreamFrame updateFrame = (await sink.WaitForCountAsync(2))[1];
            channel.Enqueue(RequireBytes(updateFrame));
            await session.PumpOnceAsync(TestContext.Current.CancellationToken);

            Assert.True(changed);
            Assert.NotNull(observed);
            Assert.False(observed!.IsFullBaseline);
            Assert.Equal(new StreamVersion(1), observed.Cursor.StreamVersion);
            Assert.Equal(new ProjectionSeq(2), observed.Cursor.ProjectionSeq);
            Assert.NotNull(session.PlayerSnapshot);
            Assert.Equal(2ul, session.PlayerSnapshot!.Cursor.ProjectionSeq);
            Assert.Single(session.PlayerSnapshot.PublicView.Board.Players[0].Discards);
        }

        [Fact]
        public async Task committed_tsumo_emits_typed_settlement_event_and_snapshot()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var winFacts = new WireEventFactOptions
            {
                TurnNumber = 1,
                WinYaku = new[] { new ContractYakuView("RIICHI", 1) },
                WinHan = 1,
                WinFu = 30,
                WinScore = 1000
            };
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request, winFacts)
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(WireOptions(codec), source, initialRound: round);

            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed = await harness.SubscribeSeatAsync(0, "tsumo", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            long[] finalScores = { 26000, 24000, 25000, 25000 };
            var settlement = new RoundSettlement(
                "result-1",
                RoundOutcome.Tsumo(0),
                finalScores,
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)),
                1234L);
            var command = new TableCommandEnvelope(
                RoundCommand.Tsumo(round.RoundId, 0, settlement),
                new RequestId("tsumo-1"),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0));
            TableOperationResult accepted = await harness.Actor.SubmitAsync(
                command, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Contains(accepted.Events, record => record.Type == RoundEventType.TsumoWin);
            Assert.Contains(accepted.Events, record => record.Type == RoundEventType.RoundEnded);

            TableStreamFrame frame = (await sink.WaitForCountAsync(2))[1];
            Envelope envelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(frame));
            PlayerUpdate update = envelope.PlayerUpdate;
            Assert.Contains(update.VisibleEvents, record => record.EventType == EventType.TsumoWin);
            Assert.Contains(update.VisibleEvents, record => record.EventType == EventType.RoundEnded);
            Assert.Equal(2, update.Snapshot.PublicView.SettlementEvents.Count);
            Assert.All(
                update.Snapshot.PublicView.SettlementEvents,
                record => Assert.True(record.EventCase != PublicEventEnvelope.EventOneofCase.None));
            Assert.Equal(finalScores, update.Snapshot.PublicView.Board.Scores.Select(score => score.Score).ToArray());
            Assert.Equal(1ul, update.Snapshot.StateVersion);
        }

        [Fact]
        public async Task diagnostic_window_zero_keeps_discard_update_and_failed_commit_is_silent()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request)
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(
                WireOptions(codec),
                source,
                initialRound: round,
                snapshotEventWindow: 0);

            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed = await harness.SubscribeSpectatorAsync("zero-discard", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            IReadOnlyList<TableCommandEnvelope> chain = harness.DiscardChain(2);
            TableOperationResult accepted = await harness.Actor.SubmitAsync(
                chain[0], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            TableStreamFrame update = (await sink.WaitForCountAsync(2))[1];
            Envelope envelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(update));
            Assert.Single(envelope.PublicUpdate.VisibleEvents);
            Assert.Equal(EventType.TileDiscarded, envelope.PublicUpdate.VisibleEvents[0].EventType);
            Assert.Empty(envelope.PublicUpdate.Snapshot.View.SettlementEvents);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Empty(snapshot.RecentEvents);
            Assert.Equal(new StateVersion(1), snapshot.StateVersion);

            harness.CommitPort.FailNext();
            TableOperationResult failed = await harness.Actor.SubmitAsync(
                chain[1], TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Failed, failed.Status);
            Assert.Empty(failed.Events);
            Assert.Equal(2, sink.FrameCount);
            TableSnapshot afterFailure = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new StateVersion(1), afterFailure.StateVersion);
            Assert.Empty(afterFailure.RecentEvents);
        }

        [Fact]
        public async Task diagnostic_window_zero_keeps_complete_tsumo_settlement_for_update_baseline_sync_and_control_refresh()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request, WinOptions())
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(
                WireOptions(codec),
                source,
                withControlCommit: true,
                initialRound: round,
                snapshotEventWindow: 0);

            var seatSink = new RecordingStreamSink();
            var spectatorSink = new RecordingStreamSink();
            TableStreamControlResult seat = await harness.SubscribeSeatAsync(0, "zero-tsumo-seat", 1UL, 1UL, seatSink);
            TableStreamControlResult spectator =
                await harness.SubscribeSpectatorAsync("zero-tsumo-spec", 1UL, 1UL, spectatorSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, seat.Status);
            Assert.Equal(TableStreamControlStatus.Subscribed, spectator.Status);
            await seatSink.WaitForCountAsync(1);
            await spectatorSink.WaitForCountAsync(1);

            TableOperationResult accepted = await harness.Actor.SubmitAsync(
                TsumoCommand(round, "zero-tsumo"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(2, accepted.Events.Count);
            Assert.True(string.IsNullOrEmpty(harness.Actor.PublicationFault), harness.Actor.PublicationFault);
            Assert.Empty(harness.Terminations.Terminations);

            TableStreamFrame seatFrame = (await seatSink.WaitForCountAsync(2))[1];
            TableStreamFrame spectatorFrame = (await spectatorSink.WaitForCountAsync(2))[1];
            PlayerUpdate seatUpdate = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(seatFrame)).PlayerUpdate;
            PublicUpdate spectatorUpdate =
                StrictWireStreamCodec.DecodeAndStrict(RequireBytes(spectatorFrame)).PublicUpdate;
            Assert.Equal(2, seatUpdate.VisibleEvents.Count);
            Assert.Equal(2, spectatorUpdate.VisibleEvents.Count);
            Assert.Equal(2, seatUpdate.Snapshot.PublicView.SettlementEvents.Count);
            Assert.Equal(2, spectatorUpdate.Snapshot.View.SettlementEvents.Count);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Empty(snapshot.RecentEvents);

            var lateSink = new RecordingStreamSink();
            TableStreamControlResult late = await harness.SubscribeSpectatorAsync(
                "zero-tsumo-late", 2UL, 2UL, lateSink);
            Assert.Equal(TableStreamControlStatus.Subscribed, late.Status);
            Envelope lateBaseline = StrictWireStreamCodec.DecodeAndStrict(
                RequireBytes((await lateSink.WaitForCountAsync(1))[0]));
            Assert.True(lateBaseline.PublicUpdate.IsFullSnapshot);
            Assert.Equal(2, lateBaseline.PublicUpdate.Snapshot.View.SettlementEvents.Count);
            Assert.Empty(lateBaseline.PublicUpdate.VisibleEvents);

            TableStreamControlResult sync = await harness.SyncAsync(
                late.Handle!,
                new ContractCursor(new StreamVersion(0), new ProjectionSeq(0)));
            Assert.Equal(TableStreamControlStatus.BaselineQueued, sync.Status);
            Envelope synced = StrictWireStreamCodec.DecodeAndStrict(
                RequireBytes((await lateSink.WaitForCountAsync(2))[1]));
            Assert.True(synced.PublicUpdate.IsFullSnapshot);
            Assert.Equal(2, synced.PublicUpdate.Snapshot.View.SettlementEvents.Count);

            TableControlResult takeover = await harness.Actor.RequestTakeoverAsync(
                0,
                new OwnerEpoch(1),
                new ControlEpoch(0),
                "zero-window-control-refresh",
                TestContext.Current.CancellationToken);
            Assert.True(takeover.IsChanged);
            Envelope refreshed = StrictWireStreamCodec.DecodeAndStrict(
                RequireBytes((await spectatorSink.WaitForCountAsync(3))[2]));
            Assert.Equal(2, refreshed.PublicUpdate.Snapshot.View.SettlementEvents.Count);
            Assert.Empty(refreshed.PublicUpdate.VisibleEvents);
        }

        [Fact]
        public async Task diagnostic_window_one_keeps_multi_event_tsumo_settlement_and_one_recent_diagnostic_event()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request, WinOptions())
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(
                WireOptions(codec),
                source,
                initialRound: round,
                snapshotEventWindow: 1);

            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed = await harness.SubscribeSpectatorAsync("one-tsumo", 1UL, 1UL, sink);
            Assert.Equal(TableStreamControlStatus.Subscribed, subscribed.Status);
            await sink.WaitForCountAsync(1);

            TableOperationResult accepted = await harness.Actor.SubmitAsync(
                TsumoCommand(round, "one-tsumo"),
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, accepted.Status);
            Assert.Equal(2, accepted.Events.Count);
            TableStreamFrame frame = (await sink.WaitForCountAsync(2))[1];
            Envelope envelope = StrictWireStreamCodec.DecodeAndStrict(RequireBytes(frame));
            Assert.Equal(2, envelope.PublicUpdate.VisibleEvents.Count);
            Assert.Equal(2, envelope.PublicUpdate.Snapshot.View.SettlementEvents.Count);
            TableSnapshot snapshot = await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Single(snapshot.RecentEvents);
            Assert.Equal(RoundEventType.RoundEnded, snapshot.RecentEvents[0].Type);
        }

        [Fact]
        public async Task recovered_terminal_round_without_event_history_fails_closed()
        {
            RoundStateMachine initial = P31Fixtures.Dealt("round-1");
            RoundStateMachine terminal = V32Fixtures.ApplyTsumo(
                initial,
                0,
                new long[] { 26000, 24000, 25000, 25000 });
            var source = new TestProjectionSource
            {
                EventFactsFactory = request => WireEventFacts.Build(request, WinOptions())
            };
            var codec = new StrictWireStreamCodec();
            await using StreamHarness harness = StreamHarness.Create(
                WireOptions(codec),
                source,
                initialRound: initial,
                snapshotEventWindow: 0);

            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.BeginRecoveryAsync(
                    "terminal recovery without history",
                    TestContext.Current.CancellationToken)).Status);
            Assert.Equal(
                TableOperationStatus.Configured,
                (await harness.Actor.CompleteRecoveryAsync(
                    new TableRecoveryBaseline(
                        terminal,
                        new StateVersion(1),
                        new EventSeq(2),
                        new OwnerEpoch(1)),
                    TestContext.Current.CancellationToken)).Status);

            var sink = new RecordingStreamSink();
            TableStreamControlResult subscribed = await harness.SubscribeSpectatorAsync(
                "recovered-terminal", 1UL, 1UL, sink, recoveryGeneration: 1L);
            Assert.Equal(TableStreamControlStatus.Failed, subscribed.Status);
            Assert.Equal(0, sink.FrameCount);
            Assert.Contains("terminal round", subscribed.Detail);
        }

        private static WireEventFactOptions WinOptions()
        {
            return new WireEventFactOptions
            {
                WinYaku = new[] { new ContractYakuView("RIICHI", 1) },
                WinHan = 1,
                WinFu = 30,
                WinScore = 1000
            };
        }

        private static TableCommandEnvelope TsumoCommand(RoundStateMachine round, string requestId)
        {
            var settlement = new RoundSettlement(
                "result-" + requestId,
                RoundOutcome.Tsumo(0),
                new long[] { 26000, 24000, 25000, 25000 },
                0,
                new PersistedUtcTime(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)),
                1234L);
            return new TableCommandEnvelope(
                RoundCommand.Tsumo(round.RoundId, 0, settlement),
                new RequestId(requestId),
                new StateVersion(0),
                new OwnerEpoch(1),
                new ControlEpoch(0));
        }

        private static Envelope BuildWelcome(TableStreamContractFrame frame, PlayerSnapshot baseline)
        {
            var scope = new WireScope
            {
                ScopeId = "scope-joint",
                AccountId = "account-joint",
                Role = WireRole.Player,
                RoomId = frame.RoomId,
                IssuedAtMs = 1000,
                ExpiresAtMs = 2000000000000L
            };
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Welcome,
                SessionId = frame.SessionId.Value,
                RoomId = frame.RoomId,
                SeatId = frame.Audience.Seat,
                BindingEpoch = frame.BindingEpoch.Value,
                Welcome = new Welcome
                {
                    SessionId = frame.SessionId.Value,
                    RoomId = frame.RoomId,
                    TableId = "table-joint",
                    Role = WireRole.Player,
                    SeatId = frame.Audience.Seat,
                    ConnectionEpoch = 1,
                    OwnerEpoch = frame.OwnerEpoch.Value,
                    BindingEpoch = frame.BindingEpoch.Value,
                    Baseline = new WireStreamBaseline
                    {
                        StateVersion = frame.StateVersion.Value,
                        EventSeq = frame.EventSeq.Value,
                        Cursor = ProjectionWireMapper.MapCursor(frame.Cursor)
                    },
                    Scope = scope,
                    Limits = new ServerLimits
                    {
                        MaxInboundMessageBytes = 1024 * 1024,
                        MaxCommandPayloadBytes = 64 * 1024,
                        MaxSnapshotBytes = 1024 * 1024,
                        MaxInFlightRequests = 64,
                        HeartbeatIntervalMs = 10000,
                        IdleDeadMs = 30000,
                        MaxEarlyBufferItems = 64,
                        MaxEarlyBufferBytes = 4 * 1024 * 1024
                    },
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion
                }
            };
        }

        private static Envelope BuildPlayerSync(
            TableStreamContractFrame frame,
            PlayerSnapshot baseline,
            string correlationId)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.SyncResponse,
                SessionId = frame.SessionId.Value,
                RoomId = frame.RoomId,
                SeatId = frame.Audience.Seat,
                BindingEpoch = frame.BindingEpoch.Value,
                CorrelationId = correlationId,
                SyncResponse = new SyncResponse
                {
                    Target = SyncTarget.PlayerStream,
                    IsFullSnapshot = true,
                    Player = baseline,
                    StateVersion = frame.StateVersion.Value,
                    EventSeq = frame.EventSeq.Value
                }
            };
        }

        private static TableStreamOptions WireOptions(StrictWireStreamCodec codec)
        {
            return new TableStreamOptions
            {
                Encoder = new ContractsStreamFrameEncoder(codec)
            };
        }

        private static byte[] RequireBytes(TableStreamFrame frame)
        {
            byte[]? bytes = frame.EncodedPayload;
            Assert.NotNull(bytes);
            Assert.Equal(frame.PayloadSize, bytes!.Length);
            Assert.True(bytes.Length > 0);
            return bytes;
        }

        private static void AssertWireFrame(
            TableStreamFrame frame,
            bool isSpectator,
            int seat,
            bool isFullSnapshot,
            ulong streamVersion,
            ulong fromStreamVersion,
            ulong fromProjection,
            ulong projection)
        {
            Assert.Equal(isFullSnapshot, frame.IsFullSnapshot);
            Assert.Equal(new StreamVersion(streamVersion), frame.StreamVersion);
            Assert.Equal(new ProjectionSeq(projection), frame.ProjectionSeq);
            Assert.Equal(new StreamVersion(fromStreamVersion), frame.FromCursor.StreamVersion);
            Assert.Equal(new ProjectionSeq(fromProjection), frame.FromCursor.ProjectionSeq);
            Assert.NotNull(frame.Contract);
            Assert.Equal(isSpectator, frame.Public != null);
            Assert.Equal(isSpectator, frame.Player == null);
            if (!isSpectator)
            {
                Assert.Null(frame.Public);
                Assert.Equal(seat, frame.Player!.Seat);
            }

            AssertWireContractSurface(frame);
        }

        private static void AssertWireContractSurface(TableStreamFrame frame)
        {
            Assert.NotNull(frame.Contract);
            Assert.DoesNotContain(
                "P32A",
                System.Text.Encoding.ASCII.GetString(RequireBytes(frame)));
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            ScanForbiddenFields(frame.Contract, seen, 0);
        }

        private static void ScanForbiddenFields(object? value, HashSet<object> seen, int depth)
        {
            if (value == null || depth > 8)
            {
                return;
            }

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal || value is byte[])
            {
                return;
            }

            if (!type.IsValueType && !seen.Add(value))
            {
                return;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                string name = property.Name;
                if (name.IndexOf("Seed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Fingerprint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("WallOrder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("StateHash", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Assert.Fail("Forbidden authoritative field reached the Contracts payload: " + name);
                }

                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object? child;
                try
                {
                    child = property.GetValue(value);
                }
                catch (TargetInvocationException)
                {
                    continue;
                }

                if (child is System.Collections.IEnumerable sequence && !(child is string))
                {
                    foreach (object? item in sequence)
                    {
                        ScanForbiddenFields(item, seen, depth + 1);
                    }
                }
                else
                {
                    ScanForbiddenFields(child, seen, depth + 1);
                }
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
