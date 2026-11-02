package com.majiang.presentation.common.render

import com.badlogic.gdx.math.Matrix4
import com.badlogic.gdx.math.Vector2
import com.badlogic.gdx.math.Vector3
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

/**
 * 牌河提示打开时的局部缩放：绘制矩阵与输入逆变换必须严格互逆。
 *
 * 这里不复制常量自等，而是先用与渲染相同的 camera.combined 组合矩阵，
 * 再用矩阵把已知牌桌点投到 stage，最后走正式输入逆变换回到原点。
 */
class TableLocalZoomTest {

    @Test
    fun `projected table points invert through the input path`() {
        val stageCamera = Matrix4().setToOrtho2D(0f, 0f, 1280f, 720f)
        val localCamera = TableLocalZoom.combine(stageCamera, Matrix4())

        for (point in listOf(0f to 0f, 640f to 360f, 448f to 470f, 1280f to 720f)) {
            val projected = Vector3(point.first, point.second, 0f).mul(localCamera)
            // camera.combined 输出 NDC；输入反变换使用 stage 设计像素，先还原同一空间。
            val stageX = (projected.x + 1f) * 640f
            val stageY = (projected.y + 1f) * 360f
            val back = TableLocalZoom.toTable(stageX, stageY, Vector2())
            assertEquals(point.first, back.x, 0.02f, "x at ${point.first},${point.second}")
            assertEquals(point.second, back.y, 0.02f, "y at ${point.first},${point.second}")
        }
    }

    @Test
    fun `table region leaves the right side for the river panel`() {
        assertEquals(0.65625f, TableLocalZoom.SCALE, 0.00001f)
        assertEquals(10f, TableLocalZoom.OFFSET_X, 0.0001f)
        assertEquals(123.75f, TableLocalZoom.offsetY, 0.0001f)
        val right = TableLocalZoom.toStage(1280f, 360f)
        assertEquals(850f, right.x, 0.01f)
        assertEquals(360f, right.y, 0.01f)
    }
}
