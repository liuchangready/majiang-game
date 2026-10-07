package com.majiang.session

import com.majiang.access.MeldPreviewOption
import com.majiang.access.PlayerPresentationPreview
import com.majiang.access.RiichiPreviewOption
import com.majiang.application.PlayerDecision
import java.util.Collections

/**
 * 玩家私有展示预览提供器 (PlayerPresentationPreviewProvider)
 *
 * 契约规范：
 * 1. 严格从同一次 TableAuthority.querySnapshot 产生的真实 [PlayerView] 与 [PlayerDecision] 生成；
 * 2. 复用 [TenpaiPreviewProvider.waits]、[RiichiPreviewProvider.options]、[MeldChoiceProvider.options]，
 *    绝不复制和牌、振听或鸣牌算法，保持已验证算法绝对单一来源；
 * 3. 严格绑定 roundId、decisionId、controlEpoch；若无有效局 (decision.roundId 为空) 返回 null；
 * 4. 每个可提交候选严格按完整 [com.majiang.state.GameAction] 等值匹配当前 [decision.options] 中真实 optionId，
 *    保真赤五与同牌种不同物理副本；
 * 5. 决策无候选（如 auto-control 托管席位）时不杜撰可提交动作，仅显示真实可用候选；
 * 6. 返回对象内集合全部包装为不可修改集合。
 */
object PlayerPresentationPreviewProvider {

    /**
     * 根据当前观察者座位的只读视图与权威决策生成私有展示预览。
     *
     * @param view 当前座位的不可变只读视图
     * @param decision 当前座位的权威合法决策
     * @return 展示预览聚合根；若无活动局则返回 null
     */
    fun createPreview(view: PlayerView, decision: PlayerDecision): PlayerPresentationPreview? {
        val roundId = decision.roundId ?: return null
        if (view.viewerSeat !in 0..3) return null

        val decisionId = decision.decisionId
        val controlEpoch = decision.controlEpoch

        // 1. 复用 TenpaiPreviewProvider 推导理论待牌（非等待形、准备及终局阶段自动返回空列表）
        val waits = TenpaiPreviewProvider.waits(view)

        // 2. 复用 RiichiPreviewProvider 推导立直弃牌方案，并严格与 decision.options 匹配绑定 optionId
        val riichiChoices = RiichiPreviewProvider.options(view)
        val matchedOptionIds = mutableSetOf<String>()
        val riichiOptions = mutableListOf<RiichiPreviewOption>()

        for (choice in riichiChoices) {
            val matchedOption = decision.options.firstOrNull {
                it.action == choice.action && it.optionId !in matchedOptionIds
            }
            if (matchedOption != null) {
                matchedOptionIds.add(matchedOption.optionId)
                riichiOptions.add(
                    RiichiPreviewOption(
                        optionId = matchedOption.optionId,
                        action = choice.action,
                        waits = choice.waits,
                        furiten = choice.furiten
                    )
                )
            }
        }

        // 3. 复用 MeldChoiceProvider 分别推导吃、碰、杠候选，并严格与 decision.options 匹配绑定 optionId
        val allMeldChoices = mutableListOf<MeldChoice>()
        allMeldChoices.addAll(MeldChoiceProvider.options(view, MeldChoiceKind.CHI))
        allMeldChoices.addAll(MeldChoiceProvider.options(view, MeldChoiceKind.PON))
        allMeldChoices.addAll(MeldChoiceProvider.options(view, MeldChoiceKind.KAN))

        val meldOptions = mutableListOf<MeldPreviewOption>()
        for (choice in allMeldChoices) {
            val matchedOption = decision.options.firstOrNull {
                it.action == choice.action && it.optionId !in matchedOptionIds
            }
            if (matchedOption != null) {
                matchedOptionIds.add(matchedOption.optionId)
                meldOptions.add(
                    MeldPreviewOption(
                        optionId = matchedOption.optionId,
                        action = choice.action,
                        type = choice.type,
                        tiles = choice.tiles,
                        handTiles = choice.handTiles,
                        calledTile = choice.calledTile,
                        addedTile = choice.addedTile,
                        fromPlayer = choice.fromPlayer
                    )
                )
            }
        }

        return PlayerPresentationPreview(
            roundId = roundId,
            decisionId = decisionId,
            controlEpoch = controlEpoch,
            waits = waits,
            riichiOptions = Collections.unmodifiableList(riichiOptions),
            meldOptions = Collections.unmodifiableList(meldOptions)
        )
    }
}
