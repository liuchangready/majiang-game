package com.majiang.transport.api

import java.util.Collections

/**
 * 传输层独立偷梁换柱配置 DTO (ProtocolBeamSwapConfig)
 *
 * @property enabled 是否启用偷梁换柱功能（默认关闭）
 */
data class ProtocolBeamSwapConfig(
    val enabled: Boolean = false
)

/**
 * 传输层独立偷梁换柱窗口类型枚举 (ProtocolBeamSwapWindow)
 */
enum class ProtocolBeamSwapWindow {
    WALL,
    DEAD_WALL
}

/**
 * 传输层独立偷梁换柱动作分支枚举 (ProtocolBeamSwapAction)
 */
enum class ProtocolBeamSwapAction {
    SWAP,
    KEEP_ORIGINAL
}

/**
 * 传输层独立偷梁换柱槽位密封类 (ProtocolBeamSwapSlot)
 */
sealed class ProtocolBeamSwapSlot {
    abstract val index: Int

    data class WallSlot(override val index: Int) : ProtocolBeamSwapSlot() {
        init {
            require(index >= 0) { "WallSlot index must be non-negative: $index" }
        }
    }

    data class DeadWallSlot(override val index: Int) : ProtocolBeamSwapSlot() {
        init {
            require(index >= 0) { "DeadWallSlot index must be non-negative: $index" }
        }
    }

    data class ReplacementSlot(override val index: Int) : ProtocolBeamSwapSlot() {
        init {
            require(index >= 0) { "ReplacementSlot index must be non-negative: $index" }
        }
    }
}

/**
 * 传输层独立激活偷梁换柱请求 DTO (ProtocolBeamSwapRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 */
class ProtocolBeamSwapRequest(
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
    ): ProtocolBeamSwapRequest = ProtocolBeamSwapRequest(
        requestId, sessionId, roundId, decisionId, controlEpoch
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolBeamSwapRequest) return false
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
        "ProtocolBeamSwapRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch)"
}

/**
 * 传输层独立偷梁换柱决策请求 DTO (ProtocolBeamSwapChoiceRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 * @property offerId 挂起的选择唯一标识
 * @property action 动作分支 (SWAP / KEEP_ORIGINAL)
 * @property handTileId 手牌ID（SWAP 分支必填 0..135，KEEP_ORIGINAL 必须为 null）
 * @property targetSlot 目标槽位（SWAP 分支必填，KEEP_ORIGINAL 必须为 null）
 */
class ProtocolBeamSwapChoiceRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val decisionId: String,
    val controlEpoch: Long,
    val offerId: String,
    val action: ProtocolBeamSwapAction,
    val handTileId: Int? = null,
    val targetSlot: ProtocolBeamSwapSlot? = null
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        if (action == ProtocolBeamSwapAction.SWAP) {
            require(handTileId != null) { "handTileId is required for SWAP" }
            require(handTileId in 0..135) { "handTileId must be 0..135: $handTileId" }
            require(targetSlot != null) { "targetSlot is required for SWAP" }
        } else {
            require(handTileId == null) { "handTileId must be null for KEEP_ORIGINAL" }
            require(targetSlot == null) { "targetSlot must be null for KEEP_ORIGINAL" }
        }
    }

    operator fun component1(): String = requestId
    operator fun component2(): String = sessionId
    operator fun component3(): String = roundId
    operator fun component4(): String = decisionId
    operator fun component5(): Long = controlEpoch
    operator fun component6(): String = offerId
    operator fun component7(): ProtocolBeamSwapAction = action
    operator fun component8(): Int? = handTileId
    operator fun component9(): ProtocolBeamSwapSlot? = targetSlot

    fun copy(
        requestId: String = this.requestId,
        sessionId: String = this.sessionId,
        roundId: String = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        offerId: String = this.offerId,
        action: ProtocolBeamSwapAction = this.action,
        handTileId: Int? = this.handTileId,
        targetSlot: ProtocolBeamSwapSlot? = this.targetSlot
    ): ProtocolBeamSwapChoiceRequest = ProtocolBeamSwapChoiceRequest(
        requestId, sessionId, roundId, decisionId, controlEpoch, offerId, action, handTileId, targetSlot
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolBeamSwapChoiceRequest) return false
        return requestId == other.requestId &&
            sessionId == other.sessionId &&
            roundId == other.roundId &&
            decisionId == other.decisionId &&
            controlEpoch == other.controlEpoch &&
            offerId == other.offerId &&
            action == other.action &&
            handTileId == other.handTileId &&
            targetSlot == other.targetSlot
    }

    override fun hashCode(): Int {
        var result = requestId.hashCode()
        result = 31 * result + sessionId.hashCode()
        result = 31 * result + roundId.hashCode()
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        result = 31 * result + offerId.hashCode()
        result = 31 * result + action.hashCode()
        result = 31 * result + (handTileId?.hashCode() ?: 0)
        result = 31 * result + (targetSlot?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "ProtocolBeamSwapChoiceRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, offerId=$offerId, action=$action, handTileId=$handTileId, targetSlot=$targetSlot)"
}

/**
 * 传输层独立候选牌私有视图 DTO (ProtocolBeamSwapCandidateView)
 *
 * @property slot 候选牌所在绝对槽位
 * @property marked 是否被观察者本人有效落汗标记命中
 * @property tile 标记命中时附带的牌面（未标记时必须为 null）
 */
class ProtocolBeamSwapCandidateView(
    val slot: ProtocolBeamSwapSlot,
    val marked: Boolean,
    val tile: ProtocolTile? = null
) {
    init {
        if (marked) {
            require(tile != null) { "marked candidate must have tile" }
        } else {
            require(tile == null) { "unmarked candidate cannot have tile" }
        }
    }

    operator fun component1(): ProtocolBeamSwapSlot = slot
    operator fun component2(): Boolean = marked
    operator fun component3(): ProtocolTile? = tile

    fun copy(
        slot: ProtocolBeamSwapSlot = this.slot,
        marked: Boolean = this.marked,
        tile: ProtocolTile? = this.tile
    ): ProtocolBeamSwapCandidateView = ProtocolBeamSwapCandidateView(slot, marked, tile)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolBeamSwapCandidateView) return false
        return slot == other.slot && marked == other.marked && tile == other.tile
    }

    override fun hashCode(): Int {
        var result = slot.hashCode()
        result = 31 * result + marked.hashCode()
        result = 31 * result + (tile?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "ProtocolBeamSwapCandidateView(slot=$slot, marked=$marked, tile=$tile)"
}

/**
 * 传输层独立观察者私有偷梁换柱挂起选择视图 DTO (ProtocolBeamSwapOfferView)
 *
 * @property offerId 挂起选择唯一标识
 * @property window 施术窗口类型
 * @property candidates 候选牌列表（防御性不可变包装）
 * @property selectableHandTileIds 本人可换原有暗手牌集合（防御性不可变包装）
 * @property deadlineMillis 截止时间戳
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 */
class ProtocolBeamSwapOfferView(
    val offerId: String,
    val window: ProtocolBeamSwapWindow,
    candidates: List<ProtocolBeamSwapCandidateView>,
    selectableHandTileIds: List<Int>,
    val deadlineMillis: Long,
    val decisionId: String,
    val controlEpoch: Long
) {
    val candidates: List<ProtocolBeamSwapCandidateView> = Collections.unmodifiableList(ArrayList(candidates))
    val selectableHandTileIds: List<Int> = Collections.unmodifiableList(ArrayList(selectableHandTileIds))

    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        require(deadlineMillis >= 0L) { "deadlineMillis must be non-negative: $deadlineMillis" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
    }

    operator fun component1(): String = offerId
    operator fun component2(): ProtocolBeamSwapWindow = window
    operator fun component3(): List<ProtocolBeamSwapCandidateView> = candidates
    operator fun component4(): List<Int> = selectableHandTileIds
    operator fun component5(): Long = deadlineMillis
    operator fun component6(): String = decisionId
    operator fun component7(): Long = controlEpoch

    fun copy(
        offerId: String = this.offerId,
        window: ProtocolBeamSwapWindow = this.window,
        candidates: List<ProtocolBeamSwapCandidateView> = this.candidates,
        selectableHandTileIds: List<Int> = this.selectableHandTileIds,
        deadlineMillis: Long = this.deadlineMillis,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch
    ): ProtocolBeamSwapOfferView = ProtocolBeamSwapOfferView(
        offerId, window, candidates, selectableHandTileIds, deadlineMillis, decisionId, controlEpoch
    )

    fun deepCopy(): ProtocolBeamSwapOfferView = copy(
        candidates = candidates.map { it.copy() },
        selectableHandTileIds = ArrayList(selectableHandTileIds)
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolBeamSwapOfferView) return false
        return offerId == other.offerId &&
            window == other.window &&
            candidates == other.candidates &&
            selectableHandTileIds == other.selectableHandTileIds &&
            deadlineMillis == other.deadlineMillis &&
            decisionId == other.decisionId &&
            controlEpoch == other.controlEpoch
    }

    override fun hashCode(): Int {
        var result = offerId.hashCode()
        result = 31 * result + window.hashCode()
        result = 31 * result + candidates.hashCode()
        result = 31 * result + selectableHandTileIds.hashCode()
        result = 31 * result + deadlineMillis.hashCode()
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        return result
    }

    override fun toString(): String =
        "ProtocolBeamSwapOfferView(offerId=$offerId, window=$window, candidates=$candidates, selectableHandTileIds=$selectableHandTileIds, deadlineMillis=$deadlineMillis, decisionId=$decisionId, controlEpoch=$controlEpoch)"
}

/**
 * 传输层独立观察者私有偷梁换柱视图 DTO (ProtocolBeamSwapView)
 *
 * @property canApply 是否可激活
 * @property disabledReason 禁用原因（不可激活时提供）
 * @property cost 消耗千术点（恒为 10）
 * @property window 当前窗口类型（无摸牌时为 null）
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 * @property stateVersion 单局状态版本
 * @property pendingOffer 挂起选择视图（本人有效时提供，他家/未激活为 null）
 */
class ProtocolBeamSwapView @JvmOverloads constructor(
    val canApply: Boolean,
    val disabledReason: String?,
    val cost: Int = 10,
    val window: ProtocolBeamSwapWindow?,
    val decisionId: String,
    val controlEpoch: Long,
    val stateVersion: Long,
    val pendingOffer: ProtocolBeamSwapOfferView? = null
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
            if (window != null) {
                require(pendingOffer.window == window) {
                    "pendingOffer.window (${pendingOffer.window}) must match view window ($window)"
                }
            }
        }
    }

    operator fun component1(): Boolean = canApply
    operator fun component2(): String? = disabledReason
    operator fun component3(): Int = cost
    operator fun component4(): ProtocolBeamSwapWindow? = window
    operator fun component5(): String = decisionId
    operator fun component6(): Long = controlEpoch
    operator fun component7(): Long = stateVersion
    operator fun component8(): ProtocolBeamSwapOfferView? = pendingOffer

    fun copy(
        canApply: Boolean = this.canApply,
        disabledReason: String? = this.disabledReason,
        cost: Int = this.cost,
        window: ProtocolBeamSwapWindow? = this.window,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        stateVersion: Long = this.stateVersion,
        pendingOffer: ProtocolBeamSwapOfferView? = this.pendingOffer
    ): ProtocolBeamSwapView = ProtocolBeamSwapView(
        canApply, disabledReason, cost, window, decisionId, controlEpoch, stateVersion, pendingOffer
    )

    fun deepCopy(): ProtocolBeamSwapView = copy(pendingOffer = pendingOffer?.deepCopy())

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ProtocolBeamSwapView) return false
        return canApply == other.canApply &&
            disabledReason == other.disabledReason &&
            cost == other.cost &&
            window == other.window &&
            decisionId == other.decisionId &&
            controlEpoch == other.controlEpoch &&
            stateVersion == other.stateVersion &&
            pendingOffer == other.pendingOffer
    }

    override fun hashCode(): Int {
        var result = canApply.hashCode()
        result = 31 * result + (disabledReason?.hashCode() ?: 0)
        result = 31 * result + cost.hashCode()
        result = 31 * result + (window?.hashCode() ?: 0)
        result = 31 * result + decisionId.hashCode()
        result = 31 * result + controlEpoch.hashCode()
        result = 31 * result + stateVersion.hashCode()
        result = 31 * result + (pendingOffer?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "ProtocolBeamSwapView(canApply=$canApply, disabledReason=$disabledReason, cost=$cost, window=$window, decisionId=$decisionId, controlEpoch=$controlEpoch, stateVersion=$stateVersion, pendingOffer=$pendingOffer)"
}
