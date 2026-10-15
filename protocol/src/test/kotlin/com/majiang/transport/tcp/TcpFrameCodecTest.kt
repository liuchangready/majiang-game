package com.majiang.transport.tcp

import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.nio.ByteBuffer
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * TCP 二进制成帧编解码器单元测试 (对照 UC-F01 ~ UC-F10 规格)
 */
class TcpFrameCodecTest {

    // ============================================================================
    // UC-F01 逐字节切分
    // ============================================================================

    @Test
    fun `UC-F01 test 1-byte incremental feeding across all slice boundaries`() {
        val payload = "Hello-MJ-TCP-1-byte-feed-test".toByteArray(Charsets.UTF_8)
        val encoded = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_REQ, payload)
        assertEquals(TcpWire.HEADER_SIZE + payload.size, encoded.size)

        val decoder = TcpFrameDecoder()
        val allFrames = mutableListOf<TcpFrame>()

        // 每次喂入恰好 1 字节
        for (i in encoded.indices) {
            val chunk = byteArrayOf(encoded[i])
            val frames = decoder.decode(chunk)
            allFrames.addAll(frames)
        }

        assertEquals(1, allFrames.size, "Must produce exactly 1 frame after all bytes arrive")
        val frame = allFrames.first()
        assertEquals(TcpWire.FRAME_TYPE_REQ, frame.type)
        assertEquals(0.toShort(), frame.flags)
        assertEquals(TcpWire.WIRE_VERSION, frame.wireVersion)
        assertTrue(payload.contentEquals(frame.payload), "Decoded payload must match original byte-for-byte")
        assertFalse(decoder.hasIncompleteFrame(), "Decoder must not have pending incomplete frame")
    }

    // ============================================================================
    // UC-F02 混合半帧
    // ============================================================================

    @Test
    fun `UC-F02 test mixed partial frames across feed boundaries`() {
        val frame1 = TcpFrame(TcpWire.FRAME_TYPE_HELLO, "{\"wireVersion\":1}".toByteArray())
        val frame2 = TcpFrame(TcpWire.FRAME_TYPE_PING, "{\"nonce\":\"0123456789abcdef\"}".toByteArray())
        val frame3 = TcpFrame(TcpWire.FRAME_TYPE_PONG, "{\"nonce\":\"0123456789abcdef\"}".toByteArray())

        val enc1 = TcpFrameEncoder.encode(frame1)
        val enc2 = TcpFrameEncoder.encode(frame2)
        val enc3 = TcpFrameEncoder.encode(frame3)

        val decoder = TcpFrameDecoder()

        // 喂入 1：半个头 (enc1 的前 6 字节)
        val feed1 = enc1.copyOfRange(0, 6)
        val res1 = decoder.decode(feed1)
        assertTrue(res1.isEmpty(), "Partial header must not produce frames")
        assertTrue(decoder.hasIncompleteFrame())

        // 喂入 2：enc1 剩余字节 + 完整 enc2 + enc3 的前 14 字节 (完整头 12 字节 + 2 字节体)
        val feed2 = enc1.copyOfRange(6, enc1.size) + enc2 + enc3.copyOfRange(0, 14)
        val res2 = decoder.decode(feed2)
        assertEquals(2, res2.size, "Must produce frame1 and frame2 in order")
        assertEquals(frame1, res2[0])
        assertEquals(frame2, res2[1])
        assertTrue(decoder.hasIncompleteFrame(), "Decoder has partial frame3 pending")

        // 喂入 3：enc3 剩余体字节
        val feed3 = enc3.copyOfRange(14, enc3.size)
        val res3 = decoder.decode(feed3)
        assertEquals(1, res3.size, "Must produce frame3")
        assertEquals(frame3, res3[0])
        assertFalse(decoder.hasIncompleteFrame(), "All frames complete, no partial pending")
    }

    // ============================================================================
    // UC-F03 单次读多帧
    // ============================================================================

    @Test
    fun `UC-F03 test multiple diverse frames in single byte buffer`() {
        val largePayload = ByteArray(32 * 1024) { (it % 26 + 'a'.code).toByte() }
        val smallPayload = byteArrayOf('X'.code.toByte())
        val regularPayload = "regular-request-envelope".toByteArray()

        val f1 = TcpFrame(TcpWire.FRAME_TYPE_PLAYER_UPDATE, largePayload)
        val f2 = TcpFrame(TcpWire.FRAME_TYPE_PING, smallPayload)
        val f3 = TcpFrame(TcpWire.FRAME_TYPE_REQ, regularPayload)

        val allBytes = TcpFrameEncoder.encode(f1) + TcpFrameEncoder.encode(f2) + TcpFrameEncoder.encode(f3)
        val decoder = TcpFrameDecoder()
        val decoded = decoder.decode(allBytes)

        assertEquals(3, decoded.size)
        assertEquals(f1, decoded[0])
        assertEquals(f2, decoded[1])
        assertEquals(f3, decoded[2])
    }

    // ============================================================================
    // UC-F04 长度边界
    // ============================================================================

    @Test
    fun `UC-F04 test payload length boundaries 1, 1048576 valid, 0, 1048577, 0xFFFFFFFF invalid`() {
        val decoder = TcpFrameDecoder()

        // 边界 1：payloadLength = 1 合法
        val f1 = TcpFrame(TcpWire.FRAME_TYPE_PING, byteArrayOf(0x31))
        val dec1 = decoder.decode(TcpFrameEncoder.encode(f1))
        assertEquals(1, dec1.size)
        assertEquals(1, dec1[0].payload.size)

        // 边界 2：payloadLength = 1,048,576 (1 MiB) 合法
        val maxPayload = ByteArray(TcpWire.MAX_PAYLOAD_BYTES) { 0x41 }
        val fMax = TcpFrame(TcpWire.FRAME_TYPE_PLAYER_UPDATE, maxPayload)
        val decMax = decoder.decode(TcpFrameEncoder.encode(fMax))
        assertEquals(1, decMax.size)
        assertEquals(TcpWire.MAX_PAYLOAD_BYTES, decMax[0].payload.size)

        // 边界 3：payloadLength = 0 非法（在头部解析时拒绝，进入终态）
        val zeroLenHeader = ByteBuffer.allocate(12)
            .putInt(TcpWire.MAGIC_INT)
            .put(TcpWire.WIRE_VERSION.toByte())
            .put(TcpWire.FRAME_TYPE_REQ.toByte())
            .putShort(0)
            .putInt(0) // payloadLength = 0
            .array()

        val decZero = TcpFrameDecoder()
        val exZero = assertThrows<TcpProtocolException> {
            decZero.decode(zeroLenHeader)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, exZero.reasonCode)
        assertTrue(decZero.isFatalError)

        // 边界 4：payloadLength = 1,048,577 非法
        val overLenHeader = ByteBuffer.allocate(12)
            .putInt(TcpWire.MAGIC_INT)
            .put(TcpWire.WIRE_VERSION.toByte())
            .put(TcpWire.FRAME_TYPE_REQ.toByte())
            .putShort(0)
            .putInt(TcpWire.MAX_PAYLOAD_BYTES + 1)
            .array()

        val decOver = TcpFrameDecoder()
        val exOver = assertThrows<TcpProtocolException> {
            decOver.decode(overLenHeader)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, exOver.reasonCode)
        assertTrue(decOver.isFatalError)

        // 边界 5：payloadLength = 0xFFFFFFFF (-1 as Int, 4294967295L as unsigned Long) 非法
        val uintMaxHeader = ByteBuffer.allocate(12)
            .putInt(TcpWire.MAGIC_INT)
            .put(TcpWire.WIRE_VERSION.toByte())
            .put(TcpWire.FRAME_TYPE_REQ.toByte())
            .putShort(0)
            .putInt(-1) // 0xFFFFFFFF
            .array()

        val decUintMax = TcpFrameDecoder()
        val exUintMax = assertThrows<TcpProtocolException> {
            decUintMax.decode(uintMaxHeader)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, exUintMax.reasonCode)
        assertTrue(decUintMax.isFatalError)
        // 确认错误信息中体现了无符号数或越界值，没有因转 Int 逃逸检查
        assertTrue(exUintMax.message?.contains("4294967295") == true || exUintMax.message?.contains("bounds") == true)
    }

    // ============================================================================
    // UC-F05 错误即关闭 (6 类非法的确定性用例)
    // ============================================================================

    @Test
    fun `UC-F05 case 1 bad magic immediately enters fatal PROTOCOL_VIOLATION state`() {
        val badMagicHeader = ByteBuffer.allocate(12)
            .putInt(0x12345678) // 坏魔数
            .put(1.toByte())
            .put(1.toByte())
            .putShort(0)
            .putInt(10)
            .array()

        val decoder = TcpFrameDecoder()
        val ex = assertThrows<TcpProtocolException> {
            decoder.decode(badMagicHeader)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
        assertTrue(decoder.isFatalError)

        // 终态后后续输入不产出任何帧，不找回同步（decode 在终态直接抛协议异常）
        val validFrame = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PING, "ping".toByteArray())
        assertThrows<TcpProtocolException>("Decoder in fatal error state must not produce frames") {
            decoder.decode(validFrame)
        }
    }

    @Test
    fun `UC-F05 case 2 bad wireVersion immediately enters fatal VERSION_MISMATCH state`() {
        val badVersionHeader = ByteBuffer.allocate(12)
            .putInt(TcpWire.MAGIC_INT)
            .put(2.toByte()) // wireVersion = 2 不支持
            .put(1.toByte())
            .putShort(0)
            .putInt(10)
            .array()

        val decoder = TcpFrameDecoder()
        val ex = assertThrows<TcpProtocolException> {
            decoder.decode(badVersionHeader)
        }
        assertEquals(TcpCloseReasons.VERSION_MISMATCH, ex.reasonCode)
        assertTrue(decoder.isFatalError)
    }

    @Test
    fun `UC-F05 case 3 bad flags immediately enters fatal PROTOCOL_VIOLATION state`() {
        val badFlagsHeader = ByteBuffer.allocate(12)
            .putInt(TcpWire.MAGIC_INT)
            .put(1.toByte())
            .put(1.toByte())
            .putShort(1.toShort()) // flags != 0
            .putInt(10)
            .array()

        val decoder = TcpFrameDecoder()
        val ex = assertThrows<TcpProtocolException> {
            decoder.decode(badFlagsHeader)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
        assertTrue(decoder.isFatalError)
    }

    @Test
    fun `UC-F05 case 4 bad frame type 0 or 11 enters fatal PROTOCOL_VIOLATION state`() {
        val badTypeHeader0 = ByteBuffer.allocate(12)
            .putInt(TcpWire.MAGIC_INT)
            .put(1.toByte())
            .put(0.toByte()) // type = 0
            .putShort(0)
            .putInt(10)
            .array()

        val dec0 = TcpFrameDecoder()
        val ex0 = assertThrows<TcpProtocolException> { dec0.decode(badTypeHeader0) }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex0.reasonCode)

        val badTypeHeader11 = ByteBuffer.allocate(12)
            .putInt(TcpWire.MAGIC_INT)
            .put(1.toByte())
            .put(11.toByte()) // type = 11
            .putShort(0)
            .putInt(10)
            .array()

        val dec11 = TcpFrameDecoder()
        val ex11 = assertThrows<TcpProtocolException> { dec11.decode(badTypeHeader11) }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex11.reasonCode)
    }

    @Test
    fun `UC-F05 case 5 bad UTF-8 in payload enters fatal PROTOCOL_VIOLATION state`() {
        // 非法 UTF-8 字节：0xFF 0xFF
        val badUtf8Payload = byteArrayOf(0xFF.toByte(), 0xFF.toByte())
        val frameBytes = ByteBuffer.allocate(14)
            .putInt(TcpWire.MAGIC_INT)
            .put(TcpWire.WIRE_VERSION.toByte())
            .put(TcpWire.FRAME_TYPE_REQ.toByte())
            .putShort(0)
            .putInt(2)
            .put(badUtf8Payload)
            .array()

        val decoder = TcpFrameDecoder()
        val ex = assertThrows<TcpProtocolException> {
            decoder.decode(frameBytes)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
        assertTrue(decoder.isFatalError)
        assertTrue(ex.message?.contains("UTF-8") == true)
    }

    @Test
    fun `UC-F05 case 6 bad JSON in message parsing enters PROTOCOL_VIOLATION state`() {
        val badJsonPayload = "{invalid-json-content:".toByteArray()
        val frame = TcpFrame(TcpWire.FRAME_TYPE_HELLO, badJsonPayload)

        val ex = assertThrows<TcpProtocolException> {
            TcpMessageCodec.parseHello(frame)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
    }

    // ============================================================================
    // UC-F06 部分 EOF
    // ============================================================================

    @Test
    fun `UC-F06 partial EOF on incomplete header or body triggers fatal exception`() {
        // 情况 1：半头时流结束
        val dec1 = TcpFrameDecoder()
        dec1.decode(byteArrayOf(0x4D, 0x4A, 0x54)) // 3 字节半头
        val ex1 = assertThrows<TcpProtocolException> {
            dec1.onEndOfStream()
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex1.reasonCode)
        assertTrue(dec1.isFatalError)

        // 情况 2：半体时流结束
        val header = ByteBuffer.allocate(12)
            .putInt(TcpWire.MAGIC_INT)
            .put(TcpWire.WIRE_VERSION.toByte())
            .put(TcpWire.FRAME_TYPE_PING.toByte())
            .putShort(0)
            .putInt(10) // 声明 10 字节
            .array()

        val dec2 = TcpFrameDecoder()
        dec2.decode(header + byteArrayOf(1, 2, 3)) // 仅给 3 字节体
        val ex2 = assertThrows<TcpProtocolException> {
            dec2.onEndOfStream()
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex2.reasonCode)
        assertTrue(dec2.isFatalError)

        // 情况 3：干净帧边界结束（无异常）
        val dec3 = TcpFrameDecoder()
        val valid = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PING, "ping".toByteArray())
        dec3.decode(valid)
        dec3.onEndOfStream() // 干净结束，不应抛出异常
        assertFalse(dec3.isFatalError)
    }

    // ============================================================================
    // UC-F07 慢速灌入
    // ============================================================================

    @Test
    fun `UC-F07 test slow arrival in 20-byte increments completes successfully`() {
        val payload = ("chunked-stream-delivery-test-".repeat(10)).toByteArray(Charsets.UTF_8)
        val full = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_REQ, payload)

        val decoder = TcpFrameDecoder()
        val collected = mutableListOf<TcpFrame>()
        var offset = 0
        val chunkSize = 20

        while (offset < full.size) {
            val length = Math.min(chunkSize, full.size - offset)
            collected.addAll(decoder.decode(full, offset, length))
            offset += length
        }

        assertEquals(1, collected.size)
        assertEquals(TcpWire.FRAME_TYPE_REQ, collected[0].type)
        assertTrue(payload.contentEquals(collected[0].payload))
        assertFalse(decoder.hasIncompleteFrame())
    }

    // ============================================================================
    // UC-F08 缓冲有界
    // ============================================================================

    @Test
    fun `UC-F08 test decoder does not preallocate 1 MiB buffer on header arrival alone`() {
        // 构造声明 1 MiB 但只发送 12 字节头部的流
        val headerDeclaring1MiB = ByteBuffer.allocate(12)
            .putInt(TcpWire.MAGIC_INT)
            .put(TcpWire.WIRE_VERSION.toByte())
            .put(TcpWire.FRAME_TYPE_PLAYER_UPDATE.toByte())
            .putShort(0)
            .putInt(TcpWire.MAX_PAYLOAD_BYTES)
            .array()

        val decoder = TcpFrameDecoder()
        val frames = decoder.decode(headerDeclaring1MiB)
        assertTrue(frames.isEmpty())
        assertTrue(decoder.hasIncompleteFrame())
        assertFalse(decoder.isFatalError)

        // 仅喂入 10 字节的体
        val tinyBody = ByteArray(10) { 0x30 }
        val frames2 = decoder.decode(tinyBody)
        assertTrue(frames2.isEmpty())
        assertTrue(decoder.hasIncompleteFrame())
    }

    // ============================================================================
    // UC-F09 编码侧防护
    // ============================================================================

    @Test
    fun `UC-F09 encoder rejects out-of-bounds type, empty payload, and over-limit payload`() {
        // type 越界 (0 or 11)
        assertThrows<IllegalArgumentException> {
            TcpFrameEncoder.encode(0, "test".toByteArray())
        }
        assertThrows<IllegalArgumentException> {
            TcpFrameEncoder.encode(11, "test".toByteArray())
        }

        // 空载荷
        assertThrows<IllegalArgumentException> {
            TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PING, ByteArray(0))
        }

        // 超限载荷 (> 1 MiB)
        val overLimit = ByteArray(TcpWire.MAX_PAYLOAD_BYTES + 1)
        assertThrows<IllegalArgumentException> {
            TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PLAYER_UPDATE, overLimit)
        }

        // flags != 0
        assertThrows<IllegalArgumentException> {
            TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PING, "ping".toByteArray(), flags = 1)
        }
    }

    // ============================================================================
    // UC-F10 控制帧同路成帧
    // ============================================================================

    @Test
    fun `UC-F10 control frames PING PONG CLOSE follow identical framing path as data frames`() {
        val pingBytes = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PING, "{\"nonce\":\"0123456789abcdef\"}")
        val pongBytes = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PONG, "{\"nonce\":\"0123456789abcdef\"}")
        val closeBytes = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_CLOSE, "{\"reason\":\"SERVER_SHUTDOWN\"}")

        val frames = listOf(pingBytes, pongBytes, closeBytes)
        val expectedTypes = listOf(TcpWire.FRAME_TYPE_PING, TcpWire.FRAME_TYPE_PONG, TcpWire.FRAME_TYPE_CLOSE)

        for (i in frames.indices) {
            val bytes = frames[i]
            val buf = ByteBuffer.wrap(bytes)

            assertEquals(TcpWire.MAGIC_INT, buf.getInt())
            assertEquals(TcpWire.WIRE_VERSION.toByte(), buf.get())
            assertEquals(expectedTypes[i].toByte(), buf.get())
            assertEquals(0.toShort(), buf.getShort())
            val len = buf.getInt()
            assertTrue(len > 0)
            assertEquals(TcpWire.HEADER_SIZE + len, bytes.size)
        }
    }
}
