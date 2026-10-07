package com.majiang.state

import com.majiang.ai.AIStrategy
import com.majiang.ai.BasicAIStrategy

/**
 * 权威侧座位控制者类型 (DESIGN.md 逐座位控制者与统一动作执行设计)
 *
 * 支持 0~4 个真人座位，区分本地人类、远端人类、电脑 AI 与临时托管：
 * - LocalHuman: 本地真人玩家，等待本地交互输入
 * - RemoteHuman: 远端真人玩家（领域类型，保持等待外部输入）
 * - Computer: 自治 AI 玩家，使用指定 AIStrategy 决策
 * - Takeover: 临时托管状态，使用指定 AIStrategy 决策
 *
 * 真人自动摸切为座位级独立偏好；引擎 autoRiichiDiscard 为全局总开关，两者皆为 true 时才自动摸切。
 * Computer 与 Takeover 均受引擎全局 autoPlayAi 开关控制。
 */
sealed interface SeatController {
    data class LocalHuman(val autoRiichiDiscard: Boolean = true) : SeatController
    data class RemoteHuman(val autoRiichiDiscard: Boolean = true) : SeatController
    data class Computer(val strategy: AIStrategy = BasicAIStrategy()) : SeatController
    data class Takeover(val strategy: AIStrategy = BasicAIStrategy()) : SeatController
}
