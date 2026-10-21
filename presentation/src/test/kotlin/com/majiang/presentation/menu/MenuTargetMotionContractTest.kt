package com.majiang.presentation.menu

import com.badlogic.gdx.Input
import com.majiang.client.api.LobbySession
import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.SkinManager
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

/**
 * 点击菜单后目标内容滑入 + 输入门控回归。
 *
 * 覆盖：0/中间/终态位移；动画期间目标输入被吞；motionEnabled=false 直接可操作；
 * 关闭/ESC 返回不重播主菜单；重复点击不重复请求；resize 后命中坐标仍正确；
 * 激活反馈即时出现但业务回调不被动画延迟。
 */
class MenuTargetMotionContractTest : GdxHeadlessTestRunner() {

    private class Clock(var nowMs: Long = 900L)

    private class FakeNav : TableNavigation {
        var localLobbyCalls = 0
        var remoteLobbyCalls = 0
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

        override fun exitGame() {}
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

    @Test
    fun `secrets modal slides from right gates input then lands on the final close button`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            openWithFocus(screen, MainMenuItemId.SECRETS)
            assertEquals(MenuScreen.MenuState.SECRETS_MODAL, screen.currentState)

            // 第一帧：目标内容从 +220 开始，主菜单入场不重播。
            val entryCount = screen.mainMenuView.entryPlayCount
            screen.syncContentMotion(clock.nowMs)
            assertEquals(MenuContentMotion.OFFSET_X, screen.contentMotionOffsetX, 0.01f)
            assertTrue(screen.isContentMotionActive)
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount)

            // 动画期间点击终态关闭按钮必须被吞，不能穿透关闭。
            assertTrue(tapWorld(screen, 640f, 133f))
            assertEquals(MenuScreen.MenuState.SECRETS_MODAL, screen.currentState)

            clock.nowMs += 80L
            screen.syncContentMotion(clock.nowMs)
            val mid = screen.contentMotionOffsetX
            assertTrue(mid > 0f && mid < MenuContentMotion.OFFSET_X, "mid offset must be in flight: $mid")
            assertTrue(tapWorld(screen, 640f, 133f))
            assertEquals(MenuScreen.MenuState.SECRETS_MODAL, screen.currentState)

            clock.nowMs += MenuContentMotion.DURATION_MS
            screen.syncContentMotion(clock.nowMs)
            assertEquals(0f, screen.contentMotionOffsetX, 0.01f)
            assertFalse(screen.isContentMotionActive)
            assertTrue(tapWorld(screen, 640f, 133f))
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount, "closing overlay must not replay main entry")
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `connect dialog and settings share the same slide and escape closes during motion`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            openWithFocus(screen, MainMenuItemId.ONLINE)
            assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, screen.currentState)
            screen.syncContentMotion(clock.nowMs)
            assertEquals(MenuContentMotion.OFFSET_X, screen.contentMotionOffsetX, 0.01f)

            // ESC 是明确返回语义：立即回到 MAIN，不被 280ms 动画拖延。
            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
            screen.syncContentMotion(clock.nowMs + 300L)
            assertEquals(0f, screen.contentMotionOffsetX, 0.01f)

            clock.nowMs += 500L
            openWithFocus(screen, MainMenuItemId.SETTINGS)
            assertEquals(MenuScreen.MenuState.SETTINGS_MODAL, screen.currentState)
            screen.syncContentMotion(clock.nowMs)
            assertEquals(MenuContentMotion.OFFSET_X, screen.contentMotionOffsetX, 0.01f)

            clock.nowMs += MenuContentMotion.DURATION_MS
            screen.syncContentMotion(clock.nowMs)
            assertTrue(tapWorld(screen, 640f, 133f))
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `reduced motion target content is immediately operable without a frame`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav, motionEnabled = false)
        try {
            openWithFocus(screen, MainMenuItemId.SETTINGS)
            assertEquals(MenuScreen.MenuState.SETTINGS_MODAL, screen.currentState)
            screen.syncContentMotion(clock.nowMs)
            assertEquals(0f, screen.contentMotionOffsetX, 0.01f)
            assertFalse(screen.isContentMotionActive)

            assertTrue(tapWorld(screen, 640f, 133f))
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `resize after landing keeps target hit geometry aligned`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            openWithFocus(screen, MainMenuItemId.SECRETS)
            screen.syncContentMotion(clock.nowMs)
            clock.nowMs += MenuContentMotion.DURATION_MS
            screen.syncContentMotion(clock.nowMs)

            screen.resize(960, 540)
            assertEquals(960, screen.viewport.screenWidth)
            assertEquals(540, screen.viewport.screenHeight)
            assertTrue(tapWorld(screen, 640f, 133f))
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `repeated connect click during motion and connecting issues one request`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            openWithFocus(screen, MainMenuItemId.ONLINE)
            screen.syncContentMotion(clock.nowMs)
            clock.nowMs += MenuContentMotion.DURATION_MS
            screen.syncContentMotion(clock.nowMs)

            // 终态第一次点击连接：立即进入 CONNECTING 并只发起一次异步请求。
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
    fun `activation gives immediate visual feedback but never delays the business callback`() {
        val nav = FakeNav()
        val (screen, assets, clock) = createScreen(nav)
        try {
            val skinBefore = assets.skinManager.currentSkinId
            assertTrue(screen.mainMenuView.activate(MainMenuItemId.SKIN))
            assertTrue(assets.skinManager.currentSkinId != skinBefore, "callback must run synchronously")
            assertEquals(MainMenuItemId.SKIN, screen.mainMenuView.activationFlashItemId)

            clock.nowMs += MainMenuView.ACTIVATION_FLASH_MS - 1L
            screen.mainMenuView.update(clock.nowMs)
            assertEquals(MainMenuItemId.SKIN, screen.mainMenuView.activationFlashItemId)

            clock.nowMs += 1L
            screen.mainMenuView.update(clock.nowMs)
            assertNull(screen.mainMenuView.activationFlashItemId)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }
}
