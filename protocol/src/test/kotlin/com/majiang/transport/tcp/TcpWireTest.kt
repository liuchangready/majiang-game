package com.majiang.transport.tcp

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * TCP 协议常量、关闭码与 ApiErrors 扩展单元测试 (契约 1, 契约 6, D3, D4)
 */
class TcpWireTest {

    @Test
    fun `test TcpWire constants align strictly with design and review contracts`() {
        assertEquals("MJTC", TcpWire.MAGIC_ASCII)
        assertEquals(0x4D4A5443, TcpWire.MAGIC_INT)
        assertTrue(
            byteArrayOf(0x4D, 0x4A, 0x54, 0x43).contentEquals(TcpWire.MAGIC_BYTES),
            "MAGIC_BYTES must match Big-Endian ASCII MJTC"
        )

        // MJ-TCP/1 帧格式不变；业务契约 v7 新增看破，线路版本仍为 1。
        assertEquals(1, TcpWire.WIRE_VERSION)
        assertEquals(7, TcpWire.BUSINESS_VERSION)
        assertEquals(7, ApiProtocol.PROTOCOL_VERSION)

        assertEquals(1, TcpWire.FRAME_TYPE_HELLO)
        assertEquals(2, TcpWire.FRAME_TYPE_WELCOME)
        assertEquals(3, TcpWire.FRAME_TYPE_REQ)
        assertEquals(4, TcpWire.FRAME_TYPE_RSP)
        assertEquals(5, TcpWire.FRAME_TYPE_PLAYER_UPDATE)
        assertEquals(6, TcpWire.FRAME_TYPE_PUBLIC_UPDATE)
        assertEquals(7, TcpWire.FRAME_TYPE_ROOM_UPDATE)
        assertEquals(8, TcpWire.FRAME_TYPE_PING)
        assertEquals(9, TcpWire.FRAME_TYPE_PONG)
        assertEquals(10, TcpWire.FRAME_TYPE_CLOSE)

        assertEquals(1..10, TcpWire.FRAME_TYPES)
        assertEquals(12, TcpWire.HEADER_SIZE)
        assertEquals(1, TcpWire.MIN_PAYLOAD_BYTES)
        assertEquals(1_048_576, TcpWire.MAX_PAYLOAD_BYTES)
        assertEquals(64 * 1024, TcpWire.DECODE_SCRATCH_LIMIT)
        assertEquals(0.toShort(), TcpWire.FLAGS_DEFAULT)

        for (t in 1..10) {
            assertTrue(TcpWire.isValidFrameType(t))
            assertFalse(TcpWire.frameTypeName(t).startsWith("UNKNOWN"))
        }
        assertFalse(TcpWire.isValidFrameType(0))
        assertFalse(TcpWire.isValidFrameType(11))
        assertTrue(TcpWire.frameTypeName(0).startsWith("UNKNOWN"))
    }

    @Test
    fun `test TcpCloseReasons classification and complete sets align with D4`() {
        // 复用 ApiProtocol 稳定码
        assertEquals(ApiProtocol.SERVER_SHUTDOWN, TcpCloseReasons.SERVER_SHUTDOWN)
        assertEquals(ApiProtocol.ROOM_CLOSED, TcpCloseReasons.ROOM_CLOSED)
        assertEquals(ApiProtocol.DISPLACED_BY_NEW_CONNECTION, TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION)
        assertEquals(ApiProtocol.LOCAL_CLOSED, TcpCloseReasons.LOCAL_CLOSED)
        assertEquals(ApiProtocol.HEARTBEAT_TIMEOUT, TcpCloseReasons.HEARTBEAT_TIMEOUT)

        // TCP 新增稳定码 (D4)
        assertEquals("VERSION_MISMATCH", TcpCloseReasons.VERSION_MISMATCH)
        assertEquals("PROTOCOL_VIOLATION", TcpCloseReasons.PROTOCOL_VIOLATION)
        assertEquals("HANDSHAKE_TIMEOUT", TcpCloseReasons.HANDSHAKE_TIMEOUT)
        assertEquals("SERVER_OVERLOADED", TcpCloseReasons.SERVER_OVERLOADED)
        assertEquals("SLOW_CONSUMER", TcpCloseReasons.SLOW_CONSUMER)

        // 集合全集与分类互斥性
        assertEquals(10, TcpCloseReasons.ALL.size)
        assertEquals(6, TcpCloseReasons.PERMANENT.size)
        assertEquals(4, TcpCloseReasons.TRANSIENT.size)

        // 永久分类 (6 项)
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.VERSION_MISMATCH))
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.PROTOCOL_VIOLATION))
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION))
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.ROOM_CLOSED))
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.SERVER_SHUTDOWN))
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.LOCAL_CLOSED))
        assertFalse(TcpCloseReasons.isPermanent(TcpCloseReasons.HEARTBEAT_TIMEOUT))

        // 临时分类 (4 项)
        assertTrue(TcpCloseReasons.isTransient(TcpCloseReasons.HANDSHAKE_TIMEOUT))
        assertTrue(TcpCloseReasons.isTransient(TcpCloseReasons.HEARTBEAT_TIMEOUT))
        assertTrue(TcpCloseReasons.isTransient(TcpCloseReasons.SERVER_OVERLOADED))
        assertTrue(TcpCloseReasons.isTransient(TcpCloseReasons.SLOW_CONSUMER))
        assertFalse(TcpCloseReasons.isTransient(TcpCloseReasons.SERVER_SHUTDOWN))
        assertFalse(TcpCloseReasons.isTransient(TcpCloseReasons.LOCAL_CLOSED))
        assertFalse(TcpCloseReasons.isTransient(TcpCloseReasons.PROTOCOL_VIOLATION))

        // 无交集且并集为全集
        assertTrue(TcpCloseReasons.PERMANENT.intersect(TcpCloseReasons.TRANSIENT).isEmpty())
        assertEquals(TcpCloseReasons.ALL, TcpCloseReasons.PERMANENT + TcpCloseReasons.TRANSIENT)
    }

    @Test
    fun `test ApiErrors REQUEST_SCOPE_EXPIRED is appended to ALL without relaxing existing set`() {
        assertEquals("REQUEST_SCOPE_EXPIRED", ApiErrors.REQUEST_SCOPE_EXPIRED)
        assertTrue(
            ApiErrors.ALL.contains(ApiErrors.REQUEST_SCOPE_EXPIRED),
            "REQUEST_SCOPE_EXPIRED must be included in ApiErrors.ALL (D3)"
        )
        // 既有 16 项 + 新增 1 项 = 17 项
        assertEquals(17, ApiErrors.ALL.size)

        // 验证原有错误码全部保留
        val expectedCodes = listOf(
            ApiErrors.INVALID_PROTOCOL_VERSION,
            ApiErrors.MALFORMED_REQUEST,
            ApiErrors.ALREADY_BOUND,
            ApiErrors.ROOM_NOT_FOUND,
            ApiErrors.ROOM_FULL,
            ApiErrors.SERVER_FULL,
            ApiErrors.MISSING_ROOM_ID,
            ApiErrors.MISSING_ROOM_OR_TOKEN,
            ApiErrors.MISSING_ACTION_CHOICE,
            ApiErrors.MISSING_SHUFFLE_PLAN,
            ApiErrors.UNAUTHENTICATED_SESSION,
            ApiErrors.INVALID_TOKEN,
            ApiErrors.UNKNOWN_REQUEST_TYPE,
            ApiErrors.CONFLICT,
            ApiErrors.SERVER_BUSY,
            ApiErrors.INTERNAL_ERROR,
            ApiErrors.REQUEST_SCOPE_EXPIRED
        )
        for (code in expectedCodes) {
            assertTrue(ApiErrors.ALL.contains(code), "ApiErrors.ALL must contain $code")
        }
    }
}
