package com.majiang.presentation.common.render

/**
 * 牌桌视图同步身份 (TableBoardViewIdentity)。
 *
 * 公开桌面渲染与弃牌动画只依赖以下四项权威身份：
 * sessionId、syncEpoch、roundId 与单调递增 cursor。
 * 不包含玩家席位、手牌、决策或千术凭据，玩家屏与观战屏均可安全持有。
 */
data class TableBoardViewIdentity(
    val sessionId: String,
    val syncEpoch: String,
    val roundId: String?,
    val cursor: Long
)

/**
 * 一次牌桌快照到达时的同步过渡分类 (TableBoardSyncTransition)。
 *
 * @property firstBaseline 首次建立基线（无旧视图可比较）
 * @property sessionChanged 对局会话变更
 * @property epochChanged 同步代次变更
 * @property roundChanged 局标识变更
 * @property cursorJumped 非全量更新出现游标倒退或跳号（漏包）
 * @property fullSnapshot 权威显式全量快照（重连/重建基线）
 * @property duplicate 与上一份完全相同的普通快照（重复投递）
 */
data class TableBoardSyncTransition(
    val firstBaseline: Boolean,
    val sessionChanged: Boolean,
    val epochChanged: Boolean,
    val roundChanged: Boolean,
    val cursorJumped: Boolean,
    val fullSnapshot: Boolean,
    val duplicate: Boolean
) {
    /**
     * 是否必须先清空旧动画并重建牌河基线。
     *
     * 首次基线、换会话/代次/局、漏包跳号与显式全量快照都不得把历史牌
     * 当作“新打出的牌”重播，否则旧动画会掩盖重建后的新牌。
     */
    val requiresRebuild: Boolean
        get() = firstBaseline || sessionChanged || epochChanged || roundChanged ||
            cursorJumped || fullSnapshot
}

/**
 * 牌桌视图同步身份追踪器 (TableBoardSyncTracker)。
 *
 * 每个牌桌屏幕拥有独立实例，负责：
 * 1. 记录本屏已消费的权威身份与游标；
 * 2. 区分普通连续更新、显式全量基线、漏包跳号、重复投递与换局/换代次；
 * 3. 为调用方给出“是否清空并重建动画基线”的明确决策。
 *
 * 该组件不持有动画、快照或绘制资源，纯表现层状态追踪，不产生全局单例。
 */
class TableBoardSyncTracker(initialIdentity: TableBoardViewIdentity? = null) {

    var lastIdentity: TableBoardViewIdentity? = initialIdentity
        private set

    val lastCursor: Long? get() = lastIdentity?.cursor

    /**
     * 观察一份到达的权威视图身份并更新本屏游标。
     *
     * 注意：判定与游标推进是原子的；调用方必须在同一次快照应用中使用返回的
     * transition，不能在分类与消费之间再次插入其他快照。
     */
    fun observe(
        identity: TableBoardViewIdentity,
        isFullSnapshot: Boolean = false
    ): TableBoardSyncTransition {
        val prior = lastIdentity
        val firstBaseline = prior == null
        val sessionChanged = prior != null && prior.sessionId != identity.sessionId
        val epochChanged = prior != null && prior.syncEpoch != identity.syncEpoch
        val roundChanged = prior != null && prior.roundId != identity.roundId

        // 显式全量快照允许游标原地重建或跨越；它本身已经是“重建基线”信号，
        // 不再重复报告为漏包，避免调用方误把全量事件重播一遍。
        val cursorJumped = prior != null && !isFullSnapshot &&
            (identity.cursor < prior.cursor || identity.cursor > prior.cursor + 1L)

        val duplicate = prior != null && !isFullSnapshot &&
            !sessionChanged && !epochChanged && !roundChanged &&
            identity.cursor == prior.cursor

        val transition = TableBoardSyncTransition(
            firstBaseline = firstBaseline,
            sessionChanged = sessionChanged,
            epochChanged = epochChanged,
            roundChanged = roundChanged,
            cursorJumped = cursorJumped,
            fullSnapshot = isFullSnapshot,
            duplicate = duplicate
        )

        lastIdentity = identity
        return transition
    }

    /** 清空身份基线（屏幕释放或视角重建时使用）。 */
    fun reset() {
        lastIdentity = null
    }
}
