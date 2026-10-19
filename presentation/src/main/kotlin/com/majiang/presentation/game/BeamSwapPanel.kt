package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.BeamSwapCandidateView
import com.majiang.client.api.BeamSwapSlot
import com.majiang.client.api.GameSession
import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin

/**
 * 偷梁换柱表现层展示面板 (BeamSwapPanel)。
 *
 * 核心契约：
 * 1. 独立表现层组件，拥有并暴露只读 [model] 与公共几何包围盒供单元测试与输入判定；
 * 2. 暴露公共真实绘制/命中矩形：[btnOpenRect]、[panelRect]、[btnKeepRect]、[btnConfirmRect]、[handCardRects]、[targetCardRects]；
 * 3. 伴生对象集中提供标准服务端原因翻译 [translateServerReason]；
 * 4. 资源生命周期严格自闭环 (Disposable)。
 */
class BeamSwapPanel(
    val session: GameSession,
    val model: BeamSwapChoiceModel = BeamSwapChoiceModel()
) : Disposable {

    /** 几何包围盒契约 (1280x720 虚拟相机坐标系，返回防御性副本防止外部修改内部状态) */
    val btnOpenRect: Rectangle get() = RightActionColumnLayout.beamSwapRect()
    val panelRect: Rectangle get() = Rectangle(110f, 115f, 1060f, 510f)
    val btnKeepRect: Rectangle get() = Rectangle(300f, 155f, 200f, 48f)
    val btnConfirmRect: Rectangle get() = Rectangle(780f, 155f, 200f, 48f)

    var isDisposed: Boolean = false
        private set

    private val _handCardRects = LinkedHashMap<Int, Rectangle>()
    private val _targetCardRects = LinkedHashMap<BeamSwapSlot, Rectangle>()

    /** 当前手牌卡片包围盒（以 tileId 为键，返回防御性副本） */
    val handCardRects: Map<Int, Rectangle>
        get() = _handCardRects.mapValues { Rectangle(it.value) }

    /** 当前候选槽位包围盒（以 BeamSwapSlot 为键，返回防御性副本） */
    val targetCardRects: Map<BeamSwapSlot, Rectangle>
        get() = _targetCardRects.mapValues { Rectangle(it.value) }

    /**
     * 更新模型状态与本地命中几何。
     */
    fun update(
        snapshot: GameSnapshotView?,
        isPending: Boolean = false,
        connected: Boolean = true,
        nowMillis: Long = model.monotonicClock()
    ) {
        model.update(snapshot, connected, isPending, nowMillis)

        _targetCardRects.clear()
        _handCardRects.clear()

        val offer = snapshot?.beamSwap?.pendingOffer
        if (offer != null && model.isChoosing) {
            // 1. 候选槽位区 (y=405f, 52x72, 间隔 18)
            val candCount = offer.candidates.size
            val candW = 52f
            val candH = 72f
            val candSpacing = 18f
            val totalCandW = candCount * candW + if (candCount > 1) (candCount - 1) * candSpacing else 0f
            val startCandX = 640f - totalCandW / 2f
            val candY = 405f
            for ((i, candidate) in offer.candidates.withIndex()) {
                val rx = startCandX + i * (candW + candSpacing)
                _targetCardRects[candidate.slot] = Rectangle(rx, candY, candW, candH)
            }

            // 2. 原有暗手牌区 (y=254f, 52x72, 间隔 10)
            val handCount = offer.selectableHandTileIds.size
            val handW = 52f
            val handH = 72f
            val handSpacing = 10f
            val totalHandW = handCount * handW + if (handCount > 1) (handCount - 1) * handSpacing else 0f
            val startHandX = 640f - totalHandW / 2f
            val handY = 254f
            for ((i, tileId) in offer.selectableHandTileIds.withIndex()) {
                val hx = startHandX + i * (handW + handSpacing)
                _handCardRects[tileId] = Rectangle(hx, handY, handW, handH)
            }
        }
    }

    /**
     * 绘制偷梁换柱表现层 UI（常驻入口按钮或全屏模态选择面板）。
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
        tileMarkSelecting: Boolean = false,
        sleeveBloomChoosing: Boolean = false
    ) {
        if (snapshot == null) return
        val beam = snapshot.beamSwap ?: return

        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined

        // 1. 绘制常驻入口按钮（选择期间隐藏）
        if (!model.isChoosing) {
            val canOpen = model.canActivate &&
                !isDiscardPlaybackBusy &&
                !isPaused &&
                !session.isPending &&
                !isSubmitting &&
                !riichiSelecting &&
                !meldSelecting &&
                !tileMarkSelecting &&
                !sleeveBloomChoosing

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
            font.draw(batch, "偷梁换柱 · 10", btnOpenRect.x + 14f, btnOpenRect.y + 28f)
            batch.end()
            return
        }

        val offer = beam.pendingOffer ?: return

        // 2. 绘制模态全屏暗色遮罩
        Gdx.gl.glEnable(GL20.GL_BLEND)
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0f, 0f, 0f, 0.65f)
        shape.rect(0f, 0f, 1280f, 720f)

        // 2.1 底板与边框
        shape.color = Color(0.08f, 0.12f, 0.14f, 0.94f)
        shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.85f, 0.55f, 0.20f, 1f)
        shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)
        shape.end()

        // 2.2 标题、说明与倒计时
        val sec = maxOf(0L, (model.remainingMillis + 999L) / 1000L)
        val isUrgent = model.remainingMillis <= 3000L

        batch.begin()
        font.data.setScale(1.2f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        font.draw(batch, "偷梁换柱", panelRect.x + 35f, panelRect.y + panelRect.height - 25f)

        font.data.setScale(0.95f)
        font.color = Color(0.85f, 0.85f, 0.85f, 1f)
        font.draw(batch, "选一张原有暗手牌，再选一个目标槽位", panelRect.x + 155f, panelRect.y + panelRect.height - 25f)

        font.color = Color(0.70f, 0.75f, 0.75f, 1f)
        font.draw(batch, "已花费10点；保持原样不退还", panelRect.x + 550f, panelRect.y + panelRect.height - 25f)

        font.data.setScale(1.1f)
        font.color = if (isUrgent) Color(0.95f, 0.35f, 0.35f, 1f) else Color(1.0f, 0.90f, 0.60f, 1f)
        font.draw(batch, "${sec}秒", panelRect.x + panelRect.width - 85f, panelRect.y + panelRect.height - 25f)

        // 2.3 区域副标题
        font.data.setScale(0.85f)
        font.color = Color(0.65f, 0.80f, 0.80f, 1f)
        font.draw(batch, "目标候选槽位 (牌山/王牌)", panelRect.x + 35f, 492f)
        font.draw(batch, "原有暗手牌 (刚摸／刚补牌及副露不可交换)", panelRect.x + 35f, 342f)
        batch.end()

        // 2.4 绘制候选槽位牌体
        for (candidate in offer.candidates) {
            val rect = _targetCardRects[candidate.slot] ?: continue
            val isSelected = (model.selectedTargetSlot == candidate.slot)
            val drawY = if (isSelected) rect.y + 6f else rect.y

            if (tileSkin != null && tileDrawBuffer != null) {
                val cTile = candidate.tile
                if (candidate.marked && cTile != null) {
                    tileSkin.appendTileDraw(
                        buf = tileDrawBuffer,
                        tile = cTile,
                        x = rect.x,
                        y = drawY,
                        width = rect.width,
                        height = rect.height,
                        isHover = false,
                        isDrawn = false,
                        alpha = 0.85f
                    )
                } else {
                    tileSkin.appendTileBackDraw(
                        buf = tileDrawBuffer,
                        x = rect.x,
                        y = drawY,
                        width = rect.width,
                        height = rect.height
                    )
                }
            } else {
                shape.begin(ShapeRenderer.ShapeType.Filled)
                shape.color = if (candidate.marked) Color(0.35f, 0.50f, 0.45f, 1f) else Color(0.18f, 0.24f, 0.28f, 1f)
                shape.rect(rect.x, drawY, rect.width, rect.height)
                shape.end()
            }
        }

        // 2.5 绘制原有暗手牌牌体
        for (tileId in offer.selectableHandTileIds) {
            val rect = _handCardRects[tileId] ?: continue
            val isSelected = (model.selectedHandTileId == tileId)
            val drawY = if (isSelected) rect.y + 6f else rect.y
            val tileView = snapshot.view.myClosedTiles.find { it.tileId == tileId }
                ?: TileView.fromTileId(tileId)

            if (tileSkin != null && tileDrawBuffer != null) {
                tileSkin.appendTileDraw(
                    buf = tileDrawBuffer,
                    tile = tileView,
                    x = rect.x,
                    y = drawY,
                    width = rect.width,
                    height = rect.height,
                    isHover = false,
                    isDrawn = false,
                    alpha = 1.0f
                )
            } else {
                shape.begin(ShapeRenderer.ShapeType.Filled)
                shape.color = Color(0.25f, 0.30f, 0.35f, 1f)
                shape.rect(rect.x, drawY, rect.width, rect.height)
                shape.end()
            }
        }

        // 刷新所有牌体渲染缓冲（必须先画完牌体，再画上层选中边框、文字与汗标）
        tileDrawBuffer?.flush(batch, shape, font)

        // 2.6 绘制选中边框高亮（覆盖在牌体之上）
        for (candidate in offer.candidates) {
            val rect = _targetCardRects[candidate.slot] ?: continue
            if (model.selectedTargetSlot == candidate.slot) {
                val drawY = rect.y + 6f
                shape.begin(ShapeRenderer.ShapeType.Line)
                shape.color = Color(0.20f, 0.90f, 0.85f, 1f)
                shape.rect(rect.x - 2f, drawY - 2f, rect.width + 4f, rect.height + 4f)
                shape.end()
            }
        }
        for (tileId in offer.selectableHandTileIds) {
            val rect = _handCardRects[tileId] ?: continue
            if (model.selectedHandTileId == tileId) {
                val drawY = rect.y + 6f
                shape.begin(ShapeRenderer.ShapeType.Line)
                shape.color = Color(0.20f, 0.90f, 0.85f, 1f)
                shape.rect(rect.x - 2f, drawY - 2f, rect.width + 4f, rect.height + 4f)
                shape.end()
            }
        }

        // 2.7 绘制候选槽位文字标注与汗标（覆盖在牌体与高亮之上）
        var wallOrder = 1
        batch.begin()
        for (candidate in offer.candidates) {
            val rect = _targetCardRects[candidate.slot] ?: continue
            val isSelected = (model.selectedTargetSlot == candidate.slot)
            val drawY = if (isSelected) rect.y + 6f else rect.y

            // 槽位文本标注：普通牌山按 offer 顺序显示下1…下8；死山按实际槽绝对索引
            val slotLabel = when (val s = candidate.slot) {
                is BeamSwapSlot.WallSlot -> "下${wallOrder++}"
                is BeamSwapSlot.DeadWallSlot -> if (s.index in 0..3) "岭上${s.index + 1}" else "暗区${s.index + 1}"
                is BeamSwapSlot.ReplacementSlot -> "替补${s.index + 1}"
            }
            font.data.setScale(0.72f)
            font.color = if (candidate.marked) Color(0.35f, 0.95f, 0.70f, 1f) else Color(0.80f, 0.80f, 0.80f, 1f)
            font.draw(batch, slotLabel, rect.x + 4f, rect.y - 6f)

            // 标记候选牌上的汗标，确保绘制在牌面之上
            if (candidate.marked) {
                font.data.setScale(0.82f)
                font.color = Color(0.15f, 0.98f, 0.85f, 1f)
                font.draw(batch, "汗", rect.x + 32f, drawY + rect.height - 4f)
            }
        }
        batch.end()

        // 2.6 绘制底部按钮 (保持原样 / 互换)
        val canKeep = !session.isPending && !isSubmitting && model.remainingMillis > 0
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = if (canKeep) Color(0.35f, 0.40f, 0.45f, 1f) else Color(0.25f, 0.25f, 0.25f, 0.7f)
        shape.rect(btnKeepRect.x, btnKeepRect.y, btnKeepRect.width, btnKeepRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = if (canKeep) Color(0.60f, 0.65f, 0.70f, 1f) else Color(0.35f, 0.35f, 0.35f, 0.7f)
        shape.rect(btnKeepRect.x, btnKeepRect.y, btnKeepRect.width, btnKeepRect.height)
        shape.end()

        batch.begin()
        font.data.setScale(1.0f)
        font.color = if (canKeep) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
        font.draw(batch, "保持原样", btnKeepRect.x + 60f, btnKeepRect.y + 30f)
        batch.end()

        val canConfirm = model.canConfirm
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = if (canConfirm) Color(0.85f, 0.45f, 0.15f, 1f) else Color(0.30f, 0.30f, 0.30f, 0.7f)
        shape.rect(btnConfirmRect.x, btnConfirmRect.y, btnConfirmRect.width, btnConfirmRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = if (canConfirm) Color(1.0f, 0.75f, 0.35f, 1f) else Color(0.45f, 0.45f, 0.45f, 0.7f)
        shape.rect(btnConfirmRect.x, btnConfirmRect.y, btnConfirmRect.width, btnConfirmRect.height)
        shape.end()

        batch.begin()
        font.data.setScale(1.0f)
        font.color = if (canConfirm) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
        font.draw(batch, "互换", btnConfirmRect.x + 82f, btnConfirmRect.y + 30f)
        batch.end()
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
            "BEAM_SWAP_DISABLED" -> "偷梁换柱未启用"
            "BEAM_SWAP_RIICHI_LOCKED" -> "立直后禁止施展"
            "BEAM_SWAP_DEALER_EXTRA_EXCLUDED" -> "庄家起手第 14 张不可施展"
            "BEAM_SWAP_NO_DRAWN_TILE" -> "当前无刚摸的牌"
            "BEAM_SWAP_NO_HAND_TILES" -> "无可选原有暗手牌"
            "BEAM_SWAP_ALREADY_PENDING" -> "偷梁换柱进行中"
            "BEAM_SWAP_ALREADY_USED_THIS_DRAW" -> "本次摸牌已施展过"
            "BEAM_SWAP_NO_CANDIDATES" -> "候选槽位不足"
            "BEAM_SWAP_NO_PENDING_OFFER" -> "没有进行中的偷梁换柱选择"
            "BEAM_SWAP_OFFER_MISMATCH" -> "选择已过期"
            "BEAM_SWAP_STALE_OFFER" -> "局面已变化，选择失效"
            "BEAM_SWAP_CHOICE_PENDING" -> "请先完成偷梁换柱选择"
            "BEAM_SWAP_INVALID_CHOICE" -> "偷梁换柱选择无效"
            "BEAM_SWAP_INVALID_HAND_TILE" -> "所选手牌不可交换"
            "BEAM_SWAP_INVALID_TARGET_SLOT" -> "所选槽位不可交换"
            "SLEEVE_BLOOM_ALREADY_PENDING" -> "袖里藏花进行中"
            "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW" -> "本次摸牌已施展过袖里藏花"
            "INSUFFICIENT_FUNDS" -> "千术点数不足 (需10点)"
            "DEADLINE_EXPIRED" -> "思考时间已超时"
            "DECISION_MISMATCH" -> "决策已变更"
            "NOT_YOUR_TURN" -> "非本人出牌回合"
            "SEAT_NOT_HUMAN" -> "非真人玩家控制"
            "ACTION_IN_FLIGHT" -> "有动作正在处理中"
            "SESSION_CLOSED" -> "连接会话已关闭"
            "STALE_CONTROL_EPOCH" -> "控制代次已过期"
            "WRONG_PHASE" -> "当前阶段无法施术"
            "NO_ACTIVE_DECISION" -> "当前无有效决策"
            "HOST_PAUSED" -> "对局已暂停"
            "REQUEST_ID_CONFLICT" -> "请求标识冲突"
            else -> reason
        }
    }
}
