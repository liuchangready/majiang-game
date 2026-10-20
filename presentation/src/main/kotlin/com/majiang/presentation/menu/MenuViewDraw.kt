package com.majiang.presentation.menu

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle

/**
 * 菜单与大厅视图共享绘制辅助工具。
 *
 * 封装按钮、输入框及居中文本的基础绘制，各子视图与 [MenuScreen] 共享此工具。
 */
internal object MenuViewDraw {
    private val glyphLayout = GlyphLayout()

    fun drawButton(shape: ShapeRenderer, rect: Rectangle, isPrimary: Boolean) {
        if (isPrimary) {
            shape.color = Color(0.82f, 0.22f, 0.15f, 1f)
        } else {
            shape.color = Color(0.12f, 0.24f, 0.18f, 1f)
        }
        shape.rect(rect.x, rect.y, rect.width, rect.height)
    }

    fun drawInputBox(shape: ShapeRenderer, rect: Rectangle, isActive: Boolean) {
        shape.color = if (isActive) Color(0.15f, 0.35f, 0.25f, 1f) else Color(0.08f, 0.18f, 0.12f, 1f)
        shape.rect(rect.x, rect.y, rect.width, rect.height)
    }

    fun drawCenteredText(batch: SpriteBatch, font: BitmapFont, text: String, cx: Float, cy: Float) {
        glyphLayout.setText(font, text)
        font.draw(batch, text, cx - glyphLayout.width / 2f, cy)
    }
}
