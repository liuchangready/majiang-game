#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.Spectator
{
    public enum WinKind
    {
        None = 0,
        Tsumo = 1,
        Ron = 2,
        ChankanRon = 3,
        Draw = 4
    }

    public sealed class SettlementYakuItem
    {
        public string Code { get; }
        public string ChineseName { get; }
        public uint Han { get; }
        public string Label => ChineseName;

        public SettlementYakuItem(string code, string chineseName, uint han)
        {
            Code = code;
            ChineseName = chineseName;
            Han = han;
        }
    }

    public sealed class PointTransferItem
    {
        public int FromSeat { get; }
        public int ToSeat { get; }
        public long Amount { get; }

        public PointTransferItem(int fromSeat, int toSeat, long amount)
        {
            FromSeat = fromSeat;
            ToSeat = toSeat;
            Amount = Math.Abs(amount);
        }

        public string FormatTransfer()
        {
            if (FromSeat >= 0 && ToSeat >= 0)
            {
                return $"席位{FromSeat} -> 席位{ToSeat}: {Amount}点";
            }
            if (FromSeat >= 0)
            {
                return $"席位{FromSeat} 支付: {Amount}点";
            }
            if (ToSeat >= 0)
            {
                return $"席位{ToSeat} 收入: {Amount}点";
            }
            return $"{Amount}点";
        }
    }

    public sealed class SeatScoreItem
    {
        public int Seat { get; }
        public long Score { get; }

        public SeatScoreItem(int seat, long score)
        {
            Seat = seat;
            Score = score;
        }
    }

    public sealed class MatchRankingItem
    {
        public int Rank { get; }
        public int Seat { get; }
        public long? FinalScore { get; }

        public MatchRankingItem(int rank, int seat, long? finalScore)
        {
            Rank = rank;
            Seat = seat;
            FinalScore = finalScore;
        }
    }

    public sealed class PrivateLedgerEntryItem
    {
        public string EntryId { get; }
        public long Amount { get; }
        public long BalanceAfter { get; }
        public string ReasonCode { get; }
        public string ReasonDisplay { get; }

        public PrivateLedgerEntryItem(string entryId, long amount, long balanceAfter, string reasonCode, string reasonDisplay)
        {
            EntryId = entryId;
            Amount = amount;
            BalanceAfter = balanceAfter;
            ReasonCode = reasonCode;
            ReasonDisplay = reasonDisplay;
        }
    }

    public sealed class AuthorizedPrivateLedgerView
    {
        public bool IsAuthorized { get; }
        public int AuthorizedSeat { get; }
        public string SessionId { get; }
        public long CheatPoints { get; }
        public IReadOnlyList<PrivateLedgerEntryItem> Entries { get; }
        public bool IsLocal { get; }

        public static AuthorizedPrivateLedgerView Unauthorized { get; } = new AuthorizedPrivateLedgerView(
            false, -1, string.Empty, 0, Array.Empty<PrivateLedgerEntryItem>(), true);

        public AuthorizedPrivateLedgerView(
            bool isAuthorized,
            int authorizedSeat,
            string sessionId,
            long cheatPoints,
            IReadOnlyList<PrivateLedgerEntryItem> entries,
            bool isLocal)
        {
            IsAuthorized = isAuthorized;
            AuthorizedSeat = authorizedSeat;
            SessionId = sessionId;
            CheatPoints = cheatPoints;
            Entries = entries;
            IsLocal = isLocal;
        }
    }

    public sealed class RoundSettlementView
    {
        public string RoundId { get; }
        public WinKind Kind { get; }
        public int WinnerSeat { get; }
        public int LoserSeat { get; }
        public uint WinTileId { get; }
        public uint Han { get; }
        public uint Fu { get; }
        public long Score { get; }
        public IReadOnlyList<SettlementYakuItem> Yaku { get; }
        public ScoreBreakdownView? Breakdown { get; }
        public string DrawReason { get; }
        public IReadOnlyList<int> TenpaiSeats { get; }
        public IReadOnlyList<PointTransferItem> Payments { get; }
        public IReadOnlyList<SeatScoreItem> NewScores { get; }

        public string SummaryText => Kind switch
        {
            WinKind.Tsumo => "自摸和",
            WinKind.Ron => "荣和",
            WinKind.ChankanRon => "抢杠荣和",
            WinKind.Draw => "流局",
            _ => "对局中"
        };

        public string DrawReasonText => YakuTextMapper.GetDrawGameReasonLabel(DrawReason);

        public RoundSettlementView(
            string roundId,
            WinKind kind,
            int winnerSeat,
            int loserSeat,
            uint winTileId,
            uint han,
            uint fu,
            long score,
            IReadOnlyList<SettlementYakuItem> yaku,
            ScoreBreakdownView? breakdown,
            string drawReason,
            IReadOnlyList<int> tenpaiSeats,
            IReadOnlyList<PointTransferItem> payments,
            IReadOnlyList<SeatScoreItem> newScores)
        {
            RoundId = roundId;
            Kind = kind;
            WinnerSeat = winnerSeat;
            LoserSeat = loserSeat;
            WinTileId = winTileId;
            Han = han;
            Fu = fu;
            Score = score;
            Yaku = yaku;
            Breakdown = breakdown;
            DrawReason = drawReason;
            TenpaiSeats = tenpaiSeats;
            Payments = payments;
            NewScores = newScores;
        }
    }

    public sealed class MatchSettlementView
    {
        public bool IsFinished { get; }
        public string FinishReason { get; }
        public IReadOnlyList<MatchRankingItem> Rankings { get; }
        public IReadOnlyList<SeatScoreItem> FinalScores { get; }

        public static MatchSettlementView Empty { get; } = new MatchSettlementView(
            false, string.Empty, Array.Empty<MatchRankingItem>(), Array.Empty<SeatScoreItem>());

        public MatchSettlementView(
            bool isFinished,
            string finishReason,
            IReadOnlyList<MatchRankingItem> rankings,
            IReadOnlyList<SeatScoreItem> finalScores)
        {
            IsFinished = isFinished;
            FinishReason = finishReason;
            Rankings = rankings;
            FinalScores = finalScores;
        }
    }

    public sealed class SettlementViewModel
    {
        public RoundSettlementView? Round { get; }
        public MatchSettlementView Match { get; }
        public AuthorizedPrivateLedgerView PrivateLedger { get; }
        public AuthorizedPrivateLedgerView Ledger => PrivateLedger;

        public SettlementViewModel(
            RoundSettlementView? round,
            MatchSettlementView match,
            AuthorizedPrivateLedgerView privateLedger)
        {
            Round = round;
            Match = match;
            PrivateLedger = privateLedger;
        }

        public static SettlementViewModel Extract(SessionViewState? state)
        {
            if (!IsContextConsistent(state))
            {
                return new SettlementViewModel(null, MatchSettlementView.Empty, AuthorizedPrivateLedgerView.Unauthorized);
            }

            RoundSettlementView? round = ExtractRound(state!);
            MatchSettlementView match = ExtractMatch(state!);
            AuthorizedPrivateLedgerView ledger = ExtractAuthorizedLedger(state!);

            return new SettlementViewModel(round, match, ledger);
        }

        private static bool IsContextConsistent(SessionViewState? state)
        {
            if (state == null || state.Binding == null || string.IsNullOrEmpty(state.Binding.SessionId))
            {
                return false;
            }

            if (state.RoomSnapshot != null)
            {
                if (!string.IsNullOrEmpty(state.RoomSnapshot.RoomId) && !string.Equals(state.RoomSnapshot.RoomId, state.Binding.RoomId, StringComparison.Ordinal))
                {
                    return false;
                }
                if (!string.IsNullOrEmpty(state.RoomSnapshot.SessionId) && !string.Equals(state.RoomSnapshot.SessionId, state.Binding.SessionId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (state.PublicSnapshot != null && !string.IsNullOrEmpty(state.PublicSnapshot.SessionId) &&
                !string.Equals(state.Binding.SessionId, state.PublicSnapshot.SessionId, StringComparison.Ordinal))
            {
                return false;
            }

            if (state.PlayerSnapshot != null && !string.IsNullOrEmpty(state.PlayerSnapshot.SessionId) &&
                !string.Equals(state.Binding.SessionId, state.PlayerSnapshot.SessionId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static PublicMatchView? GetAuthorizedMatchView(SessionViewState state)
        {
            if (!IsContextConsistent(state)) return null;

            // Role.ROLE_PLAYER = 1. Spectator is role 2.
            // Spectator must strictly consume PublicSnapshot.View and NEVER fall back to PlayerSnapshot.PublicView!
            if (state.Binding!.Role == 1)
            {
                return state.PublicSnapshot?.View ?? state.PlayerSnapshot?.PublicView;
            }

            return state.PublicSnapshot?.View;
        }

        public static RoundSettlementView? ExtractRound(SessionViewState state)
        {
            PublicMatchView? matchView = GetAuthorizedMatchView(state);
            if (matchView == null)
            {
                return null;
            }

            string roundId = matchView.SettlementRoundId ?? state.PublicSnapshot?.RoundId ?? state.PlayerSnapshot?.RoundId ?? string.Empty;
            if (matchView.SettlementEvents == null || matchView.SettlementEvents.Count == 0)
            {
                return null;
            }

            WinKind kind = WinKind.None;
            int winnerSeat = -1;
            int loserSeat = -1;
            uint winTileId = 0;
            uint han = 0;
            uint fu = 0;
            long score = 0;
            var yakuList = new List<SettlementYakuItem>();
            ScoreBreakdownView? breakdown = null;
            string drawReason = string.Empty;
            var tenpaiSeats = new List<int>();
            var payments = new List<PointTransferItem>();
            var newScores = new List<SeatScoreItem>();

            foreach (PublicEventEnvelope env in matchView.SettlementEvents)
            {
                if (env.TsumoWin != null)
                {
                    kind = WinKind.Tsumo;
                    winnerSeat = env.TsumoWin.Player;
                    winTileId = env.TsumoWin.WinTileId;
                    han = env.TsumoWin.Han;
                    fu = env.TsumoWin.Fu;
                    score = env.TsumoWin.Score;
                    breakdown = env.TsumoWin.Breakdown?.Clone();

                    if (env.TsumoWin.Yaku != null)
                    {
                        foreach (YakuView y in env.TsumoWin.Yaku)
                        {
                            yakuList.Add(new SettlementYakuItem(y.Code, YakuTextMapper.GetChineseName(y.Code), y.Han));
                        }
                    }
                }
                else if (env.RonWin != null)
                {
                    kind = env.RonWin.IsChankan ? WinKind.ChankanRon : WinKind.Ron;
                    winnerSeat = env.RonWin.Winner;
                    loserSeat = env.RonWin.Loser;
                    winTileId = env.RonWin.WinTileId;
                    han = env.RonWin.Han;
                    fu = env.RonWin.Fu;
                    score = env.RonWin.Score;
                    breakdown = env.RonWin.Breakdown?.Clone();

                    if (env.RonWin.Yaku != null)
                    {
                        foreach (YakuView y in env.RonWin.Yaku)
                        {
                            yakuList.Add(new SettlementYakuItem(y.Code, YakuTextMapper.GetChineseName(y.Code), y.Han));
                        }
                    }
                }
                else if (env.DrawGame != null)
                {
                    kind = WinKind.Draw;
                    drawReason = env.DrawGame.Reason ?? string.Empty;
                    if (env.DrawGame.TenpaiPlayers != null)
                    {
                        tenpaiSeats.AddRange(env.DrawGame.TenpaiPlayers);
                    }
                }
                else if (env.ScoreSettled != null)
                {
                    if (env.ScoreSettled.Payments != null)
                    {
                        foreach (PaymentView p in env.ScoreSettled.Payments)
                        {
                            payments.Add(new PointTransferItem(p.FromSeat, p.ToSeat, p.Amount));
                        }
                    }
                    if (env.ScoreSettled.NewScores != null)
                    {
                        foreach (ScoreView s in env.ScoreSettled.NewScores)
                        {
                            newScores.Add(new SeatScoreItem(s.Seat, s.Score));
                        }
                    }
                }
            }

            if (kind == WinKind.None && payments.Count == 0 && newScores.Count == 0)
            {
                return null;
            }

            return new RoundSettlementView(
                roundId,
                kind,
                winnerSeat,
                loserSeat,
                winTileId,
                han,
                fu,
                score,
                yakuList.AsReadOnly(),
                breakdown,
                drawReason,
                tenpaiSeats.AsReadOnly(),
                payments.AsReadOnly(),
                newScores.AsReadOnly());
        }

        public static MatchSettlementView ExtractMatch(SessionViewState state)
        {
            PublicMatchView? matchView = GetAuthorizedMatchView(state);
            PublicSessionSummary? summary = matchView?.Session;

            if (summary == null)
            {
                return MatchSettlementView.Empty;
            }

            var finalScores = new List<SeatScoreItem>();
            var scoreMap = new Dictionary<int, long>();
            if (summary.FinalScores != null)
            {
                foreach (ScoreView sc in summary.FinalScores)
                {
                    finalScores.Add(new SeatScoreItem(sc.Seat, sc.Score));
                    scoreMap[sc.Seat] = sc.Score;
                }
            }

            var rankings = new List<MatchRankingItem>();
            if (summary.Rankings != null)
            {
                for (int rankIndex = 0; rankIndex < summary.Rankings.Count; rankIndex++)
                {
                    int seat = summary.Rankings[rankIndex];
                    long? sc = scoreMap.TryGetValue(seat, out long val) ? (long?)val : null;
                    rankings.Add(new MatchRankingItem(rankIndex + 1, seat, sc));
                }
            }

            return new MatchSettlementView(
                summary.Finished,
                summary.FinishReason ?? string.Empty,
                rankings.AsReadOnly(),
                finalScores.AsReadOnly());
        }

        public static AuthorizedPrivateLedgerView ExtractAuthorizedLedger(SessionViewState state)
        {
            if (!IsContextConsistent(state))
            {
                return AuthorizedPrivateLedgerView.Unauthorized;
            }

            if (state.Binding!.Role != 1) // Role.ROLE_PLAYER = 1
            {
                return AuthorizedPrivateLedgerView.Unauthorized;
            }

            PlayerSnapshot? playerSnapshot = state.PlayerSnapshot;
            if (playerSnapshot == null || playerSnapshot.PrivateView == null || playerSnapshot.PrivateView.Cheat == null)
            {
                return AuthorizedPrivateLedgerView.Unauthorized;
            }

            if (playerSnapshot.PrivateView.Seat != state.Binding.SeatId)
            {
                return AuthorizedPrivateLedgerView.Unauthorized;
            }

            if (!string.Equals(playerSnapshot.SessionId, state.Binding.SessionId, StringComparison.Ordinal))
            {
                return AuthorizedPrivateLedgerView.Unauthorized;
            }

            PrivateCheatView cheat = playerSnapshot.PrivateView.Cheat;
            var entries = new List<PrivateLedgerEntryItem>();
            if (cheat.LedgerEntries != null)
            {
                foreach (CheatEntryView e in cheat.LedgerEntries)
                {
                    string display = MapLedgerReason(e.ReasonCode);
                    entries.Add(new PrivateLedgerEntryItem(e.EntryId, e.Amount, e.BalanceAfter, e.ReasonCode, display));
                }
            }

            return new AuthorizedPrivateLedgerView(
                true,
                state.Binding.SeatId,
                state.Binding.SessionId,
                cheat.CheatPoints,
                entries.AsReadOnly(),
                isLocal: false); // ISessionView supplies no origin; unknown origin cannot prove LOCAL
        }

        private static string MapLedgerReason(string? reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode)) return "出千变动";
            switch (reasonCode!.ToUpperInvariant())
            {
                case "TILE_MARK":
                case "MARK_TILE": return "落汗";
                case "SLEEVE_BLOOM": return "袖里藏花";
                case "BEAM_SWAP": return "偷梁换柱";
                case "INITIAL_GRANT": return "开局发放";
                case "ROUND_RESET": return "局末重置";
                case "CHEAT_LEDGER": return "绝技账本结算";
                default: return $"账本明细({reasonCode})";
            }
        }
    }

    public static class SettlementModelExtractor
    {
        public static SettlementViewModel Extract(SessionViewState? state) => SettlementViewModel.Extract(state);
        public static RoundSettlementView? ExtractRound(SessionViewState state) => SettlementViewModel.ExtractRound(state);
        public static MatchSettlementView ExtractMatch(SessionViewState state) => SettlementViewModel.ExtractMatch(state);
        public static AuthorizedPrivateLedgerView ExtractAuthorizedLedger(SessionViewState state) => SettlementViewModel.ExtractAuthorizedLedger(state);
    }
}
