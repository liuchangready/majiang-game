package com.majiang.screen

import com.majiang.session.MeldChoice
import com.majiang.session.MeldChoiceKind
import com.majiang.session.MeldChoiceProvider
import com.majiang.session.PlayerView
import com.majiang.state.GameAction
import java.util.Collections

/**
 * 吃碰杠纯表现层选牌状态模型。
 *
 * 独立于 libGDX 图形上下文，便于无头测试：
 * - 跟踪鸣牌选择模式的开启状态 (isSelecting)；
 * - 记录当前选牌类别 (kind: CHI / PON / KAN)；
 * - 缓存当前类别的候选方案与分页列表 (options, visibleOptions, pageIndex, pageCount)；
 * - 记录当前选中的动作 (selectedAction)；
 * - 响应 PlayerView 视图更新，自动维持或重置选牌状态。
 */
class MeldSelectionModel {

    /** 是否正处于鸣牌选牌模式中 */
    var isSelecting: Boolean = false
        private set

    /** 当前处于选择状态的鸣牌类别（未处于选择模式时为 null） */
    var kind: MeldChoiceKind? = null
        private set

    /** 当前类别的全部候选方案列表 */
    var options: List<MeldChoice> = emptyList()
        private set

    /** 当前选中的领域动作（未选中时为 null） */
    var selectedAction: GameAction? = null
        private set

    /** 当前查看的方案分页索引（0-indexed） */
    var pageIndex: Int = 0
        private set

    /** 总页数（每页最多 3 个方案） */
    val pageCount: Int
        get() = if (options.isEmpty()) 0 else (options.size + 2) / 3

    /** 当前可见页的方案列表（每页最多 3 个） */
    val visibleOptions: List<MeldChoice>
        get() {
            if (!isSelecting || options.isEmpty()) return emptyList()
            val start = pageIndex * 3
            if (start >= options.size) return emptyList()
            val end = minOf(start + 3, options.size)
            return Collections.unmodifiableList(options.subList(start, end))
        }

    /** 当前选中的方案对象（纯查询助手，未选中或模式外为 null） */
    val selectedChoice: MeldChoice?
        get() {
            val act = selectedAction ?: return null
            return options.firstOrNull { it.action == act }
        }

    private var lastView: PlayerView? = null

    /**
     * 刷新会话视图。
     * - 初次 update 不打开选牌模式；
     * - 相等 view 保留当前选择与选牌模式；
     * - 视图发生实质变化时退出并清空所有选择状态，防止旧动作确认；
     * - 理牌开关等纯 UI 状态不修改 PlayerView 结构值，不影响选择。
     */
    fun update(view: PlayerView) {
        val previous = lastView
        if (previous != null && previous == view) {
            return
        }
        lastView = view
        cancel()
    }

    /**
     * 进入指定鸣牌类别的选牌模式。
     * - 仅在有对应方案（options 非空）时才进入并返回 true；
     * - 若已在选牌模式且为相同 kind，重复调用不改变当前选中项与页码；
     * - 若换用不同 kind，重置页码为 0 并重新计算方案；
     * - 单方案自动预选（selectedAction 为该方案动作）；
     * - 多方案初始不选中任何项（selectedAction = null），确认禁用。
     *
     * @return 是否成功进入或处于选牌模式
     */
    fun begin(kind: MeldChoiceKind): Boolean {
        val currentView = lastView ?: return false
        val newOptions = MeldChoiceProvider.options(currentView, kind)
        if (newOptions.isEmpty()) {
            return false
        }
        if (isSelecting && this.kind == kind) {
            return true
        }
        isSelecting = true
        this.kind = kind
        options = newOptions
        pageIndex = 0
        selectedAction = if (newOptions.size == 1) newOptions[0].action else null
        return true
    }

    /**
     * 选中一个候选动作。
     * - 仅在选牌模式内且属于当前可见页 (visibleOptions) 的合法动作才能选中；
     * - 非法或跨页选择返回 false 且不改变已有选择。
     *
     * @return 是否选中成功
     */
    fun select(action: GameAction): Boolean {
        if (!isSelecting) return false
        val matched = visibleOptions.firstOrNull { it.action == action }
        if (matched != null) {
            selectedAction = matched.action
            return true
        }
        return false
    }

    /**
     * 设置当前页码。
     * - 仅在选牌模式内且有效页码范围内生效；
     * - 仅在页码实质变化（index != pageIndex）时翻页并清空当前选中项；
     * - 非法页码或相同页码返回 false，不修改当前页码与选择。
     *
     * @return 是否成功翻页
     */
    fun setPage(index: Int): Boolean {
        if (!isSelecting) return false
        if (index !in 0 until pageCount) return false
        if (index == pageIndex) return false
        pageIndex = index
        selectedAction = null
        return true
    }

    /**
     * 获取确认鸣牌时需提交的合法动作。
     * - 仅在选牌模式内且已选中合法方案时返回精确原始动作；
     * - 选牌模式外或多方案未选中时返回 null；
     * - 本方法不自行向会话提交。
     */
    fun confirmationAction(): GameAction? {
        if (!isSelecting) return null
        val selected = selectedAction ?: return null
        return options.firstOrNull { it.action == selected }?.action
    }

    /**
     * 退出选牌模式并清空类别、方案、选中项与页码。
     * 不清除 lastView，便于后续重新进入。
     */
    fun cancel() {
        isSelecting = false
        kind = null
        options = emptyList()
        selectedAction = null
        pageIndex = 0
    }
}
