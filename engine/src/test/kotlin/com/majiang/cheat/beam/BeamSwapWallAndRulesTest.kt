package com.majiang.cheat.beam

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.authority.TableAuthority
import com.majiang.cheat.bloom.*
import com.majiang.cheat.mark.*
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.GameEvent
import com.majiang.match.ApprovedSessionStep
import com.majiang.match.MatchConfig
import com.majiang.model.*
import com.majiang.state.*
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.assertThrows
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.EnumSource
import org.junit.jupiter.params.provider.ValueSource

/** Codex-owned acceptance: physical identities, real kan/discard/win paths and private marks. */
@Timeout(40)
class BeamSwapWallAndRulesTest {
    private val controls = List(4) { SeatControlView(it, true, "HUMAN", 0) }
    private val quad = (0..3).map { Tile(Suit.SOU, 8, it) }
    private val win = Tile(Suit.PIN, 5, 1)
    private val miss = Tile(Suit.SOU, 6, 3)
    private val base = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit ->
        (1..3).map { Tile(suit, it) }
    } + Tile(Suit.PIN, 5)

    private fun table(s: GameState) = TableAuthority(
        config = MatchConfig(
            cheatEconomy = CheatEconomyConfig(true, List(4) { 100 },
                mapOf("BEAM_SWAP" to 10, "SLEEVE_BLOOM" to 10, "TILE_MARK" to 10)),
            beamSwapConfig = BeamSwapConfig(true), sleeveBloomConfig = SleeveBloomConfig(true),
            tileMarkConfig = TileMarkConfig(true)),
        clock = MonotonicClock { 1_000L }, controllers = List(4) { SeatController.RemoteHuman(false) },
        initialState = s.also { it.cheatPoints.fill(100) }
    ).also { it.syncDecisionsAndClocks(1_000, emptySet()) }

    private fun state(a: TableAuthority) = a.coordinator.activeRound!!.readState()
    private fun snap(a: TableAuthority, seat: Int = 0) = a.querySnapshot(seat, "beam-rules", 0, controls)
    private fun activate(a: TableAuthority, seat: Int = 0, id: String = "beam"): BeamSwapChoiceRequest {
        a.syncDecisionsAndClocks(1_000, emptySet())
        val before = snap(a, seat)
        val receipt = a.submitBeamSwap(seat, BeamSwapRequest(id, before.sessionId, before.roundId!!,
            before.decision.decisionId, before.decision.controlEpoch), 1_000).receipt
        assertEquals(ReceiptStatus.APPLIED, receipt.status, receipt.reason)
        val s = snap(a, seat)
        return BeamSwapChoiceRequest("$id-choice", s.sessionId, s.roundId!!, s.decision.decisionId,
            s.decision.controlEpoch, s.beamSwap!!.pendingOffer!!.offerId, BeamSwapAction.KEEP_ORIGINAL)
    }
    private fun choose(a: TableAuthority, request: BeamSwapChoiceRequest, hand: Tile, slot: BeamSwapSlot, seat: Int = 0) {
        val result = a.submitBeamSwapChoice(seat, request.copy(action = BeamSwapAction.SWAP,
            handTileId = hand.tileId, targetSlot = slot), 1_000)
        assertEquals(ReceiptStatus.APPLIED, result.receipt.status, result.receipt.reason)
    }
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
        play(a, a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DeclareKan>()
            .first { it.kanType == kind && (rank == null || it.tiles.first().rank == rank) })
        pass(a)
    }
    private fun pon(q: List<Tile>) = Meld(MeldType.PON, q.take(3), 3, q[0])
    private fun fixture(kind: MeldType, concealed: List<Tile> = base, first: Tile = miss,
                        second: Tile = win): GameState {
        val dead = listOf(first, second, Tile(Suit.WIND, 1), Tile(Suit.WIND, 1, 1),
            Tile(Suit.WIND, 3), Tile(Suit.WIND, 3, 1), Tile(Suit.PIN, 4, 3))
        val s = when (kind) {
            MeldType.CLOSED_KAN -> TestTableBuilder.create(hands = mapOf(0 to (quad + concealed.dropLast(1))),
                drawn = 0 to concealed.last(), deadWallPrefix = dead, phase = GamePhase.CHEAT_WINDOW)
            MeldType.ADDED_KAN -> TestTableBuilder.create(hands = mapOf(0 to concealed),
                melds = mapOf(0 to listOf(pon(quad))), drawn = 0 to quad[3],
                deadWallPrefix = dead, phase = GamePhase.CHEAT_WINDOW)
            else -> TestTableBuilder.create(hands = mapOf(0 to (quad.take(3) + concealed)),
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
    private fun physical(s: GameState) {
        TestTableBuilder.assertPhysical(s)
        StateInvariants.assertValid(s)
        assertEquals(14, s.deadWall.size)
        assertEquals(14, s.deadWall.size - s.rinshanPointer + s.deadWallReplacements.size)
    }
    private fun unchangedDrawAndCounters(before: GameState, after: GameState) {
        assertEquals(before.players.map { it.drawnTile }, after.players.map { it.drawnTile })
        assertEquals(before.players.map { it.melds }, after.players.map { it.melds })
        assertEquals(before.wallPointer, after.wallPointer)
        assertEquals(before.wallEndExclusive, after.wallEndExclusive)
        assertEquals(before.wallRemaining, after.wallRemaining)
        assertEquals(before.rinshanPointer, after.rinshanPointer)
        assertEquals(before.totalKanCount, after.totalKanCount)
        assertEquals(before.playerKanCounts.toList(), after.playerKanCounts.toList())
        assertEquals(before.pendingKanDoraCount, after.pendingKanDoraCount)
        assertEquals(before.doraIndicators, after.doraIndicators)
        assertEquals(before.isRinshanDraw, after.isRinshanDraw)
        assertEquals(before.turnNumber, after.turnNumber)
        physical(after)
    }
    private fun normal(remaining: Int? = null): GameState = TestTableBuilder.create(
        drawn = 0 to miss, phase = GamePhase.CHEAT_WINDOW).also { s ->
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        if (remaining != null) {
            val consumed = s.wallRemaining - remaining
            for (tile in s.wall.take(consumed)) {
                s.players[2].discards.add(tile)
                s.players[2].discardHistory.add(tile)
                s.tilePool.transfer(tile, TileLocation.WALL, TileLocation.discard(2))
            }
            s.wallPointer += consumed
        }
        physical(s)
    }
    private fun rinshan(): GameState = TestTableBuilder.create(phase = GamePhase.KAN_DRAW).also {
        TileMovement.drawRinshan(it, 0)
        it.phase = GamePhase.CHEAT_WINDOW
        it.lastDrawKind = SleeveBloomDrawKind.RINSHAN
        it.isRinshanDraw = true
        it.doraIndicators.add(it.deadWall[4])
    }

    @ParameterizedTest
    @ValueSource(ints = [1, 3, 8, 12])
    fun `live candidates are absolute next eight truncated and exchange only concealed hand`(remaining: Int) {
        table(normal(remaining)).use { a ->
            val before = state(a)
            val source = before.players[0].closedTiles.first()
            val slots = (before.wallPointer until minOf(before.wallPointer + 8, before.wallEndExclusive))
                .map { BeamSwapSlot.WallSlot(it) }
            val choice = activate(a)
            val view = snap(a).beamSwap!!.pendingOffer!!
            assertEquals(BeamSwapWindow.WALL, view.window)
            assertEquals(slots, view.candidates.map { it.slot })
            assertTrue(view.candidates.all { !it.marked && it.tile == null })
            assertEquals(before.players[0].closedTiles.map { it.tileId }.toSet(), view.selectableHandTileIds.toSet())
            assertFalse(before.players[0].drawnTile!!.tileId in view.selectableHandTileIds)
            assertEquals(TestTableBuilder.actualTiles(before), TestTableBuilder.actualTiles(state(a)))
            val target = before.wall[slots.last().index]
            choose(a, choice, source, slots.last())
            val after = state(a)
            assertEquals(source, after.wall[slots.last().index])
            assertEquals(before.players[0].closedTiles.toSet() - source + target, after.players[0].closedTiles.toSet())
            assertEquals(before.wall.filterIndexed { i, _ -> i != slots.last().index },
                after.wall.filterIndexed { i, _ -> i != slots.last().index })
            assertEquals(before.deadWall, after.deadWall)
            assertEquals(before.deadWallReplacements, after.deadWallReplacements)
            unchangedDrawAndCounters(before, after)
            assertEquals(90, after.cheatPoints[0])
            val discards = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DiscardTile>()
            assertTrue(discards.any { it.tile == target })
            assertFalse(discards.any { it.tile == source })
        }
    }

    @Test fun `last normal draw has no exchange candidate and costs nothing`() {
        table(normal(0)).use { a ->
            val s = snap(a)
            assertFalse(s.beamSwap!!.canApply)
            val result = a.submitBeamSwap(0, BeamSwapRequest("empty", s.sessionId, s.roundId!!,
                s.decision.decisionId, s.decision.controlEpoch), 1_000)
            assertEquals("BEAM_SWAP_NO_CANDIDATES", result.receipt.reason)
            assertEquals(100, state(a).cheatPoints[0])
            assertNull(state(a).beamSwapOffer)
            physical(state(a))
        }
    }

    @ParameterizedTest
    @EnumSource(MeldType::class, names = ["CLOSED_KAN", "OPEN_KAN", "ADDED_KAN"])
    fun `real kans enumerate only currently hidden dead wall and reject drawn and meld sources`(kind: MeldType) {
        table(fixture(kind)).use { a ->
            start(a, kind)
            val before = state(a)
            val choice = activate(a)
            val offered = state(a).beamSwapOffer!!
            val candidates = offered.candidates
            val hidden = before.tilePool.getByZone(Zone.DEAD_WALL).toSet() - before.doraIndicators.toSet()
            assertEquals(hidden.map { it.tileId }.toSet(), candidates.map { it.tileId }.toSet())
            assertEquals(if (kind == MeldType.CLOSED_KAN) 12 else 13, candidates.size)
            assertEquals(candidates.size, candidates.map { it.tileId }.distinct().size)
            assertFalse(candidates.any { it.slot == BeamSwapSlot.DeadWallSlot(0) })
            assertFalse(candidates.any { it.slot == BeamSwapSlot.DeadWallSlot(4) })
            assertEquals(kind != MeldType.CLOSED_KAN, candidates.any { it.slot == BeamSwapSlot.DeadWallSlot(6) })
            assertTrue(candidates.any { it.slot == BeamSwapSlot.ReplacementSlot(0) })
            val view = snap(a).beamSwap!!.pendingOffer!!
            assertTrue(view.candidates.all { !it.marked && it.tile == null })
            val invalid = listOf(before.players[0].drawnTile!!) + before.players[0].melds.flatMap { it.tiles }
            for ((i, tile) in invalid.withIndex()) {
                assertFalse(tile.tileId in view.selectableHandTileIds)
                val result = a.submitBeamSwapChoice(0, choice.copy(requestId = "invalid-$i", action = BeamSwapAction.SWAP,
                    handTileId = tile.tileId, targetSlot = candidates.first().slot), 1_000)
                assertEquals("BEAM_SWAP_INVALID_CHOICE", result.receipt.reason)
                assertFalse(result.stateChanged)
                assertEquals(offered, state(a).beamSwapOffer)
                assertEquals(TestTableBuilder.actualTiles(before), TestTableBuilder.actualTiles(state(a)))
            }
            val source = before.players[0].closedTiles.first()
            val replacement = before.deadWallReplacements.single()
            choose(a, choice, source, BeamSwapSlot.ReplacementSlot(0))
            val after = state(a)
            assertEquals(listOf(source), after.deadWallReplacements)
            assertTrue(replacement in after.players[0].closedTiles)
            assertEquals(before.deadWall, after.deadWall)
            assertEquals(before.wall, after.wall)
            unchangedDrawAndCounters(before, after)
        }
    }

    @ParameterizedTest
    @EnumSource(MeldType::class, names = ["OPEN_KAN", "ADDED_KAN"])
    fun `swapping deferred indicator does not reveal it until actual kan discard`(kind: MeldType) {
        table(fixture(kind)).use { a ->
            start(a, kind)
            val round = a.coordinator.activeRound!!
            val before = state(a)
            val source = before.players[0].closedTiles.first()
            val oldIndicator = before.deadWall[6]
            val choice = activate(a)
            choose(a, choice, source, BeamSwapSlot.DeadWallSlot(6))
            unchangedDrawAndCounters(before, state(a))
            assertEquals(1, state(a).pendingKanDoraCount)
            assertEquals(source, state(a).deadWall[6])
            assertTrue(oldIndicator in state(a).players[0].closedTiles)
            assertTrue(round.accumulatedEvents.none { it is GameEvent.DoraRevealed })
            for (seat in 0..3) assertEquals(before.doraIndicators, snap(a, seat).view.doraIndicators)
            val result = play(a, GameAction.DiscardTile(0, before.players[0].drawnTile!!))
            assertEquals(1, result.events.filterIsInstance<GameEvent.DoraRevealed>().size)
            assertEquals(before.doraIndicators + source, round.readState().doraIndicators)
            assertEquals(0, round.readState().pendingKanDoraCount)
            assertEquals(before.doraIndicators + source, a.queryPublicProjection().board!!.doraIndicators)
            physical(round.readState())
        }
    }

    @Test fun `four real added kans still permit beam while bloom rejects without charging`() {
        val qs = (1..4).map { rank -> (0..3).map { Tile(Suit.MAN, rank, it) } }
        val original = Tile(Suit.PIN, 5)
        val s = TestTableBuilder.create(hands = mapOf(0 to listOf(original)),
            melds = mapOf(0 to qs.map(::pon)), drawn = 0 to qs[0][3],
            deadWallPrefix = qs.drop(1).map { it[3] } + win, phase = GamePhase.CHEAT_WINDOW)
        s.doraIndicators.add(s.deadWall[4])
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        table(s).use { a ->
            for (rank in 1..4) kan(a, MeldType.ADDED_KAN, rank)
            val before = state(a)
            assertEquals(4, before.rinshanPointer)
            assertEquals(GamePhase.CHEAT_WINDOW, before.phase)
            a.syncDecisionsAndClocks(1_000, emptySet())
            val snapshot = snap(a)
            val bloom = a.submitSleeveBloom(0, SleeveBloomRequest("fourth-bloom", snapshot.sessionId,
                snapshot.roundId!!, snapshot.decision.decisionId, snapshot.decision.controlEpoch), 1_000)
            assertEquals("SLEEVE_BLOOM_WALL_EMPTY", bloom.receipt.reason)
            assertEquals(100, state(a).cheatPoints[0])
            assertTrue(snap(a).beamSwap!!.canApply)
            val choice = activate(a)
            val offer = snap(a).beamSwap!!.pendingOffer!!
            assertEquals(listOf(original.tileId), offer.selectableHandTileIds)
            assertEquals(10, offer.candidates.size)
            assertFalse(offer.candidates.any { it.slot in (0..3).map { index -> BeamSwapSlot.DeadWallSlot(index) } })
            val target = before.deadWallReplacements[3]
            choose(a, choice, original, BeamSwapSlot.ReplacementSlot(3))
            assertEquals(listOf(target), state(a).players[0].closedTiles)
            unchangedDrawAndCounters(before, state(a))
            assertEquals(90, state(a).cheatPoints[0])
        }
    }

    @Test fun `dead wall primitive rejects exposed consumed foreign and mismatched targets atomically`() {
        val s = rinshan()
        val source = s.players[0].closedTiles.first()
        val good = BeamSwapSlot.DeadWallSlot(1)
        val before = s.deepCopy()
        val invalid = listOf(
            Triple(s.players[0].drawnTile!!, good, s.deadWall[1].tileId),
            Triple(s.players[1].closedTiles.first(), good, s.deadWall[1].tileId),
            Triple(source, BeamSwapSlot.DeadWallSlot(0), s.deadWall[0].tileId),
            Triple(source, BeamSwapSlot.DeadWallSlot(4), s.deadWall[4].tileId),
            Triple(source, BeamSwapSlot.WallSlot(s.wallPointer), s.wall[s.wallPointer].tileId),
            Triple(source, BeamSwapSlot.ReplacementSlot(1), s.deadWallReplacements[0].tileId),
            Triple(source, good, s.deadWall[2].tileId))
        for ((hand, slot, expected) in invalid) {
            assertThrows<IllegalArgumentException> { TileMovement.swapHandWithDeadWall(s, 0, hand, slot, expected) }
            assertEquals(TestTableBuilder.actualTiles(before), TestTableBuilder.actualTiles(s))
            unchangedDrawAndCounters(before, s)
        }
    }

    @Test fun `historical consumed reference never steals the active returned rinshan slot`() {
        val s = rinshan()
        val original = s.players[0].drawnTile!!
        TileMovement.swapDrawnWithNextRinshan(s, 0)
        assertEquals(original, s.deadWall[0])
        assertEquals(original, s.deadWall[1])
        val before = s.deepCopy()
        val source = s.players[0].closedTiles.first()
        assertEquals(original, TileMovement.swapHandWithDeadWall(s, 0, source,
            BeamSwapSlot.DeadWallSlot(1), original.tileId))
        assertEquals(original, s.deadWall[0], "Historical prefix is never an active exchange destination")
        assertEquals(source, s.deadWall[1])
        assertTrue(original in s.players[0].closedTiles)
        unchangedDrawAndCounters(before, s)
        TileMovement.discard(s, 0, s.players[0].drawnTile!!)
        assertEquals(source, TileMovement.drawRinshan(s, 0))
        physical(s)
    }

    @Test fun `deep rinshan exchange affects its own future consumption and replacement may return later`() {
        val s = rinshan()
        val firstSource = s.players[0].closedTiles.first()
        val deep = s.deadWall[3]
        val next = s.deadWall[1]
        val third = s.deadWall[2]
        TileMovement.swapHandWithDeadWall(s, 0, firstSource, BeamSwapSlot.DeadWallSlot(3), deep.tileId)
        val replacement = s.deadWallReplacements[0]
        val secondSource = s.players[0].closedTiles.first { it != deep }
        TileMovement.swapHandWithDeadWall(s, 0, secondSource, BeamSwapSlot.ReplacementSlot(0), replacement.tileId)
        for (expected in listOf(next, third, firstSource)) {
            TileMovement.discard(s, 0, s.players[0].drawnTile!!)
            assertEquals(expected, TileMovement.drawRinshan(s, 0))
            physical(s)
        }
        assertEquals(secondSource, s.deadWallReplacements[0], "Replacement is not a normal rinshan path")
        TileMovement.swapHandWithDeadWall(s, 0, replacement, BeamSwapSlot.ReplacementSlot(0), secondSource.tileId)
        assertTrue(secondSource in s.players[0].closedTiles, "Returned replacement remains exchangeable later")
        assertEquals(replacement, s.deadWallReplacements[0])
        physical(s)
    }

    @Test fun `deep indicator exchange waits until the actual second kan reveals that slot`() {
        val firstQuad = (0..3).map { Tile(Suit.MAN, 9, it) }
        val secondQuad = (0..3).map { Tile(Suit.MAN, 2, it) }
        val s = TestTableBuilder.create(hands = mapOf(0 to secondQuad),
            melds = mapOf(0 to listOf(pon(firstQuad))), drawn = 0 to firstQuad[3],
            deadWallPrefix = listOf(miss, win), phase = GamePhase.CHEAT_WINDOW)
        s.doraIndicators.add(s.deadWall[4])
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        table(s).use { a ->
            kan(a, MeldType.ADDED_KAN, 9)
            val before = state(a)
            val source = before.players[0].closedTiles.first { it !in secondQuad }
            choose(a, activate(a), source, BeamSwapSlot.DeadWallSlot(8))
            unchangedDrawAndCounters(before, state(a))
            kan(a, MeldType.CLOSED_KAN, 2)
            assertEquals(listOf(before.deadWall[4], before.deadWall[6], source), state(a).doraIndicators)
            assertEquals(0, state(a).pendingKanDoraCount)
            assertEquals(2, state(a).rinshanPointer)
            physical(state(a))
        }
    }

    @Test fun `exchanged ura slot scores its new tile after actual riichi and later winning draw`() {
        val uraSource = Tile(Suit.PIN, 4)
        val uraTarget = base.last()
        val laterDraws = (1..3).map { Tile(Suit.DRAGON, it, 3) }
        val dead = listOf(miss, Tile(Suit.WIND, 4), Tile(Suit.WIND, 4, 1), Tile(Suit.WIND, 4, 2),
            Tile(Suit.WIND, 3), uraTarget, Tile(Suit.WIND, 3, 1), Tile(Suit.WIND, 3, 2))
        val s = TestTableBuilder.create(hands = mapOf(0 to (quad + base.dropLast(1))), drawn = 0 to uraSource,
            deadWallPrefix = dead, wallPrefix = laterDraws + win, phase = GamePhase.CHEAT_WINDOW)
        s.doraIndicators.add(s.deadWall[4])
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        table(s).use { a ->
            kan(a, MeldType.CLOSED_KAN)
            choose(a, activate(a), uraSource, BeamSwapSlot.DeadWallSlot(5))
            assertEquals(uraSource, state(a).deadWall[5])
            assertTrue(state(a).uraDoraIndicators.isEmpty())
            val riichi = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DeclareRiichi>()
                .single { it.discardTile == miss }
            play(a, riichi)
            pass(a)
            for (seat in 1..3) {
                a.coordinator.advanceAutomaticActions()
                assertEquals(seat, state(a).currentPlayer)
                assertEquals(laterDraws[seat - 1], state(a).players[seat].drawnTile)
                play(a, GameAction.DiscardTile(seat, laterDraws[seat - 1]))
                pass(a)
            }
            a.coordinator.advanceAutomaticActions()
            assertEquals(win, state(a).players[0].drawnTile)
            val round = a.coordinator.activeRound!!
            val won = play(a, GameAction.DeclareTsumo(0)).events.filterIsInstance<GameEvent.TsumoWin>().single()
            assertEquals(listOf(uraSource, dead[7]), round.readState().uraDoraIndicators)
            assertEquals(2, won.breakdown!!.dora.uraCount)
            assertEquals(2, won.breakdown!!.dora.uraHits.size)
            assertTrue(won.yakuList.any { it.contains("立直") })
            assertTrue(won.yakuList.none { it.contains("岭上") })
            physical(round.readState())
        }
    }

    @Test fun `normal concealed exchange creates a real tsumo without changing drawn tile or source yaku`() {
        val full = base + (7..9).map { Tile(Suit.SOU, it) }
        val needed = full.first()
        val s = TestTableBuilder.create(hands = mapOf(0 to (full.drop(1) + miss)), drawn = 0 to win,
            wallPrefix = listOf(needed), phase = GamePhase.CHEAT_WINDOW)
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        table(s).use { a ->
            assertTrue(a.queryDecision(0, false).options.none { it.action is GameAction.DeclareTsumo })
            choose(a, activate(a), miss, BeamSwapSlot.WallSlot(s.wallPointer))
            assertEquals(win, state(a).players[0].drawnTile)
            assertTrue(a.queryDecision(0, false).options.any { it.action is GameAction.DeclareTsumo })
            val round = a.coordinator.activeRound!!
            val won = play(a, GameAction.DeclareTsumo(0)).events.filterIsInstance<GameEvent.TsumoWin>().single()
            assertTrue(won.yakuList.isNotEmpty())
            assertTrue(won.yakuList.none { it.contains("岭上") || it.contains("地和") || it.contains("天和") })
            assertNull(round.readState().beamSwapOffer)
            physical(round.readState())
        }
    }

    @ParameterizedTest
    @EnumSource(MeldType::class, names = ["CLOSED_KAN", "OPEN_KAN", "ADDED_KAN"])
    fun `real kan concealed exchange creates rinshan win while preserving current draw`(kind: MeldType) {
        val needed = base.first()
        table(fixture(kind, base.drop(1) + miss, win, needed)).use { a ->
            start(a, kind)
            val round = a.coordinator.activeRound!!
            assertTrue(a.queryDecision(0, false).options.none { it.action is GameAction.DeclareTsumo })
            choose(a, activate(a), miss, BeamSwapSlot.DeadWallSlot(1))
            assertEquals(win, state(a).players[0].drawnTile)
            assertTrue(state(a).isRinshanDraw)
            val won = play(a, GameAction.DeclareTsumo(0)).events.filterIsInstance<GameEvent.TsumoWin>().single()
            assertTrue(won.yakuList.any { it.contains("岭上") }, won.yakuList.toString())
            assertEquals(if (kind == MeldType.CLOSED_KAN) 2 else 0, won.breakdown!!.dora.indicatorCount)
            assertEquals(if (kind == MeldType.CLOSED_KAN) 2 else 1, won.breakdown!!.dora.indicatorHits.size)
            assertEquals(0, round.readState().pendingKanDoraCount)
            assertNull(round.readState().beamSwapOffer)
            physical(round.readState())
        }
    }

    /** Install a valid persisted mark ledger; candidates may carry marks made before this round. */
    private fun installMarks(a: TableAuthority, marks: TileMarkState) {
        val session = a.coordinator.session
        val step = session.prepare(ApprovedSessionStep.RegisterDealtRound(a.coordinator.activeRound!!.roundId))
        session.install(step.copy(targetTileMarkState = marks))
    }
    private fun group(id: String, seat: Int, tiles: List<Tile>, round: String, ordinal: Int) =
        TileMarkGroup(id, seat, tiles.map { it.tileId }, round, ordinal)

    @Test fun `marking rinshan and kan tiles does not make them legal sources and swapped marks follow identities`() {
        table(fixture(MeldType.ADDED_KAN)).use { a ->
            start(a, MeldType.ADDED_KAN)
            val before = state(a)
            val source = before.players[0].closedTiles.first()
            val drawn = before.players[0].drawnTile!!
            val meld = before.players[0].melds.single().tiles.first()
            val target = before.deadWall[1]
            val marks = a.coordinator.session.tileMarkState
            val roundId = a.coordinator.activeRound!!.roundId
            installMarks(a, marks.addGroup(group("own-dead-wall", 0,
                listOf(source, drawn, meld, target), roundId, marks.dealtRoundOrdinal)))
            val choice = activate(a)
            val offer = snap(a).beamSwap!!.pendingOffer!!
            assertEquals(listOf(target), offer.candidates.mapNotNull { it.tile })
            assertEquals(BeamSwapSlot.DeadWallSlot(1), offer.candidates.single { it.marked }.slot)
            for ((i, forbidden) in listOf(drawn, meld).withIndex()) {
                val result = a.submitBeamSwapChoice(0, choice.copy(requestId = "marked-forbidden-$i",
                    action = BeamSwapAction.SWAP, handTileId = forbidden.tileId,
                    targetSlot = BeamSwapSlot.DeadWallSlot(1)), 1_000)
                assertEquals("BEAM_SWAP_INVALID_CHOICE", result.receipt.reason)
                assertNotNull(state(a).beamSwapOffer)
            }
            choose(a, choice, source, BeamSwapSlot.DeadWallSlot(1))
            val tracking = snap(a).tileMark!!.activeGroups.single().tiles.associateBy { it.tileId }
            assertEquals(TileMarkArea.OWN_HAND, tracking.getValue(target.tileId).area)
            assertEquals(TileMarkArea.NOT_VISIBLE, tracking.getValue(source.tileId).area)
            assertNull(tracking.getValue(source.tileId).seat)
            assertNull(tracking.getValue(source.tileId).riverPosition)
            assertEquals(TileMarkArea.OWN_HAND, tracking.getValue(drawn.tileId).area)
            assertEquals(TileMarkArea.EXPOSED_MELD, tracking.getValue(meld.tileId).area)
            unchangedDrawAndCounters(before, state(a))
        }
    }

    @Test fun `only own active marks reveal local candidate identities and cancellation removes locations`() {
        table(normal()).use { a ->
            val s = state(a)
            val own = listOf(s.wall[0], s.wall[7], s.wall[8], s.deadWall[5])
            val foreign = listOf(s.wall[1], s.wall[2], s.wall[3], s.wall[9])
            val roundId = a.coordinator.activeRound!!.roundId
            var marks = a.coordinator.session.tileMarkState
            marks = marks.addGroup(group("own", 0, own, roundId, marks.dealtRoundOrdinal))
            marks = marks.addGroup(group("foreign", 1, foreign, roundId, marks.dealtRoundOrdinal))
            installMarks(a, marks)
            val before = snap(a)
            assertNull(before.beamSwap!!.pendingOffer)
            assertTrue(before.tileMark!!.activeGroups.single().tiles.all { it.area == TileMarkArea.NOT_VISIBLE })
            val choice = activate(a)
            val offered = snap(a).beamSwap!!.pendingOffer!!
            assertEquals(listOf(0, 7).map { BeamSwapSlot.WallSlot(it) }, offered.candidates.filter { it.marked }.map { it.slot })
            assertEquals(own.take(2), offered.candidates.mapNotNull { it.tile })
            assertTrue(offered.candidates.filter { !it.marked }.all { it.tile == null })
            assertFalse(offered.candidates.any { it.tile in own.drop(2) })
            for (seat in 1..3) assertNull(snap(a, seat).beamSwap?.pendingOffer)
            assertTrue(snap(a, 1).tileMark!!.activeGroups.single().tiles.all { it.area == TileMarkArea.NOT_VISIBLE })
            assertEquals(ReceiptStatus.APPLIED, a.submitBeamSwapChoice(0, choice, 1_000).receipt.status)
            val after = snap(a)
            assertNull(after.beamSwap!!.pendingOffer)
            assertTrue(after.tileMark!!.activeGroups.single().tiles.all {
                it.area == TileMarkArea.NOT_VISIBLE && it.seat == null && it.riverPosition == null
            })
            assertEquals(2, offered.candidates.count { it.marked }, "Old snapshot must remain immutable")
            assertFalse(a.queryPublicProjection().javaClass.declaredFields.any { it.name.contains("beam", ignoreCase = true) })
            physical(state(a))
        }
    }

    @Test fun `expired own marks never reveal candidate identities`() {
        table(normal()).use { a ->
            val marked = state(a).wall.take(4)
            var ledger = TileMarkState().registerDealtRound("historic-1")
            ledger = ledger.addGroup(group("expired", 0, marked, "historic-1", 1))
            for (n in 1..4) {
                ledger = ledger.completeRound("historic-$n")
                if (n < 4) ledger = ledger.registerDealtRound("historic-${n + 1}")
            }
            ledger = ledger.registerDealtRound(a.coordinator.activeRound!!.roundId)
            assertTrue(ledger.groups.isEmpty())
            installMarks(a, ledger)
            activate(a)
            assertTrue(snap(a).beamSwap!!.pendingOffer!!.candidates.all { !it.marked && it.tile == null })
            assertTrue(snap(a).tileMark!!.activeGroups.isEmpty())
            physical(state(a))
        }
    }

    @Test fun `state copies isolate pending offer and new round clears offer and used marker`() {
        table(normal()).use { a ->
            activate(a)
            val original = state(a)
            val offer = original.beamSwapOffer!!
            assertEquals(original.turnNumber, original.beamSwapUsedOnTurn)
            val copiedInto = GameState().also { original.copyInto(it) }
            val copiedFrom = GameState().also { it.copyFrom(original) }
            for (copy in listOf(original.deepCopy(), copiedInto, copiedFrom)) {
                assertEquals(offer, copy.beamSwapOffer)
                assertEquals(original.turnNumber, copy.beamSwapUsedOnTurn)
                copy.resetForNewRound()
                assertNull(copy.beamSwapOffer)
                assertEquals(-1, copy.beamSwapUsedOnTurn)
                assertEquals(offer, state(a).beamSwapOffer)
                assertEquals(original.turnNumber, state(a).beamSwapUsedOnTurn)
            }
            physical(state(a))
        }
    }
}
