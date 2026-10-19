package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.Input
import com.badlogic.gdx.InputAdapter
import com.badlogic.gdx.Screen
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.math.Vector3
import com.majiang.client.api.GameEventView
import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.PublicBoardView
import com.majiang.client.api.PublicSnapshotView
import com.majiang.client.api.PublicSessionResultView
import com.majiang.client.api.PublicUpdateView
import com.majiang.client.api.SettlementDetailView
import com.majiang.client.api.SpectatorSession
import com.majiang.presentation.common.FinalResultRenderer
import com.majiang.presentation.common.SettlementDetailPresenter
import com.majiang.presentation.common.SettlementDetailRenderer
import com.majiang.presentation.common.SettlementDetailRow
import com.majiang.presentation.common.SettlementModalGeometry
import com.majiang.presentation.common.SettlementSection
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.DiscardPhase
import com.majiang.presentation.common.render.TableBoardRenderModel
import com.majiang.presentation.common.render.TableBoardRenderer
import com.majiang.presentation.common.render.TableBoardRenderOptions
import com.majiang.presentation.common.render.TableBoardSyncTracker
import com.majiang.presentation.common.render.TableBoardSyncTransition
import com.majiang.presentation.common.render.TableBoardViewIdentity
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin
import com.majiang.presentation.common.render.fromPublicSnapshot

/**
 * 独立观战牌桌场景 (V4 presentation.game)。
 *
 * 核心契约与安全隔离规范：
 * 1. 严格只依赖 [UiAssets]（根借用）、[SpectatorSession]、[TableNavigation] 与可选 [TileSkin]；
 * 2. 独立只读公开流：仅消费 [PublicSnapshotView] 与 [PublicUpdateView]，绝不伪造或依赖私有玩家快照；
 * 3. 严格私密隔离：手牌完全按 [PublicBoardView.handTileCounts] 渲染为牌背，绝无手牌明面；
 * 4. 纯只读界面：完全不渲染吃碰杠、立直、自摸、荣和、过牌、托管、开赛、理牌或千术按钮；
 * 5. 每屏独立弃牌动画/游标/重建状态：全量快照、漏包、换局/换代次只重建基线，不重播历史；
 * 6. 结算只消费公开 settlementEvents 中已授权的公开字段与 breakdown，绝不泄漏里宝/暗牌/账本/千术；
 * 7. 零 engine、protocol、server 依赖。
 */
class SpectatorScreen(
    val assets: UiAssets,
    val session: SpectatorSession,
    val navigation: TableNavigation,
    private val skinOverride: TileSkin? = null
) : Screen, InputAdapter() {

    val tileSkin: TileSkin
        get() = skinOverride ?: assets.skinManager.currentSkin

    private val tileDrawBuffer = TileDrawBuffer()

    val camera = OrthographicCamera().apply {
        setToOrtho(false, 1280f, 720f)
    }

    private var updateSubscription: AutoCloseable? = null
    private var disconnectSubscription: AutoCloseable? = null

    var currentSnapshot: PublicSnapshotView? = session.currentPublicSnapshot
    val currentBoard: PublicBoardView? get() = currentSnapshot?.board
    var isLoading: Boolean = (currentSnapshot == null)
    private var lastClockUpdateLocalMillis: Long = System.currentTimeMillis()

    /** 本屏独立的同步游标/代次/换局状态；与玩家屏或任何其他观战屏绝不共享。 */
    private val syncTracker = TableBoardSyncTracker(
        currentSnapshot?.let {
            TableBoardViewIdentity(it.sessionId, it.syncEpoch, it.roundId, it.cursor)
        }
    )

    /**
     * 本屏独立的弃牌动画控制器。
     *
     * 观战始终以 viewerSeat=0 的公开相对布局绘制；起点/落点与玩家屏共用
     * [TableBoardRenderer] 的纯几何常量，但不共享任何动画或基准状态。
     */
    val discardAnimationController = DiscardAnimationController(
        startPositionProvider = { seat -> TableBoardRenderer.computeDiscardStart(seat, 0) },
        slotPositionProvider = { seat, slotIndex ->
            TableBoardRenderer.computeDiscardSlotPosition(seat, slotIndex, 0)
        },
        initialDiscards = currentSnapshot?.board?.discards ?: List(4) { emptyList() }
    )

    val isDiscardPlaybackBusy: Boolean
        get() = discardAnimationController.isDiscardPlaybackBusy

    /** 观战公开结算状态（独立于玩家屏的 currentSettlement）。 */
    private var showSettlement = false
    var currentSettlement: SettlementDetailView? = null
        private set
    private var activeSettlementRoundId: String? = null
    var settlementSection: SettlementSection = SettlementSection.YAKU
        private set
    var settlementPage: Int = 0
        private set

    private val finalResultRenderer = FinalResultRenderer()
    private val settlementDetailRenderer = SettlementDetailRenderer(assets)

    val isSettlementShown: Boolean get() = showSettlement

    val isMatchFinished: Boolean
        get() = currentBoard?.phase == GamePhaseView.MATCH_END ||
            currentSnapshot?.session?.finalResult != null

    val settlementPageCount: Int
        get() {
            val s = currentSettlement ?: return 0
            val bd = s.breakdown
            if (bd != null) return SettlementDetailPresenter.pageCount(bd, settlementSection)
            val rows = legacySettlementRows(s)
            return if (rows.isEmpty()) 1 else (rows.size + SettlementDetailPresenter.ROWS_PER_PAGE - 1) /
                SettlementDetailPresenter.ROWS_PER_PAGE
        }

    fun visibleSettlementRows(): List<SettlementDetailRow> {
        val s = currentSettlement ?: return emptyList()
        val bd = s.breakdown
        if (bd != null) {
            return SettlementDetailPresenter.page(bd, settlementSection, settlementPage)
        }
        val rows = legacySettlementRows(s)
        val fromIndex = settlementPage * SettlementDetailPresenter.ROWS_PER_PAGE
        if (settlementPage < 0 || fromIndex >= rows.size) return emptyList()
        val toIndex = minOf(fromIndex + SettlementDetailPresenter.ROWS_PER_PAGE, rows.size)
        return rows.subList(fromIndex, toIndex)
    }

    private fun legacySettlementRows(s: SettlementDetailView): List<SettlementDetailRow> {
        val font = assets.chineseFont ?: assets.font
        val source = when {
            s.isDraw -> listOf(if (s.drawReason.isBlank()) "流局" else "流局：${s.drawReason}")
            s.yakuList.isEmpty() -> listOf("无役")
            else -> s.yakuList
        }
        return source.flatMap { line ->
            SettlementDetailRenderer.wrapRowText(font, line, 800f).map { SettlementDetailRow(it) }
        }
    }

    private val unprojectVec = Vector3()
    private var previousCatchBack = false
    private var ownsBackCatch = false

    init {
        updateSubscription = session.onPublicUpdate { update ->
            runOnUi {
                onPublicUpdate(update)
            }
        }

        disconnectSubscription = session.onDisconnect { _ ->
            // Disconnect notification
        }

        val snap = session.currentPublicSnapshot
        if (snap != null) {
            currentSnapshot = snap
            isLoading = false
            refreshSettlement(snap, emptyList())
        } else {
            isLoading = true
            session.publicSnapshot().whenComplete { s, _ ->
                runOnUi {
                    isLoading = false
                    if (s != null) {
                        applyPublicSnapshot(s, emptyList(), isFullSnapshot = true)
                    }
                }
            }
        }
    }

    fun onPublicUpdate(update: PublicUpdateView) {
        applyPublicSnapshot(
            snapshot = update.snapshot,
            events = update.visibleEvents,
            isFullSnapshot = update.isFullSnapshot
        )
    }

    /**
     * 应用一份公开快照。
     *
     * 事件顺序契约：
     * 1. 普通连续更新（fromCursor == 上一 cursor）：仅按 visibleEvents 的权威顺序为新增弃牌入队；
     * 2. 显式全量快照 / 首次基线 / 游标跳号 / 换会话 / 换 syncEpoch / 换局：立即清空旧动画并
     *    以新牌河重建基线，不重播历史牌，避免旧动画掩盖新牌；
     * 3. 重复投递（同身份同 cursor）不重复入队，避免重复执行动画动作。
     */
    private fun applyPublicSnapshot(
        snapshot: PublicSnapshotView,
        events: List<GameEventView>,
        isFullSnapshot: Boolean
    ) {
        val transition = syncTracker.observe(
            TableBoardViewIdentity(
                snapshot.sessionId,
                snapshot.syncEpoch,
                snapshot.roundId,
                snapshot.cursor
            ),
            isFullSnapshot = isFullSnapshot
        )
        val discards = snapshot.board?.discards ?: List(4) { emptyList() }

        if (transition.requiresRebuild) {
            discardAnimationController.resetBaseline(discards)
        } else if (!transition.duplicate) {
            discardAnimationController.checkDiscardsDiff(discards, events)
        }

        currentSnapshot = snapshot
        lastClockUpdateLocalMillis = System.currentTimeMillis()
        isLoading = false

        refreshSettlement(snapshot, events, transition)
    }

    private fun refreshSettlement(
        snapshot: PublicSnapshotView,
        events: List<GameEventView>,
        transition: TableBoardSyncTransition? = null
    ) {
        val stackingStarted = events.any { it is GameEventView.StackingStarted }
        if (stackingStarted || transition?.sessionChanged == true ||
            transition?.epochChanged == true || transition?.roundChanged == true
        ) {
            hideSettlement()
        }

        val target = snapshot.settlementRoundId
        if (target != null) {
            if (!showSettlement || activeSettlementRoundId != target) {
                val detail = snapshot.settlementDetail
                if (detail != null) {
                    // 结算到达时立即收掉尚未播完的弃牌动画，避免结算遮罩下残留旧牌。
                    discardAnimationController.fastForward(
                        snapshot.board?.discards ?: List(4) { emptyList() }
                    )
                    currentSettlement = detail
                    showSettlement = true
                    activeSettlementRoundId = target
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                } else {
                    hideSettlement()
                }
            }
        } else if (showSettlement && !isMatchFinished) {
            hideSettlement()
        }
    }

    private fun hideSettlement() {
        showSettlement = false
        currentSettlement = null
        activeSettlementRoundId = null
        settlementSection = SettlementSection.YAKU
        settlementPage = 0
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

    val backButtonRect = Rectangle(30f, 655f, 140f, 42f)
    val btnReturnToMenuRect = Rectangle(520f, 145f, 240f, 46f)

    @Volatile
    var isDisposed: Boolean = false
        private set

    override fun show() {
        Gdx.input?.let { input ->
            previousCatchBack = input.isCatchKey(Input.Keys.BACK)
            input.setCatchKey(Input.Keys.BACK, true)
            ownsBackCatch = true
            input.inputProcessor = this
        }
    }

    override fun hide() {
        discardAnimationController.fastForward(currentBoard?.discards ?: List(4) { emptyList() })
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
        discardAnimationController.update(delta)

        camera.update()
        Gdx.gl.glClearColor(0.06f, 0.16f, 0.12f, 1f)
        Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT)

        val batch = assets.batch
        val shape = assets.shapeRenderer
        val font = assets.font
        val chineseFont = assets.chineseFont

        // 1. 顶部返回菜单按钮
        renderBackButton(shape, batch, font)

        val snap = currentSnapshot
        if (snap?.board != null) {
            val model = TableBoardRenderModel.fromPublicSnapshot(snap)
            val options = TableBoardRenderOptions(
                isTileVisibleInRiver = { seat, tile, index ->
                    discardAnimationController.isTileVisibleInRiver(seat, tile, index)
                },
                highlightTileId = null,
                clockElapsedMillis = System.currentTimeMillis() - lastClockUpdateLocalMillis
            )

            // 2. 桌面中心信息（风局、牌山、宝牌等）
            TableBoardRenderer.renderTableCenter(
                model = model,
                projectionMatrix = camera.combined,
                batch = batch,
                shape = shape,
                font = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                options = options
            )

            // 3. 四家席位信息（分数、时钟、立直等）
            TableBoardRenderer.renderSeatsInfo(
                model = model,
                projectionMatrix = camera.combined,
                batch = batch,
                shape = shape,
                font = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                options = options
            )

            // 4. 四家手牌（严格纯牌背！）
            TableBoardRenderer.renderHandAreas(
                model = model,
                projectionMatrix = camera.combined,
                batch = batch,
                shape = shape,
                font = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                options = options
            )

            // 5. 四家弃牌河
            TableBoardRenderer.renderDiscards(
                model = model,
                projectionMatrix = camera.combined,
                batch = batch,
                shape = shape,
                font = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                options = options
            )

            // 6. 四家副露
            TableBoardRenderer.renderMelds(
                model = model,
                projectionMatrix = camera.combined,
                batch = batch,
                shape = shape,
                font = font,
                chineseFont = chineseFont,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                options = options
            )

            // 7. 正在飞行的弃牌（仅使用本屏独立控制器）
            renderFlyingDiscard(batch, shape, font)
        }

        // 8. 公开结算详情
        if (showSettlement && currentSettlement != null) {
            settlementDetailRenderer.renderPublicDetailModal(
                camera = camera,
                shape = shape,
                batch = batch,
                fallbackFont = font,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                settlement = currentSettlement!!,
                section = settlementSection,
                page = settlementPage,
                currentScores = currentBoard?.scores ?: emptyList()
            )
        }

        // 9. 终局总成绩弹窗（比赛结束时展示）
        val finalResult = currentSnapshot?.session?.finalResult
        if (finalResult != null || currentBoard?.phase == GamePhaseView.MATCH_END) {
            val board = currentBoard
            val scores = board?.scores ?: listOf(25000, 25000, 25000, 25000)
            finalResultRenderer.render(
                camera = camera,
                shape = shape,
                batch = batch,
                fallbackFont = font,
                chineseFont = chineseFont,
                finalResult = finalResult?.let {
                    PublicSessionResultView(it.finalScores, it.rankedSeats, it.reason)
                },
                sessionView = null,
                currentScores = scores,
                viewerSeat = -1, // 观战模式无特定玩家席位
                hasRoundDetails = false,
                btnReturnToMenuRect = btnReturnToMenuRect,
                btnRoundDetailsRect = Rectangle(0f, 0f, 0f, 0f)
            )
        }
    }

    private fun renderFlyingDiscard(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        val anim = discardAnimationController.currentDiscardAnim ?: return
        val pose = discardAnimationController.currentPose() ?: return
        if (pose.phase == DiscardPhase.DONE) return

        val tileW = 44f * pose.scaleX
        val tileH = 62f * pose.scaleY

        // 观战没有落汗授权，飞牌始终保持公开牌面不透明；绝不读取玩家私有标记。
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
            alpha = 1.0f
        )
        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, font)
    }

    private fun renderBackButton(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0.20f, 0.35f, 0.28f, 1f)
        shape.rect(backButtonRect.x, backButtonRect.y, backButtonRect.width, backButtonRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color.WHITE
        shape.rect(backButtonRect.x, backButtonRect.y, backButtonRect.width, backButtonRect.height)
        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()
        val textFont = assets.chineseFont ?: font
        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY
        val origColor = Color(textFont.color)
        try {
            textFont.data.setScale(1.0f)
            textFont.color = Color.WHITE
            textFont.draw(batch, "返回大厅", backButtonRect.x + 28f, backButtonRect.y + 27f)
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
            batch.end()
        }
    }

    override fun keyDown(keycode: Int): Boolean {
        if (keycode == Input.Keys.ESCAPE || keycode == Input.Keys.BACK) {
            leaveToMenu()
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

        if (backButtonRect.contains(tx, ty)) {
            leaveToMenu()
            return true
        }

        if (showSettlement && currentSettlement != null) {
            handleSettlementTouch(tx, ty)
            return true
        }

        val isFinished = (currentSnapshot?.session?.finalResult != null || currentBoard?.phase == GamePhaseView.MATCH_END)
        if (isFinished && btnReturnToMenuRect.contains(tx, ty)) {
            leaveToMenu()
            return true
        }

        return true // 观战拦截所有其他点击，防止任何非预期操作
    }

    /**
     * 公开结算只允许在公开页面/标签间导航，绝不提交任何游戏动作。
     */
    private fun handleSettlementTouch(tx: Float, ty: Float) {
        val breakdown = currentSettlement?.breakdown
        if (breakdown != null) {
            if (SettlementModalGeometry.TAB_YAKU_RECT.contains(tx, ty)) {
                settlementSection = SettlementSection.YAKU
                settlementPage = 0
                return
            }
            if (SettlementModalGeometry.TAB_DORA_RECT.contains(tx, ty)) {
                settlementSection = SettlementSection.DORA
                settlementPage = 0
                return
            }
            if (SettlementModalGeometry.TAB_CALC_RECT.contains(tx, ty)) {
                settlementSection = SettlementSection.CALC
                settlementPage = 0
                return
            }
        }
        if (SettlementModalGeometry.BTN_PREV_PAGE_RECT.contains(tx, ty)) {
            if (settlementPage > 0) settlementPage--
            return
        }
        if (SettlementModalGeometry.BTN_NEXT_PAGE_RECT.contains(tx, ty)) {
            if (settlementPage < settlementPageCount - 1) settlementPage++
        }
    }

    private fun leaveToMenu() {
        navigation.leaveToMenu()
    }

    override fun resize(width: Int, height: Int) {}
    override fun pause() {}
    override fun resume() {}

    override fun dispose() {
        if (isDisposed) return
        isDisposed = true
        updateSubscription?.close()
        updateSubscription = null
        disconnectSubscription?.close()
        disconnectSubscription = null
        discardAnimationController.fastForward()
        settlementDetailRenderer.dispose()
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
}
