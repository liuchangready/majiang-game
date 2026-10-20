package com.majiang.presentation.menu

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle

/**
 * TCP 远端服务器连接弹窗视图。
 *
 * 负责主机与端口输入、光标闪烁、键盘字符过滤与连接/取消回调触发。
 * 为纯展示组件，自身持有 HOST 与 PORT 状态，不持有 LobbySession 或网络客户端。
 */
class ConnectDialogView(
    private val onConnect: (host: String, port: Int) -> Unit,
    private val onCancel: () -> Unit
) {
    enum class ActiveInput { NONE, HOST, PORT }

    var activeInput: ActiveInput = ActiveInput.NONE
        private set

    var inputHost: String = "127.0.0.1"
    var inputPort: String = "8080"

    // 交互区域常量 (与基线严格逐像素一致)
    val rectTcpModal = Rectangle(360f, 180f, 560f, 360f)
    val rectHostInput = Rectangle(420f, 390f, 440f, 42f)
    val rectPortInput = Rectangle(420f, 310f, 440f, 42f)
    val btnTcpConnect = Rectangle(420f, 220f, 200f, 44f)
    val btnTcpCancel = Rectangle(660f, 220f, 200f, 44f)

    fun resetActiveInput() {
        activeInput = ActiveInput.NONE
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

        // 弹窗背景
        shape.color = Color(0.08f, 0.16f, 0.12f, 0.98f)
        shape.rect(rectTcpModal.x, rectTcpModal.y, rectTcpModal.width, rectTcpModal.height)

        // 输入框背景
        MenuViewDraw.drawInputBox(shape, rectHostInput, activeInput == ActiveInput.HOST)
        MenuViewDraw.drawInputBox(shape, rectPortInput, activeInput == ActiveInput.PORT)

        // 按钮背景
        MenuViewDraw.drawButton(shape, btnTcpConnect, isPrimary = true)
        MenuViewDraw.drawButton(shape, btnTcpCancel, isPrimary = false)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        font.data.setScale(1.6f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        MenuViewDraw.drawCenteredText(batch, font, "连接远程 TCP 服务器", rectTcpModal.x + rectTcpModal.width / 2f, rectTcpModal.y + 320f)

        font.data.setScale(1.1f)
        font.color = Color.LIGHT_GRAY
        font.draw(batch, "主机地址 (IP / Host):", rectHostInput.x, rectHostInput.y + 60f)
        font.draw(batch, "TCP 端口 (Port):", rectPortInput.x, rectPortInput.y + 60f)

        font.color = Color.WHITE
        font.draw(batch, inputHost + if (activeInput == ActiveInput.HOST && (animTimer % 1f < 0.5f)) "_" else "", rectHostInput.x + 15f, rectHostInput.y + 28f)
        font.draw(batch, inputPort + if (activeInput == ActiveInput.PORT && (animTimer % 1f < 0.5f)) "_" else "", rectPortInput.x + 15f, rectPortInput.y + 28f)

        MenuViewDraw.drawCenteredText(batch, font, "立即连接", btnTcpConnect.x + btnTcpConnect.width / 2f, btnTcpConnect.y + 28f)
        MenuViewDraw.drawCenteredText(batch, font, "返回", btnTcpCancel.x + btnTcpCancel.width / 2f, btnTcpCancel.y + 28f)

        errorMessage?.let {
            font.color = Color.RED
            MenuViewDraw.drawCenteredText(batch, font, it, rectTcpModal.x + rectTcpModal.width / 2f, rectTcpModal.y + 50f)
        }

        batch.end()
    }

    fun handleTouch(tx: Float, ty: Float): Boolean {
        if (rectHostInput.contains(tx, ty)) {
            activeInput = ActiveInput.HOST
            return true
        }
        if (rectPortInput.contains(tx, ty)) {
            activeInput = ActiveInput.PORT
            return true
        }
        if (btnTcpConnect.contains(tx, ty)) {
            val port = inputPort.toIntOrNull() ?: 8080
            onConnect(inputHost, port)
            return true
        }
        if (btnTcpCancel.contains(tx, ty)) {
            onCancel()
            return true
        }
        activeInput = ActiveInput.NONE
        return false
    }

    fun keyTyped(character: Char): Boolean {
        when (activeInput) {
            ActiveInput.HOST -> {
                if (character == '\b') {
                    if (inputHost.isNotEmpty()) inputHost = inputHost.dropLast(1)
                } else if (character in 'a'..'z' || character in 'A'..'Z' || character in '0'..'9' || character == '.' || character == '-') {
                    inputHost += character
                }
                return true
            }
            ActiveInput.PORT -> {
                if (character == '\b') {
                    if (inputPort.isNotEmpty()) inputPort = inputPort.dropLast(1)
                } else if (character in '0'..'9') {
                    if (inputPort.length < 5) inputPort += character
                }
                return true
            }
            ActiveInput.NONE -> {}
        }
        return false
    }
}
