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
import com.majiang.presentation.common.render.TableBoardRenderModel
import com.majiang.presentation.common.render.TableBoardRenderer
import com.majiang.presentation.common.render.TableBoardRenderOptions
import com.majiang.presentation.common.render.fromPublicSnapshot

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

        val snap = currentSnapshot
        if (snap?.board != null) {
            val model = TableBoardRenderModel.fromPublicSnapshot(snap)
            val options = TableBoardRenderOptions(
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
        }

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
