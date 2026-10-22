using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Mahjong.Contracts.Wire;
using Xunit;
using BusinessPayload = Mahjong.Contracts.BusinessPayload;
using CanonicalPayloadDigest = Mahjong.Contracts.CanonicalPayloadDigest;
using CommandKind = Mahjong.Contracts.CommandKind;
using CommandPayloadFactory = Mahjong.Contracts.CommandPayloadFactory;
using PayloadDigest = Mahjong.Contracts.PayloadDigest;
using ProtocolActionType = Mahjong.Contracts.ProtocolActionType;
using ProtocolCommand = Mahjong.Contracts.ProtocolCommand;
using ProtocolConstants = Mahjong.Contracts.ProtocolConstants;
using ProtocolErrorCode = Mahjong.Contracts.ProtocolErrorCode;
using ProtocolEventType = Mahjong.Contracts.ProtocolEventType;
using PublicProjectionException = Mahjong.Contracts.PublicProjectionException;
using RequestId = Mahjong.Contracts.RequestId;
using RequestScopeId = Mahjong.Contracts.RequestScopeId;
using StateVersion = Mahjong.Contracts.StateVersion;

namespace Mahjong.Protocol.Tests
{
    public sealed class WireCodecTests
    {
        [Fact]
        public void AllMessageFamiliesRoundTrip()
        {
            var envelopes = new List<Envelope>
            {
                TestWire.Envelope(MessageType.Hello, e => e.Hello = new Hello
                {
                    Ticket = "ticket-1",
                    ProtocolMajor = 1,
                    ProtocolMinor = 0,
                    RulesetVersion = 1,
                    ProjectionVersion = 1,
                    ClientBuild = "test",
                    ResumeRequested = true,
                    ResumeSessionId = "session-1"
                }),
                TestWire.Envelope(MessageType.Welcome, e => e.Welcome = TestWire.Welcome()),
                TestWire.Envelope(MessageType.Command, e => e.Command = TestWire.Command()),
                TestWire.Envelope(MessageType.Receipt, e => e.Receipt = new Receipt
                {
                    Status = ReceiptStatus.Applied,
                    ReasonCode = string.Empty,
                    EvaluatedStateVersion = 1,
                    CommandVersion = 2,
                    EventSeq = 3,
                    CommittedAtMs = 1234,
                    CanonicalPayloadSha256 = new string('a', 64)
                }),
                TestWire.Envelope(MessageType.PlayerSnapshot, e => e.PlayerSnapshot = TestWire.PlayerSnapshot()),
                TestWire.Envelope(MessageType.PlayerSnapshotQuery, e => e.PlayerSnapshotQuery = new PlayerSnapshotQuery { SessionId = "session-1" }),
                TestWire.Envelope(MessageType.PublicSnapshot, e => e.PublicSnapshot = TestWire.PublicSnapshot()),
                TestWire.Envelope(MessageType.PublicSnapshotQuery, e => e.PublicSnapshotQuery = new PublicSnapshotQuery { SessionId = "session-1" }),
                TestWire.Envelope(MessageType.RoomUpdate, e => e.RoomUpdate = new RoomUpdate
                {
                    RoomId = "room-1",
                    SessionId = "session-1",
                    RoomVersion = 3,
                    Lifecycle = RoomLifecycle.Lobby,
                    Capacity = 4
                }),
                TestWire.Envelope(MessageType.SyncRequest, e => e.SyncRequest = new SyncRequest
                {
                    Target = SyncTarget.PlayerStream,
                    KnownCursor = new StreamCursor { StreamVersion = 5, ProjectionSeq = 10 },
                    ReasonCode = ProtocolErrorCode.StreamGap,
                    RequestFullBaseline = true
                }),
                TestWire.Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
                {
                    Target = SyncTarget.PublicStream,
                    IsFullSnapshot = true,
                    Public = TestWire.PublicSnapshot(),
                    StateVersion = 7,
                    EventSeq = 101
                }),
                TestWire.Envelope(MessageType.ReceiptQuery, e => e.ReceiptQuery = new ReceiptQuery
                {
                    RequestScopeId = "scope-1",
                    RequestId = "request-1"
                }),
                TestWire.Envelope(MessageType.ReceiptLookup, e => e.ReceiptLookup = new ReceiptLookup
                {
                    Status = LookupStatus.NotFound,
                    CanonicalPayloadSha256 = new string('b', 64)
                }),
                TestWire.Envelope(MessageType.Heartbeat, e => e.Heartbeat = new Heartbeat
                {
                    Ping = new Ping { Nonce = "0123456789abcdef" }
                }),
                TestWire.Envelope(MessageType.Error, e => e.Error = new ProtocolError
                {
                    Code = ProtocolErrorCode.ServerBusy,
                    Retryable = true,
                    FieldPath = "command",
                    DetailCode = "QUEUE_FULL",
                    RetryAfterMs = 250
                }),
                TestWire.Envelope(MessageType.Close, e => e.Close = new Close
                {
                    Code = ProtocolErrorCode.SlowConsumer,
                    Permanent = true,
                    WebsocketCloseCode = 1008
                }),
                TestWire.Envelope(MessageType.PlayerUpdate, e => e.PlayerUpdate = new PlayerUpdate
                {
                    Snapshot = TestWire.PlayerSnapshot(),
                    FromCursor = new StreamCursor { StreamVersion = 5, ProjectionSeq = 10 },
                    Cursor = new StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                    IsFullSnapshot = false
                }),
                TestWire.Envelope(MessageType.PublicUpdate, e => e.PublicUpdate = new PublicUpdate
                {
                    Snapshot = TestWire.PublicSnapshot(),
                    FromCursor = new StreamCursor { StreamVersion = 5, ProjectionSeq = 10 },
                    Cursor = new StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                    IsFullSnapshot = false
                })
            };

            foreach (Envelope original in envelopes)
            {
                byte[] bytes = WireCodec.Encode(original);
                WireDecodeResult decoded = WireCodec.Decode(bytes);

                Assert.True(decoded.IsOk, original.MessageType + " -> " + decoded.Status + " " + decoded.ErrorCode);
                Assert.NotNull(decoded.Envelope);
                Assert.Equal(original.MessageType, decoded.Envelope!.MessageType);
                Assert.Equal(original.BodyCase, decoded.Envelope.BodyCase);
            }
        }

        [Fact]
        public void MajorMismatchIsClassifiedInsteadOfParsedAsV5()
        {
            Envelope envelope = TestWire.Envelope(MessageType.Hello, e => e.Hello = new Hello());
            envelope.ProtocolMajor = 2;

            WireDecodeResult result = WireCodec.Decode(envelope.ToByteArray());

            Assert.Equal(WireDecodeStatus.VersionRejected, result.Status);
            Assert.Equal(ProtocolErrorCode.ProtocolMajorUnsupported, result.ErrorCode);
            Assert.False(result.IsOk);
        }

        [Fact]
        public void ZeroRulesetOrProjectionIsRejectedInsteadOfThrowing()
        {
            Envelope ruleset = TestWire.Envelope(MessageType.Hello, e => e.Hello = new Hello());
            ruleset.RulesetVersion = 0;
            WireDecodeResult rulesetResult = WireCodec.Decode(ruleset.ToByteArray());

            Envelope projection = TestWire.Envelope(MessageType.Hello, e => e.Hello = new Hello());
            projection.ProjectionVersion = 0;
            WireDecodeResult projectionResult = WireCodec.Decode(projection.ToByteArray());

            Assert.Equal(WireDecodeStatus.VersionRejected, rulesetResult.Status);
            Assert.Equal(ProtocolErrorCode.RulesetVersionUnsupported, rulesetResult.ErrorCode);
            Assert.Equal(WireDecodeStatus.VersionRejected, projectionResult.Status);
            Assert.Equal(ProtocolErrorCode.ProjectionVersionUnsupported, projectionResult.ErrorCode);
        }

        [Fact]
        public void V4BusinessVersionEightIsRejectedAsLegacy()
        {
            Envelope envelope = TestWire.Envelope(MessageType.Hello, e => e.Hello = new Hello());
            envelope.ProtocolMajor = 8;

            WireDecodeResult result = WireCodec.Decode(envelope.ToByteArray());

            Assert.Equal(WireDecodeStatus.LegacyV4Rejected, result.Status);
            Assert.Equal(ProtocolErrorCode.LegacyV4Rejected, result.ErrorCode);
        }

        [Fact]
        public void MjctHeaderIsRejectedWithoutJsonFallback()
        {
            byte[] v4 = Encoding.ASCII.GetBytes("MJTC" + "\u0001" + "\u0000\u0000" + "\u0000\u0000\u0000\u0002{}");
            WireDecodeResult result = WireCodec.Decode(v4);

            Assert.Equal(WireDecodeStatus.LegacyV4Rejected, result.Status);
            Assert.Equal(ProtocolErrorCode.LegacyV4Rejected, result.ErrorCode);
        }

        [Fact]
        public void HigherMinorWithAnUnknownOptionalFieldIsAcceptedAndCounted()
        {
            Envelope envelope = TestWire.Envelope(MessageType.Hello, e => e.Hello = new Hello { Ticket = "ticket-1", ProtocolMajor = 1, ProtocolMinor = 1 });
            envelope.ProtocolMinor = 1;
            byte[] bytes = TestWire.AppendVarintField(envelope.ToByteArray(), 900, 1);

            WireDecodeResult result = WireCodec.Decode(bytes);

            Assert.True(result.IsOk);
            Assert.Equal(1, result.UnknownFieldCount);
            Envelope decoded = result.Envelope!;
            Assert.Equal(1u, decoded.ProtocolMinor);
            Assert.Equal("ticket-1", decoded.Hello.Ticket);
        }

        [Fact]
        public void UnknownTopLevelFieldsArePreservedByProtobuf()
        {
            Envelope envelope = TestWire.Envelope(MessageType.Hello, e => e.Hello = new Hello { Ticket = "ticket-1" });
            byte[] withUnknown = TestWire.AppendVarintField(envelope.ToByteArray(), 901, 42);

            WireDecodeResult result = WireCodec.Decode(withUnknown);
            Assert.True(result.IsOk);
            Envelope decoded = result.Envelope!;
            byte[] reencoded = decoded.ToByteArray();

            Assert.Equal(1, result.UnknownFieldCount);
            Assert.Equal(1, WireCodec.CountTopLevelUnknownFields(reencoded, Envelope.Descriptor));
            Assert.Equal("ticket-1", decoded.Hello.Ticket);
        }

        [Fact]
        public void UnknownMessageTypeIsSafeAndDoesNotParseABody()
        {
            var envelope = new Envelope
            {
                ProtocolMajor = 1,
                ProtocolMinor = 0,
                RulesetVersion = 1,
                ProjectionVersion = 1,
                MessageType = (MessageType)999,
                CorrelationId = "corr-1"
            };

            WireDecodeResult result = WireCodec.Decode(envelope.ToByteArray());

            Assert.Equal(WireDecodeStatus.UnknownMessageType, result.Status);
            Assert.Equal(ProtocolErrorCode.UnknownMessageType, result.ErrorCode);
        }

        [Fact]
        public void MissingAndMismatchedBodiesAreMalformed()
        {
            var missing = new Envelope
            {
                ProtocolMajor = 1,
                ProtocolMinor = 0,
                RulesetVersion = 1,
                ProjectionVersion = 1,
                MessageType = MessageType.Command
            };
            var mismatched = new Envelope
            {
                ProtocolMajor = 1,
                ProtocolMinor = 0,
                RulesetVersion = 1,
                ProjectionVersion = 1,
                MessageType = MessageType.Command
            };
            mismatched.Hello = new Hello();

            Assert.Equal(WireDecodeStatus.MissingBody, WireCodec.Decode(missing.ToByteArray()).Status);
            Assert.Equal(WireDecodeStatus.MalformedBody, WireCodec.Decode(mismatched.ToByteArray()).Status);
        }

        [Fact]
        public void OversizeMessageIsRejectedBeforeParsing()
        {
            byte[] bytes = new byte[WireLimits.MaxInboundMessageBytes + 1];

            WireMessageTooLargeException ex = Assert.Throws<WireMessageTooLargeException>(() => WireCodec.Decode(bytes));
            Assert.Equal(ProtocolErrorCode.MessageTooLarge, ex.Code);
        }

        [Fact]
        public void OversizeCommandPayloadIsRejectedWithoutTruncation()
        {
            var payload = new byte[WireLimits.MaxCommandPayloadBytes + 1];
            payload[0] = (byte)'M';
            payload[1] = (byte)'J';
            payload[2] = (byte)'C';
            payload[3] = (byte)'1';
            var command = new Command
            {
                Type = CommandType.SubmitAction,
                CanonicalPayload = ByteString.CopyFrom(payload),
                PayloadSchemaVersion = 1
            };

            WireMessageTooLargeException ex = Assert.Throws<WireMessageTooLargeException>(() => WireCodec.ValidateCommand(command));
            Assert.Equal(ProtocolErrorCode.MessageTooLarge, ex.Code);
        }

        [Fact]
        public void MalformedCanonicalCommandPayloadIsRejectedAtTheWireBoundary()
        {
            var command = new Command
            {
                Type = CommandType.SubmitAction,
                CanonicalPayload = ByteString.CopyFrom(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }),
                PayloadSchemaVersion = 1
            };

            WireMalformedException ex = Assert.Throws<WireMalformedException>(() => WireCodec.ValidateCommand(command));
            Assert.Equal(ProtocolErrorCode.MalformedMessage, ex.Code);
        }

        [Fact]
        public void HeartbeatNonceMustBeExactlySixteenLowercaseHexCharacters()
        {
            WireCodec.ValidateHeartbeatNonce("0123456789abcdef");
            Assert.Throws<WireMalformedException>(() => WireCodec.ValidateHeartbeatNonce("0123456789ABCDEF"));
            Assert.Throws<WireMalformedException>(() => WireCodec.ValidateHeartbeatNonce("0123456789abcde"));
            Assert.Throws<WireMalformedException>(() => WireCodec.ValidateHeartbeatNonce("0123456789abcdeg"));

            var bad = TestWire.Envelope(MessageType.Heartbeat, e => e.Heartbeat = new Heartbeat
            {
                Pong = new Pong { Nonce = "short" }
            });
            Assert.Throws<WireMalformedException>(() => WireCodec.Encode(bad));
        }

        [Fact]
        public void UnknownEventIsPreservedButNeverADomainFact()
        {
            var unknown = new EventEnvelope
            {
                EventSchemaVersion = 1,
                EventSeq = 12,
                CommandVersion = 7,
                EventOrdinal = 1,
                EventType = (EventType)999,
                Visibility = EventVisibility.Public,
                UnknownPayload = ByteString.CopyFrom(new byte[] { 9, 8, 7 })
            };

            WireEventDecodeResult result = WireCodec.DecodeEvent(unknown);

            Assert.Equal(WireEventDecodeStatus.Unknown, result.Status);
            Assert.NotNull(result.Record);
            Assert.False(result.Record!.IsDomainFact);
            Assert.True(result.Record.Unknown!.RequiresFullBaseline);
            Assert.Equal(999u, result.Record.Unknown.OriginalEventType);
            Assert.Equal(new byte[] { 9, 8, 7 }, result.Record.Unknown.CopyRawPayload());
            Assert.Equal(ProtocolErrorCode.StreamGap, result.ErrorCode);
        }

        [Fact]
        public void KnownEventTypeWithAnUnknownSchemaIsPreservedAsUnknown()
        {
            var future = TestWire.DiscardEvent();
            future.EventSchemaVersion = 2;

            WireEventDecodeResult result = WireCodec.DecodeEvent(future);

            Assert.Equal(WireEventDecodeStatus.Unknown, result.Status);
            Assert.False(result.Record!.IsDomainFact);
            Assert.Equal((uint)EventType.TileDiscarded, result.Record.Unknown!.OriginalEventType);
        }

        [Fact]
        public void KnownEventWithUnknownFieldsIsReportedAndNotPromoted()
        {
            EventEnvelope discard = TestWire.DiscardEvent();
            byte[] withUnknown = TestWire.AppendVarintField(discard.ToByteArray(), 902, 1);
            EventEnvelope parsed = EventEnvelope.Parser.ParseFrom(withUnknown);

            WireEventDecodeResult result = WireCodec.DecodeEvent(parsed);

            Assert.Equal(WireEventDecodeStatus.Known, result.Status);
            Assert.True(result.Record!.HasUnknownFields);
            Assert.Equal(1, result.UnknownFieldCount);
            Assert.Equal(ProtocolEventType.TileDiscarded, result.Record.Type);
            Assert.True(result.Record.HasPayload);
            TileDiscarded body = TileDiscarded.Parser.ParseFrom(result.Record.CopyWirePayload());
            Assert.Equal(5u, body.TileId);
        }

        [Fact]
        public void KnownEventTypeWithMissingBodyIsMalformed()
        {
            var missing = new EventEnvelope
            {
                EventSchemaVersion = 1,
                EventSeq = 1,
                CommandVersion = 1,
                EventType = EventType.TileDiscarded,
                Visibility = EventVisibility.Public
            };

            Assert.Equal(WireEventDecodeStatus.Malformed, WireCodec.DecodeEvent(missing).Status);
        }

        [Fact]
        public void EventTypeAndBodyMismatchIsMalformed()
        {
            var mismatch = new EventEnvelope
            {
                EventSchemaVersion = 1,
                EventSeq = 1,
                CommandVersion = 1,
                EventType = EventType.TileDiscarded,
                Visibility = EventVisibility.Public
            };
            mismatch.TileDrawn = new TileDrawn { TurnNumber = 1, Player = 0, TileId = 3 };

            Assert.Equal(WireEventDecodeStatus.Malformed, WireCodec.DecodeEvent(mismatch).Status);
        }

        [Fact]
        public void PublicSnapshotStructurallyExcludesPrivatePlayerFields()
        {
            HashSet<string> publicFields = TestWire.CollectReachableFieldNames(PublicSnapshot.Descriptor, skipEvents: false);

            string[] forbidden =
            {
                "closed_tiles", "drawn_tile_id", "discard_history", "legal_actions", "decision",
                "cheat_points", "ledger_entries", "tile_mark", "sleeve_bloom", "beam_swap",
                "preparation", "wall", "dead_wall", "seed", "tile_dealt", "tile_drawn",
                "rinshan_draw", "cheat_points_changed", "sleeve_bloom_resolved", "beam_swap_resolved"
            };

            foreach (string name in forbidden)
            {
                Assert.DoesNotContain(name, publicFields);
            }

            Assert.Null(PublicSnapshot.Descriptor.FindFieldByName("private_view"));
            Assert.NotNull(PlayerPrivateView.Descriptor.FindFieldByName("closed_tiles"));
            Assert.NotNull(PlayerPrivateView.Descriptor.FindFieldByName("cheat"));
            Assert.NotNull(PublicSnapshot.Descriptor.FindFieldByName("view"));
        }

        [Fact]
        public void PublicStreamGuardRejectsPrivateUnknownAndMistypedEvents()
        {
            PublicEventEnvelope publicDiscard = TestWire.PublicDiscardEvent();
            WireCodec.EnsurePublicEvent(publicDiscard);

            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("tile_drawn"));
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("tile_dealt"));
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("cheat_points_changed"));
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("sleeve_bloom_resolved"));
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("beam_swap_resolved"));

            var unknown = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = (EventType)999,
                Visibility = EventVisibility.Public,
                UnknownPayload = ByteString.CopyFrom(new byte[] { 1 })
            };
            Assert.Equal(ProtocolErrorCode.StreamGap, Assert.Throws<PublicProjectionException>(
                () => WireCodec.EnsurePublicEvent(unknown)).Code);

            var privateVisibility = TestWire.PublicDiscardEvent();
            privateVisibility.Visibility = EventVisibility.Private;
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, Assert.Throws<PublicProjectionException>(
                () => WireCodec.EnsurePublicEvent(privateVisibility)).Code);

            var mismatch = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = EventType.TileDiscarded,
                Visibility = EventVisibility.Public
            };
            mismatch.StackingStarted = new StackingStarted { TurnNumber = 1 };
            Assert.Equal(ProtocolErrorCode.MalformedMessage, Assert.Throws<PublicProjectionException>(
                () => WireCodec.EnsurePublicEvent(mismatch)).Code);
        }

        [Fact]
        public void PublicEventEnvelopeDecodesKnownAndUnknownSafely()
        {
            PublicEventEnvelope discard = TestWire.PublicDiscardEvent();
            WireEventDecodeResult known = WireCodec.DecodePublicEvent(PublicEventEnvelope.Parser.ParseFrom(discard.ToByteArray()));

            Assert.Equal(WireEventDecodeStatus.Known, known.Status);
            Assert.Equal(ProtocolEventType.TileDiscarded, known.Record!.Type);
            TileDiscarded body = TileDiscarded.Parser.ParseFrom(known.Record.CopyWirePayload());
            Assert.Equal(5u, body.TileId);

            var future = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = (EventType)999,
                Visibility = EventVisibility.Public,
                UnknownPayload = ByteString.CopyFrom(new byte[] { 4, 5, 6 })
            };
            WireEventDecodeResult unknown = WireCodec.DecodePublicEvent(future);

            Assert.Equal(WireEventDecodeStatus.Unknown, unknown.Status);
            Assert.False(unknown.Record!.IsDomainFact);
            Assert.True(unknown.RequiresFullBaseline);
        }

        [Fact]
        public void PublicStreamGuardRejectsUnauthorizedUraDetails()
        {
            var uraDora = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = EventType.DoraRevealed,
                Visibility = EventVisibility.SettlementPublic
            };
            uraDora.DoraRevealed = new DoraRevealed { TurnNumber = 1, IndicatorTileId = 3, IsUraDora = true };
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, Assert.Throws<PublicProjectionException>(
                () => WireCodec.EnsurePublicEvent(uraDora)).Code);

            uraDora.DoraRevealed.UraAuthorized = true;
            uraDora.Visibility = EventVisibility.Public;
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, Assert.Throws<PublicProjectionException>(
                () => WireCodec.EnsurePublicEvent(uraDora)).Code);

            uraDora.Visibility = EventVisibility.SettlementPublic;
            WireCodec.EnsurePublicEvent(uraDora);

            var win = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = EventType.TsumoWin,
                Visibility = EventVisibility.SettlementPublic
            };
            win.TsumoWin = new TsumoWin
            {
                TurnNumber = 1,
                Player = 0,
                WinTileId = 5,
                Han = 1,
                Fu = 30,
                Score = 1000,
                Breakdown = new ScoreBreakdownView { Han = 1, Fu = 30, TotalPoints = 1000 }
            };
            win.TsumoWin.Breakdown.UraTiles.Add(7);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, Assert.Throws<PublicProjectionException>(
                () => WireCodec.EnsurePublicEvent(win)).Code);

            win.TsumoWin.Breakdown.UraAuthorized = true;
            WireCodec.EnsurePublicEvent(win);
        }

        [Fact]
        public void PublicSyncResponseEncodingRejectsUnauthorizedUraAndPrivateEvents()
        {
            PublicSnapshot uraSnapshot = TestWire.PublicSnapshot();
            var uraEvent = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventSeq = 101,
                CommandVersion = 7,
                EventType = EventType.TsumoWin,
                Visibility = EventVisibility.SettlementPublic
            };
            uraEvent.TsumoWin = new TsumoWin
            {
                TurnNumber = 1,
                Player = 0,
                WinTileId = 5,
                Han = 1,
                Fu = 30,
                Score = 1000,
                Breakdown = new ScoreBreakdownView { Han = 1, Fu = 30, TotalPoints = 1000 }
            };
            uraEvent.TsumoWin.Breakdown.UraTiles.Add(7);
            uraSnapshot.View.SettlementEvents.Add(uraEvent);

            PublicProjectionException ura = Assert.Throws<PublicProjectionException>(
                () => WireCodec.Encode(TestWire.PublicSyncResponse(uraSnapshot)));
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, ura.Code);

            PublicSnapshot privateSnapshot = TestWire.PublicSnapshot();
            PublicEventEnvelope privateEvent = TestWire.PublicDiscardEvent();
            privateEvent.Visibility = EventVisibility.Private;
            privateSnapshot.View.SettlementEvents.Add(privateEvent);

            PublicProjectionException hidden = Assert.Throws<PublicProjectionException>(
                () => WireCodec.Encode(TestWire.PublicSyncResponse(privateSnapshot)));
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, hidden.Code);
        }

        [Fact]
        public void PublicSyncResponseDecodingRejectsTheSameUnauthorizedPayloads()
        {
            PublicSnapshot uraSnapshot = TestWire.PublicSnapshot();
            var uraEvent = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventSeq = 101,
                CommandVersion = 7,
                EventType = EventType.DoraRevealed,
                Visibility = EventVisibility.SettlementPublic
            };
            uraEvent.DoraRevealed = new DoraRevealed { TurnNumber = 1, IndicatorTileId = 3, IsUraDora = true };
            uraSnapshot.View.SettlementEvents.Add(uraEvent);

            // Bypass Encode deliberately: the receiver must reject the same
            // payload even when the sender skipped the encode-side guard.
            WireDecodeResult ura = WireCodec.Decode(TestWire.PublicSyncResponse(uraSnapshot).ToByteArray());
            Assert.Equal(WireDecodeStatus.MalformedBody, ura.Status);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, ura.ErrorCode);

            PublicSnapshot privateSnapshot = TestWire.PublicSnapshot();
            PublicEventEnvelope privateEvent = TestWire.PublicDiscardEvent();
            privateEvent.Visibility = EventVisibility.Private;
            privateSnapshot.View.SettlementEvents.Add(privateEvent);

            WireDecodeResult hidden = WireCodec.Decode(TestWire.PublicSyncResponse(privateSnapshot).ToByteArray());
            Assert.Equal(WireDecodeStatus.MalformedBody, hidden.Status);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, hidden.ErrorCode);
        }

        [Fact]
        public void PublicSyncResponseWithALegalPublicBaselineRoundTrips()
        {
            PublicSnapshot snapshot = TestWire.PublicSnapshot();
            snapshot.View.Board = new PublicBoardView
            {
                Phase = GamePhase.Discard,
                RoundWind = 1,
                RoundNumber = 2,
                DealerSeat = 0,
                CurrentSeat = 1,
                WallRemaining = 42
            };
            snapshot.View.Board.DoraIndicators.Add(3);
            snapshot.View.Board.Scores.Add(new ScoreView { Seat = 0, Score = 25000 });
            snapshot.View.Board.Players.Add(new PlayerPublicView { Seat = 0, HandCount = 13 });
            snapshot.View.SettlementEvents.Add(TestWire.PublicDiscardEvent());
            Envelope envelope = TestWire.PublicSyncResponse(snapshot);

            byte[] bytes = WireCodec.Encode(envelope);
            WireDecodeResult decoded = WireCodec.Decode(bytes);

            Assert.True(decoded.IsOk);
            Assert.Equal(SyncResponse.BaselineOneofCase.Public, decoded.Envelope!.SyncResponse.BaselineCase);
            Assert.Single(decoded.Envelope.SyncResponse.Public.View.SettlementEvents);
            Assert.Equal(EventType.TileDiscarded, decoded.Envelope.SyncResponse.Public.View.SettlementEvents[0].EventType);
            WireCodec.EnsurePublicSnapshot(decoded.Envelope.SyncResponse.Public);
        }

        [Fact]
        public void SyncResponseTargetMustMatchBaselineAndBeFull()
        {
            var invalid = new[]
            {
                new SyncResponse
                {
                    Target = SyncTarget.PublicStream,
                    IsFullSnapshot = true,
                    Player = TestWire.PlayerSnapshot()
                },
                new SyncResponse
                {
                    Target = SyncTarget.PlayerStream,
                    IsFullSnapshot = true,
                    Public = TestWire.PublicSnapshot()
                },
                new SyncResponse
                {
                    Target = SyncTarget.Room,
                    IsFullSnapshot = true,
                    Public = TestWire.PublicSnapshot()
                },
                new SyncResponse
                {
                    Target = SyncTarget.Unspecified,
                    IsFullSnapshot = true,
                    Public = TestWire.PublicSnapshot()
                },
                new SyncResponse
                {
                    Target = SyncTarget.PublicStream,
                    IsFullSnapshot = true
                },
                new SyncResponse
                {
                    Target = SyncTarget.PublicStream,
                    IsFullSnapshot = false,
                    Public = TestWire.PublicSnapshot()
                }
            };

            foreach (SyncResponse response in invalid)
            {
                Envelope envelope = TestWire.Envelope(MessageType.SyncResponse, e => e.SyncResponse = response);
                WireMalformedException encode = Assert.Throws<WireMalformedException>(() => WireCodec.Encode(envelope));
                Assert.Equal(ProtocolErrorCode.MalformedMessage, encode.Code);

                WireDecodeResult decode = WireCodec.Decode(envelope.ToByteArray());
                Assert.Equal(WireDecodeStatus.MalformedBody, decode.Status);
                Assert.Equal(ProtocolErrorCode.MalformedMessage, decode.ErrorCode);
                Assert.Contains("SYNC_RESPONSE", encode.Message);
            }
        }

        [Fact]
        public void LegalSyncResponseTargetAndBaselineCombinationsRoundTrip()
        {
            var valid = new (SyncTarget Target, SyncResponse Response, SyncResponse.BaselineOneofCase Baseline)[]
            {
                (SyncTarget.PlayerStream, new SyncResponse
                {
                    Target = SyncTarget.PlayerStream,
                    IsFullSnapshot = true,
                    Player = TestWire.PlayerSnapshot()
                }, SyncResponse.BaselineOneofCase.Player),
                (SyncTarget.PublicStream, new SyncResponse
                {
                    Target = SyncTarget.PublicStream,
                    IsFullSnapshot = true,
                    Public = TestWire.PublicSnapshot()
                }, SyncResponse.BaselineOneofCase.Public),
                (SyncTarget.Room, new SyncResponse
                {
                    Target = SyncTarget.Room,
                    IsFullSnapshot = true,
                    Room = new RoomUpdate { RoomId = "room-1", Lifecycle = RoomLifecycle.Lobby }
                }, SyncResponse.BaselineOneofCase.Room)
            };

            foreach ((SyncTarget target, SyncResponse response, SyncResponse.BaselineOneofCase baseline) in valid)
            {
                Envelope envelope = TestWire.Envelope(MessageType.SyncResponse, e => e.SyncResponse = response);
                WireDecodeResult decoded = WireCodec.Decode(WireCodec.Encode(envelope));

                Assert.True(decoded.IsOk);
                Assert.Equal(target, decoded.Envelope!.SyncResponse.Target);
                Assert.Equal(baseline, decoded.Envelope.SyncResponse.BaselineCase);
                Assert.True(decoded.Envelope.SyncResponse.IsFullSnapshot);
            }
        }

        [Fact]
        public void KnownPublicEventCannotCarryUnknownPayloadOnAnyPublicEncodeExit()
        {
            PublicEventEnvelope poisoned = TestWire.PublicDiscardEvent();
            poisoned.UnknownPayload = ByteString.CopyFrom(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

            PublicProjectionException direct = Assert.Throws<PublicProjectionException>(
                () => WireCodec.EnsurePublicEvent(poisoned));
            Assert.Equal(ProtocolErrorCode.MalformedMessage, direct.Code);

            WireEventDecodeResult decodedEvent = WireCodec.DecodePublicEvent(poisoned);
            Assert.Equal(WireEventDecodeStatus.Malformed, decodedEvent.Status);
            Assert.Equal(ProtocolErrorCode.MalformedMessage, decodedEvent.ErrorCode);
            Assert.Null(decodedEvent.Record);

            PublicSnapshot snapshot = TestWire.PublicSnapshot();
            snapshot.View.SettlementEvents.Add(poisoned);
            var envelopes = new[]
            {
                TestWire.Envelope(MessageType.PublicSnapshot, e => e.PublicSnapshot = snapshot),
                TestWire.Envelope(MessageType.PublicUpdate, e => e.PublicUpdate = new PublicUpdate
                {
                    Snapshot = snapshot,
                    IsFullSnapshot = true
                }),
                TestWire.PublicSyncResponse(snapshot)
            };

            foreach (Envelope envelope in envelopes)
            {
                PublicProjectionException encode = Assert.Throws<PublicProjectionException>(() => WireCodec.Encode(envelope));
                Assert.Equal(ProtocolErrorCode.MalformedMessage, encode.Code);

                WireDecodeResult decode = WireCodec.Decode(envelope.ToByteArray());
                Assert.Equal(WireDecodeStatus.MalformedBody, decode.Status);
                Assert.Equal(ProtocolErrorCode.MalformedMessage, decode.ErrorCode);
            }

            // An actually unknown event keeps its raw bytes and baseline
            // requirement; only known events are barred from the byte channel.
            var future = new PublicEventEnvelope
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventType = (EventType)999,
                Visibility = EventVisibility.Public,
                UnknownPayload = ByteString.CopyFrom(new byte[] { 0x01, 0x02 })
            };
            WireEventDecodeResult futureResult = WireCodec.DecodePublicEvent(future);
            Assert.Equal(WireEventDecodeStatus.Unknown, futureResult.Status);
            Assert.True(futureResult.RequiresFullBaseline);
            Assert.Equal(new byte[] { 0x01, 0x02 }, futureResult.Record!.Unknown!.CopyRawPayload());
        }

        [Fact]
        public void PublicEncodeExitsRejectUnknownFieldsAtEveryLevel()
        {
            byte[] marker = { (byte)'P', (byte)'R', (byte)'I', (byte)'V', (byte)'A', (byte)'T', (byte)'E' };
            var poisoned = new List<PublicSnapshot>();

            PublicSnapshot top = TestWire.PublicSnapshot();
            poisoned.Add(PublicSnapshot.Parser.ParseFrom(
                TestWire.AppendLengthDelimitedField(top.ToByteArray(), 902, marker)));

            PublicSnapshot nestedView = TestWire.PublicSnapshot();
            nestedView.View = PublicMatchView.Parser.ParseFrom(
                TestWire.AppendLengthDelimitedField(nestedView.View.ToByteArray(), 903, marker));
            poisoned.Add(nestedView);

            PublicSnapshot nestedEvent = TestWire.PublicSnapshot();
            nestedEvent.View.SettlementEvents.Add(PublicEventEnvelope.Parser.ParseFrom(
                TestWire.AppendLengthDelimitedField(TestWire.PublicDiscardEvent().ToByteArray(), 904, marker)));
            poisoned.Add(nestedEvent);

            foreach (PublicSnapshot snapshot in poisoned)
            {
                Assert.Equal(
                    ProtocolErrorCode.MalformedMessage,
                    Assert.Throws<PublicProjectionException>(() => WireCodec.EnsurePublicSnapshot(snapshot)).Code);

                var update = new PublicUpdate { Snapshot = snapshot, IsFullSnapshot = true };
                Assert.Equal(
                    ProtocolErrorCode.MalformedMessage,
                    Assert.Throws<PublicProjectionException>(() => WireCodec.EnsurePublicUpdate(update)).Code);

                var envelopes = new[]
                {
                    TestWire.Envelope(MessageType.PublicSnapshot, e => e.PublicSnapshot = snapshot),
                    TestWire.Envelope(MessageType.PublicUpdate, e => e.PublicUpdate = update),
                    TestWire.PublicSyncResponse(snapshot)
                };

                foreach (Envelope envelope in envelopes)
                {
                    PublicProjectionException ex = Assert.Throws<PublicProjectionException>(
                        () => WireCodec.Encode(envelope));
                    Assert.Equal(ProtocolErrorCode.MalformedMessage, ex.Code);
                }
            }

            Assert.Equal(
                ProtocolErrorCode.MalformedMessage,
                Assert.Throws<PublicProjectionException>(
                    () => WireCodec.EnsurePublicEvent(nestedEvent.View.SettlementEvents[0])).Code);
        }

        [Fact]
        public void PublicUpdateVisibleEventsRejectUnknownFieldsToo()
        {
            PublicEventEnvelope poisoned = PublicEventEnvelope.Parser.ParseFrom(
                TestWire.AppendLengthDelimitedField(
                    TestWire.PublicDiscardEvent().ToByteArray(),
                    907,
                    new byte[] { 0xFE, 0xED, 0xFA, 0xCE }));

            var update = new PublicUpdate
            {
                Snapshot = TestWire.PublicSnapshot(),
                IsFullSnapshot = true
            };
            update.VisibleEvents.Add(poisoned);
            Envelope envelope = TestWire.Envelope(MessageType.PublicUpdate, e => e.PublicUpdate = update);

            PublicProjectionException ex = Assert.Throws<PublicProjectionException>(() => WireCodec.Encode(envelope));
            Assert.Equal(ProtocolErrorCode.MalformedMessage, ex.Code);
        }

        [Fact]
        public void InboundPublicUnknownFieldsAreAcceptedButNeverReemitted()
        {
            PublicSnapshot snapshot = TestWire.PublicSnapshot();
            snapshot.View.SettlementEvents.Add(TestWire.PublicDiscardEvent());
            byte[] bytes = TestWire.AppendLengthDelimitedField(
                TestWire.Envelope(MessageType.PublicSnapshot, e => e.PublicSnapshot = snapshot).ToByteArray(),
                905,
                new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

            WireDecodeResult decoded = WireCodec.Decode(bytes);

            Assert.True(decoded.IsOk);
            Assert.Equal(1, decoded.UnknownFieldCount);
            PublicProjectionException ex = Assert.Throws<PublicProjectionException>(
                () => WireCodec.Encode(decoded.Envelope!));
            Assert.Equal(ProtocolErrorCode.MalformedMessage, ex.Code);
        }

        [Fact]
        public void DecodePublicEventRejectsPrivateVisibilityAndQuarantinesUnknownFields()
        {
            PublicEventEnvelope privateEvent = TestWire.PublicDiscardEvent();
            privateEvent.Visibility = EventVisibility.Private;
            WireEventDecodeResult privateResult = WireCodec.DecodePublicEvent(privateEvent);
            Assert.Equal(WireEventDecodeStatus.Malformed, privateResult.Status);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, privateResult.ErrorCode);
            Assert.Null(privateResult.Record);

            byte[] marker = { 0xDE, 0xAD, 0xBE, 0xEF };
            PublicEventEnvelope poisoned = PublicEventEnvelope.Parser.ParseFrom(
                TestWire.AppendLengthDelimitedField(TestWire.PublicDiscardEvent().ToByteArray(), 906, marker));
            WireEventDecodeResult result = WireCodec.DecodePublicEvent(poisoned);

            Assert.Equal(WireEventDecodeStatus.Unknown, result.Status);
            Assert.False(result.Record!.IsDomainFact);
            Assert.True(result.RequiresFullBaseline);
            Assert.Contains(marker[0], result.Record.Unknown!.CopyRawPayload());
        }

        [Fact]
        public void DecodePublicEventPrivateVisibilityWinsBeforeUnknownBranches()
        {
            var privateUnknownSchema = new PublicEventEnvelope
            {
                EventSchemaVersion = 2,
                EventType = (EventType)999,
                Visibility = EventVisibility.Private,
                UnknownPayload = ByteString.CopyFrom(new byte[] { 0xAA, 0xBB })
            };
            WireEventDecodeResult schemaResult = WireCodec.DecodePublicEvent(privateUnknownSchema);
            Assert.Equal(WireEventDecodeStatus.Malformed, schemaResult.Status);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, schemaResult.ErrorCode);
            Assert.Null(schemaResult.Record);

            var unspecifiedVisibility = new PublicEventEnvelope
            {
                EventSchemaVersion = 2,
                EventType = (EventType)999,
                Visibility = EventVisibility.Unspecified,
                UnknownPayload = ByteString.CopyFrom(new byte[] { 0xCC })
            };
            WireEventDecodeResult unspecifiedResult = WireCodec.DecodePublicEvent(unspecifiedVisibility);
            Assert.Equal(WireEventDecodeStatus.Malformed, unspecifiedResult.Status);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, unspecifiedResult.ErrorCode);
            Assert.Null(unspecifiedResult.Record);
        }

        [Fact]
        public void DecodePublicEventKeepsUnknownButPublicEventsAsFullBaseline()
        {
            var futureSchema = new PublicEventEnvelope
            {
                EventSchemaVersion = 2,
                EventType = EventType.TileDiscarded,
                Visibility = EventVisibility.Public,
                UnknownPayload = ByteString.CopyFrom(new byte[] { 0x01, 0x02 })
            };
            WireEventDecodeResult result = WireCodec.DecodePublicEvent(futureSchema);

            Assert.Equal(WireEventDecodeStatus.Unknown, result.Status);
            Assert.NotNull(result.Record);
            Assert.False(result.Record!.IsDomainFact);
            Assert.True(result.RequiresFullBaseline);
        }

        [Fact]
        public void DecodePublicEventRejectsUnauthorizedUraForKnownEvents()
        {
            var uraDora = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = EventType.DoraRevealed,
                Visibility = EventVisibility.SettlementPublic
            };
            uraDora.DoraRevealed = new DoraRevealed { TurnNumber = 1, IndicatorTileId = 3, IsUraDora = true };
            WireEventDecodeResult unauthorizedDora = WireCodec.DecodePublicEvent(uraDora);
            Assert.Equal(WireEventDecodeStatus.Malformed, unauthorizedDora.Status);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, unauthorizedDora.ErrorCode);
            Assert.Null(unauthorizedDora.Record);

            uraDora.DoraRevealed.UraAuthorized = true;
            uraDora.Visibility = EventVisibility.Public;
            WireEventDecodeResult publicBatchDora = WireCodec.DecodePublicEvent(uraDora);
            Assert.Equal(WireEventDecodeStatus.Malformed, publicBatchDora.Status);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, publicBatchDora.ErrorCode);

            uraDora.Visibility = EventVisibility.SettlementPublic;
            WireEventDecodeResult authorizedDora = WireCodec.DecodePublicEvent(uraDora);
            Assert.Equal(WireEventDecodeStatus.Known, authorizedDora.Status);
            Assert.True(authorizedDora.Record!.IsDomainFact);

            var win = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = EventType.TsumoWin,
                Visibility = EventVisibility.SettlementPublic
            };
            win.TsumoWin = new TsumoWin
            {
                TurnNumber = 1,
                Player = 0,
                WinTileId = 5,
                Breakdown = new ScoreBreakdownView { Han = 1, Fu = 30, TotalPoints = 1000 }
            };
            win.TsumoWin.Breakdown.UraTiles.Add(7);
            WireEventDecodeResult unauthorizedWin = WireCodec.DecodePublicEvent(win);
            Assert.Equal(WireEventDecodeStatus.Malformed, unauthorizedWin.Status);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, unauthorizedWin.ErrorCode);
            Assert.Null(unauthorizedWin.Record);

            win.TsumoWin.Breakdown.UraAuthorized = true;
            WireEventDecodeResult authorizedWin = WireCodec.DecodePublicEvent(win);
            Assert.Equal(WireEventDecodeStatus.Known, authorizedWin.Status);

            var ron = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventType = EventType.RonWin,
                Visibility = EventVisibility.SettlementPublic
            };
            ron.RonWin = new RonWin
            {
                TurnNumber = 1,
                Winner = 1,
                Loser = 2,
                WinTileId = 6,
                Breakdown = new ScoreBreakdownView { Han = 1, Fu = 30, TotalPoints = 1000 }
            };
            ron.RonWin.Breakdown.UraHits.Add(8);
            WireEventDecodeResult unauthorizedRon = WireCodec.DecodePublicEvent(ron);
            Assert.Equal(WireEventDecodeStatus.Malformed, unauthorizedRon.Status);
            Assert.Equal(ProtocolErrorCode.PrivateDataInPublicStream, unauthorizedRon.ErrorCode);
        }

        [Fact]
        public void PublicSyncResponseOversizeSnapshotIsRejectedOnEncode()
        {
            PublicSnapshot snapshot = TestWire.PublicSnapshot();
            var huge = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventSeq = 101,
                CommandVersion = 7,
                EventType = EventType.TsumoWin,
                Visibility = EventVisibility.Public
            };
            huge.TsumoWin = new TsumoWin { TurnNumber = 1, Player = 0, WinTileId = 5 };
            for (int i = 0; i < 20; i++)
            {
                huge.TsumoWin.Yaku.Add(new YakuView { Code = new string('A', 60000), Han = 1 });
            }

            snapshot.View.SettlementEvents.Add(huge);

            WireMessageTooLargeException ex = Assert.Throws<WireMessageTooLargeException>(
                () => WireCodec.Encode(TestWire.PublicSyncResponse(snapshot)));
            Assert.Equal(ProtocolErrorCode.MessageTooLarge, ex.Code);
        }

        [Fact]
        public void CanonicalDigestUsesOriginalPayloadNotProtobufReserialization()
        {
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                "round-1", "decision-1", 1, ProtocolActionType.DiscardTile, tileIds: new uint[] { 5, 6 });
            Command command = TestWire.Command(payload);
            PayloadDigest canonical = WireCodec.ComputeCanonicalDigest(command);

            byte[] protobuf = command.ToByteArray();
            byte[] reencodedWithUnknown = TestWire.AppendVarintField(protobuf, 903, 1);
            Command reparsed = Command.Parser.ParseFrom(reencodedWithUnknown);
            PayloadDigest afterReparse = WireCodec.ComputeCanonicalDigest(reparsed);

            string protobufHash = TestWire.Sha256Hex(protobuf);

            Assert.Equal(canonical, afterReparse);
            Assert.Equal(payload.Digest, canonical);
            Assert.NotEqual(canonical.ToHex(), protobufHash);
            Assert.Equal(payload.CopyBytes(), reparsed.CanonicalPayload.ToByteArray());
        }

        [Fact]
        public void WireCommandRoundTripRetainsExactBusinessBytesForRetry()
        {
            BusinessPayload payload = CommandPayloadFactory.SubmitTileMark(
                "round-1",
                "decision-1",
                2,
                new uint[] { 1, 2, 3, 4 });
            Envelope envelope = TestWire.Envelope(MessageType.Command, e => e.Command = TestWire.Command(payload, CommandType.SubmitTileMark));

            byte[] bytes = WireCodec.Encode(envelope);
            WireDecodeResult decoded = WireCodec.Decode(bytes);

            Assert.True(decoded.IsOk);
            Assert.Equal(payload.CopyBytes(), decoded.Envelope!.Command.CanonicalPayload.ToByteArray());
            Assert.Equal(payload.Digest, WireCodec.ComputeCanonicalDigest(decoded.Envelope.Command));

            var protocolCommand = new ProtocolCommand(
                CommandKind.SubmitTileMark,
                BusinessPayload.Retain(decoded.Envelope.Command.CanonicalPayload.Span),
                new RequestId("request-1"),
                new RequestScopeId("scope-1"),
                new StateVersion(4));
            ProtocolCommand retry = protocolCommand.Retry();

            Assert.Equal(protocolCommand.CanonicalDigest, retry.CanonicalDigest);
            Assert.Equal(payload.CopyBytes(), retry.Payload.CopyBytes());
        }

        [Fact]
        public void EpochAndVersionFieldsRemainDistinctOnTheWire()
        {
            Envelope envelope = TestWire.Envelope(MessageType.PlayerUpdate, e => e.PlayerUpdate = new PlayerUpdate
            {
                Snapshot = TestWire.PlayerSnapshot(),
                Cursor = new StreamCursor { StreamVersion = 40, ProjectionSeq = 41 },
                FromCursor = new StreamCursor { StreamVersion = 39, ProjectionSeq = 40 }
            });
            envelope.BindingEpoch = 1;
            envelope.ControlEpoch = 2;
            envelope.OwnerEpoch = 3;
            envelope.Scope = TestWire.Scope();
            envelope.RequestId = "request-1";
            envelope.Cursor = new StreamCursor { StreamVersion = 40, ProjectionSeq = 41 };
            envelope.FromCursor = new StreamCursor { StreamVersion = 39, ProjectionSeq = 40 };

            WireDecodeResult decoded = WireCodec.Decode(WireCodec.Encode(envelope));

            Assert.True(decoded.IsOk);
            Envelope actual = decoded.Envelope!;
            Assert.Equal(1ul, actual.BindingEpoch);
            Assert.Equal(2ul, actual.ControlEpoch);
            Assert.Equal(3ul, actual.OwnerEpoch);
            Assert.Equal(40ul, actual.Cursor.StreamVersion);
            Assert.Equal(41ul, actual.Cursor.ProjectionSeq);
            Assert.Equal("scope-1", actual.Scope.ScopeId);
            Assert.Equal("request-1", actual.RequestId);
        }
    }

    internal static class TestWire
    {
        public static Envelope Envelope(MessageType type, Action<Envelope> assignBody)
        {
            var envelope = new Envelope
            {
                ProtocolMajor = 1,
                ProtocolMinor = 0,
                RulesetVersion = 1,
                ProjectionVersion = 1,
                MessageType = type,
                CorrelationId = "corr-1",
                RequestId = "request-1"
            };
            assignBody(envelope);
            return envelope;
        }

        public static RequestScope Scope()
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

        public static Welcome Welcome()
        {
            return new Welcome
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
                    Cursor = new StreamCursor { StreamVersion = 7, ProjectionSeq = 11 }
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
                ProtocolMajor = 1,
                ProtocolMinor = 0,
                RulesetVersion = 1,
                ProjectionVersion = 1
            };
        }

        public static BusinessPayload DefaultPayload()
        {
            return CommandPayloadFactory.SubmitAction(
                "round-1",
                "decision-1",
                1,
                ProtocolActionType.DiscardTile,
                tileIds: new uint[] { 5 });
        }

        public static Command Command(BusinessPayload? payload = null, CommandType type = CommandType.SubmitAction)
        {
            return new Command
            {
                Type = type,
                CanonicalPayload = ByteString.CopyFrom((payload ?? DefaultPayload()).CopyBytes()),
                PayloadSchemaVersion = 1,
                ExpectedStateVersion = 4,
                ExpectedRoundId = "round-1",
                ExpectedDecisionId = "decision-1"
            };
        }

        public static PublicSnapshot PublicSnapshot()
        {
            var snapshot = new PublicSnapshot
            {
                SessionId = "session-1",
                RoundId = "round-1",
                Cursor = new StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                StateVersion = 7,
                EventSeq = 101,
                SessionVersion = 1,
                RoundVersion = 2
            };
            snapshot.View = new PublicMatchView
            {
                Session = new PublicSessionSummary { Finished = false },
                Paused = false,
                SettlementRoundId = "round-1"
            };
            return snapshot;
        }

        public static Envelope PublicSyncResponse(PublicSnapshot snapshot)
        {
            return Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
            {
                Target = SyncTarget.PublicStream,
                IsFullSnapshot = true,
                Public = snapshot,
                StateVersion = 7,
                EventSeq = 101
            });
        }

        public static PlayerSnapshot PlayerSnapshot()
        {            var snapshot = new PlayerSnapshot
            {
                SessionId = "session-1",
                RoundId = "round-1",
                Cursor = new StreamCursor { StreamVersion = 7, ProjectionSeq = 11 },
                StateVersion = 7,
                EventSeq = 101,
                SessionVersion = 1,
                RoundVersion = 2
            };
            snapshot.PublicView = new PublicMatchView
            {
                Session = new PublicSessionSummary { Finished = false },
                Paused = false,
                SettlementRoundId = "round-1"
            };
            snapshot.PrivateView = new PlayerPrivateView
            {
                Seat = 0,
                Furiten = false
            };
            snapshot.PrivateView.ClosedTiles.Add(1);
            snapshot.PrivateView.ClosedTiles.Add(2);
            return snapshot;
        }

        public static EventEnvelope DiscardEvent()
        {
            var envelope = new EventEnvelope
            {
                EventSchemaVersion = 1,
                EventSeq = 1,
                CommandVersion = 1,
                EventOrdinal = 0,
                EventType = EventType.TileDiscarded,
                Visibility = EventVisibility.Public
            };
            envelope.TileDiscarded = new TileDiscarded
            {
                TurnNumber = 1,
                Player = 0,
                TileId = 5,
                RiichiDiscard = false,
                Tsumogiri = false
            };
            return envelope;
        }

        public static PublicEventEnvelope PublicDiscardEvent()
        {
            var envelope = new PublicEventEnvelope
            {
                EventSchemaVersion = 1,
                EventSeq = 1,
                CommandVersion = 1,
                EventOrdinal = 0,
                EventType = EventType.TileDiscarded,
                Visibility = EventVisibility.Public
            };
            envelope.TileDiscarded = new TileDiscarded
            {
                TurnNumber = 1,
                Player = 0,
                TileId = 5,
                RiichiDiscard = false,
                Tsumogiri = false
            };
            return envelope;
        }

        public static byte[] AppendVarintField(byte[] message, int fieldNumber, ulong value)
        {
            var bytes = new List<byte>(message);
            WriteVarint(bytes, (ulong)((fieldNumber << 3) | 0));
            WriteVarint(bytes, value);
            return bytes.ToArray();
        }

        public static byte[] AppendLengthDelimitedField(byte[] message, int fieldNumber, byte[] payload)
        {
            var bytes = new List<byte>(message);
            WriteVarint(bytes, (ulong)((fieldNumber << 3) | 2));
            WriteVarint(bytes, (ulong)payload.Length);
            bytes.AddRange(payload);
            return bytes.ToArray();
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        public static HashSet<string> CollectReachableFieldNames(MessageDescriptor root, bool skipEvents)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<MessageDescriptor>();
            var pending = new Stack<MessageDescriptor>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                MessageDescriptor descriptor = pending.Pop();
                if (!visited.Add(descriptor))
                {
                    continue;
                }

                foreach (FieldDescriptor field in descriptor.Fields.InDeclarationOrder())
                {
                    names.Add(field.Name);
                    if (field.FieldType != FieldType.Message || field.MessageType == null)
                    {
                        continue;
                    }

                    if (skipEvents && field.MessageType.FullName == "mahjong.v1.EventEnvelope")
                    {
                        continue;
                    }

                    pending.Push(field.MessageType);
                }
            }

            return names;
        }

        private static void WriteVarint(List<byte> destination, ulong value)
        {
            while (value >= 0x80)
            {
                destination.Add((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }

            destination.Add((byte)value);
        }
    }
}
