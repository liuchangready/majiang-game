package com.majiang.session

import com.majiang.client.LocalPlayerEndpoint
import com.majiang.event.GameEventBus
import com.majiang.state.GameState
import com.majiang.state.GameStateMachine
import com.majiang.state.SeatController

/**
 * 本地对局会话实现（兼容薄适配层）。
 *
 * 现继承自 [LocalPlayerEndpoint]，保留原构造参数并将调用转发至 engine.coordinator。
 * 内部保持 engine 与 eventBus 的私有封装，避免向外部泄露权威引擎。
 */
class LocalGameSession(
    override val viewerSeat: Int = 0,
    engine: GameStateMachine = GameStateMachine(GameState(), GameEventBus()),
    eventBus: GameEventBus = engine.eventBus,
    controllers: List<SeatController>? = null
) : LocalPlayerEndpoint(
    viewerSeat = viewerSeat,
    coordinator = engine.coordinator,
    controllers = controllers
), GameSession {
    private val engine: GameStateMachine = engine
    private val eventBus: GameEventBus = eventBus
}
