package com.majiang.server.api

import com.majiang.transport.api.ProtocolPlayerUpdate

/**
 * 服务端主动推送通道 (PushChannel)
 *
 * 负责从服务端向客户端单向派发对局状态演进通知与连接生命周期事件。
 */
interface PushChannel {

    /**
     * 注册对局状态更新监听器
     *
     * 当对局产生动作生效、时钟推进或单局结算时，服务端下发最新的 [ProtocolPlayerUpdate]
     * （包含全量快照自愈视图、前置游标与脱敏可见事件）。
     *
     * @param listener 消费状态更新的回调函数
     * @return 用于注销该监听器的 [AutoCloseable] 资源柄
     */
    fun onUpdate(listener: (ProtocolPlayerUpdate) -> Unit): AutoCloseable

    /**
     * 注册连接断开 / 终结通知监听器
     *
     * 当连接因顶号（KICK）、房间关闭、服务端停机或网络心跳超时而终结时触发。
     *
     * @param listener 接收稳定终结原因码的回调函数（如 DISPLACED_BY_NEW_CONNECTION、ROOM_CLOSED 等）
     * @return 用于注销该监听器的 [AutoCloseable] 资源柄
     */
    fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable
}
