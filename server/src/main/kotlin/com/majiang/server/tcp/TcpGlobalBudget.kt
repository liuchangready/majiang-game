package com.majiang.server.tcp

import java.util.concurrent.atomic.AtomicLong

/**
 * TCP 服务器全局网络内存预算记账器 (TcpGlobalBudget, 契约 1 / UC-L03)
 *
 * 全局预算 256 MiB 预留制：
 * 1. 记账范围涵盖所有连接的读暂存、写队列、解码暂存与接收串行邮箱的实际保留字节；
 * 2. 预留后再分配 (reserve-before-allocate)：当增加请求使内存超出上限时立即返回 false 拒绝准入；
 * 3. 释放时原子扣减，确保全局字节数严格受控，彻底杜绝无界积压与内存放大攻击。
 */
class TcpGlobalBudget(val maxBytes: Long = 256L * 1024 * 1024) {

    private val allocatedBytes = AtomicLong(0L)

    /** 当前已实际预留/占用的总字节数 */
    val currentAllocatedBytes: Long get() = allocatedBytes.get()

    /**
     * 尝试预留指定字节数
     *
     * @param bytes 欲预留的字节数 (<= 0 直接返回 true)
     * @return true 表示预算充足且已成功扣减；false 表示预算不足被拒绝
     */
    fun reserve(bytes: Long): Boolean {
        if (bytes <= 0L) return true
        while (true) {
            val current = allocatedBytes.get()
            if (current + bytes > maxBytes) {
                return false
            }
            if (allocatedBytes.compareAndSet(current, current + bytes)) {
                return true
            }
        }
    }

    /**
     * 释放此前已预留的字节数
     *
     * @param bytes 归还的字节数 (<= 0 为无操作)
     */
    fun release(bytes: Long) {
        if (bytes <= 0L) return
        while (true) {
            val current = allocatedBytes.get()
            val newBytes = (current - bytes).coerceAtLeast(0L)
            if (allocatedBytes.compareAndSet(current, newBytes)) {
                return
            }
        }
    }

    /**
     * 重置所有预算记账（仅在停服或测试重置时使用）
     */
    fun reset() {
        allocatedBytes.set(0L)
    }
}
