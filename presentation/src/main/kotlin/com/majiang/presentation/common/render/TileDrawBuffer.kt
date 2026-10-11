package com.majiang.presentation.common.render

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.Texture
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer

/**
 * ShapeRenderer.ShapeType.Filled 填充矩形绘制指令。
 */
data class FilledRectOp(
    val x: Float,
    val y: Float,
    val width: Float,
    val height: Float,
    val color: Color
)

/**
 * ShapeRenderer.ShapeType.Line 线框矩形绘制指令。
 */
data class LineRectOp(
    val x: Float,
    val y: Float,
    val width: Float,
    val height: Float,
    val color: Color
)

/**
 * SpriteBatch 纹理绘制指令。
 * 支持可选 rotationDeg (旋转角度)、originX/Y (旋转中心)、tint (着色) 与 scaleX/Y。
 */
data class TextureOp(
    val texture: Texture,
    val x: Float,
    val y: Float,
    val width: Float,
    val height: Float,
    val rotationDeg: Float = 0f,
    val originX: Float = width / 2f,
    val originY: Float = height / 2f,
    val tint: Color = Color.WHITE,
    val scaleX: Float = 1f,
    val scaleY: Float = 1f
)

/**
 * BitmapFont 牌面文字绘制指令。
 */
data class TextOp(
    val text: String,
    val color: Color,
    val x: Float,
    val y: Float,
    val scaleX: Float,
    val scaleY: Float
)

/**
 * 纯数据牌面绘制指令缓冲 (TileDrawBuffer)。
 *
 * 采用图元分桶（Bucket）机制收集一帧内的所有牌面绘制指令，在合适的时机合并单批次提交：
 * 1. 所有 Filled 填充矩形合并进一次 [ShapeRenderer.begin(Filled)] / [ShapeRenderer.end]；
 * 2. 所有纹理（含普通牌面与 overlayTextures 飞行手套）与文字绘制合并进恰好一次 [SpriteBatch.begin] / [SpriteBatch.end]；
 * 3. 所有 overlay 覆盖层填充矩形（牌面之上的辉光等）合并进第二次 Filled 会话；
 * 4. 所有 Line 描边线框合并进一次 [ShapeRenderer.begin(Line)] / [ShapeRenderer.end]。
 *
 * 分层等价性论证：
 * 牌与牌在桌面上互不重叠。在各自独立的矩形区域内，"逐张绘制 背景→牌面→边框" 与
 * "批次绘制 全部背景→全部牌面→全部边框" 在像素级别是严格等价的。
 * 将图元按类别分组批次 flush，消除原先每张牌频繁切换 batch.end/begin 和 shape.begin/end 造成的 GPU draw call 碎片，
 * 最终渲染画面逐像素完全一致。
 */
class TileDrawBuffer {
    val filledRects: MutableList<FilledRectOp> = mutableListOf()
    val lineRects: MutableList<LineRectOp> = mutableListOf()
    val textureDraws: MutableList<TextureOp> = mutableListOf()
    val textDraws: MutableList<TextOp> = mutableListOf()

    /** 覆盖层填充矩形：在纹理与文字之后、线框之前 flush（用于宝牌辉光叠加等"压在牌面之上"的效果） */
    val overlayRects: MutableList<FilledRectOp> = mutableListOf()

    /** 覆盖层纹理：在桌面纹理与文字之后、同一个 SpriteBatch 会话中 flush（用于飞行牌与幽灵白手套，手压在牌之上） */
    val overlayTextures: MutableList<TextureOp> = mutableListOf()

    fun addFilledRect(x: Float, y: Float, width: Float, height: Float, color: Color) {
        filledRects.add(FilledRectOp(x, y, width, height, Color(color)))
    }

    fun addOverlayRect(x: Float, y: Float, width: Float, height: Float, color: Color) {
        overlayRects.add(FilledRectOp(x, y, width, height, Color(color)))
    }

    fun addLineRect(x: Float, y: Float, width: Float, height: Float, color: Color) {
        lineRects.add(LineRectOp(x, y, width, height, Color(color)))
    }

    fun addTexture(
        texture: Texture,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        rotationDeg: Float = 0f,
        originX: Float = width / 2f,
        originY: Float = height / 2f,
        tint: Color = Color.WHITE,
        scaleX: Float = 1f,
        scaleY: Float = 1f
    ) {
        textureDraws.add(
            TextureOp(
                texture = texture,
                x = x,
                y = y,
                width = width,
                height = height,
                rotationDeg = rotationDeg,
                originX = originX,
                originY = originY,
                tint = Color(tint),
                scaleX = scaleX,
                scaleY = scaleY
            )
        )
    }

    fun addOverlayTexture(
        texture: Texture,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        rotationDeg: Float = 0f,
        originX: Float = width / 2f,
        originY: Float = height / 2f,
        tint: Color = Color.WHITE,
        scaleX: Float = 1f,
        scaleY: Float = 1f
    ) {
        overlayTextures.add(
            TextureOp(
                texture = texture,
                x = x,
                y = y,
                width = width,
                height = height,
                rotationDeg = rotationDeg,
                originX = originX,
                originY = originY,
                tint = Color(tint),
                scaleX = scaleX,
                scaleY = scaleY
            )
        )
    }

    fun addText(text: String, color: Color, x: Float, y: Float, scaleX: Float, scaleY: Float) {
        textDraws.add(TextOp(text, Color(color), x, y, scaleX, scaleY))
    }

    /**
     * 清空所有图元缓冲（幂等）。
     */
    fun clear() {
        filledRects.clear()
        lineRects.clear()
        textureDraws.clear()
        textDraws.clear()
        overlayRects.clear()
        overlayTextures.clear()
    }

    /**
     * 判断当前缓冲是否没有任何待绘制指令。
     */
    fun isEmpty(): Boolean =
        filledRects.isEmpty() && lineRects.isEmpty() && textureDraws.isEmpty() &&
                textDraws.isEmpty() && overlayRects.isEmpty() && overlayTextures.isEmpty()

    /**
     * 将缓冲中的所有牌面图元指令分组执行单批次绘制。
     * flush 顺序：Filled 矩形 →【batch.begin 一次：纹理 + 文字 + overlay 纹理】→ overlay 覆盖层 → Line 矩形。
     * 绘制完成后自动清空缓冲 (clear)。
     */
    fun flush(batch: SpriteBatch, shape: ShapeRenderer, font: BitmapFont) {
        if (isEmpty()) return

        // 1. Filled 填充矩形批次（牌体底色与内阴影）
        if (filledRects.isNotEmpty()) {
            val prevShapeType = if (shape.isDrawing) shape.currentType else null
            if (prevShapeType != null) shape.end()
            ensureBlendEnabled()
            shape.begin(ShapeRenderer.ShapeType.Filled)
            for (op in filledRects) {
                shape.color = op.color
                shape.rect(op.x, op.y, op.width, op.height)
            }
            shape.end()
            if (prevShapeType != null) shape.begin(prevShapeType)
        }

        // 2. 纹理与文字批次（合并在同一个 batch.begin / batch.end 会话中，保持恰好一次）
        if (textureDraws.isNotEmpty() || textDraws.isNotEmpty() || overlayTextures.isNotEmpty()) {
            val wasBatchDrawing = batch.isDrawing
            val initialBatchColor = Color(batch.color)
            val initialFontColor = Color(font.color)
            val initialFontScaleX = font.data.scaleX
            val initialFontScaleY = font.data.scaleY
            if (!wasBatchDrawing) batch.begin()

            try {
                // 绘制所有桌面普通纹理
                if (textureDraws.isNotEmpty()) {
                    for (op in textureDraws) {
                        drawTextureOp(batch, op)
                    }
                }

                // 绘制所有牌面文字
                if (textDraws.isNotEmpty()) {
                    for (op in textDraws) {
                        font.data.setScale(op.scaleX, op.scaleY)
                        font.color = op.color
                        font.draw(batch, op.text, op.x, op.y)
                    }
                }

                // 绘制覆盖层纹理（飞行牌与幽灵白手套：手压在牌之上）
                if (overlayTextures.isNotEmpty()) {
                    for (op in overlayTextures) {
                        drawTextureOp(batch, op)
                    }
                }
            } finally {
                batch.color = initialBatchColor
                font.data.setScale(initialFontScaleX, initialFontScaleY)
                font.color = initialFontColor
                if (!wasBatchDrawing) batch.end()
            }
        }

        // 3. 覆盖层填充矩形批次（纹理/文字之上、线框之下）：宝牌整牌辉光叠加、闪光星等
        if (overlayRects.isNotEmpty()) {
            val prevShapeType = if (shape.isDrawing) shape.currentType else null
            if (prevShapeType != null) shape.end()
            ensureBlendEnabled()
            shape.begin(ShapeRenderer.ShapeType.Filled)
            for (op in overlayRects) {
                shape.color = op.color
                shape.rect(op.x, op.y, op.width, op.height)
            }
            shape.end()
            if (prevShapeType != null) shape.begin(prevShapeType)
        }

        // 4. Line 线框矩形批次
        if (lineRects.isNotEmpty()) {
            val prevShapeType = if (shape.isDrawing) shape.currentType else null
            if (prevShapeType != null) shape.end()
            ensureBlendEnabled()
            shape.begin(ShapeRenderer.ShapeType.Line)
            for (op in lineRects) {
                shape.color = op.color
                shape.rect(op.x, op.y, op.width, op.height)
            }
            shape.end()
            if (prevShapeType != null) shape.begin(prevShapeType)
        }

        clear()
    }

    private fun drawTextureOp(batch: SpriteBatch, op: TextureOp) {
        batch.color = op.tint
        if (op.rotationDeg != 0f || op.scaleX != 1f || op.scaleY != 1f) {
            batch.draw(
                op.texture,
                op.x, op.y,
                op.originX, op.originY,
                op.width, op.height,
                op.scaleX, op.scaleY,
                op.rotationDeg,
                0, 0,
                op.texture.width, op.texture.height,
                false, false
            )
        } else {
            batch.draw(op.texture, op.x, op.y, op.width, op.height)
        }
    }

    /**
     * 确保标准 Alpha 混合处于开启状态。
     *
     * ⚠️ 每个 ShapeRenderer pass 前都必须重新调用：libGDX 在 ShapeRenderer.end()
     * 与 SpriteBatch/ShapeRenderer 会话切换时可能重置 GL_BLEND，若混合被关闭，
     * 带透明度的颜色（如宝牌辉光）会以不透明绘制，直接遮住牌面。
     */
    private fun ensureBlendEnabled() {
        val gl = Gdx.gl ?: return
        gl.glEnable(GL20.GL_BLEND)
        gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
    }
}
