package com.majiang.ai.cheat

import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.ai.AiCandidateFactory
import com.majiang.ai.AiCandidateLimits
import com.majiang.ai.AiCheatPolicy
import com.majiang.ai.AiDecisionStamp
import com.majiang.ai.AiPhase
import com.majiang.ai.BasicAiDecisionStrategy
import com.majiang.authority.TableAuthority
import com.majiang.cheat.beam.BeamSwapConfig
import com.majiang.cheat.bloom.SleeveBloomConfig
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.cheat.bloom.SleeveBloomOfferMode
import com.majiang.cheat.mark.TileMarkConfig
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.stacking.ShuffleConfig
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

@Timeout(40)
class CheatAiCapabilityTest {

    private class Clock(var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis(): Long = now
    }

    private val controls = List(4) { SeatControlView(it, true, "HUMAN", 0) }

    private fun economy(vararg costs: Pair<String, Int>) = CheatEconomyConfig(
        enabled = true,
        initialPoints = List(4) { 50 },
        costs = costs.toMap()
    )

    private fun table(
        state: com.majiang.state.GameState,
        config: MatchConfig,
        controllers: List<SeatController> = listOf(
            SeatController.RemoteHuman(false),
            SeatController.Computer(),
            SeatController.RemoteHuman(false),
            SeatController.RemoteHuman(false)
        ),
        clock: Clock = Clock()
    ): TableAuthority = TableAuthority(
        config = config,
        clock = clock,
        controllers = controllers,
        initialState = state.also { it.cheatPoints.fill(50) }
    )

    private fun activeState(): com.majiang.state.GameState =
        TestTableBuilder.create(
            drawn = 1 to Tile(Suit.SOU, 4, 3),
            phase = GamePhase.CHEAT_WINDOW,
            currentPlayer = 1
        ).also {
            it.lastDrawKind = SleeveBloomDrawKind.NORMAL
            it.turnNumber = 5
        }

    private fun baseConfig(
        policy: AiCheatPolicy = AiCheatPolicy(enabled = true, maxSpendPerRound = 20),
        mark: Boolean = false,
        bloom: Boolean = false,
        beam: Boolean = false
    ): MatchConfig = MatchConfig(
        cheatEconomy = economy(
            "TILE_MARK" to 10,
            "SLEEVE_BLOOM" to 10,
            "BEAM_SWAP" to 10
        ),
        shuffleConfig = ShuffleConfig(enabled = false),
        tileMarkConfig = TileMarkConfig(mark),
        sleeveBloomConfig = SleeveBloomConfig(bloom),
        beamSwapConfig = BeamSwapConfig(beam),
        aiCheatPolicy = policy
    )

    @Test
    fun `custom tile mark fee flows through projection candidate budget and ledger`() {
        val clock = Clock()
        val custom = MatchConfig(
            cheatEconomy = CheatEconomyConfig(
                enabled = true,
                initialPoints = List(4) { 50 },
                costs = mapOf("TILE_MARK" to 5)
            ),
            shuffleConfig = ShuffleConfig(enabled = false),
            tileMarkConfig = TileMarkConfig(true),
            aiCheatPolicy = AiCheatPolicy(enabled = true, maxSpendPerRound = 5)
        )
        table(activeState(), custom, clock = clock).use { authority ->
            val snapshot = authority.buildAiSeatSnapshot(1, clock.now)
            assertEquals(5, snapshot.cheatView.tileMark!!.cost)
            assertEquals(5, snapshot.limits.spendablePoints)
            assertTrue(snapshot.limits.allows(AiCapabilityIds.TILE_MARK))

            val registry = CheatAiRegistry.builtIn(authority)
            val candidate = registry.candidates(snapshot, snapshot.limits)
                .first { it.capabilityId == AiCapabilityIds.TILE_MARK }
            assertEquals(5, candidate.cost, "AI candidate must use the authorized projection fee")

            val result = registry.require(AiCapabilityIds.TILE_MARK)
                .submit(1, candidate, snapshot.stamp, clock.now)
            assertEquals(ReceiptStatus.APPLIED, result.receipt.status, result.receipt.reason)

            val state = authority.coordinator.activeRound!!.readState()
            TestTableBuilder.assertPhysical(state)
            assertEquals(45, state.cheatPoints[1])
            assertEquals(1, state.cheatEntries.size)
            assertEquals(-5, state.cheatEntries.single().amount, "Ledger must charge the configured fee")
            assertEquals(
                1,
                authority.querySnapshot(1, "custom-fee", 0, controls).tileMark!!.activeGroups.size
            )
        }
    }

    @Test
    fun `tile mark capability submits through authority and preserves 136 identities and ledger`() {
        val clock = Clock()
        table(activeState(), baseConfig(mark = true), clock = clock).use { authority ->
            val snapshot = authority.buildAiSeatSnapshot(1, clock.now)
            assertEquals(AiPhase.ORDINARY_ACTION, snapshot.phase)
            assertTrue(snapshot.limits.allows(AiCapabilityIds.TILE_MARK))
            assertTrue(snapshot.cheatView.tileMark!!.canApply)

            val registry = CheatAiRegistry.builtIn(authority)
            val candidates = registry.candidates(snapshot, snapshot.limits)
            val mark = candidates.first { it.capabilityId == AiCapabilityIds.TILE_MARK }
            val result = registry.require(AiCapabilityIds.TILE_MARK)
                .submit(1, mark, snapshot.stamp, clock.now)

            assertEquals(ReceiptStatus.APPLIED, result.receipt.status, result.receipt.reason)
            assertTrue(result.stateChanged)
            val state = authority.coordinator.activeRound!!.readState()
            TestTableBuilder.assertPhysical(state)
            assertEquals(40, state.cheatPoints[1])
            assertEquals(1, state.cheatEntries.size)
            assertEquals(-10, state.cheatEntries.single().amount)
            assertEquals(1, authority.querySnapshot(1, "ai-test", 0, controls).tileMark!!.activeGroups.size)
        }
    }

    @Test
    fun `sleeve bloom activation is followed by a separate offer choice decision`() {
        val clock = Clock()
        table(activeState(), baseConfig(bloom = true), clock = clock).use { authority ->
            val registry = CheatAiRegistry.builtIn(authority)
            val first = authority.buildAiSeatSnapshot(1, clock.now)
            val activation = registry.candidates(first, first.limits)
                .first { it.capabilityId == AiCapabilityIds.SLEEVE_BLOOM }
            assertEquals(AiPhase.ORDINARY_ACTION, activation.phase)
            val applied = registry.require(AiCapabilityIds.SLEEVE_BLOOM)
                .submit(1, activation, first.stamp, clock.now)
            assertEquals(ReceiptStatus.APPLIED, applied.receipt.status, applied.receipt.reason)

            val offerSnapshot = authority.buildAiSeatSnapshot(1, clock.now)
            assertEquals(AiPhase.OFFER_CHOICE, offerSnapshot.phase)
            val offer = offerSnapshot.cheatView.sleeveBloom!!.pendingOffer!!
            assertEquals(SleeveBloomOfferMode.IMMEDIATE, offer.mode)
            val choices = registry.candidates(offerSnapshot, offerSnapshot.limits)
                .filter { it.capabilityId == AiCapabilityIds.SLEEVE_BLOOM }
            assertTrue(choices.any { it.isSafeDefault })
            val firstChoice = choices.first { it.parameters.string("keep") == "FIRST" }
            val chosen = registry.require(AiCapabilityIds.SLEEVE_BLOOM)
                .submit(1, firstChoice, offerSnapshot.stamp, clock.now)
            assertEquals(ReceiptStatus.APPLIED, chosen.receipt.status, chosen.receipt.reason)

            val state = authority.coordinator.activeRound!!.readState()
            TestTableBuilder.assertPhysical(state)
            assertEquals(40, state.cheatPoints[1])
            assertEquals(1, state.cheatEntries.size)
            assertEquals(null, state.sleeveBloomOffer)
            assertTrue(state.players[1].drawnTile != null)
        }
    }

    @Test
    fun `beam swap activation exposes only owned candidates and keeps safely`() {
        val clock = Clock()
        table(activeState(), baseConfig(beam = true), clock = clock).use { authority ->
            val registry = CheatAiRegistry.builtIn(authority)
            val first = authority.buildAiSeatSnapshot(1, clock.now)
            val activation = registry.candidates(first, first.limits)
                .first { it.capabilityId == AiCapabilityIds.BEAM_SWAP }
            val applied = registry.require(AiCapabilityIds.BEAM_SWAP)
                .submit(1, activation, first.stamp, clock.now)
            assertEquals(ReceiptStatus.APPLIED, applied.receipt.status, applied.receipt.reason)

            val offerSnapshot = authority.buildAiSeatSnapshot(1, clock.now)
            assertEquals(AiPhase.OFFER_CHOICE, offerSnapshot.phase)
            val offer = offerSnapshot.cheatView.beamSwap!!.pendingOffer!!
            assertTrue(offer.candidates.all { it.marked == (it.tile != null) })
            val choices = registry.candidates(offerSnapshot, offerSnapshot.limits)
            val keep = choices.first { it.isSafeDefault }
            val chosen = registry.require(AiCapabilityIds.BEAM_SWAP)
                .submit(1, keep, offerSnapshot.stamp, clock.now)
            assertEquals(ReceiptStatus.APPLIED, chosen.receipt.status, chosen.receipt.reason)

            val state = authority.coordinator.activeRound!!.readState()
            TestTableBuilder.assertPhysical(state)
            assertEquals(40, state.cheatPoints[1])
            assertEquals(null, state.beamSwapOffer)
        }
    }

    @Test
    fun `disabled policy and zero budget expose no cheat candidates`() {
        for (policy in listOf(
            AiCheatPolicy(enabled = false, maxSpendPerRound = 20),
            AiCheatPolicy(enabled = true, maxSpendPerRound = 0)
        )) {
            table(activeState(), baseConfig(policy = policy, mark = true, bloom = true, beam = true)).use { authority ->
                val snapshot = authority.buildAiSeatSnapshot(1, 1_000L)
                assertTrue(snapshot.limits.enabledCapabilityIds.isEmpty())
                val candidates = CheatAiRegistry.builtIn(authority).candidates(snapshot, snapshot.limits)
                assertTrue(candidates.isEmpty())
            }
        }
    }

    @Test
    fun `capability toggles and reserved points are enforced before candidate generation`() {
        val toggledOff = baseConfig(
            policy = AiCheatPolicy(
                enabled = true,
                maxSpendPerRound = 30,
                capabilityToggles = mapOf(AiCapabilityIds.TILE_MARK to false)
            ),
            mark = true
        )
        table(activeState(), toggledOff).use { authority ->
            val snapshot = authority.buildAiSeatSnapshot(1, 1_000L)
            assertFalse(snapshot.limits.allows(AiCapabilityIds.TILE_MARK))
            assertTrue(
                CheatAiRegistry.builtIn(authority).candidates(snapshot, snapshot.limits)
                    .none { it.capabilityId == AiCapabilityIds.TILE_MARK }
            )
        }

        val reserved = baseConfig(
            policy = AiCheatPolicy(enabled = true, maxSpendPerRound = 60, reservePoints = 45),
            mark = true
        )
        table(activeState(), reserved).use { authority ->
            val snapshot = authority.buildAiSeatSnapshot(1, 1_000L)
            assertEquals(5, snapshot.limits.spendablePoints)
            assertFalse(snapshot.limits.canSpend(10))
            assertTrue(
                CheatAiRegistry.builtIn(authority).candidates(snapshot, snapshot.limits)
                    .none { it.capabilityId == AiCapabilityIds.TILE_MARK }
            )
        }
    }

    @Test
    fun `tile mark repeated after a state version change uses a new request id`() {
        val clock = Clock()
        val config = MatchConfig(
            cheatEconomy = CheatEconomyConfig(
                enabled = true,
                initialPoints = List(4) { 50 },
                costs = mapOf("TILE_MARK" to 5)
            ),
            shuffleConfig = ShuffleConfig(enabled = false),
            tileMarkConfig = TileMarkConfig(true),
            aiCheatPolicy = AiCheatPolicy(enabled = true, maxSpendPerRound = 10)
        )
        table(activeState(), config, clock = clock).use { authority ->
            val registry = CheatAiRegistry.builtIn(authority)
            val firstSnapshot = authority.buildAiSeatSnapshot(1, clock.now)
            val firstCandidate = registry.candidates(firstSnapshot, firstSnapshot.limits)
                .first { it.capabilityId == AiCapabilityIds.TILE_MARK }
            val firstRequestId = AiRequestId.of(AiCapabilityIds.TILE_MARK, firstSnapshot.stamp, firstCandidate.optionId)
            assertEquals(
                ReceiptStatus.APPLIED,
                registry.require(AiCapabilityIds.TILE_MARK)
                    .submit(1, firstCandidate, firstSnapshot.stamp, clock.now).receipt.status
            )

            val secondSnapshot = authority.buildAiSeatSnapshot(1, clock.now)
            assertNotEquals(
                firstSnapshot.stamp.stateVersion,
                secondSnapshot.stamp.stateVersion,
                "Tile mark must advance the round state version"
            )
            val secondCandidate = registry.candidates(secondSnapshot, secondSnapshot.limits)
                .first { it.capabilityId == AiCapabilityIds.TILE_MARK }
            val secondRequestId = AiRequestId.of(AiCapabilityIds.TILE_MARK, secondSnapshot.stamp, secondCandidate.optionId)
            assertNotEquals(firstRequestId, secondRequestId, "stateVersion must participate in request identity")

            val secondResult = registry.require(AiCapabilityIds.TILE_MARK)
                .submit(1, secondCandidate, secondSnapshot.stamp, clock.now)
            assertEquals(ReceiptStatus.APPLIED, secondResult.receipt.status, secondResult.receipt.reason)

            val state = authority.coordinator.activeRound!!.readState()
            TestTableBuilder.assertPhysical(state)
            assertEquals(40, state.cheatPoints[1])
            assertEquals(2, state.cheatEntries.size)
            assertTrue(state.cheatEntries.all { it.amount == -5 })
            assertEquals(
                2,
                authority.querySnapshot(1, "repeat-fee", 0, controls).tileMark!!.activeGroups.size
            )
        }
    }

    @Test
    fun `takeover only spends when the policy explicitly allows it`() {
        val allowed = baseConfig(
            policy = AiCheatPolicy(enabled = true, maxSpendPerRound = 20, takeoverMaySpend = true),
            mark = true
        )
        table(
            activeState(),
            allowed,
            controllers = listOf(
                SeatController.RemoteHuman(false),
                SeatController.Takeover(),
                SeatController.RemoteHuman(false),
                SeatController.RemoteHuman(false)
            )
        ).use { authority ->
            val snapshot = authority.buildAiSeatSnapshot(1, 1_000L)
            assertTrue(snapshot.takeover)
            assertTrue(snapshot.limits.allows(AiCapabilityIds.TILE_MARK))
            assertTrue(
                CheatAiRegistry.builtIn(authority).candidates(snapshot, snapshot.limits)
                    .any { it.capabilityId == AiCapabilityIds.TILE_MARK }
            )
        }

        val denied = baseConfig(
            policy = AiCheatPolicy(enabled = true, maxSpendPerRound = 20, takeoverMaySpend = false),
            mark = true
        )
        table(
            activeState(),
            denied,
            controllers = listOf(
                SeatController.RemoteHuman(false),
                SeatController.Takeover(),
                SeatController.RemoteHuman(false),
                SeatController.RemoteHuman(false)
            )
        ).use { authority ->
            val snapshot = authority.buildAiSeatSnapshot(1, 1_000L)
            assertTrue(snapshot.limits.enabledCapabilityIds.isEmpty())
            assertTrue(
                CheatAiRegistry.builtIn(authority).candidates(snapshot, snapshot.limits).isEmpty()
            )
        }
    }

    @Test
    fun `legacy aiMaySpend still enables shuffle preparation for native computer only`() {
        val clock = Clock()
        val config = MatchConfig(
            cheatEconomy = CheatEconomyConfig(enabled = true, initialPoints = List(4) { 50 }),
            shuffleConfig = ShuffleConfig(enabled = true, aiMaySpend = true),
            aiCheatPolicy = AiCheatPolicy()
        )
        TableAuthority(
            config = config,
            clock = clock,
            controllers = listOf(
                SeatController.RemoteHuman(false),
                SeatController.Computer(),
                SeatController.RemoteHuman(false),
                SeatController.RemoteHuman(false)
            )
        ).use { authority ->
            assertEquals(ReceiptStatus.APPLIED, authority.start(0, clock.now).receipt.status)
            val native = authority.buildAiSeatSnapshot(1, clock.now)
            assertEquals(AiPhase.SHUFFLE_PREPARATION, native.phase)
            assertTrue(native.limits.allows(AiCapabilityIds.SHUFFLE_PREPARATION))
            assertEquals(60, native.limits.maxSpend)

            authority.setSeatController(1, SeatController.Takeover())
            val takeover = authority.buildAiSeatSnapshot(1, clock.now)
            assertFalse(takeover.limits.allows(AiCapabilityIds.SHUFFLE_PREPARATION))
        }
    }

    @Test
    fun `new capability is discovered and selected without dispatcher changes`() {
        val clock = Clock()
        table(activeState(), baseConfig(mark = false), clock = clock).use { authority ->
            var submitted = 0
            val probe = object : CheatAiCapability {
                override val id: String = "test_probe_capability"
                override val version: Int = 1
                override fun describe(): AiCapabilityDescriptor = AiCapabilityDescriptor(
                    id = id,
                    version = version,
                    phases = setOf(AiPhase.ORDINARY_ACTION),
                    supportsSubmit = true,
                    maxCandidates = 2
                )

                override fun candidates(
                    view: com.majiang.ai.AiSeatSnapshot,
                    limits: AiCandidateLimits
                ): List<com.majiang.ai.AiCandidate> = listOf(
                    com.majiang.ai.AiCandidate(
                        optionId = "$id:probe",
                        capabilityId = id,
                        phase = AiPhase.ORDINARY_ACTION,
                        cost = 0,
                        risk = com.majiang.ai.AiRisk(),
                        estimatedEffect = com.majiang.ai.AiEffect(handValue = 500, note = "probe"),
                        terminalForWindow = true,
                        parameters = com.majiang.ai.AiVisibleParams.EMPTY
                    )
                )

                override fun submit(
                    seat: Int,
                    selected: com.majiang.ai.AiCandidate,
                    stamp: AiDecisionStamp,
                    now: Long
                ): TableAuthority.MutationResult {
                    submitted++
                    return TableAuthority.MutationResult(
                        com.majiang.access.ActionReceipt(selected.optionId, ReceiptStatus.APPLIED),
                        emptyList(),
                        true
                    )
                }
            }
            val registry = CheatAiRegistry(
                listOf(
                    TileMarkAiCapability(authority),
                    SleeveBloomAiCapability(authority),
                    BeamSwapAiCapability(authority),
                    ShufflePreparationAiCapability(authority),
                    probe
                )
            )
            val snapshot = authority.buildAiSeatSnapshot(
                1,
                clock.now,
                registeredCapabilityIds = registry.ids.toSet()
            )
            val candidates = AiCandidateFactory.build(snapshot, registry)
            assertTrue(candidates.any { it.capabilityId == probe.id })
            val chosen = BasicAiDecisionStrategy().choose(
                com.majiang.ai.AiDecisionContext(snapshot, candidates)
            )
            assertEquals("${probe.id}:probe", chosen)
            val selected = registry.candidates(snapshot, snapshot.limits)
                .first { it.capabilityId == probe.id }
            registry.require(probe.id).submit(1, selected, snapshot.stamp, clock.now)
            assertEquals(1, submitted)
        }
    }

    @Test
    fun `different off turn windows produce distinct request ids and both submit`() {
        val clock = Clock()
        val state = TestTableBuilder.create(
            phase = GamePhase.DRAW,
            currentPlayer = 0
        ).also {
            it.turnNumber = 5
            it.cheatPoints.fill(50)
            it.openSleeveBloomWindow(1)
        }
        val config = MatchConfig(
            cheatEconomy = CheatEconomyConfig(
                enabled = true,
                initialPoints = List(4) { 50 },
                costs = mapOf("SLEEVE_BLOOM" to 10)
            ),
            shuffleConfig = ShuffleConfig(enabled = false),
            sleeveBloomConfig = SleeveBloomConfig(true),
            aiCheatPolicy = AiCheatPolicy(enabled = true, maxSpendPerRound = 30)
        )
        table(
            state,
            config,
            controllers = listOf(
                SeatController.RemoteHuman(false),
                SeatController.Computer(),
                SeatController.RemoteHuman(false),
                SeatController.RemoteHuman(false)
            ),
            clock = clock
        ).use { authority ->
            val registry = CheatAiRegistry.builtIn(authority)

            val firstSnapshot = authority.buildAiSeatSnapshot(1, clock.now)
            assertEquals(AiPhase.ORDINARY_ACTION, firstSnapshot.phase)
            val firstWindow = firstSnapshot.stamp.windowId
            assertNotNull(firstWindow)
            val firstCandidate = registry.candidates(firstSnapshot, firstSnapshot.limits)
                .first { it.capabilityId == AiCapabilityIds.SLEEVE_BLOOM && it.optionId.endsWith(":offturn") }
            val firstRequestId = AiRequestId.of(AiCapabilityIds.SLEEVE_BLOOM, firstSnapshot.stamp, firstCandidate.optionId)
            val firstResult = registry.require(AiCapabilityIds.SLEEVE_BLOOM)
                .submit(1, firstCandidate, firstSnapshot.stamp, clock.now)
            assertEquals(ReceiptStatus.APPLIED, firstResult.receipt.status, firstResult.receipt.reason)
            assertEquals(40, authority.coordinator.activeRound!!.readState().cheatPoints[1])

            // 清除第一次预约并签发第二个窗口，模拟同一座位同一局的第二次 off-turn 预约。
            val live = authority.coordinator.activeRound!!.state
            live.removeSleeveBloomOffTurnOffer(1)
            live.removeSleeveBloomReservation(1)
            val secondWindow = live.openSleeveBloomWindow(1)
            assertNotEquals(firstWindow, secondWindow)

            val secondSnapshot = authority.buildAiSeatSnapshot(1, clock.now)
            assertEquals(
                firstSnapshot.stamp.decisionId,
                secondSnapshot.stamp.decisionId,
                "Regression precondition: old request id would collide on the same decisionId"
            )
            assertNotEquals(firstSnapshot.stamp.stateVersion, secondSnapshot.stamp.stateVersion)
            assertEquals(firstCandidate.optionId, registry.candidates(secondSnapshot, secondSnapshot.limits)
                .first { it.capabilityId == AiCapabilityIds.SLEEVE_BLOOM && it.optionId.endsWith(":offturn") }
                .optionId, "Option id is intentionally stable across windows")
            assertEquals(secondWindow, secondSnapshot.stamp.windowId)
            val secondRequestId = AiRequestId.of(AiCapabilityIds.SLEEVE_BLOOM, secondSnapshot.stamp, firstCandidate.optionId)
            assertNotEquals(firstRequestId, secondRequestId, "windowId must participate in request identity")

            val secondCandidate = registry.candidates(secondSnapshot, secondSnapshot.limits)
                .first { it.capabilityId == AiCapabilityIds.SLEEVE_BLOOM && it.optionId.endsWith(":offturn") }
            val secondResult = registry.require(AiCapabilityIds.SLEEVE_BLOOM)
                .submit(1, secondCandidate, secondSnapshot.stamp, clock.now)
            assertEquals(ReceiptStatus.APPLIED, secondResult.receipt.status, secondResult.receipt.reason)

            val finalState = authority.coordinator.activeRound!!.readState()
            TestTableBuilder.assertPhysical(finalState)
            assertEquals(30, finalState.cheatPoints[1])
            assertEquals(2, finalState.cheatEntries.size)
            assertTrue(finalState.cheatEntries.all { it.amount == -10 })
        }
    }

    @Test
    fun `snapshot only exposes own hand and authorized private cheat data`() {
        val clock = Clock()
        val state = TestTableBuilder.create(
            hands = mapOf(
                0 to listOf(Tile(Suit.MAN, 1), Tile(Suit.MAN, 1, 1)),
                2 to listOf(Tile(Suit.PIN, 9), Tile(Suit.PIN, 9, 1)),
                3 to listOf(Tile(Suit.SOU, 9), Tile(Suit.SOU, 9, 1))
            ),
            drawn = 1 to Tile(Suit.SOU, 4, 3),
            phase = GamePhase.CHEAT_WINDOW,
            currentPlayer = 1
        ).also { it.lastDrawKind = SleeveBloomDrawKind.NORMAL }
        table(state, baseConfig(mark = true, bloom = true, beam = true), clock = clock).use { authority ->
            val snapshot = authority.buildAiSeatSnapshot(1, clock.now)
            val ownIds = (state.players[1].closedTiles + listOfNotNull(state.players[1].drawnTile))
                .map { it.tileId }.toSet()
            assertTrue(snapshot.view.myClosedTiles.all { it.tileId in ownIds })
            assertTrue(snapshot.view.discards.all { it.isEmpty() })
            assertNotNull(snapshot.cheatView.tileMark)
            assertNotNull(snapshot.cheatView.sleeveBloom)
            assertNotNull(snapshot.cheatView.beamSwap)
            // 不可变快照：深拷贝后的候选参数与原快照互不影响。
            val copy = snapshot.deepCopy()
            assertEquals(snapshot.view.myClosedTiles, copy.view.myClosedTiles)
            assertEquals(snapshot.stamp, copy.stamp)
        }
    }
}
