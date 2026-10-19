package com.majiang.settlement

import com.majiang.rules.ScoreCalculator
import com.majiang.rules.ScoreTextFormatter

/**
 * 结算输入参数
 *
 * @property scores 四位玩家当前的持有分数
 * @property dealer 当前庄家座位索引 (0~3)
 * @property honba 当前本场棒数
 * @property riichiSticks 场上立直棒（供托）数（每根 1000 点）
 */
data class SettlementInput(
    val scores: List<Int>,
    val dealer: Int,
    val honba: Int,
    val riichiSticks: Int
)

/**
 * 结算结果
 *
 * @property deltas 各玩家的分数收支变化量 (newScores - scores)
 * @property newScores 结算后各玩家的新分数
 * @property remainingRiichiSticks 结算后场上剩余的立直棒数
 */
data class SettlementResult(
    val deltas: List<Int>,
    val newScores: List<Int>,
    val remainingRiichiSticks: Int
)

/**
 * 纯结算领域服务
 *
 * 遵循日麻权威计分与供托物理守恒规则：
 * - 荣和：放铳者支付基础得点 + 300 × 本场；和牌者收取放铳者支付及全部供托 (riichiSticks)。
 * - 自摸：每位付款者在基础付款上追加 100 × 本场；和牌者收取全部付款及全部供托。
 * - 和牌后供托清零；流局保留供托。
 * - 荒牌流局：1/2/3 人听牌时共支付 3000 不听罚符；0/4 人听牌无收支。
 * - 途中流局：不支付罚符，保留供托。
 * - 物理守恒断言：sum(newScores) + remainingRiichiSticks * 1000 == sum(scores) + riichiSticks * 1000
 */
class SettlementService {

    /**
     * 荣和结算
     *
     * @param input 结算前场上数据
     * @param winner 和牌玩家座位 (0~3)
     * @param loser 放铳玩家座位 (0~3)
     * @param score 计算出的翻符得点结果
     */
    fun ron(
        input: SettlementInput,
        winner: Int,
        loser: Int,
        score: ScoreCalculator.ScoreResult
    ): SettlementResult {
        validateInput(input)
        require(winner in 0..3) { "Winner seat out of range: $winner" }
        require(loser in 0..3) { "Loser seat out of range: $loser" }
        require(winner != loser) { "Winner and loser cannot be the same player in ron: winner=$winner, loser=$loser" }

        val isDealer = (winner == input.dealer)
        val basePayment = if (isDealer) score.dealerRonPayment() else score.nonDealerRonPayment()
        val honbaPayment = 300 * input.honba
        val totalLoserPay = basePayment + honbaPayment
        val riichiPoints = input.riichiSticks * 1000

        val deltas = MutableList(4) { 0 }
        deltas[loser] = -totalLoserPay
        deltas[winner] = totalLoserPay + riichiPoints

        val remainingRiichiSticks = 0
        val newScores = List(4) { input.scores[it] + deltas[it] }

        assertConservation(input, deltas, newScores, remainingRiichiSticks)
        return SettlementResult(deltas, newScores, remainingRiichiSticks)
    }

    /**
     * 自摸结算
     *
     * @param input 结算前场上数据
     * @param winner 自摸和牌玩家座位 (0~3)
     * @param score 计算出的翻符得点结果
     */
    fun tsumo(
        input: SettlementInput,
        winner: Int,
        score: ScoreCalculator.ScoreResult
    ): SettlementResult {
        validateInput(input)
        require(winner in 0..3) { "Winner seat out of range: $winner" }

        val isDealer = (winner == input.dealer)
        val honbaPerPayer = 100 * input.honba
        val riichiPoints = input.riichiSticks * 1000
        val deltas = MutableList(4) { 0 }

        if (isDealer) {
            val basePayment = score.dealerTsumoPayment()
            val payEach = basePayment + honbaPerPayer
            var totalCollected = 0
            for (i in 0 until 4) {
                if (i != winner) {
                    deltas[i] = -payEach
                    totalCollected += payEach
                }
            }
            deltas[winner] = totalCollected + riichiPoints
        } else {
            val (dealerBase, otherBase) = score.nonDealerTsumoPayment()
            val dealerPay = dealerBase + honbaPerPayer
            val otherPay = otherBase + honbaPerPayer
            var totalCollected = 0
            for (i in 0 until 4) {
                if (i != winner) {
                    val pay = if (i == input.dealer) dealerPay else otherPay
                    deltas[i] = -pay
                    totalCollected += pay
                }
            }
            deltas[winner] = totalCollected + riichiPoints
        }

        val remainingRiichiSticks = 0
        val newScores = List(4) { input.scores[it] + deltas[it] }

        assertConservation(input, deltas, newScores, remainingRiichiSticks)
        return SettlementResult(deltas, newScores, remainingRiichiSticks)
    }

    /**
     * 荒牌流局结算（不听罚符）
     *
     * @param input 结算前场上数据
     * @param tenpai 听牌玩家的座位集合 (0~3)
     */
    fun exhaustiveDraw(
        input: SettlementInput,
        tenpai: Set<Int>
    ): SettlementResult {
        validateInput(input)
        val tenpaiSeats = tenpai.filter { it in 0..3 }.toSet()
        val tenpaiCount = tenpaiSeats.size
        val deltas = MutableList(4) { 0 }

        when (tenpaiCount) {
            1 -> {
                val receive = 3000
                val pay = 1000
                for (i in 0 until 4) {
                    deltas[i] = if (i in tenpaiSeats) receive else -pay
                }
            }
            2 -> {
                val receive = 1500
                val pay = 1500
                for (i in 0 until 4) {
                    deltas[i] = if (i in tenpaiSeats) receive else -pay
                }
            }
            3 -> {
                val receive = 1000
                val pay = 3000
                for (i in 0 until 4) {
                    deltas[i] = if (i in tenpaiSeats) receive else -pay
                }
            }
            else -> {
                // 0 人或 4 人听牌：无收支
            }
        }

        val remainingRiichiSticks = input.riichiSticks
        val newScores = List(4) { input.scores[it] + deltas[it] }

        assertConservation(input, deltas, newScores, remainingRiichiSticks)
        return SettlementResult(deltas, newScores, remainingRiichiSticks)
    }

    /**
     * 途中流局结算（无罚符收支，供托保留）
     *
     * @param input 结算前场上数据
     */
    fun abortiveDraw(
        input: SettlementInput
    ): SettlementResult {
        validateInput(input)
        val deltas = List(4) { 0 }
        val remainingRiichiSticks = input.riichiSticks
        val newScores = input.scores.toList()

        assertConservation(input, deltas, newScores, remainingRiichiSticks)
        return SettlementResult(deltas, newScores, remainingRiichiSticks)
    }

    /**
     * 格式化支付行说明文本（兼容入口）
     *
     * 文本构造已统一收敛到 [ScoreTextFormatter.paymentLines]，本方法保持原有签名与逐字输出，
     * 仅做委托，避免出现第二套文案实现。
     *
     * 与 ScoreResult 各支付函数 dealerTsumoPayment / nonDealerTsumoPayment / dealerRonPayment / nonDealerRonPayment 完全一致
     */
    fun formatPaymentLines(
        score: ScoreCalculator.ScoreResult,
        isDealer: Boolean,
        isTsumo: Boolean,
        loser: Int? = null
    ): List<String> = ScoreTextFormatter.paymentLines(
        score = score,
        isDealer = isDealer,
        isTsumo = isTsumo,
        loser = loser
    )

    private fun validateInput(input: SettlementInput) {
        require(input.scores.size == 4) { "Scores must contain exactly 4 entries, got ${input.scores.size}" }
        require(input.dealer in 0..3) { "Dealer index must be 0..3, got ${input.dealer}" }
        require(input.honba >= 0) { "Honba must be non-negative, got ${input.honba}" }
        require(input.riichiSticks >= 0) { "Riichi sticks must be non-negative, got ${input.riichiSticks}" }
    }

    private fun assertConservation(
        input: SettlementInput,
        deltas: List<Int>,
        newScores: List<Int>,
        remainingRiichiSticks: Int
    ) {
        val totalOld = input.scores.sum() + input.riichiSticks * 1000
        val totalNew = newScores.sum() + remainingRiichiSticks * 1000
        check(totalOld == totalNew) {
            "Points conservation violated: oldTotal=$totalOld (scores=${input.scores.sum()} + sticks=${input.riichiSticks * 1000}), " +
                    "newTotal=$totalNew (newScores=${newScores.sum()} + sticks=${remainingRiichiSticks * 1000})"
        }
        for (i in 0 until 4) {
            check(deltas[i] == newScores[i] - input.scores[i]) {
                "Delta mismatch at seat $i: delta=${deltas[i]} != newScore(${newScores[i]}) - oldScore(${input.scores[i]})"
            }
        }
    }

    companion object {
        val INSTANCE = SettlementService()
    }
}
