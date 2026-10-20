package com.majiang.ai

import com.majiang.ai.cheat.AiCapabilityDescriptor
import com.majiang.ai.cheat.AiCapabilityIds
import com.majiang.ai.cheat.AiRequestId
import com.majiang.ai.cheat.CheatAiCapability
import com.majiang.ai.cheat.CheatAiRegistry
import com.majiang.application.ActionOption
import com.majiang.application.PlayerDecision
import com.majiang.authority.TableAuthority
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout

@Timeout(30)
class AiDecisionStrategyTest {

    private val tile = Tile(Suit.MAN, 3)

    private fun decision(includeRiichi: Boolean = true): PlayerDecision {
        val options = mutableListOf(
            ActionOption("opt-discard", GameAction.DiscardTile(1, tile))
        )
        if (includeRiichi) options.add(ActionOption("opt-riichi", GameAction.DeclareRiichi(1, tile)))
        return PlayerDecision("round", "decision", 0L, options)
    }

    private fun view(decision: PlayerDecision): PlayerView = PlayerView.create(
        viewerSeat = 1,
        phase = GamePhase.DISCARD,
        roundWind = 1,
        roundNumber = 1,
        honbaSticks = 0,
        riichiSticks = 0,
        dealerSeat = 0,
        currentTurnPlayer = 1,
        wallRemaining = 40,
        doraIndicators = emptyList(),
        scores = listOf(25000, 25000, 25000, 25000),
        riichiStatus = listOf(false, false, false, false),
        discards = List(4) { emptyList() },
        melds = List(4) { emptyList() },
        handTileCounts = listOf(13, 14, 13, 13),
        myClosedTiles = listOf(tile),
        myDrawnTile = null,
        legalActions = decision.options.map { it.action }
    )

    private fun snapshot(
        decision: PlayerDecision,
        phase: AiPhase = AiPhase.ORDINARY_ACTION
    ): AiSeatSnapshot {
        val stamp = AiDecisionStamp(
            seat = 1,
            sessionId = "session",
            roundId = "round",
            decisionId = "decision",
            controlEpoch = 0L,
            phase = phase,
            stateVersion = 1L,
            offerId = if (phase == AiPhase.OFFER_CHOICE) "offer" else null,
            windowId = null,
            deadlineMillis = 10_000L
        )
        return AiSeatSnapshot(
            stamp = stamp,
            view = view(decision),
            decision = decision,
            cheatView = AiCheatView(),
            limits = AiCandidateLimits(
                enabledCapabilityIds = setOf("test_cheat"),
                maxSpend = 60,
                remainingPoints = 50,
                reservePoints = 0,
                candidateLimit = 6,
                riskTolerance = 0.5
            ),
            policy = AiCheatPolicy(enabled = true),
            takeover = false
        )
    }

    private class FirstActionStrategy : AIStrategy {
        override val name: String = "first"
        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? =
            legalActions.firstOrNull()
    }

    private fun cheatCandidate(optionId: String, effect: Int, safe: Boolean = false): AiCandidate =
        AiCandidate(
            optionId = optionId,
            capabilityId = "test_cheat",
            phase = AiPhase.ORDINARY_ACTION,
            cost = 0,
            risk = AiRisk(),
            estimatedEffect = AiEffect(handValue = effect),
            terminalForWindow = false,
            isSafeDefault = safe
        )

    @Test
    fun `legacy strategy wrapper keeps ordinary action semantics`() {
        val snap = snapshot(decision())
        val candidates = AiCandidateFactory.ordinary(snap)
        val chosen = LegacyAiStrategyAdapter(FirstActionStrategy()).choose(
            AiDecisionContext(snap, candidates)
        )
        assertEquals("opt-discard", chosen)
    }

    @Test
    fun `legacy wrapper refuses cheats but offer gets a safe default`() {
        val offerDecision = PlayerDecision("round", "offer-decision", 0L, emptyList())
        val offerSnapshot = snapshot(offerDecision, AiPhase.OFFER_CHOICE)
        val candidates = listOf(
            cheatCandidate("cheat:keep", 0, safe = true),
            cheatCandidate("cheat:swap", 100)
        )
        val chosen = LegacyAiStrategyAdapter(FirstActionStrategy(), allowCheats = false).choose(
            AiDecisionContext(offerSnapshot, candidates)
        )
        assertEquals("cheat:keep", chosen)
    }

    @Test
    fun `basic strategy only overrides ordinary action when cheat score is higher`() {
        val snap = snapshot(decision())
        val ordinary = AiCandidateFactory.ordinary(snap)
        val strong = BasicAiDecisionStrategy(FirstActionStrategy()).choose(
            AiDecisionContext(snap, ordinary + cheatCandidate("cheat:strong", 50))
        )
        assertEquals("cheat:strong", strong)

        val weak = BasicAiDecisionStrategy(BasicAIStrategy()).choose(
            AiDecisionContext(snap, ordinary + cheatCandidate("cheat:weak", 1))
        )
        // BasicAIStrategy declares riichi (effect 13) before discarding; weak cheat must not override it.
        assertEquals("opt-riichi", weak)
    }

    @Test
    fun `request id covers every stamp field and remains stable for the same decision`() {
        val base = AiDecisionStamp(
            seat = 1,
            sessionId = "session-a",
            roundId = "round-a",
            decisionId = "decision-a",
            controlEpoch = 1L,
            phase = AiPhase.ORDINARY_ACTION,
            stateVersion = 2L,
            offerId = "offer-a",
            windowId = "window-a",
            deadlineMillis = 1_000L
        )
        val capability = AiCapabilityIds.SLEEVE_BLOOM
        val option = "sleeve_bloom:offturn"
        val baseId = AiRequestId.of(capability, base, option)
        assertEquals(baseId, AiRequestId.of(capability, base.copy(), option))

        val variants = listOf(
            base.copy(seat = 2),
            base.copy(sessionId = "session-b"),
            base.copy(roundId = "round-b"),
            base.copy(decisionId = null),
            base.copy(controlEpoch = 2L),
            base.copy(phase = AiPhase.OFFER_CHOICE),
            base.copy(stateVersion = 3L),
            base.copy(offerId = "offer-b"),
            base.copy(windowId = "window-b"),
            base.copy(deadlineMillis = 2_000L)
        )
        for (variant in variants) {
            assertNotEquals(baseId, AiRequestId.of(capability, variant, option), "stamp variant must change request id: $variant")
        }
        assertNotEquals(baseId, AiRequestId.of("tile_mark", base, option))
        assertNotEquals(baseId, AiRequestId.of(capability, base, "sleeve_bloom:keep-first"))
    }

    @Test
    fun `stamp identity rejects cross round decision offer window epoch version and deadline reuse`() {
        val base = AiDecisionStamp(
            seat = 1,
            sessionId = "session",
            roundId = "round-a",
            decisionId = "decision",
            controlEpoch = 1L,
            phase = AiPhase.ORDINARY_ACTION,
            stateVersion = 2L,
            offerId = "offer",
            windowId = "window",
            deadlineMillis = 1_000L
        )
        val variants = listOf(
            base.copy(roundId = "round-b"),
            base.copy(decisionId = "other"),
            base.copy(controlEpoch = 2L),
            base.copy(phase = AiPhase.OFFER_CHOICE),
            base.copy(stateVersion = 3L),
            base.copy(offerId = "other"),
            base.copy(windowId = "other"),
            base.copy(deadlineMillis = 2_000L)
        )
        assertTrue(variants.all { it != base })
        assertEquals(base, base.copy())
    }

    @Test
    fun `registry rejects duplicate ids and never changes an existing id`() {
        assertThrows(IllegalArgumentException::class.java) {
            CheatAiRegistry(
                listOf(dummyCapability("dup"), dummyCapability("dup"))
            )
        }
        val authority = TableAuthority()
        try {
            assertEquals(
                listOf(
                    AiCapabilityIds.SHUFFLE_PREPARATION,
                    AiCapabilityIds.TILE_MARK,
                    AiCapabilityIds.SLEEVE_BLOOM,
                    AiCapabilityIds.BEAM_SWAP
                ),
                CheatAiRegistry.builtIn(authority).ids
            )
        } finally {
            authority.close()
        }
    }

    @Test
    fun `registry truncates and filters malformed candidates`() {
        val capability = object : CheatAiCapability {
            override val id: String = "flood"
            override val version: Int = 1
            override fun describe(): AiCapabilityDescriptor = AiCapabilityDescriptor(
                id = id,
                version = version,
                phases = setOf(AiPhase.ORDINARY_ACTION),
                supportsSubmit = true,
                maxCandidates = 3
            )

            override fun candidates(view: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate> =
                (1..10).map { index ->
                    AiCandidate(
                        optionId = "flood:$index",
                        capabilityId = if (index == 1) "wrong" else id,
                        phase = AiPhase.ORDINARY_ACTION,
                        cost = 0,
                        risk = AiRisk(),
                        estimatedEffect = AiEffect(),
                        terminalForWindow = false
                    )
                }

            override fun submit(
                seat: Int,
                selected: AiCandidate,
                stamp: AiDecisionStamp,
                now: Long
            ): TableAuthority.MutationResult = error("not used")
        }
        val snap = snapshot(decision())
        val registry = CheatAiRegistry(listOf(capability))
        val result = registry.candidates(snap, snap.limits)
        assertTrue(result.size <= 3)
        assertTrue(result.all { it.capabilityId == "flood" })
        assertEquals(result.map { it.optionId }.toSet().size, result.size)
    }

    private fun dummyCapability(id: String): CheatAiCapability = object : CheatAiCapability {
        override val id: String = id
        override val version: Int = 1
        override fun describe(): AiCapabilityDescriptor = AiCapabilityDescriptor(
            id = id,
            version = version,
            phases = setOf(AiPhase.ORDINARY_ACTION),
            supportsSubmit = true,
            maxCandidates = 2
        )

        override fun candidates(view: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate> =
            emptyList()

        override fun submit(
            seat: Int,
            selected: AiCandidate,
            stamp: AiDecisionStamp,
            now: Long
        ): TableAuthority.MutationResult = error("not used")
    }
}
