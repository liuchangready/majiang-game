package com.majiang.transport.api

import java.util.Collections
import java.util.LinkedHashMap
import java.util.LinkedHashSet

/**
 * 传输层独立协议花色枚举 (ProtocolSuit)
 * 纯协议 DTO，不依赖 engine 类型。
 */
enum class ProtocolSuit {
    MAN,
    PIN,
    SOU,
    WIND,
    DRAGON
}

/**
 * 传输层独立协议副露类型枚举 (ProtocolMeldType)
 */
enum class ProtocolMeldType {
    CHI,
    PON,
    OPEN_KAN,
    CLOSED_KAN,
    ADDED_KAN
}

/**
 * 传输层独立协议对局阶段枚举 (ProtocolGamePhase)
 */
enum class ProtocolGamePhase {
    STACKING,
    DEALING,
    DRAW,
    CHEAT_WINDOW,
    DISCARD,
    RESPONSE_WINDOW,
    CHANKAN_WINDOW,
    KAN_DRAW,
    ROUND_END,
    MATCH_END
}

/**
 * 传输层独立协议千术类型枚举 (ProtocolCheatTechnique)
 */
enum class ProtocolCheatTechnique {
    SLEEVE_SWAP,
    WALL_STEAL,
    DIVINE_SIGHT,
    DIVERSION
}

/**
 * 传输层独立协议流局原因枚举 (ProtocolDrawReason)
 */
enum class ProtocolDrawReason {
    EXHAUSTIVE,
    FOUR_WINDS,
    FOUR_KANS,
    FOUR_RIICHI,
    NINE_TERMINALS,
    TRIPLE_RON
}

/**
 * 传输层独立协议洗牌千术目标枚举 (ProtocolShuffleTarget)
 */
enum class ProtocolShuffleTarget {
    CHINITSU_MAN,
    CHINITSU_SOU,
    CHINITSU_PIN,
    DAISANGEN,
    KOKUSHI,
    DAISUUSHII
}

/**
 * 传输层独立协议洗牌千术准备阶段状态枚举 (ProtocolShufflePreparationStatus)
 */
enum class ProtocolShufflePreparationStatus {
    SELECTING,
    GENERATING
}

/**
 * 传输层独立协议操作回执状态枚举 (ProtocolReceiptStatus)
 */
enum class ProtocolReceiptStatus {
    APPLIED,
    RECORDED,
    REJECTED
}

/**
 * 传输层独立千术参数类型安全包装 (ProtocolParamValue)
 * 保真支持 Int, Long, Double, String, Boolean，禁止 Any 或裸 JSON 逃逸。
 */
sealed class ProtocolParamValue {
    data class IntValue(val value: Int) : ProtocolParamValue()
    data class LongValue(val value: Long) : ProtocolParamValue()
    data class DoubleValue(val value: Double) : ProtocolParamValue()
    data class StringValue(val value: String) : ProtocolParamValue()
    data class BooleanValue(val value: Boolean) : ProtocolParamValue()

    fun asInt(): Int? = (this as? IntValue)?.value
    fun asLong(): Long? = (this as? LongValue)?.value
    fun asDouble(): Double? = (this as? DoubleValue)?.value
    fun asString(): String? = (this as? StringValue)?.value
    fun asBoolean(): Boolean? = (this as? BooleanValue)?.value
}

/**
 * 传输层独立协议麻将牌 DTO (ProtocolTile)
 * 0..135 全局唯一物理编号，自洽校验物理花色、数字与赤五。
 */
data class ProtocolTile(
    val tileId: Int,
    val suit: ProtocolSuit = derivedSuit(tileId),
    val rank: Int = derivedRank(tileId)
) : Comparable<ProtocolTile> {

    init {
        require(tileId in 0..135) { "tileId must be in 0..135: $tileId" }
        val expectedSuit = derivedSuit(tileId)
        val expectedRank = derivedRank(tileId)
        require(suit == expectedSuit) {
            "Contradictory suit for tileId $tileId: expected $expectedSuit, got $suit"
        }
        require(rank == expectedRank) {
            "Contradictory rank for tileId $tileId: expected $expectedRank, got $rank"
        }
    }

    val copyIndex: Int get() = tileId % 4
    val typeIndex: Int get() = tileId / 4
    val index: Int get() = rank
    val isRed: Boolean get() = isRedFive
    val isRedFive: Boolean
        get() = rank == 5 && copyIndex == 3 &&
            (suit == ProtocolSuit.MAN || suit == ProtocolSuit.PIN || suit == ProtocolSuit.SOU)

    override fun compareTo(other: ProtocolTile): Int =
        compareValuesBy(this, other, { it.suit }, { it.rank }, { it.copyIndex })

    companion object {
        fun derivedSuit(tileId: Int): ProtocolSuit = when (tileId / 4) {
            in 0..8 -> ProtocolSuit.MAN
            in 9..17 -> ProtocolSuit.SOU
            in 18..26 -> ProtocolSuit.PIN
            in 27..30 -> ProtocolSuit.WIND
            in 31..33 -> ProtocolSuit.DRAGON
            else -> throw IllegalArgumentException("Invalid tileId: $tileId")
        }

        fun derivedRank(tileId: Int): Int = when (val t = tileId / 4) {
            in 0..8 -> t + 1
            in 9..17 -> t - 9 + 1
            in 18..26 -> t - 18 + 1
            in 27..30 -> t - 27 + 1
            in 31..33 -> t - 31 + 1
            else -> throw IllegalArgumentException("Invalid tileId: $tileId")
        }

        fun fromTypeIndex(index: Int, copyIndex: Int = 0): ProtocolTile {
            require(index in 0..33) { "Invalid typeIndex: $index" }
            require(copyIndex in 0..3) { "Invalid copyIndex: $copyIndex" }
            return ProtocolTile(index * 4 + copyIndex)
        }

        fun fromTileId(tileId: Int): ProtocolTile = ProtocolTile(tileId)
    }
}

/**
 * 传输层独立协议副露 DTO (ProtocolMeld)
 * 构造时建立不可变防御性快照。
 */
class ProtocolMeld(
    val type: ProtocolMeldType,
    tiles: List<ProtocolTile>,
    val fromPlayer: Int = 0,
    val calledTile: ProtocolTile? = null
) {
    val tiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(tiles))

    init {
        require(this.tiles.isNotEmpty()) { "tiles cannot be empty" }
        if (type == ProtocolMeldType.CLOSED_KAN) {
            require(fromPlayer in -1..3) { "fromPlayer for CLOSED_KAN must be in -1..3: $fromPlayer" }
        } else {
            require(fromPlayer in 0..3) { "fromPlayer must be in 0..3: $fromPlayer" }
        }
    }

    operator fun component1(): ProtocolMeldType = type
    operator fun component2(): List<ProtocolTile> = tiles
    operator fun component3(): Int = fromPlayer
    operator fun component4(): ProtocolTile? = calledTile

    fun copy(
        type: ProtocolMeldType = this.type,
        tiles: List<ProtocolTile> = this.tiles,
        fromPlayer: Int = this.fromPlayer,
        calledTile: ProtocolTile? = this.calledTile
    ): ProtocolMeld = ProtocolMeld(type, tiles, fromPlayer, calledTile)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolMeld) return false
        return type == other.type && tiles == other.tiles && fromPlayer == other.fromPlayer && calledTile == other.calledTile
    }

    override fun hashCode(): Int {
        var result = type.hashCode()
        result = 31 * result + tiles.hashCode()
        result = 31 * result + fromPlayer.hashCode()
        result = 31 * result + (calledTile?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "ProtocolMeld(type=$type, tiles=$tiles, fromPlayer=$fromPlayer, calledTile=$calledTile)"
}

/**
 * 传输层独立协议振听状态 DTO (ProtocolFuritenStatus)
 */
data class ProtocolFuritenStatus(
    val discard: Boolean = false,
    val temporary: Boolean = false,
    val riichi: Boolean = false
)

/**
 * 传输层独立协议合法动作 DTO (ProtocolLegalAction)
 * 带显式多态标签 actionType，集合防御性不可变。
 */
sealed class ProtocolLegalAction(val actionType: String) {
    class StartStacking(stackingPlans: List<String> = emptyList()) : ProtocolLegalAction("StartStacking") {
        val stackingPlans: List<String> = Collections.unmodifiableList(ArrayList(stackingPlans))
        operator fun component1(): List<String> = stackingPlans
        fun copy(stackingPlans: List<String> = this.stackingPlans): StartStacking = StartStacking(stackingPlans)
        override fun equals(other: Any?): Boolean = other is StartStacking && stackingPlans == other.stackingPlans
        override fun hashCode(): Int = stackingPlans.hashCode()
        override fun toString(): String = "StartStacking(stackingPlans=$stackingPlans)"
    }
    data object DealComplete : ProtocolLegalAction("DealComplete")
    data class DrawTile(val player: Int) : ProtocolLegalAction("DrawTile") {
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
    }
    data class DiscardTile(val player: Int, val tile: ProtocolTile) : ProtocolLegalAction("DiscardTile") {
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
    }
    data class DeclareChi(val player: Int, val meld: ProtocolMeld) : ProtocolLegalAction("DeclareChi") {
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
    }
    class DeclarePon(val player: Int, tiles: List<ProtocolTile>) : ProtocolLegalAction("DeclarePon") {
        val tiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(tiles))
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
        operator fun component1(): Int = player
        operator fun component2(): List<ProtocolTile> = tiles
        fun copy(player: Int = this.player, tiles: List<ProtocolTile> = this.tiles): DeclarePon = DeclarePon(player, tiles)
        override fun equals(other: Any?): Boolean = other is DeclarePon && player == other.player && tiles == other.tiles
        override fun hashCode(): Int = 31 * player.hashCode() + tiles.hashCode()
        override fun toString(): String = "DeclarePon(player=$player, tiles=$tiles)"
    }
    class DeclareKan(val player: Int, tiles: List<ProtocolTile>, val kanType: ProtocolMeldType) : ProtocolLegalAction("DeclareKan") {
        val tiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(tiles))
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
        operator fun component1(): Int = player
        operator fun component2(): List<ProtocolTile> = tiles
        operator fun component3(): ProtocolMeldType = kanType
        fun copy(player: Int = this.player, tiles: List<ProtocolTile> = this.tiles, kanType: ProtocolMeldType = this.kanType): DeclareKan = DeclareKan(player, tiles, kanType)
        override fun equals(other: Any?): Boolean = other is DeclareKan && player == other.player && tiles == other.tiles && kanType == other.kanType
        override fun hashCode(): Int = 31 * (31 * player.hashCode() + tiles.hashCode()) + kanType.hashCode()
        override fun toString(): String = "DeclareKan(player=$player, tiles=$tiles, kanType=$kanType)"
    }
    data class DeclareRiichi(val player: Int, val discardTile: ProtocolTile) : ProtocolLegalAction("DeclareRiichi") {
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
    }
    data class DeclareTsumo(val player: Int) : ProtocolLegalAction("DeclareTsumo") {
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
    }
    data class DeclareRon(val player: Int) : ProtocolLegalAction("DeclareRon") {
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
    }
    class UseCheatTechnique(
        val player: Int,
        val technique: ProtocolCheatTechnique,
        params: Map<String, ProtocolParamValue> = emptyMap()
    ) : ProtocolLegalAction("UseCheatTechnique") {
        val params: Map<String, ProtocolParamValue> = Collections.unmodifiableMap(LinkedHashMap(params))
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
        operator fun component1(): Int = player
        operator fun component2(): ProtocolCheatTechnique = technique
        operator fun component3(): Map<String, ProtocolParamValue> = params
        fun copy(player: Int = this.player, technique: ProtocolCheatTechnique = this.technique, params: Map<String, ProtocolParamValue> = this.params): UseCheatTechnique = UseCheatTechnique(player, technique, params)
        override fun equals(other: Any?): Boolean = other is UseCheatTechnique && player == other.player && technique == other.technique && params == other.params
        override fun hashCode(): Int = 31 * (31 * player.hashCode() + technique.hashCode()) + params.hashCode()
        override fun toString(): String = "UseCheatTechnique(player=$player, technique=$technique, params=$params)"
    }
    data class Pass(val player: Int) : ProtocolLegalAction("Pass") {
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
    }
    data class DeclareAbortiveDraw(val player: Int) : ProtocolLegalAction("DeclareAbortiveDraw") {
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
    }
    data class DeclareAgariYame(val player: Int) : ProtocolLegalAction("DeclareAgariYame") {
        init { require(player in 0..3) { "player must be in 0..3: $player" } }
    }
}

/**
 * 传输层独立协议动作候选选项 DTO (ProtocolActionOption)
 */
data class ProtocolActionOption(
    val optionId: String,
    val action: ProtocolLegalAction
) {
    init {
        require(optionId.isNotBlank()) { "optionId cannot be blank" }
    }
}

/**
 * 传输层独立协议玩家决策 DTO (ProtocolPlayerDecision)
 */
class ProtocolPlayerDecision(
    val roundId: String?,
    val decisionId: String,
    val controlEpoch: Long,
    options: List<ProtocolActionOption> = emptyList()
) {
    val options: List<ProtocolActionOption> = Collections.unmodifiableList(ArrayList(options))
    val actions: List<ProtocolLegalAction> get() = options.map { it.action }

    init {
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be >= 0: $controlEpoch" }
    }

    operator fun component1(): String? = roundId
    operator fun component2(): String = decisionId
    operator fun component3(): Long = controlEpoch
    operator fun component4(): List<ProtocolActionOption> = options

    fun copy(
        roundId: String? = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        options: List<ProtocolActionOption> = this.options
    ): ProtocolPlayerDecision = ProtocolPlayerDecision(roundId, decisionId, controlEpoch, options)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPlayerDecision) return false
        return roundId == other.roundId && decisionId == other.decisionId && controlEpoch == other.controlEpoch && options == other.options
    }

    override fun hashCode(): Int {
        var result = roundId?.hashCode() ?: 0
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        result = 31 * result + options.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolPlayerDecision(roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, options=$options)"
}

/**
 * 传输层独立协议动作选择提交载荷 DTO (ProtocolActionChoice)
 */
data class ProtocolActionChoice(
    val requestId: String,
    val roundId: String? = null,
    val decisionId: String,
    val controlEpoch: Long,
    val optionId: String
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        if (roundId != null) {
            require(roundId.isNotBlank()) { "roundId cannot be blank when provided" }
        }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be >= 0: $controlEpoch" }
        require(optionId.isNotBlank()) { "optionId cannot be blank" }
    }
}

/**
 * 传输层独立协议操作回执 DTO (ProtocolActionReceipt)
 */
data class ProtocolActionReceipt(
    val requestId: String,
    val status: ProtocolReceiptStatus,
    val reason: String? = null,
    val cursor: Long? = null
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        if (cursor != null) {
            require(cursor >= 0L) { "cursor must be >= 0: $cursor" }
        }
    }
}

/**
 * 传输层独立协议访问能力声明 DTO (ProtocolAccessCapabilities)
 */
class ProtocolAccessCapabilities(
    val protocolVersion: Int = 4,
    supportedPlayerCounts: Set<Int> = setOf(4),
    val remoteTransport: Boolean = false,
    val persistence: Boolean = false,
    val resumption: Boolean = true,
    val tileMarkSupported: Boolean = false
) {
    val supportedPlayerCounts: Set<Int> = Collections.unmodifiableSet(LinkedHashSet(supportedPlayerCounts))

    init {
        require(protocolVersion >= 1) { "protocolVersion must be >= 1: $protocolVersion" }
        require(this.supportedPlayerCounts.isNotEmpty()) { "supportedPlayerCounts cannot be empty" }
    }

    operator fun component1(): Int = protocolVersion
    operator fun component2(): Set<Int> = supportedPlayerCounts
    operator fun component3(): Boolean = remoteTransport
    operator fun component4(): Boolean = persistence
    operator fun component5(): Boolean = resumption
    operator fun component6(): Boolean = tileMarkSupported

    fun copy(
        protocolVersion: Int = this.protocolVersion,
        supportedPlayerCounts: Set<Int> = this.supportedPlayerCounts,
        remoteTransport: Boolean = this.remoteTransport,
        persistence: Boolean = this.persistence,
        resumption: Boolean = this.resumption,
        tileMarkSupported: Boolean = this.tileMarkSupported
    ): ProtocolAccessCapabilities = ProtocolAccessCapabilities(
        protocolVersion, supportedPlayerCounts, remoteTransport, persistence, resumption, tileMarkSupported
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolAccessCapabilities) return false
        return protocolVersion == other.protocolVersion &&
            supportedPlayerCounts == other.supportedPlayerCounts &&
            remoteTransport == other.remoteTransport &&
            persistence == other.persistence &&
            resumption == other.resumption &&
            tileMarkSupported == other.tileMarkSupported
    }

    override fun hashCode(): Int {
        var result = protocolVersion.hashCode()
        result = 31 * result + supportedPlayerCounts.hashCode()
        result = 31 * result + remoteTransport.hashCode()
        result = 31 * result + persistence.hashCode()
        result = 31 * result + resumption.hashCode()
        result = 31 * result + tileMarkSupported.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolAccessCapabilities(protocolVersion=$protocolVersion, supportedPlayerCounts=$supportedPlayerCounts, remoteTransport=$remoteTransport, persistence=$persistence, resumption=$resumption, tileMarkSupported=$tileMarkSupported)"
}

/**
 * 传输层独立协议权威时钟信息 DTO (ProtocolClock)
 */
data class ProtocolClock(
    val seat: Int,
    val opensAt: Long,
    val deadline: Long,
    val reserve: Long,
    val serverNow: Long
) {
    init {
        require(seat in 0..3) { "seat must be 0..3: $seat" }
        require(deadline >= opensAt) { "deadline must be >= opensAt: deadline=$deadline, opensAt=$opensAt" }
        require(reserve >= 0L) { "reserve must be >= 0: $reserve" }
    }
}

/**
 * 传输层独立协议席位控制者状态 DTO (ProtocolSeatControl)
 */
data class ProtocolSeatControl(
    val seat: Int,
    val connected: Boolean,
    val role: String,
    val controlEpoch: Long
) {
    init {
        require(seat in 0..3) { "seat must be 0..3: $seat" }
        require(role in VALID_ROLES) { "role must be one of $VALID_ROLES: $role" }
        require(controlEpoch >= 0L) { "controlEpoch must be >= 0: $controlEpoch" }
    }

    companion object {
        val VALID_ROLES: Set<String> = setOf("HUMAN", "COMPUTER", "TAKEOVER")
    }
}

/**
 * 传输层独立协议洗牌千术目标选择方案 DTO (ProtocolShuffleSelection)
 */
class ProtocolShuffleSelection(
    initialTargets: Set<ProtocolShuffleTarget> = emptySet(),
    drawTargets: Set<ProtocolShuffleTarget> = emptySet()
) {
    val initialTargets: Set<ProtocolShuffleTarget> = Collections.unmodifiableSet(LinkedHashSet(initialTargets))
    val drawTargets: Set<ProtocolShuffleTarget> = Collections.unmodifiableSet(LinkedHashSet(drawTargets))

    fun cost(): Int = (if (initialTargets.isNotEmpty()) 40 else 0) + (if (drawTargets.isNotEmpty()) 20 else 0)
    val isActive: Boolean get() = initialTargets.isNotEmpty() || drawTargets.isNotEmpty()

    operator fun component1(): Set<ProtocolShuffleTarget> = initialTargets
    operator fun component2(): Set<ProtocolShuffleTarget> = drawTargets

    fun copy(
        initialTargets: Set<ProtocolShuffleTarget> = this.initialTargets,
        drawTargets: Set<ProtocolShuffleTarget> = this.drawTargets
    ): ProtocolShuffleSelection = ProtocolShuffleSelection(initialTargets, drawTargets)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolShuffleSelection) return false
        return initialTargets == other.initialTargets && drawTargets == other.drawTargets
    }

    override fun hashCode(): Int = 31 * initialTargets.hashCode() + drawTargets.hashCode()

    override fun toString(): String =
        "ProtocolShuffleSelection(initialTargets=$initialTargets, drawTargets=$drawTargets)"
}

/**
 * 传输层独立协议洗牌千术准备阶段私有视图 DTO (ProtocolShufflePreparation)
 */
data class ProtocolShufflePreparation(
    val roundId: String,
    val status: ProtocolShufflePreparationStatus,
    val opensAtMillis: Long,
    val deadlineMillis: Long,
    val serverNowMillis: Long,
    val confirmed: Boolean,
    val selection: ProtocolShuffleSelection?,
    val reservedPoints: Int,
    val availablePoints: Int
) {
    init {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(deadlineMillis >= opensAtMillis) { "deadline must be >= opensAt" }
    }
}

/**
 * 传输层独立协议洗牌方案提交请求 DTO (ProtocolShufflePlan)
 */
data class ProtocolShufflePlan(
    val requestId: String,
    val roundId: String,
    val controlEpoch: Long,
    val selection: ProtocolShuffleSelection
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be >= 0: $controlEpoch" }
    }
}

/**
 * 传输层独立协议出千经济配置 DTO (ProtocolCheatEconomyConfig)
 */
class ProtocolCheatEconomyConfig(
    val enabled: Boolean = false,
    initialPoints: List<Int> = listOf(0, 0, 0, 0),
    costs: Map<String, Int> = emptyMap()
) {
    val initialPoints: List<Int> = Collections.unmodifiableList(ArrayList(initialPoints))
    val costs: Map<String, Int> = Collections.unmodifiableMap(LinkedHashMap(costs))

    init {
        require(this.initialPoints.size == 4) { "initialPoints size must be 4: ${this.initialPoints.size}" }
        require(this.initialPoints.all { it >= 0 }) { "initialPoints must be non-negative: ${this.initialPoints}" }
    }

    fun costFor(techniqueId: String): Int = costs[techniqueId] ?: 0

    operator fun component1(): Boolean = enabled
    operator fun component2(): List<Int> = initialPoints
    operator fun component3(): Map<String, Int> = costs

    fun copy(
        enabled: Boolean = this.enabled,
        initialPoints: List<Int> = this.initialPoints,
        costs: Map<String, Int> = this.costs
    ): ProtocolCheatEconomyConfig = ProtocolCheatEconomyConfig(enabled, initialPoints, costs)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolCheatEconomyConfig) return false
        return enabled == other.enabled && initialPoints == other.initialPoints && costs == other.costs
    }

    override fun hashCode(): Int {
        var result = enabled.hashCode()
        result = 31 * result + initialPoints.hashCode()
        result = 31 * result + costs.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolCheatEconomyConfig(enabled=$enabled, initialPoints=$initialPoints, costs=$costs)"
}

/**
 * 传输层独立协议洗牌配置 DTO (ProtocolShuffleConfig)
 */
data class ProtocolShuffleConfig(
    val enabled: Boolean = false,
    val selectionMillis: Long = 10000L,
    val plannedDraws: Int = 8,
    val strength: Double = 0.75,
    val interference: Double = 0.65,
    val aiMaySpend: Boolean = false
) {
    init {
        require(selectionMillis > 0L) { "selectionMillis must be positive: $selectionMillis" }
        require(plannedDraws in 0..18) { "plannedDraws must be in 0..18: $plannedDraws" }
        require(!strength.isNaN() && strength in 0.0..1.0) { "strength must be in 0.0..1.0: $strength" }
        require(!interference.isNaN() && !interference.isInfinite() && interference >= 0.0) {
            "interference must be finite and >= 0.0: $interference"
        }
    }
}

/**
 * 传输层独立协议比赛配置 DTO (ProtocolMatchConfig)
 * 严格保留 13 维全量配置，公开集合建立不可变防御性副本。
 */
class ProtocolMatchConfig(
    val playerCount: Int = 4,
    val handsPerWind: Int = 4,
    winds: List<Int> = listOf(1, 2),
    startingScores: List<Int> = listOf(25000, 25000, 25000, 25000),
    val allowExtension: Boolean = true,
    val allowAgariYame: Boolean = true,
    val bankruptcyEnabled: Boolean = true,
    val bankruptcyThreshold: Int = 0,
    val bankruptcyInclusive: Boolean = false,
    val targetScore: Int = 30000,
    tieBreakSeats: List<Int> = listOf(0, 1, 2, 3),
    val cheatEconomy: ProtocolCheatEconomyConfig = ProtocolCheatEconomyConfig(),
    val shuffleConfig: ProtocolShuffleConfig = ProtocolShuffleConfig(),
    val tileMarkConfig: ProtocolTileMarkConfig = ProtocolTileMarkConfig()
) {
    val winds: List<Int> = Collections.unmodifiableList(ArrayList(winds))
    val startingScores: List<Int> = Collections.unmodifiableList(ArrayList(startingScores))
    val tieBreakSeats: List<Int> = Collections.unmodifiableList(ArrayList(tieBreakSeats))

    init {
        require(playerCount == 4) { "playerCount must be 4: $playerCount" }
        require(handsPerWind in 1..4) { "handsPerWind must be in 1..4: $handsPerWind" }
        require(this.winds.isNotEmpty()) { "winds cannot be empty" }
        require(this.startingScores.size == playerCount) { "startingScores size must match playerCount" }
        require(this.startingScores.all { it >= 0 }) { "startingScores must be non-negative: ${this.startingScores}" }
        require(this.tieBreakSeats.size == playerCount) { "tieBreakSeats size must match playerCount" }
        require(this.tieBreakSeats.toSet().size == playerCount) { "tieBreakSeats must not contain duplicates" }
        require(targetScore >= 0) { "targetScore must be non-negative: $targetScore" }
        if (tileMarkConfig.enabled) {
            require(cheatEconomy.enabled) { "cheatEconomy must be enabled when tileMarkConfig is enabled" }
            require(cheatEconomy.costs["TILE_MARK"] == 10) {
                "TILE_MARK cost must be explicitly 10 when tileMarkConfig is enabled, got: ${cheatEconomy.costs["TILE_MARK"]}"
            }
        }
    }

    operator fun component1(): Int = playerCount
    operator fun component2(): Int = handsPerWind
    operator fun component3(): List<Int> = winds
    operator fun component4(): List<Int> = startingScores
    operator fun component5(): Boolean = allowExtension
    operator fun component6(): Boolean = allowAgariYame
    operator fun component7(): Boolean = bankruptcyEnabled
    operator fun component8(): Int = bankruptcyThreshold
    operator fun component9(): Boolean = bankruptcyInclusive
    operator fun component10(): Int = targetScore
    operator fun component11(): List<Int> = tieBreakSeats
    operator fun component12(): ProtocolCheatEconomyConfig = cheatEconomy
    operator fun component13(): ProtocolShuffleConfig = shuffleConfig
    operator fun component14(): ProtocolTileMarkConfig = tileMarkConfig

    fun copy(
        playerCount: Int = this.playerCount,
        handsPerWind: Int = this.handsPerWind,
        winds: List<Int> = this.winds,
        startingScores: List<Int> = this.startingScores,
        allowExtension: Boolean = this.allowExtension,
        allowAgariYame: Boolean = this.allowAgariYame,
        bankruptcyEnabled: Boolean = this.bankruptcyEnabled,
        bankruptcyThreshold: Int = this.bankruptcyThreshold,
        bankruptcyInclusive: Boolean = this.bankruptcyInclusive,
        targetScore: Int = this.targetScore,
        tieBreakSeats: List<Int> = this.tieBreakSeats,
        cheatEconomy: ProtocolCheatEconomyConfig = this.cheatEconomy,
        shuffleConfig: ProtocolShuffleConfig = this.shuffleConfig,
        tileMarkConfig: ProtocolTileMarkConfig = this.tileMarkConfig
    ): ProtocolMatchConfig = ProtocolMatchConfig(
        playerCount, handsPerWind, winds, startingScores,
        allowExtension, allowAgariYame, bankruptcyEnabled, bankruptcyThreshold,
        bankruptcyInclusive, targetScore, tieBreakSeats, cheatEconomy, shuffleConfig, tileMarkConfig
    )

    fun deepCopy(): ProtocolMatchConfig = copy(
        winds = ArrayList(winds),
        startingScores = ArrayList(startingScores),
        tieBreakSeats = ArrayList(tieBreakSeats)
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolMatchConfig) return false
        return playerCount == other.playerCount &&
            handsPerWind == other.handsPerWind &&
            winds == other.winds &&
            startingScores == other.startingScores &&
            allowExtension == other.allowExtension &&
            allowAgariYame == other.allowAgariYame &&
            bankruptcyEnabled == other.bankruptcyEnabled &&
            bankruptcyThreshold == other.bankruptcyThreshold &&
            bankruptcyInclusive == other.bankruptcyInclusive &&
            targetScore == other.targetScore &&
            tieBreakSeats == other.tieBreakSeats &&
            cheatEconomy == other.cheatEconomy &&
            shuffleConfig == other.shuffleConfig &&
            tileMarkConfig == other.tileMarkConfig
    }

    override fun hashCode(): Int {
        var result = playerCount.hashCode()
        result = 31 * result + handsPerWind.hashCode()
        result = 31 * result + winds.hashCode()
        result = 31 * result + startingScores.hashCode()
        result = 31 * result + allowExtension.hashCode()
        result = 31 * result + allowAgariYame.hashCode()
        result = 31 * result + bankruptcyEnabled.hashCode()
        result = 31 * result + bankruptcyThreshold.hashCode()
        result = 31 * result + bankruptcyInclusive.hashCode()
        result = 31 * result + targetScore.hashCode()
        result = 31 * result + tieBreakSeats.hashCode()
        result = 31 * result + cheatEconomy.hashCode()
        result = 31 * result + shuffleConfig.hashCode()
        result = 31 * result + tileMarkConfig.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolMatchConfig(playerCount=$playerCount, handsPerWind=$handsPerWind, winds=$winds, startingScores=$startingScores, allowExtension=$allowExtension, allowAgariYame=$allowAgariYame, bankruptcyEnabled=$bankruptcyEnabled, bankruptcyThreshold=$bankruptcyThreshold, bankruptcyInclusive=$bankruptcyInclusive, targetScore=$targetScore, tieBreakSeats=$tieBreakSeats, cheatEconomy=$cheatEconomy, shuffleConfig=$shuffleConfig, tileMarkConfig=$tileMarkConfig)"
}

/**
 * 传输层独立协议符数明细行 DTO (ProtocolFuLine)
 */
data class ProtocolFuLine(
    val description: String,
    val fu: Int
)

/**
 * 传输层独立协议符数推导 DTO (ProtocolFuBreakdown)
 */
class ProtocolFuBreakdown(
    lines: List<ProtocolFuLine>,
    val subtotal: Int,
    val roundedUpTo: Int,
    val finalFu: Int,
    val specialNote: String? = null
) {
    val lines: List<ProtocolFuLine> = Collections.unmodifiableList(ArrayList(lines))

    operator fun component1(): List<ProtocolFuLine> = lines
    operator fun component2(): Int = subtotal
    operator fun component3(): Int = roundedUpTo
    operator fun component4(): Int = finalFu
    operator fun component5(): String? = specialNote

    fun copy(
        lines: List<ProtocolFuLine> = this.lines,
        subtotal: Int = this.subtotal,
        roundedUpTo: Int = this.roundedUpTo,
        finalFu: Int = this.finalFu,
        specialNote: String? = this.specialNote
    ): ProtocolFuBreakdown = ProtocolFuBreakdown(lines, subtotal, roundedUpTo, finalFu, specialNote)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolFuBreakdown) return false
        return lines == other.lines && subtotal == other.subtotal && roundedUpTo == other.roundedUpTo &&
            finalFu == other.finalFu && specialNote == other.specialNote
    }

    override fun hashCode(): Int {
        var result = lines.hashCode()
        result = 31 * result + subtotal.hashCode()
        result = 31 * result + roundedUpTo.hashCode()
        result = 31 * result + finalFu.hashCode()
        result = 31 * result + (specialNote?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "ProtocolFuBreakdown(lines=$lines, subtotal=$subtotal, roundedUpTo=$roundedUpTo, finalFu=$finalFu, specialNote=$specialNote)"
}

/**
 * 传输层独立协议宝牌指示牌命中明细 DTO (ProtocolDoraIndicatorHit)
 */
class ProtocolDoraIndicatorHit(
    val indicator: ProtocolTile,
    val target: ProtocolTile,
    tiles: List<ProtocolTile>
) {
    val tiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(tiles))

    operator fun component1(): ProtocolTile = indicator
    operator fun component2(): ProtocolTile = target
    operator fun component3(): List<ProtocolTile> = tiles

    fun copy(
        indicator: ProtocolTile = this.indicator,
        target: ProtocolTile = this.target,
        tiles: List<ProtocolTile> = this.tiles
    ): ProtocolDoraIndicatorHit = ProtocolDoraIndicatorHit(indicator, target, tiles)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolDoraIndicatorHit) return false
        return indicator == other.indicator && target == other.target && tiles == other.tiles
    }

    override fun hashCode(): Int {
        var result = indicator.hashCode()
        result = 31 * result + target.hashCode()
        result = 31 * result + tiles.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolDoraIndicatorHit(indicator=$indicator, target=$target, tiles=$tiles)"
}

/**
 * 传输层独立协议宝牌推导明细 DTO (ProtocolDoraBreakdown)
 */
class ProtocolDoraBreakdown(
    val indicatorCount: Int,
    val uraCount: Int,
    val redCount: Int,
    val totalHan: Int,
    indicatorTiles: List<ProtocolTile>,
    uraTiles: List<ProtocolTile>,
    redTiles: List<ProtocolTile>,
    indicatorHits: List<ProtocolDoraIndicatorHit>,
    uraHits: List<ProtocolDoraIndicatorHit>
) {
    val indicatorTiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(indicatorTiles))
    val uraTiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(uraTiles))
    val redTiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(redTiles))
    val indicatorHits: List<ProtocolDoraIndicatorHit> = Collections.unmodifiableList(ArrayList(indicatorHits))
    val uraHits: List<ProtocolDoraIndicatorHit> = Collections.unmodifiableList(ArrayList(uraHits))

    operator fun component1(): Int = indicatorCount
    operator fun component2(): Int = uraCount
    operator fun component3(): Int = redCount
    operator fun component4(): Int = totalHan
    operator fun component5(): List<ProtocolTile> = indicatorTiles
    operator fun component6(): List<ProtocolTile> = uraTiles
    operator fun component7(): List<ProtocolTile> = redTiles
    operator fun component8(): List<ProtocolDoraIndicatorHit> = indicatorHits
    operator fun component9(): List<ProtocolDoraIndicatorHit> = uraHits

    fun copy(
        indicatorCount: Int = this.indicatorCount,
        uraCount: Int = this.uraCount,
        redCount: Int = this.redCount,
        totalHan: Int = this.totalHan,
        indicatorTiles: List<ProtocolTile> = this.indicatorTiles,
        uraTiles: List<ProtocolTile> = this.uraTiles,
        redTiles: List<ProtocolTile> = this.redTiles,
        indicatorHits: List<ProtocolDoraIndicatorHit> = this.indicatorHits,
        uraHits: List<ProtocolDoraIndicatorHit> = this.uraHits
    ): ProtocolDoraBreakdown = ProtocolDoraBreakdown(
        indicatorCount, uraCount, redCount, totalHan,
        indicatorTiles, uraTiles, redTiles, indicatorHits, uraHits
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolDoraBreakdown) return false
        return indicatorCount == other.indicatorCount &&
            uraCount == other.uraCount &&
            redCount == other.redCount &&
            totalHan == other.totalHan &&
            indicatorTiles == other.indicatorTiles &&
            uraTiles == other.uraTiles &&
            redTiles == other.redTiles &&
            indicatorHits == other.indicatorHits &&
            uraHits == other.uraHits
    }

    override fun hashCode(): Int {
        var result = indicatorCount.hashCode()
        result = 31 * result + uraCount.hashCode()
        result = 31 * result + redCount.hashCode()
        result = 31 * result + totalHan.hashCode()
        result = 31 * result + indicatorTiles.hashCode()
        result = 31 * result + uraTiles.hashCode()
        result = 31 * result + redTiles.hashCode()
        result = 31 * result + indicatorHits.hashCode()
        result = 31 * result + uraHits.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolDoraBreakdown(indicatorCount=$indicatorCount, uraCount=$uraCount, redCount=$redCount, totalHan=$totalHan, indicatorTiles=$indicatorTiles, uraTiles=$uraTiles, redTiles=$redTiles, indicatorHits=$indicatorHits, uraHits=$uraHits)"
}

/**
 * 传输层独立协议算分结果 DTO (ProtocolScoreResult)
 */
data class ProtocolScoreResult(
    val fu: Int,
    val han: Int,
    val basePoints: Int,
    val title: String,
    val isYakuman: Boolean,
    val yakumanMultiplier: Int,
    val rawBasePoints: Int = basePoints,
    val capReason: String? = null,
    val formulaText: String = "",
    val rawBasePointsExact: String = rawBasePoints.toString()
)

/**
 * 传输层独立协议完整结算推导明细 DTO (ProtocolScoreBreakdown)
 */
class ProtocolScoreBreakdown(
    yakuLines: List<Pair<String, Int>>,
    val yakumanCount: Int,
    val dora: ProtocolDoraBreakdown,
    val fu: ProtocolFuBreakdown,
    val score: ProtocolScoreResult,
    paymentLines: List<String>,
    val honbaAddition: Int,
    val riichiAddition: Int,
    val totalPoints: Int,
    val uraEligible: Boolean,
    val isDealer: Boolean,
    val isTsumo: Boolean
) {
    val yakuLines: List<Pair<String, Int>> = Collections.unmodifiableList(ArrayList(yakuLines))
    val paymentLines: List<String> = Collections.unmodifiableList(ArrayList(paymentLines))

    operator fun component1(): List<Pair<String, Int>> = yakuLines
    operator fun component2(): Int = yakumanCount
    operator fun component3(): ProtocolDoraBreakdown = dora
    operator fun component4(): ProtocolFuBreakdown = fu
    operator fun component5(): ProtocolScoreResult = score
    operator fun component6(): List<String> = paymentLines
    operator fun component7(): Int = honbaAddition
    operator fun component8(): Int = riichiAddition
    operator fun component9(): Int = totalPoints
    operator fun component10(): Boolean = uraEligible
    operator fun component11(): Boolean = isDealer
    operator fun component12(): Boolean = isTsumo

    fun copy(
        yakuLines: List<Pair<String, Int>> = this.yakuLines,
        yakumanCount: Int = this.yakumanCount,
        dora: ProtocolDoraBreakdown = this.dora,
        fu: ProtocolFuBreakdown = this.fu,
        score: ProtocolScoreResult = this.score,
        paymentLines: List<String> = this.paymentLines,
        honbaAddition: Int = this.honbaAddition,
        riichiAddition: Int = this.riichiAddition,
        totalPoints: Int = this.totalPoints,
        uraEligible: Boolean = this.uraEligible,
        isDealer: Boolean = this.isDealer,
        isTsumo: Boolean = this.isTsumo
    ): ProtocolScoreBreakdown = ProtocolScoreBreakdown(
        yakuLines, yakumanCount, dora, fu, score, paymentLines,
        honbaAddition, riichiAddition, totalPoints, uraEligible, isDealer, isTsumo
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolScoreBreakdown) return false
        return yakuLines == other.yakuLines &&
            yakumanCount == other.yakumanCount &&
            dora == other.dora &&
            fu == other.fu &&
            score == other.score &&
            paymentLines == other.paymentLines &&
            honbaAddition == other.honbaAddition &&
            riichiAddition == other.riichiAddition &&
            totalPoints == other.totalPoints &&
            uraEligible == other.uraEligible &&
            isDealer == other.isDealer &&
            isTsumo == other.isTsumo
    }

    override fun hashCode(): Int {
        var result = yakuLines.hashCode()
        result = 31 * result + yakumanCount.hashCode()
        result = 31 * result + dora.hashCode()
        result = 31 * result + fu.hashCode()
        result = 31 * result + score.hashCode()
        result = 31 * result + paymentLines.hashCode()
        result = 31 * result + honbaAddition.hashCode()
        result = 31 * result + riichiAddition.hashCode()
        result = 31 * result + totalPoints.hashCode()
        result = 31 * result + uraEligible.hashCode()
        result = 31 * result + isDealer.hashCode()
        result = 31 * result + isTsumo.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolScoreBreakdown(yakuLines=$yakuLines, yakumanCount=$yakumanCount, dora=$dora, fu=$fu, score=$score, paymentLines=$paymentLines, honbaAddition=$honbaAddition, riichiAddition=$riichiAddition, totalPoints=$totalPoints, uraEligible=$uraEligible, isDealer=$isDealer, isTsumo=$isTsumo)"
}

/**
 * 传输层独立协议公开单局历史结算记录 DTO (ProtocolPublicRoundRecord)
 */
class ProtocolPublicRoundRecord(
    val roundId: String,
    val resultId: String,
    finalScores: List<Int>,
    deltas: List<Int>,
    finalCheatPoints: List<Int>,
    cheatDeltas: List<Int>
) {
    val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
    val deltas: List<Int> = Collections.unmodifiableList(ArrayList(deltas))
    val finalCheatPoints: List<Int> = Collections.unmodifiableList(ArrayList(finalCheatPoints))
    val cheatDeltas: List<Int> = Collections.unmodifiableList(ArrayList(cheatDeltas))

    init {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(resultId.isNotBlank()) { "resultId cannot be blank" }
    }

    operator fun component1(): String = roundId
    operator fun component2(): String = resultId
    operator fun component3(): List<Int> = finalScores
    operator fun component4(): List<Int> = deltas
    operator fun component5(): List<Int> = finalCheatPoints
    operator fun component6(): List<Int> = cheatDeltas

    fun copy(
        roundId: String = this.roundId,
        resultId: String = this.resultId,
        finalScores: List<Int> = this.finalScores,
        deltas: List<Int> = this.deltas,
        finalCheatPoints: List<Int> = this.finalCheatPoints,
        cheatDeltas: List<Int> = this.cheatDeltas
    ): ProtocolPublicRoundRecord = ProtocolPublicRoundRecord(
        roundId, resultId, finalScores, deltas, finalCheatPoints, cheatDeltas
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPublicRoundRecord) return false
        return roundId == other.roundId && resultId == other.resultId &&
            finalScores == other.finalScores && deltas == other.deltas &&
            finalCheatPoints == other.finalCheatPoints && cheatDeltas == other.cheatDeltas
    }

    override fun hashCode(): Int {
        var result = roundId.hashCode()
        result = 31 * result + resultId.hashCode()
        result = 31 * result + finalScores.hashCode()
        result = 31 * result + deltas.hashCode()
        result = 31 * result + finalCheatPoints.hashCode()
        result = 31 * result + cheatDeltas.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolPublicRoundRecord(roundId=$roundId, resultId=$resultId, finalScores=$finalScores, deltas=$deltas, finalCheatPoints=$finalCheatPoints, cheatDeltas=$cheatDeltas)"
}

/**
 * 传输层独立协议公开整场终局结果 DTO (ProtocolPublicSessionResult)
 */
class ProtocolPublicSessionResult(
    finalScores: List<Int>,
    rankedSeats: List<Int>,
    val reason: String,
    finalCheatPoints: List<Int> = emptyList()
) {
    val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
    val rankedSeats: List<Int> = Collections.unmodifiableList(ArrayList(rankedSeats))
    val finalCheatPoints: List<Int> = Collections.unmodifiableList(ArrayList(finalCheatPoints))

    init {
        require(reason.isNotBlank()) { "reason cannot be blank" }
    }

    operator fun component1(): List<Int> = finalScores
    operator fun component2(): List<Int> = rankedSeats
    operator fun component3(): String = reason
    operator fun component4(): List<Int> = finalCheatPoints

    fun copy(
        finalScores: List<Int> = this.finalScores,
        rankedSeats: List<Int> = this.rankedSeats,
        reason: String = this.reason,
        finalCheatPoints: List<Int> = this.finalCheatPoints
    ): ProtocolPublicSessionResult = ProtocolPublicSessionResult(
        finalScores, rankedSeats, reason, finalCheatPoints
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPublicSessionResult) return false
        return finalScores == other.finalScores && rankedSeats == other.rankedSeats &&
            reason == other.reason && finalCheatPoints == other.finalCheatPoints
    }

    override fun hashCode(): Int {
        var result = finalScores.hashCode()
        result = 31 * result + rankedSeats.hashCode()
        result = 31 * result + reason.hashCode()
        result = 31 * result + finalCheatPoints.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolPublicSessionResult(finalScores=$finalScores, rankedSeats=$rankedSeats, reason=$reason, finalCheatPoints=$finalCheatPoints)"
}

/**
 * 传输层独立协议整场赛程进度 DTO (ProtocolSessionView)
 */
class ProtocolSessionView(
    val lifecycle: String,
    scores: List<Int>,
    val roundWind: Int,
    val roundNumber: Int,
    val dealerSeat: Int,
    val honba: Int,
    roundRecords: List<ProtocolPublicRoundRecord> = emptyList(),
    val finalResult: ProtocolPublicSessionResult? = null
) {
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val roundRecords: List<ProtocolPublicRoundRecord> = Collections.unmodifiableList(ArrayList(roundRecords))

    init {
        require(lifecycle.isNotBlank()) { "lifecycle cannot be blank" }
        require(dealerSeat in 0..3) { "dealerSeat must be in 0..3: $dealerSeat" }
        require(honba >= 0) { "honba must be >= 0: $honba" }
    }

    operator fun component1(): String = lifecycle
    operator fun component2(): List<Int> = scores
    operator fun component3(): Int = roundWind
    operator fun component4(): Int = roundNumber
    operator fun component5(): Int = dealerSeat
    operator fun component6(): Int = honba
    operator fun component7(): List<ProtocolPublicRoundRecord> = roundRecords
    operator fun component8(): ProtocolPublicSessionResult? = finalResult

    fun copy(
        lifecycle: String = this.lifecycle,
        scores: List<Int> = this.scores,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        dealerSeat: Int = this.dealerSeat,
        honba: Int = this.honba,
        roundRecords: List<ProtocolPublicRoundRecord> = this.roundRecords,
        finalResult: ProtocolPublicSessionResult? = this.finalResult
    ): ProtocolSessionView = ProtocolSessionView(
        lifecycle, scores, roundWind, roundNumber, dealerSeat, honba, roundRecords, finalResult
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolSessionView) return false
        return lifecycle == other.lifecycle && scores == other.scores &&
            roundWind == other.roundWind && roundNumber == other.roundNumber &&
            dealerSeat == other.dealerSeat && honba == other.honba &&
            roundRecords == other.roundRecords && finalResult == other.finalResult
    }

    override fun hashCode(): Int {
        var result = lifecycle.hashCode()
        result = 31 * result + scores.hashCode()
        result = 31 * result + roundWind.hashCode()
        result = 31 * result + roundNumber.hashCode()
        result = 31 * result + dealerSeat.hashCode()
        result = 31 * result + honba.hashCode()
        result = 31 * result + roundRecords.hashCode()
        result = 31 * result + (finalResult?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "ProtocolSessionView(lifecycle=$lifecycle, scores=$scores, roundWind=$roundWind, roundNumber=$roundNumber, dealerSeat=$dealerSeat, honba=$honba, roundRecords=$roundRecords, finalResult=$finalResult)"
}

/**
 * 传输层独立协议游戏事件 DTO (ProtocolGameEvent)
 * 对应 engine 全部 23 种事件，带显式多态标签 eventType。
 */
sealed class ProtocolGameEvent(val eventType: String) {
    abstract val turnNumber: Int

    data class StackingStarted(override val turnNumber: Int = 0) : ProtocolGameEvent("StackingStarted")
    data class WallBuilt(override val turnNumber: Int = 0, val wallSize: Int = 136) : ProtocolGameEvent("WallBuilt")
    data class TileDealt(override val turnNumber: Int, val player: Int, val tile: ProtocolTile) : ProtocolGameEvent("TileDealt")
    data class DealingComplete(override val turnNumber: Int = 0) : ProtocolGameEvent("DealingComplete")
    data class TileDrawn(override val turnNumber: Int, val player: Int, val tile: ProtocolTile, val wallRemaining: Int) : ProtocolGameEvent("TileDrawn")
    data class TileDiscarded(
        override val turnNumber: Int,
        val player: Int,
        val tile: ProtocolTile,
        val isRiichiDiscard: Boolean = false,
        val isTsumogiri: Boolean = false
    ) : ProtocolGameEvent("TileDiscarded")
    data class ChiDeclared(override val turnNumber: Int, val player: Int, val meld: ProtocolMeld, val fromPlayer: Int) : ProtocolGameEvent("ChiDeclared")
    data class PonDeclared(override val turnNumber: Int, val player: Int, val meld: ProtocolMeld, val fromPlayer: Int) : ProtocolGameEvent("PonDeclared")
    data class KanDeclared(override val turnNumber: Int, val player: Int, val meld: ProtocolMeld, val kanType: ProtocolMeldType) : ProtocolGameEvent("KanDeclared")
    data class RinshanDraw(override val turnNumber: Int, val player: Int, val tile: ProtocolTile) : ProtocolGameEvent("RinshanDraw")
    data class RiichiDeclared(
        override val turnNumber: Int,
        val player: Int,
        val discardTile: ProtocolTile,
        val isDoubleRiichi: Boolean = false
    ) : ProtocolGameEvent("RiichiDeclared")
    class TsumoWin(
        override val turnNumber: Int,
        val player: Int,
        val winTile: ProtocolTile,
        yakuList: List<String>,
        val han: Int,
        val fu: Int,
        val score: Int,
        val breakdown: ProtocolScoreBreakdown? = null
    ) : ProtocolGameEvent("TsumoWin") {
        val yakuList: List<String> = Collections.unmodifiableList(ArrayList(yakuList))
        operator fun component1(): Int = turnNumber
        operator fun component2(): Int = player
        operator fun component3(): ProtocolTile = winTile
        operator fun component4(): List<String> = yakuList
        operator fun component5(): Int = han
        operator fun component6(): Int = fu
        operator fun component7(): Int = score
        operator fun component8(): ProtocolScoreBreakdown? = breakdown

        fun copy(
            turnNumber: Int = this.turnNumber,
            player: Int = this.player,
            winTile: ProtocolTile = this.winTile,
            yakuList: List<String> = this.yakuList,
            han: Int = this.han,
            fu: Int = this.fu,
            score: Int = this.score,
            breakdown: ProtocolScoreBreakdown? = this.breakdown
        ): TsumoWin = TsumoWin(turnNumber, player, winTile, yakuList, han, fu, score, breakdown)

        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is TsumoWin) return false
            return turnNumber == other.turnNumber && player == other.player &&
                winTile == other.winTile && yakuList == other.yakuList &&
                han == other.han && fu == other.fu && score == other.score && breakdown == other.breakdown
        }

        override fun hashCode(): Int {
            var result = turnNumber.hashCode()
            result = 31 * result + player.hashCode()
            result = 31 * result + winTile.hashCode()
            result = 31 * result + yakuList.hashCode()
            result = 31 * result + han.hashCode()
            result = 31 * result + fu.hashCode()
            result = 31 * result + score.hashCode()
            result = 31 * result + (breakdown?.hashCode() ?: 0)
            return result
        }

        override fun toString(): String =
            "TsumoWin(turnNumber=$turnNumber, player=$player, winTile=$winTile, yakuList=$yakuList, han=$han, fu=$fu, score=$score, breakdown=$breakdown)"
    }
    class RonWin(
        override val turnNumber: Int,
        val winner: Int,
        val loser: Int,
        val winTile: ProtocolTile,
        yakuList: List<String>,
        val han: Int,
        val fu: Int,
        val score: Int,
        val breakdown: ProtocolScoreBreakdown? = null
    ) : ProtocolGameEvent("RonWin") {
        val yakuList: List<String> = Collections.unmodifiableList(ArrayList(yakuList))
        operator fun component1(): Int = turnNumber
        operator fun component2(): Int = winner
        operator fun component3(): Int = loser
        operator fun component4(): ProtocolTile = winTile
        operator fun component5(): List<String> = yakuList
        operator fun component6(): Int = han
        operator fun component7(): Int = fu
        operator fun component8(): Int = score
        operator fun component9(): ProtocolScoreBreakdown? = breakdown

        fun copy(
            turnNumber: Int = this.turnNumber,
            winner: Int = this.winner,
            loser: Int = this.loser,
            winTile: ProtocolTile = this.winTile,
            yakuList: List<String> = this.yakuList,
            han: Int = this.han,
            fu: Int = this.fu,
            score: Int = this.score,
            breakdown: ProtocolScoreBreakdown? = this.breakdown
        ): RonWin = RonWin(turnNumber, winner, loser, winTile, yakuList, han, fu, score, breakdown)

        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is RonWin) return false
            return turnNumber == other.turnNumber && winner == other.winner && loser == other.loser &&
                winTile == other.winTile && yakuList == other.yakuList &&
                han == other.han && fu == other.fu && score == other.score && breakdown == other.breakdown
        }

        override fun hashCode(): Int {
            var result = turnNumber.hashCode()
            result = 31 * result + winner.hashCode()
            result = 31 * result + loser.hashCode()
            result = 31 * result + winTile.hashCode()
            result = 31 * result + yakuList.hashCode()
            result = 31 * result + han.hashCode()
            result = 31 * result + fu.hashCode()
            result = 31 * result + score.hashCode()
            result = 31 * result + (breakdown?.hashCode() ?: 0)
            return result
        }

        override fun toString(): String =
            "RonWin(turnNumber=$turnNumber, winner=$winner, loser=$loser, winTile=$winTile, yakuList=$yakuList, han=$han, fu=$fu, score=$score, breakdown=$breakdown)"
    }
    data class DoraRevealed(override val turnNumber: Int, val indicator: ProtocolTile, val isUraDora: Boolean = false) : ProtocolGameEvent("DoraRevealed")
    data class CheatAttempted(
        override val turnNumber: Int,
        val player: Int,
        val technique: ProtocolCheatTechnique,
        val success: Boolean
    ) : ProtocolGameEvent("CheatAttempted")
    data class SuspicionChanged(
        override val turnNumber: Int,
        val target: Int,
        val observer: Int,
        val oldValue: Float,
        val newValue: Float
    ) : ProtocolGameEvent("SuspicionChanged")
    data class BustedEvent(
        override val turnNumber: Int,
        val player: Int,
        val caughtBy: Int,
        val technique: ProtocolCheatTechnique
    ) : ProtocolGameEvent("BustedEvent")
    data class GazeShifted(override val turnNumber: Int, val observer: Int, val newTarget: Int?, val oldTarget: Int?) : ProtocolGameEvent("GazeShifted")
    data class CheatPointsChanged(override val turnNumber: Int, val seat: Int, val amount: Int, val newBalance: Int) : ProtocolGameEvent("CheatPointsChanged")
    class DrawGame(
        override val turnNumber: Int,
        val reason: ProtocolDrawReason,
        tenpaiPlayers: List<Int>
    ) : ProtocolGameEvent("DrawGame") {
        val tenpaiPlayers: List<Int> = Collections.unmodifiableList(ArrayList(tenpaiPlayers))
        operator fun component1(): Int = turnNumber
        operator fun component2(): ProtocolDrawReason = reason
        operator fun component3(): List<Int> = tenpaiPlayers
        fun copy(
            turnNumber: Int = this.turnNumber,
            reason: ProtocolDrawReason = this.reason,
            tenpaiPlayers: List<Int> = this.tenpaiPlayers
        ): DrawGame = DrawGame(turnNumber, reason, tenpaiPlayers)

        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is DrawGame) return false
            return turnNumber == other.turnNumber && reason == other.reason && tenpaiPlayers == other.tenpaiPlayers
        }

        override fun hashCode(): Int = 31 * (31 * turnNumber.hashCode() + reason.hashCode()) + tenpaiPlayers.hashCode()
        override fun toString(): String = "DrawGame(turnNumber=$turnNumber, reason=$reason, tenpaiPlayers=$tenpaiPlayers)"
    }
    class ScoreSettled(
        override val turnNumber: Int,
        payments: Map<Int, Int>,
        newScores: List<Int>
    ) : ProtocolGameEvent("ScoreSettled") {
        val payments: Map<Int, Int> = Collections.unmodifiableMap(LinkedHashMap(payments))
        val newScores: List<Int> = Collections.unmodifiableList(ArrayList(newScores))
        operator fun component1(): Int = turnNumber
        operator fun component2(): Map<Int, Int> = payments
        operator fun component3(): List<Int> = newScores
        fun copy(
            turnNumber: Int = this.turnNumber,
            payments: Map<Int, Int> = this.payments,
            newScores: List<Int> = this.newScores
        ): ScoreSettled = ScoreSettled(turnNumber, payments, newScores)

        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is ScoreSettled) return false
            return turnNumber == other.turnNumber && payments == other.payments && newScores == other.newScores
        }

        override fun hashCode(): Int = 31 * (31 * turnNumber.hashCode() + payments.hashCode()) + newScores.hashCode()
        override fun toString(): String = "ScoreSettled(turnNumber=$turnNumber, payments=$payments, newScores=$newScores)"
    }
    data class RoundEnded(override val turnNumber: Int, val roundWind: Int, val roundNumber: Int) : ProtocolGameEvent("RoundEnded")
    class MatchEnded(
        override val turnNumber: Int,
        finalScores: List<Int>,
        val winner: Int
    ) : ProtocolGameEvent("MatchEnded") {
        val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
        operator fun component1(): Int = turnNumber
        operator fun component2(): List<Int> = finalScores
        operator fun component3(): Int = winner
        fun copy(
            turnNumber: Int = this.turnNumber,
            finalScores: List<Int> = this.finalScores,
            winner: Int = this.winner
        ): MatchEnded = MatchEnded(turnNumber, finalScores, winner)

        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is MatchEnded) return false
            return turnNumber == other.turnNumber && finalScores == other.finalScores && winner == other.winner
        }

        override fun hashCode(): Int = 31 * (31 * turnNumber.hashCode() + finalScores.hashCode()) + winner.hashCode()
        override fun toString(): String = "MatchEnded(turnNumber=$turnNumber, finalScores=$finalScores, winner=$winner)"
    }
}

/**
 * 传输层独立协议观察者玩家座位的 PlayerView DTO (ProtocolPlayerView)
 */
class ProtocolPlayerView(
    val viewerSeat: Int,
    val phase: ProtocolGamePhase,
    val roundWind: Int,
    val roundNumber: Int,
    val honbaSticks: Int,
    val riichiSticks: Int,
    val dealerSeat: Int,
    val currentTurnPlayer: Int,
    val wallRemaining: Int,
    doraIndicators: List<ProtocolTile>,
    scores: List<Int>,
    riichiStatus: List<Boolean>,
    discards: List<List<ProtocolTile>>,
    melds: List<List<ProtocolMeld>>,
    handTileCounts: List<Int>,
    myClosedTiles: List<ProtocolTile>,
    val myDrawnTile: ProtocolTile?,
    legalActions: List<ProtocolLegalAction>,
    val lastDiscard: ProtocolTile? = null,
    val lastDiscardPlayer: Int? = null,
    mySleeveTiles: List<ProtocolTile> = emptyList(),
    val myFuriten: ProtocolFuritenStatus = ProtocolFuritenStatus(),
    myDiscardHistory: List<ProtocolTile> = emptyList()
) {
    val doraIndicators: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(doraIndicators))
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val riichiStatus: List<Boolean> = Collections.unmodifiableList(ArrayList(riichiStatus))
    val discards: List<List<ProtocolTile>> = Collections.unmodifiableList(discards.map { Collections.unmodifiableList(ArrayList(it)) })
    val melds: List<List<ProtocolMeld>> = Collections.unmodifiableList(melds.map { Collections.unmodifiableList(ArrayList(it)) })
    val handTileCounts: List<Int> = Collections.unmodifiableList(ArrayList(handTileCounts))
    val myClosedTiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(myClosedTiles))
    val legalActions: List<ProtocolLegalAction> = Collections.unmodifiableList(ArrayList(legalActions))
    val mySleeveTiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(mySleeveTiles))
    val myDiscardHistory: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(myDiscardHistory))

    init {
        require(viewerSeat in 0..3) { "viewerSeat must be 0..3: $viewerSeat" }
        require(this.scores.size == 4) { "scores must have size 4" }
        require(this.riichiStatus.size == 4) { "riichiStatus must have size 4" }
        require(this.discards.size == 4) { "discards must have size 4" }
        require(this.melds.size == 4) { "melds must have size 4" }
        require(this.handTileCounts.size == 4) { "handTileCounts must have size 4" }
    }

    operator fun component1(): Int = viewerSeat
    operator fun component2(): ProtocolGamePhase = phase
    operator fun component3(): Int = roundWind
    operator fun component4(): Int = roundNumber
    operator fun component5(): Int = honbaSticks
    operator fun component6(): Int = riichiSticks
    operator fun component7(): Int = dealerSeat
    operator fun component8(): Int = currentTurnPlayer
    operator fun component9(): Int = wallRemaining
    operator fun component10(): List<ProtocolTile> = doraIndicators
    operator fun component11(): List<Int> = scores
    operator fun component12(): List<Boolean> = riichiStatus
    operator fun component13(): List<List<ProtocolTile>> = discards
    operator fun component14(): List<List<ProtocolMeld>> = melds
    operator fun component15(): List<Int> = handTileCounts
    operator fun component16(): List<ProtocolTile> = myClosedTiles
    operator fun component17(): ProtocolTile? = myDrawnTile
    operator fun component18(): List<ProtocolLegalAction> = legalActions
    operator fun component19(): ProtocolTile? = lastDiscard
    operator fun component20(): Int? = lastDiscardPlayer
    operator fun component21(): List<ProtocolTile> = mySleeveTiles
    operator fun component22(): ProtocolFuritenStatus = myFuriten
    operator fun component23(): List<ProtocolTile> = myDiscardHistory

    fun copy(
        viewerSeat: Int = this.viewerSeat,
        phase: ProtocolGamePhase = this.phase,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        honbaSticks: Int = this.honbaSticks,
        riichiSticks: Int = this.riichiSticks,
        dealerSeat: Int = this.dealerSeat,
        currentTurnPlayer: Int = this.currentTurnPlayer,
        wallRemaining: Int = this.wallRemaining,
        doraIndicators: List<ProtocolTile> = this.doraIndicators,
        scores: List<Int> = this.scores,
        riichiStatus: List<Boolean> = this.riichiStatus,
        discards: List<List<ProtocolTile>> = this.discards,
        melds: List<List<ProtocolMeld>> = this.melds,
        handTileCounts: List<Int> = this.handTileCounts,
        myClosedTiles: List<ProtocolTile> = this.myClosedTiles,
        myDrawnTile: ProtocolTile? = this.myDrawnTile,
        legalActions: List<ProtocolLegalAction> = this.legalActions,
        lastDiscard: ProtocolTile? = this.lastDiscard,
        lastDiscardPlayer: Int? = this.lastDiscardPlayer,
        mySleeveTiles: List<ProtocolTile> = this.mySleeveTiles,
        myFuriten: ProtocolFuritenStatus = this.myFuriten,
        myDiscardHistory: List<ProtocolTile> = this.myDiscardHistory
    ): ProtocolPlayerView = ProtocolPlayerView(
        viewerSeat, phase, roundWind, roundNumber, honbaSticks, riichiSticks, dealerSeat,
        currentTurnPlayer, wallRemaining, doraIndicators, scores, riichiStatus, discards,
        melds, handTileCounts, myClosedTiles, myDrawnTile, legalActions, lastDiscard,
        lastDiscardPlayer, mySleeveTiles, myFuriten, myDiscardHistory
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPlayerView) return false
        return viewerSeat == other.viewerSeat && phase == other.phase &&
            roundWind == other.roundWind && roundNumber == other.roundNumber &&
            honbaSticks == other.honbaSticks && riichiSticks == other.riichiSticks &&
            dealerSeat == other.dealerSeat && currentTurnPlayer == other.currentTurnPlayer &&
            wallRemaining == other.wallRemaining && doraIndicators == other.doraIndicators &&
            scores == other.scores && riichiStatus == other.riichiStatus &&
            discards == other.discards && melds == other.melds &&
            handTileCounts == other.handTileCounts && myClosedTiles == other.myClosedTiles &&
            myDrawnTile == other.myDrawnTile && legalActions == other.legalActions &&
            lastDiscard == other.lastDiscard && lastDiscardPlayer == other.lastDiscardPlayer &&
            mySleeveTiles == other.mySleeveTiles && myFuriten == other.myFuriten &&
            myDiscardHistory == other.myDiscardHistory
    }

    override fun hashCode(): Int {
        var result = viewerSeat.hashCode()
        result = 31 * result + phase.hashCode()
        result = 31 * result + roundWind.hashCode()
        result = 31 * result + roundNumber.hashCode()
        result = 31 * result + honbaSticks.hashCode()
        result = 31 * result + riichiSticks.hashCode()
        result = 31 * result + dealerSeat.hashCode()
        result = 31 * result + currentTurnPlayer.hashCode()
        result = 31 * result + wallRemaining.hashCode()
        result = 31 * result + doraIndicators.hashCode()
        result = 31 * result + scores.hashCode()
        result = 31 * result + riichiStatus.hashCode()
        result = 31 * result + discards.hashCode()
        result = 31 * result + melds.hashCode()
        result = 31 * result + handTileCounts.hashCode()
        result = 31 * result + myClosedTiles.hashCode()
        result = 31 * result + (myDrawnTile?.hashCode() ?: 0)
        result = 31 * result + legalActions.hashCode()
        result = 31 * result + (lastDiscard?.hashCode() ?: 0)
        result = 31 * result + (lastDiscardPlayer?.hashCode() ?: 0)
        result = 31 * result + mySleeveTiles.hashCode()
        result = 31 * result + myFuriten.hashCode()
        result = 31 * result + myDiscardHistory.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolPlayerView(viewerSeat=$viewerSeat, phase=$phase, roundWind=$roundWind, roundNumber=$roundNumber, dealerSeat=$dealerSeat, scores=$scores)"
}

/**
 * 传输层独立协议完整快照 DTO (ProtocolPlayerSnapshot)
 */
class ProtocolPlayerSnapshot(
    val sessionId: String,
    val roundId: String?,
    val syncEpoch: String,
    val cursor: Long,
    val sessionVersion: Long,
    val roundVersion: Long?,
    val view: ProtocolPlayerView,
    val decision: ProtocolPlayerDecision,
    clocks: List<ProtocolClock> = emptyList(),
    cheatPoints: List<Int> = emptyList(),
    val session: ProtocolSessionView? = null,
    controls: List<ProtocolSeatControl> = emptyList(),
    val paused: Boolean = false,
    val settlementRoundId: String? = null,
    settlementEvents: List<ProtocolGameEvent> = emptyList(),
    val preparation: ProtocolShufflePreparation? = null,
    val presentationPreview: ProtocolPlayerPresentationPreview? = null,
    val tileMark: ProtocolTileMarkView? = null
) {
    val viewerSeat: Int get() = view.viewerSeat
    val clocks: List<ProtocolClock> = Collections.unmodifiableList(ArrayList(clocks))
    val cheatPoints: List<Int> = Collections.unmodifiableList(ArrayList(cheatPoints))
    val controls: List<ProtocolSeatControl> = Collections.unmodifiableList(ArrayList(controls))
    val settlementEvents: List<ProtocolGameEvent> = Collections.unmodifiableList(ArrayList(settlementEvents))

    init {
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(syncEpoch.isNotBlank()) { "syncEpoch cannot be blank" }
        require(cursor >= 0L) { "cursor must be >= 0: $cursor" }
        require(sessionVersion >= 0L) { "sessionVersion must be >= 0: $sessionVersion" }
        if (roundVersion != null) {
            require(roundVersion >= 0L) { "roundVersion must be >= 0: $roundVersion" }
        }
        if (settlementRoundId != null) {
            require(settlementRoundId.isNotBlank()) { "settlementRoundId cannot be blank" }
        }
        if (presentationPreview != null) {
            require(roundId != null) {
                "presentationPreview cannot be present when roundId is null"
            }
            require(presentationPreview.roundId == roundId) {
                "presentationPreview.roundId (${presentationPreview.roundId}) does not match roundId ($roundId)"
            }
            require(decision.roundId == roundId) {
                "decision.roundId (${decision.roundId}) does not match roundId ($roundId) when presentationPreview is present"
            }
            require(presentationPreview.decisionId == decision.decisionId) {
                "presentationPreview.decisionId (${presentationPreview.decisionId}) does not match decision.decisionId (${decision.decisionId})"
            }
            require(presentationPreview.controlEpoch == decision.controlEpoch) {
                "presentationPreview.controlEpoch (${presentationPreview.controlEpoch}) does not match decision.controlEpoch (${decision.controlEpoch})"
            }
            val decisionOptionsById = decision.options.associateBy { it.optionId }
            for (riichiOpt in presentationPreview.riichiOptions) {
                val matched = decisionOptionsById[riichiOpt.optionId]
                require(matched != null) {
                    "presentationPreview riichi optionId '${riichiOpt.optionId}' not found in decision.options"
                }
                require(matched.action == riichiOpt.action) {
                    "presentationPreview riichi option '${riichiOpt.optionId}' action (${riichiOpt.action}) does not match decision action (${matched.action})"
                }
            }
            for (meldOpt in presentationPreview.meldOptions) {
                val matched = decisionOptionsById[meldOpt.optionId]
                require(matched != null) {
                    "presentationPreview meld optionId '${meldOpt.optionId}' not found in decision.options"
                }
                require(matched.action == meldOpt.action) {
                    "presentationPreview meld option '${meldOpt.optionId}' action (${meldOpt.action}) does not match decision action (${matched.action})"
                }
            }
        }
        if (tileMark != null) {
            for (group in tileMark.activeGroups) {
                for (tile in group.tiles) {
                    if (tile.area == ProtocolTileMarkArea.DISCARD) {
                        val pos = tile.riverPosition
                            ?: throw IllegalArgumentException("Missing riverPosition for DISCARD marked tile")
                        require(roundId != null && pos.roundId == roundId) {
                            "RiverPosition roundId (${pos.roundId}) does not match snapshot roundId ($roundId)"
                        }
                        require(pos.seat == tile.seat) {
                            "RiverPosition seat (${pos.seat}) does not match marked tile seat (${tile.seat})"
                        }
                        val discards = view.discards.getOrNull(pos.seat)
                            ?: throw IllegalArgumentException("Seat ${pos.seat} discards not found in view")
                        require(pos.discardIndex in discards.indices) {
                            "RiverPosition discardIndex (${pos.discardIndex}) out of range (0 until ${discards.size})"
                        }
                        require(discards[pos.discardIndex].tileId == tile.tileId) {
                            "RiverPosition tile at seat ${pos.seat} index ${pos.discardIndex} is ${discards[pos.discardIndex].tileId}, expected ${tile.tileId}"
                        }
                    }
                }
            }
        }
    }

    operator fun component1(): String = sessionId
    operator fun component2(): String? = roundId
    operator fun component3(): String = syncEpoch
    operator fun component4(): Long = cursor
    operator fun component5(): Long = sessionVersion
    operator fun component6(): Long? = roundVersion
    operator fun component7(): ProtocolPlayerView = view
    operator fun component8(): ProtocolPlayerDecision = decision
    operator fun component9(): List<ProtocolClock> = clocks
    operator fun component10(): List<Int> = cheatPoints
    operator fun component11(): ProtocolSessionView? = session
    operator fun component12(): List<ProtocolSeatControl> = controls
    operator fun component13(): Boolean = paused
    operator fun component14(): String? = settlementRoundId
    operator fun component15(): List<ProtocolGameEvent> = settlementEvents
    operator fun component16(): ProtocolShufflePreparation? = preparation
    operator fun component17(): ProtocolPlayerPresentationPreview? = presentationPreview
    operator fun component18(): ProtocolTileMarkView? = tileMark

    fun copy(
        sessionId: String = this.sessionId,
        roundId: String? = this.roundId,
        syncEpoch: String = this.syncEpoch,
        cursor: Long = this.cursor,
        sessionVersion: Long = this.sessionVersion,
        roundVersion: Long? = this.roundVersion,
        view: ProtocolPlayerView = this.view,
        decision: ProtocolPlayerDecision = this.decision,
        clocks: List<ProtocolClock> = this.clocks,
        cheatPoints: List<Int> = this.cheatPoints,
        session: ProtocolSessionView? = this.session,
        controls: List<ProtocolSeatControl> = this.controls,
        paused: Boolean = this.paused,
        settlementRoundId: String? = this.settlementRoundId,
        settlementEvents: List<ProtocolGameEvent> = this.settlementEvents,
        preparation: ProtocolShufflePreparation? = this.preparation,
        presentationPreview: ProtocolPlayerPresentationPreview? = this.presentationPreview,
        tileMark: ProtocolTileMarkView? = this.tileMark
    ): ProtocolPlayerSnapshot = ProtocolPlayerSnapshot(
        sessionId, roundId, syncEpoch, cursor, sessionVersion, roundVersion,
        view, decision, clocks, cheatPoints, session, controls, paused,
        settlementRoundId, settlementEvents, preparation, presentationPreview, tileMark
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPlayerSnapshot) return false
        return sessionId == other.sessionId && roundId == other.roundId &&
            syncEpoch == other.syncEpoch && cursor == other.cursor &&
            sessionVersion == other.sessionVersion && roundVersion == other.roundVersion &&
            view == other.view && decision == other.decision &&
            clocks == other.clocks && cheatPoints == other.cheatPoints &&
            session == other.session && controls == other.controls &&
            paused == other.paused && settlementRoundId == other.settlementRoundId &&
            settlementEvents == other.settlementEvents && preparation == other.preparation &&
            presentationPreview == other.presentationPreview &&
            tileMark == other.tileMark
    }

    override fun hashCode(): Int {
        var result = sessionId.hashCode()
        result = 31 * result + (roundId?.hashCode() ?: 0)
        result = 31 * result + syncEpoch.hashCode()
        result = 31 * result + cursor.hashCode()
        result = 31 * result + sessionVersion.hashCode()
        result = 31 * result + (roundVersion?.hashCode() ?: 0)
        result = 31 * result + view.hashCode()
        result = 31 * result + decision.hashCode()
        result = 31 * result + clocks.hashCode()
        result = 31 * result + cheatPoints.hashCode()
        result = 31 * result + (session?.hashCode() ?: 0)
        result = 31 * result + controls.hashCode()
        result = 31 * result + paused.hashCode()
        result = 31 * result + (settlementRoundId?.hashCode() ?: 0)
        result = 31 * result + settlementEvents.hashCode()
        result = 31 * result + (preparation?.hashCode() ?: 0)
        result = 31 * result + (presentationPreview?.hashCode() ?: 0)
        result = 31 * result + (tileMark?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "ProtocolPlayerSnapshot(sessionId=$sessionId, roundId=$roundId, cursor=$cursor, viewerSeat=$viewerSeat)"
}

/**
 * 传输层独立协议玩家增量推送 DTO (ProtocolPlayerUpdate)
 */
class ProtocolPlayerUpdate(
    val snapshot: ProtocolPlayerSnapshot,
    val fromCursor: Long,
    visibleEvents: List<ProtocolGameEvent> = emptyList(),
    val isFullSnapshot: Boolean = false
) {
    val visibleEvents: List<ProtocolGameEvent> = Collections.unmodifiableList(ArrayList(visibleEvents))

    init {
        require(fromCursor >= 0L) { "fromCursor must be >= 0: $fromCursor" }
        require(snapshot.cursor >= fromCursor) {
            "snapshot.cursor (${snapshot.cursor}) cannot be less than fromCursor ($fromCursor)"
        }
    }

    operator fun component1(): ProtocolPlayerSnapshot = snapshot
    operator fun component2(): Long = fromCursor
    operator fun component3(): List<ProtocolGameEvent> = visibleEvents
    operator fun component4(): Boolean = isFullSnapshot

    fun copy(
        snapshot: ProtocolPlayerSnapshot = this.snapshot,
        fromCursor: Long = this.fromCursor,
        visibleEvents: List<ProtocolGameEvent> = this.visibleEvents,
        isFullSnapshot: Boolean = this.isFullSnapshot
    ): ProtocolPlayerUpdate = ProtocolPlayerUpdate(snapshot, fromCursor, visibleEvents, isFullSnapshot)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolPlayerUpdate) return false
        return snapshot == other.snapshot && fromCursor == other.fromCursor &&
            visibleEvents == other.visibleEvents && isFullSnapshot == other.isFullSnapshot
    }

    override fun hashCode(): Int {
        var result = snapshot.hashCode()
        result = 31 * result + fromCursor.hashCode()
        result = 31 * result + visibleEvents.hashCode()
        result = 31 * result + isFullSnapshot.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolPlayerUpdate(snapshot=$snapshot, fromCursor=$fromCursor, visibleEvents=$visibleEvents, isFullSnapshot=$isFullSnapshot)"
}
