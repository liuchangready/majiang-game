package com.majiang.server.api

import java.util.concurrent.CompletionException
import java.util.concurrent.ExecutionException

/**
 * 传输与业务共享的服务端 API 业务异常 (MR-02 统一契约)。
 *
 * 携带稳定的业务错误码 [code]（通常对应 [ApiErrors] 或 [ApiProtocol] 常量），
 * 保持进程内与远端传输（TCP）业务失败的一致性与可比较性，同时保留底层原始原因 [cause]。
 *
 * 继承 [IllegalStateException] 以保持对现有捕获 [IllegalStateException] 的调用方兼容。
 */
open class ServerApiException(
    val code: String,
    message: String = code,
    cause: Throwable? = null
) : IllegalStateException(message, cause) {
    override fun toString(): String =
        if (cause != null) "ServerApiException(code='$code', message='$message', cause=$cause)"
        else "ServerApiException(code='$code', message='$message')"

    companion object {
        /**
         * 逐层解包 CompletionException 与 ExecutionException，提取真正的底层原因。
         */
        fun unwrap(throwable: Throwable?): Throwable? {
            var root = throwable
            while ((root is CompletionException || root is ExecutionException) && root.cause != null) {
                root = root.cause
            }
            return root
        }
    }
}
