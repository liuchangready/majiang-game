package com.majiang.transport.udp

/**
 * UDP 协议栈时钟接口
 *
 * 用于解耦系统时钟，生产环境默认注入系统时间，单测注入虚拟时钟进行确定性时间推进，
 * 严禁业务代码散落使用 System.currentTimeMillis()。
 */
fun interface UdpClock {
    /**
     * 获取当前毫秒时间戳
     */
    fun currentTimeMillis(): Long

    companion object {
        /** 默认系统时钟实现 */
        val SYSTEM: UdpClock = UdpClock { System.currentTimeMillis() }
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
 * UDP 协议栈定时调度接口
 *
 * 用于解耦后台线程与延迟重传/心跳驱动，单测注入假调度器，严禁散落使用 Thread.sleep()。
 */
interface UdpScheduler {
    /**
     * 调度指定任务在 [delayMillis] 毫秒后执行
     *
     * @param delayMillis 延迟毫秒数
     * @param task 待执行的任务
     * @return 可用于取消任务的 [Cancellable]
     */
    fun schedule(delayMillis: Long, task: Runnable): Cancellable
}
