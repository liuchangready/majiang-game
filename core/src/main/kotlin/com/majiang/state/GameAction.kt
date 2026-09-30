package com.majiang.state

import com.majiang.model.Tile
import com.majiang.model.Meld
import com.majiang.model.MeldType

/**
 * 玩家或系统可执行的所有动作
 *
 * 状态机接收 GameAction，校验合法性后修改 GameState 并发布 GameEvent
 */
sealed class GameAction {
    // --- 系统动作 ---
    /** 开始码牌（系统触发） */
    data class StartStacking(val stackingPlans: List<Any> = emptyList()) : GameAction()
    
    /** 发牌完成（系统触发） */
    object DealComplete : GameAction()
    
    // --- 玩家回合动作 ---
    /** 摸牌 */
    data class DrawTile(val player: Int) : GameAction()
    
    /** 打牌 */
    data class DiscardTile(val player: Int, val tile: Tile) : GameAction()
    
    // --- 副露动作 ---
    /** 宣告吃 */
    data class DeclareChi(val player: Int, val meld: Meld) : GameAction()
    
    /** 宣告碰 */
    data class DeclarePon(val player: Int, val tiles: List<Tile>) : GameAction()
    
    /** 宣告杠 */
    data class DeclareKan(val player: Int, val tiles: List<Tile>, val kanType: MeldType) : GameAction()
    
    // --- 立直与和了 ---
    /** 宣告立直 */
    data class DeclareRiichi(val player: Int, val discardTile: Tile) : GameAction()
    
    /** 宣告自摸 */
    data class DeclareTsumo(val player: Int) : GameAction()
    
    /** 宣告荣和 */
    data class DeclareRon(val player: Int) : GameAction()
    
    // --- 千术动作 ---
    /** 使用千术绝技 */
    data class UseCheatTechnique(
        val player: Int,
        val technique: com.majiang.event.CheatTechnique,
        val params: Map<String, Any> = emptyMap()
    ) : GameAction()
    
    // --- 响应窗口 ---
    /** 跳过/放弃副露机会 */
    data class Pass(val player: Int) : GameAction()
    
    /** 九种九牌宣言流局 */
    data class DeclareAbortiveDraw(val player: Int) : GameAction()

    // --- 场次终了选择 ---
    /** 尾庄选择和了/听牌终了 (Agari-Yame) */
    data class DeclareAgariYame(val player: Int) : GameAction()
}
