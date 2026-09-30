package com.majiang.state

/**
 * 日麻对局阶段状态机
 *
 * 完整流转路径：
 * STACKING → DEALING → (DRAW → CHEAT_WINDOW → DISCARD → RESPONSE_WINDOW)* → ROUND_END → MATCH_END
 *
 * 特殊路径：
 * - 杠后岭上摸牌：RESPONSE_WINDOW(杠) → KAN_DRAW → CHEAT_WINDOW → DISCARD → ...
 * - 自摸和了：DRAW/KAN_DRAW → ROUND_END
 * - 荣和：RESPONSE_WINDOW → ROUND_END
 * - 流局：DRAW(牌山空) → ROUND_END
 */
enum class GamePhase {
    /** 码牌阶段 — 千术引擎在此介入，重排牌山 */
    STACKING,
    
    /** 发牌阶段 — 从牌山向四位玩家发13张（庄家14张） */
    DEALING,
    
    /** 摸牌阶段 — 当前玩家从牌山摸一张 */
    DRAW,
    
    /** 千术操作窗口 — 仅在摸牌后、打牌前短暂开放，玩家可选择使用绝技 */
    CHEAT_WINDOW,
    
    /** 打牌阶段 — 当前玩家选择一张手牌打出 */
    DISCARD,
    
    /** 响应窗口 — 其他玩家可声明吃/碰/杠/荣和，需处理优先级 */
    RESPONSE_WINDOW,

    /** 抢杠响应窗口 — 加杠宣告后等待其他玩家宣告抢杠荣和 */
    CHANKAN_WINDOW,
    
    /** 杠后岭上摸牌 */
    KAN_DRAW,
    
    /** 一局结束（和了/流局结算） */
    ROUND_END,
    
    /** 整场对局结束 */
    MATCH_END;
    
    /**
     * 返回当前阶段允许转移到的下一阶段集合
     * 用于状态机合法性校验
     */
    fun allowedTransitions(): Set<GamePhase> = when (this) {
        STACKING -> setOf(DEALING)
        DEALING -> setOf(DRAW)
        DRAW -> setOf(CHEAT_WINDOW, DISCARD, ROUND_END, CHANKAN_WINDOW, KAN_DRAW)
        CHEAT_WINDOW -> setOf(DISCARD, ROUND_END, CHANKAN_WINDOW, KAN_DRAW)
        DISCARD -> setOf(RESPONSE_WINDOW, CHANKAN_WINDOW, KAN_DRAW)
        RESPONSE_WINDOW -> setOf(DRAW, KAN_DRAW, ROUND_END)
        CHANKAN_WINDOW -> setOf(KAN_DRAW, ROUND_END)
        KAN_DRAW -> setOf(CHEAT_WINDOW, DISCARD, ROUND_END)
        ROUND_END -> setOf(STACKING, MATCH_END)
        MATCH_END -> emptySet()
    }
}
