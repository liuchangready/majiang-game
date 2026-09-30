package com.majiang.application

import com.majiang.client.LocalPlayerEndpoint
import com.majiang.state.ActionResult
import com.majiang.state.SeatController
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 本地牌桌调度器 (LocalTableRuntime) - 与客户端分离的本地宿主运行时。
 *
 * 核心职责：
 * 1. 宿主与端点解耦：持有 ApplicationCoordinator(deferAutomaticActions=true) 与 LocalPlayerEndpoint；
 * 2. 动画感知的动作节奏调度：动画忙时不推进也不累积欠账；
 * 3. 稳态心跳驱动：空闲至少 0.15 秒才调用一次 advanceAutomaticActions；一帧最多一次，不用 while 追赶；
 * 4. 健壮容错：忽略非法/负 delta，大 delta 只触发一次；close 后不推进；无计时线程，无 GL 依赖；
 * 5. 权限安全：真人选择、响应等待、自摸与合法暗杠由 coordinator 保持暂停，绝不越权代选或自动 Pass。
 */
class LocalTableRuntime(
    val coordinator: ApplicationCoordinator = ApplicationCoordinator(deferAutomaticActions = true),
    endpoint: LocalPlayerEndpoint? = null
) : AutoCloseable {

    init {
        require(coordinator.deferAutomaticActions) {
            "LocalTableRuntime requires an ApplicationCoordinator with deferAutomaticActions = true"
        }
    }

    val endpoint: LocalPlayerEndpoint = endpoint ?: LocalPlayerEndpoint(viewerSeat = 0, coordinator = coordinator)

    constructor(
        coordinator: ApplicationCoordinator,
        viewerSeat: Int,
        controllers: List<SeatController>? = null
    ) : this(
        coordinator = coordinator,
        endpoint = LocalPlayerEndpoint(viewerSeat = viewerSeat, coordinator = coordinator, controllers = controllers)
    )

    private val isClosed = AtomicBoolean(false)
    private var idleAccumulator: Float = 0f

    val closed: Boolean
        get() = isClosed.get()

    /**
     * 主循环推进驱动。
     *
     * 规则与节奏：
     * - presentationBusy 为 true 时立即复位 idle 累积并不推进；
     * - delta <= 0f 或非有限浮点数直接忽略；
     * - 空闲时间达到 0.15 秒时触发单次 coordinator.advanceAutomaticActions()；
     * - 触发后立即复位累积为 0f，不循环追赶欠账，大 delta 仅触发一次；
     * - 调度器已关闭 (close) 则不再推进。
     */
    fun update(delta: Float, presentationBusy: Boolean): ActionResult? {
        if (isClosed.get()) return null
        if (presentationBusy) {
            idleAccumulator = 0f
            return null
        }
        if (delta <= 0f || !delta.isFinite()) {
            return null
        }

        idleAccumulator += delta
        if (idleAccumulator >= IDLE_THRESHOLD_SECONDS) {
            idleAccumulator = 0f
            return coordinator.advanceAutomaticActions()
        }
        return null
    }

    override fun close() {
        if (isClosed.compareAndSet(false, true)) {
            idleAccumulator = 0f
            endpoint.close()
        }
    }

    companion object {
        const val IDLE_THRESHOLD_SECONDS: Float = 0.15f
    }
}
