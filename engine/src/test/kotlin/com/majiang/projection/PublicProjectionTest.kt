package com.majiang.projection

import com.majiang.access.ClockView
import com.majiang.event.DrawReason
import com.majiang.event.GameEvent
import com.majiang.match.MatchFinishReason
import com.majiang.match.MatchRuleConfig
import com.majiang.match.RankingEntry
import com.majiang.match.RoundOutcome
import com.majiang.match.RoundRecord
import com.majiang.match.SessionLifecycle
import com.majiang.match.SessionResult
import com.majiang.match.SessionSnapshot
import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.rules.DoraBreakdown
import com.majiang.rules.DoraIndicatorHit
import com.majiang.rules.FuBreakdown
import com.majiang.rules.FuLine
import com.majiang.rules.ScoreBreakdown
import com.majiang.rules.ScoreCalculator
import com.majiang.state.GamePhase
import com.majiang.state.GameState
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import kotlin.test.*

class PublicProjectionTest {

    private fun sampleTile(rank: Int, suit: Suit = Suit.MAN, copy: Int = 0): Tile = Tile(suit, rank, copy)

    private fun sampleSessionSnapshot(
        lifecycle: SessionLifecycle = SessionLifecycle.ROUND_ACTIVE,
        roundRecords: List<RoundRecord> = emptyList(),
        result: SessionResult? = null,
        scores: List<Int> = listOf(25000, 25000, 25000, 25000)
    ): SessionSnapshot = SessionSnapshot(
        sessionId = "test-session-1",
        version = 42L,
        lifecycle = lifecycle,
        ruleConfig = MatchRuleConfig(),
        currentDealer = 1,
        currentRoundWind = 2,
        currentRoundNumber = 3,
        currentHonba = 2,
        betweenRoundScores = scores,
        betweenRoundRiichiSticks = 1,
        activeRoundId = "round-1",
        roundRecords = roundRecords,
        canDealerDeclareAgariYame = false,
        result = result
    )

    private fun sampleScoreBreakdown(
        uraEligible: Boolean = true,
        uraTiles: List<Tile> = listOf(sampleTile(1, Suit.PIN)),
        uraHits: List<DoraIndicatorHit> = listOf(
            DoraIndicatorHit(sampleTile(1, Suit.PIN), sampleTile(2, Suit.PIN), listOf(sampleTile(2, Suit.PIN)))
        )
    ): ScoreBreakdown {
        val fuLines = listOf(
            FuLine("底符", 20),
            FuLine("门清加符", 10)
        )
        val fuBreakdown = FuBreakdown(
            lines = fuLines,
            subtotal = 30,
            roundedUpTo = 30,
            finalFu = 30,
            specialNote = null
        )
        val scoreResult = ScoreCalculator.INSTANCE.calculate(fu = 30, han = 3)
        return ScoreBreakdown(
            yakuLines = listOf("立直" to 1, "断幺九" to 1),
            yakumanCount = 0,
            dora = DoraBreakdown(
                indicatorCount = 1,
                uraCount = if (uraTiles.isNotEmpty()) 1 else 0,
                redCount = 0,
                totalHan = 1 + if (uraTiles.isNotEmpty()) 1 else 0,
                indicatorTiles = listOf(sampleTile(5, Suit.MAN)),
                uraTiles = uraTiles,
                redTiles = emptyList(),
                indicatorHits = listOf(DoraIndicatorHit(sampleTile(4, Suit.MAN), sampleTile(5, Suit.MAN), listOf(sampleTile(5, Suit.MAN)))),
                uraHits = uraHits
            ),
            fu = fuBreakdown,
            score = scoreResult,
            paymentLines = listOf("自摸 庄家2000, 闲家1000"),
            honbaAddition = 600,
            riichiAddition = 1000,
            totalPoints = scoreResult.totalPoints(isDealer = false, isTsumo = true) + 600 + 1000,
            uraEligible = uraEligible,
            isDealer = false,
            isTsumo = true
        )
    }

    // ========================================
    // 1. 准备阶段与暗牌/牌山信息隐匿
    // ========================================

    @Test
    fun preparationPhaseHidesPrivateWallOrderAndClosedTiles() {
        val session = sampleSessionSnapshot()
        val state1 = GameState().apply {
            phase = GamePhase.STACKING
            dealerIndex = 1
            currentPlayer = 1
            roundWind = 2
            roundNumber = 3
            honbaSticks = 2
            riichiSticks = 1
            wall.addAll((1..9).map { sampleTile(it) })
            wallPointer = 0
            wallEndExclusive = 9
            players[0].closedTiles.addAll(listOf(sampleTile(1), sampleTile(2)))
            players[1].closedTiles.addAll(listOf(sampleTile(3)))
            players[1].drawnTile = sampleTile(4)
            sleeveTiles[0].add(sampleTile(9))
            cheatPoints[0] = 50
        }

        val state2 = GameState().apply {
            phase = GamePhase.STACKING
            dealerIndex = 1
            currentPlayer = 1
            roundWind = 2
            roundNumber = 3
            honbaSticks = 2
            riichiSticks = 1
            // 牌山顺序完全不同
            wall.addAll((9 downTo 1).map { sampleTile(it) })
            wallPointer = 0
            wallEndExclusive = 9
            // 暗牌牌面完全不同，但数量保持相同 (0号2张，1号2张包含drawnTile)
            players[0].closedTiles.addAll(listOf(sampleTile(7, Suit.PIN), sampleTile(8, Suit.PIN)))
            players[1].closedTiles.addAll(listOf(sampleTile(9, Suit.SOU)))
            players[1].drawnTile = sampleTile(8, Suit.SOU)
            // 袖牌与出千点数不同
            sleeveTiles[0].add(sampleTile(1, Suit.DRAGON))
            cheatPoints[0] = 100
        }

        val proj1 = PublicProjectionFactory.project(session, state1, "round-1", 1L, emptyList(), false)
        val proj2 = PublicProjectionFactory.project(session, state2, "round-1", 1L, emptyList(), false)

        assertNotNull(proj1.board)
        assertNotNull(proj2.board)
        assertEquals(proj1.board, proj2.board, "Changing private tiles, wall order, sleeves, and cheat points must not alter public board")
        assertEquals(listOf(2, 2, 0, 0), proj1.board?.handTileCounts)
    }

    // ========================================
    // 2. 局中投影与里宝牌隔离
    // ========================================

    @Test
    fun activeRoundOmitsUraDoraAndIncludesDiscardsAndMelds() {
        val session = sampleSessionSnapshot()
        val frontDora = sampleTile(1, Suit.PIN)
        val secretUra = sampleTile(9, Suit.PIN)
        val meldTile = sampleTile(2, Suit.SOU)
        val discardTile = sampleTile(3, Suit.MAN)

        val state = GameState().apply {
            phase = GamePhase.DISCARD
            dealerIndex = 0
            currentPlayer = 0
            doraIndicators.add(frontDora)
            uraDoraIndicators.add(secretUra)
            players[0].discards.add(discardTile)
            players[1].melds.add(Meld(MeldType.PON, listOf(meldTile, meldTile.copy(copyIndex = 1), meldTile.copy(copyIndex = 2)), 0, meldTile))
            lastDiscard = discardTile
            lastDiscardPlayer = 0
            riichiDeclared[0] = true
        }

        val proj = PublicProjectionFactory.project(session, state, "round-1", 5L, emptyList(), false)
        val board = assertNotNull(proj.board)

        assertEquals(listOf(frontDora), board.doraIndicators, "Only public dora indicators must be present")
        assertFalse(board.doraIndicators.contains(secretUra), "Secret ura dora must never leak into public board dora indicators")
        assertEquals(discardTile, board.lastDiscard)
        assertEquals(0, board.lastDiscardPlayer)
        assertTrue(board.riichiStatus[0])
        assertEquals(1, board.discards[0].size)
        assertEquals(1, board.melds[1].size)
    }

    // ========================================
    // 3. 局间与 state == null 边界
    // ========================================

    @Test
    fun nullStateYieldsNullBoardAndDrawsFromSessionSnapshot() {
        val session = sampleSessionSnapshot(
            lifecycle = SessionLifecycle.ROUND_RESULT,
            scores = listOf(30000, 20000, 25000, 25000)
        )

        val proj = PublicProjectionFactory.project(session, null, null, null, emptyList(), false)

        assertNull(proj.board, "Board must be null when state is null (no fake table)")
        assertEquals("ROUND_RESULT", proj.session.lifecycle)
        assertEquals(listOf(30000, 20000, 25000, 25000), proj.session.scores)
        assertEquals(1, proj.session.dealerSeat)
        assertEquals(2, proj.session.roundWind)
        assertEquals(3, proj.session.roundNumber)
        assertEquals(2, proj.session.honba)
    }

    // ========================================
    // 4. 终局结果与单局历史去私密化
    // ========================================

    @Test
    fun sessionSummaryOmitsCheatPointsFromRecordsAndFinalResult() {
        val record = RoundRecord(
            roundId = "r-0",
            resultId = "res-0",
            outcome = RoundOutcome.DealerWin(0),
            finalScores = listOf(29000, 23000, 24000, 24000),
            deltas = listOf(4000, -2000, -1000, -1000),
            remainingRiichiSticks = 0,
            finalCheatPoints = listOf(10, 20, 30, 40),
            cheatDeltas = listOf(-5, 0, 5, 0)
        )
        val result = SessionResult(
            sessionId = "test-session-1",
            finalScores = listOf(35000, 20000, 25000, 20000),
            rankings = listOf(
                RankingEntry(1, 0, 35000),
                RankingEntry(2, 2, 25000),
                RankingEntry(3, 1, 20000),
                RankingEntry(4, 3, 20000)
            ),
            reason = MatchFinishReason.NORMAL_COMPLETION,
            remainingRiichiSticksTransferredTo = 0,
            finalCheatPoints = listOf(50, 0, 0, 0)
        )

        val session = sampleSessionSnapshot(
            lifecycle = SessionLifecycle.FINISHED,
            roundRecords = listOf(record),
            result = result,
            scores = listOf(35000, 20000, 25000, 20000)
        )

        val proj = PublicProjectionFactory.project(session, null, null, null, emptyList(), false)

        assertEquals("FINISHED", proj.session.lifecycle)
        assertEquals(1, proj.session.roundRecords.size)
        val publicRecord = proj.session.roundRecords[0]
        assertEquals("r-0", publicRecord.roundId)
        assertEquals("res-0", publicRecord.resultId)
        assertEquals(listOf(29000, 23000, 24000, 24000), publicRecord.finalScores)
        assertEquals(listOf(4000, -2000, -1000, -1000), publicRecord.deltas)

        val publicResult = assertNotNull(proj.session.finalResult)
        assertEquals(listOf(35000, 20000, 25000, 20000), publicResult.finalScores)
        assertEquals(listOf(0, 2, 1, 3), publicResult.rankedSeats)
        assertEquals("NORMAL_COMPLETION", publicResult.reason)
        assertEquals(0, publicResult.remainingRiichiSticksTransferredTo)

        // 强反射审计：禁止声明出千字段
        val recordFields = PublicScoreRecord::class.java.declaredFields.map { it.name }
        assertFalse(recordFields.any { it.contains("cheat", ignoreCase = true) }, "PublicScoreRecord must not leak cheat fields")
        val matchResultFields = PublicMatchResult::class.java.declaredFields.map { it.name }
        assertFalse(matchResultFields.any { it.contains("cheat", ignoreCase = true) }, "PublicMatchResult must not leak cheat fields")
    }

    // ========================================
    // 5. 荒牌流局嵌套 Set 防御性复制
    // ========================================

    @Test
    fun exhaustiveDrawOutcomeDefensivelyCopiesTenpaiSeats() {
        val mutableSeats = mutableSetOf(0, 2)
        val outcome = RoundOutcome.ExhaustiveDraw(mutableSeats)
        val record = PublicScoreRecord(
            roundId = "r-draw",
            resultId = "res-draw",
            finalScores = listOf(25000, 25000, 25000, 25000),
            deltas = listOf(0, 0, 0, 0),
            outcome = outcome
        )

        // 修改输入 mutableSet
        mutableSeats.add(1)
        val publicOutcome = assertNotNull(record.outcome) as RoundOutcome.ExhaustiveDraw
        assertEquals(setOf(0, 2), publicOutcome.tenpaiSeats, "ExhaustiveDraw.tenpaiSeats must not be mutated via caller set")

        // 尝试通过 getter 修改内部 set
        assertThrows<UnsupportedOperationException> {
            (publicOutcome.tenpaiSeats as MutableSet<Int>).add(3)
        }

        // deepCopy 依然独立
        val copiedRecord = record.deepCopy()
        assertEquals(record, copiedRecord)
    }

    // ========================================
    // 6. 事件白名单过滤机制
    // ========================================

    @Test
    fun publicEventsDropsPrivateEventsAndRetainsPublicEvents() {
        val privateEvents = listOf(
            GameEvent.TileDealt(0, 0, sampleTile(1)),
            GameEvent.TileDrawn(1, 0, sampleTile(2), 60),
            GameEvent.RinshanDraw(2, 0, sampleTile(3)),
            GameEvent.CheatPointsChanged(7, 0, -20, 30)
        )

        val publicEvents = listOf(
            GameEvent.StackingStarted(0),
            GameEvent.WallBuilt(0, 136),
            GameEvent.DealingComplete(0),
            GameEvent.TileDiscarded(1, 0, sampleTile(1)),
            GameEvent.ChiDeclared(2, 1, Meld(MeldType.CHI, listOf(sampleTile(1), sampleTile(2), sampleTile(3)), 0, sampleTile(1)), 0),
            GameEvent.PonDeclared(3, 2, Meld(MeldType.PON, listOf(sampleTile(4), sampleTile(4), sampleTile(4)), 1, sampleTile(4)), 1),
            GameEvent.KanDeclared(4, 3, Meld(MeldType.OPEN_KAN, listOf(sampleTile(5), sampleTile(5), sampleTile(5), sampleTile(5)), 2, sampleTile(5)), MeldType.OPEN_KAN),
            GameEvent.RiichiDeclared(5, 0, sampleTile(9)),
            GameEvent.DrawGame(7, DrawReason.EXHAUSTIVE, listOf(0, 1)),
            GameEvent.ScoreSettled(8, mapOf(0 to 1000), intArrayOf(26000, 24000, 25000, 25000)),
            GameEvent.RoundEnded(9, 1, 0),
            GameEvent.MatchEnded(10, intArrayOf(26000, 24000, 25000, 25000), 0)
        )

        val mixed = privateEvents + publicEvents
        val filtered = PublicProjectionFactory.publicEvents(mixed, revealSettlementUra = false)

        assertEquals(publicEvents.size, filtered.size)
        for (event in privateEvents) {
            assertFalse(filtered.any { it::class == event::class }, "Private event ${event::class.simpleName} must be dropped")
        }
        for (event in publicEvents) {
            assertTrue(filtered.any { it::class == event::class }, "Public event ${event::class.simpleName} must be retained")
        }
    }

    // ========================================
    // 7. 里宝牌授权与深层 Breakdown 剥除
    // ========================================

    @Test
    fun uraDoraRevealedOnlyWhenExplicitlyAuthorized() {
        val frontDora = GameEvent.DoraRevealed(1, sampleTile(1, Suit.PIN), isUraDora = false)
        val uraDora = GameEvent.DoraRevealed(2, sampleTile(9, Suit.PIN), isUraDora = true)
        val events = listOf(frontDora, uraDora, GameEvent.RoundEnded(3, 1, 0))

        val unauthorized = PublicProjectionFactory.publicEvents(events, revealSettlementUra = false)
        assertEquals(listOf(frontDora, GameEvent.RoundEnded(3, 1, 0)), unauthorized,
            "RoundEnded cannot authorize ura dora alone without revealSettlementUra=true")

        val authorized = PublicProjectionFactory.publicEvents(events, revealSettlementUra = true)
        assertEquals(listOf(frontDora, uraDora, GameEvent.RoundEnded(3, 1, 0)), authorized,
            "revealSettlementUra=true must preserve ura dora event")
    }

    @Test
    fun deepBreakdownUraDetailsStrippedWhenUnauthorizedAndPreservedWhenAuthorized() {
        val breakdown = sampleScoreBreakdown(
            uraEligible = true,
            uraTiles = listOf(sampleTile(1, Suit.PIN)),
            uraHits = listOf(DoraIndicatorHit(sampleTile(1, Suit.PIN), sampleTile(2, Suit.PIN), listOf(sampleTile(2, Suit.PIN))))
        )
        val tsumo = GameEvent.TsumoWin(
            turnNumber = 10,
            player = 0,
            winTile = sampleTile(2, Suit.PIN),
            yakuList = listOf("立直", "断幺九"),
            han = 3,
            fu = 30,
            score = 4000,
            breakdown = breakdown
        )
        val events = listOf(tsumo)

        // 1. 无授权：剥除 uraTiles 与 uraHits，但保留所有计分数值和公开条目
        val unauthList = PublicProjectionFactory.publicEvents(events, revealSettlementUra = false)
        val unauthTsumo = unauthList[0] as GameEvent.TsumoWin
        val unauthBreakdown = assertNotNull(unauthTsumo.breakdown)
        assertTrue(unauthBreakdown.dora.uraTiles.isEmpty(), "uraTiles must be stripped when revealSettlementUra=false")
        assertTrue(unauthBreakdown.dora.uraHits.isEmpty(), "uraHits must be stripped when revealSettlementUra=false")
        assertEquals(listOf(sampleTile(5, Suit.MAN)), unauthBreakdown.dora.indicatorTiles, "Public front dora tiles must remain")
        assertEquals(listOf("立直" to 1, "断幺九" to 1), unauthBreakdown.yakuLines, "Yaku lines must remain")
        assertEquals(5600, unauthBreakdown.totalPoints, "Total points must remain")
        assertEquals(1, unauthBreakdown.dora.uraCount, "uraCount numeric score value must remain")
        assertEquals(4000, unauthTsumo.score)

        // 2. 有授权：保留完整里宝实体与命中详情
        val authList = PublicProjectionFactory.publicEvents(events, revealSettlementUra = true)
        val authTsumo = authList[0] as GameEvent.TsumoWin
        val authBreakdown = assertNotNull(authTsumo.breakdown)
        assertEquals(listOf(sampleTile(1, Suit.PIN)), authBreakdown.dora.uraTiles)
        assertEquals(1, authBreakdown.dora.uraHits.size)
        assertEquals(5600, authBreakdown.totalPoints)
        assertEquals(4000, authTsumo.score)
    }

    // ========================================
    // 8. 防御拷贝、值语义与不可变性 (CopyOnRead 与 等值关系)
    // ========================================

    @Test
    fun modifyingCallerInputDoesNotAffectProjection() {
        val mutableScores = mutableListOf(25000, 25000, 25000, 25000)
        val mutableClocks = mutableListOf(ClockView(0, 100L, 200L, 5000L, 100L))
        val mutableEvents = mutableListOf<GameEvent>(GameEvent.DealingComplete(0))

        val session = sampleSessionSnapshot(scores = mutableScores)
        val proj = PublicProjectionFactory.project(
            session = session,
            state = null,
            roundId = null,
            roundVersion = null,
            clocks = mutableClocks,
            paused = false,
            settlementRoundId = null,
            settlementEvents = mutableEvents,
            revealSettlementUra = false
        )

        mutableScores[0] = 99999
        mutableClocks.clear()
        mutableEvents.clear()

        assertEquals(25000, proj.session.scores[0], "Mutating input scores list must not pollute projection")
        assertEquals(1, proj.clocks.size, "Mutating input clocks list must not pollute projection")
        assertEquals(1, proj.settlementEvents.size, "Mutating input events list must not pollute projection")
    }

    @Test
    fun gettersReturnUnmodifiableCollections() {
        val session = sampleSessionSnapshot()
        val state = GameState().apply {
            phase = GamePhase.DISCARD
            players[0].discards.add(sampleTile(1))
            players[1].melds.add(Meld(MeldType.PON, listOf(sampleTile(2), sampleTile(2), sampleTile(2)), 0, sampleTile(2)))
        }
        val initialEvents = listOf(GameEvent.StackingStarted(0))
        val proj = PublicProjectionFactory.project(
            session = session,
            state = state,
            roundId = "round-1",
            roundVersion = 1L,
            clocks = listOf(ClockView(0, 100L, 200L, 5000L, 100L)),
            paused = false,
            settlementRoundId = "round-1",
            settlementEvents = initialEvents,
            revealSettlementUra = false
        )

        // 1. 各非空集合修改断言必须保留
        assertThrows<UnsupportedOperationException> {
            (proj.session.scores as MutableList<Int>)[0] = 1234
        }
        assertThrows<UnsupportedOperationException> {
            (proj.board?.discards?.get(0) as MutableList<Tile>).add(sampleTile(9))
        }
        assertThrows<UnsupportedOperationException> {
            (proj.board?.melds?.get(1) as MutableList<Meld>).clear()
        }
        assertThrows<UnsupportedOperationException> {
            (proj.board?.melds?.get(1)?.get(0)?.tiles as MutableList<Tile>).clear()
        }
        assertThrows<UnsupportedOperationException> {
            (proj.clocks as MutableList<ClockView>).clear()
        }

        // 2. settlementEvents 不可变探针：精确接受 ClassCastException 或 UnsupportedOperationException，并确认修改前后公开内容不变
        val beforeSize = proj.settlementEvents.size
        val ex = assertThrows<Exception> {
            @Suppress("UNCHECKED_CAST")
            (proj.settlementEvents as MutableList<GameEvent>).clear()
        }
        assertTrue(
            ex is ClassCastException || ex is UnsupportedOperationException,
            "Expected mutation to fail with ClassCastException or UnsupportedOperationException, but got ${ex::class}"
        )
        assertEquals(beforeSize, proj.settlementEvents.size, "settlementEvents size must remain unchanged")
        assertEquals(initialEvents, proj.settlementEvents, "settlementEvents content must remain unchanged")
    }

    @Test
    fun copyOnReadPreventsEventArrayPollutionOnSubsequentReads() {
        val originalScores = intArrayOf(26000, 24000, 25000, 25000)
        val scoreSettled = GameEvent.ScoreSettled(1, mapOf(0 to 1000), originalScores)
        val matchEnded = GameEvent.MatchEnded(2, originalScores, 0)
        val events = listOf(scoreSettled, matchEnded)

        val publicList = PublicProjectionFactory.publicEvents(events, revealSettlementUra = false)

        // 第一次读取并破坏数组
        val firstReadSettled = publicList[0] as GameEvent.ScoreSettled
        firstReadSettled.newScores[0] = -99999
        val firstReadEnded = publicList[1] as GameEvent.MatchEnded
        firstReadEnded.finalScores[0] = -99999

        // 第二次读取必须依然为原值 (CopyOnRead 语义)
        val secondReadSettled = publicList[0] as GameEvent.ScoreSettled
        assertEquals(26000, secondReadSettled.newScores[0],
            "Subsequent read of ScoreSettled must return unmodified array")

        val secondReadEnded = publicList[1] as GameEvent.MatchEnded
        assertEquals(26000, secondReadEnded.finalScores[0],
            "Subsequent read of MatchEnded must return unmodified array")
    }

    @Test
    fun copyOnReadEventListMaintainsListEqualityAndContainsSemantics() {
        val scores = intArrayOf(26000, 24000, 25000, 25000)
        val scoreSettled = GameEvent.ScoreSettled(1, mapOf(0 to 1000), scores)
        val matchEnded = GameEvent.MatchEnded(2, scores, 0)
        val events = listOf(scoreSettled, matchEnded)

        val list = PublicProjectionFactory.publicEvents(events, revealSettlementUra = false)
        val list2 = PublicProjectionFactory.publicEvents(events, revealSettlementUra = false)

        // contains 应当基于事件值语义正确返回 true
        val elem0 = list[0]
        assertTrue(list.contains(elem0), "list.contains(list[0]) must be true")
        assertEquals(0, list.indexOf(elem0), "list.indexOf(list[0]) must be 0")

        // 自身相等、同类副本相等与子列表相等
        assertEquals(list, list)
        assertEquals(list, list2)
        assertEquals(list2, list)
        assertEquals(list.hashCode(), list2.hashCode())
        assertEquals(list, list.subList(0, 2))

        // 普通 List 与 CopyOnReadEventList 的对称性契约：
        // 1. 含原生数组事件时，因防御拷贝各持有独立数组，双向必须对称返回 false
        assertEquals(list == events, events == list, "equals between CopyOnReadEventList and plain List must be symmetric")
        assertFalse(list == events, "CopyOnReadEventList != plain List when array events have independent instances")
        assertFalse(events == list, "plain List != CopyOnReadEventList symmetrically when array events have independent instances")

        // 2. 无原生数组事件时，双向必须对称返回 true
        val plainEvents = listOf(GameEvent.StackingStarted(0), GameEvent.DealingComplete(0))
        val plainCopyOnRead = PublicProjectionFactory.publicEvents(plainEvents, revealSettlementUra = false)
        assertEquals(plainCopyOnRead, plainEvents, "CopyOnReadEventList must equal plain List when no array events present")
        assertEquals(plainEvents, plainCopyOnRead, "plain List must equal CopyOnReadEventList symmetrically when no array events present")

        // listIterator(index) 边界与末尾游标契约校验
        val startIter = list.listIterator(0)
        assertTrue(startIter.hasNext())
        assertFalse(startIter.hasPrevious())

        val endIter = list.listIterator(list.size)
        assertFalse(endIter.hasNext(), "listIterator(size) at end cursor must have no next")
        assertTrue(endIter.hasPrevious(), "listIterator(size) at end cursor must have previous")
        val previousEvent = endIter.previous()
        assertTrue(list.contains(previousEvent))

        assertThrows<IndexOutOfBoundsException> { list.listIterator(-1) }
        assertThrows<IndexOutOfBoundsException> { list.listIterator(list.size + 1) }

        // PublicTableProjection 的 deepCopy / copy / 独立重建 等值与 hash 一致性
        val session = sampleSessionSnapshot()
        val proj = PublicProjectionFactory.project(
            session = session,
            state = null,
            roundId = "round-1",
            roundVersion = 1L,
            clocks = emptyList(),
            paused = false,
            settlementRoundId = "round-1",
            settlementEvents = events,
            revealSettlementUra = false
        )

        val copy = proj.deepCopy()
        assertEquals(proj, copy, "PublicTableProjection.deepCopy must maintain equals value semantics")
        assertEquals(proj.hashCode(), copy.hashCode(), "PublicTableProjection.deepCopy must maintain hashCode")

        val projRebuilt = PublicProjectionFactory.project(
            session = session,
            state = null,
            roundId = "round-1",
            roundVersion = 1L,
            clocks = emptyList(),
            paused = false,
            settlementRoundId = "round-1",
            settlementEvents = events,
            revealSettlementUra = false
        )
        assertEquals(proj, projRebuilt, "Independently reconstructed PublicTableProjection must maintain equals value semantics")
        assertEquals(projRebuilt, proj, "Independently reconstructed PublicTableProjection equality must be symmetric")
        assertEquals(proj.hashCode(), projRebuilt.hashCode(), "Independently reconstructed PublicTableProjection must maintain hashCode")

        val modifiedCopy = proj.copy(paused = true)
        assertNotEquals(proj, modifiedCopy)
    }
}
