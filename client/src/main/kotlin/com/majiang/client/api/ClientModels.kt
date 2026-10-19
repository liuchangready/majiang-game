package com.majiang.client.api

import java.util.Collections

/**
 * 客户端操作回执状态 (ReceiptStatus)
 */
enum class ReceiptStatus {
    APPLIED,
    RECORDED,
    REJECTED
}

/**
 * 客户端操作回执 (Receipt)
 *
 * @property requestId 请求唯一标识
 * @property status 执行状态 (APPLIED/RECORDED/REJECTED)
 * @property cursor 本次动作推进到的权威游标，失败或无推进时为 null
 * @property reason 拒绝原因码或内部错误信息，成功时为 null
 */
data class Receipt(
    val requestId: String,
    val status: ReceiptStatus,
    val cursor: Long? = null,
    val reason: String? = null
)

/**
 * 客户端动作选择载荷 (ActionChoice)
 *
 * 封装动作提交时的幂等标识、局次与动作选项 ID。
 */
data class ActionChoice(
    val requestId: String,
    val roundId: String? = null,
    val decisionId: String,
    val controlEpoch: Long,
    val optionId: String
)

/**
 * 客户端洗牌千术做牌目标
 */
enum class ShuffleTarget {
    CHINITSU_MAN,   // 万子清一色
    CHINITSU_SOU,   // 索子清一色
    CHINITSU_PIN,   // 筒子清一色
    DAISANGEN,      // 大三元 (白发中)
    KOKUSHI,        // 国士无双 / 十三幺
    DAISUUSHII      // 大四喜 (东南西北)
}

/**
 * 客户端洗牌千术目标选择方案
 *
 * @property initialTargets 影响起手牌的目标集合（偷天换日，40点）
 * @property drawTargets 影响后续摸牌流的目标集合（瞒天过海，20点）
 */
class ShuffleSelection(
    initialTargets: Set<ShuffleTarget> = emptySet(),
    drawTargets: Set<ShuffleTarget> = emptySet()
) {
    val initialTargets: Set<ShuffleTarget> = Collections.unmodifiableSet(LinkedHashSet(initialTargets))
    val drawTargets: Set<ShuffleTarget> = Collections.unmodifiableSet(LinkedHashSet(drawTargets))

    fun cost(): Int =
        (if (initialTargets.isNotEmpty()) 40 else 0) + (if (drawTargets.isNotEmpty()) 20 else 0)

    val isActive: Boolean get() = initialTargets.isNotEmpty() || drawTargets.isNotEmpty()

    operator fun component1(): Set<ShuffleTarget> = initialTargets
    operator fun component2(): Set<ShuffleTarget> = drawTargets

    fun copy(
        initialTargets: Set<ShuffleTarget> = this.initialTargets,
        drawTargets: Set<ShuffleTarget> = this.drawTargets
    ): ShuffleSelection = ShuffleSelection(initialTargets, drawTargets)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ShuffleSelection) return false
        return initialTargets == other.initialTargets && drawTargets == other.drawTargets
    }

    override fun hashCode(): Int = 31 * initialTargets.hashCode() + drawTargets.hashCode()

    override fun toString(): String =
        "ShuffleSelection(initialTargets=$initialTargets, drawTargets=$drawTargets)"
}

/**
 * 客户端洗牌千术方案提交请求 (ShufflePlan)
 */
data class ShufflePlan(
    val requestId: String,
    val roundId: String,
    val controlEpoch: Long,
    val selection: ShuffleSelection = ShuffleSelection()
)

/**
 * 客户端出千经济系统配置
 *
 * @property enabled 是否启用出千经济系统
 * @property initialPoints 各座位初始出千点数（4 个座位）
 * @property costs 各项千术的消耗点数配置
 */
class CheatEconomyConfig(
    val enabled: Boolean = false,
    initialPoints: List<Int> = listOf(0, 0, 0, 0),
    costs: Map<String, Int> = emptyMap()
) {
    val initialPoints: List<Int> = Collections.unmodifiableList(ArrayList(initialPoints))
    val costs: Map<String, Int> = Collections.unmodifiableMap(LinkedHashMap(costs))

    fun costFor(techniqueId: String): Int = costs[techniqueId] ?: 0

    operator fun component1(): Boolean = enabled
    operator fun component2(): List<Int> = initialPoints
    operator fun component3(): Map<String, Int> = costs

    fun copy(
        enabled: Boolean = this.enabled,
        initialPoints: List<Int> = this.initialPoints,
        costs: Map<String, Int> = this.costs
    ): CheatEconomyConfig = CheatEconomyConfig(enabled, initialPoints, costs)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is CheatEconomyConfig) return false
        return enabled == other.enabled && initialPoints == other.initialPoints && costs == other.costs
    }

    override fun hashCode(): Int {
        var result = enabled.hashCode()
        result = 31 * result + initialPoints.hashCode()
        result = 31 * result + costs.hashCode()
        return result
    }

    override fun toString(): String =
        "CheatEconomyConfig(enabled=$enabled, initialPoints=$initialPoints, costs=$costs)"
}

/**
 * 客户端洗牌千术阶段配置
 *
 * @property enabled 是否启用洗牌阶段千术
 * @property selectionMillis 准备阶段选择时长（毫秒）
 * @property plannedDraws 瞒天过海影响的后续预计摸牌轮数
 * @property strength 基础做牌强度 [0.0, 1.0]
 * @property interference 冲突干扰衰减系数 lambda >= 0.0
 * @property aiMaySpend AI 玩家是否允许消耗点数出千
 */
data class ShuffleConfig(
    val enabled: Boolean = false,
    val selectionMillis: Long = 10000L,
    val plannedDraws: Int = 8,
    val strength: Double = 0.75,
    val interference: Double = 0.65,
    val aiMaySpend: Boolean = false
)

/**
 * 客户端比赛规则配置 (MatchConfig)
 *
 * 忠实反映完整 13 维配置，严禁虚构字段。
 */
class MatchConfig(
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
    val cheatEconomy: CheatEconomyConfig = CheatEconomyConfig(),
    val shuffleConfig: ShuffleConfig = ShuffleConfig(),
    val tileMarkConfig: TileMarkConfig = TileMarkConfig(),
    val sleeveBloomConfig: SleeveBloomConfig = SleeveBloomConfig(),
    val beamSwapConfig: BeamSwapConfig = BeamSwapConfig()
) {
    val winds: List<Int> = Collections.unmodifiableList(ArrayList(winds))
    val startingScores: List<Int> = Collections.unmodifiableList(ArrayList(startingScores))
    val tieBreakSeats: List<Int> = Collections.unmodifiableList(ArrayList(tieBreakSeats))

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
    operator fun component12(): CheatEconomyConfig = cheatEconomy
    operator fun component13(): ShuffleConfig = shuffleConfig
    operator fun component14(): TileMarkConfig = tileMarkConfig
    operator fun component15(): SleeveBloomConfig = sleeveBloomConfig
    operator fun component16(): BeamSwapConfig = beamSwapConfig

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
        cheatEconomy: CheatEconomyConfig = this.cheatEconomy,
        shuffleConfig: ShuffleConfig = this.shuffleConfig,
        tileMarkConfig: TileMarkConfig = this.tileMarkConfig,
        sleeveBloomConfig: SleeveBloomConfig = this.sleeveBloomConfig,
        beamSwapConfig: BeamSwapConfig = this.beamSwapConfig
    ): MatchConfig = MatchConfig(
        playerCount, handsPerWind, winds, startingScores,
        allowExtension, allowAgariYame, bankruptcyEnabled,
        bankruptcyThreshold, bankruptcyInclusive, targetScore,
        tieBreakSeats, cheatEconomy, shuffleConfig, tileMarkConfig,
        sleeveBloomConfig, beamSwapConfig
    )

    fun deepCopy(): MatchConfig = copy(
        winds = ArrayList(winds),
        startingScores = ArrayList(startingScores),
        tieBreakSeats = ArrayList(tieBreakSeats)
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is MatchConfig) return false
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
            tileMarkConfig == other.tileMarkConfig &&
            sleeveBloomConfig == other.sleeveBloomConfig &&
            beamSwapConfig == other.beamSwapConfig
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
        result = 31 * result + sleeveBloomConfig.hashCode()
        result = 31 * result + beamSwapConfig.hashCode()
        return result
    }

    override fun toString(): String =
        "MatchConfig(playerCount=$playerCount, handsPerWind=$handsPerWind, winds=$winds, startingScores=$startingScores, allowExtension=$allowExtension, allowAgariYame=$allowAgariYame, bankruptcyEnabled=$bankruptcyEnabled, bankruptcyThreshold=$bankruptcyThreshold, bankruptcyInclusive=$bankruptcyInclusive, targetScore=$targetScore, tieBreakSeats=$tieBreakSeats, cheatEconomy=$cheatEconomy, shuffleConfig=$shuffleConfig, tileMarkConfig=$tileMarkConfig, sleeveBloomConfig=$sleeveBloomConfig, beamSwapConfig=$beamSwapConfig)"
}

/**
 * 客户端连接能力描述 (Capabilities)
 */
class Capabilities(
    val protocolVersion: Int = 5,
    supportedPlayerCounts: Set<Int> = setOf(4),
    val remoteTransport: Boolean = false,
    val persistence: Boolean = false,
    val resumption: Boolean = true,
    val tileMarkSupported: Boolean = false,
    val sleeveBloomSupported: Boolean = false,
    val beamSwapSupported: Boolean = false,
    val scoreDetailVersion: Int = 0
) {
    val supportedPlayerCounts: Set<Int> = Collections.unmodifiableSet(LinkedHashSet(supportedPlayerCounts))

    init {
        require(scoreDetailVersion >= 0) { "scoreDetailVersion cannot be negative: $scoreDetailVersion" }
    }

    operator fun component1(): Int = protocolVersion
    operator fun component2(): Set<Int> = supportedPlayerCounts
    operator fun component3(): Boolean = remoteTransport
    operator fun component4(): Boolean = persistence
    operator fun component5(): Boolean = resumption
    operator fun component6(): Boolean = tileMarkSupported
    operator fun component7(): Boolean = sleeveBloomSupported
    operator fun component8(): Boolean = beamSwapSupported
    operator fun component9(): Int = scoreDetailVersion

    fun copy(
        protocolVersion: Int = this.protocolVersion,
        supportedPlayerCounts: Set<Int> = this.supportedPlayerCounts,
        remoteTransport: Boolean = this.remoteTransport,
        persistence: Boolean = this.persistence,
        resumption: Boolean = this.resumption,
        tileMarkSupported: Boolean = this.tileMarkSupported,
        sleeveBloomSupported: Boolean = this.sleeveBloomSupported,
        beamSwapSupported: Boolean = this.beamSwapSupported,
        scoreDetailVersion: Int = this.scoreDetailVersion
    ): Capabilities = Capabilities(
        protocolVersion, supportedPlayerCounts, remoteTransport, persistence, resumption,
        tileMarkSupported, sleeveBloomSupported, beamSwapSupported, scoreDetailVersion
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is Capabilities) return false
        return protocolVersion == other.protocolVersion &&
            supportedPlayerCounts == other.supportedPlayerCounts &&
            remoteTransport == other.remoteTransport &&
            persistence == other.persistence &&
            resumption == other.resumption &&
            tileMarkSupported == other.tileMarkSupported &&
            sleeveBloomSupported == other.sleeveBloomSupported &&
            beamSwapSupported == other.beamSwapSupported &&
            scoreDetailVersion == other.scoreDetailVersion
    }

    override fun hashCode(): Int {
        var result = protocolVersion.hashCode()
        result = 31 * result + supportedPlayerCounts.hashCode()
        result = 31 * result + remoteTransport.hashCode()
        result = 31 * result + persistence.hashCode()
        result = 31 * result + resumption.hashCode()
        result = 31 * result + tileMarkSupported.hashCode()
        result = 31 * result + sleeveBloomSupported.hashCode()
        result = 31 * result + beamSwapSupported.hashCode()
        result = 31 * result + scoreDetailVersion.hashCode()
        return result
    }

    override fun toString(): String =
        "Capabilities(protocolVersion=$protocolVersion, supportedPlayerCounts=$supportedPlayerCounts, remoteTransport=$remoteTransport, persistence=$persistence, resumption=$resumption, tileMarkSupported=$tileMarkSupported, sleeveBloomSupported=$sleeveBloomSupported, beamSwapSupported=$beamSwapSupported, scoreDetailVersion=$scoreDetailVersion)"
}

/**
 * 客户端房间生命周期枚举 (RoomLifecycle)
 */
enum class RoomLifecycle {
    LOBBY,
    PREPARING,
    PLAYING,
    SETTLED,
    FINISHED,
    CLOSED
}

/**
 * 客户端房间成员信息 (RoomMember)
 */
data class RoomMember(
    val seat: Int,
    val playerName: String,
    val isHost: Boolean,
    val isReady: Boolean,
    val role: String
) {
    init {
        require(seat in 0..3) { "seat must be in 0..3: $seat" }
        require(playerName.isNotBlank()) { "playerName cannot be blank" }
        require(role in setOf("HUMAN", "COMPUTER", "TAKEOVER")) { "invalid role: $role" }
    }
}

/**
 * 客户端思考与时钟配置 (TimeControlConfig)
 */
data class TimeControlConfig(
    val roundReserveSeconds: Long = 20L,
    val turnThinkingSeconds: Long = 3L
) {
    init {
        require(roundReserveSeconds in 0L..(Long.MAX_VALUE / 1000L)) {
            "roundReserveSeconds out of valid range (0..${Long.MAX_VALUE / 1000L}): $roundReserveSeconds"
        }
        require(turnThinkingSeconds in 0L..(Long.MAX_VALUE / 1000L)) {
            "turnThinkingSeconds out of valid range (0..${Long.MAX_VALUE / 1000L}): $turnThinkingSeconds"
        }
    }
}

/**
 * 客户端房间配置 (RoomConfig)
 */
data class RoomConfig(
    val matchConfig: MatchConfig = MatchConfig(),
    val timeControl: TimeControlConfig = TimeControlConfig()
)

/**
 * 客户端完整房间信息 (RoomInfo)
 */
class RoomInfo(
    val roomId: String,
    val lifecycle: RoomLifecycle,
    val version: Long = 1L,
    val hostSeat: Int = 0,
    members: List<RoomMember> = emptyList(),
    val config: RoomConfig = RoomConfig()
) {
    val members: List<RoomMember> = Collections.unmodifiableList(ArrayList(members))

    constructor(
        roomId: String,
        lifecycle: RoomLifecycle,
        hostSeat: Int = 0,
        members: List<RoomMember> = emptyList(),
        config: RoomConfig = RoomConfig()
    ) : this(roomId, lifecycle, 1L, hostSeat, members, config)

    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        require(version >= 0L) { "version must be >= 0: $version" }
        require(hostSeat in 0..3) { "hostSeat must be in 0..3: $hostSeat" }
    }

    operator fun component1(): String = roomId
    operator fun component2(): RoomLifecycle = lifecycle
    operator fun component3(): Long = version
    operator fun component4(): Int = hostSeat
    operator fun component5(): List<RoomMember> = members
    operator fun component6(): RoomConfig = config

    fun copy(
        roomId: String = this.roomId,
        lifecycle: RoomLifecycle = this.lifecycle,
        version: Long = this.version,
        hostSeat: Int = this.hostSeat,
        members: List<RoomMember> = this.members,
        config: RoomConfig = this.config
    ): RoomInfo = RoomInfo(roomId, lifecycle, version, hostSeat, members, config)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is RoomInfo) return false
        return roomId == other.roomId && lifecycle == other.lifecycle &&
            version == other.version && hostSeat == other.hostSeat &&
            members == other.members && config == other.config
    }

    override fun hashCode(): Int {
        var r = roomId.hashCode()
        r = 31 * r + lifecycle.hashCode()
        r = 31 * r + version.hashCode()
        r = 31 * r + hostSeat.hashCode()
        r = 31 * r + members.hashCode()
        r = 31 * r + config.hashCode()
        return r
    }

    override fun toString(): String =
        "RoomInfo(roomId=$roomId, lifecycle=$lifecycle, version=$version, hostSeat=$hostSeat, members=$members, config=$config)"
}


