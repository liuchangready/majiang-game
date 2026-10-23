using System;
using System.Collections.Generic;
using System.Text;
using Google.Protobuf;
using Mahjong.Contracts;

namespace Mahjong.Contracts.Wire.AotSmoke
{
    /// <summary>
    /// Minimal direct consumer of the generated Wire codec. It is intentionally
    /// free of reflection-based serialization, dynamic code and JSON so the same
    /// code path can run in .NET, Unity Editor (Mono) and an IL2CPP player.
    /// </summary>
    public static class WireAotProbe
    {
        public const string PassPrefix = "PASS";

        public static string Run()
        {
            var envelopes = BuildEnvelopes();
            int totalBytes = 0;

            foreach (Envelope original in envelopes)
            {
                byte[] bytes = WireCodec.Encode(original);
                totalBytes += bytes.Length;

                WireDecodeResult decoded = WireCodec.Decode(bytes);
                if (!decoded.IsOk || decoded.Envelope == null)
                {
                    throw new InvalidOperationException(
                        "Decode failed for " + original.MessageType + ": " + decoded.Status + " " + decoded.ErrorCode);
                }

                if (decoded.Envelope.MessageType != original.MessageType ||
                    decoded.Envelope.BodyCase != original.BodyCase)
                {
                    throw new InvalidOperationException(
                        "Round trip changed envelope identity for " + original.MessageType + ".");
                }

                if (decoded.UnknownFieldCount != 0)
                {
                    throw new InvalidOperationException(
                        "Round trip reported unknown fields for " + original.MessageType + ".");
                }

                byte[] reencoded = WireCodec.Encode(decoded.Envelope);
                if (!BytesEqual(bytes, reencoded))
                {
                    throw new InvalidOperationException(
                        "Re-encode changed wire bytes for " + original.MessageType + ".");
                }
            }

            // The codec scans descriptors at runtime; this exercises unknown
            // field preservation without relying on reflection-based serializers.
            byte[] heartbeatBytes = WireCodec.Encode(Envelopes.Heartbeat());
            byte[] withUnknownField = AppendUnknownVarint(heartbeatBytes, 1000, 1);
            WireDecodeResult unknown = WireCodec.Decode(withUnknownField);
            if (!unknown.IsOk || unknown.UnknownFieldCount != 1)
            {
                throw new InvalidOperationException(
                    "Unknown-field scan returned " + unknown.Status + " / " + unknown.UnknownFieldCount + ".");
            }

            // V4 MJ-TCP/1 traffic must never be interpreted as V5 protobuf.
            byte[] v4Magic = Encoding.ASCII.GetBytes("MJTC");
            WireDecodeResult legacy = WireCodec.Decode(v4Magic);
            if (legacy.Status != WireDecodeStatus.LegacyV4Rejected)
            {
                throw new InvalidOperationException("V4 magic was not rejected: " + legacy.Status + ".");
            }

            return PassPrefix +
                   " codec=netstandard2.1" +
                   " messages=" + envelopes.Count +
                   " bytes=" + totalBytes +
                   " unknownFields=" + unknown.UnknownFieldCount +
                   " legacyV4=rejected" +
                   " assembly=" + typeof(WireCodec).Assembly.GetName().Name;
        }

        private static List<Envelope> BuildEnvelopes()
        {
            return new List<Envelope>
            {
                Envelopes.Hello(),
                Envelopes.Welcome(),
                Envelopes.Command(),
                Envelopes.Receipt(),
                Envelopes.PlayerSnapshot(),
                Envelopes.PlayerSnapshotQuery(),
                Envelopes.PublicSnapshot(),
                Envelopes.PublicSnapshotQuery(),
                Envelopes.RoomUpdate(),
                Envelopes.SyncRequest(),
                Envelopes.SyncResponse(),
                Envelopes.ReceiptQuery(),
                Envelopes.ReceiptLookup(),
                Envelopes.Heartbeat(),
                Envelopes.Error(),
                Envelopes.Close(),
                Envelopes.PlayerUpdate(),
                Envelopes.PublicUpdate()
            };
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] AppendUnknownVarint(byte[] source, int fieldNumber, ulong value)
        {
            var result = new byte[source.Length + 8];
            Buffer.BlockCopy(source, 0, result, 0, source.Length);
            int offset = source.Length;
            WriteVarint(result, ref offset, ((ulong)fieldNumber << 3) | 0);
            WriteVarint(result, ref offset, value);
            var trimmed = new byte[offset];
            Buffer.BlockCopy(result, 0, trimmed, 0, offset);
            return trimmed;
        }

        private static void WriteVarint(byte[] buffer, ref int offset, ulong value)
        {
            while (value >= 0x80)
            {
                buffer[offset++] = (byte)(value | 0x80);
                value >>= 7;
            }

            buffer[offset++] = (byte)value;
        }

        private static class Envelopes
        {
            public static Envelope Hello()
            {
                return Envelope(MessageType.Hello, e => e.Hello = new Hello
                {
                    Ticket = "ticket-1",
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    ClientBuild = "aot-probe",
                    ResumeRequested = true,
                    ResumeSessionId = "session-1"
                });
            }

            public static Envelope Welcome()
            {
                return Envelope(MessageType.Welcome, e => e.Welcome = new Welcome
                {
                    SessionId = "session-1",
                    RoomId = "room-1",
                    TableId = "table-1",
                    Role = Role.Player,
                    SeatId = 0,
                    ConnectionEpoch = 1,
                    OwnerEpoch = 2,
                    BindingEpoch = 3,
                    Baseline = new StreamBaseline
                    {
                        StateVersion = 7,
                        EventSeq = 101,
                        Cursor = Cursor(7, 11)
                    },
                    Scope = Scope(),
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
                });
            }

            public static Envelope Command()
            {
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    "round-1",
                    "decision-1",
                    1,
                    ProtocolActionType.DiscardTile,
                    tileIds: new uint[] { 5 });

                return Envelope(MessageType.Command, e => e.Command = new Command
                {
                    Type = CommandType.SubmitAction,
                    CanonicalPayload = ByteString.CopyFrom(payload.CopyBytes()),
                    PayloadSchemaVersion = CanonicalPayloadDigest.FormatVersion,
                    ExpectedStateVersion = 4,
                    ExpectedRoundId = "round-1",
                    ExpectedDecisionId = "decision-1"
                });
            }

            public static Envelope Receipt()
            {
                return Envelope(MessageType.Receipt, e => e.Receipt = new Receipt
                {
                    Status = ReceiptStatus.Applied,
                    ReasonCode = string.Empty,
                    EvaluatedStateVersion = 1,
                    CommandVersion = 2,
                    EventSeq = 3,
                    CommittedAtMs = 1234,
                    CanonicalPayloadSha256 = new string('a', 64)
                });
            }

            public static Envelope PlayerSnapshot()
            {
                PlayerSnapshot snapshot = new PlayerSnapshot
                {
                    SessionId = "session-1",
                    RoundId = "round-1",
                    Cursor = Cursor(7, 11),
                    StateVersion = 7,
                    EventSeq = 101,
                    SessionVersion = 1,
                    RoundVersion = 2,
                    PublicView = PublicView(),
                    PrivateView = new PlayerPrivateView
                    {
                        Seat = 0,
                        Furiten = false
                    }
                };
                snapshot.PrivateView.ClosedTiles.Add(1);
                snapshot.PrivateView.ClosedTiles.Add(2);

                return Envelope(MessageType.PlayerSnapshot, e => e.PlayerSnapshot = snapshot);
            }

            public static Envelope PlayerSnapshotQuery()
            {
                return Envelope(MessageType.PlayerSnapshotQuery, e => e.PlayerSnapshotQuery = new PlayerSnapshotQuery
                {
                    SessionId = "session-1"
                });
            }

            public static Envelope PublicSnapshot()
            {
                return Envelope(MessageType.PublicSnapshot, e => e.PublicSnapshot = new PublicSnapshot
                {
                    SessionId = "session-1",
                    RoundId = "round-1",
                    Cursor = Cursor(7, 11),
                    StateVersion = 7,
                    EventSeq = 101,
                    SessionVersion = 1,
                    RoundVersion = 2,
                    View = PublicView()
                });
            }

            public static Envelope PublicSnapshotQuery()
            {
                return Envelope(MessageType.PublicSnapshotQuery, e => e.PublicSnapshotQuery = new PublicSnapshotQuery
                {
                    SessionId = "session-1"
                });
            }

            public static Envelope RoomUpdate()
            {
                return Envelope(MessageType.RoomUpdate, e => e.RoomUpdate = new RoomUpdate
                {
                    RoomId = "room-1",
                    SessionId = "session-1",
                    RoomVersion = 3,
                    Lifecycle = RoomLifecycle.Lobby,
                    Capacity = 4
                });
            }

            public static Envelope SyncRequest()
            {
                return Envelope(MessageType.SyncRequest, e => e.SyncRequest = new SyncRequest
                {
                    Target = SyncTarget.PlayerStream,
                    KnownCursor = Cursor(5, 10),
                    ReasonCode = ProtocolErrorCode.StreamGap,
                    RequestFullBaseline = true
                });
            }

            public static Envelope SyncResponse()
            {
                return Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
                {
                    Target = SyncTarget.PublicStream,
                    IsFullSnapshot = true,
                    Public = new PublicSnapshot
                    {
                        SessionId = "session-1",
                        RoundId = "round-1",
                        Cursor = Cursor(7, 11),
                        StateVersion = 7,
                        EventSeq = 101,
                        SessionVersion = 1,
                        RoundVersion = 2,
                        View = PublicView()
                    },
                    StateVersion = 7,
                    EventSeq = 101
                });
            }

            public static Envelope ReceiptQuery()
            {
                return Envelope(MessageType.ReceiptQuery, e => e.ReceiptQuery = new ReceiptQuery
                {
                    RequestScopeId = "scope-1",
                    RequestId = "request-1"
                });
            }

            public static Envelope ReceiptLookup()
            {
                return Envelope(MessageType.ReceiptLookup, e => e.ReceiptLookup = new ReceiptLookup
                {
                    Status = LookupStatus.NotFound,
                    CanonicalPayloadSha256 = new string('b', 64)
                });
            }

            public static Envelope Heartbeat()
            {
                return Envelope(MessageType.Heartbeat, e => e.Heartbeat = new Heartbeat
                {
                    Ping = new Ping { Nonce = "0123456789abcdef" }
                });
            }

            public static Envelope Error()
            {
                return Envelope(MessageType.Error, e => e.Error = new ProtocolError
                {
                    Code = ProtocolErrorCode.ServerBusy,
                    Retryable = true,
                    FieldPath = "command",
                    DetailCode = "QUEUE_FULL",
                    RetryAfterMs = 250
                });
            }

            public static Envelope Close()
            {
                return Envelope(MessageType.Close, e => e.Close = new Close
                {
                    Code = ProtocolErrorCode.SlowConsumer,
                    Permanent = true,
                    WebsocketCloseCode = 1008
                });
            }

            public static Envelope PlayerUpdate()
            {
                return Envelope(MessageType.PlayerUpdate, e => e.PlayerUpdate = new PlayerUpdate
                {
                    Snapshot = PlayerSnapshot().PlayerSnapshot,
                    FromCursor = Cursor(5, 10),
                    Cursor = Cursor(7, 11),
                    IsFullSnapshot = false
                });
            }

            public static Envelope PublicUpdate()
            {
                return Envelope(MessageType.PublicUpdate, e => e.PublicUpdate = new PublicUpdate
                {
                    Snapshot = new PublicSnapshot
                    {
                        SessionId = "session-1",
                        RoundId = "round-1",
                        Cursor = Cursor(7, 11),
                        StateVersion = 7,
                        EventSeq = 101,
                        SessionVersion = 1,
                        RoundVersion = 2,
                        View = PublicView()
                    },
                    FromCursor = Cursor(5, 10),
                    Cursor = Cursor(7, 11),
                    IsFullSnapshot = false
                });
            }

            private static Envelope Envelope(MessageType type, Action<Envelope> assignBody)
            {
                var envelope = new Envelope
                {
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    MessageType = type,
                    CorrelationId = "corr-1",
                    RequestId = "request-1"
                };
                assignBody(envelope);
                return envelope;
            }

            private static StreamCursor Cursor(ulong streamVersion, ulong projectionSeq)
            {
                return new StreamCursor { StreamVersion = streamVersion, ProjectionSeq = projectionSeq };
            }

            private static RequestScope Scope()
            {
                return new RequestScope
                {
                    ScopeId = "scope-1",
                    AccountId = "account-1",
                    Role = Role.Player,
                    RoomId = "room-1",
                    IssuedAtMs = 1000,
                    ExpiresAtMs = 2000
                };
            }

            private static PublicMatchView PublicView()
            {
                return new PublicMatchView
                {
                    Session = new PublicSessionSummary { Finished = false },
                    Paused = false,
                    SettlementRoundId = "round-1"
                };
            }
        }
    }
}
