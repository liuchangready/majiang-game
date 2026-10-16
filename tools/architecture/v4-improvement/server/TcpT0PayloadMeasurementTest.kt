package com.majiang.review

import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.SpectatorApi
import com.majiang.server.inprocess.InProcessServer
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolBeamSwapAction
import com.majiang.transport.api.ProtocolBeamSwapChoiceRequest
import com.majiang.transport.api.ProtocolBeamSwapConfig
import com.majiang.transport.api.ProtocolBeamSwapRequest
import com.majiang.transport.api.ProtocolCheatEconomyConfig
import com.majiang.transport.api.ProtocolGamePhase
import com.majiang.transport.api.ProtocolLegalAction
import com.majiang.transport.api.ProtocolMatchConfig
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPublicSnapshot
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolShuffleSelection
import com.majiang.transport.api.ProtocolSleeveBloomChoiceRequest
import com.majiang.transport.api.ProtocolSleeveBloomConfig
import com.majiang.transport.api.ProtocolSleeveBloomKeep
import com.majiang.transport.api.ProtocolSleeveBloomRequest
import com.majiang.transport.api.ProtocolTileMarkConfig
import com.majiang.transport.api.ProtocolTileMarkRequest
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpWire
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import kotlin.test.assertTrue

/**
 * T0 载荷实测：真实四人 + 三绝技 + 观战整局，测量 TCP 线路各消息类型的最大 JSON 载荷。
 *
 * 目的（检视报告 F14 / 设计 §4）：
 * 1. 验证 1 MiB 单帧上限对真实最大载荷有 ≥25% 余量（max * 4/3 <= 1,048,576）。
 * 2. 记录每成员单局副作用提交数，复核 UC-R06 预算（512/成员、在途 64）。
 * 序列化与 TCP 线路同源（ApiMessageCodec/TcpMessageCodec 的同一组 Gson 适配器）。
 */
@Timeout(180)
class TcpT0PayloadMeasurementTest {

    private class Stats {
        val max = HashMap<String, Int>()
        val count = HashMap<String, Int>()
        @Synchronized fun record(kind: String, bytes: ByteArray) {
            count.merge(kind, 1, Int::plus)
            if (bytes.size > max.getOrDefault(kind, 0)) max[kind] = bytes.size
        }
        @Synchronized fun report(): String =
            max.keys.sorted().joinToString("\n") { k ->
                "  $k: max=${max[k]}B count=${count[k]} (limit=${TcpWire.MAX_PAYLOAD_BYTES}B)"
            }
    }

    private fun <T> CompletableFuture<T>.await(): T = get(10, TimeUnit.SECONDS)

    /** 副作用提交计数（按座位，含码牌/出千/打牌 APPLIED 与 RECORDED），用于 UC-R06 预算复核 */
    private val sideEffectSubmits = IntArray(4)

    @Test
    fun `full four-player cheat game with spectator fits wire payload budget`() {
        val stats = Stats()
        InProcessServer(isSingleplayer = false).use { server ->
            val connections = (0..3).map { server.createConnection() }
            val spectator = server.createConnection()
            val config = ProtocolMatchConfig(
                cheatEconomy = ProtocolCheatEconomyConfig(
                    true, List(4) { 100 },
                    mapOf("TILE_MARK" to 10, "SLEEVE_BLOOM" to 10, "BEAM_SWAP" to 10)
                ),
                tileMarkConfig = ProtocolTileMarkConfig(true),
                sleeveBloomConfig = ProtocolSleeveBloomConfig(true),
                beamSwapConfig = ProtocolBeamSwapConfig(true)
            )
            try {
                val bound = connections[0].createRoom(CreateRoomRequest(playerName = "P0", config = config)).await()
                for (seat in 1..3) {
                    connections[seat].joinRoom(JoinRoomRequest(bound.roomId, "P$seat")).await()
                }
                spectator.joinSpectator(JoinSpectatorRequest(bound.roomId, spectatorName = "Observer")).await()
                for (c in connections) c.setReady(true).await()
                check(connections[0].start().await().status == ProtocolReceiptStatus.APPLIED)

                // 推送监听：全部连接 + 观战，按线路同一 Gson 适配器序列化计量
                val hooks = ArrayList<AutoCloseable>()
                for (c in connections + spectator) {
                    hooks += c.onUpdate { u: ProtocolPlayerUpdate ->
                        stats.record("PLAYER_UPDATE", ApiMessageCodec.encodePush(u, isFullSnapshot = u.isFullSnapshot).toByteArray())
                    }
                    hooks += (c as SpectatorApi).onPublicUpdate { u: ProtocolPublicUpdate ->
                        stats.record("PUBLIC_UPDATE", ApiMessageCodec.encodePublicUpdatePush(u).toByteArray())
                    }
                }

                // 房间信息帧（ROOM_UPDATE 同一序列化路径）
                stats.record("ROOM_UPDATE", ApiMessageCodec.gson.toJson(connections[0].getRoomInfo().await()).toByteArray())

                // 大型 RSP：初始全量玩家快照 / 公开快照 / 房间信息
                val firstSnap = connections[0].snapshot().await()
                stats.record("RSP_snapshot", ApiMessageCodec.encodeResponse(ApiResponse(requestId = "m-init", snapshot = firstSnap)).toByteArray())
                val pub = spectator.publicSnapshot().await()
                stats.record("RSP_publicSnapshot", ApiMessageCodec.encodeResponse(ApiResponse(requestId = "m-init", publicSnapshot = pub)).toByteArray())
                stats.record("RSP_roomInfo", ApiMessageCodec.encodeResponse(ApiResponse(requestId = "m-init", roomInfo = connections[0].getRoomInfo().await())).toByteArray())
                // 大型 RSP 下限标定：把公开全量快照包成 full PUBLIC_UPDATE 计量
                stats.record(
                    "PUBLIC_UPDATE(full)",
                    ApiMessageCodec.encodePublicUpdatePush(
                        ProtocolPublicUpdate(pub, pub.cursor, emptyList(), true)
                    ).toByteArray()
                )

                // ---- 机器人整局：码牌准备 -> 出千 -> 和牌 > 打牌 > Pass > 首个，直到结算 ----
                var settled = false
                var settledRoundId: String? = null
                val cheatDone = BooleanArray(3) // 0: tileMark, 1: sleeveBloom, 2: beamSwap
                var moves = 0
                val moveBudget = 4000
                var rejectedSubmits = 0
                val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(150)

                while (!settled && moves < moveBudget && System.nanoTime() < deadline) {
                    var acted = false
                    for (seat in 0..3) {
                        if (settled) break
                        val snap = try {
                            connections[seat].snapshot().await()
                        } catch (_: Exception) {
                            continue // 座位可能处于无决策窗口或临时状态
                        }
                        if (snap.settlementRoundId != null) {
                            settled = true
                            settledRoundId = snap.settlementRoundId
                            break
                        }

                        // 1. 码牌准备阶段：若处于准备阶段且该席位尚未提交方案，则提交空做牌方案
                        val prep = snap.preparation
                        if (prep != null) {
                            if (prep.selection == null) {
                                val plan = ProtocolShufflePlan(
                                    requestId = "t0-plan-$seat-$moves",
                                    roundId = snap.roundId ?: "",
                                    controlEpoch = snap.decision.controlEpoch,
                                    selection = ProtocolShuffleSelection()
                                )
                                val receipt = connections[seat].submitShufflePlan(plan).await()
                                if (receipt.status == ProtocolReceiptStatus.RECORDED || receipt.status == ProtocolReceiptStatus.APPLIED) {
                                    sideEffectSubmits[seat]++
                                    moves++
                                    acted = true
                                } else {
                                    rejectedSubmits++
                                }
                            }
                            continue
                        }

                        // 2. 挂起出千 offer 处理（防止阻塞后续出牌决策）
                        val bloomOffer = snap.sleeveBloom?.pendingOffer
                        if (bloomOffer != null) {
                            val choiceReq = ProtocolSleeveBloomChoiceRequest(
                                requestId = "t0-bloom-choice-$seat-$moves",
                                sessionId = snap.sessionId,
                                roundId = snap.roundId ?: "",
                                decisionId = snap.decision.decisionId,
                                controlEpoch = snap.decision.controlEpoch,
                                offerId = bloomOffer.offerId,
                                keep = ProtocolSleeveBloomKeep.FIRST
                            )
                            val receipt = connections[seat].submitSleeveBloomChoice(choiceReq).await()
                            if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                                sideEffectSubmits[seat]++
                                moves++
                                acted = true
                            } else {
                                rejectedSubmits++
                            }
                            continue
                        }

                        val beamOffer = snap.beamSwap?.pendingOffer
                        if (beamOffer != null) {
                            val choiceReq = ProtocolBeamSwapChoiceRequest(
                                requestId = "t0-beam-choice-$seat-$moves",
                                sessionId = snap.sessionId,
                                roundId = snap.roundId ?: "",
                                decisionId = snap.decision.decisionId,
                                controlEpoch = snap.decision.controlEpoch,
                                offerId = beamOffer.offerId,
                                action = ProtocolBeamSwapAction.KEEP_ORIGINAL
                            )
                            val receipt = connections[seat].submitBeamSwapChoice(choiceReq).await()
                            if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                                sideEffectSubmits[seat]++
                                moves++
                                acted = true
                            } else {
                                rejectedSubmits++
                            }
                            continue
                        }

                        // 3. 三绝技触发（正式打牌期 CHEAT_WINDOW 阶段，各触发一次）
                        if (snap.roundId != null && snap.decision.decisionId.isNotBlank() && snap.view.phase == ProtocolGamePhase.CHEAT_WINDOW) {
                            if (!cheatDone[0] && snap.tileMark?.canApply == true) {
                                val tiles = (snap.view.myClosedTiles + listOfNotNull(snap.view.myDrawnTile)).take(4).map { it.tileId }
                                if (tiles.size == 4) {
                                    val markReq = ProtocolTileMarkRequest(
                                        requestId = "t0-mark-$seat-$moves",
                                        sessionId = snap.sessionId,
                                        roundId = snap.roundId!!,
                                        decisionId = snap.decision.decisionId,
                                        controlEpoch = snap.decision.controlEpoch,
                                        tileIds = tiles
                                    )
                                    val receipt = connections[seat].submitTileMark(markReq).await()
                                    if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                                        sideEffectSubmits[seat]++
                                        moves++
                                        acted = true
                                        cheatDone[0] = true
                                    } else {
                                        rejectedSubmits++
                                    }
                                }
                            }
                            if (!cheatDone[1] && snap.view.currentTurnPlayer == seat && snap.view.myDrawnTile != null) {
                                val bloomReq = ProtocolSleeveBloomRequest(
                                    requestId = "t0-bloom-$seat-$moves",
                                    sessionId = snap.sessionId,
                                    roundId = snap.roundId!!,
                                    decisionId = snap.decision.decisionId,
                                    controlEpoch = snap.decision.controlEpoch
                                )
                                val receipt = connections[seat].submitSleeveBloom(bloomReq).await()
                                if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                                    sideEffectSubmits[seat]++
                                    moves++
                                    acted = true
                                    cheatDone[1] = true
                                } else {
                                    rejectedSubmits++
                                }
                            }
                            if (!cheatDone[2] && snap.view.currentTurnPlayer == seat && snap.view.myDrawnTile != null) {
                                val beamReq = ProtocolBeamSwapRequest(
                                    requestId = "t0-beam-$seat-$moves",
                                    sessionId = snap.sessionId,
                                    roundId = snap.roundId!!,
                                    decisionId = snap.decision.decisionId,
                                    controlEpoch = snap.decision.controlEpoch
                                )
                                val receipt = connections[seat].submitBeamSwap(beamReq).await()
                                if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                                    sideEffectSubmits[seat]++
                                    moves++
                                    acted = true
                                    cheatDone[2] = true
                                } else {
                                    rejectedSubmits++
                                }
                            }
                        }

                        // 4. 常规打牌动作（和牌 > 打牌 > Pass > 首个选项）
                        val opt = primaryOption(snap) ?: continue
                        val choice = ProtocolActionChoice(
                            requestId = "t0-act-$moves-$seat",
                            roundId = snap.roundId,
                            decisionId = snap.decision.decisionId,
                            controlEpoch = snap.decision.controlEpoch,
                            optionId = opt.optionId
                        )

                        // 计量 REQ 信封样本
                        val req = ApiRequest(api = "SUBMIT_ACTION", requestId = choice.requestId, roundId = snap.roundId, choice = choice)
                        stats.record("REQ_envelope", TcpMessageCodec.encodeRequestEnvelope(TcpRequestEnvelope("scope-t0", choice.requestId, req)).toByteArray())

                        val receipt = connections[seat].submit(choice).await()
                        if (receipt.status == ProtocolReceiptStatus.APPLIED || receipt.status == ProtocolReceiptStatus.RECORDED) {
                            sideEffectSubmits[seat]++
                            moves++
                            acted = true
                        } else {
                            rejectedSubmits++
                        }
                    }

                    // 推进服务端 tick（驱动准备发牌、超时裁决与状态推进）
                    if (!acted) {
                        runCatching { server.tick().get(500, TimeUnit.MILLISECONDS) }
                        Thread.sleep(5)
                    } else {
                        runCatching { server.tick().get(500, TimeUnit.MILLISECONDS) }
                    }
                }

                // 结算结果补查 RSP 与终态快照计量
                settledRoundId?.let { rid ->
                    val events = connections[0].queryRoundResult(rid).await()
                    stats.record("RSP_roundResult", ApiMessageCodec.encodeResponse(ApiResponse(requestId = "m-settled", events = events)).toByteArray())
                }
                runCatching {
                    val finalSnap = connections[0].snapshot().await()
                    stats.record("RSP_snapshot", ApiMessageCodec.encodeResponse(ApiResponse(requestId = "m-final", snapshot = finalSnap)).toByteArray())
                    val finalPub = spectator.publicSnapshot().await()
                    stats.record("RSP_publicSnapshot", ApiMessageCodec.encodeResponse(ApiResponse(requestId = "m-final", publicSnapshot = finalPub)).toByteArray())
                    stats.record(
                        "PUBLIC_UPDATE(full)",
                        ApiMessageCodec.encodePublicUpdatePush(
                            ProtocolPublicUpdate(finalPub, finalPub.cursor, emptyList(), true)
                        ).toByteArray()
                    )
                }

                hooks.forEach { runCatching { it.close() } }

                println("=" .repeat(20) + " T0 载荷实测报告 " + "=".repeat(20))
                println(stats.report())
                println("  settled=$settled roundId=$settledRoundId moves=$moves rejectedSubmits=$rejectedSubmits")
                println("  每座位副作用提交（整局，含码牌/出千/打牌）: ${sideEffectSubmits.toList()}")
                println("=" .repeat(58))

                // 门槛：覆盖所有 9 种预期类型
                val expectedKinds = listOf(
                    "PLAYER_UPDATE",
                    "PUBLIC_UPDATE",
                    "PUBLIC_UPDATE(full)",
                    "ROOM_UPDATE",
                    "RSP_snapshot",
                    "RSP_publicSnapshot",
                    "RSP_roomInfo",
                    "RSP_roundResult",
                    "REQ_envelope"
                )
                for (kind in expectedKinds) {
                    assertTrue(stats.max.containsKey(kind), "缺少预期测量类型: $kind")
                }

                // 门槛：所有类型均须放入单帧，且最大值留 ≥25% 余量
                for ((kind, size) in stats.max) {
                    assertTrue(size <= TcpWire.MAX_PAYLOAD_BYTES, "$kind 超出单帧上限: $size")
                    assertTrue(size * 4 <= TcpWire.MAX_PAYLOAD_BYTES * 3, "$kind 超出 75% 水位（无 25% 余量）: $size")
                }
                // 整局必须真的打完（否则测量不覆盖结算载荷）
                assertTrue(settled, "整局未到结算，测量不完整（moves=$moves, rejectedSubmits=$rejectedSubmits）")
            } finally {
                runCatching { spectator.close() }
                connections.forEach { runCatching { it.close() } }
            }
        }
    }

    private fun primaryOption(snap: ProtocolPlayerSnapshot) = run {
        val opts = snap.decision.options
        opts.firstOrNull { it.action is ProtocolLegalAction.DeclareTsumo || it.action is ProtocolLegalAction.DeclareRon }
            ?: opts.firstOrNull { it.action is ProtocolLegalAction.DiscardTile }
            ?: opts.firstOrNull { it.action is ProtocolLegalAction.Pass }
            ?: opts.firstOrNull()
    }
}
