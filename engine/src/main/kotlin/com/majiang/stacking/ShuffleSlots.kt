package com.majiang.stacking

import java.util.Collections

/**
 * 洗牌物理槽位划分计算器
 *
 * 物理守恒 136 张牌山区间分布：
 * - 0..52: 起手发牌区（共 53 张）
 *   4x3 发牌轮次（每人4张*3轮） + 单张轮次（每人1张） + 庄家第14张 Extra
 * - 53..121: 正常顺位后续摸牌区（共 69 张）
 * - 122..135: 王牌区（Dead Wall，14 张），绝对不可作为做牌目标或替换来源
 */
object ShuffleSlots {
    const val TOTAL_WALL_TILES = 136
    const val MAIN_WALL_TILES = 122
    const val DEAD_WALL_START = 122
    const val DEAD_WALL_END = 135
    const val INITIAL_DEAL_TILES = 53

    /**
     * 计算指定座位玩家在起手发牌阶段所分配到的绝对牌山槽位 (0..52)
     *
     * @param dealerSeat 庄家座位 (0..3)
     * @param seat 目标玩家座位 (0..3)
     * @return 庄家 14 个槽位，子家 13 个槽位
     */
    fun initial(dealerSeat: Int, seat: Int): List<Int> {
        require(dealerSeat in 0..3) { "dealerSeat must be 0..3, got $dealerSeat" }
        require(seat in 0..3) { "seat must be 0..3, got $seat" }

        val k = (seat - dealerSeat + 4) % 4
        val slots = ArrayList<Int>(if (k == 0) 14 else 13)

        // 3 轮每人 4 张 (0..47)
        for (round in 0 until 3) {
            val base = round * 16 + 4 * k
            for (i in 0 until 4) {
                slots.add(base + i)
            }
        }

        // 单张发牌轮 (48..51)
        slots.add(48 + k)

        // 庄家第 14 张 extra (52)
        if (k == 0) {
            slots.add(52)
        }

        return Collections.unmodifiableList(slots)
    }

    /**
     * 计算指定座位玩家在无碰杠理想顺位下，后续前 count 次预计摸牌的绝对牌山槽位 (53..121)
     * 绝不包含 122..135 王牌区。若超出 121 则自动截断。
     *
     * @param dealerSeat 庄家座位 (0..3)
     * @param seat 目标玩家座位 (0..3)
     * @param count 预计摸牌轮数 (0..18，默认 18，覆盖本座位全部 17/18 个预计槽)
     */
    fun draws(dealerSeat: Int, seat: Int, count: Int = 18): List<Int> {
        require(dealerSeat in 0..3) { "dealerSeat must be 0..3, got $dealerSeat" }
        require(seat in 0..3) { "seat must be 0..3, got $seat" }
        require(count >= 0) { "count must be non-negative, got $count" }

        // 顺位偏移：庄家首轮已摸第14张牌，下家 (dealer+1)%4 先摸第53张，随后顺时针轮转
        val turnOffset = (seat - dealerSeat - 1 + 4) % 4
        val slots = ArrayList<Int>()

        for (r in 0 until count) {
            val slot = 53 + 4 * r + turnOffset
            if (slot in 53..121) {
                slots.add(slot)
            }
        }

        return Collections.unmodifiableList(slots)
    }
}
