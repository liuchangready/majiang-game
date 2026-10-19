package com.majiang.presentation.game

import com.majiang.client.api.GamePhaseView
import com.majiang.client.api.MeldTypeView
import com.majiang.client.api.SuitView
import com.majiang.presentation.GdxHeadlessTestRunner
import com.majiang.presentation.testing.FakeGameSession
import com.majiang.presentation.testing.FakeSpectatorSession
import com.majiang.presentation.testing.FakeTableNavigation
import com.majiang.presentation.testing.TestUiAssets
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * 观战与玩家共享牌桌渲染集成验证 (SpectatorSharedTableRenderTest)。
 */
class SpectatorSharedTableRenderTest : GdxHeadlessTestRunner() {

    @Test
    fun `spectator screen renders multiple frames with red dora and melds without exception and preserves hand counts`() {
        val redFive = TestViewBuilder.tile(SuitView.MAN, 5, copyIndex = 3)
        assertTrue(redFive.isRedFive)

        val chiMeld = TestViewBuilder.meld(
            type = MeldTypeView.CHI,
            tiles = listOf(
                TestViewBuilder.tile(SuitView.PIN, 2),
                TestViewBuilder.tile(SuitView.PIN, 3),
                TestViewBuilder.tile(SuitView.PIN, 4)
            ),
            fromPlayer = 0
        )
        val ponMeld = TestViewBuilder.meld(
            type = MeldTypeView.PON,
            tiles = listOf(
                TestViewBuilder.tile(SuitView.SOU, 8, 0),
                TestViewBuilder.tile(SuitView.SOU, 8, 1),
                TestViewBuilder.tile(SuitView.SOU, 8, 2)
            ),
            fromPlayer = 1
        )

        val board = TestViewBuilder.publicBoardView(
            phase = GamePhaseView.DISCARD,
            roundWind = 1,
            roundNumber = 0,
            honbaSticks = 1,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 2,
            wallRemaining = 50,
            doraIndicators = listOf(redFive),
            scores = listOf(25000, 24000, 26000, 25000),
            riichiStatus = listOf(false, false, true, false),
            discards = listOf(
                listOf(TestViewBuilder.tile(SuitView.WIND, 1)),
                emptyList(),
                listOf(TestViewBuilder.tile(SuitView.DRAGON, 1)),
                emptyList()
            ),
            melds = listOf(emptyList(), listOf(chiMeld), listOf(ponMeld), emptyList()),
            handTileCounts = listOf(14, 10, 7, 13)
        )

        val snap = TestViewBuilder.publicSnapshotView(board = board)
        val session = FakeSpectatorSession(initialSnapshot = snap)
        val nav = FakeTableNavigation()
        val screen = SpectatorScreen(TestUiAssets.create(), session, nav)

        try {
            // 渲染多帧，确保在赤宝牌、副露、各家不同牌数下不抛出任何渲染异常
            for (frame in 1..10) {
                screen.render(0.016f)
            }

            // 公开快照中的手牌数量严格原样保留
            assertEquals(listOf(14, 10, 7, 13), screen.currentSnapshot?.board?.handTileCounts)
        } finally {
            screen.dispose()
        }
    }

    @Test
    fun `four relative viewerSeat layouts have rotationally equivalent discard slot sets and start sets`() {
        // 对比 viewerSeat = 0..3 四个不同视点下的 GameScreen 牌河槽位与弃牌起点集合
        val screens = (0..3).map { vs ->
            val playerView = TestViewBuilder.playerView(
                viewerSeat = vs,
                phase = GamePhaseView.DISCARD
            )
            val fakeSession = FakeGameSession(initialView = playerView)
            GameScreen(TestUiAssets.create(), fakeSession, FakeTableNavigation())
        }

        try {
            // 1. 验证 getDiscardStart 在四个 viewerSeat 下对全席位输出集合完全一致
            val baselineStartSet = (0..3).map { seat -> screens[0].getDiscardStart(seat) }.toSet()
            assertEquals(4, baselineStartSet.size, "Four distinct discard start positions expected")

            for (vs in 1..3) {
                val startSet = (0..3).map { seat -> screens[vs].getDiscardStart(seat) }.toSet()
                assertEquals(
                    baselineStartSet,
                    startSet,
                    "Discard start set for viewerSeat=$vs must be rotationally equivalent to viewerSeat=0"
                )
            }

            // 2. 验证每个 slotIndex (0..23) 下的 getDiscardSlotPosition 输出集合完全一致
            for (slotIndex in 0..23) {
                val baselineSlotSet = (0..3).map { seat -> screens[0].getDiscardSlotPosition(seat, slotIndex) }.toSet()
                assertEquals(4, baselineSlotSet.size, "Four distinct slot positions expected for slot $slotIndex")

                for (vs in 1..3) {
                    val slotSet = (0..3).map { seat -> screens[vs].getDiscardSlotPosition(seat, slotIndex) }.toSet()
                    assertEquals(
                        baselineSlotSet,
                        slotSet,
                        "Discard slot position set for slot $slotIndex at viewerSeat=$vs must be rotationally equivalent to viewerSeat=0"
                    )
                }
            }
        } finally {
            for (s in screens) {
                s.dispose()
            }
        }
    }
}
