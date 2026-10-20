package com.majiang.client.transport.tcp

import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrameDecoder
import com.majiang.transport.tcp.TcpFrameEncoder
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpLimits
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpProtocolException
import com.majiang.transport.tcp.TcpScope
import com.majiang.transport.tcp.TcpWelcome
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.ServerSocket
import java.nio.ByteBuffer
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit

/**
 * v5 双端业务版本拒绝：客户端必须拒绝旧服务端 WELCOME（businessVersion = 1），
 * 明确失败为 VERSION_MISMATCH，不静默降级或继续解析 v4 快照。
 */
class TcpBusinessVersionHandshakeTest {

    @Test
    @Timeout(20)
    fun `client rejects legacy business version 1 WELCOME with VERSION_MISMATCH`() {
        val server = ServerSocket(0)
        val serverThread = Thread {
            try {
                val socket = server.accept()
                socket.soTimeout = 5000
                val decoder = TcpFrameDecoder()
                val buffer = ByteArray(4096)
                var hello: TcpHello? = null
                while (hello == null) {
                    val read = socket.getInputStream().read(buffer)
                    if (read < 0) break
                    val frames = decoder.decode(ByteBuffer.wrap(buffer, 0, read))
                    if (frames.isNotEmpty()) {
                        hello = TcpMessageCodec.parseHello(frames.first())
                    }
                }
                assertNotNull(hello)
                assertEquals(TcpWire.BUSINESS_VERSION, hello!!.businessVersion)
                assertEquals(3, hello!!.businessVersion)

                val legacyWelcome = TcpWelcome(
                    wireVersion = TcpWire.WIRE_VERSION,
                    businessVersion = 1,
                    connectionEpoch = "legacy-epoch",
                    lobbyResumeToken = "legacy-token",
                    lobbyScope = TcpScope("legacy-scope", Long.MAX_VALUE),
                    limits = TcpLimits(requestTimeoutMillis = 1500L)
                )
                val bytes = TcpFrameEncoder.encode(
                    TcpWire.FRAME_TYPE_WELCOME,
                    TcpMessageCodec.toFrame(legacyWelcome).payload
                )
                socket.getOutputStream().write(bytes)
                socket.getOutputStream().flush()
                Thread.sleep(500)
                try {
                    socket.close()
                } catch (_: Throwable) {
                }
            } catch (_: Throwable) {
            }
        }.apply {
            isDaemon = true
            start()
        }

        try {
            val binding = TcpClientBinding("127.0.0.1", server.localPort)
            try {
                val failure = try {
                    binding.connectInitial().get(5, TimeUnit.SECONDS)
                    null
                } catch (e: ExecutionException) {
                    e.cause
                }
                assertNotNull(failure, "legacy server WELCOME must not be accepted")
                assertTrue(failure is TcpProtocolException, "expected TcpProtocolException, got $failure")
                assertEquals(TcpCloseReasons.VERSION_MISMATCH, (failure as TcpProtocolException).reasonCode)
                assertTrue(failure.message.orEmpty().contains("Version mismatch"), failure.message.orEmpty())
            } finally {
                binding.close()
            }
        } finally {
            try {
                server.close()
            } catch (_: Throwable) {
            }
            serverThread.join(1000)
        }
    }
}
