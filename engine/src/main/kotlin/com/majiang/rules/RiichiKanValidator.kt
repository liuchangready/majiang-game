package com.majiang.rules

import com.majiang.model.Meld
import com.majiang.model.MeldType
import com.majiang.model.PlayerHand
import com.majiang.model.Tile

/**
 * 立直后暗杠合法性校验器 (WRC 2022 §8.9.1)
 *
 * 核心契约与规则设计：
 * 1. 门清且摸牌参与：hand 必须门清 (isClosed)，drawnTile 必须非空且必须为 kanTiles 的四张之一。
 * 2. 物理与持有校验：kanTiles 必须是己方确切持有的 4 张互异同种牌 (copyIndex 0..3 互异)，
 *    除 drawnTile 外的其余 3 张必须确切来自 closedTiles，严禁借用他家牌或手牌未持有的物理副本。
 * 3. 待牌集合不变：以摸牌前 closedTiles 为原手牌，剔除原刻子后的 closedTiles 为杠后手牌；
 *    两边枚举 34 种候选和牌，排除算上已有副露（含已有暗杠与本次新暗杠）后的第 5 张同种牌；
 *    原手牌听牌集合非空，且杠前后待牌集合严格相等 (waitsOrig == waitsPost)。
 * 4. 手牌结构不变：对每一张原听牌的所有和牌分解，均必须全为标准形 (REGULAR) 并含该完整刻子；
 *    任何顺子、雀头或特殊形 (七对子/国士无双) 的替代解释均予以拒绝；
 *    移除此刻子后的规范化正规分解集合，必须与杠后正规分解集合完全一致。
 * 5. 纯函数设计：纯校验，零副作用，不修改 hand 或其他对局状态。
 */
object RiichiKanValidator {

    /**
     * 校验立直暗杠是否满足 WRC 2022 §8.9.1
     */
    fun canDeclare(hand: PlayerHand, kanTiles: List<Tile>): Boolean {
        // 1. 门清检查（无明副露，已有暗杠不破坏门前清）
        if (!hand.isClosed) return false

        // 2. 摸牌非空检查
        val drawn = hand.drawnTile ?: return false

        // 3. 必须恰好 4 张牌
        if (kanTiles.size != 4) return false

        // 4. 同种牌校验 (相同 suit 和 rank，即 typeIndex 一致)
        val targetType = kanTiles[0].typeIndex
        if (kanTiles.any { it.typeIndex != targetType }) return false

        // 5. 物理副本互异性校验 (4 张牌的 copyIndex 必须各不相同，即覆盖 0..3)
        if (kanTiles.map { it.copyIndex }.distinct().size != 4) return false

        // 6. 本巡摸牌必须参与本次暗杠 (四张已在手内、摸入别种牌时不可暗杠)
        if (drawn !in kanTiles) return false

        // 7. 其余三张必须来自 hand.closedTiles
        val otherThree = kanTiles.filter { it != drawn }
        if (otherThree.size != 3) return false

        // 8. 手牌规模校验 (门前手牌张数需满足 13 - melds.size * 3)
        if (hand.melds.size !in 0..3) return false
        if (hand.closedTiles.size != 13 - hand.melds.size * 3) return false

        // 9. 物理确切持有校验：从 closedTiles 中精确核减 otherThree 的 3 张牌
        val remainingClosed = hand.closedTiles.toMutableList()
        for (tile in otherThree) {
            if (!remainingClosed.remove(tile)) {
                return false
            }
        }

        val origClosed = hand.closedTiles
        val postClosed = remainingClosed

        // 杠后手牌包含原已有副露加上本次新暗杠
        val postMelds = hand.melds + Meld(
            type = MeldType.CLOSED_KAN,
            tiles = kanTiles,
            fromPlayer = -1,
            calledTile = drawn
        )

        val decomposer = HandDecomposer.INSTANCE

        // 10. 计算原手牌与杠后手牌的听牌集合（排除第 5 张牌）
        val waitsOrig = calculateWaits(origClosed, hand.melds, decomposer)
        val waitsPost = calculateWaits(postClosed, postMelds, decomposer)

        // 原听牌集合必须非空，且杠前后待牌集合严格相等
        if (waitsOrig.isEmpty()) return false
        if (waitsOrig != waitsPost) return false

        // 11. 针对每张原待牌，深度验证手牌分解结构的一致性
        for (winType in waitsOrig) {
            val winTile = Tile.fromTypeIndex(winType)

            // 原手牌 + 和了牌 的所有分解
            val testOrigTiles = origClosed + winTile
            val decompsOrig = decomposer.decompose(testOrigTiles)
            if (decompsOrig.isEmpty()) return false

            // 杠后手牌 + 和了牌 的所有分解
            val testPostTiles = postClosed + winTile
            val decompsPost = decomposer.decompose(testPostTiles)
            if (decompsPost.isEmpty()) return false

            // 杠后分解必须全为标准形 (REGULAR)
            if (decompsPost.any { it.pattern != HandDecomposer.DecompPattern.REGULAR }) {
                return false
            }

            // 原手牌分解必须全为标准形，且每组分解均必须包含该完整刻子
            val normOrigDecomps = mutableListOf<NormalizedDecomposition>()
            for (decomp in decompsOrig) {
                if (decomp.pattern != HandDecomposer.DecompPattern.REGULAR) {
                    // 七对子、国士无双等特殊形拒绝
                    return false
                }

                // 查找该 targetType 的刻子 (isTriple && typeIndices == targetType)
                val tripletIndex = decomp.mentsu.indexOfFirst {
                    it.isTriple && it.typeIndices.all { idx -> idx == targetType }
                }
                if (tripletIndex == -1) {
                    // 该牌型存在顺子或雀头等替代解释，拒绝暗杠
                    return false
                }

                // 移除此刻子并规范化剩余面子顺序
                val remainingMentsu = decomp.mentsu.toMutableList()
                remainingMentsu.removeAt(tripletIndex)
                val normalizedMentsu = remainingMentsu.map { normalizeMentsu(it) }.sorted()
                normOrigDecomps.add(NormalizedDecomposition(decomp.jantai, normalizedMentsu))
            }

            val normPostDecomps = decompsPost.map { decomp ->
                val normalizedMentsu = decomp.mentsu.map { normalizeMentsu(it) }.sorted()
                NormalizedDecomposition(decomp.jantai, normalizedMentsu)
            }

            // 移除刻子后的规范化正规分解集合，必须与杠后分解集合严格一致（多重集与集合均相等）
            if (normOrigDecomps.sorted() != normPostDecomps.sorted()) {
                return false
            }
        }

        return true
    }

    /**
     * 计算手牌的待牌集合（枚举 34 种牌，排除第 5 张牌）
     */
    private fun calculateWaits(
        closedTiles: List<Tile>,
        melds: List<Meld>,
        decomposer: HandDecomposer
    ): Set<Int> {
        val waits = mutableSetOf<Int>()
        for (candidate in 0 until 34) {
            // 排除算上已有副露（含暗杠）后的第五张同种牌
            val usedCount = closedTiles.count { it.typeIndex == candidate } +
                    melds.sumOf { meld -> meld.tiles.count { it.typeIndex == candidate } }
            if (usedCount >= 4) continue

            val candidateTile = Tile.fromTypeIndex(candidate)
            val testTiles = closedTiles + candidateTile
            if (decomposer.canWin(testTiles)) {
                waits.add(candidate)
            }
        }
        return waits
    }

    private fun normalizeMentsu(group: HandDecomposer.MentsuGroup): NormalizedMentsu {
        return NormalizedMentsu(
            isSequence = group.isSequence,
            typeIndices = group.typeIndices.sorted()
        )
    }

    private data class NormalizedMentsu(
        val isSequence: Boolean,
        val typeIndices: List<Int>
    ) : Comparable<NormalizedMentsu> {
        override fun compareTo(other: NormalizedMentsu): Int {
            if (this.isSequence != other.isSequence) {
                return this.isSequence.compareTo(other.isSequence)
            }
            val minSize = minOf(this.typeIndices.size, other.typeIndices.size)
            for (i in 0 until minSize) {
                val cmp = this.typeIndices[i].compareTo(other.typeIndices[i])
                if (cmp != 0) return cmp
            }
            return this.typeIndices.size.compareTo(other.typeIndices.size)
        }
    }

    private data class NormalizedDecomposition(
        val jantai: Int,
        val mentsu: List<NormalizedMentsu>
    ) : Comparable<NormalizedDecomposition> {
        override fun compareTo(other: NormalizedDecomposition): Int {
            val jcmp = this.jantai.compareTo(other.jantai)
            if (jcmp != 0) return jcmp
            val scmp = this.mentsu.size.compareTo(other.mentsu.size)
            if (scmp != 0) return scmp
            for (i in this.mentsu.indices) {
                val mcmp = this.mentsu[i].compareTo(other.mentsu[i])
                if (mcmp != 0) return mcmp
            }
            return 0
        }
    }
}
