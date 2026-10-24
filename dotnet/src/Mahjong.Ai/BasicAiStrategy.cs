using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Mahjong.Domain;
using Mahjong.Domain.Rules;

namespace Mahjong.Ai
{
    /// <summary>
    /// Port of V4 <c>engine/src/main/kotlin/com/majiang/ai/AIStrategy.kt</c>
    /// <c>BasicAIStrategy</c> (ENG-013). The branch order is the V4 source of record:
    /// <list type="number">
    /// <item>first Ron candidate;</item>
    /// <item>first Tsumo candidate;</item>
    /// <item>first candidate explicitly classified as a 九种九牌 abortive draw;</item>
    /// <item>when the viewer's score is at least 1000: the Riichi candidate whose discard
    /// leaves the largest number of wait <em>types</em>, first list position on a tie;</item>
    /// <item>first closed-kan candidate (never an open or added kan);</item>
    /// <item>first Pon candidate when the public last discard is a yakuhai;</item>
    /// <item>the first Pass when the response window also offers Chi/Pon/Kan/Ron;</item>
    /// <item>when the viewer is in riichi: the discard of the exact physical drawn tile;</item>
    /// <item>the discard with the largest integer disposability, first list position on a
    /// tie;</item>
    /// <item>the previously found Pass, otherwise the first legal candidate.</item>
    /// </list>
    ///
    /// It consumes only <see cref="AiObservation"/> plus <see cref="BasicAiContext"/>. It
    /// never reads a wall, opponent hand, seed, clock or authority object. Waits are computed
    /// with the existing <see cref="HandDecomposer"/>; no separate rule implementation is
    /// introduced. The integer/waits-count heuristic is returned only as the non-authoritative
    /// <see cref="AiStrategicChoice.HeuristicScore"/> and is excluded from
    /// <see cref="CommandIntent.AuthorityHash"/>.
    /// </summary>
    public sealed class BasicAiStrategy : IAiStrategy
    {
        /// <summary>Stable algorithm identity recorded in every intent this strategy produces.</summary>
        public const string AlgorithmIdValue = "baseline-basic";

        /// <summary>Stable algorithm version recorded beside <see cref="AlgorithmIdValue"/>.</summary>
        public const int AlgorithmVersionValue = 1;

        private const double NeutralScore = 0d;

        public string AlgorithmId => AlgorithmIdValue;

        public int AlgorithmVersion => AlgorithmVersionValue;

        public AiStrategicChoice Decide(AiObservation observation, CancellationToken cancellationToken)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            // Cancellation wins over a missing/invalid context: a cancelled reasoning request
            // must not be converted into a contract error or a fabricated action.
            cancellationToken.ThrowIfCancellationRequested();

            BasicAiContext context = observation.BasicContext ??
                throw new BasicAiContextException(
                    "BasicAiStrategy requires AiObservation.BasicContext (BasicAiContext). " +
                    "Missing explicit facts: own riichi status, public last discard, " +
                    "and per-candidate closed/open/added-kan and abortive-draw metadata.");

            AiStrategicChoice? choice = TryChoose(
                observation.LegalActions,
                context,
                observation.MyHandTileIds,
                observation.MyDrawnTileId,
                observation.DealerSeat,
                observation.RoundWind,
                observation.Scores,
                cancellationToken);
            if (choice == null)
            {
                // Unreachable through AiObservation: its constructor rejects an empty
                // candidate list. Kept explicit so the strategy can never invent an action.
                throw new InvalidOperationException(
                    "BasicAiStrategy found no legal candidate; an AiObservation always carries at least one.");
            }

            return choice;
        }

        /// <summary>
        /// V4-shaped nullable entry point over raw candidate lists. It returns null exactly
        /// when V4's <c>decideAction</c> returned null: an empty legal list.
        ///
        /// <paramref name="myClosedTiles"/> may be V4's closed-only hand (with the drawn tile
        /// supplied separately) or the V5 runtime's hand-zone list that already contains the
        /// drawn tile. <see cref="AllClosedTiles"/> normalises both representations without
        /// duplicating a physical tile, so the wait/disposability inputs are always V4's
        /// <c>myAllClosedTiles</c>.
        /// </summary>
        public AiStrategicChoice? TryChoose(
            IReadOnlyList<LegalActionOption> legalActions,
            BasicAiContext context,
            IReadOnlyList<TileId> myClosedTiles,
            TileId? myDrawnTile,
            int dealerSeat,
            int roundWind,
            IReadOnlyList<long> scores,
            CancellationToken cancellationToken)
        {
            if (legalActions == null)
            {
                throw new ArgumentNullException(nameof(legalActions));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (myClosedTiles == null)
            {
                throw new ArgumentNullException(nameof(myClosedTiles));
            }

            if (scores == null)
            {
                throw new ArgumentNullException(nameof(scores));
            }

            AiGuard.CheckSeat(dealerSeat, nameof(dealerSeat));
            if (roundWind < 1 || roundWind > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(roundWind), roundWind, "Round wind must be 1..3.");
            }

            if (scores.Count != 4)
            {
                throw new ArgumentException("Scores must contain exactly four seats.", nameof(scores));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (legalActions.Count == 0)
            {
                // V4: legalActions.isEmpty() -> null.
                return null;
            }

            ValidateCandidateFacts(legalActions, context);
            var allClosed = AllClosedTiles(myClosedTiles, myDrawnTile);

            // 1. Win is always first: Ron, then Tsumo.
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (legalActions[i].Kind == LegalActionKind.Ron)
                {
                    return new AiStrategicChoice(legalActions[i], NeutralScore);
                }
            }

            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (legalActions[i].Kind == LegalActionKind.Tsumo)
                {
                    return new AiStrategicChoice(legalActions[i], NeutralScore);
                }
            }

            // 2. 九种九牌 abortive draw, selected only from explicit candidate metadata.
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption candidate = legalActions[i];
                if (candidate.Kind == LegalActionKind.Other &&
                    context.TryGetFacts(candidate, out BasicCandidateFacts facts) &&
                    facts.AbortiveDraw.HasValue)
                {
                    return new AiStrategicChoice(candidate, NeutralScore);
                }
            }

            // 3. Riichi: only when the viewer has at least 1000 points. The chosen discard
            // maximises the number of wait types after removing exactly one physical copy.
            LegalActionOption? bestRiichi = null;
            int bestRiichiWaits = -1;
            if (scores[context.Seat] >= 1000L)
            {
                for (int i = 0; i < legalActions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LegalActionOption candidate = legalActions[i];
                    if (candidate.Kind != LegalActionKind.Riichi)
                    {
                        continue;
                    }

                    TileId discard = RequireTile(candidate, "Riichi");
                    int waits = WaitsAfterDiscard(allClosed, discard, cancellationToken);
                    if (waits > bestRiichiWaits)
                    {
                        bestRiichi = candidate;
                        bestRiichiWaits = waits;
                    }
                }
            }

            if (bestRiichi != null)
            {
                return new AiStrategicChoice(bestRiichi, bestRiichiWaits);
            }

            // 4. Kan: only an explicitly closed kan; open and added kan fall through.
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption candidate = legalActions[i];
                if (candidate.Kind == LegalActionKind.Kan &&
                    context.TryGetFacts(candidate, out BasicCandidateFacts facts) &&
                    facts.KanKind == BasicKanKind.Closed)
                {
                    return new AiStrategicChoice(candidate, NeutralScore);
                }
            }

            // 5. Pon a yakuhai last discard. Seat wind and round wind are compared as ranks
            // exactly as V4's isYakuhai did.
            int seatWind = SeatWind(context.Seat, dealerSeat);
            LegalActionOption? firstPon = null;
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (legalActions[i].Kind == LegalActionKind.Pon)
                {
                    firstPon = legalActions[i];
                    break;
                }
            }

            if (firstPon != null &&
                context.LastDiscard.HasValue &&
                IsYakuhai(context.LastDiscard.Value, seatWind, roundWind))
            {
                return new AiStrategicChoice(firstPon, NeutralScore);
            }

            // 6. Response-window Pass. V4 requires both a Pass and another Chi/Pon/Kan/Ron
            // candidate; a Pass beside only a discard does not take this branch.
            LegalActionOption? passAction = null;
            bool hasMeldOrWinChoice = false;
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption candidate = legalActions[i];
                if (candidate.Kind == LegalActionKind.Pass && passAction == null)
                {
                    passAction = candidate;
                }

                if (candidate.Kind == LegalActionKind.Chi ||
                    candidate.Kind == LegalActionKind.Pon ||
                    candidate.Kind == LegalActionKind.Kan ||
                    candidate.Kind == LegalActionKind.Ron)
                {
                    hasMeldOrWinChoice = true;
                }
            }

            if (passAction != null && hasMeldOrWinChoice)
            {
                return new AiStrategicChoice(passAction, NeutralScore);
            }

            // 7. Discard decisions.
            bool hasDiscard = false;
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (legalActions[i].Kind == LegalActionKind.Discard)
                {
                    hasDiscard = true;
                    break;
                }
            }

            if (hasDiscard)
            {
                // 7a. A riichi player discards the exact physical drawn tile. Copy identity
                // matters: a red five never matches the normal five of the same type.
                if (context.IsRiichi && myDrawnTile.HasValue)
                {
                    for (int i = 0; i < legalActions.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        LegalActionOption candidate = legalActions[i];
                        if (candidate.Kind == LegalActionKind.Discard &&
                            candidate.Tile.HasValue &&
                            candidate.Tile.Value == myDrawnTile.Value)
                        {
                            return new AiStrategicChoice(candidate, NeutralScore);
                        }
                    }
                }

                // 7b. Integer disposability ranking. maxBy keeps the first candidate on a
                // tie, so a strict greater-than comparison is required.
                LegalActionOption? bestDiscard = null;
                int bestScore = int.MinValue;
                for (int i = 0; i < legalActions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LegalActionOption candidate = legalActions[i];
                    if (candidate.Kind != LegalActionKind.Discard)
                    {
                        continue;
                    }

                    TileId tile = RequireTile(candidate, "Discard");
                    int score = TileDisposability(tile, allClosed, seatWind, roundWind);
                    if (bestDiscard == null || score > bestScore)
                    {
                        bestDiscard = candidate;
                        bestScore = score;
                    }
                }

                if (bestDiscard != null)
                {
                    return new AiStrategicChoice(bestDiscard, bestScore);
                }
            }

            // 8. V4 final fallback: the Pass found earlier, else the first legal action.
            if (passAction != null)
            {
                return new AiStrategicChoice(passAction, NeutralScore);
            }

            return new AiStrategicChoice(legalActions[0], NeutralScore);
        }

        /// <summary>
        /// Constant, decision-independent text. Never includes observation, hand or tile
        /// data, so logging a strategy instance cannot leak hidden state.
        /// </summary>
        public override string ToString() =>
            "BasicAiStrategy/algorithm=" + AlgorithmIdValue +
            "/version=" + AlgorithmVersionValue.ToString(CultureInfo.InvariantCulture);

        private static void ValidateCandidateFacts(
            IReadOnlyList<LegalActionOption> legalActions,
            BasicAiContext context)
        {
            for (int i = 0; i < legalActions.Count; i++)
            {
                LegalActionOption candidate = legalActions[i];
                if (candidate.Kind == LegalActionKind.Kan)
                {
                    if (!context.TryGetFacts(candidate, out BasicCandidateFacts facts) ||
                        !facts.KanKind.HasValue)
                    {
                        throw new BasicAiContextException(
                            "BasicAiContext is missing an explicit physical kan kind for a Kan candidate " +
                            "(option=" + DisplayOption(candidate) + "). " +
                            "An open or added kan must not be treated as a closed kan.");
                    }
                }
                else if (candidate.Kind == LegalActionKind.Other)
                {
                    if (!context.TryGetFacts(candidate, out _))
                    {
                        throw new BasicAiContextException(
                            "BasicAiContext is missing an explicit abortive-draw classification for an " +
                            "Other candidate (option=" + DisplayOption(candidate) + "). " +
                            "An Other candidate must not be assumed to be 九种九牌.");
                    }
                }
            }
        }

        /// <summary>
        /// V4 <c>myAllClosedTiles</c>. The V5 runtime stores the drawn tile in the hand zone,
        /// while the V4 PlayerView kept it separate; appending only when absent supports both
        /// representations and never duplicates a physical tile.
        /// </summary>
        private static List<TileId> AllClosedTiles(
            IReadOnlyList<TileId> myClosedTiles,
            TileId? myDrawnTile)
        {
            var allClosed = new List<TileId>(myClosedTiles.Count + (myDrawnTile.HasValue ? 1 : 0));
            for (int i = 0; i < myClosedTiles.Count; i++)
            {
                allClosed.Add(myClosedTiles[i]);
            }

            if (myDrawnTile.HasValue)
            {
                bool present = false;
                for (int i = 0; i < myClosedTiles.Count; i++)
                {
                    if (myClosedTiles[i] == myDrawnTile.Value)
                    {
                        present = true;
                        break;
                    }
                }

                if (!present)
                {
                    allClosed.Add(myDrawnTile.Value);
                }
            }

            return allClosed;
        }

        private static int WaitsAfterDiscard(
            IReadOnlyList<TileId> allClosed,
            TileId discard,
            CancellationToken cancellationToken)
        {
            var remaining = new List<TileId>(allClosed);
            cancellationToken.ThrowIfCancellationRequested();

            // Kotlin's MutableList.remove(element) removes the first equal element. V4 Tile
            // equality included copyIndex, so this is one physical copy, not every tile of
            // the same type.
            for (int i = 0; i < remaining.Count; i++)
            {
                if (remaining[i] == discard)
                {
                    remaining.RemoveAt(i);
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return HandDecomposer.CalculateWaits(remaining).Count;
        }

        private static int SeatWind(int seat, int dealerSeat) =>
            ((seat - dealerSeat + 4) % 4) + 1;

        private static bool IsYakuhai(TileId tile, int seatWind, int roundWind)
        {
            if (tile.Suit == TileSuit.Dragon)
            {
                return true;
            }

            if (tile.Suit == TileSuit.Wind)
            {
                return tile.Rank == seatWind || tile.Rank == roundWind;
            }

            return false;
        }

        /// <summary>
        /// V4 <c>tileDisposability</c> as an integer. It uses physical copy identity for the
        /// neighbour exclusion (<c>it != tile</c>): another copy of the same type is a
        /// different physical tile and therefore counts as a neighbour at distance zero.
        /// </summary>
        private static int TileDisposability(
            TileId tile,
            IReadOnlyList<TileId> allClosed,
            int seatWind,
            int roundWind)
        {
            int score = 0;

            int sameTypeCount = 0;
            for (int i = 0; i < allClosed.Count; i++)
            {
                if (allClosed[i].TypeIndex == tile.TypeIndex)
                {
                    sameTypeCount++;
                }
            }

            if (tile.IsHonor && sameTypeCount == 1)
            {
                bool yakuhai = tile.Suit == TileSuit.Dragon ||
                    (tile.Suit == TileSuit.Wind &&
                     (tile.Rank == seatWind || tile.Rank == roundWind));
                score += yakuhai ? 5 : 15;
            }

            if (tile.IsTerminal && sameTypeCount == 1)
            {
                score += 10;
            }

            if (sameTypeCount >= 2)
            {
                score -= 20;
            }

            if (sameTypeCount >= 3)
            {
                score -= 40;
            }

            if (tile.IsNumberTile)
            {
                bool hasNeighbor = false;
                for (int i = 0; i < allClosed.Count; i++)
                {
                    TileId other = allClosed[i];
                    if (other.Suit == tile.Suit &&
                        other != tile &&
                        Math.Abs(other.Rank - tile.Rank) <= 2)
                    {
                        hasNeighbor = true;
                        break;
                    }
                }

                if (hasNeighbor)
                {
                    score -= 10;
                }
            }

            if (tile.IsNumberTile && (tile.Rank == 1 || tile.Rank == 2 || tile.Rank == 8 || tile.Rank == 9))
            {
                score += 3;
            }

            return score;
        }

        private static TileId RequireTile(LegalActionOption candidate, string role)
        {
            if (!candidate.Tile.HasValue)
            {
                throw new BasicAiContextException(
                    "A " + role + " candidate must carry a physical tile (option=" +
                    DisplayOption(candidate) + ").");
            }

            return candidate.Tile.Value;
        }

        private static string DisplayOption(LegalActionOption candidate) =>
            candidate.OptionId ?? "<none>";
    }
}
