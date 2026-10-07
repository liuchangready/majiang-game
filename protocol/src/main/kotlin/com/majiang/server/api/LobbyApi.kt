package com.majiang.server.api

import com.majiang.transport.api.ProtocolActionReceipt
import java.util.concurrent.CompletableFuture

/**
 * 大厅期异步服务接口 (LobbyApi)
 *
 * 提供未绑定座位的连接可用的房间生命周期操作。
 *
 * 核心契约：
 * 1. **全异步契约**：所有方法一律返回 [CompletableFuture]，调用方 UI / 渲染线程零阻塞；
 * 2. **合法回执模型**：业务层面的拒绝（如房间人满、非法状态等）作为带有 REJECTED 状态的合法回执返回，
 *    仅在底层传输通道损坏或响应反序列化失败等异常故障时，Future 才异常完成；
 * 3. **绑定前可用**：客户端连接建立后但在获得座位绑定前即可调用大厅接口；绑定成功后连接进入对局期；
 * 4. **显式离开语义**：[leaveRoom] 与底层连接断开的差异在于显式语义——房主离开将中止比赛并保留已完成局记录，
 *    普通成员离开后座位交由 AI 托管；离开后该连接恢复为未绑定状态，可继续建房或加入其他房间。
 */
interface LobbyApi {

    /**
     * 查询当前服务端可见的公开房间列表摘要
     *
     * @return 包含所有房间摘要列表的异步凭据
     */
    fun listRooms(): CompletableFuture<List<RoomSummary>>

    /**
     * 创建新房间并自动将当前连接绑定为房主座（seat 0）
     *
     * @param req 创建房间请求参数（含房主名、比赛规则配置、协议版本）
     * @return 绑定成功凭据 [RoomBinding]（包含分配的 roomId、seat 0、resumeToken 与初始快照）
     */
    fun createRoom(req: CreateRoomRequest): CompletableFuture<RoomBinding>

    /**
     * 加入指定房间并分配空闲座位
     *
     * @param req 加入房间请求参数（含目标 roomId、玩家名、期望座位）
     * @return 绑定成功凭据 [RoomBinding]（包含分配的 seat、resumeToken 与初始快照）
     */
    fun joinRoom(req: JoinRoomRequest): CompletableFuture<RoomBinding>

    /**
     * 断线重连 / 会话恢复（重新绑定原座位）
     *
     * 校验 token 并换发新连接代次，原旧连接立即被注销（旧连接将收到 KICK 终结通知并终止挂起请求）；
     * 托管期间已发生的动作不回滚。
     *
     * @param req 恢复请求参数（含原 roomId、原 resumeToken 及最后持有游标）
     * @return 重新绑定凭据 [RoomBinding]（包含重连时刻的全量视图快照）
     */
    fun resumeSession(req: ResumeRequest): CompletableFuture<RoomBinding>

    /**
     * 显式离开当前绑定的房间
     *
     * 房主离开时中止比赛并保留已结算记录；普通成员离开后席位交由 AI 接管。
     * 离开后当前连接恢复为未绑定状态，可重新建房或加入。
     *
     * @return 操作回执（APPLIED 表示成功脱离房间）
     */
    fun leaveRoom(): CompletableFuture<ProtocolActionReceipt>

    /**
     * 查询当前所在房间的完整状态信息
     */
    fun getRoomInfo(): CompletableFuture<com.majiang.transport.api.ProtocolRoomInfo>

    /**
     * 设置当前玩家在房间中的准备就绪状态
     *
     * @param ready 是否准备就绪
     * @return 操作回执
     */
    fun setReady(ready: Boolean): CompletableFuture<ProtocolActionReceipt>

    /**
     * 房主更新房间配置（仅大厅期允许）
     *
     * @param config 新的房间规则与时钟配置
     * @return 操作回执
     */
    fun updateConfig(config: com.majiang.transport.api.ProtocolRoomConfig): CompletableFuture<ProtocolActionReceipt>

    /**
     * 注册房间状态变更监听器（成员加入/离开、准备就绪、规则变更、生命周期流转）
     *
     * @param listener 房间状态回调
     * @return 用于注销该监听器的资源句柄
     */
    fun onRoomUpdate(listener: (com.majiang.transport.api.ProtocolRoomInfo) -> Unit): AutoCloseable
}
