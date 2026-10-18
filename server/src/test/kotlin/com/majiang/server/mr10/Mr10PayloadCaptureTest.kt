package com.majiang.server.mr10

import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.inprocess.InProcessServer
import com.majiang.transport.api.*
import org.junit.jupiter.api.Test
import java.io.File
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import kotlin.test.assertNotNull

class Mr10PayloadCaptureTest {

    private fun <T> CompletableFuture<T>.await(): T = get(10, TimeUnit.SECONDS)

    private fun primaryOption(snap: ProtocolPlayerSnapshot): ProtocolActionOption? {
        val opts = snap.decision.options
        return opts.firstOrNull { it.action is ProtocolLegalAction.DeclareTsumo || it.action is ProtocolLegalAction.DeclareRon }
            ?: opts.firstOrNull { it.action is ProtocolLegalAction.DeclareRiichi }
            ?: opts.firstOrNull { it.action is ProtocolLegalAction.DiscardTile }
            ?: opts.firstOrNull { it.action is ProtocolLegalAction.Pass }
            ?: opts.firstOrNull()
    }

    @Test
    fun `capture real phase payloads from InProcessServer authority`() {
        var earlyGameUpdate: ProtocolPlayerUpdate? = null
        var midGameUpdate: ProtocolPlayerUpdate? = null
        var lateGameUpdate: ProtocolPlayerUpdate? = null
        var tileMarkUpdate: ProtocolPlayerUpdate? = null
        var candidateUpdate: ProtocolPlayerUpdate? = null
        var settlementUpdate: ProtocolPlayerUpdate? = null

        InProcessServer(isSingleplayer = false).use { server ->
            val connections = (0..3).map { server.createConnection() }
            val config = ProtocolMatchConfig(
                cheatEconomy = ProtocolCheatEconomyConfig(
                    true, List(4) { 100 },
                    mapOf("TILE_MARK" to 10, "SLEEVE_BLOOM" to 10, "BEAM_SWAP" to 10)
                ),
                tileMarkConfig = ProtocolTileMarkConfig(true),
                sleeveBloomConfig = ProtocolSleeveBloomConfig(true),
                beamSwapConfig = ProtocolBeamSwapConfig(true)
            )
            val bound = connections[0].createRoom(CreateRoomRequest(playerName = "P0", config = config)).await()
            for (seat in 1..3) {
                connections[seat].joinRoom(JoinRoomRequest(bound.roomId, "P$seat")).await()
            }
            for (c in connections) c.setReady(true).await()
            val startReceipt = connections[0].start().await()
            check(startReceipt.status == ProtocolReceiptStatus.APPLIED)

            connections[0].onUpdate { u ->
                val snap = u.snapshot
                if (snap != null) {
                    if (earlyGameUpdate == null && snap.view.wallRemaining >= 65) {
                        earlyGameUpdate = u
                    }
                    if (midGameUpdate == null && snap.view.wallRemaining in 35..55 && snap.view.discards[0].isNotEmpty()) {
                        midGameUpdate = u
                    }
                    if (lateGameUpdate == null && snap.view.wallRemaining in 5..25) {
                        lateGameUpdate = u
                    }
                    if (candidateUpdate == null && snap.decision.options.size >= 2) {
                        candidateUpdate = u
                    }
                    if (settlementUpdate == null && snap.settlementRoundId != null) {
                        settlementUpdate = u
                    }
                }
            }

            var settled = false
            var moves = 0
            val moveBudget = 3000
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(60)
            var cheatDone = false

            while (!settled && moves < moveBudget && System.nanoTime() < deadline) {
                var acted = false
                for (seat in 0..3) {
                    if (settled) break
                    val snap = try {
                        connections[seat].snapshot().await()
                    } catch (_: Exception) {
                        continue
                    }
                    if (snap.settlementRoundId != null) {
                        settled = true
                        if (settlementUpdate == null) {
                            settlementUpdate = ProtocolPlayerUpdate(snap, snap.cursor, emptyList(), true)
                        }
                        break
                    }

                    // 1. 码牌准备阶段
                    val prep = snap.preparation
                    if (prep != null) {
                        if (prep.selection == null) {
                            val plan = ProtocolShufflePlan(
                                requestId = "plan-$seat-$moves",
                                roundId = snap.roundId ?: "",
                                controlEpoch = snap.decision.controlEpoch,
                                selection = ProtocolShuffleSelection()
                            )
                            val receipt = connections[seat].submitShufflePlan(plan).await()
                            if (receipt.status == ProtocolReceiptStatus.RECORDED || receipt.status == ProtocolReceiptStatus.APPLIED) {
                                moves++
                                acted = true
                            }
                        }
                        continue
                    }

                    // 2. 挂起 offer
                    val bloomOffer = snap.sleeveBloom?.pendingOffer
                    if (bloomOffer != null) {
                        val choiceReq = ProtocolSleeveBloomChoiceRequest(
                            requestId = "bloom-choice-$seat-$moves",
                            sessionId = snap.sessionId,
                            roundId = snap.roundId ?: "",
                            decisionId = snap.decision.decisionId,
                            controlEpoch = snap.decision.controlEpoch,
                            offerId = bloomOffer.offerId,
                            keep = ProtocolSleeveBloomKeep.FIRST
                        )
                        val receipt = connections[seat].submitSleeveBloomChoice(choiceReq).await()
                        if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                            moves++
                            acted = true
                        }
                        continue
                    }

                    val beamOffer = snap.beamSwap?.pendingOffer
                    if (beamOffer != null) {
                        val choiceReq = ProtocolBeamSwapChoiceRequest(
                            requestId = "beam-choice-$seat-$moves",
                            sessionId = snap.sessionId,
                            roundId = snap.roundId ?: "",
                            decisionId = snap.decision.decisionId,
                            controlEpoch = snap.decision.controlEpoch,
                            offerId = beamOffer.offerId,
                            action = ProtocolBeamSwapAction.KEEP_ORIGINAL
                        )
                        val receipt = connections[seat].submitBeamSwapChoice(choiceReq).await()
                        if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                            moves++
                            acted = true
                        }
                        continue
                    }

                    // 3. 出千
                    if (!cheatDone && seat == 0 && snap.tileMark?.canApply == true && snap.view.phase == ProtocolGamePhase.CHEAT_WINDOW) {
                        val tiles = (snap.view.myClosedTiles + listOfNotNull(snap.view.myDrawnTile)).take(4).map { it.tileId }
                        if (tiles.size == 4) {
                            val markReq = ProtocolTileMarkRequest(
                                requestId = "mark-$moves",
                                sessionId = snap.sessionId,
                                roundId = snap.roundId!!,
                                decisionId = snap.decision.decisionId,
                                controlEpoch = snap.decision.controlEpoch,
                                tileIds = tiles
                            )
                            val receipt = connections[0].submitTileMark(markReq).await()
                            if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                                moves++
                                acted = true
                                cheatDone = true
                                val updatedSnap = connections[0].snapshot().await()
                                tileMarkUpdate = ProtocolPlayerUpdate(updatedSnap, updatedSnap.cursor, emptyList(), true)
                            }
                        }
                    }

                    // 4. 常规打牌动作
                    val opt = primaryOption(snap) ?: continue
                    val choice = ProtocolActionChoice(
                        requestId = "act-$moves-$seat",
                        roundId = snap.roundId,
                        decisionId = snap.decision.decisionId,
                        controlEpoch = snap.decision.controlEpoch,
                        optionId = opt.optionId
                    )
                    val receipt = connections[seat].submit(choice).await()
                    if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                        moves++
                        acted = true
                    }
                }

                if (!acted) {
                    runCatching { server.tick().get(500, TimeUnit.MILLISECONDS) }
                    Thread.sleep(5)
                } else {
                    runCatching { server.tick().get(500, TimeUnit.MILLISECONDS) }
                }
            }
        }

        println("Capture completed:")
        println("earlyGameUpdate: " + (earlyGameUpdate != null))
        println("midGameUpdate: " + (midGameUpdate != null))
        println("lateGameUpdate: " + (lateGameUpdate != null))
        println("tileMarkUpdate: " + (tileMarkUpdate != null))
        println("candidateUpdate: " + (candidateUpdate != null))
        println("settlementUpdate: " + (settlementUpdate != null))

        assertNotNull(earlyGameUpdate, "earlyGameUpdate must be captured")
        assertNotNull(midGameUpdate, "midGameUpdate must be captured")
        assertNotNull(lateGameUpdate, "lateGameUpdate must be captured")
        assertNotNull(tileMarkUpdate, "tileMarkUpdate must be captured")
        assertNotNull(candidateUpdate, "candidateUpdate must be captured")
        assertNotNull(settlementUpdate, "settlementUpdate must be captured")

        val payloads = mapOf(
            "EARLY_GAME" to ApiMessageCodec.encodePush(earlyGameUpdate!!, isFullSnapshot = true),
            "MID_GAME" to ApiMessageCodec.encodePush(midGameUpdate!!, isFullSnapshot = true),
            "LATE_GAME" to ApiMessageCodec.encodePush(lateGameUpdate!!, isFullSnapshot = true),
            "TILE_MARK" to ApiMessageCodec.encodePush(tileMarkUpdate!!, isFullSnapshot = true),
            "CANDIDATES" to ApiMessageCodec.encodePush(candidateUpdate!!, isFullSnapshot = true),
            "SETTLEMENT" to ApiMessageCodec.encodePush(settlementUpdate!!, isFullSnapshot = true)
        )

        val outDir = File("build/mr10-captured-payloads")
        outDir.mkdirs()
        for ((name, json) in payloads) {
            File(outDir, "$name.json").writeText(json)
            println("$name length: ${json.length} chars, ${json.toByteArray(Charsets.UTF_8).size} bytes")
        }
    }
}
