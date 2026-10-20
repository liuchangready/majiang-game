package com.majiang.server.host

import com.majiang.access.ReceiptStatus
import com.majiang.ai.AIStrategy
import com.majiang.ai.AiCandidate
import com.majiang.ai.AiCandidateLimits
import com.majiang.ai.AiCheatPolicy
import com.majiang.ai.AiDecisionContext
import com.majiang.ai.AiDecisionStamp
import com.majiang.ai.AiDecisionStrategy
import com.majiang.ai.AiEffect
import com.majiang.ai.AiPhase
import com.majiang.ai.AiRisk
import com.majiang.ai.AiSeatSnapshot
import com.majiang.ai.AiVisibleParams
import com.majiang.ai.cheat.AiCapabilityDescriptor
import com.majiang.ai.cheat.AiCapabilityIds
import com.majiang.ai.cheat.CheatAiCapability
import com.majiang.authority.TableAuthority
import com.majiang.cheat.beam.BeamSwapConfig
import com.majiang.cheat.bloom.SleeveBloomConfig
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.cheat.mark.TileMarkConfig
import com.majiang.economy.CheatEconomyConfig
import com.majiang.match.MatchConfig
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.stacking.ShuffleConfig
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.AbstractExecutorService
import java.util.concurrent.CountDownLatch
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

@Timeout(30)
class AiCheatDispatchContractTest {

    private class TestClock(var now: Long = 0L) : MonotonicClock {
        override fun nowMillis(): Long = now
    }

    private class DirectExecutorService : AbstractExecutorService() {
        private val closed = AtomicBoolean(false)
        override fun execute(command: Runnable) {
            if (!closed.get()) command.run()
        }

        override fun shutdown() {
            closed.set(true)
        }

        override fun shutdownNow(): MutableList<Runnable> {
            closed.set(true)
            return mutableListOf()
        }

        override fun isShutdown(): Boolean = closed.get()
        override fun isTerminated(): Boolean = closed.get()
        override fun awaitTermination(timeout: Long, unit: TimeUnit): Boolean = true
    }

    private fun economy() = CheatEconomyConfig(
        enabled = true,
        initialPoints = List(4) { 50 },
        costs = mapOf(
            "TILE_MARK" to 10,
            "SLEEVE_BLOOM" to 10,
            "BEAM_SWAP" to 10
        )
    )

    private fun config(
        policy: AiCheatPolicy = AiCheatPolicy(enabled = true, maxSpendPerRound = 30),
        mark: Boolean = false,
        bloom: Boolean = false,
        beam: Boolean = false,
        shuffle: ShuffleConfig = ShuffleConfig(enabled = false)
    ) = MatchConfig(
        cheatEconomy = economy(),
        shuffleConfig = shuffle,
        tileMarkConfig = TileMarkConfig(mark),
        sleeveBloomConfig = SleeveBloomConfig(bloom),
        beamSwapConfig = BeamSwapConfig(beam),
        aiCheatPolicy = policy
    )

    private fun activeState(currentPlayer: Int = 1): GameState = TestTableBuilder.create(
        drawn = currentPlayer to Tile(Suit.SOU, 4, 3),
        phase = GamePhase.CHEAT_WINDOW,
        currentPlayer = currentPlayer
    ).also {
        it.lastDrawKind = SleeveBloomDrawKind.NORMAL
        it.turnNumber = 5
        it.cheatPoints.fill(50)
    }

    private fun controllers(
        seatOne: SeatController = SeatController.Computer()
    ): List<SeatController> = listOf(
        SeatController.RemoteHuman(false),
        seatOne,
        SeatController.RemoteHuman(false),
        SeatController.RemoteHuman(false)
    )

    private fun <T> java.util.concurrent.CompletableFuture<T>.await(): T =
        get(5, TimeUnit.SECONDS)

    private class CheatSelectingStrategy(
        private val capabilityId: String
    ) : AIStrategy, AiDecisionStrategy {
        override val name: String = "cheat-selector-$capabilityId"

        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? = null

        override fun choose(context: AiDecisionContext): String? =
            context.candidates.firstOrNull {
                it.capabilityId == capabilityId && !it.isSafeDefault
            }?.optionId
    }

    @Test
    fun `ai tile mark is dispatched asynchronously and applied on the serial authority`() {
        val clock = TestClock()
        val audits = CopyOnWriteArrayList<AiDecisionAudit>()
        val host = ServerTableHost(
            config = config(mark = true),
            clock = clock,
            controllers = controllers(
                SeatController.Computer(CheatSelectingStrategy(AiCapabilityIds.TILE_MARK))
            ),
            initialState = activeState(),
            aiDecisionObserver = { audits.add(it) },
            aiExecutor = DirectExecutorService()
        )
        try {
            clock.now = 200L
            assertTrue(host.tick().await())
            val snapshot = host.snapshot(1).await()
            assertEquals(40, snapshot.cheatPoints[1], "audits=$audits")
            assertNotNull(snapshot.tileMark)
            assertEquals(1, snapshot.tileMark!!.activeGroups.size)
            assertTrue(host.registeredCheatAiCapabilityIds().contains(AiCapabilityIds.TILE_MARK))
        } finally {
            host.close()
        }
    }

    @Test
    fun `custom tile mark fee keeps candidate budget audit and ledger aligned`() {
        val clock = TestClock()
        val audits = CopyOnWriteArrayList<AiDecisionAudit>()
        val custom = MatchConfig(
            cheatEconomy = CheatEconomyConfig(
                enabled = true,
                initialPoints = List(4) { 50 },
                costs = mapOf("TILE_MARK" to 5)
            ),
            shuffleConfig = ShuffleConfig(enabled = false),
            tileMarkConfig = TileMarkConfig(true),
            aiCheatPolicy = AiCheatPolicy(enabled = true, maxSpendPerRound = 10)
        )
        val host = ServerTableHost(
            config = custom,
            clock = clock,
            controllers = controllers(
                SeatController.Computer(CheatSelectingStrategy(AiCapabilityIds.TILE_MARK))
            ),
            initialState = activeState(),
            aiDecisionObserver = { audits.add(it) },
            aiExecutor = DirectExecutorService()
        )
        try {
            clock.now = 200L
            assertTrue(host.tick().await())
            val snapshot = host.snapshot(1).await()
            assertEquals(45, snapshot.cheatPoints[1])
            assertEquals(1, snapshot.tileMark!!.activeGroups.size)
            val audit = audits.single { it.selectedCapabilityId == AiCapabilityIds.TILE_MARK }
            assertEquals(5, audit.cost, "Audit must report the configured fee")
            assertEquals(ReceiptStatus.APPLIED.name, audit.status)

            // 预算 10 / 单次 5：允许第二次落汗，总花费必须仍是 10。
            clock.now = 400L
            host.tick().await()
            val second = host.snapshot(1).await()
            assertEquals(40, second.cheatPoints[1])
            assertEquals(2, second.tileMark!!.activeGroups.size)

            // 预算耗尽后不得再产生第三组付费落汗。
            clock.now = 600L
            host.tick().await()
            val after = host.snapshot(1).await()
            assertEquals(40, after.cheatPoints[1])
            assertEquals(2, after.tileMark!!.activeGroups.size)
        } finally {
            host.close()
        }
    }

    @Test
    fun `ai sleeve bloom activation gets a follow-up offer choice decision`() {
        val clock = TestClock()
        val host = ServerTableHost(
            config = config(bloom = true),
            clock = clock,
            controllers = controllers(
                SeatController.Computer(CheatSelectingStrategy(AiCapabilityIds.SLEEVE_BLOOM))
            ),
            initialState = activeState(),
            aiExecutor = DirectExecutorService()
        )
        try {
            clock.now = 200L
            assertTrue(host.tick().await())
            val offered = host.snapshot(1).await()
            assertEquals(40, offered.cheatPoints[1])
            assertNotNull(offered.sleeveBloom!!.pendingOffer)

            clock.now = 400L
            host.tick().await()
            val resolved = host.snapshot(1).await()
            assertEquals(40, resolved.cheatPoints[1])
            assertNull(resolved.sleeveBloom!!.pendingOffer)
        } finally {
            host.close()
        }
    }

    @Test
    fun `ai beam swap activation and keep choice use existing authority submits`() {
        val clock = TestClock()
        val host = ServerTableHost(
            config = config(beam = true),
            clock = clock,
            controllers = controllers(
                SeatController.Computer(CheatSelectingStrategy(AiCapabilityIds.BEAM_SWAP))
            ),
            initialState = activeState(),
            aiExecutor = DirectExecutorService()
        )
        try {
            clock.now = 200L
            assertTrue(host.tick().await())
            val offered = host.snapshot(1).await()
            assertEquals(40, offered.cheatPoints[1])
            assertNotNull(offered.beamSwap!!.pendingOffer)

            clock.now = 400L
            host.tick().await()
            val resolved = host.snapshot(1).await()
            assertEquals(40, resolved.cheatPoints[1])
            assertNull(resolved.beamSwap!!.pendingOffer)
        } finally {
            host.close()
        }
    }

    @Test
    fun `disabled policy and takeover never spend points`() {
        val disabled = ServerTableHost(
            config = config(
                policy = AiCheatPolicy(enabled = false),
                mark = true,
                bloom = true,
                beam = true
            ),
            clock = TestClock(),
            controllers = controllers(),
            initialState = activeState(),
            aiExecutor = DirectExecutorService()
        )
        disabled.use {
            it.tick().await()
            val snapshot = it.snapshot(1).await()
            assertEquals(50, snapshot.cheatPoints[1])
            assertTrue(snapshot.tileMark!!.activeGroups.isEmpty())
            assertNull(snapshot.sleeveBloom?.pendingOffer)
            assertNull(snapshot.beamSwap?.pendingOffer)
        }

        val takeover = ServerTableHost(
            config = config(mark = true, bloom = true, beam = true),
            clock = TestClock(),
            controllers = controllers(seatOne = SeatController.RemoteHuman(false)),
            initialState = activeState(),
            aiExecutor = DirectExecutorService()
        )
        takeover.use {
            assertEquals(ReceiptStatus.APPLIED, it.releaseControl(1).await().status)
            it.tick().await()
            val snapshot = it.snapshot(1).await()
            assertEquals(50, snapshot.cheatPoints[1])
            assertTrue(snapshot.tileMark!!.activeGroups.isEmpty())
            assertNull(snapshot.sleeveBloom?.pendingOffer)
            assertNull(snapshot.beamSwap?.pendingOffer)
        }
    }

    @Test
    fun `shuffle preparation uses the unified strategy before the authority deadline`() {
        val clock = TestClock(0L)
        val host = ServerTableHost(
            config = MatchConfig(
                cheatEconomy = economy(),
                shuffleConfig = ShuffleConfig(enabled = true, aiMaySpend = false),
                aiCheatPolicy = AiCheatPolicy(enabled = true, maxSpendPerRound = 60)
            ),
            clock = clock,
            controllers = listOf(
                SeatController.RemoteHuman(false),
                SeatController.Computer(),
                SeatController.RemoteHuman(false),
                SeatController.RemoteHuman(false)
            ),
            aiExecutor = DirectExecutorService()
        )
        try {
            assertEquals(ReceiptStatus.APPLIED, host.start(0).await().status)
            assertTrue(host.tick().await())
            val prepared = host.snapshot(1).await()
            assertTrue(prepared.preparation!!.confirmed)
            assertEquals(30, prepared.preparation!!.reservedPoints)
            assertEquals(50, prepared.cheatPoints[1])

            clock.now = 10_000L
            host.tick().await()
            val dealt = host.snapshot(1).await()
            assertEquals(20, dealt.cheatPoints[1])
            assertNull(dealt.preparation)
        } finally {
            host.close()
        }
    }

    /**
     * 测试用新千术适配器：仅验证组合根追加后通用调度器与策略无需修改即可发现并提交。
     */
    private class ProbeCapability : CheatAiCapability {
        val submissions = AtomicInteger()
        override val id: String = "test_probe_capability"
        override val version: Int = 1

        override fun describe(): AiCapabilityDescriptor = AiCapabilityDescriptor(
            id = id,
            version = version,
            phases = setOf(AiPhase.ORDINARY_ACTION),
            supportsSubmit = true,
            maxCandidates = 2
        )

        override fun candidates(view: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate> =
            listOf(
                AiCandidate(
                    optionId = "$id:probe",
                    capabilityId = id,
                    phase = AiPhase.ORDINARY_ACTION,
                    cost = 0,
                    risk = AiRisk(),
                    estimatedEffect = AiEffect(handValue = 900, note = "probe"),
                    terminalForWindow = true,
                    parameters = AiVisibleParams.EMPTY
                )
            )

        override fun submit(
            seat: Int,
            selected: AiCandidate,
            stamp: AiDecisionStamp,
            now: Long
        ): TableAuthority.MutationResult {
            submissions.incrementAndGet()
            return TableAuthority.MutationResult(
                com.majiang.access.ActionReceipt(selected.optionId, ReceiptStatus.APPLIED),
                emptyList(),
                true
            )
        }
    }

    private class ProbeStrategy : AIStrategy, AiDecisionStrategy {
        override val name: String = "probe"

        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? =
            legalActions.filterIsInstance<GameAction.DiscardTile>().firstOrNull()

        override fun choose(context: AiDecisionContext): String? =
            context.candidates.firstOrNull { it.capabilityId == "test_probe_capability" }?.optionId
    }

    @Test
    fun `a capability appended at the composition root is discovered and submitted`() {
        val probe = ProbeCapability()
        val audits = CopyOnWriteArrayList<AiDecisionAudit>()
        val clock = TestClock()
        val host = ServerTableHost(
            config = config(),
            clock = clock,
            controllers = controllers(SeatController.Computer(ProbeStrategy())),
            initialState = activeState(),
            cheatAiCapabilityFactories = listOf { probe },
            aiDecisionObserver = { audits.add(it) },
            aiExecutor = DirectExecutorService()
        )
        try {
            assertTrue(host.registeredCheatAiCapabilityIds().contains(probe.id))
            clock.now = 200L
            assertTrue(host.tick().await())
            assertEquals(1, probe.submissions.get())
            assertTrue(audits.any { it.selectedCapabilityId == probe.id })
        } finally {
            host.close()
        }
    }

    private class BlockingStrategy : AIStrategy, AiDecisionStrategy {
        override val name: String = "blocking"
        val entered = CountDownLatch(1)
        val finished = CountDownLatch(1)
        val release = CountDownLatch(1)
        val calls = AtomicInteger()

        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
            entered.countDown()
            release.await(10, TimeUnit.SECONDS)
            finished.countDown()
            calls.incrementAndGet()
            return legalActions.filterIsInstance<GameAction.DiscardTile>().firstOrNull()
        }

        override fun choose(context: AiDecisionContext): String? {
            entered.countDown()
            release.await(10, TimeUnit.SECONDS)
            finished.countDown()
            calls.incrementAndGet()
            return context.candidates.firstOrNull {
                it.capabilityId == AiCandidateIds.MAHJONG
            }?.optionId
        }

        private object AiCandidateIds {
            const val MAHJONG = "mahjong"
        }
    }

    private class ActivateThenNullStrategy(
        private val capabilityId: String
    ) : AIStrategy, AiDecisionStrategy {
        override val name: String = "activate-then-null-$capabilityId"

        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? = null

        override fun choose(context: AiDecisionContext): String? {
            if (context.stamp.phase != AiPhase.ORDINARY_ACTION) return null
            return context.candidates.firstOrNull {
                it.capabilityId == capabilityId && !it.isSafeDefault
            }?.optionId
        }
    }

    @Test
    fun `offer choice safely keeps first when the strategy declines or throws`() {
        val clock = TestClock()
        val audits = CopyOnWriteArrayList<AiDecisionAudit>()
        val host = ServerTableHost(
            config = config(bloom = true),
            clock = clock,
            controllers = controllers(
                SeatController.Computer(ActivateThenNullStrategy(AiCapabilityIds.SLEEVE_BLOOM))
            ),
            initialState = activeState(),
            aiDecisionObserver = { audits.add(it) },
            aiExecutor = DirectExecutorService()
        )
        try {
            clock.now = 200L
            assertTrue(host.tick().await())
            val offered = host.snapshot(1).await()
            val firstTile = offered.sleeveBloom!!.pendingOffer!!.firstTile
            assertEquals(40, offered.cheatPoints[1])

            clock.now = 400L
            assertTrue(host.tick().await())
            val resolved = host.snapshot(1).await()
            assertEquals(40, resolved.cheatPoints[1])
            assertNull(resolved.sleeveBloom!!.pendingOffer)
            assertEquals(firstTile, resolved.view.myDrawnTile)
            assertTrue(audits.any { it.safeFallback && it.stamp.phase == AiPhase.OFFER_CHOICE })
        } finally {
            host.close()
        }
    }

    @Test
    fun `per round budget prevents a second activation in the same window`() {
        val clock = TestClock()
        val host = ServerTableHost(
            config = config(
                policy = AiCheatPolicy(enabled = true, maxSpendPerRound = 10),
                mark = true
            ),
            clock = clock,
            controllers = controllers(
                SeatController.Computer(CheatSelectingStrategy(AiCapabilityIds.TILE_MARK))
            ),
            initialState = activeState(),
            aiExecutor = DirectExecutorService()
        )
        try {
            clock.now = 200L
            assertTrue(host.tick().await())
            val first = host.snapshot(1).await()
            assertEquals(40, first.cheatPoints[1])
            assertEquals(1, first.tileMark!!.activeGroups.size)

            clock.now = 400L
            host.tick().await()
            val after = host.snapshot(1).await()
            assertEquals(40, after.cheatPoints[1], "Second activation must be blocked by the per-round budget")
            assertEquals(1, after.tileMark!!.activeGroups.size)
        } finally {
            host.close()
        }
    }

    private fun offTurnState(): GameState = TestTableBuilder.create(
        phase = GamePhase.DRAW,
        currentPlayer = 0
    ).also {
        it.turnNumber = 5
        it.cheatPoints.fill(50)
        it.openSleeveBloomWindow(1)
    }

    @Test
    fun `off turn sleeve bloom reservation is discovered and resolved in a later decision`() {
        val clock = TestClock()
        val host = ServerTableHost(
            config = config(bloom = true),
            clock = clock,
            controllers = controllers(
                SeatController.Computer(CheatSelectingStrategy(AiCapabilityIds.SLEEVE_BLOOM))
            ),
            initialState = offTurnState(),
            aiExecutor = DirectExecutorService()
        )
        try {
            clock.now = 200L
            assertTrue(host.tick().await())
            val activated = host.snapshot(1).await()
            assertEquals(40, activated.cheatPoints[1])
            assertNotNull(activated.sleeveBloom!!.pendingOffer)

            clock.now = 400L
            host.tick().await()
            val reserved = host.snapshot(1).await()
            assertEquals(40, reserved.cheatPoints[1])
            assertNull(reserved.sleeveBloom!!.pendingOffer)
            assertNotNull(reserved.sleeveBloom!!.reservation)
        } finally {
            host.close()
        }
    }

    @Test
    fun `control epoch change discards an in-flight ai result`() {
        val clock = TestClock()
        val blocker = BlockingStrategy()
        val audits = CopyOnWriteArrayList<AiDecisionAudit>()
        val aiExecutor: ExecutorService = Executors.newSingleThreadExecutor { r ->
            Thread(r, "ai-stale-test").apply { isDaemon = true }
        }
        val host = ServerTableHost(
            config = config(),
            clock = clock,
            controllers = controllers(SeatController.Computer(blocker)),
            initialState = activeState(),
            aiDecisionObserver = { audits.add(it) },
            aiExecutor = aiExecutor
        )
        try {
            assertTrue(blocker.entered.await(5, TimeUnit.SECONDS))
            val oldEpoch = host.snapshot(1).await().decision.controlEpoch
            assertEquals(ReceiptStatus.APPLIED, host.releaseControl(1).await().status)
            blocker.release.countDown()
            assertTrue(blocker.finished.await(5, TimeUnit.SECONDS))

            clock.now = 200L
            host.tick().await()
            clock.now = 400L
            host.tick().await()
            assertTrue(
                audits.none { it.stamp.controlEpoch == oldEpoch && it.selectedCapabilityId == "mahjong" },
                "Stale epoch result must be discarded, audits=$audits"
            )
        } finally {
            blocker.release.countDown()
            aiExecutor.shutdownNow()
            host.close()
        }
    }
}
