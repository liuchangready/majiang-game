package com.majiang.screen

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.Input
import com.badlogic.gdx.InputAdapter
import com.badlogic.gdx.Screen
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.MathUtils
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.math.Vector3
import com.majiang.MahjongGame

/**
 * 千术麻将主菜单场景 (MenuScreen)。
 *
 * 融合古典暗黑牌馆与雀圣秘传氛围，支持：
 * 1. 开始对局（平滑切入牌桌 GameScreen）
 * 2. 千术秘籍弹窗（展示四大出千绝技与怀疑度机制）
 * 3. 对局设置弹窗（音效开关与雀力难度设定）
 * 4. 退出游戏
 * 5. 跨平台支持：PC 鼠标悬停高亮、手机触控交互与全分辨率视口自适应。
 */
class MenuScreen(private val game: MahjongGame) : Screen, InputAdapter() {

    private val camera = OrthographicCamera().apply {
        setToOrtho(false, 1280f, 720f)
    }

    // 菜单主按钮区域
    private val btnPlay = Rectangle(490f, 395f, 300f, 52f)
    private val btnSkin = Rectangle(490f, 330f, 300f, 48f)
    private val btnSecrets = Rectangle(490f, 265f, 300f, 48f)
    private val btnSettings = Rectangle(490f, 200f, 300f, 48f)
    private val btnExit = Rectangle(490f, 135f, 300f, 48f)

    // 弹窗与关闭按钮区域
    private val modalRect = Rectangle(260f, 110f, 760f, 500f)
    private val btnModalClose = Rectangle(570f, 135f, 140f, 42f)

    // 弹窗状态：NONE, SECRETS, SETTINGS
    private enum class ModalState { NONE, SECRETS, SETTINGS }
    private var activeModal = ModalState.NONE

    // 交互与悬停状态
    private val unprojectVec = Vector3()
    private var hoverPlay = false
    private var hoverSkin = false
    private var hoverSecrets = false
    private var hoverSettings = false
    private var hoverExit = false
    private var hoverModalClose = false

    // 动画计时与装饰粒子
    private var animTimer = 0f
    private val particles = List(35) {
        FloatingParticle(
            x = MathUtils.random(0f, 1280f),
            y = MathUtils.random(0f, 720f),
            size = MathUtils.random(2f, 5f),
            speedY = MathUtils.random(15f, 40f),
            alpha = MathUtils.random(0.2f, 0.7f)
        )
    }

    override fun show() {
        Gdx.input.inputProcessor = this
    }

    override fun render(delta: Float) {
        animTimer += delta
        updateParticles(delta)
        updateHoverStates()

        camera.update()
        Gdx.gl.glClearColor(0.04f, 0.08f, 0.06f, 1f)
        Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT)

        val shape = game.shapeRenderer
        val batch = game.batch
        val font = game.font

        // 1. 渲染背景与装饰麻将牌
        renderBackground(shape)

        // 2. 渲染主菜单按钮
        renderMenuButtons(shape)

        // 3. 渲染主标题与文字
        renderTexts(batch, font)

        // 4. 若有弹窗，渲染模态遮罩与弹窗内容
        if (activeModal != ModalState.NONE) {
            renderModal(shape, batch, font)
        }
    }

    private fun updateParticles(delta: Float) {
        for (p in particles) {
            p.y += p.speedY * delta
            if (p.y > 720f) {
                p.y = -10f
                p.x = MathUtils.random(0f, 1280f)
            }
        }
    }

    private fun updateHoverStates() {
        if (Gdx.graphics == null || Gdx.graphics.width == 0) return
        val mouseX = Gdx.input.x.toFloat()
        val mouseY = Gdx.input.y.toFloat()
        unprojectVec.set(mouseX, mouseY, 0f)
        camera.unproject(unprojectVec)

        if (activeModal != ModalState.NONE) {
            hoverModalClose = btnModalClose.contains(unprojectVec.x, unprojectVec.y)
            hoverPlay = false
            hoverSkin = false
            hoverSecrets = false
            hoverSettings = false
            hoverExit = false
        } else {
            hoverPlay = btnPlay.contains(unprojectVec.x, unprojectVec.y)
            hoverSkin = btnSkin.contains(unprojectVec.x, unprojectVec.y)
            hoverSecrets = btnSecrets.contains(unprojectVec.x, unprojectVec.y)
            hoverSettings = btnSettings.contains(unprojectVec.x, unprojectVec.y)
            hoverExit = btnExit.contains(unprojectVec.x, unprojectVec.y)
            hoverModalClose = false
        }
    }

    private fun renderBackground(shape: ShapeRenderer) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 墨绿牌桌暗纹背景
        shape.color = Color(0.06f, 0.16f, 0.11f, 1f)
        shape.rect(0f, 0f, 1280f, 720f)

        // 暗角光晕边框
        shape.color = Color(0.02f, 0.05f, 0.03f, 1f)
        shape.rect(0f, 0f, 1280f, 25f)
        shape.rect(0f, 695f, 1280f, 25f)
        shape.rect(0f, 0f, 25f, 720f)
        shape.rect(1255f, 0f, 25f, 720f)

        // 装饰麻将牌阵（左右两侧与边角）
        renderDecorativeTile(shape, 120f, 520f, -15f)
        renderDecorativeTile(shape, 180f, 480f, 10f)
        renderDecorativeTile(shape, 140f, 200f, 25f)
        renderDecorativeTile(shape, 1100f, 510f, 18f)
        renderDecorativeTile(shape, 1040f, 460f, -12f)
        renderDecorativeTile(shape, 1080f, 210f, -22f)

        // 金色浮尘微粒
        for (p in particles) {
            shape.color = Color(0.95f, 0.85f, 0.40f, p.alpha * (0.6f + 0.4f * MathUtils.sin(animTimer * 2f + p.x)))
            shape.circle(p.x, p.y, p.size)
        }

        shape.end()
    }

    /**
     * 绘制具有立体厚度质感的装饰麻将牌
     */
    private fun renderDecorativeTile(shape: ShapeRenderer, x: Float, y: Float, rot: Float) {
        val w = 54f
        val h = 76f
        // 牌背底色（翠玉绿）
        shape.color = Color(0.05f, 0.38f, 0.22f, 0.85f)
        shape.rect(x + 4f, y - 4f, w, h)
        // 牌体象牙白
        shape.color = Color(0.96f, 0.94f, 0.88f, 0.95f)
        shape.rect(x, y, w, h)
        // 牌面内框阴影
        shape.color = Color(0.85f, 0.82f, 0.74f, 0.95f)
        shape.rect(x + 4f, y + 4f, w - 8f, h - 8f)
    }

    private fun renderMenuButtons(shape: ShapeRenderer) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        drawButtonBase(shape, btnPlay, hoverPlay, isPrimary = true)
        drawButtonBase(shape, btnSkin, hoverSkin, isPrimary = false)
        drawButtonBase(shape, btnSecrets, hoverSecrets, isPrimary = false)
        drawButtonBase(shape, btnSettings, hoverSettings, isPrimary = false)
        drawButtonBase(shape, btnExit, hoverExit, isPrimary = false)

        shape.end()

        // 绘制金色边框
        shape.begin(ShapeRenderer.ShapeType.Line)
        drawButtonBorder(shape, btnPlay, hoverPlay, isPrimary = true)
        drawButtonBorder(shape, btnSkin, hoverSkin, isPrimary = false)
        drawButtonBorder(shape, btnSecrets, hoverSecrets, isPrimary = false)
        drawButtonBorder(shape, btnSettings, hoverSettings, isPrimary = false)
        drawButtonBorder(shape, btnExit, hoverExit, isPrimary = false)
        shape.end()
    }

    private fun drawButtonBase(shape: ShapeRenderer, rect: Rectangle, hovered: Boolean, isPrimary: Boolean) {
        val pulse = 0.05f * MathUtils.sin(animTimer * 4f)
        if (isPrimary) {
            shape.color = if (hovered) {
                Color(0.85f + pulse, 0.22f, 0.18f, 1f)
            } else {
                Color(0.68f, 0.15f, 0.12f, 1f)
            }
        } else {
            shape.color = if (hovered) {
                Color(0.24f, 0.36f, 0.28f, 1f)
            } else {
                Color(0.12f, 0.20f, 0.15f, 1f)
            }
        }
        shape.rect(rect.x, rect.y, rect.width, rect.height)
    }

    private fun drawButtonBorder(shape: ShapeRenderer, rect: Rectangle, hovered: Boolean, isPrimary: Boolean) {
        shape.color = if (hovered) {
            Color(1.0f, 0.92f, 0.45f, 1f)
        } else if (isPrimary) {
            Color(0.92f, 0.78f, 0.35f, 1f)
        } else {
            Color(0.55f, 0.65f, 0.50f, 0.8f)
        }
        shape.rect(rect.x, rect.y, rect.width, rect.height)
    }

    private fun renderTexts(batch: com.badlogic.gdx.graphics.g2d.SpriteBatch, font: com.badlogic.gdx.graphics.g2d.BitmapFont) {
        batch.projectionMatrix = camera.combined
        batch.begin()

        // 主标题
        font.data.setScale(3.2f)
        val pulseColor = 0.85f + 0.15f * MathUtils.sin(animTimer * 3f)
        font.color = Color(1.0f, pulseColor, 0.35f, 1f)
        val title = "MAHJONG CHEATER"
        font.draw(batch, title, 390f, 620f)

        // 副标题
        font.data.setScale(1.4f)
        font.color = Color(0.90f, 0.80f, 0.60f, 0.95f)
        val subtitle = "- THE SECRET ART OF SLEIGHT & TILES -"
        font.draw(batch, subtitle, 415f, 545f)

        // 按钮文本
        font.data.setScale(1.5f)
        font.color = if (hoverPlay) Color.WHITE else Color(1f, 0.95f, 0.8f, 1f)
        font.draw(batch, "ENTER TABLE (PLAY)", btnPlay.x + 36f, btnPlay.y + 36f)

        font.data.setScale(1.3f)
        font.color = if (hoverSkin) Color.YELLOW else Color.WHITE
        val skinText = "SKIN: ${game.skinManager.currentSkinId.name}"
        font.draw(batch, skinText, btnSkin.x + 55f, btnSkin.y + 32f)

        font.color = if (hoverSecrets) Color.YELLOW else Color.WHITE
        font.draw(batch, "CHEATING SECRETS", btnSecrets.x + 48f, btnSecrets.y + 32f)

        font.color = if (hoverSettings) Color.YELLOW else Color.WHITE
        font.draw(batch, "SETTINGS", btnSettings.x + 95f, btnSettings.y + 32f)

        font.color = if (hoverExit) Color.CORAL else Color.LIGHT_GRAY
        font.draw(batch, "EXIT GAME", btnExit.x + 92f, btnExit.y + 32f)

        // 底部版本与平台说明
        font.data.setScale(1.0f)
        font.color = Color(0.6f, 0.7f, 0.65f, 0.7f)
        font.draw(batch, "v1.0.0 | PC & Mobile Cross-Platform Edition | libGDX 1.14.2", 430f, 60f)

        batch.end()
    }

    private fun renderModal(
        shape: ShapeRenderer,
        batch: com.badlogic.gdx.graphics.g2d.SpriteBatch,
        font: com.badlogic.gdx.graphics.g2d.BitmapFont
    ) {
        // 半透明黑色背景蒙层
        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)

        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0f, 0f, 0f, 0.75f)
        shape.rect(0f, 0f, 1280f, 720f)

        // 弹窗主框（深褐色红木边框 + 羊皮纸色底）
        shape.color = Color(0.18f, 0.10f, 0.05f, 1f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)
        shape.color = Color(0.10f, 0.22f, 0.15f, 0.98f)
        shape.rect(modalRect.x + 8f, modalRect.y + 8f, modalRect.width - 16f, modalRect.height - 16f)

        // 关闭按钮
        shape.color = if (hoverModalClose) Color(0.85f, 0.25f, 0.20f, 1f) else Color(0.55f, 0.15f, 0.12f, 1f)
        shape.rect(btnModalClose.x, btnModalClose.y, btnModalClose.width, btnModalClose.height)

        shape.end()

        // 弹窗金线边框
        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.95f, 0.85f, 0.40f, 1f)
        shape.rect(modalRect.x + 8f, modalRect.y + 8f, modalRect.width - 16f, modalRect.height - 16f)
        shape.rect(btnModalClose.x, btnModalClose.y, btnModalClose.width, btnModalClose.height)
        shape.end()

        // 弹窗文字内容
        batch.projectionMatrix = camera.combined
        batch.begin()

        if (activeModal == ModalState.SECRETS) {
            font.data.setScale(2.0f)
            font.color = Color(1.0f, 0.85f, 0.35f, 1f)
            font.draw(batch, "-- SECRET CHEATING TECHNIQUES --", modalRect.x + 120f, modalRect.y + 460f)

            font.data.setScale(1.25f)
            font.color = Color.WHITE

            val lines = listOf(
                "[1] SLEEVE SWAP (Sleeve Pocket):",
                "    Hide 1-2 secret tiles up your sleeve. Swap with hand unnoticed.",
                "",
                "[2] WALL STEAL (Flower to Timber):",
                "    When drawing, stealthily steal from the deck's bottom tile.",
                "",
                "[3] X-RAY GLANCE (God's Eye):",
                "    Peek at opponents' concealed tiles and the upcoming deck wall.",
                "",
                "[4] DISTRACTION (Sound East, Strike West):",
                "    Spill a tea cup or tap table to divert all opponents' gaze for 3s.",
                "",
                "(!) CAUTION: Opponents have a dynamic Suspicion Gauge (0~100%).",
                "    If caught cheating under direct gaze, BUSTED triggers penalty!"
            )

            var textY = modalRect.y + 400f
            for (line in lines) {
                if (line.startsWith("[") || line.startsWith("(!)")) {
                    font.color = Color(0.98f, 0.88f, 0.42f, 1f)
                } else {
                    font.color = Color.LIGHT_GRAY
                }
                font.draw(batch, line, modalRect.x + 40f, textY)
                textY -= 28f
            }
        } else if (activeModal == ModalState.SETTINGS) {
            font.data.setScale(2.0f)
            font.color = Color(1.0f, 0.85f, 0.35f, 1f)
            font.draw(batch, "-- GAME SETTINGS --", modalRect.x + 240f, modalRect.y + 450f)

            font.data.setScale(1.4f)
            font.color = Color.WHITE
            font.draw(batch, "TILE SKIN     : [ ${game.skinManager.currentSkinId.name} (CLICK TO SWITCH) ]", modalRect.x + 80f, modalRect.y + 370f)
            font.draw(batch, "CONTROLS      : PC Mouse Drag / Mobile Touch Gestures", modalRect.x + 80f, modalRect.y + 310f)
        }

        // 关闭按钮文本
        font.data.setScale(1.2f)
        font.color = Color.WHITE
        font.draw(batch, "CLOSE [X]", btnModalClose.x + 28f, btnModalClose.y + 28f)

        batch.end()
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

        if (activeModal != ModalState.NONE) {
            // 点击关闭按钮
            if (btnModalClose.contains(tx, ty)) {
                activeModal = ModalState.NONE
                return true
            }

            // 设置弹窗内点击切换选项
            if (activeModal == ModalState.SETTINGS) {
                if (ty in 345f..395f) {
                    game.skinManager.toggleSkin()
                    return true
                }
            }
            return true
        }

        // 主菜单按钮点击
        if (btnPlay.contains(tx, ty)) {
            game.setScreen(GameScreen(game))
            return true
        }
        if (btnSkin.contains(tx, ty)) {
            game.skinManager.toggleSkin()
            return true
        }
        if (btnSecrets.contains(tx, ty)) {
            activeModal = ModalState.SECRETS
            return true
        }
        if (btnSettings.contains(tx, ty)) {
            activeModal = ModalState.SETTINGS
            return true
        }
        if (btnExit.contains(tx, ty)) {
            Gdx.app.exit()
            return true
        }

        return false
    }

    override fun keyDown(keycode: Int): Boolean {
        if (keycode == Input.Keys.ESCAPE) {
            if (activeModal != ModalState.NONE) {
                activeModal = ModalState.NONE
                return true
            } else {
                Gdx.app.exit()
                return true
            }
        }
        if (keycode == Input.Keys.ENTER || keycode == Input.Keys.SPACE) {
            if (activeModal == ModalState.NONE) {
                game.setScreen(GameScreen(game))
                return true
            }
        }
        return false
    }

    override fun resize(width: Int, height: Int) {}
    override fun pause() {}
    override fun resume() {}
    override fun hide() {}
    override fun dispose() {}

    private class FloatingParticle(
        var x: Float,
        var y: Float,
        val size: Float,
        val speedY: Float,
        val alpha: Float
    )
}

