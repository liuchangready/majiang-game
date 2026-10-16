package com.majiang.presentation.menu

import com.badlogic.gdx.Input
import com.badlogic.gdx.Gdx
import com.majiang.MahjongGame
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.ConnectionState
import com.majiang.client.api.LobbySession
import com.majiang.client.api.MatchConfig
import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.RoomInfo
import com.majiang.client.api.RoomLifecycle
import com.majiang.client.api.RoomSummaryView
import com.majiang.client.session.ServerConnectionSession
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.SkinId
import com.majiang.presentation.common.render.SkinManager
import com.majiang.presentation.game.GameScreen
import com.majiang.server.tcp.TcpServerConfig
import com.majiang.server.tcp.TcpTableServer
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class MenuScreenTest : GdxHeadlessTestRunner() {

    private fun <T> CompletableFuture<T>.await(): T = get(4, TimeUnit.SECONDS)

    @Test
    fun testMahjongGameInstanceCreation() {
        val game = MahjongGame()
        assertNotNull(game, "MahjongGame instance should be created successfully")
        assertNotNull(game.skinManager, "SkinManager should be present")
        assertFalse(game.isAssetsInitialized, "UiAssets should not be initialized before create()")
    }

    @Test
    fun testMahjongGameLifecycleAndUiAssets() {
        val game = MahjongGame()
        game.create()
        assertTrue(game.isAssetsInitialized, "UiAssets should be initialized after create()")
        assertNotNull(game.uiAssets, "UiAssets instance should be accessible")
        assertNotNull(game.batch, "SpriteBatch should be accessible")
        assertNotNull(game.shapeRenderer, "ShapeRenderer should be accessible")
        assertNotNull(game.font, "Fallback BitmapFont should be accessible")
        assertNotNull(game.effectiveFont, "Effective font should be accessible")
        assertTrue(game.screen is MenuScreen, "Initial screen should be MenuScreen")

        game.dispose()
    }

    @Test
    fun testTableNavigationOpenLocalLobby() {
        val game = MahjongGame()
        game.create()

        val lobbyFuture = game.openLocalLobby()
        val lobby = lobbyFuture.await()
        assertNotNull(lobby, "openLocalLobby should return a live LobbySession")
        assertNotNull(game.inProcessServer, "inProcessServer should be active")
        assertFalse(game.inProcessServer!!.closed, "inProcessServer should not be closed")
        assertEquals(ConnectionState.CONNECTED, lobby.connectionState)

        // 真实在进程内大厅建房
        val snap = lobby.createRoom(playerName = "测试房主", config = MatchConfig()).await()
        assertNotNull(snap, "createRoom should return valid initial snapshot")

        val roomInfo = lobby.getRoomInfo().await()
        assertNotNull(roomInfo, "getRoomInfo should return valid room state")
        assertTrue(roomInfo.members.isNotEmpty(), "Room should contain members")
        assertEquals("测试房主", roomInfo.members[0].playerName)

        // 测试 LobbySession.start()
        val startReceipt = lobby.start().await()
        assertEquals(ReceiptStatus.APPLIED, startReceipt.status, "start() should be APPLIED for auto-readied singleplayer room")

        game.dispose()
    }

    @Test
    fun testQuickStartCreatesAndStartsARealLocalRoom() {
        val game = MahjongGame()
        game.create()
        try {
            game.launchGame()
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
            while (game.screen !is GameScreen && System.nanoTime() < deadline) {
                Thread.sleep(10)
            }
            assertTrue(game.screen is GameScreen, "Quick start must navigate to the player table after start")
            assertNotNull(game.currentSession?.currentSnapshot, "Started room must publish a player snapshot")
            assertNotNull(game.currentSession?.currentSnapshot?.preparation, "Quick start must keep the shuffle preparation phase")
            assertEquals(100, game.currentSession?.currentSnapshot?.cheatPoints?.get(0))
            assertNotNull(game.inProcessServer?.currentHost, "Quick start must retain the local authoritative host")
        } finally {
            game.dispose()
        }
    }

    @Test
    fun testRealTcpLobbyConnectionCanCreateAndListRoom() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()
        val game = MahjongGame()
        game.create()
        try {
            val lobby = game.openRemoteLobby("127.0.0.1", server.boundPort).await()
            game.setScreen(MenuScreen(game.uiAssets, game, lobby))
            assertEquals(MenuScreen.MenuState.LOBBY, (game.screen as MenuScreen).currentState)
            assertEquals(ConnectionState.CONNECTED, lobby.connectionState)

            val created = lobby.createRoom(playerName = "远端房主", config = MatchConfig()).await()
            assertNotNull(created)
            val room = lobby.getRoomInfo().await()
            assertEquals("远端房主", room.members.first().playerName)
            ServerConnectionSession.connectTcp("127.0.0.1", server.boundPort).await().use { observer ->
                assertTrue(observer.listRooms().await().any { it.roomId == room.roomId },
                    "An unbound TCP lobby must see the newly created room")
            }
        } finally {
            game.dispose()
            server.close()
        }
    }

    @Test
    fun testRealTcpLobbyStartsPlayerTableAndReturnsToLobby() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()
        val game = MahjongGame()
        game.create()
        try {
            val host = game.openRemoteLobby("127.0.0.1", server.boundPort).await()
            game.setScreen(MenuScreen(game.uiAssets, game, host))
            host.createRoom(playerName = "远端房主", config = MatchConfig()).await()
            val roomId = host.getRoomInfo().await().roomId

            ServerConnectionSession.connectTcp("127.0.0.1", server.boundPort).await().use { guest ->
                val guestSnapshot = guest.joinRoom(roomId, playerName = "远端客人", preferSeat = 1).await()
                assertEquals(1, guestSnapshot.viewerSeat)
                assertEquals(ReceiptStatus.APPLIED, guest.setReady(true).await().status)
                assertEquals(ReceiptStatus.APPLIED, host.setReady(true).await().status)
                assertEquals(ReceiptStatus.APPLIED, host.start().await().status)

                game.showPlayerTable()
                assertTrue(game.screen is GameScreen, "TCP room must open the real V4 player table")
                assertNotNull(game.currentSession?.currentSnapshot?.roundId)
                assertNotNull(guest.currentSnapshot?.roundId, "Guest must receive the same started room")

                assertEquals(ReceiptStatus.APPLIED, game.leaveToMenu().await()?.status)
                assertTrue(game.screen is MenuScreen, "Remote leave should return to the lobby")
                assertEquals(MenuScreen.MenuState.LOBBY, (game.screen as MenuScreen).currentState)
                assertEquals(ConnectionState.CONNECTED, host.connectionState)
            }
        } finally {
            game.dispose()
            server.close()
        }
    }

    @Test
    fun testTableNavigationLeaveToMenu() {
        val game = MahjongGame()
        game.create()

        val lobby = game.openLocalLobby().await()
        lobby.createRoom(playerName = "玩家").await()
        assertNotNull(game.currentSession)

        val leaveReceipt = game.leaveToMenu().await()
        assertNotNull(leaveReceipt)
        assertEquals(ReceiptStatus.APPLIED, leaveReceipt!!.status)
        assertTrue(game.screen is MenuScreen, "Current screen should return to MenuScreen")
        assertNull(game.inProcessServer, "InProcessServer should be cleaned up on local table leave")

        game.dispose()
    }

    @Test
    fun testContinuousEnterAndExitTableWithSkinSwitch() {
        val game = MahjongGame()
        game.create()

        // 连续 3 次进退桌与换肤验证，确保共享资源不被提前或重复 dispose
        repeat(3) { index ->
            val lobby = game.openLocalLobby().await()
            lobby.createRoom(playerName = "玩家$index").await()

            // 切换皮肤
            game.uiAssets.skinManager.toggleSkin()
            val skinId = game.uiAssets.skinManager.currentSkinId
            assertNotNull(skinId)

            // 退桌回到大厅
            val receipt = game.leaveToMenu().await()
            assertEquals(ReceiptStatus.APPLIED, receipt!!.status)
            assertTrue(game.screen is MenuScreen)
            assertEquals(skinId, game.uiAssets.skinManager.currentSkinId, "Skin selection should persist across table transitions")
        }

        game.dispose()
    }

    @Test
    fun testMenuScreenStateTransitions() {
        val game = MahjongGame()
        game.create()
        val menuScreen = game.screen as MenuScreen

        assertEquals(MenuScreen.MenuState.MAIN, menuScreen.currentState)

        // 打开秘籍弹窗
        menuScreen.touchDown(640, 495, 0, 0) // 点击 btnSecrets
        assertEquals(MenuScreen.MenuState.SECRETS_MODAL, menuScreen.currentState)

        // ESC 关闭弹窗
        menuScreen.keyDown(Input.Keys.ESCAPE)
        assertEquals(MenuScreen.MenuState.MAIN, menuScreen.currentState)

        // 打开设置弹窗
        menuScreen.touchDown(640, 560, 0, 0) // 点击 btnSettings
        assertEquals(MenuScreen.MenuState.SETTINGS_MODAL, menuScreen.currentState)

        // ESC 关闭设置弹窗
        menuScreen.keyDown(Input.Keys.ESCAPE)
        assertEquals(MenuScreen.MenuState.MAIN, menuScreen.currentState)

        // 打开 TCP 连接弹窗
        menuScreen.touchDown(640, 365, 0, 0) // 点击 btnOpenLobby
        assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, menuScreen.currentState)

        // ESC 返回主菜单
        menuScreen.keyDown(Input.Keys.ESCAPE)
        assertEquals(MenuScreen.MenuState.MAIN, menuScreen.currentState)

        game.dispose()
    }

    @Test
    fun testMenuScreenKeyTypedForInputFields() {
        val skinManager = SkinManager()
        val assets = UiAssets.createDefault(skinManager)
        val nav = FakeTableNavigation()
        val menuScreen = MenuScreen(assets, nav, null)

        // 打开 TCP 弹窗并激活 HOST 输入
        menuScreen.touchDown(640, 365, 0, 0) // 打开弹窗
        menuScreen.touchDown(500, 330, 0, 0) // 点击 Host 输入框

        // 输入测试字符
        menuScreen.keyTyped('1')
        menuScreen.keyTyped('9')
        menuScreen.keyTyped('2')
        menuScreen.keyTyped('\b') // 退格删除 '2'

        // 点击取消
        menuScreen.touchDown(760, 500, 0, 0) // btnTcpCancel
        assertEquals(MenuScreen.MenuState.MAIN, menuScreen.currentState)

        assets.dispose()
    }

    @Test
    fun testCancelledRemoteConnectionClosesItsLateSession() {
        val assets = UiAssets.createDefault(SkinManager())
        val nav = FakeTableNavigation()
        val pending = CompletableFuture<LobbySession>()
        nav.remoteLobbyFuture = pending
        val menu = MenuScreen(assets, nav, null)
        try {
            menu.touchDown(640, 365, 0, 0)
            menu.touchDown(520, 478, 0, 0)
            assertEquals(MenuScreen.MenuState.CONNECTING, menu.currentState)

            menu.keyDown(Input.Keys.ESCAPE)
            assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, menu.currentState)

            val lateLobby = FakeLobbySession()
            pending.complete(lateLobby)
            assertTrue(lateLobby.closeLatch.await(3, TimeUnit.SECONDS), "Cancelled connection must close a late session")
            assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, menu.currentState)
            assertNull(menu.activeLobby, "Cancelled connection must not enter the lobby")
        } finally {
            menu.dispose()
            assets.dispose()
        }
    }

    @Test
    fun testLobbyStateUpdatesSurviveRoomListRefresh() {
        val assets = UiAssets.createDefault(SkinManager())
        val lobby = FakeLobbySession()
        val menu = MenuScreen(assets, FakeTableNavigation(), lobby)
        try {
            menu.touchDown(190, 290, 0, 0)
            lobby.emitState(ConnectionState.CLOSED)
            val processed = CountDownLatch(1)
            Gdx.app.postRunnable { processed.countDown() }
            assertTrue(processed.await(3, TimeUnit.SECONDS))
            assertTrue(menu.errorMessage?.startsWith("连接已关闭") == true,
                "A room list request must not silence the live lobby connection listener")
        } finally {
            menu.dispose()
            assets.dispose()
        }
    }

    @Test
    fun testMenuScreenWithFakeLobbySession() {
        val skinManager = SkinManager()
        val assets = UiAssets.createDefault(skinManager)
        val nav = FakeTableNavigation()
        val fakeLobby = FakeLobbySession()

        val menuScreen = MenuScreen(assets, nav, fakeLobby)
        assertEquals(MenuScreen.MenuState.LOBBY, menuScreen.currentState)

        // 刷新房间
        menuScreen.touchDown(190, 290, 0, 0) // 点击 btnLobbyRefresh

        // 切换到建房规则弹窗
        menuScreen.touchDown(190, 160, 0, 0) // 点击 btnLobbyCreateRoom
        assertEquals(MenuScreen.MenuState.CREATE_ROOM_DIALOG, menuScreen.currentState)

        // 取消建房回到大厅
        menuScreen.touchDown(780, 530, 0, 0) // 点击 btnCreateCancel
        assertEquals(MenuScreen.MenuState.LOBBY, menuScreen.currentState)

        menuScreen.dispose()
        assets.dispose()
    }

    @Test
    fun testLobbyBackDisconnectsSessionAndLeavesToMenu() {
        val skinManager = SkinManager()
        val assets = UiAssets.createDefault(skinManager)
        val nav = FakeTableNavigation()
        val fakeLobby = FakeLobbySession()

        val menuScreen = MenuScreen(assets, nav, fakeLobby)
        assertEquals(MenuScreen.MenuState.LOBBY, menuScreen.currentState)

        // 点击 btnLobbyBack (x=190, y=720 - 143 = 577)
        menuScreen.touchDown(190, 577, 0, 0)
        val uiProcessed = CountDownLatch(1)
        Gdx.app.postRunnable { uiProcessed.countDown() }
        assertTrue(uiProcessed.await(3, TimeUnit.SECONDS))
        assertEquals(MenuScreen.MenuState.MAIN, menuScreen.currentState, "Lobby back must transition to MAIN")
        assertNull(menuScreen.activeLobby, "Active lobby must be cleared on back")

        menuScreen.dispose()
        assets.dispose()
    }

    private class FakeTableNavigation : TableNavigation {
        var localLobbyOpened = false
        var remoteLobbyOpened = false
        var playerTableShown = false
        var spectatorTableShown = false
        var gameExited = false
        var remoteLobbyFuture: CompletableFuture<LobbySession>? = null

        override fun openLocalLobby(): CompletableFuture<LobbySession> {
            localLobbyOpened = true
            return CompletableFuture.completedFuture(FakeLobbySession())
        }

        override fun openRemoteLobby(host: String, port: Int): CompletableFuture<LobbySession> {
            remoteLobbyOpened = true
            return remoteLobbyFuture ?: CompletableFuture.completedFuture(FakeLobbySession())
        }

        override fun showPlayerTable() {
            playerTableShown = true
        }

        override fun showSpectatorTable() {
            spectatorTableShown = true
        }

        override fun leaveToMenu(): CompletableFuture<Receipt?> =
            CompletableFuture.completedFuture(Receipt("leave", ReceiptStatus.APPLIED))

        override fun exitGame() {
            gameExited = true
        }
    }

    private class FakeLobbySession(
        private val sampleRooms: List<RoomSummaryView> = listOf(
            RoomSummaryView(
                roomId = "88888888",
                roomName = "测试房间",
                hostPlayerName = "房主小李",
                playerCount = 1,
                maxPlayers = 4,
                matchStarted = false,
                configSummary = "半庄/赤五/码牌"
            )
        )
    ) : LobbySession {
        val closeLatch = CountDownLatch(1)
        override fun close() { closeLatch.countDown() }
        private var stateListener: ((ConnectionState) -> Unit)? = null
        override fun onStateChange(listener: (ConnectionState) -> Unit): AutoCloseable {
            stateListener = listener
            return AutoCloseable { if (stateListener === listener) stateListener = null }
        }
        fun emitState(state: ConnectionState) { stateListener?.invoke(state) }
        var startCalled = false
        var readyState = false
        var leaveCalled = false

        override fun listRooms(): CompletableFuture<List<RoomSummaryView>> =
            CompletableFuture.completedFuture(sampleRooms)

        override fun createRoom(playerName: String?, config: MatchConfig?): CompletableFuture<com.majiang.client.api.GameSnapshotView> =
            CompletableFuture.failedFuture(UnsupportedOperationException("Not required for menu fake"))

        override fun joinRoom(roomId: String, playerName: String?, preferSeat: Int?): CompletableFuture<com.majiang.client.api.GameSnapshotView> =
            CompletableFuture.failedFuture(UnsupportedOperationException("Not required for menu fake"))

        override fun resumeSession(roomId: String, token: String, cursor: Long?): CompletableFuture<com.majiang.client.api.GameSnapshotView> =
            CompletableFuture.failedFuture(UnsupportedOperationException("Not required for menu fake"))

        override fun joinSpectator(roomId: String, spectatorName: String?): CompletableFuture<com.majiang.client.api.PublicSnapshotView> =
            CompletableFuture.failedFuture(UnsupportedOperationException("Not required for menu fake"))

        override fun resumeSpectator(roomId: String, spectatorId: String, resumeToken: String, checkpoint: com.majiang.client.api.PublicCheckpoint?): CompletableFuture<com.majiang.client.api.PublicSnapshotView> =
            CompletableFuture.failedFuture(UnsupportedOperationException("Not required for menu fake"))

        override fun leaveRoom(): CompletableFuture<Receipt> {
            leaveCalled = true
            return CompletableFuture.completedFuture(Receipt("leave", ReceiptStatus.APPLIED))
        }

        override fun getRoomInfo(): CompletableFuture<RoomInfo> =
            CompletableFuture.completedFuture(RoomInfo("88888888", RoomLifecycle.LOBBY, members = listOf(
                com.majiang.client.api.RoomMember(0, "房主小李", isHost = true, isReady = readyState, role = "HUMAN")
            )))

        override fun setReady(ready: Boolean): CompletableFuture<Receipt> {
            readyState = ready
            return CompletableFuture.completedFuture(Receipt("ready", ReceiptStatus.APPLIED))
        }

        override fun updateConfig(config: com.majiang.client.api.RoomConfig): CompletableFuture<Receipt> =
            CompletableFuture.completedFuture(Receipt("config", ReceiptStatus.APPLIED))

        override fun start(): CompletableFuture<Receipt> {
            startCalled = true
            return CompletableFuture.completedFuture(Receipt("start", ReceiptStatus.APPLIED))
        }

        override fun onRoomUpdate(listener: (RoomInfo) -> Unit): AutoCloseable = AutoCloseable {}
    }
}
