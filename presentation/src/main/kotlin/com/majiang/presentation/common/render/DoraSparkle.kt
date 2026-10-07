package com.majiang.presentation.common.render

import com.badlogic.gdx.graphics.Color
import com.majiang.client.api.SuitView
import com.majiang.client.api.TileView
import kotlin.math.PI
import kotlin.math.sin

/**
 * 宝牌高亮状态快照
 *
 * @property doraTypeIndices 命中可见指示牌的 34 维 typeIndex 目标集合
 * @property redFive 是否包含赤宝牌（红五）
 */
data class DoraHighlight(
    val doraTypeIndices: Set<Int>,
    val redFive: Boolean = false
)

/**
 * 宝牌 / 赤宝牌闪光特效纯逻辑计算与图元指令追加器
 *
 * 遵循 DESIGN.md 表现层契约：
 * 1. computeVisibleTargets：只吃表指示牌（计算下一个顺位），天然排除未揭示的里宝牌；
 * 2. sparkleAlpha：确定性纯函数，周期 1.2s，值域严格 [0.15, 1.0]（强呼吸振幅）；
 * 3. 走 TileDrawBuffer 的 LineRectOp 桶（光环）与 overlay 覆盖层桶（整牌辉光 + 闪光星），
 *    SpriteBatch 每帧仍恰好一次提交，不破坏单批次渲染；
 * 4. 交互高亮（悬停/摸牌/被鸣/加杠）优先于光环描边，整牌辉光与闪光星始终保留；
 * 5. 赤宝牌始终持续闪光，不依赖指示牌。
 */
object DoraSparkle {
    /** 脉动周期（秒） */
    const val SPARKLE_PERIOD = 1.2f

    /** 脉动振幅下限（强呼吸感） */
    const val SPARKLE_ALPHA_MIN = 0.15f

    /** 整牌金色辉光叠加的最大透明度（覆盖层，压在牌面之上；过高会遮牌面，谨慎调大） */
    const val FACE_GLOW_MAX_ALPHA = 0.25f

    /** 光环层数（嵌套线框，由内到外透明度递减） */
    private val RING_OFFSETS = floatArrayOf(0f, 1.5f, 3f, 4.5f)
    private val RING_ALPHA_FACTORS = floatArrayOf(0.95f, 0.70f, 0.45f, 0.25f)

    /** 闪光星尺寸参数（十字星：两道金色光臂 + 白色星核） */
    const val STAR_ARM_LENGTH = 22f
    const val STAR_ARM_THICKNESS = 6f
    const val STAR_CORE_SIZE = 8f
    private const val STAR_INSET = 3f

    /**
     * 计算指示牌指示的 Dora 牌的 34 维 typeIndex。
     * Man: 0..8 (1m..9m), Sou: 9..17 (1s..9s), Pin: 18..26 (1p..9p)
     * Wind: 27..30 (東南西北), Dragon: 31..33 (白發中)
     */
    fun doraIndicatorTarget(tile: TileView): Int = when (tile.suit) {
        SuitView.MAN -> {
            val nextRank = if (tile.rank == 9) 1 else tile.rank + 1
            nextRank - 1
        }
        SuitView.SOU -> {
            val nextRank = if (tile.rank == 9) 1 else tile.rank + 1
            nextRank - 1 + 9
        }
        SuitView.PIN -> {
            val nextRank = if (tile.rank == 9) 1 else tile.rank + 1
            nextRank - 1 + 18
        }
        SuitView.WIND -> {
            val nextRank = if (tile.rank == 4) 1 else tile.rank + 1
            nextRank - 1 + 27
        }
        SuitView.DRAGON -> {
            val nextRank = if (tile.rank == 3) 1 else tile.rank + 1
            nextRank - 1 + 31
        }
    }

    /**
     * 由可见指示牌（不含未揭示里宝牌）计算高亮目标牌的 34 维特征索引集合。
     */
    fun computeVisibleTargets(doraIndicators: List<TileView>): Set<Int> =
        doraIndicators.map { doraIndicatorTarget(it) }.toSet()

    /**
     * 脉动相位 alpha ∈ [0.15, 1.0]，周期 1.2s，纯函数确定性。
     * 算式：0.15 + 0.85 * (0.5 + 0.5 * sin(2π * t / 1.2s))
     */
    fun sparkleAlpha(timeSeconds: Float): Float {
        val phase = (timeSeconds * (2.0 * PI) / SPARKLE_PERIOD).toFloat()
        val sinVal = sin(phase)
        val alpha = SPARKLE_ALPHA_MIN + (1.0f - SPARKLE_ALPHA_MIN) * (0.5f + 0.5f * sinVal)
        return alpha.coerceIn(SPARKLE_ALPHA_MIN, 1.0f)
    }

    /**
     * 判定指定牌是否为宝牌闪光目标：
     * - 赤宝牌（红五）始终闪光（不依赖指示牌）；
     * - 或牌的 34 维 typeIndex 命中可见指示牌目标。
     */
    fun isSparkleTarget(tile: TileView, visibleTargets: Set<Int>): Boolean =
        tile.isRedFive || tile.typeIndex in visibleTargets

    /**
     * 向 [TileDrawBuffer] 追加宝牌脉动金色闪光图元指令（高可见度三要素）：
     *
     * 1. **整牌金色辉光叠加**：覆盖层 [FilledRectOp]（overlay 桶，纹理/文字之上、线框之下），
     *    以最大 [FACE_GLOW_MAX_ALPHA] 的透明度随相位呼吸，整张牌泛金光——最主要的视觉信号；
     * 2. **四层嵌套光环**：由内向外的 [LineRectOp]，透明度递减形成柔和光晕（无交互高亮时绘制）；
     * 3. **右上角十字闪光星**：两道金色光臂 + 白色星核（overlay 桶），始终绘制、同相位脉动。
     *
     * 交互高亮（悬停/摸牌/被鸣/加杠）优先于光环描边（抑制光环），辉光与闪光星始终保留。
     *
     * @param buf 牌面绘制缓冲
     * @param x 牌矩形 X 坐标
     * @param y 牌矩形 Y 坐标
     * @param width 牌宽度
     * @param height 牌高度
     * @param alpha 当前闪光脉动透明度（由 [sparkleAlpha] 计算）
     * @param hasInteractionHighlight 当前牌是否有悬停/摸牌等交互高亮（若为 true 则抑制光环描边）
     */
    fun appendSparkle(
        buf: TileDrawBuffer,
        x: Float,
        y: Float,
        width: Float,
        height: Float,
        alpha: Float,
        hasInteractionHighlight: Boolean = false
    ) {
        // 1. 整牌金色辉光叠加（覆盖层，压在牌面之上）
        buf.addOverlayRect(
            x, y, width, height,
            Color(1.0f, 0.82f, 0.25f, FACE_GLOW_MAX_ALPHA * alpha)
        )

        // 2. 四层嵌套光环（无交互高亮时绘制，由内到外透明度递减）
        if (!hasInteractionHighlight) {
            for (i in RING_OFFSETS.indices) {
                val off = RING_OFFSETS[i]
                buf.addLineRect(
                    x - off, y - off, width + off * 2f, height + off * 2f,
                    Color(1.0f, 0.85f, 0.2f, alpha * RING_ALPHA_FACTORS[i])
                )
            }
        }

        // 3. 右上角十字闪光星（星核白、光臂金，中心在牌右上角内侧）
        val cx = x + width - STAR_INSET
        val cy = y + height - STAR_INSET
        val armAlpha = (0.5f + 0.5f * alpha).coerceAtMost(1f)
        buf.addOverlayRect(
            cx - STAR_ARM_LENGTH / 2f, cy - STAR_ARM_THICKNESS / 2f,
            STAR_ARM_LENGTH, STAR_ARM_THICKNESS,
            Color(1.0f, 0.88f, 0.35f, armAlpha)
        )
        buf.addOverlayRect(
            cx - STAR_ARM_THICKNESS / 2f, cy - STAR_ARM_LENGTH / 2f,
            STAR_ARM_THICKNESS, STAR_ARM_LENGTH,
            Color(1.0f, 0.88f, 0.35f, armAlpha)
        )
        buf.addOverlayRect(
            cx - STAR_CORE_SIZE / 2f, cy - STAR_CORE_SIZE / 2f,
            STAR_CORE_SIZE, STAR_CORE_SIZE,
            Color(1.0f, 1.0f, 0.95f, armAlpha)
        )
    }
}
