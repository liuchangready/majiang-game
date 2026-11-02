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

                LocalTableConnectionTicket spectatorTicket = room.CreateSpectatorTicket();
                ITableChannel spectatorChannel = await room.ConnectAsync(spectatorTicket, timeout.Token);
                var spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.Value, "room-spectator"), timeout.Token);
                await PumpUntilReadyAsync(spectator, timeout.Token);
                Assert.Equal(ProtocolRole.Spectator, spectator.Binding!.Role);
                Assert.Equal(-1, spectator.Binding.SeatId);
                Assert.Null(spectator.PlayerSnapshot);
                Assert.NotNull(spectator.PublicSnapshot);
                Assert.NotEqual(string.Empty, spectator.PublicSnapshot!.RoundId);

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
                    SessionId = spectator.Binding.SessionId.Value,
                    RoomId = room.RoomId,
                    SeatId = -1,
                    BindingEpoch = spectator.Binding.BindingEpoch.Value,
                    CorrelationId = "spectator-command-negative",
                    Command = new Command
                    {
                        Type = CommandType.SubmitAction,
                        CanonicalPayload = ByteString.CopyFrom(spectatorPayload.CopyBytes()),
                        PayloadSchemaVersion = CanonicalPayloadDigest.FormatVersion
                    }
                };
                await spectatorChannel.SendAsync(WireCodec.Encode(attack), timeout.Token);
                WireDecodeResult rejected = WireCodec.Decode((await spectatorChannel.ReceiveAsync(timeout.Token)).ToArray());
                Assert.NotNull(rejected.Envelope);
                Assert.Equal(MessageType.Error, rejected.Envelope!.MessageType);
                Assert.Equal("SPECTATOR_READ_ONLY", rejected.Envelope.Error!.DetailCode);

                await spectator.CloseAsync("TEST_DONE", CancellationToken.None);
                await spectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
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
    }
}
