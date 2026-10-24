using System;
using System.Collections.Generic;

namespace Mahjong.Contracts
{
    /// <summary>
    /// Immutable public history for one match session.
    ///
    /// The history is the ordered list of completed public round records plus
    /// an optional final summary. The order is exactly the caller's order:
    /// this type does not sort by round id, wind, honba or result id, and it
    /// does not silently de-duplicate adjacent or non-adjacent entries.
    /// Duplicate round ids, duplicate result ids and round/result identity
    /// collisions are rejected before a record is stored, so a frozen history
    /// cannot contain the same round twice or attach one result to two rounds.
    ///
    /// The optional summary reuses the existing
    /// <see cref="PublicSessionSummary"/> for final scores/rankings. No second
    /// session-result type and no duplicate wind/round-number/honba game state
    /// are introduced. An unfinished match is represented by an empty history
    /// and/or a null summary, not by inventing a partial settlement.
    ///
    /// This read model does not carry cheat balances or details, seeds, tiles,
    /// wall/ura order, private ledgers or a MatchSession. It is not a claim that
    /// the V5 wire protocol already carries it; projection, persistence and
    /// wire evolution remain downstream responsibilities.
    /// </summary>
    public sealed class PublicSessionHistory
    {
        private readonly IReadOnlyList<PublicRoundRecord> _rounds;

        /// <summary>
        /// Creates a frozen public session history.
        /// </summary>
        /// <param name="sessionId">Session identity. A default/blank value is rejected.</param>
        /// <param name="rounds">
        /// Completed rounds in caller-supplied chronological order. May be empty
        /// for a match with no completed round. The list is copied; each record
        /// is already immutable. Null entries, duplicate round ids, duplicate
        /// result ids and round/result identity collisions are rejected.
        /// </param>
        /// <param name="finalSummary">
        /// Optional final summary. When supplied it must be finished and is
        /// defensively copied into a new <see cref="PublicSessionSummary"/>.
        /// Pass null while the match is still in progress or has no final
        /// summary. This contract does not infer match completion from the
        /// round count.
        /// </param>
        public PublicSessionHistory(
            SessionId sessionId,
            IReadOnlyList<PublicRoundRecord> rounds,
            PublicSessionSummary? finalSummary = null)
        {
            if (sessionId.IsEmpty)
            {
                throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
            }

            if (rounds == null)
            {
                throw new ArgumentNullException(nameof(rounds));
            }

            var copy = new PublicRoundRecord[rounds.Count];
            var roundIds = new HashSet<string>(StringComparer.Ordinal);
            var resultIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rounds.Count; i++)
            {
                PublicRoundRecord record = rounds[i];
                if (record == null)
                {
                    throw new ArgumentException("rounds cannot contain null records.", nameof(rounds));
                }

                string roundId = record.RoundId.Value;
                string resultId = record.ResultId;

                if (resultIds.Contains(roundId))
                {
                    throw new ArgumentException(
                        "rounds[" + i + "] roundId '" + roundId + "' collides with an earlier resultId.",
                        nameof(rounds));
                }

                if (!roundIds.Add(roundId))
                {
                    throw new ArgumentException(
                        "rounds[" + i + "] contains duplicate roundId '" + roundId + "'.",
                        nameof(rounds));
                }

                if (roundIds.Contains(resultId))
                {
                    throw new ArgumentException(
                        "rounds[" + i + "] resultId '" + resultId + "' collides with a roundId.",
                        nameof(rounds));
                }

                if (!resultIds.Add(resultId))
                {
                    throw new ArgumentException(
                        "rounds[" + i + "] contains duplicate resultId '" + resultId + "'.",
                        nameof(rounds));
                }

                copy[i] = record;
            }

            _rounds = Array.AsReadOnly(copy);
            SessionId = sessionId;
            if (finalSummary != null && !finalSummary.Finished)
            {
                throw new ArgumentException(
                    "finalSummary must be finished when supplied.",
                    nameof(finalSummary));
            }

            FinalSummary = CopyFinalSummary(finalSummary);
        }

        public SessionId SessionId { get; }

        /// <summary>
        /// Completed rounds in the exact caller-supplied order. The list is
        /// read-only and cannot be reordered, replaced or cleared through the
        /// public surface.
        /// </summary>
        public IReadOnlyList<PublicRoundRecord> Rounds => _rounds;

        /// <summary>
        /// Optional finished summary, copied from the existing
        /// <see cref="PublicSessionSummary"/> type. Null means the match has not
        /// produced a final summary in this read model.
        /// </summary>
        public PublicSessionSummary? FinalSummary { get; }

        private static PublicSessionSummary? CopyFinalSummary(PublicSessionSummary? summary)
        {
            if (summary == null)
            {
                return null;
            }

            return new PublicSessionSummary(
                summary.Finished,
                summary.FinishReason,
                summary.FinalScores,
                summary.Rankings);
        }
    }
}
