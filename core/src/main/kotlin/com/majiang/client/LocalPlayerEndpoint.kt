package com.majiang.client

import com.majiang.application.ActionChoice
import com.majiang.application.ApplicationCoordinator
import com.majiang.application.PlayerDecision
import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.session.PlayerView
import com.majiang.session.SessionUpdate
import com.majiang.state.ActionResult
import com.majiang.state.GameAction
import com.majiang.state.GamePhase
import com.majiang.state.RejectionReason
import com.majiang.state.SeatController
import java.util.Collections
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 本地进程内客户端端点实现 (LocalPlayerEndpoint)。
 *
 * 表现层与领域层解耦的核心端点：
 * - 面向 ApplicationCoordinator 协作，对外暴露统一 PlayerEndpoint 契约；
 * - 外部只能观察到基于 viewerSeat 投影生成的深拷贝 PlayerView 与 PlayerDecision；
 * - 广播更新 SessionUpdate 经过 viewerSeat 脱敏过滤与针对每个监听者的独立深拷贝快照；
 * - 多个端点可连接同一协调器，任一端点动作更新所有订阅端点；关闭单个端点不影响其他端点或整场；
 * - 协调器保证内部一致性读写，端点不加嵌套锁，杜绝与协调器的死锁风险；
 * - 动作合法性、控制者权限与幂等校验统一委托给 ApplicationCoordinator。
 */
open class LocalPlayerEndpoint(
    override val viewerSeat: Int = 0,
    protected val coordinator: ApplicationCoordinator = ApplicationCoordinator(),
    controllers: List<SeatController>? = null
) : PlayerEndpoint {

    private val configuredControllers: List<SeatController>? = controllers?.let { list ->
        val copy = Collections.unmodifiableList(ArrayList(list))
        require(copy.size == 4) { "controllers must contain exactly 4 seats, got ${copy.size}" }
        copy
    }

    override val capabilities: EndpointCapabilities = EndpointCapabilities(
        local = true,
        remoteTransport = false,
        persistence = false,
        decisions = true
    )

    private val isClosed = AtomicBoolean(false)
    private val listeners = CopyOnWriteArrayList<(SessionUpdate) -> Unit>()
    private val coordinatorSubscription: AutoCloseable

    init {
        require(viewerSeat in 0..3) { "viewerSeat must be 0..3, got $viewerSeat" }
        // 注意：构造多端点绝不能在 init 中重置或初始化协调器的控制者配置，
        // 控制配置由 start() 或外部显式调用统一控制。
        coordinatorSubscription = coordinator.subscribe { rawEvents ->
            notifySubscribers(rawEvents)
        }
    }

    private fun isMatchStarted(viewSnapshot: PlayerView? = null): Boolean {
        val round = coordinator.activeRound ?: return true
        if (round.version > 0L) return true
        val view = viewSnapshot ?: coordinator.queryView(viewerSeat)
        return view.phase != GamePhase.STACKING || view.wallRemaining > 0
    }

    private fun currentController(viewSnapshot: PlayerView? = null): SeatController {
        if (!coordinator.hasControllerConfiguration && !isMatchStarted(viewSnapshot)) {
            if (configuredControllers != null) {
                return configuredControllers[viewerSeat]
            }
            return SeatController.LocalHuman()
        }
        return coordinator.controllerFor(viewerSeat)
    }

    override fun view(): PlayerView {
        val baseView = coordinator.queryView(viewerSeat)
        if (isClosed.get()) {
            return if (baseView.legalActions.isEmpty()) baseView else baseView.copy(legalActions = emptyList())
        }
        val controller = currentController(baseView)
        val isHuman = controller is SeatController.LocalHuman || controller is SeatController.RemoteHuman
        if (!isHuman) {
            return if (baseView.legalActions.isEmpty()) baseView else baseView.copy(legalActions = emptyList())
        }
        return baseView
    }

    override fun legalActions(): List<GameAction> = view().legalActions

    override fun queryDecision(): PlayerDecision {
        if (isClosed.get()) {
            return PlayerDecision(
                roundId = null,
                decisionId = "",
                controlEpoch = 0L,
                options = emptyList()
            )
        }
        val controller = currentController()
        val isHuman = controller is SeatController.LocalHuman || controller is SeatController.RemoteHuman
        if (!isHuman) {
            val baseDecision = coordinator.queryDecision(viewerSeat)
            return baseDecision.copy(options = emptyList())
        }
        return coordinator.queryDecision(viewerSeat)
    }

    override fun start(): ActionResult {
        if (isClosed.get()) {
            return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
        }
        if (!isMatchStarted()) {
            if (configuredControllers != null) {
                coordinator.initializeMatch(configuredControllers)
            } else if (!coordinator.hasControllerConfiguration) {
                coordinator.initializeMatch(humanIndex = viewerSeat)
            }
        }
        val result = coordinator.start()
        if (result is ActionResult.Accepted) {
            val commandEvents = SessionUpdate.filterVisibleEvents(result.events, viewerSeat).map { snapshotGameEvent(it) }
            return ActionResult.Accepted(Collections.unmodifiableList(commandEvents))
        }
        return result
    }

    override fun submit(action: GameAction): ActionResult {
        if (isClosed.get()) {
            return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
        }
        val result = coordinator.submitPlayerAction(viewerSeat, action)
        if (result is ActionResult.Accepted) {
            val commandEvents = SessionUpdate.filterVisibleEvents(result.events, viewerSeat).map { snapshotGameEvent(it) }
            return ActionResult.Accepted(Collections.unmodifiableList(commandEvents))
        }
        return result
    }

    override fun submitChoice(choice: ActionChoice): ActionResult {
        if (isClosed.get()) {
            return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
        }
        val result = coordinator.submitChoice(viewerSeat, choice)
        if (result is ActionResult.Accepted) {
            val commandEvents = SessionUpdate.filterVisibleEvents(result.events, viewerSeat).map { snapshotGameEvent(it) }
            return ActionResult.Accepted(Collections.unmodifiableList(commandEvents))
        }
        return result
    }

    override fun nextRound(): ActionResult {
        if (isClosed.get()) {
            return ActionResult.Rejected(RejectionReason.MATCH_FINISHED)
        }
        val result = coordinator.nextRound()
        if (result is ActionResult.Accepted) {
            val commandEvents = SessionUpdate.filterVisibleEvents(result.events, viewerSeat).map { snapshotGameEvent(it) }
            return ActionResult.Accepted(Collections.unmodifiableList(commandEvents))
        }
        return result
    }

    override fun subscribe(listener: (SessionUpdate) -> Unit): AutoCloseable {
        if (isClosed.get()) {
            return AutoCloseable { }
        }
        listeners.add(listener)
        // 初始推送当前视图快照（必须捕获回调异常）
        try {
            listener(SessionUpdate(view(), emptyList()))
        } catch (t: Throwable) {
            // 首次推送回调异常不得中止或崩溃调用方
        }
        return AutoCloseable { listeners.remove(listener) }
    }

    override fun close() {
        if (isClosed.compareAndSet(false, true)) {
            coordinatorSubscription.close()
            listeners.clear()
        }
    }

    private fun notifySubscribers(rawEvents: List<GameEvent>) {
        if (isClosed.get() || listeners.isEmpty()) return
        val filtered = SessionUpdate.filterVisibleEvents(rawEvents, viewerSeat)
        for (listener in listeners) {
            try {
                // 每个监听者必须收到独立的深复制事件与视图，防止某个监听者修改数据（如 newScores）污染后续监听者
                val isolatedEvents = filtered.map { snapshotGameEvent(it) }
                listener(SessionUpdate(view(), isolatedEvents))
            } catch (t: Throwable) {
                // 监听器异常不破坏其他监听器、端点与领域状态
            }
        }
    }
}
