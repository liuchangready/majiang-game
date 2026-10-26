using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mahjong.Domain.Stacking
{
    /// <summary>
    /// 统一码牌千术人数档位强度与成功交换额度配置
    /// （V4 <c>com.majiang.stacking.ShuffleTier</c> 的逐值移植）。
    ///
    /// 档位按“看破抵消后仍有效施术者人数 n”取值（n=1..3；n=4 恒为完全无交换的恒等档）。
    /// 这是独立、可命名、可测试、可调的平衡参数，绝不把目标胜率倍率直接乘到单张概率或结算结果上。
    /// </summary>
    public readonly struct ShuffleTier : IEquatable<ShuffleTier>
    {
        public ShuffleTier(double strength, int initialBudget, int drawBudget)
        {
            if (double.IsNaN(strength) || strength < 0.0 || strength > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(strength),
                    strength,
                    "tier strength must be in 0.0..1.0, got " + Format(strength) + ".");
            }

            if (initialBudget < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialBudget),
                    initialBudget,
                    "tier initialBudget must be >= 0, got " + initialBudget.ToString(CultureInfo.InvariantCulture) + ".");
            }

            if (drawBudget < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawBudget),
                    drawBudget,
                    "tier drawBudget must be >= 0, got " + drawBudget.ToString(CultureInfo.InvariantCulture) + ".");
            }

            Strength = strength;
            InitialBudget = initialBudget;
            DrawBudget = drawBudget;
        }

        /// <summary>该人数档位的基础熟练度 s_n ∈ [0,1]。</summary>
        public double Strength { get; }

        /// <summary>每席起手区成功交换额度 B_initial(n)。</summary>
        public int InitialBudget { get; }

        /// <summary>每席预计摸牌槽成功交换额度 B_draw(n)。</summary>
        public int DrawBudget { get; }

        /// <summary>应用全局强度上限：strength 取 min(本档 strength, cap)，额度不变。</summary>
        public ShuffleTier WithStrengthCap(double cap)
        {
            double capped = Strength < cap ? Strength : cap;
            if (capped == Strength)
            {
                return this;
            }

            return new ShuffleTier(capped, InitialBudget, DrawBudget);
        }

        public bool Equals(ShuffleTier other)
        {
            return Strength == other.Strength && InitialBudget == other.InitialBudget && DrawBudget == other.DrawBudget;
        }

        public override bool Equals(object? obj) => obj is ShuffleTier other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Strength.GetHashCode();
                hash = (hash * 397) ^ InitialBudget;
                hash = (hash * 397) ^ DrawBudget;
                return hash;
            }
        }

        public override string ToString()
        {
            return "ShuffleTier(s=" + Format(Strength) +
                   ", initial=" + InitialBudget.ToString(CultureInfo.InvariantCulture) +
                   ", draw=" + DrawBudget.ToString(CultureInfo.InvariantCulture) + ")";
        }

        public static bool operator ==(ShuffleTier left, ShuffleTier right) => left.Equals(right);

        public static bool operator !=(ShuffleTier left, ShuffleTier right) => !left.Equals(right);

        private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 洗牌千术全局与单局配置（V4 <c>com.majiang.stacking.ShuffleConfig</c> 的逐值移植）。
    ///
    /// 不可变值对象：所有 With* 方法返回全新副本；只读视图每次访问做防御性复制。
    /// 非法参数在构造边界即被拒绝（零副作用）。
    /// </summary>
    public sealed class ShuffleConfig
    {
        private static readonly IReadOnlyDictionary<int, ShuffleTier> DefaultTiers =
            new Dictionary<int, ShuffleTier>
            {
                { 1, new ShuffleTier(0.52, 1, 4) },
                { 2, new ShuffleTier(0.52, 2, 2) },
                { 3, new ShuffleTier(0.65, 3, 5) }
            };

        private static readonly IReadOnlyDictionary<int, ShuffleTier> DefaultMixedTiers =
            new Dictionary<int, ShuffleTier>
            {
                { 2, new ShuffleTier(0.52, 1, 1) },
                { 3, new ShuffleTier(0.65, 1, 0) }
            };

        private static readonly IReadOnlyDictionary<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>>
            DefaultTargetTiers = new Dictionary<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>>
            {
                {
                    ShuffleTarget.Kokushi, new Dictionary<int, ShuffleTier>
                    {
                        { 1, new ShuffleTier(0.06, 1, 1) },
                        { 2, new ShuffleTier(0.029, 0, 1) },
                        { 3, new ShuffleTier(0.0175, 0, 1) }
                    }
                },
                {
                    ShuffleTarget.ChinitsuMan, new Dictionary<int, ShuffleTier>
                    {
                        { 2, new ShuffleTier(0.52, 1, 2) },
                        { 3, new ShuffleTier(0.65, 1, 1) }
                    }
                },
                {
                    ShuffleTarget.Daisangen, new Dictionary<int, ShuffleTier>
                    {
                        { 2, new ShuffleTier(0.75, 5, 7) },
                        { 3, new ShuffleTier(0.75, 8, 12) }
                    }
                },
                {
                    ShuffleTarget.Daisuushii, new Dictionary<int, ShuffleTier>
                    {
                        { 1, new ShuffleTier(0.75, 1, 5) },
                        { 2, new ShuffleTier(0.75, 5, 7) },
                        { 3, new ShuffleTier(0.75, 5, 8) }
                    }
                },
                {
                    ShuffleTarget.ChinitsuSou, new Dictionary<int, ShuffleTier>
                    {
                        { 2, new ShuffleTier(0.52, 1, 1) },
                        { 3, new ShuffleTier(0.65, 1, 1) }
                    }
                },
                {
                    ShuffleTarget.ChinitsuPin, new Dictionary<int, ShuffleTier>
                    {
                        { 1, new ShuffleTier(0.58, 1, 5) },
                        { 2, new ShuffleTier(0.52, 1, 1) },
                        { 3, new ShuffleTier(0.65, 1, 1) }
                    }
                }
            };

        /// <summary>是否启用洗牌阶段千术（默认 false，保持兼容与原规则测试直接发牌）。</summary>
        public bool Enabled { get; }

        /// <summary>准备阶段选择时长（毫秒，默认 10000ms = 10s）。</summary>
        public long SelectionMillis { get; }

        /// <summary>统一目标影响的后续预计摸牌轮数（默认 18）。</summary>
        public int PlannedDraws { get; }

        /// <summary>全局强度上限 ∈ [0,1]（默认 0.75）；实际档位强度取 min(档位 strength, 本值)。</summary>
        public double Strength { get; }

        /// <summary>同目标资源冲突衰减系数 lambda >= 0.0（默认 0.65）。</summary>
        public double Interference { get; }

        /// <summary>异目标分化损耗系数 >= 0.0（默认 2.5）。</summary>
        public double DiversityInterference { get; }

        /// <summary>异目标额度损耗回退系数 >= 0.0（默认 0.0，已由混合档显式定档）。</summary>
        public double DiversityBudgetPenalty { get; }

        /// <summary>AI 玩家是否允许消耗点数出千（默认 false）。</summary>
        public bool AiMaySpend { get; }

        /// <summary>是否允许准备阶段选用看破（默认 false）。</summary>
        public bool SeeThroughEnabled { get; }

        public ShuffleConfig(
            bool enabled = false,
            long selectionMillis = 10000L,
            int plannedDraws = ShuffleSlots.DefaultPlannedDraws,
            double strength = 0.75,
            double interference = 0.65,
            bool aiMaySpend = false,
            bool seeThroughEnabled = false,
            double diversityInterference = DefaultDiversityInterference,
            double diversityBudgetPenalty = DefaultDiversityBudgetPenalty,
            IReadOnlyDictionary<int, double>? diversityBudgetPenaltyByTier = null,
            IReadOnlyDictionary<int, ShuffleTier>? tiers = null,
            IReadOnlyDictionary<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>>? targetTiers = null,
            IReadOnlyDictionary<int, ShuffleTier>? mixedTiers = null)
        {
            if (selectionMillis <= 0L)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(selectionMillis),
                    selectionMillis,
                    "selectionMillis must be positive, got " + selectionMillis.ToString(CultureInfo.InvariantCulture) + ".");
            }

            if (plannedDraws < 0 || plannedDraws > 18)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(plannedDraws),
                    plannedDraws,
                    "plannedDraws must be in 0..18, got " + plannedDraws.ToString(CultureInfo.InvariantCulture) + ".");
            }

            RequireBoundedRatio(strength, nameof(strength));
            RequireNonNegativeFinite(interference, nameof(interference));
            RequireNonNegativeFinite(diversityInterference, nameof(diversityInterference));
            RequireNonNegativeFinite(diversityBudgetPenalty, nameof(diversityBudgetPenalty));

            IReadOnlyDictionary<int, double> penaltyByTier =
                diversityBudgetPenaltyByTier ?? DefaultDiversityBudgetPenaltyByTier;
            foreach (KeyValuePair<int, double> entry in penaltyByTier)
            {
                if (entry.Key < 1 || entry.Key > 3)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(diversityBudgetPenaltyByTier),
                        entry.Key,
                        "diversity budget penalty tier must be in 1..3, got " +
                        entry.Key.ToString(CultureInfo.InvariantCulture) + ".");
                }

                RequireNonNegativeFinite(entry.Value, nameof(diversityBudgetPenaltyByTier));
            }

            IReadOnlyDictionary<int, ShuffleTier> tierMap = tiers ?? DefaultTiers;
            foreach (KeyValuePair<int, ShuffleTier> entry in tierMap)
            {
                RequireTierKey(entry.Key, nameof(tiers));
            }

            IReadOnlyDictionary<int, ShuffleTier> mixedTierMap = mixedTiers ?? DefaultMixedTiers;
            foreach (KeyValuePair<int, ShuffleTier> entry in mixedTierMap)
            {
                RequireTierKey(entry.Key, nameof(mixedTiers));
            }

            IReadOnlyDictionary<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>> targetTierMap =
                targetTiers ?? DefaultTargetTiers;
            foreach (KeyValuePair<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>> entry in targetTierMap)
            {
                ShuffleTargetRules.RequireDefined(entry.Key);
                foreach (KeyValuePair<int, ShuffleTier> byCount in entry.Value)
                {
                    RequireTierKey(byCount.Key, nameof(targetTiers));
                }
            }

            Enabled = enabled;
            SelectionMillis = selectionMillis;
            PlannedDraws = plannedDraws;
            Strength = strength;
            Interference = interference;
            AiMaySpend = aiMaySpend;
            SeeThroughEnabled = seeThroughEnabled;
            DiversityInterference = diversityInterference;
            DiversityBudgetPenalty = diversityBudgetPenalty;
            _diversityBudgetPenaltyByTier = penaltyByTier;
            _tiers = tierMap;
            _targetTiers = targetTierMap;
            _mixedTiers = mixedTierMap;
        }

        private readonly IReadOnlyDictionary<int, double> _diversityBudgetPenaltyByTier;
        private readonly IReadOnlyDictionary<int, ShuffleTier> _tiers;
        private readonly IReadOnlyDictionary<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>> _targetTiers;
        private readonly IReadOnlyDictionary<int, ShuffleTier> _mixedTiers;

        /// <summary>人数档位只读视图。每次访问返回防御性复制。</summary>
        public IReadOnlyDictionary<int, ShuffleTier> Tiers => new Dictionary<int, ShuffleTier>(_tiers);

        /// <summary>按目标覆盖档位只读视图（防御性复制）。</summary>
        public IReadOnlyDictionary<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>> TargetTiers
        {
            get
            {
                var copy = new Dictionary<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>>();
                foreach (KeyValuePair<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>> entry in _targetTiers)
                {
                    copy[entry.Key] = new Dictionary<int, ShuffleTier>(entry.Value);
                }

                return copy;
            }
        }

        /// <summary>按人数覆盖的异目标额度损耗系数只读视图（防御性复制）。</summary>
        public IReadOnlyDictionary<int, double> DiversityBudgetPenalties
        {
            get => new Dictionary<int, double>(_diversityBudgetPenaltyByTier);
        }

        /// <summary>异目标组合专用人数档位只读视图（防御性复制）。</summary>
        public IReadOnlyDictionary<int, ShuffleTier> MixedTiers => new Dictionary<int, ShuffleTier>(_mixedTiers);

        /// <summary>
        /// 获取有效施术人数 n 对应的档位；n=4 返回 null（恒等档，无交换）。
        /// 指定 target 时优先采用按目标覆盖档位，否则回退人数档位；
        /// 未显式配置的 n 回退到全局 strength 与初始回退额度。
        /// 实际强度为 min(档位 strength, 全局 strength)，保证 strength=0.0 严格回退基础洗牌。
        /// </summary>
        public ShuffleTier? TierFor(int n, ShuffleTarget? target = null)
        {
            RequireCount(n);
            if (n == 4)
            {
                return null;
            }

            ShuffleTier? configured = null;
            if (target.HasValue && _targetTiers.TryGetValue(target.Value, out IReadOnlyDictionary<int, ShuffleTier>? byCount))
            {
                if (byCount.TryGetValue(n, out ShuffleTier targetTier))
                {
                    configured = targetTier;
                }
            }

            if (!configured.HasValue)
            {
                if (_tiers.TryGetValue(n, out ShuffleTier countTier))
                {
                    configured = countTier;
                }
                else
                {
                    configured = new ShuffleTier(Strength, DefaultInitialBudget, DefaultDrawBudget);
                }
            }

            return configured.Value.WithStrengthCap(Strength);
        }

        /// <summary>
        /// 取异目标组合专用档位；目标全部相同的桌不使用该档。
        /// 未显式配置时回退到同目标人数档；同样服从全局 strength 上限。
        /// </summary>
        public ShuffleTier? MixedTierFor(int n)
        {
            RequireCount(n);
            if (n == 4)
            {
                return null;
            }

            if (_mixedTiers.TryGetValue(n, out ShuffleTier configured))
            {
                return configured.WithStrengthCap(Strength);
            }

            return TierFor(n);
        }

        /// <summary>取有效施术人数 n 的异目标额度损耗系数；未按人数覆盖时回退到全局值。</summary>
        public double DiversityBudgetPenaltyFor(int n)
        {
            RequireCount(n);
            return _diversityBudgetPenaltyByTier.TryGetValue(n, out double penalty) ? penalty : DiversityBudgetPenalty;
        }

        /// <summary>返回替换单个 n 的人数档位的配置副本。target=null 时替换人数默认档。</summary>
        public ShuffleConfig WithTier(int n, ShuffleTier tier, ShuffleTarget? target = null)
        {
            RequireTierKey(n, nameof(n));
            if (!target.HasValue)
            {
                var next = new Dictionary<int, ShuffleTier>(_tiers);
                next[n] = tier;
                return Replace(tiers: next);
            }

            var nextTarget = new Dictionary<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>>();
            foreach (KeyValuePair<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>> entry in _targetTiers)
            {
                nextTarget[entry.Key] = new Dictionary<int, ShuffleTier>(entry.Value);
            }

            if (!nextTarget.TryGetValue(target.Value, out IReadOnlyDictionary<int, ShuffleTier>? existing))
            {
                existing = new Dictionary<int, ShuffleTier>();
            }

            var byCount = new Dictionary<int, ShuffleTier>(existing);
            byCount[n] = tier;
            nextTarget[target.Value] = byCount;
            return Replace(targetTiers: nextTarget);
        }

        /// <summary>返回替换全部人数档位的配置副本；供平衡实验与可命名测试使用。</summary>
        public ShuffleConfig WithTiers(IReadOnlyDictionary<int, ShuffleTier> tiers)
        {
            return Replace(tiers: tiers);
        }

        /// <summary>返回替换单个 n 的异目标专用档位的配置副本。</summary>
        public ShuffleConfig WithMixedTier(int n, ShuffleTier tier)
        {
            RequireTierKey(n, nameof(n));
            var next = new Dictionary<int, ShuffleTier>(_mixedTiers);
            next[n] = tier;
            return Replace(mixedTiers: next);
        }

        /// <summary>返回替换单个 n 的异目标额度损耗系数的配置副本。</summary>
        public ShuffleConfig WithDiversityBudgetPenalty(int n, double penalty)
        {
            RequireTierKey(n, nameof(n));
            var next = new Dictionary<int, double>(_diversityBudgetPenaltyByTier);
            next[n] = penalty;
            return Replace(diversityBudgetPenaltyByTier: next);
        }

        /// <summary>返回使用统一异目标额度损耗系数（清空按人数覆盖）的配置副本。</summary>
        public ShuffleConfig WithUniformDiversityBudgetPenalty(double penalty)
        {
            return Replace(
                diversityBudgetPenalty: penalty,
                diversityBudgetPenaltyByTier: DefaultDiversityBudgetPenaltyByTier);
        }

        private ShuffleConfig Replace(
            double? diversityBudgetPenalty = null,
            IReadOnlyDictionary<int, double>? diversityBudgetPenaltyByTier = null,
            IReadOnlyDictionary<int, ShuffleTier>? tiers = null,
            IReadOnlyDictionary<ShuffleTarget, IReadOnlyDictionary<int, ShuffleTier>>? targetTiers = null,
            IReadOnlyDictionary<int, ShuffleTier>? mixedTiers = null)
        {
            return new ShuffleConfig(
                enabled: Enabled,
                selectionMillis: SelectionMillis,
                plannedDraws: PlannedDraws,
                strength: Strength,
                interference: Interference,
                aiMaySpend: AiMaySpend,
                seeThroughEnabled: SeeThroughEnabled,
                diversityInterference: DiversityInterference,
                diversityBudgetPenalty: diversityBudgetPenalty ?? DiversityBudgetPenalty,
                diversityBudgetPenaltyByTier: diversityBudgetPenaltyByTier ?? _diversityBudgetPenaltyByTier,
                tiers: tiers ?? _tiers,
                targetTiers: targetTiers ?? _targetTiers,
                mixedTiers: mixedTiers ?? _mixedTiers);
        }

        /// <summary>单人初始测量档成功交换额度（起手），仅作为未显式配置档位的回退。</summary>
        public const int DefaultInitialBudget = 3;

        /// <summary>单人初始测量档成功交换额度（预计摸牌槽），仅作为未显式配置档位的回退。</summary>
        public const int DefaultDrawBudget = 4;

        /// <summary>异目标分化损耗初始标定值（默认 2.5）：对每个“其他施术者”追加 diversityInterference x (1 - cosine) 衰减。</summary>
        public const double DefaultDiversityInterference = 2.5;

        /// <summary>异目标额度损耗初始标定值（默认 0.0）：异目标组合已由 DefaultMixedTiers 单独定档。</summary>
        public const double DefaultDiversityBudgetPenalty = 0.0;

        /// <summary>按人数档位的异目标额度损耗值；默认空表，表示使用 DefaultMixedTiers 的显式额度。</summary>
        public static IReadOnlyDictionary<int, double> DefaultDiversityBudgetPenaltyByTier { get; } =
            new Dictionary<int, double>();

        private static void RequireCount(int n)
        {
            if (n < 1 || n > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(n),
                    n,
                    "n must be in 1..4, got " + n.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        private static void RequireTierKey(int n, string paramName)
        {
            if (n < 1 || n > 3)
            {
                throw new ArgumentOutOfRangeException(
                    paramName,
                    n,
                    paramName + " tier key must be in 1..3, got " + n.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        private static void RequireBoundedRatio(double value, string paramName)
        {
            if (double.IsNaN(value) || value < 0.0 || value > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    paramName,
                    value,
                    paramName + " must be in 0.0..1.0, got " + value.ToString("R", CultureInfo.InvariantCulture) + ".");
            }
        }

        private static void RequireNonNegativeFinite(double value, string paramName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    paramName,
                    value,
                    paramName + " must be finite and >= 0.0, got " + value.ToString("R", CultureInfo.InvariantCulture) + ".");
            }
        }
    }
}
