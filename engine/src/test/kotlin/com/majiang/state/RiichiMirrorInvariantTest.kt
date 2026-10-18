package com.majiang.state

import com.majiang.application.ApplicationCoordinator
import com.majiang.event.*
import com.majiang.model.*
import com.majiang.testing.TestTableBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class RiichiMirrorInvariantTest {

    private fun tiles(suit: Suit, ranks: String): List<Tile> {
        val copies = mutableMapOf<Int, Int>()
        return ranks.map { c ->
            val r = c.digitToInt()
            Tile(suit, r, copies.getOrDefault(r, 0).also { copies[r] = it + 1 })
        }
    }

    private fun waiting(): List<Tile> =
        tiles(Suit.DRAGON, "111") +
        tiles(Suit.MAN, "234") +
        tiles(Suit.PIN, "678") +
        tiles(Suit.SOU, "2256")

    fun assertRiichiInvariants(s: GameState) {
        for (i in 0 until 4) {
            assertEquals(s.riichiDeclared[i], s.players[i].riichiDeclared, "INV1 seat $i: riichiDeclared")
            assertEquals(s.riichiTurn[i], s.players[i].riichiTurn, "INV2 seat $i: riichiTurn")
            assertEquals(s.ippatsuActive[i], s.players[i].ippatsu, "INV3 seat $i: ippatsu")
            if (s.riichiDeclared[i]) {
                assertTrue(s.riichiTurn[i] >= 0, "INV4 seat $i: riichiTurn >= 0 when riichiDeclared")
            }
            if (s.doubleRiichiDeclared[i]) {
                assertTrue(s.riichiDeclared[i], "INV5 seat $i: riichiDeclared when doubleRiichiDeclared")
            }
            if (s.ippatsuActive[i]) {
                assertTrue(s.riichiDeclared[i], "INV6 seat $i: riichiDeclared when ippatsuActive")
            }
        }
    }

    @Test
    fun `scenario 1 - real riichi flow verifies invariants and event flag consistency`() {
        val discard = Tile(Suit.WIND, 1)
        val s = TestTableBuilder.create(
            hands = mapOf(0 to waiting() + discard),
            phase = GamePhase.DISCARD,
            currentPlayer = 0
        )
        assertRiichiInvariants(s)

        val bus = GameEventBus()
        val coordinator = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false, autoRiichiDiscard = false)
        coordinator.initializeMatch(0)

        val action = GameAction.DeclareRiichi(0, discard)
        assertTrue(action in coordinator.legalActions(0), "DeclareRiichi should be legal for tenpai hand")
        val result = coordinator.handleAction(action)
        assertTrue(result is ActionResult.Accepted, "DeclareRiichi should be accepted: $result")

        val post = coordinator.activeRound!!.readState()
        val riichiEvent = (result as ActionResult.Accepted).events.filterIsInstance<GameEvent.RiichiDeclared>().single()
        assertEquals(0, riichiEvent.player)
        assertEquals(discard, riichiEvent.discardTile)
        assertEquals(post.doubleRiichiDeclared[0], riichiEvent.isDoubleRiichi, "doubleRiichiDeclared must match event flag")

        assertTrue(post.riichiDeclared[0])
        assertTrue(post.players[0].riichiDeclared)
        assertEquals(riichiEvent.turnNumber, post.riichiTurn[0])
        assertEquals(riichiEvent.turnNumber, post.players[0].riichiTurn)
        assertTrue(post.ippatsuActive[0])
        assertTrue(post.players[0].ippatsu)

        assertRiichiInvariants(post)
        assertDoesNotThrow { post.tilePool.assertConservation() }
    }

    @Test
    fun `scenario 1b - double riichi flow verifies doubleRiichiDeclared is true and matches event`() {
        val discard = Tile(Suit.WIND, 1)
        val s = TestTableBuilder.create(
            hands = mapOf(0 to waiting() + discard),
            phase = GamePhase.DISCARD,
            currentPlayer = 0
        )
        s.isFirstUninterruptedGo = true
        s.players[0].discards.clear()
        assertRiichiInvariants(s)

        val bus = GameEventBus()
        val coordinator = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false, autoRiichiDiscard = false)
        coordinator.initializeMatch(0)

        val action = GameAction.DeclareRiichi(0, discard)
        val result = coordinator.handleAction(action)
        assertTrue(result is ActionResult.Accepted)

        val post = coordinator.activeRound!!.readState()
        val riichiEvent = (result as ActionResult.Accepted).events.filterIsInstance<GameEvent.RiichiDeclared>().single()
        assertTrue(riichiEvent.isDoubleRiichi, "Should be double riichi")
        assertTrue(post.doubleRiichiDeclared[0], "doubleRiichiDeclared must be true")
        assertEquals(post.doubleRiichiDeclared[0], riichiEvent.isDoubleRiichi)

        assertRiichiInvariants(post)
        assertDoesNotThrow { post.tilePool.assertConservation() }
    }

    @Test
    fun `scenario 2 - opponent pon after riichi clears ippatsu on both sides for all seats`() {
        val riichiDiscard = Tile(Suit.WIND, 1, 0)
        val p1PonTiles = listOf(Tile(Suit.WIND, 1, 1), Tile(Suit.WIND, 1, 2))
        val s = TestTableBuilder.create(
            hands = mapOf(
                0 to waiting() + riichiDiscard,
                1 to p1PonTiles
            ),
            phase = GamePhase.DISCARD,
            currentPlayer = 0
        )
        val bus = GameEventBus()
        val coordinator = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false, autoRiichiDiscard = false)
        coordinator.initializeMatch(0)

        val riichiResult = coordinator.handleAction(GameAction.DeclareRiichi(0, riichiDiscard))
        assertTrue(riichiResult is ActionResult.Accepted)

        var post = coordinator.activeRound!!.readState()
        assertTrue(post.ippatsuActive[0])
        assertTrue(post.players[0].ippatsu)
        assertRiichiInvariants(post)

        assertEquals(GamePhase.RESPONSE_WINDOW, post.phase)
        val ponAction = GameAction.DeclarePon(1, p1PonTiles)
        assertTrue(ponAction in coordinator.legalActions(1), "DeclarePon should be legal for seat 1")

        val ponResult = coordinator.handleAction(ponAction)
        assertTrue(ponResult is ActionResult.Accepted, "DeclarePon should be accepted: $ponResult")

        post = coordinator.activeRound!!.readState()
        for (seat in 0 until 4) {
            assertFalse(post.ippatsuActive[seat], "ippatsuActive[$seat] must be cleared after Pon")
            assertFalse(post.players[seat].ippatsu, "players[$seat].ippatsu must be cleared after Pon")
        }
        assertRiichiInvariants(post)
        assertDoesNotThrow { post.tilePool.assertConservation() }
    }

    @Test
    fun `scenario 3 - closed kan clears ippatsu on both sides for all seats`() {
        val fourth = Tile(Suit.DRAGON, 1, 3)
        val miss = Tile(Suit.WIND, 4)
        val s = TestTableBuilder.create(
            hands = mapOf(0 to waiting()),
            wallPrefix = listOf(fourth),
            deadWallPrefix = listOf(miss),
            phase = GamePhase.DRAW,
            currentPlayer = 0
        )
        s.declareRiichi(0, s.turnNumber, false)
        assertRiichiInvariants(s)

        val bus = GameEventBus()
        val coordinator = ApplicationCoordinator.createForTest(s, bus, autoPlayAi = false, autoRiichiDiscard = false)
        coordinator.initializeMatch(0)

        val drawResult = coordinator.handleAction(GameAction.DrawTile(0))
        assertTrue(drawResult is ActionResult.Accepted)
        val post1 = coordinator.activeRound!!.readState()
        assertEquals(fourth, post1.players[0].drawnTile)

        val kanAction = coordinator.legalActions(0).filterIsInstance<GameAction.DeclareKan>().single()
        assertEquals(MeldType.CLOSED_KAN, kanAction.kanType)

        val kanResult = coordinator.handleAction(kanAction)
        assertTrue(kanResult is ActionResult.Accepted)

        val post2 = coordinator.activeRound!!.readState()
        for (seat in 0 until 4) {
            assertFalse(post2.ippatsuActive[seat], "ippatsuActive[$seat] must be cleared after CLOSED_KAN")
            assertFalse(post2.players[seat].ippatsu, "players[$seat].ippatsu must be cleared after CLOSED_KAN")
        }
        assertRiichiInvariants(post2)
        assertDoesNotThrow { post2.tilePool.assertConservation() }
    }

    @Test
    fun `scenario 4 - resetForNewRound restores all riichi fields to defaults and invariants hold`() {
        val s = GameState()
        s.declareRiichi(0, 3, true)
        s.declareRiichi(2, 7, false)
        s.setIppatsu(2, false)
        assertRiichiInvariants(s)

        s.resetForNewRound()

        for (i in 0 until 4) {
            assertFalse(s.riichiDeclared[i], "riichiDeclared[$i] default false")
            assertFalse(s.doubleRiichiDeclared[i], "doubleRiichiDeclared[$i] default false")
            assertEquals(-1, s.riichiTurn[i], "riichiTurn[$i] default -1")
            assertFalse(s.ippatsuActive[i], "ippatsuActive[$i] default false")
            assertFalse(s.players[i].riichiDeclared, "players[$i].riichiDeclared default false")
            assertEquals(-1, s.players[i].riichiTurn, "players[$i].riichiTurn default -1")
            assertFalse(s.players[i].ippatsu, "players[$i].ippatsu default false")
        }
        assertRiichiInvariants(s)
        assertDoesNotThrow { s.tilePool.assertConservation() }
    }

    @Test
    fun `scenario 5 - copyFrom and deepCopy roundtrip preserves invariants across source and copies`() {
        val s1 = GameState()
        s1.declareRiichi(1, 5, true)
        s1.declareRiichi(3, 8, false)
        assertRiichiInvariants(s1)

        val s2 = s1.deepCopy()
        assertRiichiInvariants(s1)
        assertRiichiInvariants(s2)

        for (i in 0 until 4) {
            assertEquals(s1.riichiDeclared[i], s2.riichiDeclared[i])
            assertEquals(s1.doubleRiichiDeclared[i], s2.doubleRiichiDeclared[i])
            assertEquals(s1.riichiTurn[i], s2.riichiTurn[i])
            assertEquals(s1.ippatsuActive[i], s2.ippatsuActive[i])
            assertEquals(s1.players[i].riichiDeclared, s2.players[i].riichiDeclared)
            assertEquals(s1.players[i].riichiTurn, s2.players[i].riichiTurn)
            assertEquals(s1.players[i].ippatsu, s2.players[i].ippatsu)
        }

        s2.setIppatsu(1, false)
        assertRiichiInvariants(s2)
        assertTrue(s1.ippatsuActive[1], "Source s1 must be independent of s2 mutation")
        assertFalse(s2.ippatsuActive[1], "Copy s2 must reflect setIppatsu(1, false)")

        val s3 = GameState()
        s3.copyFrom(s2)
        assertRiichiInvariants(s2)
        assertRiichiInvariants(s3)
        assertFalse(s3.ippatsuActive[1])
        assertFalse(s3.players[1].ippatsu)

        val handCopy = s1.players[1].deepCopy()
        assertEquals(s1.players[1].riichiDeclared, handCopy.riichiDeclared)
        assertEquals(s1.players[1].riichiTurn, handCopy.riichiTurn)
        assertEquals(s1.players[1].ippatsu, handCopy.ippatsu)
    }

    @Test
    fun `scenario 6 - direct entry points enforce invariants, sync mirrors, and validate seat range`() {
        val s = GameState()

        s.declareRiichi(1, 5, true)
        assertTrue(s.riichiDeclared[1])
        assertTrue(s.players[1].riichiDeclared)
        assertEquals(5, s.riichiTurn[1])
        assertEquals(5, s.players[1].riichiTurn)
        assertTrue(s.doubleRiichiDeclared[1])
        assertTrue(s.ippatsuActive[1])
        assertTrue(s.players[1].ippatsu)
        assertRiichiInvariants(s)

        s.setIppatsu(1, false)
        assertFalse(s.ippatsuActive[1])
        assertFalse(s.players[1].ippatsu)
        assertRiichiInvariants(s)

        s.setIppatsu(1, true)
        assertTrue(s.ippatsuActive[1])
        assertTrue(s.players[1].ippatsu)
        assertRiichiInvariants(s)

        s.declareRiichi(2, 6, false)
        assertTrue(s.ippatsuActive[2])
        assertTrue(s.players[2].ippatsu)

        s.clearAllIppatsu()
        for (i in 0 until 4) {
            assertFalse(s.ippatsuActive[i])
            assertFalse(s.players[i].ippatsu)
        }
        assertRiichiInvariants(s)

        assertThrows(IllegalArgumentException::class.java) { s.declareRiichi(4, 1, false) }
        assertThrows(IllegalArgumentException::class.java) { s.declareRiichi(-1, 1, false) }
        assertThrows(IllegalArgumentException::class.java) { s.setIppatsu(4, false) }
        assertThrows(IllegalArgumentException::class.java) { s.setIppatsu(-1, false) }
    }
}
