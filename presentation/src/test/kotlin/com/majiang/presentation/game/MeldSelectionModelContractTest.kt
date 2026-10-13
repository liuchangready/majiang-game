package com.majiang.presentation.game

import com.majiang.client.api.LegalActionView
import com.majiang.client.api.MeldChoiceKindView
import com.majiang.client.api.MeldPreviewOptionView
import com.majiang.client.api.MeldTypeView
import com.majiang.client.api.MeldView
import com.majiang.client.api.PlayerPresentationPreviewView
import com.majiang.client.api.SuitView
import com.majiang.presentation.game.MeldSelectionModel as V4MeldSelectionModel
import com.majiang.presentation.testing.TestViewBuilder
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class MeldSelectionModelContractTest {
    private fun chiChoices(): List<MeldPreviewOptionView> {
        val fours = (0..1).map { TestViewBuilder.tile(SuitView.MAN, 4, it) }
        val sixes = (0..1).map { TestViewBuilder.tile(SuitView.MAN, 6, it) }
        val called = TestViewBuilder.tile(SuitView.MAN, 5)
        return fours.flatMap { four -> sixes.map { six ->
            val tiles = listOf(four, called, six)
            val action = LegalActionView.DeclareChi(2, MeldView(MeldTypeView.CHI, tiles, 1, called))
            MeldPreviewOptionView(
                optionId = "chi-${four.tileId}-${six.tileId}",
                action = action,
                type = MeldTypeView.CHI,
                tiles = tiles,
                handTiles = listOf(four, six),
                calledTile = called,
                fromPlayer = 1
            )
        } }
    }

    private fun ponChoice(): MeldPreviewOptionView {
        val hand = (0..1).map { TestViewBuilder.tile(SuitView.PIN, 8, it) }
        val called = TestViewBuilder.tile(SuitView.PIN, 8, 2)
        return MeldPreviewOptionView(
            optionId = "pon-8p",
            action = LegalActionView.DeclarePon(2, hand),
            type = MeldTypeView.PON,
            tiles = hand + called,
            handTiles = hand,
            calledTile = called,
            fromPlayer = 1
        )
    }

    private fun preview(vararg options: MeldPreviewOptionView) =
        PlayerPresentationPreviewView("round-1", "decision-1", 0L, meldOptions = options.toList())

    @Test fun `multiple choices require selection and confirm returns exact physical action`() {
        val model = V4MeldSelectionModel()
        val options = chiChoices().take(2)
        model.update(preview(*options.toTypedArray()))
        assertFalse(model.isSelecting)
        assertNull(model.confirmationAction())
        assertTrue(model.begin(MeldChoiceKindView.CHI))
        assertNull(model.selectedAction)
        assertNull(model.confirmationAction())
        assertTrue(model.select(options.last()))
        assertEquals(options.last().action, model.confirmationAction())
        assertEquals(options.last().optionId, model.confirmationOption()?.optionId)
        assertFalse(model.select(LegalActionView.Pass(2)))
        assertEquals(options.last().action, model.confirmationAction())
        assertTrue(model.begin(MeldChoiceKindView.CHI))
        assertEquals(options.last().action, model.confirmationAction())
    }

    @Test fun `single choice preselects but cancellation and unavailable categories never confirm`() {
        val model = V4MeldSelectionModel()
        val pon = ponChoice()
        model.update(preview(pon))
        assertFalse(model.begin(MeldChoiceKindView.KAN))
        assertTrue(model.begin(MeldChoiceKindView.PON))
        assertEquals(pon.action, model.confirmationAction())
        assertEquals(pon.optionId, model.confirmationOption()?.optionId)
        model.cancel()
        assertFalse(model.isSelecting)
        assertNull(model.selectedAction)
        assertNull(model.confirmationAction())
    }

    @Test fun `equal preview preserves selection and new decision invalidates it`() {
        val model = V4MeldSelectionModel()
        val preview = preview(*chiChoices().take(2).toTypedArray())
        model.update(preview)
        assertTrue(model.begin(MeldChoiceKindView.CHI))
        val selected = model.options.last()
        assertTrue(model.select(selected))
        model.update(preview.copy())
        assertEquals(selected.action, model.confirmationAction())
        model.update(preview.copy(decisionId = "decision-2"))
        assertFalse(model.isSelecting)
        assertNull(model.confirmationAction())
    }

    @Test fun `pages expose all physical choices and never confirm an off page option`() {
        val choices = chiChoices()
        val model = V4MeldSelectionModel()
        model.update(preview(*choices.toTypedArray()))
        assertTrue(model.begin(MeldChoiceKindView.CHI))
        assertEquals(2, model.pageCount)
        assertEquals(3, model.visibleOptions.size)
        assertFalse(model.select(choices.last()))
        assertTrue(model.select(choices.first()))
        assertFalse(model.setPage(-1))
        assertEquals(choices.first().action, model.confirmationAction())
        assertTrue(model.setPage(1))
        assertNull(model.selectedAction)
        assertEquals(listOf(choices.last().optionId), model.visibleOptions.map { it.optionId })
        assertTrue(model.select(choices.last()))
        assertEquals(choices.last().action, model.confirmationAction())
        assertFalse(model.setPage(2))
        assertEquals(1, model.pageIndex)
    }
}
