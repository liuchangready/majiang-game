package com.majiang.server.tcp

import com.majiang.access.PlayerSnapshot
import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.api.JoinRoomRequest
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeRequest
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.RoomBinding
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.ServerApiException
import com.majiang.server.api.SpectatorBinding
import com.majiang.server.host.HostDispatchPools
import com.majiang.server.idempotency.AdmissionResult
import com.majiang.server.idempotency.IdempotencyKey
import com.majiang.server.idempotency.InFlightReceipt
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.server.room.RoomRegistry
import com.majiang.server.room.SharedRoom
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ApiTags
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolScoreCodes
import com.majiang.transport.api.ProtocolReceiptStatus
import com.majiang.transport.api.ProtocolRoomConfig
import com.majiang.transport.tcp.TcpCloseReasons
import com.majiang.transport.tcp.TcpMessageCodec
import com.majiang.transport.tcp.TcpRequestEnvelope
import com.majiang.transport.tcp.TcpResponseEnvelope
import com.majiang.transport.tcp.TcpScope
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 连接顶号通知函数契约
 */
fun interface ConnectionDisplacer {
    fun displaceConnections(predicate: (TransportConnection) -> Boolean, reason: String, message: String)
}

/**
 * TCP 业务请求路由调度器 (TcpRequestRouter, D-08 / §9.2)
 *
 * 核心架构职责：
 * 1. 业务路由与传输连接解耦：使用显式的传输连接 [TransportConnection] 与已验证逻辑身份上下文 [LogicalIdentityContext]，
 *    经 [SharedRoom] 与 [com.majiang.server.host.ServerTableHost] 串行权威队列提交业务操作；
 * 2. 强类型身份隔离与边界校验 (D-02)：
 *    - 玩家专属对局指令全部经 [withPlayer] 接入，player.seat 严格处于 0..3，seat = -1 哨兵值绝不流入业务；
 *    - 观战专属指令全部经 [withSpectator] 接入，非观战身份非法调用直接拦截；
 *    - 房主专属指令 (UPDATE_CONFIG, START_MATCH) 严格核验 seat == 0；
 * 3. 业务回执委托与解耦：所有幂等裁决、重放检测、在途排队与完成回执全部委托给独立的 [TcpReceiptManager]；
 * 4. 原子基线推送与订阅安装 (UC-S01)：在同桌串行队列内原子获取初始基线快照并安装订阅，含基线的 RSP 优先入队后再放行后续 UPDATE；
 * 5. 顶号协调：席位/观战恢复时通过 [connectionDisplacer] 向旧连接发出 DISPLACED_BY_NEW_CONNECTION 并安全卸载旧订阅。
 */
class TcpRequestRouter(
    val registry: RoomRegistry,
    val receiptManager: TcpReceiptManager,
    val roomAdmission: CreateRoomAdmission,
    val publicFrameCache: PublicUpdateFrameCache,
    val hostDispatchPools: () -> HostDispatchPools? = { null },
    val connectionDisplacer: ConnectionDisplacer = ConnectionDisplacer { _, _, _ -> },
    val clock: com.majiang.transport.common.TransportClock = receiptManager.clock
) : AutoCloseable {

    private val isClosed = AtomicBoolean(false)

    /**
     * 路由并执行已验证的入站请求信封
     *
     * @param connection 物理传输连接端点
     * @param identity 已验证逻辑身份上下文
     * @param envelope 请求信封 (外层与内层 requestId 已校验一致)
     * @param rawPayload 原始请求体 JSON 串（用于精确载荷一致性比对）
     */
    fun routeRequest(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        envelope: TcpRequestEnvelope,
        rawPayload: String
    ) {
        if (isClosed.get() || connection.isClosingOrClosed) {
            return
        }

        val req = envelope.request
        val api = req.api

        // 1. D9 特殊控制查询: GET_REQUEST_SCOPE (仅凭认证身份准入，不受普通 scope 约束)
        if (api == "GET_REQUEST_SCOPE") {
            val (resp, grantedScope) = receiptManager.handleGetRequestScope(identity, envelope.requestId)
            sendResponse(connection, envelope.requestScope, envelope.requestId, resp, grantedScope)
            return
        }

        val isSideEffect = isSideEffectApi(api)
        val isLobby = identity.isLobby
        val key = IdempotencyKey(
            principal = identity.principal,
            scope = envelope.requestScope,
            requestId = envelope.requestId
        )

        // 2. 幂等准入裁决
        val admission = receiptManager.acquireAdmission(
            key = key,
            request = req,
            rawPayload = rawPayload,
            isSideEffect = isSideEffect,
            isLobby = isLobby,
            nowMillis = clock.currentTimeMillis()
        )

        when (admission) {
            is AdmissionResult.Replay -> {
                // 重放历史回执 (已由 receiptManager 完成房间存活新鲜度核验)
                sendResponse(
                    connection = connection,
                    requestScope = envelope.requestScope,
                    requestId = envelope.requestId,
                    response = admission.completed.response,
                    grantedScope = admission.completed.grantedScope
                )
            }
            is AdmissionResult.WaitInFlight -> {
                // 关联跨连接在途 Future (UC-R02 / D-08 跨连接在途所有权)
                admission.inFlight.future.whenComplete { resp, err ->
                    if (!connection.isClosingOrClosed) {
                        if (resp != null) {
                            sendResponse(
                                connection = connection,
                                requestScope = envelope.requestScope,
                                requestId = envelope.requestId,
                                response = resp,
                                grantedScope = admission.inFlight.grantedScope.get()
                            )
                        } else {
                            val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                            sendResponse(connection, envelope.requestScope, envelope.requestId, errResp)
                        }
                    }
                }
            }
            is AdmissionResult.Conflict -> {
                // 同键不同载荷/API 冲突 (D1 / UC-R03)
                val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.CONFLICT)
                sendResponse(connection, envelope.requestScope, envelope.requestId, errResp)
            }
            is AdmissionResult.ScopeExpired -> {
                // Scope 已过期且无历史回执 (D3 / UC-R05)
                val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.REQUEST_SCOPE_EXPIRED)
                sendResponse(connection, envelope.requestScope, envelope.requestId, errResp)
            }
            is AdmissionResult.ScopeInvalid -> {
                // 伪造或未知 Scope
                val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                sendResponse(connection, envelope.requestScope, envelope.requestId, errResp)
            }
            is AdmissionResult.ServerBusy -> {
                // 容量饱和受理前拒绝 (D2 / UC-R06)
                val errResp = ApiResponse(requestId = envelope.requestId, success = false, error = ApiErrors.SERVER_BUSY)
                sendResponse(connection, envelope.requestScope, envelope.requestId, errResp)
            }
            is AdmissionResult.Admitted -> {
                // 准入成功，执行真实业务调度
                executeBusinessRequest(connection, identity, key, envelope, admission.inFlight, isSideEffect, isLobby)
            }
        }
    }

    /**
     * 发送响应信封 (RSP)
     */
    fun sendResponse(
        connection: TransportConnection,
        requestScope: String,
        requestId: String,
        response: ApiResponse,
        grantedScope: TcpScope? = null
    ) {
        if (connection.isClosingOrClosed) return
        val rspEnvelope = TcpResponseEnvelope(
            requestScope = requestScope,
            requestId = requestId,
            response = response,
            grantedScope = grantedScope
        )
        val frame = TcpMessageCodec.toFrame(rspEnvelope)
        connection.sendNormalFrame(frame.toByteArray())
    }

    /**
     * 强类型玩家角色业务拦截器 (D-02: 保证 player.seat 严格在 0..3，seat = -1 哨兵值绝不流入业务)
     */
    private inline fun withPlayer(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean,
        block: (PlayerState) -> Unit
    ) {
        val player = identity.playerState
        if (player == null) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
        } else {
            block(player)
        }
    }

    /**
     * 强类型观战者角色业务拦截器 (D-02)
     */
    private inline fun withSpectator(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean,
        block: (SpectatorState) -> Unit
    ) {
        val spectator = identity.spectatorState
        if (spectator == null) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
        } else {
            block(spectator)
        }
    }

    /**
     * 派发真实业务请求
     */
    private fun executeBusinessRequest(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        key: IdempotencyKey,
        envelope: TcpRequestEnvelope,
        inFlight: InFlightReceipt,
        isSideEffect: Boolean,
        isLobby: Boolean
    ) {
        val req = envelope.request

        when (req.api) {
            ApiTags.LIST_ROOMS -> {
                val summaries = registry.listSummaries()
                val resp = ApiResponse(requestId = req.requestId, success = true, rooms = summaries)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            }
            ApiTags.CREATE_ROOM -> {
                handleCreateRoom(connection, identity, key, req, inFlight, isLobby)
            }
            ApiTags.JOIN_ROOM -> {
                handleJoinRoom(connection, identity, key, req, inFlight, isLobby)
            }
            ApiTags.RESUME_SESSION -> {
                handleResumeSession(connection, identity, key, req, inFlight, isLobby)
            }
            ApiTags.JOIN_SPECTATOR -> {
                handleJoinSpectator(connection, identity, key, req, inFlight, isLobby)
            }
            ApiTags.RESUME_SPECTATOR -> {
                handleResumeSpectator(connection, identity, key, req, inFlight, isLobby)
            }
            ApiTags.LEAVE_ROOM -> {
                handleLeaveRoom(connection, identity, key, req, inFlight, isLobby)
            }
            ApiTags.GET_ROOM_INFO -> {
                val room = identity.room
                if (room == null || room.closed) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    room.getRoomInfo { !connection.isClosingOrClosed }.whenComplete { info, err ->
                        val resp = if (err != null || info == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, roomInfo = info)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SET_READY -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                player.room.setReady(player.seat, req.ready ?: true, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                    val resp = if (err != null || receipt == null) {
                        ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
                    }
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                }
            }
            ApiTags.UPDATE_CONFIG -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                if (player.seat != 0) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    val effectiveRoomConfig = req.roomConfig ?: (req.config?.let { ProtocolRoomConfig(matchConfig = it) } ?: ProtocolRoomConfig())
                    player.room.updateConfig(player.seat, effectiveRoomConfig, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.START_MATCH -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                if (player.seat != 0) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNAUTHENTICATED_SESSION)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    player.room.startMatch(player.seat, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.NEXT_ROUND -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                player.room.nextRound(player.seat, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                    val resp = if (err != null || receipt == null) {
                        ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                        ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                    }
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                }
            }
            ApiTags.SUBMIT_CHOICE -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                val choice = req.choice
                if (choice == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    val engineChoice = EngineProtocolMapper.toEngineActionChoice(choice)
                    player.room.submitChoice(player.seat, engineChoice, player.makeSessionValidator(connection)).whenComplete { engineReceipt, err ->
                        val resp = if (err != null || engineReceipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(engineReceipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_SHUFFLE_PLAN -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                val plan = req.shufflePlan
                if (plan == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    val enginePlan = EngineProtocolMapper.toEngineShufflePlan(plan)
                    player.room.submitShufflePlan(player.seat, enginePlan, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_TILE_MARK -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                val mark = req.tileMark
                if (mark == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineTileMarkRequest(mark)
                    player.room.submitTileMark(player.seat, engineReq, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_SLEEVE_BLOOM -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                val bloom = req.sleeveBloom
                if (bloom == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineSleeveBloomRequest(bloom)
                    player.room.submitSleeveBloom(player.seat, engineReq, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_SLEEVE_BLOOM_CHOICE -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                val choice = req.sleeveBloomChoice
                if (choice == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineSleeveBloomChoiceRequest(choice)
                    player.room.submitSleeveBloomChoice(player.seat, engineReq, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_BEAM_SWAP -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                val swap = req.beamSwap
                if (swap == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineBeamSwapRequest(swap)
                    player.room.submitBeamSwap(player.seat, engineReq, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.SUBMIT_BEAM_SWAP_CHOICE -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                val choice = req.beamSwapChoice
                if (choice == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    val engineReq = EngineProtocolMapper.toEngineBeamSwapChoiceRequest(choice)
                    player.room.submitBeamSwapChoice(player.seat, engineReq, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                        val resp = if (err != null || receipt == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                            ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.CLAIM_CONTROL -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                player.room.claimControl(player.seat, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                    val resp = if (err != null || receipt == null) {
                        ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                        ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                    }
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                }
            }
            ApiTags.RELEASE_CONTROL -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                player.room.releaseControl(player.seat, player.makeSessionValidator(connection)).whenComplete { receipt, err ->
                    val resp = if (err != null || receipt == null) {
                        ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        val protoReceipt = EngineProtocolMapper.toProtocolReceipt(receipt)
                        ApiResponse(requestId = req.requestId, success = protoReceipt.status != ProtocolReceiptStatus.REJECTED, receipt = protoReceipt)
                    }
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                }
            }
            ApiTags.RESUME_CURSOR -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                player.room.resume(player.seat, req.cursor ?: 0L, player.makeSessionValidator(connection)).whenComplete { snap, err ->
                    val resp = if (err != null || snap == null) {
                        ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = req.requestId, success = true, snapshot = EngineProtocolMapper.toProtocolSnapshot(snap))
                    }
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                }
            }
            ApiTags.SNAPSHOT -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                player.snapshotSupplier().whenComplete { snap, err ->
                    val resp = if (err != null || snap == null) {
                        ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = req.requestId, success = true, snapshot = EngineProtocolMapper.toProtocolSnapshot(snap))
                    }
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                }
            }
            ApiTags.DECISION -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                player.room.decision(player.seat, player.makeSessionValidator(connection)).whenComplete { dec, err ->
                    val resp = if (err != null || dec == null) {
                        ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = req.requestId, success = true, decision = EngineProtocolMapper.toProtocolDecision(dec))
                    }
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                }
            }
            ApiTags.PUBLIC_SNAPSHOT -> withSpectator(connection, identity, key, req, inFlight, isLobby) { spectator ->
                spectator.publicSnapshotSupplier().whenComplete { snap, err ->
                    val resp = if (err != null || snap == null) {
                        ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                    } else {
                        ApiResponse(requestId = req.requestId, success = true, publicSnapshot = snap)
                    }
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                }
            }
            ApiTags.RESUME_PUBLIC -> withSpectator(connection, identity, key, req, inFlight, isLobby) { spectator ->
                val checkpoint = req.publicCheckpoint
                if (checkpoint == null) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    spectator.room.resumePublic(checkpoint, spectator.makeSessionValidator(connection)).whenComplete { snap, err ->
                        val resp = if (err != null || snap == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, publicSnapshot = snap)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.QUERY_ROUND_RESULT -> withPlayer(connection, identity, key, req, inFlight, isLobby) { player ->
                val roundId = req.roundId
                if (roundId.isNullOrBlank()) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    player.room.queryRoundResultEvents(player.seat, roundId, player.makeSessionValidator(connection)).whenComplete { events, err ->
                        val resp = if (err != null || events == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, events = EngineProtocolMapper.toProtocolEvents(events))
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.QUERY_PUBLIC_ROUND_RESULT -> withSpectator(connection, identity, key, req, inFlight, isLobby) { spectator ->
                val roundId = req.roundId
                if (roundId.isNullOrBlank()) {
                    val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.MALFORMED_REQUEST)
                    receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                    sendResponse(connection, key.scope, key.requestId, resp)
                } else {
                    spectator.room.queryPublicRoundResultEvents(roundId, spectator.makeSessionValidator(connection)).whenComplete { events, err ->
                        val resp = if (err != null || events == null) {
                            ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                        } else {
                            ApiResponse(requestId = req.requestId, success = true, publicEvents = events)
                        }
                        receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                        sendResponse(connection, key.scope, key.requestId, resp)
                    }
                }
            }
            ApiTags.CAPABILITIES -> {
                val caps = ProtocolAccessCapabilities(
                    protocolVersion = ApiProtocol.PROTOCOL_VERSION,
                    supportedPlayerCounts = setOf(4),
                    remoteTransport = true,
                    persistence = false,
                    resumption = true,
                    tileMarkSupported = true,
                    sleeveBloomSupported = true,
                    beamSwapSupported = true,
                    scoreDetailVersion = ProtocolScoreCodes.CONTRACT_VERSION
                )
                val resp = ApiResponse(requestId = req.requestId, success = true, capabilities = caps)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            }
            else -> {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.UNKNOWN_REQUEST_TYPE)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            }
        }
    }

    /**
     * 建房处理与原子订阅安装 (UC-S01 / R2 / MR-06 任务 D)
     */
    private fun handleCreateRoom(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val principal = identity.principal
        val remoteIp = CreateRoomAdmission.extractRemoteIp(connection.remoteAddress)

        val outcome = roomAdmission.tryReserveOutcome(principal, remoteIp)
        val reservation = when (outcome) {
            is CreateRoomAdmission.Outcome.Accepted -> outcome.reservation
            is CreateRoomAdmission.Outcome.RateLimited -> {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.SERVER_BUSY)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
                return
            }
            is CreateRoomAdmission.Outcome.QuotaExceeded -> {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.SERVER_FULL)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
                return
            }
        }

        val room = try {
            val effectiveRoomConfig = req.roomConfig ?: (req.config?.let { ProtocolRoomConfig(matchConfig = it) } ?: ProtocolRoomConfig())
            registry.createRoom(
                hostPlayerName = req.playerName ?: "Host",
                initialConfig = effectiveRoomConfig,
                isSingleplayer = false,
                hostSeat = 0,
                hostDispatch = hostDispatchPools()
            )
        } catch (ex: ServerApiException) {
            reservation.release()
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ex.code)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        } catch (t: Throwable) {
            reservation.release()
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        reservation.commit(room.roomId)
        val capturedSeatGen = room.seatMemberGeneration(0)
        bindPlayerToRoom(connection, room, 0, capturedSeatGen, key, req, inFlight, isLobby)
    }

    /**
     * 加入房间处理与原子订阅安装 (UC-S01)
     */
    private fun handleJoinRoom(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val targetRoom = registry.getRoom(req.roomId ?: "")
        if (targetRoom == null || targetRoom.closed) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        targetRoom.join(req.playerName ?: "Player", req.preferSeat, { !connection.isClosingOrClosed }).whenComplete { pair, err ->
            if (err != null || pair == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_FULL)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            } else if (connection.isClosingOrClosed) {
                val seat = pair.first
                val seatGen = targetRoom.seatMemberGeneration(seat)
                targetRoom.disconnectSeat(seat, seatGen)
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_FULL)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            } else {
                val seat = pair.first
                val seatGen = targetRoom.seatMemberGeneration(seat)
                bindPlayerToRoom(connection, targetRoom, seat, seatGen, key, req, inFlight, isLobby)
            }
        }
    }

    /**
     * 会话恢复处理与原子订阅安装 (UC-S01 / UC-S08)
     */
    private fun handleResumeSession(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val targetRoom = registry.getRoom(req.roomId ?: "")
        if (targetRoom == null || targetRoom.closed) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        val token = req.token
        if (token.isNullOrBlank()) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        targetRoom.resumeSession(token, { !connection.isClosingOrClosed }).whenComplete { res, err ->
            if (err != null || res == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            } else if (connection.isClosingOrClosed) {
                targetRoom.disconnectSeat(res.seat, res.generation)
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            } else {
                bindPlayerToRoom(connection, targetRoom, res.seat, res.generation, key, req, inFlight, isLobby)
            }
        }
    }

    /**
     * 将玩家绑定到指定房间席位，并在同桌串行队列内原子获取基线快照与安装订阅 (UC-S01 / R7 / R06 / R15 / D-02)
     */
    private fun bindPlayerToRoom(
        connection: TransportConnection,
        room: SharedRoom,
        seat: Int,
        capturedSeatGen: Long,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        if (connection.isClosingOrClosed) {
            room.disconnectSeat(seat, capturedSeatGen)
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        val playerPrincipal = "player:${room.roomId}:$seat"
        lateinit var playerState: PlayerState
        playerState = PlayerState(
            seat = seat,
            snapshotSupplier = {
                room.snapshot(seat, playerState.makeSessionValidator(connection))
                    .thenApply { it ?: throw ServerApiException(ApiErrors.INTERNAL_ERROR, "Snapshot unavailable") }
            },
            room = room,
            bindingGeneration = capturedSeatGen
        )

        val installed = (connection as? TcpConnection)?.installPlayerRole(playerState) ?: false
        if (!installed) {
            playerState.detach()
            playerState.notifyDisconnect()
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        // 顶号扫描：向持有同房间同席位的旧连接主动发出 CLOSE(DISPLACED_BY_NEW_CONNECTION)
        connectionDisplacer.displaceConnections(
            predicate = { other ->
                val otherRole = (other as? TcpConnection)?.roleState as? PlayerState
                other !== connection && otherRole?.room === room && otherRole.seat == seat
            },
            reason = TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION,
            message = "Seat resumed by a new connection"
        )

        val roleScope = receiptManager.issueRoleScope(playerPrincipal)
        inFlight.grantedScope.set(roleScope)

        // 1. 同桌串行队列内原子获取基线快照与安装订阅 (R15 / Design §6)
        room.snapshotAndSubscribe(seat, playerState.makeSessionValidator(connection)) { update ->
            if (!connection.isClosingOrClosed && playerState.makeSessionValidator(connection)()) {
                val protoUpdate = EngineProtocolMapper.toProtocolUpdate(update)
                val frame = TcpMessageCodec.toPlayerUpdateFrame(protoUpdate)
                connection.sendNormalFrame(frame.toByteArray())
            }
        }.whenComplete { pair, snapErr ->
            if (snapErr != null || pair == null) {
                playerState.detach()
                playerState.notifyDisconnect()
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
                return@whenComplete
            }

            val (snap, subHandle) = pair
            if (connection.isClosingOrClosed || !playerState.makeSessionValidator(connection)() || room.closed || registry.getRoom(room.roomId) == null) {
                subHandle.close()
                playerState.detach()
                playerState.notifyDisconnect()
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
                return@whenComplete
            }
            val displacedSub = room.onSeatDisplaced { displacedSeat, newGen ->
                if (playerState.seat == displacedSeat && newGen > playerState.bindingGeneration) {
                    connection.close(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, "Seat resumed by a new connection", immediate = false)
                }
            }
            playerState.attachSubscriptions(seatSub = subHandle, displacedSub = displacedSub)

            // 通知房间席位已成功连接 (MR-06 任务 B / D-02 核对代次与连接有效性)
            room.markSeatConnected(seat, capturedSeatGen, playerState.makeSessionValidator(connection))

            val protoSnap = EngineProtocolMapper.toProtocolSnapshot(snap)
            val resp = ApiResponse(
                requestId = req.requestId,
                success = true,
                roomId = room.roomId,
                seat = seat,
                resumeToken = room.seatResumeToken(seat),
                snapshot = protoSnap
            )

            // 2. 存入已完成回执
            receiptManager.completeReceipt(key, inFlight, resp, grantedScope = roleScope, isLobby = isLobby)

            // 3. 将携带基线的 RSP 优先排入业务输出队列 (UC-S01)
            sendResponse(connection, key.scope, key.requestId, resp, grantedScope = roleScope)

            // 4. 房间状态更新订阅
            val roomSub = room.onRoomUpdate { info ->
                if (!connection.isClosingOrClosed && playerState.makeSessionValidator(connection)()) {
                    val frame = TcpMessageCodec.toRoomUpdateFrame(info)
                    connection.sendNormalFrame(frame.toByteArray())
                }
            }
            playerState.updateRoomSubscription(roomSub)
        }
    }

    /**
     * 加入观战处理 (UC-S01)
     */
    private fun handleJoinSpectator(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val targetRoom = registry.getRoom(req.roomId ?: "")
        if (targetRoom == null || targetRoom.closed) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        val specName = req.spectatorName ?: "Spectator"
        targetRoom.joinSpectator(specName, { !connection.isClosingOrClosed }).whenComplete { res, err ->
            if (err != null || res == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            } else if (connection.isClosingOrClosed) {
                targetRoom.disconnectSpectator(res.spectatorId, res.generation)
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            } else {
                bindSpectatorToRoom(connection, targetRoom, res.spectatorId, res.resumeToken, res.generation, key, req, inFlight, isLobby)
            }
        }
    }

    /**
     * 恢复观战处理 (UC-S01)
     */
    private fun handleResumeSpectator(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        val targetRoom = registry.getRoom(req.roomId ?: "")
        if (targetRoom == null || targetRoom.closed) {
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        val specId = req.spectatorId ?: ""
        val token = req.token ?: ""
        targetRoom.resumeSpectator(specId, token, req.publicCheckpoint, { !connection.isClosingOrClosed }).whenComplete { res, err ->
            if (err != null || res == null) {
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            } else if (connection.isClosingOrClosed) {
                targetRoom.disconnectSpectator(res.spectatorId, res.generation)
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INVALID_TOKEN)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
            } else {
                val specToken = targetRoom.spectatorResumeToken(res.spectatorId) ?: token
                bindSpectatorToRoom(connection, targetRoom, res.spectatorId, specToken, res.generation, key, req, inFlight, isLobby)
            }
        }
    }

    /**
     * 绑定观战者并在同桌串行队列内原子获取公开快照基线与安装订阅 (UC-S01 / R06 / R15 / D-02)
     */
    private fun bindSpectatorToRoom(
        connection: TransportConnection,
        room: SharedRoom,
        specId: String,
        specToken: String,
        generation: Long,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        if (connection.isClosingOrClosed) {
            room.disconnectSpectator(specId, generation)
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        val spectatorPrincipal = "spectator:${room.roomId}:$specId"
        lateinit var spectatorState: SpectatorState
        spectatorState = SpectatorState(
            spectatorId = specId,
            publicSnapshotSupplier = {
                room.publicSnapshot(spectatorState.makeSessionValidator(connection))
                    .thenApply { it ?: throw ServerApiException(ApiErrors.INTERNAL_ERROR, "Public snapshot unavailable") }
            },
            room = room,
            bindingGeneration = generation
        )

        val installed = (connection as? TcpConnection)?.installSpectatorRole(spectatorState) ?: false
        if (!installed) {
            spectatorState.detach()
            spectatorState.notifyDisconnect()
            val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        // 顶号扫描：向持有同房间同观战者身份的旧连接主动发出 CLOSE(DISPLACED_BY_NEW_CONNECTION)
        connectionDisplacer.displaceConnections(
            predicate = { other ->
                val otherRole = (other as? TcpConnection)?.roleState as? SpectatorState
                other !== connection && otherRole?.room === room && otherRole.spectatorId == specId
            },
            reason = TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION,
            message = "Spectator session resumed by a new connection"
        )

        val roleScope = receiptManager.issueRoleScope(spectatorPrincipal)
        inFlight.grantedScope.set(roleScope)

        // 1. 同桌串行队列内原子获取基线快照与安装订阅 (R15 / Design §6)
        val capturedGen = spectatorState.bindingGeneration
        room.publicSnapshotAndSubscribe(spectatorState.makeSessionValidator(connection)) { pubUpdate ->
            if (!connection.isClosingOrClosed && spectatorState.makeSessionValidator(connection)()) {
                val frameBytes = publicFrameCache.frameBytesFor(pubUpdate)
                connection.sendNormalFrame(frameBytes)
            }
        }.whenComplete { pair, snapErr ->
            if (snapErr != null || pair == null) {
                spectatorState.detach()
                spectatorState.notifyDisconnect()
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
                return@whenComplete
            }

            val (pubSnap, subHandle) = pair
            if (connection.isClosingOrClosed || !spectatorState.makeSessionValidator(connection)() || room.closed || registry.getRoom(room.roomId) == null) {
                subHandle.close()
                spectatorState.detach()
                spectatorState.notifyDisconnect()
                val resp = ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.ROOM_NOT_FOUND)
                receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp)
                return@whenComplete
            }
            val displacedSub = room.onSpectatorDisplaced { displacedId, newGen ->
                if (spectatorState.spectatorId == displacedId && newGen > spectatorState.bindingGeneration) {
                    connection.close(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, "Spectator session resumed by a new connection", immediate = false)
                }
            }
            spectatorState.attachSubscriptions(publicSub = subHandle, displacedSub = displacedSub)

            // 通知房间观战者已成功连接 (MR-06 任务 B / D-02 核对 expectedGeneration 与会话有效性)
            room.markSpectatorConnected(specId, capturedGen, spectatorState.makeSessionValidator(connection))

            val binding = SpectatorBinding(
                roomId = room.roomId,
                spectatorId = specId,
                resumeToken = specToken,
                snapshot = pubSnap
            )
            val resp = ApiResponse(
                requestId = req.requestId,
                success = true,
                spectatorBinding = binding,
                publicSnapshot = pubSnap
            )

            // 2. 存入已完成回执
            receiptManager.completeReceipt(key, inFlight, resp, grantedScope = roleScope, isLobby = isLobby)

            // 3. 将包含基线的 RSP 先排入出站队列 (UC-S01)
            sendResponse(connection, key.scope, key.requestId, resp, grantedScope = roleScope)

            // 4. 房间状态更新订阅
            val roomSub = room.onRoomUpdate { info ->
                if (!connection.isClosingOrClosed && spectatorState.makeSessionValidator(connection)()) {
                    val frame = TcpMessageCodec.toRoomUpdateFrame(info)
                    connection.sendNormalFrame(frame.toByteArray())
                }
            }
            spectatorState.updateRoomSubscription(roomSub)
        }
    }

    /** Leave once per role binding, and grant a lobby scope only after an applied leave. */
    private fun handleLeaveRoom(
        connection: TransportConnection,
        identity: LogicalIdentityContext,
        key: IdempotencyKey,
        req: ApiRequest,
        inFlight: InFlightReceipt,
        isLobby: Boolean
    ) {
        if (connection.isClosingOrClosed) {
            val receipt = ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")
            val resp = ApiResponse(requestId = req.requestId, success = false, receipt = receipt, error = ApiErrors.UNAUTHENTICATED_SESSION)
            receiptManager.completeReceipt(key, inFlight, resp, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp)
            return
        }

        val roomRole = identity.roleState as? RoomRoleState
        val room = roomRole?.room
        if (roomRole == null || room == null || room.closed) {
            if (roomRole != null) (connection as? TcpConnection)?.unbindToLobbyRole()
            val receipt = ProtocolActionReceipt("leaveRoom", ProtocolReceiptStatus.APPLIED)
            val resp = ApiResponse(requestId = req.requestId, success = true, receipt = receipt)
            val lobbyScope = lobbyScopeAfterLeave(connection)
            receiptManager.completeReceipt(key, inFlight, resp, grantedScope = lobbyScope, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp, grantedScope = lobbyScope)
            return
        }

        val existingLeave = when (roomRole) {
            is PlayerState -> synchronized(roomRole) { roomRole.inFlightLeave }
            is SpectatorState -> synchronized(roomRole) { roomRole.inFlightLeave }
        }
        if (existingLeave != null) {
            existingLeave.whenComplete { receipt, err ->
                val lobbyScope = if (receipt?.status == ProtocolReceiptStatus.APPLIED) {
                    (connection as? TcpConnection)?.unbindToLobbyRole()
                    lobbyScopeAfterLeave(connection)
                } else null
                val resp = if (err != null || receipt == null) {
                    ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
                } else {
                    ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
                }
                receiptManager.completeReceipt(key, inFlight, resp, grantedScope = lobbyScope, isLobby = isLobby)
                sendResponse(connection, key.scope, key.requestId, resp, grantedScope = lobbyScope)
            }
            return
        }

        val leaveFuture = when (roomRole) {
            is SpectatorState -> room.leaveSpectator(roomRole.spectatorId, roomRole.makeSessionValidator(connection)).also {
                synchronized(roomRole) { roomRole.inFlightLeave = it }
            }
            is PlayerState -> room.leave(roomRole.seat, roomRole.makeSessionValidator(connection)).also {
                synchronized(roomRole) { roomRole.inFlightLeave = it }
            }
        }

        leaveFuture.whenComplete { receipt, err ->
            when (roomRole) {
                is PlayerState -> synchronized(roomRole) { roomRole.inFlightLeave = null }
                is SpectatorState -> synchronized(roomRole) { roomRole.inFlightLeave = null }
            }
            val lobbyScope = if (receipt?.status == ProtocolReceiptStatus.APPLIED) {
                (connection as? TcpConnection)?.unbindToLobbyRole()
                lobbyScopeAfterLeave(connection)
            } else null
            if (room.closed) {
                try {
                    val sessionId = room.tableHost.publicSessionId
                    publicFrameCache.releaseSession(sessionId)
                } catch (_: Throwable) {
                }
            }

            val resp = if (err != null || receipt == null) {
                ApiResponse(requestId = req.requestId, success = false, error = ApiErrors.INTERNAL_ERROR)
            } else {
                ApiResponse(requestId = req.requestId, success = receipt.status == ProtocolReceiptStatus.APPLIED, receipt = receipt)
            }
            receiptManager.completeReceipt(key, inFlight, resp, grantedScope = lobbyScope, isLobby = isLobby)
            sendResponse(connection, key.scope, key.requestId, resp, grantedScope = lobbyScope)
        }
    }

    private fun lobbyScopeAfterLeave(connection: TransportConnection): TcpScope? {
        val token = (connection as? TcpConnection)?.lobbyResumeToken ?: return null
        return receiptManager.currentScopeForPrincipal("lobby:$token")
    }

    /**
     * 判定是否属于副作用 API
     */
    fun isSideEffectApi(api: String): Boolean = when (api) {
        ApiTags.CREATE_ROOM,
        ApiTags.JOIN_ROOM,
        ApiTags.JOIN_SPECTATOR,
        ApiTags.RESUME_SESSION,
        ApiTags.RESUME_SPECTATOR,
        ApiTags.LEAVE_ROOM,
        ApiTags.SET_READY,
        ApiTags.UPDATE_CONFIG,
        ApiTags.START_MATCH,
        ApiTags.NEXT_ROUND,
        ApiTags.SUBMIT_CHOICE,
        ApiTags.SUBMIT_SHUFFLE_PLAN,
        ApiTags.SUBMIT_TILE_MARK,
        ApiTags.SUBMIT_SLEEVE_BLOOM,
        ApiTags.SUBMIT_SLEEVE_BLOOM_CHOICE,
        ApiTags.SUBMIT_BEAM_SWAP,
        ApiTags.SUBMIT_BEAM_SWAP_CHOICE,
        ApiTags.CLAIM_CONTROL,
        ApiTags.RELEASE_CONTROL -> true
        else -> false
    }

    override fun close() {
        if (!isClosed.compareAndSet(false, true)) {
            return
        }
        receiptManager.close()
    }
}
