package com.majiang.session

import com.majiang.model.PlayerHand
import com.majiang.model.Tile
import com.majiang.rules.FuritenRules
import com.majiang.state.GamePhase
import java.util.Collections

/**
 * 听牌待牌预览提供器 (Task ANTI-END-TENPAI-IMPL)。
 *
 * 纯只读计算组件，负责从当前观察者座位的 [PlayerView] 推导所有和牌所需的理论待牌列表。
 *
 * 规则契约：
 * 1. 只读本人视图，复用 [FuritenRules.waits]，不复制分解算法；
 * 2. 输出按 typeIndex 去重升序排序、普通非赤展示牌 ([Tile.fromTypeIndex])、不可修改列表；
 * 3. 仅使用本人暗牌与已有副露，绝不读取未知牌山、他家暗牌或精确剩余张数；
 * 4. 有效等待形为暗牌数 + 副露数 * 3 == 13：
 *    - 当本人刚摸牌且 [PlayerView.myDrawnTile] 非空时，使用 [PlayerView.myClosedTiles] 推导摸牌前等待形；
 *    - 打牌后改用打牌后的新等待形；
 *    - 吃碰后尚未弃牌、其余 14 张等效形、准备/终局阶段 (STACKING, DEALING, ROUND_END, MATCH_END) 不展示待牌；
 * 5. DRAW, CHEAT_WINDOW, DISCARD, RESPONSE_WINDOW, CHANKAN_WINDOW, KAN_DRAW 等正常打牌阶段按此逻辑推导；
 * 6. 杠按一个面子计数，且自动排除本人闭手与副露中已占满四张的第五张候选。
 */
object TenpaiPreviewProvider {

    private val ACTIVE_PHASES = setOf(
        GamePhase.DRAW,
        GamePhase.CHEAT_WINDOW,
        GamePhase.DISCARD,
        GamePhase.RESPONSE_WINDOW,
        GamePhase.CHANKAN_WINDOW,
        GamePhase.KAN_DRAW
    )

    /**
     * 推导当前观察者座位的理论听牌待牌列表。
     *
     * @param view 当前座位的不可变只读视图
     * @return 理论待牌列表（按 typeIndex 升序排序的纯展示牌，不可修改集合；若未听牌或处于非等待形阶段则返回空列表）
     */
    fun waits(view: PlayerView): List<Tile> {
        // 1. 阶段校验：准备与终局阶段不计算/展示旧待牌
        if (view.phase !in ACTIVE_PHASES) {
            return Collections.emptyList()
        }

        // 2. 提取观察者本人已有副露
        val myMelds = view.melds.getOrNull(view.viewerSeat) ?: emptyList()

        // 3. 提取用于判定听牌的有效门前暗牌集合
        // 规则约束：有效等待形必须满足 暗牌数 + 副露数 * 3 == 13。
        // 当自己摸牌时 (myDrawnTile != null)，myClosedTiles 即为摸牌前的 13 张等效闭手；打牌后 myClosedTiles 为新等效闭手。
        // 若刚刚吃碰尚未弃牌，此时闭手 + melds*3 为 14 张，不满足等待形，直接返回空列表。
        val candidateClosed = view.myClosedTiles
        if (candidateClosed.size + myMelds.size * 3 != 13) {
            return Collections.emptyList()
        }

        // 4. 构建用于纯待牌判定的手牌模型
        val hand = PlayerHand().apply {
            closedTiles.addAll(candidateClosed)
            melds.addAll(myMelds)
        }

        // 5. 复用 FuritenRules.waits（已处理 HandDecomposer 拆解、国士13面、七对子、四张上限过滤）
        val waitIndices = FuritenRules.waits(hand, candidateClosed)
        if (waitIndices.isEmpty()) {
            return Collections.emptyList()
        }

        // 6. 按 typeIndex 排序并转换为普通非赤展示牌
        val result = waitIndices
            .sorted()
            .map { Tile.fromTypeIndex(it) }

        return Collections.unmodifiableList(result)
    }
}
