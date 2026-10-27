using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Domain;
using Mahjong.Domain.Rules;

namespace Mahjong.LocalHost
{
    /// <summary>
    /// Derives legal action candidates from authoritative Domain facts across all phases.
    /// Reuses Domain HandDecomposer and RoundStateMachine.Prepare; never invents custom winning rules.
    /// </summary>
    internal static class LocalCandidateProvider
    {
        public static bool CanChi(IReadOnlyList<TileId> hand, TileId targetTile)
        {
            if (!targetTile.IsNumberTile)
            {
                return false;
            }

            bool HasRank(int rank) => hand.Any(tile =>
                tile.IsNumberTile && tile.Suit == targetTile.Suit && tile.Rank == rank);

            int rank = targetTile.Rank;
            return (rank <= 7 && HasRank(rank + 1) && HasRank(rank + 2)) ||
                   (rank >= 2 && rank <= 8 && HasRank(rank - 1) && HasRank(rank + 1)) ||
                   (rank >= 3 && HasRank(rank - 2) && HasRank(rank - 1));
        }

        public static (TileId a, TileId b)? FindChiTiles(IReadOnlyList<TileId> hand, TileId targetTile)
        {
            if (!targetTile.IsNumberTile)
            {
                return null;
            }

            int rank = targetTile.Rank;
            TileId? Find(int r)
            {
                for (int i = 0; i < hand.Count; i++)
                {
                    TileId tile = hand[i];
                    if (tile.IsNumberTile && tile.Suit == targetTile.Suit && tile.Rank == r)
                    {
                        return tile;
                    }
                }

                return null;
            }

            if (rank <= 7)
            {
                TileId? a = Find(rank + 1);
                TileId? b = Find(rank + 2);
                if (a.HasValue && b.HasValue)
                {
                    return (a.Value, b.Value);
                }
            }

            if (rank >= 2 && rank <= 8)
            {
                TileId? a = Find(rank - 1);
                TileId? b = Find(rank + 1);
                if (a.HasValue && b.HasValue)
                {
                    return (a.Value, b.Value);
                }
            }

            if (rank >= 3)
            {
                TileId? a = Find(rank - 2);
                TileId? b = Find(rank - 1);
                if (a.HasValue && b.HasValue)
                {
                    return (a.Value, b.Value);
                }
            }

            return null;
        }

        public static int CountSameType(IReadOnlyList<TileId> hand, TileId targetTile)
        {
            int count = 0;
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i].TypeIndex == targetTile.TypeIndex)
                {
                    count++;
                }
            }

            return count;
        }

        public static bool IsFuriten(
            RoundStateMachine round,
            int seat,
            bool[]? tempFuriten,
            bool[]? riichiFuriten)
        {
            if (tempFuriten != null && seat >= 0 && seat < tempFuriten.Length && tempFuriten[seat])
            {
                return true;
            }

            if (riichiFuriten != null && seat >= 0 && seat < riichiFuriten.Length && riichiFuriten[seat])
            {
                return true;
            }

            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            if (hand.Count % 3 != 1)
            {
                return false;
            }

            IReadOnlyCollection<int> waits = HandDecomposer.CalculateWaits(hand);
            if (waits.Count == 0)
            {
                return false;
            }

            IReadOnlyList<TileId> discards = round.Tiles.TilesInZone(TileZone.Discard, seat);
            for (int i = 0; i < discards.Count; i++)
            {
                if (waits.Contains(discards[i].TypeIndex))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool CanRon(
            RoundStateMachine round,
            int seat,
            IReadOnlyList<TileId> hand,
            TileId targetTile,
            bool[]? tempFuriten,
            bool[]? riichiFuriten)
        {
            if (IsFuriten(round, seat, tempFuriten, riichiFuriten))
            {
                return false;
            }

            var testHand = new List<TileId>(hand.Count + 1);
            testHand.AddRange(hand);
            testHand.Add(targetTile);
            return HandDecomposer.CanWin(testHand);
        }

        public static IReadOnlyList<int> DeriveEligibleSeatsForDiscard(
            RoundStateMachine round,
            int discarder,
            TileId discard,
            bool[]? tempFuriten,
            bool[]? riichiFuriten)
        {
            var eligible = new List<int>();
            for (int seat = 0; seat < 4; seat++)
            {
                if (seat == discarder)
                {
                    continue;
                }

                IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
                if (hand.Count == 0)
                {
                    continue;
                }

                bool canRon = CanRon(round, seat, hand, discard, tempFuriten, riichiFuriten);
                bool canPon = !round.IsRiichi(seat) && CountSameType(hand, discard) >= 2;
                bool canOpenKan = !round.IsRiichi(seat) && CountSameType(hand, discard) >= 3 &&
                                  round.Tiles.WallRemaining > 0 &&
                                  round.Tiles.RinshanPointer < 4 &&
                                  round.Tiles.KanCount < 4;
                bool canChi = !round.IsRiichi(seat) && seat == (discarder + 1) % 4 && CanChi(hand, discard);

                if (canRon || canPon || canOpenKan || canChi)
                {
                    eligible.Add(seat);
                }
            }

            return eligible;
        }

        public static IReadOnlyList<int> DeriveEligibleSeatsForAddedKan(
            RoundStateMachine round,
            int kanser,
            TileId addedTile,
            bool[]? tempFuriten,
            bool[]? riichiFuriten)
        {
            var eligible = new List<int>();
            for (int seat = 0; seat < 4; seat++)
            {
                if (seat == kanser)
                {
                    continue;
                }

                IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
                if (hand.Count == 0)
                {
                    continue;
                }

                if (CanRon(round, seat, hand, addedTile, tempFuriten, riichiFuriten))
                {
                    eligible.Add(seat);
                }
            }

            return eligible;
        }

        public static IReadOnlyList<LegalActionOption> DeriveTurnCandidates(
            RoundStateMachine round,
            int seat,
            bool[]? tempFuriten,
            bool[]? riichiFuriten)
        {
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            if (hand.Count == 0)
            {
                return Array.Empty<LegalActionOption>();
            }

            var candidates = new List<LegalActionOption>();

            // 1. Discard
            if (round.IsRiichi(seat))
            {
                if (round.Tiles.DrawnTile.HasValue && round.Tiles.DrawnTile.Value.Seat == seat)
                {
                    candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, round.Tiles.DrawnTile.Value.Tile));
                }
                else
                {
                    candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, hand[hand.Count - 1]));
                }
            }
            else
            {
                for (int i = 0; i < hand.Count; i++)
                {
                    candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, hand[i]));
                }
            }

            // 2. Tsumo (Standard, Chiitoitsu, Kokushi Musou, Rinshan Kaihou)
            if (hand.Count % 3 == 2 && HandDecomposer.CanWin(hand))
            {
                TileId? drawn = round.Tiles.DrawnTile?.Seat == seat ? round.Tiles.DrawnTile?.Tile : null;
                candidates.Add(new LegalActionOption(LegalActionKind.Tsumo, seat, drawn));
            }

            // 3. Riichi
            if (!round.IsRiichi(seat) &&
                round.Scores[seat] >= 1000 &&
                round.Tiles.WallRemaining >= 4 &&
                round.Tiles.CountAt(TileZone.Meld, seat) == 0 &&
                hand.Count % 3 == 2)
            {
                var testedTypes = new HashSet<int>();
                for (int i = 0; i < hand.Count; i++)
                {
                    TileId t = hand[i];
                    if (!testedTypes.Add(t.TypeIndex))
                    {
                        continue;
                    }

                    if (round.Prepare(RoundCommand.Riichi(round.RoundId, seat, t, null)).IsReady)
                    {
                        candidates.Add(new LegalActionOption(LegalActionKind.Riichi, seat, t, "riichi-" + t.Value));
                    }
                }
            }

            // 4. Closed Kan (暗杠)
            if (round.Tiles.WallRemaining > 0 &&
                round.Tiles.RinshanPointer < 4 &&
                round.Tiles.KanCount < 4)
            {
                var countsByType = new Dictionary<int, List<TileId>>();
                for (int i = 0; i < hand.Count; i++)
                {
                    TileId t = hand[i];
                    if (!countsByType.TryGetValue(t.TypeIndex, out List<TileId>? list))
                    {
                        list = new List<TileId>(4);
                        countsByType[t.TypeIndex] = list;
                    }

                    list.Add(t);
                }

                foreach (KeyValuePair<int, List<TileId>> pair in countsByType)
                {
                    if (pair.Value.Count == 4)
                    {
                        RoundCommand kanCmd = RoundCommand.ClosedKan(
                            round.RoundId,
                            seat,
                            pair.Value[0],
                            pair.Value[1],
                            pair.Value[2],
                            pair.Value[3]);
                        if (round.Prepare(kanCmd).IsReady)
                        {
                            candidates.Add(new LegalActionOption(
                                LegalActionKind.Kan,
                                seat,
                                pair.Value[0],
                                "closed-kan-" + pair.Key));
                        }
                    }
                }
            }

            // 5. Added Kan (加杠/小明杠)
            if (!round.IsRiichi(seat) &&
                round.Tiles.WallRemaining > 0 &&
                round.Tiles.RinshanPointer < 4 &&
                round.Tiles.KanCount < 4 &&
                round.Melds.Count > 0)
            {
                var testedMeldTypes = new HashSet<int>();
                for (int m = 0; m < round.Melds.Count; m++)
                {
                    RoundMeld meld = round.Melds[m];
                    if (meld.Seat != seat || meld.Kind != RoundMeldKind.Pon)
                    {
                        continue;
                    }

                    int typeIndex = meld.Tiles[0].TypeIndex;
                    if (!testedMeldTypes.Add(typeIndex))
                    {
                        continue;
                    }

                    TileId? matchingInHand = null;
                    for (int i = 0; i < hand.Count; i++)
                    {
                        if (hand[i].TypeIndex == typeIndex)
                        {
                            matchingInHand = hand[i];
                            break;
                        }
                    }

                    if (matchingInHand.HasValue)
                    {
                        RoundCommand addedKanCmd = RoundCommand.AddedKan(round.RoundId, seat, matchingInHand.Value);
                        if (round.Prepare(addedKanCmd).IsReady)
                        {
                            candidates.Add(new LegalActionOption(
                                LegalActionKind.Kan,
                                seat,
                                matchingInHand.Value,
                                "added-kan-" + typeIndex));
                        }
                    }
                }
            }

            return candidates;
        }

        public static IReadOnlyList<LegalActionOption> DeriveResponseCandidates(
            RoundStateMachine round,
            int seat,
            RoundResponseWindow window,
            bool[]? tempFuriten,
            bool[]? riichiFuriten)
        {
            if (window == null || !window.EligibleSeats.Contains(seat) || window.HasResponded(seat))
            {
                return Array.Empty<LegalActionOption>();
            }

            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            var candidates = new List<LegalActionOption>();

            // 1. Ron
            if (CanRon(round, seat, hand, window.TargetTile, tempFuriten, riichiFuriten))
            {
                candidates.Add(new LegalActionOption(LegalActionKind.Ron, seat, window.TargetTile));
            }

            // 2. Calls (not in Chankan, and not in Riichi)
            if (!window.IsChankan && !round.IsRiichi(seat))
            {
                int count = CountSameType(hand, window.TargetTile);

                // Pon
                if (count >= 2)
                {
                    candidates.Add(new LegalActionOption(
                        LegalActionKind.Pon,
                        seat,
                        window.TargetTile,
                        "pon-" + window.TargetTile.TypeIndex));
                }

                // OpenKan
                if (count >= 3 &&
                    round.Tiles.WallRemaining > 0 &&
                    round.Tiles.RinshanPointer < 4 &&
                    round.Tiles.KanCount < 4)
                {
                    candidates.Add(new LegalActionOption(
                        LegalActionKind.Kan,
                        seat,
                        window.TargetTile,
                        "open-kan-" + window.TargetTile.TypeIndex));
                }

                // Chi
                if (seat == (window.FromSeat + 1) % 4 && CanChi(hand, window.TargetTile))
                {
                    candidates.Add(new LegalActionOption(
                        LegalActionKind.Chi,
                        seat,
                        window.TargetTile,
                        "chi-" + window.TargetTile.TypeIndex));
                }
            }

            // 3. Pass
            candidates.Add(new LegalActionOption(LegalActionKind.Pass, seat));

            return candidates;
        }
    }
}
