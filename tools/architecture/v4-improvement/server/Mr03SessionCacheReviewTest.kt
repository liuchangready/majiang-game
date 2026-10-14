package com.majiang.review

import com.majiang.server.api.ApiErrors
import com.majiang.server.udp.UdpClientSession
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.udp.UdpClock
import org.junit.jupiter.api.Test
import java.net.InetSocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ExecutionException
import kotlin.test.*

/**
 * MR-03 独立验收：UdpClientSession 幂等回执缓存与在途请求缓存的确定性淘汰契约。
 *
 * 契约（相对基线 8a4b59f 的变更）：
 * 1. idempotentCache（容量 512）淘汰为确定性 FIFO（插入顺序），不再依赖哈希顺序；
 *    过期清理（60s）优先于容量淘汰。
 * 2. 重复写同 ID 更新回执内容但保留原插入顺序与保留锚点，不延长保留窗口。
 * 3. inFlightRequests（容量 256）满时对第 257 个请求返回 SERVER_BUSY（既有行为保持）；
 *    15s 超时清扫有界（不得每次写入全表扫描）。
 * 4. 在途条目的回收/完成使用比较删除 removeInFlightEntry(requestId, entry)，
 *    迟到完成不得删除同 ID 的新条目。
 * 5. Future 完成在缓存锁外执行。
 */
class Mr03SessionCacheReviewTest {

    /** 确定性虚拟时钟。 */
    private class FakeClock(start: Long = 1000L) : UdpClock {
        private var now = start

        override fun currentTimeMillis(): Long = now

        fun advance(deltaMillis: Long) {
            require(deltaMillis >= 0L) { "deltaMillis must be >= 0" }
            now += deltaMillis
        }
    }

    private fun newSession(clock: FakeClock): UdpClientSession = UdpClientSession(
        connectionId = 7,
        remoteAddress = InetSocketAddress(java.net.InetAddress.getLoopbackAddress(), 5000),
        roomId = "room-1",
        seat = 0,
        token = "token-1",
        generation = 0,
        clock = clock
    )

    private fun response(id: String): ApiResponse = ApiResponse(requestId = id, success = true)

    @Test
    fun `513th cached response evicts oldest entry deterministically`() {
        val clock = FakeClock()
        val session = newSession(clock)

        for (i in 1..UdpClientSession.MAX_IDEMPOTENT_CACHE_ENTRIES) {
            session.cacheResponse("id-$i", response("id-$i"))
        }
        session.cacheResponse("id-513", response("id-513"))

        assertNull(session.getCachedEntry("id-1"), "oldest entry must be evicted at the 513th insert")
        assertNotNull(session.getCachedEntry("id-2"), "second-oldest entry must be retained")
        assertNotNull(session.getCachedEntry("id-513"), "newest entry must be retained")
    }

    @Test
    fun `cached entry expires after 60s retention window`() {
        val clock = FakeClock()
        val session = newSession(clock)

        session.cacheResponse("id-A", response("id-A"))
        assertNotNull(session.getCachedEntry("id-A"))

        clock.advance(60_001L)
        assertNull(session.getCachedEntry("id-A"), "entry past the 60s retention must not be served")
    }

    @Test
    fun `re-put same id updates response but keeps original retention anchor`() {
        val clock = FakeClock()
        val session = newSession(clock)

        session.cacheResponse("id-A", response("id-A"))
        clock.advance(50_000L)
        session.cacheResponse("id-A", ApiResponse(requestId = "id-A", success = false, error = "UPDATED"))

        // 保留锚点为首次写入 t0：t0+55s 时新回执内容可见
        clock.advance(5_000L)
        val entry = assertNotNull(session.getCachedEntry("id-A"), "within original window the entry must be served")
        assertEquals("UPDATED", entry.response.error, "re-put must update the cached response content")

        // t0+61s：即使中途重写过，也必须按首次写入时间过期
        clock.advance(6_000L)
        assertNull(
            session.getCachedEntry("id-A"),
            "re-put must not extend the retention window beyond the original insertion time"
        )
    }

    @Test
    fun `257th in-flight request is rejected with SERVER_BUSY`() {
        val clock = FakeClock()
        val session = newSession(clock)

        for (i in 1..UdpClientSession.MAX_IN_FLIGHT_ENTRIES) {
            val existing = session.putInFlight("id-$i", CompletableFuture())
            assertNull(existing, "entry $i within capacity must be accepted")
        }

        val busyFuture = session.putInFlight("id-257+", CompletableFuture())
        val future = assertNotNull(busyFuture, "over-capacity put must return the busy-completed future")
        assertTrue(future.isDone, "busy rejection must complete the future")
        val resp = future.getNow(null)
        assertNotNull(resp)
        assertEquals(false, resp.success)
        assertEquals(ApiErrors.SERVER_BUSY, resp.error)

        // 拒绝不驱逐既有在途条目
        assertNotNull(session.getInFlightEntry("id-1"), "busy rejection must not evict in-flight entries")
    }

    @Test
    fun `timed out in-flight entry is swept and completed exceptionally`() {
        val clock = FakeClock()
        val session = newSession(clock)

        val staleFuture = CompletableFuture<ApiResponse>()
        session.putInFlight("id-stale", staleFuture)
        clock.advance(15_001L)

        // 新写入触发有界清扫：超时条目被移除并异常完成
        session.putInFlight("id-fresh", CompletableFuture())
        assertTrue(staleFuture.isCompletedExceptionally, "timed-out in-flight future must complete exceptionally")
        assertNull(session.getInFlightEntry("id-stale"), "timed-out entry must be removed")
        assertNotNull(session.getInFlightEntry("id-fresh"))
    }

    @Test
    fun `compare-delete removes only the matching entry instance`() {
        val clock = FakeClock()
        val session = newSession(clock)

        val e1 = session.putInFlightEntry(
            "id-R",
            UdpClientSession.InFlightSessionEntry(CompletableFuture(), "SNAPSHOT", "{}", null, clock.currentTimeMillis())
        )
        assertNull(e1, "first put must be accepted")
        val original = assertNotNull(session.getInFlightEntry("id-R"))

        val wrongInstance = UdpClientSession.InFlightSessionEntry(
            CompletableFuture(), "SNAPSHOT", "{}", null, clock.currentTimeMillis()
        )
        assertFalse(
            session.removeInFlightEntry("id-R", wrongInstance),
            "compare-delete with a different instance must fail"
        )
        assertSame(original, session.getInFlightEntry("id-R"), "failed compare-delete must not remove the entry")

        assertTrue(
            session.removeInFlightEntry("id-R", original),
            "compare-delete with the mapped instance must succeed"
        )
        assertNull(session.getInFlightEntry("id-R"))
    }

    @Test
    fun `late completion cannot remove a replaced same-id entry`() {
        val clock = FakeClock()
        val session = newSession(clock)

        val staleFuture = CompletableFuture<ApiResponse>()
        session.putInFlight("id-R", staleFuture)
        val staleEntry = assertNotNull(session.getInFlightEntry("id-R"))

        clock.advance(15_001L)
        // 触发清扫移除旧条目
        session.putInFlight("id-other", CompletableFuture())

        // 同 ID 新条目被接受
        val renewedFuture = CompletableFuture<ApiResponse>()
        val prev = session.putInFlight("id-R", renewedFuture)
        assertNull(prev, "re-put after sweep must be accepted")
        val renewedEntry = assertNotNull(session.getInFlightEntry("id-R"))

        // 迟到的旧完成回调：比较删除必须失败，不得影响新条目
        assertFalse(
            session.removeInFlightEntry("id-R", staleEntry),
            "late completion of a swept entry must not delete the replacement"
        )
        assertSame(renewedEntry, session.getInFlightEntry("id-R"))
        assertFalse(renewedFuture.isDone, "replacement future must remain pending")
    }

    @Test
    fun `close completes in-flight futures exceptionally`() {
        val clock = FakeClock()
        val session = newSession(clock)

        val future = CompletableFuture<ApiResponse>()
        session.putInFlight("id-close", future)
        session.close()
        assertTrue(future.isCompletedExceptionally, "close must fail in-flight futures")
    }
}
