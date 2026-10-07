package com.majiang.rules

import com.majiang.model.MeldType
import com.majiang.model.PlayerHand
import com.majiang.model.Suit
import com.majiang.model.Tile
import com.majiang.model.Meld

/**
 * 麻将役种定义
 */
enum class Yaku(val displayName: String, val hanClosed: Int, val hanOpen: Int, val isYakuman: Boolean = false) {
    // 1翻
    RIICHI("立直", 1, 0),
    IPPATSU("一发", 1, 0),
    MENZEN_TSUMO("门前清自摸和", 1, 0),
    TANYAO("断幺九", 1, 1),
    PINFU("平和", 1, 0),
    IIPEIKO("一杯口", 1, 0),
    YAKUHAI_HAKU("役牌 白", 1, 1),
    YAKUHAI_HATSU("役牌 发", 1, 1),
    YAKUHAI_CHUN("役牌 中", 1, 1),
    YAKUHAI_BAKAZE("场风牌", 1, 1),
    YAKUHAI_JIKAZE("自风牌", 1, 1),
    HAITEI("海底摸月", 1, 1),
    HOUTEI("河底捞鱼", 1, 1),
    RINSHAN("岭上开花", 1, 1),
    CHANKAN("抢杠", 1, 1),
    // 2翻
    DOUBLE_RIICHI("两立直", 2, 0),
    CHANTA("混全带幺九", 2, 1),
    ITTSU("一气通贯", 2, 1),
    SAN_SHOKU_DOUJUN("三色同顺", 2, 1),
    SAN_SHOKU_DOUKOU("三色同刻", 2, 2),
    TOITOI("对对和", 2, 2),
    SAN_ANKOU("三暗刻", 2, 2),
    SAN_KANTSU("三杠子", 2, 2),
    SHOUSANGEN("小三元", 2, 2),
    HONROUTOU("混老头", 2, 2),
    CHIITOITSU("七对子", 2, 0),
    // 3翻
    HONITSU("混一色", 3, 2),
    JUNCHAN("纯全带幺九", 3, 2),
    RYANPEIKOU("两杯口", 3, 0),
    // 6翻
    CHINITSU("清一色", 6, 5),
    // 役满
    KOKUSHI("国士无双", 0, 0, true),
    SUUANKOU("四暗刻", 0, 0, true),
    DAISANGEN("大三元", 0, 0, true),
    SHOUSUUSHII("小四喜", 0, 0, true),
    DAISUUSHII("大四喜", 0, 0, true),
    TSUUIISOU("字一色", 0, 0, true),
    RYUUIISOU("绿一色", 0, 0, true),
    CHINROUTOU("清老头", 0, 0, true),
    CHURENPOUTOU("九莲宝灯", 0, 0, true),
    SUUKANTSU("四杠子", 0, 0, true),
    TENHOU("天和", 0, 0, true),
    CHIIHOU("地和", 0, 0, true)
}

/**
 * 役种算分与评估核心类
 */
class YakuEvaluator {
    data class YakuResult(
        val yakuList: List<Pair<Yaku, Int>>,
        val totalHan: Int,
        val isYakuman: Boolean,
        val yakumanCount: Int
    )

    data class WinContext(
        val winTile: Tile,
        val isTsumo: Boolean,
        val isClosed: Boolean,
        val seatWind: Int,      // 1=東,2=南,3=西,4=北
        val roundWind: Int,     // 1=東,2=南,3=西,4=北
        val isRiichi: Boolean = false,
        val isDoubleRiichi: Boolean = false,
        val isIppatsu: Boolean = false,
        val isRinshan: Boolean = false,
        val isHaitei: Boolean = false,
        val isHoutei: Boolean = false,
        val isChankan: Boolean = false,
        val isTenhou: Boolean = false,
        val isChiihou: Boolean = false
    )

    fun evaluate(hand: PlayerHand, context: WinContext): YakuResult {
        val decomposer = HandDecomposer.INSTANCE
        val decompositions = decomposer.decompose(hand.allClosedTiles)

        var bestResult = YakuResult(emptyList(), 0, false, 0)

        for (decomp in decompositions) {
            val result = evaluateDecomposition(decomp, hand, context)
            
            if (result.isYakuman) {
                if (!bestResult.isYakuman || result.yakumanCount > bestResult.yakumanCount) {
                    bestResult = result
                }
            } else if (!bestResult.isYakuman && result.totalHan > bestResult.totalHan) {
                bestResult = result
            }
        }

        return bestResult
    }

    /**
     * 针对单个特定拆解方案进行役种评价
     */
    fun evaluateForDecomposition(
        decomp: HandDecomposer.Decomposition,
        hand: PlayerHand,
        context: WinContext
    ): YakuResult = evaluateDecomposition(decomp, hand, context)

    fun evaluateDecomposition(
        decomp: HandDecomposer.Decomposition,
        hand: PlayerHand,
        context: WinContext
    ): YakuResult {
        val allTiles = hand.allClosedTiles + hand.melds.flatMap { it.tiles }
        
        val yakumanList = mutableListOf<Yaku>()
        if (checkKokushi(decomp)) yakumanList.add(Yaku.KOKUSHI)
        if (checkSuuankou(decomp, hand, context)) yakumanList.add(Yaku.SUUANKOU)
        if (checkDaisangen(decomp, hand)) yakumanList.add(Yaku.DAISANGEN)
        if (checkShousuushii(decomp, hand)) yakumanList.add(Yaku.SHOUSUUSHII)
        if (checkDaisuushii(decomp, hand)) yakumanList.add(Yaku.DAISUUSHII)
        if (checkTsuuiisou(allTiles)) yakumanList.add(Yaku.TSUUIISOU)
        if (checkRyuuiisou(allTiles)) yakumanList.add(Yaku.RYUUIISOU)
        if (checkChinroutou(allTiles)) yakumanList.add(Yaku.CHINROUTOU)
        if (checkChurenpoutou(decomp, hand, context)) yakumanList.add(Yaku.CHURENPOUTOU)
        if (checkSuukantsu(hand)) yakumanList.add(Yaku.SUUKANTSU)
        if (context.isTenhou) yakumanList.add(Yaku.TENHOU)
        if (context.isChiihou) yakumanList.add(Yaku.CHIIHOU)

        if (yakumanList.isNotEmpty()) {
            val yakuCount = yakumanList.size
            return YakuResult(yakumanList.map { it to 0 }, 0, true, yakuCount)
        }

        val normalYakuList = mutableListOf<Pair<Yaku, Int>>()
        
        fun addYaku(yaku: Yaku) {
            val han = if (context.isClosed) yaku.hanClosed else yaku.hanOpen
            if (han > 0) normalYakuList.add(yaku to han)
        }

        if (context.isRiichi && !context.isDoubleRiichi) addYaku(Yaku.RIICHI)
        if (context.isDoubleRiichi) addYaku(Yaku.DOUBLE_RIICHI)
        if (context.isIppatsu) addYaku(Yaku.IPPATSU)
        if (context.isClosed && context.isTsumo) addYaku(Yaku.MENZEN_TSUMO)
        if (context.isHaitei) addYaku(Yaku.HAITEI)
        if (context.isHoutei) addYaku(Yaku.HOUTEI)
        if (context.isRinshan) addYaku(Yaku.RINSHAN)
        if (context.isChankan) addYaku(Yaku.CHANKAN)

        if (checkTanyao(allTiles)) addYaku(Yaku.TANYAO)
        if (checkPinfu(decomp, hand, context)) addYaku(Yaku.PINFU)
        
        val iipeikoCount = checkIipeiko(decomp, context)
        if (iipeikoCount == 1) addYaku(Yaku.IIPEIKO)
        else if (iipeikoCount == 2) addYaku(Yaku.RYANPEIKOU)

        val yakuhai = checkYakuhai(decomp, hand, context)
        yakuhai.forEach { addYaku(it) }

        val isJunchan = checkJunchan(decomp, hand)
        val isChanta = checkChanta(decomp, hand)
        if (isJunchan) addYaku(Yaku.JUNCHAN)
        else if (isChanta && !isJunchan) addYaku(Yaku.CHANTA)
        
        val isHonroutou = checkHonroutou(allTiles)
        if (isHonroutou) addYaku(Yaku.HONROUTOU)

        if (checkIttsu(decomp, hand)) addYaku(Yaku.ITTSU)
        if (checkSanShokuDoujun(decomp, hand)) addYaku(Yaku.SAN_SHOKU_DOUJUN)
        if (checkSanShokuDoukou(decomp, hand)) addYaku(Yaku.SAN_SHOKU_DOUKOU)
        if (checkToitoi(decomp, hand)) addYaku(Yaku.TOITOI)
        if (checkSanAnkou(decomp, hand, context)) addYaku(Yaku.SAN_ANKOU)
        if (checkSanKantsu(hand)) addYaku(Yaku.SAN_KANTSU)
        if (checkShousangen(decomp, hand)) addYaku(Yaku.SHOUSANGEN)
        if (checkChiitoitsu(decomp)) addYaku(Yaku.CHIITOITSU)

        val isChinitsu = checkChinitsu(allTiles)
        val isHonitsu = checkHonitsu(allTiles)
        if (isChinitsu) addYaku(Yaku.CHINITSU)
        else if (isHonitsu && !isChinitsu) addYaku(Yaku.HONITSU)

        val totalHan = normalYakuList.sumOf { it.second }
        return YakuResult(normalYakuList, totalHan, false, 0)
    }

    private fun checkKokushi(decomp: HandDecomposer.Decomposition): Boolean {
        return decomp.pattern == HandDecomposer.DecompPattern.KOKUSHI
    }

    private fun checkSuuankou(decomp: HandDecomposer.Decomposition, hand: PlayerHand, context: WinContext): Boolean {
        if (decomp.pattern != HandDecomposer.DecompPattern.REGULAR) return false
        val closedTriplets = countClosedTriplets(decomp, hand, context)
        return closedTriplets == 4
    }

    private fun checkDaisangen(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        return countDragonTriplets(decomp, hand) == 3
    }

    private fun checkShousuushii(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        if (decomp.pattern != HandDecomposer.DecompPattern.REGULAR) return false
        val windTriplets = countWindTriplets(decomp, hand)
        val isWindPair = decomp.jantai in 27..30
        return windTriplets == 3 && isWindPair
    }

    private fun checkDaisuushii(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        return countWindTriplets(decomp, hand) == 4
    }

    private fun checkTsuuiisou(allTiles: List<Tile>): Boolean {
        return allTiles.all { it.isHonor }
    }

    private fun checkRyuuiisou(allTiles: List<Tile>): Boolean {
        return allTiles.all { it.isGreen }
    }

    private fun checkChinroutou(allTiles: List<Tile>): Boolean {
        return allTiles.all { it.isTerminal }
    }

    private fun checkChurenpoutou(decomp: HandDecomposer.Decomposition, hand: PlayerHand, context: WinContext): Boolean {
        if (!context.isClosed) return false
        val allTiles = hand.allClosedTiles
        if (allTiles.size != 14) return false
        
        val suits = allTiles.map { it.suit }.distinct()
        if (suits.size != 1 || suits[0] == Suit.WIND || suits[0] == Suit.DRAGON) return false
        
        val counts = IntArray(9)
        for (t in allTiles) counts[t.rank - 1]++
        
        var extras = 0
        if (counts[0] < 3) return false else extras += counts[0] - 3
        if (counts[8] < 3) return false else extras += counts[8] - 3
        for (i in 1..7) {
            if (counts[i] < 1) return false else extras += counts[i] - 1
        }
        return extras == 1
    }

    private fun checkSuukantsu(hand: PlayerHand): Boolean {
        return hand.melds.count { it.isKan } == 4
    }

    private fun checkTanyao(allTiles: List<Tile>): Boolean {
        return allTiles.all { it.isSimple }
    }

    private fun checkPinfu(decomp: HandDecomposer.Decomposition, hand: PlayerHand, context: WinContext): Boolean {
        if (!context.isClosed) return false
        if (decomp.pattern != HandDecomposer.DecompPattern.REGULAR) return false
        
        if (decomp.mentsu.size != 4 || decomp.mentsu.any { !it.isSequence }) return false
        
        val pairIndex = decomp.jantai
        if (pairIndex in 31..33) return false
        if (pairIndex == 27 + context.seatWind - 1) return false
        if (pairIndex == 27 + context.roundWind - 1) return false
        
        var isRyanmen = false
        for (mentsu in decomp.mentsu) {
            val winIndex = context.winTile.typeIndex
            if (mentsu.typeIndices.contains(winIndex)) {
                val idx = mentsu.typeIndices.indexOf(winIndex)
                if (idx == 0) { 
                    val rank = winIndex % 9
                    if (rank != 6) isRyanmen = true
                } else if (idx == 2) { 
                    val rank = winIndex % 9
                    if (rank != 2) isRyanmen = true 
                }
            }
        }
        return isRyanmen
    }

    private fun checkIipeiko(decomp: HandDecomposer.Decomposition, context: WinContext): Int {
        if (!context.isClosed) return 0
        if (decomp.pattern != HandDecomposer.DecompPattern.REGULAR) return 0
        
        val sequences = decomp.mentsu.filter { it.isSequence }.map { it.typeIndices[0] }.sorted()
        var pairs = 0
        var i = 0
        while (i < sequences.size - 1) {
            if (sequences[i] == sequences[i+1]) {
                pairs++
                i += 2
            } else {
                i++
            }
        }
        return pairs
    }

    private fun checkYakuhai(decomp: HandDecomposer.Decomposition, hand: PlayerHand, context: WinContext): List<Yaku> {
        val yaku = mutableListOf<Yaku>()
        val triplets = decomp.mentsu.filter { !it.isSequence }.map { it.typeIndices[0] } +
                       hand.melds.filter { !it.isSequence }.map { it.tiles[0].typeIndex }
        
        triplets.forEach { index ->
            when (index) {
                31 -> yaku.add(Yaku.YAKUHAI_HAKU)
                32 -> yaku.add(Yaku.YAKUHAI_HATSU)
                33 -> yaku.add(Yaku.YAKUHAI_CHUN)
            }
            if (index == 27 + context.seatWind - 1) yaku.add(Yaku.YAKUHAI_JIKAZE)
            if (index == 27 + context.roundWind - 1) yaku.add(Yaku.YAKUHAI_BAKAZE)
        }
        return yaku
    }

    private fun checkJunchan(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        if (decomp.pattern != HandDecomposer.DecompPattern.REGULAR) return false
        val allMentsuHasTerminal = decomp.mentsu.all { hasTerminal(it) } &&
                                   hand.melds.all { hasTerminalMeld(it) }
        val pairHasTerminal = isTerminalIndex(decomp.jantai)
        val noHonors = checkNoHonors(decomp, hand)
        return allMentsuHasTerminal && pairHasTerminal && noHonors
    }

    private fun checkChanta(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        if (decomp.pattern != HandDecomposer.DecompPattern.REGULAR) return false
        val allMentsuHasTerminalOrHonor = decomp.mentsu.all { hasTerminalOrHonor(it) } &&
                                          hand.melds.all { hasTerminalOrHonorMeld(it) }
        val pairHasTerminalOrHonor = isTerminalOrHonorIndex(decomp.jantai)
        val hasHonors = !checkNoHonors(decomp, hand) 
        return allMentsuHasTerminalOrHonor && pairHasTerminalOrHonor && hasHonors
    }

    private fun checkHonroutou(allTiles: List<Tile>): Boolean {
        return allTiles.all { it.isTerminalOrHonor } && allTiles.any { it.isHonor } && allTiles.any { it.isTerminal }
    }

    private fun checkIttsu(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        val sequences = decomp.mentsu.filter { it.isSequence }.map { it.typeIndices[0] } +
                        hand.melds.filter { it.isSequence }.map { it.tiles.sortedBy { t -> t.typeIndex }[0].typeIndex }
        for (suitBase in listOf(0, 9, 18)) {
            if (sequences.contains(suitBase) && sequences.contains(suitBase + 3) && sequences.contains(suitBase + 6)) {
                return true
            }
        }
        return false
    }

    private fun checkSanShokuDoujun(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        val sequences = decomp.mentsu.filter { it.isSequence }.map { it.typeIndices[0] % 9 } +
                        hand.melds.filter { it.isSequence }.map { it.tiles.sortedBy { t -> t.typeIndex }[0].typeIndex % 9 }
        for (rank in 0..6) {
            val man = decomp.mentsu.any { it.isSequence && it.typeIndices[0] == rank } || 
                      hand.melds.any { it.isSequence && it.tiles.sortedBy { t->t.typeIndex }[0].typeIndex == rank }
            val pin = decomp.mentsu.any { it.isSequence && it.typeIndices[0] == rank + 18 } || 
                      hand.melds.any { it.isSequence && it.tiles.sortedBy { t->t.typeIndex }[0].typeIndex == rank + 18 }
            val sou = decomp.mentsu.any { it.isSequence && it.typeIndices[0] == rank + 9 } || 
                      hand.melds.any { it.isSequence && it.tiles.sortedBy { t->t.typeIndex }[0].typeIndex == rank + 9 }
            if (man && pin && sou) return true
        }
        return false
    }

    private fun checkSanShokuDoukou(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        val fullTriplets = decomp.mentsu.filter { !it.isSequence }.map { it.typeIndices[0] } +
                           hand.melds.filter { !it.isSequence }.map { it.tiles[0].typeIndex }
        for (rank in 0..8) {
            if (fullTriplets.contains(rank) && fullTriplets.contains(rank + 9) && fullTriplets.contains(rank + 18)) {
                return true
            }
        }
        return false
    }

    private fun checkToitoi(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        if (decomp.pattern != HandDecomposer.DecompPattern.REGULAR) return false
        val hasSeq = decomp.mentsu.any { it.isSequence } || hand.melds.any { it.isSequence }
        return !hasSeq
    }

    private fun checkSanAnkou(decomp: HandDecomposer.Decomposition, hand: PlayerHand, context: WinContext): Boolean {
        if (decomp.pattern != HandDecomposer.DecompPattern.REGULAR) return false
        return countClosedTriplets(decomp, hand, context) == 3
    }

    private fun checkSanKantsu(hand: PlayerHand): Boolean {
        return hand.melds.count { it.isKan } == 3
    }

    private fun checkShousangen(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        val dragons = countDragonTriplets(decomp, hand)
        val pairIsDragon = decomp.jantai in 31..33
        return dragons == 2 && pairIsDragon
    }

    private fun checkChiitoitsu(decomp: HandDecomposer.Decomposition): Boolean {
        return decomp.pattern == HandDecomposer.DecompPattern.CHIITOITSU
    }

    private fun checkHonitsu(allTiles: List<Tile>): Boolean {
        val hasHonor = allTiles.any { it.isHonor }
        if (!hasHonor) return false
        val suits = allTiles.filter { it.isNumberTile }.map { it.suit }.distinct()
        return suits.size == 1
    }

    private fun checkChinitsu(allTiles: List<Tile>): Boolean {
        val suits = allTiles.map { it.suit }.distinct()
        return suits.size == 1 && suits[0] != Suit.WIND && suits[0] != Suit.DRAGON
    }

    private fun countClosedTriplets(decomp: HandDecomposer.Decomposition, hand: PlayerHand, context: WinContext): Int {
        var count = 0
        for (m in decomp.mentsu) {
            if (!m.isSequence) {
                if (!context.isTsumo && m.typeIndices[0] == context.winTile.typeIndex) {
                    // 检查是否暗刻里只有3张相同的，且是荣和那张
                    // 如果手牌里本来就有暗刻且不是最后和的一张，它不应该被扣减，但这里简单处理
                    // 这是标准的判断方式：如果你荣和了暗刻对应的牌，那这个刻子算明刻。
                    continue
                }
                count++
            }
        }
        count += hand.melds.count { it.type == MeldType.CLOSED_KAN }
        return count
    }

    private fun countDragonTriplets(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Int {
        val indices = decomp.mentsu.filter { !it.isSequence }.map { it.typeIndices[0] } +
                      hand.melds.filter { !it.isSequence }.map { it.tiles[0].typeIndex }
        return indices.count { it in 31..33 }
    }

    private fun countWindTriplets(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Int {
        val indices = decomp.mentsu.filter { !it.isSequence }.map { it.typeIndices[0] } +
                      hand.melds.filter { !it.isSequence }.map { it.tiles[0].typeIndex }
        return indices.count { it in 27..30 }
    }

    private fun isTerminalIndex(index: Int): Boolean = (index in 0..26) && (index % 9 == 0 || index % 9 == 8)
    private fun isTerminalOrHonorIndex(index: Int): Boolean = isTerminalIndex(index) || (index in 27..33)
    
    private fun hasTerminal(m: HandDecomposer.MentsuGroup): Boolean = m.typeIndices.any { isTerminalIndex(it) }
    private fun hasTerminalOrHonor(m: HandDecomposer.MentsuGroup): Boolean = m.typeIndices.any { isTerminalOrHonorIndex(it) }
    
    private fun hasTerminalMeld(m: Meld): Boolean = m.tiles.any { it.isTerminal }
    private fun hasTerminalOrHonorMeld(m: Meld): Boolean = m.tiles.any { it.isTerminalOrHonor }

    private fun checkNoHonors(decomp: HandDecomposer.Decomposition, hand: PlayerHand): Boolean {
        if (decomp.jantai in 27..33) return false
        if (decomp.mentsu.any { m -> m.typeIndices.any { it in 27..33 } }) return false
        if (hand.melds.any { m -> m.tiles.any { it.isHonor } }) return false
        return true
    }
}
