using System;
using System.Collections.Generic;
using System.Threading;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Deterministic tsumogiri (pure discard-drawn) baseline strategy ported from V4
    /// <c>engine/src/main/kotlin/com/majiang/ai/TsumogiriAIStrategy.kt</c> (ENG-015).
    ///
    /// Branch order is the V4 source of record. Unlike <see cref="TrivialAiStrategy"/> this
    /// strategy never wins on its own: Pass precedes every discard branch and there is no
    /// Ron/Tsumo scan, so with a win candidate present it still passes. Its discard selection
    /// also differs: when no drawn tile matches it takes the first discard in list order,
    /// while Trivial matches the closed hand's last tile and then falls back to the last
    /// discard. The two therefore must not be merged into one strategy.
    /// <list type="number">
    /// <item>first Pass of the viewing seat, else first Pass of any seat;</item>
    /// <item>when a drawn tile exists: first discard of that tile from the viewing seat, else
    /// first discard of that tile from any seat;</item>
    /// <item>first discard of the viewing seat, else first discard of any seat;</item>
    /// <item>first legal candidate of any remaining kind.</item>
    /// </list>
    /// The "any seat" fallback arms mirror the V4 source one-to-one. On the
    /// <see cref="IAiStrategy"/> path they are unreachable by construction:
    /// <see cref="AiObservation"/> rejects any candidate whose seat differs from the viewing
    /// seat, so a cross-seat candidate can never reach this strategy. The arms stay because
    /// they are part of the ported behaviour; <see cref="TryChoose"/> keeps them testable
    /// over raw lists without weakening the observation contract.
    /// </summary>
    public sealed class TsumogiriAiStrategy : IAiStrategy
    {
        /// <summary>Stable algorithm identity recorded in every intent this strategy produces.</summary>
        public const string AlgorithmIdValue = "baseline-tsumogiri";

        /// <summary>Stable algorithm version recorded beside <see cref="AlgorithmIdValue"/>.</summary>
        public const int AlgorithmVersionValue = 1;

        /// <summary>
        /// Baseline strategies pick a single deterministic candidate instead of ranking, so
        /// the score is a constant. It is never recorded in <see cref="CommandIntent"/>.
        /// </summary>
        private const double BaselineScore = 0d;

        public string AlgorithmId => AlgorithmIdValue;

        public int AlgorithmVersion => AlgorithmVersionValue;

        /// <summary>
        /// Runs the strategy over one per-seat observation. <see cref="AiObservation"/>
        /// guarantees at least one legal candidate, so this never fabricates a choice and
        /// never returns null; the null arm below is unreachable for a validated observation
        /// and surfaces as a failed operation instead of an invented action.
        /// </summary>
        public AiStrategicChoice Decide(AiObservation observation, CancellationToken cancellationToken)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            AiStrategicChoice? choice = TryChoose(
                observation.LegalActions,
                observation.Seat,
                observation.MyDrawnTileId,
                cancellationToken);
            if (choice == null)
            {
                // Unreachable through AiObservation: its constructor rejects an empty
                // candidate list. Kept explicit so the strategy can never invent an action.
                throw new InvalidOperationException(
                    "TsumogiriAiStrategy found no legal candidate; an AiObservation always carries at least one.");
            }

            return choice;
        }

        /// <summary>
        /// V4-shaped nullable entry point over raw candidate lists. Returns null exactly when
        /// V4's <c>decideAction</c> returned null: an empty legal list. The viewer seat is the
        /// V4 <c>view.viewerSeat</c> filter used by every tier of this strategy.
        /// </summary>
        public AiStrategicChoice? TryChoose(
            IReadOnlyList<LegalActionOption> legalActions,
            int viewerSeat,
            TileId? drawnTileId,
            CancellationToken cancellationToken)
        {
            if (legalActions == null)
            {
                throw new ArgumentNullException(nameof(legalActions));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (legalActions.Count == 0)
            {
                // V4: legalActions.isEmpty() -> null.
                return null;
            }

            // V4 branch 1: first Pass of the viewer, else first Pass of any seat.
            LegalActionOption? pass = First(legalActions, IsPassOfSeat(viewerSeat), cancellationToken) ??
                First(legalActions, IsPass, cancellationToken);
            if (pass != null)
            {
                return new AiStrategicChoice(pass, BaselineScore);
            }

            // V4 branch 2: tsumogiri — discard the drawn tile, viewer first.
            if (drawnTileId.HasValue)
            {
                LegalActionOption? tsumogiri =
                    First(legalActions, IsDiscardOfSeatAndTile(viewerSeat, drawnTileId.Value), cancellationToken) ??
                    First(legalActions, IsDiscardOfTile(drawnTileId.Value), cancellationToken);
                if (tsumogiri != null)
                {
                    return new AiStrategicChoice(tsumogiri, BaselineScore);
                }
            }

            // V4 branch 3: first discard of the viewer, else first discard of any seat.
            LegalActionOption? discard =
                First(legalActions, IsDiscardOfSeat(viewerSeat), cancellationToken) ??
                First(legalActions, IsDiscard, cancellationToken);
            if (discard != null)
            {
                return new AiStrategicChoice(discard, BaselineScore);
            }

            // V4 branch 4: first legal action of any kind.
            return new AiStrategicChoice(legalActions[0], BaselineScore);
        }

        /// <summary>
        /// Constant, decision-independent text. Never includes observation, hand or tile
        /// data, so logging a strategy instance cannot leak hidden state.
        /// </summary>
        public override string ToString() =>
            "TsumogiriAiStrategy/algorithm=" + AlgorithmIdValue +
            "/version=" + AlgorithmVersionValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static LegalActionOption? First(
            IReadOnlyList<LegalActionOption> legalActions,
            Func<LegalActionOption, bool> predicate,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (predicate(legalActions[i]))
                {
                    return legalActions[i];
                }
            }

            return null;
        }

        private static bool IsPass(LegalActionOption candidate) => candidate.Kind == LegalActionKind.Pass;

        private static Func<LegalActionOption, bool> IsPassOfSeat(int seat) =>
            candidate => candidate.Kind == LegalActionKind.Pass && candidate.Seat == seat;

        private static bool IsDiscard(LegalActionOption candidate) => candidate.Kind == LegalActionKind.Discard;

        private static Func<LegalActionOption, bool> IsDiscardOfSeat(int seat) =>
            candidate => candidate.Kind == LegalActionKind.Discard && candidate.Seat == seat;

        /// <summary>
        /// Physical tile identity: V4 compared <c>Tile</c> data-class equality (suit, rank and
        /// copyIndex), so a red five (copy 3) never matches a normal five copy;
        /// <see cref="TileId"/> value equality preserves that entity distinction.
        /// </summary>
        private static Func<LegalActionOption, bool> IsDiscardOfTile(TileId tile) =>
            candidate => candidate.Kind == LegalActionKind.Discard &&
                         candidate.Tile.HasValue &&
                         candidate.Tile.Value == tile;

        private static Func<LegalActionOption, bool> IsDiscardOfSeatAndTile(int seat, TileId tile) =>
            candidate => candidate.Kind == LegalActionKind.Discard &&
                         candidate.Seat == seat &&
                         candidate.Tile.HasValue &&
                         candidate.Tile.Value == tile;
    }
}
