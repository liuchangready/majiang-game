package com.majiang.transport.udp

import java.util.zip.CRC32

/**
 * UDP 自封装数据报帧异常，携带稳定原因码
 *
 * 继承自 [IllegalArgumentException]，便于通用参数异常捕获与细粒度恶意包识别。
 */
class UdpFrameException(
    val reasonCode: String,
    message: String,
    cause: Throwable? = null
) : IllegalArgumentException("[$reasonCode] $message", cause)

/**
 * UDP 自封装二进制数据报结构 (UdpFrame)
 *
 * 对应设计文档：doc/SERVER_CLIENT_API.md §7.2 数据报格式：
 * 所有多字节整数采用大端序（Big-Endian），头部固定 22 字节，单个数据报总长不超过 1200 字节。
 *
 * 头部字段偏移与语义全表：
 * - 偏移 0..1 (2 字节): magic = 0x4D47 ("MG")
 * - 偏移 2 (1 字节): version = 4 (当前协议版本)
 * - 偏移 3 (1 字节): packetType (高 2 位 flags: bit7=zlib, bit6=加密预留; 低 6 位为具体包类型 ID 0x01..0x3F)
 * - 偏移 4..7 (4 字节): connectionId (服务端绑定的 32 位会话标识，未绑定为 0)
 * - 偏移 8..11 (4 字节): seq (发送方单调递增序号，每连接每方向独立)
 * - 偏移 12..15 (4 字节): ackSeq (回指对方已处理的序号，ACK/RSP 用；其余填 0)
 * - 偏移 16..17 (2 字节): payloadLen (payload 载荷实际字节数，0..1178)
 * - 偏移 18..21 (4 字节): crc32 (覆盖头部+载荷的 CRC-32 校验和，计算时本字段 4 字节清零)
 * - 偏移 22.. (N 字节): payload (具体载荷数据)
 *
 * @property packetType 对应 [UdpPacketType]，如 REQ, RSP, PUSH, ACK 等
 * @property connectionId 会话连接 ID
 * @property seq 发送方序号
 * @property ackSeq 确认序号
 * @property payload 数据载荷
 * @property flagZlib bit7 是否经 zlib 压缩
 * @property flagEncrypted bit6 是否加密
 * @property version 协议版本号，默认 4
 */
data class UdpFrame(
    val packetType: UdpPacketType,
    val connectionId: Int,
    val seq: Int,
    val ackSeq: Int = 0,
    val payload: ByteArray = EMPTY_PAYLOAD,
    val flagZlib: Boolean = false,
    val flagEncrypted: Boolean = false,
    val version: Int = UdpProtocol.VERSION
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (javaClass != other?.javaClass) return false

        other as UdpFrame

        if (packetType != other.packetType) return false
        if (connectionId != other.connectionId) return false
        if (seq != other.seq) return false
        if (ackSeq != other.ackSeq) return false
        if (!payload.contentEquals(other.payload)) return false
        if (flagZlib != other.flagZlib) return false
        if (flagEncrypted != other.flagEncrypted) return false
        if (version != other.version) return false

        return true
    }

    override fun hashCode(): Int {
        var result = packetType.hashCode()
        result = 31 * result + connectionId
        result = 31 * result + seq
        result = 31 * result + ackSeq
        result = 31 * result + payload.contentHashCode()
        result = 31 * result + flagZlib.hashCode()
        result = 31 * result + flagEncrypted.hashCode()
        result = 31 * result + version
        return result
    }

    override fun toString(): String =
        "UdpFrame(type=$packetType, connId=$connectionId, seq=$seq, ackSeq=$ackSeq, " +
            "payloadLen=${payload.size}, zlib=$flagZlib, enc=$flagEncrypted, v=$version)"

    companion object {
        val EMPTY_PAYLOAD: ByteArray = ByteArray(0)

        /**
         * 编码一帧为二进制 UDP 数据报
         */
        fun encode(frame: UdpFrame): ByteArray = UdpFrameCodec.encode(frame)

        /**
         * 解码二进制 UDP 数据报为 [UdpFrame]
         */
        fun decode(bytes: ByteArray): UdpFrame = UdpFrameCodec.decode(bytes)
    }
}

/**
 * UDP 数据报编解码器 (UdpFrameCodec)
 */
object UdpFrameCodec {

    /**
     * 编码 UDP 帧为大端序字节数组，自动填充 22 字节头部并计算 CRC-32。
     *
     * @throws IllegalArgumentException 当总数据报长度超过 [UdpProtocol.MAX_DATAGRAM_SIZE] (1200) 时抛出
     */
    fun encode(frame: UdpFrame): ByteArray {
        val payloadSize = frame.payload.size
        val totalSize = UdpProtocol.HEADER_SIZE + payloadSize
        if (totalSize > UdpProtocol.MAX_DATAGRAM_SIZE) {
            throw UdpFrameException(
                UdpProtocol.REASON_DATAGRAM_TOO_LARGE,
                "Datagram total length $totalSize exceeds maximum allowed ${UdpProtocol.MAX_DATAGRAM_SIZE} bytes"
            )
        }

        val datagram = ByteArray(totalSize)

        // 0..1: magic (0x4D47)
        datagram[0] = ((UdpProtocol.MAGIC.toInt() ushr 8) and 0xFF).toByte()
        datagram[1] = (UdpProtocol.MAGIC.toInt() and 0xFF).toByte()

        // 2: version
        datagram[2] = (frame.version and 0xFF).toByte()

        // 3: packetType & flags
        var typeByte = frame.packetType.typeId and UdpProtocol.PACKET_TYPE_MASK
        if (frame.flagZlib) typeByte = typeByte or UdpProtocol.FLAG_ZLIB
        if (frame.flagEncrypted) typeByte = typeByte or UdpProtocol.FLAG_ENCRYPTED
        datagram[3] = typeByte.toByte()

        // 4..7: connectionId
        writeInt(datagram, 4, frame.connectionId)

        // 8..11: seq
        writeInt(datagram, 8, frame.seq)

        // 12..15: ackSeq
        writeInt(datagram, 12, frame.ackSeq)

        // 16..17: payloadLen
        writeShort(datagram, 16, payloadSize.toShort())

        // 18..21: crc32 (初始化为 0)
        datagram[18] = 0
        datagram[19] = 0
        datagram[20] = 0
        datagram[21] = 0

        // 22..: payload
        if (payloadSize > 0) {
            System.arraycopy(frame.payload, 0, datagram, UdpProtocol.HEADER_SIZE, payloadSize)
        }

        // 计算覆盖头部 (crc 字段为 0) + 载荷的 CRC-32
        val crc = CRC32()
        crc.update(datagram, 0, totalSize)
        val crcValue = crc.value

        writeInt(datagram, 18, crcValue.toInt())
        return datagram
    }

    /**
     * 从大端序字节数组解码 [UdpFrame]。
     *
     * @throws UdpFrameException 当格式不符合规范时抛出（含稳定原因码）：
     * - [UdpProtocol.REASON_DATAGRAM_TOO_LARGE]: 数据报总长 > 1200
     * - [UdpProtocol.REASON_DATAGRAM_TOO_SHORT]: 数据报总长 < 22
     * - [UdpProtocol.REASON_INVALID_MAGIC]: 魔数不是 0x4D47
     * - [UdpProtocol.REASON_INVALID_VERSION]: 协议版本不匹配 (当前 4)
     * - [UdpProtocol.REASON_UNKNOWN_PACKET_TYPE]: 未知包类型
     * - [UdpProtocol.REASON_PAYLOAD_LENGTH_MISMATCH]: 载荷长度声明与剩余字节不符
     * - [UdpProtocol.REASON_CRC_MISMATCH]: CRC-32 校验失败
     */
    fun decode(bytes: ByteArray): UdpFrame {
        if (bytes.size > UdpProtocol.MAX_DATAGRAM_SIZE) {
            throw UdpFrameException(
                UdpProtocol.REASON_DATAGRAM_TOO_LARGE,
                "Datagram total length ${bytes.size} exceeds maximum allowed ${UdpProtocol.MAX_DATAGRAM_SIZE} bytes"
            )
        }
        if (bytes.size < UdpProtocol.HEADER_SIZE) {
            throw UdpFrameException(
                UdpProtocol.REASON_DATAGRAM_TOO_SHORT,
                "Datagram length ${bytes.size} is less than header size ${UdpProtocol.HEADER_SIZE} bytes"
            )
        }

        // 0..1: magic
        val magic = ((bytes[0].toInt() and 0xFF) shl 8) or (bytes[1].toInt() and 0xFF)
        if (magic.toShort() != UdpProtocol.MAGIC) {
            throw UdpFrameException(
                UdpProtocol.REASON_INVALID_MAGIC,
                "Invalid magic: 0x${magic.toString(16).uppercase()} (expected 0x${(UdpProtocol.MAGIC.toInt() and 0xFFFF).toString(16).uppercase()})"
            )
        }

        // 2: version
        val version = bytes[2].toInt() and 0xFF
        if (version != UdpProtocol.VERSION) {
            throw UdpFrameException(
                UdpProtocol.REASON_INVALID_VERSION,
                "Invalid protocol version: $version (expected ${UdpProtocol.VERSION})"
            )
        }

        // 3: packetType & flags
        val typeByte = bytes[3].toInt() and 0xFF
        val flagZlib = (typeByte and UdpProtocol.FLAG_ZLIB) != 0
        val flagEncrypted = (typeByte and UdpProtocol.FLAG_ENCRYPTED) != 0
        val typeId = typeByte and UdpProtocol.PACKET_TYPE_MASK
        val packetType = UdpPacketType.fromTypeId(typeId)
            ?: throw UdpFrameException(
                UdpProtocol.REASON_UNKNOWN_PACKET_TYPE,
                "Unknown packet type ID: $typeId"
            )

        // 4..7: connectionId
        val connectionId = readInt(bytes, 4)

        // 8..11: seq
        val seq = readInt(bytes, 8)

        // 12..15: ackSeq
        val ackSeq = readInt(bytes, 12)

        // 16..17: payloadLen
        val payloadLen = readShort(bytes, 16).toInt() and 0xFFFF
        val actualPayloadLen = bytes.size - UdpProtocol.HEADER_SIZE
        if (payloadLen != actualPayloadLen) {
            throw UdpFrameException(
                UdpProtocol.REASON_PAYLOAD_LENGTH_MISMATCH,
                "Payload length field ($payloadLen) does not match remaining bytes ($actualPayloadLen)"
            )
        }

        // 18..21: crc32
        val storedCrc = readInt(bytes, 18).toLong() and 0xFFFFFFFFL

        // 计算预期 CRC32（校验时 crc 字段视为 0）
        val crc = CRC32()
        crc.update(bytes, 0, 18)
        crc.update(0)
        crc.update(0)
        crc.update(0)
        crc.update(0)
        if (payloadLen > 0) {
            crc.update(bytes, UdpProtocol.HEADER_SIZE, payloadLen)
        }
        val expectedCrc = crc.value

        if (storedCrc != expectedCrc) {
            throw UdpFrameException(
                UdpProtocol.REASON_CRC_MISMATCH,
                "CRC32 verification failed: stored=0x${storedCrc.toString(16).uppercase()}, expected=0x${expectedCrc.toString(16).uppercase()}"
            )
        }

        val payload = if (payloadLen > 0) {
            val p = ByteArray(payloadLen)
            System.arraycopy(bytes, UdpProtocol.HEADER_SIZE, p, 0, payloadLen)
            p
        } else {
            UdpFrame.EMPTY_PAYLOAD
        }

        return UdpFrame(
            packetType = packetType,
            connectionId = connectionId,
            seq = seq,
            ackSeq = ackSeq,
            payload = payload,
            flagZlib = flagZlib,
            flagEncrypted = flagEncrypted,
            version = version
        )
    }

    private fun writeInt(dest: ByteArray, offset: Int, value: Int) {
        dest[offset] = (value ushr 24).toByte()
        dest[offset + 1] = (value ushr 16).toByte()
        dest[offset + 2] = (value ushr 8).toByte()
        dest[offset + 3] = value.toByte()
    }

    private fun writeShort(dest: ByteArray, offset: Int, value: Short) {
        val v = value.toInt()
        dest[offset] = (v ushr 8).toByte()
        dest[offset + 1] = v.toByte()
    }

    private fun readInt(src: ByteArray, offset: Int): Int {
        return ((src[offset].toInt() and 0xFF) shl 24) or
            ((src[offset + 1].toInt() and 0xFF) shl 16) or
            ((src[offset + 2].toInt() and 0xFF) shl 8) or
            (src[offset + 3].toInt() and 0xFF)
    }

    private fun readShort(src: ByteArray, offset: Int): Short {
        return (((src[offset].toInt() and 0xFF) shl 8) or (src[offset + 1].toInt() and 0xFF)).toShort()
    }
}
