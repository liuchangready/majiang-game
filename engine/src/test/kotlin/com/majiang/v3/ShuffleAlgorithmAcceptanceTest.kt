package com.majiang.v3

import com.majiang.model.*
import com.majiang.stacking.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import kotlin.math.sqrt

class ShuffleAlgorithmAcceptanceTest {
    private val generator = ShuffleWallGenerator()
    private val cfg = ShuffleConfig(enabled = true)
    private fun plan(seat: Int, initial: ShuffleTarget? = null, draw: ShuffleTarget? = null) =
        SeatShufflePlan(seat, ShuffleSelection(initial?.let { setOf(it) } ?: emptySet(), draw?.let { setOf(it) } ?: emptySet()))
    private fun affinity(tiles: List<Tile>, target: ShuffleTarget): Int {
        val demand = ShuffleTargets.demand(target)
        return if (target == ShuffleTarget.KOKUSHI) tiles.filter { demand[it.typeIndex] > 0 }.map { it.typeIndex }.toSet().size
            else tiles.count { demand[it.typeIndex] > 0 }
    }

    @Test fun `physical slots follow actual dealer and never include the dead wall`() {
        for (dealer in 0..3) {
            val dealt = (0..3).flatMap { seat ->
                val slots = ShuffleSlots.initial(dealer, seat)
                assertEquals(if (seat == dealer) 14 else 13, slots.size)
                assertEquals(if (seat == dealer) 52 else 48 + (seat - dealer + 4) % 4, slots.last())
                slots
            }
            assertEquals((0..52).toSet(), dealt.toSet())
            assertEquals(53, dealt.size)
            for (seat in 0..3) {
                val slots = ShuffleSlots.draws(dealer, seat, 18)
                val expected = (53 + (seat - dealer - 1 + 4) % 4..121 step 4).toList()
                assertEquals(expected, slots)
            }
        }
    }

    @Test fun `resource matrix and combined targets are normalized without multiplying strength`() {
        for (a in ShuffleTarget.entries) {
            val demand = ShuffleTargets.demand(a)
            assertEquals(34, demand.size)
            assertEquals(1.0, demand.sum(), 1e-8)
            assertTrue(demand.all { it.isFinite() && it >= 0 })
            assertEquals(1.0, ShuffleTargets.overlap(a, a), 1e-8)
            for (b in ShuffleTarget.entries) assertEquals(ShuffleTargets.overlap(a, b), ShuffleTargets.overlap(b, a), 1e-8)
        }
        assertEquals(0.0, ShuffleTargets.overlap(ShuffleTarget.CHINITSU_MAN, ShuffleTarget.CHINITSU_PIN), 1e-8)
        assertEquals(2 / sqrt(117.0), ShuffleTargets.overlap(ShuffleTarget.CHINITSU_MAN, ShuffleTarget.KOKUSHI), 1e-8)
        assertEquals(3 / sqrt(39.0), ShuffleTargets.overlap(ShuffleTarget.DAISANGEN, ShuffleTarget.KOKUSHI), 1e-8)
        assertEquals(4 / sqrt(52.0), ShuffleTargets.overlap(ShuffleTarget.DAISUUSHII, ShuffleTarget.KOKUSHI), 1e-8)
        assertEquals(0.0, ShuffleTargets.overlap(ShuffleTarget.DAISANGEN, ShuffleTarget.DAISUUSHII), 1e-8)
        assertEquals(1.0, ShuffleTargets.combined(ShuffleTarget.entries.toSet()).sum(), 1e-8)
        assertTrue(ShuffleTargets.combined(emptySet()).all { it == 0.0 })
        val mutable = ShuffleTargets.demand(ShuffleTarget.KOKUSHI)
        mutable.fill(0.0)
        assertEquals(1.0, ShuffleTargets.demand(ShuffleTarget.KOKUSHI).sum(), 1e-8)
    }

    @Test fun `generation conserves identities dead wall and reproducibility independently of request order`() {
        val plans = listOf(plan(0, ShuffleTarget.KOKUSHI, ShuffleTarget.DAISUUSHII),
            plan(1, ShuffleTarget.DAISANGEN, ShuffleTarget.CHINITSU_PIN), plan(2, ShuffleTarget.CHINITSU_MAN), plan(3, draw = ShuffleTarget.CHINITSU_SOU))
        for (seed in 0L..31L) {
            val result = generator.generate(seed, (seed % 4).toInt(), plans, cfg)
            assertEquals(136, result.size)
            assertEquals(TilePool.buildStandardSet().toSet(), result.toSet())
            assertEquals(3, result.count { it.isRedFive })
            assertEquals(result, generator.generate(seed, (seed % 4).toInt(), plans.reversed(), cfg))
            assertEquals(generator.generate(seed, (seed % 4).toInt(), emptyList(), cfg).drop(122), result.drop(122))
        }
    }

    @Test fun `draw only technique leaves initial deal untouched and zero strength stays random`() {
        for (seed in 100L..131L) {
            val baseline = generator.generate(seed, 3, emptyList(), cfg)
            val drawn = generator.generate(seed, 3, listOf(plan(2, draw = ShuffleTarget.DAISANGEN)), cfg)
            assertEquals(baseline.take(53), drawn.take(53), "Draw stream technique must not steal initial deal tiles")
            assertEquals(baseline, generator.generate(seed, 3, listOf(plan(2, ShuffleTarget.KOKUSHI)), cfg.copy(strength = 0.0)))
        }
    }

    @Test fun `every target improves its intended stage over a fixed random sample`() {
        for (target in ShuffleTarget.entries) for (initial in listOf(true, false)) {
            var gain = 0
            for (seed in 0L until 96L) {
                val dealer = (seed % 4).toInt()
                val seat = ((seed / 4) % 4).toInt()
                val slots = if (initial) ShuffleSlots.initial(dealer, seat) else ShuffleSlots.draws(dealer, seat, 8)
                val baseline = generator.generate(seed, dealer, emptyList(), cfg)
                val result = generator.generate(seed, dealer, listOf(if (initial) plan(seat, target) else plan(seat, draw = target)), cfg)
                gain += affinity(slots.map { result[it] }, target) - affinity(slots.map { baseline[it] }, target)
            }
            assertTrue(gain > 48, "Expected measurable progress for $target, initial=$initial, total gain=$gain")
        }
    }

    @Test fun `same resource competition is weaker than independent suit plans on average`() {
        var same = 0
        var independent = 0
        for (seed in 0L until 96L) {
            val own = plan(0, ShuffleTarget.CHINITSU_MAN)
            val slots = ShuffleSlots.initial(0, 0)
            val a = generator.generate(seed, 0, listOf(own, plan(1, ShuffleTarget.CHINITSU_MAN)), cfg)
            val b = generator.generate(seed, 0, listOf(own, plan(1, ShuffleTarget.CHINITSU_PIN)), cfg)
            same += affinity(slots.map { a[it] }, ShuffleTarget.CHINITSU_MAN)
            independent += affinity(slots.map { b[it] }, ShuffleTarget.CHINITSU_MAN)
        }
        assertTrue(independent > same, "Same-suit competition must have real effect: same=$same independent=$independent")
    }
}
