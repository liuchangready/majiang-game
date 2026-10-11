package com.majiang.presentation.menu

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
import com.badlogic.gdx.math.MathUtils
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.math.Vector3
import com.majiang.client.api.ConnectionState
import com.majiang.client.api.CheatEconomyConfig
import com.majiang.client.api.LobbySession
import com.majiang.client.api.MatchConfig
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.RoomInfo
import com.majiang.client.api.RoomLifecycle
import com.majiang.client.api.RoomSummaryView
import com.majiang.client.api.ShuffleConfig
import com.majiang.client.api.TileMarkConfig
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets

/**
 * 千术麻将主菜单与大厅场景 (MenuScreen)。
 *
 * 遵循 V4 架构契约与 TASK-ANTI-R5ROOT 规范：
 * 1. 仅依赖 [UiAssets]、[TableNavigation] 与 [LobbySession] 门面接口，严禁 import engine、protocol 或 server 实现；
 * 2. 支持单机对局启动、远端 UDP 地址端口配置与连接；
 * 3. 大厅期功能完整支持：房间列表拉取与刷新、赛制规则配置、创建房间、加入房间、房间详情与就绪、开赛与观战接入；
 * 4. 菜单开赛直接通过 [LobbySession.start] 发起，不向玩家画面借动作；
 * 5. 全中文展示通过共享 NotoSansSC 字体渲染，无内部组件名泄露；
 * 6. 支持键盘与触控多端输入，画面退出时幂等注销监听器。
 */
class MenuScreen(
    val assets: UiAssets,
    val navigation: TableNavigation,
    initialLobby: LobbySession? = null
) : Screen, InputAdapter() {

    private val camera = OrthographicCamera().apply {
        setToOrtho(false, 1280f, 720f)
    }

    private val unprojectVec = Vector3()
    private val glyphLayout = GlyphLayout()

    // 屏幕生命周期与请求代次守卫
    @Volatile private var isDisposed = false
    private var requestGeneration = 0L

    // 席位与角色跟踪
    private enum class ClientRole { NONE, PLAYER, SPECTATOR }
    private var myRole = ClientRole.NONE
    private var myViewerSeat: Int? = null
    private var hasNavigatedToTable: Boolean = false

    // 活跃大厅会话引用与订阅
    var activeLobby: LobbySession? = initialLobby
        private set
    private var lobbyStateSub: AutoCloseable? = null
    private var roomUpdateSub: AutoCloseable? = null

    // 状态机
    enum class MenuState {
        MAIN,
        UDP_CONNECT_DIALOG,
        CONNECTING,
        LOBBY,
        CREATE_ROOM_DIALOG,
        JOIN_ROOM_DIALOG,
        ROOM,
        SECRETS_MODAL,
        SETTINGS_MODAL
    }

    var currentState: MenuState = if (initialLobby != null) MenuState.LOBBY else MenuState.MAIN
        private set

    // 状态提示与错误信息
    private var statusMessage: String? = null
    var errorMessage: String? = null
        private set

    // 大厅数据
    private var roomList: List<RoomSummaryView> = emptyList()
    private var selectedRoom: RoomSummaryView? = null
    private var currentRoomInfo: RoomInfo? = null
    private var isMyReady: Boolean = false
    private var isHost: Boolean = false

    // 输入框状态
    private enum class ActiveInput { NONE, HOST, PORT, ROOM_ID }
    private var activeInput: ActiveInput = ActiveInput.NONE
    private var inputHost: String = "127.0.0.1"
    private var inputPort: String = "8080"
    private var inputRoomId: String = ""

    // 建房规则配置
    private var cfgIsHanchan: Boolean = true
    private var cfgRedFive: Boolean = true
    private var cfgShuffleCheating: Boolean = true

    // 按钮交互区域 - 主菜单
    private val btnPlaySingle = Rectangle(490f, 420f, 300f, 50f)
    private val btnOpenLobby = Rectangle(490f, 355f, 300f, 50f)
    private val btnSkin = Rectangle(490f, 290f, 300f, 48f)
    private val btnSecrets = Rectangle(490f, 225f, 300f, 48f)
    private val btnSettings = Rectangle(490f, 160f, 300f, 48f)
    private val btnExit = Rectangle(490f, 95f, 300f, 48f)

    // 按钮交互区域 - UDP 连接弹窗
    private val rectUdpModal = Rectangle(360f, 180f, 560f, 360f)
    private val rectHostInput = Rectangle(420f, 390f, 440f, 42f)
    private val rectPortInput = Rectangle(420f, 310f, 440f, 42f)
    private val btnUdpConnect = Rectangle(420f, 220f, 200f, 44f)
    private val btnUdpCancel = Rectangle(660f, 220f, 200f, 44f)

    // 按钮交互区域 - 大厅
    private val btnLobbyCreateRoom = Rectangle(80f, 560f, 220f, 46f)
    private val btnLobbyJoinById = Rectangle(80f, 495f, 220f, 46f)
    private val btnLobbyRefresh = Rectangle(80f, 430f, 220f, 46f)
    private val btnLobbyBack = Rectangle(80f, 120f, 220f, 46f)
    private val rectRoomListPanel = Rectangle(330f, 180f, 870f, 440f)
    private val btnJoinSelected = Rectangle(770f, 110f, 200f, 46f)
    private val btnSpectateSelected = Rectangle(1000f, 110f, 200f, 46f)

    // 按钮交互区域 - 建房弹窗
    private val rectCreateModal = Rectangle(320f, 140f, 640f, 460f)
    private val btnCfgWind = Rectangle(380f, 430f, 520f, 42f)
    private val btnCfgRedFive = Rectangle(380f, 365f, 520f, 42f)
    private val btnCfgShuffle = Rectangle(380f, 300f, 520f, 42f)
    private val btnCreateConfirm = Rectangle(380f, 190f, 240f, 46f)
    private val btnCreateCancel = Rectangle(660f, 190f, 240f, 46f)

    // 按钮交互区域 - 输码加入弹窗
    private val rectJoinModal = Rectangle(380f, 220f, 520f, 300f)
    private val rectJoinRoomInput = Rectangle(440f, 360f, 400f, 44f)
    private val btnJoinConfirm = Rectangle(440f, 270f, 180f, 44f)
    private val btnJoinCancel = Rectangle(660f, 270f, 180f, 44f)

    // 按钮交互区域 - 房间等待
    private val btnRoomReadyToggle = Rectangle(100f, 130f, 240f, 46f)
    private val btnRoomStart = Rectangle(370f, 130f, 240f, 46f)
    private val btnRoomLeave = Rectangle(640f, 130f, 240f, 46f)

    // 按钮交互区域 - 通用弹窗
    private val modalRect = Rectangle(260f, 110f, 760f, 500f)
    private val btnModalClose = Rectangle(570f, 135f, 140f, 42f)

    // 装饰粒子与动画计时
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

    init {
        initialLobby?.let { attachLobby(it) }
    }

    private fun runOnUi(expectedGen: Long? = null, action: () -> Unit) {
        if (isDisposed) return
        val task = Runnable {
            if (isDisposed) return@Runnable
            if (expectedGen != null && expectedGen != requestGeneration) return@Runnable
            action()
        }
        val app = Gdx.app
        if (app != null) {
            app.postRunnable(task)
        } else {
            task.run()
        }
    }

    private fun attachLobby(lobby: LobbySession) {
        activeLobby = lobby
        lobbyStateSub?.close()
        lobbyStateSub = lobby.onStateChange { state ->
            runOnUi {
                if (activeLobby !== lobby) return@runOnUi
                if (state == ConnectionState.CLOSED) {
                    errorMessage = "连接已关闭: ${lobby.disconnectReason ?: "远程连接断开"}"
                }
            }
        }
        roomUpdateSub?.close()
        roomUpdateSub = lobby.onRoomUpdate { info ->
            runOnUi {
                if (activeLobby !== lobby) return@runOnUi
                onRoomInfoUpdated(info)
            }
        }
    }

    private fun detachLobby() {
        lobbyStateSub?.close()
        lobbyStateSub = null
        roomUpdateSub?.close()
        roomUpdateSub = null
        activeLobby = null
    }

    override fun show() {
        Gdx.input.inputProcessor = this
    }

    override fun render(delta: Float) {
        animTimer += delta
        updateParticles(delta)

        camera.update()
        Gdx.gl.glClearColor(0.04f, 0.08f, 0.06f, 1f)
        Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT)

        val shape = assets.shapeRenderer
        val batch = assets.batch
        val font = assets.effectiveFont

        renderBackground(shape)

        when (currentState) {
            MenuState.MAIN -> renderMainMenu(shape, batch, font)
            MenuState.UDP_CONNECT_DIALOG -> renderUdpConnectDialog(shape, batch, font)
            MenuState.CONNECTING -> renderConnectingScreen(shape, batch, font)
            MenuState.LOBBY -> renderLobbyScreen(shape, batch, font)
            MenuState.CREATE_ROOM_DIALOG -> renderCreateRoomDialog(shape, batch, font)
            MenuState.JOIN_ROOM_DIALOG -> renderJoinRoomDialog(shape, batch, font)
            MenuState.ROOM -> renderRoomScreen(shape, batch, font)
            MenuState.SECRETS_MODAL -> renderSecretsModal(shape, batch, font)
            MenuState.SETTINGS_MODAL -> renderSettingsModal(shape, batch, font)
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

        // 金色微粒
        for (p in particles) {
            shape.color = Color(0.95f, 0.85f, 0.40f, p.alpha * (0.6f + 0.4f * MathUtils.sin(animTimer * 2f + p.x)))
            shape.circle(p.x, p.y, p.size)
        }

        shape.end()
    }

    // ==========================================
    // 主菜单视图渲染
    // ==========================================

    private fun renderMainMenu(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        drawButton(shape, btnPlaySingle, isPrimary = true)
        drawButton(shape, btnOpenLobby, isPrimary = true)
        drawButton(shape, btnSkin, isPrimary = false)
        drawButton(shape, btnSecrets, isPrimary = false)
        drawButton(shape, btnSettings, isPrimary = false)
        drawButton(shape, btnExit, isPrimary = false)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        // 标题
        font.data.setScale(2.4f)
        font.color = Color(1.0f, 0.84f, 0.28f, 1f)
        drawCenteredText(batch, font, "千术麻将", 640f, 620f)

        font.data.setScale(1.1f)
        font.color = Color(0.78f, 0.88f, 0.80f, 0.85f)
        drawCenteredText(batch, font, "MAHJONG CHEATER - 雀圣心战与防抓包博弈", 640f, 560f)

        // 按钮文字
        font.data.setScale(1.2f)
        font.color = Color.WHITE
        drawCenteredText(batch, font, "单机对局 (快速开赛)", btnPlaySingle.x + btnPlaySingle.width / 2f, btnPlaySingle.y + 32f)
        drawCenteredText(batch, font, "联机对战 (UDP 大厅)", btnOpenLobby.x + btnOpenLobby.width / 2f, btnOpenLobby.y + 32f)
        drawCenteredText(batch, font, "牌面皮肤: [ ${assets.skinManager.currentSkinId.name} ]", btnSkin.x + btnSkin.width / 2f, btnSkin.y + 30f)
        drawCenteredText(batch, font, "千术绝技秘籍", btnSecrets.x + btnSecrets.width / 2f, btnSecrets.y + 30f)
        drawCenteredText(batch, font, "游戏设置", btnSettings.x + btnSettings.width / 2f, btnSettings.y + 30f)
        drawCenteredText(batch, font, "退出游戏", btnExit.x + btnExit.width / 2f, btnExit.y + 30f)

        statusMessage?.let {
            font.data.setScale(1.0f)
            font.color = Color(0.9f, 0.9f, 0.4f, 1f)
            drawCenteredText(batch, font, it, 640f, 480f)
        }
        errorMessage?.let {
            font.data.setScale(1.0f)
            font.color = Color(1f, 0.4f, 0.4f, 1f)
            drawCenteredText(batch, font, it, 640f, 480f)
        }

        batch.end()
    }

    // ==========================================
    // UDP 连接弹窗渲染
    // ==========================================

    private fun renderUdpConnectDialog(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 弹窗背景
        shape.color = Color(0.08f, 0.16f, 0.12f, 0.98f)
        shape.rect(rectUdpModal.x, rectUdpModal.y, rectUdpModal.width, rectUdpModal.height)

        // 输入框背景
        drawInputBox(shape, rectHostInput, activeInput == ActiveInput.HOST)
        drawInputBox(shape, rectPortInput, activeInput == ActiveInput.PORT)

        // 按钮背景
        drawButton(shape, btnUdpConnect, isPrimary = true)
        drawButton(shape, btnUdpCancel, isPrimary = false)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        font.data.setScale(1.6f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        drawCenteredText(batch, font, "连接远程 UDP 服务器", rectUdpModal.x + rectUdpModal.width / 2f, rectUdpModal.y + 320f)

        font.data.setScale(1.1f)
        font.color = Color.LIGHT_GRAY
        font.draw(batch, "主机地址 (IP / Host):", rectHostInput.x, rectHostInput.y + 60f)
        font.draw(batch, "UDP 端口 (Port):", rectPortInput.x, rectPortInput.y + 60f)

        font.color = Color.WHITE
        font.draw(batch, inputHost + if (activeInput == ActiveInput.HOST && (animTimer % 1f < 0.5f)) "_" else "", rectHostInput.x + 15f, rectHostInput.y + 28f)
        font.draw(batch, inputPort + if (activeInput == ActiveInput.PORT && (animTimer % 1f < 0.5f)) "_" else "", rectPortInput.x + 15f, rectPortInput.y + 28f)

        drawCenteredText(batch, font, "立即连接", btnUdpConnect.x + btnUdpConnect.width / 2f, btnUdpConnect.y + 28f)
        drawCenteredText(batch, font, "返回", btnUdpCancel.x + btnUdpCancel.width / 2f, btnUdpCancel.y + 28f)

        errorMessage?.let {
            font.color = Color.RED
            drawCenteredText(batch, font, it, rectUdpModal.x + rectUdpModal.width / 2f, rectUdpModal.y + 50f)
        }

        batch.end()
    }

    private fun renderConnectingScreen(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        batch.projectionMatrix = camera.combined
        batch.begin()
        font.data.setScale(1.5f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        drawCenteredText(batch, font, "正在连接到 $inputHost:$inputPort ...", 640f, 380f)
        batch.end()
    }

    // ==========================================
    // 大厅视图渲染
    // ==========================================

    private fun renderLobbyScreen(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 按钮栏
        drawButton(shape, btnLobbyCreateRoom, isPrimary = true)
        drawButton(shape, btnLobbyJoinById, isPrimary = false)
        drawButton(shape, btnLobbyRefresh, isPrimary = false)
        drawButton(shape, btnLobbyBack, isPrimary = false)

        // 房间列表面板背景
        shape.color = Color(0.06f, 0.12f, 0.09f, 0.95f)
        shape.rect(rectRoomListPanel.x, rectRoomListPanel.y, rectRoomListPanel.width, rectRoomListPanel.height)

        // 若有选中项，高亮操作按钮
        if (selectedRoom != null) {
            drawButton(shape, btnJoinSelected, isPrimary = true)
            drawButton(shape, btnSpectateSelected, isPrimary = false)
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
        val connState = activeLobby?.connectionState ?: ConnectionState.CLOSED
        val connText = when (connState) {
            ConnectionState.CONNECTED -> "已连接"
            ConnectionState.CONNECTING -> "正在连接..."
            ConnectionState.RECONNECTING -> "正在重连..."
            ConnectionState.CLOSED -> "未连接"
        }
        font.draw(batch, "连接状态: $connText ($connState)", 330f, 680f)

        // 左侧操作按钮文本
        font.color = Color.WHITE
        drawCenteredText(batch, font, "创建房间", btnLobbyCreateRoom.x + btnLobbyCreateRoom.width / 2f, btnLobbyCreateRoom.y + 28f)
        drawCenteredText(batch, font, "输入房号加入", btnLobbyJoinById.x + btnLobbyJoinById.width / 2f, btnLobbyJoinById.y + 28f)
        drawCenteredText(batch, font, "刷新列表", btnLobbyRefresh.x + btnLobbyRefresh.width / 2f, btnLobbyRefresh.y + 28f)
        drawCenteredText(batch, font, "断开返回", btnLobbyBack.x + btnLobbyBack.width / 2f, btnLobbyBack.y + 28f)

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
            drawCenteredText(batch, font, "加入该房 (${selectedRoom?.roomId})", btnJoinSelected.x + btnJoinSelected.width / 2f, btnJoinSelected.y + 28f)
            drawCenteredText(batch, font, "观战", btnSpectateSelected.x + btnSpectateSelected.width / 2f, btnSpectateSelected.y + 28f)
        }

        statusMessage?.let {
            font.color = Color.YELLOW
            drawCenteredText(batch, font, it, 640f, 80f)
        }
        errorMessage?.let {
            font.color = Color.RED
            drawCenteredText(batch, font, it, 640f, 80f)
        }

        batch.end()
    }

    // ==========================================
    // 创建房间规则配置弹窗渲染
    // ==========================================

    private fun renderCreateRoomDialog(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        shape.color = Color(0.08f, 0.16f, 0.12f, 0.98f)
        shape.rect(rectCreateModal.x, rectCreateModal.y, rectCreateModal.width, rectCreateModal.height)

        drawButton(shape, btnCfgWind, isPrimary = false)
        drawButton(shape, btnCfgRedFive, isPrimary = false)
        drawButton(shape, btnCfgShuffle, isPrimary = false)
        drawButton(shape, btnCreateConfirm, isPrimary = true)
        drawButton(shape, btnCreateCancel, isPrimary = false)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        font.data.setScale(1.6f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        drawCenteredText(batch, font, "创建比赛房间规则配置", rectCreateModal.x + rectCreateModal.width / 2f, rectCreateModal.y + 420f)

        font.data.setScale(1.2f)
        font.color = Color.WHITE
        drawCenteredText(batch, font, "对局长度: ${if (cfgIsHanchan) "半庄战 (东南战)" else "东风战 (东场战)"}  [点击切换]", btnCfgWind.x + btnCfgWind.width / 2f, btnCfgWind.y + 27f)
        drawCenteredText(batch, font, "赤宝牌规则: 权威默认固定开启 (3张赤五标准)", btnCfgRedFive.x + btnCfgRedFive.width / 2f, btnCfgRedFive.y + 27f)
        drawCenteredText(batch, font, "千术绝技码牌: ${if (cfgShuffleCheating) "开启 (做牌准备阶段)" else "关闭"}  [点击切换]", btnCfgShuffle.x + btnCfgShuffle.width / 2f, btnCfgShuffle.y + 27f)

        drawCenteredText(batch, font, "确认创建房间", btnCreateConfirm.x + btnCreateConfirm.width / 2f, btnCreateConfirm.y + 28f)
        drawCenteredText(batch, font, "取消", btnCreateCancel.x + btnCreateCancel.width / 2f, btnCreateCancel.y + 28f)

        batch.end()
    }

    // ==========================================
    // 输码加入弹窗渲染
    // ==========================================

    private fun renderJoinRoomDialog(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        shape.color = Color(0.08f, 0.16f, 0.12f, 0.98f)
        shape.rect(rectJoinModal.x, rectJoinModal.y, rectJoinModal.width, rectJoinModal.height)

        drawInputBox(shape, rectJoinRoomInput, activeInput == ActiveInput.ROOM_ID)
        drawButton(shape, btnJoinConfirm, isPrimary = true)
        drawButton(shape, btnJoinCancel, isPrimary = false)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        font.data.setScale(1.6f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        drawCenteredText(batch, font, "输入房间号加入", rectJoinModal.x + rectJoinModal.width / 2f, rectJoinModal.y + 250f)

        font.data.setScale(1.2f)
        font.color = Color.WHITE
        font.draw(batch, inputRoomId + if (activeInput == ActiveInput.ROOM_ID && (animTimer % 1f < 0.5f)) "_" else "", rectJoinRoomInput.x + 15f, rectJoinRoomInput.y + 30f)

        drawCenteredText(batch, font, "加入", btnJoinConfirm.x + btnJoinConfirm.width / 2f, btnJoinConfirm.y + 28f)
        drawCenteredText(batch, font, "取消", btnJoinCancel.x + btnJoinCancel.width / 2f, btnJoinCancel.y + 28f)

        errorMessage?.let {
            font.color = Color.RED
            drawCenteredText(batch, font, it, rectJoinModal.x + rectJoinModal.width / 2f, rectJoinModal.y + 40f)
        }

        batch.end()
    }

    // ==========================================
    // 房间等待视图渲染
    // ==========================================

    private fun renderRoomScreen(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 按钮
        drawButton(shape, btnRoomReadyToggle, isPrimary = true)
        if (isHost) {
            drawButton(shape, btnRoomStart, isPrimary = true)
        }
        drawButton(shape, btnRoomLeave, isPrimary = false)

        // 席位面板底色
        shape.color = Color(0.06f, 0.12f, 0.09f, 0.95f)
        shape.rect(100f, 220f, 1080f, 380f)

        shape.end()

        batch.projectionMatrix = camera.combined
        batch.begin()

        val info = currentRoomInfo
        val roomId = info?.roomId ?: inputRoomId
        val lifecycle = info?.lifecycle ?: RoomLifecycle.LOBBY

        font.data.setScale(1.8f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        font.draw(batch, "房间: $roomId", 100f, 680f)

        font.data.setScale(1.2f)
        font.color = Color.LIGHT_GRAY
        font.draw(batch, "状态: $lifecycle", 400f, 680f)

        val members = info?.members ?: emptyList()
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
        drawCenteredText(batch, font, if (isMyReady) "取消就绪" else "准备就绪", btnRoomReadyToggle.x + btnRoomReadyToggle.width / 2f, btnRoomReadyToggle.y + 28f)
        if (isHost) {
            drawCenteredText(batch, font, "开始对局", btnRoomStart.x + btnRoomStart.width / 2f, btnRoomStart.y + 28f)
        }
        drawCenteredText(batch, font, "离开房间", btnRoomLeave.x + btnRoomLeave.width / 2f, btnRoomLeave.y + 28f)

        statusMessage?.let {
            font.color = Color.YELLOW
            drawCenteredText(batch, font, it, 640f, 80f)
        }
        errorMessage?.let {
            font.color = Color.RED
            drawCenteredText(batch, font, it, 640f, 80f)
        }

        batch.end()
    }

    // ==========================================
    // 千术秘籍与设置弹窗
    // ==========================================

    private fun renderSecretsModal(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        renderModalBase(shape, batch, font)
        batch.begin()

        font.data.setScale(1.8f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        drawCenteredText(batch, font, "-- 雀圣千术绝技秘籍 --", modalRect.x + modalRect.width / 2f, modalRect.y + 460f)

        font.data.setScale(1.1f)
        font.color = Color.WHITE
        font.draw(batch, "1. 落汗绝技: 摸牌/出牌决策时，选择手牌中 4 张不同实体牌进行微痕标记。", modalRect.x + 60f, modalRect.y + 390f)
        font.draw(batch, "2. 千术消耗: 每次落汗消耗 10 额度点数，每名玩家最多同时维持 12 张标记牌。", modalRect.x + 60f, modalRect.y + 330f)
        font.draw(batch, "3. 有效期限: 当局立即生效，另持续后续 3 局；思考超时仅禁止新施术。", modalRect.x + 60f, modalRect.y + 270f)
        font.draw(batch, "4. 洞察识别: 标记牌进入对手手牌时可识别牌面，打入牌河时精准定位标识。", modalRect.x + 60f, modalRect.y + 210f)

        font.color = Color(0.95f, 0.45f, 0.45f, 1f)
        font.draw(batch, "注意: 立直后、非本人回合或思考超时禁止施展落汗，请选准时机！", modalRect.x + 60f, modalRect.y + 150f)

        font.color = Color.WHITE
        drawCenteredText(batch, font, "关闭 [X]", btnModalClose.x + btnModalClose.width / 2f, btnModalClose.y + 26f)

        batch.end()
    }

    private fun renderSettingsModal(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        renderModalBase(shape, batch, font)
        batch.begin()

        font.data.setScale(1.8f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        drawCenteredText(batch, font, "-- 游戏设置 --", modalRect.x + modalRect.width / 2f, modalRect.y + 450f)

        font.data.setScale(1.2f)
        font.color = Color.WHITE
        font.draw(batch, "牌面皮肤     : [ ${assets.skinManager.currentSkinId.name} (点击切换) ]", modalRect.x + 80f, modalRect.y + 370f)
        font.draw(batch, "操作模式     : PC 鼠标拖拽点击 / 移动端触控手势", modalRect.x + 80f, modalRect.y + 310f)
        font.draw(batch, "对局思考时钟 : 20秒每局储备 + 3秒每次思考缓冲", modalRect.x + 80f, modalRect.y + 250f)
        font.draw(batch, "网络通信架构 : 本地进程内权威服务 / 远端 UDP 协议", modalRect.x + 80f, modalRect.y + 190f)

        drawCenteredText(batch, font, "关闭 [X]", btnModalClose.x + btnModalClose.width / 2f, btnModalClose.y + 26f)

        batch.end()
    }

    private fun renderModalBase(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        shape.color = Color(0f, 0f, 0f, 0.75f)
        shape.rect(0f, 0f, 1280f, 720f)

        shape.color = Color(0.08f, 0.16f, 0.12f, 0.98f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        drawButton(shape, btnModalClose, isPrimary = false)

        shape.end()
    }

    // ==========================================
    // 渲染辅助函数
    // ==========================================

    private fun drawButton(shape: ShapeRenderer, rect: Rectangle, isPrimary: Boolean) {
        if (isPrimary) {
            shape.color = Color(0.82f, 0.22f, 0.15f, 1f)
        } else {
            shape.color = Color(0.12f, 0.24f, 0.18f, 1f)
        }
        shape.rect(rect.x, rect.y, rect.width, rect.height)
    }

    private fun drawInputBox(shape: ShapeRenderer, rect: Rectangle, isActive: Boolean) {
        shape.color = if (isActive) Color(0.15f, 0.35f, 0.25f, 1f) else Color(0.08f, 0.18f, 0.12f, 1f)
        shape.rect(rect.x, rect.y, rect.width, rect.height)
    }

    private fun drawCenteredText(batch: SpriteBatch, font: BitmapFont, text: String, cx: Float, cy: Float) {
        glyphLayout.setText(font, text)
        font.draw(batch, text, cx - glyphLayout.width / 2f, cy)
    }

    // ==========================================
    // 业务流与大厅交互
    // ==========================================

    private fun refreshRoomList() {
        val lobby = activeLobby ?: return
        statusMessage = "正在拉取房间列表..."
        errorMessage = null
        val gen = ++requestGeneration
        lobby.listRooms().whenComplete { list, err ->
            runOnUi(gen) {
                statusMessage = null
                if (err != null) {
                    errorMessage = "获取房间列表失败: ${err.message}"
                } else {
                    roomList = list ?: emptyList()
                    selectedRoom = roomList.firstOrNull()
                }
            }
        }
    }

    private fun onRoomInfoUpdated(info: RoomInfo) {
        if (isDisposed) return
        currentRoomInfo = info
        if (info.lifecycle == RoomLifecycle.PLAYING) {
            if (myRole == ClientRole.PLAYER && !hasNavigatedToTable) {
                hasNavigatedToTable = true
                navigation.showPlayerTable()
            }
            return
        }
        val seat = myViewerSeat
        if (seat != null) {
            val me = info.members.firstOrNull { it.seat == seat }
            if (me != null) {
                isMyReady = me.isReady
                isHost = me.isHost
            }
        }
    }

    // ==========================================
    // 交互输入事件响应
    // ==========================================

    override fun touchDown(screenX: Int, screenY: Int, pointer: Int, button: Int): Boolean {
        val tx: Float
        val ty: Float
        if (Gdx.graphics == null || Gdx.graphics.width == 0) {
            tx = screenX.toFloat()
            ty = 720f - screenY
        } else {
            unprojectVec.set(screenX.toFloat(), screenY.toFloat(), 0f)
            camera.unproject(unprojectVec)
            tx = unprojectVec.x
            ty = unprojectVec.y
        }

        when (currentState) {
            MenuState.MAIN -> handleTouchMain(tx, ty)
            MenuState.UDP_CONNECT_DIALOG -> handleTouchUdpDialog(tx, ty)
            MenuState.LOBBY -> handleTouchLobby(tx, ty)
            MenuState.CREATE_ROOM_DIALOG -> handleTouchCreateDialog(tx, ty)
            MenuState.JOIN_ROOM_DIALOG -> handleTouchJoinDialog(tx, ty)
            MenuState.ROOM -> handleTouchRoom(tx, ty)
            MenuState.SECRETS_MODAL, MenuState.SETTINGS_MODAL -> handleTouchModal(tx, ty)
            else -> {}
        }
        return true
    }

    private fun handleTouchMain(tx: Float, ty: Float) {
        if (btnPlaySingle.contains(tx, ty)) {
            // 单机快速建房并进入牌桌
            statusMessage = "正在初始化单机对局..."
            errorMessage = null
            val gen = ++requestGeneration
            var targetLobby: LobbySession? = null
            navigation.openLocalLobby().thenCompose { lobby ->
                targetLobby = lobby
                attachLobby(lobby)
                val defaultCosts = mapOf(
                    "TILE_MARK" to 10
                )
                lobby.createRoom(playerName = "玩家", config = MatchConfig(
                    cheatEconomy = CheatEconomyConfig(
                        enabled = true,
                        initialPoints = List(4) { 100 },
                        costs = defaultCosts
                    ),
                    shuffleConfig = ShuffleConfig(enabled = true),
                    tileMarkConfig = TileMarkConfig(enabled = true)
                ))
            }.thenCompose { snap ->
                myViewerSeat = snap.viewerSeat
                myRole = ClientRole.PLAYER
                hasNavigatedToTable = false
                targetLobby!!.start()
            }.whenComplete { receipt, err ->
                runOnUi(gen) {
                    statusMessage = null
                    if (err != null) {
                        errorMessage = "单机启动失败: ${err.message}"
                    } else if (receipt?.status == ReceiptStatus.APPLIED) {
                        if (!hasNavigatedToTable) {
                            hasNavigatedToTable = true
                            navigation.showPlayerTable()
                        }
                    } else {
                        errorMessage = "单机开赛被拒绝: ${receipt?.reason ?: receipt?.status}"
                    }
                }
            }
            return
        }
        if (btnOpenLobby.contains(tx, ty)) {
            if (activeLobby != null) {
                currentState = MenuState.LOBBY
                refreshRoomList()
            } else {
                currentState = MenuState.UDP_CONNECT_DIALOG
            }
            return
        }
        if (btnSkin.contains(tx, ty)) {
            assets.skinManager.toggleSkin()
            return
        }
        if (btnSecrets.contains(tx, ty)) {
            currentState = MenuState.SECRETS_MODAL
            return
        }
        if (btnSettings.contains(tx, ty)) {
            currentState = MenuState.SETTINGS_MODAL
            return
        }
        if (btnExit.contains(tx, ty)) {
            navigation.exitGame()
            return
        }
    }

    private fun handleTouchUdpDialog(tx: Float, ty: Float) {
        if (rectHostInput.contains(tx, ty)) {
            activeInput = ActiveInput.HOST
            return
        }
        if (rectPortInput.contains(tx, ty)) {
            activeInput = ActiveInput.PORT
            return
        }
        if (btnUdpConnect.contains(tx, ty)) {
            val port = inputPort.toIntOrNull() ?: 8080
            currentState = MenuState.CONNECTING
            errorMessage = null
            val gen = ++requestGeneration
            navigation.openRemoteLobby(inputHost, port).whenComplete { lobby, err ->
                val task = Runnable {
                    if (isDisposed || gen != requestGeneration) {
                        lobby?.close()
                        return@Runnable
                    }
                    if (err != null || lobby == null) {
                        currentState = MenuState.UDP_CONNECT_DIALOG
                        errorMessage = "UDP 连接失败: ${err?.message ?: "未知错误"}"
                    } else {
                        attachLobby(lobby)
                        currentState = MenuState.LOBBY
                        refreshRoomList()
                    }
                }
                val app = Gdx.app
                if (app != null) app.postRunnable(task) else task.run()
            }
            return
        }
        if (btnUdpCancel.contains(tx, ty)) {
            requestGeneration++
            currentState = MenuState.MAIN
            return
        }
        activeInput = ActiveInput.NONE
    }

    private fun handleTouchLobby(tx: Float, ty: Float) {
        if (btnLobbyCreateRoom.contains(tx, ty)) {
            currentState = MenuState.CREATE_ROOM_DIALOG
            return
        }
        if (btnLobbyJoinById.contains(tx, ty)) {
            inputRoomId = ""
            activeInput = ActiveInput.ROOM_ID
            currentState = MenuState.JOIN_ROOM_DIALOG
            return
        }
        if (btnLobbyRefresh.contains(tx, ty)) {
            refreshRoomList()
            return
        }
        if (btnLobbyBack.contains(tx, ty)) {
            statusMessage = "正在断开大厅连接..."
            errorMessage = null
            val lobbyToClose = activeLobby
            detachLobby()
            lobbyToClose?.close()
            navigation.leaveToMenu().whenComplete { _, _ ->
                runOnUi {
                    statusMessage = null
                    currentState = MenuState.MAIN
                }
            }
            return
        }

        // 房间列表项点击
        if (rectRoomListPanel.contains(tx, ty)) {
            var rowY = rectRoomListPanel.y + rectRoomListPanel.height - 65f
            for (room in roomList.take(8)) {
                if (ty in (rowY - 20f)..(rowY + 20f)) {
                    selectedRoom = room
                    return
                }
                rowY -= 40f
            }
        }

        val room = selectedRoom
        if (room != null) {
            if (btnJoinSelected.contains(tx, ty)) {
                val lobby = activeLobby ?: return
                statusMessage = "正在加入房间 ${room.roomId}..."
                val gen = ++requestGeneration
                lobby.joinRoom(room.roomId, playerName = "玩家").whenComplete { snap, err ->
                    runOnUi(gen) {
                        statusMessage = null
                        if (err != null) {
                            errorMessage = "加入失败: ${err.message}"
                        } else if (snap != null) {
                            myViewerSeat = snap.viewerSeat
                            myRole = ClientRole.PLAYER
                            hasNavigatedToTable = false
                            currentState = MenuState.ROOM
                            fetchRoomInfo()
                        }
                    }
                }
                return
            }
            if (btnSpectateSelected.contains(tx, ty)) {
                val lobby = activeLobby ?: return
                statusMessage = "正在加入观战 ${room.roomId}..."
                val gen = ++requestGeneration
                lobby.joinSpectator(room.roomId, spectatorName = "观战者").whenComplete { _, err ->
                    runOnUi(gen) {
                        statusMessage = null
                        if (err != null) {
                            errorMessage = "观战加入失败: ${err.message}"
                        } else {
                            myViewerSeat = null
                            myRole = ClientRole.SPECTATOR
                            hasNavigatedToTable = true
                            navigation.showSpectatorTable()
                        }
                    }
                }
                return
            }
        }
    }

    private fun handleTouchCreateDialog(tx: Float, ty: Float) {
        if (btnCfgWind.contains(tx, ty)) {
            cfgIsHanchan = !cfgIsHanchan
            return
        }
        if (btnCfgShuffle.contains(tx, ty)) {
            cfgShuffleCheating = !cfgShuffleCheating
            return
        }
        if (btnCreateConfirm.contains(tx, ty)) {
            val lobby = activeLobby ?: return
            val defaultCosts = mapOf(
                "TILE_MARK" to 10
            )
            val config = MatchConfig(
                handsPerWind = 4,
                winds = if (cfgIsHanchan) listOf(1, 2) else listOf(1),
                cheatEconomy = CheatEconomyConfig(
                    enabled = true,
                    initialPoints = List(4) { 100 },
                    costs = defaultCosts
                ),
                shuffleConfig = ShuffleConfig(enabled = cfgShuffleCheating),
                tileMarkConfig = TileMarkConfig(enabled = true)
            )
            statusMessage = "正在创建房间..."
            val gen = ++requestGeneration
            lobby.createRoom(playerName = "房主", config = config).whenComplete { snap, err ->
                runOnUi(gen) {
                    statusMessage = null
                    if (err != null) {
                        errorMessage = "创建房间失败: ${err.message}"
                    } else if (snap != null) {
                        myViewerSeat = snap.viewerSeat
                        myRole = ClientRole.PLAYER
                        isHost = true
                        hasNavigatedToTable = false
                        currentState = MenuState.ROOM
                        fetchRoomInfo()
                    }
                }
            }
            return
        }
        if (btnCreateCancel.contains(tx, ty)) {
            currentState = MenuState.LOBBY
            return
        }
    }

    private fun handleTouchJoinDialog(tx: Float, ty: Float) {
        if (rectJoinRoomInput.contains(tx, ty)) {
            activeInput = ActiveInput.ROOM_ID
            return
        }
        if (btnJoinConfirm.contains(tx, ty)) {
            val lobby = activeLobby ?: return
            if (inputRoomId.isBlank()) {
                errorMessage = "请输入房间号"
                return
            }
            statusMessage = "正在加入房间 $inputRoomId..."
            val gen = ++requestGeneration
            lobby.joinRoom(inputRoomId, playerName = "玩家").whenComplete { snap, err ->
                runOnUi(gen) {
                    statusMessage = null
                    if (err != null) {
                        errorMessage = "加入失败: ${err.message}"
                    } else if (snap != null) {
                        myViewerSeat = snap.viewerSeat
                        myRole = ClientRole.PLAYER
                        hasNavigatedToTable = false
                        currentState = MenuState.ROOM
                        fetchRoomInfo()
                    }
                }
            }
            return
        }
        if (btnJoinCancel.contains(tx, ty)) {
            currentState = MenuState.LOBBY
            return
        }
        activeInput = ActiveInput.NONE
    }

    private fun handleTouchRoom(tx: Float, ty: Float) {
        val lobby = activeLobby ?: return
        if (btnRoomReadyToggle.contains(tx, ty)) {
            val nextReady = !isMyReady
            val gen = ++requestGeneration
            lobby.setReady(nextReady).whenComplete { receipt, err ->
                runOnUi(gen) {
                    if (err == null && receipt.status == ReceiptStatus.APPLIED) {
                        isMyReady = nextReady
                    }
                }
            }
            return
        }
        if (isHost && btnRoomStart.contains(tx, ty)) {
            statusMessage = "正在开始比赛..."
            val gen = ++requestGeneration
            lobby.start().whenComplete { receipt, err ->
                runOnUi(gen) {
                    statusMessage = null
                    if (err != null) {
                        errorMessage = "开赛失败: ${err.message}"
                    } else if (receipt.status == ReceiptStatus.APPLIED) {
                        if (myRole == ClientRole.PLAYER && !hasNavigatedToTable) {
                            hasNavigatedToTable = true
                            navigation.showPlayerTable()
                        }
                    } else {
                        errorMessage = "开赛被拒绝: ${receipt.reason ?: "所有玩家尚未准备"}"
                    }
                }
            }
            return
        }
        if (btnRoomLeave.contains(tx, ty)) {
            statusMessage = "正在离开房间..."
            errorMessage = null
            val gen = ++requestGeneration
            lobby.leaveRoom().whenComplete { receipt, err ->
                runOnUi(gen) {
                    statusMessage = null
                    if (err != null) {
                        errorMessage = "离开房间失败: ${err.message}"
                    } else if (receipt?.status == ReceiptStatus.APPLIED) {
                        currentRoomInfo = null
                        myViewerSeat = null
                        myRole = ClientRole.NONE
                        isMyReady = false
                        isHost = false
                        hasNavigatedToTable = false
                        currentState = MenuState.LOBBY
                        refreshRoomList()
                    } else {
                        errorMessage = "离开房间被拒绝: ${receipt?.reason ?: receipt?.status}"
                    }
                }
            }
            return
        }
    }

    private fun handleTouchModal(tx: Float, ty: Float) {
        if (btnModalClose.contains(tx, ty)) {
            currentState = MenuState.MAIN
            return
        }
        if (currentState == MenuState.SETTINGS_MODAL) {
            if (ty in 345f..395f) {
                assets.skinManager.toggleSkin()
                return
            }
        }
    }

    private fun fetchRoomInfo() {
        activeLobby?.getRoomInfo()?.whenComplete { info, _ ->
            Gdx.app?.postRunnable {
                if (info != null) {
                    onRoomInfoUpdated(info)
                }
            }
        }
    }

    override fun keyTyped(character: Char): Boolean {
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
            ActiveInput.ROOM_ID -> {
                if (character == '\b') {
                    if (inputRoomId.isNotEmpty()) inputRoomId = inputRoomId.dropLast(1)
                } else if (character.isLetterOrDigit()) {
                    if (inputRoomId.length < 12) inputRoomId += character
                }
                return true
            }
            ActiveInput.NONE -> {}
        }
        return false
    }

    override fun keyDown(keycode: Int): Boolean {
        if (keycode == Input.Keys.ESCAPE) {
            when (currentState) {
                MenuState.SECRETS_MODAL, MenuState.SETTINGS_MODAL -> currentState = MenuState.MAIN
                MenuState.UDP_CONNECT_DIALOG -> currentState = MenuState.MAIN
                MenuState.CONNECTING -> {
                    requestGeneration++
                    currentState = MenuState.UDP_CONNECT_DIALOG
                }
                MenuState.CREATE_ROOM_DIALOG, MenuState.JOIN_ROOM_DIALOG -> currentState = MenuState.LOBBY
                MenuState.LOBBY -> currentState = MenuState.MAIN
                MenuState.MAIN -> navigation.exitGame()
                else -> {}
            }
            return true
        }
        return false
    }

    override fun resize(width: Int, height: Int) {}
    override fun pause() {}
    override fun resume() {}
    override fun hide() {}

    override fun dispose() {
        isDisposed = true
        requestGeneration++
        detachLobby()
    }

    private class FloatingParticle(
        var x: Float,
        var y: Float,
        val size: Float,
        val speedY: Float,
        val alpha: Float
    )
}
