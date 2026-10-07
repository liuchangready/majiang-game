package com.majiang.stacking

/**
 * 洗牌千术全局与单局配置
 *
 * @property enabled 是否启用洗牌阶段千术（默认 false 保持兼容与原规则测试直接发牌）
 * @property selectionMillis 准备阶段选择时长（毫秒，默认 10000ms = 10s）
 * @property plannedDraws 瞒天过海影响的后续预计摸牌轮数（默认 8 轮）
 * @property strength 基础做牌强度 [0.0, 1.0]（默认 0.75）
 * @property interference 冲突干扰衰减系数 lambda >= 0.0（默认 0.65）
 * @property aiMaySpend AI 玩家是否允许消耗点数出千（默认 false）
 */
data class ShuffleConfig(
    val enabled: Boolean = false,
    val selectionMillis: Long = 10000L,
    val plannedDraws: Int = 8,
    val strength: Double = 0.75,
    val interference: Double = 0.65,
    val aiMaySpend: Boolean = false
) {
    init {
        require(selectionMillis > 0L) { "selectionMillis must be positive, got $selectionMillis" }
        require(plannedDraws in 0..18) { "plannedDraws must be in 0..18, got $plannedDraws" }
        require(!strength.isNaN() && strength in 0.0..1.0) { "strength must be in 0.0..1.0, got $strength" }
        require(!interference.isNaN() && !interference.isInfinite() && interference >= 0.0) {
            "interference must be finite and >= 0.0, got $interference"
        }
    }

    fun deepCopy(): ShuffleConfig = copy()
}
