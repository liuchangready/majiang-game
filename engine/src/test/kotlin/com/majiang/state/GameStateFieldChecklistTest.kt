package com.majiang.state

import com.majiang.economy.CheatLedgerEntry
import com.majiang.model.*
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.lang.reflect.Modifier

/**
 * 显式字段清单测试（MR-09a）
 *
 * 守护网机制：
 * - GameState 的所有非合成实例字段必须显式归属于 RESET_BY_RESET_FOR_NEW_ROUND 或 PRESERVED_ACROSS_ROUND 之一。
 * - 新增任何字段如未在清单中显式归类，此测试将直接阻断并报错。
 * - RESET 桶字段由 resetForNewRound() 重置为单局初始默认值；
 * - PRESERVED 桶字段跨局保留，其合法写方在局间流程：
 *   - MatchProgression.kt: roundWind / roundNumber
 *   - RoundTransitionPlanner.kt L159: 开局 currentPlayer = dealerIndex
 *   - TableAuthority.kt L110-112: 初始 roundWind / dealerIndex / currentPlayer
 *   - 结算与局间交接: scores / cheatPoints / riichiSticks / honbaSticks
 */
class GameStateFieldChecklistTest {

    companion object {
        val RESET_BY_RESET_FOR_NEW_ROUND: Set<String> = setOf(
            "wall", "deadWall", "deadWallReplacements", "wallPointer", "wallEndExclusive",
            "rinshanPointer", "players", "riichiDeclared", "doubleRiichiDeclared", "riichiTurn",
            "ippatsuActive", "sameTurnFuriten", "riichiFuriten", "doraIndicators",
            "uraDoraIndicators", "pendingKanDoraCount", "lastDiscard", "lastDiscardPlayer",
            "isFirstUninterruptedGo", "isRinshanDraw", "totalKanCount", "playerKanCounts",
            "sleeveTiles", "cheatEntries", "sleeveBloomOffer", "sleeveBloomUsedOnTurn",
            "beamSwapOffer", "beamSwapUsedOnTurn", "lastDrawKind", "turnNumber", "phase", "tilePool"
        )

        val PRESERVED_ACROSS_ROUND: Set<String> = setOf(
            "scores", "cheatPoints", "riichiSticks", "honbaSticks", "roundWind", "roundNumber",
            "dealerIndex", "currentPlayer"
        )
    }

    @Test
    fun `actual fields must exactly match two bucket checklist with no missing or uncategorized fields`() {
        val actualFields = GameState::class.java.declaredFields
            .filter { !it.isSynthetic && !Modifier.isStatic(it.modifiers) }
            .map { it.name }
            .toSet()

        val disjointCheck = RESET_BY_RESET_FOR_NEW_ROUND intersect PRESERVED_ACROSS_ROUND
        assertTrue(disjointCheck.isEmpty(), "RESET and PRESERVED buckets must be mutually exclusive, but shared: $disjointCheck")

        val uncategorized = actualFields - (RESET_BY_RESET_FOR_NEW_ROUND + PRESERVED_ACROSS_ROUND)
        assertTrue(uncategorized.isEmpty(), "Found uncategorized new fields in GameState: $uncategorized")

        val missing = (RESET_BY_RESET_FOR_NEW_ROUND + PRESERVED_ACROSS_ROUND) - actualFields
        assertTrue(missing.isEmpty(), "Checklist contains fields not declared on GameState: $missing")

        assertEquals(RESET_BY_RESET_FOR_NEW_ROUND.size, 32, "RESET bucket must contain exactly 32 fields")
        assertEquals(PRESERVED_ACROSS_ROUND.size, 8, "PRESERVED bucket must contain exactly 8 fields")
        assertEquals(40, actualFields.size, "GameState must declare exactly 40 instance fields")
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
}
