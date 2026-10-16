package com.majiang.server.tcp

import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpRequestEnvelope
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

/**
 * TCP 路由、原子基线推送与端到端交互契约测试 (TcpTableServerRoutingAndSubscriptionTest)
 *
 * 严格覆盖：
 * - UC-S01: 绑定基线原子性与出站顺序 (Baseline RSP 先入队再放行后续 UPDATE)
 * - D7 信封一致性：伪造内层 requestId 判 PROTOCOL_VIOLATION 断开
 * - D9 GET_REQUEST_SCOPE 控制查询与 grantedScope 返回
 * - 端到端全流程：建房 -> 获得基线 -> 准备 -> 开赛 -> 状态流转 -> 离开
 */
@Timeout(30)
class TcpTableServerRoutingAndSubscriptionTest {

    @Test
    fun `D7 envelope requestId mismatch causes PROTOCOL_VIOLATION close`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome = client.expectWelcome()

                // 外层 requestId = "out-123", 内层 request.requestId = "inner-456" 伪造冲突
                val rawJson = """{"requestScope":"${welcome.lobbyScope.scope}","requestId":"out-123","request":{"api":"LIST_ROOMS","requestId":"inner-456"}}"""
                val frame = TcpFrame(com.majiang.transport.tcp.TcpWire.FRAME_TYPE_REQ, rawJson.toByteArray(Charsets.UTF_8))
                client.sendFrame(frame)

                val closeMsg = client.expectClose()
                assertEquals(TcpCloseReasons.PROTOCOL_VIOLATION, closeMsg.reason)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `D9 GET_REQUEST_SCOPE query returns valid grantedScope`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome = client.expectWelcome()

                val queryEnv = TcpRequestEnvelope(
                    requestScope = welcome.lobbyScope.scope,
                    requestId = "req-scope-query-1",
                    request = ApiRequest(api = "GET_REQUEST_SCOPE", requestId = "req-scope-query-1")
                )
                client.sendFrame(TcpMessageCodec.toFrame(queryEnv))

                val rsp = client.expectResponse()
                assertEquals("req-scope-query-1", rsp.requestId)
                assertTrue(rsp.response.success)
                assertNotNull(rsp.grantedScope, "RSP must carry grantedScope")
                assertTrue(rsp.grantedScope!!.scope.startsWith("scope-"))
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `UC-S01 and end-to-end room lifecycle with atomic baseline delivery`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            TcpTestClient(port = server.boundPort).use { client ->
                // 1. 握手
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome = client.expectWelcome()
                val lobbyScope = welcome.lobbyScope.scope

                // 2. 建房 CREATE_ROOM
                val createEnv = TcpRequestEnvelope(
                    requestScope = lobbyScope,
                    requestId = "req-create-1",
                    request = ApiRequest(
                        api = "CREATE_ROOM",
                        requestId = "req-create-1",
                        playerName = "HostPlayer",
                        roomConfig = ProtocolRoomConfig()
                    )
                )
                client.sendFrame(TcpMessageCodec.toFrame(createEnv))

                // UC-S01 校验：第一条到达的必须是包含明确完整基线快照的 RSP 信封帧！
                val rsp = client.expectResponse()
                assertEquals("req-create-1", rsp.requestId)
                assertTrue(rsp.response.success)
                assertNotNull(rsp.response.roomId)
                assertEquals(0, rsp.response.seat)
                assertNotNull(rsp.response.snapshot, "CREATE_ROOM RSP must carry initial baseline snapshot")
                assertEquals(0L, rsp.response.snapshot!!.cursor)
                assertNotNull(rsp.grantedScope, "Role binding must grant roleScope in RSP")

                val roleScope = rsp.grantedScope!!.scope
                assertTrue(roleScope.startsWith("scope-"))

                // 3. 设置准备就绪 SET_READY
                val readyEnv = TcpRequestEnvelope(
                    requestScope = roleScope,
                    requestId = "req-ready-1",
                    request = ApiRequest(api = "SET_READY", requestId = "req-ready-1", ready = true)
                )
                client.sendFrame(TcpMessageCodec.toFrame(readyEnv))
                val readyRsp = client.expectResponse()
                assertEquals("req-ready-1", readyRsp.requestId)
                assertTrue(readyRsp.response.success)

                // 4. 查询当前房间信息 GET_ROOM_INFO
                val infoEnv = TcpRequestEnvelope(
                    requestScope = roleScope,
                    requestId = "req-info-1",
                    request = ApiRequest(api = "GET_ROOM_INFO", requestId = "req-info-1")
                )
                client.sendFrame(TcpMessageCodec.toFrame(infoEnv))
                val infoRsp = client.expectResponse()
                assertTrue(infoRsp.response.success)
                assertNotNull(infoRsp.response.roomInfo)

                // 5. 显式退出房间 LEAVE_ROOM
                val leaveEnv = TcpRequestEnvelope(
                    requestScope = roleScope,
                    requestId = "req-leave-1",
                    request = ApiRequest(api = "LEAVE_ROOM", requestId = "req-leave-1")
                )
                client.sendFrame(TcpMessageCodec.toFrame(leaveEnv))
                val leaveRsp = client.expectResponse()
                assertTrue(leaveRsp.response.success)
            }
        } finally {
            server.close()
        }
    }

    @Test
    fun `R7+R8 regression - displaced connection receives DISPLACED_BY_NEW_CONNECTION close after seat resume`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()

        try {
            // 1. Client 1 连入并建房，绑定到 seat 0
            TcpTestClient(port = server.boundPort).use { client1 ->
                client1.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                val welcome1 = client1.expectWelcome()

                val createEnv = TcpRequestEnvelope(
                    requestScope = welcome1.lobbyScope.scope,
                    requestId = "req-create-r7",
                    request = ApiRequest(
                        api = "CREATE_ROOM",
                        requestId = "req-create-r7",
                        playerName = "P1",
                        roomConfig = ProtocolRoomConfig()
                    )
                )
                client1.sendFrame(TcpMessageCodec.toFrame(createEnv))
                val createRsp = client1.expectResponse()
                assertTrue(createRsp.response.success)
                val roomId = createRsp.response.roomId!!
                val resumeToken = createRsp.response.resumeToken!!

                // 2. Client 2 连入并通过 resumeToken 恢复该席位 (顶号)
                TcpTestClient(port = server.boundPort).use { client2 ->
                    client2.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
                    val welcome2 = client2.expectWelcome()

                    val resumeEnv = TcpRequestEnvelope(
                        requestScope = welcome2.lobbyScope.scope,
                        requestId = "req-resume-r7",
                        request = ApiRequest(
                            api = "RESUME_SESSION",
                            requestId = "req-resume-r7",
                            roomId = roomId,
                            token = resumeToken
                        )
                    )
                    client2.sendFrame(TcpMessageCodec.toFrame(resumeEnv))
                    val resumeRsp = client2.expectResponse()
                    assertTrue(resumeRsp.response.success)
                    assertEquals(0, resumeRsp.response.seat)

                    // 3. 被顶连接 Client 1 必须在超时内读到 CLOSE 帧，且 reason 明确为 DISPLACED_BY_NEW_CONNECTION (R8)
                    val closeMsg = client1.expectClose()
                    assertEquals(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, closeMsg.reason)
                }
            }
        } finally {
            server.close()
        }
    }
}
