package com.majiang.presentation.common.render

import com.majiang.client.api.MeldTypeView
import com.majiang.client.api.MeldView
import com.majiang.client.api.PlayerView
import com.majiang.client.api.TileView
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
    val tile: TileView,
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
 * @property ownerSeat 该副露组拥有者座位 (0..3)
 * @property y 该副露组基准 Y 坐标
 */
data class MeldDisplayGroup(
    val type: MeldTypeView,
    val fromPlayer: Int?,
    val tiles: List<MeldDisplayTile>,
    val x: Float,
    val width: Float,
    val ownerSeat: Int = 0,
    val y: Float = 40f
)

/**
 * 副露牌桌布局计算器（纯无状态函数，无 libGDX 依赖）。
 *
 * 核心布局规则契约：
 * 1. 本人已吃/碰/杠副露固定在本人底部牌区右侧，整体右对齐到 x=1048f，y=40f；
 * 2. 对家副露最多四组横排，同 36x52/38/12 宽度，整体右对齐到 x=958f，y=600f；四杠占 x322..958，不越界；
 * 3. 左侧副露位于 x=80f，右侧副露位于 x=1050f，每组一行，依次 y=480f, 390f, 300f, 210f；每行最多4张，宽150f；
 * 4. 每牌 width=36f, height=52f，组内横间距 step=38f (留 2f 间隙)；横向组间间隙 gap=12f；
 * 5. 吃/碰 3 张（宽 112f），各类型杠 4 张（宽 150f）；加杠替换原碰组后为 4 张；
 * 6. 暗杠 (CLOSED_KAN) 首尾展示牌背、中间两张正面，不标 from/called；
 * 7. 加杠 (ADDED_KAN) 补入牌为最后一批 tiles.last()，加杠和明副露标明 FROM Pn；
 * 8. 最宽四杠状态与剩余手牌最大跨度保持物理隔离，绝对不重叠。
 */
object MeldDisplayLayout {

    const val RIGHT_EDGE: Float = 1048f
    const val TOP_RIGHT_EDGE: Float = 958f
    const val LEFT_X: Float = 80f
    const val RIGHT_X: Float = 1050f
    const val TOP_Y: Float = 600f
    const val BOTTOM_Y: Float = 40f
    const val TILE_WIDTH: Float = 36f
    const val TILE_HEIGHT: Float = 52f
    const val TILE_STEP: Float = 38f
    const val GROUP_GAP: Float = 12f
    const val TILE_Y: Float = 40f

    private val SIDE_Y_COORDS = listOf(480f, 390f, 300f, 210f)

    /**
     * 计算当前观察者本人的已完成副露布局（保持现有语义与测试兼容）。
     */
    fun layout(view: PlayerView): List<MeldDisplayGroup> {
        val seatMelds = view.melds.getOrNull(view.viewerSeat) ?: return emptyList()
        if (seatMelds.isEmpty()) return emptyList()

        return layoutSelfMelds(seatMelds, view.viewerSeat)
    }

    /**
     * 计算牌桌全场四家的已完成副露布局（以当前观察者为相对视点）。
     */
    fun layoutAll(view: PlayerView): List<MeldDisplayGroup> = layoutAll(view.melds, view.viewerSeat)

    /** 同一公开副露布局也供不持有玩家私有视图的观战牌桌使用。 */
    fun layoutAll(melds: List<List<MeldView>>, viewerSeat: Int): List<MeldDisplayGroup> {
        require(viewerSeat in 0..3) { "viewerSeat must be in 0..3" }
        val allGroups = ArrayList<MeldDisplayGroup>()

        for (seat in 0..3) {
            val seatMelds = melds.getOrNull(seat) ?: continue
            if (seatMelds.isEmpty()) continue

            val rel = (seat - viewerSeat + 4) % 4
            val groups = when (rel) {
                0 -> layoutSelfMelds(seatMelds, seat)
                1 -> layoutSideMelds(seatMelds, seat, RIGHT_X)
                2 -> layoutTopMelds(seatMelds, seat)
                3 -> layoutSideMelds(seatMelds, seat, LEFT_X)
                else -> emptyList()
            }
            allGroups.addAll(groups)
        }

        return Collections.unmodifiableList(allGroups)
    }

    private fun layoutSelfMelds(seatMelds: List<MeldView>, seat: Int): List<MeldDisplayGroup> {
        val groupWidths = seatMelds.map { meld ->
            if (meld.tiles.isEmpty()) 0f else (meld.tiles.size - 1) * TILE_STEP + TILE_WIDTH
        }
        val totalWidth = groupWidths.sum() + (seatMelds.size - 1) * GROUP_GAP
        var currentX = RIGHT_EDGE - totalWidth

        val groups = ArrayList<MeldDisplayGroup>(seatMelds.size)
        for (gIdx in seatMelds.indices) {
            val meld = seatMelds[gIdx]
            val groupW = groupWidths[gIdx]
            val groupX = currentX
            val displayTiles = createDisplayTiles(meld, groupX, TILE_Y)
            val fromPlayer = resolveFromPlayer(meld)

            groups.add(
                MeldDisplayGroup(
                    type = meld.type,
                    fromPlayer = fromPlayer,
                    tiles = displayTiles,
                    x = groupX,
                    width = groupW,
                    ownerSeat = seat,
                    y = TILE_Y
                )
            )
            currentX += groupW + GROUP_GAP
        }
        return Collections.unmodifiableList(groups)
    }

    private fun layoutTopMelds(seatMelds: List<MeldView>, seat: Int): List<MeldDisplayGroup> {
        val groupWidths = seatMelds.map { meld ->
            if (meld.tiles.isEmpty()) 0f else (meld.tiles.size - 1) * TILE_STEP + TILE_WIDTH
        }
        val totalWidth = groupWidths.sum() + (seatMelds.size - 1) * GROUP_GAP
        var currentX = TOP_RIGHT_EDGE - totalWidth

        val groups = ArrayList<MeldDisplayGroup>(seatMelds.size)
        for (gIdx in seatMelds.indices) {
            val meld = seatMelds[gIdx]
            val groupW = groupWidths[gIdx]
            val groupX = currentX
            val displayTiles = createDisplayTiles(meld, groupX, TOP_Y)
            val fromPlayer = resolveFromPlayer(meld)

            groups.add(
                MeldDisplayGroup(
                    type = meld.type,
                    fromPlayer = fromPlayer,
                    tiles = displayTiles,
                    x = groupX,
                    width = groupW,
                    ownerSeat = seat,
                    y = TOP_Y
                )
            )
            currentX += groupW + GROUP_GAP
        }
        return Collections.unmodifiableList(groups)
    }

    private fun layoutSideMelds(seatMelds: List<MeldView>, seat: Int, startX: Float): List<MeldDisplayGroup> {
        val groups = ArrayList<MeldDisplayGroup>(seatMelds.size)
        for (gIdx in seatMelds.indices) {
            val meld = seatMelds[gIdx]
            val groupY = if (gIdx < SIDE_Y_COORDS.size) SIDE_Y_COORDS[gIdx] else 480f - gIdx * 90f
            val groupW = if (meld.tiles.isEmpty()) 0f else (meld.tiles.size - 1) * TILE_STEP + TILE_WIDTH
            val displayTiles = createDisplayTiles(meld, startX, groupY)
            val fromPlayer = resolveFromPlayer(meld)

            groups.add(
                MeldDisplayGroup(
                    type = meld.type,
                    fromPlayer = fromPlayer,
                    tiles = displayTiles,
                    x = startX,
                    width = groupW,
                    ownerSeat = seat,
                    y = groupY
                )
            )
        }
        return Collections.unmodifiableList(groups)
    }

    private fun createDisplayTiles(meld: MeldView, groupX: Float, groupY: Float): List<MeldDisplayTile> {
        val tiles = meld.tiles
        val displayTiles = ArrayList<MeldDisplayTile>(tiles.size)
        val isClosedKan = (meld.type == MeldTypeView.CLOSED_KAN)
        val isAddedKan = (meld.type == MeldTypeView.ADDED_KAN)

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
                    y = groupY,
                    width = TILE_WIDTH,
                    height = TILE_HEIGHT,
                    faceDown = faceDown,
                    called = isCalled,
                    added = isAdded
                )
            )
        }
        return Collections.unmodifiableList(displayTiles)
    }

    private fun resolveFromPlayer(meld: MeldView): Int? {
        return if (meld.type == MeldTypeView.CLOSED_KAN) {
            null
        } else if (meld.fromPlayer in 0..3) {
            meld.fromPlayer
        } else {
            null
        }
    }
}
