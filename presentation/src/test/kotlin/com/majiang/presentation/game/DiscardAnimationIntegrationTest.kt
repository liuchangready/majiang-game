package com.majiang.presentation.game

import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.GameEventView
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import com.majiang.presentation.common.render.DiscardAnimation
import com.majiang.presentation.common.render.DiscardPhase
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class DiscardAnimationIntegrationTest : GdxHeadlessTestRunner() {

    private fun createGameScreen(
        initialDiscards: List<List<TileView>> = List(4) { emptyList() }
    ): Pair<GameScreen, FakeGameSession> {
        val closed = (1..9).map { TestViewBuilder.tile(SuitView.MAN, it) } +
            listOf(TestViewBuilder.tile(SuitView.PIN, 1), TestViewBuilder.tile(SuitView.PIN, 2), TestViewBuilder.tile(SuitView.PIN, 3), TestViewBuilder.tile(SuitView.PIN, 4))
        val drawn = TestViewBuilder.tile(SuitView.PIN, 5)
        val view = TestViewBuilder.playerView(
            viewerSeat = 0,
            currentTurnPlayer = 0,
            myClosedTiles = closed,
            myDrawnTile = drawn,
            discards = initialDiscards
        )
        val session = FakeGameSession(initialView = view)
        val nav = FakeTableNavigation()
        val screen = GameScreen(TestUiAssets.create(), session, nav)
        screen.startGame()
        return screen to session
    }

    @Test
    fun `牌河新增弃牌自动检测并触发打牌动画入队`() {
        val (screen, session) = createGameScreen()
        assertEquals(0, screen.discardQueue.size)
        assertNull(screen.currentDiscardAnim)

        // 模拟打出一张牌 1m
        val discardTile = TestViewBuilder.tile(SuitView.MAN, 1, 0)
        val currentDiscards = session.snapshot.view.discards.map { it.toMutableList() }
        currentDiscards[0].add(discardTile)

        session.update(session.snapshot.view.copy(discards = currentDiscards))

        // 执行一帧渲染更新
        screen.checkDiscardsDiff()

        // 验证已成功入队
        assertEquals(1, screen.discardQueue.size, "新打出的牌应入队 discardQueue")
        val queued = screen.discardQueue.first()
        assertEquals(0, queued.seat)
        assertEquals(discardTile, queued.tile)
        assertEquals(0, queued.targetIndex)

        // 推进一帧动画
        screen.updateDiscardAnimation(0.016f)
        assertNotNull(screen.currentDiscardAnim, "队列首个动画应成为当前激活播放动画")
        assertEquals(0, screen.discardQueue.size, "入池播放后队列数量应为 0")
        assertEquals(0.016f, screen.discardAnimElapsed, 1e-4f)

        screen.dispose()
    }

    @Test
    fun `单槽队列多张出牌按 FIFO 顺序排队播放`() {
        val (screen, session) = createGameScreen()

        val tile1 = TestViewBuilder.tile(SuitView.MAN, 1, 0)
        val tile2 = TestViewBuilder.tile(SuitView.PIN, 2, 0)
        val tile3 = TestViewBuilder.tile(SuitView.SOU, 3, 0)

        val discards = session.snapshot.view.discards.map { it.toMutableList() }
        discards[0].add(tile1)
        discards[1].add(tile2)
        discards[2].add(tile3)

        session.update(session.snapshot.view.copy(discards = discards))
        screen.checkDiscardsDiff()

        // 3 张牌全部入队
        assertEquals(3, screen.discardQueue.size)

        // 启动第 1 个动画
        screen.updateDiscardAnimation(0.01f)
        assertEquals(tile1, screen.currentDiscardAnim?.tile)
        assertEquals(2, screen.discardQueue.size)

        // 快进推进 0.82s，第 1 个动画结束，应自动无缝衔接第 2 个动画
        screen.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION)
        assertEquals(tile2, screen.currentDiscardAnim?.tile, "第 1 个播完后必须自动衔接播第 2 个动画")
        assertEquals(1, screen.discardQueue.size)

        // 再次推进 0.82s，自动衔接第 3 个动画
        screen.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION)
        assertEquals(tile3, screen.currentDiscardAnim?.tile, "第 2 个播完后必须自动衔接播第 3 个动画")
        assertEquals(0, screen.discardQueue.size)

        // 再次推进 0.82s，全部播放完毕
        screen.updateDiscardAnimation(DiscardAnimation.TOTAL_DURATION)
        assertNull(screen.currentDiscardAnim)
        assertEquals(0, screen.discardQueue.size)

        screen.dispose()
    }

    @Test
    fun `视图调和：动画播放期间末位牌扣牌隐藏，播完瞬间恢复常驻`() {
        val (screen, session) = createGameScreen()
        val tile = TestViewBuilder.tile(SuitView.MAN, 5, 0)

        val discards = session.snapshot.view.discards.map { it.toMutableList() }
        discards[0].add(tile)
        session.update(session.snapshot.view.copy(discards = discards))

        screen.checkDiscardsDiff()
        screen.updateDiscardAnimation(0.05f) // 处于 REACH 阶段

        val anim = screen.currentDiscardAnim
        assertNotNull(anim)
        val pose = DiscardAnimation.poseAt(screen.discardAnimElapsed, anim!!.start, anim.end, anim.seat)
        assertNotEquals(DiscardPhase.DONE, pose.phase)

        // 视图调和断言：此时牌河中该牌应被判定为扣下
        val isSuppressedDuring = DiscardAnimation.shouldSuppressDiscard(
            pondSeat = 0,
            tile = tile,
            tileIndex = 0,
            animatingIndex = 0,
            animatingSeat = anim.seat,
            animatingTile = anim.tile,
            phase = pose.phase
        )
        assertTrue(isSuppressedDuring, "飞行期间末位牌必须被扣牌隐藏")

        // 推进到动画结束 (>= 0.82s)
        screen.updateDiscardAnimation(0.80f)
        assertNull(screen.currentDiscardAnim, "动画完成应置空 currentDiscardAnim")

        // 此时由于 currentDiscardAnim 已置空或 DONE，牌河恢复常驻绘制
        val isSuppressedAfter = DiscardAnimation.shouldSuppressDiscard(
            pondSeat = 0,
            tile = tile,
            tileIndex = 0,
            animatingIndex = 0,
            animatingSeat = 0,
            animatingTile = tile,
            phase = DiscardPhase.DONE
        )
        assertFalse(isSuppressedAfter, "动画结束后末位牌必须恢复常驻绘制")

        screen.dispose()
    }

    @Test
    fun `边界情况：飞行中的牌被副露取走时动画快进到 DONE`() {
        val (screen, session) = createGameScreen()
        val flyingTile = TestViewBuilder.tile(SuitView.SOU, 5, 0)

        // 玩家 0 打出 5s
        val discards = session.snapshot.view.discards.map { it.toMutableList() }
        discards[0].add(flyingTile)
        session.update(session.snapshot.view.copy(discards = discards))

        screen.checkDiscardsDiff()
        screen.updateDiscardAnimation(0.10f)
        assertEquals(flyingTile, screen.currentDiscardAnim?.tile)

        // 他家碰牌：牌河中的 5s 被移除
        discards[0].remove(flyingTile)
        session.update(session.snapshot.view.copy(discards = discards))

        // 下一帧检测
        screen.checkDiscardsDiff()

        // 验证动画已快进到 DONE (置空)
        assertNull(screen.currentDiscardAnim, "被副露取走的牌动画必须立即快进到 DONE 并置空")

        screen.dispose()
    }

    @Test
    fun `边界情况：结算弹窗出现时所有未完成动画快进收尾并清空队列`() {
        val (screen, session) = createGameScreen()
        val tile1 = TestViewBuilder.tile(SuitView.MAN, 1, 0)
        val tile2 = TestViewBuilder.tile(SuitView.MAN, 2, 0)

        val discards = session.snapshot.view.discards.map { it.toMutableList() }
        discards[0].add(tile1)
        discards[0].add(tile2)
        session.update(session.snapshot.view.copy(discards = discards))

        screen.checkDiscardsDiff()
        screen.updateDiscardAnimation(0.05f)
        assertNotNull(screen.currentDiscardAnim)
        assertEquals(1, screen.discardQueue.size)

        // 结算事件发生（自摸）
        val tsumoEvent = GameEventView.TsumoWin(
            turnNumber = 1,
            player = 0,
            winTile = TestViewBuilder.tile(SuitView.PIN, 5),
            yakuList = listOf("TSUMO"),
            han = 1,
            fu = 30,
            score = 1000
        )
        session.update(session.snapshot.view, listOf(tsumoEvent))

        assertTrue(screen.isSettlementShown)
        assertNull(screen.currentDiscardAnim, "结算弹窗出现后当前动画必须置空")
        assertEquals(0, screen.discardQueue.size, "结算弹窗出现后动画队列必须被清空")

        screen.dispose()
    }

    @Test
    fun `边界情况：开启理牌开关时打牌即时重排手牌`() {
        val (screen, session) = createGameScreen()
        screen.isAutoSort = true

        val slotsBefore = screen.refreshHandTileRects()
        assertTrue(slotsBefore.isNotEmpty())

        val drawn = session.snapshot.view.myDrawnTile
        assertNotNull(drawn)

        // 模拟打牌后视图更新（drawn 牌打出进入 discards，手牌只剩闭手牌）
        val view = session.snapshot.view
        val updatedDiscards = view.discards.mapIndexed { idx, list ->
            if (idx == 0) list + drawn!! else list
        }
        val newView = view.copy(
            myDrawnTile = null,
            discards = updatedDiscards
        )
        session.update(newView)

        // 手牌槽位立即无状态重排，不再包含刚刚打出的牌
        val slotsAfter = screen.refreshHandTileRects()
        assertFalse(slotsAfter.any { it.tile == drawn }, "打出的牌离开手牌后手牌立即无状态重排完成")
        assertEquals(slotsBefore.size - 1, slotsAfter.size)

        screen.dispose()
    }
}
