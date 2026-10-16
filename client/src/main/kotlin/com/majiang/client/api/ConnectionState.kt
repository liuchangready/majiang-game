package com.majiang.client.api

/**
 * 客户端连接生命周期状态 (ConnectionState)
 *
 * 表达客户端统一运行时的连接与会话状态，供表现层与外部调用者观察，
 * 屏蔽单机（进程内绑定）与远端（TCP）等具体底层传输实现细节。
 */
enum class ConnectionState {
    /**
     * 正在尝试连接服务端、创建房间、加入房间或恢复会话
     */
    CONNECTING,

    /**
     * 连接已建立并处于正常活跃通信状态
     */
    CONNECTED,

    /**
     * 意外掉线或连接丢失，正在使用指数退避算法尝试自动重新连接与恢复会话
     */
    RECONNECTING,

    /**
     * 会话已关闭或终结（显式关闭、主动退出、被顶号、停服、房间关闭等）
     */
    CLOSED
}
