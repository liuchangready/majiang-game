package com.majiang.transport.api

import java.util.ArrayList
import java.util.Collections

/**
 * 传输层立直打牌展示候选 DTO (ProtocolRiichiPreviewOption)
 *
 * @property optionId 绑定的真实决策选项标识
 * @property action 精确的立直宣告动作协议 DTO
 * @property waits 该打牌方案后手牌能和的理论牌种（按 typeIndex 去重升序排序，纯展示牌）
 * @property furiten 该打牌方案产生的振听预判状态
 */
class ProtocolRiichiPreviewOption(
    val optionId: String,
    val action: ProtocolLegalAction.DeclareRiichi,
    waits: List<ProtocolTile>,
    val furiten: ProtocolFuritenStatus = ProtocolFuritenStatus()
) {
    val discardTile: ProtocolTile get() = action.discardTile
    val waits: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(waits))

    init {
        require(optionId.isNotBlank()) { "optionId cannot be blank" }
        require(discardTile == action.discardTile) { "discardTile must match action.discardTile" }
        require(waits.distinct().size == waits.size) { "waits cannot contain duplicate tiles: $waits" }
    }

    operator fun component1(): String = optionId
    operator fun component2(): ProtocolLegalAction.DeclareRiichi = action
    operator fun component3(): List<ProtocolTile> = waits
    operator fun component4(): ProtocolFuritenStatus = furiten

    fun copy(
        optionId: String = this.optionId,
        action: ProtocolLegalAction.DeclareRiichi = this.action,
        waits: List<ProtocolTile> = this.waits,
        furiten: ProtocolFuritenStatus = this.furiten
    ): ProtocolRiichiPreviewOption = ProtocolRiichiPreviewOption(optionId, action, waits, furiten)

    override fun equals(other: Any?): Boolean = other is ProtocolRiichiPreviewOption &&
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
        "ProtocolRiichiPreviewOption(optionId=$optionId, action=$action, waits=$waits, furiten=$furiten)"
}

/**
 * 传输层鸣牌展示候选 DTO (ProtocolMeldPreviewOption)
 *
 * 覆盖 chi / pon / open-kan / closed-kan / added-kan 五类鸣牌方案。
 *
 * @property optionId 绑定的真实决策选项标识
 * @property action 对应的协议合法动作 DTO
 * @property type 副露类型
 * @property tiles 完整组合包含的所有实际牌面（吃/碰3张，杠4张）
 * @property handTiles 参与本次鸣牌的精确本人手牌（加杠仅包含补入的第4张）
 * @property calledTile 外来被鸣牌（加杠来自原碰组；暗杠为 null）
 * @property addedTile 加杠补入的第4张牌（非加杠为 null）
 * @property fromPlayer 外来牌来源座位号（加杠来自原碰；暗杠为 null）
 */
class ProtocolMeldPreviewOption(
    val optionId: String,
    val action: ProtocolLegalAction,
    val type: ProtocolMeldType,
    tiles: List<ProtocolTile>,
    handTiles: List<ProtocolTile>,
    val calledTile: ProtocolTile? = null,
    val addedTile: ProtocolTile? = null,
    val fromPlayer: Int? = null
) {
    val tiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(tiles))
    val handTiles: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(handTiles))

    init {
        require(optionId.isNotBlank()) { "optionId cannot be blank" }

        when (action) {
            is ProtocolLegalAction.DeclareChi -> Unit
            is ProtocolLegalAction.DeclarePon -> Unit
            is ProtocolLegalAction.DeclareKan -> Unit
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
            ProtocolMeldType.CHI -> {
                require(action is ProtocolLegalAction.DeclareChi) {
                    "action must be DeclareChi for CHI, got ${action::class.simpleName}"
                }
                require(action.meld.type == ProtocolMeldType.CHI) {
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
            ProtocolMeldType.PON -> {
                require(action is ProtocolLegalAction.DeclarePon) {
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
                require(tiles.all { it.typeIndex == tiles[0].typeIndex }) {
                    "all tiles in PON must be of the same type: $tiles"
                }
                require(addedTile == null) {
                    "addedTile must be null for PON, got $addedTile"
                }
            }
            ProtocolMeldType.OPEN_KAN -> {
                require(action is ProtocolLegalAction.DeclareKan && action.kanType == ProtocolMeldType.OPEN_KAN) {
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
                require(tiles.all { it.typeIndex == tiles[0].typeIndex }) {
                    "all tiles in OPEN_KAN must be of the same type: $tiles"
                }
                require(addedTile == null) {
                    "addedTile must be null for OPEN_KAN, got $addedTile"
                }
            }
            ProtocolMeldType.CLOSED_KAN -> {
                require(action is ProtocolLegalAction.DeclareKan && action.kanType == ProtocolMeldType.CLOSED_KAN) {
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
                require(tiles.all { it.typeIndex == tiles[0].typeIndex }) {
                    "all tiles in CLOSED_KAN must be of the same type: $tiles"
                }
            }
            ProtocolMeldType.ADDED_KAN -> {
                require(action is ProtocolLegalAction.DeclareKan && action.kanType == ProtocolMeldType.ADDED_KAN) {
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
                require(tiles.all { it.typeIndex == tiles[0].typeIndex }) {
                    "all tiles in ADDED_KAN must be of the same type: $tiles"
                }
            }
        }
    }

    operator fun component1(): String = optionId
    operator fun component2(): ProtocolLegalAction = action
    operator fun component3(): ProtocolMeldType = type
    operator fun component4(): List<ProtocolTile> = tiles
    operator fun component5(): List<ProtocolTile> = handTiles
    operator fun component6(): ProtocolTile? = calledTile
    operator fun component7(): ProtocolTile? = addedTile
    operator fun component8(): Int? = fromPlayer

    fun copy(
        optionId: String = this.optionId,
        action: ProtocolLegalAction = this.action,
        type: ProtocolMeldType = this.type,
        tiles: List<ProtocolTile> = this.tiles,
        handTiles: List<ProtocolTile> = this.handTiles,
        calledTile: ProtocolTile? = this.calledTile,
        addedTile: ProtocolTile? = this.addedTile,
        fromPlayer: Int? = this.fromPlayer
    ): ProtocolMeldPreviewOption = ProtocolMeldPreviewOption(
        optionId, action, type, tiles, handTiles, calledTile, addedTile, fromPlayer
    )

    override fun equals(other: Any?): Boolean = other is ProtocolMeldPreviewOption &&
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
        "ProtocolMeldPreviewOption(optionId=$optionId, action=$action, type=$type, tiles=$tiles, handTiles=$handTiles, calledTile=$calledTile, addedTile=$addedTile, fromPlayer=$fromPlayer)"
}

/**
 * 传输层私有玩家展示预览 DTO (ProtocolPlayerPresentationPreview)
 *
 * 严格绑定权威 roundId、decisionId 与 controlEpoch。
 *
 * @property roundId 绑定的单局标识
 * @property decisionId 绑定的决策标识
 * @property controlEpoch 绑定的控制代次
 * @property waits 当前理论听牌待牌列表（按 typeIndex 去重升序排序，纯展示牌）
 * @property riichiOptions 所有合法的立直打牌方案
 * @property meldOptions 所有合法的鸣牌牌组方案
 */
class ProtocolPlayerPresentationPreview(
    val roundId: String,
    val decisionId: String,
    val controlEpoch: Long,
    waits: List<ProtocolTile> = emptyList(),
    riichiOptions: List<ProtocolRiichiPreviewOption> = emptyList(),
    meldOptions: List<ProtocolMeldPreviewOption> = emptyList()
) {
    val waits: List<ProtocolTile> = Collections.unmodifiableList(ArrayList(waits))
    val riichiOptions: List<ProtocolRiichiPreviewOption> = Collections.unmodifiableList(ArrayList(riichiOptions))
    val meldOptions: List<ProtocolMeldPreviewOption> = Collections.unmodifiableList(ArrayList(meldOptions))

    /** 吃牌选项子集 */
    val chiOptions: List<ProtocolMeldPreviewOption>
        get() = Collections.unmodifiableList(meldOptions.filter { it.type == ProtocolMeldType.CHI })

    /** 碰牌选项子集 */
    val ponOptions: List<ProtocolMeldPreviewOption>
        get() = Collections.unmodifiableList(meldOptions.filter { it.type == ProtocolMeldType.PON })

    /** 杠牌选项子集 (大明杠 / 暗杠 / 加杠) */
    val kanOptions: List<ProtocolMeldPreviewOption>
        get() = Collections.unmodifiableList(
            meldOptions.filter {
                it.type == ProtocolMeldType.OPEN_KAN ||
                it.type == ProtocolMeldType.CLOSED_KAN ||
                it.type == ProtocolMeldType.ADDED_KAN
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
    operator fun component4(): List<ProtocolTile> = waits
    operator fun component5(): List<ProtocolRiichiPreviewOption> = riichiOptions
    operator fun component6(): List<ProtocolMeldPreviewOption> = meldOptions

    fun copy(
        roundId: String = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        waits: List<ProtocolTile> = this.waits,
        riichiOptions: List<ProtocolRiichiPreviewOption> = this.riichiOptions,
        meldOptions: List<ProtocolMeldPreviewOption> = this.meldOptions
    ): ProtocolPlayerPresentationPreview = ProtocolPlayerPresentationPreview(
        roundId, decisionId, controlEpoch, waits, riichiOptions, meldOptions
    )

    override fun equals(other: Any?): Boolean = other is ProtocolPlayerPresentationPreview &&
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
        "ProtocolPlayerPresentationPreview(roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, waits=$waits, riichiOptions=$riichiOptions, meldOptions=$meldOptions)"
}
