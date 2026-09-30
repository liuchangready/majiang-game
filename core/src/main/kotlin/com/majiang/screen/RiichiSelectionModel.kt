package com.majiang.screen

import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.session.RiichiDiscardOption
import com.majiang.session.RiichiPreviewProvider
import com.majiang.state.GameAction

/**
 * 立直选牌纯表现层状态模型。
 *
 * 独立于 libGDX 图形上下文，便于无头测试：
 * - 跟踪立直选牌模式的开启状态 (isSelecting)；
 * - 记录当前选中的物理弃牌 (selectedTile)；
 * - 缓存当前合法立直打牌方案与待牌列表 (options)；
 * - 响应 PlayerView 视图更新，自动维持或重置选牌状态。
 */
class RiichiSelectionModel {

    /** 是否正处于立直选牌模式中 */
    var isSelecting: Boolean = false
        private set

    /** 当前选中的立直打出物理牌（未选中时为 null） */
    var selectedTile: Tile? = null
        private set

    /** 当前可行的立直弃牌方案列表 */
    var options: List<RiichiDiscardOption> = emptyList()
        private set

    private var lastView: PlayerView? = null

    /**
     * 刷新立直打牌方案。
     * - 首次初始化不进入选择；
     * - 值相等视图更新保留选择；
     * - 视图实质变化时退出并清空选择，防止旧动作确认；
     * - 排序等纯 UI 状态不修改 PlayerView 视图值，因此不影响选择。
     */
    fun update(view: PlayerView) {
        val previous = lastView
        if (previous != null && previous == view) {
            return
        }
        lastView = view
        isSelecting = false
        selectedTile = null
        options = RiichiPreviewProvider.options(view)
    }

    /**
     * 进入立直选牌模式。
     * - 有方案才进入（options 非空）；
     * - 首次进入时不自动选中任何牌；
     * - 已在选牌模式中再次调用 begin 时不清空已有选择。
     *
     * @return 是否成功处于选牌模式
     */
    fun begin(): Boolean {
        if (options.isEmpty()) return false
        if (!isSelecting) {
            isSelecting = true
            selectedTile = null
        }
        return true
    }

    /**
     * 在选牌模式中选中一张待打出的物理牌。
     * - 仅模式内合法物理弃牌可选；
     * - 非法选择不出牌、不改变已有选择。
     *
     * @return 是否选中成功
     */
    fun select(tile: Tile): Boolean {
        if (!isSelecting) return false
        val isLegal = options.any { it.action.discardTile == tile }
        if (isLegal) {
            selectedTile = tile
            return true
        }
        return false
    }

    /**
     * 纯查询指定牌对应的立直方案与待牌（用于鼠标悬停临时预览等）。
     * 无任何内部状态变化。
     */
    fun previewFor(tile: Tile): RiichiDiscardOption? {
        return options.firstOrNull { it.action.discardTile == tile }
    }

    /**
     * 获取确认立直时需提交的动作。
     * 仅在选牌模式内且已选中物理牌时返回精确合法动作，不自行提交。
     */
    fun confirmationAction(): GameAction.DeclareRiichi? {
        if (!isSelecting) return null
        val chosen = selectedTile ?: return null
        return options.firstOrNull { it.action.discardTile == chosen }?.action
    }

    /**
     * 退出选牌模式并清空当前选择。
     */
    fun cancel() {
        isSelecting = false
        selectedTile = null
    }
}
