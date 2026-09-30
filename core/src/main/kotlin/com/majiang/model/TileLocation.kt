package com.majiang.model

/**
 * 物理牌所有权记录 — 精确到区域与座位
 *
 * 规范约束（DESIGN.md 3.1）：
 * - WALL, DEAD_WALL, UNASSIGNED 区域无座位归属，player 必须为 -1
 * - HAND, DISCARD, MELD, SLEEVE 区域必须归属于具体玩家座位 (0..3)
 */
data class TileLocation(
    val zone: Zone,
    val player: Int = -1
) {
    init {
        when (zone) {
            Zone.WALL, Zone.DEAD_WALL, Zone.UNASSIGNED -> {
                require(player == -1) { "Zone $zone must not have player owner, but got player=$player" }
            }
            Zone.HAND, Zone.DISCARD, Zone.MELD, Zone.SLEEVE -> {
                require(player in 0..3) { "Zone $zone must belong to player 0..3, but got player=$player" }
            }
        }
    }

    companion object {
        val UNASSIGNED = TileLocation(Zone.UNASSIGNED, -1)
        val WALL = TileLocation(Zone.WALL, -1)
        val DEAD_WALL = TileLocation(Zone.DEAD_WALL, -1)

        fun hand(player: Int) = TileLocation(Zone.HAND, player)
        fun discard(player: Int) = TileLocation(Zone.DISCARD, player)
        fun meld(player: Int) = TileLocation(Zone.MELD, player)
        fun sleeve(player: Int) = TileLocation(Zone.SLEEVE, player)
    }
}
