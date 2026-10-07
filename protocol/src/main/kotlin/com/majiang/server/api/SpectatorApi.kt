package com.majiang.server.api

import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import java.util.concurrent.CompletableFuture

/**
 * 独立观战期异步服务接口 (SpectatorApi)
 *
 * 面向观战身份的连接，提供公开信息流查询与推送订阅。
 *
 * 核心契约：
 * 1. **全异步契约**：所有查询与接入均返回 [CompletableFuture]，无锁非阻塞；
 * 2. **独立公开流**：不属于四位玩家中的任何一位，不分发私有暗牌、手牌决策或千术信息；
 * 3. **全量基线保障**：[joinSpectator] / [resumeSpectator] 的 binding.snapshot 以及
 *    [publicSnapshot] / [resumePublic] 返回值均为权威全量基线；
 * 4. **生命周期兼容**：leaveRoom / getRoomInfo / onRoomUpdate / onDisconnect 均由组合连接支持。
 */
interface SpectatorApi {

    /**
     * 加入指定房间观战
     *
     * @param req 观战请求（含目标 roomId、观战者显示名、协议版本）
     * @return 观战绑定凭据 [SpectatorBinding]（含分配的 spectatorId、resumeToken 与初始全量公开快照基线）
     */
    fun joinSpectator(req: JoinSpectatorRequest): CompletableFuture<SpectatorBinding>

    /**
     * 断线恢复观战会话
     *
     * @param req 观战恢复请求（含目标 roomId、spectatorId、resumeToken 及最后公开检查点基线）
     * @return 重新绑定凭据 [SpectatorBinding]（含重连时刻的全量公开快照基线）
     */
    fun resumeSpectator(req: ResumeSpectatorRequest): CompletableFuture<SpectatorBinding>

    /**
     * 拉取当前房间最新的权威全量公开快照
     *
     * @return 全量公开快照凭据 [ProtocolPublicSnapshot]
     */
    fun publicSnapshot(): CompletableFuture<ProtocolPublicSnapshot>

    /**
     * 基于指定公开检查点对账或恢复全量公开快照
     *
     * @param checkpoint 客户端当前持有的完整公开检查点 (sessionId, syncEpoch, cursor)
     * @return 权威全量公开快照凭据 [ProtocolPublicSnapshot]
     */
    fun resumePublic(checkpoint: PublicCheckpoint): CompletableFuture<ProtocolPublicSnapshot>

    /**
     * 查询指定单局的公开结算事件
     *
     * @param roundId 目标局标识
     * @return 仅包含公开事件（白名单放行）的结算事件列表凭据
     */
    fun queryPublicRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>>

    /**
     * 注册公开状态更新推送监听器
     *
     * @param listener 消费公开更新的回调函数
     * @return 用于注销该监听器的 [AutoCloseable] 资源句柄
     */
    fun onPublicUpdate(listener: (ProtocolPublicUpdate) -> Unit): AutoCloseable
}
