package com.majiang.server.api

import com.majiang.transport.api.ProtocolAccessCapabilities
import com.majiang.transport.api.ProtocolActionChoice
import com.majiang.transport.api.ProtocolActionReceipt
import com.majiang.transport.api.ProtocolGameEvent
import com.majiang.transport.api.ProtocolPlayerDecision
import com.majiang.transport.api.ProtocolPlayerSnapshot
import com.majiang.transport.api.ProtocolShufflePlan
import com.majiang.transport.api.ProtocolSleeveBloomChoiceRequest
import com.majiang.transport.api.ProtocolSleeveBloomRequest
import com.majiang.transport.api.ProtocolTileMarkRequest
import java.util.concurrent.CompletableFuture

/**
 * 对局期异步服务接口 (SessionApi)
 *
 * 面向已完成房间席位绑定、持有合法 token 的连接。
 *
 * 核心契约：
 * 1. **全异步契约**：所有交互均返回 [CompletableFuture]，权威裁决在服务端单桌串行循环中执行；
 * 2. **幂等提交契约**：所有写操作均携带 `requestId`，服务端按 `(seat, requestId)` 作用域进行去重缓存；
 * 3. **合法回执模型**：业务性拒绝（如决策过期、非法动作、出千点数不足、非房主开赛等）均封装在 [ProtocolActionReceipt]
 *    中返回（status = REJECTED 并附带稳定原因码）；
 * 4. **控制权语义**：通过 [claimControl]（收回本人控制权，终止 AI 托管）与 [releaseControl]（交出控制权给 AI 托管）
 *    明确表达控制切换，不使用含糊的布尔值参数；
 * 5. **按座位脱敏投影**：快照与查询结果在服务端按当前座位严格投影脱敏，对手暗牌与私密信息绝不外泄。
 */
interface SessionApi {

    /**
     * 查询服务端及当前环境的能力集声明
     *
     * @return 包含协议版本、支持人数、是否远端传输与重连能力等的能力集凭据
     */
    fun capabilities(): CompletableFuture<ProtocolAccessCapabilities>

    /**
     * 拉取当前座位的最新完整快照
     *
     * @return 脱敏后的完整快照凭据 [ProtocolPlayerSnapshot]
     */
    fun snapshot(): CompletableFuture<ProtocolPlayerSnapshot>

    /**
     * 查询当前座位可执行的合法决策与候选动作
     *
     * @return 包含当前决策标识 [ProtocolPlayerDecision] 的异步凭据（含千术动作已按点数/怀疑度过滤）
     */
    fun decision(): CompletableFuture<ProtocolPlayerDecision>

    /**
     * 提交结构化动作选择
     *
     * 具备按 `(seat, requestId)` 作用域的幂等重放保证。
     *
     * @param choice 玩家动作选择载荷（含 requestId、roundId、decisionId、controlEpoch、optionId）
     * @return 操作回执凭据 [ProtocolActionReceipt]（APPLIED 生效 / RECORDED 记录待裁决 / REJECTED 拒绝）
     */
    fun submit(choice: ProtocolActionChoice): CompletableFuture<ProtocolActionReceipt>

    /**
     * 在开局码牌洗牌阶段提交千术预设方案
     *
     * @param plan 洗牌方案请求（含目标牌型设定与消耗点数预算）
     * @return 操作回执凭据 [ProtocolActionReceipt]（成功时通常为 RECORDED，发牌时统一扣点结算）
     */
    fun submitShufflePlan(plan: ProtocolShufflePlan): CompletableFuture<ProtocolActionReceipt>

    /**
     * 开始比赛（房主席位专属）
     *
     * @return 操作回执凭据（非房主或人数不足时返回 REJECTED）
     */
    fun start(): CompletableFuture<ProtocolActionReceipt>

    /**
     * 推进到下一局（单局结算完成后进入局间准备）
     *
     * @return 操作回执凭据（整场比赛终局后返回 REJECTED）
     */
    fun nextRound(): CompletableFuture<ProtocolActionReceipt>

    /**
     * 会话内基于指定游标恢复或对账状态
     *
     * @param cursor 客户端当前持有的单调递增游标（0 表示请求全量）
     * @return 权威快照凭据 [ProtocolPlayerSnapshot]
     */
    fun resume(cursor: Long): CompletableFuture<ProtocolPlayerSnapshot>

    /**
     * 收回当前席位的控制权（声明真人亲自操作，关闭 AI 托管）
     *
     * @return 操作回执凭据（不撤销托管期间已提交的动作、不重置思考用时）
     */
    fun claimControl(): CompletableFuture<ProtocolActionReceipt>

    /**
     * 交出当前席位的控制权（交由 AI 托管自动决策）
     *
     * @return 操作回执凭据
     */
    fun releaseControl(): CompletableFuture<ProtocolActionReceipt>


    /**
     * 查询指定单局的公开结算事件
     *
     * @param roundId 目标局标识
     * @return 该局公开结算事件列表凭据（包含役种/番/符/宝牌与计分明细）
     */
    fun queryRoundResult(roundId: String): CompletableFuture<List<ProtocolGameEvent>>

    /**
     * 提交落汗千术请求
     *
     * @param request 标记请求载荷（含 requestId、sessionId、roundId、decisionId、controlEpoch、tileIds）
     * @return 操作回执凭据 [ProtocolActionReceipt]（APPLIED 生效 / REJECTED 拒绝）
     */
    fun submitTileMark(request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt> =
        CompletableFuture.failedFuture(UnsupportedOperationException("submitTileMark not implemented"))

    /**
     * 提交袖里藏花激活请求
     */
    fun submitSleeveBloom(request: ProtocolSleeveBloomRequest): CompletableFuture<ProtocolActionReceipt> =
        CompletableFuture.failedFuture(UnsupportedOperationException("submitSleeveBloom not implemented"))

    /**
     * 提交袖里藏花二选一决策请求
     */
    fun submitSleeveBloomChoice(request: ProtocolSleeveBloomChoiceRequest): CompletableFuture<ProtocolActionReceipt> =
        CompletableFuture.failedFuture(UnsupportedOperationException("submitSleeveBloomChoice not implemented"))
}
