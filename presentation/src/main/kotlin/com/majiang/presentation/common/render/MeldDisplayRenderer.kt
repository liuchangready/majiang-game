package com.majiang.presentation.common.render

import com.badlogic.gdx.graphics.Color
import com.majiang.client.api.MeldTypeView

/**
 * 副露牌面表现层渲染指令追加器。
 *
 * 遵循指令生产者 (Producer) 与缓冲消费者 (Consumer) 模式：
 * 1. 严格只向 [TileDrawBuffer] 追加图元绘制指令，不执行任何直接 GL 调用，不调用 begin/end；
 * 2. 牌面绘制委托 [TileSkin.appendTileDraw] 与 [TileSkin.appendTileBackDraw]；
 * 3. 边框通过 [TileDrawBuffer.addLineRect] 绘制，文字通过 [TileDrawBuffer.addText] 提交；
 * 4. 标签最高 y=110f (<= 114f)，最低 y=27f (>= 25f)，保守字号杜绝重叠与挤压。
 */
object MeldDisplayRenderer {

    /**
     * 将已计算好的副露组布局指令追加到现有的 [TileDrawBuffer] 牌面缓冲中。
     */
    fun append(
        buffer: TileDrawBuffer,
        skin: TileSkin,
        groups: List<MeldDisplayGroup>,
        doraTargets: Set<Int> = emptySet(),
        sparkleAlpha: Float = 0f,
        markedTileIds: Set<Int> = emptySet()
    ) {
        if (groups.isEmpty()) return

        for (group in groups) {
            // 1. 绘制组内所有牌面及高亮框 / 牌标签
            for (dt in group.tiles) {
                if (dt.faceDown) {
                    skin.appendTileBackDraw(buffer, dt.x, dt.y, dt.width, dt.height)
                } else {
                    val tileAlpha = if (dt.tile.tileId in markedTileIds) 0.55f else 1.0f
                    skin.appendTileDraw(
                        buf = buffer,
                        tile = dt.tile,
                        x = dt.x,
                        y = dt.y,
                        width = dt.width,
                        height = dt.height,
                        isHover = false,
                        isDrawn = false,
                        alpha = tileAlpha
                    )

                    if (DoraSparkle.isSparkleTarget(dt.tile, doraTargets) && sparkleAlpha > 0f) {
                        val hasInteraction = dt.called || dt.added
                        DoraSparkle.appendSparkle(
                            buf = buffer,
                            x = dt.x,
                            y = dt.y,
                            width = dt.width,
                            height = dt.height,
                            alpha = sparkleAlpha,
                            hasInteractionHighlight = hasInteraction
                        )
                    }
                }

                val subLabelY = dt.y - 5f
                if (dt.called) {
                    // 外来被鸣牌橙色描边 (双层 lineRect 增强清晰度)
                    buffer.addLineRect(dt.x, dt.y, dt.width, dt.height, Color(1.0f, 0.45f, 0.15f, 1f))
                    buffer.addLineRect(dt.x - 1f, dt.y - 1f, dt.width + 2f, dt.height + 2f, Color(1.0f, 0.45f, 0.15f, 1f))
                    // CALLED 小标签居于牌下方 (dt.y - 5f, 针对本人 y=35f, 针对对家 y=595f, 左右根据 group.y - 5f)
                    buffer.addText("CALLED", Color(1.0f, 0.55f, 0.20f, 1f), dt.x + 2f, subLabelY, 0.50f, 0.50f)
                } else if (dt.added) {
                    // 加杠补入牌蓝色描边 (双层 lineRect 增强清晰度)
                    buffer.addLineRect(dt.x, dt.y, dt.width, dt.height, Color(0.2f, 0.85f, 1.0f, 1f))
                    buffer.addLineRect(dt.x - 1f, dt.y - 1f, dt.width + 2f, dt.height + 2f, Color(0.2f, 0.85f, 1.0f, 1f))
                    // ADD 小标签居于牌下方 (dt.y - 5f, 针对本人 y=35f, 针对对家 y=595f, 左右根据 group.y - 5f)
                    buffer.addText("ADD", Color(0.2f, 0.85f, 1.0f, 1f), dt.x + 9f, subLabelY, 0.50f, 0.50f)
                }
            }

            // 2. 绘制组上方类型标签与来源
            val isSelf = (group.y == MeldDisplayLayout.TILE_Y)
            if (isSelf) {
                // 本人组：绘制组上方类型标签与来源 (最高 y=110f <= 114f，最低 y=99f > 92f 避开牌面)
                if (group.type == MeldTypeView.CLOSED_KAN) {
                    // 暗杠不标 from/called，使用浅蓝浅灰字样的 CLOSED KAN 标签
                    buffer.addText("CLOSED KAN", Color(0.70f, 0.85f, 0.95f, 1f), group.x + 2f, 108f, 0.65f, 0.65f)
                } else {
                    val typeName = when (group.type) {
                        MeldTypeView.CHI -> "CHI"
                        MeldTypeView.PON -> "PON"
                        MeldTypeView.OPEN_KAN -> "OPEN KAN"
                        MeldTypeView.ADDED_KAN -> "ADDED KAN"
                        MeldTypeView.CLOSED_KAN -> "CLOSED KAN"
                    }
                    buffer.addText(typeName, Color(1.0f, 0.85f, 0.3f, 1f), group.x + 2f, 110f, 0.65f, 0.65f)

                    if (group.fromPlayer != null) {
                        buffer.addText("FROM P${group.fromPlayer}", Color(0.85f, 0.85f, 0.85f, 1f), group.x + 2f, 99f, 0.55f, 0.55f)
                    }
                }
            } else {
                // 对手组：对家 (top, y >= 550f, y666) 与左右侧 (y < 550f, y+68)
                val isTop = (group.y >= 550f)
                val labelY = if (isTop) 666f else group.y + 68f
                val typeName = when (group.type) {
                    MeldTypeView.CHI -> "CHI"
                    MeldTypeView.PON -> "PON"
                    MeldTypeView.OPEN_KAN -> "OPEN KAN"
                    MeldTypeView.ADDED_KAN -> "ADDED KAN"
                    MeldTypeView.CLOSED_KAN -> "CLOSED KAN"
                }

                if (group.type == MeldTypeView.CLOSED_KAN) {
                    val labelText = "P${group.ownerSeat} CLOSED KAN"
                    buffer.addText(labelText, Color(0.70f, 0.85f, 0.95f, 1f), group.x + 2f, labelY, 0.60f, 0.60f)
                } else {
                    val labelText = if (group.fromPlayer != null) {
                        "P${group.ownerSeat} $typeName FROM P${group.fromPlayer}"
                    } else {
                        "P${group.ownerSeat} $typeName"
                    }
                    buffer.addText(labelText, Color(1.0f, 0.85f, 0.3f, 1f), group.x + 2f, labelY, 0.60f, 0.60f)
                }
            }
        }
    }
}
