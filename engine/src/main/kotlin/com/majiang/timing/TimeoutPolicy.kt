package com.majiang.timing

import com.majiang.model.Tile
import com.majiang.session.PlayerView
import com.majiang.state.GameAction

/**
 * 权威超时默认决策策略 (TimeoutPolicy)
 *
 * 当玩家决策计时耗尽时，由权威宿主调用此策略生成默认动作。
 *
 * 核心决策优先级（V3 架构 §7.4）：
 * 1. 优先响应 [GameAction.Pass]（放弃吃碰杠荣和等副露/和牌机会）；
 * 2. 否则合法摸切（若有当前摸牌 [PlayerView.myDrawnTile]，且其作为弃牌在合法候选中）；
 * 3. 否则确定性合法弃牌（从合法候选中按牌张自然序升序选择最小合法弃牌，不盲目打手牌末尾）；
 * 4. 严禁自动选择自摸 ([GameAction.DeclareTsumo])、荣和 ([GameAction.DeclareRon])、副露杠牌 ([GameAction.DeclareChi] / [GameAction.DeclarePon] / [GameAction.DeclareKan])、立直宣言 ([GameAction.DeclareRiichi]) 或千术动作；
 * 5. 若无合法必选默认动作，返回 null 由权威宿主报错与异常处理。
 */
object TimeoutPolicy {

    /**
     * 基于玩家视图与合法候选动作列表选择超时默认动作
     */
    fun choose(view: PlayerView, options: List<GameAction>): GameAction? {
        return choose(
            options = options,
            viewerSeat = view.viewerSeat,
            drawnTile = view.myDrawnTile
        )
    }

    /**
     * 重载入口：基于候选列表、座位与可选摸牌选择超时默认动作
     */
    fun choose(options: List<GameAction>, viewerSeat: Int = 0, drawnTile: Tile? = null): GameAction? {
        if (options.isEmpty()) return null

        // 1. 优先响应 Pass (跳过副露/和了机会)
        val passAction = options.firstOrNull { it is GameAction.Pass && it.player == viewerSeat }
            ?: options.firstOrNull { it is GameAction.Pass }
        if (passAction != null) {
            return passAction
        }

        // 2. 否则合法摸切 (Tsumogiri)
        if (drawnTile != null) {
            val tsumogiri = options.firstOrNull {
                it is GameAction.DiscardTile && it.player == viewerSeat && it.tile == drawnTile
            } ?: options.firstOrNull {
                it is GameAction.DiscardTile && it.tile == drawnTile
            }
            if (tsumogiri != null) {
                return tsumogiri
            }
        }

        // 3. 否则确定性合法弃牌 (按 Tile 自然顺序：花色 -> 点数 -> copyIndex 升序取第一个)
        val discards = options.filterIsInstance<GameAction.DiscardTile>()
        val seatDiscards = discards.filter { it.player == viewerSeat }.ifEmpty { discards }
        if (seatDiscards.isNotEmpty()) {
            return seatDiscards.minByOrNull { it.tile }
        }

        // 4. 无合法必选默认动作（不自动和牌、杠、副露或立直）
        return null
    }
}
