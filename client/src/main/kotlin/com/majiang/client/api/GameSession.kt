package com.majiang.client.api

import java.util.concurrent.CompletableFuture

/**
 * 客户端对局会话门面接口 (GameSession)
 *
 * 面向牌桌画面 (GameScreen) 提供已绑定席位后的权威操作、决策拉取与状态同步通道。
 *
 * 核心契约：
 * 1. **全客户端模型出参/入参**：快照 ([GameSnapshotView])、决策 ([DecisionView])、增量更新 ([GameUpdateView])、
 *    动作选择 ([ActionChoice])、洗牌计划 ([ShufflePlan])、回执 ([Receipt]) 全部采用不可变的客户端模型，
 *    彻底隔离 engine 与 protocol 领域实现；
 * 2. **控制权语义明确**：通过 [claimControl]（真人收回控制）与 [releaseControl]（AI 托管），废弃歧义的布尔 takeover；
 * 3. **全异步契约**：所有交互均返回 [CompletableFuture]，UI 渲染主线程零阻塞；
 * 4. **生命周期透传**：提供只读 [closed]、[disconnectReason] 与 [connectionState]，支持注册 [onStateChange]；
 * 5. **状态防护与缓存**：提供只读最新缓存快照 [currentSnapshot]，并在请求在途期间维护 [isPending] 状态，杜绝连点误触。
 */
interface GameSession : AutoCloseable {

    /**
     * 当前会话连接是否已经关闭或断开
     */
    val closed: Boolean

    /**
     * 连接终结稳定原因码（正常活跃时为 null）
     */
    val disconnectReason: String?

    /**
     * 当前连接状态 (CONNECTING, CONNECTED, RECONNECTING, CLOSED)
     */
    val connectionState: ConnectionState get() = ConnectionState.CONNECTED

    /**
     * 当前是否有动作/决策请求在途等待回执
     */
    val isPending: Boolean get() = false

    /**
     * 客户端本地缓存的最新权威快照视图（未收到快照前为 null）
     */
    val currentSnapshot: GameSnapshotView? get() = null

    /**
     * 注册连接状态变更监听器
     *
     * @param listener 接收最新 [ConnectionState] 的回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onStateChange(listener: (ConnectionState) -> Unit): AutoCloseable = AutoCloseable { }

    /**
     * 拉取当前座位的最新完整快照视图模型
     */
    fun snapshot(): CompletableFuture<GameSnapshotView>

    /**
     * 查询当前座位可执行的合法决策与候选动作
     */
    fun decision(): CompletableFuture<DecisionView>

    /**
     * 提交结构化动作选择（带幂等去重保证）
     *
     * @param choice 客户端动作选择载荷
     * @return 操作回执（status = APPLIED / RECORDED / REJECTED）
     */
    fun submit(choice: ActionChoice): CompletableFuture<Receipt>

    /**
     * 在开局码牌洗牌阶段提交千术预设方案
     *
     * @param plan 客户端洗牌方案请求
     * @return 操作回执
     */
    fun submitShufflePlan(plan: ShufflePlan): CompletableFuture<Receipt>

    /**
     * 提交落汗千术请求
     *
     * @param request 客户端标记请求
     * @return 操作回执
     */
    fun submitTileMark(request: TileMarkRequest): CompletableFuture<Receipt> =
        CompletableFuture.failedFuture(UnsupportedOperationException("submitTileMark is not supported"))

    /**
     * 提交袖里藏花千术激活请求
     *
     * @param request 客户端激活请求
     * @return 操作回执
     */
    fun submitSleeveBloom(request: SleeveBloomRequest): CompletableFuture<Receipt> =
        CompletableFuture.failedFuture(UnsupportedOperationException("submitSleeveBloom is not supported"))

    /**
     * 提交袖里藏花二选一决策请求
     *
     * @param request 客户端选择请求
     * @return 操作回执
     */
    fun submitSleeveBloomChoice(request: SleeveBloomChoiceRequest): CompletableFuture<Receipt> =
        CompletableFuture.failedFuture(UnsupportedOperationException("submitSleeveBloomChoice is not supported"))

    /**
     * 提交偷梁换柱千术激活请求
     *
     * @param request 客户端激活请求
     * @return 操作回执
     */
    fun submitBeamSwap(request: BeamSwapRequest): CompletableFuture<Receipt> =
        CompletableFuture.failedFuture(UnsupportedOperationException("submitBeamSwap is not supported"))

    /**
     * 提交偷梁换柱分支选择请求
     *
     * @param request 客户端选择请求
     * @return 操作回执
     */
    fun submitBeamSwapChoice(request: BeamSwapChoiceRequest): CompletableFuture<Receipt> =
        CompletableFuture.failedFuture(UnsupportedOperationException("submitBeamSwapChoice is not supported"))

    /**
     * 开始比赛（房主席位专属）
     */
    fun start(): CompletableFuture<Receipt>

    /**
     * 推进到下一局（单局结算完成后进入下一局准备）
     */
    fun nextRound(): CompletableFuture<Receipt>

    /**
     * 会话内基于指定游标恢复或对账状态
     *
     * @param cursor 客户端当前持有的单调递增游标（0 表示拉取全量）
     */
    fun resume(cursor: Long = 0L): CompletableFuture<GameSnapshotView>

    /**
     * 声明并收回当前席位的真人控制权（脱离 AI 托管）
     */
    fun claimControl(): CompletableFuture<Receipt>

    /**
     * 释放当前席位控制权交由 AI 托管
     */
    fun releaseControl(): CompletableFuture<Receipt>

    /**
     * 查询指定单局的公开结算事件投影
     *
     * @param roundId 目标局标识
     */
    fun queryRoundResult(roundId: String): CompletableFuture<List<GameEventView>>

    /**
     * 注册对局状态增量更新监听器
     *
     * @param listener 接收最新 [GameUpdateView] 的回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onUpdate(listener: (GameUpdateView) -> Unit): AutoCloseable

    /**
     * 注册连接终结/断线通知监听器
     *
     * @param listener 接收稳定终结原因码的回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable

    /**
     * 幂等关闭会话并释放底层连接
     */
    override fun close()
}
