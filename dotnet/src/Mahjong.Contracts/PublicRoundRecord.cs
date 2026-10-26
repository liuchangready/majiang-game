using System;
using System.Collections.Generic;

namespace Mahjong.Contracts
{
    /// <summary>
    /// Immutable public settlement record for one completed round.
    ///
    /// This is a V5 public read model, not a wire DTO. It deliberately carries
    /// only the round identity, the four public seat scores and the four signed
    /// seat deltas. V4's access-layer record also carried
    /// finalCheatPoints/cheatDeltas; those fields are structurally absent here
    /// so a public spectator/history consumer cannot observe cheat balances or
    /// private ledger detail. Seeds, tiles, wall order, ura tiles, decisions and
    /// the full MatchSession are likewise absent.
    ///
    /// Every value is copied from the caller. This type never recomputes a
    /// settlement, adjusts a rank, rejects a legal negative score, or requires
    /// deltas to sum to zero: a riichi deposit and a negative final score are
    /// legal facts that must remain representable. It performs no dealing and
    /// changes no score.
    /// </summary>
    public sealed class PublicRoundRecord
    {
        private readonly IReadOnlyList<ScoreView> _finalScores;
        private readonly IReadOnlyList<ScoreView> _deltas;

        /// <summary>
        /// Creates a frozen public round record.
        ///
        /// Both row lists must contain exactly the four seats 0..3, each once.
        /// Rows may be supplied in any order; they are stored by seat, so
        /// <c>FinalScores[seat].Seat == seat</c> and <c>Deltas[seat].Seat == seat</c>.
        /// Duplicate, missing or null rows are rejected rather than silently
        /// sorted or de-duplicated. The input lists are copied and exposed as
        /// read-only: later caller mutation cannot change this record.
        /// </summary>
        /// <param name="roundId">Round identity. A default/blank value is rejected.</param>
        /// <param name="resultId">
        /// Round-result identity. Kept as a validated string to match the V4
        /// round-result identity; blank, oversized and control-character values
        /// are rejected. This is not a second public session result.
        /// </param>
        /// <param name="finalScores">Four public final score rows, one per seat.</param>
        /// <param name="deltas">
        /// Four signed net score deltas, one per seat. <see cref="ScoreView.Score"/>
        /// holds the delta for this parameter; it is not a score total and may be
        /// negative. No zero-sum rule is applied.
        /// </param>
        public PublicRoundRecord(
            RoundId roundId,
            string resultId,
            IReadOnlyList<ScoreView> finalScores,
            IReadOnlyList<ScoreView> deltas)
        {
            if (roundId.IsEmpty)
            {
                throw new ArgumentException("Round id must not be empty.", nameof(roundId));
            }

            string checkedResultId = ProtocolText.RequireId(resultId, nameof(resultId));

            _finalScores = CopySeatRows(finalScores, nameof(finalScores), "finalScores");
            _deltas = CopySeatRows(deltas, nameof(deltas), "deltas");

            RoundId = roundId;
            ResultId = checkedResultId;
        }

        public RoundId RoundId { get; }

        public string ResultId { get; }

        /// <summary>
        /// Public final scores indexed by seat (0..3), copied from the input.
        /// The list and its rows are read-only.
        /// </summary>
        public IReadOnlyList<ScoreView> FinalScores => _finalScores;

        /// <summary>
        /// Signed net score deltas indexed by seat (0..3). Each
        /// <see cref="ScoreView.Score"/> is a delta, not a total; negative values
        /// and a non-zero sum are accepted because riichi deposits and other
        /// public transfers are legal.
        /// </summary>
        public IReadOnlyList<ScoreView> Deltas => _deltas;

        private static IReadOnlyList<ScoreView> CopySeatRows(
            IReadOnlyList<ScoreView>? values,
            string parameterName,
            string label)
        {
            if (values == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (values.Count != ProtocolConstants.MaxSeat + 1)
            {
                throw new ArgumentException(
                    label + " must contain exactly four seat rows.",
                    parameterName);
            }

            var rows = new ScoreView[ProtocolConstants.MaxSeat + 1];
            var seen = new bool[ProtocolConstants.MaxSeat + 1];
            for (int i = 0; i < values.Count; i++)
            {
                ScoreView row = values[i];
                if (row == null)
                {
                    throw new ArgumentException(label + " cannot contain null rows.", parameterName);
                }

                int seat = row.Seat;
                if (seen[seat])
                {
                    throw new ArgumentException(
                        label + " contains duplicate seat " + seat + ".",
                        parameterName);
                }

                seen[seat] = true;
                rows[seat] = row;
            }

            for (int seat = 0; seat < seen.Length; seat++)
            {
                if (!seen[seat])
                {
                    throw new ArgumentException(
                        label + " is missing seat " + seat + ".",
                        parameterName);
                }
            }

            return Array.AsReadOnly(rows);
        }
    }
}
