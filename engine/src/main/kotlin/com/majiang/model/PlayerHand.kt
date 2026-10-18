package com.majiang.model

/**
 * 玩家手牌
 *
 * 管理玩家的暗牌、副露、弃牌以及摸到的牌。
 */
class PlayerHand {
    /** 玩家手中的暗牌（未鸣的牌） */
    val closedTiles: MutableList<Tile> = mutableListOf()

    /** 玩家的副露（鸣牌） */
    val melds: MutableList<Meld> = mutableListOf()

    /** 玩家的牌河（尚未被叫走的物理弃牌，参与物理守恒） */
    val discards: MutableList<Tile> = mutableListOf()

    /** 玩家的完整舍牌历史（不会因吃碰杠而删除），不参与物理守恒计数，供振听等规则判定使用 */
    val discardHistory: MutableList<Tile> = mutableListOf()

    /** 玩家刚摸到但尚未处理的牌（用于展示，与暗牌分离） */
    var drawnTile: Tile? = null

    /**
     * 是否已宣告立直
     *
     * 镜像字段（MR-09a）：与 GameState 同名数组保持 INV1~INV3 同步；生产代码只经
     * GameState.declareRiichi / setIppatsu / clearAllIppatsu 写入，禁止独立赋值。
     */
    var riichiDeclared: Boolean = false

    /**
     * 宣告立直时的巡目（未宣告则为 -1）
     *
     * 镜像字段（MR-09a）：与 GameState 同名数组保持 INV1~INV3 同步；生产代码只经
     * GameState.declareRiichi / setIppatsu / clearAllIppatsu 写入，禁止独立赋值。
     */
    var riichiTurn: Int = -1

    /**
     * 是否处于一发状态（立直后的一巡内，如果有人鸣牌或自己摸牌后会解除）
     *
     * 镜像字段（MR-09a）：与 GameState 同名数组保持 INV1~INV3 同步；生产代码只经
     * GameState.declareRiichi / setIppatsu / clearAllIppatsu 写入，禁止独立赋值。
     */
    var ippatsu: Boolean = false

    /** 门前清状态（只要没有明副露即为门前清，暗杠不算明副露） */
    val isClosed: Boolean
        get() = melds.all { !it.isOpen }

    /** 手牌中的等效牌数（暗牌数量 + 鸣牌组数 * 3 + 刚摸的牌）。用于验证13/14张牌状态 */
    val tileCount: Int
        get() = closedTiles.size + melds.size * 3 + (if (drawnTile != null) 1 else 0)

    /** 所有的暗牌（包含刚摸到的牌，摸到的牌放在最后） */
    val allClosedTiles: List<Tile>
        get() = if (drawnTile != null) closedTiles + drawnTile!! else closedTiles.toList()

    /** 检查是否持有指定物理牌（含 drawnTile 与 closedTiles） */
    fun contains(tile: Tile): Boolean = drawnTile == tile || closedTiles.contains(tile)

    /** 添加一张暗牌入手中，并排序 */
    fun addTile(tile: Tile) {
        closedTiles.add(tile)
        closedTiles.sort()
    }

    /** 
     * 从手中移除一张暗牌（或刚摸的牌）
     * 优先尝试移除刚摸到的牌
     */
    fun removeTile(tile: Tile): Boolean {
        if (drawnTile == tile) {
            drawnTile = null
            return true
        }
        return closedTiles.remove(tile)
    }

    /** 增加一组副露 */
    fun addMeld(meld: Meld) {
        melds.add(meld)
    }

    /** 弃一张牌放入牌河与舍牌历史 */
    fun addDiscard(tile: Tile) {
        discards.add(tile)
        discardHistory.add(tile)
    }

    /** 清空手牌状态 */
    fun clear() {
        closedTiles.clear()
        melds.clear()
        discards.clear()
        discardHistory.clear()
        drawnTile = null
        riichiDeclared = false
        riichiTurn = -1
        ippatsu = false
    }

    /** 深拷贝 */
    fun deepCopy(): PlayerHand {
        val copy = PlayerHand()
        copy.closedTiles.addAll(this.closedTiles)
        copy.melds.addAll(this.melds.map { it.copy(tiles = it.tiles.toList()) })
        copy.discards.addAll(this.discards)
        copy.discardHistory.addAll(this.discardHistory)
        copy.drawnTile = this.drawnTile
        copy.riichiDeclared = this.riichiDeclared
        copy.riichiTurn = this.riichiTurn
        copy.ippatsu = this.ippatsu
        return copy
    }
}
