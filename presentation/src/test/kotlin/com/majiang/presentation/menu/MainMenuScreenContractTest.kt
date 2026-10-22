package com.majiang.presentation.menu

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.Input
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.ConnectionState
import com.majiang.client.api.DecisionView
import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.LobbySession
import com.majiang.client.api.MatchConfig
import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.RoomConfig
import com.majiang.client.api.RoomInfo
import com.majiang.client.api.RoomLifecycle
import com.majiang.client.api.RoomSummaryView
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.SkinManager
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * 正式主菜单页面契约：入场时序、同帧命中、重入门禁、覆盖弹窗焦点、
 * 视口 resize 后的输入一致性、减少动画和单机启动重复门禁。
 */
class MainMenuScreenContractTest : GdxHeadlessTestRunner() {

    private class Clock(var nowMs: Long = 0L)

    private fun createScreen(
        nav: FakeNav = FakeNav(),
        initialLobby: LobbySession? = null,
        motionEnabled: Boolean = true,
        clock: Clock = Clock()
    ): Pair<MenuScreen, UiAssets> {
        val assets = UiAssets.createDefault(SkinManager())
        val screen = MenuScreen(assets, nav, initialLobby, motionEnabled) { clock.nowMs }
        screen.resize(MainMenuLayout.DESIGN_WIDTH.toInt(), MainMenuLayout.DESIGN_HEIGHT.toInt())
        return screen to assets
    }

    private fun snapshot(viewerSeat: Int = 0): GameSnapshotView {
        return GameSnapshotView(
            "session", "round", "epoch", 1L, 1L, 1L,
            TestViewBuilder.playerView(
                viewerSeat = viewerSeat,
                myClosedTiles = emptyList(),
                myDrawnTile = null
            ),
            DecisionView("round", "decision", 0L, emptyList())
        )
    }

    private fun tapWorld(screen: MenuScreen, worldX: Float, worldY: Float) {
        val point = screen.screenPoint(worldX, worldY)
        screen.touchDown(point.x.toInt(), point.y.toInt(), 0, 0)
        screen.touchUp(point.x.toInt(), point.y.toInt(), 0, 0)
    }

    private fun tapItem(screen: MenuScreen, id: MainMenuItemId) {
        val rect = screen.mainMenuView.itemRect(id) ?: screen.mainMenuView.rectFor(id)
        tapWorld(screen, rect.x + rect.width / 2f, rect.y + rect.height / 2f)
    }

    private fun drainUi() {
        val latch = CountDownLatch(1)
        Gdx.app.postRunnable { latch.countDown() }
        assertTrue(latch.await(3, TimeUnit.SECONDS), "posted UI work must run")
    }

    @Test
    fun `entry gate rejects slide clicks and enables each item independently`() {
        val clock = Clock()
        val nav = FakeNav()
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        try {
            screen.show()
            screen.resize(1280, 720)
            assertTrue(screen.mainMenuView.isEntryActive)

            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(0, nav.localLobbyCalls, "0ms click must not launch")

            clock.nowMs = 240L
            screen.mainMenuView.update(clock.nowMs)
            assertFalse(screen.mainMenuView.isItemSettled(MainMenuItemId.SOLO))
            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(0, nav.localLobbyCalls, "moving solo must not launch")
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)

            clock.nowMs = 539L
            screen.mainMenuView.update(clock.nowMs)
            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(0, nav.localLobbyCalls, "solo is not settled before 540ms")

            clock.nowMs = 540L
            screen.mainMenuView.update(clock.nowMs)
            assertTrue(screen.mainMenuView.isItemSettled(MainMenuItemId.SOLO))
            assertFalse(screen.mainMenuView.isItemSettled(MainMenuItemId.ONLINE))
            tapItem(screen, MainMenuItemId.ONLINE)
            assertEquals(0, nav.remoteLobbyCalls, "online remains disabled at 540ms")
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(1, nav.localLobbyCalls, "settled solo must launch")
            drainUi()
            assertNotNull(screen.errorMessage, "failed single start must display a reason")
            assertTrue(screen.mainMenuView.isItemEnabled(MainMenuItemId.SOLO), "failure must restore entry")

            clock.nowMs = 620L
            screen.mainMenuView.update(clock.nowMs)
            tapItem(screen, MainMenuItemId.ONLINE)
            assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, screen.currentState)
            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)

            clock.nowMs = 940L
            screen.mainMenuView.update(clock.nowMs)
            assertFalse(screen.mainMenuView.isEntryActive)
            tapItem(screen, MainMenuItemId.EXIT)
            assertEquals(1, nav.exitCalls, "exit callback must still be reachable")
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `return from lobby replays once and overlay close restores focus without replay`() {
        val clock = Clock()
        val nav = FakeNav()
        val lobby = FakeLobbySession()
        val (screen, assets) = createScreen(nav = nav, initialLobby = lobby, clock = clock)
        try {
            screen.show()
            assertEquals(MenuScreen.MenuState.LOBBY, screen.currentState)
            assertEquals(0, screen.mainMenuView.entryPlayCount, "lobby initial state must not run MAIN entry")

            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
            assertEquals(1, screen.mainMenuView.entryPlayCount, "return from lobby must replay once")
            assertTrue(screen.mainMenuView.isEntryActive)

            clock.nowMs = 940L
            screen.mainMenuView.update(clock.nowMs)
            val entryCount = screen.mainMenuView.entryPlayCount
            screen.resume()
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount, "resume must not replay")
            screen.pause()
            screen.resume()
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount, "pause/resume must not replay")

            screen.mainMenuView.restoreFocus(MainMenuItemId.SECRETS)
            assertEquals(MainMenuItemId.SECRETS, screen.mainMenuView.focusedItemId)
            screen.keyDown(Input.Keys.ENTER)
            assertEquals(MenuScreen.MenuState.SECRETS_MODAL, screen.currentState)
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount, "opening overlay must not replay")

            // 覆盖层打开时，父层门禁必须吞掉菜单底图的点击。
            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(0, nav.localLobbyCalls, "overlay must block underlying menu input")
            assertEquals(MenuScreen.MenuState.SECRETS_MODAL, screen.currentState)

            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
            assertEquals(MainMenuItemId.SECRETS, screen.mainMenuView.focusedItemId, "focus must be restored")
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount, "closing overlay must not replay")

            val skinBefore = assets.skinManager.currentSkinId
            tapItem(screen, MainMenuItemId.SKIN)
            assertTrue(assets.skinManager.currentSkinId != skinBefore, "skin entry must update immediately")
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount, "skin refresh must not replay")

            // 点击关闭按钮（世界坐标 640,133）关闭设置；覆盖层点击不得穿透。
            screen.mainMenuView.restoreFocus(MainMenuItemId.SETTINGS)
            screen.keyDown(Input.Keys.ENTER)
            assertEquals(MenuScreen.MenuState.SETTINGS_MODAL, screen.currentState)
            val closePoint = screen.screenPoint(640f, 133f)
            screen.touchDown(closePoint.x.toInt(), closePoint.y.toInt(), 0, 0)
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
            assertEquals(MainMenuItemId.SETTINGS, screen.mainMenuView.focusedItemId)
            screen.touchUp(closePoint.x.toInt(), closePoint.y.toInt(), 0, 0)
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `hover press focus and keyboard navigation share the same frame geometry`() {
        val clock = Clock(nowMs = 0L)
        val nav = FakeNav()
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        try {
            screen.show()
            clock.nowMs = 240L
            screen.mainMenuView.update(240L)
            val finalSolo = screen.screenPoint(972f, 520f)
            screen.mouseMoved(finalSolo.x.toInt(), finalSolo.y.toInt())
            assertNull(screen.mainMenuView.hoveredItemId, "moving item must not accept hover")

            clock.nowMs = 940L
            screen.mainMenuView.update(940L)
            val settledSolo = screen.screenPoint(972f, 520f)
            screen.mouseMoved(settledSolo.x.toInt(), settledSolo.y.toInt())
            assertEquals(MainMenuItemId.SOLO, screen.mainMenuView.hoveredItemId)

            screen.touchDown(settledSolo.x.toInt(), settledSolo.y.toInt(), 0, 0)
            assertEquals(MainMenuItemId.SOLO, screen.mainMenuView.pressedItemId)
            assertEquals(MainMenuItemId.SOLO, screen.mainMenuView.focusedItemId)
            screen.mainMenuView.cancelPress()
            assertNull(screen.mainMenuView.pressedItemId)

            screen.keyDown(Input.Keys.TAB)
            assertEquals(MainMenuItemId.ONLINE, screen.mainMenuView.focusedItemId)
            screen.keyDown(Input.Keys.DOWN)
            assertEquals(MainMenuItemId.SKIN, screen.mainMenuView.focusedItemId)
            screen.keyDown(Input.Keys.UP)
            assertEquals(MainMenuItemId.ONLINE, screen.mainMenuView.focusedItemId)
            screen.keyDown(Input.Keys.SPACE)
            assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, screen.currentState)
            screen.keyDown(Input.Keys.ESCAPE)
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `double lobby back click exits once and replays entry once`() {
        val clock = Clock()
        val nav = FakeNav()
        val lobby = FakeLobbySession()
        val (screen, assets) = createScreen(nav = nav, initialLobby = lobby, clock = clock)
        try {
            screen.show()
            val back = screen.screenPoint(190f, 143f)
            screen.touchDown(back.x.toInt(), back.y.toInt(), 0, 0)
            screen.touchDown(back.x.toInt(), back.y.toInt(), 0, 0)
            drainUi()
            assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
            assertEquals(1, nav.leaveCalls, "double back must issue one leave")
            assertEquals(1, screen.mainMenuView.entryPlayCount, "main must replay exactly once")
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `viewport resize keeps draw and hit geometry aligned`() {
        val clock = Clock()
        val nav = FakeNav()
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        try {
            screen.show()
            clock.nowMs = 940L
            screen.mainMenuView.update(clock.nowMs)

            screen.resize(960, 540)
            assertEquals(960, screen.viewport.screenWidth)
            assertEquals(540, screen.viewport.screenHeight)
            val skinBefore = assets.skinManager.currentSkinId
            tapItem(screen, MainMenuItemId.SKIN)
            assertTrue(assets.skinManager.currentSkinId != skinBefore, "960x540 hit must align with draw")

            screen.resize(2340, 1080)
            assertEquals(1920, screen.viewport.screenWidth, "19.5:9 content must stay 16:9 and centered")
            assertEquals(210, screen.viewport.screenX)
            assertEquals(1080, screen.viewport.screenHeight)
            tapItem(screen, MainMenuItemId.SETTINGS)
            assertEquals(MenuScreen.MenuState.SETTINGS_MODAL, screen.currentState, "ultrawide hit must align")
            screen.keyDown(Input.Keys.ESCAPE)

            screen.resize(1280, 720)
            assertEquals(1280, screen.viewport.screenWidth)
            assertEquals(0, screen.viewport.screenX)
            tapItem(screen, MainMenuItemId.SECRETS)
            assertEquals(MenuScreen.MenuState.SECRETS_MODAL, screen.currentState)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `reduced motion settles immediately and is operable without a frame`() {
        val clock = Clock(nowMs = 12_345L)
        val nav = FakeNav()
        val (screen, assets) = createScreen(nav = nav, clock = clock, motionEnabled = false)
        try {
            screen.show()
            assertFalse(screen.mainMenuView.isEntryActive)
            assertTrue(screen.mainMenuView.currentFrame.allSettled)
            assertEquals(MainMenuItemId.SOLO, screen.mainMenuView.focusedItemId)
            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(1, nav.localLobbyCalls, "reduced motion must be immediately operable")
            drainUi()
            assertNotNull(screen.errorMessage)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `single start is guarded and failure restores the entry`() {
        val clock = Clock(nowMs = 0L)
        val nav = FakeNav()
        val pending = CompletableFuture<LobbySession>()
        nav.localFutureProvider = { pending }
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        try {
            screen.show()
            clock.nowMs = 940L
            screen.mainMenuView.update(940L)
            val entryCount = screen.mainMenuView.entryPlayCount
            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(1, nav.localLobbyCalls)
            assertEquals(entryCount, screen.mainMenuView.entryPlayCount, "status refresh must not replay")
            assertFalse(screen.mainMenuView.isItemEnabled(MainMenuItemId.SOLO))
            assertFalse(screen.mainMenuView.isItemEnabled(MainMenuItemId.ONLINE))

            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(1, nav.localLobbyCalls, "second tap while starting must be ignored")

            pending.completeExceptionally(IllegalStateException("smoke failure"))
            drainUi()
            assertTrue(screen.mainMenuView.isItemEnabled(MainMenuItemId.SOLO))
            assertTrue(screen.mainMenuView.isItemEnabled(MainMenuItemId.ONLINE))
            assertTrue(screen.errorMessage?.contains("单机启动失败") == true)

            // 点击反馈位移期间该入口禁止再次激活：先把时间轴推进到位移结束再重试。
            clock.nowMs += MainMenuButtonMotion.TOTAL_MS
            screen.mainMenuView.update(clock.nowMs)
            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(2, nav.localLobbyCalls, "failure must allow a clean retry")
            drainUi()        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `createRoom failure detaches closes local lobby and restores online plus retry`() {
        val clock = Clock(nowMs = 0L)
        val nav = FakeNav()
        val firstLobby = FakeLobbySession().apply {
            createRoomProvider = {
                CompletableFuture.failedFuture(IllegalStateException("create room boom"))
            }
        }
        nav.localFutureProvider = { CompletableFuture.completedFuture(firstLobby) }
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        try {
            screen.show()
            clock.nowMs = 940L
            screen.mainMenuView.update(940L)
            tapItem(screen, MainMenuItemId.SOLO)
            drainUi()

            assertEquals(1, nav.localLobbyCalls)
            assertEquals(1, firstLobby.closeCount, "failed createRoom local session must be closed")
            assertNull(screen.activeLobby, "failed local session must not stay active")
            assertEquals("NONE", screen.roleNameForTest)
            assertNull(screen.viewerSeatForTest)
            assertFalse(screen.navigatedToTableForTest)
            assertTrue(screen.mainMenuView.isItemEnabled(MainMenuItemId.SOLO))
            assertTrue(screen.errorMessage?.contains("单机启动失败") == true)

            assertOnlineGoesToTcpDialog(screen)

            val retryLobby = FakeLobbySession().apply {
                createRoomProvider = { CompletableFuture.completedFuture(snapshot(viewerSeat = 2)) }
            }
            nav.localFutureProvider = { CompletableFuture.completedFuture(retryLobby) }
            tapItem(screen, MainMenuItemId.SOLO)
            drainUi()
            assertEquals(2, nav.localLobbyCalls, "retry must open a fresh local lobby")
            assertEquals(1, nav.playerTableCalls, "clean retry must reach the table")
            assertEquals(0, retryLobby.closeCount, "successful retry must retain its session")
            assertEquals(retryLobby, screen.activeLobby)
            assertEquals("PLAYER", screen.roleNameForTest)
            assertEquals(2, screen.viewerSeatForTest)
            assertTrue(screen.navigatedToTableForTest)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `start failure closes attached local lobby and restores online plus retry`() {
        val clock = Clock(nowMs = 0L)
        val nav = FakeNav()
        val firstLobby = FakeLobbySession().apply {
            createRoomProvider = { CompletableFuture.completedFuture(snapshot(viewerSeat = 3)) }
            startProvider = {
                CompletableFuture.failedFuture(IllegalStateException("start boom"))
            }
        }
        nav.localFutureProvider = { CompletableFuture.completedFuture(firstLobby) }
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        try {
            screen.show()
            clock.nowMs = 940L
            screen.mainMenuView.update(940L)
            tapItem(screen, MainMenuItemId.SOLO)
            drainUi()

            assertEquals(1, nav.localLobbyCalls)
            assertEquals(1, firstLobby.closeCount, "start failure must close the attached local session")
            assertNull(screen.activeLobby, "start failure must detach the local session")
            assertEquals("NONE", screen.roleNameForTest)
            assertNull(screen.viewerSeatForTest)
            assertFalse(screen.navigatedToTableForTest)
            assertTrue(screen.mainMenuView.isItemEnabled(MainMenuItemId.ONLINE))
            assertTrue(screen.errorMessage?.contains("单机启动失败") == true)
            assertEquals(0, nav.playerTableCalls)

            assertOnlineGoesToTcpDialog(screen)

            val retryLobby = FakeLobbySession().apply {
                createRoomProvider = { CompletableFuture.completedFuture(snapshot(viewerSeat = 1)) }
            }
            nav.localFutureProvider = { CompletableFuture.completedFuture(retryLobby) }
            tapItem(screen, MainMenuItemId.SOLO)
            drainUi()
            assertEquals(2, nav.localLobbyCalls)
            assertEquals(1, nav.playerTableCalls, "retry after start failure must succeed")
            assertEquals(0, retryLobby.closeCount)
            assertEquals(retryLobby, screen.activeLobby)
            assertEquals("PLAYER", screen.roleNameForTest)
            assertEquals(1, screen.viewerSeatForTest)
            assertTrue(screen.navigatedToTableForTest)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `start rejection closes attached local lobby and restores online plus retry`() {
        val clock = Clock(nowMs = 0L)
        val nav = FakeNav()
        val firstLobby = FakeLobbySession().apply {
            createRoomProvider = { CompletableFuture.completedFuture(snapshot(viewerSeat = 1)) }
            startProvider = {
                CompletableFuture.completedFuture(
                    Receipt("start", ReceiptStatus.REJECTED, null, "not ready")
                )
            }
        }
        nav.localFutureProvider = { CompletableFuture.completedFuture(firstLobby) }
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        try {
            screen.show()
            clock.nowMs = 940L
            screen.mainMenuView.update(940L)
            tapItem(screen, MainMenuItemId.SOLO)
            drainUi()

            assertEquals(1, nav.localLobbyCalls)
            assertEquals(1, firstLobby.closeCount, "rejected start must close the attached local session")
            assertNull(screen.activeLobby)
            assertEquals("NONE", screen.roleNameForTest)
            assertNull(screen.viewerSeatForTest)
            assertFalse(screen.navigatedToTableForTest)
            assertTrue(screen.mainMenuView.isItemEnabled(MainMenuItemId.SOLO))
            assertTrue(screen.errorMessage?.contains("单机开赛被拒绝") == true)
            assertTrue(screen.errorMessage?.contains("not ready") == true)
            assertEquals(0, nav.playerTableCalls)

            assertOnlineGoesToTcpDialog(screen)

            val retryLobby = FakeLobbySession().apply {
                createRoomProvider = { CompletableFuture.completedFuture(snapshot(viewerSeat = 0)) }
            }
            nav.localFutureProvider = { CompletableFuture.completedFuture(retryLobby) }
            tapItem(screen, MainMenuItemId.SOLO)
            drainUi()
            assertEquals(2, nav.localLobbyCalls)
            assertEquals(1, nav.playerTableCalls, "retry after rejection must succeed")
            assertEquals(0, retryLobby.closeCount)
            assertEquals(retryLobby, screen.activeLobby)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `late openLocalLobby result after dispose is closed and never attached`() {
        val clock = Clock(nowMs = 0L)
        val nav = FakeNav()
        val pending = CompletableFuture<LobbySession>()
        nav.localFutureProvider = { pending }
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        screen.show()
        clock.nowMs = 940L
        screen.mainMenuView.update(940L)
        try {
            tapItem(screen, MainMenuItemId.SOLO)
            screen.dispose()
            val lateLobby = FakeLobbySession()
            pending.complete(lateLobby)
            assertEquals(1, lateLobby.closeCount, "late local lobby after dispose must be closed")
            assertNull(screen.activeLobby)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `dispose after local attach closes delivered lobby before createRoom completes`() {
        val clock = Clock(nowMs = 0L)
        val nav = FakeNav()
        val roomFuture = CompletableFuture<GameSnapshotView>()
        val lobby = FakeLobbySession().apply { createRoomProvider = { roomFuture } }
        nav.localFutureProvider = { CompletableFuture.completedFuture(lobby) }
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        try {
            screen.show()
            clock.nowMs = 940L
            screen.mainMenuView.update(940L)
            tapItem(screen, MainMenuItemId.SOLO)
            assertEquals(0, lobby.closeCount)

            screen.dispose()
            assertEquals(1, lobby.closeCount, "dispose after attach must close the delivered lobby")
            assertNull(screen.activeLobby)

            roomFuture.complete(snapshot())
            drainUi()
            assertEquals(1, lobby.closeCount, "late createRoom completion must not reopen or double-close")
            assertNull(screen.activeLobby)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `dispose during attach closes lobby and leaves no active ownership`() {
        val clock = Clock(nowMs = 0L)
        val nav = FakeNav()
        lateinit var screen: MenuScreen
        val lobby = FakeLobbySession().apply {
            onStateChangeHook = { screen.dispose() }
        }
        nav.localFutureProvider = { CompletableFuture.completedFuture(lobby) }
        val pair = createScreen(nav = nav, clock = clock)
        screen = pair.first
        val assets = pair.second
        try {
            screen.show()
            clock.nowMs = 940L
            screen.mainMenuView.update(940L)
            tapItem(screen, MainMenuItemId.SOLO)

            assertEquals(1, lobby.closeCount, "dispose exactly during attach must close the lobby")
            assertNull(screen.activeLobby, "attach must not survive a dispose inside the delivery path")
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    @Test
    fun `stale generation result closes only its own lobby and not the replaced session`() {
        val clock = Clock(nowMs = 0L)
        val nav = FakeNav()
        val pending = CompletableFuture<LobbySession>()
        nav.localFutureProvider = { pending }
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        screen.show()
        clock.nowMs = 940L
        screen.mainMenuView.update(940L)
        try {
            tapItem(screen, MainMenuItemId.SOLO)
            val oldLobby = FakeLobbySession()
            val replacement = FakeLobbySession()

            // Simulate a newer generation replacing the active session while the old open is in flight.
            val generationField = MenuScreen::class.java.getDeclaredField("requestGeneration").apply {
                isAccessible = true
            }
            generationField.setLong(screen, generationField.getLong(screen) + 1L)
            val activeField = MenuScreen::class.java.getDeclaredField("activeLobby").apply {
                isAccessible = true
            }
            activeField.set(screen, replacement)

            pending.complete(oldLobby)
            drainUi()
            assertEquals(1, oldLobby.closeCount, "stale attempt must close its own lobby")
            assertEquals(0, replacement.closeCount, "stale attempt must never close the replaced session")
            assertTrue(screen.activeLobby === replacement)
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    private fun assertOnlineGoesToTcpDialog(screen: MenuScreen) {
        tapItem(screen, MainMenuItemId.ONLINE)
        assertEquals(
            MenuScreen.MenuState.TCP_CONNECT_DIALOG,
            screen.currentState,
            "after local failure online must use TCP dialog, not stale local LOBBY"
        )
        screen.keyDown(Input.Keys.ESCAPE)
        assertEquals(MenuScreen.MenuState.MAIN, screen.currentState)
    }

    @Test
    fun `new menu screen from a finished table replays entry on first show`() {
        val clock = Clock()
        val nav = FakeNav()
        val (screen, assets) = createScreen(nav = nav, clock = clock)
        try {
            screen.show()
            assertEquals(1, screen.mainMenuView.entryPlayCount)
            clock.nowMs = 240L
            screen.mainMenuView.update(240L)
            assertTrue(screen.mainMenuView.isEntryActive)
            screen.show()
            assertEquals(1, screen.mainMenuView.entryPlayCount, "repeated show must not restart an active entry")
        } finally {
            screen.dispose()
            assets.dispose()
        }
    }

    private class FakeNav : TableNavigation {
        var localLobbyCalls = 0
        var remoteLobbyCalls = 0
        var playerTableCalls = 0
        var spectatorTableCalls = 0
        var exitCalls = 0
        var leaveCalls = 0
        var localFutureProvider: () -> CompletableFuture<LobbySession> = {
            CompletableFuture.failedFuture(IllegalStateException("local start failed for test"))
        }

        override fun openLocalLobby(): CompletableFuture<LobbySession> {
            localLobbyCalls++
            return localFutureProvider()
        }

        override fun openRemoteLobby(host: String, port: Int): CompletableFuture<LobbySession> {
            remoteLobbyCalls++
            return CompletableFuture.failedFuture(IllegalStateException("remote disabled for test"))
        }

        override fun showPlayerTable() {
            playerTableCalls++
        }

        override fun showSpectatorTable() {
            spectatorTableCalls++
        }

        override fun leaveToMenu(): CompletableFuture<Receipt?> =
            CompletableFuture.completedFuture(Receipt("leave", ReceiptStatus.APPLIED)).also {
                leaveCalls++
            }

        override fun exitGame() {
            exitCalls++
        }
    }

    private class FakeLobbySession : LobbySession {
        private val closeInvocations = AtomicInteger()
        val closeCount: Int get() = closeInvocations.get()
        var createRoomProvider: () -> CompletableFuture<GameSnapshotView> = {
            CompletableFuture.failedFuture(UnsupportedOperationException("not used"))
        }
        var startProvider: () -> CompletableFuture<Receipt> = {
            CompletableFuture.completedFuture(Receipt("start", ReceiptStatus.APPLIED))
        }
        var onStateChangeHook: (() -> Unit)? = null

        override fun close() {
            closeInvocations.incrementAndGet()
        }

        override fun onStateChange(listener: (ConnectionState) -> Unit): AutoCloseable {
            onStateChangeHook?.invoke()
            return AutoCloseable {}
        }
        override fun listRooms(): CompletableFuture<List<RoomSummaryView>> =
            CompletableFuture.completedFuture(emptyList())

        override fun createRoom(playerName: String?, config: MatchConfig?): CompletableFuture<GameSnapshotView> =
            createRoomProvider()

        override fun joinRoom(roomId: String, playerName: String?, preferSeat: Int?): CompletableFuture<GameSnapshotView> =
            CompletableFuture.failedFuture(UnsupportedOperationException("not used"))

        override fun resumeSession(roomId: String, token: String, cursor: Long?): CompletableFuture<GameSnapshotView> =
            CompletableFuture.failedFuture(UnsupportedOperationException("not used"))

        override fun joinSpectator(roomId: String, spectatorName: String?): CompletableFuture<com.majiang.client.api.PublicSnapshotView> =
            CompletableFuture.failedFuture(UnsupportedOperationException("not used"))

        override fun resumeSpectator(roomId: String, spectatorId: String, resumeToken: String, checkpoint: com.majiang.client.api.PublicCheckpoint?): CompletableFuture<com.majiang.client.api.PublicSnapshotView> =
            CompletableFuture.failedFuture(UnsupportedOperationException("not used"))

        override fun leaveRoom(): CompletableFuture<Receipt> =
            CompletableFuture.completedFuture(Receipt("leave", ReceiptStatus.APPLIED))

        override fun getRoomInfo(): CompletableFuture<RoomInfo> =
            CompletableFuture.completedFuture(RoomInfo("88888888", RoomLifecycle.LOBBY, members = emptyList()))

        override fun setReady(ready: Boolean): CompletableFuture<Receipt> =
            CompletableFuture.completedFuture(Receipt("ready", ReceiptStatus.APPLIED))

        override fun updateConfig(config: RoomConfig): CompletableFuture<Receipt> =
            CompletableFuture.completedFuture(Receipt("config", ReceiptStatus.APPLIED))

        override fun start(): CompletableFuture<Receipt> = startProvider()

        override fun onRoomUpdate(listener: (RoomInfo) -> Unit): AutoCloseable = AutoCloseable {}
    }
}
