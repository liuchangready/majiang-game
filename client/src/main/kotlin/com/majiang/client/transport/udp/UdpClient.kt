package com.majiang.client.transport.udp

import com.majiang.transport.udp.UdpClock
import com.majiang.transport.udp.UdpProtocol
import com.majiang.transport.udp.UdpScheduler
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.util.concurrent.CompletableFuture

/**
 * UDP 客户端连接入口 (UdpClient)
 *
 * 负责通过标准 Java UDP [DatagramSocket] 异步建立与服务端 [UdpBinding] 的网络连接，
 * 并完成帧编解码线程与 [UdpClientBinding] 的装配。
 */
object UdpClient {

    /**
     * 连接远端 UDP 服务端并返回绑定对象
     *
     * @param host 远端服务器主机名或 IP 地址
     * @param port 远端服务器 UDP 监听端口
     * @param clock 注入的协议时钟实现（默认系统时钟）
     * @param scheduler 注入的异步调度器实现（为空时内部创建 ScheduledExecutorUdpScheduler 并由当前连接拥有与释放）
     * @param closeSchedulerOnShutdown 仅在注入外部调度器时生效：是否在连接关闭/失败时释放该调度器（默认 false，由调用方保留所有权；显式 true 时转交所有权由连接释放）。内部创建的调度器始终由连接自动释放，不受此参数影响。
     * @return 包含已建立网络监听的 [UdpClientBinding] 异步凭据
     */
    fun connect(
        host: String,
        port: Int,
        clock: UdpClock = UdpClock.SYSTEM,
        scheduler: UdpScheduler? = null,
        closeSchedulerOnShutdown: Boolean = false
    ): CompletableFuture<UdpClientBinding> {
        return connectInternal(
            host = host,
            port = port,
            clock = clock,
            scheduler = scheduler,
            closeSchedulerOnShutdown = closeSchedulerOnShutdown,
            schedulerFactory = { ScheduledExecutorUdpScheduler() }
        )
    }

    /**
     * 内部工厂重载：仅供测试与模块内部在调用级别注入构造调度器的工厂，用于失败路径资源观测，不向公共 API 暴露
     */
    internal fun connect(
        host: String,
        port: Int,
        clock: UdpClock = UdpClock.SYSTEM,
        scheduler: UdpScheduler? = null,
        closeSchedulerOnShutdown: Boolean = false,
        schedulerFactory: () -> UdpScheduler
    ): CompletableFuture<UdpClientBinding> {
        return connectInternal(
            host = host,
            port = port,
            clock = clock,
            scheduler = scheduler,
            closeSchedulerOnShutdown = closeSchedulerOnShutdown,
            schedulerFactory = schedulerFactory
        )
    }

    internal fun connectInternal(
        host: String,
        port: Int,
        clock: UdpClock = UdpClock.SYSTEM,
        scheduler: UdpScheduler? = null,
        closeSchedulerOnShutdown: Boolean = false,
        schedulerFactory: () -> UdpScheduler = { ScheduledExecutorUdpScheduler() }
    ): CompletableFuture<UdpClientBinding> {
        val future = CompletableFuture<UdpClientBinding>()
        val shouldCloseScheduler = if (scheduler == null) true else closeSchedulerOnShutdown
        var effectiveScheduler: UdpScheduler? = scheduler
        var socket: DatagramSocket? = null
        try {
            val activeScheduler = effectiveScheduler ?: schedulerFactory().also { effectiveScheduler = it }
            val sock = DatagramSocket()
            socket = sock
            if (port !in 1..65535) {
                throw IllegalArgumentException("Port out of range: $port")
            }
            val inetAddress = java.net.InetAddress.getByName(host)
            val remoteAddress = InetSocketAddress(inetAddress, port)
            if (remoteAddress.isUnresolved) {
                throw java.net.UnknownHostException("Cannot resolve host: $host")
            }
            sock.connect(remoteAddress)

            val rawSender: (ByteArray) -> Unit = { bytes ->
                if (!sock.isClosed) {
                    val packet = DatagramPacket(bytes, bytes.size, remoteAddress)
                    sock.send(packet)
                }
            }

            lateinit var binding: UdpClientBinding

            val receiverThread = Thread({
                val buffer = ByteArray(UdpProtocol.MAX_DATAGRAM_SIZE)
                while (!sock.isClosed) {
                    try {
                        val packet = DatagramPacket(buffer, buffer.size)
                        sock.receive(packet)
                        val length = packet.length
                        if (length in UdpProtocol.HEADER_SIZE..UdpProtocol.MAX_DATAGRAM_SIZE) {
                            val data = ByteArray(length)
                            System.arraycopy(packet.data, packet.offset, data, 0, length)
                            binding.onInboundDatagram(data)
                        }
                    } catch (_: Exception) {
                        if (sock.isClosed) break
                    }
                }
            }, "udp-client-receiver")
            receiverThread.isDaemon = true

            val socketClosed = java.util.concurrent.atomic.AtomicBoolean(false)
            val closeSocket: () -> Unit = {
                if (socketClosed.compareAndSet(false, true)) {
                    try {
                        sock.close()
                    } catch (_: Exception) {}
                    val schedulerToClose = activeScheduler
                    if (shouldCloseScheduler && schedulerToClose is AutoCloseable) {
                        try {
                            schedulerToClose.close()
                        } catch (_: Exception) {}
                    }
                }
            }

            binding = UdpClientBinding(
                rawSender = rawSender,
                clock = clock,
                scheduler = activeScheduler,
                onCloseCallback = {
                    try {
                        activeScheduler.schedule(2000L) {
                            closeSocket()
                        }
                    } catch (_: Exception) {}

                    try {
                        CompletableFuture.delayedExecutor(2000, java.util.concurrent.TimeUnit.MILLISECONDS).execute {
                            closeSocket()
                        }
                    } catch (_: Exception) {
                        closeSocket()
                    }
                }
            )

            receiverThread.start()
            binding.startHeartbeat()
            socket = null
            future.complete(binding)
        } catch (e: Exception) {
            try {
                socket?.close()
            } catch (_: Exception) {}
            val schedulerToClose = effectiveScheduler
            if (shouldCloseScheduler && schedulerToClose is AutoCloseable) {
                try {
                    schedulerToClose.close()
                } catch (_: Exception) {}
            }
            future.completeExceptionally(e)
        }
        return future
    }
}
