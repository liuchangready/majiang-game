package com.majiang.session

import com.majiang.client.PlayerEndpoint

/**
 * 表现层与领域层解耦的核心会话接口（兼容适配层）。
 *
 * 现继承自统一客户端端点契约 [PlayerEndpoint]，保留现有测试与旧调用者兼容性。
 */
interface GameSession : PlayerEndpoint
