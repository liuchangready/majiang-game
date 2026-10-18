package com.majiang.presentation.common

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.g2d.freetype.FreeTypeFontGenerator
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.utils.Disposable
import com.majiang.presentation.common.render.GhostHandTexture
import com.majiang.presentation.common.render.SkinManager

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
    private val fontGenerator: FreeTypeFontGenerator? = null
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

    var isDisposed: Boolean = false
        private set

    override fun dispose() {
        if (isDisposed) return
        isDisposed = true
        if (activeInstance === this) {
            activeInstance = null
        }
        try {
            chineseFont?.dispose()
        } catch (_: Throwable) {}
        if (chineseFont17 !== chineseFont) {
            try {
                chineseFont17?.dispose()
            } catch (_: Throwable) {}
        }
        try {
            fontGenerator?.dispose()
        } catch (_: Throwable) {}
        if (font !== chineseFont && font !== chineseFont17) {
            try {
                font.dispose()
            } catch (_: Throwable) {}
        }
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
                    try {
                        chineseFont?.dispose()
                    } catch (_: Throwable) {}
                    if (chineseFont17 !== chineseFont) {
                        try {
                            chineseFont17?.dispose()
                        } catch (_: Throwable) {}
                    }
                    fontGenerator = null
                    chineseFont = null
                    chineseFont17 = null
                }
            }

            val assets = UiAssets(
                batch = batch,
                shapeRenderer = shapeRenderer,
                font = font,
                chineseFont = chineseFont,
                chineseFont17 = chineseFont17,
                skinManager = skinManager,
                fontGenerator = fontGenerator
            )
            activeInstance = assets
            return assets
        }
    }
}
