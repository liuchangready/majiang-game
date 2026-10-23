using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;
using WirePublicMatchView = Mahjong.Contracts.Wire.PublicMatchView;
using WirePublicSessionSummary = Mahjong.Contracts.Wire.PublicSessionSummary;
using WireRole = Mahjong.Contracts.Wire.Role;
using WireScope = Mahjong.Contracts.Wire.RequestScope;
using WireStreamBaseline = Mahjong.Contracts.Wire.StreamBaseline;

namespace Mahjong.Client.Wss.Tests
{
    internal static class TestWire
    {
        public const string SessionId = "session-1";
        public const string RoomId = "room-1";

        public static Envelope Envelope(MessageType type, Action<Envelope> assignBody)
        {
            var envelope = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = type
            };
            assignBody(envelope);
            return envelope;
        }

        public static Envelope Bound(Envelope envelope, int bindingEpoch, int seatId = 0)
        {
            envelope.BindingEpoch = (ulong)bindingEpoch;
            envelope.SeatId = seatId;
            envelope.SessionId = SessionId;
            envelope.RoomId = RoomId;
            return envelope;
        }

        public static WireScope Scope(string scopeId = "scope-1", string accountId = "account-1", WireRole role = WireRole.Player)
        {
            return new WireScope
            {
                ScopeId = scopeId,
                AccountId = accountId,
                Role = role,
                RoomId = RoomId,
                IssuedAtMs = 1000,
                ExpiresAtMs = 2000
            };
        }

        public static Envelope Welcome(
            int bindingEpoch = 11,
            ulong streamVersion = 5,
            ulong projection = 10,
            uint heartbeatIntervalMs = 10_000,
            uint idleDeadMs = 30_000,
            int seatId = 0,
            WireRole role = WireRole.Player)
        {
            Envelope envelope = Envelope(MessageType.Welcome, e => e.Welcome = new Welcome
            {
                SessionId = SessionId,
                RoomId = RoomId,
                TableId = "table-1",
                Role = role,
                SeatId = seatId,
                ConnectionEpoch = 1,
                OwnerEpoch = 3,
                BindingEpoch = (ulong)bindingEpoch,
                Baseline = new WireStreamBaseline
                {
                    StateVersion = streamVersion,
                    EventSeq = 100 + streamVersion,
                    Cursor = new WireCursor { StreamVersion = streamVersion, ProjectionSeq = projection }
                },
                Scope = Scope(role: role),
                Limits = new ServerLimits
                {
                    MaxInboundMessageBytes = 1024 * 1024,
                    MaxCommandPayloadBytes = 64 * 1024,
                    MaxSnapshotBytes = 1024 * 1024,
                    MaxInFlightRequests = 64,
                    HeartbeatIntervalMs = heartbeatIntervalMs,
                    IdleDeadMs = idleDeadMs,
                    MaxEarlyBufferItems = 64,
                    MaxEarlyBufferBytes = 4 * 1024 * 1024
                },
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion
            });
            return Bound(envelope, bindingEpoch, seatId);
        }

        public static Envelope PlayerSync(int bindingEpoch, string correlationId, ulong streamVersion = 5, ulong projection = 10)
        {
            Envelope envelope = Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
            {
                Target = SyncTarget.PlayerStream,
                IsFullSnapshot = true,
                Player = PlayerSnapshot(streamVersion, projection),
                StateVersion = streamVersion,
                EventSeq = 100 + streamVersion
            });
            envelope.CorrelationId = correlationId;
            return Bound(envelope, bindingEpoch);
        }

        public static Envelope PlayerPush(
            int bindingEpoch,
            ulong fromStreamVersion,
            ulong fromProjection,
            ulong toStreamVersion,
            ulong toProjection,
            bool isFullSnapshot = false)
        {
            Envelope envelope = Envelope(MessageType.PlayerUpdate, e => e.PlayerUpdate = new PlayerUpdate
            {
                Snapshot = PlayerSnapshot(toStreamVersion, toProjection),
                FromCursor = new WireCursor { StreamVersion = fromStreamVersion, ProjectionSeq = fromProjection },
                Cursor = new WireCursor { StreamVersion = toStreamVersion, ProjectionSeq = toProjection },
                IsFullSnapshot = isFullSnapshot
            });
            return Bound(envelope, bindingEpoch);
        }

        public static Envelope HeartbeatPing(int bindingEpoch, string nonce, int seatId = 0)
        {
            Envelope envelope = Envelope(MessageType.Heartbeat, e => e.Heartbeat = new Heartbeat
            {
                Ping = new Ping { Nonce = nonce }
            });
            return Bound(envelope, bindingEpoch, seatId);
        }

        public static Envelope HeartbeatPong(int bindingEpoch, string nonce, int seatId = 0)
        {
            Envelope envelope = Envelope(MessageType.Heartbeat, e => e.Heartbeat = new Heartbeat
            {
                Pong = new Pong { Nonce = nonce }
            });
            return Bound(envelope, bindingEpoch, seatId);
        }

        public static Envelope Close(string code, bool permanent)
        {
            return Envelope(MessageType.Close, e => e.Close = new Close
            {
                Code = code,
                Permanent = permanent
            });
        }

        public static Envelope Error(string code, bool retryable)
        {
            return Envelope(MessageType.Error, e => e.Error = new ProtocolError
            {
                Code = code,
                Retryable = retryable
            });
        }

        public static PlayerSnapshot PlayerSnapshot(ulong streamVersion, ulong projection)
        {
            var snapshot = new PlayerSnapshot
            {
                SessionId = SessionId,
                RoundId = "round-1",
                Cursor = new WireCursor { StreamVersion = streamVersion, ProjectionSeq = projection },
                StateVersion = streamVersion,
                EventSeq = 100 + streamVersion,
                SessionVersion = 1,
                RoundVersion = 2
            };
            snapshot.PublicView = new WirePublicMatchView
            {
                Session = new WirePublicSessionSummary { Finished = false },
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

        public static byte[] Encode(Envelope envelope)
        {
            return WireCodec.Encode(envelope);
        }

        public static Envelope Decode(ReadOnlyMemory<byte> bytes)
        {
            WireDecodeResult result = WireCodec.Decode(bytes.Span);
            if (!result.IsOk || result.Envelope == null)
            {
                throw new InvalidOperationException("Test wire could not decode: " + result.Status + "/" + result.ErrorCode);
            }

            return result.Envelope;
        }

        public static ProtocolCommand SubmitActionCommand(
            string requestId = "request-1",
            string scopeId = "scope-1",
            ulong expectedStateVersion = 5)
        {
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                "round-1",
                "decision-1",
                7,
                ProtocolActionType.DiscardTile,
                tileIds: new uint[] { 5, 6 });
            return new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId(requestId),
                new RequestScopeId(scopeId),
                new StateVersion(expectedStateVersion),
                new RoundId("round-1"),
                new DecisionId("decision-1"),
                new ControlEpoch(7),
                new OwnerEpoch(3));
        }

        public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            if (!condition())
            {
                throw new TimeoutException("Timed out waiting for " + because + ".");
            }
        }

        public static byte[] RawBytes(Envelope envelope)
        {
            // Bypasses WireCodec.Encode validation for deliberately malformed
            // test messages (for example an invalid heartbeat nonce).
            return envelope.ToByteArray();
        }
    }
}
