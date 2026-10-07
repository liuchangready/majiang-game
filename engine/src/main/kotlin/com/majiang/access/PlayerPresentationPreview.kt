package com.majiang.access

import com.majiang.model.MeldType
import com.majiang.model.Tile
import com.majiang.rules.FuritenStatus
import com.majiang.state.GameAction
import java.util.ArrayList
import java.util.Collections

private fun sanitizeGameAction(action: GameAction): GameAction = when (action) {
    is GameAction.DeclareChi -> {
        val safeMeldTiles = Collections.unmodifiableList(ArrayList(action.meld.tiles))
        val safeMeld = action.meld.copy(tiles = safeMeldTiles)
        action.copy(meld = safeMeld)
    }
    is GameAction.DeclarePon -> {
        val safeTiles = Collections.unmodifiableList(ArrayList(action.tiles))
        action.copy(tiles = safeTiles)
    }
    is GameAction.DeclareKan -> {
        val safeTiles = Collections.unmodifiableList(ArrayList(action.tiles))
        action.copy(tiles = safeTiles)
    }
    is GameAction.DeclareRiichi -> action.copy()
    else -> action
}

/**
 * 立直候选弃牌展示预览数据模型 (RiichiPreviewOption)
 *
 * 绑定当前可执行决策中的真实 optionId，并携带对应的立直宣告动作、打牌后理论待牌及振听状态。
 *
 * @property optionId 当前决策 options 中的真实选项标识
 * @property action 精确的立直宣告动作（绑定选中的物理弃牌）
 * @property waits 该弃牌后手牌能和的理论牌种（按 typeIndex 去重升序排序，纯展示牌）
 * @property furiten 该弃牌方案产生的振听预判状态
 */
class RiichiPreviewOption(
    val optionId: String,
    action: GameAction.DeclareRiichi,
    waits: List<Tile>,
    val furiten: FuritenStatus = FuritenStatus()
) {
    val action: GameAction.DeclareRiichi = action.copy()
    val discardTile: Tile get() = action.discardTile
    val waits: List<Tile> = Collections.unmodifiableList(ArrayList(waits))

    init {
        require(optionId.isNotBlank()) { "optionId cannot be blank" }
        require(discardTile == action.discardTile) { "discardTile must match action.discardTile" }
        require(waits.distinct().size == waits.size) { "waits cannot contain duplicate tiles: $waits" }
    }

    operator fun component1(): String = optionId
    operator fun component2(): GameAction.DeclareRiichi = action
    operator fun component3(): List<Tile> = waits
    operator fun component4(): FuritenStatus = furiten

    fun copy(
        optionId: String = this.optionId,
        action: GameAction.DeclareRiichi = this.action,
        waits: List<Tile> = this.waits,
        furiten: FuritenStatus = this.furiten
    ): RiichiPreviewOption = RiichiPreviewOption(optionId, action, waits, furiten)

    fun deepCopy(): RiichiPreviewOption = RiichiPreviewOption(
        optionId = optionId,
        action = action.copy(),
        waits = ArrayList(waits),
        furiten = furiten.copy()
    )

    override fun equals(other: Any?): Boolean = other is RiichiPreviewOption &&
        optionId == other.optionId && action == other.action &&
        waits == other.waits && furiten == other.furiten

    override fun hashCode(): Int {
        var r = optionId.hashCode()
        r = 31 * r + action.hashCode()
        r = 31 * r + waits.hashCode()
        r = 31 * r + furiten.hashCode()
        return r
    }

    override fun toString(): String =
        "RiichiPreviewOption(optionId=$optionId, action=$action, waits=$waits, furiten=$furiten)"
}

/**
 * 鸣牌候选展示预览数据模型 (MeldPreviewOption)
 *
 * 覆盖 chi / pon / open-kan / closed-kan / added-kan 五类鸣牌，
 * 绑定当前决策 options 中的真实 optionId 与精确物理牌。
 *
 * @property optionId 当前决策 options 中的真实选项标识
 * @property action 对应的领域动作
 * @property type 副露类型 (CHI / PON / OPEN_KAN / CLOSED_KAN / ADDED_KAN)
 * @property tiles 完整组合包含的所有实际牌面（吃/碰3张，杠4张）
 * @property handTiles 参与本次鸣牌的精确本人手牌（加杠仅包含补入的第4张）
 * @property calledTile 外来被鸣牌（加杠来自原碰组；暗杠为 null）
 * @property addedTile 加杠补入的第4张牌（非加杠为 null）
 * @property fromPlayer 外来牌来源座位号（加杠来自原碰；暗杠为 null）
 */
class MeldPreviewOption(
    val optionId: String,
    action: GameAction,
    val type: MeldType,
    tiles: List<Tile>,
    handTiles: List<Tile>,
    val calledTile: Tile? = null,
    val addedTile: Tile? = null,
    val fromPlayer: Int? = null
) {
    val action: GameAction = sanitizeGameAction(action)
    val tiles: List<Tile> = Collections.unmodifiableList(ArrayList(tiles))
    val handTiles: List<Tile> = Collections.unmodifiableList(ArrayList(handTiles))

    init {
        require(optionId.isNotBlank()) { "optionId cannot be blank" }

        when (action) {
            is GameAction.DeclareChi -> Unit
            is GameAction.DeclarePon -> Unit
            is GameAction.DeclareKan -> Unit
            else -> throw IllegalArgumentException("action must be DeclareChi, DeclarePon, or DeclareKan, got ${action::class.simpleName}")
        }

        require(tiles.distinct().size == tiles.size) {
            "tiles cannot contain duplicate physical tiles: $tiles"
        }
        require(handTiles.distinct().size == handTiles.size) {
            "handTiles cannot contain duplicate physical tiles: $handTiles"
        }
        require(tiles.containsAll(handTiles)) {
            "tiles must contain all handTiles: tiles=$tiles, handTiles=$handTiles"
        }

        when (type) {
            MeldType.CHI -> {
                require(action is GameAction.DeclareChi) {
                    "action must be DeclareChi for CHI, got ${action::class.simpleName}"
                }
                require(action.meld.type == MeldType.CHI) {
                    "action.meld.type must be CHI, got ${action.meld.type}"
                }
                require(tiles.size == 3) {
                    "CHI tiles must have size 3, got ${tiles.size}"
                }
                require(tiles == action.meld.tiles) {
                    "CHI tiles must match action.meld.tiles: tiles=$tiles, action.meld.tiles=${action.meld.tiles}"
                }
                require(calledTile != null) {
                    "calledTile cannot be null for CHI"
                }
                require(calledTile == action.meld.calledTile) {
                    "calledTile must match action.meld.calledTile: calledTile=$calledTile, action.meld.calledTile=${action.meld.calledTile}"
                }
                require(fromPlayer != null && fromPlayer in 0..3) {
                    "fromPlayer must be in 0..3 for CHI, got $fromPlayer"
                }
                require(fromPlayer == action.meld.fromPlayer) {
                    "fromPlayer must match action.meld.fromPlayer: fromPlayer=$fromPlayer, action.meld.fromPlayer=${action.meld.fromPlayer}"
                }
                require(fromPlayer != action.player) {
                    "fromPlayer cannot be the declaring player (${action.player})"
                }
                require(calledTile in tiles) {
                    "calledTile must be in tiles for CHI"
                }
                require(calledTile !in handTiles) {
                    "calledTile cannot be in handTiles for CHI"
                }
                require(handTiles.size == 2) {
                    "handTiles must have size 2 for CHI, got ${handTiles.size}"
                }
                require(handTiles == tiles.filter { it != calledTile }) {
                    "handTiles must match tiles excluding calledTile: handTiles=$handTiles, tiles=$tiles, calledTile=$calledTile"
                }
                require(addedTile == null) {
                    "addedTile must be null for CHI, got $addedTile"
                }
            }
            MeldType.PON -> {
                require(action is GameAction.DeclarePon) {
                    "action must be DeclarePon for PON, got ${action::class.simpleName}"
                }
                require(action.tiles.size == 2) {
                    "action.tiles must have size 2 for PON, got ${action.tiles.size}"
                }
                require(handTiles.size == 2 && handTiles == action.tiles) {
                    "handTiles must match action.tiles for PON: handTiles=$handTiles, action.tiles=${action.tiles}"
                }
                require(calledTile != null) {
                    "calledTile cannot be null for PON"
                }
                require(fromPlayer != null && fromPlayer in 0..3) {
                    "fromPlayer must be in 0..3 for PON, got $fromPlayer"
                }
                require(fromPlayer != action.player) {
                    "fromPlayer cannot be the declaring player (${action.player})"
                }
                require(calledTile !in handTiles) {
                    "calledTile cannot be in handTiles for PON"
                }
                require(tiles.size == 3) {
                    "tiles must have size 3 for PON, got ${tiles.size}"
                }
                require(tiles == handTiles + calledTile) {
                    "tiles must be handTiles + calledTile for PON: tiles=$tiles, expected=${handTiles + calledTile}"
                }
                require(tiles.all { it.sameType(tiles[0]) }) {
                    "all tiles in PON must be of the same type: $tiles"
                }
                require(addedTile == null) {
                    "addedTile must be null for PON, got $addedTile"
                }
            }
            MeldType.OPEN_KAN -> {
                require(action is GameAction.DeclareKan && action.kanType == MeldType.OPEN_KAN) {
                    "action must be DeclareKan(OPEN_KAN) for OPEN_KAN, got $action"
                }
                require(action.tiles.size == 3) {
                    "action.tiles must have size 3 for OPEN_KAN, got ${action.tiles.size}"
                }
                require(handTiles.size == 3 && handTiles == action.tiles) {
                    "handTiles must match action.tiles for OPEN_KAN: handTiles=$handTiles, action.tiles=${action.tiles}"
                }
                require(calledTile != null) {
                    "calledTile cannot be null for OPEN_KAN"
                }
                require(fromPlayer != null && fromPlayer in 0..3) {
                    "fromPlayer must be in 0..3 for OPEN_KAN, got $fromPlayer"
                }
                require(fromPlayer != action.player) {
                    "fromPlayer cannot be the declaring player (${action.player})"
                }
                require(calledTile !in handTiles) {
                    "calledTile cannot be in handTiles for OPEN_KAN"
                }
                require(tiles.size == 4) {
                    "tiles must have size 4 for OPEN_KAN, got ${tiles.size}"
                }
                require(tiles == handTiles + calledTile) {
                    "tiles must be handTiles + calledTile for OPEN_KAN: tiles=$tiles, expected=${handTiles + calledTile}"
                }
                require(tiles.all { it.sameType(tiles[0]) }) {
                    "all tiles in OPEN_KAN must be of the same type: $tiles"
                }
                require(addedTile == null) {
                    "addedTile must be null for OPEN_KAN, got $addedTile"
                }
            }
            MeldType.CLOSED_KAN -> {
                require(action is GameAction.DeclareKan && action.kanType == MeldType.CLOSED_KAN) {
                    "action must be DeclareKan(CLOSED_KAN) for CLOSED_KAN, got $action"
                }
                require(action.tiles.size == 4) {
                    "action.tiles must have size 4 for CLOSED_KAN, got ${action.tiles.size}"
                }
                require(handTiles.size == 4 && handTiles == action.tiles) {
                    "handTiles must match action.tiles for CLOSED_KAN: handTiles=$handTiles, action.tiles=${action.tiles}"
                }
                require(calledTile == null) {
                    "calledTile must be null for CLOSED_KAN, got $calledTile"
                }
                require(fromPlayer == null) {
                    "fromPlayer must be null for CLOSED_KAN, got $fromPlayer"
                }
                require(addedTile == null) {
                    "addedTile must be null for CLOSED_KAN, got $addedTile"
                }
                require(tiles.size == 4 && tiles == handTiles) {
                    "tiles must match handTiles for CLOSED_KAN: tiles=$tiles, handTiles=$handTiles"
                }
                require(tiles.all { it.sameType(tiles[0]) }) {
                    "all tiles in CLOSED_KAN must be of the same type: $tiles"
                }
            }
            MeldType.ADDED_KAN -> {
                require(action is GameAction.DeclareKan && action.kanType == MeldType.ADDED_KAN) {
                    "action must be DeclareKan(ADDED_KAN) for ADDED_KAN, got $action"
                }
                require(addedTile != null) {
                    "addedTile cannot be null for ADDED_KAN"
                }
                require(handTiles.size == 1 && handTiles[0] == addedTile) {
                    "handTiles must contain exactly the addedTile for ADDED_KAN: handTiles=$handTiles, addedTile=$addedTile"
                }
                require(action.tiles.size == 4) {
                    "action.tiles must have size 4 for ADDED_KAN, got ${action.tiles.size}"
                }
                require(action.tiles.containsAll(tiles) && tiles.containsAll(action.tiles)) {
                    "action.tiles must contain all tiles for ADDED_KAN"
                }
                require(calledTile != null) {
                    "calledTile cannot be null for ADDED_KAN"
                }
                require(fromPlayer != null && fromPlayer in 0..3) {
                    "fromPlayer must be in 0..3 for ADDED_KAN, got $fromPlayer"
                }
                require(fromPlayer != action.player) {
                    "fromPlayer cannot be the declaring player (${action.player})"
                }
                require(tiles.size == 4) {
                    "tiles must have size 4 for ADDED_KAN, got ${tiles.size}"
                }
                require(tiles.last() == addedTile) {
                    "tiles.last() must be addedTile for ADDED_KAN, got ${tiles.last()} vs $addedTile"
                }
                require(addedTile in tiles && calledTile in tiles) {
                    "tiles must contain addedTile and calledTile for ADDED_KAN"
                }
                require(addedTile != calledTile) {
                    "addedTile cannot equal calledTile for ADDED_KAN"
                }
                require(calledTile !in handTiles) {
                    "calledTile cannot be in handTiles for ADDED_KAN"
                }
                require(tiles.all { it.sameType(tiles[0]) }) {
                    "all tiles in ADDED_KAN must be of the same type: $tiles"
                }
            }
        }
    }

    operator fun component1(): String = optionId
    operator fun component2(): GameAction = action
    operator fun component3(): MeldType = type
    operator fun component4(): List<Tile> = tiles
    operator fun component5(): List<Tile> = handTiles
    operator fun component6(): Tile? = calledTile
    operator fun component7(): Tile? = addedTile
    operator fun component8(): Int? = fromPlayer

    fun copy(
        optionId: String = this.optionId,
        action: GameAction = this.action,
        type: MeldType = this.type,
        tiles: List<Tile> = this.tiles,
        handTiles: List<Tile> = this.handTiles,
        calledTile: Tile? = this.calledTile,
        addedTile: Tile? = this.addedTile,
        fromPlayer: Int? = this.fromPlayer
    ): MeldPreviewOption = MeldPreviewOption(
        optionId, action, type, tiles, handTiles, calledTile, addedTile, fromPlayer
    )

    fun deepCopy(): MeldPreviewOption = MeldPreviewOption(
        optionId = optionId,
        action = sanitizeGameAction(action),
        type = type,
        tiles = ArrayList(tiles),
        handTiles = ArrayList(handTiles),
        calledTile = calledTile,
        addedTile = addedTile,
        fromPlayer = fromPlayer
    )

    override fun equals(other: Any?): Boolean = other is MeldPreviewOption &&
        optionId == other.optionId && action == other.action && type == other.type &&
        tiles == other.tiles && handTiles == other.handTiles &&
        calledTile == other.calledTile && addedTile == other.addedTile &&
        fromPlayer == other.fromPlayer

    override fun hashCode(): Int {
        var r = optionId.hashCode()
        r = 31 * r + action.hashCode()
        r = 31 * r + type.hashCode()
        r = 31 * r + tiles.hashCode()
        r = 31 * r + handTiles.hashCode()
        r = 31 * r + (calledTile?.hashCode() ?: 0)
        r = 31 * r + (addedTile?.hashCode() ?: 0)
        r = 31 * r + (fromPlayer?.hashCode() ?: 0)
        return r
    }

    override fun toString(): String =
        "MeldPreviewOption(optionId=$optionId, action=$action, type=$type, tiles=$tiles, handTiles=$handTiles, calledTile=$calledTile, addedTile=$addedTile, fromPlayer=$fromPlayer)"
}

/**
 * 玩家私有展示预览聚合根 (PlayerPresentationPreview)
 *
 * 聚合同一次快照中计算的待牌、立直弃牌方案与鸣牌方案。
 * 严格绑定权威 roundId、decisionId 与 controlEpoch。
 *
 * @property roundId 绑定的单局标识
 * @property decisionId 绑定的决策标识
 * @property controlEpoch 绑定的控制代次
 * @property waits 当前理论听牌待牌列表（按 typeIndex 去重升序排序，纯展示牌）
 * @property riichiOptions 所有合法的立直打牌方案
 * @property meldOptions 所有合法的鸣牌牌组方案
 */
class PlayerPresentationPreview(
    val roundId: String,
    val decisionId: String,
    val controlEpoch: Long,
    waits: List<Tile> = emptyList(),
    riichiOptions: List<RiichiPreviewOption> = emptyList(),
    meldOptions: List<MeldPreviewOption> = emptyList()
) {
    val waits: List<Tile> = Collections.unmodifiableList(ArrayList(waits))
    val riichiOptions: List<RiichiPreviewOption> = Collections.unmodifiableList(ArrayList(riichiOptions))
    val meldOptions: List<MeldPreviewOption> = Collections.unmodifiableList(ArrayList(meldOptions))

    /** 吃牌选项子集 */
    val chiOptions: List<MeldPreviewOption>
        get() = Collections.unmodifiableList(meldOptions.filter { it.type == MeldType.CHI })

    /** 碰牌选项子集 */
    val ponOptions: List<MeldPreviewOption>
        get() = Collections.unmodifiableList(meldOptions.filter { it.type == MeldType.PON })

    /** 杠牌选项子集 (大明杠 / 暗杠 / 加杠) */
    val kanOptions: List<MeldPreviewOption>
        get() = Collections.unmodifiableList(
            meldOptions.filter {
                it.type == MeldType.OPEN_KAN || it.type == MeldType.CLOSED_KAN || it.type == MeldType.ADDED_KAN
            }
        )

    init {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        require(decisionId.isNotBlank()) { "decisionId cannot be blank" }
        require(controlEpoch >= 0L) { "controlEpoch must be >= 0: $controlEpoch" }
    }

    operator fun component1(): String = roundId
    operator fun component2(): String = decisionId
    operator fun component3(): Long = controlEpoch
    operator fun component4(): List<Tile> = waits
    operator fun component5(): List<RiichiPreviewOption> = riichiOptions
    operator fun component6(): List<MeldPreviewOption> = meldOptions

    fun copy(
        roundId: String = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        waits: List<Tile> = this.waits,
        riichiOptions: List<RiichiPreviewOption> = this.riichiOptions,
        meldOptions: List<MeldPreviewOption> = this.meldOptions
    ): PlayerPresentationPreview = PlayerPresentationPreview(
        roundId, decisionId, controlEpoch, waits, riichiOptions, meldOptions
    )

    fun deepCopy(): PlayerPresentationPreview = PlayerPresentationPreview(
        roundId = roundId,
        decisionId = decisionId,
        controlEpoch = controlEpoch,
        waits = ArrayList(waits),
        riichiOptions = riichiOptions.map { it.deepCopy() },
        meldOptions = meldOptions.map { it.deepCopy() }
    )

    override fun equals(other: Any?): Boolean = other is PlayerPresentationPreview &&
        roundId == other.roundId && decisionId == other.decisionId && controlEpoch == other.controlEpoch &&
        waits == other.waits && riichiOptions == other.riichiOptions && meldOptions == other.meldOptions

    override fun hashCode(): Int {
        var r = roundId.hashCode()
        r = 31 * r + decisionId.hashCode()
        r = 31 * r + controlEpoch.hashCode()
        r = 31 * r + waits.hashCode()
        r = 31 * r + riichiOptions.hashCode()
        r = 31 * r + meldOptions.hashCode()
        return r
    }

    override fun toString(): String =
        "PlayerPresentationPreview(roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, waits=$waits, riichiOptions=$riichiOptions, meldOptions=$meldOptions)"
}
