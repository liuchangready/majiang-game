package com.majiang.review

import com.majiang.server.tcp.TcpServerConfig
import com.majiang.server.tcp.TcpTableServer
import com.majiang.transport.tcp.TcpFrameDecoder
import com.majiang.transport.tcp.TcpFrameEncoder
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.Socket
import java.nio.ByteBuffer

/** Real loopback proof that partial decoder payload counts against the server-wide byte budget. */
@Timeout(15)
class TcpBudgetReviewTest {
    @Test fun `partial inbound frame reserves global bytes before payload allocation`() {
        TcpTableServer(TcpServerConfig(port = 0)).use { server ->
            server.start()
            Socket("127.0.0.1", server.boundPort).use { socket ->
                socket.soTimeout = 3000
                val hello = TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION))
                socket.getOutputStream().write(TcpFrameEncoder.encode(hello.type, hello.payload, hello.flags))
                val decoder = TcpFrameDecoder()
                val read = ByteArray(4096)
                while (true) {
                    val n = socket.getInputStream().read(read)
                    assertTrue(n > 0)
                    if (decoder.decode(ByteBuffer.wrap(read, 0, n)).isNotEmpty()) break
                }
                val frame = TcpFrameEncoder.encode(TcpWire.FRAME_TYPE_REQ, ByteArray(32768))
                socket.getOutputStream().write(frame, 0, 12 + 4096)
                socket.getOutputStream().flush()
                val deadline = System.nanoTime() + java.util.concurrent.TimeUnit.SECONDS.toNanos(2)
                var accounted = false
                while (System.nanoTime() < deadline) {
                    if (server.globalBudget.currentAllocatedBytes >= 4096L) {
                        accounted = true
                        break
                    }
                    Thread.sleep(5)
                }
                assertTrue(accounted, "Decoder-held partial payload must be globally budgeted before allocation")
            }
        }
    }
}
