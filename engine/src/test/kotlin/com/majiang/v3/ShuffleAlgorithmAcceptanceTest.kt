package com.majiang.v3

import com.majiang.model.*
import com.majiang.stacking.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import kotlin.math.sqrt

/**
 * 统一码牌千术牌山算法验收。
 *
 * 覆盖：物理槽位、34 维需求矩阵、136 张守恒、王牌隔离、排序无关、
 * 固定种子复现、无方案恒等、四人档逐张恒等、起手锁定、尾段受益、
 * 人数档位强度/额度，以及多人同/异目标竞争。
 */
class ShuffleAlgorithmAcceptanceTest {
    private val generator = ShuffleWallGenerator()
    private val cfg = ShuffleConfig(enabled = true)

    private fun plan(seat: Int, target: ShuffleTarget?) = SeatShufflePlan(seat, ShuffleSelection(target))

    private fun affinity(tiles: List<Tile>, target: ShuffleTarget): Int {
        val demand = ShuffleTargets.demand(target)
        return if (target == ShuffleTarget.KOKUSHI) {
            tiles.filter { demand[it.typeIndex] > 0 }.map { it.typeIndex }.toSet().size
        } else {
            tiles.count { demand[it.typeIndex] > 0 }
        }
    }

    @Test
    fun `physical slots follow actual dealer and never include the dead wall`() {
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

    @Test
    fun `resource matrix is normalized and overlap is symmetric`() {
        for (a in ShuffleTarget.entries) {
            val demand = ShuffleTargets.demand(a)
            assertEquals(34, demand.size)
            assertEquals(1.0, demand.sum(), 1e-8)
            assertTrue(demand.all { it.isFinite() && it >= 0 })
            assertEquals(1.0, ShuffleTargets.overlap(a, a), 1e-8)
            for (b in ShuffleTarget.entries) {
                assertEquals(ShuffleTargets.overlap(a, b), ShuffleTargets.overlap(b, a), 1e-8)
            }
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

    @Test
    fun `generation conserves identities dead wall and is reproducible independently of request order`() {
        val plans = listOf(
            plan(0, ShuffleTarget.KOKUSHI),
            plan(1, ShuffleTarget.DAISANGEN),
            plan(2, ShuffleTarget.CHINITSU_MAN)
        )
        for (seed in 0L..31L) {
            val result = generator.generate(seed, (seed % 4).toInt(), plans, cfg)
            assertEquals(136, result.size)
            assertEquals(TilePool.buildStandardSet().toSet(), result.toSet())
            assertEquals(3, result.count { it.isRedFive })
            assertEquals(result, generator.generate(seed, (seed % 4).toInt(), plans.reversed(), cfg))
            assertEquals(generator.generate(seed, (seed % 4).toInt(), emptyList(), cfg).drop(122), result.drop(122))
        }
    }

    @Test
    fun `no target or disabled config equals the base shuffle exactly`() {
        val base = generator.generate(77L, 2, emptyList(), cfg)
        assertEquals(base, generator.generate(77L, 2, List(4) { plan(it, null) }, cfg))
        assertEquals(base, generator.generate(77L, 2, listOf(plan(0, ShuffleTarget.KOKUSHI)), cfg.copy(enabled = false)))
        assertEquals(
            base,
            generator.generate(
                77L,
                2,
                listOf(plan(0, ShuffleTarget.CHINITSU_MAN)),
                cfg.withTier(1, ShuffleTier(0.0, 3, 4))
            )
        )
        // 显式 strength=0 必须压过默认档位表；否则房间级“关闭强度”会被静默忽略。
        assertEquals(
            base,
            generator.generate(
                77L,
                2,
                listOf(plan(0, ShuffleTarget.CHINITSU_MAN)),
                cfg.copy(strength = 0.0)
            )
        )
    }

    @Test
    fun `zero success budgets cannot alter the wall`() {
        val base = generator.generate(88L, 3, emptyList(), cfg)
        val zeroBudget = cfg.withTier(1, ShuffleTier(1.0, initialBudget = 0, drawBudget = 0))
        assertEquals(
            base,
            generator.generate(88L, 3, listOf(plan(0, ShuffleTarget.CHINITSU_MAN)), zeroBudget)
        )
    }

    @Test
    fun `four effective casters are identical to the base shuffle tile for tile`() {
        val fourTargets = listOf(
            plan(0, ShuffleTarget.CHINITSU_MAN),
            plan(1, ShuffleTarget.KOKUSHI),
            plan(2, ShuffleTarget.DAISUUSHII),
            plan(3, ShuffleTarget.DAISANGEN)
        )
        val fourWithFlags = fourTargets.map {
            it.copy(selection = it.selection.copy(seeThrough = true))
        }
        for (seed in 0L..127L) {
            val dealer = (seed % 4).toInt()
            val base = generator.generate(seed, dealer, emptyList(), cfg)
            assertEquals(base, generator.generate(seed, dealer, fourTargets, cfg), "seed=$seed")
            assertEquals(base, generator.generate(seed, dealer, fourTargets.reversed(), cfg), "seed=$seed reversed")
            assertEquals(base, generator.generate(seed, dealer, fourWithFlags, cfg), "seed=$seed flags")
        }
    }

    @Test
    fun `three casters still shape the wall while four cancel completely`() {
        val three = listOf(
            plan(0, ShuffleTarget.CHINITSU_MAN),
            plan(1, ShuffleTarget.KOKUSHI),
            plan(2, ShuffleTarget.DAISANGEN)
        )
        val changed = (0L..31L).count { seed ->
            generator.generate(seed, 0, three, cfg) != generator.generate(seed, 0, emptyList(), cfg)
        }
        assertTrue(changed > 0, "n=3 must still perform exchanges")
        val four = three + plan(3, ShuffleTarget.DAISUUSHII)
        for (seed in 0L..15L) {
            assertEquals(
                generator.generate(seed, 0, emptyList(), cfg),
                generator.generate(seed, 0, four, cfg)
            )
        }
    }

    @Test
    fun `draw phase never steals the dealt initial wall`() {
        val config = cfg.withTier(1, ShuffleTier(0.75, initialBudget = 0, drawBudget = 4))
        for (seed in 100L..131L) {
            val baseline = generator.generate(seed, 3, emptyList(), cfg)
            val shaped = generator.generate(seed, 3, listOf(plan(2, ShuffleTarget.DAISANGEN)), config)
            assertEquals(baseline.take(53), shaped.take(53), "draw phase must not touch 0..52")
            assertEquals(baseline.drop(122), shaped.drop(122), "dead wall must never change")
        }
    }

    @Test
    fun `late expected draw slots can receive gains`() {
        val config = cfg.withTier(1, ShuffleTier(0.75, initialBudget = 0, drawBudget = 4))
        val dealer = 1
        val seat = 3
        val slots = ShuffleSlots.draws(dealer, seat, 18)
        var early = 0
        var late = 0
        for (seed in 0L until 128L) {
            val baseline = generator.generate(seed, dealer, emptyList(), cfg)
            val shaped = generator.generate(seed, dealer, listOf(plan(seat, ShuffleTarget.CHINITSU_MAN)), config)
            early += affinity(slots.take(9).map { shaped[it] }, ShuffleTarget.CHINITSU_MAN) -
                affinity(slots.take(9).map { baseline[it] }, ShuffleTarget.CHINITSU_MAN)
            late += affinity(slots.drop(9).map { shaped[it] }, ShuffleTarget.CHINITSU_MAN) -
                affinity(slots.drop(9).map { baseline[it] }, ShuffleTarget.CHINITSU_MAN)
        }
        assertTrue(early > 0, "early slots should gain: $early")
        assertTrue(late > 0, "late planned slots must also gain: $late")
    }

    @Test
    fun `every target improves its intended initial hand over a fixed random sample`() {
        // 使用统一的测试档位，避免把已按实战标定的目标专用低额度误判为算法无增益。
        val testTier = ShuffleTier(0.75, 3, 4)
        val config = ShuffleTarget.entries.fold(cfg.withTier(1, testTier)) { acc, target ->
            acc.withTier(1, testTier, target)
        }
        for (target in ShuffleTarget.entries) {
            var gain = 0
            for (seed in 0L until 96L) {
                val dealer = (seed % 4).toInt()
                val seat = ((seed / 4) % 4).toInt()
                val slots = ShuffleSlots.initial(dealer, seat)
                val baseline = generator.generate(seed, dealer, emptyList(), cfg)
                val result = generator.generate(seed, dealer, listOf(plan(seat, target)), config)
                gain += affinity(slots.map { result[it] }, target) - affinity(slots.map { baseline[it] }, target)
            }
            assertTrue(gain > 48, "Expected measurable progress for $target, total gain=$gain")
        }
    }

    @Test
    fun `same-resource competition is weaker than independent targets on average`() {
        var same = 0
        var independent = 0
        // 同目标与异目标都使用相同测试档位，并关闭“异目标额外分化衰减”，
        // 只保留人物牌资源竞争这一个变量。
        val tier = ShuffleTier(0.9, initialBudget = 10, drawBudget = 0)
        val config = cfg.copy(diversityInterference = 0.0)
            .withTier(2, tier, ShuffleTarget.CHINITSU_MAN)
            .withTier(2, tier, ShuffleTarget.CHINITSU_PIN)
            .withMixedTier(2, tier)
        for (seed in 0L until 128L) {
            val own = plan(0, ShuffleTarget.CHINITSU_MAN)
            val slots = ShuffleSlots.initial(0, 0)
            val a = generator.generate(seed, 0, listOf(own, plan(1, ShuffleTarget.CHINITSU_MAN)), config)
            val b = generator.generate(seed, 0, listOf(own, plan(1, ShuffleTarget.CHINITSU_PIN)), config)
            same += affinity(slots.map { a[it] }, ShuffleTarget.CHINITSU_MAN)
            independent += affinity(slots.map { b[it] }, ShuffleTarget.CHINITSU_MAN)
        }
        assertTrue(independent > same, "Same-suit competition must have real effect: same=$same independent=$independent")
    }

    @Test
    fun `tier configuration is named validated and bounded`() {
        assertEquals(18, ShuffleConfig().plannedDraws)
        assertEquals(ShuffleTier(0.52, 1, 4), ShuffleConfig().tierFor(1))
        assertEquals(ShuffleTier(0.52, 1, 4), ShuffleConfig().tierFor(1, ShuffleTarget.CHINITSU_MAN))
        assertEquals(ShuffleTier(0.0, 1, 4), ShuffleConfig().copy(strength = 0.0).tierFor(1))
        assertEquals(ShuffleTier(0.4, 1, 4), ShuffleConfig().copy(strength = 0.4).tierFor(1))
        assertNull(ShuffleConfig().tierFor(4))
        assertEquals(ShuffleTier(0.5, 1, 2), ShuffleConfig().withTier(2, ShuffleTier(0.5, 1, 2)).tierFor(2))
        assertEquals(0.06, ShuffleConfig().tierFor(1, ShuffleTarget.KOKUSHI)!!.strength, 1e-9)
        assertEquals(
            ShuffleTier(0.4, 1, 1),
            ShuffleConfig().withTier(1, ShuffleTier(0.4, 1, 1), ShuffleTarget.KOKUSHI)
                .tierFor(1, ShuffleTarget.KOKUSHI)
        )
        assertThrows<IllegalArgumentException> { ShuffleTier(1.2, 3, 4) }
        assertThrows<IllegalArgumentException> { ShuffleTier(0.5, -1, 4) }
        assertThrows<IllegalArgumentException> { ShuffleConfig(enabled = true).withTiers(mapOf(4 to ShuffleTier(0.5, 1, 1))) }
        val snapshot = ShuffleConfig(enabled = true).tiers
        assertThrows<UnsupportedOperationException> {
            (snapshot as MutableMap<Int, ShuffleTier>)[1] = ShuffleTier(0.0, 0, 0)
        }
        assertEquals(0.0, ShuffleConfig().diversityBudgetPenaltyFor(2), 1e-9)
        assertEquals(0.0, ShuffleConfig().diversityBudgetPenaltyFor(3), 1e-9)
        assertEquals(ShuffleTier(0.52, 1, 1), ShuffleConfig().mixedTierFor(2))
        assertEquals(ShuffleTier(0.65, 1, 0), ShuffleConfig().mixedTierFor(3))
        assertEquals(
            ShuffleTier(0.4, 2, 1),
            ShuffleConfig().withMixedTier(2, ShuffleTier(0.4, 2, 1)).mixedTierFor(2)
        )
        assertThrows<IllegalArgumentException> {
            ShuffleConfig().withDiversityBudgetPenalty(2, -0.1)
        }
        assertThrows<IllegalArgumentException> { ShuffleConfig(diversityInterference = -1.0) }
    }

    @Test
    fun `global strength cap applies to mixed tiers including custom overrides`() {
        val low = ShuffleConfig(enabled = true, strength = 0.2)
            .withMixedTier(2, ShuffleTier(1.0, 10, 10))
            .withMixedTier(3, ShuffleTier(0.9, 10, 10))
        assertEquals(0.2, low.mixedTierFor(2)!!.strength, 1e-9)
        assertEquals(0.2, low.mixedTierFor(3)!!.strength, 1e-9)
        assertEquals(10, low.mixedTierFor(2)!!.initialBudget)
        assertEquals(10, low.mixedTierFor(3)!!.drawBudget)
    }

    @Test
    fun `zero global strength makes mixed target walls identical to base for n=2 and n=3`() {
        val mixed2 = listOf(
            plan(0, ShuffleTarget.CHINITSU_MAN),
            plan(1, ShuffleTarget.CHINITSU_SOU)
        )
        val mixed3 = mixed2 + plan(2, ShuffleTarget.CHINITSU_PIN)
        val zero = cfg.copy(strength = 0.0)
        val zeroWithCustomMixed = zero
            .withMixedTier(2, ShuffleTier(1.0, 10, 10))
            .withMixedTier(3, ShuffleTier(1.0, 10, 10))
        for (seed in 0L..31L) {
            val dealer = (seed % 4).toInt()
            val base = generator.generate(seed, dealer, emptyList(), cfg)
            assertEquals(base, generator.generate(seed, dealer, mixed2, zero), "n=2 seed=$seed")
            assertEquals(base, generator.generate(seed, dealer, mixed3, zero), "n=3 seed=$seed")
            assertEquals(
                base,
                generator.generate(seed, dealer, mixed2, zeroWithCustomMixed),
                "n=2 custom mixed tier seed=$seed"
            )
            assertEquals(
                base,
                generator.generate(seed, dealer, mixed3, zeroWithCustomMixed),
                "n=3 custom mixed tier seed=$seed"
            )
        }
    }

    @Test
    fun `diversity penalties affect only mixed targets while four casters stay identical`() {
        val same = listOf(plan(0, ShuffleTarget.CHINITSU_MAN), plan(1, ShuffleTarget.CHINITSU_MAN))
        val mixed = listOf(plan(0, ShuffleTarget.CHINITSU_MAN), plan(1, ShuffleTarget.CHINITSU_SOU))
        val neutral = cfg.copy(diversityInterference = 0.0).withUniformDiversityBudgetPenalty(0.0)
        val high = cfg.copy(diversityInterference = 10.0).withUniformDiversityBudgetPenalty(10.0)
        var mixedChanged = false
        for (seed in 0L..31L) {
            val dealer = (seed % 4).toInt()
            assertEquals(
                generator.generate(seed, dealer, same, neutral),
                generator.generate(seed, dealer, same, high),
                "same-target plans must not be affected by diversity penalties (seed=$seed)"
            )
            if (generator.generate(seed, dealer, mixed, neutral) != generator.generate(seed, dealer, mixed, high)) {
                mixedChanged = true
            }
            assertEquals(
                generator.generate(seed, dealer, emptyList(), high),
                generator.generate(
                    seed,
                    dealer,
                    listOf(
                        plan(0, ShuffleTarget.CHINITSU_MAN),
                        plan(1, ShuffleTarget.KOKUSHI),
                        plan(2, ShuffleTarget.DAISANGEN),
                        plan(3, ShuffleTarget.DAISUUSHII)
                    ),
                    high
                ),
                "n=4 identity must ignore diversity penalties (seed=$seed)"
            )
        }
        assertTrue(mixedChanged, "mixed-target plans must respond to diversity penalties")
    }
}
