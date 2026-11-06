using System;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// V5 framing discipline against the real Kestrel WebSocket stack: one complete binary
    /// message is one Envelope; fragments accumulate to EndOfMessage; concatenated envelopes,
    /// oversize messages, text frames and silent connections are refused with the frozen
    /// close-code map.
    /// </summary>
    public sealed class HandshakeFramingTests
    {
        [Fact]
        public async Task FragmentedHello_IsAcceptedExactlyOnce()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string ticket = host.Signer.Sign(new TableTestTicket("acct-f", "room-a", TableAdmissionRole.Player, 0));
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            byte[] hello = WireCodec.Encode(TableWire.Hello(ticket));
            await client.SendFragmentedAsync(hello, 4);

            TableWireMessage message = await client.ReceiveAsync(TimeSpan.FromSeconds(10));
            Assert.False(message.IsClose, "server closed: " + message.WsCloseReason);
            Assert.Equal(Envelope.BodyOneofCase.Welcome, message.Envelope!.BodyCase);
            Assert.Equal(Contracts.Wire.Role.Player, message.Envelope.Welcome.Role);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task TwoEnvelopesInOneMessage_AreRefusedAsConcatenation()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string ticket = host.Signer.Sign(new TableTestTicket("acct-c", "room-a", TableAdmissionRole.Player, 0));
            byte[] hello = WireCodec.Encode(TableWire.Hello(ticket));
            byte[] concat = new byte[hello.Length * 2];
            Array.Copy(hello, 0, concat, 0, hello.Length);
            Array.Copy(hello, 0, concat, hello.Length, hello.Length);

            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await client.SendAsync(concat);
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
        }

        [Fact]
        public async Task TextFrame_IsRefusedAsProtocolViolation()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await client.SendTextAsync("{\"not\":\"protobuf\"}");
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
        }

        [Fact]
        public async Task EmptyMessage_IsRefused()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await client.SendAsync(Array.Empty<byte>());
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
        }

        [Fact]
        public async Task OversizeMessage_IsRefusedWith1009()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(options =>
            {
                options.MaxInboundMessageBytes = 16384;
                options.MaxEarlyBufferBytes = 4096; // pre-WELCOME cap refuses the 8 KiB burst
            });
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await client.SendAsync(new byte[8192]);
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1009, code);
            Assert.Equal(ProtocolErrorCode.MessageTooLarge, reason);
        }

        [Fact]
        public async Task LegacyV4Magic_IsRefusedWithoutMixingWires()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            byte[] legacy = { (byte)'M', (byte)'J', (byte)'T', (byte)'C', 0x00, 0x01 };
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await client.SendAsync(legacy);
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.LegacyV4Rejected, reason);
        }

        [Fact]
        public async Task MajorMismatch_IsRefusedPermanently()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string ticket = host.Signer.Sign(new TableTestTicket("acct-v", "room-a", TableAdmissionRole.Player, 0));
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await client.SendAsync(TableWire.RawHello(ticket, major: ProtocolConstants.Major + 1));
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolMajorUnsupported, reason);
        }

        [Fact]
        public async Task RulesetAndProjectionMismatch_AreRefused()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string ticket = host.Signer.Sign(new TableTestTicket("acct-v", "room-a", TableAdmissionRole.Player, 0));

            TableTestClient ruleset = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await ruleset.SendAsync(TableWire.RawHello(ticket, ProtocolConstants.Major, ruleset: ProtocolConstants.RulesetVersion + 1));
            (int code, string reason, _) = await ruleset.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.RulesetVersionUnsupported, reason);
            await ruleset.DisposeAsync();

            TableTestClient projection = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await projection.SendAsync(TableWire.RawHello(ticket, ProtocolConstants.Major, projection: ProtocolConstants.ProjectionVersion + 1));
            (int code2, string reason2, _) = await projection.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code2);
            Assert.Equal(ProtocolErrorCode.ProjectionVersionUnsupported, reason2);
        }

        [Fact]
        public async Task NonHelloFirstMessage_IsRefusedAsProtocolViolation()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            // A heartbeat is never a legal opening message.
            await client.SendAsync(TableWire.HeartbeatPing(TableWire.RandomNonce()));
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
        }

        [Fact]
        public async Task SilentHandshake_TimesOutWith4401()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options => options.HandshakeTimeoutMs = 800);
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4401, code);
            Assert.Equal(ProtocolErrorCode.HandshakeTimeout, reason);
        }

        [Fact]
        public async Task HandshakeRouteProbes_EnforceRoomAndUpgrade()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();

            // Invalid room charset is refused before the upgrade completes.
            (int badRoom, _) = await host.ProbeHandshakeAsync("?room=bad%20room!");
            Assert.Equal(400, badRoom);

            (int missingRoom, _) = await host.ProbeHandshakeAsync(string.Empty);
            Assert.Equal(400, missingRoom);

            // A plain GET is told to upgrade.
            int plainGet = await host.GetAsync(TableServiceWebHost.TablePath + "?room=room-a");
            Assert.Equal(426, plainGet);
        }

        [Fact]
        public async Task ClientInitiatedClose_AcknowledgedWith1000()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-a", "acct-close", 0);
            await client.CloseCleanAsync();
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1000, code);
            Assert.Equal(ProtocolErrorCode.LocalClosed, reason);
        }
    }
}
