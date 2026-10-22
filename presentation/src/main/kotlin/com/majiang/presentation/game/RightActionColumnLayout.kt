package com.majiang.presentation.game

import com.badlogic.gdx.math.Rectangle

/**
 * 正式 APP 右侧操作列 (RightActionColumnLayout)。
 *
 * 设计矩形来自 RESPONSIVE-DESIGN.md：x=1078、宽 174、高 46，首行 y=466（左上角），
 * 行距 49。转换为 libGDX 左下角后为 208 / 159 / 110 / 61 / 12。
 * 五个按钮都落在固定 stage 内，不贴物理窗口右沿。
 */
object RightActionColumnLayout {
    const val X = 1078f
    const val WIDTH = 174f
    const val HEIGHT = 46f
    const val SPACING = 3f

    const val Y_TAKEOVER = 12f
    const val Y_SORT = 61f
    const val Y_SLEEVE_BLOOM = 110f
    const val Y_BEAM_SWAP = 159f
    const val Y_TILE_MARK = 208f

    fun takeoverRect(): Rectangle = Rectangle(X, Y_TAKEOVER, WIDTH, HEIGHT)
    fun sortRect(): Rectangle = Rectangle(X, Y_SORT, WIDTH, HEIGHT)
    fun sleeveBloomRect(): Rectangle = Rectangle(X, Y_SLEEVE_BLOOM, WIDTH, HEIGHT)
    fun beamSwapRect(): Rectangle = Rectangle(X, Y_BEAM_SWAP, WIDTH, HEIGHT)
    fun tileMarkRect(): Rectangle = Rectangle(X, Y_TILE_MARK, WIDTH, HEIGHT)
}
