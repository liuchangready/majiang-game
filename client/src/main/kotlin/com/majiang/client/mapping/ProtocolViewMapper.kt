package com.majiang.client.mapping

import com.majiang.client.api.*

import com.majiang.server.api.RoomSummary
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionOption
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolBeamSwapAction
import com.majiang.transport.api.ProtocolBeamSwapCandidateView
import com.majiang.transport.api.ProtocolBeamSwapChoiceRequest
import com.majiang.transport.api.ProtocolBeamSwapConfig
import com.majiang.transport.api.ProtocolBeamSwapOfferView
import com.majiang.transport.api.ProtocolBeamSwapRequest
import com.majiang.transport.api.ProtocolBeamSwapSlot
import com.majiang.transport.api.ProtocolBeamSwapView
import com.majiang.transport.api.ProtocolBeamSwapWindow
import com.majiang.transport.api.ProtocolCheatEconomyConfig
import com.majiang.transport.api.ProtocolClock
import com.majiang.transport.api.ProtocolDoraBreakdown
import com.majiang.transport.api.ProtocolDoraIndicatorHit
import com.majiang.transport.api.ProtocolDrawReason
import com.majiang.transport.api.ProtocolFuBreakdown
import com.majiang.transport.api.ProtocolFuLine
import com.majiang.transport.api.ProtocolFuritenStatus
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolGamePhase
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolMatchConfig
import com.majiang.transport.api.ProtocolMeld
import com.majiang.transport.api.ProtocolMeldPreviewOption
import com.majiang.transport.api.ProtocolMeldType
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerPresentationPreview
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPlayerView
import com.majiang.transport.api.ProtocolPublicRoundRecord
import com.majiang.transport.api.ProtocolPublicSessionResult
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRiichiPreviewOption
import com.majiang.transport.api.ProtocolPaymentComponent
import com.majiang.transport.api.ProtocolPaymentPlan
import com.majiang.transport.api.ProtocolPaymentPlanValidator
import com.majiang.transport.api.ProtocolScoreBreakdown
import com.majiang.transport.api.ProtocolScoreCodes
import com.majiang.transport.api.ProtocolScoreResult
import com.majiang.transport.api.ProtocolYakuEntry
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.ProtocolRoomLifecycle
import com.majiang.transport.api.ProtocolRoomMember
import com.majiang.transport.api.ProtocolSeatControl
import com.majiang.transport.api.ProtocolSessionView
import com.majiang.transport.api.ProtocolTimeControlConfig
import com.majiang.transport.api.ProtocolShuffleConfig
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolShufflePreparation
import com.majiang.transport.api.ProtocolShufflePreparationStatus
import com.majiang.transport.api.ProtocolShuffleSelection
import com.majiang.transport.api.ProtocolShuffleTarget
import com.majiang.transport.api.ProtocolSuit
import com.majiang.transport.api.ProtocolTile
import com.majiang.server.api.PublicCheckpoint as ProtocolPublicCheckpoint
import com.majiang.transport.api.ProtocolPublicBoardView
import com.majiang.transport.api.ProtocolPublicMatchResult
import com.majiang.transport.api.ProtocolPublicScoreRecord
import com.majiang.transport.api.ProtocolPublicSessionSummary
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolRoundOutcome
import com.majiang.transport.api.ProtocolTileMarkConfig
import com.majiang.transport.api.ProtocolTileMarkRequest
import com.majiang.transport.api.ProtocolTileMarkArea
import com.majiang.transport.api.ProtocolRiverPosition
import com.majiang.transport.api.ProtocolMarkedTileView
import com.majiang.transport.api.ProtocolTileMarkGroupView
import com.majiang.transport.api.ProtocolTileMarkView
import com.majiang.transport.api.ProtocolSleeveBloomConfig
import com.majiang.transport.api.ProtocolSleeveBloomDrawKind
import com.majiang.transport.api.ProtocolSleeveBloomKeep
import com.majiang.transport.api.ProtocolSleeveBloomOfferView
import com.majiang.transport.api.ProtocolSleeveBloomRequest
import com.majiang.transport.api.ProtocolSleeveBloomChoiceRequest
import com.majiang.transport.api.ProtocolSleeveBloomView
import com.majiang.transport.api.validatePublicGameEvent
import java.util.Collections
import java.util.LinkedHashMap
import java.util.LinkedHashSet

/**
 * 传输层独立协议 DTO 与表现层 ViewModel / ClientModel 双向映射器 (ProtocolViewMapper)
 *
 * 核心架构约束：
 * 1. 纯协议 DTO 与纯 ViewModel / ClientModel 互转，严禁持有或泄露 engine 领域模型；
 * 2. 映射保真：0..135 全物理编码、赤五标记、23 种游戏事件、14 种合法动作、13 维配置严格保真。
 */
object ProtocolViewMapper {

    // ==========================================
    // 基础枚举映射
    // ==========================================

    fun mapSuit(suit: ProtocolSuit): SuitView = when (suit) {
        ProtocolSuit.MAN -> SuitView.MAN
        ProtocolSuit.PIN -> SuitView.PIN
        ProtocolSuit.SOU -> SuitView.SOU
        ProtocolSuit.WIND -> SuitView.WIND
        ProtocolSuit.DRAGON -> SuitView.DRAGON
    }

    fun toProtocolSuit(suit: SuitView): ProtocolSuit = when (suit) {
        SuitView.MAN -> ProtocolSuit.MAN
        SuitView.PIN -> ProtocolSuit.PIN
        SuitView.SOU -> ProtocolSuit.SOU
        SuitView.WIND -> ProtocolSuit.WIND
        SuitView.DRAGON -> ProtocolSuit.DRAGON
    }

    fun mapMeldType(type: ProtocolMeldType): MeldTypeView = when (type) {
        ProtocolMeldType.CHI -> MeldTypeView.CHI
        ProtocolMeldType.PON -> MeldTypeView.PON
        ProtocolMeldType.OPEN_KAN -> MeldTypeView.OPEN_KAN
        ProtocolMeldType.CLOSED_KAN -> MeldTypeView.CLOSED_KAN
        ProtocolMeldType.ADDED_KAN -> MeldTypeView.ADDED_KAN
    }

    fun toProtocolMeldType(type: MeldTypeView): ProtocolMeldType = when (type) {
        MeldTypeView.CHI -> ProtocolMeldType.CHI
        MeldTypeView.PON -> ProtocolMeldType.PON
        MeldTypeView.OPEN_KAN -> ProtocolMeldType.OPEN_KAN
        MeldTypeView.CLOSED_KAN -> ProtocolMeldType.CLOSED_KAN
        MeldTypeView.ADDED_KAN -> ProtocolMeldType.ADDED_KAN
    }

    fun mapGamePhase(phase: ProtocolGamePhase): GamePhaseView = when (phase) {
        ProtocolGamePhase.STACKING -> GamePhaseView.STACKING
        ProtocolGamePhase.DEALING -> GamePhaseView.DEALING
        ProtocolGamePhase.DRAW -> GamePhaseView.DRAW
        ProtocolGamePhase.CHEAT_WINDOW -> GamePhaseView.CHEAT_WINDOW
        ProtocolGamePhase.DISCARD -> GamePhaseView.DISCARD
        ProtocolGamePhase.RESPONSE_WINDOW -> GamePhaseView.RESPONSE_WINDOW
        ProtocolGamePhase.CHANKAN_WINDOW -> GamePhaseView.CHANKAN_WINDOW
        ProtocolGamePhase.KAN_DRAW -> GamePhaseView.KAN_DRAW
        ProtocolGamePhase.ROUND_END -> GamePhaseView.ROUND_END
        ProtocolGamePhase.MATCH_END -> GamePhaseView.MATCH_END
    }

    fun toProtocolGamePhase(phase: GamePhaseView): ProtocolGamePhase = when (phase) {
        GamePhaseView.STACKING -> ProtocolGamePhase.STACKING
        GamePhaseView.DEALING -> ProtocolGamePhase.DEALING
        GamePhaseView.DRAW -> ProtocolGamePhase.DRAW
        GamePhaseView.CHEAT_WINDOW -> ProtocolGamePhase.CHEAT_WINDOW
        GamePhaseView.DISCARD -> ProtocolGamePhase.DISCARD
        GamePhaseView.RESPONSE_WINDOW -> ProtocolGamePhase.RESPONSE_WINDOW
        GamePhaseView.CHANKAN_WINDOW -> ProtocolGamePhase.CHANKAN_WINDOW
        GamePhaseView.KAN_DRAW -> ProtocolGamePhase.KAN_DRAW
        GamePhaseView.ROUND_END -> ProtocolGamePhase.ROUND_END
        GamePhaseView.MATCH_END -> ProtocolGamePhase.MATCH_END
    }

    fun mapDrawReason(reason: ProtocolDrawReason): DrawReasonView = when (reason) {
        ProtocolDrawReason.EXHAUSTIVE -> DrawReasonView.EXHAUSTIVE
        ProtocolDrawReason.FOUR_WINDS -> DrawReasonView.FOUR_WINDS
        ProtocolDrawReason.FOUR_KANS -> DrawReasonView.FOUR_KANS
        ProtocolDrawReason.FOUR_RIICHI -> DrawReasonView.FOUR_RIICHI
        ProtocolDrawReason.NINE_TERMINALS -> DrawReasonView.NINE_TERMINALS
        ProtocolDrawReason.TRIPLE_RON -> DrawReasonView.TRIPLE_RON
    }

    fun toProtocolDrawReason(reason: DrawReasonView): ProtocolDrawReason = when (reason) {
        DrawReasonView.EXHAUSTIVE -> ProtocolDrawReason.EXHAUSTIVE
        DrawReasonView.FOUR_WINDS -> ProtocolDrawReason.FOUR_WINDS
        DrawReasonView.FOUR_KANS -> ProtocolDrawReason.FOUR_KANS
        DrawReasonView.FOUR_RIICHI -> ProtocolDrawReason.FOUR_RIICHI
        DrawReasonView.NINE_TERMINALS -> ProtocolDrawReason.NINE_TERMINALS
        DrawReasonView.TRIPLE_RON -> ProtocolDrawReason.TRIPLE_RON
    }

    // ==========================================
    // 麻将牌与副露映射
    // ==========================================

    fun mapTile(tile: ProtocolTile): TileView =
        TileView.fromTileId(tile.tileId)

    fun toProtocolTile(tile: TileView): ProtocolTile {
        val expected = ProtocolTile.fromTileId(tile.tileId)
        require(toProtocolSuit(tile.suit) == expected.suit && tile.rank == expected.rank) {
            "Contradictory TileView for tileId ${tile.tileId}: suit=${tile.suit}, rank=${tile.rank}, expected suit=${expected.suit}, rank=${expected.rank}"
        }
        return expected
    }

    fun mapTiles(tiles: List<ProtocolTile>): List<TileView> =
        Collections.unmodifiableList(tiles.map { mapTile(it) })

    fun toProtocolTiles(tiles: List<TileView>): List<ProtocolTile> =
        Collections.unmodifiableList(tiles.map { toProtocolTile(it) })

    fun mapMeld(meld: ProtocolMeld): MeldView = MeldView(
        type = mapMeldType(meld.type),
        tiles = mapTiles(meld.tiles),
        fromPlayer = meld.fromPlayer,
        calledTile = meld.calledTile?.let { mapTile(it) } ?: mapTile(meld.tiles.first())
    )

    fun toProtocolMeld(meld: MeldView): ProtocolMeld = ProtocolMeld(
        type = toProtocolMeldType(meld.type),
        tiles = toProtocolTiles(meld.tiles),
        fromPlayer = meld.fromPlayer,
        calledTile = toProtocolTile(meld.calledTile)
    )

    // ==========================================
    // 振听状态与合法动作
    // ==========================================

    fun mapFuriten(f: ProtocolFuritenStatus): FuritenStatusView = FuritenStatusView(
        discard = f.discard,
        temporary = f.temporary,
        riichi = f.riichi
    )

    fun toProtocolFuriten(f: FuritenStatusView): ProtocolFuritenStatus = ProtocolFuritenStatus(
        discard = f.discard,
        temporary = f.temporary,
        riichi = f.riichi
    )

    fun mapLegalAction(action: ProtocolLegalAction): LegalActionView = when (action) {
        is ProtocolLegalAction.StartStacking -> LegalActionView.StartStacking(action.stackingPlans)
        is ProtocolLegalAction.DealComplete -> LegalActionView.DealComplete
        is ProtocolLegalAction.DrawTile -> LegalActionView.DrawTile(action.player)
        is ProtocolLegalAction.DiscardTile -> LegalActionView.DiscardTile(action.player, mapTile(action.tile))
        is ProtocolLegalAction.DeclareChi -> LegalActionView.DeclareChi(action.player, mapMeld(action.meld))
        is ProtocolLegalAction.DeclarePon -> LegalActionView.DeclarePon(action.player, mapTiles(action.tiles))
        is ProtocolLegalAction.DeclareKan -> LegalActionView.DeclareKan(action.player, mapTiles(action.tiles), mapMeldType(action.kanType))
        is ProtocolLegalAction.DeclareRiichi -> LegalActionView.DeclareRiichi(action.player, mapTile(action.discardTile))
        is ProtocolLegalAction.DeclareTsumo -> LegalActionView.DeclareTsumo(action.player)
        is ProtocolLegalAction.DeclareRon -> LegalActionView.DeclareRon(action.player)
        is ProtocolLegalAction.Pass -> LegalActionView.Pass(action.player)
        is ProtocolLegalAction.DeclareAbortiveDraw -> LegalActionView.DeclareAbortiveDraw(action.player)
        is ProtocolLegalAction.DeclareAgariYame -> LegalActionView.DeclareAgariYame(action.player)
    }

    fun toProtocolLegalAction(action: LegalActionView): ProtocolLegalAction = when (action) {
        is LegalActionView.StartStacking -> {
            require(action.stackingPlans.isEmpty()) { "Legacy StartStacking nonempty plans are unsupported: ${action.stackingPlans}" }
            ProtocolLegalAction.StartStacking(emptyList())
        }
        is LegalActionView.DealComplete -> ProtocolLegalAction.DealComplete
        is LegalActionView.DrawTile -> ProtocolLegalAction.DrawTile(action.player)
        is LegalActionView.DiscardTile -> ProtocolLegalAction.DiscardTile(action.player, toProtocolTile(action.tile))
        is LegalActionView.DeclareChi -> ProtocolLegalAction.DeclareChi(action.player, toProtocolMeld(action.meld))
        is LegalActionView.DeclarePon -> ProtocolLegalAction.DeclarePon(action.player, toProtocolTiles(action.tiles))
        is LegalActionView.DeclareKan -> ProtocolLegalAction.DeclareKan(action.player, toProtocolTiles(action.tiles), toProtocolMeldType(action.kanType))
        is LegalActionView.DeclareRiichi -> ProtocolLegalAction.DeclareRiichi(action.player, toProtocolTile(action.discardTile))
        is LegalActionView.DeclareTsumo -> ProtocolLegalAction.DeclareTsumo(action.player)
        is LegalActionView.DeclareRon -> ProtocolLegalAction.DeclareRon(action.player)
        is LegalActionView.Pass -> ProtocolLegalAction.Pass(action.player)
        is LegalActionView.DeclareAbortiveDraw -> ProtocolLegalAction.DeclareAbortiveDraw(action.player)
        is LegalActionView.DeclareAgariYame -> ProtocolLegalAction.DeclareAgariYame(action.player)
    }

    fun mapActionOption(option: ProtocolActionOption): ActionOptionView = ActionOptionView(
        optionId = option.optionId,
        action = mapLegalAction(option.action)
    )

    fun mapDecision(dec: ProtocolPlayerDecision): DecisionView = DecisionView(
        roundId = dec.roundId,
        decisionId = dec.decisionId,
        controlEpoch = dec.controlEpoch,
        actions = Collections.unmodifiableList(dec.options.map { mapLegalAction(it.action) }),
        options = Collections.unmodifiableList(dec.options.map { mapActionOption(it) })
    )

    // ==========================================
    // 时钟、席位控制、公开结算记录与进度
    // ==========================================

    fun mapClock(clock: ProtocolClock): ClockInfo = ClockInfo(
        seat = clock.seat,
        opensAt = clock.opensAt,
        deadline = clock.deadline,
        reserve = clock.reserve,
        serverNow = clock.serverNow
    )

    fun mapSeatControl(control: ProtocolSeatControl): ControlsInfo = ControlsInfo(
        seat = control.seat,
        connected = control.connected,
        role = control.role,
        controlEpoch = control.controlEpoch
    )

    fun mapPublicRoundRecord(rec: ProtocolPublicRoundRecord): PublicRoundRecordView = PublicRoundRecordView(
        roundId = rec.roundId,
        resultId = rec.resultId,
        finalScores = Collections.unmodifiableList(ArrayList(rec.finalScores)),
        deltas = Collections.unmodifiableList(ArrayList(rec.deltas)),
        finalCheatPoints = Collections.unmodifiableList(ArrayList(rec.finalCheatPoints)),
        cheatDeltas = Collections.unmodifiableList(ArrayList(rec.cheatDeltas))
    )

    fun mapPublicSessionResult(res: ProtocolPublicSessionResult): PublicSessionResultView = PublicSessionResultView(
        finalScores = Collections.unmodifiableList(ArrayList(res.finalScores)),
        rankedSeats = Collections.unmodifiableList(ArrayList(res.rankedSeats)),
        reason = res.reason,
        finalCheatPoints = Collections.unmodifiableList(ArrayList(res.finalCheatPoints))
    )

    fun mapSessionProgress(session: ProtocolSessionView): SessionProgressView = SessionProgressView(
        lifecycle = session.lifecycle,
        scores = Collections.unmodifiableList(ArrayList(session.scores)),
        roundWind = session.roundWind,
        roundNumber = session.roundNumber,
        dealerSeat = session.dealerSeat,
        honba = session.honba,
        roundRecords = Collections.unmodifiableList(session.roundRecords.map { mapPublicRoundRecord(it) }),
        finalResult = session.finalResult?.let { mapPublicSessionResult(it) }
    )

    // ==========================================
    // 算分、符数与宝牌分解
    // ==========================================

    fun mapFuLine(line: ProtocolFuLine): FuLineView = FuLineView(
        description = line.description,
        fu = line.fu
    )

    fun mapFuBreakdown(fu: ProtocolFuBreakdown): FuBreakdownView = FuBreakdownView(
        lines = Collections.unmodifiableList(fu.lines.map { mapFuLine(it) }),
        subtotal = fu.subtotal,
        roundedUpTo = fu.roundedUpTo,
        finalFu = fu.finalFu,
        specialNote = fu.specialNote
    )

    fun mapDoraIndicatorHit(hit: ProtocolDoraIndicatorHit): DoraIndicatorHitView = DoraIndicatorHitView(
        indicator = mapTile(hit.indicator),
        target = mapTile(hit.target),
        tiles = mapTiles(hit.tiles)
    )

    fun mapDoraBreakdown(dora: ProtocolDoraBreakdown): DoraBreakdownView = DoraBreakdownView(
        indicatorCount = dora.indicatorCount,
        uraCount = dora.uraCount,
        redCount = dora.redCount,
        totalHan = dora.totalHan,
        indicatorTiles = mapTiles(dora.indicatorTiles),
        uraTiles = mapTiles(dora.uraTiles),
        redTiles = mapTiles(dora.redTiles),
        indicatorHits = Collections.unmodifiableList(dora.indicatorHits.map { mapDoraIndicatorHit(it) }),
        uraHits = Collections.unmodifiableList(dora.uraHits.map { mapDoraIndicatorHit(it) })
    )

    fun mapScoreResult(score: ProtocolScoreResult): ScoreResultView = ScoreResultView(
        fu = score.fu,
        han = score.han,
        basePoints = score.basePoints,
        title = score.title,
        isYakuman = score.isYakuman,
        yakumanMultiplier = score.yakumanMultiplier,
        rawBasePoints = score.rawBasePoints,
        capReason = score.capReason,
        formulaText = score.formulaText,
        rawBasePointsExact = score.rawBasePointsExact,
        // 未知封顶码不进入视图，展示层自然回退 capReason -> title。
        capCode = score.capCode?.takeIf { ProtocolScoreCodes.isKnownCapCode(it) }
    )

    /**
     * 只把与所有权威字段一致的支付计划放进视图。
     *
     * 必须同时满足：已知 kind/payerRole、四种冻结节形态、角色/人数、
     * 金额与 basePoints 的取整公式、`totalPoints = total + honba + riichi`，
     * 以及与旧 `paymentLines` 的逐字金额一致。任何不一致返回 null，
     * 展示层回退旧 `paymentLines`；未知可扩展码不被当作整条快照损坏。
     */
    fun mapPaymentPlan(
        plan: ProtocolPaymentPlan,
        score: ProtocolScoreResult,
        paymentLines: List<String>,
        honbaAddition: Int,
        riichiAddition: Int,
        totalPoints: Int,
        isDealer: Boolean,
        isTsumo: Boolean
    ): PaymentPlanView? {
        if (!ProtocolPaymentPlanValidator.isConsistent(
                plan = plan,
                score = score,
                paymentLines = paymentLines,
                honbaAddition = honbaAddition,
                riichiAddition = riichiAddition,
                totalPoints = totalPoints,
                isDealer = isDealer,
                isTsumo = isTsumo
            )
        ) {
            return null
        }
        val components = ArrayList<PaymentComponentView>(plan.components.size)
        for (component in plan.components) {
            components.add(
                PaymentComponentView(
                    payerRole = component.payerRole,
                    amount = component.amount,
                    count = component.count
                )
            )
        }
        return PaymentPlanView(plan.kind, components, plan.total)
    }

    fun mapScoreBreakdown(sb: ProtocolScoreBreakdown): ScoreBreakdownView = ScoreBreakdownView(
        yakuLines = Collections.unmodifiableList(ArrayList(sb.yakuLines)),
        yakumanCount = sb.yakumanCount,
        dora = mapDoraBreakdown(sb.dora),
        fu = mapFuBreakdown(sb.fu),
        score = mapScoreResult(sb.score),
        paymentLines = Collections.unmodifiableList(ArrayList(sb.paymentLines)),
        honbaAddition = sb.honbaAddition,
        riichiAddition = sb.riichiAddition,
        totalPoints = sb.totalPoints,
        uraEligible = sb.uraEligible,
        isDealer = sb.isDealer,
        isTsumo = sb.isTsumo,
        yakuEntries = Collections.unmodifiableList(
            sb.yakuEntries.map { YakuEntryView(it.code, it.displayName, it.han) }
        ),
        paymentPlan = sb.paymentPlan?.let {
            mapPaymentPlan(
                plan = it,
                score = sb.score,
                paymentLines = sb.paymentLines,
                honbaAddition = sb.honbaAddition,
                riichiAddition = sb.riichiAddition,
                totalPoints = sb.totalPoints,
                isDealer = sb.isDealer,
                isTsumo = sb.isTsumo
            )
        }
    )

    // ==========================================
    // 游戏事件映射 (23 种全部覆盖)
    // ==========================================

    fun mapGameEvent(event: ProtocolGameEvent): GameEventView = when (event) {
        is ProtocolGameEvent.StackingStarted -> GameEventView.StackingStarted(event.turnNumber)
        is ProtocolGameEvent.WallBuilt -> GameEventView.WallBuilt(event.turnNumber, event.wallSize)
        is ProtocolGameEvent.TileDealt -> GameEventView.TileDealt(event.turnNumber, event.player, mapTile(event.tile))
        is ProtocolGameEvent.DealingComplete -> GameEventView.DealingComplete(event.turnNumber)
        is ProtocolGameEvent.TileDrawn -> GameEventView.TileDrawn(event.turnNumber, event.player, mapTile(event.tile), event.wallRemaining)
        is ProtocolGameEvent.TileDiscarded -> GameEventView.TileDiscarded(
            event.turnNumber, event.player, mapTile(event.tile), event.isRiichiDiscard, event.isTsumogiri
        )
        is ProtocolGameEvent.ChiDeclared -> GameEventView.ChiDeclared(event.turnNumber, event.player, mapMeld(event.meld), event.fromPlayer)
        is ProtocolGameEvent.PonDeclared -> GameEventView.PonDeclared(event.turnNumber, event.player, mapMeld(event.meld), event.fromPlayer)
        is ProtocolGameEvent.KanDeclared -> GameEventView.KanDeclared(event.turnNumber, event.player, mapMeld(event.meld), mapMeldType(event.kanType))
        is ProtocolGameEvent.RinshanDraw -> GameEventView.RinshanDraw(event.turnNumber, event.player, mapTile(event.tile))
        is ProtocolGameEvent.RiichiDeclared -> GameEventView.RiichiDeclared(event.turnNumber, event.player, mapTile(event.discardTile), event.isDoubleRiichi)
        is ProtocolGameEvent.TsumoWin -> GameEventView.TsumoWin(
            event.turnNumber, event.player, mapTile(event.winTile), Collections.unmodifiableList(ArrayList(event.yakuList)),
            event.han, event.fu, event.score, event.breakdown?.let { mapScoreBreakdown(it) }
        )
        is ProtocolGameEvent.RonWin -> GameEventView.RonWin(
            event.turnNumber, event.winner, event.loser, mapTile(event.winTile), Collections.unmodifiableList(ArrayList(event.yakuList)),
            event.han, event.fu, event.score, event.breakdown?.let { mapScoreBreakdown(it) }
        )
        is ProtocolGameEvent.DoraRevealed -> GameEventView.DoraRevealed(event.turnNumber, mapTile(event.indicator), event.isUraDora)
        is ProtocolGameEvent.CheatPointsChanged -> GameEventView.CheatPointsChanged(event.turnNumber, event.seat, event.amount, event.newBalance)
        is ProtocolGameEvent.DrawGame -> GameEventView.DrawGame(event.turnNumber, mapDrawReason(event.reason), Collections.unmodifiableList(ArrayList(event.tenpaiPlayers)))
        is ProtocolGameEvent.ScoreSettled -> GameEventView.ScoreSettled(
            event.turnNumber,
            Collections.unmodifiableMap(LinkedHashMap(event.payments)),
            Collections.unmodifiableList(ArrayList(event.newScores))
        )
        is ProtocolGameEvent.RoundEnded -> GameEventView.RoundEnded(event.turnNumber, event.roundWind, event.roundNumber)
        is ProtocolGameEvent.MatchEnded -> GameEventView.MatchEnded(event.turnNumber, Collections.unmodifiableList(ArrayList(event.finalScores)), event.winner)
    }

    fun mapEvents(events: List<ProtocolGameEvent>): List<GameEventView> =
        Collections.unmodifiableList(events.map { mapGameEvent(it) })

    // ==========================================
    // 洗牌千术相关映射
    // ==========================================

    fun mapShuffleTargetView(target: ProtocolShuffleTarget): ShuffleTargetView = when (target) {
        ProtocolShuffleTarget.CHINITSU_MAN -> ShuffleTargetView.CHINITSU_MAN
        ProtocolShuffleTarget.CHINITSU_SOU -> ShuffleTargetView.CHINITSU_SOU
        ProtocolShuffleTarget.CHINITSU_PIN -> ShuffleTargetView.CHINITSU_PIN
        ProtocolShuffleTarget.DAISANGEN -> ShuffleTargetView.DAISANGEN
        ProtocolShuffleTarget.KOKUSHI -> ShuffleTargetView.KOKUSHI
        ProtocolShuffleTarget.DAISUUSHII -> ShuffleTargetView.DAISUUSHII
    }

    fun mapShufflePreparationStatus(status: ProtocolShufflePreparationStatus): ShufflePreparationStatusView = when (status) {
        ProtocolShufflePreparationStatus.SELECTING -> ShufflePreparationStatusView.SELECTING
        ProtocolShufflePreparationStatus.GENERATING -> ShufflePreparationStatusView.GENERATING
    }

    fun mapShuffleSelectionView(s: ProtocolShuffleSelection): ShuffleSelectionView = ShuffleSelectionView(
        initialTargets = s.initialTargets.map { mapShuffleTargetView(it) }.toSet(),
        drawTargets = s.drawTargets.map { mapShuffleTargetView(it) }.toSet()
    )

    fun mapShufflePreparation(prep: ProtocolShufflePreparation): ShufflePreparationView = ShufflePreparationView(
        roundId = prep.roundId,
        status = mapShufflePreparationStatus(prep.status),
        opensAtMillis = prep.opensAtMillis,
        deadlineMillis = prep.deadlineMillis,
        serverNowMillis = prep.serverNowMillis,
        confirmed = prep.confirmed,
        selection = prep.selection?.let { mapShuffleSelectionView(it) },
        reservedPoints = prep.reservedPoints,
        availablePoints = prep.availablePoints
    )

    // ==========================================
    // 玩家视图与快照聚合根映射
    // ==========================================

    fun projectSettlementDetail(events: List<ProtocolGameEvent>): SettlementDetailView? =
        PublicSettlementDetailProjection.from(events.map { mapGameEvent(it) })

    fun mapPlayerView(view: ProtocolPlayerView): PlayerView = PlayerView(
        viewerSeat = view.viewerSeat,
        phase = mapGamePhase(view.phase),
        roundWind = view.roundWind,
        roundNumber = view.roundNumber,
        honbaSticks = view.honbaSticks,
        riichiSticks = view.riichiSticks,
        dealerSeat = view.dealerSeat,
        currentTurnPlayer = view.currentTurnPlayer,
        wallRemaining = view.wallRemaining,
        doraIndicators = mapTiles(view.doraIndicators),
        scores = Collections.unmodifiableList(ArrayList(view.scores)),
        riichiStatus = Collections.unmodifiableList(ArrayList(view.riichiStatus)),
        discards = Collections.unmodifiableList(view.discards.map { Collections.unmodifiableList(mapTiles(it)) }),
        melds = Collections.unmodifiableList(view.melds.map { list -> Collections.unmodifiableList(list.map { mapMeld(it) }) }),
        handTileCounts = Collections.unmodifiableList(ArrayList(view.handTileCounts)),
        myClosedTiles = mapTiles(view.myClosedTiles),
        myDrawnTile = view.myDrawnTile?.let { mapTile(it) },
        legalActions = Collections.unmodifiableList(view.legalActions.map { mapLegalAction(it) }),
        lastDiscard = view.lastDiscard?.let { mapTile(it) },
        lastDiscardPlayer = view.lastDiscardPlayer,
        mySleeveTiles = mapTiles(view.mySleeveTiles),
        myFuriten = mapFuriten(view.myFuriten),
        myDiscardHistory = mapTiles(view.myDiscardHistory)
    )

    // ==========================================
    // 展示预览映射 (Presentation Preview)
    // ==========================================

    fun mapRiichiPreviewOption(option: ProtocolRiichiPreviewOption): RiichiPreviewOptionView = RiichiPreviewOptionView(
        optionId = option.optionId,
        action = mapLegalAction(option.action) as LegalActionView.DeclareRiichi,
        waits = Collections.unmodifiableList(mapTiles(option.waits)),
        furiten = mapFuriten(option.furiten)
    )

    fun toProtocolRiichiPreviewOption(option: RiichiPreviewOptionView): ProtocolRiichiPreviewOption = ProtocolRiichiPreviewOption(
        optionId = option.optionId,
        action = toProtocolLegalAction(option.action) as ProtocolLegalAction.DeclareRiichi,
        waits = Collections.unmodifiableList(toProtocolTiles(option.waits)),
        furiten = toProtocolFuriten(option.furiten)
    )

    fun mapMeldPreviewOption(option: ProtocolMeldPreviewOption): MeldPreviewOptionView = MeldPreviewOptionView(
        optionId = option.optionId,
        action = mapLegalAction(option.action),
        type = mapMeldType(option.type),
        tiles = Collections.unmodifiableList(mapTiles(option.tiles)),
        handTiles = Collections.unmodifiableList(mapTiles(option.handTiles)),
        calledTile = option.calledTile?.let { mapTile(it) },
        addedTile = option.addedTile?.let { mapTile(it) },
        fromPlayer = option.fromPlayer
    )

    fun toProtocolMeldPreviewOption(option: MeldPreviewOptionView): ProtocolMeldPreviewOption = ProtocolMeldPreviewOption(
        optionId = option.optionId,
        action = toProtocolLegalAction(option.action),
        type = toProtocolMeldType(option.type),
        tiles = Collections.unmodifiableList(toProtocolTiles(option.tiles)),
        handTiles = Collections.unmodifiableList(toProtocolTiles(option.handTiles)),
        calledTile = option.calledTile?.let { toProtocolTile(it) },
        addedTile = option.addedTile?.let { toProtocolTile(it) },
        fromPlayer = option.fromPlayer
    )

    fun mapPresentationPreview(preview: ProtocolPlayerPresentationPreview): PlayerPresentationPreviewView = PlayerPresentationPreviewView(
        roundId = preview.roundId,
        decisionId = preview.decisionId,
        controlEpoch = preview.controlEpoch,
        waits = Collections.unmodifiableList(mapTiles(preview.waits)),
        riichiOptions = Collections.unmodifiableList(preview.riichiOptions.map { mapRiichiPreviewOption(it) }),
        meldOptions = Collections.unmodifiableList(preview.meldOptions.map { mapMeldPreviewOption(it) })
    )

    fun toProtocolPresentationPreview(preview: PlayerPresentationPreviewView): ProtocolPlayerPresentationPreview = ProtocolPlayerPresentationPreview(
        roundId = preview.roundId,
        decisionId = preview.decisionId,
        controlEpoch = preview.controlEpoch,
        waits = Collections.unmodifiableList(toProtocolTiles(preview.waits)),
        riichiOptions = Collections.unmodifiableList(preview.riichiOptions.map { toProtocolRiichiPreviewOption(it) }),
        meldOptions = Collections.unmodifiableList(preview.meldOptions.map { toProtocolMeldPreviewOption(it) })
    )

    fun mapSnapshot(snap: ProtocolPlayerSnapshot): GameSnapshotView = GameSnapshotView(
        sessionId = snap.sessionId,
        roundId = snap.roundId,
        syncEpoch = snap.syncEpoch,
        cursor = snap.cursor,
        sessionVersion = snap.sessionVersion,
        roundVersion = snap.roundVersion,
        view = mapPlayerView(snap.view),
        decision = mapDecision(snap.decision),
        clocks = Collections.unmodifiableList(snap.clocks.map { mapClock(it) }),
        cheatPoints = Collections.unmodifiableList(ArrayList(snap.cheatPoints)),
        session = snap.session?.let { mapSessionProgress(it) },
        controls = Collections.unmodifiableList(snap.controls.map { mapSeatControl(it) }),
        paused = snap.paused,
        settlementRoundId = snap.settlementRoundId,
        settlementEvents = Collections.unmodifiableList(snap.settlementEvents.map { mapGameEvent(it) }),
        settlementDetail = projectSettlementDetail(snap.settlementEvents),
        preparation = snap.preparation?.let { mapShufflePreparation(it) },
        presentationPreview = snap.presentationPreview?.let { mapPresentationPreview(it) },
        tileMark = snap.tileMark?.let { mapTileMark(it) },
        sleeveBloom = snap.sleeveBloom?.let { mapSleeveBloom(it) },
        beamSwap = snap.beamSwap?.let { mapBeamSwap(it) }
    )

    fun mapUpdate(update: ProtocolPlayerUpdate): GameUpdateView = GameUpdateView(
        snapshot = mapSnapshot(update.snapshot),
        fromCursor = update.fromCursor,
        visibleEvents = Collections.unmodifiableList(update.visibleEvents.map { mapGameEvent(it) }),
        isFullSnapshot = update.isFullSnapshot
    )

    // ==========================================
    // ClientModels 转换
    // ==========================================

    fun toReceiptStatus(status: ProtocolReceiptStatus): ReceiptStatus = when (status) {
        ProtocolReceiptStatus.APPLIED -> ReceiptStatus.APPLIED
        ProtocolReceiptStatus.RECORDED -> ReceiptStatus.RECORDED
        ProtocolReceiptStatus.REJECTED -> ReceiptStatus.REJECTED
    }

    fun toProtocolReceiptStatus(status: ReceiptStatus): ProtocolReceiptStatus = when (status) {
        ReceiptStatus.APPLIED -> ProtocolReceiptStatus.APPLIED
        ReceiptStatus.RECORDED -> ProtocolReceiptStatus.RECORDED
        ReceiptStatus.REJECTED -> ProtocolReceiptStatus.REJECTED
    }

    fun mapReceipt(receipt: ProtocolActionReceipt): Receipt = Receipt(
        requestId = receipt.requestId,
        status = toReceiptStatus(receipt.status),
        cursor = receipt.cursor,
        reason = receipt.reason
    )

    fun toProtocolActionChoice(choice: ActionChoice): ProtocolActionChoice = ProtocolActionChoice(
        requestId = choice.requestId,
        roundId = choice.roundId,
        decisionId = choice.decisionId,
        controlEpoch = choice.controlEpoch,
        optionId = choice.optionId
    )

    fun mapShuffleTarget(target: ProtocolShuffleTarget): ShuffleTarget = when (target) {
        ProtocolShuffleTarget.CHINITSU_MAN -> ShuffleTarget.CHINITSU_MAN
        ProtocolShuffleTarget.CHINITSU_SOU -> ShuffleTarget.CHINITSU_SOU
        ProtocolShuffleTarget.CHINITSU_PIN -> ShuffleTarget.CHINITSU_PIN
        ProtocolShuffleTarget.DAISANGEN -> ShuffleTarget.DAISANGEN
        ProtocolShuffleTarget.KOKUSHI -> ShuffleTarget.KOKUSHI
        ProtocolShuffleTarget.DAISUUSHII -> ShuffleTarget.DAISUUSHII
    }

    fun toProtocolShuffleTarget(target: ShuffleTarget): ProtocolShuffleTarget = when (target) {
        ShuffleTarget.CHINITSU_MAN -> ProtocolShuffleTarget.CHINITSU_MAN
        ShuffleTarget.CHINITSU_SOU -> ProtocolShuffleTarget.CHINITSU_SOU
        ShuffleTarget.CHINITSU_PIN -> ProtocolShuffleTarget.CHINITSU_PIN
        ShuffleTarget.DAISANGEN -> ProtocolShuffleTarget.DAISANGEN
        ShuffleTarget.KOKUSHI -> ProtocolShuffleTarget.KOKUSHI
        ShuffleTarget.DAISUUSHII -> ProtocolShuffleTarget.DAISUUSHII
    }

    fun mapShuffleSelection(selection: ProtocolShuffleSelection): ShuffleSelection = ShuffleSelection(
        initialTargets = selection.initialTargets.map { mapShuffleTarget(it) }.toSet(),
        drawTargets = selection.drawTargets.map { mapShuffleTarget(it) }.toSet()
    )

    fun toProtocolShuffleSelection(selection: ShuffleSelection): ProtocolShuffleSelection = ProtocolShuffleSelection(
        initialTargets = selection.initialTargets.map { toProtocolShuffleTarget(it) }.toSet(),
        drawTargets = selection.drawTargets.map { toProtocolShuffleTarget(it) }.toSet()
    )

    fun toProtocolShufflePlan(plan: ShufflePlan): ProtocolShufflePlan = ProtocolShufflePlan(
        requestId = plan.requestId,
        roundId = plan.roundId,
        controlEpoch = plan.controlEpoch,
        selection = toProtocolShuffleSelection(plan.selection)
    )

    fun mapCheatEconomyConfig(config: ProtocolCheatEconomyConfig): CheatEconomyConfig = CheatEconomyConfig(
        enabled = config.enabled,
        initialPoints = config.initialPoints,
        costs = config.costs
    )

    fun toProtocolCheatEconomyConfig(config: CheatEconomyConfig): ProtocolCheatEconomyConfig = ProtocolCheatEconomyConfig(
        enabled = config.enabled,
        initialPoints = config.initialPoints,
        costs = config.costs
    )

    fun mapShuffleConfig(config: ProtocolShuffleConfig): ShuffleConfig = ShuffleConfig(
        enabled = config.enabled,
        selectionMillis = config.selectionMillis,
        plannedDraws = config.plannedDraws,
        strength = config.strength,
        interference = config.interference,
        aiMaySpend = config.aiMaySpend
    )

    fun toProtocolShuffleConfig(config: ShuffleConfig): ProtocolShuffleConfig = ProtocolShuffleConfig(
        enabled = config.enabled,
        selectionMillis = config.selectionMillis,
        plannedDraws = config.plannedDraws,
        strength = config.strength,
        interference = config.interference,
        aiMaySpend = config.aiMaySpend
    )

    fun mapMatchConfig(config: ProtocolMatchConfig): MatchConfig = MatchConfig(
        playerCount = config.playerCount,
        handsPerWind = config.handsPerWind,
        winds = config.winds,
        startingScores = config.startingScores,
        allowExtension = config.allowExtension,
        allowAgariYame = config.allowAgariYame,
        bankruptcyEnabled = config.bankruptcyEnabled,
        bankruptcyThreshold = config.bankruptcyThreshold,
        bankruptcyInclusive = config.bankruptcyInclusive,
        targetScore = config.targetScore,
        tieBreakSeats = config.tieBreakSeats,
        cheatEconomy = mapCheatEconomyConfig(config.cheatEconomy),
        shuffleConfig = mapShuffleConfig(config.shuffleConfig),
        tileMarkConfig = mapTileMarkConfig(config.tileMarkConfig),
        sleeveBloomConfig = mapSleeveBloomConfig(config.sleeveBloomConfig),
        beamSwapConfig = mapBeamSwapConfig(config.beamSwapConfig)
    )

    fun toProtocolMatchConfig(config: MatchConfig): ProtocolMatchConfig = ProtocolMatchConfig(
        playerCount = config.playerCount,
        handsPerWind = config.handsPerWind,
        winds = config.winds,
        startingScores = config.startingScores,
        allowExtension = config.allowExtension,
        allowAgariYame = config.allowAgariYame,
        bankruptcyEnabled = config.bankruptcyEnabled,
        bankruptcyThreshold = config.bankruptcyThreshold,
        bankruptcyInclusive = config.bankruptcyInclusive,
        targetScore = config.targetScore,
        tieBreakSeats = config.tieBreakSeats,
        cheatEconomy = toProtocolCheatEconomyConfig(config.cheatEconomy),
        shuffleConfig = toProtocolShuffleConfig(config.shuffleConfig),
        tileMarkConfig = toProtocolTileMarkConfig(config.tileMarkConfig),
        sleeveBloomConfig = toProtocolSleeveBloomConfig(config.sleeveBloomConfig),
        beamSwapConfig = toProtocolBeamSwapConfig(config.beamSwapConfig)
    )

    fun mapCapabilities(caps: ProtocolAccessCapabilities): Capabilities = Capabilities(
        protocolVersion = caps.protocolVersion,
        supportedPlayerCounts = caps.supportedPlayerCounts,
        remoteTransport = caps.remoteTransport,
        persistence = caps.persistence,
        resumption = caps.resumption,
        tileMarkSupported = caps.tileMarkSupported,
        sleeveBloomSupported = caps.sleeveBloomSupported,
        beamSwapSupported = caps.beamSwapSupported,
        scoreDetailVersion = caps.scoreDetailVersion
    )

    fun toProtocolCapabilities(caps: Capabilities): ProtocolAccessCapabilities = ProtocolAccessCapabilities(
        protocolVersion = caps.protocolVersion,
        supportedPlayerCounts = caps.supportedPlayerCounts,
        remoteTransport = caps.remoteTransport,
        persistence = caps.persistence,
        resumption = caps.resumption,
        tileMarkSupported = caps.tileMarkSupported,
        sleeveBloomSupported = caps.sleeveBloomSupported,
        beamSwapSupported = caps.beamSwapSupported,
        scoreDetailVersion = caps.scoreDetailVersion
    )

    fun mapRoomSummary(room: RoomSummary): RoomSummaryView = RoomSummaryView(
        roomId = room.roomId,
        roomName = room.roomName,
        hostPlayerName = room.hostPlayerName,
        playerCount = room.playerCount,
        maxPlayers = room.maxPlayers,
        matchStarted = room.matchStarted,
        configSummary = room.configSummary
    )

    fun toProtocolRoomSummary(view: RoomSummaryView): RoomSummary = RoomSummary(
        roomId = view.roomId,
        roomName = view.roomName,
        hostPlayerName = view.hostPlayerName,
        playerCount = view.playerCount,
        maxPlayers = view.maxPlayers,
        matchStarted = view.matchStarted,
        configSummary = view.configSummary
    )

    fun mapRoomSummaries(rooms: List<RoomSummary>): List<RoomSummaryView> =
        Collections.unmodifiableList(rooms.map { mapRoomSummary(it) })

    // ==========================================
    // 房间模型映射 (Room Models)
    // ==========================================

    fun mapRoomLifecycle(proto: ProtocolRoomLifecycle): RoomLifecycle = when (proto) {
        ProtocolRoomLifecycle.LOBBY -> RoomLifecycle.LOBBY
        ProtocolRoomLifecycle.PREPARING -> RoomLifecycle.PREPARING
        ProtocolRoomLifecycle.PLAYING -> RoomLifecycle.PLAYING
        ProtocolRoomLifecycle.SETTLED -> RoomLifecycle.SETTLED
        ProtocolRoomLifecycle.FINISHED -> RoomLifecycle.FINISHED
        ProtocolRoomLifecycle.CLOSED -> RoomLifecycle.CLOSED
    }

    fun toProtocolRoomLifecycle(client: RoomLifecycle): ProtocolRoomLifecycle = when (client) {
        RoomLifecycle.LOBBY -> ProtocolRoomLifecycle.LOBBY
        RoomLifecycle.PREPARING -> ProtocolRoomLifecycle.PREPARING
        RoomLifecycle.PLAYING -> ProtocolRoomLifecycle.PLAYING
        RoomLifecycle.SETTLED -> ProtocolRoomLifecycle.SETTLED
        RoomLifecycle.FINISHED -> ProtocolRoomLifecycle.FINISHED
        RoomLifecycle.CLOSED -> ProtocolRoomLifecycle.CLOSED
    }

    fun mapRoomMember(proto: ProtocolRoomMember): RoomMember = RoomMember(
        seat = proto.seat,
        playerName = proto.playerName,
        isHost = proto.isHost,
        isReady = proto.isReady,
        role = proto.role
    )

    fun toProtocolRoomMember(client: RoomMember): ProtocolRoomMember = ProtocolRoomMember(
        seat = client.seat,
        playerName = client.playerName,
        isHost = client.isHost,
        isReady = client.isReady,
        role = client.role
    )

    fun mapTimeControl(proto: ProtocolTimeControlConfig): TimeControlConfig = TimeControlConfig(
        roundReserveSeconds = proto.roundReserveSeconds,
        turnThinkingSeconds = proto.turnThinkingSeconds
    )

    fun toProtocolTimeControl(client: TimeControlConfig): ProtocolTimeControlConfig = ProtocolTimeControlConfig(
        roundReserveSeconds = client.roundReserveSeconds,
        turnThinkingSeconds = client.turnThinkingSeconds
    )

    fun mapRoomConfig(proto: ProtocolRoomConfig): RoomConfig = RoomConfig(
        matchConfig = mapMatchConfig(proto.matchConfig),
        timeControl = mapTimeControl(proto.timeControl)
    )

    fun toProtocolRoomConfig(client: RoomConfig): ProtocolRoomConfig = ProtocolRoomConfig(
        matchConfig = toProtocolMatchConfig(client.matchConfig),
        timeControl = toProtocolTimeControl(client.timeControl)
    )

    fun mapRoomInfo(proto: ProtocolRoomInfo): RoomInfo = RoomInfo(
        roomId = proto.roomId,
        lifecycle = mapRoomLifecycle(proto.lifecycle),
        version = proto.version,
        hostSeat = proto.hostSeat,
        members = proto.members.map { mapRoomMember(it) },
        config = mapRoomConfig(proto.config)
    )

    fun toProtocolRoomInfo(client: RoomInfo): ProtocolRoomInfo = ProtocolRoomInfo(
        roomId = client.roomId,
        lifecycle = toProtocolRoomLifecycle(client.lifecycle),
        version = client.version,
        hostSeat = client.hostSeat,
        members = client.members.map { toProtocolRoomMember(it) },
        config = toProtocolRoomConfig(client.config)
    )

    // ==========================================
    // 时钟与反向计分分解映射
    // ==========================================

    fun toProtocolClock(clock: ClockInfo): ProtocolClock = ProtocolClock(
        seat = clock.seat,
        opensAt = clock.opensAt,
        deadline = clock.deadline,
        reserve = clock.reserve,
        serverNow = clock.serverNow
    )

    fun toProtocolFuLine(line: FuLineView): ProtocolFuLine = ProtocolFuLine(
        description = line.description,
        fu = line.fu
    )

    fun toProtocolFuBreakdown(fu: FuBreakdownView): ProtocolFuBreakdown = ProtocolFuBreakdown(
        lines = Collections.unmodifiableList(fu.lines.map { toProtocolFuLine(it) }),
        subtotal = fu.subtotal,
        roundedUpTo = fu.roundedUpTo,
        finalFu = fu.finalFu,
        specialNote = fu.specialNote
    )

    fun toProtocolDoraIndicatorHit(hit: DoraIndicatorHitView): ProtocolDoraIndicatorHit = ProtocolDoraIndicatorHit(
        indicator = toProtocolTile(hit.indicator),
        target = toProtocolTile(hit.target),
        tiles = toProtocolTiles(hit.tiles)
    )

    fun toProtocolDoraBreakdown(dora: DoraBreakdownView): ProtocolDoraBreakdown = ProtocolDoraBreakdown(
        indicatorCount = dora.indicatorCount,
        uraCount = dora.uraCount,
        redCount = dora.redCount,
        totalHan = dora.totalHan,
        indicatorTiles = toProtocolTiles(dora.indicatorTiles),
        uraTiles = toProtocolTiles(dora.uraTiles),
        redTiles = toProtocolTiles(dora.redTiles),
        indicatorHits = Collections.unmodifiableList(dora.indicatorHits.map { toProtocolDoraIndicatorHit(it) }),
        uraHits = Collections.unmodifiableList(dora.uraHits.map { toProtocolDoraIndicatorHit(it) })
    )

    fun toProtocolScoreResult(score: ScoreResultView): ProtocolScoreResult = ProtocolScoreResult(
        fu = score.fu,
        han = score.han,
        basePoints = score.basePoints,
        title = score.title,
        isYakuman = score.isYakuman,
        yakumanMultiplier = score.yakumanMultiplier,
        rawBasePoints = score.rawBasePoints,
        capReason = score.capReason,
        formulaText = score.formulaText,
        rawBasePointsExact = score.rawBasePointsExact,
        capCode = score.capCode
    )

    fun toProtocolPaymentPlan(plan: PaymentPlanView): ProtocolPaymentPlan = ProtocolPaymentPlan(
        kind = plan.kind,
        components = Collections.unmodifiableList(
            plan.components.map {
                ProtocolPaymentComponent(it.payerRole, it.amount, it.count)
            }
        ),
        total = plan.total
    )

    fun toProtocolScoreBreakdown(sb: ScoreBreakdownView): ProtocolScoreBreakdown = ProtocolScoreBreakdown(
        yakuLines = Collections.unmodifiableList(ArrayList(sb.yakuLines)),
        yakumanCount = sb.yakumanCount,
        dora = toProtocolDoraBreakdown(sb.dora),
        fu = toProtocolFuBreakdown(sb.fu),
        score = toProtocolScoreResult(sb.score),
        paymentLines = Collections.unmodifiableList(ArrayList(sb.paymentLines)),
        honbaAddition = sb.honbaAddition,
        riichiAddition = sb.riichiAddition,
        totalPoints = sb.totalPoints,
        uraEligible = sb.uraEligible,
        isDealer = sb.isDealer,
        isTsumo = sb.isTsumo,
        yakuEntries = Collections.unmodifiableList(
            sb.yakuEntries.map { ProtocolYakuEntry(it.code, it.displayName, it.han) }
        ),
        paymentPlan = sb.paymentPlan?.let { toProtocolPaymentPlan(it) }
    )

    fun toProtocolGameEvent(view: GameEventView): ProtocolGameEvent = when (view) {
        is GameEventView.StackingStarted -> ProtocolGameEvent.StackingStarted(view.turnNumber)
        is GameEventView.WallBuilt -> ProtocolGameEvent.WallBuilt(view.turnNumber, view.wallSize)
        is GameEventView.TileDealt -> ProtocolGameEvent.TileDealt(view.turnNumber, view.player, toProtocolTile(view.tile))
        is GameEventView.DealingComplete -> ProtocolGameEvent.DealingComplete(view.turnNumber)
        is GameEventView.TileDrawn -> ProtocolGameEvent.TileDrawn(view.turnNumber, view.player, toProtocolTile(view.tile), view.wallRemaining)
        is GameEventView.TileDiscarded -> ProtocolGameEvent.TileDiscarded(
            view.turnNumber, view.player, toProtocolTile(view.tile), view.isRiichiDiscard, view.isTsumogiri
        )
        is GameEventView.ChiDeclared -> ProtocolGameEvent.ChiDeclared(view.turnNumber, view.player, toProtocolMeld(view.meld), view.fromPlayer)
        is GameEventView.PonDeclared -> ProtocolGameEvent.PonDeclared(view.turnNumber, view.player, toProtocolMeld(view.meld), view.fromPlayer)
        is GameEventView.KanDeclared -> ProtocolGameEvent.KanDeclared(view.turnNumber, view.player, toProtocolMeld(view.meld), toProtocolMeldType(view.kanType))
        is GameEventView.RinshanDraw -> ProtocolGameEvent.RinshanDraw(view.turnNumber, view.player, toProtocolTile(view.tile))
        is GameEventView.RiichiDeclared -> ProtocolGameEvent.RiichiDeclared(view.turnNumber, view.player, toProtocolTile(view.discardTile), view.isDoubleRiichi)
        is GameEventView.TsumoWin -> ProtocolGameEvent.TsumoWin(
            view.turnNumber, view.player, toProtocolTile(view.winTile), Collections.unmodifiableList(ArrayList(view.yakuList)),
            view.han, view.fu, view.score, view.breakdown?.let { toProtocolScoreBreakdown(it) }
        )
        is GameEventView.RonWin -> ProtocolGameEvent.RonWin(
            view.turnNumber, view.winner, view.loser, toProtocolTile(view.winTile), Collections.unmodifiableList(ArrayList(view.yakuList)),
            view.han, view.fu, view.score, view.breakdown?.let { toProtocolScoreBreakdown(it) }
        )
        is GameEventView.DoraRevealed -> ProtocolGameEvent.DoraRevealed(view.turnNumber, toProtocolTile(view.indicator), view.isUraDora)
        is GameEventView.CheatPointsChanged -> ProtocolGameEvent.CheatPointsChanged(view.turnNumber, view.seat, view.amount, view.newBalance)
        is GameEventView.DrawGame -> ProtocolGameEvent.DrawGame(view.turnNumber, toProtocolDrawReason(view.reason), Collections.unmodifiableList(ArrayList(view.tenpaiPlayers)))
        is GameEventView.ScoreSettled -> ProtocolGameEvent.ScoreSettled(
            view.turnNumber,
            Collections.unmodifiableMap(LinkedHashMap(view.payments)),
            Collections.unmodifiableList(ArrayList(view.newScores))
        )
        is GameEventView.RoundEnded -> ProtocolGameEvent.RoundEnded(view.turnNumber, view.roundWind, view.roundNumber)
        is GameEventView.MatchEnded -> ProtocolGameEvent.MatchEnded(view.turnNumber, Collections.unmodifiableList(ArrayList(view.finalScores)), view.winner)
    }

    fun toProtocolEvents(events: List<GameEventView>): List<ProtocolGameEvent> =
        Collections.unmodifiableList(events.map { toProtocolGameEvent(it) })

    // ==========================================
    // 独立公开模型双向映射 (Public View Models)
    // ==========================================

    fun mapPublicRoundOutcome(outcome: ProtocolRoundOutcome): PublicRoundOutcomeView = when (outcome) {
        is ProtocolRoundOutcome.DealerWin -> PublicRoundOutcomeView.DealerWin(outcome.winner)
        is ProtocolRoundOutcome.NonDealerWin -> PublicRoundOutcomeView.NonDealerWin(outcome.winner)
        is ProtocolRoundOutcome.Ron -> PublicRoundOutcomeView.Ron(outcome.winner, outcome.loser)
        is ProtocolRoundOutcome.Tsumo -> PublicRoundOutcomeView.Tsumo(outcome.winner)
        is ProtocolRoundOutcome.ExhaustiveDraw -> PublicRoundOutcomeView.ExhaustiveDraw(outcome.tenpaiSeats)
        is ProtocolRoundOutcome.AbortiveDraw -> PublicRoundOutcomeView.AbortiveDraw(outcome.reason)
    }

    fun toProtocolRoundOutcome(view: PublicRoundOutcomeView): ProtocolRoundOutcome = when (view) {
        is PublicRoundOutcomeView.DealerWin -> ProtocolRoundOutcome.DealerWin(view.winner)
        is PublicRoundOutcomeView.NonDealerWin -> ProtocolRoundOutcome.NonDealerWin(view.winner)
        is PublicRoundOutcomeView.Ron -> ProtocolRoundOutcome.Ron(view.winner, view.loser)
        is PublicRoundOutcomeView.Tsumo -> ProtocolRoundOutcome.Tsumo(view.winner)
        is PublicRoundOutcomeView.ExhaustiveDraw -> ProtocolRoundOutcome.ExhaustiveDraw(view.tenpaiSeats)
        is PublicRoundOutcomeView.AbortiveDraw -> ProtocolRoundOutcome.AbortiveDraw(view.reason)
    }

    fun mapPublicScoreRecord(record: ProtocolPublicScoreRecord): PublicScoreRecordView = PublicScoreRecordView(
        roundId = record.roundId,
        resultId = record.resultId,
        finalScores = record.finalScores,
        deltas = record.deltas,
        remainingRiichiSticks = record.remainingRiichiSticks,
        outcome = record.outcome?.let { mapPublicRoundOutcome(it) }
    )

    fun toProtocolPublicScoreRecord(view: PublicScoreRecordView): ProtocolPublicScoreRecord = ProtocolPublicScoreRecord(
        roundId = view.roundId,
        resultId = view.resultId,
        finalScores = view.finalScores,
        deltas = view.deltas,
        remainingRiichiSticks = view.remainingRiichiSticks,
        outcome = view.outcome?.let { toProtocolRoundOutcome(it) }
    )

    fun mapPublicMatchResult(result: ProtocolPublicMatchResult): PublicMatchResultView = PublicMatchResultView(
        finalScores = result.finalScores,
        rankedSeats = result.rankedSeats,
        reason = result.reason,
        remainingRiichiSticksTransferredTo = result.remainingRiichiSticksTransferredTo
    )

    fun toProtocolPublicMatchResult(view: PublicMatchResultView): ProtocolPublicMatchResult = ProtocolPublicMatchResult(
        finalScores = view.finalScores,
        rankedSeats = view.rankedSeats,
        reason = view.reason,
        remainingRiichiSticksTransferredTo = view.remainingRiichiSticksTransferredTo
    )

    fun mapPublicSessionSummary(summary: ProtocolPublicSessionSummary): PublicSessionSummaryView = PublicSessionSummaryView(
        lifecycle = summary.lifecycle,
        scores = summary.scores,
        roundWind = summary.roundWind,
        roundNumber = summary.roundNumber,
        dealerSeat = summary.dealerSeat,
        honba = summary.honba,
        roundRecords = Collections.unmodifiableList(summary.roundRecords.map { mapPublicScoreRecord(it) }),
        finalResult = summary.finalResult?.let { mapPublicMatchResult(it) }
    )

    fun toProtocolPublicSessionSummary(view: PublicSessionSummaryView): ProtocolPublicSessionSummary = ProtocolPublicSessionSummary(
        lifecycle = view.lifecycle,
        scores = view.scores,
        roundWind = view.roundWind,
        roundNumber = view.roundNumber,
        dealerSeat = view.dealerSeat,
        honba = view.honba,
        roundRecords = Collections.unmodifiableList(view.roundRecords.map { toProtocolPublicScoreRecord(it) }),
        finalResult = view.finalResult?.let { toProtocolPublicMatchResult(it) }
    )

    fun mapPublicBoardView(board: ProtocolPublicBoardView): PublicBoardView = PublicBoardView(
        phase = mapGamePhase(board.phase),
        roundWind = board.roundWind,
        roundNumber = board.roundNumber,
        honbaSticks = board.honbaSticks,
        riichiSticks = board.riichiSticks,
        dealerSeat = board.dealerSeat,
        currentTurnPlayer = board.currentTurnPlayer,
        wallRemaining = board.wallRemaining,
        doraIndicators = mapTiles(board.doraIndicators),
        scores = Collections.unmodifiableList(ArrayList(board.scores)),
        riichiStatus = Collections.unmodifiableList(ArrayList(board.riichiStatus)),
        discards = Collections.unmodifiableList(board.discards.map { Collections.unmodifiableList(mapTiles(it)) }),
        melds = Collections.unmodifiableList(board.melds.map { playerMelds -> Collections.unmodifiableList(playerMelds.map { mapMeld(it) }) }),
        handTileCounts = Collections.unmodifiableList(ArrayList(board.handTileCounts)),
        lastDiscard = board.lastDiscard?.let { mapTile(it) },
        lastDiscardPlayer = board.lastDiscardPlayer
    )

    fun toProtocolPublicBoardView(view: PublicBoardView): ProtocolPublicBoardView = ProtocolPublicBoardView(
        phase = toProtocolGamePhase(view.phase),
        roundWind = view.roundWind,
        roundNumber = view.roundNumber,
        honbaSticks = view.honbaSticks,
        riichiSticks = view.riichiSticks,
        dealerSeat = view.dealerSeat,
        currentTurnPlayer = view.currentTurnPlayer,
        wallRemaining = view.wallRemaining,
        doraIndicators = toProtocolTiles(view.doraIndicators),
        scores = Collections.unmodifiableList(ArrayList(view.scores)),
        riichiStatus = Collections.unmodifiableList(ArrayList(view.riichiStatus)),
        discards = Collections.unmodifiableList(view.discards.map { Collections.unmodifiableList(toProtocolTiles(it)) }),
        melds = Collections.unmodifiableList(view.melds.map { playerMelds -> Collections.unmodifiableList(playerMelds.map { toProtocolMeld(it) }) }),
        handTileCounts = Collections.unmodifiableList(ArrayList(view.handTileCounts)),
        lastDiscard = view.lastDiscard?.let { toProtocolTile(it) },
        lastDiscardPlayer = view.lastDiscardPlayer
    )

    fun mapPublicCheckpoint(cp: ProtocolPublicCheckpoint): PublicCheckpoint = PublicCheckpoint(
        sessionId = cp.sessionId,
        syncEpoch = cp.syncEpoch,
        cursor = cp.cursor
    )

    fun toProtocolPublicCheckpoint(cp: PublicCheckpoint): ProtocolPublicCheckpoint = ProtocolPublicCheckpoint(
        sessionId = cp.sessionId,
        syncEpoch = cp.syncEpoch,
        cursor = cp.cursor
    )

    fun mapPublicSnapshot(snap: ProtocolPublicSnapshot): PublicSnapshotView = PublicSnapshotView(
        sessionId = snap.sessionId,
        sessionVersion = snap.sessionVersion,
        roundId = snap.roundId,
        roundVersion = snap.roundVersion,
        syncEpoch = snap.syncEpoch,
        cursor = snap.cursor,
        board = snap.board?.let { mapPublicBoardView(it) },
        session = mapPublicSessionSummary(snap.session),
        clocks = Collections.unmodifiableList(snap.clocks.map { mapClock(it) }),
        paused = snap.paused,
        settlementRoundId = snap.settlementRoundId,
        settlementEvents = Collections.unmodifiableList(snap.settlementEvents.map {
            validatePublicGameEvent(it)
            mapGameEvent(it)
        })
    )

    fun toProtocolPublicSnapshot(view: PublicSnapshotView): ProtocolPublicSnapshot = ProtocolPublicSnapshot(
        sessionId = view.sessionId,
        sessionVersion = view.sessionVersion,
        roundId = view.roundId,
        roundVersion = view.roundVersion,
        syncEpoch = view.syncEpoch,
        cursor = view.cursor,
        board = view.board?.let { toProtocolPublicBoardView(it) },
        session = toProtocolPublicSessionSummary(view.session),
        clocks = Collections.unmodifiableList(view.clocks.map { toProtocolClock(it) }),
        paused = view.paused,
        settlementRoundId = view.settlementRoundId,
        settlementEvents = Collections.unmodifiableList(view.settlementEvents.map {
            validatePublicGameEventView(it)
            toProtocolGameEvent(it)
        })
    )

    fun mapPublicUpdate(update: ProtocolPublicUpdate): PublicUpdateView = PublicUpdateView(
        snapshot = mapPublicSnapshot(update.snapshot),
        fromCursor = update.fromCursor,
        visibleEvents = Collections.unmodifiableList(update.visibleEvents.map {
            validatePublicGameEvent(it)
            mapGameEvent(it)
        }),
        isFullSnapshot = update.isFullSnapshot
    )

    fun toProtocolPublicUpdate(view: PublicUpdateView): ProtocolPublicUpdate = ProtocolPublicUpdate(
        snapshot = toProtocolPublicSnapshot(view.snapshot),
        fromCursor = view.fromCursor,
        visibleEvents = Collections.unmodifiableList(view.visibleEvents.map {
            validatePublicGameEventView(it)
            toProtocolGameEvent(it)
        }),
        isFullSnapshot = view.isFullSnapshot
    )

    // ==========================================
    // 落汗千术 (TileMark) 模型映射
    // ==========================================

    fun mapTileMarkConfig(config: ProtocolTileMarkConfig): TileMarkConfig = TileMarkConfig(
        enabled = config.enabled
    )

    fun toProtocolTileMarkConfig(config: TileMarkConfig): ProtocolTileMarkConfig = ProtocolTileMarkConfig(
        enabled = config.enabled
    )

    fun toProtocolTileMarkRequest(request: TileMarkRequest): ProtocolTileMarkRequest = ProtocolTileMarkRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch,
        tileIds = request.tileIds
    )

    fun mapTileMarkArea(area: ProtocolTileMarkArea): TileMarkArea = when (area) {
        ProtocolTileMarkArea.OWN_HAND -> TileMarkArea.OWN_HAND
        ProtocolTileMarkArea.OPPONENT_HAND -> TileMarkArea.OPPONENT_HAND
        ProtocolTileMarkArea.DISCARD -> TileMarkArea.DISCARD
        ProtocolTileMarkArea.EXPOSED_MELD -> TileMarkArea.EXPOSED_MELD
        ProtocolTileMarkArea.EXPOSED_INDICATOR -> TileMarkArea.EXPOSED_INDICATOR
        ProtocolTileMarkArea.NOT_VISIBLE -> TileMarkArea.NOT_VISIBLE
    }

    fun toProtocolTileMarkArea(area: TileMarkArea): ProtocolTileMarkArea = when (area) {
        TileMarkArea.OWN_HAND -> ProtocolTileMarkArea.OWN_HAND
        TileMarkArea.OPPONENT_HAND -> ProtocolTileMarkArea.OPPONENT_HAND
        TileMarkArea.DISCARD -> ProtocolTileMarkArea.DISCARD
        TileMarkArea.EXPOSED_MELD -> ProtocolTileMarkArea.EXPOSED_MELD
        TileMarkArea.EXPOSED_INDICATOR -> ProtocolTileMarkArea.EXPOSED_INDICATOR
        TileMarkArea.NOT_VISIBLE -> ProtocolTileMarkArea.NOT_VISIBLE
    }

    fun mapRiverPosition(pos: ProtocolRiverPosition): RiverPositionView = RiverPositionView(
        roundId = pos.roundId,
        seat = pos.seat,
        discardIndex = pos.discardIndex
    )

    fun toProtocolRiverPosition(pos: RiverPositionView): ProtocolRiverPosition = ProtocolRiverPosition(
        roundId = pos.roundId,
        seat = pos.seat,
        discardIndex = pos.discardIndex
    )

    fun mapMarkedTile(tile: ProtocolMarkedTileView): MarkedTileView = MarkedTileView(
        tileId = tile.tileId,
        tile = mapTile(tile.tile),
        area = mapTileMarkArea(tile.area),
        seat = tile.seat,
        riverPosition = tile.riverPosition?.let { mapRiverPosition(it) }
    )

    fun toProtocolMarkedTile(tile: MarkedTileView): ProtocolMarkedTileView = ProtocolMarkedTileView(
        tileId = tile.tileId,
        tile = toProtocolTile(tile.tile),
        area = toProtocolTileMarkArea(tile.area),
        seat = tile.seat,
        riverPosition = tile.riverPosition?.let { toProtocolRiverPosition(it) }
    )

    fun mapTileMarkGroup(group: ProtocolTileMarkGroupView): TileMarkGroupView = TileMarkGroupView(
        groupId = group.groupId,
        createdRoundId = group.createdRoundId,
        createdRoundOrdinal = group.createdRoundOrdinal,
        expiresAfterRoundOrdinal = group.expiresAfterRoundOrdinal,
        futureRoundsRemaining = group.futureRoundsRemaining,
        isLastEffectiveRound = group.isLastEffectiveRound,
        tiles = group.tiles.map { mapMarkedTile(it) }
    )

    fun toProtocolTileMarkGroup(group: TileMarkGroupView): ProtocolTileMarkGroupView = ProtocolTileMarkGroupView(
        groupId = group.groupId,
        createdRoundId = group.createdRoundId,
        createdRoundOrdinal = group.createdRoundOrdinal,
        expiresAfterRoundOrdinal = group.expiresAfterRoundOrdinal,
        futureRoundsRemaining = group.futureRoundsRemaining,
        isLastEffectiveRound = group.isLastEffectiveRound,
        tiles = group.tiles.map { toProtocolMarkedTile(it) }
    )

    fun mapTileMark(mark: ProtocolTileMarkView): TileMarkView = TileMarkView(
        canApply = mark.canApply,
        disabledReason = mark.disabledReason,
        cost = mark.cost,
        activeTileCount = mark.activeTileCount,
        maxActiveTiles = mark.maxActiveTiles,
        selectableTileIds = mark.selectableTileIds,
        decisionId = mark.decisionId,
        controlEpoch = mark.controlEpoch,
        stateVersion = mark.stateVersion,
        activeGroups = mark.activeGroups.map { mapTileMarkGroup(it) }
    )

    fun toProtocolTileMark(mark: TileMarkView): ProtocolTileMarkView = ProtocolTileMarkView(
        canApply = mark.canApply,
        disabledReason = mark.disabledReason,
        cost = mark.cost,
        activeTileCount = mark.activeTileCount,
        maxActiveTiles = mark.maxActiveTiles,
        selectableTileIds = mark.selectableTileIds,
        decisionId = mark.decisionId,
        controlEpoch = mark.controlEpoch,
        stateVersion = mark.stateVersion,
        activeGroups = mark.activeGroups.map { toProtocolTileMarkGroup(it) }
    )

    // === 袖里藏花 (SleeveBloom) 模型映射 ===

    fun mapSleeveBloomConfig(config: ProtocolSleeveBloomConfig): SleeveBloomConfig = SleeveBloomConfig(
        enabled = config.enabled
    )

    fun toProtocolSleeveBloomConfig(config: SleeveBloomConfig): ProtocolSleeveBloomConfig = ProtocolSleeveBloomConfig(
        enabled = config.enabled
    )

    fun mapSleeveBloomDrawKind(kind: ProtocolSleeveBloomDrawKind): SleeveBloomDrawKind = when (kind) {
        ProtocolSleeveBloomDrawKind.DEAL_EXTRA -> SleeveBloomDrawKind.DEAL_EXTRA
        ProtocolSleeveBloomDrawKind.NORMAL -> SleeveBloomDrawKind.NORMAL
        ProtocolSleeveBloomDrawKind.RINSHAN -> SleeveBloomDrawKind.RINSHAN
    }

    fun toProtocolSleeveBloomDrawKind(kind: SleeveBloomDrawKind): ProtocolSleeveBloomDrawKind = when (kind) {
        SleeveBloomDrawKind.DEAL_EXTRA -> ProtocolSleeveBloomDrawKind.DEAL_EXTRA
        SleeveBloomDrawKind.NORMAL -> ProtocolSleeveBloomDrawKind.NORMAL
        SleeveBloomDrawKind.RINSHAN -> ProtocolSleeveBloomDrawKind.RINSHAN
    }

    fun mapSleeveBloomKeep(keep: ProtocolSleeveBloomKeep): SleeveBloomKeep = when (keep) {
        ProtocolSleeveBloomKeep.FIRST -> SleeveBloomKeep.FIRST
        ProtocolSleeveBloomKeep.SECOND -> SleeveBloomKeep.SECOND
    }

    fun toProtocolSleeveBloomKeep(keep: SleeveBloomKeep): ProtocolSleeveBloomKeep = when (keep) {
        SleeveBloomKeep.FIRST -> ProtocolSleeveBloomKeep.FIRST
        SleeveBloomKeep.SECOND -> ProtocolSleeveBloomKeep.SECOND
    }

    fun toProtocolSleeveBloomRequest(request: SleeveBloomRequest): ProtocolSleeveBloomRequest = ProtocolSleeveBloomRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch
    )

    fun toProtocolSleeveBloomChoiceRequest(request: SleeveBloomChoiceRequest): ProtocolSleeveBloomChoiceRequest = ProtocolSleeveBloomChoiceRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch,
        offerId = request.offerId,
        keep = toProtocolSleeveBloomKeep(request.keep)
    )

    fun mapSleeveBloomOfferView(view: ProtocolSleeveBloomOfferView): SleeveBloomOfferView = SleeveBloomOfferView(
        offerId = view.offerId,
        firstTile = mapTile(view.firstTile),
        secondTile = mapTile(view.secondTile),
        drawKind = mapSleeveBloomDrawKind(view.drawKind),
        deadlineMillis = view.deadlineMillis,
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch
    )

    fun toProtocolSleeveBloomOfferView(view: SleeveBloomOfferView): ProtocolSleeveBloomOfferView = ProtocolSleeveBloomOfferView(
        offerId = view.offerId,
        firstTile = toProtocolTile(view.firstTile),
        secondTile = toProtocolTile(view.secondTile),
        drawKind = toProtocolSleeveBloomDrawKind(view.drawKind),
        deadlineMillis = view.deadlineMillis,
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch
    )

    fun mapSleeveBloom(view: ProtocolSleeveBloomView): SleeveBloomView = SleeveBloomView(
        canApply = view.canApply,
        disabledReason = view.disabledReason,
        cost = view.cost,
        drawKind = view.drawKind?.let { mapSleeveBloomDrawKind(it) },
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch,
        stateVersion = view.stateVersion,
        pendingOffer = view.pendingOffer?.let { mapSleeveBloomOfferView(it) }
    )

    fun toProtocolSleeveBloom(view: SleeveBloomView): ProtocolSleeveBloomView = ProtocolSleeveBloomView(
        canApply = view.canApply,
        disabledReason = view.disabledReason,
        cost = view.cost,
        drawKind = view.drawKind?.let { toProtocolSleeveBloomDrawKind(it) },
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch,
        stateVersion = view.stateVersion,
        pendingOffer = view.pendingOffer?.let { toProtocolSleeveBloomOfferView(it) }
    )

    fun mapBeamSwapConfig(config: ProtocolBeamSwapConfig): BeamSwapConfig = BeamSwapConfig(
        enabled = config.enabled
    )

    fun toProtocolBeamSwapConfig(config: BeamSwapConfig): ProtocolBeamSwapConfig = ProtocolBeamSwapConfig(
        enabled = config.enabled
    )

    fun mapBeamSwapWindow(window: ProtocolBeamSwapWindow): BeamSwapWindow = when (window) {
        ProtocolBeamSwapWindow.WALL -> BeamSwapWindow.WALL
        ProtocolBeamSwapWindow.DEAD_WALL -> BeamSwapWindow.DEAD_WALL
    }

    fun toProtocolBeamSwapWindow(window: BeamSwapWindow): ProtocolBeamSwapWindow = when (window) {
        BeamSwapWindow.WALL -> ProtocolBeamSwapWindow.WALL
        BeamSwapWindow.DEAD_WALL -> ProtocolBeamSwapWindow.DEAD_WALL
    }

    fun mapBeamSwapAction(action: ProtocolBeamSwapAction): BeamSwapAction = when (action) {
        ProtocolBeamSwapAction.SWAP -> BeamSwapAction.SWAP
        ProtocolBeamSwapAction.KEEP_ORIGINAL -> BeamSwapAction.KEEP_ORIGINAL
    }

    fun toProtocolBeamSwapAction(action: BeamSwapAction): ProtocolBeamSwapAction = when (action) {
        BeamSwapAction.SWAP -> ProtocolBeamSwapAction.SWAP
        BeamSwapAction.KEEP_ORIGINAL -> ProtocolBeamSwapAction.KEEP_ORIGINAL
    }

    fun mapBeamSwapSlot(slot: ProtocolBeamSwapSlot): BeamSwapSlot = when (slot) {
        is ProtocolBeamSwapSlot.WallSlot -> BeamSwapSlot.WallSlot(slot.index)
        is ProtocolBeamSwapSlot.DeadWallSlot -> BeamSwapSlot.DeadWallSlot(slot.index)
        is ProtocolBeamSwapSlot.ReplacementSlot -> BeamSwapSlot.ReplacementSlot(slot.index)
    }

    fun toProtocolBeamSwapSlot(slot: BeamSwapSlot): ProtocolBeamSwapSlot = when (slot) {
        is BeamSwapSlot.WallSlot -> ProtocolBeamSwapSlot.WallSlot(slot.index)
        is BeamSwapSlot.DeadWallSlot -> ProtocolBeamSwapSlot.DeadWallSlot(slot.index)
        is BeamSwapSlot.ReplacementSlot -> ProtocolBeamSwapSlot.ReplacementSlot(slot.index)
    }

    fun mapBeamSwapCandidateView(view: ProtocolBeamSwapCandidateView): BeamSwapCandidateView = BeamSwapCandidateView(
        slot = mapBeamSwapSlot(view.slot),
        marked = view.marked,
        tile = view.tile?.let { mapTile(it) }
    )

    fun toProtocolBeamSwapCandidateView(view: BeamSwapCandidateView): ProtocolBeamSwapCandidateView = ProtocolBeamSwapCandidateView(
        slot = toProtocolBeamSwapSlot(view.slot),
        marked = view.marked,
        tile = view.tile?.let { toProtocolTile(it) }
    )

    fun toProtocolBeamSwapRequest(request: BeamSwapRequest): ProtocolBeamSwapRequest = ProtocolBeamSwapRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch
    )

    fun toProtocolBeamSwapChoiceRequest(request: BeamSwapChoiceRequest): ProtocolBeamSwapChoiceRequest = ProtocolBeamSwapChoiceRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch,
        offerId = request.offerId,
        action = toProtocolBeamSwapAction(request.action),
        handTileId = request.handTileId,
        targetSlot = request.targetSlot?.let { toProtocolBeamSwapSlot(it) }
    )

    fun mapBeamSwapOfferView(view: ProtocolBeamSwapOfferView): BeamSwapOfferView = BeamSwapOfferView(
        offerId = view.offerId,
        window = mapBeamSwapWindow(view.window),
        candidates = view.candidates.map { mapBeamSwapCandidateView(it) },
        selectableHandTileIds = view.selectableHandTileIds,
        deadlineMillis = view.deadlineMillis,
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch
    )

    fun toProtocolBeamSwapOfferView(view: BeamSwapOfferView): ProtocolBeamSwapOfferView = ProtocolBeamSwapOfferView(
        offerId = view.offerId,
        window = toProtocolBeamSwapWindow(view.window),
        candidates = view.candidates.map { toProtocolBeamSwapCandidateView(it) },
        selectableHandTileIds = view.selectableHandTileIds,
        deadlineMillis = view.deadlineMillis,
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch
    )

    fun mapBeamSwap(view: ProtocolBeamSwapView): BeamSwapView = BeamSwapView(
        canApply = view.canApply,
        disabledReason = view.disabledReason,
        cost = view.cost,
        window = view.window?.let { mapBeamSwapWindow(it) },
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch,
        stateVersion = view.stateVersion,
        pendingOffer = view.pendingOffer?.let { mapBeamSwapOfferView(it) }
    )

    fun toProtocolBeamSwap(view: BeamSwapView): ProtocolBeamSwapView = ProtocolBeamSwapView(
        canApply = view.canApply,
        disabledReason = view.disabledReason,
        cost = view.cost,
        window = view.window?.let { toProtocolBeamSwapWindow(it) },
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch,
        stateVersion = view.stateVersion,
        pendingOffer = view.pendingOffer?.let { toProtocolBeamSwapOfferView(it) }
    )
}
