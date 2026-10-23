package com.majiang.v3

import com.majiang.application.PlayerDecision
import com.majiang.cheat.beam.BeamSwapAction
import com.majiang.cheat.beam.BeamSwapCandidate
import com.majiang.cheat.beam.BeamSwapChoiceRequest
import com.majiang.cheat.beam.BeamSwapOffer
import com.majiang.cheat.beam.BeamSwapRequest
import com.majiang.cheat.beam.BeamSwapSlot
import com.majiang.cheat.beam.BeamSwapWindow
import com.majiang.cheat.bloom.SleeveBloomChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.cheat.bloom.SleeveBloomKeep
import com.majiang.cheat.bloom.SleeveBloomOffTurnChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomOffTurnRequest
import com.majiang.cheat.bloom.SleeveBloomOfferMode
import com.majiang.cheat.bloom.SleeveBloomRequest
import com.majiang.cheat.bloom.SleeveBloomReservation
import com.majiang.cheat.mark.TileMarkGroup
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.cheat.mark.TileMarkState
import com.majiang.authority.cheat.BeamSwapSubmissionHandler
import com.majiang.authority.cheat.CheatSubmissionCheck
import com.majiang.authority.cheat.SleeveBloomSubmissionHandler
import com.majiang.authority.cheat.TileMarkSubmissionHandler
import com.majiang.economy.CheatEconomyConfig
import com.majiang.economy.CheatLedger
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.model.TileLocation
import com.majiang.model.Zone
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.model.TileMovement
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.DecisionKey
import com.majiang.timing.DecisionTiming
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.util.Locale

/**
 * V4 只读 golden 导出器（三绝技、出千账本与私有状态域，V5 P1.4）。
 *
 * 仅当 `V5_CHEAT_GOLDEN_EXPORT=1`（或系统属性 v5.cheat.export=true）时执行：
 * 真实运行 V4 生产源 [CheatLedger]（economy）、[TileMarkState]（cheat.mark）、
 * 三个权威预检处理器（authority.cheat，D-09 真实拒绝原因串）与 [TileMovement]
 * （model，等额物理移动原语）后，先在测试内断言 V4 验收法律（幂等重放、冲突零
 * 副作用、费用必须为 10、候选冻结一致性、136 张守恒、StateInvariants），全部
 * 通过后才写 `tests/v5-golden/cheat-v1.json`。文件中的一切数值均来自真实 V4
 * 运行；严禁手填预期哈希，也不得把未运行写成 exported。
 *
 * 槽位口径：`wall[i]` 为 V4 活牌山列表下标；`deadWall[i]`/`replacement[i]` 为
 * 王牌区下标；手牌只按“座位 + 实体牌集合”记录（不依赖容器内排序）。
 * C# 侧 Wall(i) 为活墙区相对下标（标准发牌消耗区下标 0..52，指针从 53 起），
 * 该映射常量由 C# golden 测试持有，不进入本文件。
 */
class V5CheatGoldenExporterTest {

    @Test
    fun exportCheatGoldenSamples() {
        if (System.getenv("V5_CHEAT_GOLDEN_EXPORT") != "1" && System.getProperty("v5.cheat.export") != "true") {
            return
        }

        val root = obj(
            "schema" to j("v5-cheat-golden-v1"),
            "generator" to j("engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt"),
            "generatorVersion" to j(1),
            "v4Commit" to j(v4Commit()),
            "rulesetVersion" to j(1),
            "generatedByCommand" to j(
                "V5_CHEAT_GOLDEN_EXPORT=1 ./gradlew :engine:test --tests com.majiang.v3.V5CheatGoldenExporterTest --console=plain"
            ),
            "extractionStatus" to j("exported"),
            "exportedAtNote" to j(
                "generated from actual V4 economy.CheatLedger / cheat.mark.TileMarkState / authority.cheat handlers / model.TileMovement calls; no expected value is hand-written"
            ),
            "v4RulesSourceNote" to j(
                "engine/src/main/kotlin/com/majiang/economy, /cheat/mark, /authority/cheat, /model/TileMovement.kt are the V4 production sources; the v4Commit above is the exact tree used"
            ),
            "slotConventionNote" to j(
                "wall[i] is the V4 live-wall list index; deadWall[i] and replacement[i] are dead-wall region indices; hands are recorded as seat + tile-id sets without container order"
            ),
            "ledger" to ledgerSection(),
            "tileMarkState" to tileMarkStateSection(),
            "tileMarkHandler" to arr(tileMarkHandlerSection()),
            "sleeveBloomHandler" to arr(sleeveBloomHandlerSection()),
            "beamSwapHandler" to arr(beamSwapHandlerSection()),
            "tileMovement" to arr(tileMovementSection())
        )

        val path = repoRoot().resolve("tests/v5-golden/cheat-v1.json")
        Files.createDirectories(path.parent)
        Files.write(path, root.render().toByteArray(StandardCharsets.UTF_8))
        assertTrue(Files.size(path) > 5_000L, "golden file looks unexpectedly small: ${Files.size(path)} bytes")
        assertTrue(Files.exists(path), "golden file was not written: $path")
    }

    // ------------------------------------------------------------------
    // 账本（V4 economy.CheatLedger 真实调用）
    // ------------------------------------------------------------------

    private fun ledgerSection(): J {
        val fresh = { CheatLedger(CheatEconomyConfig(enabled = true, initialPoints = List(4) { 40 })) }

        val ledger = fresh()
        val entry = ledger.apply("skill-1", 0, -7, "TILE_MARK")
        assertEquals(33, entry.balanceAfter)
        assertEquals(listOf(33, 40, 40, 40), ledger.balances())
        assertEquals(1, ledger.entries().size)

        val replay = ledger.apply("skill-1", 0, -7, "TILE_MARK")
        assertSame(entry, replay) // 同 id 同载荷重放必须返回原 entry 实例
        assertEquals(33, replay.balanceAfter) // 且 balanceAfter 是首次入账值
        assertEquals(1, ledger.entries().size)

        val conflictEx = exName { ledger.apply("skill-1", 0, 6, "TILE_MARK") }
        assertEquals("IllegalArgumentException", conflictEx)
        assertEquals(listOf(33, 40, 40, 40), ledger.balances()) // 冲突零副作用
        assertEquals(1, ledger.entries().size)

        val poor = CheatLedger(CheatEconomyConfig(enabled = true, initialPoints = List(4) { 10 }))
        val insufficientEx = exName { poor.apply("over", 1, -11, "TILE_MARK") }
        assertEquals("IllegalStateException", insufficientEx)
        val overflowEx = exName { poor.apply("big", 2, Int.MAX_VALUE, "TILE_MARK") }
        assertEquals("ArithmeticException", overflowEx)
        val seatEx = exName { poor.apply("seat", 4, -1, "TILE_MARK") }
        assertEquals("IllegalArgumentException", seatEx)
        assertEquals(listOf(10, 10, 10, 10), poor.balances())
        assertTrue(poor.entries().isEmpty())

        val flow = CheatLedger(CheatEconomyConfig(enabled = true, initialPoints = List(4) { 10 }))
        flow.apply("spend", 0, -7, "TILE_MARK")
        flow.apply("gain", 1, 5, "ROUND_ACHIEVEMENT")
        val summary = flow.summary()
        assertEquals(7, summary.seatStatistics[0].totalSpent)
        assertEquals(5, summary.seatStatistics[1].totalGained)
        assertEquals(5, summary.seatStatistics[1].netDelta)

        val restored = CheatLedger.fromSnapshot(
            CheatEconomyConfig(enabled = true, initialPoints = List(4) { 10 }),
            flow.snapshot()
        )
        assertEquals(flow.balances(), restored.balances())
        assertEquals(flow.entries(), restored.entries())

        return obj(
            "initialPoints" to intsJson(List(4) { 40 }),
            "chargeOnce" to obj(
                "entryId" to j("skill-1"), "seat" to j(0), "amount" to j(-7), "reason" to j("TILE_MARK"),
                "balanceAfter" to j(entry.balanceAfter),
                "balancesAfter" to intsJson(ledger.balances()),
                "entryCount" to j(ledger.entries().size)
            ),
            "replaySamePayload" to obj(
                "returnsOriginalEntry" to j(true),
                "balanceAfter" to j(replay.balanceAfter),
                "entryCount" to j(ledger.entries().size)
            ),
            "conflictDifferentPayload" to obj(
                "requested" to obj("seat" to j(0), "amount" to j(6)),
                "outcome" to j("rejected"), "exception" to j(conflictEx),
                "balancesAfter" to intsJson(ledger.balances()),
                "entryCount" to j(ledger.entries().size)
            ),
            "insufficientFunds" to obj("amount" to j(-11), "initialPoints" to j(10), "outcome" to j("rejected"), "exception" to j(insufficientEx)),
            "overflow" to obj("amount" to j(Int.MAX_VALUE), "outcome" to j("rejected"), "exception" to j(overflowEx)),
            "invalidSeat" to obj("seat" to j(4), "outcome" to j("rejected"), "exception" to j(seatEx)),
            "flowSummary" to obj(
                "spend" to obj("seat" to j(0), "amount" to j(-7)),
                "gain" to obj("seat" to j(1), "amount" to j(5)),
                "seat0TotalSpent" to j(summary.seatStatistics[0].totalSpent),
                "seat1TotalGained" to j(summary.seatStatistics[1].totalGained),
                "seat1NetDelta" to j(summary.seatStatistics[1].netDelta),
                "finalBalances" to intsJson(flow.balances())
            ),
            "snapshotRoundTrip" to obj("balancesEqual" to j(true), "entriesEqual" to j(true))
        )
    }

    // ------------------------------------------------------------------
    // 落汗标记状态（V4 cheat.mark.TileMarkState 真实调用）
    // ------------------------------------------------------------------

    private fun tileMarkStateSection(): J {
        var state = TileMarkState()
        state = state.registerDealtRound("r1")
        assertEquals(1, state.dealtRoundOrdinal)
        assertSame(state, state.registerDealtRound("r1")) // 同 id 登记无变化

        val g1 = TileMarkGroup("g1", 0, listOf(0, 1, 2, 3), "r1", 1)
        val marked = state.addGroup(g1)
        assertEquals(4, marked.groups.single().expiresAfterRoundOrdinal)
        assertSame(marked, marked.addGroup(g1)) // 同 id 同内容重放返回原实例

        val conflictEx = exName { marked.addGroup(TileMarkGroup("g1", 0, listOf(4, 5, 6, 7), "r1", 1)) }
        assertEquals("IllegalArgumentException", conflictEx)

        val g2 = TileMarkGroup("g2", 0, listOf(4, 5, 6, 7), "r1", 1)
        val g3 = TileMarkGroup("g3", 0, listOf(8, 9, 10, 11), "r1", 1)
        val three = marked.addGroup(g2).addGroup(g3)
        assertEquals(3, three.groups.size)
        val capacityEx = exName { three.addGroup(TileMarkGroup("g4", 0, listOf(12, 13, 14, 15), "r1", 1)) }
        assertEquals("IllegalArgumentException", capacityEx)

        val crossSeat = marked.addGroup(TileMarkGroup("other", 1, listOf(0, 1, 2, 3), "r1", 1))
        assertEquals(2, crossSeat.groups.size) // 不同座位允许标记同一实体
        assertEquals(3L, crossSeat.version) // 每次内容变更 +1

        // 到期：createdRoundOrdinal=1 → expiresAfterRoundOrdinal=4，完成第 4 局时移除
        var rolling = state.addGroup(TileMarkGroup("g1", 0, listOf(0, 1, 2, 3), "r1", 1))
        assertEquals(2L, rolling.version) // registerDealtRound=1, addGroup=2
        rolling = rolling.completeRound("r1").registerDealtRound("r2")
        rolling = rolling.completeRound("r2").registerDealtRound("r3")
        val afterThird = rolling.completeRound("r3")
        assertEquals(1, afterThird.groups.size) // 4 > 3 → g1 仍在
        assertEquals(7L, afterThird.version)
        val afterFourth = afterThird.registerDealtRound("r4").completeRound("r4")
        assertEquals(0, afterFourth.groups.size) // 4 > 4 不成立 → g1 到期移除
        assertEquals(4, afterFourth.settledRoundIds.size)
        assertEquals(9L, afterFourth.version)

        return obj(
            "registerDealtRound" to obj("ordinalAfterFirst" to j(1), "sameIdReplays" to j(true), "versionAfterFirstRegister" to j(1L)),
            "addGroup" to obj(
                "expiresAfterRoundOrdinal" to j(4),
                "sameIdSameContentReplays" to j(true),
                "sameIdDifferentContentException" to j(conflictEx),
                "seatGroupLimitIsThree" to j(true),
                "seatGroupLimitException" to j(capacityEx),
                "seatTileLimitIsTwelve" to j(true),
                "crossSeatSameTilesAllowed" to j(true),
                "crossSeatGroupCount" to j(crossSeat.groups.size),
                "versionTrail" to intsJson(listOf(0, 1, 2, 3))
            ),
            "expiry" to obj(
                "createdRoundOrdinal" to j(1),
                "groupsAfterCompletingRound3" to j(afterThird.groups.size),
                "groupsAfterCompletingRound4" to j(afterFourth.groups.size),
                "settledRoundCount" to j(afterFourth.settledRoundIds.size),
                "versionAfterFirstAdd" to j(2L),
                "versionAfterCompletingRound3" to j(afterThird.version),
                "versionAfterCompletingRound4" to j(afterFourth.version)
            )
        )
    }

    // ------------------------------------------------------------------
    // 三个权威预检处理器（V4 authority.cheat 真实调用）
    // ------------------------------------------------------------------

    private val roundId = "r-golden"
    private val sessionId = "s-golden"
    private val decisionId = "d-1"
    private val controlEpoch = 1L
    private val now = 1_000L
    private val deadline = 10_000L

    private fun decision(seat: Int) = PlayerDecision(roundId, decisionId, controlEpoch, emptyList())

    private fun timing(seat: Int, deadlineOverride: Long = deadline) = DecisionTiming(
        key = DecisionKey(roundId, seat, decisionId),
        generation = 1L,
        opensAtMillis = 0L,
        deadlineMillis = deadlineOverride,
        reserveAtOpenMillis = 0L
    )

    private fun checkOf(check: CheatSubmissionCheck): J = when (check) {
        is CheatSubmissionCheck.Allowed -> obj("outcome" to j("Allowed"), "reason" to J.Null)
        is CheatSubmissionCheck.Rejected -> obj("outcome" to j("Rejected"), "reason" to j(check.reason))
        is CheatSubmissionCheck.StaleOffer -> obj("outcome" to j("StaleOffer"), "reason" to j(check.reason))
    }

    private fun case(id: String, check: CheatSubmissionCheck, expected: CheatSubmissionCheck, note: String): J {
        when (expected) {
            is CheatSubmissionCheck.Allowed -> assertTrue(check is CheatSubmissionCheck.Allowed, id)
            is CheatSubmissionCheck.Rejected ->
                assertTrue(check is CheatSubmissionCheck.Rejected && check.reason == expected.reason, id)
            is CheatSubmissionCheck.StaleOffer ->
                assertTrue(check is CheatSubmissionCheck.StaleOffer && check.reason == expected.reason, id)
        }
        val verdict = checkOf(check)
        val fields = (verdict as J.Object).fields
        fields.add(0, "caseId" to j(id))
        fields.add(1, "note" to j(note))
        return verdict
    }

    // Appends the real V4 validator inputs of a success path so the C# golden replay can
    // bind its fixture to exported facts instead of private constants.
    private fun caseWithInputs(id: String, check: CheatSubmissionCheck, expected: CheatSubmissionCheck, note: String, inputs: Pair<String, J>): J {
        val base = case(id, check, expected, note)
        (base as J.Object).fields.add(inputs)
        return base
    }

    private fun nullableNum(value: Int?): J = if (value == null) J.Null else j(value)

    private fun inputsOf(input: SleeveBloomSubmissionHandler.ChoiceInput): Pair<String, J> = "goldenInputs" to obj(
        "seat" to j(input.seat),
        "keep" to j(input.request.keep.name),
        "offerId" to j(requireNotNull(input.offerId)),
        "turnNumber" to j(input.turnNumber),
        "offerCreatedTurnNumber" to j(requireNotNull(input.offerCreatedTurnNumber)),
        "drawKind" to j(requireNotNull(input.offerDrawKind).name),
        "drawnTileId" to j(requireNotNull(input.drawnTileId)),
        "offerFirstTileId" to j(requireNotNull(input.offerFirstTileId)),
        "offerSecondTileId" to j(requireNotNull(input.offerSecondTileId)),
        "offerSecondWallIndex" to nullableNum(input.offerSecondWallIndex),
        "nextAvailableWallIndex" to nullableNum(input.nextAvailableWallIndex),
        "wallPointer" to j(input.wallPointer),
        "wallEndExclusive" to j(input.wallEndExclusive),
        "rinshanPointer" to j(input.rinshanPointer),
        "cost" to j(requireNotNull(input.configuredCost))
    )

    private fun inputsOf(input: SleeveBloomSubmissionHandler.ActivationInput): Pair<String, J> = "goldenInputs" to obj(
        "seat" to j(input.seat),
        "drawKind" to j(input.lastDrawKind.name),
        "drawnTileId" to j(requireNotNull(input.drawnTileId)),
        "nextWallTileId" to nullableNum(input.nextWallTileId),
        "nextDeadWallTileId" to nullableNum(input.nextDeadWallTileId),
        "wallPointer" to j(input.wallPointer),
        "wallEndExclusive" to j(input.wallEndExclusive),
        "rinshanPointer" to j(input.rinshanPointer),
        "cheatPoints" to j(requireNotNull(input.cheatPoints)),
        "cost" to j(requireNotNull(input.configuredCost))
    )

    private fun inputsOf(input: SleeveBloomSubmissionHandler.OffTurnActivationInput): Pair<String, J> = "goldenInputs" to obj(
        "seat" to j(input.seat),
        "currentPlayer" to j(requireNotNull(input.currentPlayer)),
        "phase" to j(requireNotNull(input.phase).name),
        "windowId" to j(input.request.windowId),
        "availableWallCount" to j(input.availableWallCount),
        "cheatPoints" to j(input.cheatPoints),
        "cost" to j(requireNotNull(input.configuredCost))
    )

    private fun inputsOf(input: SleeveBloomSubmissionHandler.OffTurnChoiceInput): Pair<String, J> = "goldenInputs" to obj(
        "seat" to j(input.seat),
        "keep" to j(input.request.keep.name),
        "offerId" to j(requireNotNull(input.offerId)),
        "now" to j(input.now),
        "deadlineMillis" to j(requireNotNull(input.offerDeadlineMillis)),
        "offerFirstInWall" to j(input.offerFirstInWall),
        "offerSecondInWall" to j(input.offerSecondInWall)
    )

    private fun tileMarkHandlerSection(): List<J> {
        val handler = TileMarkSubmissionHandler()
        val handIds = (0..12).map { it * 4 } // 13 张暗手牌实体 id（红五不在其中）

        fun input(
            seat: Int = 0,
            tileIds: List<Int> = handIds.subList(0, 4),
            phase: GamePhase = GamePhase.CHEAT_WINDOW,
            currentPlayer: Int = 0,
            responseWindow: Boolean = false,
            preparation: Boolean = false,
            riichi: Boolean = false,
            nowOverride: Long = now,
            timingGiven: Boolean = true,
            roundIdGiven: String? = roundId,
            hand: Set<Int> = handIds.toSet(),
            marked: Set<Int> = emptySet(),
            groups: Int = 0,
            points: Int = 40,
            sleeveSeat: Int? = null,
            offTurnSeat: Int? = null,
            beamSeat: Int? = null
        ): TileMarkSubmissionHandler.Input = TileMarkSubmissionHandler.Input(
            seat = seat,
            request = TileMarkRequest("req-1", sessionId, roundId, decisionId, controlEpoch, tileIds),
            currentSessionId = sessionId,
            roundId = roundIdGiven,
            phase = phase,
            responseWindowActive = responseWindow,
            preparationActive = preparation,
            currentPlayer = currentPlayer,
            decision = decision(seat),
            controller = SeatController.RemoteHuman(false),
            timing = if (timingGiven) timing(seat) else null,
            now = nowOverride,
            riichiDeclared = riichi,
            sleeveBloomOfferSeat = sleeveSeat,
            sleeveBloomOffTurnOfferSeat = offTurnSeat,
            beamSwapOfferSeat = beamSeat,
            handTileIds = hand,
            markedTileIds = marked,
            existingGroupCount = groups,
            cheatPoints = points,
            cost = 10
        )

        return listOf(
            case("tm-allowed", handler.validate(input()), CheatSubmissionCheck.Allowed, "own turn CHEAT_WINDOW 4 owned unmarked tiles"),
            case("tm-not-your-turn", handler.validate(input(seat = 1, currentPlayer = 0)), rejected("NOT_YOUR_TURN"), "actor seat 1 while seat 0 acts"),
            case("tm-wrong-phase-preparation", handler.validate(input(preparation = true)), rejected("WRONG_PHASE"), "stacking/preparation active"),
            case("tm-wrong-phase-draw", handler.validate(input(phase = GamePhase.DRAW)), rejected("WRONG_PHASE"), "phase DRAW is neither trick window"),
            case("tm-wrong-phase-round-end", handler.validate(input(phase = GamePhase.ROUND_END)), rejected("DECISION_MISMATCH"), "round already ended is reported as decision mismatch"),
            case("tm-response-window", handler.validate(input(responseWindow = true)), rejected("WRONG_PHASE"), "response window open"),
            case("tm-riichi-locked", handler.validate(input(riichi = true)), rejected("TILE_MARK_RIICHI_LOCKED"), "riichi declared this round"),
            case("tm-invalid-selection-three", handler.validate(input(tileIds = handIds.subList(0, 3))), rejected("TILE_MARK_INVALID_SELECTION"), "only 3 tiles"),
            case("tm-invalid-selection-duplicate", handler.validate(input(tileIds = listOf(0, 0, 1, 2))), rejected("TILE_MARK_INVALID_SELECTION"), "duplicate entity id"),
            case("tm-tile-not-owned", handler.validate(input(tileIds = listOf(52, 56, 60, 64))), rejected("TILE_MARK_TILE_NOT_OWNED"), "tiles belong to other hands"),
            case("tm-already-marked", handler.validate(input(marked = setOf(8, 12), tileIds = listOf(0, 4, 8, 12))), rejected("TILE_MARK_ALREADY_MARKED"), "tiles 8 and 12 already marked"),
            case("tm-capacity-groups", handler.validate(input(groups = 3)), rejected("TILE_MARK_CAPACITY_EXCEEDED"), "3 existing groups"),
            case("tm-capacity-tiles", handler.validate(input(groups = 2, marked = (16..27).toSet())), rejected("TILE_MARK_CAPACITY_EXCEEDED"), "8 marked + 4 > 12"),
            case("tm-insufficient-funds", handler.validate(input(points = 5)), rejected("INSUFFICIENT_FUNDS"), "5 cheat points < cost 10"),
            case("tm-sleeve-pending", handler.validate(input(sleeveSeat = 0)), rejected("SLEEVE_BLOOM_CHOICE_PENDING"), "own sleeve offer pending"),
            case("tm-offturn-sleeve-pending", handler.validate(input(offTurnSeat = 0)), rejected("SLEEVE_BLOOM_CHOICE_PENDING"), "own off-turn offer pending"),
            case("tm-beam-pending", handler.validate(input(beamSeat = 0)), rejected("BEAM_SWAP_CHOICE_PENDING"), "own beam offer pending"),
            case("tm-no-active-decision", handler.validate(input(timingGiven = false)), rejected("NO_ACTIVE_DECISION"), "no open decision timing"),
            case("tm-deadline-expired", handler.validate(input(nowOverride = deadline)), rejected("DEADLINE_EXPIRED"), "now == deadlineMillis"),
            case("tm-decision-mismatch", handler.validate(input(roundIdGiven = null)), rejected("DECISION_MISMATCH"), "no active round")
        )
    }

    private fun rejected(reason: String): CheatSubmissionCheck = CheatSubmissionCheck.Rejected(reason)
    private fun stale(reason: String): CheatSubmissionCheck = CheatSubmissionCheck.StaleOffer(reason)

    private fun sleeveBloomHandlerSection(): List<J> {
        val handler = SleeveBloomSubmissionHandler()

        fun buildActivation(
            seat: Int = 0,
            drawKind: SleeveBloomDrawKind = SleeveBloomDrawKind.NORMAL,
            phase: GamePhase = GamePhase.CHEAT_WINDOW,
            currentPlayer: Int = 0,
            responseWindow: Boolean = false,
            drawnTileId: Int? = 16,
            drawnInHand: Boolean = true,
            nextWallTileId: Int? = 32,
            nextWallTileInWall: Boolean = true,
            sleevePending: Boolean = false,
            offTurnPending: Boolean = false,
            beamPending: Boolean = false,
            sleeveUsed: Int = -1,
            beamUsed: Int = -1,
            cost: Int? = 10,
            points: Int = 40,
            rinshanPointer: Int = 0,
            nextDeadWallTileId: Int? = null,
            nextDeadWallInDeadWall: Boolean = false
        ): SleeveBloomSubmissionHandler.ActivationInput {
            val input = SleeveBloomSubmissionHandler.ActivationInput(
                seat = seat,
                request = SleeveBloomRequest("req-1", sessionId, roundId, decisionId, controlEpoch),
                featureEnabled = true,
                economyEnabled = true,
                configuredCost = cost,
                closed = false,
                sessionClosed = false,
                paused = false,
                preparationActive = false,
                roundId = roundId,
                roundEnded = false,
                decision = decision(seat),
                controller = SeatController.RemoteHuman(false),
                timing = timing(seat),
                now = now,
                currentPlayer = currentPlayer,
                phase = phase,
                responseWindowActive = responseWindow,
                drawnTileId = drawnTileId,
                drawnTileInHand = drawnInHand,
                lastDrawKind = drawKind,
                riichiDeclared = false,
                sleeveBloomOfferActive = sleevePending,
                offTurnOfferActive = offTurnPending,
                beamSwapOfferActive = beamPending,
                sleeveBloomUsedOnTurn = sleeveUsed,
                beamSwapUsedOnTurn = beamUsed,
                turnNumber = 12,
                wallRemaining = 60,
                wallPointer = 53,
                wallEndExclusive = 113,
                nextWallTileId = nextWallTileId,
                nextWallTileInWall = nextWallTileInWall,
                rinshanPointer = rinshanPointer,
                totalKanCount = 0,
                deadWallPreviousTileId = if (drawKind == SleeveBloomDrawKind.RINSHAN) drawnTileId else null,
                nextDeadWallTileId = nextDeadWallTileId,
                nextDeadWallInDeadWall = nextDeadWallInDeadWall,
                cheatPoints = points,
                cost = 10
            )
            return input
        }

        fun activation(
            seat: Int = 0,
            drawKind: SleeveBloomDrawKind = SleeveBloomDrawKind.NORMAL,
            phase: GamePhase = GamePhase.CHEAT_WINDOW,
            currentPlayer: Int = 0,
            responseWindow: Boolean = false,
            drawnTileId: Int? = 16,
            drawnInHand: Boolean = true,
            nextWallTileId: Int? = 32,
            nextWallTileInWall: Boolean = true,
            sleevePending: Boolean = false,
            offTurnPending: Boolean = false,
            beamPending: Boolean = false,
            sleeveUsed: Int = -1,
            beamUsed: Int = -1,
            cost: Int? = 10,
            points: Int = 40,
            rinshanPointer: Int = 0,
            nextDeadWallTileId: Int? = null,
            nextDeadWallInDeadWall: Boolean = false
        ): CheatSubmissionCheck = handler.validateActivation(
            buildActivation(
                seat, drawKind, phase, currentPlayer, responseWindow, drawnTileId, drawnInHand,
                nextWallTileId, nextWallTileInWall, sleevePending, offTurnPending, beamPending,
                sleeveUsed, beamUsed, cost, points, rinshanPointer, nextDeadWallTileId, nextDeadWallInDeadWall
            )
        )

        fun buildChoice(
            keep: SleeveBloomKeep = SleeveBloomKeep.SECOND,
            offerId: String = "bloom-0-req-1",
            requestOfferId: String = offerId,
            drawKind: SleeveBloomDrawKind = SleeveBloomDrawKind.NORMAL,
            turnNumber: Int = 12,
            drawnTileId: Int? = 16,
            drawnInHand: Boolean = true,
            offerFirstTileId: Int? = 16,
            offerSecondTileId: Int? = 32,
            offerSecondWallIndex: Int? = 53,
            nextAvailableWallIndex: Int? = 53,
            wallTileAtOfferIndexInWall: Boolean = true,
            rinshanPointer: Int = 0,
            deadWallPreviousTileId: Int? = null,
            deadWallSecondTileId: Int? = null,
            deadWallSecondInDeadWall: Boolean = false,
            offerMode: SleeveBloomOfferMode = SleeveBloomOfferMode.IMMEDIATE,
            offerSeat: Int? = 0
        ): SleeveBloomSubmissionHandler.ChoiceInput {
            val input = SleeveBloomSubmissionHandler.ChoiceInput(
                seat = 0,
                request = SleeveBloomChoiceRequest("req-2", sessionId, roundId, decisionId, controlEpoch, requestOfferId, keep),
                featureEnabled = true,
                economyEnabled = true,
                configuredCost = 10,
                closed = false,
                sessionClosed = false,
                paused = false,
                preparationActive = false,
                roundId = roundId,
                roundEnded = false,
                decision = decision(0),
                controller = SeatController.RemoteHuman(false),
                timing = timing(0),
                now = now,
                currentPlayer = 0,
                phase = GamePhase.CHEAT_WINDOW,
                responseWindowActive = false,
                turnNumber = turnNumber,
                drawnTileId = drawnTileId,
                drawnTileInHand = drawnInHand,
                offerSeat = offerSeat,
                offerId = offerId,
                offerMode = offerMode,
                offerCreatedTurnNumber = 12,
                offerFirstTileId = offerFirstTileId,
                offerSecondTileId = offerSecondTileId,
                offerDrawKind = drawKind,
                offerSecondWallIndex = offerSecondWallIndex,
                nextAvailableWallIndex = nextAvailableWallIndex,
                wallPointer = 53,
                wallEndExclusive = 113,
                wallTileAtOfferIndexInWall = wallTileAtOfferIndexInWall,
                rinshanPointer = rinshanPointer,
                totalKanCount = 0,
                deadWallPreviousTileId = deadWallPreviousTileId,
                deadWallSecondTileId = deadWallSecondTileId,
                deadWallSecondInDeadWall = deadWallSecondInDeadWall
            )
            return input
        }

        fun choice(
            keep: SleeveBloomKeep = SleeveBloomKeep.SECOND,
            offerId: String = "bloom-0-req-1",
            requestOfferId: String = offerId,
            drawKind: SleeveBloomDrawKind = SleeveBloomDrawKind.NORMAL,
            turnNumber: Int = 12,
            drawnTileId: Int? = 16,
            drawnInHand: Boolean = true,
            offerFirstTileId: Int? = 16,
            offerSecondTileId: Int? = 32,
            offerSecondWallIndex: Int? = 53,
            nextAvailableWallIndex: Int? = 53,
            wallTileAtOfferIndexInWall: Boolean = true,
            rinshanPointer: Int = 0,
            deadWallPreviousTileId: Int? = null,
            deadWallSecondTileId: Int? = null,
            deadWallSecondInDeadWall: Boolean = false,
            offerMode: SleeveBloomOfferMode = SleeveBloomOfferMode.IMMEDIATE,
            offerSeat: Int? = 0
        ): CheatSubmissionCheck = handler.validateChoice(
            buildChoice(
                keep, offerId, requestOfferId, drawKind, turnNumber, drawnTileId, drawnInHand,
                offerFirstTileId, offerSecondTileId, offerSecondWallIndex, nextAvailableWallIndex,
                wallTileAtOfferIndexInWall, rinshanPointer, deadWallPreviousTileId, deadWallSecondTileId,
                deadWallSecondInDeadWall, offerMode, offerSeat
            )
        )

        fun buildOffTurnActivation(
            seat: Int = 2,
            currentPlayer: Int = 0,
            phase: GamePhase = GamePhase.DISCARD,
            windowMatches: Boolean = true,
            reservationActive: Boolean = false,
            availableWallCount: Int = 60,
            points: Int = 40
        ): SleeveBloomSubmissionHandler.OffTurnActivationInput {
            val input = SleeveBloomSubmissionHandler.OffTurnActivationInput(
                seat = seat,
                request = SleeveBloomOffTurnRequest("req-1", sessionId, roundId, "bloom-window-2-1", controlEpoch),
                featureEnabled = true,
                economyEnabled = true,
                configuredCost = 10,
                closed = false,
                sessionClosed = false,
                paused = false,
                preparationActive = false,
                roundId = roundId,
                roundEnded = false,
                controller = SeatController.RemoteHuman(false),
                controlEpoch = controlEpoch,
                currentPlayer = currentPlayer,
                phase = phase,
                windowMatches = windowMatches,
                immediateOfferActive = false,
                offTurnOfferActive = false,
                reservationActive = reservationActive,
                beamSwapOfferActive = false,
                availableWallCount = availableWallCount,
                cheatPoints = points,
                cost = 10
            )
            return input
        }

        fun offTurnActivation(
            seat: Int = 2,
            currentPlayer: Int = 0,
            phase: GamePhase = GamePhase.DISCARD,
            windowMatches: Boolean = true,
            reservationActive: Boolean = false,
            availableWallCount: Int = 60,
            points: Int = 40
        ): CheatSubmissionCheck = handler.validateOffTurnActivation(
            buildOffTurnActivation(seat, currentPlayer, phase, windowMatches, reservationActive, availableWallCount, points)
        )

        fun buildOffTurnChoice(
            nowOverride: Long = now,
            firstInWall: Boolean = true,
            secondInWall: Boolean = true,
            deadlineMillis: Long? = 11_000L
        ): SleeveBloomSubmissionHandler.OffTurnChoiceInput {
            val input = SleeveBloomSubmissionHandler.OffTurnChoiceInput(
                seat = 2,
                request = SleeveBloomOffTurnChoiceRequest("req-2", sessionId, roundId, controlEpoch, "bloom-offturn-2-req-1", SleeveBloomKeep.SECOND),
                featureEnabled = true,
                economyEnabled = true,
                configuredCost = 10,
                closed = false,
                sessionClosed = false,
                paused = false,
                preparationActive = false,
                roundId = roundId,
                roundEnded = false,
                controller = SeatController.RemoteHuman(false),
                controlEpoch = controlEpoch,
                now = nowOverride,
                offerSeat = 2,
                offerId = "bloom-offturn-2-req-1",
                offerDeadlineMillis = deadlineMillis,
                offerFirstInWall = firstInWall,
                offerSecondInWall = secondInWall
            )
            return input
        }

        fun offTurnChoice(
            nowOverride: Long = now,
            firstInWall: Boolean = true,
            secondInWall: Boolean = true,
            deadlineMillis: Long? = 11_000L
        ): CheatSubmissionCheck = handler.validateOffTurnChoice(
            buildOffTurnChoice(nowOverride, firstInWall, secondInWall, deadlineMillis)
        )

        return listOf(
            caseWithInputs("sb-activate-allowed", activation(), CheatSubmissionCheck.Allowed, "own normal draw, next wall tile present",
                inputsOf(buildActivation())),
            case("sb-activate-dealer-extra", activation(drawKind = SleeveBloomDrawKind.DEAL_EXTRA), rejected("SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED"), "dealer 14th tile excluded"),
            case("sb-activate-already-used", activation(sleeveUsed = 12), rejected("SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW"), "same turn number"),
            case("sb-activate-beam-pending", activation(beamPending = true), rejected("BEAM_SWAP_ALREADY_PENDING"), "mutual exclusion"),
            case("sb-activate-sleeve-pending", activation(sleevePending = true), rejected("SLEEVE_BLOOM_ALREADY_PENDING"), "offer already frozen"),
            case("sb-activate-wall-empty", activation(nextWallTileId = null), rejected("SLEEVE_BLOOM_WALL_EMPTY"), "no second candidate in wall"),
            case("sb-activate-wall-empty-drift", activation(nextWallTileInWall = false), rejected("SLEEVE_BLOOM_WALL_EMPTY"), "candidate left the wall"),
            case("sb-activate-no-drawn-tile", activation(drawnTileId = null), rejected("SLEEVE_BLOOM_NO_DRAWN_TILE"), "no tracked draw"),
            case("sb-activate-not-your-turn", activation(seat = 1, currentPlayer = 0), rejected("NOT_YOUR_TURN"), "seat 1 on seat 0 turn"),
            case("sb-activate-wrong-phase", activation(phase = GamePhase.DISCARD), rejected("WRONG_PHASE"), "only CHEAT_WINDOW hosts it"),
            case("sb-activate-response-window", activation(responseWindow = true), rejected("WRONG_PHASE"), "responses override tricks"),
            case("sb-activate-disabled", activation(cost = 5), rejected("SLEEVE_BLOOM_DISABLED"), "cost must be exactly 10"),
            case("sb-activate-insufficient", activation(points = 5), rejected("INSUFFICIENT_FUNDS"), "5 < 10"),
            caseWithInputs("sb-activate-rinshan-allowed", activation(
                drawKind = SleeveBloomDrawKind.RINSHAN, drawnTileId = 24, rinshanPointer = 1,
                nextDeadWallTileId = 28, nextDeadWallInDeadWall = true
            ), CheatSubmissionCheck.Allowed, "rinshan draw with valid next dead wall tile",
                inputsOf(buildActivation(
                    drawKind = SleeveBloomDrawKind.RINSHAN, drawnTileId = 24, rinshanPointer = 1,
                    nextDeadWallTileId = 28, nextDeadWallInDeadWall = true
                ))),
            case("sb-activate-rinshan-empty", activation(
                drawKind = SleeveBloomDrawKind.RINSHAN, drawnTileId = 24, rinshanPointer = 4
            ), rejected("SLEEVE_BLOOM_WALL_EMPTY"), "rinshan exhausted (pointer 4)"),
            caseWithInputs("sb-choose-first", choice(keep = SleeveBloomKeep.FIRST), CheatSubmissionCheck.Allowed, "consistent immediate offer",
                inputsOf(buildChoice(keep = SleeveBloomKeep.FIRST))),
            caseWithInputs("sb-choose-second", choice(keep = SleeveBloomKeep.SECOND), CheatSubmissionCheck.Allowed, "consistent immediate offer",
                inputsOf(buildChoice(keep = SleeveBloomKeep.SECOND))),
            case("sb-choose-stale-turn", choice(turnNumber = 13), stale("SLEEVE_BLOOM_STALE_OFFER"), "offer created on turn 12"),
            case("sb-choose-stale-drawn", choice(drawnTileId = 20), stale("SLEEVE_BLOOM_STALE_OFFER"), "drawn tile drifted"),
            case("sb-choose-stale-index", choice(nextAvailableWallIndex = 54), stale("SLEEVE_BLOOM_STALE_OFFER"), "second candidate slot moved"),
            case("sb-choose-offer-mismatch", choice(requestOfferId = "bloom-0-other"), rejected("SLEEVE_BLOOM_OFFER_MISMATCH"), "state offer bloom-0-req-1, request carries bloom-0-other"),
            case("sb-choose-no-offer", choice(offerSeat = null), rejected("SLEEVE_BLOOM_NO_PENDING_OFFER"), "nothing pending"),
            caseWithInputs("sb-choose-rinshan", choice(
                drawKind = SleeveBloomDrawKind.RINSHAN, drawnTileId = 24, offerFirstTileId = 24, offerSecondTileId = 28,
                offerSecondWallIndex = null, rinshanPointer = 1, deadWallPreviousTileId = 24,
                deadWallSecondTileId = 28, deadWallSecondInDeadWall = true
            ), CheatSubmissionCheck.Allowed, "rinshan offer fully consistent",
                inputsOf(buildChoice(
                    drawKind = SleeveBloomDrawKind.RINSHAN, drawnTileId = 24, offerFirstTileId = 24, offerSecondTileId = 28,
                    offerSecondWallIndex = null, rinshanPointer = 1, deadWallPreviousTileId = 24,
                    deadWallSecondTileId = 28, deadWallSecondInDeadWall = true
                ))),
            caseWithInputs("sbo-activate-allowed", offTurnActivation(), CheatSubmissionCheck.Allowed, "window open after own discard",
                inputsOf(buildOffTurnActivation())),
            case("sbo-activate-no-window", offTurnActivation(windowMatches = false), rejected("SLEEVE_BLOOM_NO_WINDOW"), "window id mismatch"),
            case("sbo-activate-reserved", offTurnActivation(reservationActive = true), rejected("SLEEVE_BLOOM_RESERVED_PENDING"), "reservation outstanding"),
            case("sbo-activate-own-turn", offTurnActivation(currentPlayer = 2), rejected("NOT_YOUR_TURN"), "own turn uses immediate path"),
            case("sbo-activate-wall-empty", offTurnActivation(availableWallCount = 1), rejected("SLEEVE_BLOOM_WALL_EMPTY"), "fewer than two live tiles"),
            caseWithInputs("sbo-choose-allowed", offTurnChoice(), CheatSubmissionCheck.Allowed, "before deadline, both candidates in wall",
                inputsOf(buildOffTurnChoice())),
            case("sbo-choose-expired", offTurnChoice(nowOverride = 11_000L), rejected("DEADLINE_EXPIRED"), "now == deadlineMillis"),
            case("sbo-choose-stale-first", offTurnChoice(firstInWall = false), rejected("SLEEVE_BLOOM_STALE_OFFER"), "first candidate drawn away (off-turn drift is a plain rejection)"),
            case("sbo-choose-stale-second", offTurnChoice(secondInWall = false), rejected("SLEEVE_BLOOM_STALE_OFFER"), "second candidate drawn away (off-turn drift is a plain rejection)")
        )
    }

    private fun beamSwapHandlerSection(): List<J> {
        val handler = BeamSwapSubmissionHandler()
        val deadWallTiles = (108..121).map { Tile.fromTileId(it) } // 14 张风牌实体
        val deadWallIds = deadWallTiles.map { it.tileId }.toSet()
        val rinshanDora = deadWallTiles[4].tileId

        fun activation(
            drawKind: SleeveBloomDrawKind = SleeveBloomDrawKind.NORMAL,
            riichi: Boolean = false,
            drawnTileId: Int? = 16,
            beamPending: Boolean = false,
            sleevePending: Boolean = false,
            beamUsed: Int = -1,
            sleeveUsed: Int = -1,
            wallRemaining: Int = 60,
            cost: Int? = 10,
            points: Int = 40,
            rinshanPointer: Int = 1,
            deadWallInWall: Set<Int> = deadWallIds,
            doraIds: Set<Int> = setOf(rinshanDora)
        ): CheatSubmissionCheck {
            val input = BeamSwapSubmissionHandler.ActivationInput(
                seat = 0,
                request = BeamSwapRequest("req-1", sessionId, roundId, decisionId, controlEpoch),
                featureEnabled = true,
                economyEnabled = true,
                configuredCost = cost,
                closed = false,
                sessionClosed = false,
                paused = false,
                preparationActive = false,
                roundId = roundId,
                roundEnded = false,
                decision = decision(0),
                controller = SeatController.RemoteHuman(false),
                timing = timing(0),
                now = now,
                currentPlayer = 0,
                phase = GamePhase.CHEAT_WINDOW,
                responseWindowActive = false,
                drawnTileId = drawnTileId,
                drawnTileInHand = true,
                eligibleHandTileCount = 13,
                lastDrawKind = drawKind,
                riichiDeclared = riichi,
                sleeveBloomOfferActive = sleevePending,
                beamSwapOfferActive = beamPending,
                sleeveBloomUsedOnTurn = sleeveUsed,
                beamSwapUsedOnTurn = beamUsed,
                turnNumber = 12,
                wallRemaining = wallRemaining,
                wallPointer = 53,
                wallEndExclusive = 113,
                deadWall = if (drawKind == SleeveBloomDrawKind.RINSHAN) deadWallTiles else emptyList(),
                deadWallReplacements = if (drawKind == SleeveBloomDrawKind.RINSHAN) listOf(Tile.fromTileId(0)) else emptyList(),
                deadWallTileIdsInDeadWall = deadWallInWall,
                doraIndicatorTileIds = doraIds,
                rinshanPointer = rinshanPointer,
                cheatPoints = points,
                cost = 10
            )
            return handler.validateActivation(input)
        }

        fun choice(
            offerWindow: BeamSwapWindow = BeamSwapWindow.WALL,
            action: BeamSwapAction = BeamSwapAction.SWAP,
            handTileId: Int? = 4,
            targetSlot: BeamSwapSlot? = null,
            turnNumber: Int = 12,
            drawnTileId: Int? = 52,
            offerDrawnTileId: Int? = null,
            table: GameState? = null,
            offerCandidates: List<BeamSwapCandidate>? = null,
            doraIds: Set<Int> = emptySet()
        ): CheatSubmissionCheck {
            val live = table ?: beamTable()
            val wallPointer = 53
            val wallEnd = live.wallEndExclusive
            val wallTileIdsInWall = live.wall
                .filter { live.tilePool.getLocation(it) == TileLocation.WALL }
                .map { it.tileId }
                .toSet()
            val candidates = offerCandidates ?: (wallPointer until wallPointer + 8).map {
                BeamSwapCandidate(live.wall[it].tileId, BeamSwapSlot.WallSlot(it))
            }
            val frozenDrawn = offerDrawnTileId ?: drawnTileId ?: 52
            val eligible = live.players[0].closedTiles.map { it.tileId }.filter { it != frozenDrawn }.sorted()
            val offer = BeamSwapOffer(
                offerId = "beam-0-req-1",
                seat = 0,
                window = offerWindow,
                candidates = candidates,
                eligibleHandTileIds = eligible,
                drawnTileId = frozenDrawn,
                createdTurnNumber = 12
            )
            val slot = if (action == BeamSwapAction.SWAP) targetSlot ?: BeamSwapSlot.WallSlot(wallPointer) else targetSlot
            val input = BeamSwapSubmissionHandler.ChoiceInput(
                seat = 0,
                request = BeamSwapChoiceRequest("req-2", sessionId, roundId, decisionId, controlEpoch, "beam-0-req-1", action, handTileId, slot),
                featureEnabled = true,
                economyEnabled = true,
                configuredCost = 10,
                closed = false,
                sessionClosed = false,
                paused = false,
                preparationActive = false,
                roundId = roundId,
                roundEnded = false,
                decision = decision(0),
                controller = SeatController.RemoteHuman(false),
                timing = timing(0),
                now = now,
                currentPlayer = 0,
                phase = GamePhase.CHEAT_WINDOW,
                responseWindowActive = false,
                offer = offer,
                turnNumber = turnNumber,
                drawnTileId = drawnTileId,
                drawnTileInHand = true,
                lastDrawKind = if (offerWindow == BeamSwapWindow.WALL) SleeveBloomDrawKind.NORMAL else SleeveBloomDrawKind.RINSHAN,
                currentEligibleHandTileIds = live.players[0].closedTiles.map { it.tileId }.filter { it != drawnTileId }.toSet(),
                wall = live.wall,
                wallPointer = wallPointer,
                wallEndExclusive = wallEnd,
                wallTileIdsInWall = wallTileIdsInWall,
                deadWall = live.deadWall,
                deadWallReplacements = live.deadWallReplacements,
                deadWallTileIdsInDeadWall = (live.deadWall.filter { live.tilePool.getLocation(it) == TileLocation.DEAD_WALL } +
                        live.deadWallReplacements.filter { live.tilePool.getLocation(it) == TileLocation.DEAD_WALL })
                    .map { it.tileId }.toSet(),
                doraIndicatorTileIds = doraIds,
                rinshanPointer = live.rinshanPointer
            )
            return handler.validateChoice(input)
        }

        // KEEP_ORIGINAL 携带参数在请求构造边界即被拒绝（先于处理器）
        val keepArgsEx = exName {
            BeamSwapChoiceRequest(
                "req-2", sessionId, roundId, decisionId, controlEpoch,
                "beam-0-req-1", BeamSwapAction.KEEP_ORIGINAL, 4, BeamSwapSlot.WallSlot(53)
            )
        }
        assertEquals("IllegalArgumentException", keepArgsEx)

        val wallTable = beamTable()
        val deadTable = deadWallTable()
        val replacementTileId = deadTable.deadWallReplacements[0].tileId

        return listOf(
            case("bm-activate-allowed", activation(), CheatSubmissionCheck.Allowed, "own normal draw, live wall has candidates"),
            case("bm-activate-no-candidates", activation(wallRemaining = 0), rejected("BEAM_SWAP_NO_CANDIDATES"), "wall exhausted"),
            case("bm-activate-dealer-extra", activation(drawKind = SleeveBloomDrawKind.DEAL_EXTRA), rejected("BEAM_SWAP_DEALER_EXTRA_EXCLUDED"), "dealer 14th tile excluded"),
            case("bm-activate-riichi", activation(riichi = true), rejected("BEAM_SWAP_RIICHI_LOCKED"), "riichi declared"),
            case("bm-activate-no-drawn", activation(drawnTileId = null), rejected("BEAM_SWAP_NO_DRAWN_TILE"), "no tracked draw"),
            case("bm-activate-beam-pending", activation(beamPending = true), rejected("BEAM_SWAP_ALREADY_PENDING"), "offer already frozen"),
            case("bm-activate-sleeve-pending", activation(sleevePending = true), rejected("BEAM_SWAP_ALREADY_PENDING"), "mutual exclusion"),
            case("bm-activate-already-used", activation(beamUsed = 12), rejected("BEAM_SWAP_ALREADY_USED_THIS_DRAW"), "same turn number"),
            case("bm-activate-disabled", activation(cost = 5), rejected("BEAM_SWAP_DISABLED"), "cost must be exactly 10"),
            case("bm-activate-insufficient", activation(points = 5), rejected("INSUFFICIENT_FUNDS"), "5 < 10"),
            case("bm-activate-deadwall-allowed", activation(
                drawKind = SleeveBloomDrawKind.RINSHAN
            ), CheatSubmissionCheck.Allowed, "rinshan window with undrawn rinshan + back half + replacement"),
            case("bm-activate-deadwall-empty", activation(
                drawKind = SleeveBloomDrawKind.RINSHAN, rinshanPointer = 4,
                deadWallInWall = emptySet()
            ), rejected("BEAM_SWAP_NO_CANDIDATES"), "everything consumed"),
            case("bm-activate-deadwall-dora-only", activation(
                drawKind = SleeveBloomDrawKind.RINSHAN,
                deadWallInWall = setOf(rinshanDora),
                doraIds = setOf(rinshanDora)
            ), rejected("BEAM_SWAP_NO_CANDIDATES"), "revealed dora indicator never counts"),
            case("bm-choose-swap", choice(table = wallTable), CheatSubmissionCheck.Allowed, "wall slot candidate consistent"),
            case("bm-choose-keep", choice(table = wallTable, action = BeamSwapAction.KEEP_ORIGINAL, handTileId = null, targetSlot = null), CheatSubmissionCheck.Allowed, "keep original takes no arguments"),
            case("bm-choose-invalid-slot", choice(table = wallTable, targetSlot = BeamSwapSlot.WallSlot(99)), rejected("BEAM_SWAP_INVALID_CHOICE"), "slot outside frozen candidates"),
            case("bm-choose-invalid-foreign-hand", choice(table = wallTable, handTileId = 52), rejected("BEAM_SWAP_INVALID_CHOICE"), "tile not eligible at freeze time"),
            obj(
                "caseId" to j("bm-keep-original-args-forbidden-at-request"),
                "note" to j("BeamSwapChoiceRequest itself refuses arguments for KEEP_ORIGINAL before the handler runs"),
                "outcome" to j("Rejected"), "reason" to j("BEAM_SWAP_INVALID_CHOICE"),
                "requestException" to j(keepArgsEx)
            ),
            case("bm-choose-stale-turn", choice(table = wallTable, turnNumber = 13), stale("BEAM_SWAP_STALE_OFFER"), "offer created on turn 12"),
            case("bm-choose-stale-drawn", choice(table = wallTable, drawnTileId = 20, offerDrawnTileId = 52), stale("BEAM_SWAP_STALE_OFFER"), "drawn tile drifted"),
            case("bm-choose-deadwall-allowed", choice(
                offerWindow = BeamSwapWindow.DEAD_WALL,
                targetSlot = BeamSwapSlot.DeadWallSlot(1),
                table = deadTable,
                drawnTileId = deadWallTiles[0].tileId,
                offerCandidates = listOf(BeamSwapCandidate(109, BeamSwapSlot.DeadWallSlot(1)))
            ), CheatSubmissionCheck.Allowed, "undrawn rinshan slot candidate consistent"),
            case("bm-choose-deadwall-dora-stale", choice(
                offerWindow = BeamSwapWindow.DEAD_WALL,
                targetSlot = BeamSwapSlot.DeadWallSlot(4),
                table = deadTable,
                drawnTileId = deadWallTiles[0].tileId,
                offerCandidates = listOf(BeamSwapCandidate(rinshanDora, BeamSwapSlot.DeadWallSlot(4))),
                doraIds = setOf(rinshanDora)
            ), stale("BEAM_SWAP_STALE_OFFER"), "dora indicator slot is never a valid candidate"),
            case("bm-choose-replacement-allowed", choice(
                offerWindow = BeamSwapWindow.DEAD_WALL,
                targetSlot = BeamSwapSlot.ReplacementSlot(0),
                table = deadTable,
                drawnTileId = deadWallTiles[0].tileId,
                offerCandidates = listOf(BeamSwapCandidate(replacementTileId, BeamSwapSlot.ReplacementSlot(0)))
            ), CheatSubmissionCheck.Allowed, "replacement slot candidate consistent")
        )
    }

    /** 偷梁换柱墙窗基准桌：seat0 暗手 13 张（万子 1..9c0 + 索子 1..4c0），摸切牌 id 52，墙前段 8 张确定。 */
    private fun beamTable(): GameState {
        val hand = (0..12).map { Tile.fromTileId(it * 4) }
        return TestTableBuilder.create(
            hands = mapOf(0 to hand),
            drawn = 0 to Tile.fromTileId(52),
            wallPrefix = (56..84 step 4).map { Tile.fromTileId(it) },
            phase = GamePhase.CHEAT_WINDOW,
            currentPlayer = 0
        )
    }

    /** 偷梁换柱死山窗基准桌：先真实岭上摸一次（pointer 0→1）。 */
    private fun deadWallTable(): GameState {
        val hand = (0..13).map { Tile.fromTileId(it * 4) }
        val state = TestTableBuilder.create(
            hands = mapOf(0 to hand),
            wallPrefix = (56..68 step 4).map { Tile.fromTileId(it) },
            deadWallPrefix = (108..121).map { Tile.fromTileId(it) },
            phase = GamePhase.DISCARD,
            currentPlayer = 0
        )
        TileMovement.drawRinshan(state, 0)
        TestTableBuilder.assertPhysical(state)
        return state
    }

    // ------------------------------------------------------------------
    // 等额物理移动（V4 model.TileMovement 真实调用）
    // ------------------------------------------------------------------

    private data class MoveFact(val tileId: Int, val container: String, val seat: Int?, val index: Int?)

    private fun factOf(state: GameState, tile: Tile): MoveFact {
        return when (val loc = state.tilePool.getLocation(tile)) {
            TileLocation.WALL -> MoveFact(tile.tileId, "wall", null, state.wall.indexOf(tile))
            TileLocation.DEAD_WALL ->
                if (tile in state.deadWall) MoveFact(tile.tileId, "deadWall", null, state.deadWall.indexOf(tile))
                else MoveFact(tile.tileId, "replacement", null, state.deadWallReplacements.indexOf(tile))
            null -> error("tile ${tile.tileId} has no pool location")
            else -> when (loc.zone) {
                Zone.HAND -> MoveFact(tile.tileId, "hand", loc.player, null)
                Zone.DISCARD -> MoveFact(tile.tileId, "discard", loc.player, null)
                else -> error("unexpected zone ${loc.zone} for tile ${tile.tileId}")
            }
        }
    }

    private fun factsJson(state: GameState, tiles: List<Tile>): J =
        arr(tiles.map { t ->
            val f = factOf(state, t)
            obj(
                "tileId" to j(f.tileId),
                "container" to j(f.container),
                "seat" to (f.seat?.let { j(it) } ?: J.Null),
                "index" to (f.index?.let { j(it) } ?: J.Null)
            )
        })

    private fun gate(state: GameState, note: String) {
        TestTableBuilder.assertPhysical(state)
        com.majiang.state.StateInvariants.assertValid(state)
        assertTrue(state.cheatPoints.size == 4, note)
    }

    private fun tileMovementSection(): List<J> {
        val man2 = Tile(Suit.MAN, 2, 0)
        val man3 = Tile(Suit.MAN, 3, 0)
        val man5 = Tile(Suit.MAN, 5, 0)
        val man7 = Tile(Suit.MAN, 7, 0)
        val man8 = Tile(Suit.MAN, 8, 0)
        val man9 = Tile(Suit.MAN, 9, 0)
        // 12 张暗牌（man5c0 留作 drawn，避免夹具重复占用同一实体；差 1 张由 builder 确定性补齐）
        val seat0Hand = listOf(
            Tile(Suit.MAN, 1, 0), man2, man3,
            Tile(Suit.MAN, 4, 0),
            Tile(Suit.MAN, 6, 0), man7, man8, man9,
            Tile(Suit.MAN, 1, 1), Tile(Suit.MAN, 2, 1), Tile(Suit.MAN, 3, 1), Tile(Suit.MAN, 4, 1)
        )
        // 牌山/预留位使用独立副本实体，确保与手牌零重叠（物理守恒的关键）
        val wall9 = Tile(Suit.MAN, 9, 1)
        val wall8 = Tile(Suit.MAN, 8, 1)
        val wall7 = Tile(Suit.MAN, 7, 1)
        val reservedTile = Tile(Suit.MAN, 5, 1)
        val movedFrom = mutableListOf<J>()

        // --- M1a：暗手牌与活牌山指定槽位等额置换（无洞回填路径） ---
        run {
            val s = TestTableBuilder.create(
                hands = mapOf(0 to seat0Hand),
                drawn = 0 to man5,
                wallPrefix = listOf(wall9, wall8, wall7),
                phase = GamePhase.CHEAT_WINDOW,
                currentPlayer = 0
            )
            val pointerBefore = s.wallPointer
            val factsBefore = factsJson(s, listOf(man3, wall9))
            val displaced = TileMovement.swapHandWithWall(s, 0, man3, 0)
            assertEquals(wall9, displaced)
            assertEquals(man3, s.wall[0])
            gate(s, "m1a")
            movedFrom.add(obj(
                "caseId" to j("mv-swap-hand-with-wall-direct"),
                "note" to j("closed hand tile swaps with wall[0]; no backfill because the hand tile never lived in the wall"),
                "handTileId" to j(man3.tileId),
                "displacedTileId" to j(displaced.tileId),
                "wallIndex" to j(0),
                "wallPointerBefore" to j(pointerBefore), "wallPointerAfter" to j(s.wallPointer),
                "factsBefore" to factsBefore,
                "factsAfter" to factsJson(s, listOf(man3, wall9))
            ))
        }

        // --- M1b：刚摸到的牌（drawnTile）换墙牌 → 旧墙槽回填（洞被墙牌补上） ---
        run {
            val s = TestTableBuilder.create(
                hands = mapOf(0 to seat0Hand),
                wallPrefix = listOf(wall9, wall8, wall7),
                phase = GamePhase.DRAW, // 无预置 drawn：由 drawNormal 真实摸出以保留尸体槽
                currentPlayer = 0
            )
            val drawnResult = TileMovement.drawNormal(s, 0)
            val drawn = drawnResult.tile
            assertEquals(wall9, drawn)
            assertEquals(false, drawnResult.fromReservation)
            assertEquals(1, s.wallPointer)
            s.phase = GamePhase.DISCARD // 摸牌动作完成，进入出牌窗口以满足轮次不变量
            val displaced = TileMovement.swapHandWithWall(s, 0, drawn, 1)
            assertEquals(wall8, displaced)
            assertEquals(wall8, s.wall[0]) // drawn 旧槽位 0 被墙牌回填
            assertEquals(drawn, s.wall[1])
            assertEquals(wall8, s.players[0].drawnTile) // drawnTile 参与置换时被替换为墙牌
            gate(s, "m1b")
            movedFrom.add(obj(
                "caseId" to j("mv-swap-drawn-with-wall-backfill"),
                "note" to j("after drawNormal the drawn tile keeps its old wall slot; swapping it backfills that slot with the displaced wall tile"),
                "drawnTileId" to j(drawn.tileId),
                "displacedTileId" to j(displaced.tileId),
                "resolvedIndex" to j(1),
                "backfilledIndex" to j(0),
                "backfilledTileId" to j(s.wall[0].tileId),
                "wallPointerBeforeDraw" to j(0),
                "wallPointerAfterDraw" to j(1),
                "wallPointerAfterSwap" to j(s.wallPointer),
                "factsAfter" to factsJson(s, listOf(drawn, wall8))
            ))
        }

        // --- M2：袖里藏花预留牌随下一次正常摸牌交付 ---
        run {
            val s = TestTableBuilder.create(
                hands = mapOf(0 to seat0Hand),
                drawn = 0 to man5,
                wallPrefix = listOf(wall9, reservedTile, wall7),
                phase = GamePhase.DISCARD,
                currentPlayer = 0
            )
            val first = TileMovement.discard(s, 0, man5)
            assertEquals(null, s.players[0].drawnTile)
            s.putSleeveBloomReservation(SleeveBloomReservation(0, "bloom-offturn-0-req-1", reservedTile, "bloom-window-0-1", 1_000L))
            val pointerBefore = s.wallPointer
            val wallEndBefore = s.wallEndExclusive
            val delivery = TileMovement.drawNormal(s, 0)
            assertTrue(delivery.fromReservation)
            assertEquals(reservedTile, delivery.tile)
            assertEquals(1, delivery.wallIndex)
            assertEquals(wallEndBefore - 1, s.wallEndExclusive)
            gate(s, "m2")
            movedFrom.add(obj(
                "caseId" to j("mv-reservation-delivery"),
                "note" to j("the reserved tile is pulled out of the live wall by the owner's next normal draw"),
                "reservedTileId" to j(reservedTile.tileId),
                "fromReservation" to j(delivery.fromReservation),
                "wallIndex" to j(delivery.wallIndex),
                "wallEndExclusiveBefore" to j(wallEndBefore),
                "wallEndExclusiveAfter" to j(s.wallEndExclusive),
                "wallPointerBefore" to j(pointerBefore),
                "wallPointerAfter" to j(s.wallPointer),
                "factsAfter" to factsJson(s, listOf(reservedTile, first))
            ))
        }

        // --- M5：预留交付 → 同次摸牌 CheatWindow 偷梁换柱激活/选择 → 后续摸牌 ---
        // ApplicationCoordinator.kt:2100-2108 的冻结窗：[wallPointer, min(wallPointer+8,
        // wallEndExclusive))，逐槽读 wall、跳过受保护袖里藏花实体（本场景无其他预留，
        // 无跳过项）。交付（TileMovement.drawNormal 的 removeAt 路径）不推进指针，因此
        // 墙首未摸牌仍在窗内、已交付实体不在墙——下游 C# 从交付前输入布局重建牌桌后
        // 以实体集合逐位对照本节。
        run {
            val s = TestTableBuilder.create(
                hands = mapOf(0 to seat0Hand),
                drawn = 0 to man5,
                wallPrefix = listOf(wall9, reservedTile, wall7),
                phase = GamePhase.DISCARD,
                currentPlayer = 0
            )
            TileMovement.discard(s, 0, man5)
            val reservation = SleeveBloomReservation(0, "bloom-offturn-0-req-1", reservedTile, "bloom-window-0-1", 1_000L)
            s.putSleeveBloomReservation(reservation)
            // 交付前完整输入布局：136 placements（恒等 tileId 空间）+ 指针/王牌/河事实。
            // 下游 C# 以 FromPlacements 重建同一张牌桌——输入双射在任何动作执行前
            // 由黄金唯一定义，不依赖被测实现自身的输出。
            val inputPlacements = mutableListOf<J>()
            for (seat in 0..3) {
                val hand = s.players[seat]
                hand.closedTiles.forEachIndexed { idx, t ->
                    inputPlacements.add(obj(
                        "tileId" to j(t.tileId), "container" to j("hand"),
                        "seat" to j(seat), "index" to j(idx)
                    ))
                }
                hand.drawnTile?.let {
                    inputPlacements.add(obj(
                        "tileId" to j(it.tileId), "container" to j("hand"),
                        "seat" to j(seat), "index" to j(hand.closedTiles.size)
                    ))
                }
                hand.discards.forEachIndexed { idx, t ->
                    inputPlacements.add(obj(
                        "tileId" to j(t.tileId), "container" to j("discard"),
                        "seat" to j(seat), "index" to j(idx)
                    ))
                }
            }
            s.wall.forEachIndexed { idx, t ->
                inputPlacements.add(obj(
                    "tileId" to j(t.tileId), "container" to j("wall"),
                    "seat" to j(-1), "index" to j(idx)
                ))
            }
            s.deadWall.forEachIndexed { idx, t ->
                inputPlacements.add(obj(
                    "tileId" to j(t.tileId), "container" to j("deadWall"),
                    "seat" to j(-1), "index" to j(idx)
                ))
            }
            s.deadWallReplacements.forEachIndexed { idx, t ->
                inputPlacements.add(obj(
                    "tileId" to j(t.tileId), "container" to j("replacement"),
                    "seat" to j(-1), "index" to j(idx)
                ))
            }
            assertEquals(136, inputPlacements.size)
            val inputWallPointer = s.wallPointer
            val inputWallEndExclusive = s.wallEndExclusive
            val inputRinshanPointer = s.rinshanPointer
            val inputSeat = reservation.seat
            val inputDrawnTileTileId = s.players[0].drawnTile?.tileId ?: -1
            val inputLastDiscardTileId = s.lastDiscard?.tileId ?: -1
            val inputLastDiscardSeat = s.lastDiscardPlayer
            val inputKanCount = s.totalKanCount
            val inputDoraIndicatorTileIds = s.doraIndicators.map { it.tileId }
            val pointerBefore = s.wallPointer
            val firstUndrawn = s.wall[s.wallPointer]
            val successor = s.wall[2] // 预留槽 wallIndex=1 的后继实体（交付后前移一格）

            val delivery = TileMovement.drawNormal(s, 0)
            assertTrue(delivery.fromReservation)
            assertEquals(reservedTile, delivery.tile)
            assertEquals(1, delivery.wallIndex)
            val pointerAfterDelivery = s.wallPointer
            val wallEndAfterDelivery = s.wallEndExclusive
            assertEquals(pointerBefore, pointerAfterDelivery) // removeAt：指针不回退不推进

            // 同次摸牌 CheatWindow：冻结窗真实实体集合
            s.phase = GamePhase.CHEAT_WINDOW
            val endIdx = minOf(s.wallPointer + 8, s.wallEndExclusive)
            val candidateIds = mutableListOf<Int>()
            val candidateSlots = mutableListOf<Int>()
            for (i in s.wallPointer until endIdx) {
                val t = s.wall[i]
                if (s.isProtectedSleeveBloomTile(t)) continue
                candidateIds.add(t.tileId)
                candidateSlots.add(i)
            }
            assertEquals(8, candidateIds.size)
            assertEquals(true, candidateIds.contains(firstUndrawn.tileId))
            assertEquals(true, candidateIds.contains(successor.tileId))
            assertEquals(false, candidateIds.contains(reservedTile.tileId))

            // beam 选择：暗手牌首张与窗口首槽对换（TileMovement 真实路径）
            val handTile = s.players[0].closedTiles.first { it != delivery.tile }
            val beamGot = TileMovement.swapHandWithWall(s, 0, handTile, candidateSlots[0])
            assertEquals(handTile, s.wall[candidateSlots[0]])

            // 后续摸牌：换入窗口首槽的实体就是下一手普通摸牌
            s.phase = GamePhase.DISCARD
            TileMovement.discard(s, 0, beamGot)
            s.currentPlayer = 1
            val next = TileMovement.drawNormal(s, 1)
            assertEquals(handTile, next.tile)
            s.phase = GamePhase.DISCARD
            gate(s, "m5")
            movedFrom.add(obj(
                "caseId" to j("mv-reservation-delivery-then-beam"),
                "note" to j("after the reservation delivery (removeAt, pointer unmoved) the same draw's CHEAT_WINDOW freezes beam candidates from the unmoved pointer; the wall-front undrawn tile stays in the window, the delivered tile is out of the wall, and the swapped-in hand tile becomes the next normal draw"),
                "reservedTileId" to j(reservedTile.tileId),
                "deliveredTileId" to j(delivery.tile.tileId),
                "wallPointerBeforeDelivery" to j(pointerBefore),
                "wallPointerAfterDelivery" to j(pointerAfterDelivery),
                "wallEndExclusiveAfterDelivery" to j(wallEndAfterDelivery),
                "firstUndrawnTileId" to j(firstUndrawn.tileId),
                "successorTileId" to j(successor.tileId),
                "candidateTileIds" to intsJson(candidateIds),
                "candidateSlots" to intsJson(candidateSlots),
                "handTileId" to j(handTile.tileId),
                "beamGotTileId" to j(beamGot.tileId),
                "nextDrawTileId" to j(next.tile.tileId),
                "inputPlacements" to arr(inputPlacements),
                "inputWallPointer" to j(inputWallPointer),
                "inputWallEndExclusive" to j(inputWallEndExclusive),
                "inputRinshanPointer" to j(inputRinshanPointer),
                "inputSeat" to j(inputSeat),
                "inputDrawnTileTileId" to j(inputDrawnTileTileId),
                "inputLastDiscardTileId" to j(inputLastDiscardTileId),
                "inputLastDiscardSeat" to j(inputLastDiscardSeat),
                "inputKanCount" to j(inputKanCount),
                "inputDoraIndicatorTileIds" to intsJson(inputDoraIndicatorTileIds),
                "factsAfter" to factsJson(s, listOf(reservedTile, firstUndrawn, handTile, beamGot))
            ))
        }

        // --- M3：岭上摸牌与 swapDrawnWithNextRinshan ---
        run {
            val s = TestTableBuilder.create(
                hands = mapOf(0 to seat0Hand + listOf(Tile(Suit.MAN, 5, 1))),
                wallPrefix = (56..68 step 4).map { Tile.fromTileId(it) },
                deadWallPrefix = (108..121).map { Tile.fromTileId(it) },
                phase = GamePhase.DISCARD,
                currentPlayer = 0
            )
            val wallEndBefore = s.wallEndExclusive
            val rinshan = TileMovement.drawRinshan(s, 0)
            assertEquals(1, s.rinshanPointer)
            assertEquals(wallEndBefore - 1, s.wallEndExclusive)
            assertEquals(1, s.deadWallReplacements.size)
            val replacement = s.deadWallReplacements[0]
            val next = TileMovement.swapDrawnWithNextRinshan(s, 0)
            assertEquals(Tile.fromTileId(109), next)
            assertEquals(rinshan.tileId, s.deadWall[1].tileId)
            assertEquals(next.tileId, s.players[0].drawnTile!!.tileId)
            TestTableBuilder.assertPhysical(s) // 合成杠后场景，轮次不变量不适用，仅物理守恒闸门
            movedFrom.add(obj(
                "caseId" to j("mv-rinshan-and-drawn-swap"),
                "note" to j("drawRinshan takes deadWall[0] and a wall-end replacement; swapDrawnWithNextRinshan exchanges drawn with deadWall[1] in place"),
                "rinshanTileId" to j(rinshan.tileId),
                "nextRinshanTileId" to j(next.tileId),
                "replacementTileId" to j(replacement.tileId),
                "rinshanPointerAfterDraw" to j(s.rinshanPointer),
                "wallEndExclusiveBefore" to j(wallEndBefore),
                "wallEndExclusiveAfter" to j(s.wallEndExclusive),
                "deadWallSlot1AfterSwap" to j(s.deadWall[1].tileId),
                "factsAfter" to factsJson(s, listOf(rinshan, next, replacement))
            ))
        }

        // --- M4：swapHandWithDeadWall（岭上槽原地对调 / 宝牌指示牌拒绝 / WallSlot 拒绝 / 替补槽） ---
        run {
            val s = TestTableBuilder.create(
                hands = mapOf(0 to seat0Hand + listOf(Tile(Suit.MAN, 5, 1))),
                wallPrefix = (56..68 step 4).map { Tile.fromTileId(it) },
                deadWallPrefix = (108..121).map { Tile.fromTileId(it) },
                phase = GamePhase.DISCARD,
                currentPlayer = 0
            )
            val rinshan = TileMovement.drawRinshan(s, 0)
            val got = TileMovement.swapHandWithDeadWall(s, 0, man3, BeamSwapSlot.DeadWallSlot(1), Tile.fromTileId(109).tileId)
            assertEquals(Tile.fromTileId(109), got)
            assertEquals(man3.tileId, s.deadWall[1].tileId)
            TestTableBuilder.assertPhysical(s) // 合成杠后场景，仅物理守恒闸门

            val doraTile = s.deadWall[4]
            s.doraIndicators.add(doraTile)
            val doraEx = exName { TileMovement.swapHandWithDeadWall(s, 0, man2, BeamSwapSlot.DeadWallSlot(4), doraTile.tileId) }
            assertEquals("IllegalArgumentException", doraEx)
            val wallSlotEx = exName { TileMovement.swapHandWithDeadWall(s, 0, man2, BeamSwapSlot.WallSlot(54), s.wall[54].tileId) }
            assertEquals("IllegalArgumentException", wallSlotEx)
            val replacementTile = s.deadWallReplacements[0]
            val gotReplacement = TileMovement.swapHandWithDeadWall(s, 0, man2, BeamSwapSlot.ReplacementSlot(0), replacementTile.tileId)
            assertEquals(replacementTile, gotReplacement)
            assertEquals(man2.tileId, s.deadWallReplacements[0].tileId)
            TestTableBuilder.assertPhysical(s) // dora 指示牌已翻开，仅物理守恒闸门

            movedFrom.add(obj(
                "caseId" to j("mv-swap-hand-with-dead-wall"),
                "note" to j("dead-wall swap is in place; revealed dora indicator and wall slots are refused; replacement slots participate"),
                "rinshanTileId" to j(rinshan.tileId),
                "handTileId" to j(man3.tileId),
                "gotTileId" to j(got.tileId),
                "deadWallSlot1AfterSwap" to j(s.deadWall[1].tileId),
                "doraSlotIndex" to j(4),
                "doraRefusalException" to j(doraEx),
                "wallSlotRefusalException" to j(wallSlotEx),
                "replacementTileIdBefore" to j(replacementTile.tileId),
                "replacementSlot0AfterSwap" to j(s.deadWallReplacements[0].tileId),
                "factsAfter" to factsJson(s, listOf(man3, man2, got, gotReplacement))
            ))
        }

        return movedFrom
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private fun exName(block: () -> Unit): String = try {
        block()
        ""
    } catch (e: Exception) {
        e::class.java.simpleName
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

    private fun intsJson(values: List<Int>): J = J.Array(values.map { j(it) }, compact = true)

// -------------------------------------------------------------------------
// Small JSON helpers (local copy of the shuffle exporter pattern)
// -------------------------------------------------------------------------

    private sealed class J {
        data class Str(val value: String) : J()
        data class Num(val value: Long) : J()
        data class Bool(val value: Boolean) : J()
        data object Null : J()
        data class Array(val items: List<J>, val compact: Boolean = false) : J()
        data class Object(val fields: MutableList<Pair<String, J>>) : J()

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
                            if (!childCompact) sb.append('\n').append(" ".repeat(indent + 2))
                            item.renderTo(sb, indent + 2, childCompact)
                        }
                        if (!childCompact) sb.append('\n').append(" ".repeat(indent))
                    }
                    sb.append(']')
                }
                is Object -> {
                    sb.append('{')
                    if (fields.isNotEmpty()) {
                        fields.forEachIndexed { index, field ->
                            if (index > 0) sb.append(',')
                            if (!compact) sb.append('\n').append(" ".repeat(indent + 2))
                            sb.append(quote(field.first)).append(':')
                            if (!compact) sb.append(' ')
                            field.second.renderTo(sb, indent + 2, compact)
                        }
                        if (!compact) sb.append('\n').append(" ".repeat(indent))
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
    private fun obj(vararg fields: Pair<String, J>): J = J.Object(fields.toMutableList())
    private fun arr(items: List<J>): J = J.Array(items)
}
