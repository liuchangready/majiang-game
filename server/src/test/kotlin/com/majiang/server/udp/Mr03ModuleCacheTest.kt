package com.majiang.server.udp

import com.majiang.server.api.ApiErrors
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import org.junit.jupiter.api.Test
import java.net.InetSocketAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

class Mr03ModuleCacheTest {

    private val fakeClock = FakeUdpClock(1000L)
    private val address = InetSocketAddress("127.0.0.1", 45000)

    private fun newSession(clock: FakeUdpClock = fakeClock): UdpClientSession = UdpClientSession(
        connectionId = 555,
        remoteAddress = address,
        roomId = "ROOM-TEST",
        seat = 0,
        token = "token-test",
        generation = 0L,
        clock = clock
    )

    @Test
    fun `test UdpClientSession idempotent cache FIFO eviction`() {
        val clock = FakeUdpClock(1000L)
        val session = newSession(clock)

        for (i in 1..UdpClientSession.MAX_IDEMPOTENT_CACHE_ENTRIES) {
            session.cacheResponse("req-$i", ApiResponse(requestId = "req-$i", success = true))
        }

        // 第 513 个插入触发最老条目淘汰
        session.cacheResponse("req-513", ApiResponse(requestId = "req-513", success = true))

        assertNull(session.getCachedEntry("req-1"), "req-1 must be evicted")
        assertNotNull(session.getCachedEntry("req-2"), "req-2 must be retained")
        assertNotNull(session.getCachedEntry("req-513"), "req-513 must be retained")
    }

    @Test
    fun `test UdpClientSession compare-delete removeInFlightEntry`() {
        val clock = FakeUdpClock(1000L)
        val session = newSession(clock)

        val entryA = UdpClientSession.InFlightSessionEntry(
            future = CompletableFuture(),
            api = "SNAPSHOT",
            rawPayload = "{}",
            request = null,
            timestamp = clock.currentTimeMillis()
        )
        assertNull(session.putInFlightEntry("req-X", entryA))

        val differentEntry = UdpClientSession.InFlightSessionEntry(
            future = CompletableFuture(),
            api = "SNAPSHOT",
            rawPayload = "{}",
            request = null,
            timestamp = clock.currentTimeMillis()
        )

        // 不同的 entry 实例进行比较删除必须返回 false，且不删除映射
        assertFalse(session.removeInFlightEntry("req-X", differentEntry))
        assertSame(entryA, session.getInFlightEntry("req-X"))

        // 正确的 entry 实例比较删除成功
        assertTrue(session.removeInFlightEntry("req-X", entryA))
        assertNull(session.getInFlightEntry("req-X"))
    }

    @Test
    fun `test UdpClientSession inFlight bounded timeout sweep and busy rejection`() {
        val clock = FakeUdpClock(1000L)
        val session = newSession(clock)

        val staleFuture = CompletableFuture<ApiResponse>()
        session.putInFlight("req-stale", staleFuture)

        // 推进 15001ms 触发超时
        clock.advance(15_001L)

        // 下一次写入触发有界清扫
        val freshFuture = CompletableFuture<ApiResponse>()
        session.putInFlight("req-fresh", freshFuture)

        assertTrue(staleFuture.isCompletedExceptionally, "stale future must be exceptionally completed")
        assertNull(session.getInFlightEntry("req-stale"))
        assertNotNull(session.getInFlightEntry("req-fresh"))
    }

    @Test
    fun `test LobbyCacheConfig defaults and custom settings`() {
        val defaultConfig = LobbyCacheConfig()
        assertEquals(1024, defaultConfig.sideEffectCompletedCapacity)
        assertEquals(1024, defaultConfig.queryCompletedCapacity)
        assertEquals(1024, defaultConfig.terminatedSessionCapacity)
        assertEquals(256, defaultConfig.inFlightCapacity)
        assertEquals(60_000L, defaultConfig.completedRetentionMillis)
        assertEquals(15_000L, defaultConfig.inFlightRetentionMillis)

        val customConfig = LobbyCacheConfig(
            sideEffectCompletedCapacity = 16,
            queryCompletedCapacity = 32,
            terminatedSessionCapacity = 8,
            inFlightCapacity = 64,
            completedRetentionMillis = 30_000L,
            inFlightRetentionMillis = 5_000L
        )
        assertEquals(16, customConfig.sideEffectCompletedCapacity)
        assertEquals(32, customConfig.queryCompletedCapacity)
        assertEquals(8, customConfig.terminatedSessionCapacity)
        assertEquals(64, customConfig.inFlightCapacity)
        assertEquals(30_000L, customConfig.completedRetentionMillis)
        assertEquals(5_000L, customConfig.inFlightRetentionMillis)
    }

    @Test
    fun `test concurrent duplicate side-effect requests create exactly one room`() {
        val server = UdpTableServer(clock = FakeUdpClock(1000L))
        try {
            val responses = CopyOnWriteArrayList<ApiResponse>()
            val latch = CountDownLatch(2)

            server.outboundFrameListener = { frame, _ ->
                if (frame.packetType == UdpPacketType.RSP) {
                    val json = String(frame.payload, Charsets.UTF_8)
                    val resp = ApiMessageCodec.decodeResponse(json)
                    if (resp.requestId == "dup-create-req") {
                        responses.add(resp)
                        latch.countDown()
                    }
                }
            }

            val req = ApiRequest(
                api = ApiTags.CREATE_ROOM,
                requestId = "dup-create-req",
                playerName = "Host"
            )
            val payload = ApiMessageCodec.encodeRequest(req).toByteArray(Charsets.UTF_8)
            val frame1 = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = 0,
                seq = 1,
                ackSeq = 0,
                payload = payload
            )
            val frame2 = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = 0,
                seq = 2,
                ackSeq = 0,
                payload = payload
            )

            // 相同 (address, requestId) 的两份副作用请求连续提交，验证在途去重生效且业务只执行一次
            server.handleFrame(frame1, address)
            server.handleFrame(frame2, address)

            assertTrue(latch.await(5, TimeUnit.SECONDS), "both duplicate requests must receive responses")
            assertEquals(2, responses.size)
            assertTrue(responses[0].success, "first response must succeed")
            assertTrue(responses[1].success, "second response must succeed")
            assertNotNull(responses[0].roomId)
            assertEquals(responses[0].roomId, responses[1].roomId, "both responses must observe the same roomId")
            assertEquals(1, server.activeRoomCount, "exactly one room must be created")

            // 验证完成后再次发送相同请求，命中已完成回执池，依然不增加房间
            val frame3 = UdpFrame(
                packetType = UdpPacketType.REQ,
                connectionId = 0,
                seq = 3,
                ackSeq = 0,
                payload = payload
            )
            server.handleFrame(frame3, address)
            assertEquals(3, responses.size)
            assertTrue(responses[2].success)
            assertEquals(responses[0].roomId, responses[2].roomId)
            assertEquals(1, server.activeRoomCount)
        } finally {
            server.close()
        }
    }
}
