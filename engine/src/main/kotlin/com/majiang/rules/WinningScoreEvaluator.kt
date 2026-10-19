package com.majiang.rules

import com.majiang.model.PlayerHand
import com.majiang.model.Tile

/**
 * 宝牌指示牌命中记录
 *
 * @property indicator 翻开的宝牌指示牌
 * @property target 该指示牌对应的目标宝牌（标准牌面，非赤牌，copyIndex=0）
 * @property tiles 手牌与副露中实际命中的物理牌列表（保留物理 copyIndex，含红五）
 */
data class DoraIndicatorHit(val indicator: Tile, val target: Tile, val tiles: List<Tile>)

/**
 * 宝牌翻数明细（表/里/赤三分列 + 命中具体牌列表）
 *
 * @property indicatorCount 表宝牌命中张数（不含里）
 * @property uraCount 里宝牌命中张数（仅立直时非零）
 * @property redCount 赤宝牌张数
 * @property totalHan 总宝牌翻数 (= indicator + ura + red)
 * @property indicatorTiles 命中的具体牌（表）
 * @property uraTiles 命中的具体牌（里）
 * @property redTiles 命中的红五
 * @property indicatorHits 逐个表宝牌指示牌的命中项（含零命中）
 * @property uraHits 逐个里宝牌指示牌的命中项（含零命中，仅立直非空）
 */
data class DoraBreakdown(
    val indicatorCount: Int,
    val uraCount: Int = 0,
    val redCount: Int,
    val totalHan: Int = indicatorCount + uraCount + redCount,
    val indicatorTiles: List<Tile> = emptyList(),
    val uraTiles: List<Tile> = emptyList(),
    val redTiles: List<Tile> = emptyList(),
    val indicatorHits: List<DoraIndicatorHit> = emptyList(),
    val uraHits: List<DoraIndicatorHit> = emptyList()
) {
    val total: Int get() = totalHan
}

/**
 * 和牌结算完整推导链汇总结构
 *
 * @property yakuLines 最优拆解下命中的全部役种 (役名, 翻数)
 * @property yakumanCount 役满倍数 (0=非役满)
 * @property dora 宝牌三分列明细与具体牌列表
 * @property fu 符数构成明细
 * @property score 基本点与计分结果
 * @property paymentLines 支付分配说明行
 * @property honbaAddition 本场棒追加得点 (300 × 本场)
 * @property riichiAddition 供托立直棒追加得点 (1000 × 根数)
 * @property totalPoints 最终获得的总点数
 * @property uraEligible 是否具备里宝牌资格（仅立直和牌为 true）
 * @property isDealer 是否庄家（结算时上下文）
 * @property isTsumo 是否自摸（结算时上下文）
 * @property yakuEntries 结构化役种行（稳定码 + 既有展示名 + 番数）；v5 起进入线路
 * @property paymentPlan 基础支付结构（由 score/isDealer/isTsumo 派生，不含本场棒与供托）
 */
data class ScoreBreakdown(
    val yakuLines: List<Pair<String, Int>>,
    val yakumanCount: Int,
    val dora: DoraBreakdown,
    val fu: FuBreakdown,
    val score: ScoreCalculator.ScoreResult,
    val paymentLines: List<String>,
    val honbaAddition: Int,
    val riichiAddition: Int,
    val totalPoints: Int,
    val uraEligible: Boolean = false,
    val isDealer: Boolean = false,
    val isTsumo: Boolean = false,
    /**
     * 结构化役种行（稳定码 + 展示名 + 番数）。
     *
     * 默认由 [yakuLines] 的旧展示名反查生成，保证既有构造点无需改动即可得到稳定码；
     * [WinningScoreEvaluator] 会显式传入由 [Yaku] 直接生成的条目。该字段不进入当前
     * MJ-TCP/1 线路，既有 yakuLines 字符串仍是唯一线路字段。
     */
    val yakuEntries: List<YakuEntry> = ScoreTextFormatter.yakuEntriesFromLegacy(yakuLines)
) {
    init {
        require(yakuEntries.size == yakuLines.size) {
            "yakuEntries.size (${yakuEntries.size}) must match yakuLines.size (${yakuLines.size})"
        }
        for (i in yakuLines.indices) {
            val entry = yakuEntries[i]
            val line = yakuLines[i]
            require(entry.displayName == line.first && entry.han == line.second) {
                "yakuEntries[$i] does not match yakuLines[$i]: $entry vs $line"
            }
        }
    }

    /** 基础支付结构（不含本场棒与供托立直棒）。 */
    val paymentPlan: PaymentPlan
        get() = score.paymentPlan(isDealer = isDealer, isTsumo = isTsumo)
}

class WinningScoreEvaluator(
    private val decomposer: HandDecomposer = HandDecomposer.INSTANCE,
    private val yakuEvaluator: YakuEvaluator = YakuEvaluator(),
    private val fuCalculator: FuCalculator = FuCalculator.INSTANCE,
    private val scoreCalculator: ScoreCalculator = ScoreCalculator.INSTANCE
) {
    typealias DoraBreakdown = com.majiang.rules.DoraBreakdown

    /**
     * 单个拆解方案的完整和了评估结果
     *
     * @property decomposition 所选择的手牌拆解
     * @property yakuResult 该拆解下的役种评价结果
     * @property fu 该拆解下的符数
     * @property scoreResult 该拆解下的基本点与得点计算结果
     * @property totalPoints 和牌所得总点数（自摸为闲家+庄家付款总和，荣和为放铳者基础支付总额）
     */
    data class Candidate(
        val decomposition: HandDecomposer.Decomposition,
        val yakuResult: YakuEvaluator.YakuResult,
        val fu: Int,
        val scoreResult: ScoreCalculator.ScoreResult,
        val totalPoints: Int
    )

    /**
     * 对和牌手牌的所有拆解进行全局评估，挑选最终得点最高的合法解释
     *
     * @param hand 玩家当前手牌
     * @param context 和牌上下文环境（和了牌、自摸/荣和、门前、自风、场风、立直等）
     * @param isDealer 是否庄家（默认根据 context.seatWind == 1 自动判定）
     * @param extraHan 宝牌/里宝牌等不属于独立役种的附加翻数
     * @return 最终得点最高的合法解释结果，若无役或无法和牌则返回 null
     */
    fun evaluate(
        hand: PlayerHand,
        context: YakuEvaluator.WinContext,
        isDealer: Boolean = (context.seatWind == 1),
        extraHan: Int = 0
    ): Candidate? {
        val evalHand = prepareEvaluationHand(hand, context.winTile, context.isTsumo)
        val decompositions = decomposer.decompose(evalHand.allClosedTiles)
        if (decompositions.isEmpty()) return null

        val candidates = mutableListOf<Candidate>()

        for (decomp in decompositions) {
            val yaku = yakuEvaluator.evaluateForDecomposition(decomp, evalHand, context)
            // 必须有合法役种（役满 或 totalHan > 0），禁止无役和了
            if (!yaku.isYakuman && yaku.totalHan <= 0) {
                continue
            }

            val (fu, scoreResult) = if (yaku.isYakuman) {
                val fu = if (decomp.pattern == HandDecomposer.DecompPattern.CHIITOITSU) 25 else 30
                val score = scoreCalculator.calculate(fu, 0, yaku.yakumanCount)
                Pair(fu, score)
            } else {
                val calculatedFu = fuCalculator.calculate(
                    decomposition = decomp,
                    winTile = context.winTile,
                    isTsumo = context.isTsumo,
                    isClosed = context.isClosed,
                    seatWind = context.seatWind,
                    roundWind = context.roundWind,
                    openMelds = evalHand.melds
                )
                val effectiveHan = yaku.totalHan + extraHan
                val score = scoreCalculator.calculate(calculatedFu, effectiveHan, 0)
                Pair(calculatedFu, score)
            }

            val totalPoints = scoreResult.totalPoints(isDealer = isDealer, isTsumo = context.isTsumo)
            candidates.add(Candidate(decomp, yaku, fu, scoreResult, totalPoints))
        }

        if (candidates.isEmpty()) return null

        // 选择规则：
        // 1. 得点最高优先 (totalPoints)
        // 2. 若得点相同，役满倍数 / 翻数最高优先
        // 3. 若翻数相同，符数最高优先
        return candidates.maxWithOrNull(
            compareBy<Candidate> { it.totalPoints }
                .thenBy { if (it.yakuResult.isYakuman) it.yakuResult.yakumanCount else 0 }
                .thenBy { it.yakuResult.totalHan }
                .thenBy { it.fu }
        )
    }

    /**
     * 结合 Dora 与里 Dora 指示牌进行完整和牌与得点评估
     */
    fun evaluate(
        hand: PlayerHand,
        context: YakuEvaluator.WinContext,
        doraIndicators: List<Tile>,
        uraDoraIndicators: List<Tile> = emptyList(),
        isDealer: Boolean = (context.seatWind == 1)
    ): Candidate? {
        val evalHand = prepareEvaluationHand(hand, context.winTile, context.isTsumo)
        val ura = if (context.isRiichi) uraDoraIndicators else emptyList()
        val doraCount = countDora(evalHand, doraIndicators, ura)
        return evaluate(hand, context, isDealer = isDealer, extraHan = doraCount)
    }

    /**
     * 计算手牌中包含的 Dora 总张数（含指示宝牌与赤宝牌）
     *
     * 遵循日麻宝牌规则：
     * - 委托 [breakdownDora] 实现：返回值 = 指示命中数 + 里宝牌命中数 + 红五张数。
     */
    fun countDora(
        hand: PlayerHand,
        doraIndicators: List<Tile>,
        uraDoraIndicators: List<Tile> = emptyList()
    ): Int {
        val breakdown = breakdownDora(hand, doraIndicators, uraDoraIndicators)
        return breakdown.totalHan
    }

    /**
     * 分解计算手牌中的指示宝牌张数与赤宝牌张数
     *
     * 遵循日麻宝牌与赤宝牌规则：
     * - 表宝牌/里宝牌：分别遍历每个指示牌，累计命中手牌（暗牌及摸牌）与副露中的目标牌张数（同一物理牌可被重复指示）。
     * - 赤宝牌：手牌与副露中的红五每张独立计 1 翻。
     * - 叠加：当红五恰好被指示为宝牌时，指示命中翻数与赤宝牌翻数叠加（单张红五共 2 翻）。
     * - 里宝牌仅在传入时（通常由上层限定立直状态）参与累计。
     */
    fun breakdownDora(
        hand: PlayerHand,
        doraIndicators: List<Tile>,
        uraDoraIndicators: List<Tile> = emptyList()
    ): DoraBreakdown {
        val allTiles = hand.allClosedTiles + hand.melds.flatMap { it.tiles }
        val redTiles = allTiles.filter { it.isRedFive }
        val redCount = redTiles.size

        val indicatorHits = mutableListOf<DoraIndicatorHit>()
        val indicatorTiles = mutableListOf<Tile>()
        for (indicator in doraIndicators) {
            val target = indicator.doraIndicatorTarget()
            val targetTile = Tile.fromTypeIndex(target)
            val matched = allTiles.filter { it.typeIndex == target }
            indicatorHits.add(DoraIndicatorHit(indicator = indicator, target = targetTile, tiles = matched))
            indicatorTiles.addAll(matched)
        }
        val indicatorCount = indicatorTiles.size

        val uraHits = mutableListOf<DoraIndicatorHit>()
        val uraTiles = mutableListOf<Tile>()
        for (indicator in uraDoraIndicators) {
            val target = indicator.doraIndicatorTarget()
            val targetTile = Tile.fromTypeIndex(target)
            val matched = allTiles.filter { it.typeIndex == target }
            uraHits.add(DoraIndicatorHit(indicator = indicator, target = targetTile, tiles = matched))
            uraTiles.addAll(matched)
        }
        val uraCount = uraTiles.size

        return DoraBreakdown(
            indicatorCount = indicatorCount,
            uraCount = uraCount,
            redCount = redCount,
            totalHan = indicatorCount + uraCount + redCount,
            indicatorTiles = indicatorTiles,
            uraTiles = uraTiles,
            redTiles = redTiles,
            indicatorHits = indicatorHits,
            uraHits = uraHits
        )
    }

    /**
     * 结算完整推导链汇总解释
     *
     * @param hand 玩家当前手牌
     * @param context 和牌上下文环境
     * @param doraIndicators 表宝牌指示牌
     * @param uraDoraIndicators 里宝牌指示牌
     * @param isDealer 是否庄家
     * @param honba 场上本场棒数
     * @param riichiSticks 场上供托立直棒数
     * @param loser 放铳玩家座位 (荣和时有效)
     */
    fun explain(
        hand: PlayerHand,
        context: YakuEvaluator.WinContext,
        doraIndicators: List<Tile>,
        uraDoraIndicators: List<Tile> = emptyList(),
        isDealer: Boolean = (context.seatWind == 1),
        honba: Int = 0,
        riichiSticks: Int = 0,
        loser: Int? = null
    ): ScoreBreakdown? {
        val evalHand = prepareEvaluationHand(hand, context.winTile, context.isTsumo)
        val ura = if (context.isRiichi) uraDoraIndicators else emptyList()
        val doraBreakdown = breakdownDora(evalHand, doraIndicators, ura)
        val candidate = evaluate(hand, context, doraIndicators, uraDoraIndicators, isDealer) ?: return null

        val fuBreakdown = fuCalculator.calculateBreakdown(
            decomposition = candidate.decomposition,
            winTile = context.winTile,
            isTsumo = context.isTsumo,
            isClosed = context.isClosed,
            seatWind = context.seatWind,
            roundWind = context.roundWind,
            openMelds = evalHand.melds
        )

        val yakuEntries = ScoreTextFormatter.yakuEntries(candidate.yakuResult.yakuList)
        val yakuLines = yakuEntries.map { Pair(it.displayName, it.han) }
        val paymentLines = ScoreTextFormatter.paymentLines(
            score = candidate.scoreResult,
            isDealer = isDealer,
            isTsumo = context.isTsumo,
            loser = loser
        )
        val honbaAddition = 300 * honba
        val riichiAddition = 1000 * riichiSticks
        val totalPoints = candidate.totalPoints + honbaAddition + riichiAddition

        return ScoreBreakdown(
            yakuLines = yakuLines,
            yakumanCount = if (candidate.yakuResult.isYakuman) candidate.yakuResult.yakumanCount else 0,
            dora = doraBreakdown,
            fu = fuBreakdown,
            score = candidate.scoreResult,
            paymentLines = paymentLines,
            honbaAddition = honbaAddition,
            riichiAddition = riichiAddition,
            totalPoints = totalPoints,
            uraEligible = context.isRiichi,
            isDealer = isDealer,
            isTsumo = context.isTsumo,
            yakuEntries = yakuEntries
        )
    }

    /**
     * 判定手牌是否具有至少一种有役且可和的拆解解释
     */
    fun canWinWithYaku(
        hand: PlayerHand,
        context: YakuEvaluator.WinContext,
        isDealer: Boolean = (context.seatWind == 1)
    ): Boolean = evaluate(hand, context, isDealer) != null

    /**
     * 辅助方法：确保手牌包含和了牌，用于拆解计算
     */
    private fun prepareEvaluationHand(hand: PlayerHand, winTile: Tile, isTsumo: Boolean): PlayerHand {
        // 若手牌闭手牌数模 3 为 1（例如 13 张、10 张、7 张、4 张），说明和了牌尚未加入闭手
        return if (hand.allClosedTiles.size % 3 == 1) {
            val copy = PlayerHand()
            copy.closedTiles.addAll(hand.closedTiles)
            copy.drawnTile = winTile
            copy.melds.addAll(hand.melds)
            copy.riichiDeclared = hand.riichiDeclared
            copy.riichiTurn = hand.riichiTurn
            copy.ippatsu = hand.ippatsu
            copy.discards.addAll(hand.discards)
            copy
        } else {
            hand
        }
    }

    companion object {
        val INSTANCE = WinningScoreEvaluator()
    }
}
