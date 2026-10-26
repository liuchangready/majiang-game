using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.UI.Session;
using WireCursor = Mahjong.Contracts.Wire.StreamCursor;

namespace Mahjong.Unity.Table
{
    /// <summary>
    /// Maps an owner-confirmed <see cref="SessionViewState"/> into an immutable
    /// table presentation projection.
    ///
    /// It consumes only the already-confirmed Client snapshot and its public
    /// fields. It does not read a live session, parse bytes, or consult a
    /// domain/runtime object. Public counts are never expanded into tile ids;
    /// an opponent closed kan is represented by an identity-free placeholder.
    /// If the binding, session, seat, room or ready/sync state disagrees, the
    /// private part is dropped instead of being shown for another identity.
    /// One mapper instance is the countdown observation context: reuse it for
    /// the lifetime of a table view so a repainted/re-sent confirmed sample does
    /// not renew its server-relative deadline. It is single-owner UI state and
    /// is not thread-safe.
    /// </summary>
    public sealed class PlayerTableProjectionMapper
    {
        private const int PlayerSeatCount = 4;
        private const int MaxObservationAnchors = 32;

        private readonly ITableClientClock _clock;
        private readonly Dictionary<string, long> _observationAnchors =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Queue<string> _observationOrder = new Queue<string>();

        public PlayerTableProjectionMapper(ITableClientClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public PlayerTableViewState Project(SessionViewState sessionView)
        {
            if (sessionView == null)
            {
                throw new ArgumentNullException(nameof(sessionView));
            }

            long now = _clock.MonotonicMilliseconds;
            long observedAt = ResolveObservationAnchor(
                BuildConfirmationFingerprint(sessionView),
                now);
            var gaps = new List<string>();
            AddStructuralGaps(gaps);

            SessionBindingView? binding = sessionView.Binding;
            PlayerTableRole role = binding == null ? PlayerTableRole.Unknown : (PlayerTableRole)binding.Role;
            bool playerBinding = binding != null &&
                                 role == PlayerTableRole.Player &&
                                 binding.SeatId >= 0 &&
                                 binding.SeatId <= 3;

            bool roomMatches = true;
            if (binding != null && sessionView.RoomSnapshot != null)
            {
                RoomUpdate room = sessionView.RoomSnapshot;
                if (!string.Equals(room.RoomId, binding.RoomId, StringComparison.Ordinal) ||
                    !string.Equals(room.SessionId, binding.SessionId, StringComparison.Ordinal))
                {
                    roomMatches = false;
                    AddGap(gaps, PlayerTableGapCodes.RoomBindingMismatch);
                }
            }

            bool ready = sessionView.State == TableConnectionState.Ready;

            PlayerSnapshot? player = sessionView.PlayerSnapshot;
            PublicSnapshot? publicSnapshot = sessionView.PublicSnapshot;

            if (role == PlayerTableRole.Spectator && player != null)
            {
                AddGap(gaps, PlayerTableGapCodes.SpectatorPlayerSnapshotIgnored);
                player = null;
            }
            else if (!playerBinding && player != null)
            {
                // A lobby/unknown binding must never receive a player's private
                // or public seat snapshot in this model.
                player = null;
            }

            if (publicSnapshot != null &&
                binding != null &&
                !string.Equals(publicSnapshot.SessionId, binding.SessionId, StringComparison.Ordinal))
            {
                AddGap(gaps, PlayerTableGapCodes.PublicSessionMismatch);
                publicSnapshot = null;
            }

            if (!roomMatches)
            {
                publicSnapshot = null;
                player = null;
            }

            if (player != null &&
                publicSnapshot != null &&
                binding != null &&
                string.Equals(player.SessionId, binding.SessionId, StringComparison.Ordinal) &&
                string.Equals(publicSnapshot.SessionId, binding.SessionId, StringComparison.Ordinal) &&
                !string.Equals(player.RoundId, publicSnapshot.RoundId, StringComparison.Ordinal))
            {
                AddGap(gaps, PlayerTableGapCodes.PublicRoundMismatch);
                publicSnapshot = null;
                player = null;
            }

            bool privateAuthorized = playerBinding && ready && roomMatches && player != null;
            if (playerBinding && !ready && player != null)
            {
                AddGap(gaps, PlayerTableGapCodes.PrivateViewNotReady);
                privateAuthorized = false;
            }

            if (player != null && binding != null)
            {
                if (!string.Equals(player.SessionId, binding.SessionId, StringComparison.Ordinal))
                {
                    AddGap(gaps, PlayerTableGapCodes.PrivateSessionMismatch);
                    privateAuthorized = false;
                    player = null;
                }
                else if (player.PrivateView == null)
                {
                    privateAuthorized = false;
                }
                else if (player.PrivateView.Seat != binding.SeatId)
                {
                    AddGap(gaps, PlayerTableGapCodes.PrivateSeatMismatch);
                    privateAuthorized = false;
                }
            }

            PublicMatchView? publicMatch = null;
            if (player != null && player.PublicView != null && privateAuthorized)
            {
                publicMatch = player.PublicView;
            }
            else if (publicSnapshot != null)
            {
                publicMatch = publicSnapshot.View;
            }
            else if (player != null && player.PublicView != null && playerBinding)
            {
                // Public part of a player snapshot may still be shown while a
                // private part is rejected; it contains no private field.
                publicMatch = player.PublicView;
            }

            string sessionId = binding?.SessionId ?? string.Empty;
            string roomId = binding?.RoomId ?? string.Empty;
            string roundId = player != null && privateAuthorized
                ? player.RoundId
                : publicSnapshot?.RoundId ?? player?.RoundId ?? string.Empty;
            ulong stateVersion = player != null && privateAuthorized
                ? player.StateVersion
                : publicSnapshot?.StateVersion ?? player?.StateVersion ?? 0UL;
            ulong eventSeq = player != null && privateAuthorized
                ? player.EventSeq
                : publicSnapshot?.EventSeq ?? player?.EventSeq ?? 0UL;
            WireCursor? cursor = player != null && privateAuthorized
                ? player.Cursor
                : publicSnapshot?.Cursor ?? player?.Cursor;

            var publicSeats = new Dictionary<int, PlayerPublicView>();
            if (publicMatch?.Board != null)
            {
                for (int i = 0; i < publicMatch.Board.Players.Count; i++)
                {
                    PlayerPublicView seatView = publicMatch.Board.Players[i];
                    if (!publicSeats.ContainsKey(seatView.Seat))
                    {
                        publicSeats.Add(seatView.Seat, seatView);
                    }
                }
            }

            PlayerOwnState? own = null;
            IReadOnlyList<PlayerMeldState> ownMelds = PlayerTableCopy.Empty<PlayerMeldState>();
            if (privateAuthorized && player?.PrivateView != null && playerBinding)
            {
                own = MapOwn(player.PrivateView, player.RoundId, observedAt, gaps, out ownMelds);
            }

            var seats = new List<PlayerSeatState>(PlayerSeatCount);
            for (int seat = 0; seat < PlayerSeatCount; seat++)
            {
                PlayerPublicView seatView;
                bool hasPublic = publicSeats.TryGetValue(seat, out seatView!);
                if (!hasPublic)
                {
                    if (publicMatch?.Board != null)
                    {
                        AddGap(gaps, PlayerTableGapCodes.MissingSeatProjection);
                    }
                }

                IReadOnlyList<PlayerDiscardState> discards = hasPublic
                    ? MapDiscards(seatView.Discards)
                    : PlayerTableCopy.Empty<PlayerDiscardState>();

                var publicMelds = new List<PlayerMeldState>();
                if (hasPublic)
                {
                    for (int i = 0; i < seatView.Melds.Count; i++)
                    {
                        PlayerMeldState? meld = MapMeld(seatView.Melds[i], i, gaps);
                        if (meld != null)
                        {
                            publicMelds.Add(meld);
                        }
                    }

                    for (int i = 0; i < seatView.ConcealedMelds.Count; i++)
                    {
                        ConcealedMeldView concealed = seatView.ConcealedMelds[i];
                        publicMelds.Add(new PlayerMeldState(
                            PlayerMeldKind.ClosedKan,
                            PlayerTableCopy.Empty<uint>(),
                            null,
                            concealed.FromSeat,
                            isPlaceholder: true,
                            placeholderOrdinal: i));
                    }
                }

                IReadOnlyList<PlayerMeldState> displayedMelds = publicMelds;
                bool meldsAreOwnerOnly = false;
                if (own != null && own.Seat == seat)
                {
                    // The owner's meld list is identity-complete. The public
                    // copy of the same open melds is evidence only and must not
                    // be rendered a second time.
                    if (ownMelds.Count > 0)
                    {
                        displayedMelds = ownMelds;
                        meldsAreOwnerOnly = true;
                    }
                    else if (publicMelds.Count > 0)
                    {
                        AddGap(gaps, PlayerTableGapCodes.OwnerMeldsMissing);
                    }
                }

                seats.Add(new PlayerSeatState(
                    seat,
                    hasPublic,
                    hasPublic ? seatView.HandCount : (uint?)null,
                    hasPublic && seatView.Riichi,
                    discards,
                    displayedMelds,
                    meldsAreOwnerOnly));
            }

            PlayerBoardState board = MapBoard(publicMatch);
            IReadOnlyList<PlayerClockState> clocks = MapClocks(publicMatch, observedAt);

            var identity = new PlayerTableIdentity(
                sessionView.Generation,
                sessionId,
                roomId,
                roundId,
                role,
                binding?.SeatId ?? -1,
                binding?.ConnectionEpoch ?? 0UL,
                binding?.OwnerEpoch ?? 0UL,
                binding?.BindingEpoch ?? 0UL,
                stateVersion,
                eventSeq,
                cursor?.StreamVersion ?? 0UL,
                cursor?.ProjectionSeq ?? 0UL,
                own != null,
                binding?.ScopeId ?? string.Empty);

            if (own != null && haveOwnCountMismatch(own, seats))
            {
                AddGap(gaps, PlayerTableGapCodes.OwnHandCountMismatch);
            }

            return new PlayerTableViewState(
                identity,
                sessionView.State,
                board,
                seats,
                own,
                clocks,
                board.Paused,
                gaps);
        }

        private static bool haveOwnCountMismatch(PlayerOwnState own, List<PlayerSeatState> seats)
        {
            for (int i = 0; i < seats.Count; i++)
            {
                if (seats[i].Seat != own.Seat)
                {
                    continue;
                }

                uint? count = seats[i].HandCount;
                if (!count.HasValue)
                {
                    continue;
                }

                return count.Value != own.PhysicalHandTileCount;
            }

            return false;
        }

        private static PlayerOwnState? MapOwn(
            PlayerPrivateView privateView,
            string roundId,
            long now,
            List<string> gaps,
            out IReadOnlyList<PlayerMeldState> ownerMelds)
        {
            ownerMelds = PlayerTableCopy.Empty<PlayerMeldState>();

            var closed = new List<uint>(privateView.ClosedTiles.Count);
            for (int i = 0; i < privateView.ClosedTiles.Count; i++)
            {
                closed.Add(privateView.ClosedTiles[i]);
            }

            var sleeve = new List<uint>(privateView.SleeveTiles.Count);
            for (int i = 0; i < privateView.SleeveTiles.Count; i++)
            {
                sleeve.Add(privateView.SleeveTiles[i]);
            }

            var mappedMelds = new List<PlayerMeldState>(privateView.OwnerMelds.Count);
            for (int i = 0; i < privateView.OwnerMelds.Count; i++)
            {
                PlayerMeldState? meld = MapMeld(privateView.OwnerMelds[i], i, gaps);
                if (meld == null)
                {
                    AddGap(gaps, PlayerTableGapCodes.PrivateTileDuplicated);
                    return null;
                }

                mappedMelds.Add(meld);
            }

            if (!OwnTilesAreDisjoint(closed, privateView.HasDrawnTileId ? privateView.DrawnTileId : (uint?)null, sleeve, mappedMelds))
            {
                AddGap(gaps, PlayerTableGapCodes.PrivateTileDuplicated);
                return null;
            }

            ownerMelds = PlayerTableCopy.ReadOnly(mappedMelds);

            PlayerDecisionState? decision = null;
            if (privateView.Decision != null)
            {
                var candidates = new List<PlayerLegalActionState>(privateView.Decision.Actions.Count);
                for (int i = 0; i < privateView.Decision.Actions.Count; i++)
                {
                    LegalActionView action = privateView.Decision.Actions[i];
                    candidates.Add(new PlayerLegalActionState(
                        i,
                        MapAction(action.ActionType),
                        action.ActionType,
                        action.OptionId ?? string.Empty,
                        CopyTiles(action.TileIds),
                        roundId,
                        privateView.Decision.DecisionId,
                        privateView.Decision.ControlEpoch));
                }

                decision = new PlayerDecisionState(
                    privateView.Decision.DecisionId,
                    privateView.Decision.ControlEpoch,
                    roundId,
                    new PlayerCountdown(privateView.Decision.DeadlineRemainingMs, now),
                    candidates);
            }

            var advisory = new List<PlayerLegalActionState>(privateView.LegalActions.Count);
            for (int i = 0; i < privateView.LegalActions.Count; i++)
            {
                LegalActionView action = privateView.LegalActions[i];
                advisory.Add(new PlayerLegalActionState(
                    i,
                    MapAction(action.ActionType),
                    action.ActionType,
                    action.OptionId ?? string.Empty,
                    CopyTiles(action.TileIds),
                    roundId,
                    null,
                    null));
            }

            var history = new List<uint>(privateView.DiscardHistory.Count);
            for (int i = 0; i < privateView.DiscardHistory.Count; i++)
            {
                history.Add(privateView.DiscardHistory[i]);
            }

            PlayerCheatState? cheat = privateView.Cheat == null
                ? null
                : MapCheat(privateView.Cheat, roundId, now, gaps);

            var own = new PlayerOwnState(
                privateView.Seat,
                roundId,
                closed,
                privateView.HasDrawnTileId ? privateView.DrawnTileId : (uint?)null,
                sleeve,
                history,
                privateView.Furiten,
                decision,
                advisory,
                cheat);

            return own;
        }

        private static bool OwnTilesAreDisjoint(
            IReadOnlyList<uint> closed,
            uint? drawn,
            IReadOnlyList<uint> sleeve,
            IReadOnlyList<PlayerMeldState> ownerMelds)
        {
            var seen = new HashSet<uint>();
            for (int i = 0; i < closed.Count; i++)
            {
                if (!seen.Add(closed[i]))
                {
                    return false;
                }
            }

            if (drawn.HasValue && !seen.Add(drawn.Value))
            {
                return false;
            }

            for (int i = 0; i < sleeve.Count; i++)
            {
                if (!seen.Add(sleeve[i]))
                {
                    return false;
                }
            }

            for (int i = 0; i < ownerMelds.Count; i++)
            {
                IReadOnlyList<uint> tiles = ownerMelds[i].TileIds;
                for (int t = 0; t < tiles.Count; t++)
                {
                    if (!seen.Add(tiles[t]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static PlayerCheatState? MapCheat(PrivateCheatView cheat, string roundId, long now, List<string> gaps)
        {
            PlayerTileMarkState? tileMark = null;
            if (cheat.TileMark != null)
            {
                var selectable = new List<uint>(cheat.TileMark.SelectableTileIds.Count);
                for (int i = 0; i < cheat.TileMark.SelectableTileIds.Count; i++)
                {
                    selectable.Add(cheat.TileMark.SelectableTileIds[i]);
                }

                var groups = new List<PlayerTileMarkGroupState>(cheat.TileMark.Groups.Count);
                for (int i = 0; i < cheat.TileMark.Groups.Count; i++)
                {
                    TileMarkGroupView group = cheat.TileMark.Groups[i];
                    var tiles = new List<PlayerMarkedTileState>(group.Tiles.Count);
                    for (int t = 0; t < group.Tiles.Count; t++)
                    {
                        MarkedTileView tile = group.Tiles[t];
                        tiles.Add(new PlayerMarkedTileState(tile.TileId, tile.Area, tile.Seat, tile.RiverPosition));
                    }

                    groups.Add(new PlayerTileMarkGroupState(
                        group.GroupId,
                        tiles,
                        group.CreatedRoundId,
                        group.CreatedRoundOrdinal,
                        group.ExpiresAfterRoundOrdinal));
                }

                tileMark = new PlayerTileMarkState(roundId, selectable, groups);
            }

            PlayerSleeveBloomState? immediate = cheat.SleeveBloom == null
                ? null
                : MapSleeveBloom(cheat.SleeveBloom, roundId, now);
            PlayerSleeveBloomState? offTurn = cheat.OffTurnSleeveBloom == null
                ? null
                : MapSleeveBloom(cheat.OffTurnSleeveBloom, roundId, now);

            PlayerSleeveBloomReservationState? reservation = null;
            if (cheat.SleeveBloomReservation != null)
            {
                SleeveBloomReservationView source = cheat.SleeveBloomReservation;
                reservation = new PlayerSleeveBloomReservationState(roundId, source.OfferId, source.TileId, source.WindowId);
            }

            PlayerBeamSwapState? beam = null;
            if (cheat.BeamSwap != null)
            {
                BeamSwapView source = cheat.BeamSwap;
                var candidates = new List<PlayerBeamSwapCandidateState>(source.Candidates.Count);
                for (int i = 0; i < source.Candidates.Count; i++)
                {
                    BeamSwapCandidateView candidate = source.Candidates[i];
                    candidates.Add(new PlayerBeamSwapCandidateState(
                        candidate.Slot,
                        candidate.HasTileId ? candidate.TileId : (uint?)null,
                        MapSlotKind(candidate.SlotKind),
                        candidate.SlotIndex));
                }

                var hand = new List<uint>(source.SelectableHandTileIds.Count);
                for (int i = 0; i < source.SelectableHandTileIds.Count; i++)
                {
                    hand.Add(source.SelectableHandTileIds[i]);
                }

                beam = new PlayerBeamSwapState(
                    roundId,
                    source.OfferId,
                    candidates,
                    hand,
                    source.HasDeadline ? new PlayerCountdown(source.DeadlineRemainingMs, now) : null,
                    source.HasDrawnTileId ? source.DrawnTileId : (uint?)null,
                    source.HasDeadline);
            }

            PlayerPreparationState? preparation = null;
            if (cheat.Preparation != null)
            {
                ShufflePreparationView source = cheat.Preparation;
                preparation = new PlayerPreparationState(
                    roundId,
                    source.Target,
                    source.SeeThrough,
                    source.Confirmed,
                    source.ReservedPoints,
                    source.AvailablePoints,
                    source.SeeThroughEnabled,
                    source.SeeThroughCost,
                    new PlayerCountdown(source.DeadlineRemainingMs, now));
            }

            var ledger = new List<PlayerCheatLedgerEntryState>(cheat.LedgerEntries.Count);
            for (int i = 0; i < cheat.LedgerEntries.Count; i++)
            {
                CheatEntryView entry = cheat.LedgerEntries[i];
                ledger.Add(new PlayerCheatLedgerEntryState(entry.EntryId, entry.Amount, entry.BalanceAfter, entry.ReasonCode));
            }

            return new PlayerCheatState(
                roundId,
                cheat.CheatPoints,
                ledger,
                tileMark,
                immediate,
                offTurn,
                reservation,
                beam,
                preparation,
                cheat.WindowId);
        }

        private static PlayerSleeveBloomState MapSleeveBloom(SleeveBloomView source, string roundId, long now)
        {
            return new PlayerSleeveBloomState(
                roundId,
                source.OfferId,
                source.Mode,
                source.HasFirstTileId ? source.FirstTileId : (uint?)null,
                source.HasSecondTileId ? source.SecondTileId : (uint?)null,
                source.HasDeadline ? new PlayerCountdown(source.DeadlineRemainingMs, now) : null,
                source.WindowId,
                source.HasReservedTileId ? source.ReservedTileId : (uint?)null,
                source.CreatedTurnNumber,
                MapDrawKind(source.DrawKind),
                source.HasDeadline);
        }

        private static PlayerBoardState MapBoard(PublicMatchView? match)
        {
            if (match == null)
            {
                return new PlayerBoardState(
                    false,
                    PlayerGamePhase.Unknown,
                    0,
                    0,
                    0,
                    0,
                    -1,
                    -1,
                    0,
                    PlayerTableCopy.Empty<uint>(),
                    PlayerTableCopy.Empty<PlayerTableScore>(),
                    null,
                    false,
                    false,
                    string.Empty,
                    PlayerTableCopy.Empty<PlayerTableScore>(),
                    PlayerTableCopy.Empty<int>(),
                    string.Empty);
            }

            PublicBoardView? board = match.Board;
            PublicSessionSummary? session = match.Session;
            var scores = new List<PlayerTableScore>();
            var finalScores = new List<PlayerTableScore>();
            var rankings = new List<int>();
            var dora = new List<uint>();

            if (board != null)
            {
                for (int i = 0; i < board.DoraIndicators.Count; i++)
                {
                    dora.Add(board.DoraIndicators[i]);
                }

                for (int i = 0; i < board.Scores.Count; i++)
                {
                    scores.Add(new PlayerTableScore(board.Scores[i].Seat, board.Scores[i].Score));
                }
            }

            if (session != null)
            {
                for (int i = 0; i < session.FinalScores.Count; i++)
                {
                    finalScores.Add(new PlayerTableScore(session.FinalScores[i].Seat, session.FinalScores[i].Score));
                }

                for (int i = 0; i < session.Rankings.Count; i++)
                {
                    rankings.Add(session.Rankings[i]);
                }
            }

            PlayerDiscardState? lastDiscard = null;
            if (board?.LastDiscard != null)
            {
                DiscardView discard = board.LastDiscard;
                lastDiscard = new PlayerDiscardState(discard.TileId, discard.TurnNumber, discard.RiichiDiscard, discard.Tsumogiri);
            }

            return new PlayerBoardState(
                board != null,
                MapPhase(board?.Phase ?? GamePhase.Unspecified),
                board?.RoundWind ?? 0U,
                board?.RoundNumber ?? 0U,
                board?.Honba ?? 0U,
                board?.RiichiSticks ?? 0U,
                board?.DealerSeat ?? -1,
                board?.CurrentSeat ?? -1,
                board?.WallRemaining ?? 0U,
                dora,
                scores,
                lastDiscard,
                (match.Paused || (board != null && board.Paused)),
                session?.Finished ?? false,
                session?.FinishReason ?? string.Empty,
                finalScores,
                rankings,
                match.SettlementRoundId ?? string.Empty);
        }

        private static IReadOnlyList<PlayerClockState> MapClocks(PublicMatchView? match, long now)
        {
            if (match == null || match.Clocks.Count == 0)
            {
                return PlayerTableCopy.Empty<PlayerClockState>();
            }

            var clocks = new List<PlayerClockState>(match.Clocks.Count);
            for (int i = 0; i < match.Clocks.Count && i < 4; i++)
            {
                ClockView clock = match.Clocks[i];
                clocks.Add(new PlayerClockState(
                    i,
                    new PlayerCountdown(Math.Max(0L, clock.DeadlineRemainingMs), now),
                    clock.ReserveRemainingMs,
                    clock.DecisionSeat,
                    clock.ServerTimeMs));
            }

            return clocks;
        }

        private static IReadOnlyList<PlayerDiscardState> MapDiscards(IEnumerable<DiscardView> source)
        {
            var result = new List<PlayerDiscardState>();
            foreach (DiscardView discard in source)
            {
                result.Add(new PlayerDiscardState(discard.TileId, discard.TurnNumber, discard.RiichiDiscard, discard.Tsumogiri));
            }

            return result;
        }

        private static PlayerMeldState? MapMeld(MeldView source, int ordinal, List<string> gaps)
        {
            PlayerMeldKind kind = MapMeldKind(source.Type);
            if (kind == PlayerMeldKind.Unknown)
            {
                AddGap(gaps, PlayerTableGapCodes.PublicMeldTypeUnknown);
                return null;
            }

            return new PlayerMeldState(
                kind,
                CopyTiles(source.TileIds),
                source.HasCalledTileId ? source.CalledTileId : (uint?)null,
                source.FromSeat,
                isPlaceholder: false,
                placeholderOrdinal: ordinal);
        }

        private static IReadOnlyList<uint> CopyTiles(IEnumerable<uint> source)
        {
            var copy = new List<uint>();
            foreach (uint tile in source)
            {
                copy.Add(tile);
            }

            return copy;
        }

        private static PlayerActionKind MapAction(uint raw)
        {
            switch (raw)
            {
                case 1: return PlayerActionKind.StartStacking;
                case 2: return PlayerActionKind.DealComplete;
                case 3: return PlayerActionKind.DrawTile;
                case 4: return PlayerActionKind.DiscardTile;
                case 5: return PlayerActionKind.DeclareChi;
                case 6: return PlayerActionKind.DeclarePon;
                case 7: return PlayerActionKind.DeclareKan;
                case 8: return PlayerActionKind.DeclareRiichi;
                case 9: return PlayerActionKind.DeclareTsumo;
                case 10: return PlayerActionKind.DeclareRon;
                case 11: return PlayerActionKind.Pass;
                case 12: return PlayerActionKind.DeclareAbortiveDraw;
                case 13: return PlayerActionKind.DeclareAgariYame;
                default: return PlayerActionKind.Unknown;
            }
        }

        private static PlayerGamePhase MapPhase(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.Stacking: return PlayerGamePhase.Stacking;
                case GamePhase.Dealing: return PlayerGamePhase.Dealing;
                case GamePhase.Draw: return PlayerGamePhase.Draw;
                case GamePhase.CheatWindow: return PlayerGamePhase.CheatWindow;
                case GamePhase.Discard: return PlayerGamePhase.Discard;
                case GamePhase.ResponseWindow: return PlayerGamePhase.ResponseWindow;
                case GamePhase.ChankanWindow: return PlayerGamePhase.ChankanWindow;
                case GamePhase.KanDraw: return PlayerGamePhase.KanDraw;
                case GamePhase.RoundEnd: return PlayerGamePhase.RoundEnd;
                case GamePhase.MatchEnd: return PlayerGamePhase.MatchEnd;
                default: return PlayerGamePhase.Unknown;
            }
        }

        private static PlayerMeldKind MapMeldKind(MeldType type)
        {
            switch (type)
            {
                case MeldType.Chi: return PlayerMeldKind.Chi;
                case MeldType.Pon: return PlayerMeldKind.Pon;
                case MeldType.OpenKan: return PlayerMeldKind.OpenKan;
                case MeldType.ClosedKan: return PlayerMeldKind.ClosedKan;
                case MeldType.AddedKan: return PlayerMeldKind.AddedKan;
                default: return PlayerMeldKind.Unknown;
            }
        }

        private static PlayerSleeveBloomDrawKind MapDrawKind(SleeveBloomDrawKind kind)
        {
            switch (kind)
            {
                case SleeveBloomDrawKind.DealExtra: return PlayerSleeveBloomDrawKind.DealExtra;
                case SleeveBloomDrawKind.Normal: return PlayerSleeveBloomDrawKind.Normal;
                case SleeveBloomDrawKind.Rinshan: return PlayerSleeveBloomDrawKind.Rinshan;
                default: return PlayerSleeveBloomDrawKind.Unknown;
            }
        }

        private static PlayerBeamSwapSlotKind MapSlotKind(BeamSwapSlotKind kind)
        {
            switch (kind)
            {
                case BeamSwapSlotKind.Wall: return PlayerBeamSwapSlotKind.Wall;
                case BeamSwapSlotKind.DeadWall: return PlayerBeamSwapSlotKind.DeadWall;
                case BeamSwapSlotKind.Replacement: return PlayerBeamSwapSlotKind.Replacement;
                default: return PlayerBeamSwapSlotKind.Unknown;
            }
        }

        private long ResolveObservationAnchor(string fingerprint, long now)
        {
            if (_observationAnchors.TryGetValue(fingerprint, out long existing))
            {
                return existing;
            }

            _observationAnchors[fingerprint] = now;
            _observationOrder.Enqueue(fingerprint);
            while (_observationOrder.Count > MaxObservationAnchors)
            {
                string expired = _observationOrder.Dequeue();
                _observationAnchors.Remove(expired);
            }

            return now;
        }

        /// <summary>
        /// Identity of one confirmed sample for countdown anchoring. It includes
        /// generation/session/binding/round/scope plus the actual snapshot bytes
        /// (cursor, state version, event sequence and content), so a repaint of
        /// the same sample cannot renew a deadline and a different projection
        /// with a reused state version is still a new sample. Connection state,
        /// pending commands and last-error notifications are deliberately not
        /// part of this fingerprint: they do not carry a new deadline.
        /// </summary>
        private static string BuildConfirmationFingerprint(SessionViewState sessionView)
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(sessionView.Generation);
                SessionBindingView? binding = sessionView.Binding;
                writer.Write(binding != null);
                if (binding != null)
                {
                    writer.Write(binding.SessionId);
                    writer.Write(binding.RoomId);
                    writer.Write(binding.TableId);
                    writer.Write(binding.Role);
                    writer.Write(binding.SeatId);
                    writer.Write(binding.ConnectionEpoch);
                    writer.Write(binding.OwnerEpoch);
                    writer.Write(binding.BindingEpoch);
                    writer.Write(binding.ScopeId);
                    writer.Write(binding.AccountId);
                }

                WriteSnapshot(writer, sessionView.PlayerSnapshot);
                WriteSnapshot(writer, sessionView.PublicSnapshot);
            }

            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(buffer.ToArray()));
        }

        private static void WriteSnapshot(BinaryWriter writer, Google.Protobuf.IMessage? message)
        {
            if (message == null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(message.ToByteArray());
        }

        private static void AddStructuralGaps(List<string> gaps)
        {
            AddGap(gaps, PlayerTableGapCodes.DeadWallIdentityNotPublic);
            AddGap(gaps, PlayerTableGapCodes.ControlStateNotRepresentable);
            AddGap(gaps, PlayerTableGapCodes.LegalActionMeldTypeNotRepresentable);
            AddGap(gaps, PlayerTableGapCodes.AbsoluteDeadlineNotRepresentable);
            AddGap(gaps, PlayerTableGapCodes.VisibleEventsNotExposedBySessionView);
            AddGap(gaps, PlayerTableGapCodes.ShuffleTargetCandidatesNotRepresentable);
            AddGap(gaps, PlayerTableGapCodes.BeamSlotKindNotInCommand);
        }

        private static void AddGap(List<string> gaps, string code)
        {
            if (!gaps.Contains(code))
            {
                gaps.Add(code);
            }
        }
    }
}
