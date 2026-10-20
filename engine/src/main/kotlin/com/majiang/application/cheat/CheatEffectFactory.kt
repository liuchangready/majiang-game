package com.majiang.application.cheat

import com.majiang.cheat.beam.BeamSwapCandidate
import com.majiang.cheat.beam.BeamSwapOffer
import com.majiang.cheat.beam.BeamSwapWindow
import com.majiang.cheat.bloom.SleeveBloomDrawKind
import com.majiang.cheat.bloom.SleeveBloomOffer
import com.majiang.cheat.mark.TileMarkGroup
import com.majiang.model.Tile

/**
 * 千术效果对象的纯构造能力（D-09）。
 *
 * 只构造不可变/防御复制后的组、offer 与标识，不安装状态、不扣费、不发事件。
 * 调用方仍在 ApplicationCoordinator 原有事务位置完成 install 与事件发布。
 */
internal object CheatEffectFactory {

    fun tileMarkGroup(
        seat: Int,
        requestId: String,
        tileIds: List<Int>,
        roundId: String,
        dealtRoundOrdinal: Int
    ): TileMarkGroup = TileMarkGroup(
        groupId = "mark-$seat-$requestId",
        ownerSeat = seat,
        tileIds = tileIds,
        createdRoundId = roundId,
        createdRoundOrdinal = dealtRoundOrdinal,
        expiresAfterRoundOrdinal = dealtRoundOrdinal + 3
    )

    fun sleeveBloomOffer(
        seat: Int,
        requestId: String,
        drawKind: SleeveBloomDrawKind,
        firstTile: Tile,
        secondTile: Tile,
        turnNumber: Int,
        mode: com.majiang.cheat.bloom.SleeveBloomOfferMode =
            com.majiang.cheat.bloom.SleeveBloomOfferMode.IMMEDIATE,
        windowId: String? = null,
        deadlineMillis: Long = 0L,
        createdAtMillis: Long = 0L,
        firstCanTsumo: Boolean = false,
        secondCanTsumo: Boolean = false,
        firstCanKan: Boolean = false,
        secondCanKan: Boolean = false
    ): SleeveBloomOffer = SleeveBloomOffer(
        offerId = "bloom-$seat-$requestId",
        seat = seat,
        drawKind = drawKind,
        firstTile = firstTile,
        secondTile = secondTile,
        createdTurnNumber = turnNumber,
        mode = mode,
        windowId = windowId,
        deadlineMillis = deadlineMillis,
        createdAtMillis = createdAtMillis,
        firstCanTsumo = firstCanTsumo,
        secondCanTsumo = secondCanTsumo,
        firstCanKan = firstCanKan,
        secondCanKan = secondCanKan
    )

    fun beamSwapOffer(
        seat: Int,
        requestId: String,
        window: BeamSwapWindow,
        candidates: List<BeamSwapCandidate>,
        eligibleHandTileIds: List<Int>,
        drawnTileId: Int,
        turnNumber: Int
    ): BeamSwapOffer = BeamSwapOffer(
        offerId = "beam-$seat-$requestId",
        seat = seat,
        window = window,
        candidates = candidates,
        eligibleHandTileIds = eligibleHandTileIds,
        drawnTileId = drawnTileId,
        createdTurnNumber = turnNumber
    )
}
