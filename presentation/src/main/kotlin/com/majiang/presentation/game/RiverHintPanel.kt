package com.majiang.presentation.game

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.TileMarkArea
import com.majiang.client.api.TileView
import com.majiang.presentation.common.render.TableStageFonts
import com.majiang.presentation.common.render.TableVisualAssets
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin

/**
 * 牌河提示交互面板 (RiverHintPanel)。
 *
 * 核心契约：
 * 1. 独立表现层组件，仅依赖 client.api；
 * 2. 默认关闭，提供只读 [btnToggleRect]，点击或按 Esc 切换开关；
 * 3. 顶部仅显示权威快照 wallRemaining 剩余可摸总数；
 * 4. 东南西北四行完整绘制暗手牌背面，仅本人有效落汗牌半透明翻面；
 * 5. 下部提供四家当前牌河小型网格与可滚动浏览，支持点击落汗实体高亮联动定位；
 * 6. 输入完全自闭环，不产生穿透，不发起游戏动作，不暂停计时；
 * 7. 选牌或结算时自动收起。
 */
class RiverHintPanel : Disposable {

    /** 切换开关按钮矩形包围盒：设计 `(1098,18,154,48)`，左下角 y=654。 */
    val btnToggleRect: Rectangle = Rectangle(1098f, 654f, 154f, 48f)

    /** 侧边面板整体几何包围盒 */
    val panelRect: Rectangle = Rectangle(860f, 10f, 410f, 700f)

    /** 借用 UiAssets 根持有的牌桌纹理与字号；Screen 不转移所有权。 */
    var visualAssets: TableVisualAssets? = null
    var stageFonts: TableStageFonts? = null

    /** 面板当前开关状态 */
    var isOpen: Boolean = false
        private set

    /** 当前选中的落汗实体牌全局 ID (0..135)，用于跨桌面与面板实体高亮联动 */
    var selectedTileId: Int? = null
        private set

    /** 牌河网格垂直滚动偏移 */
    var scrollOffsetY: Float = 0f
        private set

    private val markedSlotClickTargets = mutableListOf<Pair<Rectangle, Int>>()
    private val riverTileClickTargets = mutableListOf<Pair<Rectangle, Int>>()

    private var isDragging: Boolean = false
    private var dragStartY: Float = 0f
    private var dragStartScrollOffset: Float = 0f
    private var pendingTileId: Int? = null
    private var prevSelectedTileId: Int? = null
    private val DRAG_THRESHOLD = 8f
    private var currentSnapshot: GameSnapshotView? = null

    var isDisposed: Boolean = false
        private set

    fun open() {
        isOpen = true
    }

    fun close() {
        isOpen = false
        isDragging = false
        pendingTileId = null
    }

    fun toggle() {
        if (isOpen) close() else open()
    }

    fun contains(tx: Float, ty: Float): Boolean =
        btnToggleRect.contains(tx, ty) || (isOpen && panelRect.contains(tx, ty))

    private var lastSessionId: String? = null
    private var lastRoundId: String? = null

    fun clearSelection() {
        selectedTileId = null
    }

    fun calculateContentHeight(snapshot: GameSnapshotView?): Float {
        if (snapshot == null) return 0f
        val dealer = snapshot.view.dealerSeat
        var height = 0f
        val riverStepY = 29f
        for (windIdx in 0..3) {
            val seat = (dealer + windIdx) % 4
            val discards = snapshot.view.discards.getOrNull(seat) ?: emptyList()
            val rowCount = if (discards.isEmpty()) 0 else (discards.size - 1) / 6 + 1
            height += 18f + rowCount * riverStepY + 12f
        }
        return height
    }

    fun calculateMaxScroll(snapshot: GameSnapshotView?): Float {
        val handsBaseY = panelRect.y + panelRect.height - 95f
        val rowHeight = 44f
        val riverAreaTop = handsBaseY - 4 * rowHeight - 8f
        val riverContentTop = riverAreaTop - 36f
        val riverContentBottom = panelRect.y + 10f
        val viewportHeight = riverContentTop - riverContentBottom

        val contentHeight = calculateContentHeight(snapshot)
        return (contentHeight - viewportHeight + 40f).coerceAtLeast(0f)
    }

    fun update(snapshot: GameSnapshotView?) {
        currentSnapshot = snapshot
        if (snapshot == null) {
            selectedTileId = null
            lastSessionId = null
            lastRoundId = null
            scrollOffsetY = 0f
            isDragging = false
            pendingTileId = null
            return
        }

        if ((lastSessionId != null && snapshot.sessionId != lastSessionId) ||
            (lastRoundId != null && snapshot.roundId != lastRoundId)
        ) {
            selectedTileId = null
            scrollOffsetY = 0f
            isDragging = false
            pendingTileId = null
        }
        lastSessionId = snapshot.sessionId
        lastRoundId = snapshot.roundId

        val currentSelected = selectedTileId ?: return
        val inDiscards = snapshot.view.discards.any { list -> list.any { it.tileId == currentSelected } }
        val inMelds = snapshot.view.melds.any { list -> list.any { meld -> meld.tiles.any { it.tileId == currentSelected } } }
        val inMarkedVisible = snapshot.tileMark?.activeGroups?.flatMap { it.tiles }?.any {
            it.tileId == currentSelected && it.area != TileMarkArea.NOT_VISIBLE
        } == true
        val inDoras = snapshot.view.doraIndicators.any { it.tileId == currentSelected }

        if (!inDiscards && !inMelds && !inMarkedVisible && !inDoras) {
            selectedTileId = null
        }
    }

    fun touchDown(tx: Float, ty: Float): Boolean {
        if (btnToggleRect.contains(tx, ty)) {
            toggle()
            return true
        }
        if (!isOpen || !panelRect.contains(tx, ty)) {
            return false
        }

        val handsBaseY = panelRect.y + panelRect.height - 95f
        val rowHeight = 44f
        val riverAreaTop = handsBaseY - 4 * rowHeight - 8f
        val isInsideRiverGrid = (ty < riverAreaTop - 15f && ty >= panelRect.y + 5f)

        if (isInsideRiverGrid) {
            isDragging = true
            dragStartY = ty
            dragStartScrollOffset = scrollOffsetY
            prevSelectedTileId = selectedTileId

            var clicked = false
            for (target in riverTileClickTargets) {
                if (target.first.contains(tx, ty)) {
                    selectedTileId = if (selectedTileId == target.second) null else target.second
                    pendingTileId = target.second
                    clicked = true
                    break
                }
            }
            if (!clicked) {
                pendingTileId = -1
            }
            return true
        }

        isDragging = false
        pendingTileId = null

        // 1. 点击手牌行中的落汗实体
        for (target in markedSlotClickTargets) {
            if (target.first.contains(tx, ty)) {
                selectedTileId = if (selectedTileId == target.second) null else target.second
                return true
            }
        }

        // 消费面板内部的所有点击，防止穿透
        return true
    }

    fun touchDragged(tx: Float, ty: Float): Boolean {
        if (!isOpen || !isDragging) return false

        val deltaY = ty - dragStartY
        if (Math.abs(deltaY) > DRAG_THRESHOLD) {
            if (pendingTileId != null) {
                selectedTileId = prevSelectedTileId
                pendingTileId = null
            }
            val maxScroll = calculateMaxScroll(currentSnapshot)
            scrollOffsetY = (dragStartScrollOffset + deltaY).coerceIn(0f, maxScroll)
        }
        return true
    }

    fun touchUp(tx: Float, ty: Float): Boolean {
        if (!isOpen) return false
        if (isDragging) {
            if (pendingTileId == -1) {
                selectedTileId = null
            }
            isDragging = false
            pendingTileId = null
            return true
        }
        return false
    }

    fun scrolled(amountX: Float, amountY: Float): Boolean {
        if (!isOpen) return false
        val maxScroll = calculateMaxScroll(currentSnapshot)
        scrollOffsetY = (scrollOffsetY + amountY * 24f).coerceIn(0f, maxScroll)
        return true
    }

    /**
     * 绘制切换按钮（无论面板展开与否均可绘制）。
     */
    fun renderToggleButton(
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont
    ) {
        val patch = visualAssets?.buttonSecondary
        val label = if (isOpen) "关闭提示" else "牌河提示"
        if (patch != null) {
            batch.begin()
            batch.color = if (isOpen) Color(0.92f, 1f, 0.94f, 1f) else Color.WHITE
            patch.draw(batch, btnToggleRect.x, btnToggleRect.y, btnToggleRect.width, btnToggleRect.height)
            batch.color = Color.WHITE
            batch.end()
        } else {
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.05f, 0.16f, 0.12f, 0.96f)
            shape.rect(btnToggleRect.x, btnToggleRect.y, btnToggleRect.width, btnToggleRect.height)
            shape.end()
            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.91f, 0.73f, 0.33f, 0.9f)
            shape.rect(btnToggleRect.x, btnToggleRect.y, btnToggleRect.width, btnToggleRect.height)
            shape.end()
        }

        val stage = stageFonts?.small
        if (stage != null) {
            batch.begin()
            stage.drawCentered(
                batch,
                label,
                btnToggleRect.x + btnToggleRect.width / 2f,
                btnToggleRect.y + 31f,
                if (isOpen) Color(0.72f, 0.95f, 0.80f, 1f) else Color(0.97f, 0.90f, 0.67f, 1f)
            )
            batch.end()
        } else {
            batch.begin()
            val origScaleX = font.data.scaleX
            val origScaleY = font.data.scaleY
            font.data.setScale(1.0f)
            font.color = Color.WHITE
            font.draw(batch, label, btnToggleRect.x + 28f, btnToggleRect.y + 30f)
            font.data.setScale(origScaleX, origScaleY)
            batch.end()
        }
    }

    /**
     * 绘制展开状态下的牌河提示侧面板。
     */
    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont,
        snapshot: GameSnapshotView?,
        tileSkin: TileSkin?,
        tileDrawBuffer: TileDrawBuffer?,
        isTileVisibleInRiver: (seat: Int, tile: TileView, index: Int) -> Boolean = { _, _, _ -> true }
    ) {
        if (!isOpen || snapshot == null) return
        currentSnapshot = snapshot

        markedSlotClickTargets.clear()
        riverTileClickTargets.clear()

        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined

        // 1. 面板主体使用同一套翡翠锦纹九宫格，绝不画旧版蓝灰矩形。
        val panelPatch = visualAssets?.panelMain
        if (panelPatch != null) {
            batch.begin()
            batch.color = Color(1f, 1f, 1f, 0.98f)
            panelPatch.draw(batch, panelRect.x, panelRect.y, panelRect.width, panelRect.height)
            batch.color = Color.WHITE
            batch.end()
        } else {
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.04f, 0.10f, 0.07f, 0.96f)
            shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)
            shape.end()
            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.91f, 0.73f, 0.33f, 0.88f)
            shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)
            shape.end()
        }

        val hint = RiverHintPresentation.build(snapshot)

        // 2. 顶部牌山剩余可摸总数
        val remainingText = hint.wallRemaining?.toString() ?: "—"
        val titleText = "牌山剩余: $remainingText 张"
        val stageSmall = stageFonts?.small
        val origScaleX = font.data.scaleX
        val origScaleY = font.data.scaleY
        if (stageSmall != null) {
            batch.begin()
            stageSmall.draw(
                batch,
                titleText,
                panelRect.x + 20f,
                panelRect.y + panelRect.height - 18f,
                Color(1.0f, 0.88f, 0.45f, 1.0f),
                embolden = true
            )
            stageSmall.draw(
                batch,
                "四方暗手牌",
                panelRect.x + 20f,
                panelRect.y + panelRect.height - 46f,
                Color(0.86f, 0.95f, 0.83f, 0.96f)
            )
            batch.end()
        } else {
            batch.begin()
            font.data.setScale(1.15f)
            font.color = Color(1.0f, 0.88f, 0.45f, 1.0f)
            font.draw(batch, titleText, panelRect.x + 20f, panelRect.y + panelRect.height - 24f)
            font.data.setScale(0.95f)
            font.color = Color(0.75f, 0.90f, 0.78f, 0.9f)
            font.draw(batch, "四方暗手牌", panelRect.x + 20f, panelRect.y + panelRect.height - 52f)
            font.data.setScale(origScaleX, origScaleY)
            batch.end()
        }

        // 3. 东南西北四行暗手牌展示
        val windNames = listOf("東", "南", "西", "北")
        val handsBaseY = panelRect.y + panelRect.height - 95f
        val rowHeight = 44f
        val tileW = 20f
        val tileH = 28f
        val stepX = 22f

        tileDrawBuffer?.clear()

        for (windIdx in 0..3) {
            val row = hint.hands.getOrNull(windIdx) ?: continue
            val rowY = handsBaseY - windIdx * rowHeight
            val isViewer = (row.seat == snapshot.viewerSeat)
            val windTitle = if (isViewer) "${windNames[windIdx]} •" else windNames[windIdx]

            batch.begin()
            font.data.setScale(0.95f)
            font.color = if (isViewer) Color(1.0f, 0.90f, 0.35f, 1f) else Color.WHITE
            font.draw(batch, windTitle, panelRect.x + 16f, rowY + 22f)
            font.data.setScale(origScaleX, origScaleY)
            batch.end()

            val slotsStartX = panelRect.x + 55f
            for ((sIdx, slot) in row.slots.withIndex()) {
                val sx = slotsStartX + sIdx * stepX
                val sy = rowY
                if (slot.tile == null) {
                    tileSkin?.appendTileBackDraw(tileDrawBuffer!!, sx, sy, tileW, tileH)
                } else {
                    tileSkin?.appendTileDraw(
                        buf = tileDrawBuffer!!,
                        tile = slot.tile,
                        x = sx,
                        y = sy,
                        width = tileW,
                        height = tileH,
                        isHover = false,
                        isDrawn = false,
                        alpha = 0.55f
                    )
                    markedSlotClickTargets.add(Rectangle(sx, sy, tileW, tileH) to slot.tile.tileId)
                }
            }
        }

        // 4. 下部分割线与四方牌河缩略网格
        val riverAreaTop = handsBaseY - 4 * rowHeight - 8f
        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.72f, 0.58f, 0.28f, 0.75f)
        shape.line(panelRect.x + 12f, riverAreaTop, panelRect.x + panelRect.width - 12f, riverAreaTop)
        shape.end()

        if (stageSmall != null) {
            batch.begin()
            stageSmall.draw(
                batch,
                "四方牌河",
                panelRect.x + 16f,
                riverAreaTop - 8f,
                Color(0.92f, 0.84f, 0.63f, 1f),
                outline = true
            )
            batch.end()
        } else {
            batch.begin()
            font.data.setScale(0.95f)
            font.color = Color(0.85f, 0.95f, 1.0f, 1.0f)
            font.draw(batch, "四方牌河", panelRect.x + 16f, riverAreaTop - 12f)
            font.data.setScale(origScaleX, origScaleY)
            batch.end()
        }

        val riverContentTop = riverAreaTop - 36f
        val riverContentBottom = panelRect.y + 10f
        val riverGridTop = riverContentTop + scrollOffsetY
        val riverTileW = 20f
        val riverTileH = 27f
        val riverStepX = 22f
        val riverStepY = 29f

        val riverMarks = TileMarkPresentation.riverMarks(snapshot)
        val dealer = snapshot.view.dealerSeat

        var currentGridY = riverGridTop
        for (windIdx in 0..3) {
            val seat = (dealer + windIdx) % 4
            val discards = snapshot.view.discards.getOrNull(seat) ?: emptyList()
            val windTitle = "${windNames[windIdx]}家 (${discards.size})"

            if (currentGridY in (riverContentBottom + 18f)..riverContentTop) {
                batch.begin()
                font.data.setScale(0.85f)
                font.color = Color(0.70f, 0.85f, 0.95f, 0.9f)
                font.draw(batch, windTitle, panelRect.x + 16f, currentGridY)
                font.data.setScale(origScaleX, origScaleY)
                batch.end()
            }
            currentGridY -= 18f

            val rowCount = if (discards.isEmpty()) 0 else (discards.size - 1) / 6 + 1
            for (i in discards.indices) {
                val tile = discards[i]
                if (!isTileVisibleInRiver(seat, tile, i)) {
                    continue
                }

                val col = i % 6
                val rowInRiver = i / 6
                val tx = panelRect.x + 20f + col * riverStepX
                val ty = currentGridY - (rowInRiver + 1) * riverStepY

                if (ty in riverContentBottom..(riverContentTop - riverTileH)) {
                    val isMarkedRiver = riverMarks.any { it.seat == seat && it.riverPosition?.discardIndex == i && it.tileId == tile.tileId }
                    val alpha = if (isMarkedRiver) 0.55f else 1.0f
                    tileSkin?.appendTileDraw(
                        buf = tileDrawBuffer!!,
                        tile = tile,
                        x = tx,
                        y = ty,
                        width = riverTileW,
                        height = riverTileH,
                        isHover = false,
                        isDrawn = false,
                        alpha = alpha
                    )
                    riverTileClickTargets.add(Rectangle(tx, ty, riverTileW, riverTileH) to tile.tileId)
                }
            }
            currentGridY -= (rowCount * riverStepY + 12f)
        }

        if (tileDrawBuffer != null && !tileDrawBuffer.isEmpty()) {
            tileDrawBuffer.flush(batch, shape, font)
        }

        // 5. 选中实体的跨面板高亮线框
        val selected = selectedTileId
        if (selected != null) {
            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.20f, 1.0f, 0.85f, 1.0f)
            for (target in markedSlotClickTargets) {
                if (target.second == selected) {
                    val r = target.first
                    shape.rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f)
                }
            }
            for (target in riverTileClickTargets) {
                if (target.second == selected) {
                    val r = target.first
                    shape.rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f)
                }
            }
            shape.end()
        }
    }

    override fun dispose() {
        isDisposed = true
        markedSlotClickTargets.clear()
        riverTileClickTargets.clear()
    }
}
