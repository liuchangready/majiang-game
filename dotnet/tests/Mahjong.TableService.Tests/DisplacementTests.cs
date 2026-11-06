using System;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// 顶号 (same-account re-bind) semantics: the newest valid consumption of the same
    /// (account, room, seat) displaces the older live session with
    /// DISPLACED_BY_NEW_CONNECTION (4403), the new session takes over immediately, and
    /// cross-account seat refusals never touch the incumbent.
    /// </summary>
    public sealed class DisplacementTests
    {
        [Fact]
        public async Task SameAccountRebind_DisplacesOldSessionWith4403()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient first = await host.ConnectPlayerAsync("room-top", "acct-top", 0);
            try
            {
                await first.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                envelope.PlayerUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));

                // Same account, same room, same seat, fresh ticket (fresh jti): the newest
                // consumption wins.
                string secondTicket = host.Signer.Sign(new TableTestTicket(
                    "acct-top", "room-top", TableAdmissionRole.Player, 0));
                TableTestClient second = await TableTestClient
                    .ConnectAsync(host.WsUrl + "?room=room-top")
                    ;
                await second.AdmitAsync(secondTicket);
                Assert.Equal(Contracts.Wire.Role.Player, second.Welcome!.Role);

                // The superseded session is closed with the frozen displacement code.
                (int code, string reason, _) =
                    await first.ExpectCloseAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(4403, code);
                Assert.Equal(ProtocolErrorCode.DisplacedByNewConnection, reason);
                await first.DisposeAsync();

                // The takeover is live at once: the new session drives the actor.
                await second.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                envelope.PlayerUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));
                byte[] payload = TableWire.SubmitActionPayload(
                    "room-top-r1", "decision-post-takeover", 0, (uint)ProtocolActionType.Pass);
                await second.SendAsync(TableWire.Command(
                    second.Welcome!, TableWire.RandomId("req"),
                    Contracts.CommandKind.SubmitAction, payload));
                Envelope receipt = await second.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt,
                    TimeSpan.FromSeconds(20));
                Assert.True(
                    receipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Applied ||
                    receipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Rejected);
                await second.DisposeAsync();
            }
            finally
            {
                await first.DisposeAsync();
            }
        }

        [Fact]
        public async Task OldTicketCannotRejoinWhileDisplacingSessionLives()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string firstTicket = host.Signer.Sign(new TableTestTicket(
                "acct-re", "room-re", TableAdmissionRole.Player, 0, Jti: "jti-displaced-1"));
            TableTestClient first = await TableTestClient
                .ConnectAsync(host.WsUrl + "?room=room-re")
                ;
            await first.AdmitAsync(firstTicket);

            string secondTicket = host.Signer.Sign(new TableTestTicket(
                "acct-re", "room-re", TableAdmissionRole.Player, 0, Jti: "jti-displaced-2"));
            TableTestClient second = await TableTestClient
                .ConnectAsync(host.WsUrl + "?room=room-re")
                ;
            await second.AdmitAsync(secondTicket);
            await first.ExpectCloseAsync(TimeSpan.FromSeconds(20));
            await first.DisposeAsync();

            // The displaced ticket was consumed once; a replay is refused outright.
            TableTestClient replay = await TableTestClient
                .ConnectAsync(host.WsUrl + "?room=room-re")
                ;
            await replay.SendAsync(TableWire.Hello(firstTicket));
            (int code, string reason, _) = await replay.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4401, code);
            Assert.Equal("INVALID_TOKEN", reason);
            await replay.DisposeAsync();
            await second.DisposeAsync();
        }

        [Fact]
        public async Task DisplacementIsLimitedToTheSameAccount()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient owner = await host.ConnectPlayerAsync("room-own", "acct-own", 2);
            try
            {
                await owner.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                envelope.PlayerUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));

                // A different account can never displace the incumbent seat.
                TableTestClient other = await TableTestClient
                    .ConnectAsync(host.WsUrl + "?room=room-own")
                    ;
                await other.SendAsync(TableWire.Hello(host.Signer.Sign(new TableTestTicket(
                    "acct-other", "room-own", TableAdmissionRole.Player, 2))));
                (int code, string reason, _) = await other.ExpectCloseAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(4401, code);
                Assert.Equal("INVALID_TOKEN", reason);
                await other.DisposeAsync();

                // The incumbent is untouched and still driving the actor.
                byte[] payload = TableWire.SubmitActionPayload(
                    "room-own-r1", "decision-incumbent", 0, (uint)ProtocolActionType.Pass);
                await owner.SendAsync(TableWire.Command(
                    owner.Welcome!, TableWire.RandomId("req"),
                    Contracts.CommandKind.SubmitAction, payload));
                Envelope receipt = await owner.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt,
                    TimeSpan.FromSeconds(20));
                Assert.NotEqual(MessageType.Error, receipt.MessageType);
                await owner.DisposeAsync();
            }
            finally
            {
                await owner.DisposeAsync();
            }
        }
    }
}
