package com.majiang.match

import com.majiang.economy.CheatEconomyConfig
import com.majiang.stacking.ShuffleConfig
import java.util.Collections

/**
 * 比赛整体规则配置 (V3)
 *
 * 冻结契约规格：
 * - playerCount: Int = 4
 * - handsPerWind: Int = 4
 * - winds: List<Int> = listOf(1, 2)
 * - startingScores: List<Int> = List(4) { 25000 }
 * - allowExtension: Boolean = true
 * - allowAgariYame: Boolean = true
 * - bankruptcyEnabled: Boolean = true
 * - bankruptcyThreshold: Int = 0
 * - bankruptcyInclusive: Boolean = false
 * - targetScore: Int = 30000
 * - tieBreakSeats: List<Int> = listOf(0, 1, 2, 3)
 *
 * @property playerCount 玩家人数（当前仅实现四人规则：4）
 * @property handsPerWind 每场风局数 (1..4，默认 4 局：如東一~東四)
 * @property winds 比赛场风序列 (从東風 1 开始连续，如 [1]=东风战, [1, 2]=半庄战)
 * @property startingScores 起始点数列表 (默认四家各 25000 点)
 * @property allowExtension 是否允许延长战 (半庄南四/东风东四未达返点时延长，最多再一场)
 * @property allowAgariYame 是否允许尾庄和了/听牌终了 (达到 targetScore 且独占第一)
 * @property bankruptcyEnabled 是否启用击飞/破产判定
 * @property bankruptcyThreshold 击飞判定阈值（默认 0 点）
 * @property bankruptcyInclusive 击飞是否包含等于阈值 (false 为 < threshold 击飞，true 为 <= threshold 击飞)
 * @property targetScore 返点基准分（默认 30000 点）
 * @property tieBreakSeats 同分排名时的优先级座次顺序（默认起家优先 [0, 1, 2, 3]）
 * @property cheatEconomy 独立出千经济系统配置
 * @property shuffleConfig 洗牌千术准备阶段配置
 * @property tileMarkConfig 落汗千术配置
 */
data class MatchConfig @JvmOverloads constructor(
    val playerCount: Int = 4,
    val handsPerWind: Int = 4,
    val winds: List<Int> = listOf(1, 2),
    val startingScores: List<Int> = List(4) { 25000 },
    val allowExtension: Boolean = true,
    val allowAgariYame: Boolean = true,
    val bankruptcyEnabled: Boolean = true,
    val bankruptcyThreshold: Int = 0,
    val bankruptcyInclusive: Boolean = false,
    val targetScore: Int = 30000,
    val tieBreakSeats: List<Int> = listOf(0, 1, 2, 3),
    val cheatEconomy: CheatEconomyConfig = CheatEconomyConfig(),
    val shuffleConfig: ShuffleConfig = ShuffleConfig(),
    val tileMarkConfig: com.majiang.cheat.mark.TileMarkConfig = com.majiang.cheat.mark.TileMarkConfig()
) {
    /**
     * 校验当前配置是否被引擎支持
     *
     * 拒绝未实现三人规则、非法赛程、非法分数与重复座次。
     * @throws IllegalArgumentException 发现不合法或未支持的配置时抛出
     */
    fun validateSupported(): MatchConfig {
        require(playerCount == 4) {
            "Unsupported player count: $playerCount. Three-player rules are not supported yet, only 4 players are supported."
        }
        require(handsPerWind in 1..4) {
            "Illegal handsPerWind: $handsPerWind. Must be between 1 and 4."
        }
        require(winds.isNotEmpty()) { "winds must not be empty" }
        require(winds == listOf(1) || winds == listOf(1, 2)) {
            "winds must be contiguous starting from East (1..2), supported: [1] or [1, 2], got: $winds"
        }
        require(startingScores.size == playerCount) {
            "startingScores size (${startingScores.size}) must match playerCount ($playerCount)"
        }
        require(startingScores.all { it >= 0 }) {
            "startingScores must be non-negative, got: $startingScores"
        }
        require(tieBreakSeats.size == playerCount) {
            "tieBreakSeats size (${tieBreakSeats.size}) must match playerCount ($playerCount)"
        }
        require(tieBreakSeats.toSet().size == playerCount) {
            "tieBreakSeats must not contain duplicates: $tieBreakSeats"
        }
        require(tieBreakSeats.all { it in 0 until playerCount }) {
            "tieBreakSeats must be within 0 until $playerCount, got: $tieBreakSeats"
        }
        require(targetScore >= 0) {
            "targetScore must be non-negative, got: $targetScore"
        }
        if (shuffleConfig.enabled) {
            require(shuffleConfig.selectionMillis > 0L) {
                "shuffleConfig.selectionMillis must be positive, got ${shuffleConfig.selectionMillis}"
            }
            require(shuffleConfig.plannedDraws in 0..18) {
                "shuffleConfig.plannedDraws must be in 0..18, got ${shuffleConfig.plannedDraws}"
            }
            require(shuffleConfig.strength in 0.0..1.0 && !shuffleConfig.strength.isNaN()) {
                "shuffleConfig.strength must be in 0.0..1.0, got ${shuffleConfig.strength}"
            }
            require(shuffleConfig.interference >= 0.0 && !shuffleConfig.interference.isNaN() && !shuffleConfig.interference.isInfinite()) {
                "shuffleConfig.interference must be non-negative and finite, got ${shuffleConfig.interference}"
            }
        }
        if (tileMarkConfig.enabled) {
            require(cheatEconomy.enabled) {
                "tileMarkConfig requires cheatEconomy to be enabled"
            }
            require(cheatEconomy.costs["TILE_MARK"] == 10) {
                "tileMarkConfig requires cheatEconomy.costs['TILE_MARK'] == 10, got: ${cheatEconomy.costs["TILE_MARK"]}"
            }
        }
        return this
    }

    /**
     * 桥接至现有 MatchRuleConfig 进度算法配置
     */
    fun toRuleConfig(): MatchRuleConfig = MatchRuleConfig(
        length = if (winds.size == 1 && winds[0] == 1) MatchLength.TONPUUSEN else MatchLength.HANCHAN,
        allowBankruptcy = bankruptcyEnabled,
        targetScore = targetScore,
        allowWestExtension = allowExtension,
        awardRemainingRiichiSticksToFirst = true,
        allowAgariYame = allowAgariYame,
        handsPerWind = handsPerWind,
        winds = winds,
        bankruptcyThreshold = bankruptcyThreshold,
        bankruptcyInclusive = bankruptcyInclusive,
        tieBreakSeats = tieBreakSeats
    )
}
