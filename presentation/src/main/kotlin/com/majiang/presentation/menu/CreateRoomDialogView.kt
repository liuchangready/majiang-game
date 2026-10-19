package com.majiang.presentation.menu

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle

/**
 * 创建比赛房间规则配置弹窗视图。
 *
 * 负责半庄/东风、赤宝牌规则说明、码牌开关的状态维护与确认/取消回调。
 * 本组件自身维护配置开关，确认时将最终布尔值上报父层构造 MatchConfig。
 */
class CreateRoomDialogView(
    private val onConfirm: (isHanchan: Boolean, shuffleEnabled: Boolean) -> Unit,
    private val onCancel: () -> Unit
) {
    var cfgIsHanchan: Boolean = true
    var cfgRedFive: Boolean = true
    var cfgShuffleCheating: Boolean = true

    // 交互区域常量 (与基线严格逐像素一致)
    val rectCreateModal = Rectangle(320f, 140f, 640f, 460f)
    val btnCfgWind = Rectangle(380f, 430f, 520f, 42f)
    val btnCfgRedFive = Rectangle(380f, 365f, 520f, 42f)
    val btnCfgShuffle = Rectangle(380f, 300f, 520f, 42f)
    val btnCreateConfirm = Rectangle(380f, 190f, 240f, 46f)
    val btnCreateCancel = Rectangle(660f, 190f, 240f, 46f)

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont
    ) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        shape.color = Color(0.08f, 0.16f, 0.12f, 0.98f)
        shape.rect(rectCreateModal.x, rectCreateModal.y, rectCreateModal.width, rectCreateModal.height)

        MenuViewDraw.drawButton(shape, btnCfgWind, isPrimary = false)
        MenuViewDraw.drawButton(shape, btnCfgRedFive, isPrimary = false)
        MenuViewDraw.drawButton(shape, btnCfgShuffle, isPrimary = false)
        MenuViewDraw.drawButton(shape, btnCreateConfirm, isPrimary = true)
        MenuViewDraw.drawButton(shape, btnCreateCancel, isPrimary = false)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        font.data.setScale(1.6f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        MenuViewDraw.drawCenteredText(batch, font, "创建比赛房间规则配置", rectCreateModal.x + rectCreateModal.width / 2f, rectCreateModal.y + 420f)

        font.data.setScale(1.2f)
        font.color = Color.WHITE
        MenuViewDraw.drawCenteredText(batch, font, "对局长度: ${if (cfgIsHanchan) "半庄战 (东南战)" else "东风战 (东场战)"}  [点击切换]", btnCfgWind.x + btnCfgWind.width / 2f, btnCfgWind.y + 27f)
        MenuViewDraw.drawCenteredText(batch, font, "赤宝牌规则: 权威默认固定开启 (3张赤五标准)", btnCfgRedFive.x + btnCfgRedFive.width / 2f, btnCfgRedFive.y + 27f)
        MenuViewDraw.drawCenteredText(batch, font, "千术绝技码牌: ${if (cfgShuffleCheating) "开启 (做牌准备阶段)" else "关闭"}  [点击切换]", btnCfgShuffle.x + btnCfgShuffle.width / 2f, btnCfgShuffle.y + 27f)

        MenuViewDraw.drawCenteredText(batch, font, "确认创建房间", btnCreateConfirm.x + btnCreateConfirm.width / 2f, btnCreateConfirm.y + 28f)
        MenuViewDraw.drawCenteredText(batch, font, "取消", btnCreateCancel.x + btnCreateCancel.width / 2f, btnCreateCancel.y + 28f)

        batch.end()
    }

    fun handleTouch(tx: Float, ty: Float): Boolean {
        if (btnCfgWind.contains(tx, ty)) {
            cfgIsHanchan = !cfgIsHanchan
            return true
        }
        if (btnCfgShuffle.contains(tx, ty)) {
            cfgShuffleCheating = !cfgShuffleCheating
            return true
        }
        if (btnCreateConfirm.contains(tx, ty)) {
            onConfirm(cfgIsHanchan, cfgShuffleCheating)
            return true
        }
        if (btnCreateCancel.contains(tx, ty)) {
            onCancel()
            return true
        }
        return false
    }
}
