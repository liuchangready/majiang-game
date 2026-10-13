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

    @Test fun `rinshan swap only exchanges next slot without touching wall replacements or pointers`() {
        val s = TestTableBuilder.create(phase = GamePhase.KAN_DRAW)
        val first = TileMovement.drawRinshan(s, 0)
        s.phase = GamePhase.CHEAT_WINDOW
        val next = s.deadWall[s.rinshanPointer]
        val liveWall = s.wall.toList()
        val deadWall = s.deadWall.toList()
        val replacements = s.deadWallReplacements.toList()
        val end = s.wallEndExclusive
        val pointer = s.wallPointer
        assertEquals(next, TileMovement.swapDrawnWithNextRinshan(s, 0))
        assertEquals(next, s.players[0].drawnTile)
        assertEquals(1, s.rinshanPointer)
        assertEquals(first, s.deadWall[1])
        assertEquals(deadWall.filterIndexed { i, _ -> i != 1 }, s.deadWall.filterIndexed { i, _ -> i != 1 })
        assertEquals(TileLocation.DEAD_WALL, s.tilePool.getLocation(first))
        assertEquals(TileLocation.hand(0), s.tilePool.getLocation(next))
        assertEquals(replacements, s.deadWallReplacements)
        assertEquals(liveWall, s.wall)
        assertEquals(pointer, s.wallPointer)
        assertEquals(end, s.wallEndExclusive)
        TestTableBuilder.assertPhysical(s)
        StateInvariants.assertValid(s)
        val before = TestTableBuilder.actualTiles(s).toList()
        assertThrows<IllegalArgumentException> { TileMovement.swapDrawnWithNextRinshan(s, 0) }
        assertEquals(before, TestTableBuilder.actualTiles(s), "Rejected second physical swap mutated containers")
        TileMovement.discard(s, 0, next)
        assertEquals(first, TileMovement.drawRinshan(s, 0), "Next real kan must draw the unselected original rinshan")
        assertEquals(2, s.rinshanPointer)
        StateInvariants.assertValid(s)
        TestTableBuilder.assertPhysical(s)
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

    @Test fun `all three actual kan paths offer next rinshan and retain counts with either choice`() {
        for (kind in listOf(MeldType.CLOSED_KAN, MeldType.ADDED_KAN, MeldType.OPEN_KAN)) for (keep in SleeveBloomKeep.entries) {
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
                val next = before.deadWall[before.rinshanPointer]
                bloom(a, keep)
                val after = state(a)
                assertEquals(if (keep == SleeveBloomKeep.SECOND) next else original, after.players[0].drawnTile)
                assertEquals(if (keep == SleeveBloomKeep.SECOND) original else next, after.deadWall[after.rinshanPointer])
                assertEquals(before.rinshanPointer, after.rinshanPointer)
                assertEquals(before.deadWallReplacements, after.deadWallReplacements)
                assertEquals(before.wall, after.wall)
                assertEquals(before.wallPointer, after.wallPointer)
                assertEquals(before.wallEndExclusive, after.wallEndExclusive)
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
        // The actual kan cap remains authoritative even if supplied a stale pointer.
        assertEquals(0, s.rinshanPointer)
        table(s).use { a ->
            assertTrue(a.queryDecision(1, false).options.none { it.action is GameAction.DeclareKan })
            assertTrue(a.coordinator.handleAction(GameAction.DeclareKan(1, fifth, MeldType.CLOSED_KAN)) is ActionResult.Rejected)
            assertEquals(4, state(a).totalKanCount)
            TestTableBuilder.assertPhysical(state(a))
        }
    }

    @Test fun `consecutive kans preserve next rinshan order and fourth kan rejects bloom without charge`() {
        val triples = (1..4).flatMap { rank -> (0..2).map { Tile(Suit.MAN, rank, it) } }
        val s = TestTableBuilder.create(hands = mapOf(0 to (triples + Tile(Suit.PIN, 5, 0))),
            drawn = 0 to Tile(Suit.MAN, 1, 3), phase = GamePhase.CHEAT_WINDOW,
            deadWallPrefix = listOf(Tile(Suit.MAN, 2, 3), Tile(Suit.MAN, 3, 3), Tile(Suit.MAN, 4, 3), Tile(Suit.PIN, 5, 1)))
        s.lastDrawKind = SleeveBloomDrawKind.NORMAL
        table(s).use { a ->
            for ((index, rank) in listOf(1, 3, 2, 4).withIndex()) {
                val kan = a.queryDecision(0, false).options.map { it.action }.filterIsInstance<GameAction.DeclareKan>()
                    .first { it.kanType == MeldType.CLOSED_KAN && it.tiles.first().suit == Suit.MAN && it.tiles.first().rank == rank }
                assertTrue(a.coordinator.handleAction(kan) is ActionResult.Accepted)
                val before = state(a)
                assertEquals(index + 1, before.totalKanCount)
                assertEquals(index + 1, before.rinshanPointer)
                if (index < 3) {
                    bloom(a, if (index == 0) SleeveBloomKeep.SECOND else SleeveBloomKeep.FIRST, id = "kan-$rank")
                } else {
                    a.syncDecisionsAndClocks(1_000, emptySet())
                    val snapshot = snap(a)
                    assertFalse(snapshot.sleeveBloom!!.canApply)
                    assertEquals("SLEEVE_BLOOM_WALL_EMPTY", snapshot.sleeveBloom!!.disabledReason)
                    val request = SleeveBloomRequest("fourth", snapshot.sessionId, snapshot.roundId!!,
                        snapshot.decision.decisionId, snapshot.decision.controlEpoch)
                    val result = a.submitSleeveBloom(0, request, 1_000)
                    assertEquals("SLEEVE_BLOOM_WALL_EMPTY", result.receipt.reason)
                    assertFalse(result.stateChanged)
                    assertEquals(result.receipt, a.submitSleeveBloom(0, request, 1_000).receipt)
                    assertEquals(before.players[0].drawnTile, state(a).players[0].drawnTile)
                    assertEquals(before.deadWall, state(a).deadWall)
                    assertEquals(before.deadWallReplacements, state(a).deadWallReplacements)
                    assertNull(state(a).sleeveBloomOffer)
                }
                assertEquals(before.turnNumber, state(a).turnNumber)
                TestTableBuilder.assertPhysical(state(a))
            }
            assertEquals(4, state(a).totalKanCount)
            assertEquals(4, state(a).playerKanCounts[0])
            assertEquals(4, state(a).rinshanPointer)
            assertEquals(70, state(a).cheatPoints[0])
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

    @Test fun `next rinshan win and first nondealer draw retain their scoring context`() {
        val hand = listOf(
            Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0),
            Tile(Suit.PIN, 1, 0), Tile(Suit.PIN, 2, 0), Tile(Suit.PIN, 3, 0),
            Tile(Suit.SOU, 1, 0), Tile(Suit.SOU, 2, 0), Tile(Suit.SOU, 3, 0),
            Tile(Suit.SOU, 7, 0), Tile(Suit.SOU, 8, 0), Tile(Suit.SOU, 9, 0), Tile(Suit.PIN, 5, 0))
        val win = Tile(Suit.PIN, 5, 1)
        for (rinshan in listOf(true, false)) {
            val seat = if (rinshan) 0 else 1
            val s = if (rinshan) {
                TestTableBuilder.create(hands = mapOf(seat to hand),
                    deadWallPrefix = listOf(Tile(Suit.MAN, 9, 3), win), phase = GamePhase.KAN_DRAW).also {
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

    private fun rinshanState(): GameState = TestTableBuilder.create(phase = GamePhase.KAN_DRAW).also {
        TileMovement.drawRinshan(it, 0)
        it.phase = GamePhase.CHEAT_WINDOW
        it.lastDrawKind = SleeveBloomDrawKind.RINSHAN
        it.isRinshanDraw = true
        it.totalKanCount = 1
        it.playerKanCounts[0] = 1
    }

    @Test fun `rinshan offer reveals only next rinshan and timeout keeps original without moving wall`() {
        table(rinshanState()).use { a ->
            val before = state(a)
            val snapshot = snap(a)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            val request = SleeveBloomRequest("rinshan-offer", snapshot.sessionId, snapshot.roundId!!,
                snapshot.decision.decisionId, snapshot.decision.controlEpoch)
            val receipt = a.submitSleeveBloom(0, request, 1_000).receipt
            assertEquals(ReceiptStatus.APPLIED, receipt.status)
            val offered = state(a)
            val offer = snap(a).sleeveBloom!!.pendingOffer!!
            assertEquals(before.players[0].drawnTile, offer.firstTile)
            assertEquals(before.deadWall[before.rinshanPointer], offer.secondTile)
            assertNotEquals(before.deadWallReplacements.last(), offer.secondTile)
            assertNotEquals(before.wall[before.wallPointer], offer.secondTile)
            assertEquals(SleeveBloomDrawKind.RINSHAN, offer.drawKind)
            assertEquals(before.deadWall, offered.deadWall)
            assertEquals(before.deadWallReplacements, offered.deadWallReplacements)
            assertEquals(before.wall, offered.wall)
            assertEquals(TestTableBuilder.actualTiles(before), TestTableBuilder.actualTiles(offered))
            assertEquals(90, offered.cheatPoints[0])
            assertEquals(receipt, a.submitSleeveBloom(0, request, 1_000).receipt)
            for (seat in 1..3) assertNull(snap(a, seat).sleeveBloom?.pendingOffer)
            assertEquals(deadline, a.decisionClock.timing(0)!!.deadlineMillis)
            assertNotNull(a.checkTimeout(deadline))
            val after = state(a)
            assertNull(after.sleeveBloomOffer)
            assertEquals(offer.firstTile, after.players[0].discards.last())
            assertEquals(before.deadWall, after.deadWall)
            assertEquals(before.deadWallReplacements, after.deadWallReplacements)
            assertEquals(before.rinshanPointer, after.rinshanPointer)
            assertEquals(90, after.cheatPoints[0])
            TestTableBuilder.assertPhysical(after)
        }
    }

    @Test fun `changed next rinshan rejects stale choice and never falls back to replacement`() {
        table(rinshanState()).use { a ->
            val snapshot = snap(a)
            assertEquals(ReceiptStatus.APPLIED, a.submitSleeveBloom(0,
                SleeveBloomRequest("stale-start", snapshot.sessionId, snapshot.roundId!!,
                    snapshot.decision.decisionId, snapshot.decision.controlEpoch), 1_000).receipt.status)
            val offered = snap(a)
            val request = SleeveBloomChoiceRequest("stale-choice", offered.sessionId, offered.roundId!!,
                offered.decision.decisionId, offered.decision.controlEpoch,
                offered.sleeveBloom!!.pendingOffer!!.offerId, SleeveBloomKeep.SECOND)
            val live = a.coordinator.activeRound!!.state
            val nextIndex = live.rinshanPointer
            val oldNext = live.deadWall[nextIndex]
            live.deadWall[nextIndex] = live.deadWall[nextIndex + 1]
            live.deadWall[nextIndex + 1] = oldNext
            val before = state(a)
            val deadline = a.decisionClock.timing(0)!!.deadlineMillis
            val result = a.submitSleeveBloomChoice(0, request, 1_000)
            assertEquals("SLEEVE_BLOOM_STALE_OFFER", result.receipt.reason)
            assertTrue(result.stateChanged)
            assertNull(state(a).sleeveBloomOffer)
            assertEquals(TestTableBuilder.actualTiles(before), TestTableBuilder.actualTiles(state(a)))
            assertEquals(before.deadWall, state(a).deadWall)
            assertEquals(deadline, a.decisionClock.timing(0)!!.deadlineMillis)
            assertEquals(90, state(a).cheatPoints[0])
            TestTableBuilder.assertPhysical(state(a))
        }
    }

    @Test fun `remaining live wall is irrelevant when next rinshan exists`() {
        val s = rinshanState()
        for (tile in s.wall.subList(s.wallPointer, s.wallEndExclusive).toList()) {
            s.players[2].discards.add(tile)
            s.players[2].discardHistory.add(tile)
            s.tilePool.transfer(tile, TileLocation.WALL, TileLocation.discard(2))
        }
        s.wallPointer = s.wallEndExclusive
        StateInvariants.assertValid(s)
        table(s).use { a ->
            val next = s.deadWall[s.rinshanPointer]
            assertTrue(snap(a).sleeveBloom!!.canApply)
            bloom(a, SleeveBloomKeep.SECOND)
            assertEquals(next, state(a).players[0].drawnTile)
            assertEquals(0, state(a).wallRemaining)
            assertEquals(s.deadWallReplacements, state(a).deadWallReplacements)
            TestTableBuilder.assertPhysical(state(a))
        }
    }

    @Test fun `fourth kan rejects even with stale remaining rinshan pointer and preserves receipt`() {
        val s = rinshanState().also { it.totalKanCount = 4; it.playerKanCounts[0] = 4 }
        val physical = TestTableBuilder.actualTiles(s)
        assertThrows<IllegalArgumentException> { TileMovement.swapDrawnWithNextRinshan(s, 0) }
        assertEquals(physical, TestTableBuilder.actualTiles(s))
        table(s).use { a ->
            val snapshot = snap(a)
            assertFalse(snapshot.sleeveBloom!!.canApply)
            val request = SleeveBloomRequest("fourth-stale-pointer", snapshot.sessionId, snapshot.roundId!!,
                snapshot.decision.decisionId, snapshot.decision.controlEpoch)
            val before = state(a)
            assertTrue(a.coordinator.submitSleeveBloom(0, request, 1_000) is ActionResult.Rejected)
            val receipt = a.submitSleeveBloom(0, request, 1_000)
            assertEquals("SLEEVE_BLOOM_WALL_EMPTY", receipt.receipt.reason)
            assertFalse(receipt.stateChanged)
            assertEquals(receipt.receipt, a.submitSleeveBloom(0, request, 1_000).receipt)
            assertEquals(100, state(a).cheatPoints[0])
            assertEquals(TestTableBuilder.actualTiles(before), TestTableBuilder.actualTiles(state(a)))
            assertEquals(before.deadWall, state(a).deadWall)
            assertNull(state(a).sleeveBloomOffer)
            TestTableBuilder.assertPhysical(state(a))
        }
    }

    @Test fun `third kan may choose fourth rinshan but never dora and fourth rejects physical swap`() {
        val s = rinshanState()
        repeat(2) {
            TileMovement.discard(s, 0, s.players[0].drawnTile!!)
            TileMovement.drawRinshan(s, 0)
        }
        s.totalKanCount = 3
        s.playerKanCounts[0] = 3
        val original = s.players[0].drawnTile!!
        val fourth = s.deadWall[3]
        val indicatorSlots = s.deadWall.drop(4)
        table(s).use { a ->
            bloom(a, SleeveBloomKeep.SECOND)
            val after = state(a)
            assertEquals(fourth, after.players[0].drawnTile)
            assertEquals(original, after.deadWall[3])
            assertEquals(3, after.rinshanPointer)
            assertEquals(indicatorSlots, after.deadWall.drop(4))
            assertEquals(s.deadWallReplacements, after.deadWallReplacements)
            TileMovement.discard(after, 0, fourth)
            assertEquals(original, TileMovement.drawRinshan(after, 0))
            after.totalKanCount = 4
            after.playerKanCounts[0] = 4
            val physical = TestTableBuilder.actualTiles(after)
            assertThrows<IllegalArgumentException> { TileMovement.swapDrawnWithNextRinshan(after, 0) }
            assertEquals(physical, TestTableBuilder.actualTiles(after))
            assertEquals(indicatorSlots, after.deadWall.drop(4))
            StateInvariants.assertValid(after)
            TestTableBuilder.assertPhysical(after)
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
