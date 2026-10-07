package com.majiang.presentation.common

import com.majiang.client.api.ScoreBreakdownView
import com.majiang.client.api.TileView

/**
 * 结算明细分页与展示标签分类
 */
enum class SettlementSection {
    YAKU,
    DORA,
    CALC
}

/**
 * 结算明细单行数据（纯函数与表现层协议）
 *
 * @property text 行内展示的文本说明
 * @property tiles 该行附加展示的麻将牌列表（指示牌、目标牌、命中牌等，最多支持6张）
 */
data class SettlementDetailRow(
    val text: String,
    val tiles: List<TileView> = emptyList()
)

/**
 * 结算明细推导与分页呈现纯函数对象（契约驱动）
 */
object SettlementDetailPresenter {
    const val ROWS_PER_PAGE = 6

    /**
     * 针对指定标签提取全部推导行
     */
    fun rows(b: ScoreBreakdownView, section: SettlementSection): List<SettlementDetailRow> = when (section) {
        SettlementSection.YAKU -> buildYakuRows(b)
        SettlementSection.DORA -> buildDoraRows(b)
        SettlementSection.CALC -> buildCalcRows(b)
    }

    /**
     * 计算指定标签下的总页数（至少1页）
     */
    fun pageCount(b: ScoreBreakdownView, section: SettlementSection): Int {
        val total = rows(b, section).size
        return if (total == 0) 1 else (total + ROWS_PER_PAGE - 1) / ROWS_PER_PAGE
    }

    /**
     * 获取指定标签下第 index 页（0-based）的明细行切片
     */
    fun page(b: ScoreBreakdownView, section: SettlementSection, index: Int): List<SettlementDetailRow> {
        val all = rows(b, section)
        if (index < 0 || all.isEmpty()) return emptyList()
        val fromIndex = index * ROWS_PER_PAGE
        if (fromIndex >= all.size) return emptyList()
        val toIndex = minOf(fromIndex + ROWS_PER_PAGE, all.size)
        return all.subList(fromIndex, toIndex)
    }

    // ==========================================
    // 1. 役种部分 (YAKU)
    // ==========================================
    private fun buildYakuRows(b: ScoreBreakdownView): List<SettlementDetailRow> {
        val list = mutableListOf<SettlementDetailRow>()
        val isYakuman = (b.yakumanCount > 0 || b.score.isYakuman)

        if (isYakuman) {
            val mult = if (b.yakumanCount > 0) b.yakumanCount else maxOf(1, b.score.yakumanMultiplier)
            if (b.yakuLines.isNotEmpty()) {
                for ((name, han) in b.yakuLines) {
                    val label = if (han > 1) "$name 役满 ($han 倍)" else "$name 役满"
                    list.add(SettlementDetailRow(label))
                }
            } else {
                list.add(SettlementDetailRow("${b.score.title} 役满"))
            }
            list.add(SettlementDetailRow("【拆解说明】已选最高得点拆解方案"))
            list.add(SettlementDetailRow("役满合计：${mult}倍役满 (固定基本点 ${mult * 8000} 点)"))
            list.add(SettlementDetailRow("计分规则：常规役种番数与宝牌加番规则在役满中不适用"))
            if (b.dora.totalHan > 0) {
                list.add(SettlementDetailRow("宝牌提示：命中宝牌 ${b.dora.totalHan} 次 (役满达成，常规加番规则不适用，命中但不计番)"))
            }
        } else {
            for ((name, han) in b.yakuLines) {
                list.add(SettlementDetailRow("$name $han 番"))
            }
            list.add(SettlementDetailRow("【拆解说明】已选最高得点拆解方案"))
            val yakuSubtotal = b.yakuLines.sumOf { it.second }
            list.add(SettlementDetailRow("役种番数小计：$yakuSubtotal 番"))
            list.add(
                SettlementDetailRow(
                    "宝牌番数分项：表宝牌 ${b.dora.indicatorCount} 番 | 里宝牌 ${b.dora.uraCount} 番 | 赤宝牌 ${b.dora.redCount} 番 (宝牌小计 ${b.dora.totalHan} 番)"
                )
            )
            list.add(
                SettlementDetailRow(
                    "总番数等式：役种 $yakuSubtotal 番 + 宝牌 ${b.dora.totalHan} 番 = 合计 ${b.score.han} 番"
                )
            )
        }
        return list
    }

    // ==========================================
    // 2. 宝牌部分 (DORA)
    // ==========================================
    private fun buildDoraRows(b: ScoreBreakdownView): List<SettlementDetailRow> {
        val list = mutableListOf<SettlementDetailRow>()
        val isYakuman = (b.yakumanCount > 0 || b.score.isYakuman)

        if (isYakuman) {
            list.add(SettlementDetailRow("【宝牌明细】(役满达成：常规加番规则不适用，所有宝牌命中仅展示，不计入翻数与基本点)"))
        } else {
            list.add(
                SettlementDetailRow(
                    "【宝牌明细】表宝牌 ${b.dora.indicatorCount} 番 | 里宝牌 ${b.dora.uraCount} 番 | 赤宝牌 ${b.dora.redCount} 番 (宝牌总计 ${b.dora.totalHan} 番)"
                )
            )
        }
        list.add(SettlementDetailRow("牌面图例：第1张为指示牌，第2张为宝牌目标，后续为手牌与副露中实际命中物理牌"))

        // 2.1 表宝牌：直接读取 b.dora.indicatorHits，每个指示牌逐项生成
        if (b.dora.indicatorHits.isNotEmpty()) {
            for ((idx, hit) in b.dora.indicatorHits.withIndex()) {
                val hitCount = hit.tiles.size
                val scoreSuffix = if (isYakuman) "命中 ${hitCount} 张 (役满不计番)" else "命中 ${hitCount} 张 +${hitCount} 番"
                val text = "表指示牌 ${idx + 1} [${tileName(hit.indicator)}] → 宝牌 [${tileName(hit.target)}]：$scoreSuffix"
                val rowTiles = listOf(hit.indicator, hit.target) + hit.tiles
                list.add(SettlementDetailRow(text, rowTiles))
            }
        } else if (b.dora.indicatorTiles.isNotEmpty()) {
            val hitCount = b.dora.indicatorTiles.size
            val scoreSuffix = if (isYakuman) "命中 ${hitCount} 张 (役满不计番)" else "命中 ${hitCount} 张 +${b.dora.indicatorCount} 番"
            list.add(SettlementDetailRow("表宝牌：$scoreSuffix", b.dora.indicatorTiles))
        } else {
            list.add(SettlementDetailRow("表宝牌：未命中表宝牌 (0 番)"))
        }

        // 2.2 里宝牌：直接读取 b.uraEligible，未立直绝不展示指示牌与命中
        if (!b.uraEligible) {
            list.add(SettlementDetailRow("里宝牌：未立直，里宝牌不适用 (不展示里指示牌与牌面)"))
        } else {
            if (b.dora.uraHits.isNotEmpty()) {
                for ((idx, hit) in b.dora.uraHits.withIndex()) {
                    val hitCount = hit.tiles.size
                    val scoreSuffix = if (isYakuman) "命中 ${hitCount} 张 (役满不计番)" else "命中 ${hitCount} 张 +${hitCount} 番"
                    val text = "里指示牌 ${idx + 1} [${tileName(hit.indicator)}] → 宝牌 [${tileName(hit.target)}]：$scoreSuffix"
                    val rowTiles = listOf(hit.indicator, hit.target) + hit.tiles
                    list.add(SettlementDetailRow(text, rowTiles))
                }
            } else if (b.dora.uraTiles.isNotEmpty()) {
                val hitCount = b.dora.uraTiles.size
                val scoreSuffix = if (isYakuman) "命中 ${hitCount} 张 (役满不计番)" else "命中 ${hitCount} 张 +${b.dora.uraCount} 番"
                list.add(SettlementDetailRow("里宝牌：$scoreSuffix", b.dora.uraTiles))
            } else {
                list.add(SettlementDetailRow("里宝牌：已立直，未命中里宝牌 (0 番)"))
            }
        }

        // 2.3 赤宝牌
        if (b.dora.redCount > 0) {
            val scoreSuffix = if (isYakuman) {
                "命中 ${b.dora.redCount} 张 (各红五每张1番，役满不计番)"
            } else {
                "命中 ${b.dora.redCount} 张 (每张1番，合计 +${b.dora.redCount} 番)"
            }
            list.add(SettlementDetailRow("赤宝牌：$scoreSuffix", b.dora.redTiles))
        } else {
            list.add(SettlementDetailRow("赤宝牌：无赤宝牌 (0 番)"))
        }

        // 2.4 规则说明
        list.add(SettlementDetailRow("叠加规则：重复指示牌独立计次加算；赤宝牌可与表/里宝牌多重叠加加番 (总番按累计命中加番)"))
        if (isYakuman) {
            list.add(SettlementDetailRow("役满规则：常规加番规则在本役满中不适用，宝牌命中仅展示不计入总番"))
        }
        return list
    }

    // ==========================================
    // 3. 计算部分 (CALC)
    // ==========================================
    private fun buildCalcRows(b: ScoreBreakdownView): List<SettlementDetailRow> {
        val list = mutableListOf<SettlementDetailRow>()
        val isYakuman = (b.yakumanCount > 0 || b.score.isYakuman)
        val isDealer = b.isDealer
        val isTsumo = b.isTsumo
        val rawExact = b.score.rawBasePointsExact

        if (isYakuman) {
            val mult = if (b.yakumanCount > 0) b.yakumanCount else maxOf(1, b.score.yakumanMultiplier)
            val base = mult * 8000
            list.add(SettlementDetailRow("【总番组成】役满达成 (${b.score.title})"))
            list.add(SettlementDetailRow("【符数计算】役满跳过常规符数计算 (固定基本点)"))
            list.add(SettlementDetailRow("【基本点公式】8000 × $mult 倍役满 = $base 点 (固定役满基本点)"))

            when {
                isDealer && isTsumo -> {
                    val each = base * 2
                    list.add(SettlementDetailRow("【支付倍率】庄家自摸：三家闲家各付 基本点×2 = $each 点 (向上取整至百位)"))
                    list.add(SettlementDetailRow("基础支付合计：三家闲家各付 $each 点，基础支付小计 ${each * 3} 点"))
                }
                isDealer && !isTsumo -> {
                    val total = base * 6
                    list.add(SettlementDetailRow("【支付倍率】庄家荣和：放铳者独付 基本点×6 = $total 点 (向上取整至百位)"))
                }
                !isDealer && isTsumo -> {
                    val dealerPay = base * 2
                    val nonDealerPay = base * 1
                    list.add(SettlementDetailRow("【支付倍率(庄家)】闲家自摸：庄家付 基本点×2 = $dealerPay 点 (向上取整至百位)"))
                    list.add(SettlementDetailRow("【支付倍率(闲家)】闲家自摸：两闲家各付 基本点×1 = $nonDealerPay 点 (两家各付)"))
                    list.add(SettlementDetailRow("基础支付合计：庄家 $dealerPay 点 + 闲家 $nonDealerPay × 2 点 = ${dealerPay + nonDealerPay * 2} 点"))
                }
                else -> {
                    val total = base * 4
                    list.add(SettlementDetailRow("【支付倍率】闲家荣和：放铳者独付 基本点×4 = $total 点 (向上取整至百位)"))
                }
            }
        } else {
            val yakuSubtotal = b.yakuLines.sumOf { it.second }
            list.add(SettlementDetailRow("【总番组成】役种 $yakuSubtotal 番 + 宝牌 ${b.dora.totalHan} 番 = 合计 ${b.score.han} 番"))

            // 符数明细：Fu 逐项独立行；特例明确直接定符
            val isChiitoitsu = (b.fu.finalFu == 25 || b.fu.specialNote?.contains("七对子") == true)
            val isPinfuTsumo = (b.fu.specialNote?.contains("平和自摸") == true)

            if (isChiitoitsu) {
                for (line in b.fu.lines) {
                    list.add(SettlementDetailRow("符数明细：${line.description} ${line.fu} 符"))
                }
                list.add(SettlementDetailRow("符数计算规则：七对子固定 25 符 (特例直接定符，不向十位取整进位)"))
            } else if (isPinfuTsumo) {
                for (line in b.fu.lines) {
                    list.add(SettlementDetailRow("符数明细：${line.description} ${line.fu} 符"))
                }
                list.add(SettlementDetailRow("符数计算规则：门前平和自摸固定 20 符 (特例直接定符，舍弃2符自摸，不向十位进位)"))
            } else if (b.fu.lines.isNotEmpty()) {
                for (line in b.fu.lines) {
                    list.add(SettlementDetailRow("符数明细：${line.description} +${line.fu} 符"))
                }
                list.add(SettlementDetailRow("符数小计：${b.fu.subtotal} 符"))
                if (b.fu.subtotal == b.fu.roundedUpTo) {
                    if (b.fu.roundedUpTo == b.fu.finalFu) {
                        list.add(SettlementDetailRow("符数进位：小计 ${b.fu.subtotal} 符已整十，最终计 ${b.fu.roundedUpTo} 符"))
                    } else {
                        list.add(SettlementDetailRow("符数进位：小计 ${b.fu.subtotal} 符已整十，向上取整至十位：${b.fu.roundedUpTo} 符"))
                    }
                } else {
                    list.add(SettlementDetailRow("符数进位：小计 ${b.fu.subtotal} 符 → 切上向上取整至十位：${b.fu.roundedUpTo} 符"))
                }
                if (b.fu.finalFu != b.fu.roundedUpTo) {
                    val note = b.fu.specialNote ?: "荣和 30符保底"
                    list.add(SettlementDetailRow("符数保底说明：$note，最终计 ${b.fu.finalFu} 符"))
                } else if (b.fu.specialNote != null) {
                    list.add(SettlementDetailRow("符数特例说明：${b.fu.specialNote}"))
                }
            } else {
                val noteSuffix = if (b.fu.specialNote != null) " (${b.fu.specialNote})" else ""
                list.add(SettlementDetailRow("符数计算规则：${b.fu.finalFu} 符$noteSuffix"))
            }

            // 基本点原始公式与等号结果拆分为2条，支持高番大整数安全展示
            list.add(SettlementDetailRow("基本点计算：${b.score.fu} 符 × 2^(2 + ${b.score.han} 番)"))
            list.add(SettlementDetailRow("原始基本点：$rawExact 点"))

            if (b.score.capReason != null) {
                list.add(SettlementDetailRow("上限封顶规则：达到【${b.score.capReason}】封顶，基本点调整为 ${b.score.basePoints} 点"))
            } else {
                list.add(SettlementDetailRow("上限封顶规则：未达到满贯封顶线，基本点维持 ${b.score.basePoints} 点"))
            }

            val base = b.score.basePoints
            when {
                isDealer && isTsumo -> {
                    val rawPay = base * 2
                    val roundPay = roundUp100(rawPay)
                    list.add(SettlementDetailRow("【支付倍率】庄家自摸：三家闲家各付 基本点×2"))
                    list.add(
                        SettlementDetailRow(
                            "闲家支付计算：${base} × 2 = $rawPay 点 → 向上取整至百位：$roundPay 点 (三人合计 ${roundPay * 3} 点)"
                        )
                    )
                }
                isDealer && !isTsumo -> {
                    val rawPay = base * 6
                    val roundPay = roundUp100(rawPay)
                    list.add(SettlementDetailRow("【支付倍率】庄家荣和：放铳者独付 基本点×6"))
                    list.add(
                        SettlementDetailRow(
                            "放铳支付计算：${base} × 6 = $rawPay 点 → 向上取整至百位：$roundPay 点"
                        )
                    )
                }
                !isDealer && isTsumo -> {
                    val rawDealer = base * 2
                    val roundDealer = roundUp100(rawDealer)
                    val rawNonDealer = base * 1
                    val roundNonDealer = roundUp100(rawNonDealer)
                    list.add(SettlementDetailRow("【支付倍率(庄家)】庄家付 基本点×2 = $rawDealer 点 → 向上取整至百位：$roundDealer 点"))
                    list.add(SettlementDetailRow("【支付倍率(闲家)】两闲家各付 基本点×1 = $rawNonDealer 点 → 向上取整至百位：$roundNonDealer 点 (两家各付)"))
                    list.add(
                        SettlementDetailRow(
                            "基础支付小计：庄家 $roundDealer 点 + 闲家 $roundNonDealer × 2 点 = ${roundDealer + roundNonDealer * 2} 点"
                        )
                    )
                }
                else -> {
                    val rawPay = base * 4
                    val roundPay = roundUp100(rawPay)
                    list.add(SettlementDetailRow("【支付倍率】闲家荣和：放铳者独付 基本点×4"))
                    list.add(
                        SettlementDetailRow(
                            "放铳支付计算：${base} × 4 = $rawPay 点 → 向上取整至百位：$roundPay 点"
                        )
                    )
                }
            }
        }

        if (b.paymentLines.isNotEmpty()) {
            list.add(SettlementDetailRow("基础支付分配：${b.paymentLines.joinToString("; ")}"))
        }

        // 本场棒与供托棒独立分条：自摸以 100 × 本场计算
        val honbaCount = b.honbaAddition / 300
        val riichiCount = b.riichiAddition / 1000
        if (honbaCount > 0) {
            if (isTsumo) {
                list.add(SettlementDetailRow("本场棒加算：$honbaCount 本场 (每人加付 100×$honbaCount = ${100 * honbaCount} 点，三人合计 +${b.honbaAddition} 点)"))
            } else {
                list.add(SettlementDetailRow("本场棒加算：$honbaCount 本场 (放铳者独付 300×$honbaCount = +${b.honbaAddition} 点)"))
            }
        } else {
            list.add(SettlementDetailRow("本场棒加算：0 本场 (+0 点)"))
        }

        if (riichiCount > 0) {
            list.add(SettlementDetailRow("供托立直棒：$riichiCount 根 (独得 $riichiCount × 1000 = +${b.riichiAddition} 点)"))
        } else {
            list.add(SettlementDetailRow("供托立直棒：0 根 (+0 点)"))
        }

        // 最终结算总得点，明确列出具体数字加法
        val baseTotal = b.totalPoints - b.honbaAddition - b.riichiAddition
        list.add(
            SettlementDetailRow(
                "最终结算总得点：$baseTotal 点 (基础) + ${b.honbaAddition} 点 (本场) + ${b.riichiAddition} 点 (供托) = ${b.totalPoints} 点"
            )
        )
        return list
    }

    // ==========================================
    // 4. 辅助工具
    // ==========================================
    private fun roundUp100(value: Int): Int = (value + 99) / 100 * 100

    private fun tileName(tile: TileView): String =
        if (tile.isRedFive) "赤${tile.displayName()}" else tile.displayName()
}
