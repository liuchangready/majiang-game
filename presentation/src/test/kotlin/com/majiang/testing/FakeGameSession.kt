package com.majiang.testing

import com.badlogic.gdx.Gdx
import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.*
import java.util.Collections
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit

/**
 * 表现层测试专用的纯 client.api 对局会话测试双工 (FakeGameSession)。
 *
 * 遵循 V4 契约与纯客户端隔离规范：
 * 1. 严格只依赖 client.api 视图与模型，杜绝 import engine、protocol 或 server；
 * 2. 维护不可变缓存快照 [currentSnapshot] 与连接状态；
 * 3. 记录提交的动作 [submittedChoices] 并解析回 [submittedActions] / [submitted]；
 * 4. 自动合成决策选项与表现层立直/副露展示预览；
 * 5. 支持 [update]、[publish] 与 [flushUi]。
 */
class FakeGameSession(
    initialSnapshot: GameSnapshotView? = null,
    initialView: PlayerView? = null
) : GameSession {

    private var cursorSeq: Long = 1L
    private var roundSeq: Int = 1
    private var currentSessionId: String = "fake-session"
    private var currentSyncEpoch: String = "fake-epoch"
    private var currentRoundId: String = "round-1-1-0-1"
    private var currentSettlementRoundId: String? = null
    private var lastRoundWind: Int = 1
    private var lastRoundNumber: Int = 1
    private var lastHonbaSticks: Int = 0
    private var lastPhase: GamePhaseView = GamePhaseView.DISCARD
    private val currentSettlementEvents = mutableListOf<GameEventView>()

    private val updateListeners = mutableListOf<(GameUpdateView) -> Unit>()
    private val disconnectListeners = mutableListOf<(String) -> Unit>()
    private val stateListeners = mutableListOf<(ConnectionState) -> Unit>()

    override var closed: Boolean = false
    override var disconnectReason: String? = null
    override var connectionState: ConnectionState = ConnectionState.CONNECTED
    override var isPending: Boolean = false

    val submittedChoices = mutableListOf<ActionChoice>()
    val submittedActions = mutableListOf<LegalActionView>()
    val submitted: List<LegalActionView> get() = submittedActions
    val submittedPlans = mutableListOf<ShufflePlan>()
    val submittedMarks = mutableListOf<TileMarkRequest>()

    var started: Boolean = false
    var nextRoundCalls: Int = 0
    var claimControlCalls: Int = 0
    var releaseControlCalls: Int = 0

    var submitResult: CompletableFuture<Receipt>? = null

    private var _snapshot: GameSnapshotView

    init {
        val view = initialView ?: initialSnapshot?.view ?: TestViewBuilder.playerView()
        lastRoundWind = view.roundWind
        lastRoundNumber = view.roundNumber
        lastHonbaSticks = view.honbaSticks
        lastPhase = view.phase
        currentRoundId = "round-${view.roundWind}-${view.roundNumber}-${view.honbaSticks}-$roundSeq"

        _snapshot = initialSnapshot ?: buildSnapshot(view, emptyList(), cursorSeq)
    }

    override val currentSnapshot: GameSnapshotView get() = _snapshot
    val snapshot: GameSnapshotView get() = _snapshot
    val view: PlayerView get() = _snapshot.view

    override fun onStateChange(listener: (ConnectionState) -> Unit): AutoCloseable {
        stateListeners.add(listener)
        return AutoCloseable { stateListeners.remove(listener) }
    }

    override fun snapshot(): CompletableFuture<GameSnapshotView> {
        return CompletableFuture.completedFuture(_snapshot)
    }

    override fun decision(): CompletableFuture<DecisionView> {
        return CompletableFuture.completedFuture(_snapshot.decision)
    }

    override fun submit(choice: ActionChoice): CompletableFuture<Receipt> {
        submittedChoices.add(choice)
        val matched = _snapshot.decision.options.find { it.optionId == choice.optionId }
        if (matched != null) {
            submittedActions.add(matched.action)
        }
        val custom = submitResult
        if (custom != null) {
            submitResult = null
            return custom
        }
        return CompletableFuture.completedFuture(Receipt(choice.requestId, ReceiptStatus.APPLIED))
    }

    override fun submitShufflePlan(plan: ShufflePlan): CompletableFuture<Receipt> {
        submittedPlans.add(plan)
        val custom = submitResult
        if (custom != null) {
            submitResult = null
            return custom
        }
        return CompletableFuture.completedFuture(Receipt(plan.requestId, ReceiptStatus.RECORDED))
    }

    override fun submitTileMark(request: TileMarkRequest): CompletableFuture<Receipt> {
        submittedMarks.add(request)
        val result = submitResult ?: CompletableFuture.completedFuture(Receipt(request.requestId, ReceiptStatus.APPLIED))
        submitResult = null
        isPending = true
        result.whenComplete { _, _ -> isPending = false }
        return result
    }

    override fun start(): CompletableFuture<Receipt> {
        started = true
        return CompletableFuture.completedFuture(Receipt("start", ReceiptStatus.APPLIED))
    }

    override fun nextRound(): CompletableFuture<Receipt> {
        nextRoundCalls++
        return CompletableFuture.completedFuture(Receipt("nextRound", ReceiptStatus.APPLIED))
    }

    override fun resume(cursor: Long): CompletableFuture<GameSnapshotView> {
        return CompletableFuture.completedFuture(_snapshot)
    }

    override fun claimControl(): CompletableFuture<Receipt> {
        claimControlCalls++
        return CompletableFuture.completedFuture(Receipt("claimControl", ReceiptStatus.APPLIED))
    }

    override fun releaseControl(): CompletableFuture<Receipt> {
        releaseControlCalls++
        return CompletableFuture.completedFuture(Receipt("releaseControl", ReceiptStatus.APPLIED))
    }

    override fun queryRoundResult(roundId: String): CompletableFuture<List<GameEventView>> {
        return CompletableFuture.completedFuture(_snapshot.settlementEvents)
    }

    override fun onUpdate(listener: (GameUpdateView) -> Unit): AutoCloseable {
        updateListeners.add(listener)
        return AutoCloseable { updateListeners.remove(listener) }
    }

    override fun onDisconnect(listener: (reason: String) -> Unit): AutoCloseable {
        disconnectListeners.add(listener)
        return AutoCloseable { disconnectListeners.remove(listener) }
    }

    override fun close() {
        closed = true
        updateListeners.clear()
        disconnectListeners.clear()
        stateListeners.clear()
    }

    fun update(view: PlayerView, events: List<GameEventView> = emptyList()) {
        val isNewRound = (view.roundWind != lastRoundWind ||
            view.roundNumber != lastRoundNumber ||
            view.honbaSticks != lastHonbaSticks ||
            ((lastPhase == GamePhaseView.ROUND_END || lastPhase == GamePhaseView.MATCH_END) &&
                view.phase != GamePhaseView.ROUND_END && view.phase != GamePhaseView.MATCH_END) ||
            events.any { it is GameEventView.StackingStarted || it is GameEventView.DealingComplete })

        if (isNewRound) {
            roundSeq++
            currentRoundId = "round-${view.roundWind}-${view.roundNumber}-${view.honbaSticks}-$roundSeq"
            currentSettlementEvents.clear()
            currentSettlementRoundId = null
        }

        lastRoundWind = view.roundWind
        lastRoundNumber = view.roundNumber
        lastHonbaSticks = view.honbaSticks
        lastPhase = view.phase

        for (event in events) {
            if (isSettlementEvent(event) && event !in currentSettlementEvents) {
                currentSettlementEvents.add(event)
            }
        }
        if (currentSettlementEvents.isNotEmpty() && currentSettlementRoundId == null) {
            currentSettlementRoundId = currentRoundId
        }

        cursorSeq++
        _snapshot = buildSnapshot(view, events, cursorSeq)
        val update = GameUpdateView(
            snapshot = _snapshot,
            fromCursor = cursorSeq - 1,
            visibleEvents = events,
            isFullSnapshot = isNewRound
        )
        updateListeners.toList().forEach { it(update) }
        flushUi()
    }

    fun publish(snap: GameSnapshotView) {
        _snapshot = snap
        cursorSeq = maxOf(cursorSeq, snap.cursor)
        snap.roundId?.let { currentRoundId = it }
        val update = GameUpdateView(
            snapshot = snap,
            fromCursor = snap.cursor - 1,
            visibleEvents = emptyList(),
            isFullSnapshot = true
        )
        updateListeners.toList().forEach { it(update) }
        flushUi()
    }

    fun flushUi() {
        val app = Gdx.app ?: return
        if (GdxHeadlessTestRunner.isUiThread()) return
        val done = CompletableFuture<Unit>()
        app.postRunnable {
            app.postRunnable {
                done.complete(Unit)
            }
        }
        done.get(3, TimeUnit.SECONDS)
    }

    private fun isSettlementEvent(event: GameEventView): Boolean = when (event) {
        is GameEventView.TsumoWin,
        is GameEventView.RonWin,
        is GameEventView.DrawGame,
        is GameEventView.ScoreSettled,
        is GameEventView.RoundEnded,
        is GameEventView.MatchEnded -> true
        is GameEventView.DoraRevealed -> event.isUraDora
        is GameEventView.CheatPointsChanged -> true
        else -> false
    }

    private fun buildSnapshot(view: PlayerView, events: List<GameEventView>, cursor: Long): GameSnapshotView {
        val decisionId = "dec-$cursor"
        val options = view.legalActions.mapIndexed { idx, action ->
            ActionOptionView(optionId = "opt-${action::class.simpleName}-$idx", action = action)
        }

        val riichiOptions = options.mapNotNull { opt ->
            val action = opt.action as? LegalActionView.DeclareRiichi ?: return@mapNotNull null
            RiichiPreviewOptionView(
                optionId = opt.optionId,
                action = action,
                waits = listOf(TestViewBuilder.tile(SuitView.SOU, 4)),
                furiten = view.myFuriten
            )
        }

        val meldOptions = options.mapNotNull { opt ->
            when (val action = opt.action) {
                is LegalActionView.DeclareChi -> {
                    MeldPreviewOptionView(
                        optionId = opt.optionId,
                        action = action,
                        type = MeldTypeView.CHI,
                        tiles = action.meld.tiles,
                        handTiles = action.meld.tiles.filter { it != action.meld.calledTile },
                        calledTile = action.meld.calledTile,
                        addedTile = null,
                        fromPlayer = action.meld.fromPlayer
                    )
                }
                is LegalActionView.DeclarePon -> {
                    val typeIdx = action.tiles.firstOrNull()?.typeIndex ?: 0
                    val usedCopies = action.tiles.map { it.copyIndex }.toSet()
                    val unusedCopy = (0..3).firstOrNull { it !in usedCopies } ?: 0
                    val called = view.lastDiscard ?: TileView.fromTypeIndex(typeIdx, unusedCopy)
                    val fromP = view.lastDiscardPlayer ?: ((view.viewerSeat + 3) % 4)
                    MeldPreviewOptionView(
                        optionId = opt.optionId,
                        action = action,
                        type = MeldTypeView.PON,
                        tiles = action.tiles + called,
                        handTiles = action.tiles,
                        calledTile = called,
                        addedTile = null,
                        fromPlayer = fromP
                    )
                }
                is LegalActionView.DeclareKan -> {
                    when (action.kanType) {
                        MeldTypeView.OPEN_KAN -> {
                            val typeIdx = action.tiles.firstOrNull()?.typeIndex ?: 0
                            val usedCopies = action.tiles.map { it.copyIndex }.toSet()
                            val unusedCopy = (0..3).firstOrNull { it !in usedCopies } ?: 0
                            val called = view.lastDiscard ?: TileView.fromTypeIndex(typeIdx, unusedCopy)
                            val fromP = view.lastDiscardPlayer ?: ((view.viewerSeat + 3) % 4)
                            MeldPreviewOptionView(
                                optionId = opt.optionId,
                                action = action,
                                type = MeldTypeView.OPEN_KAN,
                                tiles = action.tiles + called,
                                handTiles = action.tiles,
                                calledTile = called,
                                addedTile = null,
                                fromPlayer = fromP
                            )
                        }
                        MeldTypeView.CLOSED_KAN -> {
                            MeldPreviewOptionView(
                                optionId = opt.optionId,
                                action = action,
                                type = MeldTypeView.CLOSED_KAN,
                                tiles = action.tiles,
                                handTiles = action.tiles,
                                calledTile = null,
                                addedTile = null,
                                fromPlayer = null
                            )
                        }
                        MeldTypeView.ADDED_KAN -> {
                            val added = action.tiles.last()
                            MeldPreviewOptionView(
                                optionId = opt.optionId,
                                action = action,
                                type = MeldTypeView.ADDED_KAN,
                                tiles = action.tiles,
                                handTiles = listOf(added),
                                calledTile = action.tiles.first(),
                                addedTile = added,
                                fromPlayer = (view.viewerSeat + 3) % 4
                            )
                        }
                        else -> null
                    }
                }
                else -> null
            }
        }

        val presentationPreview = if (riichiOptions.isNotEmpty() || meldOptions.isNotEmpty()) {
            PlayerPresentationPreviewView(
                roundId = currentRoundId,
                decisionId = decisionId,
                controlEpoch = 0L,
                waits = emptyList(),
                riichiOptions = riichiOptions,
                meldOptions = meldOptions
            )
        } else null

        val decision = DecisionView(
            roundId = currentRoundId,
            decisionId = decisionId,
            controlEpoch = 0L,
            actions = view.legalActions,
            options = options
        )

        val tsumoWin = events.filterIsInstance<GameEventView.TsumoWin>().firstOrNull()
        val ronWin = events.filterIsInstance<GameEventView.RonWin>().firstOrNull()
        val settlementDetail = when {
            tsumoWin != null -> SettlementDetailView(
                title = "自摸和牌",
                winner = tsumoWin.player,
                loser = null,
                winTile = tsumoWin.winTile,
                yakuList = tsumoWin.yakuList,
                han = tsumoWin.han,
                fu = tsumoWin.fu,
                score = tsumoWin.score,
                isDraw = false,
                breakdown = tsumoWin.breakdown
            )
            ronWin != null -> SettlementDetailView(
                title = "荣和",
                winner = ronWin.winner,
                loser = ronWin.loser,
                winTile = ronWin.winTile,
                yakuList = ronWin.yakuList,
                han = ronWin.han,
                fu = ronWin.fu,
                score = ronWin.score,
                isDraw = false,
                breakdown = ronWin.breakdown
            )
            else -> null
        }

        return GameSnapshotView(
            sessionId = currentSessionId,
            roundId = currentRoundId,
            syncEpoch = currentSyncEpoch,
            cursor = cursor,
            sessionVersion = cursor,
            roundVersion = cursor,
            view = view,
            decision = decision,
            clocks = emptyList(),
            cheatPoints = listOf(0, 0, 0, 0),
            session = null,
            controls = emptyList(),
            paused = false,
            settlementRoundId = currentSettlementRoundId,
            settlementEvents = Collections.unmodifiableList(ArrayList(currentSettlementEvents)),
            settlementDetail = settlementDetail,
            preparation = null,
            presentationPreview = presentationPreview
        )
    }
}
