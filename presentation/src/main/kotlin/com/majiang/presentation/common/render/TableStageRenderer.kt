package com.majiang.presentation.common.render

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Matrix4
import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.ClockInfo
import com.majiang.presentation.common.ClockDisplay

/**
 * 持续牌桌场景渲染器 (TableStageRenderer)。
 *
 * 坐标契约：
 * - Z0 环境按 screen 空间 cover 整个窗口，只读、不接输入；
 * - Z10 桌面、Z20 罗盘、Z30 四席公开 HUD / 托盘、Z40 准备栏共用 1280×720 stage，
 *   由 TableStageViewport 统一 fit 与 scissor；
 * - 这里不读取窗口尺寸、不扩大 worldW/worldH，也不加载整张 ASSEMBLY 效果图。
 */
object TableStageRenderer {

    private val INK = Color(0.973f, 0.902f, 0.667f, 1f)
    private val GOLD = Color(0.906f, 0.725f, 0.325f, 1f)
    private val MUTED = Color(0.827f, 0.788f, 0.659f, 1f)
    private val JADE = Color(0.584f, 0.871f, 0.651f, 1f)
    private val DANGER = Color(1.0f, 0.608f, 0.514f, 1f)

    /**
     * Z0。环境 cover 到整个窗口；这是唯一用 window 空间绘制的牌桌图层。
     * 宽屏/4:3 多出来的区域只显示环境，不参与任何按钮命中。
     */
    fun renderEnvironment(
        visuals: TableVisualAssets,
        viewport: TableStageViewport,
        batch: SpriteBatch
    ) {
        if (!viewport.drawable) return
        val priorColor = Color(batch.color)
        val priorProjection = Matrix4(batch.projectionMatrix)
        val priorTransform = Matrix4(batch.transformMatrix)
        try {
            batch.projectionMatrix = Matrix4().setToOrtho2D(
                0f,
                0f,
                viewport.windowWidth.toFloat(),
                viewport.windowHeight.toFloat()
            )
            batch.transformMatrix.idt()
            batch.begin()
            batch.color = Color.WHITE
            val envW = visuals.environment.regionWidth.toFloat()
            val envH = visuals.environment.regionHeight.toFloat()
            if (envW > 0f && envH > 0f) {
                val cover = maxOf(
                    viewport.windowWidth / envW,
                    viewport.windowHeight / envH
                )
                val width = envW * cover
                val height = envH * cover
                batch.draw(
                    visuals.environment,
                    (viewport.windowWidth - width) / 2f,
                    (viewport.windowHeight - height) / 2f,
                    width,
                    height
                )
            }
        } finally {
            if (batch.isDrawing) batch.end()
            batch.color = priorColor
            batch.projectionMatrix = priorProjection
            batch.transformMatrix = priorTransform
        }
    }

    /** Z10。固定桌面，不随准备栏、抽屉或 safeTableRect 缩放。 */
    fun renderBoard(
        visuals: TableVisualAssets,
        projection: Matrix4,
        batch: SpriteBatch
    ) {
        val priorColor = Color(batch.color)
        val priorProjection = Matrix4(batch.projectionMatrix)
        try {
            batch.projectionMatrix = projection
            batch.begin()
            batch.color = Color.WHITE
            val board = TableStageLayout.BOARD
            batch.draw(visuals.board, board.x, board.y, board.width, board.height)
        } finally {
            if (batch.isDrawing) batch.end()
            batch.color = priorColor
            batch.projectionMatrix = priorProjection
        }
    }

    /**
     * Z20 罗盘。四向风位来自公开风局；准备阶段在中心显示局次与准备态。
     * 发牌后的中心数据由 [TableBoardRenderer.renderTableCenter] 在同一罗盘内绘制。
     */
    fun renderCompass(
        visuals: TableVisualAssets,
        rect: Rectangle,
        view: TableHudView,
        isPreparation: Boolean,
        projection: Matrix4,
        batch: SpriteBatch,
        fonts: TableStageFonts
    ) {
        val priorColor = Color(batch.color)
        val priorProjection = Matrix4(batch.projectionMatrix)
        try {
            batch.projectionMatrix = projection
            batch.begin()
            batch.color = Color.WHITE
            batch.draw(visuals.compass, rect.x, rect.y, rect.width, rect.height)

            val centers = if (isPreparation) {
                listOf(
                    TableStageLayout.SeatSlot.TOP to floatArrayOf(0.50f, 0.175f),
                    TableStageLayout.SeatSlot.RIGHT to floatArrayOf(0.84f, 0.48f),
                    TableStageLayout.SeatSlot.SELF to floatArrayOf(0.50f, 0.805f),
                    TableStageLayout.SeatSlot.LEFT to floatArrayOf(0.16f, 0.48f)
                )
            } else {
                listOf(
                    TableStageLayout.SeatSlot.TOP to floatArrayOf(0.50f, 0.175f),
                    TableStageLayout.SeatSlot.RIGHT to floatArrayOf(0.84f, 0.48f),
                    TableStageLayout.SeatSlot.LEFT to floatArrayOf(0.16f, 0.48f)
                )
            }
            for ((slot, center) in centers) {
                val seat = TableStageLayout.absoluteSeat(view.viewerSeat, slot)
                val text = windName(view.seatWind(seat))
                val cx = rect.x + rect.width * center[0]
                val top = rect.y + rect.height * (1f - center[1]) + fonts.body.height(text) / 2f
                fonts.body.drawCentered(batch, text, cx, top, INK, shadow = true, outline = true)
            }

            if (isPreparation) {
                val cx = rect.x + rect.width / 2f
                val roundText = "${windName(view.roundWind)}${view.roundNumber + 1}局"
                fonts.primary.drawCentered(
                    batch,
                    roundText,
                    cx,
                    rect.y + rect.height * 0.61f,
                    GOLD,
                    embolden = true,
                    outline = true
                )
                val state = if (view.phase == com.majiang.client.api.GamePhaseView.STACKING) {
                    "准备开局"
                } else {
                    "准备中"
                }
                fonts.body.drawCentered(
                    batch,
                    state,
                    cx,
                    rect.y + rect.height * 0.465f,
                    JADE,
                    shadow = true
                )
                val extra = "本${view.honbaSticks}  供${view.riichiSticks}"
                fonts.small.drawCentered(
                    batch,
                    extra,
                    cx,
                    rect.y + rect.height * 0.365f,
                    MUTED,
                    shadow = true
                )
            }
        } finally {
            if (batch.isDrawing) batch.end()
            batch.color = priorColor
            batch.projectionMatrix = priorProjection
        }
    }

    /**
     * Z30 四席公开头像与独立名牌。名称固定为 P0..P3 + 风位，不伪造昵称/动物身份；
     * 点数、庄家、立直和时钟均来自公开快照。17/21px 原生字号不再压缩到 72px。
     */
    fun renderHud(
        visuals: TableVisualAssets,
        isPreparation: Boolean,
        drawerProgress: Float,
        view: TableHudView,
        clocks: List<ClockInfo>,
        clockElapsedMillis: Long,
        projection: Matrix4,
        batch: SpriteBatch,
        shape: ShapeRenderer,
        fonts: TableStageFonts
    ) {
        val priorColor = Color(batch.color)
        val priorProjection = Matrix4(batch.projectionMatrix)
        val highlights = ArrayList<Rectangle>(4)
        try {
            batch.projectionMatrix = projection
            batch.begin()
            for (slot in TableStageLayout.SeatSlot.entries) {
                val hud = when {
                    slot == TableStageLayout.SeatSlot.RIGHT && isPreparation -> {
                        TableStageLayout.drawerRightHud(drawerProgress)
                    }
                    isPreparation -> TableStageLayout.preparationHud(slot)
                    else -> TableStageLayout.playingHud(slot)
                }
                val absoluteSeat = TableStageLayout.absoluteSeat(view.viewerSeat, slot)
                val portrait = visuals.portraitsByAbsoluteSeat[absoluteSeat] ?: continue

                batch.color = Color.WHITE
                batch.draw(portrait, hud.portrait.x, hud.portrait.y, hud.portrait.width, hud.portrait.height)

                batch.color = Color(1f, 1f, 1f, 0.98f)
                visuals.panelPlate.draw(batch, hud.plate.x, hud.plate.y, hud.plate.width, hud.plate.height)

                val seat = absoluteSeat
                val wind = windName(view.seatWind(seat))
                val isDealer = seat == view.dealerSeat
                val isRiichi = view.riichiStatus.getOrElse(seat) { false }
                val clock = clocks.firstOrNull { it.seat == seat }
                val formattedClock = ClockDisplay.format(clock, clockElapsedMillis)
                val clockText = if (clock != null) formattedClock.displayText else null
                val name = "P$seat · $wind"
                val marker = when {
                    isDealer && isRiichi -> "庄/立"
                    isRiichi -> "立直"
                    isDealer -> "庄"
                    else -> ""
                }
                val markerColor = when {
                    isRiichi -> DANGER
                    isDealer -> GOLD
                    else -> MUTED
                }

                val left = hud.plate.x + 8f
                val right = hud.plate.x + hud.plate.width - 6f
                val nameTop = hud.plate.y + hud.plate.height - 5f
                val scoreTop = hud.plate.y + 23f

                // 名称最高优先级；仅在极端情况下做 0.92 倍收窄，绝不改变字号档位。
                val nameWidth = hud.plate.width - 54f
                val nameScale = fitScale(fonts.name, name, nameWidth, 1f)
                fonts.name.draw(
                    batch,
                    name,
                    left,
                    nameTop,
                    INK,
                    relativeScale = nameScale,
                    outline = true
                )

                if (marker.isNotEmpty()) {
                    fonts.small.drawRight(
                        batch,
                        marker,
                        right,
                        nameTop + 1f,
                        markerColor,
                        shadow = true
                    )
                }

                fonts.card.draw(
                    batch,
                    "${view.scores.getOrElse(seat) { 25000 }}",
                    left,
                    scoreTop,
                    Color.WHITE,
                    shadow = true
                )

                if (clockText != null) {
                    fonts.small.drawRight(
                        batch,
                        clockText,
                        right,
                        scoreTop + 1f,
                        if (formattedClock.isUrgent) DANGER else INK,
                        shadow = true
                    )
                }

                if (view.currentTurnPlayer == seat) {
                    highlights += Rectangle(hud.portrait)
                }
            }
        } finally {
            if (batch.isDrawing) batch.end()
            batch.color = priorColor
            batch.projectionMatrix = priorProjection
        }

        if (highlights.isNotEmpty()) {
            val priorShapeProjection = Matrix4(shape.projectionMatrix)
            try {
                shape.projectionMatrix = projection
                shape.begin(ShapeRenderer.ShapeType.Line)
                shape.color = GOLD
                for (rect in highlights) {
                    // 圆形金环，绝不用矩形描边（会被误读为头像外白方框）。
                    val cx = rect.x + rect.width / 2f
                    val cy = rect.y + rect.height / 2f
                    val radius = rect.width / 2f + 2f
                    shape.circle(cx, cy, radius, 64)
                    shape.circle(cx, cy, radius + 2f, 64)
                }
                shape.end()
            } finally {
                shape.projectionMatrix = priorShapeProjection
            }
        }
    }

    /** 发牌后本人手牌托盘与对家手牌托盘（纯装饰，位于真实牌层之下）。 */
    fun renderPlayingTrays(
        visuals: TableVisualAssets,
        projection: Matrix4,
        batch: SpriteBatch
    ) {
        val priorColor = Color(batch.color)
        val priorProjection = Matrix4(batch.projectionMatrix)
        try {
            batch.projectionMatrix = projection
            batch.begin()
            batch.color = Color(1f, 1f, 1f, 0.92f)
            val hand = TableStageLayout.HAND_TRAY
            visuals.panelHint.draw(batch, hand.x, hand.y, hand.width, hand.height)
            batch.color = Color(1f, 1f, 1f, 0.78f)
            val top = TableStageLayout.TOP_TRAY
            visuals.panelHint.draw(batch, top.x, top.y, top.width, top.height)
        } finally {
            if (batch.isDrawing) batch.end()
            batch.color = priorColor
            batch.projectionMatrix = priorProjection
        }
    }

    /** 准备阶段底部短提示面板。 */
    fun renderPreparationHint(
        visuals: TableVisualAssets,
        projection: Matrix4,
        batch: SpriteBatch,
        fonts: TableStageFonts
    ) {
        val priorColor = Color(batch.color)
        val priorProjection = Matrix4(batch.projectionMatrix)
        try {
            batch.projectionMatrix = projection
            batch.begin()
            batch.color = Color.WHITE
            val hint = TableStageLayout.PREP_HINT
            visuals.panelHint.draw(batch, hint.x, hint.y, hint.width, hint.height)
            val text = "◆ 准备完成后发牌 ◆"
            fonts.body.drawCentered(
                batch,
                text,
                hint.x + hint.width / 2f,
                hint.y + (hint.height + fonts.body.height(text)) / 2f,
                INK,
                outline = true
            )
        } finally {
            if (batch.isDrawing) batch.end()
            batch.color = priorColor
            batch.projectionMatrix = priorProjection
        }
    }

    /** 右下操作底板，保留既有按钮坐标、权限、禁用态与命中区。 */
    fun renderActionPlate(
        visuals: TableVisualAssets,
        projection: Matrix4,
        batch: SpriteBatch
    ) {
        val priorColor = Color(batch.color)
        val priorProjection = Matrix4(batch.projectionMatrix)
        try {
            batch.projectionMatrix = projection
            batch.begin()
            batch.color = Color(1f, 1f, 1f, 0.82f)
            val plate = TableStageLayout.ACTION_PLATE
            visuals.panelHint.draw(batch, plate.x, plate.y, plate.width, plate.height)
        } finally {
            if (batch.isDrawing) batch.end()
            batch.color = priorColor
            batch.projectionMatrix = priorProjection
        }
    }

    /** 资源加载失败时的轻量兜底；正常截图绝不走此路径。 */
    fun renderFallback(
        visible: Rectangle,
        projection: Matrix4,
        shape: ShapeRenderer
    ) {
        val priorProjection = Matrix4(shape.projectionMatrix)
        try {
            shape.projectionMatrix = projection
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.035f, 0.075f, 0.06f, 1f)
            shape.rect(visible.x, visible.y, visible.width, visible.height)
            shape.color = Color(0.11f, 0.22f, 0.16f, 1f)
            val board = TableStageLayout.BOARD
            shape.rect(board.x, board.y, board.width, board.height)
            shape.color = Color(0.36f, 0.14f, 0.10f, 1f)
            shape.rect(board.x + 7f, board.y + 7f, board.width - 14f, board.height - 14f)
            shape.color = Color(0.06f, 0.26f, 0.18f, 1f)
            shape.rect(board.x + 16f, board.y + 16f, board.width - 32f, board.height - 32f)
            shape.end()
        } finally {
            shape.projectionMatrix = priorProjection
        }
    }

    /** 将文字宽度限制在盒内，仅轻微收窄，不跨字号档位。 */
    private fun fitScale(font: StageFont, text: String, maxWidth: Float, start: Float): Float {
        var scale = start
        while (scale > 0.72f && font.width(text, scale) > maxWidth) {
            scale -= 0.04f
        }
        return scale.coerceAtLeast(0.72f)
    }

    private fun windName(wind: Int): String = when (wind) {
        1 -> "東"
        2 -> "南"
        3 -> "西"
        4 -> "北"
        else -> "東"
    }
}
