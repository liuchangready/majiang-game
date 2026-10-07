package com.majiang.client.api

import java.util.Collections

/**
 * 花色视图模型 (SuitView)
 *
 * 表现层专用枚举，禁止直接持有 engine 领域枚举 [com.majiang.model.Suit]。
 */
enum class SuitView {
    MAN,    // 万子
    PIN,    // 筒子
    SOU,    // 索子
    WIND,   // 风牌
    DRAGON  // 三元牌
}

/**
 * 副露类型视图模型 (MeldTypeView)
 *
 * 表现层专用枚举，禁止直接持有 engine 领域枚举 [com.majiang.model.MeldType]。
 */
enum class MeldTypeView {
    CHI,          // 吃
    PON,          // 碰
    OPEN_KAN,     // 大明杠
    CLOSED_KAN,   // 暗杠
    ADDED_KAN     // 加杠 (小明杠)
}

/**
 * 对局阶段视图模型 (GamePhaseView)
 *
 * 表现层专用枚举，禁止直接持有 engine 领域枚举 [com.majiang.state.GamePhase]。
 */
enum class GamePhaseView {
    STACKING,
    DEALING,
    DRAW,
    CHEAT_WINDOW,
    DISCARD,
    RESPONSE_WINDOW,
    CHANKAN_WINDOW,
    KAN_DRAW,
    ROUND_END,
    MATCH_END
}

/**
 * 千术绝技视图模型 (CheatTechniqueView)
 *
 * 表现层专用枚举，禁止直接持有 engine 领域枚举 [com.majiang.event.CheatTechnique]。
 */
enum class CheatTechniqueView {
    SLEEVE_SWAP,     // 袖里乾坤
    WALL_STEAL,      // 移花接木
    DIVINE_SIGHT,    // 神仙透视
    DIVERSION        // 声东击西
}

/**
 * 鸣牌选择类别视图模型 (MeldChoiceKindView)
 */
enum class MeldChoiceKindView {
    CHI,
    PON,
    KAN
}

/**
 * 流局原因视图模型 (DrawReasonView)
 */
enum class DrawReasonView {
    EXHAUSTIVE,
    FOUR_WINDS,
    FOUR_KANS,
    FOUR_RIICHI,
    NINE_TERMINALS,
    TRIPLE_RON
}

/**
 * 麻将牌视图模型 (TileView)
 *
 * 表现层专用不可变值对象，0..135 全局物理编码与 engine 双向映射。
 *
 * @property tileId 0..135 全局唯一物理编号
 * @property suit 花色视图模型
 * @property rank 数牌1~9, 风牌1~4(东南西北), 三元牌1~3(白发中)
 */
data class TileView(
    val tileId: Int,
    val suit: SuitView,
    val rank: Int
) : Comparable<TileView> {

    init {
        require(tileId in 0..135) { "tileId must be in 0..135, got $tileId" }
        when (suit) {
            SuitView.MAN, SuitView.PIN, SuitView.SOU -> require(rank in 1..9) { "$suit rank must be 1..9, got $rank" }
            SuitView.WIND -> require(rank in 1..4) { "WIND rank must be 1..4, got $rank" }
            SuitView.DRAGON -> require(rank in 1..3) { "DRAGON rank must be 1..3, got $rank" }
        }
    }

    /** 同种牌的物理副本编号 0~3 */
    val copyIndex: Int get() = tileId % 4

    /** 34维特征索引 (0~33) */
    val typeIndex: Int get() = when (suit) {
        SuitView.MAN -> rank - 1
        SuitView.SOU -> rank - 1 + 9
        SuitView.PIN -> rank - 1 + 18
        SuitView.WIND -> rank - 1 + 27
        SuitView.DRAGON -> rank - 1 + 31
    }

    /** 老头牌（1或9的数牌） */
    val isTerminal: Boolean get() = (suit == SuitView.MAN || suit == SuitView.PIN || suit == SuitView.SOU) && (rank == 1 || rank == 9)

    /** 字牌（风牌或三元牌） */
    val isHonor: Boolean get() = suit == SuitView.WIND || suit == SuitView.DRAGON

    /** 幺九牌（老头牌或字牌） */
    val isTerminalOrHonor: Boolean get() = isTerminal || isHonor

    /** 中张牌（2~8数牌） */
    val isSimple: Boolean get() = !isTerminalOrHonor

    /** 数牌 */
    val isNumberTile: Boolean get() = suit == SuitView.MAN || suit == SuitView.PIN || suit == SuitView.SOU

    /** 赤宝牌（红五） */
    val isRedFive: Boolean
        get() = rank == 5 && copyIndex == 3 &&
            (suit == SuitView.MAN || suit == SuitView.PIN || suit == SuitView.SOU)

    /** 忽略 copyIndex 的同类型判定 */
    fun sameType(other: TileView): Boolean = suit == other.suit && rank == other.rank

    /** 中文牌名 */
    fun displayName(): String = when (suit) {
        SuitView.MAN -> "${rank}万"
        SuitView.PIN -> "${rank}筒"
        SuitView.SOU -> "${rank}索"
        SuitView.WIND -> listOf("東", "南", "西", "北")[rank - 1]
        SuitView.DRAGON -> listOf("白", "發", "中")[rank - 1]
    }

    override fun compareTo(other: TileView): Int =
        compareValuesBy(this, other, { it.suit }, { it.rank }, { it.copyIndex })

    companion object {
        /**
         * 根据 34 维 typeIndex (0..33) 与 copyIndex (0..3) 构造物理 TileView
         */
        fun fromTypeIndex(index: Int, copyIndex: Int = 0): TileView {
            require(index in 0..33) { "Invalid typeIndex: $index" }
            require(copyIndex in 0..3) { "Invalid copyIndex: $copyIndex" }
            val (suit, rank) = when {
                index in 0..8 -> Pair(SuitView.MAN, index + 1)
                index in 9..17 -> Pair(SuitView.SOU, index - 9 + 1)
                index in 18..26 -> Pair(SuitView.PIN, index - 18 + 1)
                index in 27..30 -> Pair(SuitView.WIND, index - 27 + 1)
                index in 31..33 -> Pair(SuitView.DRAGON, index - 31 + 1)
                else -> throw IllegalArgumentException("Invalid typeIndex: $index")
            }
            val tileId = index * 4 + copyIndex
            return TileView(tileId = tileId, suit = suit, rank = rank)
        }

        /**
         * 根据 0..135 唯一整数标识构造物理 TileView
         */
        fun fromTileId(tileId: Int): TileView {
            require(tileId in 0..135) { "tileId must be 0..135, got $tileId" }
            return fromTypeIndex(tileId / 4, tileId % 4)
        }
    }
}

/**
 * 副露视图模型 (MeldView)
 *
 * 表现层不可变副露数据结构。
 */
class MeldView(
    val type: MeldTypeView,
    tiles: List<TileView>,
    val fromPlayer: Int,
    val calledTile: TileView
) {
    val tiles: List<TileView> = Collections.unmodifiableList(ArrayList(tiles))

    init {
        require(this.tiles.isNotEmpty()) { "tiles cannot be empty" }
    }

    val isOpen: Boolean get() = type != MeldTypeView.CLOSED_KAN
    val isKan: Boolean get() = type == MeldTypeView.OPEN_KAN || type == MeldTypeView.CLOSED_KAN || type == MeldTypeView.ADDED_KAN
    val isSequence: Boolean get() = type == MeldTypeView.CHI
    val isTriplet: Boolean get() = type == MeldTypeView.PON
    val isQuad: Boolean get() = isKan

    operator fun component1(): MeldTypeView = type
    operator fun component2(): List<TileView> = tiles
    operator fun component3(): Int = fromPlayer
    operator fun component4(): TileView = calledTile

    fun copy(
        type: MeldTypeView = this.type,
        tiles: List<TileView> = this.tiles,
        fromPlayer: Int = this.fromPlayer,
        calledTile: TileView = this.calledTile
    ): MeldView = MeldView(type, tiles, fromPlayer, calledTile)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is MeldView) return false
        return type == other.type && tiles == other.tiles &&
            fromPlayer == other.fromPlayer && calledTile == other.calledTile
    }

    override fun hashCode(): Int {
        var result = type.hashCode()
        result = 31 * result + tiles.hashCode()
        result = 31 * result + fromPlayer.hashCode()
        result = 31 * result + calledTile.hashCode()
        return result
    }

    override fun toString(): String =
        "MeldView(type=$type, tiles=$tiles, fromPlayer=$fromPlayer, calledTile=$calledTile)"
}

/**
 * 客户端千术参数强类型值 (ClientParamValue)
 *
 * 表现层/客户端专用的千术动作参数强类型封装，不暴露 protocol、engine 或 Any。
 */
sealed class ClientParamValue {
    data class IntValue(val value: Int) : ClientParamValue() {
        override fun toString(): String = value.toString()
    }

    data class LongValue(val value: Long) : ClientParamValue() {
        override fun toString(): String = value.toString()
    }

    data class DoubleValue(val value: Double) : ClientParamValue() {
        override fun toString(): String = value.toString()
    }

    data class StringValue(val value: String) : ClientParamValue() {
        override fun toString(): String = value
    }

    data class BooleanValue(val value: Boolean) : ClientParamValue() {
        override fun toString(): String = value.toString()
    }

    fun asInt(): Int? = (this as? IntValue)?.value
    fun asLong(): Long? = (this as? LongValue)?.value ?: (this as? IntValue)?.value?.toLong()
    fun asDouble(): Double? = (this as? DoubleValue)?.value ?: (this as? IntValue)?.value?.toDouble() ?: (this as? LongValue)?.value?.toDouble()
    fun asString(): String? = (this as? StringValue)?.value
    fun asBoolean(): Boolean? = (this as? BooleanValue)?.value

    companion object {
        fun of(value: Int): ClientParamValue = IntValue(value)
        fun of(value: Long): ClientParamValue = LongValue(value)
        fun of(value: Double): ClientParamValue = DoubleValue(value)
        fun of(value: String): ClientParamValue = StringValue(value)
        fun of(value: Boolean): ClientParamValue = BooleanValue(value)
    }
}

/**
 * 合法动作视图模型 (LegalActionView)
 *
 * 密封接口，完全对应 engine [com.majiang.state.GameAction] 的 14 种变体。
 */
sealed interface LegalActionView {
    class StartStacking(stackingPlans: List<String> = emptyList()) : LegalActionView {
        val stackingPlans: List<String> = Collections.unmodifiableList(ArrayList(stackingPlans))
        operator fun component1(): List<String> = stackingPlans
        fun copy(stackingPlans: List<String> = this.stackingPlans): StartStacking = StartStacking(stackingPlans)
        override fun equals(other: Any?): Boolean = other is StartStacking && stackingPlans == other.stackingPlans
        override fun hashCode(): Int = stackingPlans.hashCode()
        override fun toString(): String = "StartStacking(stackingPlans=$stackingPlans)"
    }
    data object DealComplete : LegalActionView
    data class DrawTile(val player: Int) : LegalActionView
    data class DiscardTile(val player: Int, val tile: TileView) : LegalActionView
    data class DeclareChi(val player: Int, val meld: MeldView) : LegalActionView
    class DeclarePon(val player: Int, tiles: List<TileView>) : LegalActionView {
        val tiles: List<TileView> = Collections.unmodifiableList(ArrayList(tiles))
        operator fun component1(): Int = player
        operator fun component2(): List<TileView> = tiles
        fun copy(player: Int = this.player, tiles: List<TileView> = this.tiles): DeclarePon = DeclarePon(player, tiles)
        override fun equals(other: Any?): Boolean = other is DeclarePon && player == other.player && tiles == other.tiles
        override fun hashCode(): Int = 31 * player.hashCode() + tiles.hashCode()
        override fun toString(): String = "DeclarePon(player=$player, tiles=$tiles)"
    }
    class DeclareKan(val player: Int, tiles: List<TileView>, val kanType: MeldTypeView) : LegalActionView {
        val tiles: List<TileView> = Collections.unmodifiableList(ArrayList(tiles))
        operator fun component1(): Int = player
        operator fun component2(): List<TileView> = tiles
        operator fun component3(): MeldTypeView = kanType
        fun copy(player: Int = this.player, tiles: List<TileView> = this.tiles, kanType: MeldTypeView = this.kanType): DeclareKan = DeclareKan(player, tiles, kanType)
        override fun equals(other: Any?): Boolean = other is DeclareKan && player == other.player && tiles == other.tiles && kanType == other.kanType
        override fun hashCode(): Int = 31 * (31 * player.hashCode() + tiles.hashCode()) + kanType.hashCode()
        override fun toString(): String = "DeclareKan(player=$player, tiles=$tiles, kanType=$kanType)"
    }
    data class DeclareRiichi(val player: Int, val discardTile: TileView) : LegalActionView
    data class DeclareTsumo(val player: Int) : LegalActionView
    data class DeclareRon(val player: Int) : LegalActionView
    class UseCheatTechnique(
        val player: Int,
        val technique: CheatTechniqueView,
        params: Map<String, ClientParamValue> = emptyMap()
    ) : LegalActionView {
        val params: Map<String, ClientParamValue> = Collections.unmodifiableMap(LinkedHashMap(params))
        operator fun component1(): Int = player
        operator fun component2(): CheatTechniqueView = technique
        operator fun component3(): Map<String, ClientParamValue> = params
        fun copy(player: Int = this.player, technique: CheatTechniqueView = this.technique, params: Map<String, ClientParamValue> = this.params): UseCheatTechnique = UseCheatTechnique(player, technique, params)
        override fun equals(other: Any?): Boolean = other is UseCheatTechnique && player == other.player && technique == other.technique && params == other.params
        override fun hashCode(): Int = 31 * (31 * player.hashCode() + technique.hashCode()) + params.hashCode()
        override fun toString(): String = "UseCheatTechnique(player=$player, technique=$technique, params=$params)"
    }
    data class Pass(val player: Int) : LegalActionView
    data class DeclareAbortiveDraw(val player: Int) : LegalActionView
    data class DeclareAgariYame(val player: Int) : LegalActionView
}

/**
 * 权威时钟信息视图模型 (ClockInfo)
 */
data class ClockInfo(
    val seat: Int,
    val opensAt: Long,
    val deadline: Long,
    val reserve: Long,
    val serverNow: Long
) {
    init {
        require(seat in 0..3) { "seat must be 0..3, got $seat" }
        require(deadline >= opensAt) { "deadline must be >= opensAt: deadline=$deadline, opensAt=$opensAt" }
        require(reserve >= 0L) { "reserve must be >= 0: $reserve" }
    }
}

/**
 * 席位控制者状态视图模型 (ControlsInfo)
 */
data class ControlsInfo(
    val seat: Int,
    val connected: Boolean,
    val role: String,
    val controlEpoch: Long
) {
    init {
        require(seat in 0..3) { "seat must be 0..3, got $seat" }
        require(controlEpoch >= 0L) { "controlEpoch must be >= 0: $controlEpoch" }
    }
}

/**
 * 公开单局历史结算记录视图模型 (PublicRoundRecordView)
 */
class PublicRoundRecordView(
    val roundId: String,
    val resultId: String,
    finalScores: List<Int>,
    deltas: List<Int>,
    finalCheatPoints: List<Int>,
    cheatDeltas: List<Int>
) {
    val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
    val deltas: List<Int> = Collections.unmodifiableList(ArrayList(deltas))
    val finalCheatPoints: List<Int> = Collections.unmodifiableList(ArrayList(finalCheatPoints))
    val cheatDeltas: List<Int> = Collections.unmodifiableList(ArrayList(cheatDeltas))

    operator fun component1(): String = roundId
    operator fun component2(): String = resultId
    operator fun component3(): List<Int> = finalScores
    operator fun component4(): List<Int> = deltas
    operator fun component5(): List<Int> = finalCheatPoints
    operator fun component6(): List<Int> = cheatDeltas

    fun copy(
        roundId: String = this.roundId,
        resultId: String = this.resultId,
        finalScores: List<Int> = this.finalScores,
        deltas: List<Int> = this.deltas,
        finalCheatPoints: List<Int> = this.finalCheatPoints,
        cheatDeltas: List<Int> = this.cheatDeltas
    ): PublicRoundRecordView = PublicRoundRecordView(roundId, resultId, finalScores, deltas, finalCheatPoints, cheatDeltas)

    override fun equals(other: Any?): Boolean = other is PublicRoundRecordView &&
        roundId == other.roundId && resultId == other.resultId &&
        finalScores == other.finalScores && deltas == other.deltas &&
        finalCheatPoints == other.finalCheatPoints && cheatDeltas == other.cheatDeltas

    override fun hashCode(): Int {
        var r = roundId.hashCode()
        r = 31 * r + resultId.hashCode()
        r = 31 * r + finalScores.hashCode()
        r = 31 * r + deltas.hashCode()
        r = 31 * r + finalCheatPoints.hashCode()
        r = 31 * r + cheatDeltas.hashCode()
        return r
    }

    override fun toString(): String =
        "PublicRoundRecordView(roundId=$roundId, resultId=$resultId, finalScores=$finalScores, deltas=$deltas, finalCheatPoints=$finalCheatPoints, cheatDeltas=$cheatDeltas)"
}

/**
 * 公开整场终局结果视图模型 (PublicSessionResultView)
 */
class PublicSessionResultView(
    finalScores: List<Int>,
    rankedSeats: List<Int>,
    val reason: String,
    finalCheatPoints: List<Int> = emptyList()
) {
    val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
    val rankedSeats: List<Int> = Collections.unmodifiableList(ArrayList(rankedSeats))
    val finalCheatPoints: List<Int> = Collections.unmodifiableList(ArrayList(finalCheatPoints))

    operator fun component1(): List<Int> = finalScores
    operator fun component2(): List<Int> = rankedSeats
    operator fun component3(): String = reason
    operator fun component4(): List<Int> = finalCheatPoints

    fun copy(
        finalScores: List<Int> = this.finalScores,
        rankedSeats: List<Int> = this.rankedSeats,
        reason: String = this.reason,
        finalCheatPoints: List<Int> = this.finalCheatPoints
    ): PublicSessionResultView = PublicSessionResultView(finalScores, rankedSeats, reason, finalCheatPoints)

    override fun equals(other: Any?): Boolean = other is PublicSessionResultView &&
        finalScores == other.finalScores && rankedSeats == other.rankedSeats &&
        reason == other.reason && finalCheatPoints == other.finalCheatPoints

    override fun hashCode(): Int {
        var r = finalScores.hashCode()
        r = 31 * r + rankedSeats.hashCode()
        r = 31 * r + reason.hashCode()
        r = 31 * r + finalCheatPoints.hashCode()
        return r
    }

    override fun toString(): String =
        "PublicSessionResultView(finalScores=$finalScores, rankedSeats=$rankedSeats, reason=$reason, finalCheatPoints=$finalCheatPoints)"
}

/**
 * 整场赛程进度视图模型 (SessionProgressView)
 */
class SessionProgressView(
    val lifecycle: String,
    scores: List<Int>,
    val roundWind: Int,
    val roundNumber: Int,
    val dealerSeat: Int,
    val honba: Int,
    roundRecords: List<PublicRoundRecordView> = emptyList(),
    val finalResult: PublicSessionResultView? = null
) {
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val roundRecords: List<PublicRoundRecordView> = Collections.unmodifiableList(ArrayList(roundRecords))

    init {
        require(lifecycle.isNotBlank()) { "lifecycle cannot be blank" }
        require(dealerSeat in 0..3) { "dealerSeat must be in 0..3, got $dealerSeat" }
        require(honba >= 0) { "honba must be >= 0, got $honba" }
    }

    operator fun component1(): String = lifecycle
    operator fun component2(): List<Int> = scores
    operator fun component3(): Int = roundWind
    operator fun component4(): Int = roundNumber
    operator fun component5(): Int = dealerSeat
    operator fun component6(): Int = honba
    operator fun component7(): List<PublicRoundRecordView> = roundRecords
    operator fun component8(): PublicSessionResultView? = finalResult

    fun copy(
        lifecycle: String = this.lifecycle,
        scores: List<Int> = this.scores,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        dealerSeat: Int = this.dealerSeat,
        honba: Int = this.honba,
        roundRecords: List<PublicRoundRecordView> = this.roundRecords,
        finalResult: PublicSessionResultView? = this.finalResult
    ): SessionProgressView = SessionProgressView(
        lifecycle, scores, roundWind, roundNumber, dealerSeat, honba, roundRecords, finalResult
    )

    override fun equals(other: Any?): Boolean = other is SessionProgressView &&
        lifecycle == other.lifecycle && scores == other.scores &&
        roundWind == other.roundWind && roundNumber == other.roundNumber &&
        dealerSeat == other.dealerSeat && honba == other.honba &&
        roundRecords == other.roundRecords && finalResult == other.finalResult

    override fun hashCode(): Int {
        var r = lifecycle.hashCode()
        r = 31 * r + scores.hashCode()
        r = 31 * r + roundWind.hashCode()
        r = 31 * r + roundNumber.hashCode()
        r = 31 * r + dealerSeat.hashCode()
        r = 31 * r + honba.hashCode()
        r = 31 * r + roundRecords.hashCode()
        r = 31 * r + (finalResult?.hashCode() ?: 0)
        return r
    }

    override fun toString(): String =
        "SessionProgressView(lifecycle=$lifecycle, scores=$scores, roundWind=$roundWind, roundNumber=$roundNumber, dealerSeat=$dealerSeat, honba=$honba, roundRecords=$roundRecords, finalResult=$finalResult)"
}

/**
 * 振听状态视图模型 (FuritenStatusView)
 */
data class FuritenStatusView(
    val discard: Boolean = false,
    val temporary: Boolean = false,
    val riichi: Boolean = false
) {
    val isFuriten: Boolean get() = discard || temporary || riichi
}

/**
 * 符数明细单行视图 (FuLineView)
 */
data class FuLineView(
    val description: String,
    val fu: Int
)

/**
 * 符数推导分解视图 (FuBreakdownView)
 */
class FuBreakdownView(
    lines: List<FuLineView>,
    val subtotal: Int,
    val roundedUpTo: Int,
    val finalFu: Int,
    val specialNote: String? = null
) {
    val lines: List<FuLineView> = Collections.unmodifiableList(ArrayList(lines))

    operator fun component1(): List<FuLineView> = lines
    operator fun component2(): Int = subtotal
    operator fun component3(): Int = roundedUpTo
    operator fun component4(): Int = finalFu
    operator fun component5(): String? = specialNote

    fun copy(
        lines: List<FuLineView> = this.lines,
        subtotal: Int = this.subtotal,
        roundedUpTo: Int = this.roundedUpTo,
        finalFu: Int = this.finalFu,
        specialNote: String? = this.specialNote
    ): FuBreakdownView = FuBreakdownView(lines, subtotal, roundedUpTo, finalFu, specialNote)

    override fun equals(other: Any?): Boolean = other is FuBreakdownView &&
        lines == other.lines && subtotal == other.subtotal &&
        roundedUpTo == other.roundedUpTo && finalFu == other.finalFu &&
        specialNote == other.specialNote

    override fun hashCode(): Int {
        var r = lines.hashCode()
        r = 31 * r + subtotal.hashCode()
        r = 31 * r + roundedUpTo.hashCode()
        r = 31 * r + finalFu.hashCode()
        r = 31 * r + (specialNote?.hashCode() ?: 0)
        return r
    }

    override fun toString(): String =
        "FuBreakdownView(lines=$lines, subtotal=$subtotal, roundedUpTo=$roundedUpTo, finalFu=$finalFu, specialNote=$specialNote)"
}

/**
 * 宝牌指示牌命中明细视图 (DoraIndicatorHitView)
 */
class DoraIndicatorHitView(
    val indicator: TileView,
    val target: TileView,
    tiles: List<TileView>
) {
    val tiles: List<TileView> = Collections.unmodifiableList(ArrayList(tiles))

    operator fun component1(): TileView = indicator
    operator fun component2(): TileView = target
    operator fun component3(): List<TileView> = tiles

    fun copy(
        indicator: TileView = this.indicator,
        target: TileView = this.target,
        tiles: List<TileView> = this.tiles
    ): DoraIndicatorHitView = DoraIndicatorHitView(indicator, target, tiles)

    override fun equals(other: Any?): Boolean = other is DoraIndicatorHitView &&
        indicator == other.indicator && target == other.target && tiles == other.tiles

    override fun hashCode(): Int {
        var r = indicator.hashCode()
        r = 31 * r + target.hashCode()
        r = 31 * r + tiles.hashCode()
        return r
    }

    override fun toString(): String =
        "DoraIndicatorHitView(indicator=$indicator, target=$target, tiles=$tiles)"
}

/**
 * 宝牌推导明细视图 (DoraBreakdownView)
 */
class DoraBreakdownView(
    val indicatorCount: Int,
    val uraCount: Int,
    val redCount: Int,
    val totalHan: Int,
    indicatorTiles: List<TileView>,
    uraTiles: List<TileView>,
    redTiles: List<TileView>,
    indicatorHits: List<DoraIndicatorHitView>,
    uraHits: List<DoraIndicatorHitView>
) {
    val indicatorTiles: List<TileView> = Collections.unmodifiableList(ArrayList(indicatorTiles))
    val uraTiles: List<TileView> = Collections.unmodifiableList(ArrayList(uraTiles))
    val redTiles: List<TileView> = Collections.unmodifiableList(ArrayList(redTiles))
    val indicatorHits: List<DoraIndicatorHitView> = Collections.unmodifiableList(ArrayList(indicatorHits))
    val uraHits: List<DoraIndicatorHitView> = Collections.unmodifiableList(ArrayList(uraHits))

    operator fun component1(): Int = indicatorCount
    operator fun component2(): Int = uraCount
    operator fun component3(): Int = redCount
    operator fun component4(): Int = totalHan
    operator fun component5(): List<TileView> = indicatorTiles
    operator fun component6(): List<TileView> = uraTiles
    operator fun component7(): List<TileView> = redTiles
    operator fun component8(): List<DoraIndicatorHitView> = indicatorHits
    operator fun component9(): List<DoraIndicatorHitView> = uraHits

    fun copy(
        indicatorCount: Int = this.indicatorCount,
        uraCount: Int = this.uraCount,
        redCount: Int = this.redCount,
        totalHan: Int = this.totalHan,
        indicatorTiles: List<TileView> = this.indicatorTiles,
        uraTiles: List<TileView> = this.uraTiles,
        redTiles: List<TileView> = this.redTiles,
        indicatorHits: List<DoraIndicatorHitView> = this.indicatorHits,
        uraHits: List<DoraIndicatorHitView> = this.uraHits
    ): DoraBreakdownView = DoraBreakdownView(
        indicatorCount, uraCount, redCount, totalHan,
        indicatorTiles, uraTiles, redTiles, indicatorHits, uraHits
    )

    override fun equals(other: Any?): Boolean = other is DoraBreakdownView &&
        indicatorCount == other.indicatorCount && uraCount == other.uraCount &&
        redCount == other.redCount && totalHan == other.totalHan &&
        indicatorTiles == other.indicatorTiles && uraTiles == other.uraTiles &&
        redTiles == other.redTiles && indicatorHits == other.indicatorHits &&
        uraHits == other.uraHits

    override fun hashCode(): Int {
        var r = indicatorCount.hashCode()
        r = 31 * r + uraCount.hashCode()
        r = 31 * r + redCount.hashCode()
        r = 31 * r + totalHan.hashCode()
        r = 31 * r + indicatorTiles.hashCode()
        r = 31 * r + uraTiles.hashCode()
        r = 31 * r + redTiles.hashCode()
        r = 31 * r + indicatorHits.hashCode()
        r = 31 * r + uraHits.hashCode()
        return r
    }

    override fun toString(): String =
        "DoraBreakdownView(indicatorCount=$indicatorCount, uraCount=$uraCount, redCount=$redCount, totalHan=$totalHan, indicatorTiles=$indicatorTiles, uraTiles=$uraTiles, redTiles=$redTiles, indicatorHits=$indicatorHits, uraHits=$uraHits)"
}

/**
 * 算分结果明细视图 (ScoreResultView)
 */
data class ScoreResultView(
    val fu: Int,
    val han: Int,
    val basePoints: Int,
    val title: String,
    val isYakuman: Boolean,
    val yakumanMultiplier: Int,
    val rawBasePoints: Int,
    val capReason: String? = null,
    val formulaText: String = "",
    val rawBasePointsExact: String = rawBasePoints.toString()
)

/**
 * 役种/番/符/宝牌完整计分明细视图 (ScoreBreakdownView)
 */
class ScoreBreakdownView(
    yakuLines: List<Pair<String, Int>>,
    val yakumanCount: Int,
    val dora: DoraBreakdownView,
    val fu: FuBreakdownView,
    val score: ScoreResultView,
    paymentLines: List<String>,
    val honbaAddition: Int,
    val riichiAddition: Int,
    val totalPoints: Int,
    val uraEligible: Boolean,
    val isDealer: Boolean,
    val isTsumo: Boolean
) {
    val yakuLines: List<Pair<String, Int>> = Collections.unmodifiableList(ArrayList(yakuLines))
    val paymentLines: List<String> = Collections.unmodifiableList(ArrayList(paymentLines))

    operator fun component1(): List<Pair<String, Int>> = yakuLines
    operator fun component2(): Int = yakumanCount
    operator fun component3(): DoraBreakdownView = dora
    operator fun component4(): FuBreakdownView = fu
    operator fun component5(): ScoreResultView = score
    operator fun component6(): List<String> = paymentLines
    operator fun component7(): Int = honbaAddition
    operator fun component8(): Int = riichiAddition
    operator fun component9(): Int = totalPoints
    operator fun component10(): Boolean = uraEligible
    operator fun component11(): Boolean = isDealer
    operator fun component12(): Boolean = isTsumo

    fun copy(
        yakuLines: List<Pair<String, Int>> = this.yakuLines,
        yakumanCount: Int = this.yakumanCount,
        dora: DoraBreakdownView = this.dora,
        fu: FuBreakdownView = this.fu,
        score: ScoreResultView = this.score,
        paymentLines: List<String> = this.paymentLines,
        honbaAddition: Int = this.honbaAddition,
        riichiAddition: Int = this.riichiAddition,
        totalPoints: Int = this.totalPoints,
        uraEligible: Boolean = this.uraEligible,
        isDealer: Boolean = this.isDealer,
        isTsumo: Boolean = this.isTsumo
    ): ScoreBreakdownView = ScoreBreakdownView(
        yakuLines, yakumanCount, dora, fu, score, paymentLines,
        honbaAddition, riichiAddition, totalPoints, uraEligible, isDealer, isTsumo
    )

    override fun equals(other: Any?): Boolean = other is ScoreBreakdownView &&
        yakuLines == other.yakuLines && yakumanCount == other.yakumanCount &&
        dora == other.dora && fu == other.fu && score == other.score &&
        paymentLines == other.paymentLines && honbaAddition == other.honbaAddition &&
        riichiAddition == other.riichiAddition && totalPoints == other.totalPoints &&
        uraEligible == other.uraEligible && isDealer == other.isDealer && isTsumo == other.isTsumo

    override fun hashCode(): Int {
        var r = yakuLines.hashCode()
        r = 31 * r + yakumanCount.hashCode()
        r = 31 * r + dora.hashCode()
        r = 31 * r + fu.hashCode()
        r = 31 * r + score.hashCode()
        r = 31 * r + paymentLines.hashCode()
        r = 31 * r + honbaAddition.hashCode()
        r = 31 * r + riichiAddition.hashCode()
        r = 31 * r + totalPoints.hashCode()
        r = 31 * r + uraEligible.hashCode()
        r = 31 * r + isDealer.hashCode()
        r = 31 * r + isTsumo.hashCode()
        return r
    }

    override fun toString(): String =
        "ScoreBreakdownView(yakuLines=$yakuLines, yakumanCount=$yakumanCount, dora=$dora, fu=$fu, score=$score, paymentLines=$paymentLines, totalPoints=$totalPoints)"
}

/**
 * 结算详情展现视图模型 (SettlementDetailView)
 */
class SettlementDetailView(
    val title: String,
    val winner: Int?,
    val loser: Int?,
    val winTile: TileView?,
    yakuList: List<String>,
    val han: Int,
    val fu: Int,
    val score: Int,
    val isDraw: Boolean = false,
    val drawReason: String = "",
    payments: Map<Int, Int> = emptyMap(),
    newScores: List<Int> = emptyList(),
    val breakdown: ScoreBreakdownView? = null
) {
    val yakuList: List<String> = Collections.unmodifiableList(ArrayList(yakuList))
    val payments: Map<Int, Int> = Collections.unmodifiableMap(LinkedHashMap(payments))
    val newScores: List<Int> = Collections.unmodifiableList(ArrayList(newScores))

    operator fun component1(): String = title
    operator fun component2(): Int? = winner
    operator fun component3(): Int? = loser
    operator fun component4(): TileView? = winTile
    operator fun component5(): List<String> = yakuList
    operator fun component6(): Int = han
    operator fun component7(): Int = fu
    operator fun component8(): Int = score
    operator fun component9(): Boolean = isDraw
    operator fun component10(): String = drawReason
    operator fun component11(): Map<Int, Int> = payments
    operator fun component12(): List<Int> = newScores
    operator fun component13(): ScoreBreakdownView? = breakdown

    fun copy(
        title: String = this.title,
        winner: Int? = this.winner,
        loser: Int? = this.loser,
        winTile: TileView? = this.winTile,
        yakuList: List<String> = this.yakuList,
        han: Int = this.han,
        fu: Int = this.fu,
        score: Int = this.score,
        isDraw: Boolean = this.isDraw,
        drawReason: String = this.drawReason,
        payments: Map<Int, Int> = this.payments,
        newScores: List<Int> = this.newScores,
        breakdown: ScoreBreakdownView? = this.breakdown
    ): SettlementDetailView = SettlementDetailView(
        title, winner, loser, winTile, yakuList, han, fu, score,
        isDraw, drawReason, payments, newScores, breakdown
    )

    override fun equals(other: Any?): Boolean = other is SettlementDetailView &&
        title == other.title && winner == other.winner && loser == other.loser &&
        winTile == other.winTile && yakuList == other.yakuList &&
        han == other.han && fu == other.fu && score == other.score &&
        isDraw == other.isDraw && drawReason == other.drawReason &&
        payments == other.payments && newScores == other.newScores &&
        breakdown == other.breakdown

    override fun hashCode(): Int {
        var r = title.hashCode()
        r = 31 * r + (winner?.hashCode() ?: 0)
        r = 31 * r + (loser?.hashCode() ?: 0)
        r = 31 * r + (winTile?.hashCode() ?: 0)
        r = 31 * r + yakuList.hashCode()
        r = 31 * r + han.hashCode()
        r = 31 * r + fu.hashCode()
        r = 31 * r + score.hashCode()
        r = 31 * r + isDraw.hashCode()
        r = 31 * r + drawReason.hashCode()
        r = 31 * r + payments.hashCode()
        r = 31 * r + newScores.hashCode()
        r = 31 * r + (breakdown?.hashCode() ?: 0)
        return r
    }

    override fun toString(): String =
        "SettlementDetailView(title=$title, winner=$winner, loser=$loser, winTile=$winTile, yakuList=$yakuList, score=$score, payments=$payments)"
}

/**
 * 房间摘要信息视图模型 (RoomSummaryView)
 */
data class RoomSummaryView(
    val roomId: String,
    val roomName: String,
    val hostPlayerName: String,
    val playerCount: Int,
    val maxPlayers: Int = 4,
    val matchStarted: Boolean,
    val configSummary: String = ""
) {
    init {
        require(roomId.isNotBlank()) { "roomId cannot be blank" }
        require(roomName.isNotBlank()) { "roomName cannot be blank" }
        require(hostPlayerName.isNotBlank()) { "hostPlayerName cannot be blank" }
        require(maxPlayers >= 1) { "maxPlayers must be >= 1: $maxPlayers" }
        require(playerCount in 1..maxPlayers) { "playerCount must be in 1..$maxPlayers: $playerCount" }
    }
}

/** 洗牌千术目标视图模型 */
enum class ShuffleTargetView {
    CHINITSU_MAN,
    CHINITSU_SOU,
    CHINITSU_PIN,
    DAISANGEN,
    KOKUSHI,
    DAISUUSHII
}

/** 洗牌千术状态视图模型 */
enum class ShufflePreparationStatusView {
    SELECTING,
    GENERATING
}

/** 洗牌千术预选方案视图模型 */
class ShuffleSelectionView(
    initialTargets: Set<ShuffleTargetView> = emptySet(),
    drawTargets: Set<ShuffleTargetView> = emptySet()
) {
    val initialTargets: Set<ShuffleTargetView> = Collections.unmodifiableSet(LinkedHashSet(initialTargets))
    val drawTargets: Set<ShuffleTargetView> = Collections.unmodifiableSet(LinkedHashSet(drawTargets))

    fun cost(): Int =
        (if (initialTargets.isNotEmpty()) 40 else 0) + (if (drawTargets.isNotEmpty()) 20 else 0)
    val isActive: Boolean get() = initialTargets.isNotEmpty() || drawTargets.isNotEmpty()

    fun copy(
        initialTargets: Set<ShuffleTargetView> = this.initialTargets,
        drawTargets: Set<ShuffleTargetView> = this.drawTargets
    ): ShuffleSelectionView = ShuffleSelectionView(initialTargets, drawTargets)

    override fun equals(other: Any?): Boolean = other is ShuffleSelectionView &&
        initialTargets == other.initialTargets && drawTargets == other.drawTargets

    override fun hashCode(): Int = 31 * initialTargets.hashCode() + drawTargets.hashCode()

    override fun toString(): String =
        "ShuffleSelectionView(initialTargets=$initialTargets, drawTargets=$drawTargets)"
}

/** 洗牌千术准备阶段视图模型 */
data class ShufflePreparationView(
    val roundId: String,
    val status: ShufflePreparationStatusView,
    val opensAtMillis: Long,
    val deadlineMillis: Long,
    val serverNowMillis: Long,
    val confirmed: Boolean,
    val selection: ShuffleSelectionView?,
    val reservedPoints: Int,
    val availablePoints: Int
)

/** 决策动作选项视图模型 */
data class ActionOptionView(
    val optionId: String,
    val action: LegalActionView
)

/**
 * 决策视图模型 (DecisionView)
 *
 * 封装当前座位可执行的合法动作候选。
 */
class DecisionView(
    val roundId: String?,
    val decisionId: String,
    val controlEpoch: Long,
    actions: List<LegalActionView> = emptyList(),
    options: List<ActionOptionView> = emptyList()
) {
    val actions: List<LegalActionView> = Collections.unmodifiableList(ArrayList(actions))
    val options: List<ActionOptionView> = Collections.unmodifiableList(ArrayList(options))

    fun copy(
        roundId: String? = this.roundId,
        decisionId: String = this.decisionId,
        controlEpoch: Long = this.controlEpoch,
        actions: List<LegalActionView> = this.actions,
        options: List<ActionOptionView> = this.options
    ): DecisionView = DecisionView(roundId, decisionId, controlEpoch, actions, options)

    operator fun component1(): String? = roundId
    operator fun component2(): String = decisionId
    operator fun component3(): Long = controlEpoch
    operator fun component4(): List<LegalActionView> = actions
    operator fun component5(): List<ActionOptionView> = options

    override fun equals(other: Any?): Boolean = other is DecisionView &&
        roundId == other.roundId && decisionId == other.decisionId &&
        controlEpoch == other.controlEpoch && actions == other.actions && options == other.options

    override fun hashCode(): Int {
        var r = roundId?.hashCode() ?: 0
        r = 31 * r + decisionId.hashCode()
        r = 31 * r + controlEpoch.hashCode()
        r = 31 * r + actions.hashCode()
        r = 31 * r + options.hashCode()
        return r
    }

    override fun toString(): String =
        "DecisionView(roundId=$roundId, decisionId=$decisionId, controlEpoch=$controlEpoch, actions=$actions, options=$options)"
}

/**
 * 观察者玩家座位的完整只读投影视图模型 (PlayerView)
 *
 * 表现层专用视图模型，完全不持有 engine 任何领域类。
 */
class PlayerView(
    val viewerSeat: Int,
    val phase: GamePhaseView,
    val roundWind: Int,
    val roundNumber: Int,
    val honbaSticks: Int,
    val riichiSticks: Int,
    val dealerSeat: Int,
    val currentTurnPlayer: Int,
    val wallRemaining: Int,
    doraIndicators: List<TileView>,
    scores: List<Int>,
    riichiStatus: List<Boolean>,
    discards: List<List<TileView>>,
    melds: List<List<MeldView>>,
    handTileCounts: List<Int>,
    myClosedTiles: List<TileView>,
    val myDrawnTile: TileView?,
    legalActions: List<LegalActionView>,
    val lastDiscard: TileView? = null,
    val lastDiscardPlayer: Int? = null,
    mySleeveTiles: List<TileView> = emptyList(),
    val myFuriten: FuritenStatusView = FuritenStatusView(),
    myDiscardHistory: List<TileView> = emptyList()
) {
    val doraIndicators: List<TileView> = Collections.unmodifiableList(ArrayList(doraIndicators))
    val scores: List<Int> = Collections.unmodifiableList(ArrayList(scores))
    val riichiStatus: List<Boolean> = Collections.unmodifiableList(ArrayList(riichiStatus))
    val discards: List<List<TileView>> = Collections.unmodifiableList(discards.map { Collections.unmodifiableList(ArrayList(it)) })
    val melds: List<List<MeldView>> = Collections.unmodifiableList(melds.map { list -> Collections.unmodifiableList(ArrayList(list)) })
    val handTileCounts: List<Int> = Collections.unmodifiableList(ArrayList(handTileCounts))
    val myClosedTiles: List<TileView> = Collections.unmodifiableList(ArrayList(myClosedTiles))
    val legalActions: List<LegalActionView> = Collections.unmodifiableList(ArrayList(legalActions))
    val mySleeveTiles: List<TileView> = Collections.unmodifiableList(ArrayList(mySleeveTiles))
    val myDiscardHistory: List<TileView> = Collections.unmodifiableList(ArrayList(myDiscardHistory))

    init {
        require(viewerSeat in 0..3) { "viewerSeat must be 0..3, got $viewerSeat" }
        require(scores.size == 4) { "scores must have size 4" }
        require(riichiStatus.size == 4) { "riichiStatus must have size 4" }
        require(discards.size == 4) { "discards must have size 4" }
        require(melds.size == 4) { "melds must have size 4" }
        require(handTileCounts.size == 4) { "handTileCounts must have size 4" }
    }

    val isMyTurn: Boolean get() = currentTurnPlayer == viewerSeat
    fun seatWind(seat: Int): Int = ((seat - dealerSeat + 4) % 4) + 1
    val mySeatWind: Int get() = seatWind(viewerSeat)
    val myAllClosedTiles: List<TileView>
        get() = Collections.unmodifiableList(if (myDrawnTile != null) myClosedTiles + myDrawnTile else myClosedTiles)

    fun copy(
        viewerSeat: Int = this.viewerSeat,
        phase: GamePhaseView = this.phase,
        roundWind: Int = this.roundWind,
        roundNumber: Int = this.roundNumber,
        honbaSticks: Int = this.honbaSticks,
        riichiSticks: Int = this.riichiSticks,
        dealerSeat: Int = this.dealerSeat,
        currentTurnPlayer: Int = this.currentTurnPlayer,
        wallRemaining: Int = this.wallRemaining,
        doraIndicators: List<TileView> = this.doraIndicators,
        scores: List<Int> = this.scores,
        riichiStatus: List<Boolean> = this.riichiStatus,
        discards: List<List<TileView>> = this.discards,
        melds: List<List<MeldView>> = this.melds,
        handTileCounts: List<Int> = this.handTileCounts,
        myClosedTiles: List<TileView> = this.myClosedTiles,
        myDrawnTile: TileView? = this.myDrawnTile,
        legalActions: List<LegalActionView> = this.legalActions,
        lastDiscard: TileView? = this.lastDiscard,
        lastDiscardPlayer: Int? = this.lastDiscardPlayer,
        mySleeveTiles: List<TileView> = this.mySleeveTiles,
        myFuriten: FuritenStatusView = this.myFuriten,
        myDiscardHistory: List<TileView> = this.myDiscardHistory
    ): PlayerView = PlayerView(
        viewerSeat, phase, roundWind, roundNumber, honbaSticks, riichiSticks, dealerSeat,
        currentTurnPlayer, wallRemaining, doraIndicators, scores, riichiStatus, discards,
        melds, handTileCounts, myClosedTiles, myDrawnTile, legalActions, lastDiscard,
        lastDiscardPlayer, mySleeveTiles, myFuriten, myDiscardHistory
    )

    override fun equals(other: Any?): Boolean = other is PlayerView &&
        viewerSeat == other.viewerSeat && phase == other.phase &&
        roundWind == other.roundWind && roundNumber == other.roundNumber &&
        honbaSticks == other.honbaSticks && riichiSticks == other.riichiSticks &&
        dealerSeat == other.dealerSeat && currentTurnPlayer == other.currentTurnPlayer &&
        wallRemaining == other.wallRemaining && doraIndicators == other.doraIndicators &&
        scores == other.scores && riichiStatus == other.riichiStatus &&
        discards == other.discards && melds == other.melds &&
        handTileCounts == other.handTileCounts && myClosedTiles == other.myClosedTiles &&
        myDrawnTile == other.myDrawnTile && legalActions == other.legalActions &&
        lastDiscard == other.lastDiscard && lastDiscardPlayer == other.lastDiscardPlayer &&
        mySleeveTiles == other.mySleeveTiles && myFuriten == other.myFuriten &&
        myDiscardHistory == other.myDiscardHistory

    override fun hashCode(): Int {
        var r = viewerSeat.hashCode()
        r = 31 * r + phase.hashCode()
        r = 31 * r + roundWind.hashCode()
        r = 31 * r + roundNumber.hashCode()
        r = 31 * r + honbaSticks.hashCode()
        r = 31 * r + riichiSticks.hashCode()
        r = 31 * r + dealerSeat.hashCode()
        r = 31 * r + currentTurnPlayer.hashCode()
        r = 31 * r + wallRemaining.hashCode()
        r = 31 * r + doraIndicators.hashCode()
        r = 31 * r + scores.hashCode()
        r = 31 * r + riichiStatus.hashCode()
        r = 31 * r + discards.hashCode()
        r = 31 * r + melds.hashCode()
        r = 31 * r + handTileCounts.hashCode()
        r = 31 * r + myClosedTiles.hashCode()
        r = 31 * r + (myDrawnTile?.hashCode() ?: 0)
        r = 31 * r + legalActions.hashCode()
        r = 31 * r + (lastDiscard?.hashCode() ?: 0)
        r = 31 * r + (lastDiscardPlayer?.hashCode() ?: 0)
        r = 31 * r + mySleeveTiles.hashCode()
        r = 31 * r + myFuriten.hashCode()
        r = 31 * r + myDiscardHistory.hashCode()
        return r
    }

    override fun toString(): String =
        "PlayerView(viewerSeat=$viewerSeat, phase=$phase, roundWind=$roundWind, roundNumber=$roundNumber, scores=$scores)"
}

/** 鸣牌方案视图模型 */
class MeldChoiceView(
    val action: LegalActionView,
    val type: MeldTypeView,
    tiles: List<TileView>,
    handTiles: List<TileView>,
    val calledTile: TileView? = null,
    val addedTile: TileView? = null,
    val fromPlayer: Int? = null
) {
    val tiles: List<TileView> = Collections.unmodifiableList(ArrayList(tiles))
    val handTiles: List<TileView> = Collections.unmodifiableList(ArrayList(handTiles))

    fun copy(
        action: LegalActionView = this.action,
        type: MeldTypeView = this.type,
        tiles: List<TileView> = this.tiles,
        handTiles: List<TileView> = this.handTiles,
        calledTile: TileView? = this.calledTile,
        addedTile: TileView? = this.addedTile,
        fromPlayer: Int? = this.fromPlayer
    ): MeldChoiceView = MeldChoiceView(action, type, tiles, handTiles, calledTile, addedTile, fromPlayer)

    override fun equals(other: Any?): Boolean = other is MeldChoiceView &&
        action == other.action && type == other.type && tiles == other.tiles &&
        handTiles == other.handTiles && calledTile == other.calledTile &&
        addedTile == other.addedTile && fromPlayer == other.fromPlayer

    override fun hashCode(): Int {
        var r = action.hashCode()
        r = 31 * r + type.hashCode()
        r = 31 * r + tiles.hashCode()
        r = 31 * r + handTiles.hashCode()
        r = 31 * r + (calledTile?.hashCode() ?: 0)
        r = 31 * r + (addedTile?.hashCode() ?: 0)
        r = 31 * r + (fromPlayer?.hashCode() ?: 0)
        return r
    }

    override fun toString(): String =
        "MeldChoiceView(action=$action, type=$type, tiles=$tiles, handTiles=$handTiles, calledTile=$calledTile, addedTile=$addedTile, fromPlayer=$fromPlayer)"
}

/** 立直弃牌方案视图模型 */
class RiichiDiscardOptionView(
    val action: LegalActionView.DeclareRiichi,
    waits: List<TileView>,
    val furiten: FuritenStatusView = FuritenStatusView()
) {
    val waits: List<TileView> = Collections.unmodifiableList(ArrayList(waits))

    fun copy(
        action: LegalActionView.DeclareRiichi = this.action,
        waits: List<TileView> = this.waits,
        furiten: FuritenStatusView = this.furiten
    ): RiichiDiscardOptionView = RiichiDiscardOptionView(action, waits, furiten)

    override fun equals(other: Any?): Boolean = other is RiichiDiscardOptionView &&
        action == other.action && waits == other.waits && furiten == other.furiten

    override fun hashCode(): Int {
        var r = action.hashCode()
        r = 31 * r + waits.hashCode()
        r = 31 * r + furiten.hashCode()
        return r
    }

    override fun toString(): String =
        "RiichiDiscardOptionView(action=$action, waits=$waits, furiten=$furiten)"
}

/**
 * 游戏事件视图模型 (GameEventView)
 *
 * 对应 [com.majiang.event.GameEvent] 的全部 23 种变体。
 */
sealed class GameEventView {
    abstract val turnNumber: Int

    data class StackingStarted(override val turnNumber: Int = 0) : GameEventView()
    data class WallBuilt(override val turnNumber: Int = 0, val wallSize: Int = 136) : GameEventView()
    data class TileDealt(override val turnNumber: Int, val player: Int, val tile: TileView) : GameEventView()
    data class DealingComplete(override val turnNumber: Int = 0) : GameEventView()
    data class TileDrawn(override val turnNumber: Int, val player: Int, val tile: TileView, val wallRemaining: Int) : GameEventView()
    data class TileDiscarded(
        override val turnNumber: Int,
        val player: Int,
        val tile: TileView,
        val isRiichiDiscard: Boolean = false,
        val isTsumogiri: Boolean = false
    ) : GameEventView()
    data class ChiDeclared(override val turnNumber: Int, val player: Int, val meld: MeldView, val fromPlayer: Int) : GameEventView()
    data class PonDeclared(override val turnNumber: Int, val player: Int, val meld: MeldView, val fromPlayer: Int) : GameEventView()
    data class KanDeclared(override val turnNumber: Int, val player: Int, val meld: MeldView, val kanType: MeldTypeView) : GameEventView()
    data class RinshanDraw(override val turnNumber: Int, val player: Int, val tile: TileView) : GameEventView()
    data class RiichiDeclared(
        override val turnNumber: Int,
        val player: Int,
        val discardTile: TileView,
        val isDoubleRiichi: Boolean = false
    ) : GameEventView()
    class TsumoWin(
        override val turnNumber: Int,
        val player: Int,
        val winTile: TileView,
        yakuList: List<String>,
        val han: Int,
        val fu: Int,
        val score: Int,
        val breakdown: ScoreBreakdownView? = null
    ) : GameEventView() {
        val yakuList: List<String> = Collections.unmodifiableList(ArrayList(yakuList))
        override fun equals(other: Any?): Boolean = other is TsumoWin &&
            turnNumber == other.turnNumber && player == other.player &&
            winTile == other.winTile && yakuList == other.yakuList &&
            han == other.han && fu == other.fu && score == other.score &&
            breakdown == other.breakdown
        override fun hashCode(): Int {
            var r = turnNumber.hashCode()
            r = 31 * r + player.hashCode()
            r = 31 * r + winTile.hashCode()
            r = 31 * r + yakuList.hashCode()
            r = 31 * r + han.hashCode()
            r = 31 * r + fu.hashCode()
            r = 31 * r + score.hashCode()
            r = 31 * r + (breakdown?.hashCode() ?: 0)
            return r
        }
        override fun toString(): String =
            "TsumoWin(turnNumber=$turnNumber, player=$player, winTile=$winTile, yakuList=$yakuList, han=$han, fu=$fu, score=$score, breakdown=$breakdown)"
    }
    class RonWin(
        override val turnNumber: Int,
        val winner: Int,
        val loser: Int,
        val winTile: TileView,
        yakuList: List<String>,
        val han: Int,
        val fu: Int,
        val score: Int,
        val breakdown: ScoreBreakdownView? = null
    ) : GameEventView() {
        val yakuList: List<String> = Collections.unmodifiableList(ArrayList(yakuList))
        override fun equals(other: Any?): Boolean = other is RonWin &&
            turnNumber == other.turnNumber && winner == other.winner &&
            loser == other.loser && winTile == other.winTile &&
            yakuList == other.yakuList && han == other.han &&
            fu == other.fu && score == other.score && breakdown == other.breakdown
        override fun hashCode(): Int {
            var r = turnNumber.hashCode()
            r = 31 * r + winner.hashCode()
            r = 31 * r + loser.hashCode()
            r = 31 * r + winTile.hashCode()
            r = 31 * r + yakuList.hashCode()
            r = 31 * r + han.hashCode()
            r = 31 * r + fu.hashCode()
            r = 31 * r + score.hashCode()
            r = 31 * r + (breakdown?.hashCode() ?: 0)
            return r
        }
        override fun toString(): String =
            "RonWin(turnNumber=$turnNumber, winner=$winner, loser=$loser, winTile=$winTile, yakuList=$yakuList, han=$han, fu=$fu, score=$score, breakdown=$breakdown)"
    }
    data class DoraRevealed(override val turnNumber: Int, val indicator: TileView, val isUraDora: Boolean = false) : GameEventView()
    data class CheatAttempted(
        override val turnNumber: Int,
        val player: Int,
        val technique: CheatTechniqueView,
        val success: Boolean
    ) : GameEventView()
    data class SuspicionChanged(
        override val turnNumber: Int,
        val target: Int,
        val observer: Int,
        val oldValue: Float,
        val newValue: Float
    ) : GameEventView()
    data class BustedEvent(
        override val turnNumber: Int,
        val player: Int,
        val caughtBy: Int,
        val technique: CheatTechniqueView
    ) : GameEventView()
    data class GazeShifted(override val turnNumber: Int, val observer: Int, val newTarget: Int?, val oldTarget: Int?) : GameEventView()
    data class CheatPointsChanged(override val turnNumber: Int, val seat: Int, val amount: Int, val newBalance: Int) : GameEventView()
    class DrawGame(
        override val turnNumber: Int,
        val reason: DrawReasonView,
        tenpaiPlayers: List<Int>
    ) : GameEventView() {
        val tenpaiPlayers: List<Int> = Collections.unmodifiableList(ArrayList(tenpaiPlayers))
        override fun equals(other: Any?): Boolean = other is DrawGame &&
            turnNumber == other.turnNumber && reason == other.reason &&
            tenpaiPlayers == other.tenpaiPlayers
        override fun hashCode(): Int =
            31 * (31 * turnNumber.hashCode() + reason.hashCode()) + tenpaiPlayers.hashCode()
        override fun toString(): String =
            "DrawGame(turnNumber=$turnNumber, reason=$reason, tenpaiPlayers=$tenpaiPlayers)"
    }
    class ScoreSettled(
        override val turnNumber: Int,
        payments: Map<Int, Int>,
        newScores: List<Int>
    ) : GameEventView() {
        val payments: Map<Int, Int> = Collections.unmodifiableMap(LinkedHashMap(payments))
        val newScores: List<Int> = Collections.unmodifiableList(ArrayList(newScores))
        override fun equals(other: Any?): Boolean = other is ScoreSettled &&
            turnNumber == other.turnNumber && payments == other.payments &&
            newScores == other.newScores
        override fun hashCode(): Int =
            31 * (31 * turnNumber.hashCode() + payments.hashCode()) + newScores.hashCode()
        override fun toString(): String =
            "ScoreSettled(turnNumber=$turnNumber, payments=$payments, newScores=$newScores)"
    }
    data class RoundEnded(override val turnNumber: Int, val roundWind: Int, val roundNumber: Int) : GameEventView()
    class MatchEnded(
        override val turnNumber: Int,
        finalScores: List<Int>,
        val winner: Int
    ) : GameEventView() {
        val finalScores: List<Int> = Collections.unmodifiableList(ArrayList(finalScores))
        override fun equals(other: Any?): Boolean = other is MatchEnded &&
            turnNumber == other.turnNumber && finalScores == other.finalScores &&
            winner == other.winner
        override fun hashCode(): Int =
            31 * (31 * turnNumber.hashCode() + finalScores.hashCode()) + winner.hashCode()
        override fun toString(): String =
            "MatchEnded(turnNumber=$turnNumber, finalScores=$finalScores, winner=$winner)"
    }
}

/**
 * 完整对局快照聚合根视图模型 (GameSnapshotView)
 *
 * 对应 [com.majiang.access.PlayerSnapshot] 全字段，纯客户端视图模型。
 */
class GameSnapshotView(
    val sessionId: String,
    val roundId: String?,
    val syncEpoch: String,
    val cursor: Long,
    val sessionVersion: Long,
    val roundVersion: Long?,
    val view: PlayerView,
    val decision: DecisionView,
    clocks: List<ClockInfo> = emptyList(),
    cheatPoints: List<Int> = emptyList(),
    val session: SessionProgressView? = null,
    controls: List<ControlsInfo> = emptyList(),
    val paused: Boolean = false,
    val settlementRoundId: String? = null,
    settlementEvents: List<GameEventView> = emptyList(),
    val settlementDetail: SettlementDetailView? = null,
    val preparation: ShufflePreparationView? = null,
    val presentationPreview: PlayerPresentationPreviewView? = null
) {
    val clocks: List<ClockInfo> = Collections.unmodifiableList(ArrayList(clocks))
    val cheatPoints: List<Int> = Collections.unmodifiableList(ArrayList(cheatPoints))
    val controls: List<ControlsInfo> = Collections.unmodifiableList(ArrayList(controls))
    val settlementEvents: List<GameEventView> = Collections.unmodifiableList(ArrayList(settlementEvents))

    val viewerSeat: Int get() = view.viewerSeat

    init {
        require(sessionId.isNotBlank()) { "sessionId cannot be blank" }
        require(syncEpoch.isNotBlank()) { "syncEpoch cannot be blank" }
        require(cursor >= 0L) { "cursor must be >= 0: $cursor" }
        require(sessionVersion >= 0L) { "sessionVersion must be >= 0: $sessionVersion" }
        if (roundVersion != null) {
            require(roundVersion >= 0L) { "roundVersion must be >= 0: $roundVersion" }
        }
        if (presentationPreview != null) {
            require(roundId != null) {
                "presentationPreview cannot be present when roundId is null"
            }
            require(presentationPreview.roundId == roundId) {
                "presentationPreview.roundId (${presentationPreview.roundId}) does not match roundId ($roundId)"
            }
            require(decision.roundId == roundId) {
                "decision.roundId (${decision.roundId}) does not match roundId ($roundId) when presentationPreview is present"
            }
            require(presentationPreview.decisionId == decision.decisionId) {
                "presentationPreview.decisionId (${presentationPreview.decisionId}) does not match decision.decisionId (${decision.decisionId})"
            }
            require(presentationPreview.controlEpoch == decision.controlEpoch) {
                "presentationPreview.controlEpoch (${presentationPreview.controlEpoch}) does not match decision.controlEpoch (${decision.controlEpoch})"
            }
            val decisionOptionsById = decision.options.associateBy { it.optionId }
            for (riichiOpt in presentationPreview.riichiOptions) {
                val matched = decisionOptionsById[riichiOpt.optionId]
                require(matched != null) {
                    "presentationPreview riichi optionId '${riichiOpt.optionId}' not found in decision.options"
                }
                require(matched.action == riichiOpt.action) {
                    "presentationPreview riichi option '${riichiOpt.optionId}' action (${riichiOpt.action}) does not match decision action (${matched.action})"
                }
            }
            for (meldOpt in presentationPreview.meldOptions) {
                val matched = decisionOptionsById[meldOpt.optionId]
                require(matched != null) {
                    "presentationPreview meld optionId '${meldOpt.optionId}' not found in decision.options"
                }
                require(matched.action == meldOpt.action) {
                    "presentationPreview meld option '${meldOpt.optionId}' action (${meldOpt.action}) does not match decision action (${matched.action})"
                }
            }
        }
    }

    fun copy(
        sessionId: String = this.sessionId,
        roundId: String? = this.roundId,
        syncEpoch: String = this.syncEpoch,
        cursor: Long = this.cursor,
        sessionVersion: Long = this.sessionVersion,
        roundVersion: Long? = this.roundVersion,
        view: PlayerView = this.view,
        decision: DecisionView = this.decision,
        clocks: List<ClockInfo> = this.clocks,
        cheatPoints: List<Int> = this.cheatPoints,
        session: SessionProgressView? = this.session,
        controls: List<ControlsInfo> = this.controls,
        paused: Boolean = this.paused,
        settlementRoundId: String? = this.settlementRoundId,
        settlementEvents: List<GameEventView> = this.settlementEvents,
        settlementDetail: SettlementDetailView? = this.settlementDetail,
        preparation: ShufflePreparationView? = this.preparation,
        presentationPreview: PlayerPresentationPreviewView? = this.presentationPreview
    ): GameSnapshotView = GameSnapshotView(
        sessionId, roundId, syncEpoch, cursor, sessionVersion, roundVersion,
        view, decision, clocks, cheatPoints, session, controls, paused,
        settlementRoundId, settlementEvents, settlementDetail, preparation, presentationPreview
    )

    override fun equals(other: Any?): Boolean = other is GameSnapshotView &&
        sessionId == other.sessionId && roundId == other.roundId &&
        syncEpoch == other.syncEpoch && cursor == other.cursor &&
        sessionVersion == other.sessionVersion && roundVersion == other.roundVersion &&
        view == other.view && decision == other.decision &&
        clocks == other.clocks && cheatPoints == other.cheatPoints &&
        session == other.session && controls == other.controls &&
        paused == other.paused && settlementRoundId == other.settlementRoundId &&
        settlementEvents == other.settlementEvents && settlementDetail == other.settlementDetail &&
        preparation == other.preparation && presentationPreview == other.presentationPreview

    override fun hashCode(): Int {
        var r = sessionId.hashCode()
        r = 31 * r + (roundId?.hashCode() ?: 0)
        r = 31 * r + syncEpoch.hashCode()
        r = 31 * r + cursor.hashCode()
        r = 31 * r + sessionVersion.hashCode()
        r = 31 * r + (roundVersion?.hashCode() ?: 0)
        r = 31 * r + view.hashCode()
        r = 31 * r + decision.hashCode()
        r = 31 * r + clocks.hashCode()
        r = 31 * r + cheatPoints.hashCode()
        r = 31 * r + (session?.hashCode() ?: 0)
        r = 31 * r + controls.hashCode()
        r = 31 * r + paused.hashCode()
        r = 31 * r + (settlementRoundId?.hashCode() ?: 0)
        r = 31 * r + settlementEvents.hashCode()
        r = 31 * r + (settlementDetail?.hashCode() ?: 0)
        r = 31 * r + (preparation?.hashCode() ?: 0)
        r = 31 * r + (presentationPreview?.hashCode() ?: 0)
        return r
    }

    override fun toString(): String =
        "GameSnapshotView(sessionId=$sessionId, roundId=$roundId, cursor=$cursor, sessionVersion=$sessionVersion, view=$view, decision=$decision)"
}

/**
 * 对局增量更新视图模型 (GameUpdateView)
 *
 * 对应 [com.majiang.access.PlayerUpdate] 全字段。
 */
class GameUpdateView(
    val snapshot: GameSnapshotView,
    val fromCursor: Long,
    visibleEvents: List<GameEventView> = emptyList(),
    val isFullSnapshot: Boolean = false
) {
    val visibleEvents: List<GameEventView> = Collections.unmodifiableList(ArrayList(visibleEvents))

    init {
        require(fromCursor >= 0L) { "fromCursor must be >= 0: $fromCursor" }
        require(snapshot.cursor >= fromCursor) {
            "snapshot.cursor (${snapshot.cursor}) cannot be less than fromCursor ($fromCursor)"
        }
    }

    operator fun component1(): GameSnapshotView = snapshot
    operator fun component2(): Long = fromCursor
    operator fun component3(): List<GameEventView> = visibleEvents
    operator fun component4(): Boolean = isFullSnapshot

    fun copy(
        snapshot: GameSnapshotView = this.snapshot,
        fromCursor: Long = this.fromCursor,
        visibleEvents: List<GameEventView> = this.visibleEvents,
        isFullSnapshot: Boolean = this.isFullSnapshot
    ): GameUpdateView = GameUpdateView(snapshot, fromCursor, visibleEvents, isFullSnapshot)

    override fun equals(other: Any?): Boolean = other is GameUpdateView &&
        snapshot == other.snapshot && fromCursor == other.fromCursor &&
        visibleEvents == other.visibleEvents && isFullSnapshot == other.isFullSnapshot

    override fun hashCode(): Int {
        var r = snapshot.hashCode()
        r = 31 * r + fromCursor.hashCode()
        r = 31 * r + visibleEvents.hashCode()
        r = 31 * r + isFullSnapshot.hashCode()
        return r
    }

    override fun toString(): String =
        "GameUpdateView(snapshot=$snapshot, fromCursor=$fromCursor, visibleEvents=$visibleEvents, isFullSnapshot=$isFullSnapshot)"
}
