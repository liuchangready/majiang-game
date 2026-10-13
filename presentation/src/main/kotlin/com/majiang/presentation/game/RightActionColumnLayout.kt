package com.majiang.presentation.game

import com.badlogic.gdx.math.Rectangle

/**
 * 统一右侧操作列按钮布局 (RightActionColumnLayout)。
 *
 * 冻结几何契约 (1280x720 逻辑坐标系，VISUAL-DESIGN.md)：
 * 统一宽 140、高 44、纵向间隔 6，x = 1080f：
 * 从下往上：
 * - 托管 (takeover): y = 6f
 * - 理牌 (sort): y = 56f
 * - 袖里藏花 (sleeveBloom): y = 106f
 * - 偷梁换柱 (beamSwap): y = 156f
 * - 落汗追踪 (tileMark): y = 206f
 *
 * 所有方法均返回防御性副本 (Rectangle)，防止外部调用方修改内部状态。
 */
object RightActionColumnLayout {
    const val X = 1080f
    const val WIDTH = 140f
    const val HEIGHT = 44f
    const val SPACING = 6f

    const val Y_TAKEOVER = 6f
    const val Y_SORT = 56f
    const val Y_SLEEVE_BLOOM = 106f
    const val Y_BEAM_SWAP = 156f
    const val Y_TILE_MARK = 206f

    fun takeoverRect(): Rectangle = Rectangle(X, Y_TAKEOVER, WIDTH, HEIGHT)
    fun sortRect(): Rectangle = Rectangle(X, Y_SORT, WIDTH, HEIGHT)
    fun sleeveBloomRect(): Rectangle = Rectangle(X, Y_SLEEVE_BLOOM, WIDTH, HEIGHT)
    fun beamSwapRect(): Rectangle = Rectangle(X, Y_BEAM_SWAP, WIDTH, HEIGHT)
    fun tileMarkRect(): Rectangle = Rectangle(X, Y_TILE_MARK, WIDTH, HEIGHT)
}
