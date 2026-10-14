package com.majiang.projection

import com.majiang.access.ClockView
import com.majiang.event.GameEvent
import com.majiang.match.SessionLifecycle
import com.majiang.match.SessionSnapshot
import com.majiang.state.GameState

/**
 * 独立公共领域投影工厂 (PublicProjectionFactory)
 *
 * 遵循冻结领域契约与 TASK-ANTI-R4B2PUBLIC 规范：
 * 1. 作为纯函数入口，仅消费 [SessionSnapshot] 与 [GameState]，不读取 PlayerView、不调用 querySnapshot(seat)、不伪造 seat 身份；
 * 2. 状态机未就绪（state == null）时严格产出 board = null，不伪造桌面；
 * 3. 严格执行公共事件白名单脱敏与不可变深拷贝防御；
 * 4. 严禁自作主张给领域投影加入网络/连接/游标语义；
 * 5. 严格执行里宝牌结算授权边界：仅调用者显式传入 revealSettlementUra = true 时暴露已公开里宝，无授权时剥除 uraTiles 与 uraHits。
 */
object PublicProjectionFactory {

    /**
     * 将权威整场快照与活动局状态投影为公开领域视图
     */
    fun project(
        session: SessionSnapshot,
        state: GameState?,
        roundId: String?,
        roundVersion: Long?,
        clocks: List<ClockView>,
        paused: Boolean,
        settlementRoundId: String? = null,
        settlementEvents: List<GameEvent> = emptyList(),
        revealSettlementUra: Boolean = false
    ): PublicTableProjection {
        val board = if (state != null) {
            PublicBoardView.fromGameState(state)
        } else {
            null
        }

        val scores = if (session.lifecycle == SessionLifecycle.FINISHED) {
            session.betweenRoundScores.toList()
        } else if (state != null) {
            state.scores.toList()
        } else {
            session.betweenRoundScores.toList()
        }

        val publicRecords = session.roundRecords.map { rec ->
            PublicScoreRecord.create(
                roundId = rec.roundId,
                resultId = rec.resultId,
                finalScores = rec.finalScores,
                deltas = rec.deltas,
                remainingRiichiSticks = rec.remainingRiichiSticks,
                outcome = rec.outcome
            )
        }

        val publicResult = session.result?.let { res ->
            PublicMatchResult.create(
                finalScores = res.finalScores,
                rankedSeats = res.rankings.map { it.seat },
                reason = res.reason.name,
                remainingRiichiSticksTransferredTo = res.remainingRiichiSticksTransferredTo
            )
        }

        val sessionSummary = PublicSessionSummary.create(
            lifecycle = session.lifecycle.name,
            scores = scores,
            roundWind = session.currentRoundWind,
            roundNumber = session.currentRoundNumber,
            dealerSeat = session.currentDealer,
            honba = session.currentHonba,
            roundRecords = publicRecords,
            finalResult = publicResult
        )

        val publicSettlementEvents = publicEvents(settlementEvents, revealSettlementUra = revealSettlementUra)

        return PublicTableProjection.create(
            sessionId = session.sessionId,
            sessionVersion = session.version,
            roundId = roundId,
            roundVersion = roundVersion,
            board = board,
            session = sessionSummary,
            clocks = clocks,
            paused = paused,
            settlementRoundId = settlementRoundId,
            settlementEvents = publicSettlementEvents
        )
    }

    /**
     * 针对公共观战投影脱敏并过滤事件。
     *
     * 明确白名单规则：
     * - 允许：流程 (StackingStarted, WallBuilt, DealingComplete)、公开弃牌 (TileDiscarded)、
     *         副露 (ChiDeclared, PonDeclared, KanDeclared)、立直 (RiichiDeclared)、
     *         和牌 (TsumoWin, RonWin)、流局 (DrawGame)、计分结算 (ScoreSettled)、
     *         终局 (RoundEnded, MatchEnded)、表宝牌 (DoraRevealed with isUraDora=false)。
     * - 里宝牌 (DoraRevealed with isUraDora=true)：仅在调用方明确传入 revealSettlementUra=true 时保留，
     *   其他情况（即使事件列表包含普通 RoundEnded）一律过滤丢弃。
     * - 丢弃：发牌 (TileDealt)、摸牌 (TileDrawn)、岭上摸牌 (RinshanDraw)、
     *         出千点数流水 (CheatPointsChanged) 及其他私密事件。
     */
    fun publicEvents(
        events: List<GameEvent>,
        revealSettlementUra: Boolean = false
    ): List<GameEvent> {
        val filtered = events.mapNotNull { event ->
            filterPublicEvent(event, revealSettlementUra)
        }
        return CopyOnReadEventList(filtered, revealSettlementUra)
    }

    private fun filterPublicEvent(
        event: GameEvent,
        revealSettlementUra: Boolean
    ): GameEvent? {
        val snap = deepCopyEvent(event, revealSettlementUra)
        return when (snap) {
            is GameEvent.StackingStarted -> snap
            is GameEvent.WallBuilt -> snap
            is GameEvent.DealingComplete -> snap
            is GameEvent.TileDiscarded -> snap
            is GameEvent.ChiDeclared -> snap
            is GameEvent.PonDeclared -> snap
            is GameEvent.KanDeclared -> snap
            is GameEvent.RiichiDeclared -> snap
            is GameEvent.TsumoWin -> snap
            is GameEvent.RonWin -> snap
            is GameEvent.DoraRevealed -> {
                if (!snap.isUraDora || revealSettlementUra) {
                    snap
                } else {
                    null
                }
            }
            is GameEvent.DrawGame -> snap
            is GameEvent.ScoreSettled -> snap
            is GameEvent.RoundEnded -> snap
            is GameEvent.MatchEnded -> snap

            // 严格过滤丢弃的私密/千术/摸发牌/流水事件：
            is GameEvent.TileDealt -> null
            is GameEvent.TileDrawn -> null
            is GameEvent.RinshanDraw -> null
            is GameEvent.CheatPointsChanged -> null
            else -> null
        }
    }
}
