package com.majiang.presentation.common.render

import com.majiang.client.api.*
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.presentation.game.OpponentHandPresentation
import com.majiang.presentation.game.TileMarkPresentation
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.io.File

/**
 * TableBoardRenderModel 契约与隐私隔离边界测试。
 */
class TableBoardRenderModelContractTest : GdxHeadlessTestRunner() {

    private fun findSourceFile(relativePath: String): File {
        val candidates = listOf(
            File(relativePath),
            File("presentation", relativePath),
            File("../presentation", relativePath),
            File("../../presentation", relativePath)
        )
        return candidates.firstOrNull { it.exists() }
            ?: error("Could not locate source file: $relativePath among candidates: ${candidates.map { it.absolutePath }}")
    }

    @Test
    fun `fromPlayerSnapshot maps fields correctly and extracts active tile marks`() {
        val doraTile = TestViewBuilder.tile(10)
        val riverTile = TestViewBuilder.tile(20)
        val meldTile1 = TestViewBuilder.tile(30)
        val meldTile2 = TestViewBuilder.tile(31)
        val meldTile3 = TestViewBuilder.tile(32)

        val playerView = TestViewBuilder.playerView(
            viewerSeat = 1,
            phase = GamePhaseView.DISCARD,
            roundWind = 2,
            roundNumber = 3,
            honbaSticks = 2,
            riichiSticks = 1,
            dealerSeat = 0,
            currentTurnPlayer = 1,
            wallRemaining = 55,
            doraIndicators = listOf(doraTile),
            scores = listOf(20000, 30000, 25000, 25000),
            riichiStatus = listOf(false, true, false, false),
            discards = listOf(listOf(riverTile), emptyList(), emptyList(), emptyList()),
            melds = listOf(emptyList(), emptyList(), listOf(TestViewBuilder.meld(MeldTypeView.PON, listOf(meldTile1, meldTile2, meldTile3), 2)), emptyList()),
            handTileCounts = listOf(13, 14, 10, 13)
        )

        val markedDora = MarkedTileView(10, doraTile, TileMarkArea.EXPOSED_INDICATOR)
        val markedRiver = MarkedTileView(20, riverTile, TileMarkArea.DISCARD, seat = 0, riverPosition = RiverPositionView("round-test-1", 0, 0))
        val markedMeld = MarkedTileView(30, meldTile1, TileMarkArea.EXPOSED_MELD, seat = 2)

        val group = TileMarkGroupView(
            groupId = "group-1",
            createdRoundId = "round-test-1",
            createdRoundOrdinal = 1,
            expiresAfterRoundOrdinal = 4,
            futureRoundsRemaining = 2,
            isLastEffectiveRound = false,
            tiles = listOf(markedDora, markedRiver, markedMeld, MarkedTileView(32, meldTile3, TileMarkArea.NOT_VISIBLE))
        )

        val tileMark = TileMarkView(
            canApply = true,
            disabledReason = null,
            cost = 10,
            activeTileCount = 4,
            maxActiveTiles = 12,
            selectableTileIds = emptyList(),
            decisionId = "dec-1",
            controlEpoch = 1L,
            stateVersion = 1L,
            activeGroups = listOf(group)
        )

        val clocks = listOf(ClockInfo(0, 1000L, 5000L, 20000L, 1000L))

        val snapshot = GameSnapshotView(
            sessionId = "session-test-1",
            roundId = "round-test-1",
            syncEpoch = "epoch-1",
            cursor = 1L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = playerView,
            decision = DecisionView("round-test-1", "dec-1", 1L),
            clocks = clocks,
            tileMark = tileMark
        )

        val expectedRiverMarks = TileMarkPresentation.riverMarks(snapshot)
        assertEquals(1, expectedRiverMarks.size)
        assertEquals(20, expectedRiverMarks.first().tileId)

        val model = TableBoardRenderModel.fromPlayerSnapshot(snapshot)

        assertEquals(1, model.viewerSeat)
        assertFalse(model.isSpectator)
        assertEquals(2, model.roundWind)
        assertEquals(3, model.roundNumber)
        assertEquals(2, model.honbaSticks)
        assertEquals(1, model.riichiSticks)
        assertEquals(55, model.wallRemaining)
        assertEquals(listOf(doraTile), model.doraIndicators)
        assertEquals(setOf(10), model.markedDoraIndicatorIds)
        assertEquals(listOf(20000, 30000, 25000, 25000), model.scores)
        assertEquals(listOf(false, true, false, false), model.riichiStatus)
        assertEquals(1, model.currentTurnPlayer)
        assertEquals(listOf(listOf(riverTile), emptyList(), emptyList(), emptyList()), model.discards)
        assertEquals(setOf(RiverMarkedTile(0, 0, 20)), model.riverMarkedTileKeys)
        assertEquals(setOf(30), model.markedMeldTileIds)
        assertEquals(OpponentHandPresentation.build(snapshot), model.opponentHandRows)
        assertEquals(0, model.viewerHandBackCount)
        assertEquals(clocks, model.clocks)
    }

    @Test
    fun `fromPublicSnapshot sets spectator defaults, null slots, and zero privacy leakage`() {
        val board = TestViewBuilder.publicBoardView(
            phase = GamePhaseView.DISCARD,
            roundWind = 1,
            roundNumber = 2,
            honbaSticks = 0,
            riichiSticks = 1,
            dealerSeat = 3,
            currentTurnPlayer = 2,
            wallRemaining = 48,
            doraIndicators = listOf(TestViewBuilder.tile(5)),
            scores = listOf(22000, 28000, 25000, 25000),
            riichiStatus = listOf(true, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(14, 13, 10, 13)
        )

        val clocks = listOf(ClockInfo(2, 1000L, 5000L, 20000L, 1000L))
        val snapshot = TestViewBuilder.publicSnapshotView(board = board, clocks = clocks)

        val model = TableBoardRenderModel.fromPublicSnapshot(snapshot)

        assertEquals(0, model.viewerSeat)
        assertTrue(model.isSpectator)
        assertEquals(1, model.roundWind)
        assertEquals(2, model.roundNumber)
        assertEquals(0, model.honbaSticks)
        assertEquals(1, model.riichiSticks)
        assertEquals(48, model.wallRemaining)
        assertEquals(listOf(TestViewBuilder.tile(5)), model.doraIndicators)
        assertTrue(model.markedDoraIndicatorIds.isEmpty(), "Spectator markedDoraIndicatorIds must be empty")
        assertTrue(model.markedMeldTileIds.isEmpty(), "Spectator markedMeldTileIds must be empty")
        assertTrue(model.riverMarkedTileKeys.isEmpty(), "Spectator riverMarkedTileKeys must be empty")
        assertEquals(14, model.viewerHandBackCount, "viewerHandBackCount must equal handTileCounts[0]")
        assertEquals(clocks, model.clocks)

        assertEquals(3, model.opponentHandRows.size)
        for (row in model.opponentHandRows) {
            assertTrue(row.slots.all { it.tile == null }, "All slots for spectator opponent hands must have null tile face")
        }
        assertEquals(1, model.opponentHandRows[0].seat)
        assertEquals(13, model.opponentHandRows[0].slots.size)
        assertEquals(2, model.opponentHandRows[1].seat)
        assertEquals(10, model.opponentHandRows[1].slots.size)
        assertEquals(3, model.opponentHandRows[2].seat)
        assertEquals(13, model.opponentHandRows[2].slots.size)
    }

    @Test
    fun `source scan verification - TableBoardRenderModel and TableBoardRenderer do not reference forbidden types`() {
        val forbiddenTypeNames = listOf(
            "GameSnapshotView",
            "PlayerView",
            "GameSession",
            "SpectatorSession"
        )

        val modelFile = findSourceFile("src/main/kotlin/com/majiang/presentation/common/render/TableBoardRenderModel.kt")
        assertTrue(modelFile.exists(), "TableBoardRenderModel.kt must exist")
        val modelText = modelFile.readText()
        for (forbidden in forbiddenTypeNames) {
            assertFalse(
                modelText.contains(forbidden),
                "TableBoardRenderModel.kt must not contain '$forbidden' to prevent architectural and privacy regression"
            )
        }

        val rendererFile = findSourceFile("src/main/kotlin/com/majiang/presentation/common/render/TableBoardRenderer.kt")
        assertTrue(rendererFile.exists(), "TableBoardRenderer.kt must exist")
        val rendererText = rendererFile.readText()
        for (forbidden in forbiddenTypeNames) {
            assertFalse(
                rendererText.contains(forbidden),
                "TableBoardRenderer.kt must not contain '$forbidden' to prevent architectural and privacy regression"
            )
        }
    }
}
