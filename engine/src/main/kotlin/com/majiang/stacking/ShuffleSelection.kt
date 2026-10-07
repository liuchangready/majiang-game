package com.majiang.stacking

import java.util.Collections
import java.util.LinkedHashSet

/**
 * 玩家洗牌千术目标选择方案
 *
 * @property initialTargets 影响起手牌的目标集合（偷天换日，40点）
 * @property drawTargets 影响后续摸牌流的目标集合（瞒天过海，20点）
 */
data class ShuffleSelection(
    val initialTargets: Set<ShuffleTarget> = emptySet(),
    val drawTargets: Set<ShuffleTarget> = emptySet()
) {
    /**
     * 计算本方案消耗的出千点数
     * 偷天换日（initial 非空）40 点，瞒天过海（draw 非空）20 点；同时使用 60 点
     */
    fun cost(): Int =
        (if (initialTargets.isNotEmpty()) 40 else 0) + (if (drawTargets.isNotEmpty()) 20 else 0)

    /** 是否有任何生效千术 */
    val isActive: Boolean get() = initialTargets.isNotEmpty() || drawTargets.isNotEmpty()

    /** 深拷贝，确保不可变集合防御性复制 */
    fun deepCopy(): ShuffleSelection = ShuffleSelection(
        initialTargets = Collections.unmodifiableSet(LinkedHashSet(initialTargets)),
        drawTargets = Collections.unmodifiableSet(LinkedHashSet(drawTargets))
    )
}
