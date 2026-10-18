package com.majiang.presentation.game

import com.majiang.client.api.*
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * MR-08a 计时与身份委托等价与边界验证测试。
 * 验证 SleeveBloomChoiceModel 与 BeamSwapChoiceModel 委托 AuthorityCountdownTracker / DecisionIdentityGuard
 * 后的行为完全等价、时钟流逝一致、代次与候选变更语义完备。
 */
class Mr08aDelegationEquivalenceTest {

    private fun sleeveBloomSnapshot(offered: Boolean = true): GameSnapshotView {
        val first = TestViewBuilder.tile(13)
        val second = TestViewBuilder.tile(135)
        val offer = if (offered) SleeveBloomOfferView("offer-bloom", first, second, SleeveBloomDrawKind.NORMAL, 24_000, "decision-1", 3) else null
        return GameSnapshotView(
            sessionId = "session-1",
            roundId = "round-1",
            syncEpoch = "epoch-1",
            cursor = 1L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = TestViewBuilder.playerView(viewerSeat = 0, myClosedTiles = (0..12).map { TestViewBuilder.tile(it) }, myDrawnTile = first)
                .copy(phase = GamePhaseView.CHEAT_WINDOW, currentTurnPlayer = 0),
            decision = DecisionView("round-1", "decision-1", 3),
            clocks = listOf(ClockInfo(0, 1000, 24000, 20000, 1000)),
            cheatPoints = listOf(if (offered) 0 else 10, 10, 10, 10),
            controls = listOf(ControlsInfo(0, true, "HUMAN", 3)),
            sleeveBloom = SleeveBloomView(
                canApply = !offered,
                disabledReason = if (offered) "SLEEVE_BLOOM_ALREADY_PENDING" else null,
                cost = 10,
                drawKind = SleeveBloomDrawKind.NORMAL,
                decisionId = "decision-1",
                controlEpoch = 3,
                stateVersion = 1L,
                pendingOffer = offer
            )
        )
    }

    private fun beamSwapSnapshot(offered: Boolean = true): GameSnapshotView {
        val slots = (32..39).map { BeamSwapSlot.WallSlot(it) }
        val candidates = slots.mapIndexed { index, slot ->
            BeamSwapCandidateView(slot, index == 1, if (index == 1) TestViewBuilder.tile(134) else null)
        }
        val offer = if (offered) BeamSwapOfferView(
            offerId = "offer-beam",
            window = BeamSwapWindow.WALL,
            candidates = candidates,
            selectableHandTileIds = (0..12).toList(),
            deadlineMillis = 24_000,
            decisionId = "decision-1",
            controlEpoch = 3
        ) else null

        return GameSnapshotView(
            sessionId = "session-1",
            roundId = "round-1",
            syncEpoch = "epoch-1",
            cursor = 1L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = TestViewBuilder.playerView(
                viewerSeat = 0,
                myClosedTiles = (0..12).map(TestViewBuilder::tile),
                myDrawnTile = TestViewBuilder.tile(13),
                phase = GamePhaseView.CHEAT_WINDOW,
                currentTurnPlayer = 0
            ),
            decision = DecisionView("round-1", "decision-1", 3),
            clocks = listOf(ClockInfo(0, 1_000, 24_000, 20_000, 1_000)),
            cheatPoints = listOf(if (offered) 0 else 10, 10, 10, 10),
            controls = listOf(ControlsInfo(0, true, "HUMAN", 3)),
            beamSwap = BeamSwapView(
                canApply = !offered,
                disabledReason = if (offered) "BEAM_SWAP_ALREADY_PENDING" else null,
                cost = 10,
                window = BeamSwapWindow.WALL,
                decisionId = "decision-1",
                controlEpoch = 3,
                stateVersion = 1L,
                pendingOffer = offer
            )
        )
    }

    @Test
    fun `sleeve bloom delegation exposes delegates and tracks countdown precisely`() {
        var clock = 200_000L
        val model = SleeveBloomChoiceModel { clock }
        assertNotNull(model.countdownTracker)
        assertNotNull(model.identityGuard)

        val snap = sleeveBloomSnapshot(offered = true)
        model.update(snap)

        assertTrue(model.identityGuard.hasIdentity)
        assertEquals("session-1", model.identityGuard.lastSessionId)
        assertEquals("decision-1", model.identityGuard.lastDecisionId)
        assertEquals("offer-bloom", model.identityGuard.lastExtraToken)

        assertTrue(model.countdownTracker.hasActiveSample)
        assertEquals(200_000L, model.countdownTracker.sampledLocalMillis)
        assertEquals(1_000L, model.countdownTracker.sampledServerNow)
        assertEquals(24_000L, model.countdownTracker.cachedDeadline)
        assertEquals(23_000L, model.remainingMillis)

        // 步进 5 秒并在同一代次重复刷新
        clock += 5_000L
        model.update(snap)
        assertEquals(18_000L, model.remainingMillis)
        assertEquals(200_000L, model.countdownTracker.sampledLocalMillis, "刷新不重置采样基准")

        // 步进到超时
        clock += 18_000L
        assertEquals(0L, model.remainingMillis)
        assertFalse(model.canConfirm)
    }

    @Test
    fun `beam swap delegation preserves disconnected elapsed time upon reconnect`() {
        var clock = 200_000L
        val model = BeamSwapChoiceModel { clock }
        val snap = beamSwapSnapshot(offered = true)

        model.update(snap)
        assertTrue(model.selectHand(0))
        assertTrue(model.selectTarget(BeamSwapSlot.WallSlot(32)))
        assertTrue(model.canConfirm)
        assertEquals(23_000L, model.remainingMillis)

        // 掉线 7 秒
        clock += 7_000L
        model.update(snap, connected = false)
        assertFalse(model.isChoosing)
        assertNull(model.selectedHandTileId)
        assertNull(model.selectedTargetSlot)

        // 重连：保留原采样样本，消耗掉线期间的 7 秒
        clock += 3_000L
        model.update(snap, connected = true)
        assertTrue(model.isChoosing)
        assertEquals(13_000L, model.remainingMillis, "必须扣除掉线期间流逝的 7+3=10 秒")
        assertEquals(200_000L, model.countdownTracker.sampledLocalMillis)

        // 再次选择并成功生成请求
        assertTrue(model.selectHand(1))
        assertTrue(model.selectTarget(BeamSwapSlot.WallSlot(33)))
        val req = model.buildChoiceRequest("req-1", BeamSwapAction.SWAP)!!
        assertEquals("session-1", req.sessionId)
        assertEquals("decision-1", req.decisionId)
        assertEquals(1, req.handTileId)
        assertEquals(BeamSwapSlot.WallSlot(33), req.targetSlot)
    }

    @Test
    fun `decision and candidate changes reset local selection without losing offer visibility`() {
        var clock = 100_000L
        val bloomModel = SleeveBloomChoiceModel { clock }
        val bloomSnap = sleeveBloomSnapshot(offered = true)
        bloomModel.update(bloomSnap)
        bloomModel.select(SleeveBloomKeep.SECOND)
        assertEquals(SleeveBloomKeep.SECOND, bloomModel.selected)

        // 决策 ID 变更
        val newDecisionSnap = bloomSnap.copy(
            decision = bloomSnap.decision.copy(decisionId = "decision-2"),
            sleeveBloom = bloomSnap.sleeveBloom!!.copy(
                decisionId = "decision-2",
                pendingOffer = bloomSnap.sleeveBloom!!.pendingOffer!!.copy(decisionId = "decision-2")
            )
        )
        bloomModel.update(newDecisionSnap)
        assertTrue(bloomModel.isChoosing, "模态仍然由有效 offer 保持")
        assertNull(bloomModel.selected, "代次变化后必须清空本地草稿选中")
        assertFalse(bloomModel.canConfirm)

        val swapModel = BeamSwapChoiceModel { clock }
        val swapSnap = beamSwapSnapshot(offered = true)
        swapModel.update(swapSnap)
        assertTrue(swapModel.selectHand(0))
        assertTrue(swapModel.selectTarget(BeamSwapSlot.WallSlot(32)))
        assertTrue(swapModel.canConfirm)

        // 候选牌特征变更
        val alteredCandidatesSnap = swapSnap.copy(
            beamSwap = swapSnap.beamSwap!!.copy(
                pendingOffer = swapSnap.beamSwap!!.pendingOffer!!.copy(
                    candidates = swapSnap.beamSwap!!.pendingOffer!!.candidates.drop(1)
                )
            )
        )
        swapModel.update(alteredCandidatesSnap)
        assertTrue(swapModel.isChoosing)
        assertNull(swapModel.selectedHandTileId)
        assertNull(swapModel.selectedTargetSlot)
        assertFalse(swapModel.canConfirm)
    }

    @Test
    fun `cancel clears countdown tracker and choice state without losing identity baseline`() {
        val model = SleeveBloomChoiceModel { 100_000L }
        val snap = sleeveBloomSnapshot(offered = true)
        model.update(snap)
        model.select(SleeveBloomKeep.SECOND)

        model.cancel()
        assertFalse(model.isChoosing)
        assertNull(model.selected)
        assertFalse(model.countdownTracker.hasActiveSample)
        assertEquals(0L, model.remainingMillis)
        assertTrue(model.identityGuard.hasIdentity, "cancel 不应破坏快照会话基准")
    }
}
