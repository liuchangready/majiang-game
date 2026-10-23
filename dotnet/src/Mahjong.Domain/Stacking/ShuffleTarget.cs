using System;

namespace Mahjong.Domain.Stacking
{
    /// <summary>
    /// 统一码牌千术目标（V4 <c>com.majiang.stacking.ShuffleTarget</c>）。
    /// 每名玩家准备阶段最多提交一个目标；4 人全部生效时码牌完全恒等（规则）。
    /// </summary>
    public enum ShuffleTarget : byte
    {
        /// <summary>国士无双（十三幺）：十三种幺九各一张。</summary>
        Kokushi = 0,

        /// <summary>清一色·万：只收集万子。</summary>
        ChinitsuMan = 1,

        /// <summary>大三元：白/发/中三张刻子。</summary>
        Daisangen = 2,

        /// <summary>大四喜：东南西北四张刻子。</summary>
        Daisuushii = 3,

        /// <summary>清一色·索：只收集索子。</summary>
        ChinitsuSou = 4,

        /// <summary>清一色·饼：只收集饼子。</summary>
        ChinitsuPin = 5
    }

    /// <summary>枚举合法性守卫；非法枚举值在构造边界即被拒绝。</summary>
    public static class ShuffleTargetRules
    {
        public static bool IsDefined(ShuffleTarget target)
        {
            return target >= ShuffleTarget.Kokushi && target <= ShuffleTarget.ChinitsuPin;
        }

        public static void RequireDefined(ShuffleTarget target)
        {
            if (!IsDefined(target))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(target),
                    target,
                    "Unknown shuffle target: " + ((byte)target).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            }
        }
    }
}
