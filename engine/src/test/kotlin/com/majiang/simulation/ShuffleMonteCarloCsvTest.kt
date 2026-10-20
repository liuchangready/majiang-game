package com.majiang.simulation

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.io.ByteArrayOutputStream
import java.io.PrintStream
import kotlin.math.abs

/**
 * 可执行 CSV 口径回归：all 行与逐席行必须使用同一“施术者机会”分母。
 *
 * 用 2 对、n=1..4 实际跑一次模拟，解析真实 stdout，而不是只测一个格式化辅助函数。
 */
class ShuffleMonteCarloCsvTest {

    @Test
    fun `CSV samples and all denominators match seat opportunities for n one to four`() {
        val originalOut = System.out
        val buffer = ByteArrayOutputStream()
        try {
            System.setOut(PrintStream(buffer, true, Charsets.UTF_8))
            ShuffleMonteCarloMain.main(arrayOf("2", "CHINITSU_MAN", "1,2,3,4", "basic"))
        } finally {
            System.setOut(originalOut)
        }

        val output = buffer.toString(Charsets.UTF_8.name())
        val header = output.lineSequence()
            .map { it.trim() }
            .first { it.startsWith("target,n,samples,scope,") }
            .split(',')
        val rows = output.lineSequence()
            .map { it.trim() }
            .filter { it.startsWith("CHINITSU_MAN,") }
            .map { line -> header.zip(line.split(',')).toMap() }
            .toList()
        assertTrue(rows.isNotEmpty(), "simulation produced no CSV rows:\n$output")

        for (n in 1..4) {
            val cell = rows.filter { it.getValue("n") == n.toString() }
            val aggregate = cell.single { it.getValue("scope") == "all" }
            val seats = cell.filter { it.getValue("scope").startsWith("seat") }
            val total = aggregate.getValue("samples").toInt()
            assertEquals(2 * n, total, "all.samples must be pairs × n for n=$n")
            assertEquals(seats.sumOf { it.getValue("samples").toInt() }, total, "all.samples must sum seats for n=$n")

            for (row in cell) {
                val samples = row.getValue("samples").toInt()
                assertTrue(samples > 0, "samples must be positive for n=$n scope=${row["scope"]}")
                assertEquals(
                    samples,
                    row.getValue("both").toInt() +
                        row.getValue("shapedOnly").toInt() +
                        row.getValue("baseOnly").toInt() +
                        row.getValue("neither").toInt(),
                    "win buckets must sum to samples for n=$n scope=${row["scope"]}"
                )
                assertRate(row, "baseWinCount", "baseRate", samples)
                assertRate(row, "shapedWinCount", "shapedRate", samples)
                assertRate(row, "baseTargetWinCount", "baseTargetRate", samples)
                assertRate(row, "shapedTargetWinCount", "shapedTargetRate", samples)

                val expectedDelta = (
                    row.getValue("shapedWinCount").toInt() - row.getValue("baseWinCount").toInt()
                    ).toDouble() / samples
                assertEquals(
                    expectedDelta,
                    row.getValue("pairedDelta").replace("n/a", "0").toDouble(),
                    2e-4,
                    "pairedDelta denominator must be samples for n=$n scope=${row["scope"]}"
                )
            }

            if (n == 4) {
                assertEquals(0, aggregate.getValue("shapedOnly").toInt())
                assertEquals(0, aggregate.getValue("baseOnly").toInt())
                assertEquals(0.0, aggregate.getValue("pairedDelta").toDouble(), 1e-9)
            }
        }
    }

    private fun assertRate(row: Map<String, String>, countKey: String, rateKey: String, samples: Int) {
        val expected = row.getValue(countKey).toInt().toDouble() / samples
        val actual = row.getValue(rateKey).replace("n/a", "0").toDouble()
        assertTrue(abs(expected - actual) <= 2e-4) {
            "$rateKey=$actual must equal $countKey/$samples=$expected for scope=${row["scope"]}"
        }
    }
}
