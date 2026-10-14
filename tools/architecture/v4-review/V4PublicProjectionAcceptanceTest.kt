package com.majiang.v4lobby

import com.majiang.access.ClockView
import com.majiang.economy.CheatLedgerEntry
import com.majiang.economy.CheatLedgerSummary
import com.majiang.event.DrawReason
import com.majiang.event.GameEvent
import com.majiang.match.*
import com.majiang.model.*
import com.majiang.projection.PublicProjectionFactory
import com.majiang.rules.*
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import org.junit.jupiter.api.Test
import java.lang.reflect.Modifier
import kotlin.test.*

/** Independent PUBLIC domain probes: no player projection, mapper, or spectator transport shortcut. */
class V4PublicProjectionAcceptanceTest {
    private val visibleTile = Tile(Suit.MAN, 3)
    private val hiddenTile = Tile(Suit.SOU, 9)
    private val uraTile = Tile(Suit.DRAGON, 2)

    private fun session() = MatchSession(sessionId = "public-review").readState()

    private fun state() = GameState().apply {
        phase = GamePhase.DISCARD
        currentPlayer = 2
        dealerIndex = 1
        roundWind = 2
        roundNumber = 1
        honbaSticks = 2
        riichiSticks = 1
        wall.addAll(List(40) { Tile.fromTypeIndex(it % 34, it / 34) })
        wallPointer = 3
        wallEndExclusive = 36
        doraIndicators.add(visibleTile)
        uraDoraIndicators.add(uraTile)
        players.forEachIndexed { seat, hand ->
            hand.closedTiles.addAll(List(10 + seat) { Tile.fromTypeIndex(it, seat) })
            hand.discards.add(Tile(Suit.PIN, seat + 1))
            sleeveTiles[seat].add(Tile(Suit.WIND, seat + 1))
            cheatPoints[seat] = 10 + seat
        }
        players[2].drawnTile = hiddenTile
        players[0].melds.add(Meld(MeldType.PON,
            mutableListOf(visibleTile, visibleTile.copy(copyIndex = 1), visibleTile.copy(copyIndex = 2)),
            3, visibleTile))
        riichiDeclared[1] = true
        lastDiscard = players[1].discards.single()
        lastDiscardPlayer = 1
    }

    private fun project(state: GameState? = state(), snapshot: SessionSnapshot = session(),
                        events: List<GameEvent> = emptyList(), revealUra: Boolean = false) =
        PublicProjectionFactory.project(snapshot, state, if (state == null) null else "round-review",
            if (state == null) null else 7L, listOf(ClockView(2, 100L, 3100L, 2000L, 150L)), false,
            settlementRoundId = if (events.isEmpty()) null else "settled-round",
            settlementEvents = events, revealSettlementUra = revealUra)

    /** Compare public getters rather than assuming implementation-specific data-class equality. */
    private fun shape(value: Any?): Any? = when (value) {
        null, is String, is Number, is Boolean -> value
        is Enum<*> -> value.name
        is IntArray -> value.toList()
        is Iterable<*> -> value.map(::shape)
        is Map<*, *> -> value.entries.associate { shape(it.key) to shape(it.value) }
        else -> value.javaClass.methods.filter {
            Modifier.isPublic(it.modifiers) && !Modifier.isStatic(it.modifiers) &&
                it.parameterCount == 0 && it.name != "getClass" &&
                (it.name.startsWith("get") || it.name.startsWith("is"))
        }.sortedBy { it.name }.associate { it.name to shape(it.invoke(value)) }
    }

    @Test fun hiddenTilesWallOrderSleevesAndResourcesCannotInfluencePublicProjection() {
        val first = state()
        val second = first.deepCopy().apply {
            players.forEach { hand ->
                hand.closedTiles.replaceAll { it.copy(suit = Suit.PIN) }
            }
            players[2].drawnTile = Tile(Suit.MAN, 8, 3)
            sleeveTiles.forEach { it.clear(); it.add(hiddenTile) }
            wall.reverse()
            deadWall.add(hiddenTile)
            deadWallReplacements.add(uraTile)
            uraDoraIndicators.clear()
            uraDoraIndicators.add(hiddenTile)
            cheatPoints.fill(999)
            cheatEntries.add(CheatLedgerEntry("secret", 2, -7, 999, "private-technique"))
            sameTurnFuriten.fill(true)
            riichiFuriten.fill(true)
        }
        val before = project(first)
        val after = project(second, session().copy(betweenRoundCheatPoints = listOf(900, 901, 902, 903)))
        assertEquals(shape(before), shape(after), "Private identities and resource balances must not escape PUBLIC")
        val board = assertNotNull(before.board)
        assertEquals(listOf(10, 11, 13, 13), board.handTileCounts)
        assertEquals(33, board.wallRemaining)
        assertEquals(listOf(visibleTile), board.doraIndicators)
        assertEquals(2, board.currentTurnPlayer)
        assertEquals(1, board.dealerSeat)
        assertEquals(listOf(false, true, false, false), board.riichiStatus)
        assertEquals("public-review", before.sessionId)
        assertEquals(7L, before.roundVersion)
    }

    @Test fun preparationAndFinishedSessionsWithNoRoundDoNotInventABoard() {
        val lobby = project(null)
        assertNull(lobby.board)
        assertNull(lobby.roundId)
        assertNull(lobby.roundVersion)
        assertEquals(SessionLifecycle.CREATED.name, lobby.session.lifecycle)
        val finished = project(null, finishedSession())
        assertNull(finished.board)
        assertEquals(SessionLifecycle.FINISHED.name, finished.session.lifecycle)
        assertEquals(listOf(33000, 17000, 25000, 25000), finished.session.scores)
        assertEquals("record-1", finished.session.roundRecords.single().roundId)
        assertEquals("result-1", finished.session.roundRecords.single().resultId)
        assertEquals(listOf(8000, -8000, 0, 0), finished.session.roundRecords.single().deltas)
        val result = assertNotNull(finished.session.finalResult)
        assertEquals(listOf(0, 2, 3, 1), result.rankedSeats)
        assertEquals(MatchFinishReason.NORMAL_COMPLETION.name, result.reason)
    }

    private fun finishedSession(secret: Int = 40): SessionSnapshot {
        val scores = mutableListOf(33000, 17000, 25000, 25000)
        val privateEntry = CheatLedgerEntry("private-$secret", 0, -5, secret, "legacy-cost-$secret")
        val records = mutableListOf(RoundRecord("record-1", "result-1", RoundOutcome.Ron(0, 1),
            scores, mutableListOf(8000, -8000, 0, 0), 0,
            List(4) { secret + it }, List(4) { -secret }, listOf(privateEntry)))
        val result = SessionResult("public-review", scores,
            mutableListOf(RankingEntry(1, 0, 33000), RankingEntry(2, 2, 25000),
                RankingEntry(3, 3, 25000), RankingEntry(4, 1, 17000)),
            MatchFinishReason.NORMAL_COMPLETION, finalCheatPoints = List(4) { secret },
            cheatSummary = CheatLedgerSummary(List(4) { secret + 5 }, List(4) { secret },
                List(4) { -5 }, listOf(privateEntry), emptyList()))
        return session().copy(version = 20L, lifecycle = SessionLifecycle.FINISHED,
            betweenRoundScores = scores, roundRecords = records, result = result,
            betweenRoundCheatPoints = List(4) { secret })
    }

    @Test fun publicHistoryAndFinalResultExcludeCheatBalancesDeltasAndLedgerEntries() {
        val first = project(null, finishedSession(40))
        val second = project(null, finishedSession(700))
        assertEquals(shape(first), shape(second), "Changing only private history must have no public effect")
        val forbidden = Regex("(?i)cheat|sleeve|decision|option|preview|ura|closedTile|drawnTile|wallOrder")
        fun inspect(value: Any?) {
            when (value) {
                is Map<*, *> -> value.forEach { (key, item) ->
                    assertFalse(forbidden.containsMatchIn(key.toString()), "Private public-model getter: $key")
                    inspect(item)
                }
                is Iterable<*> -> value.forEach(::inspect)
            }
        }
        inspect(shape(first))
    }

    @Test fun privateEventTypesAreDeniedAndPublicDiscardMeldScoreAreRetained() {
        val meld = state().players[0].melds.single()
        val allowed = listOf(
            GameEvent.StackingStarted(), GameEvent.WallBuilt(), GameEvent.DealingComplete(),
            GameEvent.TileDiscarded(3, 0, visibleTile), GameEvent.ChiDeclared(3, 0, meld, 1),
            GameEvent.PonDeclared(3, 0, meld, 1), GameEvent.KanDeclared(3, 0, meld, MeldType.OPEN_KAN),
            GameEvent.RiichiDeclared(3, 0, visibleTile),
            GameEvent.DoraRevealed(3, visibleTile),
            GameEvent.DrawGame(3, DrawReason.EXHAUSTIVE, listOf(0, 2)),
            GameEvent.ScoreSettled(3, mapOf(0 to 1500, 1 to -1500), intArrayOf(26500, 23500, 25000, 25000)),
            GameEvent.RoundEnded(3, 1, 0), GameEvent.MatchEnded(3, intArrayOf(33000, 17000, 25000, 25000), 0))
        val denied = listOf(
            GameEvent.TileDealt(3, 0, hiddenTile), GameEvent.TileDrawn(3, 1, hiddenTile, 20),
            GameEvent.RinshanDraw(3, 2, hiddenTile),
            GameEvent.CheatPointsChanged(3, 0, -7, 33),
            GameEvent.DoraRevealed(3, uraTile, true))
        assertEquals(shape(allowed), shape(PublicProjectionFactory.publicEvents(allowed + denied)))
        assertTrue(PublicProjectionFactory.publicEvents(denied).isEmpty())
    }

    @Test fun roundEndDoesNotAuthorizeUraWithoutExplicitSettlementPermission() {
        val ura = GameEvent.DoraRevealed(4, uraTile, true)
        val terminal = GameEvent.RoundEnded(4, 1, 0)
        assertEquals(listOf(terminal), PublicProjectionFactory.publicEvents(listOf(ura, terminal)))
        assertEquals(listOf(ura, terminal),
            PublicProjectionFactory.publicEvents(listOf(ura, terminal), revealSettlementUra = true))
        val state = state().apply { phase = GamePhase.ROUND_END }
        val withoutPublishedEvent = project(state, revealUra = true)
        assertTrue(withoutPublishedEvent.settlementEvents.isEmpty(), "Permission cannot invent ura from GameState")
        assertEquals(listOf(visibleTile), assertNotNull(withoutPublishedEvent.board).doraIndicators)
        assertEquals(listOf(terminal), project(state, events = listOf(ura, terminal)).settlementEvents)
        assertEquals(listOf(ura, terminal), project(state, events = listOf(ura, terminal), revealUra = true).settlementEvents)
    }

    @Test fun boardAndSessionCollectionsAreDetachedAndRejectNestedMutations() {
        val state = state()
        val snapshot = finishedSession()
        val output = project(state, snapshot)
        val originalShape = shape(output)
        state.scores.fill(-1)
        state.doraIndicators.clear()
        state.players[0].discards.clear()
        (state.players[0].melds[0].tiles as MutableList<Tile>).clear()
        (snapshot.betweenRoundScores as MutableList<Int>)[0] = 1
        (snapshot.roundRecords as MutableList<RoundRecord>).clear()
        (snapshot.result!!.rankings as MutableList<RankingEntry>).clear()
        assertEquals(originalShape, shape(output))
        val board = assertNotNull(output.board)
        immutable(board.scores); immutable(board.riichiStatus); immutable(board.handTileCounts)
        immutable(board.doraIndicators); immutable(board.discards); immutable(board.discards[1])
        immutable(board.melds); immutable(board.melds[0]); immutable(board.melds[0][0].tiles)
        immutable(output.clocks); immutable(output.session.scores); immutable(output.session.roundRecords)
        immutable(output.session.roundRecords[0].finalScores); immutable(output.session.roundRecords[0].deltas)
        immutable(output.session.finalResult!!.finalScores); immutable(output.session.finalResult!!.rankedSeats)
        assertEquals(originalShape, shape(output))
    }

    private fun <T> immutable(list: List<T>) {
        assertTrue(list.isNotEmpty(), "Mutation probe must exercise a nonempty collection")
        val original = shape(list)
        val error = runCatching { (list as MutableList<T>).clear() }.exceptionOrNull()
        assertTrue(error is UnsupportedOperationException || error is ClassCastException,
            "Readonly list must reject the mutable cast or mutation; got $error")
        assertEquals(original, shape(list), "Rejected mutation must not change retained contents")
    }

    @Test fun exhaustiveDrawOutcomeCopiesAndProtectsNestedTenpaiSeatSet() {
        val sourceSeats = linkedSetOf(0, 2)
        val snapshot = finishedSession().let { finished ->
            finished.copy(roundRecords = listOf(finished.roundRecords.single().copy(
                outcome = RoundOutcome.ExhaustiveDraw(sourceSeats))))
        }
        val output = project(null, snapshot)
        val expected = setOf(0, 2)
        val outcome = assertIs<RoundOutcome.ExhaustiveDraw>(output.session.roundRecords.single().outcome)
        assertEquals(expected, outcome.tenpaiSeats)
        sourceSeats.clear()
        sourceSeats.add(3)
        assertEquals(expected, outcome.tenpaiSeats,
            "PublicScoreRecord must not retain RoundOutcome.ExhaustiveDraw's mutable source set")
        val before = shape(output)
        val error = runCatching { (outcome.tenpaiSeats as MutableSet<Int>).clear() }.exceptionOrNull()
        assertTrue(error is UnsupportedOperationException || error is ClassCastException,
            "Public history outcome must reject nested set mutation; got $error")
        assertEquals(expected, outcome.tenpaiSeats)
        assertEquals(before, shape(output))
    }

    @Test fun publishedScoreBreakdownKeepsRealDetailWithoutMutableAliases() {
        val hitTiles = mutableListOf(visibleTile)
        val yaku = mutableListOf("Riichi" to 1)
        val fuLines = mutableListOf(FuLine("Closed ron", 30))
        val paymentLines = mutableListOf("Seat 1 pays 3900")
        val breakdown = ScoreBreakdown(yaku, 0,
            DoraBreakdown(1, 1, 0, indicatorTiles = hitTiles, uraTiles = mutableListOf(hiddenTile),
                indicatorHits = mutableListOf(DoraIndicatorHit(Tile(Suit.MAN, 2), visibleTile, hitTiles)),
                uraHits = mutableListOf(DoraIndicatorHit(Tile(Suit.SOU, 8), hiddenTile, mutableListOf(hiddenTile)))),
            FuBreakdown(fuLines, 30, 30, 30), ScoreCalculator.INSTANCE.calculate(30, 3),
            paymentLines, 0, 0, 3900, uraEligible = true)
        val yakuNames = mutableListOf("Riichi")
        val ron = GameEvent.RonWin(5, 0, 1, visibleTile, yakuNames, 3, 30, 3900, breakdown)
        val tsumo = GameEvent.TsumoWin(5, 0, visibleTile, yakuNames, 3, 30, 4000, breakdown)
        val output = project(events = listOf(ron, tsumo), revealUra = true)
        val copy = output.settlementEvents[0] as GameEvent.RonWin
        assertEquals(shape(breakdown), shape(copy.breakdown))
        assertIs<GameEvent.TsumoWin>(output.settlementEvents[1])
        val saved = shape(output)
        hitTiles.clear(); yaku.clear(); fuLines.clear(); paymentLines.clear(); yakuNames.clear()
        assertEquals(saved, shape(output), "Published scoring detail must survive source mutation")
        immutable(output.settlementEvents); immutable(copy.yakuList)
        val detail = assertNotNull(copy.breakdown)
        immutable(detail.yakuLines); immutable(detail.paymentLines); immutable(detail.fu.lines)
        immutable(detail.dora.indicatorTiles); immutable(detail.dora.uraTiles)
        immutable(detail.dora.indicatorHits); immutable(detail.dora.indicatorHits[0].tiles)
        immutable(detail.dora.uraHits); immutable(detail.dora.uraHits[0].tiles)
    }

    @Test fun winningBreakdownsCannotBypassExplicitUraPermissionThroughNestedEntities() {
        fun scoreWithPrivateUra(indicator: Tile, hit: Tile) = ScoreBreakdown(
            yakuLines = listOf("Riichi" to 1), yakumanCount = 0,
            dora = DoraBreakdown(1, 1, 0,
                indicatorTiles = listOf(visibleTile), uraTiles = listOf(hit),
                indicatorHits = listOf(DoraIndicatorHit(Tile(Suit.MAN, 2), visibleTile, listOf(visibleTile))),
                uraHits = listOf(DoraIndicatorHit(indicator, hit, listOf(hit)))),
            fu = FuBreakdown(listOf(FuLine("Closed ron", 30)), 30, 30, 30),
            score = ScoreCalculator.INSTANCE.calculate(30, 3),
            paymentLines = listOf("Seat 1 pays 3900"), honbaAddition = 0,
            riichiAddition = 0, totalPoints = 3900, uraEligible = true)

        fun wins(detail: ScoreBreakdown): List<GameEvent> = listOf(
            GameEvent.RonWin(6, 0, 1, visibleTile, listOf("Riichi"), 3, 30, 3900, detail),
            GameEvent.TsumoWin(6, 0, visibleTile, listOf("Riichi"), 3, 30, 4000, detail))

        fun details(events: List<GameEvent>) = events.mapNotNull { event ->
            when (event) {
                is GameEvent.RonWin -> assertNotNull(event.breakdown)
                is GameEvent.TsumoWin -> assertNotNull(event.breakdown)
                else -> null
            }
        }

        val secretA = scoreWithPrivateUra(Tile(Suit.SOU, 8), hiddenTile)
        val secretB = scoreWithPrivateUra(Tile(Suit.PIN, 6), Tile(Suit.PIN, 7))
        val terminal = GameEvent.RoundEnded(6, 1, 0)
        val inputA = wins(secretA) + terminal
        val inputB = wins(secretB) + terminal
        val denied = PublicProjectionFactory.publicEvents(inputA)
        assertEquals(2, details(denied).size, "Privacy filtering must preserve both public win events")
        details(denied).forEach { detail ->
            assertTrue(detail.dora.uraTiles.isEmpty(), "uraEligible does not authorize publishing ura tile identities")
            assertTrue(detail.dora.uraHits.isEmpty(), "Nested ura indicators cannot bypass the explicit permission")
            assertEquals(secretA.yakuLines, detail.yakuLines)
            assertEquals(secretA.fu, detail.fu)
            assertEquals(secretA.score, detail.score)
            assertEquals(secretA.paymentLines, detail.paymentLines)
            assertEquals(secretA.totalPoints, detail.totalPoints)
            assertEquals(secretA.dora.indicatorHits, detail.dora.indicatorHits)
            assertEquals(secretA.dora.indicatorTiles, detail.dora.indicatorTiles)
        }
        assertEquals(shape(denied), shape(PublicProjectionFactory.publicEvents(inputB)),
            "Changing only unrevealed ura entities must have no public effect")
        assertEquals(shape(denied), shape(project(events = inputA).settlementEvents),
            "The settlement snapshot must enforce the same nested privacy rule as publicEvents")

        val authorized = PublicProjectionFactory.publicEvents(inputA, revealSettlementUra = true)
        assertEquals(shape(inputA), shape(authorized), "Explicit public settlement authorization preserves full scoring detail")
        assertEquals(shape(inputA), shape(project(events = inputA, revealUra = true).settlementEvents))
        assertEquals(listOf(hiddenTile), details(authorized).first().dora.uraTiles)
        assertEquals(Tile(Suit.SOU, 8), details(authorized).last().dora.uraHits.single().indicator)
    }

    @Test fun publishedScoreArraysCannotMutateRetainedEventsThroughGetters() {
        val scores = intArrayOf(33000, 17000, 25000, 25000)
        val payments = mutableMapOf(0 to 8000, 1 to -8000)
        val source = mutableListOf<GameEvent>(GameEvent.ScoreSettled(5, payments, scores),
            GameEvent.MatchEnded(5, scores, 0))
        val events = PublicProjectionFactory.publicEvents(source)
        val projection = project(events = source)
        scores.fill(0); payments.clear(); source.clear()
        val expected = listOf(33000, 17000, 25000, 25000)
        assertEquals(expected, (events[0] as GameEvent.ScoreSettled).newScores.toList())
        assertEquals(mapOf(0 to 8000, 1 to -8000), (events[0] as GameEvent.ScoreSettled).payments)
        assertFailsWith<UnsupportedOperationException> {
            ((events[0] as GameEvent.ScoreSettled).payments as MutableMap<Int, Int>).clear()
        }
        (events[0] as GameEvent.ScoreSettled).newScores.fill(-999)
        (events[1] as GameEvent.MatchEnded).finalScores.fill(-999)
        assertEquals(expected, (events[0] as GameEvent.ScoreSettled).newScores.toList())
        assertEquals(expected, (events[1] as GameEvent.MatchEnded).finalScores.toList())
        (projection.settlementEvents[0] as GameEvent.ScoreSettled).newScores.fill(-999)
        (projection.settlementEvents[1] as GameEvent.MatchEnded).finalScores.fill(-999)
        assertEquals(expected, (projection.settlementEvents[0] as GameEvent.ScoreSettled).newScores.toList())
        assertEquals(expected, (projection.settlementEvents[1] as GameEvent.MatchEnded).finalScores.toList())
    }

    @Test fun eventListEqualityIsSymmetricAndProjectionCopiesPreserveValuesWithEagerIteratorBounds() {
        val source = listOf<GameEvent>(
            GameEvent.ScoreSettled(7, mapOf(0 to 8000, 1 to -8000), intArrayOf(33000, 17000, 25000, 25000)),
            GameEvent.MatchEnded(7, intArrayOf(33000, 17000, 25000, 25000), 0))
        val publicList = PublicProjectionFactory.publicEvents(source)
        val ordinaryList = publicList.toList()
        assertEquals(publicList == ordinaryList, ordinaryList == publicList,
            "List equality must be symmetric even when getter snapshots contain arrays")

        val independentList = PublicProjectionFactory.publicEvents(source.map { event ->
            when (event) {
                is GameEvent.ScoreSettled -> event.copy(newScores = event.newScores.clone())
                is GameEvent.MatchEnded -> event.copy(finalScores = event.finalScores.clone())
                else -> event
            }
        })
        assertEquals(publicList, independentList, "Independent public event lists preserve event value equality")
        assertEquals(independentList, publicList)
        assertEquals(publicList.hashCode(), independentList.hashCode())

        val projection = project(events = source)
        val rebuilt = project(events = source)
        for (copy in listOf(rebuilt, projection.copy(), projection.deepCopy())) {
            assertEquals(projection, copy, "Projection rebuild/copy/deepCopy must preserve public values")
            assertEquals(copy, projection)
            assertEquals(projection.hashCode(), copy.hashCode())
        }

        assertFailsWith<IndexOutOfBoundsException> { publicList.listIterator(-1) }
        assertFailsWith<IndexOutOfBoundsException> { publicList.listIterator(publicList.size + 1) }
        val beginning = publicList.listIterator(0)
        assertFalse(beginning.hasPrevious())
        assertTrue(beginning.hasNext())
        val end = publicList.listIterator(publicList.size)
        assertFalse(end.hasNext())
        assertTrue(end.hasPrevious())
        assertIs<GameEvent.MatchEnded>(end.previous())
    }
}
