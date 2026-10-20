package com.majiang.transport.tcp

import com.majiang.server.api.FakeServerConnection
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * TCP 握手、信封与控制 DTO 严格编解码单元测试 (契约 4, D5/D6/D7/D9, UC-H01 ~ UC-H08)
 */
class TcpMessagesTest {

    // ============================================================================
    // 1. HELLO & WELCOME (UC-H01, D6)
    // ============================================================================

    @Test
    fun `UC-H01 test initial HELLO and WELCOME encode-decode roundtrip`() {
        // 首次握手（无 token）
        val hello = TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION, lobbyResumeToken = null)
        val helloFrame = TcpMessageCodec.toFrame(hello)
        assertEquals(TcpWire.FRAME_TYPE_HELLO, helloFrame.type)

        val parsedHello = TcpMessageCodec.parseHello(helloFrame)
        assertEquals(1, parsedHello.wireVersion)
        assertEquals(TcpWire.BUSINESS_VERSION, parsedHello.businessVersion)
        assertEquals(8, parsedHello.businessVersion)
        assertNull(parsedHello.lobbyResumeToken)

        // 恢复握手（携 token）
        val resumeHello = TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION, lobbyResumeToken = "tok-1234567890abcdef")
        val resumeFrame = TcpMessageCodec.toFrame(resumeHello)
        val parsedResume = TcpMessageCodec.parseHello(resumeFrame)
        assertEquals("tok-1234567890abcdef", parsedResume.lobbyResumeToken)

        // WELCOME 应答 (D6 冻结字段)
        val welcome = TcpWelcome(
            wireVersion = 1,
            businessVersion = TcpWire.BUSINESS_VERSION,
            connectionEpoch = "epoch-server-abc-123",
            lobbyResumeToken = "tok-server-lobby-xyz",
            lobbyScope = TcpScope(scope = "scope-lobby-001", expiresAtMillis = 1_800_000_000_000L),
            limits = TcpLimits(
                maxInFlightRequests = 64,
                requestTimeoutMillis = 10_000L,
                heartbeatIntervalMillis = 3_000L,
                idleDeadMillis = 10_000L
            )
        )
        val welcomeFrame = TcpMessageCodec.toFrame(welcome)
        assertEquals(TcpWire.FRAME_TYPE_WELCOME, welcomeFrame.type)

        val parsedWelcome = TcpMessageCodec.parseWelcome(welcomeFrame)
        assertEquals(1, parsedWelcome.wireVersion)
        assertEquals(TcpWire.BUSINESS_VERSION, parsedWelcome.businessVersion)
        assertEquals(8, parsedWelcome.businessVersion)
        assertEquals("epoch-server-abc-123", parsedWelcome.connectionEpoch)
        assertEquals("tok-server-lobby-xyz", parsedWelcome.lobbyResumeToken)
        assertEquals("scope-lobby-001", parsedWelcome.lobbyScope.scope)
        assertEquals(1_800_000_000_000L, parsedWelcome.lobbyScope.expiresAtMillis)
        assertEquals(64, parsedWelcome.limits.maxInFlightRequests)
        assertEquals(10_000L, parsedWelcome.limits.requestTimeoutMillis)
        assertEquals(3_000L, parsedWelcome.limits.heartbeatIntervalMillis)
        assertEquals(10_000L, parsedWelcome.limits.idleDeadMillis)
    }

    // ============================================================================
    // 2. Nonce 8 字节 16 hex 规范与 PING / PONG (D5)
    // ============================================================================

    @Test
    fun `test TcpNonce generation and strict 16 hex validation (D5)`() {
        val nonce = TcpNonce.generate()
        assertEquals(16, nonce.length)
        assertTrue(TcpNonce.isValid(nonce))
        TcpNonce.validate(nonce) // 不抛异常

        // 非法长度
        assertFalse(TcpNonce.isValid("0123456789abcde"))   // 15
        assertFalse(TcpNonce.isValid("0123456789abcdef0"))  // 17
        assertFalse(TcpNonce.isValid(""))

        // 非法字符
        assertFalse(TcpNonce.isValid("0123456789abcdeg")) // 'g' 不是 hex
        assertFalse(TcpNonce.isValid("0123456789abcde-"))

        val ex = assertThrows<TcpProtocolException> {
            TcpNonce.validate("invalid-nonce-12")
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
    }

    @Test
    fun `test PING and PONG frames encode-decode and verbatim nonce echo (D5)`() {
        val nonce = "0123456789abcdef"
        val ping = TcpPing(nonce)
        val pingFrame = TcpMessageCodec.toFrame(ping)
        assertEquals(TcpWire.FRAME_TYPE_PING, pingFrame.type)

        val parsedPing = TcpMessageCodec.parsePing(pingFrame)
        assertEquals(nonce, parsedPing.nonce)

        // PONG 逐字节回显 nonce
        val pong = TcpPong(parsedPing.nonce)
        val pongFrame = TcpMessageCodec.toFrame(pong)
        assertEquals(TcpWire.FRAME_TYPE_PONG, pongFrame.type)

        val parsedPong = TcpMessageCodec.parsePong(pongFrame)
        assertEquals(nonce, parsedPong.nonce)
    }

    // ============================================================================
    // 3. REQ 信封与 requestId 外层一致性 (D7)
    // ============================================================================

    @Test
    fun `test REQ envelope encode-decode and consistency validation (D7)`() {
        val innerReq = ApiRequest(
            api = ApiTags.CREATE_ROOM,
            requestId = "req-match-123",
            playerName = "Tester"
        )
        val envelope = TcpRequestEnvelope(
            requestScope = "scope-1",
            requestId = "req-match-123",
            request = innerReq
        )
        assertTrue(envelope.isRequestIdConsistent())
        envelope.validateRequestIdConsistency()

        val frame = TcpMessageCodec.toFrame(envelope)
        assertEquals(TcpWire.FRAME_TYPE_REQ, frame.type)

        val decoded = TcpMessageCodec.parseRequestEnvelope(frame)
        assertEquals("scope-1", decoded.requestScope)
        assertEquals("req-match-123", decoded.requestId)
        assertEquals(ApiTags.CREATE_ROOM, decoded.request.api)
        assertEquals("Tester", decoded.request.playerName)
    }

    @Test
    fun `test REQ envelope rejects mismatched requestId with PROTOCOL_VIOLATION (D7)`() {
        val innerReq = ApiRequest(
            api = ApiTags.CREATE_ROOM,
            requestId = "req-inner-001",
            playerName = "Tester"
        )
        val mismatchedEnvelope = TcpRequestEnvelope(
            requestScope = "scope-1",
            requestId = "req-outer-999", // 不一致
            request = innerReq
        )
        assertFalse(mismatchedEnvelope.isRequestIdConsistent())

        val ex1 = assertThrows<TcpProtocolException> {
            mismatchedEnvelope.validateRequestIdConsistency()
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex1.reasonCode)

        val ex2 = assertThrows<TcpProtocolException> {
            TcpMessageCodec.toFrame(mismatchedEnvelope)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex2.reasonCode)

        // 模拟线路上到达的外层与内层不一致的 JSON
        val forgedJson = """
            {
                "requestScope": "scope-1",
                "requestId": "outer-forged",
                "request": {
                    "api": "CREATE_ROOM",
                    "requestId": "inner-real",
                    "playerName": "Tester"
                }
            }
        """.trimIndent()
        val ex3 = assertThrows<TcpProtocolException> {
            TcpMessageCodec.decodeRequestEnvelope(forgedJson)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex3.reasonCode)
        assertTrue(ex3.message?.contains("does not match") == true)
    }

    // ============================================================================
    // 4. RSP 信封与 grantedScope (D7)
    // ============================================================================

    @Test
    fun `test RSP envelope without grantedScope and with grantedScope (D7)`() {
        // 普通 RSP（无 grantedScope）
        val resp1 = ApiResponse(
            requestId = "req-1",
            success = true,
            roomId = "ROOM123"
        )
        val env1 = TcpResponseEnvelope(
            requestScope = "scope-1",
            requestId = "req-1",
            response = resp1
        )
        val frame1 = TcpMessageCodec.toFrame(env1)
        assertEquals(TcpWire.FRAME_TYPE_RSP, frame1.type)

        val decoded1 = TcpMessageCodec.parseResponseEnvelope(frame1)
        assertEquals("scope-1", decoded1.requestScope)
        assertEquals("req-1", decoded1.requestId)
        assertTrue(decoded1.response.success)
        assertEquals("ROOM123", decoded1.response.roomId)
        assertNull(decoded1.grantedScope)

        // 角色绑定成功的 RSP（外层携带 grantedScope）
        val granted = TcpScope("scope-player-seat-0", 1_700_000_100_000L)
        val env2 = TcpResponseEnvelope(
            requestScope = "scope-lobby",
            requestId = "req-join",
            response = ApiResponse(requestId = "req-join", success = true, seat = 0),
            grantedScope = granted
        )
        val frame2 = TcpMessageCodec.toFrame(env2)
        val decoded2 = TcpMessageCodec.parseResponseEnvelope(frame2)
        assertEquals("scope-lobby", decoded2.requestScope)
        assertEquals("req-join", decoded2.requestId)
        assertNotNull(decoded2.grantedScope)
        assertEquals("scope-player-seat-0", decoded2.grantedScope?.scope)
        assertEquals(1_700_000_100_000L, decoded2.grantedScope?.expiresAtMillis)
    }

    // ============================================================================
    // 5. CLOSE 消息 (D4)
    // ============================================================================

    @Test
    fun `test CLOSE message encode-decode roundtrip`() {
        val closeMsg = TcpClose(
            reason = TcpCloseReasons.SLOW_CONSUMER,
            message = "outbound queue exceeded 256 frames"
        )
        val frame = TcpMessageCodec.toFrame(closeMsg)
        assertEquals(TcpWire.FRAME_TYPE_CLOSE, frame.type)

        val decoded = TcpMessageCodec.parseClose(frame)
        assertEquals(TcpCloseReasons.SLOW_CONSUMER, decoded.reason)
        assertEquals("outbound queue exceeded 256 frames", decoded.message)
    }

    // ============================================================================
    // 6. 确定性测试：坏 JSON 与未知字段严格拒绝 (契约 4)
    // ============================================================================

    @Test
    fun `test bad JSON syntax in HELLO is rejected with PROTOCOL_VIOLATION`() {
        val badSyntax = "{wireVersion:1, broken"
        val ex = assertThrows<TcpProtocolException> {
            TcpMessageCodec.decodeHello(badSyntax)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
        assertTrue(ex.message?.contains("Malformed JSON") == true)
    }

    @Test
    fun `test unknown fields in HELLO are strictly rejected with PROTOCOL_VIOLATION`() {
        val jsonWithUnknown = """
            {
                "wireVersion": 1,
                "businessVersion": 3,
                "extraMaliciousField": "evil"
            }
        """.trimIndent()

        val ex = assertThrows<TcpProtocolException> {
            TcpMessageCodec.decodeHello(jsonWithUnknown)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
        assertTrue(ex.message?.contains("Unknown field") == true)
    }

    @Test
    fun `test bad JSON syntax in REQ envelope is rejected with PROTOCOL_VIOLATION`() {
        val badSyntax = """{"requestScope":"s", "requestId": """
        val ex = assertThrows<TcpProtocolException> {
            TcpMessageCodec.decodeRequestEnvelope(badSyntax)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
    }

    @Test
    fun `test unknown fields in REQ envelope are strictly rejected with PROTOCOL_VIOLATION`() {
        val jsonWithUnknown = """
            {
                "requestScope": "scope-1",
                "requestId": "req-1",
                "request": {
                    "api": "CREATE_ROOM",
                    "requestId": "req-1"
                },
                "unauthorizedField": 123
            }
        """.trimIndent()

        val ex = assertThrows<TcpProtocolException> {
            TcpMessageCodec.decodeRequestEnvelope(jsonWithUnknown)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
        assertTrue(ex.message?.contains("Unknown field") == true)
    }

    @Test
    fun `test missing required fields in WELCOME is rejected with PROTOCOL_VIOLATION`() {
        // 缺少 lobbyResumeToken
        val missingFieldJson = """
            {
                "wireVersion": 1,
                "businessVersion": 3,
                "connectionEpoch": "epoch-1",
                "lobbyScope": {"scope":"s1", "expiresAtMillis": 1000},
                "limits": {"maxInFlightRequests":64, "requestTimeoutMillis":10000, "heartbeatIntervalMillis":3000, "idleDeadMillis":10000}
            }
        """.trimIndent()

        val ex = assertThrows<TcpProtocolException> {
            TcpMessageCodec.decodeWelcome(missingFieldJson)
        }
        assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, ex.reasonCode)
        assertTrue(ex.message?.contains("missing lobbyResumeToken") == true)
    }

    // ============================================================================
    // 7. 更新帧 (PLAYER_UPDATE, PUBLIC_UPDATE, ROOM_UPDATE)
    // ============================================================================

    @Test
    fun `test player and public update frames encode and parse correctly`() {
        val snapshot = FakeServerConnection.createDefaultSnapshot(cursor = 42L)
        val playerUpdate = ProtocolPlayerUpdate(snapshot = snapshot, fromCursor = 40L, visibleEvents = emptyList())
        val playerFrame = TcpMessageCodec.toPlayerUpdateFrame(playerUpdate, isFullSnapshot = true)
        assertEquals(TcpWire.FRAME_TYPE_PLAYER_UPDATE, playerFrame.type)

        val parsedPlayer = TcpMessageCodec.parsePlayerUpdate(playerFrame)
        assertEquals(42L, parsedPlayer.snapshot.cursor)
        assertEquals(40L, parsedPlayer.fromCursor)

        val pubSnapshot = FakeServerConnection.createDefaultPublicSnapshot(cursor = 15L)
        val publicUpdate = ProtocolPublicUpdate(snapshot = pubSnapshot, fromCursor = 14L, visibleEvents = emptyList(), isFullSnapshot = false)
        val publicFrame = TcpMessageCodec.toPublicUpdateFrame(publicUpdate)
        assertEquals(TcpWire.FRAME_TYPE_PUBLIC_UPDATE, publicFrame.type)

        val parsedPublic = TcpMessageCodec.parsePublicUpdate(publicFrame)
        assertEquals(15L, parsedPublic.snapshot.cursor)
        assertEquals(14L, parsedPublic.fromCursor)

        val roomInfo = FakeServerConnection.createDefaultRoomInfo(
            roomId = "ROOM1",
            lifecycle = ProtocolRoomLifecycle.PLAYING,
            version = 2L
        )
        val roomFrame = TcpMessageCodec.toRoomUpdateFrame(roomInfo)
        assertEquals(TcpWire.FRAME_TYPE_ROOM_UPDATE, roomFrame.type)

        val parsedRoom = TcpMessageCodec.parseRoomUpdate(roomFrame)
        assertEquals("ROOM1", parsedRoom.roomId)
        assertEquals(ProtocolRoomLifecycle.PLAYING, parsedRoom.lifecycle)
        assertEquals(2L, parsedRoom.version)
    }

    @Test
    fun `test parse functions reject mismatched frame type with PROTOCOL_VIOLATION`() {
        val pingFrame = TcpMessageCodec.toFrame(TcpPing("0123456789abcdef"))

        assertThrows<TcpProtocolException> {
            TcpMessageCodec.parseHello(pingFrame) // 期待 HELLO 但传入 PING
        }
        assertThrows<TcpProtocolException> {
            TcpMessageCodec.parseWelcome(pingFrame)
        }
        assertThrows<TcpProtocolException> {
            TcpMessageCodec.parseRequestEnvelope(pingFrame)
        }
    }
}
