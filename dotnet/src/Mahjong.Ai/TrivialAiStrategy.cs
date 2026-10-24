using System;
using System.Collections.Generic;
using System.Threading;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// Deterministic baseline strategy ported from V4
    /// <c>engine/src/main/kotlin/com/majiang/ai/TrivialAIStrategy.kt</c> (ENG-014).
    ///
    /// Branch order is the V4 source of record, not the class comment. The V4 KDoc claims
    /// "永不副露/立直", but the code never rejects those actions; they are only skipped
    /// because a higher-priority branch (win, then any discard, then pass, then first legal)
    /// runs first. When a response window offers only meld/riichi calls plus a pass, that
    /// ordering yields the pass, which is what the V4 KDoc observed. This port keeps the
    /// source behaviour, including its fallback boundaries:
    /// <list type="number">
    /// <item>first Ron candidate, then first Tsumo candidate ("能和就和");</item>
    /// <item>when any discard candidate exists: first discard of the drawn tile, else first
    /// discard of the closed hand's last tile, else the last discard in list order;</item>
    /// <item>first Pass candidate;</item>
    /// <item>first legal candidate of any remaining kind (meld, riichi, cheat, ...).</item>
    /// </list>
    /// V4 labelled both this class and <see cref="TsumogiriAiStrategy"/> with the display
    /// name "摸切AI". That label is a string only; here the algorithm identity enters the
    /// recorded intent hash, so the two strategies report distinct, stable
    /// <see cref="AlgorithmId"/> values.
    /// </summary>
    public sealed class TrivialAiStrategy : IAiStrategy
    {
        /// <summary>Stable algorithm identity recorded in every intent this strategy produces.</summary>
        public const string AlgorithmIdValue = "baseline-trivial";

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
                observation.MyHandTileIds,
                observation.MyDrawnTileId,
                cancellationToken);
            if (choice == null)
            {
                // Unreachable through AiObservation: its constructor rejects an empty
                // candidate list. Kept explicit so the strategy can never invent an action.
                throw new InvalidOperationException(
                    "TrivialAiStrategy found no legal candidate; an AiObservation always carries at least one.");
            }

            return choice;
        }

        /// <summary>
        /// V4-shaped nullable entry point over raw candidate lists. Returns null exactly when
        /// V4's <c>decideAction</c> returned null: an empty legal list. Unlike
        /// <see cref="Decide"/>, this entry accepts any candidate list, so the documented
        /// fallback arms are directly observable without forging an observation.
        /// The hand list mirrors V4 <c>view.myClosedTiles</c>: it excludes the drawn tile.
        /// </summary>
        public AiStrategicChoice? TryChoose(
            IReadOnlyList<LegalActionOption> legalActions,
            IReadOnlyList<TileId> myClosedTiles,
            TileId? drawnTileId,
            CancellationToken cancellationToken)
        {
            if (legalActions == null)
            {
                throw new ArgumentNullException(nameof(legalActions));
            }

            if (myClosedTiles == null)
            {
                throw new ArgumentNullException(nameof(myClosedTiles));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (legalActions.Count == 0)
            {
                // V4: legalActions.isEmpty() -> null.
                return null;
            }

            // V4 branch 1: first DeclareRon, then first DeclareTsumo.
            LegalActionOption? ron = FirstOfKind(legalActions, LegalActionKind.Ron, cancellationToken);
            if (ron != null)
            {
                return new AiStrategicChoice(ron, BaselineScore);
            }

            LegalActionOption? tsumo = FirstOfKind(legalActions, LegalActionKind.Tsumo, cancellationToken);
            if (tsumo != null)
            {
                return new AiStrategicChoice(tsumo, BaselineScore);
            }

            // V4 branch 2: discard selection, only when at least one discard exists.
            LegalActionOption? drawnMatch = null;
            if (drawnTileId.HasValue)
            {
                drawnMatch = FirstDiscardOfTile(legalActions, drawnTileId.Value, cancellationToken);
            }

            if (drawnMatch != null)
            {
                return new AiStrategicChoice(drawnMatch, BaselineScore);
            }

            LegalActionOption? lastClosedMatch = null;
            if (myClosedTiles.Count > 0)
            {
                TileId lastClosed = myClosedTiles[myClosedTiles.Count - 1];
                lastClosedMatch = FirstDiscardOfTile(legalActions, lastClosed, cancellationToken);
            }

            if (lastClosedMatch != null)
            {
                return new AiStrategicChoice(lastClosedMatch, BaselineScore);
            }

            LegalActionOption? lastDiscard = LastDiscard(legalActions, cancellationToken);
            if (lastDiscard != null)
            {
                return new AiStrategicChoice(lastDiscard, BaselineScore);
            }

            // V4 branch 3: first Pass.
            LegalActionOption? pass = FirstOfKind(legalActions, LegalActionKind.Pass, cancellationToken);
            if (pass != null)
            {
                return new AiStrategicChoice(pass, BaselineScore);
            }

            // V4 branch 4: first legal action of any kind.
            return new AiStrategicChoice(legalActions[0], BaselineScore);
        }

        /// <summary>
        /// Constant, decision-independent text. Never includes observation, hand or tile
        /// data, so logging a strategy instance cannot leak hidden state.
        /// </summary>
        public override string ToString() =>
            "TrivialAiStrategy/algorithm=" + AlgorithmIdValue +
            "/version=" + AlgorithmVersionValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static LegalActionOption? FirstOfKind(
            IReadOnlyList<LegalActionOption> legalActions,
            LegalActionKind kind,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (legalActions[i].Kind == kind)
                {
                    return legalActions[i];
                }
            }

            return null;
        }

        /// <summary>
        /// First discard of the exact physical tile. V4 compared <c>Tile</c> data-class
        /// equality (suit, rank and copyIndex), so a red five (copy 3) never matches a normal
        /// five copy; <see cref="TileId"/> value equality preserves that entity identity.
        /// </summary>
        private static LegalActionOption? FirstDiscardOfTile(
            IReadOnlyList<LegalActionOption> legalActions,
            TileId tile,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LegalActionOption candidate = legalActions[i];
                if (candidate.Kind == LegalActionKind.Discard &&
                    candidate.Tile.HasValue &&
                    candidate.Tile.Value == tile)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static LegalActionOption? LastDiscard(
            IReadOnlyList<LegalActionOption> legalActions,
            CancellationToken cancellationToken)
        {
            LegalActionOption? last = null;
            for (int i = 0; i < legalActions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (legalActions[i].Kind == LegalActionKind.Discard)
                {
                    last = legalActions[i];
                }
            }

            return last;
        }
    }
}
