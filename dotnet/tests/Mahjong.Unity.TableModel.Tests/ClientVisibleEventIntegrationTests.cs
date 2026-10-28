using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;
using Xunit;
using WireDiscard = Mahjong.Contracts.Wire.DiscardView;
using WireMeld = Mahjong.Contracts.Wire.MeldView;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;
using WireScore = Mahjong.Contracts.Wire.ScoreView;
using WirePayment = Mahjong.Contracts.Wire.PaymentView;

namespace Mahjong.Unity.TableModel.Tests
{
    /// <summary>
    /// Directed B2b tests use the real TableSession, real WireCodec frames and
    /// the real source-linked table model. They observe the actual animation
    /// queue items, not only non-null properties or counts.
    /// </summary>
    public sealed class ClientVisibleEventIntegrationTests
    {
        private sealed class Captured
        {
            public Captured(TableSnapshotChangedEventArgs args)
            {
                Args = args;
            }

            public TableSnapshotChangedEventArgs Args { get; }
        }

        private static async Task<(TableSession Session, ScriptedTableChannel Channel, List<Captured> Captures)> ReadyAsync()
        {
            var channel = new ScriptedTableChannel();
            var session = new TableSession(channel, new InMemoryOutboxStore());
            var captures = new List<Captured>();
            session.SnapshotChanged += (_, args) => captures.Add(new Captured(args));
            var queue = new TableSessionOperationQueue(session);
            await queue.OpenAsync(new TableSessionOpenRequest("ticket-b2b", "build-b2b"), CancellationToken.None);
            await queue.PumpOnceAsync(CancellationToken.None);
            await queue.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(TableConnectionState.Ready, session.State);
            Assert.NotEmpty(captures);
            return (session, channel, captures);
        }

        private static SessionViewState View(TableSession session, Captured captured)
        {
            return new SessionViewState(
                1,
                TableConnectionState.Ready,
                session.Binding,
                captured.Args.PlayerSnapshot?.Clone(),
                captured.Args.PublicSnapshot?.Clone(),
                captured.Args.RoomSnapshot?.Clone(),
                null,
                session.LastError,
                captured.Args.IsFullBaseline,
                captured.Args.Cursor,
                captured.Args.VisibleEventBatch);
        }

        private static Envelope PlayerPush(
            int seat,
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
                    : new WireCursor { StreamVersion = 5, ProjectionSeq = fromProjection },
                Cursor = new WireCursor { StreamVersion = 5, ProjectionSeq = toProjection },
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
                SessionId = P43TestData.SessionId,
                RoomId = P43TestData.RoomId,
                SeatId = seat,
                BindingEpoch = P43TestData.BindingEpoch,
                PlayerUpdate = update
            };
        }

        private static EventEnvelope Event(
            ulong eventSeq,
            uint ordinal,
            EventType type,
            EventVisibility visibility,
            Action<EventEnvelope> body,
            ulong commandVersion = 200)
        {
            var envelope = new EventEnvelope
            {
                EventSchemaVersion = ProtocolConstants.EventSchemaVersion,
                EventSeq = eventSeq,
                CommandVersion = commandVersion,
                EventOrdinal = ordinal,
                EventType = type,
                Visibility = visibility
            };
            body(envelope);
            return envelope;
        }

        private static PlayerSnapshot MatrixSnapshot()
        {
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot(
                stateVersion: 200,
                eventSeq: 106,
                streamVersion: 5,
                projectionSeq: 11);
            snapshot.PublicView.Board.Players[1].Discards.Add(new WireDiscard
            {
                TileId = 44,
                TurnNumber = 4,
                RiichiDiscard = false,
                Tsumogiri = false
            });
            snapshot.PublicView.Board.Players[2].Discards.Add(new WireDiscard
            {
                TileId = 45,
                TurnNumber = 5,
                RiichiDiscard = true,
                Tsumogiri = false
            });
            snapshot.PublicView.Board.Players[2].Riichi = true;
            snapshot.PrivateView.DrawnTileId = 21;
            snapshot.PrivateView.ClosedTiles.Add(25);
            snapshot.PublicView.Board.Players[0].HandCount = 14;
            return snapshot;
        }

        private static EventEnvelope[] MatrixEvents()
        {
            var events = new List<EventEnvelope>();
            uint ordinal = 0;
            events.Add(Event(106, ordinal++, EventType.StackingStarted, EventVisibility.Public,
                e => e.StackingStarted = new StackingStarted { TurnNumber = 1 }));
            events.Add(Event(106, ordinal++, EventType.WallBuilt, EventVisibility.Public,
                e => e.WallBuilt = new WallBuilt { TurnNumber = 1, WallSize = 136 }));
            events.Add(Event(106, ordinal++, EventType.TileDealt, EventVisibility.Private,
                e => e.TileDealt = new TileDealt { TurnNumber = 1, Player = 0, TileId = 13 }));
            events.Add(Event(106, ordinal++, EventType.DealingComplete, EventVisibility.Public,
                e => e.DealingComplete = new DealingComplete { TurnNumber = 1 }));
            events.Add(Event(106, ordinal++, EventType.TileDrawn, EventVisibility.Private,
                e => e.TileDrawn = new TileDrawn { TurnNumber = 2, Player = 0, TileId = 21, WallRemaining = 50 }));
            events.Add(Event(106, ordinal++, EventType.TileDiscarded, EventVisibility.Public,
                e => e.TileDiscarded = new TileDiscarded { TurnNumber = 4, Player = 1, TileId = 44 }));
            events.Add(Event(106, ordinal++, EventType.ChiDeclared, EventVisibility.Public,
                e => e.ChiDeclared = new ChiDeclared
                {
                    TurnNumber = 5,
                    Player = 2,
                    FromPlayer = 1,
                    Meld = new WireMeld { Type = MeldType.Chi, CalledTileId = 0, FromSeat = 1, TileIds = { 0, 4, 8 } }
                }));
            events.Add(Event(106, ordinal++, EventType.PonDeclared, EventVisibility.Public,
                e => e.PonDeclared = new PonDeclared
                {
                    TurnNumber = 5,
                    Player = 2,
                    FromPlayer = 1,
                    Meld = new WireMeld { Type = MeldType.Pon, CalledTileId = 12, FromSeat = 1, TileIds = { 12, 13, 14 } }
                }));
            events.Add(Event(106, ordinal++, EventType.KanDeclared, EventVisibility.Public,
                e => e.KanDeclared = new KanDeclared
                {
                    TurnNumber = 5,
                    Player = 3,
                    KanType = KanType.Open,
                    Meld = new WireMeld { Type = MeldType.OpenKan, CalledTileId = 20, FromSeat = 2, TileIds = { 20, 21, 22, 23 } }
                }));
            events.Add(Event(106, ordinal++, EventType.KanDeclared, EventVisibility.Public,
                e => e.KanDeclared = new KanDeclared
                {
                    TurnNumber = 5,
                    Player = 1,
                    KanType = KanType.Closed
                }));
            events.Add(Event(106, ordinal++, EventType.RinshanDraw, EventVisibility.Private,
                e => e.RinshanDraw = new RinshanDraw { TurnNumber = 5, Player = 0, TileId = 24 }));
            events.Add(Event(106, ordinal++, EventType.RiichiDeclared, EventVisibility.Public,
                e => e.RiichiDeclared = new RiichiDeclared
                {
                    TurnNumber = 5,
                    Player = 2,
                    DiscardTileId = 45,
                    DoubleRiichi = false
                }));
            events.Add(Event(106, ordinal++, EventType.TsumoWin, EventVisibility.SettlementPublic,
                e => e.TsumoWin = new TsumoWin
                {
                    TurnNumber = 6,
                    Player = 2,
                    WinTileId = 45,
                    Han = 1,
                    Fu = 30,
                    Score = 1000
                }));
            events.Add(Event(106, ordinal++, EventType.RonWin, EventVisibility.SettlementPublic,
                e => e.RonWin = new RonWin
                {
                    TurnNumber = 6,
                    Winner = 3,
                    Loser = 1,
                    WinTileId = 46,
                    Han = 1,
                    Fu = 30,
                    Score = 1000,
                    IsChankan = false
                }));
            events.Add(Event(106, ordinal++, EventType.DoraRevealed, EventVisibility.Public,
                e => e.DoraRevealed = new DoraRevealed
                {
                    TurnNumber = 6,
                    IndicatorTileId = 50,
                    IsUraDora = false,
                    UraAuthorized = false
                }));
            events.Add(Event(106, ordinal++, EventType.CheatPointsChanged, EventVisibility.Private,
                e => e.CheatPointsChanged = new CheatPointsChanged
                {
                    TurnNumber = 6,
                    Seat = 0,
                    Amount = -1,
                    NewBalance = 4
                }));
            events.Add(Event(106, ordinal++, EventType.DrawGame, EventVisibility.Public,
                e => e.DrawGame = new DrawGame
                {
                    TurnNumber = 7,
                    Reason = "NINE_TYPES",
                    TenpaiPlayers = { 0, 1 }
                }));
            events.Add(Event(106, ordinal++, EventType.ScoreSettled, EventVisibility.SettlementPublic,
                e =>
                {
                    e.ScoreSettled = new ScoreSettled
                    {
                        TurnNumber = 7,
                        Payments = { new WirePayment { FromSeat = 1, ToSeat = 2, Amount = 1000 } },
                        NewScores =
                        {
                            new WireScore { Seat = 0, Score = 25000 },
                            new WireScore { Seat = 1, Score = 24000 },
                            new WireScore { Seat = 2, Score = 26000 },
                            new WireScore { Seat = 3, Score = 25000 }
                        }
                    };
                }));
            events.Add(Event(106, ordinal++, EventType.RoundEnded, EventVisibility.Public,
                e => e.RoundEnded = new RoundEnded { TurnNumber = 7, RoundWind = 0, RoundNumber = 1 }));
            events.Add(Event(106, ordinal++, EventType.MatchEnded, EventVisibility.SettlementPublic,
                e =>
                {
                    e.MatchEnded = new MatchEnded
                    {
                        TurnNumber = 7,
                        Winner = 0,
                        FinalScores =
                        {
                            new WireScore { Seat = 0, Score = 30000 },
                            new WireScore { Seat = 1, Score = 20000 },
                            new WireScore { Seat = 2, Score = 25000 },
                            new WireScore { Seat = 3, Score = 25000 }
                        }
                    };
                }));
            events.Add(Event(106, ordinal++, EventType.SleeveBloomResolved, EventVisibility.Private,
                e => e.SleeveBloomResolved = new SleeveBloomResolved
                {
                    OwnerSeat = 0,
                    OfferId = "offer-1",
                    KeptFirst = true,
                    ResultTileId = 13
                }));
            events.Add(Event(106, ordinal++, EventType.BeamSwapResolved, EventVisibility.Private,
                e => e.BeamSwapResolved = new BeamSwapResolved
                {
                    OwnerSeat = 0,
                    OfferId = "beam-1",
                    Action = "SWAP",
                    HandTileId = 13,
                    TargetSlot = 4
                }));
            return events.ToArray();
        }

        [Fact]
        public async Task RealWireBatchMapsEveryEventFamilyAndKeepsStateOnlyEventsOutOfAnimation()
        {
            (TableSession session, ScriptedTableChannel channel, List<Captured> captures) = await ReadyAsync();
            Assert.True(captures[0].Args.VisibleEventBatch.IsFullBaseline);

            PlayerSnapshot snapshot = MatrixSnapshot();
            channel.EnqueueInbound(P43TestData.Encode(PlayerPush(
                0,
                snapshot,
                fromProjection: 10,
                toProjection: 11,
                isFullSnapshot: false,
                MatrixEvents())));
            await session.PumpOnceAsync(CancellationToken.None);

            Assert.Equal(2, captures.Count);
            TableSnapshotChangedEventArgs args = captures[1].Args;
            Assert.False(args.IsFullBaseline);
            Assert.Equal(TableVisibleEventBatchKind.Incremental, args.VisibleEventBatch.Kind);
            Assert.Equal(22, args.VisibleEventBatch.Events.Count);

            var clock = new ManualTableClock();
            var mapper = new PlayerTableProjectionMapper(clock);
            PlayerTableViewState baselineView = mapper.Project(View(session, captures[0]));
            PlayerTableViewState view = mapper.Project(View(session, captures[1]));
            var mapping = new ClientVisibleEventMapper().Map(args.VisibleEventBatch, view, 1);

            Assert.True(mapping.Accepted, mapping.Code);
            Assert.Equal(18, mapping.AnimatedEvents.Count);
            Assert.Equal(4, mapping.StateOnlyEvents.Count);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.TileDealt);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.TileDrawn);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.RinshanDraw);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.TileDiscarded);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.ChiDeclared);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.PonDeclared);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.KanDeclared);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.RiichiDeclared);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.TsumoWin);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.RonWin);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.DoraRevealed);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.CheatPointsChanged);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.DrawGame);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.ScoreSettled);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.RoundEnded);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.MatchEnded);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.SleeveBloomResolved);
            Assert.Contains(mapping.AnimatedEvents, e => e.Kind == ConfirmedVisibleEventKind.BeamSwapResolved);
            Assert.Contains(mapping.StateOnlyEvents, e => e.ReasonCode == ClientVisibleEventStateOnlyCodes.NoAnimation);
            Assert.Contains(mapping.StateOnlyEvents, e => e.ReasonCode == ClientVisibleEventStateOnlyCodes.RedactedClosedKan);
            Assert.DoesNotContain(mapping.AnimatedEvents, e => e.Visibility == ConfirmedEventVisibility.Private && e.OwnerSeat != 0);

            var queue = new ConfirmedAnimationQueue();
            Assert.Equal(TableAnimationCodes.BaselineReset, queue.AcceptConfirmedBaseline(baselineView).Code);
            TableAnimationResult result = queue.AcceptConfirmedIncremental(
                baselineView,
                view,
                mapping.Context!,
                mapping.AnimatedEvents,
                TableAnimationAudience.Player(0));

            Assert.True(result.Accepted, result.Code);
            Assert.Equal(18, result.Enqueued);
            Assert.Equal(18, queue.PendingCount);
            var drained = queue.Drain();
            Assert.Contains(drained, item => item.Kind == TableAnimationKind.Deal && item.IsPrivate && item.OwnerSeat == 0);
            Assert.Contains(drained, item => item.Kind == TableAnimationKind.Draw && item.IsPrivate && item.TileId == 21);
            Assert.Contains(drained, item => item.Kind == TableAnimationKind.Meld && item.Seat == 2);
            Assert.Contains(drained, item => item.Kind == TableAnimationKind.Win && item.Seat == 2);
            Assert.Contains(drained, item => item.Kind == TableAnimationKind.CheatPoints && item.IsPrivate);
            Assert.Contains(drained, item => item.Kind == TableAnimationKind.SleeveBloomResolved && item.IsPrivate);
            Assert.Contains(drained, item => item.Kind == TableAnimationKind.BeamSwapResolved && item.IsPrivate);
            Assert.DoesNotContain(drained, item => item.Kind == TableAnimationKind.Unknown);
            Assert.DoesNotContain(drained, item => item.Seat == 1 && item.Kind == TableAnimationKind.Meld);
        }

        [Fact]
        public async Task SameSnapshotAndTypedEventsProduceOneAnimationPerActionAndRewriteSparseEvents()
        {
            (TableSession session, ScriptedTableChannel channel, List<Captured> captures) = await ReadyAsync();
            var clock = new ManualTableClock();
            var mapper = new PlayerTableProjectionMapper(clock);
            var queue = new ConfirmedAnimationQueue();
            PlayerTableViewState baseline = mapper.Project(View(session, captures[0]));
            Assert.Equal(TableAnimationCodes.BaselineReset, queue.AcceptConfirmedBaseline(baseline).Code);

            PlayerSnapshot first = P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                streamVersion: 5,
                projectionSeq: 11);
            first.PublicView.Board.Players[1].Discards.Add(new WireDiscard
            {
                TileId = 44,
                TurnNumber = 4,
                Tsumogiri = false
            });
            first.PrivateView.DrawnTileId = 21;
            first.PrivateView.ClosedTiles.Add(25);
            first.PublicView.Board.Players[0].HandCount = 14;

            var firstEvents = new[]
            {
                Event(106, 0, EventType.TileDiscarded, EventVisibility.Public,
                    e => e.TileDiscarded = new TileDiscarded { TurnNumber = 4, Player = 1, TileId = 44 },
                    commandVersion: 6),
                Event(106, 1, EventType.TileDrawn, EventVisibility.Private,
                    e => e.TileDrawn = new TileDrawn { TurnNumber = 4, Player = 0, TileId = 21, WallRemaining = 50 },
                    commandVersion: 6)
            };
            channel.EnqueueInbound(P43TestData.Encode(PlayerPush(0, first, 10, 11, false, firstEvents)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(2, captures.Count);

            PlayerTableViewState firstView = mapper.Project(View(session, captures[1]));
            var firstMapping = new ClientVisibleEventMapper().Map(captures[1].Args.VisibleEventBatch, firstView, 1);
            Assert.True(firstMapping.Accepted, firstMapping.Code);
            Assert.Equal(2, firstMapping.AnimatedEvents.Count);
            TableAnimationResult firstResult = queue.AcceptConfirmedIncremental(
                baseline,
                firstView,
                firstMapping.Context!,
                firstMapping.AnimatedEvents,
                TableAnimationAudience.Player(0));
            Assert.True(firstResult.Accepted, firstResult.Code);
            Assert.Equal(2, firstResult.Enqueued);
            Assert.Equal(2, queue.PendingCount);

            // The snapshot also contains the same discard and drawn tile. The
            // typed batch path must not add a second snapshot-difference copy.
            var firstItems = queue.Drain();
            Assert.Single(firstItems, item => item.Kind == TableAnimationKind.Discard && item.TileId == 44);
            Assert.Single(firstItems, item => item.Kind == TableAnimationKind.Draw && item.TileId == 21);

            // A later sparse event sequence is legal without filling 107/108.
            PlayerSnapshot second = first.Clone();
            second.StateVersion = 7;
            second.EventSeq = 109;
            second.Cursor = new WireCursor { StreamVersion = 5, ProjectionSeq = 12 };
            second.PublicView.Board.Players[2].Discards.Add(new WireDiscard
            {
                TileId = 46,
                TurnNumber = 5,
                Tsumogiri = false
            });
            var sparseEvent = Event(109, 0, EventType.RiichiDeclared, EventVisibility.Public,
                e => e.RiichiDeclared = new RiichiDeclared { TurnNumber = 5, Player = 2, DiscardTileId = 46 },
                commandVersion: 7);
            channel.EnqueueInbound(P43TestData.Encode(PlayerPush(0, second, 11, 12, false, sparseEvent)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(3, captures.Count);

            PlayerTableViewState secondView = mapper.Project(View(session, captures[2]));
            var secondMapping = new ClientVisibleEventMapper().Map(captures[2].Args.VisibleEventBatch, secondView, 1);
            Assert.True(secondMapping.Accepted, secondMapping.Code);
            TableAnimationResult secondResult = queue.AcceptConfirmedIncremental(
                firstView,
                secondView,
                secondMapping.Context!,
                secondMapping.AnimatedEvents,
                TableAnimationAudience.Player(0));
            Assert.True(secondResult.Accepted, secondResult.Code);
            var secondItems = queue.Drain();
            TableAnimationItem only = Assert.Single(secondItems);
            Assert.Equal(TableAnimationKind.Riichi, only.Kind);
            Assert.Equal(109UL, only.Context.EventSeq);
        }

        [Fact]
        public async Task FullBaselineAndRoundResetClearAnimationWithoutReplayingTheirEvents()
        {
            (TableSession session, ScriptedTableChannel channel, List<Captured> captures) = await ReadyAsync();
            var clock = new ManualTableClock();
            var mapper = new PlayerTableProjectionMapper(clock);
            var queue = new ConfirmedAnimationQueue();
            PlayerTableViewState baseline = mapper.Project(View(session, captures[0]));
            queue.AcceptConfirmedBaseline(baseline);

            PlayerSnapshot reset = P43TestData.PlayerSnapshot(
                stateVersion: 20,
                eventSeq: 120,
                streamVersion: 5,
                projectionSeq: 11,
                roundId: "round-next");
            reset.PublicView.Board.Players[1].Discards.Add(new WireDiscard
            {
                TileId = 44,
                TurnNumber = 20,
                Tsumogiri = false
            });
            var oldEvent = Event(120, 0, EventType.TileDiscarded, EventVisibility.Public,
                e => e.TileDiscarded = new TileDiscarded { TurnNumber = 20, Player = 1, TileId = 44 },
                commandVersion: 20);
            channel.EnqueueInbound(P43TestData.Encode(PlayerPush(0, reset, 10, 11, true, oldEvent)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(2, captures.Count);

            TableSnapshotChangedEventArgs args = captures[1].Args;
            Assert.True(args.IsFullBaseline);
            Assert.Equal(TableVisibleEventBatchKind.Baseline, args.VisibleEventBatch.Kind);
            Assert.Single(args.VisibleEventBatch.Events);
            Assert.Empty(args.VisibleEventBatch.PlayableEvents);

            PlayerTableViewState resetView = mapper.Project(View(session, captures[1]));
            var mapping = new ClientVisibleEventMapper().Map(args.VisibleEventBatch, resetView, 1);
            Assert.True(mapping.Accepted, mapping.Code);
            Assert.True(mapping.IsBaseline);
            Assert.Empty(mapping.AnimatedEvents);
            Assert.Equal(TableAnimationCodes.BaselineReset, queue.AcceptConfirmedBaseline(resetView).Code);
            Assert.Equal(0, queue.PendingCount);

            // A baseline at event watermark 120 makes a later event at or below
            // 120 historical, never a fresh animation.
            PlayerSnapshot stale = reset.Clone();
            stale.StateVersion = 21;
            stale.Cursor = new WireCursor { StreamVersion = 5, ProjectionSeq = 12 };
            var staleEvent = Event(120, 0, EventType.TileDiscarded, EventVisibility.Public,
                e => e.TileDiscarded = new TileDiscarded { TurnNumber = 21, Player = 2, TileId = 47 },
                commandVersion: 21);
            channel.EnqueueInbound(P43TestData.Encode(PlayerPush(0, stale, 11, 12, false, staleEvent)));
            await session.PumpOnceAsync(CancellationToken.None);
            Assert.Equal(3, captures.Count);
            Assert.Empty(captures[2].Args.VisibleEventBatch.PlayableEvents);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public async Task MapperRejectsRetargetedBindingAndPrivateRevocationWithoutQueueMutation()
        {
            (TableSession session, ScriptedTableChannel channel, List<Captured> captures) = await ReadyAsync();
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot(
                stateVersion: 6,
                eventSeq: 106,
                streamVersion: 5,
                projectionSeq: 11);
            var privateEvent = Event(106, 0, EventType.TileDrawn, EventVisibility.Private,
                e => e.TileDrawn = new TileDrawn { TurnNumber = 4, Player = 0, TileId = 21, WallRemaining = 50 },
                commandVersion: 6);
            channel.EnqueueInbound(P43TestData.Encode(PlayerPush(0, snapshot, 10, 11, false, privateEvent)));
            await session.PumpOnceAsync(CancellationToken.None);

            TableSnapshotChangedEventArgs args = captures[1].Args;
            var clock = new ManualTableClock();
            var mapper = new PlayerTableProjectionMapper(clock);
            PlayerTableViewState matching = mapper.Project(View(session, captures[1]));

            // Rebinding the same accepted batch to a later epoch must not let
            // its events acquire the later identity.
            TableSessionBinding later = P43TestData.Binding(bindingEpoch: 12);
            var retargetedView = new SessionViewState(
                1,
                TableConnectionState.Ready,
                later,
                args.PlayerSnapshot?.Clone(),
                null,
                null,
                null,
                null,
                false,
                args.Cursor,
                args.VisibleEventBatch);
            PlayerTableViewState retargeted = mapper.Project(retargetedView);
            var retargetedMapping = new ClientVisibleEventMapper().Map(args.VisibleEventBatch, retargeted, 1);
            Assert.False(retargetedMapping.Accepted);
            Assert.Equal(ClientVisibleEventMapper.ContextMismatchCode, retargetedMapping.Code);

            // Authorization loss is a different exact context and cannot play
            // the previously private event.
            var revokedView = new SessionViewState(
                1,
                TableConnectionState.Closed,
                session.Binding,
                args.PlayerSnapshot?.Clone(),
                null,
                null,
                null,
                "TEST_REVOKED",
                false,
                args.Cursor,
                args.VisibleEventBatch);
            PlayerTableViewState revoked = mapper.Project(revokedView);
            var revokedMapping = new ClientVisibleEventMapper().Map(args.VisibleEventBatch, revoked, 1);
            Assert.False(revokedMapping.Accepted);
            Assert.Equal(ClientVisibleEventMapper.NotReadyCode, revokedMapping.Code);

            var spectatorView = new SessionViewState(
                1,
                TableConnectionState.Ready,
                P43TestData.Binding(seat: -1, role: ProtocolRole.Spectator),
                null,
                P43TestData.PublicSnapshot(stateVersion: 6, eventSeq: 106, streamVersion: 5, projectionSeq: 11),
                null,
                null,
                null,
                false,
                args.Cursor,
                args.VisibleEventBatch);
            PlayerTableViewState spectator = mapper.Project(spectatorView);
            var spectatorMapping = new ClientVisibleEventMapper().Map(args.VisibleEventBatch, spectator, 1);
            Assert.False(spectatorMapping.Accepted);
            Assert.Equal(ClientVisibleEventMapper.ContextMismatchCode, spectatorMapping.Code);

            var queue = new ConfirmedAnimationQueue();
            queue.AcceptConfirmedBaseline(matching);
            Assert.Equal(0, queue.PendingCount);
        }
    }
}
