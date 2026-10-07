package com.majiang.presentation.common.render

import com.majiang.GdxHeadlessTestRunner
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * 宝牌与赤宝牌闪光特效纯逻辑与渲染指令测试
 */
class DoraSparkleTest : GdxHeadlessTestRunner() {

    private fun tile(suit: SuitView, rank: Int, copyIndex: Int = 0): TileView {
        val base = when (suit) {
            SuitView.MAN -> (rank - 1) * 4
            SuitView.SOU -> 36 + (rank - 1) * 4
            SuitView.PIN -> 72 + (rank - 1) * 4
            SuitView.WIND -> 108 + (rank - 1) * 4
            SuitView.DRAGON -> 124 + (rank - 1) * 4
        }
        return TileView(base + copyIndex, suit, rank)
    }

    @Test
    fun `computeVisibleTargets 覆盖数牌指示牌与端点循环`() {
        // 3万(rank 3, typeIndex 2) -> 指示 4万(rank 4, typeIndex 3)
        val targets3m = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.MAN, 3)))
        assertEquals(setOf(tile(SuitView.MAN, 4).typeIndex), targets3m)

        // 9万(rank 9, typeIndex 8) -> 循环指示 1万(rank 1, typeIndex 0)
        val targets9m = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.MAN, 9)))
        assertEquals(setOf(tile(SuitView.MAN, 1).typeIndex), targets9m)

        // 9筒(rank 9, typeIndex 26) -> 循环指示 1筒(rank 1, typeIndex 18)
        val targets9p = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.PIN, 9)))
        assertEquals(setOf(tile(SuitView.PIN, 1).typeIndex), targets9p)

        // 9索(rank 9, typeIndex 17) -> 循环指示 1索(rank 1, typeIndex 9)
        val targets9s = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.SOU, 9)))
        assertEquals(setOf(tile(SuitView.SOU, 1).typeIndex), targets9s)

        // 空列表返回空集合
        assertTrue(DoraSparkle.computeVisibleTargets(emptyList()).isEmpty())

        // 多个指示牌去重合并
        val multi = DoraSparkle.computeVisibleTargets(listOf(
            tile(SuitView.MAN, 3),
            tile(SuitView.MAN, 3, 1),
            tile(SuitView.PIN, 9)
        ))
        assertEquals(setOf(tile(SuitView.MAN, 4).typeIndex, tile(SuitView.PIN, 1).typeIndex), multi)
    }

    @Test
    fun `computeVisibleTargets 覆盖风牌东南西北与三元白发中循环`() {
        // 风牌循环：東(1) -> 南(2), 南(2) -> 西(3), 西(3) -> 北(4), 北(4) -> 東(1)
        val eastTarget = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.WIND, 1)))
        assertEquals(setOf(tile(SuitView.WIND, 2).typeIndex), eastTarget)

        val southTarget = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.WIND, 2)))
        assertEquals(setOf(tile(SuitView.WIND, 3).typeIndex), southTarget)

        val westTarget = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.WIND, 3)))
        assertEquals(setOf(tile(SuitView.WIND, 4).typeIndex), westTarget)

        val northTarget = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.WIND, 4)))
        assertEquals(setOf(tile(SuitView.WIND, 1).typeIndex), northTarget)

        // 三元牌循环：白(1) -> 發(2), 發(2) -> 中(3), 中(3) -> 白(1)
        val whiteTarget = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.DRAGON, 1)))
        assertEquals(setOf(tile(SuitView.DRAGON, 2).typeIndex), whiteTarget)

        val greenTarget = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.DRAGON, 2)))
        assertEquals(setOf(tile(SuitView.DRAGON, 3).typeIndex), greenTarget)

        val redDragonTarget = DoraSparkle.computeVisibleTargets(listOf(tile(SuitView.DRAGON, 3)))
        assertEquals(setOf(tile(SuitView.DRAGON, 1).typeIndex), redDragonTarget)
    }

    @Test
    fun `sparkleAlpha 纯函数值域严格在 0点15 到 1点0 之间`() {
        var t = 0f
        while (t <= 10.0f) {
            val alpha = DoraSparkle.sparkleAlpha(t)
            assertTrue(alpha in 0.15f..1.0f, "在 t=$t 时，alpha 应在 [0.15, 1.0]，实际为 $alpha")
            t += 0.02f
        }
    }

    @Test
    fun `sparkleAlpha 周期为 1点2 秒且具有完全确定性`() {
        val testTimes = listOf(0.0f, 0.15f, 0.3f, 0.45f, 0.6f, 0.75f, 0.9f, 1.05f)

        for (t in testTimes) {
            val a1 = DoraSparkle.sparkleAlpha(t)
            val a2 = DoraSparkle.sparkleAlpha(t)
            assertEquals(a1, a2, "同时间参数 t=$t 调用必须产生完全相同确定性结果")

            val aNextPeriod = DoraSparkle.sparkleAlpha(t + 1.2f)
            assertEquals(a1, aNextPeriod, 1e-4f, "t=$t 与 t+1.2s 周期对应值应在浮点误差内精确一致")

            val aMultiPeriod = DoraSparkle.sparkleAlpha(t + 3.6f)
            assertEquals(a1, aMultiPeriod, 1e-4f, "3 个周期后(t+3.6s)应保持周期性")
        }

        // 特殊相位精确点验证
        assertEquals(1.0f, DoraSparkle.sparkleAlpha(0.3f), 1e-4f)
        assertEquals(0.15f, DoraSparkle.sparkleAlpha(0.9f), 1e-4f)
        assertEquals(0.575f, DoraSparkle.sparkleAlpha(0.0f), 1e-4f)
        assertEquals(0.575f, DoraSparkle.sparkleAlpha(1.2f), 1e-4f)
    }

    @Test
    fun `isSparkleTarget 赤宝牌始终持续闪光且与指示牌命中一致`() {
        val emptyTargets = emptySet<Int>()

        // 3 张赤宝牌即便在没有任何指示牌时亦为真
        assertTrue(DoraSparkle.isSparkleTarget(tile(SuitView.MAN, 5, 3), emptyTargets))
        assertTrue(DoraSparkle.isSparkleTarget(tile(SuitView.PIN, 5, 3), emptyTargets))
        assertTrue(DoraSparkle.isSparkleTarget(tile(SuitView.SOU, 5, 3), emptyTargets))

        // 普通 5 号牌在无指示牌时永假
        assertFalse(DoraSparkle.isSparkleTarget(tile(SuitView.MAN, 5, 0), emptyTargets))
        assertFalse(DoraSparkle.isSparkleTarget(tile(SuitView.PIN, 5, 1), emptyTargets))
        assertFalse(DoraSparkle.isSparkleTarget(tile(SuitView.SOU, 5, 2), emptyTargets))

        // 当指示牌指示 5万 (typeIndex=4) 时，普通 5万 亦闪光
        val targetsWith5m = setOf(tile(SuitView.MAN, 5).typeIndex)
        assertTrue(DoraSparkle.isSparkleTarget(tile(SuitView.MAN, 5, 0), targetsWith5m))
        assertTrue(DoraSparkle.isSparkleTarget(tile(SuitView.MAN, 5, 3), targetsWith5m)) // 红五亦真
        assertFalse(DoraSparkle.isSparkleTarget(tile(SuitView.MAN, 4, 0), targetsWith5m))
    }

    @Test
    fun `appendSparkle 无交互时生成整牌辉光与四层光环与十字闪光星`() {
        val buffer = TileDrawBuffer()
        val x = 100f
        val y = 50f
        val width = 54f
        val height = 80f
        val alpha = 0.8f

        DoraSparkle.appendSparkle(
            buf = buffer,
            x = x,
            y = y,
            width = width,
            height = height,
            alpha = alpha,
            hasInteractionHighlight = false
        )

        // 四层嵌套光环（LineRectOp × 4，由内到外透明度递减）
        assertEquals(4, buffer.lineRects.size, "应生成 4 层嵌套光环描边")
        val innerRing = buffer.lineRects[0]
        assertEquals(x, innerRing.x, 1e-4f)
        assertEquals(y, innerRing.y, 1e-4f)
        assertEquals(width, innerRing.width, 1e-4f)
        assertEquals(height, innerRing.height, 1e-4f)
        assertEquals(alpha * 0.95f, innerRing.color.a, 1e-4f)

        // 最外层环外扩 4.5px
        val outerRing = buffer.lineRects[3]
        assertEquals(x - 4.5f, outerRing.x, 1e-4f)
        assertEquals(alpha * 0.25f, outerRing.color.a, 1e-4f)

        // 覆盖层：整牌辉光 ×1 + 十字星（双臂 + 白核）×3
        assertEquals(4, buffer.overlayRects.size, "应为 1 个整牌辉光 + 3 个十字星图元")
        assertEquals(0, buffer.filledRects.size, "不再使用普通填充桶")
        assertEquals(0, buffer.textureDraws.size)
        assertEquals(0, buffer.textDraws.size)

        // 整牌辉光覆盖牌矩形，透明度 = FACE_GLOW_MAX_ALPHA * alpha
        val faceGlow = buffer.overlayRects[0]
        assertEquals(x, faceGlow.x, 1e-4f)
        assertEquals(width, faceGlow.width, 1e-4f)
        assertEquals(DoraSparkle.FACE_GLOW_MAX_ALPHA * alpha, faceGlow.color.a, 1e-4f)

        // 十字星中心在右上角内侧，光臂金色、星核近白
        val cx = x + width - 3f
        val cy = y + height - 3f
        val horizontalArm = buffer.overlayRects[1]
        assertEquals(cx - 11f, horizontalArm.x, 1e-4f)
        assertEquals(22f, horizontalArm.width, 1e-4f)
        assertEquals(6f, horizontalArm.height, 1e-4f)
        assertEquals(0.9f, horizontalArm.color.a, 1e-4f, "光臂 alpha = 0.5 + 0.5*alpha")
        val core = buffer.overlayRects[3]
        assertEquals(cx - 4f, core.x, 1e-4f)
        assertEquals(8f, core.width, 1e-4f)
        assertEquals(8f, core.height, 1e-4f)
    }

    @Test
    fun `appendSparkle 有交互高亮时抑制光环但保留辉光与闪光星`() {
        val buffer = TileDrawBuffer()
        val x = 200f
        val y = 60f
        val width = 54f
        val height = 80f
        val alpha = 0.75f

        DoraSparkle.appendSparkle(
            buf = buffer,
            x = x,
            y = y,
            width = width,
            height = height,
            alpha = alpha,
            hasInteractionHighlight = true
        )

        // 交互高亮优先：四层光环被抑制
        assertEquals(0, buffer.lineRects.size, "交互高亮时必须抑制光环描边，避免视觉打架")

        // 整牌辉光与十字闪光星依然保留（覆盖层 4 个图元）
        assertEquals(4, buffer.overlayRects.size, "整牌辉光与闪光星必须保留")
        assertEquals(0, buffer.filledRects.size)
    }
}
