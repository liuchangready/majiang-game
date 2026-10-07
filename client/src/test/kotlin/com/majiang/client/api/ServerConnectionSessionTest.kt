package com.majiang.client.api

import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint as ProtocolPublicCheckpoint
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.ServerConnection
import com.majiang.server.api.SpectatorBinding
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionOption
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolGamePhase
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPlayerView
import com.majiang.transport.api.ProtocolPublicBoardView
import com.majiang.transport.api.ProtocolPublicSessionSummary
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.api.ProtocolRoomMember
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolTile
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpScheduler
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.io.IOException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

class ServerConnectionSessionTest {

    private class FakeUdpClock(var now: Long = 1000L) : UdpClock {
        override fun currentTimeMillis(): Long = now
    }

    private class FakeUdpScheduler(private val clock: FakeUdpClock) : UdpScheduler {
        data class ScheduledTask(val triggerTime: Long, val task: Runnable, var cancelled: Boolean = false)
        val tasks = mutableListOf<ScheduledTask>()

        override fun schedule(delayMillis: Long, task: Runnable): Cancellable {
            val entry = ScheduledTask(clock.now + delayMillis, task)
            tasks.add(entry)
            return Cancellable { entry.cancelled = true }
        }

        fun advanceTime(deltaMillis: Long) {
            clock.now += deltaMillis
            while (true) {
                val nextTask = tasks
                    .filter { !it.cancelled && it.triggerTime <= clock.now }
                    .minByOrNull { it.triggerTime } ?: break
                tasks.remove(nextTask)
                if (!nextTask.cancelled) {
                    nextTask.task.run()
                }
            }
        }
    }

    private class FakeServerConnection : ServerConnection {
        var closedState: Boolean = false
        var disconnectReasonState: String? = null

        val updateListeners = CopyOnWriteArrayList<(ProtocolPlayerUpdate) -> Unit>()
        val disconnectListeners = CopyOnWriteArrayList<(String) -> Unit>()
        val roomListeners = CopyOnWriteArrayList<(ProtocolRoomInfo) -> Unit>()

        var defaultRoomInfo = ProtocolRoomInfo(
            roomId = "ROOM1234",
            lifecycle = ProtocolRoomLifecycle.LOBBY,
            version = 1L,
            members = listOf(ProtocolRoomMember(0, "Host", true, true, "HUMAN"))
        )
        var customRoomInfoFuture: CompletableFuture<ProtocolRoomInfo>? = null
        var lastSetReady: Boolean? = null
        var lastUpdatedConfig: ProtocolRoomConfig? = null

        var lastCreateRoomReq: CreateRoomRequest? = null
        var lastJoinRoomReq: JoinRoomRequest? = null
        var lastResumeReq: ResumeRequest? = null
        var lastSubmittedChoice: ProtocolActionChoice? = null
        var lastSubmittedPlan: ProtocolShufflePlan? = null
        var lastClaimedControl: Boolean = false
        var lastReleasedControl: Boolean = false

        var customCreateRoomFuture: CompletableFuture<RoomBinding>? = null
        var customResumeCursorFuture: CompletableFuture<ProtocolPlayerSnapshot>? = null
        var customSubmitFuture: CompletableFuture<ProtocolActionReceipt>? = null
        var customSubmitShuffleFuture: CompletableFuture<ProtocolActionReceipt>? = null
        var customSnapshotFuture: CompletableFuture<ProtocolPlayerSnapshot>? = null
        var lastResumeCursorRequested: Long? = null
        val resumeCursorRequests = mutableListOf<Long>()
        var synchronousFailure: String? = null

        fun createSnapshot(
            cursor: Long = 10L,
            roundId: String = "round-1",
            syncEpoch: String = "sync-1",
            sessionId: String = "session-1",
            customDecision: ProtocolPlayerDecision? = null
        ): ProtocolPlayerSnapshot {
            val tile = ProtocolTile.fromTileId(0)
            val view = ProtocolPlayerView(
                viewerSeat = 0,
                phase = ProtocolGamePhase.DISCARD,
                roundWind = 1,
                roundNumber = 1,
                honbaSticks = 0,
                riichiSticks = 0,
                dealerSeat = 0,
                currentTurnPlayer = 0,
                wallRemaining = 70,
                doraIndicators = listOf(tile),
                scores = listOf(25000, 25000, 25000, 25000),
                riichiStatus = listOf(false, false, false, false),
                discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                handTileCounts = listOf(14, 13, 13, 13),
                myClosedTiles = listOf(tile),
                myDrawnTile = null,
                legalActions = customDecision?.options?.map { it.action }
                    ?: listOf(ProtocolLegalAction.DiscardTile(0, tile))
            )
            val decision = customDecision ?: ProtocolPlayerDecision(
                roundId = roundId,
                decisionId = "dec-cursor-$cursor",
                controlEpoch = 1L,
                options = listOf(ProtocolActionOption("opt-1", ProtocolLegalAction.DiscardTile(0, tile)))
            )
            return ProtocolPlayerSnapshot(
                sessionId = sessionId,
                roundId = roundId,
                syncEpoch = syncEpoch,
                cursor = cursor,
                sessionVersion = 1L,
                roundVersion = 1L,
                view = view,
                decision = decision
            )
        }

        val defaultSnapshot: ProtocolPlayerSnapshot
            get() = createSnapshot()

        override val closed: Boolean get() = closedState
        override val disconnectReason: String? get() = disconnectReasonState

        override fun listRooms(): CompletableFuture<List<RoomSummary>> =
            CompletableFuture.completedFuture(
                listOf(RoomSummary("ROOM1234", "Test Room", "Host", 1, 4, false, "Standard"))
            )

        override fun createRoom(req: CreateRoomRequest): CompletableFuture<RoomBinding> {
            check(synchronousFailure != "createRoom") { "createRoom failed synchronously" }
            lastCreateRoomReq = req
            customCreateRoomFuture?.let { return it }
            val binding = RoomBinding("ROOM1234", 0, "token-host", defaultSnapshot)
            return CompletableFuture.completedFuture(binding)
        }

        override fun joinRoom(req: JoinRoomRequest): CompletableFuture<RoomBinding> {
            lastJoinRoomReq = req
            val binding = RoomBinding(req.roomId, req.preferSeat ?: 1, "token-player", defaultSnapshot)
            return CompletableFuture.completedFuture(binding)
        }

        override fun resumeSession(req: ResumeRequest): CompletableFuture<RoomBinding> {
            lastResumeReq = req
            val binding = RoomBinding(req.roomId, 0, req.token, defaultSnapshot)
            return CompletableFuture.completedFuture(binding)
        }

        var customLeaveRoomFuture: CompletableFuture<ProtocolActionReceipt>? = null

        override fun leaveRoom(): CompletableFuture<ProtocolActionReceipt> {
            customLeaveRoomFuture?.let { return it }
            return CompletableFuture.completedFuture(ProtocolActionReceipt("leave-req", ProtocolReceiptStatus.APPLIED))
        }

        override fun capabilities(): CompletableFuture<ProtocolAccessCapabilities> =
            CompletableFuture.completedFuture(ProtocolAccessCapabilities(1, setOf(4), remoteTransport = false, persistence = false, resumption = true))

        override fun snapshot(): CompletableFuture<ProtocolPlayerSnapshot> {
            customSnapshotFuture?.let { return it }
            return CompletableFuture.completedFuture(defaultSnapshot)
        }

        var customDecisionFuture: CompletableFuture<ProtocolPlayerDecision>? = null

        override fun decision(): CompletableFuture<ProtocolPlayerDecision> {
            customDecisionFuture?.let { return it }
            return CompletableFuture.completedFuture(defaultSnapshot.decision)
        }

        override fun submit(choice: ProtocolActionChoice): CompletableFuture<ProtocolActionReceipt> {
            check(synchronousFailure != "submit") { "submit failed synchronously" }
            lastSubmittedChoice = choice
            customSubmitFuture?.let { return it }
            return CompletableFuture.completedFuture(ProtocolActionReceipt(choice.requestId, ProtocolReceiptStatus.APPLIED, cursor = 11L))
        }

        override fun submitShufflePlan(plan: ProtocolShufflePlan): CompletableFuture<ProtocolActionReceipt> {
            lastSubmittedPlan = plan
            customSubmitShuffleFuture?.let { return it }
            return CompletableFuture.completedFuture(ProtocolActionReceipt(plan.requestId, ProtocolReceiptStatus.RECORDED))
        }

        override fun start(): CompletableFuture<ProtocolActionReceipt> {
            check(synchronousFailure != "start") { "start failed synchronously" }
            return CompletableFuture.completedFuture(ProtocolActionReceipt("start-req", ProtocolReceiptStatus.APPLIED))
        }

        override fun nextRound(): CompletableFuture<ProtocolActionReceipt> =
            CompletableFuture.completedFuture(ProtocolActionReceipt("next-req", ProtocolReceiptStatus.APPLIED))

        override fun resume(cursor: Long): CompletableFuture<ProtocolPlayerSnapshot> {
            lastResumeCursorRequested = cursor
            resumeCursorRequests.add(cursor)
            customResumeCursorFuture?.let { return it }
            return CompletableFuture.completedFuture(createSnapshot(cursor = cursor))
        }

        override fun claimControl(): CompletableFuture<ProtocolActionReceipt> {
            lastClaimedControl = true
            return CompletableFuture.completedFuture(ProtocolActionReceipt("claim-req", ProtocolReceiptStatus.APPLIED))
        }

        override fun releaseControl(): CompletableFuture<ProtocolActionReceipt> {
            lastReleasedControl = true
            return CompletableFuture.completedFuture(ProtocolActionReceipt("release-req", ProtocolReceiptStatus.APPLIED))
        }

        override fun queryRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> =
            CompletableFuture.completedFuture(emptyList())

        override fun onUpdate(listener: (ProtocolPlayerUpdate) -> Unit): AutoCloseable {
            updateListeners.add(listener)
            return AutoCloseable { updateListeners.remove(listener) }
        }

        override fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable {
            disconnectListeners.add(listener)
            return AutoCloseable { disconnectListeners.remove(listener) }
        }

        override fun getRoomInfo(): CompletableFuture<ProtocolRoomInfo> {
            customRoomInfoFuture?.let { return it }
            return CompletableFuture.completedFuture(defaultRoomInfo)
        }

        override fun setReady(ready: Boolean): CompletableFuture<ProtocolActionReceipt> {
            lastSetReady = ready
            return CompletableFuture.completedFuture(ProtocolActionReceipt("ready-req", ProtocolReceiptStatus.APPLIED))
        }

        override fun updateConfig(config: ProtocolRoomConfig): CompletableFuture<ProtocolActionReceipt> {
            lastUpdatedConfig = config
            return CompletableFuture.completedFuture(ProtocolActionReceipt("config-req", ProtocolReceiptStatus.APPLIED))
        }

        override fun onRoomUpdate(listener: (ProtocolRoomInfo) -> Unit): AutoCloseable {
            roomListeners.add(listener)
            return AutoCloseable { roomListeners.remove(listener) }
        }

        fun emitRoomUpdate(info: ProtocolRoomInfo) {
            roomListeners.forEach { it(info) }
        }

        val publicUpdateListeners = CopyOnWriteArrayList<(ProtocolPublicUpdate) -> Unit>()
        var lastJoinSpectatorReq: JoinSpectatorRequest? = null
        var lastResumeSpectatorReq: ResumeSpectatorRequest? = null
        var lastResumePublicCp: ProtocolPublicCheckpoint? = null
        var customJoinSpectatorFuture: CompletableFuture<SpectatorBinding>? = null
        var customResumeSpectatorFuture: CompletableFuture<SpectatorBinding>? = null
        var customPublicSnapshotFuture: CompletableFuture<ProtocolPublicSnapshot>? = null
        var customResumePublicFuture: CompletableFuture<ProtocolPublicSnapshot>? = null
        var customQueryPublicRoundResultFuture: CompletableFuture<List<ProtocolGameEvent>>? = null

        fun createPublicSnapshot(
            cursor: Long = 10L,
            syncEpoch: String = "sync-1",
            sessionId: String = "session-1",
            paused: Boolean = false
        ): ProtocolPublicSnapshot {
            val board = ProtocolPublicBoardView(
                phase = ProtocolGamePhase.DISCARD,
                roundWind = 1,
                roundNumber = 1,
                honbaSticks = 0,
                riichiSticks = 0,
                dealerSeat = 0,
                currentTurnPlayer = 0,
                wallRemaining = 70,
                doraIndicators = listOf(ProtocolTile.fromTileId(0)),
                scores = listOf(25000, 25000, 25000, 25000),
                riichiStatus = listOf(false, false, false, false),
                discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                handTileCounts = listOf(13, 13, 13, 13)
            )
            val session = ProtocolPublicSessionSummary(
                lifecycle = "ROUND_ACTIVE",
                scores = listOf(25000, 25000, 25000, 25000),
                roundWind = 1,
                roundNumber = 1,
                dealerSeat = 0,
                honba = 0
            )
            return ProtocolPublicSnapshot(
                sessionId = sessionId,
                sessionVersion = 1L,
                roundId = "round-1",
                roundVersion = 1L,
                syncEpoch = syncEpoch,
                cursor = cursor,
                board = board,
                session = session,
                clocks = emptyList(),
                paused = paused,
                settlementRoundId = null,
                settlementEvents = emptyList()
            )
        }

        val defaultPublicSnapshot: ProtocolPublicSnapshot
            get() = createPublicSnapshot()

        override fun joinSpectator(req: JoinSpectatorRequest): CompletableFuture<SpectatorBinding> {
            lastJoinSpectatorReq = req
            customJoinSpectatorFuture?.let { return it }
            val binding = SpectatorBinding(req.roomId, "spec-1", "spec-token-1", defaultPublicSnapshot)
            return CompletableFuture.completedFuture(binding)
        }

        override fun resumeSpectator(req: ResumeSpectatorRequest): CompletableFuture<SpectatorBinding> {
            lastResumeSpectatorReq = req
            customResumeSpectatorFuture?.let { return it }
            val binding = SpectatorBinding(req.roomId, req.spectatorId, req.resumeToken, defaultPublicSnapshot)
            return CompletableFuture.completedFuture(binding)
        }

        override fun publicSnapshot(): CompletableFuture<ProtocolPublicSnapshot> {
            customPublicSnapshotFuture?.let { return it }
            return CompletableFuture.completedFuture(defaultPublicSnapshot)
        }

        var resumePublicCallCount = 0
        override fun resumePublic(checkpoint: ProtocolPublicCheckpoint): CompletableFuture<ProtocolPublicSnapshot> {
            lastResumePublicCp = checkpoint
            resumePublicCallCount++
            customResumePublicFuture?.let { return it }
            return CompletableFuture.completedFuture(createPublicSnapshot(cursor = checkpoint.cursor))
        }

        override fun queryPublicRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> {
            customQueryPublicRoundResultFuture?.let { return it }
            return CompletableFuture.completedFuture(emptyList())
        }

        override fun onPublicUpdate(listener: (ProtocolPublicUpdate) -> Unit): AutoCloseable {
            publicUpdateListeners.add(listener)
            return AutoCloseable { publicUpdateListeners.remove(listener) }
        }

        fun emitPublicUpdate(update: ProtocolPublicUpdate) {
            publicUpdateListeners.forEach { it(update) }
        }

        override fun close() {
            closedState = true
            disconnectReasonState = ApiProtocol.LOCAL_CLOSED
        }

        fun emitUpdate(update: ProtocolPlayerUpdate) {
            updateListeners.forEach { it(update) }
        }

        fun emitDisconnect(reason: String) {
            disconnectListeners.forEach { it(reason) }
        }
    }

    @Test
    fun testLobbyFacadeOperations() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)

        // 1. listRooms
        val rooms = session.listRooms().join()
        assertEquals(1, rooms.size)
        assertEquals("ROOM1234", rooms[0].roomId)
        assertEquals("Test Room", rooms[0].roomName)

        // 2. createRoom
        assertNull(session.currentBinding)
        val snap = session.createRoom("MyHost", null).join()
        assertNotNull(session.currentBinding)
        assertEquals("ROOM1234", session.currentBinding!!.roomId)
        assertEquals(0, session.currentBinding!!.seat)
        assertEquals("round-1", snap.roundId)
        assertEquals(0, snap.viewerSeat)

        // Room operations
        val roomInfo = session.getRoomInfo().join()
        assertEquals("ROOM1234", roomInfo.roomId)
        assertEquals(1L, roomInfo.version)

        val readyReceipt = session.setReady(true).join()
        assertEquals(ReceiptStatus.APPLIED, readyReceipt.status)
        assertEquals(true, fake.lastSetReady)

        val configReceipt = session.updateConfig(RoomConfig()).join()
        assertEquals(ReceiptStatus.APPLIED, configReceipt.status)
        assertNotNull(fake.lastUpdatedConfig)

        var observedRoom: RoomInfo? = null
        val roomSub = session.onRoomUpdate { observedRoom = it }
        val updatedRoomInfo = fake.defaultRoomInfo.copy(version = 2L)
        fake.emitRoomUpdate(updatedRoomInfo)
        assertNotNull(observedRoom)
        assertEquals(2L, observedRoom!!.version)
        roomSub.close()

        // 3. joinRoom
        val joinSnap = session.joinRoom("ROOM5678", "Player2", 2).join()
        assertEquals("ROOM5678", session.currentBinding!!.roomId)
        assertEquals(2, session.currentBinding!!.seat)
        assertEquals("round-1", joinSnap.roundId)

        // 4. resumeSession
        val resumeSnap = session.resumeSession("ROOM5678", "token-player", 5L).join()
        assertEquals("ROOM5678", session.currentBinding!!.roomId)
        assertEquals("round-1", resumeSnap.roundId)

        // 5. leaveRoom
        val leaveReceipt = session.leaveRoom().join()
        assertEquals(ReceiptStatus.APPLIED, leaveReceipt.status)
        assertNull(session.currentBinding)
    }

    @Test
    fun testGameSessionFacadeOperations() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)

        // snapshot & decision
        val snap = session.snapshot().join()
        assertEquals("round-1", snap.roundId)

        val dec = session.decision().join()
        assertEquals("dec-cursor-10", dec.decisionId)
        assertEquals(1, dec.actions.size)
        assertTrue(dec.actions[0] is LegalActionView.DiscardTile)

        // submit ActionChoice
        val choice = ActionChoice("req-1", "round-1", "dec-1", 1L, "opt-1")
        val receipt = session.submit(choice).join()
        assertEquals(ReceiptStatus.APPLIED, receipt.status)
        assertEquals("req-1", fake.lastSubmittedChoice?.requestId)
        assertEquals("round-1", fake.lastSubmittedChoice?.roundId)
        assertEquals("dec-1", fake.lastSubmittedChoice?.decisionId)
        assertEquals(1L, fake.lastSubmittedChoice?.controlEpoch)
        assertEquals("opt-1", fake.lastSubmittedChoice?.optionId)

        // submitShufflePlan
        val plan = ShufflePlan("plan-1", "round-1", 1L, ShuffleSelection())
        val planReceipt = session.submitShufflePlan(plan).join()
        assertEquals(ReceiptStatus.RECORDED, planReceipt.status)
        assertEquals("plan-1", fake.lastSubmittedPlan?.requestId)
        assertEquals("round-1", fake.lastSubmittedPlan?.roundId)

        // start & nextRound
        assertEquals(ReceiptStatus.APPLIED, session.start().join().status)
        assertEquals(ReceiptStatus.APPLIED, session.nextRound().join().status)

        // claimControl & releaseControl
        assertEquals(ReceiptStatus.APPLIED, session.claimControl().join().status)
        assertTrue(fake.lastClaimedControl)

        assertEquals(ReceiptStatus.APPLIED, session.releaseControl().join().status)
        assertTrue(fake.lastReleasedControl)

        // queryRoundResult
        val events = session.queryRoundResult("round-1").join()
        assertEquals(0, events.size)

        // onUpdate listener
        var receivedUpdate: GameUpdateView? = null
        val sub = session.onUpdate { update ->
            receivedUpdate = update
        }
        val update = ProtocolPlayerUpdate(fake.createSnapshot(cursor = 11L), fromCursor = 10L, visibleEvents = emptyList())
        fake.emitUpdate(update)
        assertNotNull(receivedUpdate)
        assertEquals(10L, receivedUpdate!!.fromCursor)
        assertEquals("round-1", receivedUpdate!!.snapshot.roundId)
        sub.close()

        // onDisconnect listener
        var receivedReason: String? = null
        val discSub = session.onDisconnect { reason ->
            receivedReason = reason
        }
        fake.emitDisconnect("KICKED")
        assertEquals("KICKED", receivedReason)
        discSub.close()

        // KICKED is a permanent terminal state under R3 contract
        assertTrue(session.closed)
        assertEquals("KICKED", session.disconnectReason)
        session.close()
        assertTrue(session.closed)
        assertEquals("KICKED", session.disconnectReason)
    }

    @Test
    fun testEarlyPushBufferHandshake() {
        val fake = FakeServerConnection()
        val deferredCreate = CompletableFuture<RoomBinding>()
        fake.customCreateRoomFuture = deferredCreate

        val session = ServerConnectionSession(fake)
        val createFuture = session.createRoom("HostPlayer", null)
        assertEquals(ConnectionState.CONNECTING, session.connectionState)

        // Early PUSH arriving before createRoom response completes
        val earlySnap = fake.createSnapshot(cursor = 11L)
        val earlyUpdate = ProtocolPlayerUpdate(earlySnap, fromCursor = 10L, visibleEvents = emptyList())
        fake.emitUpdate(earlyUpdate)

        // Now complete createRoom response with base cursor = 10L
        val baseBinding = RoomBinding("ROOM-HANDSHAKE", 0, "token-1", fake.createSnapshot(cursor = 10L))
        deferredCreate.complete(baseBinding)

        val resultSnap = createFuture.join()
        // Must drain early updates and not roll back to cursor 10
        assertEquals(11L, resultSnap.cursor)
        assertEquals(11L, session.currentSnapshot?.cursor)
        assertEquals(ConnectionState.CONNECTED, session.connectionState)
    }

    @Test
    fun testReorderBufferAndGapHealing() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("HostPlayer", null).join()
        assertEquals(10L, session.currentSnapshot?.cursor)

        val receivedUpdates = mutableListOf<GameUpdateView>()
        session.onUpdate { receivedUpdates.add(it) }

        // Cursor 12 arrives before Cursor 11 (GAP: fromCursor=11 > currentCursor=10)
        val snap12 = fake.createSnapshot(cursor = 12L)
        val update12 = ProtocolPlayerUpdate(snap12, fromCursor = 11L, visibleEvents = emptyList())
        fake.emitUpdate(update12)

        // Verified: cursor 12 not yet installed, decision not exposed
        assertEquals(10L, session.currentSnapshot?.cursor)
        assertEquals("dec-cursor-10", session.decision().join().decisionId)
        assertEquals(10L, fake.lastResumeCursorRequested)
        assertEquals(0, receivedUpdates.size)

        // Now Cursor 11 arrives (contiguous fromCursor=10)
        val snap11 = fake.createSnapshot(cursor = 11L)
        val update11 = ProtocolPlayerUpdate(snap11, fromCursor = 10L, visibleEvents = emptyList())
        fake.emitUpdate(update11)

        // Both updates drained in sequence!
        assertEquals(12L, session.currentSnapshot?.cursor)
        assertEquals(2, receivedUpdates.size)
        assertEquals(11L, receivedUpdates[0].snapshot.cursor)
        assertEquals(12L, receivedUpdates[1].snapshot.cursor)
        assertEquals("dec-cursor-12", session.decision().join().decisionId)
    }

    @Test
    fun testGapRecoveryFailureAllowsRetry() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("HostPlayer", null).join()

        // Prepare failing resume
        val failingResume = CompletableFuture<ProtocolPlayerSnapshot>()
        fake.customResumeCursorFuture = failingResume

        // GAP arrives: fromCursor = 11 > 10
        val update12 = ProtocolPlayerUpdate(fake.createSnapshot(cursor = 12L), fromCursor = 11L, visibleEvents = emptyList())
        fake.emitUpdate(update12)
        assertEquals(10L, fake.lastResumeCursorRequested)

        // Resume fails
        failingResume.completeExceptionally(IOException("Network error"))

        // Next gap update arrives: should be able to retry resume without permanently hanging
        fake.customResumeCursorFuture = CompletableFuture.completedFuture(fake.createSnapshot(cursor = 12L))
        val update13 = ProtocolPlayerUpdate(fake.createSnapshot(cursor = 13L), fromCursor = 12L, visibleEvents = emptyList())
        fake.emitUpdate(update13)

        // Verified: recovered
        assertEquals(13L, session.currentSnapshot?.cursor)
    }

    @Test
    fun testFullReplacementPushEstablishesNewBaseline() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("HostPlayer", null).join()
        assertEquals(10L, session.currentSnapshot?.cursor)

        // Buffer an old gap update
        val gapUpdate = ProtocolPlayerUpdate(fake.createSnapshot(cursor = 15L), fromCursor = 14L, visibleEvents = emptyList())
        fake.emitUpdate(gapUpdate)

        // Server sends explicit full replacement with isFullSnapshot = true
        val fullSnap = fake.createSnapshot(cursor = 20L)
        val fullPush = ProtocolPlayerUpdate(fullSnap, fromCursor = 20L, visibleEvents = emptyList(), isFullSnapshot = true)
        fake.emitUpdate(fullPush)

        assertEquals(20L, session.currentSnapshot?.cursor)
    }

    @Test
    fun testCrossSessionAndEpochIsolation() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("HostPlayer", null).join()
        assertEquals("session-1", session.currentSnapshot?.sessionId)
        assertEquals("sync-1", session.currentSnapshot?.syncEpoch)
        assertEquals(10L, session.currentSnapshot?.cursor)

        // Foreign session update
        val foreignSnap = fake.createSnapshot(cursor = 15L, sessionId = "foreign-session")
        fake.emitUpdate(ProtocolPlayerUpdate(foreignSnap, fromCursor = 10L, visibleEvents = emptyList()))
        assertEquals(10L, session.currentSnapshot?.cursor)

        // Old epoch non-full replacement update
        val oldEpochSnap = fake.createSnapshot(cursor = 15L, syncEpoch = "old-epoch")
        fake.emitUpdate(ProtocolPlayerUpdate(oldEpochSnap, fromCursor = 10L, visibleEvents = emptyList()))
        assertEquals(10L, session.currentSnapshot?.cursor)
    }

    @Test
    fun testPendingStateGuardsAgainstConcurrentActions() {
        val fake = FakeServerConnection()
        val deferredSubmit = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitFuture = deferredSubmit

        val session = ServerConnectionSession(fake)
        session.createRoom("HostPlayer", null).join()
        assertFalse(session.isPending)

        val choice1 = ActionChoice("req-1", "round-1", "dec-1", 1L, "opt-1")
        val future1 = session.submit(choice1)
        assertTrue(session.isPending)

        // Second submit while first is in-flight must be rejected with ACTION_IN_FLIGHT
        val choice2 = ActionChoice("req-2", "round-1", "dec-1", 1L, "opt-2")
        val future2 = session.submit(choice2)
        val receipt2 = future2.join()
        assertEquals(ReceiptStatus.REJECTED, receipt2.status)
        assertEquals("ACTION_IN_FLIGHT", receipt2.reason)
        assertTrue(session.isPending)

        // Complete first submit
        deferredSubmit.complete(ProtocolActionReceipt("req-1", ProtocolReceiptStatus.APPLIED))
        val receipt1 = future1.join()
        assertEquals(ReceiptStatus.APPLIED, receipt1.status)
        assertFalse(session.isPending)

        // Now third submit is accepted
        fake.customSubmitFuture = null
        val choice3 = ActionChoice("req-3", "round-1", "dec-1", 1L, "opt-3")
        val receipt3 = session.submit(choice3).join()
        assertEquals(ReceiptStatus.APPLIED, receipt3.status)
        assertFalse(session.isPending)
    }

    @Test
    fun testCloseTerminatesPendingFutures() {
        val fake = FakeServerConnection()
        val deferredSubmit = CompletableFuture<ProtocolActionReceipt>()
        val deferredSnapshot = CompletableFuture<ProtocolPlayerSnapshot>()
        fake.customSubmitFuture = deferredSubmit
        fake.customSnapshotFuture = deferredSnapshot

        val session = ServerConnectionSession(fake)
        session.createRoom("HostPlayer", null).join()

        val choice = ActionChoice("req-pending", "round-1", "dec-1", 1L, "opt-1")
        val submitFuture = session.submit(choice)
        val snapshotFuture = session.snapshot()
        assertTrue(session.isPending)

        session.close()

        assertTrue(session.closed)
        assertFalse(session.isPending)
        assertEquals(ConnectionState.CLOSED, session.connectionState)

        try {
            submitFuture.get()
            org.junit.jupiter.api.fail("Should have thrown ExecutionException for submitFuture")
        } catch (e: ExecutionException) {
            assertTrue(e.cause is IOException)
        }

        try {
            snapshotFuture.get()
            org.junit.jupiter.api.fail("Should have thrown ExecutionException for snapshotFuture")
        } catch (e: ExecutionException) {
            assertTrue(e.cause is IOException)
        }
    }

    @Test
    fun testListenerExceptionDoesNotAffectOtherListeners() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        var listener2Called = false
        session.onUpdate {
            throw RuntimeException("Boom in listener 1")
        }
        session.onUpdate {
            listener2Called = true
        }

        val update = ProtocolPlayerUpdate(fake.createSnapshot(cursor = 11L), fromCursor = 10L, visibleEvents = emptyList())
        fake.emitUpdate(update)

        assertTrue(listener2Called, "Second listener should still receive update even if first listener throws")
    }

    @Test
    fun testExplicitFullReplacementPushFlag() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()
        assertEquals(10L, session.currentSnapshot?.cursor)

        var observedUpdate: GameUpdateView? = null
        session.onUpdate { observedUpdate = it }

        // Arrive with isFullSnapshot = true
        val fullSnap = fake.createSnapshot(cursor = 50L)
        val fullUpdate = ProtocolPlayerUpdate(fullSnap, fromCursor = 30L, visibleEvents = emptyList(), isFullSnapshot = true)
        fake.emitUpdate(fullUpdate)

        assertEquals(50L, session.currentSnapshot?.cursor)
        assertNotNull(observedUpdate)
        assertEquals(50L, observedUpdate!!.snapshot.cursor)
        assertTrue(observedUpdate!!.isFullSnapshot)
    }

    @Test
    fun testManualDispatcherDefersAndSuppressesCallbacks() {
        val fake = FakeServerConnection()
        val manualQueue = mutableListOf<() -> Unit>()
        val session = ServerConnectionSession(fake, dispatch = { manualQueue.add(it) })
        session.createRoom("HostPlayer", null)
        assertEquals(1, manualQueue.size)
        manualQueue.removeAt(0).invoke() // Drain createRoom completion

        var listenerUpdateReceived: GameUpdateView? = null
        session.onUpdate { listenerUpdateReceived = it }

        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 11L), fromCursor = 10L, visibleEvents = emptyList()))

        // Callback not called until dispatch queue drains
        assertNull(listenerUpdateReceived)
        assertEquals(1, manualQueue.size)

        manualQueue.removeAt(0).invoke()
        assertNotNull(listenerUpdateReceived)
        assertEquals(11L, listenerUpdateReceived!!.snapshot.cursor)

        // When closed, queued callbacks must be suppressed
        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 12L), fromCursor = 11L, visibleEvents = emptyList()))
        assertEquals(1, manualQueue.size)
        session.close()
        manualQueue.removeAt(0).invoke()
        assertEquals(11L, listenerUpdateReceived!!.snapshot.cursor) // Still 11L, not updated
    }

    @Test
    fun testAutoReconnectWithExponentialBackoff() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)

        val firstConn = FakeServerConnection()
        val secondConn = FakeServerConnection()

        var factoryInvocations = 0
        val factory: () -> CompletableFuture<ServerConnection> = {
            factoryInvocations++
            CompletableFuture.completedFuture(secondConn)
        }

        val session = ServerConnectionSession(
            initialConnection = firstConn,
            connectionFactory = factory,
            clock = clock,
            scheduler = scheduler
        )

        session.createRoom("Host", null).join()
        assertEquals(ConnectionState.CONNECTED, session.connectionState)
        assertEquals(10L, session.currentSnapshot?.cursor)

        val stateTransitions = mutableListOf<ConnectionState>()
        session.onStateChange { stateTransitions.add(it) }
        stateTransitions.clear()

        // Unexpected disconnect
        firstConn.emitDisconnect("CONNECTION_LOST")

        assertEquals(ConnectionState.RECONNECTING, session.connectionState)
        assertEquals(listOf(ConnectionState.RECONNECTING), stateTransitions)
        assertEquals(10L, session.currentSnapshot?.cursor) // View preserved
        assertNotNull(session.currentBinding) // Credentials preserved

        // Advance scheduler to trigger backoff
        assertEquals(1, scheduler.tasks.size)
        assertEquals(1500L, scheduler.tasks[0].triggerTime) // 1000 + 500ms

        scheduler.advanceTime(500L)

        assertEquals(1, factoryInvocations)
        assertEquals(ConnectionState.CONNECTED, session.connectionState)
        assertEquals(listOf(ConnectionState.RECONNECTING, ConnectionState.CONNECTED), stateTransitions)
        assertFalse(secondConn.lastClaimedControl) // Not automatically claimed control
    }

    @Test
    fun testPermanentDisconnectDoesNotReconnect() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val conn = FakeServerConnection()
        var factoryInvocations = 0

        val session = ServerConnectionSession(
            initialConnection = conn,
            connectionFactory = { factoryInvocations++; CompletableFuture.completedFuture(conn) },
            clock = clock,
            scheduler = scheduler
        )
        session.createRoom("Host", null).join()

        // Disconnect with DISPLACED_BY_NEW_CONNECTION
        conn.emitDisconnect(ApiProtocol.DISPLACED_BY_NEW_CONNECTION)

        assertEquals(ConnectionState.CLOSED, session.connectionState)
        assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, session.disconnectReason)
        assertEquals(0, scheduler.tasks.size)
        assertEquals(0, factoryInvocations)
    }

    @Test
    fun testInProcessConnectionDoesNotReconnect() {
        val conn = FakeServerConnection()
        // No connectionFactory provided (in-process)
        val session = ServerConnectionSession(initialConnection = conn)
        session.createRoom("Host", null).join()

        conn.emitDisconnect("CONNECTION_LOST")

        assertEquals(ConnectionState.CLOSED, session.connectionState)
        assertEquals("CONNECTION_LOST", session.disconnectReason)
    }

    @Test
    fun testEventDeduplicationAndCollectionIsolation() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val listener1Events = mutableListOf<List<GameEventView>>()
        val listener2Events = mutableListOf<List<GameEventView>>()

        session.onUpdate { update ->
            listener1Events.add(update.visibleEvents)
        }
        session.onUpdate { update ->
            listener2Events.add(update.visibleEvents)
        }

        val event1 = ProtocolGameEvent.StackingStarted(0)
        val update11 = ProtocolPlayerUpdate(fake.createSnapshot(cursor = 11L), fromCursor = 10L, visibleEvents = listOf(event1))
        fake.emitUpdate(update11)

        val update12 = ProtocolPlayerUpdate(fake.createSnapshot(cursor = 12L), fromCursor = 11L, visibleEvents = listOf(event1))
        fake.emitUpdate(update12)

        // Both updates delivered (distinct batch cursors), not dropped due to same event content
        assertEquals(2, listener1Events.size)
        assertEquals(2, listener2Events.size)
        assertEquals(1, listener1Events[0].size)
        assertEquals(1, listener1Events[1].size)
    }

    @Test
    fun testIdenticalEventPayloadInNextRoundRemainsDistinct() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()
        val received = mutableListOf<GameUpdateView>()
        session.onUpdate { received.add(it) }

        val event = ProtocolGameEvent.StackingStarted(0)
        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 11L), 10L, listOf(event)))
        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 12L, roundId = "round-2"), 11L, listOf(event)))

        assertEquals(listOf("round-1", "round-2"), received.map { it.snapshot.roundId })
        assertEquals(2, received.flatMap { it.visibleEvents }.filterIsInstance<GameEventView.StackingStarted>().size)
        assertEquals("round-2", session.currentSnapshot?.roundId)
    }

    @Test
    fun testScorePaymentFactsCannotBeMutatedAcrossListenersOrSnapshots() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()
        val firstListenerScores = mutableListOf<List<Int>>()
        val secondListenerScores = mutableListOf<List<Int>>()
        var scoresImmutable = false
        var paymentsImmutable = false
        session.onUpdate { update ->
            val score = update.visibleEvents.single() as GameEventView.ScoreSettled
            firstListenerScores.add(score.newScores)
            scoresImmutable = runCatching { (score.newScores as MutableList<Int>)[0] = -999 }
                .exceptionOrNull() is UnsupportedOperationException
            paymentsImmutable = runCatching { (score.payments as MutableMap<Int, Int>)[0] = -999 }
                .exceptionOrNull() is UnsupportedOperationException
        }
        session.onUpdate { update ->
            secondListenerScores.add((update.visibleEvents.single() as GameEventView.ScoreSettled).newScores)
        }

        val sourceScores = mutableListOf(25000, 25000, 25000, 25000)
        val sourcePayments = mutableMapOf(0 to 1000, 1 to -1000)
        val event = ProtocolGameEvent.ScoreSettled(0, sourcePayments, sourceScores)
        sourceScores[0] = -999
        sourcePayments[0] = -999
        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 11L), 10L, listOf(event)))

        assertEquals(25000, firstListenerScores.single()[0])
        assertEquals(25000, secondListenerScores.single()[0])
        assertTrue(scoresImmutable)
        assertTrue(paymentsImmutable)
        assertEquals(11L, session.currentSnapshot?.cursor)
    }

    @Test
    fun testSnapshotPointCollectionsAreCopiedOnInstallAndUpdate() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()
        val points = mutableListOf(10, 20, 30, 40)
        val first = fake.createSnapshot(cursor = 11L).copy(cheatPoints = points)
        fake.emitUpdate(ProtocolPlayerUpdate(first, 10L, emptyList()))
        val retained = session.currentSnapshot!!
        points[0] = 999
        assertEquals(10, retained.cheatPoints[0])

        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 12L).copy(cheatPoints = listOf(5, 20, 30, 40)), 11L, emptyList()))
        assertEquals(10, retained.cheatPoints[0])
        assertEquals(5, session.currentSnapshot!!.cheatPoints[0])
    }

    @Test
    fun testRemainingGapIsRecoveredAfterPartialResumeProgress() {
        val fake = FakeServerConnection()
        val firstResume = CompletableFuture<ProtocolPlayerSnapshot>()
        val secondResume = CompletableFuture<ProtocolPlayerSnapshot>()
        fake.customResumeCursorFuture = firstResume
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 12L), 11L, emptyList()))
        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 15L), 14L, emptyList()))
        assertEquals(listOf(10L), fake.resumeCursorRequests)
        fake.customResumeCursorFuture = secondResume
        firstResume.complete(fake.createSnapshot(cursor = 12L))
        assertEquals(listOf(10L, 12L), fake.resumeCursorRequests)
        secondResume.complete(fake.createSnapshot(cursor = 15L))
        assertEquals(15L, session.currentSnapshot?.cursor)
    }

    @Test
    fun testLateResumeCannotRollBackUpdatesAppliedDuringRecovery() {
        val fake = FakeServerConnection()
        val delayedResume = CompletableFuture<ProtocolPlayerSnapshot>()
        fake.customResumeCursorFuture = delayedResume
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 12L), 11L, emptyList()))
        assertEquals(listOf(10L), fake.resumeCursorRequests)
        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 11L), 10L, emptyList()))
        fake.emitUpdate(ProtocolPlayerUpdate(fake.createSnapshot(cursor = 13L), 12L, emptyList()))
        assertEquals(13L, session.currentSnapshot?.cursor)
        delayedResume.complete(fake.createSnapshot(cursor = 12L))
        assertEquals(13L, session.currentSnapshot?.cursor)
        assertEquals("dec-cursor-13", session.decision().join().decisionId)
    }

    @Test
    fun testClosingDuringCreateInvalidatesLateBindingAndUpdateSubscription() {
        val fake = FakeServerConnection()
        val delayedCreate = CompletableFuture<RoomBinding>()
        fake.customCreateRoomFuture = delayedCreate
        val session = ServerConnectionSession(fake)
        val connecting = session.createRoom("Host", null)
        assertFalse(connecting.isDone)
        session.close()
        assertTrue(connecting.isCompletedExceptionally)
        delayedCreate.complete(RoomBinding("ROOM1234", 0, "token-host", fake.createSnapshot()))
        assertNull(session.currentSnapshot)
        assertTrue(fake.updateListeners.isEmpty())
    }

    @Test
    fun testSynchronousConnectionAndCommandFailuresBecomeFuturesAndPermitRetry() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        fake.synchronousFailure = "createRoom"
        assertTrue(session.createRoom("Host", null).isCompletedExceptionally)
        fake.synchronousFailure = null
        assertEquals(10L, session.createRoom("Host", null).join().cursor)

        val choice = ActionChoice("req-sync-fail", "round-1", "dec-cursor-10", 1L, "opt-1")
        fake.synchronousFailure = "submit"
        assertTrue(session.submit(choice).isCompletedExceptionally)
        assertFalse(session.isPending)
        fake.synchronousFailure = "start"
        assertTrue(session.start().isCompletedExceptionally)
        assertFalse(session.isPending)
        fake.synchronousFailure = null
        assertEquals(ReceiptStatus.APPLIED, session.submit(choice.copy(requestId = "req-retry")).join().status)
        assertFalse(session.isPending)
    }

    @Test
    fun testDecisionOptionsPreservePhysicalCopyIdDuringSubmission() {
        val fake = FakeServerConnection()
        val firstCopy = ProtocolTile.fromTileId(16)
        val secondCopy = ProtocolTile.fromTileId(17)
        val decision = ProtocolPlayerDecision(
            roundId = "round-1",
            decisionId = "physical-copy-decision",
            controlEpoch = 1L,
            options = listOf(
                ProtocolActionOption("discard-copy-0", ProtocolLegalAction.DiscardTile(0, firstCopy)),
                ProtocolActionOption("discard-copy-1", ProtocolLegalAction.DiscardTile(0, secondCopy))
            )
        )
        fake.customCreateRoomFuture = CompletableFuture.completedFuture(
            RoomBinding("ROOM1234", 0, "token-host", fake.createSnapshot(customDecision = decision))
        )
        val session = ServerConnectionSession(fake)
        val snapshot = session.createRoom("Host", null).join()
        val mappedDecision = snapshot.decision!!
        val actions = mappedDecision.options.map { it.action as LegalActionView.DiscardTile }
        assertEquals(listOf(16, 17), actions.map { it.tile.tileId })
        assertEquals(listOf(0, 1), actions.map { it.tile.copyIndex })

        val selected = mappedDecision.options.single { it.action == actions[1] }
        val choice = ActionChoice("req-copy-1", "round-1", decision.decisionId, decision.controlEpoch, selected.optionId)
        assertEquals(ReceiptStatus.APPLIED, session.submit(choice).join().status)
        assertEquals("discard-copy-1", fake.lastSubmittedChoice?.optionId)
        assertEquals("physical-copy-decision", fake.lastSubmittedChoice?.decisionId)
    }

    @Test
    fun testShuffleConfirmationIsPendingUntilReceiptAndCloseTerminatesIt() {
        val fake = FakeServerConnection()
        val delayed = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitShuffleFuture = delayed
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()
        val plan = ShufflePlan("slow-shuffle", "round-1", 1L, ShuffleSelection())

        val first = session.submitShufflePlan(plan)
        assertFalse(first.isDone)
        assertTrue(session.isPending)
        val duplicate = session.submitShufflePlan(plan.copy(requestId = "duplicate-shuffle")).join()
        assertEquals(ReceiptStatus.REJECTED, duplicate.status)
        assertEquals("ACTION_IN_FLIGHT", duplicate.reason)
        assertEquals("slow-shuffle", fake.lastSubmittedPlan?.requestId)

        session.close()
        assertTrue(first.isCompletedExceptionally)
        assertFalse(session.isPending)
        delayed.complete(ProtocolActionReceipt("slow-shuffle", ProtocolReceiptStatus.RECORDED))
        assertTrue(session.closed)
    }

    @Test
    fun testDuplicateDisconnectDoesNotTriggerConcurrentReconnectLoops() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val firstConn = FakeServerConnection()
        val secondConn = FakeServerConnection()

        var factoryCount = 0
        val session = ServerConnectionSession(
            initialConnection = firstConn,
            connectionFactory = { factoryCount++; CompletableFuture.completedFuture(secondConn) },
            clock = clock,
            scheduler = scheduler
        )
        session.createRoom("Host", null).join()

        // First disconnect
        firstConn.emitDisconnect("NETWORK_DROP")
        assertEquals(ConnectionState.RECONNECTING, session.connectionState)
        assertEquals(1, scheduler.tasks.size)

        // Duplicate disconnect notification
        firstConn.emitDisconnect("NETWORK_DROP_AGAIN")
        // Must NOT schedule another task!
        assertEquals(1, scheduler.tasks.size)
    }

    @Test
    fun testLeaveRoomInFlightPreventsReconnectOnDisconnect() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val conn = FakeServerConnection()

        var factoryCount = 0
        val session = ServerConnectionSession(
            initialConnection = conn,
            connectionFactory = { factoryCount++; CompletableFuture.completedFuture(conn) },
            clock = clock,
            scheduler = scheduler
        )
        session.createRoom("Host", null).join()
        assertNotNull(session.currentBinding)

        // Initiate leaveRoom
        val deferredLeave = CompletableFuture<ProtocolActionReceipt>()
        conn.customLeaveRoomFuture = deferredLeave
        val leaveFuture = session.leaveRoom()
        assertFalse(leaveFuture.isDone)

        // Connection disconnects while leaveRoom is in-flight
        conn.emitDisconnect("SOCKET_CLOSED")

        // Should NOT attempt reconnect because explicit leave was in-flight
        assertEquals(0, scheduler.tasks.size)
        assertEquals(0, factoryCount)
        assertTrue(session.closed)
        assertNull(session.currentBinding)
    }

    @Test
    fun testRejectedLeavePreservesViableBinding() {
        val fake = FakeServerConnection()
        val deferredLeave = CompletableFuture<ProtocolActionReceipt>()
        fake.customLeaveRoomFuture = deferredLeave

        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()
        assertNotNull(session.currentBinding)

        // Submitting leaveRoom
        val leaveFuture = session.leaveRoom()
        assertNotNull(session.currentBinding, "In-flight leave must not destroy binding prematurely")

        // Server rejects leave
        deferredLeave.complete(ProtocolActionReceipt("leave-req", ProtocolReceiptStatus.REJECTED, reason = "MATCH_ACTIVE"))
        val receipt = leaveFuture.join()
        assertEquals(ReceiptStatus.REJECTED, receipt.status)

        // Binding is preserved!
        assertNotNull(session.currentBinding, "Rejected leave must preserve viable binding")
        assertEquals("ROOM1234", session.currentBinding!!.roomId)
    }

    @Test
    fun testOldEpochPushCannotReplaceCurrentBindingEvenIfFull() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()
        assertEquals("sync-1", session.currentSnapshot?.syncEpoch)
        assertEquals(10L, session.currentSnapshot?.cursor)

        // Foreign epoch full push arriving
        val oldEpochFullSnap = fake.createSnapshot(cursor = 50L, syncEpoch = "different-epoch")
        val push = ProtocolPlayerUpdate(oldEpochFullSnap, fromCursor = 50L, visibleEvents = emptyList(), isFullSnapshot = true)
        fake.emitUpdate(push)

        // Dropped! Baseline remains sync-1, cursor 10
        assertEquals("sync-1", session.currentSnapshot?.syncEpoch)
        assertEquals(10L, session.currentSnapshot?.cursor)
    }

    @Test
    fun testActionCancelledOrFailedReleasesPendingState() {
        val fake = FakeServerConnection()
        val deferredSubmit = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitFuture = deferredSubmit

        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val choice = ActionChoice("req-cancel", "round-1", "dec-1", 1L, "opt-1")
        val future = session.submit(choice)
        assertTrue(session.isPending)

        // Cancel the future
        future.cancel(true)
        assertFalse(session.isPending, "Pending state must be released upon future cancellation")
    }

    @Test
    fun testLateReceiptFromCancelledActionCannotClearNewPending() {
        val fake = FakeServerConnection()
        val firstReply = CompletableFuture<ProtocolActionReceipt>()
        val secondReply = CompletableFuture<ProtocolActionReceipt>()
        fake.customSubmitFuture = firstReply

        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val choice1 = ActionChoice("req-1", "round-1", "dec-1", 1L, "opt-1")
        val first = session.submit(choice1)
        assertTrue(session.isPending)

        // Cancel first action
        first.cancel(false)
        assertFalse(session.isPending)

        // Submit second action
        fake.customSubmitFuture = secondReply
        val choice2 = ActionChoice("req-2", "round-1", "dec-1", 1L, "opt-2")
        val second = session.submit(choice2)
        assertTrue(session.isPending)

        // Late first receipt arrives
        firstReply.complete(ProtocolActionReceipt("req-1", ProtocolReceiptStatus.APPLIED))
        assertTrue(session.isPending, "Late receipt from cancelled action must not release newer pending gate")

        // Second receipt arrives
        secondReply.complete(ProtocolActionReceipt("req-2", ProtocolReceiptStatus.REJECTED))
        second.join()
        assertFalse(session.isPending)
    }

    @Test
    fun testRebindingTerminatesOldGenerationRequests() {
        val fake = FakeServerConnection()
        val deferredSnapshot = CompletableFuture<ProtocolPlayerSnapshot>()
        fake.customSnapshotFuture = deferredSnapshot

        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val oldSnapshotFuture = session.snapshot()
        assertFalse(oldSnapshotFuture.isDone)

        // Rebinding via resumeSession
        fake.customSnapshotFuture = null
        val resumeSnap = session.resumeSession("ROOM1234", "token-player", 10L).join()
        assertNotNull(resumeSnap)

        // Old generation read must be terminated
        assertTrue(oldSnapshotFuture.isDone, "Old generation request must be terminated on rebinding")
        try {
            oldSnapshotFuture.get()
            org.junit.jupiter.api.fail("Expected ExecutionException for old generation request")
        } catch (e: ExecutionException) {
            assertTrue(e.cause is IOException)
        }
    }

    @Test
    fun testDelayedDecisionCannotReturnOldOptionsAfterNewPushInSameControlEpoch() {
        val fake = FakeServerConnection()
        val deferredDecision = CompletableFuture<ProtocolPlayerDecision>()
        fake.customDecisionFuture = deferredDecision

        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        // 1. Pending decision query
        val decFuture = session.decision()
        assertFalse(decFuture.isDone)

        // 2. Inbound update arrives with newer cursor and updated decision in SAME controlEpoch
        val newDecision = ProtocolPlayerDecision(
            roundId = "round-1",
            decisionId = "dec-newer",
            controlEpoch = 1L,
            options = listOf(
                ProtocolActionOption("opt-new", ProtocolLegalAction.Pass(0))
            )
        )
        val advancedSnapshot = fake.createSnapshot(
            cursor = 12L,
            customDecision = newDecision
        )
        fake.emitUpdate(ProtocolPlayerUpdate(advancedSnapshot, fromCursor = 10L, visibleEvents = emptyList()))

        // 3. Delayed old decision response arrives with same controlEpoch
        val oldDecision = ProtocolPlayerDecision(
            roundId = "round-1",
            decisionId = "dec-old",
            controlEpoch = 1L,
            options = listOf(
                ProtocolActionOption("opt-old", ProtocolLegalAction.Pass(0))
            )
        )
        deferredDecision.complete(oldDecision)

        // 4. Must return the confirmed newer decision from advanced snapshot
        val result = decFuture.join()
        assertEquals("dec-newer", result.decisionId)
    }

    @Test
    fun testQueuedQueryCompletionIsInvalidatedByCloseBeforeUiConsumesIt() {
        val fake = FakeServerConnection()
        val uiQueue = mutableListOf<() -> Unit>()
        val session = ServerConnectionSession(fake, dispatch = { uiQueue.add(it) })
        session.createRoom("Host", null)
        while (uiQueue.isNotEmpty()) uiQueue.removeAt(0)()

        // Snapshot query
        val snapFuture = session.snapshot()
        // Connection returned immediately, but completion is queued in uiQueue
        assertFalse(snapFuture.isDone)

        // Runtime closes before dispatcher drain
        session.close()

        // Now drain dispatcher
        while (uiQueue.isNotEmpty()) uiQueue.removeAt(0)()

        // Must fail with IOException, never succeed
        assertTrue(snapFuture.isCompletedExceptionally, "Queued query completion must fail after close")
        try {
            snapFuture.get()
            org.junit.jupiter.api.fail("Expected ExecutionException")
        } catch (e: ExecutionException) {
            assertTrue(e.cause is IOException)
        }
    }

    @Test
    fun testQueuedActiveStateCannotBePublishedAfterClose() {
        val fake = FakeServerConnection()
        val uiQueue = mutableListOf<() -> Unit>()
        val session = ServerConnectionSession(fake, dispatch = { uiQueue.add(it) })

        val observedStates = mutableListOf<ConnectionState>()
        session.onStateChange { observedStates.add(it) }

        // Session closes before dispatcher drain
        session.close()

        // Drain dispatcher
        while (uiQueue.isNotEmpty()) uiQueue.removeAt(0)()

        // Must not contain CONNECTED, only CLOSED
        assertFalse(observedStates.contains(ConnectionState.CONNECTED), "Queued active state must be suppressed after close")
        assertEquals(listOf(ConnectionState.CLOSED), observedStates)

        // Test unregister handle suppression
        val unregStates = mutableListOf<ConnectionState>()
        val handle2 = session.onStateChange { unregStates.add(it) }
        handle2.close()
        while (uiQueue.isNotEmpty()) uiQueue.removeAt(0)()
        assertTrue(unregStates.isEmpty(), "Unregistered listener must receive no notifications")
    }

    @Test
    fun testReorderedAndDuplicateRoomUpdatesCannotRollReadinessBackward() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val observedVersions = mutableListOf<Long>()
        val handle = session.onRoomUpdate { observedVersions.add(it.version) }
        observedVersions.clear()

        val ready3 = fake.defaultRoomInfo.copy(
            version = 3L,
            members = listOf(ProtocolRoomMember(0, "Host", true, true, "HUMAN"))
        )
        fake.emitRoomUpdate(ready3)
        fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 2L))
        fake.emitRoomUpdate(ready3)

        assertEquals(listOf(3L), observedVersions, "Room UI must ignore older and duplicate authoritative versions")
        handle.close()
    }

    @Test
    fun testDelayedRoomQueryReturnsLatestAlreadyObservedVersion() {
        val fake = FakeServerConnection()
        val delayedQueryReply = CompletableFuture<ProtocolRoomInfo>()
        fake.customRoomInfoFuture = delayedQueryReply

        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val query = session.getRoomInfo()
        val ready2 = fake.defaultRoomInfo.copy(
            version = 2L,
            members = listOf(ProtocolRoomMember(0, "Host", true, true, "HUMAN"))
        )
        fake.emitRoomUpdate(ready2)

        delayedQueryReply.complete(fake.defaultRoomInfo) // version 1L
        val result = query.join()
        assertEquals(2L, result.version, "A delayed query must not return a superseded room view")
        assertTrue(result.members.first { it.seat == 0 }.isReady)
    }

    @Test
    fun testSuccessfulLeaveInvalidatesRoomReadsAndCapturedRoomCallbacks() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val received = mutableListOf<Long>()
        val handle = session.onRoomUpdate { received.add(it.version) }
        received.clear()

        val capturedCallbacks = fake.roomListeners.toList()
        val delayedRoomInfo = CompletableFuture<ProtocolRoomInfo>()
        fake.customRoomInfoFuture = delayedRoomInfo
        val query = session.getRoomInfo()

        val leaveReceipt = session.leaveRoom().join()
        assertEquals(ReceiptStatus.APPLIED, leaveReceipt.status)

        assertTrue(query.isCompletedExceptionally || query.isCancelled,
            "Leaving must terminate reads belonging to the retired room binding")

        delayedRoomInfo.complete(fake.defaultRoomInfo)
        capturedCallbacks.forEach { it(fake.defaultRoomInfo.copy(version = 2L)) }
        assertTrue(received.isEmpty(), "Old room must not notify an unbound lobby")
        handle.close()
    }

    @Test
    fun testRemovingRoomObserverSuppressesAlreadyQueuedUiCallback() {
        val fake = FakeServerConnection()
        val queue = java.util.ArrayDeque<() -> Unit>()
        fun drain() { while (queue.isNotEmpty()) queue.removeFirst().invoke() }
        val session = ServerConnectionSession(fake, dispatch = { queue.add(it) })
        try {
            val created = session.createRoom("Host", null)
            drain()
            created.join()

            val received = mutableListOf<String>()
            val observer = session.onRoomUpdate { received.add(it.roomId) }
            drain()
            received.clear()

            fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 2L))
            assertTrue(queue.isNotEmpty(), "A live observer update should be queued before unregister")
            observer.close()
            drain()
            assertTrue(received.isEmpty(), "Unregistered UI observer cannot receive queued room callbacks")
        } finally {
            session.close()
            drain()
        }
    }

    @Test
    fun testRoomUpdateIgnoredWhenUnboundOrRoomIdMismatch() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        val received = mutableListOf<Long>()
        session.onRoomUpdate { received.add(it.version) }

        // 1. Unbound state
        fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 2L))
        assertTrue(received.isEmpty(), "Unbound lobby must ignore room push")

        // 2. Bound state
        session.createRoom("Host", null).join()
        // Mismatched room id
        fake.emitRoomUpdate(fake.defaultRoomInfo.copy(roomId = "OTHER_ROOM", version = 2L))
        assertTrue(received.isEmpty(), "Mismatched roomId must be ignored")

        // Matching room id
        fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 2L))
        assertEquals(listOf(2L), received)
    }

    @Test
    fun testRoomSubscriptionSurvivesReconnectAndRejectsOldTransportCallbacks() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val firstConn = FakeServerConnection()
        val secondConn = FakeServerConnection()

        val session = ServerConnectionSession(
            initialConnection = firstConn,
            connectionFactory = { CompletableFuture.completedFuture(secondConn) },
            clock = clock,
            scheduler = scheduler
        )
        session.createRoom("Host", null).join()

        val received = mutableListOf<Boolean>()
        val handle = session.onRoomUpdate { received.add(it.members.first { m -> m.seat == 0 }.isReady) }
        val oldCallbacks = firstConn.roomListeners.toList()

        firstConn.emitDisconnect("HEARTBEAT_TIMEOUT")
        scheduler.advanceTime(500L)

        assertEquals(ConnectionState.CONNECTED, session.connectionState)
        assertTrue(secondConn.roomListeners.isNotEmpty(), "Reconnect must restore room subscription")

        received.clear()
        oldCallbacks.forEach { it(firstConn.defaultRoomInfo) }
        assertTrue(received.isEmpty(), "Retired transport must not notify room UI")

        val readyInfo = secondConn.defaultRoomInfo.copy(
            version = 2L,
            members = listOf(ProtocolRoomMember(0, "Host", true, true, "HUMAN"))
        )
        secondConn.emitRoomUpdate(readyInfo)
        assertEquals(listOf(true), received)
        handle.close()
    }

    @Test
    fun testLeavingAndCreatingNewRoomResetsRoomVersionCache() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val versions = mutableListOf<Long>()
        session.onRoomUpdate { versions.add(it.version) }

        fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 5L))
        assertEquals(listOf(5L), versions)

        // Leave room
        session.leaveRoom().join()
        versions.clear()

        // Create new room with lower version (e.g. version 2L)
        session.createRoom("Host", null).join()
        val newRoomInfo = fake.defaultRoomInfo.copy(version = 2L)
        fake.emitRoomUpdate(newRoomInfo)

        assertEquals(listOf(2L), versions, "New room binding must reset room version cache and accept version 2L")
    }

    @Test
    fun testRetiredRoomCallbackCannotAttachToNewBindingWithSameRoomId() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val retiredCallbacks = fake.roomListeners.toList()
        assertTrue(retiredCallbacks.isNotEmpty())

        session.leaveRoom().join()
        session.createRoom("Host", null).join()

        val versions = mutableListOf<Long>()
        val handle = session.onRoomUpdate { versions.add(it.version) }

        retiredCallbacks.forEach { it(fake.defaultRoomInfo.copy(version = 90L)) }
        assertTrue(versions.isEmpty(), "Retired room subscription adopted the replacement binding epoch")

        fake.roomListeners.toList().forEach { it(fake.defaultRoomInfo.copy(version = 2L)) }
        assertEquals(listOf(2L), versions, "Current room subscription must remain active")
        assertEquals(2L, session.getRoomInfo().join().version,
            "Retired room callback must not poison the replacement cache")
        handle.close()
    }

    @Test
    fun testRoomCallbackAlreadyInFlightCannotPoisonReplacementCache() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        session.createRoom("Host", null).join()

        val oldCallback = fake.roomListeners.single()
        val field = ServerConnectionSession::class.java.getDeclaredField("roomLock")
        field.isAccessible = true
        val cacheLock = field.get(session)
        val failure = AtomicReference<Throwable?>()

        val worker = Thread({
            try {
                oldCallback(fake.defaultRoomInfo.copy(version = 90L))
            } catch (t: Throwable) {
                failure.set(t)
            }
        }, "unit-test-retired-room-callback").also { it.isDaemon = true }

        try {
            synchronized(cacheLock) {
                worker.start()
                val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2)
                while (worker.state != Thread.State.BLOCKED && worker.isAlive && System.nanoTime() < deadline) {
                    Thread.yield()
                }
                assertEquals(Thread.State.BLOCKED, worker.state, "Callback never reached its cache commit")
                session.leaveRoom().join()
                session.createRoom("Host", null).join()
            }
        } finally {
            worker.join(2000)
        }

        assertFalse(worker.isAlive, "Retired callback failed to finish")
        failure.get()?.let { throw AssertionError("Callback failed unexpectedly", it) }

        fake.roomListeners.toList().forEach { it(fake.defaultRoomInfo.copy(version = 2L)) }
        assertEquals(2L, session.getRoomInfo().join().version,
            "A callback validated before leave committed into the replacement room cache")
    }

    @Test
    fun testRoomNotificationDispatchPreservesVersionMonotonicity() {
        val fake = FakeServerConnection()
        val queue = mutableListOf<() -> Unit>()
        val session = ServerConnectionSession(fake, dispatch = { queue.add(it) })
        session.createRoom("Host", null)
        while (queue.isNotEmpty()) queue.removeAt(0)()

        val versions = mutableListOf<Long>()
        session.onRoomUpdate { versions.add(it.version) }

        // Emit update v2, then v3
        fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 2L))
        fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 3L))

        // Two tasks are queued: task 0 (v2) and task 1 (v3)
        assertEquals(2, queue.size)

        // Drain task 1 (v3) first to simulate out-of-order dispatch
        queue.removeAt(1)()
        assertEquals(listOf(3L), versions)

        // Now drain task 0 (v2): should be suppressed because cache and observer have moved to 3L
        queue.removeAt(0)()
        assertEquals(listOf(3L), versions, "Stale update v2 must not be delivered after v3")
    }

    @Test
    fun testDelayedRoomQueryCannotPoisonReplacementRoomCache() {
        val fake = FakeServerConnection()
        val delayedQueryReply = CompletableFuture<ProtocolRoomInfo>()
        fake.customRoomInfoFuture = delayedQueryReply

        val session = ServerConnectionSession(fake)
        try {
            session.createRoom("Host", null).get(2, TimeUnit.SECONDS)

            val query = session.getRoomInfo()
            assertFalse(query.isDone)

            session.leaveRoom().get(2, TimeUnit.SECONDS)

            // Provide independent mock response for subsequent queries of the replacement room
            fake.customRoomInfoFuture = CompletableFuture.completedFuture(fake.defaultRoomInfo.copy(version = 1L))

            session.createRoom("Host", null).get(2, TimeUnit.SECONDS)

            // Complete the old query with stale high version
            delayedQueryReply.complete(fake.defaultRoomInfo.copy(version = 99L))

            assertTrue(query.isCompletedExceptionally, "Old room query must complete exceptionally after leave and replacement")

            fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 2L))
            assertEquals(2L, session.getRoomInfo().get(2, TimeUnit.SECONDS).version,
                "Delayed query must not poison the replacement room cache")
        } finally {
            session.close()
        }
    }

    @Test
    fun testRoomUpdateObserverInterleavedThreadExecutionNeverDeliversStaleAfterNew() {
        val fake = FakeServerConnection()
        val dispatchedTasks = CopyOnWriteArrayList<() -> Unit>()
        val session = ServerConnectionSession(fake, dispatch = { dispatchedTasks.add(it) })
        try {
            val createFuture = session.createRoom("Host", null)
            while (dispatchedTasks.isNotEmpty()) {
                dispatchedTasks.removeAt(0)()
            }
            createFuture.get(2, TimeUnit.SECONDS)
            while (dispatchedTasks.isNotEmpty()) {
                dispatchedTasks.removeAt(0)()
            }
            dispatchedTasks.clear()

            val deliveredVersions = CopyOnWriteArrayList<Long>()
            session.onRoomUpdate { info ->
                deliveredVersions.add(info.version)
            }

            fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 2L))
            fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = 3L))

            assertEquals(2, dispatchedTasks.size)
            val taskV2 = dispatchedTasks[0]
            val taskV3 = dispatchedTasks[1]

            val t1Started = CountDownLatch(1)
            val t2Finished = CountDownLatch(1)
            val threadError = AtomicReference<Throwable?>()

            val t1 = Thread({
                try {
                    t1Started.countDown()
                    assertTrue(t2Finished.await(2, TimeUnit.SECONDS), "t2Finished latch timed out")
                    taskV2()
                } catch (t: Throwable) {
                    threadError.compareAndSet(null, t)
                }
            }, "test-dispatch-v2")

            val t2 = Thread({
                try {
                    assertTrue(t1Started.await(2, TimeUnit.SECONDS), "t1Started latch timed out")
                    taskV3()
                    t2Finished.countDown()
                } catch (t: Throwable) {
                    threadError.compareAndSet(null, t)
                }
            }, "test-dispatch-v3")

            t1.start()
            t2.start()

            t1.join(2000)
            t2.join(2000)

            assertFalse(t1.isAlive, "Thread t1 failed to finish")
            assertFalse(t2.isAlive, "Thread t2 failed to finish")

            threadError.get()?.let { throw AssertionError("Subthread failed", it) }

            assertEquals(listOf(3L), deliveredVersions.toList(),
                "When v3 dispatch completes before v2 dispatch, stale v2 must never be delivered after v3")
        } finally {
            session.close()
        }
    }

    @Test
    fun testRoomUpdateObserverHighConcurrencyDispatchNeverDeliversBackwards() {
        val fake = FakeServerConnection()
        val pool = Executors.newFixedThreadPool(4)
        val session = ServerConnectionSession(fake, dispatch = { pool.execute(it) })
        try {
            session.createRoom("Host", null).get(2, TimeUnit.SECONDS)

            val deliveredVersions = CopyOnWriteArrayList<Long>()
            val v30Delivered = CountDownLatch(1)
            session.onRoomUpdate { info ->
                deliveredVersions.add(info.version)
                if (info.version == 30L) {
                    v30Delivered.countDown()
                }
            }

            for (v in 2L..30L) {
                fake.emitRoomUpdate(fake.defaultRoomInfo.copy(version = v))
                Thread.sleep(1)
            }

            assertTrue(v30Delivered.await(5, TimeUnit.SECONDS), "Timed out waiting for final version 30 to be delivered")

            pool.shutdown()
            assertTrue(pool.awaitTermination(2, TimeUnit.SECONDS), "Thread pool failed to terminate cleanly")

            val list = deliveredVersions.toList()
            assertTrue(list.isNotEmpty(), "At least one update should be delivered")
            assertEquals(30L, list.last(), "Final delivered version must be 30L")
            for (i in 0 until list.size - 1) {
                assertTrue(list[i] < list[i + 1],
                    "Delivered versions must be strictly monotonically increasing, saw ${list[i]} followed by ${list[i + 1]} in $list")
            }
        } finally {
            pool.shutdownNow()
            session.close()
        }
    }

    @Test
    fun testJoinSpectatorAndPublicSnapshot() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            val snapshot = session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            assertNotNull(snapshot)
            assertEquals("session-1", snapshot.sessionId)
            assertEquals(10L, snapshot.cursor)
            assertNotNull(session.currentPublicSnapshot)
            assertEquals(10L, session.currentPublicSnapshot?.cursor)
            assertNull(session.currentSnapshot, "Player snapshot must remain null when joined as spectator")

            val fetched = session.publicSnapshot().get(2, TimeUnit.SECONDS)
            assertEquals(10L, fetched.cursor)
        } finally {
            session.close()
        }
    }

    @Test
    fun testMutualExclusionSpectatorCannotCallPlayerApis() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)

            val snapEx = assertThrows(ExecutionException::class.java) {
                session.snapshot().get(2, TimeUnit.SECONDS)
            }
            assertTrue(snapEx.cause is IllegalStateException)

            val decEx = assertThrows(ExecutionException::class.java) {
                session.decision().get(2, TimeUnit.SECONDS)
            }
            assertTrue(decEx.cause is IllegalStateException)

            val readyEx = assertThrows(ExecutionException::class.java) {
                session.setReady(true).get(2, TimeUnit.SECONDS)
            }
            assertTrue(readyEx.cause is IllegalStateException)

            val startEx = assertThrows(ExecutionException::class.java) {
                session.start().get(2, TimeUnit.SECONDS)
            }
            assertTrue(startEx.cause is IllegalStateException)
        } finally {
            session.close()
        }
    }

    @Test
    fun testMutualExclusionPlayerCannotCallSpectatorApis() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.createRoom("Host", null).get(2, TimeUnit.SECONDS)

            val specSnapEx = assertThrows(ExecutionException::class.java) {
                session.publicSnapshot().get(2, TimeUnit.SECONDS)
            }
            assertTrue(specSnapEx.cause is IllegalStateException)

            val resumePubEx = assertThrows(ExecutionException::class.java) {
                val cp = PublicCheckpoint("session-1", "sync-1", 10L)
                session.resumePublic(cp).get(2, TimeUnit.SECONDS)
            }
            assertTrue(resumePubEx.cause is IllegalStateException)

            val queryEx = assertThrows(ExecutionException::class.java) {
                session.queryPublicRoundResult("round-1").get(2, TimeUnit.SECONDS)
            }
            assertTrue(queryEx.cause is IllegalStateException)
        } finally {
            session.close()
        }
    }

    @Test
    fun testPublicUpdateDeliveryAndGapRecovery() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)

            val deliveredUpdates = CopyOnWriteArrayList<PublicUpdateView>()
            session.onPublicUpdate { deliveredUpdates.add(it) }

            // 1. 严格连续更新 (fromCursor == 10, cursor = 11)
            val nextSnap = fake.createPublicSnapshot(cursor = 11L)
            val protoUpdate = ProtocolPublicUpdate(
                snapshot = nextSnap,
                fromCursor = 10L,
                visibleEvents = emptyList(),
                isFullSnapshot = false
            )
            fake.emitPublicUpdate(protoUpdate)

            assertEquals(1, deliveredUpdates.size)
            assertEquals(11L, deliveredUpdates[0].snapshot.cursor)
            assertEquals(11L, session.currentPublicSnapshot?.cursor)

            // 2. 乱序跳变更新 (fromCursor = 13, cursor = 14) -> 触发 gap 恢复
            val gapSnap = fake.createPublicSnapshot(cursor = 14L)
            val gapUpdate = ProtocolPublicUpdate(
                snapshot = gapSnap,
                fromCursor = 13L,
                visibleEvents = emptyList(),
                isFullSnapshot = false
            )
            fake.emitPublicUpdate(gapUpdate)

            // 验证触发了对账恢复
            assertNotNull(fake.lastResumePublicCp)
            assertEquals(11L, fake.lastResumePublicCp?.cursor)
        } finally {
            session.close()
        }
    }

    @Test
    fun testSpectatorReconnectOnUnexpectedDisconnect() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val fake1 = FakeServerConnection()
        val fake2 = FakeServerConnection()
        val factory = {
            CompletableFuture.completedFuture(fake2 as ServerConnection)
        }

        val session = ServerConnectionSession(
            initialConnection = fake1,
            connectionFactory = factory,
            clock = clock,
            scheduler = scheduler
        )
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            assertEquals(ConnectionState.CONNECTED, session.connectionState)

            // 发生意外掉线
            fake1.emitDisconnect("NETWORK_TIMEOUT")
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)

            // 前进时间触发重连
            scheduler.advanceTime(500L)

            assertEquals(ConnectionState.CONNECTED, session.connectionState)
            assertNotNull(fake2.lastResumeSpectatorReq, "Reconnect must invoke resumeSpectator, not player resumeSession")
            assertNull(fake2.lastResumeReq, "Must never invoke player resumeSession")
            assertEquals("spec-1", fake2.lastResumeSpectatorReq?.spectatorId)
            assertEquals("spec-token-1", fake2.lastResumeSpectatorReq?.resumeToken)
        } finally {
            session.close()
        }
    }

    @Test
    fun testSpectatorLeaveRoom() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            assertNotNull(session.currentSpectatorBinding)
            assertNotNull(session.currentPublicSnapshot)

            val receipt = session.leaveRoom().get(2, TimeUnit.SECONDS)
            assertEquals(ReceiptStatus.APPLIED, receipt.status)
            assertNull(session.currentSpectatorBinding)
            assertNull(session.currentPublicSnapshot)
        } finally {
            session.close()
        }
    }

    @Test
    fun testSpectatorReceivesRoomUpdates() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)

            val received = CopyOnWriteArrayList<RoomInfo>()
            session.onRoomUpdate { received.add(it) }

            val updatedRoom = fake.defaultRoomInfo.copy(version = 2L)
            fake.emitRoomUpdate(updatedRoom)

            assertEquals(1, received.size)
            assertEquals(2L, received[0].version)
        } finally {
            session.close()
        }
    }

    @Test
    fun testPublicGapReorderDeliversHeldAndSkippedUpdatesInOrderExactlyOnce() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)

            val delivered = CopyOnWriteArrayList<PublicUpdateView>()
            session.onPublicUpdate { delivered.add(it) }

            // 持有 cursor+1（fromCursor=10），先送达 cursor=12（fromCursor=11）
            fake.emitPublicUpdate(
                ProtocolPublicUpdate(fake.createPublicSnapshot(cursor = 12L), fromCursor = 11L, visibleEvents = emptyList())
            )

            // 12 未被应用，且触发以当前基线 10 的公开恢复
            assertEquals(0, delivered.size)
            assertEquals(10L, session.currentPublicSnapshot?.cursor)
            assertNotNull(fake.lastResumePublicCp)
            assertEquals(10L, fake.lastResumePublicCp?.cursor)

            // 释放 cursor=11：11 与缓冲中的 12 必须按序恰好各交付一次
            fake.emitPublicUpdate(
                ProtocolPublicUpdate(fake.createPublicSnapshot(cursor = 11L), fromCursor = 10L, visibleEvents = emptyList())
            )

            assertEquals(2, delivered.size)
            assertEquals(11L, delivered[0].snapshot.cursor)
            assertEquals(12L, delivered[1].snapshot.cursor)
            assertEquals(12L, session.currentPublicSnapshot?.cursor)
        } finally {
            session.close()
        }
    }

    @Test
    fun testEarlyPublicBufferOverflowTriggersExplicitRecoveryAfterBinding() {
        val fake = FakeServerConnection()
        val deferredJoin = CompletableFuture<SpectatorBinding>()
        fake.customJoinSpectatorFuture = deferredJoin
        val session = ServerConnectionSession(fake)
        try {
            val joinFuture = session.joinSpectator("ROOM1234", "Watcher")

            // 握手期间灌入超过有界早期缓冲（64）的更新：cursor 11..76
            for (c in 11L..76L) {
                fake.emitPublicUpdate(
                    ProtocolPublicUpdate(fake.createPublicSnapshot(cursor = c), fromCursor = c - 1L, visibleEvents = emptyList())
                )
            }

            fake.customJoinSpectatorFuture = null
            deferredJoin.complete(SpectatorBinding("ROOM1234", "spec-1", "spec-token-1", fake.createPublicSnapshot(cursor = 10L)))
            val snapshot = joinFuture.get(2, TimeUnit.SECONDS)

            // 前 64 条连续排水（11..74），溢出部分不得静默丢失：必须显式发起权威对账恢复
            assertEquals(74L, snapshot.cursor)
            assertEquals(74L, session.currentPublicSnapshot?.cursor)
            assertNotNull(fake.lastResumePublicCp)
            assertEquals(74L, fake.lastResumePublicCp?.cursor)
        } finally {
            session.close()
        }
    }

    @Test
    fun testStalePublicRecoveryCallbackCannotPoisonNewBindingOrClearNewResumingFlag() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)

            // 第一次缺口恢复：resumePublic 挂起
            val staleRecovery = CompletableFuture<ProtocolPublicSnapshot>()
            fake.customResumePublicFuture = staleRecovery
            fake.emitPublicUpdate(
                ProtocolPublicUpdate(fake.createPublicSnapshot(cursor = 12L), fromCursor = 11L, visibleEvents = emptyList())
            )
            val callsAfterFirstGap = fake.resumePublicCallCount
            assertTrue(callsAfterFirstGap >= 1)

            // 离房后同连接重新加入观战（房间代次与连接代次均变化）
            session.leaveRoom().get(2, TimeUnit.SECONDS)
            fake.customResumePublicFuture = null
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)

            // 常规更新推进到 11，再次缺口：新绑定的恢复在途
            fake.emitPublicUpdate(
                ProtocolPublicUpdate(fake.createPublicSnapshot(cursor = 11L), fromCursor = 10L, visibleEvents = emptyList())
            )
            val newRecovery = CompletableFuture<ProtocolPublicSnapshot>()
            fake.customResumePublicFuture = newRecovery
            fake.emitPublicUpdate(
                ProtocolPublicUpdate(fake.createPublicSnapshot(cursor = 13L), fromCursor = 12L, visibleEvents = emptyList())
            )
            val callsAfterNewGap = fake.resumePublicCallCount
            assertEquals(callsAfterFirstGap + 1, callsAfterNewGap)
            assertEquals(11L, session.currentPublicSnapshot?.cursor)

            // 旧恢复响应迟到（跨 leave/rejoin）：不得写回缓存，也不得清除新恢复在途标记
            staleRecovery.complete(fake.createPublicSnapshot(cursor = 99L))
            assertEquals(11L, session.currentPublicSnapshot?.cursor)
            assertNull(fake.lastResumePublicCp?.takeIf { it.cursor == 99L })

            // 新缺口到达时，新绑定的恢复仍在途：不得再次发起恢复（旧实现会因标记被清而重复发起）
            fake.emitPublicUpdate(
                ProtocolPublicUpdate(fake.createPublicSnapshot(cursor = 14L), fromCursor = 13L, visibleEvents = emptyList())
            )
            assertEquals(callsAfterNewGap, fake.resumePublicCallCount)

            // 新恢复响应到达：基线恢复到 13，并继续排水先前已缓冲的连续更新 14，收敛到 14
            newRecovery.complete(fake.createPublicSnapshot(cursor = 13L))
            assertEquals(14L, session.currentPublicSnapshot?.cursor)
        } finally {
            session.close()
        }
    }

    @Test
    fun testPublicSnapshotQueryAfterLeaveFailsAndDoesNotRefillCache() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            assertEquals(10L, session.currentPublicSnapshot?.cursor)

            val deferredQuery = CompletableFuture<ProtocolPublicSnapshot>()
            fake.customPublicSnapshotFuture = deferredQuery
            val query = session.publicSnapshot()
            assertFalse(query.isDone)

            session.leaveRoom().get(2, TimeUnit.SECONDS)
            assertNull(session.currentPublicSnapshot)

            // 离房后迟到的旧查询响应：Future 必须失败，缓存不得被回填
            deferredQuery.complete(fake.createPublicSnapshot(cursor = 99L))
            assertTrue(query.isCompletedExceptionally)
            assertNull(session.currentPublicSnapshot, "Stale public query must not refill the unbound cache")

            // 回大厅后可再次正常加入观战
            fake.customPublicSnapshotFuture = null
            val rejoined = session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            assertEquals(10L, rejoined.cursor)
            assertEquals(10L, session.currentPublicSnapshot?.cursor)
        } finally {
            session.close()
        }
    }

    @Test
    fun testSpectatorSurvivesTwoConsecutiveReconnects() {
        val clock = FakeUdpClock(1000L)
        val scheduler = FakeUdpScheduler(clock)
        val fake1 = FakeServerConnection()
        val fake2 = FakeServerConnection()
        val fake3 = FakeServerConnection()
        val conns = ArrayDeque(listOf(fake2, fake3))
        var current = fake1
        val factory = {
            current = conns.removeFirst()
            CompletableFuture.completedFuture(current as ServerConnection)
        }

        val session = ServerConnectionSession(
            initialConnection = fake1,
            connectionFactory = factory,
            clock = clock,
            scheduler = scheduler
        )
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            assertEquals(10L, session.currentPublicSnapshot?.cursor)

            // 第一次意外掉线 → fake2 恢复
            fake1.emitDisconnect("NETWORK_TIMEOUT")
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)
            scheduler.advanceTime(500L)
            assertEquals(ConnectionState.CONNECTED, session.connectionState)
            assertEquals("spec-1", fake2.lastResumeSpectatorReq?.spectatorId)
            assertEquals("spec-token-1", fake2.lastResumeSpectatorReq?.resumeToken)
            assertNull(fake2.lastResumeReq, "Spectator reconnect must never invoke player resumeSession")

            // 第二次意外掉线 → fake3 恢复，观战身份与令牌仍然可用
            fake2.emitDisconnect("HEARTBEAT_TIMEOUT")
            assertEquals(ConnectionState.RECONNECTING, session.connectionState)
            assertEquals(10L, session.currentPublicSnapshot?.cursor, "Public view must be preserved while reconnecting")
            scheduler.advanceTime(500L)
            assertEquals(ConnectionState.CONNECTED, session.connectionState)
            assertEquals("spec-1", fake3.lastResumeSpectatorReq?.spectatorId)
            assertEquals("spec-token-1", fake3.lastResumeSpectatorReq?.resumeToken)
            assertNotNull(session.currentSpectatorBinding)
            assertEquals(10L, session.currentPublicSnapshot?.cursor)

            // 恢复后的新连接上的公开推送仍可正常消费
            val delivered = CopyOnWriteArrayList<PublicUpdateView>()
            session.onPublicUpdate { delivered.add(it) }
            fake3.emitPublicUpdate(
                ProtocolPublicUpdate(fake3.createPublicSnapshot(cursor = 11L), fromCursor = 10L, visibleEvents = emptyList())
            )
            assertEquals(1, delivered.size)
            assertEquals(11L, session.currentPublicSnapshot?.cursor)
        } finally {
            session.close()
        }
    }

    private fun acrossRebinding(session: ServerConnectionSession, callback: () -> Unit) {
        val field = ServerConnectionSession::class.java.getDeclaredField("publicSyncLock")
        field.isAccessible = true
        val lock = field.get(session)
        val failure = java.util.concurrent.atomic.AtomicReference<Throwable?>()
        val worker = Thread({
            try { callback() } catch (t: Throwable) { failure.set(t) }
        }, "test-retired-public-commit").also { it.isDaemon = true }
        try {
            synchronized(lock) {
                worker.start()
                val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2)
                while (worker.state != Thread.State.BLOCKED && worker.isAlive && System.nanoTime() < deadline) Thread.yield()
                assertEquals(Thread.State.BLOCKED, worker.state, "Callback must reach its cache commit")
                session.leaveRoom().get(2, TimeUnit.SECONDS)
                session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            }
        } finally {
            worker.join(2000)
        }
        assertFalse(worker.isAlive, "Retired PUBLIC callback must finish")
        failure.get()?.let { throw AssertionError("Unexpected callback failure", it) }
    }

    @Test
    fun testPublicPushValidatedBeforeLeaveCannotCommitIntoReplacementBinding() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            assertEquals(10L, session.currentPublicSnapshot?.cursor)

            val update = ProtocolPublicUpdate(fake.createPublicSnapshot(cursor = 11L), fromCursor = 10L, visibleEvents = emptyList())
            acrossRebinding(session) {
                fake.emitPublicUpdate(update)
            }
            assertEquals(10L, session.currentPublicSnapshot?.cursor, "Old public push must not commit into replacement binding")
        } finally {
            session.close()
        }
    }

    @Test
    fun testSnapshotQueryValidatedBeforeLeaveCannotPoisonReplacementCache() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            assertEquals(10L, session.currentPublicSnapshot?.cursor)

            val deferredQuery = CompletableFuture<ProtocolPublicSnapshot>()
            fake.customPublicSnapshotFuture = deferredQuery
            val query = session.publicSnapshot()

            acrossRebinding(session) {
                deferredQuery.complete(fake.createPublicSnapshot(cursor = 99L))
            }
            assertEquals(10L, session.currentPublicSnapshot?.cursor, "Stale query response must not poison replacement cache")
            assertTrue(query.isCompletedExceptionally, "Stale query future must fail")
        } finally {
            session.close()
        }
    }

    @Test
    fun testExplicitResumeValidatedBeforeLeaveCannotPoisonReplacementCache() {
        val fake = FakeServerConnection()
        val session = ServerConnectionSession(fake)
        try {
            session.joinSpectator("ROOM1234", "Watcher").get(2, TimeUnit.SECONDS)
            assertEquals(10L, session.currentPublicSnapshot?.cursor)

            val deferredResume = CompletableFuture<ProtocolPublicSnapshot>()
            fake.customResumePublicFuture = deferredResume
            val resumeFut = session.resumePublic(com.majiang.client.api.PublicCheckpoint("session-pub-1", "epoch-1", 10L))

            acrossRebinding(session) {
                deferredResume.complete(fake.createPublicSnapshot(cursor = 99L))
            }
            assertEquals(10L, session.currentPublicSnapshot?.cursor, "Stale explicit resume response must not poison replacement cache")
            assertTrue(resumeFut.isCompletedExceptionally, "Stale resume future must fail")
        } finally {
            session.close()
        }
    }
}
