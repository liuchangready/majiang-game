using System;
using System.Collections.Generic;
using System.Linq;

namespace Mahjong.Domain.Rules
{
    /// <summary>
    /// Frozen rulesetVersion=1 yaku catalogue. The 42 stable codes mirror V4
    /// <c>YakuEvaluator.Yaku</c>; display names and han values are migration data.
    /// </summary>
    public sealed class Yaku
    {
        private Yaku(string code, string displayName, int hanClosed, int hanOpen, bool isYakuman)
        {
            Code = code;
            DisplayName = displayName;
            HanClosed = hanClosed;
            HanOpen = hanOpen;
            IsYakuman = isYakuman;
        }

        public string Code { get; }

        public string DisplayName { get; }

        public int HanClosed { get; }

        public int HanOpen { get; }

        public bool IsYakuman { get; }

        public static readonly Yaku Riichi = new Yaku("RIICHI", "立直", 1, 0, false);
        public static readonly Yaku Ippatsu = new Yaku("IPPATSU", "一发", 1, 0, false);
        public static readonly Yaku MenzenTsumo = new Yaku("MENZEN_TSUMO", "门前清自摸和", 1, 0, false);
        public static readonly Yaku Tanyao = new Yaku("TANYAO", "断幺九", 1, 1, false);
        public static readonly Yaku Pinfu = new Yaku("PINFU", "平和", 1, 0, false);
        public static readonly Yaku Iipeiko = new Yaku("IIPEIKO", "一杯口", 1, 0, false);
        public static readonly Yaku YakuhaiHaku = new Yaku("YAKUHAI_HAKU", "役牌 白", 1, 1, false);
        public static readonly Yaku YakuhaiHatsu = new Yaku("YAKUHAI_HATSU", "役牌 发", 1, 1, false);
        public static readonly Yaku YakuhaiChun = new Yaku("YAKUHAI_CHUN", "役牌 中", 1, 1, false);
        public static readonly Yaku YakuhaiBakaze = new Yaku("YAKUHAI_BAKAZE", "场风牌", 1, 1, false);
        public static readonly Yaku YakuhaiJikaze = new Yaku("YAKUHAI_JIKAZE", "自风牌", 1, 1, false);
        public static readonly Yaku Haitei = new Yaku("HAITEI", "海底摸月", 1, 1, false);
        public static readonly Yaku Houtei = new Yaku("HOUTEI", "河底捞鱼", 1, 1, false);
        public static readonly Yaku Rinshan = new Yaku("RINSHAN", "岭上开花", 1, 1, false);
        public static readonly Yaku Chankan = new Yaku("CHANKAN", "抢杠", 1, 1, false);

        public static readonly Yaku DoubleRiichi = new Yaku("DOUBLE_RIICHI", "两立直", 2, 0, false);
        public static readonly Yaku Chanta = new Yaku("CHANTA", "混全带幺九", 2, 1, false);
        public static readonly Yaku Ittsu = new Yaku("ITTSU", "一气通贯", 2, 1, false);
        public static readonly Yaku SanShokuDoujun = new Yaku("SAN_SHOKU_DOUJUN", "三色同顺", 2, 1, false);
        public static readonly Yaku SanShokuDoukou = new Yaku("SAN_SHOKU_DOUKOU", "三色同刻", 2, 2, false);
        public static readonly Yaku Toitoi = new Yaku("TOITOI", "对对和", 2, 2, false);
        public static readonly Yaku SanAnkou = new Yaku("SAN_ANKOU", "三暗刻", 2, 2, false);
        public static readonly Yaku SanKantsu = new Yaku("SAN_KANTSU", "三杠子", 2, 2, false);
        public static readonly Yaku Shousangen = new Yaku("SHOUSANGEN", "小三元", 2, 2, false);
        public static readonly Yaku Honroutou = new Yaku("HONROUTOU", "混老头", 2, 2, false);
        public static readonly Yaku Chiitoitsu = new Yaku("CHIITOITSU", "七对子", 2, 0, false);

        public static readonly Yaku Honitsu = new Yaku("HONITSU", "混一色", 3, 2, false);
        public static readonly Yaku Junchan = new Yaku("JUNCHAN", "纯全带幺九", 3, 2, false);
        public static readonly Yaku Ryanpeikou = new Yaku("RYANPEIKOU", "两杯口", 3, 0, false);

        public static readonly Yaku Chinitsu = new Yaku("CHINITSU", "清一色", 6, 5, false);

        public static readonly Yaku Kokushi = new Yaku("KOKUSHI", "国士无双", 0, 0, true);
        public static readonly Yaku Suuankou = new Yaku("SUUANKOU", "四暗刻", 0, 0, true);
        public static readonly Yaku Daisangen = new Yaku("DAISANGEN", "大三元", 0, 0, true);
        public static readonly Yaku Shousuushii = new Yaku("SHOUSUUSHII", "小四喜", 0, 0, true);
        public static readonly Yaku Daisuushii = new Yaku("DAISUUSHII", "大四喜", 0, 0, true);
        public static readonly Yaku Tsuuiisou = new Yaku("TSUUIISOU", "字一色", 0, 0, true);
        public static readonly Yaku Ryuuiisou = new Yaku("RYUUIISOU", "绿一色", 0, 0, true);
        public static readonly Yaku Chinroutou = new Yaku("CHINROUTOU", "清老头", 0, 0, true);
        public static readonly Yaku Churenpoutou = new Yaku("CHURENPOUTOU", "九莲宝灯", 0, 0, true);
        public static readonly Yaku Suukantsu = new Yaku("SUUKANTSU", "四杠子", 0, 0, true);
        public static readonly Yaku Tenhou = new Yaku("TENHOU", "天和", 0, 0, true);
        public static readonly Yaku Chiihou = new Yaku("CHIIHOU", "地和", 0, 0, true);

        public static readonly IReadOnlyList<Yaku> All = Array.AsReadOnly(new[]
        {
            Riichi, Ippatsu, MenzenTsumo, Tanyao, Pinfu, Iipeiko,
            YakuhaiHaku, YakuhaiHatsu, YakuhaiChun, YakuhaiBakaze, YakuhaiJikaze,
            Haitei, Houtei, Rinshan, Chankan,
            DoubleRiichi, Chanta, Ittsu, SanShokuDoujun, SanShokuDoukou,
            Toitoi, SanAnkou, SanKantsu, Shousangen, Honroutou, Chiitoitsu,
            Honitsu, Junchan, Ryanpeikou, Chinitsu,
            Kokushi, Suuankou, Daisangen, Shousuushii, Daisuushii, Tsuuiisou,
            Ryuuiisou, Chinroutou, Churenpoutou, Suukantsu, Tenhou, Chiihou
        });

        private static readonly Dictionary<string, Yaku> ByCode =
            All.ToDictionary(y => y.Code, StringComparer.Ordinal);

        private static readonly Dictionary<string, Yaku> ByDisplayName =
            All.ToDictionary(y => y.DisplayName, StringComparer.Ordinal);

        public static Yaku? FromCode(string code)
        {
            if (code == null)
            {
                throw new ArgumentNullException(nameof(code));
            }

            if (ByCode.TryGetValue(code, out Yaku yaku))
            {
                return yaku;
            }

            return null;
        }

        public static Yaku? FromDisplayName(string displayName)
        {
            if (displayName == null)
            {
                throw new ArgumentNullException(nameof(displayName));
            }

            if (ByDisplayName.TryGetValue(displayName, out Yaku yaku))
            {
                return yaku;
            }

            return null;
        }

        public static bool IsKnownCode(string code)
        {
            return code != null && ByCode.ContainsKey(code);
        }

        public YakuHit Hit(int han)
        {
            if (han < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(han), han, "han cannot be negative.");
            }

            return new YakuHit(this, han);
        }
    }

    /// <summary>One hit yaku with the han actually awarded for the current open/closed context.</summary>
    public sealed class YakuHit
    {
        public YakuHit(Yaku yaku, int han)
        {
            Yaku = yaku ?? throw new ArgumentNullException(nameof(yaku));
            if (han < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(han), han, "han cannot be negative.");
            }

            Han = han;
        }

        public Yaku Yaku { get; }

        public int Han { get; }

        public string Code => Yaku.Code;

        public string DisplayName => Yaku.DisplayName;
    }
}
