package com.majiang.server.api

/**
 * 统一服务端连接入口 (ServerConnection)
 *
 * 组合大厅期服务 [LobbyApi]、对局期服务 [SessionApi]、观战服务 [SpectatorApi] 与推送通道 [PushChannel]，
 * 并提供生命周期状态查询与幂等释放能力。
 *
 * 单机（进程内绑定）与联机（远端网络绑定）均实现此接口，客户端上层无需感知部署差异。
 */
interface ServerConnection : LobbyApi, SessionApi, PushChannel, SpectatorApi, AutoCloseable {

    /**
     * 当前连接是否已经关闭或终结
     *
     * 显式调用 [close]、收到服务端断连推送或检测到心跳超时后为 true。
     */
    val closed: Boolean

    /**
     * 连接断开或终结的稳定原因说明
     *
     * 连接仍处于正常活跃状态时为 null；终结后保存稳定原因码
     * （例如 DISPLACED_BY_NEW_CONNECTION、ROOM_CLOSED、LOCAL_CLOSED 等）。
     */
    val disconnectReason: String?

    /**
     * 幂等释放连接资源
     *
     * 关闭后后续请求将立刻被拒绝或以异常终结，不会向对端产生额外网络流量。
     */
    override fun close()
}
