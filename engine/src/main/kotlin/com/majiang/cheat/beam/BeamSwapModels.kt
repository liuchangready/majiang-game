package com.majiang.cheat.beam

import com.majiang.model.Tile
import java.util.ArrayList
import java.util.Collections

/**
 * 偷梁换柱千术配置
 *
 * @property enabled 是否启用偷梁换柱功能（默认关闭）
 */
data class BeamSwapConfig(
    val enabled: Boolean = false
)

/**
 * 偷梁换柱施术窗口
 */
enum class BeamSwapWindow {
    WALL,      // 正常摸牌窗口（活牌山）
    DEAD_WALL  // 杠后补牌窗口（死山/王牌区）
}

/**
 * 偷梁换柱二选一动作
 */
enum class BeamSwapAction {
    SWAP,          // 执行互换
    KEEP_ORIGINAL  // 保持原样
}

/**
 * 偷梁换柱目标槽位描述
 */
sealed class BeamSwapSlot {
    data class WallSlot(val index: Int) : BeamSwapSlot() {
        init {
            require(index >= 0) { "index must be non-negative: $index" }
        }
    }

    data class DeadWallSlot(val index: Int) : BeamSwapSlot() {
        init {
            require(index >= 0) { "index must be non-negative: $index" }
        }
    }

    data class ReplacementSlot(val index: Int) : BeamSwapSlot() {
        init {
            require(index >= 0) { "index must be non-negative: $index" }
        }
    }
}

/**
 * 激活偷梁换柱请求
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
 * 偷梁换柱选择请求
 *
 * @property requestId 请求唯一标识
 * @property sessionId 整场对局标识
 * @property roundId 单局标识
 * @property decisionId 当前决策标识
 * @property controlEpoch 控制代次
 * @property offerId 挂起的选择唯一标识
 * @property action 选择动作 (SWAP / KEEP_ORIGINAL)
 * @property handTileId 换出的暗手牌实体标识 (SWAP 时必须提供)
 * @property targetSlot 目标槽位 (SWAP 时必须提供)
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
            BeamSwapAction.SWAP -> {
                require(handTileId != null) { "handTileId cannot be null when action is SWAP" }
                require(targetSlot != null) { "targetSlot cannot be null when action is SWAP" }
            }
            BeamSwapAction.KEEP_ORIGINAL -> {
                require(handTileId == null) { "handTileId must be null when action is KEEP_ORIGINAL" }
                require(targetSlot == null) { "targetSlot must be null when action is KEEP_ORIGINAL" }
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
 * 偷梁换柱领域候选
 *
 * @property tileId 目标实体牌标识 (0..135)
 * @property slot 目标槽位
 */
data class BeamSwapCandidate(
    val tileId: Int,
    val slot: BeamSwapSlot
) {
    init {
        require(tileId in 0..135) { "tileId must be in 0..135: $tileId" }
    }
}

/**
 * 挂起的偷梁换柱选择领域状态
 *
 * @property offerId 唯一标识 (beam-$seat-$requestId)
 * @property seat 所属玩家席位 (0..3)
 * @property window 施术窗口
 * @property candidates 候选列表（防御性复制且只读）
 * @property eligibleHandTileIds 激活时原有暗手牌实体ID列表（防御性复制且只读）
 * @property drawnTileId 激活时的摸牌实体标识
 * @property createdTurnNumber 创建时的摸牌序号
 */
class BeamSwapOffer(
    val offerId: String,
    val seat: Int,
    val window: BeamSwapWindow,
    candidates: List<BeamSwapCandidate>,
    eligibleHandTileIds: List<Int>,
    val drawnTileId: Int,
    val createdTurnNumber: Int
) {
    val candidates: List<BeamSwapCandidate> = Collections.unmodifiableList(ArrayList(candidates))
    val eligibleHandTileIds: List<Int> = Collections.unmodifiableList(ArrayList(eligibleHandTileIds))

    init {
        require(offerId.isNotBlank()) { "offerId cannot be blank" }
        require(seat in 0..3) { "seat must be in 0..3: $seat" }
        require(drawnTileId in 0..135) { "drawnTileId must be in 0..135: $drawnTileId" }
        require(createdTurnNumber >= 0) { "createdTurnNumber must be non-negative: $createdTurnNumber" }
        require(this.candidates.isNotEmpty()) { "candidates cannot be empty" }
        require(this.eligibleHandTileIds.isNotEmpty()) { "eligibleHandTileIds cannot be empty" }
    }

    operator fun component1(): String = offerId
    operator fun component2(): Int = seat
    operator fun component3(): BeamSwapWindow = window
    operator fun component4(): List<BeamSwapCandidate> = candidates
    operator fun component5(): List<Int> = eligibleHandTileIds
    operator fun component6(): Int = drawnTileId
    operator fun component7(): Int = createdTurnNumber

    fun copy(
        offerId: String = this.offerId,
        seat: Int = this.seat,
        window: BeamSwapWindow = this.window,
        candidates: List<BeamSwapCandidate> = this.candidates,
        eligibleHandTileIds: List<Int> = this.eligibleHandTileIds,
        drawnTileId: Int = this.drawnTileId,
        createdTurnNumber: Int = this.createdTurnNumber
    ): BeamSwapOffer = BeamSwapOffer(
        offerId = offerId,
        seat = seat,
        window = window,
        candidates = candidates,
        eligibleHandTileIds = eligibleHandTileIds,
        drawnTileId = drawnTileId,
        createdTurnNumber = createdTurnNumber
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is BeamSwapOffer) return false
        return offerId == other.offerId &&
                seat == other.seat &&
                window == other.window &&
                candidates == other.candidates &&
                eligibleHandTileIds == other.eligibleHandTileIds &&
                drawnTileId == other.drawnTileId &&
                createdTurnNumber == other.createdTurnNumber
    }

    override fun hashCode(): Int {
        var result = offerId.hashCode()
        result = 31 * result + seat.hashCode()
        result = 31 * result + window.hashCode()
        result = 31 * result + candidates.hashCode()
        result = 31 * result + eligibleHandTileIds.hashCode()
        result = 31 * result + drawnTileId.hashCode()
        result = 31 * result + createdTurnNumber.hashCode()
        return result
    }

    override fun toString(): String {
        return "BeamSwapOffer(offerId=$offerId, seat=$seat, window=$window, candidates=$candidates, eligibleHandTileIds=$eligibleHandTileIds, drawnTileId=$drawnTileId, createdTurnNumber=$createdTurnNumber)"
    }
}

/**
 * 观察者私有候选视图
 *
 * @property slot 候选槽位
 * @property marked 是否被观察者本人的落汗标记命中
 * @property tile 标记命中时携带牌面与实体标识；未标记必须为 null
 */
data class BeamSwapCandidateView(
    val slot: BeamSwapSlot,
    val marked: Boolean,
    val tile: Tile? = null
) {
    init {
        if (marked) {
            require(tile != null) { "tile cannot be null when marked is true" }
        } else {
            require(tile == null) { "tile must be null when marked is false" }
        }
    }
}

/**
 * 观察者私有偷梁换柱挂起视图
 *
 * @property offerId 挂起选择标识
 * @property window 施术窗口
 * @property candidates 候选槽位列表（防御性复制且只读）
 * @property selectableHandTileIds 可供换出的本人原有暗手牌实体ID列表（防御性复制且只读）
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
        offerId = offerId,
        window = window,
        candidates = candidates,
        selectableHandTileIds = selectableHandTileIds,
        deadlineMillis = deadlineMillis,
        decisionId = decisionId,
        controlEpoch = controlEpoch
    )

    fun deepCopy(): BeamSwapOfferView = copy(
        candidates = candidates.map { it.copy() }
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

    override fun toString(): String {
        return "BeamSwapOfferView(offerId=$offerId, window=$window, candidates=$candidates, selectableHandTileIds=$selectableHandTileIds, deadlineMillis=$deadlineMillis, decisionId=$decisionId, controlEpoch=$controlEpoch)"
    }
}

/**
 * 观察者私有偷梁换柱视图
 *
 * @property canApply 是否可激活
 * @property disabledReason 禁用原因（不可激活时提供）
 * @property cost 消耗千术点（恒为 10）
 * @property window 当前摸牌窗口类型（无摸牌时为 null）
 * @property decisionId 当前决策标识
 * @property controlEpoch 当前控制代次
 * @property stateVersion 单局状态版本
 * @property pendingOffer 挂起的选择视图（本人有效时提供，他家/未激活为 null）
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
        canApply = canApply,
        disabledReason = disabledReason,
        cost = cost,
        window = window,
        decisionId = decisionId,
        controlEpoch = controlEpoch,
        stateVersion = stateVersion,
        pendingOffer = pendingOffer
    )

    fun deepCopy(): BeamSwapView = copy(
        pendingOffer = pendingOffer?.deepCopy()
    )

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

    override fun toString(): String {
        return "BeamSwapView(canApply=$canApply, disabledReason=$disabledReason, cost=$cost, window=$window, decisionId=$decisionId, controlEpoch=$controlEpoch, stateVersion=$stateVersion, pendingOffer=$pendingOffer)"
    }
}
