package com.majiang.screen

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
import com.majiang.MahjongGame
import com.majiang.event.GameEvent
import com.majiang.model.MeldType
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.render.DiscardAnimation
import com.majiang.render.DiscardPhase
import com.majiang.render.DiscardPose
import com.majiang.render.DoraSparkle
import com.majiang.render.GhostHandTexture
import com.majiang.render.MeldDisplayGroup
import com.majiang.render.MeldDisplayLayout
import com.majiang.render.MeldDisplayRenderer
import com.majiang.render.TileArranger
import com.majiang.application.ActionChoice
import com.majiang.client.LocalPlayerEndpoint
import com.majiang.client.PlayerEndpoint
import com.majiang.render.TileDrawBuffer
import com.majiang.render.TileSkin
import com.majiang.rules.FuritenStatus
import com.majiang.session.GameSession
import com.majiang.session.MeldChoice
import com.majiang.session.MeldChoiceKind
import com.majiang.session.PlayerView
import com.majiang.session.RiichiDiscardOption
import com.majiang.state.ActionResult
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import java.util.UUID

/**
 * 游戏牌桌场景（表现层）。
 *
 * 核心设计原则（三层解耦架构 V2 / V3）：
 * 1. 严格只依赖 PlayerEndpoint 与只读深拷贝 PlayerView，完全移除对内部 GameState、GameStateMachine 和 RiichiRuleEngine 的直接依赖。
 * 2. 按钮与交互仅取 legalActions，副露后自摸等合法性由领域层裁决，不在此处假定暗牌数 >= 14。
 * 3. 牌河直接从 PlayerView.discards 渲染，避免暗杠/加杠误删牌河。
 * 4. 支持任意 viewerSeat（包括非 0 号人类），面向接口编程。
 * 5. 集成立直选牌与待牌预览模型 (RiichiSelectionModel)，点击立直仅进入选牌，确认才提交。
 * 6. 默认使用新 Coordinator + MatchSession + Round 路径，UI 交互优先通过候选决策接口 (queryDecision / submitChoice) 提交。
 */
class GameScreen(
    private val game: MahjongGame,
    val endpoint: PlayerEndpoint,
    private val skinOverride: TileSkin? = null
) : Screen, InputAdapter() {

    /** 便利构造器：默认创建面向 PlayerEndpoint 的本地单机端点（默认使用新 Coordinator + MatchSession + Round 路径） */
    constructor(game: MahjongGame, skinOverride: TileSkin? = null) : this(
        game = game,
        endpoint = game.createLocalEndpoint(),
        skinOverride = skinOverride
    )

    /** 兼容旧 GameSession 调用的构造器 */
    constructor(game: MahjongGame, session: GameSession, skinOverride: TileSkin? = null) : this(
        game = game,
        endpoint = session,
        skinOverride = skinOverride
    )

    /** 兼容旧代码与测试的 session 引用 */
    val session: GameSession = (endpoint as? GameSession) ?: object : GameSession, PlayerEndpoint by endpoint {}

    private val tileSkin: TileSkin
        get() = skinOverride ?: game.skinManager.currentSkin

    private val tileDrawBuffer = TileDrawBuffer()
    private val glyphLayout = GlyphLayout()

    private val camera = OrthographicCamera().apply {
        setToOrtho(false, 1280f, 720f)
    }

    // 会话订阅与当前只读视图
    private var sessionSubscription: AutoCloseable? = null
    private var currentView: PlayerView = endpoint.view()

    // 立直选牌纯表现层状态模型（解耦并支持独立测试）
    val riichiSelectionModel = RiichiSelectionModel()

    // 副露选牌纯表现层状态模型（解耦并支持独立测试）
    val meldSelectionModel = MeldSelectionModel()

    // 牌桌 UI 状态（纯表现层）
    private var gameStarted = false

    // 结算数据封装
    data class SettlementData(
        val title: String,
        val winner: Int?,
        val loser: Int?,
        val winTile: Tile?,
        val yakuList: List<String>,
        val han: Int,
        val fu: Int,
        val score: Int,
        val isDraw: Boolean = false,
        val drawReason: String = "",
        val payments: Map<Int, Int> = emptyMap(),
        val newScores: List<Int> = emptyList(),
        val breakdown: com.majiang.rules.ScoreBreakdown? = null
    )

    private var showSettlement = false
    private var currentSettlement: SettlementData? = null
    var stateTime: Float = 0f
    val isSettlementShown: Boolean get() = showSettlement
    val currentSettlementData: SettlementData? get() = currentSettlement

    // 结算明细分页与呈现状态 (Task U / Task H 契约)
    var settlementSection: SettlementSection = SettlementSection.YAKU
        private set
    var settlementPage: Int = 0
        private set
    val settlementPageCount: Int
        get() {
            val s = currentSettlement ?: return 0
            if (s.breakdown != null) {
                return SettlementDetailPresenter.pageCount(s.breakdown, settlementSection)
            }
            if (s.isDraw) return 0
            val allRows = legacyYakuRows(s)
            return maxOf(1, (allRows.size + 5) / 6)
        }

    fun visibleSettlementRows(): List<SettlementDetailRow> {
        val s = currentSettlement ?: return emptyList()
        if (s.breakdown != null) {
            return SettlementDetailPresenter.page(s.breakdown, settlementSection, settlementPage)
        }
        if (s.isDraw) return emptyList()
        val allRows = legacyYakuRows(s)
        val fromIndex = settlementPage * 6
        if (settlementPage < 0 || fromIndex >= allRows.size) return emptyList()
        val toIndex = minOf(fromIndex + 6, allRows.size)
        return allRows.subList(fromIndex, toIndex)
    }

    fun legacyYakuRows(s: SettlementData): List<SettlementDetailRow> {
        val font = if (Gdx.graphics != null && Gdx.graphics.width > 0) {
            try {
                settlementDetailRenderer.getOrInitChineseFont()
            } catch (_: Throwable) {
                null
            }
        } else {
            null
        }
        if (s.yakuList.isEmpty()) {
            val isCustom = font != null
            return listOf(SettlementDetailRow(if (isCustom) "无役" else "No Yaku"))
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

    private val settlementDetailRenderer = SettlementDetailRenderer()

    // 详情弹窗交互矩形区域 (Task U 契约)
    val detailModalRect: Rectangle get() = SettlementModalGeometry.MODAL_RECT
    val tabYakuRect: Rectangle get() = SettlementModalGeometry.TAB_YAKU_RECT
    val tabDoraRect: Rectangle get() = SettlementModalGeometry.TAB_DORA_RECT
    val tabCalcRect: Rectangle get() = SettlementModalGeometry.TAB_CALC_RECT
    val btnPrevPageRect: Rectangle get() = SettlementModalGeometry.BTN_PREV_PAGE_RECT
    val btnNextPageRect: Rectangle get() = SettlementModalGeometry.BTN_NEXT_PAGE_RECT

    // 旧记录和牌翻页按钮矩形区域 (Task H 契约)
    val btnLegacyPrevPageRect = Rectangle(350f, 250f, 100f, 30f)
    val btnLegacyNextPageRect = Rectangle(810f, 250f, 100f, 30f)

    // 交互矩形区域
    private val backButtonRect = Rectangle(30f, 655f, 140f, 42f)
    private val btnStartGameRect = Rectangle(500f, 320f, 280f, 60f)
    private val btnTsumoRect = Rectangle(840f, 145f, 150f, 44f)
    val btnRiichiRect = Rectangle(670f, 145f, 140f, 44f)
    val btnCancelRiichiRect = Rectangle(1030f, 145f, 170f, 44f)
    val waitsPanelRect = Rectangle(230f, 210f, 800f, 190f)

    // 副露候选面板及卡片布局（统一布局契约）
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
    private val btnNextRoundRect = Rectangle(520f, 145f, 240f, 46f)
    private val btnNextRoundSplitRect = Rectangle(370f, 145f, 260f, 46f)
    private val btnAgariYameRect = Rectangle(660f, 145f, 260f, 46f)
    private val modalRect = Rectangle(290f, 120f, 700f, 480f)
    val btnSortRect = Rectangle(1080f, 58f, 140f, 44f)

    // 理牌开关状态（默认 OFF）
    var isAutoSort: Boolean = false

    // 手牌槽位描述
    data class HandTileSlot(
        val rect: Rectangle,
        val tile: Tile,
        val isDrawn: Boolean
    )

    // 动态手牌包围盒（保留供旧测试或外部检查）
    val handTileRects = mutableListOf<Pair<Rectangle, Tile>>()

    // 坐标投影与悬停
    private val unprojectVec = Vector3()
    private var isBackHovered = false
    private var isSortHovered = false
    private var hoveredTile: Tile? = null
    private var hoveredTileIndex = -1
    private var previousCatchBack = false
    private var ownsBackCatch = false

    // 打牌动画单槽队列与调和状态
    data class QueuedDiscard(
        val seat: Int,
        val tile: Tile,
        val targetIndex: Int,
        val start: Pair<Float, Float>,
        val end: Pair<Float, Float>
    )

    val discardQueue = ArrayDeque<QueuedDiscard>()
    var currentDiscardAnim: QueuedDiscard? = null
    var discardAnimElapsed: Float = 0f
    private var lastDiscards: List<List<Tile>> = (0..3).map { currentView.discards.getOrNull(it)?.toList() ?: emptyList() }

    val isDiscardPlaybackBusy: Boolean
        get() = currentDiscardAnim != null || discardQueue.isNotEmpty()

    private fun clearGameplayHover() {
        isSortHovered = false
        hoveredMeldCardIndex = -1
        hoveredTile = null
        hoveredTileIndex = -1
    }

    init {
        // 若进入场景时对局已在进行中，同步开局标记
        if (currentView.myAllClosedTiles.isNotEmpty() || currentView.phase != GamePhase.STACKING) {
            gameStarted = true
        }

        // 初始化立直与副露模型选项
        riichiSelectionModel.update(currentView)
        meldSelectionModel.update(currentView)

        // 订阅端点广播更新
        sessionSubscription = endpoint.subscribe { update ->
            currentView = update.view
            riichiSelectionModel.update(currentView)
            meldSelectionModel.update(currentView)
            handleSessionEvents(update.visibleEvents)
            checkDiscardsDiff(update.visibleEvents)
            if (isDiscardPlaybackBusy) {
                clearGameplayHover()
            }
        }

        // 初始刷新手牌槽位与包围盒
        refreshHandTileRects()
    }

    private fun handleSessionEvents(events: List<GameEvent>) {
        for (event in events) {
            when (event) {
                is GameEvent.StackingStarted -> {
                    fastForwardDiscardAnimations()
                    lastDiscards = (0..3).map { currentView.discards.getOrNull(it)?.toList() ?: emptyList() }
                    clearGameplayHover()
                    showSettlement = false
                    currentSettlement = null
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                    gameStarted = true
                    riichiSelectionModel.cancel()
                    meldSelectionModel.cancel()
                }
                is GameEvent.TsumoWin -> {
                    fastForwardDiscardAnimations()
                    clearGameplayHover()
                    riichiSelectionModel.cancel()
                    meldSelectionModel.cancel()
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                    currentSettlement = SettlementData(
                        title = "TSUMO WIN!",
                        winner = event.player,
                        loser = null,
                        winTile = event.winTile,
                        yakuList = event.yakuList,
                        han = event.han,
                        fu = event.fu,
                        score = event.score,
                        breakdown = event.breakdown
                    )
                    showSettlement = true
                }
                is GameEvent.RonWin -> {
                    fastForwardDiscardAnimations()
                    clearGameplayHover()
                    riichiSelectionModel.cancel()
                    meldSelectionModel.cancel()
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                    currentSettlement = SettlementData(
                        title = "RON WIN!",
                        winner = event.winner,
                        loser = event.loser,
                        winTile = event.winTile,
                        yakuList = event.yakuList,
                        han = event.han,
                        fu = event.fu,
                        score = event.score,
                        breakdown = event.breakdown
                    )
                    showSettlement = true
                }
                is GameEvent.DrawGame -> {
                    fastForwardDiscardAnimations()
                    clearGameplayHover()
                    riichiSelectionModel.cancel()
                    meldSelectionModel.cancel()
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                    currentSettlement = SettlementData(
                        title = "EXHAUSTIVE DRAW",
                        winner = null,
                        loser = null,
                        winTile = null,
                        yakuList = emptyList(),
                        han = 0,
                        fu = 0,
                        score = 0,
                        isDraw = true,
                        drawReason = event.reason.name
                    )
                    showSettlement = true
                }
                is GameEvent.ScoreSettled -> {
                    currentSettlement = currentSettlement?.copy(
                        payments = event.payments,
                        newScores = event.newScores.toList()
                    )
                }
                else -> {}
            }
        }
    }

    override fun show() {
        Gdx.input?.let { input ->
            input.inputProcessor = this
            if (!ownsBackCatch) {
                previousCatchBack = input.isCatchKey(Input.Keys.BACK)
                ownsBackCatch = true
            }
            input.setCatchKey(Input.Keys.BACK, true)
        }
    }

    override fun render(delta: Float) {
        stateTime += delta

        // 检查上一帧与当前帧牌河差异，触发打牌动画入队并驱动
        checkDiscardsDiff()
        updateDiscardAnimation(delta)

        // 动画更新后再更新鼠标悬停，不显示误导的可操作高亮
        updateMouseHover()

        camera.update()
        Gdx.gl.glClearColor(0.05f, 0.16f, 0.10f, 1f)
        Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT)

        val shape = game.shapeRenderer
        val batch = game.batch
        val font = game.font

        tileDrawBuffer.clear()

        // 1. 绘制底色与桌面
        renderTableFelt(shape)

        // 2. 收集各家牌河与牌桌信息（直接使用 PlayerView 中的牌河快照）
        renderDiscardRivers()
        renderTableInfo(batch, font)

        // 3. 收集观察者手牌与已完成副露展示
        renderPlayerHands(batch, font)
        val melds = displayedMelds()
        if (melds.isNotEmpty()) {
            val visibleDoraTargets = DoraSparkle.computeVisibleTargets(currentView.doraIndicators)
            val alpha = DoraSparkle.sparkleAlpha(stateTime)
            MeldDisplayRenderer.append(tileDrawBuffer, tileSkin, melds, visibleDoraTargets, alpha)
        }

        // 3.5 收集正在飞行的打牌与幽灵白手套
        renderDiscardAnimation()

        // 4. 绘制交互操作按钮（根据 legalActions 及选牌状态动态展示）
        renderActionButtons(shape, batch, font)

        // 5. 合并批次提交所有桌面牌面（必须先 flush 桌面牌面，再绘制面板与它的牌面，否则牌河会覆盖面板）
        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, font)

        // 6. 若处于立直选牌模式：绘制手牌高亮/暗化框线，以及待牌预览面板
        if (riichiSelectionModel.isSelecting && !isDiscardPlaybackBusy) {
            renderRiichiSelectionOverlay(shape, batch, font)
            renderWaitsPreviewPanel(shape, batch, font)
        }

        // 6.5 若处于副露选牌模式：绘制手牌高亮/暗化框线，以及副露方案候选面板
        if (meldSelectionModel.isSelecting && !isDiscardPlaybackBusy) {
            renderMeldSelectionOverlay(shape, batch, font)
            renderMeldChoicePanel(shape, batch, font)
        }

        // 7. 绘制结算弹窗
        if (showSettlement) {
            renderSettlementModal(shape, batch, font)
        }

        // 8. 绘制顶部 HUD 与返回按钮
        renderTopHud(shape, batch, font)
    }

    private fun updateMouseHover() {
        if (Gdx.graphics == null || Gdx.graphics.width == 0) return
        val mouseX = Gdx.input.x.toFloat()
        val mouseY = Gdx.input.y.toFloat()
        unprojectVec.set(mouseX, mouseY, 0f)
        camera.unproject(unprojectVec)
        updateHoverCoordinates(unprojectVec.x, unprojectVec.y)
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
        updateHoverCoordinates(tx, ty)
        return false
    }

    private fun updateHoverCoordinates(tx: Float, ty: Float) {
        isBackHovered = backButtonRect.contains(tx, ty)
        if (isDiscardPlaybackBusy) {
            clearGameplayHover()
            return
        }

        isSortHovered = gameStarted && btnSortRect.contains(tx, ty)

        // 检查副露候选卡片悬停（悬停可临时高亮参与的手牌，但不能改变确认方案）
        hoveredMeldCardIndex = -1
        if (meldSelectionModel.isSelecting) {
            for (i in meldSelectionModel.visibleOptions.indices) {
                if (meldCardRects[i].contains(tx, ty)) {
                    hoveredMeldCardIndex = i
                    break
                }
            }
        }

        // 检查手牌悬停
        hoveredTile = null
        hoveredTileIndex = -1
        if (isHumanTurn() || riichiSelectionModel.isSelecting) {
            val slots = refreshHandTileRects()
            for (i in slots.indices) {
                val slot = slots[i]
                if (slot.rect.contains(tx, ty)) {
                    hoveredTile = slot.tile
                    hoveredTileIndex = i
                    break
                }
            }
        }
    }

    fun isHumanTurn(): Boolean {
        return gameStarted && !showSettlement && !isDiscardPlaybackBusy &&
                !riichiSelectionModel.isSelecting && !meldSelectionModel.isSelecting &&
                currentView.legalActions.any { it is GameAction.DiscardTile && it.player == endpoint.viewerSeat }
    }

    private fun renderTableFelt(shape: ShapeRenderer) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 桌面边框（红木暗色）
        shape.color = Color(0.18f, 0.10f, 0.05f, 1f)
        shape.rect(15f, 15f, 1250f, 690f)

        // 绿呢台面
        shape.color = Color(0.07f, 0.24f, 0.14f, 1f)
        shape.rect(28f, 28f, 1224f, 664f)

        // 牌桌中央牌池装饰框
        shape.color = Color(0.05f, 0.18f, 0.10f, 1f)
        shape.rect(340f, 210f, 600f, 380f)

        shape.end()

        // 金色装饰线条
        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.85f, 0.72f, 0.35f, 0.5f)
        shape.rect(340f, 210f, 600f, 380f)
        shape.end()
    }

    private fun renderTopHud(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 返回按钮
        shape.color = if (isBackHovered) Color(0.85f, 0.65f, 0.15f, 1f) else Color(0.35f, 0.22f, 0.12f, 1f)
        shape.rect(backButtonRect.x, backButtonRect.y, backButtonRect.width, backButtonRect.height)

        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.95f, 0.85f, 0.45f, 1f)
        shape.rect(backButtonRect.x, backButtonRect.y, backButtonRect.width, backButtonRect.height)
        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        font.color = Color.WHITE
        font.data.setScale(1.1f)
        font.draw(batch, "< MENU", backButtonRect.x + 36f, backButtonRect.y + 27f)

        // 顶部场况信息
        font.data.setScale(1.2f)
        font.color = Color(0.95f, 0.85f, 0.4f, 1f)
        val windName = when (currentView.roundWind) {
            1 -> "EAST"; 2 -> "SOUTH"; 3 -> "WEST"; 4 -> "NORTH"; else -> "EAST"
        }
        val roundTitle = "$windName ${currentView.roundNumber + 1} ROUND"
        font.draw(batch, roundTitle, 200f, 683f)

        font.data.setScale(1.0f)
        font.color = Color.LIGHT_GRAY
        val subInfo = "HONBA: ${currentView.honbaSticks}   RIICHI: ${currentView.riichiSticks}   WALL: ${currentView.wallRemaining} TILES"
        font.draw(batch, subInfo, 400f, 682f)

        // Dora 指示牌提示
        if (currentView.doraIndicators.isNotEmpty()) {
            val doraText = "DORA: " + currentView.doraIndicators.joinToString(" ") { getTileText(it) }
            font.color = Color(1.0f, 0.85f, 0.3f, 1f)
            font.draw(batch, doraText, 850f, 682f)
        }

        // 振听状态醒目提示 (HUD)：向本人显示醒目短提示 FURITEN - TSUMO ONLY 附原因，不遮挡手牌、按钮与待牌面板
        val furitenText = currentFuritenText()
        if (furitenText != null) {
            batch.end()

            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.70f, 0.12f, 0.12f, 0.92f)
            shape.rect(380f, 642f, 520f, 28f)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(1.0f, 0.85f, 0.30f, 1f)
            shape.rect(380f, 642f, 520f, 28f)
            shape.end()

            batch.begin()
            val prevScaleX = font.data.scaleX
            val prevScaleY = font.data.scaleY
            val prevColor = Color(font.color)
            try {
                font.data.setScale(1.0f, 1.0f)
                val textWidth = measureTextWidth(font, furitenText)
                val maxTextWidth = 500f
                val scale = if (textWidth > maxTextWidth && textWidth > 0f) {
                    (maxTextWidth / textWidth).coerceIn(0.6f, 1.0f)
                } else {
                    1.0f
                }
                font.data.setScale(scale, scale)
                val finalWidth = measureTextWidth(font, furitenText)
                val drawX = 380f + (520f - finalWidth) / 2f
                val drawY = 642f + 20f
                font.color = Color(1.0f, 0.95f, 0.40f, 1f)
                font.draw(batch, furitenText, drawX, drawY)
            } finally {
                font.data.setScale(prevScaleX, prevScaleY)
                font.color = prevColor
            }
        }

        batch.end()
    }

    private fun renderTableInfo(batch: SpriteBatch, font: BitmapFont) {
        batch.projectionMatrix = camera.combined
        batch.begin()

        val viewer = endpoint.viewerSeat

        // P0 (中央偏下)
        font.data.setScale(1.1f)
        font.color = if (currentView.currentTurnPlayer == 0) Color.YELLOW else Color.WHITE
        val p0Wind = getWindText(currentView.seatWind(0))
        val p0Label = if (viewer == 0) "P0 [YOU]" else "P0 [AI 0]"
        font.draw(batch, "$p0Label ($p0Wind) : ${currentView.scores[0]} pts", 480f, 175f)

        // P1 (右侧 AI 1)
        font.color = if (currentView.currentTurnPlayer == 1) Color.YELLOW else Color.WHITE
        val p1Wind = getWindText(currentView.seatWind(1))
        val p1Label = if (viewer == 1) "P1 [YOU]" else "P1 [AI 1]"
        font.draw(batch, "$p1Label ($p1Wind)", 960f, 430f)
        font.draw(batch, "${currentView.scores[1]} pts", 960f, 405f)

        // P2 (对家 AI 2)
        font.color = if (currentView.currentTurnPlayer == 2) Color.YELLOW else Color.WHITE
        val p2Wind = getWindText(currentView.seatWind(2))
        val p2Label = if (viewer == 2) "P2 [YOU]" else "P2 [AI 2]"
        font.draw(batch, "$p2Label ($p2Wind) : ${currentView.scores[2]} pts", 500f, 615f)

        // P3 (左侧 AI 3)
        font.color = if (currentView.currentTurnPlayer == 3) Color.YELLOW else Color.WHITE
        val p3Wind = getWindText(currentView.seatWind(3))
        val p3Label = if (viewer == 3) "P3 [YOU]" else "P3 [AI 3]"
        font.draw(batch, "$p3Label ($p3Wind)", 140f, 430f)
        font.draw(batch, "${currentView.scores[3]} pts", 140f, 405f)

        batch.end()
    }

    fun getDiscardStart(seat: Int): Pair<Float, Float> = when (seat % 4) {
        0 -> Pair(626f, 80f)   // P0 底部手牌区中央
        1 -> Pair(1050f, 420f) // P1 右侧
        2 -> Pair(626f, 630f)  // P2 顶部
        3 -> Pair(200f, 420f)  // P3 左侧
        else -> Pair(626f, 80f)
    }

    private data class DiscardRiverLayout(val startX: Float, val startY: Float, val cols: Int, val yStep: Float)

    fun getDiscardSlotPosition(seat: Int, index: Int): Pair<Float, Float> {
        val tileW = 28f
        val layout = when (seat % 4) {
            0 -> DiscardRiverLayout(460f, 280f, 6, -38f)
            1 -> DiscardRiverLayout(750f, 440f, 3, -38f)
            2 -> DiscardRiverLayout(460f, 530f, 6, 38f)
            3 -> DiscardRiverLayout(350f, 440f, 3, -38f)
            else -> DiscardRiverLayout(460f, 280f, 6, -38f)
        }
        val col = index % layout.cols
        val row = index / layout.cols
        val x = layout.startX + col * (tileW + 3f)
        val y = layout.startY + row * layout.yStep
        return Pair(x, y)
    }

    fun checkDiscardsDiff(events: List<GameEvent> = emptyList()) {
        if (showSettlement) {
            fastForwardDiscardAnimations()
            lastDiscards = (0..3).map { currentView.discards.getOrNull(it)?.toList() ?: emptyList() }
            return
        }

        // 1. 副露调和：从当前待播队列中移除已被吃碰杠取走的牌（按seat+真实Tile身份）
        discardQueue.removeAll { q ->
            val currRiver = currentView.discards.getOrNull(q.seat) ?: emptyList()
            q.tile !in currRiver
        }

        // 2. 若当前正在播放的动画其牌已被副露取走，立即结束该动画
        val current = currentDiscardAnim
        if (current != null) {
            val currRiver = currentView.discards.getOrNull(current.seat) ?: emptyList()
            if (current.tile !in currRiver) {
                currentDiscardAnim = null
                discardAnimElapsed = 0f
            }
        }

        // 3. 事件驱动排队：以 visibleEvents 中 TileDiscarded 的时间戳顺序给动画排队
        for (event in events) {
            if (event is GameEvent.TileDiscarded) {
                val seat = event.player
                val tile = event.tile
                val currRiver = currentView.discards.getOrNull(seat) ?: emptyList()

                // 不把已被吃碰杠取走的牌重新入队
                if (tile !in currRiver) continue

                // 按座位+物理 Tile 身份去重，防止同一广播被事件和快照差异重复入队
                val alreadyQueued = discardQueue.any { it.seat == seat && it.tile == tile }
                val isCurrentAnim = currentDiscardAnim?.let { it.seat == seat && it.tile == tile } == true
                val alreadyInLast = lastDiscards.getOrNull(seat)?.contains(tile) == true
                if (alreadyQueued || isCurrentAnim || alreadyInLast) continue

                val targetIndex = currRiver.indexOf(tile)
                if (targetIndex >= 0) {
                    val start = getDiscardStart(seat)
                    val end = getDiscardSlotPosition(seat, targetIndex)
                    discardQueue.add(QueuedDiscard(seat, tile, targetIndex, start, end))
                }
            }
        }

        // 4. 牌河差异兜底：处理无事件广播、纯快照直更等情况
        for (seat in 0..3) {
            val prevList = lastDiscards.getOrNull(seat) ?: emptyList()
            val currList = currentView.discards.getOrNull(seat) ?: emptyList()

            if (currList.size > prevList.size) {
                for (i in prevList.size until currList.size) {
                    val tile = currList[i]
                    val alreadyQueued = discardQueue.any { it.seat == seat && it.tile == tile }
                    val isCurrentAnim = currentDiscardAnim?.let { it.seat == seat && it.tile == tile } == true
                    val alreadyInLast = prevList.contains(tile)
                    if (alreadyQueued || isCurrentAnim || alreadyInLast) continue

                    val start = getDiscardStart(seat)
                    val end = getDiscardSlotPosition(seat, i)
                    discardQueue.add(QueuedDiscard(seat, tile, i, start, end))
                }
            }
        }
        lastDiscards = (0..3).map { currentView.discards.getOrNull(it)?.toList() ?: emptyList() }

        if (isDiscardPlaybackBusy) {
            clearGameplayHover()
        }
    }

    fun updateDiscardAnimation(delta: Float) {
        if (showSettlement) {
            fastForwardDiscardAnimations()
            return
        }

        while (currentDiscardAnim == null && discardQueue.isNotEmpty()) {
            val next = discardQueue.removeFirst()
            val river = currentView.discards.getOrNull(next.seat) ?: emptyList()
            if (next.tile in river) {
                currentDiscardAnim = next
                discardAnimElapsed = 0f
                break
            }
        }

        val anim = currentDiscardAnim ?: return
        discardAnimElapsed += delta

        if (discardAnimElapsed >= DiscardAnimation.TOTAL_DURATION) {
            currentDiscardAnim = null
            discardAnimElapsed = 0f
            while (discardQueue.isNotEmpty()) {
                val next = discardQueue.removeFirst()
                val river = currentView.discards.getOrNull(next.seat) ?: emptyList()
                if (next.tile in river) {
                    currentDiscardAnim = next
                    break
                }
            }
        }
    }

    fun fastForwardDiscardAnimations() {
        currentDiscardAnim = null
        discardQueue.clear()
        discardAnimElapsed = 0f
    }

    fun computeHandPosition(
        pose: DiscardPose,
        seat: Int,
        start: Pair<Float, Float>,
        end: Pair<Float, Float>,
        tileW: Float,
        tileH: Float,
        handW: Float,
        handH: Float
    ): Pair<Float, Float> {
        val targetX = pose.x + (tileW - handW) / 2f
        val targetY = pose.y + tileH - handH * 0.25f

        val (edgeX, edgeY) = when (seat % 4) {
            0 -> Pair(start.first + (tileW - handW) / 2f, -handH)
            1 -> Pair(1280f, start.second)
            2 -> Pair(start.first + (tileW - handW) / 2f, 720f)
            3 -> Pair(-handW, start.second)
            else -> Pair(start.first, -handH)
        }

        val (retractEdgeX, retractEdgeY) = when (seat % 4) {
            0 -> Pair(end.first + (tileW - handW) / 2f, -handH)
            1 -> Pair(1280f, end.second)
            2 -> Pair(end.first + (tileW - handW) / 2f, 720f)
            3 -> Pair(-handW, end.second)
            else -> Pair(end.first, -handH)
        }

        return when (pose.phase) {
            DiscardPhase.REACH -> {
                val t = (discardAnimElapsed / DiscardAnimation.REACH).coerceIn(0f, 1f)
                Pair(edgeX + (targetX - edgeX) * t, edgeY + (targetY - edgeY) * t)
            }
            DiscardPhase.RETRACT -> {
                val t = ((discardAnimElapsed - (DiscardAnimation.TOTAL_DURATION - DiscardAnimation.RETRACT)) / DiscardAnimation.RETRACT).coerceIn(0f, 1f)
                Pair(targetX + (retractEdgeX - targetX) * t, targetY + (retractEdgeY - targetY) * t)
            }
            else -> Pair(targetX, targetY)
        }
    }

    fun renderDiscardAnimation() {
        val anim = currentDiscardAnim ?: return
        val pose = DiscardAnimation.poseAt(discardAnimElapsed, anim.start, anim.end, anim.seat)
        if (pose.phase == DiscardPhase.DONE) return

        val tileW = 28f
        val tileH = 36f

        // 1. 牌下阴影 (半透明黑矩形，随飞行高度缩放，落地淡出)
        if (pose.phase == DiscardPhase.CARRY || pose.phase == DiscardPhase.PLACE) {
            val shadowAlpha = if (pose.phase == DiscardPhase.CARRY) {
                0.35f
            } else {
                val placeProgress = ((discardAnimElapsed - (DiscardAnimation.REACH + DiscardAnimation.GRAB + DiscardAnimation.CARRY)) / DiscardAnimation.PLACE).coerceIn(0f, 1f)
                0.35f * (1f - placeProgress)
            }
            if (shadowAlpha > 0.01f) {
                val shadowW = tileW * pose.scaleX
                val shadowH = 10f * pose.scaleY
                val shadowX = pose.x + (tileW - shadowW) / 2f
                val shadowY = (pose.y - 6f).coerceAtLeast(anim.end.second - 2f)
                tileDrawBuffer.addFilledRect(shadowX, shadowY, shadowW, shadowH, Color(0f, 0f, 0f, shadowAlpha))
            }
        }

        // 2. 飞行中的牌
        tileSkin.appendFlyingTileDraw(
            buf = tileDrawBuffer,
            tile = anim.tile,
            x = pose.x,
            y = pose.y,
            width = tileW,
            height = tileH,
            scaleX = pose.scaleX,
            scaleY = pose.scaleY,
            rotationDeg = pose.tiltDeg
        )

        // 3. 幽灵手套纹理 (手压在牌之上)
        if (pose.handAlpha > 0.01f) {
            val handW = 56f
            val handH = 68f
            val handPos = computeHandPosition(pose, anim.seat, anim.start, anim.end, tileW, tileH, handW, handH)
            val handTexture = GhostHandTexture.getTexture()
            val seatHandRot = when (anim.seat % 4) {
                0 -> 180f
                1 -> 90f
                2 -> 0f
                3 -> -90f
                else -> 0f
            }
            tileDrawBuffer.addOverlayTexture(
                texture = handTexture,
                x = handPos.first,
                y = handPos.second,
                width = handW,
                height = handH,
                rotationDeg = seatHandRot + pose.tiltDeg,
                originX = handW / 2f,
                originY = handH / 2f,
                tint = Color(1f, 1f, 1f, pose.handAlpha)
            )
        }
    }

    private fun renderDiscardRivers() {
        val anim = currentDiscardAnim
        val pose = if (anim != null) {
            DiscardAnimation.poseAt(discardAnimElapsed, anim.start, anim.end, anim.seat)
        } else null

        // 直接从 PlayerView.discards 渲染四家牌河，避免暗杠/加杠误删牌河
        // P0 牌河（中央偏下，6张一行）
        renderDiscardPond(0, currentView.discards[0], startX = 460f, startY = 280f, cols = 6, yStep = -38f, anim, pose)

        // P2 牌河（中央偏上，6张一行）
        renderDiscardPond(2, currentView.discards[2], startX = 460f, startY = 530f, cols = 6, yStep = 38f, anim, pose)

        // P3 牌河（左侧）
        renderDiscardPond(3, currentView.discards[3], startX = 350f, startY = 440f, cols = 3, yStep = -38f, anim, pose)

        // P1 牌河（右侧）
        renderDiscardPond(1, currentView.discards[1], startX = 750f, startY = 440f, cols = 3, yStep = -38f, anim, pose)
    }

    private fun renderDiscardPond(
        seat: Int,
        tiles: List<Tile>,
        startX: Float,
        startY: Float,
        cols: Int,
        yStep: Float,
        anim: QueuedDiscard?,
        pose: DiscardPose?
    ) {
        val tileW = 28f
        val tileH = 36f
        val visibleDoraTargets = DoraSparkle.computeVisibleTargets(currentView.doraIndicators)
        val alpha = DoraSparkle.sparkleAlpha(stateTime)

        tiles.forEachIndexed { i, tile ->
            // 视图调和：正在播放的动画条目 + 排队中尚未播放的条目，其目标河槽均扣下不画
            val isSuppressed =
                (anim != null && pose != null && DiscardAnimation.shouldSuppressDiscard(
                    pondSeat = seat,
                    tile = tile,
                    tileIndex = i,
                    animatingSeat = anim.seat,
                    animatingTile = anim.tile,
                    animatingIndex = anim.targetIndex,
                    phase = pose.phase
                )) || discardQueue.any { q ->
                    DiscardAnimation.isQueuedRiverTile(
                        pondSeat = seat,
                        tile = tile,
                        tileIndex = i,
                        queuedSeat = q.seat,
                        queuedTile = q.tile,
                        queuedIndex = q.targetIndex
                    )
                }

            if (!isSuppressed) {
                val col = i % cols
                val row = i / cols
                val x = startX + col * (tileW + 3f)
                val y = startY + row * yStep

                tileSkin.appendTileDraw(
                    buf = tileDrawBuffer,
                    tile = tile,
                    x = x,
                    y = y,
                    width = tileW,
                    height = tileH,
                    isHover = false,
                    isDrawn = false
                )

                if (DoraSparkle.isSparkleTarget(tile, visibleDoraTargets)) {
                    DoraSparkle.appendSparkle(
                        buf = tileDrawBuffer,
                        x = x,
                        y = y,
                        width = tileW,
                        height = tileH,
                        alpha = alpha,
                        hasInteractionHighlight = false
                    )
                }
            }
        }
    }

    /**
     * 获取副露选牌模式下当前应在手牌高亮参与的物理手牌列表。
     * - 若鼠标正悬停在某张候选卡片上，优先使用该卡片的 handTiles（悬停临时高亮）；
     * - 否则使用当前已选中的副露方案 handTiles；
     * - 未选择且未悬停时返回空列表；加杠仅包含补入的 1 张牌。
     */
    fun currentMeldHandTiles(): List<Tile> {
        if (!meldSelectionModel.isSelecting) return emptyList()
        val hoveredChoice = if (hoveredMeldCardIndex in meldSelectionModel.visibleOptions.indices) {
            meldSelectionModel.visibleOptions[hoveredMeldCardIndex]
        } else null
        if (hoveredChoice != null) {
            return hoveredChoice.handTiles
        }
        val selectedAction = meldSelectionModel.selectedAction
        if (selectedAction != null) {
            val selectedChoice = meldSelectionModel.options.firstOrNull { it.action == selectedAction }
            if (selectedChoice != null) {
                return selectedChoice.handTiles
            }
        }
        return emptyList()
    }

    /**
     * 获取当前观察者本人的已完成副露展示列表（纯视图驱动，不读取他家暗牌）。
     */
    fun displayedMelds(): List<MeldDisplayGroup> = MeldDisplayLayout.layout(currentView)

    private fun computeHandSlots(): List<HandTileSlot> {
        if (!gameStarted) return emptyList()
        val closed = currentView.myClosedTiles
        val drawn = currentView.myDrawnTile

        val tileW = 54f
        val tileH = 80f
        val xSpacing = 58f
        val startX = 220f
        val baseY = 40f

        val result = ArrayList<HandTileSlot>()
        val meldHandTiles = if (meldSelectionModel.isSelecting && !isDiscardPlaybackBusy) currentMeldHandTiles() else emptyList()

        if (isAutoSort) {
            val arranged = TileArranger.sortForDisplay(closed, drawn)
            val displayTiles = arranged.tiles
            val drawnIndex = arranged.drawnTileIndex

            for (i in displayTiles.indices) {
                val tile = displayTiles[i]
                val isSelected = (!isDiscardPlaybackBusy && riichiSelectionModel.isSelecting && riichiSelectionModel.selectedTile == tile) ||
                        (!isDiscardPlaybackBusy && meldSelectionModel.isSelecting && meldHandTiles.contains(tile))
                val isHover = (!isDiscardPlaybackBusy && (i == hoveredTileIndex || (hoveredTileIndex == -1 && hoveredTile == tile)))
                val x = startX + i * xSpacing
                val y = if (isSelected || isHover) baseY + 12f else baseY
                result.add(HandTileSlot(Rectangle(x, y, tileW, tileH), tile, isDrawn = (i == drawnIndex)))
            }
        } else {
            for (i in closed.indices) {
                val tile = closed[i]
                val isSelected = (!isDiscardPlaybackBusy && riichiSelectionModel.isSelecting && riichiSelectionModel.selectedTile == tile) ||
                        (!isDiscardPlaybackBusy && meldSelectionModel.isSelecting && meldHandTiles.contains(tile))
                val isHover = (!isDiscardPlaybackBusy && (i == hoveredTileIndex || (hoveredTileIndex == -1 && hoveredTile == tile)))
                val x = startX + i * xSpacing
                val y = if (isSelected || isHover) baseY + 12f else baseY
                result.add(HandTileSlot(Rectangle(x, y, tileW, tileH), tile, isDrawn = false))
            }

            if (drawn != null) {
                val drawnIdx = closed.size
                val isSelected = (!isDiscardPlaybackBusy && riichiSelectionModel.isSelecting && riichiSelectionModel.selectedTile == drawn) ||
                        (!isDiscardPlaybackBusy && meldSelectionModel.isSelecting && meldHandTiles.contains(drawn))
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

    private fun renderPlayerHands(batch: SpriteBatch, font: BitmapFont) {
        if (!gameStarted) return

        val slots = refreshHandTileRects()
        val visibleDoraTargets = DoraSparkle.computeVisibleTargets(currentView.doraIndicators)
        val alpha = DoraSparkle.sparkleAlpha(stateTime)

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

            if (DoraSparkle.isSparkleTarget(slot.tile, visibleDoraTargets)) {
                val hasInteraction = isHover || slot.isDrawn
                DoraSparkle.appendSparkle(
                    buf = tileDrawBuffer,
                    x = slot.rect.x,
                    y = slot.rect.y,
                    width = slot.rect.width,
                    height = slot.rect.height,
                    alpha = alpha,
                    hasInteractionHighlight = hasInteraction
                )
            }
        }

        val drawnSlot = slots.firstOrNull { it.isDrawn }
        if (drawnSlot != null) {
            val isSelectedInRiichi = !isDiscardPlaybackBusy && riichiSelectionModel.isSelecting && riichiSelectionModel.selectedTile == drawnSlot.tile
            val isSelectedInMeld = !isDiscardPlaybackBusy && meldSelectionModel.isSelecting && currentMeldHandTiles().contains(drawnSlot.tile)
            if (!isSelectedInRiichi && !isSelectedInMeld) {
                batch.projectionMatrix = camera.combined
                batch.begin()
                font.data.setScale(0.9f)
                font.color = Color(1.0f, 0.85f, 0.2f, 1f)
                font.draw(batch, "[DRAW]", drawnSlot.rect.x + 2f, drawnSlot.rect.y + drawnSlot.rect.height + 18f)
                batch.end()
            }
        }
    }

    private fun renderRiichiSelectionOverlay(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val slots = refreshHandTileRects()
        val legalTiles = riichiSelectionModel.options.map { it.action.discardTile }

        // 1. 非法手牌半透明暗化
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

        // 2. 合法弃牌金色轮廓；已选中的牌特殊高亮标记
        shape.begin(ShapeRenderer.ShapeType.Line)
        for (slot in slots) {
            if (slot.tile in legalTiles) {
                val isSelected = (slot.tile == riichiSelectionModel.selectedTile)
                if (isSelected) {
                    shape.color = Color(0.2f, 1.0f, 0.85f, 1f)
                    shape.rect(slot.rect.x - 2f, slot.rect.y - 2f, slot.rect.width + 4f, slot.rect.height + 4f)
                    shape.rect(slot.rect.x - 1f, slot.rect.y - 1f, slot.rect.width + 2f, slot.rect.height + 2f)
                } else {
                    shape.color = Color(1.0f, 0.84f, 0.0f, 1f)
                    shape.rect(slot.rect.x, slot.rect.y, slot.rect.width, slot.rect.height)
                    shape.rect(slot.rect.x + 1f, slot.rect.y + 1f, slot.rect.width - 2f, slot.rect.height - 2f)
                }
            }
        }
        shape.end()

        // 3. 选中牌上方标语 [SELECTED]
        val selectedSlot = slots.firstOrNull { it.tile == riichiSelectionModel.selectedTile }
        if (selectedSlot != null) {
            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(0.85f)
            font.color = Color(0.2f, 1.0f, 0.85f, 1f)
            font.draw(batch, "[SELECTED]", selectedSlot.rect.x - 4f, selectedSlot.rect.y + selectedSlot.rect.height + 18f)
            batch.end()
        }
    }

    private fun renderWaitsPreviewPanel(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        // 悬停合法弃牌可临时预览，移开恢复已选方案；悬停不能修改选中项或提交
        val previewTile = if (hoveredTile != null && riichiSelectionModel.options.any { it.action.discardTile == hoveredTile }) {
            hoveredTile
        } else {
            riichiSelectionModel.selectedTile
        }
        val option = previewTile?.let { riichiSelectionModel.previewFor(it) }

        // 1. 面板背景与边框
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
        shape.rect(waitsPanelRect.x + 1f, waitsPanelRect.y + 1f, waitsPanelRect.width - 2f, waitsPanelRect.height - 2f)

        if (option != null) {
            // 分隔线
            shape.color = Color(0.40f, 0.55f, 0.45f, 0.7f)
            shape.line(360f, waitsPanelRect.y + 15f, 360f, waitsPanelRect.y + waitsPanelRect.height - 15f)
        }
        shape.end()

        if (option == null) {
            // 无选中时显示选择提示
            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(1.3f)
            font.color = Color(1.0f, 0.85f, 0.3f, 1f)
            font.draw(batch, "RIICHI: SELECT A DISCARD TILE", waitsPanelRect.x + 40f, waitsPanelRect.y + 130f)

            font.data.setScale(1.0f)
            font.color = Color(0.85f, 0.85f, 0.85f, 1f)
            font.draw(batch, "Click any golden-bordered tile in your hand to choose your Riichi discard.", waitsPanelRect.x + 40f, waitsPanelRect.y + 85f)
            font.draw(batch, "Hover over a tile to preview waits. Click CONFIRM RIICHI to declare.", waitsPanelRect.x + 40f, waitsPanelRect.y + 55f)
            batch.end()
        } else {
            // 左侧弃牌，右侧待牌
            val isHoverPreview = (previewTile != riichiSelectionModel.selectedTile)
            batch.projectionMatrix = camera.combined
            batch.begin()
            val panelPrevScaleX = font.data.scaleX
            val panelPrevScaleY = font.data.scaleY
            val panelPrevColor = Color(font.color)
            try {
                font.data.setScale(1.0f)
                font.color = Color(1.0f, 0.85f, 0.3f, 1f)
                font.draw(batch, "DISCARD", waitsPanelRect.x + 20f, waitsPanelRect.y + 165f)

                val waitCount = option.waits.size
                val waitTitle = if (isHoverPreview) "PREVIEW WAITS ($waitCount):" else "WINNING WAITS ($waitCount):"
                font.color = if (isHoverPreview) Color(0.4f, 0.9f, 1.0f, 1f) else Color(1.0f, 0.85f, 0.3f, 1f)
                font.draw(batch, waitTitle, 380f, waitsPanelRect.y + 165f)

                // 方案振听警示（该弃牌方案会振听）
                val optionFuriten = option.furiten
                if (optionFuriten.isFuriten) {
                    val statusTitle = PREVIEW_FURITEN_TITLE
                    font.data.setScale(0.85f, 0.85f)
                    val titleWidth = measureTextWidth(font, statusTitle)
                    val maxTitleWidth = 1010f - 580f
                    if (titleWidth > maxTitleWidth && maxTitleWidth > 0f) {
                        val titleScale = (0.85f * (maxTitleWidth / titleWidth)).coerceIn(0.5f, 0.85f)
                        font.data.setScale(titleScale, titleScale)
                    }
                    font.color = Color(1.0f, 0.35f, 0.25f, 1f)
                    font.draw(batch, statusTitle, 580f, waitsPanelRect.y + 165f)

                    // Cause details in right-side unused area x740..1010 with wrapping/measurement
                    font.data.setScale(0.85f, 0.85f)
                    font.color = Color(1.0f, 0.40f, 0.30f, 1f)
                    val causesText = previewFuritenCausesText(optionFuriten)
                    val lines = wrapText(font, causesText, maxWidth = 270f)
                    var lineY = waitsPanelRect.y + 135f
                    for (line in lines) {
                        font.draw(batch, line, 740f, lineY)
                        lineY -= 20f
                    }
                }
            } finally {
                font.data.setScale(panelPrevScaleX, panelPrevScaleY)
                font.color = panelPrevColor
            }
            batch.end()

            // 收集面板牌面
            tileDrawBuffer.clear()
            // 左侧弃牌牌面
            val discardTile = option.action.discardTile
            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = discardTile,
                x = waitsPanelRect.x + 35f,
                y = waitsPanelRect.y + 45f,
                width = 50f,
                height = 72f,
                isHover = false,
                isDrawn = false
            )

            // 右侧待牌列表（支持最多13种不裁剪，两行自适应布局）
            val waits = option.waits
            if (waits.size <= 7) {
                val waitW = 44f
                val waitH = 64f
                val spacingX = 52f
                val startX = 380f
                val startY = waitsPanelRect.y + 50f
                for (i in waits.indices) {
                    tileSkin.appendTileDraw(
                        buf = tileDrawBuffer,
                        tile = waits[i],
                        x = startX + i * spacingX,
                        y = startY,
                        width = waitW,
                        height = waitH,
                        isHover = false,
                        isDrawn = false
                    )
                }
            } else {
                val waitW = 40f
                val waitH = 58f
                val spacingX = 48f
                val startX = 380f
                val row0Y = waitsPanelRect.y + 88f
                val row1Y = waitsPanelRect.y + 20f
                for (i in waits.indices) {
                    val row = i / 7
                    val col = i % 7
                    val wx = startX + col * spacingX
                    val wy = if (row == 0) row0Y else row1Y
                    tileSkin.appendTileDraw(
                        buf = tileDrawBuffer,
                        tile = waits[i],
                        x = wx,
                        y = wy,
                        width = waitW,
                        height = waitH,
                        isHover = false,
                        isDrawn = false
                    )
                }
            }

            // flush 面板内牌面
            shape.projectionMatrix = camera.combined
            batch.projectionMatrix = camera.combined
            tileDrawBuffer.flush(batch, shape, font)

            // 若该方案会振听，在左侧弃牌下方标记 [FURITEN]
            if (option.furiten.isFuriten) {
                batch.projectionMatrix = camera.combined
                batch.begin()
                val prevScaleX = font.data.scaleX
                val prevScaleY = font.data.scaleY
                val prevColor = Color(font.color)
                try {
                    font.data.setScale(0.85f)
                    font.color = Color(1.0f, 0.40f, 0.30f, 1f)
                    font.draw(batch, "[FURITEN]", waitsPanelRect.x + 28f, waitsPanelRect.y + 30f)
                } finally {
                    font.data.setScale(prevScaleX, prevScaleY)
                    font.color = prevColor
                }
                batch.end()
            }
        }
    }

    private fun renderMeldSelectionOverlay(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val slots = refreshHandTileRects()
        val meldHandTiles = currentMeldHandTiles()

        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)

        // 1. 若有参与手牌：未参与的手牌半透明暗化
        if (meldHandTiles.isNotEmpty()) {
            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0f, 0f, 0f, 0.58f)
            for (slot in slots) {
                if (slot.tile !in meldHandTiles) {
                    shape.rect(slot.rect.x, slot.rect.y, slot.rect.width, slot.rect.height)
                }
            }
            shape.end()
        }

        // 2. 参与的手牌加高亮轮廓线
        shape.begin(ShapeRenderer.ShapeType.Line)
        for (slot in slots) {
            if (slot.tile in meldHandTiles) {
                shape.color = Color(0.2f, 1.0f, 0.85f, 1f)
                shape.rect(slot.rect.x - 2f, slot.rect.y - 2f, slot.rect.width + 4f, slot.rect.height + 4f)
                shape.rect(slot.rect.x - 1f, slot.rect.y - 1f, slot.rect.width + 2f, slot.rect.height + 2f)
            }
        }
        shape.end()
    }

    private fun renderMeldChoicePanel(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val visibleOptions = meldSelectionModel.visibleOptions

        // 1. 面板背景与卡片填充
        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 面板主背景
        shape.color = Color(0.04f, 0.12f, 0.08f, 0.94f)
        shape.rect(meldPanelRect.x, meldPanelRect.y, meldPanelRect.width, meldPanelRect.height)

        // 卡片背景
        for (i in visibleOptions.indices) {
            val choice = visibleOptions[i]
            val cardRect = meldCardRects[i]
            val isSelected = (meldSelectionModel.selectedAction == choice.action)
            val isHover = (hoveredMeldCardIndex == i)

            shape.color = when {
                isSelected -> Color(0.10f, 0.28f, 0.22f, 0.95f)
                isHover -> Color(0.08f, 0.20f, 0.16f, 0.95f)
                else -> Color(0.05f, 0.15f, 0.10f, 0.90f)
            }
            shape.rect(cardRect.x, cardRect.y, cardRect.width, cardRect.height)
        }

        // 翻页按钮背景
        val canPrev = meldSelectionModel.pageIndex > 0
        val canNext = meldSelectionModel.pageIndex < meldSelectionModel.pageCount - 1
        shape.color = if (canPrev) Color(0.20f, 0.50f, 0.75f, 1f) else Color(0.20f, 0.20f, 0.20f, 0.6f)
        shape.rect(btnMeldPrevRect.x, btnMeldPrevRect.y, btnMeldPrevRect.width, btnMeldPrevRect.height)
        shape.color = if (canNext) Color(0.20f, 0.50f, 0.75f, 1f) else Color(0.20f, 0.20f, 0.20f, 0.6f)
        shape.rect(btnMeldNextRect.x, btnMeldNextRect.y, btnMeldNextRect.width, btnMeldNextRect.height)

        shape.end()

        // 2. 边框线条绘制
        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.85f, 0.72f, 0.35f, 0.95f)
        shape.rect(meldPanelRect.x, meldPanelRect.y, meldPanelRect.width, meldPanelRect.height)
        shape.rect(meldPanelRect.x + 1f, meldPanelRect.y + 1f, meldPanelRect.width - 2f, meldPanelRect.height - 2f)

        shape.color = if (canPrev) Color.WHITE else Color(0.4f, 0.4f, 0.4f, 0.6f)
        shape.rect(btnMeldPrevRect.x, btnMeldPrevRect.y, btnMeldPrevRect.width, btnMeldPrevRect.height)
        shape.color = if (canNext) Color.WHITE else Color(0.4f, 0.4f, 0.4f, 0.6f)
        shape.rect(btnMeldNextRect.x, btnMeldNextRect.y, btnMeldNextRect.width, btnMeldNextRect.height)

        for (i in visibleOptions.indices) {
            val choice = visibleOptions[i]
            val cardRect = meldCardRects[i]
            val isSelected = (meldSelectionModel.selectedAction == choice.action)
            val isHover = (hoveredMeldCardIndex == i)

            when {
                isSelected -> {
                    shape.color = Color(0.2f, 1.0f, 0.85f, 1f)
                    shape.rect(cardRect.x - 2f, cardRect.y - 2f, cardRect.width + 4f, cardRect.height + 4f)
                    shape.rect(cardRect.x - 1f, cardRect.y - 1f, cardRect.width + 2f, cardRect.height + 2f)
                    shape.rect(cardRect.x, cardRect.y, cardRect.width, cardRect.height)
                }
                isHover -> {
                    shape.color = Color(0.60f, 0.85f, 0.70f, 1f)
                    shape.rect(cardRect.x, cardRect.y, cardRect.width, cardRect.height)
                }
                else -> {
                    shape.color = Color(0.40f, 0.55f, 0.45f, 0.7f)
                    shape.rect(cardRect.x, cardRect.y, cardRect.width, cardRect.height)
                }
            }
        }
        shape.end()

        // 3. 绘制文字与信息
        batch.projectionMatrix = camera.combined
        batch.begin()
        val prevScaleX = font.data.scaleX
        val prevScaleY = font.data.scaleY
        val prevColor = Color(font.color)

        try {
            font.data.setScale(1.2f)
            font.color = Color(1.0f, 0.85f, 0.3f, 1f)
            val panelTitle = when (meldSelectionModel.kind) {
                MeldChoiceKind.CHI -> "CHOOSE CHI COMBINATION"
                MeldChoiceKind.PON -> "CHOOSE PON COMBINATION"
                MeldChoiceKind.KAN -> "CHOOSE KAN COMBINATION"
                null -> "CHOOSE MELD COMBINATION"
            }
            font.draw(batch, panelTitle, meldPanelRect.x + 25f, meldPanelRect.y + meldPanelRect.height - 15f)

            font.data.setScale(1.0f)
            font.color = Color.WHITE
            val pageText = "PAGE ${meldSelectionModel.pageIndex + 1} / ${maxOf(1, meldSelectionModel.pageCount)}"
            val pageTextWidth = measureTextWidth(font, pageText)
            font.draw(batch, pageText, 630f - pageTextWidth / 2f, btnMeldPrevRect.y + 24f)

            font.data.setScale(0.95f)
            font.color = if (canPrev) Color.WHITE else Color(0.6f, 0.6f, 0.6f, 0.6f)
            font.draw(batch, "< PREV", btnMeldPrevRect.x + 14f, btnMeldPrevRect.y + 24f)
            font.color = if (canNext) Color.WHITE else Color(0.6f, 0.6f, 0.6f, 0.6f)
            font.draw(batch, "NEXT >", btnMeldNextRect.x + 16f, btnMeldNextRect.y + 24f)

            for (i in visibleOptions.indices) {
                val choice = visibleOptions[i]
                val cardRect = meldCardRects[i]
                val isSelected = (meldSelectionModel.selectedAction == choice.action)

                font.data.setScale(1.05f)
                font.color = Color(1.0f, 0.85f, 0.3f, 1f)
                val typeLabel = when (choice.type) {
                    MeldType.CHI -> "CHI"
                    MeldType.PON -> "PON"
                    MeldType.OPEN_KAN -> "OPEN KAN"
                    MeldType.CLOSED_KAN -> "CLOSED KAN"
                    MeldType.ADDED_KAN -> "ADDED KAN"
                }
                font.draw(batch, typeLabel, cardRect.x + 14f, cardRect.y + cardRect.height - 16f)

                if (isSelected) {
                    font.data.setScale(0.9f)
                    font.color = Color(0.2f, 1.0f, 0.85f, 1f)
                    font.draw(batch, "[SELECTED]", cardRect.x + cardRect.width - 92f, cardRect.y + cardRect.height - 16f)
                }

                font.data.setScale(0.85f)
                if (choice.fromPlayer != null) {
                    font.color = Color(0.85f, 0.85f, 0.85f, 1f)
                    font.draw(batch, "FROM P${choice.fromPlayer}", cardRect.x + 14f, cardRect.y + cardRect.height - 38f)
                } else if (choice.type == MeldType.CLOSED_KAN) {
                    font.color = Color(0.70f, 0.80f, 0.90f, 1f)
                    font.draw(batch, "CONCEALED", cardRect.x + 14f, cardRect.y + cardRect.height - 38f)
                }
            }
        } finally {
            font.data.setScale(prevScaleX, prevScaleY)
            font.color = prevColor
        }
        batch.end()

        // 4. 收集并提交每张卡片的牌面
        tileDrawBuffer.clear()
        for (i in visibleOptions.indices) {
            val choice = visibleOptions[i]
            val cardRect = meldCardRects[i]
            val tiles = choice.tiles
            val tileCount = tiles.size

            val tileW = 42f
            val tileH = 60f
            val spacingX = if (tileCount == 4) 48f else 54f
            val totalTilesWidth = (tileCount - 1) * spacingX + tileW
            val startTileX = cardRect.x + (cardRect.width - totalTilesWidth) / 2f
            val tileY = cardRect.y + 40f

            for (tIdx in tiles.indices) {
                val tile = tiles[tIdx]
                val tileX = startTileX + tIdx * spacingX
                tileSkin.appendTileDraw(
                    buf = tileDrawBuffer,
                    tile = tile,
                    x = tileX,
                    y = tileY,
                    width = tileW,
                    height = tileH,
                    isHover = false,
                    isDrawn = false
                )
            }
        }

        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, font)

        // 5. 绘制 CALLED / ADD 边框
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Line)
        for (i in visibleOptions.indices) {
            val choice = visibleOptions[i]
            val cardRect = meldCardRects[i]
            val tiles = choice.tiles
            val tileCount = tiles.size
            val tileW = 42f
            val tileH = 60f
            val spacingX = if (tileCount == 4) 48f else 54f
            val totalTilesWidth = (tileCount - 1) * spacingX + tileW
            val startTileX = cardRect.x + (cardRect.width - totalTilesWidth) / 2f
            val tileY = cardRect.y + 40f

            for (tIdx in tiles.indices) {
                val tile = tiles[tIdx]
                val tileX = startTileX + tIdx * spacingX

                if (choice.calledTile != null && tile == choice.calledTile) {
                    shape.color = Color(1.0f, 0.45f, 0.15f, 1f)
                    shape.rect(tileX - 1f, tileY - 1f, tileW + 2f, tileH + 2f)
                    shape.rect(tileX, tileY, tileW, tileH)
                } else if (choice.addedTile != null && tile == choice.addedTile) {
                    shape.color = Color(0.2f, 0.85f, 1.0f, 1f)
                    shape.rect(tileX - 1f, tileY - 1f, tileW + 2f, tileH + 2f)
                    shape.rect(tileX, tileY, tileW, tileH)
                }
            }
        }
        shape.end()

        // 6. 绘制 CALLED / ADD 标签
        batch.projectionMatrix = camera.combined
        batch.begin()
        val tagPrevScaleX = font.data.scaleX
        val tagPrevScaleY = font.data.scaleY
        val tagPrevColor = Color(font.color)

        try {
            font.data.setScale(0.70f)
            for (i in visibleOptions.indices) {
                val choice = visibleOptions[i]
                val cardRect = meldCardRects[i]
                val tiles = choice.tiles
                val tileCount = tiles.size
                val tileW = 42f
                val spacingX = if (tileCount == 4) 48f else 54f
                val totalTilesWidth = (tileCount - 1) * spacingX + tileW
                val startTileX = cardRect.x + (cardRect.width - totalTilesWidth) / 2f
                val tileY = cardRect.y + 40f

                for (tIdx in tiles.indices) {
                    val tile = tiles[tIdx]
                    val tileX = startTileX + tIdx * spacingX

                    if (choice.calledTile != null && tile == choice.calledTile) {
                        font.color = Color(1.0f, 0.50f, 0.20f, 1f)
                        val w = measureTextWidth(font, "CALLED")
                        font.draw(batch, "CALLED", tileX + (tileW - w) / 2f, tileY - 6f)
                    } else if (choice.addedTile != null && tile == choice.addedTile) {
                        font.color = Color(0.2f, 0.85f, 1.0f, 1f)
                        val w = measureTextWidth(font, "ADD")
                        font.draw(batch, "ADD", tileX + (tileW - w) / 2f, tileY - 6f)
                    }
                }
            }
        } finally {
            font.data.setScale(tagPrevScaleX, tagPrevScaleY)
            font.color = tagPrevColor
        }
        batch.end()
    }

    private fun renderMeldActionButtons(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val hasSelection = meldSelectionModel.selectedAction != null

        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 1. CONFIRM 按钮 (Rectangle(670f, 145f, 140f, 44f))
        shape.color = if (hasSelection) Color(0.85f, 0.45f, 0.15f, 1f) else Color(0.30f, 0.30f, 0.30f, 0.7f)
        shape.rect(btnMeldConfirmRect.x, btnMeldConfirmRect.y, btnMeldConfirmRect.width, btnMeldConfirmRect.height)

        // 2. CANCEL 按钮 (Rectangle(1030f, 145f, 170f, 44f))
        shape.color = Color(0.45f, 0.20f, 0.20f, 1f)
        shape.rect(btnMeldCancelRect.x, btnMeldCancelRect.y, btnMeldCancelRect.width, btnMeldCancelRect.height)

        // 3. WIN 快捷按钮 (Rectangle(490f, 145f, 160f, 44f))
        val canRon = canHumanRon()
        val canTsumo = canHumanTsumo()
        if (canRon || canTsumo) {
            shape.color = if (canRon) Color(0.88f, 0.18f, 0.15f, 1f) else Color(0.90f, 0.65f, 0.15f, 1f)
            shape.rect(btnMeldWinRect.x, btnMeldWinRect.y, btnMeldWinRect.width, btnMeldWinRect.height)
        }

        // 4. PASS 按钮 (Rectangle(840f, 145f, 150f, 44f))
        val canPass = canHumanPass()
        if (canPass) {
            shape.color = Color(0.35f, 0.40f, 0.45f, 1f)
            shape.rect(btnMeldPassRect.x, btnMeldPassRect.y, btnMeldPassRect.width, btnMeldPassRect.height)
        }

        shape.end()

        // 边框绘制
        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = if (hasSelection) Color.WHITE else Color(0.55f, 0.55f, 0.55f, 0.7f)
        shape.rect(btnMeldConfirmRect.x, btnMeldConfirmRect.y, btnMeldConfirmRect.width, btnMeldConfirmRect.height)

        shape.color = Color.WHITE
        shape.rect(btnMeldCancelRect.x, btnMeldCancelRect.y, btnMeldCancelRect.width, btnMeldCancelRect.height)

        if (canRon || canTsumo) {
            shape.color = if (canRon) Color.YELLOW else Color.WHITE
            shape.rect(btnMeldWinRect.x, btnMeldWinRect.y, btnMeldWinRect.width, btnMeldWinRect.height)
        }

        if (canPass) {
            shape.color = Color.LIGHT_GRAY
            shape.rect(btnMeldPassRect.x, btnMeldPassRect.y, btnMeldPassRect.width, btnMeldPassRect.height)
        }
        shape.end()

        // 按钮文字
        batch.projectionMatrix = camera.combined
        batch.begin()
        val prevScaleX = font.data.scaleX
        val prevScaleY = font.data.scaleY
        val prevColor = Color(font.color)

        try {
            font.data.setScale(1.1f)

            font.color = if (hasSelection) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
            font.draw(batch, "CONFIRM", btnMeldConfirmRect.x + 28f, btnMeldConfirmRect.y + 29f)

            font.color = Color.WHITE
            font.draw(batch, "CANCEL", btnMeldCancelRect.x + 48f, btnMeldCancelRect.y + 29f)

            if (canRon) {
                font.draw(batch, "RON (WIN)", btnMeldWinRect.x + 24f, btnMeldWinRect.y + 29f)
            } else if (canTsumo) {
                font.draw(batch, "TSUMO (WIN)", btnMeldWinRect.x + 14f, btnMeldWinRect.y + 29f)
            }

            if (canPass) {
                font.draw(batch, "PASS", btnMeldPassRect.x + 50f, btnMeldPassRect.y + 29f)
            }
        } finally {
            font.data.setScale(prevScaleX, prevScaleY)
            font.color = prevColor
        }
        batch.end()
    }

    private fun renderActionButtons(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        // 1. 若对局未开始，绘制「START GAME」居中按钮
        if (!gameStarted) {
            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.85f, 0.25f, 0.18f, 1f)
            shape.rect(btnStartGameRect.x, btnStartGameRect.y, btnStartGameRect.width, btnStartGameRect.height)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color.YELLOW
            shape.rect(btnStartGameRect.x, btnStartGameRect.y, btnStartGameRect.width, btnStartGameRect.height)
            shape.end()

            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(1.6f)
            font.color = Color.WHITE
            font.draw(batch, "START GAME", btnStartGameRect.x + 48f, btnStartGameRect.y + 40f)
            batch.end()
            return
        }

        // 2. 理牌开关按钮 (SORT: ON / SORT: OFF)
        renderSortButton(shape, batch, font)

        // 2.5 打牌演出繁忙时：不显示可用出牌/鸣牌操作，显示简短可读的等待提示
        if (isDiscardPlaybackBusy) {
            renderWaitingForDiscardsPrompt(shape, batch, font)
            return
        }

        // 3. 若处于立直选牌模式：仅展示 CONFIRM RIICHI 和 CANCEL 按钮
        if (riichiSelectionModel.isSelecting) {
            val hasSelection = riichiSelectionModel.selectedTile != null

            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)

            // CONFIRM RIICHI 按钮 (x670..810, y145..189)
            if (hasSelection) {
                shape.color = Color(0.85f, 0.45f, 0.15f, 1f)
            } else {
                shape.color = Color(0.30f, 0.30f, 0.30f, 0.7f)
            }
            shape.rect(btnRiichiRect.x, btnRiichiRect.y, btnRiichiRect.width, btnRiichiRect.height)

            // CANCEL 按钮 (x1030..1200, y145..189)
            shape.color = Color(0.45f, 0.20f, 0.20f, 1f)
            shape.rect(btnCancelRiichiRect.x, btnCancelRiichiRect.y, btnCancelRiichiRect.width, btnCancelRiichiRect.height)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            if (hasSelection) {
                shape.color = Color.WHITE
            } else {
                shape.color = Color(0.55f, 0.55f, 0.55f, 0.7f)
            }
            shape.rect(btnRiichiRect.x, btnRiichiRect.y, btnRiichiRect.width, btnRiichiRect.height)

            shape.color = Color.WHITE
            shape.rect(btnCancelRiichiRect.x, btnCancelRiichiRect.y, btnCancelRiichiRect.width, btnCancelRiichiRect.height)
            shape.end()

            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(1.0f)
            font.color = if (hasSelection) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
            font.draw(batch, "CONFIRM RIICHI", btnRiichiRect.x + 8f, btnRiichiRect.y + 28f)

            font.data.setScale(1.2f)
            font.color = Color.WHITE
            font.draw(batch, "CANCEL", btnCancelRiichiRect.x + 48f, btnCancelRiichiRect.y + 29f)
            batch.end()
            return
        }

        // 3.5 若处于副露选牌模式：仅展示副露确认、取消、WIN 与 PASS 快捷按钮（原动作按钮不重叠绘制）
        if (meldSelectionModel.isSelecting) {
            renderMeldActionButtons(shape, batch, font)
            return
        }

        // 提示信息：真人立直后暂停时的操作说明
        val isViewerRiichi = currentView.riichiStatus.getOrNull(endpoint.viewerSeat) == true
        if (isViewerRiichi && (canHumanTsumo() || canHumanKan())) {
            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(1.0f)
            font.color = Color(1.0f, 0.85f, 0.3f, 1f)
            font.draw(batch, "RIICHI: TSUMO / KAN OR CLICK DRAW TILE TO PASS", 420f, 135f)
            batch.end()
        }

        // 4. 普通回合：自摸与立直按钮判定
        if (canHumanTsumo()) {
            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.90f, 0.65f, 0.15f, 1f)
            shape.rect(btnTsumoRect.x, btnTsumoRect.y, btnTsumoRect.width, btnTsumoRect.height)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color.WHITE
            shape.rect(btnTsumoRect.x, btnTsumoRect.y, btnTsumoRect.width, btnTsumoRect.height)
            shape.end()

            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(1.2f)
            font.color = Color.WHITE
            font.draw(batch, "TSUMO (WIN)", btnTsumoRect.x + 12f, btnTsumoRect.y + 29f)
            batch.end()
        }

        if (canHumanRiichi()) {
            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.85f, 0.45f, 0.15f, 1f)
            shape.rect(btnRiichiRect.x, btnRiichiRect.y, btnRiichiRect.width, btnRiichiRect.height)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color.WHITE
            shape.rect(btnRiichiRect.x, btnRiichiRect.y, btnRiichiRect.width, btnRiichiRect.height)
            shape.end()

            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(1.2f)
            font.color = Color.WHITE
            font.draw(batch, "RIICHI", btnRiichiRect.x + 36f, btnRiichiRect.y + 29f)
            batch.end()
        }

        val canAbortiveDraw = canHumanAbortiveDraw()
        if (canAbortiveDraw) {
            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.60f, 0.25f, 0.45f, 1f)
            shape.rect(btnAbortiveDrawRect.x, btnAbortiveDrawRect.y, btnAbortiveDrawRect.width, btnAbortiveDrawRect.height)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color.WHITE
            shape.rect(btnAbortiveDrawRect.x, btnAbortiveDrawRect.y, btnAbortiveDrawRect.width, btnAbortiveDrawRect.height)
            shape.end()

            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(1.2f)
            font.color = Color.WHITE
            font.draw(batch, "9 TERMINALS", btnAbortiveDrawRect.x + 14f, btnAbortiveDrawRect.y + 29f)
            batch.end()
        }

        // 5. 鸣牌与响应窗口按钮判定 (Ron, Pass, Pon, Chi, Kan)
        val canRon = canHumanRon()
        val canPass = canHumanPass()
        val canPon = canHumanPon()
        val canChi = canHumanChi()
        val canKan = canHumanKan()

        if (canRon || canPass || canPon || canChi || canKan) {
            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)

            if (canRon) {
                shape.color = Color(0.88f, 0.18f, 0.15f, 1f)
                shape.rect(btnRonRect.x, btnRonRect.y, btnRonRect.width, btnRonRect.height)
            }
            if (canPass) {
                shape.color = Color(0.35f, 0.40f, 0.45f, 1f)
                shape.rect(btnPassRect.x, btnPassRect.y, btnPassRect.width, btnPassRect.height)
            }
            if (canPon) {
                shape.color = Color(0.20f, 0.55f, 0.85f, 1f)
                shape.rect(btnPonRect.x, btnPonRect.y, btnPonRect.width, btnPonRect.height)
            }
            if (canChi) {
                shape.color = Color(0.25f, 0.65f, 0.35f, 1f)
                shape.rect(btnChiRect.x, btnChiRect.y, btnChiRect.width, btnChiRect.height)
            }
            if (canKan) {
                shape.color = Color(0.70f, 0.35f, 0.80f, 1f)
                shape.rect(btnKanRect.x, btnKanRect.y, btnKanRect.width, btnKanRect.height)
            }

            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            if (canRon) {
                shape.color = Color.YELLOW
                shape.rect(btnRonRect.x, btnRonRect.y, btnRonRect.width, btnRonRect.height)
            }
            if (canPass) {
                shape.color = Color.LIGHT_GRAY
                shape.rect(btnPassRect.x, btnPassRect.y, btnPassRect.width, btnPassRect.height)
            }
            if (canPon) {
                shape.color = Color.WHITE
                shape.rect(btnPonRect.x, btnPonRect.y, btnPonRect.width, btnPonRect.height)
            }
            if (canChi) {
                shape.color = Color.WHITE
                shape.rect(btnChiRect.x, btnChiRect.y, btnChiRect.width, btnChiRect.height)
            }
            if (canKan) {
                shape.color = Color.WHITE
                shape.rect(btnKanRect.x, btnKanRect.y, btnKanRect.width, btnKanRect.height)
            }
            shape.end()

            batch.projectionMatrix = camera.combined
            batch.begin()
            font.data.setScale(1.2f)
            font.color = Color.WHITE
            if (canRon) font.draw(batch, "RON (WIN)", btnRonRect.x + 16f, btnRonRect.y + 29f)
            if (canPass) font.draw(batch, "PASS", btnPassRect.x + 20f, btnPassRect.y + 29f)
            if (canPon) font.draw(batch, "PON", btnPonRect.x + 36f, btnPonRect.y + 29f)
            if (canChi) font.draw(batch, "CHI", btnChiRect.x + 38f, btnChiRect.y + 29f)
            if (canKan) {
                font.draw(batch, "KAN", btnKanRect.x + 36f, btnKanRect.y + 29f)
            }
            batch.end()
        }
    }

    private fun renderWaitingForDiscardsPrompt(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val text = "WAITING FOR DISCARDS..."
        val prevScaleX = font.data.scaleX
        val prevScaleY = font.data.scaleY
        val prevColor = Color(font.color)

        val textWidth: Float
        try {
            font.data.setScale(1.1f)
            textWidth = measureTextWidth(font, text)
        } finally {
            font.data.setScale(prevScaleX, prevScaleY)
            font.color = prevColor
        }

        val boxWidth = textWidth + 48f
        val boxHeight = 36f
        val boxX = 640f - boxWidth / 2f
        val boxY = 149f

        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0.04f, 0.12f, 0.08f, 0.85f)
        shape.rect(boxX, boxY, boxWidth, boxHeight)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.85f, 0.72f, 0.35f, 0.85f)
        shape.rect(boxX, boxY, boxWidth, boxHeight)
        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()
        try {
            font.data.setScale(1.1f)
            font.color = Color(1.0f, 0.85f, 0.35f, 0.95f)
            val textX = 640f - textWidth / 2f
            val textY = boxY + 25f
            font.draw(batch, text, textX, textY)
        } finally {
            font.data.setScale(prevScaleX, prevScaleY)
            font.color = prevColor
        }
        batch.end()
    }

    private fun renderSortButton(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        if (isAutoSort) {
            shape.color = if (isSortHovered) Color(0.18f, 0.60f, 0.32f, 1f) else Color(0.12f, 0.45f, 0.24f, 1f)
        } else {
            shape.color = if (isSortHovered) Color(0.38f, 0.28f, 0.20f, 1f) else Color(0.26f, 0.18f, 0.12f, 1f)
        }
        shape.rect(btnSortRect.x, btnSortRect.y, btnSortRect.width, btnSortRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = if (isAutoSort) Color(0.40f, 0.95f, 0.50f, 1f) else Color(0.70f, 0.60f, 0.45f, 1f)
        shape.rect(btnSortRect.x, btnSortRect.y, btnSortRect.width, btnSortRect.height)
        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()
        font.data.setScale(1.1f)
        if (isAutoSort) {
            font.color = Color(0.95f, 1.0f, 0.95f, 1f)
            font.draw(batch, "SORT: ON", btnSortRect.x + 20f, btnSortRect.y + 29f)
        } else {
            font.color = Color.LIGHT_GRAY
            font.draw(batch, "SORT: OFF", btnSortRect.x + 16f, btnSortRect.y + 29f)
        }
        batch.end()
    }

    private fun measureTextWidth(font: BitmapFont, text: String): Float {
        glyphLayout.setText(font, text)
        val measured = glyphLayout.width
        if (measured > 0f) return measured
        return text.length * 9f * font.data.scaleX
    }

    private fun wrapText(font: BitmapFont, text: String, maxWidth: Float): List<String> {
        if (text.isEmpty()) return emptyList()
        val words = text.split(" ")
        val lines = mutableListOf<String>()
        var currentLine = StringBuilder()

        for (word in words) {
            val testLine = if (currentLine.isEmpty()) word else "$currentLine $word"
            if (measureTextWidth(font, testLine) <= maxWidth) {
                currentLine = StringBuilder(testLine)
            } else {
                if (currentLine.isNotEmpty()) {
                    lines.add(currentLine.toString())
                }
                currentLine = StringBuilder(word)
            }
        }
        if (currentLine.isNotEmpty()) {
            lines.add(currentLine.toString())
        }
        return lines
    }

    /**
     * 获取立直选牌面板当前预览选项（悬停优先，未悬停时为已选）。
     */
    fun currentPreviewOption(): RiichiDiscardOption? {
        if (!riichiSelectionModel.isSelecting) return null
        val previewTile = if (hoveredTile != null && riichiSelectionModel.options.any { it.action.discardTile == hoveredTile }) {
            hoveredTile
        } else {
            riichiSelectionModel.selectedTile
        }
        return previewTile?.let { riichiSelectionModel.previewFor(it) }
    }

    /**
     * 获取副露选牌面板当前预览选项（悬停优先，未悬停时为已选）。
     */
    fun currentMeldPreviewChoice(): MeldChoice? {
        if (!meldSelectionModel.isSelecting) return null
        if (hoveredMeldCardIndex in meldSelectionModel.visibleOptions.indices) {
            return meldSelectionModel.visibleOptions[hoveredMeldCardIndex]
        }
        val action = meldSelectionModel.selectedAction ?: return null
        return meldSelectionModel.options.firstOrNull { it.action == action }
    }

    /**
     * 当前观察者本人的振听状态文本描述（用于 HUD 顶部醒目短提示）。
     * 仅在对局进行中、未处于结算且处于振听时返回，否则返回 null。
     */
    fun currentFuritenText(): String? {
        if (!gameStarted || showSettlement || currentView.phase == GamePhase.MATCH_END) return null
        val furiten = currentView.myFuriten
        if (!furiten.isFuriten) return null
        val reasons = formatFuritenReasons(furiten)
        return "FURITEN - TSUMO ONLY (${reasons.joinToString(", ")})"
    }

    /**
     * 立直选牌面板当前预览选项（选中或悬停）的振听警示文本。
     * 仅在立直选牌中且预览方案导致振听时返回，否则返回 null。
     */
    fun previewFuritenText(): String? {
        val option = currentPreviewOption() ?: return null
        if (!option.furiten.isFuriten) return null
        return previewFuritenFullText(option.furiten)
    }

    fun canHumanTsumo(): Boolean = !isDiscardPlaybackBusy && currentView.legalActions.any { it is GameAction.DeclareTsumo }
    fun canHumanRon(): Boolean = !isDiscardPlaybackBusy && currentView.legalActions.any { it is GameAction.DeclareRon }
    fun canHumanPass(): Boolean = !isDiscardPlaybackBusy && currentView.legalActions.any { it is GameAction.Pass }
    fun canHumanPon(): Boolean = !isDiscardPlaybackBusy && currentView.legalActions.any { it is GameAction.DeclarePon }
    fun canHumanChi(): Boolean = !isDiscardPlaybackBusy && currentView.legalActions.any { it is GameAction.DeclareChi }
    fun canHumanKan(): Boolean = !isDiscardPlaybackBusy && currentView.legalActions.any { it is GameAction.DeclareKan }
    fun canHumanRiichi(): Boolean = !isDiscardPlaybackBusy && currentView.legalActions.any { it is GameAction.DeclareRiichi }
    fun canHumanAgariYame(): Boolean = currentView.legalActions.any { it is GameAction.DeclareAgariYame }
    fun canHumanAbortiveDraw(): Boolean = !isDiscardPlaybackBusy && currentView.legalActions.any { it is GameAction.DeclareAbortiveDraw }

    private fun renderSettlementModal(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        val s = currentSettlement ?: return

        // 1. 若拥有和牌完整拆解 (breakdown)，交由 Task U 专用的 SettlementDetailRenderer 处理（支持中文、三标签页、6行明细与小牌面）
        if (s.breakdown != null) {
            val isAgariYame = canHumanAgariYame()
            val isMatchEnd = (currentView.phase == GamePhase.MATCH_END)
            settlementDetailRenderer.renderDetailModal(
                camera = camera,
                shape = shape,
                batch = batch,
                fallbackFont = font,
                tileSkin = tileSkin,
                tileDrawBuffer = tileDrawBuffer,
                settlement = s,
                breakdown = s.breakdown,
                section = settlementSection,
                page = settlementPage,
                viewerSeat = endpoint.viewerSeat,
                currentScores = currentView.scores,
                canAgariYame = isAgariYame,
                isMatchEnd = isMatchEnd,
                btnNextRoundRect = btnNextRoundRect,
                btnNextRoundSplitRect = btnNextRoundSplitRect,
                btnAgariYameRect = btnAgariYameRect
            )
            return
        }

        // 2. 流局或无 breakdown 时，保留原始弹窗与 modalRect 几何契约
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 半透明遮罩
        shape.color = Color(0f, 0f, 0f, 0.72f)
        shape.rect(0f, 0f, 1280f, 720f)

        // 弹窗背景
        shape.color = Color(0.12f, 0.22f, 0.16f, 0.98f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        // 旧记录和牌翻页按钮底色
        if (!s.isDraw) {
            val canPrev = settlementPage > 0
            val canNext = settlementPage < settlementPageCount - 1
            shape.color = if (canPrev) Color(0.20f, 0.40f, 0.30f, 1f) else Color(0.18f, 0.22f, 0.20f, 0.5f)
            shape.rect(btnLegacyPrevPageRect.x, btnLegacyPrevPageRect.y, btnLegacyPrevPageRect.width, btnLegacyPrevPageRect.height)
            shape.color = if (canNext) Color(0.20f, 0.40f, 0.30f, 1f) else Color(0.18f, 0.22f, 0.20f, 0.5f)
            shape.rect(btnLegacyNextPageRect.x, btnLegacyNextPageRect.y, btnLegacyNextPageRect.width, btnLegacyNextPageRect.height)
        }

        // 结算按钮 (支持和了流局续庄 vs 和了止 / 终局显示)
        val isAgariYame = canHumanAgariYame()
        val isMatchEnd = (currentView.phase == GamePhase.MATCH_END)

        if (isAgariYame) {
            shape.color = Color(0.20f, 0.55f, 0.85f, 1f)
            shape.rect(btnNextRoundSplitRect.x, btnNextRoundSplitRect.y, btnNextRoundSplitRect.width, btnNextRoundSplitRect.height)
            shape.color = Color(0.85f, 0.45f, 0.15f, 1f)
            shape.rect(btnAgariYameRect.x, btnAgariYameRect.y, btnAgariYameRect.width, btnAgariYameRect.height)
        } else {
            shape.color = if (isMatchEnd) Color(0.38f, 0.38f, 0.38f, 1f) else Color(0.85f, 0.65f, 0.15f, 1f)
            shape.rect(btnNextRoundRect.x, btnNextRoundRect.y, btnNextRoundRect.width, btnNextRoundRect.height)
        }

        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.95f, 0.85f, 0.4f, 1f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        if (!s.isDraw) {
            val canPrev = settlementPage > 0
            val canNext = settlementPage < settlementPageCount - 1
            shape.color = if (canPrev) Color.WHITE else Color(0.45f, 0.45f, 0.45f, 0.5f)
            shape.rect(btnLegacyPrevPageRect.x, btnLegacyPrevPageRect.y, btnLegacyPrevPageRect.width, btnLegacyPrevPageRect.height)
            shape.color = if (canNext) Color.WHITE else Color(0.45f, 0.45f, 0.45f, 0.5f)
            shape.rect(btnLegacyNextPageRect.x, btnLegacyNextPageRect.y, btnLegacyNextPageRect.width, btnLegacyNextPageRect.height)
        }

        shape.color = Color.WHITE
        if (isAgariYame) {
            shape.rect(btnNextRoundSplitRect.x, btnNextRoundSplitRect.y, btnNextRoundSplitRect.width, btnNextRoundSplitRect.height)
            shape.rect(btnAgariYameRect.x, btnAgariYameRect.y, btnAgariYameRect.width, btnAgariYameRect.height)
        } else {
            shape.rect(btnNextRoundRect.x, btnNextRoundRect.y, btnNextRoundRect.width, btnNextRoundRect.height)
        }
        shape.end()

        // 结算胡牌牌面渲染（和了牌移右上角 x930, y535 (30×42)，远离标题/文字）
        if (s.winTile != null) {
            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = s.winTile,
                x = 930f,
                y = 535f,
                width = 30f,
                height = 42f,
                isHover = false,
                isDrawn = false
            )
            shape.projectionMatrix = camera.combined
            batch.projectionMatrix = camera.combined
            tileDrawBuffer.flush(batch, shape, font)
        }

        val textFont = settlementDetailRenderer.getOrInitChineseFont() ?: font
        val isCustom = (textFont !== font)

        batch.projectionMatrix = camera.combined
        batch.begin()

        val prevScaleX = textFont.data.scaleX
        val prevScaleY = textFont.data.scaleY
        val prevColor = Color(textFont.color)

        try {
            // 标题
            if (!isCustom) textFont.data.setScale(1.8f)
            textFont.color = Color(1.0f, 0.85f, 0.35f, 1f)
            textFont.draw(batch, s.title, modalRect.x + 220f, modalRect.y + 440f)

            if (!isCustom) textFont.data.setScale(1.1f)
            textFont.color = Color.WHITE

            if (s.isDraw) {
                val reasonZh = when (s.drawReason) {
                    "EXHAUSTIVE_WALL", "EXHAUSTIVE" -> "荒牌流局 (牌山摸尽)"
                    "FOUR_RIICHI" -> "四家立直"
                    "FOUR_KANS" -> "四杠散了"
                    "FOUR_WINDS" -> "四风连打"
                    "NINE_TERMINALS" -> "九种九牌"
                    "TRIPLE_RON" -> "三家和了"
                    else -> s.drawReason
                }
                val reasonEn = when (s.drawReason) {
                    "EXHAUSTIVE_WALL", "EXHAUSTIVE" -> "Exhaustive Wall"
                    "FOUR_RIICHI" -> "Four Riichi"
                    "FOUR_KANS" -> "Four Kans"
                    "FOUR_WINDS" -> "Four Winds"
                    "NINE_TERMINALS" -> "Nine Terminals"
                    "TRIPLE_RON" -> "Triple Ron"
                    else -> s.drawReason
                }
                val reasonText = if (isCustom) "流局原因: $reasonZh" else "Reason: $reasonEn"
                val descText = if (isCustom) "荒牌流局 - 本局无胜者" else "Exhaustive Draw - No Winner"
                textFont.draw(batch, reasonText, modalRect.x + 60f, modalRect.y + 380f)
                textFont.draw(batch, descText, modalRect.x + 60f, modalRect.y + 350f)

                // 四家得分变化列表（流局）
                if (!isCustom) textFont.data.setScale(1.0f)
                textFont.color = Color.LIGHT_GRAY
                val summaryHeader = if (isCustom) "-- 得分汇总 --" else "-- SCORE SUMMARY --"
                textFont.draw(batch, summaryHeader, modalRect.x + 60f, modalRect.y + 235f)

                for (i in 0 until 4) {
                    val delta = s.payments[i] ?: 0
                    val deltaStr = if (delta >= 0) "+$delta" else "$delta"
                    val scoreNow = if (s.newScores.size == 4) s.newScores[i] else currentView.scores.getOrElse(i) { 0 }
                    val playerName = if (i == session.viewerSeat) "P$i [YOU]" else "P$i [AI $i]"
                    val line = "$playerName: $scoreNow pts ($deltaStr)"
                    textFont.draw(batch, line, modalRect.x + 60f + (i % 2) * 300f, modalRect.y + 210f - (i / 2) * 22f)
                }
            } else {
                val winnerText = if (isCustom) {
                    if (s.winner == session.viewerSeat) "获胜: P${s.winner} [YOU]" else "获胜: P${s.winner} [AI ${s.winner}]"
                } else {
                    if (s.winner == session.viewerSeat) "WINNER: P${s.winner} [YOU]" else "WINNER: P${s.winner} [AI ${s.winner}]"
                }
                textFont.draw(batch, winnerText, 350f, 515f)

                if (s.loser != null) {
                    val loserText = if (isCustom) {
                        if (s.loser == session.viewerSeat) "放铳: P${s.loser} [YOU]" else "放铳: P${s.loser} [AI ${s.loser}]"
                    } else {
                        if (s.loser == session.viewerSeat) "LOSER: P${s.loser} [YOU]" else "FROM: P${s.loser} [AI ${s.loser}]"
                    }
                    textFont.draw(batch, loserText, 650f, 515f)
                }

                textFont.color = Color(0.75f, 0.80f, 0.75f, 1f)
                val legacyPrompt = if (isCustom) "（旧记录无完整推导明细）" else "(Legacy record without full breakdown)"
                textFont.draw(batch, legacyPrompt, 350f, 485f)

                if (s.winTile != null) {
                    textFont.color = Color(1.0f, 0.85f, 0.35f, 1f)
                    val winTileLabel = if (isCustom) "和了牌" else "WIN TILE"
                    textFont.draw(batch, winTileLabel, 865f, 560f)
                }

                // 旧 yakuList 每一项独立一行，不用 take(3)，六项一页。
                // 正文基线世界 y450,424,398,372,346,320，x350，可用宽580
                textFont.color = Color(0.85f, 0.95f, 0.85f, 1f)
                val pageRows = visibleSettlementRows()
                val baselineYs = floatArrayOf(450f, 424f, 398f, 372f, 346f, 320f)
                for (i in pageRows.indices) {
                    if (i < baselineYs.size) {
                        textFont.draw(batch, pageRows[i].text, 350f, baselineYs[i])
                    }
                }

                // 正文下方 y300 显示总番/符/点数
                textFont.color = Color.YELLOW
                val scoreText = if (isCustom) {
                    "${s.han} 番  ${s.fu} 符  -->  ${s.score} 点"
                } else {
                    "${s.han} Han  ${s.fu} Fu  -->  ${s.score} Pts"
                }
                textFont.draw(batch, scoreText, 350f, 300f)

                // 旧页按钮：prev Rect(350,260,100,30)，next Rect(810,260,100,30)，中间页码
                val canPrev = settlementPage > 0
                val canNext = settlementPage < settlementPageCount - 1
                if (!isCustom) textFont.data.setScale(0.95f)

                textFont.color = if (canPrev) Color.WHITE else Color(0.5f, 0.5f, 0.5f, 0.5f)
                val prevLabel = if (isCustom) "< 上一页" else "< PREV"
                textFont.draw(batch, prevLabel, btnLegacyPrevPageRect.x + 14f, btnLegacyPrevPageRect.y + 22f)

                textFont.color = if (canNext) Color.WHITE else Color(0.5f, 0.5f, 0.5f, 0.5f)
                val nextLabel = if (isCustom) "下一页 >" else "NEXT >"
                textFont.draw(batch, nextLabel, btnLegacyNextPageRect.x + 14f, btnLegacyNextPageRect.y + 22f)

                textFont.color = Color(1.0f, 0.9f, 0.6f, 1f)
                val pageStr = if (isCustom) {
                    "第 ${settlementPage + 1} / $settlementPageCount 页"
                } else {
                    "PAGE ${settlementPage + 1} / $settlementPageCount"
                }
                glyphLayout.setText(textFont, pageStr)
                val pageTextX = 630f - glyphLayout.width / 2f
                textFont.draw(batch, pageStr, pageTextX, btnLegacyPrevPageRect.y + 22f)

                // 玩家分数两行 y240,y218，列x350与650
                if (!isCustom) textFont.data.setScale(1.0f)
                textFont.color = Color.LIGHT_GRAY
                for (i in 0 until 4) {
                    val delta = s.payments[i] ?: 0
                    val deltaStr = if (delta >= 0) "+$delta" else "$delta"
                    val scoreNow = if (s.newScores.size == 4) s.newScores[i] else currentView.scores.getOrElse(i) { 0 }
                    val playerName = if (i == endpoint.viewerSeat) "P$i [YOU]" else "P$i [AI $i]"
                    val line = "$playerName: $scoreNow pts ($deltaStr)"
                    val colX = if (i % 2 == 0) 350f else 650f
                    val rowY = if (i < 2) 240f else 218f
                    textFont.draw(batch, line, colX, rowY)
                }
            }

            // 下一局与和了止按钮文字
            if (!isCustom) textFont.data.setScale(1.2f)
            if (isAgariYame) {
                textFont.color = Color.WHITE
                textFont.draw(batch, "CONTINUE", btnNextRoundSplitRect.x + 60f, btnNextRoundSplitRect.y + 30f)
                textFont.draw(batch, "AGARI-YAME", btnAgariYameRect.x + 50f, btnAgariYameRect.y + 30f)
            } else if (isMatchEnd) {
                textFont.color = Color.LIGHT_GRAY
                textFont.draw(batch, "MATCH ENDED", btnNextRoundRect.x + 40f, btnNextRoundRect.y + 32f)
            } else {
                textFont.color = Color.BLACK
                textFont.draw(batch, "NEXT ROUND >", btnNextRoundRect.x + 40f, btnNextRoundRect.y + 32f)
            }
        } finally {
            textFont.data.setScale(prevScaleX, prevScaleY)
            textFont.color = prevColor
        }

        batch.end()
    }

    /** 开始对局：委托给端点 */
    fun startGame() {
        settlementSection = SettlementSection.YAKU
        settlementPage = 0
        gameStarted = true
        endpoint.start()
    }

    override fun keyDown(keycode: Int): Boolean {
        if (keycode == Input.Keys.ESCAPE || keycode == Input.Keys.BACK) {
            if (meldSelectionModel.isSelecting) {
                meldSelectionModel.cancel()
                return true
            }
            if (riichiSelectionModel.isSelecting) {
                riichiSelectionModel.cancel()
                return true
            }
            riichiSelectionModel.cancel()
            meldSelectionModel.cancel()
            sessionSubscription?.close()
            sessionSubscription = null
            endpoint.close()
            game.setScreen(MenuScreen(game))
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

        // 1. 返回主菜单（释放会话与订阅）
        if (backButtonRect.contains(tx, ty)) {
            riichiSelectionModel.cancel()
            meldSelectionModel.cancel()
            sessionSubscription?.close()
            sessionSubscription = null
            endpoint.close()
            game.setScreen(MenuScreen(game))
            return true
        }

        // 2. 结算弹窗中的交互 (支持标签切换、翻页、NEXT ROUND、AGARI-YAME 和 MATCH ENDED)
        if (showSettlement) {
            val bd = currentSettlement?.breakdown
            if (bd != null) {
                // 2.1 标签页切换（切换或重复点击均重置为第0页）
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

                // 2.2 翻页控制（越界按钮不可点击）
                if (btnPrevPageRect.contains(tx, ty)) {
                    if (settlementPage > 0) {
                        settlementPage--
                    }
                    return true
                }
                if (btnNextPageRect.contains(tx, ty)) {
                    if (settlementPage < settlementPageCount - 1) {
                        settlementPage++
                    }
                    return true
                }
            } else if (currentSettlement != null && !currentSettlement!!.isDraw) {
                // 2.2.1 旧记录和牌翻页（当前三标签按钮不得处理旧记录，只有回退和牌点击这两个按钮才翻页）
                val legacyPageCount = settlementPageCount
                if (btnLegacyPrevPageRect.contains(tx, ty)) {
                    if (settlementPage > 0) {
                        settlementPage--
                    }
                    return true
                }
                if (btnLegacyNextPageRect.contains(tx, ty)) {
                    if (settlementPage < legacyPageCount - 1) {
                        settlementPage++
                    }
                    return true
                }
            }

            // 2.3 推进对局 / 终局
            if (canHumanAgariYame()) {
                if (btnAgariYameRect.contains(tx, ty)) {
                    submitAction(GameAction.DeclareAgariYame(endpoint.viewerSeat))
                    return true
                }
                if (btnNextRoundSplitRect.contains(tx, ty)) {
                    showSettlement = false
                    currentSettlement = null
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                    riichiSelectionModel.cancel()
                    meldSelectionModel.cancel()
                    endpoint.nextRound()
                    return true
                }
            } else if (currentView.phase == GamePhase.MATCH_END) {
                if (btnNextRoundRect.contains(tx, ty)) {
                    showSettlement = false
                    currentSettlement = null
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                    riichiSelectionModel.cancel()
                    meldSelectionModel.cancel()
                    return true
                }
            } else {
                if (btnNextRoundRect.contains(tx, ty)) {
                    showSettlement = false
                    currentSettlement = null
                    settlementSection = SettlementSection.YAKU
                    settlementPage = 0
                    riichiSelectionModel.cancel()
                    meldSelectionModel.cancel()
                    endpoint.nextRound()
                    return true
                }
            }
            return true // 拦截模态弹窗外点击，绝不提交打牌/鸣牌/Pass
        }

        // 2.5 拦截：打牌动画播放期间，吞掉所有游戏交互（理牌、选牌确认、打牌等）
        if (isDiscardPlaybackBusy) {
            return true
        }

        // 3. 理牌开关按钮点击（在选牌态依然可用，且不清除已选中的 Tile 物理身份）
        if (btnSortRect.contains(tx, ty)) {
            isAutoSort = !isAutoSort
            return true
        }

        // 4. 未开局时点击「START GAME」
        if (!gameStarted) {
            if (btnStartGameRect.contains(tx, ty)) {
                startGame()
                return true
            }
            return false
        }

        // 5. 若处于立直选牌模式
        if (riichiSelectionModel.isSelecting) {
            // 点击 CANCEL 按钮
            if (btnCancelRiichiRect.contains(tx, ty)) {
                riichiSelectionModel.cancel()
                return true
            }

            // 点击 CONFIRM RIICHI 按钮
            if (btnRiichiRect.contains(tx, ty)) {
                val action = riichiSelectionModel.confirmationAction()
                if (action != null) {
                    val result = submitAction(action)
                    when (result) {
                        is ActionResult.Accepted -> {
                            riichiSelectionModel.cancel()
                        }
                        is ActionResult.Rejected -> {
                            riichiSelectionModel.cancel()
                            currentView = endpoint.view()
                            riichiSelectionModel.update(currentView)
                        }
                    }
                    return true
                }
                // 未选择牌时确认禁用，点击无效
                return true
            }

            // 点击手牌选择立直打牌
            val slots = refreshHandTileRects()
            for (slot in slots) {
                val hitRect = Rectangle(slot.rect.x, 40f, slot.rect.width, 94f)
                if (hitRect.contains(tx, ty) || slot.rect.contains(tx, ty)) {
                    riichiSelectionModel.select(slot.tile)
                    return true
                }
            }

            // 选牌模式拦截其他所有桌面点击，避免误触普通打牌或鸣牌
            return true
        }

        // 5.5 若处于副露选牌模式
        if (meldSelectionModel.isSelecting) {
            // 点击 CANCEL 按钮（取消只退回普通操作，不提交 Pass，也不关游戏）
            if (btnMeldCancelRect.contains(tx, ty)) {
                meldSelectionModel.cancel()
                return true
            }

            // 点击 CONFIRM 按钮（确认只提交被选中合法动作的原始内容一次）
            if (btnMeldConfirmRect.contains(tx, ty)) {
                val action = meldSelectionModel.confirmationAction()
                if (action != null) {
                    val latestView = endpoint.view()
                    val isStillLegal = (latestView == currentView && latestView.legalActions.contains(action))
                    if (!isStillLegal) {
                        meldSelectionModel.cancel()
                        currentView = latestView
                        meldSelectionModel.update(latestView)
                        riichiSelectionModel.update(latestView)
                        return true
                    }
                    meldSelectionModel.cancel()
                    val result = submitAction(action)
                    when (result) {
                        is ActionResult.Accepted -> {}
                        is ActionResult.Rejected -> {
                            currentView = endpoint.view()
                            meldSelectionModel.update(currentView)
                            riichiSelectionModel.update(currentView)
                        }
                    }
                }
                return true
            }

            // 点击 WIN 快捷按钮（按合法动作显示 RON 或 TSUMO，自摸和荣和不因打开副露选择而消失）
            if ((canHumanRon() || canHumanTsumo()) && btnMeldWinRect.contains(tx, ty)) {
                meldSelectionModel.cancel()
                if (canHumanRon()) {
                    submitAction(GameAction.DeclareRon(endpoint.viewerSeat))
                } else if (canHumanTsumo()) {
                    submitAction(GameAction.DeclareTsumo(endpoint.viewerSeat))
                }
                return true
            }

            // 点击 PASS 按钮（仅合法 Pass 时出现，点击才提交 Pass）
            if (canHumanPass() && btnMeldPassRect.contains(tx, ty)) {
                meldSelectionModel.cancel()
                submitAction(GameAction.Pass(endpoint.viewerSeat))
                return true
            }

            // 点击 PREV 翻页
            if (btnMeldPrevRect.contains(tx, ty)) {
                if (meldSelectionModel.pageIndex > 0) {
                    meldSelectionModel.setPage(meldSelectionModel.pageIndex - 1)
                }
                return true
            }

            // 点击 NEXT 翻页
            if (btnMeldNextRect.contains(tx, ty)) {
                if (meldSelectionModel.pageIndex < meldSelectionModel.pageCount - 1) {
                    meldSelectionModel.setPage(meldSelectionModel.pageIndex + 1)
                }
                return true
            }

            // 点击候选卡片进行选中
            for (i in meldSelectionModel.visibleOptions.indices) {
                val cardRect = meldCardRects[i]
                if (cardRect.contains(tx, ty)) {
                    val choice = meldSelectionModel.visibleOptions[i]
                    meldSelectionModel.select(choice.action)
                    return true
                }
            }

            // 点手牌或面板空白不得出牌，拦截选牌模式下的所有其他桌面点击
            return true
        }

        // 6. 普通回合：荣和与跳过点击
        if (canHumanRon() && btnRonRect.contains(tx, ty)) {
            submitAction(GameAction.DeclareRon(endpoint.viewerSeat))
            return true
        }
        if (canHumanPass() && btnPassRect.contains(tx, ty)) {
            submitAction(GameAction.Pass(endpoint.viewerSeat))
            return true
        }

        // 7. 鸣牌点击 (Pon, Chi, Kan)
        if (canHumanPon() && btnPonRect.contains(tx, ty)) {
            if (riichiSelectionModel.isSelecting) {
                riichiSelectionModel.cancel()
            }
            meldSelectionModel.begin(MeldChoiceKind.PON)
            return true
        }
        if (canHumanChi() && btnChiRect.contains(tx, ty)) {
            if (riichiSelectionModel.isSelecting) {
                riichiSelectionModel.cancel()
            }
            meldSelectionModel.begin(MeldChoiceKind.CHI)
            return true
        }
        if (canHumanKan() && btnKanRect.contains(tx, ty)) {
            if (riichiSelectionModel.isSelecting) {
                riichiSelectionModel.cancel()
            }
            meldSelectionModel.begin(MeldChoiceKind.KAN)
            return true
        }

        // 8. 自摸与立直点击
        if (canHumanTsumo() && btnTsumoRect.contains(tx, ty)) {
            submitAction(GameAction.DeclareTsumo(endpoint.viewerSeat))
            return true
        }
        if (canHumanRiichi() && btnRiichiRect.contains(tx, ty)) {
            if (meldSelectionModel.isSelecting) {
                meldSelectionModel.cancel()
            }
            // 点击 RIICHI 仅进入选牌，不提交命令、不自动选择第一项、不扣点
            val begun = riichiSelectionModel.begin()
            return begun
        }
        if (canHumanAbortiveDraw() && btnAbortiveDrawRect.contains(tx, ty)) {
            submitAction(GameAction.DeclareAbortiveDraw(endpoint.viewerSeat))
            return true
        }

        // 9. 人类出牌点击（普通手牌交互）
        if (isHumanTurn()) {
            val slots = refreshHandTileRects()
            for (slot in slots) {
                val hitRect = Rectangle(slot.rect.x, 40f, slot.rect.width, 94f)
                if (hitRect.contains(tx, ty) || slot.rect.contains(tx, ty)) {
                    submitAction(GameAction.DiscardTile(endpoint.viewerSeat, slot.tile))
                    return true
                }
            }
        }

        return false
    }

    private fun getTileText(tile: Tile): String = when (tile.suit) {
        Suit.MAN -> "${tile.rank}m"
        Suit.PIN -> "${tile.rank}p"
        Suit.SOU -> "${tile.rank}s"
        Suit.WIND -> when (tile.rank) {
            1 -> "E"; 2 -> "S"; 3 -> "W"; 4 -> "N"; else -> "?"
        }
        Suit.DRAGON -> when (tile.rank) {
            1 -> "Wh"; 2 -> "Gr"; 3 -> "Rd"; else -> "?"
        }
    }

    private fun getWindText(wind: Int): String = when (wind) {
        1 -> "EAST"; 2 -> "SOUTH"; 3 -> "WEST"; 4 -> "NORTH"; else -> "EAST"
    }

    override fun resize(width: Int, height: Int) {}
    override fun pause() {}
    override fun resume() {}
    override fun hide() {
        fastForwardDiscardAnimations()
        riichiSelectionModel.cancel()
        meldSelectionModel.cancel()
        sessionSubscription?.close()
        sessionSubscription = null
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
    override fun dispose() {
        fastForwardDiscardAnimations()
        riichiSelectionModel.cancel()
        meldSelectionModel.cancel()
        sessionSubscription?.close()
        sessionSubscription = null
        endpoint.close()
        tileSkin.dispose()
        GhostHandTexture.dispose()
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

    /**
     * 统一动作提交辅助函数。
     * 针对支持决策候选机制的端点 (capabilities.decisions == true)，仅通过 queryDecision / submitChoice 提交结构化选择；
     * 严格按真实实体牌与完整候选组合进行精确匹配，禁止同种牌模糊回退。
     * 若未找到匹配候选或调用中发生异常，返回明确拒绝 ActionResult.Rejected(ACTION_NOT_AVAILABLE)，绝不回退至旧 submit(action) 接口重试。
     * 仅当端点不支持决策候选 (capabilities.decisions == false，如旧测试替身) 时，才通过 submit(action) 提交。
     */
    fun submitAction(action: GameAction): ActionResult {
        if (endpoint.capabilities.decisions) {
            return try {
                val decision = endpoint.queryDecision()
                val option = decision.options.firstOrNull { it.action == action }
                    ?: return ActionResult.Rejected(com.majiang.state.RejectionReason.ACTION_NOT_AVAILABLE)
                val choice = ActionChoice(
                    requestId = UUID.randomUUID().toString(),
                    roundId = decision.roundId,
                    decisionId = decision.decisionId,
                    controlEpoch = decision.controlEpoch,
                    optionId = option.optionId
                )
                endpoint.submitChoice(choice)
            } catch (e: Exception) {
                ActionResult.Rejected(com.majiang.state.RejectionReason.ACTION_NOT_AVAILABLE)
            }
        }
        return endpoint.submit(action)
    }

    companion object {
        const val PREVIEW_FURITEN_TITLE = "AFTER DISCARD: FURITEN - TSUMO ONLY"

        fun formatFuritenReasons(furiten: FuritenStatus): List<String> {
            val reasons = mutableListOf<String>()
            if (furiten.discard) reasons.add("DISCARD")
            if (furiten.temporary) reasons.add("TEMPORARY")
            if (furiten.riichi) reasons.add("RIICHI")
            return reasons
        }

        fun previewFuritenCausesText(furiten: FuritenStatus): String {
            val reasons = formatFuritenReasons(furiten)
            return if (reasons.isEmpty()) "" else "CAUSES: ${reasons.joinToString(", ")}"
        }

        fun previewFuritenFullText(furiten: FuritenStatus): String {
            val reasons = formatFuritenReasons(furiten)
            return if (reasons.isEmpty()) {
                PREVIEW_FURITEN_TITLE
            } else {
                "$PREVIEW_FURITEN_TITLE (${reasons.joinToString(", ")})"
            }
        }
    }
}
