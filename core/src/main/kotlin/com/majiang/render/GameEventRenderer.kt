package com.majiang.render

import com.majiang.event.*

/**
 * 事件驱动渲染器 — 将游戏事件翻译为视觉表现
 *
 * 设计原则：
 * 1. 渲染器不持有 GameState 引用
 * 2. 所有动画/特效由事件触发
 * 3. 动画完毕后通过回调通知逻辑层可以继续
 *
 * 具体的动画实现（绘制逻辑）由各个具体的 GameAnimation 子类负责，
 * 本类只负责事件→动画的映射和队列管理。
 */
class GameEventRenderer(
    private val eventBus: GameEventBus,
    private val animationQueue: AnimationQueue = AnimationQueue()
) {
    /** 动画工厂接口 — 允许外部注入具体动画实现 */
    interface AnimationFactory {
        fun createDrawAnimation(player: Int, wallRemaining: Int): AnimationQueue.GameAnimation?
        fun createDiscardAnimation(player: Int, isRiichiDiscard: Boolean): AnimationQueue.GameAnimation?
        fun createChiAnimation(player: Int): AnimationQueue.GameAnimation?
        fun createPonAnimation(player: Int): AnimationQueue.GameAnimation?
        fun createKanAnimation(player: Int, kanType: String): AnimationQueue.GameAnimation?
        fun createRiichiAnimation(player: Int): AnimationQueue.GameAnimation?
        fun createTsumoAnimation(player: Int, score: Int): AnimationQueue.GameAnimation?
        fun createRonAnimation(winner: Int, loser: Int, score: Int): AnimationQueue.GameAnimation?
        fun createCheatSuccessAnimation(technique: CheatTechnique): AnimationQueue.GameAnimation?
        fun createCheatFailAnimation(technique: CheatTechnique): AnimationQueue.GameAnimation?
        fun createBustedAnimation(player: Int, caughtBy: Int): AnimationQueue.GameAnimation?
        fun createGazeShiftAnimation(observer: Int, newTarget: Int?): AnimationQueue.GameAnimation?
        fun createDoraRevealAnimation(isUraDora: Boolean): AnimationQueue.GameAnimation?
    }

    /** 默认动画工厂（占位符，只打日志） */
    private var factory: AnimationFactory = DefaultAnimationFactory()

    fun setAnimationFactory(factory: AnimationFactory) {
        this.factory = factory
    }

    /** 在 GameScreen.show() 中调用，注册所有事件监听 */
    fun registerListeners() {
        eventBus.subscribe<GameEvent.TileDrawn> { e ->
            factory.createDrawAnimation(e.player, e.wallRemaining)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.TileDiscarded> { e ->
            factory.createDiscardAnimation(e.player, e.isRiichiDiscard)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.ChiDeclared> { e ->
            factory.createChiAnimation(e.player)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.PonDeclared> { e ->
            factory.createPonAnimation(e.player)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.KanDeclared> { e ->
            factory.createKanAnimation(e.player, e.kanType.name)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.RiichiDeclared> { e ->
            factory.createRiichiAnimation(e.player)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.TsumoWin> { e ->
            factory.createTsumoAnimation(e.player, e.score)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.RonWin> { e ->
            factory.createRonAnimation(e.winner, e.loser, e.score)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.CheatAttempted> { e ->
            val anim = if (e.success) {
                factory.createCheatSuccessAnimation(e.technique)
            } else {
                factory.createCheatFailAnimation(e.technique)
            }
            anim?.let { animationQueue.enqueue(it) }
        }

        eventBus.subscribe<GameEvent.BustedEvent> { e ->
            factory.createBustedAnimation(e.player, e.caughtBy)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.GazeShifted> { e ->
            factory.createGazeShiftAnimation(e.observer, e.newTarget)?.let {
                animationQueue.enqueue(it)
            }
        }

        eventBus.subscribe<GameEvent.DoraRevealed> { e ->
            factory.createDoraRevealAnimation(e.isUraDora)?.let {
                animationQueue.enqueue(it)
            }
        }
    }

    /** 每帧更新动画队列 */
    fun update(delta: Float) {
        animationQueue.update(delta)
    }

    /** 动画队列是否空闲 */
    val isIdle: Boolean get() = animationQueue.isIdle

    /** 注销所有监听 */
    fun unregisterListeners() {
        eventBus.clearSubscriptions()
    }

    /** 默认动画工厂（日志输出，用于开发调试） */
    private class DefaultAnimationFactory : AnimationFactory {
        private fun logAnimation(name: String, duration: Float = 0.3f) = object : AnimationQueue.GameAnimation() {
            override val duration = duration
            override val name = name
            override fun onStart() {
                println("[动画] 开始: $name")
            }
            override fun onFinish() {
                println("[动画] 完成: $name")
            }
        }

        override fun createDrawAnimation(player: Int, wallRemaining: Int) = logAnimation("摸牌 P$player (剩余$wallRemaining)")
        override fun createDiscardAnimation(player: Int, isRiichiDiscard: Boolean) = logAnimation("打牌 P$player${if (isRiichiDiscard) " [立直]" else ""}")
        override fun createChiAnimation(player: Int) = logAnimation("吃 P$player", 0.5f)
        override fun createPonAnimation(player: Int) = logAnimation("碰 P$player", 0.5f)
        override fun createKanAnimation(player: Int, kanType: String) = logAnimation("杠 P$player ($kanType)", 0.6f)
        override fun createRiichiAnimation(player: Int) = logAnimation("立直 P$player", 0.8f)
        override fun createTsumoAnimation(player: Int, score: Int) = logAnimation("自摸 P$player (${score}点)", 1.5f)
        override fun createRonAnimation(winner: Int, loser: Int, score: Int) = logAnimation("荣和 P$winner ← P$loser (${score}点)", 1.5f)
        override fun createCheatSuccessAnimation(technique: CheatTechnique) = logAnimation("千术成功: $technique", 0.8f)
        override fun createCheatFailAnimation(technique: CheatTechnique) = logAnimation("千术失败: $technique", 0.5f)
        override fun createBustedAnimation(player: Int, caughtBy: Int) = logAnimation("抓包! P$player 被 P$caughtBy 发现", 2.0f)
        override fun createGazeShiftAnimation(observer: Int, newTarget: Int?) = logAnimation("视线 P$observer → ${newTarget ?: "手牌"}", 0.2f)
        override fun createDoraRevealAnimation(isUraDora: Boolean) = logAnimation("${if (isUraDora) "裏" else ""}Dora揭示", 0.6f)
    }
}
