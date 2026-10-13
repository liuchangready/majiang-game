package com.majiang.presentation.common

import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.majiang.presentation.GdxHeadlessTestRunner
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SettlementDetailRendererTest : GdxHeadlessTestRunner() {

    @Test
    fun `wrapRowText keeps short text on single line`() {
        val font = BitmapFont()
        val text = "立直 1 番"
        val lines = SettlementDetailRenderer.wrapRowText(font, text, 500f)
        assertEquals(listOf("立直 1 番"), lines)
    }

    @Test
    fun `wrapRowText wraps long text across multiple lines without losing characters`() {
        val font = BitmapFont()
        val longText = "宝牌番数分项：表宝牌 10 番 | 里宝牌 10 番 | 赤宝牌 3 番 (宝牌小计 23 番)"
        val lines = SettlementDetailRenderer.wrapRowText(font, longText, 200f)
        assertTrue(lines.size >= 2, "Long text should wrap into 2 or more lines")

        // No characters should be lost when joined
        val joined = lines.joinToString(" ")
        for (token in listOf("表宝牌", "里宝牌", "赤宝牌", "宝牌小计")) {
            assertTrue(joined.contains(token), "Joined lines must contain token $token")
        }
    }

    @Test
    fun `wrapRowText handles empty text`() {
        val font = BitmapFont()
        val lines = SettlementDetailRenderer.wrapRowText(font, "", 200f)
        assertEquals(listOf(""), lines)
    }

    @Test
    fun `modal geometry constants are consistent`() {
        assertEquals(180f, SettlementModalGeometry.MODAL_RECT.x)
        assertEquals(95f, SettlementModalGeometry.MODAL_RECT.y)
        assertEquals(920f, SettlementModalGeometry.MODAL_RECT.width)
        assertEquals(540f, SettlementModalGeometry.MODAL_RECT.height)

        assertEquals(220f, SettlementModalGeometry.TAB_YAKU_RECT.x)
        assertEquals(390f, SettlementModalGeometry.TAB_DORA_RECT.x)
        assertEquals(560f, SettlementModalGeometry.TAB_CALC_RECT.x)

        assertEquals(220f, SettlementModalGeometry.BTN_PREV_PAGE_RECT.x)
        assertEquals(940f, SettlementModalGeometry.BTN_NEXT_PAGE_RECT.x)
    }
}
