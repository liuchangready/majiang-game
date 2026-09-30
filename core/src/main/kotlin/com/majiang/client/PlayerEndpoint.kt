package com.majiang.client

import com.majiang.application.ActionChoice
import com.majiang.application.PlayerDecision
import com.majiang.session.PlayerView
import com.majiang.session.SessionUpdate
import com.majiang.state.ActionResult
import com.majiang.state.GameAction
import com.majiang.state.RejectionReason

/**
 * 客户端端点能力声明。
 *
 * 显式声明当前端点支持的特性集：
 * - local: 是否为本地进程内端点；
 * - remoteTransport: 是否经由远程网络传输；
 * - persistence: 是否具备持久化存储与断线恢复；
 * - decisions: 是否支持结构化决策查询与候选提交 (queryDecision / submitChoice)。
 */
data class EndpointCapabilities(
    val local: Boolean = true,
    val remoteTransport: Boolean = false,
    val persistence: Boolean = false,
    val decisions: Boolean = true
)

/**
 * 统一客户端端点契约 (PlayerEndpoint)。
 *
 * 表现层 UI（如 GameScreen）及远程网络客户端均面向此接口编程。
 * 外部观察者通过此接口观察基于 viewerSeat 投影的只读 PlayerView 或结构化 PlayerDecision，
 * 所有的交互通过候选动作/选择接口进行校验与提交。
 *
 * 为保持对 FakeGameSession 等测试替身的兼容性，扩展接口方法提供了显式不支持的默认实现。
 */
interface PlayerEndpoint : AutoCloseable {
    /** 当前端点绑定的观察者座位 (0~3) */
    val viewerSeat: Int

    /** 当前端点能力声明 */
    val capabilities: EndpointCapabilities
        get() = EndpointCapabilities(
            local = true,
            remoteTransport = false,
            persistence = false,
            decisions = false
        )

    /** 获取当前座位的深拷贝只读视图 */
    fun view(): PlayerView

    /** 获取当前座位的合法动作列表 */
    fun legalActions(): List<GameAction>

    /** 开始对局 */
    fun start(): ActionResult

    /** 提交玩家动作（兼容入口，与 submitChoice 经过相同校验） */
    fun submit(action: GameAction): ActionResult

    /** 推进到下一局 */
    fun nextRound(): ActionResult

    /** 订阅端点更新通知（视图变化与脱敏后的可见事件） */
    fun subscribe(listener: (SessionUpdate) -> Unit): AutoCloseable

    /** 查询当前座位的结构化候选决策（包含 roundId、decisionId、controlEpoch 与候选 options） */
    fun queryDecision(): PlayerDecision =
        throw UnsupportedOperationException("queryDecision is not supported by this endpoint")

    /** 提交结构化动作选择（带幂等 requestId、代次与决策标识校验） */
    fun submitChoice(choice: ActionChoice): ActionResult =
        ActionResult.Rejected(RejectionReason.UNSUPPORTED_ACTION)

    /** 关闭并释放端点资源（关闭单个端点不关闭整场或影响其他端点） */
    override fun close()
}
