package com.majiang.cheat.bloom

import com.majiang.access.SeatControlView
import com.majiang.application.PlayerDecision
import com.majiang.economy.CheatEconomyConfig
import com.majiang.model.Tile
import com.majiang.model.TileLocation
import com.majiang.round.RoundStateMachine
import com.majiang.session.PlayerView
import com.majiang.state.GamePhase
import com.majiang.timing.DecisionTiming

/**
 * 袖里藏花千术配置
 *
 * @property enabled 是否启用袖里藏花功能（默认关闭）
 */
data class SleeveBloomConfig(
    val enabled: Boolean = false
)

/**
 * 最近一次摸牌来源类型
 */
enum class SleeveBloomDrawKind {
    DEAL_EXTRA, // 庄家起手第 14 张发牌（禁止施术）
    NORMAL,     // 正常活牌山摸牌
    RINSHAN     // 杠后岭上补牌
}

/**
 * 袖里藏花二选一保留目标
 */
enum class SleeveBloomKeep {
    FIRST,  // 保留第一张（原摸到/补到的牌）
    SECOND  // 保留第二张（活牌山头端次张/王牌区替补牌）
}

/**
 * 激活袖里藏花请求
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 单局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 */
class SleeveBloomRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val decisionId: String,
    val controlEpoch: Long
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
    }

    operator fun component1(): String = requestId
    operator fun component2(): String = sessionId
    operator fun component3(): String = roundId
    operator fun component4(): String = decisionId
    operator fun component5(): Long = controlEpoch

    fun copy(
        requestId: String = this.requestId,
        sessionId: String = this.sessionId,
        roundId: String = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch
    ): SleeveBloomRequest = SleeveBloomRequest(
        requestId = requestId,
        sessionId = sessionId,
        roundId = roundId,
        decisionId = decisionId,
        controlEpoch = controlEpoch
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SleeveBloomRequest) return false
        return requestId == other.requestId &&
                sessionId == other.sessionId &&
                roundId == other.roundId &&
                decisionId == other.decisionId &&
                controlEpoch == other.controlEpoch
    }

    override fun hashCode(): Int {
        var result = requestId.hashCode()
        result = 31 * result + sessionId.hashCode()
        result = 31 * result + roundId.hashCode()
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        return result
    }

    override fun toString(): String {
        return "SleeveBloomRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch)"
    }
}

/**
 * 袖里藏花二选一决策请求
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 单局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 * @property offerId 挂起的二选一唯一标识
 * @property keep 选择保留的目标候选 (FIRST / SECOND)
 */
class SleeveBloomChoiceRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val decisionId: String,
    val controlEpoch: Long,
    val offerId: String,
    val keep: SleeveBloomKeep
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
    }

    operator fun component1(): String = requestId
    operator fun component2(): String = sessionId
    operator fun component3(): String = roundId
    operator fun component4(): String = decisionId
    operator fun component5(): Long = controlEpoch
    operator fun component6(): String = offerId
    operator fun component7(): SleeveBloomKeep = keep

    fun copy(
        requestId: String = this.requestId,
        sessionId: String = this.sessionId,
        roundId: String = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        offerId: String = this.offerId,
        keep: SleeveBloomKeep = this.keep
    ): SleeveBloomChoiceRequest = SleeveBloomChoiceRequest(
        requestId = requestId,
        sessionId = sessionId,
        roundId = roundId,
        decisionId = decisionId,
        controlEpoch = controlEpoch,
        offerId = offerId,
        keep = keep
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SleeveBloomChoiceRequest) return false
        return requestId == other.requestId &&
                sessionId == other.sessionId &&
                roundId == other.roundId &&
                decisionId == other.decisionId &&
                controlEpoch == other.controlEpoch &&
                offerId == other.offerId &&
                keep == other.keep
    }

    override fun hashCode(): Int {
        var result = requestId.hashCode()
        result = 31 * result + sessionId.hashCode()
        result = 31 * result + roundId.hashCode()
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        result = 31 * result + offerId.hashCode()
        result = 31 * result + keep.hashCode()
        return result
    }

    override fun toString(): String {
        return "SleeveBloomChoiceRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, offerId=$offerId, keep=$keep)"
    }
}

/**
 * 挂起的袖里藏花二选一领域状态
 *
 * @property offerId 唯一标识 (bloom-$seat-$requestId)
 * @property seat 所属玩家席位 (0..3)
 * @property drawKind 摸牌来源类型
 * @property firstTile 第一候选牌（原摸到/补到的牌）
 * @property secondTile 第二候选牌（逻辑引用，物理原位不动）
 * @property createdTurnNumber 创建时的摸牌巡目 (turnNumber)
 */
data class SleeveBloomOffer(
    val offerId: String,
    val seat: Int,
    val drawKind: SleeveBloomDrawKind,
    val firstTile: Tile,
    val secondTile: Tile,
    val createdTurnNumber: Int
) {
    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        require(seat in 0..3) { "seat must be in 0..3: $seat" }
        require(createdTurnNumber >= 0) { "createdTurnNumber must be non-negative: $createdTurnNumber" }
    }
}

/**
 * 观察者私有二选一挂起视图
 *
 * @property offerId 挂起二选一标识
 * @property firstTile 第一候选牌
 * @property secondTile 第二候选牌
 * @property drawKind 摸牌来源
 * @property deadlineMillis 截止时间戳
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 */
class SleeveBloomOfferView(
    val offerId: String,
    val firstTile: Tile,
    val secondTile: Tile,
    val drawKind: SleeveBloomDrawKind,
    val deadlineMillis: Long,
    val decisionId: String,
    val controlEpoch: Long
) {
    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        require(deadlineMillis >= 0L) { "deadlineMillis must be non-negative: $deadlineMillis" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
    }

    operator fun component1(): String = offerId
    operator fun component2(): Tile = firstTile
    operator fun component3(): Tile = secondTile
    operator fun component4(): SleeveBloomDrawKind = drawKind
    operator fun component5(): Long = deadlineMillis
    operator fun component6(): String = decisionId
    operator fun component7(): Long = controlEpoch

    fun copy(
        offerId: String = this.offerId,
        firstTile: Tile = this.firstTile,
        secondTile: Tile = this.secondTile,
        drawKind: SleeveBloomDrawKind = this.drawKind,
        deadlineMillis: Long = this.deadlineMillis,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch
    ): SleeveBloomOfferView = SleeveBloomOfferView(
        offerId = offerId,
        firstTile = firstTile,
        secondTile = secondTile,
        drawKind = drawKind,
        deadlineMillis = deadlineMillis,
        decisionId = decisionId,
        controlEpoch = controlEpoch
    )

    fun deepCopy(): SleeveBloomOfferView = copy()

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SleeveBloomOfferView) return false
        return offerId == other.offerId &&
                firstTile == other.firstTile &&
                secondTile == other.secondTile &&
                drawKind == other.drawKind &&
                deadlineMillis == other.deadlineMillis &&
                decisionId == other.decisionId &&
                controlEpoch == other.controlEpoch
    }

    override fun hashCode(): Int {
        var result = offerId.hashCode()
        result = 31 * result + firstTile.hashCode()
        result = 31 * result + secondTile.hashCode()
        result = 31 * result + drawKind.hashCode()
        result = 31 * result + deadlineMillis.hashCode()
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        return result
    }

    override fun toString(): String {
        return "SleeveBloomOfferView(offerId=$offerId, firstTile=$firstTile, secondTile=$secondTile, drawKind=$drawKind, deadlineMillis=$deadlineMillis, decisionId=$decisionId, controlEpoch=$controlEpoch)"
    }
}

/**
 * 观察者私有袖里藏花视图
 *
 * @property canApply 是否可激活
 * @property disabledReason 禁用原因（不可激活时提供）
 * @property cost 消耗千术点（恒为 10）
 * @property drawKind 当前摸牌来源类型（无摸牌时为 null）
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 * @property stateVersion 单局状态版本
 * @property pendingOffer 挂起的二选一视图（本人有效时提供，他家/未激活为 null）
 */
class SleeveBloomView @JvmOverloads constructor(
    val canApply: Boolean,
    val disabledReason: String?,
    val cost: Int = 10,
    val drawKind: SleeveBloomDrawKind?,
    val decisionId: String,
    val controlEpoch: Long,
    val stateVersion: Long,
    val pendingOffer: SleeveBloomOfferView? = null
) {
    init {
        require(cost == 10) { "cost must be 10, got: $cost" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(stateVersion >= 0L) { "stateVersion must be non-negative: $stateVersion" }
    }

    operator fun component1(): Boolean = canApply
    operator fun component2(): String? = disabledReason
    operator fun component3(): Int = cost
    operator fun component4(): SleeveBloomDrawKind? = drawKind
    operator fun component5(): String = decisionId
    operator fun component6(): Long = controlEpoch
    operator fun component7(): Long = stateVersion
    operator fun component8(): SleeveBloomOfferView? = pendingOffer

    fun copy(
        canApply: Boolean = this.canApply,
        disabledReason: String? = this.disabledReason,
        cost: Int = this.cost,
        drawKind: SleeveBloomDrawKind? = this.drawKind,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        stateVersion: Long = this.stateVersion,
        pendingOffer: SleeveBloomOfferView? = this.pendingOffer
    ): SleeveBloomView = SleeveBloomView(
        canApply = canApply,
        disabledReason = disabledReason,
        cost = cost,
        drawKind = drawKind,
        decisionId = decisionId,
        controlEpoch = controlEpoch,
        stateVersion = stateVersion,
        pendingOffer = pendingOffer
    )

    fun deepCopy(): SleeveBloomView = copy(
        pendingOffer = pendingOffer?.deepCopy()
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SleeveBloomView) return false
        return canApply == other.canApply &&
                disabledReason == other.disabledReason &&
                cost == other.cost &&
                drawKind == other.drawKind &&
                decisionId == other.decisionId &&
                controlEpoch == other.controlEpoch &&
                stateVersion == other.stateVersion &&
                pendingOffer == other.pendingOffer
    }

    override fun hashCode(): Int {
        var result = canApply.hashCode()
        result = 31 * result + (disabledReason?.hashCode() ?: 0)
        result = 31 * result + cost.hashCode()
        result = 31 * result + (drawKind?.hashCode() ?: 0)
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        result = 31 * result + stateVersion.hashCode()
        result = 31 * result + (pendingOffer?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String {
        return "SleeveBloomView(canApply=$canApply, disabledReason=$disabledReason, cost=$cost, drawKind=$drawKind, decisionId=$decisionId, controlEpoch=$controlEpoch, stateVersion=$stateVersion, pendingOffer=$pendingOffer)"
    }
}

/**
 * 袖里藏花私有投影工厂
 */
object SleeveBloomProjectionFactory {
    fun create(
        observerSeat: Int,
        activeRound: RoundStateMachine?,
        view: PlayerView,
        decision: PlayerDecision,
        controls: List<SeatControlView>,
        timing: DecisionTiming?,
        nowMillis: Long,
        isPaused: Boolean = false,
        config: SleeveBloomConfig,
        economyConfig: CheatEconomyConfig
    ): SleeveBloomView? {
        if (!config.enabled || observerSeat !in 0..3) {
            return null
        }

        val cost = economyConfig.costs["SLEEVE_BLOOM"] ?: 10
        val stateVersion = activeRound?.version ?: 0L

        if (activeRound == null || activeRound.state.phase == GamePhase.ROUND_END) {
            return SleeveBloomView(
                canApply = false,
                disabledReason = "NO_ACTIVE_ROUND",
                cost = cost,
                drawKind = null,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                stateVersion = stateVersion,
                pendingOffer = null
            )
        }

        val state = activeRound.readState()
        val offer = state.sleeveBloomOffer

        val pendingOffer = if (offer != null && offer.seat == observerSeat) {
            SleeveBloomOfferView(
                offerId = offer.offerId,
                firstTile = offer.firstTile,
                secondTile = offer.secondTile,
                drawKind = offer.drawKind,
                deadlineMillis = timing?.deadlineMillis ?: 0L,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch
            )
        } else {
            null
        }

        val drawKind = if (state.players[observerSeat].drawnTile != null && state.currentPlayer == observerSeat) {
            state.lastDrawKind
        } else {
            null
        }

        val ctrl = controls.find { it.seat == observerSeat }
        val isHuman = ctrl != null && ctrl.role == "HUMAN" && ctrl.connected

        val drawn = state.players[observerSeat].drawnTile
        val drawnInHand = drawn != null && state.tilePool.getLocation(drawn) == TileLocation.hand(observerSeat)
        val normalValid = state.wallRemaining >= 1 &&
            state.wallPointer < state.wallEndExclusive &&
            drawnInHand &&
            state.wall.getOrNull(state.wallPointer)?.let { state.tilePool.getLocation(it) == TileLocation.WALL } == true
        val rinshanValid = state.rinshanPointer in 1..4 &&
            drawnInHand &&
            state.deadWall.getOrNull(state.rinshanPointer - 1) == drawn &&
            state.deadWallReplacements.isNotEmpty() &&
            state.tilePool.getLocation(state.deadWallReplacements.last()) == TileLocation.DEAD_WALL

        val canApply: Boolean
        val disabledReason: String?

        if (isPaused) {
            canApply = false
            disabledReason = "HOST_PAUSED"
        } else if (pendingOffer != null) {
            canApply = false
            disabledReason = "SLEEVE_BLOOM_ALREADY_PENDING"
        } else if (!isHuman) {
            canApply = false
            disabledReason = "ACTION_NOT_AVAILABLE"
        } else if (state.currentPlayer != observerSeat) {
            canApply = false
            disabledReason = "NOT_YOUR_TURN"
        } else if (state.phase != GamePhase.CHEAT_WINDOW || activeRound.responseWindow != null) {
            canApply = false
            disabledReason = "WRONG_PHASE"
        } else if (drawn == null || !drawnInHand) {
            canApply = false
            disabledReason = "SLEEVE_BLOOM_NO_DRAWN_TILE"
        } else if (state.riichiDeclared[observerSeat]) {
            canApply = false
            disabledReason = "SLEEVE_BLOOM_RIICHI_LOCKED"
        } else if (state.lastDrawKind == SleeveBloomDrawKind.DEAL_EXTRA) {
            canApply = false
            disabledReason = "SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED"
        } else if (state.sleeveBloomUsedOnTurn == state.turnNumber) {
            canApply = false
            disabledReason = "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW"
        } else if (state.lastDrawKind == SleeveBloomDrawKind.NORMAL && !normalValid) {
            canApply = false
            disabledReason = "SLEEVE_BLOOM_WALL_EMPTY"
        } else if (state.lastDrawKind == SleeveBloomDrawKind.RINSHAN && !rinshanValid) {
            canApply = false
            disabledReason = "SLEEVE_BLOOM_WALL_EMPTY"
        } else if (state.cheatPoints[observerSeat] < cost) {
            canApply = false
            disabledReason = "INSUFFICIENT_FUNDS"
        } else if (timing == null) {
            canApply = false
            disabledReason = "NO_ACTIVE_DECISION"
        } else if (nowMillis >= timing.deadlineMillis) {
            canApply = false
            disabledReason = "DEADLINE_EXPIRED"
        } else {
            canApply = true
            disabledReason = null
        }

        return SleeveBloomView(
            canApply = canApply,
            disabledReason = disabledReason,
            cost = cost,
            drawKind = drawKind,
            decisionId = decision.decisionId,
            controlEpoch = decision.controlEpoch,
            stateVersion = stateVersion,
            pendingOffer = pendingOffer
        )
    }
}
