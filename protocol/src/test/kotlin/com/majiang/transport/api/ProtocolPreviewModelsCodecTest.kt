package com.majiang.transport.api

import com.google.gson.JsonParseException
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

class ProtocolPreviewModelsCodecTest {

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
        doraIndicators = listOf(ProtocolTile(0)), // 1万
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
    fun `test ProtocolPlayerPresentationPreview json serialization and deserialization roundtrip`() {
        val waits = listOf(
            ProtocolTile(0),  // 1万 (typeIndex 0, copyIndex 0)
            ProtocolTile(12)  // 4万 (typeIndex 3, copyIndex 0)
        )
        // 5万 赤五 (typeIndex 4, copyIndex 3 -> tileId 19)
        val redFive = ProtocolTile(19)
        assertTrue(redFive.isRed, "Tile 19 must be red 5")

        val riichiAction = ProtocolLegalAction.DeclareRiichi(0, redFive)
        val riichiOpt = ProtocolRiichiPreviewOption(
            optionId = "opt-riichi-red",
            action = riichiAction,
            waits = waits,
            furiten = ProtocolFuritenStatus(discard = false, temporary = false, riichi = false)
        )

        // 1筒, 2筒, 3筒 (typeIndex 18, 19, 20 -> tileId 72, 76, 80)
        val t1 = ProtocolTile(72)
        val t2 = ProtocolTile(76)
        val t3 = ProtocolTile(80)
        val chiMeld = ProtocolMeld(
            type = ProtocolMeldType.CHI,
            tiles = listOf(t1, t2, t3),
            fromPlayer = 3,
            calledTile = t3
        )
        val chiOpt = ProtocolMeldPreviewOption(
            optionId = "opt-chi-1",
            action = ProtocolLegalAction.DeclareChi(0, chiMeld),
            type = ProtocolMeldType.CHI,
            tiles = chiMeld.tiles,
            handTiles = listOf(t1, t2),
            calledTile = t3,
            addedTile = null,
            fromPlayer = 3
        )

        val preview = ProtocolPlayerPresentationPreview(
            roundId = "round-preview-1",
            decisionId = "dec-preview-1",
            controlEpoch = 3L,
            waits = waits,
            riichiOptions = listOf(riichiOpt),
            meldOptions = listOf(chiOpt)
        )

        val json = ApiMessageCodec.gson.toJson(preview)
        val decoded = ApiMessageCodec.gson.fromJson(json, ProtocolPlayerPresentationPreview::class.java)

        assertEquals(preview, decoded)
        assertEquals(preview.roundId, decoded.roundId)
        assertEquals(preview.decisionId, decoded.decisionId)
        assertEquals(preview.controlEpoch, decoded.controlEpoch)
        assertEquals(preview.waits, decoded.waits)
        assertEquals(preview.riichiOptions, decoded.riichiOptions)
        assertEquals(preview.meldOptions, decoded.meldOptions)
        assertEquals(1, decoded.chiOptions.size)
        assertTrue(decoded.ponOptions.isEmpty())
        assertTrue(decoded.kanOptions.isEmpty())
    }

    @Test
    fun `test ProtocolPlayerSnapshot with presentationPreview json roundtrip`() {
        // 5万 普通五 (typeIndex 4, copyIndex 0 -> tileId 16)
        val regFive = ProtocolTile(16)
        assertFalse(regFive.isRed, "Tile 16 must be regular 5")

        val riichiAction = ProtocolLegalAction.DeclareRiichi(0, regFive)
        val decision = ProtocolPlayerDecision(
            roundId = "round-snap-1",
            decisionId = "dec-snap-1",
            controlEpoch = 5L,
            options = listOf(
                ProtocolActionOption("opt-riichi-1", riichiAction)
            )
        )

        val preview = ProtocolPlayerPresentationPreview(
            roundId = "round-snap-1",
            decisionId = "dec-snap-1",
            controlEpoch = 5L,
            waits = listOf(ProtocolTile(0)),
            riichiOptions = listOf(
                ProtocolRiichiPreviewOption("opt-riichi-1", riichiAction, listOf(ProtocolTile(0)))
            ),
            meldOptions = emptyList()
        )

        val snap = ProtocolPlayerSnapshot(
            sessionId = "sess-snap-1",
            roundId = "round-snap-1",
            syncEpoch = "epoch-1",
            cursor = 100L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = createBaseProtocolView(),
            decision = decision,
            presentationPreview = preview
        )

        val json = ApiMessageCodec.gson.toJson(snap)
        val decoded = ApiMessageCodec.gson.fromJson(json, ProtocolPlayerSnapshot::class.java)

        assertEquals(snap, decoded)
        assertNotNull(decoded.presentationPreview)
        assertEquals(preview, decoded.presentationPreview)
    }

    @Test
    fun `test unknown fields rejected during preview deserialization`() {
        val invalidPreviewJson = """
            {
                "roundId": "round-1",
                "decisionId": "dec-1",
                "controlEpoch": 0,
                "waits": [],
                "riichiOptions": [],
                "meldOptions": [],
                "unknownField": "hacker"
            }
        """.trimIndent()

        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson(invalidPreviewJson, ProtocolPlayerPresentationPreview::class.java)
        }
    }

    @Test
    fun `test wire tampering rejected during ProtocolPlayerSnapshot deserialization`() {
        val regFive = ProtocolTile(16)
        val validAction = ProtocolLegalAction.DeclareRiichi(0, regFive)
        val decision = ProtocolPlayerDecision(
            roundId = "round-tamper-1",
            decisionId = "dec-tamper-1",
            controlEpoch = 1L,
            options = listOf(ProtocolActionOption("opt-1", validAction))
        )
        val validPreview = ProtocolPlayerPresentationPreview(
            roundId = "round-tamper-1",
            decisionId = "dec-tamper-1",
            controlEpoch = 1L,
            waits = emptyList(),
            riichiOptions = listOf(ProtocolRiichiPreviewOption("opt-1", validAction, emptyList())),
            meldOptions = emptyList()
        )
        val snap = ProtocolPlayerSnapshot(
            sessionId = "sess-1",
            roundId = "round-tamper-1",
            syncEpoch = "epoch-1",
            cursor = 10L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = createBaseProtocolView(),
            decision = decision,
            presentationPreview = validPreview
        )

        val jsonTree = ApiMessageCodec.gson.toJsonTree(snap).asJsonObject

        // 1. 篡改 presentationPreview.roundId
        val tamperedRoundId = jsonTree.deepCopy()
        tamperedRoundId.getAsJsonObject("presentationPreview").addProperty("roundId", "round-mismatched")
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson(tamperedRoundId, ProtocolPlayerSnapshot::class.java)
        }

        // 2. 篡改 presentationPreview.decisionId
        val tamperedDecisionId = jsonTree.deepCopy()
        tamperedDecisionId.getAsJsonObject("presentationPreview").addProperty("decisionId", "dec-mismatched")
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson(tamperedDecisionId, ProtocolPlayerSnapshot::class.java)
        }

        // 3. 篡改 presentationPreview.controlEpoch
        val tamperedEpoch = jsonTree.deepCopy()
        tamperedEpoch.getAsJsonObject("presentationPreview").addProperty("controlEpoch", 999L)
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson(tamperedEpoch, ProtocolPlayerSnapshot::class.java)
        }

        // 4. 篡改 optionId (预览注入不在 decision.options 中的 optionId)
        val tamperedOptionId = jsonTree.deepCopy()
        val riichiOpts1 = tamperedOptionId.getAsJsonObject("presentationPreview").getAsJsonArray("riichiOptions")
        riichiOpts1[0].asJsonObject.addProperty("optionId", "rogue-opt-id")
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson(tamperedOptionId, ProtocolPlayerSnapshot::class.java)
        }

        // 5. 篡改 action (预览 optionId 匹配但物理动作不匹配)
        val tamperedAction = jsonTree.deepCopy()
        val riichiOpts2 = tamperedAction.getAsJsonObject("presentationPreview").getAsJsonArray("riichiOptions")
        val mismatchedAction = ProtocolLegalAction.DeclareRiichi(0, ProtocolTile(0)) // 1万 instead of 5万
        riichiOpts2[0].asJsonObject.add("action", ApiMessageCodec.gson.toJsonTree(mismatchedAction))
        assertThrows<JsonParseException> {
            ApiMessageCodec.gson.fromJson(tamperedAction, ProtocolPlayerSnapshot::class.java)
        }
    }

    @Test
    fun `test ProtocolPlayerSnapshot constructor strictly rejects mismatched metadata and rogue options`() {
        val regFive = ProtocolTile(16)
        val validAction = ProtocolLegalAction.DeclareRiichi(0, regFive)
        val decision = ProtocolPlayerDecision(
            roundId = "round-ctor-1",
            decisionId = "dec-ctor-1",
            controlEpoch = 1L,
            options = listOf(ProtocolActionOption("opt-1", validAction))
        )
        val validPreview = ProtocolPlayerPresentationPreview(
            roundId = "round-ctor-1",
            decisionId = "dec-ctor-1",
            controlEpoch = 1L,
            waits = emptyList(),
            riichiOptions = listOf(ProtocolRiichiPreviewOption("opt-1", validAction, emptyList())),
            meldOptions = emptyList()
        )

        // 1. roundId 为 null 时不可携带 presentationPreview
        assertThrows<IllegalArgumentException> {
            ProtocolPlayerSnapshot(
                sessionId = "sess-1",
                roundId = null,
                syncEpoch = "epoch-1",
                cursor = 10L,
                sessionVersion = 1L,
                roundVersion = null,
                view = createBaseProtocolView(),
                decision = decision.copy(roundId = null),
                presentationPreview = validPreview
            )
        }

        // 2. roundId 不匹配拒绝
        assertThrows<IllegalArgumentException> {
            ProtocolPlayerSnapshot(
                sessionId = "sess-1",
                roundId = "round-ctor-1",
                syncEpoch = "epoch-1",
                cursor = 10L,
                sessionVersion = 1L,
                roundVersion = 1L,
                view = createBaseProtocolView(),
                decision = decision,
                presentationPreview = validPreview.copy(roundId = "round-other")
            )
        }

        // 3. optionId 不在 decision.options 中拒绝
        val roguePreview = validPreview.copy(
            riichiOptions = listOf(ProtocolRiichiPreviewOption("rogue-opt-id", validAction, emptyList()))
        )
        assertThrows<IllegalArgumentException> {
            ProtocolPlayerSnapshot(
                sessionId = "sess-1",
                roundId = "round-ctor-1",
                syncEpoch = "epoch-1",
                cursor = 10L,
                sessionVersion = 1L,
                roundVersion = 1L,
                view = createBaseProtocolView(),
                decision = decision,
                presentationPreview = roguePreview
            )
        }

        // 4. action 不匹配拒绝
        val diffAction = ProtocolLegalAction.DeclareRiichi(0, ProtocolTile(0))
        val diffPreview = validPreview.copy(
            riichiOptions = listOf(ProtocolRiichiPreviewOption("opt-1", diffAction, emptyList()))
        )
        assertThrows<IllegalArgumentException> {
            ProtocolPlayerSnapshot(
                sessionId = "sess-1",
                roundId = "round-ctor-1",
                syncEpoch = "epoch-1",
                cursor = 10L,
                sessionVersion = 1L,
                roundVersion = 1L,
                view = createBaseProtocolView(),
                decision = decision,
                presentationPreview = diffPreview
            )
        }
    }

    @Test
    fun `test collections immutability in Protocol preview models`() {
        val preview = ProtocolPlayerPresentationPreview(
            roundId = "round-immut",
            decisionId = "dec-immut",
            controlEpoch = 0L,
            waits = listOf(ProtocolTile(0)),
            riichiOptions = emptyList(),
            meldOptions = emptyList()
        )

        assertThrows<UnsupportedOperationException> {
            (preview.waits as MutableList<ProtocolTile>).clear()
        }
        assertThrows<UnsupportedOperationException> {
            (preview.riichiOptions as MutableList<ProtocolRiichiPreviewOption>).clear()
        }
        assertThrows<UnsupportedOperationException> {
            (preview.meldOptions as MutableList<ProtocolMeldPreviewOption>).clear()
        }
    }

    @Test
    fun `test ProtocolMeldPreviewOption constructor strictly rejects invalid compositions and contradicting actions`() {
        val t1 = ProtocolTile(72) // 1筒
        val t2 = ProtocolTile(76) // 2筒
        val calledTile = ProtocolTile(80) // 3筒
        val chiMeld = ProtocolMeld(ProtocolMeldType.CHI, listOf(t1, t2, calledTile), 3, calledTile)
        val validChiAction = ProtocolLegalAction.DeclareChi(0, chiMeld)

        // 1. CHI 动作声明为 PON 类型
        assertThrows<IllegalArgumentException> {
            ProtocolMeldPreviewOption(
                optionId = "opt-bad-type",
                action = validChiAction,
                type = ProtocolMeldType.PON,
                tiles = listOf(t1, t2, calledTile),
                handTiles = listOf(t1, t2),
                calledTile = calledTile,
                fromPlayer = 3
            )
        }

        // 2. CHI 动作手牌为空
        assertThrows<IllegalArgumentException> {
            ProtocolMeldPreviewOption(
                optionId = "opt-empty-hand",
                action = validChiAction,
                type = ProtocolMeldType.CHI,
                tiles = listOf(t1, t2, calledTile),
                handTiles = emptyList(),
                calledTile = calledTile,
                fromPlayer = 3
            )
        }

        // 3. 被鸣牌替换本人手牌
        assertThrows<IllegalArgumentException> {
            ProtocolMeldPreviewOption(
                optionId = "opt-called-in-hand",
                action = validChiAction,
                type = ProtocolMeldType.CHI,
                tiles = listOf(t1, t2, calledTile),
                handTiles = listOf(calledTile, t2),
                calledTile = calledTile,
                fromPlayer = 3
            )
        }

        // 4. Pass 动作伪装为鸣牌
        assertThrows<IllegalArgumentException> {
            ProtocolMeldPreviewOption(
                optionId = "opt-pass-action",
                action = ProtocolLegalAction.Pass(0),
                type = ProtocolMeldType.CHI,
                tiles = listOf(t1, t2, calledTile),
                handTiles = listOf(t1, t2),
                calledTile = calledTile,
                fromPlayer = 3
            )
        }

        // 5. 包含重复物理牌
        assertThrows<IllegalArgumentException> {
            ProtocolMeldPreviewOption(
                optionId = "opt-dup-tiles",
                action = validChiAction,
                type = ProtocolMeldType.CHI,
                tiles = listOf(t1, t1, calledTile),
                handTiles = listOf(t1, t1),
                calledTile = calledTile,
                fromPlayer = 3
            )
        }

        // 6. ADDED_KAN 严格要求 action.tiles 为完整 4 张且与 tiles 一致
        val p1 = ProtocolTile(0)
        val p2 = ProtocolTile(1)
        val pCalled = ProtocolTile(2)
        val pAdded = ProtocolTile(3)
        val kanAction4 = ProtocolLegalAction.DeclareKan(0, listOf(p1, p2, pCalled, pAdded), ProtocolMeldType.ADDED_KAN)
        val kanAction1 = ProtocolLegalAction.DeclareKan(0, listOf(pAdded), ProtocolMeldType.ADDED_KAN)

        // 拒绝非 4 张的 action.tiles
        assertThrows<IllegalArgumentException> {
            ProtocolMeldPreviewOption(
                optionId = "opt-added-kan-1",
                action = kanAction1,
                type = ProtocolMeldType.ADDED_KAN,
                tiles = listOf(p1, p2, pCalled, pAdded),
                handTiles = listOf(pAdded),
                calledTile = pCalled,
                addedTile = pAdded,
                fromPlayer = 1
            )
        }

        // 正常 ADDED_KAN 构造成功
        val validAddedKan = ProtocolMeldPreviewOption(
            optionId = "opt-added-kan-4",
            action = kanAction4,
            type = ProtocolMeldType.ADDED_KAN,
            tiles = listOf(p1, p2, pCalled, pAdded),
            handTiles = listOf(pAdded),
            calledTile = pCalled,
            addedTile = pAdded,
            fromPlayer = 1
        )
        assertNotNull(validAddedKan)
    }

    @Test
    fun `test wire tampering on meld composition is strictly rejected by ApiMessageCodec`() {
        val t1 = ProtocolTile(72)
        val t2 = ProtocolTile(76)
        val calledTile = ProtocolTile(80)
        val chiMeld = ProtocolMeld(ProtocolMeldType.CHI, listOf(t1, t2, calledTile), 3, calledTile)
        val validChiAction = ProtocolLegalAction.DeclareChi(0, chiMeld)
        val chiOpt = ProtocolMeldPreviewOption(
            optionId = "opt-chi-1",
            action = validChiAction,
            type = ProtocolMeldType.CHI,
            tiles = listOf(t1, t2, calledTile),
            handTiles = listOf(t1, t2),
            calledTile = calledTile,
            fromPlayer = 3
        )
        val decision = ProtocolPlayerDecision(
            roundId = "round-tamper-meld",
            decisionId = "dec-tamper-meld",
            controlEpoch = 1L,
            options = listOf(ProtocolActionOption("opt-chi-1", validChiAction))
        )
        val preview = ProtocolPlayerPresentationPreview(
            roundId = "round-tamper-meld",
            decisionId = "dec-tamper-meld",
            controlEpoch = 1L,
            waits = emptyList(),
            riichiOptions = emptyList(),
            meldOptions = listOf(chiOpt)
        )
        val snap = ProtocolPlayerSnapshot(
            sessionId = "sess-tamper",
            roundId = "round-tamper-meld",
            syncEpoch = "epoch-1",
            cursor = 1L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = createBaseProtocolView(),
            decision = decision,
            presentationPreview = preview
        )

        val json = ApiMessageCodec.encodeSnapshot(snap)

        // 1. CHI 动作篡改 type = PON -> 拒绝
        val root1 = com.google.gson.JsonParser.parseString(json).asJsonObject
        root1.getAsJsonObject("presentationPreview")
            .getAsJsonArray("meldOptions")[0].asJsonObject.addProperty("type", "PON")
        assertThrows<JsonParseException> {
            ApiMessageCodec.decodeSnapshot(root1.toString())
        }

        // 2. CHI 动作篡改 handTiles = [] -> 拒绝
        val root2 = com.google.gson.JsonParser.parseString(json).asJsonObject
        root2.getAsJsonObject("presentationPreview")
            .getAsJsonArray("meldOptions")[0].asJsonObject.add("handTiles", com.google.gson.JsonArray())
        assertThrows<JsonParseException> {
            ApiMessageCodec.decodeSnapshot(root2.toString())
        }

        // 3. 篡改 handTiles[0] = calledTile -> 拒绝
        val root3 = com.google.gson.JsonParser.parseString(json).asJsonObject
        val optObj3 = root3.getAsJsonObject("presentationPreview")
            .getAsJsonArray("meldOptions")[0].asJsonObject
        optObj3.getAsJsonArray("handTiles").set(0, optObj3.get("calledTile").deepCopy())
        assertThrows<JsonParseException> {
            ApiMessageCodec.decodeSnapshot(root3.toString())
        }
    }
}
