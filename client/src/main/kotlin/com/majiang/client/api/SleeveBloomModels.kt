package com.majiang.client.api

/**
 * 客户端独立袖里藏花配置模型 (SleeveBloomConfig)
 *
 * @property enabled 是否启用袖里藏花功能（默认关闭）
 * @property offTurnChoiceMillis 他家回合预定的权威二选一截止时长（默认 10 秒）
 */
data class SleeveBloomConfig @JvmOverloads constructor(
    val enabled: Boolean = false,
    val offTurnChoiceMillis: Long = 10_000L
) {
    init {
        require(offTurnChoiceMillis > 0L) { "offTurnChoiceMillis must be positive: $offTurnChoiceMillis" }
    }
}

/**
 * 客户端独立摸牌来源枚举 (SleeveBloomDrawKind)
 */
enum class SleeveBloomDrawKind {
    DEAL_EXTRA, // 庄家起手第 14 张发牌（禁止施术）
    NORMAL,     // 正常活牌山摸牌
    RINSHAN     // 杠后岭上补牌
}

/**
 * 客户端独立二选一保留目标枚举 (SleeveBloomKeep)
 */
enum class SleeveBloomKeep {
    FIRST,  // 保留第一张（原摸到/补到的牌）
    SECOND  // 保留第二张（活牌山头端次张/王牌区替补牌）
}

/**
 * 客户端独立 offer 使用路径枚举 (SleeveBloomOfferMode)
 */
enum class SleeveBloomOfferMode {
    IMMEDIATE,
    OFF_TURN
}

/**
 * 客户端独立激活袖里藏花请求模型 (SleeveBloomRequest)
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

    fun deepCopy(): SleeveBloomRequest = copy()

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
 * 客户端独立二选一选择请求模型 (SleeveBloomChoiceRequest)
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

    fun deepCopy(): SleeveBloomChoiceRequest = copy()

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
 * 客户端他家回合预定袖里藏花激活请求 (SleeveBloomOffTurnRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 单局标识
 * @property windowId 本人弃牌后时段的权威窗口标识
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
        requestId, sessionId, roundId, windowId, controlEpoch
    )

    fun deepCopy(): SleeveBloomOffTurnRequest = copy()

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SleeveBloomOffTurnRequest) return false
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
        "SleeveBloomOffTurnRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, windowId=$windowId, controlEpoch=$controlEpoch)"
}

/**
 * 客户端他家回合预定二选一请求 (SleeveBloomOffTurnChoiceRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 单局标识
 * @property controlEpoch 当前控制代次
 * @property offerId 挂起的预定二选一标识
 * @property keep 保留目标
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
        requestId, sessionId, roundId, controlEpoch, offerId, keep
    )

    fun deepCopy(): SleeveBloomOffTurnChoiceRequest = copy()

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SleeveBloomOffTurnChoiceRequest) return false
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
        "SleeveBloomOffTurnChoiceRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, controlEpoch=$controlEpoch, offerId=$offerId, keep=$keep)"
}

/**
 * 客户端独立挂起二选一视图模型 (SleeveBloomOfferView)
 *
 * @property offerId 挂起二选一标识
 * @property firstTile 第一候选牌
 * @property secondTile 第二候选牌
 * @property drawKind 摸牌来源
 * @property deadlineMillis 截止时间戳
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 */
class SleeveBloomOfferView @JvmOverloads constructor(
    val offerId: String,
    val firstTile: TileView,
    val secondTile: TileView,
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
        require(drawKind != SleeveBloomDrawKind.DEAL_EXTRA) { "dealer deal tile cannot be offered" }
        require(deadlineMillis >= 0L) { "deadlineMillis must be non-negative: $deadlineMillis" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        if (mode == SleeveBloomOfferMode.OFF_TURN) {
            require(drawKind == SleeveBloomDrawKind.NORMAL) { "off-turn offer must be NORMAL" }
            require(!windowId.isNullOrBlank()) { "off-turn offer view requires windowId" }
            require(!firstCanTsumo && !secondCanTsumo && !firstCanKan && !secondCanKan) {
                "off-turn offer cannot carry immediate tsumo/kan previews"
            }
        }
    }

    operator fun component1(): String = offerId
    operator fun component2(): TileView = firstTile
    operator fun component3(): TileView = secondTile
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
        firstTile: TileView = this.firstTile,
        secondTile: TileView = this.secondTile,
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

    fun deepCopy(): SleeveBloomOfferView = copy(
        firstTile = firstTile.copy(),
        secondTile = secondTile.copy()
    )

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
 * 客户端独立预留视图模型 (SleeveBloomReservationView)
 *
 * @property tile 已选定、等待本人下次普通摸牌交付的实体牌
 * @property offerId 产生该预留的 offer 标识
 * @property windowId 预定窗口标识
 */
class SleeveBloomReservationView(
    val tile: TileView,
    val offerId: String,
    val windowId: String?
) {
    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
    }

    fun deepCopy(): SleeveBloomReservationView =
        SleeveBloomReservationView(tile = tile.copy(), offerId = offerId, windowId = windowId)

    override fun equals(other: Any?): Boolean =
        this === other || (other is SleeveBloomReservationView &&
            tile == other.tile && offerId == other.offerId && windowId == other.windowId)

    override fun hashCode(): Int {
        var result = tile.hashCode()
        result = 31 * result + offerId.hashCode()
        result = 31 * result + (windowId?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "SleeveBloomReservationView(tile=$tile, offerId=$offerId, windowId=$windowId)"
}

/**
 * 客户端独立袖里藏花视图模型 (SleeveBloomView)
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
        pendingOffer = pendingOffer?.deepCopy(),
        reservation = reservation?.deepCopy()
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
