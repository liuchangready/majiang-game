package com.majiang.server.inprocess

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.ServerApiException
import com.majiang.server.udp.UdpTableServer
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.udp.Cancellable
import com.majiang.transport.udp.Fragmenter
import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpFrame
import com.majiang.transport.udp.UdpPacketType
import com.majiang.transport.udp.UdpScheduler
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * 跨传输链路业务错误码一致性验证 (DualTransportErrorCodeConsistencyTest)
 *
 * 验证目标 (MR-02 第二批):
 * 1. 进程内绑定 (InProcessBinding) 与 UDP 权威房间服务 (UdpTableServer) 在相同业务失败下产生完全相同的结构化错误码 (ServerApiException.code / ApiResponse.error)。
 * 2. 覆盖场景：
 *    - ROOM_NOT_FOUND：加房不存在、重连不存在房间
 *    - INVALID_TOKEN：房间存在但 resumeToken 无效、旁观重连 token 无效
 *    - ROOM_FULL：房间 4 席已满仍尝试加入
 * 3. 验证 ServerApiException.unwrap 在进程内链路上正确提取强类型错误码。
 */
@Timeout(15)
class DualTransportErrorCodeConsistencyTest {

    private lateinit var inProcessServer: InProcessServer
    private lateinit var udpServer: UdpTableServer
    private lateinit var udpSocket: DatagramSocket
    private lateinit var fragmenter: Fragmenter
    private var udpServerPort: Int = 0
    private val seqGenerator = AtomicInteger(1)

    @BeforeEach
    fun setUp() {
        inProcessServer = InProcessServer(isSingleplayer = false)
        udpServer = UdpTableServer(bindHost = "127.0.0.1", port = 0)
        udpServer.start()
        udpServerPort = udpServer.boundPort
        udpSocket = DatagramSocket()
        fragmenter = Fragmenter(UdpClock.SYSTEM, object : UdpScheduler {
            override fun schedule(delayMillis: Long, task: Runnable): Cancellable = Cancellable { }
        })
    }

    @AfterEach
    fun tearDown() {
        try {
            udpSocket.close()
        } catch (_: Throwable) {
        }
        try {
            udpServer.close()
        } catch (_: Throwable) {
        }
        try {
            inProcessServer.close()
        } catch (_: Throwable) {
        }
    }

    private fun sendUdpRequest(request: ApiRequest, connectionId: Int = 0): ApiResponse {
        val payload = ApiMessageCodec.encodeRequest(request).toByteArray(Charsets.UTF_8)
        val frame = UdpFrame(
            packetType = UdpPacketType.REQ,
            connectionId = connectionId,
            seq = seqGenerator.getAndIncrement(),
            ackSeq = 0,
            payload = payload
        )
        val bytes = UdpFrame.encode(frame)
        val packet = DatagramPacket(bytes, bytes.size, InetAddress.getByName("127.0.0.1"), udpServerPort)
        udpSocket.send(packet)

        val deadline = System.currentTimeMillis() + 4000
        val buffer = ByteArray(4096)
        while (System.currentTimeMillis() < deadline) {
            udpSocket.soTimeout = 1000
            val recvPacket = DatagramPacket(buffer, buffer.size)
            try {
                udpSocket.receive(recvPacket)
                val rawBytes = recvPacket.data.copyOfRange(recvPacket.offset, recvPacket.offset + recvPacket.length)
                val rawFrame = UdpFrame.decode(rawBytes)
                val completeFrame = if (rawFrame.packetType == UdpPacketType.FRAG) {
                    fragmenter.reassemble(rawFrame) ?: continue
                } else {
                    rawFrame
                }
                if (completeFrame.packetType == UdpPacketType.RSP) {
                    val json = String(completeFrame.payload, Charsets.UTF_8)
                    val resp = ApiMessageCodec.decodeResponse(json)
                    if (resp.requestId == request.requestId) {
                        return resp
                    }
                }
            } catch (_: SocketTimeoutException) {
            }
        }
        throw SocketTimeoutException("Timed out waiting for response to ${request.requestId}")
    }

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

        // 2. UDP transport
        val udpReq = ApiRequest(
            api = ApiTags.JOIN_ROOM,
            requestId = "req-join-not-found",
            roomId = nonExistentRoomId,
            playerName = "PlayerUdp"
        )
        val udpResp = sendUdpRequest(udpReq)
        assertFalse(udpResp.success)
        assertEquals(ApiErrors.ROOM_NOT_FOUND, udpResp.error)

        // 3. Consistency assertion
        assertEquals(inProcessTyped.code, udpResp.error)
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

        // 2. UDP transport
        val udpReq = ApiRequest(
            api = ApiTags.RESUME_SESSION,
            requestId = "req-resume-not-found",
            roomId = nonExistentRoomId,
            token = "fake_token"
        )
        val udpResp = sendUdpRequest(udpReq)
        assertFalse(udpResp.success)
        assertEquals(ApiErrors.ROOM_NOT_FOUND, udpResp.error)

        // 3. Consistency assertion
        assertEquals(inProcessTyped.code, udpResp.error)
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

        // 2. UDP: create room first
        val udpCreateReq = ApiRequest(
            api = ApiTags.CREATE_ROOM,
            requestId = "req-create-room-udp",
            playerName = "HostUdp"
        )
        val udpCreateResp = sendUdpRequest(udpCreateReq)
        val udpRoomId = udpCreateResp.roomId
        assertNotNull(udpRoomId)

        // UDP: resume with bad token
        val udpResumeReq = ApiRequest(
            api = ApiTags.RESUME_SESSION,
            requestId = "req-resume-bad-token",
            roomId = udpRoomId,
            token = "INVALID_TOKEN_XYZ"
        )
        val udpResumeResp = sendUdpRequest(udpResumeReq)
        assertFalse(udpResumeResp.success)
        assertEquals(ApiErrors.INVALID_TOKEN, udpResumeResp.error)

        // 3. Consistency assertion
        assertEquals(inProcessTyped.code, udpResumeResp.error)
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

        // 2. UDP: create room and fill 4 seats
        val udpCreateResp = sendUdpRequest(
            ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "req-udp-create-full", playerName = "HostUdp")
        )
        val udpRoomId = udpCreateResp.roomId!!

        // Join seats 1, 2, 3
        sendUdpRequest(ApiRequest(api = ApiTags.JOIN_ROOM, requestId = "req-udp-j1", roomId = udpRoomId, playerName = "Udp1", preferSeat = 1))
        sendUdpRequest(ApiRequest(api = ApiTags.JOIN_ROOM, requestId = "req-udp-j2", roomId = udpRoomId, playerName = "Udp2", preferSeat = 2))
        sendUdpRequest(ApiRequest(api = ApiTags.JOIN_ROOM, requestId = "req-udp-j3", roomId = udpRoomId, playerName = "Udp3", preferSeat = 3))

        // 5th player attempts to join
        val udpFullResp = sendUdpRequest(
            ApiRequest(api = ApiTags.JOIN_ROOM, requestId = "req-udp-j4", roomId = udpRoomId, playerName = "Udp4")
        )
        assertFalse(udpFullResp.success)
        assertEquals(ApiErrors.ROOM_FULL, udpFullResp.error)

        // 3. Consistency assertion
        assertEquals(inProcessTyped.code, udpFullResp.error)
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

        // 2. UDP: create room first
        val udpCreateResp = sendUdpRequest(
            ApiRequest(api = ApiTags.CREATE_ROOM, requestId = "req-udp-create-spec", playerName = "HostUdpSpec")
        )
        val udpRoomId = udpCreateResp.roomId!!

        // UDP: resume spectator with bad token
        val udpResumeSpecResp = sendUdpRequest(
            ApiRequest(
                api = ApiTags.RESUME_SPECTATOR,
                requestId = "req-udp-spec-bad-token",
                roomId = udpRoomId,
                token = "INVALID_SPEC_TOKEN",
                spectatorId = "spec-udp-1"
            )
        )
        assertFalse(udpResumeSpecResp.success)
        assertEquals(ApiErrors.INVALID_TOKEN, udpResumeSpecResp.error)

        // 3. Consistency assertion
        assertEquals(inProcessTyped.code, udpResumeSpecResp.error)
    }
}
