package com.majiang

import com.badlogic.gdx.Game
import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.majiang.application.LocalTableRuntime
import com.majiang.client.LocalPlayerEndpoint
import com.majiang.client.PlayerEndpoint
import com.majiang.render.SkinManager
import com.majiang.screen.GameScreen
import com.majiang.screen.MenuScreen

/**
 * 游戏主入口（基于 Game 状态机与多 Screen 场景架构）。
 *
 * 管理全局共享渲染批处理（SpriteBatch、ShapeRenderer、BitmapFont），
 * 持有本地宿主调度器 (LocalTableRuntime)，
 * 并作为场景路由器在主菜单 (MenuScreen) 与游戏场景 (GameScreen) 之间平滑切换。
 */
class MahjongGame(
    val skinManager: SkinManager = SkinManager()
) : Game() {

    lateinit var batch: SpriteBatch
        private set

    lateinit var shapeRenderer: ShapeRenderer
        private set

    lateinit var font: BitmapFont
        private set

    var localRuntime: LocalTableRuntime? = null
        private set

    override fun create() {
        batch = SpriteBatch()
        shapeRenderer = ShapeRenderer()
        font = BitmapFont() // 内置默认字体，跨平台无外部依赖即可运行

        // 默认进入千术麻将主菜单
        setScreen(MenuScreen(this))
    }

    /**
     * 创建/注册本地宿主运行时并返回其端点。
     * 若已存在旧 runtime，先关闭旧 runtime。
     */
    fun createLocalEndpoint(): LocalPlayerEndpoint {
        localRuntime?.close()
        val runtime = LocalTableRuntime()
        localRuntime = runtime
        return runtime.endpoint
    }

    /**
     * 启动游戏牌桌场景（组合根装配入口，默认直接装配本地宿主 runtime 及其端点）。
     * 显式传入外部 endpoint 时不自行创建调度器。
     */
    fun launchGame(endpoint: PlayerEndpoint = createLocalEndpoint()) {
        if (endpoint !== localRuntime?.endpoint) {
            localRuntime?.close()
            localRuntime = null
        }
        setScreen(GameScreen(this, endpoint))
    }

    override fun render() {
        val runtime = localRuntime
        val currentScreen = screen
        if (runtime != null && currentScreen is GameScreen && currentScreen.endpoint === runtime.endpoint) {
            val delta = Gdx.graphics?.deltaTime ?: 0f
            runtime.update(delta, currentScreen.isDiscardPlaybackBusy)
        }
        super.render()
    }

    override fun dispose() {
        localRuntime?.close()
        localRuntime = null
        super.dispose()
        screen?.dispose()
        skinManager.dispose()
        batch.dispose()
        shapeRenderer.dispose()
        font.dispose()
    }
}
