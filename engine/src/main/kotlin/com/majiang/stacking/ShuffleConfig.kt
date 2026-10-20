package com.majiang.stacking

import java.util.Collections

/**
 * 统一码牌千术人数档位强度与成功交换额度配置。
 *
 * 档位按“看破抵消后仍有效施术者人数 n”取值（n=1..3；n=4 恒为完全无交换的恒等档，不在此配置）。
 * 这是独立、可命名、可测试、可调的平衡参数，绝不把目标胜率倍率直接乘到单张概率或结算结果上。
 *
 * @property strength 该人数档位的基础熟练度 s_n ∈ [0,1]
 * @property initialBudget 每席起手区成功交换额度 B_initial(n)
 * @property drawBudget 每席预计摸牌槽成功交换额度 B_draw(n)
 */
data class ShuffleTier(
    val strength: Double,
    val initialBudget: Int,
    val drawBudget: Int
) {
    init {
        require(!strength.isNaN() && strength in 0.0..1.0) { "tier strength must be in 0.0..1.0, got $strength" }
        require(initialBudget >= 0) { "tier initialBudget must be >= 0, got $initialBudget" }
        require(drawBudget >= 0) { "tier drawBudget must be >= 0, got $drawBudget" }
    }
}

/**
 * 洗牌千术全局与单局配置
 *
 * @property enabled 是否启用洗牌阶段千术（默认 false 保持兼容与原规则测试直接发牌）
 * @property selectionMillis 准备阶段选择时长（毫秒，默认 10000ms = 10s）
 * @property plannedDraws 统一目标影响的后续预计摸牌轮数（默认 18，覆盖 53..121 中本座位的全部 17/18 个预计槽）
 * @property strength 全局强度上限 ∈ [0,1]（默认 0.75）；实际档位强度取 min(档位 strength, 本值)，
 *                   因此显式设为 0.0 可关闭全部交换，避免“档位表存在时 strength 失效”。
 * @property interference 同目标资源冲突衰减系数 lambda >= 0.0（默认 0.65）
 * @property diversityInterference 异目标分化损耗系数 >= 0.0；对每个其他施术者按 `(1 - 余弦重叠)` 追加衰减，
 *                                使目标完全不重叠的 n>1 组合也受到人数档位约束（默认见 [DEFAULT_DIVERSITY_INTERFERENCE]）。
 * @property diversityBudgetPenalty 异目标额度损耗回退系数 >= 0.0；按 `1/(1 + 系数 * Σ(1-余弦))` 缩减成功交换额度。
 * @property diversityBudgetPenaltyByTier 按人数档位覆盖的异目标额度损耗系数；未覆盖的 n 回退到
 *                                        [diversityBudgetPenalty]。只影响目标不同的组合，同目标额度保持不变。
 * @property aiMaySpend AI 玩家是否允许消耗点数出千（默认 false）
 * @property seeThroughEnabled 是否允许准备阶段选用看破（默认 false；启用需洗牌与经济配置及固定 SEE_THROUGH=10）
 * @property tiers 人数档位 (n=1..3) 的强度与成功交换额度配置；n=4 恒为完全无交换，不参与该表。
 * @property targetTiers 可选的按目标覆盖档位；未覆盖的目标回退到人数档位。
 */
data class ShuffleConfig(
    val enabled: Boolean = false,
    val selectionMillis: Long = 10000L,
    val plannedDraws: Int = 18,
    val strength: Double = 0.75,
    val interference: Double = 0.65,
    val aiMaySpend: Boolean = false,
    val seeThroughEnabled: Boolean = false,
    val diversityInterference: Double = DEFAULT_DIVERSITY_INTERFERENCE,
    val diversityBudgetPenalty: Double = DEFAULT_DIVERSITY_BUDGET_PENALTY,
    private val diversityBudgetPenaltyTierMap: Map<Int, Double> = DEFAULT_DIVERSITY_BUDGET_PENALTY_BY_TIER,
    private val tierMap: Map<Int, ShuffleTier> = DEFAULT_TIERS,
    private val targetTierMap: Map<ShuffleTarget, Map<Int, ShuffleTier>> = DEFAULT_TARGET_TIERS,
    private val mixedTierMap: Map<Int, ShuffleTier> = DEFAULT_MIXED_TIERS
) {
    /**
     * 人数档位只读视图。每次访问返回防御性复制，调用方无法通过修改返回集合影响配置。
     */
    val tiers: Map<Int, ShuffleTier>
        get() = Collections.unmodifiableMap(LinkedHashMap(tierMap))

    /** 按目标覆盖档位只读视图（防御性复制）。 */
    val targetTiers: Map<ShuffleTarget, Map<Int, ShuffleTier>>
        get() = Collections.unmodifiableMap(
            targetTierMap.mapValues { (_, byCount) -> Collections.unmodifiableMap(LinkedHashMap(byCount)) }
        )

    /** 按人数覆盖的异目标额度损耗系数只读视图（防御性复制）。 */
    val diversityBudgetPenalties: Map<Int, Double>
        get() = Collections.unmodifiableMap(LinkedHashMap(diversityBudgetPenaltyTierMap))

    /** 异目标组合专用人数档位只读视图（防御性复制）。 */
    val mixedTiers: Map<Int, ShuffleTier>
        get() = Collections.unmodifiableMap(LinkedHashMap(mixedTierMap))

    init {
        require(selectionMillis > 0L) { "selectionMillis must be positive, got $selectionMillis" }
        require(plannedDraws in 0..18) { "plannedDraws must be in 0..18, got $plannedDraws" }
        require(!strength.isNaN() && strength in 0.0..1.0) { "strength must be in 0.0..1.0, got $strength" }
        require(!interference.isNaN() && !interference.isInfinite() && interference >= 0.0) {
            "interference must be finite and >= 0.0, got $interference"
        }
        require(!diversityInterference.isNaN() && !diversityInterference.isInfinite() && diversityInterference >= 0.0) {
            "diversityInterference must be finite and >= 0.0, got $diversityInterference"
        }
        require(!diversityBudgetPenalty.isNaN() && !diversityBudgetPenalty.isInfinite() && diversityBudgetPenalty >= 0.0) {
            "diversityBudgetPenalty must be finite and >= 0.0, got $diversityBudgetPenalty"
        }
        for ((n, penalty) in diversityBudgetPenaltyTierMap) {
            require(n in 1..3) { "diversity budget penalty tier must be in 1..3, got $n" }
            require(!penalty.isNaN() && !penalty.isInfinite() && penalty >= 0.0) {
                "diversity budget penalty must be finite and >= 0.0, got $penalty for n=$n"
            }
        }
        for (n in mixedTierMap.keys) {
            require(n in 1..3) { "mixed tier key must be in 1..3, got $n" }
        }
        for (n in tierMap.keys) {
            require(n in 1..3) { "tier key must be in 1..3, got $n" }
        }
        for ((target, byCount) in targetTierMap) {
            require(target in ShuffleTarget.entries) { "unknown target tier key: $target" }
            for (n in byCount.keys) {
                require(n in 1..3) { "target tier key must be in 1..3, got $n for $target" }
            }
        }
    }

    /**
     * 获取有效施术人数 n 对应的档位；n=4 返回 null（恒等档，无交换）。
     * 指定 [target] 时优先采用按目标覆盖档位，否则回退人数档位。
     * 未显式配置的 n 回退到 [strength] 与初始回退额度。
     *
     * [strength] 是全局上限而非被档位表覆盖的“死配置”：实际强度为
     * `min(档位 strength, strength)`。这样房间配置把 strength 设为 0.0 时
     * 仍能严格回退到基础洗牌，而默认 0.75 不会削弱已标定的低强度档位。
     */
    fun tierFor(n: Int, target: ShuffleTarget? = null): ShuffleTier? {
        require(n in 1..4) { "n must be in 1..4, got $n" }
        if (n == 4) return null
        val configured = if (target != null) {
            targetTierMap[target]?.get(n)
        } else {
            null
        } ?: tierMap[n] ?: ShuffleTier(strength, DEFAULT_INITIAL_BUDGET, DEFAULT_DRAW_BUDGET)
        return configured.copy(strength = configured.strength.coerceAtMost(strength))
    }

    /**
     * 取异目标组合专用档位；目标全部相同的桌不使用该档。
     * 未显式配置时回退到同目标人数档，保证 n=4 之外的调用总有可用档位。
     */
    fun mixedTierFor(n: Int): ShuffleTier? {
        require(n in 1..4) { "n must be in 1..4, got $n" }
        if (n == 4) return null
        val configured = mixedTierMap[n] ?: return tierFor(n)
        // 异目标专用档同样服从全局 strength 上限；否则 strength=0 时 n=2/3 仍会做牌。
        return configured.copy(strength = configured.strength.coerceAtMost(strength))
    }

    /** 返回替换单个 n 的异目标专用档位的配置副本。 */
    fun withMixedTier(n: Int, tier: ShuffleTier): ShuffleConfig {
        require(n in 1..3) { "mixed tier key must be in 1..3, got $n" }
        val next = LinkedHashMap(mixedTierMap)
        next[n] = tier
        return copy(mixedTierMap = next)
    }

    /** 取有效施术人数 n 的异目标额度损耗系数；未按人数覆盖时回退到全局值。 */
    fun diversityBudgetPenaltyFor(n: Int): Double {
        require(n in 1..4) { "n must be in 1..4, got $n" }
        return diversityBudgetPenaltyTierMap[n] ?: diversityBudgetPenalty
    }

    /** 返回使用统一异目标额度损耗系数（清空按人数覆盖）的配置副本。 */
    fun withUniformDiversityBudgetPenalty(penalty: Double): ShuffleConfig = copy(
        diversityBudgetPenalty = penalty,
        diversityBudgetPenaltyTierMap = emptyMap()
    )

    /** 返回替换单个 n 的异目标额度损耗系数的配置副本。 */
    fun withDiversityBudgetPenalty(n: Int, penalty: Double): ShuffleConfig {
        require(n in 1..3) { "diversity budget penalty tier must be in 1..3, got $n" }
        val next = LinkedHashMap(diversityBudgetPenaltyTierMap)
        next[n] = penalty
        return copy(diversityBudgetPenaltyTierMap = next)
    }

    fun deepCopy(): ShuffleConfig = copy(
        diversityBudgetPenaltyTierMap = LinkedHashMap(diversityBudgetPenaltyTierMap),
        tierMap = LinkedHashMap(tierMap),
        targetTierMap = targetTierMap.mapValues { (_, byCount) -> LinkedHashMap(byCount) },
        mixedTierMap = LinkedHashMap(mixedTierMap)
    )

    /** 返回替换全部人数档位的配置副本；供平衡实验与可命名测试使用。 */
    fun withTiers(tiers: Map<Int, ShuffleTier>): ShuffleConfig =
        copy(tierMap = LinkedHashMap(tiers))

    /** 返回替换单个有效施术人数档位的配置副本。target=null 时替换人数默认档。 */
    fun withTier(n: Int, tier: ShuffleTier, target: ShuffleTarget? = null): ShuffleConfig {
        require(n in 1..3) { "tier key must be in 1..3, got $n" }
        if (target == null) {
            val next = LinkedHashMap(tierMap)
            next[n] = tier
            return copy(tierMap = next)
        }
        val next = LinkedHashMap<ShuffleTarget, Map<Int, ShuffleTier>>()
        for ((t, byCount) in targetTierMap) next[t] = LinkedHashMap(byCount)
        val byCount = LinkedHashMap(next[target] ?: emptyMap())
        byCount[n] = tier
        next[target] = byCount
        return copy(targetTierMap = next)
    }

    companion object {
        /**
         * 异目标分化损耗初始标定值。
         *
         * 旧公式只按余弦重叠衰减：目标完全不重叠时 n 个施术者各自获得接近单人的熟练度，
         * 实测 n=2/3 异目标倍率可达 4–9 倍。该系数对每个“其他施术者”追加
         * `diversityInterference × (1 - cosine)` 的衰减，同目标（cosine=1）不受影响，
         * 异目标则回到人数档位目标附近。它是平衡参数，不是结算或单牌概率乘数。
         */
        const val DEFAULT_DIVERSITY_INTERFERENCE: Double = 2.5

        /**
         * 异目标额度损耗初始标定值。
         *
         * 异目标组合已由 [DEFAULT_MIXED_TIERS] 单独定档，因此该系数默认关闭；
         * 保留它用于进一步细调部分重叠或特殊房间。
         */
        const val DEFAULT_DIVERSITY_BUDGET_PENALTY: Double = 0.0

        /**
         * 按人数档位的异目标额度损耗值；默认空表，表示使用 [DEFAULT_MIXED_TIERS] 的显式额度。
         * 非空值仍可用于对混合档做额外分数缩放。
         */
        val DEFAULT_DIVERSITY_BUDGET_PENALTY_BY_TIER: Map<Int, Double> = emptyMap()

        /** 单人初始测量档成功交换额度（起手），仅作为未显式配置档位的回退。 */
        const val DEFAULT_INITIAL_BUDGET: Int = 3

        /** 单人初始测量档成功交换额度（预计摸牌槽），仅作为未显式配置档位的回退。 */
        const val DEFAULT_DRAW_BUDGET: Int = 4

        /**
         * 成对无头标定候选档（非对所有目标/对手的保证值）：
         * - n=1：s_1=0.52, B_initial=1, B_draw=4
         * - n=2：s_2=0.52, B_initial=2, B_draw=2
         * - n=3：s_3=0.65, B_initial=3, B_draw=5
         * - n=4：恒等（无交换，不在此表）
         *
         * 数值来自 CHINITSU_MAN + 基础 AI 对手的成对样本，须以
         * doc/shuffle-unification/SIMULATION-RESULTS.md 的区间和未达标项为准。
         */
        val DEFAULT_TIERS: Map<Int, ShuffleTier> = Collections.unmodifiableMap(
            LinkedHashMap<Int, ShuffleTier>().apply {
                put(1, ShuffleTier(0.52, 1, 4))
                put(2, ShuffleTier(0.52, 2, 2))
                put(3, ShuffleTier(0.65, 3, 5))
            }
        )

        /**
         * 异目标组合专用人数档位。
         *
         * 目标全部相同时使用按目标覆盖档；只要桌面上存在目标差异，所有施术者改用本表。
         * 这样同一套同目标校准不会被强/弱目标混搭放大或抵消，异目标组合可以独立命中
         * 1.75/1.50。额度较小是因为异目标时供体几乎不竞争，同样的成功次数会带来更高增益。
         */
        val DEFAULT_MIXED_TIERS: Map<Int, ShuffleTier> = Collections.unmodifiableMap(
            linkedMapOf(
                2 to ShuffleTier(0.52, 1, 1),
                3 to ShuffleTier(0.65, 1, 0)
            )
        )

        /**
         * 按目标覆盖档。目标感知 AI 的基线/成形效率差异很大，单一人数档无法同时命中
         * 2.50/1.75/1.50；这里用 3,000–20,000 对同目标样本分别校准：
         *
         * - 索子/筒子：目标 AI 的任意役增益偏强，n=2/3 缩减到 1+1/1+1 次成功交换；
         * - 大三元：目标 AI 的成形效率偏低，n=2/3 放大额度，但 n=3 仍受字牌供给约束；
         * - 国士无双：对牌山极敏感，显著弱化；
         * - 大四喜：提高起手/摸牌额度，n=2/3 接近目标；
         * - 万子清一色：n=2/3 单独降低额度，使同目标倍率回到 1.75/1.50 附近。
         *
         * 这些值仍是标定候选，不代表所有对手、异目标组合与真实玩家的保证。
         */
        val DEFAULT_TARGET_TIERS: Map<ShuffleTarget, Map<Int, ShuffleTier>> = Collections.unmodifiableMap(
            LinkedHashMap<ShuffleTarget, Map<Int, ShuffleTier>>().apply {
                put(
                    ShuffleTarget.KOKUSHI,
                    Collections.unmodifiableMap(
                        linkedMapOf(
                            1 to ShuffleTier(0.06, 1, 1),
                            2 to ShuffleTier(0.029, 0, 1),
                            3 to ShuffleTier(0.0175, 0, 1)
                        )
                    )
                )
                put(
                    ShuffleTarget.CHINITSU_MAN,
                    Collections.unmodifiableMap(
                        linkedMapOf(
                            2 to ShuffleTier(0.52, 1, 2),
                            3 to ShuffleTier(0.65, 1, 1)
                        )
                    )
                )
                put(
                    ShuffleTarget.DAISANGEN,
                    Collections.unmodifiableMap(
                        linkedMapOf(
                            2 to ShuffleTier(0.75, 5, 7),
                            3 to ShuffleTier(0.75, 8, 12)
                        )
                    )
                )
                put(
                    ShuffleTarget.DAISUUSHII,
                    Collections.unmodifiableMap(
                        linkedMapOf(
                            1 to ShuffleTier(0.75, 1, 5),
                            2 to ShuffleTier(0.75, 5, 7),
                            3 to ShuffleTier(0.75, 5, 8)
                        )
                    )
                )
                put(
                    ShuffleTarget.CHINITSU_SOU,
                    Collections.unmodifiableMap(
                        linkedMapOf(
                            2 to ShuffleTier(0.52, 1, 1),
                            3 to ShuffleTier(0.65, 1, 1)
                        )
                    )
                )
                put(
                    ShuffleTarget.CHINITSU_PIN,
                    Collections.unmodifiableMap(
                        linkedMapOf(
                            1 to ShuffleTier(0.58, 1, 5),
                            2 to ShuffleTier(0.52, 1, 1),
                            3 to ShuffleTier(0.65, 1, 1)
                        )
                    )
                )
            }
        )
    }
}
