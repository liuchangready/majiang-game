using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// One meld that is already public to the table. Tiles are physical ids so a strategy
    /// cannot infer a hidden wall or another seat's closed hand from this type.
    /// </summary>
    public sealed class AiVisibleMeld
    {
        private readonly ReadOnlyCollection<TileId> _tiles;

        public AiVisibleMeld(int seat, IReadOnlyList<TileId> tiles)
        {
            AiGuard.CheckSeat(seat, nameof(seat));
            if (tiles == null)
            {
                throw new ArgumentNullException(nameof(tiles));
            }

            if (tiles.Count != 3 && tiles.Count != 4)
            {
                throw new ArgumentException("A visible meld must contain three or four tiles.", nameof(tiles));
            }

            var copy = new List<TileId>(tiles.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < tiles.Count; i++)
            {
                TileId tile = tiles[i];
                if (!seen.Add(tile.Value))
                {
                    throw new ArgumentException("A visible meld cannot repeat a physical tile.", nameof(tiles));
                }

                copy.Add(tile);
            }

            Seat = seat;
            _tiles = new ReadOnlyCollection<TileId>(copy);
        }

        public int Seat { get; }

        public IReadOnlyList<TileId> Tiles => _tiles;
    }

    /// <summary>One public discard, including the flags needed to read the river.</summary>
    public sealed class AiVisibleDiscard
    {
        public AiVisibleDiscard(int seat, TileId tile, bool isRiichi = false, bool isClaimed = false)
        {
            AiGuard.CheckSeat(seat, nameof(seat));
            Seat = seat;
            Tile = tile;
            IsRiichi = isRiichi;
            IsClaimed = isClaimed;
        }

        public int Seat { get; }

        public TileId Tile { get; }

        public bool IsRiichi { get; }

        public bool IsClaimed { get; }
    }

    /// <summary>
    /// The complete input an AI strategy is allowed to see. It is a per-seat projection,
    /// not a copy of the domain state: it carries the viewer's own hand, public discards and
    /// melds, public dora indicators, scores, the authoritative decision identity and the
    /// frozen legal candidate list. There is intentionally no wall composition, dead-wall
    /// composition, opponent closed hand, ura-dora, seed or full <c>TilePositionMap</c>.
    ///
    /// Construction validates and defensively copies every collection. The table runtime
    /// (P3.2 projection) is responsible for passing only information that this seat may see;
    /// this type makes it impossible for the strategy to reach anything else.
    /// </summary>
    public sealed class AiObservation
    {
        public const int CurrentVersion = 1;

        private readonly ReadOnlyCollection<TileId> _myHandTileIds;
        private readonly ReadOnlyCollection<AiVisibleMeld> _myMelds;
        private readonly ReadOnlyCollection<AiVisibleMeld> _visibleMelds;
        private readonly ReadOnlyCollection<AiVisibleDiscard> _visibleDiscards;
        private readonly ReadOnlyCollection<TileId> _visibleDoraIndicators;
        private readonly ReadOnlyCollection<long> _scores;
        private readonly ReadOnlyCollection<LegalActionOption> _legalActions;

        public AiObservation(
            string sessionId,
            DecisionKey decisionKey,
            long controlEpoch,
            long stateVersion,
            long decisionDeadlineMillis,
            RoundPhase phase,
            int dealerSeat,
            int roundWind,
            int roundNumber,
            int honbaSticks,
            int riichiSticks,
            int remainingWallCount,
            IReadOnlyList<TileId> myHandTileIds,
            TileId? myDrawnTileId,
            IReadOnlyList<AiVisibleMeld> myMelds,
            IReadOnlyList<AiVisibleMeld> visibleMelds,
            IReadOnlyList<AiVisibleDiscard> visibleDiscards,
            IReadOnlyList<TileId> visibleDoraIndicators,
            IReadOnlyList<long> scores,
            IReadOnlyList<LegalActionOption> legalActions)
        {
            SessionId = AiGuard.RequireText(sessionId, nameof(sessionId));
            AiGuard.RequireText(decisionKey.RoundId, "decisionKey.RoundId");
            AiGuard.RequireText(decisionKey.DecisionId, "decisionKey.DecisionId");
            AiGuard.CheckSeat(decisionKey.Seat, nameof(decisionKey));
            AiGuard.CheckNonNegative(controlEpoch, nameof(controlEpoch));
            AiGuard.CheckNonNegative(stateVersion, nameof(stateVersion));
            AiGuard.CheckNonNegative(decisionDeadlineMillis, nameof(decisionDeadlineMillis));
            if (phase < RoundPhase.Stacking || phase > RoundPhase.RoundEnd)
            {
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown round phase.");
            }

            AiGuard.CheckSeat(dealerSeat, nameof(dealerSeat));
            if (roundWind < 1 || roundWind > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(roundWind), roundWind, "Round wind must be 1..3.");
            }

            if (roundNumber < 0 || roundNumber > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(roundNumber), roundNumber, "Round number must be 0..3.");
            }

            AiGuard.CheckNonNegative(honbaSticks, nameof(honbaSticks));
            AiGuard.CheckNonNegative(riichiSticks, nameof(riichiSticks));
            if (remainingWallCount < 0 || remainingWallCount > TileId.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(remainingWallCount),
                    remainingWallCount,
                    "Remaining wall count must be 0..136.");
            }

            if (myHandTileIds == null)
            {
                throw new ArgumentNullException(nameof(myHandTileIds));
            }

            if (myHandTileIds.Count > 14)
            {
                throw new ArgumentException("A visible hand cannot exceed 14 physical tiles.", nameof(myHandTileIds));
            }

            var hand = new List<TileId>(myHandTileIds.Count);
            var handSeen = new HashSet<int>();
            for (int i = 0; i < myHandTileIds.Count; i++)
            {
                TileId tile = myHandTileIds[i];
                if (!handSeen.Add(tile.Value))
                {
                    throw new ArgumentException("The visible hand cannot repeat a physical tile.", nameof(myHandTileIds));
                }

                hand.Add(tile);
            }

            _myMelds = CopyMelds(myMelds, decisionKey.Seat, nameof(myMelds));
            _visibleMelds = CopyMelds(visibleMelds, null, nameof(visibleMelds));
            _visibleDiscards = CopyDiscards(visibleDiscards);
            _visibleDoraIndicators = CopyTiles(visibleDoraIndicators, nameof(visibleDoraIndicators));
            if (_visibleDoraIndicators.Count > 5)
            {
                throw new ArgumentException("At most five dora indicators are visible in V4.", nameof(visibleDoraIndicators));
            }

            if (scores == null)
            {
                throw new ArgumentNullException(nameof(scores));
            }

            if (scores.Count != 4)
            {
                throw new ArgumentException("Scores must contain exactly four seats.", nameof(scores));
            }

            var scoreCopy = new long[4];
            for (int i = 0; i < 4; i++)
            {
                scoreCopy[i] = scores[i];
            }

            _scores = new ReadOnlyCollection<long>(scoreCopy);
            _legalActions = CopyActions(legalActions, decisionKey.Seat);
            if (_legalActions.Count == 0)
            {
                throw new ArgumentException("An AI observation must contain at least one legal candidate.", nameof(legalActions));
            }

            _myHandTileIds = new ReadOnlyCollection<TileId>(hand);
            DecisionKey = decisionKey;
            ControlEpoch = controlEpoch;
            StateVersion = stateVersion;
            DecisionDeadlineMillis = decisionDeadlineMillis;
            Phase = phase;
            DealerSeat = dealerSeat;
            RoundWind = roundWind;
            RoundNumber = roundNumber;
            HonbaSticks = honbaSticks;
            RiichiSticks = riichiSticks;
            RemainingWallCount = remainingWallCount;
            MyDrawnTileId = myDrawnTileId;
        }

        public int Version => CurrentVersion;

        public string SessionId { get; }

        public DecisionKey DecisionKey { get; }

        public string RoundId => DecisionKey.RoundId;

        public int Seat => DecisionKey.Seat;

        public string DecisionId => DecisionKey.DecisionId;

        public long ControlEpoch { get; }

        public long StateVersion { get; }

        public long DecisionDeadlineMillis { get; }

        public RoundPhase Phase { get; }

        public int DealerSeat { get; }

        public int RoundWind { get; }

        public int RoundNumber { get; }

        public int HonbaSticks { get; }

        public int RiichiSticks { get; }

        /// <summary>Public remaining-draw count, never the identities or order of wall tiles.</summary>
        public int RemainingWallCount { get; }

        public IReadOnlyList<TileId> MyHandTileIds => _myHandTileIds;

        public TileId? MyDrawnTileId { get; }

        public IReadOnlyList<AiVisibleMeld> MyMelds => _myMelds;

        public IReadOnlyList<AiVisibleMeld> VisibleMelds => _visibleMelds;

        public IReadOnlyList<AiVisibleDiscard> VisibleDiscards => _visibleDiscards;

        public IReadOnlyList<TileId> VisibleDoraIndicators => _visibleDoraIndicators;

        public IReadOnlyList<long> Scores => _scores;

        public IReadOnlyList<LegalActionOption> LegalActions => _legalActions;

        public bool ContainsLegalAction(LegalActionOption action)
        {
            if (action == null)
            {
                return false;
            }

            for (int i = 0; i < _legalActions.Count; i++)
            {
                if (AiGuard.ActionsEqual(_legalActions[i], action))
                {
                    return true;
                }
            }

            return false;
        }

        private static ReadOnlyCollection<AiVisibleMeld> CopyMelds(
            IReadOnlyList<AiVisibleMeld>? melds,
            int? requiredSeat,
            string name)
        {
            if (melds == null)
            {
                throw new ArgumentNullException(name);
            }

            var copy = new List<AiVisibleMeld>(melds.Count);
            for (int i = 0; i < melds.Count; i++)
            {
                AiVisibleMeld meld = melds[i] ??
                    throw new ArgumentException("Visible meld cannot be null.", name);
                if (requiredSeat.HasValue && meld.Seat != requiredSeat.Value)
                {
                    throw new ArgumentException("The viewer's melds must belong to the viewing seat.", name);
                }

                copy.Add(meld);
            }

            return new ReadOnlyCollection<AiVisibleMeld>(copy);
        }

        private static ReadOnlyCollection<AiVisibleDiscard> CopyDiscards(IReadOnlyList<AiVisibleDiscard>? discards)
        {
            if (discards == null)
            {
                throw new ArgumentNullException(nameof(discards));
            }

            var copy = new List<AiVisibleDiscard>(discards.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < discards.Count; i++)
            {
                AiVisibleDiscard discard = discards[i] ??
                    throw new ArgumentException("Visible discard cannot be null.", nameof(discards));
                if (!seen.Add(discard.Tile.Value))
                {
                    throw new ArgumentException("A physical tile can be discarded at most once.", nameof(discards));
                }

                copy.Add(discard);
            }

            return new ReadOnlyCollection<AiVisibleDiscard>(copy);
        }

        private static ReadOnlyCollection<TileId> CopyTiles(IReadOnlyList<TileId>? tiles, string name)
        {
            if (tiles == null)
            {
                throw new ArgumentNullException(name);
            }

            var copy = new List<TileId>(tiles.Count);
            var seen = new HashSet<int>();
            for (int i = 0; i < tiles.Count; i++)
            {
                TileId tile = tiles[i];
                if (!seen.Add(tile.Value))
                {
                    throw new ArgumentException("A visible tile list cannot repeat a physical tile.", name);
                }

                copy.Add(tile);
            }

            return new ReadOnlyCollection<TileId>(copy);
        }

        private static ReadOnlyCollection<LegalActionOption> CopyActions(
            IReadOnlyList<LegalActionOption>? actions,
            int requiredSeat)
        {
            if (actions == null)
            {
                throw new ArgumentNullException(nameof(actions));
            }

            var copy = new List<LegalActionOption>(actions.Count);
            for (int i = 0; i < actions.Count; i++)
            {
                LegalActionOption action = actions[i] ??
                    throw new ArgumentException("Legal candidate cannot be null.", nameof(actions));
                if (action.Seat != requiredSeat)
                {
                    throw new ArgumentException("Every legal candidate must belong to the viewing seat.", nameof(actions));
                }

                for (int j = 0; j < copy.Count; j++)
                {
                    if (AiGuard.ActionsEqual(copy[j], action))
                    {
                        throw new ArgumentException("Legal candidate list cannot contain duplicates.", nameof(actions));
                    }
                }

                copy.Add(action);
            }

            return new ReadOnlyCollection<LegalActionOption>(copy);
        }
    }

    internal static class AiGuard
    {
        internal static string RequireText(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(name + " cannot be blank.", name);
            }

            return value!;
        }

        internal static void CheckSeat(int seat, string name)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(name, seat, "Seat must be 0..3.");
            }
        }

        internal static void CheckNonNegative(long value, string name)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(name, value, name + " must be non-negative.");
            }
        }

        internal static bool ActionsEqual(LegalActionOption left, LegalActionOption right)
        {
            return left.Kind == right.Kind &&
                   left.Seat == right.Seat &&
                   Nullable.Equals(left.Tile, right.Tile) &&
                   string.Equals(left.OptionId, right.OptionId, StringComparison.Ordinal);
        }
    }
}
