using System;
using System.Collections.Generic;
using Google.Protobuf;
using Mahjong.Contracts;

namespace Mahjong.Contracts.Wire
{
    /// <summary>
    /// Maps the Contracts projection DTOs (never Domain, TableRuntime, actor state or opaque
    /// runtime bytes) to the generated Protobuf messages. It copies the source identity/cursor
    /// values unchanged and never allocates a stream cursor or a projection sequence.
    /// </summary>
    public static class ProjectionWireMapper
    {
        public const string MissingEventBodyCode = "PROJECTION_EVENT_BODY_MISSING";
        public const string InvalidProjectionCode = "PROJECTION_INVALID";

        public static PublicSnapshot MapPublicSnapshot(PublicSnapshotView source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return new PublicSnapshot
            {
                SessionId = RequireId(source.SessionId.Value, "session_id"),
                RoundId = RequireRoundId(source.RoundId),
                Cursor = MapCursor(source.Cursor),
                StateVersion = source.StateVersion.Value,
                EventSeq = source.EventSeq.Value,
                SessionVersion = source.SessionVersion.Value,
                RoundVersion = source.RoundVersion.Value,
                View = MapPublicMatchView(source.View)
            };
        }

        public static PlayerSnapshot MapPlayerSnapshot(PlayerSnapshotView source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (source.Private.Seat != source.Seat)
            {
                throw Invalid("Player private view belongs to seat " + source.Private.Seat + ", not " + source.Seat + ".");
            }

            return new PlayerSnapshot
            {
                SessionId = RequireId(source.SessionId.Value, "session_id"),
                RoundId = RequireRoundId(source.RoundId),
                Cursor = MapCursor(source.Cursor),
                StateVersion = source.StateVersion.Value,
                EventSeq = source.EventSeq.Value,
                SessionVersion = source.SessionVersion.Value,
                RoundVersion = source.RoundVersion.Value,
                PublicView = MapPublicMatchView(source.Public),
                PrivateView = MapPrivatePlayerView(source.Private)
            };
        }

        public static PublicUpdate MapPublicUpdate(
            PublicSnapshotView snapshot,
            IReadOnlyList<ProtocolEventRecord> visibleEvents,
            Contracts.StreamCursor fromCursor,
            bool isFullSnapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (visibleEvents == null)
            {
                throw new ArgumentNullException(nameof(visibleEvents));
            }

            var update = new PublicUpdate
            {
                Snapshot = MapPublicSnapshot(snapshot),
                FromCursor = MapCursor(fromCursor),
                Cursor = MapCursor(snapshot.Cursor),
                IsFullSnapshot = isFullSnapshot
            };
            for (int i = 0; i < visibleEvents.Count; i++)
            {
                update.VisibleEvents.Add(MapPublicEvent(visibleEvents[i]));
            }

            return update;
        }

        public static PlayerUpdate MapPlayerUpdate(
            PlayerSnapshotView snapshot,
            IReadOnlyList<ProtocolEventRecord> visibleEvents,
            Contracts.StreamCursor fromCursor,
            bool isFullSnapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (visibleEvents == null)
            {
                throw new ArgumentNullException(nameof(visibleEvents));
            }

            var update = new PlayerUpdate
            {
                Snapshot = MapPlayerSnapshot(snapshot),
                FromCursor = MapCursor(fromCursor),
                Cursor = MapCursor(snapshot.Cursor),
                IsFullSnapshot = isFullSnapshot
            };
            for (int i = 0; i < visibleEvents.Count; i++)
            {
                ValidateEventOwner(visibleEvents[i], snapshot.Seat);
                update.VisibleEvents.Add(MapEvent(visibleEvents[i]));
            }

            return update;
        }

        private static void ValidateEventOwner(ProtocolEventRecord record, int ownerSeat)
        {
            if (ownerSeat < ProtocolConstants.MinSeat || ownerSeat > ProtocolConstants.MaxSeat)
            {
                throw Invalid("A player update requires a concrete owner seat.");
            }

            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            ProjectedEventPayload? payload = record.ProjectionPayload;
            if (payload == null)
            {
                return;
            }

            if (ProtocolEventPolicy.IsPrivateEventType(record.Type) && payload.Seat != ownerSeat)
            {
                throw new PublicProjectionException(
                    "Private event " + record.Type + " belongs to seat " + payload.Seat + ", not receiver seat " + ownerSeat + ".",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }

            if (record.Type == ProtocolEventType.KanDeclared &&
                payload.KanType == ProtocolMeldType.ClosedKan &&
                payload.Meld != null &&
                payload.Meld.TileIds.Count > 0 &&
                payload.Seat != ownerSeat)
            {
                throw new PublicProjectionException(
                    "Closed-kan identities are owner-only; receiver seat " + ownerSeat +
                    " cannot receive identities for seat " + payload.Seat + ".",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }
        }

        public static EventEnvelope MapEvent(ProtocolEventRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            if (!record.IsKnown)
            {
                throw Invalid("An unknown event cannot be encoded as a typed event; request a full baseline.");
            }

            if (record.ProjectionPayload == null)
            {
                throw new WireProtocolException(
                    "Event " + record.Type + " has no typed Contracts projection body.",
                    MissingEventBodyCode);
            }

            EnsureSettlementVisibility(record.Type, record.Visibility);
            EnsureVisibilityMatchesType(record.Type, record.EffectiveVisibility);

            var envelope = new EventEnvelope
            {
                EventSchemaVersion = record.EventSchemaVersion,
                EventSeq = record.EventSeq.Value,
                CommandVersion = record.CommandVersion.Value,
                EventOrdinal = record.EventOrdinal,
                EventType = (EventType)(uint)record.Type,
                Visibility = (EventVisibility)(uint)record.EffectiveVisibility
            };

            MapEventBody(envelope, record.ProjectionPayload);
            return envelope;
        }

        public static PublicEventEnvelope MapPublicEvent(ProtocolEventRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            if (!record.IsKnown)
            {
                throw Invalid("An unknown event cannot be published as a typed public event.");
            }

            if (ProtocolEventPolicy.IsPrivateEventType(record.Type))
            {
                throw new PublicProjectionException(
                    "Private event " + record.Type + " cannot be mapped into a public update.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }

            if (!ProtocolEventPolicy.IsPublicStreamVisible(record.Visibility))
            {
                throw new PublicProjectionException(
                    "Private visibility event " + record.Type + " cannot be mapped into a public update.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }

            if (record.ProjectionPayload == null)
            {
                throw new WireProtocolException(
                    "Public event " + record.Type + " has no typed Contracts projection body.",
                    MissingEventBodyCode);
            }

            EnsureSettlementVisibility(record.Type, record.Visibility);

            if (record.ProjectionPayload.IsUraDora &&
                (!record.ProjectionPayload.UraAuthorized ||
                 record.Visibility != ProtocolEventVisibility.SettlementPublic))
            {
                throw new PublicProjectionException(
                    "Ura dora may enter a public stream only in an explicitly authorized settlement batch.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }

            var envelope = new PublicEventEnvelope
            {
                EventSchemaVersion = record.EventSchemaVersion,
                EventSeq = record.EventSeq.Value,
                CommandVersion = record.CommandVersion.Value,
                EventOrdinal = record.EventOrdinal,
                EventType = (EventType)(uint)record.Type,
                Visibility = (EventVisibility)(uint)record.EffectiveVisibility
            };

            MapPublicEventBody(envelope, record.ProjectionPayload);
            return envelope;
        }

        public static StreamCursor MapCursor(Contracts.StreamCursor cursor)
        {
            return new StreamCursor
            {
                StreamVersion = cursor.StreamVersion.Value,
                ProjectionSeq = cursor.ProjectionSeq.Value
            };
        }

        private static PublicMatchView MapPublicMatchView(Contracts.PublicMatchView source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var view = new PublicMatchView
            {
                Session = MapSession(source.Session),
                Paused = source.Paused,
                SettlementRoundId = RequireOptionalId(source.SettlementRoundId, "settlement_round_id")
            };

            if (source.Board != null)
            {
                view.Board = MapBoard(source.Board);
            }

            for (int i = 0; i < source.Clocks.Count; i++)
            {
                view.Clocks.Add(MapClock(source.Clocks[i]));
            }

            for (int i = 0; i < source.SettlementEvents.Count; i++)
            {
                view.SettlementEvents.Add(MapPublicEvent(source.SettlementEvents[i]));
            }

            return view;
        }

        private static PublicBoardView MapBoard(Contracts.PublicBoardView source)
        {
            if (source.Phase == ProtocolGamePhase.Unspecified)
            {
                throw Invalid("A public board must carry an explicit game phase.");
            }

            var board = new PublicBoardView
            {
                Phase = MapPhase(source.Phase),
                RoundWind = source.RoundWind,
                RoundNumber = source.RoundNumber,
                Honba = source.Honba,
                RiichiSticks = source.RiichiSticks,
                DealerSeat = RequireSeat(source.DealerSeat, "dealer_seat"),
                CurrentSeat = RequireSeat(source.CurrentSeat, "current_seat"),
                WallRemaining = source.WallRemaining,
                Paused = source.Paused
            };

            if (source.DoraIndicators.Count > 5)
            {
                throw Invalid("At most five public dora indicators are allowed.");
            }

            for (int i = 0; i < source.DoraIndicators.Count; i++)
            {
                board.DoraIndicators.Add(RequireTile(source.DoraIndicators[i], "dora_indicator"));
            }

            if (source.Scores.Count != 4)
            {
                throw Invalid("A public board requires exactly four score rows.");
            }

            var seats = new HashSet<int>();
            for (int i = 0; i < source.Scores.Count; i++)
            {
                Contracts.ScoreView score = source.Scores[i];
                if (!seats.Add(score.Seat))
                {
                    throw Invalid("Public score rows repeat seat " + score.Seat + ".");
                }

                board.Scores.Add(MapScore(score));
            }

            if (source.Players.Count != 4)
            {
                throw Invalid("A public board requires exactly four player views.");
            }

            var playerSeats = new HashSet<int>();
            for (int i = 0; i < source.Players.Count; i++)
            {
                Contracts.PlayerPublicView player = source.Players[i];
                if (!playerSeats.Add(player.Seat))
                {
                    throw Invalid("Public player views repeat seat " + player.Seat + ".");
                }

                board.Players.Add(MapPlayerPublicView(player));
            }

            if (source.LastDiscard != null)
            {
                board.LastDiscard = MapDiscard(source.LastDiscard);
            }

            return board;
        }

        private static PlayerPublicView MapPlayerPublicView(Contracts.PlayerPublicView source)
        {
            var view = new PlayerPublicView
            {
                Seat = RequireSeat(source.Seat, "player.seat"),
                HandCount = source.HandCount,
                Riichi = source.Riichi
            };

            if (source.HandCount > 14)
            {
                throw Invalid("A public hand count cannot exceed 14.");
            }

            for (int i = 0; i < source.Melds.Count; i++)
            {
                if (source.Melds[i].Type == ProtocolMeldType.ClosedKan)
                {
                    throw new PublicProjectionException(
                        "A public meld must not carry closed-kan identities; use concealed_melds.",
                        ProtocolErrorCode.PrivateDataInPublicStream);
                }

                view.Melds.Add(MapMeld(source.Melds[i]));
            }

            for (int i = 0; i < source.Discards.Count; i++)
            {
                view.Discards.Add(MapDiscard(source.Discards[i]));
            }

            for (int i = 0; i < source.ConcealedMelds.Count; i++)
            {
                view.ConcealedMelds.Add(MapConcealedMeld(source.ConcealedMelds[i]));
            }

            return view;
        }

        private static ConcealedMeldView MapConcealedMeld(Contracts.ConcealedMeldView source)
        {
            if (source.Type != ProtocolMeldType.ClosedKan)
            {
                throw Invalid("A concealed meld placeholder must be a closed kan.");
            }

            if (source.TileCount != 4)
            {
                throw Invalid("A concealed kan placeholder must occupy four tiles.");
            }

            return new ConcealedMeldView
            {
                Type = MeldType.ClosedKan,
                TileCount = 4,
                FromSeat = ProtocolConstants.NoSeat
            };
        }

        private static MeldView MapMeld(Contracts.MeldView source)
        {
            var meld = new MeldView
            {
                Type = MapMeldType(source.Type),
                FromSeat = source.FromSeat
            };

            var seen = new HashSet<uint>();
            for (int i = 0; i < source.TileIds.Count; i++)
            {
                uint tile = RequireTile(source.TileIds[i], "meld.tile_ids");
                if (!seen.Add(tile))
                {
                    throw Invalid("A meld repeats physical tile " + tile + ".");
                }

                meld.TileIds.Add(tile);
            }

            if (source.CalledTileId.HasValue)
            {
                meld.CalledTileId = RequireTile(source.CalledTileId.Value, "meld.called_tile_id");
                if (!seen.Contains(meld.CalledTileId))
                {
                    throw Invalid("The called tile must be one of the meld's physical tiles.");
                }
            }

            return meld;
        }

        private static DiscardView MapDiscard(Contracts.DiscardView source)
        {
            return new DiscardView
            {
                TileId = RequireTile(source.TileId, "discard.tile_id"),
                TurnNumber = source.TurnNumber,
                RiichiDiscard = source.RiichiDiscard,
                Tsumogiri = source.Tsumogiri
            };
        }

        private static ScoreView MapScore(Contracts.ScoreView source)
        {
            return new ScoreView
            {
                Seat = RequireSeat(source.Seat, "score.seat"),
                Score = source.Score
            };
        }

        private static ClockView MapClock(Contracts.ClockView source)
        {
            if (source.ServerTimeMs < 0)
            {
                throw Invalid("A clock server time cannot be negative.");
            }

            var clock = new ClockView
            {
                ServerTimeMs = source.ServerTimeMs,
                DeadlineRemainingMs = source.DeadlineRemainingMs,
                ReserveRemainingMs = source.ReserveRemainingMs,
                DecisionSeat = source.DecisionSeat
            };
            return clock;
        }

        private static PublicSessionSummary MapSession(Contracts.PublicSessionSummary source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var session = new PublicSessionSummary
            {
                Finished = source.Finished,
                FinishReason = RequireOptionalStableCode(source.FinishReason, "session.finish_reason")
            };
            for (int i = 0; i < source.FinalScores.Count; i++)
            {
                session.FinalScores.Add(MapScore(source.FinalScores[i]));
            }

            for (int i = 0; i < source.Rankings.Count; i++)
            {
                session.Rankings.Add(RequireSeat(source.Rankings[i], "ranking"));
            }

            return session;
        }

        private static PlayerPrivateView MapPrivatePlayerView(Contracts.PrivatePlayerView source)
        {
            var view = new PlayerPrivateView
            {
                Seat = RequireSeat(source.Seat, "private.seat"),
                Furiten = source.Furiten
            };

            var seen = new HashSet<uint>();
            for (int i = 0; i < source.ClosedTiles.Count; i++)
            {
                uint tile = RequireTile(source.ClosedTiles[i], "private.closed_tiles");
                if (!seen.Add(tile))
                {
                    throw Invalid("The closed hand repeats tile " + tile + ".");
                }

                view.ClosedTiles.Add(tile);
            }

            if (source.DrawnTileId.HasValue)
            {
                uint drawn = RequireTile(source.DrawnTileId.Value, "private.drawn_tile_id");
                if (!seen.Add(drawn))
                {
                    throw Invalid("The drawn tile duplicates a closed hand tile.");
                }

                view.DrawnTileId = drawn;
            }

            for (int i = 0; i < source.DiscardHistory.Count; i++)
            {
                view.DiscardHistory.Add(RequireTile(source.DiscardHistory[i], "private.discard_history"));
            }

            for (int i = 0; i < source.SleeveTiles.Count; i++)
            {
                uint tile = RequireTile(source.SleeveTiles[i], "private.sleeve_tiles");
                if (!seen.Add(tile))
                {
                    throw Invalid("A sleeve tile duplicates an owner hand tile.");
                }

                view.SleeveTiles.Add(tile);
            }

            for (int i = 0; i < source.OwnerMelds.Count; i++)
            {
                MeldView meld = MapMeld(source.OwnerMelds[i]);
                for (int t = 0; t < meld.TileIds.Count; t++)
                {
                    if (!seen.Add(meld.TileIds[t]))
                    {
                        throw Invalid("An owner meld tile duplicates an owner hand/sleeve tile.");
                    }
                }

                view.OwnerMelds.Add(meld);
            }

            if (source.Decision != null)
            {
                view.Decision = MapDecision(source.Decision);
                for (int i = 0; i < source.Decision.Actions.Count; i++)
                {
                    view.LegalActions.Add(MapLegalAction(source.Decision.Actions[i]));
                }
            }

            if (source.Cheat != null)
            {
                view.Cheat = MapPrivateCheat(source.Cheat);
            }

            return view;
        }

        private static DecisionView MapDecision(Contracts.DecisionView source)
        {
            var view = new DecisionView
            {
                DecisionId = RequireId(source.DecisionId.Value, "decision.decision_id"),
                ControlEpoch = source.ControlEpoch.Value,
                DeadlineRemainingMs = source.DeadlineRemainingMs
            };
            for (int i = 0; i < source.Actions.Count; i++)
            {
                view.Actions.Add(MapLegalAction(source.Actions[i]));
            }

            return view;
        }

        private static LegalActionView MapLegalAction(Contracts.LegalActionView source)
        {
            if (source.ActionType == ProtocolActionType.Unspecified)
            {
                throw Invalid("A legal action must carry an explicit action type.");
            }

            var action = new LegalActionView
            {
                ActionType = (uint)source.ActionType
            };
            if (!string.IsNullOrEmpty(source.OptionId))
            {
                action.OptionId = RequireId(source.OptionId, "legal_action.option_id");
            }

            for (int i = 0; i < source.TileIds.Count; i++)
            {
                action.TileIds.Add(RequireTile(source.TileIds[i], "legal_action.tile_ids"));
            }

            return action;
        }

        private static PrivateCheatView MapPrivateCheat(Contracts.PrivateCheatView source)
        {
            var view = new PrivateCheatView
            {
                CheatPoints = source.CheatPoints
            };

            for (int i = 0; i < source.Ledger.Count; i++)
            {
                CheatLedgerEntryView entry = source.Ledger[i];
                view.LedgerEntries.Add(new CheatEntryView
                {
                    EntryId = RequireId(entry.EntryId, "cheat.entry_id"),
                    Amount = entry.Amount,
                    BalanceAfter = entry.BalanceAfter,
                    ReasonCode = RequireStableCode(entry.ReasonCode, "cheat.reason_code")
                });
            }

            if (source.OwnerTileMark != null)
            {
                view.TileMark = MapTileMark(source.OwnerTileMark);
            }

            if (source.ImmediateSleeveOffer != null)
            {
                view.SleeveBloom = MapSleeveBloom(source.ImmediateSleeveOffer);
            }

            if (source.OffTurnSleeveOffer != null)
            {
                view.OffTurnSleeveBloom = MapSleeveBloom(source.OffTurnSleeveOffer);
            }

            if (source.SleeveReservation != null)
            {
                view.SleeveBloomReservation = new SleeveBloomReservationView
                {
                    OfferId = RequireId(source.SleeveReservation.OfferId, "reservation.offer_id"),
                    TileId = RequireTile(source.SleeveReservation.TileId, "reservation.tile_id"),
                    WindowId = source.SleeveReservation.WindowId ?? string.Empty
                };
            }

            if (source.OwnerBeamSwap != null)
            {
                view.BeamSwap = MapBeamSwap(source.OwnerBeamSwap);
            }

            if (source.Preparation != null)
            {
                view.Preparation = MapPreparation(source.Preparation);
            }

            return view;
        }

        private static TileMarkView MapTileMark(Contracts.TileMarkView source)
        {
            var view = new TileMarkView();
            for (int i = 0; i < source.SelectableTileIds.Count; i++)
            {
                view.SelectableTileIds.Add(RequireTile(source.SelectableTileIds[i], "tile_mark.selectable_tile_ids"));
            }

            for (int i = 0; i < source.Groups.Count; i++)
            {
                Contracts.TileMarkGroupView group = source.Groups[i];
                var mapped = new TileMarkGroupView
                {
                    GroupId = RequireId(group.GroupId, "tile_mark.group_id"),
                    CreatedRoundId = RequireId(group.CreatedRoundId, "tile_mark.created_round_id"),
                    CreatedRoundOrdinal = group.CreatedRoundOrdinal,
                    ExpiresAfterRoundOrdinal = group.ExpiresAfterRoundOrdinal
                };
                for (int t = 0; t < group.Tiles.Count; t++)
                {
                    Contracts.MarkedTileView tile = group.Tiles[t];
                    mapped.Tiles.Add(new MarkedTileView
                    {
                        TileId = RequireTile(tile.TileId, "tile_mark.tile_id"),
                        Area = tile.Area ?? string.Empty,
                        Seat = tile.Seat,
                        RiverPosition = tile.RiverPosition
                    });
                }

                view.Groups.Add(mapped);
            }

            return view;
        }

        private static SleeveBloomView MapSleeveBloom(Contracts.SleeveBloomView source)
        {
            var view = new SleeveBloomView
            {
                OfferId = RequireId(source.OfferId, "sleeve_bloom.offer_id"),
                Mode = source.Mode == ProjectionSleeveBloomMode.Immediate ? "IMMEDIATE" : "OFF_TURN",
                FirstTileId = RequireTile(source.FirstTileId, "sleeve_bloom.first_tile_id"),
                SecondTileId = RequireTile(source.SecondTileId, "sleeve_bloom.second_tile_id"),
                HasDeadline = source.HasDeadline,
                DeadlineRemainingMs = source.DeadlineRemainingMs,
                WindowId = source.WindowId ?? string.Empty,
                CreatedTurnNumber = source.CreatedTurnNumber,
                DrawKind = MapSleeveBloomDrawKind(source.DrawKind)
            };
            if (source.ReservedTileId.HasValue)
            {
                view.ReservedTileId = RequireTile(source.ReservedTileId.Value, "sleeve_bloom.reserved_tile_id");
            }

            return view;
        }

        private static BeamSwapView MapBeamSwap(Contracts.BeamSwapView source)
        {
            var view = new BeamSwapView
            {
                OfferId = RequireId(source.OfferId, "beam_swap.offer_id"),
                HasDeadline = source.HasDeadline,
                DeadlineRemainingMs = source.DeadlineRemainingMs,
                DrawnTileId = RequireTile(source.DrawnTileId, "beam_swap.drawn_tile_id")
            };
            for (int i = 0; i < source.Candidates.Count; i++)
            {
                Contracts.BeamSwapCandidateView candidate = source.Candidates[i];
                var mapped = new BeamSwapCandidateView
                {
                    Slot = candidate.Slot,
                    SlotKind = MapBeamSlotKind(candidate.SlotKind),
                    SlotIndex = candidate.SlotIndex
                };
                if (candidate.TileId.HasValue)
                {
                    mapped.TileId = RequireTile(candidate.TileId.Value, "beam_swap.candidate.tile_id");
                }

                view.Candidates.Add(mapped);
            }

            for (int i = 0; i < source.SelectableHandTileIds.Count; i++)
            {
                view.SelectableHandTileIds.Add(
                    RequireTile(source.SelectableHandTileIds[i], "beam_swap.selectable_hand_tile_ids"));
            }

            return view;
        }

        private static ShufflePreparationView MapPreparation(Contracts.ShufflePreparationView source)
        {
            return new ShufflePreparationView
            {
                Target = source.Target,
                SeeThrough = source.SeeThrough,
                Confirmed = source.Confirmed,
                ReservedPoints = source.ReservedPoints,
                AvailablePoints = source.AvailablePoints,
                SeeThroughEnabled = source.SeeThroughEnabled,
                SeeThroughCost = source.SeeThroughCost,
                DeadlineRemainingMs = source.DeadlineRemainingMs
            };
        }

        private static void MapEventBody(EventEnvelope envelope, ProjectedEventPayload payload)
        {
            switch (payload.Type)
            {
                case ProtocolEventType.StackingStarted:
                    envelope.StackingStarted = new StackingStarted { TurnNumber = payload.TurnNumber };
                    break;
                case ProtocolEventType.WallBuilt:
                    envelope.WallBuilt = new WallBuilt
                    {
                        TurnNumber = payload.TurnNumber,
                        WallSize = payload.WallSize
                    };
                    break;
                case ProtocolEventType.TileDealt:
                    envelope.TileDealt = new TileDealt
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "tile_dealt.player"),
                        TileId = RequireSingleTile(payload, "tile_dealt")
                    };
                    break;
                case ProtocolEventType.DealingComplete:
                    envelope.DealingComplete = new DealingComplete { TurnNumber = payload.TurnNumber };
                    break;
                case ProtocolEventType.TileDrawn:
                    envelope.TileDrawn = new TileDrawn
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "tile_drawn.player"),
                        TileId = RequireSingleTile(payload, "tile_drawn"),
                        WallRemaining = payload.WallRemaining
                    };
                    break;
                case ProtocolEventType.TileDiscarded:
                    envelope.TileDiscarded = new TileDiscarded
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "tile_discarded.player"),
                        TileId = RequireSingleTile(payload, "tile_discarded"),
                        RiichiDiscard = payload.RiichiDiscard,
                        Tsumogiri = payload.Tsumogiri
                    };
                    break;
                case ProtocolEventType.ChiDeclared:
                    envelope.ChiDeclared = new ChiDeclared
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "chi_declared.player"),
                        Meld = MapMeld(RequireMeld(payload, "chi_declared")),
                        FromPlayer = RequireSeatOrNoSeat(payload.FromSeat, "chi_declared.from_player")
                    };
                    break;
                case ProtocolEventType.PonDeclared:
                    envelope.PonDeclared = new PonDeclared
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "pon_declared.player"),
                        Meld = MapMeld(RequireMeld(payload, "pon_declared")),
                        FromPlayer = RequireSeatOrNoSeat(payload.FromSeat, "pon_declared.from_player")
                    };
                    break;
                case ProtocolEventType.KanDeclared:
                    envelope.KanDeclared = new KanDeclared
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "kan_declared.player"),
                        KanType = MapKanType(payload.KanType)
                    };
                    if (payload.Meld != null)
                    {
                        envelope.KanDeclared.Meld = MapMeld(payload.Meld);
                    }

                    break;
                case ProtocolEventType.RinshanDraw:
                    envelope.RinshanDraw = new RinshanDraw
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "rinshan_draw.player"),
                        TileId = RequireSingleTile(payload, "rinshan_draw")
                    };
                    break;
                case ProtocolEventType.RiichiDeclared:
                    if (!payload.DiscardTileId.HasValue)
                    {
                        throw new WireProtocolException(
                            "RiichiDeclared requires an explicit discard tile.",
                            MissingEventBodyCode);
                    }

                    envelope.RiichiDeclared = new RiichiDeclared
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "riichi_declared.player"),
                        DiscardTileId = RequireTile(payload.DiscardTileId.Value, "riichi_declared.discard_tile_id"),
                        DoubleRiichi = payload.DoubleRiichi
                    };
                    break;
                case ProtocolEventType.TsumoWin:
                    envelope.TsumoWin = new TsumoWin
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "tsumo_win.player"),
                        WinTileId = RequireSingleTile(payload, "tsumo_win"),
                        Han = payload.Han,
                        Fu = payload.Fu,
                        Score = payload.Score
                    };
                    AddYaku(envelope.TsumoWin.Yaku, payload.Yaku);
                    envelope.TsumoWin.Breakdown = MapBreakdown(payload.Breakdown);
                    break;
                case ProtocolEventType.RonWin:
                    if (!payload.LoserSeat.HasValue)
                    {
                        throw new WireProtocolException(
                            "RonWin requires an explicit loser seat.",
                            MissingEventBodyCode);
                    }

                    envelope.RonWin = new RonWin
                    {
                        TurnNumber = payload.TurnNumber,
                        Winner = RequireSeat(payload.Seat, "ron_win.winner"),
                        Loser = RequireSeat(payload.LoserSeat.Value, "ron_win.loser"),
                        WinTileId = RequireSingleTile(payload, "ron_win"),
                        Han = payload.Han,
                        Fu = payload.Fu,
                        Score = payload.Score,
                        IsChankan = payload.IsChankan
                    };
                    AddYaku(envelope.RonWin.Yaku, payload.Yaku);
                    envelope.RonWin.Breakdown = MapBreakdown(payload.Breakdown);
                    break;
                case ProtocolEventType.DoraRevealed:
                    envelope.DoraRevealed = new DoraRevealed
                    {
                        TurnNumber = payload.TurnNumber,
                        IndicatorTileId = RequireSingleTile(payload, "dora_revealed"),
                        IsUraDora = payload.IsUraDora,
                        UraAuthorized = payload.UraAuthorized
                    };
                    break;
                case ProtocolEventType.CheatPointsChanged:
                    if (!payload.CheatAmount.HasValue || !payload.CheatBalance.HasValue)
                    {
                        throw new WireProtocolException(
                            "CheatPointsChanged requires both amount and resulting balance.",
                            MissingEventBodyCode);
                    }

                    envelope.CheatPointsChanged = new CheatPointsChanged
                    {
                        TurnNumber = payload.TurnNumber,
                        Seat = RequireSeat(payload.Seat, "cheat_points_changed.seat"),
                        Amount = payload.CheatAmount.Value,
                        NewBalance = payload.CheatBalance.Value
                    };
                    break;
                case ProtocolEventType.DrawGame:
                    envelope.DrawGame = new DrawGame
                    {
                        TurnNumber = payload.TurnNumber,
                        Reason = RequireStableCode(payload.ReasonCode, "draw_game.reason")
                    };
                    for (int i = 0; i < payload.TenpaiPlayers.Count; i++)
                    {
                        envelope.DrawGame.TenpaiPlayers.Add(RequireSeat(payload.TenpaiPlayers[i], "draw_game.tenpai_players"));
                    }

                    break;
                case ProtocolEventType.ScoreSettled:
                    envelope.ScoreSettled = new ScoreSettled { TurnNumber = payload.TurnNumber };
                    for (int i = 0; i < payload.Payments.Count; i++)
                    {
                        Contracts.PaymentView payment = payload.Payments[i];
                        envelope.ScoreSettled.Payments.Add(new PaymentView
                        {
                            FromSeat = RequireSeat(payment.FromSeat, "payment.from_seat"),
                            ToSeat = RequireSeat(payment.ToSeat, "payment.to_seat"),
                            Amount = payment.Amount
                        });
                    }

                    for (int i = 0; i < payload.NewScores.Count; i++)
                    {
                        envelope.ScoreSettled.NewScores.Add(MapScore(payload.NewScores[i]));
                    }

                    break;
                case ProtocolEventType.RoundEnded:
                    envelope.RoundEnded = new RoundEnded
                    {
                        TurnNumber = payload.TurnNumber,
                        RoundWind = payload.RoundWind,
                        RoundNumber = payload.RoundNumber
                    };
                    break;
                case ProtocolEventType.MatchEnded:
                    if (!payload.WinnerSeat.HasValue)
                    {
                        throw new WireProtocolException(
                            "MatchEnded requires an explicit winner seat.",
                            MissingEventBodyCode);
                    }

                    envelope.MatchEnded = new MatchEnded
                    {
                        TurnNumber = payload.TurnNumber,
                        Winner = RequireSeat(payload.WinnerSeat.Value, "match_ended.winner")
                    };
                    for (int i = 0; i < payload.FinalScores.Count; i++)
                    {
                        envelope.MatchEnded.FinalScores.Add(MapScore(payload.FinalScores[i]));
                    }

                    break;
                case ProtocolEventType.SleeveBloomResolved:
                    envelope.SleeveBloomResolved = new SleeveBloomResolved
                    {
                        OfferId = RequireId(payload.OfferId, "sleeve_bloom_resolved.offer_id"),
                        KeptFirst = payload.KeptFirst,
                        OwnerSeat = RequireSeat(payload.Seat, "sleeve_bloom_resolved.owner_seat")
                    };
                    if (payload.ResultTileId.HasValue)
                    {
                        envelope.SleeveBloomResolved.ResultTileId =
                            RequireTile(payload.ResultTileId.Value, "sleeve_bloom_resolved.result_tile_id");
                    }

                    break;
                case ProtocolEventType.BeamSwapResolved:
                    if (!payload.BeamSwapResolution.HasValue ||
                        payload.BeamSwapResolution.Value == ProjectionBeamSwapResolution.Unspecified)
                    {
                        throw new WireProtocolException(
                            "BeamSwapResolved requires an explicit resolution.",
                            MissingEventBodyCode);
                    }

                    var beam = new BeamSwapResolved
                    {
                        OfferId = RequireId(payload.OfferId, "beam_swap_resolved.offer_id"),
                        Action = payload.BeamSwapResolution.Value == ProjectionBeamSwapResolution.Swap
                            ? "SWAP"
                            : "KEEP_ORIGINAL",
                        OwnerSeat = RequireSeat(payload.Seat, "beam_swap_resolved.owner_seat")
                    };
                    if (payload.HandTileId.HasValue)
                    {
                        beam.HandTileId = RequireTile(payload.HandTileId.Value, "beam_swap_resolved.hand_tile_id");
                    }

                    if (payload.TargetSlot.HasValue)
                    {
                        beam.TargetSlot = payload.TargetSlot.Value;
                    }

                    envelope.BeamSwapResolved = beam;
                    break;
                default:
                    throw Invalid("Event type " + payload.Type + " has no EventEnvelope body.");
            }
        }

        private static void MapPublicEventBody(PublicEventEnvelope envelope, ProjectedEventPayload payload)
        {
            switch (payload.Type)
            {
                case ProtocolEventType.StackingStarted:
                    envelope.StackingStarted = new StackingStarted { TurnNumber = payload.TurnNumber };
                    break;
                case ProtocolEventType.WallBuilt:
                    envelope.WallBuilt = new WallBuilt
                    {
                        TurnNumber = payload.TurnNumber,
                        WallSize = payload.WallSize
                    };
                    break;
                case ProtocolEventType.DealingComplete:
                    envelope.DealingComplete = new DealingComplete { TurnNumber = payload.TurnNumber };
                    break;
                case ProtocolEventType.TileDiscarded:
                    envelope.TileDiscarded = new TileDiscarded
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "tile_discarded.player"),
                        TileId = RequireSingleTile(payload, "tile_discarded"),
                        RiichiDiscard = payload.RiichiDiscard,
                        Tsumogiri = payload.Tsumogiri
                    };
                    break;
                case ProtocolEventType.ChiDeclared:
                    envelope.ChiDeclared = new ChiDeclared
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "chi_declared.player"),
                        Meld = MapMeld(RequireMeld(payload, "chi_declared")),
                        FromPlayer = RequireSeatOrNoSeat(payload.FromSeat, "chi_declared.from_player")
                    };
                    break;
                case ProtocolEventType.PonDeclared:
                    envelope.PonDeclared = new PonDeclared
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "pon_declared.player"),
                        Meld = MapMeld(RequireMeld(payload, "pon_declared")),
                        FromPlayer = RequireSeatOrNoSeat(payload.FromSeat, "pon_declared.from_player")
                    };
                    break;
                case ProtocolEventType.KanDeclared:
                    var kan = new KanDeclared
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "kan_declared.player"),
                        KanType = MapKanType(payload.KanType)
                    };
                    if (payload.KanType == ProtocolMeldType.ClosedKan)
                    {
                        if (payload.Meld != null)
                        {
                            throw new PublicProjectionException(
                                "A public closed-kan event must not carry a meld body; identities are owner-only.",
                                ProtocolErrorCode.PrivateDataInPublicStream);
                        }
                    }
                    else if (payload.Meld != null)
                    {
                        kan.Meld = MapMeld(payload.Meld);
                    }

                    envelope.KanDeclared = kan;
                    break;
                case ProtocolEventType.RiichiDeclared:
                    if (!payload.DiscardTileId.HasValue)
                    {
                        throw new WireProtocolException(
                            "RiichiDeclared requires an explicit discard tile.",
                            MissingEventBodyCode);
                    }

                    envelope.RiichiDeclared = new RiichiDeclared
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "riichi_declared.player"),
                        DiscardTileId = RequireTile(payload.DiscardTileId.Value, "riichi_declared.discard_tile_id"),
                        DoubleRiichi = payload.DoubleRiichi
                    };
                    break;
                case ProtocolEventType.TsumoWin:
                    envelope.TsumoWin = new TsumoWin
                    {
                        TurnNumber = payload.TurnNumber,
                        Player = RequireSeat(payload.Seat, "tsumo_win.player"),
                        WinTileId = RequireSingleTile(payload, "tsumo_win"),
                        Han = payload.Han,
                        Fu = payload.Fu,
                        Score = payload.Score
                    };
                    AddYaku(envelope.TsumoWin.Yaku, payload.Yaku);
                    envelope.TsumoWin.Breakdown = MapBreakdown(payload.Breakdown);
                    break;
                case ProtocolEventType.RonWin:
                    if (!payload.LoserSeat.HasValue)
                    {
                        throw new WireProtocolException(
                            "RonWin requires an explicit loser seat.",
                            MissingEventBodyCode);
                    }

                    envelope.RonWin = new RonWin
                    {
                        TurnNumber = payload.TurnNumber,
                        Winner = RequireSeat(payload.Seat, "ron_win.winner"),
                        Loser = RequireSeat(payload.LoserSeat.Value, "ron_win.loser"),
                        WinTileId = RequireSingleTile(payload, "ron_win"),
                        Han = payload.Han,
                        Fu = payload.Fu,
                        Score = payload.Score,
                        IsChankan = payload.IsChankan
                    };
                    AddYaku(envelope.RonWin.Yaku, payload.Yaku);
                    envelope.RonWin.Breakdown = MapBreakdown(payload.Breakdown);
                    break;
                case ProtocolEventType.DoraRevealed:
                    envelope.DoraRevealed = new DoraRevealed
                    {
                        TurnNumber = payload.TurnNumber,
                        IndicatorTileId = RequireSingleTile(payload, "dora_revealed"),
                        IsUraDora = payload.IsUraDora,
                        UraAuthorized = payload.UraAuthorized
                    };
                    break;
                case ProtocolEventType.DrawGame:
                    envelope.DrawGame = new DrawGame
                    {
                        TurnNumber = payload.TurnNumber,
                        Reason = RequireStableCode(payload.ReasonCode, "draw_game.reason")
                    };
                    for (int i = 0; i < payload.TenpaiPlayers.Count; i++)
                    {
                        envelope.DrawGame.TenpaiPlayers.Add(RequireSeat(payload.TenpaiPlayers[i], "draw_game.tenpai_players"));
                    }

                    break;
                case ProtocolEventType.ScoreSettled:
                    envelope.ScoreSettled = new ScoreSettled { TurnNumber = payload.TurnNumber };
                    for (int i = 0; i < payload.Payments.Count; i++)
                    {
                        Contracts.PaymentView payment = payload.Payments[i];
                        envelope.ScoreSettled.Payments.Add(new PaymentView
                        {
                            FromSeat = RequireSeat(payment.FromSeat, "payment.from_seat"),
                            ToSeat = RequireSeat(payment.ToSeat, "payment.to_seat"),
                            Amount = payment.Amount
                        });
                    }

                    for (int i = 0; i < payload.NewScores.Count; i++)
                    {
                        envelope.ScoreSettled.NewScores.Add(MapScore(payload.NewScores[i]));
                    }

                    break;
                case ProtocolEventType.RoundEnded:
                    envelope.RoundEnded = new RoundEnded
                    {
                        TurnNumber = payload.TurnNumber,
                        RoundWind = payload.RoundWind,
                        RoundNumber = payload.RoundNumber
                    };
                    break;
                case ProtocolEventType.MatchEnded:
                    if (!payload.WinnerSeat.HasValue)
                    {
                        throw new WireProtocolException(
                            "MatchEnded requires an explicit winner seat.",
                            MissingEventBodyCode);
                    }

                    envelope.MatchEnded = new MatchEnded
                    {
                        TurnNumber = payload.TurnNumber,
                        Winner = RequireSeat(payload.WinnerSeat.Value, "match_ended.winner")
                    };
                    for (int i = 0; i < payload.FinalScores.Count; i++)
                    {
                        envelope.MatchEnded.FinalScores.Add(MapScore(payload.FinalScores[i]));
                    }

                    break;
                default:
                    throw new PublicProjectionException(
                        "Event " + payload.Type + " has no public oneof body.",
                        ProtocolErrorCode.PrivateDataInPublicStream);
            }
        }

        private static void AddYaku(Google.Protobuf.Collections.RepeatedField<YakuView> target, IReadOnlyList<Contracts.YakuView> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                target.Add(new YakuView
                {
                    Code = RequireId(source[i].Code, "yaku.code"),
                    Han = source[i].Han
                });
            }
        }

        private static ScoreBreakdownView? MapBreakdown(Contracts.ScoreBreakdownView? source)
        {
            if (source == null)
            {
                return null;
            }

            var breakdown = new ScoreBreakdownView
            {
                Han = source.Han,
                Fu = source.Fu,
                TotalPoints = source.TotalPoints,
                DoraCount = source.DoraCount,
                UraCount = source.UraCount,
                UraAuthorized = source.UraAuthorized
            };
            AddYaku(breakdown.Yaku, source.Yaku);
            for (int i = 0; i < source.UraTiles.Count; i++)
            {
                breakdown.UraTiles.Add(RequireTile(source.UraTiles[i], "breakdown.ura_tiles"));
            }

            for (int i = 0; i < source.UraHits.Count; i++)
            {
                breakdown.UraHits.Add(RequireTile(source.UraHits[i], "breakdown.ura_hits"));
            }

            return breakdown;
        }

        private static GamePhase MapPhase(ProtocolGamePhase phase)
        {
            switch (phase)
            {
                case ProtocolGamePhase.Stacking: return GamePhase.Stacking;
                case ProtocolGamePhase.Dealing: return GamePhase.Dealing;
                case ProtocolGamePhase.Draw: return GamePhase.Draw;
                case ProtocolGamePhase.CheatWindow: return GamePhase.CheatWindow;
                case ProtocolGamePhase.Discard: return GamePhase.Discard;
                case ProtocolGamePhase.ResponseWindow: return GamePhase.ResponseWindow;
                case ProtocolGamePhase.ChankanWindow: return GamePhase.ChankanWindow;
                case ProtocolGamePhase.KanDraw: return GamePhase.KanDraw;
                case ProtocolGamePhase.RoundEnd: return GamePhase.RoundEnd;
                case ProtocolGamePhase.MatchEnd: return GamePhase.MatchEnd;
                default: throw Invalid("Unknown public game phase " + phase + ".");
            }
        }

        private static MeldType MapMeldType(ProtocolMeldType type)
        {
            switch (type)
            {
                case ProtocolMeldType.Chi: return MeldType.Chi;
                case ProtocolMeldType.Pon: return MeldType.Pon;
                case ProtocolMeldType.OpenKan: return MeldType.OpenKan;
                case ProtocolMeldType.ClosedKan: return MeldType.ClosedKan;
                case ProtocolMeldType.AddedKan: return MeldType.AddedKan;
                default: throw Invalid("Meld type must be explicit.");
            }
        }

        private static KanType MapKanType(ProtocolMeldType? type)
        {
            if (!type.HasValue)
            {
                throw new WireProtocolException(
                    "KanDeclared requires an explicit kan type.",
                    MissingEventBodyCode);
            }

            switch (type.Value)
            {
                case ProtocolMeldType.OpenKan: return KanType.Open;
                case ProtocolMeldType.ClosedKan: return KanType.Closed;
                case ProtocolMeldType.AddedKan: return KanType.Added;
                default: throw Invalid("Kan type must be an open, closed or added kan.");
            }
        }

        private static SleeveBloomDrawKind MapSleeveBloomDrawKind(ProjectionSleeveBloomDrawKind kind)
        {
            switch (kind)
            {
                case ProjectionSleeveBloomDrawKind.DealExtra: return SleeveBloomDrawKind.DealExtra;
                case ProjectionSleeveBloomDrawKind.Normal: return SleeveBloomDrawKind.Normal;
                case ProjectionSleeveBloomDrawKind.Rinshan: return SleeveBloomDrawKind.Rinshan;
                default: throw Invalid("Sleeve-bloom draw kind must be explicit.");
            }
        }

        private static BeamSwapSlotKind MapBeamSlotKind(ProjectionBeamSwapSlotKind kind)
        {
            switch (kind)
            {
                case ProjectionBeamSwapSlotKind.Wall: return BeamSwapSlotKind.Wall;
                case ProjectionBeamSwapSlotKind.DeadWall: return BeamSwapSlotKind.DeadWall;
                case ProjectionBeamSwapSlotKind.Replacement: return BeamSwapSlotKind.Replacement;
                default: throw Invalid("Beam-swap slot kind must be explicit.");
            }
        }

        private static uint RequireSingleTile(ProjectedEventPayload payload, string name)
        {
            if (payload.TileIds.Count != 1)
            {
                throw new WireProtocolException(
                    name + " requires exactly one tile identity.",
                    MissingEventBodyCode);
            }

            return RequireTile(payload.TileIds[0], name + ".tile_id");
        }

        private static Contracts.MeldView RequireMeld(ProjectedEventPayload payload, string name)
        {
            if (payload.Meld == null)
            {
                throw new WireProtocolException(
                    name + " requires an explicit meld body.",
                    MissingEventBodyCode);
            }

            return payload.Meld;
        }

        private static uint RequireTile(uint value, string name)
        {
            if (value > 135)
            {
                throw Invalid(name + " has tile id " + value + " outside 0..135.");
            }

            return value;
        }

        private static int RequireSeat(int value, string name)
        {
            if (value < ProtocolConstants.MinSeat || value > ProtocolConstants.MaxSeat)
            {
                throw Invalid(name + " has seat " + value + " outside 0..3.");
            }

            return value;
        }

        private static int RequireSeatOrNoSeat(int value, string name)
        {
            if (value != ProtocolConstants.NoSeat)
            {
                RequireSeat(value, name);
            }

            return value;
        }

        private static string RequireId(string? value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw Invalid(name + " must not be empty.");
            }

            return ProtocolText.RequireId(value, name);
        }

        private static string RequireRoundId(Contracts.RoundId? value)
        {
            if (!value.HasValue || value.Value.IsEmpty)
            {
                throw Invalid("A table snapshot requires an explicit round id.");
            }

            return value.Value.Value;
        }

        private static string RequireStableCode(string? value, string name)
        {
            if (string.IsNullOrEmpty(value) || !ProtocolErrorCode.IsWellFormed(value))
            {
                throw Invalid(name + " must be a stable uppercase code.");
            }

            return value;
        }

        private static void EnsureSettlementVisibility(
            ProtocolEventType type,
            ProtocolEventVisibility visibility)
        {
            if ((type == ProtocolEventType.TsumoWin || type == ProtocolEventType.RonWin) &&
                visibility != ProtocolEventVisibility.SettlementPublic)
            {
                throw new PublicProjectionException(
                    "Win events require explicit SETTLEMENT_PUBLIC visibility.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }
        }

        private static void EnsureVisibilityMatchesType(
            ProtocolEventType type,
            ProtocolEventVisibility visibility)
        {
            if (ProtocolEventPolicy.IsPrivateEventType(type))
            {
                if (visibility != ProtocolEventVisibility.Private)
                {
                    throw new PublicProjectionException(
                        "Private event " + type + " requires explicit PRIVATE visibility.",
                        ProtocolErrorCode.PrivateDataInPublicStream);
                }

                return;
            }

            if (visibility != ProtocolEventVisibility.Public &&
                visibility != ProtocolEventVisibility.SettlementPublic)
            {
                throw new PublicProjectionException(
                    "Public event " + type + " requires PUBLIC or SETTLEMENT_PUBLIC visibility.",
                    ProtocolErrorCode.PrivateDataInPublicStream);
            }
        }

        private static string RequireOptionalId(string? value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return RequireId(value, name);
        }

        private static string RequireOptionalStableCode(string? value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return RequireStableCode(value, name);
        }

        private static WireProtocolException Invalid(string message)
        {
            return new WireProtocolException(message, InvalidProjectionCode);
        }
    }
}
