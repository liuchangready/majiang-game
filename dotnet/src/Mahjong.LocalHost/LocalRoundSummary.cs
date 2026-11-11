using System;
using System.Collections.Generic;

namespace Mahjong.LocalHost
{
    /// <summary>Cheat usage aggregated by ledger reason (for example TILE_MARK).</summary>
    public sealed class LocalCheatUsage
    {
        public LocalCheatUsage(string reason, int count, int spentPoints)
        {
            Reason = reason ?? string.Empty;
            Count = count;
            SpentPoints = spentPoints;
        }

        public string Reason { get; }

        public int Count { get; }

        /// <summary>Positive points charged for this reason (ledger amounts are negative).</summary>
        public int SpentPoints { get; }
    }

    /// <summary>
    /// Read-only per-round summary returned by an open local table. It carries only public
    /// settlement facts, physical conservation evidence and aggregate cheat accounting; it
    /// never exposes hidden hands, wall order or seeds.
    /// </summary>
    public sealed class LocalRoundSummary
    {
        public LocalRoundSummary(
            string roomId,
            string roundId,
            bool finished,
            string phase,
            long stateVersion,
            string outcomeCode,
            int winnerSeat,
            int loserSeat,
            IReadOnlyList<long> finalScores,
            int remainingRiichiSticks,
            int wallAssignedCount,
            bool wallConserved,
            IReadOnlyList<string> conservationErrors,
            IReadOnlyList<int> cheatBalances,
            IReadOnlyList<LocalCheatUsage> cheatUsage)
        {
            RoomId = roomId ?? string.Empty;
            RoundId = roundId ?? string.Empty;
            Finished = finished;
            Phase = phase ?? string.Empty;
            StateVersion = stateVersion;
            OutcomeCode = outcomeCode ?? string.Empty;
            WinnerSeat = winnerSeat;
            LoserSeat = loserSeat;
            FinalScores = finalScores ?? Array.Empty<long>();
            RemainingRiichiSticks = remainingRiichiSticks;
            WallAssignedCount = wallAssignedCount;
            WallConserved = wallConserved;
            ConservationErrors = conservationErrors ?? Array.Empty<string>();
            CheatBalances = cheatBalances ?? Array.Empty<int>();
            CheatUsage = cheatUsage ?? Array.Empty<LocalCheatUsage>();
        }

        public string RoomId { get; }

        public string RoundId { get; }

        public bool Finished { get; }

        public string Phase { get; }

        public long StateVersion { get; }

        public string OutcomeCode { get; }

        public int WinnerSeat { get; }

        public int LoserSeat { get; }

        public IReadOnlyList<long> FinalScores { get; }

        public int RemainingRiichiSticks { get; }

        public int WallAssignedCount { get; }

        public bool WallConserved { get; }

        public IReadOnlyList<string> ConservationErrors { get; }

        public IReadOnlyList<int> CheatBalances { get; }

        public IReadOnlyList<LocalCheatUsage> CheatUsage { get; }
    }
}
