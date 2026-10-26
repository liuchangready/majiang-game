package com.majiang.rules

import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.PlayerHand
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.settlement.SettlementInput
import com.majiang.settlement.SettlementService
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.util.Locale
import kotlin.random.Random

/**
 * P1.2 golden exporter.
 *
 * This file is test-only and is the only V4 change for the scoring migration. It reads
 * actual V4 `WinningScoreEvaluator` / `FuCalculator` / `ScoreCalculator` /
 * `SettlementService` results and writes `tests/v5-golden/scoring-v1.json`.
 *
 * It is inert during ordinary test runs. Enable with `V5_GOLDEN_EXPORT=1` (the Gradle
 * test JVM inherits the environment variable) so a normal `:engine:test` does not rewrite
 * the repository.
 */
class V5ScoringGoldenExporterTest {

    @Test
    fun exportScoringGoldenSamples() {
        if (System.getenv("V5_GOLDEN_EXPORT") != "1" && System.getProperty("v5.golden.export") != "true") {
            return
        }

        val wins = buildWinSamples()
        val noWins = buildNoWinSamples()
        val fuSamples = buildFuSamples()
        val doraSamples = buildDoraSamples()
        val settlementSamples = buildSettlementSamples()
        val scoreSamples = buildScoreSamples()
        val random = buildRandomSamples(10_000)

        val coveredCodes = wins.flatMap { sample ->
            val r = evaluator.explain(
                sample.hand,
                sample.context,
                sample.dora,
                sample.ura,
                isDealer = sample.context.seatWind == 1,
                honba = sample.honba,
                riichiSticks = sample.sticks
            ) ?: error("V4 exporter sample ${sample.id} did not win")
            r.yakuEntries.map { it.code }
        }.toSet()
        val missing = Yaku.values().map { it.code }.filter { it !in coveredCodes }
        assertTrue(missing.isEmpty(), "V4 exporter is missing yaku coverage: $missing")
        assertEquals(42, coveredCodes.size, "all 42 frozen yaku codes must be produced by actual samples")
        assertEquals(10_000, random.size, "random differential target is exactly 10,000")

        val ids = (wins.map { it.id } + noWins.map { it.id } + fuSamples.map { it.id } +
                doraSamples.map { it.id } + settlementSamples.map { it.id } +
                scoreSamples.map { it.id } + random.map { it.id })
        assertEquals(ids.size, ids.toSet().size, "sample ids must be unique")

        val root = obj(
            "schema" to j("v5-scoring-golden-v1"),
            "generator" to j("engine/src/test/kotlin/com/majiang/rules/V5ScoringGoldenExporterTest.kt"),
            "generatorVersion" to j(1),
            "v4Commit" to j(v4Commit()),
            "rulesetVersion" to j(1),
            "generatedByCommand" to j(
                "\$env:V5_GOLDEN_EXPORT='1'; .\\gradlew.bat :engine:test --tests \"com.majiang.rules.V5ScoringGoldenExporterTest\" --console=plain"
            ),
            "extractionStatus" to j("exported"),
            "unexportedSamples" to j("none in scoring-v1; see the P1.2 report for rules not yet ported"),
            "exportedAtNote" to j("generated from actual V4 evaluator calls; no expected value is hand-written"),
            "v4RulesSourceNote" to j("engine/src/main/kotlin/com/majiang/rules and settlement are the V4 production sources; the v4Commit above is the exact tree used"),
            "generationResult" to obj(
                "winSamples" to j(wins.size),
                "noWinSamples" to j(noWins.size),
                "fuSamples" to j(fuSamples.size),
                "doraSamples" to j(doraSamples.size),
                "settlementSamples" to j(settlementSamples.size),
                "scoreSamples" to j(scoreSamples.size),
                "randomSamples" to j(random.size),
                "totalSamples" to j(wins.size + noWins.size + fuSamples.size + doraSamples.size + settlementSamples.size + scoreSamples.size + random.size),
                "exporterTestResult" to j("PASSED")
            ),
            "winSamples" to arr(wins.map { winJson(it) }),
            "noWinSamples" to arr(noWins.map { noWinJson(it) }),
            "fuSamples" to arr(fuSamples.map { fuJsonSample(it) }),
            "doraSamples" to arr(doraSamples.map { doraJsonSample(it) }),
            "settlementSamples" to arr(settlementSamples.map { settlementJsonSample(it) }),
            "scoreSamples" to arr(scoreSamples.map { scoreJsonSample(it) }),
            "randomMeta" to randomMeta(random),
            "randomSamples" to J.Array(random.map { randomJson(it) }, compact = true)
        )

        val path = repoRoot().resolve("tests/v5-golden/scoring-v1.json")
        Files.createDirectories(path.parent)
        Files.write(path, root.render().toByteArray(StandardCharsets.UTF_8))
        assertTrue(Files.size(path) > 100_000L, "golden file looks unexpectedly small: ${Files.size(path)} bytes")
        assertTrue(Files.exists(path), "golden file was not written: $path")
    }

    private val evaluator = WinningScoreEvaluator()
    private val scoreCalculator = ScoreCalculator.INSTANCE

    // ---------------------------------------------------------------------
    // Sample construction
    // ---------------------------------------------------------------------

    private data class WinSample(
        val id: String,
        val note: String,
        val tags: List<String>,
        val hand: PlayerHand,
        val context: YakuEvaluator.WinContext,
        val dora: List<Tile> = emptyList(),
        val ura: List<Tile> = emptyList(),
        val honba: Int = 0,
        val sticks: Int = 0
    )

    private data class NoWinSample(
        val id: String,
        val note: String,
        val hand: PlayerHand,
        val context: YakuEvaluator.WinContext,
        val dora: List<Tile> = emptyList(),
        val ura: List<Tile> = emptyList(),
        val extraHan: Int = 0
    )

    private fun buildWinSamples(): List<WinSample> {
        val out = mutableListOf<WinSample>()

        fun add(
            id: String,
            note: String,
            tags: List<String>,
            hand: HandBuilder,
            winType: Int,
            tsumo: Boolean,
            closed: Boolean,
            seat: Int,
            round: Int,
            dora: List<Tile> = emptyList(),
            ura: List<Tile> = emptyList(),
            riichi: Boolean = false,
            doubleRiichi: Boolean = false,
            ippatsu: Boolean = false,
            rinshan: Boolean = false,
            haitei: Boolean = false,
            houtei: Boolean = false,
            chankan: Boolean = false,
            tenhou: Boolean = false,
            chiihou: Boolean = false,
            honba: Int = 0,
            sticks: Int = 0
        ) {
            val win = hand.tileOf(winType)
            val context = YakuEvaluator.WinContext(
                winTile = win,
                isTsumo = tsumo,
                isClosed = closed,
                seatWind = seat,
                roundWind = round,
                isRiichi = riichi,
                isDoubleRiichi = doubleRiichi,
                isIppatsu = ippatsu,
                isRinshan = rinshan,
                isHaitei = haitei,
                isHoutei = houtei,
                isChankan = chankan,
                isTenhou = tenhou,
                isChiihou = chiihou
            )
            out.add(WinSample(id, note, tags, hand.build(), context, dora, ura, honba, sticks))
        }

        fun pinfuHand(): HandBuilder = HandBuilder()
            .seqM(1).seqP(4).seqS(7).seqS(2).pairP(5)

        fun tanyaoHand(): HandBuilder = HandBuilder()
            .seqM(2).seqM(3).seqP(4).seqS(6).pairP(5)

        add(
            "W-001", "riichi + pinfu ron", listOf("yaku:RIICHI"),
            pinfuHand(), 12, tsumo = false, closed = true, seat = 2, round = 1, riichi = true
        )
        add(
            "W-002", "riichi + ippatsu + pinfu", listOf("yaku:IPPATSU"),
            pinfuHand(), 12, tsumo = false, closed = true, seat = 2, round = 1,
            riichi = true, ippatsu = true
        )
        add(
            "W-003", "closed tsumo tanyao pinfu", listOf("yaku:MENZEN_TSUMO", "yaku:TANYAO"),
            tanyaoHand(), 3, tsumo = true, closed = true, seat = 2, round = 2
        )
        add(
            "W-004", "tanyao pinfu ron", listOf("yaku:TANYAO"),
            tanyaoHand(), 3, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-005", "pinfu only", listOf("yaku:PINFU"),
            pinfuHand(), 12, tsumo = false, closed = true, seat = 2, round = 1
        )
        add(
            "W-006", "iipeiko", listOf("yaku:IIPEIKO"),
            HandBuilder().m(1).m(1).m(2).m(2).m(3).m(3).seqP(4).seqS(7).pairP(5),
            2, tsumo = false, closed = true, seat = 2, round = 1
        )
        add(
            "W-007", "white dragon triplet", listOf("yaku:YAKUHAI_HAKU"),
            HandBuilder().tripM(1).seqP(2).seqP(5).tripDragon(1).pairS(9),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-008", "green dragon triplet", listOf("yaku:YAKUHAI_HATSU"),
            HandBuilder().tripM(1).seqP(2).seqP(5).tripDragon(2).pairS(9),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-009", "red dragon triplet", listOf("yaku:YAKUHAI_CHUN"),
            HandBuilder().tripM(1).seqP(2).seqP(5).tripDragon(3).pairS(9),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-010", "round wind triplet", listOf("yaku:YAKUHAI_BAKAZE"),
            HandBuilder().tripWind(1).seqM(1).seqP(4).seqS(7).pairP(5),
            0, tsumo = false, closed = true, seat = 2, round = 1
        )
        add(
            "W-011", "seat wind triplet", listOf("yaku:YAKUHAI_JIKAZE"),
            HandBuilder().tripWind(1).seqM(1).seqP(4).seqS(7).pairP(5),
            0, tsumo = false, closed = true, seat = 1, round = 2
        )
        add(
            "W-012", "haitei", listOf("yaku:HAITEI"),
            tanyaoHand(), 3, tsumo = false, closed = true, seat = 2, round = 2, haitei = true
        )
        add(
            "W-013", "houtei", listOf("yaku:HOUTEI"),
            tanyaoHand(), 3, tsumo = false, closed = true, seat = 2, round = 2, houtei = true
        )
        add(
            "W-014", "rinshan", listOf("yaku:RINSHAN"),
            tanyaoHand(), 3, tsumo = true, closed = true, seat = 2, round = 2, rinshan = true
        )
        add(
            "W-015", "chankan", listOf("yaku:CHANKAN"),
            tanyaoHand(), 3, tsumo = false, closed = true, seat = 2, round = 2, chankan = true
        )
        add(
            "W-016", "double riichi", listOf("yaku:DOUBLE_RIICHI"),
            pinfuHand(), 12, tsumo = false, closed = true, seat = 2, round = 1,
            riichi = true, doubleRiichi = true
        )
        add(
            "W-017", "chanta", listOf("yaku:CHANTA"),
            HandBuilder().seqM(1).seqP(7).tripS(1).tripWind(1).pairM(9),
            9, tsumo = false, closed = true, seat = 3, round = 2
        )
        add(
            "W-018", "ittsu", listOf("yaku:ITTSU"),
            HandBuilder().seqM(1).seqM(4).seqM(7).seqP(2).pairS(5),
            21, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-019", "san shoku doujun", listOf("yaku:SAN_SHOKU_DOUJUN"),
            HandBuilder().seqM(1).seqS(1).seqP(1).seqM(4).pairS(5),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-020", "san shoku doukou", listOf("yaku:SAN_SHOKU_DOUKOU"),
            HandBuilder().tripM(1).tripS(1).tripP(1).seqM(4).pairS(5),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-021", "toitoi with open pon", listOf("yaku:TOITOI"),
            HandBuilder().tripS(2).tripP(3).tripWind(1).pairS(5).pon(0),
            10, tsumo = false, closed = false, seat = 2, round = 2
        )
        add(
            "W-022", "san ankou", listOf("yaku:SAN_ANKOU"),
            HandBuilder().tripM(1).tripS(2).tripP(3).seqM(4).pairM(5),
            3, tsumo = true, closed = true, seat = 2, round = 2
        )
        add(
            "W-023", "san kantsu", listOf("yaku:SAN_KANTSU"),
            HandBuilder().seqM(1).pairP(5)
                .closedKan(9).openKan(19).openKan(26),
            1, tsumo = false, closed = false, seat = 2, round = 2
        )
        add(
            "W-024", "shousangen", listOf("yaku:SHOUSANGEN", "yaku:YAKUHAI_HAKU", "yaku:YAKUHAI_HATSU"),
            HandBuilder().tripDragon(1).tripDragon(2).pairDragon(3).seqM(1).seqP(4),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-025", "honroutou", listOf("yaku:HONROUTOU"),
            HandBuilder().tripM(1).tripP(9).tripS(1).tripWind(1).pairS(9),
            0, tsumo = false, closed = true, seat = 3, round = 2
        )
        add(
            "W-026", "chiitoitsu", listOf("yaku:CHIITOITSU"),
            HandBuilder().pairM(1).pairM(2).pairM(3).pairP(4).pairP(5).pairS(6).pairWind(1),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-027", "honitsu + ittsu", listOf("yaku:HONITSU"),
            HandBuilder().seqM(1).seqM(4).seqM(7).tripWind(1).pairM(5),
            0, tsumo = false, closed = true, seat = 3, round = 2
        )
        add(
            "W-028", "junchan", listOf("yaku:JUNCHAN"),
            HandBuilder().seqM(1).seqM(7).tripP(1).tripS(9).pairM(1),
            1, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-029", "ryanpeikou", listOf("yaku:RYANPEIKOU"),
            HandBuilder().m(1).m(1).m(2).m(2).m(3).m(3)
                .p(4).p(4).p(5).p(5).p(6).p(6).pairS(7),
            2, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-030", "chinitsu", listOf("yaku:CHINITSU"),
            HandBuilder().tripM(1).seqM(2).seqM(5).seqM(7).pairM(5),
            1, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-031", "kokushi", listOf("yaku:KOKUSHI"),
            HandBuilder().m(1).m(1).m(9).s(1).s(9).p(1).p(9)
                .wind(1).wind(2).wind(3).wind(4).dragon(1).dragon(2).dragon(3),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-032", "suuankou", listOf("yaku:SUUANKOU"),
            HandBuilder().tripM(1).tripS(2).tripP(3).tripS(4).pairM(5),
            12, tsumo = true, closed = true, seat = 2, round = 2
        )
        add(
            "W-033", "daisangen", listOf("yaku:DAISANGEN"),
            HandBuilder().tripDragon(1).tripDragon(2).tripDragon(3).seqM(1).pairP(5),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-034", "shousuushii", listOf("yaku:SHOUSUUSHII"),
            HandBuilder().tripWind(1).tripWind(2).tripWind(3).pairWind(4).seqM(1),
            0, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-035", "daisuushii", listOf("yaku:DAISUUSHII"),
            HandBuilder().tripWind(1).tripWind(2).tripWind(3).tripWind(4).pairM(5),
            4, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-036", "tsuuiisou", listOf("yaku:TSUUIISOU"),
            HandBuilder().tripWind(1).tripWind(2).tripDragon(1).tripDragon(2).pairDragon(3),
            27, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-037", "ryuuiisou", listOf("yaku:RYUUIISOU"),
            HandBuilder().seqS(2).seqS(2).tripS(6).tripS(8).pairDragon(2),
            10, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-038", "chinroutou with open pon", listOf("yaku:CHINROUTOU"),
            HandBuilder().tripM(9).tripP(1).tripP(9).pairS(1).pon(0),
            9, tsumo = false, closed = false, seat = 2, round = 2
        )
        add(
            "W-039", "churenpoutou",
            listOf("yaku:CHURENPOUTOU"),
            HandBuilder().m(1).m(1).m(1).m(2).m(3).m(4).m(5).m(6).m(7).m(8)
                .m(9).m(9).m(9).m(5),
            4, tsumo = false, closed = true, seat = 2, round = 2
        )
        add(
            "W-040", "suukantsu",
            listOf("yaku:SUUKANTSU"),
            HandBuilder().pairM(5)
                .closedKan(0).openKan(10).openKan(20).openKan(27),
            4, tsumo = false, closed = false, seat = 2, round = 2
        )
        add(
            "W-041", "tenhou", listOf("yaku:TENHOU"),
            tanyaoHand(), 3, tsumo = true, closed = true, seat = 1, round = 1, tenhou = true
        )
        add(
            "W-042", "chiihou", listOf("yaku:CHIIHOU"),
            tanyaoHand(), 3, tsumo = true, closed = true, seat = 2, round = 1, chiihou = true
        )
        add(
            "W-043", "daisuushii + tsuuiisou double yakuman",
            listOf("yaku:DAISUUSHII", "yaku:TSUUIISOU"),
            HandBuilder().tripWind(1).tripWind(2).tripWind(3).tripWind(4).pairDragon(1),
            27, tsumo = false, closed = true, seat = 2, round = 2
        )
        val thirteen = pinfuHand()
        val thirteenWin = thirteen.tileOf(12)
        thirteen.removeTile(thirteenWin)
        out.add(
            WinSample(
                "W-044", "13-tile hand with win tile added by evaluator", listOf("yaku:PINFU"),
                thirteen.build(),
                YakuEvaluator.WinContext(thirteenWin, isTsumo = false, isClosed = true, seatWind = 2, roundWind = 1)
            )
        )
        add(
            "W-DORA-1", "pinfu with one dora and one ura, non-riichi",
            listOf("yaku:PINFU"),
            pinfuHand(), 12, tsumo = false, closed = true, seat = 2, round = 1,
            dora = listOf(Tile(Suit.PIN, 3, 0)), ura = listOf(Tile(Suit.SOU, 1, 0))
        )
        add(
            "W-DORA-2", "riichi pinfu with one dora and one ura",
            listOf("yaku:RIICHI", "yaku:PINFU"),
            pinfuHand(), 12, tsumo = false, closed = true, seat = 2, round = 1,
            dora = listOf(Tile(Suit.PIN, 3, 0)), ura = listOf(Tile(Suit.SOU, 1, 0)),
            riichi = true
        )
        add(
            "W-DORA-3", "red five pair plus dora indicator",
            listOf("yaku:TANYAO", "yaku:PINFU"),
            HandBuilder().seqM(2).seqM(3).seqP(6).seqS(6).p(5, 3).p(5, 0),
            3, tsumo = false, closed = true, seat = 2, round = 2,
            dora = listOf(Tile(Suit.PIN, 4, 0))
        )
        add(
            "W-SETTLE-1", "pinfu ron with honba and riichi sticks",
            listOf("yaku:PINFU"),
            pinfuHand(), 12, tsumo = false, closed = true, seat = 2, round = 1,
            honba = 2, sticks = 3
        )
        return out
    }

    private fun buildNoWinSamples(): List<NoWinSample> {
        val out = mutableListOf<NoWinSample>()

        fun add(
            id: String,
            note: String,
            hand: HandBuilder,
            winType: Int,
            tsumo: Boolean,
            closed: Boolean,
            seat: Int,
            round: Int,
            dora: List<Tile> = emptyList(),
            ura: List<Tile> = emptyList(),
            extraHan: Int = 0
        ) {
            val context = YakuEvaluator.WinContext(
                winTile = hand.tileOf(winType),
                isTsumo = tsumo,
                isClosed = closed,
                seatWind = seat,
                roundWind = round
            )
            out.add(NoWinSample(id, note, hand.build(), context, dora, ura, extraHan))
        }

        add(
            "N-001", "open hand with dora but no yaku",
            HandBuilder().seqM(4).seqP(7).p(5).p(5).seqS(1).chi(0, 1, 2),
            22, tsumo = false, closed = false, seat = 2, round = 1, extraHan = 10
        )
        add(
            "N-002", "open hand with red five but no yaku",
            HandBuilder().p(5, 3).p(6).p(7).seqM(4).s(1).s(2).s(3).pairS(7).chi(0, 1, 2),
            15, tsumo = false, closed = false, seat = 2, round = 1
        )
        add(
            "N-003", "closed hand with no yaku because win tile is a middle wait",
            HandBuilder().seqM(1).seqM(4).seqP(7).seqS(1).pairP(9),
            1, tsumo = false, closed = true, seat = 2, round = 1
        )
        add(
            "N-004", "not a complete hand",
            HandBuilder().seqM(1).seqP(4).seqS(7).p(1).p(3).p(5).p(7).p(9),
            0, tsumo = false, closed = true, seat = 2, round = 1
        )
        return out
    }

    private data class FuSample(
        val id: String,
        val note: String,
        val decomposition: HandDecomposer.Decomposition,
        val winTile: Tile,
        val isTsumo: Boolean,
        val isClosed: Boolean,
        val seatWind: Int,
        val roundWind: Int,
        val openMelds: List<Meld>
    )

    private fun buildFuSamples(): List<FuSample> {
        val out = mutableListOf<FuSample>()

        fun add(
            id: String,
            note: String,
            decomposition: HandDecomposer.Decomposition,
            winType: Int,
            tsumo: Boolean,
            closed: Boolean,
            seat: Int,
            round: Int,
            openMelds: List<Meld> = emptyList()
        ) {
            out.add(
                FuSample(
                    id,
                    note,
                    decomposition,
                    Tile.fromTypeIndex(winType, 0),
                    tsumo,
                    closed,
                    seat,
                    round,
                    openMelds
                )
            )
        }

        add(
            "F-001", "chiitoitsu fixed 25 fu",
            HandDecomposer.Decomposition(emptyList(), -1, HandDecomposer.DecompPattern.CHIITOITSU),
            16, true, true, 1, 1
        )
        add(
            "F-002", "kokushi fixed 30 fu",
            HandDecomposer.Decomposition(emptyList(), 0, HandDecomposer.DecompPattern.KOKUSHI),
            0, false, true, 1, 1
        )
        add(
            "F-003", "pinfu tsumo fixed 20 fu",
            HandDecomposer.Decomposition(
                listOf(seq(0, 1, 2), seq(21, 22, 23), seq(15, 16, 17), seq(10, 11, 12)),
                22, HandDecomposer.DecompPattern.REGULAR
            ),
            12, true, true, 2, 1
        )
        add(
            "F-004", "open pinfu ron floor 30 fu",
            HandDecomposer.Decomposition(
                listOf(seq(0, 1, 2), seq(19, 20, 21)),
                10, HandDecomposer.DecompPattern.REGULAR
            ),
            21, false, false, 1, 2,
            listOf(
                Meld(MeldType.CHI, listOf(Tile(Suit.MAN, 4), Tile(Suit.MAN, 5), Tile(Suit.MAN, 6)), 1, Tile(Suit.MAN, 4)),
                Meld(MeldType.CHI, listOf(Tile(Suit.SOU, 7), Tile(Suit.SOU, 8), Tile(Suit.SOU, 9)), 1, Tile(Suit.SOU, 7))
            )
        )
        add(
            "F-005", "synthetic 68 fu rounded to 70",
            HandDecomposer.Decomposition(
                listOf(trip(0), trip(16), seq(12, 13, 14), seq(3, 4, 5)),
                19, HandDecomposer.DecompPattern.REGULAR
            ),
            4, true, false, 1, 1,
            listOf(
                Meld(
                    MeldType.CLOSED_KAN,
                    listOf(Tile(Suit.DRAGON, 3, 0), Tile(Suit.DRAGON, 3, 1), Tile(Suit.DRAGON, 3, 2), Tile(Suit.DRAGON, 3, 3)),
                    0,
                    Tile(Suit.DRAGON, 3, 0)
                )
            )
        )
        add(
            "F-006", "double wind pair adds 4 fu",
            HandDecomposer.Decomposition(
                listOf(seq(0, 1, 2), seq(21, 22, 23), seq(15, 16, 17), seq(10, 11, 12)),
                27, HandDecomposer.DecompPattern.REGULAR
            ),
            12, false, true, 1, 1
        )
        add(
            "F-007", "open/closed/added kan fu 76 rounded to 80",
            HandDecomposer.Decomposition(
                listOf(seq(0, 1, 2), seq(21, 22, 23), seq(15, 16, 17), seq(10, 11, 12)),
                22, HandDecomposer.DecompPattern.REGULAR
            ),
            12, false, false, 2, 2,
            listOf(
                Meld(MeldType.OPEN_KAN, listOf(Tile(Suit.SOU, 1, 0), Tile(Suit.SOU, 1, 1), Tile(Suit.SOU, 1, 2), Tile(Suit.SOU, 1, 3)), 1, Tile(Suit.SOU, 1, 0)),
                Meld(MeldType.CLOSED_KAN, listOf(Tile(Suit.PIN, 9, 0), Tile(Suit.PIN, 9, 1), Tile(Suit.PIN, 9, 2), Tile(Suit.PIN, 9, 3)), 0, Tile(Suit.PIN, 9, 0)),
                Meld(MeldType.ADDED_KAN, listOf(Tile(Suit.MAN, 3, 0), Tile(Suit.MAN, 3, 1), Tile(Suit.MAN, 3, 2), Tile(Suit.MAN, 3, 3)), 1, Tile(Suit.MAN, 3, 0))
            )
        )
        add(
            "F-008", "penchan wait 2 fu",
            HandDecomposer.Decomposition(
                listOf(seq(0, 1, 2), seq(21, 22, 23), seq(15, 16, 17), seq(10, 11, 12)),
                22, HandDecomposer.DecompPattern.REGULAR
            ),
            2, false, true, 2, 2
        )
        add(
            "F-009", "single wait 2 fu",
            HandDecomposer.Decomposition(
                listOf(seq(0, 1, 2), seq(21, 22, 23), seq(15, 16, 17), seq(10, 11, 12)),
                22, HandDecomposer.DecompPattern.REGULAR
            ),
            22, false, true, 2, 2
        )
        return out
    }

    private data class DoraSample(
        val id: String,
        val note: String,
        val hand: PlayerHand,
        val dora: List<Tile>,
        val ura: List<Tile>
    )

    private fun buildDoraSamples(): List<DoraSample> {
        val out = mutableListOf<DoraSample>()
        fun add(id: String, note: String, hand: HandBuilder, dora: List<Tile>, ura: List<Tile> = emptyList()) {
            out.add(DoraSample(id, note, hand.build(), dora, ura))
        }

        add(
            "D-001", "red five only",
            HandBuilder().m(5, 3).m(5, 0).seqP(1).seqS(7),
            emptyList()
        )
        add(
            "D-002", "indicator and red stack on the same physical tile",
            HandBuilder().m(5, 3).m(5, 0).seqP(1).seqS(7),
            listOf(Tile(Suit.MAN, 4, 0))
        )
        add(
            "D-003", "repeated indicators and meld hits",
            HandBuilder().m(5).m(5).pon(20),
            listOf(Tile(Suit.MAN, 4, 0), Tile(Suit.MAN, 4, 1), Tile(Suit.PIN, 2, 0))
        )
        add(
            "D-004", "red five inside a chi meld",
            HandBuilder().p(5, 3).chiRed5(11),
            listOf(Tile(Suit.SOU, 4, 0))
        )
        add(
            "D-005", "ura indicators count in raw breakdown",
            HandBuilder().p(2, 0).s(7, 0),
            listOf(Tile(Suit.PIN, 1, 0), Tile(Suit.SOU, 6, 0)),
            listOf(Tile(Suit.PIN, 1, 0), Tile(Suit.SOU, 6, 0))
        )
        add(
            "D-006", "no red and no indicators",
            HandBuilder().seqM(1).seqP(4).seqS(7),
            emptyList()
        )
        return out
    }

    private data class SettlementSample(
        val id: String,
        val kind: String,
        val note: String,
        val input: SettlementInput,
        val winner: Int?,
        val loser: Int?,
        val tenpai: Set<Int>?,
        val fu: Int,
        val han: Int
    )

    private fun buildSettlementSamples(): List<SettlementSample> {
        val out = mutableListOf<SettlementSample>()
        val funded = SettlementInput(listOf(25000, 25000, 25000, 25000), 0, 2, 3)
        val noHonba = SettlementInput(listOf(25000, 25000, 25000, 25000), 0, 0, 0)
        val dealerThree = SettlementInput(listOf(25000, 23000, 27000, 21000), 3, 2, 3)

        fun add(
            id: String,
            kind: String,
            note: String,
            input: SettlementInput,
            winner: Int? = null,
            loser: Int? = null,
            tenpai: Set<Int>? = null
        ) {
            out.add(SettlementSample(id, kind, note, input, winner, loser, tenpai, 30, 3))
        }

        add("ST-001", "ron", "nondealer ron", funded, winner = 1, loser = 2)
        add("ST-002", "ron", "dealer ron", funded, winner = 0, loser = 2)
        add("ST-003", "tsumo", "nondealer tsumo", funded, winner = 1)
        add("ST-004", "tsumo", "dealer tsumo", funded, winner = 0)
        add("ST-005", "ron", "nondealer ron zero honba and sticks", noHonba, winner = 1, loser = 2)
        add("ST-006", "tsumo", "nondealer tsumo zero honba and sticks", noHonba, winner = 1)
        add("ST-007", "tsumo", "dealer three changes the high tsumo payer", dealerThree, winner = 1)
        add("ST-008", "abortive", "abortive draw", funded)
        for (mask in 0..15) {
            val tenpai = (0..3).filter { mask and (1 shl it) != 0 }.toSet()
            add("ST-1%02d".format(Locale.ROOT, mask), "exhaustive", "exhaustive draw mask $mask", funded, tenpai = tenpai)
        }
        return out
    }

    private data class ScoreSample(val id: String, val fu: Int, val han: Int, val yakuman: Int)

    private fun buildScoreSamples(): List<ScoreSample> {
        val out = mutableListOf<ScoreSample>()
        val fuValues = listOf(20, 25, 30, 40, 50, 60, 70, 80, 90, 100, 110)
        for (fu in fuValues) {
            for (han in 1..13) {
                out.add(ScoreSample("S-F%03d-H%02d".format(Locale.ROOT, fu, han), fu, han, 0))
            }
        }
        for (yakuman in 1..4) {
            out.add(ScoreSample("S-YAKUMAN-$yakuman", 30, 0, yakuman))
        }
        return out
    }

    // ---------------------------------------------------------------------
    // Random 10,000 generation
    // ---------------------------------------------------------------------

    private data class RandomSample(
        val id: String,
        val hand: PlayerHand,
        val context: YakuEvaluator.WinContext,
        val dora: List<Tile>,
        val ura: List<Tile>,
        val removedWinTile: Boolean
    )

    private fun buildRandomSamples(count: Int): List<RandomSample> {
        val rng = Random(20261023)
        val out = ArrayList<RandomSample>(count)
        var attempts = 0
        while (out.size < count) {
            attempts++
            if (attempts > count * 50) {
                error("random hand generator failed to produce enough valid hands")
            }
            val openCount = when (rng.nextInt(100)) {
                in 0..54 -> 0
                in 55..74 -> 1
                in 75..89 -> 2
                in 90..96 -> 3
                else -> 4
            }
            val generated = generateRandomHand(rng, openCount) ?: continue
            val handBuilder = generated.first
            val openIndices = generated.second
            val mentsu = generated.third
            val pair = generated.fourth

            val closedBefore = handBuilder.closedCount()
            val removeWin = closedBefore > 0 && rng.nextDouble() < 0.30
            val removed = if (removeWin) handBuilder.removeRandomClosed(rng) else null
            val hand = handBuilder.build()
            val winTile = removed ?: hand.closedTiles[rng.nextInt(hand.closedTiles.size)]
            val seat = 1 + rng.nextInt(4)
            val round = 1 + rng.nextInt(4)
            val tsumo = rng.nextBoolean()
            val closed = openCount == 0
            val riichi = rng.nextInt(100) < 12
            val doubleRiichi = riichi && rng.nextInt(100) < 10
            val ippatsu = riichi && rng.nextInt(100) < 20
            val haitei = rng.nextInt(100) < 3
            val houtei = !tsumo && rng.nextInt(100) < 3
            val rinshan = tsumo && rng.nextInt(100) < 3
            val chankan = !tsumo && rng.nextInt(100) < 3
            val tenhou = closed && tsumo && rng.nextInt(1000) < 1
            val chiihou = closed && tsumo && !tenhou && rng.nextInt(1000) < 1
            val context = YakuEvaluator.WinContext(
                winTile = winTile,
                isTsumo = tsumo,
                isClosed = closed,
                seatWind = seat,
                roundWind = round,
                isRiichi = riichi,
                isDoubleRiichi = doubleRiichi,
                isIppatsu = ippatsu,
                isRinshan = rinshan,
                isHaitei = haitei,
                isHoutei = houtei,
                isChankan = chankan,
                isTenhou = tenhou,
                isChiihou = chiihou
            )
            val dora = (0 until rng.nextInt(4)).map { Tile.fromTileId(rng.nextInt(136)) }
            val ura = if (riichi) (0 until rng.nextInt(3)).map { Tile.fromTileId(rng.nextInt(136)) } else emptyList()
            out.add(
                RandomSample(
                    "R-%06d".format(Locale.ROOT, out.size + 1),
                    hand,
                    context,
                    dora,
                    ura,
                    removed != null
                )
            )
        }
        return out
    }

    /**
     * Generates four mentsu and a pair with at most four physical copies per type.
     * Returns (builder, open mentsu indexes, mentsu type lists, pair type list).
     */
    private fun generateRandomHand(
        rng: Random,
        openCount: Int
    ): Quad<HandBuilder, Set<Int>, List<List<Int>>, List<Int>>? {
        val counts = IntArray(34)
        val mentsu = mutableListOf<List<Int>>()
        for (i in 0 until 4) {
            var added = false
            for (attempt in 0 until 80) {
                if (rng.nextInt(100) < 30) {
                    val type = rng.nextInt(34)
                    if (counts[type] + 3 <= 4) {
                        counts[type] += 3
                        mentsu.add(listOf(type, type, type))
                        added = true
                        break
                    }
                } else {
                    val suitBase = rng.nextInt(3) * 9
                    val start = suitBase + rng.nextInt(7)
                    if (counts[start] < 4 && counts[start + 1] < 4 && counts[start + 2] < 4) {
                        counts[start]++
                        counts[start + 1]++
                        counts[start + 2]++
                        mentsu.add(listOf(start, start + 1, start + 2))
                        added = true
                        break
                    }
                }
            }
            if (!added) {
                return null
            }
        }
        var pair: List<Int>? = null
        for (attempt in 0 until 100) {
            val type = rng.nextInt(34)
            if (counts[type] + 2 <= 4) {
                counts[type] += 2
                pair = listOf(type, type)
                break
            }
        }
        if (pair == null) {
            return null
        }

        val openIndices = (0 until 4).shuffled(rng).take(openCount).toSet()
        val builder = HandBuilder()
        mentsu.forEachIndexed { index, group ->
            if (index in openIndices) {
                if (group[0] == group[1]) {
                    builder.pon(group[0])
                } else {
                    builder.chi(group[0], group[1], group[2])
                }
            } else {
                group.forEach { builder.add(it) }
            }
        }
        pair.forEach { builder.add(it) }
        return Quad(builder, openIndices, mentsu, pair)
    }

    private data class Quad<A, B, C, D>(val first: A, val second: B, val third: C, val fourth: D)

    // ---------------------------------------------------------------------
    // JSON projections
    // ---------------------------------------------------------------------

    private fun winJson(sample: WinSample): J {
        val r = evaluator.explain(
            sample.hand,
            sample.context,
            sample.dora,
            sample.ura,
            isDealer = sample.context.seatWind == 1,
            honba = sample.honba,
            riichiSticks = sample.sticks
        ) ?: error("win sample ${sample.id} was not a win")
        return obj(
            "id" to j(sample.id),
            "kind" to j("win"),
            "extractionStatus" to j("exported"),
            "tags" to arr(sample.tags.map { j(it) }),
            "note" to j(sample.note),
            "source" to sourceJson("engine/src/main/kotlin/com/majiang/rules/WinningScoreEvaluator.kt", sample.note),
            "input" to handJson(sample.hand, sample.context, sample.dora, sample.ura, sample.honba, sample.sticks, 0),
            "expected" to obj(
                "status" to j("win"),
                "yaku" to arr(r.yakuEntries.map {
                    obj(
                        "code" to j(it.code),
                        "displayName" to j(it.displayName),
                        "han" to j(it.han)
                    )
                }),
                "totalHan" to j(r.yakuEntries.sumOf { it.han }),
                "scoreHan" to j(r.score.han),
                "isYakuman" to j(r.score.isYakuman),
                "yakumanCount" to j(r.yakumanCount),
                "dora" to doraJson(r.dora),
                "fu" to fuJson(r.fu),
                "score" to scoreJson(r.score),
                "paymentPlan" to paymentPlanJson(r.paymentPlan),
                "honbaAddition" to j(r.honbaAddition),
                "riichiAddition" to j(r.riichiAddition),
                "totalPoints" to j(r.totalPoints)
            )
        )
    }

    private fun noWinJson(sample: NoWinSample): J {
        val result = evaluator.evaluate(sample.hand, sample.context, extraHan = sample.extraHan)
        check(result == null) { "no-win sample ${sample.id} unexpectedly won" }
        return obj(
            "id" to j(sample.id),
            "kind" to j("noWin"),
            "extractionStatus" to j("exported"),
            "note" to j(sample.note),
            "source" to sourceJson("engine/src/main/kotlin/com/majiang/rules/WinningScoreEvaluator.kt", sample.note),
            "input" to handJson(sample.hand, sample.context, sample.dora, sample.ura, 0, 0, sample.extraHan),
            "expected" to obj("status" to j("noWin"))
        )
    }

    private fun fuJsonSample(sample: FuSample): J {
        val r = FuCalculator.INSTANCE.calculateBreakdown(
            sample.decomposition,
            sample.winTile,
            sample.isTsumo,
            sample.isClosed,
            sample.seatWind,
            sample.roundWind,
            sample.openMelds
        )
        return obj(
            "id" to j(sample.id),
            "kind" to j("fu"),
            "extractionStatus" to j("exported"),
            "note" to j(sample.note),
            "source" to sourceJson("engine/src/main/kotlin/com/majiang/rules/FuCalculator.kt", sample.note),
            "input" to obj(
                "decomposition" to obj(
                    "pattern" to j(sample.decomposition.pattern.name),
                    "jantai" to j(sample.decomposition.jantai),
                    "mentsu" to arr(sample.decomposition.mentsu.map {
                        obj(
                            "typeIndices" to arr(it.typeIndices.map { index -> j(index) }),
                            "isSequence" to j(it.isSequence)
                        )
                    })
                ),
                "winTile" to j(sample.winTile.tileId),
                "isTsumo" to j(sample.isTsumo),
                "isClosed" to j(sample.isClosed),
                "seatWind" to j(sample.seatWind),
                "roundWind" to j(sample.roundWind),
                "openMelds" to arr(sample.openMelds.map { meldJson(it) })
            ),
            "expected" to fuJson(r)
        )
    }

    private fun doraJsonSample(sample: DoraSample): J {
        val r = evaluator.breakdownDora(sample.hand, sample.dora, sample.ura)
        return obj(
            "id" to j(sample.id),
            "kind" to j("dora"),
            "extractionStatus" to j("exported"),
            "note" to j(sample.note),
            "source" to sourceJson("engine/src/main/kotlin/com/majiang/rules/WinningScoreEvaluator.kt", sample.note),
            "input" to obj(
                "closedTiles" to tilesJson(sample.hand.closedTiles),
                "drawnTile" to (sample.hand.drawnTile?.let { j(it.tileId) } ?: J.Null),
                "melds" to arr(sample.hand.melds.map { meldJson(it) }),
                "doraIndicators" to tilesJson(sample.dora),
                "uraDoraIndicators" to tilesJson(sample.ura)
            ),
            "expected" to doraJson(r)
        )
    }

    private fun settlementJsonSample(sample: SettlementSample): J {
        val score = scoreCalculator.calculate(sample.fu, sample.han)
        val result = when (sample.kind) {
            "ron" -> SettlementService.INSTANCE.ron(sample.input, sample.winner!!, sample.loser!!, score)
            "tsumo" -> SettlementService.INSTANCE.tsumo(sample.input, sample.winner!!, score)
            "exhaustive" -> SettlementService.INSTANCE.exhaustiveDraw(sample.input, sample.tenpai!!)
            "abortive" -> SettlementService.INSTANCE.abortiveDraw(sample.input)
            else -> error("unknown settlement kind ${sample.kind}")
        }
        val normalizedKind = when (sample.kind) {
            "exhaustive" -> "exhaustiveDraw"
            else -> sample.kind
        }
        return obj(
            "id" to j(sample.id),
            "kind" to j("settlement"),
            "extractionStatus" to j("exported"),
            "note" to j(sample.note),
            "source" to sourceJson("engine/src/main/kotlin/com/majiang/settlement/SettlementService.kt", sample.note),
            "input" to obj(
                "settlementKind" to j(normalizedKind),
                "scores" to arr(sample.input.scores.map { j(it) }),
                "dealer" to j(sample.input.dealer),
                "honba" to j(sample.input.honba),
                "riichiSticks" to j(sample.input.riichiSticks),
                "winner" to (sample.winner?.let { j(it) } ?: J.Null),
                "loser" to (sample.loser?.let { j(it) } ?: J.Null),
                "tenpai" to (sample.tenpai?.let { seats -> arr(seats.sorted().map { j(it) }) } ?: J.Null),
                "fu" to j(sample.fu),
                "han" to j(sample.han)
            ),
            "expected" to obj(
                "deltas" to arr(result.deltas.map { j(it) }),
                "newScores" to arr(result.newScores.map { j(it) }),
                "remainingRiichiSticks" to j(result.remainingRiichiSticks)
            )
        )
    }

    private fun scoreJsonSample(sample: ScoreSample): J {
        val r = scoreCalculator.calculate(sample.fu, sample.han, sample.yakuman)
        return obj(
            "id" to j(sample.id),
            "kind" to j("score"),
            "extractionStatus" to j("exported"),
            "source" to sourceJson("engine/src/main/kotlin/com/majiang/rules/ScoreCalculator.kt", "fu/han/yakuman score table"),
            "input" to obj(
                "fu" to j(sample.fu),
                "han" to j(sample.han),
                "yakumanCount" to j(sample.yakuman)
            ),
            "expected" to obj(
                "basePoints" to j(r.basePoints),
                "rawBasePoints" to j(r.rawBasePoints),
                "rawBasePointsExact" to j(r.rawBasePointsExact),
                "capCode" to (r.capCode?.let { j(it) } ?: J.Null),
                "isYakuman" to j(r.isYakuman),
                "yakumanMultiplier" to j(r.yakumanMultiplier),
                "payments" to obj(
                    "dealerTsumo" to j(r.dealerTsumoPayment()),
                    "nonDealerTsumoDealer" to j(r.nonDealerTsumoPayment().first),
                    "nonDealerTsumoOther" to j(r.nonDealerTsumoPayment().second),
                    "dealerRon" to j(r.dealerRonPayment()),
                    "nonDealerRon" to j(r.nonDealerRonPayment())
                ),
                "paymentPlans" to obj(
                    "dealerTsumo" to paymentPlanJson(r.paymentPlan(isDealer = true, isTsumo = true)),
                    "nonDealerTsumo" to paymentPlanJson(r.paymentPlan(isDealer = false, isTsumo = true)),
                    "dealerRon" to paymentPlanJson(r.paymentPlan(isDealer = true, isTsumo = false)),
                    "nonDealerRon" to paymentPlanJson(r.paymentPlan(isDealer = false, isTsumo = false))
                )
            )
        )
    }

    private fun randomJson(sample: RandomSample): J {
        val r = evaluator.explain(
            sample.hand,
            sample.context,
            sample.dora,
            sample.ura,
            isDealer = sample.context.seatWind == 1
        )
        val expected = if (r == null) {
            obj("status" to j("noWin"))
        } else {
            obj(
                "status" to j("win"),
                "yaku" to arr(r.yakuEntries.map {
                    obj("code" to j(it.code), "han" to j(it.han))
                }),
                "yakuHan" to j(r.yakuEntries.sumOf { it.han }),
                "scoreHan" to j(r.score.han),
                "isYakuman" to j(r.score.isYakuman),
                "yakumanCount" to j(r.yakumanCount),
                "fu" to j(r.fu.finalFu),
                "basePoints" to j(r.score.basePoints),
                "capCode" to (r.score.capCode?.let { j(it) } ?: J.Null),
                "totalPoints" to j(r.paymentPlan.total)
            )
        }
        return obj(
            "id" to j(sample.id),
            "extractionStatus" to j("exported"),
            "source" to sourceJson("engine/src/main/kotlin/com/majiang/rules/WinningScoreEvaluator.kt", "random V4 evaluator result"),
            "closed" to tilesJson(sample.hand.closedTiles),
            "drawn" to (sample.hand.drawnTile?.let { j(it.tileId) } ?: J.Null),
            "melds" to arr(sample.hand.melds.map { meldJson(it) }),
            "context" to contextJson(sample.context),
            "dora" to tilesJson(sample.dora),
            "ura" to tilesJson(sample.ura),
            "removedWinTile" to j(sample.removedWinTile),
            "expected" to expected
        )
    }

    private fun randomMeta(samples: List<RandomSample>): J {
        var win = 0
        var noWin = 0
        var open = 0
        var closed = 0
        var tsumo = 0
        var ron = 0
        var removed = 0
        var yakuman = 0
        val yakuCounts = sortedMapOf<String, Int>()
        for (sample in samples) {
            if (sample.context.isTsumo) tsumo++ else ron++
            if (sample.hand.melds.isEmpty()) closed++ else open++
            if (sample.removedWinTile) removed++
            val r = evaluator.explain(
                sample.hand,
                sample.context,
                sample.dora,
                sample.ura,
                isDealer = sample.context.seatWind == 1
            )
            if (r == null) {
                noWin++
            } else {
                win++
                if (r.score.isYakuman) yakuman++
                r.yakuEntries.forEach { entry ->
                    yakuCounts[entry.code] = (yakuCounts[entry.code] ?: 0) + 1
                }
            }
        }
        return obj(
            "seed" to j(20261023L),
            "iterations" to j(samples.size),
            "win" to j(win),
            "noWin" to j(noWin),
            "openHands" to j(open),
            "closedHands" to j(closed),
            "tsumo" to j(tsumo),
            "ron" to j(ron),
            "removedWinTile" to j(removed),
            "yakumanSamples" to j(yakuman),
            "yakuCounts" to obj(*yakuCounts.map { (code, count) -> code to j(count) }.toTypedArray()),
            "deterministicNote" to j("Kotlin Random(20261023) generator; exact generated hands are stored, not regenerated")
        )
    }

    private fun handJson(
        hand: PlayerHand,
        context: YakuEvaluator.WinContext,
        dora: List<Tile>,
        ura: List<Tile>,
        honba: Int = 0,
        riichiSticks: Int = 0,
        extraHan: Int = 0
    ): J {
        return obj(
            "closedTiles" to tilesJson(hand.closedTiles),
            "drawnTile" to (hand.drawnTile?.let { j(it.tileId) } ?: J.Null),
            "melds" to arr(hand.melds.map { meldJson(it) }),
            "context" to contextJson(context),
            "doraIndicators" to tilesJson(dora),
            "uraDoraIndicators" to tilesJson(ura),
            "honba" to j(honba),
            "riichiSticks" to j(riichiSticks),
            "extraHan" to j(extraHan)
        )
    }

    private fun contextJson(context: YakuEvaluator.WinContext): J {
        return obj(
            "winTile" to j(context.winTile.tileId),
            "isTsumo" to j(context.isTsumo),
            "isClosed" to j(context.isClosed),
            "seatWind" to j(context.seatWind),
            "roundWind" to j(context.roundWind),
            "isRiichi" to j(context.isRiichi),
            "isDoubleRiichi" to j(context.isDoubleRiichi),
            "isIppatsu" to j(context.isIppatsu),
            "isRinshan" to j(context.isRinshan),
            "isHaitei" to j(context.isHaitei),
            "isHoutei" to j(context.isHoutei),
            "isChankan" to j(context.isChankan),
            "isTenhou" to j(context.isTenhou),
            "isChiihou" to j(context.isChiihou)
        )
    }

    private fun meldJson(meld: Meld): J {
        return obj(
            "type" to j(meld.type.name),
            "tiles" to tilesJson(meld.tiles),
            "fromPlayer" to j(meld.fromPlayer),
            "calledTile" to j(meld.calledTile.tileId)
        )
    }

    private fun doraJson(dora: DoraBreakdown): J {
        return obj(
            "indicatorCount" to j(dora.indicatorCount),
            "uraCount" to j(dora.uraCount),
            "redCount" to j(dora.redCount),
            "totalHan" to j(dora.totalHan),
            "indicatorTiles" to tilesJson(dora.indicatorTiles),
            "uraTiles" to tilesJson(dora.uraTiles),
            "redTiles" to tilesJson(dora.redTiles)
        )
    }

    private fun fuJson(fu: FuBreakdown): J {
        return obj(
            "lines" to arr(fu.lines.map {
                obj("description" to j(it.description), "fu" to j(it.fu))
            }),
            "subtotal" to j(fu.subtotal),
            "roundedUpTo" to j(fu.roundedUpTo),
            "finalFu" to j(fu.finalFu),
            "specialNote" to (fu.specialNote?.let { j(it) } ?: J.Null)
        )
    }

    private fun scoreJson(score: ScoreCalculator.ScoreResult): J {
        return obj(
            "fu" to j(score.fu),
            "han" to j(score.han),
            "basePoints" to j(score.basePoints),
            "rawBasePoints" to j(score.rawBasePoints),
            "rawBasePointsExact" to j(score.rawBasePointsExact),
            "capCode" to (score.capCode?.let { j(it) } ?: J.Null),
            "isYakuman" to j(score.isYakuman),
            "yakumanMultiplier" to j(score.yakumanMultiplier)
        )
    }

    private fun paymentPlanJson(plan: PaymentPlan): J {
        return obj(
            "kind" to j(plan.kind.code),
            "components" to arr(plan.components.map {
                obj(
                    "payerRole" to j(it.payerRole.code),
                    "amount" to j(it.amount),
                    "count" to j(it.count)
                )
            }),
            "total" to j(plan.total)
        )
    }

    private fun sourceJson(file: String, note: String): J {
        return obj(
            "file" to j(file),
            "testClass" to j("com.majiang.rules.V5ScoringGoldenExporterTest"),
            "note" to j(note)
        )
    }

    private fun tilesJson(tiles: List<Tile>): J {
        return arr(tiles.map { j(it.tileId) })
    }

    private fun repoRoot(): Path {
        var path: Path? = Paths.get(System.getProperty("user.dir")).toAbsolutePath()
        while (path != null) {
            if (Files.exists(path.resolve("AGENTS.md")) && Files.exists(path.resolve("dotnet"))) {
                return path
            }
            path = path.parent
        }
        error("cannot locate repository root from ${System.getProperty("user.dir")}")
    }

    private fun v4Commit(): String {
        return try {
            val process = ProcessBuilder("git", "-C", repoRoot().toString(), "rev-parse", "HEAD")
                .redirectErrorStream(true)
                .start()
            val text = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }.trim()
            if (process.waitFor() == 0 && text.isNotEmpty()) text else "unknown"
        } catch (e: Exception) {
            "unknown"
        }
    }
}

// -------------------------------------------------------------------------
// Small helpers
// -------------------------------------------------------------------------

private class HandBuilder {
    private val used = mutableSetOf<Int>()
    val closed = mutableListOf<Tile>()
    val melds = mutableListOf<Meld>()

    fun add(typeIndex: Int, copy: Int? = null): HandBuilder {
        addTile(typeIndex, copy)
        return this
    }

    fun addTile(typeIndex: Int, copy: Int? = null): Tile {
        val tile = allocateTile(typeIndex, copy)
        closed.add(tile)
        return tile
    }

    private fun allocateTile(typeIndex: Int, copy: Int? = null): Tile {
        val chosen = copy ?: (0..3).firstOrNull { (typeIndex * 4 + it) !in used }
            ?: error("no physical copy left for typeIndex $typeIndex")
        val id = typeIndex * 4 + chosen
        require(used.add(id)) { "duplicate physical tile $id" }
        return Tile.fromTypeIndex(typeIndex, chosen)
    }

    fun m(rank: Int, copy: Int? = null) = add(rank - 1, copy)
    fun s(rank: Int, copy: Int? = null) = add(9 + rank - 1, copy)
    fun p(rank: Int, copy: Int? = null) = add(18 + rank - 1, copy)
    fun wind(rank: Int, copy: Int? = null) = add(27 + rank - 1, copy)
    fun dragon(rank: Int) = add(31 + rank - 1)

    fun seqM(start: Int): HandBuilder {
        m(start); m(start + 1); m(start + 2); return this
    }

    fun seqS(start: Int): HandBuilder {
        s(start); s(start + 1); s(start + 2); return this
    }

    fun seqP(start: Int): HandBuilder {
        p(start); p(start + 1); p(start + 2); return this
    }

    fun tripM(rank: Int): HandBuilder {
        m(rank); m(rank); m(rank); return this
    }

    fun tripS(rank: Int): HandBuilder {
        s(rank); s(rank); s(rank); return this
    }

    fun tripP(rank: Int): HandBuilder {
        p(rank); p(rank); p(rank); return this
    }

    fun tripWind(rank: Int): HandBuilder {
        wind(rank); wind(rank); wind(rank); return this
    }

    fun tripDragon(rank: Int): HandBuilder {
        dragon(rank); dragon(rank); dragon(rank); return this
    }

    fun pairM(rank: Int): HandBuilder {
        m(rank); m(rank); return this
    }

    fun pairS(rank: Int): HandBuilder {
        s(rank); s(rank); return this
    }

    fun pairP(rank: Int): HandBuilder {
        p(rank); p(rank); return this
    }

    fun pairWind(rank: Int): HandBuilder {
        wind(rank); wind(rank); return this
    }

    fun pairDragon(rank: Int): HandBuilder {
        dragon(rank); dragon(rank); return this
    }

    fun chi(vararg typeIndices: Int): HandBuilder {
        val tiles = typeIndices.map { allocateTile(it) }
        melds.add(Meld(MeldType.CHI, tiles, 1, tiles.first()))
        return this
    }

    /** 3-4-5 sequence whose five is the physical red copy (copy 3). */
    fun chiRed5(startType: Int): HandBuilder {
        val tiles = listOf(
            allocateTile(startType),
            allocateTile(startType + 1),
            allocateTile(startType + 2, 3)
        )
        melds.add(Meld(MeldType.CHI, tiles, 1, tiles.first()))
        return this
    }

    fun pon(typeIndex: Int): HandBuilder {
        val tiles = (0..2).map { allocateTile(typeIndex) }
        melds.add(Meld(MeldType.PON, tiles, 1, tiles.first()))
        return this
    }

    fun closedKan(typeIndex: Int): HandBuilder {
        val tiles = (0..3).map { allocateTile(typeIndex) }
        melds.add(Meld(MeldType.CLOSED_KAN, tiles, 0, tiles.first()))
        return this
    }

    fun openKan(typeIndex: Int): HandBuilder {
        val tiles = (0..3).map { allocateTile(typeIndex) }
        melds.add(Meld(MeldType.OPEN_KAN, tiles, 1, tiles.first()))
        return this
    }

    fun tileOf(typeIndex: Int): Tile {
        return closed.firstOrNull { it.typeIndex == typeIndex }
            ?: melds.flatMap { it.tiles }.firstOrNull { it.typeIndex == typeIndex }
            ?: error("sample does not contain typeIndex $typeIndex")
    }

    fun closedCount(): Int = closed.size

    fun removeRandomClosed(rng: Random): Tile {
        require(closed.isNotEmpty())
        val tile = closed.removeAt(rng.nextInt(closed.size))
        used.remove(tile.tileId)
        return tile
    }

    fun removeTile(tile: Tile) {
        require(closed.remove(tile)) { "tile $tile is not in the closed hand" }
        used.remove(tile.tileId)
    }

    fun build(): PlayerHand {
        val hand = PlayerHand()
        hand.closedTiles.addAll(closed)
        hand.melds.addAll(melds)
        return hand
    }
}

private fun seq(vararg typeIndices: Int) =
    HandDecomposer.MentsuGroup(typeIndices.toList(), isSequence = true)

private fun trip(typeIndex: Int) =
    HandDecomposer.MentsuGroup(listOf(typeIndex, typeIndex, typeIndex), isSequence = false)

// -------------------------------------------------------------------------
// Minimal JSON tree (test-only)
// -------------------------------------------------------------------------

private sealed class J {
    data class Str(val value: String) : J()
    data class Num(val value: Long) : J()
    data class Bool(val value: Boolean) : J()
    object Null : J()
    data class Array(val items: List<J>, val compact: Boolean = false) : J()
    data class Object(val fields: List<Pair<String, J>>) : J()

    fun render(): String {
        val sb = StringBuilder()
        renderTo(sb, 0, compact = false)
        return sb.toString()
    }

    private fun renderTo(sb: StringBuilder, indent: Int, compact: Boolean) {
        when (this) {
            is Str -> sb.append(quote(value))
            is Num -> sb.append(value)
            is Bool -> sb.append(if (value) "true" else "false")
            Null -> sb.append("null")
            is Array -> {
                sb.append('[')
                if (items.isNotEmpty()) {
                    val childCompact = compact || this.compact
                    items.forEachIndexed { index, item ->
                        if (index > 0) sb.append(',')
                        if (!childCompact) {
                            sb.append('\n').append(" ".repeat(indent + 2))
                        }
                        item.renderTo(sb, indent + 2, childCompact)
                    }
                    if (!childCompact) {
                        sb.append('\n').append(" ".repeat(indent))
                    }
                }
                sb.append(']')
            }
            is Object -> {
                sb.append('{')
                if (fields.isNotEmpty()) {
                    fields.forEachIndexed { index, field ->
                        if (index > 0) sb.append(',')
                        if (!compact) {
                            sb.append('\n').append(" ".repeat(indent + 2))
                        }
                        sb.append(quote(field.first)).append(':')
                        if (!compact) sb.append(' ')
                        field.second.renderTo(sb, indent + 2, compact)
                    }
                    if (!compact) {
                        sb.append('\n').append(" ".repeat(indent))
                    }
                }
                sb.append('}')
            }
        }
    }

    private fun quote(value: String): String {
        val sb = StringBuilder("\"")
        for (c in value) {
            when (c) {
                '"' -> sb.append("\\\"")
                '\\' -> sb.append("\\\\")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                else -> if (c.code < 0x20) {
                    sb.append("\\u").append(String.format(Locale.ROOT, "%04x", c.code))
                } else {
                    sb.append(c)
                }
            }
        }
        sb.append('"')
        return sb.toString()
    }
}

private fun j(value: String): J = J.Str(value)
private fun j(value: Int): J = J.Num(value.toLong())
private fun j(value: Long): J = J.Num(value)
private fun j(value: Boolean): J = J.Bool(value)
private fun obj(vararg fields: Pair<String, J>): J = J.Object(fields.toList())
private fun arr(items: List<J>): J = J.Array(items)
