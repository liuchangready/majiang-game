package com.majiang.transport.api

/**
 * 传输层独立袖里藏花千术配置 DTO (ProtocolSleeveBloomConfig)
 *
 * @property enabled 是否启用袖里藏花功能（默认关闭）
 * @property offTurnChoiceMillis 他家回合预定的权威二选一截止时长（默认 10 秒）
 */
data class ProtocolSleeveBloomConfig @JvmOverloads constructor(
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
 * 传输层独立最近一次摸牌来源类型枚举 (ProtocolSleeveBloomDrawKind)
 */
enum class ProtocolSleeveBloomDrawKind {
    DEAL_EXTRA,
    NORMAL,
    RINSHAN
}

/**
 * 传输层独立二选一保留目标枚举 (ProtocolSleeveBloomKeep)
 */
enum class ProtocolSleeveBloomKeep {
    FIRST,
    SECOND
}

/**
 * 传输层独立 offer 使用路径枚举 (ProtocolSleeveBloomOfferMode)
 */
enum class ProtocolSleeveBloomOfferMode {
    IMMEDIATE,
    OFF_TURN
}

/**
 * 传输层独立他家回合预定激活请求 DTO (ProtocolSleeveBloomOffTurnRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 局标识
 * @property windowId 本人弃牌后时段的权威窗口标识
 * @property controlEpoch 控制代次
 */
class ProtocolSleeveBloomOffTurnRequest(
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
    ): ProtocolSleeveBloomOffTurnRequest = ProtocolSleeveBloomOffTurnRequest(
        requestId, sessionId, roundId, windowId, controlEpoch
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolSleeveBloomOffTurnRequest) return false
        return requestId == other.requestId && sessionId == other.sessionId &&
            roundId == other.roundId && windowId == other.windowId &&
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

    override fun toString(): String =
        "ProtocolSleeveBloomOffTurnRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, windowId=$windowId, controlEpoch=$controlEpoch)"
}

/**
 * 传输层独立他家回合预定二选一请求 DTO (ProtocolSleeveBloomOffTurnChoiceRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 局标识
 * @property controlEpoch 当前控制代次
 * @property offerId 挂起的预定二选一标识
 * @property keep 保留目标
 */
class ProtocolSleeveBloomOffTurnChoiceRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val controlEpoch: Long,
    val offerId: String,
    val keep: ProtocolSleeveBloomKeep
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
    operator fun component6(): ProtocolSleeveBloomKeep = keep

    fun copy(
        requestId: String = this.requestId,
        sessionId: String = this.sessionId,
        roundId: String = this.roundId,
        controlEpoch: Long = this.controlEpoch,
        offerId: String = this.offerId,
        keep: ProtocolSleeveBloomKeep = this.keep
    ): ProtocolSleeveBloomOffTurnChoiceRequest = ProtocolSleeveBloomOffTurnChoiceRequest(
        requestId, sessionId, roundId, controlEpoch, offerId, keep
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolSleeveBloomOffTurnChoiceRequest) return false
        return requestId == other.requestId && sessionId == other.sessionId &&
            roundId == other.roundId && controlEpoch == other.controlEpoch &&
            offerId == other.offerId && keep == other.keep
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

    override fun toString(): String =
        "ProtocolSleeveBloomOffTurnChoiceRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, controlEpoch=$controlEpoch, offerId=$offerId, keep=$keep)"
}

/**
 * 传输层独立激活袖里藏花请求 DTO (ProtocolSleeveBloomRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 */
class ProtocolSleeveBloomRequest(
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
    ): ProtocolSleeveBloomRequest = ProtocolSleeveBloomRequest(
        requestId, sessionId, roundId, decisionId, controlEpoch
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolSleeveBloomRequest) return false
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

    override fun toString(): String =
        "ProtocolSleeveBloomRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch)"
}

/**
 * 传输层独立袖里藏花二选一决策请求 DTO (ProtocolSleeveBloomChoiceRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 * @property offerId 挂起的二选一唯一标识
 * @property keep 选择保留的目标候选 (FIRST / SECOND)
 */
class ProtocolSleeveBloomChoiceRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val decisionId: String,
    val controlEpoch: Long,
    val offerId: String,
    val keep: ProtocolSleeveBloomKeep
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
    operator fun component7(): ProtocolSleeveBloomKeep = keep

    fun copy(
        requestId: String = this.requestId,
        sessionId: String = this.sessionId,
        roundId: String = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        offerId: String = this.offerId,
        keep: ProtocolSleeveBloomKeep = this.keep
    ): ProtocolSleeveBloomChoiceRequest = ProtocolSleeveBloomChoiceRequest(
        requestId, sessionId, roundId, decisionId, controlEpoch, offerId, keep
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolSleeveBloomChoiceRequest) return false
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

    override fun toString(): String =
        "ProtocolSleeveBloomChoiceRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, offerId=$offerId, keep=$keep)"
}

/**
 * 传输层独立观察者私有二选一挂起视图 DTO (ProtocolSleeveBloomOfferView)
 *
 * @property offerId 挂起二选一标识
 * @property firstTile 第一候选牌
 * @property secondTile 第二候选牌
 * @property drawKind 摸牌来源
 * @property deadlineMillis 截止时间戳
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 */
class ProtocolSleeveBloomOfferView @JvmOverloads constructor(
    val offerId: String,
    val firstTile: ProtocolTile,
    val secondTile: ProtocolTile,
    val drawKind: ProtocolSleeveBloomDrawKind,
    val deadlineMillis: Long,
    val decisionId: String,
    val controlEpoch: Long,
    val mode: ProtocolSleeveBloomOfferMode = ProtocolSleeveBloomOfferMode.IMMEDIATE,
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
        require(drawKind != ProtocolSleeveBloomDrawKind.DEAL_EXTRA) {
            "pendingOffer drawKind cannot be DEAL_EXTRA: $drawKind"
        }
        if (mode == ProtocolSleeveBloomOfferMode.OFF_TURN) {
            require(drawKind == ProtocolSleeveBloomDrawKind.NORMAL) { "off-turn offer must be NORMAL" }
            require(!windowId.isNullOrBlank()) { "off-turn offer view requires windowId" }
            require(!firstCanTsumo && !secondCanTsumo && !firstCanKan && !secondCanKan) {
                "off-turn offer cannot carry immediate tsumo/kan previews"
            }
        }
    }

    operator fun component1(): String = offerId
    operator fun component2(): ProtocolTile = firstTile
    operator fun component3(): ProtocolTile = secondTile
    operator fun component4(): ProtocolSleeveBloomDrawKind = drawKind
    operator fun component5(): Long = deadlineMillis
    operator fun component6(): String = decisionId
    operator fun component7(): Long = controlEpoch
    operator fun component8(): ProtocolSleeveBloomOfferMode = mode
    operator fun component9(): String? = windowId
    operator fun component10(): Boolean = firstCanTsumo
    operator fun component11(): Boolean = secondCanTsumo
    operator fun component12(): Boolean = firstCanKan
    operator fun component13(): Boolean = secondCanKan

    fun copy(
        offerId: String = this.offerId,
        firstTile: ProtocolTile = this.firstTile,
        secondTile: ProtocolTile = this.secondTile,
        drawKind: ProtocolSleeveBloomDrawKind = this.drawKind,
        deadlineMillis: Long = this.deadlineMillis,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        mode: ProtocolSleeveBloomOfferMode = this.mode,
        windowId: String? = this.windowId,
        firstCanTsumo: Boolean = this.firstCanTsumo,
        secondCanTsumo: Boolean = this.secondCanTsumo,
        firstCanKan: Boolean = this.firstCanKan,
        secondCanKan: Boolean = this.secondCanKan
    ): ProtocolSleeveBloomOfferView = ProtocolSleeveBloomOfferView(
        offerId, firstTile, secondTile, drawKind, deadlineMillis, decisionId, controlEpoch, mode, windowId,
        firstCanTsumo, secondCanTsumo, firstCanKan, secondCanKan
    )

    fun deepCopy(): ProtocolSleeveBloomOfferView = copy()

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolSleeveBloomOfferView) return false
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

    override fun toString(): String =
        "ProtocolSleeveBloomOfferView(offerId=$offerId, firstTile=$firstTile, secondTile=$secondTile, drawKind=$drawKind, deadlineMillis=$deadlineMillis, decisionId=$decisionId, controlEpoch=$controlEpoch, mode=$mode, windowId=$windowId, firstCanTsumo=$firstCanTsumo, secondCanTsumo=$secondCanTsumo, firstCanKan=$firstCanKan, secondCanKan=$secondCanKan)"
}

/**
 * 传输层独立观察者私有预留视图 DTO (ProtocolSleeveBloomReservationView)
 *
 * @property tile 已选定、等待本人下次普通摸牌交付的实体牌
 * @property offerId 产生该预留的 offer 标识
 * @property windowId 预定窗口标识
 */
data class ProtocolSleeveBloomReservationView(
    val tile: ProtocolTile,
    val offerId: String,
    val windowId: String?
) {
    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
    }

    fun deepCopy(): ProtocolSleeveBloomReservationView = copy()
}

/**
 * 传输层独立观察者私有袖里藏花视图 DTO (ProtocolSleeveBloomView)
 *
 * @property canApply 是否可激活
 * @property disabledReason 禁用原因（不可激活时提供）
 * @property cost 消耗千术点（恒为 10）
 * @property drawKind 当前摸牌来源类型（无摸牌时为 null）
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 * @property stateVersion 单局状态版本
 * @property pendingOffer 挂起的二选一视图（本人有效时提供，他家/未激活为 null）
 * @property canApplyOffTurn 他家回合是否可预定
 * @property offTurnDisabledReason 他家回合预定禁用原因
 * @property offTurnWindowId 当前有效的他家回合窗口标识
 * @property reservation 已选中的预留牌
 */
class ProtocolSleeveBloomView @JvmOverloads constructor(
    val canApply: Boolean,
    val disabledReason: String?,
    val cost: Int = 10,
    val drawKind: ProtocolSleeveBloomDrawKind?,
    val decisionId: String,
    val controlEpoch: Long,
    val stateVersion: Long,
    val pendingOffer: ProtocolSleeveBloomOfferView? = null,
    val canApplyOffTurn: Boolean = false,
    val offTurnDisabledReason: String? = null,
    val offTurnWindowId: String? = null,
    val reservation: ProtocolSleeveBloomReservationView? = null
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
    operator fun component4(): ProtocolSleeveBloomDrawKind? = drawKind
    operator fun component5(): String = decisionId
    operator fun component6(): Long = controlEpoch
    operator fun component7(): Long = stateVersion
    operator fun component8(): ProtocolSleeveBloomOfferView? = pendingOffer
    operator fun component9(): Boolean = canApplyOffTurn
    operator fun component10(): String? = offTurnDisabledReason
    operator fun component11(): String? = offTurnWindowId
    operator fun component12(): ProtocolSleeveBloomReservationView? = reservation

    fun copy(
        canApply: Boolean = this.canApply,
        disabledReason: String? = this.disabledReason,
        cost: Int = this.cost,
        drawKind: ProtocolSleeveBloomDrawKind? = this.drawKind,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        stateVersion: Long = this.stateVersion,
        pendingOffer: ProtocolSleeveBloomOfferView? = this.pendingOffer,
        canApplyOffTurn: Boolean = this.canApplyOffTurn,
        offTurnDisabledReason: String? = this.offTurnDisabledReason,
        offTurnWindowId: String? = this.offTurnWindowId,
        reservation: ProtocolSleeveBloomReservationView? = this.reservation
    ): ProtocolSleeveBloomView = ProtocolSleeveBloomView(
        canApply, disabledReason, cost, drawKind, decisionId, controlEpoch, stateVersion,
        pendingOffer, canApplyOffTurn, offTurnDisabledReason, offTurnWindowId, reservation
    )

    fun deepCopy(): ProtocolSleeveBloomView = copy(
        pendingOffer = pendingOffer?.deepCopy(),
        reservation = reservation?.deepCopy()
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolSleeveBloomView) return false
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

    override fun toString(): String =
        "ProtocolSleeveBloomView(canApply=$canApply, disabledReason=$disabledReason, cost=$cost, drawKind=$drawKind, decisionId=$decisionId, controlEpoch=$controlEpoch, stateVersion=$stateVersion, pendingOffer=$pendingOffer, canApplyOffTurn=$canApplyOffTurn, offTurnDisabledReason=$offTurnDisabledReason, offTurnWindowId=$offTurnWindowId, reservation=$reservation)"
}
