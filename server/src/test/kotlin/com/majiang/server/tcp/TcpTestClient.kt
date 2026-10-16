package com.majiang.server.tcp

import com.majiang.transport.tcp.TcpClose
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpFrameDecoder
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpPong
import com.majiang.transport.tcp.TcpResponseEnvelope
import com.majiang.transport.tcp.TcpWelcome
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.util.ArrayDeque

/**
 * TCP 单测客户端测试工具 (TcpTestClient)
 *
 * 包装真实底层 TCP Socket，提供收发原始字节、单帧、断言特定消息及超时探测能力。
 */
class TcpTestClient(
    val host: String = "127.0.0.1",
    val port: Int
) : AutoCloseable {

    private val socket: Socket = Socket()
    private val decoder: TcpFrameDecoder = TcpFrameDecoder()
    private val frameQueue = ArrayDeque<TcpFrame>()

    init {
        socket.tcpNoDelay = true
        socket.soTimeout = 5000
        socket.connect(InetSocketAddress(host, port), 5000)
    }

    val isConnected: Boolean get() = socket.isConnected && !socket.isClosed

    val inputStream: InputStream get() = socket.getInputStream()
    val outputStream: OutputStream get() = socket.getOutputStream()

    /**
     * 发送原始字节流
     */
    fun sendRaw(bytes: ByteArray) {
        outputStream.write(bytes)
        outputStream.flush()
    }

    /**
     * 发送完整的 TcpFrame
     */
    fun sendFrame(frame: TcpFrame) {
        sendRaw(frame.toByteArray())
    }

    /**
     * 阻塞读取直到解码出至少一个 TcpFrame
     */
    fun receiveFrame(timeoutMillis: Long = 5000L): TcpFrame {
        if (frameQueue.isNotEmpty()) {
            return frameQueue.poll()
        }

        val deadline = System.currentTimeMillis() + timeoutMillis
        val buf = ByteArray(8192)

        while (System.currentTimeMillis() < deadline) {
            val available = inputStream.available()
            val toRead = if (available > 0) available.coerceAtMost(buf.size) else buf.size
            socket.soTimeout = (deadline - System.currentTimeMillis()).coerceAtLeast(10L).toInt()

            val read = try {
                inputStream.read(buf, 0, toRead)
            } catch (e: Exception) {
                if (System.currentTimeMillis() >= deadline) {
                    throw IllegalStateException("Timeout waiting for TcpFrame after $timeoutMillis ms", e)
                }
                continue
            }

            if (read == -1) {
                try {
                    decoder.onEndOfStream()
                } catch (e: Throwable) {
                    throw IllegalStateException("Stream ended with incomplete frame", e)
                }
                throw IllegalStateException("EOF reached before receiving frame")
            }

            val frames = decoder.decode(ByteBuffer.wrap(buf, 0, read))
            if (frames.isNotEmpty()) {
                frameQueue.addAll(frames)
                return frameQueue.poll()
            }
        }

        throw IllegalStateException("Timeout waiting for TcpFrame after $timeoutMillis ms")
    }

    fun hasFrameAvailable(): Boolean =
        frameQueue.isNotEmpty() || (try { inputStream.available() > 0 } catch (_: Throwable) { false })

    /**
     * 阻塞收取一条 WELCOME 帧
     */
    fun expectWelcome(timeoutMillis: Long = 5000L): TcpWelcome {
        val deadline = System.currentTimeMillis() + timeoutMillis
        while (System.currentTimeMillis() < deadline) {
            val frame = receiveFrame((deadline - System.currentTimeMillis()).coerceAtLeast(10L))
            if (frame.type == com.majiang.transport.tcp.TcpWire.FRAME_TYPE_WELCOME) {
                return TcpMessageCodec.parseWelcome(frame)
            }
        }
        throw IllegalStateException("Timeout waiting for WELCOME frame after $timeoutMillis ms")
    }

    /**
     * 阻塞收取一条 PONG 帧
     */
    fun expectPong(timeoutMillis: Long = 5000L): TcpPong {
        val deadline = System.currentTimeMillis() + timeoutMillis
        while (System.currentTimeMillis() < deadline) {
            val frame = receiveFrame((deadline - System.currentTimeMillis()).coerceAtLeast(10L))
            if (frame.type == com.majiang.transport.tcp.TcpWire.FRAME_TYPE_PONG) {
                return TcpMessageCodec.parsePong(frame)
            }
        }
        throw IllegalStateException("Timeout waiting for PONG frame after $timeoutMillis ms")
    }

    /**
     * 阻塞收取一条 CLOSE 帧 (自动跳过中间推送更新)
     */
    fun expectClose(timeoutMillis: Long = 5000L): TcpClose {
        val deadline = System.currentTimeMillis() + timeoutMillis
        while (System.currentTimeMillis() < deadline) {
            val frame = receiveFrame((deadline - System.currentTimeMillis()).coerceAtLeast(10L))
            if (frame.type == com.majiang.transport.tcp.TcpWire.FRAME_TYPE_CLOSE) {
                return TcpMessageCodec.parseClose(frame)
            }
        }
        throw IllegalStateException("Timeout waiting for CLOSE frame after $timeoutMillis ms")
    }

    /**
     * 阻塞收取一条 RSP 信封帧 (自动跳过中间推送更新)
     */
    fun expectResponse(timeoutMillis: Long = 5000L): TcpResponseEnvelope {
        val deadline = System.currentTimeMillis() + timeoutMillis
        while (System.currentTimeMillis() < deadline) {
            val frame = receiveFrame((deadline - System.currentTimeMillis()).coerceAtLeast(10L))
            if (frame.type == com.majiang.transport.tcp.TcpWire.FRAME_TYPE_RSP) {
                return TcpMessageCodec.parseResponseEnvelope(frame)
            }
        }
        throw IllegalStateException("Timeout waiting for RSP frame after $timeoutMillis ms")
    }

    override fun close() {
        try {
            socket.close()
        } catch (_: Throwable) {
        }
    }
}
