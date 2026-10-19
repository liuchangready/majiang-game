package com.majiang.presentation.game

import com.majiang.client.api.GameEventView
import com.majiang.client.api.TileView
import com.majiang.presentation.common.render.DiscardAnimation
import com.majiang.presentation.common.render.DiscardPhase
import com.majiang.presentation.common.render.DiscardPose
import java.util.ArrayDeque

/**
 * 待播放或正在播放的物理牌弃牌动画数据类型别名（保持 GameScreen.QueuedDiscard 契约兼容）。
 */
typealias QueuedDiscard = GameScreen.QueuedDiscard

/**
 * 弃牌飞行动画状态控制器 (DiscardAnimationController)。
 *
 * 核心职责：
 * 1. 独立维护弃牌队列 (discardQueue)、当前飞行动画 (currentDiscardAnim)、已播放时间 (discardAnimElapsed)
 *    以及上一快照基准牌河 (lastDiscards)；
 * 2. 差异检测与入队 (checkDiscardsDiff)：检测副露抢牌（若飞行动画或队列中的牌已从牌河中被移走，及时清空/移除），
 *    依照事件流 (TileDiscarded) 与新增物理牌时间序入队动画；
 * 3. 逐帧推进 (update)：根据单调 delta 推进飞行动画时长，到达 TOTAL_DURATION 后自动出队下一张；
 * 4. 显式快进 (fastForward)：在小局切换、断线恢复、代次跳变或结算展示时瞬时清空队列并同步基准；
 * 5. 牌河可见性与抑制判定 (isTileSuppressed, isTileQueued, isTileVisibleInRiver)；
 * 6. 姿态计算 (currentPose)：计算当前飞行动画的插值姿态。
 */
class DiscardAnimationController(
    val startPositionProvider: (seat: Int) -> Pair<Float, Float>,
    val slotPositionProvider: (seat: Int, slotIndex: Int) -> Pair<Float, Float>,
    initialDiscards: List<List<TileView>> = emptyList(),
    val onPlaybackBusyChanged: ((Boolean) -> Unit)? = null
) {
    val discardQueue = ArrayDeque<QueuedDiscard>()
    var currentDiscardAnim: QueuedDiscard? = null
    var discardAnimElapsed: Float = 0f
    var lastDiscards: List<List<TileView>> = (0..3).map { initialDiscards.getOrNull(it) ?: emptyList() }

    val isDiscardPlaybackBusy: Boolean
        get() = currentDiscardAnim != null || discardQueue.isNotEmpty()

    /**
     * 对照权威牌河与事件流，计算出新增弃牌动画并排入队列。
     *
     * @param currentDiscards 当前权威快照中各座位的牌河
     * @param events 当前批次到达的可见事件
     * @return 当前是否正处于动画播放繁忙状态 (isDiscardPlaybackBusy)
     */
    fun checkDiscardsDiff(
        currentDiscards: List<List<TileView>>,
        events: List<GameEventView> = emptyList()
    ): Boolean {
        // 1. 副露抢牌检测：若当前正在飞行的牌从牌河中消失，说明已被副露移走，快进置空
        val activeAnim = currentDiscardAnim
        if (activeAnim != null) {
            val river = currentDiscards.getOrNull(activeAnim.seat) ?: emptyList()
            if (!river.contains(activeAnim.tile)) {
                currentDiscardAnim = null
                discardAnimElapsed = 0f
            }
        }

        // 队列中若有已被移走的牌同样清除
        if (discardQueue.isNotEmpty()) {
            val iter = discardQueue.iterator()
            while (iter.hasNext()) {
                val queued = iter.next()
                val river = currentDiscards.getOrNull(queued.seat) ?: emptyList()
                if (!river.contains(queued.tile)) {
                    iter.remove()
                }
            }
        }

        // Match newly visible physical tiles against the event stream so a batched
        // update plays discards in chronological order, even across seat changes.
        val pending = linkedMapOf<Pair<Int, TileView>, Int>()
        for (seat in 0..3) {
            val oldList = lastDiscards.getOrNull(seat) ?: emptyList()
            val newList = currentDiscards.getOrNull(seat) ?: emptyList()
            for ((index, tile) in newList.withIndex()) {
                if (tile !in oldList) pending[seat to tile] = index
            }
        }
        fun enqueue(seat: Int, tile: TileView, index: Int) {
            discardQueue.add(
                QueuedDiscard(
                    seat = seat,
                    tile = tile,
                    targetIndex = index,
                    start = startPositionProvider(seat),
                    end = slotPositionProvider(seat, index)
                )
            )
        }
        for (event in events) {
            if (event is GameEventView.TileDiscarded) {
                val index = pending.remove(event.player to event.tile) ?: continue
                enqueue(event.player, event.tile, index)
            }
        }
        for ((key, index) in pending) enqueue(key.first, key.second, index)

        val busy = isDiscardPlaybackBusy
        if (busy) {
            onPlaybackBusyChanged?.invoke(true)
        }

        lastDiscards = (0..3).map { currentDiscards.getOrNull(it) ?: emptyList() }
        return busy
    }

    /**
     * 逐帧推移动画状态。
     */
    fun update(delta: Float) {
        if (currentDiscardAnim == null && discardQueue.isNotEmpty()) {
            currentDiscardAnim = discardQueue.poll()
            discardAnimElapsed = 0f
        }

        val anim = currentDiscardAnim
        if (anim != null) {
            discardAnimElapsed += delta
            if (discardAnimElapsed >= DiscardAnimation.TOTAL_DURATION) {
                currentDiscardAnim = null
                discardAnimElapsed = 0f
                if (discardQueue.isNotEmpty()) {
                    currentDiscardAnim = discardQueue.poll()
                }
            }
        }
    }

    /**
     * 快进/重置所有弃牌动画（清除队列与飞行动画），并将 lastDiscards 对齐当前快照牌河。
     */
    fun fastForward(currentDiscards: List<List<TileView>>? = null) {
        currentDiscardAnim = null
        discardQueue.clear()
        discardAnimElapsed = 0f
        if (currentDiscards != null) {
            lastDiscards = (0..3).map { currentDiscards.getOrNull(it) ?: emptyList() }
        }
    }

    /**
     * 初始化/重置基准牌河，不产生飞行动画。
     */
    fun resetBaseline(discards: List<List<TileView>>) {
        currentDiscardAnim = null
        discardQueue.clear()
        discardAnimElapsed = 0f
        lastDiscards = (0..3).map { discards.getOrNull(it) ?: emptyList() }
    }

    /**
     * 判定指定牌河中的牌是否正处于飞行动画中被抑制绘制。
     */
    fun isTileSuppressed(seat: Int, tile: TileView, index: Int): Boolean {
        val anim = currentDiscardAnim ?: return false
        val pose = DiscardAnimation.poseAt(discardAnimElapsed, anim.start, anim.end, anim.seat)
        return DiscardAnimation.shouldSuppressDiscard(
            pondSeat = seat,
            tile = tile,
            tileIndex = index,
            animatingSeat = anim.seat,
            animatingTile = anim.tile,
            animatingIndex = anim.targetIndex,
            phase = pose.phase
        )
    }

    /**
     * 判定指定牌河中的牌是否在等待队列中。
     */
    fun isTileQueued(seat: Int, tile: TileView, index: Int): Boolean {
        return discardQueue.any { q ->
            DiscardAnimation.isQueuedRiverTile(seat, tile, index, q.seat, q.tile, q.targetIndex)
        }
    }

    /**
     * 牌河中指定索引的牌当前是否应正常渲染（未被抑制且未排队）。
     */
    fun isTileVisibleInRiver(seat: Int, tile: TileView, index: Int): Boolean {
        return !isTileSuppressed(seat, tile, index) && !isTileQueued(seat, tile, index)
    }

    /**
     * 计算当前正在飞行的牌的动画姿态 (DiscardPose)。
     */
    fun currentPose(): DiscardPose? {
        val anim = currentDiscardAnim ?: return null
        return DiscardAnimation.poseAt(discardAnimElapsed, anim.start, anim.end, anim.seat)
    }
}
