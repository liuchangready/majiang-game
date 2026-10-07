package com.majiang.stacking

/**
 * 绑定座位的洗牌千术方案
 *
 * @property seat 座位索引 0..3
 * @property selection 玩家选择的千术方案
 */
data class SeatShufflePlan(
    val seat: Int,
    val selection: ShuffleSelection
) {
    init {
        require(seat in 0..3) { "seat must be 0..3, got $seat" }
    }

    fun deepCopy(): SeatShufflePlan = SeatShufflePlan(seat, selection.deepCopy())
}
