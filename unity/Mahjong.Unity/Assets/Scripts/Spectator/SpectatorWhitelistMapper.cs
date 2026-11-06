#nullable enable
using System;
using System.Collections.Generic;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.UI.Session;

namespace Mahjong.Unity.Spectator
{
    public sealed class SpectatorSeatModel
    {
        public int SeatId { get; }
        public string SeatName { get; }
        public bool IsDealer { get; }
        public bool IsCurrent { get; }
        public bool IsRiichi { get; }
        public uint HandCount { get; }
        public long? Score { get; }
        public IReadOnlyList<MeldView> Melds { get; }
        public IReadOnlyList<DiscardView> Discards { get; }
        public int ConcealedMeldCount { get; }

        public SpectatorSeatModel(
            int seatId,
            string seatName,
            bool isDealer,
            bool isCurrent,
            bool isRiichi,
            uint handCount,
            long? score,
            IReadOnlyList<MeldView> melds,
            IReadOnlyList<DiscardView> discards,
            int concealedMeldCount)
        {
            SeatId = seatId;
            SeatName = seatName;
            IsDealer = isDealer;
            IsCurrent = isCurrent;
            IsRiichi = isRiichi;
            HandCount = handCount;
            Score = score;
            Melds = melds;
            Discards = discards;
            ConcealedMeldCount = concealedMeldCount;
        }
    }

    public sealed class SpectatorBoardModel
    {
        public string SessionId { get; }
        public string RoundId { get; }
        public GamePhase Phase { get; }
        public string PhaseText { get; }
        public uint RoundWind { get; }
        public string RoundWindText { get; }
        public uint RoundNumber { get; }
        public string RoundText { get; }
        public uint Honba { get; }
        public uint RiichiSticks { get; }
        public int DealerSeat { get; }
        public int CurrentSeat { get; }
        public uint WallRemaining { get; }
        public IReadOnlyList<uint> DoraIndicators { get; }
        public IReadOnlyList<SpectatorSeatModel> Seats { get; }
        public IReadOnlyList<ClockView> Clocks { get; }
        public bool IsPaused { get; }
        public bool IsMatchFinished { get; }
        public string MatchFinishReason { get; }
        public IReadOnlyList<int> FinalRankings { get; }
        public IReadOnlyList<ScoreView> FinalScores { get; }

        public SpectatorBoardModel(
            string sessionId,
            string roundId,
            GamePhase phase,
            string phaseText,
            uint roundWind,
            string roundWindText,
            uint roundNumber,
            string roundText,
            uint honba,
            uint riichiSticks,
            int dealerSeat,
            int currentSeat,
            uint wallRemaining,
            IReadOnlyList<uint> doraIndicators,
            IReadOnlyList<SpectatorSeatModel> seats,
            IReadOnlyList<ClockView> clocks,
            bool isPaused,
            bool isMatchFinished,
            string matchFinishReason,
            IReadOnlyList<int> finalRankings,
            IReadOnlyList<ScoreView> finalScores)
        {
            SessionId = sessionId;
            RoundId = roundId;
            Phase = phase;
            PhaseText = phaseText;
            RoundWind = roundWind;
            RoundWindText = roundWindText;
            RoundNumber = roundNumber;
            RoundText = roundText;
            Honba = honba;
            RiichiSticks = riichiSticks;
            DealerSeat = dealerSeat;
            CurrentSeat = currentSeat;
            WallRemaining = wallRemaining;
            DoraIndicators = doraIndicators;
            Seats = seats;
            Clocks = clocks;
            IsPaused = isPaused;
            IsMatchFinished = isMatchFinished;
            MatchFinishReason = matchFinishReason;
            FinalRankings = finalRankings;
            FinalScores = finalScores;
        }
    }

    /// <summary>
    /// Pure projection mapping from authoritative Wire PublicSnapshot to spectator view model.
    /// Enforces the public whitelist: hand counts, river discards, public melds,
    /// and identity-free concealed meld placeholders.
    /// Absolutely prohibits consuming or serializing private views.
    /// </summary>
    public static class SpectatorWhitelistMapper
    {
        public static SpectatorBoardModel? Map(PublicSnapshot? snapshot, SessionBindingView? binding)
        {
            return Map(snapshot, binding, null);
        }

        public static SpectatorBoardModel? Map(PublicSnapshot? snapshot, SessionBindingView? binding, RoomUpdate? room)
        {
            if (snapshot == null || snapshot.View == null)
            {
                return null;
            }

            // Context validation: binding is required (fail closed if null)
            if (binding == null || string.IsNullOrEmpty(binding.SessionId))
            {
                return null;
            }

            if (!string.Equals(binding.SessionId, snapshot.SessionId, StringComparison.Ordinal))
            {
                return null;
            }

            if (room != null)
            {
                if (!string.IsNullOrEmpty(room.RoomId) && !string.Equals(room.RoomId, binding.RoomId, StringComparison.Ordinal))
                {
                    return null;
                }
                if (!string.IsNullOrEmpty(room.SessionId) && !string.Equals(room.SessionId, binding.SessionId, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            PublicMatchView matchView = snapshot.View;
            PublicBoardView? board = matchView.Board;
            PublicSessionSummary? sessionSummary = matchView.Session;

            GamePhase phase = board != null ? board.Phase : GamePhase.Unspecified;
            string phaseText = MapPhaseText(phase);
            uint roundWind = board != null ? board.RoundWind : 0;
            string roundWindText = MapWindText(roundWind);
            uint roundNumber = board != null ? board.RoundNumber : 1;
            uint honba = board != null ? board.Honba : 0;
            string roundText = $"{roundWindText}{roundNumber}局 {honba}本场";
            uint riichiSticks = board != null ? board.RiichiSticks : 0;
            int dealerSeat = board != null ? board.DealerSeat : -1;
            int currentSeat = board != null ? board.CurrentSeat : -1;
            uint wallRemaining = board != null ? board.WallRemaining : 0;

            var doraList = new List<uint>();
            if (board != null && board.DoraIndicators != null)
            {
                doraList.AddRange(board.DoraIndicators);
            }

            var scoresBySeat = new Dictionary<int, long>();
            if (board != null && board.Scores != null)
            {
                foreach (ScoreView sc in board.Scores)
                {
                    scoresBySeat[sc.Seat] = sc.Score;
                }
            }

            var seats = new List<SpectatorSeatModel>(4);
            for (int seatId = 0; seatId < 4; seatId++)
            {
                PlayerPublicView? player = null;
                if (board != null && board.Players != null)
                {
                    foreach (PlayerPublicView p in board.Players)
                    {
                        if (p.Seat == seatId)
                        {
                            player = p;
                            break;
                        }
                    }
                }

                string seatName = GetSeatWindName(roundWind, dealerSeat, seatId);
                bool isDealer = dealerSeat == seatId;
                bool isCurrent = currentSeat == seatId;
                bool isRiichi = player != null && player.Riichi;
                uint handCount = player != null ? player.HandCount : 0;
                long? score = scoresBySeat.TryGetValue(seatId, out long s) ? (long?)s : null;

                var melds = new List<MeldView>();
                if (player != null && player.Melds != null)
                {
                    foreach (MeldView m in player.Melds)
                    {
                        melds.Add(m.Clone());
                    }
                }

                var discards = new List<DiscardView>();
                if (player != null && player.Discards != null)
                {
                    foreach (DiscardView d in player.Discards)
                    {
                        discards.Add(d.Clone());
                    }
                }

                int concealedMeldCount = 0;
                if (player != null && player.ConcealedMelds != null)
                {
                    // Proof of existence without carrying any tile identities
                    concealedMeldCount = player.ConcealedMelds.Count;
                }

                seats.Add(new SpectatorSeatModel(
                    seatId,
                    seatName,
                    isDealer,
                    isCurrent,
                    isRiichi,
                    handCount,
                    score,
                    melds.AsReadOnly(),
                    discards.AsReadOnly(),
                    concealedMeldCount));
            }

            var clocks = new List<ClockView>();
            if (matchView.Clocks != null)
            {
                foreach (ClockView clk in matchView.Clocks)
                {
                    clocks.Add(clk.Clone());
                }
            }

            bool isMatchFinished = sessionSummary != null && sessionSummary.Finished;
            string finishReason = sessionSummary != null ? sessionSummary.FinishReason ?? string.Empty : string.Empty;

            var finalRankings = new List<int>();
            if (sessionSummary != null && sessionSummary.Rankings != null)
            {
                finalRankings.AddRange(sessionSummary.Rankings);
            }

            var finalScores = new List<ScoreView>();
            if (sessionSummary != null && sessionSummary.FinalScores != null)
            {
                foreach (ScoreView sc in sessionSummary.FinalScores)
                {
                    finalScores.Add(sc.Clone());
                }
            }

            bool isPaused = matchView.Paused || (board != null && board.Paused);

            return new SpectatorBoardModel(
                snapshot.SessionId ?? string.Empty,
                snapshot.RoundId ?? string.Empty,
                phase,
                phaseText,
                roundWind,
                roundWindText,
                roundNumber,
                roundText,
                honba,
                riichiSticks,
                dealerSeat,
                currentSeat,
                wallRemaining,
                doraList.AsReadOnly(),
                seats.AsReadOnly(),
                clocks.AsReadOnly(),
                isPaused,
                isMatchFinished,
                finishReason,
                finalRankings.AsReadOnly(),
                finalScores.AsReadOnly());
        }

        public static string MapWindText(uint wind)
        {
            return wind switch
            {
                0 => "东",
                1 => "南",
                2 => "西",
                3 => "北",
                _ => "?"
            };
        }

        public static string MapPhaseText(GamePhase phase)
        {
            return phase switch
            {
                GamePhase.Stacking => "洗牌码牌中",
                GamePhase.Dealing => "发牌中",
                GamePhase.Draw => "摸牌阶段",
                GamePhase.CheatWindow => "千术博弈窗口",
                GamePhase.Discard => "出牌阶段",
                GamePhase.ResponseWindow => "鸣牌/和了响应窗口",
                GamePhase.ChankanWindow => "抢杠响应窗口",
                GamePhase.KanDraw => "岭上摸牌",
                GamePhase.RoundEnd => "单局结算中",
                GamePhase.MatchEnd => "整场终局对局结束",
                _ => "对局等待就绪"
            };
        }

        public static string GetSeatWindName(uint roundWind, int dealerSeat, int seatId)
        {
            if (dealerSeat < 0 || dealerSeat > 3)
            {
                return $"席位{seatId}";
            }

            int seatOffset = (seatId - dealerSeat + 4) % 4;
            string seatWind = seatOffset switch
            {
                0 => "东",
                1 => "南",
                2 => "西",
                3 => "北",
                _ => "?"
            };

            return dealerSeat == seatId ? $"[庄] 席位{seatId}({seatWind})" : $"席位{seatId}({seatWind})";
        }
    }
}
