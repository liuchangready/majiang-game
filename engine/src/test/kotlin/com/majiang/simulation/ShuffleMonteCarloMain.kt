package com.majiang.simulation

import com.majiang.ai.AIPlayer
import com.majiang.ai.AIStrategy
import com.majiang.ai.BasicAIStrategy
import com.majiang.ai.TargetAwareAi
import com.majiang.application.ApplicationCoordinator
import com.majiang.event.GameEvent
import com.majiang.event.GameEventBus
import com.majiang.model.Meld
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.model.TilePool
import com.majiang.stacking.SeatShufflePlan
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.stacking.ShuffleWallGenerator
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import kotlin.math.sqrt
import kotlin.random.Random

/**
 * 统一码牌千术成对全局模拟入口（仅测试源集，供离线标定）。
 *
 * 每个样本对使用同一局种子、庄家、座位轮换与 AI 策略，只切换“是否施术”：
 * - 基线：同种子基础洗牌；
 * - 施术：同一 ShuffleWallGenerator 生成的目标牌山。
 *
 * 统计按**每名有效施术者**汇总，而不是“任一施术者获胜”：
 * 每名施术者每对贡献一个 0/1 结果，n 人时聚合分母为 pairs × n。
 * n=4 时基线牌山与施术牌山必须逐张相等，统计上恒为 1.00 倍。
 *
 * 用法（Gradle）：
 *   ./gradlew :engine:shuffleSimulation -Ppairs=200 -Ptarget=CHINITSU_MAN -Pn=1,2,3,4 -Popper=basic
 *   ./gradlew :engine:shuffleSimulation -Ppairs=200 -Ptarget=ALL -Pn=1,2,3 -Popper=target -Pmix=true
 * 或直接运行本类并传入 pairs target n opponent [tier1] [tier2] [tier3] [mix] [seedStart]。
 */
object ShuffleMonteCarloMain {

    private const val START_SCORE = 25_000

    private data class Outcome(
        val winner: Int?,
        val yaku: List<String>,
        val draw: Boolean,
        val finalScores: List<Int>,
        val initialHands: List<List<Tile>>,
        val finalHands: List<List<Tile>>,
        val finalMelds: List<List<Meld>>
    )

    private data class SeatStats(
        var opportunities: Int = 0,
        var baseWins: Int = 0,
        var shapedWins: Int = 0,
        var baseTargetWins: Int = 0,
        var shapedTargetWins: Int = 0,
        var basePoints: Long = 0L,
        var shapedPoints: Long = 0L,
        var baseTargetTiles: Long = 0L,
        var shapedTargetTiles: Long = 0L,
        var baseTargetMelds: Long = 0L,
        var shapedTargetMelds: Long = 0L,
        var bothWin: Int = 0,
        var shapedOnly: Int = 0,
        var baseOnly: Int = 0,
        var neither: Int = 0
    )

    private data class Cell(
        val target: ShuffleTarget,
        val n: Int,
        val pairs: Int,
        val opponentMode: String,
        val mixTargets: Boolean,
        val stats: Map<Int, SeatStats>,
        val baseDraws: Int,
        val shapedDraws: Int,
        val aggregateDelta: Double,
        val aggregateCiLow: Double,
        val aggregateCiHigh: Double
    )

    private val DISPLAY = mapOf(
        ShuffleTarget.CHINITSU_MAN to "清一色",
        ShuffleTarget.CHINITSU_SOU to "清一色",
        ShuffleTarget.CHINITSU_PIN to "清一色",
        ShuffleTarget.DAISANGEN to "大三元",
        ShuffleTarget.KOKUSHI to "国士无双",
        ShuffleTarget.DAISUUSHII to "大四喜"
    )

    @JvmStatic
    fun main(args: Array<String>) {
        val pairs = args.getOrNull(0)?.toIntOrNull() ?: 200
        val targetSpec = args.getOrNull(1)?.uppercase() ?: "CHINITSU_MAN"
        val targets = if (targetSpec == "ALL") {
            ShuffleTarget.entries.toList()
        } else {
            listOf(
                runCatching { ShuffleTarget.valueOf(targetSpec) }.getOrNull()
                    ?: error("unknown target: $targetSpec")
            )
        }
        val ns = args.getOrNull(2)?.split(',')?.mapNotNull { it.toIntOrNull() } ?: listOf(1, 2, 3, 4)
        val opponentMode = args.getOrNull(3) ?: "basic"
        val mixTargets = args.getOrNull(7)?.toBooleanStrictOrNull() == true
        val seedStart = args.getOrNull(8)?.toLongOrNull() ?: 10_000L
        val diversity = args.getOrNull(9)?.takeIf { it.isNotBlank() }?.toDoubleOrNull()
        val targetTierSpec = args.getOrNull(10)?.takeIf { it.isNotBlank() }
        val diversityBudgetPenalty = args.getOrNull(11)?.takeIf { it.isNotBlank() }?.toDoubleOrNull()
        val diversityBudgetTierSpec = args.getOrNull(12)?.takeIf { it.isNotBlank() }
        val mixedTierSpec = args.getOrNull(13)?.takeIf { it.isNotBlank() }
        require(pairs > 0) { "pairs must be positive" }
        require(ns.all { it in 1..4 }) { "n must be in 1..4" }

        var config = ShuffleConfig(enabled = true)
        // 可选：args[4..6] 分别为 n=1..3 的 "strength,initialBudget,drawBudget" 覆盖值。
        // 覆盖值只影响人数默认档；按目标覆盖的档位仍可通过 ShuffleConfig.withTier(target=...) 调整。
        for (i in 0 until 3) {
            val spec = args.getOrNull(4 + i)?.takeIf { it.isNotBlank() } ?: continue
            val parts = spec.split(',')
            require(parts.size == 3) { "tier spec must be strength,initialBudget,drawBudget: $spec" }
            val strength = parts[0].toDouble()
            val initial = parts[1].toInt()
            val draw = parts[2].toInt()
            config = config.withTier(i + 1, com.majiang.stacking.ShuffleTier(strength, initial, draw))
        }
        if (diversity != null) {
            require(!diversity.isNaN() && !diversity.isInfinite() && diversity >= 0.0) {
                "diversity must be finite and >= 0.0: $diversity"
            }
            config = config.copy(diversityInterference = diversity)
        }
        if (diversityBudgetPenalty != null) {
            require(
                !diversityBudgetPenalty.isNaN() && !diversityBudgetPenalty.isInfinite() &&
                    diversityBudgetPenalty >= 0.0
            ) {
                "diversityBudgetPenalty must be finite and >= 0.0: $diversityBudgetPenalty"
            }
            config = config.withUniformDiversityBudgetPenalty(diversityBudgetPenalty)
        }
        if (diversityBudgetTierSpec != null) {
            for (entry in diversityBudgetTierSpec.split(';').map { it.trim() }.filter { it.isNotEmpty() }) {
                val bits = entry.split(':', limit = 2)
                require(bits.size == 2) { "diversity budget tier entry must be n:penalty: $entry" }
                val n = bits[0].toIntOrNull() ?: error("invalid n in diversity budget tier entry: $entry")
                require(n in 1..3) { "diversity budget tier n must be in 1..3: $entry" }
                config = config.withDiversityBudgetPenalty(n, bits[1].toDouble())
            }
        }
        if (mixedTierSpec != null) {
            for (entry in mixedTierSpec.split(';').map { it.trim() }.filter { it.isNotEmpty() }) {
                val bits = entry.split(':', limit = 2)
                require(bits.size == 2) { "mixed tier entry must be n:strength,initial,draw: $entry" }
                val n = bits[0].toIntOrNull() ?: error("invalid n in mixed tier entry: $entry")
                require(n in 1..3) { "mixed tier n must be in 1..3: $entry" }
                val tierParts = bits[1].split(',')
                require(tierParts.size == 3) { "mixed tier spec must be strength,initial,draw: $entry" }
                val tier = com.majiang.stacking.ShuffleTier(
                    tierParts[0].toDouble(),
                    tierParts[1].toInt(),
                    tierParts[2].toInt()
                )
                config = config.withMixedTier(n, tier)
            }
        }
        if (targetTierSpec != null) {
            for (entry in targetTierSpec.split(';').map { it.trim() }.filter { it.isNotEmpty() }) {
                val bits = entry.split(':', limit = 3)
                require(bits.size == 3) { "target tier entry must be TARGET:n:strength,initial,draw: $entry" }
                val target = runCatching { ShuffleTarget.valueOf(bits[0].uppercase()) }.getOrNull()
                    ?: error("unknown target in target tier entry: $entry")
                val n = bits[1].toIntOrNull() ?: error("invalid n in target tier entry: $entry")
                require(n in 1..3) { "target tier n must be in 1..3: $entry" }
                val tierParts = bits[2].split(',')
                require(tierParts.size == 3) { "target tier spec must be strength,initial,draw: $entry" }
                val tier = com.majiang.stacking.ShuffleTier(
                    tierParts[0].toDouble(),
                    tierParts[1].toInt(),
                    tierParts[2].toInt()
                )
                config = config.withTier(n, tier, target)
            }
        }

        val generator = ShuffleWallGenerator()
        val started = System.currentTimeMillis()
        val cells = ArrayList<Cell>()

        for (target in targets) {
            for (n in ns) {
                val cell = runCell(
                    target = target,
                    n = n,
                    pairs = pairs,
                    seedStart = seedStart,
                    opponentMode = opponentMode,
                    mixTargets = mixTargets,
                    config = config,
                    generator = generator
                )
                cells.add(cell)
            }
        }

        println(
            "targets=${targets.joinToString("+") { it.name }} opponent=$opponentMode mix=$mixTargets " +
                "pairs=$pairs seedStart=$seedStart seedEnd=${seedStart + pairs - 1} " +
                "diversity=${config.diversityInterference} " +
                "diversityBudgetPenalty=${config.diversityBudgetPenalty} " +
                "diversityBudgetTiers=${config.diversityBudgetPenalties} " +
                "mixedTiers=${config.mixedTiers} " +
                "elapsedMs=${System.currentTimeMillis() - started}"
        )
        println(
            "target,n,samples,scope,baseRate,shapedRate,ratio,baseTargetRate,shapedTargetRate," +
                "baseDrawRate,shapedDrawRate,baseAvgPoints,shapedAvgPoints," +
                "baseTargetTiles,shapedTargetTiles,baseTargetMelds,shapedTargetMelds," +
                "both,shapedOnly,baseOnly,neither,pairedDelta,ciLow,ciHigh," +
                "baseWinCount,shapedWinCount,baseTargetWinCount,shapedTargetWinCount"
        )
        for (c in cells) {
            for ((seat, stats) in c.stats.toSortedMap()) {
                printRow(c, "seat$seat", stats)
            }
            printAggregateRow(c)
        }
        if (targets.size > 1 || ns.size > 1) {
            println("# 以上每一行只描述该 target/n 格的样本；不是正式 10k 标定。")
        }
    }

    private fun runCell(
        target: ShuffleTarget,
        n: Int,
        pairs: Int,
        seedStart: Long,
        opponentMode: String,
        mixTargets: Boolean,
        config: ShuffleConfig,
        generator: ShuffleWallGenerator
    ): Cell {
        val stats = (0..3).associateWith { SeatStats() }
        var baseDraws = 0
        var shapedDraws = 0
        var aggregateDeltaSum = 0.0
        var aggregateDeltaSq = 0.0

        for (index in 0 until pairs) {
            val seed = seedStart + index
            val dealer = ((seed % 4 + 4) % 4).toInt()
            val casterSeats = casterSeats(dealer, n).toList()
            val casters = casterSeats.toSet()
            val targetBySeat = casterSeats.mapIndexed { i, seat ->
                seat to if (mixTargets) {
                    ShuffleTarget.entries[(target.ordinal + i) % ShuffleTarget.entries.size]
                } else {
                    target
                }
            }.toMap()

            val baselineWall = generator.generate(seed, dealer, emptyList(), config)
            val plans = targetBySeat.map { (seat, seatTarget) ->
                SeatShufflePlan(seat, ShuffleSelection(target = seatTarget))
            }
            val shapedWall = generator.generate(seed, dealer, plans, config)
            if (n == 4 && baselineWall != shapedWall) {
                error("n=4 must be tile-for-tile identical to base shuffle (seed=$seed, dealer=$dealer)")
            }

            val strategies = strategiesFor(target, casters, targetBySeat, opponentMode)
            val baseline = play(seed, dealer, baselineWall, strategies)
            // n=4 的牌山与策略完全相同，直接复用结果，避免无意义的第二局并保证严格配对。
            val shaped = if (n == 4) baseline else play(seed, dealer, shapedWall, strategies)

            if (baseline.draw) baseDraws++
            if (shaped.draw) shapedDraws++

            var shapedMinusBaseForPair = 0
            for (seat in casterSeats) {
                val seatTarget = targetBySeat.getValue(seat)
                val st = stats.getValue(seat)
                st.opportunities++
                val baseWin = baseline.winner == seat
                val shapedWin = shaped.winner == seat
                if (baseWin) st.baseWins++
                if (shapedWin) st.shapedWins++
                when {
                    baseWin && shapedWin -> st.bothWin++
                    !baseWin && shapedWin -> st.shapedOnly++
                    baseWin && !shapedWin -> st.baseOnly++
                    else -> st.neither++
                }
                if (baseWin && targetAchieved(baseline, seat, seatTarget)) st.baseTargetWins++
                if (shapedWin && targetAchieved(shaped, seat, seatTarget)) st.shapedTargetWins++
                st.basePoints += (baseline.finalScores[seat] - START_SCORE).toLong()
                st.shapedPoints += (shaped.finalScores[seat] - START_SCORE).toLong()
                st.baseTargetTiles += targetTileCount(baseline.initialHands[seat], seatTarget)
                st.shapedTargetTiles += targetTileCount(shaped.initialHands[seat], seatTarget)
                st.baseTargetMelds += targetMeldTileCount(baseline.finalMelds[seat], seatTarget)
                st.shapedTargetMelds += targetMeldTileCount(shaped.finalMelds[seat], seatTarget)
                shapedMinusBaseForPair += (if (shapedWin) 1 else 0) - (if (baseWin) 1 else 0)
            }
            val pairDelta = shapedMinusBaseForPair.toDouble() / n
            aggregateDeltaSum += pairDelta
            aggregateDeltaSq += pairDelta * pairDelta
        }

        val meanDelta = aggregateDeltaSum / pairs
        val variance = if (pairs > 1) {
            ((aggregateDeltaSq - pairs * meanDelta * meanDelta) / (pairs - 1)).coerceAtLeast(0.0)
        } else {
            0.0
        }
        val se = sqrt(variance / pairs)
        return Cell(
            target = target,
            n = n,
            pairs = pairs,
            opponentMode = opponentMode,
            mixTargets = mixTargets,
            stats = stats,
            baseDraws = baseDraws,
            shapedDraws = shapedDraws,
            aggregateDelta = meanDelta,
            aggregateCiLow = meanDelta - 1.96 * se,
            aggregateCiHigh = meanDelta + 1.96 * se
        )
    }

    private fun printRow(c: Cell, scope: String, st: SeatStats) {
        val opportunities = st.opportunities
        if (opportunities == 0) return
        val baseRate = st.baseWins.toDouble() / opportunities
        val shapedRate = st.shapedWins.toDouble() / opportunities
        val ratio = if (st.baseWins > 0) shapedRate / baseRate else Double.NaN
        val se = sqrt((st.shapedOnly + st.baseOnly).toDouble()) / opportunities
        val delta = (st.shapedWins - st.baseWins).toDouble() / opportunities
        println(
            listOf(
                c.target.name,
                c.n,
                opportunities,
                scope,
                fmt(baseRate),
                fmt(shapedRate),
                fmtOrNa(ratio),
                fmt(st.baseTargetWins.toDouble() / opportunities),
                fmt(st.shapedTargetWins.toDouble() / opportunities),
                fmt(c.baseDraws.toDouble() / c.pairs),
                fmt(c.shapedDraws.toDouble() / c.pairs),
                fmt(st.basePoints.toDouble() / opportunities),
                fmt(st.shapedPoints.toDouble() / opportunities),
                fmt(st.baseTargetTiles.toDouble() / opportunities),
                fmt(st.shapedTargetTiles.toDouble() / opportunities),
                fmt(st.baseTargetMelds.toDouble() / opportunities),
                fmt(st.shapedTargetMelds.toDouble() / opportunities),
                st.bothWin,
                st.shapedOnly,
                st.baseOnly,
                st.neither,
                fmt(delta),
                fmt(delta - 1.96 * se),
                fmt(delta + 1.96 * se),
                st.baseWins,
                st.shapedWins,
                st.baseTargetWins,
                st.shapedTargetWins
            ).joinToString(",")
        )
    }

    private fun printAggregateRow(c: Cell) {
        val total = c.stats.values.sumOf { it.opportunities }
        // all 行与逐席行必须使用同一分母：每个施术者每对贡献一次机会。
        check(total == c.pairs * c.n) {
            "aggregate opportunities ($total) must equal pairs × n (${c.pairs} × ${c.n})"
        }
        val baseWins = c.stats.values.sumOf { it.baseWins }
        val shapedWins = c.stats.values.sumOf { it.shapedWins }
        val baseTarget = c.stats.values.sumOf { it.baseTargetWins }
        val shapedTarget = c.stats.values.sumOf { it.shapedTargetWins }
        val basePoints = c.stats.values.sumOf { it.basePoints }
        val shapedPoints = c.stats.values.sumOf { it.shapedPoints }
        val baseTargetTiles = c.stats.values.sumOf { it.baseTargetTiles }
        val shapedTargetTiles = c.stats.values.sumOf { it.shapedTargetTiles }
        val baseTargetMelds = c.stats.values.sumOf { it.baseTargetMelds }
        val shapedTargetMelds = c.stats.values.sumOf { it.shapedTargetMelds }
        val both = c.stats.values.sumOf { it.bothWin }
        val shapedOnly = c.stats.values.sumOf { it.shapedOnly }
        val baseOnly = c.stats.values.sumOf { it.baseOnly }
        val neither = c.stats.values.sumOf { it.neither }
        val baseRate = baseWins.toDouble() / total
        val shapedRate = shapedWins.toDouble() / total
        val ratio = if (baseWins > 0) shapedWins.toDouble() / baseWins else Double.NaN
        println(
            listOf(
                c.target.name,
                c.n,
                total,
                "all",
                fmt(baseRate),
                fmt(shapedRate),
                fmtOrNa(ratio),
                fmt(baseTarget.toDouble() / total),
                fmt(shapedTarget.toDouble() / total),
                fmt(c.baseDraws.toDouble() / c.pairs),
                fmt(c.shapedDraws.toDouble() / c.pairs),
                fmt(basePoints.toDouble() / total),
                fmt(shapedPoints.toDouble() / total),
                fmt(baseTargetTiles.toDouble() / total),
                fmt(shapedTargetTiles.toDouble() / total),
                fmt(baseTargetMelds.toDouble() / total),
                fmt(shapedTargetMelds.toDouble() / total),
                both,
                shapedOnly,
                baseOnly,
                neither,
                fmt(c.aggregateDelta),
                fmt(c.aggregateCiLow),
                fmt(c.aggregateCiHigh),
                baseWins,
                shapedWins,
                baseTarget,
                shapedTarget
            ).joinToString(",")
        )
    }

    private fun casterSeats(dealer: Int, n: Int): Set<Int> =
        (1..n).map { (dealer + it) % 4 }.toSet()

    private fun strategiesFor(
        target: ShuffleTarget,
        casters: Set<Int>,
        targetBySeat: Map<Int, ShuffleTarget>,
        opponentMode: String
    ): Map<Int, AIStrategy> {
        if (opponentMode == "all-basic") {
            return (0..3).associateWith { BasicAIStrategy() }
        }
        return (0..3).associateWith { seat ->
            when {
                seat in casters -> targetStrategy(targetBySeat.getValue(seat))
                opponentMode == "target" -> targetStrategy(targetBySeat[seat] ?: target)
                opponentMode == "basic" -> BasicAIStrategy()
                else -> error("unknown opponent mode: $opponentMode")
            }
        }
    }

    private fun targetStrategy(target: ShuffleTarget): AIStrategy = when (target) {
        ShuffleTarget.CHINITSU_MAN -> TargetAwareAi.SuitGoalAi(Suit.MAN)
        ShuffleTarget.CHINITSU_SOU -> TargetAwareAi.SuitGoalAi(Suit.SOU)
        ShuffleTarget.CHINITSU_PIN -> TargetAwareAi.SuitGoalAi(Suit.PIN)
        ShuffleTarget.DAISANGEN -> TargetAwareAi.HonorTripletGoalAi(TargetAwareAi.DAISANGEN_INDICES)
        ShuffleTarget.DAISUUSHII -> TargetAwareAi.HonorTripletGoalAi(TargetAwareAi.DAISUUSHII_INDICES)
        ShuffleTarget.KOKUSHI -> TargetAwareAi.KokushiGoalAi
    }

    private fun play(
        seed: Long,
        dealer: Int,
        wall: List<Tile>,
        strategies: Map<Int, AIStrategy>
    ): Outcome {
        require(wall.size == 136)
        require(wall.toSet() == TilePool.buildStandardSet().toSet())
        val bus = GameEventBus()
        var tsumo: GameEvent.TsumoWin? = null
        var ron: GameEvent.RonWin? = null
        var draw: GameEvent.DrawGame? = null
        var ended = false
        bus.subscribe<GameEvent.TsumoWin> { tsumo = it }
        bus.subscribe<GameEvent.RonWin> { ron = it }
        bus.subscribe<GameEvent.DrawGame> { draw = it }
        bus.subscribe<GameEvent.RoundEnded> { ended = true }

        val state = GameState().apply {
            dealerIndex = dealer
            roundWind = 1
            roundNumber = 0
            currentPlayer = dealer
            phase = GamePhase.STACKING
        }
        val coordinator = ApplicationCoordinator(
            initialState = state,
            eventBus = bus,
            autoPlayAi = true,
            deferAutomaticActions = true,
            random = Random(seed xor 0x5DEECE66DL)
        )
        coordinator.initializeMatch(
            humanIndex = -1,
            aiStrategies = (0..3).associateWith { AIPlayer(it, strategies.getValue(it)) }
        )
        val roundId = coordinator.activeRound!!.roundId
        val result = coordinator.executeDealWithPreparation(roundId, wall)
        check(result is com.majiang.state.ActionResult.Accepted) { "deal rejected: $result" }

        val afterDeal = coordinator.activeRound!!.readState()
        val initialHands = (0..3).map { seat ->
            afterDeal.players[seat].closedTiles + listOfNotNull(afterDeal.players[seat].drawnTile)
        }

        var guard = 0
        while (!ended && coordinator.activeRound?.state?.phase != GamePhase.ROUND_END && guard++ < 10_000) {
            val advanced = coordinator.advanceAutomaticActions()
            if (advanced !is com.majiang.state.ActionResult.Accepted) break
        }
        check(guard < 10_000) { "round did not terminate (seed=$seed)" }

        val final = coordinator.lastEndedRoundSnapshot ?: coordinator.activeRound!!.readState()
        val finalHands = (0..3).map { seat ->
            final.players[seat].closedTiles + listOfNotNull(final.players[seat].drawnTile)
        }
        val finalMelds = (0..3).map { seat -> final.players[seat].melds.toList() }
        return Outcome(
            winner = tsumo?.player ?: ron?.winner,
            yaku = tsumo?.yakuList ?: ron?.yakuList ?: emptyList(),
            draw = draw != null,
            finalScores = final.scores.toList(),
            initialHands = initialHands,
            finalHands = finalHands,
            finalMelds = finalMelds
        )
    }

    private fun targetAchieved(outcome: Outcome, seat: Int, target: ShuffleTarget): Boolean {
        if (outcome.winner != seat) return false
        if (outcome.yaku.none { it.contains(DISPLAY.getValue(target)) }) return false
        return suitMatches(outcome.finalHands[seat], outcome.finalMelds[seat], target)
    }

    /**
     * 清一色目标额外核对和牌者最终手牌/副露全为指定花色。
     */
    private fun suitMatches(hand: List<Tile>, melds: List<Meld>, target: ShuffleTarget): Boolean {
        val required = when (target) {
            ShuffleTarget.CHINITSU_MAN -> Suit.MAN
            ShuffleTarget.CHINITSU_SOU -> Suit.SOU
            ShuffleTarget.CHINITSU_PIN -> Suit.PIN
            else -> return true
        }
        val suits = buildSet {
            hand.forEach { add(it.suit) }
            melds.flatMapTo(this) { meld -> meld.tiles.map { it.suit } }
        }
        return suits.size == 1 && suits.first() == required
    }

    private fun targetIndices(target: ShuffleTarget): Set<Int> = when (target) {
        ShuffleTarget.CHINITSU_MAN -> (0..8).toSet()
        ShuffleTarget.CHINITSU_SOU -> (9..17).toSet()
        ShuffleTarget.CHINITSU_PIN -> (18..26).toSet()
        ShuffleTarget.DAISANGEN -> setOf(31, 32, 33)
        ShuffleTarget.KOKUSHI -> setOf(0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33)
        ShuffleTarget.DAISUUSHII -> setOf(27, 28, 29, 30)
    }

    private fun targetTileCount(tiles: List<Tile>, target: ShuffleTarget): Long {
        val indices = targetIndices(target)
        return tiles.count { it.typeIndex in indices }.toLong()
    }

    private fun targetMeldTileCount(melds: List<Meld>, target: ShuffleTarget): Long {
        val indices = targetIndices(target)
        return melds.sumOf { meld -> meld.tiles.count { it.typeIndex in indices }.toLong() }
    }

    private fun fmt(v: Double): String = String.format(java.util.Locale.ROOT, "%.4f", v)

    private fun fmtOrNa(v: Double): String =
        if (v.isNaN() || v.isInfinite()) "n/a" else fmt(v)
}

fun main(args: Array<String>) {
    ShuffleMonteCarloMain.main(args)
}
