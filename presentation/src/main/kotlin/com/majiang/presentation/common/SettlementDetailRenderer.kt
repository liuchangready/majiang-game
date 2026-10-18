package com.majiang.presentation.common

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.ScoreBreakdownView
import com.majiang.client.api.SettlementDetailView
import com.majiang.client.api.TileView
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin

/**
 * 结算详情弹窗几何布局契约常量
 */
object SettlementModalGeometry {
    val MODAL_RECT = Rectangle(180f, 95f, 920f, 540f)

    val TAB_YAKU_RECT = Rectangle(220f, 515f, 150f, 36f)
    val TAB_DORA_RECT = Rectangle(390f, 515f, 150f, 36f)
    val TAB_CALC_RECT = Rectangle(560f, 515f, 150f, 36f)

    val BTN_PREV_PAGE_RECT = Rectangle(220f, 195f, 100f, 34f)
    val BTN_NEXT_PAGE_RECT = Rectangle(940f, 195f, 100f, 34f)

    const val ROW_START_Y = 495f
    const val ROW_HEIGHT = 42f
    const val TILE_WIDTH = 24f
    const val TILE_HEIGHT = 34f
}

/**
 * 结算明细渲染器 (V4 presentation.common)
 *
 * 遵循架构规范：
 * 1. 独立管理 NotoSansSC-Regular 中文字体资源，使用 FreeType incremental 按需字形，懒加载并安全释放。
 * 2. 避免在 headless / 无 GL 环境下触碰 OpenGL 资源；字体加载失败记录明确错误且避免重试泄漏。
 * 3. 负责和牌结算详情弹窗 (180, 95, 920, 540) 的三标签页、6行明细（文字测量换行与麻将牌面）、翻页控制与底部得分展示。
 * 4. 纯粹基于 client.api 视图模型 (SettlementDetailView, ScoreBreakdownView, TileView)，无 engine/protocol/server 依赖。
 */
class SettlementDetailRenderer @JvmOverloads constructor(
    private val assets: UiAssets? = null
) : Disposable {

    private val layout = GlyphLayout()

    /**
     * 按需安全获取中文字体（借用 UiAssets 管理的 18px 共享字体）。
     * 若在 headless 或无 GL 环境中则返回 null，不引发 GL 崩溃。
     * 组件自身不创建或销毁共享生成器。
     */
    fun getOrInitChineseFont(): BitmapFont? {
        val uiAssets = assets ?: UiAssets.activeInstance
        return uiAssets?.chineseFont
    }

    /**
     * 渲染新版和牌详情模态弹窗
     */
    fun renderDetailModal(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        fallbackFont: BitmapFont,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer,
        settlement: SettlementDetailView,
        breakdown: ScoreBreakdownView,
        section: SettlementSection,
        page: Int,
        viewerSeat: Int,
        currentScores: List<Int>,
        canAgariYame: Boolean,
        isMatchEnd: Boolean,
        btnNextRoundRect: Rectangle,
        btnNextRoundSplitRect: Rectangle,
        btnAgariYameRect: Rectangle
    ) {
        val modalRect = SettlementModalGeometry.MODAL_RECT
        val pageCount = SettlementDetailPresenter.pageCount(breakdown, section)
        val visibleRows = SettlementDetailPresenter.page(breakdown, section, page)

        // 1. 绘制几何底色与边框 (ShapeRenderer)
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 半透明背景遮罩
        shape.color = Color(0f, 0f, 0f, 0.72f)
        shape.rect(0f, 0f, 1280f, 720f)

        // 详情弹窗底色
        shape.color = Color(0.08f, 0.18f, 0.12f, 0.98f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        // 标签栏背景
        drawTabFill(shape, SettlementModalGeometry.TAB_YAKU_RECT, section == SettlementSection.YAKU)
        drawTabFill(shape, SettlementModalGeometry.TAB_DORA_RECT, section == SettlementSection.DORA)
        drawTabFill(shape, SettlementModalGeometry.TAB_CALC_RECT, section == SettlementSection.CALC)

        // 翻页按钮底色
        val canPrev = page > 0
        val canNext = page < pageCount - 1
        shape.color = if (canPrev) Color(0.20f, 0.40f, 0.30f, 1f) else Color(0.18f, 0.22f, 0.20f, 0.5f)
        shape.rect(
            SettlementModalGeometry.BTN_PREV_PAGE_RECT.x,
            SettlementModalGeometry.BTN_PREV_PAGE_RECT.y,
            SettlementModalGeometry.BTN_PREV_PAGE_RECT.width,
            SettlementModalGeometry.BTN_PREV_PAGE_RECT.height
        )

        shape.color = if (canNext) Color(0.20f, 0.40f, 0.30f, 1f) else Color(0.18f, 0.22f, 0.20f, 0.5f)
        shape.rect(
            SettlementModalGeometry.BTN_NEXT_PAGE_RECT.x,
            SettlementModalGeometry.BTN_NEXT_PAGE_RECT.y,
            SettlementModalGeometry.BTN_NEXT_PAGE_RECT.width,
            SettlementModalGeometry.BTN_NEXT_PAGE_RECT.height
        )

        // 6行明细交替斑马线背景
        for (i in 0 until SettlementDetailPresenter.ROWS_PER_PAGE) {
            val rowTop = SettlementModalGeometry.ROW_START_Y - i * SettlementModalGeometry.ROW_HEIGHT
            val rowBottom = rowTop - SettlementModalGeometry.ROW_HEIGHT
            if (i % 2 == 1) {
                shape.color = Color(0.12f, 0.24f, 0.16f, 0.35f)
                shape.rect(modalRect.x + 20f, rowBottom, modalRect.width - 40f, SettlementModalGeometry.ROW_HEIGHT)
            }
        }

        // 下一局 / 续庄 / 终局按钮底色
        if (canAgariYame) {
            shape.color = Color(0.20f, 0.55f, 0.85f, 1f)
            shape.rect(btnNextRoundSplitRect.x, btnNextRoundSplitRect.y, btnNextRoundSplitRect.width, btnNextRoundSplitRect.height)
            shape.color = Color(0.85f, 0.45f, 0.15f, 1f)
            shape.rect(btnAgariYameRect.x, btnAgariYameRect.y, btnAgariYameRect.width, btnAgariYameRect.height)
        } else {
            shape.color = if (isMatchEnd) Color(0.20f, 0.50f, 0.75f, 1f) else Color(0.85f, 0.65f, 0.15f, 1f)
            shape.rect(btnNextRoundRect.x, btnNextRoundRect.y, btnNextRoundRect.width, btnNextRoundRect.height)
        }
        shape.end()

        // 边框绘制
        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.95f, 0.85f, 0.4f, 1f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        drawTabBorder(shape, SettlementModalGeometry.TAB_YAKU_RECT, section == SettlementSection.YAKU)
        drawTabBorder(shape, SettlementModalGeometry.TAB_DORA_RECT, section == SettlementSection.DORA)
        drawTabBorder(shape, SettlementModalGeometry.TAB_CALC_RECT, section == SettlementSection.CALC)

        shape.color = if (canPrev) Color.WHITE else Color(0.45f, 0.45f, 0.45f, 0.5f)
        shape.rect(
            SettlementModalGeometry.BTN_PREV_PAGE_RECT.x,
            SettlementModalGeometry.BTN_PREV_PAGE_RECT.y,
            SettlementModalGeometry.BTN_PREV_PAGE_RECT.width,
            SettlementModalGeometry.BTN_PREV_PAGE_RECT.height
        )

        shape.color = if (canNext) Color.WHITE else Color(0.45f, 0.45f, 0.45f, 0.5f)
        shape.rect(
            SettlementModalGeometry.BTN_NEXT_PAGE_RECT.x,
            SettlementModalGeometry.BTN_NEXT_PAGE_RECT.y,
            SettlementModalGeometry.BTN_NEXT_PAGE_RECT.width,
            SettlementModalGeometry.BTN_NEXT_PAGE_RECT.height
        )

        shape.color = Color.WHITE
        if (canAgariYame) {
            shape.rect(btnNextRoundSplitRect.x, btnNextRoundSplitRect.y, btnNextRoundSplitRect.width, btnNextRoundSplitRect.height)
            shape.rect(btnAgariYameRect.x, btnAgariYameRect.y, btnAgariYameRect.width, btnAgariYameRect.height)
        } else {
            shape.rect(btnNextRoundRect.x, btnNextRoundRect.y, btnNextRoundRect.width, btnNextRoundRect.height)
        }
        shape.end()

        // 2. 收集并提交所有弹窗内牌面 (和了牌 + 明细行小牌面)
        tileDrawBuffer.clear()

        // 和了牌小牌面展示（置于最右侧 x=1030 处，避免覆盖头部概览长文字）
        val winTile = settlement.winTile
        if (winTile != null) {
            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = winTile,
                x = 1030f,
                y = modalRect.y + 468f,
                width = 28f,
                height = 40f,
                isHover = false,
                isDrawn = false
            )
        }

        // 明细行内小牌面（每行右侧展示）
        for ((idx, row) in visibleRows.withIndex()) {
            if (row.tiles.isNotEmpty()) {
                val rowTop = SettlementModalGeometry.ROW_START_Y - idx * SettlementModalGeometry.ROW_HEIGHT
                val rowBottom = rowTop - SettlementModalGeometry.ROW_HEIGHT
                val tileY = rowBottom + 4f
                val count = row.tiles.size
                val rightEdge = modalRect.x + modalRect.width - 40f

                // 区分指示行（前2张为指示与目标）与普通牌列（如赤宝牌）
                val isIndicatorRow = count >= 2 && (row.text.contains("指示牌") || row.text.contains("→"))
                val totalWidth = if (isIndicatorRow) {
                    2 * 27f + 10f + (count - 2) * 27f
                } else {
                    count * 27f
                }
                val startX = rightEdge - totalWidth

                for ((tIdx, tile) in row.tiles.withIndex()) {
                    val tileX = if (isIndicatorRow && tIdx >= 2) {
                        startX + 2 * 27f + 10f + (tIdx - 2) * 27f
                    } else {
                        startX + tIdx * 27f
                    }
                    tileSkin.appendTileDraw(
                        buf = tileDrawBuffer,
                        tile = tile,
                        x = tileX,
                        y = tileY,
                        width = SettlementModalGeometry.TILE_WIDTH,
                        height = SettlementModalGeometry.TILE_HEIGHT,
                        isHover = false,
                        isDrawn = false
                    )
                }
            }
        }

        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined
        tileDrawBuffer.flush(batch, shape, fallbackFont)

        // 3. 绘制文字内容 (使用 NotoSansSC 中文字体，若无则回退默认字体)
        val textFont = getOrInitChineseFont() ?: fallbackFont
        val isCustomFont = (textFont !== fallbackFont)

        batch.projectionMatrix = camera.combined
        batch.begin()

        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY
        val origColor = Color(textFont.color)

        try {
            // 3.1 弹窗大标题与和牌概览
            val winnerName = if (settlement.winner == viewerSeat) {
                "P${settlement.winner} [YOU]"
            } else {
                "P${settlement.winner} [AI ${settlement.winner}]"
            }
            val loserName = if (settlement.loser != null) {
                if (settlement.loser == viewerSeat) "P${settlement.loser} [YOU]" else "P${settlement.loser} [AI ${settlement.loser}]"
            } else null

            if (!isCustomFont) textFont.data.setScale(1.4f)
            textFont.color = Color(1.0f, 0.85f, 0.35f, 1f)
            textFont.draw(batch, settlement.title, modalRect.x + 40f, modalRect.y + 515f)

            if (!isCustomFont) textFont.data.setScale(1.0f)
            textFont.color = Color.WHITE

            val isYakuman = (breakdown.yakumanCount > 0 || breakdown.score.isYakuman)
            val mult = if (breakdown.yakumanCount > 0) breakdown.yakumanCount else maxOf(1, breakdown.score.yakumanMultiplier)
            val scoreDesc = if (isYakuman) {
                val label = if (mult > 1) "${mult}倍役满" else "役满"
                "$label → ${breakdown.totalPoints}点"
            } else {
                "${breakdown.score.han}番 ${breakdown.score.fu}符 → ${breakdown.totalPoints}点"
            }

            val overview = if (loserName != null) {
                "Winner: $winnerName   From: $loserName   ($scoreDesc)"
            } else {
                "Winner: $winnerName   (自摸 $scoreDesc)"
            }
            textFont.draw(batch, overview, modalRect.x + 40f, modalRect.y + 482f)

            // 和了牌标注文字
            if (settlement.winTile != null) {
                textFont.color = Color(1.0f, 0.85f, 0.35f, 1f)
                textFont.draw(batch, "和了牌", 975f, modalRect.y + 494f)
            }

            // 3.2 标签按钮文字
            drawTabTitle(batch, textFont, isCustomFont, "役种明细", SettlementModalGeometry.TAB_YAKU_RECT, section == SettlementSection.YAKU)
            drawTabTitle(batch, textFont, isCustomFont, "宝牌明细", SettlementModalGeometry.TAB_DORA_RECT, section == SettlementSection.DORA)
            drawTabTitle(batch, textFont, isCustomFont, "计算推导", SettlementModalGeometry.TAB_CALC_RECT, section == SettlementSection.CALC)

            // 3.3 6行明细文字（测量排版与最多2行换行）
            for ((idx, row) in visibleRows.withIndex()) {
                val rowTop = SettlementModalGeometry.ROW_START_Y - idx * SettlementModalGeometry.ROW_HEIGHT
                val rowBottom = rowTop - SettlementModalGeometry.ROW_HEIGHT

                // 计算可用文本宽度：正文横向范围 x220..1060；牌行预留右侧 tiles 个数×27+10+16 padding
                val count = row.tiles.size
                val maxTextWidth = if (count > 0) {
                    val rightEdge = modalRect.x + modalRect.width - 40f
                    val isIndicatorRow = count >= 2 && (row.text.contains("指示牌") || row.text.contains("→"))
                    val totalTilesWidth = if (isIndicatorRow) {
                        2 * 27f + 10f + (count - 2) * 27f
                    } else {
                        count * 27f
                    }
                    val startX = rightEdge - totalTilesWidth
                    (startX - 16f) - (modalRect.x + 40f)
                } else {
                    (modalRect.x + modalRect.width - 40f) - (modalRect.x + 40f) // 1060 - 220 = 840f
                }

                textFont.color = determineRowTextColor(section, row)
                if (!isCustomFont) textFont.data.setScale(0.92f)

                val lines = wrapRowText(textFont, row.text, maxTextWidth)
                val textX = modalRect.x + 40f
                if (lines.isEmpty()) {
                    // 无内容
                } else if (lines.size == 1) {
                    textFont.draw(batch, lines[0], textX, rowBottom + 26f)
                } else if (lines.size == 2) {
                    textFont.draw(batch, lines[0], textX, rowTop - 13f)
                    textFont.draw(batch, lines[1], textX, rowTop - 31f)
                } else {
                    val step = (SettlementModalGeometry.ROW_HEIGHT - 8f) / lines.size
                    for ((lIdx, line) in lines.withIndex()) {
                        textFont.draw(batch, line, textX, rowTop - 10f - lIdx * step)
                    }
                }
            }

            // 3.4 翻页与页码
            if (!isCustomFont) textFont.data.setScale(1.0f)
            textFont.color = if (canPrev) Color.WHITE else Color(0.5f, 0.5f, 0.5f, 0.5f)
            textFont.draw(batch, "< 上一页", SettlementModalGeometry.BTN_PREV_PAGE_RECT.x + 14f, SettlementModalGeometry.BTN_PREV_PAGE_RECT.y + 24f)

            textFont.color = if (canNext) Color.WHITE else Color(0.5f, 0.5f, 0.5f, 0.5f)
            textFont.draw(batch, "下一页 >", SettlementModalGeometry.BTN_NEXT_PAGE_RECT.x + 14f, SettlementModalGeometry.BTN_NEXT_PAGE_RECT.y + 24f)

            textFont.color = Color(1.0f, 0.9f, 0.6f, 1f)
            val pageStr = "第 ${page + 1} / $pageCount 页"
            layout.setText(textFont, pageStr)
            val pageTextX = (SettlementModalGeometry.BTN_PREV_PAGE_RECT.x + SettlementModalGeometry.BTN_PREV_PAGE_RECT.width +
                    SettlementModalGeometry.BTN_NEXT_PAGE_RECT.x - layout.width) / 2f
            textFont.draw(batch, pageStr, pageTextX, SettlementModalGeometry.BTN_PREV_PAGE_RECT.y + 24f)

            // 3.5 底部四家真实分数与增减（底部 y132/y110，两行各两列，脱离上一页按钮 y195..229 与下一局按钮 y145..191）
            textFont.color = Color.LIGHT_GRAY
            for (i in 0 until 4) {
                val delta = settlement.payments[i] ?: 0
                val deltaStr = if (delta >= 0) "+$delta" else "$delta"
                val scoreNow = if (settlement.newScores.size == 4) {
                    settlement.newScores[i]
                } else if (i < currentScores.size) {
                    currentScores[i]
                } else {
                    0
                }
                val pName = if (i == viewerSeat) "P$i [YOU]" else "P$i [AI $i]"
                val line = "$pName: $scoreNow pts ($deltaStr)"
                val col = i % 2
                val row = i / 2
                val posX = if (col == 0) 220f else 680f
                val posY = if (row == 0) 132f else 110f
                textFont.draw(batch, line, posX, posY)
            }

            // 3.6 下一局 / 续庄 / 终局按钮文字
            if (!isCustomFont) textFont.data.setScale(1.2f)
            if (canAgariYame) {
                textFont.color = Color.WHITE
                textFont.draw(batch, "CONTINUE", btnNextRoundSplitRect.x + 60f, btnNextRoundSplitRect.y + 30f)
                textFont.draw(batch, "AGARI-YAME", btnAgariYameRect.x + 50f, btnAgariYameRect.y + 30f)
            } else if (isMatchEnd) {
                textFont.color = Color.WHITE
                val label = if (isCustomFont) "查看总成绩" else "MATCH ENDED"
                layout.setText(textFont, label)
                val textX = btnNextRoundRect.x + (btnNextRoundRect.width - layout.width) / 2f
                textFont.draw(batch, label, textX, btnNextRoundRect.y + 32f)
            } else {
                textFont.color = Color.BLACK
                textFont.draw(batch, "NEXT ROUND >", btnNextRoundRect.x + 40f, btnNextRoundRect.y + 32f)
            }
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
            if (batch.isDrawing) {
                batch.end()
            }
        }
    }

    /**
     * 行内文本宽度测量与自适应换行，确保每行测量宽度严格 <= maxWidth 且不丢失不截断文本
     */
    fun wrapRowText(font: BitmapFont, text: String, maxWidth: Float): List<String> =
        Companion.wrapRowText(font, text, maxWidth)

    companion object {
        /**
         * 纯函数文本折行测量，确保每行 GlyphLayout.width <= maxWidth
         */
        fun wrapRowText(font: BitmapFont, text: String, maxWidth: Float): List<String> {
            if (text.isEmpty()) return listOf("")
            val testLayout = GlyphLayout()
            testLayout.setText(font, text)
            if (testLayout.width <= maxWidth) {
                return listOf(text)
            }

            val lines = mutableListOf<String>()
            var remaining = text

            while (remaining.isNotEmpty()) {
                testLayout.setText(font, remaining)
                if (testLayout.width <= maxWidth) {
                    lines.add(remaining)
                    break
                }

                // 1. 二分查找最大可容纳前缀字符数 (1..remaining.length)
                var low = 1
                var high = remaining.length
                var bestSplit = 1
                while (low <= high) {
                    val mid = (low + high) / 2
                    testLayout.setText(font, remaining.substring(0, mid))
                    if (testLayout.width <= maxWidth) {
                        bestSplit = mid
                        low = mid + 1
                    } else {
                        high = mid - 1
                    }
                }

                // 2. 标点与分隔符回退扫描，安全范围为已容纳前缀内部 0 until bestSplit (即 0..bestSplit - 1)
                var splitIndex = bestSplit
                val lookback = maxOf(0, bestSplit - 8)
                for (j in (bestSplit - 1) downTo lookback) {
                    if (j in 0 until remaining.length && remaining[j] in " ，、：: ；;（(→|+-") {
                        splitIndex = j + 1
                        break
                    }
                }

                // 确保 splitIndex 至少为 1 且绝不超过 bestSplit (绝不超出已容纳长度导致超宽)
                splitIndex = maxOf(1, minOf(splitIndex, bestSplit))

                // 3. 严格宽度校验与收缩保证：确保当前行测量宽度严格 <= maxWidth
                while (splitIndex > 1) {
                    testLayout.setText(font, remaining.substring(0, splitIndex).trimEnd())
                    if (testLayout.width <= maxWidth) break
                    splitIndex--
                }

                val currentLine = remaining.substring(0, splitIndex).trimEnd()
                lines.add(currentLine)
                var next = remaining.substring(splitIndex)
                if (next.startsWith(" ")) {
                    next = next.trimStart()
                }
                remaining = next
            }

            return if (lines.isEmpty()) listOf(text) else lines
        }
    }

    private fun drawTabFill(shape: ShapeRenderer, rect: Rectangle, isActive: Boolean) {
        shape.color = if (isActive) Color(0.85f, 0.45f, 0.15f, 1f) else Color(0.18f, 0.28f, 0.22f, 0.85f)
        shape.rect(rect.x, rect.y, rect.width, rect.height)
    }

    private fun drawTabBorder(shape: ShapeRenderer, rect: Rectangle, isActive: Boolean) {
        shape.color = if (isActive) Color.YELLOW else Color(0.55f, 0.65f, 0.55f, 0.7f)
        shape.rect(rect.x, rect.y, rect.width, rect.height)
    }

    private fun drawTabTitle(
        batch: SpriteBatch,
        font: BitmapFont,
        isCustomFont: Boolean,
        title: String,
        rect: Rectangle,
        isActive: Boolean
    ) {
        font.color = if (isActive) Color.WHITE else Color(0.80f, 0.85f, 0.80f, 0.9f)
        layout.setText(font, title)
        val tx = rect.x + (rect.width - layout.width) / 2f
        val ty = rect.y + (rect.height + layout.height) / 2f
        font.draw(batch, title, tx, ty)
    }

    private fun determineRowTextColor(section: SettlementSection, row: SettlementDetailRow): Color = when (section) {
        SettlementSection.YAKU -> when {
            row.text.contains("役满") -> Color(1.0f, 0.85f, 0.35f, 1f)
            row.text.contains("最高得点") || row.text.contains("拆解") -> Color(0.70f, 0.95f, 0.75f, 1f)
            row.text.contains("小计") || row.text.contains("等式") -> Color(1.0f, 0.95f, 0.60f, 1f)
            else -> Color(0.88f, 0.95f, 0.88f, 1f)
        }
        SettlementSection.DORA -> when {
            row.text.contains("命中") && !row.text.contains("命中 0") -> Color(1.0f, 0.90f, 0.40f, 1f)
            row.text.contains("未立直") -> Color(0.70f, 0.70f, 0.70f, 1f)
            row.text.contains("明细") -> Color(1.0f, 0.85f, 0.35f, 1f)
            else -> Color(0.88f, 0.95f, 0.88f, 1f)
        }
        SettlementSection.CALC -> when {
            row.text.contains("总番") || row.text.contains("最终") -> Color(1.0f, 0.85f, 0.35f, 1f)
            row.text.contains("公式") || row.text.contains("封顶") || row.text.contains("计算") -> Color(1.0f, 0.95f, 0.70f, 1f)
            row.text.contains("支付") -> Color(1.0f, 0.80f, 0.50f, 1f)
            else -> Color(0.88f, 0.95f, 0.88f, 1f)
        }
    }

    override fun dispose() {
        // 借用 UiAssets 管理的字体与生命周期，组件不自行释放共享生成器与共享字体
    }
}
