package com.majiang.transport.udp

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class FragmenterTest {

    @Test
    fun `payload below threshold is not fragmented`() {
        val scheduler = FakeClockAndScheduler()
        val fragmenter = Fragmenter(scheduler, scheduler)

        val payload = ByteArray(500) { (it % 100).toByte() }
        val originalFrame = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 1,
            seq = 10,
            payload = payload
        )

        val fragments = fragmenter.fragment(originalFrame)
        assertEquals(1, fragments.size)
        assertEquals(originalFrame, fragments[0])

        // 接收端原样直通
        val reassembled = fragmenter.reassemble(fragments[0])
        assertEquals(originalFrame, reassembled)
    }

    @Test
    fun `large payload fragmentation and reassembly roundtrip`() {
        val scheduler = FakeClockAndScheduler()
        val fragmenter = Fragmenter(scheduler, scheduler)

        // 构造 3500 字节的大载荷（如全量快照）
        val payload = ByteArray(3500) { (it % 256).toByte() }
        val originalFrame = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 888,
            seq = 100,
            payload = payload,
            flagZlib = true
        )

        var seqCounter = 200
        val fragments = fragmenter.fragment(originalFrame) { seqCounter++ }

        // 3500 / 1090 = 3 片 1090 + 1 片 230 = 4 片
        assertEquals(4, fragments.size)
        for (f in fragments) {
            assertEquals(UdpPacketType.FRAG, f.packetType)
            assertEquals(888, f.connectionId)
            assertTrue(f.payload.size <= Fragmenter.SUBHEADER_SIZE + 1090)
        }

        // 接收端依次输入前 3 片，应返回 null (未集齐)
        assertNull(fragmenter.reassemble(fragments[0]))
        assertNull(fragmenter.reassemble(fragments[1]))
        assertNull(fragmenter.reassemble(fragments[2]))
        assertEquals(1, fragmenter.getInFlightCount())

        // 输入第 4 片，集齐重组
        val reassembled = fragmenter.reassemble(fragments[3])
        assertNotNull(reassembled)
        assertEquals(UdpPacketType.PUSH, reassembled.packetType)
        assertEquals(888, reassembled.connectionId)
        assertEquals(true, reassembled.flagZlib)
        assertEquals(3500, reassembled.payload.size)
        assertTrue(originalFrame.payload.contentEquals(reassembled.payload))

        // 重组完成后释放缓冲
        assertEquals(0, fragmenter.getInFlightCount())
    }

    @Test
    fun `incomplete fragments are discarded and reported on 3s timeout`() {
        val scheduler = FakeClockAndScheduler(1000L)
        var timedOutMsgId: Int? = null
        val fragmenter = Fragmenter(
            clock = scheduler,
            scheduler = scheduler,
            timeoutMillis = 3000L,
            onTimeout = { timedOutMsgId = it }
        )

        val payload = ByteArray(2500) { 1 } // 3 片
        val frame = UdpFrame(packetType = UdpPacketType.PUSH, connectionId = 1, seq = 1, payload = payload)
        val fragments = fragmenter.fragment(frame)
        assertEquals(3, fragments.size)

        // 仅喂入片 0 和片 1，丢失片 2
        assertNull(fragmenter.reassemble(fragments[0]))
        assertNull(fragmenter.reassemble(fragments[1]))
        assertEquals(1, fragmenter.getInFlightCount())

        // 推进 2999ms，尚未超时
        scheduler.advanceTimeBy(2999L)
        assertEquals(1, fragmenter.getInFlightCount())
        assertNull(timedOutMsgId)

        // 推进 1ms (满 3000ms)，触发超时丢弃
        scheduler.advanceTimeBy(1L)
        assertEquals(0, fragmenter.getInFlightCount())
        assertNotNull(timedOutMsgId)

        // 迟到的片 2 到达时被安全丢弃，不发生崩溃
        assertNull(fragmenter.reassemble(fragments[2]))
    }

    @Test
    fun `concurrent in-flight reassembly capped at 8 messages`() {
        val scheduler = FakeClockAndScheduler()
        val fragmenter = Fragmenter(
            clock = scheduler,
            scheduler = scheduler,
            maxConcurrent = 8
        )

        // 同时启动 8 条不同的分片消息
        val messageFragments = mutableListOf<List<UdpFrame>>()
        for (i in 0 until 8) {
            val payload = ByteArray(2000) { i.toByte() }
            val frame = UdpFrame(packetType = UdpPacketType.PUSH, connectionId = i, seq = i, payload = payload)
            val frags = fragmenter.fragment(frame)
            messageFragments.add(frags)
            // 喂入每条消息的第 1 片，使其挂起在重组缓冲中
            fragmenter.reassemble(frags[0])
        }

        assertEquals(8, fragmenter.getInFlightCount())

        // 尝试注入第 9 条消息的首片
        val ninthPayload = ByteArray(2000) { 9 }
        val ninthFrame = UdpFrame(packetType = UdpPacketType.PUSH, connectionId = 9, seq = 9, payload = ninthPayload)
        val ninthFrags = fragmenter.fragment(ninthFrame)

        val rejected = fragmenter.reassemble(ninthFrags[0])
        assertNull(rejected)
        // 并发仍然保持上限 8，第 9 条无法挤占
        assertEquals(8, fragmenter.getInFlightCount())

        // 将第 1 条消息集齐 (喂入第 2 片)
        val completedFirst = fragmenter.reassemble(messageFragments[0][1])
        assertNotNull(completedFirst)
        assertEquals(7, fragmenter.getInFlightCount())

        // 此时腾出 1 个槽位，再次喂入第 9 条消息首片，成功接纳
        fragmenter.reassemble(ninthFrags[0])
        assertEquals(8, fragmenter.getInFlightCount())
    }

    @Test
    fun `unattached fragments are isolated by remote address`() {
        val scheduler = FakeClockAndScheduler()
        val fragmenter = Fragmenter(scheduler, scheduler)

        val addrA = java.net.InetSocketAddress("192.168.1.10", 5000)
        val addrB = java.net.InetSocketAddress("192.168.1.20", 5000)

        val payload = ByteArray(2500) { 1 }
        val frame = UdpFrame(packetType = UdpPacketType.REQ, connectionId = 0, seq = 1, payload = payload)
        val fragments = fragmenter.fragment(frame)

        // addrA 注入片 0
        assertNull(fragmenter.reassemble(fragments[0], addrA))
        assertEquals(1, fragmenter.getInFlightCount())

        // addrB 尝试注入片 1（即便 msgId 相同，由于来自不同未绑定地址，不会混淆到 addrA 的缓冲区）
        assertNull(fragmenter.reassemble(fragments[1], addrB))
        assertEquals(2, fragmenter.getInFlightCount())

        // addrA 注入片 1 和片 2，成功重组
        assertNull(fragmenter.reassemble(fragments[1], addrA))
        val reassembledA = fragmenter.reassemble(fragments[2], addrA)
        assertNotNull(reassembledA)
        assertEquals(1, fragmenter.getInFlightCount())
    }
}
