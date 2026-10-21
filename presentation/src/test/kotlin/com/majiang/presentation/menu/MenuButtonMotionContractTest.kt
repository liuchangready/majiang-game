package com.majiang.presentation.menu

import com.badlogic.gdx.Input
import com.majiang.client.api.LobbySession
import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.SkinManager
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

/**
 * 菜单点击反馈回归（2026-10-22 用户更正后的语义）。
 *
 * 用户原话：“点击时按钮自身向右滑，背景稳定”。
 * 因此这里锁定：
 * - 被点项产生**正 X** 位移（0 → 峰值 → 0），其余五项、标题、章鱼、遮罩不动；
 * - 绘制矩形与命中矩形来自同一帧布局，位移中禁止重复激活；
 * - 鼠标、触控、键盘确认共用同一份位移状态；
 * - 业务回调即时且只执行一次，视觉反馈不延迟请求；
 * - 目标弹层固定位置，相机位置与投影不发生任何横移；
 * - 关闭/返回/暂停/resize 后没有残留位移或焦点错误；
 * - 减少动画直接落到最终稳定位置，无任何视觉等待。
 */
class MenuButtonMotionContractTest : GdxHeadlessTestRunner() {

    private class Clock(var nowMs: Long = 900L)

    private class FakeNav : TableNavigation {
        var localLobbyCalls = 0
        var remoteLobbyCalls = 0
        var exitCalls = 0
        var remoteFuture: CompletableFuture<LobbySession> = CompletableFuture()

        override fun openLocalLobby(): CompletableFuture<LobbySession> {
            localLobbyCalls++
            return CompletableFuture.completedFuture(null)
        }

        override fun openRemoteLobby(host: String, port: Int): CompletableFuture<LobbySession> {
            remoteLobbyCalls++
            return remoteFuture
        }

        override fun showPlayerTable() {}
        override fun showSpectatorTable() {}
        override fun leaveToMenu(): CompletableFuture<Receipt?> =
            CompletableFuture.completedFuture(Receipt("leave", ReceiptStatus.APPLIED))

        override fun exitGame() {
            exitCalls++
        }
    }

    private fun createScreen(
        nav: FakeNav = FakeNav(),
        clock: Clock = Clock(),
        motionEnabled: Boolean = true
    ): Triple<MenuScreen, UiAssets, Clock> {
        val assets = UiAssets.createDefault(SkinManager())
        val screen = MenuScreen(assets, nav, null, motionEnabled) { clock.nowMs }
        screen.resize(1280, 720)
        screen.show()
        screen.mainMenuView.beginEntry(0L)
        clock.nowMs = 940L
        screen.mainMenuView.update(940L)
        return Triple(screen, assets, clock)
    }

    private fun openWithFocus(screen: MenuScreen, item: MainMenuItemId) {
        screen.mainMenuView.restoreFocus(item)
        screen.keyDown(Input.Keys.ENTER)
    }

    private fun tapWorld(screen: MenuScreen, x: Float, y: Float): Boolean {
        val point = screen.screenPoint(x, y)
        return screen.touchDown(point.x.toInt(), point.y.toInt(), 0, 0)
    }

    private fun releaseWorld(screen: MenuScreen, x: Float, y: Float) {
        val point = screen.screenPoint(x, y)
        screen.touchUp(point.x.toInt(), point.y.toInt(), 0, 0)
    }

    private fun itemCenter(id: MainMenuItemId): Pair<Float, Float> {
        val rect = MainMenuLayout.spec(id).baseRect
        return (rect.x + rect.width / 2f) to (rect.y + rect.height / 2f)
    }

    @Test
    fun `clicked button slides to positive X while every other item stays put`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val (skinX, skinY) = itemCenter(MainMenuItemId.SKIN)
            val baseX = screen.mainMenuView.itemRect(MainMenuItemId.SKIN)!!.x
            val skinBefore = assets.skinManager.currentSkinId
            val others = MainMenuItemId.entries
                .filter { it != MainMenuItemId.SKIN }
                .associateWith { screen.mainMenuView.itemRect(it)!!.x }

            tapWorld(screen, skinX, skinY)
            releaseWorld(screen, skinX, skinY)

            // 0ms：位移为 0，回调已经同步执行。
            assertEquals(0f, screen.mainMenuView.buttonMotionOffsetX, 0.01f)
            assertTrue(assets.skinManager.currentSkinId != skinBefore, "callback must run synchronously")

            clock.nowMs += 80L
            screen.mainMenuView.update(clock.nowMs)
            val midOffset = screen.mainMenuView.buttonMotionOffsetX
            assertTrue(midOffset > 0f, "clicked item must move to positive X, got $midOffset")
            assertEquals(
                baseX + midOffset,
                screen.mainMenuView.itemRect(MainMenuItemId.SKIN)!!.x,
                0.01f,
                "the drawn rect must follow the motion"
            )

            // 未被点的五项必须纹丝不动。
            others.forEach { (id, x) ->
                assertEquals(x, screen.mainMenuView.itemRect(id)!!.x, 0.01f, "$id must not move")
            }

            clock.nowMs += 80L
            screen.mainMenuView.update(clock.nowMs)
            val peak = screen.mainMenuView.buttonMotionOffsetX
            assertTrue(
                peak >= 48f && peak <= 64f,
                "peak offset must stay in 48..64 logical px, got $peak"
            )

            clock.nowMs += MainMenuButtonMotion.RETRACT_MS + 20L
            screen.mainMenuView.update(clock.nowMs)
            assertEquals(0f, screen.mainMenuView.buttonMotionOffsetX, 0.01f)
            assertEquals(baseX, screen.mainMenuView.itemRect(MainMenuItemId.SKIN)!!.x, 0.01f)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `keyboard confirmation uses the same motion state as the mouse`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val baseX = screen.mainMenuView.itemRect(MainMenuItemId.EXIT)!!.x
            openWithFocus(screen, MainMenuItemId.EXIT)
            assertEquals(1, nav.exitCalls, "keyboard confirm must run the callback once")
            assertEquals(MainMenuItemId.EXIT, screen.mainMenuView.buttonMotionItemId)

            clock.nowMs += 80L
            screen.mainMenuView.update(clock.nowMs)
            val offset = screen.mainMenuView.buttonMotionOffsetX
            assertTrue(offset > 0f, "keyboard path must slide the button to positive X")
            assertEquals(baseX + offset, screen.mainMenuView.itemRect(MainMenuItemId.EXIT)!!.x, 0.01f)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `dragging out of the button cancels the press so nothing slides and nothing fires`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val (skinX, skinY) = itemCenter(MainMenuItemId.SKIN)
            val skinBefore = assets.skinManager.currentSkinId

            tapWorld(screen, skinX, skinY)
            assertEquals(MainMenuItemId.SKIN, screen.mainMenuView.pressedItemId)

            val point = screen.screenPoint(skinX, skinY)
            screen.touchDragged(point.x.toInt() + 400, point.y.toInt(), 0)
            assertNull(screen.mainMenuView.pressedItemId, "dragging out must cancel the press")

            releaseWorld(screen, skinX, skinY)
            screen.mainMenuView.update(clock.nowMs)
            assertEquals(0f, screen.mainMenuView.buttonMotionOffsetX, 0.01f)
            assertEquals(skinBefore, assets.skinManager.currentSkinId, "cancelled press must not fire")
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `sliding item cannot be activated again before it lands`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val (skinX, skinY) = itemCenter(MainMenuItemId.SKIN)
            tapWorld(screen, skinX, skinY)
            releaseWorld(screen, skinX, skinY)

            clock.nowMs += 80L
            screen.mainMenuView.update(clock.nowMs)
            assertTrue(screen.mainMenuView.isButtonMotionActive)

            val skinAfter = assets.skinManager.currentSkinId
            // 起点已空出、终点仍在位移中：两种点击都不允许再次触发。
            tapWorld(screen, skinX, skinY)
            releaseWorld(screen, skinX, skinY)
            tapWorld(screen, skinX + screen.mainMenuView.buttonMotionOffsetX, skinY)
            releaseWorld(screen, skinX + screen.mainMenuView.buttonMotionOffsetX, skinY)
            assertEquals(skinAfter, assets.skinManager.currentSkinId, "no duplicate activation while sliding")
            assertFalse(screen.mainMenuView.activate(MainMenuItemId.SKIN), "activate must be blocked while sliding")

            clock.nowMs += MainMenuButtonMotion.TOTAL_MS
            screen.mainMenuView.update(clock.nowMs)
            assertFalse(screen.mainMenuView.isButtonMotionActive)
            assertTrue(screen.mainMenuView.activate(MainMenuItemId.SKIN), "the item must be usable again after landing")
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `reduced motion is immediately stable with no visual wait`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav, motionEnabled = false)
        try {
            val (skinX, skinY) = itemCenter(MainMenuItemId.SKIN)
            val skinBefore = assets.skinManager.currentSkinId

            tapWorld(screen, skinX, skinY)
            releaseWorld(screen, skinX, skinY)

            assertEquals(0f, screen.mainMenuView.buttonMotionOffsetX, 0.01f)
            assertFalse(screen.mainMenuView.isButtonMotionActive)
            assertTrue(assets.skinManager.currentSkinId != skinBefore, "callback still runs synchronously")
            assertEquals(
                MainMenuLayout.spec(MainMenuItemId.SKIN).baseRect.x,
                screen.mainMenuView.itemRect(MainMenuItemId.SKIN)!!.x,
                0.01f
            )
            // 没有视觉等待：同一帧即可再次操作。
            assertTrue(screen.mainMenuView.activate(MainMenuItemId.SKIN))
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `overlay opens at a fixed position and the camera never shifts`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val cameraBefore = screen.cameraStateForDiagnostics()

            openWithFocus(screen, MainMenuItemId.SECRETS)
            assertEquals(MenuScreen.MenuState.SECRETS_MODAL, screen.currentState)

            // 相机位置与投影逐元素完全一致：目标弹层没有任何横移。
            val cameraAfter = screen.cameraStateForDiagnostics()
            assertArrayEquals(cameraBefore, cameraAfter, 0.0001f, "camera must not move on click")

            // 目标弹层固定位置：关闭按钮立即可用（不再有滑入门禁）。
            assertTrue(tapWorld(screen, 640f, 133f))
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
            assertArrayEquals(cameraBefore, screen.cameraStateForDiagnostics(), 0.0001f)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `escape closes the overlay without residual offset or focus error`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val entryCount = screen.mainMenuView.entryPlayCount
            openWithFocus(screen, MainMenuItemId.SETTINGS)
            assertEquals(MenuScreen.MenuState.SETTINGS_MODAL, screen.currentState)

            // 覆盖层还开着就按 ESC：菜单立刻回到可交互状态，且不留位移。
            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
            assertEquals(0f, screen.mainMenuView.buttonMotionOffsetX, 0.01f)
            assertEquals(
                MainMenuLayout.spec(MainMenuItemId.SETTINGS).baseRect.x,
                screen.mainMenuView.itemRect(MainMenuItemId.SETTINGS)!!.x,
                0.01f
            )
            assertEquals(MainMenuItemId.SETTINGS, screen.mainMenuView.focusedItemId, "focus must be restored")
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount, "closing overlay must not replay entry")
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `pause and resize leave no residual offset`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val (skinX, skinY) = itemCenter(MainMenuItemId.SKIN)
            tapWorld(screen, skinX, skinY)
            releaseWorld(screen, skinX, skinY)

            clock.nowMs += 40L
            screen.mainMenuView.update(clock.nowMs)
            assertTrue(screen.mainMenuView.buttonMotionOffsetX > 0f)

            screen.pause()
            clock.nowMs += 5_000L
            screen.mainMenuView.update(clock.nowMs)
            val frozen = screen.mainMenuView.buttonMotionOffsetX
            assertTrue(frozen > 0f, "paused motion must stay frozen, not snap away")

            screen.resume()
            screen.resize(960, 540)
            assertEquals(960, screen.viewport.screenWidth)
            assertEquals(540, screen.viewport.screenHeight)

            clock.nowMs += MainMenuButtonMotion.TOTAL_MS
            screen.mainMenuView.update(clock.nowMs)
            assertEquals(0f, screen.mainMenuView.buttonMotionOffsetX, 0.01f)
            assertEquals(
                MainMenuLayout.spec(MainMenuItemId.SKIN).baseRect.x,
                screen.mainMenuView.itemRect(MainMenuItemId.SKIN)!!.x,
                0.01f
            )
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `business callback runs immediately once and is never delayed by the visual feedback`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val skinBefore = assets.skinManager.currentSkinId
            val started = System.nanoTime()
            assertTrue(screen.mainMenuView.activate(MainMenuItemId.SKIN))
            val elapsedMs = (System.nanoTime() - started) / 1_000_000.0

            assertTrue(assets.skinManager.currentSkinId != skinBefore, "callback must run synchronously")
            assertTrue(elapsedMs < 50.0, "callback must not wait for the animation, took ${elapsedMs}ms")
            assertEquals(MainMenuItemId.SKIN, screen.mainMenuView.activationFlashItemId)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `repeated connect click issues exactly one request`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            openWithFocus(screen, MainMenuItemId.ONLINE)
            assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, screen.currentState)

            tapWorld(screen, 520f, 242f)
            assertEquals(MenuScreen.MenuState.CONNECTING, screen.currentState)
            assertEquals(1, nav.remoteLobbyCalls)

            tapWorld(screen, 520f, 242f)
            assertEquals(1, nav.remoteLobbyCalls, "second click must not duplicate the request")

            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, screen.currentState)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `clicking never replays the first entry sequence`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val entryCount = screen.mainMenuView.entryPlayCount
            val (skinX, skinY) = itemCenter(MainMenuItemId.SKIN)
            tapWorld(screen, skinX, skinY)
            releaseWorld(screen, skinX, skinY)

            clock.nowMs += MainMenuButtonMotion.TOTAL_MS
            screen.mainMenuView.update(clock.nowMs)
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount, "click must not replay the entry")
            assertTrue(screen.mainMenuView.currentFrame.allSettled)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `menu draw and hit geometry come from the same frame during the slide`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val (skinX, skinY) = itemCenter(MainMenuItemId.SKIN)
            tapWorld(screen, skinX, skinY)
            releaseWorld(screen, skinX, skinY)

            clock.nowMs += 80L
            screen.mainMenuView.update(clock.nowMs)
            val rect = screen.mainMenuView.itemRect(MainMenuItemId.SKIN)!!
            assertEquals(
                rect.x,
                screen.mainMenuView.currentFrame.item(MainMenuItemId.SKIN)!!.rect.x,
                0.01f,
                "the drawn rect and the hit rect must come from the same frame"
            )

            // 位移中的按钮不接受命中：命中终点不会空白触发。
            assertNull(screen.mainMenuView.currentFrame.hitTest(rect.x + 10f, rect.y + 10f))
            // 其它入口仍然可以命中。
            assertEquals(
                MainMenuItemId.SECRETS,
                screen.mainMenuView.currentFrame.hitTest(
                    MainMenuLayout.spec(MainMenuItemId.SECRETS).baseRect.x + 20f,
                    MainMenuLayout.spec(MainMenuItemId.SECRETS).baseRect.y + 20f
                )
            )
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }
}
