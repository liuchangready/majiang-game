package com.majiang.render

import com.majiang.presentation.common.render.*

import com.majiang.testing.TestViewBuilder

import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import kotlin.math.abs

/**
 * 打牌动画 (DiscardAnimation) 纯逻辑与算法单元测试。
 * 严格覆盖 doc/discard-animation/DESIGN.md 中的「测试要求」全部 5 组纯逻辑契约：
 * 1. bezier: 端点精确、中点性质、单调性；
 * 2. easeInOutCubic: 值域 [0, 1]、单调性、端点精确；
 * 3. poseAt: 阶段切分时长精确、elapsed >= TOTAL 判 DONE、tilt/scale 值域、handAlpha 渐变方向；
 * 4. controlPoint: 四座位偏移方向均远离桌面中心、随距离增高弧度；
 * 5. shouldSuppressDiscard: 视图调和纯函数解调与扣牌判定。
 */
class DiscardAnimationTest {

    // ==========================================
    // 1. bezier 测试
    // ==========================================
    @Test
    fun `bezier 端点精确、中点性质与单调性`() {
        val sx = 100f
        val sy = 200f
        val cx = 300f
        val cy = 400f
        val ex = 500f
        val ey = 600f

        // t=0 端点精确
        val p0 = DiscardAnimation.bezier(0f, sx, sy, cx, cy, ex, ey)
        assertEquals(sx, p0.first, 1e-4f, "t=0 时 X 坐标必须严格精确等于起点 sx")
        assertEquals(sy, p0.second, 1e-4f, "t=0 时 Y 坐标必须严格精确等于起点 sy")

        // t=1 端点精确
        val p1 = DiscardAnimation.bezier(1f, sx, sy, cx, cy, ex, ey)
        assertEquals(ex, p1.first, 1e-4f, "t=1 时 X 坐标必须严格精确等于落点 ex")
        assertEquals(ey, p1.second, 1e-4f, "t=1 时 Y 坐标必须严格精确等于落点 ey")

        // t=0.5 落在控制点与起终中点的中点上
        // B(0.5) = 0.25*P0 + 0.5*P1 + 0.25*P2 = 0.5*P1 + 0.5*((P0+P2)/2)
        val pMid = DiscardAnimation.bezier(0.5f, sx, sy, cx, cy, ex, ey)
        val expectedMidX = 0.5f * cx + 0.5f * ((sx + ex) / 2f)
        val expectedMidY = 0.5f * cy + 0.5f * ((sy + ey) / 2f)
        assertEquals(expectedMidX, pMid.first, 1e-4f, "t=0.5 时 X 坐标应精确落在控制点与中点之中点")
        assertEquals(expectedMidY, pMid.second, 1e-4f, "t=0.5 时 Y 坐标应精确落在控制点与中点之中点")

        // 对单调控制点单调测试
        var prevX = sx
        var prevY = sy
        for (step in 1..100) {
            val t = step / 100f
            val pt = DiscardAnimation.bezier(t, sx, sy, cx, cy, ex, ey)
            assertTrue(pt.first >= prevX - 1e-4f, "在 sx<=cx<=ex 条件下 X 必须对 t 单调递增")
            assertTrue(pt.second >= prevY - 1e-4f, "在 sy<=cy<=ey 条件下 Y 必须对 t 单调递增")
            prevX = pt.first
            prevY = pt.second
        }
    }

    // ==========================================
    // 2. easeInOutCubic 测试
    // ==========================================
    @Test
    fun `easeInOutCubic 值域单调与端点精确`() {
        // 端点
        assertEquals(0f, DiscardAnimation.easeInOutCubic(0f), 1e-5f)
        assertEquals(1f, DiscardAnimation.easeInOutCubic(1f), 1e-5f)
        assertEquals(0.5f, DiscardAnimation.easeInOutCubic(0.5f), 1e-5f)

        // 界外保护
        assertEquals(0f, DiscardAnimation.easeInOutCubic(-0.1f), 1e-5f)
        assertEquals(1f, DiscardAnimation.easeInOutCubic(1.5f), 1e-5f)

        // 值域与单调性
        var prevVal = 0f
        for (step in 1..200) {
            val t = step / 200f
            val value = DiscardAnimation.easeInOutCubic(t)
            assertTrue(value in 0f..1f, "输出值域必须在 [0, 1] 内，当前为 $value")
            assertTrue(value >= prevVal - 1e-5f, "函数必须对输入单调不减，当前 $value < 上一个 $prevVal")
            prevVal = value
        }
    }

    // ==========================================
    // 3. poseAt 测试
    // ==========================================
    @Test
    fun `poseAt 阶段切分时长精确与 elapsed 超出 TOTAL 判 DONE`() {
        val start = Pair(626f, 80f)
        val end = Pair(460f, 280f)
        val seat = 0

        // 阶段切分点验证：
        // REACH: [0, 0.12)
        assertEquals(DiscardPhase.REACH, DiscardAnimation.poseAt(0.0f, start, end, seat).phase)
        assertEquals(DiscardPhase.REACH, DiscardAnimation.poseAt(0.06f, start, end, seat).phase)
        assertEquals(DiscardPhase.REACH, DiscardAnimation.poseAt(0.119f, start, end, seat).phase)

        // GRAB: [0.12, 0.20)
        assertEquals(DiscardPhase.GRAB, DiscardAnimation.poseAt(0.12f, start, end, seat).phase)
        assertEquals(DiscardPhase.GRAB, DiscardAnimation.poseAt(0.16f, start, end, seat).phase)
        assertEquals(DiscardPhase.GRAB, DiscardAnimation.poseAt(0.199f, start, end, seat).phase)

        // CARRY: [0.20, 0.55)
        assertEquals(DiscardPhase.CARRY, DiscardAnimation.poseAt(0.20f, start, end, seat).phase)
        assertEquals(DiscardPhase.CARRY, DiscardAnimation.poseAt(0.35f, start, end, seat).phase)
        assertEquals(DiscardPhase.CARRY, DiscardAnimation.poseAt(0.549f, start, end, seat).phase)

        // PLACE: [0.55, 0.67)
        assertEquals(DiscardPhase.PLACE, DiscardAnimation.poseAt(0.55f, start, end, seat).phase)
        assertEquals(DiscardPhase.PLACE, DiscardAnimation.poseAt(0.60f, start, end, seat).phase)
        assertEquals(DiscardPhase.PLACE, DiscardAnimation.poseAt(0.669f, start, end, seat).phase)

        // RETRACT: [0.67, 0.82)
        assertEquals(DiscardPhase.RETRACT, DiscardAnimation.poseAt(0.67f, start, end, seat).phase)
        assertEquals(DiscardPhase.RETRACT, DiscardAnimation.poseAt(0.75f, start, end, seat).phase)
        assertEquals(DiscardPhase.RETRACT, DiscardAnimation.poseAt(0.819f, start, end, seat).phase)

        // DONE: elapsed >= 0.82f
        assertEquals(DiscardPhase.DONE, DiscardAnimation.poseAt(0.82f, start, end, seat).phase)
        assertEquals(DiscardPhase.DONE, DiscardAnimation.poseAt(0.90f, start, end, seat).phase)
        assertEquals(DiscardPhase.DONE, DiscardAnimation.poseAt(1.50f, start, end, seat).phase)
    }

    @Test
    fun `poseAt 各阶段 tilt 与 scale 值域符合契约断言`() {
        val start = Pair(626f, 80f)
        val end = Pair(460f, 280f)

        for (seat in 0..3) {
            for (step in 0..82) {
                val elapsed = step * 0.01f
                val pose = DiscardAnimation.poseAt(elapsed, start, end, seat)

                // 倾斜值域：|tilt| <= 8°
                assertTrue(abs(pose.tiltDeg) <= 8.001f, "tiltDeg 绝对值必须 <= 8°，当前为 ${pose.tiltDeg} (elapsed=$elapsed)")

                // 缩放值域：scaleX, scaleY in [0.90, 1.15]
                assertTrue(pose.scaleX in 0.90f..1.15f, "scaleX 必须在 [0.9, 1.15] 之间，当前为 ${pose.scaleX} (elapsed=$elapsed)")
                assertTrue(pose.scaleY in 0.90f..1.15f, "scaleY 必须在 [0.9, 1.15] 之间，当前为 ${pose.scaleY} (elapsed=$elapsed)")
            }
        }
    }

    @Test
    fun `poseAt handAlpha 渐变方向 REACH 升与 RETRACT 降`() {
        val start = Pair(626f, 80f)
        val end = Pair(460f, 280f)
        val seat = 0

        // REACH 阶段: handAlpha 从 0 升至 0.75
        val reachStart = DiscardAnimation.poseAt(0.0f, start, end, seat)
        val reachMid = DiscardAnimation.poseAt(0.06f, start, end, seat)
        val reachEnd = DiscardAnimation.poseAt(0.119f, start, end, seat)

        assertEquals(0f, reachStart.handAlpha, 1e-4f)
        assertTrue(reachMid.handAlpha > reachStart.handAlpha, "REACH 阶段 handAlpha 应递增")
        assertTrue(reachEnd.handAlpha > reachMid.handAlpha, "REACH 阶段 handAlpha 应递增")
        assertTrue(reachEnd.handAlpha <= 0.75f)

        // GRAB, CARRY, PLACE 阶段保持 0.75
        val grabPose = DiscardAnimation.poseAt(0.15f, start, end, seat)
        val carryPose = DiscardAnimation.poseAt(0.35f, start, end, seat)
        val placePose = DiscardAnimation.poseAt(0.60f, start, end, seat)
        assertEquals(0.75f, grabPose.handAlpha, 1e-4f)
        assertEquals(0.75f, carryPose.handAlpha, 1e-4f)
        assertEquals(0.75f, placePose.handAlpha, 1e-4f)

        // RETRACT 阶段: handAlpha 从 0.75 降至 0
        val retractStart = DiscardAnimation.poseAt(0.67f, start, end, seat)
        val retractMid = DiscardAnimation.poseAt(0.74f, start, end, seat)
        val retractEnd = DiscardAnimation.poseAt(0.819f, start, end, seat)
        val donePose = DiscardAnimation.poseAt(0.82f, start, end, seat)

        assertTrue(retractStart.handAlpha >= 0.70f)
        assertTrue(retractMid.handAlpha < retractStart.handAlpha, "RETRACT 阶段 handAlpha 应递减")
        assertTrue(retractEnd.handAlpha < retractMid.handAlpha, "RETRACT 阶段 handAlpha 应递减")
        assertEquals(0f, donePose.handAlpha, 1e-4f, "DONE 阶段 handAlpha 必须为 0")
    }

    // ==========================================
    // 4. controlPoint 四座位偏移方向与距离高度测试
    // ==========================================
    @Test
    fun `controlPoint 四座位偏移方向均远离桌面中心`() {
        val start = Pair(600f, 300f)
        val end = Pair(680f, 420f)
        val midX = (start.first + end.first) / 2f
        val midY = (start.second + end.second) / 2f

        // Seat 0 (底端): 向 -Y 偏移 (向下远离中心)
        val ctrl0 = DiscardAnimation.controlPoint(start, end, 0)
        assertEquals(midX, ctrl0.first, 1e-4f)
        assertTrue(ctrl0.second < midY, "Seat 0 (底端) 控制点 Y 必须小于中点 midY，远离桌面中心")

        // Seat 1 (右侧): 向 +X 偏移 (向右远离中心)
        val ctrl1 = DiscardAnimation.controlPoint(start, end, 1)
        assertTrue(ctrl1.first > midX, "Seat 1 (右侧) 控制点 X 必须大于中点 midX，远离桌面中心")
        assertEquals(midY, ctrl1.second, 1e-4f)

        // Seat 2 (顶端): 向 +Y 偏移 (向上远离中心)
        val ctrl2 = DiscardAnimation.controlPoint(start, end, 2)
        assertEquals(midX, ctrl2.first, 1e-4f)
        assertTrue(ctrl2.second > midY, "Seat 2 (顶端) 控制点 Y 必须大于中点 midY，远离桌面中心")

        // Seat 3 (左侧): 向 -X 偏移 (向左远离中心)
        val ctrl3 = DiscardAnimation.controlPoint(start, end, 3)
        assertTrue(ctrl3.first < midX, "Seat 3 (左侧) 控制点 X 必须小于中点 midX，远离桌面中心")
        assertEquals(midY, ctrl3.second, 1e-4f)
    }

    @Test
    fun `controlPoint 飞行距离越远弧越高且上抛在40至90px`() {
        // 短距离
        val shortStart = Pair(100f, 100f)
        val shortEnd = Pair(130f, 140f) // dist = 50px
        val ctrlShort = DiscardAnimation.controlPoint(shortStart, shortEnd, 2)
        val shortMidY = (shortStart.second + shortEnd.second) / 2f
        val shortOffset = ctrlShort.second - shortMidY

        // 长距离
        val longStart = Pair(100f, 100f)
        val longEnd = Pair(400f, 500f) // dist = 500px
        val ctrlLong = DiscardAnimation.controlPoint(longStart, longEnd, 2)
        val longMidY = (longStart.second + longEnd.second) / 2f
        val longOffset = ctrlLong.second - longMidY

        assertTrue(longOffset > shortOffset, "飞行距离越远弧必须越高: longOffset($longOffset) > shortOffset($shortOffset)")

        // 验证上抛量在 40~90px 范围内
        val shortDist = 50f
        val shortToss = shortOffset - shortDist * 0.35f
        assertTrue(shortToss in 40f..90f, "短距离上抛分量必须在 [40, 90] px 内，当前为 $shortToss")

        val longDist = 500f
        val longToss = longOffset - longDist * 0.35f
        assertTrue(longToss in 40f..90f, "长距离上抛分量必须在 [40, 90] px 内，当前为 $longToss")
    }

    // ==========================================
    // 5. shouldSuppressDiscard 视图调和判定测试
    // ==========================================
    @Test
    fun `shouldSuppressDiscard 身份匹配与末位扣牌判定精确性`() {
        val targetTile = TestViewBuilder.tile(SuitView.MAN, 1, 0)
        val otherTile = TestViewBuilder.tile(SuitView.MAN, 2, 0)
        val seat = 0
        val totalDiscards = 6
        val lastIndex = totalDiscards - 1

        // 1. 全部匹配且在动画运行中 (非 DONE) -> 应该扣牌隐藏
        for (phase in listOf(DiscardPhase.REACH, DiscardPhase.GRAB, DiscardPhase.CARRY, DiscardPhase.PLACE, DiscardPhase.RETRACT)) {
            val suppress = DiscardAnimation.shouldSuppressDiscard(
                pondSeat = seat,
                tile = targetTile,
                tileIndex = lastIndex,
                animatingIndex = lastIndex,
                animatingSeat = seat,
                animatingTile = targetTile,
                phase = phase
            )
            assertTrue(suppress, "在 phase=$phase 阶段且身份、末位匹配时必须扣牌")
        }

        // 2. DONE 阶段 -> 恢复常驻绘制，不扣牌
        val suppressDone = DiscardAnimation.shouldSuppressDiscard(
            pondSeat = seat,
            tile = targetTile,
            tileIndex = lastIndex,
            animatingIndex = lastIndex,
            animatingSeat = seat,
            animatingTile = targetTile,
            phase = DiscardPhase.DONE
        )
        assertFalse(suppressDone, "DONE 阶段必须恢复常驻绘制，不可继续扣牌")

        // 3. 座位不匹配 -> 不扣牌
        val suppressDiffSeat = DiscardAnimation.shouldSuppressDiscard(
            pondSeat = 1,
            tile = targetTile,
            tileIndex = lastIndex,
            animatingIndex = lastIndex,
            animatingSeat = seat,
            animatingTile = targetTile,
            phase = DiscardPhase.CARRY
        )
        assertFalse(suppressDiffSeat, "非动画打牌者的牌河不可被扣牌")

        // 4. 不是末位牌 -> 不扣牌
        val suppressNotLast = DiscardAnimation.shouldSuppressDiscard(
            pondSeat = seat,
            tile = targetTile,
            tileIndex = lastIndex - 1,
            animatingIndex = lastIndex,
            animatingSeat = seat,
            animatingTile = targetTile,
            phase = DiscardPhase.CARRY
        )
        assertFalse(suppressNotLast, "非牌河末位的牌绝对不可被扣牌")

        // 5. 牌面身份不一致 (例如同花色同数但不同 copyIndex 或不同牌) -> 不扣牌
        val suppressDiffTile = DiscardAnimation.shouldSuppressDiscard(
            pondSeat = seat,
            tile = otherTile,
            tileIndex = lastIndex,
            animatingIndex = lastIndex,
            animatingSeat = seat,
            animatingTile = targetTile,
            phase = DiscardPhase.CARRY
        )
        assertFalse(suppressDiffTile, "牌对象身份不一致时不扣牌")
    }

    @Test
    fun `isQueuedRiverTile 排队中的打牌条目同样扣住其目标河槽`() {
        val tile = TestViewBuilder.tile(SuitView.PIN, 3, 2)

        // 座位、槽位、身份全部匹配 -> 排队期间扣牌（不出现"先出现再消失重飞"）
        assertTrue(
            DiscardAnimation.isQueuedRiverTile(
                pondSeat = 2, tile = tile, tileIndex = 4,
                queuedSeat = 2, queuedTile = tile, queuedIndex = 4
            )
        )

        // 任一不匹配均不扣牌
        assertFalse(DiscardAnimation.isQueuedRiverTile(1, tile, 4, 2, tile, 4), "座位不同不扣")
        assertFalse(DiscardAnimation.isQueuedRiverTile(2, tile, 3, 2, tile, 4), "槽位不同不扣")
        assertFalse(DiscardAnimation.isQueuedRiverTile(2, TestViewBuilder.tile(SuitView.PIN, 3, 1), 4, 2, tile, 4), "身份不同不扣")
    }
}
