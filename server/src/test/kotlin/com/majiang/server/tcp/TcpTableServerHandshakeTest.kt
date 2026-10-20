package com.majiang.server.tcp

import com.majiang.server.api.ApiProtocol
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpFrameEncoder
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNotSame
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.nio.ByteBuffer

/**
 * TCP 握手与会话建立契约测试 (TcpTableServerHandshakeTest)
 *
 * 严格覆盖 UC-H01 ~ UC-H09 全部用例。
 */
@Timeout(30)
class TcpTableServerHandshakeTest {

    @Test
    fun `UC-H01 first handshake exchange connect hello welcome`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                val hello = TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)
                client.sendFrame(TcpMessageCodec.toFrame(hello))

                val welcome = client.expectWelcome()
                assertEquals(1, welcome.wireVersion)
                assertEquals(TcpWire.BUSINESS_VERSION, welcome.businessVersion)
                assertEquals(8, welcome.businessVersion)
                assertEquals(8, ApiProtocol.PROTOCOL_VERSION)
                assertTrue(welcome.connectionEpoch.isNotBlank())
                assertTrue(welcome.lobbyResumeToken.startsWith("lbt-"))
                assertTrue(welcome.lobbyScope.scope.startsWith("scope-"))
                assertTrue(welcome.lobbyScope.expiresAtMillis > System.currentTimeMillis())
                assertEquals(64, welcome.limits.maxInFlightRequests)
                assertEquals(10000L, welcome.limits.requestTimeoutMillis)
                assertEquals(3000L, welcome.limits.heartbeatIntervalMillis)
                assertEquals(10000L, welcome.limits.idleDeadMillis)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-H02 duplicate HELLO causes PROTOCOL_VIOLATION close`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                val hello = TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)
                client.sendFrame(TcpMessageCodec.toFrame(hello))
                val welcome = client.expectWelcome()
                assertNotNull(welcome)

                // 第二次发送 HELLO
                client.sendFrame(TcpMessageCodec.toFrame(hello))

                val closeMsg = client.expectClose()
                assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, closeMsg.reason)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-H03 business REQ before HELLO causes PROTOCOL_VIOLATION close`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                // 在发送 HELLO 之前直接发送业务 REQ 信封
                val env = TcpRequestEnvelope(
                    requestScope = "scope-fake",
                    requestId = "req-premature",
                    request = ApiRequest(api = "LIST_ROOMS", requestId = "req-premature")
                )
                client.sendFrame(TcpMessageCodec.toFrame(env))

                val closeMsg = client.expectClose()
                assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, closeMsg.reason)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-H04 handshake timeout closes connection with HANDSHAKE_TIMEOUT`() {
        // 配置 500ms 快速超时的服务器
        val server = TcpTableServer(TcpServerConfig(port = 0, handshakeTimeoutMillis = 500L))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                // 建连后不发送 HELLO
                val closeMsg = client.expectClose(timeoutMillis = 4000L)
                assertEquals(TcpCloseReasons.HANDSHAKE_TIMEOUT, closeMsg.reason)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-H05 version mismatch closes connection with VERSION_MISMATCH`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                // v8 升版后，旧业务版本 7 必须被明确拒绝；活动房间不做热迁移。
                val helloBadBiz = TcpHello(
                    wireVersion = TcpWire.WIRE_VERSION,
                    businessVersion = TcpWire.BUSINESS_VERSION - 1
                )
                assertEquals(7, helloBadBiz.businessVersion)
                client.sendFrame(TcpMessageCodec.toFrame(helloBadBiz))

                val closeMsg = client.expectClose()
                assertEquals(TcpCloseReasons.VERSION_MISMATCH, closeMsg.reason)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-H05b future business version is rejected with VERSION_MISMATCH`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                val helloFuture = TcpHello(
                    wireVersion = TcpWire.WIRE_VERSION,
                    businessVersion = TcpWire.BUSINESS_VERSION + 1
                )
                client.sendFrame(TcpMessageCodec.toFrame(helloFuture))

                val closeMsg = client.expectClose()
                assertEquals(TcpCloseReasons.VERSION_MISMATCH, closeMsg.reason)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-H06 resume handshake with valid lobbyResumeToken preserves principal`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            val token: String
            // 首次连接取得 token
            TcpTestClient(port = server.boundPort).use { client1 ->
                client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome1 = client1.expectWelcome()
                token = welcome1.lobbyResumeToken
            }

            // 二次连接携带 token
            TcpTestClient(port = server.boundPort).use { client2 ->
                client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION, lobbyResumeToken = token)))
                val welcome2 = client2.expectWelcome()
                assertEquals(token, welcome2.lobbyResumeToken)
                assertTrue(welcome2.lobbyScope.scope.startsWith("scope-"))
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-H07 connectionEpoch is unique across connections`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            val epochs = mutableSetOf<String>()
            for (i in 0 until 5) {
                TcpTestClient(port = server.boundPort).use { client ->
                    client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                    val welcome = client.expectWelcome()
                    assertTrue(welcome.connectionEpoch.isNotBlank())
                    epochs.add(welcome.connectionEpoch)
                }
            }
            assertEquals(5, epochs.size, "Each connection must receive a unique connectionEpoch")
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-H08 flags non-zero in frame header causes PROTOCOL_VIOLATION`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                // 构造 flags = 1 的非法帧 (UC-F05 / UC-H08)
                val payload = "{\"wireVersion\":1,\"businessVersion\":1}".toByteArray(Charsets.UTF_8)
                val rawBuf = ByteBuffer.allocate(TcpWire.HEADER_SIZE + payload.size)
                rawBuf.putInt(TcpWire.MAGIC_INT)
                rawBuf.put(1.toByte()) // wireVersion = 1
                rawBuf.put(1.toByte()) // type = HELLO (1)
                rawBuf.putShort(1.toShort()) // 非法 flags = 1
                rawBuf.putInt(payload.size)
                rawBuf.put(payload)

                client.sendRaw(rawBuf.array())

                // 服务端应直接断开连接或发送 CLOSE
                val closeMsg = client.expectClose()
                assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, closeMsg.reason)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-H09 unhandshaked connection bucket limit rejects 65th connection immediately`() {
        val server = TcpTableServer(TcpServerConfig(port = 0, maxUnhandshakedConnections = 4))
        server.start()

        val clients = mutableListOf<TcpTestClient>()
        try {
            // 建立 4 条未握手连接填满桶
            for (i in 1..4) {
                clients.add(TcpTestClient(port = server.boundPort))
            }
            // 确保服务端已 accept
            Thread.sleep(100)
            assertEquals(4, server.unhandshakedCount.get())

            // 第 5 条到达，立即被拒绝 (SERVER_OVERLOADED)
            TcpTestClient(port = server.boundPort).use { rejectedClient ->
                val closeMsg = rejectedClient.expectClose()
                assertEquals(TcpCloseReasons.SERVER_OVERLOADED, closeMsg.reason)
            }
        } finally {
            clients.forEach { it.close() }
            server.close()
        }
    }

    @Test
    fun `R3 regression - repeatedly connecting and closing without handshake does not leak bucket`() {
        val server = TcpTableServer(TcpServerConfig(port = 0, maxUnhandshakedConnections = 4))
        server.start()

        try {
            // 反复建连不握手直接断开 10 次 (> maxUnhandshakedConnections = 4)
            for (i in 1..10) {
                TcpTestClient(port = server.boundPort).use { client ->
                    // 连上后什么都不发直接 close
                }
            }
            // 等待服务端事件循环处理并清理连接
            Thread.sleep(200)
            assertEquals(0, server.unhandshakedCount.get(), "Unhandshaked count must be 0 after all disconnected")

            // 第 11 条连接建立并正常握手，必须成功收到 WELCOME，证明桶未泄漏
            TcpTestClient(port = server.boundPort).use { client ->
                val hello = TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)
                client.sendFrame(TcpMessageCodec.toFrame(hello))
                val welcome = client.expectWelcome()
                assertNotNull(welcome)
                assertEquals(1, welcome.wireVersion)
            }
        } finally {
            server.close()
        }
    }
}
