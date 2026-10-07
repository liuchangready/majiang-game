package com.majiang.cheat

import com.majiang.model.*
import com.majiang.state.GameState
import com.majiang.event.*
import kotlin.random.Random

/**
 * 运行时千术执行器 — 四大绝技的物理实现
 *
 * 核心约束：每个操作都是物理交换/偷窥，绝不凭空创造或消灭牌
 * 
 * 四大绝技：
 * 1. 袖里乾坤：手牌 ↔ 袖中牌物理交换
 * 2. 移花接木：牌山顶牌 ↔ 手牌交换
 * 3. 神仙透视：只读窃视，不改变任何牌位
 * 4. 声东击西：强制AI视线转移
 */
class CheatActionExecutor(
    private val state: GameState,
    private val eventBus: GameEventBus,
    private val suspicionSystem: SuspicionSystem
) {
    /** 透视目标类型 */
    sealed class DivineSightTarget {
        data class OpponentHand(val playerIndex: Int) : DivineSightTarget()
        data class WallPeek(val count: Int = 3) : DivineSightTarget()
        object DeadWallPeek : DivineSightTarget()
    }

    /** 千术执行结果 */
    data class CheatResult(
        val success: Boolean,
        val bustedBy: Int? = null,
        val revealedTiles: List<Tile>? = null,  // for divine sight
        val message: String = ""
    )

    /**
     * 袖里乾坤：将手中一张牌与袖中暗藏牌交换
     * 守恒：hand.remove(tileOut) + sleeve.remove(tileIn) + hand.add(tileIn) + sleeve.add(tileOut)
     */
    fun sleeveSwap(player: Int, tileOut: Tile, tileIn: Tile): CheatResult {
        // 1. Validate preconditions
        if (!state.players[player].closedTiles.contains(tileOut)) {
            return CheatResult(false, message = "手牌中没有 ${tileOut.displayName()}")
        }
        if (!state.sleeveTiles[player].contains(tileIn)) {
            return CheatResult(false, message = "袖中没有 ${tileIn.displayName()}")
        }

        // 2. Check if being watched
        val watchers = suspicionSystem.getWatchers(player)
        if (watchers.isNotEmpty()) {
            // Being watched! Increase suspicion
            for (watcher in watchers) {
                suspicionSystem.addSuspicion(player, watcher.playerIndex, 35f)
            }
            // Check if busted
            val buster = suspicionSystem.checkBusted(player)
            if (buster != null) {
                eventBus.publish(GameEvent.BustedEvent(
                    turnNumber = state.turnNumber,
                    player = player,
                    caughtBy = buster,
                    technique = CheatTechnique.SLEEVE_SWAP
                ))
                eventBus.publish(GameEvent.CheatAttempted(
                    turnNumber = state.turnNumber,
                    player = player,
                    technique = CheatTechnique.SLEEVE_SWAP,
                    success = false
                ))
                return CheatResult(false, bustedBy = buster, message = "被 ${buster} 号玩家抓包！")
            }
        }

        // 3. Execute physical swap (conservation-safe)
        TileMovement.swapHandWithSleeve(state, player, tileOut, tileIn)
        state.tilePool.assertConservation()

        // 4. Publish success event
        eventBus.publish(GameEvent.CheatAttempted(
            turnNumber = state.turnNumber,
            player = player,
            technique = CheatTechnique.SLEEVE_SWAP,
            success = true
        ))

        // 5. Even successful cheats add minor suspicion to non-watching observers
        for (i in 0 until 4) {
            if (i != player && !suspicionSystem.isBeingWatched(player)) {
                suspicionSystem.addSuspicion(player, i, 3f)
            }
        }

        return CheatResult(true, message = "袖里乾坤成功！${tileOut.displayName()} ↔ ${tileIn.displayName()}")
    }

    /**
     * 移花接木：偷换牌山顶牌
     * 守恒：wall[wallPointer] ↔ hand 中的指定牌
     */
    fun wallSteal(player: Int, handTile: Tile): CheatResult {
        if (!state.players[player].closedTiles.contains(handTile)) {
            return CheatResult(false, message = "手牌中没有 ${handTile.displayName()}")
        }
        if (state.wallPointer >= state.wallEndExclusive) {
            return CheatResult(false, message = "牌山已经摸完")
        }

        // Check gaze
        val watchers = suspicionSystem.getWatchers(player)
        if (watchers.isNotEmpty()) {
            for (watcher in watchers) {
                suspicionSystem.addSuspicion(player, watcher.playerIndex, 40f)
            }
            val buster = suspicionSystem.checkBusted(player)
            if (buster != null) {
                eventBus.publish(GameEvent.BustedEvent(
                    turnNumber = state.turnNumber, player = player,
                    caughtBy = buster, technique = CheatTechnique.WALL_STEAL
                ))
                eventBus.publish(GameEvent.CheatAttempted(
                    turnNumber = state.turnNumber, player = player,
                    technique = CheatTechnique.WALL_STEAL, success = false
                ))
                return CheatResult(false, bustedBy = buster, message = "被抓包！")
            }
        }

        // Swap wall top with hand tile
        val wallTile = TileMovement.swapHandWithWall(state, player, handTile)
        state.tilePool.assertConservation()

        eventBus.publish(GameEvent.CheatAttempted(
            turnNumber = state.turnNumber, player = player,
            technique = CheatTechnique.WALL_STEAL, success = true
        ))

        return CheatResult(true, message = "移花接木成功！得到 ${wallTile.displayName()}")
    }

    /**
     * 神仙透视：窃探对手手牌或牌山前 N 张
     * 物理实现：只读操作，不改变任何牌的位置
     */
    fun divineSight(player: Int, target: DivineSightTarget): CheatResult {
        // Lighter suspicion for peeking
        val watchers = suspicionSystem.getWatchers(player)
        if (watchers.isNotEmpty()) {
            for (watcher in watchers) {
                suspicionSystem.addSuspicion(player, watcher.playerIndex, 20f)
            }
            val buster = suspicionSystem.checkBusted(player)
            if (buster != null) {
                eventBus.publish(GameEvent.BustedEvent(
                    turnNumber = state.turnNumber, player = player,
                    caughtBy = buster, technique = CheatTechnique.DIVINE_SIGHT
                ))
                eventBus.publish(GameEvent.CheatAttempted(
                    turnNumber = state.turnNumber, player = player,
                    technique = CheatTechnique.DIVINE_SIGHT, success = false
                ))
                return CheatResult(false, bustedBy = buster, message = "被抓包！")
            }
        }

        val revealed = when (target) {
            is DivineSightTarget.OpponentHand -> {
                state.players[target.playerIndex].closedTiles.toList()
            }
            is DivineSightTarget.WallPeek -> {
                val start = state.wallPointer
                val end = minOf(start + target.count, state.wallEndExclusive)
                state.wall.subList(start, end).toList()
            }
            is DivineSightTarget.DeadWallPeek -> {
                state.deadWall.toList()
            }
        }

        eventBus.publish(GameEvent.CheatAttempted(
            turnNumber = state.turnNumber, player = player,
            technique = CheatTechnique.DIVINE_SIGHT, success = true
        ))

        return CheatResult(true, revealedTiles = revealed, message = "透视成功！看到 ${revealed.size} 张牌")
    }

    /**
     * 声东击西：制造响动，使所有AI视线转向干扰源
     * 为后续出千创造安全窗口
     */
    fun diversion(player: Int, durationSeconds: Float = 3.0f): CheatResult {
        // Diversion itself is less risky (it's just knocking on the table etc.)
        suspicionSystem.applyDiversion(diversionTarget = null, durationSeconds = durationSeconds)

        // Small flat suspicion increase (something felt off)
        for (i in 0 until 4) {
            if (i != player) {
                suspicionSystem.addSuspicion(player, i, 5f)
            }
        }

        eventBus.publish(GameEvent.CheatAttempted(
            turnNumber = state.turnNumber, player = player,
            technique = CheatTechnique.DIVERSION, success = true
        ))

        return CheatResult(true, message = "声东击西成功！所有AI视线被吸引 ${durationSeconds}秒")
    }
}
