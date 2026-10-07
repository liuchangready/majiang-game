package com.majiang.server.mapping

import com.majiang.access.MeldPreviewOption
import com.majiang.access.PlayerPresentationPreview
import com.majiang.access.PlayerSnapshot
import com.majiang.access.ReceiptStatus
import com.majiang.access.RiichiPreviewOption
import com.majiang.access.SeatControlView
import com.majiang.application.ActionOption
import com.majiang.application.PlayerDecision
import com.majiang.authority.TableAuthority
import com.majiang.client.api.ProtocolViewMapper
import com.majiang.match.MatchConfig
import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.rules.FuritenStatus
import com.majiang.session.PlayerView
import com.majiang.stacking.ShuffleConfig
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.SeatController
import com.majiang.timing.MonotonicClock
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ProtocolMeldType
import com.majiang.transport.api.ProtocolPlayerSnapshot
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class EngineProtocolPreviewMapperTest {

    private class ManualClock(var current: Long = 1000L) : MonotonicClock {
        override fun nowMillis(): Long = current
    }

    private fun createBaseView(): PlayerView = PlayerView.create(
        viewerSeat = 0,
        phase = GamePhase.DISCARD,
        roundWind = 1,
        roundNumber = 1,
        honbaSticks = 0,
        riichiSticks = 0,
        dealerSeat = 0,
        currentTurnPlayer = 0,
        wallRemaining = 70,
        doraIndicators = listOf(Tile(Suit.MAN, 1, 0)),
        scores = listOf(25000, 25000, 25000, 25000),
        riichiStatus = listOf(false, false, false, false),
        discards = List(4) { emptyList() },
        melds = List(4) { emptyList() },
        handTileCounts = listOf(14, 13, 13, 13),
        myClosedTiles = listOf(Tile(Suit.MAN, 1, 0)),
        myDrawnTile = null,
        legalActions = emptyList()
    )

    @Test
    fun `test RiichiPreviewOption engine to protocol and back roundtrip`() {
        val redTile = Tile(Suit.MAN, 5, 3) // isRedFive == true
        assertTrue(redTile.isRedFive)
        val action = GameAction.DeclareRiichi(0, redTile)
        val waits = listOf(Tile(Suit.PIN, 1, 0), Tile(Suit.PIN, 4, 0))
        val furiten = FuritenStatus(discard = true, temporary = false, riichi = false)

        val engineOption = RiichiPreviewOption(
            optionId = "opt-riichi-red",
            action = action,
            waits = waits,
            furiten = furiten
        )

        val protoOption = EngineProtocolMapper.toProtocolRiichiPreviewOption(engineOption)
        assertEquals("opt-riichi-red", protoOption.optionId)
        assertEquals(redTile.tileId, protoOption.discardTile.tileId)
        assertEquals(redTile.isRedFive, protoOption.discardTile.isRed)
        assertEquals(2, protoOption.waits.size)
        assertTrue(protoOption.furiten.discard)

        val roundtripOption = EngineProtocolMapper.toEngineRiichiPreviewOption(protoOption)
        assertEquals(engineOption, roundtripOption)
        assertEquals(engineOption.discardTile, roundtripOption.discardTile)
        assertTrue(roundtripOption.discardTile.isRedFive)
    }

    @Test
    fun `test MeldPreviewOption for all meld kinds roundtrip`() {
        val chiTile1 = Tile(Suit.MAN, 1, 0)
        val chiTile2 = Tile(Suit.MAN, 2, 0)
        val called3 = Tile(Suit.MAN, 3, 0)
        val chiMeld = Meld(MeldType.CHI, listOf(chiTile1, chiTile2, called3), 3, called3)
        val chiOpt = MeldPreviewOption(
            optionId = "opt-chi",
            action = GameAction.DeclareChi(0, chiMeld),
            type = MeldType.CHI,
            tiles = chiMeld.tiles,
            handTiles = listOf(chiTile1, chiTile2),
            calledTile = called3,
            fromPlayer = 3
        )
        val protoChi = EngineProtocolMapper.toProtocolMeldPreviewOption(chiOpt)
        assertEquals(ProtocolMeldType.CHI, protoChi.type)
        assertEquals(chiOpt, EngineProtocolMapper.toEngineMeldPreviewOption(protoChi))

        // 碰
        val ponTile1 = Tile(Suit.PIN, 5, 0)
        val ponTile2 = Tile(Suit.PIN, 5, 3) // 赤五
        val calledPon = Tile(Suit.PIN, 5, 1)
        val ponOpt = MeldPreviewOption(
            optionId = "opt-pon",
            action = GameAction.DeclarePon(0, listOf(ponTile1, ponTile2)),
            type = MeldType.PON,
            tiles = listOf(ponTile1, ponTile2, calledPon),
            handTiles = listOf(ponTile1, ponTile2),
            calledTile = calledPon,
            fromPlayer = 1
        )
        val protoPon = EngineProtocolMapper.toProtocolMeldPreviewOption(ponOpt)
        assertEquals(ProtocolMeldType.PON, protoPon.type)
        val roundtripPon = EngineProtocolMapper.toEngineMeldPreviewOption(protoPon)
        assertEquals(ponOpt, roundtripPon)
        assertTrue(roundtripPon.handTiles.any { it.isRedFive })

        // 大明杠
        val k1 = Tile(Suit.SOU, 1, 0)
        val k2 = Tile(Suit.SOU, 1, 1)
        val k3 = Tile(Suit.SOU, 1, 2)
        val kCalled = Tile(Suit.SOU, 1, 3)
        val openKanOpt = MeldPreviewOption(
            optionId = "opt-open-kan",
            action = GameAction.DeclareKan(0, listOf(k1, k2, k3), MeldType.OPEN_KAN),
            type = MeldType.OPEN_KAN,
            tiles = listOf(k1, k2, k3, kCalled),
            handTiles = listOf(k1, k2, k3),
            calledTile = kCalled,
            fromPlayer = 2
        )
        val protoOpenKan = EngineProtocolMapper.toProtocolMeldPreviewOption(openKanOpt)
        assertEquals(ProtocolMeldType.OPEN_KAN, protoOpenKan.type)
        assertEquals(openKanOpt, EngineProtocolMapper.toEngineMeldPreviewOption(protoOpenKan))

        // 暗杠
        val closedKanOpt = MeldPreviewOption(
            optionId = "opt-closed-kan",
            action = GameAction.DeclareKan(0, listOf(k1, k2, k3, kCalled), MeldType.CLOSED_KAN),
            type = MeldType.CLOSED_KAN,
            tiles = listOf(k1, k2, k3, kCalled),
            handTiles = listOf(k1, k2, k3, kCalled),
            calledTile = null,
            fromPlayer = null
        )
        val protoClosedKan = EngineProtocolMapper.toProtocolMeldPreviewOption(closedKanOpt)
        assertEquals(ProtocolMeldType.CLOSED_KAN, protoClosedKan.type)
        assertEquals(closedKanOpt, EngineProtocolMapper.toEngineMeldPreviewOption(protoClosedKan))

        // 加杠
        val addedTile = Tile(Suit.PIN, 5, 2)
        val addedKanOpt = MeldPreviewOption(
            optionId = "opt-added-kan",
            action = GameAction.DeclareKan(0, listOf(ponTile1, ponTile2, calledPon, addedTile), MeldType.ADDED_KAN),
            type = MeldType.ADDED_KAN,
            tiles = listOf(ponTile1, ponTile2, calledPon, addedTile),
            handTiles = listOf(addedTile),
            calledTile = calledPon,
            addedTile = addedTile,
            fromPlayer = 1
        )
        val protoAddedKan = EngineProtocolMapper.toProtocolMeldPreviewOption(addedKanOpt)
        assertEquals(ProtocolMeldType.ADDED_KAN, protoAddedKan.type)
        val roundtripAddedKan = EngineProtocolMapper.toEngineMeldPreviewOption(protoAddedKan)
        assertEquals(addedKanOpt, roundtripAddedKan)
        assertEquals(4, (roundtripAddedKan.action as GameAction.DeclareKan).tiles.size)
        assertEquals(1, roundtripAddedKan.handTiles.size)
        assertEquals(addedTile, roundtripAddedKan.handTiles[0])
        assertEquals(addedTile, roundtripAddedKan.addedTile)
        assertEquals(calledPon, roundtripAddedKan.calledTile)
        assertEquals(1, roundtripAddedKan.fromPlayer)
        assertTrue(roundtripAddedKan.tiles.any { it.isRedFive })
    }

    @Test
    fun `test PlayerPresentationPreview and PlayerSnapshot mapping roundtrip`() {
        val riichiAction = GameAction.DeclareRiichi(0, Tile(Suit.MAN, 5, 3))
        val decision = PlayerDecision(
            roundId = "round-preview-test",
            decisionId = "dec-test-1",
            controlEpoch = 4L,
            options = listOf(ActionOption("opt-r1", riichiAction))
        )

        val riichiOpt = RiichiPreviewOption(
            optionId = "opt-r1",
            action = riichiAction,
            waits = listOf(Tile(Suit.PIN, 1, 0))
        )
        val preview = PlayerPresentationPreview(
            roundId = "round-preview-test",
            decisionId = "dec-test-1",
            controlEpoch = 4L,
            waits = listOf(Tile(Suit.PIN, 1, 0)),
            riichiOptions = listOf(riichiOpt),
            meldOptions = emptyList()
        )

        val snap = PlayerSnapshot(
            sessionId = "sess-1",
            roundId = "round-preview-test",
            syncEpoch = "epoch-1",
            cursor = 42L,
            sessionVersion = 2L,
            roundVersion = 3L,
            view = createBaseView(),
            decision = decision,
            presentationPreview = preview
        )

        val protoSnap = EngineProtocolMapper.toProtocolSnapshot(snap)
        assertNotNull(protoSnap.presentationPreview)
        assertEquals("round-preview-test", protoSnap.presentationPreview!!.roundId)
        assertEquals(1, protoSnap.presentationPreview!!.riichiOptions.size)

        val roundtripPreview = EngineProtocolMapper.toEnginePresentationPreview(protoSnap.presentationPreview!!)
        assertEquals(preview, roundtripPreview)
    }

    @Test
    fun `test full TableAuthority to server mapper to codec to client mapper end to end pipeline`() {
        val clock = ManualClock(1000L)
        val authority = TableAuthority(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            clock = clock,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.Computer(),
                SeatController.Computer(),
                SeatController.Computer()
            ),
            adminSeat = 0
        )

        try {
            // 1. 房主启动牌局
            val startReceipt = authority.start(0, clock.nowMillis())
            assertEquals(ReceiptStatus.APPLIED, startReceipt.receipt.status)

            val controls = listOf(
                SeatControlView(0, true, "HUMAN", 0L),
                SeatControlView(1, true, "COMPUTER", 0L),
                SeatControlView(2, true, "COMPUTER", 0L),
                SeatControlView(3, true, "COMPUTER", 0L)
            )

            // 2. 真实 TableAuthority 查询庄家 (0号位) 快照
            val engineSnapshot = authority.querySnapshot(0, "epoch-pipeline", 1L, controls)
            assertNotNull(engineSnapshot.roundId)
            assertNotNull(engineSnapshot.presentationPreview)

            val enginePreview = engineSnapshot.presentationPreview!!
            assertEquals(engineSnapshot.roundId, enginePreview.roundId)
            assertEquals(engineSnapshot.decision.decisionId, enginePreview.decisionId)
            assertEquals(engineSnapshot.decision.controlEpoch, enginePreview.controlEpoch)

            // 庄家发牌后持有 14 张牌（含 drawnTile），应有合法弃牌决策
            assertTrue(engineSnapshot.decision.options.isNotEmpty(), "Dealer must have decision options")

            // 3. Server 端映射至 Protocol
            val protoSnapshot = EngineProtocolMapper.toProtocolSnapshot(engineSnapshot)
            assertNotNull(protoSnapshot.presentationPreview)

            val protoPreview = protoSnapshot.presentationPreview!!
            assertEquals(enginePreview.roundId, protoPreview.roundId)
            assertEquals(enginePreview.decisionId, protoPreview.decisionId)
            assertEquals(enginePreview.controlEpoch, protoPreview.controlEpoch)
            assertEquals(enginePreview.waits.size, protoPreview.waits.size)
            assertEquals(enginePreview.riichiOptions.size, protoPreview.riichiOptions.size)
            assertEquals(enginePreview.meldOptions.size, protoPreview.meldOptions.size)

            // 4. 传输层 JSON 序列化与反序列化
            val wireJson = ApiMessageCodec.gson.toJson(protoSnapshot)
            val decodedWireSnapshot = ApiMessageCodec.gson.fromJson(wireJson, ProtocolPlayerSnapshot::class.java)
            assertNotNull(decodedWireSnapshot.presentationPreview)

            val decodedPreview = decodedWireSnapshot.presentationPreview!!
            assertEquals(protoPreview, decodedPreview)

            // 5. Client 端映射至 ViewModel
            val clientSnapshot = ProtocolViewMapper.mapSnapshot(decodedWireSnapshot)
            assertNotNull(clientSnapshot.presentationPreview)

            val clientPreview = clientSnapshot.presentationPreview!!
            assertEquals(enginePreview.roundId, clientPreview.roundId)
            assertEquals(enginePreview.decisionId, clientPreview.decisionId)
            assertEquals(enginePreview.controlEpoch, clientPreview.controlEpoch)
            assertEquals(enginePreview.waits.size, clientPreview.waits.size)
            assertEquals(enginePreview.riichiOptions.size, clientPreview.riichiOptions.size)
            assertEquals(enginePreview.meldOptions.size, clientPreview.meldOptions.size)

            // 6. 验证所有客户端展示候选项的 optionId 严格与 clientSnapshot.decision.options 一致
            val clientOptionIds = clientSnapshot.decision.options.map { it.optionId }.toSet()
            for (riichiOpt in clientPreview.riichiOptions) {
                assertTrue(riichiOpt.optionId in clientOptionIds, "Riichi optionId ${riichiOpt.optionId} must exist in decision.options")
            }
            for (meldOpt in clientPreview.meldOptions) {
                assertTrue(meldOpt.optionId in clientOptionIds, "Meld optionId ${meldOpt.optionId} must exist in decision.options")
            }
        } finally {
            authority.close()
        }
    }
}
