package com.majiang.server.mr10

import com.majiang.access.SeatControlView
import com.majiang.authority.TableAuthority
import com.majiang.match.MatchConfig
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.stacking.ShuffleConfig
import org.junit.jupiter.api.Test
import java.lang.management.ManagementFactory
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotSame
import kotlin.test.assertTrue

/**
 * MR-10 测量基线测试：快照共享的全对象图不可变审计与分配热点测量。
 */
class Mr10SnapshotAuditAndHotspotTest {

    private val threadMXBean = ManagementFactory.getThreadMXBean() as? com.sun.management.ThreadMXBean

    private fun getCurrentThreadAllocatedBytes(): Long {
        return threadMXBean?.getThreadAllocatedBytes(Thread.currentThread().id) ?: 0L
    }

    private fun createRunningAuthority(): Pair<TableAuthority, List<SeatControlView>> {
        val authority = TableAuthority(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false))
        )
        val controls = List(4) { SeatControlView(it, connected = true, role = "HUMAN", controlEpoch = 1L) }
        authority.start(0, System.currentTimeMillis())
        return Pair(authority, controls)
    }

    @Test
    fun `measure querySnapshot and deepCopy allocation and latency hot spots`() {
        val (authority, controls) = createRunningAuthority()
        val seat = 0
        val syncEpoch = "sync-bench"
        val cursor = 1L

        // 预热
        for (i in 0 until 1_000) {
            val snap = authority.querySnapshot(seat, syncEpoch, cursor, controls)
            snap.deepCopy()
        }

        val iterations = 10_000

        // 1. 测量 TableAuthority.querySnapshot 原始生成
        val startBytesQuery = getCurrentThreadAllocatedBytes()
        val startNanoQuery = System.nanoTime()
        var dummy = 0
        for (i in 0 until iterations) {
            val snap = authority.querySnapshot(seat, syncEpoch, cursor, controls)
            dummy += snap.viewerSeat
        }
        val elapsedNanoQuery = System.nanoTime() - startNanoQuery
        val allocatedBytesQuery = getCurrentThreadAllocatedBytes() - startBytesQuery

        // 2. 测量 snapshot.deepCopy() 重复深拷贝
        val baselineSnap = authority.querySnapshot(seat, syncEpoch, cursor, controls)
        val startBytesCopy = getCurrentThreadAllocatedBytes()
        val startNanoCopy = System.nanoTime()
        for (i in 0 until iterations) {
            val copied = baselineSnap.deepCopy()
            dummy += copied.viewerSeat
        }
        val elapsedNanoCopy = System.nanoTime() - startNanoCopy
        val allocatedBytesCopy = getCurrentThreadAllocatedBytes() - startBytesCopy

        val queryLatency = elapsedNanoQuery.toDouble() / iterations
        val queryBytes = allocatedBytesQuery.toDouble() / iterations
        val copyLatency = elapsedNanoCopy.toDouble() / iterations
        val copyBytes = allocatedBytesCopy.toDouble() / iterations

        println("=== MR-10 Snapshot Allocation & Hotspot Measurement ===")
        println("Iterations: $iterations")
        println("TableAuthority.querySnapshot: Latency = %.2f ns, Allocated = %.2f bytes".format(queryLatency, queryBytes))
        println("PlayerSnapshot.deepCopy:     Latency = %.2f ns, Allocated = %.2f bytes".format(copyLatency, copyBytes))
        println("Ratio copy/query: Latency = %.1f%%, Allocated = %.1f%%".format(
            (copyLatency / queryLatency) * 100.0,
            (copyBytes / queryBytes) * 100.0
        ))
        println("Dummy checksum: $dummy")

        assertTrue(dummy >= 0)
    }

    @Test
    fun `immutability audit - deepCopy creates independent defensive copies preventing aliasing`() {
        val (authority, controls) = createRunningAuthority()
        val original = authority.querySnapshot(0, "sync-audit", 1L, controls)
        val copy = original.deepCopy()

        // 验证对象引用隔离
        assertNotSame(original, copy, "Snapshot copy must be an independent instance")
        assertNotSame(original.view, copy.view, "PlayerView copy must be independent")
        assertNotSame(original.decision, copy.decision, "PlayerDecision copy must be independent")
        assertNotSame(original.clocks, copy.clocks, "Clocks list must be independent instance")
        assertNotSame(original.controls, copy.controls, "Controls list must be independent instance")

        // 验证只读不可篡改集合特性
        assertFailsWith<UnsupportedOperationException> {
            (original.view.myClosedTiles as MutableList<Tile>).add(Tile(Suit.MAN, 1))
        }
        assertFailsWith<UnsupportedOperationException> {
            (copy.view.myClosedTiles as MutableList<Tile>).add(Tile(Suit.MAN, 1))
        }

        // 跨座位私密隔离审计：座位 0 与 座位 1 的手牌绝不相同，严禁座位间共享 PlayerSnapshot
        val snapSeat1 = authority.querySnapshot(1, "sync-audit", 1L, controls)
        assertNotSame(original.view.myClosedTiles, snapSeat1.view.myClosedTiles)
        assertEquals(0, original.viewerSeat)
        assertEquals(1, snapSeat1.viewerSeat)
    }
}
