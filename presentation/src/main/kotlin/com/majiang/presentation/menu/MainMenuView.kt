package com.majiang.presentation.menu

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle

/**
 * 主菜单视图组件。
 *
 * 负责主菜单六大核心入口的渲染与点击命中事件分发。
 * 为纯展示组件，不持有会话与网络状态。
 */
class MainMenuView(
    private val onPlaySingle: () -> Unit,
    private val onOpenLobby: () -> Unit,
    private val onToggleSkin: () -> Unit,
    private val onSecrets: () -> Unit,
    private val onSettings: () -> Unit,
    private val onExit: () -> Unit
) {
    // 按钮交互区域 - 主菜单 (坐标与基线严格逐像素一致)
    val btnPlaySingle = Rectangle(490f, 420f, 300f, 50f)
    val btnOpenLobby = Rectangle(490f, 355f, 300f, 50f)
    val btnSkin = Rectangle(490f, 290f, 300f, 48f)
    val btnSecrets = Rectangle(490f, 225f, 300f, 48f)
    val btnSettings = Rectangle(490f, 160f, 300f, 48f)
    val btnExit = Rectangle(490f, 95f, 300f, 48f)

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont,
        currentSkinName: String,
        statusMessage: String?,
        errorMessage: String?
    ) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        MenuViewDraw.drawButton(shape, btnPlaySingle, isPrimary = true)
        MenuViewDraw.drawButton(shape, btnOpenLobby, isPrimary = true)
        MenuViewDraw.drawButton(shape, btnSkin, isPrimary = false)
        MenuViewDraw.drawButton(shape, btnSecrets, isPrimary = false)
        MenuViewDraw.drawButton(shape, btnSettings, isPrimary = false)
        MenuViewDraw.drawButton(shape, btnExit, isPrimary = false)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        // 标题
        font.data.setScale(2.4f)
        font.color = Color(1.0f, 0.84f, 0.28f, 1f)
        MenuViewDraw.drawCenteredText(batch, font, "千术麻将", 640f, 620f)

        font.data.setScale(1.1f)
        font.color = Color(0.78f, 0.88f, 0.80f, 0.85f)
        MenuViewDraw.drawCenteredText(batch, font, "MAHJONG CHEATER - 雀圣心战与防抓包博弈", 640f, 560f)

        // 按钮文字
        font.data.setScale(1.2f)
        font.color = Color.WHITE
        MenuViewDraw.drawCenteredText(batch, font, "单机对局 (快速开赛)", btnPlaySingle.x + btnPlaySingle.width / 2f, btnPlaySingle.y + 32f)
        MenuViewDraw.drawCenteredText(batch, font, "联机对战 (TCP 大厅)", btnOpenLobby.x + btnOpenLobby.width / 2f, btnOpenLobby.y + 32f)
        MenuViewDraw.drawCenteredText(batch, font, "牌面皮肤: [ $currentSkinName ]", btnSkin.x + btnSkin.width / 2f, btnSkin.y + 30f)
        MenuViewDraw.drawCenteredText(batch, font, "千术绝技秘籍", btnSecrets.x + btnSecrets.width / 2f, btnSecrets.y + 30f)
        MenuViewDraw.drawCenteredText(batch, font, "游戏设置", btnSettings.x + btnSettings.width / 2f, btnSettings.y + 30f)
        MenuViewDraw.drawCenteredText(batch, font, "退出游戏", btnExit.x + btnExit.width / 2f, btnExit.y + 30f)

        statusMessage?.let {
            font.data.setScale(1.0f)
            font.color = Color(0.9f, 0.9f, 0.4f, 1f)
            MenuViewDraw.drawCenteredText(batch, font, it, 640f, 480f)
        }
        errorMessage?.let {
            font.data.setScale(1.0f)
            font.color = Color(1f, 0.4f, 0.4f, 1f)
            MenuViewDraw.drawCenteredText(batch, font, it, 640f, 480f)
        }

        batch.end()
    }

    fun handleTouch(tx: Float, ty: Float): Boolean {
        if (btnPlaySingle.contains(tx, ty)) {
            onPlaySingle()
            return true
        }
        if (btnOpenLobby.contains(tx, ty)) {
            onOpenLobby()
            return true
        }
        if (btnSkin.contains(tx, ty)) {
            onToggleSkin()
            return true
        }
        if (btnSecrets.contains(tx, ty)) {
            onSecrets()
            return true
        }
        if (btnSettings.contains(tx, ty)) {
            onSettings()
            return true
        }
        if (btnExit.contains(tx, ty)) {
            onExit()
            return true
        }
        return false
    }
}
