package com.majiang.v4lobby

import com.majiang.client.transport.udp.UdpClient
import com.majiang.server.api.*
import com.majiang.server.inprocess.InProcessServer
import com.majiang.server.udp.UdpTableServer
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolTimeControlConfig
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** R4B player lobby behavior through actual transport and facade contracts. */
@Timeout(20)
class V4LobbyAcceptanceTest {
    private fun <T> CompletableFuture<T>.await(): T = get(4, TimeUnit.SECONDS)
    private val com.majiang.transport.api.ProtocolPlayerView.closedHand
        get() = myClosedTiles + listOfNotNull(myDrawnTile)
    private fun ready(connection: ServerConnection, value: Boolean): ProtocolActionReceipt = connection.setReady(value).await()

    @Test fun lobbyContractsExposeRoomStateReadinessConfigurationAndUpdates() {
        val protocolMethods = ServerConnection::class.java.methods.map { it.name }.toSet()
        val clientMethods = com.majiang.client.api.LobbySession::class.java.methods.map { it.name }.toSet()
        for (method in listOf("getRoomInfo", "setReady", "updateConfig", "onRoomUpdate")) {
            assertTrue(method in protocolMethods, "Protocol lobby missing $method")
            assertTrue(method in clientMethods, "Client lobby missing $method")
        }
    }

    @Test fun localHostCanLeaveAndCreateAnotherRoomOnTheLiveConnection() {
        InProcessServer().use { server ->
            val connection = server.createLocalTable()
            var oldSession = connection.snapshot().await().sessionId
            // Repeated room replacement exercises late initial/start callbacks racing successful leave.
            repeat(20) {
                assertEquals(ProtocolReceiptStatus.APPLIED, connection.leaveRoom().await().status)
                assertFalse(connection.closed, "Room leave must return the connection to lobby")
                val fresh = connection.createRoom(CreateRoomRequest(protocolVersion = 4)).await()
                assertNotEquals(oldSession, fresh.snapshot.sessionId)
                oldSession = fresh.snapshot.sessionId
                assertEquals(ProtocolReceiptStatus.APPLIED, connection.start().await().status,
                    "Singleplayer assembly auto-readies one human and fills AI")
                assertEquals(14, connection.snapshot().await().view.closedHand.size)
            }
        }
    }

    @Test fun overlappingLeaveRequestsCannotInvalidateTheNextRoomBinding() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        InProcessServer().use { server ->
            val connection = server.createLocalTable()
            val gate = server.currentHost!!.enqueueOperation {
                entered.countDown()
                check(release.await(5, TimeUnit.SECONDS))
            }
            try {
                assertTrue(entered.await(2, TimeUnit.SECONDS))
                val first = connection.leaveRoom()
                val duplicate = connection.leaveRoom()
                assertFalse(first.isDone, "The authority queue is deliberately held")
                release.countDown()
                assertEquals(ProtocolReceiptStatus.APPLIED, first.await().status)
                assertFalse(connection.closed)
                val fresh = connection.createRoom(CreateRoomRequest()).await()
                // A duplicate may share the receipt or be explicitly rejected; it cannot detach the next room.
                assertTrue(duplicate.await().status in setOf(ProtocolReceiptStatus.APPLIED, ProtocolReceiptStatus.REJECTED))
                assertFalse(connection.closed)
                assertEquals(fresh.snapshot.sessionId, connection.snapshot().await().sessionId)
                assertEquals(ProtocolReceiptStatus.APPLIED, connection.start().await().status)
                gate.await()
            } finally {
                release.countDown()
            }
        }
    }

    @Test fun guestLeaveKeepsConnectionReusableBeforeAndAfterStart() {
        for (started in listOf(false, true)) {
            InProcessServer(isSingleplayer = false).use { server ->
                server.createConnection().use { host ->
                    server.createConnection().use { guest ->
                        val room = host.createRoom(CreateRoomRequest()).await()
                        guest.joinRoom(JoinRoomRequest(room.roomId)).await()
                        if (started) {
                            ready(host, true)
                            ready(guest, true)
                            assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
                        }
                        val original = host.snapshot().await()
                        assertEquals(ProtocolReceiptStatus.APPLIED, guest.leaveRoom().await().status)
                        assertFalse(guest.closed, "Guest leave must preserve the lobby connection")
                        val fresh = guest.createRoom(CreateRoomRequest()).await()
                        assertNotEquals(room.roomId, fresh.roomId)
                        assertEquals(fresh.snapshot.sessionId, guest.snapshot().await().sessionId)
                        assertEquals(original.sessionId, host.snapshot().await().sessionId)
                        assertEquals(original.roundId, host.snapshot().await().roundId,
                            "Guest leave and independent room creation must not replace the host's round")
                    }
                }
            }
        }
    }

    @Test fun networkCannotStartBeforeHumanMembersAreReady() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                UdpClient.connect("127.0.0.1", server.boundPort).await().use { guest ->
                    val room = host.createRoom(CreateRoomRequest(protocolVersion = 4)).await()
                    guest.joinRoom(JoinRoomRequest(room.roomId)).await()
                    assertEquals(ProtocolReceiptStatus.REJECTED, host.start().await().status)
                    assertTrue(host.snapshot().await().view.closedHand.isEmpty())
                    assertTrue(guest.snapshot().await().view.closedHand.isEmpty())
                }
            }
        }
    }

    @Test fun inProcessResumeRejectsForeignAndExplicitlyRevokedRoomIdentity() {
        for (started in listOf(false, true)) {
            InProcessServer(isSingleplayer = false).use { server ->
                server.createConnection().use { host ->
                    server.createConnection().use { guest ->
                        val room = host.createRoom(CreateRoomRequest(playerName = "Host")).await()
                        val binding = guest.joinRoom(JoinRoomRequest(room.roomId, playerName = "Guest")).await()
                        assertFailsWith<java.util.concurrent.ExecutionException> {
                            guest.resumeSession(ResumeRequest("foreign-room", binding.resumeToken)).await()
                        }
                        assertFailsWith<java.util.concurrent.ExecutionException> {
                            guest.resumeSession(ResumeRequest(room.roomId, "invalid-token")).await()
                        }
                        assertEquals(binding.snapshot.sessionId, guest.snapshot().await().sessionId)
                        if (started) {
                            ready(host, true)
                            ready(guest, true)
                            assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
                        }
                        assertEquals(ProtocolReceiptStatus.APPLIED, guest.leaveRoom().await().status)
                        server.createConnection().use { replacement ->
                            assertFailsWith<java.util.concurrent.ExecutionException> {
                                replacement.resumeSession(ResumeRequest(room.roomId, binding.resumeToken)).await()
                            }
                        }
                        assertEquals(room.snapshot.sessionId, host.snapshot().await().sessionId)
                    }
                }
            }
        }
    }

    @Test fun closingGuestTransportKeepsRoomAndResumeIdentityAlive() {
        InProcessServer(isSingleplayer = false).use { server ->
            server.createConnection().use { host ->
                val guest = server.createConnection()
                try {
                    val room = host.createRoom(CreateRoomRequest(playerName = "Host")).await()
                    val binding = guest.joinRoom(JoinRoomRequest(room.roomId, playerName = "Guest")).await()
                    ready(host, true)
                    ready(guest, true)
                    assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
                    val before = host.snapshot().await()
                    guest.close()
                    assertEquals(before.roundId, host.snapshot().await().roundId,
                        "Closing a guest connection must not dissolve the authoritative room")
                    server.createConnection().use { replacement ->
                        val resumed = replacement.resumeSession(ResumeRequest(room.roomId, binding.resumeToken)).await()
                        assertEquals(binding.seat, resumed.seat)
                        assertEquals(before.roundId, resumed.snapshot.roundId)
                        assertEquals(13, resumed.snapshot.view.closedHand.size)
                        assertEquals(before.clocks.first { it.seat == 0 }.deadline,
                            host.snapshot().await().clocks.first { it.seat == 0 }.deadline)
                    }
                } finally {
                    guest.close()
                }
            }
        }
    }

    @Test fun replacementResumePreventsPreviousConnectionFromChangingReadiness() {
        InProcessServer(isSingleplayer = false).use { server ->
            server.createConnection().use { host ->
                server.createConnection().use { guest ->
                    server.createConnection().use { replacement ->
                        val room = host.createRoom(CreateRoomRequest()).await()
                        val binding = guest.joinRoom(JoinRoomRequest(room.roomId)).await()
                        ready(host, true)
                        ready(guest, true)
                        val before = guest.snapshot().await()
                        val resumed = replacement.resumeSession(
                            ResumeRequest(room.roomId, binding.resumeToken, cursor = 999999L)).await()
                        assertTrue(resumed.snapshot.cursor < 999999L,
                            "Resume must return the server cursor, not trust a supplied future cursor")
                        assertTrue(resumed.snapshot.cursor >= before.cursor)
                        assertEquals(ProtocolReceiptStatus.REJECTED, guest.setReady(false).await().status,
                            "Replaced connection must not mutate the new seat binding")
                        assertTrue(replacement.getRoomInfo().await().members.first { it.seat == binding.seat }.isReady)
                        assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
                    }
                }
            }
        }
    }

    @Test fun hostLeaveSucceedsWhileGuestReceivesRoomClosedWithoutUiSubscription() {
        InProcessServer(isSingleplayer = false).use { server ->
            server.createConnection().use { host ->
                server.createConnection().use { guest ->
                    val room = host.createRoom(CreateRoomRequest()).await()
                    guest.joinRoom(JoinRoomRequest(room.roomId)).await()
                    val terminal = CompletableFuture<String>()
                    guest.onDisconnect { terminal.complete(it) }.use {
                        assertEquals(ProtocolReceiptStatus.APPLIED, host.leaveRoom().await().status)
                        assertFalse(host.closed, "Initiator must return to the unbound lobby")
                        assertEquals(ApiProtocol.ROOM_CLOSED, terminal.await())
                        assertTrue(guest.closed)
                        assertEquals(ProtocolReceiptStatus.REJECTED, guest.setReady(true).await().status)
                    }
                }
            }
        }
    }

    @Test fun slowRoomObserverCannotBlockCallerAuthorityQueryOrConfigurationReceipt() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val armed = AtomicBoolean(false)
        val callers = Executors.newFixedThreadPool(2)
        val server = InProcessServer()
        try {
            val connection = server.createLocalTable()
            val original = connection.getRoomInfo().await()
            val changed = original.config.copy(matchConfig = original.config.matchConfig.copy(targetScore = 32000))
            connection.onRoomUpdate {
                if (armed.compareAndSet(true, false)) {
                    entered.countDown()
                    check(release.await(8, TimeUnit.SECONDS)) { "Reviewer observer was never released" }
                }
            }.use {
                armed.set(true)
                val invocation = callers.submit<CompletableFuture<ProtocolActionReceipt>> {
                    connection.updateConfig(changed)
                }
                assertTrue(entered.await(3, TimeUnit.SECONDS), "Successful configuration must notify room observers")
                val receipt = invocation.get(2, TimeUnit.SECONDS)
                val query = callers.submit<CompletableFuture<ProtocolRoomInfo>> { connection.getRoomInfo() }
                    .get(2, TimeUnit.SECONDS)
                assertEquals(changed, query.get(2, TimeUnit.SECONDS).config)
                assertEquals(ProtocolReceiptStatus.APPLIED, receipt.get(2, TimeUnit.SECONDS).status)
                release.countDown()
            }
        } finally {
            release.countDown()
            callers.shutdownNow()
            server.close()
        }
    }

    @Test fun onlyReadyHostCanStartAfterEveryHumanIsReadyAndRepeatedStartDoesNotRedeal() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                UdpClient.connect("127.0.0.1", server.boundPort).await().use { guest ->
                    val room = host.createRoom(CreateRoomRequest(protocolVersion = 4)).await()
                    guest.joinRoom(JoinRoomRequest(room.roomId)).await()
                    assertEquals(ProtocolReceiptStatus.APPLIED, ready(host, true).status)
                    assertEquals(ProtocolReceiptStatus.REJECTED, host.start().await().status)
                    assertEquals(ProtocolReceiptStatus.APPLIED, ready(guest, true).status)
                    assertEquals(ProtocolReceiptStatus.REJECTED, guest.start().await().status)
                    assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
                    val first = host.snapshot().await()
                    assertEquals(14, first.view.closedHand.size)
                    host.start().await()
                    val repeated = host.snapshot().await()
                    assertEquals(first.roundId, repeated.roundId)
                    assertEquals(first.view.closedHand, repeated.view.closedHand)
                }
            }
        }
    }

    @Test fun withdrawingReadinessBeforeStartPreventsDealingUntilReadyAgain() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                UdpClient.connect("127.0.0.1", server.boundPort).await().use { guest ->
                    val room = host.createRoom(CreateRoomRequest(protocolVersion = 4)).await()
                    guest.joinRoom(JoinRoomRequest(room.roomId)).await()
                    assertEquals(ProtocolReceiptStatus.APPLIED, ready(host, true).status)
                    assertEquals(ProtocolReceiptStatus.APPLIED, ready(guest, true).status)
                    assertEquals(ProtocolReceiptStatus.APPLIED, ready(guest, false).status)
                    assertEquals(ProtocolReceiptStatus.REJECTED, host.start().await().status)
                    assertTrue(host.snapshot().await().view.closedHand.isEmpty())
                    assertEquals(ProtocolReceiptStatus.APPLIED, ready(guest, true).status)
                    assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
                    assertEquals(13, guest.snapshot().await().view.closedHand.size)
                }
            }
        }
    }

    @Test fun hostConfigurationPublishesToGuestResetsReadinessAndDrivesActualClock() {
        UdpTableServer().use { server ->
            server.start()
            UdpClient.connect("127.0.0.1", server.boundPort).await().use { host ->
                UdpClient.connect("127.0.0.1", server.boundPort).await().use { guest ->
                    val room = host.createRoom(CreateRoomRequest(protocolVersion = 4)).await()
                    guest.joinRoom(JoinRoomRequest(room.roomId)).await()
                    ready(host, true)
                    ready(guest, true)
                    val original = host.getRoomInfo().await()
                    val beforeConfig = host.snapshot().await()
                    val guestBeforeConfig = guest.snapshot().await()
                    val changed = original.config.copy(
                        matchConfig = original.config.matchConfig.copy(winds = listOf(1),
                            startingScores = listOf(26000, 24000, 25000, 25000)),
                        timeControl = ProtocolTimeControlConfig(roundReserveSeconds = 10L, turnThinkingSeconds = 2L))
                    val published = CompletableFuture<ProtocolRoomInfo>()
                    guest.onRoomUpdate { if (it.config == changed) published.complete(it) }.use {
                        assertEquals(ProtocolReceiptStatus.REJECTED, guest.updateConfig(changed).await().status)
                        assertEquals(original.config, host.getRoomInfo().await().config)
                        assertEquals(ProtocolReceiptStatus.APPLIED, host.updateConfig(changed).await().status)
                        assertEquals(changed, published.await().config)
                    }
                    val reset = guest.getRoomInfo().await()
                    assertFalse(reset.members.first { it.seat == 0 }.isReady)
                    assertFalse(reset.members.first { it.seat == 1 }.isReady)
                    assertEquals(ProtocolReceiptStatus.REJECTED, host.start().await().status)
                    ready(host, true)
                    ready(guest, true)
                    assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
                    val active = host.snapshot().await()
                    assertEquals(beforeConfig.sessionId, active.sessionId,
                        "Lobby reconfiguration must preserve the connected session identity")
                    assertEquals(beforeConfig.syncEpoch, active.syncEpoch)
                    assertEquals(changed.matchConfig.startingScores, active.view.scores,
                        "Lobby configuration must affect actual domain state")
                    val clock = active.clocks.first { it.seat == 0 }
                    assertEquals(12000L, clock.deadline - clock.opensAt)
                    val guestActive = guest.snapshot().await()
                    assertEquals(guestBeforeConfig.sessionId, guestActive.sessionId)
                    assertEquals(guestBeforeConfig.syncEpoch, guestActive.syncEpoch)
                    assertEquals(13, guestActive.view.closedHand.size)
                    assertEquals(ProtocolReceiptStatus.REJECTED, host.updateConfig(original.config).await().status)
                    assertEquals(changed, guest.getRoomInfo().await().config)
                    assertEquals(active.roundId, host.snapshot().await().roundId)
                }
            }
        }
    }
}
