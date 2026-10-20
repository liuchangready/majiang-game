package com.majiang.stacking

import java.util.Collections

/**
 * 玩家洗牌千术目标选择方案（统一码牌千术）
 *
 * 准备阶段只提交一次、只选一个目标。目标同时作用于起手发牌与本局全部正常顺位预计摸牌槽。
 *
 * @property target 唯一做牌目标；null 表示本局不做牌（跳过目标，0 点）
 * @property seeThrough 本局准备阶段是否使用看破（10 点，抵消被看破者整局码牌效果并清除其既有落汗）
 */
data class ShuffleSelection(
    val target: ShuffleTarget? = null,
    val seeThrough: Boolean = false
) {
    /**
     * 计算本方案消耗的出千点数：
     * 统一目标 30 点/局，看破 10 点/局，可叠加为 40 点；跳过 0 点。
     */
    fun cost(): Int =
        (if (target != null) TARGET_COST else 0) +
            (if (seeThrough) SEE_THROUGH_COST else 0)

    /** 是否有任何生效千术（仅选择看破也是合法非空方案） */
    val isActive: Boolean
        get() = target != null || seeThrough

    /** 深拷贝，确保不可变集合防御性复制（当前无集合字段，保留语义不变） */
    fun deepCopy(): ShuffleSelection = copy()

    companion object {
        /** 统一码牌目标固定费用（权威校验使用该常量，不接受客户端自报费用） */
        const val TARGET_COST: Int = 30

        /** 看破固定费用（权威校验使用该常量，不接受客户端自报费用） */
        const val SEE_THROUGH_COST: Int = 10

        /** 准备阶段单方案合法费用上限：30 + 10 = 40 */
        const val MAX_COST: Int = TARGET_COST + SEE_THROUGH_COST
    }
}
