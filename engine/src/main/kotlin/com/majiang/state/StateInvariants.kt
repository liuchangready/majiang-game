package com.majiang.state

import com.majiang.model.*

/**
 * 对局状态不变量强审计器
 *
 * 核心法则（DESIGN.md 3.2 & ACCEPTANCE.md A01~A05）：
 * 1. 真实物理守恒：从全部实际物理容器重建位置清单，断言恰好 136 个合法物理身份、无多无少无重复；
 * 2. 账本一致性：实际容器重建清单与 TilePool 中的逐张位置完全相等；
 * 3. 拒绝自动修复：发现不变量违背直接抛出异常，绝不顺手重建或修改账本；
 * 4. 指针与王牌界限：活牌山界限与有效王牌数（始终恒为 14）；
 * 5. 副露合法性与手牌等效张数。
 */
object StateInvariants {

    /**
     * 对 GameState 进行全面的强不变量断言
     *
     * @throws IllegalStateException 或 IllegalArgumentException 当任一不变量受损时
     */
    fun assertValid(state: GameState) {
        // 1. 指针界限基础检查
        require(state.wallPointer >= 0) {
            "wallPointer must be >= 0, was ${state.wallPointer}"
        }
        require(state.wallEndExclusive >= state.wallPointer) {
            "wallEndExclusive (${state.wallEndExclusive}) must be >= wallPointer (${state.wallPointer})"
        }
        require(state.wallEndExclusive <= state.wall.size) {
            "wallEndExclusive (${state.wallEndExclusive}) must be <= wall.size (${state.wall.size})"
        }
        require(state.rinshanPointer in 0..4) {
            "rinshanPointer must be in 0..4, was ${state.rinshanPointer}"
        }

        // 1.1 出千点数状态不变量校验
        require(state.cheatPoints.size == 4) {
            "cheatPoints size must be 4, was ${state.cheatPoints.size}"
        }
        for (i in 0 until 4) {
            val pts = state.cheatPoints[i]
            require(pts >= 0) {
                "cheatPoints[$i] must be non-negative, was $pts"
            }
        }
        for (entry in state.cheatEntries) {
            require(entry.seat in 0..3) { "CheatLedgerEntry seat out of bounds: ${entry.seat}" }
            require(entry.balanceAfter >= 0) { "CheatLedgerEntry balanceAfter must be non-negative: ${entry.balanceAfter}" }
            require(entry.entryId.isNotBlank()) { "CheatLedgerEntry entryId cannot be blank" }
        }

        // 1.2 杠计数与待翻杠宝牌不变量校验
        require(state.totalKanCount in 0..4) {
            "totalKanCount must be in 0..4, was ${state.totalKanCount}"
        }
        require(state.playerKanCounts.size == 4) {
            "playerKanCounts size must be 4, was ${state.playerKanCounts.size}"
        }
        for (i in 0 until 4) {
            require(state.playerKanCounts[i] in 0..4) {
                "playerKanCounts[$i] must be in 0..4, was ${state.playerKanCounts[i]}"
            }
        }
        require(state.playerKanCounts.sum() == state.totalKanCount) {
            "playerKanCounts sum (${state.playerKanCounts.sum()}) must equal totalKanCount (${state.totalKanCount})"
        }
        require(state.pendingKanDoraCount in 0..4) {
            "pendingKanDoraCount must be in 0..4, was ${state.pendingKanDoraCount}"
        }
        require(state.pendingKanDoraCount <= state.totalKanCount) {
            "pendingKanDoraCount (${state.pendingKanDoraCount}) cannot exceed totalKanCount (${state.totalKanCount})"
        }
        require(state.doraIndicators.size in 0..5) {
            "doraIndicators size must be in 0..5, was ${state.doraIndicators.size}"
        }
        require(state.doraIndicators.size + state.pendingKanDoraCount <= 5) {
            "Total dora indicators (revealed=${state.doraIndicators.size} + pending=${state.pendingKanDoraCount}) must be <= 5"
        }

        // 1.3 袖里藏花挂起二选一校验
        state.sleeveBloomOffer?.let { offer ->
            require(offer.seat in 0..3) { "sleeveBloomOffer seat out of bounds: ${offer.seat}" }
            require(offer.offerId.isNotBlank()) { "sleeveBloomOffer offerId cannot be blank" }
            require(offer.seat == state.currentPlayer) {
                "sleeveBloomOffer seat (${offer.seat}) must match currentPlayer (${state.currentPlayer})"
            }
            require(state.phase == GamePhase.CHEAT_WINDOW) {
                "sleeveBloomOffer can only exist in CHEAT_WINDOW phase, was ${state.phase}"
            }
            val hand = state.players[offer.seat]
            require(hand.drawnTile == offer.firstTile) {
                "sleeveBloomOffer firstTile (${offer.firstTile}) must match player drawnTile (${hand.drawnTile})"
            }
        }

        // 1.4 偷梁换柱挂起选择校验
        state.beamSwapOffer?.let { offer ->
            require(offer.seat in 0..3) { "beamSwapOffer seat out of bounds: ${offer.seat}" }
            require(offer.offerId.isNotBlank()) { "beamSwapOffer offerId cannot be blank" }
            require(offer.seat == state.currentPlayer) {
                "beamSwapOffer seat (${offer.seat}) must match currentPlayer (${state.currentPlayer})"
            }
            require(state.phase == GamePhase.CHEAT_WINDOW) {
                "beamSwapOffer can only exist in CHEAT_WINDOW phase, was ${state.phase}"
            }
            val hand = state.players[offer.seat]
            require(hand.drawnTile != null && hand.drawnTile!!.tileId == offer.drawnTileId) {
                "beamSwapOffer drawnTileId (${offer.drawnTileId}) must match player drawnTile (${hand.drawnTile})"
            }
            require(offer.candidates.isNotEmpty()) { "beamSwapOffer candidates cannot be empty" }
            require(offer.eligibleHandTileIds.isNotEmpty()) { "beamSwapOffer eligibleHandTileIds cannot be empty" }
        }

        if (state.sleeveBloomOffer != null && state.beamSwapOffer != null) {
            throw IllegalStateException("Cannot have both sleeveBloomOffer and beamSwapOffer pending simultaneously")
        }

        // 2. 检查尚未开局时的全 UNASSIGNED 特殊状态
        val containersEmpty = state.wall.isEmpty() && state.deadWall.isEmpty() &&
                state.deadWallReplacements.isEmpty() &&
                state.players.all { it.closedTiles.isEmpty() && it.drawnTile == null && it.melds.isEmpty() && it.discards.isEmpty() } &&
                state.sleeveTiles.all { it.isEmpty() }

        if (containersEmpty && state.phase == GamePhase.STACKING) {
            val allUnassigned = state.tilePool.tileLocations.values.all {
                it.zone == Zone.UNASSIGNED && it.player == -1
            }
            if (allUnassigned && state.tilePool.size == 136) {
                // 合法开局前初始态
                return
            }
        }

        // 3. 从实际物理容器重建全量位置清单
        val actualLocations = mutableMapOf<Tile, TileLocation>()
        val standardSet = TilePool.buildStandardSet()

        fun record(tile: Tile, location: TileLocation, containerDesc: String) {
            require(tile.copyIndex in 0..3) {
                "Tile $tile in $containerDesc has illegal copyIndex ${tile.copyIndex}"
            }
            val existing = actualLocations.put(tile, location)
            if (existing != null) {
                throw IllegalStateException(
                    "Physical conservation violated! Duplicate tile $tile found in $containerDesc ($location), already recorded at $existing"
                )
            }
        }

        // 3.1 活牌山容器: [wallPointer, wallEndExclusive)
        for (i in state.wallPointer until state.wallEndExclusive) {
            record(state.wall[i], TileLocation(Zone.WALL, -1), "wall[$i]")
        }

        // 3.2 当前有效王牌容器: [rinshanPointer, deadWall.size) + deadWallReplacements
        for (i in state.rinshanPointer until state.deadWall.size) {
            record(state.deadWall[i], TileLocation(Zone.DEAD_WALL, -1), "deadWall[$i]")
        }
        for (i in state.deadWallReplacements.indices) {
            record(state.deadWallReplacements[i], TileLocation(Zone.DEAD_WALL, -1), "deadWallReplacements[$i]")
        }

        // 3.3 玩家物理容器：暗牌、摸牌、副露、尚未叫走的弃牌河
        for (p in 0 until 4) {
            val hand = state.players[p]
            for (i in hand.closedTiles.indices) {
                record(hand.closedTiles[i], TileLocation(Zone.HAND, p), "players[$p].closedTiles[$i]")
            }
            hand.drawnTile?.let {
                record(it, TileLocation(Zone.HAND, p), "players[$p].drawnTile")
            }
            for (meldIdx in hand.melds.indices) {
                val meld = hand.melds[meldIdx]
                for (tileIdx in meld.tiles.indices) {
                    record(meld.tiles[tileIdx], TileLocation(Zone.MELD, p), "players[$p].melds[$meldIdx].tiles[$tileIdx]")
                }
            }
            for (i in hand.discards.indices) {
                record(hand.discards[i], TileLocation(Zone.DISCARD, p), "players[$p].discards[$i]")
            }
        }

        // 3.4 袖中暗藏牌容器
        for (p in 0 until 4) {
            val sleeve = state.sleeveTiles[p]
            for (i in sleeve.indices) {
                record(sleeve[i], TileLocation(Zone.SLEEVE, p), "sleeveTiles[$p][$i]")
            }
        }

        // 4. 断言物理总张数恰好 136，且标准 136 张无一遗漏
        if (actualLocations.size != 136) {
            throw IllegalStateException(
                "Physical conservation violated: expected exactly 136 tiles in actual containers, but found ${actualLocations.size}"
            )
        }
        for (stdTile in standardSet) {
            if (!actualLocations.containsKey(stdTile)) {
                throw IllegalStateException(
                    "Physical conservation violated: tile $stdTile is completely missing from all actual containers"
                )
            }
        }

        // 5. 断言与 TilePool 账本逐张精确相等（包括区域与座位）
        for (stdTile in standardSet) {
            val actualLoc = actualLocations[stdTile]!!
            val poolLoc = state.tilePool.getLocation(stdTile)
                ?: throw IllegalStateException("TilePool is missing tile $stdTile")
            if (actualLoc != poolLoc) {
                throw IllegalStateException(
                    "TilePool audit failed for tile $stdTile: actual container has $actualLoc, but TilePool recorded $poolLoc"
                )
            }
        }

        // 6. 审计王牌区有效张数：已开局的有效王牌必须恰好 14 张（DESIGN.md 3.1 & ACCEPTANCE.md A03）
        require(state.deadWall.size == 14) {
            "Dead wall invariant violated: deadWall.size must be exactly 14, but was ${state.deadWall.size}"
        }
        val effectiveDeadWallCount = (state.deadWall.size - state.rinshanPointer) + state.deadWallReplacements.size
        if (effectiveDeadWallCount != 14) {
            throw IllegalStateException(
                "Dead wall invariant violated: effective dead wall count must be 14, but was $effectiveDeadWallCount " +
                        "(deadWall.size=${state.deadWall.size}, rinshanPointer=${state.rinshanPointer}, replacements=${state.deadWallReplacements.size})"
            )
        }

        // 7. 审计副露结构合法性（DESIGN.md 3.2）
        for (p in 0 until 4) {
            for (meld in state.players[p].melds) {
                require(meld.tiles.contains(meld.calledTile)) {
                    "Meld $meld does not contain calledTile ${meld.calledTile}"
                }
                require(meld.tiles.distinct().size == meld.tiles.size) {
                    "Meld $meld contains duplicate tiles: ${meld.tiles}"
                }

                when (meld.type) {
                    MeldType.CHI -> {
                        require(meld.tiles.size == 3) { "CHI meld must have 3 tiles, got ${meld.tiles.size}" }
                        require(meld.tiles.all { it.isNumberTile }) { "CHI tiles must all be number tiles" }
                        val suit = meld.tiles.first().suit
                        require(meld.tiles.all { it.suit == suit }) { "CHI tiles must be of same suit" }
                        val sortedRanks = meld.tiles.map { it.rank }.sorted()
                        require(sortedRanks[1] == sortedRanks[0] + 1 && sortedRanks[2] == sortedRanks[1] + 1) {
                            "CHI meld ranks must be consecutive, got $sortedRanks"
                        }
                        require(meld.fromPlayer == (p + 3) % 4) {
                            "CHI fromPlayer must be kamicha ${(p + 3) % 4}, got ${meld.fromPlayer}"
                        }
                    }
                    MeldType.PON -> {
                        require(meld.tiles.size == 3) { "PON meld must have 3 tiles, got ${meld.tiles.size}" }
                        val first = meld.tiles.first()
                        require(meld.tiles.all { it.sameType(first) }) { "PON tiles must be of same type" }
                        require(meld.fromPlayer != p && meld.fromPlayer in 0..3) {
                            "PON fromPlayer must be another player (0..3), got ${meld.fromPlayer}"
                        }
                    }
                    MeldType.OPEN_KAN -> {
                        require(meld.tiles.size == 4) { "OPEN_KAN meld must have 4 tiles, got ${meld.tiles.size}" }
                        val first = meld.tiles.first()
                        require(meld.tiles.all { it.sameType(first) }) { "OPEN_KAN tiles must be of same type" }
                        require(meld.fromPlayer != p && meld.fromPlayer in 0..3) {
                            "OPEN_KAN fromPlayer must be another player (0..3), got ${meld.fromPlayer}"
                        }
                    }
                    MeldType.CLOSED_KAN -> {
                        require(meld.tiles.size == 4) { "CLOSED_KAN meld must have 4 tiles, got ${meld.tiles.size}" }
                        val first = meld.tiles.first()
                        require(meld.tiles.all { it.sameType(first) }) { "CLOSED_KAN tiles must be of same type" }
                        require(meld.fromPlayer == -1 || meld.fromPlayer == p) {
                            "CLOSED_KAN fromPlayer must be -1 or self, got ${meld.fromPlayer}"
                        }
                    }
                    MeldType.ADDED_KAN -> {
                        require(meld.tiles.size == 4) { "ADDED_KAN meld must have 4 tiles, got ${meld.tiles.size}" }
                        val first = meld.tiles.first()
                        require(meld.tiles.all { it.sameType(first) }) { "ADDED_KAN tiles must be of same type" }
                        require(meld.fromPlayer != p && meld.fromPlayer in 0..3) {
                            "ADDED_KAN fromPlayer must be another player (0..3), got ${meld.fromPlayer}"
                        }
                    }
                }
            }
        }

        // 8. 审计各阶段手牌等效张数与 drawnTile 归属约束（DESIGN.md 3.2）
        val inNormalRound = state.phase !in setOf(GamePhase.STACKING, GamePhase.DEALING)
        val handsDealt = state.players.any { it.closedTiles.isNotEmpty() }

        if (inNormalRound && handsDealt) {
            val drawnHolders = (0 until 4).filter { state.players[it].drawnTile != null }
            if (drawnHolders.size > 1) {
                throw IllegalStateException("At most one player can hold drawnTile, but players $drawnHolders do")
            }

            fun eqCount(p: Int): Int {
                val hand = state.players[p]
                return hand.closedTiles.size + hand.melds.size * 3 + (if (hand.drawnTile != null) 1 else 0) + state.sleeveTiles[p].size
            }

            when (state.phase) {
                GamePhase.DRAW -> {
                    // DRAW 阶段：摸牌前，全员等效 13 张，且全员 drawnTile 必须为 null
                    if (drawnHolders.isNotEmpty()) {
                        throw IllegalStateException(
                            "During DRAW phase, no player can hold drawnTile, but players $drawnHolders do"
                        )
                    }
                    for (p in 0 until 4) {
                        val count = eqCount(p)
                        if (count != 13) {
                            throw IllegalStateException(
                                "During DRAW phase, player $p must have equivalent 13 tiles, but got $count"
                            )
                        }
                    }
                }
                GamePhase.DISCARD, GamePhase.CHEAT_WINDOW -> {
                    // DISCARD / CHEAT_WINDOW 阶段：行动者 14 张，其余玩家 13 张
                    val actor = state.currentPlayer
                    require(actor in 0..3) { "Invalid currentPlayer: $actor" }
                    if (drawnHolders.any { it != actor }) {
                        throw IllegalStateException(
                            "During ${state.phase}, non-actor players cannot hold drawnTile: $drawnHolders (actor=$actor)"
                        )
                    }
                    for (p in 0 until 4) {
                        val count = eqCount(p)
                        val expected = if (p == actor) 14 else 13
                        if (count != expected) {
                            throw IllegalStateException(
                                "During ${state.phase}, player $p expected equivalent $expected tiles, but got $count (actor=$actor)"
                            )
                        }
                    }
                }
                GamePhase.RESPONSE_WINDOW -> {
                    // RESPONSE_WINDOW 阶段：打牌后等待响应，全员 13 张，且全员 drawnTile 必须为 null
                    if (drawnHolders.isNotEmpty()) {
                        throw IllegalStateException(
                            "During RESPONSE_WINDOW, no player can hold drawnTile, but players $drawnHolders do"
                        )
                    }
                    for (p in 0 until 4) {
                        val count = eqCount(p)
                        if (count != 13) {
                            throw IllegalStateException(
                                "During RESPONSE_WINDOW, player $p must have equivalent 13 tiles, but got $count"
                            )
                        }
                    }
                }
                GamePhase.CHANKAN_WINDOW -> {
                    // CHANKAN_WINDOW 抢杠响应窗口：加杠宣告后等待他家荣和响应，宣告加杠者 14 张，其余 3 家 13 张且 drawnTile 必须为 null
                    val actor = state.currentPlayer
                    require(actor in 0..3) { "Invalid currentPlayer: $actor" }
                    if (drawnHolders.any { it != actor }) {
                        throw IllegalStateException(
                            "During CHANKAN_WINDOW, non-actor players cannot hold drawnTile: $drawnHolders (actor=$actor)"
                        )
                    }
                    for (p in 0 until 4) {
                        val count = eqCount(p)
                        val expected = if (p == actor) 14 else 13
                        if (count != expected) {
                            throw IllegalStateException(
                                "During CHANKAN_WINDOW, player $p expected equivalent $expected tiles, but got $count (actor=$actor)"
                            )
                        }
                    }
                }
                GamePhase.KAN_DRAW -> {
                    // KAN_DRAW 阶段：杠声明后、岭上摸牌前，待岭上摸牌者（及全员）13 张，且全员 drawnTile 为 null
                    if (drawnHolders.isNotEmpty()) {
                        throw IllegalStateException(
                            "During KAN_DRAW, no player can hold drawnTile before rinshan draw, but players $drawnHolders do"
                        )
                    }
                    for (p in 0 until 4) {
                        val count = eqCount(p)
                        if (count != 13) {
                            throw IllegalStateException(
                                "During KAN_DRAW, player $p must have equivalent 13 tiles, but got $count"
                            )
                        }
                    }
                }
                GamePhase.ROUND_END, GamePhase.MATCH_END -> {
                    // 终局阶段：各家等效张数为 13 或 14（和牌者/自摸者/流局）
                    for (p in 0 until 4) {
                        val count = eqCount(p)
                        if (count !in 13..14) {
                            throw IllegalStateException(
                                "At round/match end, player $p equivalent tile count must be 13 or 14, but got $count"
                            )
                        }
                    }
                }
                GamePhase.STACKING, GamePhase.DEALING -> {
                    // 码牌与发牌过程不在此做 13/14 严格审计
                }
            }
        }
    }
}
