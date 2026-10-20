package com.majiang.stacking

/**
 * 玩家私有的洗牌准备阶段快照视图
 *
 * @property roundId 局标识
 * @property status 阶段状态 (SELECTING / GENERATING)
 * @property opensAtMillis 准备窗口开始时间戳
 * @property deadlineMillis 准备窗口截止时间戳
 * @property serverNowMillis 权威服务端当前时间戳
 * @property confirmed 是否已主动确认锁定方案
 * @property selection 本人当前方案（未选择时为空）
 * @property reservedPoints 为本方案预留的点数
 * @property availablePoints 本人当前可用出千点数
 * @property seeThroughEnabled 当前房间是否允许在准备阶段选择看破
 * @property seeThroughCost 看破固定费用（始终为 10，供 UI 展示）
 */
data class ShufflePreparationView(
    val roundId: String,
    val status: ShufflePreparationStatus,
    val opensAtMillis: Long,
    val deadlineMillis: Long,
    val serverNowMillis: Long,
    val confirmed: Boolean,
    val selection: ShuffleSelection?,
    val reservedPoints: Int,
    val availablePoints: Int,
    val seeThroughEnabled: Boolean = false,
    val seeThroughCost: Int = ShuffleSelection.SEE_THROUGH_COST
) {
    init {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(opensAtMillis >= 0L) { "opensAtMillis must be non-negative, got $opensAtMillis" }
        require(deadlineMillis >= opensAtMillis) {
            "deadlineMillis ($deadlineMillis) must be >= opensAtMillis ($opensAtMillis)"
        }
        require(reservedPoints >= 0) { "reservedPoints must be non-negative, got $reservedPoints" }
        require(availablePoints >= 0) { "availablePoints must be non-negative, got $availablePoints" }
        require(seeThroughCost == ShuffleSelection.SEE_THROUGH_COST) {
            "seeThroughCost must be ${ShuffleSelection.SEE_THROUGH_COST}, got $seeThroughCost"
        }
    }

    fun deepCopy(): ShufflePreparationView = copy(selection = selection?.deepCopy())
}
