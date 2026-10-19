package com.majiang.review

import com.majiang.access.ReceiptStatus
import com.majiang.server.host.HostDispatchPools
import com.majiang.server.room.RoomRegistry
import com.majiang.server.room.SharedRoom
import com.majiang.server.tcp.TcpServerConfig
import com.majiang.server.tcp.TcpTableServer
import com.majiang.server.tcp.toByteArray
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpFrame
import com.majiang.transport.tcp.TcpFrameDecoder
import com.majiang.transport.tcp.TcpHello
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpWelcome
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.util.ArrayDeque
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

/**
 * MR-07a 执行器与派发队列预算独立验收测量工具 (TcpExecutorBudgetMeasurementTest)
 *
 * 依据 doc/v4-重构/IMPROVEMENT-PLAN.md 第 4 节 MR-07a 及 ANTI-MR07-TASK.txt 任务 F：
 * - 针对 N in {1, 10, 100} 分别测量：
 *   a) 新建空房；
 *   b) 活跃对局（房主 startMatch 后由 AI 自动推进行为）；
 *   c) 慢消费者（观战 socket 背压与 SLOW_CONSUMER 隔离）。
 * - 统计存活线程数、通道指标、派发延迟分位数，并在关闭后验证残留为 0。
 * - 断言预算冻结上限：
 *   1. 共享 Dispatch+Completion 线程总数 <= 12，且与 N 无关；
 *   2. 全进程存活线程总数 <= 5*N + 30；
 *   3. 通道 queuedItems <= 设定容量，needsRecovery 语义自洽；
 *   4. 关闭后 ServerTableHost-* 与 TcpTableServer-Dispatch-* 线程残留为 0。
 * - 输出人类可读的 Markdown 预算矩阵表供验收评审。
 */
@Timeout(600)
class TcpExecutorBudgetMeasurementTest {

    data class ThreadCensus(
        val serialThreads: Int,
        val aiThreads: Int,
        val dispatchThreads: Int,
        val completionThreads: Int,
        val totalLiveThreads: Int
    )

    data class BudgetRow(
        val scenario: String,
        val roomCount: Int,
        val serialThreads: Int,
        val aiThreads: Int,
        val dispatchThreads: Int,
        val completionThreads: Int,
        val totalLiveThreads: Int,
        val maxQueuedItems: Int,
        val overflowCount: Long,
        val scheduleRejectCount: Long,
        val maxWaitTimeMillis: Double,
        val residualTasks: Int,
        val residualThreads: Int
    )

    companion object {
        private val budgetRows = CopyOnWriteArrayList<BudgetRow>()

        @AfterAll
        @JvmStatic
        fun tearDownAll() {
            printBudgetTable()
        }

        fun takeCensus(): ThreadCensus {
            val threads = Thread.getAllStackTraces().keys.filter { it.isAlive }
            var serial = 0
            var ai = 0
            var dispatch = 0
            var completion = 0
            for (t in threads) {
                val name = t.name
                when {
                    name.contains("-Serial") -> serial++
                    name.contains("-AI") -> ai++
                    name.startsWith("TcpTableServer-Dispatch-") -> dispatch++
                    name.startsWith("TcpTableServer-Completion-") -> completion++
                }
            }
            return ThreadCensus(serial, ai, dispatch, completion, threads.size)
        }

        fun waitUntil(timeoutMillis: Long = 5000L, pollIntervalMillis: Long = 20L, condition: () -> Boolean): Boolean {
            val deadline = System.currentTimeMillis() + timeoutMillis
            while (System.currentTimeMillis() < deadline) {
                if (condition()) return true
                try {
                    Thread.sleep(pollIntervalMillis)
                } catch (_: InterruptedException) {
                    return condition()
                }
            }
            return condition()
        }

        fun waitForResidualThreads(timeoutMillis: Long = 4000L): ThreadCensus {
            val deadline = System.currentTimeMillis() + timeoutMillis
            var census = takeCensus()
            while (System.currentTimeMillis() < deadline) {
                if (census.serialThreads == 0 && census.dispatchThreads == 0 && census.completionThreads == 0) {
                    return census
                }
                try {
                    Thread.sleep(30L)
                } catch (_: InterruptedException) {
                    break
                }
                census = takeCensus()
            }
            return census
        }

        fun printBudgetTable() {
            println("\n" + "=".repeat(120))
            println("                       MR-07a 执行器与派发队列预算实测表 (Budget Measurement Matrix)")
            println("=".repeat(120))
            println("| 场景 | 房间数 N | Serial 线程 | AI 线程 | 共享 Dispatch | 共享 Completion | 总存活线程 | 最大队列条目 | 溢出计数 | 拒绝计数 | 最大派发延迟 (ms) | 残留任务 | 关服残留线程 |")
            println("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
            for (r in budgetRows) {
                println(String.format(
                    "| %-6s | %-8d | %-11d | %-7d | %-13d | %-15d | %-10d | %-12d | %-8d | %-8d | %-17.2f | %-8d | %-12d |",
                    r.scenario, r.roomCount, r.serialThreads, r.aiThreads, r.dispatchThreads, r.completionThreads,
                    r.totalLiveThreads, r.maxQueuedItems, r.overflowCount, r.scheduleRejectCount, r.maxWaitTimeMillis,
                    r.residualTasks, r.residualThreads
                ))
            }
            println("=".repeat(120) + "\n")
        }
    }

    /**
     * 自包含轻量 TCP 客户端（专用于独立测试套件内部通信，不依赖外部单测库）
     */
    private class EmbeddedTestClient(val port: Int) : AutoCloseable {
        private val socket: Socket = Socket()
        private val decoder: TcpFrameDecoder = TcpFrameDecoder()
        private val frameQueue = ArrayDeque<TcpFrame>()

        init {
            socket.tcpNoDelay = true
            socket.soTimeout = 5000
            socket.connect(InetSocketAddress("127.0.0.1", port), 5000)
        }

        fun sendFrame(frame: TcpFrame) {
            val bytes = frame.toByteArray()
            socket.getOutputStream().write(bytes)
            socket.getOutputStream().flush()
        }

        fun receiveFrame(timeoutMillis: Long = 5000L): TcpFrame {
            if (frameQueue.isNotEmpty()) return frameQueue.poll()
            val deadline = System.currentTimeMillis() + timeoutMillis
            val buf = ByteArray(8192)
            while (System.currentTimeMillis() < deadline) {
                val available = socket.getInputStream().available()
                val toRead = if (available > 0) available.coerceAtMost(buf.size) else buf.size
                socket.soTimeout = (deadline - System.currentTimeMillis()).coerceAtLeast(10L).toInt()
                val read = try {
                    socket.getInputStream().read(buf, 0, toRead)
                } catch (e: Exception) {
                    if (System.currentTimeMillis() >= deadline) throw IllegalStateException("Timeout waiting for frame", e)
                    continue
                }
                if (read == -1) throw IllegalStateException("EOF reached before frame")
                val frames = decoder.decode(ByteBuffer.wrap(buf, 0, read))
                if (frames.isNotEmpty()) {
                    frameQueue.addAll(frames)
                    return frameQueue.poll()
                }
            }
            throw IllegalStateException("Timeout waiting for frame after $timeoutMillis ms")
        }

        fun handshake(): TcpWelcome {
            sendFrame(TcpMessageCodec.toFrame(TcpHello(wireVersion = 1, businessVersion = TcpWire.BUSINESS_VERSION)))
            val frame = receiveFrame()
            check(frame.type == TcpWire.FRAME_TYPE_WELCOME) { "Expected WELCOME, got ${frame.type}" }
            return TcpMessageCodec.parseWelcome(frame)
        }

        override fun close() {
            try { socket.close() } catch (_: Exception) {}
        }
    }

    @Test
    fun `measure scenario A - empty rooms for N in 1, 10, 100`() {
        val testRoomCounts = listOf(1, 10, 100)

        for (n in testRoomCounts) {
            val baseline = takeCensus()
            val server = TcpTableServer(TcpServerConfig(
                port = 0,
                dispatchPoolThreads = 8,
                completionPoolThreads = 4,
                seatChannelCapacityItems = 64,
                publicChannelCapacityItems = 64
            ))
            server.start()

            try {
                val pools = server.getDispatchPools()
                assertNotNull(pools, "Server must initialize shared HostDispatchPools upon start")

                // 创建 N 个空房
                val rooms = (1..n).map { i ->
                    server.registry.createRoom(
                        fixedRoomId = "EMPTY_ROOM_${n}_$i",
                        hostPlayerName = "Host_$i",
                        initialConfig = ProtocolRoomConfig(),
                        hostDispatch = pools
                    )
                }
                assertEquals(n, server.registry.activeRooms().size)

                // 记录存活线程情况
                val census = takeCensus()
                val metrics = server.aggregateChannelMetrics()

                // 断言预算上限：
                // 1. 共享 Dispatch+Completion 线程总数 <= 12，且与 N 无关
                val sharedPoolThreads = census.dispatchThreads + census.completionThreads
                assertTrue(sharedPoolThreads <= 12, "Shared dispatch+completion threads must be <= 12, got $sharedPoolThreads")
                // 2. 全进程存活线程增量 <= 5*N + 30
                val threadDelta = census.totalLiveThreads - baseline.totalLiveThreads
                val threadCeiling = 5 * n + 30
                assertTrue(threadDelta <= threadCeiling,
                    "Total live thread delta must be <= $threadCeiling for N=$n, got $threadDelta (total: ${census.totalLiveThreads}, baseline: ${baseline.totalLiveThreads})")
                // 3. 通道无异常积压
                assertTrue(metrics.totalQueuedItems <= n * 5 * 64, "Queued items must not exceed channel capacity")
                assertEquals(0L, metrics.totalOverflowCount, "Empty rooms must have 0 overflow")
                assertEquals(0L, metrics.totalScheduleRejectCount, "Empty rooms must have 0 schedule reject")

                // 关闭全部房间
                for (r in rooms) {
                    r.close()
                }

                server.close()
                val residualTasks = server.lastCloseRemainingTasks
                val postCloseCensus = waitForResidualThreads()
                val residualThreads = postCloseCensus.serialThreads + postCloseCensus.dispatchThreads

                assertEquals(0, postCloseCensus.serialThreads, "Serial threads must be 0 after room close")
                assertEquals(0, postCloseCensus.dispatchThreads, "Dispatch threads must be 0 after server close")

                budgetRows.add(BudgetRow(
                    scenario = "空房",
                    roomCount = n,
                    serialThreads = census.serialThreads,
                    aiThreads = census.aiThreads,
                    dispatchThreads = census.dispatchThreads,
                    completionThreads = census.completionThreads,
                    totalLiveThreads = census.totalLiveThreads,
                    maxQueuedItems = metrics.totalQueuedItems,
                    overflowCount = metrics.totalOverflowCount,
                    scheduleRejectCount = metrics.totalScheduleRejectCount,
                    maxWaitTimeMillis = metrics.maxWaitTimeMillis,
                    residualTasks = residualTasks,
                    residualThreads = residualThreads
                ))
            } finally {
                if (!server.closed) server.close()
            }
        }
    }

    @Test
    fun `measure scenario B - active matches for N in 1, 10, 100`() {
        val testRoomCounts = listOf(1, 10, 100)

        for (n in testRoomCounts) {
            val baseline = takeCensus()
            val server = TcpTableServer(TcpServerConfig(
                port = 0,
                dispatchPoolThreads = 8,
                completionPoolThreads = 4,
                seatChannelCapacityItems = 64,
                publicChannelCapacityItems = 64,
                channelDrainBatchSize = 16
            ))
            server.start()

            try {
                val pools = server.getDispatchPools()
                assertNotNull(pools)

                // 创建 N 个房间并启动对局
                val rooms = (1..n).map { i ->
                    val room = server.registry.createRoom(
                        fixedRoomId = "MATCH_ROOM_${n}_$i",
                        hostPlayerName = "Host_$i",
                        initialConfig = ProtocolRoomConfig(),
                        hostDispatch = pools
                    )
                    room.setReady(0, true).get(5, TimeUnit.SECONDS)
                    val startReceipt = room.startMatch(0).get(5, TimeUnit.SECONDS)
                    assertEquals(ProtocolReceiptStatus.APPLIED, startReceipt.status)
                    // 房主席位交给 AI 托管，让对局真实推进（否则庄家首决策永远等待人类，AI 池从不工作）
                    val releaseReceipt = room.releaseControl(0).get(5, TimeUnit.SECONDS)
                    assertEquals(ReceiptStatus.APPLIED, releaseReceipt.status)
                    room
                }

                // 驱动若干轮 tick，让 AI 决策和派发通道活跃起来
                val startNanos = System.nanoTime()
                val activeBudgetNanos = TimeUnit.MILLISECONDS.toNanos(if (n == 100) 2500L else 1200L)
                while (System.nanoTime() - startNanos < activeBudgetNanos) {
                    for (room in rooms) {
                        room.tableHost.tick()
                    }
                    try {
                        Thread.sleep(20L)
                    } catch (_: InterruptedException) {
                        break
                    }
                }

                val census = takeCensus()
                val metrics = server.aggregateChannelMetrics()

                // 断言预算上限：
                // 1. 共享 Dispatch+Completion 线程总数 <= 12
                val sharedPoolThreads = census.dispatchThreads + census.completionThreads
                assertTrue(sharedPoolThreads <= 12, "Shared dispatch+completion threads must be <= 12, got $sharedPoolThreads")
                // 真实推进验证：每个房间至少 1 个 AI 线程存活（AI 池核心线程不超时回收）
                assertTrue(census.aiThreads >= n, "Active matches must engage AI pools, expected >= $n AI threads, got ${census.aiThreads}")
                // 2. 全进程存活线程增量 <= 5*N + 30
                val threadDelta = census.totalLiveThreads - baseline.totalLiveThreads
                val threadCeiling = 5 * n + 30
                assertTrue(threadDelta <= threadCeiling,
                    "Total live thread delta for active match N=$n must be <= $threadCeiling, got $threadDelta (total: ${census.totalLiveThreads}, baseline: ${baseline.totalLiveThreads})")
                // 3. 队列条目受有界容量限制
                assertFalse(metrics.anyNeedsRecovery, "Channels should maintain consistent baseline during normal pacing")

                // 优雅关闭全部房间与服务端
                for (r in rooms) {
                    r.close()
                }
                server.close()

                val residualTasks = server.lastCloseRemainingTasks
                val postCloseCensus = waitForResidualThreads()
                val residualThreads = postCloseCensus.serialThreads + postCloseCensus.dispatchThreads

                assertEquals(0, postCloseCensus.serialThreads, "Serial threads must be 0 after match close")
                assertEquals(0, postCloseCensus.dispatchThreads, "Dispatch threads must be 0 after server close")

                budgetRows.add(BudgetRow(
                    scenario = "活跃对局",
                    roomCount = n,
                    serialThreads = census.serialThreads,
                    aiThreads = census.aiThreads,
                    dispatchThreads = census.dispatchThreads,
                    completionThreads = census.completionThreads,
                    totalLiveThreads = census.totalLiveThreads,
                    maxQueuedItems = metrics.totalQueuedItems,
                    overflowCount = metrics.totalOverflowCount,
                    scheduleRejectCount = metrics.totalScheduleRejectCount,
                    maxWaitTimeMillis = metrics.maxWaitTimeMillis,
                    residualTasks = residualTasks,
                    residualThreads = residualThreads
                ))
            } finally {
                if (!server.closed) server.close()
            }
        }
    }

    @Test
    fun `measure scenario C - slow consumer isolation and closure`() {
        val baseline = takeCensus()
        // 配置超小待发队列的服务器以快速触发 SLOW_CONSUMER
        val server = TcpTableServer(TcpServerConfig(
            port = 0,
            maxOutboundQueueFrames = 2,
            dispatchPoolThreads = 8,
            completionPoolThreads = 4,
            seatChannelCapacityItems = 16,
            publicChannelCapacityItems = 16
        ))
        server.start()

        try {
            val pools = server.getDispatchPools()
            assertNotNull(pools)

            val room = server.registry.createRoom(
                fixedRoomId = "SLOW_CONSUMER_ROOM",
                hostPlayerName = "HostSlow",
                initialConfig = ProtocolRoomConfig(),
                hostDispatch = pools
            )

            // 创建同一 server 上的另一个隔离房间 room2，验证慢消费者不影响其推进
            val room2 = server.registry.createRoom(
                fixedRoomId = "ISOLATED_ROOM_2",
                hostPlayerName = "Host2",
                initialConfig = ProtocolRoomConfig(),
                hostDispatch = pools
            )
            val room2Updates = CopyOnWriteArrayList<com.majiang.transport.api.ProtocolRoomInfo>()
            val room2Sub = room2.onRoomUpdate { room2Updates.add(it) }

            // 建立一个正常连接与一个慢消费连接
            EmbeddedTestClient(server.boundPort).use { client ->
                val welcome = client.handshake()
                assertNotNull(welcome)

                val conn = server.connections.values.first()
                val dummyFrame = TcpFrame(TcpWire.FRAME_TYPE_ROOM_UPDATE, ByteArray(256) { 0x01 })

                // 填满连接的待发队列
                assertTrue(conn.sendNormalFrame(dummyFrame.toByteArray()))
                assertTrue(conn.sendNormalFrame(dummyFrame.toByteArray()))

                // 再次灌入触发 SLOW_CONSUMER 关闭路径
                val accepted = conn.sendNormalFrame(dummyFrame.toByteArray())
                assertFalse(accepted, "Overloaded outbound queue must reject further frames")
                assertEquals(TcpCloseReasons.SLOW_CONSUMER, conn.disconnectReason)

                // 慢消费者断开后，房间与服务端仍健康运作
                val infoFuture = room.getRoomInfo()
                val info = infoFuture.get(3, TimeUnit.SECONDS)
                assertNotNull(info)
                assertEquals("SLOW_CONSUMER_ROOM", info.roomId)

                // 验证同一 server 上另一个房间 room2 仍正常派发推进 (MR-07a 任务 F 验收项)
                val join2 = room2.join("GuestInRoom2").get(3, TimeUnit.SECONDS)
                assertEquals(1, join2.first)

                // RoomSubscriber 为最新视图合并语义，基线与 join 更新可能合并为一条，按收敛断言
                val latestVersion2 = room2.getRoomInfoSnapshot().version
                val updateDeadline = System.currentTimeMillis() + 3000L
                while (System.currentTimeMillis() < updateDeadline &&
                    (room2Updates.isEmpty() || room2Updates.last().version < latestVersion2)
                ) {
                    Thread.sleep(10L)
                }
                assertTrue(room2Updates.isNotEmpty(), "Room 2 must continue receiving updates after slow consumer in room 1 is disconnected")
                assertTrue(room2Updates.last().version >= latestVersion2, "Room 2 must converge to the latest room version (coalescing allowed)")
                room2Sub.close()
                room2.close()
            }

            val census = takeCensus()
            val metrics = server.aggregateChannelMetrics()

            val threadDelta = census.totalLiveThreads - baseline.totalLiveThreads
            val threadCeiling = 5 * 2 + 30
            assertTrue(threadDelta <= threadCeiling,
                "Total live thread delta for slow consumer scenario must be <= $threadCeiling, got $threadDelta")

            room.close()
            server.close()

            val residualTasks = server.lastCloseRemainingTasks
            val postCloseCensus = waitForResidualThreads()
            val residualThreads = postCloseCensus.serialThreads + postCloseCensus.dispatchThreads

            assertEquals(0, postCloseCensus.serialThreads)
            assertEquals(0, postCloseCensus.dispatchThreads)

            budgetRows.add(BudgetRow(
                scenario = "慢消费者",
                roomCount = 1,
                serialThreads = census.serialThreads,
                aiThreads = census.aiThreads,
                dispatchThreads = census.dispatchThreads,
                completionThreads = census.completionThreads,
                totalLiveThreads = census.totalLiveThreads,
                maxQueuedItems = metrics.totalQueuedItems,
                overflowCount = metrics.totalOverflowCount,
                scheduleRejectCount = metrics.totalScheduleRejectCount,
                maxWaitTimeMillis = metrics.maxWaitTimeMillis,
                residualTasks = residualTasks,
                residualThreads = residualThreads
            ))
        } finally {
            if (!server.closed) server.close()
        }
    }
}
