package com.majiang.server.beam

import com.majiang.access.ReceiptStatus
import com.majiang.access.PlayerUpdate
import com.majiang.application.ActionChoice
import com.majiang.cheat.beam.*
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.model.*
import com.majiang.server.api.*
import com.majiang.server.host.ServerTableHost
import com.majiang.server.inprocess.InProcessServer
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import com.majiang.transport.api.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** Codex-owned independent serial-host and real local-binding contracts. */
@Timeout(40)
class BeamSwapHostContractTest {
    private class Clock(@Volatile var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis() = now
    }
    private fun <T> CompletableFuture<T>.await(): T = get(10, TimeUnit.SECONDS)
    private fun config() = MatchConfig(
        cheatEconomy = CheatEconomyConfig(true, List(4) { 20 }, mapOf("BEAM_SWAP" to 10)),
        beamSwapConfig = BeamSwapConfig(true))
    private fun initial() = TestTableBuilder.create(drawn = 0 to Tile(Suit.SOU, 9, 3),
        wallPrefix = listOf(Tile(Suit.PIN, 9, 3)), phase = GamePhase.CHEAT_WINDOW).also {
        it.lastDrawKind = SleeveBloomDrawKind.NORMAL
        it.cheatPoints.fill(20)
        it.doraIndicators.add(it.deadWall[4])
    }
    private fun activation(host: ServerTableHost, id: String = "activate"): BeamSwapRequest {
        val snap = host.snapshot(0).await()
        return BeamSwapRequest(id, snap.sessionId, snap.roundId!!, snap.decision.decisionId, snap.decision.controlEpoch)
    }
    private fun assertPublicHasNoOffer(snapshot: ProtocolPublicSnapshot) {
        val json = ApiMessageCodec.encodeResponse(ApiResponse(publicSnapshot = snapshot))
        for (privateField in listOf("beamSwap", "pendingOffer", "candidates", "selectableHandTileIds", "targetSlot")) {
            assertFalse(json.contains("\"$privateField\""), "Public snapshot leaked $privateField")
        }
    }

    @Test fun `host privately offers eight blind slots and publishes an eventless swap only once`() {
        val state = initial()
        val expectedIncoming = state.wall[state.wallPointer]
        ServerTableHost(config(), clock = Clock(), controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = state).use { host ->
            val before = host.snapshot(0).await()
            val fee = CompletableFuture<PlayerUpdate>()
            val ownerUpdates = CopyOnWriteArrayList<PlayerUpdate>()
            // Existing public spectator projection intentionally excludes cheat ledger events.
            host.subscribeSeat(0) { update ->
                ownerUpdates.add(update)
                if (update.visibleEvents.any { it is GameEvent.CheatPointsChanged }) fee.complete(update)
            }.use {
                val request = activation(host)
                val applied = host.submitBeamSwap(0, request).await()
                assertEquals(ReceiptStatus.APPLIED, applied.status)
                val offered = host.snapshot(0).await()
                val offer = offered.beamSwap!!.pendingOffer!!
                assertEquals(8, offer.candidates.size)
                assertEquals(before.view.myClosedTiles.map { it.tileId }.toSet(), offer.selectableHandTileIds.toSet())
                assertFalse(before.view.myDrawnTile!!.tileId in offer.selectableHandTileIds)
                assertTrue(offer.candidates.all { !it.marked && it.tile == null })
                assertEquals(10, offered.cheatPoints[0])
                assertEquals(before.view.myDrawnTile, offered.view.myDrawnTile)
                for (seat in 1..3) {
                    val observer = EngineProtocolMapper.toProtocolSnapshot(host.snapshot(seat).await())
                    assertNull(observer.beamSwap?.pendingOffer)
                    assertFalse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = observer)).contains("\"candidates\""))
                }
                assertPublicHasNoOffer(host.publicSnapshot().await())
                val charged = fee.await().visibleEvents.filterIsInstance<GameEvent.CheatPointsChanged>()
                assertEquals(1, charged.size)
                assertEquals(-10, charged.single().amount)
                val wire = EngineProtocolMapper.toProtocolSnapshot(offered)
                assertEquals(wire.beamSwap,
                    ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = wire))).snapshot!!.beamSwap)

                val outgoingId = offer.selectableHandTileIds.first()
                val target = offer.candidates.first().slot
                val choice = BeamSwapChoiceRequest("choose", offered.sessionId, offered.roundId!!,
                    offered.decision.decisionId, offered.decision.controlEpoch, offer.offerId,
                    BeamSwapAction.SWAP, outgoingId, target)
                val clearedPush = CompletableFuture<com.majiang.access.PlayerUpdate>()
                host.subscribeSeat(0) { update ->
                    if (update.snapshot.cursor > offered.cursor && update.snapshot.beamSwap?.pendingOffer == null) {
                        clearedPush.complete(update)
                    }
                }.use {
                    val chosen = host.submitBeamSwapChoice(0, choice).await()
                    assertEquals(ReceiptStatus.APPLIED, chosen.status)
                    val pushed = clearedPush.await()
                    val after = host.snapshot(0).await()
                    assertTrue(pushed.visibleEvents.isEmpty(), "Selection changes state without another public event")
                    assertEquals(after.cursor, pushed.snapshot.cursor)
                    assertTrue(after.cursor > offered.cursor)
                    assertEquals(10, after.cheatPoints[0])
                    assertEquals(before.view.myDrawnTile, after.view.myDrawnTile)
                    assertEquals(before.view.melds, after.view.melds)
                    assertTrue(expectedIncoming in after.view.myClosedTiles)
                    assertFalse(after.view.myClosedTiles.any { it.tileId == outgoingId })
                    assertEquals(before.clocks.first { it.seat == 0 }.deadline, after.clocks.first { it.seat == 0 }.deadline)
                    assertEquals(applied, host.submitBeamSwap(0, request).await())
                    assertEquals(chosen, host.submitBeamSwapChoice(0, choice).await())
                    assertEquals("REQUEST_ID_CONFLICT", host.submitBeamSwapChoice(0, choice.copy(
                        action = BeamSwapAction.KEEP_ORIGINAL, handTileId = null, targetSlot = null)).await().reason)
                    assertEquals("REQUEST_ID_CONFLICT", host.submitBeamSwap(0, request.copy(requestId = choice.requestId)).await().reason)
                    assertEquals(after.cursor, host.snapshot(0).await().cursor)
                    assertEquals(1, ownerUpdates.flatMap { it.visibleEvents }.filterIsInstance<GameEvent.CheatPointsChanged>().size)
                }
            }
        }
    }

    @Test fun `wrong offer and drawn source preserve selection then keep clears without moving or refunding`() {
        ServerTableHost(config(), clock = Clock(), controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = initial()).use { host ->
            host.submitBeamSwap(0, activation(host)).await()
            val before = host.snapshot(0).await()
            val offer = before.beamSwap!!.pendingOffer!!
            val keep = BeamSwapChoiceRequest("keep", before.sessionId, before.roundId!!,
                before.decision.decisionId, before.decision.controlEpoch, offer.offerId, BeamSwapAction.KEEP_ORIGINAL)
            assertEquals("BEAM_SWAP_OFFER_MISMATCH",
                host.submitBeamSwapChoice(0, keep.copy(requestId = "wrong-offer", offerId = "foreign")).await().reason)
            val illegalDrawn = keep.copy(requestId = "drawn", action = BeamSwapAction.SWAP,
                handTileId = before.view.myDrawnTile!!.tileId, targetSlot = offer.candidates.first().slot)
            val rejected = host.submitBeamSwapChoice(0, illegalDrawn).await()
            assertEquals("BEAM_SWAP_INVALID_CHOICE", rejected.reason)
            assertEquals(rejected, host.submitBeamSwapChoice(0, illegalDrawn).await())
            assertEquals(before, host.snapshot(0).await())
            val kept = host.submitBeamSwapChoice(0, keep).await()
            assertEquals(ReceiptStatus.APPLIED, kept.status)
            val after = host.snapshot(0).await()
            assertNull(after.beamSwap!!.pendingOffer)
            assertEquals(before.view.myClosedTiles, after.view.myClosedTiles)
            assertEquals(before.view.myDrawnTile, after.view.myDrawnTile)
            assertEquals(10, after.cheatPoints[0])
            assertEquals("BEAM_SWAP_ALREADY_USED_THIS_DRAW", host.submitBeamSwap(0, activation(host, "again")).await().reason)
            assertEquals(kept, host.submitBeamSwapChoice(0, keep).await())
            assertEquals("REQUEST_ID_CONFLICT", host.submitBeamSwapChoice(0, keep.copy(action = BeamSwapAction.SWAP,
                handTileId = offer.selectableHandTileIds.first(), targetSlot = offer.candidates.first().slot)).await().reason)
        }
    }

    @Test fun `rejected choice replays its original full receipt after another choice advances the stream`() {
        ServerTableHost(config(), clock = Clock(), controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = initial()).use { host ->
            assertEquals(ReceiptStatus.APPLIED, host.submitBeamSwap(0, activation(host)).await().status)
            val offered = host.snapshot(0).await()
            val offer = offered.beamSwap!!.pendingOffer!!
            val invalid = BeamSwapChoiceRequest("invalid-drawn", offered.sessionId, offered.roundId!!,
                offered.decision.decisionId, offered.decision.controlEpoch, offer.offerId,
                BeamSwapAction.SWAP, offered.view.myDrawnTile!!.tileId, offer.candidates.first().slot)
            val rejected = host.submitBeamSwapChoice(0, invalid).await()
            assertEquals(ReceiptStatus.REJECTED, rejected.status)
            assertEquals("BEAM_SWAP_INVALID_CHOICE", rejected.reason)
            assertEquals(offered.cursor, rejected.cursor)
            assertEquals(offered, host.snapshot(0).await())

            val keep = BeamSwapChoiceRequest("keep-after-reject", offered.sessionId, offered.roundId!!,
                offered.decision.decisionId, offered.decision.controlEpoch, offer.offerId, BeamSwapAction.KEEP_ORIGINAL)
            assertEquals(ReceiptStatus.APPLIED, host.submitBeamSwapChoice(0, keep).await().status)
            val settled = host.snapshot(0).await()
            assertTrue(settled.cursor > offered.cursor)
            assertNull(settled.beamSwap!!.pendingOffer)

            val replay = host.submitBeamSwapChoice(0, invalid).await()
            assertEquals(rejected, replay, "Rejected replay must retain the original cursor as well as status and reason")
            assertEquals(settled, host.snapshot(0).await(), "Replaying a rejected request must not change the state or stream")
            assertEquals(10, host.snapshot(0).await().cheatPoints[0])
        }
    }

    @Test fun `default config refuses activation without charging and enabled config survives mapping`() {
        assertFalse(MatchConfig().beamSwapConfig.enabled)
        val enabled = config()
        val wire = EngineProtocolMapper.toProtocolConfig(enabled)
        assertTrue(wire.beamSwapConfig.enabled)
        assertEquals(10, wire.cheatEconomy.costs["BEAM_SWAP"])
        assertEquals(enabled.beamSwapConfig, EngineProtocolMapper.toEngineConfig(wire).beamSwapConfig)
        ServerTableHost(MatchConfig(), clock = Clock(), controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = initial()).use { host ->
            val before = host.snapshot(0).await()
            assertEquals("BEAM_SWAP_DISABLED", host.submitBeamSwap(0, activation(host)).await().reason)
            val after = host.snapshot(0).await()
            assertEquals(before.cheatPoints, after.cheatPoints)
            assertEquals(before.cursor, after.cursor)
            assertNull(after.beamSwap?.pendingOffer)
        }
    }

    @Test fun `queued binding validity is checked before deadline and before replay lookup`() {
        val clock = Clock()
        val executor = Executors.newSingleThreadExecutor()
        val release = CountDownLatch(1)
        val host = ServerTableHost(config(), clock = clock, controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = initial(), serialExecutor = executor)
        try {
            val before = host.snapshot(0).await()
            val request = activation(host, "binding")
            val entered = CountDownLatch(1)
            executor.execute { entered.countDown(); check(release.await(10, TimeUnit.SECONDS)) }
            assertTrue(entered.await(3, TimeUnit.SECONDS))
            val valid = AtomicBoolean(true)
            val stale = host.submitBeamSwap(0, request) { valid.get() }
            valid.set(false)
            clock.now = before.clocks.first { it.seat == 0 }.deadline
            val late = host.submitBeamSwap(0, request.copy(requestId = "late"))
            release.countDown()
            assertEquals("STALE_SESSION", stale.await().reason)
            assertEquals("DEADLINE_EXPIRED", late.await().reason)
            assertNull(host.snapshot(0).await().beamSwap?.pendingOffer)
            assertEquals(20, host.snapshot(0).await().cheatPoints[0])
        } finally {
            release.countDown()
            host.close()
            executor.shutdownNow()
        }
        ServerTableHost(config(), clock = Clock(), controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = initial()).use { replayHost ->
            val request = activation(replayHost)
            val applied = replayHost.submitBeamSwap(0, request).await()
            assertEquals(ReceiptStatus.APPLIED, applied.status)
            assertEquals("STALE_SESSION", replayHost.submitBeamSwap(0, request) { false }.await().reason)
            val offered = replayHost.snapshot(0).await()
            val keep = BeamSwapChoiceRequest("keep", offered.sessionId, offered.roundId!!,
                offered.decision.decisionId, offered.decision.controlEpoch, offered.beamSwap!!.pendingOffer!!.offerId,
                BeamSwapAction.KEEP_ORIGINAL)
            assertEquals("STALE_SESSION", replayHost.submitBeamSwapChoice(0, keep) { false }.await().reason)
            assertEquals(offered.cursor, replayHost.snapshot(0).await().cursor)
            val kept = replayHost.submitBeamSwapChoice(0, keep).await()
            assertEquals(ReceiptStatus.APPLIED, kept.status)
            assertEquals("STALE_SESSION", replayHost.submitBeamSwapChoice(0, keep) { false }.await().reason)
            assertEquals(kept, replayHost.submitBeamSwapChoice(0, keep).await())
        }
    }

    @Test fun `added kan pending dora can be swapped but is revealed only on discard using its new tile`() {
        val quad = (0..3).map { Tile(Suit.SOU, 8, it) }
        val state = TestTableBuilder.create(melds = mapOf(0 to listOf(Meld(MeldType.PON, quad.take(3), 3, quad[0]))),
            drawn = 0 to quad[3], phase = GamePhase.CHEAT_WINDOW).also {
            it.doraIndicators.add(it.deadWall[4])
            it.lastDrawKind = SleeveBloomDrawKind.NORMAL
            it.cheatPoints.fill(20)
        }
        val originalIndicator = state.deadWall[6]
        ServerTableHost(config(), clock = Clock(), controllers = List(4) { SeatController.RemoteHuman(false) },
            initialState = state).use { host ->
            fun action(id: String, predicate: (GameAction) -> Boolean): ActionChoice {
                val snap = host.snapshot(0).await()
                return ActionChoice(id, snap.roundId, snap.decision.decisionId, snap.decision.controlEpoch,
                    snap.decision.options.first { predicate(it.action) }.optionId)
            }
            val updates = CopyOnWriteArrayList<ProtocolPublicUpdate>()
            val revealed = CompletableFuture<ProtocolPublicUpdate>()
            host.subscribePublic { update ->
                updates.add(update)
                if (update.visibleEvents.any { it is ProtocolGameEvent.DoraRevealed }) revealed.complete(update)
            }.use {
                assertEquals(ReceiptStatus.APPLIED, host.submitChoice(0,
                    action("kan") { it is GameAction.DeclareKan && it.kanType == MeldType.ADDED_KAN }).await().status)
                for (seat in 1..3) {
                    val current = host.snapshot(seat).await()
                    current.decision.options.firstOrNull { it.action is GameAction.Pass }?.let { option ->
                        host.submitChoice(seat, ActionChoice("pass-$seat", current.roundId, current.decision.decisionId,
                            current.decision.controlEpoch, option.optionId)).await()
                    }
                }
                val kanDraw = host.snapshot(0).await()
                assertEquals(1, kanDraw.view.doraIndicators.size)
                assertEquals(ReceiptStatus.APPLIED, host.submitBeamSwap(0, activation(host)).await().status)
                val offered = host.snapshot(0).await()
                val offer = offered.beamSwap!!.pendingOffer!!
                assertEquals(BeamSwapWindow.DEAD_WALL, offer.window)
                assertTrue(offer.candidates.any { it.slot == BeamSwapSlot.DeadWallSlot(6) })
                assertTrue(offer.candidates.none { it.slot == BeamSwapSlot.DeadWallSlot(4) })
                val outgoing = offered.view.myClosedTiles.first { it.tileId in offer.selectableHandTileIds }
                val choice = BeamSwapChoiceRequest("choose-dora", offered.sessionId, offered.roundId!!,
                    offered.decision.decisionId, offered.decision.controlEpoch, offer.offerId,
                    BeamSwapAction.SWAP, outgoing.tileId, BeamSwapSlot.DeadWallSlot(6))
                val chosen = host.submitBeamSwapChoice(0, choice).await()
                assertEquals(ReceiptStatus.APPLIED, chosen.status)
                val swapped = host.snapshot(0).await()
                assertEquals(kanDraw.view.myDrawnTile, swapped.view.myDrawnTile)
                assertEquals(kanDraw.view.melds, swapped.view.melds)
                assertTrue(originalIndicator in swapped.view.myClosedTiles)
                for (seat in 0..3) assertEquals(1, host.snapshot(seat).await().view.doraIndicators.size)
                assertEquals(1, host.publicSnapshot().await().board!!.doraIndicators.size)
                assertFalse(revealed.isDone)
                val discard = action("discard") { it is GameAction.DiscardTile }
                val discarded = host.submitChoice(0, discard).await()
                assertEquals(ReceiptStatus.APPLIED, discarded.status)
                val event = revealed.await().visibleEvents.filterIsInstance<ProtocolGameEvent.DoraRevealed>().single()
                assertEquals(outgoing.tileId, event.indicator.tileId)
                for (seat in 0..3) {
                    val wire = EngineProtocolMapper.toProtocolSnapshot(host.snapshot(seat).await())
                    val restored = ApiMessageCodec.decodeResponse(ApiMessageCodec.encodeResponse(ApiResponse(snapshot = wire))).snapshot!!
                    assertEquals(2, restored.view.doraIndicators.size)
                    assertEquals(outgoing.tileId, restored.view.doraIndicators.last().tileId)
                }
                val cursor = host.publicSnapshot().await().cursor
                assertEquals(chosen, host.submitBeamSwapChoice(0, choice).await())
                assertEquals(discarded, host.submitChoice(0, discard).await())
                assertEquals(cursor, host.publicSnapshot().await().cursor)
                assertEquals(1, updates.flatMap { it.visibleEvents }.filterIsInstance<ProtocolGameEvent.DoraRevealed>().size)
            }
        }
    }

    @Test fun `real local binding routes both operations with private pushes and rejects spectator forgery`() {
        InProcessServer(isSingleplayer = false).use { server ->
            val connections = (0..3).map { server.createConnection() }
            val spectator = server.createConnection()
            try {
                val match = ProtocolMatchConfig(
                    cheatEconomy = ProtocolCheatEconomyConfig(true, List(4) { 20 }, mapOf("BEAM_SWAP" to 10)),
                    beamSwapConfig = ProtocolBeamSwapConfig(true))
                val bound = connections[0].createRoom(CreateRoomRequest(config = match)).await()
                assertTrue(connections[0].capabilities().await().beamSwapSupported)
                for (seat in 1..3) connections[seat].joinRoom(JoinRoomRequest(bound.roomId, "Guest-$seat")).await()
                spectator.joinSpectator(JoinSpectatorRequest(bound.roomId, spectatorName = "Observer")).await()
                for (connection in connections) connection.setReady(true).await()
                assertEquals(ProtocolReceiptStatus.APPLIED, connections[0].start().await().status)
                val dealt = connections[0].snapshot().await()
                val discard = dealt.decision.options.first { it.action is ProtocolLegalAction.DiscardTile }
                connections[0].submit(ProtocolActionChoice("discard", dealt.roundId, dealt.decision.decisionId,
                    dealt.decision.controlEpoch, discard.optionId)).await()
                for (seat in 1..3) {
                    val current = connections[seat].snapshot().await()
                    current.decision.options.firstOrNull { it.action is ProtocolLegalAction.Pass }?.let { pass ->
                        connections[seat].submit(ProtocolActionChoice("pass-$seat", current.roundId, current.decision.decisionId,
                            current.decision.controlEpoch, pass.optionId)).await()
                    }
                }
                val before = connections[1].snapshot().await()
                val request = ProtocolBeamSwapRequest("local-activate", before.sessionId, before.roundId!!,
                    before.decision.decisionId, before.decision.controlEpoch)
                assertEquals(ProtocolReceiptStatus.REJECTED, spectator.submitBeamSwap(request).await().status)
                val activated = connections[1].submitBeamSwap(request).await()
                assertEquals(ProtocolReceiptStatus.APPLIED, activated.status)
                val offered = connections[1].snapshot().await()
                val offer = offered.beamSwap!!.pendingOffer!!
                assertTrue(offer.candidates.all { !it.marked && it.tile == null })
                for (seat in listOf(0, 2, 3)) assertNull(connections[seat].snapshot().await().beamSwap?.pendingOffer)
                assertPublicHasNoOffer(spectator.publicSnapshot().await())
                val keep = ProtocolBeamSwapChoiceRequest("local-keep", offered.sessionId, offered.roundId!!,
                    offered.decision.decisionId, offered.decision.controlEpoch, offer.offerId, ProtocolBeamSwapAction.KEEP_ORIGINAL)
                assertEquals(ProtocolReceiptStatus.REJECTED, spectator.submitBeamSwapChoice(keep).await().status)
                val pushed = CompletableFuture<ProtocolPlayerUpdate>()
                connections[1].onUpdate { update ->
                    if (update.snapshot.cursor > offered.cursor && update.snapshot.beamSwap?.pendingOffer == null) pushed.complete(update)
                }.use {
                    val chosen = connections[1].submitBeamSwapChoice(keep).await()
                    assertEquals(ProtocolReceiptStatus.APPLIED, chosen.status)
                    val cleared = pushed.await()
                    assertTrue(cleared.visibleEvents.isEmpty())
                    assertEquals(before.view.myClosedTiles, cleared.snapshot.view.myClosedTiles)
                    assertEquals(before.view.myDrawnTile, cleared.snapshot.view.myDrawnTile)
                    assertEquals(10, cleared.snapshot.cheatPoints[1])
                    assertEquals(before.clocks.first { it.seat == 1 }.deadline, cleared.snapshot.clocks.first { it.seat == 1 }.deadline)
                    assertEquals(activated, connections[1].submitBeamSwap(request).await())
                    assertEquals(chosen, connections[1].submitBeamSwapChoice(keep).await())
                    assertEquals(cleared.snapshot.cursor, connections[1].snapshot().await().cursor)
                }
            } finally {
                spectator.close()
                connections.forEach { it.close() }
            }
        }
    }
}
