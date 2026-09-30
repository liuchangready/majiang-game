package com.majiang.model

/**
 * 副露类型
 */
enum class MeldType {
    CHI,          // 吃 (顺子)
    PON,          // 碰 (刻子)
    OPEN_KAN,     // 大明杠
    CLOSED_KAN,   // 暗杠
    ADDED_KAN     // 加杠 (小明杠)
}

/**
 * 副露 (鸣牌)
 *
 * @property type 副露类型
 * @property tiles 组成该副露的所有牌
 * @property fromPlayer 鸣牌对象 (0~3)，如果是暗杠可设为 -1 或自身
 * @property calledTile 实际被鸣的那张牌（如果是暗杠，可为四张牌之一）
 */
data class Meld(
    val type: MeldType,
    val tiles: List<Tile>,
    val fromPlayer: Int,
    val calledTile: Tile
) {
    /** 是否为明副露（除暗杠外的所有鸣牌） */
    val isOpen: Boolean get() = type != MeldType.CLOSED_KAN

    /** 是否为杠子 */
    val isKan: Boolean get() = type == MeldType.OPEN_KAN || type == MeldType.CLOSED_KAN || type == MeldType.ADDED_KAN

    /** 是否为顺子（吃） */
    val isSequence: Boolean get() = type == MeldType.CHI

    /** 是否为刻子（碰） */
    val isTriplet: Boolean get() = type == MeldType.PON

    /** 是否为杠子（四张牌） */
    val isQuad: Boolean get() = isKan
}
