#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using Mahjong.Client;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Spectator;
using Mahjong.Unity.UI.Session;
using ProtocolRole = Mahjong.Contracts.ProtocolRole;
using RequestScope = Mahjong.Contracts.RequestScope;
using RequestScopeId = Mahjong.Contracts.RequestScopeId;
using RoomId = Mahjong.Contracts.RoomId;
using SessionId = Mahjong.Contracts.SessionId;
using ConnectionEpoch = Mahjong.Contracts.ConnectionEpoch;
using OwnerEpoch = Mahjong.Contracts.OwnerEpoch;
using BindingEpoch = Mahjong.Contracts.BindingEpoch;
using StreamBaseline = Mahjong.Contracts.StreamBaseline;
using StateVersion = Mahjong.Contracts.StateVersion;
using EventSeq = Mahjong.Contracts.EventSeq;
using StreamCursor = Mahjong.Contracts.StreamCursor;
using StreamVersion = Mahjong.Contracts.StreamVersion;
using ProjectionSeq = Mahjong.Contracts.ProjectionSeq;

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class SettlementDisplayTests
    {
        private TableSessionBinding CreateBinding(string sessionId, string roomId = "room_101", ProtocolRole role = ProtocolRole.Spectator, int seat = -1)
        {
            return new TableSessionBinding(
                new SessionId(sessionId),
                new RoomId(roomId),
                "table_1",
                role,
                seat,
                new ConnectionEpoch(1),
                new OwnerEpoch(1),
                new BindingEpoch(1),
                new RequestScope(new RequestScopeId("scope_1"), "account_1", role, new RoomId(roomId), 1000, 2000),
                new StreamBaseline(new StateVersion(1), new EventSeq(1), new StreamCursor(new StreamVersion(1), new ProjectionSeq(1))));
        }

        [Test]
        public void YakuTextMapper_AllStandardYakuCodes_MappedToCorrectChineseNames()
        {
            var expectedNames = new Dictionary<string, string>
            {
                // 1 Han
                { "RIICHI", "立直" },
                { "IPPATSU", "一发" },
                { "MENZEN_TSUMO", "门前清自摸和" },
                { "TANYAO", "断幺九" },
                { "PINFU", "平和" },
                { "IIPEIKO", "一杯口" },
                { "YAKUHAI_HAKU", "役牌 白" },
                { "YAKUHAI_HATSU", "役牌 发" },
                { "YAKUHAI_CHUN", "役牌 中" },
                { "YAKUHAI_BAKAZE", "场风牌" },
                { "YAKUHAI_JIKAZE", "自风牌" },
                { "HAITEI", "海底摸月" },
                { "HOUTEI", "河底捞鱼" },
                { "RINSHAN", "岭上开花" },
                { "CHANKAN", "抢杠" },

                // 2 Han
                { "DOUBLE_RIICHI", "两立直" },
                { "CHANTA", "混全带幺九" },
                { "ITTSU", "一气通贯" },
                { "SAN_SHOKU_DOUJUN", "三色同顺" },
                { "SAN_SHOKU_DOUKOU", "三色同刻" },
                { "TOITOI", "对对和" },
                { "SAN_ANKOU", "三暗刻" },
                { "SAN_KANTSU", "三杠子" },
                { "SHOUSANGEN", "小三元" },
                { "HONROUTOU", "混老头" },
                { "CHIITOITSU", "七对子" },

                // 3 Han
                { "HONITSU", "混一色" },
                { "JUNCHAN", "纯全带幺九" },
                { "RYANPEIKOU", "两杯口" },

                // 6 Han
                { "CHINITSU", "清一色" },

                // Yakuman
                { "KOKUSHI", "国士无双" },
                { "SUUANKOU", "四暗刻" },
                { "DAISANGEN", "大三元" },
                { "SHOUSUUSHII", "小四喜" },
                { "DAISUUSHII", "大四喜" },
                { "TSUUIISOU", "字一色" },
                { "RYUUIISOU", "绿一色" },
                { "CHINROUTOU", "清老头" },
                { "CHURENPOUTOU", "九莲宝灯" },
                { "SUUKANTSU", "四杠子" },
                { "TENHOU", "天和" },
                { "CHIIHOU", "地和" }
            };

            foreach (var kvp in expectedNames)
            {
                string label = YakuTextMapper.GetChineseName(kvp.Key);
                Assert.AreEqual(kvp.Value, label, $"Yaku code {kvp.Key} mapped incorrectly.");
            }
        }

        [Test]
        public void YakuTextMapper_DoraCodes_MappedCorrectly()
        {
            Assert.AreEqual("宝牌", YakuTextMapper.GetChineseName("DORA"));
            Assert.AreEqual("里宝牌", YakuTextMapper.GetChineseName("URA_DORA"));
            Assert.AreEqual("赤宝牌", YakuTextMapper.GetChineseName("AKA_DORA"));
            Assert.AreEqual("拔北宝牌", YakuTextMapper.GetChineseName("KITA"));
        }

        [Test]
        public void YakuTextMapper_UnknownCode_ReturnsExplicitSafeFallbackWithoutGuessing()
        {
            string label = YakuTextMapper.GetChineseName("CUSTOM_YAKU_CODE");
            Assert.AreEqual("未知役种(CUSTOM_YAKU_CODE)", label);

            string labelNull = YakuTextMapper.GetChineseName(null);
            Assert.AreEqual("未知役种", labelNull);
        }

        [Test]
        public void YakuTextMapper_DrawGameReasons_MappedToExplicitChineseNames()
        {
            Assert.AreEqual("荒牌流局", YakuTextMapper.GetDrawGameReasonLabel("RYUUKYOKU_INTERVAL"));
            Assert.AreEqual("荒牌流局", YakuTextMapper.GetDrawGameReasonLabel("EXHAUSTIVE_DRAW"));
            Assert.AreEqual("九种九牌", YakuTextMapper.GetDrawGameReasonLabel("KYUUSHU_KYUUHAI"));
            Assert.AreEqual("四风连打", YakuTextMapper.GetDrawGameReasonLabel("SUUFON_RENDA"));
            Assert.AreEqual("四杠散了", YakuTextMapper.GetDrawGameReasonLabel("SUUKAN_SANGA"));
            Assert.AreEqual("四家立直", YakuTextMapper.GetDrawGameReasonLabel("SUUCHA_RIICHI"));
            Assert.AreEqual("三家和了", YakuTextMapper.GetDrawGameReasonLabel("SANGOU_HOUCHI"));

            Assert.AreEqual("流局(CUSTOM_REASON)", YakuTextMapper.GetDrawGameReasonLabel("CUSTOM_REASON"));
            Assert.AreEqual("荒牌流局", YakuTextMapper.GetDrawGameReasonLabel(null));
        }

        [Test]
        public void SettlementModelExtractor_ExtractRound_TsumoWin()
        {
            var publicSnap = new PublicSnapshot
            {
                SessionId = "sess_1",
                RoundId = "round_E1_0",
                View = new PublicMatchView
                {
                    SettlementRoundId = "round_E1_0"
                }
            };

            var tsumoEnv = new PublicEventEnvelope
            {
                TsumoWin = new TsumoWin
                {
                    Player = 0,
                    WinTileId = 5,
                    Han = 3,
                    Fu = 30,
                    Score = 3900
                }
            };
            tsumoEnv.TsumoWin.Yaku.Add(new YakuView { Code = "TANYAO", Han = 1 });
            tsumoEnv.TsumoWin.Yaku.Add(new YakuView { Code = "PINFU", Han = 1 });
            tsumoEnv.TsumoWin.Yaku.Add(new YakuView { Code = "MENZEN_TSUMO", Han = 1 });
            publicSnap.View.SettlementEvents.Add(tsumoEnv);

            var scoreEnv = new PublicEventEnvelope
            {
                ScoreSettled = new ScoreSettled()
            };
            scoreEnv.ScoreSettled.Payments.Add(new PaymentView { FromSeat = 1, ToSeat = 0, Amount = 1300 });
            scoreEnv.ScoreSettled.Payments.Add(new PaymentView { FromSeat = 2, ToSeat = 0, Amount = 1300 });
            scoreEnv.ScoreSettled.Payments.Add(new PaymentView { FromSeat = 3, ToSeat = 0, Amount = 1300 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 0, Score = 28900 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 1, Score = 23700 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 2, Score = 23700 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 3, Score = 23700 });
            publicSnap.View.SettlementEvents.Add(scoreEnv);

            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: CreateBinding("sess_1"),
                playerSnapshot: null,
                publicSnapshot: publicSnap,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null);

            var round = SettlementModelExtractor.ExtractRound(state);
            Assert.IsNotNull(round);
            Assert.AreEqual("round_E1_0", round!.RoundId);
            Assert.AreEqual(WinKind.Tsumo, round.Kind);
            Assert.AreEqual(0, round.WinnerSeat);
            Assert.AreEqual(-1, round.LoserSeat);
            Assert.AreEqual(3, round.Han);
            Assert.AreEqual(30, round.Fu);
            Assert.AreEqual(3900, round.Score);

            Assert.AreEqual(3, round.Yaku.Count);
            Assert.AreEqual("断幺九", round.Yaku[0].Label);
            Assert.AreEqual("平和", round.Yaku[1].Label);
            Assert.AreEqual("门前清自摸和", round.Yaku[2].Label);

            Assert.AreEqual(3, round.Payments.Count);
            Assert.AreEqual(1, round.Payments[0].FromSeat);
            Assert.AreEqual(0, round.Payments[0].ToSeat);
            Assert.AreEqual(1300, round.Payments[0].Amount);
            Assert.AreEqual("席位1 -> 席位0: 1300点", round.Payments[0].FormatTransfer());

            Assert.AreEqual(4, round.NewScores.Count);
            Assert.AreEqual(0, round.NewScores[0].Seat);
            Assert.AreEqual(28900, round.NewScores[0].Score);
        }

        [Test]
        public void SettlementModelExtractor_ExtractRound_RonWin()
        {
            var publicSnap = new PublicSnapshot
            {
                SessionId = "sess_1",
                RoundId = "round_E1_1",
                View = new PublicMatchView
                {
                    SettlementRoundId = "round_E1_1"
                }
            };

            var ronEnv = new PublicEventEnvelope
            {
                RonWin = new RonWin
                {
                    Winner = 2,
                    Loser = 1,
                    WinTileId = 9,
                    Han = 4,
                    Fu = 40,
                    Score = 8000
                }
            };
            ronEnv.RonWin.Yaku.Add(new YakuView { Code = "RIICHI", Han = 1 });
            ronEnv.RonWin.Yaku.Add(new YakuView { Code = "IPPATSU", Han = 1 });
            ronEnv.RonWin.Yaku.Add(new YakuView { Code = "TANYAO", Han = 1 });
            ronEnv.RonWin.Yaku.Add(new YakuView { Code = "IIPEIKO", Han = 1 });
            publicSnap.View.SettlementEvents.Add(ronEnv);

            var scoreEnv = new PublicEventEnvelope { ScoreSettled = new ScoreSettled() };
            scoreEnv.ScoreSettled.Payments.Add(new PaymentView { FromSeat = 1, ToSeat = 2, Amount = 8000 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 0, Score = 25000 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 1, Score = 17000 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 2, Score = 33000 });
            scoreEnv.ScoreSettled.NewScores.Add(new ScoreView { Seat = 3, Score = 25000 });
            publicSnap.View.SettlementEvents.Add(scoreEnv);

            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: CreateBinding("sess_1"),
                playerSnapshot: null,
                publicSnapshot: publicSnap,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null);

            var round = SettlementModelExtractor.ExtractRound(state);
            Assert.IsNotNull(round);
            Assert.AreEqual(WinKind.Ron, round!.Kind);
            Assert.AreEqual(2, round.WinnerSeat);
            Assert.AreEqual(1, round.LoserSeat);
            Assert.AreEqual(8000, round.Score);
            Assert.AreEqual(4, round.Yaku.Count);
            Assert.AreEqual(1, round.Payments.Count);
            Assert.AreEqual(1, round.Payments[0].FromSeat);
            Assert.AreEqual(2, round.Payments[0].ToSeat);
            Assert.AreEqual(8000, round.Payments[0].Amount);
            Assert.AreEqual("席位1 -> 席位2: 8000点", round.Payments[0].FormatTransfer());

            Assert.AreEqual(4, round.NewScores.Count);
            Assert.AreEqual(2, round.NewScores[2].Seat);
            Assert.AreEqual(33000, round.NewScores[2].Score);
        }

        [Test]
        public void SettlementModelExtractor_ExtractRound_DrawGame()
        {
            var publicSnap = new PublicSnapshot
            {
                SessionId = "sess_1",
                RoundId = "round_E2_0",
                View = new PublicMatchView
                {
                    SettlementRoundId = "round_E2_0"
                }
            };

            var drawEnv = new PublicEventEnvelope
            {
                DrawGame = new DrawGame
                {
                    Reason = "KYUUSHU_KYUUHAI"
                }
            };
            publicSnap.View.SettlementEvents.Add(drawEnv);

            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: CreateBinding("sess_1"),
                playerSnapshot: null,
                publicSnapshot: publicSnap,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null);

            var round = SettlementModelExtractor.ExtractRound(state);
            Assert.IsNotNull(round);
            Assert.AreEqual(WinKind.Draw, round!.Kind);
            Assert.AreEqual("KYUUSHU_KYUUHAI", round.DrawReason);
            Assert.AreEqual("九种九牌", round.DrawReasonText);
        }

        [Test]
        public void SettlementModelExtractor_ExtractMatch_RankingsExtractedCorrectly()
        {
            var publicSnap = new PublicSnapshot
            {
                SessionId = "sess_1",
                View = new PublicMatchView
                {
                    Session = new PublicSessionSummary
                    {
                        Finished = true,
                        FinishReason = "ROUND_LIMIT"
                    }
                }
            };
            publicSnap.View.Session.Rankings.AddRange(new int[] { 2, 0, 1, 3 });
            publicSnap.View.Session.FinalScores.Add(new ScoreView { Seat = 0, Score = 32000 });
            publicSnap.View.Session.FinalScores.Add(new ScoreView { Seat = 1, Score = 15000 });
            publicSnap.View.Session.FinalScores.Add(new ScoreView { Seat = 2, Score = 41000 });
            publicSnap.View.Session.FinalScores.Add(new ScoreView { Seat = 3, Score = 12000 });

            var state = new SessionViewState(
                generation: 1,
                state: TableConnectionState.Ready,
                binding: CreateBinding("sess_1"),
                playerSnapshot: null,
                publicSnapshot: publicSnap,
                roomSnapshot: null,
                pendingCommands: null,
                lastError: null);

            var match = SettlementModelExtractor.ExtractMatch(state);
            Assert.IsTrue(match.IsFinished);
            Assert.AreEqual(4, match.Rankings.Count);

            // 1st: Seat 2 (41000)
            Assert.AreEqual(1, match.Rankings[0].Rank);
            Assert.AreEqual(2, match.Rankings[0].Seat);
            Assert.AreEqual(41000, match.Rankings[0].FinalScore);

            // 2nd: Seat 0 (32000)
            Assert.AreEqual(2, match.Rankings[1].Rank);
            Assert.AreEqual(0, match.Rankings[1].Seat);
            Assert.AreEqual(32000, match.Rankings[1].FinalScore);

            // 3rd: Seat 1 (15000)
            Assert.AreEqual(3, match.Rankings[2].Rank);
            Assert.AreEqual(1, match.Rankings[2].Seat);
            Assert.AreEqual(15000, match.Rankings[2].FinalScore);

            // 4th: Seat 3 (12000)
            Assert.AreEqual(4, match.Rankings[3].Rank);
            Assert.AreEqual(3, match.Rankings[3].Seat);
            Assert.AreEqual(12000, match.Rankings[3].FinalScore);
        }
    }
}
