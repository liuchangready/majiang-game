package com.majiang.event

import com.majiang.model.*

/** 千术绝技类型 */
enum class CheatTechnique {
    SLEEVE_SWAP,     // 袖里乾坤
    WALL_STEAL,      // 移花接木
    DIVINE_SIGHT,    // 神仙透视
    DIVERSION        // 声东击西
}

/**
 * 所有游戏事件的密封类层级
 * 
 * 设计原则：
 * - 逻辑层产生事件，表现层消费事件
 * - 事件是不可变的数据快照
 * - 表现层根据事件类型决定播放什么动画/音效
 */
sealed class GameEvent {
    /** 事件发生的时间戳（游戏内逻辑帧号） */
    abstract val turnNumber: Int

    // === 对局流程事件 ===
    
    /** 码牌阶段开始 */
    data class StackingStarted(override val turnNumber: Int = 0) : GameEvent()
    
    /** 牌山构建完成 */
    data class WallBuilt(override val turnNumber: Int = 0, val wallSize: Int = 136) : GameEvent()
    
    /** 单张牌发给玩家（发牌阶段逐张触发） */
    data class TileDealt(override val turnNumber: Int, val player: Int, val tile: Tile) : GameEvent()
    
    /** 发牌完成 */
    data class DealingComplete(override val turnNumber: Int = 0) : GameEvent()
    
    /** 玩家摸牌 */
    data class TileDrawn(override val turnNumber: Int, val player: Int, val tile: Tile, val wallRemaining: Int) : GameEvent()
    
    /** 玩家打牌 */
    data class TileDiscarded(override val turnNumber: Int, val player: Int, val tile: Tile, val isRiichiDiscard: Boolean = false, val isTsumogiri: Boolean = false) : GameEvent()
    
    // === 副露事件 ===
    
    /** 吃 */
    data class ChiDeclared(override val turnNumber: Int, val player: Int, val meld: Meld, val fromPlayer: Int) : GameEvent()
    
    /** 碰 */
    data class PonDeclared(override val turnNumber: Int, val player: Int, val meld: Meld, val fromPlayer: Int) : GameEvent()
    
    /** 杠（含明杠、暗杠、加杠） */
    data class KanDeclared(override val turnNumber: Int, val player: Int, val meld: Meld, val kanType: MeldType) : GameEvent()
    
    /** 岭上摸牌 */
    data class RinshanDraw(override val turnNumber: Int, val player: Int, val tile: Tile) : GameEvent()
    
    // === 立直与和了 ===
    
    /** 立直宣言 */
    data class RiichiDeclared(override val turnNumber: Int, val player: Int, val discardTile: Tile, val isDoubleRiichi: Boolean = false) : GameEvent()
    
    /** 自摸和 */
    data class TsumoWin(
        override val turnNumber: Int,
        val player: Int,
        val winTile: Tile,
        val yakuList: List<String>,
        val han: Int,
        val fu: Int,
        val score: Int,
        val breakdown: com.majiang.rules.ScoreBreakdown? = null
    ) : GameEvent()
    
    /** 荣和 */
    data class RonWin(
        override val turnNumber: Int,
        val winner: Int,
        val loser: Int,
        val winTile: Tile,
        val yakuList: List<String>,
        val han: Int,
        val fu: Int,
        val score: Int,
        val breakdown: com.majiang.rules.ScoreBreakdown? = null
    ) : GameEvent()
    
    // === Dora ===
    
    /** Dora指示牌翻开 */
    data class DoraRevealed(override val turnNumber: Int, val indicator: Tile, val isUraDora: Boolean = false) : GameEvent()
    
    // === 千术事件 ===
    
    /** 千术尝试（成功或失败） */
    data class CheatAttempted(override val turnNumber: Int, val player: Int, val technique: CheatTechnique, val success: Boolean) : GameEvent()
    
    /** 怀疑度变化 */
    data class SuspicionChanged(override val turnNumber: Int, val target: Int, val observer: Int, val oldValue: Float, val newValue: Float) : GameEvent()
    
    /** 被抓包！ */
    data class BustedEvent(override val turnNumber: Int, val player: Int, val caughtBy: Int, val technique: CheatTechnique) : GameEvent()
    
    /** AI视线转移 */
    data class GazeShifted(override val turnNumber: Int, val observer: Int, val newTarget: Int?, val oldTarget: Int?) : GameEvent()
    
    /** 出千点数变更（仅记录变动席位、变动量与新余额，不泄露私密扣减原因） */
    data class CheatPointsChanged(
        override val turnNumber: Int,
        val seat: Int,
        val amount: Int,
        val newBalance: Int
    ) : GameEvent()
    
    // === 对局结果 ===
    
    /** 流局 */
    data class DrawGame(override val turnNumber: Int, val reason: DrawReason, val tenpaiPlayers: List<Int>) : GameEvent()
    
    /** 分数结算 */
    data class ScoreSettled(override val turnNumber: Int, val payments: Map<Int, Int>, val newScores: IntArray) : GameEvent()
    
    /** 一局结束 */
    data class RoundEnded(override val turnNumber: Int, val roundWind: Int, val roundNumber: Int) : GameEvent()
    
    /** 整场对局结束 */
    data class MatchEnded(override val turnNumber: Int, val finalScores: IntArray, val winner: Int) : GameEvent()
}

/** 流局原因 */
enum class DrawReason {
    EXHAUSTIVE,           // 荒牌（牌山摸完）
    FOUR_WINDS,           // 四风连打
    FOUR_KANS,            // 四杠散了
    FOUR_RIICHI,          // 四家立直
    NINE_TERMINALS,       // 九种九牌
    TRIPLE_RON            // 三家和（可选规则）
}
