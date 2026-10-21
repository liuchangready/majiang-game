package com.majiang.presentation.menu

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Texture
import com.badlogic.gdx.graphics.g2d.NinePatch
import com.badlogic.gdx.graphics.g2d.TextureRegion
import com.badlogic.gdx.math.Rectangle
import com.badlogic.gdx.utils.Disposable

/**
 * 正式主菜单美术资源包。
 *
 * 所有权：仅由 [com.majiang.presentation.common.UiAssets] 延迟创建、统一释放；
 * [MainMenuView] 与 [MenuScreen] 只借用，绝不自行 dispose 或每帧重新加载。
 */
class MainMenuAssets private constructor(
    private val textures: List<Texture>,
    val background: TextureRegion,
    val mascot: TextureRegion,
    val primaryPatch: NinePatch,
    val secondaryPatch: NinePatch,
    val focusPatch: NinePatch,
    val icons: Map<MainMenuItemId, TextureRegion>
) : Disposable {

    var isDisposed: Boolean = false
        private set

    override fun dispose() {
        if (isDisposed) return
        isDisposed = true
        textures.forEach { texture ->
            try {
                texture.dispose()
            } catch (_: Throwable) {
            }
        }
    }

    companion object {
        const val ROOT = "ui/main-menu"
        const val PATCH_SPLIT = 18

        fun load(): MainMenuAssets {
            val textures = mutableListOf<Texture>()
            fun texture(path: String): Texture {
                val tex = Texture(Gdx.files.internal("$ROOT/$path"))
                tex.setFilter(Texture.TextureFilter.Linear, Texture.TextureFilter.Linear)
                tex.setWrap(Texture.TextureWrap.ClampToEdge, Texture.TextureWrap.ClampToEdge)
                textures += tex
                return tex
            }

            try {
                val background = texture("club-background.png")
                val mascot = texture("octopus-hero.png")
                val primary = texture("button-primary.png")
                val secondary = texture("button-secondary.png")
                val focus = texture("button-focus.png")
                val iconAtlas = texture("menu-icons.png")

                val icons = MainMenuItemId.entries.associateWith { id ->
                    TextureRegion(iconAtlas, id.iconIndex * 64, 0, 64, 64)
                }
                return MainMenuAssets(
                    textures = textures.toList(),
                    background = TextureRegion(background),
                    mascot = TextureRegion(mascot),
                    primaryPatch = NinePatch(primary, PATCH_SPLIT, PATCH_SPLIT, PATCH_SPLIT, PATCH_SPLIT),
                    secondaryPatch = NinePatch(secondary, PATCH_SPLIT, PATCH_SPLIT, PATCH_SPLIT, PATCH_SPLIT),
                    focusPatch = NinePatch(focus, PATCH_SPLIT, PATCH_SPLIT, PATCH_SPLIT, PATCH_SPLIT),
                    icons = icons
                )
            } catch (t: Throwable) {
                textures.forEach { texture ->
                    try {
                        texture.dispose()
                    } catch (_: Throwable) {
                    }
                }
                throw t
            }
        }

        /** cover：等比放大到铺满屏幕，允许裁掉边缘，绝不拉伸。 */
        fun coverRect(imageWidth: Float, imageHeight: Float, screenWidth: Float, screenHeight: Float): Rectangle {
            if (imageWidth <= 0f || imageHeight <= 0f || screenWidth <= 0f || screenHeight <= 0f) {
                return Rectangle(0f, 0f, screenWidth, screenHeight)
            }
            val scale = maxOf(screenWidth / imageWidth, screenHeight / imageHeight)
            val width = imageWidth * scale
            val height = imageHeight * scale
            return Rectangle(
                (screenWidth - width) / 2f,
                (screenHeight - height) / 2f,
                width,
                height
            )
        }

        /** contain：等比完整显示，绝不裁脸或扭曲。 */
        fun containRect(sourceWidth: Float, sourceHeight: Float, target: Rectangle): Rectangle {
            if (sourceWidth <= 0f || sourceHeight <= 0f) return Rectangle(target)
            val scale = minOf(target.width / sourceWidth, target.height / sourceHeight)
            val width = sourceWidth * scale
            val height = sourceHeight * scale
            return Rectangle(
                target.x + (target.width - width) / 2f,
                target.y + (target.height - height) / 2f,
                width,
                height
            )
        }
    }
}
