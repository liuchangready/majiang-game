package com.majiang.presentation.menu

import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.client.api.ConnectionState
import com.majiang.client.api.LobbySession
import com.majiang.client.api.MatchConfig
import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.RoomInfo
import com.majiang.client.api.RoomLifecycle
import com.majiang.client.api.RoomSummaryView
import com.majiang.presentation.common.TableNavigation
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.SkinManager
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.CompletableFuture

/**
 * D-04 菜单子组件架构契约与行为不变量测试 (MenuScreenSubViewsContractTest)。
 *
 * 验证：
 * 1. 子视图纯展示组件契约：不直接依赖 LobbySession 与 TableNavigation；
 * 2. 状态保持契约：ConnectDialogView 输入在关闭再打开后保留；
 * 3. 规则配置契约：CreateRoomDialogView 开关在关闭再打开后保留，且确认时准确上报父层构造 MatchConfig；
 * 4. 输码清空语义：从大厅再次点击“输入房号加入”时房间号输入清空。
 */
class MenuScreenSubViewsContractTest : GdxHeadlessTestRunner() {

    @Test
    fun testSubViewsArePurePresentationComponentsWithoutSessionOrNavigation() {
        val subViewClasses = listOf(
            MainMenuView::class.java,
            ConnectDialogView::class.java,
            LobbyListView::class.java,
            CreateRoomDialogView::class.java,
            JoinRoomDialogView::class.java,
            RoomPreparationView::class.java
        )

        val forbiddenTypes = setOf(
            LobbySession::class.java,
            TableNavigation::class.java
        )

        for (clazz in subViewClasses) {
            // 构造函数参数类型检查
            for (ctor in clazz.constructors) {
                for (paramType in ctor.parameterTypes) {
                    assertFalse(
                        forbiddenTypes.contains(paramType),
                        "${clazz.simpleName} constructor should not accept forbidden type: ${paramType.simpleName}"
                    )
                }
            }
            // 公开字段类型检查
            for (field in clazz.fields) {
                assertFalse(
                    forbiddenTypes.contains(field.type),
                    "${clazz.simpleName} public field ${field.name} should not have forbidden type: ${field.type.simpleName}"
                )
            }
            // 公开方法参数与返回值类型检查
            for (method in clazz.methods) {
                if (method.declaringClass == Any::class.java) continue
                assertFalse(
                    forbiddenTypes.contains(method.returnType),
                    "${clazz.simpleName}.${method.name} return type should not be forbidden type: ${method.returnType.simpleName}"
                )
                for (paramType in method.parameterTypes) {
                    assertFalse(
                        forbiddenTypes.contains(paramType),
                        "${clazz.simpleName}.${method.name} parameter should not be forbidden type: ${paramType.simpleName}"
                    )
                }
            }
        }
    }

    @Test
    fun testConnectDialogInputPersistenceAcrossReopen() {
        val assets = UiAssets.createDefault(SkinManager())
        val nav = FakeContractNavigation()
        val menu = MenuScreen(assets, nav, null)
        try {
            assertEquals(MenuScreen.MenuState.MAIN, menu.currentState)

            // 打开 TCP 连接弹窗 (点击 btnOpenLobby: y=355f, 720 - 365 = 355)
            menu.touchDown(640, 365, 0, 0)
            assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, menu.currentState)

            // 点击 Host 输入框 (rectHostInput: y=390f, 720 - 330 = 390)
            menu.touchDown(500, 330, 0, 0)
            assertEquals(ConnectDialogView.ActiveInput.HOST, menu.connectDialogView.activeInput)

            // 输入自定义 host 后缀
            menu.keyTyped('.')
            menu.keyTyped('8')
            menu.keyTyped('8')
            val expectedHost = "127.0.0.1.88"
            assertEquals(expectedHost, menu.connectDialogView.inputHost)

            // 点击返回 (btnTcpCancel: y=220f, 720 - 500 = 220)
            menu.touchDown(760, 500, 0, 0)
            assertEquals(MenuScreen.MenuState.MAIN, menu.currentState)

            // 再次打开 TCP 弹窗
            menu.touchDown(640, 365, 0, 0)
            assertEquals(MenuScreen.MenuState.TCP_CONNECT_DIALOG, menu.currentState)

            // 验证输入内容在同一 MenuScreen 生命周期内保留
            assertEquals(expectedHost, menu.connectDialogView.inputHost)
        } finally {
            menu.dispose()
            assets.dispose()
        }
    }

    @Test
    fun testCreateRoomDialogConfigPersistenceAndConfirmationPayload() {
        val assets = UiAssets.createDefault(SkinManager())
        val nav = FakeContractNavigation()
        val lobby = FakeContractLobbySession()
        val menu = MenuScreen(assets, nav, lobby)
        try {
            assertEquals(MenuScreen.MenuState.LOBBY, menu.currentState)

            // 进入建房弹窗 (btnLobbyCreateRoom: y=560f, 720 - 160 = 560)
            menu.touchDown(190, 160, 0, 0)
            assertEquals(MenuScreen.MenuState.CREATE_ROOM_DIALOG, menu.currentState)

            // 初始值检查
            assertTrue(menu.createRoomDialogView.cfgIsHanchan, "Default match length should be Hanchan")
            assertTrue(menu.createRoomDialogView.cfgShuffleCheating, "Default shuffle cheating should be enabled")

            // 点击切换半庄/东风 (btnCfgWind: y=430..472, screenY = 720 - 451 = 269)
            menu.touchDown(400, 269, 0, 0)
            assertFalse(menu.createRoomDialogView.cfgIsHanchan, "Should switch to Tonpuusen (East only)")

            // 点击切换码牌开关 (btnCfgShuffle: y=300..342, screenY = 720 - 321 = 399)
            menu.touchDown(400, 399, 0, 0)
            assertFalse(menu.createRoomDialogView.cfgShuffleCheating, "Should disable shuffle cheating")

            // 取消建房返回大厅 (btnCreateCancel: y=190..236, screenY = 720 - 190 = 530)
            menu.touchDown(780, 530, 0, 0)
            assertEquals(MenuScreen.MenuState.LOBBY, menu.currentState)

            // 再次进入建房弹窗
            menu.touchDown(190, 160, 0, 0)
            assertEquals(MenuScreen.MenuState.CREATE_ROOM_DIALOG, menu.currentState)

            // 验证状态在同一 MenuScreen 生命周期内保留
            assertFalse(menu.createRoomDialogView.cfgIsHanchan, "Persisted cfgIsHanchan should still be false")
            assertFalse(menu.createRoomDialogView.cfgShuffleCheating, "Persisted cfgShuffleCheating should still be false")

            // 点击确认创建房间 (btnCreateConfirm: y=190..236, screenX=400, screenY=530)
            menu.touchDown(400, 530, 0, 0)

            // 验证 Fake 捕获的 MatchConfig 规则
            val captured = lobby.lastCreatedConfig
            assertNotNull(captured, "lobby.createRoom should have received MatchConfig")
            assertEquals(listOf(1), captured!!.winds, "Winds should be East-only [1] when not Hanchan")
            assertFalse(captured.shuffleConfig.enabled, "Shuffle cheating config should be disabled")
            assertFalse(captured.shuffleConfig.seeThroughEnabled, "See-through must follow the shuffle preset")
        } finally {
            menu.dispose()
            assets.dispose()
        }
    }

    @Test
    fun testDefaultRoomPresetEnablesSeeThroughWithFixedTenPointCost() {
        val assets = UiAssets.createDefault(SkinManager())
        val nav = FakeContractNavigation()
        val lobby = FakeContractLobbySession()
        val menu = MenuScreen(assets, nav, lobby)
        try {
            menu.touchDown(190, 160, 0, 0)
            assertEquals(MenuScreen.MenuState.CREATE_ROOM_DIALOG, menu.currentState)
            menu.touchDown(400, 530, 0, 0)
            val captured = lobby.lastCreatedConfig
            assertNotNull(captured)
            assertTrue(captured!!.shuffleConfig.enabled)
            assertTrue(captured.shuffleConfig.seeThroughEnabled, "Room preset must turn see-through on with shuffle")
            assertEquals(10, captured.cheatEconomy.costs["SEE_THROUGH"])
        } finally {
            menu.dispose()
            assets.dispose()
        }
    }

    @Test
    fun testJoinRoomInputResetOnEntryFromLobby() {
        val assets = UiAssets.createDefault(SkinManager())
        val nav = FakeContractNavigation()
        val lobby = FakeContractLobbySession()
        val menu = MenuScreen(assets, nav, lobby)
        try {
            assertEquals(MenuScreen.MenuState.LOBBY, menu.currentState)

            // 点击“输入房号加入” (btnLobbyJoinById: y=495f, screenY = 720 - 510 = 210)
            menu.touchDown(190, 210, 0, 0)
            assertEquals(MenuScreen.MenuState.JOIN_ROOM_DIALOG, menu.currentState)
            assertTrue(menu.joinRoomDialogView.isInputActive)

            // 输入房间号
            menu.keyTyped('8')
            menu.keyTyped('8')
            menu.keyTyped('8')
            assertEquals("888", menu.joinRoomDialogView.inputRoomId)

            // 取消返回大厅 (btnJoinCancel: y=270..314, screenY = 720 - 290 = 430)
            menu.touchDown(700, 430, 0, 0)
            assertEquals(MenuScreen.MenuState.LOBBY, menu.currentState)

            // 再次点击“输入房号加入”
            menu.touchDown(190, 210, 0, 0)
            assertEquals(MenuScreen.MenuState.JOIN_ROOM_DIALOG, menu.currentState)

            // 验证原代码语义：从大厅重新点击加入时清空 inputRoomId 并激活输入
            assertEquals("", menu.joinRoomDialogView.inputRoomId, "inputRoomId must be reset on entry from lobby")
            assertTrue(menu.joinRoomDialogView.isInputActive, "input should be active on entry from lobby")
        } finally {
            menu.dispose()
            assets.dispose()
        }
    }

    private class FakeContractNavigation : TableNavigation {
        override fun openLocalLobby(): CompletableFuture<LobbySession> =
            CompletableFuture.completedFuture(FakeContractLobbySession())

        override fun openRemoteLobby(host: String, port: Int): CompletableFuture<LobbySession> =
            CompletableFuture.completedFuture(FakeContractLobbySession())

        override fun showPlayerTable() {}
        override fun showSpectatorTable() {}

        override fun leaveToMenu(): CompletableFuture<Receipt?> =
            CompletableFuture.completedFuture(Receipt("leave", ReceiptStatus.APPLIED))

        override fun exitGame() {}
    }

    private class FakeContractLobbySession : LobbySession {
        var lastCreatedConfig: MatchConfig? = null

        override fun close() {}
        override fun onStateChange(listener: (ConnectionState) -> Unit): AutoCloseable = AutoCloseable {}
        override fun listRooms(): CompletableFuture<List<RoomSummaryView>> =
            CompletableFuture.completedFuture(emptyList())

        override fun createRoom(playerName: String?, config: MatchConfig?): CompletableFuture<com.majiang.client.api.GameSnapshotView> {
            lastCreatedConfig = config
            return CompletableFuture()
        }

        override fun joinRoom(roomId: String, playerName: String?, preferSeat: Int?): CompletableFuture<com.majiang.client.api.GameSnapshotView> =
            CompletableFuture()

        override fun resumeSession(roomId: String, token: String, cursor: Long?): CompletableFuture<com.majiang.client.api.GameSnapshotView> =
            CompletableFuture()

        override fun joinSpectator(roomId: String, spectatorName: String?): CompletableFuture<com.majiang.client.api.PublicSnapshotView> =
            CompletableFuture()

        override fun resumeSpectator(roomId: String, spectatorId: String, resumeToken: String, checkpoint: com.majiang.client.api.PublicCheckpoint?): CompletableFuture<com.majiang.client.api.PublicSnapshotView> =
            CompletableFuture()

        override fun leaveRoom(): CompletableFuture<Receipt> =
            CompletableFuture.completedFuture(Receipt("leave", ReceiptStatus.APPLIED))

        override fun getRoomInfo(): CompletableFuture<RoomInfo> =
            CompletableFuture.completedFuture(RoomInfo("88888888", RoomLifecycle.LOBBY, members = emptyList()))

        override fun setReady(ready: Boolean): CompletableFuture<Receipt> =
            CompletableFuture.completedFuture(Receipt("ready", ReceiptStatus.APPLIED))

        override fun updateConfig(config: com.majiang.client.api.RoomConfig): CompletableFuture<Receipt> =
            CompletableFuture.completedFuture(Receipt("config", ReceiptStatus.APPLIED))

        override fun start(): CompletableFuture<Receipt> =
            CompletableFuture.completedFuture(Receipt("start", ReceiptStatus.APPLIED))

        override fun onRoomUpdate(listener: (RoomInfo) -> Unit): AutoCloseable = AutoCloseable {}
    }
}
