package com.majiang.presentation.game

import com.majiang.client.api.LegalActionView
import com.majiang.client.api.MeldChoiceKindView
import com.majiang.client.api.MeldPreviewOptionView
import com.majiang.client.api.MeldTypeView
import com.majiang.client.api.PlayerPresentationPreviewView
import java.util.Collections

/**
 * 吃碰杠纯表现层选牌状态模型 (V4 presentation.game)。
 *
 * 遵循契约与纯客户端模型规范：
 * 1. 严格只依赖 client.api 视图模型 (PlayerPresentationPreviewView, MeldPreviewOptionView, MeldChoiceKindView)；
 * 2. 跟踪鸣牌选择模式 (isSelecting, kind)；
 * 3. 缓存当前类别的候选方案与分页列表 (options, visibleOptions, pageIndex, pageCount)，每页最多 3 个；
 * 4. 单方案自动预选，多方案初始不选中任何项需手动点选；
 * 5. 视图或决策发生实质变化时重置选牌状态，防止旧动作确认；
 * 6. 不含任何 engine 或 protocol 依赖。
 */
class MeldSelectionModel {

    /** 是否正处于鸣牌选牌模式中 */
    var isSelecting: Boolean = false
        private set

    /** 当前处于选择状态的鸣牌类别（未处于选择模式时为 null） */
    var kind: MeldChoiceKindView? = null
        private set

    /** 当前类别的全部候选方案列表 */
    var options: List<MeldPreviewOptionView> = emptyList()
        private set

    /** 当前选中的方案（未选中时为 null） */
    var selectedOption: MeldPreviewOptionView? = null
        private set

    /** 当前选中的动作（纯查询助手，等价于 selectedOption?.action） */
    val selectedAction: LegalActionView?
        get() = selectedOption?.action

    /** 当前查看的方案分页索引（0-indexed） */
    var pageIndex: Int = 0
        private set

    /** 总页数（每页最多 3 个方案） */
    val pageCount: Int
        get() = if (options.isEmpty()) 0 else (options.size + 2) / 3

    /** 当前可见页的方案列表（每页最多 3 个） */
    val visibleOptions: List<MeldPreviewOptionView>
        get() {
            if (!isSelecting || options.isEmpty()) return emptyList()
            val start = pageIndex * 3
            if (start >= options.size) return emptyList()
            val end = minOf(start + 3, options.size)
            return Collections.unmodifiableList(options.subList(start, end))
        }

    /** 当前选中的方案对象（纯查询助手） */
    val selectedChoice: MeldPreviewOptionView?
        get() = selectedOption

    private var lastPreview: PlayerPresentationPreviewView? = null

    /**
     * 刷新表现层预览视图。
     * - 初次 update 不打开选牌模式；
     * - 相等 preview 保留当前选择与选牌模式；
     * - 预览发生实质变化时退出并清空所有选择状态，防止旧动作确认。
     */
    fun update(preview: PlayerPresentationPreviewView?) {
        val previous = lastPreview
        if (previous != null && previous == preview) {
            return
        }
        lastPreview = preview
        cancel()
    }

    /**
     * 进入指定鸣牌类别的选牌模式。
     * - 仅在有对应方案（options 非空）时才进入并返回 true；
     * - 若已在选牌模式且为相同 kind，重复调用不改变当前选中项与页码；
     * - 若换用不同 kind，重置页码为 0 并重新计算方案；
     * - 单方案自动预选；
     * - 多方案初始不选中任何项（selectedOption = null），确认禁用。
     */
    fun begin(kind: MeldChoiceKindView): Boolean {
        val preview = lastPreview ?: return false
        val newOptions = preview.meldOptions.filter { opt ->
            when (kind) {
                MeldChoiceKindView.CHI -> opt.type == MeldTypeView.CHI
                MeldChoiceKindView.PON -> opt.type == MeldTypeView.PON
                MeldChoiceKindView.KAN -> opt.type == MeldTypeView.OPEN_KAN ||
                    opt.type == MeldTypeView.CLOSED_KAN ||
                    opt.type == MeldTypeView.ADDED_KAN
            }
        }
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
        selectedOption = if (newOptions.size == 1) newOptions[0] else null
        return true
    }

    /**
     * 选中一个候选方案。
     * - 仅在选牌模式内且属于当前可见页 (visibleOptions) 的合法方案才能选中；
     * - 非法或跨页选择返回 false 且不改变已有选择。
     */
    fun select(option: MeldPreviewOptionView): Boolean {
        if (!isSelecting) return false
        val matched = visibleOptions.firstOrNull { it.optionId == option.optionId }
        if (matched != null) {
            selectedOption = matched
            return true
        }
        return false
    }

    /**
     * 按动作匹配选中一个候选方案（测试兼容与精确动作匹配）。
     */
    fun select(action: LegalActionView): Boolean {
        if (!isSelecting) return false
        val matched = visibleOptions.firstOrNull { it.action == action }
        if (matched != null) {
            selectedOption = matched
            return true
        }
        return false
    }

    /**
     * 设置当前页码。
     * - 仅在选牌模式内且有效页码范围内生效；
     * - 仅在页码实质变化（index != pageIndex）时翻页并清空当前选中项；
     * - 非法页码或相同页码返回 false。
     */
    fun setPage(index: Int): Boolean {
        if (!isSelecting) return false
        if (index !in 0 until pageCount) return false
        if (index == pageIndex) return false
        pageIndex = index
        selectedOption = null
        return true
    }

    /**
     * 获取确认鸣牌时需提交的合法动作。
     * - 仅在选牌模式内且已选中合法方案时返回；
     * - 选牌模式外或多方案未选中时返回 null。
     */
    fun confirmationAction(): LegalActionView? {
        if (!isSelecting) return null
        return selectedOption?.action
    }

    /**
     * 获取确认鸣牌时需提交的方案。
     */
    fun confirmationOption(): MeldPreviewOptionView? {
        if (!isSelecting) return null
        return selectedOption
    }

    /**
     * 退出选牌模式并清空类别、方案、选中项与页码。
     */
    fun cancel() {
        isSelecting = false
        kind = null
        options = emptyList()
        selectedOption = null
        pageIndex = 0
    }
}
