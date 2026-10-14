package com.majiang.client.session

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.ServerApiException
import com.majiang.transport.udp.UdpFrameException
import java.io.IOException
import java.util.concurrent.CompletionException
import java.util.concurrent.ExecutionException

/**
 * 重连错误分类器 (MR-02)
 *
 * 遵循改进计划与契约要求：
 * 1. 业务失败保留稳定 code 和原 cause；网络 IOException 保留传输故障身份。
 * 2. 逐层解包 CompletionException/ExecutionException，不能按文案 contains 猜永久失败。
 * 3. 永久终止只接受明确契约原因（房间不存在、token 无效、主动关闭/替换等）；
 *    未知异常与临时 IOException 即使文案含 CLOSED/INVALID 也必须有界退避重试。
 * 4. 如映射 UdpFrameException，字段是 reasonCode，不能将所有协议异常都等同永久 token 失败。
 */
object ReconnectErrorClassifier {

    /**
     * 明确导致重连永久终止的标准错误码集合 (对齐 ApiErrors / ApiProtocol)
     */
    val PERMANENT_ERROR_CODES: Set<String> = setOf(
        ApiErrors.ROOM_NOT_FOUND,
        ApiErrors.INVALID_TOKEN,
        ApiErrors.ALREADY_BOUND,
        ApiProtocol.ROOM_CLOSED,
        ApiProtocol.DISPLACED_BY_NEW_CONNECTION,
        ApiProtocol.DISCONNECTED_BY_NEW_CONNECTION,
        ApiProtocol.SERVER_SHUTDOWN,
        ApiProtocol.LOCAL_CLOSED
    )

    /**
     * 严格全字匹配的稳定原因集合（用于未包装成 ServerApiException 的历史测试桩/原始异常）
     * 严禁 contains 模糊匹配，防止误伤 "Socket closed" 等网络异常。
     */
    val EXACT_PERMANENT_REASONS: Set<String> = setOf(
        ApiErrors.ROOM_NOT_FOUND,
        ApiErrors.INVALID_TOKEN,
        ApiErrors.ALREADY_BOUND,
        ApiProtocol.ROOM_CLOSED,
        ApiProtocol.DISPLACED_BY_NEW_CONNECTION,
        ApiProtocol.DISCONNECTED_BY_NEW_CONNECTION,
        ApiProtocol.SERVER_SHUTDOWN,
        ApiProtocol.LOCAL_CLOSED,
        "INVALID_RESUME_TOKEN",
        "ROOM_CLOSED",
        "TOKEN_INVALID"
    )

    /**
     * 逐层解包 CompletionException 与 ExecutionException，提取真正的底层异常。
     */
    fun unwrap(throwable: Throwable?): Throwable? {
        var root = throwable
        while ((root is CompletionException || root is ExecutionException) && root.cause != null) {
            root = root.cause
        }
        return root
    }

    /**
     * 判断会话恢复 (resumeSession / resumeSpectator) 失败是否为永久性终止错误。
     */
    fun isPermanent(throwable: Throwable?): Boolean {
        if (throwable == null) return false
        val root = unwrap(throwable) ?: return false

        // 1. 网络与传输层故障始终为临时故障，即使文案含 "CLOSED"/"INVALID" 也必须有界退避重试
        if (root is IOException) {
            return false
        }

        // 2. 结构化服务端业务异常：按稳定 code 分类
        if (root is ServerApiException) {
            return root.code in PERMANENT_ERROR_CODES
        }

        // 3. UDP 帧异常：检查 reasonCode 字段，超时等网络层异常绝不判定为永久 token 失败
        if (root is UdpFrameException) {
            return root.reasonCode in PERMANENT_ERROR_CODES
        }

        // 4. 未知/普通异常：仅当其 message 严格等于明确契约原因时才判定为永久终止，严禁 contains 模糊匹配
        val msg = root.message?.trim().orEmpty()
        return msg in EXACT_PERMANENT_REASONS
    }

    /**
     * 提取用于会话终止的稳定原因字符串。
     */
    fun extractReason(throwable: Throwable?): String {
        if (throwable == null) return "RESUME_FAILED"
        val root = unwrap(throwable) ?: return "RESUME_FAILED"

        return when (root) {
            is ServerApiException -> root.code
            is UdpFrameException -> root.reasonCode
            else -> root.message?.takeIf { it.isNotBlank() } ?: "RESUME_FAILED"
        }
    }
}
