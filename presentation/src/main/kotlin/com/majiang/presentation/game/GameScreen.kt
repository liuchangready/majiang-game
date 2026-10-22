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
import com.badlogic.gdx.math.Vector2
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
import com.majiang.client.api.BeamSwapAction
import com.majiang.client.api.BeamSwapChoiceRequest
import com.majiang.client.api.BeamSwapRequest
import com.majiang.client.api.SleeveBloomChoiceRequest
import com.majiang.client.api.SleeveBloomKeep
import com.majiang.client.api.SleeveBloomOfferMode
import com.majiang.client.api.SleeveBloomOfferView
import com.majiang.client.api.SleeveBloomOffTurnChoiceRequest
import com.majiang.client.api.SleeveBloomRequest
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
import com.majiang.presentation.common.render.TableBoardRenderModel
import com.majiang.presentation.common.render.TableBoardRenderer
import com.majiang.presentation.common.render.TableBoardRenderOptions
import com.majiang.presentation.common.render.TableBoardSyncTracker
import com.majiang.presentation.common.render.TableBoardViewIdentity
import com.majiang.presentation.common.render.TableHudView
import com.majiang.presentation.common.render.TableLocalZoom
import com.majiang.presentation.common.render.TableStageFonts
import com.majiang.presentation.common.render.TableStageLayout
import com.majiang.presentation.common.render.TableStageRenderer
import com.majiang.presentation.common.render.TableStageViewport
import com.majiang.presentation.common.render.TileArranger
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin
import com.majiang.presentation.common.render.fromPlayerSnapshot
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

    val tableCamera = OrthographicCamera().apply {
        setToOrtho(false, 1280f, 720f)
    }

    fun isModalActive(): Boolean = modalCoordinator.isModalActive

    // 会话订阅与快照
    private var updateSubscription: AutoCloseable? = null
    private var disconnectSubscription: AutoCloseable? = null
    var currentSnapshot: GameSnapshotView? = session.currentSnapshot
    val currentView: PlayerView
        get() = currentSnapshot?.view ?: defaultEmptyPlayerView(0)
    var isLoading: Boolean = (currentSnapshot == null)
    /** 本屏独立的同步游标/代次/换局状态；绝不与其他屏幕共享。 */
    private val syncTracker = TableBoardSyncTracker(
        currentSnapshot?.let {
            TableBoardViewIdentity(it.sessionId, it.syncEpoch, it.roundId, it.cursor)
        }
    )
    private var lastClockUpdateLocalMillis: Long = System.currentTimeMillis()
    private var activeSettlementRoundId: String? = null

    val isPaused: Boolean get() = currentSnapshot?.paused == true

    @Volatile
    var isDisposed: Boolean = false
        private set

    /**
     * 正式 APP 唯一输入反变换：窗口逻辑坐标 → 1280×720 stage 局部坐标。
     * 舞台外返回 null；不得再对 camera 做默认全窗口 unproject。
     */
    @JvmOverloads
    fun screenToStage(screenX: Int, screenY: Int, out: Vector2 = Vector2()): Vector2? =
        stageViewport.toStage(screenX.toFloat(), screenY.toFloat(), out)

    /** 测试/点击驱动使用的 stage → 窗口正向变换。 */
    @JvmOverloads
    fun stageToScreen(localX: Float, localY: Float, out: Vector2 = Vector2()): Vector2 =
        stageViewport.toWindow(localX, localY, out)

    fun getStageViewport(): TableStageViewport = stageViewport

    fun getStageFonts(): TableStageFonts = stageFonts

    private fun screenToUi(screenX: Int, screenY: Int): Vector2? {
        if (usesHeadlessStageInput) {
            if (screenX < 0 || screenY < 0 ||
                screenX > TableStageLayout.DESIGN_WIDTH.toInt() ||
                screenY > TableStageLayout.DESIGN_HEIGHT.toInt()
            ) {
                return null
            }
            return uiPoint.set(screenX.toFloat(), screenY.toFloat())
        }
        return stageViewport.toStage(screenX.toFloat(), screenY.toFloat(), uiPoint)
    }

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
    val btnTakeoverRect: Rectangle
        get() = RightActionColumnLayout.takeoverRect()
    private var isTakeoverHovered = false

    // 选牌模型与准备面板
    val riichiSelectionModel = RiichiSelectionModel()
    val meldSelectionModel = MeldSelectionModel()
    val shufflePreparationPanel = ShufflePreparationPanel(session, assets)
    val tileMarkPanel = TileMarkPanel(session)
    val sleeveBloomPanel = SleeveBloomPanel(session)
    val beamSwapPanel = BeamSwapPanel(session)
    val riverHintPanel = RiverHintPanel()
    private var isSubmittingMark = false
    private var isSubmittingBloom = false
    private var isSubmittingBloomChoice = false
    private var isSubmittingBeamSwap = false
    private var isSubmittingBeamSwapChoice = false
    private var activeBloomOffer: SleeveBloomOfferView? = null
    private var bloomChoiceSubmitted: Boolean = false

    private var lastMouseX = 0f
    private var lastMouseY = 0f

    fun getActionButtonRect(base: Rectangle): Rectangle = base

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

    val modalCoordinator = ModalPriorityCoordinator(
        isFinalResultActive = { showFinalResult },
        isSettlementActive = { showSettlement },
        isShufflePreparationActive = { shufflePreparationPanel.isActive },
        isTileMarkActive = { tileMarkPanel.model.isSelecting },
        isSleeveBloomActive = {
            sleeveBloomPanel.model.isChoosing && sleeveBloomPanel.model.isImmediateChoice
        },
        isBeamSwapActive = { beamSwapPanel.model.isChoosing },
        isRiichiActive = { riichiSelectionModel.isSelecting },
        isMeldActive = { meldSelectionModel.isSelecting },
        isRiverHintOpen = { riverHintPanel.isOpen },
        isMatchFinished = { isMatchFinished }
    )

    val tenpaiWaits: List<TileView>
        get() = currentSnapshot?.presentationPreview?.waits ?: emptyList()

    private val finalResultRenderer = FinalResultRenderer()
    private val tenpaiPanelRenderer = TenpaiPanelRenderer()
    private val settlementDetailRenderer = SettlementDetailRenderer(assets)

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
    val btnSortRect: Rectangle
        get() = RightActionColumnLayout.sortRect()

    var isAutoSort: Boolean = false

    data class HandTileSlot(
        val rect: Rectangle,
        val tile: TileView,
        val isDrawn: Boolean
    )

    val handTileRects = mutableListOf<Pair<Rectangle, TileView>>()

    private var stageViewport: TableStageViewport = initialStageViewport()
    private var stageFonts: TableStageFonts = TableStageFonts.create(assets, 1f, 1f)
    private var stageFontsKey: Triple<Float, Float, Float>? = null
    private val uiPoint = Vector2()
    private val tablePoint = Vector2()
    /**
     * 无头单元测试没有窗口帧与 unproject；历史测试直接把 1280×720 世界坐标传给
     * touchDown。该标志只在构建时 Gdx 窗口为零时启用，正式最小化不会走此分支。
     */
    private val usesHeadlessStageInput: Boolean =
        Gdx.graphics == null || Gdx.graphics.width <= 0 || Gdx.graphics.height <= 0
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

    /** 弃牌飞行动画状态控制器 */
    val discardAnimationController = DiscardAnimationController(
        startPositionProvider = { seat -> getDiscardStart(seat) },
        slotPositionProvider = { seat, slotIndex -> getDiscardSlotPosition(seat, slotIndex) },
        initialDiscards = (0..3).map { currentView.discards.getOrNull(it) ?: emptyList() },
        onPlaybackBusyChanged = { busy -> if (busy) clearGameplayHover() }
    )

    val discardQueue: ArrayDeque<QueuedDiscard>
        get() = discardAnimationController.discardQueue

    var currentDiscardAnim: QueuedDiscard?
        get() = discardAnimationController.currentDiscardAnim
        set(value) { discardAnimationController.currentDiscardAnim = value }

    var discardAnimElapsed: Float
        get() = discardAnimationController.discardAnimElapsed
        set(value) { discardAnimationController.discardAnimElapsed = value }

    var lastDiscards: List<List<TileView>>
        get() = discardAnimationController.lastDiscards
        set(value) { discardAnimationController.lastDiscards = value }

    val isDiscardPlaybackBusy: Boolean
        get() = discardAnimationController.isDiscardPlaybackBusy

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

        refreshRiichiSelection(currentSnapshot)
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
        onSnapshotAndEvents(update.snapshot, update.visibleEvents, update.isFullSnapshot)
    }

    fun onSnapshotAndEvents(
        snap: GameSnapshotView,
        events: List<GameEventView>,
        isFullSnapshot: Boolean = false
    ) {
        isLoading = false
        val transition = syncTracker.observe(
            TableBoardViewIdentity(snap.sessionId, snap.syncEpoch, snap.roundId, snap.cursor),
            isFullSnapshot = isFullSnapshot
        )
        val epochChanged = transition.epochChanged
        val roundChanged = transition.roundChanged
        val sessionChanged = transition.sessionChanged

        // 本地径路（含断线恢复）与显式全量快照都按“重建基线”处理：清空旧动画
        // 与旧基准，绝不把重建快照里的历史牌当成新打出的牌重播。
        if (transition.requiresRebuild) {
            riichiSelectionModel.cancel()
            meldSelectionModel.cancel()
        }
        // 重连（换同步代次 / 换会话）回到默认准备栏：已确认显示锁定回执，未确认不拼接旧选择。
        if (transition.epochChanged || transition.sessionChanged) {
            shufflePreparationPanel.onReconnect()
        }
        lastClockUpdateLocalMillis = System.currentTimeMillis()

        currentSnapshot = snap
        if (transition.requiresRebuild) {
            discardAnimationController.resetBaseline(snap.view.discards)
        }
        shufflePreparationPanel.update(snap)
        val isConnected = (session.connectionState == ConnectionState.CONNECTED) && !session.closed
        tileMarkPanel.update(
            snapshot = snap,
            pending = session.isPending,
            connected = isConnected,
            nowMillis = tileMarkPanel.model.monotonicClock()
        )
        sleeveBloomPanel.update(
            snapshot = snap,
            pending = session.isPending,
            connected = isConnected,
            nowMillis = sleeveBloomPanel.model.monotonicClock()
        )

        val oldOffer = activeBloomOffer
        val newOffer = snap.sleeveBloom?.pendingOffer
        if (oldOffer != null && newOffer == null) {
            val latestServerNow = snap.clocks.map { it.serverNow }.maxOrNull() ?: 0L
            val myDiscards = snap.view.discards.getOrNull(snap.viewerSeat) ?: emptyList()
            val firstTileDiscarded = (myDiscards.lastOrNull()?.tileId == oldOffer.firstTile.tileId) ||
                (snap.view.lastDiscard?.tileId == oldOffer.firstTile.tileId && snap.view.lastDiscardPlayer == snap.viewerSeat)
            val myControl = snap.controls.firstOrNull { it.seat == snap.viewerSeat }
            val isTakeover = (myControl?.role == "TAKEOVER")
            val isTimeout = (latestServerNow >= oldOffer.deadlineMillis && oldOffer.deadlineMillis > 0L) &&
                (oldOffer.mode == SleeveBloomOfferMode.OFF_TURN || firstTileDiscarded) &&
                !isTakeover
            if (isTimeout && !bloomChoiceSubmitted && !roundChanged && !epochChanged && !sessionChanged &&
                snap.view.phase != GamePhaseView.ROUND_END && snap.view.phase != GamePhaseView.MATCH_END) {
                showToast("超时已默认保留第一张，额度不退")
            }
            activeBloomOffer = null
            bloomChoiceSubmitted = false
        } else if (newOffer != null) {
            if (newOffer.offerId != oldOffer?.offerId) {
                activeBloomOffer = newOffer
                bloomChoiceSubmitted = false
            }
        }
        if (roundChanged || epochChanged || sessionChanged) {
            activeBloomOffer = newOffer
            bloomChoiceSubmitted = false
        }

        if (snap.view.myAllClosedTiles.isNotEmpty() || snap.view.phase != GamePhaseView.STACKING || shufflePreparationPanel.isActive) {
            gameStarted = true
        }

        refreshRiichiSelection(snap)
        meldSelectionModel.update(snap.presentationPreview)

        val isConn = (session.connectionState == ConnectionState.CONNECTED) && !session.closed
        tileMarkPanel.update(
            snapshot = snap,
            pending = session.isPending,
            connected = isConn,
            nowMillis = tileMarkPanel.model.monotonicClock()
        )
        sleeveBloomPanel.update(
            snapshot = snap,
            pending = session.isPending,
            connected = isConn,
            nowMillis = sleeveBloomPanel.model.monotonicClock()
        )
        beamSwapPanel.update(
            snapshot = snap,
            isPending = session.isPending,
            connected = isConn,
            nowMillis = beamSwapPanel.model.monotonicClock()
        )

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
        val busy = discardAnimationController.checkDiscardsDiff(currentView.discards, events)
        if (busy) clearGameplayHover()
    }

    fun updateDiscardAnimation(delta: Float) {
        discardAnimationController.update(delta)
    }

    fun fastForwardDiscardAnimations() {
        discardAnimationController.fastForward(currentView.discards)
    }

    fun computeHandSlots(): List<HandTileSlot> {
        val closed = currentView.myClosedTiles
        val drawn = currentView.myDrawnTile
        val startX = 220f
        val xSpacing = 58f
        val tileW = 54f
        val tileH = 76f
        val baseY = 40f
        val drawnGap = 24f

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
                val drawnX = startX + closed.size * xSpacing + drawnGap
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

    fun getDiscardStart(seat: Int): Pair<Float, Float> =
        TableBoardRenderer.computeDiscardStart(seat, currentView.viewerSeat)

    fun getDiscardSlotPosition(seat: Int, slotIndex: Int): Pair<Float, Float> =
        TableBoardRenderer.computeDiscardSlotPosition(seat, slotIndex, currentView.viewerSeat)

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

    fun submitSleeveBloomAsync() {
        if (isSubmittingBloom || isSubmitting || session.isPending) return
        val current = currentSnapshot ?: return
        val bloom = current.sleeveBloom ?: return
        val requestId = UUID.randomUUID().toString()

        if (sleeveBloomPanel.model.canActivate) {
            isSubmittingBloom = true
            val req = sleeveBloomPanel.model.buildActivateRequest(requestId) ?: run {
                isSubmittingBloom = false
                return
            }
            session.submitSleeveBloom(req).whenComplete { receipt, err ->
                runOnUi {
                    isSubmittingBloom = false
                    if (err != null) {
                        showToast("袖里藏花提交失败: ${err.message ?: err.javaClass.simpleName}")
                    } else if (receipt?.status == ReceiptStatus.REJECTED) {
                        val reason = SleeveBloomPanel.translateServerReason(receipt.reason ?: "UNKNOWN")
                        showToast("袖里藏花失败: $reason")
                    }
                }
            }
            return
        }

        if (!sleeveBloomPanel.model.canActivateOffTurn) return
        isSubmittingBloom = true
        val offTurnReq = sleeveBloomPanel.model.buildOffTurnActivateRequest(requestId) ?: run {
            isSubmittingBloom = false
            return
        }
        session.submitSleeveBloomOffTurn(offTurnReq).whenComplete { receipt, err ->
            runOnUi {
                isSubmittingBloom = false
                if (err != null) {
                    showToast("袖里藏花预定失败: ${err.message ?: err.javaClass.simpleName}")
                } else if (receipt?.status == ReceiptStatus.REJECTED) {
                    val reason = SleeveBloomPanel.translateServerReason(receipt.reason ?: "UNKNOWN")
                    showToast("袖里藏花预定失败: $reason")
                }
            }
        }
    }

    fun submitSleeveBloomChoiceAsync(keep: SleeveBloomKeep) {
        if (isSubmittingBloomChoice || isSubmitting || session.isPending) return
        val current = currentSnapshot ?: return
        val bloom = current.sleeveBloom ?: return
        val offer = bloom.pendingOffer ?: return

        isSubmittingBloomChoice = true
        bloomChoiceSubmitted = true
        val requestId = UUID.randomUUID().toString()

        if (offer.mode == SleeveBloomOfferMode.OFF_TURN) {
            val req = sleeveBloomPanel.model.buildOffTurnChoiceRequest(requestId, keep) ?: run {
                isSubmittingBloomChoice = false
                return
            }
            session.submitSleeveBloomOffTurnChoice(req).whenComplete { receipt, err ->
                runOnUi {
                    isSubmittingBloomChoice = false
                    if (err != null) {
                        showToast("选择提交失败: ${err.message ?: err.javaClass.simpleName}")
                    } else if (receipt?.status == ReceiptStatus.REJECTED) {
                        val reason = SleeveBloomPanel.translateServerReason(receipt.reason ?: "UNKNOWN")
                        showToast("选择失败: $reason")
                    }
                }
            }
            return
        }

        val roundId = current.roundId ?: run {
            isSubmittingBloomChoice = false
            return
        }
        val req = SleeveBloomChoiceRequest(
            requestId = requestId,
            sessionId = current.sessionId,
            roundId = roundId,
            decisionId = current.decision.decisionId,
            controlEpoch = current.decision.controlEpoch,
            offerId = offer.offerId,
            keep = keep
        )
        session.submitSleeveBloomChoice(req).whenComplete { receipt, err ->
            runOnUi {
                isSubmittingBloomChoice = false
                if (err != null) {
                    showToast("选择提交失败: ${err.message ?: err.javaClass.simpleName}")
                } else if (receipt?.status == ReceiptStatus.REJECTED) {
                    val reason = SleeveBloomPanel.translateServerReason(receipt.reason ?: "UNKNOWN")
                    showToast("选择失败: $reason")
                }
            }
        }
    }

    fun submitBeamSwapAsync() {
        if (isSubmittingBeamSwap || isSubmitting || session.isPending) return
        val current = currentSnapshot ?: return
        val beam = current.beamSwap ?: return
        if (!beamSwapPanel.model.canActivate) return

        isSubmittingBeamSwap = true
        val req = beamSwapPanel.model.buildActivateRequest(UUID.randomUUID().toString()) ?: run {
            isSubmittingBeamSwap = false
            return
        }
        val scopeSession = current.sessionId
        val scopeRound = current.roundId
        val scopeSync = current.syncEpoch
        session.submitBeamSwap(req).whenComplete { receipt, err ->
            runOnUi {
                isSubmittingBeamSwap = false
                val snap = currentSnapshot
                if (snap != null && snap.sessionId == scopeSession && snap.roundId == scopeRound && snap.syncEpoch == scopeSync) {
                    if (err != null) {
                        showToast("偷梁换柱提交失败: ${err.message ?: err.javaClass.simpleName}")
                    } else if (receipt?.status == ReceiptStatus.REJECTED) {
                        val reason = BeamSwapPanel.translateServerReason(receipt.reason ?: "UNKNOWN")
                        showToast("偷梁换柱失败: $reason")
                    }
                }
            }
        }
    }

    fun submitBeamSwapChoiceAsync(action: BeamSwapAction) {
        if (isSubmittingBeamSwapChoice || isSubmitting || session.isPending) return
        val current = currentSnapshot ?: return
        val beam = current.beamSwap ?: return
        if (beam.pendingOffer == null) return

        isSubmittingBeamSwapChoice = true
        val req = beamSwapPanel.model.buildChoiceRequest(UUID.randomUUID().toString(), action) ?: run {
            isSubmittingBeamSwapChoice = false
            return
        }
        val scopeSession = current.sessionId
        val scopeRound = current.roundId
        val scopeSync = current.syncEpoch
        session.submitBeamSwapChoice(req).whenComplete { receipt, err ->
            runOnUi {
                isSubmittingBeamSwapChoice = false
                val snap = currentSnapshot
                if (snap != null && snap.sessionId == scopeSession && snap.roundId == scopeRound && snap.syncEpoch == scopeSync) {
                    if (err != null) {
                        showToast("选择提交失败: ${err.message ?: err.javaClass.simpleName}")
                    } else if (receipt?.status == ReceiptStatus.REJECTED) {
                        val reason = BeamSwapPanel.translateServerReason(receipt.reason ?: "UNKNOWN")
                        showToast("选择失败: $reason")
                    }
                }
            }
        }
    }

    /**
     * 立直预览与当前权威 legal options 必须一致。
     *
     * 断线恢复/全量快照可能带来“预览仍停留在旧选项、decision 已撤销立直”的
     * 不一致夹具或陈旧投影；此时清空预览选项，避免旧选牌重新打开或提交。
     */
    private fun refreshRiichiSelection(snapshot: GameSnapshotView?) {
        riichiSelectionModel.update(snapshot?.presentationPreview)
        val hasAuthorizedRiichi = snapshot != null &&
            snapshot.decision.options.any { it.action is LegalActionView.DeclareRiichi } &&
            snapshot.view.legalActions.any { it is LegalActionView.DeclareRiichi }
        if (!hasAuthorizedRiichi) {
            riichiSelectionModel.update(emptyList())
        }
    }

    private fun refreshLocalSelectionModels() {
        refreshRiichiSelection(currentSnapshot)
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

    /**
     * 只重算 view / stage / 字体档位，不重置任何对局或准备草稿。
     * 相机永远保持 1280×720，由 GL viewport 和 scissor 把内容放进 stage。
     */
    private fun updateStageForWindow() {
        val graphics = Gdx.graphics ?: return
        val w = graphics.width
        val h = graphics.height
        val next = if (w <= 0 || h <= 0) {
            TableStageViewport.EMPTY
        } else {
            TableStageViewport.compute(
                windowWidth = w,
                windowHeight = h,
                framebufferWidth = graphics.backBufferWidth.coerceAtLeast(1),
                framebufferHeight = graphics.backBufferHeight.coerceAtLeast(1)
            )
        }
        stageViewport = next
        camera.setToOrtho(false, TableStageLayout.DESIGN_WIDTH, TableStageLayout.DESIGN_HEIGHT)
        camera.position.set(TableStageLayout.DESIGN_WIDTH / 2f, TableStageLayout.DESIGN_HEIGHT / 2f, 0f)
        camera.update()
        tableCamera.setToOrtho(false, TableStageLayout.DESIGN_WIDTH, TableStageLayout.DESIGN_HEIGHT)
        tableCamera.position.set(
            TableStageLayout.DESIGN_WIDTH / 2f,
            TableStageLayout.DESIGN_HEIGHT / 2f,
            0f
        )
        tableCamera.update()
        if (next.drawable) {
            // 只有 stageScale 或帧缓冲比例真正变化时才换角色集合；字形本身由
            // UiAssets 的有界 LRU 复用，连续拖动不会无限新建字体纹理。
            val fontKey = Triple(next.scale, next.framebufferRatio, 0f)
            if (stageFontsKey != fontKey) {
                stageFonts = TableStageFonts.create(assets, next.scale, next.framebufferRatio)
                stageFontsKey = fontKey
            }
        }
    }

    private fun applyStageGlViewport() {
        val v = stageViewport
        if (!v.drawable) return
        Gdx.gl.glViewport(
            v.framebufferStageX,
            v.framebufferStageY,
            v.framebufferStageWidth,
            v.framebufferStageHeight
        )
        Gdx.gl.glEnable(GL20.GL_SCISSOR_TEST)
        Gdx.gl.glScissor(
            v.framebufferStageX,
            v.framebufferStageY,
            v.framebufferStageWidth,
            v.framebufferStageHeight
        )
    }

    private fun resetGlViewportToWindow() {
        val g = Gdx.graphics ?: return
        Gdx.gl.glDisable(GL20.GL_SCISSOR_TEST)
        Gdx.gl.glViewport(0, 0, g.backBufferWidth.coerceAtLeast(1), g.backBufferHeight.coerceAtLeast(1))
    }

    override fun render(delta: Float) {
        stateTime += delta
        updateStageForWindow()
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
        sleeveBloomPanel.update(
            snapshot = currentSnapshot,
            pending = session.isPending,
            connected = isConnected,
            nowMillis = sleeveBloomPanel.model.monotonicClock()
        )
        beamSwapPanel.update(
            snapshot = currentSnapshot,
            isPending = session.isPending,
            connected = isConnected,
            nowMillis = beamSwapPanel.model.monotonicClock()
        )
        riverHintPanel.update(currentSnapshot)
        if (isModalActive()) {
            if (riverHintPanel.isOpen) {
                riverHintPanel.close()
            }
        }

        camera.update()
        if (riverHintPanel.isOpen) {
            TableLocalZoom.combine(camera.combined, tableCamera.combined)
        } else {
            tableCamera.combined.set(camera.combined)
        }

        resetGlViewportToWindow()
        Gdx.gl.glClearColor(0.015f, 0.035f, 0.025f, 1f)
        Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT)

        val batch = assets.batch
        val shape = assets.shapeRenderer
        val font = assets.font
        val chineseFont = assets.chineseFont
        if (!stageViewport.drawable) {
            resetGlViewportToWindow()
            return
        }

        val snapshot = currentSnapshot
        val tableModel = snapshot?.let { TableBoardRenderModel.fromPlayerSnapshot(it) }
        val tableOptions = TableBoardRenderOptions(
            isTileVisibleInRiver = { seat, tile, index ->
                discardAnimationController.isTileVisibleInRiver(seat, tile, index)
            },
            highlightTileId = riverHintPanel.selectedTileId,
            clockElapsedMillis = System.currentTimeMillis() - lastClockUpdateLocalMillis
        )

        val isPreparing = shufflePreparationPanel.isActive
        val hudView = TableHudView.fromPlayerView(currentView)
        val visibleBounds = RoundPreparationLayout.DEFAULT_VISIBLE
        val visuals = assets.obtainTableVisualAssets()
        if (isPreparing) {
            shufflePreparationPanel.setVisibleBounds(visibleBounds)
            shufflePreparationPanel.syncGeometry()
        }

        // Z0 环境先以 screen 空间 cover 整个窗口；随后切到 stage viewport/scissor。
        visuals?.let { TableStageRenderer.renderEnvironment(it, stageViewport, batch) }
        applyStageGlViewport()
        try {
            if (visuals != null) {
                TableStageRenderer.renderBoard(visuals, camera.combined, batch)
                TableStageRenderer.renderCompass(
                    visuals = visuals,
                    rect = if (isPreparing) TableStageLayout.PREP_COMPASS else TableStageLayout.PLAY_COMPASS,
                    view = hudView,
                    isPreparation = isPreparing,
                    projection = camera.combined,
                    batch = batch,
                    fonts = stageFonts
                )
                TableStageRenderer.renderHud(
                    visuals = visuals,
                    isPreparation = isPreparing,
                    drawerProgress = if (isPreparing) shufflePreparationPanel.drawerProgress else 0f,
                    view = hudView,
                    clocks = snapshot?.clocks ?: emptyList(),
                    clockElapsedMillis = System.currentTimeMillis() - lastClockUpdateLocalMillis,
                    projection = camera.combined,
                    batch = batch,
                    shape = shape,
                    fonts = stageFonts
                )
                if (isPreparing) {
                    TableStageRenderer.renderPreparationHint(
                        visuals = visuals,
                        projection = camera.combined,
                        batch = batch,
                        fonts = stageFonts
                    )
                } else {
                    TableStageRenderer.renderPlayingTrays(visuals, camera.combined, batch)
                }
            } else {
                TableStageRenderer.renderFallback(visibleBounds, camera.combined, shape)
            }

        // 1. 发牌后中心罗盘数据/宝牌与真实牌层。
        if (tableModel != null && !isPreparing) {
            TableBoardRenderer.renderTableCenter(
                model = tableModel,
                projectionMatrix = camera.combined,
                batch = batch,
                shape = shape,
                font = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                options = tableOptions,
                stageFonts = stageFonts
            )
        }

        // 1.5 三名对手的完整暗手牌行 (普通牌背 + 有效落汗半透明牌面)
        if (tableModel != null && gameStarted && !isPreparing) {
            TableBoardRenderer.renderHandAreas(
                model = tableModel,
                projectionMatrix = tableCamera.combined,
                batch = batch,
                shape = shape,
                font = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                options = tableOptions
            )
        }

        // 2. 牌河
        if (tableModel != null && !isPreparing) {
            TableBoardRenderer.renderDiscards(
                model = tableModel,
                projectionMatrix = tableCamera.combined,
                batch = batch,
                shape = shape,
                font = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                options = tableOptions
            )
        }

        // 3. 副露
        if (tableModel != null && !isPreparing) {
            TableBoardRenderer.renderMelds(
                model = tableModel,
                projectionMatrix = tableCamera.combined,
                batch = batch,
                shape = shape,
                font = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                options = tableOptions
            )
        }

        // 4. 手牌（准备阶段保持空白，发牌后才出现真实手牌）
        if (!isPreparing) {
            renderPlayerHands(batch, font)
        }

        // 5. 正在飞行的打牌动画
        if (!isPreparing) {
            renderFlyingDiscard(batch, shape, font)
        }

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
                camera = tableCamera,
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

        // 8. 操作按钮（准备阶段只保留右侧准备栏，牌局操作按钮暂不开放）
        if (!isPreparing) {
            // 五个技能入口和理牌/托管都使用 button-skins 金边按钮，不再额外铺一块
            // 会与按钮金线重叠的操作底板。
            renderActionButtons(shape, batch, font, visuals, stageFonts)
        }

        // 8.5 落汗 / 袖里藏花 / 偷梁换柱与牌河提示只在真实牌局出现。
        if (!isPreparing) {
            tileMarkPanel.render(
                camera = tableCamera,
                shape = shape,
                batch = batch,
                font = assets.chineseFont ?: font,
                snapshot = currentSnapshot,
                isDiscardPlaybackBusy = isDiscardPlaybackBusy,
                isPaused = isPaused,
                isSubmitting = isSubmitting || isSubmittingMark,
                riichiSelecting = riichiSelectionModel.isSelecting,
                meldSelecting = meldSelectionModel.isSelecting,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                sleeveBloomChoosing = sleeveBloomPanel.model.isChoosing,
                beamSwapChoosing = beamSwapPanel.model.isChoosing,
                entryPatch = visuals?.buttonPrimary,
                labelBand = visuals?.labelBand,
                stageFonts = stageFonts
            )

            sleeveBloomPanel.render(
                camera = tableCamera,
                shape = shape,
                batch = batch,
                font = assets.chineseFont ?: font,
                snapshot = currentSnapshot,
                isDiscardPlaybackBusy = isDiscardPlaybackBusy,
                isPaused = isPaused,
                isSubmitting = isSubmitting || isSubmittingBloom || isSubmittingBloomChoice,
                riichiSelecting = riichiSelectionModel.isSelecting,
                meldSelecting = meldSelectionModel.isSelecting,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                tileMarkSelecting = tileMarkPanel.model.isSelecting,
                beamSwapChoosing = beamSwapPanel.model.isChoosing,
                entryPatch = visuals?.buttonPrimary,
                labelBand = visuals?.labelBand,
                stageFonts = stageFonts
            )

            beamSwapPanel.render(
                camera = tableCamera,
                shape = shape,
                batch = batch,
                font = assets.chineseFont ?: font,
                snapshot = currentSnapshot,
                isDiscardPlaybackBusy = isDiscardPlaybackBusy,
                isPaused = isPaused,
                isSubmitting = isSubmitting || isSubmittingBeamSwap || isSubmittingBeamSwapChoice,
                riichiSelecting = riichiSelectionModel.isSelecting,
                meldSelecting = meldSelectionModel.isSelecting,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                tileMarkSelecting = tileMarkPanel.model.isSelecting,
                sleeveBloomChoosing = sleeveBloomPanel.model.isChoosing,
                entryPatch = visuals?.buttonPrimary,
                labelBand = visuals?.labelBand,
                stageFonts = stageFonts
            )

            if (!isModalActive()) {
                riverHintPanel.visualAssets = visuals
                riverHintPanel.stageFonts = stageFonts
                riverHintPanel.render(
                    camera = camera,
                    shape = shape,
                    batch = batch,
                    font = assets.chineseFont ?: font,
                    snapshot = currentSnapshot,
                    tileSkin = tileSkin,
                    tileDrawBuffer = tileDrawBuffer,
                    isTileVisibleInRiver = { seat, tile, index ->
                        discardAnimationController.isTileVisibleInRiver(seat, tile, index)
                    }
                )
                riverHintPanel.renderToggleButton(shape, batch, chineseFont ?: font)
            }
        }

        // 9. 洗牌千术准备栏（权威准备阶段结束后，回执仍短暂保留并淡出）
        if (shufflePreparationPanel.isVisible) {
            shufflePreparationPanel.render(camera, shape, batch, font, tileSkin, stageFonts)
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
        } finally {
            resetGlViewportToWindow()
        }
    }


    private fun renderPlayerHands(batch: SpriteBatch, font: BitmapFont) {
        if (!gameStarted) return
        val slots = refreshHandTileRects()

        val ownMarkedIds = currentSnapshot?.tileMark?.activeGroups?.flatMap { it.tiles }
            ?.filter { it.area == TileMarkArea.OWN_HAND }
            ?.map { it.tileId }?.toSet() ?: emptySet()

        for (slot in slots) {
            val isHover = (!isDiscardPlaybackBusy && (slot.tile == hoveredTile || (hoveredTileIndex != -1 && slots.getOrNull(hoveredTileIndex)?.tile == slot.tile)))
            val alpha = if (slot.tile.tileId in ownMarkedIds) 0.55f else 1.0f
            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = slot.tile,
                x = slot.rect.x,
                y = slot.rect.y,
                width = slot.rect.width,
                height = slot.rect.height,
                isHover = isHover,
                isDrawn = slot.isDrawn,
                alpha = alpha
            )
        }
        assets.shapeRenderer.projectionMatrix = tableCamera.combined
        batch.projectionMatrix = tableCamera.combined
        tileDrawBuffer.flush(batch, assets.shapeRenderer, font)

        val selected = riverHintPanel.selectedTileId
        if (selected != null) {
            val shape = assets.shapeRenderer
            shape.projectionMatrix = tableCamera.combined
            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.20f, 1.0f, 0.85f, 1.0f)
            for (slot in slots) {
                if (slot.tile.tileId == selected) {
                    shape.rect(slot.rect.x - 2f, slot.rect.y - 2f, slot.rect.width + 4f, slot.rect.height + 4f)
                }
            }
            shape.end()
        }
    }

    private fun renderFlyingDiscard(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        val anim = discardAnimationController.currentDiscardAnim ?: return
        val pose = discardAnimationController.currentPose() ?: return
        if (pose.phase == DiscardPhase.DONE) return

        val tileW = 44f * pose.scaleX
        val tileH = 62f * pose.scaleY

        val isMarked = currentSnapshot?.tileMark?.activeGroups?.any { g -> g.tiles.any { it.tileId == anim.tile.tileId } } == true
        val alpha = if (isMarked) 0.55f else 1.0f

        tileSkin.appendFlyingTileDraw(
            buf = tileDrawBuffer,
            tile = anim.tile,
            x = pose.x - tileW / 2f,
            y = pose.y - tileH / 2f,
            width = tileW,
            height = tileH,
            scaleX = 1f,
            scaleY = 1f,
            rotationDeg = 0f,
            alpha = alpha
        )
        shape.projectionMatrix = tableCamera.combined
        batch.projectionMatrix = tableCamera.combined
        tileDrawBuffer.flush(batch, shape, font)
    }

    private fun renderRiichiSelectionOverlay(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val slots = refreshHandTileRects()
        val legalTiles = riichiSelectionModel.options.map { it.discardTile }

        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
        shape.projectionMatrix = tableCamera.combined
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
        shape.projectionMatrix = tableCamera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0.04f, 0.12f, 0.08f, 0.94f)
        shape.rect(waitsPanelRect.x, waitsPanelRect.y, waitsPanelRect.width, waitsPanelRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.85f, 0.72f, 0.35f, 0.95f)
        shape.rect(waitsPanelRect.x, waitsPanelRect.y, waitsPanelRect.width, waitsPanelRect.height)
        shape.end()

        batch.projectionMatrix = tableCamera.combined
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
            shape.projectionMatrix = tableCamera.combined
            batch.projectionMatrix = tableCamera.combined
            tileDrawBuffer.flush(batch, shape, font)
        }
    }

    private fun renderMeldSelectionPanel(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val visibleOptions = meldSelectionModel.visibleOptions

        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
        shape.projectionMatrix = tableCamera.combined
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
        shape.projectionMatrix = tableCamera.combined
        batch.projectionMatrix = tableCamera.combined
        tileDrawBuffer.flush(batch, shape, font)

        renderMeldActionButtons(shape, batch, font)
    }

    private fun renderMeldActionButtons(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val hasSelection = meldSelectionModel.selectedOption != null
        shape.projectionMatrix = tableCamera.combined
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

        batch.projectionMatrix = tableCamera.combined
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

    private fun renderActionButtons(
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont,
        visuals: com.majiang.presentation.common.render.TableVisualAssets?,
        stageFonts: TableStageFonts
    ) {
        if (!gameStarted && !shufflePreparationPanel.isActive) {
            shape.projectionMatrix = tableCamera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.85f, 0.25f, 0.18f, 1f)
            shape.rect(btnStartGameRect.x, btnStartGameRect.y, btnStartGameRect.width, btnStartGameRect.height)
            shape.end()

            batch.projectionMatrix = tableCamera.combined
            batch.begin()
            val textFont = assets.chineseFont ?: font
            textFont.data.setScale(1.5f)
            textFont.color = Color.WHITE
            textFont.draw(batch, "开始对局", btnStartGameRect.x + 75f, btnStartGameRect.y + 42f)
            batch.end()
            return
        }

        val textFont = assets.chineseFont ?: font

        val controls = currentSnapshot?.controls ?: emptyList()
        val myControl = controls.firstOrNull { it.seat == (currentSnapshot?.viewerSeat ?: 0) }
        val isTakeover = (myControl?.role == "TAKEOVER")
        if (visuals != null) {
            drawArtAction(
                batch,
                visuals,
                stageFonts,
                btnSortRect,
                if (isAutoSort) "理牌 · 开" else "理牌 · 关",
                enabled = true,
                highlighted = isAutoSort
            )
            drawArtAction(
                batch,
                visuals,
                stageFonts,
                btnTakeoverRect,
                if (isTakeover) "收回控制" else "托管",
                enabled = true,
                highlighted = isTakeover
            )
        } else {
            drawActionSurface(
                shape,
                batch,
                textFont,
                btnSortRect,
                if (isAutoSort) Color(0.20f, 0.60f, 0.40f, 1f) else Color(0.45f, 0.45f, 0.45f, 1f),
                if (isAutoSort) "理牌: 开" else "理牌: 关",
                true,
                30f,
                1.10f
            )
            drawActionSurface(
                shape,
                batch,
                textFont,
                btnTakeoverRect,
                if (isTakeover) Color(0.85f, 0.40f, 0.20f, 1f) else Color(0.25f, 0.45f, 0.65f, 1f),
                if (isTakeover) "收回控制" else "托管",
                true,
                if (isTakeover) 30f else 50f,
                1.10f
            )
        }

        if (isDiscardPlaybackBusy || isPaused || session.isPending || isSubmitting) return

        if (riichiSelectionModel.isSelecting) {
            val hasSelection = riichiSelectionModel.selectedTile != null
            drawActionSurface(
                shape,
                batch,
                textFont,
                btnRiichiRect,
                Color(0.85f, 0.45f, 0.15f, 1f),
                "确认立直",
                hasSelection,
                28f
            )
            drawActionSurface(
                shape,
                batch,
                textFont,
                btnCancelRiichiRect,
                Color(0.55f, 0.22f, 0.20f, 1f),
                "取消",
                true,
                60f
            )
            return
        }

        if (meldSelectionModel.isSelecting) return

        // 普通回合按钮
        if (canHumanTsumo()) {
            drawButton(shape, batch, textFont, getActionButtonRect(btnTsumoRect), Color(0.90f, 0.65f, 0.15f, 1f), "自摸 (TSUMO)")
        }
        if (canHumanRiichi()) {
            drawButton(shape, batch, textFont, getActionButtonRect(btnRiichiRect), Color(0.85f, 0.45f, 0.15f, 1f), "立直 (RIICHI)")
        }
        if (canHumanAbortiveDraw()) {
            drawButton(shape, batch, textFont, getActionButtonRect(btnAbortiveDrawRect), Color(0.60f, 0.25f, 0.45f, 1f), "九种九牌")
        }
        if (canHumanRon()) {
            drawButton(shape, batch, textFont, getActionButtonRect(btnRonRect), Color(0.88f, 0.18f, 0.15f, 1f), "荣和 (RON)")
        }
        if (canHumanPass()) {
            drawButton(shape, batch, textFont, getActionButtonRect(btnPassRect), Color(0.35f, 0.40f, 0.45f, 1f), "过 (PASS)")
        }
        if (canHumanPon()) {
            drawButton(shape, batch, textFont, getActionButtonRect(btnPonRect), Color(0.20f, 0.55f, 0.85f, 1f), "碰 (PON)")
        }
        if (canHumanChi()) {
            drawButton(shape, batch, textFont, getActionButtonRect(btnChiRect), Color(0.25f, 0.65f, 0.35f, 1f), "吃 (CHI)")
        }
        if (canHumanKan()) {
            drawButton(shape, batch, textFont, getActionButtonRect(btnKanRect), Color(0.70f, 0.35f, 0.80f, 1f), "杠 (KAN)")
        }
    }

    /**
     * 右侧操作列的纹理按钮。复用 button-skins 图集，保留红底/绿底金边，
     * 不再用纯色矩形冒充技能/系统按钮；文字仍走当前 stage 原生字号。
     */
    private fun drawArtAction(
        batch: SpriteBatch,
        visuals: com.majiang.presentation.common.render.TableVisualAssets,
        fonts: TableStageFonts,
        rect: Rectangle,
        text: String,
        enabled: Boolean,
        highlighted: Boolean
    ) {
        batch.projectionMatrix = camera.combined
        batch.begin()
        val patch = if (highlighted) visuals.buttonPrimary else visuals.buttonSecondary
        batch.color = if (enabled) Color.WHITE else Color(0.58f, 0.58f, 0.58f, 0.9f)
        patch.draw(batch, rect.x, rect.y, rect.width, rect.height)
        batch.color = Color.WHITE
        batch.end()
        batch.begin()
        fonts.body.drawCentered(
            batch,
            text,
            rect.x + rect.width / 2f,
            rect.y + 31f,
            if (enabled) Color(0.97f, 0.90f, 0.67f, 1f) else Color(0.72f, 0.72f, 0.68f, 1f),
            outline = true
        )
        batch.end()
    }

    private fun drawButton(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont, rect: Rectangle, color: Color, text: String) {
        drawActionSurface(shape, batch, font, rect, color, text, true, 12f)
    }

    private fun drawActionSurface(
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont,
        rect: Rectangle,
        accent: Color,
        text: String,
        enabled: Boolean,
        textOffsetX: Float,
        textScale: Float = 1f
    ) {
        val visuals = assets.obtainTableVisualAssets()
        if (visuals == null) {
            shape.projectionMatrix = tableCamera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = if (enabled) accent else Color(0.25f, 0.25f, 0.25f, 0.7f)
            shape.rect(rect.x, rect.y, rect.width, rect.height)
            shape.end()
            batch.projectionMatrix = tableCamera.combined
            batch.begin()
            font.data.setScale(textScale, textScale)
            font.color = if (enabled) Color.WHITE else Color(0.72f, 0.72f, 0.68f, 1f)
            font.draw(batch, text, rect.x + textOffsetX, rect.y + 24f - (textScale - 1f) * 9f)
            batch.end()
            return
        }

        batch.projectionMatrix = tableCamera.combined
        batch.begin()
        batch.color = if (enabled) Color.WHITE else Color(0.52f, 0.52f, 0.52f, 0.96f)
        visuals.buttonPrimary.draw(batch, rect.x, rect.y, rect.width, rect.height)
        // 金漆花纹上压一条深色字带，保留外圈红/金，保证 1280 下标签稳定可读。
        batch.color = Color(0.01f, 0.015f, 0.01f, 0.62f)
        batch.draw(
            visuals.labelBand,
            rect.x + 4f,
            rect.y + 4f,
            rect.width - 8f,
            rect.height - 8f
        )
        batch.color = Color.WHITE
        val textTop = rect.y + 24f - (textScale - 1f) * 9f
        font.data.setScale(textScale, textScale)
        font.color = Color(0.02f, 0.02f, 0.01f, 0.98f)
        font.draw(batch, text, rect.x + textOffsetX + 2f, textTop - 1f)
        font.draw(batch, text, rect.x + textOffsetX + 1f, textTop)
        font.color = if (enabled) Color(0.99f, 0.96f, 0.86f, 1f) else Color(0.86f, 0.83f, 0.74f, 1f)
        font.draw(batch, text, rect.x + textOffsetX, textTop)
        batch.end()

        shape.projectionMatrix = tableCamera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(accent.r, accent.g, accent.b, if (enabled) 0.95f else 0.45f)
        shape.rect(rect.x + 2f, rect.y + 10f, 3f, rect.height - 20f)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.91f, 0.73f, 0.33f, if (enabled) 0.92f else 0.48f)
        shape.rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f)
        shape.end()
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
        // 准备阶段独占键盘：Esc 回栏、Enter/Space 确认、Tab/方向键移动焦点；
        // 其余按键不得穿透到牌局动作。
        if (shufflePreparationPanel.isActive) {
            when (keycode) {
                Input.Keys.ESCAPE, Input.Keys.BACK -> {
                    shufflePreparationPanel.handleEscape()
                    return true
                }
                Input.Keys.ENTER, Input.Keys.NUMPAD_ENTER, Input.Keys.SPACE -> {
                    shufflePreparationPanel.handleConfirmKey()
                    return true
                }
                Input.Keys.TAB, Input.Keys.DPAD_DOWN, Input.Keys.DPAD_RIGHT -> {
                    shufflePreparationPanel.moveFocus(1)
                    return true
                }
                Input.Keys.DPAD_UP, Input.Keys.DPAD_LEFT, Input.Keys.SHIFT_LEFT, Input.Keys.SHIFT_RIGHT -> {
                    shufflePreparationPanel.moveFocus(-1)
                    return true
                }
            }
            return true
        }
        if (keycode == Input.Keys.ESCAPE || keycode == Input.Keys.BACK) {
            return modalCoordinator.handleEscapeOrBack(
                ModalEscapeRoutes(
                    onDismissBeamSwap = { submitBeamSwapChoiceAsync(BeamSwapAction.KEEP_ORIGINAL) },
                    onDismissSleeveBloom = { submitSleeveBloomChoiceAsync(SleeveBloomKeep.FIRST) },
                    onDismissRiverHint = { riverHintPanel.close() },
                    onDismissTileMark = { tileMarkPanel.model.cancel() },
                    onDismissMeld = { meldSelectionModel.cancel() },
                    onDismissRiichi = { riichiSelectionModel.cancel() },
                    onDismissSettlementToFinalResult = {
                        showSettlement = false
                        showFinalResult = true
                    },
                    onExitToMenu = { exitToMenu() }
                )
            )
        }
        return false
    }

    /**
     * 处理袖里藏花面板自身的输入。返回 true 表示点击已由面板消费。
     * 他家回合 offer 不作为全局模态，因此面板外点击继续交给牌桌/响应按钮。
     */
    private fun handleSleeveBloomPanelClick(uiTx: Float, uiTy: Float): Boolean {
        if (sleeveBloomPanel.btnConfirmRect.contains(uiTx, uiTy)) {
            val sel = sleeveBloomPanel.model.selected
            if (sel != null) submitSleeveBloomChoiceAsync(sel)
            return true
        }
        if (sleeveBloomPanel.btnCancelRect.contains(uiTx, uiTy)) {
            submitSleeveBloomChoiceAsync(SleeveBloomKeep.FIRST)
            return true
        }
        if (sleeveBloomPanel.firstCardRect.contains(uiTx, uiTy)) {
            sleeveBloomPanel.model.select(SleeveBloomKeep.FIRST)
            return true
        }
        if (sleeveBloomPanel.secondCardRect.contains(uiTx, uiTy)) {
            sleeveBloomPanel.model.select(SleeveBloomKeep.SECOND)
            return true
        }
        return sleeveBloomPanel.panelRect.contains(uiTx, uiTy)
    }

    override fun touchDown(screenX: Int, screenY: Int, pointer: Int, button: Int): Boolean {
        val ui = screenToUi(screenX, screenY) ?: run {
            shufflePreparationPanel.pressCancel()
            return false
        }
        val uiTx = ui.x
        val uiTy = ui.y
        lastMouseX = uiTx
        lastMouseY = uiTy

        val beforeTouchSnapshot = modalCoordinator.captureSnapshot()

        // 准备阶段：背景可见但不可穿透做牌局动作，只保留“返回主菜单”这类导航出口。
        if (shufflePreparationPanel.isActive && backButtonRect.contains(uiTx, uiTy)) {
            exitToMenu()
            return true
        }

        val isConnected = (session.connectionState == ConnectionState.CONNECTED) && !session.closed
        tileMarkPanel.update(
            snapshot = currentSnapshot,
            pending = session.isPending,
            connected = isConnected,
            nowMillis = tileMarkPanel.model.monotonicClock()
        )
        sleeveBloomPanel.update(
            snapshot = currentSnapshot,
            pending = session.isPending,
            connected = isConnected,
            nowMillis = sleeveBloomPanel.model.monotonicClock()
        )
        beamSwapPanel.update(
            snapshot = currentSnapshot,
            isPending = session.isPending,
            connected = isConnected,
            nowMillis = beamSwapPanel.model.monotonicClock()
        )

        // 他家回合 offer 只锁本人技能选择：面板内点击由面板消费，面板外继续交给响应/牌桌输入。
        if (sleeveBloomPanel.model.isChoosing && sleeveBloomPanel.model.isOffTurnChoice) {
            if (handleSleeveBloomPanelClick(uiTx, uiTy)) return true
        }

        // 0. 弹窗模态优先级最高：在模态展示期间，拦截所有输入并禁止与底层提示/牌桌交互
        when (modalCoordinator.activeModal()) {
            ModalKind.FINAL_RESULT -> {
                if (btnReturnToMenuRect.contains(uiTx, uiTy)) {
                    exitToMenu()
                    return true
                }
                if (currentSettlement != null && btnRoundDetailsRect.contains(uiTx, uiTy)) {
                    showFinalResult = false
                    showSettlement = true
                    return true
                }
                return true
            }
            ModalKind.SETTLEMENT -> {
                val bd = currentSettlement?.breakdown
                if (bd != null) {
                    if (tabYakuRect.contains(uiTx, uiTy)) {
                        settlementSection = SettlementSection.YAKU
                        settlementPage = 0
                        return true
                    }
                    if (tabDoraRect.contains(uiTx, uiTy)) {
                        settlementSection = SettlementSection.DORA
                        settlementPage = 0
                        return true
                    }
                    if (tabCalcRect.contains(uiTx, uiTy)) {
                        settlementSection = SettlementSection.CALC
                        settlementPage = 0
                        return true
                    }
                    if (btnPrevPageRect.contains(uiTx, uiTy)) {
                        if (settlementPage > 0) settlementPage--
                        return true
                    }
                    if (btnNextPageRect.contains(uiTx, uiTy)) {
                        if (settlementPage < settlementPageCount - 1) settlementPage++
                        return true
                    }
                } else {
                    if (btnLegacyPrevPageRect.contains(uiTx, uiTy)) {
                        if (settlementPage > 0) settlementPage--
                        return true
                    }
                    if (btnLegacyNextPageRect.contains(uiTx, uiTy)) {
                        if (settlementPage < settlementPageCount - 1) settlementPage++
                        return true
                    }
                }

                if (canHumanAgariYame()) {
                    if (btnAgariYameRect.contains(uiTx, uiTy)) {
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
                    if (btnNextRoundSplitRect.contains(uiTx, uiTy)) {
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
                    if (btnNextRoundRect.contains(uiTx, uiTy)) {
                        showSettlement = false
                        showFinalResult = true
                        riichiSelectionModel.cancel()
                        meldSelectionModel.cancel()
                        return true
                    }
                } else {
                    if (btnNextRoundRect.contains(uiTx, uiTy)) {
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
            ModalKind.SHUFFLE_PREPARATION -> {
                // 抽屉入场未完成时只绘制/裁切，不接收点击；避免压着移动中的按钮提交。
                if (shufflePreparationPanel.model.page == ShufflePreparationModel.PreparationPage.DRAWER &&
                    shufflePreparationPanel.motion.isAnimating
                ) {
                    shufflePreparationPanel.pressCancel()
                    return true
                }
                shufflePreparationPanel.pressDown(uiTx, uiTy)
                return true
            }
            ModalKind.TILE_MARK -> {
                if (tileMarkPanel.btnCancelRect.contains(uiTx, uiTy)) {
                    tileMarkPanel.model.cancel()
                    return true
                }
                if (tileMarkPanel.btnConfirmRect.contains(uiTx, uiTy)) {
                    submitTileMarkAsync()
                    return true
                }
                val slots = refreshHandTileRects()
                for (slot in slots) {
                    val hitRect = Rectangle(slot.rect.x, 40f, slot.rect.width, 94f)
                    if (hitRect.contains(uiTx, uiTy) || slot.rect.contains(uiTx, uiTy)) {
                        tileMarkPanel.model.toggle(slot.tile.tileId)
                        return true
                    }
                }
                return true
            }
            ModalKind.SLEEVE_BLOOM -> {
                handleSleeveBloomPanelClick(uiTx, uiTy)
                return true
            }
            ModalKind.BEAM_SWAP -> {
                if (beamSwapPanel.btnConfirmRect.contains(uiTx, uiTy)) {
                    if (beamSwapPanel.model.canConfirm) {
                        submitBeamSwapChoiceAsync(BeamSwapAction.SWAP)
                    }
                    return true
                }
                if (beamSwapPanel.btnKeepRect.contains(uiTx, uiTy)) {
                    submitBeamSwapChoiceAsync(BeamSwapAction.KEEP_ORIGINAL)
                    return true
                }
                for ((slot, rect) in beamSwapPanel.targetCardRects) {
                    if (rect.contains(uiTx, uiTy)) {
                        beamSwapPanel.model.selectTarget(slot)
                        return true
                    }
                }
                for ((tileId, rect) in beamSwapPanel.handCardRects) {
                    if (rect.contains(uiTx, uiTy)) {
                        beamSwapPanel.model.selectHand(tileId)
                        return true
                    }
                }
                return true
            }
            else -> Unit
        }

        // 1. 牌河提示面板切换按钮与展开面板内交互 (非模态下可用)
        if (!isModalActive() && riverHintPanel.btnToggleRect.contains(uiTx, uiTy)) {
            riverHintPanel.toggle()
            return true
        }

        if (!isModalActive() && riverHintPanel.isOpen && riverHintPanel.panelRect.contains(uiTx, uiTy)) {
            riverHintPanel.touchDown(uiTx, uiTy)
            return true
        }

        if (backButtonRect.contains(uiTx, uiTy)) {
            exitToMenu()
            return true
        }

        if (modalCoordinator.wasModalInvalidated(beforeTouchSnapshot)) {
            return true
        }

        // 2. 将 UI 坐标逆变换为主牌桌坐标；牌河提示打开时叠加同一个局部缩放。
        val tablePoint = if (riverHintPanel.isOpen) {
            TableLocalZoom.toTable(uiTx, uiTy, this.tablePoint)
        } else {
            this.tablePoint.set(uiTx, uiTy)
        }
        val tableTx = tablePoint.x
        val tableTy = tablePoint.y

        // 1.8 袖里藏花入口 (防止穿透)
        if (!isModalActive() && sleeveBloomPanel.btnOpenRect.contains(tableTx, tableTy)) {
            val model = sleeveBloomPanel.model
            val canOpenBloom = (model.canActivate || model.canActivateOffTurn) &&
                (!isDiscardPlaybackBusy || model.canActivateOffTurn) &&
                !isPaused &&
                !session.isPending &&
                !isSubmitting &&
                !isSubmittingMark &&
                !isSubmittingBloom &&
                !isSubmittingBloomChoice &&
                !isSubmittingBeamSwap &&
                !isSubmittingBeamSwapChoice &&
                modalCoordinator.canOpen(ModalKind.SLEEVE_BLOOM)
            if (canOpenBloom) {
                submitSleeveBloomAsync()
            } else {
                val bloom = currentSnapshot?.sleeveBloom
                val viewerSeat = currentSnapshot?.viewerSeat ?: 0
                val pts = currentSnapshot?.cheatPoints?.getOrNull(viewerSeat) ?: 0
                val disReason = if (currentSnapshot?.view?.currentTurnPlayer == viewerSeat) {
                    bloom?.disabledReason
                } else {
                    bloom?.offTurnDisabledReason
                }
                val reasonText = when {
                    disReason != null -> SleeveBloomPanel.translateServerReason(disReason)
                    pts < (bloom?.cost ?: 10) -> "千术点不足"
                    isSubmittingBloom || isSubmittingBloomChoice -> "正在提交中..."
                    else -> "当前不可施展袖里藏花"
                }
                showToast(reasonText)
            }
            return true
        }

        // 1.85 偷梁换柱入口 (防止穿透)
        if (!isModalActive() && beamSwapPanel.btnOpenRect.contains(tableTx, tableTy)) {
            val canOpenBeam = beamSwapPanel.model.canActivate &&
                !isDiscardPlaybackBusy &&
                !isPaused &&
                !session.isPending &&
                !isSubmitting &&
                !isSubmittingMark &&
                !isSubmittingBloom &&
                !isSubmittingBloomChoice &&
                !isSubmittingBeamSwap &&
                !isSubmittingBeamSwapChoice &&
                modalCoordinator.canOpen(ModalKind.BEAM_SWAP)
            if (canOpenBeam) {
                submitBeamSwapAsync()
            } else {
                val beam = currentSnapshot?.beamSwap
                val viewerSeat = currentSnapshot?.viewerSeat ?: 0
                val pts = currentSnapshot?.cheatPoints?.getOrNull(viewerSeat) ?: 0
                val disReason = beam?.disabledReason
                val reasonText = when {
                    disReason != null -> BeamSwapPanel.translateServerReason(disReason)
                    pts < (beam?.cost ?: 10) -> "千术点不足"
                    isSubmittingBeamSwap || isSubmittingBeamSwapChoice -> "正在提交中..."
                    else -> "当前不可施展偷梁换柱"
                }
                showToast(reasonText)
            }
            return true
        }

        // 托管切换
        if (gameStarted && !isLoading && !isMatchFinished && !showSettlement && btnTakeoverRect.contains(tableTx, tableTy)) {
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

        if (isDiscardPlaybackBusy || isLoading || session.isPending || isSubmitting || isSubmittingMark || isSubmittingBloom || isSubmittingBloomChoice || isSubmittingBeamSwap || isSubmittingBeamSwapChoice || isPaused) {
            return true
        }

        // 理牌开关
        if (btnSortRect.contains(tableTx, tableTy)) {
            isAutoSort = !isAutoSort
            return true
        }

        // 开赛
        if (!gameStarted) {
            if (btnStartGameRect.contains(tableTx, tableTy)) {
                startGame()
                return true
            }
            return false
        }

        // 立直选牌
        if (modalCoordinator.hasModal(ModalKind.RIICHI)) {
            if (btnCancelRiichiRect.contains(tableTx, tableTy)) {
                riichiSelectionModel.cancel()
                return true
            }
            if (btnRiichiRect.contains(tableTx, tableTy)) {
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
                if (hitRect.contains(tableTx, tableTy) || slot.rect.contains(tableTx, tableTy)) {
                    if (riichiSelectionModel.select(slot.tile)) {
                        hoveredTile = slot.tile
                    }
                    return true
                }
            }
            return true
        }

        // 副露选牌
        if (modalCoordinator.hasModal(ModalKind.MELD)) {
            if (btnMeldCancelRect.contains(tableTx, tableTy)) {
                meldSelectionModel.cancel()
                return true
            }
            if (btnMeldConfirmRect.contains(tableTx, tableTy)) {
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
            if ((canHumanRon() || canHumanTsumo()) && btnMeldWinRect.contains(tableTx, tableTy)) {
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
            if (canHumanPass() && btnMeldPassRect.contains(tableTx, tableTy)) {
                meldSelectionModel.cancel()
                val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.Pass }
                if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
                return true
            }
            if (btnMeldPrevRect.contains(tableTx, tableTy)) {
                if (meldSelectionModel.pageIndex > 0) meldSelectionModel.setPage(meldSelectionModel.pageIndex - 1)
                return true
            }
            if (btnMeldNextRect.contains(tableTx, tableTy)) {
                if (meldSelectionModel.pageIndex < meldSelectionModel.pageCount - 1) meldSelectionModel.setPage(meldSelectionModel.pageIndex + 1)
                return true
            }
            for (i in meldSelectionModel.visibleOptions.indices) {
                val cardRect = meldCardRects[i]
                if (cardRect.contains(tableTx, tableTy)) {
                    meldSelectionModel.select(meldSelectionModel.visibleOptions[i])
                    return true
                }
            }
            return true
        }

        // 普通回合操作
        if (canHumanRon() && btnRonRect.contains(tableTx, tableTy)) {
            val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.DeclareRon }
            if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
            return true
        }
        if (canHumanPass() && btnPassRect.contains(tableTx, tableTy)) {
            val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.Pass }
            if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
            return true
        }
        if (canHumanPon() && btnPonRect.contains(tableTx, tableTy)) {
            if (riichiSelectionModel.isSelecting) riichiSelectionModel.cancel()
            meldSelectionModel.begin(MeldChoiceKindView.PON)
            return true
        }
        if (canHumanChi() && btnChiRect.contains(tableTx, tableTy)) {
            if (riichiSelectionModel.isSelecting) riichiSelectionModel.cancel()
            meldSelectionModel.begin(MeldChoiceKindView.CHI)
            return true
        }
        if (canHumanKan() && btnKanRect.contains(tableTx, tableTy)) {
            if (riichiSelectionModel.isSelecting) riichiSelectionModel.cancel()
            meldSelectionModel.begin(MeldChoiceKindView.KAN)
            return true
        }
        if (canHumanTsumo() && btnTsumoRect.contains(tableTx, tableTy)) {
            val opt = currentSnapshot?.decision?.options?.firstOrNull { it.action is LegalActionView.DeclareTsumo }
            if (opt != null) submitChoiceAsync(ActionChoice(UUID.randomUUID().toString(), currentSnapshot?.roundId, currentSnapshot?.decision?.decisionId ?: "", currentSnapshot?.decision?.controlEpoch ?: 0L, opt.optionId))
            return true
        }
        if (canHumanRiichi() && btnRiichiRect.contains(tableTx, tableTy)) {
            if (meldSelectionModel.isSelecting) meldSelectionModel.cancel()
            return riichiSelectionModel.begin()
        }
        if (canHumanAbortiveDraw() && btnAbortiveDrawRect.contains(tableTx, tableTy)) {
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
            !isSubmittingBloom &&
            !isSubmittingBloomChoice &&
            !isSubmittingBeamSwap &&
            !isSubmittingBeamSwapChoice &&
            modalCoordinator.canOpen(ModalKind.TILE_MARK)
        if (tileMarkPanel.btnOpenRect.contains(tableTx, tableTy)) {
            if (canOpenTileMark) {
                tileMarkPanel.model.begin()
            }
            return true
        }

        // 落汗追踪翻页
        val activeGroupCount = currentSnapshot?.tileMark?.activeGroups?.size ?: 0
        if (tileMarkPanel.touchDown(tableTx, tableTy, activeGroupCount)) {
            return true
        }

        // 人类出牌点击
        if (isHumanTurn()) {
            val slots = refreshHandTileRects()
            for (slot in slots) {
                val hitRect = Rectangle(slot.rect.x, 40f, slot.rect.width, 94f)
                if (hitRect.contains(tableTx, tableTy) || slot.rect.contains(tableTx, tableTy)) {
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
        val ui = screenToUi(screenX, screenY)
        if (ui == null) {
            clearGameplayHover()
            hoveredTile = null
            hoveredTileIndex = -1
            return false
        }
        val uiTx = ui.x
        val uiTy = ui.y
        lastMouseX = uiTx
        lastMouseY = uiTy

        val tablePoint = if (riverHintPanel.isOpen) {
            TableLocalZoom.toTable(uiTx, uiTy, this.tablePoint)
        } else {
            this.tablePoint.set(uiTx, uiTy)
        }
        val tableTx = tablePoint.x
        val tableTy = tablePoint.y

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
            if (hitRect.contains(tableTx, tableTy) || slot.rect.contains(tableTx, tableTy)) {
                foundSlot = slot
                foundIdx = i
                break
            }
        }
        hoveredTile = foundSlot?.tile
        hoveredTileIndex = foundIdx

        if (shufflePreparationPanel.isActive) {
            shufflePreparationPanel.mouseMoved(uiTx, uiTy)
        }
        return false
    }

    override fun touchDragged(screenX: Int, screenY: Int, pointer: Int): Boolean {
        val ui = screenToUi(screenX, screenY) ?: run {
            shufflePreparationPanel.pressCancel()
            return true
        }
        val uiTx = ui.x
        val uiTy = ui.y
        lastMouseX = uiTx
        lastMouseY = uiTy

        if (shufflePreparationPanel.isActive) {
            // 拖出按钮即取消按下（准备阶段的局部输入不穿透牌桌）。
            shufflePreparationPanel.pressDragged(uiTx, uiTy)
            return true
        }

        if (!isModalActive() && riverHintPanel.isOpen) {
            return riverHintPanel.touchDragged(uiTx, uiTy)
        }
        return false
    }

    override fun touchUp(screenX: Int, screenY: Int, pointer: Int, button: Int): Boolean {
        val ui = screenToUi(screenX, screenY) ?: run {
            shufflePreparationPanel.pressCancel()
            return false
        }
        val uiTx = ui.x
        val uiTy = ui.y
        lastMouseX = uiTx
        lastMouseY = uiTy

        if (shufflePreparationPanel.isActive) {
            // 按下与松开位置一致才确认。
            shufflePreparationPanel.pressUp(uiTx, uiTy)
            return true
        }

        if (!isModalActive() && riverHintPanel.isOpen) {
            return riverHintPanel.touchUp(uiTx, uiTy)
        }
        return false
    }

    override fun scrolled(amountX: Float, amountY: Float): Boolean {
        if (!isModalActive() && riverHintPanel.isOpen) {
            val inPanel = riverHintPanel.panelRect.contains(lastMouseX, lastMouseY) || (Gdx.graphics == null || Gdx.graphics.width == 0)
            if (inPanel) {
                return riverHintPanel.scrolled(amountX, amountY)
            }
        }
        return false
    }

    override fun resize(width: Int, height: Int) {
        // 先更新视图几何，再处理后续输入；resize 只取消已按住的控件，不清草稿、
        // 看破、费用、权威截止、回执或动画进度。零宽高时相机保持不可绘制，恢复后重算。
        if (width <= 0 || height <= 0) {
            stageViewport = TableStageViewport.EMPTY
            shufflePreparationPanel.pressCancel()
            return
        }
        updateStageForWindow()
        shufflePreparationPanel.pressCancel()
    }
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
        sleeveBloomPanel.dispose()
        beamSwapPanel.dispose()
        riverHintPanel.dispose()
        shufflePreparationPanel.dispose()
        settlementDetailRenderer.dispose()
    }
}

/**
 * 无头测试的 Gdx.graphics 可能没有有效窗口尺寸；此时用 1280×720 设计视口作为
 * 输入命中的初始值。真实桌面/Android 在首帧 resize/render 之前也有窗口尺寸，
 * 正式零尺寸会在 resize 中显式切成不可绘制，不会落到这里。
 */
private fun initialStageViewport(): TableStageViewport {
    val g = Gdx.graphics
    if (g == null || g.width <= 0 || g.height <= 0) {
        return TableStageViewport.compute(1280, 720)
    }
    return TableStageViewport.compute(
        windowWidth = g.width,
        windowHeight = g.height,
        framebufferWidth = g.backBufferWidth.coerceAtLeast(1),
        framebufferHeight = g.backBufferHeight.coerceAtLeast(1)
    )
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
