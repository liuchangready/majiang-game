package com.majiang.presentation.common

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.Texture
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.NinePatch
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.g2d.freetype.FreeTypeFontGenerator
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.utils.Disposable
import com.majiang.presentation.common.render.GhostHandTexture
import com.majiang.presentation.common.render.SkinManager
import com.majiang.presentation.common.render.StageFont
import com.majiang.presentation.common.render.TableVisualAssets
import com.majiang.presentation.menu.MainMenuAssets

/**
 * 表现层统一共享资产容器 (UiAssets)。
 * 由组合根 (MahjongGame) 统一创建、持有并负责生命周期释放。
 *
 * 核心契约与资源所有权规范：
 * 1. 组合根持有并释放，各 Screen 仅借用资源；
 * 2. 共享 [TileSkin] 仅由 [SkinManager] 管理与释放，进退桌绝不重复释放；
 * 3. 共享 [chineseFont] 基于 NotoSansSC-Regular 按需生成，并在根关闭时统一回收；
 * 4. 全局缓存的 [GhostHandTexture] 在 [dispose] 中安全收尾。
 */
class UiAssets(
    val batch: SpriteBatch,
    val shapeRenderer: ShapeRenderer,
    val font: BitmapFont,
    val chineseFont: BitmapFont? = null,
    val chineseFont17: BitmapFont? = null,
    val skinManager: SkinManager = SkinManager(),
    private val fontGenerator: FreeTypeFontGenerator? = null,
    /** 主菜单专用 64px 标题字库，禁止用 18px 字体直接放大冒充。 */
    val mainMenuTitleFont: BitmapFont? = null,
    /** 主菜单主文案 24px 动态中文字库。 */
    val mainMenuFont24: BitmapFont? = null,
    /** 主菜单次文案 21px 动态中文字库。 */
    val mainMenuFont21: BitmapFont? = null,
    /** 主菜单状态/皮肤值 16px 动态中文字库。 */
    val mainMenuFont16: BitmapFont? = null,
    /** 正式牌桌标题 34px 动态中文字库。 */
    val tableTitleFont34: BitmapFont? = null,
    /** 正式牌桌抽屉标题 28px 动态中文字库。 */
    val tableDrawerFont28: BitmapFont? = null,
    /** 正式牌桌主按钮 25px 动态中文字库。 */
    val tablePrimaryFont25: BitmapFont? = null,
    /** 正式牌桌正文 19px 动态中文字库。 */
    val tableBodyFont19: BitmapFont? = null,
    /** 正式牌桌回执标题 31px 动态中文字库。 */
    val tableReceiptFont31: BitmapFont? = null,
    /** 牌桌目标卡名/分数 21px。 */
    val tableCardFont21: BitmapFont? = null,
    /** 牌桌辅助信息 16px。 */
    val tableSmallFont16: BitmapFont? = null,
    /** 牌桌席位名 17px。 */
    val tableNameFont17: BitmapFont? = null
) : Disposable {

    /**
     * 最佳可用 18px 字体：优先使用支持中文的 NotoSansSC 字体 (18px)；若无头测试或环境不支持则安全降级回内置默认字体。
     */
    val effectiveFont: BitmapFont
        get() = chineseFont ?: font

    /**
     * 最佳可用 17px 字体：优先使用支持中文的 NotoSansSC 字体 (17px)；若无头测试或环境不支持则安全降级回 effectiveFont / font。
     */
    val effectiveFont17: BitmapFont
        get() = chineseFont17 ?: effectiveFont

    /**
     * 18px 中文字体别名（与结算及全界面规格保持一致）。
     */
    val chineseFont18: BitmapFont?
        get() = chineseFont

    /** 主菜单标题字体：专用 64px 字库，缺失时安全降级。 */
    val effectiveMainMenuTitleFont: BitmapFont
        get() = mainMenuTitleFont ?: chineseFont ?: font

    /** 主菜单主入口 24px 字体。 */
    val effectiveMainMenuFont24: BitmapFont
        get() = mainMenuFont24 ?: effectiveFont

    /** 主菜单次入口 21px 字体。 */
    val effectiveMainMenuFont21: BitmapFont
        get() = mainMenuFont21 ?: effectiveFont

    /** 主菜单状态与辅助信息 16px 字体。 */
    val effectiveMainMenuFont16: BitmapFont
        get() = mainMenuFont16 ?: effectiveFont17

    /** 牌桌大标题 34px（缺失时只做安全降级，不用 17px 字库放大伪装）。 */
    val effectiveTableTitleFont: BitmapFont
        get() = tableTitleFont34 ?: mainMenuFont24 ?: effectiveFont

    /** 抽屉标题 28px。 */
    val effectiveTableDrawerFont: BitmapFont
        get() = tableDrawerFont28 ?: mainMenuFont24 ?: effectiveFont

    /** 主按钮 25px。 */
    val effectiveTablePrimaryFont: BitmapFont
        get() = tablePrimaryFont25 ?: mainMenuFont24 ?: effectiveFont

    /** 正文 19px。 */
    val effectiveTableBodyFont: BitmapFont
        get() = tableBodyFont19 ?: chineseFont ?: font

    /** 目标卡名称 21px。 */
    val effectiveTableCardFont: BitmapFont
        get() = tableCardFont21 ?: mainMenuFont21 ?: chineseFont ?: font

    /** 辅助信息 16px。 */
    val effectiveTableSmallFont: BitmapFont
        get() = tableSmallFont16 ?: mainMenuFont16 ?: effectiveFont17

    /** 席位名字 17px。 */
    val effectiveTableNameFont: BitmapFont
        get() = tableNameFont17 ?: chineseFont17 ?: effectiveFont

    /** 回执标题 31px。 */
    val effectiveTableReceiptFont: BitmapFont
        get() = tableReceiptFont31 ?: tableTitleFont34 ?: mainMenuFont24 ?: effectiveFont

    /**
     * 正式牌桌字体根缓存。
     *
     * 键是“已经乘过 stageScale 与 framebufferRatio 的原生像素字号”，因此 1280、960、
     * 1600 以及高 DPI 帧缓冲各有清晰字形，不会把 17px 字体放大冒充标题。
     * 连续拖动窗口会产生大量近似字号，故使用有界 LRU；淘汰和根释放都会 dispose，
     * 不会每拖一像素永久新增字体纹理。最大 32 个原生字号，按 8 个固定角色计算
     * 可覆盖约 4 个不同缩放档位。
     */
    private val stageFontCache = object : LinkedHashMap<Int, BitmapFont>(32, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<Int, BitmapFont>): Boolean {
            if (size <= MAX_STAGE_FONTS) return false
            try {
                eldest.value.dispose()
            } catch (_: Throwable) {
            }
            return true
        }
    }

    /** 测试/交付日志可读的有界缓存计数。 */
    val stageFontCacheSize: Int
        get() = stageFontCache.size

    /**
     * 取得按当前 stage / 帧缓冲原生像素生成的字形。返回的 [StageFont] 只借用缓存，
     * 调用方不得 dispose；下一次 resize 后由 LRU 或根统一释放。
     */
    fun stageFont(designSize: Int, stageScale: Float, framebufferRatio: Float): StageFont? {
        if (isDisposed) return null
        val generator = fontGenerator ?: return null
        if (designSize <= 0 || stageScale <= 0f || framebufferRatio <= 0f) return null
        val pixelSize = Math.round(designSize * stageScale * framebufferRatio).coerceIn(8, 160)
        val font = synchronized(stageFontCache) {
            stageFontCache[pixelSize] ?: run {
                try {
                    val parameter = FreeTypeFontGenerator.FreeTypeFontParameter().apply {
                        size = pixelSize
                        incremental = true
                        characters = FreeTypeFontGenerator.DEFAULT_CHARS
                        minFilter = Texture.TextureFilter.Linear
                        magFilter = Texture.TextureFilter.Linear
                        // 小字补一点白色/暖白描边，避免 Linear 缩小后笔画发灰；
                        // 字号越大描边越轻，不靠拉伸低分辨率字库制造“粗体”。
                        borderWidth = when {
                            pixelSize <= 22 -> 0.32f
                            pixelSize <= 32 -> 0.42f
                            else -> 0.55f
                        }
                        borderColor = Color.WHITE
                        borderStraight = true
                    }
                    generator.generateFont(parameter).also { stageFontCache[pixelSize] = it }
                } catch (t: Throwable) {
                    Gdx.app?.error("UiAssets", "Failed to generate stage font ${pixelSize}px: ${t.message}")
                    null
                }
            }
        } ?: return null
        return StageFont(font, designSize, pixelSize, designSize.toFloat() / pixelSize.toFloat())
    }

    /**
     * 正式牌桌七张素材的根所有权。首次取得时加载一次，Screen 仅借用。
     */
    var tableVisualAssets: TableVisualAssets? = null
        private set

    private var tableVisualAssetsAttempted = false

    fun obtainTableVisualAssets(): TableVisualAssets? {
        if (isDisposed) return null
        tableVisualAssets?.takeUnless { it.isDisposed }?.let { return it }
        if (tableVisualAssetsAttempted) return null
        if (Gdx.gl == null || Gdx.files == null || Gdx.graphics == null || Gdx.graphics.width <= 0) {
            return null
        }
        tableVisualAssetsAttempted = true
        return try {
            TableVisualAssets.load().also { tableVisualAssets = it }
        } catch (t: Throwable) {
            Gdx.app?.error("UiAssets", "Failed to load table visual assets: ${t.message}")
            null
        }
    }

    /**
     * 主菜单资源根所有权：仅在首次真正渲染 MAIN 时延迟创建一次。
     * 无头测试或缺失 GL 时返回 null，不强制创建纹理；失败后不每帧重试。
     */
    var mainMenuAssets: MainMenuAssets? = null
        private set

    private var mainMenuAssetsAttempted = false

    fun obtainMainMenuAssets(): MainMenuAssets? {
        if (isDisposed) return null
        menuAssetsOrNull()?.let { return it }
        if (mainMenuAssetsAttempted) return null
        if (Gdx.gl == null || Gdx.files == null || Gdx.graphics == null || Gdx.graphics.width <= 0) {
            return null
        }
        mainMenuAssetsAttempted = true
        return try {
            MainMenuAssets.load().also { mainMenuAssets = it }
        } catch (t: Throwable) {
            Gdx.app?.error("UiAssets", "Failed to load main-menu assets: ${t.message}")
            null
        }
    }

    private fun menuAssetsOrNull(): MainMenuAssets? = mainMenuAssets?.takeUnless { it.isDisposed }

    /**
     * 开局准备阶段复用的九宫格按钮底板（朱红主按钮 / 翡翠次按钮 / 暖金焦点框）。
     *
     * 同样复用 [MainMenuAssets] 已持有的九宫格资源，避免为同一张 PNG 再建一份纹理。
     */
    fun obtainPreparationButtonPatches(): PreparationButtonPatches? {
        val table = obtainTableVisualAssets() ?: return null
        if (table.isDisposed) return null
        return PreparationButtonPatches(
            primary = table.buttonPrimary,
            secondary = table.buttonSecondary,
            focus = table.panelCard
        )
    }

    /**
     * 准备阶段按钮底板集合（只读借用，不拥有纹理生命周期）。
     */
    class PreparationButtonPatches(
        val primary: NinePatch,
        val secondary: NinePatch,
        val focus: NinePatch
    )

    var isDisposed: Boolean = false
        private set

    override fun dispose() {
        if (isDisposed) return
        isDisposed = true
        if (activeInstance === this) {
            activeInstance = null
        }
        try {
            tableVisualAssets?.dispose()
        } catch (_: Throwable) {}
        tableVisualAssets = null
        try {
            mainMenuAssets?.dispose()
        } catch (_: Throwable) {}
        mainMenuAssets = null
        val disposedFonts = java.util.Collections.newSetFromMap(
            java.util.IdentityHashMap<BitmapFont, Boolean>()
        )
        fun disposeFont(fontToDispose: BitmapFont?) {
            if (fontToDispose == null) return
            if (!disposedFonts.add(fontToDispose)) return
            try {
                fontToDispose.dispose()
            } catch (_: Throwable) {}
        }
        disposeFont(chineseFont)
        disposeFont(chineseFont17)
        disposeFont(mainMenuTitleFont)
        disposeFont(mainMenuFont24)
        disposeFont(mainMenuFont21)
        disposeFont(mainMenuFont16)
        disposeFont(tableTitleFont34)
        disposeFont(tableDrawerFont28)
        disposeFont(tablePrimaryFont25)
        disposeFont(tableBodyFont19)
        disposeFont(tableReceiptFont31)
        disposeFont(tableCardFont21)
        disposeFont(tableSmallFont16)
        disposeFont(tableNameFont17)
        synchronized(stageFontCache) {
            for (cached in stageFontCache.values) {
                disposeFont(cached)
            }
            stageFontCache.clear()
        }
        disposeFont(font)
        try {
            fontGenerator?.dispose()
        } catch (_: Throwable) {}
        try {
            batch.dispose()
        } catch (_: Throwable) {}
        try {
            shapeRenderer.dispose()
        } catch (_: Throwable) {}
        try {
            skinManager.dispose()
        } catch (_: Throwable) {}
        try {
            GhostHandTexture.dispose()
        } catch (_: Throwable) {}
    }

    companion object {
        /** 有界字形缓存上限；超过即淘汰最久未用字号并立即 dispose。 */
        const val MAX_STAGE_FONTS = 32

        @Volatile
        var activeInstance: UiAssets? = null
            internal set

        fun createDefault(skinManager: SkinManager = SkinManager()): UiAssets {
            val batch = SpriteBatch()
            val shapeRenderer = ShapeRenderer()
            val font = BitmapFont()
            var chineseFont: BitmapFont? = null
            var chineseFont17: BitmapFont? = null
            var mainMenuTitleFont: BitmapFont? = null
            var mainMenuFont24: BitmapFont? = null
            var mainMenuFont21: BitmapFont? = null
            var mainMenuFont16: BitmapFont? = null
            var tableTitleFont34: BitmapFont? = null
            var tableDrawerFont28: BitmapFont? = null
            var tablePrimaryFont25: BitmapFont? = null
            var tableBodyFont19: BitmapFont? = null
            var tableReceiptFont31: BitmapFont? = null
            var tableCardFont21: BitmapFont? = null
            var tableSmallFont16: BitmapFont? = null
            var tableNameFont17: BitmapFont? = null
            var fontGenerator: FreeTypeFontGenerator? = null

            if (Gdx.files != null && Gdx.gl != null && Gdx.graphics != null && Gdx.graphics.width > 0) {
                var localGenerator: FreeTypeFontGenerator? = null
                try {
                    val fontHandle = Gdx.files.internal("fonts/NotoSansSC-Regular.ttf")
                    if (fontHandle.exists()) {
                        val generator = FreeTypeFontGenerator(fontHandle)
                        localGenerator = generator
                        val param18 = FreeTypeFontGenerator.FreeTypeFontParameter().apply {
                            size = 18
                            incremental = true
                            characters = FreeTypeFontGenerator.DEFAULT_CHARS
                        }
                        chineseFont = generator.generateFont(param18)

                        val param17 = FreeTypeFontGenerator.FreeTypeFontParameter().apply {
                            size = 17
                            incremental = true
                            characters = FreeTypeFontGenerator.DEFAULT_CHARS
                        }
                        chineseFont17 = generator.generateFont(param17)

                        fun menuFont(sizePx: Int): BitmapFont {
                            val parameter = FreeTypeFontGenerator.FreeTypeFontParameter().apply {
                                size = sizePx
                                incremental = true
                                characters = FreeTypeFontGenerator.DEFAULT_CHARS
                                minFilter = Texture.TextureFilter.Linear
                                magFilter = Texture.TextureFilter.Linear
                            }
                            return generator.generateFont(parameter)
                        }
                        fun tableFont(sizePx: Int): BitmapFont {
                            val parameter = FreeTypeFontGenerator.FreeTypeFontParameter().apply {
                                size = sizePx
                                incremental = true
                                characters = FreeTypeFontGenerator.DEFAULT_CHARS
                                minFilter = Texture.TextureFilter.Linear
                                magFilter = Texture.TextureFilter.Linear
                            }
                            return generator.generateFont(parameter)
                        }
                        mainMenuTitleFont = menuFont(64)
                        mainMenuFont24 = menuFont(24)
                        mainMenuFont21 = menuFont(21)
                        mainMenuFont16 = menuFont(16)
                        // 字段名保留历史宽度；正式牌桌取更清楚的原生字号。
                        tableTitleFont34 = tableFont(36)
                        tableDrawerFont28 = tableFont(30)
                        tablePrimaryFont25 = tableFont(27)
                        tableBodyFont19 = tableFont(19)
                        tableReceiptFont31 = tableFont(36)
                        tableCardFont21 = tableFont(22)
                        tableSmallFont16 = tableFont(17)
                        tableNameFont17 = tableFont(18)

                        fontGenerator = generator
                        localGenerator = null
                    }
                } catch (t: Throwable) {
                    Gdx.app?.error("UiAssets", "Failed to load NotoSansSC font: ${t.message}")
                    try {
                        localGenerator?.dispose()
                    } catch (_: Throwable) {}
                    if (fontGenerator !== localGenerator) {
                        try {
                            fontGenerator?.dispose()
                        } catch (_: Throwable) {}
                    }
                    listOf(
                        chineseFont, chineseFont17, mainMenuTitleFont, mainMenuFont24,
                        mainMenuFont21, mainMenuFont16, tableTitleFont34, tableDrawerFont28,
                        tablePrimaryFont25, tableBodyFont19, tableReceiptFont31,
                        tableCardFont21, tableSmallFont16, tableNameFont17
                    ).forEach { fontToDispose ->
                        try {
                            fontToDispose?.dispose()
                        } catch (_: Throwable) {}
                    }
                    fontGenerator = null
                    chineseFont = null
                    chineseFont17 = null
                    mainMenuTitleFont = null
                    mainMenuFont24 = null
                    mainMenuFont21 = null
                    mainMenuFont16 = null
                    tableTitleFont34 = null
                    tableDrawerFont28 = null
                    tablePrimaryFont25 = null
                    tableBodyFont19 = null
                    tableReceiptFont31 = null
                    tableCardFont21 = null
                    tableSmallFont16 = null
                    tableNameFont17 = null
                }
            }

            val assets = UiAssets(
                batch = batch,
                shapeRenderer = shapeRenderer,
                font = font,
                chineseFont = chineseFont,
                chineseFont17 = chineseFont17,
                skinManager = skinManager,
                mainMenuTitleFont = mainMenuTitleFont,
                mainMenuFont24 = mainMenuFont24,
                mainMenuFont21 = mainMenuFont21,
                mainMenuFont16 = mainMenuFont16,
                tableTitleFont34 = tableTitleFont34,
                tableDrawerFont28 = tableDrawerFont28,
                tablePrimaryFont25 = tablePrimaryFont25,
                tableBodyFont19 = tableBodyFont19,
                tableReceiptFont31 = tableReceiptFont31,
                tableCardFont21 = tableCardFont21,
                tableSmallFont16 = tableSmallFont16,
                tableNameFont17 = tableNameFont17,
                fontGenerator = fontGenerator
            )
            activeInstance = assets
            return assets
        }
    }
}
