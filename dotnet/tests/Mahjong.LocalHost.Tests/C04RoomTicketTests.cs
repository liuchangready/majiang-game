using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class C04RoomTicketTests
    {
        [Fact]
        public async Task Four_players_and_spectator_share_one_room_with_owner_scoped_snapshots()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            var separateOptions = P41Harness.CloneOptions(harness.Options);
            separateOptions.RoomId = "room-independent";
            separateOptions.SaveDirectory = Path.Combine(harness.Root, "independent-room");
            await using LocalTableRoom independentRoom = await LocalTableHost.OpenRoomAsync(separateOptions, TestContext.Current.CancellationToken);
            LocalTableRecoveryCapture independentBefore = await LocalTableHost.CaptureRecoveryAsync(
                separateOptions.SaveDirectory, TestContext.Current.CancellationToken);
            var sessions = new List<(ITableChannel Channel, TableSession Session, LocalTableConnectionTicket Ticket)>();
            TableSession? firstSpectator = null;
            ITableChannel? firstSpectatorChannel = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    LocalTableConnectionTicket ticket = room.CreatePlayerTicket(seat);
                    ITableChannel channel = await room.ConnectAsync(ticket, timeout.Token);
                    var session = new TableSession(channel, new InMemoryOutboxStore());
                    await session.OpenAsync(new TableSessionOpenRequest(ticket.Value, "room-seat-" + seat), timeout.Token);
                    sessions.Add((channel, session, ticket));
                }

                for (int i = 0; i < sessions.Count; i++)
                {
                    try { await PumpUntilReadyAsync(sessions[i].Session, timeout.Token); }
                    catch
                    {
                        var runtime = Assert.IsType<LocalTableChannel>(sessions[i].Channel).RuntimeForMatch;
                        var frame = runtime?.LastStreamFrameForTest;
                        throw new InvalidOperationException("seat=" + i + "; session=" + (sessions[i].Session.LastError ?? "<none>") +
                            "; frame=" + (frame == null ? "<none>" : frame.Identity.Audience + "/" + frame.IsFullSnapshot +
                                "/player=" + (frame.Contract?.Player != null) + "/public=" + (frame.Contract?.Public != null)));
                    }
                }
                Assert.Equal(new[] { 0, 1, 2, 3 }, sessions.Select(item => item.Session.Binding!.SeatId).OrderBy(value => value));
                Assert.Equal(4, sessions.Select(item => item.Session.Binding!.SessionId.Value).Distinct().Count());
                LocalTableConnectionTicket firstSpectatorTicket = room.CreateSpectatorTicket();
                firstSpectatorChannel = await room.ConnectAsync(firstSpectatorTicket, timeout.Token);
                firstSpectator = new TableSession(firstSpectatorChannel, new InMemoryOutboxStore());
                await firstSpectator.OpenAsync(
                    new TableSessionOpenRequest(firstSpectatorTicket.Value, "room-spectator-before"), timeout.Token);
                await PumpUntilReadyAsync(firstSpectator, timeout.Token);
                ulong firstSpectatorVersion = Assert.IsType<PublicSnapshot>(firstSpectator.PublicSnapshot).StateVersion;
                ulong version = sessions[0].Session.PlayerSnapshot!.StateVersion;
                foreach (var item in sessions)
                {
                    var snapshot = Assert.IsType<PlayerSnapshot>(item.Session.PlayerSnapshot);
                    Assert.Equal(version, snapshot.StateVersion);
                    Assert.Equal(item.Ticket.SeatId, item.Session.Binding!.SeatId);
                    Assert.Equal(item.Ticket.SeatId, snapshot.PrivateView.Seat);
                    Assert.NotEmpty(snapshot.PrivateView.ClosedTiles);
                }

                TableSession ownerSession = sessions[0].Session;
                PlayerSnapshot ownerBefore = ownerSession.PlayerSnapshot!;
                var decision = Assert.IsType<Mahjong.Contracts.Wire.DecisionView>(ownerBefore.PrivateView.Decision);
                var discard = decision.Actions.First(action => action.ActionType == (uint)ProtocolActionType.DiscardTile);
                var applied = new TaskCompletionSource<TableReceiptResolvedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                ownerSession.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == "d05-four-seat-discard") applied.TrySetResult(args);
                };
                BusinessPayload actionPayload = CommandPayloadFactory.SubmitAction(
                    ownerBefore.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: discard.TileIds);
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    actionPayload,
                    new RequestId("d05-four-seat-discard"),
                    ownerSession.Binding!.Scope.ScopeId,
                    new StateVersion(ownerBefore.StateVersion),
                    new RoundId(ownerBefore.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));
                CommandSubmission sent = await ownerSession.SubmitAsync(command, timeout.Token);
                Assert.True(sent.Sent);
                while (!applied.Task.IsCompleted)
                    await ownerSession.PumpOnceAsync(timeout.Token);
                TableReceiptResolvedEventArgs appliedReceipt = await applied.Task;
                Assert.Equal(Mahjong.Contracts.ReceiptStatus.Applied, appliedReceipt.Resolution.ReceiptStatus);
                Assert.True(appliedReceipt.Resolution.CommandVersion.HasValue);
                LocalTableRecoveryCapture independentAfter = await LocalTableHost.CaptureRecoveryAsync(
                    separateOptions.SaveDirectory, timeout.Token);
                Assert.Equal(independentBefore.Facts.StateVersion, independentAfter.Facts.StateVersion);
                foreach (var item in sessions)
                {
                    while (item.Session.PlayerSnapshot!.StateVersion <= ownerBefore.StateVersion)
                        await item.Session.PumpOnceAsync(timeout.Token);
                    Assert.Equal(appliedReceipt.Resolution.CommandVersion.Value.Value, item.Session.PlayerSnapshot!.StateVersion);
                }

                LocalTableConnectionTicket secondSpectatorTicket = room.CreateSpectatorTicket();
                ITableChannel secondSpectatorChannel = await room.ConnectAsync(secondSpectatorTicket, timeout.Token);
                var secondSpectator = new TableSession(secondSpectatorChannel, new InMemoryOutboxStore());
                await secondSpectator.OpenAsync(
                    new TableSessionOpenRequest(secondSpectatorTicket.Value, "room-spectator-after"), timeout.Token);
                await PumpUntilReadyAsync(secondSpectator, timeout.Token);
                Assert.Equal(ProtocolRole.Spectator, secondSpectator.Binding!.Role);
                Assert.Equal(-1, secondSpectator.Binding.SeatId);
                Assert.Null(secondSpectator.PlayerSnapshot);
                var secondPublic = Assert.IsType<PublicSnapshot>(secondSpectator.PublicSnapshot);
                Assert.NotEqual(string.Empty, secondPublic.RoundId);
                Assert.True(secondPublic.StateVersion > firstSpectatorVersion,
                    "The second spectator baseline includes the intervening owner action.");
                Assert.True(firstSpectatorVersion ==
                    Assert.IsType<PublicSnapshot>(firstSpectator!.PublicSnapshot).StateVersion,
                    "The unpumped spectator retains its own earlier baseline.");
                await PumpUntilPublicVersionAsync(firstSpectator, secondPublic.StateVersion, timeout.Token);
                Assert.Equal(secondPublic.StateVersion,
                    Assert.IsType<PublicSnapshot>(firstSpectator.PublicSnapshot).StateVersion);

                await firstSpectator.CloseAsync("TEST_DONE", CancellationToken.None);
                await firstSpectatorChannel!.CloseAsync(
                    new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                Assert.Equal(TableConnectionState.Ready, secondSpectator.State);
                Assert.NotNull(secondSpectator.PublicSnapshot);

                await sessions[1].Session.CloseAsync("PLAYER_VIEW_CLOSED", CancellationToken.None);
                await sessions[1].Channel.CloseAsync(
                    new TableChannelCloseReason("PLAYER_VIEW_CLOSED", true), CancellationToken.None);
                Assert.Equal(TableConnectionState.Ready, sessions[0].Session.State);
                Assert.Equal(TableConnectionState.Ready, secondSpectator.State);

                var spectatorPayload = CommandPayloadFactory.SubmitAction(
                    sessions[0].Session.PlayerSnapshot!.RoundId,
                    "spectator-forged-action",
                    0UL,
                    ProtocolActionType.DiscardTile,
                    tileIds: new[] { sessions[0].Session.PlayerSnapshot!.PrivateView.ClosedTiles[0] });
                Envelope attack = new Envelope
                {
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    MessageType = MessageType.Command,
                    SessionId = secondSpectator.Binding.SessionId.Value,
                    RoomId = room.RoomId,
                    SeatId = -1,
                    BindingEpoch = secondSpectator.Binding.BindingEpoch.Value,
                    CorrelationId = "spectator-command-negative",
                    Command = new Command
                    {
                        Type = CommandType.SubmitAction,
                        CanonicalPayload = ByteString.CopyFrom(spectatorPayload.CopyBytes()),
                        PayloadSchemaVersion = CanonicalPayloadDigest.FormatVersion
                    }
                };
                await secondSpectatorChannel.SendAsync(WireCodec.Encode(attack), timeout.Token);
                WireDecodeResult rejected = WireCodec.Decode((await secondSpectatorChannel.ReceiveAsync(timeout.Token)).ToArray());
                Assert.NotNull(rejected.Envelope);
                Assert.Equal(MessageType.Error, rejected.Envelope!.MessageType);
                Assert.Equal("SPECTATOR_READ_ONLY", rejected.Envelope.Error!.DetailCode);

                await secondSpectator.CloseAsync("TEST_DONE", CancellationToken.None);
                await secondSpectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                LocalTableConnectionTicket replacement = room.CreateSpectatorTicket();
                ITableChannel replacementChannel = await room.ConnectAsync(replacement, timeout.Token);
                await replacementChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
            finally
            {
                for (int i = sessions.Count - 1; i >= 0; i--)
                {
                    try { await sessions[i].Session.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    try { await sessions[i].Channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
                if (firstSpectator != null) try { await firstSpectator.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (firstSpectatorChannel != null)
                    try { await firstSpectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
            }
        }

        [Fact]
        public async Task Ticket_reservation_is_single_use_and_room_lease_is_unique_per_directory()
        {
            await using var harness = new P41Harness();
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            LocalTableConnectionTicket ticket = room.CreatePlayerTicket(0);
            ITableChannel first = await room.ConnectAsync(ticket, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<LocalTableException>(async () =>
                await room.ConnectAsync(ticket, TestContext.Current.CancellationToken).AsTask());
            Assert.Throws<LocalTableException>(() => room.CreatePlayerTicket(0));
            var competingOptions = P41Harness.CloneOptions(harness.Options);
            competingOptions.OwnerInstanceId = "second-room-owner";
            LocalTableException conflict = await Assert.ThrowsAsync<LocalTableException>(async () =>
                await LocalTableHost.OpenRoomAsync(competingOptions, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(LocalTableErrorKind.LeaseConflict, conflict.ErrorKind);
            await first.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            LocalTableConnectionTicket afterClose = room.CreatePlayerTicket(0);
            Assert.NotSame(ticket, afterClose);
        }

        [Fact]
        public async Task Cancelled_hello_mismatch_and_expired_reservations_release_the_seat()
        {
            await using var harness = new P41Harness();
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);

            LocalTableConnectionTicket cancelledTicket = room.CreatePlayerTicket(0);
            ITableChannel cancelledChannel = await room.ConnectAsync(cancelledTicket, TestContext.Current.CancellationToken);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledChannel.OpenAsync(
                    new TableChannelConnectRequest(cancelledTicket.Value, "cancelled-open", null), cancelled.Token));
            }
            LocalTableConnectionTicket afterCancellation = room.CreatePlayerTicket(0);
            Assert.NotSame(cancelledTicket, afterCancellation);

            ITableChannel mismatchChannel = await room.ConnectAsync(afterCancellation, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<LocalTableException>(async () => await mismatchChannel.OpenAsync(
                new TableChannelConnectRequest("foreign-ticket", "foreign-hello", null), TestContext.Current.CancellationToken));
            LocalTableConnectionTicket afterMismatch = room.CreatePlayerTicket(0);
            Assert.NotSame(afterCancellation, afterMismatch);

            ITableChannel expiredChannel = await room.ConnectAsync(afterMismatch, TestContext.Current.CancellationToken);
            harness.Clock.UtcNowMs += 30_001L;
            LocalTableConnectionTicket afterExpiry = room.CreatePlayerTicket(0);
            Assert.NotSame(afterMismatch, afterExpiry);
            await Assert.ThrowsAsync<LocalTableException>(async () => await expiredChannel.OpenAsync(
                new TableChannelConnectRequest(afterMismatch.Value, "expired-hello", null), TestContext.Current.CancellationToken));

            await cancelledChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            await mismatchChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            await expiredChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
        }

        [Fact]
        public async Task Spectator_limit_and_close_before_open_release_reservations()
        {
            await using var harness = new P41Harness();
            harness.Options.MaxSpectators = 1;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            LocalTableConnectionTicket first = room.CreateSpectatorTicket();
            ITableChannel pending = await room.ConnectAsync(first, TestContext.Current.CancellationToken);
            Assert.Throws<LocalTableException>(() => room.CreateSpectatorTicket());
            await pending.CloseAsync(new TableChannelCloseReason("CANCEL_BEFORE_OPEN", true), CancellationToken.None);
            LocalTableConnectionTicket retry = room.CreateSpectatorTicket();
            Assert.NotSame(first, retry);
            ITableChannel failedHello = await room.ConnectAsync(retry, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<LocalTableException>(async () => await failedHello.OpenAsync(
                new TableChannelConnectRequest("wrong-ticket", "spectator-wrong-ticket", null), TestContext.Current.CancellationToken));
            LocalTableConnectionTicket afterFailure = room.CreateSpectatorTicket();
            Assert.NotSame(retry, afterFailure);
            await failedHello.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
        }

        [Fact]
        public async Task Delayed_real_private_wire_snapshot_from_old_seat_binding_is_ignored()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(
                harness.Options,
                TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            LocalTableConnectionTicket seatZeroTicket = room.CreatePlayerTicket(0);
            ITableChannel seatZeroInner = await room.ConnectAsync(seatZeroTicket, timeout.Token);
            var seatZeroChannel = new C04WireCaptureChannel(seatZeroInner);
            var seatZero = new TableSession(seatZeroChannel, new InMemoryOutboxStore());
            LocalTableConnectionTicket seatOneTicket;
            ITableChannel seatOneInner;
            var seatOneChannel = default(C04WireCaptureChannel);
            TableSession? seatOne = null;
            try
            {
                await seatZero.OpenAsync(new TableSessionOpenRequest(seatZeroTicket.Value, "delayed-wire-seat0"), timeout.Token);
                await PumpUntilReadyAsync(seatZero, timeout.Token);
                var oldPrivate = Assert.IsType<PlayerSnapshot>(seatZero.PlayerSnapshot);
                Assert.Equal(0, oldPrivate.PrivateView.Seat);
                Assert.NotEmpty(oldPrivate.PrivateView.ClosedTiles);
                byte[] delayedOldSeatFrame = Assert.IsType<byte[]>(seatZeroChannel.CapturedPlayerSnapshotFrame);
                WireDecodeResult captured = WireCodec.Decode(delayedOldSeatFrame);
                Assert.True(captured.IsOk, "The delayed fixture must be a real serialized production wire frame.");
                Assert.Equal(MessageType.SyncResponse, captured.Envelope!.MessageType);
                Assert.Equal(0, captured.Envelope.SeatId);
                Assert.Equal(seatZero.Binding!.SessionId.Value, captured.Envelope.SessionId);
                Assert.Equal(seatZero.Binding.BindingEpoch.Value, captured.Envelope.BindingEpoch);

                seatOneTicket = room.CreatePlayerTicket(1);
                seatOneInner = await room.ConnectAsync(seatOneTicket, timeout.Token);
                seatOneChannel = new C04WireCaptureChannel(seatOneInner);
                seatOne = new TableSession(seatOneChannel, new InMemoryOutboxStore());
                await seatOne.OpenAsync(new TableSessionOpenRequest(seatOneTicket.Value, "delayed-wire-seat1"), timeout.Token);
                await PumpUntilReadyAsync(seatOne, timeout.Token);
                var current = Assert.IsType<PlayerSnapshot>(seatOne.PlayerSnapshot);
                Assert.Equal(1, current.PrivateView.Seat);
                Assert.NotEmpty(current.PrivateView.ClosedTiles);
                string currentTiles = string.Join(",", current.PrivateView.ClosedTiles);

                // Replay the captured bytes from the old seat 0 session only after the
                // seat 1 session has established its own binding and full baseline.
                seatOneChannel.InjectNextReceive(delayedOldSeatFrame);
                await seatOne.PumpOnceAsync(timeout.Token);

                var afterDelayedFrame = Assert.IsType<PlayerSnapshot>(seatOne.PlayerSnapshot);
                Assert.Equal(seatOne.Binding!.SessionId.Value, afterDelayedFrame.SessionId);
                Assert.Equal(1, afterDelayedFrame.PrivateView.Seat);
                Assert.Equal(currentTiles, string.Join(",", afterDelayedFrame.PrivateView.ClosedTiles));
                Assert.NotEqual(seatZero.Binding!.SessionId.Value, seatOne.Binding.SessionId.Value);
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
        public async Task New_room_cold_reopen_queries_original_scope_without_resubmitting_or_duplicating()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var outbox = new InMemoryOutboxStore();
            LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, timeout.Token);
            TableSession? firstSession = null;
            ITableChannel? firstChannel = null;
            LocalTableRoom? recoveredRoom = null;
            C04WireCaptureChannel? recoveredChannel = null;
            TableSession? recoveredSession = null;
            LocalTableConnectionTicket firstTicket = room.CreatePlayerTicket(0);
            RequestScopeId originalScope = default;
            string originalDigest = string.Empty;
            string originalPayloadDigest = string.Empty;
            RequestId originalRequest = new RequestId("d05-room-reopen-original-discard");
            LocalTableRecoveryFacts beforeRecovery;

            try
            {
                firstChannel = await room.ConnectAsync(firstTicket, timeout.Token);
                firstSession = new TableSession(firstChannel, outbox);
                await firstSession.OpenAsync(new TableSessionOpenRequest(firstTicket.Value, "room-recovery-first"), timeout.Token);
                await PumpUntilReadyAsync(firstSession, timeout.Token);
                PlayerSnapshot baseline = Assert.IsType<PlayerSnapshot>(firstSession.PlayerSnapshot);
                var decision = Assert.IsType<Mahjong.Contracts.Wire.DecisionView>(baseline.PrivateView.Decision);
                var discard = decision.Actions.First(action => action.ActionType == (uint)ProtocolActionType.DiscardTile);
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    baseline.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: discard.TileIds);
                originalScope = firstSession.Binding!.Scope.ScopeId;
                originalDigest = CanonicalPayloadDigest.Compute(payload).ToHex();
                originalPayloadDigest = TablePayloadSha256.Compute(payload.CopyBytes()).ToHex();
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    originalRequest,
                    originalScope,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));
                CommandSubmission submitted = await firstSession.SubmitAsync(command, timeout.Token);
                Assert.True(submitted.Sent);
                Assert.Equal(originalDigest, submitted.Command.DigestSha256);

                beforeRecovery = (await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, timeout.Token)).Facts;
                LocalTableRecoveryRequestFact originalFact = Assert.Single(beforeRecovery.Requests,
                    fact => fact.AccountId == "local-" + harness.Options.RoomId + "-seat0" &&
                        fact.ScopeId == originalScope.Value && fact.RequestId == originalRequest.Value);
                Assert.True(string.Equals(originalPayloadDigest, originalFact.PayloadSha256, StringComparison.OrdinalIgnoreCase),
                    "The durable payload hash must match the canonical request bytes.");
                Assert.Equal("APPLIED", originalFact.Status);

                await firstSession.CloseAsync("ROOM_COLD_REOPEN", CancellationToken.None);
                await firstChannel.CloseAsync(new TableChannelCloseReason("ROOM_COLD_REOPEN", true), CancellationToken.None);
                await room.DisposeAsync();

                recoveredRoom = await LocalTableHost.OpenRoomAsync(harness.Options, timeout.Token);
                LocalTableConnectionTicket recoveredTicket = recoveredRoom.CreatePlayerTicket(0);
                ITableChannel rawRecoveredChannel = await recoveredRoom.ConnectAsync(recoveredTicket, timeout.Token);
                recoveredChannel = new C04WireCaptureChannel(rawRecoveredChannel);
                recoveredSession = new TableSession(recoveredChannel, outbox);
                TableReceiptResolvedEventArgs? reconciled = null;
                recoveredSession.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == originalRequest.Value) reconciled = args;
                };
                await recoveredSession.OpenAsync(
                    new TableSessionOpenRequest(recoveredTicket.Value, "room-recovery-reopened"), timeout.Token);
                await PumpUntilReadyAsync(recoveredSession, timeout.Token);
                for (int i = 0; i < 20 && recoveredSession.PendingCommands.Count > 0; i++)
                {
                    await recoveredSession.ReconcilePendingAsync(timeout.Token);
                    await recoveredSession.PumpOnceAsync(timeout.Token);
                }

                Assert.Empty(recoveredSession.PendingCommands);
                Assert.NotNull(reconciled);
                Assert.True(reconciled!.Resolution.FromReceiptLookup);
                Assert.Equal(Mahjong.Contracts.ReceiptStatus.Applied, reconciled.Resolution.ReceiptStatus);
                Assert.Equal(originalScope.Value, reconciled.Command.Key.ScopeId.Value);
                Assert.Equal(originalRequest.Value, reconciled.Command.Key.RequestId.Value);
                Assert.Equal(originalDigest, reconciled.Resolution.CanonicalPayloadSha256);
                Assert.Equal(0, recoveredChannel.CommandFrames);
                Assert.True(recoveredChannel.ReceiptQueryFrames > 0);

                LocalTableRecoveryFacts afterRecovery =
                    (await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, timeout.Token)).Facts;
                Assert.Equal(beforeRecovery.StateVersion, afterRecovery.StateVersion);
                Assert.Equal(beforeRecovery.EventSeq, afterRecovery.EventSeq);
                Assert.Equal(beforeRecovery.StateFingerprint, afterRecovery.StateFingerprint);
                Assert.Equal(beforeRecovery.Ledger.EntriesSha256, afterRecovery.Ledger.EntriesSha256);
                Assert.Equal(beforeRecovery.Ledger.BalancesSha256, afterRecovery.Ledger.BalancesSha256);
                Assert.Equal(beforeRecovery.Physical.TilesSha256, afterRecovery.Physical.TilesSha256);
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

        [Fact]
        public async Task ColdRecoveryUsesPersistedAllAiControllersInsteadOfNewHumanSeatPlan()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using (LocalTableRoom initial = await LocalTableHost.OpenRoomAsync(harness.Options, ct))
            {
                Assert.True(initial.CanTickAuthority, "A fresh all-AI room must tick without a human binding.");
            }

            // The caller now asks for four human seats. The persisted all-AI controllers
            // (and their epochs) must win; SeatPlan only initializes a fresh checkpoint.
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom restored = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            Assert.True(restored.CanTickAuthority, "The recovered all-AI room must tick without a human binding.");
            await using RoomSession spectator = await RoomSession.OpenSpectatorAsync(restored, "recovered-all-ai", ct);
            TableControlSnapshot control = await spectator.Runtime.ActorForTest.GetControlSnapshotAsync(ct);
            Assert.All(control.Seats, seat =>
            {
                Assert.Equal(SeatControllerKind.Computer, seat.Kind);
                Assert.True(seat.IsAutomated);
            });
            Assert.DoesNotContain(
                restored.SeatPlan.HumanSeats,
                seat => control.Seats[seat].Kind == SeatControllerKind.LocalHuman);
        }

        [Fact]
        public async Task RecoveredReleasedSeatTicksWithoutHumanBindingAndKeepsEpoch()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using (LocalTableRoom initial = await LocalTableHost.OpenRoomAsync(harness.Options, ct))
            {
                await using RoomSession releasedPlayer = await RoomSession.OpenPlayerAsync(initial, 0, "recover-release", ct);
                TableReceiptResolvedEventArgs released = await releasedPlayer.SubmitControlAsync(
                    CommandKind.ReleaseControl,
                    0UL,
                    "room-recover-release",
                    ct);
                Assert.Equal(OutboxDisposition.Recorded, released.Resolution.Disposition);
                Assert.Equal("LOCAL_CONTROL", released.Resolution.ReasonCode);
            }

            await using LocalTableRoom restored = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            Assert.True(restored.CanTickAuthority, "A recovered released/AI seat must not require a human binding.");
            await using RoomSession spectator = await RoomSession.OpenSpectatorAsync(restored, "recover-release-spectator", ct);
            TableControlSnapshot control = await spectator.Runtime.ActorForTest.GetControlSnapshotAsync(ct);
            Assert.True(control.Seats[0].IsAutomated);
            Assert.Equal(1L, control.Seats[0].ControlEpoch);
            Assert.True(restored.CanTickAuthority, "Adding a spectator must not change the recovered authority.");

            await using RoomSession player = await RoomSession.OpenPlayerAsync(restored, 0, "recover-release-claim", ct);
            TableReceiptResolvedEventArgs stale = await player.SubmitControlAsync(
                CommandKind.ClaimControl,
                0UL,
                "recover-release-old-epoch",
                ct);
            Assert.Equal(OutboxDisposition.Rejected, stale.Resolution.Disposition);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, stale.Resolution.ReasonCode);
            TableReceiptResolvedEventArgs claim = await player.SubmitControlAsync(
                CommandKind.ClaimControl,
                1UL,
                "recover-release-new-epoch",
                ct);
            Assert.Equal(OutboxDisposition.Recorded, claim.Resolution.Disposition);
            TableControlSnapshot claimed = await player.Runtime.ActorForTest.GetControlSnapshotAsync(ct);
            Assert.Equal(SeatControllerKind.LocalHuman, claimed.Seats[0].Kind);
            Assert.Equal(2L, claimed.Seats[0].ControlEpoch);
            Assert.True(restored.CanTickAuthority, "A recovered started room keeps ticking across a claim.");
        }

        [Fact]
        public async Task RecoveredHumanControllerSurvivesASeatPlanThatDropsIt()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            harness.Options.SeatPlan = LocalTableSeatPlan.SingleHumanSeatZero;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using (LocalTableRoom initial = await LocalTableHost.OpenRoomAsync(harness.Options, ct))
            {
                Assert.False(initial.CanTickAuthority, "A fresh LocalHuman seat without a binding is still AwaitingSeats.");
            }

            // Four human seats are now requested. The recovered controller is LocalHuman
            // for seat 0 and Computer for 1..3; the new plan must not turn 1..3 into humans.
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom restored = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            Assert.False(restored.CanTickAuthority, "The recovered LocalHuman seat still needs a live binding.");
            await using RoomSession player = await RoomSession.OpenPlayerAsync(restored, 0, "recovered-human", ct);
            Assert.True(restored.CanTickAuthority,
                "Only recovered seat 0 is human; binding it must satisfy AwaitingSeats regardless of the four-seat plan.");
            TableControlSnapshot control = await player.Runtime.ActorForTest.GetControlSnapshotAsync(ct);
            Assert.Equal(SeatControllerKind.LocalHuman, control.Seats[0].Kind);
            Assert.All(new[] { 1, 2, 3 }, seat => Assert.Equal(SeatControllerKind.Computer, control.Seats[seat].Kind));
        }

        [Fact]
        public async Task FreshAllHumanRoomStartsOnlyAfterEveryInitialHumanBinds()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            var players = new List<RoomSession>();
            try
            {
                Assert.False(room.CanTickAuthority);
                for (int seat = 0; seat < 4; seat++)
                {
                    players.Add(await RoomSession.OpenPlayerAsync(room, seat, "human-" + seat, ct));
                    Assert.Equal(seat == 3, room.CanTickAuthority);
                }

                int retainedBefore = room.RetainedTicketCountForTest;
                await players[1].DisposeAsync();
                players.RemoveAt(1);
                Assert.True(room.CanTickAuthority, "A started room must not fall back to AwaitingSeats after a detach.");
                Assert.Equal(retainedBefore - 1, room.RetainedTicketCountForTest);
            }
            finally
            {
                for (int i = players.Count - 1; i >= 0; i--) await players[i].DisposeAsync();
            }
        }

        [Fact]
        public async Task RecoveredReleasedSeatActuallyAdvancesAsAiWithoutHumanBinding()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = true;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using (LocalTableRoom initial = await LocalTableHost.OpenRoomAsync(harness.Options, ct))
            {
                await using RoomSession player = await RoomSession.OpenPlayerAsync(initial, 0, "released-ai-progress", ct);
                await WaitUntilAsync(
                    () => !string.IsNullOrEmpty(player.Runtime.ActorForTest.GetPendingDecisionId(0)),
                    "the initial human decision",
                    ct);
                TableReceiptResolvedEventArgs released = await player.SubmitControlAsync(
                    CommandKind.ReleaseControl,
                    0UL,
                    "released-ai-progress-release",
                    ct);
                Assert.Equal(OutboxDisposition.Recorded, released.Resolution.Disposition);
                bool automated = false;
                for (int i = 0; i < 500 && !automated; i++)
                {
                    TableControlSnapshot control = await player.Runtime.ActorForTest.GetControlSnapshotAsync(ct);
                    automated = control.Seats[0].IsAutomated && control.Seats[0].ControlEpoch == 1L;
                    if (!automated) await Task.Delay(10, ct);
                }
                Assert.True(automated, "The released seat must become an automated controller with epoch 1.");
            }

            await using LocalTableRoom restored = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            Assert.True(restored.CanTickAuthority);
            await using RoomSession spectator = await RoomSession.OpenSpectatorAsync(restored, "released-ai-progress-spectator", ct);
            TableActor actor = spectator.Runtime.ActorForTest;
            ulong startVersion = (await actor.GetSnapshotAsync(ct)).StateVersion.Value;
            bool advanced = false;
            TableSnapshot last = await actor.GetSnapshotAsync(ct);
            for (int i = 0; i < 500 && !advanced; i++)
            {
                await Task.Delay(10, ct);
                last = await actor.GetSnapshotAsync(ct);
                advanced = last.StateVersion.Value > startVersion || last.IsFinished;
            }
            Assert.True(advanced,
                "A recovered released AI seat must advance on its own without any human binding; start=" + startVersion +
                "; state=" + last.StateVersion.Value + "; phase=" + last.Phase + "; finished=" + last.IsFinished +
                "; pending0=" + (actor.GetPendingDecisionId(0) ?? "<none>") +
                "; driver=" + spectator.Runtime.DriverForTest.DescribeDriverForTest());
        }

        [Fact]
        public async Task AutoPlayDriverCannotLeaveAwaitingSeatsBeforeAControlSeedExists()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = true;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            // The actor starts its autonomous driver during construction, before the room
            // has an authority. A default all-false seed must not make AwaitingSeats pass.
            await Task.Delay(100, ct);
            Assert.False(room.CanTickAuthority);
            await using RoomSession spectator = await RoomSession.OpenSpectatorAsync(room, "startup-spectator", ct);
            await Task.Delay(100, ct);
            Assert.False(room.CanTickAuthority);
            Assert.True(string.IsNullOrEmpty(spectator.Runtime.ActorForTest.GetPendingDecisionId(0)),
                "The driver must not open a decision while the human seat is unbound.");
        }

        [Fact]
        public async Task SpectatorCloseLeavesAuthorityAndSeatControlUnchanged()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            await using RoomSession spectator = await RoomSession.OpenSpectatorAsync(room, "spectator-authority", ct);
            Assert.False(room.CanTickAuthority, "A spectator binding never counts toward AwaitingSeats.");
            await using RoomSession player = await RoomSession.OpenPlayerAsync(room, 0, "spectator-authority-player", ct);
            Assert.True(room.CanTickAuthority);
            TableControlSnapshot before = await player.Runtime.ActorForTest.GetControlSnapshotAsync(ct);
            await spectator.DisposeAsync();
            Assert.True(room.CanTickAuthority);
            Assert.Equal(TableConnectionState.Ready, player.Session.State);
            TableControlSnapshot after = await player.Runtime.ActorForTest.GetControlSnapshotAsync(ct);
            Assert.Equal(before.Seats[0].Kind, after.Seats[0].Kind);
            Assert.Equal(before.Seats[0].ControlEpoch, after.Seats[0].ControlEpoch);
        }

        [Fact]
        public async Task OpenDecisionDeadlineStillRunsAfterOwnerDetachWithoutOpeningANewOne()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            await using RoomSession spectator = await RoomSession.OpenSpectatorAsync(room, "deadline-spectator", ct);
            RoomSession player = await RoomSession.OpenPlayerAsync(room, 0, "deadline-player", ct);
            string decision;
            try
            {
                await WaitUntilAsync(
                    () => !string.IsNullOrEmpty(player.Runtime.ActorForTest.GetPendingDecisionId(0)),
                    "an authoritative decision for the live human",
                    ct);
                decision = player.Runtime.ActorForTest.GetPendingDecisionId(0)!;
                Assert.True(room.CanTickAuthority);
                // Move five seconds into the already-open 23s deadline before detaching.
                // A detach must neither reset nor extend it.
                harness.Clock.MonotonicMillis += 5_000L;
                harness.Clock.UtcNowMs += 5_000L;
            }
            finally
            {
                await player.DisposeAsync();
            }

            Assert.True(room.CanTickAuthority, "A started room keeps ticking after the owner detaches.");
            TableActor actor = spectator.Runtime.ActorForTest;
            // 19 more seconds crosses the original 23s deadline, but would not cross a
            // deadline that had been silently re-armed at detach time (5s + 23s = 28s).
            harness.Clock.MonotonicMillis += 19_000L;
            harness.Clock.UtcNowMs += 19_000L;
            await WaitUntilAsync(
                () => !string.Equals(actor.GetPendingDecisionId(0), decision, StringComparison.Ordinal),
                "the already-open deadline to retire the decision",
                ct);
            await Task.Delay(300, ct);
            Assert.True(string.IsNullOrEmpty(actor.GetPendingDecisionId(0)),
                "No new decision may be opened for a LocalHuman seat without a live binding.");
            TableControlSnapshot control = await actor.GetControlSnapshotAsync(ct);
            Assert.Equal(SeatControllerKind.LocalHuman, control.Seats[0].Kind);
            Assert.Equal(0L, control.Seats[0].ControlEpoch);
        }

        [Fact]
        public async Task TicketRetirementIsBoundedAndDetachedTicketsCannotBeReplayed()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            var retired = new List<LocalTableConnectionTicket>();
            const int cycles = 128;
            for (int i = 0; i < cycles; i++)
            {
                LocalTableConnectionTicket ticket = room.CreatePlayerTicket(0);
                ITableChannel channel = await room.ConnectAsync(ticket, ct);
                await channel.OpenAsync(new TableChannelConnectRequest(ticket.Value, "ticket-cycle-" + i, null), ct);
                Assert.Equal(1, room.RetainedTicketCountForTest);
                Assert.Equal(i, room.RetiredTicketCountForTest);
                await channel.CloseAsync(new TableChannelCloseReason("TICKET_CYCLE", true), CancellationToken.None);
                Assert.Equal(0, room.RetainedTicketCountForTest);
                Assert.Equal(i + 1, room.RetiredTicketCountForTest);
                retired.Add(ticket);
            }

            Assert.Equal(cycles, room.RetiredTicketCountForTest);
            Assert.Equal(0, room.RetainedTicketCountForTest);
            foreach (LocalTableConnectionTicket ticket in retired)
            {
                await Assert.ThrowsAsync<LocalTableException>(async () =>
                    await room.ConnectAsync(ticket, ct).AsTask());
            }

            LocalTableConnectionTicket replacement = room.CreatePlayerTicket(0);
            Assert.Equal(1, room.RetainedTicketCountForTest);
            Assert.NotSame(retired[^1], replacement);
        }

        [Fact]
        public async Task ExpiredFailedDetachedAndConcurrentTicketsAreRejectedOnceAndReusableOnlyByANewTicket()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct);

            // Concurrent reservation: the room lock must admit exactly one channel.
            LocalTableConnectionTicket contended = room.CreatePlayerTicket(0);
            Task<ITableChannel>[] attempts =
            {
                Task.Run(async () => await room.ConnectAsync(contended, ct)),
                Task.Run(async () => await room.ConnectAsync(contended, ct))
            };
            var admitted = new List<ITableChannel>();
            var rejected = new List<Exception>();
            foreach (Task<ITableChannel> attempt in attempts)
            {
                try { admitted.Add(await attempt); }
                catch (Exception exception) { rejected.Add(exception); }
            }
            Assert.Single(admitted);
            Assert.Single(rejected);
            Assert.IsType<LocalTableException>(rejected[0]);
            await admitted[0].CloseAsync(new TableChannelCloseReason("CONCURRENT_DONE", true), CancellationToken.None);
            await Assert.ThrowsAsync<LocalTableException>(async () => await room.ConnectAsync(contended, ct).AsTask());
            LocalTableConnectionTicket afterContention = room.CreatePlayerTicket(0);
            Assert.NotSame(contended, afterContention);

            // Expired reservation: prune retires it and a replay buys a new reservation.
            LocalTableConnectionTicket expiring = afterContention;
            harness.Clock.UtcNowMs += 30_001L;
            LocalTableConnectionTicket afterExpiry = room.CreatePlayerTicket(0);
            Assert.NotSame(expiring, afterExpiry);
            await Assert.ThrowsAsync<LocalTableException>(async () => await room.ConnectAsync(expiring, ct).AsTask());
            ITableChannel expiringChannel = await room.ConnectAsync(afterExpiry, ct);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await expiringChannel.OpenAsync(
                    new TableChannelConnectRequest(afterExpiry.Value, "cancelled-ticket", null), cancelled.Token));
            }
            await Assert.ThrowsAsync<LocalTableException>(async () => await room.ConnectAsync(afterExpiry, ct).AsTask());
            await expiringChannel.CloseAsync(new TableChannelCloseReason("CANCEL_DONE", true), CancellationToken.None);
            LocalTableConnectionTicket afterCancel = room.CreatePlayerTicket(0);
            Assert.NotSame(afterExpiry, afterCancel);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, string description, CancellationToken ct)
        {
            for (int i = 0; i < 500 && !predicate(); i++) await Task.Delay(10, ct);
            Assert.True(predicate(), "Timed out waiting for " + description);
        }

        private sealed class RoomSession : IAsyncDisposable
        {
            private readonly ITableChannel _channel;

            private RoomSession(ITableChannel channel, TableSession session)
            {
                _channel = channel;
                Session = session;
            }

            public TableSession Session { get; }

            public LocalTableRuntime Runtime => Assert.IsType<LocalTableChannel>(_channel).RuntimeForMatch!;

            public static async Task<RoomSession> OpenPlayerAsync(
                LocalTableRoom room,
                int seat,
                string label,
                CancellationToken ct)
            {
                LocalTableConnectionTicket ticket = room.CreatePlayerTicket(seat);
                ITableChannel channel = await room.ConnectAsync(ticket, ct);
                var session = new TableSession(channel, new InMemoryOutboxStore());
                await session.OpenAsync(new TableSessionOpenRequest(ticket.Value, label), ct);
                await PumpUntilReadyAsync(session, ct);
                return new RoomSession(channel, session);
            }

            public static async Task<RoomSession> OpenSpectatorAsync(
                LocalTableRoom room,
                string label,
                CancellationToken ct)
            {
                LocalTableConnectionTicket ticket = room.CreateSpectatorTicket();
                ITableChannel channel = await room.ConnectAsync(ticket, ct);
                var session = new TableSession(channel, new InMemoryOutboxStore());
                await session.OpenAsync(new TableSessionOpenRequest(ticket.Value, label), ct);
                await PumpUntilReadyAsync(session, ct);
                return new RoomSession(channel, session);
            }

            public async Task<TableReceiptResolvedEventArgs> SubmitControlAsync(
                CommandKind kind,
                ulong expectedEpoch,
                string requestId,
                CancellationToken ct)
            {
                PlayerSnapshot baseline = Session.PlayerSnapshot
                    ?? throw new InvalidOperationException("A player session has no baseline snapshot.");
                BusinessPayload payload = kind == CommandKind.ClaimControl
                    ? CommandPayloadFactory.ClaimControl(baseline.RoundId, expectedEpoch)
                    : CommandPayloadFactory.ReleaseControl(baseline.RoundId, expectedEpoch);
                var command = new ProtocolCommand(
                    kind,
                    payload,
                    new RequestId(requestId),
                    Session.Binding!.Scope.ScopeId,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(expectedEpoch));
                var completion = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
                {
                    if (string.Equals(args.Command.Key.RequestId.Value, requestId, StringComparison.Ordinal))
                        completion.TrySetResult(args);
                };
                Session.ReceiptResolved += handler;
                try
                {
                    CommandSubmission sent = await Session.SubmitAsync(command, ct);
                    Assert.True(sent.Sent);
                    for (int i = 0; i < 64 && !completion.Task.IsCompleted; i++)
                        await Session.PumpOnceAsync(ct);
                    return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                }
                finally
                {
                    Session.ReceiptResolved -= handler;
                }
            }

            public async ValueTask DisposeAsync()
            {
                try { await Session.CloseAsync("ROOM_TEST_DONE", CancellationToken.None); } catch { }
                try
                {
                    await _channel.CloseAsync(
                        new TableChannelCloseReason("ROOM_TEST_DONE", true),
                        CancellationToken.None);
                }
                catch { }
            }
        }

        private static async Task PumpUntilReadyAsync(TableSession session, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 16 && session.State != TableConnectionState.Ready; i++)
            {
                if (session.State == TableConnectionState.Faulted)
                    throw new InvalidOperationException("Session faulted before Ready: " + (session.LastError ?? "<no error>"));
                await session.PumpOnceAsync(cancellationToken);
            }
            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        private static async Task PumpUntilPublicVersionAsync(
            TableSession session,
            ulong version,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < 16 && (session.PublicSnapshot?.StateVersion ?? 0UL) < version; i++)
                await session.PumpOnceAsync(cancellationToken);
            Assert.Equal(version, Assert.IsType<PublicSnapshot>(session.PublicSnapshot).StateVersion);
        }

        private sealed class C04WireCaptureChannel : ITableChannel
        {
            private readonly ITableChannel _inner;
            private byte[]? _injectedFrame;

            public C04WireCaptureChannel(ITableChannel inner) => _inner = inner;

            public bool IsOpen => _inner.IsOpen;

            public byte[]? CapturedPlayerSnapshotFrame { get; private set; }

            public int CommandFrames { get; private set; }

            public int ReceiptQueryFrames { get; private set; }

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                _inner.OpenAsync(request, cancellationToken);

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
            {
                WireDecodeResult decoded = WireCodec.Decode(wireEnvelope.ToArray());
                if (decoded.IsOk && decoded.Envelope != null)
                {
                    if (decoded.Envelope.MessageType == MessageType.Command) CommandFrames++;
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
