package com.majiang.presentation.menu

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
import com.badlogic.gdx.math.MathUtils
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.math.Vector2
import com.badlogic.gdx.math.Vector3
import com.badlogic.gdx.utils.TimeUtils
import com.badlogic.gdx.utils.viewport.FitViewport
import com.badlogic.gdx.utils.viewport.Viewport
import com.majiang.client.api.ConnectionState
import com.majiang.client.api.CheatEconomyConfig
import com.majiang.client.api.LobbySession
import com.majiang.client.api.MatchConfig
import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.RoomInfo
import com.majiang.client.api.RoomLifecycle
import com.majiang.client.api.RoomSummaryView
import com.majiang.client.api.ShuffleConfig
import com.majiang.client.api.SleeveBloomConfig
import com.majiang.client.api.BeamSwapConfig
import com.majiang.client.api.TileMarkConfig
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets
import java.util.concurrent.CompletableFuture
import java.util.concurrent.atomic.AtomicReference

/**
 * 千术麻将主菜单与大厅场景 (MenuScreen)。
 *
 * 遵循 V4 架构契约与 D-04 模块拆分规范：
 * 1. 仅依赖 [UiAssets]、[TableNavigation] 与 [LobbySession] 门面接口，严禁 import engine、protocol 或 server 实现；
 * 2. 状态机、会话生命周期、异步业务编排与一次性导航守卫归父层 [MenuScreen] 唯一所有；
 * 3. 拆离子视图 [MainMenuView]、[LobbyListView]、[RoomPreparationView]、
 *    [ConnectDialogView]、[CreateRoomDialogView]、[JoinRoomDialogView] 以及绘制辅助 [MenuViewDraw]；
 * 4. 子视图均为纯展示组件，不持有网络与业务状态，通过明确的回调意图上报父层；
 * 5. 全中文展示通过共享 NotoSansSC 字体渲染，布局坐标与基线严格逐像素一致。
 */
class MenuScreen(
    val assets: UiAssets,
    val navigation: TableNavigation,
    initialLobby: LobbySession? = null,
    val motionEnabled: Boolean = true,
    private val clock: () -> Long = { TimeUtils.millis() }
) : Screen, InputAdapter() {

    private val camera = OrthographicCamera()
    val viewport: Viewport = FitViewport(MainMenuLayout.DESIGN_WIDTH, MainMenuLayout.DESIGN_HEIGHT, camera)

    private val unprojectVec = Vector3()

    // 屏幕生命周期与请求代次守卫
    @Volatile private var isDisposed = false
    @Volatile private var requestGeneration = 0L
    @Volatile private var singleStartInFlight = false
    @Volatile private var singleStartLobby: LobbySession? = null
    private val singleStartLock = Any()
    private var lobbyExitInFlight = false

    // 覆盖弹窗关闭后恢复原焦点，不重播入场
    private var overlayReturnFocus: MainMenuItemId? = null

    // 席位与角色跟踪
    private enum class ClientRole { NONE, PLAYER, SPECTATOR }
    private var myRole = ClientRole.NONE
    private var myViewerSeat: Int? = null
    private var hasNavigatedToTable: Boolean = false

    internal val roleNameForTest: String get() = myRole.name
    internal val viewerSeatForTest: Int? get() = myViewerSeat
    internal val navigatedToTableForTest: Boolean get() = hasNavigatedToTable

    // 活跃大厅会话引用与订阅
    @Volatile
    var activeLobby: LobbySession? = initialLobby
        private set
    private var lobbyStateSub: AutoCloseable? = null
    private var roomUpdateSub: AutoCloseable? = null

    // 状态机
    enum class MenuState {
        MAIN,
        TCP_CONNECT_DIALOG,
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

    /**
     * 只有“点击入口后出现的目标内容”使用**淡入**（固定位置，不做任何平移）；
     * 完整大厅/房间页保持既有整屏切换，避免主场景被整体拖动。
     * MAIN 自身的六项入场由 [MainMenuView] 负责，点击反馈位移由 [MainMenuButtonMotion] 负责。
     */
    private val overlayReveal = MenuOverlayReveal(motionEnabled = motionEnabled, clock = clock)
    private var overlayRevealState: MenuState = currentState

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

    // 通用弹窗区域常量 (保留在父层)
    private val modalRect = Rectangle(260f, 110f, 760f, 500f)
    private val btnModalClose = Rectangle(570f, 112f, 140f, 42f)

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

    // 拆分的子视图组件 (父层长期持有，在同一 MenuScreen 生命周期内保留各自输入/翻转状态)
    val mainMenuView = MainMenuView(
        onPlaySingle = ::startSinglePlay,
        onOpenLobby = ::openLobbyFlow,
        onToggleSkin = { assets.skinManager.toggleSkin() },
        onSecrets = { openOverlay(MenuState.SECRETS_MODAL) },
        onSettings = { openOverlay(MenuState.SETTINGS_MODAL) },
        onExit = { navigation.exitGame() },
        motionEnabled = motionEnabled,
        clock = clock,
        assetsProvider = { assets.obtainMainMenuAssets() }
    )

    val connectDialogView = ConnectDialogView(
        onConnect = ::connectRemoteLobby,
        onCancel = {
            requestGeneration++
            currentState = MenuState.MAIN
        }
    )

    val lobbyListView = LobbyListView(
        onCreateRoom = { currentState = MenuState.CREATE_ROOM_DIALOG },
        onJoinById = {
            joinRoomDialogView.resetInput()
            currentState = MenuState.JOIN_ROOM_DIALOG
        },
        onRefresh = ::refreshRoomList,
        onBack = ::disconnectLobbyAndReturn,
        onRoomRowSelected = { index ->
            selectedRoom = roomList.getOrNull(index)
        },
        onJoinSelected = ::joinSelectedRoom,
        onSpectateSelected = ::spectateSelectedRoom
    )

    val createRoomDialogView = CreateRoomDialogView(
        onConfirm = ::createRoomWithConfig,
        onCancel = { currentState = MenuState.LOBBY }
    )

    val joinRoomDialogView = JoinRoomDialogView(
        onConfirm = ::joinRoomById,
        onCancel = { currentState = MenuState.LOBBY }
    )

    val roomPreparationView = RoomPreparationView(
        onToggleReady = ::toggleReadyState,
        onStart = ::startRoomMatch,
        onLeave = ::leaveCurrentRoom
    )

    init {
        initialLobby?.let { attachLobby(it) }
        val graphics = Gdx.graphics
        if (graphics != null && graphics.width > 0 && graphics.height > 0) {
            resize(graphics.width, graphics.height)
        } else {
            viewport.update(1280, 720, true)
        }
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
        if (currentState == MenuState.MAIN) {
            mainMenuView.beginEntryIfNeeded(clock())
        }
    }

    override fun render(delta: Float) {
        animTimer += delta
        updateParticles(delta)

        Gdx.gl.glClearColor(0.02f, 0.05f, 0.04f, 1f)
        Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT)
        // ShapeRenderer 不自动开启混合；九宫格/章鱼/渐暗层都依赖标准 alpha 混合。
        enableMenuAlphaBlending()

        val menuLikeState = currentState == MenuState.MAIN ||
            currentState == MenuState.SECRETS_MODAL ||
            currentState == MenuState.SETTINGS_MODAL
        val menuAssets = if (menuLikeState) assets.obtainMainMenuAssets() else null

        // L0：底图独立按真实屏幕 cover，超宽区域由场景填充；内容随后由 FitViewport 限制。
        if (menuAssets != null) {
            Gdx.gl.glViewport(0, 0, Gdx.graphics.width, Gdx.graphics.height)
            MainMenuView.renderBackdrop(
                assets.batch,
                menuAssets,
                Gdx.graphics.width.toFloat(),
                Gdx.graphics.height.toFloat()
            )
            // SpriteBatch.end() 会关闭混合；后续 ShapeRenderer 的 L1/L2/L7 必须重新开启。
            enableMenuAlphaBlending()
        }

        // 统一内容视口：绘制与 unproject 使用同一 viewport。
        viewport.apply()
        camera.update()
        // 动画只在渲染帧启动/推进；输入不因视觉反馈被门控，业务回调永远即时。
        syncOverlayReveal(clock())

        val shape = assets.shapeRenderer
        val batch = assets.batch
        val font = assets.effectiveFont

        if (menuAssets == null) {
            renderBackground(shape)
        }

        when (currentState) {
            MenuState.MAIN -> renderMainMenuContent(shape, batch, font)
            MenuState.TCP_CONNECT_DIALOG -> connectDialogView.render(
                camera, shape, batch, font,
                animTimer, errorMessage
            )
            MenuState.CONNECTING -> renderConnectingScreen(shape, batch, font)
            MenuState.LOBBY -> lobbyListView.render(
                camera, shape, batch, font,
                activeLobby?.connectionState ?: ConnectionState.CLOSED,
                roomList, selectedRoom,
                statusMessage, errorMessage
            )
            MenuState.CREATE_ROOM_DIALOG -> createRoomDialogView.render(camera, shape, batch, font)
            MenuState.JOIN_ROOM_DIALOG -> joinRoomDialogView.render(
                camera, shape, batch, font,
                animTimer, errorMessage
            )
            MenuState.ROOM -> roomPreparationView.render(
                camera, shape, batch, font,
                currentRoomInfo, joinRoomDialogView.inputRoomId,
                isMyReady, isHost,
                statusMessage, errorMessage
            )
            MenuState.SECRETS_MODAL -> {
                // 菜单底图与全屏遮罩保持稳定，只让目标弹窗原位淡入。
                renderMainMenuContent(shape, batch, font)
                renderModalDimmer(shape)
                renderSecretsModal(shape, batch, font)
            }
            MenuState.SETTINGS_MODAL -> {
                renderMainMenuContent(shape, batch, font)
                renderModalDimmer(shape)
                renderSettingsModal(shape, batch, font)
            }
        }
    }

    /**
     * 检测覆盖层状态切换并推进淡入。目标弹窗始终固定在原位，只改 alpha；
     * 相机位置与投影在菜单生命周期内从不因点击而改变。
     */
    internal fun syncOverlayReveal(nowMs: Long = clock()) {
        if (currentState != overlayRevealState) {
            overlayRevealState = currentState
            if (currentState == MenuState.SECRETS_MODAL || currentState == MenuState.SETTINGS_MODAL) {
                overlayReveal.start(nowMs)
            } else {
                overlayReveal.snap()
            }
            return
        }
        if (overlayReveal.isActive) {
            overlayReveal.update(nowMs)
        }
    }

    /**
     * 仅供 GL 冒烟与诊断：当前覆盖层遮罩的 alpha（0..1）。
     * 遮罩只做淡入，不移动任何像素；冒烟用它对帧做亮度归一化后比较按钮位移。
     */
    val overlayRevealAlpha: Float get() = overlayReveal.alpha

    /**
     * 仅供 GL 冒烟与诊断：返回相机状态快照（位置 + 投影矩阵元素）。
     * 用于证明菜单点击与弹窗打开都不会让相机或投影横移。
     */
    fun cameraStateForDiagnostics(): FloatArray {
        val projection = camera.combined.getValues()
        val state = FloatArray(2 + projection.size)
        state[0] = camera.position.x
        state[1] = camera.position.y
        System.arraycopy(projection, 0, state, 2, projection.size)
        return state
    }

    private fun renderMainMenuContent(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        mainMenuView.render(
            camera = camera,
            shape = shape,
            batch = batch,
            font = font,
            currentSkinName = assets.skinManager.currentSkinId.name,
            statusMessage = statusMessage,
            errorMessage = errorMessage,
            titleFont = assets.effectiveMainMenuTitleFont,
            primaryFont = assets.effectiveMainMenuFont24,
            secondaryFont = assets.effectiveMainMenuFont21,
            statusFont = assets.effectiveMainMenuFont16,
            disabledItems = menuDisabledItems()
        )
    }

    private fun menuDisabledItems(): Set<MainMenuItemId> {
        if (!singleStartInFlight) return emptySet()
        return setOf(MainMenuItemId.SOLO, MainMenuItemId.ONLINE)
    }

    private fun openOverlay(state: MenuState) {
        if (currentState != MenuState.MAIN) return
        if (state != MenuState.SECRETS_MODAL && state != MenuState.SETTINGS_MODAL) return
        overlayReturnFocus = mainMenuView.focusedItemId
        mainMenuView.cancelPress()
        currentState = state
    }

    private fun closeOverlay() {
        if (currentState != MenuState.SECRETS_MODAL && currentState != MenuState.SETTINGS_MODAL) return
        currentState = MenuState.MAIN
        // 关闭覆盖层后不允许残留任何点击位移。
        mainMenuView.resetButtonMotion()
        mainMenuView.restoreFocus(overlayReturnFocus)
        overlayReturnFocus = null
    }

    private fun enterMain(replayEntry: Boolean) {
        val wasMain = currentState == MenuState.MAIN
        currentState = MenuState.MAIN
        if (replayEntry && !wasMain) {
            mainMenuView.beginEntry(clock())
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
        enableMenuAlphaBlending()
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

    private fun renderConnectingScreen(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        batch.projectionMatrix = camera.combined
        batch.begin()
        font.data.setScale(1.5f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        MenuViewDraw.drawCenteredText(batch, font, "正在连接到 ${connectDialogView.inputHost}:${connectDialogView.inputPort} ...", 640f, 380f)
        batch.end()
    }

    // ==========================================
    // 千术秘籍与设置弹窗 (保留在 MenuScreen)
    // ==========================================

    private fun renderSecretsModal(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        renderModalBase(shape, batch, font)
        batch.begin()

        font.data.setScale(1.8f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        MenuViewDraw.drawCenteredText(batch, font, "-- 雀圣千术绝技秘籍 --", modalRect.x + modalRect.width / 2f, modalRect.y + 465f)

        font.data.setScale(0.86f)
        font.color = Color.WHITE
        font.draw(batch, "1. 落汗绝技: 摸牌/出牌决策时，选择手牌中 4 张不同实体牌进行微痕标记。", modalRect.x + 40f, modalRect.y + 428f)
        font.draw(batch, "2. 千术消耗: 每次落汗消耗 10 额度点数，每名玩家最多同时维持 12 张标记牌。", modalRect.x + 40f, modalRect.y + 404f)
        font.draw(batch, "3. 有效期限: 当局立即生效，另持续后续 3 局；思考超时仅禁止新施术。", modalRect.x + 40f, modalRect.y + 380f)
        font.draw(batch, "4. 洞察识别: 标记牌进入对手手牌时可识别牌面，打入牌河时精准定位标识。", modalRect.x + 40f, modalRect.y + 356f)
        font.draw(batch, "5. 袖里藏花: 摸牌后或他家回合本人弃牌后消耗 10 额度，二选一保留候选牌；", modalRect.x + 40f, modalRect.y + 332f)
        font.draw(batch, "   立直可施术；他家回合预定牌待本人下次普通摸牌入手；超时不退费。", modalRect.x + 40f, modalRect.y + 308f)
        font.draw(batch, "6. 偷梁换柱: 摸牌后出牌前消耗 10 额度，从牌山/王牌暗槽中选目标与原有暗手牌互换；", modalRect.x + 40f, modalRect.y + 284f)
        font.draw(batch, "   候选暗槽不露牌面，落汗命中亮面；可保持原样；超时摸切费用不退。", modalRect.x + 40f, modalRect.y + 260f)
        font.draw(batch, "7. 统一码牌(30点): 准备阶段六选一目标，同时作用于起手与本局全部正常预计摸牌槽；", modalRect.x + 40f, modalRect.y + 236f)
        font.draw(batch, "   看破(10点)可叠加，合计最高 40 点；看破抵消对手整局码牌并清除其落汗，双方费用照付。", modalRect.x + 40f, modalRect.y + 212f)

        font.color = Color(0.95f, 0.45f, 0.45f, 1f)
        font.draw(batch, "注意: 偷梁换柱立直后禁止；袖里藏花立直可施术；超时或预定后不退款。", modalRect.x + 40f, modalRect.y + 186f)

        font.color = Color.WHITE
        MenuViewDraw.drawCenteredText(batch, font, "关闭 [X]", btnModalClose.x + btnModalClose.width / 2f, btnModalClose.y + 26f)

        batch.end()
    }

    private fun renderSettingsModal(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        renderModalBase(shape, batch, font)
        batch.begin()

        font.data.setScale(1.8f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        MenuViewDraw.drawCenteredText(batch, font, "-- 游戏设置 --", modalRect.x + modalRect.width / 2f, modalRect.y + 450f)

        font.data.setScale(1.2f)
        font.color = Color.WHITE
        font.draw(batch, "牌面皮肤     : [ ${assets.skinManager.currentSkinId.name} (点击切换) ]", modalRect.x + 80f, modalRect.y + 370f)
        font.draw(batch, "操作模式     : PC 鼠标拖拽点击 / 移动端触控手势", modalRect.x + 80f, modalRect.y + 310f)
        font.draw(batch, "对局思考时钟 : 20秒每局储备 + 3秒每次思考缓冲", modalRect.x + 80f, modalRect.y + 250f)
        font.draw(batch, "网络通信架构 : 本地进程权威宿主 / 远端 TCP 协议", modalRect.x + 80f, modalRect.y + 190f)

        MenuViewDraw.drawCenteredText(batch, font, "关闭 [X]", btnModalClose.x + btnModalClose.width / 2f, btnModalClose.y + 26f)

        batch.end()
    }

    /**
     * 全屏遮罩单独绘制：它属于稳定底图，不随任何内容平移，只做短促淡入。
     */
    private fun renderModalDimmer(shape: ShapeRenderer) {
        val alpha = overlayReveal.alpha
        if (alpha <= 0f) return
        enableMenuAlphaBlending()
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        try {
            shape.color = Color(0f, 0f, 0f, 0.75f * alpha)
            shape.rect(0f, 0f, 1280f, 720f)
        } finally {
            shape.end()
        }
    }

    private fun renderModalBase(shape: ShapeRenderer, batch: SpriteBatch, font: BitmapFont) {
        // 菜单底图刚结束 SpriteBatch，GL_BLEND 已被关闭；弹窗面板必须重新开启混合。
        enableMenuAlphaBlending()
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 面板固定在原位且完全不透明：覆盖层出现时目标层没有任何横向位移。
        shape.color = Color(0.08f, 0.16f, 0.12f, 1f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        MenuViewDraw.drawButton(shape, btnModalClose, isPrimary = false)

        shape.end()
    }

    // ==========================================
    // 业务流与大厅交互 (MenuScreen 唯一拥有)
    // ==========================================

    private fun startSinglePlay() {
        // 入口门禁：同一启动流程完成/失败前不接受第二次单机启动。
        if (isDisposed || singleStartInFlight) return
        singleStartInFlight = true
        mainMenuView.setDisabledItems(menuDisabledItems())
        statusMessage = "正在初始化单机对局..."
        errorMessage = null
        val gen = ++requestGeneration
        val targetLobbyRef = AtomicReference<LobbySession?>(null)
        val defaultCosts = mapOf(
            "TILE_MARK" to 10,
            "SLEEVE_BLOOM" to 10,
            "BEAM_SWAP" to 10,
            "SEE_THROUGH" to 10
        )
        val startFuture: CompletableFuture<LobbySession> = try {
            navigation.openLocalLobby()
        } catch (t: Throwable) {
            CompletableFuture.failedFuture(t)
        }
        startFuture.thenCompose { lobby ->
            // 存活检查、所有权发布、attach 与 dispose 回收共用同一临界区，
            // 避免“检查通过后被 dispose，再 attach 已废弃 screen”的竞态。
            synchronized(singleStartLock) {
                if (isDisposed || gen != requestGeneration) {
                    closeLobbyQuietly(lobby)
                    throw IllegalStateException("单机启动已过期")
                }
                targetLobbyRef.set(lobby)
                singleStartLobby = lobby
                attachLobby(lobby)
                if (isDisposed) {
                    // attach 期间（例如回调触发 dispose）已失效：撤销订阅并关闭。
                    detachLobby()
                    throw IllegalStateException("单机启动在 attach 期间被丢弃")
                }
            }
            lobby.createRoom(playerName = "玩家", config = MatchConfig(
                cheatEconomy = CheatEconomyConfig(
                    enabled = true,
                    initialPoints = List(4) { 100 },
                    costs = defaultCosts
                ),
                shuffleConfig = ShuffleConfig(enabled = true, seeThroughEnabled = true),
                tileMarkConfig = TileMarkConfig(enabled = true),
                sleeveBloomConfig = SleeveBloomConfig(enabled = true),
                beamSwapConfig = BeamSwapConfig(enabled = true)
            ))
        }.thenCompose { snap ->
            synchronized(singleStartLock) {
                if (isDisposed || gen != requestGeneration) {
                    throw IllegalStateException("单机启动已过期")
                }
                myViewerSeat = snap.viewerSeat
                myRole = ClientRole.PLAYER
                hasNavigatedToTable = false
                targetLobbyRef.get()!!.start()
            }
        }.whenComplete { receipt, err ->
            val failedLobby = targetLobbyRef.get()
            // 不按 gen 过滤清理：迟到失败也必须关闭自己创建的会话；但不会触碰已替换的 activeLobby。
            runOnUi {
                finishSingleStart(gen, failedLobby, receipt, err)
            }
        }
    }

    private fun finishSingleStart(
        gen: Long,
        lobby: LobbySession?,
        receipt: Receipt?,
        err: Throwable?
    ) {
        var current = false
        var failed = false
        synchronized(singleStartLock) {
            current = gen == requestGeneration
            failed = err != null || receipt?.status != ReceiptStatus.APPLIED
            if (current) {
                singleStartInFlight = false
                if (failed) {
                    cleanupSingleStartLobby(lobby)
                    if (lobby == null && activeLobby == null) {
                        resetSingleStartState()
                    }
                } else if (singleStartLobby === lobby) {
                    // 成功路径保留 activeLobby 供牌桌使用；只清除“待清理”标记。
                    singleStartLobby = null
                }
            } else {
                // 旧代次完成：只清理自己创建的会话，绝不触碰替换后的 activeLobby。
                if (failed) {
                    cleanupSingleStartLobby(lobby)
                } else if (activeLobby === lobby || singleStartLobby === lobby) {
                    cleanupSingleStartLobby(lobby)
                }
            }
        }

        if (!current) return

        mainMenuView.setDisabledItems(menuDisabledItems())
        statusMessage = null
        if (failed) {
            errorMessage = if (err != null) {
                "单机启动失败: ${err.message}"
            } else {
                "单机开赛被拒绝: ${receipt?.reason ?: receipt?.status}"
            }
        } else if (!hasNavigatedToTable) {
            hasNavigatedToTable = true
            navigation.showPlayerTable()
        }
    }

    /**
     * 只清理本流程自己的 lobby，绝不按 activeLobby 盲关：
     * - 若它仍是当前 activeLobby，先 detach 再 close，并恢复角色/席位/导航；
     * - 若已被新会话替换，只 close 旧对象，不触碰新 activeLobby。
     */
    private fun cleanupSingleStartLobby(lobby: LobbySession?) {
        if (lobby == null) return
        val wasActive = activeLobby === lobby
        if (wasActive) {
            detachLobby()
        }
        closeLobbyQuietly(lobby)
        if (singleStartLobby === lobby) {
            singleStartLobby = null
        }
        if (wasActive) {
            resetSingleStartState()
        }
    }

    private fun resetSingleStartState() {
        myRole = ClientRole.NONE
        myViewerSeat = null
        hasNavigatedToTable = false
        currentRoomInfo = null
        isMyReady = false
        isHost = false
    }

    private fun closeLobbyQuietly(lobby: LobbySession?) {
        if (lobby == null) return
        try {
            lobby.close()
        } catch (_: Throwable) {
        }
    }

    private fun openLobbyFlow() {
        if (singleStartInFlight) return
        if (currentState != MenuState.MAIN) return
        if (activeLobby != null) {
            currentState = MenuState.LOBBY
            refreshRoomList()
        } else {
            currentState = MenuState.TCP_CONNECT_DIALOG
        }
    }

    private fun connectRemoteLobby(host: String, port: Int) {
        currentState = MenuState.CONNECTING
        errorMessage = null
        val gen = ++requestGeneration
        navigation.openRemoteLobby(host, port).whenComplete { lobby, err ->
            val task = Runnable {
                if (isDisposed || gen != requestGeneration) {
                    lobby?.close()
                    return@Runnable
                }
                if (err != null || lobby == null) {
                    currentState = MenuState.TCP_CONNECT_DIALOG
                    errorMessage = "TCP 连接失败: ${err?.message ?: "未知错误"}"
                } else {
                    attachLobby(lobby)
                    currentState = MenuState.LOBBY
                    refreshRoomList()
                }
            }
            val app = Gdx.app
            if (app != null) app.postRunnable(task) else task.run()
        }
    }

    private fun disconnectLobbyAndReturn() {
        if (lobbyExitInFlight) return
        lobbyExitInFlight = true
        statusMessage = "正在断开大厅连接..."
        errorMessage = null
        val lobbyToClose = activeLobby
        detachLobby()
        lobbyToClose?.close()
        navigation.leaveToMenu().whenComplete { _, _ ->
            runOnUi {
                lobbyExitInFlight = false
                statusMessage = null
                enterMain(replayEntry = true)
            }
        }
    }

    private fun joinSelectedRoom() {
        val room = selectedRoom ?: return
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
    }

    private fun spectateSelectedRoom() {
        val room = selectedRoom ?: return
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
    }

    private fun createRoomWithConfig(isHanchan: Boolean, shuffleEnabled: Boolean) {
        val lobby = activeLobby ?: return
        val defaultCosts = mapOf(
            "TILE_MARK" to 10,
            "SLEEVE_BLOOM" to 10,
            "BEAM_SWAP" to 10,
            "SEE_THROUGH" to 10
        )
        val config = MatchConfig(
            handsPerWind = 4,
            winds = if (isHanchan) listOf(1, 2) else listOf(1),
            cheatEconomy = CheatEconomyConfig(
                enabled = true,
                initialPoints = List(4) { 100 },
                costs = defaultCosts
            ),
            // 玩法预设：开启码牌的建房同时启用看破；关闭码牌时看破也保持关闭。
            shuffleConfig = ShuffleConfig(enabled = shuffleEnabled, seeThroughEnabled = shuffleEnabled),
            tileMarkConfig = TileMarkConfig(enabled = true),
            sleeveBloomConfig = SleeveBloomConfig(enabled = true),
            beamSwapConfig = BeamSwapConfig(enabled = true)
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
    }

    private fun joinRoomById(roomId: String) {
        val lobby = activeLobby ?: return
        if (roomId.isBlank()) {
            errorMessage = "请输入房间号"
            return
        }
        statusMessage = "正在加入房间 $roomId..."
        val gen = ++requestGeneration
        lobby.joinRoom(roomId, playerName = "玩家").whenComplete { snap, err ->
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
    }

    private fun toggleReadyState() {
        val lobby = activeLobby ?: return
        val nextReady = !isMyReady
        val gen = ++requestGeneration
        lobby.setReady(nextReady).whenComplete { receipt, err ->
            runOnUi(gen) {
                if (err == null && receipt.status == ReceiptStatus.APPLIED) {
                    isMyReady = nextReady
                }
            }
        }
    }

    private fun startRoomMatch() {
        val lobby = activeLobby ?: return
        if (!isHost) return
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
    }

    private fun leaveCurrentRoom() {
        val lobby = activeLobby ?: return
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
    }

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

    private fun fetchRoomInfo() {
        activeLobby?.getRoomInfo()?.whenComplete { info, _ ->
            Gdx.app?.postRunnable {
                if (info != null) {
                    onRoomInfoUpdated(info)
                }
            }
        }
    }

    // ==========================================
    // 交互输入事件响应
    // ==========================================

    override fun touchDown(screenX: Int, screenY: Int, pointer: Int, button: Int): Boolean {
        val world = screenToWorld(screenX, screenY)
        val tx = world.x
        val ty = world.y

        when (currentState) {
            MenuState.MAIN -> mainMenuView.pointerDown(tx, ty)
            MenuState.TCP_CONNECT_DIALOG -> connectDialogView.handleTouch(tx, ty)
            MenuState.LOBBY -> lobbyListView.handleTouch(tx, ty, roomList, selectedRoom != null)
            MenuState.CREATE_ROOM_DIALOG -> createRoomDialogView.handleTouch(tx, ty)
            MenuState.JOIN_ROOM_DIALOG -> joinRoomDialogView.handleTouch(tx, ty)
            MenuState.ROOM -> roomPreparationView.handleTouch(tx, ty, isHost)
            MenuState.SECRETS_MODAL, MenuState.SETTINGS_MODAL -> handleTouchModal(tx, ty)
            else -> {}
        }
        return true
    }

    override fun touchUp(screenX: Int, screenY: Int, pointer: Int, button: Int): Boolean {
        if (currentState != MenuState.MAIN) return false
        val world = screenToWorld(screenX, screenY)
        mainMenuView.pointerUp(world.x, world.y)
        return true
    }

    override fun touchDragged(screenX: Int, screenY: Int, pointer: Int): Boolean {
        if (currentState != MenuState.MAIN) return false
        val world = screenToWorld(screenX, screenY)
        mainMenuView.pointerDragged(world.x, world.y)
        return true
    }

    override fun mouseMoved(screenX: Int, screenY: Int): Boolean {
        if (currentState != MenuState.MAIN) return false
        val world = screenToWorld(screenX, screenY)
        mainMenuView.pointerMoved(world.x, world.y)
        return true
    }

    /** 供自动化/测试把世界坐标转换成 libGDX 屏幕坐标（y 向下）。 */
    fun screenPoint(worldX: Float, worldY: Float): Vector2 {
        val point = Vector2(worldX, worldY)
        viewport.project(point)
        val screenHeight = if (Gdx.graphics != null) Gdx.graphics.height.toFloat() else viewport.screenHeight.toFloat()
        point.y = screenHeight - point.y
        return point
    }

    private fun screenToWorld(screenX: Int, screenY: Int): Vector2 {
        if (Gdx.graphics == null) {
            return Vector2(screenX.toFloat(), MainMenuLayout.DESIGN_HEIGHT - screenY)
        }
        unprojectVec.set(screenX.toFloat(), screenY.toFloat(), 0f)
        viewport.unproject(unprojectVec)
        return Vector2(unprojectVec.x, unprojectVec.y)
    }

    private fun handleTouchModal(tx: Float, ty: Float) {
        if (btnModalClose.contains(tx, ty)) {
            closeOverlay()
            return
        }
        if (currentState == MenuState.SETTINGS_MODAL) {
            if (ty in 345f..395f) {
                assets.skinManager.toggleSkin()
                return
            }
        }
    }

    override fun keyTyped(character: Char): Boolean {
        return when (currentState) {
            MenuState.TCP_CONNECT_DIALOG -> connectDialogView.keyTyped(character)
            MenuState.JOIN_ROOM_DIALOG -> joinRoomDialogView.keyTyped(character)
            else -> false
        }
    }

    override fun keyDown(keycode: Int): Boolean {
        if (keycode == Input.Keys.ESCAPE) {
            when (currentState) {
                MenuState.SECRETS_MODAL, MenuState.SETTINGS_MODAL -> closeOverlay()
                MenuState.TCP_CONNECT_DIALOG -> currentState = MenuState.MAIN
                MenuState.CONNECTING -> {
                    requestGeneration++
                    currentState = MenuState.TCP_CONNECT_DIALOG
                }
                MenuState.CREATE_ROOM_DIALOG, MenuState.JOIN_ROOM_DIALOG -> currentState = MenuState.LOBBY
                MenuState.LOBBY -> enterMain(replayEntry = true)
                MenuState.MAIN -> navigation.exitGame()
                else -> {}
            }
            return true
        }
        if (currentState == MenuState.MAIN) {
            when (keycode) {
                Input.Keys.TAB, Input.Keys.DOWN -> {
                    mainMenuView.focusNext()
                    return true
                }
                Input.Keys.UP -> {
                    mainMenuView.focusPrevious()
                    return true
                }
                Input.Keys.ENTER, Input.Keys.SPACE -> {
                    mainMenuView.activateFocused()
                    return true
                }
            }
        }
        return false
    }

    override fun resize(width: Int, height: Int) {
        if (width <= 0 || height <= 0) {
            // 无头测试/窗口最小化时保持设计视口，避免输入坐标系退化。
            viewport.update(
                MainMenuLayout.DESIGN_WIDTH.toInt(),
                MainMenuLayout.DESIGN_HEIGHT.toInt(),
                true
            )
            return
        }
        val graphics = Gdx.graphics
        val left = safeInset { graphics.safeInsetLeft }
        val right = safeInset { graphics.safeInsetRight }
        val top = safeInset { graphics.safeInsetTop }
        val bottom = safeInset { graphics.safeInsetBottom }
        val usableWidth = (width - left - right).coerceAtLeast(1)
        val usableHeight = (height - top - bottom).coerceAtLeast(1)
        viewport.update(usableWidth, usableHeight, true)
        // FitViewport 的屏幕矩形从 0 开始，安全边距通过平移 viewport 实现；
        // 内容仍保持等比，不会因刘海/导航栏拉伸。
        viewport.setScreenBounds(
            viewport.screenX + left,
            viewport.screenY + bottom,
            viewport.screenWidth,
            viewport.screenHeight
        )
    }

    private inline fun safeInset(block: () -> Int): Int = try {
        block().coerceAtLeast(0)
    } catch (_: Throwable) {
        0
    }

    override fun pause() {
        mainMenuView.onPause(clock())
    }

    override fun resume() {
        // 后台恢复不重播入场；只把暂停期间的时间平移掉，保持当前进度。
        mainMenuView.onResume(clock())
    }

    override fun hide() {
        mainMenuView.cancelPress()
        // 隐藏后不允许残留任何点击位移或焦点状态。
        mainMenuView.resetButtonMotion()
    }

    override fun dispose() {
        val pendingSingleStart = synchronized(singleStartLock) {
            isDisposed = true
            requestGeneration++
            val pending = singleStartLobby
            singleStartLobby = null
            detachLobby()
            pending
        }
        closeLobbyQuietly(pendingSingleStart)
    }

    private class FloatingParticle(
        var x: Float,
        var y: Float,
        val size: Float,
        val speedY: Float,
        val alpha: Float
    )
}
