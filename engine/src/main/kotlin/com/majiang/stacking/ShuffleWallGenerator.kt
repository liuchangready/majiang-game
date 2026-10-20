package com.majiang.stacking

import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.model.TilePool
import java.util.Collections
import kotlin.math.abs
import kotlin.random.Random

/**
 * 确定性统一码牌千术牌山生成器
 *
 * 核心设计与物理守恒规则：
 * 1. 严格 136 张物理守恒：基于 TilePool.buildStandardSet() 构建，包含全量赤宝牌与 copyIndex 副本身份；
 * 2. 确定性重现：给定相同 seed、dealerSeat、plans 与 config，必定产出完全一致的牌山序列；
 * 3. 零千术不变律：无出千方案（全部 target==null 或 config.enabled==false）与空方案列表产出完全一致的基础洗牌；
 * 4. 排序无关性：输入 plans 列表顺序不影响结果（按 seat 升序规范化）；
 * 5. 单一目标：每席只有一个 34 维需求向量，起手与摸牌共用同一目标，强度只计算一次；
 * 6. 四人恒等律：n==4（四人均有效施术）时直接返回同种子基础洗牌，不做任何交换，逐张相等；
 * 7. 王牌隔离律：王牌区 122..135 绝不作目标槽，也绝不作交换供体来源；
 * 8. 槽位归属：玩家只优化本人槽位（起手 0..52，摸牌 53..121）；起手完成后 0..52 整体锁定；
 * 9. 人数档位强度与成功交换额度：按有效施术人数 n 取 ShuffleConfig.tierFor(n)，失败尝试不耗额度；
 * 10. 多人冲突衰减：p_i = s_i / (1 + lambda * Σ s_j * cosine + diversity * Σ s_j * (1 - cosine))，结果限于 [0,1]；
 *     同目标由 lambda 处理，异目标由 diversity 处理，避免零重叠时无任何人数衰减；
 *     异目标同时按 `1/(1 + budgetPenalty * Σ(1-cosine))` 缩减成功交换额度，防止熟练度仍高时额度被耗尽；
 * 11. 供体保守规则：只从非已提交、非王牌的活牌山槽取供体；本人尚待处理槽绝不可借用，
 *     其他施术者尚待处理槽仅在换入牌不降低其目标效用时才可借用。
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

        // 1. 基础确定性洗牌（保证 136 张物理守恒）
        val rng = Random(seed)
        val wall = TilePool.buildStandardSet().toMutableList()
        wall.shuffle(rng)

        // 2. 规范化方案（按席位排序保证输入顺序无关）
        val canonicalPlans = plans.sortedBy { it.seat }

        // 3. 有效施术方案：target 非空才参与做牌（看破抵消后的有效方案由权威层传入）
        val activePlans = if (config.enabled) {
            canonicalPlans.filter { it.selection.target != null }
        } else {
            emptyList()
        }

        // 若千术未启用或无人做牌，直接返回基础洗牌（与空方案一致）
        if (activePlans.isEmpty()) {
            return Collections.unmodifiableList(wall.toList())
        }

        val n = activePlans.size

        // 4. 四人档硬性恒等：四人均有效施术时直接返回同种子基础洗牌，不做任何交换。
        if (n == 4) {
            return Collections.unmodifiableList(wall.toList())
        }

        // 同目标桌按目标取覆盖档；存在异目标时全桌改用混合专用档，避免强/弱目标混搭
        // 把同目标校准放大或抵消。n=4 已在上面恒等返回。
        val mixedTargetTable = activePlans.map { it.selection.target }.distinct().size > 1
        val tierBySeat = activePlans.associate { plan ->
            val tier = if (mixedTargetTable) {
                config.mixedTierFor(n)
            } else {
                config.tierFor(n, plan.selection.target)
            } ?: throw IllegalStateException("tier for n=$n, target=${plan.selection.target} must be non-null")
            plan.seat to tier
        }

        // 5. 每席一个 34 维需求向量，只计算一次有效熟练度（同目标冲突 + 异目标分化损耗）
        val strengths = computeEffectiveStrengths(
            plans = activePlans,
            baseStrengthBySeat = tierBySeat.mapValues { (_, tier) -> tier.strength },
            lambda = config.interference,
            diversity = config.diversityInterference
        )

        // 6. 槽位映射（本人起手槽 / 本人预计摸牌槽）
        val initialSlotsMap = (0..3).associateWith { seat -> ShuffleSlots.initial(dealerSeat, seat) }
        val drawSlotsMap = (0..3).associateWith { seat -> ShuffleSlots.draws(dealerSeat, seat, config.plannedDraws) }

        // 目标槽 -> 施术席位。其他施术者尚待处理的目标槽只有在“换入牌不降低其目标效用”时才可借用；
        // 本人尚待处理的目标槽始终不可作为供体。
        val targetOwners = LinkedHashMap<Int, Int>()
        for (plan in activePlans) {
            for (slot in initialSlotsMap.getValue(plan.seat)) targetOwners[slot] = plan.seat
            for (slot in drawSlotsMap.getValue(plan.seat)) targetOwners[slot] = plan.seat
        }
        val planBySeat = activePlans.associateBy { it.seat }

        val committedSlots = mutableSetOf<Int>()

        // 各席位成功交换额度（失败尝试不耗额度）。异目标按分化损耗缩减为小数额度。
        val budgetScaleBySeat = computeBudgetScales(activePlans, config.diversityBudgetPenaltyFor(n))
        val initialBudget = activePlans.associate {
            it.seat to tierBySeat.getValue(it.seat).initialBudget * budgetScaleBySeat.getValue(it.seat)
        }.toMutableMap()
        val drawBudget = activePlans.associate {
            it.seat to tierBySeat.getValue(it.seat).drawBudget * budgetScaleBySeat.getValue(it.seat)
        }.toMutableMap()

        // 7. 阶段一：起手发牌槽优化 (0..52)。随机打乱活跃席位与本人槽位，消除固定席位优先级并让所有槽位有被选机会。
        for (seat in activePlans.map { it.seat }.shuffled(rng)) {
            val plan = activePlans.first { it.seat == seat }
            val slots = initialSlotsMap[seat]!!.shuffled(rng)
            val strength = strengths.getValue(seat)
            for (targetSlot in slots) {
                val remaining = initialBudget[seat] ?: 0.0
                if (remaining <= 0.0) break
                // 异目标额度可能是小数：按剩余额度概率允许最后一次尝试，得到期望额度。
                if (remaining < 1.0 && rng.nextDouble() >= remaining) break
                committedSlots.add(targetSlot)
                // 有效熟练度只决定一次“正收益交换尝试”是否发生；失败不耗成功额度。
                if (rng.nextDouble() >= strength) continue
                val contextTiles = initialSlotsMap[seat]!!
                    .filter { it != targetSlot }
                    .map { wall[it] }
                val swapped = improveSlot(
                    wall = wall,
                    targetSlot = targetSlot,
                    seat = seat,
                    target = plan.selection.target!!,
                    contextTiles = contextTiles,
                    committedSlots = committedSlots,
                    targetOwners = targetOwners,
                    planBySeat = planBySeat,
                    initialSlotsMap = initialSlotsMap,
                    drawSlotsMap = drawSlotsMap,
                    rng = rng
                )
                if (swapped) initialBudget[seat] = (initialBudget[seat] ?: 0.0) - 1.0
            }
        }

        // 起手发牌完毕后，所有起手槽位 (0..52) 锁定，摸牌阶段绝不可从已发手牌抽牌作为 donor。
        for (s in 0 until ShuffleSlots.INITIAL_DEAL_TILES) {
            committedSlots.add(s)
        }

        // 8. 阶段二：后续预计摸牌槽优化 (53..121)。随机打乱各席位可尝试槽，使首尾均有非零受益机会。
        for (seat in activePlans.map { it.seat }.shuffled(rng)) {
            val plan = activePlans.first { it.seat == seat }
            val slots = drawSlotsMap[seat]!!.shuffled(rng)
            val strength = strengths.getValue(seat)
            for (targetSlot in slots) {
                val remaining = drawBudget[seat] ?: 0.0
                if (remaining <= 0.0) break
                if (remaining < 1.0 && rng.nextDouble() >= remaining) break
                committedSlots.add(targetSlot)
                if (rng.nextDouble() >= strength) continue
                val contextTiles = drawVirtualHand(
                    seat = seat,
                    wall = wall,
                    slot = targetSlot,
                    initialSlots = initialSlotsMap[seat]!!,
                    drawSlots = drawSlotsMap[seat]!!,
                    target = plan.selection.target!!
                )
                val swapped = improveSlot(
                    wall = wall,
                    targetSlot = targetSlot,
                    seat = seat,
                    target = plan.selection.target!!,
                    contextTiles = contextTiles,
                    committedSlots = committedSlots,
                    targetOwners = targetOwners,
                    planBySeat = planBySeat,
                    initialSlotsMap = initialSlotsMap,
                    drawSlotsMap = drawSlotsMap,
                    rng = rng
                )
                if (swapped) drawBudget[seat] = (drawBudget[seat] ?: 0.0) - 1.0
            }
        }

        return Collections.unmodifiableList(wall.toList())
    }

    /**
     * 计算各活跃席位的有效熟练度（同目标冲突 + 异目标分化损耗）：
     *
     * `p_i = s_i / (1 + lambda * Σ s_j * cosine + diversity * Σ s_j * (1 - cosine))`
     *
     * 第一项沿用原设计，惩罚争夺同一类牌的施术者；第二项只惩罚“目标不同”的施术者，
     * 使完全不重叠的 n>1 组合不会各自获得接近单人的熟练度。两项都是概率/额度层参数，
     * 不改变结算、役种判定或物理牌张。
     */
    private fun computeEffectiveStrengths(
        plans: List<SeatShufflePlan>,
        baseStrengthBySeat: Map<Int, Double>,
        lambda: Double,
        diversity: Double
    ): Map<Int, Double> {
        val profiles = plans.associate { plan ->
            plan.seat to ShuffleTargets.demand(plan.selection.target!!)
        }
        val strengths = mutableMapOf<Int, Double>()
        for (plan in plans) {
            val myProfile = profiles[plan.seat]!!
            val ownStrength = baseStrengthBySeat.getValue(plan.seat)
            var interference = 0.0
            var divergence = 0.0
            for (other in plans) {
                if (other.seat != plan.seat) {
                    val otherProfile = profiles[other.seat]!!
                    val otherStrength = baseStrengthBySeat.getValue(other.seat)
                    val similarity = ShuffleTargets.cosineSimilarity(myProfile, otherProfile)
                    interference += otherStrength * similarity
                    divergence += otherStrength * (1.0 - similarity)
                }
            }
            val denominator = 1.0 + lambda * interference + diversity * divergence
            val p = ownStrength / denominator
            strengths[plan.seat] = p.coerceIn(0.0, 1.0)
        }
        return strengths
    }

    /**
     * 计算各席位的成功交换额度缩放系数：
     * `scale_i = 1 / (1 + penalty * Σ_{j≠i}(1 - cosine(d_i,d_j)))`。
     *
     * 同目标时所有余弦为 1，缩放为 1；目标越分散，额度越低。额度缩减是必要的：
     * 只降低熟练度时，只要尝试次数仍大于额度，最后仍会耗尽完整额度，异目标增益不会下降。
     */
    private fun computeBudgetScales(
        plans: List<SeatShufflePlan>,
        penalty: Double
    ): Map<Int, Double> {
        if (penalty <= 0.0) return plans.associate { it.seat to 1.0 }
        val profiles = plans.associate { it.seat to ShuffleTargets.demand(it.selection.target!!) }
        return plans.associate { plan ->
            val myProfile = profiles.getValue(plan.seat)
            var divergence = 0.0
            for (other in plans) {
                if (other.seat == plan.seat) continue
                divergence += 1.0 - ShuffleTargets.cosineSimilarity(myProfile, profiles.getValue(other.seat))
            }
            plan.seat to (1.0 / (1.0 + penalty * divergence))
        }
    }

    /**
     * 有界采样优化单个槽位：仅当存在 U(donor,H) > U(current,H) 的正收益供体时按 Δ² 确定性抽样交换。
     * 返回是否发生交换。
     */
    private fun improveSlot(
        wall: MutableList<Tile>,
        targetSlot: Int,
        seat: Int,
        target: ShuffleTarget,
        contextTiles: List<Tile>,
        committedSlots: Set<Int>,
        targetOwners: Map<Int, Int>,
        planBySeat: Map<Int, SeatShufflePlan>,
        initialSlotsMap: Map<Int, List<Int>>,
        drawSlotsMap: Map<Int, List<Int>>,
        rng: Random
    ): Boolean {
        val currentTile = wall[targetSlot]
        val currentUtil = evaluateTile(currentTile, target, contextTiles)

        class Candidate(val slot: Int, val delta: Double)

        val candidates = ArrayList<Candidate>()

        // 仅在活牌山 0..121 内挑选供体，绝不触碰王牌区 122..135。
        // 本人尚待处理的目标槽不可借用；其他施术者的尚待目标槽仅在换入牌不降低其目标效用时可借用。
        for (d in 0 until ShuffleSlots.MAIN_WALL_TILES) {
            if (d == targetSlot || d in committedSlots) continue
            val owner = targetOwners[d]
            if (owner == seat) continue
            if (owner != null) {
                val otherPlan = planBySeat[owner] ?: continue
                val otherTarget = otherPlan.selection.target ?: continue
                val otherContext = contextForSlot(
                    seat = owner,
                    slot = d,
                    wall = wall,
                    initialSlots = initialSlotsMap.getValue(owner),
                    drawSlots = drawSlotsMap.getValue(owner),
                    target = otherTarget
                )
                val donorForOther = evaluateTile(wall[d], otherTarget, otherContext)
                val incomingForOther = evaluateTile(currentTile, otherTarget, otherContext)
                if (incomingForOther < donorForOther) continue
            }
            val donorTile = wall[d]
            val donorUtil = evaluateTile(donorTile, target, contextTiles)
            val delta = donorUtil - currentUtil
            if (delta > 0.0) {
                candidates.add(Candidate(d, delta))
            }
        }

        if (candidates.isEmpty()) return false

        // 正收益平方加权确定性抽样
        val totalWeight = candidates.sumOf { it.delta * it.delta }
        if (totalWeight <= 0.0) return false

        var pick = rng.nextDouble() * totalWeight
        var chosen = candidates.first()
        for (c in candidates) {
            val w = c.delta * c.delta
            pick -= w
            if (pick <= 0.0) {
                chosen = c
                break
            }
        }

        // 物理槽位原地对调（保持 136 张守恒、copyIndex 与赤五身份）
        val temp = wall[targetSlot]
        wall[targetSlot] = wall[chosen.slot]
        wall[chosen.slot] = temp
        return true
    }

    /**
     * 摸牌段确定性虚拟手牌（约 13 张）：
     * 以本席位起手前 13 张为底，按自然顺位逐次“摸入”目标槽之前的牌，
     * 每次确定性打出目标效用最低的牌（并列按 typeIndex/copyIndex），保持手牌不累计膨胀。
     */
    private fun drawVirtualHand(
        seat: Int,
        wall: List<Tile>,
        slot: Int,
        initialSlots: List<Int>,
        drawSlots: List<Int>,
        target: ShuffleTarget
    ): List<Tile> {
        val hand = ArrayList<Tile>()
        // 统一取 13 张底牌：庄家的第 14 张（槽 52）不进入虚拟手牌，保持四席一致。
        for (s in initialSlots.take(13)) {
            hand.add(wall[s])
        }
        for (d in drawSlots.sorted()) {
            if (d >= slot) break
            hand.add(wall[d])
            discardLowestUtility(hand, target)
        }
        return hand
    }

    /**
     * 计算某施术者在指定目标槽被评估时的上下文手牌。
     * 起手槽：本人其余起手牌；预计摸牌槽：13 张底牌 + 之前预计摸牌并确定性弃牌后的虚拟手牌。
     */
    private fun contextForSlot(
        seat: Int,
        slot: Int,
        wall: List<Tile>,
        initialSlots: List<Int>,
        drawSlots: List<Int>,
        target: ShuffleTarget
    ): List<Tile> {
        if (slot in initialSlots) {
            return initialSlots.filter { it != slot }.map { wall[it] }
        }
        return drawVirtualHand(seat, wall, slot, initialSlots, drawSlots, target)
    }

    private fun discardLowestUtility(hand: MutableList<Tile>, target: ShuffleTarget) {
        if (hand.size <= 1) return
        val discard = hand.minWithOrNull(
            compareBy<Tile> { evaluateTile(it, target, hand) }
                .thenBy { it.typeIndex }
                .thenBy { it.copyIndex }
        ) ?: return
        hand.remove(discard)
    }

    /**
     * 评估一张牌在单一目标与上下文手牌下的效用
     */
    fun evaluateTile(candidateTile: Tile, target: ShuffleTarget, contextTiles: List<Tile>): Double {
        return when (target) {
            ShuffleTarget.KOKUSHI -> {
                // 国士无双：13 种幺九牌
                if (candidateTile.typeIndex !in ShuffleTargets.KOKUSHI_INDICES) return 0.0
                val count = contextTiles.count { it.typeIndex == candidateTile.typeIndex }
                when (count) {
                    0 -> 10.0 // 极力优先补齐缺失孤张
                    1 -> {
                        val pairExists = ShuffleTargets.KOKUSHI_INDICES.any { idx ->
                            contextTiles.count { it.typeIndex == idx } >= 2
                        }
                        if (!pairExists) 5.0 else 1.0
                    }
                    2 -> 0.2
                    else -> 0.0
                }
            }

            ShuffleTarget.DAISANGEN -> {
                if (candidateTile.suit != Suit.DRAGON) return 0.0
                honorTripletUtility(candidateTile, contextTiles)
            }

            ShuffleTarget.DAISUUSHII -> {
                if (candidateTile.suit != Suit.WIND) return 0.0
                honorTripletUtility(candidateTile, contextTiles)
            }

            ShuffleTarget.CHINITSU_MAN -> evaluateSuitUtility(candidateTile, Suit.MAN, contextTiles)
            ShuffleTarget.CHINITSU_SOU -> evaluateSuitUtility(candidateTile, Suit.SOU, contextTiles)
            ShuffleTarget.CHINITSU_PIN -> evaluateSuitUtility(candidateTile, Suit.PIN, contextTiles)
        }
    }

    private fun honorTripletUtility(tile: Tile, contextTiles: List<Tile>): Double {
        val count = contextTiles.count { it.typeIndex == tile.typeIndex }
        return when (count) {
            0 -> 6.0
            1 -> 8.0
            2 -> 10.0
            else -> 1.0
        }
    }

    /**
     * 清一色目标效用计算：花色张数 + 搭子临近度 + 雀头/刻子 + 赤宝牌
     */
    private fun evaluateSuitUtility(tile: Tile, targetSuit: Suit, contextTiles: List<Tile>): Double {
        if (tile.suit != targetSuit) return 0.0

        var utility = 4.0

        val sameSuitTiles = contextTiles.filter { it.suit == targetSuit }
        val sameRankCount = sameSuitTiles.count { it.rank == tile.rank }
        val adj1Count = sameSuitTiles.count { abs(it.rank - tile.rank) == 1 }
        val adj2Count = sameSuitTiles.count { abs(it.rank - tile.rank) == 2 }

        utility += when (sameRankCount) {
            1 -> 2.5
            2 -> 3.5
            else -> 0.0
        }

        if (adj1Count > 0) {
            utility += 2.0
        } else if (adj2Count > 0) {
            utility += 1.0
        }

        if (tile.isRedFive) {
            utility += 0.5
        }

        return utility
    }
}
