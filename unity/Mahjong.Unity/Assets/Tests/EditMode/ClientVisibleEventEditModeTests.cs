#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
using NUnit.Framework;
using WireDiscardView = Mahjong.Contracts.Wire.DiscardView;
using WirePlayerPublicView = Mahjong.Contracts.Wire.PlayerPublicView;
using WirePublicBoardView = Mahjong.Contracts.Wire.PublicBoardView;
using WirePublicMatchView = Mahjong.Contracts.Wire.PublicMatchView;
using WirePublicSessionSummary = Mahjong.Contracts.Wire.PublicSessionSummary;
using WireRequestScope = Mahjong.Contracts.Wire.RequestScope;
using WireScoreView = Mahjong.Contracts.Wire.ScoreView;
using WireStreamBaseline = Mahjong.Contracts.Wire.StreamBaseline;
using WireStreamCursor = Mahjong.Contracts.Wire.StreamCursor;

namespace Mahjong.Unity.Tests.EditMode
{
    /// <summary>
    /// EditMode proof for the B2b production path that does not depend on a
    /// scene: real TableSession + real WireCodec frames -> exact projection ->
    /// pure ClientVisibleEventMapper -> ConfirmedAnimationQueue. PlayMode covers
    /// the same path through SessionCoordinator and the real TablePresenter.
    /// </summary>
    public sealed class ClientVisibleEventEditModeTests
    {
        private const string SessionId = "session_b2b";
        private const string RoomId = "room_b2b";
        private const string RoundId = "round_b2b";
        private const string ScopeId = "scope_b2b";
        private const string AccountId = "account_b2b";
        private const ulong BindingEpoch = 11;

        [Test]
        public async Task RealWireBatchMapsExactlyAndEnqueuesOneConfirmedDiscard()
        {
            using var channel = new ScriptedTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            TableSnapshotChangedEventArgs? baseline = null;
            TableSnapshotChangedEventArgs? accepted = null;
            session.SnapshotChanged += (_, args) =>
            {
                if (args.IsFullBaseline && baseline == null)
                {
                    baseline = args;
                }
                else
                {
                    accepted = args;
                }
            };

            await session.OpenAsync(new TableSessionOpenRequest("ticket_b2b", "build_b2b"), CancellationToken.None);
            channel.EnqueueInbound(WireCodec.Encode(Welcome()));
            await session.PumpOnceAsync(CancellationToken.None);

            string? correlationId = null;
            IReadOnlyList<byte[]> outbound = channel.OutboundMessages;
            for (int i = outbound.Count - 1; i >= 0; i--)
            {
                WireDecodeResult decoded = WireCodec.Decode(outbound[i]);
                if (decoded.IsOk && decoded.Envelope?.BodyCase == Envelope.BodyOneofCase.SyncRequest)
                {
                    correlationId = decoded.Envelope.CorrelationId;
                    break;
                }
            }

            Assert.IsNotNull(correlationId, "the real session must request a baseline after WELCOME");
            channel.EnqueueInbound(WireCodec.Encode(SyncResponse(correlationId!)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.AreEqual(TableConnectionState.Ready, session.State);
            Assert.IsNotNull(baseline);

            PlayerSnapshot snapshot = PlayerSnapshot(stateVersion: 6, eventSeq: 106, projectionSeq: 11);
            snapshot.PublicView.Board.Players[1].Discards.Add(new WireDiscardView
            {
                TileId = 44,
                TurnNumber = 2,
                Tsumogiri = false
            });
            channel.EnqueueInbound(WireCodec.Encode(Push(snapshot, 10, 11, false,
                new EventEnvelope
                {
                    EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                    EventSeq = 106,
                    CommandVersion = 6,
                    EventOrdinal = 0,
                    EventType = EventType.TileDiscarded,
                    Visibility = EventVisibility.Public,
                    TileDiscarded = new TileDiscarded { TurnNumber = 2, Player = 1, TileId = 44 }
                })));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.IsNotNull(accepted);
            Assert.AreEqual(TableVisibleEventBatchKind.Incremental, accepted!.VisibleEventBatch.Kind);

            var clock = new SystemTableClientClock();
            var projection = new PlayerTableProjectionMapper(clock);
            var baselineView = projection.Project(View(session, baseline!));
            PlayerTableViewState view = projection.Project(View(session, accepted));
            var mapping = new ClientVisibleEventMapper().Map(accepted.VisibleEventBatch, view, 1);
            Assert.IsTrue(mapping.Accepted, mapping.Code);
            Assert.AreEqual(1, mapping.AnimatedEvents.Count);
            Assert.AreEqual(ConfirmedVisibleEventKind.TileDiscarded, mapping.AnimatedEvents[0].Kind);

            var queue = new ConfirmedAnimationQueue();
            Assert.AreEqual(TableAnimationCodes.BaselineReset, queue.AcceptConfirmedBaseline(baselineView).Code);
            TableAnimationResult result = queue.AcceptConfirmedIncremental(
                baselineView,
                view,
                mapping.Context!,
                mapping.AnimatedEvents,
                TableAnimationAudience.Player(0));
            Assert.IsTrue(result.Accepted, result.Code);
            Assert.AreEqual(1, result.Enqueued);
            Assert.IsTrue(queue.TryDequeue(out TableAnimationItem? item));
            Assert.AreEqual(TableAnimationKind.Discard, item!.Kind);
            Assert.AreEqual(1, item.Seat);
            Assert.AreEqual(44U, item.TileId);
            Assert.AreEqual(TableAnimationSource.VisibleEvent, item.Source);
        }

        private static SessionViewState View(TableSession session, TableSnapshotChangedEventArgs args)
        {
            return new SessionViewState(
                1,
                TableConnectionState.Ready,
                session.Binding,
                args.PlayerSnapshot?.Clone(),
                args.PublicSnapshot?.Clone(),
                args.RoomSnapshot?.Clone(),
                null,
                session.LastError,
                args.IsFullBaseline,
                args.Cursor,
                args.VisibleEventBatch);
        }

        private static Envelope Welcome()
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Welcome,
                SessionId = SessionId,
                RoomId = RoomId,
                SeatId = 0,
                BindingEpoch = BindingEpoch,
                Welcome = new Welcome
                {
                    SessionId = SessionId,
                    RoomId = RoomId,
                    TableId = "table_b2b",
                    Role = Role.Player,
                    SeatId = 0,
                    ConnectionEpoch = 1,
                    OwnerEpoch = 3,
                    BindingEpoch = BindingEpoch,
                    Baseline = new WireStreamBaseline
                    {
                        StateVersion = 5,
                        EventSeq = 105,
                        Cursor = new WireStreamCursor { StreamVersion = 5, ProjectionSeq = 10 }
                    },
                    Scope = new WireRequestScope
                    {
                        ScopeId = ScopeId,
                        AccountId = AccountId,
                        Role = Role.Player,
                        RoomId = RoomId,
                        IssuedAtMs = 1000,
                        ExpiresAtMs = 2000
                    },
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
                }
            };
        }

        private static Envelope SyncResponse(string correlationId)
        {
            PlayerSnapshot snapshot = PlayerSnapshot(stateVersion: 5, eventSeq: 105, projectionSeq: 10);
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.SyncResponse,
                SessionId = SessionId,
                RoomId = RoomId,
                SeatId = 0,
                BindingEpoch = BindingEpoch,
                CorrelationId = correlationId,
                SyncResponse = new SyncResponse
                {
                    Target = SyncTarget.PlayerStream,
                    IsFullSnapshot = true,
                    Player = snapshot,
                    StateVersion = 5,
                    EventSeq = 105
                }
            };
        }

        private static PlayerSnapshot PlayerSnapshot(ulong stateVersion, ulong eventSeq, ulong projectionSeq)
        {
            var snapshot = new PlayerSnapshot
            {
                SessionId = SessionId,
                RoundId = RoundId,
                StateVersion = stateVersion,
                EventSeq = eventSeq,
                Cursor = new WireStreamCursor { StreamVersion = 5, ProjectionSeq = projectionSeq },
                PublicView = new WirePublicMatchView
                {
                    Board = new WirePublicBoardView
                    {
                        Phase = GamePhase.Discard,
                        RoundWind = 0,
                        RoundNumber = 1,
                        Honba = 0,
                        RiichiSticks = 0,
                        DealerSeat = 0,
                        CurrentSeat = 0,
                        WallRemaining = 68
                    },
                    Session = new WirePublicSessionSummary { Finished = false },
                    SettlementRoundId = RoundId
                },
                PrivateView = new PlayerPrivateView { Seat = 0, Furiten = false }
            };

            snapshot.PrivateView.ClosedTiles.Add(1);
            snapshot.PrivateView.ClosedTiles.Add(2);
            snapshot.PrivateView.DrawnTileId = 26;
            snapshot.PrivateView.SleeveTiles.Add(124);
            for (int seat = 0; seat < 4; seat++)
            {
                snapshot.PublicView.Board.Players.Add(new WirePlayerPublicView
                {
                    Seat = seat,
                    HandCount = 13,
                    Riichi = false
                });
                snapshot.PublicView.Board.Scores.Add(new WireScoreView { Seat = seat, Score = 25000 });
            }

            snapshot.PublicView.Board.Players[1].Discards.Add(new WireDiscardView
            {
                TileId = 36,
                TurnNumber = 1,
                Tsumogiri = true
            });
            return snapshot;
        }

        private static Envelope Push(
            PlayerSnapshot snapshot,
            ulong fromProjection,
            ulong toProjection,
            bool isFullSnapshot,
            params EventEnvelope[] events)
        {
            var update = new PlayerUpdate
            {
                Snapshot = snapshot,
                FromCursor = isFullSnapshot
                    ? null
                    : new WireStreamCursor { StreamVersion = 5, ProjectionSeq = fromProjection },
                Cursor = new WireStreamCursor { StreamVersion = 5, ProjectionSeq = toProjection },
                IsFullSnapshot = isFullSnapshot
            };
            for (int i = 0; i < events.Length; i++)
            {
                update.VisibleEvents.Add(events[i]);
            }

            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.PlayerUpdate,
                SessionId = SessionId,
                RoomId = RoomId,
                SeatId = 0,
                BindingEpoch = BindingEpoch,
                PlayerUpdate = update
            };
        }
    }
}
