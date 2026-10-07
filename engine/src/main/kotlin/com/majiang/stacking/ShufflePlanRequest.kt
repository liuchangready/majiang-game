package com.majiang.stacking

/**
 * 提交洗牌方案请求
 *
 * @property requestId 请求唯一幂等标识
 * @property roundId 局标识
 * @property controlEpoch 当前客户端控制代次
 * @property selection 玩家选择的千术方案
 */
data class ShufflePlanRequest(
    val requestId: String,
    val roundId: String,
    val controlEpoch: Long,
    val selection: ShuffleSelection
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative, got $controlEpoch" }
    }

    fun deepCopy(): ShufflePlanRequest = ShufflePlanRequest(
        requestId = requestId,
        roundId = roundId,
        controlEpoch = controlEpoch,
        selection = selection.deepCopy()
    )
}
