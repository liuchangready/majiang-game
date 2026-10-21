package com.majiang.presentation.menu

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Matrix4
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.TimeUtils

/**
 * SpriteBatch.end() 会关闭 GL_BLEND。任何在本帧稍后使用 ShapeRenderer 绘制半透明
 * 遮罩/光晕/弹窗的代码都必须在自己的 begin() 前重新声明标准 alpha 混合，不能假设
 * 上一段 SpriteBatch 的混合状态仍然有效。
 */
internal fun enableMenuAlphaBlending() {
    val gl = Gdx.gl ?: return
    gl.glEnable(GL20.GL_BLEND)
    gl.glBlendEquation(GL20.GL_FUNC_ADD)
    gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)
}

/**
 * 正式 APP 主菜单视图组件（L1..L7 中的菜单内容层）。
 *
 * 职责与边界：
 * 1. 只做展示与本地菜单交互态（入场、hover、pressed、focus），不持有 LobbySession、
 *    网络、计时权威或领域状态；六个入口仍通过原有回调上报父层。
 * 2. 所有绘制/命中都读取同一个 [MainMenuFrame]，未落位入口绝不接受激活。
 * 3. 资源通过 [MainMenuAssets] 借用；纹理归 UiAssets 所有，View 不 dispose、不每帧加载。
 * 4. 绘制结束恢复 batch/font/projection，避免污染大厅与弹窗。
 *
 * [motionEnabled] 可注入：false 时直接进入最终落位态，立即可操作；正式 APP 当前没有
 * 系统“减少动态效果”开关，此参数只用于测试与未来表现层配置，不冒充已有设置。
 */
class MainMenuView(
    private val onPlaySingle: () -> Unit,
    private val onOpenLobby: () -> Unit,
    private val onToggleSkin: () -> Unit,
    private val onSecrets: () -> Unit,
    private val onSettings: () -> Unit,
    private val onExit: () -> Unit,
    private val motionEnabled: Boolean = true,
    private val clock: () -> Long = { TimeUtils.millis() },
    private val assetsProvider: () -> MainMenuAssets? = { null }
) {
    // 公开的最终落位矩形：兼容既有测试与外部只读使用。
    // 动画中间矩形请读取 currentFrame/itemRect；命中同样使用该帧矩形。
    val btnPlaySingle = Rectangle(MainMenuLayout.spec(MainMenuItemId.SOLO).baseRect)
    val btnOpenLobby = Rectangle(MainMenuLayout.spec(MainMenuItemId.ONLINE).baseRect)
    val btnSkin = Rectangle(MainMenuLayout.spec(MainMenuItemId.SKIN).baseRect)
    val btnSecrets = Rectangle(MainMenuLayout.spec(MainMenuItemId.SECRETS).baseRect)
    val btnSettings = Rectangle(MainMenuLayout.spec(MainMenuItemId.SETTINGS).baseRect)
    val btnExit = Rectangle(MainMenuLayout.spec(MainMenuItemId.EXIT).baseRect)

    private val glyphLayout = GlyphLayout()

    private var frame: MainMenuFrame = MainMenuLayout.frame(MainMenuLayout.TOTAL_ENTRY_MS, motionEnabled = true)
    private var entryStartMs: Long? = null
    private var entryActive = false
    private var pausedAtMs: Long? = null
    private var focusIndex = -1
    private var hoveredId: MainMenuItemId? = null
    private var pressedId: MainMenuItemId? = null
    private var pointerX = Float.NaN
    private var pointerY = Float.NaN
    private var disabledItems: Set<MainMenuItemId> = emptySet()

    // 激活反馈：点击/键盘确认后至少保留一小段高亮，避免“按下即抬起”时整帧看不到 pressed。
    // 该反馈只影响绘制，不延迟任何业务回调。
    private var activationFlashId: MainMenuItemId? = null
    private var activationFlashUntilMs = 0L

    /** 仅供测试/诊断读取的当前激活高亮项。 */
    internal val activationFlashItemId: MainMenuItemId? get() = activationFlashId

    /** 仅供测试/诊断读取的入场重播计数。 */
    var entryPlayCount: Int = 0
        private set

    val isEntryActive: Boolean get() = entryActive
    val currentFrame: MainMenuFrame get() = frame
    val focusedItemId: MainMenuItemId? get() = frame.items.getOrNull(focusIndex)?.id
    val hoveredItemId: MainMenuItemId? get() = hoveredId
    val pressedItemId: MainMenuItemId? get() = pressedId

    fun rectFor(id: MainMenuItemId): Rectangle = Rectangle(MainMenuLayout.spec(id).baseRect)

    fun itemRect(id: MainMenuItemId): Rectangle? = frame.item(id)?.let { Rectangle(it.rect) }

    fun isItemSettled(id: MainMenuItemId): Boolean = frame.item(id)?.settled == true

    fun isItemEnabled(id: MainMenuItemId): Boolean = id !in disabledItems

    /** 首次 show 调用；同一 MenuScreen 内重复调用不会重播。 */
    fun beginEntryIfNeeded(nowMs: Long = clock()) {
        if (entryStartMs == null) beginEntry(nowMs)
    }

    /** 从大厅/对局返回 MAIN 时调用一次；每次重播重新计时。 */
    fun beginEntry(nowMs: Long = clock()) {
        entryStartMs = nowMs
        pausedAtMs = null
        entryPlayCount++
        focusIndex = -1
        hoveredId = null
        pressedId = null
        activationFlashId = null
        if (motionEnabled) {
            entryActive = true
            applyElapsed(0L)
        } else {
            entryActive = false
            applyElapsed(MainMenuLayout.TOTAL_ENTRY_MS)
            focusFirstEnabled()
        }
    }

    /** 覆盖弹窗关闭后恢复焦点，不触发重播。 */
    fun restoreFocus(id: MainMenuItemId?) {
        update()
        if (id != null) {
            val index = frame.items.indexOfFirst { it.id == id }
            if (index >= 0 && isFocusable(frame.items[index])) {
                focusIndex = index
                return
            }
        }
        focusFirstEnabled()
    }

    fun clearFocus() {
        focusIndex = -1
    }

    fun onPause(nowMs: Long = clock()) {
        if (entryActive) pausedAtMs = nowMs
    }

    fun onResume(nowMs: Long = clock()) {
        val pausedAt = pausedAtMs ?: return
        pausedAtMs = null
        val start = entryStartMs ?: return
        if (entryActive) entryStartMs = start + (nowMs - pausedAt).coerceAtLeast(0L)
    }

    fun update(nowMs: Long = clock()) {
        if (activationFlashId != null && nowMs >= activationFlashUntilMs) {
            activationFlashId = null
        }
        val elapsed = entryStartMs?.let { (nowMs - it).coerceAtLeast(0L) }
            ?: MainMenuLayout.TOTAL_ENTRY_MS
        applyElapsed(elapsed)
    }

    private fun applyElapsed(elapsedMs: Long) {
        frame = MainMenuLayout.frame(elapsedMs, motionEnabled)
        entryActive = motionEnabled && elapsedMs < MainMenuLayout.TOTAL_ENTRY_MS
        if (!entryActive && focusIndex < 0 && frame.allSettled) {
            focusFirstEnabled()
        }
        refreshHover()
    }

    private fun refreshHover() {
        hoveredId = if (pointerX.isNaN() || pointerY.isNaN()) {
            null
        } else {
            frame.hitTest(pointerX, pointerY)?.takeIf { isItemEnabled(it) }
        }
        if (pressedId != null && pressedId != hoveredId) {
            // 指针离开按下矩形后取消 press；保持命中几何稳定。
            pressedId = null
        }
    }

    private fun isFocusable(item: MainMenuItemFrame): Boolean =
        item.settled && isItemEnabled(item.id)

    private fun focusFirstEnabled() {
        focusIndex = frame.items.indexOfFirst { isFocusable(it) }
    }

    fun focusNext(): Boolean {
        update()
        if (!frame.items.any { isFocusable(it) }) {
            focusIndex = -1
            return false
        }
        val start = focusIndex
        for (step in 1..frame.items.size) {
            val index = if (start < 0) step - 1 else (start + step) % frame.items.size
            if (isFocusable(frame.items[index])) {
                focusIndex = index
                return true
            }
        }
        return false
    }

    fun focusPrevious(): Boolean {
        update()
        if (!frame.items.any { isFocusable(it) }) {
            focusIndex = -1
            return false
        }
        val start = if (focusIndex < 0) 0 else focusIndex
        for (step in 1..frame.items.size) {
            val index = (start - step + frame.items.size) % frame.items.size
            if (isFocusable(frame.items[index])) {
                focusIndex = index
                return true
            }
        }
        return false
    }

    fun activateFocused(): Boolean {
        update()
        val item = frame.items.getOrNull(focusIndex) ?: return false
        return activate(item.id)
    }

    fun activate(id: MainMenuItemId): Boolean {
        update()
        val item = frame.item(id) ?: return false
        if (!item.settled || !isItemEnabled(id)) return false
        // 先给出本地即时反馈，再同步触发原业务回调；动画/高亮不等待网络或建桌。
        activationFlashId = id
        activationFlashUntilMs = clock() + ACTIVATION_FLASH_MS
        when (id) {
            MainMenuItemId.SOLO -> onPlaySingle()
            MainMenuItemId.ONLINE -> onOpenLobby()
            MainMenuItemId.SKIN -> onToggleSkin()
            MainMenuItemId.SECRETS -> onSecrets()
            MainMenuItemId.SETTINGS -> onSettings()
            MainMenuItemId.EXIT -> onExit()
        }
        return true
    }

    fun pointerMoved(x: Float, y: Float) {
        pointerX = x
        pointerY = y
        update()
        refreshHover()
    }

    fun pointerDown(x: Float, y: Float): Boolean {
        pointerX = x
        pointerY = y
        update()
        refreshHover()
        val hit = frame.hitTest(x, y)?.takeIf { isItemEnabled(it) } ?: return false
        pressedId = hit
        focusIndex = frame.items.indexOfFirst { it.id == hit }
        return true
    }

    fun pointerUp(x: Float, y: Float): Boolean {
        pointerX = x
        pointerY = y
        update()
        val pressed = pressedId ?: return false
        pressedId = null
        val hit = frame.hitTest(x, y)
        return hit == pressed && activate(pressed)
    }

    fun pointerDragged(x: Float, y: Float) {
        pointerX = x
        pointerY = y
        update()
        refreshHover()
        if (pressedId != null && frame.hitTest(x, y) != pressedId) {
            pressedId = null
        }
    }

    fun cancelPress() {
        pressedId = null
    }

    /**
     * 兼容旧调用：按下即激活。新输入路径使用 [pointerDown]/[pointerUp]，
     * 二者共享同一帧矩形与落位门禁。
     */
    fun handleTouch(tx: Float, ty: Float): Boolean {
        update()
        val hit = frame.hitTest(tx, ty) ?: return false
        return activate(hit)
    }

    fun setDisabledItems(ids: Set<MainMenuItemId>) {
        disabledItems = ids.toSet()
        if (pressedId != null && pressedId in disabledItems) pressedId = null
        if (focusedItemId != null && focusedItemId in disabledItems) {
            focusIndex = -1
            focusFirstEnabled()
        }
        refreshHover()
    }

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        font: BitmapFont,
        currentSkinName: String,
        statusMessage: String?,
        errorMessage: String?,
        titleFont: BitmapFont = font,
        primaryFont: BitmapFont = font,
        secondaryFont: BitmapFont = font,
        statusFont: BitmapFont = font,
        disabledItems: Set<MainMenuItemId> = emptySet()
    ) {
        update()
        setDisabledItems(disabledItems)
        val assets = assetsProvider()
        val fonts = listOf(font, titleFont, primaryFont, secondaryFont, statusFont).distinct()
        val fontStates = fonts.map { FontState(it, Color(it.color), it.data.scaleX, it.data.scaleY) }
        val priorBatchColor = Color(batch.color)
        val priorBatchProjection = Matrix4(batch.projectionMatrix)
        val priorShapeProjection = Matrix4(shape.projectionMatrix)

        try {
            // 调用方可能刚结束上一段 SpriteBatch；进入 ShapeRenderer 前必须恢复混合。
            enableMenuAlphaBlending()
            shape.projectionMatrix = camera.combined
            shape.begin(ShapeRenderer.ShapeType.Filled)
            try {
                drawReadingMasks(shape)
                drawAmbientLight(shape)
                drawStatusBar(shape, statusMessage, errorMessage)
                if (assets == null) {
                    drawFallbackPanels(shape)
                }
            } finally {
                shape.end()
            }

            batch.projectionMatrix = camera.combined
            batch.begin()
            try {
                batch.color = Color.WHITE
                assets?.let { drawMascot(batch, it) }
                drawTitles(batch, titleFont, secondaryFont, assets)
                drawItems(batch, assets, primaryFont, secondaryFont, statusFont, currentSkinName)
                drawStatusText(batch, statusFont, statusMessage, errorMessage)
            } finally {
                batch.end()
            }
        } finally {
            batch.color = priorBatchColor
            batch.projectionMatrix = priorBatchProjection
            shape.projectionMatrix = priorShapeProjection
            fontStates.forEach { state ->
                state.font.color = state.color
                state.font.data.setScale(state.scaleX, state.scaleY)
            }
        }
    }

    private fun drawReadingMasks(shape: ShapeRenderer) {
        val transparent = Color(0f, 0f, 0f, 0f)
        val darkLeft = Color(0.01f, 0.035f, 0.03f, 0.58f)
        val darkRight = Color(0.01f, 0.03f, 0.025f, 0.46f)
        val darkBottom = Color(0.01f, 0.03f, 0.025f, 0.42f)
        val darkTop = Color(0.01f, 0.03f, 0.025f, 0.30f)

        // 左侧标题/吉祥物阅读区：只压暗左侧，不全屏压黑。
        shape.rect(0f, 0f, 660f, 720f, darkLeft, transparent, transparent, darkLeft)
        // 右侧菜单区。
        shape.rect(640f, 0f, 640f, 720f, transparent, darkRight, darkRight, transparent)
        // 上下边缘暗角。
        shape.rect(0f, 0f, 1280f, 92f, darkBottom, darkBottom, transparent, transparent)
        shape.rect(0f, 628f, 1280f, 92f, transparent, transparent, darkTop, darkTop)
    }

    private fun drawAmbientLight(shape: ShapeRenderer) {
        val pulse = 0.5f + 0.5f * kotlin.math.sin((frame.elapsedMs / 2400f).toDouble()).toFloat()
        shape.color = Color(0.15f, 0.48f, 0.36f, 0.07f + 0.015f * pulse)
        shape.circle(350f, 300f, 258f, 48)
        shape.color = Color(0.96f, 0.74f, 0.31f, 0.055f)
        shape.circle(1080f, 430f, 290f, 48)
        shape.color = Color(0.96f, 0.74f, 0.31f, 0.035f)
        shape.circle(1070f, 220f, 220f, 40)
    }

    private fun drawFallbackPanels(shape: ShapeRenderer) {
        for (item in frame.items) {
            if (item.alpha <= 0f) continue
            shape.color = when {
                item.id.primary -> Color(0.58f, 0.16f, 0.12f, item.alpha)
                else -> Color(0.08f, 0.25f, 0.20f, item.alpha)
            }
            shape.rect(item.rect.x, item.rect.y, item.rect.width, item.rect.height)
            if (isFocusedOrHovered(item.id)) {
                val border = if (focusedItemId == item.id) 2.5f else 1.5f
                shape.color = Color(0.94f, 0.77f, 0.39f, item.alpha * 0.9f)
                shape.rect(item.rect.x, item.rect.y, item.rect.width, border)
                shape.rect(item.rect.x, item.rect.y + item.rect.height - border, item.rect.width, border)
                shape.rect(item.rect.x, item.rect.y, border, item.rect.height)
                shape.rect(item.rect.x + item.rect.width - border, item.rect.y, border, item.rect.height)
            }
        }
    }

    private fun drawMascot(batch: SpriteBatch, assets: MainMenuAssets) {
        if (assets.isDisposed) return
        val rect = MainMenuAssets.containRect(
            assets.mascot.regionWidth.toFloat(),
            assets.mascot.regionHeight.toFloat(),
            MainMenuLayout.mascotRect
        )
        batch.color = Color.WHITE
        batch.draw(assets.mascot, rect.x, rect.y, rect.width, rect.height)
    }

    private fun drawTitles(
        batch: SpriteBatch,
        titleFont: BitmapFont,
        subtitleFont: BitmapFont,
        assets: MainMenuAssets?
    ) {
        if (assets != null && assets.isDisposed) return
        // L4：正式标题与副标，左对齐；章鱼只是吉祥物，不显示角色/货币信息。
        titleFont.color = Color(1f, 0.87f, 0.48f, 1f)
        drawLeftTop(
            batch, titleFont, "疯狂动物麻将",
            MainMenuLayout.titleRect.x + 8f,
            MainMenuLayout.titleRect.y,
            MainMenuLayout.titleRect.height
        )
        subtitleFont.color = Color(0.86f, 0.81f, 0.66f, 0.96f)
        val subtitle = "三大千术 · 牌桌心战"
        glyphLayout.setText(subtitleFont, subtitle)
        subtitleFont.draw(
            batch,
            subtitle,
            MainMenuLayout.subtitleRect.x,
            MainMenuLayout.subtitleRect.y + (MainMenuLayout.subtitleRect.height + glyphLayout.height) / 2f
        )
    }

    private fun drawItems(
        batch: SpriteBatch,
        assets: MainMenuAssets?,
        primaryFont: BitmapFont,
        secondaryFont: BitmapFont,
        statusFont: BitmapFont,
        currentSkinName: String
    ) {
        if (assets != null && assets.isDisposed) return
        for (item in frame.items) {
            if (item.alpha <= 0f) continue
            val rect = item.rect
            val enabled = isItemEnabled(item.id)
            val pressed = pressedId == item.id || activationFlashId == item.id
            val hovered = hoveredId == item.id
            val focused = focusedItemId == item.id
            val alpha = item.alpha * if (enabled) 1f else 0.55f

            if (assets != null) {
                val tint = when {
                    pressed -> Color(0.72f, 0.72f, 0.72f, alpha)
                    hovered -> Color(1.10f, 1.07f, 0.94f, alpha)
                    else -> Color(1f, 1f, 1f, alpha)
                }
                batch.color = tint
                val patch = if (item.id.primary) assets.primaryPatch else assets.secondaryPatch
                patch.draw(batch, rect.x, rect.y, rect.width, rect.height)

                if (hovered || focused) {
                    batch.color = Color(
                        1f, 0.90f, 0.55f,
                        alpha * if (focused) 0.95f else 0.58f
                    )
                    assets.focusPatch.draw(batch, rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f)
                }

                val icon = assets.icons[item.id]
                if (icon != null) {
                    batch.color = Color(1f, 1f, 1f, alpha)
                    batch.draw(icon, rect.x + 18f, rect.y + (rect.height - 64f) / 2f, 64f, 64f)
                }
            }

            val font = if (item.id.primary) primaryFont else secondaryFont
            font.color = Color(1f, 0.97f, 0.88f, alpha)
            val textX = rect.x + 94f
            val top = rect.y + (rect.height + textHeight(font, item.id.label)) / 2f
            font.draw(batch, item.id.label, textX, top)

            if (item.id == MainMenuItemId.SKIN) {
                val value = "当前：${MainMenuLayout.skinDisplayName(currentSkinName)}"
                statusFont.color = Color(0.98f, 0.85f, 0.53f, alpha)
                drawRightAligned(batch, statusFont, value, rect.x + rect.width - 20f, top)
            }
        }
    }

    private fun drawStatusBar(
        shape: ShapeRenderer,
        statusMessage: String?,
        errorMessage: String?
    ) {
        val rect = MainMenuLayout.statusRect
        shape.color = Color(0.025f, 0.075f, 0.065f, 0.86f)
        shape.rect(rect.x, rect.y, rect.width, rect.height)
        val accent = when {
            errorMessage != null -> Color(0.94f, 0.34f, 0.28f, 1f)
            statusMessage != null -> Color(0.96f, 0.78f, 0.35f, 1f)
            else -> Color(0.38f, 0.66f, 0.53f, 1f)
        }
        shape.color = accent
        shape.rect(rect.x, rect.y, 4f, rect.height)
        shape.color = Color(accent.r, accent.g, accent.b, 0.25f)
        shape.rect(rect.x + 4f, rect.y + rect.height - 1f, rect.width - 4f, 1f)
    }

    private fun drawStatusText(
        batch: SpriteBatch,
        statusFont: BitmapFont,
        statusMessage: String?,
        errorMessage: String?
    ) {
        val text: String
        val color: Color
        when {
            errorMessage != null -> {
                text = "错误：$errorMessage"
                color = Color(1f, 0.73f, 0.66f, 1f)
            }
            statusMessage != null -> {
                text = "提示：$statusMessage"
                color = Color(0.95f, 0.89f, 0.69f, 1f)
            }
            else -> {
                text = "选择入口 · 鼠标/触控 · Tab/↑↓ + Enter"
                color = Color(0.75f, 0.84f, 0.77f, 0.95f)
            }
        }
        statusFont.color = color
        val rect = MainMenuLayout.statusRect
        val fitted = ellipsize(statusFont, text, rect.width - 30f)
        val top = rect.y + (rect.height + textHeight(statusFont, fitted)) / 2f
        statusFont.draw(batch, fitted, rect.x + 16f, top)
    }

    private fun isFocusedOrHovered(id: MainMenuItemId): Boolean =
        focusedItemId == id || hoveredId == id

    private fun textHeight(font: BitmapFont, text: String): Float {
        glyphLayout.setText(font, text)
        return glyphLayout.height
    }

    private fun drawLeftTop(batch: SpriteBatch, font: BitmapFont, text: String, x: Float, y: Float, height: Float) {
        glyphLayout.setText(font, text)
        font.draw(batch, text, x, y + (height + glyphLayout.height) / 2f)
    }

    private fun drawRightAligned(batch: SpriteBatch, font: BitmapFont, text: String, rightX: Float, top: Float) {
        glyphLayout.setText(font, text)
        font.draw(batch, text, rightX - glyphLayout.width, top)
    }

    private fun ellipsize(font: BitmapFont, text: String, maxWidth: Float): String {
        glyphLayout.setText(font, text)
        if (glyphLayout.width <= maxWidth) return text
        var end = text.length - 1
        while (end > 0) {
            val candidate = text.substring(0, end) + "…"
            glyphLayout.setText(font, candidate)
            if (glyphLayout.width <= maxWidth) return candidate
            end--
        }
        return "…"
    }

    private data class FontState(
        val font: BitmapFont,
        val color: Color,
        val scaleX: Float,
        val scaleY: Float
    )

    companion object {
        /** 激活高亮最短可见时长，只影响绘制。 */
        const val ACTIVATION_FLASH_MS = 140L

        private val backdropCamera = OrthographicCamera()

        /**
         * L0 场景层：独立于 FitViewport，按真实屏幕尺寸 cover 整屏，
         * 超宽区域的场景由底图继续填充；内容仍由 MenuScreen 的视口限制在安全区。
         */
        fun renderBackdrop(
            batch: SpriteBatch,
            assets: MainMenuAssets,
            screenWidth: Float,
            screenHeight: Float
        ) {
            if (assets.isDisposed || screenWidth <= 0f || screenHeight <= 0f) return
            val priorColor = Color(batch.color)
            val priorProjection = Matrix4(batch.projectionMatrix)
            try {
                backdropCamera.setToOrtho(false, screenWidth, screenHeight)
                backdropCamera.update()
                val cover = MainMenuAssets.coverRect(
                    assets.background.regionWidth.toFloat(),
                    assets.background.regionHeight.toFloat(),
                    screenWidth,
                    screenHeight
                )
                batch.projectionMatrix = backdropCamera.combined
                batch.begin()
                try {
                    batch.color = Color.WHITE
                    batch.draw(assets.background, cover.x, cover.y, cover.width, cover.height)
                } finally {
                    batch.end()
                }
            } finally {
                batch.color = priorColor
                batch.projectionMatrix = priorProjection
            }
        }
    }
}
