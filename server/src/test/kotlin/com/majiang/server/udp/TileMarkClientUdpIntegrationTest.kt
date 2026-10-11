package com.majiang.server.udp

import com.majiang.client.api.*
import com.majiang.client.session.ServerConnectionSession
import com.majiang.client.transport.udp.UdpClient
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/** Full client facade -> UDP codec -> server queue -> engine -> private client view. */
@Timeout(30)
class TileMarkClientUdpIntegrationTest {
    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    @Test
    fun `real client facade casts over UDP receives private push and restores exact river position`() {
        val clock = FakeUdpClock()
        UdpTableServer(clock = clock, scheduler = FakeUdpScheduler(clock)).use { server ->
            server.start()
            val hostConnection = UdpClient.connect("127.0.0.1", server.boundPort, clock, FakeUdpScheduler(clock)).await()
            val guestConnection = UdpClient.connect("127.0.0.1", server.boundPort, clock, FakeUdpScheduler(clock)).await()
            ServerConnectionSession(hostConnection).use { host ->
                ServerConnectionSession(guestConnection).use { guest ->
                    val config = MatchConfig(cheatEconomy = CheatEconomyConfig(
                        true, List(4) { 40 }, mapOf("TILE_MARK" to 10)), tileMarkConfig = TileMarkConfig(true))
                    assertTrue(hostConnection.capabilities().await().tileMarkSupported)
                    host.createRoom("Host", config).await()
                    val roomId = requireNotNull(hostConnection.boundRoomId)
                    val token = requireNotNull(hostConnection.resumeToken)
                    guest.joinRoom(roomId, "Guest").await()
                    host.setReady(true).await()
                    guest.setReady(true).await()
                    assertEquals(ReceiptStatus.APPLIED, host.start().await().status)
                    val initial = host.snapshot().await()
                    val ids = (initial.view.myClosedTiles + listOfNotNull(initial.view.myDrawnTile)).take(4).map { it.tileId }
                    val update = CompletableFuture<GameSnapshotView>()
                    host.onUpdate { if (it.snapshot.tileMark?.activeTileCount == 4) update.complete(it.snapshot) }.use {
                        val request = TileMarkRequest("udp-client-mark", initial.sessionId, initial.roundId!!,
                            initial.decision.decisionId, initial.decision.controlEpoch, ids)
                        val receipt = host.submitTileMark(request).await()
                        assertEquals(ReceiptStatus.APPLIED, receipt.status)
                        val pushed = update.await()
                        assertEquals(receipt.cursor, pushed.cursor)
                        assertEquals(30, pushed.cheatPoints[0])
                        assertEquals(ids.toSet(), pushed.tileMark!!.activeGroups.single().tiles.map { it.tileId }.toSet())
                        assertEquals(receipt, host.submitTileMark(request).await())
                        assertEquals(30, host.snapshot().await().cheatPoints[0])
                    }
                    assertTrue(guest.snapshot().await().tileMark!!.activeGroups.isEmpty())
                    val current = host.snapshot().await()
                    val option = current.decision.options.first {
                        val action = it.action
                        action is LegalActionView.DiscardTile && action.tile.tileId == ids.first()
                    }
                    assertNotEquals(ReceiptStatus.REJECTED, host.submit(ActionChoice("discard", current.roundId,
                        current.decision.decisionId, current.decision.controlEpoch, option.optionId)).await().status)
                    val discarded = host.snapshot().await()
                    val tile = discarded.tileMark!!.activeGroups.single().tiles.single { it.tileId == ids.first() }
                    assertEquals(RiverPositionView(discarded.roundId!!, 0, 0), tile.riverPosition)
                    val replacement = UdpClient.connect("127.0.0.1", server.boundPort, clock, FakeUdpScheduler(clock)).await()
                    ServerConnectionSession(replacement).use { resumed ->
                        resumed.resumeSession(roomId, token).await()
                        val restored = resumed.snapshot().await()
                        assertEquals(discarded.tileMark!!.activeGroups, restored.tileMark!!.activeGroups)
                        assertEquals(30, restored.cheatPoints[0])
                        assertEquals(tile.riverPosition, restored.tileMark!!.activeGroups.single().tiles.single { it.tileId == ids.first() }.riverPosition)
                    }
                }
            }
        }
    }
}
