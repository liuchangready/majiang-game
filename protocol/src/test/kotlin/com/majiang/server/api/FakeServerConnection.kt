package com.majiang.server.api

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
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolSuit
import com.majiang.transport.api.ProtocolTile
import java.util.Collections
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList

/**
 * 内存假实现 (FakeServerConnection)
 *
 * 实现了 [ServerConnection] 接口，用于测试，纯使用 Protocol DTOs。
 */
class FakeServerConnection(
    initialClosed: Boolean = false,
    initialDisconnectReason: String? = null
) : ServerConnection {

    private val updateListeners = CopyOnWriteArrayList<(ProtocolPlayerUpdate) -> Unit>()
    private val disconnectListeners = CopyOnWriteArrayList<(String) -> Unit>()
    private val roomUpdateListeners = CopyOnWriteArrayList<(com.majiang.transport.api.ProtocolRoomInfo) -> Unit>()
    private val publicUpdateListeners = CopyOnWriteArrayList<(ProtocolPublicUpdate) -> Unit>()

    @Volatile
    private var _closed: Boolean = initialClosed

    @Volatile
    private var _disconnectReason: String? = initialDisconnectReason

    override val closed: Boolean get() = _closed
    override val disconnectReason: String? get() = _disconnectReason

    // === 记录调用以便断言 ===
    val submittedChoices = CopyOnWriteArrayList<ProtocolActionChoice>()
    val submittedShufflePlans = CopyOnWriteArrayList<ProtocolShufflePlan>()
    var lastClaimed: Boolean? = null
    val submittedJoinSpectatorRequests = CopyOnWriteArrayList<JoinSpectatorRequest>()
    val submittedResumeSpectatorRequests = CopyOnWriteArrayList<ResumeSpectatorRequest>()
    val submittedPublicCheckpoints = CopyOnWriteArrayList<PublicCheckpoint>()
    val queriedPublicRoundIds = CopyOnWriteArrayList<String>()

    // === 可配置的响应结果 ===
    var capabilitiesResult: ProtocolAccessCapabilities = ProtocolAccessCapabilities(protocolVersion = ApiProtocol.PROTOCOL_VERSION)
    var roomsList: List<RoomSummary> = emptyList()
    var createRoomResult: RoomBinding? = null
    var joinRoomResult: RoomBinding? = null
    var resumeSessionResult: RoomBinding? = null
    var leaveRoomResult: ProtocolActionReceipt = ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)

    var roomInfoSupplier: () -> com.majiang.transport.api.ProtocolRoomInfo = { createDefaultRoomInfo() }
    var readyResult: ProtocolActionReceipt = ProtocolActionReceipt("setReady", ProtocolReceiptStatus.APPLIED)
    var updateConfigResult: ProtocolActionReceipt = ProtocolActionReceipt("updateConfig", ProtocolReceiptStatus.APPLIED)

    var snapshotSupplier: () -> ProtocolPlayerSnapshot = { createDefaultSnapshot() }
    var decisionSupplier: () -> ProtocolPlayerDecision = {
        ProtocolPlayerDecision("round-1", "decision-0", 1L, emptyList())
    }
    var roundResultSupplier: (String) -> List<ProtocolGameEvent> = { emptyList() }

    var joinSpectatorResult: SpectatorBinding? = null
    var resumeSpectatorResult: SpectatorBinding? = null
    var publicSnapshotSupplier: () -> ProtocolPublicSnapshot = { createDefaultPublicSnapshot() }
    var publicRoundResultSupplier: (String) -> List<ProtocolGameEvent> = { emptyList() }

    // === LobbyApi ===

    override fun listRooms(): CompletableFuture<List<RoomSummary>> {
        return CompletableFuture.completedFuture(Collections.unmodifiableList(roomsList))
    }

    override fun createRoom(req: CreateRoomRequest): CompletableFuture<RoomBinding> {
        val result = createRoomResult ?: RoomBinding(
            roomId = "ROOM0001",
            seat = 0,
            resumeToken = "token-host",
            snapshot = snapshotSupplier()
        )
        return CompletableFuture.completedFuture(result)
    }

    override fun joinRoom(req: JoinRoomRequest): CompletableFuture<RoomBinding> {
        val result = joinRoomResult ?: RoomBinding(
            roomId = req.roomId,
            seat = req.preferSeat ?: 1,
            resumeToken = "token-player",
            snapshot = snapshotSupplier()
        )
        return CompletableFuture.completedFuture(result)
    }

    override fun resumeSession(req: ResumeRequest): CompletableFuture<RoomBinding> {
        val result = resumeSessionResult ?: RoomBinding(
            roomId = req.roomId,
            seat = 0,
            resumeToken = req.token,
            snapshot = snapshotSupplier()
        )
        return CompletableFuture.completedFuture(result)
    }

    override fun leaveRoom(): CompletableFuture<ProtocolActionReceipt> {
        return CompletableFuture.completedFuture(leaveRoomResult)
    }

    override fun getRoomInfo(): CompletableFuture<com.majiang.transport.api.ProtocolRoomInfo> {
        return CompletableFuture.completedFuture(roomInfoSupplier())
    }

    override fun setReady(ready: Boolean): CompletableFuture<ProtocolActionReceipt> {
        return CompletableFuture.completedFuture(readyResult)
    }

    override fun updateConfig(config: com.majiang.transport.api.ProtocolRoomConfig): CompletableFuture<ProtocolActionReceipt> {
        return CompletableFuture.completedFuture(updateConfigResult)
    }

    override fun onRoomUpdate(listener: (com.majiang.transport.api.ProtocolRoomInfo) -> Unit): AutoCloseable {
        roomUpdateListeners.add(listener)
        return AutoCloseable { roomUpdateListeners.remove(listener) }
    }

    // === SessionApi ===

    override fun capabilities(): CompletableFuture<ProtocolAccessCapabilities> {
        return CompletableFuture.completedFuture(capabilitiesResult)
    }

    override fun snapshot(): CompletableFuture<ProtocolPlayerSnapshot> {
        return CompletableFuture.completedFuture(snapshotSupplier())
    }

    override fun decision(): CompletableFuture<ProtocolPlayerDecision> {
        return CompletableFuture.completedFuture(decisionSupplier())
    }

    override fun submit(choice: ProtocolActionChoice): CompletableFuture<ProtocolActionReceipt> {
        submittedChoices.add(choice)
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(requestId = choice.requestId, status = ProtocolReceiptStatus.APPLIED, cursor = 1L)
        )
    }

    override fun submitShufflePlan(plan: ProtocolShufflePlan): CompletableFuture<ProtocolActionReceipt> {
        submittedShufflePlans.add(plan)
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(requestId = plan.requestId, status = ProtocolReceiptStatus.RECORDED)
        )
    }

    override fun start(): CompletableFuture<ProtocolActionReceipt> {
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(requestId = "start", status = ProtocolReceiptStatus.APPLIED)
        )
    }

    override fun nextRound(): CompletableFuture<ProtocolActionReceipt> {
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(requestId = "nextRound", status = ProtocolReceiptStatus.APPLIED)
        )
    }

    override fun resume(cursor: Long): CompletableFuture<ProtocolPlayerSnapshot> {
        return CompletableFuture.completedFuture(snapshotSupplier())
    }

    override fun claimControl(): CompletableFuture<ProtocolActionReceipt> {
        lastClaimed = true
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(requestId = "claimControl", status = ProtocolReceiptStatus.APPLIED)
        )
    }

    override fun releaseControl(): CompletableFuture<ProtocolActionReceipt> {
        lastClaimed = false
        return CompletableFuture.completedFuture(
            ProtocolActionReceipt(requestId = "releaseControl", status = ProtocolReceiptStatus.APPLIED)
        )
    }

    override fun queryRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> {
        return CompletableFuture.completedFuture(
            Collections.unmodifiableList(roundResultSupplier(roundId))
        )
    }

    // === PushChannel ===

    override fun onUpdate(listener: (ProtocolPlayerUpdate) -> Unit): AutoCloseable {
        updateListeners.add(listener)
        return AutoCloseable { updateListeners.remove(listener) }
    }

    override fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable {
        disconnectListeners.add(listener)
        return AutoCloseable { disconnectListeners.remove(listener) }
    }

    // === SpectatorApi ===

    override fun joinSpectator(req: JoinSpectatorRequest): CompletableFuture<SpectatorBinding> {
        submittedJoinSpectatorRequests.add(req)
        val result = joinSpectatorResult ?: SpectatorBinding(
            roomId = req.roomId,
            spectatorId = "spectator-1",
            resumeToken = "token-spectator",
            snapshot = publicSnapshotSupplier()
        )
        return CompletableFuture.completedFuture(result)
    }

    override fun resumeSpectator(req: ResumeSpectatorRequest): CompletableFuture<SpectatorBinding> {
        submittedResumeSpectatorRequests.add(req)
        val result = resumeSpectatorResult ?: SpectatorBinding(
            roomId = req.roomId,
            spectatorId = req.spectatorId,
            resumeToken = req.resumeToken,
            snapshot = publicSnapshotSupplier()
        )
        return CompletableFuture.completedFuture(result)
    }

    override fun publicSnapshot(): CompletableFuture<ProtocolPublicSnapshot> {
        return CompletableFuture.completedFuture(publicSnapshotSupplier())
    }

    override fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<ProtocolPublicSnapshot> {
        submittedPublicCheckpoints.add(checkpoint)
        return CompletableFuture.completedFuture(publicSnapshotSupplier())
    }

    override fun queryPublicRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>> {
        queriedPublicRoundIds.add(roundId)
        return CompletableFuture.completedFuture(
            Collections.unmodifiableList(publicRoundResultSupplier(roundId))
        )
    }

    override fun onPublicUpdate(listener: (ProtocolPublicUpdate) -> Unit): AutoCloseable {
        publicUpdateListeners.add(listener)
        return AutoCloseable { publicUpdateListeners.remove(listener) }
    }

    fun emitUpdate(update: ProtocolPlayerUpdate) {
        for (l in updateListeners) {
            l(update)
        }
    }

    fun emitDisconnect(reason: String) {
        _closed = true
        _disconnectReason = reason
        for (l in disconnectListeners) {
            l(reason)
        }
    }

    fun emitRoomUpdate(info: com.majiang.transport.api.ProtocolRoomInfo) {
        for (l in roomUpdateListeners) {
            l(info)
        }
    }

    fun emitPublicUpdate(update: ProtocolPublicUpdate) {
        for (l in publicUpdateListeners) {
            l(update)
        }
    }

    override fun close() {
        if (_closed) return
        _closed = true
        if (_disconnectReason == null) {
            _disconnectReason = ApiProtocol.LOCAL_CLOSED
        }
        val reason = _disconnectReason!!
        for (l in disconnectListeners) {
            l(reason)
        }
        updateListeners.clear()
        disconnectListeners.clear()
        roomUpdateListeners.clear()
        publicUpdateListeners.clear()
    }

    companion object {
        fun createDefaultRoomInfo(
            roomId: String = "ROOM0001",
            lifecycle: com.majiang.transport.api.ProtocolRoomLifecycle = com.majiang.transport.api.ProtocolRoomLifecycle.LOBBY,
            version: Long = 1L,
            hostSeat: Int = 0
        ): com.majiang.transport.api.ProtocolRoomInfo {
            return com.majiang.transport.api.ProtocolRoomInfo(
                roomId = roomId,
                lifecycle = lifecycle,
                version = version,
                hostSeat = hostSeat,
                members = listOf(
                    com.majiang.transport.api.ProtocolRoomMember(
                        seat = 0,
                        playerName = "Host",
                        isHost = true,
                        isReady = false,
                        role = "HUMAN"
                    )
                ),
                config = com.majiang.transport.api.ProtocolRoomConfig()
            )
        }
        fun createDefaultSnapshot(
            sessionId: String = "session-fake",
            roundId: String? = "round-1",
            cursor: Long = 0L,
            seat: Int = 0
        ): ProtocolPlayerSnapshot {
            val tile = ProtocolTile(0, ProtocolSuit.MAN, 1)
            val view = ProtocolPlayerView(
                viewerSeat = seat,
                phase = ProtocolGamePhase.DISCARD,
                roundWind = 1,
                roundNumber = 0,
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
                handTileCounts = listOf(13, 13, 13, 13),
                myClosedTiles = listOf(tile),
                myDrawnTile = null,
                legalActions = listOf(ProtocolLegalAction.DiscardTile(seat, tile))
            )
            val decision = ProtocolPlayerDecision(
                roundId = roundId,
                decisionId = "decision-$cursor",
                controlEpoch = 1L,
                options = listOf(ProtocolActionOption("opt-1", ProtocolLegalAction.DiscardTile(seat, tile)))
            )
            return ProtocolPlayerSnapshot(
                sessionId = sessionId,
                roundId = roundId,
                syncEpoch = "epoch-1",
                cursor = cursor,
                sessionVersion = 0L,
                roundVersion = if (roundId != null) 0L else null,
                view = view,
                decision = decision
            )
        }

        fun createDefaultPublicBoardView(
            phase: ProtocolGamePhase = ProtocolGamePhase.DISCARD,
            roundWind: Int = 1,
            roundNumber: Int = 0,
            honbaSticks: Int = 0,
            riichiSticks: Int = 0,
            dealerSeat: Int = 0,
            currentTurnPlayer: Int = 0,
            wallRemaining: Int = 70
        ): ProtocolPublicBoardView {
            val tile = ProtocolTile(0, ProtocolSuit.MAN, 1)
            return ProtocolPublicBoardView(
                phase = phase,
                roundWind = roundWind,
                roundNumber = roundNumber,
                honbaSticks = honbaSticks,
                riichiSticks = riichiSticks,
                dealerSeat = dealerSeat,
                currentTurnPlayer = currentTurnPlayer,
                wallRemaining = wallRemaining,
                doraIndicators = listOf(tile),
                scores = listOf(25000, 25000, 25000, 25000),
                riichiStatus = listOf(false, false, false, false),
                discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
                handTileCounts = listOf(13, 13, 13, 13),
                lastDiscard = null,
                lastDiscardPlayer = null
            )
        }

        fun createDefaultPublicSessionSummary(
            lifecycle: String = "ROUND_ACTIVE",
            scores: List<Int> = listOf(25000, 25000, 25000, 25000),
            roundWind: Int = 1,
            roundNumber: Int = 0,
            dealerSeat: Int = 0,
            honba: Int = 0
        ): ProtocolPublicSessionSummary {
            return ProtocolPublicSessionSummary(
                lifecycle = lifecycle,
                scores = scores,
                roundWind = roundWind,
                roundNumber = roundNumber,
                dealerSeat = dealerSeat,
                honba = honba,
                roundRecords = emptyList(),
                finalResult = null
            )
        }

        fun createDefaultPublicSnapshot(
            sessionId: String = "session-public-fake",
            roundId: String? = "round-1",
            cursor: Long = 0L,
            syncEpoch: String = "epoch-1",
            sessionVersion: Long = 0L,
            roundVersion: Long? = if (roundId != null) 0L else null,
            board: ProtocolPublicBoardView? = createDefaultPublicBoardView()
        ): ProtocolPublicSnapshot {
            return ProtocolPublicSnapshot(
                sessionId = sessionId,
                sessionVersion = sessionVersion,
                roundId = roundId,
                roundVersion = roundVersion,
                syncEpoch = syncEpoch,
                cursor = cursor,
                board = board,
                session = createDefaultPublicSessionSummary(),
                clocks = emptyList(),
                paused = false,
                settlementRoundId = null,
                settlementEvents = emptyList()
            )
        }
    }
}
