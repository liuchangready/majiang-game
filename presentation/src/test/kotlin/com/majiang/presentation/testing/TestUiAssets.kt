package com.majiang.presentation.testing

import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.SkinManager

/**
 * 无头测试专用共享资产辅助对象。
 */
object TestUiAssets {
    fun create(): UiAssets = UiAssets.createDefault(SkinManager())

    fun createWithFonts(
        chineseFont: BitmapFont? = null,
        chineseFont17: BitmapFont? = null
    ): UiAssets {
        val base = create()
        val custom = UiAssets(
            batch = base.batch,
            shapeRenderer = base.shapeRenderer,
            font = base.font,
            chineseFont = chineseFont,
            chineseFont17 = chineseFont17,
            skinManager = base.skinManager
        )
        UiAssets.activeInstance = custom
        return custom
    }
}
