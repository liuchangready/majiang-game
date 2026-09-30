package com.majiang.render

/**
 * 动画队列 — 管理游戏动画的顺序播放
 *
 * 设计原则：
 * 1. 逻辑层提交动画请求 → 渲染层按帧播放 → 播完后回调
 * 2. 支持顺序播放和并行播放
 * 3. 动画完毕后通知逻辑层可以继续
 */
class AnimationQueue {

    /** 动画基类 */
    abstract class GameAnimation {
        /** 动画时长（秒） */
        abstract val duration: Float
        
        /** 动画名称（调试用） */
        abstract val name: String
        
        /** 当前进度 0.0 ~ 1.0 */
        var progress: Float = 0f
            internal set
        
        /** 已经过的时间 */
        var elapsed: Float = 0f
            internal set
        
        /** 是否已完成 */
        var isFinished: Boolean = false
            internal set
        
        /** 完成回调 */
        var onComplete: (() -> Unit)? = null
        
        /** 更新动画状态，子类可重写以实现具体表现 */
        open fun onUpdate(progress: Float) {}
        
        /** 动画开始时调用 */
        open fun onStart() {}
        
        /** 动画结束时调用 */
        open fun onFinish() {}
        
        internal fun update(delta: Float) {
            if (isFinished) return
            if (elapsed == 0f) onStart()
            
            elapsed += delta
            progress = if (duration > 0f) (elapsed / duration).coerceIn(0f, 1f) else 1f
            onUpdate(progress)
            
            if (elapsed >= duration) {
                isFinished = true
                onFinish()
                onComplete?.invoke()
            }
        }
    }
    
    /** 并行动画组（所有动画同时播放，等最长的结束） */
    class ParallelGroup(val animations: List<GameAnimation>) : GameAnimation() {
        override val duration: Float = animations.maxOfOrNull { it.duration } ?: 0f
        override val name: String = "Parallel[${animations.joinToString { it.name }}]"
        
        override fun onUpdate(progress: Float) {
            // Each animation updates independently
        }
        
        internal fun updateAll(delta: Float) {
            for (anim in animations) {
                anim.update(delta)
            }
        }
    }
    
    private val queue = ArrayDeque<GameAnimation>()
    private var current: GameAnimation? = null
    
    /** 添加动画到队列末尾 */
    fun enqueue(animation: GameAnimation) {
        queue.addLast(animation)
    }
    
    /** 添加并行动画组 */
    fun enqueueParallel(animations: List<GameAnimation>) {
        if (animations.isNotEmpty()) {
            queue.addLast(ParallelGroup(animations))
        }
    }
    
    /** 添加延迟（空白等待） */
    fun enqueueDelay(seconds: Float) {
        queue.addLast(object : GameAnimation() {
            override val duration = seconds
            override val name = "Delay(${seconds}s)"
        })
    }
    
    /** 每帧更新 */
    fun update(delta: Float) {
        // Start next if current is done or absent
        if (current == null || current!!.isFinished) {
            current = if (queue.isNotEmpty()) queue.removeFirst() else null
        }
        
        val active = current ?: return
        
        if (active is ParallelGroup) {
            active.updateAll(delta)
            // Check if all done
            if (active.animations.all { it.isFinished }) {
                active.isFinished = true
                active.onComplete?.invoke()
            }
        } else {
            active.update(delta)
        }
    }
    
    /** 队列是否为空且当前无播放中动画 */
    val isIdle: Boolean get() = current == null || (current!!.isFinished && queue.isEmpty())
    
    /** 队列中等待播放的动画数量 */
    val pendingCount: Int get() = queue.size + if (current?.isFinished == false) 1 else 0
    
    /** 清空队列 */
    fun clear() {
        queue.clear()
        current = null
    }
    
    /** 跳过当前动画 */
    fun skipCurrent() {
        current?.let {
            it.isFinished = true
            it.onComplete?.invoke()
        }
        current = null
    }
}
