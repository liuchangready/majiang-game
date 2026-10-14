package com.majiang.client.api

import com.majiang.transport.api.ProtocolClock as ClockView
import com.majiang.transport.api.ProtocolPlayerSnapshot as PlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate as PlayerUpdate
import com.majiang.transport.api.ProtocolPublicRoundRecord as PublicRoundRecord
import com.majiang.transport.api.ProtocolPublicSessionResult as PublicSessionResult
import com.majiang.transport.api.ProtocolSeatControl as SeatControlView
import com.majiang.transport.api.ProtocolSessionView as SessionView
import com.majiang.transport.api.ProtocolActionOption as ActionOption
import com.majiang.transport.api.ProtocolPlayerDecision as PlayerDecision
import com.majiang.transport.api.ProtocolDrawReason as DrawReason
import com.majiang.transport.api.ProtocolGameEvent as GameEvent
import com.majiang.transport.api.ProtocolMeld as Meld
import com.majiang.transport.api.ProtocolMeldType as MeldType
import com.majiang.transport.api.ProtocolSuit as Suit
import com.majiang.transport.api.ProtocolTile as Tile
import com.majiang.transport.api.ProtocolDoraBreakdown as DoraBreakdown
import com.majiang.transport.api.ProtocolDoraIndicatorHit as DoraIndicatorHit
import com.majiang.transport.api.ProtocolFuBreakdown as FuBreakdown
import com.majiang.transport.api.ProtocolFuLine as FuLine
import com.majiang.transport.api.ProtocolFuritenStatus as FuritenStatus
import com.majiang.transport.api.ProtocolScoreBreakdown as ScoreBreakdown
import com.majiang.transport.api.ProtocolScoreResult
import com.majiang.server.api.RoomSummary
import com.majiang.transport.api.ProtocolPlayerView as EnginePlayerView
import com.majiang.transport.api.ProtocolShufflePreparationStatus as ShufflePreparationStatus
import com.majiang.transport.api.ProtocolShufflePreparation as EngineShufflePreparationView
import com.majiang.transport.api.ProtocolShuffleSelection as EngineShuffleSelection
import com.majiang.transport.api.ProtocolShuffleTarget as ShuffleTarget
import com.majiang.transport.api.ProtocolLegalAction as GameAction
import com.majiang.transport.api.ProtocolGamePhase as GamePhase
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import com.majiang.client.mapping.ProtocolViewMapper

class ProtocolViewMappingRegressionTest {

    @Test
    fun testTileIdRoundTripAndProperties() {
        for (tileId in 0..135) {
            val viewTile = ProtocolViewMapper.mapTile(Tile.fromTileId(tileId))

            assertEquals(tileId, viewTile.tileId)
            val typeIndex = tileId / 4
            val expectedSuit = when (typeIndex) {
                in 0..8 -> SuitView.MAN
                in 9..17 -> SuitView.SOU
                in 18..26 -> SuitView.PIN
                in 27..30 -> SuitView.WIND
                else -> SuitView.DRAGON
            }
            val expectedRank = when (typeIndex) {
                in 0..8 -> typeIndex + 1
                in 9..17 -> typeIndex - 8
                in 18..26 -> typeIndex - 17
                in 27..30 -> typeIndex - 26
                else -> typeIndex - 30
            }
            assertEquals(expectedSuit, viewTile.suit)
            assertEquals(expectedRank, viewTile.rank)
            assertEquals(tileId % 4, viewTile.copyIndex)
            assertEquals(typeIndex, viewTile.typeIndex)
            assertEquals(expectedRank == 5 && tileId % 4 == 3 && expectedSuit in setOf(SuitView.MAN, SuitView.SOU, SuitView.PIN), viewTile.isRedFive)
            assertEquals(expectedSuit in setOf(SuitView.MAN, SuitView.SOU, SuitView.PIN) && expectedRank in setOf(1, 9), viewTile.isTerminal)
            assertEquals(expectedSuit in setOf(SuitView.WIND, SuitView.DRAGON), viewTile.isHonor)
            assertEquals(viewTile.isTerminal || viewTile.isHonor, viewTile.isTerminalOrHonor)
            assertEquals(!viewTile.isTerminalOrHonor, viewTile.isSimple)
            assertEquals(!viewTile.isHonor, viewTile.isNumberTile)
            val expectedName = when (expectedSuit) {
                SuitView.MAN -> "${expectedRank}万"
                SuitView.SOU -> "${expectedRank}索"
                SuitView.PIN -> "${expectedRank}筒"
                SuitView.WIND -> listOf("東", "南", "西", "北")[expectedRank - 1]
                SuitView.DRAGON -> listOf("白", "發", "中")[expectedRank - 1]
            }
            assertEquals(expectedName, viewTile.displayName())

            // 往返重构
            val reconstructed = TileView.fromTileId(tileId)
            assertEquals(viewTile, reconstructed)
        }
    }

    @Test
    fun testMeldMapping() {
        val t1 = Tile.fromTileId(0)
        val t2 = Tile.fromTileId(4)
        val t3 = Tile.fromTileId(8)
        val engineMeld = Meld(MeldType.CHI, listOf(t1, t2, t3), 1, t1)

        val meldView = ProtocolViewMapper.mapMeld(engineMeld)
        assertEquals(MeldTypeView.CHI, meldView.type)
        assertEquals(3, meldView.tiles.size)
        assertEquals(1, meldView.fromPlayer)
        assertEquals(t1.tileId, meldView.calledTile.tileId)
        assertTrue(meldView.isOpen)
        assertTrue(meldView.isSequence)
        assertFalse(meldView.isKan)
        assertFalse(meldView.isTriplet)
    }

    @Test
    fun testGameActionAllVariantsMapping() {
        val t = Tile.fromTileId(91) // 赤五筒
        val meld = Meld(MeldType.PON, listOf(Tile.fromTileId(40), Tile.fromTileId(41), Tile.fromTileId(42)), 3, Tile.fromTileId(40))

        val actions = listOf<GameAction>(
            GameAction.StartStacking(),
            GameAction.DealComplete,
            GameAction.DrawTile(0),
            GameAction.DiscardTile(1, t),
            GameAction.DeclareChi(2, meld),
            GameAction.DeclarePon(3, listOf(t)),
            GameAction.DeclareKan(0, listOf(t), MeldType.OPEN_KAN),
            GameAction.DeclareRiichi(1, t),
            GameAction.DeclareTsumo(2),
            GameAction.DeclareRon(3),
            GameAction.Pass(1),
            GameAction.DeclareAbortiveDraw(2),
            GameAction.DeclareAgariYame(3)
        )

        val mapped = actions.map { ProtocolViewMapper.mapLegalAction(it) }
        assertEquals(13, mapped.size)
        assertTrue(mapped[0] is LegalActionView.StartStacking)
        assertTrue(mapped[1] is LegalActionView.DealComplete)
        assertTrue(mapped[2] is LegalActionView.DrawTile)
        assertTrue(mapped[3] is LegalActionView.DiscardTile)
        assertTrue(mapped[4] is LegalActionView.DeclareChi)
        assertTrue(mapped[5] is LegalActionView.DeclarePon)
        assertTrue(mapped[6] is LegalActionView.DeclareKan)
        assertTrue(mapped[7] is LegalActionView.DeclareRiichi)
        assertTrue(mapped[8] is LegalActionView.DeclareTsumo)
        assertTrue(mapped[9] is LegalActionView.DeclareRon)
        assertTrue(mapped[10] is LegalActionView.Pass)
        assertTrue(mapped[11] is LegalActionView.DeclareAbortiveDraw)
        assertTrue(mapped[12] is LegalActionView.DeclareAgariYame)

        val kanView = mapped[6] as LegalActionView.DeclareKan
        assertEquals(MeldTypeView.OPEN_KAN, kanView.kanType)
    }

    @Test
    fun testScoreBreakdownAndFuMapping() {
        val fu = FuBreakdown(
            lines = listOf(FuLine("底符", 20), FuLine("自摸", 2)),
            subtotal = 22,
            roundedUpTo = 30,
            finalFu = 30,
            specialNote = "平胡自摸"
        )
        val dora = DoraBreakdown(
            indicatorCount = 1,
            uraCount = 0,
            redCount = 1,
            totalHan = 2,
            indicatorTiles = listOf(Tile.fromTileId(0)),
            uraTiles = emptyList(),
            redTiles = listOf(Tile.fromTileId(91)),
            indicatorHits = listOf(
                DoraIndicatorHit(Tile.fromTileId(0), Tile.fromTileId(4), listOf(Tile.fromTileId(4)))
            ),
            uraHits = emptyList()
        )
        val scoreRes = ProtocolScoreResult(
            fu = 30,
            han = 3,
            basePoints = 1000,
            title = "3翻30符",
            isYakuman = false,
            yakumanMultiplier = 0,
            rawBasePoints = 1000,
            capReason = null,
            formulaText = "30符 x 2^5 = 960 -> 1000",
            rawBasePointsExact = "960"
        )
        val breakdown = ScoreBreakdown(
            yakuLines = listOf("立直" to 1, "门清自摸" to 1),
            yakumanCount = 0,
            dora = dora,
            fu = fu,
            score = scoreRes,
            paymentLines = listOf("All: 1000"),
            honbaAddition = 300,
            riichiAddition = 1000,
            totalPoints = 4300,
            uraEligible = false,
            isDealer = false,
            isTsumo = true
        )

        val view = ProtocolViewMapper.mapScoreBreakdown(breakdown)
        assertEquals(2, view.yakuLines.size)
        assertEquals("立直", view.yakuLines[0].first)
        assertEquals(30, view.fu.finalFu)
        assertEquals("平胡自摸", view.fu.specialNote)
        assertEquals(2, view.dora.totalHan)
        assertEquals(1, view.dora.indicatorHits.size)
        assertEquals(4300, view.totalPoints)
        assertTrue(view.isTsumo)
        assertFalse(view.isDealer)
    }

    @Test
    fun testPlayerSnapshotMappingFullFields() {
        val tile = Tile.fromTileId(0)
        val engineView = EnginePlayerView(
            viewerSeat = 0,
            phase = GamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 0,
            honbaSticks = 1,
            riichiSticks = 2,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 60,
            doraIndicators = listOf(tile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(true, false, false, false),
            discards = listOf(listOf(tile), emptyList(), emptyList(), emptyList()),
            melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
            handTileCounts = listOf(13, 13, 13, 13),
            myClosedTiles = listOf(tile),
            myDrawnTile = tile,
            legalActions = listOf(GameAction.DiscardTile(0, tile)),
            lastDiscard = tile,
            lastDiscardPlayer = 0,
            mySleeveTiles = listOf(tile),
            myFuriten = FuritenStatus(discard = true),
            myDiscardHistory = listOf(tile)
        )

        val decision = PlayerDecision(
            roundId = "round-1",
            decisionId = "dec-1",
            controlEpoch = 5L,
            options = listOf(ActionOption("opt-1", GameAction.DiscardTile(0, tile)))
        )

        val clocks = listOf(ClockView(0, 1000L, 4000L, 20000L, 1500L))
        val controls = listOf(SeatControlView(0, true, "HUMAN", 5L))
        val session = SessionView(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 0,
            dealerSeat = 0,
            honba = 1,
            roundRecords = listOf(
                PublicRoundRecord("round-0", "res-0", listOf(25000, 25000, 25000, 25000), listOf(0, 0, 0, 0), listOf(100, 100, 100, 100), listOf(0, 0, 0, 0))
            ),
            finalResult = PublicSessionResult(listOf(25000, 25000, 25000, 25000), listOf(0, 1, 2, 3), "NORMAL")
        )

        val prep = EngineShufflePreparationView(
            roundId = "round-1",
            status = ShufflePreparationStatus.SELECTING,
            opensAtMillis = 1000L,
            deadlineMillis = 5000L,
            serverNowMillis = 2000L,
            confirmed = false,
            selection = EngineShuffleSelection(setOf(ShuffleTarget.DAISANGEN), emptySet()),
            reservedPoints = 40,
            availablePoints = 100
        )

        val settlementEvent = GameEvent.TsumoWin(
            turnNumber = 10,
            player = 0,
            winTile = tile,
            yakuList = listOf("Menzen Tsumo"),
            han = 1,
            fu = 30,
            score = 1000
        )

        val snapshot = PlayerSnapshot(
            sessionId = "sess-1",
            roundId = "round-1",
            syncEpoch = "epoch-1",
            cursor = 42L,
            sessionVersion = 1L,
            roundVersion = 2L,
            view = engineView,
            decision = decision,
            clocks = clocks,
            cheatPoints = listOf(100, 90, 80, 70),
            session = session,
            controls = controls,
            paused = false,
            settlementRoundId = "round-1",
            settlementEvents = listOf(settlementEvent),
            preparation = prep
        )

        val viewSnap = ProtocolViewMapper.mapSnapshot(snapshot)

        assertEquals("sess-1", viewSnap.sessionId)
        assertEquals("round-1", viewSnap.roundId)
        assertEquals(42L, viewSnap.cursor)
        assertEquals(0, viewSnap.viewerSeat)
        assertEquals(GamePhaseView.DISCARD, viewSnap.view.phase)
        assertTrue(viewSnap.view.myFuriten.isFuriten)
        assertTrue(viewSnap.view.myFuriten.discard)
        assertEquals(1, viewSnap.decision.actions.size)
        assertEquals(1, viewSnap.clocks.size)
        assertEquals(4, viewSnap.cheatPoints.size)
        assertNotNull(viewSnap.session)
        assertEquals("ROUND_ACTIVE", viewSnap.session!!.lifecycle)
        assertEquals(1, viewSnap.controls.size)
        assertEquals("HUMAN", viewSnap.controls[0].role)
        assertNotNull(viewSnap.preparation)
        assertEquals(ShuffleTargetView.DAISANGEN, viewSnap.preparation!!.selection!!.initialTargets.first())

        // 验证 settlementDetail 被正确推导
        assertNotNull(viewSnap.settlementDetail)
        assertEquals("TSUMO WIN!", viewSnap.settlementDetail!!.title)
        assertEquals(0, viewSnap.settlementDetail!!.winner)

        // 验证 Update 映射
        val update = PlayerUpdate(snapshot, fromCursor = 40L, visibleEvents = listOf(settlementEvent))
        val viewUpdate = ProtocolViewMapper.mapUpdate(update)
        assertEquals(40L, viewUpdate.fromCursor)
        assertEquals(42L, viewUpdate.snapshot.cursor)
        assertEquals(1, viewUpdate.visibleEvents.size)
    }

    @Test
    fun testDefensiveCopyingCollections() {
        val mutableTiles = mutableListOf(Tile.fromTileId(0), Tile.fromTileId(4))
        val mappedTiles = ProtocolViewMapper.mapTiles(mutableTiles)

        assertEquals(2, mappedTiles.size)

        // 修改原集合不影响 mapped 集合
        mutableTiles.add(Tile.fromTileId(8))
        assertEquals(2, mappedTiles.size)

        // mapped 集合不可修改
        try {
            (mappedTiles as MutableList<TileView>).add(TileView(0, SuitView.MAN, 1))
            assertFalse(true, "Should not be able to modify unmodifiable collection")
        } catch (_: UnsupportedOperationException) {
            // pass
        }
    }
}
