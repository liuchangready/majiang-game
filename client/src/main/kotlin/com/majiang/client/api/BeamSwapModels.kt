package com.majiang.client.api

import java.util.Collections

/**
 * 客户端独立偷梁换柱配置模型 (BeamSwapConfig)
 *
 * @property enabled 是否启用偷梁换柱功能（默认关闭）
 */
data class BeamSwapConfig(
    val enabled: Boolean = false
)

/**
 * 客户端独立偷梁换柱窗口类型枚举 (BeamSwapWindow)
 */
enum class BeamSwapWindow {
    WALL,
    DEAD_WALL
}

/**
 * 客户端独立偷梁换柱动作分支枚举 (BeamSwapAction)
 */
enum class BeamSwapAction {
    SWAP,
    KEEP_ORIGINAL
}

/**
 * 客户端独立偷梁换柱槽位密封类 (BeamSwapSlot)
 */
sealed class BeamSwapSlot {
    abstract val index: Int

    data class WallSlot(override val index: Int) : BeamSwapSlot() {
        init {
            require(index >= 0) { "WallSlot index must be non-negative: $index" }
        }
    }

    data class DeadWallSlot(override val index: Int) : BeamSwapSlot() {
        init {
            require(index >= 0) { "DeadWallSlot index must be non-negative: $index" }
        }
    }

    data class ReplacementSlot(override val index: Int) : BeamSwapSlot() {
        init {
            require(index >= 0) { "ReplacementSlot index must be non-negative: $index" }
        }
    }
}

/**
 * 客户端独立激活偷梁换柱请求模型 (BeamSwapRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 单局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 */
class BeamSwapRequest(
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
    ): BeamSwapRequest = BeamSwapRequest(
        requestId = requestId,
        sessionId = sessionId,
        roundId = roundId,
        decisionId = decisionId,
        controlEpoch = controlEpoch
    )

    fun deepCopy(): BeamSwapRequest = copy()

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is BeamSwapRequest) return false
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
        return "BeamSwapRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch)"
    }
}

/**
 * 客户端独立偷梁换柱选择请求模型 (BeamSwapChoiceRequest)
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 单局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 * @property offerId 挂起的选择唯一标识
 * @property action 选择分支 (SWAP / KEEP_ORIGINAL)
 * @property handTileId 调换的暗手牌实体编号（SWAP 时非空，KEEP_ORIGINAL 时必须为 null）
 * @property targetSlot 目标候选槽位（SWAP 时非空，KEEP_ORIGINAL 时必须为 null）
 */
class BeamSwapChoiceRequest(
    val requestId: String,
    val sessionId: String,
    val roundId: String,
    val decisionId: String,
    val controlEpoch: Long,
    val offerId: String,
    val action: BeamSwapAction,
    val handTileId: Int? = null,
    val targetSlot: BeamSwapSlot? = null
) {
    init {
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        when (action) {
            BeamSwapAction.KEEP_ORIGINAL -> {
                require(handTileId == null) { "handTileId must be null for KEEP_ORIGINAL" }
                require(targetSlot == null) { "targetSlot must be null for KEEP_ORIGINAL" }
            }
            BeamSwapAction.SWAP -> {
                require(handTileId != null && handTileId in 0..135) {
                    "handTileId must be in 0..135 for SWAP, got: $handTileId"
                }
                require(targetSlot != null) { "targetSlot must not be null for SWAP" }
            }
        }
    }

    operator fun component1(): String = requestId
    operator fun component2(): String = sessionId
    operator fun component3(): String = roundId
    operator fun component4(): String = decisionId
    operator fun component5(): Long = controlEpoch
    operator fun component6(): String = offerId
    operator fun component7(): BeamSwapAction = action
    operator fun component8(): Int? = handTileId
    operator fun component9(): BeamSwapSlot? = targetSlot

    fun copy(
        requestId: String = this.requestId,
        sessionId: String = this.sessionId,
        roundId: String = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        offerId: String = this.offerId,
        action: BeamSwapAction = this.action,
        handTileId: Int? = this.handTileId,
        targetSlot: BeamSwapSlot? = this.targetSlot
    ): BeamSwapChoiceRequest = BeamSwapChoiceRequest(
        requestId = requestId,
        sessionId = sessionId,
        roundId = roundId,
        decisionId = decisionId,
        controlEpoch = controlEpoch,
        offerId = offerId,
        action = action,
        handTileId = handTileId,
        targetSlot = targetSlot
    )

    fun deepCopy(): BeamSwapChoiceRequest = copy()

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is BeamSwapChoiceRequest) return false
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

    override fun toString(): String {
        return "BeamSwapChoiceRequest(requestId=$requestId, sessionId=$sessionId, roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, offerId=$offerId, action=$action, handTileId=$handTileId, targetSlot=$targetSlot)"
    }
}

/**
 * 客户端独立偷梁换柱候选牌视图模型 (BeamSwapCandidateView)
 *
 * @property slot 候选牌槽位
 * @property marked 是否被千术标记
 * @property tile 物理牌实体（仅 marked == true 时提供）
 */
class BeamSwapCandidateView(
    val slot: BeamSwapSlot,
    val marked: Boolean,
    val tile: TileView? = null
) {
    init {
        if (marked) {
            require(tile != null) { "tile must be provided when marked is true" }
        } else {
            require(tile == null) { "tile must be null when marked is false" }
        }
    }

    operator fun component1(): BeamSwapSlot = slot
    operator fun component2(): Boolean = marked
    operator fun component3(): TileView? = tile

    fun copy(
        slot: BeamSwapSlot = this.slot,
        marked: Boolean = this.marked,
        tile: TileView? = this.tile
    ): BeamSwapCandidateView = BeamSwapCandidateView(slot, marked, tile)

    fun deepCopy(): BeamSwapCandidateView = copy(tile = tile?.copy())

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is BeamSwapCandidateView) return false
        return slot == other.slot && marked == other.marked && tile == other.tile
    }

    override fun hashCode(): Int {
        var result = slot.hashCode()
        result = 31 * result + marked.hashCode()
        result = 31 * result + (tile?.hashCode() ?: 0)
        return result
    }

    override fun toString(): String =
        "BeamSwapCandidateView(slot=$slot, marked=$marked, tile=$tile)"
}

/**
 * 客户端独立观察者私有偷梁换柱挂起选择视图模型 (BeamSwapOfferView)
 *
 * @property offerId 挂起选择唯一标识
 * @property window 施术窗口类型
 * @property candidates 候选牌列表（防御性不可变包装）
 * @property selectableHandTileIds 本人可换原有暗手牌集合（防御性不可变包装）
 * @property deadlineMillis 截止时间戳
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 */
class BeamSwapOfferView(
    val offerId: String,
    val window: BeamSwapWindow,
    candidates: List<BeamSwapCandidateView>,
    selectableHandTileIds: List<Int>,
    val deadlineMillis: Long,
    val decisionId: String,
    val controlEpoch: Long
) {
    val candidates: List<BeamSwapCandidateView> = Collections.unmodifiableList(ArrayList(candidates))
    val selectableHandTileIds: List<Int> = Collections.unmodifiableList(ArrayList(selectableHandTileIds))

    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        require(deadlineMillis >= 0L) { "deadlineMillis must be non-negative: $deadlineMillis" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be non-negative: $controlEpoch" }
    }

    operator fun component1(): String = offerId
    operator fun component2(): BeamSwapWindow = window
    operator fun component3(): List<BeamSwapCandidateView> = candidates
    operator fun component4(): List<Int> = selectableHandTileIds
    operator fun component5(): Long = deadlineMillis
    operator fun component6(): String = decisionId
    operator fun component7(): Long = controlEpoch

    fun copy(
        offerId: String = this.offerId,
        window: BeamSwapWindow = this.window,
        candidates: List<BeamSwapCandidateView> = this.candidates,
        selectableHandTileIds: List<Int> = this.selectableHandTileIds,
        deadlineMillis: Long = this.deadlineMillis,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch
    ): BeamSwapOfferView = BeamSwapOfferView(
        offerId, window, candidates, selectableHandTileIds, deadlineMillis, decisionId, controlEpoch
    )

    fun deepCopy(): BeamSwapOfferView = copy(
        candidates = candidates.map { it.copy() },
        selectableHandTileIds = ArrayList(selectableHandTileIds)
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is BeamSwapOfferView) return false
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
        "BeamSwapOfferView(offerId=$offerId, window=$window, candidates=$candidates, selectableHandTileIds=$selectableHandTileIds, deadlineMillis=$deadlineMillis, decisionId=$decisionId, controlEpoch=$controlEpoch)"
}

/**
 * 客户端独立观察者私有偷梁换柱视图模型 (BeamSwapView)
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
class BeamSwapView @JvmOverloads constructor(
    val canApply: Boolean,
    val disabledReason: String?,
    val cost: Int = 10,
    val window: BeamSwapWindow?,
    val decisionId: String,
    val controlEpoch: Long,
    val stateVersion: Long,
    val pendingOffer: BeamSwapOfferView? = null
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
    operator fun component4(): BeamSwapWindow? = window
    operator fun component5(): String = decisionId
    operator fun component6(): Long = controlEpoch
    operator fun component7(): Long = stateVersion
    operator fun component8(): BeamSwapOfferView? = pendingOffer

    fun copy(
        canApply: Boolean = this.canApply,
        disabledReason: String? = this.disabledReason,
        cost: Int = this.cost,
        window: BeamSwapWindow? = this.window,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        stateVersion: Long = this.stateVersion,
        pendingOffer: BeamSwapOfferView? = this.pendingOffer
    ): BeamSwapView = BeamSwapView(
        canApply, disabledReason, cost, window, decisionId, controlEpoch, stateVersion, pendingOffer
    )

    fun deepCopy(): BeamSwapView = copy(pendingOffer = pendingOffer?.deepCopy())

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is BeamSwapView) return false
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
        "BeamSwapView(canApply=$canApply, disabledReason=$disabledReason, cost=$cost, window=$window, decisionId=$decisionId, controlEpoch=$controlEpoch, stateVersion=$stateVersion, pendingOffer=$pendingOffer)"
}
