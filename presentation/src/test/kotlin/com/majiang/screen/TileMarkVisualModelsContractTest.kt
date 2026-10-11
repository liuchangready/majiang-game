package com.majiang.screen

import com.majiang.client.api.*
import com.majiang.presentation.game.HandDisplaySlot
import com.majiang.presentation.game.OpponentHandPresentation
import com.majiang.presentation.game.OpponentHandRow
import com.majiang.presentation.game.RiverHintPresentation
import com.majiang.presentation.game.RiverHintView
import com.majiang.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/** Independent behavioral contract for the authorized visual design, using only client views. */
class TileMarkVisualModelsContractTest {
    private fun marked(id: Int, area: TileMarkArea = TileMarkArea.OPPONENT_HAND, seat: Int? = 1) =
        MarkedTileView(id, TestViewBuilder.tile(id), area, seat,
            if (area == TileMarkArea.DISCARD) RiverPositionView("round", seat!!, 0) else null)

    private fun snapshot(
        marks: List<MarkedTileView> = emptyList(),
        viewer: Int = 0,
        dealer: Int = 0,
        counts: List<Int> = listOf(14, 13, 13, 13),
        remaining: Int = 42,
        phase: GamePhaseView = GamePhaseView.DISCARD
    ): GameSnapshotView {
        // Every real group contains four physical entities, including hidden marks.
        val padded = marks.toMutableList()
        var fillerId = 135
        while (padded.size % 4 != 0) {
            while (padded.any { it.tileId == fillerId }) fillerId--
            padded.add(marked(fillerId--, TileMarkArea.NOT_VISIBLE, null))
        }
        val groups = padded.chunked(4).mapIndexed { index, tiles ->
            TileMarkGroupView("group-$index", "round", 1, 4, 3, false, tiles)
        }
        val rivers = (0..3).map { seat -> marks.filter { it.area == TileMarkArea.DISCARD && it.seat == seat }.map { it.tile } }
        return GameSnapshotView("session", "round", "epoch", 1, 1, 1,
            TestViewBuilder.playerView(viewerSeat = viewer, dealerSeat = dealer, phase = phase,
                handTileCounts = counts, wallRemaining = remaining, discards = rivers,
                myClosedTiles = (0..12).map(TestViewBuilder::tile), myDrawnTile = TestViewBuilder.tile(13)),
            DecisionView("round", "decision", 0),
            tileMark = TileMarkView(false, "NOT_YOUR_TURN", activeTileCount = padded.size,
                decisionId = "decision", controlEpoch = 0, stateVersion = 1, activeGroups = groups))
    }

    private fun faces(row: OpponentHandRow) = row.slots.mapNotNull { it.tile?.tileId }
    private fun assertBacks(rows: List<OpponentHandRow>) =
        assertTrue(rows.all { row -> row.slots.all { it.tile == null } })

    @Test
    fun `null snapshot has no fabricated hand rows or wall count`() {
        assertTrue(OpponentHandPresentation.build(null).isEmpty())
        val hint = RiverHintPresentation.build(null)
        assertNull(hint.wallRemaining)
        assertTrue(hint.hands.isEmpty())
    }

    @Test
    fun `all viewer seats have exactly downstream opposite upstream rows`() {
        for (viewer in 0..3) {
            val rows = OpponentHandPresentation.build(snapshot(viewer = viewer))
            assertEquals((1..3).map { (viewer + it) % 4 }, rows.map { it.seat })
            assertFalse(rows.any { it.seat == viewer })
        }
    }

    @Test
    fun `zero one thirteen fourteen and post meld counts are read from each snapshot`() {
        for (count in listOf(0, 1, 4, 7, 10, 13, 14)) {
            val state = snapshot(counts = List(4) { count })
            val opponents = OpponentHandPresentation.build(state)
            val hint = RiverHintPresentation.build(state)
            assertEquals(List(3) { count }, opponents.map { it.slots.size })
            assertEquals(List(4) { count }, hint.hands.map { it.slots.size })
            assertBacks(opponents)
            assertBacks(hint.hands)
        }
    }

    @Test
    fun `two known physical tiles replace two of thirteen backs`() {
        val state = snapshot(listOf(marked(4), marked(0)))
        val row = OpponentHandPresentation.build(state).single { it.seat == 1 }
        assertEquals(13, row.slots.size)
        assertEquals(11, row.slots.count { it.tile == null })
        assertEquals(listOf(0, 4), faces(row))
        assertEquals(listOf(0, 4), faces(RiverHintPresentation.build(state).hands.single { it.seat == 1 }))
    }

    @Test
    fun `face order uses type index then entity and is independent of mark submission order`() {
        // SOU has lower typeIndex than PIN, despite the SuitView enum ordering.
        val state = snapshot(listOf(74, 39, 72, 36).map { marked(it) })
        val rows = listOf(OpponentHandPresentation.build(state).first(), RiverHintPresentation.build(state).hands[1])
        rows.forEach { row ->
            assertEquals(listOf(36, 39, 72, 74), faces(row))
            val indices = row.slots.indices.filter { row.slots[it].tile != null }
            assertTrue(indices == (0..3).toList() || indices == (9..12).toList(), "Known faces must occupy one end")
        }
    }

    @Test
    fun `same type copies including red five remain distinct physical slots`() {
        val state = snapshot((16..19).map { marked(it) })
        val row = OpponentHandPresentation.build(state).first()
        assertEquals(listOf(16, 17, 18, 19), faces(row))
        assertEquals(1, row.slots.count { it.tile?.isRedFive == true })
        assertEquals(13, row.slots.size)
    }

    @Test
    fun `all twelve marks can occupy a single opponent hand without extra tiles`() {
        val state = snapshot((0..11).map { marked(it) })
        for (row in listOf(OpponentHandPresentation.build(state).first(), RiverHintPresentation.build(state).hands[1])) {
            assertEquals((0..11).toList(), faces(row))
            assertEquals(13, row.slots.size)
            assertEquals(1, row.slots.count { it.tile == null })
        }
    }

    @Test
    fun `marks only reveal the specified opponent and can be distributed across three seats`() {
        val state = snapshot(listOf(marked(0, seat = 1), marked(4, seat = 2), marked(8, seat = 3)))
        val rows = OpponentHandPresentation.build(state)
        assertEquals(listOf(listOf(0), listOf(4), listOf(8)), rows.map(::faces))
        assertEquals(39, rows.sumOf { it.slots.size })
    }

    @Test
    fun `hidden marks never get assigned an inferred holder`() {
        val state = snapshot((0..3).map { marked(it, TileMarkArea.NOT_VISIBLE, null) })
        assertBacks(OpponentHandPresentation.build(state))
        assertBacks(RiverHintPresentation.build(state).hands)
    }

    @Test
    fun `discard exposed meld and indicator marks never flip concealed hand slots`() {
        val state = snapshot(listOf(marked(0, TileMarkArea.DISCARD, 1),
            marked(4, TileMarkArea.EXPOSED_MELD, 2), marked(8, TileMarkArea.EXPOSED_INDICATOR, null),
            marked(12, TileMarkArea.NOT_VISIBLE, null)))
        assertBacks(OpponentHandPresentation.build(state))
        assertBacks(RiverHintPresentation.build(state).hands)
    }

    @Test
    fun `hint own row reveals only own marked tiles not the ordinary private hand`() {
        val state = snapshot(listOf(marked(0, TileMarkArea.OWN_HAND, 0), marked(13, TileMarkArea.OWN_HAND, 0)))
        val own = RiverHintPresentation.build(state).hands.single { it.seat == 0 }
        assertEquals(14, own.slots.size)
        assertEquals(listOf(0, 13), faces(own))
        assertEquals(12, own.slots.count { it.tile == null })
        assertBacks(OpponentHandPresentation.build(state))
    }

    @Test
    fun `area and viewer role must agree before a hand face is revealed`() {
        val state = snapshot(listOf(marked(0, TileMarkArea.OPPONENT_HAND, 0), marked(4, TileMarkArea.OWN_HAND, 1)))
        assertBacks(OpponentHandPresentation.build(state))
        assertBacks(RiverHintPresentation.build(state).hands)
    }

    @Test
    fun `marks exceeding seat concealed count safely become backs without truncating authority`() {
        val state = snapshot((0..3).map { marked(it) }, counts = listOf(14, 1, 13, 13))
        val opponent = OpponentHandPresentation.build(state).first()
        val hint = RiverHintPresentation.build(state).hands.single { it.seat == 1 }
        assertEquals(1, opponent.slots.size)
        assertEquals(1, hint.slots.size)
        assertBacks(listOf(opponent, hint))
        val zero = state.copy(view = state.view.copy(handTileCounts = listOf(14, 0, 13, 13)))
        assertTrue(OpponentHandPresentation.build(zero).first().slots.isEmpty())
    }

    @Test
    fun `east south west north follow dealer for all sixteen viewer and dealer combinations`() {
        for (viewer in 0..3) for (dealer in 0..3) {
            val state = snapshot(viewer = viewer, dealer = dealer, counts = listOf(14, 13, 10, 7))
            val rows = RiverHintPresentation.build(state).hands
            assertEquals((0..3).map { (dealer + it) % 4 }, rows.map { it.seat })
            assertEquals(listOf(1, 2, 3, 4), rows.map { state.view.seatWind(it.seat) })
            rows.forEach { assertEquals(state.view.handTileCounts[it.seat], it.slots.size) }
        }
    }

    @Test
    fun `viewer own mark follows their wind when dealer changes`() {
        for (viewer in 0..3) for (dealer in 0..3) {
            val state = snapshot(listOf(marked(0, TileMarkArea.OWN_HAND, viewer)), viewer, dealer)
            val hint = RiverHintPresentation.build(state)
            assertEquals(listOf(0), faces(hint.hands.single { it.seat == viewer }))
            assertBacks(hint.hands.filter { it.seat != viewer })
        }
    }

    @Test
    fun `wall count is the exact authorized value including zero across draw and kan phases`() {
        for (phase in listOf(GamePhaseView.DRAW, GamePhaseView.DISCARD, GamePhaseView.KAN_DRAW,
            GamePhaseView.RESPONSE_WINDOW, GamePhaseView.ROUND_END)) {
            for (remaining in listOf(70, 42, 1, 0)) {
                assertEquals(remaining, RiverHintPresentation.build(snapshot(remaining = remaining, phase = phase)).wallRemaining)
            }
        }
    }

    @Test
    fun `stacking has unknown remaining count and no previous hand positions`() {
        val state = snapshot((0..3).map { marked(it) }, remaining = 70, phase = GamePhaseView.STACKING)
        val hint = RiverHintPresentation.build(state)
        assertNull(hint.wallRemaining)
        assertTrue(hint.hands.all { it.slots.isEmpty() })
        assertTrue(OpponentHandPresentation.build(state).all { it.slots.isEmpty() })
    }

    @Test
    fun `snapshot replacement drives counts directly without animation arithmetic or retained values`() {
        val start = snapshot(listOf(marked(0)))
        val before = RiverHintPresentation.build(start)
        val afterDraw = start.copy(cursor = 2, view = start.view.copy(wallRemaining = 41,
            handTileCounts = listOf(14, 14, 13, 13)))
        val first = RiverHintPresentation.build(afterDraw)
        val repeated = RiverHintPresentation.build(afterDraw)
        assertEquals(42, before.wallRemaining)
        assertEquals(41, first.wallRemaining)
        assertEquals(41, repeated.wallRemaining)
        assertEquals(13, before.hands[1].slots.size)
        assertEquals(14, first.hands[1].slots.size)
        assertEquals(listOf(0), faces(first.hands[1]))
    }

    @Test
    fun `expiration empty groups and absent private projection immediately restore backs`() {
        val start = snapshot(listOf(marked(0)))
        assertEquals(listOf(0), faces(OpponentHandPresentation.build(start).first()))
        for (expired in listOf(start.copy(tileMark = null), snapshot())) {
            assertBacks(OpponentHandPresentation.build(expired))
            assertBacks(RiverHintPresentation.build(expired).hands)
        }
    }

    @Test
    fun `final effective round remains visible until authoritative group disappears`() {
        val start = snapshot(listOf(marked(0)))
        val mark = start.tileMark!!
        val group = mark.activeGroups.single().copy(futureRoundsRemaining = 0, isLastEffectiveRound = true)
        val last = start.copy(tileMark = mark.copy(activeGroups = listOf(group)))
        assertEquals(listOf(0), faces(OpponentHandPresentation.build(last).first()))
        assertEquals(listOf(0), faces(RiverHintPresentation.build(last).hands[1]))
    }

    @Test
    fun `reconnect and next round replace holder and remaining count without stale knowledge`() {
        val start = snapshot(listOf(marked(0, seat = 1)))
        val oldRows = OpponentHandPresentation.build(start)
        val next = snapshot(listOf(marked(0, seat = 3)), dealer = 1, remaining = 70).copy(
            roundId = "next-round", syncEpoch = "reconnected", cursor = 0,
            decision = DecisionView("next-round", "next-decision", 0))
        val newRows = OpponentHandPresentation.build(next)
        assertEquals(listOf(0), faces(oldRows.single { it.seat == 1 }))
        assertTrue(faces(newRows.single { it.seat == 1 }).isEmpty())
        assertEquals(listOf(0), faces(newRows.single { it.seat == 3 }))
        val hint = RiverHintPresentation.build(next)
        assertEquals(70, hint.wallRemaining)
        assertEquals(listOf(1, 2, 3, 0), hint.hands.map { it.seat })
    }

    @Test
    fun `different viewer projection cannot retain another viewers remembered mark`() {
        val privateA = snapshot(listOf(marked(0)), viewer = 0)
        assertFalse(faces(OpponentHandPresentation.build(privateA).first()).isEmpty())
        val privateB = snapshot(viewer = 2).copy(sessionId = "another-session", syncEpoch = "another-epoch")
        assertBacks(OpponentHandPresentation.build(privateB))
        assertBacks(RiverHintPresentation.build(privateB).hands)
    }

    @Test
    fun `own selection eligibility never grants extra faces or suppresses existing marks`() {
        val start = snapshot(listOf(marked(0)))
        val changed = start.copy(tileMark = start.tileMark!!.copy(canApply = true, disabledReason = null,
            selectableTileIds = (0..13).toList()))
        assertEquals(listOf(0), faces(OpponentHandPresentation.build(start).first()))
        assertEquals(listOf(0), faces(OpponentHandPresentation.build(changed).first()))
        assertTrue(faces(RiverHintPresentation.build(changed).hands.first()).isEmpty())
    }

    @Test
    fun `rows and hint constructors detach from mutable input lists`() {
        val inputSlots = mutableListOf(HandDisplaySlot(TestViewBuilder.tile(0)), HandDisplaySlot(null))
        val row = OpponentHandRow(1, inputSlots)
        val inputRows = mutableListOf(row)
        val hint = RiverHintView(42, inputRows)
        inputSlots.clear()
        inputRows.clear()
        assertEquals(2, row.slots.size)
        assertEquals(1, hint.hands.size)
        assertEquals(listOf(0), faces(hint.hands.single()))
        assertEquals(2, hint.hands.single().slots.size)
        assertThrows(UnsupportedOperationException::class.java) {
            (row.slots as MutableList<HandDisplaySlot>).clear()
        }
        assertThrows(UnsupportedOperationException::class.java) {
            (hint.hands as MutableList<OpponentHandRow>).clear()
        }
    }

    @Test
    fun `all outward lists reject mutations including nested slots`() {
        val state = snapshot(listOf(marked(0)))
        val opponents = OpponentHandPresentation.build(state)
        val hint = RiverHintPresentation.build(state)
        assertThrows(UnsupportedOperationException::class.java) {
            (opponents as MutableList<OpponentHandRow>).clear()
        }
        assertThrows(UnsupportedOperationException::class.java) {
            (hint.hands as MutableList<OpponentHandRow>).clear()
        }
        for (row in opponents + hint.hands) {
            assertThrows(UnsupportedOperationException::class.java) {
                (row.slots as MutableList<HandDisplaySlot>)[0] = HandDisplaySlot(TestViewBuilder.tile(100))
            }
        }
        assertEquals(listOf(0), faces(OpponentHandPresentation.build(state).first()))
    }
}
