package com.majiang.state

import com.majiang.economy.CheatLedgerEntry
import com.majiang.model.*

/**
 * 完整的游戏状态快照 — 纯数据容器
 *
 * 所有字段均为可变的，由权威状态机（RoundStateMachine / ApplicationCoordinator）及 TileMovement 修改。
 * 遵循 DESIGN.md 3.1：
 * - 活牌山范围 [wallPointer, wallEndExclusive)
 * - 有效王牌 deadWall[rinshanPointer, size) + deadWallReplacements
 * - sleeve 按座位存储 List(4) { mutableListOf() }
 * - tilePool 与当前 GameState 深度绑定并自动审计
 */
class GameState {
    // === 牌山 ===
    /** 牌山容器（初始化或码牌后装填） */
    val wall: MutableList<Tile> = mutableListOf()
    
    /** 王牌区（14张：岭上牌 + Dora指示牌等） */
    val deadWall: MutableList<Tile> = mutableListOf()

    /** 岭上摸牌后从活牌山末端补入王牌区的替补牌 */
    val deadWallReplacements: MutableList<Tile> = mutableListOf()
    
    /** 牌山摸牌指针（下一张可摸牌的位置） */
    var wallPointer: Int = 0
    
    /** 活牌山有效结束索引（开区间） */
    var wallEndExclusive: Int = 0

    /** 岭上牌指针（0..deadWall.size） */
    var rinshanPointer: Int = 0

    // === 玩家 ===
    /** 四位玩家的手牌状态 */
    val players: List<PlayerHand> = List(4) { PlayerHand() }
    
    /** 四位玩家的分数（初始25000点） */
    val scores: IntArray = IntArray(4) { 25000 }

    // === 对局信息 ===
    /** 场风: 1=東, 2=南, 3=西, 4=北 */
    var roundWind: Int = 1
    
    /** 局数: 0=東一局, 1=東二局, ... */
    var roundNumber: Int = 0
    
    /** 庄家座位 (0~3) */
    var dealerIndex: Int = 0
    
    /** 当前行动玩家 (0~3) */
    var currentPlayer: Int = 0
    
    /** 当前巡目 */
    var turnNumber: Int = 0
    
    /** 当前阶段 */
    var phase: GamePhase = GamePhase.STACKING

    // === 立直 ===
    /** 各玩家是否已宣言立直 */
    val riichiDeclared: BooleanArray = BooleanArray(4) { false }
    
    /** 各玩家是否宣告了两立直 (Double Riichi) */
    val doubleRiichiDeclared: BooleanArray = BooleanArray(4) { false }
    
    /** 各玩家宣言立直的巡目（-1=未宣言） */
    val riichiTurn: IntArray = IntArray(4) { -1 }
    
    /** 一发标记 */
    val ippatsuActive: BooleanArray = BooleanArray(4) { false }
    
    /** 场上立直棒数 */
    var riichiSticks: Int = 0
    
    /** 本场数（连庄/流局累积） */
    var honbaSticks: Int = 0

    // === Dora ===
    /** Dora指示牌列表 */
    val doraIndicators: MutableList<Tile> = mutableListOf()
    
    /** 里Dora指示牌列表 */
    val uraDoraIndicators: MutableList<Tile> = mutableListOf()

    // === 最后动作记录 ===
    /** 最后被打出的牌 */
    var lastDiscard: Tile? = null
    
    /** 最后打牌的玩家 */
    var lastDiscardPlayer: Int = -1
    
    /** 是否为第一巡（用于天和/地和/两立直判定） */
    var isFirstUninterruptedGo: Boolean = true
    
    /** 当前是否为岭上摸牌后的和牌窗口 */
    var isRinshanDraw: Boolean = false
    
    /** 杠的总数（四杠散了判定） */
    var totalKanCount: Int = 0
    
    /** 各玩家杠的数量 */
    val playerKanCounts: IntArray = IntArray(4) { 0 }

    // === 振听状态 ===
    /** 同巡振听标记（放弃荣和后本巡振听，在本人下次摸牌后清除） */
    val sameTurnFuriten: BooleanArray = BooleanArray(4) { false }

    /** 立直振听标记（立直后放弃任何荣和机会，终局前持续振听） */
    val riichiFuriten: BooleanArray = BooleanArray(4) { false }

    // === 物理牌池 ===
    /** 全局物理守恒牌池 */
    val tilePool: TilePool = TilePool()

    init {
        tilePool.gameState = this
    }

    // === 千术相关 ===
    /** 各玩家袖中暗藏牌（出千专用），按座位存储 (0..3) */
    val sleeveTiles: List<MutableList<Tile>> = List(4) { mutableListOf() }

    /** 四位玩家的出千点数余额（初始各0点；整场归属、跨局结转，不随单局重置清零） */
    val cheatPoints: IntArray = IntArray(4) { 0 }

    /** 本局已入账的出千流水记录 */
    val cheatEntries: MutableList<CheatLedgerEntry> = mutableListOf()

    // === 辅助方法 ===
    
    /** 牌山剩余可摸牌数：活牌山开区间长度 */
    val wallRemaining: Int get() = maxOf(0, wallEndExclusive - wallPointer)
    
    /** 获取下一位玩家（逆时针 0→1→2→3→0） */
    fun nextPlayer(current: Int = currentPlayer): Int = (current + 1) % 4
    
    /** 获取上家 */
    fun prevPlayer(current: Int = currentPlayer): Int = (current + 3) % 4
    
    /** 获取对家 */
    fun acrossPlayer(current: Int = currentPlayer): Int = (current + 2) % 4
    
    /** 获取玩家的自风 (1=東, 2=南, 3=西, 4=北) */
    fun seatWind(player: Int): Int = ((player - dealerIndex + 4) % 4) + 1

    /** 获取指定座位的袖中牌（便利方法） */
    fun getSleeve(player: Int): MutableList<Tile> = sleeveTiles[player]

    /**
     * 重置为新一局。
     *
     * 保留整场归属的账本字段（scores / riichiSticks / honbaSticks / cheatPoints）：
     * 出千点数余额由 MatchSession.betweenRoundCheatPoints 在局间交接并写入新局，
     * 不得随单局重置清零；仅本局流水 cheatEntries 与物理状态（牌山/手牌/袖牌）按局清空。
     */
    fun resetForNewRound() {
        wall.clear()
        deadWall.clear()
        deadWallReplacements.clear()
        wallPointer = 0
        wallEndExclusive = 0
        rinshanPointer = 0
        for (hand in players) {
            hand.clear()
        }
        riichiDeclared.fill(false)
        doubleRiichiDeclared.fill(false)
        riichiTurn.fill(-1)
        ippatsuActive.fill(false)
        sameTurnFuriten.fill(false)
        riichiFuriten.fill(false)
        doraIndicators.clear()
        uraDoraIndicators.clear()
        lastDiscard = null
        lastDiscardPlayer = -1
        isFirstUninterruptedGo = true
        isRinshanDraw = false
        totalKanCount = 0
        playerKanCounts.fill(0)
        sleeveTiles.forEach { it.clear() }
        cheatEntries.clear()
        turnNumber = 0
        phase = GamePhase.STACKING
        tilePool.initializeAll(Zone.UNASSIGNED)
    }

    /**
     * 完整深拷贝，供事务执行、状态审计与无副作用探测
     */
    fun deepCopy(): GameState {
        val copy = GameState()
        copy.copyFrom(this)
        return copy
    }

    /**
     * 从另一个 GameState 完整拷贝状态数据到当前对象中，保持当前对象的引用不变
     */
    fun copyFrom(other: GameState) {
        this.wall.clear()
        this.wall.addAll(other.wall)
        this.deadWall.clear()
        this.deadWall.addAll(other.deadWall)
        this.deadWallReplacements.clear()
        this.deadWallReplacements.addAll(other.deadWallReplacements)
        this.wallPointer = other.wallPointer
        this.wallEndExclusive = other.wallEndExclusive
        this.rinshanPointer = other.rinshanPointer

        for (i in 0 until 4) {
            val srcHand = other.players[i]
            val dstHand = this.players[i]
            dstHand.clear()
            dstHand.closedTiles.addAll(srcHand.closedTiles)
            dstHand.melds.addAll(srcHand.melds.map { it.copy(tiles = it.tiles.toList()) })
            dstHand.discards.addAll(srcHand.discards)
            dstHand.discardHistory.addAll(srcHand.discardHistory)
            dstHand.drawnTile = srcHand.drawnTile
            dstHand.riichiDeclared = srcHand.riichiDeclared
            dstHand.riichiTurn = srcHand.riichiTurn
            dstHand.ippatsu = srcHand.ippatsu

            this.scores[i] = other.scores[i]
            this.riichiDeclared[i] = other.riichiDeclared[i]
            this.doubleRiichiDeclared[i] = other.doubleRiichiDeclared[i]
            this.riichiTurn[i] = other.riichiTurn[i]
            this.ippatsuActive[i] = other.ippatsuActive[i]
            this.playerKanCounts[i] = other.playerKanCounts[i]
            this.sameTurnFuriten[i] = other.sameTurnFuriten[i]
            this.riichiFuriten[i] = other.riichiFuriten[i]

            this.sleeveTiles[i].clear()
            this.sleeveTiles[i].addAll(other.sleeveTiles[i])
            this.cheatPoints[i] = other.cheatPoints[i]
        }

        this.cheatEntries.clear()
        this.cheatEntries.addAll(other.cheatEntries.map { it.copy() })

        this.roundWind = other.roundWind
        this.roundNumber = other.roundNumber
        this.dealerIndex = other.dealerIndex
        this.currentPlayer = other.currentPlayer
        this.turnNumber = other.turnNumber
        this.phase = other.phase

        this.riichiSticks = other.riichiSticks
        this.honbaSticks = other.honbaSticks

        this.doraIndicators.clear()
        this.doraIndicators.addAll(other.doraIndicators)
        this.uraDoraIndicators.clear()
        this.uraDoraIndicators.addAll(other.uraDoraIndicators)
        this.lastDiscard = other.lastDiscard
        this.lastDiscardPlayer = other.lastDiscardPlayer
        this.isFirstUninterruptedGo = other.isFirstUninterruptedGo
        this.isRinshanDraw = other.isRinshanDraw
        this.totalKanCount = other.totalKanCount

        // 深度复制 tilePool
        this.tilePool.copyFrom(other.tilePool)
        this.tilePool.gameState = this
    }

    /**
     * 将当前状态拷贝到目标 state 对象中
     */
    fun copyInto(target: GameState) {
        target.copyFrom(this)
    }

    /**
     * 从快照完整恢复当前状态
     */
    fun restore(snapshot: GameState) {
        copyFrom(snapshot)
    }
}
