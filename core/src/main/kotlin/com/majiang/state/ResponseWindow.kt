package com.majiang.state

import com.majiang.model.MeldType
import com.majiang.model.Tile

/**
 * 响应窗口模型 (DESIGN.md 4.2)
 *
 * 替代旧有的单槽位 pendingAiResponse，支持全座位平权响应与裁决：
 * - 收集所有具有响应资格的座位的操作（吃/碰/大明杠/荣和/抢杠/Pass）
 * - AI 与人类共用相同的收集与裁决流程
 * - 幂等记录响应，重复响应无副作用
 * - 只有所有有资格座位均提交响应后，窗口才算收集完成
 *
 * @property id 窗口唯一标识
 * @property fromPlayer 产生响应源的玩家（打牌者或加杠者）
 * @property targetTile 触发响应的牌（打出的牌或加杠的牌）
 * @property eligibleActions 各座位的候选合法动作集合 (seat -> actions)
 * @property responses 已提交的座位响应映射 (seat -> action)
 * @property isChankan 是否为抢杠窗口
 * @property pendingKanAction 若为抢杠窗口，暂存待提交的加杠动作
 */
data class ResponseWindow(
    val id: Int,
    val fromPlayer: Int,
    val targetTile: Tile,
    val eligibleActions: Map<Int, List<GameAction>>,
    val responses: MutableMap<Int, GameAction> = mutableMapOf(),
    val isChankan: Boolean = false,
    val pendingKanAction: GameAction.DeclareKan? = null
) {
    /** 具有响应资格的座位集合（不含弃牌者本身） */
    val eligibleSeats: Set<Int> get() = eligibleActions.keys

    /** 所有有资格的座位是否均已完成响应 */
    val isComplete: Boolean
        get() = ResponseResolver.INSTANCE.isWindowComplete(eligibleSeats, responses)

    /** 指定座位是否已经提交过响应 */
    fun hasResponded(player: Int): Boolean = responses.containsKey(player)

    /**
     * 寻找与给定动作匹配的候选合法动作（对于无序牌组映射到合法列表中的规范动作）
     */
    fun findMatchingEligibleAction(player: Int, action: GameAction): GameAction? {
        if (getActionPlayer(action) != player) return null
        val candidates = eligibleActions[player] ?: return null
        return candidates.firstOrNull { allowed ->
            matchesEligibleAction(action, allowed)
        }
    }

    /**
     * 记录指定座位的响应动作
     * 保证幂等性：若该座位已有记录，不覆盖已有响应
     * 防御性校验：拒绝错座位或非候选合法动作；若通过则以规范动作记录
     */
    fun recordResponse(player: Int, action: GameAction): Boolean {
        if (!eligibleSeats.contains(player)) return false
        if (responses.containsKey(player)) return false
        val matchedAction = findMatchingEligibleAction(player, action) ?: return false
        responses[player] = matchedAction
        return true
    }

    /**
     * 完整深拷贝，供候选事务计算与防御复制
     */
    fun deepCopy(): ResponseWindow {
        return ResponseWindow(
            id = id,
            fromPlayer = fromPlayer,
            targetTile = targetTile,
            eligibleActions = eligibleActions.mapValues { (_, actions) -> actions.map { deepCopyAction(it) } },
            responses = responses.mapValues { (_, action) -> deepCopyAction(action) }.toMutableMap(),
            isChankan = isChankan,
            pendingKanAction = pendingKanAction?.let { deepCopyAction(it) as GameAction.DeclareKan }
        )
    }

    companion object {
        internal fun deepCopyAction(action: GameAction): GameAction = when (action) {
            is GameAction.StartStacking -> action.copy(stackingPlans = action.stackingPlans.toList())
            is GameAction.DealComplete -> action
            is GameAction.DrawTile -> action.copy()
            is GameAction.DiscardTile -> action.copy(tile = action.tile.copy())
            is GameAction.DeclareChi -> action.copy(meld = action.meld.copy(tiles = action.meld.tiles.map { it.copy() }))
            is GameAction.DeclarePon -> action.copy(tiles = action.tiles.map { it.copy() })
            is GameAction.DeclareKan -> action.copy(tiles = action.tiles.map { it.copy() })
            is GameAction.DeclareRiichi -> action.copy(discardTile = action.discardTile.copy())
            is GameAction.DeclareTsumo -> action.copy()
            is GameAction.DeclareRon -> action.copy()
            is GameAction.UseCheatTechnique -> action.copy(params = action.params.toMap())
            is GameAction.Pass -> action.copy()
            is GameAction.DeclareAbortiveDraw -> action.copy()
            is GameAction.DeclareAgariYame -> action.copy()
        }

        internal fun getActionPlayer(action: GameAction): Int = when (action) {
            is GameAction.DiscardTile -> action.player
            is GameAction.DrawTile -> action.player
            is GameAction.DeclareChi -> action.player
            is GameAction.DeclarePon -> action.player
            is GameAction.DeclareKan -> action.player
            is GameAction.DeclareRiichi -> action.player
            is GameAction.DeclareTsumo -> action.player
            is GameAction.DeclareRon -> action.player
            is GameAction.Pass -> action.player
            is GameAction.DeclareAbortiveDraw -> action.player
            is GameAction.DeclareAgariYame -> action.player
            is GameAction.UseCheatTechnique -> action.player
            else -> -1
        }

        internal fun matchesEligibleAction(action: GameAction, allowed: GameAction): Boolean {
            return when (action) {
                is GameAction.Pass -> allowed is GameAction.Pass && allowed.player == action.player
                is GameAction.DeclareRon -> allowed is GameAction.DeclareRon && allowed.player == action.player
                is GameAction.DeclarePon -> {
                    allowed is GameAction.DeclarePon &&
                            allowed.player == action.player &&
                            allowed.tiles.size == action.tiles.size &&
                            allowed.tiles.containsAll(action.tiles) &&
                            action.tiles.containsAll(allowed.tiles)
                }
                is GameAction.DeclareChi -> {
                    allowed is GameAction.DeclareChi &&
                            allowed.player == action.player &&
                            action.meld.type == MeldType.CHI &&
                            allowed.meld.type == MeldType.CHI &&
                            allowed.meld.fromPlayer == action.meld.fromPlayer &&
                            allowed.meld.calledTile == action.meld.calledTile &&
                            allowed.meld.tiles.size == action.meld.tiles.size &&
                            allowed.meld.tiles.containsAll(action.meld.tiles) &&
                            action.meld.tiles.containsAll(allowed.meld.tiles)
                }
                is GameAction.DeclareKan -> {
                    allowed is GameAction.DeclareKan &&
                            allowed.player == action.player &&
                            allowed.kanType == MeldType.OPEN_KAN &&
                            action.kanType == MeldType.OPEN_KAN &&
                            allowed.tiles.size == action.tiles.size &&
                            allowed.tiles.containsAll(action.tiles) &&
                            action.tiles.containsAll(allowed.tiles)
                }
                else -> false
            }
        }
    }
}
