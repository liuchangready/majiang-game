package com.majiang.v4lobby

import com.google.gson.JsonElement
import com.google.gson.JsonParser
import com.majiang.client.session.ServerConnectionSession
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.server.api.*
import com.majiang.server.inprocess.InProcessBinding
import com.majiang.server.inprocess.InProcessServer
import com.majiang.server.room.SharedRoom
import com.majiang.state.GameState
import com.majiang.testing.MeldChoiceFixtures
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import com.majiang.transport.api.*
import java.lang.reflect.GenericArrayType
import java.lang.reflect.Modifier
import java.lang.reflect.ParameterizedType
import java.lang.reflect.Type
import java.lang.reflect.TypeVariable
import java.lang.reflect.WildcardType
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.*

/** Real in-process role/authority probes; no fake player projection or reflected authority mutation. */
@Timeout(25)
class V4SpectatorBindingAcceptanceTest {
    private fun <T> CompletableFuture<T>.await(): T = get(4, TimeUnit.SECONDS)
    private class Clock : MonotonicClock { override fun nowMillis(): Long = 1000L }

    // initialState is the explicitly authorized SERVER assembly seam. It is not a wire input.
    private inner class Table(
        config: ProtocolRoomConfig = ProtocolRoomConfig(),
        initialState: GameState? = null
    ) : AutoCloseable {
        val server = InProcessServer(isSingleplayer = initialState != null)
        val room = SharedRoom("spectator-review", "Host", initialConfig = config,
            isSingleplayer = initialState != null, clock = Clock(), initialState = initialState)
        val connections = mutableListOf<ServerConnection>()
        val players = mutableListOf<ServerConnection>()
        val host: ServerConnection
        init {
            server.registry.registerRoom(room)
            host = InProcessBinding(server, room, 0)
            connections += host
            players += host
            if (initialState == null) repeat(3) { index ->
                val player = connection()
                val binding = player.joinRoom(JoinRoomRequest(room.roomId, "Player-${index + 1}")).await()
                assertEquals(index + 1, binding.seat)
                players += player
            }
        }
        fun connection(): ServerConnection = server.createConnection().also { connections += it }
        fun spectator(): Pair<ServerConnection, SpectatorBinding> {
            val connection = connection()
            return connection to connection.joinSpectator(JoinSpectatorRequest(room.roomId, "Observer")).await()
        }
        fun start() {
            players.forEach { assertEquals(ProtocolReceiptStatus.APPLIED, it.setReady(true).await().status) }
            assertEquals(ProtocolReceiptStatus.APPLIED, host.start().await().status)
        }
        fun privateState(): List<ProtocolPlayerSnapshot> = players.map { it.snapshot().await() }
        override fun close() {
            connections.asReversed().forEach { runCatching { it.close() } }
            server.close()
        }
    }

    private fun assertDenied(query: () -> CompletableFuture<*>) {
        val error = assertFailsWith<ExecutionException> { query().await() }
        val root = generateSequence(error as Throwable) { it.cause }.last()
        assertTrue(!root.message.isNullOrBlank(), "Authorization rejection must give a reason")
        assertFalse(root is UnsupportedOperationException, "A placeholder is not role authorization")
    }

    private val forbiddenKeys = setOf("myClosedTiles", "myDrawnTile", "drawnTile", "mySleeveTiles",
        "sleeveTiles", "wall", "deadWall", "uraDoraIndicators", "cheatPoints", "finalCheatPoints",
        "cheatDeltas", "cheatEntries", "preparation", "reservedPoints", "availablePoints", "selection",
        "decision", "options", "presentationPreview", "controls", "controlEpoch", "resumeToken")
    private fun assertNoPrivateFields(json: JsonElement, path: String = "public") {
        when {
            json.isJsonObject -> json.asJsonObject.entrySet().forEach { (key, value) ->
                assertFalse(key in forbiddenKeys, "Private field $path.$key escaped the real binding")
                assertNoPrivateFields(value, "$path.$key")
            }
            json.isJsonArray -> json.asJsonArray.forEachIndexed { index, value ->
                assertNoPrivateFields(value, "$path[$index]")
            }
        }
    }
    private fun assertPublic(snapshot: ProtocolPublicSnapshot) {
        assertNoPrivateFields(JsonParser.parseString(ApiMessageCodec.encodePublicSnapshot(snapshot)))
        assertPublicEvents(snapshot.settlementEvents)
    }
    private fun assertPublicEvents(events: List<ProtocolGameEvent>) {
        val privateTypes = setOf("TileDealt", "TileDrawn", "RinshanDraw", "CheatPointsChanged")
        events.forEach { assertFalse(it.javaClass.simpleName in privateTypes,
            "Private event ${it.javaClass.simpleName} escaped spectator transport") }
    }

    @Test fun spectatorFacadeIsIndependentAndRecursivelyExposesOnlyTypedClientModels() {
        val facade = Class.forName("com.majiang.client.api.SpectatorSession", false, javaClass.classLoader)
        assertTrue(facade.isInterface, "SpectatorSession must be an independently usable read-only facade")
        assertTrue(AutoCloseable::class.java.isAssignableFrom(facade))
        for (playerFacade in listOf("GameSession", "LobbySession")) {
            assertFalse(Class.forName("com.majiang.client.api.$playerFacade", false, javaClass.classLoader)
                .isAssignableFrom(facade), "SpectatorSession must not inherit $playerFacade")
        }
        val methods = facade.methods.filter { Modifier.isPublic(it.modifiers) && !it.isSynthetic }
        val names = methods.map { it.name }.toSet()
        val required = setOf("getCurrentPublicSnapshot", "publicSnapshot", "resumePublic",
            "queryPublicRoundResult", "onPublicUpdate", "getClosed", "getDisconnectReason",
            "getConnectionState", "onStateChange", "onDisconnect", "getRoomInfo", "onRoomUpdate",
            "leaveRoom", "close")
        assertTrue(names.containsAll(required), "Missing spectator facade methods: ${required - names}")
        val writes = setOf("submit", "submitShufflePlan", "claimControl", "releaseControl", "setReady",
            "updateConfig", "start", "nextRound", "createRoom", "joinRoom", "resumeSession",
            "snapshot", "decision", "resume", "queryRoundResult")
        assertTrue(names.intersect(writes).isEmpty(), "Player API escaped into spectator facade: ${names.intersect(writes)}")
        for (name in listOf("publicSnapshot", "resumePublic", "queryPublicRoundResult", "getRoomInfo", "leaveRoom")) {
            methods.filter { it.name == name }.forEach { method ->
                assertEquals(CompletableFuture::class.java, method.returnType, "$name must remain asynchronous")
            }
        }
        for (name in listOf("onPublicUpdate", "onStateChange", "onDisconnect", "onRoomUpdate")) {
            methods.filter { it.name == name }.forEach { method ->
                assertEquals(AutoCloseable::class.java, method.returnType, "$name must return a subscription handle")
            }
        }

        val seen = mutableSetOf<Type>()
        val leaks = mutableListOf<String>()
        fun inspect(type: Type) {
            if (!seen.add(type)) return
            when (type) {
                is ParameterizedType -> {
                    val raw = type.rawType as? Class<*>
                    if (raw == null) leaks += "Unknown generic container $type"
                    else if (raw.name.startsWith("com.majiang.client.api.")) inspect(raw)
                    else if (!raw.name.startsWith("java.") && !raw.name.startsWith("kotlin.")) {
                        leaks += "Foreign generic container ${raw.name}"
                    }
                    // Typed maps such as score payments remain legal; Map<String, Any> does not.
                    type.actualTypeArguments.forEach(::inspect)
                    type.ownerType?.let(::inspect)
                }
                is GenericArrayType -> inspect(type.genericComponentType)
                is WildcardType -> (if (type.lowerBounds.isNotEmpty()) type.lowerBounds else type.upperBounds)
                    .forEach(::inspect)
                is TypeVariable<*> -> type.bounds.forEach(::inspect)
                is Class<*> -> {
                    if (type.isArray) {
                        inspect(type.componentType)
                    } else if (type == Any::class.java) {
                        leaks += "Untyped Object/Any payload"
                    } else if (type.name.startsWith("com.majiang.client.api.")) {
                        if (type.simpleName in setOf("GameSnapshotView", "GameUpdateView", "PlayerView", "DecisionView")) {
                            leaks += "Private player model ${type.name}"
                        }
                        type.genericInterfaces.forEach(::inspect)
                        type.genericSuperclass?.takeUnless { it == Any::class.java }?.let(::inspect)
                        type.declaredMethods.filter {
                            Modifier.isPublic(it.modifiers) && !it.isSynthetic &&
                                !(it.name == "equals" && it.parameterTypes.contentEquals(arrayOf(Any::class.java)))
                        }.forEach { method ->
                            inspect(method.genericReturnType)
                            method.genericParameterTypes.forEach(::inspect)
                        }
                        type.constructors.filterNot { it.isSynthetic }.forEach { constructor ->
                            constructor.genericParameterTypes.forEach(::inspect)
                        }
                        type.declaredFields.filter {
                            Modifier.isPublic(it.modifiers) && !it.isSynthetic && it.name != "Companion"
                        }.forEach { inspect(it.genericType) }
                        type.declaredClasses.filterNot { it.isSynthetic || it.simpleName == "Companion" }
                            .forEach(::inspect)
                    } else if (type.isPrimitive) {
                        // JVM primitive values are intrinsically typed.
                    } else if (type.name.startsWith("java.") || type.name.startsWith("kotlin.")) {
                        if (type.typeParameters.isNotEmpty()) leaks += "Raw generic payload ${type.name}"
                    } else {
                        leaks += "Non-client API type ${type.name}"
                    }
                }
                else -> leaks += "Unhandled public signature type $type"
            }
        }
        inspect(facade)
        assertTrue(leaks.isEmpty(), "Spectator facade exposes non-client or untyped data: ${leaks.distinct()}")
    }

    @Test fun fullFourPlayerRoomAdmitsSpectatorWithoutChangingAnyPlayerBinding() {
        Table().use { table ->
            val before = table.privateState()
            val (observer, binding) = table.spectator()
            assertTrue(binding.spectatorId.isNotBlank())
            assertTrue(binding.resumeToken.isNotBlank())
            assertFalse(binding.toString().contains(binding.resumeToken))
            assertEquals(table.room.roomId, binding.roomId)
            assertEquals(before.first().sessionId, binding.snapshot.sessionId)
            assertEquals(before, table.privateState(), "Admission must not change player state/control/clocks")
            assertEquals(setOf(0, 1, 2, 3), observer.getRoomInfo().await().members.map { it.seat }.toSet())
            assertEquals(binding.snapshot.sessionId, observer.publicSnapshot().await().sessionId)
            assertPublic(observer.publicSnapshot().await())
        }
    }

    @Test fun spectatorCannotMutateLobbyReadinessConfigurationOrStart() {
        Table().use { table ->
            val (observer, _) = table.spectator()
            val before = table.privateState()
            val roomBefore = table.host.getRoomInfo().await()
            for (call in listOf<() -> CompletableFuture<ProtocolActionReceipt>>(
                { observer.setReady(true) }, { observer.start() }, { observer.nextRound() },
                { observer.updateConfig(roomBefore.config.copy(timeControl = ProtocolTimeControlConfig(40, 6))) },
                { observer.claimControl() }, { observer.releaseControl() }
            )) {
                assertEquals(ProtocolReceiptStatus.REJECTED, call().await().status)
                assertEquals(before, table.privateState())
                assertEquals(roomBefore, table.host.getRoomInfo().await())
            }
        }
    }

    @Test fun genuinePlayerChoiceAndShuffleIdentityCannotBeSubmittedBySpectator() {
        Table().use { table ->
            table.start()
            val (observer, _) = table.spectator()
            val decision = table.host.decision().await()
            val option = decision.options.first { it.action is ProtocolLegalAction.DiscardTile }
            val choice = ProtocolActionChoice("spectator-stolen-choice", decision.roundId,
                decision.decisionId, decision.controlEpoch, option.optionId)
            val before = table.privateState()
            assertEquals(ProtocolReceiptStatus.REJECTED, observer.submit(choice).await().status)
            val plan = ProtocolShufflePlan("spectator-shuffle", assertNotNull(decision.roundId),
                decision.controlEpoch, ProtocolShuffleSelection())
            assertEquals(ProtocolReceiptStatus.REJECTED, observer.submitShufflePlan(plan).await().status)
            assertEquals(ProtocolReceiptStatus.REJECTED, observer.claimControl().await().status)
            assertEquals(ProtocolReceiptStatus.REJECTED, observer.releaseControl().await().status)
            assertEquals(before, table.privateState())
            assertTrue(table.host.submit(choice.copy(requestId = "real-owner")).await().status in
                setOf(ProtocolReceiptStatus.APPLIED, ProtocolReceiptStatus.RECORDED))
        }
    }

    @Test fun spectatorCannotReadPlayerDecisionSnapshotResumeOrPrivateResultEndpoint() {
        Table().use { table ->
            table.start()
            val (observer, binding) = table.spectator()
            val before = table.privateState()
            assertDenied { observer.snapshot() }
            assertDenied { observer.decision() }
            assertDenied { observer.resume(0L) }
            assertDenied { observer.queryRoundResult(assertNotNull(binding.snapshot.roundId)) }
            assertEquals(before, table.privateState())
            assertPublic(observer.publicSnapshot().await())
            assertEquals(4, observer.capabilities().await().supportedPlayerCounts.single())
        }
    }

    @Test fun playerAndSpectatorRecoveryTokensAreNotInterchangeable() {
        Table().use { table ->
            val (_, binding) = table.spectator()
            val playerToken = table.room.resumeTokens[0]
            val before = table.privateState()
            assertDenied { table.connection().resumeSession(ResumeRequest(table.room.roomId, binding.resumeToken)) }
            assertDenied { table.connection().resumeSpectator(ResumeSpectatorRequest(
                table.room.roomId, binding.spectatorId, playerToken)) }
            assertDenied { table.connection().resumeSpectator(ResumeSpectatorRequest(
                table.room.roomId, "foreign-spectator", binding.resumeToken)) }
            assertDenied { table.connection().resumeSpectator(ResumeSpectatorRequest(
                "foreign-room", binding.spectatorId, binding.resumeToken)) }
            assertEquals(before, table.privateState())
        }
    }

    @Test fun boundObserverCannotSilentlyBecomeAPlayerOrReplaceItsMembership() {
        Table().use { table ->
            val (observer, binding) = table.spectator()
            val before = table.privateState()
            assertDenied { observer.createRoom(CreateRoomRequest()) }
            assertDenied { observer.joinRoom(JoinRoomRequest(table.room.roomId)) }
            assertDenied { observer.resumeSession(ResumeRequest(table.room.roomId, table.room.resumeTokens[0])) }
            assertDenied { observer.joinSpectator(JoinSpectatorRequest(table.room.roomId)) }
            assertDenied { table.host.resumeSpectator(ResumeSpectatorRequest(
                binding.roomId, binding.spectatorId, binding.resumeToken)) }
            assertEquals(binding.snapshot.sessionId, observer.publicSnapshot().await().sessionId)
            assertEquals(before, table.privateState())
        }
    }

    @Test fun wrongPublicCheckpointCannotRestoreOrReplaceAnExistingObserver() {
        Table().use { table ->
            table.start()
            val (observer, binding) = table.spectator()
            val baseline = observer.publicSnapshot().await()
            val before = table.privateState()
            val checkpoint = PublicCheckpoint(baseline.sessionId, baseline.syncEpoch, baseline.cursor)
            for (invalid in listOf(checkpoint.copy(sessionId = "another-session"),
                checkpoint.copy(syncEpoch = "another-public-stream"))) {
                assertDenied { observer.resumePublic(invalid) }
                assertDenied { table.connection().resumeSpectator(ResumeSpectatorRequest(
                    binding.roomId, binding.spectatorId, binding.resumeToken, invalid)) }
                assertFalse(observer.closed, "Rejected recovery must not replace the valid observer")
                assertEquals(baseline.cursor, observer.publicSnapshot().await().cursor,
                    "Original generation must still be authorized after an invalid recovery")
            }
            val valid = observer.resumePublic(checkpoint).await()
            assertEquals(baseline.sessionId, valid.sessionId)
            assertEquals(baseline.syncEpoch, valid.syncEpoch)
            assertEquals(baseline.cursor, valid.cursor)
            assertEquals(before, table.privateState())
        }
    }

    @Test fun spectatorRuntimeReceivesRoomAndPublicUpdatesThroughItsRealBinding() {
        Table().use { table ->
            ServerConnectionSession(table.connection()).use { runtime ->
                val baseline = runtime.joinSpectator(table.room.roomId, "Runtime viewer").await()
                assertNull(runtime.currentSnapshot, "Spectator runtime cannot hold a player snapshot")
                val ready = CompletableFuture<Boolean>()
                val paused = CompletableFuture<Boolean>()
                runtime.onRoomUpdate { info ->
                    if (info.roomId == table.room.roomId && info.members.any { it.seat == 0 && it.isReady }) {
                        ready.complete(true)
                    }
                }.use {
                    runtime.onPublicUpdate { update ->
                        if (update.snapshot.sessionId == baseline.sessionId && update.snapshot.paused) {
                            paused.complete(true)
                        }
                    }.use {
                        assertEquals(ProtocolReceiptStatus.APPLIED, table.host.setReady(true).await().status)
                        assertTrue(ready.await(), "Runtime must not discard ROOM_UPDATE for its spectator binding")
                        assertTrue(table.room.tableHost.pause().await())
                        assertTrue(paused.await(), "Runtime must deliver actual PUBLIC state changes")
                        assertTrue(assertNotNull(runtime.currentPublicSnapshot).paused)
                        assertNull(runtime.currentSnapshot)
                    }
                }
            }
        }
    }

    @Test fun replacementInvalidatesOldObserverAndOldCloseCannotRevokeNewBinding() {
        Table().use { table ->
            val (old, binding) = table.spectator()
            val before = table.privateState()
            val disconnected = CountDownLatch(1)
            val reasons = CopyOnWriteArrayList<String>()
            old.onDisconnect { reasons += it; disconnected.countDown() }.use {
                val replacement = table.connection()
                val resumed = replacement.resumeSpectator(ResumeSpectatorRequest(
                    binding.roomId, binding.spectatorId, binding.resumeToken)).await()
                assertEquals(binding.spectatorId, resumed.spectatorId)
                assertTrue(disconnected.await(3, TimeUnit.SECONDS), "Old spectator must receive terminal replacement")
                assertEquals(listOf(ApiProtocol.DISPLACED_BY_NEW_CONNECTION), reasons)
                assertDenied { old.publicSnapshot() }
                old.close()
                assertFalse(replacement.closed)
                assertEquals(binding.snapshot.sessionId, replacement.publicSnapshot().await().sessionId)
                assertEquals(before, table.privateState())
            }
        }
    }

    @Test fun explicitSpectatorLeaveRevokesTokenKeepsLobbyAndNeverChangesAnySeat() {
        for (started in listOf(false, true)) Table().use { table ->
            if (started) table.start()
            val (observer, binding) = table.spectator()
            val before = table.privateState()
            assertEquals(ProtocolReceiptStatus.APPLIED, observer.leaveRoom().await().status)
            assertFalse(observer.closed)
            assertEquals(before, table.privateState())
            assertDenied { table.connection().resumeSpectator(ResumeSpectatorRequest(
                binding.roomId, binding.spectatorId, binding.resumeToken)) }
            val fresh = observer.joinSpectator(JoinSpectatorRequest(binding.roomId)).await()
            assertNotEquals(binding.resumeToken, fresh.resumeToken)
            assertPublic(fresh.snapshot)
        }
    }

    @Test fun transportClosePreservesSpectatorResumeWithoutPlayerTakeover() {
        Table().use { table ->
            table.start()
            val (observer, binding) = table.spectator()
            val before = table.privateState()
            observer.close()
            val replacement = table.connection()
            val resumed = replacement.resumeSpectator(ResumeSpectatorRequest(
                binding.roomId, binding.spectatorId, binding.resumeToken,
                PublicCheckpoint(binding.snapshot.sessionId, binding.snapshot.syncEpoch, binding.snapshot.cursor))).await()
            assertEquals(binding.spectatorId, resumed.spectatorId)
            assertEquals(before, table.privateState())
            assertPublic(resumed.snapshot)
        }
    }

    @Test fun hostLeaveClosesSpectatorsButReturnsHostToReusableLobby() {
        Table().use { table ->
            val (observer, _) = table.spectator()
            val closed = CountDownLatch(1)
            val reasons = CopyOnWriteArrayList<String>()
            observer.onDisconnect { reasons += it; closed.countDown() }.use {
                assertEquals(ProtocolReceiptStatus.APPLIED, table.host.leaveRoom().await().status)
                assertFalse(table.host.closed)
                assertTrue(closed.await(3, TimeUnit.SECONDS))
                assertTrue(observer.closed)
                assertEquals(listOf(ApiProtocol.ROOM_CLOSED), reasons)
                assertDenied { observer.publicSnapshot() }
                assertTrue(table.host.createRoom(CreateRoomRequest()).await().roomId.isNotBlank())
            }
        }
    }

    @Test fun preparationAndLivePublicStreamContainPublicBoardButNoPlayerMessages() {
        val config = ProtocolRoomConfig(matchConfig = ProtocolMatchConfig(
            cheatEconomy = ProtocolCheatEconomyConfig(true, listOf(80, 90, 100, 110)),
            shuffleConfig = ProtocolShuffleConfig(enabled = true)))
        Table(config).use { table ->
            table.start()
            val (observer, _) = table.spectator()
            assertNotNull(table.host.snapshot().await().preparation, "Fixture must exercise actual private preparation")
            assertPublic(observer.publicSnapshot().await())
            assertEquals(config, observer.getRoomInfo().await().config, "Public rule config remains available")
        }
        Table().use { table ->
            val observer = table.connection()
            val leakedPlayerMessages = AtomicInteger()
            val changes = CopyOnWriteArrayList<ProtocolPublicUpdate>()
            val discarded = CountDownLatch(1)
            observer.onUpdate { leakedPlayerMessages.incrementAndGet() }.use {
                observer.onPublicUpdate { update ->
                    changes += update
                    if (update.visibleEvents.any { it is ProtocolGameEvent.TileDiscarded }) discarded.countDown()
                }.use {
                    val binding = observer.joinSpectator(JoinSpectatorRequest(table.room.roomId)).await()
                    table.start()
                    val player = table.host.snapshot().await()
                    val decision = player.decision
                    val option = decision.options.first { it.action is ProtocolLegalAction.DiscardTile }
                    table.host.submit(ProtocolActionChoice("public-discard", decision.roundId,
                        decision.decisionId, decision.controlEpoch, option.optionId)).await()
                    assertTrue(discarded.await(3, TimeUnit.SECONDS), "Real public discard must be delivered")
                    assertEquals(0, leakedPlayerMessages.get(), "Spectators must never receive an internal player projection")
                    val after = observer.publicSnapshot().await()
                    assertTrue(after.cursor > binding.snapshot.cursor)
                    assertEquals(player.sessionId, after.sessionId)
                    assertEquals(player.view.scores, assertNotNull(after.board).scores)
                    assertEquals(listOf((option.action as ProtocolLegalAction.DiscardTile).tile), after.board!!.discards[0])
                    assertTrue(changes.isNotEmpty())
                    changes.forEach { update ->
                        assertPublic(update.snapshot)
                        assertPublicEvents(update.visibleEvents)
                        assertEquals(if (update.isFullSnapshot) update.fromCursor else update.fromCursor + 1,
                            update.snapshot.cursor)
                    }
                    val cursors = changes.map { it.snapshot.cursor }
                    assertEquals(cursors.sorted(), cursors, "In-process PUBLIC delivery must be ordered")
                    val recovered = observer.resumePublic(PublicCheckpoint(after.sessionId, after.syncEpoch, 0L)).await()
                    assertEquals(after.cursor, recovered.cursor)
                    assertEquals(after.board, recovered.board)
                }
            }
        }
    }

    @Test fun realWinningActionPublishesFullScoringAndRecoverablePublicHistory() {
        val hand = MeldChoiceFixtures.tiles(Suit.MAN, "123456789") +
            MeldChoiceFixtures.tiles(Suit.PIN, "123") + Tile(Suit.DRAGON, 1)
        val state = TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.DRAGON, 1, 1))
        Table(initialState = state).use { table ->
            assertEquals(ProtocolRoomLifecycle.PLAYING, table.host.getRoomInfo().await().lifecycle,
                "A genuinely dealt initial table cannot masquerade as lobby")
            val (observer, binding) = table.spectator()
            val originalRound = assertNotNull(table.room.tableHost.authority.reviewCoordinator().activeRound)
            val decision = table.host.decision().await()
            val win = decision.options.single { it.action is ProtocolLegalAction.DeclareTsumo }
            val ended = CountDownLatch(1)
            observer.onPublicUpdate { update ->
                if (update.snapshot.settlementRoundId != null) ended.countDown()
            }.use {
                assertEquals(ProtocolReceiptStatus.APPLIED, table.host.submit(ProtocolActionChoice(
                    "public-real-tsumo", decision.roundId, decision.decisionId, decision.controlEpoch, win.optionId)).await().status)
                assertTrue(ended.await(3, TimeUnit.SECONDS))
                val snapshot = observer.publicSnapshot().await()
                assertPublic(snapshot)
                assertEquals(binding.snapshot.roundId, snapshot.settlementRoundId)
                val publicWin = snapshot.settlementEvents.filterIsInstance<ProtocolGameEvent.TsumoWin>().single()
                val actualPlayer = table.host.snapshot().await()
                val actualWin = actualPlayer.settlementEvents.filterIsInstance<ProtocolGameEvent.TsumoWin>().single()
                assertNotNull(publicWin.breakdown, "Do not strip publicly settled scoring to satisfy privacy")
                assertEquals(actualWin, publicWin)
                assertTrue(publicWin.han > 0)
                assertTrue(snapshot.settlementEvents.any { it is ProtocolGameEvent.ScoreSettled })
                val record = snapshot.session.roundRecords.single { it.roundId == snapshot.settlementRoundId }
                assertEquals(snapshot.session.scores, record.finalScores)
                assertNotNull(record.outcome)
                assertPublicEvents(observer.queryPublicRoundResult(assertNotNull(snapshot.settlementRoundId)).await())
                assertEquals(snapshot.settlementEvents, observer.queryPublicRoundResult(snapshot.settlementRoundId!!).await())
                val recovered = observer.resumePublic(PublicCheckpoint(snapshot.sessionId, snapshot.syncEpoch, 0L)).await()
                assertEquals(snapshot.settlementEvents, recovered.settlementEvents)
                assertEquals(snapshot.session.roundRecords, recovered.session.roundRecords)
                TestTableBuilder.assertPhysical(originalRound.readState())
            }
        }
    }
}
