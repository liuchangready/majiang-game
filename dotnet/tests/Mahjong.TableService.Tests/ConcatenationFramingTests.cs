using System;
using System.Collections.Generic;
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

        /// <summary>D-1-04 round 3: unknown field-100 GROUP nesting at the review's exact
        /// boundary depths (the frozen codec's own accepted range). The codec accepts every
        /// depth below, so the scan must walk ALL of them: a depth the scan refuses to follow
        /// is exactly the old fail-open (everything behind it — including a concatenated
        /// second Envelope — went unswept).</summary>
        public static TheoryData<int> BoundaryDepths => new TheoryData<int> { 63, 64, 65 };

        /// <summary>N nested unknown groups: field-100 group starts (A3 06) then group ends
        /// (A4 06) — schema-legal protobuf the frozen codec accepts at every depth here.</summary>
        private static byte[] NestedGroups(int depth)
        {
            byte[] bytes = new byte[depth * 4];
            for (int i = 0; i < depth; i++)
            {
                bytes[i * 2] = 0xa3;
                bytes[i * 2 + 1] = 0x06;
                bytes[depth * 2 + i * 2] = 0xa4;
                bytes[depth * 2 + i * 2 + 1] = 0x06;
            }

            return bytes;
        }

        [Theory]
        [MemberData(nameof(BoundaryDepths))]
        public async Task DeepUnknownGroups_SingleEnvelope_IsStillAnswered(int depth)
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-deep", "acct-deep", 0);
            try
            {
                await TableTestClientTestsCommon.DrainBaselineAsync(client);
                string nonce = TableWire.RandomNonce();
                byte[] frame = NestedGroups(depth).Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce))).ToArray();
                await client.SendAsync(frame);
                Assert.True(
                    await TableTestClientTestsCommon.WaitPongAsync(client, nonce),
                    "a legally nested unknown-group padding must stay accepted at depth " + depth);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Theory]
        [MemberData(nameof(BoundaryDepths))]
        public async Task DeepUnknownGroups_Concatenation_IsStillRefused(int depth)
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-deep-c");
            string nonce1 = TableWire.RandomNonce();
            string nonce2 = TableWire.RandomNonce();
            byte[] concatenated = NestedGroups(depth)
                .Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce1)))
                .Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce2)))
                .ToArray();
            await client.SendAsync(concatenated);
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task HelloPhase_DeepGroupConcatenation_IsRefusedToo()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-deep-h");
            string nonce1 = TableWire.RandomNonce();
            string nonce2 = TableWire.RandomNonce();
            byte[] concatenated = NestedGroups(65)
                .Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce1)))
                .Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce2)))
                .ToArray();
            await client.SendAsync(concatenated);
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
            await client.DisposeAsync();
        }

        /// <summary>Above the frozen codec's own accepted nesting range the codec itself
        /// refuses the message before the scan ever sees it — the upstream fail-closed the
        /// scan's unbounded walk is allowed to lean on. Pinned once so a widening of the
        /// codec range can never silently reopen the D-1-04 question.</summary>
        [Fact]
        public async Task BeyondCodecNestingRange_IsCodecFailClosed()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-deep-x");
            string nonce = TableWire.RandomNonce();
            byte[] frame = NestedGroups(200).Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce))).ToArray();
            await client.SendAsync(frame);
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.MalformedMessage, reason);
            await client.DisposeAsync();
        }

        /// <summary>The scanner's framing-error branches may only rely on the frozen codec's
        /// own rejection: an unbalanced end group and an illegal wire type must NEVER come
        /// back answered — pinning the codec-side fail-closed the scan's false-returns lean on.</summary>
        [Theory]
        [InlineData(new byte[] { 0xa4, 0x06 })] // stray end group at top level
        [InlineData(new byte[] { 0xa3, 0x06, 0xa4, 0x06, 0xa4, 0x06 })] // one end group too many
        [InlineData(new byte[] { 0xae, 0x06, 0x01 })] // wire type 6 is never legal
        public async Task FramingGarbage_IsNeverAnswered(byte[] garbage)
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-garbage", "acct-garbage", 0);
            try
            {
                await TableTestClientTestsCommon.DrainBaselineAsync(client);
                string nonce = TableWire.RandomNonce();
                byte[] frame = garbage.Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce))).ToArray();
                await client.SendAsync(frame);
                (int code, _, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(4400, code);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        // ==================================================================
        // D-1-04 round 5: CODEC-ALLOWED OVERLONG TAGS (the review probe).
        // The frozen codec's ReadTag reads an at-most-10-byte varint and
        // TRUNCATES it to uint32 before the field/wire-type split — so
        // (1UL<<34)|(100UL<<3) arrives at the schema as the legal unknown
        // field 100, and the same truncation makes wide-tagged KNOWN fields
        // count for the dedup scan. A tag the scan misreads as negative must
        // never again short-circuit the sweep.
        // ==================================================================

        /// <summary>Overlong varint encoding of value (canonical minimal bytes).</summary>
        private static byte[] OverlongVarint(ulong value)
        {
            var bytes = new List<byte>();
            do
            {
                byte group = (byte)(value & 0x7F);
                value >>= 7;
                if (value > 0)
                {
                    group |= 0x80;
                }

                bytes.Add(group);
            }
            while (value > 0);
            return bytes.ToArray();
        }

        /// <summary>A tag the codec truncates to (fieldNumber, wireType): the 34th bit is
        /// discarded by the uint32 cast, exactly like the frozen codec's ReadTag.</summary>
        private static byte[] WideTag(uint fieldNumber, int wireType)
        {
            return OverlongVarint((1UL << 34) | ((ulong)fieldNumber << 3) | (ulong)(uint)wireType);
        }

        private static readonly byte[][] WidePaddings =
        {
            WideTag(100, 0).Concat(new byte[] { 0x01 }).ToArray(),              // unknown varint field 100
            WideTag(100, 1).Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray(), // unknown fixed64
            WideTag(100, 5).Concat(new byte[] { 1, 2, 3, 4 }).ToArray(),        // unknown fixed32
            WideTag(100, 2).Concat(new byte[] { 0x02, 0x01, 0x02 }).ToArray(),  // unknown bytes
            WideTag(100, 3).Concat(WideTag(100, 4)).ToArray()                   // unknown empty group
        };

        public static TheoryData<int, int> WideLegality => new TheoryData<int, int>
        {
            { 0, 0 }, { 0, 1 }, { 0, 2 },
            { 1, 0 }, { 1, 1 }, { 1, 2 },
            { 2, 0 }, { 2, 1 }, { 2, 2 },
            { 3, 0 }, { 3, 1 }, { 3, 2 },
            { 4, 0 }, { 4, 1 }, { 4, 2 }
        };

        /// <summary>position 0 = in front of the envelope, 1 = between the envelope's known
        /// scalar fields and its heartbeat body (a true between-known-fields splice at the
        /// field-53 tag boundary), 2 = behind the envelope.</summary>
        private static byte[] WidePaddedHeartbeat(int paddingIndex, int position, string nonce)
        {
            byte[] ping = WireCodec.Encode(TableWire.HeartbeatPing(nonce));
            byte[] padding = WidePaddings[paddingIndex];
            return position switch
            {
                0 => padding.Concat(ping).ToArray(),
                1 => ping.Take(Array.IndexOf(ping, (byte)0xaa))
                    .Concat(padding)
                    .Concat(ping.Skip(Array.IndexOf(ping, (byte)0xaa))).ToArray(),
                _ => ping.Concat(padding).ToArray()
            };
        }

        /// <summary>Every codec-allowed overlong tag shape around a SINGLE legal heartbeat
        /// stays accepted and answered — at the front, splitting the envelope, and behind it.</summary>
        [Theory]
        [MemberData(nameof(WideLegality))]
        public async Task WideTagUnknownField_SingleEnvelope_IsStillAnswered(int paddingIndex, int position)
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-wide", "acct-wide", 0);
            try
            {
                await TableTestClientTestsCommon.DrainBaselineAsync(client);
                string nonce = TableWire.RandomNonce();
                await client.SendAsync(WidePaddedHeartbeat(paddingIndex, position, nonce));
                Assert.True(
                    await TableTestClientTestsCommon.WaitPongAsync(client, nonce),
                    "a wide-tagged unknown field must decode (and scan) exactly like the codec's own " +
                    "truncated view — the heartbeat stays answered (padding " + paddingIndex +
                    ", position " + position + ")");
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        /// <summary>The review's original attack: a wide-tagged unknown field must not stop
        /// the sweep — the concatenated SECOND envelope behind it is still refused.</summary>
        [Theory]
        [MemberData(nameof(WideLegality))]
        public async Task WideTagUnknownField_Concatenation_IsStillRefused(int paddingIndex, int position)
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-wide-c");
            string nonce1 = TableWire.RandomNonce();
            string nonce2 = TableWire.RandomNonce();
            byte[] padding = WidePaddings[paddingIndex];
            byte[] payload = position switch
            {
                0 => padding.Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce1)))
                    .Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce2))).ToArray(),
                2 => WireCodec.Encode(TableWire.HeartbeatPing(nonce1)).Concat(padding)
                    .Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce2))).ToArray(),
                _ => WireCodec.Encode(TableWire.HeartbeatPing(nonce1))
                    .Concat(WireCodec.Encode(TableWire.HeartbeatPing(nonce2))).Concat(padding).ToArray()
            };
            await client.SendAsync(payload);
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
            await client.DisposeAsync();
        }

        /// <summary>A KNOWN single-value envelope field (53 = heartbeat) delivered with a
        /// codec-allowed overlong tag is the SAME field number to the scan: twice in one
        /// message is a duplicate, refused — even though the codec itself would merge
        /// last-wins.</summary>
        [Fact]
        public async Task WideTagKnownField_Duplicate_IsRefusedEvenThoughCodecMerges()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-wide-dup", "acct-wdup", 0);
            try
            {
                await TableTestClientTestsCommon.DrainBaselineAsync(client);
                byte[] ping = WireCodec.Encode(TableWire.HeartbeatPing(TableWire.RandomNonce()));
                // Slice out EXACTLY the field-53 heartbeat body (tag 0xAA 0x03 + length
                // varint + declared bytes) so the duplicated entry is a well-formed
                // protobuf field — only the scanner may refuse it, not the codec.
                int tagAt = Array.IndexOf(ping, (byte)0xaa);
                int cursor = tagAt + 2;
                int bodyLen = 0;
                int shift = 0;
                while (true)
                {
                    byte lengthByte = ping[cursor++];
                    bodyLen |= (lengthByte & 0x7F) << shift;
                    if ((lengthByte & 0x80) == 0)
                    {
                        break;
                    }

                    shift += 7;
                }

                byte[] heartbeatBody = ping.Skip(cursor).Take(bodyLen).ToArray();
                byte[] wide53 = WideTag(53, 2);
                byte[] duplicated = ping
                    .Concat(wide53)
                    .Concat(OverlongVarint((ulong)heartbeatBody.Length))
                    .Concat(heartbeatBody)
                    .ToArray();
                await client.SendAsync(duplicated);
                (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(4400, code);
                Assert.Equal(ProtocolErrorCode.ProtocolViolation, reason);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        /// <summary>HELLO phase: a wide-tagged unknown field before the legal HELLO must
        /// still complete admission (the scan answers the same bytes the codec sees).</summary>
        [Fact]
        public async Task WideTagPadding_HelloPhase_IsAdmitted()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-wide-h");
            try
            {
                byte[] padding = WideTag(100, 0).Concat(new byte[] { 0x01 }).ToArray();
                string ticket = host.Signer.Sign(new TableTestTicket(
                    "acct-wide-h", "room-wide-h", TableAdmissionRole.Player, 1));
                byte[] hello = padding.Concat(
                    WireCodec.Encode(TableWire.Hello(ticket))).ToArray();
                await client.SendAsync(hello);
                Envelope welcome = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Welcome,
                    TimeSpan.FromSeconds(15));
                Assert.False(string.IsNullOrEmpty(welcome.Welcome.SessionId));
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        /// <summary>Post-admission SYNC and QUERY envelopes with wide-tagged unknown padding
        /// are still decoded and executed (sync answers frames, the receipt query answers a
        /// lookup) — the scan never downgrades codec-legal traffic into a close.</summary>
        [Fact]
        public async Task WideTagPadding_SyncAndQuery_AreStillExecuted()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-wide-sq", "acct-wsq", 0);
            try
            {
                await TableTestClientTestsCommon.DrainBaselineAsync(client);
                byte[] padding = WideTag(100, 0).Concat(new byte[] { 0x01 }).ToArray();

                await client.SendAsync(padding.Concat(
                    WireCodec.Encode(TableWire.SyncRequest(
                        client.Welcome!, playerStream: true, streamVersion: 0, projectionSeq: 0))).ToArray());
                Envelope sync = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.SyncResponse ||
                                envelope.MessageType == MessageType.PlayerUpdate,
                    TimeSpan.FromSeconds(15));
                Assert.NotEqual(MessageType.Error, sync.MessageType);

                await client.SendAsync(padding.Concat(
                    WireCodec.Encode(TableWire.ReceiptQuery(
                        client.Welcome!, client.Welcome!.Scope.ScopeId, TableWire.RandomId("wide")))).ToArray());
                Envelope lookup = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.ReceiptLookup ||
                                envelope.MessageType == MessageType.Error,
                    TimeSpan.FromSeconds(15));
                Assert.Equal(MessageType.ReceiptLookup, lookup.MessageType);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        /// <summary>A wide-tagged padded single message split across WebSocket fragments is
        /// still one Envelope — accepted and answered.</summary>
        [Fact]
        public async Task WideTagPadding_FragmentedSingle_IsStillAnswered()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-wide-f", "acct-wf", 0);
            try
            {
                await TableTestClientTestsCommon.DrainBaselineAsync(client);
                string nonce = TableWire.RandomNonce();
                byte[] padded = WidePaddings[3].Concat(
                    WireCodec.Encode(TableWire.HeartbeatPing(nonce))).ToArray();
                await client.SendFragmentedAsync(padded, 4);
                Assert.True(
                    await TableTestClientTestsCommon.WaitPongAsync(client, nonce),
                    "a fragmented wide-tagged padded heartbeat must still be answered");
            }
            finally
            {
                await client.DisposeAsync();
            }
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
