package com.majiang.transport.tcp

import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

/**
 * TCP 二进制数据报帧实体 (TcpFrame)
 *
 * 对应 MJ-TCP/1 线路契约：
 * 固定 12 字节大端头部 + N 字节严格 UTF-8 载荷。
 *
 * 注意：由于 [ByteArray] 的默认 equals 为引用比较，本类显式重写 [equals] 和 [hashCode]，
 * 按内容断言 [ByteArray.contentEquals]，防止单测与业务产生引用相等假象陷阱。
 *
 * @property type 帧类型 ID (1..10, 对应 [TcpWire])
 * @property payload 帧载荷数据 (非空, 1..1,048,576 字节)
 * @property flags 头部标志位 (MJ-TCP/1 必须为 0)
 * @property wireVersion 线路协议版本 (MJ-TCP/1 必须为 1)
 */
data class TcpFrame(
    val type: Int,
    val payload: ByteArray,
    val flags: Short = TcpWire.FLAGS_DEFAULT,
    val wireVersion: Int = TcpWire.WIRE_VERSION
) {
    init {
        require(TcpWire.isValidFrameType(type)) {
            "Invalid frame type: $type; must be in ${TcpWire.FRAME_TYPES}"
        }
        require(payload.isNotEmpty()) {
            "Payload must not be empty (minimum ${TcpWire.MIN_PAYLOAD_BYTES} byte)"
        }
        require(payload.size <= TcpWire.MAX_PAYLOAD_BYTES) {
            "Payload size ${payload.size} exceeds maximum ${TcpWire.MAX_PAYLOAD_BYTES}"
        }
        require(flags.toInt() == 0) {
            "Flags must be 0 in wire version 1: $flags"
        }
        require(wireVersion == TcpWire.WIRE_VERSION) {
            "wireVersion must be ${TcpWire.WIRE_VERSION}: $wireVersion"
        }
    }

    /**
     * 将载荷按严格 UTF-8 解码为字符串
     *
     * 使用 [CodingErrorAction.REPORT]，坏字节立即抛出 [TcpProtocolException]。
     */
    fun payloadAsString(): String {
        val decoder = Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
        return try {
            val charBuffer = decoder.decode(ByteBuffer.wrap(payload))
            charBuffer.toString()
        } catch (e: Exception) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Payload of frame type $type is not valid UTF-8",
                e
            )
        }
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TcpFrame) return false
        return type == other.type &&
            flags == other.flags &&
            wireVersion == other.wireVersion &&
            payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int {
        var result = type
        result = 31 * result + flags.toInt()
        result = 31 * result + wireVersion
        result = 31 * result + payload.contentHashCode()
        return result
    }

    override fun toString(): String {
        return "TcpFrame(type=${TcpWire.frameTypeName(type)}($type), payloadBytes=${payload.size}, flags=$flags, wireVersion=$wireVersion)"
    }
}
