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
     * @param scheduler 注入的异步调度器实现（默认单线程守护调度器）
     * @return 包含已建立网络监听的 [UdpClientBinding] 异步凭据
     */
    fun connect(
        host: String,
        port: Int,
        clock: UdpClock = UdpClock.SYSTEM,
        scheduler: UdpScheduler = ScheduledExecutorUdpScheduler(),
        closeSchedulerOnShutdown: Boolean = false
    ): CompletableFuture<UdpClientBinding> {
        val future = CompletableFuture<UdpClientBinding>()
        var socket: DatagramSocket? = null
        try {
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
                    if (closeSchedulerOnShutdown && scheduler is AutoCloseable) {
                        try {
                            scheduler.close()
                        } catch (_: Exception) {}
                    }
                }
            }

            binding = UdpClientBinding(
                rawSender = rawSender,
                clock = clock,
                scheduler = scheduler,
                onCloseCallback = {
                    try {
                        scheduler.schedule(2000L) {
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
            if (closeSchedulerOnShutdown && scheduler is AutoCloseable) {
                try {
                    scheduler.close()
                } catch (_: Exception) {}
            }
            future.completeExceptionally(e)
        }
        return future
    }
}
