package com.majiang.presentation.game

import com.majiang.client.api.LegalActionView
import com.majiang.client.api.PlayerPresentationPreviewView
import com.majiang.client.api.RiichiPreviewOptionView
import com.majiang.client.api.TileView
import java.util.Collections

/**
 * 立直选牌纯表现层状态模型 (V4 presentation.game)。
 *
 * 遵循契约与纯客户端模型规范：
 * 1. 严格只依赖 client.api 视图模型 (PlayerPresentationPreviewView, RiichiPreviewOptionView, TileView, LegalActionView)；
 * 2. 跟踪立直选牌模式的开启状态 (isSelecting)；
 * 3. 记录当前选中的物理弃牌与对应的方案 (selectedTile, selectedOption)；
 * 4. 缓存当前合法立直打牌方案与待牌列表 (options)；
 * 5. 响应视图与预览更新，自动维持或安全重置选牌状态；
 * 6. 不含任何 engine 或 protocol 依赖。
 */
class RiichiSelectionModel {

    /** 是否正处于立直选牌模式中 */
    var isSelecting: Boolean = false
        private set

    /** 当前选中的立直打出物理牌（未选中时为 null） */
    var selectedTile: TileView? = null
        private set

    /** 当前选中的立直方案（未选中时为 null） */
    var selectedOption: RiichiPreviewOptionView? = null
        private set

    /** 当前可行的立直弃牌方案列表 */
    var options: List<RiichiPreviewOptionView> = emptyList()
        private set

    /** 当前选中的合法动作（纯查询助手，等价于 selectedOption?.action） */
    val selectedAction: LegalActionView.DeclareRiichi?
        get() = selectedOption?.action

    private var lastPreview: PlayerPresentationPreviewView? = null

    /**
     * 刷新立直打牌方案。
     * - 首次初始化不进入选择；
     * - 相等 preview 保留当前选择；
     * - 预览实质变化时退出并清空选择，防止旧动作确认。
     */
    fun update(preview: PlayerPresentationPreviewView?) {
        val previous = lastPreview
        if (previous != null && previous == preview) {
            return
        }
        lastPreview = preview
        isSelecting = false
        selectedTile = null
        selectedOption = null
        options = if (preview == null) emptyList() else Collections.unmodifiableList(ArrayList(preview.riichiOptions))
    }

    /**
     * 直接更新立直方案列表（辅助或自定义无头测试）
     */
    fun update(newOptions: List<RiichiPreviewOptionView>) {
        lastPreview = null
        isSelecting = false
        selectedTile = null
        selectedOption = null
        options = Collections.unmodifiableList(ArrayList(newOptions))
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
            selectedOption = null
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
    fun select(tile: TileView): Boolean {
        if (!isSelecting) return false
        val matched = options.firstOrNull { it.discardTile == tile || it.discardTile.tileId == tile.tileId }
        if (matched != null) {
            selectedTile = matched.discardTile
            selectedOption = matched
            return true
        }
        return false
    }

    /**
     * 在选牌模式中选中指定立直方案。
     */
    fun select(option: RiichiPreviewOptionView): Boolean {
        if (!isSelecting) return false
        val matched = options.firstOrNull { it.optionId == option.optionId }
        if (matched != null) {
            selectedTile = matched.discardTile
            selectedOption = matched
            return true
        }
        return false
    }

    /**
     * 纯查询指定牌对应的立直方案与待牌（用于鼠标悬停临时预览等）。
     * 无任何内部状态变化。
     */
    fun previewFor(tile: TileView): RiichiPreviewOptionView? {
        return options.firstOrNull { it.discardTile == tile || it.discardTile.tileId == tile.tileId }
    }

    /**
     * 获取确认立直时需提交的方案。
     */
    fun confirmationOption(): RiichiPreviewOptionView? {
        if (!isSelecting) return null
        return selectedOption
    }

    /**
     * 获取确认立直时需提交的动作。
     * 仅在选牌模式内且已选中物理牌时返回精确合法动作，不自行提交。
     */
    fun confirmationAction(): LegalActionView.DeclareRiichi? {
        if (!isSelecting) return null
        return selectedOption?.action
    }

    /**
     * 退出选牌模式并清空当前选择。
     */
    fun cancel() {
        isSelecting = false
        selectedTile = null
        selectedOption = null
    }
}
