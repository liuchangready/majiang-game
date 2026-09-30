package com.majiang.model

/** 花色 */
enum class Suit {
    MAN,    // 万子
    PIN,    // 筒子
    SOU,    // 索子
    WIND,   // 风牌
    DRAGON  // 三元牌
}

/**
 * 单张麻将牌 — 不可变值对象
 *
 * @property suit 花色
 * @property rank 数字：数牌1~9, 风牌1~4(东南西北), 三元牌1~3(白发中)
 * @property copyIndex 同种牌的物理副本编号 0~3（每种牌有4张）
 */
data class Tile(
    val suit: Suit,
    val rank: Int,
    val copyIndex: Int = 0
) : Comparable<Tile> {

    init {
        require(copyIndex in 0..3) { "copyIndex must be 0..3, got $copyIndex" }
        when (suit) {
            Suit.MAN, Suit.PIN, Suit.SOU -> require(rank in 1..9) { "$suit rank must be 1..9, got $rank" }
            Suit.WIND -> require(rank in 1..4) { "WIND rank must be 1..4, got $rank" }
            Suit.DRAGON -> require(rank in 1..3) { "DRAGON rank must be 1..3, got $rank" }
        }
    }

    /** 是否为老头牌（1或9的数牌） */
    val isTerminal: Boolean get() = suit in setOf(Suit.MAN, Suit.PIN, Suit.SOU) && rank in setOf(1, 9)

    /** 是否为字牌（风牌或三元牌） */
    val isHonor: Boolean get() = suit == Suit.WIND || suit == Suit.DRAGON

    /** 是否为幺九牌（老头牌或字牌） */
    val isTerminalOrHonor: Boolean get() = isTerminal || isHonor

    /** 是否为中张牌（2~8的数牌） */
    val isSimple: Boolean get() = !isTerminalOrHonor

    /** 是否为数牌 */
    val isNumberTile: Boolean get() = suit in setOf(Suit.MAN, Suit.PIN, Suit.SOU)

    /** 赤宝牌：关东三张制——每种数牌 5 的 copyIndex==3 物理副本为红五 */
    val isRedFive: Boolean
        get() = rank == 5 && copyIndex == 3 &&
                (suit == Suit.MAN || suit == Suit.PIN || suit == Suit.SOU)

    /** 是否为绿一色构成牌（索子2,3,4,6,8 + 发） */
    val isGreen: Boolean get() = (suit == Suit.SOU && rank in setOf(2, 3, 4, 6, 8)) ||
            (suit == Suit.DRAGON && rank == 2) // 发=Dragon rank 2

    /**
     * 34维特征索引 (0~33)，与 STACKING_ALGORITHM 中的定义一致
     * Man: 0..8, Sou: 9..17, Pin: 18..26, Wind: 27..30, Dragon: 31..33
     */
    val typeIndex: Int get() = when (suit) {
        Suit.MAN -> rank - 1
        Suit.SOU -> rank - 1 + 9
        Suit.PIN -> rank - 1 + 18
        Suit.WIND -> rank - 1 + 27
        Suit.DRAGON -> rank - 1 + 31
    }

    override fun compareTo(other: Tile): Int =
        compareValuesBy(this, other, { it.suit }, { it.rank }, { it.copyIndex })

    /** 忽略 copyIndex 的逻辑类型相等（两张 1万 视为同种牌） */
    fun sameType(other: Tile): Boolean = suit == other.suit && rank == other.rank

    /** 获取该牌作为Dora指示牌时，指示的Dora牌的typeIndex */
    fun doraIndicatorTarget(): Int = when (suit) {
        Suit.MAN, Suit.PIN, Suit.SOU -> {
            val nextRank = if (rank == 9) 1 else rank + 1
            Tile(suit, nextRank).typeIndex
        }
        Suit.WIND -> {
            val nextRank = if (rank == 4) 1 else rank + 1
            Tile(suit, nextRank).typeIndex
        }
        Suit.DRAGON -> {
            val nextRank = if (rank == 3) 1 else rank + 1
            Tile(suit, nextRank).typeIndex
        }
    }

    /** 可读的牌名 */
    fun displayName(): String = when (suit) {
        Suit.MAN -> "${rank}万"
        Suit.PIN -> "${rank}筒"
        Suit.SOU -> "${rank}索"
        Suit.WIND -> listOf("東", "南", "西", "北")[rank - 1]
        Suit.DRAGON -> listOf("白", "發", "中")[rank - 1]
    }

    companion object {
        /** 根据34维typeIndex创建一张牌（copyIndex=0） */
        fun fromTypeIndex(index: Int, copyIndex: Int = 0): Tile = when {
            index in 0..8 -> Tile(Suit.MAN, index + 1, copyIndex)
            index in 9..17 -> Tile(Suit.SOU, index - 9 + 1, copyIndex)
            index in 18..26 -> Tile(Suit.PIN, index - 18 + 1, copyIndex)
            index in 27..30 -> Tile(Suit.WIND, index - 27 + 1, copyIndex)
            index in 31..33 -> Tile(Suit.DRAGON, index - 31 + 1, copyIndex)
            else -> throw IllegalArgumentException("Invalid typeIndex: $index")
        }

        /** 根据 0..135 唯一整数标识创建物理牌 (typeIndex * 4 + copyIndex) */
        fun fromTileId(tileId: Int): Tile {
            require(tileId in 0..135) { "tileId must be 0..135, got $tileId" }
            return fromTypeIndex(tileId / 4, tileId % 4)
        }
    }

    /** 0..135 全局物理唯一标识 */
    val tileId: Int get() = typeIndex * 4 + copyIndex
}
