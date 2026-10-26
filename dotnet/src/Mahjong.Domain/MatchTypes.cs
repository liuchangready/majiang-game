using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain
{
    public enum MatchLength : byte
    {
        Tonpuusen = 0,
        Hanchan = 1
    }

    public enum RoundOutcomeKind : byte
    {
        None = 0,
        DealerWin = 1,
        NonDealerWin = 2,
        Ron = 3,
        Tsumo = 4,
        ExhaustiveDraw = 5,
        AbortiveDraw = 6
    }

    /// <summary>
    /// V4 <c>RoundOutcome</c>. DealerWin/NonDealerWin are kept for exact V4 match-progression
    /// parity; Ron/Tsumo are the forms produced by the V5 round lifecycle.
    /// </summary>
    public sealed class RoundOutcome
    {
        private static readonly ReadOnlyCollection<int> NoSeats =
            new ReadOnlyCollection<int>(new List<int>());

        private RoundOutcome(
            RoundOutcomeKind kind,
            int winnerSeat,
            int loserSeat,
            IReadOnlyList<int> tenpaiSeats,
            string reason)
        {
            Kind = kind;
            WinnerSeat = winnerSeat;
            LoserSeat = loserSeat;
            TenpaiSeats = tenpaiSeats;
            Reason = reason;
        }

        public RoundOutcomeKind Kind { get; }

        public int WinnerSeat { get; }

        public int LoserSeat { get; }

        public IReadOnlyList<int> TenpaiSeats { get; }

        public string Reason { get; }

        public static RoundOutcome DealerWin(int winner)
        {
            V5Guard.CheckSeat(winner, nameof(winner));
            return new RoundOutcome(RoundOutcomeKind.DealerWin, winner, -1, NoSeats, string.Empty);
        }

        public static RoundOutcome NonDealerWin(int winner)
        {
            V5Guard.CheckSeat(winner, nameof(winner));
            return new RoundOutcome(RoundOutcomeKind.NonDealerWin, winner, -1, NoSeats, string.Empty);
        }

        public static RoundOutcome Ron(int winner, int loser)
        {
            V5Guard.CheckSeat(winner, nameof(winner));
            V5Guard.CheckSeat(loser, nameof(loser));
            if (winner == loser)
            {
                throw new ArgumentException("Ron winner and loser must differ.");
            }

            return new RoundOutcome(RoundOutcomeKind.Ron, winner, loser, NoSeats, string.Empty);
        }

        public static RoundOutcome Tsumo(int winner)
        {
            V5Guard.CheckSeat(winner, nameof(winner));
            return new RoundOutcome(RoundOutcomeKind.Tsumo, winner, -1, NoSeats, string.Empty);
        }

        public static RoundOutcome ExhaustiveDraw(IReadOnlyList<int> tenpaiSeats)
        {
            if (tenpaiSeats == null)
            {
                throw new ArgumentNullException(nameof(tenpaiSeats));
            }

            if (tenpaiSeats.Count > 4)
            {
                throw new ArgumentException("At most four seats can be tenpai.", nameof(tenpaiSeats));
            }

            return new RoundOutcome(
                RoundOutcomeKind.ExhaustiveDraw,
                -1,
                -1,
                V5Guard.CopySeats(tenpaiSeats, nameof(tenpaiSeats)),
                string.Empty);
        }

        public static RoundOutcome AbortiveDraw(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("An abortive draw needs a reason.", nameof(reason));
            }

            return new RoundOutcome(RoundOutcomeKind.AbortiveDraw, -1, -1, NoSeats, reason);
        }

        public override string ToString() => Kind + "/winner" + WinnerSeat + "/loser" + LoserSeat;
    }

    /// <summary>
    /// V4 <c>MatchRuleConfig</c>. Defaults mirror the frozen contract: hanchan, 30,000 return,
    /// west extension allowed, agari-yame allowed, strict bankruptcy below zero, no uma/oka.
    /// </summary>
    public sealed class MatchRuleConfig
    {
        private readonly ReadOnlyCollection<int> _winds;
        private readonly ReadOnlyCollection<int> _tieBreakSeats;

        public MatchRuleConfig(
            MatchLength length = MatchLength.Hanchan,
            long targetScore = 30000L,
            bool allowWestExtension = true,
            bool allowAgariYame = true,
            bool allowBankruptcy = true,
            int handsPerWind = 4,
            IReadOnlyList<int>? winds = null,
            long bankruptcyThreshold = 0L,
            bool bankruptcyInclusive = false,
            bool awardRemainingRiichiSticksToFirst = true,
            IReadOnlyList<int>? tieBreakSeats = null)
        {
            if (handsPerWind < 1 || handsPerWind > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(handsPerWind), handsPerWind, "handsPerWind must be 1..4.");
            }

            if (targetScore < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetScore), targetScore, "targetScore must be non-negative.");
            }

            var windList = winds == null
                ? (length == MatchLength.Tonpuusen ? new[] { 1 } : new[] { 1, 2 })
                : CopyWinds(winds);
            if (windList.Length != (length == MatchLength.Tonpuusen ? 1 : 2))
            {
                // A custom wind list is allowed, but it must still be a contiguous 1..2 prefix.
                if (windList.Length < 1 || windList.Length > 2)
                {
                    throw new ArgumentException("winds must contain one or two entries.", nameof(winds));
                }
            }

            for (int i = 0; i < windList.Length; i++)
            {
                if (windList[i] != i + 1)
                {
                    throw new ArgumentException("winds must be the contiguous sequence 1,2.", nameof(winds));
                }
            }

            var tieBreak = tieBreakSeats == null
                ? new[] { 0, 1, 2, 3 }
                : CopyTieBreak(tieBreakSeats);

            Length = length;
            TargetScore = targetScore;
            AllowWestExtension = allowWestExtension;
            AllowAgariYame = allowAgariYame;
            AllowBankruptcy = allowBankruptcy;
            HandsPerWind = handsPerWind;
            _winds = new ReadOnlyCollection<int>(windList);
            BankruptcyThreshold = bankruptcyThreshold;
            BankruptcyInclusive = bankruptcyInclusive;
            AwardRemainingRiichiSticksToFirst = awardRemainingRiichiSticksToFirst;
            _tieBreakSeats = new ReadOnlyCollection<int>(tieBreak);
        }

        public MatchLength Length { get; }

        public long TargetScore { get; }

        public bool AllowWestExtension { get; }

        public bool AllowAgariYame { get; }

        public bool AllowBankruptcy { get; }

        public int HandsPerWind { get; }

        public IReadOnlyList<int> Winds => _winds;

        public long BankruptcyThreshold { get; }

        public bool BankruptcyInclusive { get; }

        public bool AwardRemainingRiichiSticksToFirst { get; }

        public IReadOnlyList<int> TieBreakSeats => _tieBreakSeats;

        private static int[] CopyWinds(IReadOnlyList<int> source)
        {
            var copy = new int[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                copy[i] = source[i];
            }

            return copy;
        }

        private static int[] CopyTieBreak(IReadOnlyList<int> source)
        {
            if (source.Count != 4)
            {
                throw new ArgumentException("tieBreakSeats must contain all four seats.", nameof(source));
            }

            var copy = new int[4];
            var seen = new bool[4];
            for (int i = 0; i < 4; i++)
            {
                int seat = source[i];
                V5Guard.CheckSeat(seat, nameof(source));
                if (seen[seat])
                {
                    throw new ArgumentException("tieBreakSeats contains duplicate seat " + seat + ".", nameof(source));
                }

                seen[seat] = true;
                copy[i] = seat;
            }

            return copy;
        }
    }

    public enum MatchFinishReason : byte
    {
        Bankruptcy = 0,
        NormalCompletion = 1,
        WestCompletion = 2,
        AgariYame = 3
    }

    public sealed class RankingEntry
    {
        public RankingEntry(int rank, int seat, long score)
        {
            if (rank < 1 || rank > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(rank), rank, "Rank must be 1..4.");
            }

            V5Guard.CheckSeat(seat);
            Rank = rank;
            Seat = seat;
            Score = score;
        }

        public int Rank { get; }

        public int Seat { get; }

        public long Score { get; }
    }

    public abstract class ProgressionResult
    {
        private ProgressionResult()
        {
        }

        public sealed class NextRound : ProgressionResult
        {
            public NextRound(
                int roundWind,
                int roundNumber,
                int dealerSeat,
                int honbaSticks,
                bool canDealerDeclareAgariYame)
            {
                RoundWind = roundWind;
                RoundNumber = roundNumber;
                V5Guard.CheckSeat(dealerSeat);
                if (honbaSticks < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(honbaSticks), honbaSticks, "Honba must be non-negative.");
                }

                DealerSeat = dealerSeat;
                HonbaSticks = honbaSticks;
                CanDealerDeclareAgariYame = canDealerDeclareAgariYame;
            }

            public int RoundWind { get; }

            public int RoundNumber { get; }

            public int DealerSeat { get; }

            public int HonbaSticks { get; }

            public bool CanDealerDeclareAgariYame { get; }
        }

        public sealed class MatchFinished : ProgressionResult
        {
            private readonly ReadOnlyCollection<long> _finalScores;
            private readonly ReadOnlyCollection<RankingEntry> _rankings;

            public MatchFinished(
                IReadOnlyList<long> finalScores,
                IReadOnlyList<RankingEntry> rankings,
                MatchFinishReason reason,
                int? remainingRiichiSticksTransferredTo)
            {
                _finalScores = new ReadOnlyCollection<long>(new List<long>(finalScores));
                var rankCopy = new List<RankingEntry>(rankings.Count);
                for (int i = 0; i < rankings.Count; i++)
                {
                    RankingEntry entry = rankings[i] ?? throw new ArgumentException("Ranking entry cannot be null.");
                    rankCopy.Add(new RankingEntry(entry.Rank, entry.Seat, entry.Score));
                }

                _rankings = new ReadOnlyCollection<RankingEntry>(rankCopy);
                if (remainingRiichiSticksTransferredTo.HasValue)
                {
                    V5Guard.CheckSeat(remainingRiichiSticksTransferredTo.Value);
                }

                Reason = reason;
                RemainingRiichiSticksTransferredTo = remainingRiichiSticksTransferredTo;
            }

            public IReadOnlyList<long> FinalScores => _finalScores;

            public IReadOnlyList<RankingEntry> Rankings => _rankings;

            public MatchFinishReason Reason { get; }

            public int? RemainingRiichiSticksTransferredTo { get; }
        }
    }

    public sealed class ProgressionInput
    {
        public ProgressionInput(
            int currentDealer,
            int currentRoundWind,
            int currentRoundNumber,
            int currentHonba,
            IReadOnlyList<long> scores,
            RoundOutcome outcome,
            int remainingRiichiSticks = 0,
            bool dealerDeclaredAgariYame = false)
        {
            V5Guard.CheckSeat(currentDealer, nameof(currentDealer));
            if (currentRoundWind < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(currentRoundWind), currentRoundWind, "Wind must be positive.");
            }

            if (currentRoundNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(currentRoundNumber), currentRoundNumber, "Round number must be non-negative.");
            }

            if (currentHonba < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(currentHonba), currentHonba, "Honba must be non-negative.");
            }

            if (remainingRiichiSticks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(remainingRiichiSticks), remainingRiichiSticks, "Riichi sticks must be non-negative.");
            }

            CurrentDealer = currentDealer;
            CurrentRoundWind = currentRoundWind;
            CurrentRoundNumber = currentRoundNumber;
            CurrentHonba = currentHonba;
            Scores = V5Guard.CopyScores(scores, nameof(scores));
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
            RemainingRiichiSticks = remainingRiichiSticks;
            DealerDeclaredAgariYame = dealerDeclaredAgariYame;
        }

        public int CurrentDealer { get; }

        public int CurrentRoundWind { get; }

        public int CurrentRoundNumber { get; }

        public int CurrentHonba { get; }

        public IReadOnlyList<long> Scores { get; }

        public RoundOutcome Outcome { get; }

        public int RemainingRiichiSticks { get; }

        public bool DealerDeclaredAgariYame { get; }
    }

    public enum SessionLifecycle : byte
    {
        Created = 0,
        WaitingStart = 1,
        RoundActive = 2,
        RoundResult = 3,
        WaitingDealerChoice = 4,
        WaitingReady = 5,
        Finished = 6,
        Paused = 7,
        Closed = 8
    }

    public enum ProgressionPlanKind : byte
    {
        PrepareNextRound = 0,
        WaitDealerChoice = 1,
        FinishSession = 2
    }

    public sealed class ProgressionPlan
    {
        public ProgressionPlan(
            ProgressionPlanKind kind,
            ProgressionResult.NextRound? nextRound = null,
            ProgressionResult.MatchFinished? matchFinished = null,
            bool canDealerDeclareAgariYame = false)
        {
            Kind = kind;
            NextRound = nextRound;
            MatchFinished = matchFinished;
            CanDealerDeclareAgariYame = canDealerDeclareAgariYame;
        }

        public ProgressionPlanKind Kind { get; }

        public ProgressionResult.NextRound? NextRound { get; }

        public ProgressionResult.MatchFinished? MatchFinished { get; }

        public bool CanDealerDeclareAgariYame { get; }
    }

    public sealed class RoundRecord
    {
        private readonly ReadOnlyCollection<long> _finalScores;
        private readonly ReadOnlyCollection<long> _deltas;

        public RoundRecord(
            string roundId,
            string resultId,
            RoundOutcome outcome,
            IReadOnlyList<long> finalScores,
            IReadOnlyList<long> deltas,
            int remainingRiichiSticks)
        {
            RoundId = roundId;
            ResultId = resultId;
            Outcome = outcome;
            _finalScores = new ReadOnlyCollection<long>(new List<long>(finalScores));
            _deltas = new ReadOnlyCollection<long>(new List<long>(deltas));
            RemainingRiichiSticks = remainingRiichiSticks;
        }

        public string RoundId { get; }

        public string ResultId { get; }

        public RoundOutcome Outcome { get; }

        public IReadOnlyList<long> FinalScores => _finalScores;

        public IReadOnlyList<long> Deltas => _deltas;

        public int RemainingRiichiSticks { get; }
    }

    public sealed class SessionResult
    {
        private readonly ReadOnlyCollection<long> _finalScores;
        private readonly ReadOnlyCollection<RankingEntry> _rankings;

        public SessionResult(
            string sessionId,
            IReadOnlyList<long> finalScores,
            IReadOnlyList<RankingEntry> rankings,
            MatchFinishReason reason,
            int? remainingRiichiSticksTransferredTo,
            PersistedUtcTime completedAtUtc,
            long completedAtMonotonicMillis)
        {
            SessionId = sessionId;
            _finalScores = new ReadOnlyCollection<long>(new List<long>(finalScores));
            var rankCopy = new List<RankingEntry>(rankings.Count);
            for (int i = 0; i < rankings.Count; i++)
            {
                RankingEntry entry = rankings[i];
                rankCopy.Add(new RankingEntry(entry.Rank, entry.Seat, entry.Score));
            }

            _rankings = new ReadOnlyCollection<RankingEntry>(rankCopy);
            Reason = reason;
            RemainingRiichiSticksTransferredTo = remainingRiichiSticksTransferredTo;
            CompletedAtUtc = completedAtUtc;
            CompletedAtMonotonicMillis = completedAtMonotonicMillis;
        }

        public string SessionId { get; }

        public IReadOnlyList<long> FinalScores => _finalScores;

        public IReadOnlyList<RankingEntry> Rankings => _rankings;

        public MatchFinishReason Reason { get; }

        public int? RemainingRiichiSticksTransferredTo { get; }

        public PersistedUtcTime CompletedAtUtc { get; }

        public long CompletedAtMonotonicMillis { get; }
    }

    public enum MatchTransitionErrorCode : byte
    {
        None = 0,
        InvalidLifecycle = 1,
        NoActiveRound = 2,
        ActiveRoundNotFinished = 3,
        RoundNotFinished = 4,
        DuplicateResult = 5,
        RoundIdReuse = 6,
        RoundStateMismatch = 7,
        SessionFinished = 8,
        NoPendingProgression = 9,
        DealerChoiceUnavailable = 10,
        InvalidTimestamp = 11,
        ForeignRoundBranch = 12
    }

    public sealed class MatchTransitionError
    {
        public MatchTransitionError(MatchTransitionErrorCode code, string message)
        {
            if (code == MatchTransitionErrorCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(code), code, "A rejection needs a non-None code.");
            }

            Code = code;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public MatchTransitionErrorCode Code { get; }

        public string Message { get; }

        public override string ToString() => Code + ": " + Message;
    }

    public sealed class MatchTransition
    {
        private MatchTransition(
            bool isAccepted,
            MatchSession? state,
            long? version,
            SessionResult? result,
            MatchTransitionError? error,
            IReadOnlyList<RoundEvent> events)
        {
            IsAccepted = isAccepted;
            State = state;
            CommandVersion = version;
            Result = result;
            Error = error;
            Events = events;
        }

        public bool IsAccepted { get; }

        public MatchSession? State { get; }

        public long? CommandVersion { get; }

        public SessionResult? Result { get; }

        public MatchTransitionError? Error { get; }

        public IReadOnlyList<RoundEvent> Events { get; }

        internal static MatchTransition Accepted(
            MatchSession state,
            SessionResult? result = null,
            IReadOnlyList<RoundEvent>? events = null)
        {
            return new MatchTransition(
                true,
                state,
                state.Version,
                result,
                null,
                events == null
                    ? new ReadOnlyCollection<RoundEvent>(new List<RoundEvent>())
                    : new ReadOnlyCollection<RoundEvent>(new List<RoundEvent>(events)));
        }

        internal static MatchTransition Rejected(MatchTransitionErrorCode code, string message)
        {
            return new MatchTransition(
                false,
                null,
                null,
                null,
                new MatchTransitionError(code, message),
                new ReadOnlyCollection<RoundEvent>(new List<RoundEvent>()));
        }
    }
}
