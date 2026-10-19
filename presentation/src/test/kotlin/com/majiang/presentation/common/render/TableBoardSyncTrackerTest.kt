package com.majiang.presentation.common.render

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TableBoardSyncTrackerTest {

    private fun identity(
        session: String = "session-1",
        epoch: String = "epoch-1",
        round: String? = "round-1",
        cursor: Long = 10L
    ) = TableBoardViewIdentity(session, epoch, round, cursor)

    @Test
    fun `first observation establishes baseline and never replays history`() {
        val tracker = TableBoardSyncTracker()
        val t = tracker.observe(identity())

        assertTrue(t.firstBaseline)
        assertTrue(t.requiresRebuild)
        assertFalse(t.duplicate)
        assertEquals(10L, tracker.lastCursor)
    }

    @Test
    fun `continuous cursor is the only transition that may animate events`() {
        val tracker = TableBoardSyncTracker(identity(cursor = 10L))
        val t = tracker.observe(identity(cursor = 11L))

        assertFalse(t.requiresRebuild)
        assertFalse(t.duplicate)
        assertFalse(t.cursorJumped)
    }

    @Test
    fun `duplicate ordinary snapshot is identified without replaying actions`() {
        val tracker = TableBoardSyncTracker(identity(cursor = 10L))
        val t = tracker.observe(identity(cursor = 10L))

        assertTrue(t.duplicate)
        assertFalse(t.requiresRebuild)
        assertEquals(10L, tracker.lastCursor)
    }

    @Test
    fun `explicit full snapshot always rebuilds even at the same cursor`() {
        val tracker = TableBoardSyncTracker(identity(cursor = 10L))
        val t = tracker.observe(identity(cursor = 10L), isFullSnapshot = true)

        assertTrue(t.fullSnapshot)
        assertTrue(t.requiresRebuild)
        assertFalse(t.duplicate)
    }

    @Test
    fun `cursor gap and cursor regression both force a rebuild`() {
        val gap = TableBoardSyncTracker(identity(cursor = 10L))
            .observe(identity(cursor = 13L))
        assertTrue(gap.cursorJumped)
        assertTrue(gap.requiresRebuild)

        val regression = TableBoardSyncTracker(identity(cursor = 10L))
            .observe(identity(cursor = 9L))
        assertTrue(regression.cursorJumped)
        assertTrue(regression.requiresRebuild)
    }

    @Test
    fun `session epoch and round changes each force a rebuild`() {
        val session = TableBoardSyncTracker(identity()).observe(identity(session = "session-2"))
        assertTrue(session.sessionChanged)
        assertTrue(session.requiresRebuild)

        val epoch = TableBoardSyncTracker(identity()).observe(identity(epoch = "epoch-2"))
        assertTrue(epoch.epochChanged)
        assertTrue(epoch.requiresRebuild)

        val round = TableBoardSyncTracker(identity()).observe(identity(round = "round-2"))
        assertTrue(round.roundChanged)
        assertTrue(round.requiresRebuild)
    }

    @Test
    fun `reset makes the next observation a fresh baseline`() {
        val tracker = TableBoardSyncTracker(identity())
        tracker.reset()
        val t = tracker.observe(identity(cursor = 11L))
        assertTrue(t.firstBaseline)
        assertTrue(t.requiresRebuild)
    }
}
