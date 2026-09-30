package com.majiang.render

import com.badlogic.gdx.Gdx
import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.screen.GameScreen
import com.majiang.screen.MenuScreen
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.ActionResult
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test

/**
 * GameScreen 与 MenuScreen 换肤无头冒烟测试。
 *
 * 验证：
 * 1. 换肤后 GameScreen 初始化与开局不崩溃；
 * 2. 分别兼容 WIKIPEDIA 皮肤与 CLASSIC 皮肤；
 * 3. 对局流转正常、无头环境资源安全；
 * 4. Screen.dispose 生命周期释放安全。
 */
class GameScreenSkinSmokeTest : GdxHeadlessTestRunner() {

    @BeforeEach
    fun setUp() {
        // 清理持久化，确保测试间互不污染
        Gdx.app?.getPreferences("majiang")?.let {
            it.clear()
            it.flush()
        }
    }

    private fun createGame(initialSkin: SkinId = SkinId.WIKIPEDIA): MahjongGame {
        val prefs = InMemorySkinPreferences(initialSkin)
        val skinManager = SkinManager(prefs)
        return MahjongGame(skinManager)
    }

    @Test
    fun `默认维基皮肤下GameScreen开局与交互冒烟`() {
        val game = createGame(SkinId.WIKIPEDIA)
        assertEquals(SkinId.WIKIPEDIA, game.skinManager.currentSkinId)

        val screen = GameScreen(game)
        assertNotNull(screen)

        // 模拟点击开始对局 (550, 340)
        val handled = screen.touchDown(550, 340, 0, 0)
        assertTrue(handled)

        // 验证手牌正常发放到位且物理守恒
        val view = screen.session.view()
        val totalTiles = view.myAllClosedTiles.size
        assertTrue(totalTiles in 13..14)
        assertEquals(listOf(14, 13, 13, 13), view.handTileCounts)

        // 验证打牌动作
        assertEquals(0, view.currentTurnPlayer)
        val tileToDiscard = view.myDrawnTile ?: view.myClosedTiles.first()
        assertTrue(screen.session.submit(GameAction.DiscardTile(0, tileToDiscard)) is ActionResult.Accepted)
        assertTrue(screen.session.view().myAllClosedTiles.size in 13..14)

        // 释放安全验证
        assertDoesNotThrow { screen.dispose() }
    }

    @Test
    fun `经典皮肤下GameScreen开局与交互冒烟`() {
        val game = createGame(SkinId.CLASSIC)
        assertEquals(SkinId.CLASSIC, game.skinManager.currentSkinId)

        val screen = GameScreen(game)
        assertNotNull(screen)

        // 模拟点击开始对局
        val handled = screen.touchDown(550, 340, 0, 0)
        assertTrue(handled)

        // 验证手牌正常发放到位
        val view = screen.session.view()
        val totalTiles = view.myAllClosedTiles.size
        assertTrue(totalTiles in 13..14)
        assertEquals(listOf(14, 13, 13, 13), view.handTileCounts)

        assertDoesNotThrow { screen.dispose() }
    }

    @Test
    fun `MenuScreen切换皮肤按钮冒烟`() {
        val game = createGame(SkinId.WIKIPEDIA)
        assertEquals(SkinId.WIKIPEDIA, game.skinManager.currentSkinId)

        val menuScreen = MenuScreen(game)

        // 模拟点击主菜单皮肤切换按钮 (坐标落在 btnSkin: 490..790, 330..378 区域内，例如 550, 350)
        val handled = menuScreen.touchDown(550, 350, 0, 0)
        assertTrue(handled, "点击主菜单皮肤按钮应被消费并切换皮肤")
        assertEquals(SkinId.CLASSIC, game.skinManager.currentSkinId, "皮肤应成功切换为 CLASSIC")

        // 再次点击切回
        val handledAgain = menuScreen.touchDown(550, 350, 0, 0)
        assertTrue(handledAgain)
        assertEquals(SkinId.WIKIPEDIA, game.skinManager.currentSkinId, "皮肤应成功切换回 WIKIPEDIA")

        assertDoesNotThrow { menuScreen.dispose() }
    }
}
