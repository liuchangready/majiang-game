package com.majiang.server.inprocess

import com.majiang.client.transport.tcp.TcpClient
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.ServerApiException
import com.majiang.server.tcp.TcpServerConfig
import com.majiang.server.tcp.TcpTableServer
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit

/**
 * 跨传输链路业务错误码一致性验证 (DualTransportErrorCodeConsistencyTest)
 *
 * 验证目标 (MR-02 / T5):
 * 1. 进程内绑定 (InProcessBinding) 与 TCP 权威房间服务 (TcpTableServer) 在相同业务失败下产生完全相同的结构化错误码 (ServerApiException.code)。
 * 2. 覆盖场景：
 *    - ROOM_NOT_FOUND：加房不存在、重连不存在房间
 *    - INVALID_TOKEN：房间存在但 resumeToken 无效、旁观重连 token 无效
 *    - ROOM_FULL：房间 4 席已满仍尝试加入
 * 3. 验证 ServerApiException.unwrap 在双端链路上均正确提取强类型错误码。
 */
@Timeout(15)
class DualTransportErrorCodeConsistencyTest {

    private lateinit var inProcessServer: InProcessServer
    private lateinit var tcpServer: TcpTableServer

    @BeforeEach
    fun setUp() {
        inProcessServer = InProcessServer(isSingleplayer = false)
        tcpServer = TcpTableServer(config = TcpServerConfig(port = 0))
        tcpServer.start()
    }

    @AfterEach
    fun tearDown() {
        try {
            tcpServer.close()
        } catch (_: Throwable) {
        }
        try {
            inProcessServer.close()
        } catch (_: Throwable) {
        }
    }

    private fun createTcpConnection() =
        TcpClient.connect("127.0.0.1", tcpServer.boundPort).get(5, TimeUnit.SECONDS)

    @Test
    fun `both transports return ROOM_NOT_FOUND on join non-existent room`() {
        val nonExistentRoomId = "ROOM_DOES_NOT_EXIST_999"

        // 1. In-process transport
        val inProcessConn = inProcessServer.createConnection()
        val inProcessEx = assertThrows(ExecutionException::class.java) {
            inProcessConn.joinRoom(JoinRoomRequest(nonExistentRoomId, "PlayerInProcess", 0)).get(5, TimeUnit.SECONDS)
        }
        val inProcessTyped = ServerApiException.unwrap(inProcessEx) as? ServerApiException
        assertNotNull(inProcessTyped, "InProcess exception should unwrap to ServerApiException")
        assertEquals(ApiErrors.ROOM_NOT_FOUND, inProcessTyped!!.code)

        // 2. TCP transport
        createTcpConnection().use { tcpConn ->
            val tcpEx = assertThrows(ExecutionException::class.java) {
                tcpConn.joinRoom(JoinRoomRequest(nonExistentRoomId, "PlayerTcp", 0)).get(5, TimeUnit.SECONDS)
            }
            val tcpTyped = ServerApiException.unwrap(tcpEx) as? ServerApiException
            assertNotNull(tcpTyped, "TCP exception should unwrap to ServerApiException")
            assertEquals(ApiErrors.ROOM_NOT_FOUND, tcpTyped!!.code)

            // 3. Consistency assertion
            assertEquals(inProcessTyped.code, tcpTyped.code)
        }
    }

    @Test
    fun `both transports return ROOM_NOT_FOUND on resume non-existent room`() {
        val nonExistentRoomId = "ROOM_DOES_NOT_EXIST_888"

        // 1. In-process transport
        val inProcessConn = inProcessServer.createConnection()
        val inProcessEx = assertThrows(ExecutionException::class.java) {
            inProcessConn.resumeSession(ResumeRequest(nonExistentRoomId, "fake_token")).get(5, TimeUnit.SECONDS)
        }
        val inProcessTyped = ServerApiException.unwrap(inProcessEx) as? ServerApiException
        assertNotNull(inProcessTyped, "InProcess exception should unwrap to ServerApiException")
        assertEquals(ApiErrors.ROOM_NOT_FOUND, inProcessTyped!!.code)

        // 2. TCP transport
        createTcpConnection().use { tcpConn ->
            val tcpEx = assertThrows(ExecutionException::class.java) {
                tcpConn.resumeSession(ResumeRequest(nonExistentRoomId, "fake_token")).get(5, TimeUnit.SECONDS)
            }
            val tcpTyped = ServerApiException.unwrap(tcpEx) as? ServerApiException
            assertNotNull(tcpTyped, "TCP exception should unwrap to ServerApiException")
            assertEquals(ApiErrors.ROOM_NOT_FOUND, tcpTyped!!.code)

            // 3. Consistency assertion
            assertEquals(inProcessTyped.code, tcpTyped.code)
        }
    }

    @Test
    fun `both transports return INVALID_TOKEN on resume with wrong token in existing room`() {
        // 1. In-process: create room first
        val inProcessHostConn = inProcessServer.createConnection()
        val hostBinding = inProcessHostConn.createRoom(
            CreateRoomRequest(playerName = "HostInProcess")
        ).get(5, TimeUnit.SECONDS)
        val inProcessRoomId = hostBinding.roomId

        // In-process: resume with bad token
        val inProcessResumeConn = inProcessServer.createConnection()
        val inProcessEx = assertThrows(ExecutionException::class.java) {
            inProcessResumeConn.resumeSession(
                ResumeRequest(inProcessRoomId, "INVALID_TOKEN_XYZ")
            ).get(5, TimeUnit.SECONDS)
        }
        val inProcessTyped = ServerApiException.unwrap(inProcessEx) as? ServerApiException
        assertNotNull(inProcessTyped, "InProcess exception should unwrap to ServerApiException")
        assertEquals(ApiErrors.INVALID_TOKEN, inProcessTyped!!.code)

        // 2. TCP: create room first
        createTcpConnection().use { tcpHostConn ->
            val tcpHostBinding = tcpHostConn.createRoom(
                CreateRoomRequest(playerName = "HostTcp")
            ).get(5, TimeUnit.SECONDS)
            val tcpRoomId = tcpHostBinding.roomId

            createTcpConnection().use { tcpResumeConn ->
                val tcpEx = assertThrows(ExecutionException::class.java) {
                    tcpResumeConn.resumeSession(
                        ResumeRequest(tcpRoomId, "INVALID_TOKEN_XYZ")
                    ).get(5, TimeUnit.SECONDS)
                }
                val tcpTyped = ServerApiException.unwrap(tcpEx) as? ServerApiException
                assertNotNull(tcpTyped, "TCP exception should unwrap to ServerApiException")
                assertEquals(ApiErrors.INVALID_TOKEN, tcpTyped!!.code)

                // 3. Consistency assertion
                assertEquals(inProcessTyped.code, tcpTyped.code)
            }
        }
    }

    @Test
    fun `both transports return ROOM_FULL when joining already full room`() {
        // 1. In-process: create room and fill 4 seats
        val hostConn = inProcessServer.createConnection()
        val hostBinding = hostConn.createRoom(
            CreateRoomRequest(playerName = "Host")
        ).get(5, TimeUnit.SECONDS)
        val inProcessRoomId = hostBinding.roomId

        // Fill seats 1, 2, 3
        val p1Conn = inProcessServer.createConnection()
        p1Conn.joinRoom(JoinRoomRequest(inProcessRoomId, "P1", 1)).get(5, TimeUnit.SECONDS)
        val p2Conn = inProcessServer.createConnection()
        p2Conn.joinRoom(JoinRoomRequest(inProcessRoomId, "P2", 2)).get(5, TimeUnit.SECONDS)
        val p3Conn = inProcessServer.createConnection()
        p3Conn.joinRoom(JoinRoomRequest(inProcessRoomId, "P3", 3)).get(5, TimeUnit.SECONDS)

        // 5th player attempts to join
        val p4Conn = inProcessServer.createConnection()
        val inProcessEx = assertThrows(ExecutionException::class.java) {
            p4Conn.joinRoom(JoinRoomRequest(inProcessRoomId, "P4", null)).get(5, TimeUnit.SECONDS)
        }
        val inProcessTyped = ServerApiException.unwrap(inProcessEx) as? ServerApiException
        assertNotNull(inProcessTyped, "InProcess exception should unwrap to ServerApiException")
        assertEquals(ApiErrors.ROOM_FULL, inProcessTyped!!.code)

        // 2. TCP: create room and fill 4 seats
        createTcpConnection().use { tcpHost ->
            val tcpBinding = tcpHost.createRoom(
                CreateRoomRequest(playerName = "HostTcp")
            ).get(5, TimeUnit.SECONDS)
            val tcpRoomId = tcpBinding.roomId

            createTcpConnection().use { tcpP1 ->
                tcpP1.joinRoom(JoinRoomRequest(tcpRoomId, "Tcp1", 1)).get(5, TimeUnit.SECONDS)
                createTcpConnection().use { tcpP2 ->
                    tcpP2.joinRoom(JoinRoomRequest(tcpRoomId, "Tcp2", 2)).get(5, TimeUnit.SECONDS)
                    createTcpConnection().use { tcpP3 ->
                        tcpP3.joinRoom(JoinRoomRequest(tcpRoomId, "Tcp3", 3)).get(5, TimeUnit.SECONDS)

                        // 5th player attempts to join
                        createTcpConnection().use { tcpP4 ->
                            val tcpEx = assertThrows(ExecutionException::class.java) {
                                tcpP4.joinRoom(JoinRoomRequest(tcpRoomId, "Tcp4", null)).get(5, TimeUnit.SECONDS)
                            }
                            val tcpTyped = ServerApiException.unwrap(tcpEx) as? ServerApiException
                            assertNotNull(tcpTyped, "TCP exception should unwrap to ServerApiException")
                            assertEquals(ApiErrors.ROOM_FULL, tcpTyped!!.code)

                            // 3. Consistency assertion
                            assertEquals(inProcessTyped.code, tcpTyped.code)
                        }
                    }
                }
            }
        }
    }

    @Test
    fun `both transports return INVALID_TOKEN on resume spectator with invalid token`() {
        // 1. In-process: create room first
        val hostConn = inProcessServer.createConnection()
        val hostBinding = hostConn.createRoom(
            CreateRoomRequest(playerName = "HostInProcessSpec")
        ).get(5, TimeUnit.SECONDS)
        val inProcessRoomId = hostBinding.roomId

        // In-process: resume spectator with bad token
        val specConn = inProcessServer.createConnection()
        val inProcessEx = assertThrows(ExecutionException::class.java) {
            specConn.resumeSpectator(
                ResumeSpectatorRequest(roomId = inProcessRoomId, spectatorId = "spec-inprocess-1", resumeToken = "INVALID_SPEC_TOKEN")
            ).get(5, TimeUnit.SECONDS)
        }
        val inProcessTyped = ServerApiException.unwrap(inProcessEx) as? ServerApiException
        assertNotNull(inProcessTyped, "InProcess exception should unwrap to ServerApiException")
        assertEquals(ApiErrors.INVALID_TOKEN, inProcessTyped!!.code)

        // 2. TCP: create room first
        createTcpConnection().use { tcpHostConn ->
            val tcpBinding = tcpHostConn.createRoom(
                CreateRoomRequest(playerName = "HostTcpSpec")
            ).get(5, TimeUnit.SECONDS)
            val tcpRoomId = tcpBinding.roomId

            createTcpConnection().use { tcpSpecConn ->
                val tcpEx = assertThrows(ExecutionException::class.java) {
                    tcpSpecConn.resumeSpectator(
                        ResumeSpectatorRequest(roomId = tcpRoomId, spectatorId = "spec-tcp-1", resumeToken = "INVALID_SPEC_TOKEN")
                    ).get(5, TimeUnit.SECONDS)
                }
                val tcpTyped = ServerApiException.unwrap(tcpEx) as? ServerApiException
                assertNotNull(tcpTyped, "TCP exception should unwrap to ServerApiException")
                assertEquals(ApiErrors.INVALID_TOKEN, tcpTyped!!.code)

                // 3. Consistency assertion
                assertEquals(inProcessTyped.code, tcpTyped.code)
            }
        }
    }
}
