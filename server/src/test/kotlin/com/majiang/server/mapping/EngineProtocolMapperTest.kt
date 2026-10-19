package com.majiang.server.mapping

import com.majiang.access.AccessCapabilities
import com.majiang.access.ActionReceipt
import com.majiang.access.ClockView
import com.majiang.access.PlayerSnapshot
import com.majiang.access.PlayerUpdate
import com.majiang.access.PublicRoundRecord
import com.majiang.access.PublicSessionResult
import com.majiang.access.ReceiptStatus
import com.majiang.access.SeatControlView
import com.majiang.access.SessionView
import com.majiang.application.ActionChoice
import com.majiang.application.ActionOption
import com.majiang.application.PlayerDecision
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.DrawReason
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.rules.DoraBreakdown
import com.majiang.rules.DoraIndicatorHit
import com.majiang.rules.FuBreakdown
import com.majiang.rules.FuLine
import com.majiang.rules.FuritenStatus
import com.majiang.rules.ScoreBreakdown
import com.majiang.rules.ScoreCalculator
import com.majiang.session.PlayerView
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShufflePreparationStatus
import com.majiang.stacking.ShufflePreparationView
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolMeld
import com.majiang.transport.api.ProtocolMeldType
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolScoreBreakdown
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolSuit
import com.majiang.transport.api.ProtocolTile
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * [EngineProtocolMapper] 双向保真映射与防御性拷贝测试
 */
class EngineProtocolMapperTest {

    @Test
    fun `test all 136 physical tiles roundtrip fidelity`() {
        for (id in 0..135) {
            val engineTile = Tile.fromTileId(id)
            val protocolTile = EngineProtocolMapper.toProtocolTile(engineTile)

            assertEquals(id, protocolTile.tileId)
            assertEquals(engineTile.suit.name, protocolTile.suit.name)
            assertEquals(engineTile.rank, protocolTile.rank)
            assertEquals(engineTile.isRedFive, protocolTile.isRedFive)

            val back = EngineProtocolMapper.toEngineTile(protocolTile)
            assertEquals(engineTile, back)
            assertEquals(engineTile.tileId, back.tileId)
            assertEquals(engineTile.isRedFive, back.isRedFive)
        }

        // 校验 3 张赤宝牌 (MAN 5: 19, SOU 5: 55, PIN 5: 91)
        val redMan = EngineProtocolMapper.toProtocolTile(Tile.fromTileId(19))
        assertTrue(redMan.isRedFive)
        assertEquals(ProtocolSuit.MAN, redMan.suit)
        assertEquals(5, redMan.rank)

        val redSou = EngineProtocolMapper.toProtocolTile(Tile.fromTileId(55))
        assertTrue(redSou.isRedFive)
        assertEquals(ProtocolSuit.SOU, redSou.suit)
        assertEquals(5, redSou.rank)

        val redPin = EngineProtocolMapper.toProtocolTile(Tile.fromTileId(91))
        assertTrue(redPin.isRedFive)
        assertEquals(ProtocolSuit.PIN, redPin.suit)
        assertEquals(5, redPin.rank)

        // 校验普通 5 不是赤宝牌
        val regularMan5 = EngineProtocolMapper.toProtocolTile(Tile.fromTileId(16))
        assertFalse(regularMan5.isRedFive)
        assertEquals(5, regularMan5.rank)
    }

    @Test
    fun `test meld mapping roundtrip`() {
        for (mt in MeldType.values()) {
            val t0 = Tile.fromTileId(0)
            val t1 = Tile.fromTileId(1)
            val t2 = Tile.fromTileId(2)
            val engineMeld = Meld(
                type = mt,
                tiles = listOf(t0, t1, t2),
                fromPlayer = 1,
                calledTile = t0
            )
            val protocolMeld = EngineProtocolMapper.toProtocolMeld(engineMeld)
            assertEquals(mt.name, protocolMeld.type.name)
            assertEquals(3, protocolMeld.tiles.size)
            assertEquals(0, protocolMeld.calledTile?.tileId)
            assertEquals(1, protocolMeld.fromPlayer)

            val back = EngineProtocolMapper.toEngineMeld(protocolMeld)
            assertEquals(engineMeld.type, back.type)
            assertEquals(engineMeld.tiles, back.tiles)
            assertEquals(engineMeld.calledTile, back.calledTile)
            assertEquals(engineMeld.fromPlayer, back.fromPlayer)
        }
    }

    @Test
    fun `test all 13 legal action types mapping`() {
        val t0 = Tile.fromTileId(0)
        val t1 = Tile.fromTileId(4)
        val t2 = Tile.fromTileId(8)
        val meld = Meld(MeldType.CHI, listOf(t0, t1, t2), 1, t0)

        val actions: List<GameAction> = listOf(
            GameAction.StartStacking(),
            GameAction.DealComplete,
            GameAction.DrawTile(0),
            GameAction.DiscardTile(0, t0),
            GameAction.DeclareChi(0, meld),
            GameAction.DeclarePon(0, listOf(t0, t1)),
            GameAction.DeclareKan(0, listOf(t0, t1, t2), MeldType.OPEN_KAN),
            GameAction.DeclareRiichi(0, t0),
            GameAction.DeclareTsumo(0),
            GameAction.DeclareRon(0),
            GameAction.Pass(0),
            GameAction.DeclareAbortiveDraw(0),
            GameAction.DeclareAgariYame(0)
        )

        for (action in actions) {
            val protocolAction = EngineProtocolMapper.toProtocolAction(action)
            assertNotNull(protocolAction)
            assertEquals(action, EngineProtocolMapper.toEngineAction(protocolAction), "Action roundtrip: ${action.javaClass.simpleName}")
            when (action) {
                is GameAction.StartStacking -> assertTrue(protocolAction is ProtocolLegalAction.StartStacking)
                is GameAction.DealComplete -> assertTrue(protocolAction is ProtocolLegalAction.DealComplete)
                is GameAction.DrawTile -> assertTrue(protocolAction is ProtocolLegalAction.DrawTile)
                is GameAction.DiscardTile -> {
                    assertTrue(protocolAction is ProtocolLegalAction.DiscardTile)
                    val cast = protocolAction as ProtocolLegalAction.DiscardTile
                    assertEquals(action.tile.tileId, cast.tile.tileId)
                }
                is GameAction.DeclareChi -> assertTrue(protocolAction is ProtocolLegalAction.DeclareChi)
                is GameAction.DeclarePon -> assertTrue(protocolAction is ProtocolLegalAction.DeclarePon)
                is GameAction.DeclareKan -> assertTrue(protocolAction is ProtocolLegalAction.DeclareKan)
                is GameAction.DeclareRiichi -> assertTrue(protocolAction is ProtocolLegalAction.DeclareRiichi)
                is GameAction.DeclareTsumo -> assertTrue(protocolAction is ProtocolLegalAction.DeclareTsumo)
                is GameAction.DeclareRon -> assertTrue(protocolAction is ProtocolLegalAction.DeclareRon)
                is GameAction.Pass -> assertTrue(protocolAction is ProtocolLegalAction.Pass)
                is GameAction.DeclareAbortiveDraw -> assertTrue(protocolAction is ProtocolLegalAction.DeclareAbortiveDraw)
                is GameAction.DeclareAgariYame -> assertTrue(protocolAction is ProtocolLegalAction.DeclareAgariYame)
            }
        }
    }

    @Test
    fun `test action choice domain and protocol conversion`() {
        val domainChoice = ActionChoice(
            requestId = "req-123",
            roundId = "round-456",
            decisionId = "dec-789",
            controlEpoch = 3L,
            optionId = "opt-001"
        )
        val protoChoice = EngineProtocolMapper.toProtocolActionChoice(domainChoice)
        assertEquals("req-123", protoChoice.requestId)
        assertEquals("round-456", protoChoice.roundId)
        assertEquals("dec-789", protoChoice.decisionId)
        assertEquals(3L, protoChoice.controlEpoch)
        assertEquals("opt-001", protoChoice.optionId)

        val back = EngineProtocolMapper.toEngineActionChoice(protoChoice)
        assertEquals(domainChoice, back)

        val preRoundChoice = ActionChoice(
            requestId = "req-pre",
            roundId = null,
            decisionId = "dec-pre",
            controlEpoch = 0L,
            optionId = "opt-pre"
        )
        val protoPreChoice = EngineProtocolMapper.toProtocolActionChoice(preRoundChoice)
        org.junit.jupiter.api.Assertions.assertNull(protoPreChoice.roundId)
        assertEquals(preRoundChoice, EngineProtocolMapper.toEngineActionChoice(protoPreChoice))
    }

    @Test
    fun `test shuffle plan domain and protocol conversion`() {
        val domainPlan = ShufflePlanRequest(
            requestId = "plan-req-1",
            roundId = "round-999",
            controlEpoch = 2L,
            selection = ShuffleSelection(
                initialTargets = setOf(ShuffleTarget.CHINITSU_MAN),
                drawTargets = setOf(ShuffleTarget.DAISANGEN)
            )
        )
        val protoPlan = EngineProtocolMapper.toProtocolShufflePlan(domainPlan)
        assertEquals("plan-req-1", protoPlan.requestId)
        assertEquals("round-999", protoPlan.roundId)
        assertEquals(2L, protoPlan.controlEpoch)
        assertEquals(1, protoPlan.selection.initialTargets.size)
        assertEquals(1, protoPlan.selection.drawTargets.size)

        val back = EngineProtocolMapper.toEngineShufflePlan(protoPlan)
        assertEquals(domainPlan.requestId, back.requestId)
        assertEquals(domainPlan.roundId, back.roundId)
        assertEquals(domainPlan.controlEpoch, back.controlEpoch)
        assertEquals(domainPlan.selection.initialTargets, back.selection.initialTargets)
        assertEquals(domainPlan.selection.drawTargets, back.selection.drawTargets)
    }

    @Test
    fun `test match config domain and protocol conversion`() {
        val domainConfig = MatchConfig(
            playerCount = 4,
            handsPerWind = 2,
            winds = listOf(1),
            startingScores = listOf(32000, 28000, 24000, 16000),
            allowExtension = false,
            allowAgariYame = false,
            bankruptcyEnabled = false,
            bankruptcyThreshold = 100,
            bankruptcyInclusive = true,
            targetScore = 40000,
            tieBreakSeats = listOf(3, 1, 0, 2),
            cheatEconomy = CheatEconomyConfig(true, listOf(100, 90, 80, 70), mapOf("SLEEVE_SWAP" to 7)),
            shuffleConfig = ShuffleConfig(true, 6500, 12, 0.6, 0.8, true)
        ).validateSupported()

        val protoConfig = EngineProtocolMapper.toProtocolConfig(domainConfig)
        assertEquals(4, protoConfig.playerCount)
        assertEquals(2, protoConfig.handsPerWind)
        assertEquals(listOf(1), protoConfig.winds)
        assertEquals(listOf(32000, 28000, 24000, 16000), protoConfig.startingScores)
        assertFalse(protoConfig.allowExtension)
        assertFalse(protoConfig.allowAgariYame)
        assertFalse(protoConfig.bankruptcyEnabled)
        assertEquals(100, protoConfig.bankruptcyThreshold)
        assertTrue(protoConfig.bankruptcyInclusive)
        assertEquals(40000, protoConfig.targetScore)
        assertEquals(listOf(3, 1, 0, 2), protoConfig.tieBreakSeats)
        assertTrue(protoConfig.cheatEconomy.enabled)
        assertEquals(listOf(100, 90, 80, 70), protoConfig.cheatEconomy.initialPoints)
        assertEquals(7, protoConfig.cheatEconomy.costs["SLEEVE_SWAP"])
        assertTrue(protoConfig.shuffleConfig.enabled)
        assertEquals(6500L, protoConfig.shuffleConfig.selectionMillis)

        val back = EngineProtocolMapper.toEngineConfig(protoConfig)
        assertEquals(domainConfig, back)
    }

    @Test
    fun `test score breakdown and result mapping fidelity`() {
        val t0 = Tile.fromTileId(0)
        val red = Tile.fromTileId(19)
        val domainScore = ScoreBreakdown(
            yakuLines = listOf(Pair("Riichi", 1), Pair("Tanyao", 1)),
            yakumanCount = 0,
            dora = DoraBreakdown(
                indicatorCount = 1,
                uraCount = 0,
                redCount = 1,
                totalHan = 2,
                indicatorTiles = listOf(t0),
                uraTiles = emptyList(),
                redTiles = listOf(red),
                indicatorHits = listOf(DoraIndicatorHit(t0, t0, listOf(t0))),
                uraHits = emptyList()
            ),
            fu = FuBreakdown(
                lines = listOf(FuLine("Menzen Ron", 10)),
                subtotal = 30,
                roundedUpTo = 30,
                finalFu = 30,
                specialNote = null
            ),
            score = ScoreCalculator.ScoreResult(fu = 30, han = 4, basePoints = 2000, title = "8000", isYakuman = false, yakumanMultiplier = 0),
            paymentLines = listOf("Seat 1 pays 8000"),
            honbaAddition = 0,
            riichiAddition = 0,
            totalPoints = 8000,
            uraEligible = false,
            isDealer = true,
            isTsumo = false
        )

        val protoScore = EngineProtocolMapper.toProtocolScoreBreakdown(domainScore)
        assertEquals(normalizedFields(domainScore), normalizedFields(protoScore), "All score detail fields must survive mapping")
        assertEquals(2, protoScore.yakuLines.size)
        assertEquals("Riichi", protoScore.yakuLines[0].first)
        assertEquals(30, protoScore.fu.finalFu)
        assertEquals(1, protoScore.dora.indicatorHits.size)
        assertEquals(1, protoScore.dora.redTiles.size)
        assertEquals(8000, protoScore.totalPoints)
        assertTrue(protoScore.isDealer)
        assertFalse(protoScore.isTsumo)
    }

    @Test
    fun `test all 19 game event types mapped correctly to protocol`() {
        val t0 = Tile.fromTileId(0)
        val meld = Meld(MeldType.PON, listOf(t0, Tile.fromTileId(1), Tile.fromTileId(2)), 1, t0)

        val events: List<GameEvent> = listOf(
            GameEvent.StackingStarted(0),
            GameEvent.WallBuilt(0, 136),
            GameEvent.TileDealt(0, 0, t0),
            GameEvent.DealingComplete(0),
            GameEvent.TileDrawn(0, 0, t0, 70),
            GameEvent.TileDiscarded(0, 0, t0, false, false),
            GameEvent.ChiDeclared(0, 0, meld, 1),
            GameEvent.PonDeclared(0, 0, meld, 1),
            GameEvent.KanDeclared(0, 0, meld, MeldType.OPEN_KAN),
            GameEvent.RinshanDraw(0, 0, t0),
            GameEvent.RiichiDeclared(0, 0, t0, false),
            GameEvent.TsumoWin(0, 0, t0, listOf("Riichi"), 1, 30, 1000, null),
            GameEvent.RonWin(0, 0, 1, t0, listOf("Riichi"), 1, 30, 1000, null),
            GameEvent.DoraRevealed(0, t0, false),
            GameEvent.CheatPointsChanged(0, 0, 10, 20),
            GameEvent.DrawGame(0, DrawReason.EXHAUSTIVE, listOf(0)),
            GameEvent.ScoreSettled(0, mapOf(1 to 1000), intArrayOf(26000, 24000, 25000, 25000)),
            GameEvent.RoundEnded(0, 1, 1),
            GameEvent.MatchEnded(0, intArrayOf(30000, 25000, 25000, 20000), 0)
        )

        assertEquals(19, events.size)
        for (event in events) {
            val protocolEvent = EngineProtocolMapper.toProtocolEvent(event)
            assertNotNull(protocolEvent)
            assertEquals(normalizedFields(event), normalizedFields(protocolEvent), "Event fields: ${event.javaClass.simpleName}")
        }
    }

    /** Compare complete payloads across distinct module-owned types, ignoring only wire discriminators. */
    private fun normalizedFields(value: Any?): Any? = when (value) {
        null, is String, is Number, is Boolean -> value
        is Enum<*> -> value.name
        is Tile -> value.tileId
        is ProtocolTile -> value.tileId
        is IntArray -> value.toList()
        is LongArray -> value.toList()
        is Array<*> -> value.map(::normalizedFields)
        is Iterable<*> -> value.map(::normalizedFields)
        is Map<*, *> -> value.entries.associate { normalizedFields(it.key) to normalizedFields(it.value) }
        is Pair<*, *> -> listOf(normalizedFields(value.first), normalizedFields(value.second))
        else -> {
            val fields = linkedMapOf<String, Any?>()
            var type: Class<*>? = value.javaClass
            while (type != null && type != Any::class.java) {
                for (field in type.declaredFields) {
                    if (field.isSynthetic || java.lang.reflect.Modifier.isStatic(field.modifiers) ||
                        // Wire discriminators; engine paymentPlan is a computed getter compared separately.
                        field.name in setOf("eventType", "actionType", "paymentPlan") || fields.containsKey(field.name)) continue
                    field.isAccessible = true
                    fields[field.name] = normalizedFields(field.get(value))
                }
                type = type.superclass
            }
            fields
        }
    }
}
