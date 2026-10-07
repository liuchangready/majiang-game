package com.majiang.session

import com.majiang.access.MeldPreviewOption
import com.majiang.access.PlayerPresentationPreview
import com.majiang.access.PlayerSnapshot
import com.majiang.access.RiichiPreviewOption
import com.majiang.application.ActionOption
import com.majiang.application.PlayerDecision
import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.rules.FuritenStatus
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

class PlayerPresentationPreviewProviderTest {

    private fun createBaseView(
        seat: Int = 0,
        phase: GamePhase = GamePhase.DISCARD,
        closedTiles: List<Tile> = emptyList(),
        drawnTile: Tile? = null,
        legalActions: List<GameAction> = emptyList(),
        melds: List<List<Meld>> = List(4) { emptyList() },
        turnPlayer: Int = seat
    ): PlayerView = PlayerView.create(
        viewerSeat = seat,
        phase = phase,
        roundWind = 1,
        roundNumber = 1,
        honbaSticks = 0,
        riichiSticks = 0,
        dealerSeat = 0,
        currentTurnPlayer = turnPlayer,
        wallRemaining = 70,
        doraIndicators = listOf(Tile(Suit.MAN, 1, 0)),
        scores = listOf(25000, 25000, 25000, 25000),
        riichiStatus = listOf(false, false, false, false),
        discards = List(4) { emptyList() },
        melds = melds,
        handTileCounts = listOf(closedTiles.size + (if (drawnTile != null) 1 else 0), 13, 13, 13),
        myClosedTiles = closedTiles,
        myDrawnTile = drawnTile,
        legalActions = legalActions
    )

    @Test
    fun `test createPreview returns null when decision roundId is null`() {
        val view = createBaseView()
        val decision = PlayerDecision(
            roundId = null,
            decisionId = "dec-1",
            controlEpoch = 0L,
            options = emptyList()
        )

        val preview = PlayerPresentationPreviewProvider.createPreview(view, decision)
        assertNull(preview, "Preview must be null when decision.roundId is null")
    }

    @Test
    fun `test tenpai waiting hand vs non-waiting hand`() {
        // 13 张听牌手牌: 123m 456m 789m 11p 23s -> 待 1s, 4s (13 张，drawnTile 为 null)
        val tenpaiClosed = listOf(
            Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0),
            Tile(Suit.MAN, 4, 0), Tile(Suit.MAN, 5, 0), Tile(Suit.MAN, 6, 0),
            Tile(Suit.MAN, 7, 0), Tile(Suit.MAN, 8, 0), Tile(Suit.MAN, 9, 0),
            Tile(Suit.PIN, 1, 0), Tile(Suit.PIN, 1, 1),
            Tile(Suit.SOU, 2, 0), Tile(Suit.SOU, 3, 0)
        )
        val viewTenpai = createBaseView(seat = 0, closedTiles = tenpaiClosed, drawnTile = null)
        val decision = PlayerDecision(
            roundId = "round-101",
            decisionId = "dec-101",
            controlEpoch = 1L,
            options = emptyList()
        )

        val previewTenpai = PlayerPresentationPreviewProvider.createPreview(viewTenpai, decision)
        assertNotNull(previewTenpai)
        assertTrue(previewTenpai!!.waits.isNotEmpty(), "Tenpai hand must have waiting tiles")
        val waitRanks = previewTenpai.waits.filter { it.suit == Suit.SOU }.map { it.rank }
        assertTrue(1 in waitRanks && 4 in waitRanks, "Expected 1s and 4s waits, got: $waitRanks")

        // 非听牌手牌
        val notenClosed = listOf(
            Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 4, 0), Tile(Suit.MAN, 7, 0),
            Tile(Suit.PIN, 2, 0), Tile(Suit.PIN, 5, 0), Tile(Suit.PIN, 8, 0),
            Tile(Suit.SOU, 3, 0), Tile(Suit.SOU, 6, 0), Tile(Suit.SOU, 9, 0),
            Tile(Suit.WIND, 1, 0), Tile(Suit.WIND, 2, 0), Tile(Suit.WIND, 3, 0),
            Tile(Suit.DRAGON, 1, 0)
        )
        val viewNoten = createBaseView(seat = 0, closedTiles = notenClosed, drawnTile = null)
        val previewNoten = PlayerPresentationPreviewProvider.createPreview(viewNoten, decision)
        assertNotNull(previewNoten)
        assertTrue(previewNoten!!.waits.isEmpty(), "Noten hand must have empty waits")
    }

    @Test
    fun `test riichi options distinguish red 5 from regular 5 and bind exact optionId`() {
        // regular5: copyIndex = 0 (isRedFive == false)
        val regular5 = Tile(Suit.MAN, 5, 0)
        // red5: copyIndex = 3 (isRedFive == true)
        val red5 = Tile(Suit.MAN, 5, 3)

        // 14 张手牌，可打出 regular5 或 red5 宣告立直
        val hand = listOf(
            Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0),
            Tile(Suit.PIN, 1, 0), Tile(Suit.PIN, 2, 0), Tile(Suit.PIN, 3, 0),
            Tile(Suit.SOU, 7, 0), Tile(Suit.SOU, 8, 0), Tile(Suit.SOU, 9, 0),
            Tile(Suit.WIND, 1, 0), Tile(Suit.WIND, 1, 1),
            Tile(Suit.MAN, 4, 0),
            regular5
        )
        val drawn = red5

        val actionRegular = GameAction.DeclareRiichi(0, regular5)
        val actionRed = GameAction.DeclareRiichi(0, red5)

        val legalActions = listOf(actionRegular, actionRed)
        val view = createBaseView(
            seat = 0,
            phase = GamePhase.DISCARD,
            closedTiles = hand,
            drawnTile = drawn,
            legalActions = legalActions,
            turnPlayer = 0
        )

        val decision = PlayerDecision(
            roundId = "round-riichi",
            decisionId = "dec-riichi-1",
            controlEpoch = 0L,
            options = listOf(
                ActionOption("opt-riichi-reg", actionRegular),
                ActionOption("opt-riichi-red", actionRed)
            )
        )

        val preview = PlayerPresentationPreviewProvider.createPreview(view, decision)
        assertNotNull(preview)
        assertEquals(2, preview!!.riichiOptions.size)

        val optReg = preview.riichiOptions.first { !it.discardTile.isRedFive }
        val optRed = preview.riichiOptions.first { it.discardTile.isRedFive }

        assertEquals("opt-riichi-reg", optReg.optionId)
        assertEquals(regular5, optReg.discardTile)
        assertFalse(optReg.discardTile.isRedFive)

        assertEquals("opt-riichi-red", optRed.optionId)
        assertEquals(red5, optRed.discardTile)
        assertTrue(optRed.discardTile.isRedFive)

        // 验证待牌非空且一致
        assertTrue(optReg.waits.isNotEmpty())
        assertEquals(optReg.waits, optRed.waits)
    }

    @Test
    fun `test meld options with chi pon and kan bind matching optionIds`() {
        val calledTile = Tile(Suit.MAN, 3, 0)
        val chiTile1 = Tile(Suit.MAN, 1, 0)
        val chiTile2 = Tile(Suit.MAN, 2, 0)
        val chiMeld = Meld(MeldType.CHI, listOf(chiTile1, chiTile2, calledTile), fromPlayer = 3, calledTile = calledTile)
        val chiAction = GameAction.DeclareChi(0, chiMeld)

        val ponTile1 = Tile(Suit.MAN, 3, 1)
        val ponTile2 = Tile(Suit.MAN, 3, 2)
        // DeclarePon 动作入参为玩家自身手牌的 2 张牌
        val ponAction = GameAction.DeclarePon(0, listOf(ponTile1, ponTile2))

        val hand = listOf(chiTile1, chiTile2, ponTile1, ponTile2, Tile(Suit.PIN, 5, 0))
        val view = createBaseView(
            seat = 0,
            phase = GamePhase.RESPONSE_WINDOW,
            closedTiles = hand,
            drawnTile = null,
            legalActions = listOf(chiAction, ponAction, GameAction.Pass(0)),
            turnPlayer = 3
        ).copy(lastDiscard = calledTile, lastDiscardPlayer = 3)

        val decision = PlayerDecision(
            roundId = "round-meld",
            decisionId = "dec-meld-1",
            controlEpoch = 2L,
            options = listOf(
                ActionOption("opt-chi-1", chiAction),
                ActionOption("opt-pon-1", ponAction),
                ActionOption("opt-pass-1", GameAction.Pass(0))
            )
        )

        val preview = PlayerPresentationPreviewProvider.createPreview(view, decision)
        assertNotNull(preview)

        assertEquals(1, preview!!.chiOptions.size)
        assertEquals("opt-chi-1", preview.chiOptions[0].optionId)
        assertEquals(MeldType.CHI, preview.chiOptions[0].type)
        assertEquals(calledTile, preview.chiOptions[0].calledTile)
        assertEquals(3, preview.chiOptions[0].fromPlayer)

        assertEquals(1, preview.ponOptions.size)
        assertEquals("opt-pon-1", preview.ponOptions[0].optionId)
        assertEquals(MeldType.PON, preview.ponOptions[0].type)
        assertEquals(calledTile, preview.ponOptions[0].calledTile)
        assertEquals(3, preview.ponOptions[0].fromPlayer)
    }

    @Test
    fun `test auto control seat with empty options produces empty preview options without fabricating actions`() {
        val regular5 = Tile(Suit.MAN, 5, 0)
        val hand = listOf(
            Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0),
            Tile(Suit.PIN, 1, 0), Tile(Suit.PIN, 2, 0), Tile(Suit.PIN, 3, 0),
            Tile(Suit.SOU, 7, 0), Tile(Suit.SOU, 8, 0), Tile(Suit.SOU, 9, 0),
            Tile(Suit.WIND, 1, 0), Tile(Suit.WIND, 1, 1),
            Tile(Suit.MAN, 4, 0), regular5
        )
        val view = createBaseView(
            seat = 1, // 托管/AI 席位
            phase = GamePhase.DISCARD,
            closedTiles = hand,
            drawnTile = Tile(Suit.MAN, 5, 1),
            legalActions = listOf(GameAction.DeclareRiichi(1, regular5)),
            turnPlayer = 1
        )

        // 托管席位的 decision.options 为空
        val decision = PlayerDecision(
            roundId = "round-auto",
            decisionId = "dec-auto-1",
            controlEpoch = 0L,
            options = emptyList()
        )

        val preview = PlayerPresentationPreviewProvider.createPreview(view, decision)
        assertNotNull(preview)
        assertTrue(preview!!.riichiOptions.isEmpty(), "Automated seat must not fabricate riichi options")
        assertTrue(preview.meldOptions.isEmpty(), "Automated seat must not fabricate meld options")
    }

    @Test
    fun `test PlayerSnapshot strictly rejects mismatched metadata and rogue optionIds`() {
        val view = createBaseView(seat = 0)
        val validAction = GameAction.DeclareRiichi(0, Tile(Suit.MAN, 1, 0))
        val validOption = ActionOption("opt-1", validAction)
        val decision = PlayerDecision("round-1", "dec-1", 10L, listOf(validOption))

        val validRiichi = RiichiPreviewOption("opt-1", validAction, emptyList())
        val validPreview = PlayerPresentationPreview(
            roundId = "round-1",
            decisionId = "dec-1",
            controlEpoch = 10L,
            riichiOptions = listOf(validRiichi)
        )

        // 正常快照构建成功
        val snap = PlayerSnapshot(
            sessionId = "sess-1",
            roundId = "round-1",
            syncEpoch = "epoch-1",
            cursor = 1L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = view,
            decision = decision,
            presentationPreview = validPreview
        )
        assertNotNull(snap)

        // 1. roundId 为 null 时禁止携带 presentationPreview
        assertThrows<IllegalArgumentException> {
            snap.copy(roundId = null)
        }

        // 2. roundId 不匹配拒绝
        assertThrows<IllegalArgumentException> {
            snap.copy(presentationPreview = validPreview.copy(roundId = "round-2"))
        }

        // 3. decisionId 不匹配拒绝
        assertThrows<IllegalArgumentException> {
            snap.copy(presentationPreview = validPreview.copy(decisionId = "dec-other"))
        }

        // 4. controlEpoch 不匹配拒绝
        assertThrows<IllegalArgumentException> {
            snap.copy(presentationPreview = validPreview.copy(controlEpoch = 999L))
        }

        // 5. optionId 不在 decision.options 中拒绝
        val rogueOptionPreview = validPreview.copy(
            riichiOptions = listOf(validRiichi.copy(optionId = "rogue-opt-id"))
        )
        assertThrows<IllegalArgumentException> {
            snap.copy(presentationPreview = rogueOptionPreview)
        }

        // 6. optionId 存在但对应动作不一致拒绝（合法物理牌但动作不同）
        val actionMismatchedPreview = validPreview.copy(
            riichiOptions = listOf(validRiichi.copy(action = GameAction.DeclareRiichi(0, Tile(Suit.PIN, 9, 0))))
        )
        assertThrows<IllegalArgumentException> {
            snap.copy(presentationPreview = actionMismatchedPreview)
        }
    }

    @Test
    fun `test deepCopy produces independent unmodifiable copies`() {
        val riichiAction = GameAction.DeclareRiichi(0, Tile(Suit.MAN, 5, 3))
        val riichiOpt = RiichiPreviewOption(
            optionId = "opt-r1",
            action = riichiAction,
            waits = listOf(Tile(Suit.PIN, 1, 0)),
            furiten = FuritenStatus()
        )

        val chiMeld = Meld(MeldType.CHI, listOf(Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0)), 1, Tile(Suit.MAN, 1, 0))
        val meldOpt = MeldPreviewOption(
            optionId = "opt-m1",
            action = GameAction.DeclareChi(0, chiMeld),
            type = MeldType.CHI,
            tiles = chiMeld.tiles,
            handTiles = listOf(Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0)),
            calledTile = Tile(Suit.MAN, 1, 0),
            fromPlayer = 1
        )

        val preview = PlayerPresentationPreview(
            roundId = "r-1",
            decisionId = "d-1",
            controlEpoch = 0L,
            waits = listOf(Tile(Suit.PIN, 1, 0)),
            riichiOptions = listOf(riichiOpt),
            meldOptions = listOf(meldOpt)
        )

        val copy = preview.deepCopy()
        assertEquals(preview, copy)
        assertEquals(preview.hashCode(), copy.hashCode())

        // 验证集合只读性
        assertThrows<UnsupportedOperationException> {
            (preview.waits as MutableList<Tile>).clear()
        }
        assertThrows<UnsupportedOperationException> {
            (preview.riichiOptions as MutableList<RiichiPreviewOption>).clear()
        }
        assertThrows<UnsupportedOperationException> {
            (preview.meldOptions as MutableList<MeldPreviewOption>).clear()
        }
    }

    @Test
    fun `test MeldPreviewOption constructor strictly rejects contradicting action or corrupted composition`() {
        val t1 = Tile(Suit.MAN, 1, 0)
        val t2 = Tile(Suit.MAN, 2, 0)
        val calledTile = Tile(Suit.MAN, 3, 0)
        val meld = Meld(MeldType.CHI, listOf(t1, t2, calledTile), 1, calledTile)
        val validChiAction = GameAction.DeclareChi(0, meld)

        // 1. CHI 动作不能声明为 PON 类型
        assertThrows<IllegalArgumentException> {
            MeldPreviewOption(
                optionId = "opt-bad-type",
                action = validChiAction,
                type = MeldType.PON,
                tiles = listOf(t1, t2, calledTile),
                handTiles = listOf(t1, t2),
                calledTile = calledTile,
                fromPlayer = 1
            )
        }

        // 2. CHI 动作手牌不能为空
        assertThrows<IllegalArgumentException> {
            MeldPreviewOption(
                optionId = "opt-empty-hand",
                action = validChiAction,
                type = MeldType.CHI,
                tiles = listOf(t1, t2, calledTile),
                handTiles = emptyList(),
                calledTile = calledTile,
                fromPlayer = 1
            )
        }

        // 3. 被鸣牌不能替换本人手牌
        assertThrows<IllegalArgumentException> {
            MeldPreviewOption(
                optionId = "opt-called-in-hand",
                action = validChiAction,
                type = MeldType.CHI,
                tiles = listOf(t1, t2, calledTile),
                handTiles = listOf(calledTile, t2),
                calledTile = calledTile,
                fromPlayer = 1
            )
        }

        // 4. 非法动作（如 Pass）不能作为鸣牌选项
        assertThrows<IllegalArgumentException> {
            MeldPreviewOption(
                optionId = "opt-pass-action",
                action = GameAction.Pass(0),
                type = MeldType.CHI,
                tiles = listOf(t1, t2, calledTile),
                handTiles = listOf(t1, t2),
                calledTile = calledTile,
                fromPlayer = 1
            )
        }

        // 5. 存在重复物理牌被拒绝
        assertThrows<IllegalArgumentException> {
            MeldPreviewOption(
                optionId = "opt-duplicate-tile",
                action = validChiAction,
                type = MeldType.CHI,
                tiles = listOf(t1, t1, calledTile),
                handTiles = listOf(t1, t1),
                calledTile = calledTile,
                fromPlayer = 1
            )
        }

        // 6. ADDED_KAN 严格要求 action.tiles 为完整 4 张且与 tiles 一致
        val p1 = Tile(Suit.PIN, 1, 0)
        val p2 = Tile(Suit.PIN, 1, 1)
        val pCalled = Tile(Suit.PIN, 1, 2)
        val pAdded = Tile(Suit.PIN, 1, 3)
        val kanAction4 = GameAction.DeclareKan(0, listOf(p1, p2, pCalled, pAdded), MeldType.ADDED_KAN)
        val kanAction1 = GameAction.DeclareKan(0, listOf(pAdded), MeldType.ADDED_KAN)

        // 拒绝非 4 张的 action.tiles
        assertThrows<IllegalArgumentException> {
            MeldPreviewOption(
                optionId = "opt-added-kan-1",
                action = kanAction1,
                type = MeldType.ADDED_KAN,
                tiles = listOf(p1, p2, pCalled, pAdded),
                handTiles = listOf(pAdded),
                calledTile = pCalled,
                addedTile = pAdded,
                fromPlayer = 1
            )
        }

        // 正常 ADDED_KAN 构造成功
        val validAddedKan = MeldPreviewOption(
            optionId = "opt-added-kan-4",
            action = kanAction4,
            type = MeldType.ADDED_KAN,
            tiles = listOf(p1, p2, pCalled, pAdded),
            handTiles = listOf(pAdded),
            calledTile = pCalled,
            addedTile = pAdded,
            fromPlayer = 1
        )
        assertNotNull(validAddedKan)
    }

    @Test
    fun `test RiichiPreviewOption constructor strictly rejects mismatched discardTile or duplicate waits`() {
        val t1 = Tile(Suit.MAN, 1, 0)
        val t2 = Tile(Suit.MAN, 2, 0)
        val validAction = GameAction.DeclareRiichi(0, t1)

        assertThrows<IllegalArgumentException> {
            RiichiPreviewOption(
                optionId = "",
                action = validAction,
                waits = emptyList()
            )
        }

        assertThrows<IllegalArgumentException> {
            RiichiPreviewOption(
                optionId = "opt-dup-waits",
                action = validAction,
                waits = listOf(t2, t2)
            )
        }
    }
}
