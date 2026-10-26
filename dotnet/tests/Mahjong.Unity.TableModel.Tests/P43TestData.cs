using System;
using System.Collections.Generic;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.UI.Session;
using ContractScope = Mahjong.Contracts.RequestScope;
using ContractCursor = Mahjong.Contracts.StreamCursor;
using ContractBaseline = Mahjong.Contracts.StreamBaseline;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;
using WireScope = Mahjong.Contracts.Wire.RequestScope;
using WireStreamBaseline = Mahjong.Contracts.Wire.StreamBaseline;
using MeldView = Mahjong.Contracts.Wire.MeldView;
using ConcealedMeldView = Mahjong.Contracts.Wire.ConcealedMeldView;
using DiscardView = Mahjong.Contracts.Wire.DiscardView;
using PlayerPublicView = Mahjong.Contracts.Wire.PlayerPublicView;
using ScoreView = Mahjong.Contracts.Wire.ScoreView;
using ClockView = Mahjong.Contracts.Wire.ClockView;
using PublicBoardView = Mahjong.Contracts.Wire.PublicBoardView;
using PublicSessionSummary = Mahjong.Contracts.Wire.PublicSessionSummary;
using PublicMatchView = Mahjong.Contracts.Wire.PublicMatchView;
using LegalActionView = Mahjong.Contracts.Wire.LegalActionView;
using DecisionView = Mahjong.Contracts.Wire.DecisionView;
using PrivateCheatView = Mahjong.Contracts.Wire.PrivateCheatView;
using PrivatePlayerView = Mahjong.Contracts.Wire.PlayerPrivateView;
using SleeveBloomView = Mahjong.Contracts.Wire.SleeveBloomView;
using SleeveBloomReservationView = Mahjong.Contracts.Wire.SleeveBloomReservationView;
using TileMarkView = Mahjong.Contracts.Wire.TileMarkView;
using TileMarkGroupView = Mahjong.Contracts.Wire.TileMarkGroupView;
using MarkedTileView = Mahjong.Contracts.Wire.MarkedTileView;
using BeamSwapView = Mahjong.Contracts.Wire.BeamSwapView;
using BeamSwapCandidateView = Mahjong.Contracts.Wire.BeamSwapCandidateView;
using ShufflePreparationView = Mahjong.Contracts.Wire.ShufflePreparationView;

namespace Mahjong.Unity.TableModel.Tests
{
    internal static class P43TestData
    {
        public const string SessionId = "session-p43";
        public const string RoomId = "room-p43";
        public const string RoundId = "round-p43";
        public const string ScopeId = "scope-p43";
        public const string AccountId = "account-p43";
        public const int BindingEpoch = 11;

        public static PlayerSnapshot PlayerSnapshot(
            ulong stateVersion = 5,
            ulong eventSeq = 105,
            ulong streamVersion = 5,
            ulong projectionSeq = 10,
            string sessionId = SessionId,
            string roundId = RoundId,
            int seat = 0,
            bool includePrivate = true,
            bool includeDecision = true,
            bool includeCheat = true)
        {
            var snapshot = new PlayerSnapshot
            {
                SessionId = sessionId,
                RoundId = roundId,
                Cursor = new WireCursor { StreamVersion = streamVersion, ProjectionSeq = projectionSeq },
                StateVersion = stateVersion,
                EventSeq = eventSeq,
                SessionVersion = 1,
                RoundVersion = 2,
                PublicView = PublicMatch(seat)
            };

            if (includePrivate)
            {
                snapshot.PrivateView = PrivateView(seat, includeDecision, includeCheat);
            }

            return snapshot;
        }

        public static PublicMatchView PublicMatch(int ownSeat = 0)
        {
            var match = new PublicMatchView
            {
                Board = Board(ownSeat),
                Session = new PublicSessionSummary { Finished = false },
                Paused = false,
                SettlementRoundId = RoundId
            };

            match.Clocks.Add(new ClockView { ServerTimeMs = 1000, DeadlineRemainingMs = 8000, ReserveRemainingMs = 20000, DecisionSeat = ownSeat });
            match.Clocks.Add(new ClockView { ServerTimeMs = 1000, DeadlineRemainingMs = 0, ReserveRemainingMs = 20000, DecisionSeat = -1 });
            match.Clocks.Add(new ClockView { ServerTimeMs = 1000, DeadlineRemainingMs = 0, ReserveRemainingMs = 20000, DecisionSeat = -1 });
            match.Clocks.Add(new ClockView { ServerTimeMs = 1000, DeadlineRemainingMs = 0, ReserveRemainingMs = 20000, DecisionSeat = -1 });
            return match;
        }

        public static PublicBoardView Board(int ownSeat = 0)
        {
            var board = new PublicBoardView
            {
                Phase = GamePhase.Discard,
                RoundWind = 0,
                RoundNumber = 1,
                Honba = 0,
                RiichiSticks = 0,
                DealerSeat = 0,
                CurrentSeat = ownSeat,
                WallRemaining = 70,
                Paused = false
            };
            board.DoraIndicators.Add(50);
            for (int seat = 0; seat < 4; seat++)
            {
                board.Scores.Add(new ScoreView { Seat = seat, Score = 25000 + seat });
            }

            for (int seat = 0; seat < 4; seat++)
            {
                var player = new PlayerPublicView
                {
                    Seat = seat,
                    HandCount = seat == ownSeat ? 14U : 13U,
                    Riichi = seat == 2
                };

                if (seat == ownSeat)
                {
                    player.Melds.Add(new MeldView
                    {
                        Type = MeldType.Chi,
                        CalledTileId = 32,
                        FromSeat = 3
                    });
                    player.Melds[0].TileIds.Add(30);
                    player.Melds[0].TileIds.Add(31);
                    player.Melds[0].TileIds.Add(32);
                    player.ConcealedMelds.Add(new ConcealedMeldView { Type = MeldType.ClosedKan, TileCount = 4, FromSeat = -1 });
                }

                if (seat == 1)
                {
                    player.ConcealedMelds.Add(new ConcealedMeldView { Type = MeldType.ClosedKan, TileCount = 4, FromSeat = -1 });
                    player.ConcealedMelds.Add(new ConcealedMeldView { Type = MeldType.ClosedKan, TileCount = 4, FromSeat = -1 });
                }

                player.Discards.Add(new DiscardView
                {
                    TileId = (uint)(40 + seat),
                    TurnNumber = 1,
                    RiichiDiscard = seat == 2,
                    Tsumogiri = false
                });
                board.Players.Add(player);
            }

            board.LastDiscard = new DiscardView { TileId = 40, TurnNumber = 1, RiichiDiscard = false, Tsumogiri = false };
            return board;
        }

        public static PlayerPrivateView PrivateView(int seat = 0, bool includeDecision = true, bool includeCheat = true)
        {
            var view = new PlayerPrivateView
            {
                Seat = seat,
                Furiten = false
            };

            for (uint tile = 0; tile < 13; tile++)
            {
                view.ClosedTiles.Add(tile);
            }

            view.DrawnTileId = 13;
            view.SleeveTiles.Add(20);
            view.DiscardHistory.Add(40);
            view.LegalActions.Add(new LegalActionView
            {
                ActionType = (uint)ProtocolActionType.DiscardTile,
                OptionId = "advisory-discard-13",
                TileIds = { 13 }
            });

            MeldView ownerChi = new MeldView
            {
                Type = MeldType.Chi,
                CalledTileId = 32,
                FromSeat = 3
            };
            ownerChi.TileIds.Add(30);
            ownerChi.TileIds.Add(31);
            ownerChi.TileIds.Add(32);
            view.OwnerMelds.Add(ownerChi);

            MeldView ownerKan = new MeldView
            {
                Type = MeldType.ClosedKan,
                FromSeat = -1
            };
            ownerKan.TileIds.Add(60);
            ownerKan.TileIds.Add(61);
            ownerKan.TileIds.Add(62);
            ownerKan.TileIds.Add(63);
            view.OwnerMelds.Add(ownerKan);

            if (includeDecision)
            {
                view.Decision = new DecisionView
                {
                    DecisionId = "decision-p43",
                    ControlEpoch = 7,
                    DeadlineRemainingMs = 8000
                };
                view.Decision.Actions.Add(new LegalActionView
                {
                    ActionType = (uint)ProtocolActionType.DiscardTile,
                    OptionId = "discard-13",
                    TileIds = { 13 }
                });
                view.Decision.Actions.Add(new LegalActionView
                {
                    ActionType = (uint)ProtocolActionType.DeclareRiichi,
                    OptionId = "riichi-13",
                    TileIds = { 13 }
                });
                view.Decision.Actions.Add(new LegalActionView
                {
                    ActionType = (uint)ProtocolActionType.Pass,
                    OptionId = "pass"
                });
            }

            if (includeCheat)
            {
                view.Cheat = Cheat();
            }

            return view;
        }

        public static PrivateCheatView Cheat()
        {
            var cheat = new PrivateCheatView
            {
                CheatPoints = 5,
                WindowId = "window-1"
            };

            cheat.LedgerEntries.Add(new CheatEntryView
            {
                EntryId = "ledger-1",
                Amount = -2,
                BalanceAfter = 5,
                ReasonCode = "TILE_MARK"
            });

            var mark = new TileMarkView();
            for (uint tile = 0; tile < 6; tile++)
            {
                mark.SelectableTileIds.Add(tile);
            }

            var group = new TileMarkGroupView
            {
                GroupId = "group-1",
                CreatedRoundId = RoundId,
                CreatedRoundOrdinal = 0,
                ExpiresAfterRoundOrdinal = 1
            };
            group.Tiles.Add(new MarkedTileView { TileId = 0, Area = "HAND", Seat = 0, RiverPosition = 0 });
            mark.Groups.Add(group);
            cheat.TileMark = mark;

            cheat.SleeveBloom = new SleeveBloomView
            {
                OfferId = "offer-1",
                Mode = "IMMEDIATE",
                FirstTileId = 13,
                SecondTileId = 14,
                DeadlineRemainingMs = 5000,
                HasDeadline = true,
                CreatedTurnNumber = 1,
                DrawKind = SleeveBloomDrawKind.Normal
            };

            cheat.OffTurnSleeveBloom = new SleeveBloomView
            {
                OfferId = "offer-off",
                Mode = "OFF_TURN",
                FirstTileId = 15,
                SecondTileId = 16,
                DeadlineRemainingMs = 7000,
                HasDeadline = true,
                WindowId = "window-1",
                CreatedTurnNumber = 1,
                DrawKind = SleeveBloomDrawKind.Normal
            };

            cheat.SleeveBloomReservation = new SleeveBloomReservationView
            {
                OfferId = "offer-res",
                TileId = 17,
                WindowId = "window-1"
            };

            var beam = new BeamSwapView
            {
                OfferId = "beam-1",
                DeadlineRemainingMs = 6000,
                HasDeadline = true,
                DrawnTileId = 13
            };
            beam.Candidates.Add(new BeamSwapCandidateView
            {
                Slot = 3,
                TileId = 18,
                SlotKind = BeamSwapSlotKind.Wall,
                SlotIndex = 3
            });
            beam.Candidates.Add(new BeamSwapCandidateView
            {
                Slot = 4,
                TileId = 19,
                SlotKind = BeamSwapSlotKind.DeadWall,
                SlotIndex = 4
            });
            beam.SelectableHandTileIds.Add(0);
            beam.SelectableHandTileIds.Add(1);
            cheat.BeamSwap = beam;

            cheat.Preparation = new ShufflePreparationView
            {
                Target = 2,
                SeeThrough = false,
                Confirmed = false,
                ReservedPoints = 1,
                AvailablePoints = 10,
                SeeThroughEnabled = true,
                SeeThroughCost = 2,
                DeadlineRemainingMs = 3000
            };

            return cheat;
        }

        public static TableSessionBinding Binding(
            int seat = 0,
            ProtocolRole role = ProtocolRole.Player,
            string sessionId = SessionId,
            string roomId = RoomId,
            ulong bindingEpoch = BindingEpoch)
        {
            var scope = new ContractScope(
                new RequestScopeId(ScopeId),
                AccountId,
                role,
                new RoomId(roomId),
                1000,
                2000);
            var baseline = new ContractBaseline(
                new StateVersion(5),
                new EventSeq(105),
                new ContractCursor(new StreamVersion(5), new ProjectionSeq(10)));
            return new TableSessionBinding(
                new SessionId(sessionId),
                new RoomId(roomId),
                "table-p43",
                role,
                seat,
                new ConnectionEpoch(1),
                new OwnerEpoch(3),
                new BindingEpoch(bindingEpoch),
                scope,
                baseline);
        }

        public static SessionViewState View(
            PlayerSnapshot? player,
            PublicSnapshot? publicSnapshot = null,
            RoomUpdate? room = null,
            TableConnectionState state = TableConnectionState.Ready,
            int seat = 0,
            ProtocolRole role = ProtocolRole.Player,
            string sessionId = SessionId,
            string roomId = RoomId,
            ulong bindingEpoch = BindingEpoch,
            long generation = 1)
        {
            return new SessionViewState(
                generation,
                state,
                Binding(seat, role, sessionId, roomId, bindingEpoch),
                player,
                publicSnapshot,
                room,
                null,
                null);
        }

        public static PublicSnapshot PublicSnapshot(
            ulong stateVersion = 5,
            ulong eventSeq = 105,
            ulong streamVersion = 5,
            ulong projectionSeq = 10,
            string sessionId = SessionId,
            string roundId = RoundId)
        {
            return new PublicSnapshot
            {
                SessionId = sessionId,
                RoundId = roundId,
                Cursor = new WireCursor { StreamVersion = streamVersion, ProjectionSeq = projectionSeq },
                StateVersion = stateVersion,
                EventSeq = eventSeq,
                View = PublicMatch(0)
            };
        }

        public static Envelope Welcome(int bindingEpoch = BindingEpoch, int seat = 0)
        {
            var envelope = BaseEnvelope(MessageType.Welcome, bindingEpoch, seat);
            envelope.Welcome = new Welcome
            {
                SessionId = SessionId,
                RoomId = RoomId,
                TableId = "table-p43",
                Role = Role.Player,
                SeatId = seat,
                ConnectionEpoch = 1,
                OwnerEpoch = 3,
                BindingEpoch = (ulong)bindingEpoch,
                Baseline = new WireStreamBaseline
                {
                    StateVersion = 5,
                    EventSeq = 105,
                    Cursor = new WireCursor { StreamVersion = 5, ProjectionSeq = 10 }
                },
                Scope = new WireScope
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
            };
            return envelope;
        }

        public static Envelope SyncResponse(string correlationId, int bindingEpoch = BindingEpoch, int seat = 0)
        {
            var envelope = BaseEnvelope(MessageType.SyncResponse, bindingEpoch, seat);
            envelope.CorrelationId = correlationId;
            envelope.SyncResponse = new SyncResponse
            {
                Target = SyncTarget.PlayerStream,
                IsFullSnapshot = true,
                Player = PlayerSnapshot(),
                StateVersion = 5,
                EventSeq = 105
            };
            return envelope;
        }

        public static Envelope BaseEnvelope(MessageType type, int bindingEpoch, int seat)
        {
            return new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = type,
                SessionId = SessionId,
                RoomId = RoomId,
                SeatId = seat,
                BindingEpoch = (ulong)bindingEpoch
            };
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
                throw new InvalidOperationException("Could not decode test wire: " + result.Status + "/" + result.ErrorCode);
            }

            return result.Envelope;
        }

        public static T Clone<T>(T message)
            where T : IMessage<T>
        {
            return message.Clone();
        }
    }
}
