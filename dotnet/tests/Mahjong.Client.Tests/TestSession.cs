using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;
using ContractCommandKind = Mahjong.Contracts.CommandKind;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using ContractScope = Mahjong.Contracts.RequestScope;
using ContractScopeId = Mahjong.Contracts.RequestScopeId;
using WireRole = Mahjong.Contracts.Wire.Role;
using WireScope = Mahjong.Contracts.Wire.RequestScope;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;
using WireStreamBaseline = Mahjong.Contracts.Wire.StreamBaseline;
using WirePublicMatchView = Mahjong.Contracts.Wire.PublicMatchView;
using WirePublicSessionSummary = Mahjong.Contracts.Wire.PublicSessionSummary;

namespace Mahjong.Client.Tests
{
    internal sealed class FakeTableChannel : ITableChannel
    {
        private readonly List<string>? _timeline;

        public FakeTableChannel(List<string>? timeline = null)
        {
            _timeline = timeline;
        }

        public bool IsOpen { get; private set; }

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public TableChannelConnectRequest? LastOpenRequest { get; private set; }

        public List<byte[]> Sent { get; } = new List<byte[]>();

        public Queue<byte[]> Inbound { get; } = new Queue<byte[]>();

        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            LastOpenRequest = request;
            OpenCount++;
            IsOpen = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            if (!IsOpen)
            {
                throw new TableChannelClosedException("TEST_CHANNEL_CLOSED", false);
            }

            Sent.Add(wireEnvelope.ToArray());
            _timeline?.Add("channel.send");
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (Inbound.Count == 0)
            {
                throw new TableChannelClosedException("TEST_NO_INBOUND", false);
            }

            return Task.FromResult<ReadOnlyMemory<byte>>(Inbound.Dequeue());
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            CloseCount++;
            IsOpen = false;
            return Task.CompletedTask;
        }

        public void Enqueue(Envelope envelope)
        {
            Inbound.Enqueue(WireCodec.Encode(envelope));
        }

        public IReadOnlyList<Envelope> SentEnvelopes()
        {
            var result = new List<Envelope>(Sent.Count);
            for (int i = 0; i < Sent.Count; i++)
            {
                WireDecodeResult decoded = WireCodec.Decode(Sent[i]);
                Assert.True(decoded.IsOk);
                result.Add(decoded.Envelope!);
            }

            return result;
        }

        public int CountSent(MessageType type)
        {
            return SentEnvelopes().Count(e => e.MessageType == type);
        }

        public Envelope LastSent(MessageType type)
        {
            return SentEnvelopes().Last(e => e.MessageType == type);
        }
    }

    internal sealed class RecordingOutboxStore : IOutboxStore
    {
        private readonly InMemoryOutboxStore _inner = new InMemoryOutboxStore();
        private readonly List<string>? _timeline;

        public RecordingOutboxStore(List<string>? timeline = null)
        {
            _timeline = timeline;
        }

        public bool ThrowOnSave { get; set; }

        public int SaveCount { get; private set; }

        public int ResolveCount { get; private set; }

        public async Task SaveAsync(OutboxEntry entry, CancellationToken cancellationToken)
        {
            if (ThrowOnSave)
            {
                throw new System.IO.IOException("simulated outbox write failure");
            }

            await _inner.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
            SaveCount++;
            _timeline?.Add("store.save");
        }

        public Task<IReadOnlyList<OutboxEntry>> LoadUnresolvedAsync(CancellationToken cancellationToken)
        {
            return _inner.LoadUnresolvedAsync(cancellationToken);
        }

        public async Task ResolveAsync(IdempotencyKey key, OutboxResolution resolution, CancellationToken cancellationToken)
        {
            await _inner.ResolveAsync(key, resolution, cancellationToken).ConfigureAwait(false);
            ResolveCount++;
            _timeline?.Add("store.resolve");
        }
    }

    internal static class TestSession
    {
        public const string SessionId = "session-1";
        public const string RoomId = "room-1";

        public static async Task<(TableSession Session, FakeTableChannel Channel, RecordingOutboxStore Store)> ConnectedAsync(
            int bindingEpoch = 11,
            string scopeId = "scope-1",
            ulong baselineStreamVersion = 5,
            ulong baselineProjection = 10,
            TableSessionOptions? options = null,
            RecordingOutboxStore? store = null,
            FakeTableChannel? channel = null,
            List<string>? timeline = null)
        {
            FakeTableChannel actualChannel = channel ?? new FakeTableChannel(timeline);
            RecordingOutboxStore actualStore = store ?? new RecordingOutboxStore(timeline);
            var session = new TableSession(actualChannel, actualStore, options);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None).ConfigureAwait(false);
            actualChannel.Enqueue(TestWire.Welcome(bindingEpoch, scopeId, baselineStreamVersion, baselineProjection));
            await session.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);

            actualChannel.Enqueue(TestWire.PlayerSync(bindingEpoch, baselineStreamVersion, baselineProjection));
            await session.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.Equal(TableConnectionState.Ready, session.State);
            return (session, actualChannel, actualStore);
        }

        public static async Task<(TableSession Session, FakeTableChannel Channel, RecordingOutboxStore Store)> HandshakeAsync(
            int bindingEpoch = 11,
            string scopeId = "scope-1",
            ulong baselineStreamVersion = 5,
            ulong baselineProjection = 10,
            TableSessionOptions? options = null,
            RecordingOutboxStore? store = null,
            FakeTableChannel? channel = null,
            List<string>? timeline = null)
        {
            FakeTableChannel actualChannel = channel ?? new FakeTableChannel(timeline);
            RecordingOutboxStore actualStore = store ?? new RecordingOutboxStore(timeline);
            var session = new TableSession(actualChannel, actualStore, options);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None).ConfigureAwait(false);
            actualChannel.Enqueue(TestWire.Welcome(bindingEpoch, scopeId, baselineStreamVersion, baselineProjection));
            await session.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.Equal(TableConnectionState.Synchronizing, session.State);
            return (session, actualChannel, actualStore);
        }

        public static async Task<(TableSession Session, FakeTableChannel Channel, RecordingOutboxStore Store)> ConnectedSpectatorAsync(
            int bindingEpoch = 11,
            string scopeId = "scope-1",
            ulong baselineStreamVersion = 5,
            ulong baselineProjection = 10,
            TableSessionOptions? options = null,
            RecordingOutboxStore? store = null,
            FakeTableChannel? channel = null,
            List<string>? timeline = null)
        {
            FakeTableChannel actualChannel = channel ?? new FakeTableChannel(timeline);
            RecordingOutboxStore actualStore = store ?? new RecordingOutboxStore(timeline);
            var session = new TableSession(actualChannel, actualStore, options);

            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None).ConfigureAwait(false);
            actualChannel.Enqueue(TestWire.Welcome(
                bindingEpoch,
                scopeId,
                baselineStreamVersion,
                baselineProjection,
                seatId: -1,
                role: WireRole.Spectator));
            await session.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            actualChannel.Enqueue(TestWire.PublicSync(bindingEpoch, baselineStreamVersion, baselineProjection));
            await session.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.Equal(TableConnectionState.Ready, session.State);
            return (session, actualChannel, actualStore);
        }

        public static async Task ReconnectAsync(
            TableSession session,
            FakeTableChannel channel,
            int bindingEpoch,
            string scopeId,
            ulong baselineStreamVersion,
            ulong baselineProjection)
        {
            await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            await session.OpenAsync(new TableSessionOpenRequest("ticket-1", "client-build-1"), CancellationToken.None).ConfigureAwait(false);
            channel.Enqueue(TestWire.Welcome(bindingEpoch, scopeId, baselineStreamVersion, baselineProjection));
            await session.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            channel.Enqueue(TestWire.PlayerSync(bindingEpoch, baselineStreamVersion, baselineProjection));
            await session.PumpOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.Equal(TableConnectionState.Ready, session.State);
        }
    }

    internal static class TestWire
    {
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

        public static Envelope Bound(Envelope envelope, int bindingEpoch, int seatId = 0, string sessionId = TestSession.SessionId, string roomId = TestSession.RoomId)
        {
            envelope.BindingEpoch = (ulong)bindingEpoch;
            envelope.SeatId = seatId;
            envelope.SessionId = sessionId;
            envelope.RoomId = roomId;
            return envelope;
        }

        public static WireScope Scope(string scopeId = "scope-1", string accountId = "account-1", string roomId = TestSession.RoomId, WireRole role = WireRole.Player)
        {
            return new WireScope
            {
                ScopeId = scopeId,
                AccountId = accountId,
                Role = role,
                RoomId = roomId,
                IssuedAtMs = 1000,
                ExpiresAtMs = 2000
            };
        }

        public static ContractScope ContractScope(string scopeId = "scope-1", string accountId = "account-1", string roomId = TestSession.RoomId)
        {
            return new ContractScope(
                new ContractScopeId(scopeId),
                accountId,
                ProtocolRole.Player,
                new RoomId(roomId),
                1000,
                2000);
        }

        public static Envelope Welcome(
            int bindingEpoch = 11,
            string scopeId = "scope-1",
            ulong streamVersion = 5,
            ulong projection = 10,
            int seatId = 0,
            WireRole role = WireRole.Player,
            uint maxEarlyItems = 64,
            ulong maxEarlyBytes = 4 * 1024 * 1024)
        {
            Envelope envelope = Envelope(MessageType.Welcome, e => e.Welcome = new Welcome
            {
                SessionId = TestSession.SessionId,
                RoomId = TestSession.RoomId,
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
                Scope = Scope(scopeId, role: role),
                Limits = new ServerLimits
                {
                    MaxInboundMessageBytes = 1024 * 1024,
                    MaxCommandPayloadBytes = 64 * 1024,
                    MaxSnapshotBytes = 1024 * 1024,
                    MaxInFlightRequests = 64,
                    HeartbeatIntervalMs = 10000,
                    IdleDeadMs = 30000,
                    MaxEarlyBufferItems = maxEarlyItems,
                    MaxEarlyBufferBytes = maxEarlyBytes
                },
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion
            });
            return Bound(envelope, bindingEpoch, seatId);
        }

        public static Envelope PlayerSync(int bindingEpoch, ulong streamVersion, ulong projection, int seatId = 0)
        {
            Envelope envelope = Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
            {
                Target = SyncTarget.PlayerStream,
                IsFullSnapshot = true,
                Player = PlayerSnapshot(streamVersion, projection),
                StateVersion = streamVersion,
                EventSeq = 100 + streamVersion
            });
            return Bound(envelope, bindingEpoch, seatId);
        }

        public static Envelope PlayerPush(
            int bindingEpoch,
            ulong fromStreamVersion,
            ulong fromProjection,
            ulong toStreamVersion,
            ulong toProjection,
            bool isFullSnapshot = false,
            int seatId = 0)
        {
            Envelope envelope = Envelope(MessageType.PlayerUpdate, e => e.PlayerUpdate = new PlayerUpdate
            {
                Snapshot = PlayerSnapshot(toStreamVersion, toProjection),
                FromCursor = new WireCursor { StreamVersion = fromStreamVersion, ProjectionSeq = fromProjection },
                Cursor = new WireCursor { StreamVersion = toStreamVersion, ProjectionSeq = toProjection },
                IsFullSnapshot = isFullSnapshot
            });
            return Bound(envelope, bindingEpoch, seatId);
        }

        public static Envelope PublicSync(int bindingEpoch, ulong streamVersion, ulong projection)
        {
            Envelope envelope = Envelope(MessageType.SyncResponse, e => e.SyncResponse = new SyncResponse
            {
                Target = SyncTarget.PublicStream,
                IsFullSnapshot = true,
                Public = PublicSnapshot(streamVersion, projection),
                StateVersion = streamVersion,
                EventSeq = 100 + streamVersion
            });
            return Bound(envelope, bindingEpoch, seatId: -1);
        }

        public static Envelope PublicPush(
            int bindingEpoch,
            ulong fromStreamVersion,
            ulong fromProjection,
            ulong toStreamVersion,
            ulong toProjection)
        {
            Envelope envelope = Envelope(MessageType.PublicUpdate, e => e.PublicUpdate = new PublicUpdate
            {
                Snapshot = PublicSnapshot(toStreamVersion, toProjection),
                FromCursor = new WireCursor { StreamVersion = fromStreamVersion, ProjectionSeq = fromProjection },
                Cursor = new WireCursor { StreamVersion = toStreamVersion, ProjectionSeq = toProjection },
                IsFullSnapshot = false
            });
            return Bound(envelope, bindingEpoch, seatId: -1);
        }

        public static Envelope Receipt(
            int bindingEpoch,
            string scopeId,
            string requestId,
            string digest,
            global::Mahjong.Contracts.Wire.ReceiptStatus status,
            ulong evaluatedStateVersion,
            ulong? commandVersion,
            ulong? eventSeq,
            string reasonCode = "")
        {
            var receipt = new global::Mahjong.Contracts.Wire.Receipt
            {
                Status = status,
                ReasonCode = reasonCode,
                EvaluatedStateVersion = evaluatedStateVersion,
                CanonicalPayloadSha256 = digest
            };
            if (commandVersion.HasValue)
            {
                receipt.CommandVersion = commandVersion.Value;
            }

            if (eventSeq.HasValue)
            {
                receipt.EventSeq = eventSeq.Value;
            }

            Envelope envelope = Envelope(MessageType.Receipt, e => e.Receipt = receipt);
            envelope.RequestId = requestId;
            envelope.Scope = Scope(scopeId);
            return Bound(envelope, bindingEpoch);
        }

        public static Envelope AppliedReceipt(int bindingEpoch, string scopeId, string requestId, string digest, ulong evaluatedStateVersion = 5)
        {
            return Receipt(
                bindingEpoch,
                scopeId,
                requestId,
                digest,
                global::Mahjong.Contracts.Wire.ReceiptStatus.Applied,
                evaluatedStateVersion,
                evaluatedStateVersion + 1,
                evaluatedStateVersion + 7);
        }

        public static Envelope ReceiptLookup(string correlationId, LookupStatus status, global::Mahjong.Contracts.Wire.Receipt? receipt = null, string digest = "")
        {
            Envelope envelope = Envelope(MessageType.ReceiptLookup, e => e.ReceiptLookup = new ReceiptLookup
            {
                Status = status,
                Receipt = receipt,
                CanonicalPayloadSha256 = digest
            });
            envelope.CorrelationId = correlationId;
            return envelope;
        }

        public static PublicSnapshot PublicSnapshot(ulong streamVersion, ulong projection)
        {
            var snapshot = new PublicSnapshot
            {
                SessionId = TestSession.SessionId,
                RoundId = "round-1",
                Cursor = new WireCursor { StreamVersion = streamVersion, ProjectionSeq = projection },
                StateVersion = streamVersion,
                EventSeq = 100 + streamVersion,
                SessionVersion = 1,
                RoundVersion = 2
            };
            snapshot.View = new WirePublicMatchView
            {
                Session = new WirePublicSessionSummary { Finished = false },
                Paused = false,
                SettlementRoundId = "round-1"
            };
            return snapshot;
        }

        public static PlayerSnapshot PlayerSnapshot(ulong streamVersion, ulong projection)
        {
            var snapshot = new PlayerSnapshot
            {
                SessionId = TestSession.SessionId,
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

        public static ProtocolCommand Command(string requestId = "request-1", string scopeId = "scope-1", ulong expectedStateVersion = 5)
        {
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                "round-1",
                "decision-1",
                7,
                ProtocolActionType.DiscardTile,
                tileIds: new uint[] { 5, 6 });
            return new ProtocolCommand(
                ContractCommandKind.SubmitAction,
                payload,
                new RequestId(requestId),
                new ContractScopeId(scopeId),
                new StateVersion(expectedStateVersion),
                new RoundId("round-1"),
                new DecisionId("decision-1"),
                new ControlEpoch(7),
                new OwnerEpoch(3));
        }

        public static Envelope OutboundReceiptQuery(FakeTableChannel channel, string requestId)
        {
            return channel.SentEnvelopes().Last(e =>
                e.BodyCase == global::Mahjong.Contracts.Wire.Envelope.BodyOneofCase.ReceiptQuery &&
                e.ReceiptQuery.RequestId == requestId);
        }

        public static byte[] PayloadBytes(ProtocolCommand command)
        {
            return command.Payload.CopyBytes();
        }
    }
}
