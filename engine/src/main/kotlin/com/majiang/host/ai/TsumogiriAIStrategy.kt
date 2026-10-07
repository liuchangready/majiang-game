package com.majiang.host.ai

import com.majiang.ai.AIStrategy
import com.majiang.session.PlayerView
import com.majiang.state.GameAction

/**
 * 摸切 AI 策略 (TsumogiriAIStrategy)
 *
 * 纯粹的摸切与放弃副露策略：
 * 1. 响应窗口一律选择 Pass；
 * 2. 摸打回合一律摸切当前摸入的牌（若存在且合法）；
 * 3. 若无可摸切牌（如副露后必须弃牌），确定性选择首个合法弃牌动作；
 * 4. 绝不主动和牌、立直、吃碰杠或出千。
 */
class TsumogiriAIStrategy : AIStrategy {
    override val name: String = "摸切AI"

    override fun decideAction(view: PlayerView, legalActions: List<GameAction>): GameAction? {
        if (legalActions.isEmpty()) return null

        // 1. 响应窗口优先 Pass
        val pass = legalActions.firstOrNull { it is GameAction.Pass && it.player == view.viewerSeat }
            ?: legalActions.firstOrNull { it is GameAction.Pass }
        if (pass != null) {
            return pass
        }

        // 2. 摸打阶段优先摸切 (Tsumogiri)
        val drawn = view.myDrawnTile
        if (drawn != null) {
            val tsumogiri = legalActions.firstOrNull {
                it is GameAction.DiscardTile && it.player == view.viewerSeat && it.tile == drawn
            } ?: legalActions.firstOrNull {
                it is GameAction.DiscardTile && it.tile == drawn
            }
            if (tsumogiri != null) {
                return tsumogiri
            }
        }

        // 3. 否则确定性选择首个合法弃牌动作
        val discard = legalActions.firstOrNull { it is GameAction.DiscardTile && it.player == view.viewerSeat }
            ?: legalActions.firstOrNull { it is GameAction.DiscardTile }
        if (discard != null) {
            return discard
        }

        // 4. 兜底返回首个合法动作
        return legalActions.firstOrNull()
    }
}
