package com.majiang.presentation.testing

import com.majiang.client.api.LobbySession
import com.majiang.client.api.Receipt
import com.majiang.client.api.ReceiptStatus
import com.majiang.presentation.common.TableNavigation
import java.util.concurrent.CompletableFuture

/**
 * 表现层导航测试桩 (FakeTableNavigation)。
 */
class FakeTableNavigation : TableNavigation {
    var playerTableShown = false
    var spectatorTableShown = false
    var leftToMenu = false
    var exited = false
    var openedLocalLobby = false
    var openedRemoteLobbyHost: String? = null
    var openedRemoteLobbyPort: Int? = null

    override fun openLocalLobby(): CompletableFuture<LobbySession> {
        openedLocalLobby = true
        return CompletableFuture.completedFuture(null)
    }

    override fun openRemoteLobby(host: String, port: Int): CompletableFuture<LobbySession> {
        openedRemoteLobbyHost = host
        openedRemoteLobbyPort = port
        return CompletableFuture.completedFuture(null)
    }

    override fun showPlayerTable() {
        playerTableShown = true
    }

    override fun showSpectatorTable() {
        spectatorTableShown = true
    }

    override fun leaveToMenu(): CompletableFuture<Receipt?> {
        leftToMenu = true
        return CompletableFuture.completedFuture(Receipt("leave", ReceiptStatus.APPLIED))
    }

    override fun exitGame() {
        exited = true
    }
}
