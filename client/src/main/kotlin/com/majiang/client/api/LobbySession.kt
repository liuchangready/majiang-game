package com.majiang.client.api

import java.util.concurrent.CompletableFuture

/**
 * 客户端大厅会话门面接口 (LobbySession)
 *
 * 面向菜单画面 (MenuScreen) 提供未绑定席位或进行房间操作的高层异步接口。
 *
 * 核心契约：
 * 1. **全异步契约**：所有方法一律返回 [CompletableFuture]，调用方 UI / 渲染线程零阻塞；
 * 2. **全客户端模型**：出参入参仅依赖 `client.api` 定义的不可变模型与原生类型，无 engine 依赖；
 * 3. **配置参数**：[createRoom] 可选接收客户端比赛规则配置 [MatchConfig]，为 null 时采用服务端权威默认配置（半庄战）；
 * 4. **显式离开语义**：[leaveRoom] 显式退出房间，返回客户端 [Receipt]；
 * 5. **连接生命周期**：支持查询当前 [connectionState]、[closed] 与 [disconnectReason]，并可注册 [onStateChange]。
 */
interface LobbySession : AutoCloseable {

    override fun close() {}

    /**
     * 当前连接状态 (CONNECTING, CONNECTED, RECONNECTING, CLOSED)
     */
    val connectionState: ConnectionState get() = ConnectionState.CONNECTED

    /**
     * 连接是否已经关闭
     */
    val closed: Boolean get() = false

    /**
     * 断开连接原因（活跃时为 null）
     */
    val disconnectReason: String? get() = null

    /**
     * 注册连接状态变更监听器
     *
     * @param listener 接收最新 [ConnectionState] 的回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onStateChange(listener: (ConnectionState) -> Unit): AutoCloseable = AutoCloseable { }

    /**
     * 查询服务端公开的房间列表摘要
     */
    fun listRooms(): CompletableFuture<List<RoomSummaryView>>

    /**
     * 创建新房间并绑定当前客户端为房主座（seat 0）
     *
     * @param playerName 房主显示名，为 null 时采用默认 "Host"
     * @param config 客户端比赛规则配置，为 null 时采用服务端默认半庄配置
     * @return 初始完整视图快照 [GameSnapshotView]
     */
    fun createRoom(
        playerName: String? = null,
        config: MatchConfig? = null
    ): CompletableFuture<GameSnapshotView>

    /**
     * 加入指定房间并自动分配座位
     *
     * @param roomId 目标 8 位房间号
     * @param playerName 玩家显示名，为 null 时采用默认 "Player"
     * @param preferSeat 期望分配的座位 (0..3)，为 null 时由服务端自动分配
     * @return 加入成功后的初始视图快照 [GameSnapshotView]
     */
    fun joinRoom(
        roomId: String,
        playerName: String? = null,
        preferSeat: Int? = null
    ): CompletableFuture<GameSnapshotView>

    /**
     * 断线重连 / 会话恢复（重新绑定原席位）
     *
     * 校验 token 并换发新连接代次，原旧连接立即失效；托管期间已发生的动作不回滚。
     *
     * @param roomId 原房间号
     * @param token 原断线恢复令牌 resumeToken
     * @param cursor 客户端持有的同步游标，为 null 或 0 时拉取全量快照
     * @return 重新绑定后的当前全量视图快照 [GameSnapshotView]
     */
    fun resumeSession(
        roomId: String,
        token: String,
        cursor: Long? = null
    ): CompletableFuture<GameSnapshotView>

    /**
     * 加入指定房间观战
     *
     * @param roomId 目标 8 位房间号
     * @param spectatorName 观战者显示名，为 null 时采用服务端默认 "Spectator"
     * @return 观战加入成功后的初始全量公开快照基线 [PublicSnapshotView]
     */
    fun joinSpectator(
        roomId: String,
        spectatorName: String? = null
    ): CompletableFuture<PublicSnapshotView>

    /**
     * 断线恢复观战会话
     *
     * @param roomId 目标 8 位房间号
     * @param spectatorId 观战者标识
     * @param resumeToken 观战者断线恢复令牌
     * @param checkpoint 客户端当前持有的公开流检查点基线（可选）
     * @return 重新绑定后的当前全量公开快照基线 [PublicSnapshotView]
     */
    fun resumeSpectator(
        roomId: String,
        spectatorId: String,
        resumeToken: String,
        checkpoint: PublicCheckpoint? = null
    ): CompletableFuture<PublicSnapshotView>

    /**
     * 显式离开当前绑定的房间
     *
     * @return 操作回执（status = APPLIED 表示已成功离开）
     */
    fun leaveRoom(): CompletableFuture<Receipt>

    /**
     * 查询当前所在房间的完整状态信息
     */
    fun getRoomInfo(): CompletableFuture<RoomInfo>

    /**
     * 设置当前玩家在房间中的准备就绪状态
     *
     * @param ready 是否准备就绪
     * @return 操作回执
     */
    fun setReady(ready: Boolean): CompletableFuture<Receipt>

    /**
     * 房主更新房间配置（仅大厅期允许）
     *
     * @param config 新的房间规则与时钟配置
     * @return 操作回执
     */
    fun updateConfig(config: RoomConfig): CompletableFuture<Receipt>

    /**
     * 开始比赛（房主专属）
     *
     * @return 操作回执
     */
    fun start(): CompletableFuture<Receipt>

    /**
     * 注册房间状态变更监听器（成员加入/离开、准备就绪、规则变更、生命周期流转）
     *
     * @param listener 房间状态回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onRoomUpdate(listener: (RoomInfo) -> Unit): AutoCloseable
}
