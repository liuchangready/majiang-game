package com.majiang.transport.api

import com.majiang.server.api.FakeServerConnection
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class ProtocolSerializationTest {

    private val gson = ApiMessageCodec.gson

    @Test
    fun `all 136 physical tiles roundtrip cleanly with red fives preserved`() {
        for (id in 0..135) {
            val tile = ProtocolTile.fromTileId(id)
            assertEquals(id, tile.tileId)
            val json = gson.toJson(tile)
            val restored = gson.fromJson(json, ProtocolTile::class.java)
            assertEquals(tile, restored)
            assertEquals(tile.tileId, restored.tileId)
            assertEquals(tile.suit, restored.suit)
            assertEquals(tile.rank, restored.rank)
            assertEquals(tile.copyIndex, restored.copyIndex)
            assertEquals(tile.isRedFive, restored.isRedFive)
        }

        // Verify the 3 red fives specifically
        val redMan = ProtocolTile.fromTileId(4 * 4 + 3) // 5 Man copy 3
        val redSou = ProtocolTile.fromTileId(13 * 4 + 3) // 5 Sou copy 3
        val redPin = ProtocolTile.fromTileId(22 * 4 + 3) // 5 Pin copy 3
        assertTrue(redMan.isRedFive)
        assertTrue(redSou.isRedFive)
        assertTrue(redPin.isRedFive)
        assertEquals(ProtocolSuit.MAN, redMan.suit)
        assertEquals(ProtocolSuit.SOU, redSou.suit)
        assertEquals(ProtocolSuit.PIN, redPin.suit)

        val nonRed = ProtocolTile.fromTileId(4 * 4 + 0) // 5 Man copy 0
        assertFalse(nonRed.isRedFive)
    }

    @Test
    fun `all 13 legal action variants serialize and deserialize with polymorphic tag`() {
        val t1 = ProtocolTile.fromTileId(0)
        val t2 = ProtocolTile.fromTileId(1)
        val t3 = ProtocolTile.fromTileId(2)
        val t4 = ProtocolTile.fromTileId(3)
        val meld = ProtocolMeld(ProtocolMeldType.CHI, listOf(t1, t2, t3), 1, t1)

        val actions: List<ProtocolLegalAction> = listOf(
            ProtocolLegalAction.StartStacking(),
            ProtocolLegalAction.DealComplete,
            ProtocolLegalAction.DrawTile(0),
            ProtocolLegalAction.DiscardTile(0, t1),
            ProtocolLegalAction.DeclareChi(0, meld),
            ProtocolLegalAction.DeclarePon(0, listOf(t1, t2, t3)),
            ProtocolLegalAction.DeclareKan(0, listOf(t1, t2, t3, t4), ProtocolMeldType.CLOSED_KAN),
            ProtocolLegalAction.DeclareRiichi(0, t1),
            ProtocolLegalAction.DeclareTsumo(0),
            ProtocolLegalAction.DeclareRon(0),
            ProtocolLegalAction.Pass(0),
            ProtocolLegalAction.DeclareAbortiveDraw(0),
            ProtocolLegalAction.DeclareAgariYame(0)
        )

        assertEquals(13, actions.size)

        for (action in actions) {
            val json = gson.toJson(action, ProtocolLegalAction::class.java)
            assertTrue(json.contains(action.actionType), "JSON should contain actionType: $json")
            val restored = gson.fromJson(json, ProtocolLegalAction::class.java)
            assertEquals(action, restored)
            assertEquals(action.actionType, restored.actionType)
        }
    }

    @Test
    fun `all 19 game event variants serialize and deserialize with polymorphic tag`() {
        val t1 = ProtocolTile.fromTileId(0)
        val t2 = ProtocolTile.fromTileId(1)
        val t3 = ProtocolTile.fromTileId(2)
        val t4 = ProtocolTile.fromTileId(3)
        val meld = ProtocolMeld(ProtocolMeldType.PON, listOf(t1, t2, t3), 2, t1)

        val events: List<ProtocolGameEvent> = listOf(
            ProtocolGameEvent.StackingStarted(1),
            ProtocolGameEvent.WallBuilt(2, 136),
            ProtocolGameEvent.TileDealt(3, 0, t1),
            ProtocolGameEvent.DealingComplete(4),
            ProtocolGameEvent.TileDrawn(5, 0, t1, 69),
            ProtocolGameEvent.TileDiscarded(6, 0, t1, isRiichiDiscard = true, isTsumogiri = false),
            ProtocolGameEvent.ChiDeclared(7, 1, meld, 0),
            ProtocolGameEvent.PonDeclared(8, 2, meld, 1),
            ProtocolGameEvent.KanDeclared(9, 3, meld, ProtocolMeldType.OPEN_KAN),
            ProtocolGameEvent.RinshanDraw(10, 3, t4),
            ProtocolGameEvent.RiichiDeclared(11, 0, t1, isDoubleRiichi = true),
            ProtocolGameEvent.TsumoWin(12, 0, t1, listOf("Riichi", "Tsumo"), 2, 30, 2000),
            ProtocolGameEvent.RonWin(13, 0, 1, t1, listOf("Riichi", "Dora 1"), 2, 40, 2600),
            ProtocolGameEvent.DoraRevealed(14, t1, isUraDora = false),
            ProtocolGameEvent.CheatPointsChanged(19, 0, -40, 60),
            ProtocolGameEvent.DrawGame(20, ProtocolDrawReason.EXHAUSTIVE, listOf(0, 2)),
            ProtocolGameEvent.ScoreSettled(21, mapOf(1 to -2600, 0 to 2600), listOf(27600, 22400, 25000, 25000)),
            ProtocolGameEvent.RoundEnded(22, 1, 0),
            ProtocolGameEvent.MatchEnded(23, listOf(35000, 25000, 22000, 18000), 0)
        )

        assertEquals(19, events.size)

        for (event in events) {
            val json = gson.toJson(event, ProtocolGameEvent::class.java)
            assertTrue(json.contains(event.eventType), "JSON should contain eventType: $json")
            val restored = gson.fromJson(json, ProtocolGameEvent::class.java)
            assertEquals(event, restored)
            assertEquals(event.eventType, restored.eventType)
            assertEquals(event.turnNumber, restored.turnNumber)
        }
    }

    @Test
    fun `full score breakdown with yaku fu dora and payment lines roundtrip`() {
        val t1 = ProtocolTile.fromTileId(0)
        val t2 = ProtocolTile.fromTileId(4)
        val hit = ProtocolDoraIndicatorHit(t1, t2, listOf(t2))

        val dora = ProtocolDoraBreakdown(
            indicatorCount = 1,
            uraCount = 0,
            redCount = 1,
            totalHan = 2,
            indicatorTiles = listOf(t1),
            uraTiles = emptyList(),
            redTiles = listOf(t2),
            indicatorHits = listOf(hit),
            uraHits = emptyList()
        )

        val fu = ProtocolFuBreakdown(
            lines = listOf(ProtocolFuLine("Base Fu", 20), ProtocolFuLine("Closed Ron", 10)),
            subtotal = 30,
            roundedUpTo = 30,
            finalFu = 30,
            specialNote = null
        )

        val score = ProtocolScoreResult(
            fu = 30,
            han = 4,
            basePoints = 2000,
            title = "Mangan",
            isYakuman = false,
            yakumanMultiplier = 0
        )

        val breakdown = ProtocolScoreBreakdown(
            yakuLines = listOf("Riichi" to 1, "Pinfu" to 1),
            yakumanCount = 0,
            dora = dora,
            fu = fu,
            score = score,
            paymentLines = listOf("Seat 1 pays 8000"),
            honbaAddition = 0,
            riichiAddition = 1000,
            totalPoints = 9000,
            uraEligible = true,
            isDealer = false,
            isTsumo = false
        )

        val json = gson.toJson(breakdown)
        val restored = gson.fromJson(json, ProtocolScoreBreakdown::class.java)
        assertEquals(breakdown, restored)
        assertEquals(2, restored.yakuLines.size)
        assertEquals(30, restored.fu.finalFu)
        assertEquals(2, restored.dora.totalHan)
        assertEquals(9000, restored.totalPoints)

        val events = listOf<ProtocolGameEvent>(
            ProtocolGameEvent.TsumoWin(1, 0, t2, listOf("Riichi", "Dora"), 4, 30, 9000, breakdown),
            ProtocolGameEvent.RonWin(2, 1, 0, t2, listOf("Riichi"), 4, 30, 9000, breakdown),
            ProtocolGameEvent.ScoreSettled(2, mapOf(0 to -9000, 1 to 9000),
                listOf(16000, 34000, 25000, 25000)),
            ProtocolGameEvent.CheatPointsChanged(3, 2, -7, 3),
            ProtocolGameEvent.MatchEnded(4, listOf(16000, 34000, 25000, 25000), 1)
        )
        val decodedEvents = ApiMessageCodec.decodeResponse(
            ApiMessageCodec.encodeResponse(ApiResponse(events = events))).events!!
        assertEquals(events, decodedEvents)
        assertEquals(breakdown, (decodedEvents[0] as ProtocolGameEvent.TsumoWin).breakdown)
        assertEquals(breakdown, (decodedEvents[1] as ProtocolGameEvent.RonWin).breakdown)
        assertEquals(listOf(16000, 34000, 25000, 25000),
            (decodedEvents[2] as ProtocolGameEvent.ScoreSettled).newScores)
    }

    @Test
    fun `full player snapshot and update envelope roundtrip`() {
        val snap = FakeServerConnection.createDefaultSnapshot(
            sessionId = "sess-100",
            roundId = "round-5",
            cursor = 42L,
            seat = 1
        )
        val update = ProtocolPlayerUpdate(
            snapshot = snap,
            fromCursor = 40L,
            visibleEvents = listOf(ProtocolGameEvent.DealingComplete(1))
        )

        val json = ApiMessageCodec.encodePush(update, isFullSnapshot = false)
        val decoded = ApiMessageCodec.decodePushEnvelope(json)

        assertFalse(decoded.isFullSnapshot)
        assertEquals(42L, decoded.update.snapshot.cursor)
        assertEquals("sess-100", decoded.update.snapshot.sessionId)
        assertEquals(1, decoded.update.snapshot.viewerSeat)
        assertEquals(40L, decoded.update.fromCursor)
        assertEquals(1, decoded.update.visibleEvents.size)
    }

    @Test
    fun `snapshot response keeps exact physical action furiten clock and cheat balances`() {
        val redFive = ProtocolTile.fromTileId(19)
        val action = ProtocolLegalAction.DiscardTile(0, redFive)
        val base = FakeServerConnection.createDefaultSnapshot()
        val expected = base.copy(
            view = base.view.copy(
                myClosedTiles = listOf(redFive),
                legalActions = listOf(action),
                myFuriten = ProtocolFuritenStatus(discard = true, temporary = true, riichi = true)),
            decision = ProtocolPlayerDecision(base.roundId, "red-five-choice", 2,
                listOf(ProtocolActionOption("physical-19", action))),
            clocks = listOf(ProtocolClock(0, 0, 23_000, 20_000, 100)),
            cheatPoints = listOf(3, 20, 30, 40))
        val actual = ApiMessageCodec.decodeResponse(
            ApiMessageCodec.encodeResponse(ApiResponse(snapshot = expected))).snapshot!!
        assertEquals(expected, actual)
        assertEquals(19, actual.decision.options.single().let {
            (it.action as ProtocolLegalAction.DiscardTile).tile.tileId
        })
        assertTrue(actual.view.myClosedTiles.single().isRed)
        assertEquals(23_000, actual.clocks.single().deadline)
        assertEquals(listOf(3, 20, 30, 40), actual.cheatPoints)
        assertEquals(ProtocolFuritenStatus(true, true, true), actual.view.myFuriten)
    }

    @Test
    fun `defensive copying prevents external modification`() {
        val mutableScores = mutableListOf(25000, 25000, 25000, 25000)
        val config = ProtocolMatchConfig(startingScores = mutableScores)
        mutableScores[0] = 99999
        assertEquals(25000, config.startingScores[0])

        val selection = ProtocolShuffleSelection(target = ProtocolShuffleTarget.CHINITSU_MAN)
        assertEquals(ProtocolShuffleTarget.CHINITSU_MAN, selection.copy().target)
        assertEquals(selection, selection.copy())
    }

    @Test
    fun `invalid inputs trigger validation in constructor and codec`() {
        assertThrows<IllegalArgumentException> {
            ProtocolTile(136, ProtocolSuit.MAN, 1)
        }
        assertThrows<IllegalArgumentException> {
            ProtocolTile(0, ProtocolSuit.MAN, 10)
        }
        assertThrows<IllegalArgumentException> {
            ProtocolMatchConfig(playerCount = 3)
        }
        assertThrows<IllegalArgumentException> {
            ProtocolActionChoice("", "r", "d", 0L, "o")
        }
    }
}
