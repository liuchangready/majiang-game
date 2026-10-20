package com.majiang.presentation.menu

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.ConnectionState
import com.majiang.client.api.RoomSummaryView

/**
 * 联机大厅视图组件。
 *
 * 负责大厅左侧导航栏、房间列表面板（最多 8 项）、当前选中项高亮、
 * 快捷加入/观战按钮以及连接状态文案的渲染与触控分发。
 * 为纯展示组件，自身不持有网络会话。
 */
class LobbyListView(
    private val onCreateRoom: () -> Unit,
    private val onJoinById: () -> Unit,
    private val onRefresh: () -> Unit,
    private val onBack: () -> Unit,
    private val onRoomRowSelected: (index: Int) -> Unit,
    private val onJoinSelected: () -> Unit,
    private val onSpectateSelected: () -> Unit
) {
    // 按钮交互区域常量 (与基线严格逐像素一致)
    val btnLobbyCreateRoom = Rectangle(80f, 560f, 220f, 46f)
    val btnLobbyJoinById = Rectangle(80f, 495f, 220f, 46f)
    val btnLobbyRefresh = Rectangle(80f, 430f, 220f, 46f)
    val btnLobbyBack = Rectangle(80f, 120f, 220f, 46f)
    val rectRoomListPanel = Rectangle(330f, 180f, 870f, 440f)
    val btnJoinSelected = Rectangle(770f, 110f, 200f, 46f)
    val btnSpectateSelected = Rectangle(1000f, 110f, 200f, 46f)

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont,
        connectionState: ConnectionState,
        roomList: List<RoomSummaryView>,
        selectedRoom: RoomSummaryView?,
        statusMessage: String?,
        errorMessage: String?
    ) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 按钮栏
        MenuViewDraw.drawButton(shape, btnLobbyCreateRoom, isPrimary = true)
        MenuViewDraw.drawButton(shape, btnLobbyJoinById, isPrimary = false)
        MenuViewDraw.drawButton(shape, btnLobbyRefresh, isPrimary = false)
        MenuViewDraw.drawButton(shape, btnLobbyBack, isPrimary = false)

        // 房间列表面板背景
        shape.color = Color(0.06f, 0.12f, 0.09f, 0.95f)
        shape.rect(rectRoomListPanel.x, rectRoomListPanel.y, rectRoomListPanel.width, rectRoomListPanel.height)

        // 若有选中项，高亮操作按钮
        if (selectedRoom != null) {
            MenuViewDraw.drawButton(shape, btnJoinSelected, isPrimary = true)
            MenuViewDraw.drawButton(shape, btnSpectateSelected, isPrimary = false)
        }

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        // 顶栏状态
        font.data.setScale(1.6f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        font.draw(batch, "雀圣联机大厅", 80f, 680f)

        font.data.setScale(1.1f)
        font.color = Color.LIGHT_GRAY
        val connText = when (connectionState) {
            ConnectionState.CONNECTED -> "已连接"
            ConnectionState.CONNECTING -> "正在连接..."
            ConnectionState.RECONNECTING -> "正在重连..."
            ConnectionState.CLOSED -> "未连接"
        }
        font.draw(batch, "连接状态: $connText ($connectionState)", 330f, 680f)

        // 左侧操作按钮文本
        font.color = Color.WHITE
        MenuViewDraw.drawCenteredText(batch, font, "创建房间", btnLobbyCreateRoom.x + btnLobbyCreateRoom.width / 2f, btnLobbyCreateRoom.y + 28f)
        MenuViewDraw.drawCenteredText(batch, font, "输入房号加入", btnLobbyJoinById.x + btnLobbyJoinById.width / 2f, btnLobbyJoinById.y + 28f)
        MenuViewDraw.drawCenteredText(batch, font, "刷新列表", btnLobbyRefresh.x + btnLobbyRefresh.width / 2f, btnLobbyRefresh.y + 28f)
        MenuViewDraw.drawCenteredText(batch, font, "断开返回", btnLobbyBack.x + btnLobbyBack.width / 2f, btnLobbyBack.y + 28f)

        // 房间列表表头
        var listY = rectRoomListPanel.y + rectRoomListPanel.height - 30f
        font.color = Color(0.8f, 0.8f, 0.5f, 1f)
        font.draw(batch, "房号", rectRoomListPanel.x + 30f, listY)
        font.draw(batch, "房主", rectRoomListPanel.x + 180f, listY)
        font.draw(batch, "人数", rectRoomListPanel.x + 380f, listY)
        font.draw(batch, "状态", rectRoomListPanel.x + 520f, listY)
        font.draw(batch, "赛制规则", rectRoomListPanel.x + 660f, listY)

        listY -= 35f
        font.color = Color.WHITE
        if (roomList.isEmpty()) {
            font.color = Color.GRAY
            font.draw(batch, "暂无活跃房间，请点击“创建房间”发起对战", rectRoomListPanel.x + 180f, listY - 50f)
        } else {
            for (room in roomList.take(8)) {
                val isSelected = selectedRoom?.roomId == room.roomId
                if (isSelected) {
                    font.color = Color(1.0f, 0.9f, 0.4f, 1f)
                    font.draw(batch, "▶", rectRoomListPanel.x + 10f, listY)
                } else {
                    font.color = Color.WHITE
                }
                font.draw(batch, room.roomId, rectRoomListPanel.x + 30f, listY)
                font.draw(batch, room.hostPlayerName, rectRoomListPanel.x + 180f, listY)
                font.draw(batch, "${room.playerCount} / 4", rectRoomListPanel.x + 380f, listY)
                font.draw(batch, if (room.matchStarted) "对局中" else "招募中", rectRoomListPanel.x + 520f, listY)
                font.draw(batch, if (room.configSummary.isNotBlank()) room.configSummary else "半庄/赤五/码牌", rectRoomListPanel.x + 660f, listY)
                listY -= 40f
            }
        }

        if (selectedRoom != null) {
            font.color = Color.WHITE
            MenuViewDraw.drawCenteredText(batch, font, "加入该房 (${selectedRoom.roomId})", btnJoinSelected.x + btnJoinSelected.width / 2f, btnJoinSelected.y + 28f)
            MenuViewDraw.drawCenteredText(batch, font, "观战", btnSpectateSelected.x + btnSpectateSelected.width / 2f, btnSpectateSelected.y + 28f)
        }

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

    fun handleTouch(
        tx: Float,
        ty: Float,
        roomList: List<RoomSummaryView>,
        hasSelectedRoom: Boolean
    ): Boolean {
        if (btnLobbyCreateRoom.contains(tx, ty)) {
            onCreateRoom()
            return true
        }
        if (btnLobbyJoinById.contains(tx, ty)) {
            onJoinById()
            return true
        }
        if (btnLobbyRefresh.contains(tx, ty)) {
            onRefresh()
            return true
        }
        if (btnLobbyBack.contains(tx, ty)) {
            onBack()
            return true
        }

        // 房间列表项点击
        if (rectRoomListPanel.contains(tx, ty)) {
            var rowY = rectRoomListPanel.y + rectRoomListPanel.height - 65f
            for ((index, _) in roomList.take(8).withIndex()) {
                if (ty in (rowY - 20f)..(rowY + 20f)) {
                    onRoomRowSelected(index)
                    return true
                }
                rowY -= 40f
            }
        }

        if (hasSelectedRoom) {
            if (btnJoinSelected.contains(tx, ty)) {
                onJoinSelected()
                return true
            }
            if (btnSpectateSelected.contains(tx, ty)) {
                onSpectateSelected()
                return true
            }
        }
        return false
    }
}
