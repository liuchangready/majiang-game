package com.majiang.cheat.bloom

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.authority.TableAuthority
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.model.*
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.EnumSource

/** Codex-owned behavioral acceptance: real kan, scoring, projection and bloom paths. */
@Timeout(40)
class SleeveBloomKanDoraTest {
    private val controls = List(4) { SeatControlView(it, true, "HUMAN", 0) }
    private val quad = (0..3).map { Tile(Suit.SOU, 8, it) }
    private val win = Tile(Suit.PIN, 5, 1)
    private val miss = Tile(Suit.SOU, 6, 3)
    private val base = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
        (1..3).map { Tile(suit, it) }
    } + Tile(Suit.PIN, 5)
    private fun pon(q: List<Tile>) = Meld(MeldType.PON, q.take(3), 3, q[0])
    private fun table(s: GameState) = TableAuthority(
        config = MatchConfig(cheatEconomy = CheatEconomyConfig(true, List(4) { 100 }, mapOf("SLEEVE_BLOOM" to 10)),
            sleeveBloomConfig = SleeveBloomConfig(true)),
        clock = MonotonicClock { 1_000L }, controllers = List(4) { SeatController.RemoteHuman(false) },
        initialState = s.also { it.cheatPoints.fill(100) }
    )
    private fun state(a: TableAuthority) = a.coordinator.activeRound!!.readState()
    private fun snap(a: TableAuthority, seat: Int = 0) = a.querySnapshot(seat, "kan-dora", 0, controls)
    private fun play(a: TableAuthority, action: GameAction): ActionResult.Accepted {
        val result = a.coordinator.handleAction(action)
        assertTrue(result is ActionResult.Accepted, "$action: $result")
        return result as ActionResult.Accepted
    }
    private fun pass(a: TableAuthority) {
        repeat(12) {
            if (a.coordinator.activeRound?.responseWindow == null) return
            val action = (0..3).flatMap { a.queryDecision(it, false).options }.map { it.action }
                .filterIsInstance<GameAction.Pass>().firstOrNull() ?: error("No pass in response window")
            play(a, action)
        }
        error("Response window did not resolve")
    }
    private fun kan(a: TableAuthority, kind: MeldType, rank: Int? = null) {
        val action = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DeclareKan>()
            .first { it.kanType == kind && (rank == null || it.tiles.first().rank == rank) }
        play(a, action)
        pass(a)
    }
    private fun fixture(kind: MeldType, first: Tile = miss, second: Tile = win,
                        otherHands: Map<Int, List<Tile>> = emptyMap()): GameState {
        val dead = listOf(first, second, Tile(Suit.WIND, 1), Tile(Suit.WIND, 1, 1),
            Tile(Suit.WIND, 3), Tile(Suit.WIND, 3, 1), Tile(Suit.PIN, 4, 3))
        val s = when (kind) {
            MeldType.CLOSED_KAN -> TestTableBuilder.create(hands = otherHands + (0 to (quad + base.dropLast(1))),
                drawn = 0 to base.last(), deadWallPrefix = dead, phase = GamePhase.CHEAT_WINDOW)
            MeldType.ADDED_KAN -> TestTableBuilder.create(hands = otherHands + (0 to base),
                melds = mapOf(0 to listOf(pon(quad))), drawn = 0 to quad[3],
                deadWallPrefix = dead, phase = GamePhase.CHEAT_WINDOW)
            else -> TestTableBuilder.create(hands = otherHands + (0 to (quad.take(3) + base)),
                drawn = 3 to quad[3], deadWallPrefix = dead, phase = GamePhase.CHEAT_WINDOW)
        }
        s.doraIndicators.add(s.deadWall[4])
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        return s
    }
    private fun start(a: TableAuthority, kind: MeldType) {
        if (kind == MeldType.OPEN_KAN) play(a, GameAction.DiscardTile(3, quad[3]))
        kan(a, kind)
    }
    private fun activate(a: TableAuthority): SleeveBloomChoiceRequest {
        a.syncDecisionsAndClocks(1_000L, emptySet())
        val s = snap(a)
        val request = SleeveBloomRequest("activate-${s.roundVersion}", s.sessionId, s.roundId!!,
            s.decision.decisionId, s.decision.controlEpoch)
        assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0, request, 1_000L).receipt.status)
        val offered = snap(a)
        return SleeveBloomChoiceRequest("choose-${offered.roundVersion}", offered.sessionId, offered.roundId!!,
            offered.decision.decisionId, offered.decision.controlEpoch,
            offered.sleeveBloom!!.pendingOffer!!.offerId, SleeveBloomKeep.FIRST)
    }
    private fun physical(s: GameState) {
        TestTableBuilder.assertPhysical(s)
        StateInvariants.assertValid(s)
    }
    private fun assertVisible(a: TableAuthority, expected: List<Tile>) {
        for (seat in 0..3) assertEquals(expected, snap(a, seat).view.doraIndicators)
        assertEquals(expected, a.queryPublicProjection().board!!.doraIndicators)
        physical(state(a))
    }

    @ParameterizedTest
    @EnumSource(MeldType::class, names = ["CLOSED_KAN", "OPEN_KAN", "ADDED_KAN"])
    fun `kan timing is authoritative and bloom never reveals an indicator`(kind: MeldType) {
        for (keep in SleeveBloomKeep.entries) table(fixture(kind)).use { a ->
            val round = a.coordinator.activeRound!!
            val initial = state(a).doraIndicators.toList()
            val next = state(a).deadWall[6]
            start(a, kind)
            val visible = if (kind == MeldType.CLOSED_KAN) initial + next else initial
            assertVisible(a, visible)
            val kanEvents = round.accumulatedEvents
            assertEquals(if (kind == MeldType.CLOSED_KAN) 1 else 0, kanEvents.filterIsInstance<GameEvent.DoraRevealed>().size)
            if (kind == MeldType.CLOSED_KAN) assertTrue(kanEvents.indexOfFirst { it is GameEvent.DoraRevealed } <
                kanEvents.indexOfFirst { it is GameEvent.RinshanDraw })
            val original = state(a).players[0].drawnTile
            val candidate = state(a).deadWall[1]
            val choice = activate(a).copy(keep = keep)
            assertVisible(a, visible)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(0, choice, 1_000L).receipt.status)
            assertVisible(a, visible)
            assertEquals(deadline, a.decisionClock.timing(0)!!.deadlineMillis)
            assertEquals(if (keep == SleeveBloomKeep.FIRST) original else candidate, state(a).players[0].drawnTile)
            val result = play(a, GameAction.DiscardTile(0, state(a).players[0].drawnTile!!))
            assertVisible(a, initial + next)
            assertEquals(if (kind == MeldType.CLOSED_KAN) 0 else 1, result.events.filterIsInstance<GameEvent.DoraRevealed>().size)
            assertEquals(1, round.accumulatedEvents.filterIsInstance<GameEvent.DoraRevealed>().size)
        }
    }

    @ParameterizedTest
    @EnumSource(MeldType::class, names = ["CLOSED_KAN", "OPEN_KAN", "ADDED_KAN"])
    fun `rinshan tsumo scores only indicators already revealed with and without bloom`(kind: MeldType) {
        for (useBloom in listOf(false, true)) {
            val s = if (useBloom) fixture(kind) else fixture(kind, win, miss)
            table(s).use { a ->
                val round = a.coordinator.activeRound!!
                start(a, kind)
                if (useBloom) {
                    val choice = activate(a).copy(keep = SleeveBloomKeep.SECOND)
                    assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(0, choice, 1_000L).receipt.status)
                }
                val result = play(a, GameAction.DeclareTsumo(0))
                val won = result.events.filterIsInstance<GameEvent.TsumoWin>().single()
                assertTrue(won.yakuList.any { it.contains("岭上") })
                assertEquals(if (kind == MeldType.CLOSED_KAN) 2 else 0, won.breakdown!!.dora.indicatorCount)
                assertEquals(if (kind == MeldType.CLOSED_KAN) 2 else 1, won.breakdown!!.dora.indicatorHits.size)
                assertTrue(result.events.filterIsInstance<GameEvent.DoraRevealed>().none { !it.isUraDora })
                assertEquals(if (kind == MeldType.CLOSED_KAN) 2 else 1, round.readState().doraIndicators.size)
                assertEquals(0, round.readState().pendingKanDoraCount, "Terminal cleanup must not leave a deferred reveal")
                physical(round.readState())
            }
        }
    }

    @ParameterizedTest
    @EnumSource(MeldType::class, names = ["OPEN_KAN", "ADDED_KAN"])
    fun `bloom timeout keeps first then discard reveals pending dora exactly once`(kind: MeldType) {
        table(fixture(kind)).use { a ->
            start(a, kind)
            val round = a.coordinator.activeRound!!
            val before = state(a)
            activate(a)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            assertNotNull(a.checkTimeout(deadline))
            val after = round.readState()
            assertNull(after.sleeveBloomOffer)
            assertEquals(before.players[0].drawnTile, after.players[0].discards.last())
            assertEquals(before.doraIndicators + before.deadWall[6], after.doraIndicators)
            assertEquals(90, after.cheatPoints[0])
            assertEquals(1, round.accumulatedEvents.filterIsInstance<GameEvent.DoraRevealed>().size)
            assertEquals(before.deadWall, after.deadWall)
            physical(after)
        }
    }

    @Test fun `pending reveal survives independent copies and queries without affecting original`() {
        table(fixture(MeldType.ADDED_KAN)).use { original ->
            start(original, MeldType.ADDED_KAN)
            val pending = state(original)
            val copiedInto = GameState().also { pending.copyInto(it) }
            for (copy in listOf(pending.deepCopy(), copiedInto, GameState().also { it.copyFrom(pending) })) {
                table(copy).use { restored ->
                    assertVisible(restored, pending.doraIndicators)
                    play(restored, GameAction.DiscardTile(0, state(restored).players[0].drawnTile!!))
                    assertEquals(2, state(restored).doraIndicators.size)
                }
            }
            assertVisible(original, pending.doraIndicators)
            val reset = pending.deepCopy()
            reset.resetForNewRound()
            assertEquals(0, reset.pendingKanDoraCount)
            reset.copyFrom(fixture(MeldType.CLOSED_KAN))
            table(reset).use { fresh ->
                play(fresh, GameAction.DiscardTile(0, state(fresh).players[0].drawnTile!!))
                assertEquals(1, state(fresh).doraIndicators.size, "New round cannot inherit a delayed reveal")
            }
        }
    }

    @Test fun `riichi closed kan rinshan win reveals ura only for the two active indicators`() {
        val dead = listOf(win, miss, Tile(Suit.WIND, 1), Tile(Suit.WIND, 1, 1),
            Tile(Suit.WIND, 3), Tile(Suit.WIND, 3, 1), Tile(Suit.PIN, 4, 3), Tile(Suit.PIN, 4, 2))
        val s = TestTableBuilder.create(hands = mapOf(0 to (quad.take(3) + base)), drawn = 0 to quad[3],
            deadWallPrefix = dead, phase = GamePhase.CHEAT_WINDOW)
        s.doraIndicators.add(s.deadWall[4])
        s.riichiDeclared[0] = true
        s.players[0].riichiDeclared = true
        s.scores[0] -= 1000
        s.riichiSticks = 1
        table(s).use { a ->
            val round = a.coordinator.activeRound!!
            kan(a, MeldType.CLOSED_KAN)
            assertEquals(2, state(a).doraIndicators.size)
            assertTrue(state(a).uraDoraIndicators.isEmpty())
            val result = play(a, GameAction.DeclareTsumo(0))
            val won = result.events.filterIsInstance<GameEvent.TsumoWin>().single()
            assertEquals(2, won.breakdown!!.dora.uraHits.size)
            assertEquals(2, won.breakdown!!.dora.uraCount)
            assertEquals(2, result.events.filterIsInstance<GameEvent.DoraRevealed>().count { it.isUraDora })
            assertEquals(listOf(dead[5], dead[7]), round.readState().uraDoraIndicators)
            physical(round.readState())
        }
    }

    @Test fun `four consecutive added kans reveal predecessors before each next rinshan and refuse fourth bloom`() {
        val qs = (1..4).map { rank -> (0..3).map { Tile(Suit.MAN, rank, it) } }
        val s = TestTableBuilder.create(hands = mapOf(0 to listOf(Tile(Suit.PIN, 5))),
            melds = mapOf(0 to qs.map(::pon)), drawn = 0 to qs[0][3],
            deadWallPrefix = qs.drop(1).map { it[3] } + Tile(Suit.PIN, 5, 1), phase = GamePhase.CHEAT_WINDOW)
        s.doraIndicators.add(s.deadWall[4])
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        table(s).use { a ->
            val round = a.coordinator.activeRound!!
            for (rank in 1..4) {
                val beforeEvents = round.accumulatedEvents.size
                kan(a, MeldType.ADDED_KAN, rank)
                assertEquals(rank, state(a).doraIndicators.size)
                val events = round.accumulatedEvents.drop(beforeEvents)
                if (rank > 1) assertTrue(events.indexOfFirst { it is GameEvent.DoraRevealed } in
                    0 until events.indexOfFirst { it is GameEvent.RinshanDraw })
                if (rank < 4) {
                    val choice = activate(a)
                    assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(0, choice, 1_000L).receipt.status)
                    assertEquals(rank, state(a).doraIndicators.size)
                } else {
                    a.syncDecisionsAndClocks(1_000L, emptySet())
                    val snapshot = snap(a)
                    assertFalse(snapshot.sleeveBloom!!.canApply)
                    val request = SleeveBloomRequest("fourth", snapshot.sessionId, snapshot.roundId!!,
                        snapshot.decision.decisionId, snapshot.decision.controlEpoch)
                    assertEquals("SLEEVE_BLOOM_WALL_EMPTY", a.submitSleeveBloom(0, request, 1_000L).receipt.reason)
                    assertEquals(70, state(a).cheatPoints[0])
                }
                physical(state(a))
            }
            play(a, GameAction.DiscardTile(0, state(a).players[0].drawnTile!!))
            assertEquals(5, state(a).doraIndicators.size)
            assertEquals(4, round.accumulatedEvents.filterIsInstance<GameEvent.DoraRevealed>().size)
            physical(state(a))
        }
    }

    private fun robberHand() = (4..6).map { Tile(Suit.MAN, it) } + (7..9).map { Tile(Suit.PIN, it) } +
        (0..2).map { Tile(Suit.DRAGON, 1, it) } + listOf(Tile(Suit.SOU, 6), Tile(Suit.SOU, 7),
            Tile(Suit.WIND, 2), Tile(Suit.WIND, 2, 1))

    @Test fun `kan discard reveals before ron scoring and does not wait for a safe discard`() {
        val waiting = (4..6).map { Tile(Suit.MAN, it) } + (6..8).map { Tile(Suit.PIN, it) } +
            (4..6).map { Tile(Suit.SOU, it) } + (0..2).map { Tile(Suit.DRAGON, 1, it) } + Tile(Suit.PIN, 5, 2)
        table(fixture(MeldType.ADDED_KAN, win, miss, mapOf(1 to waiting))).use { a ->
            val round = a.coordinator.activeRound!!
            start(a, MeldType.ADDED_KAN)
            assertEquals(1, state(a).doraIndicators.size)
            val discard = play(a, GameAction.DiscardTile(0, win))
            assertEquals(1, discard.events.filterIsInstance<GameEvent.DoraRevealed>().size)
            assertEquals(2, state(a).doraIndicators.size)
            play(a, GameAction.DeclareRon(1))
            pass(a)
            val won = round.accumulatedEvents.filterIsInstance<GameEvent.RonWin>().single()
            assertEquals(2, won.breakdown!!.dora.indicatorCount)
            assertEquals(2, won.breakdown!!.dora.indicatorHits.size)
            physical(round.readState())
        }
    }

    @Test fun `consecutive closed kan reveals preceding delayed indicator and its own before draw`() {
        val firstQuad = (0..3).map { Tile(Suit.MAN, 9, it) }
        val secondQuad = (0..3).map { Tile(Suit.MAN, 2, it) }
        val s = TestTableBuilder.create(hands = mapOf(0 to secondQuad),
            melds = mapOf(0 to listOf(pon(firstQuad))), drawn = 0 to firstQuad[3],
            deadWallPrefix = listOf(miss, win), phase = GamePhase.CHEAT_WINDOW)
        s.doraIndicators.add(s.deadWall[4])
        table(s).use { a ->
            val round = a.coordinator.activeRound!!
            kan(a, MeldType.ADDED_KAN, 9)
            assertEquals(1, state(a).doraIndicators.size)
            val beforeEvents = round.accumulatedEvents.size
            kan(a, MeldType.CLOSED_KAN, 2)
            assertEquals(listOf(s.deadWall[4], s.deadWall[6], s.deadWall[8]), state(a).doraIndicators)
            val events = round.accumulatedEvents.drop(beforeEvents)
            assertEquals(2, events.filterIsInstance<GameEvent.DoraRevealed>().size)
            assertTrue(events.indexOfLast { it is GameEvent.DoraRevealed } < events.indexOfFirst { it is GameEvent.RinshanDraw })
            val discarded = play(a, GameAction.DiscardTile(0, state(a).players[0].drawnTile!!))
            assertTrue(discarded.events.none { it is GameEvent.DoraRevealed })
            physical(state(a))
        }
    }

    @Test fun `robbing a second added kan cannot flush the preceding pending indicator`() {
        val firstQuad = (0..3).map { Tile(Suit.MAN, 9, it) }
        val s = TestTableBuilder.create(hands = mapOf(1 to robberHand()),
            melds = mapOf(0 to listOf(pon(firstQuad), pon(quad))), drawn = 0 to firstQuad[3],
            deadWallPrefix = listOf(quad[3], miss), phase = GamePhase.CHEAT_WINDOW)
        s.doraIndicators.add(s.deadWall[4])
        table(s).use { a ->
            val round = a.coordinator.activeRound!!
            kan(a, MeldType.ADDED_KAN, 9)
            assertEquals(1, state(a).doraIndicators.size)
            val second = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DeclareKan>()
                .first { it.kanType == MeldType.ADDED_KAN && it.tiles.first().suit == Suit.SOU }
            play(a, second)
            assertEquals(GamePhase.CHANKAN_WINDOW, state(a).phase)
            assertEquals(1, state(a).doraIndicators.size)
            play(a, GameAction.DeclareRon(1))
            pass(a)
            assertEquals(1, round.readState().rinshanPointer)
            assertEquals(1, round.readState().doraIndicators.size)
            assertEquals(0, round.readState().pendingKanDoraCount)
            assertTrue(round.accumulatedEvents.none { it is GameEvent.DoraRevealed })
            physical(round.readState())
        }
    }

    @Test fun `robbed added kan never reveals dora or draws rinshan`() {
        table(fixture(MeldType.ADDED_KAN, otherHands = mapOf(1 to robberHand()))).use { a ->
            val round = a.coordinator.activeRound!!
            val action = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DeclareKan>().single()
            play(a, action)
            assertEquals(GamePhase.CHANKAN_WINDOW, state(a).phase)
            assertVisible(a, listOf(state(a).deadWall[4]))
            play(a, GameAction.DeclareRon(1))
            pass(a)
            assertEquals(GamePhase.ROUND_END, round.readState().phase)
            assertEquals(0, round.readState().rinshanPointer)
            assertEquals(1, round.readState().doraIndicators.size)
            assertTrue(round.accumulatedEvents.none { it is GameEvent.DoraRevealed || it is GameEvent.RinshanDraw })
            physical(round.readState())
        }
    }
}
