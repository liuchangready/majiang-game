package com.majiang.access

import java.util.Collections
import java.util.LinkedHashSet

/**
 * 访问端点能力声明
 *
 * 显式声明协议版本、支持的游戏人数、是否支持远程传输、持久化存储以及断线/重同步能力。
 */
data class AccessCapabilities(
    val protocolVersion: Int = 3,
    val supportedPlayerCounts: Set<Int> = setOf(4),
    val remoteTransport: Boolean = false,
    val persistence: Boolean = false,
    val resumption: Boolean = true
) {
    init {
        require(protocolVersion >= 1) { "protocolVersion must be >= 1, got $protocolVersion" }
        require(supportedPlayerCounts.isNotEmpty()) { "supportedPlayerCounts cannot be empty" }
    }

    fun deepCopy(): AccessCapabilities = AccessCapabilities(
        protocolVersion = protocolVersion,
        supportedPlayerCounts = Collections.unmodifiableSet(LinkedHashSet(supportedPlayerCounts)),
        remoteTransport = remoteTransport,
        persistence = persistence,
        resumption = resumption
    )
}
