package com.majiang.presentation.menu

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle

/**
 * 输码加入房间弹窗视图。
 *
 * 负责房间号输入、光标闪烁、键盘字符过滤（仅字母与数字，限 12 位）及确认/取消回调。
 * 自身维护 inputRoomId 与输入激活状态。
 */
class JoinRoomDialogView(
    private val onConfirm: (roomId: String) -> Unit,
    private val onCancel: () -> Unit
) {
    var inputRoomId: String = ""
    var isInputActive: Boolean = false

    // 交互区域常量 (与基线严格逐像素一致)
    val rectJoinModal = Rectangle(380f, 220f, 520f, 300f)
    val rectJoinRoomInput = Rectangle(440f, 360f, 400f, 44f)
    val btnJoinConfirm = Rectangle(440f, 270f, 180f, 44f)
    val btnJoinCancel = Rectangle(660f, 270f, 180f, 44f)

    fun activateInput() {
        isInputActive = true
    }

    fun resetInput() {
        inputRoomId = ""
        isInputActive = true
    }

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont,
        animTimer: Float,
        errorMessage: String?
    ) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        shape.color = Color(0.08f, 0.16f, 0.12f, 0.98f)
        shape.rect(rectJoinModal.x, rectJoinModal.y, rectJoinModal.width, rectJoinModal.height)

        MenuViewDraw.drawInputBox(shape, rectJoinRoomInput, isInputActive)
        MenuViewDraw.drawButton(shape, btnJoinConfirm, isPrimary = true)
        MenuViewDraw.drawButton(shape, btnJoinCancel, isPrimary = false)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        font.data.setScale(1.6f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        MenuViewDraw.drawCenteredText(batch, font, "输入房间号加入", rectJoinModal.x + rectJoinModal.width / 2f, rectJoinModal.y + 250f)

        font.data.setScale(1.2f)
        font.color = Color.WHITE
        font.draw(batch, inputRoomId + if (isInputActive && (animTimer % 1f < 0.5f)) "_" else "", rectJoinRoomInput.x + 15f, rectJoinRoomInput.y + 30f)

        MenuViewDraw.drawCenteredText(batch, font, "加入", btnJoinConfirm.x + btnJoinConfirm.width / 2f, btnJoinConfirm.y + 28f)
        MenuViewDraw.drawCenteredText(batch, font, "取消", btnJoinCancel.x + btnJoinCancel.width / 2f, btnJoinCancel.y + 28f)

        errorMessage?.let {
            font.color = Color.RED
            MenuViewDraw.drawCenteredText(batch, font, it, rectJoinModal.x + rectJoinModal.width / 2f, rectJoinModal.y + 40f)
        }

        batch.end()
    }

    fun handleTouch(tx: Float, ty: Float): Boolean {
        if (rectJoinRoomInput.contains(tx, ty)) {
            isInputActive = true
            return true
        }
        if (btnJoinConfirm.contains(tx, ty)) {
            onConfirm(inputRoomId)
            return true
        }
        if (btnJoinCancel.contains(tx, ty)) {
            onCancel()
            return true
        }
        isInputActive = false
        return false
    }

    fun keyTyped(character: Char): Boolean {
        if (!isInputActive) return false
        if (character == '\b') {
            if (inputRoomId.isNotEmpty()) inputRoomId = inputRoomId.dropLast(1)
            return true
        } else if (character.isLetterOrDigit()) {
            if (inputRoomId.length < 12) inputRoomId += character
            return true
        }
        return false
    }
}
