package com.majiang.cheat

import com.majiang.event.*
import kotlin.random.Random

/**
 * 怀疑度与视线系统
 *
 * 每个 AI 拥有独立的视线状态和怀疑度：
 * - gazeTarget: 当前注视的玩家 (null = 看自己手牌)
 * - suspicion[i]: 对玩家 i 的怀疑度 [0.0, 100.0]
 * - personality: 影响视线切换频率和怀疑增长速率
 */
class SuspicionSystem(private val eventBus: GameEventBus) {

    enum class SuspicionPersonality(
        val displayName: String,
        val gazeShiftInterval: Float,   // 视线切换平均间隔（秒）
        val suspicionGrowthRate: Float,  // 怀疑增长速率倍率
        val bustThreshold: Float,        // 抓包怀疑度阈值
        val naturalDecayRate: Float      // 怀疑度自然衰减速率 (每秒)
    ) {
        OBLIVIOUS("迟钝型", 4.0f, 0.5f, 90f, 0.5f),
        NORMAL("普通型", 2.5f, 1.0f, 75f, 0.3f),
        SHARP("敏锐型", 1.5f, 1.5f, 60f, 0.2f),
        PARANOID("偏执型", 0.8f, 2.0f, 45f, 0.1f)
    }

    data class AIObserver(
        val playerIndex: Int,
        val personality: SuspicionPersonality,
        var gazeTarget: Int? = null,         // null = looking at own hand
        val suspicion: FloatArray = FloatArray(4) { 0f },
        var timeSinceLastShift: Float = 0f,
        var nextShiftInterval: Float = 0f
    )

    private val observers = mutableListOf<AIObserver>()
    private var currentTurn: Int = 0

    /**
     * 初始化 AI 观察者
     * @param aiIndices AI 玩家的座位索引列表
     * @param personalities 对应的性格类型，若为 null 则随机分配
     */
    fun initialize(aiIndices: List<Int>, personalities: List<SuspicionPersonality>? = null) {
        observers.clear()
        for ((i, idx) in aiIndices.withIndex()) {
            val personality = personalities?.getOrNull(i)
                ?: SuspicionPersonality.entries.random()
            observers.add(AIObserver(
                playerIndex = idx,
                personality = personality,
                nextShiftInterval = randomShiftInterval(personality)
            ))
        }
    }

    /**
     * 每帧更新视线状态（随机切换注视目标）和怀疑度自然衰减
     */
    fun update(delta: Float, turnNumber: Int) {
        currentTurn = turnNumber
        for (observer in observers) {
            // 视线切换
            observer.timeSinceLastShift += delta
            if (observer.timeSinceLastShift >= observer.nextShiftInterval) {
                val oldTarget = observer.gazeTarget
                observer.gazeTarget = selectNewGazeTarget(observer)
                observer.timeSinceLastShift = 0f
                observer.nextShiftInterval = randomShiftInterval(observer.personality)

                if (oldTarget != observer.gazeTarget) {
                    eventBus.publish(GameEvent.GazeShifted(
                        turnNumber = currentTurn,
                        observer = observer.playerIndex,
                        newTarget = observer.gazeTarget,
                        oldTarget = oldTarget
                    ))
                }
            }

            // 怀疑度自然衰减
            for (i in 0 until 4) {
                if (observer.suspicion[i] > 0f) {
                    observer.suspicion[i] = (observer.suspicion[i] - observer.personality.naturalDecayRate * delta)
                        .coerceAtLeast(0f)
                }
            }
        }
    }

    /**
     * 检查指定玩家是否正被任何 AI 注视
     */
    fun isBeingWatched(player: Int): Boolean =
        observers.any { it.gazeTarget == player }

    /**
     * 获取正在注视指定玩家的所有 AI 观察者
     */
    fun getWatchers(player: Int): List<AIObserver> =
        observers.filter { it.gazeTarget == player }

    /**
     * 增加怀疑度
     */
    fun addSuspicion(target: Int, observer: Int, baseAmount: Float) {
        val obs = observers.find { it.playerIndex == observer } ?: return
        val oldValue = obs.suspicion[target]
        val amount = baseAmount * obs.personality.suspicionGrowthRate
        obs.suspicion[target] = (oldValue + amount).coerceIn(0f, 100f)

        eventBus.publish(GameEvent.SuspicionChanged(
            turnNumber = currentTurn,
            target = target,
            observer = observer,
            oldValue = oldValue,
            newValue = obs.suspicion[target]
        ))
    }

    /**
     * 怀疑度超过阈值则触发 Busted
     * @return 抓包者的座位索引，或 null
     */
    fun checkBusted(target: Int): Int? {
        for (obs in observers) {
            if (obs.suspicion[target] >= obs.personality.bustThreshold && obs.gazeTarget == target) {
                return obs.playerIndex
            }
        }
        return null
    }

    /**
     * 声东击西：强制所有 AI 视线转向干扰源
     * @param diversionTarget 干扰源位置（通常是某个方向或特定玩家）
     * @param durationSeconds 视线被吸引的持续时间
     */
    fun applyDiversion(diversionTarget: Int?, durationSeconds: Float = 3.0f) {
        for (obs in observers) {
            val oldTarget = obs.gazeTarget
            obs.gazeTarget = diversionTarget
            obs.timeSinceLastShift = 0f
            obs.nextShiftInterval = durationSeconds // 强制在持续时间内不切换

            eventBus.publish(GameEvent.GazeShifted(
                turnNumber = currentTurn,
                observer = obs.playerIndex,
                newTarget = diversionTarget,
                oldTarget = oldTarget
            ))
        }
    }

    /**
     * 获取指定 AI 对某玩家的怀疑度
     */
    fun getSuspicion(observer: Int, target: Int): Float =
        observers.find { it.playerIndex == observer }?.suspicion?.get(target) ?: 0f

    /**
     * 获取指定 AI 当前视线目标
     */
    fun getGazeTarget(observer: Int): Int? =
        observers.find { it.playerIndex == observer }?.gazeTarget

    /**
     * 重置所有怀疑度和视线
     */
    fun reset() {
        for (obs in observers) {
            obs.suspicion.fill(0f)
            obs.gazeTarget = null
            obs.timeSinceLastShift = 0f
        }
    }

    private fun selectNewGazeTarget(observer: AIObserver): Int? {
        // 怀疑度高的玩家更容易被盯
        val candidates = (0 until 4).filter { it != observer.playerIndex }
        val weights = candidates.map { 1f + observer.suspicion[it] / 20f }
        val totalWeight = weights.sum() + 1f // +1 for looking at own hand

        var roll = Random.nextFloat() * totalWeight
        // Check own hand first
        roll -= 1f
        if (roll < 0) return null // Look at own hand

        for ((i, candidate) in candidates.withIndex()) {
            roll -= weights[i]
            if (roll < 0) return candidate
        }
        return candidates.lastOrNull()
    }

    private fun randomShiftInterval(personality: SuspicionPersonality): Float {
        val base = personality.gazeShiftInterval
        return base * (0.7f + Random.nextFloat() * 0.6f) // ±30% 随机波动
    }
}
