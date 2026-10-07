package com.majiang.presentation.common

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.graphics.Color
import com.badlogic.gdx.graphics.GL20
import com.badlogic.gdx.graphics.OrthographicCamera
import com.badlogic.gdx.graphics.g2d.BitmapFont
import com.badlogic.gdx.graphics.g2d.GlyphLayout
import com.badlogic.gdx.graphics.g2d.SpriteBatch
import com.badlogic.gdx.graphics.glutils.ShapeRenderer
import com.badlogic.gdx.math.Rectangle
import com.majiang.client.api.PublicSessionResultView
import com.majiang.client.api.SessionProgressView

/**
 * 比赛总成绩结算模态弹窗渲染器 (V4 presentation.common)。
 *
 * 契约规范：
 * 1. 明确开启 GL20.GL_BLEND 混合，行底采用深色半透明/不透明底色，确保金色/白色/铜色/浅红文字清晰可读；
 * 2. 严格使用 session.finalResult 展示权威整场终局原因，中文映射为“击飞终局 / 正常终局 / 延长战结束 / 庄家终了”，不带括号内枚举代码；
 * 3. 兼容入口若缺失 finalResult 时显示“比赛已结束”与公开分数，绝不伪造终局原因；
 * 4. 席位显示“座位1/2/3/4”并对本人标记“[你]”，不将其他席位猜标为 AI；
 * 5. 不按 score < 0 附加“[击飞]”标签，负分仅着色；
 * 6. 底部提供“返回菜单”按钮（520,145,240,46）与“本局明细”按钮（250,145,240,46）。
 * 7. 输入仅依赖 client.api 视图模型 (PublicSessionResultView, SessionProgressView)。
 */
class FinalResultRenderer {

    private val glyphLayout = GlyphLayout()

    fun render(
        camera: OrthographicCamera,
        shape: ShapeRenderer,
        batch: SpriteBatch,
        fallbackFont: BitmapFont,
        chineseFont: BitmapFont?,
        finalResult: PublicSessionResultView?,
        sessionView: SessionProgressView?,
        currentScores: List<Int>,
        viewerSeat: Int,
        hasRoundDetails: Boolean,
        btnReturnToMenuRect: Rectangle,
        btnRoundDetailsRect: Rectangle
    ) {
        val modalRect = SettlementModalGeometry.MODAL_RECT
        val textFont = chineseFont ?: fallbackFont
        val isCustomFont = (chineseFont != null)

        // 1. 显式开启 Alpha 混合，确保遮罩与深色行底正常混合 (ShapeRenderer Filled)
        Gdx.gl.glEnable(GL20.GL_BLEND)
        Gdx.gl.glBlendFunc(GL20.GL_SRC_ALPHA, GL20.GL_ONE_MINUS_SRC_ALPHA)

        shape.projectionMatrix = camera.combined
        shape.begin(ShapeRenderer.ShapeType.Filled)

        // 半透明全屏遮罩 (78% 黑色遮罩遮挡底层桌面 UI)
        shape.color = Color(0f, 0f, 0f, 0.78f)
        shape.rect(0f, 0f, 1280f, 720f)

        // 弹窗主底色 (深墨绿黑)
        shape.color = Color(0.08f, 0.16f, 0.12f, 0.98f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        // 四人名次行背景 (采用不透明深色行底，保证金色/白色/浅红文字极高对比度)
        val rowYPositions = floatArrayOf(
            modalRect.y + 360f,
            modalRect.y + 305f,
            modalRect.y + 250f,
            modalRect.y + 195f
        )
        val rowColors = arrayOf(
            Color(0.20f, 0.18f, 0.08f, 0.94f), // 1st 深暗金底
            Color(0.14f, 0.18f, 0.20f, 0.94f), // 2nd 深暗银底
            Color(0.18f, 0.14f, 0.10f, 0.94f), // 3rd 深暗铜底
            Color(0.20f, 0.10f, 0.10f, 0.94f)  // 4th 深暗红底
        )
        for (i in 0 until 4) {
            shape.color = rowColors[i]
            shape.rect(modalRect.x + 30f, rowYPositions[i], modalRect.width - 60f, 44f)
        }

        // 按钮底色
        // 返回菜单按钮
        shape.color = Color(0.20f, 0.55f, 0.85f, 1f)
        shape.rect(btnReturnToMenuRect.x, btnReturnToMenuRect.y, btnReturnToMenuRect.width, btnReturnToMenuRect.height)

        // 本局明细按钮（若有）
        if (hasRoundDetails) {
            shape.color = Color(0.18f, 0.45f, 0.30f, 1f)
            shape.rect(btnRoundDetailsRect.x, btnRoundDetailsRect.y, btnRoundDetailsRect.width, btnRoundDetailsRect.height)
        }

        shape.end()

        // 2. 边框线条 (ShapeRenderer Line)
        shape.begin(ShapeRenderer.ShapeType.Line)

        // 弹窗金色主边框
        shape.color = Color(0.95f, 0.85f, 0.40f, 1f)
        shape.rect(modalRect.x, modalRect.y, modalRect.width, modalRect.height)

        // 四人名次行边框
        for (i in 0 until 4) {
            shape.color = when (i) {
                0 -> Color(1.0f, 0.85f, 0.35f, 0.8f)
                1 -> Color(0.80f, 0.85f, 0.90f, 0.7f)
                2 -> Color(0.80f, 0.60f, 0.40f, 0.6f)
                else -> Color(0.70f, 0.40f, 0.40f, 0.6f)
            }
            shape.rect(modalRect.x + 30f, rowYPositions[i], modalRect.width - 60f, 44f)
        }

        // 按钮白色边框
        shape.color = Color.WHITE
        shape.rect(btnReturnToMenuRect.x, btnReturnToMenuRect.y, btnReturnToMenuRect.width, btnReturnToMenuRect.height)
        if (hasRoundDetails) {
            shape.rect(btnRoundDetailsRect.x, btnRoundDetailsRect.y, btnRoundDetailsRect.width, btnRoundDetailsRect.height)
        }

        shape.end()

        // 3. 文字内容渲染 (SpriteBatch)
        batch.projectionMatrix = camera.combined
        batch.begin()

        val origScaleX = textFont.data.scaleX
        val origScaleY = textFont.data.scaleY
        val origColor = Color(textFont.color)

        try {
            // 3.1 弹窗大标题
            if (!isCustomFont) textFont.data.setScale(1.5f)
            textFont.color = Color(1.0f, 0.88f, 0.35f, 1f)
            val titleText = if (isCustomFont) "比赛总成绩" else "MATCH FINAL RESULTS"
            textFont.draw(batch, titleText, modalRect.x + 40f, modalRect.y + 510f)

            // 3.2 终局原因（使用规范中文：击飞终局 / 正常终局 / 延长战结束 / 庄家终了，不展示括号中的枚举代码；缺 finalResult 不伪造原因）
            if (!isCustomFont) textFont.data.setScale(1.05f)
            if (finalResult != null) {
                val reasonZh = when (finalResult.reason) {
                    "BANKRUPTCY" -> "击飞终局"
                    "NORMAL_COMPLETION" -> "正常终局"
                    "WEST_COMPLETION" -> "延长战结束"
                    "AGARI_YAME" -> "庄家终了"
                    "ABORTIVE" -> "途中流局"
                    else -> finalResult.reason
                }
                val reasonEn = when (finalResult.reason) {
                    "BANKRUPTCY" -> "BANKRUPTCY"
                    "NORMAL_COMPLETION" -> "NORMAL COMPLETION"
                    "WEST_COMPLETION" -> "WEST COMPLETION"
                    "AGARI_YAME" -> "AGARI-YAME"
                    "ABORTIVE" -> "ABORTIVE DRAW"
                    else -> finalResult.reason
                }
                textFont.color = if (finalResult.reason == "BANKRUPTCY") Color(1.0f, 0.45f, 0.35f, 1f) else Color(0.85f, 0.95f, 0.85f, 1f)
                val reasonLabel = if (isCustomFont) "终局原因: $reasonZh" else "Reason: $reasonEn"
                textFont.draw(batch, reasonLabel, modalRect.x + 40f, modalRect.y + 465f)
            } else {
                // 兼容入口无 finalResult 时绝不伪造终局原因
                textFont.color = Color(0.80f, 0.85f, 0.80f, 1f)
                val fallbackText = if (isCustomFont) "比赛已结束" else "Match Ended"
                textFont.draw(batch, fallbackText, modalRect.x + 40f, modalRect.y + 465f)
            }

            // 3.3 名次列表表头
            if (!isCustomFont) textFont.data.setScale(0.95f)
            textFont.color = Color(0.70f, 0.80f, 0.70f, 1f)
            val rankColX = modalRect.x + 50f
            val playerColX = modalRect.x + 220f
            val scoreColX = modalRect.x + 480f
            val cpColX = modalRect.x + 750f

            val rankHead = if (isCustomFont) "名次" else "RANK"
            val playerHead = if (isCustomFont) "席位" else "PLAYER"
            val scoreHead = if (isCustomFont) "最终得点" else "FINAL SCORE"
            val cpHead = if (isCustomFont) "出千点数" else "CHEAT PTS"

            textFont.draw(batch, rankHead, rankColX, modalRect.y + 420f)
            textFont.draw(batch, playerHead, playerColX, modalRect.y + 420f)
            textFont.draw(batch, scoreHead, scoreColX, modalRect.y + 420f)
            textFont.draw(batch, cpHead, cpColX, modalRect.y + 420f)

            // 3.4 四人最终排名行
            val rankedSeats = if (finalResult != null && finalResult.rankedSeats.size == 4) {
                finalResult.rankedSeats
            } else {
                (0..3).sortedByDescending { currentScores.getOrElse(it) { 0 } }
            }

            for (i in 0 until 4) {
                val seat = rankedSeats[i]
                val rowY = rowYPositions[i] + 30f

                // 名次文字与高亮颜色
                val rankLabel = if (isCustomFont) {
                    when (i) {
                        0 -> "1 位 (冠)"
                        1 -> "2 位 (亚)"
                        2 -> "3 位 (季)"
                        else -> "4 位 (殿)"
                    }
                } else {
                    when (i) {
                        0 -> "1st Place"
                        1 -> "2nd Place"
                        2 -> "3rd Place"
                        else -> "4th Place"
                    }
                }
                textFont.color = when (i) {
                    0 -> Color(1.0f, 0.88f, 0.35f, 1f)
                    1 -> Color(0.85f, 0.90f, 0.95f, 1f)
                    2 -> Color(0.85f, 0.65f, 0.45f, 1f)
                    else -> Color(0.85f, 0.55f, 0.55f, 1f)
                }
                if (!isCustomFont) textFont.data.setScale(1.05f)
                textFont.draw(batch, rankLabel, rankColX, rowY)

                // 玩家席位（只显示“座位1/2/3/4”和“你”的标记，不把其他席位猜为 AI）
                val seatNum = seat + 1
                val pName = if (seat == viewerSeat) {
                    if (isCustomFont) "座位$seatNum [你]" else "Seat $seatNum [YOU]"
                } else {
                    if (isCustomFont) "座位$seatNum" else "Seat $seatNum"
                }
                textFont.color = if (seat == viewerSeat) Color(0.3f, 1.0f, 0.85f, 1f) else Color.WHITE
                textFont.draw(batch, pName, playerColX, rowY)

                // 最终得点（不按 score<0 追加击飞标签，负分仅着色）
                val score = if (finalResult != null && seat < finalResult.finalScores.size) {
                    finalResult.finalScores[seat]
                } else {
                    currentScores.getOrElse(seat) { 0 }
                }
                val isNegative = (score < 0)
                val scoreStr = "$score pts"
                textFont.color = if (isNegative) Color(1.0f, 0.45f, 0.45f, 1f) else Color.WHITE
                textFont.draw(batch, scoreStr, scoreColX, rowY)

                // 出千点数（若有）
                val cheatPt = finalResult?.finalCheatPoints?.getOrNull(seat)
                val cheatPtStr = if (cheatPt != null) "$cheatPt" else "--"
                textFont.color = Color(0.75f, 0.90f, 0.75f, 1f)
                textFont.draw(batch, cheatPtStr, cpColX, rowY)
            }

            // 3.5 按钮文本
            if (!isCustomFont) textFont.data.setScale(1.15f)

            // 返回菜单按钮
            textFont.color = Color.WHITE
            val returnLabel = if (isCustomFont) "返回主菜单" else "RETURN TO MENU"
            glyphLayout.setText(textFont, returnLabel)
            val returnTextX = btnReturnToMenuRect.x + (btnReturnToMenuRect.width - glyphLayout.width) / 2f
            textFont.draw(batch, returnLabel, returnTextX, btnReturnToMenuRect.y + 31f)

            // 本局明细按钮（若有）
            if (hasRoundDetails) {
                textFont.color = Color.WHITE
                val detailLabel = if (isCustomFont) "查看本局明细" else "ROUND DETAILS"
                glyphLayout.setText(textFont, detailLabel)
                val detailTextX = btnRoundDetailsRect.x + (btnRoundDetailsRect.width - glyphLayout.width) / 2f
                textFont.draw(batch, detailLabel, detailTextX, btnRoundDetailsRect.y + 31f)
            }
        } finally {
            textFont.data.setScale(origScaleX, origScaleY)
            textFont.color = origColor
        }

        batch.end()
    }
}
