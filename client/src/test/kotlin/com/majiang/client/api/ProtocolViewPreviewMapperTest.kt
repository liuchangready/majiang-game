package com.majiang.client.api

import com.majiang.transport.api.ProtocolFuritenStatus
import com.majiang.transport.api.ProtocolGamePhase
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolMeld
import com.majiang.transport.api.ProtocolMeldPreviewOption
import com.majiang.transport.api.ProtocolMeldType
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerPresentationPreview
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerView
import com.majiang.transport.api.ProtocolRiichiPreviewOption
import com.majiang.transport.api.ProtocolSuit
import com.majiang.transport.api.ProtocolTile
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import com.majiang.client.mapping.ProtocolViewMapper

class ProtocolViewPreviewMapperTest {

    private fun createBaseProtocolView(): ProtocolPlayerView = ProtocolPlayerView(
        viewerSeat = 0,
        phase = ProtocolGamePhase.DISCARD,
        roundWind = 1,
        roundNumber = 1,
        honbaSticks = 0,
        riichiSticks = 0,
        dealerSeat = 0,
        currentTurnPlayer = 0,
        wallRemaining = 70,
        doraIndicators = listOf(ProtocolTile(0)),
        scores = listOf(25000, 25000, 25000, 25000),
        riichiStatus = listOf(false, false, false, false),
        discards = List(4) { emptyList() },
        melds = List(4) { emptyList() },
        handTileCounts = listOf(14, 13, 13, 13),
        myClosedTiles = listOf(ProtocolTile(0)),
        myDrawnTile = null,
        legalActions = emptyList()
    )

    @Test
    fun `test RiichiPreviewOption protocol to view and back roundtrip`() {
        // 赤5万: tileId = 19 (typeIndex 4, copyIndex 3)
        val redFive = ProtocolTile(19)
        assertTrue(redFive.isRed)

        val protoAction = ProtocolLegalAction.DeclareRiichi(0, redFive)
        // 1筒 (tileId 72), 4筒 (tileId 84)
        val protoOpt = ProtocolRiichiPreviewOption(
            optionId = "opt-riichi-view",
            action = protoAction,
            waits = listOf(ProtocolTile(72), ProtocolTile(84)),
            furiten = ProtocolFuritenStatus(discard = false, temporary = true, riichi = false)
        )

        val viewOpt = ProtocolViewMapper.mapRiichiPreviewOption(protoOpt)
        assertEquals("opt-riichi-view", viewOpt.optionId)
        assertEquals(19, viewOpt.discardTile.tileId)
        assertEquals(SuitView.MAN, viewOpt.discardTile.suit)
        assertEquals(5, viewOpt.discardTile.rank)
        assertTrue(viewOpt.discardTile.isRedFive)
        assertEquals(2, viewOpt.waits.size)
        assertTrue(viewOpt.furiten.temporary)

        val roundtripProto = ProtocolViewMapper.toProtocolRiichiPreviewOption(viewOpt)
        assertEquals(protoOpt, roundtripProto)
    }

    @Test
    fun `test MeldPreviewOption protocol to view and back roundtrip`() {
        // 1索 (tileId 36), 2索 (tileId 40), 3索 (tileId 44)
        val t1 = ProtocolTile(36)
        val t2 = ProtocolTile(40)
        val calledTile = ProtocolTile(44)
        val meld = ProtocolMeld(ProtocolMeldType.CHI, listOf(t1, t2, calledTile), 2, calledTile)

        val protoMeldOpt = ProtocolMeldPreviewOption(
            optionId = "opt-meld-view",
            action = ProtocolLegalAction.DeclareChi(0, meld),
            type = ProtocolMeldType.CHI,
            tiles = meld.tiles,
            handTiles = listOf(t1, t2),
            calledTile = calledTile,
            addedTile = null,
            fromPlayer = 2
        )

        val viewMeldOpt = ProtocolViewMapper.mapMeldPreviewOption(protoMeldOpt)
        assertEquals("opt-meld-view", viewMeldOpt.optionId)
        assertEquals(MeldTypeView.CHI, viewMeldOpt.type)
        assertEquals(3, viewMeldOpt.tiles.size)
        assertEquals(2, viewMeldOpt.handTiles.size)
        assertEquals(calledTile.tileId, viewMeldOpt.calledTile?.tileId)
        assertEquals(2, viewMeldOpt.fromPlayer)

        val roundtripProto = ProtocolViewMapper.toProtocolMeldPreviewOption(viewMeldOpt)
        assertEquals(protoMeldOpt, roundtripProto)
    }

    @Test
    fun `test PlayerPresentationPreview protocol to view and back roundtrip`() {
        // 普通5万: tileId = 16
        val regFive = ProtocolTile(16)
        val protoRiichiAction = ProtocolLegalAction.DeclareRiichi(0, regFive)
        val protoRiichiOpt = ProtocolRiichiPreviewOption(
            optionId = "opt-r1",
            action = protoRiichiAction,
            waits = listOf(ProtocolTile(0))
        )

        val protoPreview = ProtocolPlayerPresentationPreview(
            roundId = "round-view-1",
            decisionId = "dec-view-1",
            controlEpoch = 0L,
            waits = listOf(ProtocolTile(0)),
            riichiOptions = listOf(protoRiichiOpt),
            meldOptions = emptyList()
        )

        val viewPreview = ProtocolViewMapper.mapPresentationPreview(protoPreview)
        assertEquals("round-view-1", viewPreview.roundId)
        assertEquals("dec-view-1", viewPreview.decisionId)
        assertEquals(0L, viewPreview.controlEpoch)
        assertEquals(1, viewPreview.waits.size)
        assertEquals(1, viewPreview.riichiOptions.size)

        val roundtripProto = ProtocolViewMapper.toProtocolPresentationPreview(viewPreview)
        assertEquals(protoPreview, roundtripProto)
    }

    @Test
    fun `test GameSnapshotView with presentationPreview mapped correctly`() {
        val regFive = ProtocolTile(16)
        val protoRiichiAction = ProtocolLegalAction.DeclareRiichi(0, regFive)
        val decision = ProtocolPlayerDecision(
            roundId = "round-view-snap",
            decisionId = "dec-view-snap",
            controlEpoch = 1L,
            options = listOf(com.majiang.transport.api.ProtocolActionOption("opt-r1", protoRiichiAction))
        )
        val protoPreview = ProtocolPlayerPresentationPreview(
            roundId = "round-view-snap",
            decisionId = "dec-view-snap",
            controlEpoch = 1L,
            waits = listOf(ProtocolTile(0)),
            riichiOptions = listOf(
                ProtocolRiichiPreviewOption("opt-r1", protoRiichiAction, listOf(ProtocolTile(0)))
            ),
            meldOptions = emptyList()
        )
        val protoSnap = ProtocolPlayerSnapshot(
            sessionId = "sess-view-1",
            roundId = "round-view-snap",
            syncEpoch = "epoch-1",
            cursor = 10L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = createBaseProtocolView(),
            decision = decision,
            presentationPreview = protoPreview
        )

        val viewSnap = ProtocolViewMapper.mapSnapshot(protoSnap)
        assertNotNull(viewSnap.presentationPreview)
        assertEquals("round-view-snap", viewSnap.presentationPreview!!.roundId)
        assertEquals("dec-view-snap", viewSnap.presentationPreview!!.decisionId)
        assertEquals(1, viewSnap.presentationPreview!!.riichiOptions.size)
        assertEquals("opt-r1", viewSnap.presentationPreview!!.riichiOptions[0].optionId)
    }

    @Test
    fun `test collections immutability on PlayerPresentationPreviewView`() {
        val preview = PlayerPresentationPreviewView(
            roundId = "round-immut",
            decisionId = "dec-immut",
            controlEpoch = 0L,
            waits = listOf(TileView(0, SuitView.MAN, 1)),
            riichiOptions = emptyList(),
            meldOptions = emptyList()
        )

        assertThrows<UnsupportedOperationException> {
            (preview.waits as MutableList<TileView>).clear()
        }
        assertThrows<UnsupportedOperationException> {
            (preview.riichiOptions as MutableList<RiichiPreviewOptionView>).clear()
        }
        assertThrows<UnsupportedOperationException> {
            (preview.meldOptions as MutableList<MeldPreviewOptionView>).clear()
        }
    }

    @Test
    fun `test MeldPreviewOptionView constructor strictly rejects contradicting action or corrupted composition`() {
        val t1 = TileView(36, SuitView.SOU, 1)
        val t2 = TileView(40, SuitView.SOU, 2)
        val calledTile = TileView(44, SuitView.SOU, 3)
        val meld = MeldView(MeldTypeView.CHI, listOf(t1, t2, calledTile), 2, calledTile)
        val validChiAction = LegalActionView.DeclareChi(0, meld)

        // 1. CHI 动作不能声明为 PON 类型
        assertThrows<IllegalArgumentException> {
            MeldPreviewOptionView(
                optionId = "opt-bad-type",
                action = validChiAction,
                type = MeldTypeView.PON,
                tiles = listOf(t1, t2, calledTile),
                handTiles = listOf(t1, t2),
                calledTile = calledTile,
                fromPlayer = 2
            )
        }

        // 2. CHI 动作手牌不能为空
        assertThrows<IllegalArgumentException> {
            MeldPreviewOptionView(
                optionId = "opt-empty-hand",
                action = validChiAction,
                type = MeldTypeView.CHI,
                tiles = listOf(t1, t2, calledTile),
                handTiles = emptyList(),
                calledTile = calledTile,
                fromPlayer = 2
            )
        }

        // 3. 被鸣牌不能替换本人手牌
        assertThrows<IllegalArgumentException> {
            MeldPreviewOptionView(
                optionId = "opt-called-in-hand",
                action = validChiAction,
                type = MeldTypeView.CHI,
                tiles = listOf(t1, t2, calledTile),
                handTiles = listOf(calledTile, t2),
                calledTile = calledTile,
                fromPlayer = 2
            )
        }

        // 4. 非法动作（如 Pass）不能作为鸣牌选项
        assertThrows<IllegalArgumentException> {
            MeldPreviewOptionView(
                optionId = "opt-pass-action",
                action = LegalActionView.Pass(0),
                type = MeldTypeView.CHI,
                tiles = listOf(t1, t2, calledTile),
                handTiles = listOf(t1, t2),
                calledTile = calledTile,
                fromPlayer = 2
            )
        }

        // 5. 存在重复物理牌被拒绝
        assertThrows<IllegalArgumentException> {
            MeldPreviewOptionView(
                optionId = "opt-duplicate-tile",
                action = validChiAction,
                type = MeldTypeView.CHI,
                tiles = listOf(t1, t1, calledTile),
                handTiles = listOf(t1, t1),
                calledTile = calledTile,
                fromPlayer = 2
            )
        }

        // 6. ADDED_KAN 严格要求 action.tiles 为完整 4 张且与 tiles 一致
        val p1 = TileView(0, SuitView.MAN, 1)
        val p2 = TileView(1, SuitView.MAN, 1)
        val pCalled = TileView(2, SuitView.MAN, 1)
        val pAdded = TileView(3, SuitView.MAN, 1)
        val kanAction4 = LegalActionView.DeclareKan(0, listOf(p1, p2, pCalled, pAdded), MeldTypeView.ADDED_KAN)
        val kanAction1 = LegalActionView.DeclareKan(0, listOf(pAdded), MeldTypeView.ADDED_KAN)

        // 拒绝非 4 张的 action.tiles
        assertThrows<IllegalArgumentException> {
            MeldPreviewOptionView(
                optionId = "opt-added-kan-1",
                action = kanAction1,
                type = MeldTypeView.ADDED_KAN,
                tiles = listOf(p1, p2, pCalled, pAdded),
                handTiles = listOf(pAdded),
                calledTile = pCalled,
                addedTile = pAdded,
                fromPlayer = 1
            )
        }

        // 正常 ADDED_KAN 构造成功
        val validAddedKan = MeldPreviewOptionView(
            optionId = "opt-added-kan-4",
            action = kanAction4,
            type = MeldTypeView.ADDED_KAN,
            tiles = listOf(p1, p2, pCalled, pAdded),
            handTiles = listOf(pAdded),
            calledTile = pCalled,
            addedTile = pAdded,
            fromPlayer = 1
        )
        assertNotNull(validAddedKan)
    }

    @Test
    fun `test RiichiPreviewOptionView constructor strictly rejects duplicate waits or blank optionId`() {
        val t1 = TileView(0, SuitView.MAN, 1)
        val validAction = LegalActionView.DeclareRiichi(0, t1)

        assertThrows<IllegalArgumentException> {
            RiichiPreviewOptionView(
                optionId = "",
                action = validAction,
                waits = emptyList()
            )
        }

        assertThrows<IllegalArgumentException> {
            RiichiPreviewOptionView(
                optionId = "opt-dup-waits",
                action = validAction,
                waits = listOf(t1, t1)
            )
        }
    }
}
