package com.majiang.transport.tcp

import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

/**
 * 解码结果封闭类 (支持结果式与异常式消费)
 */
sealed class TcpDecodeResult {
    /** 成功解码出一组完整帧（可能为空，表示尚需更多字节） */
    data class Success(val frames: List<TcpFrame>) : TcpDecodeResult()

    /** 遇到终态不可恢复协议错误 */
    data class Fatal(
        val reasonCode: String,
        val message: String,
        val cause: Throwable? = null
    ) : TcpDecodeResult()
}

/**
 * 有状态 TCP 二进制帧解码器 (TcpFrameDecoder, 契约 2 / UC-F01 ~ UC-F08)
 *
 * 每连接独立持有一个解码器实例。
 *
 * 核心契约与校验顺序（严格遵守）：
 * 1. 魔数校验：前 4 字节必须等于 [TcpWire.MAGIC_INT] ("MJTC")，不匹配即判定 [TcpCloseReasons.PROTOCOL_VIOLATION]
 * 2. 线路版本校验：wireVersion 必须等于 [TcpWire.WIRE_VERSION] (1)，不匹配即判定 [TcpCloseReasons.VERSION_MISMATCH]
 * 3. 帧类型校验：type 必须在 1..10 之间，不匹配即判定 [TcpCloseReasons.PROTOCOL_VIOLATION]
 * 4. 标志位校验：flags 必须全为 0，不匹配即判定 [TcpCloseReasons.PROTOCOL_VIOLATION]
 * 5. 载荷长度校验：以无符号 32 位 Long 读取并判定 1..1,048,576（严禁先转 Int 造成负数绕过），越界即判定 [TcpCloseReasons.PROTOCOL_VIOLATION]
 * 6. UTF-8 校验：载荷组装完成后使用 CharsetDecoder(REPORT) 校验，坏 UTF-8 即判定 [TcpCloseReasons.PROTOCOL_VIOLATION]
 *
 * 六类非法（UC-F05）均进入不可恢复终态：
 * - 记录终态原因，清空暂存与组装缓冲，此后任何输入不再产出任何帧，不扫描流尝试找回同步。
 *
 * 内存有界约束（UC-F08）：
 * - 头部解析暂存最多 12 字节（<= 64 KiB scratch limit）。
 * - 载荷缓冲仅在载荷分段实际到达时增量持有，绝不在收到头部时立即为 1 MiB 预分配空缓冲，防止内存放大攻击。
 */
class TcpFrameDecoder(
    private val reserveBudget: (Long) -> Boolean = { true },
    private val releaseBudget: (Long) -> Unit = {}
) {

    /** 是否已进入不可恢复终态错误 */
    var isFatalError: Boolean = false
        private set

    /** 终态错误原因码 (对应 [TcpCloseReasons]) */
    var fatalReasonCode: String? = null
        private set

    /** 终态错误详细描述 */
    var fatalMessage: String? = null
        private set

    /** 终态错误的底层原因 */
    var fatalCause: Throwable? = null
        private set

    /** 头部拼装暂存区（固定 12 字节） */
    private val headerScratch: ByteBuffer = ByteBuffer.allocate(TcpWire.HEADER_SIZE)

    /** 当前已解析的头部元数据（null 表示正在等待头部） */
    private var currentHeader: ParsedHeader? = null

    /** 当前帧已收到的体字节数 */
    private var receivedPayloadBytes: Int = 0

    /** 当前解码器在全局预算中已实际预留的载荷字节数 */
    private var reservedPayloadBytes: Long = 0L

    /** 当前帧载荷片段收集器（按实际到达的分段存储，杜绝提前预分配 1 MiB） */
    private val payloadChunks: MutableList<ByteArray> = ArrayList()

    private data class ParsedHeader(
        val type: Int,
        val flags: Short,
        val wireVersion: Int,
        val payloadLength: Int
    )

    /**
     * 当前是否存在未完成的半帧（半头或半体）
     */
    fun hasIncompleteFrame(): Boolean =
        headerScratch.position() > 0 || currentHeader != null

    /**
     * 以异常风格解码输入的字节数据 (ByteBuffer)
     *
     * @param buffer 包含入站网络字节的缓冲区
     * @return 完整解码出的 [TcpFrame] 列表
     * @throws TcpProtocolException 当检测到非法帧或处于终态时抛出
     */
    fun decode(buffer: ByteBuffer): List<TcpFrame> {
        return when (val result = decodeResult(buffer)) {
            is TcpDecodeResult.Success -> result.frames
            is TcpDecodeResult.Fatal -> throw TcpProtocolException(
                result.reasonCode,
                result.message,
                result.cause
            )
        }
    }

    /**
     * 以异常风格解码输入的字节数组段
     */
    fun decode(bytes: ByteArray, offset: Int = 0, length: Int = bytes.size): List<TcpFrame> {
        val buf = ByteBuffer.wrap(bytes, offset, length)
        return decode(buf)
    }

    /**
     * 以结果风格解码输入的字节数据 (ByteBuffer)
     *
     * 无论成功还是失败，均更新解码器内部终态。若已处于终态，后续调用不产出任何帧。
     */
    fun decodeResult(buffer: ByteBuffer): TcpDecodeResult {
        if (isFatalError) {
            return TcpDecodeResult.Fatal(
                fatalReasonCode ?: TcpCloseReasons.PROTOCOL_VIOLATION,
                fatalMessage ?: "Decoder is in fatal error state",
                fatalCause
            )
        }

        val completedFrames = mutableListOf<TcpFrame>()

        while (buffer.hasRemaining() && !isFatalError) {
            // 阶段 1：解析头部
            if (currentHeader == null) {
                if (headerScratch.position() == 0 && buffer.remaining() >= TcpWire.HEADER_SIZE) {
                    // 零拷贝快速路径：直接从当前 buffer 读取 12 字节头部
                    if (!parseAndValidateHeader(buffer)) {
                        break
                    }
                } else {
                    // 暂存拼装路径：分片喂入
                    val needed = TcpWire.HEADER_SIZE - headerScratch.position()
                    val toCopy = Math.min(needed, buffer.remaining())
                    val oldLimit = buffer.limit()
                    buffer.limit(buffer.position() + toCopy)
                    headerScratch.put(buffer)
                    buffer.limit(oldLimit)

                    if (headerScratch.position() == TcpWire.HEADER_SIZE) {
                        headerScratch.flip()
                        val valid = parseAndValidateHeader(headerScratch)
                        headerScratch.clear()
                        if (!valid) {
                            break
                        }
                    } else {
                        // 头部尚不完整，等待后续输入
                        break
                    }
                }
            }

            // 阶段 2：收集体字节
            val header = currentHeader
            if (header != null) {
                val needed = header.payloadLength - receivedPayloadBytes
                val toRead = Math.min(needed, buffer.remaining())
                if (toRead > 0) {
                    if (!reserveBudget(toRead.toLong())) {
                        markFatal(
                            TcpCloseReasons.SLOW_CONSUMER,
                            "Global memory budget exceeded while reading frame payload"
                        )
                        break
                    }
                    reservedPayloadBytes += toRead.toLong()
                    val chunk = ByteArray(toRead)
                    buffer.get(chunk)
                    payloadChunks.add(chunk)
                    receivedPayloadBytes += toRead
                }

                if (receivedPayloadBytes == header.payloadLength) {
                    // 载荷收集完成，拼接组装
                    val payload: ByteArray
                    if (payloadChunks.size == 1) {
                        payload = payloadChunks[0]
                    } else {
                        val assembleBytes = header.payloadLength.toLong()
                        if (!reserveBudget(assembleBytes)) {
                            markFatal(
                                TcpCloseReasons.SLOW_CONSUMER,
                                "Global memory budget exceeded while assembling frame payload"
                            )
                            break
                        }
                        try {
                            payload = assemblePayload(payloadChunks, header.payloadLength)
                        } finally {
                            releaseBudget(assembleBytes)
                        }
                    }

                    // 阶段 3：严格 UTF-8 校验 (REPORT)
                    if (!isStrictUtf8(payload)) {
                        markFatal(
                            TcpCloseReasons.PROTOCOL_VIOLATION,
                            "Payload of frame type ${header.type} is not valid UTF-8"
                        )
                        break
                    }

                    completedFrames.add(
                        TcpFrame(
                            type = header.type,
                            payload = payload,
                            flags = header.flags,
                            wireVersion = header.wireVersion
                        )
                    )

                    // 帧载荷预算转移给完成帧集合持有
                    reservedPayloadBytes -= header.payloadLength.toLong()

                    // 重置，准备接收下一帧
                    currentHeader = null
                    receivedPayloadBytes = 0
                    payloadChunks.clear()
                }
            }
        }

        return if (isFatalError) {
            if (completedFrames.isNotEmpty()) {
                for (f in completedFrames) {
                    releaseBudget(f.payload.size.toLong())
                }
                completedFrames.clear()
            }
            TcpDecodeResult.Fatal(
                fatalReasonCode ?: TcpCloseReasons.PROTOCOL_VIOLATION,
                fatalMessage ?: "Fatal decoding error",
                fatalCause
            )
        } else {
            TcpDecodeResult.Success(completedFrames)
        }
    }

    /**
     * 以结果风格解码输入的字节数组段
     */
    fun decodeResult(bytes: ByteArray, offset: Int = 0, length: Int = bytes.size): TcpDecodeResult {
        val buf = ByteBuffer.wrap(bytes, offset, length)
        return decodeResult(buf)
    }

    /**
     * 流结束 (EOF) 处理 (UC-F06)
     *
     * 若在半头或半体时流结束，未完整帧不派发，缓冲全部释放，进入终态并抛出异常。
     * 若为干净的帧边界结束，则安全返回。
     *
     * @throws TcpProtocolException 当在未完整帧时遇到 EOF 抛出
     */
    fun onEndOfStream() {
        if (isFatalError) return

        if (hasIncompleteFrame()) {
            val detail = if (currentHeader != null) {
                "Stream ended unexpectedly with incomplete frame body (received $receivedPayloadBytes of ${currentHeader!!.payloadLength} bytes)"
            } else {
                "Stream ended unexpectedly with incomplete frame header (${headerScratch.position()} of ${TcpWire.HEADER_SIZE} bytes)"
            }
            markFatal(TcpCloseReasons.PROTOCOL_VIOLATION, detail)
            throw TcpProtocolException(TcpCloseReasons.PROTOCOL_VIOLATION, detail)
        }
    }

    /**
     * 校验并解析 12 字节头部
     *
     * 校验顺序：魔数 -> wireVersion -> type -> flags -> payloadLength (Long)
     *
     * @return true 表示头部合法并已写入 [currentHeader]；false 表示非法并已进入终态
     */
    private fun parseAndValidateHeader(source: ByteBuffer): Boolean {
        // 1. 魔数校验
        val magic = source.getInt()
        if (magic != TcpWire.MAGIC_INT) {
            val hex = Integer.toHexString(magic).uppercase()
            val expectedHex = Integer.toHexString(TcpWire.MAGIC_INT).uppercase()
            markFatal(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Invalid magic: 0x$hex; expected 0x$expectedHex"
            )
            return false
        }

        // 2. wireVersion 校验
        val wireVersion = source.get().toInt() and 0xFF
        if (wireVersion != TcpWire.WIRE_VERSION) {
            markFatal(
                TcpCloseReasons.VERSION_MISMATCH,
                "Unsupported wireVersion: $wireVersion; expected ${TcpWire.WIRE_VERSION}"
            )
            return false
        }

        // 3. type 校验 (1..10)
        val type = source.get().toInt() and 0xFF
        if (!TcpWire.isValidFrameType(type)) {
            markFatal(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Invalid frame type: $type; must be in ${TcpWire.FRAME_TYPES}"
            )
            return false
        }

        // 4. flags 校验 (v1 必须为 0)
        val flags = source.getShort().toInt() and 0xFFFF
        if (flags != 0) {
            markFatal(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Invalid flags: $flags; wire version 1 requires flags == 0"
            )
            return false
        }

        // 5. payloadLength 校验 (无符号 Long，严禁先转 Int)
        val rawLengthInt = source.getInt()
        val payloadLengthLong = rawLengthInt.toLong() and 0xFFFFFFFFL
        if (payloadLengthLong < TcpWire.MIN_PAYLOAD_BYTES || payloadLengthLong > TcpWire.MAX_PAYLOAD_BYTES) {
            markFatal(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Payload length $payloadLengthLong out of valid bounds (${TcpWire.MIN_PAYLOAD_BYTES}..${TcpWire.MAX_PAYLOAD_BYTES})"
            )
            return false
        }

        currentHeader = ParsedHeader(
            type = type,
            flags = flags.toShort(),
            wireVersion = wireVersion,
            payloadLength = payloadLengthLong.toInt()
        )
        receivedPayloadBytes = 0
        payloadChunks.clear()
        return true
    }

    /**
     * 进入不可恢复终态，清理所有在拼装缓冲并释放已预留预算
     */
    private fun markFatal(
        reasonCode: String,
        message: String,
        cause: Throwable? = null
    ) {
        isFatalError = true
        fatalReasonCode = reasonCode
        fatalMessage = message
        fatalCause = cause
        clearAndRelease()
    }

    /**
     * 清理所有在拼装缓冲并安全释放解码器持有的全局预算
     */
    fun clearAndRelease() {
        if (reservedPayloadBytes > 0L) {
            releaseBudget(reservedPayloadBytes)
            reservedPayloadBytes = 0L
        }
        headerScratch.clear()
        payloadChunks.clear()
        currentHeader = null
        receivedPayloadBytes = 0
    }

    /**
     * 拼接组装片段字节
     */
    private fun assemblePayload(chunks: List<ByteArray>, totalLength: Int): ByteArray {
        if (chunks.size == 1) {
            return chunks[0]
        }
        val result = ByteArray(totalLength)
        var pos = 0
        for (chunk in chunks) {
            System.arraycopy(chunk, 0, result, pos, chunk.size)
            pos += chunk.size
        }
        return result
    }

    /**
     * 校验载荷字节是否为严格 UTF-8 (REPORT)
     */
    private fun isStrictUtf8(bytes: ByteArray): Boolean {
        val decoder = Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
        return try {
            decoder.decode(ByteBuffer.wrap(bytes))
            true
        } catch (_: Exception) {
            false
        }
    }
}
