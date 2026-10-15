package com.majiang.transport.tcp

import java.util.concurrent.CompletionException
import java.util.concurrent.ExecutionException

/**
 * TCP 协议违规与协议层终态异常 (携带 D4 稳定 reasonCode)
 *
 * 用于承载魔数/版本/flags/长度非法、坏 UTF-8/JSON、外层 ID 冲突等协议违规错误。
 * 继承 [IllegalStateException] 以保持与既有调用方兼容。
 */
open class TcpProtocolException(
    val reasonCode: String,
    message: String = reasonCode,
    cause: Throwable? = null
) : IllegalStateException("[$reasonCode] $message", cause) {

    override fun toString(): String =
        if (cause != null) "TcpProtocolException(reasonCode='$reasonCode', message='$message', cause=$cause)"
        else "TcpProtocolException(reasonCode='$reasonCode', message='$message')"

    companion object {
        /**
         * 逐层解包 CompletionException 与 ExecutionException，提取真正的底层原因
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

/**
 * TCP 网络/连接/超时类临时异常
 *
 * 用于承载连接失败、握手超时、心跳判死、写停滞等网络级异常。
 */
open class TcpConnectionException(
    message: String,
    cause: Throwable? = null
) : IllegalStateException(message, cause) {

    override fun toString(): String =
        if (cause != null) "TcpConnectionException(message='$message', cause=$cause)"
        else "TcpConnectionException(message='$message')"
}
