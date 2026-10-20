package com.majiang.stacking

import java.util.Collections
import java.util.LinkedHashSet

/**
 * 玩家洗牌千术目标选择方案
 *
 * @property initialTargets 影响起手牌的目标集合（偷天换日，40点）
 * @property drawTargets 影响后续摸牌流的目标集合（瞒天过海，20点）
 * @property seeThrough 本局准备阶段是否使用看破（10点，仅抵消对手本局偷天换日并清除对手既有落汗）
 */
data class ShuffleSelection(
    val initialTargets: Set<ShuffleTarget> = emptySet(),
    val drawTargets: Set<ShuffleTarget> = emptySet(),
    val seeThrough: Boolean = false
) {
    /**
     * 计算本方案消耗的出千点数
     * 偷天换日（initial 非空）40 点，瞒天过海（draw 非空）20 点，看破 10 点；
     * 同时使用最高 70 点。
     */
    fun cost(): Int =
        (if (initialTargets.isNotEmpty()) 40 else 0) +
            (if (drawTargets.isNotEmpty()) 20 else 0) +
            (if (seeThrough) SEE_THROUGH_COST else 0)

    /** 是否有任何生效千术（仅选择看破也是合法非空方案） */
    val isActive: Boolean
        get() = initialTargets.isNotEmpty() || drawTargets.isNotEmpty() || seeThrough

    /** 深拷贝，确保不可变集合防御性复制 */
    fun deepCopy(): ShuffleSelection = ShuffleSelection(
        initialTargets = Collections.unmodifiableSet(LinkedHashSet(initialTargets)),
        drawTargets = Collections.unmodifiableSet(LinkedHashSet(drawTargets)),
        seeThrough = seeThrough
    )

    companion object {
        /** 看破固定费用（权威校验使用该常量，不接受客户端自报费用） */
        const val SEE_THROUGH_COST: Int = 10

        /** 准备阶段单方案合法费用上限：40 + 20 + 10 = 70 */
        const val MAX_COST: Int = 70
    }
}
