package com.majiang.cheat.mark

import com.majiang.model.Suit
import com.majiang.model.Tile
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Codex-owned model contracts; authority and lifecycle behavior require separate integration tests. */
class TileMarkModelContractTest {
    private fun group(
        ids: List<Int> = listOf(0, 1, 2, 3),
        seat: Int = 0,
        created: Int = 1,
        expires: Int = created + 3
    ) = TileMarkGroup("group-1", seat, ids, "round-1", created, expires)

    @Test
    fun `pending selection cannot change when caller edits its list`() {
        val selection = mutableListOf(0, 1, 2, 3)
        val request = TileMarkRequest("request-1", "session-1", "round-1", "decision-1", 0L, selection)
        selection[0] = 135
        selection.clear()
        assertEquals(listOf(0, 1, 2, 3), request.tileIds)
        assertThrows<UnsupportedOperationException> {
            (request.tileIds as MutableList<Int>)[0] = 135
        }
        assertEquals(listOf(0, 1, 2, 3), request.tileIds)
    }

    @Test
    fun `installed group cannot be retargeted through source or returned collection`() {
        val selection = mutableListOf(132, 133, 134, 135)
        val marked = group(selection)
        selection.clear()
        assertEquals(listOf(132, 133, 134, 135), marked.tileIds)
        assertThrows<UnsupportedOperationException> {
            (marked.tileIds as MutableList<Int>).clear()
        }
        assertEquals(listOf(132, 133, 134, 135), marked.tileIds)
    }

    @Test
    fun `four copies of the same face remain four distinct physical marks including red five`() {
        val copies = (0..3).map { Tile(Suit.MAN, 5, it) }
        val marked = group(copies.map { it.tileId })
        assertEquals(4, marked.tileIds.toSet().size)
        assertEquals(copies.map { it.tileId }, marked.tileIds)
    }

    @Test
    fun `group rejects incomplete duplicate and out of deck identities`() {
        val invalidSelections = listOf(
            emptyList(), listOf(0, 1, 2), listOf(0, 1, 2, 3, 4),
            listOf(0, 0, 1, 2), listOf(-1, 0, 1, 2), listOf(0, 1, 2, 136)
        )
        for (ids in invalidSelections) {
            assertThrows<IllegalArgumentException>("Invalid physical selection: $ids") { group(ids) }
        }
    }

    @Test
    fun `group rejects non player owners`() {
        for (seat in listOf(-1, 4, Int.MAX_VALUE)) {
            assertThrows<IllegalArgumentException>("Invalid owner: $seat") { group(seat = seat) }
        }
        for (seat in 0..3) assertEquals(seat, group(seat = seat).ownerSeat)
    }

    @Test
    fun `last effective ordinal includes current round and exactly three future rounds`() {
        for (created in listOf(1, 2, 20)) {
            assertEquals(created + 3, group(created = created).expiresAfterRoundOrdinal)
            assertThrows<IllegalArgumentException> { group(created = created, expires = created + 2) }
            assertThrows<IllegalArgumentException> { group(created = created, expires = created + 4) }
        }
    }

    @Test
    fun `group cannot be created before first deal or with overflowing expiry`() {
        for (created in listOf(Int.MIN_VALUE, -1, 0, Int.MAX_VALUE - 2, Int.MAX_VALUE)) {
            assertThrows<IllegalArgumentException>("Invalid dealt round ordinal: $created") {
                group(created = created)
            }
        }
        assertEquals(Int.MAX_VALUE, group(created = Int.MAX_VALUE - 3).expiresAfterRoundOrdinal)
    }

    @Test
    fun `river position uses zero based index for all four seats`() {
        for (seat in 0..3) {
            for (index in listOf(0, 5, 6, 17, 18)) {
                val position = RiverPosition("round-1", seat, index)
                assertEquals("round-1", position.roundId)
                assertEquals(seat, position.seat)
                assertEquals(index, position.discardIndex)
            }
        }
        assertThrows<IllegalArgumentException> { RiverPosition("round-1", 0, -1) }
    }

    @Test
    fun `feature is opt in for existing engine callers`() {
        assertFalse(TileMarkConfig().enabled)
    }
}
