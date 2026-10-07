package com.majiang.stacking

import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.model.TilePool
import java.util.Collections
import kotlin.math.abs
import kotlin.random.Random

/**
 * 确定性牌山洗牌与自适应千术生成器
 *
 * 核心设计与物理守恒规则：
 * 1. 严格 136 张物理守恒：基于 TilePool.buildStandardSet() 构建，包含全量赤宝牌与 copyIndex 副本身份；
 * 2. 确定性重现：给定相同 seed、dealerSeat、plans 与 config，必定产出完全一致的牌山序列；
 * 3. 零千术不变律：无出千方案（或方案为空、全部未选目标、或 config.enabled==false）与完全空方案列表产出完全一致的基础洗牌结果；
 * 4. 排序无关性：输入 plans 列表的顺序不影响洗牌结果（按 seat 升序规范化后再进行确定性洗牌处理）；
 * 5. 席位公平性：轮次处理中按种子随机打乱活跃席位顺序，消除固定席位优先级；
 * 6. 王牌隔离律：王牌区 122..135 绝对不作为做牌目标槽位，也绝对不作为交换供体 (donor) 来源；
 * 7. 槽位归属与不可变性：玩家只能优化归属于本人的槽位（起手 0..52，后续摸牌 53..121），已提交的槽位锁定为 committed，不可被后续做牌篡改；
 * 8. 多人定向冲突衰减：基于 34 维需求向量计算各玩家余弦重叠度，零重叠完全不衰减，强重叠依 lambda 精准削弱，起手与摸牌两阶段独立计算衰减；
 * 9. 智能牌型启发式：国士无双优先补齐未持有的孤张幺九与单雀头，避免堆叠 4 张；大三元/大四喜优先在各目标牌种成刻；清一色注重花色数量、搭子临近度与雀头对子。
 */
class ShuffleWallGenerator {

    /**
     * 执行确定性牌山生成
     *
     * @param seed 随机数种子（通常为局级别权威随机源）
     * @param dealerSeat 当前局庄家座位 (0..3)
     * @param plans 各席位洗牌千术方案列表
     * @param config 洗牌千术配置
     * @return 长度为 136 的不可变物理牌山列表
     */
    fun generate(
        seed: Long,
        dealerSeat: Int,
        plans: List<SeatShufflePlan>,
        config: ShuffleConfig = ShuffleConfig(enabled = true)
    ): List<Tile> {
        // 1. 参数强校验
        require(dealerSeat in 0..3) { "dealerSeat must be 0..3, got $dealerSeat" }
        require(config.selectionMillis > 0L) { "selectionMillis must be positive, got ${config.selectionMillis}" }
        require(config.plannedDraws in 0..18) { "plannedDraws must be in 0..18, got ${config.plannedDraws}" }
        require(!config.strength.isNaN() && config.strength in 0.0..1.0) {
            "strength must be in 0.0..1.0, got ${config.strength}"
        }
        require(!config.interference.isNaN() && !config.interference.isInfinite() && config.interference >= 0.0) {
            "interference must be finite and >= 0.0, got ${config.interference}"
        }
        for (plan in plans) {
            require(plan.seat in 0..3) { "plan seat must be 0..3, got ${plan.seat}" }
        }
        require(plans.map { it.seat }.distinct().size == plans.size) {
            "plans must have unique seats, got ${plans.map { it.seat }}"
        }

        // 2. 基础确定性洗牌（保证 136 张物理守恒）
        val rng = Random(seed)
        val wall = TilePool.buildStandardSet().toMutableList()
        wall.shuffle(rng)

        // 3. 规范化方案（按席位排序以保证输入顺序无关）
        val canonicalPlans = plans.sortedBy { it.seat }

        val activeInitialPlans = canonicalPlans.filter { it.selection.initialTargets.isNotEmpty() }
        val activeDrawPlans = canonicalPlans.filter { it.selection.drawTargets.isNotEmpty() }

        // 若千术未启用或全员无做牌目标，直接返回基础洗牌结果（保证与空方案完全一致）
        if (!config.enabled || (activeInitialPlans.isEmpty() && activeDrawPlans.isEmpty())) {
            return Collections.unmodifiableList(wall.toList())
        }

        // 4. 分阶段独立计算有效熟练度（定向余弦衰减模型）
        val effectiveInitialStrength = computeEffectiveStrengths(
            plans = activeInitialPlans,
            targetSelector = { it.selection.initialTargets },
            baseStrength = config.strength,
            lambda = config.interference
        )

        val effectiveDrawStrength = computeEffectiveStrengths(
            plans = activeDrawPlans,
            targetSelector = { it.selection.drawTargets },
            baseStrength = config.strength,
            lambda = config.interference
        )

        // 5. 准备槽位映射
        val initialSlotsMap = (0..3).associateWith { seat -> ShuffleSlots.initial(dealerSeat, seat) }
        val drawSlotsMap = (0..3).associateWith { seat -> ShuffleSlots.draws(dealerSeat, seat, config.plannedDraws) }

        val allTargetSlots = mutableSetOf<Int>()
        for (plan in activeInitialPlans) {
            allTargetSlots.addAll(initialSlotsMap[plan.seat] ?: emptyList())
        }
        for (plan in activeDrawPlans) {
            allTargetSlots.addAll(drawSlotsMap[plan.seat] ?: emptyList())
        }

        val committedSlots = mutableSetOf<Int>()

        // 6. 阶段一：起手发牌槽位优化 (0..52)
        if (activeInitialPlans.isNotEmpty()) {
            for (round in 0 until 14) {
                val roundSeats = activeInitialPlans
                    .filter { round < (initialSlotsMap[it.seat]?.size ?: 0) }
                    .map { it.seat }
                    .shuffled(rng) // 消除固定席位优先级

                for (seat in roundSeats) {
                    val targetSlot = initialSlotsMap[seat]!![round]
                    val plan = activeInitialPlans.first { it.seat == seat }
                    val effStrength = effectiveInitialStrength[seat] ?: config.strength

                    if (rng.nextDouble() <= effStrength) {
                        val contextTiles = initialSlotsMap[seat]!!
                            .filter { it != targetSlot }
                            .map { wall[it] }

                        improveSlot(
                            wall = wall,
                            targetSlot = targetSlot,
                            targets = plan.selection.initialTargets,
                            contextTiles = contextTiles,
                            committedSlots = committedSlots,
                            allTargetSlots = allTargetSlots,
                            activePlans = activeInitialPlans,
                            isInitialStage = true,
                            rng = rng
                        )
                    }
                    committedSlots.add(targetSlot)
                }
            }
        }

        // 起手发牌完毕后，所有起手槽位 (0..52) 锁定为已提交，后续摸牌阶段绝不可从手牌抽牌作为 donor
        for (s in 0 until ShuffleSlots.INITIAL_DEAL_TILES) {
            committedSlots.add(s)
        }

        // 7. 阶段二：后续预计摸牌槽位优化 (53..121)
        if (activeDrawPlans.isNotEmpty()) {
            val maxDrawRounds = activeDrawPlans.maxOfOrNull { drawSlotsMap[it.seat]?.size ?: 0 } ?: 0
            for (round in 0 until maxDrawRounds) {
                val roundSeats = activeDrawPlans
                    .filter { round < (drawSlotsMap[it.seat]?.size ?: 0) }
                    .map { it.seat }
                    .shuffled(rng) // 消除固定席位优先级

                for (seat in roundSeats) {
                    val targetSlot = drawSlotsMap[seat]!![round]
                    val plan = activeDrawPlans.first { it.seat == seat }
                    val effStrength = effectiveDrawStrength[seat] ?: config.strength

                    if (rng.nextDouble() <= effStrength) {
                        // 基于虚拟起手牌和在此之前的已规划摸牌，不假设任意未来丢弃
                        val initialTiles = initialSlotsMap[seat]!!.map { wall[it] }
                        val priorDrawTiles = (0 until round).map { wall[drawSlotsMap[seat]!![it]] }
                        val contextTiles = initialTiles + priorDrawTiles

                        improveSlot(
                            wall = wall,
                            targetSlot = targetSlot,
                            targets = plan.selection.drawTargets,
                            contextTiles = contextTiles,
                            committedSlots = committedSlots,
                            allTargetSlots = allTargetSlots,
                            activePlans = activeDrawPlans,
                            isInitialStage = false,
                            rng = rng
                        )
                    }
                    committedSlots.add(targetSlot)
                }
            }
        }

        return Collections.unmodifiableList(wall.toList())
    }

    /**
     * 计算特定阶段下各活跃席位的有效熟练度（定向余弦衰减模型）
     */
    private fun computeEffectiveStrengths(
        plans: List<SeatShufflePlan>,
        targetSelector: (SeatShufflePlan) -> Set<ShuffleTarget>,
        baseStrength: Double,
        lambda: Double
    ): Map<Int, Double> {
        val strengths = mutableMapOf<Int, Double>()
        for (plan in plans) {
            val myProfile = ShuffleTargets.combined(targetSelector(plan))
            var interference = 0.0
            for (other in plans) {
                if (other.seat != plan.seat) {
                    val otherProfile = ShuffleTargets.combined(targetSelector(other))
                    val overlap = ShuffleTargets.cosineSimilarity(myProfile, otherProfile)
                    interference += baseStrength * overlap
                }
            }
            val decay = 1.0 / (1.0 + lambda * interference)
            strengths[plan.seat] = (baseStrength * decay).coerceIn(0.0, 1.0)
        }
        return strengths
    }

    /**
     * 有界采样优化单个槽位
     */
    private fun improveSlot(
        wall: MutableList<Tile>,
        targetSlot: Int,
        targets: Set<ShuffleTarget>,
        contextTiles: List<Tile>,
        committedSlots: Set<Int>,
        allTargetSlots: Set<Int>,
        activePlans: List<SeatShufflePlan>,
        isInitialStage: Boolean,
        rng: Random
    ) {
        val currentTile = wall[targetSlot]
        val currentUtil = evaluateTile(currentTile, targets, contextTiles)

        class Candidate(val slot: Int, val tile: Tile, val delta: Double)

        val preferredCandidates = ArrayList<Candidate>()
        val secondaryCandidates = ArrayList<Candidate>()

        // 仅在活牌山 0..121 内挑选供体，绝不触碰王牌区 122..135
        for (d in 0 until ShuffleSlots.MAIN_WALL_TILES) {
            if (d == targetSlot || d in committedSlots) continue

            val donorTile = wall[d]
            val donorUtil = evaluateTile(donorTile, targets, contextTiles)
            val delta = donorUtil - currentUtil

            if (delta > 0.0) {
                if (d !in allTargetSlots) {
                    // 优先从非目标牌槽位中挑选
                    preferredCandidates.add(Candidate(d, donorTile, delta))
                } else {
                    // 若从他人未提交的槽位借牌，必须确保对方完全不需要该牌
                    val wantedByOther = activePlans.any { otherPlan ->
                        val otherTargets = if (isInitialStage) {
                            otherPlan.selection.initialTargets
                        } else {
                            otherPlan.selection.drawTargets
                        }
                        evaluateTile(donorTile, otherTargets, emptyList()) > 0.0
                    }
                    if (!wantedByOther) {
                        secondaryCandidates.add(Candidate(d, donorTile, delta))
                    }
                }
            }
        }

        val pool = if (preferredCandidates.isNotEmpty()) preferredCandidates else secondaryCandidates
        if (pool.isEmpty()) return

        // 目标效用与均匀随机混合采样（以 delta^2 为权重的确定性抽样）
        val totalWeight = pool.sumOf { it.delta * it.delta }
        if (totalWeight <= 0.0) return

        var pick = rng.nextDouble() * totalWeight
        var chosen = pool.first()
        for (c in pool) {
            val w = c.delta * c.delta
            pick -= w
            if (pick <= 0.0) {
                chosen = c
                break
            }
        }

        // 物理槽位原地对调（保持 136 张守恒与所有物理属性）
        val temp = wall[targetSlot]
        wall[targetSlot] = wall[chosen.slot]
        wall[chosen.slot] = temp
    }

    /**
     * 评估一张牌在当前多目标与上下文手牌下的综合效用
     * 多目标等权归一化，总强度不叠加
     */
    fun evaluateTile(candidateTile: Tile, targets: Set<ShuffleTarget>, contextTiles: List<Tile>): Double {
        if (targets.isEmpty()) return 0.0
        var sum = 0.0
        for (target in targets) {
            sum += singleTargetUtility(candidateTile, target, contextTiles)
        }
        return sum / targets.size
    }

    /**
     * 单一做牌目标的效用启发式评分
     */
    private fun singleTargetUtility(tile: Tile, target: ShuffleTarget, contextTiles: List<Tile>): Double {
        return when (target) {
            ShuffleTarget.KOKUSHI -> {
                // 国士无双：13 种幺九牌
                if (tile.typeIndex !in ShuffleTargets.KOKUSHI_INDICES) return 0.0
                val count = contextTiles.count { it.typeIndex == tile.typeIndex }
                when (count) {
                    0 -> 10.0 // 极力优先补齐缺失孤张
                    1 -> {
                        // 寻找是否已有雀头
                        val pairExists = ShuffleTargets.KOKUSHI_INDICES.any { idx ->
                            contextTiles.count { it.typeIndex == idx } >= 2
                        }
                        if (!pairExists) 5.0 else 1.0 // 无雀头时优先成对，已有雀头时降低
                    }
                    2 -> 0.2 // 第三张几乎无用
                    else -> 0.0 // 严格避免堆积 4 张同种幺九
                }
            }

            ShuffleTarget.DAISANGEN -> {
                // 大三元：白、發、中
                if (tile.suit != Suit.DRAGON) return 0.0
                val count = contextTiles.count { it.typeIndex == tile.typeIndex }
                when (count) {
                    0 -> 6.0  // 首张三元牌
                    1 -> 8.0  // 成对
                    2 -> 10.0 // 达成刻子！
                    else -> 1.0 // 已成刻子，边际收益骤降，优先留牌给其他三元牌
                }
            }

            ShuffleTarget.DAISUUSHII -> {
                // 大四喜：東、南、西、北
                if (tile.suit != Suit.WIND) return 0.0
                val count = contextTiles.count { it.typeIndex == tile.typeIndex }
                when (count) {
                    0 -> 6.0  // 首张风牌
                    1 -> 8.0  // 成对
                    2 -> 10.0 // 达成刻子！
                    else -> 1.0 // 已成刻子，边际收益骤降，优先留牌给其他风牌
                }
            }

            ShuffleTarget.CHINITSU_MAN -> evaluateSuitUtility(tile, Suit.MAN, contextTiles)
            ShuffleTarget.CHINITSU_SOU -> evaluateSuitUtility(tile, Suit.SOU, contextTiles)
            ShuffleTarget.CHINITSU_PIN -> evaluateSuitUtility(tile, Suit.PIN, contextTiles)
        }
    }

    /**
     * 清一色目标效用计算：花色张数 + 搭子临近度 + 雀头/刻子 + 赤宝牌
     */
    private fun evaluateSuitUtility(tile: Tile, targetSuit: Suit, contextTiles: List<Tile>): Double {
        if (tile.suit != targetSuit) return 0.0

        // 基础花色张数效用
        var utility = 4.0

        val sameSuitTiles = contextTiles.filter { it.suit == targetSuit }
        val sameRankCount = sameSuitTiles.count { it.rank == tile.rank }
        val adj1Count = sameSuitTiles.count { abs(it.rank - tile.rank) == 1 }
        val adj2Count = sameSuitTiles.count { abs(it.rank - tile.rank) == 2 }

        // 对子与刻子加成
        utility += when (sameRankCount) {
            1 -> 2.5 // 成对
            2 -> 3.5 // 成刻
            else -> 0.0
        }

        // 顺子邻近搭子加成（两面/嵌张搭子）
        if (adj1Count > 0) {
            utility += 2.0 // 相邻搭子 (如 2与3)
        } else if (adj2Count > 0) {
            utility += 1.0 // 间隔搭子 (如 1与3)
        }

        // 赤宝牌微量价值加成
        if (tile.isRedFive) {
            utility += 0.5
        }

        return utility
    }
}
