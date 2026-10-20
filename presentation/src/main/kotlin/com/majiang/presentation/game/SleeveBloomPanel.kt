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
import com.majiang.client.api.SleeveBloomDrawKind
import com.majiang.client.api.SleeveBloomKeep
import com.majiang.presentation.common.render.TileDrawBuffer
import com.majiang.presentation.common.render.TileSkin

/**
 * 袖里藏花表现层展示面板 (SleeveBloomPanel)。
 *
 * 核心契约：
 * 1. 独立表现层组件，拥有并暴露只读 [model] 与公共几何包围盒供单元测试与输入判定；
 * 2. 暴露公共真实绘制/命中矩形：[btnOpenRect]、[panelRect]、[firstCardRect]、[secondCardRect]、[btnConfirmRect]、[btnCancelRect]；
 * 3. 伴生对象集中提供标准服务端原因翻译 [translateServerReason]；
 * 4. 资源生命周期严格自闭环 (Disposable)。
 */
class SleeveBloomPanel(
    val session: GameSession,
    val model: SleeveBloomChoiceModel = SleeveBloomChoiceModel()
) : Disposable {

    /** 几何包围盒契约 (1280x720 虚拟相机坐标系) */
    val btnOpenRect: Rectangle
        get() = RightActionColumnLayout.sleeveBloomRect()
    val panelRect = Rectangle(390f, 210f, 500f, 300f)
    val firstCardRect = Rectangle(460f, 290f, 90f, 126f)
    val secondCardRect = Rectangle(730f, 290f, 90f, 126f)
    val btnConfirmRect = Rectangle(450f, 225f, 180f, 44f)
    val btnCancelRect = Rectangle(650f, 225f, 180f, 44f)

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
     * 绘制袖里藏花表现层 UI（常驻入口按钮或二选一模态面板）。
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
        beamSwapChoosing: Boolean = false
    ) {
        if (snapshot == null) return
        val bloom = snapshot.sleeveBloom ?: return

        shape.projectionMatrix = camera.combined
        batch.projectionMatrix = camera.combined

        // 1. 绘制常驻入口按钮（二选一期间隐藏）
        if (!model.isChoosing) {
            val canOpen = (model.canActivate || model.canActivateOffTurn) &&
                (!isDiscardPlaybackBusy || model.canActivateOffTurn) &&
                !isPaused &&
                !session.isPending &&
                !isSubmitting &&
                !riichiSelecting &&
                !meldSelecting &&
                !tileMarkSelecting &&
                !beamSwapChoosing

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
            font.draw(batch, "袖里藏花 · 10", btnOpenRect.x + 10f, btnOpenRect.y + 28f)
            if (model.isReserved) {
                font.data.setScale(0.78f)
                font.color = Color(0.55f, 0.90f, 0.85f, 1f)
                font.draw(batch, "已选牌，待本人下次普通摸牌", btnOpenRect.x - 48f, btnOpenRect.y - 8f)
            }
            batch.end()
            return
        }

        // 2. 绘制二选一模态面板
        val offer = bloom.pendingOffer ?: return

        // 2.1 底板与边框
        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = Color(0.08f, 0.12f, 0.14f, 0.94f)
        shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.85f, 0.55f, 0.20f, 1f)
        shape.rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height)
        shape.end()

        // 2.2 标题、费用与倒计时
        val sec = maxOf(0L, (model.remainingMillis + 999L) / 1000L)
        val isUrgent = model.remainingMillis <= 3000L

        batch.begin()
        font.data.setScale(1.2f)
        font.color = Color(1.0f, 0.85f, 0.35f, 1f)
        font.draw(batch, if (model.isOffTurnChoice) "袖里藏花 · 他家回合预定" else "袖里藏花 · 二选一", panelRect.x + 130f, 490f)

        font.data.setScale(0.95f)
        font.color = Color(0.85f, 0.85f, 0.85f, 1f)
        font.draw(batch, "已消耗 10 额度", panelRect.x + 195f, 455f)

        font.data.setScale(1.1f)
        font.color = if (isUrgent) Color(0.95f, 0.35f, 0.35f, 1f) else Color(1.0f, 0.90f, 0.60f, 1f)
        font.draw(batch, "${sec}秒", panelRect.x + 430f, 490f)
        batch.end()

        // 2.3 两张候选牌卡绘制
        if (tileSkin != null && tileDrawBuffer != null) {
            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = offer.firstTile,
                x = firstCardRect.x,
                y = firstCardRect.y,
                width = firstCardRect.width,
                height = firstCardRect.height,
                isHover = false,
                isDrawn = false,
                alpha = 1.0f
            )
            tileSkin.appendTileDraw(
                buf = tileDrawBuffer,
                tile = offer.secondTile,
                x = secondCardRect.x,
                y = secondCardRect.y,
                width = secondCardRect.width,
                height = secondCardRect.height,
                isHover = false,
                isDrawn = false,
                alpha = 1.0f
            )
            tileDrawBuffer.flush(batch, shape, font)
        } else {
            // 无头环境兜底绘制卡片
            shape.begin(ShapeRenderer.ShapeType.Filled)
            shape.color = Color(0.20f, 0.25f, 0.30f, 1f)
            shape.rect(firstCardRect.x, firstCardRect.y, firstCardRect.width, firstCardRect.height)
            shape.rect(secondCardRect.x, secondCardRect.y, secondCardRect.width, secondCardRect.height)
            shape.end()
        }

        // 2.4 牌卡选中描边 (青色)
        val isFirstSelected = (model.selected == SleeveBloomKeep.FIRST)
        val isSecondSelected = (model.selected == SleeveBloomKeep.SECOND)

        if (isFirstSelected || isSecondSelected) {
            shape.begin(ShapeRenderer.ShapeType.Line)
            shape.color = Color(0.20f, 0.90f, 0.85f, 1f)
            if (isFirstSelected) {
                shape.rect(firstCardRect.x - 2f, firstCardRect.y - 2f, firstCardRect.width + 4f, firstCardRect.height + 4f)
            }
            if (isSecondSelected) {
                shape.rect(secondCardRect.x - 2f, secondCardRect.y - 2f, secondCardRect.width + 4f, secondCardRect.height + 4f)
            }
            shape.end()
        }

        // 2.5 来源标注
        batch.begin()
        font.data.setScale(0.9f)
        font.color = Color(0.80f, 0.85f, 0.85f, 1f)
        font.draw(batch, if (model.isOffTurnChoice) "牌山前张" else "已摸到", firstCardRect.x + 20f, 286f)
        val secondLabel = if (model.isOffTurnChoice) "牌山后张"
        else if (offer.drawKind == SleeveBloomDrawKind.RINSHAN) "岭上次张" else "牌山次张"
        font.draw(batch, secondLabel, secondCardRect.x + 10f, 286f)

        // 2.5.1 两只候选各自按“假设成为实际摸牌”计算的私有和牌/杠提示。
        fun hint(canTsumo: Boolean, canKan: Boolean): String = when {
            canTsumo && canKan -> "可和牌 · 可杠"
            canTsumo -> "可和牌"
            canKan -> "可杠"
            else -> ""
        }
        val firstHint = hint(offer.firstCanTsumo, offer.firstCanKan)
        val secondHint = hint(offer.secondCanTsumo, offer.secondCanKan)
        if (firstHint.isNotEmpty() || secondHint.isNotEmpty()) {
            font.data.setScale(0.75f)
            font.color = Color(1.0f, 0.82f, 0.35f, 1f)
            if (firstHint.isNotEmpty()) font.draw(batch, firstHint, firstCardRect.x + 8f, firstCardRect.y + 18f)
            if (secondHint.isNotEmpty()) font.draw(batch, secondHint, secondCardRect.x + 8f, secondCardRect.y + 18f)
            font.data.setScale(0.9f)
        }
        batch.end()

        // 2.6 确认与取消按钮
        val canConfirm = model.canConfirm
        val canCancel = !session.isPending && !isSubmitting && model.remainingMillis > 0

        shape.begin(ShapeRenderer.ShapeType.Filled)
        shape.color = if (canConfirm) Color(0.85f, 0.45f, 0.15f, 1f) else Color(0.30f, 0.30f, 0.30f, 0.7f)
        shape.rect(btnConfirmRect.x, btnConfirmRect.y, btnConfirmRect.width, btnConfirmRect.height)

        shape.color = if (canCancel) Color(0.45f, 0.20f, 0.20f, 1f) else Color(0.25f, 0.20f, 0.20f, 0.7f)
        shape.rect(btnCancelRect.x, btnCancelRect.y, btnCancelRect.width, btnCancelRect.height)
        shape.end()

        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = if (canConfirm) Color(1.0f, 0.75f, 0.35f, 1f) else Color(0.45f, 0.45f, 0.45f, 0.7f)
        shape.rect(btnConfirmRect.x, btnConfirmRect.y, btnConfirmRect.width, btnConfirmRect.height)

        shape.color = if (canCancel) Color(0.70f, 0.30f, 0.30f, 1f) else Color(0.45f, 0.30f, 0.30f, 0.7f)
        shape.rect(btnCancelRect.x, btnCancelRect.y, btnCancelRect.width, btnCancelRect.height)
        shape.end()

        batch.begin()
        font.data.setScale(1.0f)
        font.color = if (canConfirm) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
        font.draw(batch, "保留所选", btnConfirmRect.x + 50f, btnConfirmRect.y + 28f)

        font.color = if (canCancel) Color.WHITE else Color(0.65f, 0.65f, 0.65f, 1f)
        font.draw(batch, "保留第一张", btnCancelRect.x + 40f, btnCancelRect.y + 28f)
        batch.end()
    }

    override fun dispose() {
        isDisposed = true
        model.cancel()
    }

    companion object {
        /** 入口、面板、预留提示与原因码翻译所需字形集合（供字体覆盖契约检查）。 */
        const val SLEEVE_BLOOM_CHARS: String =
            "袖里藏花·10他家回合预定二选一已消耗额度秒保留所选第一张牌山前后次张岭上" +
                "可和牌杠已选牌待本人下次普通摸牌未启用立直后禁止施展旧原因码庄家起手第不可当前无刚摸的" +
                "进行中本次摸牌过候选不足摸完或第四杠用没有选择已期局面变化失效请先完成" +
                "千术点数额度不足需思考时间超时决策变更非出牌玩家控制有动作正在处理连接会话关闭" +
                "控制代次过期阶段无法施术无有效暂停请求标识冲突偷梁换柱选择进行中"

        /**
         * 服务端业务拒绝原因翻译为标准中文说明。
         */
        fun translateServerReason(reason: String): String = when (reason) {
            "SLEEVE_BLOOM_DISABLED" -> "袖里藏花未启用"
            "SLEEVE_BLOOM_RIICHI_LOCKED" -> "立直后禁止施展（旧原因码）"
            "SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED" -> "庄家起手第 14 张不可施展"
            "SLEEVE_BLOOM_NO_DRAWN_TILE" -> "当前无刚摸的牌"
            "SLEEVE_BLOOM_ALREADY_PENDING" -> "二选一进行中"
            "SLEEVE_BLOOM_RESERVED_PENDING" -> "已预定，待本人下次普通摸牌"
            "SLEEVE_BLOOM_NO_WINDOW" -> "当前不在本人弃牌后的可预定窗口"
            "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW" -> "本次摸牌已施展过"
            "SLEEVE_BLOOM_WALL_EMPTY" -> "候选不足（牌山摸完或第四杠不可用）"
            "SLEEVE_BLOOM_NO_PENDING_OFFER" -> "没有进行中的二选一"
            "SLEEVE_BLOOM_OFFER_MISMATCH" -> "选择已过期"
            "SLEEVE_BLOOM_STALE_OFFER" -> "局面已变化，选择失效"
            "SLEEVE_BLOOM_CHOICE_PENDING" -> "请先完成袖里藏花二选一"
            "BEAM_SWAP_ALREADY_PENDING" -> "偷梁换柱选择进行中"
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
