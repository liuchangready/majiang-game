package com.majiang.state

import com.majiang.ai.AIPlayer
import com.majiang.ai.TrivialAIStrategy
import com.majiang.event.GameEvent
import com.majiang.event.GameEventBus
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class HeadlessGameLoopTest {

    @Test
    fun `4个TrivialAI无头整局全自动对局测试`() {
        val state = GameState()
        val eventBus = GameEventBus()
        val fsm = GameStateMachine(state, eventBus)
        val strategy = TrivialAIStrategy()

        // 4个座位全部使用 TrivialAIStrategy，humanPlayerIndex 设为 -1
        fsm.initializeMatch(
            humanIndex = -1,
            aiStrategies = (0..3).associateWith { AIPlayer(it, strategy) }
        )

        val tsumoEvents = mutableListOf<GameEvent.TsumoWin>()
        val ronEvents = mutableListOf<GameEvent.RonWin>()
        val drawEvents = mutableListOf<GameEvent.DrawGame>()
        val roundEndedEvents = mutableListOf<GameEvent.RoundEnded>()

        eventBus.subscribe<GameEvent.TsumoWin> { tsumoEvents.add(it) }
        eventBus.subscribe<GameEvent.RonWin> { ronEvents.add(it) }
        eventBus.subscribe<GameEvent.DrawGame> { drawEvents.add(it) }
        eventBus.subscribe<GameEvent.RoundEnded> { roundEndedEvents.add(it) }

        // --- 第一局启动与终局 ---
        fsm.handleAction(GameAction.StartStacking())

        // 断言：终局后阶段必须为 ROUND_END，绝不会卡在等待人类输入
        assertEquals(GamePhase.ROUND_END, state.phase, "对局应自动流转至终局阶段 ROUND_END")

        // 断言：136 张牌物理守恒断言全程通过
        assertDoesNotThrow { state.tilePool.assertConservation() }

        // 断言：终局要么收到和牌事件（自摸/荣和），要么收到流局事件
        val hadWin = tsumoEvents.isNotEmpty() || ronEvents.isNotEmpty()
        val hadDraw = drawEvents.isNotEmpty()
        assertTrue(hadWin || hadDraw, "第一局终局必须产生和牌或流局结算事件")
        assertEquals(1, roundEndedEvents.size, "第一局应收到恰好一次 RoundEnded 事件")

        // --- 第二局启动与终局 ---
        // 终局后再次提交 StartStacking 开第二局
        fsm.handleAction(GameAction.StartStacking())

        assertEquals(GamePhase.ROUND_END, state.phase, "第二局亦应全自动顺利流转至终局")
        assertDoesNotThrow { state.tilePool.assertConservation() }
        assertEquals(2, roundEndedEvents.size, "累计应完成两局并收到两次 RoundEnded 事件")
    }
}
