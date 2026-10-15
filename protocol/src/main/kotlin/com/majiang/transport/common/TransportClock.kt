package com.majiang.transport.common

/**
 * 传输中性时钟接口 (TransportClock)
 *
 * 用于解耦系统墙钟与本地单调时钟（D10/D11）。
 * 生产环境默认注入系统时钟 [SYSTEM]，单元测试注入确定性虚拟时钟，
 * 严禁业务与网络代码散落使用 System.currentTimeMillis() 或 System.nanoTime()。
 */
fun interface TransportClock {
    /**
     * 获取当前墙钟毫秒时间戳（用于业务展示、绝对时间戳记录）
     */
    fun currentTimeMillis(): Long

    /**
     * 获取当前本地单调纳秒时间戳（用于 RTT 测量、心跳周期与超时截止计算，禁止比较两机墙钟）
     */
    fun nanoTime(): Long = System.nanoTime()

    companion object {
        /** 默认系统时钟实现 */
        val SYSTEM: TransportClock = object : TransportClock {
            override fun currentTimeMillis(): Long = System.currentTimeMillis()
            override fun nanoTime(): Long = System.nanoTime()
        }
    }
}

/**
 * 可取消的定时任务句柄
 */
fun interface Cancellable {
    /**
     * 取消未执行的任务；若任务已执行或已取消则为无操作 (No-Op)
     */
    fun cancel()
}

/**
 * 传输中性定时调度接口 (TransportScheduler)
 *
 * 用于解耦后台线程调度与延迟重试/心跳驱动，单测可注入确定性虚拟调度器，
 * 严禁散落使用 Thread.sleep()。
 */
interface TransportScheduler {
    /**
     * 调度指定任务在 [delayMillis] 毫秒后执行
     *
     * @param delayMillis 延迟毫秒数（<= 0 表示立即调度）
     * @param task 待执行的任务
     * @return 可用于取消任务的 [Cancellable]
     */
    fun schedule(delayMillis: Long, task: Runnable): Cancellable
}
