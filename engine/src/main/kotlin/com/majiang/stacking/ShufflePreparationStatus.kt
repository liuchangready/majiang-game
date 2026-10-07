package com.majiang.stacking

/**
 * 洗牌准备阶段状态
 */
enum class ShufflePreparationStatus {
    SELECTING,   // 倒计时多选准备中
    GENERATING   // 准备倒计时结束，牌山生成与发牌中
}
