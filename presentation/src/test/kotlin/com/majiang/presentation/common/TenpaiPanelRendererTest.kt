package com.majiang.presentation.common

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TenpaiPanelRendererTest {

    @Test
    fun `computePanelRect returns single row height for up to 7 waits`() {
        val renderer = TenpaiPanelRenderer()

        for (count in 1..7) {
            val rect = renderer.computePanelRect(count)
            assertEquals(220f, rect.x)
            assertEquals(195f, rect.y)
            assertEquals(420f, rect.width)
            assertEquals(126f, rect.height, "Height must be 126f for $count waits")
        }
    }

    @Test
    fun `computePanelRect returns two rows height for 8 to 13 waits`() {
        val renderer = TenpaiPanelRenderer()

        for (count in 8..13) {
            val rect = renderer.computePanelRect(count)
            assertEquals(220f, rect.x)
            assertEquals(195f, rect.y)
            assertEquals(420f, rect.width)
            assertEquals(172f, rect.height, "Height must be 172f for $count waits (two rows)")
        }
    }
}
