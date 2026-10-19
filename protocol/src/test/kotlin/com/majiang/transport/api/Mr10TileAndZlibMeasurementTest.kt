package com.majiang.transport.api

import org.junit.jupiter.api.Test
import java.io.ByteArrayOutputStream
import java.lang.management.ManagementFactory
import java.util.zip.Deflater
import java.util.zip.Inflater
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * MR-10 测量基线测试：ProtocolTile 享元与 zlib 传输压缩实测。
 * 遵循 IMPROVEMENT-PLAN.md §MR-10 规范。
 * 采用权威游戏进程生成的阶段性真实载荷（早盘、中盘、晚盘、标记、候选、结算），
 * 严格验证协议编解码合法性与 136 张物理牌守恒约束。
 */
class Mr10TileAndZlibMeasurementTest {

    private val threadMXBean = ManagementFactory.getThreadMXBean() as? com.sun.management.ThreadMXBean

    private fun getCurrentThreadAllocatedBytes(): Long {
        return threadMXBean?.getThreadAllocatedBytes(Thread.currentThread().id) ?: 0L
    }

    private fun loadPayload(name: String): String {
        val stream = javaClass.getResourceAsStream("/mr10-payloads/$name.json")
            ?: throw IllegalStateException("Resource /mr10-payloads/$name.json not found")
        return stream.bufferedReader(Charsets.UTF_8).use { it.readText() }
    }

    @Test
    fun `measure ProtocolTile allocation and latency`() {
        val iterations = 100_000

        // 预热 JIT
        for (i in 0 until 10_000) {
            ProtocolTile.fromTileId(i % 136)
        }

        val startBytes = getCurrentThreadAllocatedBytes()
        val startNano = System.nanoTime()

        var dummy = 0
        for (i in 0 until iterations) {
            val tile = ProtocolTile.fromTileId(i % 136)
            dummy += tile.tileId
        }

        val elapsedNano = System.nanoTime() - startNano
        val allocatedBytes = getCurrentThreadAllocatedBytes() - startBytes

        val bytesPerOp = allocatedBytes.toDouble() / iterations
        val nanoPerOp = elapsedNano.toDouble() / iterations

        println("=== MR-10 ProtocolTile Measurement ===")
        println("Iterations: $iterations")
        println("Total elapsed: ${elapsedNano / 1_000_000} ms")
        println("Latency per fromTileId: %.2f ns".format(nanoPerOp))
        println("Allocated bytes per fromTileId: %.2f bytes".format(bytesPerOp))
        println("Dummy checksum: $dummy")

        assertTrue(dummy > 0)
    }

    @Test
    fun `validate captured authentic payloads obey protocol and physical card conservation`() {
        val phases = listOf("EARLY_GAME", "MID_GAME", "LATE_GAME", "TILE_MARK", "CANDIDATES", "SETTLEMENT")

        for (name in phases) {
            val json = loadPayload(name)
            val update = ApiMessageCodec.gson.fromJson(json, ProtocolPlayerUpdate::class.java)
            assertNotNull(update, "Payload $name must parse as ProtocolPlayerUpdate")
            val snapshot = update.snapshot
            assertNotNull(snapshot, "Payload $name snapshot must not be null")

            val view = snapshot.view
            val allTiles = mutableListOf<ProtocolTile>()
            allTiles.addAll(view.myClosedTiles)
            view.myDrawnTile?.let { allTiles.add(it) }
            for (river in view.discards) {
                allTiles.addAll(river)
            }
            for (seatMelds in view.melds) {
                for (meld in seatMelds) {
                    allTiles.addAll(meld.tiles)
                }
            }
            allTiles.addAll(view.doraIndicators)

            // 1. 验证每张牌在 0..135 物理范围内，且花色与点数同物理索引完全吻合
            for (tile in allTiles) {
                assertTrue(tile.tileId in 0..135, "tileId ${tile.tileId} out of bounds in $name")
                val flyweight = ProtocolTile.fromTileId(tile.tileId)
                assertEquals(flyweight.suit, tile.suit, "Contradictory suit for tileId ${tile.tileId} in $name")
                assertEquals(flyweight.rank, tile.rank, "Contradictory rank for tileId ${tile.tileId} in $name")
            }

            // 2. 验证物理守恒：台面活动牌（暗手牌 + 摸牌 + 四家舍牌 + 副露 + 宝牌指示牌）无重复 tileId
            val activeTileIds = mutableListOf<Int>()
            activeTileIds.addAll(view.myClosedTiles.map { it.tileId })
            view.myDrawnTile?.let { activeTileIds.add(it.tileId) }
            for (river in view.discards) {
                activeTileIds.addAll(river.map { it.tileId })
            }
            for (seatMelds in view.melds) {
                for (meld in seatMelds) {
                    activeTileIds.addAll(meld.tiles.map { it.tileId })
                }
            }
            activeTileIds.addAll(view.doraIndicators.map { it.tileId })

            val distinctActiveCount = activeTileIds.toSet().size
            assertEquals(
                activeTileIds.size, distinctActiveCount,
                "Physical tile duplicate detected in $name: total=${activeTileIds.size}, distinct=$distinctActiveCount"
            )
        }
    }

    @Test
    fun `measure zlib compression ratio and cpu cost across game phases`() {
        val payloads = mapOf(
            "EARLY_GAME" to loadPayload("EARLY_GAME"),
            "MID_GAME" to loadPayload("MID_GAME"),
            "LATE_GAME" to loadPayload("LATE_GAME"),
            "TILE_MARK" to loadPayload("TILE_MARK"),
            "CANDIDATES" to loadPayload("CANDIDATES"),
            "SETTLEMENT" to loadPayload("SETTLEMENT")
        )

        println("=== MR-10 zlib Payload & CPU Measurement (Authentic Payloads) ===")
        println("%-15s %-12s %-12s %-10s %-15s %-15s".format(
            "Phase", "Raw Bytes", "Zlib Bytes", "Ratio", "Compress(µs)", "Decompress(µs)"
        ))

        for ((name, json) in payloads) {
            val rawBytes = json.toByteArray(Charsets.UTF_8)
            val warmupRounds = 500
            val measureRounds = 2000

            // 预热
            for (i in 0 until warmupRounds) {
                val compressed = compressZlib(rawBytes)
                decompressZlib(compressed, 1024 * 1024)
            }

            // 压缩计时
            val startComp = System.nanoTime()
            var compressed: ByteArray = ByteArray(0)
            for (i in 0 until measureRounds) {
                compressed = compressZlib(rawBytes)
            }
            val compDurationNs = (System.nanoTime() - startComp) / measureRounds

            // 解压计时
            val startDecomp = System.nanoTime()
            for (i in 0 until measureRounds) {
                decompressZlib(compressed, 1024 * 1024)
            }
            val decompDurationNs = (System.nanoTime() - startDecomp) / measureRounds

            val ratio = (1.0 - (compressed.size.toDouble() / rawBytes.size)) * 100.0
            println("%-15s %-12d %-12d %-9.1f%% %-15.2f %-15.2f".format(
                name, rawBytes.size, compressed.size, ratio, compDurationNs / 1000.0, decompDurationNs / 1000.0
            ))

            // 验证解压数据自洽性
            val restored = decompressZlib(compressed, 1024 * 1024)
            assertEquals(json, String(restored, Charsets.UTF_8))
        }
    }

    @Test
    fun `zlib decompression bomb defense enforces strict maximum size`() {
        val largeJson = "{\"padding\":\"" + "A".repeat(500_000) + "\"}"
        val rawBytes = largeJson.toByteArray(Charsets.UTF_8)
        val compressed = compressZlib(rawBytes)

        // 设置解压上限为 100 KB，期望抛出异常
        val maxAllowed = 100 * 1024
        assertFailsWith<IllegalStateException> {
            decompressZlib(compressed, maxAllowed)
        }

        // 设置解压上限为 1 MB，期望正常解压
        val decompressed = decompressZlib(compressed, 1024 * 1024)
        assertEquals(rawBytes.size, decompressed.size)
    }

    companion object {
        fun compressZlib(input: ByteArray, level: Int = Deflater.DEFAULT_COMPRESSION): ByteArray {
            val deflater = Deflater(level)
            try {
                deflater.setInput(input)
                deflater.finish()
                val bos = ByteArrayOutputStream(input.size)
                val buffer = ByteArray(1024)
                while (!deflater.finished()) {
                    val count = deflater.deflate(buffer)
                    bos.write(buffer, 0, count)
                }
                return bos.toByteArray()
            } finally {
                deflater.end()
            }
        }

        fun decompressZlib(input: ByteArray, maxAllowedBytes: Int): ByteArray {
            val inflater = Inflater()
            try {
                inflater.setInput(input)
                val bos = ByteArrayOutputStream(input.size)
                val buffer = ByteArray(1024)
                var totalDecompressed = 0
                while (!inflater.finished()) {
                    val count = inflater.inflate(buffer)
                    if (count > 0) {
                        totalDecompressed += count
                        if (totalDecompressed > maxAllowedBytes) {
                            throw IllegalStateException("Decompressed bytes ($totalDecompressed) exceeded limit ($maxAllowedBytes)")
                        }
                        bos.write(buffer, 0, count)
                    }
                    if (inflater.needsInput()) break
                }
                return bos.toByteArray()
            } finally {
                inflater.end()
            }
        }
    }
}
