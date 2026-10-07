package com.majiang.timing

import java.util.concurrent.atomic.AtomicLong

/**
 * 权威决策时钟 (DecisionClock)
 *
 * 负责麻将单局与整场范围内的决策思考计时、免费时间与储备扣减、截止判定与暂停恢复。
 * 纯 JVM 实现，不持有后台系统定时器线程，全部时序状态转移由外部调用单调驱动。
 *
 * 契约规范：
 * - [beginRound]: 重复调用同局不刷新余额，ROUND 作用域新局重置，SESSION 作用域不重置；结算旧局未完成决策耗时并关闭旧局计时；
 * - [open]: 同决策重复不刷新，必须匹配当前 activeRoundId，不允许同座位同时打开另一阻塞决策；
 * - [timing]: 查询座位当前活跃决策计时（座次严格验证 0..3）；
 * - [remainingReserve]: 查询座位剩余储备毫秒数（座次严格验证 0..3）；
 * - [complete]: 仅在有效、未暂停且截止前完成，恰好达到 deadline 拒绝；未到 opensAt 可免费完成，结算一次；
 * - [expired]: 查询当前已超时的活跃决策列表，查询不消费；
 * - [expire]: 超时结算并关闭，校验 key 与 generation，必须到期且未暂停时结算，旧定时器或未到期无副作用；
 * - [cancel]: 取消决策，结算已耗时（暂停期间只结算暂停前已用时）并扣减储备，不得白送新时间；
 * - [rekey]: 同座位原子替换决策键（权威资源操作仅改 decisionId 时使用），完整保留截止与储备语义；
 * - [pause] / [resume]: 暂停与恢复，严格保留免费时间与储备时长，不重置免费时间；暂停期间不可完成与超时。
 */
class DecisionClock(
    val config: TimeControlConfig = TimeControlConfig(),
    val clock: MonotonicClock = SystemMonotonicClock
) {
    private val generationCounter = AtomicLong(0L)
    private val reserves = mutableMapOf<Int, Long>()
    private val activeTimings = mutableMapOf<Int, DecisionTiming>()

    private var currentRoundId: String? = null
    private var isPaused: Boolean = false
    private var pausedAt: Long? = null

    /** 获取当前活跃局标识 */
    val activeRoundId: String?
        @Synchronized get() = currentRoundId

    /** 是否处于全局暂停状态 */
    val isPausedState: Boolean
        @Synchronized get() = isPaused

    private fun checkSeat(seat: Int) {
        require(seat in 0..3) { "Seat must be in 0..3, got $seat" }
    }

    /**
     * 开启新局
     *
     * - 重复调用相同 roundId 不刷新已有余额；
     * - 先结算旧局中所有未结算活跃计时的已消耗储备时间，再关闭旧局计时；
     * - 若作用域为 [ReserveScope.ROUND]，新局重置全部座位初始储备时间；
     * - 若作用域为 [ReserveScope.SESSION]，新局保留已有结算后储备余额，未初始化座位赋予初始储备。
     */
    @Synchronized
    fun beginRound(roundId: String) {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        if (currentRoundId == roundId) {
            // 重复调用同局不刷新余额
            return
        }

        // 先结算旧活动计时的已用时，防止跨局白送余额
        val effectiveNow = if (isPaused && pausedAt != null) pausedAt!! else clock.nowMillis()
        for ((seat, timing) in activeTimings) {
            if (timing.status != DecisionTimingStatus.CLOSED) {
                val effectiveAtMillis = if (timing.status == DecisionTimingStatus.PAUSED && pausedAt != null) {
                    minOf(effectiveNow, pausedAt!!)
                } else {
                    effectiveNow
                }
                if (effectiveAtMillis > timing.opensAtMillis) {
                    val elapsed = Math.subtractExact(effectiveAtMillis, timing.opensAtMillis)
                    val overBase = maxOf(0L, elapsed - timing.baseMillis)
                    val reserveSpent = minOf(timing.reserveAtOpenMillis, overBase)
                    reserves[seat] = timing.reserveAtOpenMillis - reserveSpent
                } else {
                    reserves[seat] = timing.reserveAtOpenMillis
                }
            }
        }

        // 关闭旧局计时
        activeTimings.clear()
        isPaused = false
        pausedAt = null

        if (config.reserveScope == ReserveScope.ROUND) {
            reserves.clear()
            for (seat in 0..3) {
                reserves[seat] = config.reserveMillis
            }
        } else {
            // SESSION 作用域：已有余额不重置，确保 0..3 具备默认储备
            for (seat in 0..3) {
                if (!reserves.containsKey(seat)) {
                    reserves[seat] = config.reserveMillis
                }
            }
        }

        currentRoundId = roundId
    }

    /**
     * 为指定决策开启计时
     *
     * - 检查 key.roundId 与当前 activeRoundId 匹配；
     * - 同一决策重复调用不刷新，直接返回已有决策对象；
     * - 同一座位若已存在另一未关闭的活跃决策，抛出 [IllegalStateException]；
     * - 截止时间 deadline = opensAt + baseMillis + reserveAtOpen（检查溢出）。
     */
    @Synchronized
    fun open(key: DecisionKey, opensAtMillis: Long = clock.nowMillis()): DecisionTiming {
        checkSeat(key.seat)
        if (currentRoundId == null) {
            currentRoundId = key.roundId
        } else {
            require(key.roundId == currentRoundId) {
                "Decision roundId '${key.roundId}' does not match current active roundId '$currentRoundId'"
            }
        }

        val existing = activeTimings[key.seat]
        if (existing != null) {
            if (existing.key == key) {
                // 同决策重复不刷新
                return existing
            } else {
                // 不允许同座位同时打开另一阻塞决策
                throw IllegalStateException(
                    "Seat ${key.seat} already has an active decision [${existing.key}], cannot open another decision [${key}]"
                )
            }
        }

        val reserveAtOpen = remainingReserve(key.seat)
        val totalBudget = Math.addExact(config.baseMillis, reserveAtOpen)
        val deadline = Math.addExact(opensAtMillis, totalBudget)
        val gen = generationCounter.incrementAndGet()

        val timing = DecisionTiming(
            key = key,
            generation = gen,
            opensAtMillis = opensAtMillis,
            deadlineMillis = deadline,
            reserveAtOpenMillis = reserveAtOpen,
            baseMillis = config.baseMillis,
            status = if (isPaused) DecisionTimingStatus.PAUSED else DecisionTimingStatus.ACTIVE
        )
        activeTimings[key.seat] = timing
        return timing
    }

    /**
     * 查询指定座位当前活跃的决策计时
     */
    @Synchronized
    fun timing(seat: Int): DecisionTiming? {
        checkSeat(seat)
        return activeTimings[seat]
    }

    /**
     * 查询指定座位的当前剩余储备时间（毫秒）
     */
    @Synchronized
    fun remainingReserve(seat: Int): Long {
        checkSeat(seat)
        return reserves.getOrPut(seat) { config.reserveMillis }
    }

    /**
     * 正常完成决策并结算耗时
     *
     * - 暂停期间不接受正常完成命令；
     * - 仅在有效且严格早于截止时间（atMillis < deadlineMillis）时返回 true；
     * - 恰好等于或晚于 deadlineMillis 时拒绝并返回 false（应由超时流程处理）；
     * - 若 atMillis <= opensAtMillis（提前完成或未到开表点），免费完成不扣除储备；
     * - 结算一次并关闭计时。
     */
    @Synchronized
    fun complete(key: DecisionKey, atMillis: Long = clock.nowMillis()): Boolean {
        checkSeat(key.seat)
        if (isPaused) return false
        val current = activeTimings[key.seat] ?: return false
        if (current.key != key) return false
        if (current.status != DecisionTimingStatus.ACTIVE) return false

        // 恰好 deadline 拒绝
        if (atMillis >= current.deadlineMillis) {
            return false
        }

        if (atMillis > current.opensAtMillis) {
            val elapsed = Math.subtractExact(atMillis, current.opensAtMillis)
            val overBase = maxOf(0L, elapsed - current.baseMillis)
            val reserveSpent = minOf(current.reserveAtOpenMillis, overBase)
            val newReserve = current.reserveAtOpenMillis - reserveSpent
            reserves[key.seat] = newReserve
        } else {
            // 未到 opensAt 可免费完成
            reserves[key.seat] = current.reserveAtOpenMillis
        }

        activeTimings.remove(key.seat)
        return true
    }

    /**
     * 查询当前已超时的活跃决策列表
     *
     * 纯只读查询，不消费、不关闭决策、不修改储备。
     */
    @Synchronized
    fun expired(nowMillis: Long = clock.nowMillis()): List<DecisionTiming> {
        if (isPaused) {
            return emptyList()
        }
        return activeTimings.values.filter { timing ->
            timing.status == DecisionTimingStatus.ACTIVE && nowMillis >= timing.deadlineMillis
        }
    }

    /**
     * 决策超时结算
     *
     * - 严格校验 key、generation 与当前时间（nowMillis >= deadlineMillis）；
     * - 暂停状态、未到期、错代次或已关闭时返回 false 且无副作用；
     * - 匹配且确实已到期时耗尽该座位全部储备（剩余储备归 0）并关闭决策。
     */
    @Synchronized
    fun expire(key: DecisionKey, generation: Long, nowMillis: Long = clock.nowMillis()): Boolean {
        checkSeat(key.seat)
        if (isPaused) return false
        val current = activeTimings[key.seat] ?: return false
        if (current.key != key) return false
        if (current.generation != generation) return false
        if (current.status != DecisionTimingStatus.ACTIVE) return false
        if (nowMillis < current.deadlineMillis) return false

        // 到期且匹配才结算并关闭，储备耗尽
        reserves[key.seat] = 0L
        activeTimings.remove(key.seat)
        return true
    }

    /**
     * 取消决策（例如窗口因他家裁决提前关闭不再需要该玩家）
     *
     * 结算截至取消时刻（若处于暂停则只结算暂停前已用时）所消耗的时间并扣减储备，关闭计时窗口；不得白送新时间。
     */
    @Synchronized
    fun cancel(key: DecisionKey, atMillis: Long = clock.nowMillis()): Boolean {
        checkSeat(key.seat)
        val current = activeTimings[key.seat] ?: return false
        if (current.key != key) return false
        if (current.status == DecisionTimingStatus.CLOSED) return false

        val effectiveAtMillis = if ((isPaused || current.status == DecisionTimingStatus.PAUSED) && pausedAt != null) {
            minOf(atMillis, pausedAt!!)
        } else {
            atMillis
        }

        if (effectiveAtMillis > current.opensAtMillis) {
            val elapsed = Math.subtractExact(effectiveAtMillis, current.opensAtMillis)
            val overBase = maxOf(0L, elapsed - current.baseMillis)
            val reserveSpent = minOf(current.reserveAtOpenMillis, overBase)
            val newReserve = current.reserveAtOpenMillis - reserveSpent
            reserves[key.seat] = newReserve
        } else {
            reserves[key.seat] = current.reserveAtOpenMillis
        }

        activeTimings.remove(key.seat)
        return true
    }

    /**
     * 原子替换座位当前活跃计时的决策键
     *
     * 用于权威资源操作（出千记账）仅提升 round.version 而改变 decisionId 的场景：
     * 决策在逻辑上仍是同一手牌决策（行动权、选项集合不变），迁移键时严格保留
     * opensAt/deadline/generation 之外的全部时序语义，绝不重置免费时间、不消耗储备、
     * 也不产生新的截止时间——防止资源操作以一次免费的新 deadline 重开当前决策。
     *
     * - 新旧键必须属于同一座位；
     * - 座位无活跃计时或当前键不匹配时返回 false 且无副作用；
     * - 新旧键相同时幂等返回 true。
     */
    @Synchronized
    fun rekey(oldKey: DecisionKey, newKey: DecisionKey): Boolean {
        checkSeat(oldKey.seat)
        checkSeat(newKey.seat)
        require(oldKey.seat == newKey.seat) {
            "rekey must keep the same seat: $oldKey -> $newKey"
        }
        val current = activeTimings[oldKey.seat] ?: return false
        if (current.key != oldKey) return false
        if (oldKey == newKey) return true
        activeTimings[oldKey.seat] = current.copy(key = newKey)
        return true
    }

    /**
     * 暂停时钟
     *
     * 冻结所有活跃决策的耗时，状态置为 PAUSED。
     */
    @Synchronized
    fun pause(nowMillis: Long = clock.nowMillis()) {
        if (isPaused) return
        isPaused = true
        pausedAt = nowMillis
        for ((seat, timing) in activeTimings) {
            if (timing.status == DecisionTimingStatus.ACTIVE) {
                activeTimings[seat] = timing.copy(status = DecisionTimingStatus.PAUSED)
            }
        }
    }

    /**
     * 恢复时钟
     *
     * 将暂停期间流逝的时间补偿到 opensAtMillis 与 deadlineMillis，确保免费时间与储备时间不被消耗也不重置。
     */
    @Synchronized
    fun resume(nowMillis: Long = clock.nowMillis()) {
        if (!isPaused) return
        val pausedDuration = if (pausedAt != null && nowMillis > pausedAt!!) {
            Math.subtractExact(nowMillis, pausedAt!!)
        } else {
            0L
        }

        for ((seat, timing) in activeTimings) {
            if (timing.status == DecisionTimingStatus.PAUSED) {
                activeTimings[seat] = timing.copy(
                    opensAtMillis = Math.addExact(timing.opensAtMillis, pausedDuration),
                    deadlineMillis = Math.addExact(timing.deadlineMillis, pausedDuration),
                    status = DecisionTimingStatus.ACTIVE
                )
            }
        }
        isPaused = false
        pausedAt = null
    }

    /**
     * 重置所有状态（回到初始空状态）
     *
     * 注意：保持 generationCounter 单调递增，不重置为 0，防止旧 timer 超时回调与后续生成的 DecisionKey 碰撞。
     */
    @Synchronized
    fun reset() {
        reserves.clear()
        activeTimings.clear()
        currentRoundId = null
        isPaused = false
        pausedAt = null
        // 不重置 generationCounter
    }
}
