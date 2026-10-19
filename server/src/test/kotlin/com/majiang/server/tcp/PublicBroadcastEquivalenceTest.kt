package com.majiang.server.tcp

import com.majiang.server.room.RoomCapacityPolicy
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.UUID

/**
 * 公开流广播逐字节一致性与单次编码端到端集成测试 (PublicBroadcastEquivalenceTest, R07b-T1, T2, T5)
 */
@Timeout(60)
class PublicBroadcastEquivalenceTest {

    private fun TcpTestClient.expectFrameOfType(type: Int, timeoutMillis: Long = 5000L): TcpFrame {
        val deadline = System.currentTimeMillis() + timeoutMillis
        while (System.currentTimeMillis() < deadline) {
            val frame = receiveFrame((deadline - System.currentTimeMillis()).coerceAtLeast(10L))
            if (frame.type == type) {
                return frame
            }
        }
        throw IllegalStateException("Timeout waiting for frame type ${TcpWire.frameTypeName(type)}($type) after $timeoutMillis ms")
    }

    private fun createRoomWithHost(client: TcpTestClient): Pair<String, String> {
        client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
        val welcome = client.expectWelcome()
        val reqId = "req-create-${UUID.randomUUID()}"
        val envelope = TcpRequestEnvelope(
            requestScope = welcome.lobbyScope.scope,
            requestId = reqId,
            request = ApiRequest(
                api = ApiTags.CREATE_ROOM,
                requestId = reqId,
                playerName = "HostPlayer",
                roomConfig = ProtocolRoomConfig()
            )
        )
        client.sendFrame(TcpMessageCodec.toFrame(envelope))
        val rsp = client.expectResponse()
        assertTrue(rsp.response.success, "CREATE_ROOM must succeed")
        return Pair(rsp.response.roomId!!, rsp.grantedScope!!.scope)
    }

    private fun joinSpectatorClient(client: TcpTestClient, roomId: String, spectatorName: String): String {
        client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = 1)))
        val welcome = client.expectWelcome()
        val reqId = "req-spec-${UUID.randomUUID()}"
        val envelope = TcpRequestEnvelope(
            requestScope = welcome.lobbyScope.scope,
            requestId = reqId,
            request = ApiRequest(
                api = ApiTags.JOIN_SPECTATOR,
                requestId = reqId,
                roomId = roomId,
                spectatorName = spectatorName
            )
        )
        client.sendFrame(TcpMessageCodec.toFrame(envelope))
        val rsp = client.expectResponse()
        assertTrue(rsp.response.success, "JOIN_SPECTATOR must succeed")
        return rsp.grantedScope!!.scope
    }

    private fun setReadyByHost(client: TcpTestClient, roleScope: String) {
        val reqId = "req-ready-${UUID.randomUUID()}"
        val envelope = TcpRequestEnvelope(
            requestScope = roleScope,
            requestId = reqId,
            request = ApiRequest(api = ApiTags.SET_READY, requestId = reqId, ready = true)
        )
        client.sendFrame(TcpMessageCodec.toFrame(envelope))
        val rsp = client.expectResponse()
        assertTrue(rsp.response.success, "SET_READY must succeed")
    }

    private fun startMatchByHost(client: TcpTestClient, roleScope: String) {
        val reqId = "req-start-${UUID.randomUUID()}"
        val envelope = TcpRequestEnvelope(
            requestScope = roleScope,
            requestId = reqId,
            request = ApiRequest(api = ApiTags.START_MATCH, requestId = reqId)
        )
        client.sendFrame(TcpMessageCodec.toFrame(envelope))
        val rsp = client.expectResponse()
        assertTrue(rsp.response.success, "START_MATCH must succeed")
    }

    private fun runBroadcastEquivalenceScenario(spectatorCount: Int) {
        val server = TcpTableServer(TcpServerConfig(
            port = 0,
            capacityPolicy = RoomCapacityPolicy(maxSpectators = 64)
        ))
        server.start()

        val hostClient = TcpTestClient(port = server.boundPort)
        val spectatorClients = mutableListOf<TcpTestClient>()

        try {
            val (roomId, hostScope) = createRoomWithHost(hostClient)

            for (i in 1..spectatorCount) {
                val specClient = TcpTestClient(port = server.boundPort)
                spectatorClients.add(specClient)
                joinSpectatorClient(specClient, roomId, "Spectator-$i")
            }

            val encodeCountBefore = server.getPublicUpdateFrameCache().encodeCount

            // 房主开赛，触发公开状态变更 (LOBBY -> PLAYING/PREPARING 并产生 PUBLIC_UPDATE)
            setReadyByHost(hostClient, hostScope)
            startMatchByHost(hostClient, hostScope)

            // 全部观战者接收该次公开更新
            val receivedFrames = spectatorClients.map { client ->
                client.expectFrameOfType(TcpWire.FRAME_TYPE_PUBLIC_UPDATE)
            }

            // 断言 1: 全部观战者收到的 PUBLIC_UPDATE 帧字节数组逐字节完全一致
            val firstBytes = receivedFrames[0].toByteArray()
            for (i in 1 until receivedFrames.size) {
                val currentBytes = receivedFrames[i].toByteArray()
                assertArrayEquals(firstBytes, currentBytes, "Spectator $i frame bytes must be identical to spectator 0")
            }

            // 断言 2: 该条公开更新在服务端缓存中恰好只编码 1 次 (encodeCount 增量为 1)
            val encodeDelta = server.getPublicUpdateFrameCache().encodeCount - encodeCountBefore
            assertEquals(1L, encodeDelta, "encodeCount must increment by exactly 1 for all $spectatorCount spectators")
        } finally {
            for (spec in spectatorClients) {
                spec.close()
            }
            hostClient.close()
            server.close()
        }
    }

    @Test
    fun `R07b-T1 and T2 - 1 spectator receives public update and encodes exactly once`() {
        runBroadcastEquivalenceScenario(1)
    }

    @Test
    fun `R07b-T1 and T2 - 8 spectators receive byte-for-byte identical public update with single encode`() {
        runBroadcastEquivalenceScenario(8)
    }

    @Test
    fun `R07b-T1 and T2 - 32 spectators receive byte-for-byte identical public update with single encode`() {
        runBroadcastEquivalenceScenario(32)
    }

    @Test
    fun `R07b-T1 - spectator disconnect midway does not affect remaining spectators receiving consistent content`() {
        val server = TcpTableServer(TcpServerConfig(
            port = 0,
            capacityPolicy = RoomCapacityPolicy(maxSpectators = 64)
        ))
        server.start()

        val hostClient = TcpTestClient(port = server.boundPort)
        val spectatorClients = mutableListOf<TcpTestClient>()

        try {
            val (roomId, hostScope) = createRoomWithHost(hostClient)

            for (i in 1..4) {
                val specClient = TcpTestClient(port = server.boundPort)
                spectatorClients.add(specClient)
                joinSpectatorClient(specClient, roomId, "Spec-$i")
            }

            // 观战者 0 中途断开连接
            spectatorClients[0].close()

            val encodeCountBefore = server.getPublicUpdateFrameCache().encodeCount

            // 房主开赛
            setReadyByHost(hostClient, hostScope)
            startMatchByHost(hostClient, hostScope)

            // 剩余观战者 1, 2, 3 正常收取 PUBLIC_UPDATE 并断言内容逐字节一致
            val activeFrames = spectatorClients.subList(1, spectatorClients.size).map { client ->
                client.expectFrameOfType(TcpWire.FRAME_TYPE_PUBLIC_UPDATE)
            }

            val firstBytes = activeFrames[0].toByteArray()
            for (i in 1 until activeFrames.size) {
                assertArrayEquals(firstBytes, activeFrames[i].toByteArray())
            }

            val encodeDelta = server.getPublicUpdateFrameCache().encodeCount - encodeCountBefore
            assertEquals(1L, encodeDelta)
        } finally {
            for (spec in spectatorClients) {
                spec.close()
            }
            hostClient.close()
            server.close()
        }
    }

    @Test
    fun `R07b-T5 - room recycled and server close resets frame cache entryCount and cachedBytes to zero`() {
        val server = TcpTableServer(TcpServerConfig(
            port = 0,
            capacityPolicy = RoomCapacityPolicy(maxSpectators = 16)
        ))
        server.start()

        val hostClient = TcpTestClient(port = server.boundPort)
        val specClient = TcpTestClient(port = server.boundPort)

        try {
            val (roomId, hostScope) = createRoomWithHost(hostClient)
            joinSpectatorClient(specClient, roomId, "Spec-Cleanup")

            setReadyByHost(hostClient, hostScope)
            startMatchByHost(hostClient, hostScope)
            specClient.expectFrameOfType(TcpWire.FRAME_TYPE_PUBLIC_UPDATE)

            val cache = server.getPublicUpdateFrameCache()
            assertTrue(cache.entryCount > 0, "Cache must contain entries while session is active")
            assertTrue(cache.cachedBytes > 0L, "Cache must record non-zero bytes")

            // 房主显式 LEAVE_ROOM 导致房间 closed (房主退出直接解散)
            val leaveEnv = TcpRequestEnvelope(
                requestScope = hostScope,
                requestId = "req-leave-cleanup",
                request = ApiRequest(api = ApiTags.LEAVE_ROOM, requestId = "req-leave-cleanup")
            )
            hostClient.sendFrame(TcpMessageCodec.toFrame(leaveEnv))
            hostClient.expectResponse()

            // 验证会话条目已被释放，条目数与占用字节归零
            assertEquals(0, cache.entryCount, "entryCount must drop to 0 after room session is closed/released")
            assertEquals(0L, cache.cachedBytes, "cachedBytes must drop to 0 after room session is closed/released")
        } finally {
            specClient.close()
            hostClient.close()
            server.close()
            assertEquals(0, server.getPublicUpdateFrameCache().entryCount)
            assertEquals(0L, server.getPublicUpdateFrameCache().cachedBytes)
        }
    }
}
