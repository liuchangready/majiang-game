package com.majiang.event

import kotlin.reflect.KClass

/**
 * 类型安全的游戏事件总线
 *
 * 核心设计：
 * - 逻辑层通过 publish() 发布事件
 * - 表现层通过 subscribe() 订阅感兴趣的事件类型
 * - 支持优先级（数字越小越先执行，默认0）
 * - 支持事件历史回放（用于断线重连/录像回放）
 * - 隔离防御：总线发布入参、保存历史、各监听器、getHistory 返回值之间分别深拷贝
 */
class GameEventBus {

    // Use KClass as key for type safety
    private val listeners = mutableMapOf<KClass<*>, MutableList<PrioritizedListener<*>>>()
    private val history = mutableListOf<GameEvent>()
    private var recordHistory = true

    private data class PrioritizedListener<T>(
        val priority: Int,
        val handler: (T) -> Unit
    )

    /**
     * 订阅指定类型的事件
     * @param priority 优先级（数字越小越先执行，默认0）
     * @param handler 事件处理回调
     */
    inline fun <reified T : GameEvent> subscribe(priority: Int = 0, noinline handler: (T) -> Unit) {
        subscribe(T::class, priority, handler)
    }

    @Suppress("UNCHECKED_CAST")
    fun <T : GameEvent> subscribe(type: KClass<T>, priority: Int = 0, handler: (T) -> Unit) {
        val list = listeners.getOrPut(type) { mutableListOf() }
        list.add(PrioritizedListener(priority, handler))
        list.sortBy { it.priority }
    }

    /**
     * 发布事件到所有订阅者
     */
    @Suppress("UNCHECKED_CAST")
    fun publish(event: GameEvent) {
        val baseSnapshot = snapshotGameEvent(event)
        if (recordHistory) {
            history.add(snapshotGameEvent(baseSnapshot))
        }

        val matchedListeners = if (listeners.containsKey(GameEvent::class)) {
            val list = mutableListOf<PrioritizedListener<*>>()
            listeners[event::class]?.let { list.addAll(it) }
            listeners[GameEvent::class]?.let { list.addAll(it) }
            list.sortBy { it.priority }
            list
        } else {
            listeners[event::class]?.toList() ?: return
        }

        for (listener in matchedListeners) {
            try {
                val isolatedEvent = snapshotGameEvent(baseSnapshot)
                (listener.handler as (GameEvent) -> Unit)(isolatedEvent)
            } catch (t: Throwable) {
                // 监听器异常不得中止已提交操作链，也不得使其他监听器无法接收事件 (DESIGN.md 4.3 & ACCEPTANCE.md E01)
            }
        }
    }

    /** 获取事件历史（返回防御性深拷贝列表） */
    fun getHistory(): List<GameEvent> = history.map { snapshotGameEvent(it) }

    /** 清除所有订阅 */
    fun clearSubscriptions() = listeners.clear()

    /** 清除事件历史 */
    fun clearHistory() = history.clear()

    /** 完全重置 */
    fun reset() {
        clearSubscriptions()
        clearHistory()
    }

    /** 设置是否记录历史 */
    fun setRecordHistory(enabled: Boolean) {
        recordHistory = enabled
    }

    /** 回放历史事件（用于重连/录像） */
    fun replayHistory() {
        val saved = recordHistory
        recordHistory = false
        try {
            for (event in history.toList()) {
                publish(event)
            }
        } finally {
            recordHistory = saved
        }
    }
}
