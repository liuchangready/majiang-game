package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.GameSession
import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.ShufflePreparationStatusView
import com.majiang.client.api.ShufflePreparationView
import com.majiang.client.api.ShuffleTarget
import com.majiang.presentation.common.UiAssets

/**
 * 洗牌千术准备阶段展示面板 (V4 presentation.game)。
 *
 * 核心契约（统一码牌千术）：
 * 1. 独立表现层组件，拥有并暴露纯模型 [model] 与公共几何包围盒供单元测试；
 * 2. 消费统一 [GameSession]，提交 [com.majiang.client.api.ShufflePlan] 异步请求；
 * 3. 10 秒倒计时展示（权威 serverNow + 本地单调流逝时间，暂停冻结）；
 * 4. 只显示一个六选一目标区（30 点，同时作用于起手与全部正常预计摸牌槽），
 *    以及可叠加的看破（10 点），合计最高 40 点；仅看破或跳过也是合法方案；
 * 5. 允许在点数不足时编辑草稿，但禁用确认并清晰提示原因；已确认方案不误报点数不足；
 * 6. 模态输入守卫：阻断下层牌桌点击穿透；断线、关闭、pending 态消费模态并阻断交互；
 * 7. 倒计时到期与暂停时阻断误点击；
 * 8. 资源生命周期严格自闭环 (Disposable)。
 */
class ShufflePreparationPanel @JvmOverloads constructor(
    val session: GameSession,
    private val assets: UiAssets? = null
) : Disposable {

    /** 纯表现层状态模型（供无头测试与展示共享） */
    val model: ShufflePreparationModel = ShufflePreparationModel()

    /** 当前准备弹窗是否处于活动状态 */
    val isActive: Boolean get() = model.isActive

    /** 面板是否已被释放 */
    var isDisposed: Boolean = false
        private set

    // 几何包围盒契约 (1280x720 虚拟相机坐标系)
    val modalRect = Rectangle(180f, 90f, 920f, 540f)
    val countdownBoxRect = Rectangle(960f, 554f, 110f, 50f)
    val sectionTargetRect = Rectangle(200f, 175f, 880f, 355f)

    /** 六选一统一目标按钮：一次只能选择一个，再次点击取消。 */
    val btnTargets: Map<ShuffleTarget, Rectangle> = mapOf(
        ShuffleTarget.CHINITSU_MAN to Rectangle(220f, 465f, 260f, 38f),
        ShuffleTarget.CHINITSU_SOU to Rectangle(510f, 465f, 260f, 38f),
        ShuffleTarget.CHINITSU_PIN to Rectangle(800f, 465f, 260f, 38f),
        ShuffleTarget.DAISANGEN to Rectangle(220f, 410f, 260f, 38f),
        ShuffleTarget.KOKUSHI to Rectangle(510f, 410f, 260f, 38f),
        ShuffleTarget.DAISUUSHII to Rectangle(800f, 410f, 260f, 38f)
    )

    val btnSeeThroughRect = Rectangle(200f, 102f, 300f, 42f)
    val btnSkipRect = Rectangle(520f, 102f, 170f, 42f)
    val btnConfirmRect = Rectangle(710f, 102f, 180f, 42f)

    // 悬停状态
    var hoveredTarget: ShuffleTarget? = null
        private set
    var isConfirmHovered: Boolean = false
        private set
    var isSkipHovered: Boolean = false
        private set
    var isSeeThroughHovered: Boolean = false
        private set

    // 中文字体与字形管理（借用 UiAssets 管理的 17px 共享字体与生命周期）
    private val glyphLayout = GlyphLayout()

    companion object {
        fun translateServerReason(reason: String?): String {
            if (reason.isNullOrBlank()) return "请求被拒绝"
            val trimmed = reason.trim()
            return when {
                trimmed.contains("UNSUPPORTED", ignoreCase = true) -> "当前不支持该操作"
                trimmed.contains("TIMEOUT", ignoreCase = true) || trimmed.contains("TIMED_OUT", ignoreCase = true) -> "请求已超时"
                trimmed.contains("INSUFFICIENT", ignoreCase = true) -> "点数不足"
                trimmed.contains("NOT_AVAILABLE", ignoreCase = true) -> "操作不可用"
                trimmed.contains("STALE", ignoreCase = true) || trimmed.contains("EPOCH", ignoreCase = true) -> "代次过期"
                trimmed.contains("ROUND", ignoreCase = true) -> "对局已变更"
                trimmed.contains("DISCONNECTED", ignoreCase = true) || trimmed.contains("CLOSED", ignoreCase = true) -> "连接已断开"
                trimmed.contains("ALREADY", ignoreCase = true) -> "千术方案已确认"
                trimmed.contains("ILLEGAL", ignoreCase = true) || trimmed.contains("INVALID", ignoreCase = true) -> "请求不合法"
                trimmed.contains("REJECT", ignoreCase = true) -> "请求被拒绝"
                trimmed.contains("网络") || trimmed.contains("断开") -> "网络连接异常"
                trimmed.contains("超时") -> "请求已超时"
                trimmed.contains("点数") -> "点数不足"
                trimmed.contains("拒绝") -> "请求被拒绝"
                else -> "请求被拒绝或异常"
            }
        }
    }

    fun getOrInitChineseFont(): BitmapFont? {
        val uiAssets = assets ?: UiAssets.activeInstance
        return uiAssets?.chineseFont17 ?: uiAssets?.chineseFont
    }

    fun update(snapshot: GameSnapshotView) {
        model.update(snapshot)
    }

    fun update(prep: ShufflePreparationView?, paused: Boolean = false, epoch: Long = 0L) {
        model.update(prep, paused, epoch)
    }

    fun touchDown(x: Float, y: Float): Boolean {
        if (!isActive) return false

        // 模态弹窗点击守卫：吞掉弹窗区域及整个屏幕所有点击，防止穿透
        if (!modalRect.contains(x, y)) {
            return true
        }

        if (!model.canEditDraft && !model.canConfirm && !model.canSkip) {
            return true
        }

        // 1. 统一目标单选点击
        for ((target, rect) in btnTargets) {
            if (rect.contains(x, y)) {
                model.selectTarget(target)
                return true
            }
        }

        // 2. 看破开关（仅启用房间可切换，可与统一目标叠加）
        if (btnSeeThroughRect.contains(x, y)) {
            model.toggleSeeThrough()
            return true
        }

        // 3. 确认按钮
        if (btnConfirmRect.contains(x, y)) {
            if (model.canConfirm) {
                val req = model.buildPlanRequest()
                if (req != null) {
                    model.markSubmitting(req.requestId)
                    session.submitShufflePlan(req).whenComplete { receipt, error ->
                        if (error != null) {
                            model.onSubmissionRejected(req.requestId, translateServerReason(error.message))
                        } else if (receipt != null) {
                            if (receipt.status == ReceiptStatus.APPLIED || receipt.status == ReceiptStatus.RECORDED) {
                                model.onSubmissionAccepted(req.requestId, req.selection)
                            } else {
                                model.onSubmissionRejected(req.requestId, translateServerReason(receipt.reason))
                            }
                        }
                    }
                }
            }
            return true
        }

        // 4. 跳过按钮
        if (btnSkipRect.contains(x, y)) {
            if (model.canSkip) {
                val req = model.buildSkipRequest()
                if (req != null) {
                    model.markSubmitting(req.requestId)
                    session.submitShufflePlan(req).whenComplete { receipt, error ->
                        if (error != null) {
                            model.onSubmissionRejected(req.requestId, translateServerReason(error.message))
                        } else if (receipt != null) {
                            if (receipt.status == ReceiptStatus.APPLIED || receipt.status == ReceiptStatus.RECORDED) {
                                model.onSubmissionAccepted(req.requestId, req.selection)
                            } else {
                                model.onSubmissionRejected(req.requestId, translateServerReason(receipt.reason))
                            }
                        }
                    }
                }
            }
            return true
        }

        return true
    }

    fun mouseMoved(x: Float, y: Float) {
        if (!isActive) return
        hoveredTarget = btnTargets.entries.firstOrNull { it.value.contains(x, y) }?.key
        isConfirmHovered = btnConfirmRect.contains(x, y)
        isSkipHovered = btnSkipRect.contains(x, y)
        isSeeThroughHovered = btnSeeThroughRect.contains(x, y)
    }

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        fallbackFont: BitmapFont
    ) {
        if (!isActive) return

        val textFont = getOrInitChineseFont() ?: fallbackFont
        val isCustomFont = (textFont !== fallbackFont)

        // 1. 全屏半透明遮罩与弹窗背景
        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 全屏深色遮罩
        shape.color = Color(0f, 0f, 0f, 0.75f)
        shape.rect(0f, 0f, 1280f, 720f)

        // 弹窗底色
        shape.color = Color(0.06f, 0.12f, 0.08f, 0.98f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        // 倒计时独立大背景框
        val remSec = model.remainingSeconds()
        shape.color = if (remSec <= 3L && remSec > 0L) Color(0.35f, 0.08f, 0.08f, 0.9f) else Color(0.12f, 0.22f, 0.16f, 0.9f)
        shape.rect(countdownBoxRect.x, countdownBoxRect.y, countdownBoxRect.width, countdownBoxRect.height)

        // 统一目标分区底色
        shape.color = Color(0.09f, 0.18f, 0.13f, 0.85f)
        shape.rect(sectionTargetRect.x, sectionTargetRect.y, sectionTargetRect.width, sectionTargetRect.height)

        // 六大统一目标按钮底色
        for ((target, rect) in btnTargets) {
            val isSelected = model.target == target
            val isHover = (hoveredTarget == target)
            shape.color = when {
                isSelected -> Color(0.18f, 0.42f, 0.26f, 0.95f)
                isHover -> Color(0.15f, 0.28f, 0.20f, 0.9f)
                else -> Color(0.10f, 0.20f, 0.14f, 0.8f)
            }
            shape.rect(rect.x, rect.y, rect.width, rect.height)
        }

        // 看破开关底色（禁用房间灰显，选中绿色）
        shape.color = when {
            !model.seeThroughEnabled -> Color(0.12f, 0.14f, 0.13f, 0.55f)
            model.isSeeThroughSelected -> Color(0.20f, 0.48f, 0.30f, 0.98f)
            isSeeThroughHovered && model.canEditDraft -> Color(0.16f, 0.30f, 0.22f, 0.95f)
            else -> Color(0.10f, 0.20f, 0.14f, 0.9f)
        }
        shape.rect(btnSeeThroughRect.x, btnSeeThroughRect.y, btnSeeThroughRect.width, btnSeeThroughRect.height)

        // 底部按钮底色 (跳过 / 确认)
        shape.color = if (model.canSkip) {
            if (isSkipHovered) Color(0.30f, 0.35f, 0.32f, 1f) else Color(0.22f, 0.26f, 0.24f, 1f)
        } else {
            Color(0.15f, 0.18f, 0.16f, 0.5f)
        }
        shape.rect(btnSkipRect.x, btnSkipRect.y, btnSkipRect.width, btnSkipRect.height)

        shape.color = when {
            model.isAuthorityConfirmed || model.isLocallyConfirmed -> Color(0.20f, 0.45f, 0.28f, 1f)
            model.canConfirm -> if (isConfirmHovered) Color(0.85f, 0.50f, 0.18f, 1f) else Color(0.75f, 0.40f, 0.15f, 1f)
            else -> Color(0.25f, 0.25f, 0.25f, 0.5f)
        }
        shape.rect(btnConfirmRect.x, btnConfirmRect.y, btnConfirmRect.width, btnConfirmRect.height)

        shape.end()

        // 2. 边框绘制
        shape.begin(ShapeRenderer.ShapeType.Line)
        shape.color = Color(0.85f, 0.72f, 0.35f, 0.95f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        // 倒计时外框
        shape.color = if (remSec <= 3L && remSec > 0L) Color(0.95f, 0.3f, 0.3f, 1f) else Color(0.40f, 0.85f, 0.55f, 1f)
        shape.rect(countdownBoxRect.x, countdownBoxRect.y, countdownBoxRect.width, countdownBoxRect.height)

        // 分区外框
        shape.color = Color(0.35f, 0.55f, 0.42f, 0.6f)
        shape.rect(sectionTargetRect.x, sectionTargetRect.y, sectionTargetRect.width, sectionTargetRect.height)

        // 按钮边框
        for ((target, rect) in btnTargets) {
            val isSelected = model.target == target
            shape.color = if (isSelected) Color(0.40f, 0.95f, 0.55f, 1f) else Color(0.30f, 0.45f, 0.35f, 0.7f)
            shape.rect(rect.x, rect.y, rect.width, rect.height)
        }

        shape.color = when {
            !model.seeThroughEnabled -> Color(0.30f, 0.32f, 0.30f, 0.55f)
            model.isSeeThroughSelected -> Color(0.45f, 0.95f, 0.60f, 1f)
            else -> Color(0.34f, 0.52f, 0.40f, 0.85f)
        }
        shape.rect(btnSeeThroughRect.x, btnSeeThroughRect.y, btnSeeThroughRect.width, btnSeeThroughRect.height)

        shape.color = if (model.canSkip) Color.LIGHT_GRAY else Color(0.4f, 0.4f, 0.4f, 0.4f)
        shape.rect(btnSkipRect.x, btnSkipRect.y, btnSkipRect.width, btnSkipRect.height)

        shape.color = when {
            model.isAuthorityConfirmed || model.isLocallyConfirmed -> Color(0.5f, 1.0f, 0.6f, 1f)
            model.canConfirm -> Color.WHITE
            else -> Color(0.45f, 0.45f, 0.45f, 0.4f)
        }
        shape.rect(btnConfirmRect.x, btnConfirmRect.y, btnConfirmRect.width, btnConfirmRect.height)

        shape.end()

        // 3. 绘制文字内容
        batch.projectionMatrix = camera.combined
        batch.begin()

        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY
        val origColor = Color(textFont.color)

        try {
            // 3.1 弹窗大标题与点数预算
            if (!isCustomFont) textFont.data.setScale(1.4f)
            textFont.color = Color(1.0f, 0.85f, 0.35f, 1f)
            textFont.draw(batch, "洗牌千术（统一码牌）", modalRect.x + 24f, modalRect.y + 595f)

            if (!isCustomFont) textFont.data.setScale(1.0f)
            textFont.color = Color(0.85f, 0.95f, 0.90f, 1f)
            val costText = "可用点数: ${model.availablePoints}点  预计消耗: ${model.totalCost}点 (剩余: ${model.remainingPointsPreview}点)"
            textFont.draw(batch, costText, modalRect.x + 270f, modalRect.y + 585f)

            // 看破规则说明：仅正确生效，不暗示抓包或永久封锁。
            textFont.color = Color(0.78f, 0.86f, 0.82f, 1f)
            textFont.draw(
                batch,
                "看破10点（合计上限40）：抵消对手本局全部码牌并清除其既有落汗；双方费用照常支付。",
                modalRect.x + 20f,
                modalRect.y + 558f
            )

            // 倒计时文字
            val timerText = when {
                model.isPaused -> "暂停"
                model.status == ShufflePreparationStatusView.GENERATING -> "生成中"
                remSec <= 0L -> "已截止"
                else -> "${remSec}s"
            }
            if (!isCustomFont) textFont.data.setScale(1.3f)
            textFont.color = if (remSec <= 3L && remSec > 0L) Color(1.0f, 0.4f, 0.4f, 1f) else Color(0.40f, 1.0f, 0.65f, 1f)
            glyphLayout.setText(textFont, timerText)
            val tx = countdownBoxRect.x + (countdownBoxRect.width - glyphLayout.width) / 2f
            val ty = countdownBoxRect.y + (countdownBoxRect.height + glyphLayout.height) / 2f
            textFont.draw(batch, timerText, tx, ty)

            if (!isCustomFont) textFont.data.setScale(1.0f)

            // 3.2 统一目标区域标题与提示
            textFont.color = Color(1.0f, 0.75f, 0.30f, 1f)
            textFont.draw(
                batch,
                "【统一目标】 30点  起手配牌 + 本座位 17/18 个预计摸牌槽，只能选择一个目标",
                sectionTargetRect.x + 16f,
                sectionTargetRect.y + 328f
            )

            // 目标按钮文字
            for ((target, rect) in btnTargets) {
                val isSelected = model.target == target
                textFont.color = if (isSelected) Color(0.4f, 1.0f, 0.6f, 1f) else Color.WHITE
                val name = targetDisplayName(target)
                val check = if (isSelected) "[●] " else "[ ] "
                textFont.draw(batch, check + name, rect.x + 12f, rect.y + 25f)
            }

            // 提示文案
            textFont.color = Color(1.0f, 0.85f, 0.40f, 1f)
            textFont.draw(
                batch,
                "※ 同一目标贯穿起手与摸牌；吃碰杠、杠牌或提前终局会改变预期摸牌，不保证每次摸牌必得目标牌。",
                sectionTargetRect.x + 20f,
                sectionTargetRect.y + 40f
            )

            // 看破开关文字
            val seeThroughText = when {
                !model.seeThroughEnabled -> "看破未启用"
                model.isSeeThroughSelected -> "[●] 看破 ${model.seeThroughPrice}点"
                else -> "[ ] 看破 ${model.seeThroughPrice}点"
            }
            textFont.color = when {
                !model.seeThroughEnabled -> Color(0.48f, 0.50f, 0.48f, 1f)
                model.isSeeThroughSelected -> Color(0.55f, 1.0f, 0.68f, 1f)
                else -> Color.WHITE
            }
            textFont.draw(batch, seeThroughText, btnSeeThroughRect.x + 12f, btnSeeThroughRect.y + 27f)

            // 底部按钮文字
            textFont.color = if (model.canSkip) Color.WHITE else Color(0.5f, 0.5f, 0.5f, 1f)
            textFont.draw(batch, "不使用千术", btnSkipRect.x + 40f, btnSkipRect.y + 27f)

            val confirmText = when {
                model.status == ShufflePreparationStatusView.GENERATING -> "正在发牌..."
                model.isSubmitting -> "提交中..."
                model.isAuthorityConfirmed || model.isLocallyConfirmed -> "等待其他玩家"
                !model.isAffordable -> "点数不足"
                else -> "确认出千"
            }
            textFont.color = when {
                model.status == ShufflePreparationStatusView.GENERATING -> Color(0.55f, 1.0f, 0.68f, 1f)
                model.isAuthorityConfirmed || model.isLocallyConfirmed -> Color(0.4f, 1.0f, 0.6f, 1f)
                model.canConfirm -> Color.WHITE
                else -> Color(0.55f, 0.55f, 0.55f, 1f)
            }
            glyphLayout.setText(textFont, confirmText)
            val cx = btnConfirmRect.x + (btnConfirmRect.width - glyphLayout.width) / 2f
            textFont.draw(batch, confirmText, cx, btnConfirmRect.y + 27f)

            // 已确认后的准确阶段反馈：等待其他玩家 / 正在发牌；遮罩只随权威快照解除。
            model.confirmedFeedbackText?.let { feedback ->
                textFont.color = if (model.status == ShufflePreparationStatusView.GENERATING) {
                    Color(0.55f, 1.0f, 0.68f, 1f)
                } else {
                    Color(1.0f, 0.85f, 0.40f, 1f)
                }
                textFont.draw(batch, feedback, modalRect.x + 20f, modalRect.y + 76f)
            }

            // 错误/拒绝提示（覆盖规则提示行，避免与底部按钮重叠）
            val err = model.errorMessage
            if (err != null) {
                textFont.color = Color(1.0f, 0.35f, 0.35f, 1f)
                textFont.draw(batch, err, modalRect.x + 20f, modalRect.y + 558f)
            }
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
            if (batch.isDrawing) {
                batch.end()
            }
        }
    }

    private fun targetDisplayName(target: ShuffleTarget): String = when (target) {
        ShuffleTarget.CHINITSU_MAN -> "清一色（万）"
        ShuffleTarget.CHINITSU_SOU -> "清一色（条）"
        ShuffleTarget.CHINITSU_PIN -> "清一色（筒）"
        ShuffleTarget.DAISANGEN -> "大三元"
        ShuffleTarget.KOKUSHI -> "国士无双"
        ShuffleTarget.DAISUUSHII -> "大四喜"
    }

    override fun dispose() {
        if (isDisposed) return
        isDisposed = true
        // 借用 UiAssets 管理的字体与生命周期，组件不自行释放共享生成器与共享字体
    }
}
