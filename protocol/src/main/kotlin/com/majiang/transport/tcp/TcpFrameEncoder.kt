package com.majiang.transport.tcp

import java.nio.ByteBuffer

/**
 * TCP 二进制帧编码器 (TcpFrameEncoder, 契约 3 / UC-F09 / UC-F10)
 *
 * 负责产出 12 字节固定大端头部 + N 字节载荷。
 * 控制帧（PING / PONG / CLOSE）与业务数据帧同路编码，无旁路机制。
 *
 * 防护校验（UC-F09）：
 * - type 越界、payload 为空或超过 1 MiB 上限时，当场抛出 [IllegalArgumentException]，绝不产出坏帧。
 */
object TcpFrameEncoder {

    /**
     * 将指定类型与载荷编码为完整的二进制帧字节数组
     *
     * @param type 帧类型 (1..10)
     * @param payload 载荷字节 (非空，1..1,048,576 字节)
     * @param flags 标志位 (v1 必须为 0)
     * @return 12 字节头部 + payload 的连续字节数组
     * @throws IllegalArgumentException 当参数不符合规范时抛出
     */
    fun encode(
        type: Int,
        payload: ByteArray,
        flags: Short = TcpWire.FLAGS_DEFAULT
    ): ByteArray {
        if (!TcpWire.isValidFrameType(type)) {
            throw IllegalArgumentException(
                "Invalid frame type: $type; must be in ${TcpWire.FRAME_TYPES}"
            )
        }
        if (payload.isEmpty()) {
            throw IllegalArgumentException(
                "Payload must not be empty (minimum ${TcpWire.MIN_PAYLOAD_BYTES} byte)"
            )
        }
        if (payload.size > TcpWire.MAX_PAYLOAD_BYTES) {
            throw IllegalArgumentException(
                "Payload length ${payload.size} exceeds maximum ${TcpWire.MAX_PAYLOAD_BYTES} bytes"
            )
        }
        if (flags.toInt() != 0) {
            throw IllegalArgumentException(
                "Flags must be 0 in wire version 1: $flags"
            )
        }

        val totalLength = TcpWire.HEADER_SIZE + payload.size
        val buffer = ByteBuffer.allocate(totalLength)
        buffer.putInt(TcpWire.MAGIC_INT)                        // 0..3: magic (MJTC)
        buffer.put(TcpWire.WIRE_VERSION.toByte())               // 4: wireVersion (1)
        buffer.put(type.toByte())                               // 5: type (1..10)
        buffer.putShort(flags)                                  // 6..7: flags (0)
        buffer.putInt(payload.size)                             // 8..11: payloadLength (1..1,048,576)
        buffer.put(payload)                                     // 12..: payload
        return buffer.array()
    }

    /**
     * 将 [TcpFrame] 编码为完整的二进制帧字节数组
     */
    fun encode(frame: TcpFrame): ByteArray =
        encode(frame.type, frame.payload, frame.flags)

    /**
     * 将指定类型与 UTF-8 字符串载荷编码为二进制帧
     */
    fun encode(type: Int, payloadText: String): ByteArray =
        encode(type, payloadText.toByteArray(Charsets.UTF_8))

    /**
     * 将帧直接写入目标 [ByteBuffer]
     */
    fun encodeTo(
        type: Int,
        payload: ByteArray,
        destination: ByteBuffer,
        flags: Short = TcpWire.FLAGS_DEFAULT
    ) {
        val bytes = encode(type, payload, flags)
        destination.put(bytes)
    }
}
