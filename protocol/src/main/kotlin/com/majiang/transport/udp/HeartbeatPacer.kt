package com.majiang.transport.udp


/**
 * UDP 心跳起搏器与链路保活监控 (HeartbeatPacer)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §7.3、§7.6 心跳与超时参数。
 * - 3 秒间隔向对端发送 PING（携带 clientMillis 客户端发送时间戳）；
 * - 收到对端 PONG（携带 clientMillis 与 serverMillis）时，计算采样 RTT 并通过 EWMA 平滑估计链路时延；
 * - 连续 3 次未收到 PONG 期望应答，判定连接死亡（MISSED_HEARTBEATS），触发判死回调；
 * - 连续 10 秒无任何入站数据包，判定连接超时死亡（READ_TIMEOUT），触发判死回调；
 * - 时间和调度全部依赖注入的 [UdpClock] 和 [UdpScheduler]，零真实系统休眠。
 */
class HeartbeatPacer(
    private val clock: UdpClock,
    private val scheduler: UdpScheduler,
    private val pingSender: (clientMillis: Long) -> Unit,
    private val onDead: (reason: String) -> Unit,
    private val pingIntervalMillis: Long = UdpProtocol.HEARTBEAT_PING_INTERVAL_MILLIS,
    private val maxMissedPongs: Int = UdpProtocol.HEARTBEAT_MAX_MISSED_PONGS,
    private val inboundTimeoutMillis: Long = UdpProtocol.INBOUND_TIMEOUT_MILLIS,
    private val rttEwmaAlpha: Double = UdpProtocol.RTT_EWMA_ALPHA
) : AutoCloseable {

    companion object {
        const val PING_PAYLOAD_SIZE: Int = 8
        const val PONG_PAYLOAD_SIZE: Int = 16

        /**
         * 编码 PING 载荷 (8 字节大端 clientMillis)
         */
        fun encodePingPayload(clientMillis: Long): ByteArray {
            val buf = ByteArray(PING_PAYLOAD_SIZE)
            for (i in 0..7) {
                buf[i] = ((clientMillis ushr (56 - i * 8)) and 0xFF).toByte()
            }
            return buf
        }

        /**
         * 解码 PING 载荷中的 clientMillis
         */
        fun decodePingPayload(payload: ByteArray): Long {
            require(payload.size >= PING_PAYLOAD_SIZE) { "PING payload size ${payload.size} < 8" }
            var result = 0L
            for (i in 0..7) {
                result = (result shl 8) or (payload[i].toLong() and 0xFF)
            }
            return result
        }

        /**
         * 编码 PONG 载荷 (16 字节大端: clientMillis + serverMillis)
         */
        fun encodePongPayload(clientMillis: Long, serverMillis: Long): ByteArray {
            val buf = ByteArray(PONG_PAYLOAD_SIZE)
            for (i in 0..7) {
                buf[i] = ((clientMillis ushr (56 - i * 8)) and 0xFF).toByte()
            }
            for (i in 0..7) {
                buf[8 + i] = ((serverMillis ushr (56 - i * 8)) and 0xFF).toByte()
            }
            return buf
        }

        /**
         * 解码 PONG 载荷中的 (clientMillis, serverMillis)
         */
        fun decodePongPayload(payload: ByteArray): Pair<Long, Long> {
            require(payload.size >= PONG_PAYLOAD_SIZE) { "PONG payload size ${payload.size} < 16" }
            var cMillis = 0L
            var sMillis = 0L
            for (i in 0..7) {
                cMillis = (cMillis shl 8) or (payload[i].toLong() and 0xFF)
            }
            for (i in 0..7) {
                sMillis = (sMillis shl 8) or (payload[8 + i].toLong() and 0xFF)
            }
            return Pair(cMillis, sMillis)
        }
    }

    private var running = false
    private var dead = false
    private var lastInboundTime: Long = clock.currentTimeMillis()
    private var missedPongs: Int = 0
    private var awaitingPong: Boolean = false
    private var estimatedRttMillis: Double = 0.0
    private var scheduledTask: Cancellable? = null

    /**
     * 启动心跳检测与保活起搏
     */
    @Synchronized
    fun start() {
        if (running || dead) return
        running = true
        lastInboundTime = clock.currentTimeMillis()
        missedPongs = 0
        awaitingPong = false
        scheduleTick(pingIntervalMillis)
    }

    /**
     * 收到任何合法入站数据报时的通知钩子，刷新最后入站保活时间戳
     */
    @Synchronized
    fun onInboundPacket(timestamp: Long = clock.currentTimeMillis()) {
        lastInboundTime = timestamp
    }

    /**
     * 收到对端 PONG 响应时的处理逻辑
     *
     * @param clientMillis PONG 中回显的客户端发送时刻
     * @param serverMillis 服务端时间戳
     * @param arrivalTime 当前到达时间
     */
    @Synchronized
    fun onPong(clientMillis: Long, serverMillis: Long, arrivalTime: Long = clock.currentTimeMillis()) {
        onInboundPacket(arrivalTime)
        val sampleRtt = Math.max(0L, arrivalTime - clientMillis)
        if (estimatedRttMillis <= 0.0) {
            estimatedRttMillis = sampleRtt.toDouble()
        } else {
            estimatedRttMillis = rttEwmaAlpha * estimatedRttMillis + (1.0 - rttEwmaAlpha) * sampleRtt.toDouble()
        }
        awaitingPong = false
        missedPongs = 0
    }

    private fun scheduleTick(delayMillis: Long) {
        if (!running || dead) return
        scheduledTask = scheduler.schedule(delayMillis) {
            tick()
        }
    }

    private fun tick() {
        synchronized(this) {
            if (!running || dead) return

            val now = clock.currentTimeMillis()

            // 1. 检查入站总超时：10 秒内无任何入站包即判死
            if (now - lastInboundTime >= inboundTimeoutMillis) {
                markDead(UdpProtocol.REASON_READ_TIMEOUT)
                return
            }

            // 2. 检查前次 PING 的 PONG 应答：若等待仍未返回，累计失步次数
            if (awaitingPong) {
                missedPongs++
                if (missedPongs >= maxMissedPongs) {
                    markDead(UdpProtocol.REASON_MISSED_HEARTBEATS)
                    return
                }
            }

            // 3. 发送新一轮 PING
            awaitingPong = true
            pingSender(now)

            // 调度下一次周期 tick
            scheduleTick(pingIntervalMillis)
        }
    }

    private fun markDead(reason: String) {
        if (dead) return
        dead = true
        running = false
        scheduledTask?.cancel()
        onDead(reason)
    }

    /**
     * 显式触发保活检查（如外部主动探测）
     */
    @Synchronized
    fun checkLiveness(now: Long = clock.currentTimeMillis()): Boolean {
        if (dead) return false
        if (now - lastInboundTime >= inboundTimeoutMillis) {
            markDead(UdpProtocol.REASON_READ_TIMEOUT)
            return false
        }
        return true
    }

    /**
     * 获取通过 EWMA 平滑估计的往返时延 RTT (毫秒)
     */
    @Synchronized
    fun getEstimatedRtt(): Double = estimatedRttMillis

    /**
     * 获取当前连续丢失 PONG 的计数
     */
    @Synchronized
    fun getMissedPongs(): Int = missedPongs

    /**
     * 当前连接是否已被判定为死亡
     */
    @Synchronized
    fun isDead(): Boolean = dead

    /**
     * 获取最后一次收到入站包的时间戳
     */
    @Synchronized
    fun getLastInboundTime(): Long = lastInboundTime

    override fun close() {
        synchronized(this) {
            running = false
            scheduledTask?.cancel()
        }
    }
}
