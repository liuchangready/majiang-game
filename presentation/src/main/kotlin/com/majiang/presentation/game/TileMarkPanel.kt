package com.majiang.presentation.game

import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.GameSession
import com.majiang.client.api.GameSnapshotView
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin

/**
 * 落汗千术表现层展示面板 (TileMarkPanel)。
 *
 * 核心契约：
 * 1. 独立表现层组件，拥有并暴露只读 [model] 与公共几何包围盒供单元测试与输入判定；
 * 2. 暴露只读 [btnOpenRect]、[btnConfirmRect]、[btnCancelRect]，即真实绘制与点击判定的包围盒；
 * 3. 施术确认调用 [GameSession.submitTileMark]，立即执行防双击保护；
 * 4. 追踪栏展示最多三组各四张牌的牌面、位置与独立期限，1280x720 视口下点击翻页可查全部 12 张；
 * 5. 对手区域显示“落汗识别”授权牌面；
 * 6. 集中管理 [translateServerReason] 与 [TILE_MARK_CHARS] 字体子集；
 * 7. 资源生命周期严格自闭环 (Disposable)。
 */
class TileMarkPanel(
    val session: GameSession,
    val model: TileMarkSelectionModel = TileMarkSelectionModel()
) : Disposable {

    /** 几何包围盒契约 (1280x720 虚拟相机坐标系) */
    val btnOpenRect = Rectangle(1080f, 150f, 140f, 44f)
    val btnConfirmRect = Rectangle(670f, 145f, 140f, 44f)
    val btnCancelRect = Rectangle(1030f, 145f, 170f, 44f)

    // 选牌信息横幅
    val selectionBannerRect = Rectangle(230f, 210f, 820f, 130f)

    // 追踪栏几何包围盒 (可翻页查看三组共 12 张)
    val trackingPanelRect = Rectangle(20f, 470f, 320f, 175f)
    val btnTrackingPrevRect = Rectangle(200f, 618f, 42f, 22f)
    val btnTrackingNextRect = Rectangle(250f, 618f, 42f, 22f)
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
     * 绘制落汗表现层所有 UI（按钮、选牌横幅、追踪栏与对手识别）。
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
        tileDrawBuffer: TileDrawBuffer? = null
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
                !meldSelecting

            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = if (canOpen) Color(0.85f, 0.45f, 0.15f, 1f) else Color(0.25f, 0.25f, 0.25f, 0.7f)
            shape.rect(btnOpenRect.x, btnOpenRect.y, btnOpenRect.width, btnOpenRect.height)
            shape.end()

            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = if (canOpen) Color(1.0f, 0.75f, 0.35f, 1f) else Color(0.40f, 0.40f, 0.40f, 0.8f)
            shape.rect(btnOpenRect.x, btnOpenRect.y, btnOpenRect.width, btnOpenRect.height)
            shape.end()

            batch.begin()
            font.data.setScale(1.0f)
            font.color = if (canOpen) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
            font.draw(batch, "落汗 · 10", btnOpenRect.x + 22f, btnOpenRect.y + 28f)
            batch.end()
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

        // 3. 追踪栏展示 (三组各四张牌)
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
                font.data.setScale(0.75f)
                font.color = Color.WHITE
                font.draw(batch, "上组", btnTrackingPrevRect.x + 8f, btnTrackingPrevRect.y + 16f)
                font.draw(batch, "下组", btnTrackingNextRect.x + 8f, btnTrackingNextRect.y + 16f)
                batch.end()
            }

            batch.begin()
            font.data.setScale(0.90f)
            font.color = Color(1.0f, 0.85f, 0.35f, 1f)
            val groupTitle = "落汗追踪 [组 ${safePage + 1}/${groups.size}]"
            font.draw(batch, groupTitle, trackingPanelRect.x + 12f, trackingPanelRect.y + trackingPanelRect.height - 12f)

            font.data.setScale(0.85f)
            font.color = Color(0.75f, 0.95f, 0.85f, 1f)
            val duration = "有效期限: ${TileMarkPresentation.durationText(curGroup)}"
            font.draw(batch, duration, trackingPanelRect.x + 12f, trackingPanelRect.y + trackingPanelRect.height - 34f)

            for (i in curGroup.tiles.indices) {
                val t = curGroup.tiles[i]
                val loc = TileMarkPresentation.locationText(t, snapshot.viewerSeat)
                val tileName = if (t.tile.isRedFive) "赤${t.tile.displayName()}" else t.tile.displayName()
                font.color = Color.WHITE
                font.draw(
                    batch,
                    "$tileName : $loc",
                    trackingPanelRect.x + 16f,
                    trackingPanelRect.y + trackingPanelRect.height - 60f - i * 26f
                )
            }
            batch.end()
        }

        // 4. 对手区域显示“落汗识别”分行小牌面
        val viewer = snapshot.viewerSeat
        tileDrawBuffer?.clear()
        for (seat in 0..3) {
            if (seat == viewer) continue
            val oppMarks = TileMarkPresentation.opponentMarks(snapshot, seat)
            if (oppMarks.isNotEmpty()) {
                val (baseX, baseY) = when ((seat - viewer + 4) % 4) {
                    1 -> Pair(1140f, 345f) // 下家 (右侧)，分行排布，不越界(1140..1250)，在按钮(y=150)和P1信息(y=364..400)之间
                    2 -> Pair(500f, 670f)  // 对家 (顶部)，分行排布，在P2(x=640)左侧，避开右侧melds(x>=846)及左侧追踪栏(x<=340)
                    3 -> Pair(40f, 345f)   // 上家 (左侧)，分行排布，在左侧(x=40..140)，不越入牌桌中心，在追踪栏(y=470..645)下方
                    else -> Pair(40f, 345f)
                }
                batch.begin()
                font.data.setScale(0.85f)
                font.color = Color(0.35f, 0.90f, 1.0f, 1f)
                font.draw(batch, "落汗识别:", baseX, baseY)
                batch.end()

                if (tileSkin != null && tileDrawBuffer != null) {
                    val tileW = 26f
                    val tileH = 36f
                    val spacingX = 28f
                    val spacingY = 38f
                    for ((idx, markTile) in oppMarks.take(12).withIndex()) {
                        val col = idx % 4
                        val row = idx / 4
                        val tileX = baseX + col * spacingX
                        val tileY = baseY - 20f - tileH - row * spacingY
                        tileSkin.appendTileDraw(
                            buf = tileDrawBuffer,
                            tile = markTile.tile,
                            x = tileX,
                            y = tileY,
                            width = tileW,
                            height = tileH,
                            isHover = false,
                            isDrawn = false
                        )
                    }
                } else {
                    batch.begin()
                    font.data.setScale(0.80f)
                    val lines = oppMarks.chunked(4)
                    for ((lineIdx, chunk) in lines.withIndex()) {
                        val lineText = chunk.joinToString(" ") {
                            if (it.tile.isRedFive) "赤${it.tile.displayName()}" else it.tile.displayName()
                        }
                        font.draw(batch, lineText, baseX, baseY - 20f - lineIdx * 20f)
                    }
                    batch.end()
                }
            }
        }
        if (tileSkin != null && tileDrawBuffer != null && !tileDrawBuffer.isEmpty()) {
            shape.projectionMatrix = camera.combined
            batch.projectionMatrix = camera.combined
            tileDrawBuffer.flush(batch, shape, font)
        }
        font.data.setScale(1.0f)
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

        /**
         * 涵盖所有落汗相关文案、状态、拒绝原因及牌面描述的中文字形全集。
         */
        val TILE_MARK_CHARS: String = buildString {
            // 基础 ASCII 及标点
            append(" !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~")
            append("·：；？！“”‘’—，。（）[]【】《》/、")
            // 核心契约用字集（包含可见文案全集）
            append("落汗 标记四张牌 请选择 4 张 10 额度 最多 12 张 确认 取消 自己手中 下家手中 对家手中 上家手中 牌河第 13 张 当前不可见 另余 3 局 本局最后有效 落汗识别")
            // 拒绝原因翻译文本用字集
            val reasons = listOf(
                "TILE_MARK_DISABLED", "TILE_MARK_CAPACITY_EXCEEDED", "INSUFFICIENT_FUNDS",
                "DEADLINE_EXPIRED", "DECISION_MISMATCH", "NOT_YOUR_TURN", "SEAT_NOT_HUMAN",
                "ACTION_IN_FLIGHT", "SESSION_CLOSED", "TILE_MARK_RIICHI_LOCKED",
                "TILE_MARK_INVALID_SELECTION", "TILE_MARK_TILE_NOT_OWNED", "TILE_MARK_ALREADY_MARKED",
                "STALE_CONTROL_EPOCH", "WRONG_PHASE", "NO_ACTIVE_DECISION"
            )
            for (r in reasons) {
                append(translateServerReason(r))
            }
            // 麻将常用牌面字形
            append("一二三四五六七八九万筒索東南西北白發中副露宝牌指示牌追踪组已生效还需打出赤期限")
        }.toSet().joinToString("")
    }
}
