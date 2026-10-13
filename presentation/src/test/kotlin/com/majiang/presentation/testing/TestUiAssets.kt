package com.majiang.presentation.testing

import com.majiang.presentation.common.UiAssets
import com.majiang.presentation.common.render.SkinManager

/**
 * 无头测试专用共享资产辅助对象。
 */
object TestUiAssets {
    fun create(): UiAssets = UiAssets.createDefault(SkinManager())
}
