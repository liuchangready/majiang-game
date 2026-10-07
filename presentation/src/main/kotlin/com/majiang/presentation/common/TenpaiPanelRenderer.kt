package com.majiang.presentation.common

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.TileView
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin

/**
 * 手牌上方常驻听牌小面板渲染器 (V4 presentation.common)。
 *
 * 契约规范：
 * 1. 玩家进入听牌即在手牌上方展示常驻小面板，显示“听牌”或“原听牌”及所有理论待牌牌面；
 * 2. 复用现有 [TileSkin] 绘制待牌牌面；面板宽约 420px，牌面 32x44，横向间隔 36，每行 7 张；
 * 3. 单行面板高 126px，双行高 172px，保留 x220, y195 底边严格在操作按钮之上；
 * 4. 标题距顶 12px，说明距顶 40px（字号至少约 15px），牌面底行距底 14px，第二行高 50px（y14 + 50 = y64）；
 * 5. 说明文案严格包含“牌型待牌，和牌仍需满足役种与振听规则”，振听时提示“振听：不能荣和”，glyphLayout 动态适配不截断越界；
 * 6. 支持普通2面、国士无双13面等所有待牌多行自适应；
 * 7. 输入纯粹采用 client.api.TileView，无 engine 依赖。
 */
class TenpaiPanelRenderer {

    private val glyphLayout = GlyphLayout()

    /**
     * 计算常驻听牌面板的包围盒矩形。
     */
    fun computePanelRect(waitsCount: Int): Rectangle {
        val isTwoRows = waitsCount > 7
        val width = 420f
        val height = if (isTwoRows) 172f else 126f
        // x=220f 对齐手牌左边缘，y=195f 严格位于手牌 (40..120) 及操作按钮 (145..189) 之上
        return Rectangle(220f, 195f, width, height)
    }

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        fallbackFont: BitmapFont,
        chineseFont: BitmapFont?,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer,
        waits: List<TileView>,
        isOriginalWaits: Boolean,
        isFuriten: Boolean
    ) {
        if (waits.isEmpty()) return

        val panelRect = computePanelRect(waits.size)
        val isTwoRows = waits.size > 7
        val textFont = chineseFont ?: fallbackFont
        val isCustomFont = (chineseFont != null)

        // 1. 绘制面板背景与边框 (ShapeRenderer)
        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)

        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0.04f, 0.12f, 0.08f, 0.92f)
        shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = if (isFuriten) {
            Color(1.0f, 0.40f, 0.30f, 0.95f) // 振听警告边框
        } else {
            Color(0.85f, 0.72f, 0.35f, 0.95f) // 正常金色边框
        }
        shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)
        shape.end()

        // 2. 收集并提交所有待牌牌面 (32x44, spacingX=36, 每行7张, 底行距底14px, 第二行距底64px)
        tileDrawBuffer.clear()
        val tileW = 32f
        val tileH = 44f
        val spacingX = 36f

        for ((idx, tile) in waits.withIndex()) {
            val tileX: Float
            val tileY: Float
            if (isTwoRows) {
                val row = idx / 7 // 0 为上半行，1 为下半行
                val col = idx % 7
                tileX = panelRect.x + 14f + col * spacingX
                tileY = if (row == 0) panelRect.y + 14f + 50f else panelRect.y + 14f
            } else {
                tileX = panelRect.x + 14f + idx * spacingX
                tileY = panelRect.y + 14f
            }

            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = tile,
                x = tileX,
                y = tileY,
                width = tileW,
                height = tileH,
                isHover = false,
                isDrawn = false
            )
        }

        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, fallbackFont)

        // 3. 绘制文字信息 (SpriteBatch)
        batch.projectionMatrix = camera.combined
        batch.begin()

        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY
        val origColor = Color(textFont.color)

        try {
            val maxAvailableWidth = panelRect.width - 28f

            // 3.1 标题行 (原听牌 / 听牌 + 待牌数 + 振听提示，距顶12px)
            val titleY = panelRect.y + panelRect.height - 12f
            val textStartX = panelRect.x + 14f

            val titlePrefix = if (isCustomFont) {
                if (isOriginalWaits) "原听牌" else "听牌"
            } else {
                if (isOriginalWaits) "ORIG TENPAI" else "TENPAI"
            }
            val titleFull = "$titlePrefix (${waits.size})"
            val furitenLabel = if (isFuriten) {
                if (isCustomFont) " [振听：不能荣和]" else " [FURITEN: TSUMO ONLY]"
            } else null

            val baseTitleScale = if (isCustomFont) 1.0f else 0.95f
            textFont.data.setScale(baseTitleScale, baseTitleScale)
            glyphLayout.setText(textFont, titleFull)
            var titleWidth = glyphLayout.width

            var furitenWidth = 0f
            if (furitenLabel != null) {
                glyphLayout.setText(textFont, furitenLabel)
                furitenWidth = glyphLayout.width
            }

            val totalTitleWidth = titleWidth + (if (furitenLabel != null) furitenWidth + 6f else 0f)
            if (totalTitleWidth > maxAvailableWidth && totalTitleWidth > 0f) {
                val fitScale = baseTitleScale * (maxAvailableWidth / totalTitleWidth)
                textFont.data.setScale(fitScale, fitScale)
                glyphLayout.setText(textFont, titleFull)
                titleWidth = glyphLayout.width
            }

            textFont.color = Color(1.0f, 0.88f, 0.35f, 1f)
            textFont.draw(batch, titleFull, textStartX, titleY)

            // 振听警示（振听：不能荣和）
            if (furitenLabel != null) {
                textFont.color = Color(1.0f, 0.38f, 0.30f, 1f)
                textFont.draw(batch, furitenLabel, textStartX + titleWidth + 6f, titleY)
            }

            // 3.2 说明行：牌型待牌，和牌仍需满足役种与振听规则（距顶40px，字号至少约15px）
            val subtitleY = panelRect.y + panelRect.height - 40f
            val subtitleText = if (isCustomFont) {
                "牌型待牌，和牌仍需满足役种与振听规则"
            } else {
                "Tile waits; requires yaku & furiten rules."
            }

            // 中文字体基础字号 18px，scale 0.85 对应约 15.3px；英文基础 15px，scale 1.0 对应 15px
            val baseSubtitleScale = if (isCustomFont) 0.85f else 1.0f
            textFont.data.setScale(baseSubtitleScale, baseSubtitleScale)
            glyphLayout.setText(textFont, subtitleText)
            if (glyphLayout.width > maxAvailableWidth && glyphLayout.width > 0f) {
                val fitScale = baseSubtitleScale * (maxAvailableWidth / glyphLayout.width)
                textFont.data.setScale(fitScale, fitScale)
            }

            textFont.color = Color(0.75f, 0.85f, 0.75f, 0.90f)
            textFont.draw(batch, subtitleText, textStartX, subtitleY)
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
        }

        batch.end()
    }
}
