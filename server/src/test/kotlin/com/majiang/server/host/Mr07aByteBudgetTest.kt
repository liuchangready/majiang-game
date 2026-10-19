package com.majiang.server.host

import com.majiang.access.*
import com.majiang.application.ActionOption
import com.majiang.application.PlayerDecision
import com.majiang.cheat.beam.*
import com.majiang.cheat.bloom.*
import com.majiang.cheat.mark.*
import com.majiang.event.DrawReason
import com.majiang.event.GameEvent
import com.majiang.model.*
import com.majiang.rules.*
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.session.PlayerView
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.transport.api.*
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.Executor
import java.util.concurrent.Executors

/**
 * MR-07a 双字节预算真实计量、算术溢出防护、超配单项语义与全量边界测试
 */
@Timeout(15)
class Mr07aByteBudgetTest {

    private data class Item(val size: Long)

    @Test
    fun `oversized single item is rejected on empty queue under strict default policy`() {
        val executor = Executors.newSingleThreadExecutor()
        try {
            val channel = SerialDispatchChannel<Item>(
                name = "test-strict-oversize",
                executor = executor,
                maxQueuedBytes = 1000L,
                allowSingleOversized = false,
                byteEstimator = { it.size },
                consumer = {}
            )

            // 空队列尝试塞入 1500 字节条目（超出 1000 字节上限）
            val enqueued = channel.enqueue(Item(1500L))
            assertFalse(enqueued, "Oversized single item must be rejected under strict policy")
            assertEquals(1L, channel.overflowCount, "Overflow count must increment")
            assertTrue(channel.needsRecovery, "needsRecovery must be set")
            assertEquals(0, channel.queuedItems, "Item must not be queued")
            assertEquals(0L, channel.queuedBytes, "Queued bytes must remain 0")
        } finally {
            executor.shutdownNow()
        }
    }

    @Test
    fun `oversized single item is permitted on empty queue and isolates subsequent enqueues under isolation policy`() {
        val executor = Executors.newSingleThreadExecutor()
        try {
            val channel = SerialDispatchChannel<Item>(
                name = "test-isolated-oversize",
                executor = executor,
                maxQueuedBytes = 1000L,
                allowSingleOversized = true,
                byteEstimator = { it.size },
                consumer = {}
            )

            // 空队列下允许单项超配进入隔离
            val first = channel.enqueue(Item(1500L))
            assertTrue(first, "Single oversized item must be admitted when allowSingleOversized is true")
            assertEquals(1, channel.queuedItems)
            assertEquals(1500L, channel.queuedBytes)

            // 后续即便只有 1 字节的新入队，也必须被严格阻断，实现单项超额隔离
            val second = channel.enqueue(Item(1L))
            assertFalse(second, "Subsequent enqueue must be rejected while oversized item occupies queue")
            assertEquals(1L, channel.overflowCount)
            assertTrue(channel.needsRecovery)
        } finally {
            executor.shutdownNow()
        }
    }

    @Test
    fun `queue total capacity boundary strictly enforced at exact byte limit`() {
        val executor = Executors.newSingleThreadExecutor()
        try {
            val channel = SerialDispatchChannel<Item>(
                name = "test-exact-boundary",
                executor = executor,
                maxQueuedBytes = 2000L,
                allowSingleOversized = false,
                byteEstimator = { it.size },
                consumer = {}
            )

            // 1. 入队 800 字节 -> 800 <= 2000
            assertTrue(channel.enqueue(Item(800L)))
            assertEquals(800L, channel.queuedBytes)

            // 2. 入队 1200 字节 -> 刚好达到 2000 字节上限 (800 + 1200 = 2000 <= 2000)
            assertTrue(channel.enqueue(Item(1200L)))
            assertEquals(2000L, channel.queuedBytes)
            assertEquals(0L, channel.overflowCount)

            // 3. 再尝试入队 1 字节 -> 超额 2001 > 2000，必须严格阻断
            assertFalse(channel.enqueue(Item(1L)))
            assertEquals(1L, channel.overflowCount)
            assertTrue(channel.needsRecovery)
            assertEquals(2000L, channel.queuedBytes, "Queued bytes must remain at exactly 2000L")
        } finally {
            executor.shutdownNow()
        }
    }

    @Test
    fun `arithmetic overflow protection prevents extreme sizes from bypassing limits`() {
        val capturedRunnables = ArrayList<Runnable>()
        val controlledExecutor = object : java.util.concurrent.AbstractExecutorService() {
            override fun shutdown() {}
            override fun shutdownNow(): List<Runnable> = emptyList()
            override fun isShutdown(): Boolean = false
            override fun isTerminated(): Boolean = false
            override fun awaitTermination(timeout: Long, unit: java.util.concurrent.TimeUnit): Boolean = true
            override fun execute(command: Runnable) {
                synchronized(capturedRunnables) { capturedRunnables.add(command) }
            }
        }
        // 1. 严格模式下入队 Long.MAX_VALUE，绝不发生环绕或越界放行
        val strictChannel = SerialDispatchChannel<Item>(
            name = "test-overflow-strict",
            executor = controlledExecutor,
            maxQueuedBytes = 10_000L,
            allowSingleOversized = false,
            byteEstimator = { it.size },
            consumer = {}
        )
        val strictOk = strictChannel.enqueue(Item(Long.MAX_VALUE))
        assertFalse(strictOk, "Extreme size must be rejected under strict policy")
        assertEquals(1L, strictChannel.overflowCount)
        assertTrue(strictChannel.needsRecovery)
        assertEquals(0L, strictChannel.queuedBytes)

        // 2. 队列已有数据时，再入队接近 Long.MAX_VALUE 的条目，currentBytes + bytes 溢出防护
        val isolatedChannel = SerialDispatchChannel<Item>(
            name = "test-overflow-nonempty",
            executor = controlledExecutor,
            maxQueuedBytes = 10_000L,
            allowSingleOversized = true,
            byteEstimator = { it.size },
            consumer = {}
        )
        // 先入队一条合法普通数据
        assertTrue(isolatedChannel.enqueue(Item(500L)))
        assertEquals(500L, isolatedChannel.queuedBytes)

        // 再入队会导致加法溢出回绕为负数的极值（若无防护：500 + (Long.MAX_VALUE - 100) 会回绕为负数从而 < 10000 造成逃逸）
        val overflowOk = isolatedChannel.enqueue(Item(Long.MAX_VALUE - 100L))
        assertFalse(overflowOk, "Arithmetic overflow addition must NOT wrap around to bypass capacity limit")
        assertEquals(1L, isolatedChannel.overflowCount)
        assertTrue(isolatedChannel.needsRecovery)
        assertEquals(500L, isolatedChannel.queuedBytes, "Queued bytes must remain unaffected by rejected overflow")

        // 3. 隔离模式在空队列时接收 Long.MAX_VALUE，后续入队 1 字节加法溢出防护
        val maxChannel = SerialDispatchChannel<Item>(
            name = "test-overflow-max-first",
            executor = controlledExecutor,
            maxQueuedBytes = 10_000L,
            allowSingleOversized = true,
            byteEstimator = { it.size },
            consumer = {}
        )
        assertTrue(maxChannel.enqueue(Item(Long.MAX_VALUE)))
        assertEquals(Long.MAX_VALUE, maxChannel.queuedBytes)
        // 下一条入队 1 字节：currentBytes(MAX) + 1 必须被防护检测到溢出且容量超限
        val nextOk = maxChannel.enqueue(Item(1L))
        assertFalse(nextOk, "Subsequent enqueue after Long.MAX_VALUE must be strictly rejected")
        assertEquals(1L, maxChannel.overflowCount)
        assertEquals(Long.MAX_VALUE, maxChannel.queuedBytes)
    }

    @Test
    fun `structural byte estimator realistically upper bounds late game big snapshot against actual wire length`() {
        val dummyTile = Tile.fromTileId(0)
        // 构造后期大牌桌状态：4 位玩家各自 18 张弃牌（共 72 张弃牌），4 个副露，4 个宝牌指示牌
        val meld1 = Meld(MeldType.CHI, listOf(Tile.fromTileId(0), Tile.fromTileId(4), Tile.fromTileId(8)), 1, Tile.fromTileId(0))
        val meld2 = Meld(MeldType.PON, listOf(Tile.fromTileId(72), Tile.fromTileId(73), Tile.fromTileId(74)), 2, Tile.fromTileId(72))
        val meld3 = Meld(MeldType.OPEN_KAN, listOf(Tile.fromTileId(36), Tile.fromTileId(37), Tile.fromTileId(38), Tile.fromTileId(39)), 3, Tile.fromTileId(36))
        val meld4 = Meld(MeldType.CLOSED_KAN, listOf(Tile.fromTileId(124), Tile.fromTileId(125), Tile.fromTileId(126), Tile.fromTileId(127)), -1, Tile.fromTileId(124))

        val doraList = listOf(
            Tile.fromTileId(1),
            Tile.fromTileId(40),
            Tile.fromTileId(75),
            Tile.fromTileId(108)
        )

        val discards = List(4) { seat ->
            List(18) { idx -> Tile.fromTileId((seat * 20 + idx) % 136) }
        }

        val legalActions = listOf(
            GameAction.DiscardTile(0, dummyTile),
            GameAction.DeclareRiichi(0, dummyTile),
            GameAction.DeclareChi(0, meld1),
            GameAction.DeclarePon(0, meld2.tiles),
            GameAction.Pass(0)
        )

        val decisionOptions = legalActions.mapIndexed { idx, act ->
            ActionOption("opt-$idx", act)
        }

        val clocks = List(4) { s ->
            ClockView(seat = s, opensAt = 1000L, deadline = 4000L, reserve = 15000L, serverNow = 1200L)
        }

        val controls = List(4) { s ->
            SeatControlView(seat = s, connected = true, role = "HUMAN", controlEpoch = 1L)
        }

        val roundRecords = List(4) { r ->
            PublicRoundRecord(
                roundId = "round-$r",
                resultId = "res-$r",
                finalScores = listOf(26000, 24000, 25000, 25000),
                deltas = listOf(1000, -1000, 0, 0),
                finalCheatPoints = listOf(90, 110, 100, 100),
                cheatDeltas = listOf(-10, 10, 0, 0)
            )
        }

        val sessionSummary = SessionView(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(26000, 24000, 25000, 25000),
            roundWind = 1,
            roundNumber = 3,
            dealerSeat = 2,
            honba = 1,
            roundRecords = roundRecords,
            finalResult = PublicSessionResult(
                finalScores = listOf(26000, 24000, 25000, 25000),
                rankedSeats = listOf(0, 2, 3, 1),
                reason = "NORMAL_COMPLETION",
                finalCheatPoints = listOf(90, 110, 100, 100)
            )
        )

        val view = PlayerView.create(
            viewerSeat = 0,
            phase = GamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 3,
            honbaSticks = 1,
            riichiSticks = 1,
            dealerSeat = 2,
            currentTurnPlayer = 0,
            wallRemaining = 14,
            doraIndicators = doraList,
            scores = listOf(26000, 24000, 25000, 25000),
            riichiStatus = listOf(false, true, false, false),
            discards = discards,
            melds = listOf(listOf(meld1), listOf(meld2), listOf(meld3), listOf(meld4)),
            handTileCounts = listOf(14, 13, 13, 13),
            myClosedTiles = List(13) { Tile.fromTileId(12 + it) },
            myDrawnTile = dummyTile,
            legalActions = legalActions
        )

        val snap = PlayerSnapshot(
            sessionId = "test-session-big",
            roundId = "round-3",
            syncEpoch = "epoch-1",
            cursor = 120L,
            sessionVersion = 4L,
            roundVersion = 88L,
            view = view,
            decision = PlayerDecision(
                decisionId = "dec-big-99",
                roundId = "round-3",
                controlEpoch = 1L,
                options = decisionOptions
            ),
            clocks = clocks,
            cheatPoints = listOf(90, 110, 100, 100),
            session = sessionSummary,
            controls = controls
        )

        val update = PlayerUpdate(
            snapshot = snap,
            fromCursor = 119L,
            visibleEvents = listOf(
                GameEvent.TileDiscarded(turnNumber = 1, player = 3, tile = dummyTile)
            )
        )

        val seatItem = ServerTableHost.SeatDispatchItem.Broadcast(update, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimateSeatDispatchBytes(seatItem)

        // 通过协议编码获取真实的线路载荷（含 12 字节二进制帧头）
        val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
        val wireFrame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate, isFullSnapshot = true)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        // 核心上界证明：估算值必须严格大于等于真实网络载荷，且紧密贴合（在 1.0x ~ 1.5x 之间）
        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Structural estimated bytes ($estimatedBytes) must realistically upper-bound actual wire bytes ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.5,
            "Estimated bytes ($estimatedBytes) must be tightly bounded within 1.5x of actual wire bytes ($actualWireBytes), ratio was $ratio"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds complex settlement and rich events against actual wire length`() {
        val dummyTile = Tile.fromTileId(0)
        val view = PlayerView.create(
            viewerSeat = 0,
            phase = GamePhase.ROUND_END,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 1,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 50,
            doraIndicators = listOf(dummyTile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(true, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(14, 13, 13, 13),
            myClosedTiles = List(13) { dummyTile },
            myDrawnTile = dummyTile,
            legalActions = emptyList()
        )

        // 构造包含多役种、符数明细、宝牌指示牌命中与多方付款的复杂结算 ScoreBreakdown
        val scoreBreakdown = ScoreBreakdown(
            yakuLines = listOf(
                Pair("Riichi", 1),
                Pair("Menzen Tsumo", 1),
                Pair("Pinfu", 1),
                Pair("Iipeiko", 1),
                Pair("Dora", 2)
            ),
            yakumanCount = 0,
            dora = DoraBreakdown(
                indicatorCount = 1,
                uraCount = 1,
                redCount = 1,
                totalHan = 3,
                indicatorTiles = listOf(Tile.fromTileId(10)),
                uraTiles = listOf(Tile.fromTileId(20)),
                redTiles = listOf(Tile.fromTileId(30)),
                indicatorHits = listOf(
                    DoraIndicatorHit(
                        indicator = Tile.fromTileId(10),
                        target = Tile.fromTileId(11),
                        tiles = listOf(Tile.fromTileId(11))
                    )
                ),
                uraHits = listOf(
                    DoraIndicatorHit(
                        indicator = Tile.fromTileId(20),
                        target = Tile.fromTileId(21),
                        tiles = listOf(Tile.fromTileId(21))
                    )
                )
            ),
            fu = FuBreakdown(
                lines = listOf(
                    FuLine("Base fu", 20),
                    FuLine("Closed Tsumo", 2),
                    FuLine("Edge wait", 2)
                ),
                subtotal = 24,
                roundedUpTo = 30,
                finalFu = 30,
                specialNote = "Rounded up to next 10"
            ),
            score = ScoreCalculator.ScoreResult(
                fu = 30,
                han = 6,
                basePoints = 3000,
                title = "Haneman",
                isYakuman = false,
                yakumanMultiplier = 0,
                capReason = "Haneman ceiling",
                formulaText = "3000 * 2 = 6000 / 3000"
            ),
            paymentLines = listOf(
                "Seat 1 pays 3000",
                "Seat 2 pays 3000",
                "Seat 3 pays 3000"
            ),
            honbaAddition = 300,
            riichiAddition = 1000,
            totalPoints = 12000,
            uraEligible = true,
            isDealer = true,
            isTsumo = true
        )

        val tsumoEvent = GameEvent.TsumoWin(
            turnNumber = 1,
            player = 0,
            winTile = dummyTile,
            yakuList = listOf("Riichi", "Menzen Tsumo", "Pinfu", "Iipeiko", "Dora"),
            han = 6,
            fu = 30,
            score = 12000,
            breakdown = scoreBreakdown
        )

        val drawEvent = GameEvent.DrawGame(
            turnNumber = 2,
            reason = DrawReason.EXHAUSTIVE,
            tenpaiPlayers = listOf(0, 2)
        )

        val snap = PlayerSnapshot(
            sessionId = "test-session-settle",
            roundId = "round-settle",
            syncEpoch = "epoch-1",
            cursor = 50L,
            sessionVersion = 1L,
            roundVersion = 50L,
            view = view,
            decision = PlayerDecision(
                decisionId = "dec-settle",
                roundId = "round-settle",
                controlEpoch = 1L,
                options = emptyList()
            ),
            settlementRoundId = "round-settle",
            settlementEvents = listOf(tsumoEvent, drawEvent)
        )

        val update = PlayerUpdate(
            snapshot = snap,
            fromCursor = 49L,
            visibleEvents = listOf(tsumoEvent, drawEvent)
        )

        val seatItem = ServerTableHost.SeatDispatchItem.Broadcast(update, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimateSeatDispatchBytes(seatItem)

        val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
        val wireFrame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate, isFullSnapshot = true)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Settlement estimated bytes ($estimatedBytes) must upper-bound actual wire bytes ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.5,
            "Settlement estimated bytes ($estimatedBytes) must be within 1.5x of actual wire bytes ($actualWireBytes), ratio was $ratio"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds private collections and cheat views against actual wire length`() {
        val dummyTile = Tile.fromTileId(0)
        val dummyTile2 = Tile.fromTileId(4)
        val dummyTile3 = Tile.fromTileId(8)
        val sleeveTiles = listOf(Tile.fromTileId(50), Tile.fromTileId(51))
        val discardHistory = List(12) { Tile.fromTileId(60 + it) }

        val meld1 = Meld(MeldType.CHI, listOf(dummyTile, dummyTile2, dummyTile3), 3, dummyTile)

        val t0 = Tile.fromTileId(0)
        val t1 = Tile.fromTileId(1)
        val t2 = Tile.fromTileId(2)
        val t3 = Tile.fromTileId(3)

        val closedTiles = List(13) { Tile.fromTileId(12 + it) }
        val dList1 = listOf(Tile.fromTileId(80), Tile.fromTileId(81), Tile.fromTileId(82), t2)
        val view = PlayerView.create(
            viewerSeat = 0,
            phase = GamePhase.CHEAT_WINDOW,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(dummyTile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = listOf(emptyList(), dList1, emptyList(), emptyList()),
            melds = List(4) { emptyList() },
            handTileCounts = listOf(14, 13, 13, 13),
            myClosedTiles = closedTiles,
            myDrawnTile = dummyTile,
            legalActions = listOf(GameAction.DiscardTile(0, dummyTile)),
            mySleeveTiles = sleeveTiles,
            myDiscardHistory = discardHistory
        )

        // 构造落汗千术透视：含可选实体列表与跨局标记组
        val markedTilesGroup1 = listOf(
            MarkedTileView(tileId = 0, tile = t0, area = TileMarkArea.OWN_HAND, seat = 0),
            MarkedTileView(tileId = 1, tile = t1, area = TileMarkArea.OWN_HAND, seat = 0),
            MarkedTileView(
                tileId = 2,
                tile = t2,
                area = TileMarkArea.DISCARD,
                seat = 1,
                riverPosition = RiverPosition(roundId = "round-cheat", seat = 1, discardIndex = 3)
            ),
            MarkedTileView(tileId = 3, tile = t3, area = TileMarkArea.OPPONENT_HAND, seat = 2)
        )
        val tileMarkView = TileMarkView(
            canApply = true,
            disabledReason = null,
            cost = 10,
            activeTileCount = 4,
            maxActiveTiles = 12,
            selectableTileIds = listOf(10, 11, 12, 13, 14),
            decisionId = "dec-cheat",
            controlEpoch = 1L,
            stateVersion = 10L,
            activeGroups = listOf(
                TileMarkGroupView(
                    groupId = "grp-1",
                    createdRoundId = "round-cheat",
                    createdRoundOrdinal = 1,
                    expiresAfterRoundOrdinal = 4,
                    futureRoundsRemaining = 2,
                    isLastEffectiveRound = false,
                    tiles = markedTilesGroup1
                )
            )
        )

        // 构造袖里藏花透视：含未决方案
        val sleeveBloomView = SleeveBloomView(
            canApply = true,
            disabledReason = null,
            cost = 10,
            drawKind = SleeveBloomDrawKind.NORMAL,
            decisionId = "dec-cheat",
            controlEpoch = 1L,
            stateVersion = 10L,
            pendingOffer = SleeveBloomOfferView(
                offerId = "offer-bloom-1",
                firstTile = dummyTile,
                secondTile = dummyTile2,
                drawKind = SleeveBloomDrawKind.NORMAL,
                deadlineMillis = 5000L,
                decisionId = "dec-cheat",
                controlEpoch = 1L
            )
        )

        // 构造偷梁换柱透视：含候选换牌条目与可选牌
        val beamSwapView = BeamSwapView(
            canApply = true,
            disabledReason = null,
            cost = 10,
            window = BeamSwapWindow.WALL,
            decisionId = "dec-cheat",
            controlEpoch = 1L,
            stateVersion = 10L,
            pendingOffer = BeamSwapOfferView(
                offerId = "offer-beam-1",
                window = BeamSwapWindow.WALL,
                candidates = listOf(
                    BeamSwapCandidateView(slot = BeamSwapSlot.WallSlot(0), marked = true, tile = dummyTile),
                    BeamSwapCandidateView(slot = BeamSwapSlot.WallSlot(1), marked = false, tile = null),
                    BeamSwapCandidateView(slot = BeamSwapSlot.DeadWallSlot(0), marked = true, tile = dummyTile3)
                ),
                selectableHandTileIds = listOf(12, 13, 14),
                deadlineMillis = 5000L,
                decisionId = "dec-cheat",
                controlEpoch = 1L
            )
        )

        val riichiAction = GameAction.DeclareRiichi(0, dummyTile)
        val meldAction = GameAction.DeclareChi(0, meld1)
        val decisionOptions = listOf(
            ActionOption("opt-riichi-1", riichiAction),
            ActionOption("opt-meld-1", meldAction)
        )

        // 构造展示预览：含听牌、立直与鸣牌选项
        val preview = PlayerPresentationPreview(
            roundId = "round-cheat",
            decisionId = "dec-cheat",
            controlEpoch = 1L,
            waits = listOf(dummyTile, dummyTile2),
            riichiOptions = listOf(
                RiichiPreviewOption(
                    optionId = "opt-riichi-1",
                    action = riichiAction,
                    waits = listOf(dummyTile)
                )
            ),
            meldOptions = listOf(
                MeldPreviewOption(
                    optionId = "opt-meld-1",
                    action = meldAction,
                    type = MeldType.CHI,
                    tiles = meld1.tiles,
                    handTiles = listOf(dummyTile2, dummyTile3),
                    calledTile = dummyTile,
                    fromPlayer = 3
                )
            )
        )

        val snap = PlayerSnapshot(
            sessionId = "test-session-cheat",
            roundId = "round-cheat",
            syncEpoch = "epoch-1",
            cursor = 30L,
            sessionVersion = 1L,
            roundVersion = 30L,
            view = view,
            decision = PlayerDecision(
                decisionId = "dec-cheat",
                roundId = "round-cheat",
                controlEpoch = 1L,
                options = decisionOptions
            ),
            presentationPreview = preview,
            tileMark = tileMarkView,
            sleeveBloom = sleeveBloomView,
            beamSwap = beamSwapView
        )

        val update = PlayerUpdate(
            snapshot = snap,
            fromCursor = 29L,
            visibleEvents = emptyList()
        )

        val seatItem = ServerTableHost.SeatDispatchItem.Broadcast(update, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimateSeatDispatchBytes(seatItem)

        val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
        val wireFrame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate, isFullSnapshot = true)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Private collection estimated bytes ($estimatedBytes) must upper-bound actual wire bytes ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.5,
            "Private collection estimated bytes ($estimatedBytes) must be within 1.5x of actual wire bytes ($actualWireBytes), ratio was $ratio"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds public dispatch update against actual wire length`() {
        val protoTile = ProtocolTile(0)
        val doraIndicators = List(4) { ProtocolTile(10 + it) }
        val discards = List(4) { s ->
            List(18) { idx -> ProtocolTile((s * 20 + idx) % 136) }
        }
        val melds = listOf(
            listOf(
                ProtocolMeld(
                    type = ProtocolMeldType.CHI,
                    tiles = listOf(ProtocolTile(0), ProtocolTile(4), ProtocolTile(8)),
                    fromPlayer = 1,
                    calledTile = ProtocolTile(0)
                )
            ),
            emptyList(),
            emptyList(),
            emptyList()
        )

        val board = ProtocolPublicBoardView(
            phase = ProtocolGamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 2,
            honbaSticks = 1,
            riichiSticks = 1,
            dealerSeat = 1,
            currentTurnPlayer = 0,
            wallRemaining = 20,
            doraIndicators = doraIndicators,
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, true, false, false),
            discards = discards,
            melds = melds,
            handTileCounts = listOf(14, 13, 13, 13),
            lastDiscard = protoTile,
            lastDiscardPlayer = 3
        )

        val roundRecords = List(3) { r ->
            ProtocolPublicScoreRecord(
                roundId = "round-$r",
                resultId = "res-$r",
                finalScores = listOf(25000, 25000, 25000, 25000),
                deltas = listOf(0, 0, 0, 0),
                remainingRiichiSticks = 0,
                outcome = null
            )
        }

        val sessionSummary = ProtocolPublicSessionSummary(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 2,
            dealerSeat = 1,
            honba = 1,
            roundRecords = roundRecords,
            finalResult = ProtocolPublicMatchResult(
                finalScores = listOf(25000, 25000, 25000, 25000),
                rankedSeats = listOf(0, 1, 2, 3),
                reason = "NORMAL_COMPLETION",
                remainingRiichiSticksTransferredTo = null
            )
        )

        val clocks = List(4) { s ->
            ProtocolClock(seat = s, opensAt = 1000L, deadline = 4000L, reserve = 18000L, serverNow = 1100L)
        }

        val publicSnap = ProtocolPublicSnapshot(
            sessionId = "test-session-public",
            sessionVersion = 2L,
            roundId = "round-2",
            roundVersion = 40L,
            syncEpoch = "epoch-1",
            cursor = 60L,
            board = board,
            session = sessionSummary,
            clocks = clocks,
            paused = false,
            settlementRoundId = null,
            settlementEvents = emptyList()
        )

        val publicUpdate = ProtocolPublicUpdate(
            snapshot = publicSnap,
            fromCursor = 60L,
            visibleEvents = listOf(
                ProtocolGameEvent.TileDiscarded(turnNumber = 1, player = 3, tile = protoTile, isRiichiDiscard = false, isTsumogiri = true)
            ),
            isFullSnapshot = true
        )

        val publicItem = ServerTableHost.PublicDispatchItem.Broadcast(publicUpdate, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimatePublicDispatchBytes(publicItem)

        val wireFrame = TcpMessageCodec.toPublicUpdateFrame(publicUpdate)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Public dispatch estimated bytes ($estimatedBytes) must upper-bound actual wire bytes ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.5,
            "Public dispatch estimated bytes ($estimatedBytes) must be within 1.5x of actual wire bytes ($actualWireBytes), ratio was $ratio"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds long string fields and oversized IDs against actual wire length`() {
        val dummyTile = Tile.fromTileId(0)
        val view = PlayerView.create(
            viewerSeat = 0,
            phase = GamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(dummyTile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(14, 13, 13, 13),
            myClosedTiles = List(13) { dummyTile },
            myDrawnTile = dummyTile,
            legalActions = listOf(GameAction.DiscardTile(0, dummyTile))
        )

        // 构造超长 sessionId（10000 字符），超长 roundId 与 decisionId
        val longSessionId = "x".repeat(10000)
        val longRoundId = "round-" + "r".repeat(500)
        val longSyncEpoch = "epoch-" + "e".repeat(300)
        val longDecisionId = "dec-" + "d".repeat(400)
        val longOptionId = "opt-" + "o".repeat(300)

        val snap = PlayerSnapshot(
            sessionId = longSessionId,
            roundId = longRoundId,
            syncEpoch = longSyncEpoch,
            cursor = 1L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = view,
            decision = PlayerDecision(
                decisionId = longDecisionId,
                roundId = longRoundId,
                controlEpoch = 1L,
                options = listOf(
                    ActionOption(longOptionId, GameAction.DiscardTile(0, dummyTile))
                )
            ),
            clocks = listOf(
                ClockView(
                    seat = 0,
                    opensAt = 1000L,
                    deadline = 5000L,
                    reserve = 20000L,
                    serverNow = 1100L
                )
            ),
            controls = listOf(
                SeatControlView(0, true, "HUMAN", 1L)
            ),
            settlementRoundId = longRoundId
        )

        val update = PlayerUpdate(
            snapshot = snap,
            fromCursor = 0L,
            visibleEvents = emptyList()
        )

        val seatItem = ServerTableHost.SeatDispatchItem.Broadcast(update = update, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimateSeatDispatchBytes(seatItem)

        val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
        val wireFrame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate, isFullSnapshot = true)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        // 核心保证：即使存在 10000 字符的大字段，估算值仍严格 >= 实际线路载荷
        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Estimated bytes ($estimatedBytes) must upper-bound actual wire bytes ($actualWireBytes) for 10000B string"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.5,
            "Estimated bytes ($estimatedBytes) must be tightly bounded within 1.5x of actual wire bytes ($actualWireBytes), ratio was $ratio"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds complex settlement and rich multi-byte string events against actual wire length`() {
        val dummyTile = Tile.fromTileId(0)
        val view = PlayerView.create(
            viewerSeat = 0,
            phase = GamePhase.ROUND_END,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 2,
            riichiSticks = 1,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 50,
            doraIndicators = listOf(dummyTile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(true, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(14, 13, 13, 13),
            myClosedTiles = List(13) { dummyTile },
            myDrawnTile = dummyTile,
            legalActions = emptyList()
        )

        // 构造包含中日文多字节 UTF-8 字符（3 字节/字符）的复杂役满与细分拆解
        val multiByteScoreBreakdown = ScoreBreakdown(
            yakuLines = listOf(
                Pair("国士无双十三面待 (Kokushi 13-wait)", 26),
                Pair("天和 (Tenhou Blessing of Heaven)", 13),
                Pair("大四喜 (Dai Suushii Big Four Winds)", 26),
                Pair("字一色 (Tsuuiisou All Honors)", 13)
            ),
            yakumanCount = 4,
            dora = DoraBreakdown(
                indicatorCount = 2,
                uraCount = 2,
                redCount = 1,
                totalHan = 5,
                indicatorTiles = listOf(Tile.fromTileId(10), Tile.fromTileId(14)),
                uraTiles = listOf(Tile.fromTileId(20), Tile.fromTileId(24)),
                redTiles = listOf(Tile.fromTileId(30)),
                indicatorHits = listOf(
                    DoraIndicatorHit(Tile.fromTileId(10), Tile.fromTileId(11), listOf(Tile.fromTileId(11)))
                ),
                uraHits = listOf(
                    DoraIndicatorHit(Tile.fromTileId(20), Tile.fromTileId(21), listOf(Tile.fromTileId(21)))
                )
            ),
            fu = FuBreakdown(
                lines = listOf(
                    FuLine("自摸底符 (Menzen Tsumo Base Fu)", 20),
                    FuLine("幺九暗刻 (Terminal Closed Triplet)", 8),
                    FuLine("字牌暗刻 (Honor Closed Triplet)", 8),
                    FuLine("单骑听牌 (Single Wait)", 2)
                ),
                subtotal = 38,
                roundedUpTo = 40,
                finalFu = 40,
                specialNote = "役满特例结算，保留符数详细记录供表现层计分动画呈现"
            ),
            score = ScoreCalculator.ScoreResult(
                fu = 40,
                han = 78,
                basePoints = 32000,
                title = "四倍役满 (Quadruple Yakuman)",
                isYakuman = true,
                yakumanMultiplier = 4,
                capReason = "役满上限顶格封顶",
                formulaText = "32000 * 4 = 128000 点"
            ),
            paymentLines = listOf(
                "席位1 (西家) 支付 48000 点",
                "席位2 (南家) 支付 40000 点",
                "席位3 (北家) 支付 40000 点"
            ),
            honbaAddition = 600,
            riichiAddition = 1000,
            totalPoints = 129600,
            uraEligible = true,
            isDealer = true,
            isTsumo = true
        )

        val tsumoEvent = GameEvent.TsumoWin(
            turnNumber = 1,
            player = 0,
            winTile = dummyTile,
            yakuList = listOf(
                "国士无双十三面待 (Kokushi 13-wait)",
                "天和 (Tenhou Blessing of Heaven)",
                "大四喜 (Dai Suushii Big Four Winds)",
                "字一色 (Tsuuiisou All Honors)"
            ),
            han = 78,
            fu = 40,
            score = 129600,
            breakdown = multiByteScoreBreakdown
        )

        val snap = PlayerSnapshot(
            sessionId = "session-multibyte-utf8-结算-test",
            roundId = "round-utf8-东一局",
            syncEpoch = "epoch-1",
            cursor = 88L,
            sessionVersion = 1L,
            roundVersion = 88L,
            view = view,
            decision = PlayerDecision(
                decisionId = "dec-utf8",
                roundId = "round-utf8-东一局",
                controlEpoch = 1L,
                options = emptyList()
            ),
            settlementRoundId = "round-utf8-东一局",
            settlementEvents = listOf(tsumoEvent)
        )

        val update = PlayerUpdate(
            snapshot = snap,
            fromCursor = 87L,
            visibleEvents = listOf(tsumoEvent)
        )

        val seatItem = ServerTableHost.SeatDispatchItem.Broadcast(update, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimateSeatDispatchBytes(seatItem)

        val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
        val wireFrame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate, isFullSnapshot = true)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Multi-byte UTF-8 settlement estimated bytes ($estimatedBytes) must upper-bound actual wire bytes ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.5,
            "Multi-byte UTF-8 estimated bytes ($estimatedBytes) must be within 1.5x of actual wire bytes ($actualWireBytes), ratio was $ratio"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds public dispatch update with long strings against actual wire length`() {
        val protoTile = ProtocolTile.fromTileId(0)
        val board = ProtocolPublicBoardView(
            phase = ProtocolGamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(protoTile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(13, 13, 13, 13),
            lastDiscard = null,
            lastDiscardPlayer = null
        )

        val longSessionId = "pub-session-" + "s".repeat(8000)
        val longRoundId = "pub-round-" + "r".repeat(500)
        val longSyncEpoch = "pub-epoch-" + "e".repeat(200)

        val sessionSummary = ProtocolPublicSessionSummary(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 2,
            dealerSeat = 1,
            honba = 1,
            roundRecords = listOf(
                ProtocolPublicScoreRecord(
                    roundId = longRoundId,
                    resultId = "res-long-1",
                    finalScores = listOf(25000, 25000, 25000, 25000),
                    deltas = listOf(0, 0, 0, 0),
                    remainingRiichiSticks = 0,
                    outcome = null
                )
            ),
            finalResult = null
        )

        val clocks = List(4) { s ->
            ProtocolClock(seat = s, opensAt = 1000L, deadline = 4000L, reserve = 18000L, serverNow = 1100L)
        }

        val publicSnap = ProtocolPublicSnapshot(
            sessionId = longSessionId,
            sessionVersion = 2L,
            roundId = longRoundId,
            roundVersion = 40L,
            syncEpoch = longSyncEpoch,
            cursor = 60L,
            board = board,
            session = sessionSummary,
            clocks = clocks,
            paused = false,
            settlementRoundId = null,
            settlementEvents = emptyList()
        )

        val publicUpdate = ProtocolPublicUpdate(
            snapshot = publicSnap,
            fromCursor = 60L,
            visibleEvents = listOf(
                ProtocolGameEvent.TileDiscarded(turnNumber = 1, player = 3, tile = protoTile, isRiichiDiscard = false, isTsumogiri = true)
            ),
            isFullSnapshot = true
        )

        val publicItem = ServerTableHost.PublicDispatchItem.Broadcast(publicUpdate, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimatePublicDispatchBytes(publicItem)

        val wireFrame = TcpMessageCodec.toPublicUpdateFrame(publicUpdate)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Public dispatch with long strings estimated bytes ($estimatedBytes) must upper-bound actual wire bytes ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.5,
            "Public dispatch with long strings estimated bytes ($estimatedBytes) must be within 1.5x of actual wire bytes ($actualWireBytes), ratio was $ratio"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds 10000B session roundRecord resultId and finalResult reason against actual wire length`() {
        val dummyTile = Tile.fromTileId(0)
        val view = PlayerView(
            viewerSeat = 0,
            phase = GamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(dummyTile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(13, 13, 13, 13),
            myClosedTiles = List(13) { Tile.fromTileId(it * 4) },
            myDrawnTile = null,
            lastDiscard = null,
            lastDiscardPlayer = null,
            myFuriten = FuritenStatus(),
            legalActions = emptyList(),
            mySleeveTiles = emptyList(),
            myDiscardHistory = emptyList()
        )

        val longResultId = "res-" + "x".repeat(10000)
        val longReason = "reas-" + "y".repeat(10000)

        val sessionView = SessionView(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 1,
            dealerSeat = 0,
            honba = 0,
            roundRecords = listOf(
                PublicRoundRecord(
                    roundId = "round-1",
                    resultId = longResultId,
                    finalScores = listOf(25000, 25000, 25000, 25000),
                    deltas = listOf(0, 0, 0, 0),
                    finalCheatPoints = listOf(10, 10, 10, 10),
                    cheatDeltas = listOf(0, 0, 0, 0)
                )
            ),
            finalResult = PublicSessionResult(
                finalScores = listOf(25000, 25000, 25000, 25000),
                rankedSeats = listOf(0, 1, 2, 3),
                reason = longReason,
                finalCheatPoints = listOf(10, 10, 10, 10)
            )
        )

        val snap = PlayerSnapshot(
            sessionId = "sess-rr-test",
            roundId = "round-1",
            syncEpoch = "epoch-1",
            cursor = 1L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = view,
            decision = PlayerDecision(
                decisionId = "dec-1",
                roundId = "round-1",
                controlEpoch = 1L,
                options = emptyList()
            ),
            cheatPoints = listOf(10, 10, 10, 10),
            session = sessionView,
            controls = emptyList(),
            paused = false,
            settlementRoundId = null,
            settlementEvents = emptyList(),
            preparation = null,
            presentationPreview = null,
            tileMark = null,
            sleeveBloom = null,
            beamSwap = null
        )

        val update = PlayerUpdate(
            snapshot = snap,
            fromCursor = 0L,
            visibleEvents = emptyList()
        )

        val item = ServerTableHost.SeatDispatchItem.Broadcast(update, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimateSeatDispatchBytes(item)

        val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
        val wireFrame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Seat dispatch with 10000B resultId and 10000B reason estimated ($estimatedBytes) must upper-bound actual ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.35,
            "Seat dispatch estimation ratio ($ratio) must be bounded (<= 1.35), estimated=$estimatedBytes, actual=$actualWireBytes"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds preview decisionId, cheat decisionIds, and rawBasePointsExact against actual wire length`() {
        val dummyTile = Tile.fromTileId(0)
        val view = PlayerView(
            viewerSeat = 0,
            phase = GamePhase.CHEAT_WINDOW,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(dummyTile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(13, 13, 13, 13),
            myClosedTiles = List(13) { Tile.fromTileId(100 + it) },
            myDrawnTile = dummyTile,
            lastDiscard = null,
            lastDiscardPlayer = null,
            myFuriten = FuritenStatus(),
            legalActions = emptyList(),
            mySleeveTiles = emptyList(),
            myDiscardHistory = emptyList()
        )

        val longPreviewDecId = "dec-prev-" + "p".repeat(2000)
        val longTmDisabled = "dis-tm-" + "t".repeat(2000)
        val longTmDecId = "dec-tm-" + "m".repeat(2000)
        val longSbDisabled = "dis-sb-" + "s".repeat(2000)
        val longSbDecId = "dec-sb-" + "b".repeat(2000)
        val longBsDisabled = "dis-bs-" + "w".repeat(2000)
        val longBsDecId = "dec-bs-" + "d".repeat(2000)
        val longRawBase = "9".repeat(2000)

        val preview = PlayerPresentationPreview(
            roundId = "round-1",
            decisionId = longPreviewDecId,
            controlEpoch = 1L,
            waits = listOf(dummyTile),
            riichiOptions = emptyList(),
            meldOptions = emptyList()
        )

        val tileMarkView = TileMarkView(
            canApply = false,
            disabledReason = longTmDisabled,
            cost = 10,
            activeTileCount = 0,
            maxActiveTiles = 12,
            selectableTileIds = emptyList(),
            decisionId = longPreviewDecId,
            controlEpoch = 1L,
            stateVersion = 1L,
            activeGroups = emptyList()
        )

        val sleeveBloomView = SleeveBloomView(
            canApply = false,
            disabledReason = longSbDisabled,
            cost = 10,
            drawKind = null,
            decisionId = longPreviewDecId,
            controlEpoch = 1L,
            stateVersion = 1L,
            pendingOffer = SleeveBloomOfferView(
                offerId = "offer-sb-1",
                firstTile = dummyTile,
                secondTile = Tile.fromTileId(1),
                drawKind = SleeveBloomDrawKind.NORMAL,
                deadlineMillis = 5000L,
                decisionId = longPreviewDecId,
                controlEpoch = 1L
            )
        )

        val beamSwapView = BeamSwapView(
            canApply = false,
            disabledReason = longBsDisabled,
            cost = 10,
            window = null,
            decisionId = longPreviewDecId,
            controlEpoch = 1L,
            stateVersion = 1L,
            pendingOffer = BeamSwapOfferView(
                offerId = "offer-bs-1",
                window = BeamSwapWindow.WALL,
                candidates = emptyList(),
                selectableHandTileIds = listOf(100),
                deadlineMillis = 5000L,
                decisionId = longPreviewDecId,
                controlEpoch = 1L
            )
        )

        val scoreResult = ScoreCalculator.ScoreResult(
            fu = 30,
            han = 80,
            basePoints = 8000,
            title = "数え役満",
            isYakuman = true,
            yakumanMultiplier = 1,
            rawBasePoints = Int.MAX_VALUE,
            capReason = "KAZOE_YAKUMAN",
            formulaText = "30符 × 2^(2+80) = $longRawBase → 数え役満 8000",
            rawBasePointsExact = longRawBase
        )

        val breakdown = ScoreBreakdown(
            score = scoreResult,
            yakuLines = listOf("数え役満" to 80),
            paymentLines = listOf("all 8000"),
            yakumanCount = 1,
            honbaAddition = 0,
            riichiAddition = 0,
            totalPoints = 32000,
            fu = FuBreakdown(
                lines = listOf(FuLine("底符", 30)),
                subtotal = 30,
                roundedUpTo = 30,
                finalFu = 30,
                specialNote = "基本符"
            ),
            dora = DoraBreakdown(
                indicatorCount = 0,
                uraCount = 0,
                redCount = 0,
                totalHan = 0,
                indicatorTiles = emptyList(),
                uraTiles = emptyList(),
                redTiles = emptyList(),
                indicatorHits = emptyList(),
                uraHits = emptyList()
            ),
            uraEligible = false,
            isDealer = false,
            isTsumo = true
        )

        val tsumoEvent = GameEvent.TsumoWin(
            turnNumber = 10,
            player = 0,
            winTile = dummyTile,
            yakuList = listOf("数え役満"),
            han = 80,
            fu = 30,
            score = 32000,
            breakdown = breakdown
        )

        val snap = PlayerSnapshot(
            sessionId = "sess-cheat-strings",
            roundId = "round-1",
            syncEpoch = "epoch-1",
            cursor = 1L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = view,
            decision = PlayerDecision(
                decisionId = longPreviewDecId,
                roundId = "round-1",
                controlEpoch = 1L,
                options = emptyList()
            ),
            clocks = emptyList(),
            cheatPoints = listOf(10, 10, 10, 10),
            session = null,
            controls = emptyList(),
            paused = false,
            settlementRoundId = "round-1",
            settlementEvents = listOf(tsumoEvent),
            preparation = null,
            presentationPreview = preview,
            tileMark = tileMarkView,
            sleeveBloom = sleeveBloomView,
            beamSwap = beamSwapView
        )

        val update = PlayerUpdate(
            snapshot = snap,
            fromCursor = 0L,
            visibleEvents = listOf(tsumoEvent)
        )

        val item = ServerTableHost.SeatDispatchItem.Broadcast(update, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimateSeatDispatchBytes(item)

        val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
        val wireFrame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Seat dispatch with cheat/preview strings estimated ($estimatedBytes) must upper-bound actual ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.35,
            "Seat dispatch estimation ratio ($ratio) must be bounded (<= 1.35), estimated=$estimatedBytes, actual=$actualWireBytes"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds public roundRecord resultId and abortive draw reason against actual wire length`() {
        val protoTile = ProtocolTile.fromTileId(0)
        val board = ProtocolPublicBoardView(
            phase = ProtocolGamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(protoTile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(13, 13, 13, 13),
            lastDiscard = null,
            lastDiscardPlayer = null
        )

        val longResultId = "res-pub-" + "z".repeat(10000)
        val longAbortReason = "abort-draw-" + "a".repeat(4000)

        val sessionSummary = ProtocolPublicSessionSummary(
            lifecycle = "FINISHED",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 2,
            dealerSeat = 1,
            honba = 1,
            roundRecords = listOf(
                ProtocolPublicScoreRecord(
                    roundId = "round-1",
                    resultId = longResultId,
                    finalScores = listOf(25000, 25000, 25000, 25000),
                    deltas = listOf(0, 0, 0, 0),
                    remainingRiichiSticks = 0,
                    outcome = ProtocolRoundOutcome.AbortiveDraw(longAbortReason)
                )
            ),
            finalResult = ProtocolPublicMatchResult(
                finalScores = listOf(25000, 25000, 25000, 25000),
                rankedSeats = listOf(0, 1, 2, 3),
                reason = "BANKRUPTCY",
                remainingRiichiSticksTransferredTo = 0
            )
        )

        val publicSnap = ProtocolPublicSnapshot(
            sessionId = "pub-session-records-test",
            sessionVersion = 2L,
            roundId = "round-2",
            roundVersion = 40L,
            syncEpoch = "epoch-1",
            cursor = 1L,
            board = board,
            session = sessionSummary,
            clocks = emptyList(),
            paused = false,
            settlementRoundId = null,
            settlementEvents = emptyList()
        )

        val publicUpdate = ProtocolPublicUpdate(
            snapshot = publicSnap,
            fromCursor = 1L,
            visibleEvents = emptyList(),
            isFullSnapshot = true
        )

        val publicItem = ServerTableHost.PublicDispatchItem.Broadcast(publicUpdate, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimatePublicDispatchBytes(publicItem)

        val wireFrame = TcpMessageCodec.toPublicUpdateFrame(publicUpdate)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Public dispatch with 10000B resultId and abort reason estimated ($estimatedBytes) must upper-bound actual ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.35,
            "Public dispatch estimation ratio ($ratio) must be bounded (<= 1.35), estimated=$estimatedBytes, actual=$actualWireBytes"
        )
    }

    @Test
    fun `estimateStringUpperBytes matches or upper bounds gson json encoding across html chars unicode and escapes`() {
        val testStrings = listOf(
            null,
            "",
            "simple ascii text 123",
            "\"quotes\" and \\backslashes\\",
            "<tag> & 'single quotes' = \"double quotes\"",
            "<".repeat(10000),
            ">".repeat(5000),
            "&".repeat(3000),
            "=".repeat(2000),
            "'".repeat(4000),
            "\u2028".repeat(1000),
            "\u2029".repeat(1000),
            "mixed <>&=' with \u2028 and \u2029 separators",
            "CJK: 麻将・リーチ・ドラドラ・一发・ツモ・和了",
            "Emoji: 🀄🀐🀑🀒🎉🚀💡",
            "\u0000\u0001\u0002\t\n\r\u001f control chars",
            "<".repeat(2000) + "\u2028".repeat(1000) + "CJK: 断幺九" + "🀄".repeat(500)
        )

        for (s in testStrings) {
            val estimated = ServerTableHost.estimateStringUpperBytes(s)
            val json = ApiMessageCodec.gson.toJson(s)
            val actualBytes = json.toByteArray(Charsets.UTF_8).size.toLong()
            assertTrue(
                estimated >= actualBytes,
                "Estimated ($estimated) must >= actual JSON bytes ($actualBytes) for string sample of length ${s?.length}"
            )
        }
    }

    @Test
    fun `structural byte estimator realistically upper bounds html safe chars and unicode separators in seat dispatch`() {
        val baseHand = List(13) { i -> Tile.fromTileId(i) }
        val view = PlayerView(
            viewerSeat = 0,
            phase = GamePhase.CHEAT_WINDOW,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(Tile.fromTileId(0)),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(13, 13, 13, 13),
            myClosedTiles = baseHand,
            myDrawnTile = Tile.fromTileId(13),
            mySleeveTiles = listOf(Tile.fromTileId(14)),
            myDiscardHistory = emptyList(),
            legalActions = emptyList(),
            lastDiscard = null,
            lastDiscardPlayer = null,
            myFuriten = FuritenStatus()
        )

        val htmlResultId = "<".repeat(5000) + ">".repeat(5000)
        val htmlAndSepReason = "<>&='".repeat(1500) + "\u2028\u2029".repeat(500)

        val sessionView = SessionView(
            lifecycle = "FINISHED",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 2,
            dealerSeat = 1,
            honba = 1,
            roundRecords = listOf(
                PublicRoundRecord(
                    roundId = "round-1",
                    resultId = htmlResultId,
                    finalScores = listOf(25000, 25000, 25000, 25000),
                    deltas = listOf(0, 0, 0, 0),
                    finalCheatPoints = listOf(10, 10, 10, 10),
                    cheatDeltas = listOf(0, 0, 0, 0)
                )
            ),
            finalResult = PublicSessionResult(
                finalScores = listOf(25000, 25000, 25000, 25000),
                rankedSeats = listOf(0, 1, 2, 3),
                reason = htmlAndSepReason,
                finalCheatPoints = listOf(10, 10, 10, 10)
            )
        )

        val snap = PlayerSnapshot(
            sessionId = "session-seat-html-test",
            sessionVersion = 1L,
            roundId = "round-1",
            roundVersion = 10L,
            syncEpoch = "epoch-1",
            cursor = 1L,
            view = view,
            decision = PlayerDecision(
                decisionId = "dec-1",
                roundId = "round-1",
                controlEpoch = 1L,
                options = emptyList()
            ),
            clocks = emptyList(),
            cheatPoints = listOf(10, 10, 10, 10),
            session = sessionView,
            controls = emptyList(),
            paused = false,
            settlementRoundId = null,
            settlementEvents = emptyList(),
            preparation = null,
            presentationPreview = null,
            tileMark = null,
            sleeveBloom = null,
            beamSwap = null
        )

        val update = PlayerUpdate(
            snapshot = snap,
            fromCursor = 1L,
            visibleEvents = emptyList()
        )

        val item = ServerTableHost.SeatDispatchItem.Broadcast(update, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimateSeatDispatchBytes(item)

        val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
        val wireFrame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Seat dispatch with HTML-escaped and unicode separators estimated ($estimatedBytes) must upper-bound actual ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.35,
            "Seat dispatch estimation ratio ($ratio) must be bounded (<= 1.35), estimated=$estimatedBytes, actual=$actualWireBytes"
        )
    }

    @Test
    fun `structural byte estimator realistically upper bounds html safe chars and unicode separators in public dispatch`() {
        val protoTile = ProtocolTile.fromTileId(0)
        val board = ProtocolPublicBoardView(
            phase = ProtocolGamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(protoTile),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = List(4) { emptyList() },
            melds = List(4) { emptyList() },
            handTileCounts = listOf(13, 13, 13, 13),
            lastDiscard = null,
            lastDiscardPlayer = null
        )

        val htmlResultId = "<".repeat(10000)
        val htmlAbortReason = "<>&='".repeat(1000) + "\u2028\u2029".repeat(500)

        val sessionSummary = ProtocolPublicSessionSummary(
            lifecycle = "FINISHED",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 2,
            dealerSeat = 1,
            honba = 1,
            roundRecords = listOf(
                ProtocolPublicScoreRecord(
                    roundId = "round-1",
                    resultId = htmlResultId,
                    finalScores = listOf(25000, 25000, 25000, 25000),
                    deltas = listOf(0, 0, 0, 0),
                    remainingRiichiSticks = 0,
                    outcome = ProtocolRoundOutcome.AbortiveDraw(htmlAbortReason)
                )
            ),
            finalResult = ProtocolPublicMatchResult(
                finalScores = listOf(25000, 25000, 25000, 25000),
                rankedSeats = listOf(0, 1, 2, 3),
                reason = "BANKRUPTCY",
                remainingRiichiSticksTransferredTo = 0
            )
        )

        val publicSnap = ProtocolPublicSnapshot(
            sessionId = "pub-session-<>&='-" + "<".repeat(2000) + "\u2028".repeat(1000),
            sessionVersion = 2L,
            roundId = "round-2",
            roundVersion = 40L,
            syncEpoch = "epoch-1",
            cursor = 1L,
            board = board,
            session = sessionSummary,
            clocks = emptyList(),
            paused = false,
            settlementRoundId = null,
            settlementEvents = emptyList()
        )

        val publicUpdate = ProtocolPublicUpdate(
            snapshot = publicSnap,
            fromCursor = 1L,
            visibleEvents = emptyList(),
            isFullSnapshot = true
        )

        val publicItem = ServerTableHost.PublicDispatchItem.Broadcast(publicUpdate, isFullSnapshot = true)
        val estimatedBytes = ServerTableHost.estimatePublicDispatchBytes(publicItem)

        val wireFrame = TcpMessageCodec.toPublicUpdateFrame(publicUpdate)
        val actualWireBytes = (TcpWire.HEADER_SIZE + wireFrame.payload.size).toLong()

        assertTrue(
            estimatedBytes >= actualWireBytes,
            "Public dispatch with HTML-escaped and unicode separators estimated ($estimatedBytes) must upper-bound actual ($actualWireBytes)"
        )
        val ratio = estimatedBytes.toDouble() / actualWireBytes.toDouble()
        assertTrue(
            ratio <= 1.35,
            "Public dispatch estimation ratio ($ratio) must be bounded (<= 1.35), estimated=$estimatedBytes, actual=$actualWireBytes"
        )
    }
}
