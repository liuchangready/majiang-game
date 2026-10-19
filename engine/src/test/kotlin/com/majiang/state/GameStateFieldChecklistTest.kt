package com.majiang.state

import com.majiang.economy.CheatLedgerEntry
import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.lang.reflect.Modifier
import java.net.URI
import javax.tools.DiagnosticCollector
import javax.tools.JavaFileObject
import javax.tools.SimpleJavaFileObject
import javax.tools.ToolProvider

private class JavaSourceFromString(name: String, val code: String) :
    SimpleJavaFileObject(URI.create("string:///" + name.replace('.', '/') + JavaFileObject.Kind.SOURCE.extension), JavaFileObject.Kind.SOURCE) {
    override fun getCharContent(ignoreEncodingErrors: Boolean): CharSequence = code
}

/**
 * 显式字段清单与领域状态治理测试（MR-09a & D-06）
 *
 * 守护网机制：
 * - 针对拆分后的领域子状态（BoardState / SeatState / CheatState / GameState），通过反射强断言所有实例字段
 *   显式归属于 RESET 桶或 PRESERVED 桶，禁止未分类新字段。
 * - 40 项全景概念字段继续保持双桶分类：32 RESET + 8 PRESERVED。
 * - 验证各子状态与聚合根在 resetForNewRound / copyFrom / deepCopy 流程中的语义严格性与深拷贝隔离性。
 * - 封闭子状态与 backing references 的 JVM 访问入口，通过跨模块 Java 编译负例探针验证外部代码无法越权绕过。
 */
class GameStateFieldChecklistTest {

    companion object {
        // === 40 项全景概念字段分类 ===
        val CONCEPTUAL_RESET_BY_RESET_FOR_NEW_ROUND: Set<String> = setOf(
            "wall", "deadWall", "deadWallReplacements", "wallPointer", "wallEndExclusive",
            "rinshanPointer", "players", "riichiDeclared", "doubleRiichiDeclared", "riichiTurn",
            "ippatsuActive", "sameTurnFuriten", "riichiFuriten", "doraIndicators",
            "uraDoraIndicators", "pendingKanDoraCount", "lastDiscard", "lastDiscardPlayer",
            "isFirstUninterruptedGo", "isRinshanDraw", "totalKanCount", "playerKanCounts",
            "sleeveTiles", "cheatEntries", "sleeveBloomOffer", "sleeveBloomUsedOnTurn",
            "beamSwapOffer", "beamSwapUsedOnTurn", "lastDrawKind", "turnNumber", "phase", "tilePool"
        )

        val CONCEPTUAL_PRESERVED_ACROSS_ROUND: Set<String> = setOf(
            "scores", "cheatPoints", "riichiSticks", "honbaSticks",
            "roundWind", "roundNumber", "dealerIndex", "currentPlayer"
        )

        // === BoardState 字段清单 (22 字段: 16 RESET + 6 PRESERVED) ===
        val BOARD_RESET_FIELDS: Set<String> = setOf(
            "wall", "deadWall", "deadWallReplacements", "wallPointer", "wallEndExclusive",
            "rinshanPointer", "turnNumber", "phase", "doraIndicators", "uraDoraIndicators",
            "pendingKanDoraCount", "lastDiscard", "lastDiscardPlayer", "isFirstUninterruptedGo",
            "isRinshanDraw", "totalKanCount"
        )

        val BOARD_PRESERVED_FIELDS: Set<String> = setOf(
            "roundWind", "roundNumber", "dealerIndex", "currentPlayer", "riichiSticks", "honbaSticks"
        )

        // === SeatState 字段清单 (10 字段: 1 标识 + 8 内部委托引用 + 1 手牌重置) ===
        val SEAT_IMMUTABLE_IDENTIFIERS: Set<String> = setOf("seat")

        val SEAT_INTERNAL_REFS: Set<String> = setOf(
            "scoresRef", "riichiDeclaredRef", "doubleRiichiDeclaredRef", "riichiTurnRef",
            "ippatsuActiveRef", "playerKanCountsRef", "sameTurnFuritenRef", "riichiFuritenRef"
        )

        val SEAT_RESET_FIELDS: Set<String> = setOf(
            "hand"
        )

        val SEAT_MUTABLE_DOMAIN_PROPERTIES: Set<String> = setOf(
            "score", "sameTurnFuriten", "riichiFuriten", "kanCount"
        )

        val SEAT_MIRROR_READ_ONLY_PROPERTIES: Set<String> = setOf(
            "riichiDeclared", "doubleRiichiDeclared", "riichiTurn", "ippatsuActive"
        )

        // === CheatState 字段清单 (5 字段) ===
        val CHEAT_RESET_FIELDS: Set<String> = setOf(
            "sleeveTiles", "cheatEntries", "sleeveBloom", "beamSwap"
        )

        val CHEAT_PRESERVED_FIELDS: Set<String> = setOf(
            "cheatPoints"
        )

        val SLEEVE_BLOOM_RESET_FIELDS: Set<String> = setOf(
            "offer", "usedOnTurn", "lastDrawKind"
        )

        val BEAM_SWAP_RESET_FIELDS: Set<String> = setOf(
            "offer", "usedOnTurn"
        )

        // === GameState 聚合根声明字段清单 (13 字段) ===
        val GAME_STATE_DECLARED_FIELDS: Set<String> = setOf(
            "board", "seats", "cheat", "tilePool", "players", "scores",
            "riichiDeclared", "doubleRiichiDeclared", "riichiTurn", "ippatsuActive",
            "playerKanCounts", "sameTurnFuriten", "riichiFuriten"
        )
    }

    private fun declaredInstanceFields(clazz: Class<*>): Set<String> =
        clazz.declaredFields
            .filter { !it.isSynthetic && !Modifier.isStatic(it.modifiers) }
            .map { it.name }
            .toSet()

    @Test
    fun `conceptual checklist must exactly categorize all 40 domain state fields`() {
        val disjoint = CONCEPTUAL_RESET_BY_RESET_FOR_NEW_ROUND intersect CONCEPTUAL_PRESERVED_ACROSS_ROUND
        assertTrue(disjoint.isEmpty(), "RESET and PRESERVED conceptual buckets must be disjoint: $disjoint")

        assertEquals(32, CONCEPTUAL_RESET_BY_RESET_FOR_NEW_ROUND.size, "RESET bucket must contain exactly 32 fields")
        assertEquals(8, CONCEPTUAL_PRESERVED_ACROSS_ROUND.size, "PRESERVED bucket must contain exactly 8 fields")
        assertEquals(40, (CONCEPTUAL_RESET_BY_RESET_FOR_NEW_ROUND + CONCEPTUAL_PRESERVED_ACROSS_ROUND).size)
    }

    @Test
    fun `board state declared fields must exactly match two bucket checklist`() {
        val actual = declaredInstanceFields(BoardState::class.java)
        val expected = BOARD_RESET_FIELDS + BOARD_PRESERVED_FIELDS

        val disjoint = BOARD_RESET_FIELDS intersect BOARD_PRESERVED_FIELDS
        assertTrue(disjoint.isEmpty(), "BoardState RESET and PRESERVED buckets must be disjoint: $disjoint")

        val uncategorized = actual - expected
        assertTrue(uncategorized.isEmpty(), "Found uncategorized fields in BoardState: $uncategorized")

        val missing = expected - actual
        assertTrue(missing.isEmpty(), "Checklist has fields missing in BoardState: $missing")

        assertEquals(16, BOARD_RESET_FIELDS.size, "BoardState must declare exactly 16 RESET fields")
        assertEquals(6, BOARD_PRESERVED_FIELDS.size, "BoardState must declare exactly 6 PRESERVED fields")
        assertEquals(22, actual.size, "BoardState must declare exactly 22 instance fields")
    }

    @Test
    fun `seat state declared fields must exactly match checklist`() {
        val actual = declaredInstanceFields(SeatState::class.java)
        val expected = SEAT_IMMUTABLE_IDENTIFIERS + SEAT_INTERNAL_REFS + SEAT_RESET_FIELDS

        val uncategorized = actual - expected
        assertTrue(uncategorized.isEmpty(), "Found uncategorized fields in SeatState: $uncategorized")

        val missing = expected - actual
        assertTrue(missing.isEmpty(), "Checklist has fields missing in SeatState: $missing")

        assertEquals(1, SEAT_IMMUTABLE_IDENTIFIERS.size)
        assertEquals(8, SEAT_INTERNAL_REFS.size, "SeatState must declare exactly 8 internal backing references")
        assertEquals(1, SEAT_RESET_FIELDS.size, "SeatState must declare exactly 1 RESET field (hand)")
        assertEquals(10, actual.size, "SeatState must declare exactly 10 instance fields")

        // 验证可变领域属性暴露（具备 getter 与 setter）
        val declaredMethods = SeatState::class.java.methods.map { it.name }.toSet()
        for (prop in SEAT_MUTABLE_DOMAIN_PROPERTIES) {
            val capitalized = prop.replaceFirstChar { it.uppercase() }
            assertTrue(
                declaredMethods.contains("get$capitalized"),
                "SeatState must expose getter for mutable domain property: $prop"
            )
            assertTrue(
                declaredMethods.contains("set$capitalized"),
                "SeatState must expose setter for mutable domain property: $prop"
            )
        }

        // 验证立直/一发镜像只读属性暴露（具备 getter，但严禁 setter，写操作只能走漏斗）
        for (prop in SEAT_MIRROR_READ_ONLY_PROPERTIES) {
            val capitalized = prop.replaceFirstChar { it.uppercase() }
            assertTrue(
                declaredMethods.contains("get$capitalized"),
                "SeatState must expose getter for mirror property: $prop"
            )
            assertFalse(
                declaredMethods.contains("set$capitalized"),
                "SeatState MUST NOT expose setter for mirror property: $prop (all mutations must go through declareRiichi/setIppatsu)"
            )
        }
    }

    @Test
    fun `cheat state declared fields must exactly match checklist`() {
        val actualCheat = declaredInstanceFields(CheatState::class.java)
        val expectedCheat = CHEAT_RESET_FIELDS + CHEAT_PRESERVED_FIELDS

        val uncategorized = actualCheat - expectedCheat
        assertTrue(uncategorized.isEmpty(), "Found uncategorized fields in CheatState: $uncategorized")

        val missing = expectedCheat - actualCheat
        assertTrue(missing.isEmpty(), "Checklist has fields missing in CheatState: $missing")

        assertEquals(4, CHEAT_RESET_FIELDS.size)
        assertEquals(1, CHEAT_PRESERVED_FIELDS.size)
        assertEquals(5, actualCheat.size)

        // 子状态 SleeveBloomState
        val actualSleeve = declaredInstanceFields(SleeveBloomState::class.java)
        assertEquals(SLEEVE_BLOOM_RESET_FIELDS, actualSleeve)

        // 子状态 BeamSwapState
        val actualBeam = declaredInstanceFields(BeamSwapState::class.java)
        assertEquals(BEAM_SWAP_RESET_FIELDS, actualBeam)
    }

    @Test
    fun `game state declared fields must exactly match composite architecture checklist`() {
        val actual = declaredInstanceFields(GameState::class.java)

        val uncategorized = actual - GAME_STATE_DECLARED_FIELDS
        assertTrue(uncategorized.isEmpty(), "Found uncategorized fields in GameState: $uncategorized")

        val missing = GAME_STATE_DECLARED_FIELDS - actual
        assertTrue(missing.isEmpty(), "Checklist has fields missing in GameState: $missing")

        assertEquals(13, actual.size, "GameState must declare exactly 13 instance fields")
    }

    private fun populateNonDefaultState(): GameState {
        val s = GameState()

        // RESET bucket fields mutated to non-default
        s.wall.add(Tile(Suit.MAN, 1, 0))
        s.deadWall.add(Tile(Suit.MAN, 2, 0))
        s.deadWallReplacements.add(Tile(Suit.MAN, 3, 0))
        s.wallPointer = 12
        s.wallEndExclusive = 98
        s.rinshanPointer = 3
        s.players[0].closedTiles.add(Tile(Suit.PIN, 1, 0))
        s.players[0].riichiDeclared = true
        s.players[0].riichiTurn = 4
        s.players[0].ippatsu = true
        s.riichiDeclared[0] = true
        s.doubleRiichiDeclared[0] = true
        s.riichiTurn[0] = 4
        s.ippatsuActive[0] = true
        s.sameTurnFuriten[0] = true
        s.riichiFuriten[0] = true
        s.doraIndicators.add(Tile(Suit.SOU, 1, 0))
        s.uraDoraIndicators.add(Tile(Suit.SOU, 2, 0))
        s.pendingKanDoraCount = 2
        s.lastDiscard = Tile(Suit.WIND, 1, 0)
        s.lastDiscardPlayer = 1
        s.isFirstUninterruptedGo = false
        s.isRinshanDraw = true
        s.totalKanCount = 2
        s.playerKanCounts[0] = 2
        s.sleeveTiles[0].add(Tile(Suit.DRAGON, 1, 0))
        s.cheatEntries.add(CheatLedgerEntry(entryId = "entry-1", seat = 0, amount = -50, balanceAfter = 450, reason = "test_cheat"))
        s.sleeveBloomUsedOnTurn = 5
        s.beamSwapUsedOnTurn = 6
        s.lastDrawKind = com.majiang.cheat.bloom.SleeveBloomDrawKind.RINSHAN
        s.turnNumber = 18
        s.phase = GamePhase.DISCARD
        s.tilePool.transfer(Tile(Suit.MAN, 1, 0), Zone.UNASSIGNED, Zone.HAND, 0)

        // PRESERVED bucket fields mutated to non-default
        s.scores[0] = 32000
        s.cheatPoints[0] = 450
        s.riichiSticks = 2
        s.honbaSticks = 3
        s.roundWind = 2
        s.roundNumber = 3
        s.dealerIndex = 2
        s.currentPlayer = 2

        return s
    }

    @Test
    fun `resetForNewRound restores RESET bucket to defaults and preserves PRESERVED bucket`() {
        val s = populateNonDefaultState()
        s.resetForNewRound()

        val fresh = GameState()

        // 32 RESET bucket fields assertions
        assertEquals(fresh.wall, s.wall, "wall reset")
        assertEquals(fresh.deadWall, s.deadWall, "deadWall reset")
        assertEquals(fresh.deadWallReplacements, s.deadWallReplacements, "deadWallReplacements reset")
        assertEquals(fresh.wallPointer, s.wallPointer, "wallPointer reset")
        assertEquals(fresh.wallEndExclusive, s.wallEndExclusive, "wallEndExclusive reset")
        assertEquals(fresh.rinshanPointer, s.rinshanPointer, "rinshanPointer reset")

        for (i in 0 until 4) {
            assertTrue(s.players[i].closedTiles.isEmpty(), "players[$i].closedTiles reset")
            assertTrue(s.players[i].melds.isEmpty(), "players[$i].melds reset")
            assertTrue(s.players[i].discards.isEmpty(), "players[$i].discards reset")
            assertTrue(s.players[i].discardHistory.isEmpty(), "players[$i].discardHistory reset")
            assertNull(s.players[i].drawnTile, "players[$i].drawnTile reset")
            assertEquals(fresh.players[i].riichiDeclared, s.players[i].riichiDeclared, "players[$i].riichiDeclared reset")
            assertEquals(fresh.players[i].riichiTurn, s.players[i].riichiTurn, "players[$i].riichiTurn reset")
            assertEquals(fresh.players[i].ippatsu, s.players[i].ippatsu, "players[$i].ippatsu reset")
        }

        assertArrayEquals(fresh.riichiDeclared, s.riichiDeclared, "riichiDeclared reset")
        assertArrayEquals(fresh.doubleRiichiDeclared, s.doubleRiichiDeclared, "doubleRiichiDeclared reset")
        assertArrayEquals(fresh.riichiTurn, s.riichiTurn, "riichiTurn reset")
        assertArrayEquals(fresh.ippatsuActive, s.ippatsuActive, "ippatsuActive reset")
        assertArrayEquals(fresh.sameTurnFuriten, s.sameTurnFuriten, "sameTurnFuriten reset")
        assertArrayEquals(fresh.riichiFuriten, s.riichiFuriten, "riichiFuriten reset")

        assertEquals(fresh.doraIndicators, s.doraIndicators, "doraIndicators reset")
        assertEquals(fresh.uraDoraIndicators, s.uraDoraIndicators, "uraDoraIndicators reset")
        assertEquals(fresh.pendingKanDoraCount, s.pendingKanDoraCount, "pendingKanDoraCount reset")
        assertEquals(fresh.lastDiscard, s.lastDiscard, "lastDiscard reset")
        assertEquals(fresh.lastDiscardPlayer, s.lastDiscardPlayer, "lastDiscardPlayer reset")
        assertEquals(fresh.isFirstUninterruptedGo, s.isFirstUninterruptedGo, "isFirstUninterruptedGo reset")
        assertEquals(fresh.isRinshanDraw, s.isRinshanDraw, "isRinshanDraw reset")
        assertEquals(fresh.totalKanCount, s.totalKanCount, "totalKanCount reset")
        assertArrayEquals(fresh.playerKanCounts, s.playerKanCounts, "playerKanCounts reset")

        for (i in 0 until 4) {
            assertEquals(fresh.sleeveTiles[i], s.sleeveTiles[i], "sleeveTiles[$i] reset")
        }
        assertEquals(fresh.cheatEntries, s.cheatEntries, "cheatEntries reset")
        assertNull(s.sleeveBloomOffer, "sleeveBloomOffer reset")
        assertEquals(fresh.sleeveBloomUsedOnTurn, s.sleeveBloomUsedOnTurn, "sleeveBloomUsedOnTurn reset")
        assertNull(s.beamSwapOffer, "beamSwapOffer reset")
        assertEquals(fresh.beamSwapUsedOnTurn, s.beamSwapUsedOnTurn, "beamSwapUsedOnTurn reset")
        assertEquals(fresh.lastDrawKind, s.lastDrawKind, "lastDrawKind reset")
        assertEquals(fresh.turnNumber, s.turnNumber, "turnNumber reset")
        assertEquals(fresh.phase, s.phase, "phase reset")

        // tilePool reset to UNASSIGNED
        for (tile in TilePool.buildStandardSet()) {
            assertEquals(Zone.UNASSIGNED, s.tilePool.getLocation(tile)?.zone, "tilePool zone for $tile reset to UNASSIGNED")
        }

        // 8 PRESERVED bucket fields assertions
        assertEquals(32000, s.scores[0], "scores[0] preserved")
        assertEquals(450, s.cheatPoints[0], "cheatPoints[0] preserved")
        assertEquals(2, s.riichiSticks, "riichiSticks preserved")
        assertEquals(3, s.honbaSticks, "honbaSticks preserved")
        assertEquals(2, s.roundWind, "roundWind preserved")
        assertEquals(3, s.roundNumber, "roundNumber preserved")
        assertEquals(2, s.dealerIndex, "dealerIndex preserved")
        assertEquals(2, s.currentPlayer, "currentPlayer preserved")
    }

    @Test
    fun `copyFrom roundtrip preserves all fields in both RESET and PRESERVED buckets`() {
        val src = populateNonDefaultState()
        val dst = GameState()
        dst.copyFrom(src)

        // RESET bucket fields
        assertEquals(src.wall, dst.wall)
        assertEquals(src.deadWall, dst.deadWall)
        assertEquals(src.deadWallReplacements, dst.deadWallReplacements)
        assertEquals(src.wallPointer, dst.wallPointer)
        assertEquals(src.wallEndExclusive, dst.wallEndExclusive)
        assertEquals(src.rinshanPointer, dst.rinshanPointer)

        for (i in 0 until 4) {
            assertEquals(src.players[i].closedTiles, dst.players[i].closedTiles)
            assertEquals(src.players[i].melds, dst.players[i].melds)
            assertEquals(src.players[i].discards, dst.players[i].discards)
            assertEquals(src.players[i].discardHistory, dst.players[i].discardHistory)
            assertEquals(src.players[i].drawnTile, dst.players[i].drawnTile)
            assertEquals(src.players[i].riichiDeclared, dst.players[i].riichiDeclared)
            assertEquals(src.players[i].riichiTurn, dst.players[i].riichiTurn)
            assertEquals(src.players[i].ippatsu, dst.players[i].ippatsu)
        }

        assertArrayEquals(src.riichiDeclared, dst.riichiDeclared)
        assertArrayEquals(src.doubleRiichiDeclared, dst.doubleRiichiDeclared)
        assertArrayEquals(src.riichiTurn, dst.riichiTurn)
        assertArrayEquals(src.ippatsuActive, dst.ippatsuActive)
        assertArrayEquals(src.sameTurnFuriten, dst.sameTurnFuriten)
        assertArrayEquals(src.riichiFuriten, dst.riichiFuriten)

        assertEquals(src.doraIndicators, dst.doraIndicators)
        assertEquals(src.uraDoraIndicators, dst.uraDoraIndicators)
        assertEquals(src.pendingKanDoraCount, dst.pendingKanDoraCount)
        assertEquals(src.lastDiscard, dst.lastDiscard)
        assertEquals(src.lastDiscardPlayer, dst.lastDiscardPlayer)
        assertEquals(src.isFirstUninterruptedGo, dst.isFirstUninterruptedGo)
        assertEquals(src.isRinshanDraw, dst.isRinshanDraw)
        assertEquals(src.totalKanCount, dst.totalKanCount)
        assertArrayEquals(src.playerKanCounts, dst.playerKanCounts)

        for (i in 0 until 4) {
            assertEquals(src.sleeveTiles[i], dst.sleeveTiles[i])
        }
        assertEquals(src.cheatEntries, dst.cheatEntries)
        assertEquals(src.sleeveBloomOffer, dst.sleeveBloomOffer)
        assertEquals(src.sleeveBloomUsedOnTurn, dst.sleeveBloomUsedOnTurn)
        assertEquals(src.beamSwapOffer, dst.beamSwapOffer)
        assertEquals(src.beamSwapUsedOnTurn, dst.beamSwapUsedOnTurn)
        assertEquals(src.lastDrawKind, dst.lastDrawKind)
        assertEquals(src.turnNumber, dst.turnNumber)
        assertEquals(src.phase, dst.phase)

        for (tile in TilePool.buildStandardSet()) {
            assertEquals(src.tilePool.getLocation(tile)?.zone, dst.tilePool.getLocation(tile)?.zone)
        }

        // PRESERVED bucket fields
        assertArrayEquals(src.scores, dst.scores)
        assertArrayEquals(src.cheatPoints, dst.cheatPoints)
        assertEquals(src.riichiSticks, dst.riichiSticks)
        assertEquals(src.honbaSticks, dst.honbaSticks)
        assertEquals(src.roundWind, dst.roundWind)
        assertEquals(src.roundNumber, dst.roundNumber)
        assertEquals(src.dealerIndex, dst.dealerIndex)
        assertEquals(src.currentPlayer, dst.currentPlayer)
    }

    @Test
    fun `sub-states maintain independent deep copy isolation`() {
        val src = populateNonDefaultState()
        val copy = src.deepCopy()

        // 验证 BoardState 隔离 (通过 GameState 委托属性)
        copy.wall.add(Tile(Suit.PIN, 9, 0))
        assertNotEquals(src.wall.size, copy.wall.size, "BoardState wall mutation must not pollute source")

        // 验证 SeatState 隔离 (通过 GameState 属性与 PlayerHand)
        copy.scores[0] = 99999
        assertEquals(32000, src.scores[0], "SeatState score mutation must not pollute source")

        copy.players[0].closedTiles.add(Tile(Suit.PIN, 2, 0))
        assertNotEquals(src.players[0].closedTiles.size, copy.players[0].closedTiles.size, "PlayerHand must be isolated")

        // 验证 CheatState 隔离 (通过 GameState 委托属性)
        copy.cheatPoints[0] = 8888
        assertEquals(450, src.cheatPoints[0], "CheatState cheatPoints must not pollute source")

        copy.sleeveTiles[0].add(Tile(Suit.WIND, 2, 0))
        assertNotEquals(src.sleeveTiles[0].size, copy.sleeveTiles[0].size, "Sleeve tiles must be isolated")

        // 独立直接验证子状态自身 deepCopy 隔离
        val boardSrc = BoardState()
        boardSrc.wall.add(Tile(Suit.MAN, 1, 0))
        val boardCopy = boardSrc.deepCopy()
        boardCopy.wall.add(Tile(Suit.MAN, 2, 0))
        assertEquals(1, boardSrc.wall.size)
        assertEquals(2, boardCopy.wall.size)

        val seatSrc = SeatState(0)
        seatSrc.score = 50000
        val seatCopy = seatSrc.deepCopy()
        seatCopy.score = 60000
        assertEquals(50000, seatSrc.score)
        assertEquals(60000, seatCopy.score)

        val cheatSrc = CheatState()
        cheatSrc.cheatPoints[0] = 100
        val cheatCopy = cheatSrc.deepCopy()
        cheatCopy.cheatPoints[0] = 200
        assertEquals(100, cheatSrc.cheatPoints[0])
        assertEquals(200, cheatCopy.cheatPoints[0])
    }

    @Test
    fun `sub-state classes and GameState fields must be strictly internal and prevent unauthorized bypass`() {
        // 1. 验证 GameState 未暴露任何未经修饰或带 mangling 的子状态 public getter / 方法
        val publicMethodNames = GameState::class.java.methods.map { it.name }.toSet()
        assertFalse(publicMethodNames.any { it.startsWith("getBoard") }, "GameState must not expose any getBoard...() method")
        assertFalse(publicMethodNames.any { it.startsWith("getSeats") }, "GameState must not expose any getSeats...() method")
        assertFalse(publicMethodNames.any { it.startsWith("getCheat$") }, "GameState must not expose any getCheat$...() method")
        assertFalse(publicMethodNames.any { it.startsWith("seat$") }, "GameState must not expose any seat$...() method")

        // 2. 验证 SeatState 未暴露任何 backing reference 的 getter / 方法
        val seatMethodNames = SeatState::class.java.methods.map { it.name }.toSet()
        val exposedRefGetters = seatMethodNames.filter { it.contains("Ref") }
        assertTrue(exposedRefGetters.isEmpty(), "SeatState must not expose any backing reference getters, but found: $exposedRefGetters")

        // 3. 验证 GameState 聚合根内部状态字段为 private，非 public
        val internalFieldNames = listOf("board", "seats", "cheat")
        for (fieldName in internalFieldNames) {
            val field = GameState::class.java.getDeclaredField(fieldName)
            assertTrue(
                Modifier.isPrivate(field.modifiers),
                "GameState.$fieldName backing field must be private"
            )
        }

        // 4. 验证 SeatState 内部 backing reference 字段均为 private
        for (refField in SEAT_INTERNAL_REFS) {
            val field = SeatState::class.java.getDeclaredField(refField)
            assertTrue(
                Modifier.isPrivate(field.modifiers),
                "SeatState.$refField backing field must be private"
            )
        }

        // 5. 验证 GameState 仅暴露无参构造函数，禁止外部注入解绑/不一致的独立数组与席位列表
        val constructors = GameState::class.java.constructors
        assertEquals(1, constructors.size, "GameState must declare exactly 1 constructor")
        assertEquals(0, constructors[0].parameterCount, "GameState public constructor must take 0 arguments")
    }

    @Test
    fun `cross-module Java compilation negative probe verifies exploit code cannot compile`() {
        val compiler = ToolProvider.getSystemJavaCompiler()
        if (compiler == null) {
            println("ToolProvider.getSystemJavaCompiler() is not available in runtime environment, skipping javac probe")
            return
        }

        val engineClassesPath = java.io.File(GameState::class.java.protectionDomain.codeSource.location.toURI()).canonicalPath
        val classpath = engineClassesPath + java.io.File.pathSeparator + System.getProperty("java.class.path")
        val options = listOf("-classpath", classpath)

        // 探针 1: Codex 实测越权代码 (state.getBoard$majiang_engine().getWall().clear(); state.getSeats$majiang_engine().get(0).getRiichiDeclaredRef$majiang_engine()[0] = true;)
        run {
            val diagnostics = DiagnosticCollector<JavaFileObject>()
            val fileManager = compiler.getStandardFileManager(diagnostics, null, null)
            val units = listOf(
                JavaSourceFromString("exploit.CodexExploitAttempt", """
                    package exploit;
                    import com.majiang.state.GameState;

                    public class CodexExploitAttempt {
                        public void attack(GameState state) {
                            state.getBoard${'$'}majiang_engine().getWall().clear();
                            state.getSeats${'$'}majiang_engine().get(0).getRiichiDeclaredRef${'$'}majiang_engine()[0] = true;
                        }
                    }
                """.trimIndent())
            )
            val success = compiler.getTask(null, fileManager, diagnostics, options, null, units).call()
            assertFalse(success, "Compilation of Codex exploit code MUST fail because sub-state JVM methods are not exposed")
            val errors = diagnostics.diagnostics.map { it.getMessage(null) }
            assertTrue(errors.isNotEmpty(), "Javac should report compilation errors")
            assertTrue(
                errors.any { it.contains("getBoard") },
                "Compilation errors should point to missing getBoard method: $errors"
            )
        }

        // 探针 2: 尝试调用 seat$majiang_engine 或 getCheat$majiang_engine
        run {
            val diagnostics = DiagnosticCollector<JavaFileObject>()
            val fileManager = compiler.getStandardFileManager(diagnostics, null, null)
            val units = listOf(
                JavaSourceFromString("exploit.SeatExploitAttempt", """
                    package exploit;
                    import com.majiang.state.GameState;

                    public class SeatExploitAttempt {
                        public void attack(GameState state) {
                            state.seat${'$'}majiang_engine(0);
                            state.getCheat${'$'}majiang_engine();
                        }
                    }
                """.trimIndent())
            )
            val success = compiler.getTask(null, fileManager, diagnostics, options, null, units).call()
            assertFalse(success, "Compilation of seat / cheat exploit code MUST fail")
            val errors = diagnostics.diagnostics.map { it.getMessage(null) }
            assertTrue(errors.isNotEmpty(), "Javac should report compilation errors")
            assertTrue(
                errors.any { it.contains("seat") || it.contains("getCheat") },
                "Compilation errors should point to missing seat / cheat methods: $errors"
            )
        }

        // 探针 3: 尝试调用 SeatState 上 backing reference getter
        run {
            val diagnostics = DiagnosticCollector<JavaFileObject>()
            val fileManager = compiler.getStandardFileManager(diagnostics, null, null)
            val units = listOf(
                JavaSourceFromString("exploit.SeatRefExploitAttempt", """
                    package exploit;
                    import com.majiang.state.SeatState;

                    public class SeatRefExploitAttempt {
                        public void attack(SeatState seat) {
                            seat.getRiichiDeclaredRef${'$'}majiang_engine();
                        }
                    }
                """.trimIndent())
            )
            val success = compiler.getTask(null, fileManager, diagnostics, options, null, units).call()
            assertFalse(success, "Compilation of SeatState Ref exploit code MUST fail")
            val errors = diagnostics.diagnostics.map { it.getMessage(null) }
            assertTrue(errors.isNotEmpty(), "Javac should report compilation errors")
            assertTrue(
                errors.any { it.contains("getRiichiDeclaredRef") },
                "Compilation errors should point to missing getRiichiDeclaredRef method: $errors"
            )
        }
    }

    @Test
    fun `single source of truth guarantees scores and seats remain strictly synchronized`() {
        val scores = IntArray(4) { 25000 }
        val seat = SeatState(seat = 0, scoresRef = scores)

        // 修改 scores 数组视图立即反映在 seat.score
        scores[0] = 30000
        assertEquals(30000, seat.score, "Mutating backing array must update seat.score")

        // 修改 seat.score 立即反映在 scores 数组视图
        seat.score = 42000
        assertEquals(42000, scores[0], "Mutating seat.score must update backing array")

        // 验证 GameState 聚合根上 scores 数组视图一致性
        val s = GameState()
        s.scores[0] = 33000
        assertEquals(33000, s.scores[0])
    }

    @Test
    fun `riichi and ippatsu funnels enforce MR-09a mirror invariants and reject direct bypass`() {
        // 1. 验证 SeatState 独立实例漏斗
        val riichiRef = BooleanArray(4)
        val doubleRiichiRef = BooleanArray(4)
        val turnRef = IntArray(4) { -1 }
        val ippatsuRef = BooleanArray(4)
        val seat = SeatState(
            seat = 0,
            riichiDeclaredRef = riichiRef,
            doubleRiichiDeclaredRef = doubleRiichiRef,
            riichiTurnRef = turnRef,
            ippatsuActiveRef = ippatsuRef
        )
        assertFalse(seat.riichiDeclared)
        assertFalse(riichiRef[0])
        assertFalse(seat.ippatsuActive)
        assertFalse(ippatsuRef[0])

        seat.declareRiichi(turn = 3, isDouble = false)
        assertTrue(seat.riichiDeclared)
        assertTrue(riichiRef[0])
        assertEquals(3, seat.riichiTurn)
        assertEquals(3, turnRef[0])
        assertTrue(seat.ippatsuActive)
        assertTrue(ippatsuRef[0])

        seat.setIppatsu(false)
        assertFalse(seat.ippatsuActive)
        assertFalse(ippatsuRef[0])

        // 2. 验证 GameState 聚合根漏斗与 PlayerHand 镜像一致性
        val s = GameState()
        assertFalse(s.riichiDeclared[0])
        assertFalse(s.players[0].riichiDeclared)
        assertFalse(s.ippatsuActive[0])
        assertFalse(s.players[0].ippatsu)

        s.declareRiichi(0, turn = 3, isDouble = false)
        assertTrue(s.riichiDeclared[0])
        assertTrue(s.players[0].riichiDeclared)
        assertEquals(3, s.riichiTurn[0])
        assertEquals(3, s.players[0].riichiTurn)
        assertTrue(s.ippatsuActive[0])
        assertTrue(s.players[0].ippatsu)

        s.setIppatsu(0, false)
        assertFalse(s.ippatsuActive[0])
        assertFalse(s.players[0].ippatsu)
    }
}
