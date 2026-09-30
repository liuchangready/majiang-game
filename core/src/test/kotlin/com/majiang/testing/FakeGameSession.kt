package com.majiang.testing

import com.majiang.event.GameEvent
import com.majiang.session.*
import com.majiang.state.*

/** View-only UI double. GameScreen receives neither an engine nor mutable authority. */
class FakeGameSession(private var snapshot: PlayerView) : GameSession {
    override val viewerSeat: Int get() = snapshot.viewerSeat
    private val listeners = mutableListOf<(SessionUpdate) -> Unit>()
    val submitted = mutableListOf<GameAction>()
    var started = false
    var closed = false
    override fun view(): PlayerView = snapshot
    override fun legalActions(): List<GameAction> = snapshot.legalActions
    override fun start(): ActionResult {
        started = true
        update(snapshot, listOf(GameEvent.StackingStarted()))
        return ActionResult.Accepted(emptyList())
    }
    override fun submit(action: GameAction): ActionResult {
        if (action !in legalActions()) return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
        submitted.add(action)
        return ActionResult.Accepted(emptyList())
    }
    override fun nextRound(): ActionResult = ActionResult.Accepted(emptyList())
    override fun subscribe(listener: (SessionUpdate) -> Unit): AutoCloseable {
        listeners.add(listener)
        return AutoCloseable { listeners.remove(listener) }
    }
    fun update(view: PlayerView, events: List<GameEvent> = emptyList()) {
        snapshot = view
        listeners.toList().forEach { it(SessionUpdate(view, events)) }
    }
    override fun close() { closed = true; listeners.clear() }
}
