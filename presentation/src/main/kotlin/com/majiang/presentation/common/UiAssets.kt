package com.majiang.presentation.common

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Texture
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.NinePatch
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.g2d.TextureRegion
import com.badlogic.gdx.graphics.g2d.freetype.FreeTypeFontGenerator
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.utils.Disposable
import com.majiang.presentation.common.render.GhostHandTexture
import com.majiang.presentation.common.render.SkinManager
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
    val mainMenuFont16: BitmapFont? = null
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
     * 正式 APP 桌边场景（开局准备阶段）复用的麻将馆环境底图。
     *
     * 与主菜单共用同一份纹理实例：纹理由 [MainMenuAssets] 持有、由本容器在 [dispose] 统一释放，
     * 调用方只借用，禁止自行释放或每帧重新加载。
     */
    fun obtainTableBackdrop(): TextureRegion? = obtainMainMenuAssets()?.background

    /**
     * 开局准备阶段复用的九宫格按钮底板（朱红主按钮 / 翡翠次按钮 / 暖金焦点框）。
     *
     * 同样复用 [MainMenuAssets] 已持有的九宫格资源，避免为同一张 PNG 再建一份纹理。
     */
    fun obtainPreparationButtonPatches(): PreparationButtonPatches? {
        val assets = obtainMainMenuAssets() ?: return null
        if (assets.isDisposed) return null
        return PreparationButtonPatches(
            primary = assets.primaryPatch,
            secondary = assets.secondaryPatch,
            focus = assets.focusPatch
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
                        mainMenuTitleFont = menuFont(64)
                        mainMenuFont24 = menuFont(24)
                        mainMenuFont21 = menuFont(21)
                        mainMenuFont16 = menuFont(16)

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
                        mainMenuFont21, mainMenuFont16
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
                fontGenerator = fontGenerator
            )
            activeInstance = assets
            return assets
        }
    }
}
