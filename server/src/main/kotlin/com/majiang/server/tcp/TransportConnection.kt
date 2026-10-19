package com.majiang.server.tcp

import java.net.SocketAddress

/**
 * 物理传输连接抽象 (TransportConnection, D-02 / §9.2)
 *
 * 仅拥有物理连接资源与底层 I/O 能力：
 * 1. channel 与 SelectionKey；
 * 2. connectionId 与 connectionEpoch；
 * 3. 读写预算与帧编解码；
 * 4. 心跳、超时判死与状态机；
 * 5. 业务帧与控制帧发送通道。
 *
 * 逻辑角色状态 (PlayerState/SpectatorState) 与业务回执生命周期不归属物理连接。
 */
interface TransportConnection : AutoCloseable {
    val connectionId: Long
    val connectionEpoch: String
    val remoteAddress: SocketAddress
    val isClosed: Boolean
    val isClosingOrClosed: Boolean

    fun sendNormalFrame(frameBytes: ByteArray): Boolean
    fun sendControlFrame(frameBytes: ByteArray): Boolean
    fun close(reason: String, message: String? = null, immediate: Boolean = false)
}
