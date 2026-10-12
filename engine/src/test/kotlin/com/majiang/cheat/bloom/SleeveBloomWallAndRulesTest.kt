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
import org.junit.jupiter.api.assertThrows

@Timeout(40)
class SleeveBloomWallAndRulesTest {
    private val controls = List(4) { SeatControlView(it, true, "HUMAN", 0) }
    private fun table(s: GameState) = TableAuthority(
        config = MatchConfig(cheatEconomy = CheatEconomyConfig(true, List(4) { 100 }, mapOf("SLEEVE_BLOOM" to 10)),
            sleeveBloomConfig = SleeveBloomConfig(true)),
        clock = MonotonicClock { 1_000L }, controllers = List(4) { SeatController.RemoteHuman(false) },
        initialState = s.also { it.cheatPoints.fill(100) }
    )
    private fun state(a: TableAuthority) = a.coordinator.activeRound!!.readState()
    private fun snap(a: TableAuthority, seat: Int = 0) = a.querySnapshot(seat, "rules-test", 0, controls)
    private fun bloom(a: TableAuthority, keep: SleeveBloomKeep, seat: Int = 0, id: String = "bloom") {
        a.syncDecisionsAndClocks(1_000, emptySet())
        val s = snap(a, seat)
        val r = a.submitSleeveBloom(seat, SleeveBloomRequest(id, s.sessionId, s.roundId!!,
            s.decision.decisionId, s.decision.controlEpoch), 1_000)
        assertEquals(ReceiptStatus.APPLIED, r.receipt.status, r.receipt.reason)
        val offered = snap(a, seat)
        val offer = offered.sleeveBloom!!.pendingOffer!!
        val choice = SleeveBloomChoiceRequest("$id-choice", s.sessionId, s.roundId!!,
            offered.decision.decisionId, offered.decision.controlEpoch, offer.offerId, keep)
        assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloomChoice(seat, choice, 1_000).receipt.status)
    }
    private fun passWindows(a: TableAuthority) {
        repeat(12) {
            if (a.coordinator.activeRound!!.responseWindow == null) return
            val pass = (0..3).flatMap { a.queryDecision(it, false).options }.map { it.action }
                .filterIsInstance<GameAction.Pass>().firstOrNull() ?: error("Response window has no pass")
            assertTrue(a.coordinator.handleAction(pass) is ActionResult.Accepted)
        }
        error("Response window stuck")
    }

    @Test fun `rinshan swap restores exact slot and preserves all counts and other order`() {
        val s = TestTableBuilder.create(phase = GamePhase.KAN_DRAW)
        val first = TileMovement.drawRinshan(s, 0)
        s.phase = GamePhase.CHEAT_WINDOW
        val replacement = s.deadWallReplacements.last()
        val liveWall = s.wall.toList()
        val end = s.wallEndExclusive
        val pointer = s.wallPointer
        assertEquals(replacement, TileMovement.swapDrawnWithRinshanReplacement(s, 0))
        assertEquals(replacement, s.players[0].drawnTile)
        assertEquals(0, s.rinshanPointer)
        assertEquals(first, s.deadWall[s.rinshanPointer])
        assertEquals(TileLocation.DEAD_WALL, s.tilePool.getLocation(first))
        assertEquals(TileLocation.hand(0), s.tilePool.getLocation(replacement))
        assertTrue(s.deadWallReplacements.isEmpty())
        assertEquals(liveWall, s.wall)
        assertEquals(pointer, s.wallPointer)
        assertEquals(end, s.wallEndExclusive)
        TestTableBuilder.assertPhysical(s)
        StateInvariants.assertValid(s)
        val before = TestTableBuilder.actualTiles(s).toList()
        assertThrows<IllegalArgumentException> { TileMovement.swapDrawnWithRinshanReplacement(s, 0) }
        assertEquals(before, TestTableBuilder.actualTiles(s), "Rejected second physical swap mutated containers")
    }

    @Test fun `rinshan source with mismatched original slot rejects before charging`() {
        val s = TestTableBuilder.create(phase = GamePhase.KAN_DRAW)
        TileMovement.drawRinshan(s, 0)
        s.phase = GamePhase.CHEAT_WINDOW
        s.lastDrawKind = SleeveBloomDrawKind.RINSHAN
        // Physical conservation still holds, but the current drawn tile is no longer the
        // tile from the consumed rinshan slot. A stale source tag must not authorize it.
        TileMovement.swapHandWithWall(s, 0, s.players[0].drawnTile!!)
        StateInvariants.assertValid(s)
        table(s).use { a ->
            val snapshot = snap(a)
            assertFalse(snapshot.sleeveBloom!!.canApply)
            val request = SleeveBloomRequest("bad-source", snapshot.sessionId, snapshot.roundId!!,
                snapshot.decision.decisionId, snapshot.decision.controlEpoch)
            val result = a.submitSleeveBloom(0, request, 1_000)
            assertEquals(ReceiptStatus.REJECTED, result.receipt.status)
            assertFalse(result.stateChanged)
            assertNull(state(a).sleeveBloomOffer)
            assertEquals(100, state(a).cheatPoints[0])
            assertEquals(s.players[0].drawnTile, state(a).players[0].drawnTile)
            TestTableBuilder.assertPhysical(state(a))
        }
    }

    @Test fun `all three actual kan paths offer their own replacement and retain actual kan count`() {
        for (kind in listOf(MeldType.CLOSED_KAN, MeldType.ADDED_KAN, MeldType.OPEN_KAN)) {
            val copies = (0..3).map { Tile(Suit.SOU, 8, it) }
            val pon = Meld(MeldType.PON, copies.take(3), 3, copies[0])
            val s = when (kind) {
                MeldType.CLOSED_KAN -> TestTableBuilder.create(hands = mapOf(0 to copies),
                    drawn = 0 to Tile(Suit.SOU, 9, 3), phase = GamePhase.CHEAT_WINDOW)
                MeldType.ADDED_KAN -> TestTableBuilder.create(melds = mapOf(0 to listOf(pon)),
                    drawn = 0 to copies[3], phase = GamePhase.CHEAT_WINDOW)
                else -> TestTableBuilder.create(hands = mapOf(0 to copies.take(3)),
                    drawn = 3 to copies[3], phase = GamePhase.CHEAT_WINDOW)
            }.also { it.lastDrawKind = SleeveBloomDrawKind.NORMAL }
            table(s).use { a ->
                if (kind == MeldType.OPEN_KAN) {
                    assertTrue(a.coordinator.handleAction(GameAction.DiscardTile(3, copies[3])) is ActionResult.Accepted)
                }
                val kan = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DeclareKan>().first { it.kanType == kind }
                assertTrue(a.coordinator.handleAction(kan) is ActionResult.Accepted, "$kind rejected")
                passWindows(a)
                val before = state(a)
                assertEquals(SleeveBloomDrawKind.RINSHAN, before.lastDrawKind)
                assertEquals(1, before.totalKanCount)
                val original = before.players[0].drawnTile!!
                val replacement = before.deadWallReplacements.last()
                bloom(a, SleeveBloomKeep.SECOND)
                val after = state(a)
                assertEquals(replacement, after.players[0].drawnTile)
                assertEquals(original, after.deadWall[after.rinshanPointer])
                assertEquals(before.rinshanPointer - 1, after.rinshanPointer)
                assertEquals(before.totalKanCount, after.totalKanCount)
                assertEquals(before.playerKanCounts.toList(), after.playerKanCounts.toList())
                assertEquals(before.doraIndicators, after.doraIndicators)
                assertEquals(before.wallRemaining, after.wallRemaining)
                assertTrue(after.isRinshanDraw)
                TestTableBuilder.assertPhysical(after)
                StateInvariants.assertValid(after)
            }
        }
    }

    @Test fun `four kans by one player still forbid a fifth despite available rinshan slots`() {
        val quads = (1..4).map { rank -> (0..3).map { Tile(Suit.MAN, rank, it) } }
        val melds = quads.map { Meld(MeldType.CLOSED_KAN, it, 0, it.first()) }
        val fifth = (0..3).map { Tile(Suit.SOU, 8, it) }
        val s = TestTableBuilder.create(melds = mapOf(0 to melds), hands = mapOf(1 to fifth),
            drawn = 1 to Tile(Suit.SOU, 9, 3), phase = GamePhase.CHEAT_WINDOW)
        s.totalKanCount = 4
        s.playerKanCounts[0] = 4
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        // Pointer zero is reachable after each previous kan's bloom kept its replacement.
        assertEquals(0, s.rinshanPointer)
        table(s).use { a ->
            assertTrue(a.queryDecision(1, false).options.none { it.action is GameAction.DeclareKan })
            assertTrue(a.coordinator.handleAction(GameAction.DeclareKan(1, fifth, MeldType.CLOSED_KAN)) is ActionResult.Rejected)
            assertEquals(4, state(a).totalKanCount)
            TestTableBuilder.assertPhysical(state(a))
        }
    }

    @Test fun `four consecutive real kans each allow bloom and fourth pointer rollback preserves cap`() {
        val triples = (1..4).flatMap { rank -> (0..2).map { Tile(Suit.MAN, rank, it) } }
        val s = TestTableBuilder.create(hands = mapOf(0 to (triples + Tile(Suit.PIN, 5, 0))),
            drawn = 0 to Tile(Suit.MAN, 1, 3), phase = GamePhase.CHEAT_WINDOW,
            deadWallPrefix = listOf(Tile(Suit.MAN, 2, 3), Tile(Suit.MAN, 3, 3), Tile(Suit.MAN, 4, 3), Tile(Suit.PIN, 5, 1)))
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        table(s).use { a ->
            for (rank in 1..4) {
                val kan = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DeclareKan>()
                    .first { it.kanType == MeldType.CLOSED_KAN && it.tiles.first().suit == Suit.MAN && it.tiles.first().rank == rank }
                assertTrue(a.coordinator.handleAction(kan) is ActionResult.Accepted)
                val before = state(a)
                assertEquals(rank, before.totalKanCount)
                assertEquals(rank, before.rinshanPointer)
                bloom(a, if (rank == 4) SleeveBloomKeep.SECOND else SleeveBloomKeep.FIRST, id = "kan-$rank")
                assertEquals(before.turnNumber, state(a).turnNumber)
                TestTableBuilder.assertPhysical(state(a))
            }
            assertEquals(4, state(a).totalKanCount)
            assertEquals(4, state(a).playerKanCounts[0])
            assertEquals(3, state(a).rinshanPointer)
            assertEquals(60, state(a).cheatPoints[0])
            assertTrue(a.queryDecision(0, false).options.none { it.action is GameAction.DeclareKan })
            StateInvariants.assertValid(state(a))
        }
    }

    @Test fun `second candidate can create a real tsumo and normal source never gets rinshan`() {
        val hand = listOf(
            Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0),
            Tile(Suit.PIN, 1, 0), Tile(Suit.PIN, 2, 0), Tile(Suit.PIN, 3, 0),
            Tile(Suit.SOU, 1, 0), Tile(Suit.SOU, 2, 0), Tile(Suit.SOU, 3, 0),
            Tile(Suit.SOU, 7, 0), Tile(Suit.SOU, 8, 0), Tile(Suit.SOU, 9, 0), Tile(Suit.PIN, 5, 0))
        val win = Tile(Suit.PIN, 5, 1)
        val s = TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.MAN, 9, 3),
            wallPrefix = listOf(win), phase = GamePhase.CHEAT_WINDOW)
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        table(s).use { a ->
            assertTrue(a.queryDecision(0, false).options.none { it.action is GameAction.DeclareTsumo })
            bloom(a, SleeveBloomKeep.SECOND)
            assertTrue(a.queryDecision(0, false).options.any { it.action is GameAction.DeclareTsumo })
            val finishedRound = a.coordinator.activeRound!!
            val result = a.coordinator.handleAction(GameAction.DeclareTsumo(0)) as ActionResult.Accepted
            val event = result.events.filterIsInstance<GameEvent.TsumoWin>().single()
            assertTrue(event.yakuList.isNotEmpty())
            assertTrue(event.yakuList.none { it.contains("岭上") || it.contains("地和") || it.contains("天和") })
            assertNull(finishedRound.readState().sleeveBloomOffer)
            TestTableBuilder.assertPhysical(finishedRound.readState())
        }
    }

    @Test fun `snapshot copy and round reset do not retain a pending offer`() {
        val s = TestTableBuilder.create(drawn = 0 to Tile(Suit.SOU, 9, 3), phase = GamePhase.CHEAT_WINDOW)
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        table(s).use { a ->
            val snapshot = snap(a)
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0, SleeveBloomRequest("start", snapshot.sessionId,
                snapshot.roundId!!, snapshot.decision.decisionId, snapshot.decision.controlEpoch), 1_000).receipt.status)
            val copy = state(a)
            assertNotNull(copy.sleeveBloomOffer)
            copy.resetForNewRound()
            assertNull(copy.sleeveBloomOffer)
            assertEquals(-1, copy.sleeveBloomUsedOnTurn)
            assertEquals(SleeveBloomDrawKind.DEAL_EXTRA, copy.lastDrawKind)
            assertNotNull(state(a).sleeveBloomOffer, "Resetting a copied state mutated committed state")
        }
    }

    @Test fun `rinshan replacement win and first nondealer draw retain their scoring context`() {
        val hand = listOf(
            Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0),
            Tile(Suit.PIN, 1, 0), Tile(Suit.PIN, 2, 0), Tile(Suit.PIN, 3, 0),
            Tile(Suit.SOU, 1, 0), Tile(Suit.SOU, 2, 0), Tile(Suit.SOU, 3, 0),
            Tile(Suit.SOU, 7, 0), Tile(Suit.SOU, 8, 0), Tile(Suit.SOU, 9, 0), Tile(Suit.PIN, 5, 0))
        val win = Tile(Suit.PIN, 5, 1)
        for (rinshan in listOf(true, false)) {
            val seat = if (rinshan) 0 else 1
            val s = if (rinshan) {
                TestTableBuilder.create(hands = mapOf(seat to hand), wallPrefix = listOf(win),
                    deadWallPrefix = listOf(Tile(Suit.MAN, 9, 3)), phase = GamePhase.KAN_DRAW).also {
                    val last = it.wall.lastIndex
                    val other = it.wall[last]
                    it.wall[last] = win
                    it.wall[0] = other
                    TileMovement.drawRinshan(it, seat)
                    it.phase = GamePhase.CHEAT_WINDOW
                    it.lastDrawKind = SleeveBloomDrawKind.RINSHAN
                    it.isRinshanDraw = true
                }
            } else {
                TestTableBuilder.create(hands = mapOf(seat to hand), drawn = seat to Tile(Suit.MAN, 9, 3),
                    wallPrefix = listOf(win), phase = GamePhase.CHEAT_WINDOW).also {
                    it.lastDrawKind = SleeveBloomDrawKind.NORMAL
                    it.isFirstUninterruptedGo = true
                    it.turnNumber = 1
                }
            }
            table(s).use { a ->
                bloom(a, SleeveBloomKeep.SECOND, seat)
                assertEquals(win, state(a).players[seat].drawnTile)
                val finishedRound = a.coordinator.activeRound!!
                val result = a.coordinator.handleAction(GameAction.DeclareTsumo(seat)) as ActionResult.Accepted
                val yaku = result.events.filterIsInstance<GameEvent.TsumoWin>().single().yakuList
                assertTrue(yaku.any { it.contains(if (rinshan) "岭上开花" else "地和") }, yaku.toString())
                assertNull(finishedRound.readState().sleeveBloomOffer)
                StateInvariants.assertValid(finishedRound.readState())
            }
        }
    }

    @Test fun `normal final draw cannot reveal nonexistent second candidate`() {
        val s = TestTableBuilder.create(drawn = 0 to Tile(Suit.SOU, 9, 3), phase = GamePhase.CHEAT_WINDOW)
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        for (tile in s.wall.subList(s.wallPointer, s.wallEndExclusive).toList()) {
            s.players[2].discards.add(tile)
            s.players[2].discardHistory.add(tile)
            s.tilePool.transfer(tile, TileLocation.WALL, TileLocation.discard(2))
        }
        s.wallPointer = s.wallEndExclusive
        StateInvariants.assertValid(s)
        table(s).use { a ->
            val snapshot = snap(a)
            val result = a.submitSleeveBloom(0, SleeveBloomRequest("empty", snapshot.sessionId, snapshot.roundId!!,
                snapshot.decision.decisionId, snapshot.decision.controlEpoch), 1_000)
            assertEquals("SLEEVE_BLOOM_WALL_EMPTY", result.receipt.reason)
            assertEquals(100, state(a).cheatPoints[0])
            assertNull(state(a).sleeveBloomOffer)
        }
    }
}
