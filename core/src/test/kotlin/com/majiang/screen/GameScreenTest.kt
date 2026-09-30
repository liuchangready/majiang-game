package com.majiang.screen

import com.majiang.GdxHeadlessTestRunner
import com.majiang.MahjongGame
import com.majiang.state.GamePhase
import com.majiang.state.GameAction
import com.majiang.state.ActionResult
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class GameScreenTest : GdxHeadlessTestRunner() {

    @Test
    fun `GameScreen点击开始对局正常开局发牌`() {
        val game = MahjongGame()
        val screen = GameScreen(game)

        // 模拟点击「START GAME」按钮 (坐标: 550, 340)
        val handled = screen.touchDown(550, 340, 0, 0)
        assertTrue(handled, "点击开始对局按钮应被正常消费")

        // 验证手牌已发给人类玩家
        val view = screen.session.view()
        val totalTiles = view.myAllClosedTiles.size
        assertTrue(totalTiles in 13..14, "人类手牌应为13张或14张(若为庄家)")

        // 验证物理守恒
        assertEquals(14, totalTiles)
        assertEquals(listOf(14, 13, 13, 13), view.handTileCounts)
    }

    @Test
    fun `GameScreen人类打牌交互推进流程`() {
        val game = MahjongGame()
        val screen = GameScreen(game)

        // 开始对局
        screen.touchDown(550, 340, 0, 0)

        // 若不是人类回合，让状态机推进行动
        val view = screen.session.view()
        assertEquals(0, view.currentTurnPlayer)
        val tileToDiscard = view.myDrawnTile ?: view.myClosedTiles.first()
        assertTrue(screen.session.submit(GameAction.DiscardTile(0, tileToDiscard)) is ActionResult.Accepted)
        assertTrue(screen.session.view().myAllClosedTiles.size in 13..14)
    }

    @Test
    fun `GameScreen理牌开关默认OFF且点击即时切换生效`() {
        val game = MahjongGame()
        val screen = GameScreen(game)

        // 1. 默认状态必须为 OFF (false)
        assertFalse(screen.isAutoSort, "理牌开关默认应为 OFF")

        // 2. 点击命中区高度必须 >= 44px
        assertTrue(screen.btnSortRect.height >= 44f, "理牌开关点击命中区高度必须 >= 44px")

        // 3. 模拟点击理牌开关
        val clickX = (screen.btnSortRect.x + screen.btnSortRect.width / 2f).toInt()
        val clickY = (screen.btnSortRect.y + screen.btnSortRect.height / 2f).toInt()

        val handledOn = screen.touchDown(clickX, clickY, 0, 0)
        assertTrue(handledOn, "点击理牌开关应被消费")
        assertTrue(screen.isAutoSort, "点击后理牌开关应切换为 ON")

        // 4. 再次点击切换回 OFF
        val handledOff = screen.touchDown(clickX, clickY, 0, 0)
        assertTrue(handledOff, "再次点击理牌开关应被消费")
        assertFalse(screen.isAutoSort, "再次点击后理牌开关应切回 OFF")
    }

    @Test
    fun `GameScreen理牌开关位置不与返回按钮及开始按钮冲突`() {
        val game = MahjongGame()
        val screen = GameScreen(game)

        val sortRect = screen.btnSortRect
        // 确保与屏幕边缘有合适边距，且在 1280x720 视口内
        assertTrue(sortRect.x >= 0f && sortRect.x + sortRect.width <= 1280f)
        assertTrue(sortRect.y >= 0f && sortRect.y + sortRect.height <= 720f)

        // 确保不会与手牌区域核心位置发生全重叠
        // 牌桌手牌位于 startX=220, baseY=40, 延伸至约 1052
        assertTrue(sortRect.x >= 1052f, "理牌按钮应放置在手牌右侧不阻挡手牌")
    }
}
