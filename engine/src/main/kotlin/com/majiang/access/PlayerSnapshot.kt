package com.majiang.access

import com.majiang.application.PlayerDecision
import com.majiang.application.deepCopy
import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.session.PlayerView
import com.majiang.stacking.ShufflePreparationView
import java.util.ArrayList
import java.util.Collections

/**
 * 扩展函数：为 PlayerView 提供安全防御性深拷贝
 */
fun PlayerView.deepCopy(): PlayerView = PlayerView.create(
    viewerSeat = viewerSeat,
    phase = phase,
    roundWind = roundWind,
    roundNumber = roundNumber,
    honbaSticks = honbaSticks,
    riichiSticks = riichiSticks,
    dealerSeat = dealerSeat,
    currentTurnPlayer = currentTurnPlayer,
    wallRemaining = wallRemaining,
    doraIndicators = doraIndicators,
    scores = scores,
    riichiStatus = riichiStatus,
    discards = discards,
    melds = melds,
    handTileCounts = handTileCounts,
    myClosedTiles = myClosedTiles,
    myDrawnTile = myDrawnTile,
    legalActions = legalActions,
    lastDiscard = lastDiscard,
    lastDiscardPlayer = lastDiscardPlayer,
    mySleeveTiles = mySleeveTiles,
    myFuriten = myFuriten,
    myDiscardHistory = myDiscardHistory
)

/**
 * 玩家可见状态完整快照 (PlayerSnapshot)
 *
 * 包含当前可见的手牌/公开牌面、当前合法决策、时钟视图、出千点数以及同步代次与游标。
 * 此外扩展提供整场会话视图、席位控制状态、时钟暂停状态以及终局结算恢复事件。
 * 所有内部集合均保证防御性隔离与只读包装。
 *
 * @property sessionId 整场比赛标识
 * @property roundId 当前单局标识；局间或终局为 null
 * @property syncEpoch 当前同步流代次（重连或新对局会更新）
 * @property cursor 当前快照对应的单调递增游标
 * @property sessionVersion 整场 Session 版本号
 * @property roundVersion 当前单局状态机版本号；无活动局为 null
 * @property view 观察者座位的只读投影视图
 * @property decision 当前座位可执行的合法决策
 * @property clocks 权威时钟视图列表（各座位）
 * @property cheatPoints 各座位出千资源点数统计
 * @property session 整场比赛只读投影视图（赛程、局号、历史、终局排名）
 * @property controls 各席位控制者状态视图（连接状态、角色、控制代次）
 * @property paused 权威时钟是否处于暂停状态
 * @property settlementRoundId 刚结束或终局结算的单局标识；进行中为 null
 * @property settlementEvents 刚结束或终局结算公开事件投影（含 ScoreBreakdown 完整役种计分明细）
 * @property preparation 洗牌千术准备阶段私有视图；非准备阶段为 null
 * @property presentationPreview 私有玩家展示预览（含待牌、立直弃牌与鸣牌方案）；无活动局或旧快照为 null
 */
data class PlayerSnapshot(
    val sessionId: String,
    val roundId: String?,
    val syncEpoch: String,
    val cursor: Long,
    val sessionVersion: Long,
    val roundVersion: Long?,
    val view: PlayerView,
    val decision: PlayerDecision,
    val clocks: List<ClockView> = emptyList(),
    val cheatPoints: List<Int> = emptyList(),
    val session: SessionView? = null,
    val controls: List<SeatControlView> = emptyList(),
    val paused: Boolean = false,
    val settlementRoundId: String? = null,
    val settlementEvents: List<GameEvent> = emptyList(),
    val preparation: ShufflePreparationView? = null,
    val presentationPreview: PlayerPresentationPreview? = null,
    val tileMark: com.majiang.cheat.mark.TileMarkView? = null,
    val sleeveBloom: com.majiang.cheat.bloom.SleeveBloomView? = null,
    val beamSwap: com.majiang.cheat.beam.BeamSwapView? = null
) {
    /** 观察者座位快捷属性，代理至 view.viewerSeat */
    val viewerSeat: Int get() = view.viewerSeat

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
        if (sleeveBloom != null) {
            require(sleeveBloom.cost == 10) { "sleeveBloom cost must be 10, got: ${sleeveBloom.cost}" }
            require(sleeveBloom.controlEpoch >= 0L) { "sleeveBloom controlEpoch must be >= 0: ${sleeveBloom.controlEpoch}" }
            require(sleeveBloom.stateVersion >= 0L) { "sleeveBloom stateVersion must be >= 0: ${sleeveBloom.stateVersion}" }
            val pendingOffer = sleeveBloom.pendingOffer
            if (pendingOffer != null) {
                if (pendingOffer.mode == com.majiang.cheat.bloom.SleeveBloomOfferMode.IMMEDIATE) {
                    require(view.phase == com.majiang.state.GamePhase.CHEAT_WINDOW) {
                        "pendingOffer requires CHEAT_WINDOW phase, got: ${view.phase}"
                    }
                    require(view.currentTurnPlayer == view.viewerSeat) {
                        "pendingOffer requires current turn player to be viewer, current: ${view.currentTurnPlayer}, viewer: ${view.viewerSeat}"
                    }
                    require(view.myDrawnTile != null && view.myDrawnTile.tileId == pendingOffer.firstTile.tileId) {
                        "pendingOffer firstTile (${pendingOffer.firstTile}) must match view.myDrawnTile (${view.myDrawnTile})"
                    }
                } else {
                    require(pendingOffer.windowId != null) {
                        "off-turn pendingOffer requires windowId"
                    }
                }
                require(pendingOffer.decisionId == decision.decisionId) {
                    "pendingOffer decisionId (${pendingOffer.decisionId}) must match decision.decisionId (${decision.decisionId})"
                }
                require(pendingOffer.controlEpoch == decision.controlEpoch) {
                    "pendingOffer controlEpoch (${pendingOffer.controlEpoch}) must match decision.controlEpoch (${decision.controlEpoch})"
                }
            }
            sleeveBloom.reservation?.let { reservation ->
                require(view.myClosedTiles.none { it.tileId == reservation.tile.tileId }) {
                    "reserved tile cannot already be in the observer's closed hand"
                }
            }
        }
        if (beamSwap != null) {
            require(beamSwap.cost == 10) { "beamSwap cost must be 10, got: ${beamSwap.cost}" }
            require(beamSwap.controlEpoch >= 0L) { "beamSwap controlEpoch must be >= 0: ${beamSwap.controlEpoch}" }
            require(beamSwap.stateVersion >= 0L) { "beamSwap stateVersion must be >= 0: ${beamSwap.stateVersion}" }
            val pendingOffer = beamSwap.pendingOffer
            if (pendingOffer != null) {
                require(view.phase == com.majiang.state.GamePhase.CHEAT_WINDOW) {
                    "pendingOffer requires CHEAT_WINDOW phase, got: ${view.phase}"
                }
                require(view.currentTurnPlayer == view.viewerSeat) {
                    "pendingOffer requires current turn player to be viewer, current: ${view.currentTurnPlayer}, viewer: ${view.viewerSeat}"
                }
                require(view.myDrawnTile != null) {
                    "pendingOffer requires view.myDrawnTile to exist"
                }
                require(pendingOffer.decisionId == decision.decisionId) {
                    "pendingOffer decisionId (${pendingOffer.decisionId}) must match decision.decisionId (${decision.decisionId})"
                }
                require(pendingOffer.controlEpoch == decision.controlEpoch) {
                    "pendingOffer controlEpoch (${pendingOffer.controlEpoch}) must match decision.controlEpoch (${decision.controlEpoch})"
                }
                val allowedHandTileIds = view.myClosedTiles.map { it.tileId }.toSet()
                require(pendingOffer.selectableHandTileIds.all { it in allowedHandTileIds }) {
                    "pendingOffer selectableHandTileIds (${pendingOffer.selectableHandTileIds}) must all be in view.myClosedTiles ($allowedHandTileIds)"
                }
                require(!pendingOffer.selectableHandTileIds.contains(view.myDrawnTile.tileId)) {
                    "pendingOffer selectableHandTileIds cannot contain view.myDrawnTile"
                }
            }
        }
    }

    fun deepCopy(): PlayerSnapshot = PlayerSnapshot(
        sessionId = sessionId,
        roundId = roundId,
        syncEpoch = syncEpoch,
        cursor = cursor,
        sessionVersion = sessionVersion,
        roundVersion = roundVersion,
        view = view.deepCopy(),
        decision = decision.deepCopy(),
        clocks = Collections.unmodifiableList(ArrayList(clocks)),
        cheatPoints = Collections.unmodifiableList(ArrayList(cheatPoints)),
        session = session?.deepCopy(),
        controls = Collections.unmodifiableList(controls.map { it.deepCopy() }),
        paused = paused,
        settlementRoundId = settlementRoundId,
        settlementEvents = Collections.unmodifiableList(settlementEvents.map { snapshotGameEvent(it) }),
        preparation = preparation?.deepCopy(),
        presentationPreview = presentationPreview?.deepCopy(),
        tileMark = tileMark?.deepCopy(),
        sleeveBloom = sleeveBloom?.deepCopy(),
        beamSwap = beamSwap?.deepCopy()
    )
}

