package com.majiang.session

import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import java.util.Collections

/**
 * 会话广播更新数据包
 *
 * 携带更新后的座位视图 [view] 以及经过可见性脱敏的事件列表 [visibleEvents]。
 * 绝不向界面广播他家的摸牌具体牌张或全量发牌详情。
 * 内部事件与集合均进行严格深拷贝快照与不可变包装，防止领域层后续变更发生状态漂移。
 */
class SessionUpdate(
    val view: PlayerView,
    visibleEvents: List<GameEvent> = emptyList()
) {
    /** 针对外部传入的事件列表执行防御性深拷贝快照并包装为不可修改集合 */
    val visibleEvents: List<GameEvent> = Collections.unmodifiableList(
        visibleEvents.map { snapshotGameEvent(it) }
    )

    /** 获取最新的可见事件（若有） */
    val latestEvent: GameEvent? get() = visibleEvents.lastOrNull()

    operator fun component1(): PlayerView = view
    operator fun component2(): List<GameEvent> = visibleEvents

    fun copy(
        view: PlayerView = this.view,
        visibleEvents: List<GameEvent> = this.visibleEvents
    ): SessionUpdate = SessionUpdate(view, visibleEvents)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SessionUpdate) return false
        return view == other.view && visibleEvents == other.visibleEvents
    }

    override fun hashCode(): Int {
        var result = view.hashCode()
        result = 31 * result + visibleEvents.hashCode()
        return result
    }

    override fun toString(): String {
        return "SessionUpdate(view=$view, visibleEvents=$visibleEvents)"
    }

    companion object {
        /**
         * 对单个事件进行深拷贝快照，复用 event 包提供的 snapshotGameEvent
         */
        fun snapshotEvent(event: GameEvent): GameEvent = snapshotGameEvent(event)

        /**
         * 针对特定观察者座位过滤并脱敏领域事件。
         *
         * 过滤规则：
         * - 发牌 TileDealt：仅观察者自己的发牌可见，他家发牌过滤隐藏。
         * - 摸牌 TileDrawn：仅观察者自己的摸牌可见具体牌面，他家摸牌隐藏具体牌张（防透视作弊）。
         * - 岭上摸牌 RinshanDraw：同摸牌规则。
         * - 里宝牌 DoraRevealed(isUraDora=true)：若未公开或未授权，绝不发给其他人；仅在终局结算（和了/流局）时公开。
         * - 打牌、副露、立直、和了、流局、结算、表宝牌等公开事件：全员可见。
         */
        fun filterVisibleEvents(events: List<GameEvent>, viewerSeat: Int): List<GameEvent> {
            val isUraDoraAuthorized = events.any {
                it is GameEvent.TsumoWin || it is GameEvent.RonWin ||
                        it is GameEvent.ScoreSettled || it is GameEvent.RoundEnded ||
                        it is GameEvent.MatchEnded
            }

            return Collections.unmodifiableList(
                events.mapNotNull { event ->
                    redactForViewer(event, viewerSeat, isUraDoraAuthorized)
                }
            )
        }

        fun redactForViewer(
            event: GameEvent,
            viewerSeat: Int,
            isUraDoraAuthorized: Boolean = false
        ): GameEvent? {
            val visible = when (event) {
                is GameEvent.TileDealt -> if (event.player == viewerSeat) event else null
                is GameEvent.TileDrawn -> if (event.player == viewerSeat) event else null
                is GameEvent.RinshanDraw -> if (event.player == viewerSeat) event else null
                is GameEvent.DoraRevealed -> if (!event.isUraDora || isUraDoraAuthorized) event else null
                else -> event
            }
            return visible?.let { snapshotGameEvent(it) }
        }
    }
}
