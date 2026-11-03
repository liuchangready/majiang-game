using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;
using DecisionView = Mahjong.Contracts.Wire.DecisionView;
using GamePhase = Mahjong.Contracts.Wire.GamePhase;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using PrivateCheatView = Mahjong.Contracts.Wire.PrivateCheatView;
using PublicSnapshot = Mahjong.Contracts.Wire.PublicSnapshot;

namespace Mahjong.LocalHost.Tests
{
    /// <summary>
    /// C04-D05 acceptance-matrix scenarios. Every test uses the real local room,
    /// production channel and public TableSession entry points; no test writes
    /// authority state or substitutes a scripted channel.
    /// </summary>
    public sealed class C04PrivacyStreamMatrixTests
    {
        private static readonly TimeSpan MatrixTimeout = TimeSpan.FromSeconds(45);

        [Fact]
        public async Task Public_and_player_snapshots_are_typed_and_seat_owned_on_real_channels()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);
            var tickets = new LocalTableConnectionTicket[4];
            var channels = new ITableChannel[4];
            var sessions = new TableSession[4];
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    tickets[seat] = room.CreatePlayerTicket(seat);
                    channels[seat] = await room.ConnectAsync(tickets[seat], timeout.Token);
                    sessions[seat] = new TableSession(channels[seat], new InMemoryOutboxStore());
                    await sessions[seat].OpenAsync(new TableSessionOpenRequest(tickets[seat].Value, "privacy-seat-" + seat), timeout.Token);
                }
                foreach (TableSession session in sessions)
                {
                    for (int i = 0; i < 16 && session.State != TableConnectionState.Ready; i++)
                        await session.PumpOnceAsync(timeout.Token);
                    Assert.Equal(TableConnectionState.Ready, session.State);
                }
                for (int owner = 0; owner < 4; owner++)
                {
                    var snapshot = Assert.IsType<PlayerSnapshot>(sessions[owner].PlayerSnapshot);
                    Assert.Equal(owner, snapshot.PrivateView.Seat);
                    Assert.Equal(owner, sessions[owner].Binding!.SeatId);
                    Assert.NotEmpty(snapshot.PrivateView.ClosedTiles);
                    Assert.InRange(snapshot.PrivateView.ClosedTiles.Count + (snapshot.PrivateView.HasDrawnTileId ? 1 : 0), 13, 14);
                    for (int foreign = 0; foreign < 4; foreign++)
                        if (foreign != owner) Assert.NotEqual(foreign, snapshot.PrivateView.Seat);
                }
                LocalTableConnectionTicket spectatorTicket = room.CreateSpectatorTicket();
                ITableChannel spectatorChannel = await room.ConnectAsync(spectatorTicket, timeout.Token);
                var spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.Value, "privacy-spectator"), timeout.Token);
                for (int i = 0; i < 16 && spectator.State != TableConnectionState.Ready; i++)
                    await spectator.PumpOnceAsync(timeout.Token);
                Assert.Equal(TableConnectionState.Ready, spectator.State);
                Assert.Null(spectator.PlayerSnapshot);
                var publicSnapshot = Assert.IsType<PublicSnapshot>(spectator.PublicSnapshot);
                Assert.NotEmpty(publicSnapshot.RoundId);
                Assert.NotNull(publicSnapshot.View);
                await spectator.CloseAsync("TEST_DONE", CancellationToken.None);
                await spectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
            finally
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    if (sessions[i] != null) try { await sessions[i].CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    if (channels[i] != null) try { await channels[i].CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
            }
        }

        [Fact]
        public async Task Legal_public_commands_produce_non_empty_owner_sleeve_and_fee_ledger()
        {
            await using var harness = new P41Harness();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);
            (ITableChannel channel, TableSession session) = await OpenLegacySessionAsync(harness, timeout.Token);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task pump = PumpAsync(session, pumpCts.Token);
            try
            {
                LocalPrivacyScenarioFacts facts = await LocalPrivacyScenario.CaptureSleeveAndFeeAsync(session, timeout.Token);
                Assert.False(string.IsNullOrWhiteSpace(facts.RoundId));
                Assert.True(facts.BaselineStateVersion < facts.TileMarkStateVersion);
                Assert.True(facts.TileMarkStateVersion < facts.SleeveStateVersion);
                Assert.True(facts.BaselineLedgerEntryCount >= 0);
                Assert.True(facts.TileMarkLedgerEntryCount > facts.BaselineLedgerEntryCount);
                Assert.True(facts.SleeveLedgerEntryCount > facts.TileMarkLedgerEntryCount);
                Assert.True(facts.TileMarkCheatPoints < facts.BaselineCheatPoints);
                Assert.True(facts.SleeveCheatPoints < facts.TileMarkCheatPoints);
                Assert.Equal(4, facts.MarkedTiles.Count);
                Assert.True(facts.MarkedTileCount >= 4);
                Assert.False(string.IsNullOrWhiteSpace(facts.SleeveOfferId));
                Assert.NotEmpty(facts.SleeveOfferTiles);
                Assert.True(facts.SleeveOfferTiles.Count >= 2);
            }
            finally
            {
                pumpCts.Cancel();
                try { await pump; } catch { }
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task Legal_public_commands_produce_non_empty_facts_for_each_human_owner_seat()
        {
            for (int targetSeat = 0; targetSeat < 4; targetSeat++)
            {
                await using var harness = new P41Harness();
                harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
                await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(75));
                var channels = new ITableChannel[4];
                var sessions = new TableSession[4];
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                using var driverCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var pumps = new List<Task>();
                Task? driver = null;
                try
                {
                    for (int seat = 0; seat < 4; seat++)
                    {
                        LocalTableConnectionTicket ticket = room.CreatePlayerTicket(seat);
                        channels[seat] = await room.ConnectAsync(ticket, timeout.Token);
                        sessions[seat] = new TableSession(channels[seat], new InMemoryOutboxStore());
                        await sessions[seat].OpenAsync(new TableSessionOpenRequest(ticket.Value, "owner-seat-" + seat), timeout.Token);
                        pumps.Add(PumpAsync(sessions[seat], pumpCts.Token));
                    }
                    foreach (TableSession session in sessions) await PumpUntilReadyAsync(session, timeout.Token);
                    driver = DriveOtherSeatsAsync(sessions, targetSeat, driverCts.Token);
                    LocalPrivacyScenarioFacts facts = await LocalPrivacyScenario.CaptureSleeveAndFeeAsync(
                        sessions[targetSeat],
                        timeout.Token,
                        submitChoice: false,
                        seatId: targetSeat);
                    driverCts.Cancel();
                    try { await driver; } catch (OperationCanceledException) { }
                    Assert.Equal(targetSeat, sessions[targetSeat].PlayerSnapshot!.PrivateView.Seat);
                    Assert.True(facts.TileMarkLedgerEntryCount > facts.BaselineLedgerEntryCount);
                    Assert.True(facts.SleeveLedgerEntryCount > facts.TileMarkLedgerEntryCount);
                    Assert.NotEmpty(facts.SleeveOfferTiles);
                    Assert.False(string.IsNullOrWhiteSpace(facts.SleeveOfferId));
                }
                finally
                {
                    driverCts.Cancel();
                    if (driver != null) { try { await driver; } catch { } }
                    pumpCts.Cancel();
                    try { await Task.WhenAll(pumps); } catch { }
                    for (int seat = 0; seat < 4; seat++)
                    {
                        if (sessions[seat] != null) try { await sessions[seat].CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                        if (channels[seat] != null) try { await channels[seat].CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                    }
                }
            }
        }

        [Fact]
        public async Task Real_room_four_owners_and_spectator_observe_same_version_non_empty_private_target_facts()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var channels = new ITableChannel[4];
            var sessions = new TableSession[4];
            ITableChannel? spectatorChannel = null;
            TableSession? spectator = null;
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            using var driverCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var pumps = new List<Task>();
            Task? driver = null;
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    LocalTableConnectionTicket ticket = room.CreatePlayerTicket(seat);
                    channels[seat] = await room.ConnectAsync(ticket, timeout.Token);
                    sessions[seat] = new TableSession(channels[seat], new InMemoryOutboxStore());
                    await sessions[seat].OpenAsync(new TableSessionOpenRequest(ticket.Value, "room-owner-" + seat), timeout.Token);
                    pumps.Add(PumpAsync(sessions[seat], pumpCts.Token));
                }
                foreach (TableSession session in sessions) await PumpUntilReadyAsync(session, timeout.Token);

                LocalTableConnectionTicket spectatorTicket = room.CreateSpectatorTicket();
                spectatorChannel = await room.ConnectAsync(spectatorTicket, timeout.Token);
                spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.Value, "room-owner-spectator"), timeout.Token);
                pumps.Add(PumpAsync(spectator, pumpCts.Token));
                await PumpUntilReadyAsync(spectator, timeout.Token);

                driver = DriveOtherSeatsAsync(sessions, 0, driverCts.Token);
                LocalPrivacyScenarioFacts facts = await LocalPrivacyScenario.CaptureSleeveAndFeeAsync(sessions[0], timeout.Token, submitChoice: false);
                driverCts.Cancel();
                try { await driver; } catch (OperationCanceledException) { }

                ulong targetVersion = facts.SleeveStateVersion;
                foreach (TableSession session in sessions)
                    targetVersion = Math.Max(targetVersion, session.PlayerSnapshot?.StateVersion ?? 0UL);
                targetVersion = Math.Max(targetVersion, spectator.PublicSnapshot?.StateVersion ?? 0UL);
                for (int i = 0; i < 300; i++)
                {
                    bool allAtVersion = true;
                    foreach (TableSession session in sessions)
                        allAtVersion &= (session.PlayerSnapshot?.StateVersion ?? 0UL) == targetVersion;
                    allAtVersion &= (spectator.PublicSnapshot?.StateVersion ?? 0UL) == targetVersion;
                    if (allAtVersion) break;
                    await Task.Delay(20, timeout.Token);
                }

                PlayerSnapshot[] snapshots = new PlayerSnapshot[4];
                for (int seat = 0; seat < 4; seat++)
                {
                    snapshots[seat] = Assert.IsType<PlayerSnapshot>(sessions[seat].PlayerSnapshot);
                    Assert.Equal(seat, snapshots[seat].PrivateView.Seat);
                    Assert.NotEmpty(snapshots[seat].PrivateView.ClosedTiles);
                    Assert.Equal(targetVersion, snapshots[seat].StateVersion);
                    Assert.NotNull(sessions[seat].Binding);
                    Assert.Equal(seat, sessions[seat].Binding!.SeatId);
                }
                PublicSnapshot publicSnapshot = Assert.IsType<PublicSnapshot>(spectator.PublicSnapshot);
                Assert.Null(spectator.PlayerSnapshot);
                Assert.Equal(targetVersion, publicSnapshot.StateVersion);
                Assert.NotNull(publicSnapshot.View);

                PrivateCheatView targetCheat = Assert.IsType<PrivateCheatView>(snapshots[0].PrivateView.Cheat);
                Assert.NotNull(targetCheat.SleeveBloom);
                Assert.False(string.IsNullOrWhiteSpace(targetCheat.SleeveBloom.OfferId));
                Assert.NotEmpty(targetCheat.LedgerEntries);
                Assert.True(targetCheat.CheatPoints < 120);
                string targetOffer = targetCheat.SleeveBloom.OfferId;
                for (int seat = 1; seat < 4; seat++)
                {
                    PrivateCheatView foreign = Assert.IsType<PrivateCheatView>(snapshots[seat].PrivateView.Cheat);
                    if (foreign.SleeveBloom != null)
                        Assert.NotEqual(targetOffer, foreign.SleeveBloom.OfferId);
                }
            }
            finally
            {
                pumpCts.Cancel();
                if (driver != null) { try { await driver; } catch { } }
                try { await Task.WhenAll(pumps); } catch { }
                if (spectator != null) try { await spectator.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (spectatorChannel != null) try { await spectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                for (int seat = 0; seat < 4; seat++)
                {
                    if (sessions[seat] != null) try { await sessions[seat].CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    if (channels[seat] != null) try { await channels[seat].CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
            }
        }

        [Fact]
        public async Task Slow_real_channel_queue_saturates_without_stopping_room_or_other_bindings()
        {
            await using var harness = new P41Harness();
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);

            LocalTableConnectionTicket playerTicket = room.CreatePlayerTicket(0);
            ITableChannel playerInner = await room.ConnectAsync(playerTicket, timeout.Token);
            var playerChannel = new CaptureChannel(playerInner);
            var player = new TableSession(playerChannel, new InMemoryOutboxStore());
            LocalTableConnectionTicket slowTicket = room.CreateSpectatorTicket();
            ITableChannel slowChannel = await room.ConnectAsync(slowTicket, timeout.Token);
            var slow = new TableSession(slowChannel, new InMemoryOutboxStore());
            using var playerPumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task? playerPump = null;
            try
            {
                await player.OpenAsync(new TableSessionOpenRequest(playerTicket.Value, "slow-player"), timeout.Token);
                await PumpUntilReadyAsync(player, timeout.Token);
                playerPump = PumpAsync(player, playerPumpCts.Token);

                await slow.OpenAsync(new TableSessionOpenRequest(slowTicket.Value, "slow-spectator"), timeout.Token);
                await PumpUntilReadyAsync(slow, timeout.Token);
                Assert.Equal(ProtocolRole.Spectator, slow.Binding!.Role);
                Assert.Null(slow.PlayerSnapshot);
                Assert.NotNull(slow.PublicSnapshot);
                string slowSessionId = slow.Binding.SessionId.Value;
                ulong slowBindingEpoch = slow.Binding.BindingEpoch.Value;

                int accepted = 0;
                try
                {
                    for (int i = 0; i < 600; i++)
                    {
                        await slowChannel.SendAsync(
                            WireCodec.Encode(HeartbeatEnvelope(slowSessionId, slowBindingEpoch, harness.Options.RoomId, i)),
                            timeout.Token);
                        accepted++;
                    }
                }
                catch (TableChannelClosedException)
                {
                    // The bounded queue closed the slow consumer; that is the expected
                    // saturation path and must not propagate to another binding.
                }

                Assert.True(accepted >= 257, "The slow channel never reached the production queue bound.");
                await Assert.ThrowsAnyAsync<TableChannelClosedException>(
                    async () => await slowChannel.ReceiveAsync(timeout.Token));

                Assert.Equal(TableConnectionState.Ready, player.State);
                Assert.NotNull(player.PlayerSnapshot);

                PlayerSnapshot beforeLiveRequest = player.PlayerSnapshot!;
                ulong beforeControlEpoch = beforeLiveRequest.PrivateView.Control?.ControlEpoch ?? 0UL;
                ulong expectedControlEpoch = checked(beforeControlEpoch + 1);
                Assert.False(
                    beforeLiveRequest.PrivateView.Control?.ControlledByComputer ?? true,
                    "The seat must be human-controlled before ReleaseControl.");
                BusinessPayload releasePayload = CommandPayloadFactory.ReleaseControl(
                    beforeLiveRequest.RoundId,
                    beforeControlEpoch);
                var liveRequest = new RequestId("slow-player-live-request");
                // A control receipt is resolved before the client requests and applies
                // the CONTROL_APPLIED full baseline. Waiting only for ReceiptResolved
                // can therefore observe a transient Synchronizing state. Register both
                // observations before submitting so neither ordering can be missed.
                var controlBarrier = new ControlBaselineBarrier(
                    liveRequest.Value,
                    player.Binding!.SessionId.Value,
                    expectedControlEpoch,
                    expectedControlledByComputer: true);
                player.ReceiptResolved += controlBarrier.OnReceipt;
                player.SnapshotChanged += controlBarrier.OnSnapshot;
                try
                {
                    var liveCommand = new ProtocolCommand(
                        CommandKind.ReleaseControl,
                        releasePayload,
                        liveRequest,
                        player.Binding!.Scope.ScopeId,
                        new StateVersion(beforeLiveRequest.StateVersion),
                        new RoundId(beforeLiveRequest.RoundId),
                        decisionId: null,
                        controlEpoch: new ControlEpoch(beforeControlEpoch));
                    CommandSubmission liveSubmission = await player.SubmitAsync(liveCommand, timeout.Token);
                    Assert.True(liveSubmission.Sent);
                    Assert.False(liveSubmission.WasDuplicate);

                    TableReceiptResolvedEventArgs liveReceipt = await LocalHostCommandTests.WithTimeoutAsync(
                        controlBarrier.Receipt,
                        timeout.Token);
                    TableSnapshotChangedEventArgs controlBaseline = await LocalHostCommandTests.WithTimeoutAsync(
                        controlBarrier.Baseline,
                        timeout.Token);

                    Assert.Equal(OutboxDisposition.Recorded, liveReceipt.Resolution.Disposition);
                    Assert.Equal("LOCAL_CONTROL", liveReceipt.Resolution.ReasonCode);
                    Assert.Equal(CommandKind.ReleaseControl, liveReceipt.Command.Kind);
                    Assert.Equal(liveRequest.Value, liveReceipt.Command.Key.RequestId.Value);

                    Assert.True(controlBaseline.IsFullBaseline);
                    PlayerSnapshot released = Assert.IsType<PlayerSnapshot>(controlBaseline.PlayerSnapshot);
                    Assert.Equal(player.Binding.SessionId.Value, released.SessionId);
                    Assert.Equal(expectedControlEpoch, released.PrivateView.Control.ControlEpoch);
                    Assert.True(released.PrivateView.Control.ControlledByComputer);
                    Assert.Equal(TableConnectionState.Ready, player.State);
                    Assert.Empty(player.PendingCommands);
                    Assert.Equal(1, playerChannel.CountCommandFrames(liveRequest.Value));

                    LocalTableRecoveryCapture capture = await LocalTableHost.CaptureRecoveryAsync(
                        harness.Options.SaveDirectory,
                        timeout.Token);
                    Assert.Equal((long)expectedControlEpoch, capture.Facts.ControlEpochs[0]);
                    Assert.Single(
                        capture.Facts.ControlRequests,
                        fact => fact.RequestId == liveRequest.Value);
                }
                finally
                {
                    player.ReceiptResolved -= controlBarrier.OnReceipt;
                    player.SnapshotChanged -= controlBarrier.OnSnapshot;
                }

                LocalTableConnectionTicket replacementTicket = room.CreateSpectatorTicket();
                ITableChannel replacementChannel = await room.ConnectAsync(replacementTicket, timeout.Token);
                var replacement = new TableSession(replacementChannel, new InMemoryOutboxStore());
                try
                {
                    await replacement.OpenAsync(new TableSessionOpenRequest(replacementTicket.Value, "slow-replacement"), timeout.Token);
                    await PumpUntilReadyAsync(replacement, timeout.Token);
                    Assert.NotNull(replacement.PublicSnapshot);
                }
                finally
                {
                    await replacement.CloseAsync("TEST_DONE", CancellationToken.None);
                    await replacementChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                }
            }
            finally
            {
                playerPumpCts.Cancel();
                if (playerPump != null) { try { await playerPump; } catch { } }
                try { await player.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await playerChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                try { await slow.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await slowChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
            }
        }

        [Fact]
        public void Control_baseline_barrier_rejects_receipt_only_foreign_and_stale_baselines()
        {
            const string requestId = "barrier-request-a";
            const string sessionId = "barrier-session-a";
            var receiptOnly = new ControlBaselineBarrier(requestId, sessionId, 1UL, expectedControlledByComputer: true);

            receiptOnly.OnReceipt(null, ControlReceiptEvent("barrier-other-request"));
            Assert.False(receiptOnly.ReceiptObserved, "Another request must not satisfy the receipt half.");

            // Receipt first must never unlock the barrier: the control baseline has
            // not been requested or applied yet at that point.
            receiptOnly.OnReceipt(null, ControlReceiptEvent(requestId));
            Assert.True(receiptOnly.ReceiptObserved);
            Assert.False(receiptOnly.IsSatisfied);

            // A delayed frame from the previous binding, an unrelated control update,
            // a claim-style update, and a non-baseline PUSH must all stay ignored.
            receiptOnly.OnSnapshot(null, ControlSnapshotEvent(true, "barrier-old-session", 1UL, automated: true));
            receiptOnly.OnSnapshot(null, ControlSnapshotEvent(true, sessionId, 0UL, automated: true));
            receiptOnly.OnSnapshot(null, ControlSnapshotEvent(true, sessionId, 1UL, automated: false));
            receiptOnly.OnSnapshot(null, ControlSnapshotEvent(false, sessionId, 1UL, automated: true));
            Assert.False(receiptOnly.BaselineObserved);
            Assert.Equal(4, receiptOnly.IgnoredBaselineCount);
            Assert.False(receiptOnly.IsSatisfied);

            receiptOnly.OnSnapshot(null, ControlSnapshotEvent(true, sessionId, 1UL, automated: true));
            Assert.True(receiptOnly.BaselineObserved);
            Assert.True(receiptOnly.IsSatisfied);

            // The matching baseline alone is also insufficient; the request receipt
            // is the other mandatory half of the synchronization point.
            var baselineFirst = new ControlBaselineBarrier("barrier-request-b", "barrier-session-b", 2UL, expectedControlledByComputer: false);
            baselineFirst.OnSnapshot(null, ControlSnapshotEvent(true, "barrier-session-b", 2UL, automated: false));
            Assert.True(baselineFirst.BaselineObserved);
            Assert.False(baselineFirst.IsSatisfied);
            baselineFirst.OnReceipt(null, ControlReceiptEvent("barrier-request-b"));
            Assert.True(baselineFirst.IsSatisfied);
        }

        [Fact]
        public async Task Control_baseline_barrier_bounded_wait_times_out_without_matching_baseline()
        {
            var barrier = new ControlBaselineBarrier("barrier-request-c", "barrier-session-c", 1UL, expectedControlledByComputer: true);
            barrier.OnReceipt(null, ControlReceiptEvent("barrier-request-c"));
            using (var shortBound = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => barrier.WaitAsync(shortBound.Token));
            }
            Assert.True(barrier.ReceiptObserved);
            Assert.False(barrier.BaselineObserved);
            Assert.False(barrier.IsSatisfied);

            barrier.OnSnapshot(null, ControlSnapshotEvent(true, "barrier-session-c", 1UL, automated: true));
            await barrier.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(barrier.IsSatisfied);
        }

        [Fact]
        public async Task Delayed_real_private_wire_from_old_seat_binding_is_ignored_by_new_session()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);

            LocalTableConnectionTicket seatZeroTicket = room.CreatePlayerTicket(0);
            ITableChannel seatZeroInner = await room.ConnectAsync(seatZeroTicket, timeout.Token);
            var seatZeroChannel = new CaptureChannel(seatZeroInner);
            var seatZero = new TableSession(seatZeroChannel, new InMemoryOutboxStore());
            LocalTableConnectionTicket seatOneTicket;
            ITableChannel seatOneInner;
            var seatOneChannel = default(CaptureChannel);
            TableSession? seatOne = null;
            try
            {
                await seatZero.OpenAsync(new TableSessionOpenRequest(seatZeroTicket.Value, "delayed-v0"), timeout.Token);
                await PumpUntilReadyAsync(seatZero, timeout.Token);
                var oldPrivate = Assert.IsType<PlayerSnapshot>(seatZero.PlayerSnapshot);
                byte[] delayedFrame = seatZeroChannel.CapturedPlayerSnapshotFrame
                    ?? throw new InvalidOperationException("No real serialized player snapshot was captured.");
                WireDecodeResult decodedOld = WireCodec.Decode(delayedFrame);
                Assert.True(decodedOld.IsOk);
                Assert.Equal(0, decodedOld.Envelope!.SeatId);
                Assert.Equal(seatZero.Binding!.SessionId.Value, decodedOld.Envelope.SessionId);

                seatOneTicket = room.CreatePlayerTicket(1);
                seatOneInner = await room.ConnectAsync(seatOneTicket, timeout.Token);
                seatOneChannel = new CaptureChannel(seatOneInner);
                seatOne = new TableSession(seatOneChannel, new InMemoryOutboxStore());
                await seatOne.OpenAsync(new TableSessionOpenRequest(seatOneTicket.Value, "delayed-v1"), timeout.Token);
                await PumpUntilReadyAsync(seatOne, timeout.Token);
                var current = Assert.IsType<PlayerSnapshot>(seatOne.PlayerSnapshot);
                Assert.Equal(1, current.PrivateView.Seat);
                string currentTiles = string.Join(",", current.PrivateView.ClosedTiles);

                seatOneChannel.InjectNextReceive(delayedFrame);
                await seatOne.PumpOnceAsync(timeout.Token);
                var after = Assert.IsType<PlayerSnapshot>(seatOne.PlayerSnapshot);
                Assert.Equal(seatOne.Binding!.SessionId.Value, after.SessionId);
                Assert.Equal(1, after.PrivateView.Seat);
                Assert.Equal(currentTiles, string.Join(",", after.PrivateView.ClosedTiles));
                Assert.NotEqual(seatZero.Binding.SessionId.Value, seatOne.Binding.SessionId.Value);
            }
            finally
            {
                if (seatOne != null)
                {
                    try { await seatOne.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    try { await seatOneChannel!.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
                try { await seatZero.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await seatZeroChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
            }
        }

        [Fact]
        public async Task Closed_player_to_spectator_to_new_player_receive_path_rejects_old_private_raw()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);

            LocalTableConnectionTicket firstTicket = room.CreatePlayerTicket(0);
            ITableChannel firstInner = await room.ConnectAsync(firstTicket, timeout.Token);
            var firstChannel = new CaptureChannel(firstInner);
            var first = new TableSession(firstChannel, new InMemoryOutboxStore());
            ITableChannel? spectatorChannel = null;
            TableSession? spectator = null;
            ITableChannel? reopenedInner = null;
            CaptureChannel? reopenedChannel = null;
            TableSession? reopened = null;
            try
            {
                await first.OpenAsync(new TableSessionOpenRequest(firstTicket.Value, "path-first"), timeout.Token);
                await PumpUntilReadyAsync(first, timeout.Token);
                byte[] oldFrame = firstChannel.CapturedPlayerSnapshotFrame
                    ?? throw new InvalidOperationException("No real private frame was captured before player close.");
                WireDecodeResult decoded = WireCodec.Decode(oldFrame);
                Assert.True(decoded.IsOk);
                Assert.Equal(0, decoded.Envelope!.SeatId);
                string oldSession = first.Binding!.SessionId.Value;

                await first.CloseAsync("ROLE_SWITCH", CancellationToken.None);
                await firstChannel.CloseAsync(new TableChannelCloseReason("ROLE_SWITCH", true), CancellationToken.None);

                LocalTableConnectionTicket spectatorTicket = room.CreateSpectatorTicket();
                spectatorChannel = await room.ConnectAsync(spectatorTicket, timeout.Token);
                spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.Value, "path-spectator"), timeout.Token);
                await PumpUntilReadyAsync(spectator, timeout.Token);
                Assert.Null(spectator.PlayerSnapshot);
                Assert.NotNull(spectator.PublicSnapshot);

                LocalTableConnectionTicket reopenedTicket = room.CreatePlayerTicket(0);
                reopenedInner = await room.ConnectAsync(reopenedTicket, timeout.Token);
                reopenedChannel = new CaptureChannel(reopenedInner);
                reopened = new TableSession(reopenedChannel, new InMemoryOutboxStore());
                await reopened.OpenAsync(new TableSessionOpenRequest(reopenedTicket.Value, "path-reopened"), timeout.Token);
                await PumpUntilReadyAsync(reopened, timeout.Token);
                var current = Assert.IsType<PlayerSnapshot>(reopened.PlayerSnapshot);
                string currentTiles = string.Join(",", current.PrivateView.ClosedTiles);
                Assert.NotEqual(oldSession, reopened.Binding!.SessionId.Value);

                reopenedChannel.InjectNextReceive(oldFrame);
                await reopened.PumpOnceAsync(timeout.Token);
                var after = Assert.IsType<PlayerSnapshot>(reopened.PlayerSnapshot);
                Assert.Equal(reopened.Binding.SessionId.Value, after.SessionId);
                Assert.Equal(0, after.PrivateView.Seat);
                Assert.Equal(currentTiles, string.Join(",", after.PrivateView.ClosedTiles));
            }
            finally
            {
                if (reopened != null) try { await reopened.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (reopenedChannel != null) try { await reopenedChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                if (spectator != null) try { await spectator.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (spectatorChannel != null) try { await spectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                try { await first.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await firstChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
            }
        }

        [Fact]
        public async Task New_room_cold_reopen_queries_original_scope_without_resubmitting_or_duplicating()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);
            var outbox = new InMemoryOutboxStore();
            LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, timeout.Token);
            TableSession? firstSession = null;
            ITableChannel? firstChannel = null;
            LocalTableRoom? recoveredRoom = null;
            CaptureChannel? recoveredChannel = null;
            TableSession? recoveredSession = null;
            try
            {
                LocalTableConnectionTicket firstTicket = room.CreatePlayerTicket(0);
                firstChannel = await room.ConnectAsync(firstTicket, timeout.Token);
                firstSession = new TableSession(firstChannel, outbox);
                await firstSession.OpenAsync(new TableSessionOpenRequest(firstTicket.Value, "recovery-first"), timeout.Token);
                await PumpUntilReadyAsync(firstSession, timeout.Token);
                PlayerSnapshot baseline = Assert.IsType<PlayerSnapshot>(firstSession.PlayerSnapshot);
                var decision = Assert.IsType<DecisionView>(baseline.PrivateView.Decision);
                var discard = decision.Actions.First(action => action.ActionType == (uint)ProtocolActionType.DiscardTile);
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    baseline.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: discard.TileIds);
                var request = new RequestId("privacy-recovery-original");
                RequestScopeId originalScope = firstSession.Binding!.Scope.ScopeId;
                string originalDigest = CanonicalPayloadDigest.Compute(payload).ToHex();
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    request,
                    originalScope,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));
                CommandSubmission submitted = await firstSession.SubmitAsync(command, timeout.Token);
                Assert.True(submitted.Sent);

                LocalTableRecoveryFacts before = (await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, timeout.Token)).Facts;
                LocalTableRecoveryRequestFact original = Assert.Single(before.Requests,
                    fact => fact.AccountId == "local-" + harness.Options.RoomId + "-seat0" &&
                        fact.ScopeId == originalScope.Value && fact.RequestId == request.Value);
                Assert.Equal("APPLIED", original.Status);

                await firstSession.CloseAsync("ROOM_COLD_REOPEN", CancellationToken.None);
                await firstChannel.CloseAsync(new TableChannelCloseReason("ROOM_COLD_REOPEN", true), CancellationToken.None);
                await room.DisposeAsync();

                recoveredRoom = await LocalTableHost.OpenRoomAsync(harness.Options, timeout.Token);
                LocalTableConnectionTicket recoveredTicket = recoveredRoom.CreatePlayerTicket(0);
                ITableChannel rawRecovered = await recoveredRoom.ConnectAsync(recoveredTicket, timeout.Token);
                recoveredChannel = new CaptureChannel(rawRecovered);
                recoveredSession = new TableSession(recoveredChannel, outbox);
                TableReceiptResolvedEventArgs? reconciled = null;
                recoveredSession.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == request.Value) reconciled = args;
                };
                await recoveredSession.OpenAsync(new TableSessionOpenRequest(recoveredTicket.Value, "recovery-reopened"), timeout.Token);
                await PumpUntilReadyAsync(recoveredSession, timeout.Token);
                for (int i = 0; i < 30 && recoveredSession.PendingCommands.Count > 0; i++)
                {
                    await recoveredSession.ReconcilePendingAsync(timeout.Token);
                    await recoveredSession.PumpOnceAsync(timeout.Token);
                }

                Assert.Empty(recoveredSession.PendingCommands);
                Assert.NotNull(reconciled);
                Assert.True(reconciled!.Resolution.FromReceiptLookup);
                Assert.Equal(Mahjong.Contracts.ReceiptStatus.Applied, reconciled.Resolution.ReceiptStatus);
                Assert.Equal(originalScope.Value, reconciled.Command.Key.ScopeId.Value);
                Assert.Equal(request.Value, reconciled.Command.Key.RequestId.Value);
                Assert.Equal(originalDigest, reconciled.Resolution.CanonicalPayloadSha256);
                Assert.Equal(0, recoveredChannel.CommandFrames);
                Assert.True(recoveredChannel.ReceiptQueryFrames > 0);

                LocalTableRecoveryFacts after = (await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, timeout.Token)).Facts;
                Assert.Equal(before.StateVersion, after.StateVersion);
                Assert.Equal(before.EventSeq, after.EventSeq);
                Assert.Equal(before.StateFingerprint, after.StateFingerprint);
                Assert.Equal(before.Ledger.EntriesSha256, after.Ledger.EntriesSha256);
                Assert.Equal(before.Ledger.BalancesSha256, after.Ledger.BalancesSha256);
                Assert.Equal(before.Physical.TilesSha256, after.Physical.TilesSha256);
            }
            finally
            {
                if (recoveredSession != null) try { await recoveredSession.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (recoveredChannel != null) try { await recoveredChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                if (recoveredRoom != null) try { await recoveredRoom.DisposeAsync(); } catch { }
                if (firstSession != null) try { await firstSession.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (firstChannel != null) try { await firstChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                try { await room.DisposeAsync(); } catch { }
            }
        }

        private static async Task<(ITableChannel, TableSession)> OpenLegacySessionAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cancellationToken);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "privacy-matrix", null), cancellationToken);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "privacy-matrix"), cancellationToken);
            await PumpUntilReadyAsync(session, cancellationToken);
            return (channel, session);
        }

        private static async Task PumpUntilReadyAsync(TableSession session, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 32 && session.State != TableConnectionState.Ready; i++)
            {
                if (session.State == TableConnectionState.Faulted)
                    throw new InvalidOperationException("Session faulted before Ready: " + (session.LastError ?? "<no error>"));
                await session.PumpOnceAsync(cancellationToken);
            }
            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        private static async Task PumpAsync(TableSession session, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                    await session.PumpOnceAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException ||
                exception is TableChannelClosedException ||
                exception is InvalidOperationException)
            {
            }
        }

        private static async Task DriveOtherSeatsAsync(
            TableSession[] sessions,
            int targetSeat,
            CancellationToken cancellationToken)
        {
            var lastSentVersion = new Dictionary<int, ulong>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    for (int seat = 0; seat < sessions.Length; seat++)
                    {
                        if (seat == targetSeat || sessions[seat] == null) continue;
                        PlayerSnapshot? snapshot = sessions[seat].PlayerSnapshot;
                        var board = snapshot?.PublicView?.Board;
                        if (snapshot == null || board == null || board.CurrentSeat != seat) continue;
                        if (sessions[seat].PendingCommands.Count > 0) continue;
                        if (lastSentVersion.TryGetValue(seat, out ulong sentVersion) &&
                            sentVersion == snapshot.StateVersion) continue;

                        ProtocolActionType? action = null;
                        uint[]? tiles = null;
                        switch (board.Phase)
                        {
                            case GamePhase.CheatWindow:
                            case GamePhase.Discard:
                                action = ProtocolActionType.DiscardTile;
                                uint tile = snapshot.PrivateView.HasDrawnTileId
                                    ? snapshot.PrivateView.DrawnTileId
                                    : snapshot.PrivateView.ClosedTiles[0];
                                tiles = new[] { tile };
                                break;
                            case GamePhase.ResponseWindow:
                            case GamePhase.ChankanWindow:
                                action = ProtocolActionType.Pass;
                                break;
                        }
                        if (!action.HasValue) continue;
                        try
                        {
                            await SubmitActionNoWaitAsync(sessions[seat], action.Value, tiles, cancellationToken);
                            lastSentVersion[seat] = snapshot.StateVersion;
                        }
                        catch (Exception exception) when (
                            exception is OperationCanceledException ||
                            exception is TableChannelClosedException ||
                            exception is InvalidOperationException)
                        {
                        }
                    }
                    await Task.Delay(10, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task SubmitActionNoWaitAsync(
            TableSession session,
            ProtocolActionType action,
            uint[]? tiles,
            CancellationToken cancellationToken)
        {
            PlayerSnapshot snapshot = session.PlayerSnapshot
                ?? throw new InvalidOperationException("No player snapshot is available for the room driver.");
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                snapshot.RoundId,
                "room-driver",
                0UL,
                action,
                tileIds: tiles);
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId("room-driver-" + Guid.NewGuid().ToString("N")),
                session.Binding!.Scope.ScopeId,
                new StateVersion(snapshot.StateVersion),
                new RoundId(snapshot.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(snapshot.PrivateView.Control?.ControlEpoch ?? 0UL));
            await session.SubmitAsync(command, cancellationToken);
        }

        private static Envelope HeartbeatEnvelope(string sessionId, ulong bindingEpoch, string roomId, int sequence)
        {
            return new Envelope
            {
                ProtocolMajor = Mahjong.Contracts.ProtocolConstants.Major,
                ProtocolMinor = Mahjong.Contracts.ProtocolConstants.Minor,
                RulesetVersion = Mahjong.Contracts.ProtocolConstants.RulesetVersion,
                ProjectionVersion = Mahjong.Contracts.ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Heartbeat,
                SessionId = sessionId,
                RoomId = roomId,
                SeatId = -1,
                BindingEpoch = bindingEpoch,
                Heartbeat = new Heartbeat
                {
                    Ping = new Ping { Nonce = sequence.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) }
                }
            };
        }

        private static TableReceiptResolvedEventArgs ControlReceiptEvent(string requestId)
        {
            var key = new IdempotencyKey(
                "acct-barrier",
                new RequestScopeId("scope-barrier"),
                new RequestId(requestId));
            var command = new PendingCommandSnapshot(
                key,
                CommandKind.ReleaseControl,
                new string('a', 64),
                payloadLength: 0,
                handedToTransport: true,
                deliveryUnknown: false,
                needsReconciliation: false,
                lastLookup: null,
                lastReceipt: null,
                queryInFlight: false);
            return new TableReceiptResolvedEventArgs(
                command,
                OutboxResolution.ControlApplied(
                    new StateVersion(1),
                    new string('a', 64),
                    fromLookup: false));
        }

        private static TableSnapshotChangedEventArgs ControlSnapshotEvent(
            bool isFullBaseline,
            string sessionId,
            ulong controlEpoch,
            bool automated)
        {
            var snapshot = new PlayerSnapshot
            {
                SessionId = sessionId,
                PrivateView = new Mahjong.Contracts.Wire.PlayerPrivateView
                {
                    Control = new Mahjong.Contracts.Wire.PlayerControlView
                    {
                        ControlEpoch = controlEpoch,
                        ControlledByComputer = automated
                    }
                }
            };
            return new TableSnapshotChangedEventArgs(
                new Mahjong.Contracts.StreamCursor(
                    new Mahjong.Contracts.StreamVersion(1),
                    new Mahjong.Contracts.ProjectionSeq(0)),
                isFullBaseline,
                snapshot,
                publicSnapshot: null,
                roomSnapshot: null,
                TableVisibleEventBatch.Empty);
        }

        /// <summary>
        /// Synchronization point for a control command: the original request receipt
        /// and the CONTROL_APPLIED full baseline for this exact request/binding/epoch
        /// must both be observed. A receipt alone is intentionally insufficient
        /// because the client requests its baseline only after resolving the receipt.
        /// </summary>
        private sealed class ControlBaselineBarrier
        {
            private readonly TaskCompletionSource<TableReceiptResolvedEventArgs> _receipt =
                new TaskCompletionSource<TableReceiptResolvedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<TableSnapshotChangedEventArgs> _baseline =
                new TaskCompletionSource<TableSnapshotChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly string _requestId;
            private readonly string _sessionId;
            private readonly ulong _expectedControlEpoch;
            private readonly bool _expectedControlledByComputer;
            private int _ignoredBaselineCount;

            public ControlBaselineBarrier(
                string requestId,
                string sessionId,
                ulong expectedControlEpoch,
                bool expectedControlledByComputer)
            {
                _requestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
                _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
                _expectedControlEpoch = expectedControlEpoch;
                _expectedControlledByComputer = expectedControlledByComputer;
            }

            public Task<TableReceiptResolvedEventArgs> Receipt => _receipt.Task;

            public Task<TableSnapshotChangedEventArgs> Baseline => _baseline.Task;

            public bool ReceiptObserved => _receipt.Task.IsCompleted;

            public bool BaselineObserved => _baseline.Task.IsCompleted;

            public bool IsSatisfied => ReceiptObserved && BaselineObserved;

            public int IgnoredBaselineCount => Volatile.Read(ref _ignoredBaselineCount);

            public void OnReceipt(object? sender, TableReceiptResolvedEventArgs args)
            {
                if (string.Equals(args.Command.Key.RequestId.Value, _requestId, StringComparison.Ordinal))
                {
                    _receipt.TrySetResult(args);
                }
            }

            public void OnSnapshot(object? sender, TableSnapshotChangedEventArgs args)
            {
                PlayerSnapshot? snapshot = args.PlayerSnapshot;
                if (args.IsFullBaseline &&
                    snapshot != null &&
                    string.Equals(snapshot.SessionId, _sessionId, StringComparison.Ordinal) &&
                    snapshot.PrivateView?.Control != null &&
                    snapshot.PrivateView.Control.ControlEpoch == _expectedControlEpoch &&
                    snapshot.PrivateView.Control.ControlledByComputer == _expectedControlledByComputer)
                {
                    _baseline.TrySetResult(args);
                    return;
                }

                Interlocked.Increment(ref _ignoredBaselineCount);
            }

            public Task WaitAsync(CancellationToken cancellationToken)
            {
                return Task.WhenAll(new Task[] { _receipt.Task, _baseline.Task }).WaitAsync(cancellationToken);
            }
        }

        private sealed class CaptureChannel : ITableChannel
        {
            private readonly ITableChannel _inner;
            private byte[]? _injectedFrame;

            public CaptureChannel(ITableChannel inner) => _inner = inner;

            private readonly List<string> _commandRequestIds = new List<string>();

            public bool IsOpen => _inner.IsOpen;
            public byte[]? CapturedPlayerSnapshotFrame { get; private set; }
            public int CommandFrames { get; private set; }
            public int ReceiptQueryFrames { get; private set; }

            public int CountCommandFrames(string requestId)
            {
                lock (_commandRequestIds)
                {
                    return _commandRequestIds.Count(id => string.Equals(id, requestId, StringComparison.Ordinal));
                }
            }

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                _inner.OpenAsync(request, cancellationToken);

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
            {
                WireDecodeResult decoded = WireCodec.Decode(wireEnvelope.ToArray());
                if (decoded.IsOk && decoded.Envelope != null)
                {
                    if (decoded.Envelope.MessageType == MessageType.Command)
                    {
                        CommandFrames++;
                        lock (_commandRequestIds)
                        {
                            if (!string.IsNullOrEmpty(decoded.Envelope.RequestId))
                                _commandRequestIds.Add(decoded.Envelope.RequestId);
                        }
                    }
                    if (decoded.Envelope.MessageType == MessageType.ReceiptQuery) ReceiptQueryFrames++;
                }
                return _inner.SendAsync(wireEnvelope, cancellationToken);
            }

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                byte[]? injected = Interlocked.Exchange(ref _injectedFrame, null);
                if (injected != null) return new ReadOnlyMemory<byte>(injected);
                ReadOnlyMemory<byte> frame = await _inner.ReceiveAsync(cancellationToken);
                WireDecodeResult decoded = WireCodec.Decode(frame.ToArray());
                if (decoded.IsOk && decoded.Envelope?.MessageType == MessageType.SyncResponse &&
                    decoded.Envelope.SyncResponse?.Target == SyncTarget.PlayerStream)
                    CapturedPlayerSnapshotFrame = frame.ToArray();
                return frame;
            }

            public void InjectNextReceive(byte[] frame)
            {
                if (frame == null) throw new ArgumentNullException(nameof(frame));
                if (Interlocked.CompareExchange(ref _injectedFrame, (byte[])frame.Clone(), null) != null)
                    throw new InvalidOperationException("An injected wire frame is already pending.");
            }

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);
        }
    }
}
