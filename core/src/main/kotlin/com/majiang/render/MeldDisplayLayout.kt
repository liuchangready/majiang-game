package com.majiang.render

import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import java.util.Collections

/**
 * 副露牌面表现层描述（不可变数据模型）。
 *
 * @property tile 实际物理牌（保留 exact copyIndex 身份）
 * @property x 牌面绘制左下角 X 坐标
 * @property y 牌面绘制左下角 Y 坐标（默认 40f）
 * @property width 牌面绘制宽度（固定 36f）
 * @property height 牌面绘制高度（固定 52f）
 * @property faceDown 是否展示牌背（暗杠首尾两张为 true）
 * @property called 是否为外来鸣牌目标（橙色高亮并标 CALLED）
 * @property added 是否为加杠补入的第 4 张牌（蓝色高亮并标 ADD）
 */
data class MeldDisplayTile(
    val tile: Tile,
    val x: Float,
    val y: Float,
    val width: Float,
    val height: Float,
    val faceDown: Boolean,
    val called: Boolean,
    val added: Boolean
)

/**
 * 单个副露组表现层描述（不可变数据模型）。
 *
 * @property type 副露类型 (CHI / PON / OPEN_KAN / CLOSED_KAN / ADDED_KAN)
 * @property fromPlayer 鸣牌来源玩家座位 (0..3)，暗杠为 null
 * @property tiles 组成该副露的所有牌面展示列表（不可修改列表）
 * @property x 该组最左侧边界 X 坐标
 * @property width 该组总包围盒宽度
 */
data class MeldDisplayGroup(
    val type: MeldType,
    val fromPlayer: Int?,
    val tiles: List<MeldDisplayTile>,
    val x: Float,
    val width: Float
)

/**
 * 副露牌桌布局计算器（纯无状态函数，无 libGDX 依赖）。
 *
 * 核心布局规则契约：
 * 1. 已吃/碰/杠副露固定在本人底部牌区右侧，整体右对齐到 x=1048f；
 * 2. 每牌 width=36f, height=52f，组内横间距 step=38f (留 2f 间隙)；组间间隙 gap=12f；
 * 3. 所有牌 y=40f；
 * 4. 严格只读取 view.melds[viewerSeat]，只读深拷贝返回，零可变状态泄漏；
 * 5. 吃/碰 3 张（宽 112f），各类型杠 4 张（宽 150f）；加杠替换原碰组后为 4 张；
 * 6. 暗杠 (CLOSED_KAN) 首尾展示牌背、中间两张正面，不标 from/called；
 * 7. 加杠 (ADDED_KAN) 补入牌为最后一批 tiles.last()，加杠和明副露标明 FROM Pn；
 * 8. 最宽四杠状态 (412f..1048f) 与剩余手牌最大跨度 (220f..356f) 保持至少 56px 物理隔离，绝对不重叠。
 */
object MeldDisplayLayout {

    const val RIGHT_EDGE: Float = 1048f
    const val TILE_WIDTH: Float = 36f
    const val TILE_HEIGHT: Float = 52f
    const val TILE_STEP: Float = 38f
    const val GROUP_GAP: Float = 12f
    const val TILE_Y: Float = 40f

    /**
     * 计算当前观察者本人的已完成副露布局。
     */
    fun layout(view: PlayerView): List<MeldDisplayGroup> {
        val seatMelds = view.melds.getOrNull(view.viewerSeat) ?: return emptyList()
        if (seatMelds.isEmpty()) return emptyList()

        // 1. 计算每个组的实际包围盒宽度
        val groupWidths = seatMelds.map { meld ->
            if (meld.tiles.isEmpty()) 0f else (meld.tiles.size - 1) * TILE_STEP + TILE_WIDTH
        }

        // 2. 计算所有副露组整体总宽度与右对齐起始 X
        val totalWidth = groupWidths.sum() + (seatMelds.size - 1) * GROUP_GAP
        var currentX = RIGHT_EDGE - totalWidth

        val groups = ArrayList<MeldDisplayGroup>(seatMelds.size)

        for (gIdx in seatMelds.indices) {
            val meld = seatMelds[gIdx]
            val groupW = groupWidths[gIdx]
            val groupX = currentX
            val tiles = meld.tiles
            val displayTiles = ArrayList<MeldDisplayTile>(tiles.size)

            val isClosedKan = (meld.type == MeldType.CLOSED_KAN)
            val isAddedKan = (meld.type == MeldType.ADDED_KAN)

            for (tIdx in tiles.indices) {
                val tile = tiles[tIdx]
                val tileX = groupX + tIdx * TILE_STEP

                val faceDown: Boolean
                val isCalled: Boolean
                val isAdded: Boolean

                if (isClosedKan) {
                    // 暗杠：首尾牌背，中间两张正面，不标 called/added
                    faceDown = (tIdx == 0 || (tiles.size > 1 && tIdx == tiles.size - 1))
                    isCalled = false
                    isAdded = false
                } else if (isAddedKan) {
                    // 加杠：最后一牌为补入牌 (ADD)，正面展示；原碰鸣牌标 CALLED
                    faceDown = false
                    isAdded = (tiles.size > 1 && tIdx == tiles.size - 1)
                    isCalled = (!isAdded && tile == meld.calledTile)
                } else {
                    // 吃/碰/明杠：正面展示，被鸣牌标 CALLED
                    faceDown = false
                    isAdded = false
                    isCalled = (tile == meld.calledTile)
                }

                displayTiles.add(
                    MeldDisplayTile(
                        tile = tile,
                        x = tileX,
                        y = TILE_Y,
                        width = TILE_WIDTH,
                        height = TILE_HEIGHT,
                        faceDown = faceDown,
                        called = isCalled,
                        added = isAdded
                    )
                )
            }

            val fromPlayer = if (isClosedKan) {
                null
            } else if (meld.fromPlayer in 0..3) {
                meld.fromPlayer
            } else {
                null
            }

            groups.add(
                MeldDisplayGroup(
                    type = meld.type,
                    fromPlayer = fromPlayer,
                    tiles = Collections.unmodifiableList(displayTiles),
                    x = groupX,
                    width = groupW
                )
            )

            currentX += groupW + GROUP_GAP
        }

        return Collections.unmodifiableList(groups)
    }
}
