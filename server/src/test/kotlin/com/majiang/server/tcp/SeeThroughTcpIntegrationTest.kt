package com.majiang.server.tcp

import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolCheatEconomyConfig
import com.majiang.transport.api.ProtocolMatchConfig
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolShuffleConfig
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolShuffleSelection
import com.majiang.transport.api.ProtocolShuffleTarget
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

/** 真实 TCP：v7 看破提交、私有视图、单一幂等身份与冲突拒绝。 */
@Timeout(30)
class SeeThroughTcpIntegrationTest {

    private fun config() = ProtocolRoomConfig(
        matchConfig = ProtocolMatchConfig(
            cheatEconomy = ProtocolCheatEconomyConfig(
                enabled = true,
                initialPoints = List(4) { 100 },
                costs = mapOf("SEE_THROUGH" to 10)
            ),
            shuffleConfig = ProtocolShuffleConfig(enabled = true, seeThroughEnabled = true)
        )
    )

    @Test
    fun `see through travels over real tcp and keeps one idempotent identity`() {
        val server = TcpTableServer(TcpServerConfig(port = 0))
        server.start()
        try {
            TcpTestClient(port = server.boundPort).use { client ->
                client.sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
                val welcome = client.expectWelcome()

                val createId = "see-tcp-create"
                client.sendFrame(
                    TcpMessageCodec.toFrame(
                        TcpRequestEnvelope(
                            requestScope = welcome.lobbyScope.scope,
                            requestId = createId,
                            request = ApiRequest(
                                api = ApiTags.CREATE_ROOM,
                                requestId = createId,
                                playerName = "Host",
                                roomConfig = config()
                            )
                        )
                    )
                )
                val createRsp = client.expectResponse()
                assertTrue(createRsp.response.success)
                val roleScope = createRsp.grantedScope!!.scope
                assertNotNull(createRsp.response.snapshot)

                val readyId = "see-tcp-ready"
                client.sendFrame(
                    TcpMessageCodec.toFrame(
                        TcpRequestEnvelope(
                            requestScope = roleScope,
                            requestId = readyId,
                            request = ApiRequest(api = ApiTags.SET_READY, requestId = readyId, ready = true)
                        )
                    )
                )
                assertTrue(client.expectResponse().response.success)

                val startId = "see-tcp-start"
                client.sendFrame(
                    TcpMessageCodec.toFrame(
                        TcpRequestEnvelope(
                            requestScope = roleScope,
                            requestId = startId,
                            request = ApiRequest(api = ApiTags.START_MATCH, requestId = startId)
                        )
                    )
                )
                assertTrue(client.expectResponse().response.success)

                val snapId = "see-tcp-snapshot"
                client.sendFrame(
                    TcpMessageCodec.toFrame(
                        TcpRequestEnvelope(
                            requestScope = roleScope,
                            requestId = snapId,
                            request = ApiRequest(api = ApiTags.SNAPSHOT, requestId = snapId)
                        )
                    )
                )
                val snap = client.expectResponse().response.snapshot!!
                val preparation = snap.preparation!!
                assertTrue(preparation.seeThroughEnabled)
                assertEquals(10, preparation.seeThroughCost)
                assertNotNull(snap.roundId)

                val plan = ProtocolShufflePlan(
                    requestId = "see-tcp-plan",
                    roundId = snap.roundId!!,
                    controlEpoch = snap.decision.controlEpoch,
                    selection = ProtocolShuffleSelection(
                        initialTargets = setOf(ProtocolShuffleTarget.KOKUSHI),
                        drawTargets = setOf(ProtocolShuffleTarget.DAISANGEN),
                        seeThrough = true
                    )
                )
                val firstEnv = TcpRequestEnvelope(
                    requestScope = roleScope,
                    requestId = plan.requestId,
                    request = ApiRequest(api = ApiTags.SUBMIT_SHUFFLE_PLAN, requestId = plan.requestId, shufflePlan = plan)
                )
                client.sendFrame(TcpMessageCodec.toFrame(firstEnv))
                val first = client.expectResponse()
                assertTrue(first.response.success, first.response.error)
                assertEquals(plan.requestId, first.response.receipt!!.requestId)
                assertEquals(ProtocolReceiptStatus.RECORDED, first.response.receipt!!.status)

                // 完全相同的作用域 + requestId 必须回放原回执，不得二次预留。
                client.sendFrame(TcpMessageCodec.toFrame(firstEnv))
                val duplicate = client.expectResponse()
                assertTrue(duplicate.response.success)
                assertEquals(first.response.receipt, duplicate.response.receipt)

                val afterId = "see-tcp-after"
                client.sendFrame(
                    TcpMessageCodec.toFrame(
                        TcpRequestEnvelope(
                            requestScope = roleScope,
                            requestId = afterId,
                            request = ApiRequest(api = ApiTags.SNAPSHOT, requestId = afterId)
                        )
                    )
                )
                val after = client.expectResponse().response.snapshot!!
                assertEquals(70, after.preparation!!.reservedPoints)
                assertEquals(30, after.preparation!!.availablePoints)
                assertTrue(after.preparation!!.selection!!.seeThrough)
            }
        } finally {
            server.close()
        }
    }
}
