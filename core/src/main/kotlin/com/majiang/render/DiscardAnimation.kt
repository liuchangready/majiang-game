package com.majiang.render

import com.majiang.model.Tile

/**
 * 打牌动画分镜阶段枚举。
 *
 * ① REACH: 幽灵手滑入 (0.12s)
 * ② GRAB: 捏住提牌 (0.08s)
 * ③ CARRY: 飞向牌河 (0.35s)
 * ④ PLACE: 落格回弹 (0.12s)
 * ⑤ RETRACT: 手套缩回 (0.15s)
 * DONE: 动画结束 (总计 0.82s)
 */
enum class DiscardPhase {
    REACH,
    GRAB,
    CARRY,
    PLACE,
    RETRACT,
    DONE
}

/**
 * 打牌动画插值姿态数据。
 */
data class DiscardPose(
    val x: Float,
    val y: Float,
    val scaleX: Float,
    val scaleY: Float,
    val tiltDeg: Float,          // 纹理皮肤旋转用；经典皮肤恒 0
    val handAlpha: Float,
    val phase: DiscardPhase
)

/**
 * 打牌动画纯函数与时间轴定义（无头环境可测）。
 * 遵循 doc/discard-animation/DESIGN.md 契约。
 */
object DiscardAnimation {
    const val TOTAL_DURATION = 0.82f
    const val REACH = 0.12f
    const val GRAB = 0.08f
    const val CARRY = 0.35f
    const val PLACE = 0.12f
    const val RETRACT = 0.15f

    /**
     * 二次贝塞尔曲线纯函数计算。
     *
     * B(t) = (1-t)^2 * P0 + 2(1-t)t * P1 + t^2 * P2
     */
    fun bezier(
        t: Float,
        sx: Float, sy: Float,
        cx: Float, cy: Float,
        ex: Float, ey: Float
    ): Pair<Float, Float> {
        val clampedT = t.coerceIn(0f, 1f)
        val u = 1f - clampedT
        val tt = clampedT * clampedT
        val uu = u * u
        val ut2 = 2f * u * clampedT
        val x = uu * sx + ut2 * cx + tt * ex
        val y = uu * sy + ut2 * cy + tt * ey
        return Pair(x, y)
    }

    /**
     * 三次方缓动插值函数，单调映射 [0, 1] -> [0, 1]。
     */
    fun easeInOutCubic(t: Float): Float {
        val clamped = t.coerceIn(0f, 1f)
        return if (clamped < 0.5f) {
            4f * clamped * clamped * clamped
        } else {
            1f - (-2f * clamped + 2f) * (-2f * clamped + 2f) * (-2f * clamped + 2f) / 2f
        }
    }

    /**
     * 弧线控制点计算：
     * 按「起点-终点距离的 35% 向座位外侧偏移 + 上抛 40~90px」计算，飞行距离越远弧越高。
     * 四座位偏移方向均远离桌面中心 (640, 360)：
     * - Seat 0 (底端): 向 -Y 偏移
     * - Seat 1 (右侧): 向 +X 偏移
     * - Seat 2 (顶端): 向 +Y 偏移
     * - Seat 3 (左侧): 向 -X 偏移
     */
    fun controlPoint(
        start: Pair<Float, Float>,
        end: Pair<Float, Float>,
        seat: Int
    ): Pair<Float, Float> {
        val sx = start.first
        val sy = start.second
        val ex = end.first
        val ey = end.second
        val midX = (sx + ex) / 2f
        val midY = (sy + ey) / 2f

        val dx = ex - sx
        val dy = ey - sy
        val dist = Math.hypot(dx.toDouble(), dy.toDouble()).toFloat()

        // 上抛增量 40~90px，飞行距离越远弧越高
        val toss = (40f + (dist / 500f) * 50f).coerceIn(40f, 90f)
        val totalOffset = dist * 0.35f + toss

        return when (seat % 4) {
            0 -> Pair(midX, midY - totalOffset)
            1 -> Pair(midX + totalOffset, midY)
            2 -> Pair(midX, midY + totalOffset)
            3 -> Pair(midX - totalOffset, midY)
            else -> Pair(midX, midY - totalOffset)
        }
    }

    /**
     * 时间轴姿态采样纯函数：
     * 输入经过时间 elapsed 与起始坐标、落点坐标及座位，输出当前分镜阶段对应的 DiscardPose。
     */
    fun poseAt(
        elapsed: Float,
        start: Pair<Float, Float>,
        end: Pair<Float, Float>,
        seat: Int
    ): DiscardPose {
        if (elapsed >= TOTAL_DURATION) {
            return DiscardPose(
                x = end.first,
                y = end.second,
                scaleX = 1.0f,
                scaleY = 1.0f,
                tiltDeg = 0f,
                handAlpha = 0f,
                phase = DiscardPhase.DONE
            )
        }

        val tGrab = REACH
        val tCarry = tGrab + GRAB
        val tPlace = tCarry + CARRY
        val tRetract = tPlace + PLACE

        val targetTilt = if (seat % 2 == 0) 8f else -8f

        return when {
            elapsed < tGrab -> {
                // ① REACH (0.12s): 幽灵手滑入，伸向手牌区，alpha 0→0.75 淡入
                val t = (elapsed / REACH).coerceIn(0f, 1f)
                DiscardPose(
                    x = start.first,
                    y = start.second,
                    scaleX = 1.0f,
                    scaleY = 1.0f,
                    tiltDeg = 0f,
                    handAlpha = 0.75f * t,
                    phase = DiscardPhase.REACH
                )
            }
            elapsed < tCarry -> {
                // ② GRAB (0.08s): 捏住牌，牌 scale 1.0→1.1、上浮 6px、倾斜 ±8°
                val t = ((elapsed - tGrab) / GRAB).coerceIn(0f, 1f)
                val scale = 1.0f + 0.1f * t
                val lift = 6f * t
                DiscardPose(
                    x = start.first,
                    y = start.second + lift,
                    scaleX = scale,
                    scaleY = scale,
                    tiltDeg = targetTilt * t,
                    handAlpha = 0.75f,
                    phase = DiscardPhase.GRAB
                )
            }
            elapsed < tPlace -> {
                // ③ CARRY (0.35s): 贝塞尔弧线飞向牌河，ease-in-out，保持提起姿态
                val rawT = ((elapsed - tCarry) / CARRY).coerceIn(0f, 1f)
                val t = easeInOutCubic(rawT)
                val ctrl = controlPoint(start, end, seat)
                val (bx, by) = bezier(t, start.first, start.second + 6f, ctrl.first, ctrl.second, end.first, end.second)
                DiscardPose(
                    x = bx,
                    y = by,
                    scaleX = 1.1f,
                    scaleY = 1.1f,
                    tiltDeg = targetTilt,
                    handAlpha = 0.75f,
                    phase = DiscardPhase.CARRY
                )
            }
            elapsed < tRetract -> {
                // ④ PLACE (0.12s): 落格 squash (scaleY 0.92→1.0 回弹)，手松开
                val t = ((elapsed - tPlace) / PLACE).coerceIn(0f, 1f)
                val sY = 0.92f + 0.08f * t
                val tilt = targetTilt * (1f - t)
                DiscardPose(
                    x = end.first,
                    y = end.second,
                    scaleX = 1.0f,
                    scaleY = sY,
                    tiltDeg = tilt,
                    handAlpha = 0.75f,
                    phase = DiscardPhase.PLACE
                )
            }
            else -> {
                // ⑤ RETRACT (0.15s): 手缩回屏幕边缘，alpha 0.75→0 淡出
                val t = ((elapsed - tRetract) / RETRACT).coerceIn(0f, 1f)
                DiscardPose(
                    x = end.first,
                    y = end.second,
                    scaleX = 1.0f,
                    scaleY = 1.0f,
                    tiltDeg = 0f,
                    handAlpha = 0.75f * (1f - t),
                    phase = DiscardPhase.RETRACT
                )
            }
        }
    }

    /**
     * 视图调和纯函数：
     * 判断指定牌河槽位中的牌在当前帧是否应当被扣下隐藏（针对**正在播放**的动画条目）。
     *
     * 判定准则（必须全部满足）：
     * 1. 动画当前未完成 (phase != DONE)；
     * 2. 牌河所属座位与打牌者座位一致 (pondSeat == animatingSeat)；
     * 3. 槽位序号与该次打牌的牌河目标序号一致 (tileIndex == animatingIndex)；
     * 4. 该牌物理身份与打牌者打出的牌一致 (tile == animatingTile)。
     */
    fun shouldSuppressDiscard(
        pondSeat: Int,
        tile: Tile,
        tileIndex: Int,
        animatingSeat: Int,
        animatingTile: Tile,
        animatingIndex: Int,
        phase: DiscardPhase
    ): Boolean {
        return phase != DiscardPhase.DONE &&
            pondSeat == animatingSeat &&
            tileIndex == animatingIndex &&
            tile == animatingTile
    }

    /**
     * 判断指定牌河槽位中的牌是否被「**排队中**（尚未开始播放）」的打牌条目占用。
     *
     * 逻辑层瞬时推进时（如玩家打牌的同一帧内三家 AI 已各自摸打完毕），
     * 后续打牌会先进入单槽队列等待播放——这些牌在轮到自己播放前
     * 同样不得出现在牌河中，否则会出现"先出现、再消失重飞"的穿帮。
     */
    fun isQueuedRiverTile(
        pondSeat: Int,
        tile: Tile,
        tileIndex: Int,
        queuedSeat: Int,
        queuedTile: Tile,
        queuedIndex: Int
    ): Boolean =
        pondSeat == queuedSeat && tileIndex == queuedIndex && tile == queuedTile
}
