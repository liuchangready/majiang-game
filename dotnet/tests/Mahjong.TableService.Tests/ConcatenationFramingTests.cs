using System;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// D-1-04: one WebSocket message = one Envelope at EVERY phase. The frozen codec accepts
    /// schema-legal unknown fields (any wire type incl. groups), so the dedup scan must NEVER
    /// be short-circuited by their presence: schema-legal unknowns may appear before/between/
    /// after known fields; two envelopes in one message are refused in every arrangement and
    /// across fragmentation, while a lone padded legal message is accepted and answered.
    /// HELLO-phase and post-admission command phases share the same scan.
    /// </summary>
    public sealed class ConcatenationFramingTests
    {
        /// <summary>Schema-legal unknown-field paddings (tag field 100, not overlapping).</summary>
        public static TheoryData<int, int> Legality => new TheoryData<int, int>
        {
            { 0, 0 }, { 0, 1 }, { 0, 2 },
            { 1, 0 }, { 1, 1 }, { 1, 2 },
            { 2, 0 }, { 2, 1 }, { 2, 2 },
            { 3, 0 }, { 3, 1 }, { 3, 2 },
            { 4, 0 }, { 4, 1 }, { 4, 2 },
            { 5, 0 }, { 5, 1 }, { 5, 2 }
        };

        private static readonly byte[][] Paddings =
        {
            new byte[] { 0xa0, 0x06, 0x01 },                                          // unknown varint field 100 = 1
            new byte[] { 0xa1, 0x06, 1, 2, 3, 4, 5, 6, 7, 8 },                        // unknown fixed64 field 100
            new byte[] { 0xa5, 0x06, 1, 2, 3, 4 },                                    // unknown fixed32 field 100
            new byte[] { 0xa2, 0x06, 0x04, 0x01, 0x02, 0x03, 0x04 },                  // unknown bytes field 100
            new byte[] { 0xa3, 0x06, 0xa4, 0x06 },                                    // unknown group field 100 (empty)
            new byte[] { 0xa3, 0x06, 0xa8, 0x06, 0x01, 0xa4, 0x06 },                  // unknown group with a nested varint
        };

        private static byte[] Build(string nonce1, string nonce2, int paddingIndex, int position)
        {
            byte[] ping1 = WireCodec.Encode(TableWire.HeartbeatPing(nonce1));
            byte[] ping2 = WireCodec.Encode(TableWire.HeartbeatPing(nonce2));
            byte[] padding = Paddings[paddingIndex];
            return position switch
            {
                0 => padding.Concat(ping1).Concat(ping2).ToArray(),
                1 => ping1.Concat(padding).Concat(ping2).ToArray(),
                _ => ping1.Concat(ping2).Concat(padding).ToArray()
            };
        }

        [Theory]
        [MemberData(nameof(Legality))]
        public async Task UnknownPadding_AroundConcatenation_IsStillRefused(int paddingIndex, int position)
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-p");
            string nonce1 = TableWire.RandomNonce();
            string nonce2 = TableWire.RandomNonce();
            byte[] payload = Build(nonce1, nonce2, paddingIndex, position);
            await client.SendAsync(payload);
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task LegalPaddedHeartbeat_IsAcceptedAndAnswered()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-pad", "acct-pad", 0);
            try
            {
                await TableTestClientTestsCommon.DrainBaselineAsync(client);
                foreach (byte[] padding in Paddings)
                {
                    string nonce = TableWire.RandomNonce();
                    byte[] frame = padding.Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce))).ToArray();
                    await client.SendAsync(frame);
                    bool answered = await TableTestClientTestsCommon.WaitPongAsync(client, nonce);
                    Assert.True(answered, "a legal padded heartbeat must be answered with its own nonce");
                }

                // Fragmented single padded message is still legal (one Envelope overall).
                string fragmentNonce = TableWire.RandomNonce();
                byte[] paddedLegal = Paddings[4]
                    .Concat(WireCodec.Encode(TableWire.HeartbeatPing(fragmentNonce)))
                    .ToArray();
                await client.SendFragmentedAsync(paddedLegal, 4);
                Assert.True(
                    await TableTestClientTestsCommon.WaitPongAsync(client, fragmentNonce),
                    "a fragmented legal padded heartbeat must still be answered");
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task FragmentedPaddedConcatenation_IsRefusedToo()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-p2");
            string nonce1 = TableWire.RandomNonce();
            string nonce2 = TableWire.RandomNonce();
            byte[] concatenated = Build(nonce1, nonce2, paddingIndex: 4, position: 0);
            await client.SendFragmentedAsync(concatenated, 3);
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
            await client.DisposeAsync();
        }

        internal static class TableTestClientTestsCommon
        {
            public static async Task<bool> WaitPongAsync(TableTestClient client, string nonce)
            {
                long deadline = Environment.TickCount64 + 10_000;
                while (Environment.TickCount64 < deadline)
                {
                    TableWireMessage message = await client.ReceiveAsync(
                        TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64)));
                    Envelope? envelope = message.Envelope;
                    if (envelope == null)
                    {
                        throw new InvalidOperationException("unexpected close: " + message.WsCloseCode);
                    }

                    if (envelope.MessageType == MessageType.Heartbeat &&
                        envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Ping)
                    {
                        continue;
                    }

                    if (envelope.MessageType == MessageType.Heartbeat &&
                        envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong &&
                        string.Equals(envelope.Heartbeat.Pong.Nonce, nonce, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }

            public static async Task<bool> DrainBaselineAsync(TableTestClient client)
            {
                long deadline = Environment.TickCount64 + 15_000;
                while (Environment.TickCount64 < deadline)
                {
                    TableWireMessage message = await client.ReceiveAsync(
                        TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64)));
                    Envelope? envelope = message.Envelope;
                    if (envelope == null)
                    {
                        return false;
                    }

                    if (envelope.MessageType == MessageType.PlayerUpdate ||
                        envelope.MessageType == MessageType.PublicUpdate)
                    {
                        return true;
                    }
                }

                return false;
            }

            public static async Task<int> ExpectFirstClose(TableTestClient client, int seconds)
            {
                var (code, _, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(seconds));
                return code;
            }
        }
    }
}
