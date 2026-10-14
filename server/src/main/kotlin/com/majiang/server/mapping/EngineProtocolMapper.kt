package com.majiang.server.mapping

import com.majiang.access.AccessCapabilities
import com.majiang.access.ActionReceipt
import com.majiang.access.ClockView
import com.majiang.access.MeldPreviewOption
import com.majiang.access.PlayerPresentationPreview
import com.majiang.access.PlayerSnapshot
import com.majiang.access.PlayerUpdate
import com.majiang.access.PublicRoundRecord
import com.majiang.access.PublicSessionResult
import com.majiang.access.ReceiptStatus
import com.majiang.access.RiichiPreviewOption
import com.majiang.access.SeatControlView
import com.majiang.access.SessionView
import com.majiang.application.ActionChoice
import com.majiang.application.ActionOption
import com.majiang.application.PlayerDecision
import com.majiang.economy.CheatEconomyConfig
import com.majiang.event.DrawReason
import com.majiang.event.GameEvent
import com.majiang.match.MatchConfig
import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.rules.DoraBreakdown
import com.majiang.rules.DoraIndicatorHit
import com.majiang.rules.FuBreakdown
import com.majiang.rules.FuLine
import com.majiang.rules.FuritenStatus
import com.majiang.rules.ScoreBreakdown
import com.majiang.rules.ScoreCalculator
import com.majiang.session.PlayerView
import com.majiang.stacking.ShuffleConfig
import com.majiang.stacking.ShufflePlanRequest
import com.majiang.stacking.ShufflePreparationStatus
import com.majiang.stacking.ShufflePreparationView
import com.majiang.stacking.ShuffleSelection
import com.majiang.stacking.ShuffleTarget
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.cheat.mark.MarkedTileView
import com.majiang.cheat.mark.RiverPosition
import com.majiang.cheat.mark.TileMarkArea
import com.majiang.cheat.mark.TileMarkConfig
import com.majiang.cheat.mark.TileMarkGroupView
import com.majiang.cheat.mark.TileMarkRequest
import com.majiang.cheat.mark.TileMarkView
import com.majiang.cheat.bloom.SleeveBloomConfig
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.cheat.bloom.SleeveBloomKeep
import com.majiang.cheat.bloom.SleeveBloomOfferView
import com.majiang.cheat.bloom.SleeveBloomRequest
import com.majiang.cheat.bloom.SleeveBloomChoiceRequest
import com.majiang.cheat.bloom.SleeveBloomView
import com.majiang.cheat.beam.BeamSwapAction
import com.majiang.cheat.beam.BeamSwapCandidateView
import com.majiang.cheat.beam.BeamSwapChoiceRequest
import com.majiang.cheat.beam.BeamSwapConfig
import com.majiang.cheat.beam.BeamSwapOfferView
import com.majiang.cheat.beam.BeamSwapRequest
import com.majiang.cheat.beam.BeamSwapSlot
import com.majiang.cheat.beam.BeamSwapView
import com.majiang.cheat.beam.BeamSwapWindow
import com.majiang.timing.ReserveScope
import com.majiang.timing.TimeControlConfig
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionOption
import com.majiang.transport.api.ProtocolActionReceipt
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
import com.majiang.transport.api.ProtocolMarkedTileView
import com.majiang.transport.api.ProtocolMatchConfig
import com.majiang.transport.api.ProtocolMeld
import com.majiang.transport.api.ProtocolMeldPreviewOption
import com.majiang.transport.api.ProtocolMeldType
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerPresentationPreview
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPlayerView
import com.majiang.transport.api.ProtocolRiichiPreviewOption
import com.majiang.transport.api.ProtocolRiverPosition
import com.majiang.transport.api.ProtocolPublicRoundRecord
import com.majiang.transport.api.ProtocolPublicSessionResult
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolScoreBreakdown
import com.majiang.transport.api.ProtocolScoreResult
import com.majiang.transport.api.ProtocolSeatControl
import com.majiang.transport.api.ProtocolSessionView
import com.majiang.transport.api.ProtocolShuffleConfig
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolShufflePreparation
import com.majiang.transport.api.ProtocolShufflePreparationStatus
import com.majiang.transport.api.ProtocolShuffleSelection
import com.majiang.transport.api.ProtocolShuffleTarget
import com.majiang.transport.api.ProtocolSuit
import com.majiang.transport.api.ProtocolTile
import com.majiang.transport.api.ProtocolTileMarkArea
import com.majiang.transport.api.ProtocolTileMarkConfig
import com.majiang.transport.api.ProtocolTileMarkGroupView
import com.majiang.transport.api.ProtocolTileMarkRequest
import com.majiang.transport.api.ProtocolTileMarkView
import com.majiang.transport.api.ProtocolTimeControlConfig
import com.majiang.transport.api.ProtocolSleeveBloomConfig
import com.majiang.transport.api.ProtocolSleeveBloomDrawKind
import com.majiang.transport.api.ProtocolSleeveBloomKeep
import com.majiang.transport.api.ProtocolSleeveBloomOfferView
import com.majiang.transport.api.ProtocolSleeveBloomRequest
import com.majiang.transport.api.ProtocolSleeveBloomChoiceRequest
import com.majiang.transport.api.ProtocolSleeveBloomView
import com.majiang.transport.api.ProtocolBeamSwapAction
import com.majiang.transport.api.ProtocolBeamSwapCandidateView
import com.majiang.transport.api.ProtocolBeamSwapChoiceRequest
import com.majiang.transport.api.ProtocolBeamSwapConfig
import com.majiang.transport.api.ProtocolBeamSwapOfferView
import com.majiang.transport.api.ProtocolBeamSwapRequest
import com.majiang.transport.api.ProtocolBeamSwapSlot
import com.majiang.transport.api.ProtocolBeamSwapView
import com.majiang.transport.api.ProtocolBeamSwapWindow
import java.util.Collections
import java.util.LinkedHashMap
import java.util.LinkedHashSet

/**
 * 服务端引擎领域模型与协议 DTO 双向映射器 (EngineProtocolMapper)
 *
 * 核心架构约束：
 * 1. 显式类型转换：严禁使用 Any/Map/Gson JsonElement 逃避类型隔离；
 * 2. 防御性浅拷贝：所有集合均建立独立不可变集合副本；
 * 3. 严格字段范围与枚举验证：确保物理牌 0..135 全保真，红五保真。
 */
object EngineProtocolMapper {

    // === Suit ===

    fun toProtocolSuit(suit: Suit): ProtocolSuit = when (suit) {
        Suit.MAN -> ProtocolSuit.MAN
        Suit.PIN -> ProtocolSuit.PIN
        Suit.SOU -> ProtocolSuit.SOU
        Suit.WIND -> ProtocolSuit.WIND
        Suit.DRAGON -> ProtocolSuit.DRAGON
    }

    fun toEngineSuit(suit: ProtocolSuit): Suit = when (suit) {
        ProtocolSuit.MAN -> Suit.MAN
        ProtocolSuit.PIN -> Suit.PIN
        ProtocolSuit.SOU -> Suit.SOU
        ProtocolSuit.WIND -> Suit.WIND
        ProtocolSuit.DRAGON -> Suit.DRAGON
    }

    // === Tile ===

    fun toProtocolTile(tile: Tile): ProtocolTile = ProtocolTile.fromTileId(tile.tileId)

    fun toEngineTile(tile: ProtocolTile): Tile = Tile.fromTileId(tile.tileId)

    fun toProtocolTiles(tiles: List<Tile>): List<ProtocolTile> =
        Collections.unmodifiableList(tiles.map { toProtocolTile(it) })

    fun toEngineTiles(tiles: List<ProtocolTile>): List<Tile> =
        Collections.unmodifiableList(tiles.map { toEngineTile(it) })

    // === MeldType ===

    fun toProtocolMeldType(type: MeldType): ProtocolMeldType = when (type) {
        MeldType.CHI -> ProtocolMeldType.CHI
        MeldType.PON -> ProtocolMeldType.PON
        MeldType.OPEN_KAN -> ProtocolMeldType.OPEN_KAN
        MeldType.CLOSED_KAN -> ProtocolMeldType.CLOSED_KAN
        MeldType.ADDED_KAN -> ProtocolMeldType.ADDED_KAN
    }

    fun toEngineMeldType(type: ProtocolMeldType): MeldType = when (type) {
        ProtocolMeldType.CHI -> MeldType.CHI
        ProtocolMeldType.PON -> MeldType.PON
        ProtocolMeldType.OPEN_KAN -> MeldType.OPEN_KAN
        ProtocolMeldType.CLOSED_KAN -> MeldType.CLOSED_KAN
        ProtocolMeldType.ADDED_KAN -> MeldType.ADDED_KAN
    }

    // === Meld ===

    fun toProtocolMeld(meld: Meld): ProtocolMeld = ProtocolMeld(
        type = toProtocolMeldType(meld.type),
        tiles = toProtocolTiles(meld.tiles),
        fromPlayer = meld.fromPlayer,
        calledTile = toProtocolTile(meld.calledTile)
    )

    fun toEngineMeld(meld: ProtocolMeld): Meld = Meld(
        type = toEngineMeldType(meld.type),
        tiles = toEngineTiles(meld.tiles),
        fromPlayer = meld.fromPlayer,
        calledTile = meld.calledTile?.let { toEngineTile(it) } ?: toEngineTile(meld.tiles.first())
    )

    fun toProtocolMelds(melds: List<Meld>): List<ProtocolMeld> =
        Collections.unmodifiableList(melds.map { toProtocolMeld(it) })

    // === GamePhase ===

    fun toProtocolPhase(phase: GamePhase): ProtocolGamePhase = when (phase) {
        GamePhase.STACKING -> ProtocolGamePhase.STACKING
        GamePhase.DEALING -> ProtocolGamePhase.DEALING
        GamePhase.DRAW -> ProtocolGamePhase.DRAW
        GamePhase.CHEAT_WINDOW -> ProtocolGamePhase.CHEAT_WINDOW
        GamePhase.DISCARD -> ProtocolGamePhase.DISCARD
        GamePhase.RESPONSE_WINDOW -> ProtocolGamePhase.RESPONSE_WINDOW
        GamePhase.CHANKAN_WINDOW -> ProtocolGamePhase.CHANKAN_WINDOW
        GamePhase.KAN_DRAW -> ProtocolGamePhase.KAN_DRAW
        GamePhase.ROUND_END -> ProtocolGamePhase.ROUND_END
        GamePhase.MATCH_END -> ProtocolGamePhase.MATCH_END
    }

    fun toEnginePhase(phase: ProtocolGamePhase): GamePhase = when (phase) {
        ProtocolGamePhase.STACKING -> GamePhase.STACKING
        ProtocolGamePhase.DEALING -> GamePhase.DEALING
        ProtocolGamePhase.DRAW -> GamePhase.DRAW
        ProtocolGamePhase.CHEAT_WINDOW -> GamePhase.CHEAT_WINDOW
        ProtocolGamePhase.DISCARD -> GamePhase.DISCARD
        ProtocolGamePhase.RESPONSE_WINDOW -> GamePhase.RESPONSE_WINDOW
        ProtocolGamePhase.CHANKAN_WINDOW -> GamePhase.CHANKAN_WINDOW
        ProtocolGamePhase.KAN_DRAW -> GamePhase.KAN_DRAW
        ProtocolGamePhase.ROUND_END -> GamePhase.ROUND_END
        ProtocolGamePhase.MATCH_END -> GamePhase.MATCH_END
    }

    // === DrawReason ===

    fun toProtocolDrawReason(reason: DrawReason): ProtocolDrawReason = when (reason) {
        DrawReason.EXHAUSTIVE -> ProtocolDrawReason.EXHAUSTIVE
        DrawReason.FOUR_WINDS -> ProtocolDrawReason.FOUR_WINDS
        DrawReason.FOUR_KANS -> ProtocolDrawReason.FOUR_KANS
        DrawReason.FOUR_RIICHI -> ProtocolDrawReason.FOUR_RIICHI
        DrawReason.NINE_TERMINALS -> ProtocolDrawReason.NINE_TERMINALS
        DrawReason.TRIPLE_RON -> ProtocolDrawReason.TRIPLE_RON
    }

    fun toEngineDrawReason(reason: ProtocolDrawReason): DrawReason = when (reason) {
        ProtocolDrawReason.EXHAUSTIVE -> DrawReason.EXHAUSTIVE
        ProtocolDrawReason.FOUR_WINDS -> DrawReason.FOUR_WINDS
        ProtocolDrawReason.FOUR_KANS -> DrawReason.FOUR_KANS
        ProtocolDrawReason.FOUR_RIICHI -> DrawReason.FOUR_RIICHI
        ProtocolDrawReason.NINE_TERMINALS -> DrawReason.NINE_TERMINALS
        ProtocolDrawReason.TRIPLE_RON -> DrawReason.TRIPLE_RON
    }

    // === FuritenStatus ===

    fun toProtocolFuriten(f: FuritenStatus): ProtocolFuritenStatus = ProtocolFuritenStatus(
        discard = f.discard,
        temporary = f.temporary,
        riichi = f.riichi
    )

    fun toEngineFuriten(f: ProtocolFuritenStatus): FuritenStatus = FuritenStatus(
        discard = f.discard,
        temporary = f.temporary,
        riichi = f.riichi
    )

    // === GameAction -> ProtocolLegalAction ===

    fun toProtocolAction(action: GameAction): ProtocolLegalAction = when (action) {
        is GameAction.StartStacking -> {
            require(action.stackingPlans.isEmpty()) {
                "Legacy StartStacking nonempty plans are unsupported in engine: ${action.stackingPlans}"
            }
            ProtocolLegalAction.StartStacking(emptyList())
        }
        is GameAction.DealComplete -> ProtocolLegalAction.DealComplete
        is GameAction.DrawTile -> ProtocolLegalAction.DrawTile(action.player)
        is GameAction.DiscardTile -> ProtocolLegalAction.DiscardTile(action.player, toProtocolTile(action.tile))
        is GameAction.DeclareChi -> ProtocolLegalAction.DeclareChi(action.player, toProtocolMeld(action.meld))
        is GameAction.DeclarePon -> ProtocolLegalAction.DeclarePon(action.player, toProtocolTiles(action.tiles))
        is GameAction.DeclareKan -> ProtocolLegalAction.DeclareKan(
            action.player,
            toProtocolTiles(action.tiles),
            toProtocolMeldType(action.kanType)
        )
        is GameAction.DeclareRiichi -> ProtocolLegalAction.DeclareRiichi(action.player, toProtocolTile(action.discardTile))
        is GameAction.DeclareTsumo -> ProtocolLegalAction.DeclareTsumo(action.player)
        is GameAction.DeclareRon -> ProtocolLegalAction.DeclareRon(action.player)
        is GameAction.Pass -> ProtocolLegalAction.Pass(action.player)
        is GameAction.DeclareAbortiveDraw -> ProtocolLegalAction.DeclareAbortiveDraw(action.player)
        is GameAction.DeclareAgariYame -> ProtocolLegalAction.DeclareAgariYame(action.player)
    }

    fun toEngineAction(action: ProtocolLegalAction): GameAction = when (action) {
        is ProtocolLegalAction.StartStacking -> {
            require(action.stackingPlans.isEmpty()) {
                "Legacy StartStacking nonempty plans are unsupported: ${action.stackingPlans}"
            }
            GameAction.StartStacking()
        }
        is ProtocolLegalAction.DealComplete -> GameAction.DealComplete
        is ProtocolLegalAction.DrawTile -> GameAction.DrawTile(action.player)
        is ProtocolLegalAction.DiscardTile -> GameAction.DiscardTile(action.player, toEngineTile(action.tile))
        is ProtocolLegalAction.DeclareChi -> GameAction.DeclareChi(action.player, toEngineMeld(action.meld))
        is ProtocolLegalAction.DeclarePon -> GameAction.DeclarePon(action.player, toEngineTiles(action.tiles))
        is ProtocolLegalAction.DeclareKan -> GameAction.DeclareKan(
            action.player,
            toEngineTiles(action.tiles),
            toEngineMeldType(action.kanType)
        )
        is ProtocolLegalAction.DeclareRiichi -> GameAction.DeclareRiichi(action.player, toEngineTile(action.discardTile))
        is ProtocolLegalAction.DeclareTsumo -> GameAction.DeclareTsumo(action.player)
        is ProtocolLegalAction.DeclareRon -> GameAction.DeclareRon(action.player)
        is ProtocolLegalAction.Pass -> GameAction.Pass(action.player)
        is ProtocolLegalAction.DeclareAbortiveDraw -> GameAction.DeclareAbortiveDraw(action.player)
        is ProtocolLegalAction.DeclareAgariYame -> GameAction.DeclareAgariYame(action.player)
    }

    // === ActionOption / PlayerDecision ===

    fun toProtocolActionOption(opt: ActionOption): ProtocolActionOption = ProtocolActionOption(
        optionId = opt.optionId,
        action = toProtocolAction(opt.action)
    )

    fun toProtocolDecision(dec: PlayerDecision): ProtocolPlayerDecision = ProtocolPlayerDecision(
        roundId = dec.roundId,
        decisionId = dec.decisionId,
        controlEpoch = dec.controlEpoch,
        options = Collections.unmodifiableList(dec.options.map { toProtocolActionOption(it) })
    )

    // === ActionChoice ===

    fun toEngineActionChoice(choice: ProtocolActionChoice): ActionChoice = ActionChoice(
        requestId = choice.requestId,
        roundId = choice.roundId,
        decisionId = choice.decisionId,
        controlEpoch = choice.controlEpoch,
        optionId = choice.optionId
    )

    fun toProtocolActionChoice(choice: ActionChoice): ProtocolActionChoice = ProtocolActionChoice(
        requestId = choice.requestId,
        roundId = choice.roundId,
        decisionId = choice.decisionId,
        controlEpoch = choice.controlEpoch,
        optionId = choice.optionId
    )

    // === ReceiptStatus / ActionReceipt ===

    fun toProtocolReceiptStatus(status: ReceiptStatus): ProtocolReceiptStatus = when (status) {
        ReceiptStatus.APPLIED -> ProtocolReceiptStatus.APPLIED
        ReceiptStatus.RECORDED -> ProtocolReceiptStatus.RECORDED
        ReceiptStatus.REJECTED -> ProtocolReceiptStatus.REJECTED
    }

    fun toEngineReceiptStatus(status: ProtocolReceiptStatus): ReceiptStatus = when (status) {
        ProtocolReceiptStatus.APPLIED -> ReceiptStatus.APPLIED
        ProtocolReceiptStatus.RECORDED -> ReceiptStatus.RECORDED
        ProtocolReceiptStatus.REJECTED -> ReceiptStatus.REJECTED
    }

    fun toProtocolReceipt(receipt: ActionReceipt): ProtocolActionReceipt = ProtocolActionReceipt(
        requestId = receipt.requestId,
        status = toProtocolReceiptStatus(receipt.status),
        reason = receipt.reason,
        cursor = receipt.cursor
    )

    fun toEngineReceipt(receipt: ProtocolActionReceipt): ActionReceipt = ActionReceipt(
        requestId = receipt.requestId,
        status = toEngineReceiptStatus(receipt.status),
        reason = receipt.reason,
        cursor = receipt.cursor
    )

    // === AccessCapabilities ===

    fun toProtocolCapabilities(caps: AccessCapabilities): ProtocolAccessCapabilities = ProtocolAccessCapabilities(
        protocolVersion = caps.protocolVersion,
        supportedPlayerCounts = Collections.unmodifiableSet(LinkedHashSet(caps.supportedPlayerCounts)),
        remoteTransport = caps.remoteTransport,
        persistence = caps.persistence,
        resumption = caps.resumption,
        tileMarkSupported = true,
        sleeveBloomSupported = true,
        beamSwapSupported = true
    )

    // === ClockView / SeatControlView ===

    fun toProtocolClock(clock: ClockView): ProtocolClock = ProtocolClock(
        seat = clock.seat,
        opensAt = clock.opensAt,
        deadline = clock.deadline,
        reserve = clock.reserve,
        serverNow = clock.serverNow
    )

    fun toProtocolSeatControl(control: SeatControlView): ProtocolSeatControl = ProtocolSeatControl(
        seat = control.seat,
        connected = control.connected,
        role = control.role,
        controlEpoch = control.controlEpoch
    )

    // === ShuffleTarget / Selection / Plan / Config ===

    fun toProtocolShuffleTarget(target: ShuffleTarget): ProtocolShuffleTarget = when (target) {
        ShuffleTarget.CHINITSU_MAN -> ProtocolShuffleTarget.CHINITSU_MAN
        ShuffleTarget.CHINITSU_SOU -> ProtocolShuffleTarget.CHINITSU_SOU
        ShuffleTarget.CHINITSU_PIN -> ProtocolShuffleTarget.CHINITSU_PIN
        ShuffleTarget.DAISANGEN -> ProtocolShuffleTarget.DAISANGEN
        ShuffleTarget.KOKUSHI -> ProtocolShuffleTarget.KOKUSHI
        ShuffleTarget.DAISUUSHII -> ProtocolShuffleTarget.DAISUUSHII
    }

    fun toEngineShuffleTarget(target: ProtocolShuffleTarget): ShuffleTarget = when (target) {
        ProtocolShuffleTarget.CHINITSU_MAN -> ShuffleTarget.CHINITSU_MAN
        ProtocolShuffleTarget.CHINITSU_SOU -> ShuffleTarget.CHINITSU_SOU
        ProtocolShuffleTarget.CHINITSU_PIN -> ShuffleTarget.CHINITSU_PIN
        ProtocolShuffleTarget.DAISANGEN -> ShuffleTarget.DAISANGEN
        ProtocolShuffleTarget.KOKUSHI -> ShuffleTarget.KOKUSHI
        ProtocolShuffleTarget.DAISUUSHII -> ShuffleTarget.DAISUUSHII
    }

    fun toProtocolShuffleSelection(selection: ShuffleSelection): ProtocolShuffleSelection = ProtocolShuffleSelection(
        initialTargets = Collections.unmodifiableSet(selection.initialTargets.mapTo(LinkedHashSet()) { toProtocolShuffleTarget(it) }),
        drawTargets = Collections.unmodifiableSet(selection.drawTargets.mapTo(LinkedHashSet()) { toProtocolShuffleTarget(it) })
    )

    fun toEngineShuffleSelection(selection: ProtocolShuffleSelection): ShuffleSelection = ShuffleSelection(
        initialTargets = Collections.unmodifiableSet(selection.initialTargets.mapTo(LinkedHashSet()) { toEngineShuffleTarget(it) }),
        drawTargets = Collections.unmodifiableSet(selection.drawTargets.mapTo(LinkedHashSet()) { toEngineShuffleTarget(it) })
    )

    fun toProtocolShufflePreparationStatus(status: ShufflePreparationStatus): ProtocolShufflePreparationStatus = when (status) {
        ShufflePreparationStatus.SELECTING -> ProtocolShufflePreparationStatus.SELECTING
        ShufflePreparationStatus.GENERATING -> ProtocolShufflePreparationStatus.GENERATING
    }

    fun toProtocolShufflePreparation(prep: ShufflePreparationView): ProtocolShufflePreparation = ProtocolShufflePreparation(
        roundId = prep.roundId,
        status = toProtocolShufflePreparationStatus(prep.status),
        opensAtMillis = prep.opensAtMillis,
        deadlineMillis = prep.deadlineMillis,
        serverNowMillis = prep.serverNowMillis,
        confirmed = prep.confirmed,
        selection = prep.selection?.let { toProtocolShuffleSelection(it) },
        reservedPoints = prep.reservedPoints,
        availablePoints = prep.availablePoints
    )

    fun toEngineShufflePlan(plan: ProtocolShufflePlan): ShufflePlanRequest = ShufflePlanRequest(
        requestId = plan.requestId,
        roundId = plan.roundId,
        controlEpoch = plan.controlEpoch,
        selection = toEngineShuffleSelection(plan.selection)
    )

    fun toProtocolShufflePlan(plan: ShufflePlanRequest): ProtocolShufflePlan = ProtocolShufflePlan(
        requestId = plan.requestId,
        roundId = plan.roundId,
        controlEpoch = plan.controlEpoch,
        selection = toProtocolShuffleSelection(plan.selection)
    )

    // === CheatEconomyConfig / ShuffleConfig / MatchConfig ===

    fun toProtocolCheatEconomyConfig(config: CheatEconomyConfig): ProtocolCheatEconomyConfig = ProtocolCheatEconomyConfig(
        enabled = config.enabled,
        initialPoints = Collections.unmodifiableList(config.initialPoints.toList()),
        costs = Collections.unmodifiableMap(LinkedHashMap(config.costs))
    )

    fun toEngineCheatEconomyConfig(config: ProtocolCheatEconomyConfig): CheatEconomyConfig = CheatEconomyConfig(
        enabled = config.enabled,
        initialPoints = Collections.unmodifiableList(config.initialPoints.toList()),
        costs = Collections.unmodifiableMap(LinkedHashMap(config.costs))
    )

    fun toProtocolShuffleConfig(config: ShuffleConfig): ProtocolShuffleConfig = ProtocolShuffleConfig(
        enabled = config.enabled,
        selectionMillis = config.selectionMillis,
        plannedDraws = config.plannedDraws,
        strength = config.strength,
        interference = config.interference,
        aiMaySpend = config.aiMaySpend
    )

    fun toEngineShuffleConfig(config: ProtocolShuffleConfig): ShuffleConfig = ShuffleConfig(
        enabled = config.enabled,
        selectionMillis = config.selectionMillis,
        plannedDraws = config.plannedDraws,
        strength = config.strength,
        interference = config.interference,
        aiMaySpend = config.aiMaySpend
    )

    // === TileMarkConfig ===

    fun toProtocolTileMarkConfig(config: TileMarkConfig): ProtocolTileMarkConfig =
        ProtocolTileMarkConfig(enabled = config.enabled)

    fun toEngineTileMarkConfig(config: ProtocolTileMarkConfig?): TileMarkConfig =
        if (config == null) TileMarkConfig() else TileMarkConfig(enabled = config.enabled)

    // === SleeveBloomConfig ===

    fun toProtocolSleeveBloomConfig(config: SleeveBloomConfig): ProtocolSleeveBloomConfig =
        ProtocolSleeveBloomConfig(enabled = config.enabled)

    fun toEngineSleeveBloomConfig(config: ProtocolSleeveBloomConfig?): SleeveBloomConfig =
        if (config == null) SleeveBloomConfig() else SleeveBloomConfig(enabled = config.enabled)

    // === BeamSwapConfig ===

    fun toProtocolBeamSwapConfig(config: BeamSwapConfig): ProtocolBeamSwapConfig =
        ProtocolBeamSwapConfig(enabled = config.enabled)

    fun toEngineBeamSwapConfig(config: ProtocolBeamSwapConfig?): BeamSwapConfig =
        if (config == null) BeamSwapConfig() else BeamSwapConfig(enabled = config.enabled)

    fun toProtocolConfig(config: MatchConfig): ProtocolMatchConfig = ProtocolMatchConfig(
        playerCount = config.playerCount,
        handsPerWind = config.handsPerWind,
        winds = Collections.unmodifiableList(config.winds.toList()),
        startingScores = Collections.unmodifiableList(config.startingScores.toList()),
        allowExtension = config.allowExtension,
        allowAgariYame = config.allowAgariYame,
        bankruptcyEnabled = config.bankruptcyEnabled,
        bankruptcyThreshold = config.bankruptcyThreshold,
        bankruptcyInclusive = config.bankruptcyInclusive,
        targetScore = config.targetScore,
        tieBreakSeats = Collections.unmodifiableList(config.tieBreakSeats.toList()),
        cheatEconomy = toProtocolCheatEconomyConfig(config.cheatEconomy),
        shuffleConfig = toProtocolShuffleConfig(config.shuffleConfig),
        tileMarkConfig = toProtocolTileMarkConfig(config.tileMarkConfig),
        sleeveBloomConfig = toProtocolSleeveBloomConfig(config.sleeveBloomConfig),
        beamSwapConfig = toProtocolBeamSwapConfig(config.beamSwapConfig)
    )

    fun toEngineConfig(config: ProtocolMatchConfig?): MatchConfig {
        if (config == null) return MatchConfig()
        return MatchConfig(
            playerCount = config.playerCount,
            handsPerWind = config.handsPerWind,
            winds = Collections.unmodifiableList(config.winds.toList()),
            startingScores = Collections.unmodifiableList(config.startingScores.toList()),
            allowExtension = config.allowExtension,
            allowAgariYame = config.allowAgariYame,
            bankruptcyEnabled = config.bankruptcyEnabled,
            bankruptcyThreshold = config.bankruptcyThreshold,
            bankruptcyInclusive = config.bankruptcyInclusive,
            targetScore = config.targetScore,
            tieBreakSeats = Collections.unmodifiableList(config.tieBreakSeats.toList()),
            cheatEconomy = toEngineCheatEconomyConfig(config.cheatEconomy),
            shuffleConfig = toEngineShuffleConfig(config.shuffleConfig),
            tileMarkConfig = toEngineTileMarkConfig(config.tileMarkConfig),
            sleeveBloomConfig = toEngineSleeveBloomConfig(config.sleeveBloomConfig),
            beamSwapConfig = toEngineBeamSwapConfig(config.beamSwapConfig)
        )
    }

    fun toProtocolMatchConfig(config: MatchConfig): ProtocolMatchConfig = toProtocolConfig(config)

    fun toEngineTimeControlConfig(config: ProtocolTimeControlConfig?): TimeControlConfig {
        if (config == null) return TimeControlConfig()
        val baseMillis = try {
            Math.multiplyExact(config.turnThinkingSeconds, 1000L)
        } catch (e: ArithmeticException) {
            throw IllegalArgumentException("turnThinkingSeconds overflow: ${config.turnThinkingSeconds}", e)
        }
        val reserveMillis = try {
            Math.multiplyExact(config.roundReserveSeconds, 1000L)
        } catch (e: ArithmeticException) {
            throw IllegalArgumentException("roundReserveSeconds overflow: ${config.roundReserveSeconds}", e)
        }
        return TimeControlConfig(
            baseMillis = baseMillis,
            reserveMillis = reserveMillis,
            reserveScope = ReserveScope.ROUND
        )
    }

    fun toProtocolTimeControlConfig(config: TimeControlConfig): ProtocolTimeControlConfig {
        val roundReserveSeconds = config.reserveMillis / 1000L
        val turnThinkingSeconds = config.baseMillis / 1000L
        return ProtocolTimeControlConfig(
            roundReserveSeconds = roundReserveSeconds,
            turnThinkingSeconds = turnThinkingSeconds
        )
    }

    // === Settlement details ===

    fun toProtocolFuLine(line: FuLine): ProtocolFuLine = ProtocolFuLine(line.description, line.fu)

    fun toProtocolFuBreakdown(fu: FuBreakdown): ProtocolFuBreakdown = ProtocolFuBreakdown(
        lines = Collections.unmodifiableList(fu.lines.map { toProtocolFuLine(it) }),
        subtotal = fu.subtotal,
        roundedUpTo = fu.roundedUpTo,
        finalFu = fu.finalFu,
        specialNote = fu.specialNote
    )

    fun toProtocolDoraIndicatorHit(hit: DoraIndicatorHit): ProtocolDoraIndicatorHit = ProtocolDoraIndicatorHit(
        indicator = toProtocolTile(hit.indicator),
        target = toProtocolTile(hit.target),
        tiles = toProtocolTiles(hit.tiles)
    )

    fun toProtocolDoraBreakdown(dora: DoraBreakdown): ProtocolDoraBreakdown = ProtocolDoraBreakdown(
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

    fun toProtocolScoreResult(score: ScoreCalculator.ScoreResult): ProtocolScoreResult = ProtocolScoreResult(
        fu = score.fu,
        han = score.han,
        basePoints = score.basePoints,
        title = score.title,
        isYakuman = score.isYakuman,
        yakumanMultiplier = score.yakumanMultiplier,
        rawBasePoints = score.rawBasePoints,
        capReason = score.capReason,
        formulaText = score.formulaText,
        rawBasePointsExact = score.rawBasePointsExact
    )

    fun toProtocolScoreBreakdown(breakdown: ScoreBreakdown): ProtocolScoreBreakdown = ProtocolScoreBreakdown(
        yakuLines = Collections.unmodifiableList(breakdown.yakuLines.map { Pair(it.first, it.second) }),
        yakumanCount = breakdown.yakumanCount,
        dora = toProtocolDoraBreakdown(breakdown.dora),
        fu = toProtocolFuBreakdown(breakdown.fu),
        score = toProtocolScoreResult(breakdown.score),
        paymentLines = Collections.unmodifiableList(breakdown.paymentLines.toList()),
        honbaAddition = breakdown.honbaAddition,
        riichiAddition = breakdown.riichiAddition,
        totalPoints = breakdown.totalPoints,
        uraEligible = breakdown.uraEligible,
        isDealer = breakdown.isDealer,
        isTsumo = breakdown.isTsumo
    )

    // === PublicRoundRecord / PublicSessionResult / SessionView ===

    fun toProtocolRoundRecord(rec: PublicRoundRecord): ProtocolPublicRoundRecord = ProtocolPublicRoundRecord(
        roundId = rec.roundId,
        resultId = rec.resultId,
        finalScores = Collections.unmodifiableList(rec.finalScores.toList()),
        deltas = Collections.unmodifiableList(rec.deltas.toList()),
        finalCheatPoints = Collections.unmodifiableList(rec.finalCheatPoints.toList()),
        cheatDeltas = Collections.unmodifiableList(rec.cheatDeltas.toList())
    )

    fun toProtocolSessionResult(res: PublicSessionResult): ProtocolPublicSessionResult = ProtocolPublicSessionResult(
        finalScores = Collections.unmodifiableList(res.finalScores.toList()),
        rankedSeats = Collections.unmodifiableList(res.rankedSeats.toList()),
        reason = res.reason,
        finalCheatPoints = Collections.unmodifiableList(res.finalCheatPoints.toList())
    )

    fun toProtocolSessionView(view: SessionView): ProtocolSessionView = ProtocolSessionView(
        lifecycle = view.lifecycle,
        scores = Collections.unmodifiableList(view.scores.toList()),
        roundWind = view.roundWind,
        roundNumber = view.roundNumber,
        dealerSeat = view.dealerSeat,
        honba = view.honba,
        roundRecords = Collections.unmodifiableList(view.roundRecords.map { toProtocolRoundRecord(it) }),
        finalResult = view.finalResult?.let { toProtocolSessionResult(it) }
    )

    // === PlayerView ===

    fun toProtocolPlayerView(view: PlayerView): ProtocolPlayerView = ProtocolPlayerView(
        viewerSeat = view.viewerSeat,
        phase = toProtocolPhase(view.phase),
        roundWind = view.roundWind,
        roundNumber = view.roundNumber,
        honbaSticks = view.honbaSticks,
        riichiSticks = view.riichiSticks,
        dealerSeat = view.dealerSeat,
        currentTurnPlayer = view.currentTurnPlayer,
        wallRemaining = view.wallRemaining,
        doraIndicators = toProtocolTiles(view.doraIndicators),
        scores = Collections.unmodifiableList(view.scores.toList()),
        riichiStatus = Collections.unmodifiableList(view.riichiStatus.toList()),
        discards = Collections.unmodifiableList(view.discards.map { toProtocolTiles(it) }),
        melds = Collections.unmodifiableList(view.melds.map { toProtocolMelds(it) }),
        handTileCounts = Collections.unmodifiableList(view.handTileCounts.toList()),
        myClosedTiles = toProtocolTiles(view.myClosedTiles),
        myDrawnTile = view.myDrawnTile?.let { toProtocolTile(it) },
        legalActions = Collections.unmodifiableList(view.legalActions.map { toProtocolAction(it) }),
        lastDiscard = view.lastDiscard?.let { toProtocolTile(it) },
        lastDiscardPlayer = view.lastDiscardPlayer,
        mySleeveTiles = toProtocolTiles(view.mySleeveTiles),
        myFuriten = toProtocolFuriten(view.myFuriten),
        myDiscardHistory = toProtocolTiles(view.myDiscardHistory)
    )

    // === GameEvent ===

    fun toProtocolEvent(event: GameEvent): ProtocolGameEvent = when (event) {
        is GameEvent.StackingStarted -> ProtocolGameEvent.StackingStarted(event.turnNumber)
        is GameEvent.WallBuilt -> ProtocolGameEvent.WallBuilt(event.turnNumber, event.wallSize)
        is GameEvent.TileDealt -> ProtocolGameEvent.TileDealt(event.turnNumber, event.player, toProtocolTile(event.tile))
        is GameEvent.DealingComplete -> ProtocolGameEvent.DealingComplete(event.turnNumber)
        is GameEvent.TileDrawn -> ProtocolGameEvent.TileDrawn(event.turnNumber, event.player, toProtocolTile(event.tile), event.wallRemaining)
        is GameEvent.TileDiscarded -> ProtocolGameEvent.TileDiscarded(
            event.turnNumber,
            event.player,
            toProtocolTile(event.tile),
            event.isRiichiDiscard,
            event.isTsumogiri
        )
        is GameEvent.ChiDeclared -> ProtocolGameEvent.ChiDeclared(
            event.turnNumber,
            event.player,
            toProtocolMeld(event.meld),
            event.fromPlayer
        )
        is GameEvent.PonDeclared -> ProtocolGameEvent.PonDeclared(
            event.turnNumber,
            event.player,
            toProtocolMeld(event.meld),
            event.fromPlayer
        )
        is GameEvent.KanDeclared -> ProtocolGameEvent.KanDeclared(
            event.turnNumber,
            event.player,
            toProtocolMeld(event.meld),
            toProtocolMeldType(event.kanType)
        )
        is GameEvent.RinshanDraw -> ProtocolGameEvent.RinshanDraw(event.turnNumber, event.player, toProtocolTile(event.tile))
        is GameEvent.RiichiDeclared -> ProtocolGameEvent.RiichiDeclared(
            event.turnNumber,
            event.player,
            toProtocolTile(event.discardTile),
            event.isDoubleRiichi
        )
        is GameEvent.TsumoWin -> ProtocolGameEvent.TsumoWin(
            event.turnNumber,
            event.player,
            toProtocolTile(event.winTile),
            Collections.unmodifiableList(event.yakuList.toList()),
            event.han,
            event.fu,
            event.score,
            event.breakdown?.let { toProtocolScoreBreakdown(it) }
        )
        is GameEvent.RonWin -> ProtocolGameEvent.RonWin(
            event.turnNumber,
            event.winner,
            event.loser,
            toProtocolTile(event.winTile),
            Collections.unmodifiableList(event.yakuList.toList()),
            event.han,
            event.fu,
            event.score,
            event.breakdown?.let { toProtocolScoreBreakdown(it) }
        )
        is GameEvent.DoraRevealed -> ProtocolGameEvent.DoraRevealed(event.turnNumber, toProtocolTile(event.indicator), event.isUraDora)
        is GameEvent.CheatPointsChanged -> ProtocolGameEvent.CheatPointsChanged(event.turnNumber, event.seat, event.amount, event.newBalance)
        is GameEvent.DrawGame -> ProtocolGameEvent.DrawGame(
            event.turnNumber,
            toProtocolDrawReason(event.reason),
            Collections.unmodifiableList(event.tenpaiPlayers.toList())
        )
        is GameEvent.ScoreSettled -> ProtocolGameEvent.ScoreSettled(
            event.turnNumber,
            Collections.unmodifiableMap(LinkedHashMap(event.payments)),
            Collections.unmodifiableList(event.newScores.toList())
        )
        is GameEvent.RoundEnded -> ProtocolGameEvent.RoundEnded(event.turnNumber, event.roundWind, event.roundNumber)
        is GameEvent.MatchEnded -> ProtocolGameEvent.MatchEnded(
            event.turnNumber,
            Collections.unmodifiableList(event.finalScores.toList()),
            event.winner
        )
    }

    fun toProtocolEvents(events: List<GameEvent>): List<ProtocolGameEvent> =
        Collections.unmodifiableList(events.map { toProtocolEvent(it) })

    // === Presentation Preview ===

    fun toProtocolRiichiPreviewOption(opt: RiichiPreviewOption): ProtocolRiichiPreviewOption =
        ProtocolRiichiPreviewOption(
            optionId = opt.optionId,
            action = toProtocolAction(opt.action) as ProtocolLegalAction.DeclareRiichi,
            waits = toProtocolTiles(opt.waits),
            furiten = toProtocolFuriten(opt.furiten)
        )

    fun toEngineRiichiPreviewOption(proto: ProtocolRiichiPreviewOption): RiichiPreviewOption =
        RiichiPreviewOption(
            optionId = proto.optionId,
            action = toEngineAction(proto.action) as GameAction.DeclareRiichi,
            waits = toEngineTiles(proto.waits),
            furiten = toEngineFuriten(proto.furiten)
        )

    fun toProtocolMeldPreviewOption(opt: MeldPreviewOption): ProtocolMeldPreviewOption =
        ProtocolMeldPreviewOption(
            optionId = opt.optionId,
            action = toProtocolAction(opt.action),
            type = toProtocolMeldType(opt.type),
            tiles = toProtocolTiles(opt.tiles),
            handTiles = toProtocolTiles(opt.handTiles),
            calledTile = opt.calledTile?.let { toProtocolTile(it) },
            addedTile = opt.addedTile?.let { toProtocolTile(it) },
            fromPlayer = opt.fromPlayer
        )

    fun toEngineMeldPreviewOption(proto: ProtocolMeldPreviewOption): MeldPreviewOption =
        MeldPreviewOption(
            optionId = proto.optionId,
            action = toEngineAction(proto.action),
            type = toEngineMeldType(proto.type),
            tiles = toEngineTiles(proto.tiles),
            handTiles = toEngineTiles(proto.handTiles),
            calledTile = proto.calledTile?.let { toEngineTile(it) },
            addedTile = proto.addedTile?.let { toEngineTile(it) },
            fromPlayer = proto.fromPlayer
        )

    fun toProtocolPresentationPreview(preview: PlayerPresentationPreview): ProtocolPlayerPresentationPreview =
        ProtocolPlayerPresentationPreview(
            roundId = preview.roundId,
            decisionId = preview.decisionId,
            controlEpoch = preview.controlEpoch,
            waits = toProtocolTiles(preview.waits),
            riichiOptions = Collections.unmodifiableList(preview.riichiOptions.map { toProtocolRiichiPreviewOption(it) }),
            meldOptions = Collections.unmodifiableList(preview.meldOptions.map { toProtocolMeldPreviewOption(it) })
        )

    fun toEnginePresentationPreview(proto: ProtocolPlayerPresentationPreview): PlayerPresentationPreview =
        PlayerPresentationPreview(
            roundId = proto.roundId,
            decisionId = proto.decisionId,
            controlEpoch = proto.controlEpoch,
            waits = toEngineTiles(proto.waits),
            riichiOptions = Collections.unmodifiableList(proto.riichiOptions.map { toEngineRiichiPreviewOption(it) }),
            meldOptions = Collections.unmodifiableList(proto.meldOptions.map { toEngineMeldPreviewOption(it) })
        )

    // === PlayerSnapshot ===

    // === TileMark ===

    fun toEngineTileMarkRequest(request: ProtocolTileMarkRequest): TileMarkRequest = TileMarkRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch,
        tileIds = Collections.unmodifiableList(ArrayList(request.tileIds))
    )

    fun toProtocolTileMarkRequest(request: TileMarkRequest): ProtocolTileMarkRequest = ProtocolTileMarkRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch,
        tileIds = Collections.unmodifiableList(ArrayList(request.tileIds))
    )

    fun toProtocolTileMarkArea(area: TileMarkArea): ProtocolTileMarkArea = when (area) {
        TileMarkArea.OWN_HAND -> ProtocolTileMarkArea.OWN_HAND
        TileMarkArea.OPPONENT_HAND -> ProtocolTileMarkArea.OPPONENT_HAND
        TileMarkArea.DISCARD -> ProtocolTileMarkArea.DISCARD
        TileMarkArea.EXPOSED_MELD -> ProtocolTileMarkArea.EXPOSED_MELD
        TileMarkArea.EXPOSED_INDICATOR -> ProtocolTileMarkArea.EXPOSED_INDICATOR
        TileMarkArea.NOT_VISIBLE -> ProtocolTileMarkArea.NOT_VISIBLE
    }

    fun toEngineTileMarkArea(area: ProtocolTileMarkArea): TileMarkArea = when (area) {
        ProtocolTileMarkArea.OWN_HAND -> TileMarkArea.OWN_HAND
        ProtocolTileMarkArea.OPPONENT_HAND -> TileMarkArea.OPPONENT_HAND
        ProtocolTileMarkArea.DISCARD -> TileMarkArea.DISCARD
        ProtocolTileMarkArea.EXPOSED_MELD -> TileMarkArea.EXPOSED_MELD
        ProtocolTileMarkArea.EXPOSED_INDICATOR -> TileMarkArea.EXPOSED_INDICATOR
        ProtocolTileMarkArea.NOT_VISIBLE -> TileMarkArea.NOT_VISIBLE
    }

    fun toProtocolRiverPosition(pos: RiverPosition): ProtocolRiverPosition = ProtocolRiverPosition(
        roundId = pos.roundId,
        seat = pos.seat,
        discardIndex = pos.discardIndex
    )

    fun toEngineRiverPosition(pos: ProtocolRiverPosition): RiverPosition = RiverPosition(
        roundId = pos.roundId,
        seat = pos.seat,
        discardIndex = pos.discardIndex
    )

    fun toProtocolMarkedTile(view: MarkedTileView): ProtocolMarkedTileView = ProtocolMarkedTileView(
        tileId = view.tileId,
        tile = toProtocolTile(view.tile),
        area = toProtocolTileMarkArea(view.area),
        seat = view.seat,
        riverPosition = view.riverPosition?.let { toProtocolRiverPosition(it) }
    )

    fun toEngineMarkedTile(proto: ProtocolMarkedTileView): MarkedTileView = MarkedTileView(
        tileId = proto.tileId,
        tile = toEngineTile(proto.tile),
        area = toEngineTileMarkArea(proto.area),
        seat = proto.seat,
        riverPosition = proto.riverPosition?.let { toEngineRiverPosition(it) }
    )

    fun toProtocolTileMarkGroup(group: TileMarkGroupView): ProtocolTileMarkGroupView = ProtocolTileMarkGroupView(
        groupId = group.groupId,
        createdRoundId = group.createdRoundId,
        createdRoundOrdinal = group.createdRoundOrdinal,
        expiresAfterRoundOrdinal = group.expiresAfterRoundOrdinal,
        futureRoundsRemaining = group.futureRoundsRemaining,
        isLastEffectiveRound = group.isLastEffectiveRound,
        tiles = Collections.unmodifiableList(group.tiles.map { toProtocolMarkedTile(it) })
    )

    fun toEngineTileMarkGroup(proto: ProtocolTileMarkGroupView): TileMarkGroupView = TileMarkGroupView(
        groupId = proto.groupId,
        createdRoundId = proto.createdRoundId,
        createdRoundOrdinal = proto.createdRoundOrdinal,
        expiresAfterRoundOrdinal = proto.expiresAfterRoundOrdinal,
        futureRoundsRemaining = proto.futureRoundsRemaining,
        isLastEffectiveRound = proto.isLastEffectiveRound,
        tiles = Collections.unmodifiableList(proto.tiles.map { toEngineMarkedTile(it) })
    )

    fun toProtocolTileMarkView(view: TileMarkView): ProtocolTileMarkView = ProtocolTileMarkView(
        canApply = view.canApply,
        disabledReason = view.disabledReason,
        cost = view.cost,
        activeTileCount = view.activeTileCount,
        maxActiveTiles = view.maxActiveTiles,
        selectableTileIds = Collections.unmodifiableList(ArrayList(view.selectableTileIds)),
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch,
        stateVersion = view.stateVersion,
        activeGroups = Collections.unmodifiableList(view.activeGroups.map { toProtocolTileMarkGroup(it) })
    )

    fun toEngineTileMarkView(proto: ProtocolTileMarkView): TileMarkView = TileMarkView(
        canApply = proto.canApply,
        disabledReason = proto.disabledReason,
        cost = proto.cost,
        activeTileCount = proto.activeTileCount,
        maxActiveTiles = proto.maxActiveTiles,
        selectableTileIds = Collections.unmodifiableList(ArrayList(proto.selectableTileIds)),
        decisionId = proto.decisionId,
        controlEpoch = proto.controlEpoch,
        stateVersion = proto.stateVersion,
        activeGroups = Collections.unmodifiableList(proto.activeGroups.map { toEngineTileMarkGroup(it) })
    )

    // === SleeveBloom ===

    fun toProtocolSleeveBloomDrawKind(kind: SleeveBloomDrawKind): ProtocolSleeveBloomDrawKind = when (kind) {
        SleeveBloomDrawKind.DEAL_EXTRA -> ProtocolSleeveBloomDrawKind.DEAL_EXTRA
        SleeveBloomDrawKind.NORMAL -> ProtocolSleeveBloomDrawKind.NORMAL
        SleeveBloomDrawKind.RINSHAN -> ProtocolSleeveBloomDrawKind.RINSHAN
    }

    fun toEngineSleeveBloomDrawKind(kind: ProtocolSleeveBloomDrawKind): SleeveBloomDrawKind = when (kind) {
        ProtocolSleeveBloomDrawKind.DEAL_EXTRA -> SleeveBloomDrawKind.DEAL_EXTRA
        ProtocolSleeveBloomDrawKind.NORMAL -> SleeveBloomDrawKind.NORMAL
        ProtocolSleeveBloomDrawKind.RINSHAN -> SleeveBloomDrawKind.RINSHAN
    }

    fun toProtocolSleeveBloomKeep(keep: SleeveBloomKeep): ProtocolSleeveBloomKeep = when (keep) {
        SleeveBloomKeep.FIRST -> ProtocolSleeveBloomKeep.FIRST
        SleeveBloomKeep.SECOND -> ProtocolSleeveBloomKeep.SECOND
    }

    fun toEngineSleeveBloomKeep(keep: ProtocolSleeveBloomKeep): SleeveBloomKeep = when (keep) {
        ProtocolSleeveBloomKeep.FIRST -> SleeveBloomKeep.FIRST
        ProtocolSleeveBloomKeep.SECOND -> SleeveBloomKeep.SECOND
    }

    fun toEngineSleeveBloomRequest(request: ProtocolSleeveBloomRequest): SleeveBloomRequest = SleeveBloomRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch
    )

    fun toProtocolSleeveBloomRequest(request: SleeveBloomRequest): ProtocolSleeveBloomRequest = ProtocolSleeveBloomRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch
    )

    fun toEngineSleeveBloomChoiceRequest(request: ProtocolSleeveBloomChoiceRequest): SleeveBloomChoiceRequest = SleeveBloomChoiceRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch,
        offerId = request.offerId,
        keep = toEngineSleeveBloomKeep(request.keep)
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

    fun toProtocolSleeveBloomOfferView(view: SleeveBloomOfferView): ProtocolSleeveBloomOfferView = ProtocolSleeveBloomOfferView(
        offerId = view.offerId,
        firstTile = toProtocolTile(view.firstTile),
        secondTile = toProtocolTile(view.secondTile),
        drawKind = toProtocolSleeveBloomDrawKind(view.drawKind),
        deadlineMillis = view.deadlineMillis,
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch
    )

    fun toEngineSleeveBloomOfferView(proto: ProtocolSleeveBloomOfferView): SleeveBloomOfferView = SleeveBloomOfferView(
        offerId = proto.offerId,
        firstTile = toEngineTile(proto.firstTile),
        secondTile = toEngineTile(proto.secondTile),
        drawKind = toEngineSleeveBloomDrawKind(proto.drawKind),
        deadlineMillis = proto.deadlineMillis,
        decisionId = proto.decisionId,
        controlEpoch = proto.controlEpoch
    )

    fun toProtocolSleeveBloomView(view: SleeveBloomView): ProtocolSleeveBloomView = ProtocolSleeveBloomView(
        canApply = view.canApply,
        disabledReason = view.disabledReason,
        cost = view.cost,
        drawKind = view.drawKind?.let { toProtocolSleeveBloomDrawKind(it) },
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch,
        stateVersion = view.stateVersion,
        pendingOffer = view.pendingOffer?.let { toProtocolSleeveBloomOfferView(it) }
    )

    fun toEngineSleeveBloomView(proto: ProtocolSleeveBloomView): SleeveBloomView = SleeveBloomView(
        canApply = proto.canApply,
        disabledReason = proto.disabledReason,
        cost = proto.cost,
        drawKind = proto.drawKind?.let { toEngineSleeveBloomDrawKind(it) },
        decisionId = proto.decisionId,
        controlEpoch = proto.controlEpoch,
        stateVersion = proto.stateVersion,
        pendingOffer = proto.pendingOffer?.let { toEngineSleeveBloomOfferView(it) }
    )

    // === BeamSwap ===

    fun toProtocolBeamSwapSlot(slot: BeamSwapSlot): ProtocolBeamSwapSlot = when (slot) {
        is BeamSwapSlot.WallSlot -> ProtocolBeamSwapSlot.WallSlot(slot.index)
        is BeamSwapSlot.DeadWallSlot -> ProtocolBeamSwapSlot.DeadWallSlot(slot.index)
        is BeamSwapSlot.ReplacementSlot -> ProtocolBeamSwapSlot.ReplacementSlot(slot.index)
    }

    fun toEngineBeamSwapSlot(slot: ProtocolBeamSwapSlot): BeamSwapSlot = when (slot) {
        is ProtocolBeamSwapSlot.WallSlot -> BeamSwapSlot.WallSlot(slot.index)
        is ProtocolBeamSwapSlot.DeadWallSlot -> BeamSwapSlot.DeadWallSlot(slot.index)
        is ProtocolBeamSwapSlot.ReplacementSlot -> BeamSwapSlot.ReplacementSlot(slot.index)
    }

    fun toProtocolBeamSwapWindow(window: BeamSwapWindow): ProtocolBeamSwapWindow = when (window) {
        BeamSwapWindow.WALL -> ProtocolBeamSwapWindow.WALL
        BeamSwapWindow.DEAD_WALL -> ProtocolBeamSwapWindow.DEAD_WALL
    }

    fun toEngineBeamSwapWindow(window: ProtocolBeamSwapWindow): BeamSwapWindow = when (window) {
        ProtocolBeamSwapWindow.WALL -> BeamSwapWindow.WALL
        ProtocolBeamSwapWindow.DEAD_WALL -> BeamSwapWindow.DEAD_WALL
    }

    fun toProtocolBeamSwapAction(action: BeamSwapAction): ProtocolBeamSwapAction = when (action) {
        BeamSwapAction.SWAP -> ProtocolBeamSwapAction.SWAP
        BeamSwapAction.KEEP_ORIGINAL -> ProtocolBeamSwapAction.KEEP_ORIGINAL
    }

    fun toEngineBeamSwapAction(action: ProtocolBeamSwapAction): BeamSwapAction = when (action) {
        ProtocolBeamSwapAction.SWAP -> BeamSwapAction.SWAP
        ProtocolBeamSwapAction.KEEP_ORIGINAL -> BeamSwapAction.KEEP_ORIGINAL
    }

    fun toEngineBeamSwapRequest(request: ProtocolBeamSwapRequest): BeamSwapRequest = BeamSwapRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch
    )

    fun toProtocolBeamSwapRequest(request: BeamSwapRequest): ProtocolBeamSwapRequest = ProtocolBeamSwapRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch
    )

    fun toEngineBeamSwapChoiceRequest(request: ProtocolBeamSwapChoiceRequest): BeamSwapChoiceRequest = BeamSwapChoiceRequest(
        requestId = request.requestId,
        sessionId = request.sessionId,
        roundId = request.roundId,
        decisionId = request.decisionId,
        controlEpoch = request.controlEpoch,
        offerId = request.offerId,
        action = toEngineBeamSwapAction(request.action),
        handTileId = request.handTileId,
        targetSlot = request.targetSlot?.let { toEngineBeamSwapSlot(it) }
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

    fun toProtocolBeamSwapCandidateView(candidate: BeamSwapCandidateView): ProtocolBeamSwapCandidateView = ProtocolBeamSwapCandidateView(
        slot = toProtocolBeamSwapSlot(candidate.slot),
        marked = candidate.marked,
        tile = candidate.tile?.let { toProtocolTile(it) }
    )

    fun toEngineBeamSwapCandidateView(candidate: ProtocolBeamSwapCandidateView): BeamSwapCandidateView = BeamSwapCandidateView(
        slot = toEngineBeamSwapSlot(candidate.slot),
        marked = candidate.marked,
        tile = candidate.tile?.let { toEngineTile(it) }
    )

    fun toProtocolBeamSwapOfferView(view: BeamSwapOfferView): ProtocolBeamSwapOfferView = ProtocolBeamSwapOfferView(
        offerId = view.offerId,
        window = toProtocolBeamSwapWindow(view.window),
        candidates = Collections.unmodifiableList(view.candidates.map { toProtocolBeamSwapCandidateView(it) }),
        selectableHandTileIds = Collections.unmodifiableList(ArrayList(view.selectableHandTileIds)),
        deadlineMillis = view.deadlineMillis,
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch
    )

    fun toEngineBeamSwapOfferView(proto: ProtocolBeamSwapOfferView): BeamSwapOfferView = BeamSwapOfferView(
        offerId = proto.offerId,
        window = toEngineBeamSwapWindow(proto.window),
        candidates = Collections.unmodifiableList(proto.candidates.map { toEngineBeamSwapCandidateView(it) }),
        selectableHandTileIds = Collections.unmodifiableList(ArrayList(proto.selectableHandTileIds)),
        deadlineMillis = proto.deadlineMillis,
        decisionId = proto.decisionId,
        controlEpoch = proto.controlEpoch
    )

    fun toProtocolBeamSwapView(view: BeamSwapView): ProtocolBeamSwapView = ProtocolBeamSwapView(
        canApply = view.canApply,
        disabledReason = view.disabledReason,
        cost = view.cost,
        window = view.window?.let { toProtocolBeamSwapWindow(it) },
        decisionId = view.decisionId,
        controlEpoch = view.controlEpoch,
        stateVersion = view.stateVersion,
        pendingOffer = view.pendingOffer?.let { toProtocolBeamSwapOfferView(it) }
    )

    fun toEngineBeamSwapView(proto: ProtocolBeamSwapView): BeamSwapView = BeamSwapView(
        canApply = proto.canApply,
        disabledReason = proto.disabledReason,
        cost = proto.cost,
        window = proto.window?.let { toEngineBeamSwapWindow(it) },
        decisionId = proto.decisionId,
        controlEpoch = proto.controlEpoch,
        stateVersion = proto.stateVersion,
        pendingOffer = proto.pendingOffer?.let { toEngineBeamSwapOfferView(it) }
    )

    // === PlayerSnapshot ===

    fun toProtocolSnapshot(snap: PlayerSnapshot): ProtocolPlayerSnapshot = ProtocolPlayerSnapshot(
        sessionId = snap.sessionId,
        roundId = snap.roundId,
        syncEpoch = snap.syncEpoch,
        cursor = snap.cursor,
        sessionVersion = snap.sessionVersion,
        roundVersion = snap.roundVersion,
        view = toProtocolPlayerView(snap.view),
        decision = toProtocolDecision(snap.decision),
        clocks = Collections.unmodifiableList(snap.clocks.map { toProtocolClock(it) }),
        cheatPoints = Collections.unmodifiableList(snap.cheatPoints.toList()),
        session = snap.session?.let { toProtocolSessionView(it) },
        controls = Collections.unmodifiableList(snap.controls.map { toProtocolSeatControl(it) }),
        paused = snap.paused,
        settlementRoundId = snap.settlementRoundId,
        settlementEvents = toProtocolEvents(snap.settlementEvents),
        preparation = snap.preparation?.let { toProtocolShufflePreparation(it) },
        presentationPreview = snap.presentationPreview?.let { toProtocolPresentationPreview(it) },
        tileMark = snap.tileMark?.let { toProtocolTileMarkView(it) },
        sleeveBloom = snap.sleeveBloom?.let { toProtocolSleeveBloomView(it) },
        beamSwap = snap.beamSwap?.let { toProtocolBeamSwapView(it) }
    )

    // === PlayerUpdate ===

    fun toProtocolUpdate(update: PlayerUpdate): ProtocolPlayerUpdate = ProtocolPlayerUpdate(
        snapshot = toProtocolSnapshot(update.snapshot),
        fromCursor = update.fromCursor,
        visibleEvents = toProtocolEvents(update.visibleEvents)
    )

    // === Public Snapshot ===

    fun toProtocolPublicSnapshot(
        proj: com.majiang.projection.PublicTableProjection,
        syncEpoch: String,
        cursor: Long
    ): com.majiang.transport.api.ProtocolPublicSnapshot =
        PublicProtocolMapper.toProtocolPublicSnapshot(proj, syncEpoch, cursor)
}
