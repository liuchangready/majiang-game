package com.majiang.testing

import com.badlogic.gdx.Gdx
import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.*
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/**
 * 纯 client.api 观战会话测试双工 (FakeSpectatorSession)。
 *
 * 遵循 V4 契约与独立观战规范：
 * 1. 严格只依赖 client.api 视图与模型，杜绝 import engine、protocol 或 server；
 * 2. 独立只读公开流：仅消费 [PublicSnapshotView] 与 [PublicUpdateView]；
 * 3. 记录 [leaveRoom] 离房调用；
 * 4. 支持 [update] 与 [flushUi]。
 */
class FakeSpectatorSession(
    initialSnapshot: PublicSnapshotView? = null
) : SpectatorSession {

    private var _snapshot: PublicSnapshotView = initialSnapshot ?: TestViewBuilder.publicSnapshotView()
    private val publicListeners = mutableListOf<(PublicUpdateView) -> Unit>()
    private val disconnectListeners = mutableListOf<(String) -> Unit>()
    private val stateListeners = mutableListOf<(ConnectionState) -> Unit>()
    private val roomListeners = mutableListOf<(RoomInfo) -> Unit>()

    override var closed: Boolean = false
    override var disconnectReason: String? = null
    override var connectionState: ConnectionState = ConnectionState.CONNECTED

    override val currentPublicSnapshot: PublicSnapshotView get() = _snapshot

    var leaveRoomCalls: Int = 0

    override fun onStateChange(listener: (ConnectionState) -> Unit): AutoCloseable {
        stateListeners.add(listener)
        return AutoCloseable { stateListeners.remove(listener) }
    }

    override fun publicSnapshot(): CompletableFuture<PublicSnapshotView> {
        return CompletableFuture.completedFuture(_snapshot)
    }

    override fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<PublicSnapshotView> {
        return CompletableFuture.completedFuture(_snapshot)
    }

    override fun queryPublicRoundResult(roundId: String): CompletableFuture<List<GameEventView>> {
        return CompletableFuture.completedFuture(_snapshot.settlementEvents)
    }

    override fun onPublicUpdate(listener: (PublicUpdateView) -> Unit): AutoCloseable {
        publicListeners.add(listener)
        return AutoCloseable { publicListeners.remove(listener) }
    }

    override fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable {
        disconnectListeners.add(listener)
        return AutoCloseable { disconnectListeners.remove(listener) }
    }

    override fun getRoomInfo(): CompletableFuture<RoomInfo> {
        val info = RoomInfo(
            roomId = _snapshot.sessionId,
            lifecycle = RoomLifecycle.PLAYING,
            members = emptyList(),
            config = RoomConfig(),
            version = 1L
        )
        return CompletableFuture.completedFuture(info)
    }

    override fun onRoomUpdate(listener: (RoomInfo) -> Unit): AutoCloseable {
        roomListeners.add(listener)
        return AutoCloseable { roomListeners.remove(listener) }
    }

    override fun leaveRoom(): CompletableFuture<Receipt> {
        leaveRoomCalls++
        return CompletableFuture.completedFuture(Receipt("leave", ReceiptStatus.APPLIED))
    }

    override fun close() {
        closed = true
        publicListeners.clear()
        disconnectListeners.clear()
        stateListeners.clear()
        roomListeners.clear()
    }

    fun update(snapshot: PublicSnapshotView, events: List<GameEventView> = emptyList()) {
        _snapshot = snapshot
        val update = PublicUpdateView(
            snapshot = snapshot,
            fromCursor = snapshot.cursor - 1,
            visibleEvents = events,
            isFullSnapshot = false
        )
        publicListeners.toList().forEach { it(update) }
        flushUi()
    }

    fun flushUi() {
        val app = Gdx.app ?: return
        if (GdxHeadlessTestRunner.isUiThread()) return
        val done = CompletableFuture<Unit>()
        app.postRunnable {
            app.postRunnable {
                done.complete(Unit)
            }
        }
        done.get(3, TimeUnit.SECONDS)
    }
}
