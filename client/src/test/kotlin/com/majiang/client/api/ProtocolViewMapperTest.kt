package com.majiang.client.api

import com.majiang.server.api.RoomSummary
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionOption
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolCheatEconomyConfig
import com.majiang.transport.api.ProtocolClock
import com.majiang.transport.api.ProtocolDoraBreakdown
import com.majiang.transport.api.ProtocolDoraIndicatorHit
import com.majiang.transport.api.ProtocolDrawReason
import com.majiang.transport.api.ProtocolFuBreakdown
import com.majiang.transport.api.ProtocolFuLine
import com.majiang.transport.api.ProtocolFuritenStatus
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolGamePhase
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolMatchConfig
import com.majiang.transport.api.ProtocolMeld
import com.majiang.transport.api.ProtocolMeldType
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPlayerView
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolScoreBreakdown
import com.majiang.transport.api.ProtocolScoreResult
import com.majiang.transport.api.ProtocolSeatControl
import com.majiang.transport.api.ProtocolShuffleConfig
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolShufflePreparation
import com.majiang.transport.api.ProtocolShufflePreparationStatus
import com.majiang.transport.api.ProtocolShuffleSelection
import com.majiang.transport.api.ProtocolShuffleTarget
import com.majiang.transport.api.ProtocolPublicBoardView
import com.majiang.server.api.PublicCheckpoint as ProtocolPublicCheckpoint
import com.majiang.transport.api.ProtocolPublicMatchResult
import com.majiang.transport.api.ProtocolPublicScoreRecord
import com.majiang.transport.api.ProtocolPublicSessionSummary
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolRoundOutcome
import com.majiang.transport.api.ProtocolSuit
import com.majiang.transport.api.ProtocolTile
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import com.majiang.client.mapping.ProtocolViewMapper

class ProtocolViewMapperTest {

    @Test
    fun `test suit and tile mapping roundtrip`() {
        for (suit in ProtocolSuit.values()) {
            val view = ProtocolViewMapper.mapSuit(suit)
            val back = ProtocolViewMapper.toProtocolSuit(view)
            assertEquals(suit, back)
        }

        for (id in 0..135) {
            val protoTile = ProtocolTile.fromTileId(id)
            val viewTile = ProtocolViewMapper.mapTile(protoTile)

            assertEquals(id, viewTile.tileId)
            assertEquals(protoTile.suit.name, viewTile.suit.name)
            assertEquals(protoTile.rank, viewTile.rank)
            assertEquals(protoTile.isRedFive, viewTile.isRedFive)

            val back = ProtocolViewMapper.toProtocolTile(viewTile)
            assertEquals(protoTile.tileId, back.tileId)
            assertEquals(protoTile.suit, back.suit)
            assertEquals(protoTile.rank, back.rank)
            assertEquals(protoTile.isRedFive, back.isRedFive)
        }
    }

    @Test
    fun `test red fives are preserved`() {
        val redMan = ProtocolTile.fromTileId(19)
        assertTrue(redMan.isRedFive)
        val viewMan = ProtocolViewMapper.mapTile(redMan)
        assertTrue(viewMan.isRedFive)
        val backMan = ProtocolViewMapper.toProtocolTile(viewMan)
        assertTrue(backMan.isRedFive)

        val regularMan = ProtocolTile.fromTileId(16)
        assertFalse(regularMan.isRedFive)
        val viewRegular = ProtocolViewMapper.mapTile(regularMan)
        assertFalse(viewRegular.isRedFive)
        val backRegular = ProtocolViewMapper.toProtocolTile(viewRegular)
        assertFalse(backRegular.isRedFive)
    }

    @Test
    fun `test meld mapping roundtrip`() {
        for (mt in ProtocolMeldType.values()) {
            val t0 = ProtocolTile.fromTileId(0)
            val t1 = ProtocolTile.fromTileId(1)
            val t2 = ProtocolTile.fromTileId(2)
            val protoMeld = ProtocolMeld(
                type = mt,
                tiles = listOf(t0, t1, t2),
                fromPlayer = 2,
                calledTile = t0
            )
            val viewMeld = ProtocolViewMapper.mapMeld(protoMeld)
            assertEquals(3, viewMeld.tiles.size)
            assertEquals(2, viewMeld.fromPlayer)
            assertEquals(0, viewMeld.calledTile.tileId)

            val backType = ProtocolViewMapper.toProtocolMeldType(viewMeld.type)
            assertEquals(mt, backType)
        }
    }

    @Test
    fun `test all 14 legal actions mapped to LegalActionView`() {
        val t0 = ProtocolTile.fromTileId(0)
        val t1 = ProtocolTile.fromTileId(4)
        val t2 = ProtocolTile.fromTileId(8)
        val meld = ProtocolMeld(ProtocolMeldType.CHI, listOf(t0, t1, t2), 1, t0)

        val actions: List<ProtocolLegalAction> = listOf(
            ProtocolLegalAction.StartStacking(),
            ProtocolLegalAction.DealComplete,
            ProtocolLegalAction.DrawTile(0),
            ProtocolLegalAction.DiscardTile(0, t0),
            ProtocolLegalAction.DeclareChi(0, meld),
            ProtocolLegalAction.DeclarePon(0, listOf(t0, t1)),
            ProtocolLegalAction.DeclareKan(0, listOf(t0, t1, t2), ProtocolMeldType.OPEN_KAN),
            ProtocolLegalAction.DeclareRiichi(0, t0),
            ProtocolLegalAction.DeclareTsumo(0),
            ProtocolLegalAction.DeclareRon(0),
            ProtocolLegalAction.Pass(0),
            ProtocolLegalAction.DeclareAbortiveDraw(0),
            ProtocolLegalAction.DeclareAgariYame(0)
        )

        for (action in actions) {
            val view = ProtocolViewMapper.mapLegalAction(action)
            assertNotNull(view)
            when (action) {
                is ProtocolLegalAction.StartStacking -> assertTrue(view is LegalActionView.StartStacking)
                is ProtocolLegalAction.DealComplete -> assertTrue(view is LegalActionView.DealComplete)
                is ProtocolLegalAction.DrawTile -> assertTrue(view is LegalActionView.DrawTile)
                is ProtocolLegalAction.DiscardTile -> assertTrue(view is LegalActionView.DiscardTile)
                is ProtocolLegalAction.DeclareChi -> assertTrue(view is LegalActionView.DeclareChi)
                is ProtocolLegalAction.DeclarePon -> assertTrue(view is LegalActionView.DeclarePon)
                is ProtocolLegalAction.DeclareKan -> assertTrue(view is LegalActionView.DeclareKan)
                is ProtocolLegalAction.DeclareRiichi -> assertTrue(view is LegalActionView.DeclareRiichi)
                is ProtocolLegalAction.DeclareTsumo -> assertTrue(view is LegalActionView.DeclareTsumo)
                is ProtocolLegalAction.DeclareRon -> assertTrue(view is LegalActionView.DeclareRon)
                is ProtocolLegalAction.Pass -> assertTrue(view is LegalActionView.Pass)
                is ProtocolLegalAction.DeclareAbortiveDraw -> assertTrue(view is LegalActionView.DeclareAbortiveDraw)
                is ProtocolLegalAction.DeclareAgariYame -> assertTrue(view is LegalActionView.DeclareAgariYame)
            }
        }
    }

    @Test
    fun `test client action choice to protocol conversion`() {
        val clientChoice = ActionChoice("req-1", "round-1", "dec-1", 3L, "opt-1")
        val proto = ProtocolViewMapper.toProtocolActionChoice(clientChoice)
        assertEquals("req-1", proto.requestId)
        assertEquals("round-1", proto.roundId)
        assertEquals("dec-1", proto.decisionId)
        assertEquals(3L, proto.controlEpoch)
        assertEquals("opt-1", proto.optionId)
    }

    @Test
    fun `test client shuffle plan to protocol conversion`() {
        val clientPlan = ShufflePlan(
            requestId = "plan-1",
            roundId = "round-1",
            controlEpoch = 1L,
            selection = ShuffleSelection(
                initialTargets = setOf(ShuffleTarget.CHINITSU_MAN),
                drawTargets = setOf(ShuffleTarget.DAISANGEN)
            )
        )
        val proto = ProtocolViewMapper.toProtocolShufflePlan(clientPlan)
        assertEquals("plan-1", proto.requestId)
        assertEquals("round-1", proto.roundId)
        assertEquals(1L, proto.controlEpoch)
        assertEquals(setOf(ProtocolShuffleTarget.CHINITSU_MAN), proto.selection.initialTargets)
        assertEquals(setOf(ProtocolShuffleTarget.DAISANGEN), proto.selection.drawTargets)
    }

    @Test
    fun `test client match config to protocol conversion`() {
        val clientConfig = MatchConfig(
            playerCount = 4,
            handsPerWind = 2,
            winds = listOf(1),
            startingScores = listOf(30000, 30000, 20000, 20000),
            allowExtension = false,
            allowAgariYame = true,
            bankruptcyEnabled = false,
            bankruptcyThreshold = 100,
            bankruptcyInclusive = true,
            targetScore = 35000,
            tieBreakSeats = listOf(3, 2, 1, 0),
            cheatEconomy = CheatEconomyConfig(enabled = true, initialPoints = listOf(10, 20, 30, 40)),
            shuffleConfig = ShuffleConfig(enabled = true, selectionMillis = 15000L)
        )
        val proto = ProtocolViewMapper.toProtocolMatchConfig(clientConfig)
        assertEquals(4, proto.playerCount)
        assertEquals(2, proto.handsPerWind)
        assertEquals(listOf(1), proto.winds)
        assertEquals(listOf(30000, 30000, 20000, 20000), proto.startingScores)
        assertFalse(proto.allowExtension)
        assertTrue(proto.allowAgariYame)
        assertFalse(proto.bankruptcyEnabled)
        assertEquals(100, proto.bankruptcyThreshold)
        assertTrue(proto.bankruptcyInclusive)
        assertEquals(35000, proto.targetScore)
        assertEquals(listOf(3, 2, 1, 0), proto.tieBreakSeats)
        assertTrue(proto.cheatEconomy.enabled)
        assertEquals(listOf(10, 20, 30, 40), proto.cheatEconomy.initialPoints)
        assertTrue(proto.shuffleConfig.enabled)
        assertEquals(15000L, proto.shuffleConfig.selectionMillis)
    }

    @Test
    fun `test score breakdown mapping fidelity`() {
        val t0 = ProtocolTile.fromTileId(0)
        val red = ProtocolTile.fromTileId(19)
        val protoScore = ProtocolScoreBreakdown(
            yakuLines = listOf(Pair("Riichi", 1), Pair("Dora", 2)),
            yakumanCount = 0,
            dora = ProtocolDoraBreakdown(
                indicatorCount = 1,
                uraCount = 0,
                redCount = 1,
                totalHan = 2,
                indicatorTiles = listOf(t0),
                uraTiles = emptyList(),
                redTiles = listOf(red),
                indicatorHits = listOf(ProtocolDoraIndicatorHit(t0, t0, listOf(t0))),
                uraHits = emptyList()
            ),
            fu = ProtocolFuBreakdown(
                lines = listOf(ProtocolFuLine("Menzen Ron", 10)),
                subtotal = 30,
                roundedUpTo = 30,
                finalFu = 30,
                specialNote = null
            ),
            score = ProtocolScoreResult(fu = 30, han = 3, basePoints = 1000, title = "3900", isYakuman = false, yakumanMultiplier = 0),
            paymentLines = listOf("Seat 1 pays 3900"),
            honbaAddition = 0,
            riichiAddition = 0,
            totalPoints = 3900,
            uraEligible = false,
            isDealer = false,
            isTsumo = false
        )

        val view = ProtocolViewMapper.mapScoreBreakdown(protoScore)
        assertEquals(2, view.yakuLines.size)
        assertEquals("Riichi", view.yakuLines[0].first)
        assertEquals(1, view.yakuLines[0].second)
        assertEquals(30, view.fu.finalFu)
        assertEquals(1, view.dora.indicatorHits.size)
        assertEquals(1, view.dora.redTiles.size)
        assertEquals(3900, view.totalPoints)
        assertFalse(view.isDealer)
        assertFalse(view.isTsumo)
    }

    @Test
    fun `test receipt and capabilities mapping`() {
        val receipt = ProtocolActionReceipt(
            requestId = "req-1",
            status = ProtocolReceiptStatus.APPLIED,
            cursor = 10L
        )
        val clientReceipt = ProtocolViewMapper.mapReceipt(receipt)
        assertEquals("req-1", clientReceipt.requestId)
        assertEquals(ReceiptStatus.APPLIED, clientReceipt.status)
        assertEquals(10L, clientReceipt.cursor)

        val caps = ProtocolAccessCapabilities(
            protocolVersion = 4,
            supportedPlayerCounts = setOf(4),
            remoteTransport = true,
            persistence = false,
            resumption = true
        )
        val clientCaps = ProtocolViewMapper.mapCapabilities(caps)
        assertEquals(4, clientCaps.protocolVersion)
        assertEquals(setOf(4), clientCaps.supportedPlayerCounts)
        assertTrue(clientCaps.remoteTransport)
        assertTrue(clientCaps.resumption)
    }

    @Test
    fun `test snapshot and update mapping defensive copying`() {
        val t0 = ProtocolTile.fromTileId(0)
        val view = ProtocolPlayerView(
            viewerSeat = 0,
            phase = ProtocolGamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 0,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 70,
            doraIndicators = listOf(t0),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
            melds = listOf(emptyList(), emptyList(), emptyList(), emptyList()),
            handTileCounts = listOf(14, 13, 13, 13),
            myClosedTiles = listOf(t0),
            myDrawnTile = null,
            legalActions = listOf(ProtocolLegalAction.DiscardTile(0, t0))
        )
        val decision = ProtocolPlayerDecision(
            roundId = "round-1",
            decisionId = "dec-1",
            controlEpoch = 1L,
            options = listOf(ProtocolActionOption("opt-1", ProtocolLegalAction.DiscardTile(0, t0)))
        )
        val snapshot = ProtocolPlayerSnapshot(
            sessionId = "session-1",
            roundId = "round-1",
            syncEpoch = "epoch-1",
            cursor = 5L,
            sessionVersion = 1L,
            roundVersion = 1L,
            view = view,
            decision = decision
        )
        val update = ProtocolPlayerUpdate(
            snapshot = snapshot,
            fromCursor = 4L,
            visibleEvents = listOf(ProtocolGameEvent.TileDrawn(turnNumber = 1, player = 0, tile = t0, wallRemaining = 69))
        )

        val updateView = ProtocolViewMapper.mapUpdate(update)
        assertEquals(4L, updateView.fromCursor)
        assertEquals(5L, updateView.snapshot.cursor)
        assertEquals(1, updateView.snapshot.view.myClosedTiles.size)
        assertEquals(1, updateView.visibleEvents.size)
        assertTrue(updateView.visibleEvents[0] is GameEventView.TileDrawn)
    }

    @Test
    fun `test room summary mapping`() {
        val summary = RoomSummary("ROOM1", "Room 1", "Host", 1, 4, false, "Standard")
        val view = ProtocolViewMapper.mapRoomSummary(summary)
        assertEquals("ROOM1", view.roomId)
        assertEquals("Room 1", view.roomName)
        assertEquals("Host", view.hostPlayerName)
        assertEquals(1, view.playerCount)
        assertEquals(4, view.maxPlayers)
        assertFalse(view.matchStarted)
        assertEquals("Standard", view.configSummary)

        val list = ProtocolViewMapper.mapRoomSummaries(listOf(summary))
        assertEquals(1, list.size)
        assertEquals("ROOM1", list[0].roomId)
    }

    @Test
    fun `test public checkpoint mapping roundtrip`() {
        val protoCp = ProtocolPublicCheckpoint(sessionId = "sess-1", syncEpoch = "epoch-1", cursor = 42L)
        val clientCp = ProtocolViewMapper.mapPublicCheckpoint(protoCp)
        assertEquals("sess-1", clientCp.sessionId)
        assertEquals("epoch-1", clientCp.syncEpoch)
        assertEquals(42L, clientCp.cursor)

        val back = ProtocolViewMapper.toProtocolPublicCheckpoint(clientCp)
        assertEquals(protoCp, back)
    }

    @Test
    fun `test public round outcome variants mapping roundtrip`() {
        val outcomes = listOf(
            ProtocolRoundOutcome.DealerWin(0),
            ProtocolRoundOutcome.NonDealerWin(2),
            ProtocolRoundOutcome.Ron(winner = 1, loser = 3),
            ProtocolRoundOutcome.Tsumo(winner = 2),
            ProtocolRoundOutcome.ExhaustiveDraw(tenpaiSeats = setOf(0, 2)),
            ProtocolRoundOutcome.AbortiveDraw("KYUUSHU_KYUUHAI")
        )

        for (proto in outcomes) {
            val mapped = ProtocolViewMapper.mapPublicRoundOutcome(proto)
            val back = ProtocolViewMapper.toProtocolRoundOutcome(mapped)
            assertEquals(proto, back)
        }
    }

    @Test
    fun `test public score record mapping roundtrip`() {
        val protoRecord = ProtocolPublicScoreRecord(
            roundId = "round-1",
            resultId = "res-1",
            finalScores = listOf(26000, 24000, 25000, 25000),
            deltas = listOf(1000, -1000, 0, 0),
            remainingRiichiSticks = 1,
            outcome = ProtocolRoundOutcome.Ron(0, 1)
        )
        val mapped = ProtocolViewMapper.mapPublicScoreRecord(protoRecord)
        assertEquals("round-1", mapped.roundId)
        assertEquals("res-1", mapped.resultId)
        assertEquals(listOf(26000, 24000, 25000, 25000), mapped.finalScores)
        assertEquals(listOf(1000, -1000, 0, 0), mapped.deltas)
        assertEquals(1, mapped.remainingRiichiSticks)
        assertTrue(mapped.outcome is PublicRoundOutcomeView.Ron)

        val back = ProtocolViewMapper.toProtocolPublicScoreRecord(mapped)
        assertEquals(protoRecord, back)
    }

    @Test
    fun `test public match result mapping roundtrip`() {
        val protoResult = ProtocolPublicMatchResult(
            finalScores = listOf(35000, 25000, 20000, 20000),
            rankedSeats = listOf(0, 1, 2, 3),
            reason = "NORMAL_COMPLETION",
            remainingRiichiSticksTransferredTo = 0
        )
        val mapped = ProtocolViewMapper.mapPublicMatchResult(protoResult)
        assertEquals(listOf(35000, 25000, 20000, 20000), mapped.finalScores)
        assertEquals(listOf(0, 1, 2, 3), mapped.rankedSeats)
        assertEquals("NORMAL_COMPLETION", mapped.reason)
        assertEquals(0, mapped.remainingRiichiSticksTransferredTo)

        val back = ProtocolViewMapper.toProtocolPublicMatchResult(mapped)
        assertEquals(protoResult, back)
    }

    @Test
    fun `test public session summary mapping roundtrip`() {
        val protoSummary = ProtocolPublicSessionSummary(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 2,
            dealerSeat = 1,
            honba = 0,
            roundRecords = emptyList(),
            finalResult = null
        )
        val mapped = ProtocolViewMapper.mapPublicSessionSummary(protoSummary)
        assertEquals("ROUND_ACTIVE", mapped.lifecycle)
        assertEquals(2, mapped.roundNumber)
        assertEquals(1, mapped.dealerSeat)

        val back = ProtocolViewMapper.toProtocolPublicSessionSummary(mapped)
        assertEquals(protoSummary, back)
    }

    @Test
    fun `test public board view mapping roundtrip and defensive copying`() {
        val t0 = ProtocolTile.fromTileId(0)
        val t1 = ProtocolTile.fromTileId(4)
        val t2 = ProtocolTile.fromTileId(8)
        val meld = ProtocolMeld(ProtocolMeldType.CHI, listOf(t0, t1, t2), 1, t0)
        val protoBoard = ProtocolPublicBoardView(
            phase = ProtocolGamePhase.DISCARD,
            roundWind = 1,
            roundNumber = 1,
            honbaSticks = 1,
            riichiSticks = 0,
            dealerSeat = 0,
            currentTurnPlayer = 0,
            wallRemaining = 60,
            doraIndicators = listOf(t0),
            scores = listOf(25000, 25000, 25000, 25000),
            riichiStatus = listOf(false, false, false, false),
            discards = listOf(listOf(t0), emptyList(), emptyList(), emptyList()),
            melds = listOf(listOf(meld), emptyList(), emptyList(), emptyList()),
            handTileCounts = listOf(14, 13, 13, 13),
            lastDiscard = t0,
            lastDiscardPlayer = 0
        )
        val mapped = ProtocolViewMapper.mapPublicBoardView(protoBoard)
        assertEquals(GamePhaseView.DISCARD, mapped.phase)
        assertEquals(60, mapped.wallRemaining)
        assertEquals(listOf(14, 13, 13, 13), mapped.handTileCounts)
        assertEquals(1, mapped.discards[0].size)
        assertEquals(1, mapped.melds[0].size)
        assertEquals(0, mapped.lastDiscard?.tileId)
        assertEquals(0, mapped.lastDiscardPlayer)

        val back = ProtocolViewMapper.toProtocolPublicBoardView(mapped)
        assertEquals(protoBoard, back)
    }

    @Test
    fun `test public snapshot and update mapping roundtrip`() {
        val t0 = ProtocolTile.fromTileId(0)
        val protoSummary = ProtocolPublicSessionSummary(
            lifecycle = "ROUND_ACTIVE",
            scores = listOf(25000, 25000, 25000, 25000),
            roundWind = 1,
            roundNumber = 1,
            dealerSeat = 0,
            honba = 0,
            roundRecords = emptyList(),
            finalResult = null
        )
        val clock = ProtocolClock(seat = 0, opensAt = 1000L, deadline = 5000L, reserve = 20000L, serverNow = 1000L)
        val protoSnap = ProtocolPublicSnapshot(
            sessionId = "sess-1",
            sessionVersion = 1L,
            roundId = "round-1",
            roundVersion = 1L,
            syncEpoch = "epoch-1",
            cursor = 10L,
            board = null,
            session = protoSummary,
            clocks = listOf(clock),
            paused = false,
            settlementRoundId = null,
            settlementEvents = listOf(ProtocolGameEvent.DealingComplete(turnNumber = 0))
        )
        val mappedSnap = ProtocolViewMapper.mapPublicSnapshot(protoSnap)
        assertEquals("sess-1", mappedSnap.sessionId)
        assertEquals(10L, mappedSnap.cursor)
        assertEquals(1, mappedSnap.clocks.size)
        assertEquals(1, mappedSnap.settlementEvents.size)

        val backSnap = ProtocolViewMapper.toProtocolPublicSnapshot(mappedSnap)
        assertEquals(protoSnap, backSnap)

        // 普通连续更新 (cursor == fromCursor + 1)
        val protoUpdate = ProtocolPublicUpdate(
            snapshot = protoSnap,
            fromCursor = 9L,
            visibleEvents = listOf(ProtocolGameEvent.TileDiscarded(1, 0, t0, false, false)),
            isFullSnapshot = false
        )
        val mappedUpdate = ProtocolViewMapper.mapPublicUpdate(protoUpdate)
        assertEquals(9L, mappedUpdate.fromCursor)
        assertEquals(10L, mappedUpdate.snapshot.cursor)
        assertFalse(mappedUpdate.isFullSnapshot)
        assertEquals(1, mappedUpdate.visibleEvents.size)

        val backUpdate = ProtocolViewMapper.toProtocolPublicUpdate(mappedUpdate)
        assertEquals(protoUpdate, backUpdate)

        // 全量快照基线 (fromCursor == cursor)
        val fullProtoUpdate = ProtocolPublicUpdate(
            snapshot = protoSnap,
            fromCursor = 10L,
            visibleEvents = emptyList(),
            isFullSnapshot = true
        )
        val mappedFullUpdate = ProtocolViewMapper.mapPublicUpdate(fullProtoUpdate)
        assertEquals(10L, mappedFullUpdate.fromCursor)
        assertEquals(10L, mappedFullUpdate.snapshot.cursor)
        assertTrue(mappedFullUpdate.isFullSnapshot)

        val backFullUpdate = ProtocolViewMapper.toProtocolPublicUpdate(mappedFullUpdate)
        assertEquals(fullProtoUpdate, backFullUpdate)
    }

    @Test
    fun `test validatePublicGameEventView whitelist enforcement`() {
        val t0 = TileView(0, SuitView.MAN, 1)
        val allowedEvents = listOf(
            GameEventView.StackingStarted(1),
            GameEventView.WallBuilt(1, 136),
            GameEventView.DealingComplete(1),
            GameEventView.TileDiscarded(1, 0, t0, false, false),
            GameEventView.ChiDeclared(1, 0, MeldView(MeldTypeView.CHI, listOf(t0), 1, t0), 1),
            GameEventView.PonDeclared(1, 0, MeldView(MeldTypeView.PON, listOf(t0), 1, t0), 1),
            GameEventView.KanDeclared(1, 0, MeldView(MeldTypeView.OPEN_KAN, listOf(t0), 1, t0), MeldTypeView.OPEN_KAN),
            GameEventView.RiichiDeclared(1, 0, t0, false),
            GameEventView.TsumoWin(1, 0, t0, emptyList(), 1, 30, 1000, null),
            GameEventView.RonWin(1, 0, 1, t0, emptyList(), 1, 30, 1000, null),
            GameEventView.DoraRevealed(1, t0, false),
            GameEventView.DrawGame(1, DrawReasonView.EXHAUSTIVE, emptyList()),
            GameEventView.ScoreSettled(1, emptyMap(), listOf(25000, 25000, 25000, 25000)),
            GameEventView.RoundEnded(1, 1, 1),
            GameEventView.MatchEnded(1, listOf(25000, 25000, 25000, 25000), 0)
        )

        for (event in allowedEvents) {
            val validated = validatePublicGameEventView(event)
            assertEquals(event, validated)
        }

        // 禁止的私密事件应抛出 IllegalArgumentException
        val forbiddenEvents = listOf(
            GameEventView.TileDealt(1, 0, t0),
            GameEventView.TileDrawn(1, 0, t0, 69),
            GameEventView.RinshanDraw(1, 0, t0),
            GameEventView.CheatPointsChanged(1, 0, -10, 90)
        )

        for (event in forbiddenEvents) {
            assertThrows(IllegalArgumentException::class.java) {
                validatePublicGameEventView(event)
            }
        }
    }
}
