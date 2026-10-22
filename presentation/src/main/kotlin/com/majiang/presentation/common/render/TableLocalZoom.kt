package com.majiang.presentation.common.render

import com.badlogic.gdx.math.Matrix4
import com.badlogic.gdx.math.Vector2

/**
 * 牌河提示打开时的牌桌局部缩放 (TableLocalZoom)。
 *
 * 牌桌、牌河与手牌缩小到 stage 左侧 840px，右侧留给牌河提示面板；HUD 与提示面板
 * 本身仍按未缩放的 stage 绘制。绘制矩阵和输入逆变换必须共用本对象，任何一处单独
 * 改偏移都会造成“看得到、点不到”。
 */
object TableLocalZoom {

    const val SCALE = 840f / 1280f
    const val OFFSET_X = 10f

    val offsetY: Float get() = (TableStageLayout.DESIGN_HEIGHT - TableStageLayout.DESIGN_HEIGHT * SCALE) / 2f

    /** 把 stage 相机矩阵组合成“先缩放、再平移”的牌桌局部矩阵。 */
    fun combine(stageCamera: Matrix4, out: Matrix4): Matrix4 {
        out.set(stageCamera)
            .translate(OFFSET_X, offsetY, 0f)
            .scale(SCALE, SCALE, 1f)
        return out
    }

    /** stage 输入 → 局部缩放牌桌坐标；与 [combine] 严格互逆。 */
    fun toTable(stageX: Float, stageY: Float, out: Vector2 = Vector2()): Vector2 {
        out.set((stageX - OFFSET_X) / SCALE, (stageY - offsetY) / SCALE)
        return out
    }

    /** 局部缩放牌桌坐标 → stage 输入坐标。 */
    fun toStage(tableX: Float, tableY: Float, out: Vector2 = Vector2()): Vector2 {
        out.set(OFFSET_X + tableX * SCALE, offsetY + tableY * SCALE)
        return out
    }
}
