package com.majiang.client.mapping

import com.majiang.client.api.TileView
import com.majiang.transport.api.ProtocolTile
import org.junit.jupiter.api.Test
import java.lang.management.ManagementFactory
import kotlin.test.assertTrue

/**
 * MR-10 测量基线测试：TileView 享元与 ProtocolViewMapper 映射延迟实测。
 */
class Mr10TileViewBenchmarkTest {

    private val threadMXBean = ManagementFactory.getThreadMXBean() as? com.sun.management.ThreadMXBean

    private fun getCurrentThreadAllocatedBytes(): Long {
        return threadMXBean?.getThreadAllocatedBytes(Thread.currentThread().id) ?: 0L
    }

    @Test
    fun `measure TileView fromTileId allocation and latency`() {
        val iterations = 100_000

        // 预热
        for (i in 0 until 10_000) {
            TileView.fromTileId(i % 136)
        }

        val startBytes = getCurrentThreadAllocatedBytes()
        val startNano = System.nanoTime()

        var dummy = 0
        for (i in 0 until iterations) {
            val tile = TileView.fromTileId(i % 136)
            dummy += tile.tileId
        }

        val elapsedNano = System.nanoTime() - startNano
        val allocatedBytes = getCurrentThreadAllocatedBytes() - startBytes

        val bytesPerOp = allocatedBytes.toDouble() / iterations
        val nanoPerOp = elapsedNano.toDouble() / iterations

        println("=== MR-10 TileView fromTileId Baseline Measurement ===")
        println("Iterations: $iterations")
        println("Total elapsed: ${elapsedNano / 1_000_000} ms")
        println("Latency per fromTileId: %.2f ns".format(nanoPerOp))
        println("Allocated bytes per fromTileId: %.2f bytes".format(bytesPerOp))
        println("Dummy checksum: $dummy")

        assertTrue(dummy > 0)
    }

    @Test
    fun `measure ProtocolViewMapper mapTile and toProtocolTile`() {
        val iterations = 100_000
        val protoTiles = Array(136) { ProtocolTile.fromTileId(it) }

        // 预热
        for (i in 0 until 10_000) {
            val view = ProtocolViewMapper.mapTile(protoTiles[i % 136])
            ProtocolViewMapper.toProtocolTile(view)
        }

        val startBytes = getCurrentThreadAllocatedBytes()
        val startNano = System.nanoTime()

        var dummy = 0
        for (i in 0 until iterations) {
            val view = ProtocolViewMapper.mapTile(protoTiles[i % 136])
            val back = ProtocolViewMapper.toProtocolTile(view)
            dummy += back.tileId
        }

        val elapsedNano = System.nanoTime() - startNano
        val allocatedBytes = getCurrentThreadAllocatedBytes() - startBytes

        val bytesPerRoundTrip = allocatedBytes.toDouble() / iterations
        val nanoPerRoundTrip = elapsedNano.toDouble() / iterations

        println("=== MR-10 ProtocolViewMapper mapTile RoundTrip Baseline ===")
        println("Iterations: $iterations")
        println("Latency per round-trip: %.2f ns".format(nanoPerRoundTrip))
        println("Allocated bytes per round-trip: %.2f bytes".format(bytesPerRoundTrip))
        println("Dummy checksum: $dummy")

        assertTrue(dummy > 0)
    }
}
