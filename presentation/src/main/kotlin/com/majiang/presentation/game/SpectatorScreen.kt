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
import com.majiang.client.api.SpectatorSession
import com.majiang.presentation.common.ClockDisplay
import com.majiang.presentation.common.FinalResultRenderer
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.MeldDisplayRenderer
import com.majiang.presentation.common.render.MeldDisplayLayout
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin

/**
 * 独立观战牌桌场景 (V4 presentation.game)。
 *
 * 核心契约与安全隔离规范：
 * 1. 严格只依赖 [UiAssets]（根借用）、[SpectatorSession]、[TableNavigation] 与可选 [TileSkin]；
 * 2. 独立只读公开流：仅消费 [PublicSnapshotView] 与 [PublicUpdateView]，绝不伪造或依赖私有玩家快照；
 * 3. 严格私密隔离：手牌完全按 [PublicBoardView.handTileCounts] 渲染为牌背，绝无手牌明面；
 * 4. 纯只读界面：完全不渲染吃碰杠、立直、自摸、荣和、过牌、托管、开赛、理牌或千术按钮；
 * 5. 结算与终局：仅展示公开结算事件与比赛排名总成绩，提供返回菜单导航；
 * 6. 零 engine、protocol、server 依赖。
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

    val backButtonRect = Rectangle(30f, 655f, 140f, 42f)
    val btnReturnToMenuRect = Rectangle(520f, 145f, 240f, 46f)

    private val unprojectVec = Vector3()
    private var previousCatchBack = false
    private var ownsBackCatch = false

    private val finalResultRenderer = FinalResultRenderer()

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
        } else {
            isLoading = true
            session.publicSnapshot().whenComplete { s, _ ->
                runOnUi {
                    isLoading = false
                    if (s != null) {
                        currentSnapshot = s
                        lastClockUpdateLocalMillis = System.currentTimeMillis()
                    }
                }
            }
        }
    }

    fun onPublicUpdate(update: PublicUpdateView) {
        currentSnapshot = update.snapshot
        lastClockUpdateLocalMillis = System.currentTimeMillis()
        isLoading = false
    }

    override fun show() {
        Gdx.input?.let { input ->
            previousCatchBack = input.isCatchKey(Input.Keys.BACK)
            input.setCatchKey(Input.Keys.BACK, true)
            ownsBackCatch = true
            input.inputProcessor = this
        }
    }

    override fun hide() {
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
        camera.update()
        Gdx.gl.glClearColor(0.06f, 0.16f, 0.12f, 1f)
        Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT)

        val batch = assets.batch
        val shape = assets.shapeRenderer
        val font = assets.font
        val chineseFont = assets.chineseFont

        // 1. 顶部返回菜单按钮
        renderBackButton(shape, batch, font)

        // 2. 桌面中心信息（风局、牌山、宝牌等）
        renderTableCenter(batch, font, chineseFont)

        // 3. 四家席位信息（分数、时钟、立直等）
        renderSeatsInfo(batch, font)

        // 4. 四家手牌（严格纯牌背！）
        renderHandTileBacks(batch, shape, font)

        // 5. 四家弃牌河
        renderDiscards(batch, shape, font)

        // 6. 四家副露
        renderMelds(batch, shape, font)

        // 7. 终局总成绩弹窗（比赛结束时展示）
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
        textFont.data.setScale(1.0f)
        textFont.color = Color.WHITE
        textFont.draw(batch, "返回大厅", backButtonRect.x + 28f, backButtonRect.y + 27f)
        batch.end()
    }

    private fun renderTableCenter(batch: SpriteBatch, font: BitmapFont, chineseFont: BitmapFont?) {
        val board = currentBoard ?: return
        batch.projectionMatrix = camera.combined
        batch.begin()
        val textFont = chineseFont ?: font
        val origColor = Color(textFont.color)
        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY

        try {
            textFont.data.setScale(1.1f)
            textFont.color = Color(1.0f, 0.85f, 0.35f, 1f)
            val roundWindName = listOf("東", "南", "西", "北").getOrElse(board.roundWind - 1) { "東" }
            val roundInfo = "【观战】 $roundWindName${board.roundNumber + 1}局  本场:${board.honbaSticks}  供托:${board.riichiSticks}"
            textFont.draw(batch, roundInfo, 510f, 400f)

            textFont.color = Color(0.9f, 0.9f, 0.9f, 1f)
            textFont.draw(batch, "牌山剩余: ${board.wallRemaining}", 570f, 370f)

            textFont.color = Color(1.0f, 0.6f, 0.2f, 1f)
            textFont.draw(batch, "宝牌指示牌:", 520f, 340f)
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
        }
        batch.end()

        for (i in board.doraIndicators.indices) {
            val tile = board.doraIndicators[i]
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

    private fun renderSeatsInfo(batch: SpriteBatch, font: BitmapFont) {
        val board = currentBoard ?: return
        val clocks = currentSnapshot?.clocks ?: emptyList()
        val elapsed = System.currentTimeMillis() - lastClockUpdateLocalMillis

        for (seat in 0..3) {
            val clock = clocks.firstOrNull { it.seat == seat }
            val formattedClock = ClockDisplay.format(clock, elapsed)
            val score = board.scores.getOrElse(seat) { 25000 }
            val isRiichi = board.riichiStatus.getOrElse(seat) { false }
            val isCurrent = (board.currentTurnPlayer == seat)

            val (x, y) = when (seat) {
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
            font.draw(batch, "P$seat: $score", x, y)
            font.color = if (formattedClock.isUrgent) Color.RED else Color(0.7f, 1.0f, 0.8f, 1f)
            font.draw(batch, formattedClock.displayText, x, y - 18f)

            if (isRiichi) {
                font.color = Color(1.0f, 0.4f, 0.4f, 1f)
                font.draw(batch, "[RIICHI]", x, y - 36f)
            }
            batch.end()
        }
    }

    private fun renderHandTileBacks(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        val board = currentBoard ?: return

        for (seat in 0..3) {
            val count = board.handTileCounts.getOrElse(seat) { 13 }
            if (count <= 0) continue

            when (seat) {
                0 -> {
                    // 底端席位手牌背面
                    val startX = 260f
                    val y = 40f
                    val tileW = 44f
                    val tileH = 62f
                    for (i in 0 until count) {
                        tileSkin.appendTileBackDraw(
                            buf = tileDrawBuffer,
                            x = startX + i * 48f,
                            y = y,
                            width = tileW,
                            height = tileH
                        )
                    }
                }
                1 -> {
                    // 右侧席位手牌背面
                    val x = 1190f
                    val startY = 220f
                    val tileW = 42f
                    val tileH = 26f
                    for (i in 0 until count) {
                        tileSkin.appendTileBackDraw(
                            buf = tileDrawBuffer,
                            x = x,
                            y = startY + i * 28f,
                            width = tileW,
                            height = tileH
                        )
                    }
                }
                2 -> {
                    // 顶端席位手牌背面
                    val startX = 860f
                    val y = 620f
                    val tileW = 44f
                    val tileH = 62f
                    for (i in 0 until count) {
                        tileSkin.appendTileBackDraw(
                            buf = tileDrawBuffer,
                            x = startX - i * 48f,
                            y = y,
                            width = tileW,
                            height = tileH
                        )
                    }
                }
                3 -> {
                    // 左侧席位手牌背面
                    val x = 50f
                    val startY = 480f
                    val tileW = 42f
                    val tileH = 26f
                    for (i in 0 until count) {
                        tileSkin.appendTileBackDraw(
                            buf = tileDrawBuffer,
                            x = x,
                            y = startY - i * 28f,
                            width = tileW,
                            height = tileH
                        )
                    }
                }
            }
        }

        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, font)
    }

    private fun renderDiscards(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        val board = currentBoard ?: return
        for (seat in 0..3) {
            val discards = board.discards.getOrElse(seat) { emptyList() }
            for (i in discards.indices) {
                val tile = discards[i]
                val pos = getDiscardSlotPosition(seat, i)
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
    }

    private fun renderMelds(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        val board = currentBoard ?: return
        val groups = MeldDisplayLayout.layoutAll(board.melds, viewerSeat = 0)
        if (groups.isEmpty()) return
        MeldDisplayRenderer.append(tileDrawBuffer, tileSkin, groups)
        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, font)
    }

    private fun getDiscardSlotPosition(seat: Int, slotIndex: Int): Pair<Float, Float> {
        val col = slotIndex % 6
        val row = slotIndex / 6
        val tileW = 38f
        val tileH = 52f

        return when (seat % 4) {
            0 -> Pair(480f + col * (tileW + 2f), 240f - row * (tileH + 4f))
            1 -> Pair(780f + row * (tileH + 4f), 280f + col * (tileW + 2f))
            2 -> Pair(700f - col * (tileW + 2f), 480f + row * (tileH + 4f))
            3 -> Pair(420f - row * (tileH + 4f), 440f - col * (tileW + 2f))
            else -> Pair(480f, 240f)
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

        val isFinished = (currentSnapshot?.session?.finalResult != null || currentBoard?.phase == GamePhaseView.MATCH_END)
        if (isFinished && btnReturnToMenuRect.contains(tx, ty)) {
            leaveToMenu()
            return true
        }

        return true // 观战拦截所有其他点击，防止任何非预期操作
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
