package com.majiang.model

import com.majiang.state.GameState
import com.majiang.state.StateInvariants

/** 牌所在的物理区域 */
enum class Zone {
    WALL,       // 牌山
    HAND,       // 玩家手牌（暗牌与摸牌）
    DISCARD,    // 牌河（物理弃牌堆）
    MELD,       // 副露区（吃碰杠）
    DEAD_WALL,  // 王牌区（岭上牌与宝牌指示）
    SLEEVE,     // 袖里（作弊藏牌区）
    UNASSIGNED  // 未分配（尚未开局）
}

/**
 * 136张牌物理守恒池
 *
 * 记录游戏中所有136张牌的确切物理位置（精确到区域与座位），防止复制或丢失。
 * 遵循 DESIGN.md 3.1 & 3.2：
 * - 绑定 GameState 时，assertConservation() 执行全量实际区域容器审计；
 * - 独立使用时，执行 136 张标准合法身份全覆盖断言；
 * - 不在审计中顺手修复账本。
 */
class TilePool(var gameState: GameState? = null) {
    // 存储每张牌对应的区域和归属位置（精确到座位）
    private val tileMap: MutableMap<Tile, TileLocation> = mutableMapOf()

    init {
        val standardSet = buildStandardSet()
        standardSet.forEach { tile ->
            tileMap[tile] = TileLocation(Zone.UNASSIGNED, -1)
        }
    }

    val size: Int get() = tileMap.size

    /** 只读暴露所有物理位置镜像 */
    val tileLocations: Map<Tile, TileLocation> get() = tileMap.toMap()

    /** 获取指定牌当前所记录的位置 */
    fun getLocation(tile: Tile): TileLocation? = tileMap[tile]

    operator fun get(tile: Tile): TileLocation? = tileMap[tile]

    /** 直接设置指定牌的位置（供受控夹具与状态恢复使用） */
    fun setLocation(tile: Tile, location: TileLocation) {
        require(tileMap.containsKey(tile)) { "Tile $tile not found in pool" }
        tileMap[tile] = location
    }

    /**
     * 将牌从指定位置精确转移到另一位置
     */
    fun transfer(tile: Tile, fromLocation: TileLocation, toLocation: TileLocation) {
        val current = tileMap[tile] ?: throw IllegalArgumentException("Tile $tile not found in pool")
        require(current == fromLocation) {
            "Tile $tile location mismatch: expected $fromLocation, but was $current"
        }
        tileMap[tile] = toLocation
    }

    /**
     * 将牌从一个区域转移到另一个区域（兼容已有调用）
     *
     * @param tile 目标牌
     * @param fromZone 源区域（断言必须匹配当前区域）
     * @param toZone 目标区域
     * @param playerIndex 目标玩家索引（0~3），如非玩家相关区域可传 -1；
     *                    若目标为有座位区域但传入 -1，且绑定了 gameState，将尝试从容器推断座位。
     */
    fun transfer(tile: Tile, fromZone: Zone, toZone: Zone, playerIndex: Int = -1) {
        val current = tileMap[tile] ?: throw IllegalArgumentException("Tile $tile not found in pool")
        require(current.zone == fromZone) {
            "Tile $tile is in ${current.zone}, but expected in $fromZone"
        }

        val resolvedPlayer = if (toZone in setOf(Zone.WALL, Zone.DEAD_WALL, Zone.UNASSIGNED)) {
            -1
        } else if (playerIndex in 0..3) {
            playerIndex
        } else {
            // 尝试从 gameState 容器推断目标座位
            inferPlayerForTile(tile, toZone) ?: throw IllegalArgumentException(
                "Zone $toZone requires a valid player index 0..3, but got $playerIndex for tile $tile"
            )
        }

        tileMap[tile] = TileLocation(toZone, resolvedPlayer)
    }

    /**
     * 精确到源玩家和目标玩家的转移
     */
    fun transfer(tile: Tile, fromZone: Zone, toZone: Zone, fromPlayer: Int, toPlayer: Int) {
        val expectedFrom = TileLocation(fromZone, if (fromZone in setOf(Zone.WALL, Zone.DEAD_WALL, Zone.UNASSIGNED)) -1 else fromPlayer)
        val expectedTo = TileLocation(toZone, if (toZone in setOf(Zone.WALL, Zone.DEAD_WALL, Zone.UNASSIGNED)) -1 else toPlayer)
        transfer(tile, expectedFrom, expectedTo)
    }

    private fun inferPlayerForTile(tile: Tile, zone: Zone): Int? {
        val state = gameState ?: return null
        return when (zone) {
            Zone.HAND -> state.players.indices.firstOrNull { p ->
                state.players[p].closedTiles.contains(tile) || state.players[p].drawnTile == tile
            }
            Zone.DISCARD -> state.players.indices.firstOrNull { p ->
                state.players[p].discards.contains(tile)
            }
            Zone.MELD -> state.players.indices.firstOrNull { p ->
                state.players[p].melds.any { m -> m.tiles.contains(tile) }
            }
            Zone.SLEEVE -> state.players.indices.firstOrNull { p ->
                state.sleeveTiles[p].contains(tile)
            }
            else -> null
        }
    }

    /**
     * 断言 136 张牌的物理守恒性
     *
     * 规范（DESIGN.md 3.2）：
     * - 若绑定了 GameState，则调用 StateInvariants.assertValid 执行实际容器与池的逐张强一致审计；
     * - 若未绑定 GameState，则检查池内是否恰好包含 136 张合法唯一身份且无重复。
     */
    fun assertConservation() {
        val state = gameState
        if (state != null) {
            StateInvariants.assertValid(state)
        } else {
            assertStandaloneConservation()
        }
    }

    private fun assertStandaloneConservation() {
        require(tileMap.size == 136) { "Pool must contain exactly 136 tiles, but has ${tileMap.size}" }
        val standardSet = buildStandardSet()
        val duplicates = tileMap.keys.groupingBy { it }.eachCount().filter { it.value > 1 }
        require(duplicates.isEmpty()) { "Pool contains duplicate tiles: $duplicates" }
        for (tile in standardSet) {
            require(tileMap.containsKey(tile)) { "Pool is missing standard tile $tile" }
        }
    }

    /**
     * 获取指定区域下、指定玩家拥有的所有牌
     *
     * @param zone 目标区域
     * @param playerIndex 玩家索引；若为 -1 且目标区域为 HAND/DISCARD/MELD/SLEEVE，则返回全场所有玩家该区域的牌
     */
    fun getByZone(zone: Zone, playerIndex: Int = -1): List<Tile> {
        return if (playerIndex == -1 && zone !in setOf(Zone.WALL, Zone.DEAD_WALL, Zone.UNASSIGNED)) {
            tileMap.filterValues { it.zone == zone }.keys.toList()
        } else {
            tileMap.filterValues { it.zone == zone && it.player == playerIndex }.keys.toList()
        }
    }

    /**
     * 将所有牌统一重置/分配到一个指定区域
     *
     * @param zone 目标区域，通常为 UNASSIGNED 或 WALL
     */
    fun initializeAll(zone: Zone, playerIndex: Int = -1) {
        val targetPlayer = if (zone in setOf(Zone.WALL, Zone.DEAD_WALL, Zone.UNASSIGNED)) -1 else playerIndex
        val location = TileLocation(zone, targetPlayer)
        val standardSet = buildStandardSet()
        tileMap.clear()
        standardSet.forEach { tile ->
            tileMap[tile] = location
        }
    }

    /** 深拷贝物理牌池 */
    fun deepCopy(newGameState: GameState? = null): TilePool {
        val copy = TilePool(newGameState)
        copy.tileMap.clear()
        copy.tileMap.putAll(this.tileMap)
        return copy
    }

    /**
     * 从另一个 TilePool 完整复制所有账本映射
     */
    fun copyFrom(other: TilePool) {
        this.tileMap.clear()
        this.tileMap.putAll(other.tileMap)
    }

    companion object {
        /**
         * 构建标准136张麻将牌集合
         * 34种牌 * 4张物理副本 (copyIndex 0~3)
         */
        fun buildStandardSet(): List<Tile> {
            val tiles = ArrayList<Tile>(136)
            for (typeIndex in 0..33) {
                for (copyIndex in 0..3) {
                    tiles.add(Tile.fromTypeIndex(typeIndex, copyIndex))
                }
            }
            return tiles
        }
    }
}
