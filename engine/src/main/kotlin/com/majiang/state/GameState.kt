package com.majiang.state

import com.majiang.economy.CheatLedgerEntry
import com.majiang.model.*

/**
 * 桌面公共状态与局况 — 纯数据容器
 *
 * 遵循 DESIGN.md 3.1 & 3.2：
 * - 活牌山范围 [wallPointer, wallEndExclusive)
 * - 有效王牌 deadWall[rinshanPointer, size) + deadWallReplacements
 * - Dora 指示牌与待翻杠 Dora 计数
 * - 场风、局数、庄家、当前出牌玩家、巡目、阶段
 * - 桌面供托（立直棒）与本场棒
 * - 桌面最近动作（lastDiscard, lastDiscardPlayer, isFirstUninterruptedGo, isRinshanDraw, totalKanCount）
 */
internal class BoardState {
    // === 牌山 ===
    val wall: MutableList<Tile> = mutableListOf()
    val deadWall: MutableList<Tile> = mutableListOf()
    val deadWallReplacements: MutableList<Tile> = mutableListOf()
    var wallPointer: Int = 0
    var wallEndExclusive: Int = 0
    var rinshanPointer: Int = 0

    // === 场况与局信息 ===
    var roundWind: Int = 1
    var roundNumber: Int = 0
    var dealerIndex: Int = 0
    var currentPlayer: Int = 0
    var turnNumber: Int = 0
    var phase: GamePhase = GamePhase.STACKING

    // === 桌面供托与场棒 ===
    var riichiSticks: Int = 0
    var honbaSticks: Int = 0

    // === Dora ===
    val doraIndicators: MutableList<Tile> = mutableListOf()
    val uraDoraIndicators: MutableList<Tile> = mutableListOf()
    var pendingKanDoraCount: Int = 0

    // === 桌面动作记录 ===
    var lastDiscard: Tile? = null
    var lastDiscardPlayer: Int = -1
    var isFirstUninterruptedGo: Boolean = true
    var isRinshanDraw: Boolean = false
    var totalKanCount: Int = 0

    val wallRemaining: Int get() = maxOf(0, wallEndExclusive - wallPointer)

    fun nextPlayer(current: Int = currentPlayer): Int = (current + 1) % 4
    fun prevPlayer(current: Int = currentPlayer): Int = (current + 3) % 4
    fun acrossPlayer(current: Int = currentPlayer): Int = (current + 2) % 4
    fun seatWind(player: Int): Int = ((player - dealerIndex + 4) % 4) + 1

    fun resetForNewRound() {
        wall.clear()
        deadWall.clear()
        deadWallReplacements.clear()
        wallPointer = 0
        wallEndExclusive = 0
        rinshanPointer = 0
        doraIndicators.clear()
        uraDoraIndicators.clear()
        pendingKanDoraCount = 0
        lastDiscard = null
        lastDiscardPlayer = -1
        isFirstUninterruptedGo = true
        isRinshanDraw = false
        totalKanCount = 0
        turnNumber = 0
        phase = GamePhase.STACKING
        // roundWind, roundNumber, dealerIndex, currentPlayer, riichiSticks, honbaSticks are PRESERVED!
    }

    fun copyFrom(other: BoardState) {
        this.wall.clear()
        this.wall.addAll(other.wall)
        this.deadWall.clear()
        this.deadWall.addAll(other.deadWall)
        this.deadWallReplacements.clear()
        this.deadWallReplacements.addAll(other.deadWallReplacements)
        this.wallPointer = other.wallPointer
        this.wallEndExclusive = other.wallEndExclusive
        this.rinshanPointer = other.rinshanPointer

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
        this.pendingKanDoraCount = other.pendingKanDoraCount
        this.lastDiscard = other.lastDiscard
        this.lastDiscardPlayer = other.lastDiscardPlayer
        this.isFirstUninterruptedGo = other.isFirstUninterruptedGo
        this.isRinshanDraw = other.isRinshanDraw
        this.totalKanCount = other.totalKanCount
    }

    fun deepCopy(): BoardState {
        val copy = BoardState()
        copy.copyFrom(this)
        return copy
    }
}

/**
 * 单席位专属状态容器
 *
 * 封装四位玩家各席位 (0..3) 的私有与席位级状态：
 * - hand: PlayerHand（手牌、摸牌、副露、弃牌、弃牌历史）
 * - score: 玩家分数（初始 25000 点，整场保留）
 * - riichiDeclared, doubleRiichiDeclared, riichiTurn, ippatsuActive（立直与一发状态）
 * - sameTurnFuriten, riichiFuriten（振听状态）
 * - kanCount: 席位开杠计数
 *
 * 立直/一发镜像不变量（MR-09a）：
 * 宣言立直与一发变更通过 declareRiichi / setIppatsu 入口同步写入 SeatState 与 PlayerHand。
 */
internal class SeatState(
    val seat: Int,
    private val scoresRef: IntArray = IntArray(4) { 25000 },
    private val riichiDeclaredRef: BooleanArray = BooleanArray(4) { false },
    private val doubleRiichiDeclaredRef: BooleanArray = BooleanArray(4) { false },
    private val riichiTurnRef: IntArray = IntArray(4) { -1 },
    private val ippatsuActiveRef: BooleanArray = BooleanArray(4) { false },
    private val playerKanCountsRef: IntArray = IntArray(4) { 0 },
    private val sameTurnFuritenRef: BooleanArray = BooleanArray(4) { false },
    private val riichiFuritenRef: BooleanArray = BooleanArray(4) { false }
) {
    val hand: PlayerHand = PlayerHand()

    var score: Int
        get() = scoresRef[seat]
        set(value) { scoresRef[seat] = value }

    // 立直与一发状态：禁止外部直接写，强制经由 declareRiichi 与 setIppatsu 入口
    val riichiDeclared: Boolean
        get() = riichiDeclaredRef[seat]

    val doubleRiichiDeclared: Boolean
        get() = doubleRiichiDeclaredRef[seat]

    val riichiTurn: Int
        get() = riichiTurnRef[seat]

    val ippatsuActive: Boolean
        get() = ippatsuActiveRef[seat]

    var kanCount: Int
        get() = playerKanCountsRef[seat]
        set(value) { playerKanCountsRef[seat] = value }

    var sameTurnFuriten: Boolean
        get() = sameTurnFuritenRef[seat]
        set(value) { sameTurnFuritenRef[seat] = value }

    var riichiFuriten: Boolean
        get() = riichiFuritenRef[seat]
        set(value) { riichiFuritenRef[seat] = value }

    fun declareRiichi(turn: Int, isDouble: Boolean) {
        riichiDeclaredRef[seat] = true
        riichiTurnRef[seat] = turn
        doubleRiichiDeclaredRef[seat] = isDouble
        ippatsuActiveRef[seat] = true
        hand.riichiDeclared = true
        hand.riichiTurn = turn
        hand.ippatsu = true
    }

    fun setIppatsu(active: Boolean) {
        ippatsuActiveRef[seat] = active
        hand.ippatsu = active
    }

    fun resetForNewRound() {
        hand.clear()
        riichiDeclaredRef[seat] = false
        doubleRiichiDeclaredRef[seat] = false
        riichiTurnRef[seat] = -1
        ippatsuActiveRef[seat] = false
        sameTurnFuritenRef[seat] = false
        riichiFuritenRef[seat] = false
        playerKanCountsRef[seat] = 0
        // score is PRESERVED!
    }

    fun copyFrom(other: SeatState) {
        hand.clear()
        hand.closedTiles.addAll(other.hand.closedTiles)
        hand.melds.addAll(other.hand.melds.map { it.copy(tiles = it.tiles.toList()) })
        hand.discards.addAll(other.hand.discards)
        hand.discardHistory.addAll(other.hand.discardHistory)
        hand.drawnTile = other.hand.drawnTile
        hand.riichiDeclared = other.hand.riichiDeclared
        hand.riichiTurn = other.hand.riichiTurn
        hand.ippatsu = other.hand.ippatsu

        this.score = other.score
        this.riichiDeclaredRef[seat] = other.riichiDeclared
        this.doubleRiichiDeclaredRef[seat] = other.doubleRiichiDeclared
        this.riichiTurnRef[seat] = other.riichiTurn
        this.ippatsuActiveRef[seat] = other.ippatsuActive
        this.sameTurnFuriten = other.sameTurnFuriten
        this.riichiFuriten = other.riichiFuriten
        this.kanCount = other.kanCount
    }

    fun deepCopy(): SeatState {
        val copy = SeatState(seat)
        copy.copyFrom(this)
        return copy
    }
}

/**
 * 袖里藏花挂起状态
 */
internal class SleeveBloomState {
    var offer: com.majiang.cheat.bloom.SleeveBloomOffer? = null
    var usedOnTurn: Int = -1
    var lastDrawKind: com.majiang.cheat.bloom.SleeveBloomDrawKind = com.majiang.cheat.bloom.SleeveBloomDrawKind.DEAL_EXTRA

    fun resetForNewRound() {
        offer = null
        usedOnTurn = -1
        lastDrawKind = com.majiang.cheat.bloom.SleeveBloomDrawKind.DEAL_EXTRA
    }

    fun copyFrom(other: SleeveBloomState) {
        this.offer = other.offer?.copy()
        this.usedOnTurn = other.usedOnTurn
        this.lastDrawKind = other.lastDrawKind
    }

    fun deepCopy(): SleeveBloomState {
        val copy = SleeveBloomState()
        copy.copyFrom(this)
        return copy
    }
}

/**
 * 偷梁换柱挂起状态
 */
internal class BeamSwapState {
    var offer: com.majiang.cheat.beam.BeamSwapOffer? = null
    var usedOnTurn: Int = -1

    fun resetForNewRound() {
        offer = null
        usedOnTurn = -1
    }

    fun copyFrom(other: BeamSwapState) {
        this.offer = other.offer?.copy()
        this.usedOnTurn = other.usedOnTurn
    }

    fun deepCopy(): BeamSwapState {
        val copy = BeamSwapState()
        copy.copyFrom(this)
        return copy
    }
}

/**
 * 出千体系状态容器
 *
 * 遵循三大绝技与出千账本规范：
 * - sleeveTiles: 各席位袖中暗藏牌 (0..3)，物理守恒由 TilePool 追踪
 * - cheatPoints: 四位玩家出千点数余额（整场保留，跨局结转）
 * - cheatEntries: 本局已入账的出千流水记录
 * - sleeveBloom: 袖里藏花状态
 * - beamSwap: 偷梁换柱状态
 */
internal class CheatState {
    val sleeveTiles: List<MutableList<Tile>> = List(4) { mutableListOf() }
    val cheatPoints: IntArray = IntArray(4) { 0 }
    val cheatEntries: MutableList<CheatLedgerEntry> = mutableListOf()
    val sleeveBloom: SleeveBloomState = SleeveBloomState()
    val beamSwap: BeamSwapState = BeamSwapState()

    var sleeveBloomOffer: com.majiang.cheat.bloom.SleeveBloomOffer?
        get() = sleeveBloom.offer
        set(value) { sleeveBloom.offer = value }

    var sleeveBloomUsedOnTurn: Int
        get() = sleeveBloom.usedOnTurn
        set(value) { sleeveBloom.usedOnTurn = value }

    var lastDrawKind: com.majiang.cheat.bloom.SleeveBloomDrawKind
        get() = sleeveBloom.lastDrawKind
        set(value) { sleeveBloom.lastDrawKind = value }

    var beamSwapOffer: com.majiang.cheat.beam.BeamSwapOffer?
        get() = beamSwap.offer
        set(value) { beamSwap.offer = value }

    var beamSwapUsedOnTurn: Int
        get() = beamSwap.usedOnTurn
        set(value) { beamSwap.usedOnTurn = value }

    fun getSleeve(player: Int): MutableList<Tile> = sleeveTiles[player]

    fun resetForNewRound() {
        sleeveTiles.forEach { it.clear() }
        cheatEntries.clear()
        sleeveBloom.resetForNewRound()
        beamSwap.resetForNewRound()
        // cheatPoints is PRESERVED!
    }

    fun copyFrom(other: CheatState) {
        for (i in 0 until 4) {
            this.sleeveTiles[i].clear()
            this.sleeveTiles[i].addAll(other.sleeveTiles[i])
            this.cheatPoints[i] = other.cheatPoints[i]
        }
        this.cheatEntries.clear()
        this.cheatEntries.addAll(other.cheatEntries.map { it.copy() })
        this.sleeveBloom.copyFrom(other.sleeveBloom)
        this.beamSwap.copyFrom(other.beamSwap)
    }

    fun deepCopy(): CheatState {
        val copy = CheatState()
        copy.copyFrom(this)
        return copy
    }
}

/**
 * 完整的游戏状态聚合根 — 组合 BoardState、SeatState、CheatState 与 TilePool
 *
 * 遵循 V4 架构领域规范与 D-06 状态拆分设计：
 * - 明确状态拥有者：board 拥有牌山/王牌/指示牌/局况/供托；seats 拥有四席手牌/点数/立直/振听/开杠；cheat 拥有袖牌/点数/流水/绝技挂起；tilePool 拥有 136 张牌全局守恒位置。
 * - 保持 MR-09a 镜像不变量 (INV1~INV6)。
 * - 所有修改经既有领域操作与权威流程执行，不向外部暴露无监管可变子状态。
 * - 显式维护 copyFrom、deepCopy 与 resetForNewRound 治理。
 */
class GameState() {
    private val board: BoardState = BoardState()
    val scores: IntArray = IntArray(4) { 25000 }
    val riichiDeclared: BooleanArray = BooleanArray(4) { false }
    val doubleRiichiDeclared: BooleanArray = BooleanArray(4) { false }
    val riichiTurn: IntArray = IntArray(4) { -1 }
    val ippatsuActive: BooleanArray = BooleanArray(4) { false }
    val playerKanCounts: IntArray = IntArray(4) { 0 }
    val sameTurnFuriten: BooleanArray = BooleanArray(4) { false }
    val riichiFuriten: BooleanArray = BooleanArray(4) { false }
    private val seats: List<SeatState> = List(4) {
        SeatState(it, scores, riichiDeclared, doubleRiichiDeclared, riichiTurn, ippatsuActive, playerKanCounts, sameTurnFuriten, riichiFuriten)
    }
    private val cheat: CheatState = CheatState()

    // === 物理牌池 ===
    val tilePool: TilePool = TilePool()

    init {
        tilePool.gameState = this
    }

    // === 席位视图代理与便利访问 ===
    val players: List<PlayerHand> = List(4) { seats[it].hand }

    // === 牌山与局况 (BoardState 委托) ===
    val wall: MutableList<Tile> get() = board.wall
    val deadWall: MutableList<Tile> get() = board.deadWall
    val deadWallReplacements: MutableList<Tile> get() = board.deadWallReplacements
    var wallPointer: Int
        get() = board.wallPointer
        set(value) { board.wallPointer = value }
    var wallEndExclusive: Int
        get() = board.wallEndExclusive
        set(value) { board.wallEndExclusive = value }
    var rinshanPointer: Int
        get() = board.rinshanPointer
        set(value) { board.rinshanPointer = value }

    var roundWind: Int
        get() = board.roundWind
        set(value) { board.roundWind = value }
    var roundNumber: Int
        get() = board.roundNumber
        set(value) { board.roundNumber = value }
    var dealerIndex: Int
        get() = board.dealerIndex
        set(value) { board.dealerIndex = value }
    var currentPlayer: Int
        get() = board.currentPlayer
        set(value) { board.currentPlayer = value }
    var turnNumber: Int
        get() = board.turnNumber
        set(value) { board.turnNumber = value }
    var phase: GamePhase
        get() = board.phase
        set(value) { board.phase = value }

    var riichiSticks: Int
        get() = board.riichiSticks
        set(value) { board.riichiSticks = value }
    var honbaSticks: Int
        get() = board.honbaSticks
        set(value) { board.honbaSticks = value }

    val doraIndicators: MutableList<Tile> get() = board.doraIndicators
    val uraDoraIndicators: MutableList<Tile> get() = board.uraDoraIndicators
    var pendingKanDoraCount: Int
        get() = board.pendingKanDoraCount
        set(value) { board.pendingKanDoraCount = value }

    var lastDiscard: Tile?
        get() = board.lastDiscard
        set(value) { board.lastDiscard = value }
    var lastDiscardPlayer: Int
        get() = board.lastDiscardPlayer
        set(value) { board.lastDiscardPlayer = value }
    var isFirstUninterruptedGo: Boolean
        get() = board.isFirstUninterruptedGo
        set(value) { board.isFirstUninterruptedGo = value }
    var isRinshanDraw: Boolean
        get() = board.isRinshanDraw
        set(value) { board.isRinshanDraw = value }
    var totalKanCount: Int
        get() = board.totalKanCount
        set(value) { board.totalKanCount = value }

    val wallRemaining: Int get() = board.wallRemaining

    fun nextPlayer(current: Int = currentPlayer): Int = board.nextPlayer(current)
    fun prevPlayer(current: Int = currentPlayer): Int = board.prevPlayer(current)
    fun acrossPlayer(current: Int = currentPlayer): Int = board.acrossPlayer(current)
    fun seatWind(player: Int): Int = board.seatWind(player)

    // === 出千系统 (CheatState 委托) ===
    val sleeveTiles: List<MutableList<Tile>> get() = cheat.sleeveTiles
    val cheatPoints: IntArray get() = cheat.cheatPoints
    val cheatEntries: MutableList<CheatLedgerEntry> get() = cheat.cheatEntries

    var sleeveBloomOffer: com.majiang.cheat.bloom.SleeveBloomOffer?
        get() = cheat.sleeveBloomOffer
        set(value) { cheat.sleeveBloomOffer = value }
    var sleeveBloomUsedOnTurn: Int
        get() = cheat.sleeveBloomUsedOnTurn
        set(value) { cheat.sleeveBloomUsedOnTurn = value }
    var lastDrawKind: com.majiang.cheat.bloom.SleeveBloomDrawKind
        get() = cheat.lastDrawKind
        set(value) { cheat.lastDrawKind = value }

    var beamSwapOffer: com.majiang.cheat.beam.BeamSwapOffer?
        get() = cheat.beamSwapOffer
        set(value) { cheat.beamSwapOffer = value }
    var beamSwapUsedOnTurn: Int
        get() = cheat.beamSwapUsedOnTurn
        set(value) { cheat.beamSwapUsedOnTurn = value }

    fun getSleeve(player: Int): MutableList<Tile> = cheat.getSleeve(player)

    // === 立直/一发唯一写入口（MR-09a） ===
    fun declareRiichi(seat: Int, turn: Int, isDouble: Boolean) {
        require(seat in 0..3) { "seat out of range: $seat" }
        seats[seat].declareRiichi(turn, isDouble)
    }

    fun setIppatsu(seat: Int, active: Boolean) {
        require(seat in 0..3) { "seat out of range: $seat" }
        seats[seat].setIppatsu(active)
    }

    fun clearAllIppatsu() {
        for (i in 0 until 4) seats[i].setIppatsu(false)
    }

    fun resetForNewRound() {
        board.resetForNewRound()
        for (seat in seats) {
            seat.resetForNewRound()
        }
        cheat.resetForNewRound()
        tilePool.initializeAll(Zone.UNASSIGNED)
    }

    fun copyFrom(other: GameState) {
        this.board.copyFrom(other.board)
        for (i in 0 until 4) {
            this.seats[i].copyFrom(other.seats[i])
        }
        this.cheat.copyFrom(other.cheat)
        this.tilePool.copyFrom(other.tilePool)
        this.tilePool.gameState = this
    }

    fun deepCopy(): GameState {
        val copy = GameState()
        copy.copyFrom(this)
        return copy
    }

    fun copyInto(target: GameState) {
        target.copyFrom(this)
    }

    fun restore(snapshot: GameState) {
        copyFrom(snapshot)
    }
}
