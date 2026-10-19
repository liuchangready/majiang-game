package com.majiang.presentation.common.render

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Matrix4
import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.TileView
import com.majiang.presentation.common.ClockDisplay
import com.majiang.presentation.game.OpponentHandRow

/**
 * 桌面渲染配置与委托谓词选项 (TableBoardRenderOptions)。
 *
 * @property isTileVisibleInRiver 牌河中指定弃牌是否当前可见（用于动画隐藏正在飞行的牌）
 * @property highlightTileId 牌河提示选中高亮的牌物理 ID（如提供则在手牌/牌河/副露对应牌周围绘制青色框）
 * @property clockElapsedMillis 本地单调时钟逝去毫秒数（调用方使用当前时间 - lastClockUpdateLocalMillis 传入）
 */
data class TableBoardRenderOptions(
    val isTileVisibleInRiver: (seat: Int, tile: TileView, index: Int) -> Boolean = { _, _, _ -> true },
    val highlightTileId: Int? = null,
    val clockElapsedMillis: Long = 0L
)

/**
 * 玩家与观战共用的公开桌面无状态渲染器 (TableBoardRenderer)。
 *
 * 核心设计与安全规范：
 * 1. 纯无状态对象，不持有任何摄像机、快照或生命周期状态；
 * 2. 严禁引用私有玩家快照、公开快照或会话等上层视图类型（禁用清单以 TableBoardRenderModelContractTest 源扫描为准）；
 * 3. 严格由调用方传入 projectionMatrix，支持不同视角矩阵（如 GameScreen 的缩放平移 tableCamera 与 SpectatorScreen 的 camera）；
 * 4. 五大渲染段（桌面中心、席位信息、暗手牌/牌背、牌河、副露）统一使用 [TableBoardRenderModel]；
 * 5. 绘制状态（颜色、缩放）在完成后严密恢复，保证前后绘制环境干净隔离。
 */
object TableBoardRenderer {

    /**
     * 计算指定席位的弃牌起点坐标。
     *
     * 保持与 GameScreen.getDiscardStart 相同数值与旋转语义。
     */
    fun computeDiscardStart(seat: Int, viewerSeat: Int): Pair<Float, Float> =
        when ((seat - viewerSeat + 4) % 4) {
            0 -> Pair(640f, 100f)
            1 -> Pair(1180f, 360f)
            2 -> Pair(640f, 620f)
            3 -> Pair(100f, 360f)
            else -> Pair(640f, 100f)
        }

    /**
     * 计算指定席位在指定弃牌槽位索引 (0..23) 的物理坐标 (X, Y)。
     *
     * 保持与 GameScreen.getDiscardSlotPosition 相同数值与旋转语义。
     */
    fun computeDiscardSlotPosition(seat: Int, slotIndex: Int, viewerSeat: Int): Pair<Float, Float> {
        val col = slotIndex % 6
        val row = slotIndex / 6
        val tileW = 38f
        val tileH = 52f

        return when ((seat - viewerSeat + 4) % 4) {
            0 -> Pair(480f + col * (tileW + 2f), 240f - row * (tileH + 4f))
            1 -> Pair(750f + row * (tileH + 4f), 440f - col * (tileW + 2f))
            2 -> Pair(460f + col * (tileW + 2f), 530f - row * (tileH + 4f))
            3 -> Pair(420f - row * (tileH + 4f), 440f - col * (tileW + 2f))
            else -> Pair(480f, 240f)
        }
    }

    /**
     * 1. 桌面中心信息（风局、牌山剩余、宝牌指示牌文案与牌面）。
     */
    fun renderTableCenter(
        model: TableBoardRenderModel,
        projectionMatrix: Matrix4,
        batch: SpriteBatch,
        shape: ShapeRenderer,
        font: BitmapFont,
        chineseFont: BitmapFont?,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer,
        options: TableBoardRenderOptions = TableBoardRenderOptions()
    ) {
        batch.projectionMatrix = projectionMatrix
        batch.begin()
        val textFont = chineseFont ?: font
        val origColor = Color(textFont.color)
        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY

        try {
            textFont.data.setScale(1.1f)
            textFont.color = Color(1.0f, 0.85f, 0.35f, 1f)

            val roundWindName = listOf("東", "南", "西", "北").getOrElse(model.roundWind - 1) { "東" }
            val prefix = if (model.isSpectator) "【观战】 " else ""
            val roundInfo = "$prefix$roundWindName${model.roundNumber + 1}局  本场:${model.honbaSticks}  供托:${model.riichiSticks}"
            textFont.draw(batch, roundInfo, 540f, 400f)

            textFont.color = Color(0.9f, 0.9f, 0.9f, 1f)
            textFont.draw(batch, "牌山剩余: ${model.wallRemaining}", 570f, 370f)

            textFont.color = Color(1.0f, 0.6f, 0.2f, 1f)
            textFont.draw(batch, "宝牌指示牌:", 520f, 340f)
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
        }
        batch.end()

        // 渲染宝牌指示牌
        val doras = model.doraIndicators
        for (i in doras.indices) {
            val tile = doras[i]
            val alpha = if (tile.tileId in model.markedDoraIndicatorIds) 0.55f else 1.0f
            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = tile,
                x = 640f + i * 32f,
                y = 315f,
                width = 28f,
                height = 38f,
                isHover = false,
                isDrawn = false,
                alpha = alpha
            )
        }
        shape.projectionMatrix = projectionMatrix
        batch.projectionMatrix = projectionMatrix
        tileDrawBuffer.flush(batch, shape, font)
    }

    /**
     * 2. 四家席位基本信息（点数、倒计时时钟、立直标记、当前巡高亮）。
     */
    fun renderSeatsInfo(
        model: TableBoardRenderModel,
        projectionMatrix: Matrix4,
        batch: SpriteBatch,
        shape: ShapeRenderer,
        font: BitmapFont,
        chineseFont: BitmapFont?,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer,
        options: TableBoardRenderOptions = TableBoardRenderOptions()
    ) {
        val viewer = model.viewerSeat
        val clocks = model.clocks
        val elapsed = options.clockElapsedMillis

        val origColor = Color(font.color)
        val origScaleX = font.data.scaleX
        val origScaleY = font.data.scaleY

        try {
            for (seat in 0..3) {
                val clock = clocks.firstOrNull { it.seat == seat }
                val formattedClock = ClockDisplay.format(clock, elapsed)
                val score = model.scores.getOrElse(seat) { 25000 }
                val isRiichi = model.riichiStatus.getOrElse(seat) { false }
                val isCurrent = (model.currentTurnPlayer == seat)

                val (x, y) = when ((seat - viewer + 4) % 4) {
                    0 -> Pair(40f, 80f)
                    1 -> Pair(1150f, 400f)
                    2 -> Pair(320f, 675f)
                    3 -> Pair(40f, 400f)
                    else -> Pair(40f, 80f)
                }

                batch.projectionMatrix = projectionMatrix
                batch.begin()
                font.data.setScale(0.95f)
                font.color = if (isCurrent) Color(1.0f, 0.95f, 0.4f, 1f) else Color.WHITE
                val label = if (!model.isSpectator && seat == viewer) "P$seat [YOU]" else "P$seat"
                font.draw(batch, "$label: $score", x, y)
                font.color = if (formattedClock.isUrgent) Color.RED else Color(0.7f, 1.0f, 0.8f, 1f)
                font.draw(batch, formattedClock.displayText, x, y - 18f)

                if (isRiichi) {
                    font.color = Color(1.0f, 0.4f, 0.4f, 1f)
                    font.draw(batch, "[RIICHI]", x, y - 36f)
                }
                batch.end()
            }
        } finally {
            font.data.setScale(origScaleX, origScaleY)
            font.color = origColor
        }
    }

    /**
     * 3. 手牌暗区与对手手牌行（含对手暗手牌普通牌背、落汗有效翻面半透明牌面，以及观战视角的底端手牌背）。
     */
    fun renderHandAreas(
        model: TableBoardRenderModel,
        projectionMatrix: Matrix4,
        batch: SpriteBatch,
        shape: ShapeRenderer,
        font: BitmapFont,
        chineseFont: BitmapFont?,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer,
        options: TableBoardRenderOptions = TableBoardRenderOptions()
    ) {
        val viewer = model.viewerSeat
        val selected = options.highlightTileId
        val highlighted = mutableListOf<Rectangle>()

        for (row in model.opponentHandRows) {
            val seat = row.seat
            when ((seat - viewer + 4) % 4) {
                1 -> renderRightOpponentHand(row, selected, highlighted, tileSkin, tileDrawBuffer)
                2 -> renderTopOpponentHand(row, selected, highlighted, tileSkin, tileDrawBuffer)
                3 -> renderLeftOpponentHand(row, selected, highlighted, tileSkin, tileDrawBuffer)
            }
        }

        // 观战视角底端席位手牌背行 (viewerHandBackCount > 0 时渲染)
        if (model.viewerHandBackCount > 0) {
            val startX = 260f
            val y = 40f
            val tileW = 44f
            val tileH = 62f
            for (i in 0 until model.viewerHandBackCount) {
                tileSkin.appendTileBackDraw(
                    buf = tileDrawBuffer,
                    x = startX + i * 48f,
                    y = y,
                    width = tileW,
                    height = tileH
                )
            }
        }

        shape.projectionMatrix = projectionMatrix
        batch.projectionMatrix = projectionMatrix
        tileDrawBuffer.flush(batch, shape, font)

        if (highlighted.isNotEmpty()) {
            shape.projectionMatrix = projectionMatrix
            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.20f, 1.0f, 0.85f, 1.0f)
            for (r in highlighted) {
                shape.rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f)
            }
            shape.end()
        }
    }

    private fun renderTopOpponentHand(
        row: OpponentHandRow,
        selectedId: Int?,
        highlighted: MutableList<Rectangle>,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer
    ) {
        val count = row.slots.size
        if (count == 0) return
        val tileW = 28f
        val tileH = 38f
        val stepX = 30f
        val totalW = (count - 1) * stepX + tileW
        val startX = 640f - totalW / 2f
        val y = 665f

        for (i in row.slots.indices) {
            val slot = row.slots[i]
            val x = startX + i * stepX
            if (slot.tile == null) {
                tileSkin.appendTileBackDraw(tileDrawBuffer, x, y, tileW, tileH)
            } else {
                tileSkin.appendTileDraw(
                    buf = tileDrawBuffer,
                    tile = slot.tile,
                    x = x,
                    y = y,
                    width = tileW,
                    height = tileH,
                    isHover = false,
                    isDrawn = false,
                    alpha = 0.55f
                )
                if (selectedId != null && slot.tile.tileId == selectedId) {
                    highlighted.add(Rectangle(x, y, tileW, tileH))
                }
            }
        }
    }

    private fun renderLeftOpponentHand(
        row: OpponentHandRow,
        selectedId: Int?,
        highlighted: MutableList<Rectangle>,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer
    ) {
        val count = row.slots.size
        if (count == 0) return
        val tileW = 34f
        val tileH = 24f
        val stepY = 26f
        val totalH = (count - 1) * stepY + tileH
        val startY = 350f - totalH / 2f
        val x = 245f

        for (i in row.slots.indices) {
            val slot = row.slots[i]
            val y = startY + (count - 1 - i) * stepY
            if (slot.tile == null) {
                tileSkin.appendTileBackDraw(tileDrawBuffer, x, y, tileW, tileH)
            } else {
                tileSkin.appendRotatedTileDraw(
                    buf = tileDrawBuffer,
                    tile = slot.tile,
                    x = x,
                    y = y,
                    width = tileW,
                    height = tileH,
                    isHover = false,
                    isDrawn = false,
                    alpha = 0.55f,
                    rotationDeg = -90f
                )
                if (selectedId != null && slot.tile.tileId == selectedId) {
                    highlighted.add(Rectangle(x, y, tileW, tileH))
                }
            }
        }
    }

    private fun renderRightOpponentHand(
        row: OpponentHandRow,
        selectedId: Int?,
        highlighted: MutableList<Rectangle>,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer
    ) {
        val count = row.slots.size
        if (count == 0) return
        val tileW = 34f
        val tileH = 24f
        val stepY = 26f
        val totalH = (count - 1) * stepY + tileH
        val startY = 350f - totalH / 2f
        val x = 990f

        for (i in row.slots.indices) {
            val slot = row.slots[i]
            val y = startY + i * stepY
            if (slot.tile == null) {
                tileSkin.appendTileBackDraw(tileDrawBuffer, x, y, tileW, tileH)
            } else {
                tileSkin.appendRotatedTileDraw(
                    buf = tileDrawBuffer,
                    tile = slot.tile,
                    x = x,
                    y = y,
                    width = tileW,
                    height = tileH,
                    isHover = false,
                    isDrawn = false,
                    alpha = 0.55f,
                    rotationDeg = 90f
                )
                if (selectedId != null && slot.tile.tileId == selectedId) {
                    highlighted.add(Rectangle(x, y, tileW, tileH))
                }
            }
        }
    }

    /**
     * 4. 四家弃牌河（含可见性过滤、落汗 0.55 半透明以及牌河提示高亮描边框）。
     */
    fun renderDiscards(
        model: TableBoardRenderModel,
        projectionMatrix: Matrix4,
        batch: SpriteBatch,
        shape: ShapeRenderer,
        font: BitmapFont,
        chineseFont: BitmapFont?,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer,
        options: TableBoardRenderOptions = TableBoardRenderOptions()
    ) {
        val viewer = model.viewerSeat
        val selected = options.highlightTileId
        val highlightedPositions = mutableListOf<Pair<Float, Float>>()

        for (seat in 0..3) {
            val discards = model.discards.getOrElse(seat) { emptyList() }
            for (i in discards.indices) {
                val tile = discards[i]

                if (!options.isTileVisibleInRiver(seat, tile, i)) continue

                val pos = computeDiscardSlotPosition(seat, i, viewer)
                val isMarked = RiverMarkedTile(seat, i, tile.tileId) in model.riverMarkedTileKeys
                val alpha = if (isMarked) 0.55f else 1.0f

                if (selected != null && tile.tileId == selected) {
                    highlightedPositions.add(pos)
                }

                tileSkin.appendTileDraw(
                    buf = tileDrawBuffer,
                    tile = tile,
                    x = pos.first,
                    y = pos.second,
                    width = 38f,
                    height = 52f,
                    isHover = false,
                    isDrawn = false,
                    alpha = alpha
                )
            }
        }
        shape.projectionMatrix = projectionMatrix
        batch.projectionMatrix = projectionMatrix
        tileDrawBuffer.flush(batch, shape, font)

        if (highlightedPositions.isNotEmpty()) {
            shape.projectionMatrix = projectionMatrix
            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.20f, 1.0f, 0.85f, 1.0f)
            for (pos in highlightedPositions) {
                shape.rect(pos.first - 2f, pos.second - 2f, 42f, 56f)
            }
            shape.end()
        }
    }

    /**
     * 5. 四家已完成副露组（吃、碰、明暗杠、加杠牌面及落汗副露标记与选中高亮）。
     */
    fun renderMelds(
        model: TableBoardRenderModel,
        projectionMatrix: Matrix4,
        batch: SpriteBatch,
        shape: ShapeRenderer,
        font: BitmapFont,
        chineseFont: BitmapFont?,
        tileSkin: TileSkin,
        tileDrawBuffer: TileDrawBuffer,
        options: TableBoardRenderOptions = TableBoardRenderOptions()
    ) {
        val groups = MeldDisplayLayout.layoutAll(model.melds, model.viewerSeat)
        if (groups.isEmpty()) return

        MeldDisplayRenderer.append(tileDrawBuffer, tileSkin, groups, markedTileIds = model.markedMeldTileIds)
        shape.projectionMatrix = projectionMatrix
        batch.projectionMatrix = projectionMatrix
        tileDrawBuffer.flush(batch, shape, font)

        val selected = options.highlightTileId
        if (selected != null) {
            shape.projectionMatrix = projectionMatrix
            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.20f, 1.0f, 0.85f, 1.0f)
            for (group in groups) {
                for (dt in group.tiles) {
                    if (dt.tile.tileId == selected) {
                        shape.rect(dt.x - 2f, dt.y - 2f, dt.width + 4f, dt.height + 4f)
                    }
                }
            }
            shape.end()
        }
    }
}
