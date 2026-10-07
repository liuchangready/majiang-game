package com.majiang.session

import com.majiang.model.PlayerHand
import com.majiang.model.Tile
import com.majiang.rules.FuritenRules
import com.majiang.rules.FuritenStatus
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import java.util.Collections

/**
 * 玩家宣告立直时的待选打牌选项及其对应的理论和牌待牌列表与振听预判。
 *
 * @property action 对应的立直宣告动作（绑定精确的物理弃牌）
 * @property waits 该弃牌后手牌能和的理论牌种（按 typeIndex 去重升序排序，纯展示牌）
 * @property furiten 该弃牌方案产生的振听预判状态（包含准备打出的物理牌）
 */
data class RiichiDiscardOption(
    val action: GameAction.DeclareRiichi,
    val waits: List<Tile>,
    val furiten: FuritenStatus = FuritenStatus()
)

/**
 * 立直选牌与待牌预览提供器。
 *
 * 纯计算组件，负责从当前座位的 [PlayerView] 推导所有可行的立直弃牌方案及其对应的理论待牌列表。
 */
object RiichiPreviewProvider {

    /**
     * 为当前观察者在满足立直宣言条件的回合生成所有合法的立直打牌选项及对应的理论待牌列表。
     *
     * 规则约束：
     * 1. 必须是观察者自己的回合、DISCARD 或 CHEAT_WINDOW 阶段、尚未立直；
     * 2. 仅从当前观察者合法 DeclareRiichi 动作生成；
     * 3. 按精确 Tile 身份从 myAllClosedTiles 移除一张；移除失败则不生成；
     * 4. 相同牌种不同物理副本保留不同 action，不丢失真正选中的物理牌；
     * 5. 用共享 FuritenRules 求待牌与振听状态，排除剩余暗牌 + 自己已有副露中已占满四张的第五张候选；
     * 6. 不要按他家暗牌、牌山顺序或公开河里已见数量过滤理论待牌，不显示可见剩余数；
     * 7. 待牌按 typeIndex 去重排序，用 Tile.fromTypeIndex 作纯展示牌；
     * 8. 输出集合及内部 waits 均包装为不可修改集合，输入视图保持不变。
     */
    fun options(view: PlayerView): List<RiichiDiscardOption> {
        // 1. 校验前置阶段与回合状态
        if (!view.isMyTurn) return emptyList()
        if (view.phase != GamePhase.DISCARD && view.phase != GamePhase.CHEAT_WINDOW) return emptyList()
        if (view.riichiStatus.getOrNull(view.viewerSeat) == true) return emptyList()

        // 2. 仅从当前观察者合法 DeclareRiichi 动作生成
        val riichiActions = view.legalActions
            .filterIsInstance<GameAction.DeclareRiichi>()
            .filter { it.player == view.viewerSeat }
        if (riichiActions.isEmpty()) return emptyList()

        // 3. 提取自己已有副露
        val myMelds = view.melds.getOrNull(view.viewerSeat) ?: emptyList()

        val allClosed = view.myAllClosedTiles
        val options = mutableListOf<RiichiDiscardOption>()

        // 4. 遍历合法立直动作（相同牌种不同副本保留不同 action）
        for (action in riichiActions) {
            // 按精确 Tile 物理身份从 myAllClosedTiles 移除一张；移除失败则不生成
            val removeIndex = allClosed.indexOfFirst { it == action.discardTile }
            if (removeIndex == -1) continue

            val remainingClosed = allClosed.toMutableList()
            remainingClosed.removeAt(removeIndex)

            // 构建预览临时手牌模型（完整舍牌历史加上准备打出的牌，保留当前 temporary/riichi 标记）
            val previewHand = PlayerHand().apply {
                closedTiles.addAll(remainingClosed)
                melds.addAll(myMelds)
                val history = if (view.myDiscardHistory.isNotEmpty()) {
                    view.myDiscardHistory
                } else {
                    view.discards.getOrNull(view.viewerSeat) ?: emptyList()
                }
                discardHistory.addAll(history)
                discardHistory.add(action.discardTile)
            }

            // 用共享 FuritenRules 求理论待牌与振听状态
            val waitTiles = FuritenRules.waits(previewHand, remainingClosed)
                .sorted()
                .map { Tile.fromTypeIndex(it) }

            val optionFuriten = FuritenRules.status(
                hand = previewHand,
                closedTiles = remainingClosed,
                temporary = view.myFuriten.temporary,
                riichi = view.myFuriten.riichi
            )

            options.add(
                RiichiDiscardOption(
                    action = action,
                    waits = Collections.unmodifiableList(waitTiles),
                    furiten = optionFuriten
                )
            )
        }

        return Collections.unmodifiableList(options)
    }
}
