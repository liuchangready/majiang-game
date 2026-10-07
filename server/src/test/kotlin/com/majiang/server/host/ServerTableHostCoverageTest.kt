package com.majiang.server.host

import com.majiang.access.ActionReceipt
import com.majiang.access.PlayerSnapshot
import com.majiang.access.PublicRoundRecord
import com.majiang.access.ReceiptStatus
import com.majiang.ai.AIStrategy
import com.majiang.economy.CheatEconomyConfig
import com.majiang.application.ActionChoice
import com.majiang.application.PlayerDecision
import com.majiang.control.DisconnectPolicy
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.match.MatchFinishReason
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.model.TileLocation
import com.majiang.session.PlayerView
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.state.GameAction
import com.majiang.state.ActionResult
import com.majiang.state.GameState
import com.majiang.state.SeatController
import com.majiang.testing.TestTableBuilder
import com.majiang.timing.MonotonicClock
import com.majiang.timing.TimeControlConfig
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTimeoutPreemptively
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.time.Duration
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/**
 * 针对新服务端宿主 [ServerTableHost] 的全量等价行为覆盖测试集
 *
 * 忠实迁移 R4-host-test-preservation-baseline.json 中保留的旧 TableHost 全部 20 项验收测试：
 * - BankruptcyHostAcceptanceTest (3 tests)
 * - HostRhythmAcceptanceTest (4 tests)
 * - HostAiIsolationAcceptanceTest (2 tests)
 * - TableHostAcceptanceTest (4 tests)
 * - ShufflePreparationAcceptanceTest (7 tests)
 */
@Timeout(15)
class ServerTableHostCoverageTest {

    private class TestClock(var now: Long = 0L) : MonotonicClock {
        override fun nowMillis(): Long = now
    }

    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    private fun humans() = List(4) { SeatController.RemoteHuman(false) }
    private fun rhythmHumans() = listOf(SeatController.LocalHuman()) + List(3) { SeatController.RemoteHuman(false) }

    /**
     * 测试专用席位包装辅助器（仅在测试中使用，直接转发至真实 ServerTableHost API）
     */
    private class TestSeat(val seat: Int, val host: ServerTableHost) {
        val viewerSeat: Int get() = seat
        fun snapshot(): CompletableFuture<PlayerSnapshot> = host.snapshot(seat)
        fun decision(): CompletableFuture<PlayerDecision> = host.decision(seat)
        fun submit(choice: ActionChoice): CompletableFuture<ActionReceipt> = host.submitChoice(seat, choice)
        fun nextRound(): CompletableFuture<ActionReceipt> = host.nextRound(seat)
        fun start(): CompletableFuture<ActionReceipt> = host.start(seat)
        fun claimControl(): CompletableFuture<ActionReceipt> = host.claimControl(seat)
        fun releaseControl(): CompletableFuture<ActionReceipt> = host.releaseControl(seat)
        fun submitShufflePlan(plan: ShufflePlanRequest): CompletableFuture<ActionReceipt> = host.submitShufflePlan(seat, plan)
        fun queryRoundRecords(): CompletableFuture<List<PublicRoundRecord>> =
            host.snapshot(seat).thenApply { it.session?.roundRecords ?: emptyList() }
    }

    private fun choice(seat: TestSeat, id: String = "discard"): ActionChoice {
        val d = seat.decision().await()
        val option = d.options.first { it.action is GameAction.DiscardTile }
        return ActionChoice(id, d.roundId, d.decisionId, d.controlEpoch, option.optionId)
    }

    private fun request(seat: TestSeat, id: String, selection: ShuffleSelection): ShufflePlanRequest {
        val snap = seat.snapshot().await()
        return ShufflePlanRequest(id, snap.roundId!!, snap.decision.controlEpoch, selection)
    }

    private fun fixture(draw: Tile = Tile(Suit.WIND, 4)): GameState {
        fun tiles(suit: Suit, ranks: String): List<Tile> {
            val copies = mutableMapOf<Int, Int>()
            return ranks.map { ch ->
                val rank = ch.digitToInt()
                Tile(suit, rank, copies.getOrDefault(rank, 0)).also { copies[rank] = it.copyIndex + 1 }
            }
        }
        val hand = tiles(Suit.DRAGON, "111") + tiles(Suit.MAN, "234") +
            tiles(Suit.PIN, "678") + tiles(Suit.SOU, "2256")
        return TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to draw).also {
            it.riichiDeclared[0] = true
            it.players[0].riichiDeclared = true
            it.scores[0] -= 1000
            it.riichiSticks++
        }
    }

    // ========================================
    // 1. 破产与终局结算 (BankruptcyHost 对应 3 项)
    // ========================================

    @Test
    fun `exhaustive draw bankruptcy settles deposits once and outranks dealer continuation`() {
        val waiting = (1..6).map { Tile(Suit.MAN, it) } + (7..9).map { Tile(Suit.PIN, it) } +
            (2..5).map { Tile(Suit.SOU, it) }
        val noten = (1..4).map { Tile(Suit.WIND, it) } + (1..3).map { Tile(Suit.DRAGON, it) } +
            listOf(Tile(Suit.MAN, 2), Tile(Suit.MAN, 4), Tile(Suit.PIN, 1), Tile(Suit.PIN, 7), Tile(Suit.SOU, 1), Tile(Suit.SOU, 9))
        val hands = (1..3).associateWith { seat -> noten.map { it.copy(copyIndex = seat) } } + (0 to waiting)
        val state = TestTableBuilder.create(hands = hands, drawn = 0 to Tile(Suit.DRAGON, 3))
        state.riichiSticks = 3
        intArrayOf(49900, 100, 22000, 25000).copyInto(state.scores)
        for (tile in state.wall.subList(state.wallPointer, state.wallEndExclusive).toList()) {
            state.tilePool.transfer(tile, TileLocation.WALL, TileLocation.discard(2))
            state.players[2].discards.add(tile)
            state.players[2].discardHistory.add(tile)
        }
        state.wallPointer = state.wallEndExclusive
        TestTableBuilder.assertPhysical(state)

        val clock = TestClock(0L)
        ServerTableHost(
            initialState = state,
            clock = clock,
            controllers = humans()
        ).use { host ->
            val seats = (0..3).map { TestSeat(it, host) }
            val d = seats[0].decision().await()
            val discard = d.options.first { it.action == GameAction.DiscardTile(0, Tile(Suit.DRAGON, 3)) }
            seats[0].submit(ActionChoice("last-discard", d.roundId, d.decisionId, d.controlEpoch, discard.optionId)).await()
            repeat(12) { n ->
                for (seat in seats) {
                    val decision = seat.decision().await()
                    val pass = decision.options.firstOrNull { it.action is GameAction.Pass }
                    if (pass != null) {
                        seat.submit(ActionChoice("pass-$n", decision.roundId, decision.decisionId, decision.controlEpoch, pass.optionId)).await()
                    }
                }
                host.tick().await()
            }
            val ended = seats[0].snapshot().await()
            assertEquals("FINISHED", ended.session!!.lifecycle)
            val result = ended.session!!.finalResult!!
            assertEquals("BANKRUPTCY", result.reason)
            assertEquals(listOf(55900, -900, 21000, 24000), result.finalScores)
            assertEquals(100000, result.finalScores.sum())
            repeat(3) {
                seats.forEach { assertEquals(ReceiptStatus.REJECTED, it.nextRound().await().status) }
                host.tick().await()
            }
            assertEquals(result, seats[0].snapshot().await().session!!.finalResult)
        }
    }

    @Test
    fun `zero score follows configured inclusive policy in real host`() {
        for (inclusive in listOf(false, true)) {
            val hand = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit -> (2..4).map { Tile(suit, it) } } +
                (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)
            fun state() = TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.MAN, 5))
            var payment = 0
            for (probe in listOf(true, false)) {
                val s = state()
                if (!probe) {
                    s.scores[0] = 50000 - payment
                    s.scores[1] = payment
                }
                ServerTableHost(
                    config = MatchConfig(bankruptcyInclusive = inclusive),
                    initialState = s,
                    clock = TestClock(0L),
                    controllers = humans()
                ).use { host ->
                    val a = TestSeat(0, host)
                    val d = a.decision().await()
                    val win = d.options.first { it.action is GameAction.DeclareTsumo }
                    a.submit(ActionChoice("win", d.roundId, d.decisionId, d.controlEpoch, win.optionId)).await()
                    val snapshot = a.snapshot().await()
                    if (probe) {
                        payment = 25000 - snapshot.view.scores[1]
                    } else {
                        assertEquals(0, snapshot.view.scores[1])
                        assertEquals(inclusive, snapshot.session!!.lifecycle == "FINISHED")
                    }
                }
            }
        }
    }

    @Test
    fun `dealer win bankrupts opponent and permanently ends session including reconnect`() {
        val hand = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit -> (2..4).map { Tile(suit, it) } } +
            (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)
        val state = TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.MAN, 5))
        state.scores[0] = 49900
        state.scores[1] = 100
        ServerTableHost(
            initialState = state,
            clock = TestClock(0L),
            controllers = humans()
        ).use { host ->
            val access = TestSeat(0, host)
            val before = access.snapshot().await()
            val d = access.decision().await()
            val choice = d.options.first { it.action is GameAction.DeclareTsumo }
            val request = ActionChoice("bankruptcy-win", d.roundId, d.decisionId, d.controlEpoch, choice.optionId)
            assertEquals(ReceiptStatus.APPLIED, access.submit(request).await().status)
            val ended = access.snapshot().await()
            assertEquals("FINISHED", ended.session!!.lifecycle)
            assertEquals(MatchFinishReason.BANKRUPTCY.name, ended.session!!.finalResult!!.reason)
            assertTrue(ended.session!!.finalResult!!.finalScores[1] < 0)
            assertEquals(100000, ended.session!!.finalResult!!.finalScores.sum())
            assertTrue(ended.decision.options.isEmpty())
            assertEquals(ReceiptStatus.REJECTED, access.nextRound().await().status)
            assertEquals(ReceiptStatus.REJECTED, access.start().await().status)
            repeat(10) { host.tick().await() }
            val restored = host.resume(0, ended.cursor).await()
            assertEquals(ended.session!!.finalResult, restored.session!!.finalResult)
            assertEquals(before.roundId, restored.settlementRoundId)
            assertEquals(ended.roundId, restored.roundId)
            assertEquals(ended.roundVersion, restored.roundVersion)
            assertTrue(restored.settlementEvents.isNotEmpty())
        }
    }

    @Test
    fun `settlement arrays are independent for each subscriber snapshot and result query`() {
        ServerTableHost(initialState = fixture(Tile(Suit.SOU, 4)), clock = TestClock(0L),
            controllers = humans()).use { host ->
            val secondScores = AtomicReference<List<Int>>()
            val settled = CountDownLatch(2)
            val first = host.subscribeSeat(0) { update ->
                update.visibleEvents.filterIsInstance<GameEvent.ScoreSettled>().forEach { event ->
                    event.newScores[0] = -999
                    update.snapshot.settlementEvents.filterIsInstance<GameEvent.ScoreSettled>()
                        .forEach { it.newScores[1] = -999 }
                    settled.countDown()
                }
            }
            val second = host.subscribeSeat(0) { update ->
                update.visibleEvents.filterIsInstance<GameEvent.ScoreSettled>().forEach { event ->
                    secondScores.set(event.newScores.toList())
                    settled.countDown()
                }
            }
            try {
                val seat = TestSeat(0, host)
                val decision = seat.decision().await()
                val win = decision.options.first { it.action is GameAction.DeclareTsumo }
                val choice = ActionChoice("settlement-copy", decision.roundId,
                    decision.decisionId, decision.controlEpoch, win.optionId)
                assertEquals(ReceiptStatus.APPLIED, seat.submit(choice).await().status)
                assertTrue(settled.await(3, TimeUnit.SECONDS), "Both subscribers must receive settlement")
                val snapshot = seat.snapshot().await()
                val expected = snapshot.view.scores
                assertEquals(expected, secondScores.get())
                val roundId = snapshot.settlementRoundId!!
                val fromSnapshot = snapshot.settlementEvents.filterIsInstance<GameEvent.ScoreSettled>().single()
                assertEquals(expected, fromSnapshot.newScores.toList())
                fromSnapshot.newScores[2] = -999
                assertEquals(expected, seat.snapshot().await().settlementEvents
                    .filterIsInstance<GameEvent.ScoreSettled>().single().newScores.toList())

                val queried = host.queryRoundResultEvents(0, roundId).await()
                    .filterIsInstance<GameEvent.ScoreSettled>().single()
                assertEquals(expected, queried.newScores.toList())
                queried.newScores[3] = -999
                assertEquals(expected, host.queryRoundResultEvents(0, roundId).await()
                    .filterIsInstance<GameEvent.ScoreSettled>().single().newScores.toList())
            } finally {
                first.close()
                second.close()
            }
        }
    }

    // ========================================
    // 2. 宿主节奏与立直/暂停 (HostRhythm 对应 4 项)
    // ========================================

    @Test
    fun `riichi automatic discard respects interval and never catches up multiple actions`() {
        val clock = TestClock(0L)
        ServerTableHost(initialState = fixture(), clock = clock, controllers = rhythmHumans()).use { host ->
            val a = TestSeat(0, host)
            clock.now = 149
            assertFalse(host.tick().await())
            assertTrue(a.snapshot().await().view.discards[0].isEmpty())
            clock.now = 150
            assertTrue(host.tick().await())
            assertEquals(1, a.snapshot().await().view.discards[0].size)
            val version = a.snapshot().await().roundVersion
            repeat(10) { host.tick().await() }
            assertEquals(version, a.snapshot().await().roundVersion)
        }
    }

    @Test
    fun `presentation playback gates paced riichi without accumulating delayed steps`() {
        val clock = TestClock(0L)
        ServerTableHost(initialState = fixture(), clock = clock, controllers = rhythmHumans(),
            timeControl = TimeControlConfig(baseMillis = 1_000_000L)).use { host ->
            val seat = TestSeat(0, host)
            val initial = seat.snapshot().await().roundVersion!!
            clock.now = 500_000L
            repeat(5) { assertFalse(host.tick(presentationBusy = true).await()) }
            assertEquals(initial, seat.snapshot().await().roundVersion)

            // One long frame releases at most one paced action. Repeated ticks at the
            // same timestamp cannot spend the playback interval as scheduler debt.
            assertTrue(host.tick(presentationBusy = false).await())
            val after = seat.snapshot().await()
            assertEquals(initial + 1, after.roundVersion)
            assertEquals(1, after.view.discards[0].size)
            repeat(5) { assertFalse(host.tick(presentationBusy = false).await()) }
            assertEquals(after.roundVersion, seat.snapshot().await().roundVersion)
        }
    }

    @Test
    fun `presentation playback never postpones authority timeout`() {
        val clock = TestClock(0L)
        ServerTableHost(initialState = fixture(), clock = clock, controllers = rhythmHumans()).use { host ->
            val seat = TestSeat(0, host)
            clock.now = 22_999L
            assertFalse(host.tick(presentationBusy = true).await())
            assertTrue(seat.snapshot().await().view.discards[0].isEmpty())
            clock.now = 23_000L
            assertTrue(host.tick(presentationBusy = true).await())
            assertEquals(1, seat.snapshot().await().view.discards[0].size)
        }
    }

    @Test
    fun `ordinary human never auto discards and closed host rejects ticks and choices`() {
        val clock = TestClock(0L)
        val host = ServerTableHost(initialState = fixture(), clock = clock, controllers = humans())
        try {
            val seat = TestSeat(0, host)
            val command = choice(seat)
            val initial = seat.snapshot().await().roundVersion
            clock.now = 1_000L
            repeat(10) { assertFalse(host.tick().await()) }
            assertEquals(initial, seat.snapshot().await().roundVersion)
            assertTrue(seat.snapshot().await().view.discards[0].isEmpty())
            host.close()
            assertThrows(ExecutionException::class.java) { host.tick().await() }
            assertThrows(ExecutionException::class.java) { seat.submit(command).await() }
        } finally {
            host.close()
        }
    }

    @Test
    fun `riichi win and legal concealed kan remain choices until authority deadline`() {
        for (draw in listOf(Tile(Suit.SOU, 4), Tile(Suit.DRAGON, 1, 3))) {
            val clock = TestClock(0L)
            ServerTableHost(initialState = fixture(draw), clock = clock, controllers = rhythmHumans()).use { host ->
                val a = TestSeat(0, host)
                for (t in listOf(150L, 1000L, 22000L)) {
                    clock.now = t
                    host.tick().await()
                }
                assertTrue(a.snapshot().await().view.discards[0].isEmpty())
                assertTrue(a.decision().await().options.any { it.action is GameAction.DeclareTsumo || it.action is GameAction.DeclareKan })
                clock.now = 23000
                host.tick().await()
                assertEquals(1, a.snapshot().await().view.discards[0].size)
            }
        }
    }

    @Test
    fun `local pause freezes automatic actions and rejects commands until resume`() {
        val clock = TestClock(0L)
        ServerTableHost(initialState = fixture(), clock = clock, controllers = rhythmHumans()).use { host ->
            val a = TestSeat(0, host)
            host.pause().await()
            clock.now = 50000
            repeat(3) { host.tick().await() }
            assertTrue(a.snapshot().await().view.discards[0].isEmpty())
            val d = a.decision().await()
            val option = d.options.first { it.action is GameAction.DiscardTile }
            assertEquals(ReceiptStatus.REJECTED, a.submit(ActionChoice("paused", d.roundId, d.decisionId, d.controlEpoch, option.optionId)).await().status)
            host.resumeClock().await()
            assertEquals(73000L, a.snapshot().await().clocks.first { it.seat == 0 }.deadline)
        }
    }

    @Test
    fun `takeover and reclaim rotate control token without gifting decision time`() {
        val clock = TestClock(0L)
        ServerTableHost(clock = clock, controllers = rhythmHumans()).use { host ->
            val a = TestSeat(0, host)
            a.start().await()
            val first = a.decision().await()
            clock.now = 5000
            assertEquals(ReceiptStatus.APPLIED, a.releaseControl().await().status)
            val taken = a.decision().await().controlEpoch
            assertTrue(taken > first.controlEpoch)
            a.releaseControl().await()
            assertEquals(taken, a.decision().await().controlEpoch)
            a.claimControl().await()
            assertTrue(a.decision().await().controlEpoch > taken)
            assertEquals(23000L, a.snapshot().await().clocks.first { it.seat == 0 }.deadline)
            val option = first.options.first { it.action is GameAction.DiscardTile }
            assertEquals(ReceiptStatus.REJECTED, a.submit(ActionChoice("old", first.roundId, first.decisionId, first.controlEpoch, option.optionId)).await().status)
        }
    }

    // ========================================
    // 3. AI 隔离与非阻塞执行 (HostAiIsolation 对应 2 项)
    // ========================================

    private class StalledAi : AIStrategy {
        override val name = "acceptance stalled AI"
        val release = CountDownLatch(1)
        override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
            release.await(5, TimeUnit.SECONDS)
            return legalActions.firstOrNull()
        }
    }

    @Test
    fun `unresponsive turn AI cannot block queries or authoritative timeout`() {
        val clock = TestClock(0L)
        val ai = StalledAi()
        val host = ServerTableHost(
            clock = clock,
            controllers = listOf(SeatController.Computer(ai)) + List(3) { SeatController.RemoteHuman(false) }
        )
        try {
            assertTimeoutPreemptively(Duration.ofSeconds(1)) { host.start(0).await() }
            assertTimeoutPreemptively(Duration.ofSeconds(1)) { host.snapshot(0).await() }
            clock.now = 23000
            assertTimeoutPreemptively(Duration.ofSeconds(1)) { host.tick().await() }
            assertEquals(1, host.snapshot(0).await().view.discards[0].size)
        } finally {
            ai.release.countDown()
            host.close()
        }
    }

    @Test
    fun `dealer session choice never executes AI strategy while holding table lock`() {
        val hand = listOf(Suit.MAN, Suit.PIN, Suit.SOU).flatMap { suit -> (2..4).map { Tile(suit, it) } } +
            (6..8).map { Tile(Suit.SOU, it) } + Tile(Suit.MAN, 5, 3)
        val state = TestTableBuilder.create(hands = mapOf(0 to hand), drawn = 0 to Tile(Suit.MAN, 5)).also {
            it.roundWind = 2
            it.roundNumber = 3
            listOf(40000, 20000, 20000, 20000).forEachIndexed { seat, score -> it.scores[seat] = score }
        }
        val clock = TestClock(0L)
        val ai = StalledAi()
        val host = ServerTableHost(
            clock = clock,
            initialState = state,
            controllers = humans(),
            disconnectPolicy = DisconnectPolicy(aiProfileId = "stalled"),
            aiProfiles = mapOf("stalled" to { ai })
        )
        try {
            val endpoint = TestSeat(0, host)
            val decision = endpoint.decision().await()
            val win = decision.options.first { it.action is GameAction.DeclareTsumo }
            endpoint.submit(ActionChoice("win", decision.roundId, decision.decisionId, decision.controlEpoch, win.optionId)).await()
            assertTrue(endpoint.decision().await().options.any { it.action is GameAction.DeclareAgariYame })
            endpoint.releaseControl().await()
            clock.now = 150
            assertTimeoutPreemptively(Duration.ofSeconds(1)) { host.tick().await() }
            assertTimeoutPreemptively(Duration.ofSeconds(1)) { host.snapshot(1).await() }
        } finally {
            ai.release.countDown()
            host.close()
        }
    }

    // ========================================
    // 4. 基础牌桌宿主规范 (TableHostAcceptance 对应 4 项)
    // ========================================

    @Test
    fun `default table provides authenticated seats initial private hands and server decision clocks`() {
        val host = ServerTableHost(clock = TestClock(), controllers = humans())
        try {
            val a = TestSeat(0, host)
            val b = TestSeat(1, host)
            assertEquals(ReceiptStatus.REJECTED, b.start().await().status)
            assertEquals(ReceiptStatus.APPLIED, a.start().await().status)
            val av = a.snapshot().await()
            val bv = b.snapshot().await()
            assertEquals(14, av.view.myAllClosedTiles.size)
            assertEquals(13, bv.view.myAllClosedTiles.size)
            assertEquals(av.sessionId, bv.sessionId)
            assertEquals(av.roundId, bv.roundId)
            assertTrue(av.decision.options.any { it.action is GameAction.DiscardTile })
            assertTrue(bv.decision.options.isEmpty())
            assertEquals(23000L, av.clocks.first { it.seat == 0 }.deadline)
        } finally {
            host.close()
        }
    }

    @Test
    fun `timed request idempotency survives late retry without consuming a second action`() {
        val source = TestClock()
        val host = ServerTableHost(clock = source, controllers = humans())
        try {
            val a = TestSeat(0, host)
            a.start().await()
            source.now = 5000
            val command = choice(a)
            val receipt = a.submit(command).await()
            assertNotEquals(ReceiptStatus.REJECTED, receipt.status)
            val after = a.snapshot().await()
            source.now = 100000
            assertEquals(receipt, a.submit(command).await())
            val retried = a.snapshot().await()
            assertEquals(after.roundVersion, retried.roundVersion)
            assertEquals(after.view.discards, retried.view.discards)
            assertEquals(ReceiptStatus.REJECTED, a.submit(command.copy(optionId = "different")).await().status)
        } finally {
            host.close()
        }
    }

    @Test
    fun `invalid choice cannot refresh deadline and timeout discards exactly once`() {
        val source = TestClock()
        val host = ServerTableHost(clock = source, controllers = humans())
        try {
            val a = TestSeat(0, host)
            a.start().await()
            val command = choice(a)
            source.now = 22000
            assertEquals(ReceiptStatus.REJECTED, a.submit(command.copy(optionId = "invalid")).await().status)
            assertEquals(23000L, a.snapshot().await().clocks.first { it.seat == 0 }.deadline)
            host.tick().await()
            assertTrue(a.snapshot().await().view.discards[0].isEmpty())
            source.now = 23000
            host.tick().await()
            assertEquals(1, a.snapshot().await().view.discards[0].size)
            assertEquals(ReceiptStatus.REJECTED, a.submit(command).await().status)
            host.tick().await()
            assertEquals(1, a.snapshot().await().view.discards[0].size)
        } finally {
            host.close()
        }
    }

    @Test
    fun `spectator seat cannot forge another player decision and only committed events advance stream`() {
        val host = ServerTableHost(clock = TestClock(), controllers = humans())
        try {
            val a = TestSeat(0, host)
            val b = TestSeat(1, host)
            a.start().await()
            val before = a.snapshot().await()
            assertEquals(ReceiptStatus.REJECTED, b.submit(choice(a)).await().status)
            repeat(5) { host.tick().await() }
            assertEquals(before.roundVersion, a.snapshot().await().roundVersion)
            assertEquals(before.cursor, a.snapshot().await().cursor)
            assertNotEquals(ReceiptStatus.REJECTED, a.submit(choice(a)).await().status)
            assertTrue(a.snapshot().await().cursor > before.cursor)
            assertEquals(a.snapshot().await().view.discards, b.snapshot().await().view.discards)
        } finally {
            host.close()
        }
    }

    // ========================================
    // 5. 洗牌千术准备阶段 (ShufflePreparation 对应 7 项)
    // ========================================

    private val initial = ShuffleSelection(initialTargets = setOf(ShuffleTarget.KOKUSHI))
    private val both = ShuffleSelection(setOf(ShuffleTarget.CHINITSU_MAN), setOf(ShuffleTarget.DAISANGEN))

    @Test
    fun `ten second selection is fixed independent of ordinary reserve and does not reveal hands`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = MatchConfig(winds = listOf(1), shuffleConfig = ShuffleConfig(enabled = true)),
            clock = clock,
            controllers = humans()
        ).use { h ->
            val seats = (0..3).map { TestSeat(it, h) }
            assertNotEquals(ReceiptStatus.REJECTED, seats[0].start().await().status)
            for (a in seats) {
                val s = a.snapshot().await()
                assertNotNull(s.preparation)
                assertEquals(10000L, s.preparation!!.deadlineMillis)
                assertEquals(50, s.preparation!!.availablePoints)
                assertTrue(s.view.myAllClosedTiles.isEmpty())
                assertTrue(s.view.doraIndicators.isEmpty())
                assertTrue(a.decision().await().options.isEmpty())
                assertTrue(s.clocks.none { it.deadline > it.opensAt })
                assertEquals(ReceiptStatus.RECORDED, a.submitShufflePlan(request(a, "skip-${a.seat}", ShuffleSelection())).await().status)
            }
            clock.now = 9999
            h.tick().await()
            seats[0].start().await()
            assertEquals(10000L, seats[0].snapshot().await().preparation!!.deadlineMillis)
            assertTrue(seats[0].snapshot().await().view.myAllClosedTiles.isEmpty())
            clock.now = 10000
            h.tick().await()
            val all = seats.map { it.snapshot().await() }
            assertTrue(all.all { it.preparation == null })
            assertEquals(listOf(14, 13, 13, 13), all.map { it.view.myAllClosedTiles.size })
            assertEquals(53, all.flatMap { it.view.myAllClosedTiles }.toSet().size)
            val timer = all[0].clocks.first { it.seat == 0 }
            assertEquals(20000L, timer.reserve)
            assertEquals(33000L, timer.deadline)
        }
    }

    @Test
    fun `cost is reserved privately then charged once with the successful deal`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = MatchConfig(winds = listOf(1), shuffleConfig = ShuffleConfig(enabled = true)),
            clock = clock,
            controllers = humans()
        ).use { h ->
            val a = TestSeat(0, h)
            val b = TestSeat(1, h)
            a.start().await()
            val req = request(a, "one", initial)
            val receipt = a.submitShufflePlan(req).await()
            assertEquals(ReceiptStatus.RECORDED, receipt.status)
            assertEquals(receipt, a.submitShufflePlan(req).await())
            val reserved = a.snapshot().await()
            assertEquals(50, reserved.cheatPoints[0])
            assertEquals(40, reserved.preparation!!.reservedPoints)
            assertEquals(10, reserved.preparation!!.availablePoints)
            assertEquals(initial, reserved.preparation!!.selection)
            assertFalse(b.snapshot().await().preparation!!.confirmed)
            assertNull(b.snapshot().await().preparation!!.selection)
            assertEquals(ReceiptStatus.REJECTED, a.submitShufflePlan(req.copy(selection = ShuffleSelection())).await().status)
            assertEquals(ReceiptStatus.REJECTED, a.submitShufflePlan(req.copy(requestId = "replace")).await().status)
            clock.now = 10000
            h.tick().await()
            assertEquals(10, a.snapshot().await().cheatPoints[0])
            assertEquals(receipt, a.submitShufflePlan(req).await())
            assertEquals(10, a.snapshot().await().cheatPoints[0])
        }
    }

    @Test
    fun `trusted ledger debit publishes private balance without refreshing deadline or advancing on retry`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = MatchConfig(
                cheatEconomy = CheatEconomyConfig(enabled = true, initialPoints = List(4) { 20 }),
                shuffleConfig = ShuffleConfig(enabled = false)
            ),
            clock = clock,
            controllers = humans()
        ).use { host ->
            val seat = TestSeat(0, host)
            assertEquals(ReceiptStatus.APPLIED, seat.start().await().status)
            val before = seat.snapshot().await()
            val oldDecision = seat.decision().await()
            val oldClock = before.clocks.first { it.seat == 0 }
            assertEquals(20, before.cheatPoints[0])

            val updates = CopyOnWriteArrayList<com.majiang.access.PlayerUpdate>()
            val baseline = CountDownLatch(1)
            val debitPushed = CountDownLatch(1)
            host.subscribeSeat(0) { update ->
                updates.add(update)
                if (update.snapshot.cheatPoints[0] == 20) baseline.countDown()
                if (update.snapshot.cheatPoints[0] == 13) debitPushed.countDown()
            }.use {
                assertTrue(baseline.await(3, TimeUnit.SECONDS))
                updates.clear()
                clock.now = 2000L

                val debit = host.applyCheatLedgerEntry("trusted-debit", 0, -7, "host-charge").await()
                assertTrue(debit is ActionResult.Accepted)
                assertTrue((debit as ActionResult.Accepted).events.any { it is GameEvent.CheatPointsChanged })
                assertTrue(debitPushed.await(3, TimeUnit.SECONDS), "Existing client subscription missed balance change")
                val pushed = updates.single()
                val after = seat.snapshot().await()
                val newClock = after.clocks.first { it.seat == 0 }
                assertEquals(13, pushed.snapshot.cheatPoints[0])
                assertEquals(after.cursor, pushed.snapshot.cursor)
                assertEquals(before.cursor + 1, after.cursor)
                assertEquals(oldClock.opensAt, newClock.opensAt)
                assertEquals(oldClock.deadline, newClock.deadline)
                assertEquals(newClock.deadline, pushed.snapshot.clocks.first { it.seat == 0 }.deadline)
                assertNotEquals(oldDecision.decisionId, seat.decision().await().decisionId)

                val duplicate = host.applyCheatLedgerEntry("trusted-debit", 0, -7, "host-charge").await()
                assertTrue(duplicate is ActionResult.Accepted)
                assertTrue((duplicate as ActionResult.Accepted).events.isEmpty())
                assertTrue(host.applyCheatLedgerEntry("insufficient-debit", 0, -14, "host-charge").await()
                    is ActionResult.Rejected)
                val stable = seat.snapshot().await()
                assertEquals(13, stable.cheatPoints[0])
                assertEquals(after.cursor, stable.cursor)
                assertEquals(after.roundVersion, stable.roundVersion)
                assertEquals(newClock.deadline, stable.clocks.first { it.seat == 0 }.deadline)

                val deliveryBarrier = CountDownLatch(1)
                host.subscribeSeat(0) { deliveryBarrier.countDown() }.use {
                    assertTrue(deliveryBarrier.await(3, TimeUnit.SECONDS))
                    assertEquals(1, updates.size, "Retry or rejected debit must not publish another update")
                }
            }
        }
    }

    @Test
    fun `insufficient funds rejects whole request while half game can buy both`() {
        for (south in listOf(false, true)) {
            val clock = TestClock(0L)
            ServerTableHost(
                config = MatchConfig(winds = if (south) listOf(1, 2) else listOf(1), shuffleConfig = ShuffleConfig(enabled = true)),
                clock = clock,
                controllers = humans()
            ).use { h ->
                val a = TestSeat(0, h)
                a.start().await()
                val reply = a.submitShufflePlan(request(a, "both", both)).await()
                assertEquals(if (south) ReceiptStatus.RECORDED else ReceiptStatus.REJECTED, reply.status)
                assertEquals(south, a.snapshot().await().preparation!!.confirmed)
                clock.now = 10000
                h.tick().await()
                assertEquals(if (south) 40 else 50, a.snapshot().await().cheatPoints[0])
            }
        }
    }

    @Test
    fun `deadline and pause reject late actions without charging and retain remaining preparation`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = MatchConfig(winds = listOf(1), shuffleConfig = ShuffleConfig(enabled = true)),
            clock = clock,
            controllers = humans()
        ).use { h ->
            val a = TestSeat(0, h)
            a.start().await()
            val req = request(a, "late", initial)
            clock.now = 2000
            h.pause().await()
            clock.now = 52000
            h.tick().await()
            assertTrue(a.snapshot().await().view.myAllClosedTiles.isEmpty())
            assertEquals(2000L, a.snapshot().await().preparation!!.serverNowMillis)
            assertEquals(ReceiptStatus.REJECTED, a.submitShufflePlan(req).await().status)
            h.resumeClock().await()
            assertEquals(60000L, a.snapshot().await().preparation!!.deadlineMillis)
            clock.now = 60000
            assertEquals(ReceiptStatus.REJECTED, a.submitShufflePlan(req.copy(requestId = "at-deadline")).await().status)
            h.tick().await()
            assertEquals(50, a.snapshot().await().cheatPoints[0])
            assertEquals(14, a.snapshot().await().view.myAllClosedTiles.size)
        }
    }

    @Test
    fun `old control epoch and old round are rejected and active game cannot bypass setup`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = MatchConfig(winds = listOf(1), shuffleConfig = ShuffleConfig(enabled = true)),
            clock = clock,
            controllers = humans()
        ).use { h ->
            val a = TestSeat(0, h)
            a.start().await()
            val req = request(a, "stale", initial)
            a.releaseControl().await()
            a.claimControl().await()
            assertEquals(ReceiptStatus.REJECTED, a.submitShufflePlan(req).await().status)
            assertEquals(ReceiptStatus.REJECTED, a.submitShufflePlan(request(a, "bad-round", initial).copy(roundId = "other")).await().status)
            assertEquals(ReceiptStatus.REJECTED, a.nextRound().await().status)
            assertTrue(a.decision().await().options.isEmpty())
            assertEquals(ReceiptStatus.REJECTED, a.submit(ActionChoice("forge", req.roundId, "fake", req.controlEpoch, "fake")).await().status)
            assertTrue(a.snapshot().await().view.myAllClosedTiles.isEmpty())
        }
    }

    @Test
    fun `optional AI spending applies to computer seats and never spends takeover human points`() {
        val clock = TestClock(0L)
        val cfg = MatchConfig(winds = listOf(1), shuffleConfig = ShuffleConfig(enabled = true, aiMaySpend = true))
        ServerTableHost(
            config = cfg,
            clock = clock,
            controllers = listOf(
                SeatController.LocalHuman(),
                SeatController.Computer(),
                SeatController.RemoteHuman(false),
                SeatController.Computer()
            )
        ).use { h ->
            val a = TestSeat(0, h)
            val human = TestSeat(2, h)
            a.start().await()
            human.releaseControl().await()
            h.tick().await()
            clock.now = 10000
            h.tick().await()
            val points = a.snapshot().await().cheatPoints
            assertEquals(50, points[0])
            assertEquals(50, points[2], "Takeover AI cannot silently spend a human budget")
            assertTrue(points[1] in 0 until 50)
            assertTrue(points[3] in 0 until 50)
        }
    }

    @Test
    fun `completed round hands remaining points to a fresh preparation without restoring budget`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = MatchConfig(winds = listOf(1), bankruptcyEnabled = false, shuffleConfig = ShuffleConfig(enabled = true)),
            clock = clock,
            controllers = humans()
        ).use { h ->
            val seats = (0..3).map { TestSeat(it, h) }
            val a = seats[0]
            a.start().await()
            val oldRound = a.snapshot().await().roundId
            a.submitShufflePlan(request(a, "spend", initial)).await()
            clock.now = 10000
            h.tick().await()
            var steps = 0
            while (a.queryRoundRecords().await().isEmpty() && steps++ < 500) {
                var progressed = false
                for (seat in seats) {
                    val snap = seat.snapshot().await()
                    val d = seat.decision().await()
                    val option = d.options.firstOrNull { it.action is GameAction.DeclareTsumo || it.action is GameAction.DeclareRon }
                        ?: d.options.firstOrNull { it.action is GameAction.Pass }
                        ?: d.options.firstOrNull { it.action is GameAction.DiscardTile && (it.action as GameAction.DiscardTile).tile == snap.view.myDrawnTile }
                        ?: d.options.firstOrNull { it.action is GameAction.DiscardTile }
                        ?: continue
                    val receipt = seat.submit(ActionChoice("play-$steps-${seat.viewerSeat}", d.roundId, d.decisionId, d.controlEpoch, option.optionId)).await()
                    assertNotEquals(ReceiptStatus.REJECTED, receipt.status)
                    progressed = true
                    if (a.queryRoundRecords().await().isNotEmpty()) break
                }
                assertTrue(progressed, "Round stalled before result")
            }
            assertEquals(1, a.queryRoundRecords().await().size)
            assertEquals(10, a.queryRoundRecords().await().single().finalCheatPoints[0])
            assertEquals(-40, a.queryRoundRecords().await().single().cheatDeltas[0])
            // All four controllers are human: preserve the existing all-ready gate.
            // A single RECORDED ready signal must NOT start the next round.
            for (seat in seats) {
                assertNotEquals(ReceiptStatus.REJECTED, seat.nextRound().await().status)
            }
            val next = a.snapshot().await()
            assertNotEquals(oldRound, next.roundId)
            assertNotNull(next.preparation)
            assertEquals(10, next.preparation!!.availablePoints)
            assertFalse(next.preparation!!.confirmed)
            assertTrue(next.view.myAllClosedTiles.isEmpty())
        }
    }

    @Test
    fun `disabled shuffle skips preparation directly to active round`() {
        val clock = TestClock(0L)
        ServerTableHost(
            config = MatchConfig(shuffleConfig = ShuffleConfig(enabled = false)),
            clock = clock,
            controllers = humans()
        ).use { h ->
            val a = TestSeat(0, h)
            a.start().await()
            val snap = a.snapshot().await()
            assertNull(snap.preparation)
            assertEquals(14, snap.view.myAllClosedTiles.size)
            assertTrue(snap.decision.options.isNotEmpty())
        }
    }
}
