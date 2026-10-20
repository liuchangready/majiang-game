package com.majiang.client.mapping

import com.majiang.client.api.ShufflePlan
import com.majiang.client.api.ShuffleSelection
import com.majiang.client.api.ShuffleTarget
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolShuffleConfig
import com.majiang.transport.api.ProtocolShufflePreparation
import com.majiang.transport.api.ProtocolShufflePreparationStatus
import com.majiang.transport.api.ProtocolShuffleSelection
import com.majiang.transport.api.ProtocolShuffleTarget
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** v7 看破在客户端私有视图与请求映射中的完整往返。 */
class SeeThroughViewContractTest {

    private val selection = ProtocolShuffleSelection(
        initialTargets = setOf(ProtocolShuffleTarget.CHINITSU_MAN),
        drawTargets = setOf(ProtocolShuffleTarget.DAISANGEN),
        seeThrough = true
    )

    @Test
    fun `selection mapping keeps see through cost and equality`() {
        val view = ProtocolViewMapper.mapShuffleSelection(selection)
        assertTrue(view.seeThrough)
        assertEquals(70, view.cost())
        assertEquals(selection, ProtocolViewMapper.toProtocolShuffleSelection(view))

        val plain = ShuffleSelection()
        assertFalse(plain.seeThrough)
        assertFalse(plain.isActive)
        assertEquals(0, plain.cost())
        assertFalse(ProtocolViewMapper.toProtocolShuffleSelection(plain).seeThrough)
    }

    @Test
    fun `shuffle plan round trip keeps private see through payload`() {
        val plan = ShufflePlan(
            requestId = "see-plan",
            roundId = "round-7",
            controlEpoch = 2,
            selection = ShuffleSelection(
                initialTargets = setOf(ShuffleTarget.KOKUSHI),
                drawTargets = setOf(ShuffleTarget.DAISANGEN),
                seeThrough = true
            )
        )
        val proto = ProtocolViewMapper.toProtocolShufflePlan(plan)
        assertEquals("see-plan", proto.requestId)
        assertEquals(70, proto.selection.cost())
        assertTrue(proto.selection.seeThrough)
        assertEquals(plan.requestId, proto.requestId)
        assertEquals(plan.roundId, proto.roundId)
        assertEquals(plan.controlEpoch, proto.controlEpoch)
    }

    @Test
    fun `private preparation view exposes enabled flag and fixed ten point cost`() {
        val prep = ProtocolShufflePreparation(
            roundId = "round-7",
            status = ProtocolShufflePreparationStatus.SELECTING,
            opensAtMillis = 1_000L,
            deadlineMillis = 11_000L,
            serverNowMillis = 2_000L,
            confirmed = false,
            selection = null,
            reservedPoints = 0,
            availablePoints = 100,
            seeThroughEnabled = true,
            seeThroughCost = 10
        )
        val view = ProtocolViewMapper.mapShufflePreparation(prep)
        assertTrue(view.seeThroughEnabled)
        assertEquals(10, view.seeThroughCost)
        assertEquals("round-7", view.roundId)
        assertEquals(100, view.availablePoints)
    }

    @Test
    fun `shuffle config and capability mapping retain see through`() {
        val config = ProtocolShuffleConfig(enabled = true, seeThroughEnabled = true)
        val view = ProtocolViewMapper.mapShuffleConfig(config)
        assertTrue(view.enabled)
        assertTrue(view.seeThroughEnabled)
        assertEquals(config, ProtocolViewMapper.toProtocolShuffleConfig(view))

        val caps = ProtocolAccessCapabilities(
            protocolVersion = 7,
            tileMarkSupported = true,
            sleeveBloomSupported = true,
            beamSwapSupported = true,
            seeThroughSupported = true
        )
        val client = ProtocolViewMapper.mapCapabilities(caps)
        assertTrue(client.seeThroughSupported)
        assertEquals(7, client.protocolVersion)
        assertEquals(caps, ProtocolViewMapper.toProtocolCapabilities(client))
    }

    @Test
    fun `client capability defaults advertise current business version and disable see through`() {
        val caps = ProtocolAccessCapabilities()
        assertEquals(7, caps.protocolVersion)
        assertFalse(caps.seeThroughSupported)
        assertFalse(ProtocolViewMapper.mapCapabilities(caps).seeThroughSupported)
    }
}
