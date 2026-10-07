package com.majiang.client.api

import java.util.concurrent.CompletableFuture

/**
 * 客户端观战会话门面接口 (SpectatorSession)
 *
 * 面向观战画面提供独立只读公开信息流查询、更新订阅与生命周期管理。
 *
 * 核心契约：
 * 1. **全客户端模型出参/入参**：公开快照 ([PublicSnapshotView])、增量更新 ([PublicUpdateView])、
 *    公开结算事件列表 ([GameEventView])、公开检查点 ([PublicCheckpoint]) 与操作回执 ([Receipt]) 全部采用
 *    不可变的客户端独立模型，彻底隔离 protocol 与 engine 领域实现；
 * 2. **独立只读公开流**：不继承 [GameSession] 或 [LobbySession] 的可写玩家动作/决策接口（如 submit、claimControl、setReady 等），
 *    绝不包裹私有 GameSnapshotView；
 * 3. **全异步契约**：所有交互均返回 [CompletableFuture]，UI 渲染主线程零阻塞，订阅方法返回 [AutoCloseable]；
 * 4. **生命周期透传**：提供只读 [closed]、[disconnectReason] 与 [connectionState]，支持注册 [onStateChange] 与 [onDisconnect]；
 * 5. **状态防护与缓存**：提供只读最新缓存公开快照 [currentPublicSnapshot]。
 */
interface SpectatorSession : AutoCloseable {

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
    val connectionState: ConnectionState

    /**
     * 客户端本地缓存的最新权威公开快照视图（未收到快照前为 null）
     */
    val currentPublicSnapshot: PublicSnapshotView?

    /**
     * 注册连接状态变更监听器
     *
     * @param listener 接收最新 [ConnectionState] 的回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onStateChange(listener: (ConnectionState) -> Unit): AutoCloseable

    /**
     * 拉取当前房间最新的权威全量公开快照视图模型
     */
    fun publicSnapshot(): CompletableFuture<PublicSnapshotView>

    /**
     * 基于指定公开检查点对账或恢复全量公开快照
     *
     * @param checkpoint 客户端当前持有的完整公开检查点 (sessionId, syncEpoch, cursor)
     */
    fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<PublicSnapshotView>

    /**
     * 查询指定单局的公开结算事件（白名单放行）
     *
     * @param roundId 目标局标识
     */
    fun queryPublicRoundResult(roundId: String): CompletableFuture<List<GameEventView>>

    /**
     * 注册公开状态增量更新监听器
     *
     * @param listener 接收最新 [PublicUpdateView] 的回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onPublicUpdate(listener: (PublicUpdateView) -> Unit): AutoCloseable

    /**
     * 注册连接终结/断线通知监听器
     *
     * @param listener 接收稳定终结原因码的回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable

    /**
     * 查询当前所在房间的完整状态信息
     */
    fun getRoomInfo(): CompletableFuture<RoomInfo>

    /**
     * 注册房间状态变更监听器（成员加入/离开、准备就绪、规则变更、生命周期流转）
     *
     * @param listener 房间状态回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onRoomUpdate(listener: (RoomInfo) -> Unit): AutoCloseable

    /**
     * 显式离开当前观战的房间
     *
     * @return 操作回执（status = APPLIED 表示已成功离开）
     */
    fun leaveRoom(): CompletableFuture<Receipt>

    /**
     * 幂等关闭会话并释放底层连接
     */
    override fun close()
}
