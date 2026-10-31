#nullable enable
using System;
using System.Collections.Generic;

namespace Mahjong.Unity.Spectator
{
    /// <summary>
    /// Static presentation mapping for official 41 Yaku codes, dora indicators, and draw game reasons.
    /// Strictly displays structured values; does not calculate scores, deduce yaku,
    /// or reverse-engineer points from event text.
    /// </summary>
    public static class YakuTextMapper
    {
        private static readonly Dictionary<string, string> YakuDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
            { "CHIIHOU", "地和" },

            // Dora
            { "DORA", "宝牌" },
            { "URA_DORA", "里宝牌" },
            { "AKA_DORA", "赤宝牌" },
            { "KITA", "拔北宝牌" }
        };

        public static string GetChineseName(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return "未知役种";
            }

            if (YakuDisplayNames.TryGetValue(code!, out string? name))
            {
                return name;
            }

            return $"未知役种({code})";
        }

        public static bool IsKnownYaku(string? code)
        {
            return !string.IsNullOrWhiteSpace(code) && YakuDisplayNames.ContainsKey(code!);
        }

        public static string GetDrawGameReasonLabel(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return "荒牌流局";
            }

            return reason switch
            {
                "RYUUKYOKU_INTERVAL" or "EXHAUSTIVE_DRAW" => "荒牌流局",
                "KYUUSHU_KYUUHAI" => "九种九牌",
                "SUUFON_RENDA" => "四风连打",
                "SUUKAN_SANGA" => "四杠散了",
                "SUUCHA_RIICHI" => "四家立直",
                "SANGOU_HOUCHI" => "三家和了",
                _ => $"流局({reason})"
            };
        }
    }
}
