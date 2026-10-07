package com.majiang.session

import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Tile
import com.majiang.state.GameAction
import java.util.Collections

/**
 * 鸣牌选择类别
 */
enum class MeldChoiceKind {
    CHI,
    PON,
    KAN
}

/**
 * 鸣牌候选方案数据模型。
 *
 * @property action 对应的领域动作（深拷贝并包装为不可变集合）
 * @property type 副露类型 (CHI / PON / OPEN_KAN / CLOSED_KAN / ADDED_KAN)
 * @property tiles 完整组合包含的所有实际牌面（吃/碰3张，杠4张）
 * @property handTiles 参与本次鸣牌的精确本人手牌（加杠仅包含补入的第4张）
 * @property calledTile 外来被鸣牌（加杠时来自原碰组；暗杠为 null）
 * @property addedTile 加杠补入的第4张牌（非加杠为 null）
 * @property fromPlayer 外来牌来源座位号（加杠来自原碰；暗杠为 null）
 */
data class MeldChoice(
    val action: GameAction,
    val type: MeldType,
    val tiles: List<Tile>,
    val handTiles: List<Tile>,
    val calledTile: Tile? = null,
    val addedTile: Tile? = null,
    val fromPlayer: Int? = null
)

/**
 * 鸣牌牌组选择提供器。
 *
 * 纯只读计算组件：
 * 1. 严格只消费 view.legalActions 且 action.player == viewerSeat；不生成或猜测新合法动作；
 * 2. 保持合法动作原始顺序，去除完全重复动作，不丢失不同动作；
 * 3. 所有列表均深拷贝为不可变集合（含 action 内 tiles/meld.tiles）；
 * 4. 严格以本人手牌与公开信息为准，不虚构牌，无领域副作用。
 */
object MeldChoiceProvider {

    /**
     * 为当前观察者在指定类别下提供所有合法的鸣牌牌组选项。
     */
    fun options(view: PlayerView, kind: MeldChoiceKind): List<MeldChoice> {
        if (view.viewerSeat !in 0..3) return emptyList()

        val myClosedTiles = view.myAllClosedTiles
        val myMelds = view.melds.getOrNull(view.viewerSeat) ?: emptyList()
        val results = mutableListOf<MeldChoice>()
        val seenActions = mutableSetOf<GameAction>()
        val seenChoiceKeys = mutableSetOf<Any>()

        for (action in view.legalActions) {
            if (!seenActions.add(action)) continue

            val choice = when (kind) {
                MeldChoiceKind.CHI -> {
                    if (action !is GameAction.DeclareChi || action.player != view.viewerSeat) {
                        null
                    } else {
                        buildChiChoice(action, myClosedTiles, view.viewerSeat)
                    }
                }
                MeldChoiceKind.PON -> {
                    if (action !is GameAction.DeclarePon || action.player != view.viewerSeat) {
                        null
                    } else {
                        buildPonChoice(action, myClosedTiles, view.lastDiscard, view.lastDiscardPlayer, view.viewerSeat)
                    }
                }
                MeldChoiceKind.KAN -> {
                    if (action !is GameAction.DeclareKan || action.player != view.viewerSeat) {
                        null
                    } else {
                        buildKanChoice(action, myClosedTiles, myMelds, view.lastDiscard, view.lastDiscardPlayer, view.viewerSeat)
                    }
                }
            } ?: continue

            val dedupKey = ChoiceDeduplicationKey(
                kind = kind,
                type = choice.type,
                handTiles = choice.handTiles.toSet(),
                calledTile = choice.calledTile,
                addedTile = choice.addedTile,
                fromPlayer = choice.fromPlayer
            )
            if (seenChoiceKeys.add(dedupKey)) {
                results.add(choice)
            }
        }

        return Collections.unmodifiableList(results)
    }

    private data class ChoiceDeduplicationKey(
        val kind: MeldChoiceKind,
        val type: MeldType,
        val handTiles: Set<Tile>,
        val calledTile: Tile?,
        val addedTile: Tile?,
        val fromPlayer: Int?
    )

    private fun buildChiChoice(
        action: GameAction.DeclareChi,
        myClosedTiles: List<Tile>,
        viewerSeat: Int
    ): MeldChoice? {
        val meld = action.meld
        if (meld.type != MeldType.CHI) return null
        if (meld.tiles.size != 3) return null

        val calledTile = meld.calledTile
        val calledIdx = meld.tiles.indexOfFirst { it == calledTile }
        if (calledIdx == -1) return null

        val handTiles = meld.tiles.toMutableList().apply { removeAt(calledIdx) }
        if (handTiles.size != 2) return null

        if (!matchesHandTiles(handTiles, myClosedTiles)) return null

        val fromPlayer = meld.fromPlayer
        if (fromPlayer !in 0..3 || fromPlayer == viewerSeat) return null

        val safeAction = action.copy(
            meld = meld.copy(
                tiles = Collections.unmodifiableList(meld.tiles.toList())
            )
        )

        return MeldChoice(
            action = safeAction,
            type = MeldType.CHI,
            tiles = Collections.unmodifiableList(meld.tiles.toList()),
            handTiles = Collections.unmodifiableList(handTiles),
            calledTile = calledTile,
            addedTile = null,
            fromPlayer = fromPlayer
        )
    }

    private fun buildPonChoice(
        action: GameAction.DeclarePon,
        myClosedTiles: List<Tile>,
        lastDiscard: Tile?,
        lastDiscardPlayer: Int?,
        viewerSeat: Int
    ): MeldChoice? {
        if (action.tiles.size != 2) return null
        if (!matchesHandTiles(action.tiles, myClosedTiles)) return null

        val calledTile = lastDiscard ?: return null
        val fromPlayer = lastDiscardPlayer ?: return null
        if (fromPlayer !in 0..3 || fromPlayer == viewerSeat) return null

        if (action.tiles.any { !it.sameType(calledTile) }) return null

        val safeAction = action.copy(
            tiles = Collections.unmodifiableList(action.tiles.toList())
        )
        val fullTiles = listOf(action.tiles[0], action.tiles[1], calledTile)

        return MeldChoice(
            action = safeAction,
            type = MeldType.PON,
            tiles = Collections.unmodifiableList(fullTiles),
            handTiles = Collections.unmodifiableList(action.tiles.toList()),
            calledTile = calledTile,
            addedTile = null,
            fromPlayer = fromPlayer
        )
    }

    private fun buildKanChoice(
        action: GameAction.DeclareKan,
        myClosedTiles: List<Tile>,
        myMelds: List<Meld>,
        lastDiscard: Tile?,
        lastDiscardPlayer: Int?,
        viewerSeat: Int
    ): MeldChoice? {
        return when (action.kanType) {
            MeldType.OPEN_KAN -> buildOpenKanChoice(action, myClosedTiles, lastDiscard, lastDiscardPlayer, viewerSeat)
            MeldType.CLOSED_KAN -> buildClosedKanChoice(action, myClosedTiles)
            MeldType.ADDED_KAN -> buildAddedKanChoice(action, myClosedTiles, myMelds)
            else -> null
        }
    }

    private fun buildOpenKanChoice(
        action: GameAction.DeclareKan,
        myClosedTiles: List<Tile>,
        lastDiscard: Tile?,
        lastDiscardPlayer: Int?,
        viewerSeat: Int
    ): MeldChoice? {
        if (action.tiles.size != 3) return null
        if (!matchesHandTiles(action.tiles, myClosedTiles)) return null

        val calledTile = lastDiscard ?: return null
        val fromPlayer = lastDiscardPlayer ?: return null
        if (fromPlayer !in 0..3 || fromPlayer == viewerSeat) return null

        if (action.tiles.any { !it.sameType(calledTile) }) return null

        val safeAction = action.copy(
            tiles = Collections.unmodifiableList(action.tiles.toList())
        )
        val fullTiles = action.tiles + calledTile

        return MeldChoice(
            action = safeAction,
            type = MeldType.OPEN_KAN,
            tiles = Collections.unmodifiableList(fullTiles),
            handTiles = Collections.unmodifiableList(action.tiles.toList()),
            calledTile = calledTile,
            addedTile = null,
            fromPlayer = fromPlayer
        )
    }

    private fun buildClosedKanChoice(
        action: GameAction.DeclareKan,
        myClosedTiles: List<Tile>
    ): MeldChoice? {
        if (action.tiles.size != 4) return null
        if (!matchesHandTiles(action.tiles, myClosedTiles)) return null

        val first = action.tiles.first()
        if (action.tiles.any { !it.sameType(first) }) return null

        val safeAction = action.copy(
            tiles = Collections.unmodifiableList(action.tiles.toList())
        )

        return MeldChoice(
            action = safeAction,
            type = MeldType.CLOSED_KAN,
            tiles = Collections.unmodifiableList(action.tiles.toList()),
            handTiles = Collections.unmodifiableList(action.tiles.toList()),
            calledTile = null,
            addedTile = null,
            fromPlayer = null
        )
    }

    private fun buildAddedKanChoice(
        action: GameAction.DeclareKan,
        myClosedTiles: List<Tile>,
        myMelds: List<Meld>
    ): MeldChoice? {
        if (action.tiles.size != 4) return null

        // 依据 view.melds[viewerSeat] 精确匹配原 PON
        val originalPon = myMelds.firstOrNull { meld ->
            meld.type == MeldType.PON && meld.tiles.size == 3 && meld.tiles.all { pt -> action.tiles.contains(pt) }
        } ?: return null

        // 找出本人补入的第 4 张牌
        val addedTile = action.tiles.firstOrNull { pt -> !originalPon.tiles.contains(pt) } ?: return null

        // 验证补入牌属于本人当前手牌
        if (!myClosedTiles.contains(addedTile)) return null

        // 验证补入牌与原碰牌同种
        if (!addedTile.sameType(originalPon.tiles.first())) return null

        val safeAction = action.copy(
            tiles = Collections.unmodifiableList(action.tiles.toList())
        )
        val fullTiles = originalPon.tiles + addedTile

        return MeldChoice(
            action = safeAction,
            type = MeldType.ADDED_KAN,
            tiles = Collections.unmodifiableList(fullTiles),
            handTiles = Collections.unmodifiableList(listOf(addedTile)),
            calledTile = originalPon.calledTile,
            addedTile = addedTile,
            fromPlayer = originalPon.fromPlayer
        )
    }

    private fun matchesHandTiles(requiredTiles: List<Tile>, availableTiles: List<Tile>): Boolean {
        val remaining = availableTiles.toMutableList()
        for (tile in requiredTiles) {
            val idx = remaining.indexOfFirst { it == tile }
            if (idx == -1) return false
            remaining.removeAt(idx)
        }
        return true
    }
}
