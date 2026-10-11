package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.Input
import com.badlogic.gdx.InputAdapter
import com.badlogic.gdx.Screen
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.math.Vector3
import com.majiang.client.api.ActionChoice
import com.majiang.client.api.ClockInfo
import com.majiang.client.api.ConnectionState
import com.majiang.client.api.GameEventView
import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.GameSession
import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.GameUpdateView
import com.majiang.client.api.LegalActionView
import com.majiang.client.api.MeldChoiceKindView
import com.majiang.client.api.MeldTypeView
import com.majiang.client.api.PlayerView
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.ScoreBreakdownView
import com.majiang.client.api.SettlementDetailView
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileMarkArea
import com.majiang.client.api.TileMarkRequest
import com.majiang.client.api.TileView
import com.majiang.presentation.common.ClockDisplay
import com.majiang.presentation.common.FinalResultRenderer
import com.majiang.presentation.common.SettlementDetailPresenter
import com.majiang.presentation.common.SettlementDetailRenderer
import com.majiang.presentation.common.SettlementDetailRow
import com.majiang.presentation.common.SettlementModalGeometry
import com.majiang.presentation.common.SettlementSection
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.TenpaiPanelRenderer
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.DiscardAnimation
import com.majiang.presentation.common.render.DiscardPhase
import com.majiang.presentation.common.render.DoraSparkle
import com.majiang.presentation.common.render.GhostHandTexture
import com.majiang.presentation.common.render.MeldDisplayRenderer
import com.majiang.presentation.common.render.MeldDisplayLayout
import com.majiang.presentation.common.render.TileArranger
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin
import java.util.ArrayDeque
import java.util.UUID

/**
 * 玩家牌桌场景（表现层 V4 presentation.game）。
 *
 * 遵循架构契约与纯客户端模型：
 * 1. 严格只依赖 [UiAssets]（根借用）、[GameSession]、[TableNavigation] 与可选 [TileSkin]；
 * 2. 玩家动作仅通过 [ActionChoice] 提交权威 optionId、decisionId 与 controlEpoch；
 * 3. 严格遵循 136 张物理牌与不可变 TileView；
 * 4. 20+3 权威时钟本地平滑插值；
 * 5. FIFO 弃牌动画、牌河视图调和与换局/结算快进；
 * 6. 集成 MeldSelectionModel、RiichiSelectionModel、ShufflePreparationPanel；
 * 7. 结算模态与终局模态完整迁移；
 * 8. 绝对无 engine、protocol、server 或旧 GameClient 依赖。
 */
class GameScreen(
    val assets: UiAssets,
    val session: GameSession,
    val navigation: TableNavigation,
    private val skinOverride: TileSkin? = null
) : Screen, InputAdapter() {

    val tileSkin: TileSkin
        get() = skinOverride ?: assets.skinManager.currentSkin

    private val tileDrawBuffer = TileDrawBuffer()
    private val glyphLayout = GlyphLayout()

    val camera = OrthographicCamera().apply {
        setToOrtho(false, 1280f, 720f)
    }

    // 会话订阅与快照
    private var updateSubscription: AutoCloseable? = null
    private var disconnectSubscription: AutoCloseable? = null
    var currentSnapshot: GameSnapshotView? = session.currentSnapshot
    val currentView: PlayerView
        get() = currentSnapshot?.view ?: defaultEmptyPlayerView(0)
    var isLoading: Boolean = (currentSnapshot == null)
    private var lastSyncEpoch: String? = currentSnapshot?.syncEpoch
    private var lastRoundId: String? = currentSnapshot?.roundId
    private var lastCursor: Long? = currentSnapshot?.cursor
    private var lastClockUpdateLocalMillis: Long = System.currentTimeMillis()
    private var activeSettlementRoundId: String? = null

    val isPaused: Boolean get() = currentSnapshot?.paused == true

    @Volatile
    var isDisposed: Boolean = false
        private set

    private fun runOnUi(action: () -> Unit) {
        if (isDisposed) return
        val app = Gdx.app
        if (app != null) {
            app.postRunnable {
                if (!isDisposed) {
                    action()
                }
            }
        } else {
            if (!isDisposed) {
                action()
            }
        }
    }

    // 提交与提示
    var isSubmitting: Boolean = false
    private var toastMessage: String? = null
    private var toastTimer: Float = 0f

    // 状态盒与控制按钮
    val statusBoxRect = Rectangle(35f, 40f, 175f, 82f)
    val btnTakeoverRect = Rectangle(1080f, 108f, 140f, 36f)
    private var isTakeoverHovered = false

    // 选牌模型与准备面板
    val riichiSelectionModel = RiichiSelectionModel()
    val meldSelectionModel = MeldSelectionModel()
    val shufflePreparationPanel = ShufflePreparationPanel(session)
    val tileMarkPanel = TileMarkPanel(session)
    private var isSubmittingMark = false

    var gameStarted = false

    // 结算数据封装
    private var showSettlement = false
    var currentSettlement: SettlementDetailView? = null
    var stateTime: Float = 0f
    val isSettlementShown: Boolean get() = showSettlement
    val currentSettlementData: SettlementDetailView? get() = currentSettlement

    private var showFinalResult: Boolean = false
    private var isNextRoundSubmitting: Boolean = false
    val isFinalResultShown: Boolean get() = showFinalResult

    val isMatchFinished: Boolean
        get() {
            if (currentView.phase == GamePhaseView.MATCH_END) return true
            val sess = currentSnapshot?.session
            if (sess != null) {
                if (sess.lifecycle == "FINISHED" || sess.finalResult != null) return true
            }
            return false
        }

    val tenpaiWaits: List<TileView>
        get() = currentSnapshot?.presentationPreview?.waits ?: emptyList()

    private val finalResultRenderer = FinalResultRenderer()
    private val tenpaiPanelRenderer = TenpaiPanelRenderer()
    private val settlementDetailRenderer = SettlementDetailRenderer()

    val btnReturnToMenuRect: Rectangle get() = btnNextRoundRect
    val btnRoundDetailsRect = Rectangle(250f, 145f, 240f, 46f)
    val tenpaiPanelRect: Rectangle get() = tenpaiPanelRenderer.computePanelRect(tenpaiWaits.size)

    var settlementSection: SettlementSection = SettlementSection.YAKU
        private set
    var settlementPage: Int = 0
        private set
    val settlementPageCount: Int
        get() {
            val s = currentSettlement ?: return 0
            val bd = s.breakdown
            if (bd != null) {
                return SettlementDetailPresenter.pageCount(bd, settlementSection)
            }
            if (s.isDraw) return 0
            val allRows = legacyYakuRows(s)
            return maxOf(1, (allRows.size + 5) / 6)
        }

    fun visibleSettlementRows(): List<SettlementDetailRow> {
        val s = currentSettlement ?: return emptyList()
        val bd = s.breakdown
        if (bd != null) {
            return SettlementDetailPresenter.page(bd, settlementSection, settlementPage)
        }
        if (s.isDraw) return emptyList()
        val allRows = legacyYakuRows(s)
        val fromIndex = settlementPage * 6
        if (settlementPage < 0 || fromIndex >= allRows.size) return emptyList()
        val toIndex = minOf(fromIndex + 6, allRows.size)
        return allRows.subList(fromIndex, toIndex)
    }

    fun legacyYakuRows(s: SettlementDetailView): List<SettlementDetailRow> {
        val font = assets.chineseFont
        if (s.yakuList.isEmpty()) {
            return listOf(SettlementDetailRow(if (font != null) "无役" else "No Yaku"))
        }
        val rows = mutableListOf<SettlementDetailRow>()
        for (item in s.yakuList) {
            val lines = wrapLegacyText(item, 580f, font)
            for (line in lines) {
                rows.add(SettlementDetailRow(line))
            }
        }
        return rows
    }

    fun wrapLegacyText(text: String, maxWidth: Float = 580f, font: BitmapFont? = null): List<String> {
        if (text.isEmpty()) return listOf("")
        if (font != null) {
            return SettlementDetailRenderer.wrapRowText(font, text, maxWidth)
        }
        return text.chunked(maxOf(1, (maxWidth / 12f).toInt()))
    }

    val detailModalRect: Rectangle get() = SettlementModalGeometry.MODAL_RECT
    val tabYakuRect: Rectangle get() = SettlementModalGeometry.TAB_YAKU_RECT
    val tabDoraRect: Rectangle get() = SettlementModalGeometry.TAB_DORA_RECT
    val tabCalcRect: Rectangle get() = SettlementModalGeometry.TAB_CALC_RECT
    val btnPrevPageRect: Rectangle get() = SettlementModalGeometry.BTN_PREV_PAGE_RECT
    val btnNextPageRect: Rectangle get() = SettlementModalGeometry.BTN_NEXT_PAGE_RECT

    val btnLegacyPrevPageRect = Rectangle(350f, 250f, 100f, 30f)
    val btnLegacyNextPageRect = Rectangle(810f, 250f, 100f, 30f)

    private val backButtonRect = Rectangle(30f, 655f, 140f, 42f)
    private val btnStartGameRect = Rectangle(500f, 320f, 280f, 60f)
    private val btnTsumoRect = Rectangle(840f, 145f, 150f, 44f)
    val btnRiichiRect = Rectangle(670f, 145f, 140f, 44f)
    val btnCancelRiichiRect = Rectangle(1030f, 145f, 170f, 44f)
    val waitsPanelRect = Rectangle(230f, 210f, 800f, 190f)

    val meldPanelRect = Rectangle(230f, 210f, 800f, 320f)
    val meldCardRects = listOf(
        Rectangle(250f, 280f, 240f, 190f),
        Rectangle(505f, 280f, 240f, 190f),
        Rectangle(760f, 280f, 240f, 190f)
    )
    val btnMeldPrevRect = Rectangle(250f, 225f, 90f, 36f)
    val btnMeldNextRect = Rectangle(920f, 225f, 90f, 36f)
    val btnMeldConfirmRect = Rectangle(670f, 145f, 140f, 44f)
    val btnMeldCancelRect = Rectangle(1030f, 145f, 170f, 44f)
    val btnMeldWinRect = Rectangle(490f, 145f, 160f, 44f)
    val btnMeldPassRect = Rectangle(840f, 145f, 150f, 44f)
    var hoveredMeldCardIndex: Int = -1

    private val btnAbortiveDrawRect = Rectangle(490f, 145f, 160f, 44f)
    private val btnRonRect = Rectangle(730f, 145f, 130f, 44f)
    private val btnPassRect = Rectangle(880f, 145f, 110f, 44f)
    private val btnPonRect = Rectangle(590f, 145f, 110f, 44f)
    private val btnChiRect = Rectangle(460f, 145f, 110f, 44f)
    private val btnKanRect = Rectangle(330f, 145f, 110f, 44f)
    val btnNextRoundRect = Rectangle(520f, 145f, 240f, 46f)
    val btnNextRoundSplitRect = Rectangle(370f, 145f, 260f, 46f)
    val btnAgariYameRect = Rectangle(660f, 145f, 260f, 46f)
    val btnSortRect = Rectangle(1080f, 58f, 140f, 44f)

    var isAutoSort: Boolean = false

    data class HandTileSlot(
        val rect: Rectangle,
        val tile: TileView,
        val isDrawn: Boolean
    )

    val handTileRects = mutableListOf<Pair<Rectangle, TileView>>()

    private val unprojectVec = Vector3()
    private var isBackHovered = false
    private var isSortHovered = false
    private var hoveredTile: TileView? = null
    private var hoveredTileIndex = -1
    private var previousCatchBack = false
    private var ownsBackCatch = false

    data class QueuedDiscard(
        val seat: Int,
        val tile: TileView,
        val targetIndex: Int,
        val start: Pair<Float, Float>,
        val end: Pair<Float, Float>
    )

    val discardQueue = ArrayDeque<QueuedDiscard>()
    var currentDiscardAnim: QueuedDiscard? = null
    var discardAnimElapsed: Float = 0f
    private var lastDiscards: List<List<TileView>> = (0..3).map { currentView.discards.getOrNull(it) ?: emptyList() }

    val isDiscardPlaybackBusy: Boolean
        get() = currentDiscardAnim != null || discardQueue.isNotEmpty()

    private fun clearGameplayHover() {
        isSortHovered = false
        isTakeoverHovered = false
        hoveredMeldCardIndex = -1
        hoveredTile = null
        hoveredTileIndex = -1
    }

    init {
        if (currentView.myAllClosedTiles.isNotEmpty() || currentView.phase != GamePhaseView.STACKING || shufflePreparationPanel.isActive) {
            gameStarted = true
        }

        riichiSelectionModel.update(currentSnapshot?.presentationPreview)
        meldSelectionModel.update(currentSnapshot?.presentationPreview)

        updateSubscription = session.onUpdate { update ->
            runOnUi {
                onUpdateView(update)
            }
        }

        disconnectSubscription = session.onDisconnect { reason ->
            runOnUi {
                showToast("Disconnected: $reason")
            }
        }

        val initialSnap = session.currentSnapshot
        if (initialSnap != null) {
            onSnapshotAndEvents(initialSnap, emptyList())
        } else {
            isLoading = true
            session.snapshot().whenComplete { snap, err ->
                runOnUi {
                    isLoading = false
                    if (err != null) {
                        showToast("Failed to load snapshot: ${err.message ?: "Unknown error"}")
                    } else if (snap != null && currentSnapshot == null) {
                        onSnapshotAndEvents(snap, emptyList())
                    }
                }
            }
        }

        refreshHandTileRects()
    }

    fun onUpdateView(update: GameUpdateView) {
        onSnapshotAndEvents(update.snapshot, update.visibleEvents)
    }

    fun onSnapshotAndEvents(snap: GameSnapshotView, events: List<GameEventView>) {
        isLoading = false
        val epochChanged = (lastSyncEpoch != null && snap.syncEpoch != lastSyncEpoch)
        val roundChanged = (lastRoundId != null && snap.roundId != lastRoundId)
        val cursorJumped = (lastCursor != null && snap.cursor > lastCursor!! + 1L)

        if (epochChanged || roundChanged || cursorJumped) {
            fastForwardDiscardAnimations()
            riichiSelectionModel.cancel()
            meldSelectionModel.cancel()
            lastDiscards = (0..3).map { snap.view.discards.getOrNull(it) ?: emptyList() }
        }
        lastSyncEpoch = snap.syncEpoch
        lastRoundId = snap.roundId
        lastCursor = snap.cursor
        lastClockUpdateLocalMillis = System.currentTimeMillis()

        currentSnapshot = snap
        shufflePreparationPanel.update(snap)
        val isConnected = (session.connectionState == ConnectionState.CONNECTED) && !session.closed
        tileMarkPanel.update(
            snapshot = snap,
            pending = session.isPending,
            connected = isConnected,
            nowMillis = tileMarkPanel.model.monotonicClock()
        )

        if (snap.view.myAllClosedTiles.isNotEmpty() || snap.view.phase != GamePhaseView.STACKING || shufflePreparationPanel.isActive) {
            gameStarted = true
        }

        riichiSelectionModel.update(snap.presentationPreview)
        meldSelectionModel.update(snap.presentationPreview)

        handleSessionEvents(events, snap)

        val targetSettlementId = snap.settlementRoundId
        if (targetSettlementId != null) {
            if (!showSettlement || activeSettlementRoundId != targetSettlementId) {
                val detail = snap.settlementDetail
                if (detail != null) {
                    fastForwardDiscardAnimations()
                    clearGameplayHover()
                    riichiSelectionModel.cancel()
                    meldSelectionModel.cancel()
                    currentSettlement = detail
                    showSettlement = true
                    activeSettlementRoundId = targetSettlementId
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                }
            }
        } else {
            if (showSettlement && !isMatchFinished) {
                showSettlement = false
                currentSettlement = null
                activeSettlementRoundId = null
            }
        }

        checkDiscardsDiff(events)
        refreshHandTileRects()
        if (isMatchFinished && !showSettlement) showFinalResult = true
    }

    private fun handleSessionEvents(events: List<GameEventView>, snap: GameSnapshotView) {
        for (event in events) {
            when (event) {
                is GameEventView.StackingStarted -> {
                    fastForwardDiscardAnimations()
                    showSettlement = false
                    showFinalResult = false
                    currentSettlement = null
                    activeSettlementRoundId = null
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                }
                is GameEventView.TileDiscarded -> {
                    // Handled via discards diff
                }
                is GameEventView.TsumoWin -> {
                    fastForwardDiscardAnimations()
                    if (snap.settlementDetail != null) {
                        currentSettlement = snap.settlementDetail
                        showSettlement = true
                        activeSettlementRoundId = snap.settlementRoundId ?: snap.roundId
                        settlementSection = SettlementSection.YAKU
                        settlementPage = 0
                    }
                }
                is GameEventView.RonWin -> {
                    fastForwardDiscardAnimations()
                    if (snap.settlementDetail != null) {
                        currentSettlement = snap.settlementDetail
                        showSettlement = true
                        activeSettlementRoundId = snap.settlementRoundId ?: snap.roundId
                        settlementSection = SettlementSection.YAKU
                        settlementPage = 0
                    }
                }
                is GameEventView.DrawGame -> {
                    fastForwardDiscardAnimations()
                    if (snap.settlementDetail != null) {
                        currentSettlement = snap.settlementDetail
                        showSettlement = true
                        activeSettlementRoundId = snap.settlementRoundId ?: snap.roundId
                        settlementSection = SettlementSection.YAKU
                        settlementPage = 0
                    }
                }
                is GameEventView.MatchEnded -> {
                    fastForwardDiscardAnimations()
                    if (!showSettlement) {
                        showFinalResult = true
                    }
                }
                else -> Unit
            }
        }
    }

    fun checkDiscardsDiff(events: List<GameEventView> = emptyList()) {
        val view = currentView
        val currentDiscards = view.discards

        // 1. 副露抢牌检测：若当前正在飞行的牌从牌河中消失，说明已被副露移走，快进置空
        val activeAnim = currentDiscardAnim
        if (activeAnim != null) {
            val river = currentDiscards.getOrNull(activeAnim.seat) ?: emptyList()
            if (!river.contains(activeAnim.tile)) {
                currentDiscardAnim = null
                discardAnimElapsed = 0f
            }
        }

        // 队列中若有已被移走的牌同样清除
        if (discardQueue.isNotEmpty()) {
            val iter = discardQueue.iterator()
            while (iter.hasNext()) {
                val queued = iter.next()
                val river = currentDiscards.getOrNull(queued.seat) ?: emptyList()
                if (!river.contains(queued.tile)) {
                    iter.remove()
                }
            }
        }

        // Match newly visible physical tiles against the event stream so a batched
        // update plays discards in chronological order, even across seat changes.
        val pending = linkedMapOf<Pair<Int, TileView>, Int>()
        for (seat in 0..3) {
            val oldList = lastDiscards.getOrNull(seat) ?: emptyList()
            val newList = currentDiscards.getOrNull(seat) ?: emptyList()
            for ((index, tile) in newList.withIndex()) {
                if (tile !in oldList) pending[seat to tile] = index
            }
        }
        fun enqueue(seat: Int, tile: TileView, index: Int) {
            discardQueue.add(QueuedDiscard(seat, tile, index, getDiscardStart(seat), getDiscardSlotPosition(seat, index)))
        }
        for (event in events) {
            if (event is GameEventView.TileDiscarded) {
                val index = pending.remove(event.player to event.tile) ?: continue
                enqueue(event.player, event.tile, index)
            }
        }
        for ((key, index) in pending) enqueue(key.first, key.second, index)
        if (isDiscardPlaybackBusy) clearGameplayHover()

        lastDiscards = (0..3).map { currentDiscards.getOrNull(it) ?: emptyList() }
    }

    fun updateDiscardAnimation(delta: Float) {
        if (currentDiscardAnim == null && discardQueue.isNotEmpty()) {
            currentDiscardAnim = discardQueue.poll()
            discardAnimElapsed = 0f
        }

        val anim = currentDiscardAnim
        if (anim != null) {
            discardAnimElapsed += delta
            if (discardAnimElapsed >= DiscardAnimation.TOTAL_DURATION) {
                currentDiscardAnim = null
                discardAnimElapsed = 0f
                if (discardQueue.isNotEmpty()) {
                    currentDiscardAnim = discardQueue.poll()
                }
            }
        }
    }

    fun fastForwardDiscardAnimations() {
        currentDiscardAnim = null
        discardQueue.clear()
        discardAnimElapsed = 0f
        lastDiscards = (0..3).map { currentView.discards.getOrNull(it) ?: emptyList() }
    }

    fun computeHandSlots(): List<HandTileSlot> {
        val closed = currentView.myClosedTiles
        val drawn = currentView.myDrawnTile
        val startX = 220f
        val xSpacing = 58f
        val tileW = 54f
        val tileH = 76f
        val baseY = 40f

        val result = ArrayList<HandTileSlot>()
        val meldHandTiles = if (meldSelectionModel.isSelecting && !isDiscardPlaybackBusy) {
            meldSelectionModel.selectedOption?.handTiles ?: emptyList()
        } else emptyList()

        if (isAutoSort) {
            val arranged = TileArranger.sortForDisplay(closed, drawn)
            val displayTiles = arranged.tiles
            val drawnIndex = arranged.drawnTileIndex

            for (i in displayTiles.indices) {
                val tile = displayTiles[i]
                val isSelected = (!isDiscardPlaybackBusy && riichiSelectionModel.isSelecting && riichiSelectionModel.selectedTile == tile) ||
                        (!isDiscardPlaybackBusy && meldSelectionModel.isSelecting && meldHandTiles.contains(tile)) ||
                        (!isDiscardPlaybackBusy && tileMarkPanel.model.isSelecting && tileMarkPanel.model.selectedTileIds.contains(tile.tileId))
                val isHover = (!isDiscardPlaybackBusy && (i == hoveredTileIndex || (hoveredTileIndex == -1 && hoveredTile == tile)))
                val x = startX + i * xSpacing
                val y = if (isSelected || isHover) baseY + 12f else baseY
                result.add(HandTileSlot(Rectangle(x, y, tileW, tileH), tile, isDrawn = (i == drawnIndex)))
            }
        } else {
            for (i in closed.indices) {
                val tile = closed[i]
                val isSelected = (!isDiscardPlaybackBusy && riichiSelectionModel.isSelecting && riichiSelectionModel.selectedTile == tile) ||
                        (!isDiscardPlaybackBusy && meldSelectionModel.isSelecting && meldHandTiles.contains(tile)) ||
                        (!isDiscardPlaybackBusy && tileMarkPanel.model.isSelecting && tileMarkPanel.model.selectedTileIds.contains(tile.tileId))
                val isHover = (!isDiscardPlaybackBusy && (i == hoveredTileIndex || (hoveredTileIndex == -1 && hoveredTile == tile)))
                val x = startX + i * xSpacing
                val y = if (isSelected || isHover) baseY + 12f else baseY
                result.add(HandTileSlot(Rectangle(x, y, tileW, tileH), tile, isDrawn = false))
            }

            if (drawn != null) {
                val drawnIdx = closed.size
                val isSelected = (!isDiscardPlaybackBusy && riichiSelectionModel.isSelecting && riichiSelectionModel.selectedTile == drawn) ||
                        (!isDiscardPlaybackBusy && meldSelectionModel.isSelecting && meldHandTiles.contains(drawn)) ||
                        (!isDiscardPlaybackBusy && tileMarkPanel.model.isSelecting && tileMarkPanel.model.selectedTileIds.contains(drawn.tileId))
                val isHover = (!isDiscardPlaybackBusy && (drawnIdx == hoveredTileIndex || (hoveredTileIndex == -1 && hoveredTile == drawn)))
                val drawnX = startX + closed.size * xSpacing + 24f
                val y = if (isSelected || isHover) baseY + 12f else baseY
                result.add(HandTileSlot(Rectangle(drawnX, y, tileW, tileH), drawn, isDrawn = true))
            }
        }
        return result
    }

    fun refreshHandTileRects(): List<HandTileSlot> {
        val slots = computeHandSlots()
        handTileRects.clear()
        for (slot in slots) {
            handTileRects.add(slot.rect to slot.tile)
        }
        return slots
    }

    fun getDiscardStart(seat: Int): Pair<Float, Float> = when ((seat - currentView.viewerSeat + 4) % 4) {
        0 -> Pair(640f, 100f)
        1 -> Pair(1180f, 360f)
        2 -> Pair(640f, 620f)
        3 -> Pair(100f, 360f)
        else -> Pair(640f, 100f)
    }

    fun getDiscardSlotPosition(seat: Int, slotIndex: Int): Pair<Float, Float> {
        val col = slotIndex % 6
        val row = slotIndex / 6
        val tileW = 38f
        val tileH = 52f

        return when ((seat - currentView.viewerSeat + 4) % 4) {
            0 -> Pair(480f + col * (tileW + 2f), 240f - row * (tileH + 4f))
            1 -> Pair(750f + row * (tileH + 4f), 440f - col * (tileW + 2f))
            2 -> Pair(460f + col * (tileW + 2f), 530f - row * (tileH + 4f))
            3 -> Pair(420f - row * (tileH + 4f), 440f - col * (tileW + 2f))
            else -> Pair(480f, 240f)
        }
    }

    fun canHumanRon(): Boolean =
        !isDiscardPlaybackBusy && !isPaused && !isSubmitting && !session.isPending &&
                (currentSnapshot?.decision?.options?.any { it.action is LegalActionView.DeclareRon } == true)

    fun canHumanTsumo(): Boolean =
        !isDiscardPlaybackBusy && !isPaused && !isSubmitting && !session.isPending &&
                (currentSnapshot?.decision?.options?.any { it.action is LegalActionView.DeclareTsumo } == true)

    fun canHumanRiichi(): Boolean =
        !isDiscardPlaybackBusy && !isPaused && !isSubmitting && !session.isPending &&
                (riichiSelectionModel.options.isNotEmpty() || currentSnapshot?.decision?.options?.any { it.action is LegalActionView.DeclareRiichi } == true)

    fun canHumanPon(): Boolean =
        !isDiscardPlaybackBusy && !isPaused && !isSubmitting && !session.isPending &&
                (currentSnapshot?.presentationPreview?.ponOptions?.isNotEmpty() == true || currentSnapshot?.decision?.options?.any { it.action is LegalActionView.DeclarePon } == true)

    fun canHumanChi(): Boolean =
        !isDiscardPlaybackBusy && !isPaused && !isSubmitting && !session.isPending &&
                (currentSnapshot?.presentationPreview?.chiOptions?.isNotEmpty() == true || currentSnapshot?.decision?.options?.any { it.action is LegalActionView.DeclareChi } == true)

    fun canHumanKan(): Boolean =
        !isDiscardPlaybackBusy && !isPaused && !isSubmitting && !session.isPending &&
                (currentSnapshot?.presentationPreview?.kanOptions?.isNotEmpty() == true || currentSnapshot?.decision?.options?.any { it.action is LegalActionView.DeclareKan } == true)

    fun canHumanPass(): Boolean =
        !isDiscardPlaybackBusy && !isPaused && !isSubmitting && !session.isPending &&
                (currentSnapshot?.decision?.options?.any { it.action is LegalActionView.Pass } == true)

    fun canHumanAbortiveDraw(): Boolean =
        !isDiscardPlaybackBusy && !isPaused && !isSubmitting && !session.isPending &&
                (currentSnapshot?.decision?.options?.any { it.action is LegalActionView.DeclareAbortiveDraw } == true)

    fun canHumanAgariYame(): Boolean =
        currentSnapshot?.decision?.options?.any { it.action is LegalActionView.DeclareAgariYame } == true

    fun isHumanTurn(): Boolean =
        currentView.isMyTurn && !isDiscardPlaybackBusy && !isPaused && !isSubmitting && !session.isPending

    fun currentFuritenText(): String? {
        if (!gameStarted || showSettlement || showFinalResult || isMatchFinished) return null
        val furiten = currentView.myFuriten
        if (!furiten.isFuriten) return null
        val reasons = mutableListOf<String>()
        if (furiten.discard) reasons.add("DISCARD")
        if (furiten.temporary) reasons.add("SAME-TURN")
        if (furiten.riichi) reasons.add("RIICHI")
        return "FURITEN - TSUMO ONLY (${reasons.joinToString(", ")})"
    }

    fun previewFuritenText(): String? {
        if (!riichiSelectionModel.isSelecting) return null
        val furiten = hoveredTile?.let { riichiSelectionModel.previewFor(it)?.furiten } ?: return null
        if (!furiten.isFuriten) return null
        val reasons = mutableListOf<String>()
        if (furiten.discard) reasons.add("DISCARD")
        if (furiten.temporary) reasons.add("SAME-TURN")
        if (furiten.riichi) reasons.add("RIICHI")
        return "FURITEN PREVIEW - TSUMO ONLY (${reasons.joinToString(", ")})"
    }

    fun startGame() {
        gameStarted = true
        session.start().whenComplete { receipt, err ->
            runOnUi {
                if (err != null) {
                    showToast("Start failed: ${err.message ?: "Unknown"}")
                } else if (receipt.status == ReceiptStatus.REJECTED) {
                    showToast("Start rejected: ${receipt.reason ?: "Unknown"}")
                }
            }
        }
    }

    fun exitToMenu() {
        navigation.leaveToMenu()
    }

    fun showToast(msg: String) {
        toastMessage = msg
        toastTimer = 3.0f
    }

    private fun submitChoiceAsync(choice: ActionChoice) {
        isSubmitting = true
        session.submit(choice).whenComplete { receipt, err ->
            runOnUi {
                isSubmitting = false
                if (err != null) {
                    showToast("Action failed: ${err.message ?: "Unknown"}")
                    refreshLocalSelectionModels()
                } else if (receipt.status == ReceiptStatus.REJECTED) {
                    showToast("Action rejected: ${receipt.reason ?: "Rejected"}")
                    refreshLocalSelectionModels()
                }
            }
        }
    }

    fun submitTileMarkAsync() {
        if (isSubmittingMark || isSubmitting || session.isPending) return
        val current = currentSnapshot ?: return
        val mark = current.tileMark ?: return
        if (!tileMarkPanel.model.canConfirm) return
        val selected = tileMarkPanel.model.selectedTileIds.toList()
        if (selected.size != 4) return

        val roundId = current.roundId ?: return
        isSubmittingMark = true
        val req = TileMarkRequest(
            requestId = UUID.randomUUID().toString(),
            sessionId = current.sessionId,
            roundId = roundId,
            decisionId = mark.decisionId,
            controlEpoch = mark.controlEpoch,
            tileIds = selected
        )
        session.submitTileMark(req).whenComplete { receipt, err ->
            runOnUi {
                isSubmittingMark = false
                tileMarkPanel.model.cancel()
                if (err != null) {
                    showToast("落汗提交失败: ${err.message ?: err.javaClass.simpleName}")
                } else if (receipt.status == ReceiptStatus.REJECTED) {
                    val reason = TileMarkPanel.translateServerReason(receipt.reason ?: "UNKNOWN")
                    showToast("落汗失败: $reason")
                }
            }
        }
    }

    private fun refreshLocalSelectionModels() {
        riichiSelectionModel.update(currentSnapshot?.presentationPreview)
        meldSelectionModel.update(currentSnapshot?.presentationPreview)
    }

    override fun show() {
        Gdx.input?.let { input ->
            if (!ownsBackCatch) previousCatchBack = input.isCatchKey(Input.Keys.BACK)
            input.setCatchKey(Input.Keys.BACK, true)
            ownsBackCatch = true
            input.inputProcessor = this
        }
    }

    override fun hide() {
        fastForwardDiscardAnimations()
        riichiSelectionModel.cancel()
        meldSelectionModel.cancel()
        Gdx.input?.let { input ->
            if (ownsBackCatch) {
                input.setCatchKey(Input.Keys.BACK, previousCatchBack)
                ownsBackCatch = false
            }
            if (input.inputProcessor == this) {
                input.inputProcessor = null
            }
        }
    }

    override fun render(delta: Float) {
        stateTime += delta
        if (toastTimer > 0f) {
            toastTimer -= delta
            if (toastTimer <= 0f) toastMessage = null
        }

        updateDiscardAnimation(delta)

        val isConnected = (session.connectionState == ConnectionState.CONNECTED) && !session.closed
        tileMarkPanel.update(
            snapshot = currentSnapshot,
            pending = session.isPending,
            connected = isConnected,
            nowMillis = tileMarkPanel.model.monotonicClock()
        )

        camera.update()
        Gdx.gl.glClearColor(0.08f, 0.22f, 0.14f, 1f)
        Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT)

        val batch = assets.batch
        val shape = assets.shapeRenderer
        val font = assets.font
        val chineseFont = assets.chineseFont

        // 1. 桌面中心与四家基本信息
        renderTableCenter(batch, font, chineseFont)
        renderSeatsInfo(batch, shape, font)

        // 2. 牌河
        renderDiscards(batch, shape, font)

        // 3. 副露
        renderMelds(batch, shape, font)

        // 4. 手牌
        renderPlayerHands(batch, font)

        // 5. 正在飞行的打牌动画
        renderFlyingDiscard(batch, shape, font)

        // 6. 立直选牌遮罩 / 副露选牌面板 / 待牌预览
        if (riichiSelectionModel.isSelecting) {
            renderRiichiSelectionOverlay(shape, batch, font)
            renderWaitsPreviewPanel(shape, batch, font)
        } else if (meldSelectionModel.isSelecting) {
            renderMeldSelectionPanel(shape, batch, font)
        }

        // 7. 常驻听牌提示面板
        if (!riichiSelectionModel.isSelecting && !meldSelectionModel.isSelecting && tenpaiWaits.isNotEmpty()) {
            tenpaiPanelRenderer.render(
                camera = camera,
                shape = shape,
                batch = batch,
                fallbackFont = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                waits = tenpaiWaits,
                isOriginalWaits = false,
                isFuriten = currentView.myFuriten.isFuriten
            )
        }

        // 8. 操作按钮
        renderActionButtons(shape, batch, font)

        // 8.5 落汗表现层面板
        tileMarkPanel.render(
            camera = camera,
            shape = shape,
            batch = batch,
            font = chineseFont ?: font,
            snapshot = currentSnapshot,
            isDiscardPlaybackBusy = isDiscardPlaybackBusy,
            isPaused = isPaused,
            isSubmitting = isSubmitting || isSubmittingMark,
            riichiSelecting = riichiSelectionModel.isSelecting,
            meldSelecting = meldSelectionModel.isSelecting,
            tileSkin = tileSkin,
            tileDrawBuffer = tileDrawBuffer
        )

        // 9. 洗牌千术准备弹窗
        if (shufflePreparationPanel.isActive) {
            shufflePreparationPanel.render(camera, shape, batch, font)
        }

        // 10. 结算详情弹窗
        if (showSettlement && currentSettlement != null) {
            val s = currentSettlement!!
            val bd = s.breakdown
            if (bd != null) {
                settlementDetailRenderer.renderDetailModal(
                    camera = camera,
                    shape = shape,
                    batch = batch,
                    fallbackFont = font,
                    tileSkin = tileSkin,
                    tileDrawBuffer = tileDrawBuffer,
                    settlement = s,
                    breakdown = bd,
                    section = settlementSection,
                    page = settlementPage,
                    viewerSeat = currentSnapshot?.viewerSeat ?: 0,
                    currentScores = currentView.scores,
                    canAgariYame = canHumanAgariYame(),
                    isMatchEnd = isMatchFinished,
                    btnNextRoundRect = btnNextRoundRect,
                    btnNextRoundSplitRect = btnNextRoundSplitRect,
                    btnAgariYameRect = btnAgariYameRect
                )
            }
        }

        // 11. 终局总成绩弹窗
        if (showFinalResult) {
            finalResultRenderer.render(
                camera = camera,
                shape = shape,
                batch = batch,
                fallbackFont = font,
                chineseFont = chineseFont,
                finalResult = currentSnapshot?.session?.finalResult,
                sessionView = currentSnapshot?.session,
                currentScores = currentView.scores,
                viewerSeat = currentSnapshot?.viewerSeat ?: 0,
                hasRoundDetails = (currentSettlement != null),
                btnReturnToMenuRect = btnReturnToMenuRect,
                btnRoundDetailsRect = btnRoundDetailsRect
            )
        }

        // 12. Toast 提示
        renderToast(batch, font)
    }

    private fun renderTableCenter(batch: SpriteBatch, font: BitmapFont, chineseFont: BitmapFont?) {
        batch.projectionMatrix = camera.combined
        batch.begin()
        val textFont = chineseFont ?: font
        val origColor = Color(textFont.color)
        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY

        try {
            textFont.data.setScale(1.1f)
            textFont.color = Color(1.0f, 0.85f, 0.35f, 1f)

            val roundWindName = listOf("東", "南", "西", "北").getOrElse(currentView.roundWind - 1) { "東" }
            val roundInfo = "$roundWindName${currentView.roundNumber + 1}局  本场:${currentView.honbaSticks}  供托:${currentView.riichiSticks}"
            textFont.draw(batch, roundInfo, 540f, 400f)

            textFont.color = Color(0.9f, 0.9f, 0.9f, 1f)
            textFont.draw(batch, "牌山剩余: ${currentView.wallRemaining}", 570f, 370f)

            // 宝牌指示牌
            textFont.color = Color(1.0f, 0.6f, 0.2f, 1f)
            textFont.draw(batch, "宝牌指示牌:", 520f, 340f)
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
        }
        batch.end()

        // 渲染宝牌指示牌
        val doras = currentView.doraIndicators
        for (i in doras.indices) {
            val tile = doras[i]
            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = tile,
                x = 640f + i * 32f,
                y = 315f,
                width = 28f,
                height = 38f,
                isHover = false,
                isDrawn = false
            )
        }
        assets.shapeRenderer.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, assets.shapeRenderer, font)
    }

    private fun renderSeatsInfo(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        val viewer = currentSnapshot?.viewerSeat ?: 0
        val clocks = currentSnapshot?.clocks ?: emptyList()
        val elapsed = System.currentTimeMillis() - lastClockUpdateLocalMillis

        for (seat in 0..3) {
            val clock = clocks.firstOrNull { it.seat == seat }
            val formattedClock = ClockDisplay.format(clock, elapsed)
            val score = currentView.scores.getOrElse(seat) { 25000 }
            val isRiichi = currentView.riichiStatus.getOrElse(seat) { false }
            val isCurrent = (currentView.currentTurnPlayer == seat)

            val (x, y) = when ((seat - viewer + 4) % 4) {
                0 -> Pair(40f, 80f)
                1 -> Pair(1150f, 400f)
                2 -> Pair(640f, 670f)
                3 -> Pair(40f, 400f)
                else -> Pair(40f, 80f)
            }

            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(0.95f)
            font.color = if (isCurrent) Color(1.0f, 0.95f, 0.4f, 1f) else Color.WHITE
            val label = if (seat == viewer) "P$seat [YOU]" else "P$seat"
            font.draw(batch, "$label: $score", x, y)
            font.color = if (formattedClock.isUrgent) Color.RED else Color(0.7f, 1.0f, 0.8f, 1f)
            font.draw(batch, formattedClock.displayText, x, y - 18f)

            if (isRiichi) {
                font.color = Color(1.0f, 0.4f, 0.4f, 1f)
                font.draw(batch, "[RIICHI]", x, y - 36f)
            }
            batch.end()
        }
    }

    private fun renderDiscards(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        val viewer = currentSnapshot?.viewerSeat ?: 0
        val anim = currentDiscardAnim
        val riverMarks = TileMarkPresentation.riverMarks(currentSnapshot)
        val markedRiverPositions = mutableListOf<Pair<Float, Float>>()

        for (seat in 0..3) {
            val discards = currentView.discards.getOrElse(seat) { emptyList() }
            for (i in discards.indices) {
                val tile = discards[i]

                val isSuppressed = anim != null && DiscardAnimation.shouldSuppressDiscard(
                    pondSeat = seat,
                    tile = tile,
                    tileIndex = i,
                    animatingSeat = anim.seat,
                    animatingTile = anim.tile,
                    animatingIndex = anim.targetIndex,
                    phase = DiscardAnimation.poseAt(discardAnimElapsed, anim.start, anim.end, anim.seat).phase
                )

                if (isSuppressed) continue

                val isQueued = discardQueue.any { q ->
                    DiscardAnimation.isQueuedRiverTile(seat, tile, i, q.seat, q.tile, q.targetIndex)
                }
                if (isQueued) continue

                val pos = getDiscardSlotPosition(seat, i)
                if (riverMarks.any { it.seat == seat && it.riverPosition?.discardIndex == i && it.tileId == tile.tileId }) {
                    markedRiverPositions.add(pos)
                }
                tileSkin.appendTileDraw(
                    buf = tileDrawBuffer,
                    tile = tile,
                    x = pos.first,
                    y = pos.second,
                    width = 38f,
                    height = 52f,
                    isHover = false,
                    isDrawn = false
                )
            }
        }
        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, font)

        if (markedRiverPositions.isNotEmpty()) {
            shape.projectionMatrix = camera.combined
            for (pos in markedRiverPositions) {
                drawTileMarkBadge(shape, pos.first, pos.second, 38f, 52f)
            }
        }
    }

    private fun renderMelds(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        val groups = MeldDisplayLayout.layoutAll(currentView)
        if (groups.isEmpty()) return
        MeldDisplayRenderer.append(tileDrawBuffer, tileSkin, groups)
        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, font)

        val exposedMarks = currentSnapshot?.tileMark?.activeGroups?.flatMap { it.tiles }
            ?.filter { it.area == TileMarkArea.EXPOSED_MELD } ?: emptyList()
        if (exposedMarks.isNotEmpty()) {
            shape.projectionMatrix = camera.combined
            for (group in groups) {
                for (dt in group.tiles) {
                    if (exposedMarks.any { it.seat == group.ownerSeat && it.tileId == dt.tile.tileId }) {
                        drawTileMarkBadge(shape, dt.x, dt.y, dt.width, dt.height)
                    }
                }
            }
        }
    }

    private fun renderPlayerHands(batch: SpriteBatch, font: BitmapFont) {
        if (!gameStarted) return
        val slots = refreshHandTileRects()

        for (slot in slots) {
            val isHover = (!isDiscardPlaybackBusy && (slot.tile == hoveredTile || (hoveredTileIndex != -1 && slots.getOrNull(hoveredTileIndex)?.tile == slot.tile)))
            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = slot.tile,
                x = slot.rect.x,
                y = slot.rect.y,
                width = slot.rect.width,
                height = slot.rect.height,
                isHover = isHover,
                isDrawn = slot.isDrawn
            )
        }
        assets.shapeRenderer.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, assets.shapeRenderer, font)

        val ownMarkedIds = currentSnapshot?.tileMark?.activeGroups?.flatMap { it.tiles }
            ?.filter { it.area == TileMarkArea.OWN_HAND }
            ?.map { it.tileId }?.toSet() ?: emptySet()
        if (ownMarkedIds.isNotEmpty()) {
            val shape = assets.shapeRenderer
            shape.projectionMatrix = camera.combined
            for (slot in slots) {
                if (slot.tile.tileId in ownMarkedIds) {
                    drawTileMarkBadge(shape, slot.rect.x, slot.rect.y, slot.rect.width, slot.rect.height)
                }
            }
        }
    }

    private fun renderFlyingDiscard(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        val anim = currentDiscardAnim ?: return
        val pose = DiscardAnimation.poseAt(discardAnimElapsed, anim.start, anim.end, anim.seat)
        if (pose.phase == DiscardPhase.DONE) return

        val tileW = 44f * pose.scaleX
        val tileH = 62f * pose.scaleY

        tileSkin.appendTileDraw(
            buf = tileDrawBuffer,
            tile = anim.tile,
            x = pose.x - tileW / 2f,
            y = pose.y - tileH / 2f,
            width = tileW,
            height = tileH,
            isHover = true,
            isDrawn = false
        )
        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, font)

        if (currentSnapshot?.tileMark?.activeGroups?.any { g -> g.tiles.any { it.tileId == anim.tile.tileId } } == true) {
            shape.projectionMatrix = camera.combined
            drawTileMarkBadge(shape, pose.x - tileW / 2f, pose.y - tileH / 2f, tileW, tileH)
        }
    }

    private fun drawTileMarkBadge(shape: ShapeRenderer, x: Float, y: Float, width: Float, height: Float) {
        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.15f, 0.85f, 0.95f, 0.9f)
        shape.rect(x - 1f, y - 1f, width + 2f, height + 2f)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0.10f, 0.75f, 0.95f, 1f)
        val dropX = x + width - 6f
        val dropY = y + height - 6f
        shape.circle(dropX, dropY, 4.5f)
        shape.color = Color(0.85f, 0.95f, 1f, 1f)
        shape.circle(dropX - 1.2f, dropY + 1.2f, 1.8f)
        shape.end()
    }

    private fun renderRiichiSelectionOverlay(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val slots = refreshHandTileRects()
        val legalTiles = riichiSelectionModel.options.map { it.discardTile }

        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0f, 0f, 0f, 0.58f)
        for (slot in slots) {
            if (slot.tile !in legalTiles) {
                shape.rect(slot.rect.x, slot.rect.y, slot.rect.width, slot.rect.height)
            }
        }
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        for (slot in slots) {
            if (slot.tile in legalTiles) {
                val isSelected = (slot.tile == riichiSelectionModel.selectedTile)
                if (isSelected) {
                    shape.color = Color(0.2f, 1.0f, 0.85f, 1f)
                    shape.rect(slot.rect.x - 2f, slot.rect.y - 2f, slot.rect.width + 4f, slot.rect.height + 4f)
                } else {
                    shape.color = Color(1.0f, 0.84f, 0.0f, 1f)
                    shape.rect(slot.rect.x, slot.rect.y, slot.rect.width, slot.rect.height)
                }
            }
        }
        shape.end()
    }

    private fun renderWaitsPreviewPanel(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val previewTile = if (hoveredTile != null && riichiSelectionModel.options.any { it.discardTile == hoveredTile }) {
            hoveredTile
        } else {
            riichiSelectionModel.selectedTile
        }
        val option = previewTile?.let { riichiSelectionModel.previewFor(it) }

        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0.04f, 0.12f, 0.08f, 0.94f)
        shape.rect(waitsPanelRect.x, waitsPanelRect.y, waitsPanelRect.width, waitsPanelRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.85f, 0.72f, 0.35f, 0.95f)
        shape.rect(waitsPanelRect.x, waitsPanelRect.y, waitsPanelRect.width, waitsPanelRect.height)
        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()
        val textFont = assets.chineseFont ?: font
        val oldScaleX = textFont.data.scaleX
        val oldScaleY = textFont.data.scaleY
        val oldColor = Color(textFont.color)
        textFont.data.setScale(1.1f)
        textFont.color = Color(1.0f, 0.85f, 0.3f, 1f)
        if (option == null) {
            textFont.draw(batch, "请在手牌中选择一张打出以立直", waitsPanelRect.x + 30f, waitsPanelRect.y + 120f)
        } else {
            textFont.draw(batch, "打出 ${option.discardTile.displayName()} 后听牌:", waitsPanelRect.x + 30f, waitsPanelRect.y + 150f)
        }
        previewFuritenText()?.let { warning ->
            textFont.data.setScale(0.85f)
            textFont.color = Color(1.0f, 0.38f, 0.3f, 1f)
            textFont.draw(batch, warning, waitsPanelRect.x + 30f, waitsPanelRect.y + 25f)
        }
        textFont.data.setScale(oldScaleX, oldScaleY)
        textFont.color = oldColor
        batch.end()

        if (option != null) {
            for (i in option.waits.indices) {
                val t = option.waits[i]
                tileSkin.appendTileDraw(
                    buf = tileDrawBuffer,
                    tile = t,
                    x = waitsPanelRect.x + 40f + i * 46f,
                    y = waitsPanelRect.y + 50f,
                    width = 40f,
                    height = 56f,
                    isHover = false,
                    isDrawn = false
                )
            }
            shape.projectionMatrix = camera.combined
            batch.projectionMatrix = camera.combined
            tileDrawBuffer.flush(batch, shape, font)
        }
    }

    private fun renderMeldSelectionPanel(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val visibleOptions = meldSelectionModel.visibleOptions

        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0.04f, 0.12f, 0.08f, 0.96f)
        shape.rect(meldPanelRect.x, meldPanelRect.y, meldPanelRect.width, meldPanelRect.height)

        for (i in visibleOptions.indices) {
            val cardRect = meldCardRects[i]
            val isSelected = (meldSelectionModel.selectedOption?.optionId == visibleOptions[i].optionId)
            shape.color = if (isSelected) Color(0.18f, 0.38f, 0.28f, 0.9f) else Color(0.10f, 0.18f, 0.14f, 0.8f)
            shape.rect(cardRect.x, cardRect.y, cardRect.width, cardRect.height)
        }
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.85f, 0.72f, 0.35f, 0.95f)
        shape.rect(meldPanelRect.x, meldPanelRect.y, meldPanelRect.width, meldPanelRect.height)

        for (i in visibleOptions.indices) {
            val cardRect = meldCardRects[i]
            val isSelected = (meldSelectionModel.selectedOption?.optionId == visibleOptions[i].optionId)
            shape.color = if (isSelected) Color(0.3f, 1.0f, 0.6f, 1f) else Color(0.4f, 0.6f, 0.5f, 0.7f)
            shape.rect(cardRect.x, cardRect.y, cardRect.width, cardRect.height)
        }
        shape.end()

        for (i in visibleOptions.indices) {
            val opt = visibleOptions[i]
            val cardRect = meldCardRects[i]
            for (tIdx in opt.tiles.indices) {
                val t = opt.tiles[tIdx]
                tileSkin.appendTileDraw(
                    buf = tileDrawBuffer,
                    tile = t,
                    x = cardRect.x + 20f + tIdx * 48f,
                    y = cardRect.y + 60f,
                    width = 42f,
                    height = 60f,
                    isHover = false,
                    isDrawn = false
                )
            }
        }
        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, font)

        renderMeldActionButtons(shape, batch, font)
    }

    private fun renderMeldActionButtons(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val hasSelection = meldSelectionModel.selectedOption != null
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        shape.color = if (hasSelection) Color(0.85f, 0.45f, 0.15f, 1f) else Color(0.30f, 0.30f, 0.30f, 0.7f)
        shape.rect(btnMeldConfirmRect.x, btnMeldConfirmRect.y, btnMeldConfirmRect.width, btnMeldConfirmRect.height)

        shape.color = Color(0.45f, 0.20f, 0.20f, 1f)
        shape.rect(btnMeldCancelRect.x, btnMeldCancelRect.y, btnMeldCancelRect.width, btnMeldCancelRect.height)

        if (canHumanRon() || canHumanTsumo()) {
            shape.color = if (canHumanRon()) Color(0.88f, 0.18f, 0.15f, 1f) else Color(0.90f, 0.65f, 0.15f, 1f)
            shape.rect(btnMeldWinRect.x, btnMeldWinRect.y, btnMeldWinRect.width, btnMeldWinRect.height)
        }

        if (canHumanPass()) {
            shape.color = Color(0.35f, 0.40f, 0.45f, 1f)
            shape.rect(btnMeldPassRect.x, btnMeldPassRect.y, btnMeldPassRect.width, btnMeldPassRect.height)
        }
        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()
        val textFont = assets.chineseFont ?: font
        textFont.data.setScale(1.1f)
        textFont.color = if (hasSelection) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
        textFont.draw(batch, "确认", btnMeldConfirmRect.x + 48f, btnMeldConfirmRect.y + 29f)

        textFont.color = Color.WHITE
        textFont.draw(batch, "取消", btnMeldCancelRect.x + 60f, btnMeldCancelRect.y + 29f)

        if (canHumanRon()) {
            textFont.draw(batch, "荣和 (RON)", btnMeldWinRect.x + 24f, btnMeldWinRect.y + 29f)
        } else if (canHumanTsumo()) {
            textFont.draw(batch, "自摸 (TSUMO)", btnMeldWinRect.x + 14f, btnMeldWinRect.y + 29f)
        }

        if (canHumanPass()) {
            textFont.draw(batch, "跳过 (PASS)", btnMeldPassRect.x + 28f, btnMeldPassRect.y + 29f)
        }
        batch.end()
    }

    private fun renderActionButtons(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        if (!gameStarted && !shufflePreparationPanel.isActive) {
            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.85f, 0.25f, 0.18f, 1f)
            shape.rect(btnStartGameRect.x, btnStartGameRect.y, btnStartGameRect.width, btnStartGameRect.height)
            shape.end()

            batch.projectionMatrix = camera.combined
            batch.begin()
            val textFont = assets.chineseFont ?: font
            textFont.data.setScale(1.5f)
            textFont.color = Color.WHITE
            textFont.draw(batch, "开始对局", btnStartGameRect.x + 75f, btnStartGameRect.y + 42f)
            batch.end()
            return
        }

        // 理牌开关
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = if (isAutoSort) Color(0.20f, 0.50f, 0.35f, 1f) else Color(0.35f, 0.35f, 0.35f, 1f)
        shape.rect(btnSortRect.x, btnSortRect.y, btnSortRect.width, btnSortRect.height)
        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()
        val textFont = assets.chineseFont ?: font
        textFont.data.setScale(1.0f)
        textFont.color = Color.WHITE
        textFont.draw(batch, if (isAutoSort) "理牌: 开" else "理牌: 关", btnSortRect.x + 30f, btnSortRect.y + 28f)
        batch.end()

        if (isDiscardPlaybackBusy || isPaused || session.isPending || isSubmitting) return

        if (riichiSelectionModel.isSelecting) {
            val hasSelection = riichiSelectionModel.selectedTile != null
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = if (hasSelection) Color(0.85f, 0.45f, 0.15f, 1f) else Color(0.30f, 0.30f, 0.30f, 0.7f)
            shape.rect(btnRiichiRect.x, btnRiichiRect.y, btnRiichiRect.width, btnRiichiRect.height)
            shape.color = Color(0.45f, 0.20f, 0.20f, 1f)
            shape.rect(btnCancelRiichiRect.x, btnCancelRiichiRect.y, btnCancelRiichiRect.width, btnCancelRiichiRect.height)
            shape.end()

            batch.begin()
            textFont.color = if (hasSelection) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
            textFont.draw(batch, "确认立直", btnRiichiRect.x + 28f, btnRiichiRect.y + 28f)
            textFont.color = Color.WHITE
            textFont.draw(batch, "取消", btnCancelRiichiRect.x + 60f, btnCancelRiichiRect.y + 29f)
            batch.end()
            return
        }

        if (meldSelectionModel.isSelecting) return

        // 普通回合按钮
        if (canHumanTsumo()) {
            drawButton(shape, batch, textFont, btnTsumoRect, Color(0.90f, 0.65f, 0.15f, 1f), "自摸 (TSUMO)")
        }
        if (canHumanRiichi()) {
            drawButton(shape, batch, textFont, btnRiichiRect, Color(0.85f, 0.45f, 0.15f, 1f), "立直 (RIICHI)")
        }
        if (canHumanAbortiveDraw()) {
            drawButton(shape, batch, textFont, btnAbortiveDrawRect, Color(0.60f, 0.25f, 0.45f, 1f), "九种九牌")
        }
        if (canHumanRon()) {
            drawButton(shape, batch, textFont, btnRonRect, Color(0.88f, 0.18f, 0.15f, 1f), "荣和 (RON)")
        }
        if (canHumanPass()) {
            drawButton(shape, batch, textFont, btnPassRect, Color(0.35f, 0.40f, 0.45f, 1f), "过 (PASS)")
        }
        if (canHumanPon()) {
            drawButton(shape, batch, textFont, btnPonRect, Color(0.20f, 0.55f, 0.85f, 1f), "碰 (PON)")
        }
        if (canHumanChi()) {
            drawButton(shape, batch, textFont, btnChiRect, Color(0.25f, 0.65f, 0.35f, 1f), "吃 (CHI)")
        }
        if (canHumanKan()) {
            drawButton(shape, batch, textFont, btnKanRect, Color(0.70f, 0.35f, 0.80f, 1f), "杠 (KAN)")
        }
    }

    private fun drawButton(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont, rect: Rectangle, color: Color, text: String) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = color
        shape.rect(rect.x, rect.y, rect.width, rect.height)
        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()
        font.data.setScale(1.1f)
        font.color = Color.WHITE
        font.draw(batch, text, rect.x + 12f, rect.y + 29f)
        batch.end()
    }

    private fun renderToast(batch: SpriteBatch, font: BitmapFont) {
        val msg = toastMessage ?: return
        batch.projectionMatrix = camera.combined
        batch.begin()
        val textFont = assets.chineseFont ?: font
        textFont.data.setScale(1.2f)
        textFont.color = Color(1.0f, 0.3f, 0.3f, 1f)
        textFont.draw(batch, msg, 400f, 650f)
        batch.end()
    }

    override fun keyDown(keycode: Int): Boolean {
        if (keycode == Input.Keys.ESCAPE || keycode == Input.Keys.BACK) {
            if (tileMarkPanel.model.isSelecting) {
                tileMarkPanel.model.cancel()
                return true
            }
            if (meldSelectionModel.isSelecting) {
                meldSelectionModel.cancel()
                return true
            }
            if (riichiSelectionModel.isSelecting) {
                riichiSelectionModel.cancel()
                return true
            }
            if (showSettlement && isMatchFinished) {
                showSettlement = false
                showFinalResult = true
                return true
            }
            exitToMenu()
            return true
        }
        return false
    }

    override fun touchDown(screenX: Int, screenY: Int, pointer: Int, button: Int): Boolean {
        val tx: Float
        val ty: Float
        if (Gdx.graphics == null || Gdx.graphics.width == 0) {
            tx = screenX.toFloat()
            ty = screenY.toFloat()
        } else {
            unprojectVec.set(screenX.toFloat(), screenY.toFloat(), 0f)
            camera.unproject(unprojectVec)
            tx = unprojectVec.x
            ty = unprojectVec.y
        }

        val wasMeldSelecting = meldSelectionModel.isSelecting
        val wasRiichiSelecting = riichiSelectionModel.isSelecting
        val wasTileMarkSelecting = tileMarkPanel.model.isSelecting

        val isConnected = (session.connectionState == ConnectionState.CONNECTED) && !session.closed
        tileMarkPanel.update(
            snapshot = currentSnapshot,
            pending = session.isPending,
            connected = isConnected,
            nowMillis = tileMarkPanel.model.monotonicClock()
        )

        if (backButtonRect.contains(tx, ty)) {
            exitToMenu()
            return true
        }

        if (showFinalResult) {
            if (btnReturnToMenuRect.contains(tx, ty)) {
                exitToMenu()
                return true
            }
            if (currentSettlement != null && btnRoundDetailsRect.contains(tx, ty)) {
                showFinalResult = false
                showSettlement = true
                return true
            }
            return true
        }

        if (shufflePreparationPanel.isActive) {
            shufflePreparationPanel.touchDown(tx, ty)
            return true
        }

        // 选牌模式拦截：优先处理落汗选牌交互
        if (tileMarkPanel.model.isSelecting) {
            if (tileMarkPanel.btnCancelRect.contains(tx, ty)) {
                tileMarkPanel.model.cancel()
                return true
            }
            if (tileMarkPanel.btnConfirmRect.contains(tx, ty)) {
                submitTileMarkAsync()
                return true
            }
            val slots = refreshHandTileRects()
            for (slot in slots) {
                val hitRect = Rectangle(slot.rect.x, 40f, slot.rect.width, 94f)
                if (hitRect.contains(tx, ty) || slot.rect.contains(tx, ty)) {
                    tileMarkPanel.model.toggle(slot.tile.tileId)
                    return true
                }
            }
            return true
        }

        val modalInvalidatedByRefresh = (wasMeldSelecting && !meldSelectionModel.isSelecting) ||
                (wasRiichiSelecting && !riichiSelectionModel.isSelecting) ||
                (wasTileMarkSelecting && !tileMarkPanel.model.isSelecting)
        if (modalInvalidatedByRefresh) {
            return true
        }

        // 托管切换
        if (gameStarted && !isLoading && !isMatchFinished && !showSettlement && btnTakeoverRect.contains(tx, ty)) {
            val controls = currentSnapshot?.controls ?: emptyList()
            val myControl = controls.firstOrNull { it.seat == (currentSnapshot?.viewerSeat ?: 0) }
            val isTakeover = (myControl?.role == "TAKEOVER")
            if (isTakeover) {
                session.claimControl().whenComplete { receipt, err ->
                    runOnUi {
                        if (err != null) showToast("Claim control failed: ${err.message}")
                        else if (receipt.status == ReceiptStatus.REJECTED) showToast("Claim rejected: ${receipt.reason}")
                    }
                }
            } else {
                session.releaseControl().whenComplete { receipt, err ->
                    runOnUi {
                        if (err != null) showToast("Release control failed: ${err.message}")
                        else if (receipt.status == ReceiptStatus.REJECTED) showToast("Release rejected: ${receipt.reason}")
                    }
                }
            }
            return true
        }

        // 结算弹窗
        if (showSettlement) {
            val bd = currentSettlement?.breakdown
            if (bd != null) {
                if (tabYakuRect.contains(tx, ty)) {
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                    return true
                }
                if (tabDoraRect.contains(tx, ty)) {
                    settlementSection = SettlementSection.DORA
                    settlementPage = 0
                    return true
                }
                if (tabCalcRect.contains(tx, ty)) {
                    settlementSection = SettlementSection.CALC
                    settlementPage = 0
                    return true
                }
                if (btnPrevPageRect.contains(tx, ty)) {
                    if (settlementPage > 0) settlementPage--
                    return true
                }
                if (btnNextPageRect.contains(tx, ty)) {
                    if (settlementPage < settlementPageCount - 1) settlementPage++
                    return true
                }
            } else {
                if (btnLegacyPrevPageRect.contains(tx, ty)) {
                    if (settlementPage > 0) settlementPage--
                    return true
                }
                if (btnLegacyNextPageRect.contains(tx, ty)) {
                    if (settlementPage < settlementPageCount - 1) settlementPage++
                    return true
                }
            }

            if (canHumanAgariYame()) {
                if (btnAgariYameRect.contains(tx, ty)) {
                    val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.DeclareAgariYame }
                    if (opt != null) {
                        submitChoiceAsync(
                            ActionChoice(
                                requestId = UUID.randomUUID().toString(),
                                roundId = currentSnapshot?.roundId,
                                decisionId = currentSnapshot?.decision?.decisionId ?: "",
                                controlEpoch = currentSnapshot?.decision?.controlEpoch ?: 0L,
                                optionId = opt.optionId
                            )
                        )
                    }
                    return true
                }
                if (btnNextRoundSplitRect.contains(tx, ty)) {
                    if (isNextRoundSubmitting || isSubmitting || session.isPending) return true
                    isNextRoundSubmitting = true
                    session.nextRound().whenComplete { receipt, error ->
                        runOnUi {
                            isNextRoundSubmitting = false
                            if (error != null) showToast("Next round failed: ${error.message}")
                            else if (receipt.status == ReceiptStatus.REJECTED) showToast("Next round rejected: ${receipt.reason}")
                        }
                    }
                    return true
                }
            } else if (isMatchFinished) {
                if (btnNextRoundRect.contains(tx, ty)) {
                    showSettlement = false
                    showFinalResult = true
                    riichiSelectionModel.cancel()
                    meldSelectionModel.cancel()
                    return true
                }
            } else {
                if (btnNextRoundRect.contains(tx, ty)) {
                    if (isNextRoundSubmitting || isSubmitting || session.isPending) return true
                    isNextRoundSubmitting = true
                    session.nextRound().whenComplete { receipt, error ->
                        runOnUi {
                            isNextRoundSubmitting = false
                            if (error != null) showToast("Next round failed: ${error.message}")
                            else if (receipt.status == ReceiptStatus.REJECTED) showToast("Next round rejected: ${receipt.reason}")
                        }
                    }
                    return true
                }
            }
            return true
        }

        if (isDiscardPlaybackBusy || isLoading || session.isPending || isSubmitting || isSubmittingMark || isPaused) {
            return true
        }

        // 理牌开关
        if (btnSortRect.contains(tx, ty)) {
            isAutoSort = !isAutoSort
            return true
        }

        // 开赛
        if (!gameStarted) {
            if (btnStartGameRect.contains(tx, ty)) {
                startGame()
                return true
            }
            return false
        }

        // 立直选牌
        if (riichiSelectionModel.isSelecting) {
            if (btnCancelRiichiRect.contains(tx, ty)) {
                riichiSelectionModel.cancel()
                return true
            }
            if (btnRiichiRect.contains(tx, ty)) {
                val opt = riichiSelectionModel.confirmationOption()
                val latestOpts = currentSnapshot?.decision?.options ?: emptyList()
                if (opt != null && latestOpts.any { it.optionId == opt.optionId }) {
                    riichiSelectionModel.cancel()
                    submitChoiceAsync(
                        ActionChoice(
                            requestId = UUID.randomUUID().toString(),
                            roundId = currentSnapshot?.roundId,
                            decisionId = currentSnapshot?.decision?.decisionId ?: "",
                            controlEpoch = currentSnapshot?.decision?.controlEpoch ?: 0L,
                            optionId = opt.optionId
                        )
                    )
                    return true
                }
                return true
            }

            val slots = refreshHandTileRects()
            for (slot in slots) {
                val hitRect = Rectangle(slot.rect.x, 40f, slot.rect.width, 94f)
                if (hitRect.contains(tx, ty) || slot.rect.contains(tx, ty)) {
                    if (riichiSelectionModel.select(slot.tile)) {
                        hoveredTile = slot.tile
                    }
                    return true
                }
            }
            return true
        }

        // 副露选牌
        if (meldSelectionModel.isSelecting) {
            if (btnMeldCancelRect.contains(tx, ty)) {
                meldSelectionModel.cancel()
                return true
            }
            if (btnMeldConfirmRect.contains(tx, ty)) {
                val opt = meldSelectionModel.confirmationOption()
                val latestOpts = currentSnapshot?.decision?.options ?: emptyList()
                if (opt != null && latestOpts.any { it.optionId == opt.optionId }) {
                    meldSelectionModel.cancel()
                    submitChoiceAsync(
                        ActionChoice(
                            requestId = UUID.randomUUID().toString(),
                            roundId = currentSnapshot?.roundId,
                            decisionId = currentSnapshot?.decision?.decisionId ?: "",
                            controlEpoch = currentSnapshot?.decision?.controlEpoch ?: 0L,
                            optionId = opt.optionId
                        )
                    )
                    return true
                }
                return true
            }
            if ((canHumanRon() || canHumanTsumo()) && btnMeldWinRect.contains(tx, ty)) {
                meldSelectionModel.cancel()
                if (canHumanRon()) {
                    val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.DeclareRon }
                    if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
                } else if (canHumanTsumo()) {
                    val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.DeclareTsumo }
                    if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
                }
                return true
            }
            if (canHumanPass() && btnMeldPassRect.contains(tx, ty)) {
                meldSelectionModel.cancel()
                val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.Pass }
                if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
                return true
            }
            if (btnMeldPrevRect.contains(tx, ty)) {
                if (meldSelectionModel.pageIndex > 0) meldSelectionModel.setPage(meldSelectionModel.pageIndex - 1)
                return true
            }
            if (btnMeldNextRect.contains(tx, ty)) {
                if (meldSelectionModel.pageIndex < meldSelectionModel.pageCount - 1) meldSelectionModel.setPage(meldSelectionModel.pageIndex + 1)
                return true
            }
            for (i in meldSelectionModel.visibleOptions.indices) {
                val cardRect = meldCardRects[i]
                if (cardRect.contains(tx, ty)) {
                    meldSelectionModel.select(meldSelectionModel.visibleOptions[i])
                    return true
                }
            }
            return true
        }

        // 普通回合操作
        if (canHumanRon() && btnRonRect.contains(tx, ty)) {
            val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.DeclareRon }
            if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
            return true
        }
        if (canHumanPass() && btnPassRect.contains(tx, ty)) {
            val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.Pass }
            if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
            return true
        }
        if (canHumanPon() && btnPonRect.contains(tx, ty)) {
            if (riichiSelectionModel.isSelecting) riichiSelectionModel.cancel()
            meldSelectionModel.begin(MeldChoiceKindView.PON)
            return true
        }
        if (canHumanChi() && btnChiRect.contains(tx, ty)) {
            if (riichiSelectionModel.isSelecting) riichiSelectionModel.cancel()
            meldSelectionModel.begin(MeldChoiceKindView.CHI)
            return true
        }
        if (canHumanKan() && btnKanRect.contains(tx, ty)) {
            if (riichiSelectionModel.isSelecting) riichiSelectionModel.cancel()
            meldSelectionModel.begin(MeldChoiceKindView.KAN)
            return true
        }
        if (canHumanTsumo() && btnTsumoRect.contains(tx, ty)) {
            val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.DeclareTsumo }
            if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
            return true
        }
        if (canHumanRiichi() && btnRiichiRect.contains(tx, ty)) {
            if (meldSelectionModel.isSelecting) meldSelectionModel.cancel()
            return riichiSelectionModel.begin()
        }
        if (canHumanAbortiveDraw() && btnAbortiveDrawRect.contains(tx, ty)) {
            val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.DeclareAbortiveDraw }
            if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
            return true
        }

        // 落汗千术选牌入口
        val mark = currentSnapshot?.tileMark
        val canOpenTileMark = mark != null && mark.canApply &&
            !isDiscardPlaybackBusy &&
            !isPaused &&
            !session.isPending &&
            !isSubmitting &&
            !isSubmittingMark &&
            !riichiSelectionModel.isSelecting &&
            !meldSelectionModel.isSelecting
        if (canOpenTileMark && tileMarkPanel.btnOpenRect.contains(tx, ty)) {
            tileMarkPanel.model.begin()
            return true
        }

        // 落汗追踪翻页
        val activeGroupCount = currentSnapshot?.tileMark?.activeGroups?.size ?: 0
        if (tileMarkPanel.touchDown(tx, ty, activeGroupCount)) {
            return true
        }

        // 人类出牌点击
        if (isHumanTurn()) {
            val slots = refreshHandTileRects()
            for (slot in slots) {
                val hitRect = Rectangle(slot.rect.x, 40f, slot.rect.width, 94f)
                if (hitRect.contains(tx, ty) || slot.rect.contains(tx, ty)) {
                    val discardOpt = currentSnapshot?.decision?.options?.firstOrNull {
                        val act = it.action
                        act is LegalActionView.DiscardTile && act.tile.tileId == slot.tile.tileId
                    }
                    if (discardOpt != null) {
                        submitChoiceAsync(
                            ActionChoice(
                                requestId = UUID.randomUUID().toString(),
                                roundId = currentSnapshot?.roundId,
                                decisionId = currentSnapshot?.decision?.decisionId ?: "",
                                controlEpoch = currentSnapshot?.decision?.controlEpoch ?: 0L,
                                optionId = discardOpt.optionId
                            )
                        )
                        return true
                    }
                }
            }
        }

        return false
    }

    override fun mouseMoved(screenX: Int, screenY: Int): Boolean {
        val tx: Float
        val ty: Float
        if (Gdx.graphics == null || Gdx.graphics.width == 0) {
            tx = screenX.toFloat()
            ty = screenY.toFloat()
        } else {
            unprojectVec.set(screenX.toFloat(), screenY.toFloat(), 0f)
            camera.unproject(unprojectVec)
            tx = unprojectVec.x
            ty = unprojectVec.y
        }

        if (isDiscardPlaybackBusy) {
            hoveredTile = null
            hoveredTileIndex = -1
            return false
        }

        val slots = refreshHandTileRects()
        var foundSlot: HandTileSlot? = null
        var foundIdx = -1
        for (i in slots.indices) {
            val slot = slots[i]
            val hitRect = Rectangle(slot.rect.x, 40f, slot.rect.width, 94f)
            if (hitRect.contains(tx, ty) || slot.rect.contains(tx, ty)) {
                foundSlot = slot
                foundIdx = i
                break
            }
        }
        hoveredTile = foundSlot?.tile
        hoveredTileIndex = foundIdx

        if (shufflePreparationPanel.isActive) {
            shufflePreparationPanel.mouseMoved(tx, ty)
        }
        return false
    }

    override fun resize(width: Int, height: Int) {}
    override fun pause() {}
    override fun resume() {}

    override fun dispose() {
        if (isDisposed) return
        isDisposed = true
        fastForwardDiscardAnimations()
        updateSubscription?.close()
        updateSubscription = null
        disconnectSubscription?.close()
        disconnectSubscription = null
        Gdx.input?.let { input ->
            if (ownsBackCatch) {
                input.setCatchKey(Input.Keys.BACK, previousCatchBack)
                ownsBackCatch = false
            }
            if (input.inputProcessor == this) {
                input.inputProcessor = null
            }
        }
        tileMarkPanel.dispose()
        shufflePreparationPanel.dispose()
        settlementDetailRenderer.dispose()
    }
}

private fun defaultEmptyPlayerView(viewerSeat: Int): PlayerView = PlayerView(
    viewerSeat = viewerSeat,
    phase = GamePhaseView.STACKING,
    roundWind = 1,
    roundNumber = 0,
    honbaSticks = 0,
    riichiSticks = 0,
    dealerSeat = 0,
    currentTurnPlayer = 0,
    wallRemaining = 136,
    doraIndicators = emptyList(),
    scores = listOf(25000, 25000, 25000, 25000),
    riichiStatus = listOf(false, false, false, false),
    discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
    melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
    handTileCounts = listOf(0, 0, 0, 0),
    myClosedTiles = emptyList(),
    myDrawnTile = null,
    legalActions = emptyList()
)
