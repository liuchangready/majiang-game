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

        public static IReadOnlyList<(TileId a, TileId b)> FindAllChiCombinations(IReadOnlyList<TileId> hand, TileId targetTile)
        {
            if (!targetTile.IsNumberTile)
            {
                return Array.Empty<(TileId, TileId)>();
            }

            int rank = targetTile.Rank;
            TileSuit suit = targetTile.Suit;
            var list = new List<(TileId, TileId)>();

            List<TileId> FindAll(int r)
            {
                var matches = new List<TileId>();
                for (int i = 0; i < hand.Count; i++)
                {
                    TileId tile = hand[i];
                    if (tile.IsNumberTile && tile.Suit == suit && tile.Rank == r)
                    {
                        matches.Add(tile);
                    }
                }

                return matches;
            }

            // Pattern 1: targetTile is highest (rank-2, rank-1, target)
            if (rank >= 3)
            {
                List<TileId> list1 = FindAll(rank - 2);
                List<TileId> list2 = FindAll(rank - 1);
                foreach (TileId a in list1)
                {
                    foreach (TileId b in list2)
                    {
                        list.Add((a, b));
                    }
                }
            }

            // Pattern 2: targetTile is middle (rank-1, target, rank+1)
            if (rank >= 2 && rank <= 8)
            {
                List<TileId> list1 = FindAll(rank - 1);
                List<TileId> list2 = FindAll(rank + 1);
                foreach (TileId a in list1)
                {
                    foreach (TileId b in list2)
                    {
                        list.Add((a, b));
                    }
                }
            }

            // Pattern 3: targetTile is lowest (target, rank+1, rank+2)
            if (rank <= 7)
            {
                List<TileId> list1 = FindAll(rank + 1);
                List<TileId> list2 = FindAll(rank + 2);
                foreach (TileId a in list1)
                {
                    foreach (TileId b in list2)
                    {
                        list.Add((a, b));
                    }
                }
            }

            return list;
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

        public static Meld[] BuildMeldsForSeat(RoundStateMachine round, int seat)
        {
            var melds = new List<Meld>();
            for (int i = 0; i < round.Melds.Count; i++)
            {
                RoundMeld meld = round.Melds[i];
                if (meld.Seat != seat)
                {
                    continue;
                }

                MeldType type = meld.Kind switch
                {
                    RoundMeldKind.Chi => MeldType.Chi,
                    RoundMeldKind.Pon => MeldType.Pon,
                    RoundMeldKind.OpenKan => MeldType.OpenKan,
                    RoundMeldKind.ClosedKan => MeldType.ClosedKan,
                    RoundMeldKind.AddedKan => MeldType.AddedKan,
                    _ => throw new ArgumentOutOfRangeException(nameof(meld.Kind), meld.Kind, null)
                };

                melds.Add(new Meld(
                    type,
                    meld.Tiles,
                    meld.Kind == RoundMeldKind.ClosedKan ? -1 : meld.FromSeat,
                    meld.CalledTile));
            }

            return melds.ToArray();
        }

        public static bool HasWinningYaku(
            RoundStateMachine round,
            int seat,
            IReadOnlyList<TileId> hand,
            TileId winTile,
            bool isTsumo,
            bool? isChankan = null)
        {
            Meld[] melds = BuildMeldsForSeat(round, seat);
            bool isClosed = melds.All(m => !m.IsOpen);
            int seatWind = ((seat - round.DealerSeat + 4) % 4) + 1;
            int roundWind = round.RoundWind;
            bool isDealer = seat == round.DealerSeat;
            bool isRiichi = round.IsRiichi(seat);

            bool isRinshan = isTsumo &&
                             round.Tiles.DrawnTile.HasValue &&
                             round.Tiles.DrawnTile.Value.Seat == seat &&
                             round.Tiles.DrawnTile.Value.Source == DrawSource.Rinshan;
            bool isHaitei = isTsumo && !isRinshan && round.Tiles.WallRemaining == 0;
            bool isHoutei = !isTsumo && round.Tiles.WallRemaining == 0;
            bool chankanEffective = isChankan ?? (!isTsumo && round.Window != null && round.Window.IsChankan);

            PlayerHand playerHand;
            if (isTsumo)
            {
                var closed = new List<TileId>(hand.Count);
                bool removed = false;
                for (int i = 0; i < hand.Count; i++)
                {
                    if (!removed && hand[i] == winTile)
                    {
                        removed = true;
                        continue;
                    }
                    closed.Add(hand[i]);
                }
                playerHand = new PlayerHand(closed, melds, winTile);
            }
            else
            {
                playerHand = new PlayerHand(hand, melds);
            }

            var context = new WinContext(
                winTile,
                isTsumo,
                isClosed,
                seatWind,
                roundWind,
                isRiichi: isRiichi,
                isRinshan: isRinshan,
                isHaitei: isHaitei,
                isHoutei: isHoutei,
                isChankan: chankanEffective);

            return WinningScoreEvaluator.CanWinWithYaku(playerHand, context, isDealer);
        }

        public static bool CanRon(
            RoundStateMachine round,
            int seat,
            IReadOnlyList<TileId> hand,
            TileId targetTile,
            bool[]? tempFuriten,
            bool[]? riichiFuriten,
            bool? isChankan = null)
        {
            if (IsFuriten(round, seat, tempFuriten, riichiFuriten))
            {
                return false;
            }

            var testHand = new List<TileId>(hand.Count + 1);
            testHand.AddRange(hand);
            testHand.Add(targetTile);
            if (!HandDecomposer.CanWin(testHand))
            {
                return false;
            }

            return HasWinningYaku(round, seat, hand, targetTile, isTsumo: false, isChankan: isChankan);
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

                if (CanRon(round, seat, hand, addedTile, tempFuriten, riichiFuriten, isChankan: true))
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
                    TileId t = round.Tiles.DrawnTile.Value.Tile;
                    candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, t, tiles: new[] { t }));
                }
                else
                {
                    TileId t = hand[hand.Count - 1];
                    candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, t, tiles: new[] { t }));
                }
            }
            else
            {
                for (int i = 0; i < hand.Count; i++)
                {
                    TileId t = hand[i];
                    candidates.Add(new LegalActionOption(LegalActionKind.Discard, seat, t, tiles: new[] { t }));
                }
            }

            // 2. Tsumo (Standard, Chiitoitsu, Kokushi Musou, Rinshan Kaihou)
            if (round.CurrentSeat == seat &&
                round.Tiles.DrawnTile.HasValue &&
                round.Tiles.DrawnTile.Value.Seat == seat &&
                (round.Phase == RoundPhase.Draw || round.Phase == RoundPhase.CheatWindow || round.Phase == RoundPhase.Discard || round.Phase == RoundPhase.KanDraw) &&
                hand.Count % 3 == 2 &&
                HandDecomposer.CanWin(hand))
            {
                TileId drawn = round.Tiles.DrawnTile.Value.Tile;
                if (HasWinningYaku(round, seat, hand, drawn, isTsumo: true))
                {
                    candidates.Add(new LegalActionOption(LegalActionKind.Tsumo, seat, drawn, tiles: new[] { drawn }));
                }
            }

            // 3. Riichi
            if (!round.IsRiichi(seat) &&
                round.Scores[seat] >= 1000 &&
                round.Tiles.WallRemaining >= 4 &&
                round.Melds.All(m => m.Seat != seat || !m.IsOpen) &&
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
                        candidates.Add(new LegalActionOption(LegalActionKind.Riichi, seat, t, "riichi-" + t.Value, new[] { t }));
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
                                "closed-kan-" + pair.Key,
                                pair.Value.ToArray()));
                        }
                    }
                }
            }

            // 5. Added Kan (加杠/小明�?
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
                                "added-kan-" + typeIndex,
                                new[] { matchingInHand.Value, meld.Tiles[0], meld.Tiles[1], meld.Tiles[2] }));
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
            if (CanRon(round, seat, hand, window.TargetTile, tempFuriten, riichiFuriten, isChankan: window.IsChankan))
            {
                candidates.Add(new LegalActionOption(LegalActionKind.Ron, seat, window.TargetTile, tiles: new[] { window.TargetTile }));
            }

            // 2. Calls (not in Chankan, and not in Riichi)
            if (!window.IsChankan && !round.IsRiichi(seat))
            {
                var matching = new List<TileId>();
                for (int i = 0; i < hand.Count; i++)
                {
                    if (hand[i].TypeIndex == window.TargetTile.TypeIndex)
                    {
                        matching.Add(hand[i]);
                    }
                }

                // Pon: enumerate distinct pairs
                if (matching.Count >= 2)
                {
                    for (int i = 0; i < matching.Count; i++)
                    {
                        for (int j = i + 1; j < matching.Count; j++)
                        {
                            TileId a = matching[i];
                            TileId b = matching[j];
                            candidates.Add(new LegalActionOption(
                                LegalActionKind.Pon,
                                seat,
                                window.TargetTile,
                                "pon-" + a.Value + "-" + b.Value,
                                new[] { window.TargetTile, a, b }));
                        }
                    }
                }

                // OpenKan
                if (matching.Count >= 3 &&
                    round.Tiles.WallRemaining > 0 &&
                    round.Tiles.RinshanPointer < 4 &&
                    round.Tiles.KanCount < 4)
                {
                    candidates.Add(new LegalActionOption(
                        LegalActionKind.Kan,
                        seat,
                        window.TargetTile,
                        "open-kan-" + window.TargetTile.TypeIndex,
                        new[] { window.TargetTile, matching[0], matching[1], matching[2] }));
                }

                // Chi
                if (seat == (window.FromSeat + 1) % 4)
                {
                    IReadOnlyList<(TileId a, TileId b)> chiCombos = FindAllChiCombinations(hand, window.TargetTile);
                    for (int c = 0; c < chiCombos.Count; c++)
                    {
                        (TileId a, TileId b) = chiCombos[c];
                        candidates.Add(new LegalActionOption(
                            LegalActionKind.Chi,
                            seat,
                            window.TargetTile,
                            "chi-" + a.Value + "-" + b.Value,
                            new[] { window.TargetTile, a, b }));
                    }
                }
            }

            // 3. Pass
            candidates.Add(new LegalActionOption(LegalActionKind.Pass, seat, tiles: Array.Empty<TileId>()));

            return candidates;
        }
    }
}
