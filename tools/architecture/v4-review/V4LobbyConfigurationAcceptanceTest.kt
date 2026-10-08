package com.majiang.v4lobby

import com.majiang.authority.TableAuthority
import com.majiang.access.ReceiptStatus
import com.majiang.match.MatchConfig
import com.majiang.stacking.ShuffleConfig
import com.majiang.state.GamePhase
import com.majiang.timing.MonotonicClock
import com.majiang.timing.TimeControlConfig
import org.junit.jupiter.api.Test
import kotlin.test.*

// White-box review only: retain rule/configuration and physical-state assertions without
// adding a production accessor for mutable internals. Never used to mutate the authority.
internal fun TableAuthority.reviewCoordinator(): com.majiang.application.ApplicationCoordinator =
    TableAuthority::class.java.getDeclaredField("coordinator").let {
        it.isAccessible = true
        it.get(this) as com.majiang.application.ApplicationCoordinator
    }

/** Domain probes run independently before room bindings are integrated. */
class V4LobbyConfigurationAcceptanceTest {
    private class Clock(var now: Long = 100L) : MonotonicClock {
        override fun nowMillis() = now
    }

    @Test fun lobbyConfigurationChangesActualRulesScoresAndClockWithStableSession() {
        val clock = Clock()
        TableAuthority(clock = clock).use { authority ->
            val sessionId = authority.sessionId
            val changed = MatchConfig(winds = listOf(1), handsPerWind = 2,
                startingScores = listOf(26000, 24000, 25000, 25000), targetScore = 28000)
            assertTrue(authority.updateLobbyConfig(changed,
                TimeControlConfig(baseMillis = 2000L, reserveMillis = 10000L)))
            assertEquals(sessionId, authority.sessionId)
            assertEquals(changed.toRuleConfig(), authority.reviewCoordinator().session.ruleConfig)
            assertEquals(changed.toRuleConfig(), authority.reviewCoordinator().matchProgression.config)
            assertEquals(changed.startingScores, authority.queryView(0).scores)
            assertEquals(ReceiptStatus.APPLIED, authority.start(0, clock.now).receipt.status)
            authority.syncDecisionsAndClocks(clock.now, emptySet())
            assertEquals(changed.startingScores, authority.queryView(0).scores)
            val timer = authority.querySnapshot(0, "domain-review", 0L, emptyList()).clocks.first { it.seat == 0 }
            assertEquals(12000L, timer.deadline - timer.opensAt)
            assertEquals(sessionId, authority.sessionId)
        }
    }

    @Test fun invalidOrStartedConfigurationCannotPartiallyChangeStateOrClock() {
        val clock = Clock()
        TableAuthority(clock = clock).use { authority ->
            val config = authority.config
            val sessionId = authority.sessionId
            assertFailsWith<IllegalArgumentException> {
                authority.updateLobbyConfig(config.copy(startingScores = listOf(1)))
            }
            assertEquals(config, authority.config)
            assertEquals(sessionId, authority.sessionId)
            assertEquals(ReceiptStatus.APPLIED, authority.start(0, clock.now).receipt.status)
            authority.syncDecisionsAndClocks(clock.now, emptySet())
            val before = authority.querySnapshot(0, "domain-review", 0L, emptyList())
            clock.now += 1000L
            assertFalse(authority.updateLobbyConfig(config.copy(targetScore = 35000),
                TimeControlConfig(baseMillis = 9000L, reserveMillis = 90000L)))
            val after = authority.querySnapshot(0, "domain-review", 0L, emptyList())
            assertEquals(before.roundId, after.roundId)
            assertEquals(before.view, after.view)
            assertEquals(before.clocks.map { it.deadline }, after.clocks.map { it.deadline })
            assertEquals(config, authority.config)
        }
    }

    @Test fun enablingShuffleChangesRealBudgetAndLocksConfigurationDuringSelection() {
        val clock = Clock()
        TableAuthority(clock = clock).use { authority ->
            val changed = MatchConfig(winds = listOf(1), shuffleConfig = ShuffleConfig(enabled = true))
            assertTrue(authority.updateLobbyConfig(changed))
            assertEquals(ReceiptStatus.APPLIED, authority.start(0, clock.now).receipt.status)
            assertTrue(authority.isPreparationActive())
            assertEquals(GamePhase.STACKING, authority.reviewCoordinator().activeRound!!.readState().phase)
            assertEquals(listOf(50, 50, 50, 50),
                authority.querySnapshot(0, "domain-review", 0L, emptyList()).cheatPoints)
            assertFalse(authority.updateLobbyConfig(MatchConfig()),
                "Shuffle selection is already started even though tiles are not dealt")
            assertTrue(authority.isPreparationActive())
            assertTrue(authority.config.shuffleConfig.enabled)
        }
    }
}
