package com.majiang.presentation.game

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.g2d.freetype.FreeTypeFontGenerator
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.GameSession
import com.majiang.client.api.GameSnapshotView
import com.majiang.client.api.ReceiptStatus
import com.majiang.client.api.ShufflePreparationStatusView
import com.majiang.client.api.ShufflePreparationView
import com.majiang.client.api.ShuffleTarget

/**
 * 洗牌千术准备阶段展示面板 (V4 presentation.game)。
 *
 * 核心契约：
 * 1. 独立表现层组件，拥有并暴露纯模型 [model] 与公共几何包围盒供单元测试；
 * 2. 消费统一 [GameSession]，提交 [ShufflePlan] 异步请求；
 * 3. 10 秒倒计时展示（权威 serverNow + 本地单调流逝时间，暂停冻结）；
 * 4. 偷天换日 (40点) 与 瞒天过海 (20点) 两大区域，各 6 个多选目标；
 * 5. 允许在点数不足时编辑草稿，但禁用确认并清晰提示原因；已确认方案不误报点数不足；
 * 6. 模态输入守卫：阻断下层牌桌点击穿透；断线、关闭、pending 态消费模态并阻断交互；
 * 7. 倒计时到期与暂停时阻断误点击；
 * 8. 资源生命周期严格自闭环 (Disposable)。
 */
class ShufflePreparationPanel(
    val session: GameSession
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
    val sectionInitialRect = Rectangle(200f, 345f, 880f, 185f)
    val sectionDrawRect = Rectangle(200f, 155f, 880f, 185f)

    val btnInitialTargets: Map<ShuffleTarget, Rectangle> = mapOf(
        ShuffleTarget.CHINITSU_MAN to Rectangle(220f, 435f, 260f, 38f),
        ShuffleTarget.CHINITSU_SOU to Rectangle(510f, 435f, 260f, 38f),
        ShuffleTarget.CHINITSU_PIN to Rectangle(800f, 435f, 260f, 38f),
        ShuffleTarget.DAISANGEN to Rectangle(220f, 385f, 260f, 38f),
        ShuffleTarget.KOKUSHI to Rectangle(510f, 385f, 260f, 38f),
        ShuffleTarget.DAISUUSHII to Rectangle(800f, 385f, 260f, 38f)
    )

    val btnDrawTargets: Map<ShuffleTarget, Rectangle> = mapOf(
        ShuffleTarget.CHINITSU_MAN to Rectangle(220f, 245f, 260f, 38f),
        ShuffleTarget.CHINITSU_SOU to Rectangle(510f, 245f, 260f, 38f),
        ShuffleTarget.CHINITSU_PIN to Rectangle(800f, 245f, 260f, 38f),
        ShuffleTarget.DAISANGEN to Rectangle(220f, 195f, 260f, 38f),
        ShuffleTarget.KOKUSHI to Rectangle(510f, 195f, 260f, 38f),
        ShuffleTarget.DAISUUSHII to Rectangle(800f, 195f, 260f, 38f)
    )

    val btnSkipRect = Rectangle(680f, 102f, 180f, 42f)
    val btnConfirmRect = Rectangle(880f, 102f, 180f, 42f)

    // 悬停状态
    var hoveredInitialTarget: ShuffleTarget? = null
        private set
    var hoveredDrawTarget: ShuffleTarget? = null
        private set
    var isConfirmHovered: Boolean = false
        private set
    var isSkipHovered: Boolean = false
        private set

    // 中文字体与字形管理
    private var chineseFont: BitmapFont? = null
    private var fontGenerator: FreeTypeFontGenerator? = null
    private var fontInitAttempted = false
    private val glyphLayout = GlyphLayout()

    companion object {
        private const val ASCII_AND_PUNCTUATION =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
            "※【】、（）√…·：；？！“”‘’—，|[]"

        val UI_STRINGS: List<String> = listOf(
            "洗牌千术",
            "可用点数: 点",
            "预计消耗: 点 (剩余: 点)",
            "调控起手配牌（13张手牌与开门摸牌）",
            "调控前8次顺位预计摸牌",
            "【偷天换日】 40点",
            "【瞒天过海】 20点",
            "[已启用 40点]",
            "[已启用 20点]",
            "[未启用 0点]",
            "清一色（万）",
            "清一色（条）",
            "清一色（筒）",
            "清一色（万条筒）",
            "大三元",
            "国士无双",
            "大四喜",
            "多选为混合倾向，不保证同时成牌",
            "吃碰杠会改变预期摸牌",
            "【已确认千术，等待开局发牌...】",
            "【正在提交计划...】",
            "【牌山生成中...】",
            "【对局已暂停】",
            "【准备时间已到】",
            "【网络未连接】",
            "【点数不足，无法确认】",
            "暂停",
            "生成中",
            "不使用千术",
            "确认出千",
            "已确认",
            "提交中...",
            "洗牌中...",
            "已暂停",
            "已截止",
            "未连接",
            "点数不足",
            "当前不支持该操作",
            "请求已超时",
            "操作不可用",
            "代次过期",
            "对局已变更",
            "连接已断开",
            "千术方案已确认",
            "请求不合法",
            "请求被拒绝",
            "网络连接异常",
            "请求被拒绝或异常",
            "提交异常",
            "洗牌千术偷天换日瞒天过海点数可用剩余预计消耗" +
            "清一色万条筒大三元国士无双四喜" +
            "多选为混合倾向不保证同时成牌吃碰杠会改变预期摸起手配顺前次张与开门调控" +
            "确认使用已锁定生成中不足无法秒等待倒计跳过拒绝异常成功计划" +
            "启未正在提交局发山被求出暂停错代次整过期操作连网络断服响变格" +
            "到超时对开备立直自荣和流庄听" +
            "字牌役满番符雀魂当前不支持合法" +
            "请该更接方案或准间截止"
        )

        @JvmField
        val SHUFFLE_PANEL_CHARS: String = (
            ASCII_AND_PUNCTUATION + UI_STRINGS.joinToString("")
        ).toSet().joinToString("")

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
        if (chineseFont != null) return chineseFont
        if (fontInitAttempted) return null
        fontInitAttempted = true

        if (Gdx.files == null || Gdx.gl == null || Gdx.graphics == null || Gdx.graphics.width == 0) {
            return null
        }

        try {
            val fontHandle = Gdx.files.internal("fonts/NotoSansSC-Regular.ttf")
            if (!fontHandle.exists()) {
                return null
            }
            val generator = FreeTypeFontGenerator(fontHandle)
            val param = FreeTypeFontGenerator.FreeTypeFontParameter().apply {
                size = 17
                characters = SHUFFLE_PANEL_CHARS
                incremental = false
            }
            chineseFont = generator.generateFont(param)
            fontGenerator = generator
            return chineseFont
        } catch (_: Throwable) {
            try {
                fontGenerator?.dispose()
            } catch (_: Throwable) {}
            fontGenerator = null
            chineseFont = null
            return null
        }
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

        // 1. 偷天换日目标点击
        for ((target, rect) in btnInitialTargets) {
            if (rect.contains(x, y)) {
                model.toggleInitialTarget(target)
                return true
            }
        }

        // 2. 瞒天过海目标点击
        for ((target, rect) in btnDrawTargets) {
            if (rect.contains(x, y)) {
                model.toggleDrawTarget(target)
                return true
            }
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
        hoveredInitialTarget = btnInitialTargets.entries.firstOrNull { it.value.contains(x, y) }?.key
        hoveredDrawTarget = btnDrawTargets.entries.firstOrNull { it.value.contains(x, y) }?.key
        isConfirmHovered = btnConfirmRect.contains(x, y)
        isSkipHovered = btnSkipRect.contains(x, y)
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

        // 偷天换日与瞒天过海两大分区底色
        shape.color = Color(0.09f, 0.18f, 0.13f, 0.85f)
        shape.rect(sectionInitialRect.x, sectionInitialRect.y, sectionInitialRect.width, sectionInitialRect.height)
        shape.rect(sectionDrawRect.x, sectionDrawRect.y, sectionDrawRect.width, sectionDrawRect.height)

        // 六大目标按钮底色（偷天换日）
        for ((target, rect) in btnInitialTargets) {
            val isSelected = model.initialTargets.contains(target)
            val isHover = (hoveredInitialTarget == target)
            shape.color = when {
                isSelected -> Color(0.18f, 0.42f, 0.26f, 0.95f)
                isHover -> Color(0.15f, 0.28f, 0.20f, 0.9f)
                else -> Color(0.10f, 0.20f, 0.14f, 0.8f)
            }
            shape.rect(rect.x, rect.y, rect.width, rect.height)
        }

        // 六大目标按钮底色（瞒天过海）
        for ((target, rect) in btnDrawTargets) {
            val isSelected = model.drawTargets.contains(target)
            val isHover = (hoveredDrawTarget == target)
            shape.color = when {
                isSelected -> Color(0.18f, 0.42f, 0.26f, 0.95f)
                isHover -> Color(0.15f, 0.28f, 0.20f, 0.9f)
                else -> Color(0.10f, 0.20f, 0.14f, 0.8f)
            }
            shape.rect(rect.x, rect.y, rect.width, rect.height)
        }

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
        shape.rect(sectionInitialRect.x, sectionInitialRect.y, sectionInitialRect.width, sectionInitialRect.height)
        shape.rect(sectionDrawRect.x, sectionDrawRect.y, sectionDrawRect.width, sectionDrawRect.height)

        // 按钮边框
        for ((target, rect) in btnInitialTargets) {
            val isSelected = model.initialTargets.contains(target)
            shape.color = if (isSelected) Color(0.40f, 0.95f, 0.55f, 1f) else Color(0.30f, 0.45f, 0.35f, 0.7f)
            shape.rect(rect.x, rect.y, rect.width, rect.height)
        }
        for ((target, rect) in btnDrawTargets) {
            val isSelected = model.drawTargets.contains(target)
            shape.color = if (isSelected) Color(0.40f, 0.95f, 0.55f, 1f) else Color(0.30f, 0.45f, 0.35f, 0.7f)
            shape.rect(rect.x, rect.y, rect.width, rect.height)
        }

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
            textFont.draw(batch, "洗牌千术", modalRect.x + 24f, modalRect.y + 595f)

            if (!isCustomFont) textFont.data.setScale(1.0f)
            textFont.color = Color(0.85f, 0.95f, 0.90f, 1f)
            val costText = "可用点数: ${model.availablePoints}点  预计消耗: ${model.totalCost}点 (剩余: ${model.remainingPointsPreview}点)"
            textFont.draw(batch, costText, modalRect.x + 160f, modalRect.y + 585f)

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

            // 3.2 偷天换日区域标题与提示
            textFont.color = Color(1.0f, 0.75f, 0.30f, 1f)
            textFont.draw(batch, "【偷天换日】 40点  调控起手配牌（13张手牌与开门摸牌）", sectionInitialRect.x + 16f, sectionInitialRect.y + 168f)

            // 3.3 瞒天过海区域标题与提示
            textFont.draw(batch, "【瞒天过海】 20点  调控前8次顺位预计摸牌", sectionDrawRect.x + 16f, sectionDrawRect.y + 168f)

            // 目标按钮文字
            for ((target, rect) in btnInitialTargets) {
                val isSelected = model.initialTargets.contains(target)
                textFont.color = if (isSelected) Color(0.4f, 1.0f, 0.6f, 1f) else Color.WHITE
                val name = targetDisplayName(target)
                val check = if (isSelected) "[√] " else "[ ] "
                textFont.draw(batch, check + name, rect.x + 12f, rect.y + 25f)
            }

            for ((target, rect) in btnDrawTargets) {
                val isSelected = model.drawTargets.contains(target)
                textFont.color = if (isSelected) Color(0.4f, 1.0f, 0.6f, 1f) else Color.WHITE
                val name = targetDisplayName(target)
                val check = if (isSelected) "[√] " else "[ ] "
                textFont.draw(batch, check + name, rect.x + 12f, rect.y + 25f)
            }

            // 提示文案
            if (model.hasMixedTargetsNotice) {
                textFont.color = Color(1.0f, 0.85f, 0.40f, 1f)
                textFont.draw(batch, "※ 多选为混合倾向，不保证同时成牌", sectionInitialRect.x + 20f, sectionInitialRect.y + 24f)
            }
            if (model.hasDrawNotice) {
                textFont.color = Color(1.0f, 0.85f, 0.40f, 1f)
                textFont.draw(batch, "※ 吃碰杠会改变预期摸牌", sectionDrawRect.x + 20f, sectionDrawRect.y + 24f)
            }

            // 底部按钮文字
            textFont.color = if (model.canSkip) Color.WHITE else Color(0.5f, 0.5f, 0.5f, 1f)
            textFont.draw(batch, "不使用千术", btnSkipRect.x + 40f, btnSkipRect.y + 27f)

            val confirmText = when {
                model.isAuthorityConfirmed || model.isLocallyConfirmed -> "已确认"
                model.isSubmitting -> "提交中..."
                model.status == ShufflePreparationStatusView.GENERATING -> "洗牌中..."
                !model.isAffordable -> "点数不足"
                else -> "确认出千"
            }
            textFont.color = when {
                model.isAuthorityConfirmed || model.isLocallyConfirmed -> Color(0.4f, 1.0f, 0.6f, 1f)
                model.canConfirm -> Color.WHITE
                else -> Color(0.55f, 0.55f, 0.55f, 1f)
            }
            glyphLayout.setText(textFont, confirmText)
            val cx = btnConfirmRect.x + (btnConfirmRect.width - glyphLayout.width) / 2f
            textFont.draw(batch, confirmText, cx, btnConfirmRect.y + 27f)

            // 错误/拒绝提示
            val err = model.errorMessage
            if (err != null) {
                textFont.color = Color(1.0f, 0.35f, 0.35f, 1f)
                textFont.draw(batch, err, modalRect.x + 20f, btnConfirmRect.y + 27f)
            }
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
        }

        batch.end()
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
        try {
            chineseFont?.dispose()
        } catch (_: Throwable) {}
        try {
            fontGenerator?.dispose()
        } catch (_: Throwable) {}
        chineseFont = null
        fontGenerator = null
    }
}
