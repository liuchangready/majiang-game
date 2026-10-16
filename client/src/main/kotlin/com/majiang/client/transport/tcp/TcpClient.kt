package com.majiang.client.transport.tcp

import com.majiang.transport.common.TransportClock
import com.majiang.transport.common.TransportScheduler
import java.util.concurrent.CompletableFuture

/**
 * TCP 客户端连接工厂 (TcpClient)
 *
 * 负责建立底层 TCP 连接、完成 HELLO/WELCOME 握手，并返回准备就绪的 [TcpClientBinding]。
 */
object TcpClient {

    /**
     * 建立 TCP 连接并返回就绪绑定 (非阻塞，异步完成)
     *
     * @param host 远端服务器主机名或 IP
     * @param port 远端服务器端口
     * @param lobbyResumeToken 可选的大厅恢复凭据（重连时传递）
     * @param clock 注入的传输时钟实现
     * @param scheduler 注入的传输调度器实现
     * @return 就绪的 [TcpClientBinding] 异步凭据
     */
    fun connect(
        host: String,
        port: Int,
        lobbyResumeToken: String? = null,
        clock: TransportClock = TransportClock.SYSTEM,
        scheduler: TransportScheduler? = null
    ): CompletableFuture<TcpClientBinding> {
        val resolvedScheduler = scheduler ?: ScheduledExecutorTransportScheduler()
        val ownsScheduler = (scheduler == null)
        val binding = TcpClientBinding(
            host = host,
            port = port,
            clock = clock,
            scheduler = resolvedScheduler,
            ownsScheduler = ownsScheduler,
            initialLobbyResumeToken = lobbyResumeToken
        )
        return binding.connectInitial().whenComplete { _, err ->
            if (err != null) {
                binding.close()
            }
        }
    }
}
