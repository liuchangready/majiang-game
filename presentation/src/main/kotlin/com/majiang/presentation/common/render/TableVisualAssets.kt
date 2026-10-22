package com.majiang.presentation.common.render

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.Texture
import com.badlogic.gdx.graphics.g2d.NinePatch
import com.badlogic.gdx.graphics.g2d.TextureRegion
import com.badlogic.gdx.utils.Disposable
import com.majiang.client.api.ShuffleTarget

/**
 * 正式牌桌视觉资源包 (TableVisualAssets)。
 *
 * 设计来源：`assets/ui/table/manifest.json` 与
 * `doc/ui/round-preparation/VISUAL-COMPOSITION.md`。
 *
 * 所有权契约：
 * 1. 只由组合根 [com.majiang.presentation.common.UiAssets] 加载一次并统一释放；
 * 2. Screen / 准备面板只借用 [TextureRegion] 与 [NinePatch]，不得自行 dispose；
 * 3. 所有坐标系仍使用 libGDX 左下角原点；manifest 中左上角源切片在 [regionTopLeft] 内一次性转换；
 * 4. 纹理使用 MipMapLinearLinear + Linear + ClampToEdge（GLES3/桌面上生成 mipmap；
 *    GLES2 环境回退 Linear）、straight alpha，不按黑色做 alpha 抠图；源 PNG 像素不改；
 * 5. 九宫格显式设置目标边框（面板 44 / 名牌 12 / 提示 18 / 卡 20 / 按钮 32×18），
 *    绝不让 340px / 248px 的源像素直接成为逻辑边框。
 */
class TableVisualAssets private constructor(
    private val textures: List<Texture>,
    val environment: TextureRegion,
    val board: TextureRegion,
    val compass: TextureRegion,
    val panelMain: NinePatch,
    val panelPlate: NinePatch,
    val panelHint: NinePatch,
    val panelCard: NinePatch,
    val buttonPrimary: NinePatch,
    val buttonSecondary: NinePatch,
    /** 1×1 可着色像素，用于按钮字条/压暗内衬，不新增纹理。 */
    val labelBand: TextureRegion,
    /** 绝对席位 0..3 → 章鱼 / 喜鹊 / 熊猫 / 狐狸。 */
    val portraitsByAbsoluteSeat: Map<Int, TextureRegion>,
    val targetScenes: Map<ShuffleTarget, TextureRegion>
) : Disposable {

    var isDisposed: Boolean = false
        private set

    override fun dispose() {
        if (isDisposed) return
        isDisposed = true
        for (texture in textures) {
            try {
                texture.dispose()
            } catch (_: Throwable) {
            }
        }
    }

    companion object {
        const val ROOT = "ui/table"

        /** 面板源切片与目标边框。 */
        const val PANEL_SPLIT = 340
        const val PANEL_BORDER = 44
        const val PLATE_BORDER = 12
        const val HINT_BORDER = 18
        const val CARD_BORDER = 20

        /** 按钮源切片与目标边框。 */
        const val BUTTON_SPLIT_X = 248
        const val BUTTON_SPLIT_Y = 120
        const val BUTTON_BORDER_X = 32
        const val BUTTON_BORDER_Y = 18

        /**
         * 加载七张正式素材。调用方必须保证 GL / Gdx.files 已就绪。
         */
        fun load(): TableVisualAssets {
            val textures = mutableListOf<Texture>()

            fun texture(file: String): Texture {
                val tex = Texture(Gdx.files.internal("$ROOT/$file"))
                tex.setWrap(Texture.TextureWrap.ClampToEdge, Texture.TextureWrap.ClampToEdge)
                // 头像金饰/罗盘细线会缩小到 1/4 以下，Linear 单一采样会闪白、断线。
                // 源 PNG 不改，仅在 GPU 上生成 mipmap；GLES2 不支持 NPOT mipmap 时安全回退。
                if (Gdx.graphics.isGL30Available) {
                    tex.bind()
                    Gdx.gl.glGenerateMipmap(GL20.GL_TEXTURE_2D)
                    tex.setFilter(
                        Texture.TextureFilter.MipMapLinearLinear,
                        Texture.TextureFilter.Linear
                    )
                } else {
                    tex.setFilter(Texture.TextureFilter.Linear, Texture.TextureFilter.Linear)
                }
                textures += tex
                return tex
            }

            try {
                val environment = texture("club-environment.png")
                val board = texture("jade-table-board.png")
                val panel = texture("jade-panel-skin.png")
                val buttons = texture("button-skins.png")
                val portraits = texture("animal-medallions.png")
                val targets = texture("target-card-scenes.png")
                val compass = texture("round-compass.png")

                val panelPatch = ninePatchFromFull(
                    source = TextureRegion(panel),
                    srcSplitX = PANEL_SPLIT,
                    srcSplitY = PANEL_SPLIT,
                    dstLeft = PANEL_BORDER,
                    dstRight = PANEL_BORDER,
                    dstTop = PANEL_BORDER,
                    dstBottom = PANEL_BORDER
                )
                val platePatch = ninePatchFromFull(
                    source = TextureRegion(panel),
                    srcSplitX = PANEL_SPLIT,
                    srcSplitY = PANEL_SPLIT,
                    dstLeft = PLATE_BORDER,
                    dstRight = PLATE_BORDER,
                    dstTop = PLATE_BORDER,
                    dstBottom = PLATE_BORDER
                )
                val hintPatch = ninePatchFromFull(
                    source = TextureRegion(panel),
                    srcSplitX = PANEL_SPLIT,
                    srcSplitY = PANEL_SPLIT,
                    dstLeft = HINT_BORDER,
                    dstRight = HINT_BORDER,
                    dstTop = HINT_BORDER,
                    dstBottom = HINT_BORDER
                )
                val cardPatch = ninePatchFromFull(
                    source = TextureRegion(panel),
                    srcSplitX = PANEL_SPLIT,
                    srcSplitY = PANEL_SPLIT,
                    dstLeft = CARD_BORDER,
                    dstRight = CARD_BORDER,
                    dstTop = CARD_BORDER,
                    dstBottom = CARD_BORDER
                )

                val primaryRegion = regionTopLeft(buttons, 40, 96, 1456, 384)
                val secondaryRegion = regionTopLeft(buttons, 40, 544, 1456, 384)
                val primaryPatch = ninePatchFromCroppedButton(
                    primaryRegion,
                    BUTTON_SPLIT_X,
                    BUTTON_SPLIT_Y,
                    BUTTON_BORDER_X,
                    BUTTON_BORDER_Y
                )
                val secondaryPatch = ninePatchFromCroppedButton(
                    secondaryRegion,
                    BUTTON_SPLIT_X,
                    BUTTON_SPLIT_Y,
                    BUTTON_BORDER_X,
                    BUTTON_BORDER_Y
                )

                val portraitRegions = mapOf(
                    0 to regionTopLeft(portraits, 0, 0, 627, 627),
                    1 to regionTopLeft(portraits, 627, 627, 627, 627),
                    2 to regionTopLeft(portraits, 0, 627, 627, 627),
                    3 to regionTopLeft(portraits, 627, 0, 627, 627)
                )

                val targetRegions = mapOf(
                    ShuffleTarget.CHINITSU_MAN to regionTopLeft(targets, 16, 8, 744, 324),
                    ShuffleTarget.CHINITSU_SOU to regionTopLeft(targets, 776, 8, 744, 324),
                    ShuffleTarget.CHINITSU_PIN to regionTopLeft(targets, 16, 340, 744, 324),
                    ShuffleTarget.DAISANGEN to regionTopLeft(targets, 776, 340, 744, 324),
                    ShuffleTarget.KOKUSHI to regionTopLeft(targets, 16, 672, 744, 324),
                    ShuffleTarget.DAISUUSHII to regionTopLeft(targets, 776, 672, 744, 324)
                )

                return TableVisualAssets(
                    textures = textures.toList(),
                    environment = TextureRegion(environment),
                    board = TextureRegion(board),
                    compass = TextureRegion(compass),
                    panelMain = panelPatch,
                    panelPlate = platePatch,
                    panelHint = hintPatch,
                    panelCard = cardPatch,
                    buttonPrimary = primaryPatch,
                    buttonSecondary = secondaryPatch,
                    labelBand = TextureRegion(panel, 627, 627, 1, 1),
                    portraitsByAbsoluteSeat = portraitRegions,
                    targetScenes = targetRegions
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

        /**
         * 从图集按 manifest 的左上角行列取源切片。
         *
         * 这里验证过 libGDX 的 `TextureRegion(texture, x, top, width, height)` 在
         * SpriteBatch 中按图集左上角坐标显示（顶行 = top 像素）。不要再次做 720 或
         * 1024 的 Y 翻转，否则会把红/绿按钮、六个目标场景和四张头像整体上下对调。
         */
        fun regionTopLeft(region: TextureRegion, left: Int, top: Int, width: Int, height: Int): TextureRegion =
            regionTopLeft(region.texture, left, top, width, height)

        fun regionTopLeft(texture: Texture, left: Int, top: Int, width: Int, height: Int): TextureRegion {
            require(left >= 0 && top >= 0 && width > 0 && height > 0) {
                "Invalid source region: ($left,$top,$width,$height)"
            }
            require(left + width <= texture.width && top + height <= texture.height) {
                "Source region outside ${texture.width}x${texture.height}: ($left,$top,$width,$height)"
            }
            val y = top
            return TextureRegion(texture, left, y, width, height)
        }

        private fun sub(region: TextureRegion, x: Int, y: Int, width: Int, height: Int): TextureRegion =
            TextureRegion(region.texture, region.regionX + x, region.regionY + y, width, height)

        private fun ninePatchFromFull(
            source: TextureRegion,
            srcSplitX: Int,
            srcSplitY: Int,
            dstLeft: Int,
            dstRight: Int,
            dstTop: Int,
            dstBottom: Int
        ): NinePatch {
            val w = source.regionWidth
            val h = source.regionHeight
            val centerW = w - srcSplitX * 2
            val centerH = h - srcSplitY * 2
            // TextureRegion 的 y=0 是图集顶行；九宫格 Top 行必须取 local y=0。
            val tl = sub(source, 0, 0, srcSplitX, srcSplitY)
            val tc = sub(source, srcSplitX, 0, centerW, srcSplitY)
            val tr = sub(source, w - srcSplitX, 0, srcSplitX, srcSplitY)
            val ml = sub(source, 0, srcSplitY, srcSplitX, centerH)
            val mc = sub(source, srcSplitX, srcSplitY, centerW, centerH)
            val mr = sub(source, w - srcSplitX, srcSplitY, srcSplitX, centerH)
            val bl = sub(source, 0, h - srcSplitY, srcSplitX, srcSplitY)
            val bc = sub(source, srcSplitX, h - srcSplitY, centerW, srcSplitY)
            val br = sub(source, w - srcSplitX, h - srcSplitY, srcSplitX, srcSplitY)
            return NinePatch(tl, tc, tr, ml, mc, mr, bl, bc, br).apply {
                setLeftWidth(dstLeft.toFloat())
                setRightWidth(dstRight.toFloat())
                setTopHeight(dstTop.toFloat())
                setBottomHeight(dstBottom.toFloat())
            }
        }

        /**
         * 先按 manifest 从按钮图集取红/绿源区域，再对区域做 248/248/120/120 九宫格。
         */
        private fun ninePatchFromCroppedButton(
            source: TextureRegion,
            splitX: Int,
            splitY: Int,
            dstX: Int,
            dstY: Int
        ): NinePatch {
            val w = source.regionWidth
            val h = source.regionHeight
            val centerW = w - splitX * 2
            val centerH = h - splitY * 2
            require(centerW > 0 && centerH > 0) { "Button source too small for split" }
            val tl = sub(source, 0, 0, splitX, splitY)
            val tc = sub(source, splitX, 0, centerW, splitY)
            val tr = sub(source, w - splitX, 0, splitX, splitY)
            val ml = sub(source, 0, splitY, splitX, centerH)
            val mc = sub(source, splitX, splitY, centerW, centerH)
            val mr = sub(source, w - splitX, splitY, splitX, centerH)
            val bl = sub(source, 0, h - splitY, splitX, splitY)
            val bc = sub(source, splitX, h - splitY, centerW, splitY)
            val br = sub(source, w - splitX, h - splitY, splitX, splitY)
            return NinePatch(tl, tc, tr, ml, mc, mr, bl, bc, br).apply {
                setLeftWidth(dstX.toFloat())
                setRightWidth(dstX.toFloat())
                setTopHeight(dstY.toFloat())
                setBottomHeight(dstY.toFloat())
            }
        }
    }
}
