package com.majiang.match

/**
 * 对局模式长度
 *
 * @property windLimit 正常对局最大场风 (1=东风战仅东风, 2=半庄战东+南风)
 */
enum class MatchLength(val windLimit: Int) {
    TONPUUSEN(1), // 东风战 (東一~東四)
    HANCHAN(2)    // 半庄战 (東一~東四, 南一~南四)
}

/**
 * 对局规则配置
 *
 * @property length 对局长度（默认半庄）
 * @property allowBankruptcy 是否允许击飞/破产结束（任何玩家点数 < 0 立即终局）
 * @property targetScore 返点基准分（默认 30000 点）
 * @property allowWestExtension 是否允许延长战（半庄南四无人达到 targetScore 时西入）
 * @property awardRemainingRiichiSticksToFirst 终局时场上剩余供托棒是否全额归属第一名
 * @property allowAgariYame 正常终局尾庄达到 targetScore 且独占第一时是否允许选择终了
 */
data class MatchRuleConfig(
    val length: MatchLength = MatchLength.HANCHAN,
    val allowBankruptcy: Boolean = true,
    val targetScore: Int = 30000,
    val allowWestExtension: Boolean = true,
    val awardRemainingRiichiSticksToFirst: Boolean = true,
    val allowAgariYame: Boolean = true
)

/**
 * 单局结束结果描述
 */
sealed interface RoundOutcome {
    /** 庄家和牌 */
    data class DealerWin(val winner: Int) : RoundOutcome

    /** 闲家和牌 */
    data class NonDealerWin(val winner: Int) : RoundOutcome

    /** 荣和 */
    data class Ron(val winner: Int, val loser: Int) : RoundOutcome

    /** 自摸 */
    data class Tsumo(val winner: Int) : RoundOutcome

    /** 荒牌流局（听牌座位集合） */
    data class ExhaustiveDraw(val tenpaiSeats: Set<Int>) : RoundOutcome

    /** 途中流局（四风连打、四杠散了、四家立直、九种九牌等） */
    data class AbortiveDraw(val reason: String = "") : RoundOutcome
}

/**
 * 终局原因
 */
enum class MatchFinishReason {
    /** 飞人/破产（点数 < 0） */
    BANKRUPTCY,

    /** 正常终了（东风战东四 / 半庄战南四打完且达到返点） */
    NORMAL_COMPLETION,

    /** 西入延长战终了（有人达到返点或西四打完） */
    WEST_COMPLETION,

    /** 尾局庄家选择和了/听牌终了 (Agari-Yame) */
    AGARI_YAME
}

/**
 * 最终排名条目
 *
 * @property rank 名次 (1~4)
 * @property seat 座位号 (0~3)
 * @property score 最终持有点数（已包含终局供托结算）
 */
data class RankingEntry(
    val rank: Int,
    val seat: Int,
    val score: Int
)

/**
 * 场次推进结果
 */
sealed interface ProgressionResult {
    /**
     * 进入下一局
     *
     * @property roundWind 场风: 1=東, 2=南, 3=西
     * @property roundNumber 局号: 0=一局, 1=二局, 2=三局, 3=四局
     * @property dealer 庄家座位 (0~3)
     * @property honba 本场数
     * @property canDealerDeclareAgariYame 庄家是否满足并可选择终了条件
     */
    data class NextRound(
        val roundWind: Int,
        val roundNumber: Int,
        val dealer: Int,
        val honba: Int,
        val canDealerDeclareAgariYame: Boolean = false
    ) : ProgressionResult

    /**
     * 整场比赛结束
     *
     * @property finalScores 最终各座位分数（已计入终局供托）
     * @property rankings 排名列表（按得点从高到低，同分按起家逆时针座位顺序）
     * @property reason 终局原因
     * @property remainingRiichiSticksTransferredTo 终局供托转移到的座席编号（若有）
     */
    data class MatchFinished(
        val finalScores: List<Int>,
        val rankings: List<RankingEntry>,
        val reason: MatchFinishReason,
        val remainingRiichiSticksTransferredTo: Int? = null
    ) : ProgressionResult
}

/**
 * 推进输入参数包装
 */
data class ProgressionInput(
    val currentDealer: Int,
    val currentRoundWind: Int,
    val currentRoundNumber: Int,
    val currentHonba: Int,
    val scores: List<Int>,
    val outcome: RoundOutcome,
    val remainingRiichiSticks: Int = 0,
    val dealerDeclaredAgariYame: Boolean = false
)

/**
 * 纯场次推进服务
 *
 * 遵循 DESIGN.md 5.2 规范与日麻标准赛制推进：
 * - roundNumber 严格为 0..3，roundWind 为 1..3。
 * - DealerWin: 连庄、本场 + 1。
 * - NonDealerWin: 换庄、推进局数、本场归零。
 * - ExhaustiveDraw: 庄家听牌则连庄、本场 + 1；庄家未听则换庄、推进局数、本场 + 1。
 * - AbortiveDraw: 连庄、本场 + 1。
 * - 破产检查 (Bankruptcy): 分数 < 0 立即终局。
 * - 正常终局与延长 (West extension): 南四正常换庄时有人 >= 30000 终局，否则西入；西入有人 >= 30000 或西四换庄终局。
 * - 庄家终了 (Agari-Yame): 终局庄家达到 30000 且独占第一时可选择终了，同分不默认终了。
 * - 终局剩余供托归属最终第一名，并保证总资金守恒。
 */
class MatchProgression(
    val config: MatchRuleConfig = MatchRuleConfig()
) {

    /**
     * 计算单局结算后的场次推进
     */
    fun nextRound(input: ProgressionInput): ProgressionResult = nextRound(
        currentDealer = input.currentDealer,
        currentRoundWind = input.currentRoundWind,
        currentRoundNumber = input.currentRoundNumber,
        currentHonba = input.currentHonba,
        currentScores = input.scores,
        outcome = input.outcome,
        remainingRiichiSticks = input.remainingRiichiSticks,
        dealerDeclaredAgariYame = input.dealerDeclaredAgariYame
    )

    /**
     * 计算单局结算后的场次推进（平铺参数）
     */
    fun nextRound(
        currentDealer: Int,
        currentRoundWind: Int,
        currentRoundNumber: Int,
        currentHonba: Int,
        currentScores: List<Int>,
        outcome: RoundOutcome,
        remainingRiichiSticks: Int = 0,
        dealerDeclaredAgariYame: Boolean = false
    ): ProgressionResult {
        validateInputs(currentDealer, currentRoundWind, currentRoundNumber, currentHonba, currentScores, remainingRiichiSticks)

        // 1. 击飞 / 破产判定
        if (config.allowBankruptcy && currentScores.any { it < 0 }) {
            return awardRiichiSticksAndFinish(currentScores, remainingRiichiSticks, MatchFinishReason.BANKRUPTCY)
        }

        // 2. 解析胜负与连庄判定
        val isDealerContinuance = isDealerContinuing(currentDealer, outcome)
        val isExhaustiveDraw = outcome is RoundOutcome.ExhaustiveDraw
        val isNonDealerWin = isNonDealerWinning(currentDealer, outcome)

        // 3. 判定当前是否已处于常规最终局（半庄南四 / 东风东四）
        val isNormalFinalRound = (currentRoundWind == config.length.windLimit && currentRoundNumber == 3)
        // 是否处于西入延长战中
        val isWestExtension = (currentRoundWind > config.length.windLimit)

        // 4. 延长战 (西入) 中每局结算后的判定：有人达到返点即终了
        if (isWestExtension) {
            if (currentScores.any { it >= config.targetScore }) {
                return awardRiichiSticksAndFinish(currentScores, remainingRiichiSticks, MatchFinishReason.WEST_COMPLETION)
            }
        }

        // 5. 尾局庄家连庄与选择终了 (Agari-Yame) 判定
        if (isNormalFinalRound && isDealerContinuance) {
            val canAgariYame = checkAgariYameEligible(currentDealer, currentScores)
            if (canAgariYame && dealerDeclaredAgariYame) {
                return awardRiichiSticksAndFinish(currentScores, remainingRiichiSticks, MatchFinishReason.AGARI_YAME)
            }
            // 庄家连庄继续下一局
            return ProgressionResult.NextRound(
                roundWind = currentRoundWind,
                roundNumber = currentRoundNumber,
                dealer = currentDealer,
                honba = currentHonba + 1,
                canDealerDeclareAgariYame = canAgariYame
            )
        }

        // 6. 尾局换庄判定（半庄南四换庄 或 东风东四换庄）
        if (isNormalFinalRound && !isDealerContinuance) {
            val someoneReachedTarget = currentScores.any { it >= config.targetScore }
            if (someoneReachedTarget || !config.allowWestExtension) {
                return awardRiichiSticksAndFinish(currentScores, remainingRiichiSticks, MatchFinishReason.NORMAL_COMPLETION)
            } else {
                // 西入：开启西风场延长战
                val nextDealer = (currentDealer + 1) % 4
                val nextHonba = if (isExhaustiveDraw) currentHonba + 1 else 0
                return ProgressionResult.NextRound(
                    roundWind = currentRoundWind + 1, // 西风场 (3)
                    roundNumber = 0,                  // 西一局
                    dealer = nextDealer,
                    honba = nextHonba,
                    canDealerDeclareAgariYame = false
                )
            }
        }

        // 7. 西入延长战中的轮转推进
        if (isWestExtension) {
            if (isDealerContinuance) {
                return ProgressionResult.NextRound(
                    roundWind = currentRoundWind,
                    roundNumber = currentRoundNumber,
                    dealer = currentDealer,
                    honba = currentHonba + 1,
                    canDealerDeclareAgariYame = false
                )
            } else {
                // 换庄
                if (currentRoundNumber < 3) {
                    val nextDealer = (currentDealer + 1) % 4
                    val nextHonba = if (isExhaustiveDraw) currentHonba + 1 else 0
                    return ProgressionResult.NextRound(
                        roundWind = currentRoundWind,
                        roundNumber = currentRoundNumber + 1,
                        dealer = nextDealer,
                        honba = nextHonba,
                        canDealerDeclareAgariYame = false
                    )
                } else {
                    // 西四换庄：最迟西四结束
                    return awardRiichiSticksAndFinish(currentScores, remainingRiichiSticks, MatchFinishReason.WEST_COMPLETION)
                }
            }
        }

        // 8. 普通局（非终局、非西入）推进
        if (isDealerContinuance) {
            // 连庄：庄家不变，局号不变，本场 + 1
            return ProgressionResult.NextRound(
                roundWind = currentRoundWind,
                roundNumber = currentRoundNumber,
                dealer = currentDealer,
                honba = currentHonba + 1,
                canDealerDeclareAgariYame = false
            )
        } else {
            // 换庄：庄家顺延，局号 + 1
            val nextDealer = (currentDealer + 1) % 4
            val nextHonba = if (isExhaustiveDraw) currentHonba + 1 else 0
            val advancedRoundNumber = currentRoundNumber + 1

            val (nextRoundWind, nextRoundNumber) = if (advancedRoundNumber == 4) {
                Pair(currentRoundWind + 1, 0)
            } else {
                Pair(currentRoundWind, advancedRoundNumber)
            }

            return ProgressionResult.NextRound(
                roundWind = nextRoundWind,
                roundNumber = nextRoundNumber,
                dealer = nextDealer,
                honba = nextHonba,
                canDealerDeclareAgariYame = false
            )
        }
    }

    /**
     * 判断庄家是否连庄
     */
    fun isDealerContinuing(dealer: Int, outcome: RoundOutcome): Boolean = when (outcome) {
        is RoundOutcome.DealerWin -> true
        is RoundOutcome.NonDealerWin -> false
        is RoundOutcome.Ron -> outcome.winner == dealer
        is RoundOutcome.Tsumo -> outcome.winner == dealer
        is RoundOutcome.ExhaustiveDraw -> dealer in outcome.tenpaiSeats
        is RoundOutcome.AbortiveDraw -> true
    }

    /**
     * 判断是否为闲家和牌
     */
    fun isNonDealerWinning(dealer: Int, outcome: RoundOutcome): Boolean = when (outcome) {
        is RoundOutcome.DealerWin -> false
        is RoundOutcome.NonDealerWin -> true
        is RoundOutcome.Ron -> outcome.winner != dealer
        is RoundOutcome.Tsumo -> outcome.winner != dealer
        else -> false
    }

    /**
     * 判定庄家是否满足选择终了 (Agari-Yame) 条件：
     * 1. 允许 Agari-Yame
     * 2. 点数 >= targetScore (30000)
     * 3. 严格独占第一名（同分不满足）
     */
    fun checkAgariYameEligible(dealer: Int, scores: List<Int>): Boolean {
        if (!config.allowAgariYame) return false
        val dealerScore = scores[dealer]
        if (dealerScore < config.targetScore) return false

        // 检查是否独占最高
        for (i in 0 until 4) {
            if (i != dealer && scores[i] >= dealerScore) {
                return false // 并列或落后，不触发
            }
        }
        return true
    }

    /**
     * 计算并列排序
     * 规则：按分数从高到低排序；同分按初始座位 0 > 1 > 2 > 3 排序（起家优先）
     */
    fun computeRankings(scores: List<Int>): List<RankingEntry> {
        val sortedSeats = (0 until 4).sortedWith(
            compareByDescending<Int> { scores[it] }.thenBy { it }
        )
        return sortedSeats.mapIndexed { index, seat ->
            RankingEntry(
                rank = index + 1,
                seat = seat,
                score = scores[seat]
            )
        }
    }

    /**
     * 结算终局供托并构造 MatchFinished
     */
    fun awardRiichiSticksAndFinish(
        scores: List<Int>,
        remainingRiichiSticks: Int,
        reason: MatchFinishReason
    ): ProgressionResult.MatchFinished {
        val finalScores = scores.toMutableList()
        var awardedSeat: Int? = null

        val preRankings = computeRankings(scores)
        val firstSeat = preRankings.first().seat

        if (config.awardRemainingRiichiSticksToFirst && remainingRiichiSticks > 0) {
            finalScores[firstSeat] += remainingRiichiSticks * 1000
            awardedSeat = firstSeat
        }

        val finalRankings = computeRankings(finalScores)
        return ProgressionResult.MatchFinished(
            finalScores = finalScores.toList(),
            rankings = finalRankings,
            reason = reason,
            remainingRiichiSticksTransferredTo = awardedSeat
        )
    }

    private fun validateInputs(
        dealer: Int,
        roundWind: Int,
        roundNumber: Int,
        honba: Int,
        scores: List<Int>,
        remainingRiichiSticks: Int
    ) {
        require(dealer in 0..3) { "Dealer index must be 0..3, got $dealer" }
        require(roundWind in 1..3) { "Round wind must be 1..3, got $roundWind" }
        require(roundNumber in 0..3) { "Round number must be 0..3, got $roundNumber" }
        require(honba >= 0) { "Honba must be non-negative, got $honba" }
        require(scores.size == 4) { "Scores must contain exactly 4 entries, got ${scores.size}" }
        require(remainingRiichiSticks >= 0) { "Remaining riichi sticks must be non-negative, got $remainingRiichiSticks" }
    }
}
