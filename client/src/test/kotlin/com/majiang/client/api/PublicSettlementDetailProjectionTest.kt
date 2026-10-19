package com.majiang.client.api

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class PublicSettlementDetailProjectionTest {

    private val winTile = TileView.fromTypeIndex(0, 0)

    @Test
    fun `player settlement mapping may ignore private settlement events without leaking them`() {
        val win = GameEventView.TsumoWin(
            12, 2, winTile, listOf("立直"), 1, 30, 1000, null
        )
        val cheat = GameEventView.CheatPointsChanged(12, 2, 10, 110)
        val settled = GameEventView.ScoreSettled(12, mapOf(0 to -700, 2 to 2000), listOf(24300, 25000, 27000, 25000))

        val detail = PublicSettlementDetailProjection.from(listOf(cheat, win, settled))
        assertNotNull(detail)
        assertEquals("TSUMO WIN!", detail!!.title)
        assertEquals(2, detail.winner)
        assertEquals(27000, detail.newScores[2])
        assertNotEquals(110, detail.newScores[2], "Private cheat ledger balance must never become a public score")
    }

    @Test
    fun `public whitelist mode rejects private settlement events`() {
        val cheat = GameEventView.CheatPointsChanged(12, 2, 10, 110)
        assertThrows(IllegalArgumentException::class.java) {
            PublicSettlementDetailProjection.from(listOf(cheat), requirePublicWhitelist = true)
        }
    }

    @Test
    fun `public snapshot settlement detail is derived only from public events`() {
        val win = GameEventView.TsumoWin(
            12, 2, winTile, listOf("立直"), 1, 30, 1000, null
        )
        val snapshot = PublicSnapshotView(
            sessionId = "s",
            sessionVersion = 1L,
            syncEpoch = "e",
            cursor = 1L,
            session = PublicSessionSummaryView("ROUND_RESULT", listOf(25000, 25000, 25000, 25000), 1, 0, 0, 0),
            settlementRoundId = "r",
            settlementEvents = listOf(win)
        )
        assertNotNull(snapshot.settlementDetail)
        assertEquals("TSUMO WIN!", snapshot.settlementDetail!!.title)

        assertThrows(IllegalArgumentException::class.java) {
            PublicSnapshotView(
                sessionId = "s",
                sessionVersion = 1L,
                syncEpoch = "e",
                cursor = 1L,
                session = PublicSessionSummaryView("ROUND_RESULT", listOf(25000, 25000, 25000, 25000), 1, 0, 0, 0),
                settlementRoundId = "r",
                settlementEvents = listOf(GameEventView.CheatPointsChanged(12, 2, 10, 110))
            )
        }
    }
}
