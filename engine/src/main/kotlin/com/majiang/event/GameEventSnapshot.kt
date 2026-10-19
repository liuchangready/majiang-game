package com.majiang.event

import com.majiang.rules.ScoreBreakdown
import java.util.ArrayList
import java.util.Collections

/**
 * 游戏事件深拷贝快照辅助函数
 *
 * 为事件及其内部的数组、Map、嵌套副露牌列表、役种列表等可变集合生成深度隔离的快照。
 * 供 [GameEventBus] 及会话层 [com.majiang.session.SessionUpdate] 复用，
 * 避免领域事件反向依赖会话包。
 */
fun GameEvent.snapshot(): GameEvent = snapshotGameEvent(this)

/**
 * 深拷贝且不可修改 ScoreBreakdown 中的所有嵌套集合
 */
fun snapshotScoreBreakdown(breakdown: ScoreBreakdown?): ScoreBreakdown? {
    if (breakdown == null) return null
    val safeIndicatorHits = Collections.unmodifiableList(
        breakdown.dora.indicatorHits.map { hit ->
            hit.copy(tiles = Collections.unmodifiableList(ArrayList(hit.tiles)))
        }
    )
    val safeUraHits = Collections.unmodifiableList(
        breakdown.dora.uraHits.map { hit ->
            hit.copy(tiles = Collections.unmodifiableList(ArrayList(hit.tiles)))
        }
    )
    val safeDora = breakdown.dora.copy(
        indicatorTiles = Collections.unmodifiableList(ArrayList(breakdown.dora.indicatorTiles)),
        uraTiles = Collections.unmodifiableList(ArrayList(breakdown.dora.uraTiles)),
        redTiles = Collections.unmodifiableList(ArrayList(breakdown.dora.redTiles)),
        indicatorHits = safeIndicatorHits,
        uraHits = safeUraHits
    )
    val safeFu = breakdown.fu.copy(
        lines = Collections.unmodifiableList(ArrayList(breakdown.fu.lines))
    )
    return breakdown.copy(
        yakuLines = Collections.unmodifiableList(ArrayList(breakdown.yakuLines)),
        yakuEntries = Collections.unmodifiableList(ArrayList(breakdown.yakuEntries)),
        paymentLines = Collections.unmodifiableList(ArrayList(breakdown.paymentLines)),
        dora = safeDora,
        fu = safeFu
    )
}

fun snapshotGameEvent(event: GameEvent): GameEvent {
    return when (event) {
        is GameEvent.ScoreSettled -> event.copy(
            payments = Collections.unmodifiableMap(LinkedHashMap(event.payments)),
            newScores = event.newScores.clone()
        )
        is GameEvent.MatchEnded -> event.copy(
            finalScores = event.finalScores.clone()
        )
        is GameEvent.TsumoWin -> event.copy(
            yakuList = Collections.unmodifiableList(ArrayList(event.yakuList)),
            breakdown = snapshotScoreBreakdown(event.breakdown)
        )
        is GameEvent.RonWin -> event.copy(
            yakuList = Collections.unmodifiableList(ArrayList(event.yakuList)),
            breakdown = snapshotScoreBreakdown(event.breakdown)
        )
        is GameEvent.DrawGame -> event.copy(
            tenpaiPlayers = Collections.unmodifiableList(ArrayList(event.tenpaiPlayers))
        )
        is GameEvent.ChiDeclared -> event.copy(
            meld = event.meld.copy(tiles = Collections.unmodifiableList(ArrayList(event.meld.tiles)))
        )
        is GameEvent.PonDeclared -> event.copy(
            meld = event.meld.copy(tiles = Collections.unmodifiableList(ArrayList(event.meld.tiles)))
        )
        is GameEvent.KanDeclared -> event.copy(
            meld = event.meld.copy(tiles = Collections.unmodifiableList(ArrayList(event.meld.tiles)))
        )
        else -> event
    }
}

object GameEventSnapshot {
    fun snapshot(event: GameEvent): GameEvent = snapshotGameEvent(event)
    fun snapshotBreakdown(breakdown: ScoreBreakdown?): ScoreBreakdown? = snapshotScoreBreakdown(breakdown)
}
