package com.majiang.state

import com.majiang.access.ActionReceipt
import com.majiang.access.ReceiptStatus
import com.majiang.ai.AIStrategy
import com.majiang.authority.TableAuthority
import com.majiang.event.GameEvent
import com.majiang.model.Meld
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import com.majiang.timing.ReserveScope
import com.majiang.timing.TimeControlConfig
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.security.MessageDigest
import java.util.Locale

/**
 * P0.1-b / P1.6 AI V4 replay exporter.
 *
 * Test-only. This class runs the real V4 authority entry point
 * [TableAuthority.submitAiChoice] over a deterministic table fixture, records the actual
 * roundId/decisionId/controlEpoch/deadline/optionId/action and the actual before/after
 * state and event summaries, and writes tests/v5-golden/ai-v1.json.
 *
 * It is inert during ordinary test runs. Enable only with V5_AI_GOLDEN_EXPORT=1.
 *
 * Honest limits recorded in the fixture:
 * - V4 has one combined rejection reason STALE_AI_SUBMISSION; V5 splits it into StaleRound,
 *   StaleDecision, StaleEpoch, IllegalAction and DeadlineExpired. The fixture records both the
 *   actual V4 reason and an explicit per-case semantic mapping; it never claims the codes are
 *   the same.
 * - V4 sessionId/roundId are generated with UUID.randomUUID(); byte-identical regeneration is
 *   not claimed. Random identities are recorded as actual values, while semantic fields are
 *   stable across regenerations.
 * - V4 PlayerView has no public "claimed discard" flag, so C# AiVisibleDiscard.isClaimed is
 *   recorded false with that limitation stated.
 * - The C# CommandIntent canonical byte encoding is a V5 implementation detail. No V4-expected
 *   hash is recorded and no cross-language byte/hash equality is claimed.
 */
class V5AiGoldenExporterTest {

    private companion object {
        /**
         * The reviewed V4/PR19 source baseline this extraction is bound to. This is deliberately
         * a constant: re-running the exporter from a later local task commit must not silently
         * relabel the V4 source baseline as that task commit.
         */
        const val V4_BASELINE_COMMIT = "205506f07f6a1ccfef6b32cf97390431e3349f8d"
    }

    private class Clock(var now: Long = 1_000L) : MonotonicClock {
        override fun nowMillis(): Long = now
    }

    /**
     * Deterministic strategy over exactly the V4 AI surface: the viewer's own PlayerView and
     * the legal actions for that seat. It never receives GameState, TableAuthority, another
     * seat's closed hand, the wall order or a random source.
     */
    private class DeterministicDiscardStrategy : AIStrategy {
        override val name: String = "v5-ai-golden-deterministic"

        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
            require(legalActions.all { actionPlayer(it) == view.viewerSeat }) {
                "legalActions must belong to the viewing seat ${view.viewerSeat}"
            }
            val discards = legalActions.filterIsInstance<GameAction.DiscardTile>()
                .filter { it.player == view.viewerSeat }
            return discards.minWithOrNull(compareBy { it.tile.tileId })
                ?: legalActions.firstOrNull()
        }
    }

    @Test
    fun exportAiGoldenSamples() {
        if (System.getenv("V5_AI_GOLDEN_EXPORT") != "1") {
            return
        }

        val clock = Clock(1_000L)
        val timeControl = TimeControlConfig(
            baseMillis = 3_000L,
            reserveMillis = 20_000L,
            reserveScope = ReserveScope.ROUND
        )
        val hand = listOf(
            Tile(Suit.MAN, 1, 0), Tile(Suit.MAN, 2, 0), Tile(Suit.MAN, 3, 0),
            Tile(Suit.MAN, 4, 0), Tile(Suit.MAN, 5, 0), Tile(Suit.MAN, 6, 0),
            Tile(Suit.MAN, 7, 0), Tile(Suit.MAN, 8, 0), Tile(Suit.MAN, 9, 0),
            Tile(Suit.PIN, 1, 0), Tile(Suit.PIN, 2, 0),
            Tile(Suit.SOU, 1, 0), Tile(Suit.SOU, 2, 0),
            Tile(Suit.WIND, 1, 0)
        )
        val state = TestTableBuilder.create(
            hands = mapOf(1 to hand),
            phase = GamePhase.DISCARD,
            currentPlayer = 1,
            dealer = 1,
            wallPrefix = listOf(Tile(Suit.SOU, 9, 0), Tile(Suit.WIND, 2, 0))
        )
        val authority = TableAuthority(
            timeControl = timeControl,
            clock = clock,
            controllers = listOf(
                SeatController.RemoteHuman(false),
                SeatController.Computer(),
                SeatController.RemoteHuman(false),
                SeatController.RemoteHuman(false)
            ),
            initialState = state
        )

        try {
            val now = clock.nowMillis()
            val snapshot = authority.buildAiSeatSnapshot(1, now)
            val view = snapshot.view
            val decision = snapshot.decision
            val timing = authority.decisionClock.timing(1)
            assertNotNull(timing, "V4 authority must open the active seat decision timing")
            val deadline = timing!!.deadlineMillis
            val opensAt = timing.opensAtMillis
            assertEquals(now + 3_000L + 20_000L, deadline)
            val roundId = decision.roundId!!
            assertEquals(roundId, snapshot.stamp.roundId)
            assertEquals(decision.decisionId, snapshot.stamp.decisionId)
            assertEquals(decision.controlEpoch, snapshot.stamp.controlEpoch)
            assertEquals(deadline, snapshot.stamp.deadlineMillis!!)
            assertEquals(view.legalActions, decision.options.map { it.action })
            assertTrue(view.legalActions.isNotEmpty(), "fixture must expose at least one legal action")

            val strategy = DeterministicDiscardStrategy()
            val selected = strategy.decideAction(view, view.legalActions)
            assertNotNull(selected, "deterministic strategy must select an action")
            assertTrue(selected!! in view.legalActions)
            val selectedOption = decision.options.first { it.action == selected }
            val selectedIndex = decision.options.indexOfFirst { it.optionId == selectedOption.optionId }
            assertTrue(selectedIndex >= 0, "selected option must exist in the frozen decision")
            val selectedRef = "L$selectedIndex"
            val allLegalRefs = decision.options.indices.map { "L$it" }
            val observation = observationJson(authority, snapshot, view, decision, deadline, selectedOption.optionId, selectedRef)
            val before = stateSummary(authority)

            val staleRoundId = "stale-round-$roundId"
            val staleDecisionId = "stale-decision-${decision.decisionId}"

            val rejections = mutableListOf<J>()
            val oldRound = authority.submitAiChoice(
                seat = 1,
                roundId = staleRoundId,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                action = selected,
                now = now
            )
            rejections.add(
                caseJson(
                    id = "AI-002-old-round",
                    kind = "stale-round",
                    submittedSeat = 1,
                    submittedRoundId = staleRoundId,
                    submittedDecisionId = decision.decisionId,
                    submittedEpoch = decision.controlEpoch,
                    submittedAction = selected,
                    submittedOptionId = selectedOption.optionId,
                    submittedActionRef = selectedRef,
                    intentActionRef = selectedRef,
                    allLegalRefs = allLegalRefs,
                    now = now,
                    deadline = deadline,
                    receipt = oldRound,
                    before = before,
                    after = stateSummary(authority),
                    expected = expected(
                        accepted = false,
                        code = "StaleRound",
                        basis = "V4 compares submitted roundId with the current decision first and collapses every mismatch into STALE_AI_SUBMISSION; V5 reports the round mismatch as StaleRound."
                    ),
                    note = "Only the submitted roundId differs from the live V4 decision."
                )
            )
            assertRejected(oldRound, "AI-002-old-round")

            val oldDecision = authority.submitAiChoice(
                seat = 1,
                roundId = roundId,
                decisionId = staleDecisionId,
                controlEpoch = decision.controlEpoch,
                action = selected,
                now = now
            )
            rejections.add(
                caseJson(
                    id = "AI-003-old-decision",
                    kind = "stale-decision",
                    submittedSeat = 1,
                    submittedRoundId = roundId,
                    submittedDecisionId = staleDecisionId,
                    submittedEpoch = decision.controlEpoch,
                    submittedAction = selected,
                    submittedOptionId = selectedOption.optionId,
                    submittedActionRef = selectedRef,
                    intentActionRef = selectedRef,
                    allLegalRefs = allLegalRefs,
                    now = now,
                    deadline = deadline,
                    receipt = oldDecision,
                    before = before,
                    after = stateSummary(authority),
                    expected = expected(
                        accepted = false,
                        code = "StaleDecision",
                        basis = "V4 checks decisionId/timing-key equality and collapses the mismatch into STALE_AI_SUBMISSION; V5 reports a superseded decision as StaleDecision."
                    ),
                    note = "Only the submitted decisionId differs from the live V4 decision."
                )
            )
            assertRejected(oldDecision, "AI-003-old-decision")

            val previousEpoch = decision.controlEpoch - 1L
            require(previousEpoch >= 0L) { "fixture requires a non-negative true older control epoch" }
            val oldEpoch = authority.submitAiChoice(
                seat = 1,
                roundId = roundId,
                decisionId = decision.decisionId,
                controlEpoch = previousEpoch,
                action = selected,
                now = now
            )
            rejections.add(
                caseJson(
                    id = "AI-004-old-epoch",
                    kind = "stale-epoch",
                    submittedSeat = 1,
                    submittedRoundId = roundId,
                    submittedDecisionId = decision.decisionId,
                    submittedEpoch = previousEpoch,
                    submittedAction = selected,
                    submittedOptionId = selectedOption.optionId,
                    submittedActionRef = selectedRef,
                    intentActionRef = selectedRef,
                    allLegalRefs = allLegalRefs,
                    now = now,
                    deadline = deadline,
                    receipt = oldEpoch,
                    before = before,
                    after = stateSummary(authority),
                    expected = expected(
                        accepted = false,
                        code = "StaleEpoch",
                        basis = "V4 compares controlEpoch with the current decision and collapses the mismatch into STALE_AI_SUBMISSION; V5 reports it as StaleEpoch."
                    ),
                    note = "Submitted true older controlEpoch $previousEpoch, below the live current epoch ${decision.controlEpoch}; this is not a future-epoch mismatch."
                )
            )
            assertRejected(oldEpoch, "AI-004-old-epoch")

            val wrongSeat = authority.submitAiChoice(
                seat = 0,
                roundId = roundId,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                action = selected,
                now = now
            )
            rejections.add(
                caseJson(
                    id = "AI-005-wrong-seat",
                    kind = "non-own-seat",
                    submittedSeat = 0,
                    submittedRoundId = roundId,
                    submittedDecisionId = decision.decisionId,
                    submittedEpoch = decision.controlEpoch,
                    submittedAction = selected,
                    submittedOptionId = selectedOption.optionId,
                    submittedActionRef = selectedRef,
                    intentActionRef = selectedRef,
                    allLegalRefs = allLegalRefs,
                    now = now,
                    deadline = deadline,
                    receipt = wrongSeat,
                    before = before,
                    after = stateSummary(authority),
                    expected = expected(
                        accepted = false,
                        code = "StaleDecision",
                        basis = "V4 resolves the decision for the submitted seat and rejects when the seat/decision identity does not match, using STALE_AI_SUBMISSION; V5 reports a seat/decision identity mismatch as StaleDecision."
                    ),
                    note = "The submitted seat is 0 while the frozen AI observation belongs to seat 1; V5 authority captures seat 0."
                )
            )
            assertRejected(wrongSeat, "AI-005-wrong-seat")

            val illegal = authority.submitAiChoice(
                seat = 1,
                roundId = roundId,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                action = GameAction.Pass(1),
                now = now
            )
            rejections.add(
                caseJson(
                    id = "AI-006-candidate-not-legal",
                    kind = "illegal-candidate",
                    submittedSeat = 1,
                    submittedRoundId = roundId,
                    submittedDecisionId = decision.decisionId,
                    submittedEpoch = decision.controlEpoch,
                    submittedAction = GameAction.Pass(1),
                    submittedOptionId = null,
                    submittedActionRef = "outside-options",
                    intentActionRef = selectedRef,
                    allLegalRefs = allLegalRefs,
                    now = now,
                    deadline = deadline,
                    receipt = illegal,
                    before = before,
                    after = stateSummary(authority),
                    expected = expected(
                        accepted = false,
                        code = "IllegalAction",
                        basis = "V4 rejects an action absent from the live option list with the same STALE_AI_SUBMISSION reason; V5 separates the authority-side candidate check as IllegalAction. The C# test keeps the recorded action legal in its frozen observation but removes it from the fresh authority candidate list, so the same authority-side check is exercised."
                    ),
                    note = "V4 submitted GameAction.Pass(1), which is outside the DISCARD decision options. C# cannot construct a CommandIntent outside its observation, so its authority snapshot narrows the candidate list instead."
                )
            )
            assertRejected(illegal, "AI-006-candidate-not-legal")

            val expiredBefore = authority.decisionClock.expired(deadline - 1L).any { it.key.seat == 1 }
            val expiredAt = authority.decisionClock.expired(deadline).any { it.key.seat == 1 }
            assertFalse(expiredBefore, "deadline-1 must not be expired")
            assertTrue(expiredAt, "exact deadline must be expired")

            val exactDeadline = authority.submitAiChoice(
                seat = 1,
                roundId = roundId,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                action = selected,
                now = deadline
            )
            rejections.add(
                caseJson(
                    id = "AI-007-exact-deadline",
                    kind = "exact-deadline",
                    submittedSeat = 1,
                    submittedRoundId = roundId,
                    submittedDecisionId = decision.decisionId,
                    submittedEpoch = decision.controlEpoch,
                    submittedAction = selected,
                    submittedOptionId = selectedOption.optionId,
                    submittedActionRef = selectedRef,
                    intentActionRef = selectedRef,
                    allLegalRefs = allLegalRefs,
                    now = deadline,
                    deadline = deadline,
                    receipt = exactDeadline,
                    before = before,
                    after = stateSummary(authority),
                    expected = expected(
                        accepted = false,
                        code = "DeadlineExpired",
                        basis = "V4 requires now < deadline and rejects at the exact deadline with STALE_AI_SUBMISSION; V5 reports DeadlineExpired at or after the deadline."
                    ),
                    note = "now == deadline. DecisionClock.expired(deadline-1)=false and expired(deadline)=true were recorded before this call."
                )
            )
            assertRejected(exactDeadline, "AI-007-exact-deadline")

            val acceptedAt = deadline - 1L
            val accepted = authority.submitAiChoice(
                seat = 1,
                roundId = roundId,
                decisionId = decision.decisionId,
                controlEpoch = decision.controlEpoch,
                action = selected,
                now = acceptedAt
            )
            assertEquals(ReceiptStatus.APPLIED, accepted.receipt.status, accepted.receipt.toString())
            assertNull(accepted.receipt.reason)
            assertTrue(accepted.stateChanged, "accepted AI action must change state")
            assertTrue(accepted.events.isNotEmpty(), "accepted AI action must emit events")
            val after = stateSummary(authority)
            assertNotEquals(before.render(), after.render(), "accepted action must change the state summary")
            TestTableBuilder.assertPhysical(authority.coordinator.activeRound!!.readState())

            val cases = ArrayList<J>(rejections)
            cases.add(
                caseJson(
                    id = "AI-001-legal-intent-before-deadline",
                    kind = "legal-intent",
                    submittedSeat = 1,
                    submittedRoundId = roundId,
                    submittedDecisionId = decision.decisionId,
                    submittedEpoch = decision.controlEpoch,
                    submittedAction = selected,
                    submittedOptionId = selectedOption.optionId,
                    submittedActionRef = selectedRef,
                    intentActionRef = selectedRef,
                    allLegalRefs = allLegalRefs,
                    now = acceptedAt,
                    deadline = deadline,
                    receipt = accepted,
                    before = before,
                    after = after,
                    expected = expected(
                        accepted = true,
                        code = "None",
                        basis = "V4 returned APPLIED at deadline-1; the stricter boundary (now >= deadline) is rejected, so the accepted boundary is exactly V5 Accept."
                    ),
                    note = "Accepted at deadline-1 ms. This is the only case that mutates the table; all rejection cases ran first against the same live decision identity."
                )
            )

            val dirtyPaths = trackedDirtyPaths()
            val testOnlyDirty = dirtyPaths.filter { isTestOnlyDirtyPath(porcelainPath(it)) }
            val productionDirty = dirtyPaths.filterNot { isTestOnlyDirtyPath(porcelainPath(it)) }

            val root = aobj(
                "schema" to aj("v5-ai-golden-v1"),
                "generator" to aj("engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt"),
                "generatorVersion" to aj(1),
                "rulesetVersion" to aj(1),
                "v4Commit" to aj(V4_BASELINE_COMMIT),
                "generatedAtHead" to aj(currentHead()),
                "v4TrackedWorkingTreeDirty" to aj(dirtyPaths.isNotEmpty()),
                "v4TrackedDirtyPaths" to aarr(dirtyPaths.map { aj(it) }),
                "v4ProductionSourceDirty" to aj(productionDirty.isNotEmpty()),
                "v4ProductionSourceDirtyPaths" to aarr(productionDirty.map { aj(porcelainPath(it)) }),
                "v4TestOnlyTrackedDirtyPaths" to aarr(testOnlyDirty.map { aj(porcelainPath(it)) }),
                "v4EngineSourceCommit" to aj(lastCommitFor("engine/src/main/kotlin")),
                "v4DotnetAiSourceCommit" to aj(lastCommitFor("dotnet/src/Mahjong.Ai")),
                "v4DotnetDomainSourceCommit" to aj(lastCommitFor("dotnet/src/Mahjong.Domain")),
                "testOnlyChanges" to aj(
                    "The exporter is a test-only Kotlin file and ai-v1.json is a generated test fixture. " +
                        "No V4 production source, Domain/Ai C# production code, manifest, catalog, CI, Unity or solution file is changed."
                ),
                "sourceFileSha256" to aarr(
                    listOf(
                        "engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt",
                        "engine/src/main/kotlin/com/majiang/ai/AiDecisionModels.kt",
                        "engine/src/main/kotlin/com/majiang/ai/AIStrategy.kt",
                        "engine/src/main/kotlin/com/majiang/session/PlayerView.kt",
                        "engine/src/main/kotlin/com/majiang/timing/DecisionClock.kt",
                        "engine/src/test/kotlin/com/majiang/state/UnifiedActionAcceptanceTest.kt",
                        "engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt",
                        "dotnet/src/Mahjong.Ai/AiDecision.cs",
                        "dotnet/src/Mahjong.Ai/AiDecisionService.cs",
                        "dotnet/src/Mahjong.Ai/AiObservation.cs",
                        "dotnet/src/Mahjong.Ai/CommandIntent.cs",
                        "dotnet/src/Mahjong.Domain/DeadlinePolicy.cs",
                        "dotnet/src/Mahjong.Domain/DecisionClock.cs"
                    ).map { path ->
                        aobj(
                            "path" to aj(path),
                            "sha256" to aj(sha256File(repoRoot().resolve(path))),
                            "lastCommit" to aj(lastCommitFor(path))
                        )
                    }
                ),
                "generatedByCommand" to aj(
                    "\$env:V5_AI_GOLDEN_EXPORT='1'; .\\gradlew.bat :engine:test --tests \"com.majiang.state.V5AiGoldenExporterTest\" --console=plain"
                ),
                "extractionStatus" to aj("exported"),
                "v4BehaviorSource" to aj(
                    "Actual calls to TableAuthority.submitAiChoice and DecisionClock.expired in this test; no expected V4 value is hand-written."
                ),
                "reproducibility" to aobj(
                    "stableAcrossRegeneration" to aarr(
                        listOf(
                            "schema/generator/generatorVersion/rulesetVersion",
                            "case ids, kinds, option ids and semantic mapping",
                            "V4 receipt status/reason/stateChanged and event type/text",
                            "V4 before/after state summary",
                            "decisionClock opensAt/deadline arithmetic",
                            "mapped PlayerView observation fields"
                        ).map { aj(it) }
                    ),
                    "randomOrRunSpecific" to aarr(
                        listOf(
                            "sessionId (UUID generated by MatchSession)",
                            "roundId (UUID generated by RoundStateMachine)",
                            "decisionId (derived from the random roundId and round version)",
                            "stale-* prefixes embed the random decision identity"
                        ).map { aj(it) }
                    ),
                    "note" to aj(
                        "Byte-identical file regeneration is not claimed. Re-running the exporter changes the random identities and therefore the file SHA256; semantic fields and V4 outcomes must stay stable."
                    )
                ),
                "decisionClock" to aobj(
                    "baseMillis" to aj(3_000L),
                    "reserveMillis" to aj(20_000L),
                    "reserveScope" to aj("ROUND"),
                    "opensAtMillis" to aj(opensAt),
                    "deadlineMillis" to aj(deadline),
                    "expiredAtDeadlineMinus1" to aj(expiredBefore),
                    "expiredAtDeadline" to aj(expiredAt)
                ),
                "recordedIntent" to aobj(
                    "algorithmId" to aj("v4-golden-deterministic"),
                    "algorithmVersion" to aj(1),
                    "selectedLegalActionRef" to aj(selectedRef),
                    "selectedOptionId" to aj(selectedOption.optionId),
                    "selectedAction" to actionJson(selected, selectedOption.optionId)
                ),
                "v5Observation" to observation,
                "cases" to aarr(cases),
                "unexportedOrMappedSamples" to aarr(
                    listOf(
                        "V4 AiDecisionRecorded event: V4 has no such event type; the recorded CommandIntent fields in this fixture are the replay input, not a fabricated event.",
                        "AI cheat capability intent (tile mark/sleeve bloom/beam swap): not exported here; this fixture covers the ordinary turn decision and P1.6 authority stamps only.",
                        "claim flags on visible discards: V4 PlayerView does not expose them; C# records isRiichi/isClaimed from the public view where available and false otherwise.",
                        "cross-language canonical hash equality: NOT claimed; only same-language C# replay stability is checked by the C# test.",
                        "full 136-placement V4 state digest: this fixture records a bounded state summary, not a V4 hash.",
                        "server.TableHost/AI host integration: NOT wired or tested by this task."
                    ).map { aj(it) }
                )
            )

            val path = repoRoot().resolve("tests/v5-golden/ai-v1.json")
            Files.createDirectories(path.parent)
            Files.write(path, root.render().toByteArray(StandardCharsets.UTF_8))
            assertTrue(Files.size(path) > 8_000L, "ai-v1.json is unexpectedly small: ${Files.size(path)}")
            assertTrue(Files.exists(path))
        } finally {
            authority.close()
        }
    }

    // ------------------------------------------------------------------
    // Fixture mapping helpers
    // ------------------------------------------------------------------

    private fun observationJson(
        authority: TableAuthority,
        snapshot: com.majiang.ai.AiSeatSnapshot,
        view: PlayerView,
        decision: com.majiang.application.PlayerDecision,
        deadline: Long,
        selectedOptionId: String,
        selectedRef: String
    ): J {
        val legal = decision.options.mapIndexed { index, option ->
            aobj(
                "ref" to aj("L$index"),
                "kind" to aj(legalKind(option.action)),
                "seat" to aj(option.action.player()),
                "tileId" to nullableInt(actionTileId(option.action)),
                "optionId" to aj(option.optionId)
            )
        }
        val mappedSelected = decision.options.mapIndexed { index, option -> "L$index" to option }
            .first { it.second.optionId == selectedOptionId }
        assertEquals(selectedRef, mappedSelected.first, "selected action ref must be stable")

        return aobj(
            "sessionId" to aj(authority.sessionId),
            "seat" to aj(1),
            "roundId" to aj(decision.roundId!!),
            "decisionId" to aj(decision.decisionId),
            "controlEpoch" to aj(decision.controlEpoch),
            "stateVersion" to aj(snapshot.stamp.stateVersion),
            "deadlineMillis" to aj(deadline),
            "phase" to aj(roundPhase(view.phase)),
            "dealerSeat" to aj(view.dealerSeat),
            "roundWind" to aj(view.roundWind),
            "roundNumber" to aj(view.roundNumber),
            "honbaSticks" to aj(view.honbaSticks),
            "riichiSticks" to aj(view.riichiSticks),
            "remainingWallCount" to aj(view.wallRemaining),
            "myHandTileIds" to aarr(view.myClosedTiles.map { aj(it.tileId) }),
            "myDrawnTileId" to nullableInt(view.myDrawnTile?.tileId),
            "myMelds" to aarr(view.melds[1].map { meldJson(1, it) }),
            "visibleMelds" to aarr(
                (0..3).flatMap { seat -> view.melds[seat].map { meldJson(seat, it) } }
            ),
            "visibleDiscards" to aarr(
                (0..3).flatMap { seat ->
                    view.discards[seat].map { tile ->
                        aobj(
                            "seat" to aj(seat),
                            "tileId" to aj(tile.tileId),
                            "isRiichi" to aj(false),
                            "isClaimed" to aj(false)
                        )
                    }
                }
            ),
            "visibleDoraIndicators" to aarr(view.doraIndicators.map { aj(it.tileId) }),
            "scores" to aarr(view.scores.map { aj(it.toLong()) }),
            "legalActions" to aarr(legal),
            "mappingLimits" to aarr(
                listOf(
                    "PlayerView has no public claimed-discard flag; isClaimed is false.",
                    "riichi status is available as a per-seat flag but is not attached to individual discards by PlayerView; per-discard isRiichi is false.",
                    "sleeve tiles are private and are not part of AiObservation; they are intentionally absent."
                ).map { aj(it) }
            )
        )
    }

    private fun meldJson(seat: Int, meld: Meld): J = aobj(
        "seat" to aj(seat),
        "kind" to aj(meld.type.name),
        "tiles" to aarr(meld.tiles.map { aj(it.tileId) }),
        "fromPlayer" to aj(meld.fromPlayer),
        "calledTileId" to aj(meld.calledTile.tileId)
    )

    private fun caseJson(
        id: String,
        kind: String,
        submittedSeat: Int,
        submittedRoundId: String,
        submittedDecisionId: String,
        submittedEpoch: Long,
        submittedAction: GameAction,
        submittedOptionId: String?,
        submittedActionRef: String,
        intentActionRef: String,
        allLegalRefs: List<String>,
        now: Long,
        deadline: Long,
        receipt: TableAuthority.MutationResult,
        before: J,
        after: J,
        expected: Expected,
        note: String
    ): J {
        val legalRefs = when (kind) {
            "non-own-seat" -> emptyList()
            "illegal-candidate" -> allLegalRefs.filter { it != intentActionRef }
            else -> allLegalRefs
        }
        val authorityMode = when (kind) {
            "stale-round" -> "stale-round"
            "stale-decision" -> "stale-decision"
            "stale-epoch" -> "stale-epoch"
            "non-own-seat" -> "foreign-seat"
            "illegal-candidate" -> "candidate-exclusion"
            "legal-intent", "exact-deadline" -> "current"
            else -> error("unknown case kind for authority mapping: $kind")
        }
        return aobj(
            "id" to aj(id),
            "kind" to aj(kind),
            "note" to aj(note),
            "v4Call" to aobj(
                "entryPoint" to aj("TableAuthority.submitAiChoice"),
                "seat" to aj(submittedSeat),
                "roundId" to aj(submittedRoundId),
                "decisionId" to aj(submittedDecisionId),
                "controlEpoch" to aj(submittedEpoch),
                "optionId" to ajOrNull(submittedOptionId),
                "submittedActionRef" to aj(submittedActionRef),
                "action" to actionJson(submittedAction, submittedOptionId),
                "nowMillis" to aj(now),
                "deadlineMillis" to aj(deadline),
                "nowBeforeDeadline" to aj(now < deadline)
            ),
            "v4" to aobj(
                "receiptStatus" to aj(receipt.receipt.status.name),
                "reason" to ajOrNull(receipt.receipt.reason),
                "stateChanged" to aj(receipt.stateChanged),
                "eventCount" to aj(receipt.events.size),
                "events" to aarr(receipt.events.map { eventJson(it) }),
                "beforeState" to before,
                "afterState" to after
            ),
            "v5" to aobj(
                "intentActionRef" to aj(intentActionRef),
                "submittedAtMillis" to aj(now),
                "authority" to authorityJson(
                    submittedSeat,
                    submittedRoundId,
                    submittedDecisionId,
                    submittedEpoch,
                    deadline,
                    legalRefs
                ),
                "authorityMapping" to aobj(
                    "mode" to aj(authorityMode),
                    "derivedFrom" to aarr(listOf(aj("v5Observation"), aj("v4Call"))),
                    "isFreshV4Authority" to aj(false),
                    "basis" to aj(authorityMappingBasis(authorityMode))
                ),
                "expected" to aobj(
                    "accepted" to aj(expected.accepted),
                    "rejectionCode" to aj(expected.code),
                    "mappingBasis" to aj(expected.basis)
                )
            )
        )
    }

    private fun authorityMappingBasis(mode: String): String = when (mode) {
        "current" -> "seat/round/currentDecisionId/controlEpoch/deadline and the full legal list come from the frozen observation. This is a relation mapping used to admit the C# replay, not a second V4 authority capture."
        "stale-round" -> "roundId is replaced by v4Call.roundId; currentDecisionId/epoch/deadline/legal list stay from the frozen observation. This is a relation mapping, not a fresh V4 authority."
        "stale-decision" -> "currentDecisionId is replaced by v4Call.decisionId; round/epoch/deadline/legal list stay from the frozen observation. This is a relation mapping, not a fresh V4 authority."
        "stale-epoch" -> "controlEpoch is replaced by the true older v4Call.controlEpoch; round/decision/deadline/legal list stay from the frozen observation. This is a relation mapping, not a fresh V4 authority."
        "foreign-seat" -> "seat/currentDecisionId/epoch come from v4Call; the legal list is deliberately empty because a fresh seat-0 V4 authority has no candidate for this decision. This is a relation mapping, not a captured seat-0 authority."
        "candidate-exclusion" -> "seat/round/decision/epoch/deadline come from the frozen observation; the legal list excludes the recorded selected action to exercise the authority-side candidate check. V4 submitted an outside-options Pass, so this construction is explicitly different and is not a fresh V4 authority capture."
        else -> error("unknown authority mapping mode: $mode")
    }

    private fun authorityJson(
        seat: Int,
        roundId: String,
        decisionId: String,
        epoch: Long,
        deadline: Long,
        legalRefs: List<String>
    ): J = aobj(
        "seat" to aj(seat),
        "roundId" to aj(roundId),
        "currentDecisionId" to aj(decisionId),
        "controlEpoch" to aj(epoch),
        "seatAutomated" to aj(true),
        "clockPaused" to aj(false),
        "deadlineMillis" to aj(deadline),
        "legalActionRefs" to aarr(legalRefs.map { aj(it) })
    )

    private data class Expected(val accepted: Boolean, val code: String, val basis: String)

    private fun expected(accepted: Boolean, code: String, basis: String): Expected =
        Expected(accepted, code, basis)

    private fun assertRejected(result: TableAuthority.MutationResult, caseId: String) {
        assertEquals(ReceiptStatus.REJECTED, result.receipt.status, "$caseId: $result")
        assertEquals("STALE_AI_SUBMISSION", result.receipt.reason, "$caseId: $result")
        assertFalse(result.stateChanged, "$caseId must not change state")
        assertTrue(result.events.isEmpty(), "$caseId must not emit events")
    }

    private fun stateSummary(authority: TableAuthority): J {
        val round = authority.coordinator.activeRound
        assertNotNull(round, "an active round is required")
        val state = round!!.readState()
        return aobj(
            "roundId" to aj(round.roundId),
            "roundVersion" to aj(round.version),
            "phase" to aj(state.phase.name),
            "currentPlayer" to aj(state.currentPlayer),
            "dealerSeat" to aj(state.dealerIndex),
            "turnNumber" to aj(state.turnNumber),
            "wallPointer" to aj(state.wallPointer),
            "wallEndExclusive" to aj(state.wallEndExclusive),
            "wallRemaining" to aj(state.wallRemaining),
            "riichiSticks" to aj(state.riichiSticks),
            "honbaSticks" to aj(state.honbaSticks),
            "scores" to aarr(state.scores.map { aj(it.toLong()) }),
            "cheatPoints" to aarr(state.cheatPoints.map { aj(it) }),
            "lastDiscardTileId" to nullableInt(state.lastDiscard?.tileId),
            "lastDiscardPlayer" to aj(state.lastDiscardPlayer),
            "seats" to aarr(
                (0..3).map { seat ->
                    val player = state.players[seat]
                    aobj(
                        "seat" to aj(seat),
                        "closedCount" to aj(player.closedTiles.size),
                        "drawnTileId" to nullableInt(player.drawnTile?.tileId),
                        "meldCount" to aj(player.melds.size),
                        "discardCount" to aj(player.discards.size),
                        "sleeveCount" to aj(state.sleeveTiles[seat].size)
                    )
                }
            )
        )
    }

    private fun eventJson(event: GameEvent): J {
        if (event is GameEvent.TileDiscarded) {
            return aobj(
                "type" to aj("TileDiscarded"),
                "kind" to aj("Discard"),
                "player" to aj(event.player),
                "tileId" to aj(event.tile.tileId),
                "structured" to aj(true),
                "text" to aj(event.toString())
            )
        }
        return aobj(
            "type" to aj(event.javaClass.simpleName),
            "kind" to ajOrNull(null),
            "player" to ajOrNull(null),
            "tileId" to nullableInt(null),
            "structured" to aj(false),
            "text" to aj(event.toString())
        )
    }

    private fun actionJson(action: GameAction, optionId: String?): J = aobj(
        "type" to aj(action.javaClass.simpleName),
        "kind" to aj(legalKind(action)),
        "player" to aj(action.player()),
        "tileId" to nullableInt(actionTileId(action)),
        "optionId" to ajOrNull(optionId),
        "text" to aj(action.toString())
    )

    private fun legalKind(action: GameAction): String = when (action) {
        is GameAction.DiscardTile -> "Discard"
        is GameAction.Pass -> "Pass"
        is GameAction.DeclareTsumo -> "Tsumo"
        is GameAction.DeclareRon -> "Ron"
        is GameAction.DeclareChi -> "Chi"
        is GameAction.DeclarePon -> "Pon"
        is GameAction.DeclareKan -> "Kan"
        is GameAction.DeclareRiichi -> "Riichi"
        else -> "Other"
    }

    private fun actionTileId(action: GameAction): Int? = when (action) {
        is GameAction.DiscardTile -> action.tile.tileId
        is GameAction.DeclareRiichi -> action.discardTile.tileId
        is GameAction.DeclareKan -> action.tiles.firstOrNull()?.tileId
        is GameAction.DeclarePon -> action.tiles.firstOrNull()?.tileId
        is GameAction.DeclareChi -> action.meld.calledTile.tileId
        else -> null
    }

    private fun roundPhase(phase: GamePhase): String = when (phase) {
        GamePhase.STACKING -> "Stacking"
        GamePhase.DEALING -> "Dealing"
        GamePhase.DRAW -> "Draw"
        GamePhase.CHEAT_WINDOW -> "CheatWindow"
        GamePhase.DISCARD -> "Discard"
        GamePhase.RESPONSE_WINDOW -> "ResponseWindow"
        GamePhase.CHANKAN_WINDOW -> "ChankanWindow"
        GamePhase.KAN_DRAW -> "KanDraw"
        GamePhase.ROUND_END -> "RoundEnd"
        GamePhase.MATCH_END -> error("MATCH_END has no V5 RoundPhase")
    }

    // ------------------------------------------------------------------
    // Environment / provenance helpers
    // ------------------------------------------------------------------

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

    private fun git(vararg args: String): String {
        return try {
            val process = ProcessBuilder(listOf("git", "-C", repoRoot().toString()) + args)
                .redirectErrorStream(true)
                .start()
            val text = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }.trim()
            if (process.waitFor() == 0) text else "unknown"
        } catch (e: Exception) {
            "unknown"
        }
    }

    private fun currentHead(): String = git("rev-parse", "HEAD")

    private fun lastCommitFor(relativePath: String): String =
        git("log", "-1", "--format=%H", "--", relativePath).ifEmpty { "unknown" }

    private fun trackedDirtyPaths(): List<String> {
        val text = gitRaw("status", "--porcelain", "--untracked-files=no")
        if (text == "unknown" || text.isBlank()) return emptyList()
        return text.lineSequence().map { it.trimEnd() }.filter { it.isNotBlank() }.toList()
    }

    /** Reads stdout without trimming, because porcelain status uses a leading space for worktree-only edits. */
    private fun gitRaw(vararg args: String): String {
        return try {
            val process = ProcessBuilder(listOf("git", "-C", repoRoot().toString()) + args)
                .redirectErrorStream(true)
                .start()
            val text = process.inputStream.bufferedReader(StandardCharsets.UTF_8).use { it.readText() }
            if (process.waitFor() == 0) text else "unknown"
        } catch (e: Exception) {
            "unknown"
        }
    }

    private fun porcelainPath(line: String): String =
        line.drop(3).substringAfter(" -> ").trim()

    /**
     * Paths this task is allowed to touch without contaminating the V4 production baseline.
     * The report is included because it is generated documentation, not production code.
     */
    private fun isTestOnlyDirtyPath(path: String): Boolean =
        path.startsWith("engine/src/test/") ||
            path.startsWith("tests/v5-golden/") ||
            path.startsWith("dotnet/tests/Mahjong.Ai.Tests/") ||
            path.startsWith("doc/v5-重构/")

    private fun sha256File(path: Path): String {
        val digest = MessageDigest.getInstance("SHA-256")
        Files.newInputStream(path).use { input ->
            val buffer = ByteArray(8192)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { String.format(Locale.ROOT, "%02x", it.toInt() and 0xff) }
    }
}

private fun actionPlayer(action: GameAction): Int = when (action) {
    is GameAction.DrawTile -> action.player
    is GameAction.DiscardTile -> action.player
    is GameAction.DeclareChi -> action.player
    is GameAction.DeclarePon -> action.player
    is GameAction.DeclareKan -> action.player
    is GameAction.DeclareRiichi -> action.player
    is GameAction.DeclareTsumo -> action.player
    is GameAction.DeclareRon -> action.player
    is GameAction.Pass -> action.player
    is GameAction.DeclareAbortiveDraw -> action.player
    is GameAction.DeclareAgariYame -> action.player
    else -> -1
}

private fun GameAction.player(): Int = actionPlayer(this)

// ----------------------------------------------------------------------
// Minimal deterministic JSON writer (test-only, no external dependency)
// ----------------------------------------------------------------------
private sealed class AJ {
    class Obj(val fields: List<Pair<String, AJ>>) : AJ()
    class Arr(val items: List<AJ>) : AJ()
    class Str(val value: String) : AJ()
    class Num(val value: Long) : AJ()
    class Bool(val value: Boolean) : AJ()
    object Null : AJ()

    fun render(): String {
        val sb = StringBuilder()
        renderTo(sb, 0)
        return sb.toString()
    }

    private fun renderTo(sb: StringBuilder, indent: Int) {
        when (this) {
            is Str -> sb.append(quote(value))
            is Num -> sb.append(value)
            is Bool -> sb.append(if (value) "true" else "false")
            is Arr -> {
                sb.append('[')
                items.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    sb.append('\n').append(" ".repeat(indent + 2))
                    item.renderTo(sb, indent + 2)
                }
                if (items.isNotEmpty()) sb.append('\n').append(" ".repeat(indent))
                sb.append(']')
            }
            is Obj -> {
                sb.append('{')
                fields.forEachIndexed { index, (name, value) ->
                    if (index > 0) sb.append(',')
                    sb.append('\n').append(" ".repeat(indent + 2))
                    sb.append(quote(name)).append(": ")
                    value.renderTo(sb, indent + 2)
                }
                if (fields.isNotEmpty()) sb.append('\n').append(" ".repeat(indent))
                sb.append('}')
            }
            Null -> sb.append("null")
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
                else -> if (c < ' ') sb.append(String.format("\\u%04x", c.code)) else sb.append(c)
            }
        }
        return sb.append('"').toString()
    }
}

private typealias J = AJ

private fun aj(value: String): J = AJ.Str(value)
private fun aj(value: Int): J = AJ.Num(value.toLong())
private fun aj(value: Long): J = AJ.Num(value)
private fun aj(value: Boolean): J = AJ.Bool(value)
private fun ajOrNull(value: String?): J = if (value == null) AJ.Null else AJ.Str(value)
private fun nullableInt(value: Int?): J = if (value == null) AJ.Null else AJ.Num(value.toLong())
private fun aobj(vararg fields: Pair<String, J>): J = AJ.Obj(fields.toList())
private fun aarr(items: List<J>): J = AJ.Arr(items)
