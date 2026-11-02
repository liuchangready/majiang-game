package com.majiang.presentation.game

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.NinePatch
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.g2d.TextureRegion
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.GameSession
import com.majiang.client.api.GameSnapshotView
import com.majiang.presentation.common.render.TableStageFonts
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin

/**
 * 落汗千术表现层展示面板 (TileMarkPanel)。
 *
 * 核心契约：
 * 1. 独立表现层组件，拥有并暴露只读 [model] 与公共几何包围盒供单元测试与输入判定；
 * 2. 暴露只读 [btnOpenRect]、[btnConfirmRect]、[btnCancelRect]，即真实绘制与点击判定的包围盒；
 * 3. 施术确认调用 [GameSession.submitTileMark]，立即执行防双击保护；
 * 4. 追踪栏展示最多三组各四张牌的牌面与图形寿命区，1280x720 视口下点击翻页可查全部 12 张；
 * 5. 对手区域完整对手手牌展示；
 * 6. 集中管理 [translateServerReason] 服务端原因翻译；
 * 7. 资源生命周期严格自闭环 (Disposable)。
 */
class TileMarkPanel(
    val session: GameSession,
    val model: TileMarkSelectionModel = TileMarkSelectionModel()
) : Disposable {

    /** 几何包围盒契约 (1280x720 虚拟相机坐标系) */
    val btnOpenRect: Rectangle
        get() = RightActionColumnLayout.tileMarkRect()
    val btnConfirmRect = Rectangle(670f, 145f, 140f, 44f)
    val btnCancelRect = Rectangle(1030f, 145f, 170f, 44f)

    // 选牌信息横幅
    val selectionBannerRect = Rectangle(230f, 210f, 820f, 130f)

    // 追踪栏几何包围盒 (紧凑图形区，不遮挡对手手牌，可翻页查看三组共 12 张)
    val trackingPanelRect = Rectangle(20f, 530f, 185f, 95f)
    val btnTrackingPrevRect = Rectangle(100f, 595f, 38f, 22f)
    val btnTrackingNextRect = Rectangle(142f, 595f, 38f, 22f)
    var trackingPage: Int = 0

    var isDisposed: Boolean = false
        private set

    /**
     * 更新模型状态。
     */
    fun update(
        snapshot: GameSnapshotView?,
        pending: Boolean = false,
        connected: Boolean = true,
        nowMillis: Long = model.monotonicClock()
    ) {
        model.update(snapshot, pending, connected, nowMillis)
    }

    /**
     * 处理点击翻页追踪栏。
     */
    fun touchDown(tx: Float, ty: Float, groupCount: Int): Boolean {
        if (groupCount <= 1) return false
        if (btnTrackingPrevRect.contains(tx, ty)) {
            trackingPage = (trackingPage - 1 + groupCount) % groupCount
            return true
        }
        if (btnTrackingNextRect.contains(tx, ty)) {
            trackingPage = (trackingPage + 1) % groupCount
            return true
        }
        return false
    }

    /**
     * 绘制落汗表现层所有 UI（按钮、选牌横幅、追踪栏与完整对手手牌展示）。
     */
    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont,
        snapshot: GameSnapshotView?,
        isDiscardPlaybackBusy: Boolean,
        isPaused: Boolean,
        isSubmitting: Boolean,
        riichiSelecting: Boolean,
        meldSelecting: Boolean,
        tileSkin: TileSkin? = null,
        tileDrawBuffer: TileDrawBuffer? = null,
        sleeveBloomChoosing: Boolean = false,
        beamSwapChoosing: Boolean = false,
        entryPatch: NinePatch? = null,
        labelBand: TextureRegion? = null,
        stageFonts: TableStageFonts? = null
    ) {
        if (snapshot == null) return
        val mark = snapshot.tileMark ?: return

        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined

        // 1. 绘制“落汗 · 10”入口按钮（选牌期间隐藏，改显示确认/取消）
        if (!model.isSelecting) {
            val canOpen = mark.canApply &&
                !isDiscardPlaybackBusy &&
                !isPaused &&
                !session.isPending &&
                !isSubmitting &&
                !riichiSelecting &&
                !meldSelecting &&
                !sleeveBloomChoosing &&
                !beamSwapChoosing

            if (entryPatch == null) {
                shape.begin(ShapeRenderer.ShapeType.Filled)
                shape.color = if (canOpen) Color(0.85f, 0.45f, 0.15f, 1f) else Color(0.25f, 0.25f, 0.25f, 0.7f)
                shape.rect(btnOpenRect.x, btnOpenRect.y, btnOpenRect.width, btnOpenRect.height)
                shape.end()

                shape.begin(ShapeRenderer.ShapeType.Line)
                shape.color = if (canOpen) Color(1.0f, 0.75f, 0.35f, 1f) else Color(0.40f, 0.40f, 0.40f, 0.8f)
                shape.rect(btnOpenRect.x, btnOpenRect.y, btnOpenRect.width, btnOpenRect.height)
                shape.end()
            }

            if (entryPatch != null) {
                batch.begin()
                batch.color = if (canOpen) Color.WHITE else Color(0.58f, 0.58f, 0.58f, 0.95f)
                entryPatch.draw(batch, btnOpenRect.x, btnOpenRect.y, btnOpenRect.width, btnOpenRect.height)
                batch.color = Color.WHITE
                batch.end()

                // 红底金饰按钮的中央花纹会与白字争夺对比度；只压一条窄暗带，
                // 保留外圈红底与金边，三枚技能入口的标签在 960×540 也能稳定读出。
                shape.begin(ShapeRenderer.ShapeType.Filled)
                shape.color = Color(0.012f, 0.018f, 0.012f, 0.52f)
                shape.rect(
                    btnOpenRect.x + 5f,
                    btnOpenRect.y + 7f,
                    btnOpenRect.width - 10f,
                    22f
                )
                shape.end()
            }
            batch.begin()
            if (stageFonts != null) {
                stageFonts.body.drawCentered(
                    batch,
                    "落汗 10",
                    btnOpenRect.x + btnOpenRect.width / 2f,
                    btnOpenRect.y + 29f,
                    if (canOpen) Color.WHITE else Color(0.86f, 0.83f, 0.74f, 1f),
                    outline = true
                )
            } else {
                font.data.setScale(1.15f, 1.15f)
                font.color = Color(0.01f, 0.01f, 0.008f, 0.98f)
                font.draw(batch, "落汗 10", btnOpenRect.x + 23f, btnOpenRect.y + 22f)
                font.color = if (canOpen) Color.WHITE else Color(0.86f, 0.83f, 0.74f, 1f)
                font.draw(batch, "落汗 10", btnOpenRect.x + 22f, btnOpenRect.y + 23f)
            }
            batch.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.91f, 0.73f, 0.33f, if (canOpen) 0.92f else 0.48f)
            shape.rect(btnOpenRect.x + 1f, btnOpenRect.y + 1f, btnOpenRect.width - 2f, btnOpenRect.height - 2f)
            shape.end()
        }

        // 2. 选牌模式横幅与确认/取消按钮
        if (model.isSelecting) {
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.08f, 0.12f, 0.14f, 0.94f)
            shape.rect(selectionBannerRect.x, selectionBannerRect.y, selectionBannerRect.width, selectionBannerRect.height)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.85f, 0.55f, 0.20f, 1f)
            shape.rect(selectionBannerRect.x, selectionBannerRect.y, selectionBannerRect.width, selectionBannerRect.height)
            shape.end()

            batch.begin()
            font.data.setScale(1.15f)
            font.color = Color(1.0f, 0.85f, 0.35f, 1f)
            font.draw(batch, "落汗绝技: 从自身手牌选择 4 张实体牌作标记", selectionBannerRect.x + 30f, selectionBannerRect.y + 105f)

            font.data.setScale(1.0f)
            font.color = Color.WHITE
            font.draw(
                batch,
                "请选择 4 张 (已选 ${model.selectedTileIds.size}/4) · 消耗 10 额度 · 最多 12 张 (已生效 ${mark.activeTileCount} 张)",
                selectionBannerRect.x + 30f,
                selectionBannerRect.y + 70f
            )

            val statusHint = if (model.canConfirm) {
                "已选满 4 张，点击确认施展落汗"
            } else if (model.selectedTileIds.size < 4) {
                "请选择 4 张 (还需选择 ${4 - model.selectedTileIds.size} 张)"
            } else {
                mark.disabledReason?.let { translateServerReason(it) } ?: "当前不可确认"
            }
            font.color = if (model.canConfirm) Color(0.3f, 0.9f, 0.4f, 1f) else Color(0.9f, 0.6f, 0.3f, 1f)
            font.draw(batch, statusHint, selectionBannerRect.x + 30f, selectionBannerRect.y + 35f)
            batch.end()

            // 确认与取消按钮
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = if (model.canConfirm) Color(0.85f, 0.45f, 0.15f, 1f) else Color(0.30f, 0.30f, 0.30f, 0.7f)
            shape.rect(btnConfirmRect.x, btnConfirmRect.y, btnConfirmRect.width, btnConfirmRect.height)

            shape.color = Color(0.45f, 0.20f, 0.20f, 1f)
            shape.rect(btnCancelRect.x, btnCancelRect.y, btnCancelRect.width, btnCancelRect.height)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = if (model.canConfirm) Color(1.0f, 0.75f, 0.35f, 1f) else Color(0.45f, 0.45f, 0.45f, 0.7f)
            shape.rect(btnConfirmRect.x, btnConfirmRect.y, btnConfirmRect.width, btnConfirmRect.height)

            shape.color = Color(0.70f, 0.30f, 0.30f, 1f)
            shape.rect(btnCancelRect.x, btnCancelRect.y, btnCancelRect.width, btnCancelRect.height)
            shape.end()

            batch.begin()
            font.data.setScale(1.0f)
            font.color = if (model.canConfirm) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
            font.draw(batch, "确认", btnConfirmRect.x + 50f, btnConfirmRect.y + 28f)

            font.color = Color.WHITE
            font.draw(batch, "取消", btnCancelRect.x + 65f, btnCancelRect.y + 28f)
            batch.end()
        }

        // 3. 标记组展示 (四张小牌图标与四格寿命点)
        val groups = mark.activeGroups
        if (groups.isNotEmpty()) {
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.04f, 0.07f, 0.06f, 0.88f)
            shape.rect(trackingPanelRect.x, trackingPanelRect.y, trackingPanelRect.width, trackingPanelRect.height)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.20f, 0.65f, 0.55f, 0.9f)
            shape.rect(trackingPanelRect.x, trackingPanelRect.y, trackingPanelRect.width, trackingPanelRect.height)
            shape.end()

            val safePage = trackingPage.coerceIn(0, groups.size - 1)
            val curGroup = groups[safePage]

            // 绘制组别标题与翻页按钮
            batch.begin()
            font.data.setScale(0.75f)
            font.color = Color(0.85f, 0.95f, 0.90f, 1f)
            val groupTitle = if (groups.size > 1) "组 ${safePage + 1}/${groups.size}" else "落汗组"
            font.draw(batch, groupTitle, trackingPanelRect.x + 10f, trackingPanelRect.y + 80f)
            batch.end()

            // 绘制翻页按钮（当有多组时）
            if (groups.size > 1) {
                shape.begin(ShapeRenderer.ShapeType.Filled)
                shape.color = Color(0.12f, 0.28f, 0.26f, 0.95f)
                shape.rect(btnTrackingPrevRect.x, btnTrackingPrevRect.y, btnTrackingPrevRect.width, btnTrackingPrevRect.height)
                shape.rect(btnTrackingNextRect.x, btnTrackingNextRect.y, btnTrackingNextRect.width, btnTrackingNextRect.height)
                shape.end()

                shape.begin(ShapeRenderer.ShapeType.Line)
                shape.color = Color(0.35f, 0.85f, 0.70f, 1f)
                shape.rect(btnTrackingPrevRect.x, btnTrackingPrevRect.y, btnTrackingPrevRect.width, btnTrackingPrevRect.height)
                shape.rect(btnTrackingNextRect.x, btnTrackingNextRect.y, btnTrackingNextRect.width, btnTrackingNextRect.height)
                shape.end()

                batch.begin()
                font.data.setScale(0.70f)
                font.color = Color.WHITE
                font.draw(batch, "上组", btnTrackingPrevRect.x + 5f, btnTrackingPrevRect.y + 15f)
                font.draw(batch, "下组", btnTrackingNextRect.x + 5f, btnTrackingNextRect.y + 15f)
                batch.end()
            }

            // 绘制当前组 4 张小牌图标
            val smallTileW = 20f
            val smallTileH = 28f
            val tileStartX = trackingPanelRect.x + 10f
            val tileSpacing = 23f
            val tileY = trackingPanelRect.y + 36f

            if (tileSkin != null && tileDrawBuffer != null) {
                for (i in curGroup.tiles.indices) {
                    val tx = tileStartX + i * tileSpacing
                    tileSkin.appendTileDraw(
                        buf = tileDrawBuffer,
                        tile = curGroup.tiles[i].tile,
                        x = tx,
                        y = tileY,
                        width = smallTileW,
                        height = smallTileH,
                        isHover = false,
                        isDrawn = false,
                        alpha = 0.85f
                    )
                }
                tileDrawBuffer.flush(batch, shape, font)
            }

            // 绘制四格寿命点：
            // - 已消耗局：空心点
            // - 本局有效：描边高亮（局间准备阶段变空）
            // - 后续有效局：实心点
            val future = curGroup.futureRoundsRemaining
            val consumed = (3 - future).coerceIn(0, 3)
            val isStacking = (snapshot.view.phase == com.majiang.client.api.GamePhaseView.STACKING)
            val dotStartX = trackingPanelRect.x + 18f
            val dotSpacing = 20f
            val dotY = trackingPanelRect.y + 18f
            val dotR = 4.5f

            for (d in 0..3) {
                val cx = dotStartX + d * dotSpacing
                when {
                    d < consumed -> {
                        // 已经消耗的局：空心点
                        shape.begin(ShapeRenderer.ShapeType.Line)
                        shape.color = Color(0.45f, 0.50f, 0.50f, 0.70f)
                        shape.circle(cx, dotY, dotR)
                        shape.end()
                    }
                    d == consumed -> {
                        // 本局格：局间变空，当局描边高亮
                        if (isStacking) {
                            shape.begin(ShapeRenderer.ShapeType.Line)
                            shape.color = Color(0.45f, 0.50f, 0.50f, 0.70f)
                            shape.circle(cx, dotY, dotR)
                            shape.end()
                        } else {
                            shape.begin(ShapeRenderer.ShapeType.Filled)
                            shape.color = Color(1.0f, 0.85f, 0.35f, 1f)
                            shape.circle(cx, dotY, dotR)
                            shape.end()

                            shape.begin(ShapeRenderer.ShapeType.Line)
                            shape.color = Color(1.0f, 1.0f, 0.85f, 1f)
                            shape.circle(cx, dotY, dotR + 2f)
                            shape.end()
                        }
                    }
                    else -> {
                        // 后续有效局：实心点
                        shape.begin(ShapeRenderer.ShapeType.Filled)
                        shape.color = Color(0.25f, 0.80f, 0.65f, 0.95f)
                        shape.circle(cx, dotY, dotR)
                        shape.end()
                    }
                }
            }
        }
    }

    override fun dispose() {
        isDisposed = true
        model.cancel()
    }

    companion object {
        /**
         * 服务端业务拒绝原因翻译为标准中文说明。
         */
        fun translateServerReason(reason: String): String = when (reason) {
            "TILE_MARK_DISABLED" -> "落汗千术未启用"
            "TILE_MARK_CAPACITY_EXCEEDED" -> "落汗标记已达上限 (最多12张)"
            "INSUFFICIENT_FUNDS" -> "千术点数不足 (需10点)"
            "DEADLINE_EXPIRED" -> "思考时间已超时"
            "DECISION_MISMATCH" -> "决策已变更"
            "NOT_YOUR_TURN" -> "非本人出牌回合"
            "SEAT_NOT_HUMAN" -> "非真人玩家控制"
            "ACTION_IN_FLIGHT" -> "有动作正在处理中"
            "SESSION_CLOSED" -> "连接会话已关闭"
            "TILE_MARK_RIICHI_LOCKED" -> "立直后禁止施展落汗"
            "TILE_MARK_INVALID_SELECTION" -> "选牌无效 (须恰好4张实体牌)"
            "TILE_MARK_TILE_NOT_OWNED" -> "选中的牌不在本人手牌中"
            "TILE_MARK_ALREADY_MARKED" -> "包含已生效的落汗牌"
            "STALE_CONTROL_EPOCH" -> "控制代次已过期"
            "WRONG_PHASE" -> "当前阶段无法施术"
            "NO_ACTIVE_DECISION" -> "当前无有效决策"
            else -> reason
        }
    }
}
