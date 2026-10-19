package com.majiang.presentation.game

import com.majiang.client.api.DoraBreakdownView
import com.majiang.client.api.FuBreakdownView
import com.majiang.client.api.GameEventView
import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.PublicBoardView
import com.majiang.client.api.PublicSnapshotView
import com.majiang.client.api.PublicUpdateView
import com.majiang.client.api.ScoreBreakdownView
import com.majiang.client.api.ScoreResultView
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.presentation.common.SettlementSection
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeSpectatorSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.io.File

/**
 * 观战独立动画/游标/重建状态与公开结算验收。
 *
 * 覆盖 D-05 第二步/第三步：事件顺序、全量快照、断线恢复（跳号基线）、换局、
 * 重复投递、旧动画不得掩盖新牌、观战隐私与公开结算分页。
 */
class SpectatorDiscardAndSettlementAcceptanceTest : GdxHeadlessTestRunner() {

    private fun board(
        phase: GamePhaseView = GamePhaseView.DISCARD,
        roundNumber: Int = 1,
        discards: List<List<TileView>> = List(4) { emptyList() },
        melds: List<List<com.majiang.client.api.MeldView>> = List(4) { emptyList() },
        handCounts: List<Int> = listOf(13, 13, 14, 13),
        scores: List<Int> = listOf(25000, 25000, 25000, 25000)
    ): PublicBoardView = TestViewBuilder.publicBoardView(
        phase = phase,
        roundNumber = roundNumber,
        discards = discards,
        melds = melds,
        handTileCounts = handCounts,
        scores = scores
    )

    private fun snapshot(
        cursor: Long,
        roundId: String? = "round-1-1-0",
        board: PublicBoardView,
        settlementRoundId: String? = null,
        settlementEvents: List<GameEventView> = emptyList()
    ): PublicSnapshotView = TestViewBuilder.publicSnapshotView(
        cursor = cursor,
        roundId = roundId,
        board = board,
        settlementRoundId = settlementRoundId,
        settlementEvents = settlementEvents
    )

    private fun screen(
        snap: PublicSnapshotView = snapshot(1, board = board())
    ): Pair<SpectatorScreen, FakeSpectatorSession> {
        val session = FakeSpectatorSession(initialSnapshot = snap)
        val screen = SpectatorScreen(TestUiAssets.create(), session, FakeTableNavigation())
        return screen to session
    }

    private fun breakdown(
        yaku: List<String> = listOf("立直", "平和"),
        dora: DoraBreakdownView = DoraBreakdownView(
            0, 0, 0, 0, emptyList(), emptyList(), emptyList(), emptyList(), emptyList()
        ),
        total: Int = 2000
    ): ScoreBreakdownView = ScoreBreakdownView(
        yakuLines = yaku.map { it to 1 },
        yakumanCount = 0,
        dora = dora,
        fu = FuBreakdownView(emptyList(), 30, 30, 30),
        score = ScoreResultView(30, yaku.size, 1000, "满贯", false, 0, 1000),
        paymentLines = emptyList(),
        honbaAddition = 0,
        riichiAddition = 0,
        totalPoints = total,
        uraEligible = false,
        isDealer = false,
        isTsumo = true
    )

    @Test
    fun `initial public snapshot establishes a baseline without replaying history`() {
        val tile = TestViewBuilder.tile(SuitView.MAN, 1, 0)
        val initial = snapshot(1, board = board(discards = listOf(listOf(tile), emptyList(), emptyList(), emptyList())))
        val (screen, _) = screen(initial)

        assertFalse(screen.isDiscardPlaybackBusy, "Initial snapshot history must not animate")
        assertEquals(1, screen.discardAnimationController.lastDiscards[0].size)

        screen.dispose()
    }

    @Test
    fun `continuous update enqueues only new discards in event order and hides them until done`() {
        val (screen, session) = screen()
        val first = TestViewBuilder.tile(SuitView.MAN, 5, 0)
        val second = TestViewBuilder.tile(SuitView.PIN, 6, 0)
        val nextBoard = board(
            discards = listOf(listOf(first), listOf(second), emptyList(), emptyList())
        )
        val events = listOf(
            GameEventView.TileDiscarded(2, 0, first),
            GameEventView.TileDiscarded(3, 1, second)
        )

        session.update(snapshot(2, board = nextBoard), events)

        assertTrue(screen.isDiscardPlaybackBusy)
        screen.discardAnimationController.update(0.02f)
        assertEquals(first, screen.discardAnimationController.currentDiscardAnim?.tile)
        assertFalse(
            screen.discardAnimationController.isTileVisibleInRiver(0, first, 0),
            "The flying discard must be suppressed from the river until its animation ends"
        )
        assertFalse(
            screen.discardAnimationController.isTileVisibleInRiver(1, second, 0),
            "A queued discard is hidden until its own playback starts, avoiding appear-then-flight artifacts"
        )
        assertEquals(1, screen.discardAnimationController.discardQueue.size)

        screen.dispose()
    }

    @Test
    fun `full snapshot clears in-flight and queued animations and rebuilds the new baseline`() {
        val (screen, session) = screen()
        val oldTile = TestViewBuilder.tile(SuitView.MAN, 1, 0)
        val oldBoard = board(discards = listOf(listOf(oldTile), emptyList(), emptyList(), emptyList()))
        session.update(snapshot(2, board = oldBoard), listOf(GameEventView.TileDiscarded(1, 0, oldTile)))
        screen.discardAnimationController.update(0.02f)
        assertTrue(screen.isDiscardPlaybackBusy)

        val recoveredTile = TestViewBuilder.tile(SuitView.SOU, 9, 0)
        val recoveredBoard = board(
            roundNumber = 2,
            discards = listOf(listOf(recoveredTile), emptyList(), emptyList(), emptyList())
        )
        val recovered = snapshot(7, roundId = "round-2-1-0", board = recoveredBoard)
        screen.onPublicUpdate(
            PublicUpdateView(
                snapshot = recovered,
                fromCursor = recovered.cursor,
                visibleEvents = listOf(GameEventView.TileDiscarded(1, 0, recoveredTile)),
                isFullSnapshot = true
            )
        )

        assertFalse(screen.isDiscardPlaybackBusy, "Full snapshot must drop the old animation immediately")
        assertEquals(listOf(listOf(recoveredTile), emptyList(), emptyList(), emptyList()),
            screen.discardAnimationController.lastDiscards)
        assertTrue(
            screen.discardAnimationController.isTileVisibleInRiver(0, recoveredTile, 0),
            "Recovered tile must be visible immediately; old animation cannot mask it"
        )

        screen.dispose()
    }

    @Test
    fun `cursor gap rebuilds baseline without replaying missed discards`() {
        val (screen, session) = screen()
        val missed = TestViewBuilder.tile(SuitView.MAN, 3, 0)
        val jumped = snapshot(
            6,
            board = board(discards = listOf(listOf(missed), emptyList(), emptyList(), emptyList()))
        )
        session.update(jumped, listOf(GameEventView.TileDiscarded(1, 0, missed)))

        assertFalse(screen.isDiscardPlaybackBusy, "Missed updates must not be replayed as fresh animations")
        assertEquals(listOf(listOf(missed), emptyList(), emptyList(), emptyList()),
            screen.discardAnimationController.lastDiscards)

        screen.dispose()
    }

    @Test
    fun `round change rebuilds baseline and never leaks the previous round animation`() {
        val (screen, session) = screen()
        val oldTile = TestViewBuilder.tile(SuitView.MAN, 2, 0)
        session.update(
            snapshot(2, board = board(discards = listOf(listOf(oldTile), emptyList(), emptyList(), emptyList()))),
            listOf(GameEventView.TileDiscarded(1, 0, oldTile))
        )
        screen.discardAnimationController.update(0.02f)
        assertTrue(screen.isDiscardPlaybackBusy)

        val newRoundTile = TestViewBuilder.tile(SuitView.PIN, 4, 0)
        session.update(
            snapshot(
                3,
                roundId = "round-2-1-0",
                board = board(discards = listOf(listOf(newRoundTile), emptyList(), emptyList(), emptyList()))
            ),
            listOf(GameEventView.TileDiscarded(1, 0, newRoundTile))
        )

        assertFalse(screen.isDiscardPlaybackBusy)
        assertEquals(listOf(listOf(newRoundTile), emptyList(), emptyList(), emptyList()),
            screen.discardAnimationController.lastDiscards)

        screen.dispose()
    }

    @Test
    fun `each spectator screen owns independent animation and cursor state`() {
        val (left, leftSession) = screen()
        val (right, rightSession) = screen()

        assertNotSame(left.discardAnimationController, right.discardAnimationController)

        val tile = TestViewBuilder.tile(SuitView.MAN, 7, 0)
        leftSession.update(
            snapshot(2, board = board(discards = listOf(listOf(tile), emptyList(), emptyList(), emptyList()))),
            listOf(GameEventView.TileDiscarded(1, 0, tile))
        )
        left.discardAnimationController.update(0.02f)

        assertTrue(left.isDiscardPlaybackBusy)
        assertFalse(right.isDiscardPlaybackBusy)
        assertTrue(right.discardAnimationController.lastDiscards.all { it.isEmpty() })
        assertNotEquals(left.discardAnimationController.lastDiscards, right.discardAnimationController.lastDiscards)
        rightSession.update(snapshot(2, board = board()), emptyList())
        assertFalse(right.isDiscardPlaybackBusy)

        left.dispose()
        right.dispose()
    }

    @Test
    fun `switching between player and spectator views keeps animation state separate`() {
        val playerTile = TestViewBuilder.tile(SuitView.MAN, 6, 0)
        val playerView = TestViewBuilder.playerView(
            viewerSeat = 0,
            discards = listOf(listOf(playerTile), emptyList(), emptyList(), emptyList())
        )
        val playerSession = FakeGameSession(initialView = playerView)
        val playerScreen = GameScreen(TestUiAssets.create(), playerSession, FakeTableNavigation())
        val (spectator, spectatorSession) = screen()

        assertNotSame(playerScreen.discardAnimationController, spectator.discardAnimationController)

        // Player animation starts; spectator remains untouched.
        playerSession.update(
            playerView.copy(discards = listOf(listOf(playerTile), listOf(TestViewBuilder.tile(SuitView.PIN, 9, 0)), emptyList(), emptyList())),
            listOf(GameEventView.TileDiscarded(2, 1, TestViewBuilder.tile(SuitView.PIN, 9, 0)))
        )
        playerScreen.updateDiscardAnimation(0.02f)
        assertTrue(playerScreen.isDiscardPlaybackBusy)
        assertFalse(spectator.isDiscardPlaybackBusy)

        // Spectator animation starts; player state is not mutated.
        val spectatorTile = TestViewBuilder.tile(SuitView.SOU, 3, 0)
        spectatorSession.update(
            snapshot(2, board = board(discards = listOf(listOf(spectatorTile), emptyList(), emptyList(), emptyList()))),
            listOf(GameEventView.TileDiscarded(1, 0, spectatorTile))
        )
        spectator.discardAnimationController.update(0.02f)
        assertTrue(spectator.isDiscardPlaybackBusy)
        assertNotEquals(
            playerScreen.discardAnimationController.currentDiscardAnim?.tile,
            spectator.discardAnimationController.currentDiscardAnim?.tile
        )

        playerScreen.dispose()
        spectator.dispose()
    }

    @Test
    fun `spectator public settlement reuses breakdown paging and has no player actions`() {
        val winner = 2
        val winTile = TestViewBuilder.tile(SuitView.MAN, 1, 0)
        val bd = breakdown()
        val tsumo = GameEventView.TsumoWin(12, winner, winTile, listOf("立直", "平和"), 2, 30, 2000, bd)
        val score = GameEventView.ScoreSettled(
            12,
            mapOf(0 to -700, 1 to -700, 2 to 2000, 3 to -600),
            listOf(24300, 24300, 27000, 24400)
        )
        val settlementBoard = board(
            phase = GamePhaseView.ROUND_END,
            scores = listOf(24300, 24300, 27000, 24400)
        )
        val (screen, session) = screen()
        session.update(
            snapshot(
                2,
                roundId = "round-1-1-0",
                board = settlementBoard,
                settlementRoundId = "round-1-1-0",
                settlementEvents = listOf(tsumo, score)
            ),
            listOf(tsumo, score)
        )

        assertTrue(screen.isSettlementShown)
        val detail = screen.currentSettlement
        assertNotNull(detail)
        assertEquals("TSUMO WIN!", detail!!.title)
        assertNotNull(detail.breakdown)
        assertTrue(screen.visibleSettlementRows().any { it.text.contains("立直") })

        screen.touchDown(410, 530, 0, 0)
        assertEquals(SettlementSection.DORA, screen.settlementSection)
        screen.touchDown(240, 530, 0, 0)
        assertEquals(SettlementSection.YAKU, screen.settlementSection)

        // 观战没有任何下一局/续庄按钮命中区，点击后仍停留在结算页。
        screen.touchDown(640, 165, 0, 0)
        assertTrue(screen.isSettlementShown)

        screen.dispose()
    }

    @Test
    fun `public settlement without breakdown pages only through public yaku or draw text`() {
        val winTile = TestViewBuilder.tile(SuitView.MAN, 1, 0)
        val entries = (1..14).map { "公开役种$it" }
        val tsumo = GameEventView.TsumoWin(12, 2, winTile, entries, 14, 30, 32000, null)
        val score = GameEventView.ScoreSettled(12, emptyMap(), emptyList())
        val (screen, session) = screen()
        session.update(
            snapshot(
                2,
                board = board(phase = GamePhaseView.ROUND_END),
                settlementRoundId = "round-1-1-0",
                settlementEvents = listOf(tsumo, score)
            ),
            listOf(tsumo, score)
        )

        assertNull(screen.currentSettlement?.breakdown)
        assertEquals(3, screen.settlementPageCount)
        val seen = mutableListOf<String>()
        repeat(3) {
            seen.addAll(screen.visibleSettlementRows().map { row -> row.text })
            screen.touchDown(980, 210, 0, 0)
        }
        for (entry in entries) {
            assertEquals(1, seen.count { it == entry }, entry)
        }
        assertEquals(2, screen.settlementPage)

        screen.dispose()
    }

    @Test
    fun `next round hides settlement and private events are rejected from public settlement path`() {
        val tsumo = GameEventView.TsumoWin(
            12, 2, TestViewBuilder.tile(SuitView.MAN, 1, 0), listOf("立直"), 1, 30, 1000, breakdown()
        )
        val (screen, session) = screen()
        session.update(
            snapshot(
                2,
                board = board(phase = GamePhaseView.ROUND_END),
                settlementRoundId = "round-1-1-0",
                settlementEvents = listOf(tsumo)
            ),
            listOf(tsumo)
        )
        assertTrue(screen.isSettlementShown)

        session.update(
            snapshot(3, roundId = "round-2-1-0", board = board(phase = GamePhaseView.STACKING)),
            listOf(GameEventView.StackingStarted(0))
        )
        assertFalse(screen.isSettlementShown)
        assertNull(screen.currentSettlement)

        assertThrows(IllegalArgumentException::class.java) {
            TestViewBuilder.publicSnapshotView(
                settlementEvents = listOf(
                    GameEventView.CheatPointsChanged(1, 0, 10, 110)
                )
            )
        }

        screen.dispose()
    }

    @Test
    fun `spectator source never references private player snapshot or cheat fields`() {
        val file = listOf(
            File("src/main/kotlin/com/majiang/presentation/game/SpectatorScreen.kt"),
            File("presentation/src/main/kotlin/com/majiang/presentation/game/SpectatorScreen.kt")
        ).firstOrNull { it.exists() } ?: error("SpectatorScreen.kt not found")
        val text = file.readText()
        val forbidden = listOf(
            "GameSnapshotView",
            "PlayerView",
            "PlayerSnapshot",
            "myClosedTiles",
            "mySleeveTiles",
            "cheatPoints",
            "TileMarkView",
            "beamSwap",
            "sleeveBloom"
        )
        for (token in forbidden) {
            assertFalse(text.contains(token), "SpectatorScreen must not reference private token '$token'")
        }
    }

    @Test
    fun `public settlement projection is derived only from validated settlement events`() {
        val privateEvent = GameEventView.TileDrawn(1, 0, TestViewBuilder.tile(SuitView.MAN, 1, 0), 69)
        assertThrows(IllegalArgumentException::class.java) {
            TestViewBuilder.publicSnapshotView(settlementEvents = listOf(privateEvent))
        }
    }
}
