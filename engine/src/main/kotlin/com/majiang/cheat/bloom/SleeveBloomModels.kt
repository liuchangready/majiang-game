package com.majiang.cheat.bloom

import com.majiang.access.SeatControlView
import com.majiang.application.PlayerDecision
import com.majiang.economy.CheatEconomyConfig
import com.majiang.model.Tile
import com.majiang.model.TileLocation
import com.majiang.round.RoundStateMachine
import com.majiang.session.PlayerView
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.timing.DecisionTiming

/**
 * 袖里藏花千术配置
 *
 * @property enabled 是否启用袖里藏花功能（默认关闭）
 * @property offTurnChoiceMillis 他家回合预定的权威二选一截止时长（默认 10 秒）
 */
data class SleeveBloomConfig @JvmOverloads constructor(
    val enabled: Boolean = false,
    val offTurnChoiceMillis: Long = 10_000L
) {
    init {
        require(offTurnChoiceMillis > 0L) {
            "offTurnChoiceMillis must be positive, got: $offTurnChoiceMillis"
        }
    }
}

/**
 * 最近一次摸牌来源类型
 */
enum class SleeveBloomDrawKind {
    DEAL_EXTRA, // 庄家起手第 14 张发牌（禁止施术）
    NORMAL,     // 正常活牌山摸牌
    RINSHAN     // 杠后岭上补牌
}

/**
 * 袖里藏花 offer 的使用路径
 */
enum class SleeveBloomOfferMode {
    /** 本人摸牌后的即时二选一 */
    IMMEDIATE,
    /** 本人弃牌后他家回合的预定二选一 */
    OFF_TURN
}

/**
 * 袖里藏花二选一保留目标
 */
enum class SleeveBloomKeep {
    FIRST,  // 保留第一张（即时：原摸到/补到的牌；预定：牌山前一张候选）
    SECOND  // 保留第二张（即时：活牌山头端次张/王牌区替补牌；预定：牌山后一张候选）
}

/**
 * 激活袖里藏花请求（即时路径）
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
 * 他家回合预定袖里藏花的强类型激活请求。
 *
 * 预定请求不依赖他人/本人的即时 decisionId，而绑定权威在本人弃牌后签发的 windowId。
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 单局标识
 * @property windowId 本人本次弃牌后时段的权威窗口标识
 * @property controlEpoch 控制代次
 */
class SleeveBloomOffTurnRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val windowId: String,
    val controlEpoch: Long
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(windowId.isNotBlank()) { "windowId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
    }

    operator fun component1(): String = requestId
    operator fun component2(): String = sessionId
    operator fun component3(): String = roundId
    operator fun component4(): String = windowId
    operator fun component5(): Long = controlEpoch

    fun copy(
        requestId: String = this.requestId,
        sessionId: String = this.sessionId,
        roundId: String = this.roundId,
        windowId: String = this.windowId,
        controlEpoch: Long = this.controlEpoch
    ): SleeveBloomOffTurnRequest = SleeveBloomOffTurnRequest(
        requestId = requestId,
        sessionId = sessionId,
        roundId = roundId,
        windowId = windowId,
        controlEpoch = controlEpoch
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SleeveBloomOffTurnRequest) return false
        return requestId == other.requestId &&
                sessionId == other.sessionId &&
                roundId == other.roundId &&
                windowId == other.windowId &&
                controlEpoch == other.controlEpoch
    }

    override fun hashCode(): Int {
        var result = requestId.hashCode()
        result = 31 * result + sessionId.hashCode()
        result = 31 * result + roundId.hashCode()
        result = 31 * result + windowId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        return result
    }

    override fun toString(): String {
        return "SleeveBloomOffTurnRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, windowId=$windowId, controlEpoch=$controlEpoch)"
    }
}

/**
 * 袖里藏花即时二选一决策请求
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
 * 他家回合预定的二选一决策请求。
 *
 * 预定 offer 不绑定某个人的即时 decisionId；选择只按 offerId、当前控制代次与私有快照身份校验。
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 单局标识
 * @property controlEpoch 当前控制代次
 * @property offerId 挂起的预定二选一唯一标识
 * @property keep 选择保留的目标候选 (FIRST / SECOND)
 */
class SleeveBloomOffTurnChoiceRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val controlEpoch: Long,
    val offerId: String,
    val keep: SleeveBloomKeep
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
    }

    operator fun component1(): String = requestId
    operator fun component2(): String = sessionId
    operator fun component3(): String = roundId
    operator fun component4(): Long = controlEpoch
    operator fun component5(): String = offerId
    operator fun component6(): SleeveBloomKeep = keep

    fun copy(
        requestId: String = this.requestId,
        sessionId: String = this.sessionId,
        roundId: String = this.roundId,
        controlEpoch: Long = this.controlEpoch,
        offerId: String = this.offerId,
        keep: SleeveBloomKeep = this.keep
    ): SleeveBloomOffTurnChoiceRequest = SleeveBloomOffTurnChoiceRequest(
        requestId = requestId,
        sessionId = sessionId,
        roundId = roundId,
        controlEpoch = controlEpoch,
        offerId = offerId,
        keep = keep
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SleeveBloomOffTurnChoiceRequest) return false
        return requestId == other.requestId &&
                sessionId == other.sessionId &&
                roundId == other.roundId &&
                controlEpoch == other.controlEpoch &&
                offerId == other.offerId &&
                keep == other.keep
    }

    override fun hashCode(): Int {
        var result = requestId.hashCode()
        result = 31 * result + sessionId.hashCode()
        result = 31 * result + roundId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        result = 31 * result + offerId.hashCode()
        result = 31 * result + keep.hashCode()
        return result
    }

    override fun toString(): String {
        return "SleeveBloomOffTurnChoiceRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, controlEpoch=$controlEpoch, offerId=$offerId, keep=$keep)"
    }
}

/**
 * 挂起的袖里藏花二选一领域状态
 *
 * @property offerId 唯一标识 (bloom-$seat-$requestId)
 * @property seat 所属玩家席位 (0..3)
 * @property drawKind 摸牌来源类型
 * @property firstTile 第一候选牌（即时：原摸到/补到的牌；预定：牌山前一张候选）
 * @property secondTile 第二候选牌（即时：次张；预定：牌山后一张候选）
 * @property createdTurnNumber 创建时的摸牌巡目 (turnNumber)
 * @property mode 使用路径（即时 / 他家回合预定）
 * @property windowId 预定路径绑定的权威窗口标识
 * @property deadlineMillis 预定路径的权威选择截止；即时路径为 0（复用决策时钟）
 * @property createdAtMillis 预定激活时权威时间
 */
data class SleeveBloomOffer(
    val offerId: String,
    val seat: Int,
    val drawKind: SleeveBloomDrawKind,
    val firstTile: Tile,
    val secondTile: Tile,
    val createdTurnNumber: Int,
    val mode: SleeveBloomOfferMode = SleeveBloomOfferMode.IMMEDIATE,
    val windowId: String? = null,
    val deadlineMillis: Long = 0L,
    val createdAtMillis: Long = 0L,
    /** 第一候选成为实际摸牌后是否可自摸（仅即时 offer 计算） */
    val firstCanTsumo: Boolean = false,
    /** 第二候选成为实际摸牌后是否可自摸（仅即时 offer 计算） */
    val secondCanTsumo: Boolean = false,
    /** 第一候选成为实际摸牌后是否存在合法杠（仅即时 offer 计算） */
    val firstCanKan: Boolean = false,
    /** 第二候选成为实际摸牌后是否存在合法杠（仅即时 offer 计算） */
    val secondCanKan: Boolean = false
) {
    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        require(seat in 0..3) { "seat must be in 0..3: $seat" }
        require(createdTurnNumber >= 0) { "createdTurnNumber must be non-negative: $createdTurnNumber" }
        require(deadlineMillis >= 0L) { "deadlineMillis must be non-negative: $deadlineMillis" }
        require(createdAtMillis >= 0L) { "createdAtMillis must be non-negative: $createdAtMillis" }
        when (mode) {
            SleeveBloomOfferMode.IMMEDIATE -> {
                require(drawKind != SleeveBloomDrawKind.DEAL_EXTRA) {
                    "immediate offer cannot use DEAL_EXTRA draw kind"
                }
            }
            SleeveBloomOfferMode.OFF_TURN -> {
                require(drawKind == SleeveBloomDrawKind.NORMAL) {
                    "off-turn offer must use NORMAL draw kind, got: $drawKind"
                }
                require(!windowId.isNullOrBlank()) { "off-turn offer requires windowId" }
                require(firstTile != secondTile) { "off-turn offer candidates must be distinct" }
                require(!firstCanTsumo && !secondCanTsumo && !firstCanKan && !secondCanKan) {
                    "off-turn offer cannot carry immediate tsumo/kan previews"
                }
            }
        }
    }
}

/**
 * 已选中的活牌山预留实体。
 *
 * @property seat 预定者席位
 * @property offerId 产生该预留的 offer 标识
 * @property tile 预留实体牌
 * @property windowId 预定窗口标识
 * @property createdAtMillis 选择完成时的权威时间
 */
data class SleeveBloomReservation(
    val seat: Int,
    val offerId: String,
    val tile: Tile,
    val windowId: String?,
    val createdAtMillis: Long
) {
    init {
        require(seat in 0..3) { "seat must be in 0..3: $seat" }
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        require(createdAtMillis >= 0L) { "createdAtMillis must be non-negative: $createdAtMillis" }
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
 * @property mode 使用路径
 * @property windowId 预定窗口标识（仅 OFF_TURN）
 */
class SleeveBloomOfferView @JvmOverloads constructor(
    val offerId: String,
    val firstTile: Tile,
    val secondTile: Tile,
    val drawKind: SleeveBloomDrawKind,
    val deadlineMillis: Long,
    val decisionId: String,
    val controlEpoch: Long,
    val mode: SleeveBloomOfferMode = SleeveBloomOfferMode.IMMEDIATE,
    val windowId: String? = null,
    val firstCanTsumo: Boolean = false,
    val secondCanTsumo: Boolean = false,
    val firstCanKan: Boolean = false,
    val secondCanKan: Boolean = false
) {
    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        require(deadlineMillis >= 0L) { "deadlineMillis must be non-negative: $deadlineMillis" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(drawKind != SleeveBloomDrawKind.DEAL_EXTRA) {
            "pendingOffer drawKind cannot be DEAL_EXTRA: $drawKind"
        }
        if (mode == SleeveBloomOfferMode.OFF_TURN) {
            require(drawKind == SleeveBloomDrawKind.NORMAL) { "off-turn offer must be NORMAL" }
            require(!windowId.isNullOrBlank()) { "off-turn offer view requires windowId" }
        }
    }

    operator fun component1(): String = offerId
    operator fun component2(): Tile = firstTile
    operator fun component3(): Tile = secondTile
    operator fun component4(): SleeveBloomDrawKind = drawKind
    operator fun component5(): Long = deadlineMillis
    operator fun component6(): String = decisionId
    operator fun component7(): Long = controlEpoch
    operator fun component8(): SleeveBloomOfferMode = mode
    operator fun component9(): String? = windowId
    operator fun component10(): Boolean = firstCanTsumo
    operator fun component11(): Boolean = secondCanTsumo
    operator fun component12(): Boolean = firstCanKan
    operator fun component13(): Boolean = secondCanKan

    fun copy(
        offerId: String = this.offerId,
        firstTile: Tile = this.firstTile,
        secondTile: Tile = this.secondTile,
        drawKind: SleeveBloomDrawKind = this.drawKind,
        deadlineMillis: Long = this.deadlineMillis,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        mode: SleeveBloomOfferMode = this.mode,
        windowId: String? = this.windowId,
        firstCanTsumo: Boolean = this.firstCanTsumo,
        secondCanTsumo: Boolean = this.secondCanTsumo,
        firstCanKan: Boolean = this.firstCanKan,
        secondCanKan: Boolean = this.secondCanKan
    ): SleeveBloomOfferView = SleeveBloomOfferView(
        offerId = offerId,
        firstTile = firstTile,
        secondTile = secondTile,
        drawKind = drawKind,
        deadlineMillis = deadlineMillis,
        decisionId = decisionId,
        controlEpoch = controlEpoch,
        mode = mode,
        windowId = windowId,
        firstCanTsumo = firstCanTsumo,
        secondCanTsumo = secondCanTsumo,
        firstCanKan = firstCanKan,
        secondCanKan = secondCanKan
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
                controlEpoch == other.controlEpoch &&
                mode == other.mode &&
                windowId == other.windowId &&
                firstCanTsumo == other.firstCanTsumo &&
                secondCanTsumo == other.secondCanTsumo &&
                firstCanKan == other.firstCanKan &&
                secondCanKan == other.secondCanKan
    }

    override fun hashCode(): Int {
        var result = offerId.hashCode()
        result = 31 * result + firstTile.hashCode()
        result = 31 * result + secondTile.hashCode()
        result = 31 * result + drawKind.hashCode()
        result = 31 * result + deadlineMillis.hashCode()
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        result = 31 * result + mode.hashCode()
        result = 31 * result + (windowId?.hashCode() ?: 0)
        result = 31 * result + firstCanTsumo.hashCode()
        result = 31 * result + secondCanTsumo.hashCode()
        result = 31 * result + firstCanKan.hashCode()
        result = 31 * result + secondCanKan.hashCode()
        return result
    }

    override fun toString(): String {
        return "SleeveBloomOfferView(offerId=$offerId, firstTile=$firstTile, secondTile=$secondTile, drawKind=$drawKind, deadlineMillis=$deadlineMillis, decisionId=$decisionId, controlEpoch=$controlEpoch, mode=$mode, windowId=$windowId, firstCanTsumo=$firstCanTsumo, secondCanTsumo=$secondCanTsumo, firstCanKan=$firstCanKan, secondCanKan=$secondCanKan)"
    }
}

/**
 * 观察者私有预留视图
 *
 * @property tile 已选定、等待本人下次普通摸牌交付的实体牌
 * @property offerId 产生该预留的 offer 标识
 * @property windowId 预定窗口标识
 */
class SleeveBloomReservationView(
    val tile: Tile,
    val offerId: String,
    val windowId: String?
) {
    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
    }
}

/**
 * 观察者私有袖里藏花视图
 *
 * @property canApply 是否可即时激活
 * @property disabledReason 即时禁用原因
 * @property cost 消耗千术点（恒为 10）
 * @property drawKind 当前摸牌来源类型（无摸牌时为 null）
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 * @property stateVersion 单局状态版本
 * @property pendingOffer 挂起的二选一视图（本人有效时提供，他家/未激活为 null）
 * @property canApplyOffTurn 他家回合是否可预定
 * @property offTurnDisabledReason 他家回合预定禁用原因
 * @property offTurnWindowId 当前有效的他家回合窗口标识
 * @property reservation 已选中的预留牌（等待本人下次普通摸牌）
 */
class SleeveBloomView @JvmOverloads constructor(
    val canApply: Boolean,
    val disabledReason: String?,
    val cost: Int = 10,
    val drawKind: SleeveBloomDrawKind?,
    val decisionId: String,
    val controlEpoch: Long,
    val stateVersion: Long,
    val pendingOffer: SleeveBloomOfferView? = null,
    val canApplyOffTurn: Boolean = false,
    val offTurnDisabledReason: String? = null,
    val offTurnWindowId: String? = null,
    val reservation: SleeveBloomReservationView? = null
) {
    init {
        require(cost == 10) { "cost must be 10, got: $cost" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(stateVersion >= 0L) { "stateVersion must be non-negative: $stateVersion" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        if (pendingOffer != null) {
            require(pendingOffer.decisionId == decisionId) {
                "pendingOffer.decisionId (${pendingOffer.decisionId}) must match view decisionId ($decisionId)"
            }
            require(pendingOffer.controlEpoch == controlEpoch) {
                "pendingOffer.controlEpoch (${pendingOffer.controlEpoch}) must match view controlEpoch ($controlEpoch)"
            }
            if (drawKind != null) {
                require(pendingOffer.drawKind == drawKind) {
                    "pendingOffer.drawKind (${pendingOffer.drawKind}) must match view drawKind ($drawKind)"
                }
            }
        }
    }

    operator fun component1(): Boolean = canApply
    operator fun component2(): String? = disabledReason
    operator fun component3(): Int = cost
    operator fun component4(): SleeveBloomDrawKind? = drawKind
    operator fun component5(): String = decisionId
    operator fun component6(): Long = controlEpoch
    operator fun component7(): Long = stateVersion
    operator fun component8(): SleeveBloomOfferView? = pendingOffer
    operator fun component9(): Boolean = canApplyOffTurn
    operator fun component10(): String? = offTurnDisabledReason
    operator fun component11(): String? = offTurnWindowId
    operator fun component12(): SleeveBloomReservationView? = reservation

    fun copy(
        canApply: Boolean = this.canApply,
        disabledReason: String? = this.disabledReason,
        cost: Int = this.cost,
        drawKind: SleeveBloomDrawKind? = this.drawKind,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        stateVersion: Long = this.stateVersion,
        pendingOffer: SleeveBloomOfferView? = this.pendingOffer,
        canApplyOffTurn: Boolean = this.canApplyOffTurn,
        offTurnDisabledReason: String? = this.offTurnDisabledReason,
        offTurnWindowId: String? = this.offTurnWindowId,
        reservation: SleeveBloomReservationView? = this.reservation
    ): SleeveBloomView = SleeveBloomView(
        canApply = canApply,
        disabledReason = disabledReason,
        cost = cost,
        drawKind = drawKind,
        decisionId = decisionId,
        controlEpoch = controlEpoch,
        stateVersion = stateVersion,
        pendingOffer = pendingOffer,
        canApplyOffTurn = canApplyOffTurn,
        offTurnDisabledReason = offTurnDisabledReason,
        offTurnWindowId = offTurnWindowId,
        reservation = reservation
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
                pendingOffer == other.pendingOffer &&
                canApplyOffTurn == other.canApplyOffTurn &&
                offTurnDisabledReason == other.offTurnDisabledReason &&
                offTurnWindowId == other.offTurnWindowId &&
                reservation == other.reservation
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
        result = 31 * result + canApplyOffTurn.hashCode()
        result = 31 * result + (offTurnDisabledReason?.hashCode() ?: 0)
        result = 31 * result + (offTurnWindowId?.hashCode() ?: 0)
        result = 31 * result + (reservation?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String {
        return "SleeveBloomView(canApply=$canApply, disabledReason=$disabledReason, cost=$cost, drawKind=$drawKind, decisionId=$decisionId, controlEpoch=$controlEpoch, stateVersion=$stateVersion, pendingOffer=$pendingOffer, canApplyOffTurn=$canApplyOffTurn, offTurnDisabledReason=$offTurnDisabledReason, offTurnWindowId=$offTurnWindowId, reservation=$reservation)"
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
                pendingOffer = null,
                canApplyOffTurn = false,
                offTurnDisabledReason = "NO_ACTIVE_ROUND",
                offTurnWindowId = null,
                reservation = null
            )
        }

        val state = activeRound.readState()
        val immediateOffer = state.sleeveBloomOffer
        val offTurnOffer = state.sleeveBloomOffTurnOffer(observerSeat)
        val reservation = state.sleeveBloomReservation(observerSeat)
        val windowId = state.sleeveBloomWindowId(observerSeat)

        val pendingOffer = when {
            immediateOffer != null && immediateOffer.seat == observerSeat -> immediateOfferView(
                immediateOffer, decision, timing, nowMillis
            )
            offTurnOffer != null -> offTurnOfferView(offTurnOffer, decision)
            else -> null
        }

        val reservationView = reservation?.let {
            SleeveBloomReservationView(tile = it.tile, offerId = it.offerId, windowId = it.windowId)
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
        val nextWallTile = state.nextAvailableWallTile()
        val normalValid = nextWallTile != null &&
            drawnInHand &&
            state.tilePool.getLocation(nextWallTile) == TileLocation.WALL
        val rinshanValid = state.rinshanPointer in 1..3 &&
            state.totalKanCount < 4 &&
            drawnInHand &&
            state.deadWall.getOrNull(state.rinshanPointer - 1) == drawn &&
            state.deadWall.getOrNull(state.rinshanPointer)?.let {
                state.tilePool.getLocation(it) == TileLocation.DEAD_WALL
            } == true

        val canApply: Boolean
        val disabledReason: String?

        if (isPaused) {
            canApply = false
            disabledReason = "HOST_PAUSED"
        } else if (immediateOffer != null) {
            canApply = false
            disabledReason = if (immediateOffer.seat == observerSeat) {
                "SLEEVE_BLOOM_ALREADY_PENDING"
            } else {
                "SLEEVE_BLOOM_ALREADY_PENDING"
            }
        } else if (offTurnOffer != null && offTurnOffer.seat == observerSeat) {
            canApply = false
            disabledReason = "SLEEVE_BLOOM_ALREADY_PENDING"
        } else if (state.beamSwapOffer != null) {
            canApply = false
            disabledReason = "BEAM_SWAP_ALREADY_PENDING"
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
        } else if (state.lastDrawKind == SleeveBloomDrawKind.DEAL_EXTRA) {
            canApply = false
            disabledReason = "SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED"
        } else if (state.sleeveBloomUsedOnTurn == state.turnNumber || state.beamSwapUsedOnTurn == state.turnNumber) {
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

        val offTurnPhaseOk = state.phase in setOf(
            GamePhase.DRAW,
            GamePhase.CHEAT_WINDOW,
            GamePhase.DISCARD,
            GamePhase.RESPONSE_WINDOW,
            GamePhase.CHANKAN_WINDOW
        )
        val canApplyOffTurn: Boolean
        val offTurnDisabledReason: String?
        when {
            isPaused -> { canApplyOffTurn = false; offTurnDisabledReason = "HOST_PAUSED" }
            !isHuman -> { canApplyOffTurn = false; offTurnDisabledReason = "ACTION_NOT_AVAILABLE" }
            immediateOffer != null && immediateOffer.seat == observerSeat -> {
                canApplyOffTurn = false; offTurnDisabledReason = "SLEEVE_BLOOM_ALREADY_PENDING"
            }
            offTurnOffer != null -> { canApplyOffTurn = false; offTurnDisabledReason = "SLEEVE_BLOOM_ALREADY_PENDING" }
            reservation != null -> { canApplyOffTurn = false; offTurnDisabledReason = "SLEEVE_BLOOM_RESERVED_PENDING" }
            windowId == null -> { canApplyOffTurn = false; offTurnDisabledReason = "SLEEVE_BLOOM_NO_WINDOW" }
            state.currentPlayer == observerSeat -> { canApplyOffTurn = false; offTurnDisabledReason = "NOT_YOUR_TURN" }
            !offTurnPhaseOk -> { canApplyOffTurn = false; offTurnDisabledReason = "WRONG_PHASE" }
            state.availableWallTileCount() < 2 -> { canApplyOffTurn = false; offTurnDisabledReason = "SLEEVE_BLOOM_WALL_EMPTY" }
            state.cheatPoints[observerSeat] < cost -> { canApplyOffTurn = false; offTurnDisabledReason = "INSUFFICIENT_FUNDS" }
            else -> { canApplyOffTurn = true; offTurnDisabledReason = null }
        }

        return SleeveBloomView(
            canApply = canApply,
            disabledReason = disabledReason,
            cost = cost,
            drawKind = drawKind,
            decisionId = decision.decisionId,
            controlEpoch = decision.controlEpoch,
            stateVersion = stateVersion,
            pendingOffer = pendingOffer,
            canApplyOffTurn = canApplyOffTurn,
            offTurnDisabledReason = offTurnDisabledReason,
            offTurnWindowId = windowId,
            reservation = reservationView
        )
    }

    private fun immediateOfferView(
        offer: SleeveBloomOffer,
        decision: PlayerDecision,
        timing: DecisionTiming?,
        nowMillis: Long
    ): SleeveBloomOfferView = SleeveBloomOfferView(
        offerId = offer.offerId,
        firstTile = offer.firstTile,
        secondTile = offer.secondTile,
        drawKind = offer.drawKind,
        deadlineMillis = timing?.deadlineMillis ?: nowMillis,
        decisionId = decision.decisionId,
        controlEpoch = decision.controlEpoch,
        mode = SleeveBloomOfferMode.IMMEDIATE,
        windowId = null,
        firstCanTsumo = offer.firstCanTsumo,
        secondCanTsumo = offer.secondCanTsumo,
        firstCanKan = offer.firstCanKan,
        secondCanKan = offer.secondCanKan
    )

    private fun offTurnOfferView(
        offer: SleeveBloomOffer,
        decision: PlayerDecision
    ): SleeveBloomOfferView = SleeveBloomOfferView(
        offerId = offer.offerId,
        firstTile = offer.firstTile,
        secondTile = offer.secondTile,
        drawKind = offer.drawKind,
        deadlineMillis = offer.deadlineMillis,
        decisionId = decision.decisionId,
        controlEpoch = decision.controlEpoch,
        mode = SleeveBloomOfferMode.OFF_TURN,
        windowId = offer.windowId
    )
}
