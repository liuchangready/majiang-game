package com.majiang.cheat.mark

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

/** Independent state-transition expectations, before the authority integration batch. */
class TileMarkStateContractTest {
    private fun group(id: String, round: Int = 1, seat: Int = 0, firstTile: Int = 0) =
        TileMarkGroup(id, seat, (firstTile until firstTile + 4).toList(), "r$round", round, round + 3)

    @Test
    fun `only a new successful deal advances ordinal and old callbacks cannot rewind current round`() {
        val empty = TileMarkState()
        assertEquals(0, empty.dealtRoundOrdinal)
        assertTrue(empty.groups.isEmpty())
        val first = empty.registerDealtRound("r1")
        assertEquals(1, first.dealtRoundOrdinal)
        assertEquals(empty.version + 1, first.version)
        assertSame(first, first.registerDealtRound("r1"))
        assertThrows<IllegalArgumentException> { first.registerDealtRound("r2") }
        val second = first.completeRound("r1").registerDealtRound("r2")
        assertSame(second, second.registerDealtRound("r1"))
        assertEquals(listOf("r1", "r2"), second.dealtRoundIds)
        assertEquals(2, second.dealtRoundOrdinal)
        assertEquals(0, empty.dealtRoundOrdinal)
        assertThrows<IllegalArgumentException> { empty.registerDealtRound(" ") }
    }

    @Test
    fun `mark is active in casting round and all three future rounds until last result accepted`() {
        val marked = TileMarkState().registerDealtRound("r1").addGroup(group("A"))
        var state = marked
        for (round in 1..4) {
            assertEquals(listOf("A"), state.groups.map { it.groupId }, "Start of r$round")
            val settled = state.completeRound("r$round")
            assertEquals(if (round == 4) 0 else 1, settled.groups.size)
            assertSame(settled, settled.completeRound("r$round"))
            state = if (round < 4) settled.registerDealtRound("r${round + 1}") else settled
        }
        assertEquals(1, marked.groups.size, "Old state must not expire retroactively")
        assertEquals(setOf("r1", "r2", "r3", "r4"), state.settledRoundIds)
        assertThrows<IllegalArgumentException> { state.addGroup(group("A")) }
    }

    @Test
    fun `staggered groups expire independently and release exactly four slots`() {
        var state = TileMarkState().registerDealtRound("r1").addGroup(group("A"))
        state = state.completeRound("r1").registerDealtRound("r2").addGroup(group("B", 2, firstTile = 4))
        state = state.completeRound("r2").registerDealtRound("r3").addGroup(group("C", 3, firstTile = 8))
        state = state.completeRound("r3").registerDealtRound("r4")
        assertEquals(12, state.groupsForSeat(0).sumOf { it.tileIds.size })
        assertThrows<IllegalArgumentException> { state.addGroup(group("D", 4, firstTile = 12)) }
        state = state.completeRound("r4")
        assertEquals(listOf("B", "C"), state.groups.map { it.groupId })
        assertEquals(listOf(5, 6), state.groups.map { it.expiresAfterRoundOrdinal })
        state = state.registerDealtRound("r5").addGroup(group("D", 5))
        assertEquals(12, state.groupsForSeat(0).sumOf { it.tileIds.size })
        state = state.completeRound("r5")
        assertEquals(listOf("C", "D"), state.groups.map { it.groupId })
        assertEquals(listOf(6, 8), state.groups.map { it.expiresAfterRoundOrdinal })
    }

    @Test
    fun `capacity is per owner and shared physical targets across owners are allowed`() {
        var state = TileMarkState().registerDealtRound("r1")
        for (seat in 0..3) {
            for (batch in 0..2) state = state.addGroup(group("$seat-$batch", seat = seat, firstTile = batch * 4))
            assertEquals(12, state.groupsForSeat(seat).sumOf { it.tileIds.size })
            assertThrows<IllegalArgumentException> { state.addGroup(group("$seat-extra", seat = seat, firstTile = 12)) }
        }
        assertEquals(48, state.groups.sumOf { it.tileIds.size })
        assertEquals(12, state.groups.flatMap { it.tileIds }.distinct().size)
    }

    @Test
    fun `overlap cannot refresh active marks but reselecting expired tiles is allowed`() {
        var state = TileMarkState().registerDealtRound("r1").addGroup(group("A"))
        assertThrows<IllegalArgumentException> { state.addGroup(group("B", firstTile = 2)) }
        assertEquals(1, state.groups.size)
        for (round in 1..4) state = state.completeRound("r$round").registerDealtRound("r${round + 1}")
        val replacement = state.addGroup(group("new-A", 5))
        assertEquals(8, replacement.groups.single().expiresAfterRoundOrdinal)
        assertThrows<IllegalArgumentException> { state.addGroup(group("A")) }
    }

    @Test
    fun `group creation requires current registered unsettled round`() {
        assertThrows<IllegalArgumentException> { TileMarkState().addGroup(group("A")) }
        val first = TileMarkState().registerDealtRound("r1")
        assertThrows<IllegalArgumentException> { first.addGroup(group("A", 2)) }
        assertThrows<IllegalArgumentException> { first.addGroup(group("A").copy(createdRoundId = "unknown")) }
        assertThrows<IllegalArgumentException> { first.completeRound("r1").addGroup(group("A")) }
        assertTrue(first.groups.isEmpty())
    }

    @Test
    fun `duplicate active group is idempotent and conflicting identity is rejected`() {
        val first = TileMarkState().registerDealtRound("r1").addGroup(group("A"))
        assertSame(first, first.addGroup(group("A")))
        assertThrows<IllegalArgumentException> { first.addGroup(group("A", firstTile = 4)) }
        assertThrows<IllegalArgumentException> { first.addGroup(group("A", seat = 1)) }
        assertEquals(listOf(0, 1, 2, 3), first.groups.single().tileIds)
    }

    @Test
    fun `completion rejects unknown rounds and replay of old completed round does not change current state`() {
        val first = TileMarkState().registerDealtRound("r1").addGroup(group("A"))
        assertThrows<IllegalArgumentException> { first.completeRound("not-dealt") }
        val second = first.completeRound("r1").registerDealtRound("r2")
        assertSame(second, second.completeRound("r1"))
        assertEquals(1, second.groups.size)
    }

    @Test
    fun `clearing one occupant preserves other owners and all round accounting`() {
        val original = TileMarkState().registerDealtRound("r1")
            .addGroup(group("A")).addGroup(group("B", seat = 1))
        val cleared = original.clearSeat(0)
        assertTrue(cleared.groupsForSeat(0).isEmpty())
        assertEquals(listOf("B"), cleared.groupsForSeat(1).map { it.groupId })
        assertEquals(original.dealtRoundIds, cleared.dealtRoundIds)
        assertEquals(original.dealtRoundOrdinal, cleared.dealtRoundOrdinal)
        assertEquals(original.version + 1, cleared.version)
        assertSame(cleared, cleared.clearSeat(0))
        assertEquals(2, original.groups.size)
        val allCleared = cleared.clearAll()
        assertTrue(allCleared.groups.isEmpty())
        assertEquals(cleared.dealtRoundIds, allCleared.dealtRoundIds)
        assertSame(allCleared, allCleared.clearAll())
        assertThrows<IllegalArgumentException> { original.clearSeat(4) }
        assertThrows<IllegalArgumentException> { original.groupsForSeat(-1) }
    }

    @Test
    fun `returned collections cannot mutate snapshots or later transitions`() {
        val first = TileMarkState().registerDealtRound("r1").addGroup(group("A"))
        assertThrows<UnsupportedOperationException> { (first.groups as MutableList<*>).clear() }
        assertThrows<UnsupportedOperationException> { (first.groupsForSeat(0) as MutableList<*>).clear() }
        assertThrows<UnsupportedOperationException> { (first.dealtRoundIds as MutableList<*>).clear() }
        val settled = first.completeRound("r1")
        assertThrows<UnsupportedOperationException> { (settled.settledRoundIds as MutableSet<*>).clear() }
        assertEquals(1, first.groups.size)
        assertTrue(first.settledRoundIds.isEmpty())
        assertEquals(setOf("r1"), settled.settledRoundIds)
    }
}
