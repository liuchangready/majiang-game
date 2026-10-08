package com.majiang

import com.badlogic.gdx.Game
import com.badlogic.gdx.Gdx
import com.badlogic.gdx.Screen
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.majiang.client.api.GameSession
import com.majiang.client.api.CheatEconomyConfig
import com.majiang.client.api.LobbySession
import com.majiang.client.api.MatchConfig
import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.session.ServerConnectionSession
import com.majiang.client.api.SpectatorSession
import com.majiang.client.api.ShuffleConfig
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.SkinManager
import com.majiang.presentation.game.GameScreen
import com.majiang.presentation.game.SpectatorScreen
import com.majiang.presentation.menu.MenuScreen
import com.majiang.server.inprocess.InProcessServer
import java.io.IOException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * 游戏主入口与组合根 (MahjongGame)。
 *
 * 遵循 V4 架构契约与 TASK-ANTI-R5ROOT 规范：
 * 1. 仅作为组合根，装配 InProcessServer(true).createConnection() + ServerConnectionSession 统一大厅；
 * 2. 单机模式经 LobbySession.createRoom() 真实建房，远端 connectUdp 初始 socket/DNS 在后台调用，成功/错误回 GL 线程；
 * 3. 屏幕只收门面，根保存具体 runtime 并负责导航、tick、pause/resume、leave 与 close 语义；
 * 4. 共享中文 NotoSansSC 字体、SkinManager 与纹理资源统一由 UiAssets 管理并由根一次性释放；
 * 5. 实现 TableNavigation 导航契约，绝不暴露 engine 或 protocol 类型给屏幕；
 * 6. 彻底删除旧 SessionAccess 启动重载与 LocalHost 直连路径，不增兼容重载。
 */
class MahjongGame(
    val skinManager: SkinManager = SkinManager()
) : Game(), TableNavigation {

    lateinit var uiAssets: UiAssets
        private set

    val isAssetsInitialized: Boolean
        get() = ::uiAssets.isInitialized

    // 便捷访问共享资源
    val batch: SpriteBatch get() = uiAssets.batch
    val shapeRenderer: ShapeRenderer get() = uiAssets.shapeRenderer
    val font: BitmapFont get() = uiAssets.font
    val effectiveFont: BitmapFont get() = uiAssets.effectiveFont

    /** 当前单机进程内服务器（本地单机模式时持有；纯远端连接时为 null） */
    var inProcessServer: InProcessServer? = null
        private set

    /** 当前活跃的统一客户端连接会话运行时 */
    var currentSession: ServerConnectionSession? = null
        private set

    private val connectionSequence = AtomicLong(0)
    private val isDisposed = AtomicBoolean(false)

    override fun create() {
        uiAssets = UiAssets.createDefault(skinManager)
        setScreen(MenuScreen(uiAssets, this, null))
    }

    /**
     * 通过组合根统一切换场景，确保上一场景的资源被正确 dispose 一次。
     */
    fun changeScreen(newScreen: Screen?) {
        val prior = screen
        setScreen(newScreen)
        prior?.dispose()
    }

    /**
     * 打开本地单机大厅会话
     */
    override fun openLocalLobby(): CompletableFuture<LobbySession> {
        val token = connectionSequence.incrementAndGet()
        val resultFuture = CompletableFuture<LobbySession>()
        if (isDisposed.get()) {
            resultFuture.completeExceptionally(IllegalStateException("MahjongGame has been disposed"))
            return resultFuture
        }
        try {
            closeCurrentSession()
            closeInProcessServer()

            val server = InProcessServer(isSingleplayer = true)
            inProcessServer = server

            val conn = server.createConnection()
            val session = ServerConnectionSession(
                initialConnection = conn,
                dispatch = { task ->
                    val app = Gdx.app
                    if (app != null) {
                        app.postRunnable(task)
                    } else {
                        task()
                    }
                }
            )
            if (isDisposed.get() || connectionSequence.get() != token) {
                session.close()
                closeInProcessServer()
                resultFuture.completeExceptionally(IllegalStateException("Connection superseded or game disposed"))
            } else {
                currentSession = session
                resultFuture.complete(session)
            }
        } catch (t: Throwable) {
            resultFuture.completeExceptionally(t)
        }
        return resultFuture
    }

    /**
     * 建立远端 UDP 连接并打开大厅会话（初始 socket/DNS 在后台执行，成功/错误回 GL 线程）
     */
    override fun openRemoteLobby(host: String, port: Int): CompletableFuture<LobbySession> {
        val token = connectionSequence.incrementAndGet()
        val resultFuture = CompletableFuture<LobbySession>()
        if (isDisposed.get()) {
            resultFuture.completeExceptionally(IllegalStateException("MahjongGame has been disposed"))
            return resultFuture
        }
        val backgroundExecutor = Executors.newSingleThreadExecutor { r ->
            Thread(r, "RemoteLobby-Connector").apply { isDaemon = true }
        }
        backgroundExecutor.submit {
            try {
                ServerConnectionSession.connectUdp(
                    host = host,
                    port = port,
                    dispatch = { task ->
                        val app = Gdx.app
                        if (app != null) {
                            app.postRunnable(task)
                        } else {
                            task()
                        }
                    }
                ).whenComplete { session, error ->
                    backgroundExecutor.shutdown()
                    val app = Gdx.app
                    val postAction = Runnable {
                        if (isDisposed.get() || connectionSequence.get() != token) {
                            session?.close()
                            resultFuture.completeExceptionally(IllegalStateException("Connection superseded or game disposed"))
                        } else if (error != null || session == null) {
                            resultFuture.completeExceptionally(error ?: IOException("UDP connect failed"))
                        } else {
                            closeCurrentSession()
                            closeInProcessServer()
                            currentSession = session
                            resultFuture.complete(session)
                        }
                    }
                    if (app != null) {
                        app.postRunnable(postAction)
                    } else {
                        postAction.run()
                    }
                }
            } catch (t: Throwable) {
                backgroundExecutor.shutdown()
                val app = Gdx.app
                val postAction = Runnable { resultFuture.completeExceptionally(t) }
                if (app != null) {
                    app.postRunnable(postAction)
                } else {
                    postAction.run()
                }
            }
        }
        return resultFuture
    }

    /**
     * 导航到真人玩家牌桌场景 (GameScreen)
     */
    override fun showPlayerTable() {
        val session = currentSession ?: throw IllegalStateException("No active session")
        val screen = GameScreen(
            assets = uiAssets,
            session = session,
            navigation = this
        )
        changeScreen(screen)
    }

    /**
     * 导航到观战牌桌场景 (SpectatorScreen)
     */
    override fun showSpectatorTable() {
        val session = currentSession ?: throw IllegalStateException("No active session")
        val screen = SpectatorScreen(
            assets = uiAssets,
            session = session,
            navigation = this
        )
        changeScreen(screen)
    }

    /**
     * 主动离开牌桌返回主菜单场景（先完成 leaveRoom 语义）
     */
    override fun leaveToMenu(): CompletableFuture<Receipt?> {
        val resultFuture = CompletableFuture<Receipt?>()
        val session = currentSession
        if (session == null || session.closed) {
            closeInProcessServer()
            currentSession = null
            changeScreen(MenuScreen(uiAssets, this, null))
            resultFuture.complete(null)
            return resultFuture
        }

        session.leaveRoom().whenComplete { receipt, error ->
            val app = Gdx.app
            val postAction = Runnable {
                if (isDisposed.get()) {
                    resultFuture.completeExceptionally(IllegalStateException("MahjongGame disposed"))
                    return@Runnable
                }
                if (error != null) {
                    resultFuture.completeExceptionally(error)
                } else if (receipt?.status == ReceiptStatus.APPLIED) {
                    if (inProcessServer != null) {
                        // 单机：结束本地宿主并释放
                        closeCurrentSession()
                        closeInProcessServer()
                        changeScreen(MenuScreen(uiAssets, this, null))
                    } else {
                        // 远端联机：保持大厅会话并切回大厅菜单
                        changeScreen(MenuScreen(uiAssets, this, session))
                    }
                    resultFuture.complete(receipt)
                } else {
                    // REJECTED: 保留可信画面与会话
                    resultFuture.complete(receipt)
                }
            }
            if (app != null) {
                app.postRunnable(postAction)
            } else {
                postAction.run()
            }
        }
        return resultFuture
    }

    /**
     * 退出游戏
     */
    override fun exitGame() {
        Gdx.app?.exit()
    }

    /**
     * 安全返回主菜单场景
     */
    fun returnToMenu() {
        leaveToMenu()
    }

    /**
     * 默认启动游戏（单机 V4 真实建房全链路：openLocalLobby -> createRoom -> start -> showPlayerTable）
     */
    fun launchGame() {
        val lobbyFuture = openLocalLobby()
        val token = connectionSequence.get()
        lobbyFuture.thenCompose { lobby ->
            lobby.createRoom(playerName = "玩家", config = MatchConfig(
                cheatEconomy = CheatEconomyConfig(enabled = true, initialPoints = List(4) { 100 }),
                shuffleConfig = ShuffleConfig(enabled = true)
            )).thenCompose {
                lobby.start()
            }
        }.whenComplete { receipt, err ->
            val app = Gdx.app
            val action = Runnable {
                if (isDisposed.get() || connectionSequence.get() != token) {
                    return@Runnable
                }
                if (err == null && receipt?.status == ReceiptStatus.APPLIED) {
                    showPlayerTable()
                } else {
                    val msg = err?.message ?: receipt?.reason ?: "Failed to start singleplayer game: status=${receipt?.status}"
                    Gdx.app?.error("MahjongGame", msg)
                }
            }
            if (app != null) {
                app.postRunnable(action)
            } else {
                action.run()
            }
        }
    }

    fun closeCurrentSession() {
        try {
            currentSession?.close()
        } catch (_: Throwable) {
        }
        currentSession = null
    }

    fun closeInProcessServer() {
        try {
            inProcessServer?.close()
        } catch (_: Throwable) {
        }
        inProcessServer = null
    }

    override fun render() {
        val server = inProcessServer
        if (server != null && !server.closed) {
            val busy = (screen as? GameScreen)?.isDiscardPlaybackBusy ?: false
            server.tick(busy)
        }
        super.render()
    }

    override fun pause() {
        inProcessServer?.currentHost?.pause()
        super.pause()
    }

    override fun resume() {
        inProcessServer?.currentHost?.resumeClock()
        super.resume()
    }

    override fun dispose() {
        isDisposed.set(true)
        connectionSequence.incrementAndGet()
        closeCurrentSession()
        closeInProcessServer()
        val prior = screen
        setScreen(null)
        prior?.dispose()
        if (::uiAssets.isInitialized) {
            uiAssets.dispose()
        }
        super.dispose()
    }
}
