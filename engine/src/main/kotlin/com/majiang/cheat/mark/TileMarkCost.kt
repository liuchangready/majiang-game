package com.majiang.cheat.mark

import com.majiang.economy.CheatEconomyConfig

/**
 * 落汗费用的唯一解析入口。
 *
 * 投影、权威提交预检与实际扣费都必须使用同一取值，禁止在任一环节硬编码 10。
 */
object TileMarkCost {
    const val DEFAULT: Int = 10

    fun of(economy: CheatEconomyConfig): Int = economy.costs["TILE_MARK"] ?: DEFAULT
}
