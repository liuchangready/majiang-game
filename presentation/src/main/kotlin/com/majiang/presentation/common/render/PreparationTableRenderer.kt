package com.majiang.presentation.common.render

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.g2d.TextureRegion
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Matrix4
import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.PlayerView

/**
 * 正式 APP 开局准备阶段的桌面场景渲染器 (PreparationTableRenderer)。
 *
 * 设计来源：[doc/ui/round-preparation/DESIGN.md](file:///D:/majiang/doc/ui/round-preparation/DESIGN.md) §1/§6。
 *
 * 核心契约：
 * 1. 场景分层：L0 麻将馆环境（复用根 assets 的 club-background 底图）→ L1 翡翠桌布 / 朱红木边 / 暖金描线
 *    与公开四席、局次 → 准备栏与抽屉由 [com.majiang.presentation.game.ShufflePreparationPanel] 叠加；
 * 2. 发牌前 **不绘制任何实体牌**：手牌区、牌河、牌山一律保持空白，只显示「准备完成后发牌」占位文案，
 *    绝不杜撰不存在的牌或上一局的残留；
 * 3. 公开四席信息（风位、座位号、点数、庄家标记）与局次全部来自 client.api 的 [PlayerView]；
 * 4. 只依据 [safeArea] 构图：准备栏/抽屉展开时公开席位依旧落在安全桌区内，不被遮没；
 * 5. 纯渲染，无会话 / 权威状态，不改变座位、实体牌或引擎规则；
 * 6. 绘制结束后恢复 batch 颜色与投影矩阵，避免污染后续牌局渲染。
 */
object PreparationTableRenderer {

    /** 席位铭牌尺寸。 */
    const val SEAT_PLATE_WIDTH = 196f
    const val SEAT_PLATE_HEIGHT = 60f

    /** 中文字号（UiAssets 的 18px 共享中文字体）。 */
    const val CHINESE_FONT_SIZE = 18f

    /** 占位标题。 */
    const val TITLE_TEXT = "准备完成后发牌"

    /** 占位标题字号缩放。 */
    const val TITLE_FONT_SCALE = 1.05f

    /** 占位说明（分两行，避免横压席位铭牌）。 */
    val PLACEHOLDER_LINES = listOf("手牌、牌河与牌山", "准备完成后由权威生成")

    /** 占位说明字号缩放。 */
    const val PLACEHOLDER_FONT_SCALE = 0.9f

    /** 中央文字区与席位铭牌之间的最小留白。 */
    const val TEXT_BAND_PADDING = 16f

    /** 中央文字区的最小宽度（低于该值时不再收窄）。 */
    const val MIN_TEXT_BAND_WIDTH = 120f

    private val glyphLayout = GlyphLayout()

    /**
     * 渲染准备阶段桌面。
     *
     * @param view 权威公开视图（只读取公开信息）
     * @param visible 当前可见虚拟世界区域
     * @param safeArea 剩余安全桌区（准备栏/抽屉左侧）
     * @param backdrop 麻将馆环境底图（可为空，退回纯色背景）
     */
    fun render(
        view: PlayerView,
        visible: Rectangle,
        safeArea: Rectangle,
        projection: Matrix4,
        batch: SpriteBatch,
        shape: ShapeRenderer,
        font: BitmapFont,
        chineseFont: BitmapFont?,
        backdrop: TextureRegion?
    ) {
        drawBackdrop(batch, backdrop, visible, projection)

        shape.projectionMatrix = projection
        shape.begin(ShapeRenderer.ShapeType.Filled)
        drawTableFurniture(shape, safeArea)
        drawSeatPlates(shape, view, safeArea)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        drawTableBorders(shape, safeArea)
        shape.end()

        drawTexts(batch, view, safeArea, projection, font, chineseFont)
    }

    private fun drawBackdrop(
        batch: SpriteBatch,
        backdrop: TextureRegion?,
        visible: Rectangle,
        projection: Matrix4
    ) {
        val priorColor = Color(batch.color)
        val priorProjection = Matrix4(batch.projectionMatrix)
        try {
            if (backdrop != null && visible.width > 0f && visible.height > 0f) {
                // cover：等比放大铺满可见区域，允许裁掉边缘，绝不拉伸。
                val scale = maxOf(
                    visible.width / backdrop.regionWidth.toFloat(),
                    visible.height / backdrop.regionHeight.toFloat()
                )
                val width = backdrop.regionWidth * scale
                val height = backdrop.regionHeight * scale
                batch.projectionMatrix = projection
                batch.begin()
                try {
                    batch.color = Color.WHITE
                    batch.draw(
                        backdrop,
                        visible.x + (visible.width - width) / 2f,
                        visible.y + (visible.height - height) / 2f,
                        width,
                        height
                    )
                } finally {
                    if (batch.isDrawing) batch.end()
                }
            }
        } finally {
            batch.color = priorColor
            batch.projectionMatrix = priorProjection
        }
    }

    private fun drawTableFurniture(shape: ShapeRenderer, safeArea: Rectangle) {
        // 朱红木边
        shape.color = Color(0.40f, 0.15f, 0.10f, 1f)
        shape.rect(safeArea.x, safeArea.y, safeArea.width, safeArea.height)
        // 暖金描线
        shape.color = Color(0.78f, 0.64f, 0.32f, 1f)
        shape.rect(safeArea.x + 7f, safeArea.y + 7f, safeArea.width - 14f, safeArea.height - 14f)
        // 翡翠桌布
        shape.color = Color(0.07f, 0.28f, 0.19f, 1f)
        shape.rect(safeArea.x + 11f, safeArea.y + 11f, safeArea.width - 22f, safeArea.height - 22f)
        // 桌布内圈（更深的中心区，提示发牌位置）
        shape.color = Color(0.05f, 0.22f, 0.15f, 1f)
        val inner = Rectangle(
            safeArea.x + 11f + (safeArea.width - 22f) * 0.16f,
            safeArea.y + 11f + (safeArea.height - 22f) * 0.16f,
            (safeArea.width - 22f) * 0.68f,
            (safeArea.height - 22f) * 0.68f
        )
        shape.rect(inner.x, inner.y, inner.width, inner.height)
    }

    private fun drawTableBorders(shape: ShapeRenderer, safeArea: Rectangle) {
        shape.color = Color(0.86f, 0.72f, 0.36f, 0.95f)
        shape.rect(safeArea.x + 7f, safeArea.y + 7f, safeArea.width - 14f, safeArea.height - 14f)
    }

    private fun drawSeatPlates(shape: ShapeRenderer, view: PlayerView, safeArea: Rectangle) {
        val plates = seatPlates(safeArea)
        for (plate in plates) {
            shape.color = Color(0.04f, 0.10f, 0.07f, 0.88f)
            shape.rect(plate.x, plate.y, plate.width, plate.height)
        }
    }

    /**
     * 四席铭牌位置（查看者视角：自己在左下，其后依次为右下、右上、左）。
     *
     * 左家铭牌放在左侧中部而不是左上角：左上角是牌桌既有的「返回主菜单」热区，
     * 准备阶段不能把信息铭牌压在那个热区上，否则点铭牌会意外退出牌桌。
     */
    fun seatPlates(safeArea: Rectangle): List<Rectangle> = listOf(
        Rectangle(safeArea.x + 18f, safeArea.y + 18f, SEAT_PLATE_WIDTH, SEAT_PLATE_HEIGHT),
        Rectangle(safeArea.x + safeArea.width - SEAT_PLATE_WIDTH - 18f, safeArea.y + 18f, SEAT_PLATE_WIDTH, SEAT_PLATE_HEIGHT),
        Rectangle(safeArea.x + safeArea.width - SEAT_PLATE_WIDTH - 18f, safeArea.y + safeArea.height - SEAT_PLATE_HEIGHT - 18f, SEAT_PLATE_WIDTH, SEAT_PLATE_HEIGHT),
        Rectangle(safeArea.x + 18f, safeArea.y + safeArea.height / 2f - SEAT_PLATE_HEIGHT / 2f, SEAT_PLATE_WIDTH, SEAT_PLATE_HEIGHT)
    )

    /**
     * 中央文字可用区：横向避开左右两侧的席位铭牌。
     *
     * 准备阶段的占位文案（标题与说明）必须落在这个区域内：
     * 抽屉展开时安全桌区只有 664 宽，整宽居中的长句会横压到左家铭牌上。
     */
    fun centralTextRect(safeArea: Rectangle): Rectangle {
        val plates = seatPlates(safeArea)
        val mid = safeArea.x + safeArea.width / 2f
        val leftEdge = plates.filter { it.x + it.width / 2f < mid }.maxOfOrNull { it.x + it.width }
            ?: safeArea.x
        val rightEdge = plates.filter { it.x + it.width / 2f >= mid }.minOfOrNull { it.x }
            ?: (safeArea.x + safeArea.width)
        val left = leftEdge + TEXT_BAND_PADDING
        val right = rightEdge - TEXT_BAND_PADDING
        return Rectangle(
            left,
            safeArea.y,
            (right - left).coerceAtLeast(MIN_TEXT_BAND_WIDTH),
            safeArea.height
        )
    }

    private fun drawTexts(
        batch: SpriteBatch,
        view: PlayerView,
        safeArea: Rectangle,
        projection: Matrix4,
        font: BitmapFont,
        chineseFont: BitmapFont?
    ) {
        val textFont = chineseFont ?: font
        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY
        val origColor = Color(textFont.color)

        try {
            batch.projectionMatrix = projection
            batch.begin()
            try {
                // 局次（顶部居中）
                textFont.data.setScale(1.1f)
                textFont.color = Color(1.0f, 0.85f, 0.35f, 1f)
                val roundInfo = "${windName(view.roundWind)}${view.roundNumber + 1}局  本场:${view.honbaSticks}  供托:${view.riichiSticks}"
                glyphLayout.setText(textFont, roundInfo)
                textFont.draw(
                    batch,
                    roundInfo,
                    safeArea.x + (safeArea.width - glyphLayout.width) / 2f,
                    safeArea.y + safeArea.height - 34f
                )

                // 发牌前不显示任何实体牌，只给出准确占位文案。
                // 占位文案必须落在**中央文字区**（避开左右两侧的席位铭牌），
                // 否则在抽屉展开（安全桌区更窄）或 960×540 时会横压到左家铭牌上。
                val textBand = centralTextRect(safeArea)
                val bandCenterX = textBand.x + textBand.width / 2f
                val bandCenterY = safeArea.y + safeArea.height / 2f

                textFont.data.setScale(TITLE_FONT_SCALE)
                textFont.color = Color(0.86f, 0.92f, 0.86f, 1f)
                glyphLayout.setText(textFont, TITLE_TEXT)
                textFont.draw(
                    batch,
                    TITLE_TEXT,
                    bandCenterX - glyphLayout.width / 2f,
                    bandCenterY + 34f
                )

                textFont.data.setScale(PLACEHOLDER_FONT_SCALE)
                textFont.color = Color(0.72f, 0.80f, 0.74f, 1f)
                for ((index, line) in PLACEHOLDER_LINES.withIndex()) {
                    glyphLayout.setText(textFont, line)
                    textFont.draw(
                        batch,
                        line,
                        bandCenterX - glyphLayout.width / 2f,
                        bandCenterY + 4f - index * 24f
                    )
                }

                // 四席公开信息
                val plates = seatPlates(safeArea)
                for (index in plates.indices) {
                    val seat = (view.viewerSeat + index) % 4
                    val plate = plates[index]
                    val wind = windName(view.seatWind(seat))
                    val score = view.scores.getOrElse(seat) { 25000 }
                    val isDealer = seat == view.dealerSeat
                    val isViewer = seat == view.viewerSeat

                    textFont.data.setScale(0.98f)
                    textFont.color = Color(1.0f, 0.86f, 0.42f, 1f)
                    val head = buildString {
                        append(wind)
                        append("家 P")
                        append(seat)
                        if (isDealer) append(" 庄")
                        if (isViewer) append(" (你)")
                    }
                    textFont.draw(batch, head, plate.x + 12f, plate.y + 38f)

                    textFont.data.setScale(0.88f)
                    textFont.color = Color(0.90f, 0.92f, 0.90f, 1f)
                    textFont.draw(batch, "$score 点", plate.x + 12f, plate.y + 16f)

                    if (view.riichiStatus.getOrElse(seat) { false }) {
                        textFont.color = Color(1.0f, 0.45f, 0.42f, 1f)
                        textFont.draw(batch, "立直", plate.x + plate.width - 48f, plate.y + 16f)
                    }
                }
            } finally {
                if (batch.isDrawing) batch.end()
            }
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
        }
    }

    private fun windName(wind: Int): String = when (wind) {
        1 -> "東"
        2 -> "南"
        3 -> "西"
        4 -> "北"
        else -> "東"
    }
}
