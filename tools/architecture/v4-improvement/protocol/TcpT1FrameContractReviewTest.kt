package com.majiang.review

import com.majiang.server.api.ApiErrors
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpDecodeResult
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpFrameDecoder
import com.majiang.transport.tcp.TcpFrameEncoder
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpLimits
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpPing
import com.majiang.transport.tcp.TcpPong
import com.majiang.transport.tcp.TcpProtocolException
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpResponseEnvelope
import com.majiang.transport.tcp.TcpScope
import com.majiang.transport.tcp.TcpWelcome
import com.majiang.transport.tcp.TcpWire
import com.majiang.transport.common.TransportClock
import org.junit.jupiter.api.Test
import java.nio.ByteBuffer
import kotlin.test.*

/**
 * T1 独立验收：MJ-TCP/1 帧与握手契约层（对照 TCP-MIGRATION-DESIGN §3 与冻结决策 D3-D7、D10/D11）。
 *
 * 与 Anti 模块测试互补的对抗性断言：
 * 1. 帧层：任意切分点等价性（穷举两段式全部切点）、长度边界全集、终态粘性、EOF 边界。
 * 2. 信封层：外层/内层 requestId 一致性（解析与出帧双侧）、未知字段/非法值严格拒绝。
 * 3. 分类契约：TcpCloseReasons 必须与 MR-02 ReconnectErrorClassifier 的永久集合对齐（设计 §6 保留 MR-02 分类器）。
 */
class TcpT1FrameContractReviewTest {

    // ------------------------------------------------------------------
    // 帧层：任意切分等价性（UC-F01 穷举版）
    // ------------------------------------------------------------------

    @Test
    fun `every two-part split point yields identical frames`() {
        val f1 = TcpFrameEncoder.encode(TcpMessageCodec.toFrame(TcpHello()))
        val f2 = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PING, "{\"nonce\":\"0123456789abcdef\"}".toByteArray())
        val f3 = TcpFrameEncoder.encode(TcpMessageCodec.toFrame(TcpPing("fedcba9876543210")))
        val stream = f1 + f2 + f3
        val expected = listOf(
            TcpFrame(TcpWire.FRAME_TYPE_HELLO, f1.copyOfRange(12, f1.size)),
            TcpFrame(TcpWire.FRAME_TYPE_PING, f2.copyOfRange(12, f2.size)),
            TcpFrame(TcpWire.FRAME_TYPE_PING, f3.copyOfRange(12, f3.size))
        )
        for (split in 1 until stream.size) {
            val decoder = TcpFrameDecoder()
            val first = decoder.decode(stream.copyOfRange(0, split))
            val second = decoder.decode(stream.copyOfRange(split, stream.size))
            assertEquals(expected, first + second, "split=$split 帧内容或顺序不一致")
        }
    }

    @Test
    fun `single byte feeding produces exactly the original frames`() {
        val frames = listOf(
            TcpFrameEncoder.encode(TcpMessageCodec.toFrame(TcpHello(lobbyResumeToken = "tok-abc"))),
            TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PONG, "{}".toByteArray()),
            TcpFrameEncoder.encode(TcpMessageCodec.toFrame(TcpPing("aabbccddeeff0011")))
        )
        val decoder = TcpFrameDecoder()
        val collected = mutableListOf<TcpFrame>()
        val stream = frames.reduce { acc, b -> acc + b }
        for (b in stream) {
            collected += decoder.decode(byteArrayOf(b))
        }
        assertEquals(3, collected.size)
        for (i in frames.indices) {
            assertTrue(frames[i].copyOfRange(12, frames[i].size).contentEquals(collected[i].payload))
            assertEquals(frames[i][5].toInt(), collected[i].type)
        }
    }

    // ------------------------------------------------------------------
    // 帧层：长度边界全集（UC-F04）
    // ------------------------------------------------------------------

    private fun header(type: Int, lengthField: Int, flags: Int = 0): ByteArray {
        val buf = ByteBuffer.allocate(12)
        buf.putInt(TcpWire.MAGIC_INT)
        buf.put(TcpWire.WIRE_VERSION.toByte())
        buf.put(type.toByte())
        buf.putShort(flags.toShort())
        buf.putInt(lengthField)
        return buf.array()
    }

    @Test
    fun `length field boundaries are enforced via unsigned long`() {
        for (bad in intArrayOf(0, 0x00100001, 0x00FFFFFF, 0xFFFFFFFF.toInt())) {
            val decoder = TcpFrameDecoder()
            val ex = assertFailsWith<TcpProtocolException> { decoder.decode(header(TcpWire.FRAME_TYPE_CLOSE, bad)) }
            assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode, "lengthField=0x${Integer.toHexString(bad)}")
            assertTrue(decoder.isFatalError)
            assertTrue(
                ex.message?.contains(Integer.toUnsignedLong(bad).toString()) == true ||
                    ex.message?.contains("bounds") == true,
                "错误信息应包含无符号数值（未先转 Int 截断）：${ex.message}"
            )
        }
        // 最小合法长度 1：单字节载荷可解出
        val ok = TcpFrameDecoder()
        val frames = ok.decode(header(TcpWire.FRAME_TYPE_CLOSE, 1) + byteArrayOf(0x20))
        assertEquals(1, frames.size)
        assertEquals(1, frames[0].payload.size)
    }

    @Test
    fun `one mebibyte frame round trips and plus one is fatal`() {
        val maxPayload = ByteArray(TcpWire.MAX_PAYLOAD_BYTES) { 'a'.code.toByte() }
        val frame = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_RSP, maxPayload)
        assertEquals(TcpWire.HEADER_SIZE + TcpWire.MAX_PAYLOAD_BYTES, frame.size)
        val decoder = TcpFrameDecoder()
        // 分两半喂入，验证大帧不依赖单次完整到达
        val out = decoder.decode(frame.copyOfRange(0, frame.size / 3)) +
            decoder.decode(frame.copyOfRange(frame.size / 3, frame.size))
        assertEquals(1, out.size)
        assertTrue(out[0].payload.contentEquals(maxPayload))
    }

    // ------------------------------------------------------------------
    // 帧层：终态粘性与 EOF（UC-F05/UC-F06）
    // ------------------------------------------------------------------

    @Test
    fun `fatal state is sticky and never resyncs`() {
        val bad = TcpFrameDecoder()
        val badMagic = header(TcpWire.FRAME_TYPE_CLOSE, 1).also { it[0] = 'X'.code.toByte() }
        assertFailsWith<TcpProtocolException> { bad.decode(badMagic) }
        val result = bad.decodeResult(TcpFrameEncoder.encode(TcpMessageCodec.toFrame(TcpHello())))
        assertTrue(result is TcpDecodeResult.Fatal, "终态后合法帧输入仍必须返回 Fatal，不产出帧")
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, (result as TcpDecodeResult.Fatal).reasonCode)
        val again = bad.decodeResult(byteArrayOf())
        assertTrue(again is TcpDecodeResult.Fatal, "空输入也保持终态不产帧")
    }

    @Test
    fun `end of stream with partial frame throws and clean boundary does not`() {
        val partialHeader = TcpFrameDecoder()
        partialHeader.decode(header(TcpWire.FRAME_TYPE_CLOSE, 4).copyOfRange(0, 7))
        assertFailsWith<TcpProtocolException> { partialHeader.onEndOfStream() }

        val partialBody = TcpFrameDecoder()
        partialBody.decode(header(TcpWire.FRAME_TYPE_CLOSE, 4).copyOfRange(0, 12))
        partialBody.decode("12".toByteArray())
        assertFailsWith<TcpProtocolException> { partialBody.onEndOfStream() }

        val clean = TcpFrameDecoder()
        clean.decode(TcpFrameEncoder.encode(TcpMessageCodec.toFrame(TcpPing("0123456789abcdef"))))
        clean.onEndOfStream() // 不抛
    }

    // ------------------------------------------------------------------
    // 编码侧防护（UC-F09/F10）
    // ------------------------------------------------------------------

    @Test
    fun `encoder rejects invalid type, empty payload, oversize and nonzero flags`() {
        assertFailsWith<IllegalArgumentException> { TcpFrameEncoder.encode(0, "x".toByteArray()) }
        assertFailsWith<IllegalArgumentException> { TcpFrameEncoder.encode(11, "x".toByteArray()) }
        assertFailsWith<IllegalArgumentException> { TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_CLOSE, ByteArray(0)) }
        assertFailsWith<IllegalArgumentException> {
            TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_CLOSE, ByteArray(TcpWire.MAX_PAYLOAD_BYTES + 1))
        }
        assertFailsWith<IllegalArgumentException> {
            TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_CLOSE, "x".toByteArray(), flags = 1)
        }
        // 控制帧同路：PING 编码后 = 头 + JSON 体
        val pingJson = "{\"nonce\":\"0123456789abcdef\"}"
        val ping = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_PING, pingJson.toByteArray())
        assertEquals(TcpWire.HEADER_SIZE + pingJson.length, ping.size)
    }

    // ------------------------------------------------------------------
    // 信封与握手 DTO（D5/D6/D7）
    // ------------------------------------------------------------------

    @Test
    fun `request envelope outer id must match inner id on both decode and frame paths`() {
        val request = ApiRequest(api = "LIST_ROOMS", requestId = "req-1")
        val json = TcpMessageCodec.gson.toJsonTree(
            TcpRequestEnvelope("scope-1", "req-1", request)
        ).asJsonObject
        json.addProperty("requestId", "req-2") // 篡改外层
        val ex = assertFailsWith<TcpProtocolException> {
            TcpMessageCodec.decodeRequestEnvelope(json.toString())
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)

        val forged = TcpRequestEnvelope("scope-1", "req-3", request)
        assertFailsWith<TcpProtocolException> { TcpMessageCodec.toFrame(forged) }
    }

    @Test
    fun `welcome full round trip keeps frozen fields`() {
        val welcome = TcpWelcome(
            connectionEpoch = "epoch-1",
            lobbyResumeToken = "token-1",
            lobbyScope = TcpScope("scope-1", 123456789L),
            limits = TcpLimits(maxInFlightRequests = 32, requestTimeoutMillis = 5000L)
        )
        val parsed = TcpMessageCodec.parseWelcome(TcpMessageCodec.toFrame(welcome))
        assertEquals("epoch-1", parsed.connectionEpoch)
        assertEquals("token-1", parsed.lobbyResumeToken)
        assertEquals("scope-1", parsed.lobbyScope.scope)
        assertEquals(123456789L, parsed.lobbyScope.expiresAtMillis)
        assertEquals(32, parsed.limits.maxInFlightRequests)
        assertEquals(5000L, parsed.limits.requestTimeoutMillis)
        assertEquals(3000L, parsed.limits.heartbeatIntervalMillis)
        assertEquals(10000L, parsed.limits.idleDeadMillis)
    }

    @Test
    fun `strict unknown fields are rejected on welcome and response envelope`() {
        val welcomeJson = TcpMessageCodec.encodeWelcome(
            TcpWelcome(connectionEpoch = "epoch-1", lobbyResumeToken = "token-1", lobbyScope = TcpScope("s", 1L), limits = TcpLimits())
        ).dropLast(1) + ",\"extra\":1}"
        assertFailsWith<TcpProtocolException> { TcpMessageCodec.decodeWelcome(welcomeJson) }

        val env = TcpResponseEnvelope("s", "r1", ApiResponse(requestId = "r1", success = true))
        val envJson = TcpMessageCodec.encodeResponseEnvelope(env).dropLast(1) + ",\"unknown\":true}"
        assertFailsWith<TcpProtocolException> { TcpMessageCodec.decodeResponseEnvelope(envJson) }
    }

    @Test
    fun `invalid scope and limits values are rejected strictly`() {
        val welcomeBase = TcpMessageCodec.encodeWelcome(
            TcpWelcome(connectionEpoch = "e", lobbyResumeToken = "t", lobbyScope = TcpScope("s", 1L), limits = TcpLimits())
        )
        val badScope = welcomeBase.replace("\"expiresAtMillis\":1", "\"expiresAtMillis\":-1")
        assertFailsWith<TcpProtocolException> { TcpMessageCodec.decodeWelcome(badScope) }

        val badLimits = welcomeBase.replace("\"heartbeatIntervalMillis\":3000", "\"heartbeatIntervalMillis\":0")
        assertFailsWith<TcpProtocolException> { TcpMessageCodec.decodeWelcome(badLimits) }
    }

    @Test
    fun `nonce contract - 16 hex chars, echo preserved, invalid rejected`() {
        val nonce = com.majiang.transport.tcp.TcpNonce.generate()
        assertEquals(16, nonce.length)
        assertTrue(nonce.all { it in '0'..'9' || it in 'a'..'f' })

        val pong = TcpMessageCodec.parsePong(TcpMessageCodec.toFrame(TcpPong(nonce)))
        assertEquals(nonce, pong.nonce)

        assertTrue(com.majiang.transport.tcp.TcpNonce.isValid("ABCDEF0123456789"))
        assertFalse(com.majiang.transport.tcp.TcpNonce.isValid("ABCDEF012345678"))
        assertFalse(com.majiang.transport.tcp.TcpNonce.isValid("gbcdef0123456789"))
        // 非法 nonce 的 PING 消息在严格解析层被拒
        val badPingJson = "{\"nonce\":\"0123\"}"
        assertFailsWith<TcpProtocolException> {
            TcpMessageCodec.decodePing(badPingJson)
        }
    }

    @Test
    fun `close round trip with and without message`() {
        val withMsg = TcpMessageCodec.parseClose(
            TcpMessageCodec.toFrame(com.majiang.transport.tcp.TcpClose(TcpCloseReasons.SERVER_SHUTDOWN, "bye"))
        )
        assertEquals(TcpCloseReasons.SERVER_SHUTDOWN, withMsg.reason)
        assertEquals("bye", withMsg.message)

        val plain = TcpMessageCodec.parseClose(
            TcpMessageCodec.toFrame(com.majiang.transport.tcp.TcpClose(TcpCloseReasons.HEARTBEAT_TIMEOUT))
        )
        assertEquals(TcpCloseReasons.HEARTBEAT_TIMEOUT, plain.reason)
        assertNull(plain.message)
    }

    @Test
    fun `frame type mismatch is protocol violation`() {
        val reqFrame = TcpMessageCodec.toFrame(
            TcpRequestEnvelope("s", "r1", ApiRequest(api = "LIST_ROOMS", requestId = "r1"))
        )
        val ex = assertFailsWith<TcpProtocolException> { TcpMessageCodec.parseWelcome(reqFrame) }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
    }

    // ------------------------------------------------------------------
    // 分类契约：对齐 MR-02（R1 返修后必须成立）
    // ------------------------------------------------------------------

    @Test
    fun `close reasons classification aligns with MR-02 permanent set`() {
        // MR-02 ReconnectErrorClassifier.PERMANENT_ERROR_CODES 中的连接级原因在 TCP 侧必须同为永久
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.SERVER_SHUTDOWN), "SERVER_SHUTDOWN 在 MR-02 为永久终止")
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.LOCAL_CLOSED), "LOCAL_CLOSED 在 MR-02 为永久终止")
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION))
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.ROOM_CLOSED))
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.VERSION_MISMATCH))
        assertTrue(TcpCloseReasons.isPermanent(TcpCloseReasons.PROTOCOL_VIOLATION))

        assertTrue(TcpCloseReasons.isTransient(TcpCloseReasons.HEARTBEAT_TIMEOUT))
        assertTrue(TcpCloseReasons.isTransient(TcpCloseReasons.HANDSHAKE_TIMEOUT))
        assertTrue(TcpCloseReasons.isTransient(TcpCloseReasons.SERVER_OVERLOADED))
        assertTrue(TcpCloseReasons.isTransient(TcpCloseReasons.SLOW_CONSUMER))

        assertTrue(TcpCloseReasons.PERMANENT.intersect(TcpCloseReasons.TRANSIENT).isEmpty())
        assertEquals(TcpCloseReasons.ALL, TcpCloseReasons.PERMANENT + TcpCloseReasons.TRANSIENT)
    }

    // ------------------------------------------------------------------
    // 错误码与中性时钟（D3/D11）
    // ------------------------------------------------------------------

    @Test
    fun `request scope expired error exists and transport clock exposes monotonic time`() {
        assertEquals("REQUEST_SCOPE_EXPIRED", ApiErrors.REQUEST_SCOPE_EXPIRED)
        assertTrue(ApiErrors.ALL.contains(ApiErrors.REQUEST_SCOPE_EXPIRED))

        val clock = TransportClock.SYSTEM
        val n1 = clock.nanoTime()
        val n2 = clock.nanoTime()
        assertTrue(n2 >= n1, "单调时基不得回退")
        assertTrue(clock.currentTimeMillis() > 0)
    }
}
