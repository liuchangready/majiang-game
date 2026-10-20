package com.majiang.presentation.menu

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.RoomInfo
import com.majiang.client.api.RoomLifecycle

/**
 * 房间等待与就绪视图组件。
 *
 * 负责四位玩家席位展示、房主标识、就绪标记、准备/取消准备、房主开赛及离房操作。
 * 为纯展示组件，自身不持有网络会话，仅展示父层传入的数据并上报操作意图。
 */
class RoomPreparationView(
    private val onToggleReady: () -> Unit,
    private val onStart: () -> Unit,
    private val onLeave: () -> Unit
) {
    // 交互区域常量 (与基线严格逐像素一致)
    val btnRoomReadyToggle = Rectangle(100f, 130f, 240f, 46f)
    val btnRoomStart = Rectangle(370f, 130f, 240f, 46f)
    val btnRoomLeave = Rectangle(640f, 130f, 240f, 46f)

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont,
        roomInfo: RoomInfo?,
        fallbackRoomId: String,
        isMyReady: Boolean,
        isHost: Boolean,
        statusMessage: String?,
        errorMessage: String?
    ) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 按钮
        MenuViewDraw.drawButton(shape, btnRoomReadyToggle, isPrimary = true)
        if (isHost) {
            MenuViewDraw.drawButton(shape, btnRoomStart, isPrimary = true)
        }
        MenuViewDraw.drawButton(shape, btnRoomLeave, isPrimary = false)

        // 席位面板底色
        shape.color = Color(0.06f, 0.12f, 0.09f, 0.95f)
        shape.rect(100f, 220f, 1080f, 380f)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        val roomId = roomInfo?.roomId ?: fallbackRoomId
        val lifecycle = roomInfo?.lifecycle ?: RoomLifecycle.LOBBY

        font.data.setScale(1.8f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        font.draw(batch, "房间: $roomId", 100f, 680f)

        font.data.setScale(1.2f)
        font.color = Color.LIGHT_GRAY
        font.draw(batch, "状态: $lifecycle", 400f, 680f)

        val members = roomInfo?.members ?: emptyList()
        var seatY = 540f
        font.data.setScale(1.3f)
        for (seat in 0..3) {
            val member = members.firstOrNull { it.seat == seat }
            val seatLabel = when (seat) {
                0 -> "东家 (Seat 0): "
                1 -> "南家 (Seat 1): "
                2 -> "西家 (Seat 2): "
                else -> "北家 (Seat 3): "
            }

            if (member != null) {
                font.color = Color.WHITE
                val hostTag = if (member.isHost) "[房主] " else ""
                val readyTag = if (member.isReady) " [已准备就绪]" else " [等待准备]"
                val readyColor = if (member.isReady) Color.GREEN else Color.YELLOW
                font.draw(batch, seatLabel + hostTag + member.playerName, 140f, seatY)

                font.color = readyColor
                font.draw(batch, readyTag, 650f, seatY)
            } else {
                font.color = Color.GRAY
                font.draw(batch, seatLabel + "[等待玩家加入 / 电脑补位...]", 140f, seatY)
            }
            seatY -= 75f
        }

        // 底部按钮文本
        font.color = Color.WHITE
        MenuViewDraw.drawCenteredText(batch, font, if (isMyReady) "取消就绪" else "准备就绪", btnRoomReadyToggle.x + btnRoomReadyToggle.width / 2f, btnRoomReadyToggle.y + 28f)
        if (isHost) {
            MenuViewDraw.drawCenteredText(batch, font, "开始对局", btnRoomStart.x + btnRoomStart.width / 2f, btnRoomStart.y + 28f)
        }
        MenuViewDraw.drawCenteredText(batch, font, "离开房间", btnRoomLeave.x + btnRoomLeave.width / 2f, btnRoomLeave.y + 28f)

        statusMessage?.let {
            font.color = Color.YELLOW
            MenuViewDraw.drawCenteredText(batch, font, it, 640f, 80f)
        }
        errorMessage?.let {
            font.color = Color.RED
            MenuViewDraw.drawCenteredText(batch, font, it, 640f, 80f)
        }

        batch.end()
    }

    fun handleTouch(tx: Float, ty: Float, isHost: Boolean): Boolean {
        if (btnRoomReadyToggle.contains(tx, ty)) {
            onToggleReady()
            return true
        }
        if (isHost && btnRoomStart.contains(tx, ty)) {
            onStart()
            return true
        }
        if (btnRoomLeave.contains(tx, ty)) {
            onLeave()
            return true
        }
        return false
    }
}
