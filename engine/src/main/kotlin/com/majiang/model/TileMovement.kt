package com.majiang.model

import com.majiang.state.GameState

/**
 * 物理牌原子移动服务
 *
 * 核心法则（DESIGN.md 3.3 & ACCEPTANCE.md A01~A05）：
 * 1. 完整前置预校验：在执行任何容器修改前，严格验证所有源、目标、归属座位与池状态；
 * 2. 失败零副作用：任何条件不满足直接抛出异常，绝不修改任何容器与 TilePool，状态与事件完全不变；
 * 3. 完整所有权转移：吃碰杠必须同步转移所有参与牌的所有权；
 * 4. 岭上摸牌保全：未参与暗杠/加杠的 drawnTile 必须先合并回闭手，岭上牌放置于 drawnTile，不覆盖丢牌；
 * 5. 牌山末端补入王牌：每次岭上摸牌从活牌山末端 wallEndExclusive-1 补入 deadWallReplacements，有效王牌恒为 14。
 */
object TileMovement {

    /** 一次正常摸牌的结果与来源 */
    data class NormalDraw(
        val tile: Tile,
        /** 是否交付了本人预定的袖里藏花预留牌 */
        val fromReservation: Boolean,
        /** 实际取牌槽位（交付预留时为预留牌原槽位） */
        val wallIndex: Int
    )

    /**
     * 发一张牌入指定玩家手牌（暗牌）
     */
    fun deal(state: GameState, player: Int): Tile {
        require(player in 0..3) { "Invalid player: $player" }
        val index = state.nextAvailableWallIndex()
            ?: throw IllegalStateException("No drawable live wall tile for deal")
        val tile = state.wall[index]
        val expectedLoc = TileLocation.WALL
        require(state.tilePool.getLocation(tile) == expectedLoc) {
            "Tile $tile in wall has unexpected pool location: ${state.tilePool.getLocation(tile)}"
        }

        if (index >= state.wallPointer) state.wallPointer = index + 1
        state.players[player].closedTiles.add(tile)
        state.players[player].closedTiles.sort()
        state.tilePool.transfer(tile, expectedLoc, TileLocation.hand(player))
        return tile
    }

    /**
     * 发第 14 张牌给庄家（作为 drawnTile）
     */
    fun dealExtra(state: GameState, player: Int): Tile {
        require(player in 0..3) { "Invalid player: $player" }
        val hand = state.players[player]
        require(hand.drawnTile == null) { "Player $player already has drawnTile ${hand.drawnTile}" }

        val index = state.nextAvailableWallIndex()
            ?: throw IllegalStateException("No drawable live wall tile for extra deal")
        val tile = state.wall[index]
        val expectedLoc = TileLocation.WALL
        require(state.tilePool.getLocation(tile) == expectedLoc) {
            "Tile $tile in wall has unexpected pool location: ${state.tilePool.getLocation(tile)}"
        }

        if (index >= state.wallPointer) state.wallPointer = index + 1
        hand.drawnTile = tile
        state.tilePool.transfer(tile, expectedLoc, TileLocation.hand(player))
        return tile
    }

    /**
     * 正常摸牌（含本人预定预留牌的交付）。
     *
     * 交付路径：预留牌仍是活牌山中的实体，直到本次摸牌才从牌山移入手牌，
     * 因此主动减少 wallEndExclusive/必要时回退 wallPointer，normal draw 只发生一次。
     */
    fun drawNormal(state: GameState, player: Int): NormalDraw {
        require(player in 0..3) { "Invalid player: $player" }
        val hand = state.players[player]
        require(hand.drawnTile == null) {
            "Player $player already holds an unhandled drawnTile: ${hand.drawnTile}"
        }
        state.clearSleeveBloomWindow(player)

        val reservation = state.sleeveBloomReservation(player)
        if (reservation != null) {
            val index = state.wallTileIndex(reservation.tile)
            require(index in 0 until state.wallEndExclusive) {
                "Reserved tile ${reservation.tile} is not inside live wall"
            }
            require(state.tilePool.getLocation(reservation.tile) == TileLocation.WALL) {
                "Reserved tile ${reservation.tile} is not physically in WALL"
            }
            state.wall.removeAt(index)
            if (index < state.wallPointer) state.wallPointer--
            state.wallEndExclusive--
            state.removeSleeveBloomReservation(player)
            hand.drawnTile = reservation.tile
            state.tilePool.transfer(reservation.tile, TileLocation.WALL, TileLocation.hand(player))
            return NormalDraw(reservation.tile, fromReservation = true, wallIndex = index)
        }

        val index = state.nextAvailableWallIndex()
            ?: throw IllegalStateException("No drawable live wall tile for player $player")
        val tile = state.wall[index]
        val expectedLoc = TileLocation.WALL
        require(state.tilePool.getLocation(tile) == expectedLoc) {
            "Tile $tile at wallIndex=$index has unexpected location ${state.tilePool.getLocation(tile)}"
        }

        // 洞（index < wallPointer）重新摸出时不能把指针回退；物理剩余由 hole count 自然减少。
        if (index >= state.wallPointer) state.wallPointer = index + 1
        hand.drawnTile = tile
        state.tilePool.transfer(tile, expectedLoc, TileLocation.hand(player))
        return NormalDraw(tile, fromReservation = false, wallIndex = index)
    }

    /**
     * 正常摸牌：从活牌山顶端摸一张牌置入玩家 drawnTile
     */
    fun draw(state: GameState, player: Int): Tile = drawNormal(state, player).tile

    /**
     * 打牌：将玩家手中的一张牌移入物理牌河与舍牌历史
     *
     * @return 实际打出的物理牌
     */
    fun discard(state: GameState, player: Int, tile: Tile): Tile {
        require(player in 0..3) { "Invalid player: $player" }
        val hand = state.players[player]
        require(hand.contains(tile)) {
            "Player $player does not own tile $tile (closed=${hand.closedTiles}, drawn=${hand.drawnTile})"
        }
        val expectedLoc = TileLocation.hand(player)
        require(state.tilePool.getLocation(tile) == expectedLoc) {
            "Tile $tile pool location mismatch: expected $expectedLoc, got ${state.tilePool.getLocation(tile)}"
        }

        // 移出牌
        if (hand.drawnTile == tile) {
            hand.drawnTile = null
        } else {
            // 打出暗牌中的牌：若此时持有尚未合并的 drawnTile，先合并入闭手
            val currentDrawn = hand.drawnTile
            if (currentDrawn != null) {
                hand.closedTiles.add(currentDrawn)
                hand.drawnTile = null
                hand.closedTiles.sort()
            }
            val removed = hand.closedTiles.remove(tile)
            require(removed) { "Failed to remove tile $tile from closedTiles" }
        }

        hand.addDiscard(tile)
        state.lastDiscard = tile
        state.lastDiscardPlayer = player
        state.tilePool.transfer(tile, expectedLoc, TileLocation.discard(player))
        return tile
    }

    /**
     * 吃牌（チー）
     */
    fun chi(state: GameState, player: Int, calledTile: Tile, fromPlayer: Int, handTiles: List<Tile>): Meld {
        require(player in 0..3) { "Invalid player: $player" }
        require(fromPlayer in 0..3) { "Invalid fromPlayer: $fromPlayer" }
        require(fromPlayer == (player + 3) % 4) {
            "Chi is only allowed from kamicha ${(player + 3) % 4}, but got $fromPlayer"
        }
        require(calledTile.isNumberTile) { "Chi is only allowed for number tiles, got $calledTile" }
        require(handTiles.size == 2) { "Chi requires exactly 2 hand tiles, got ${handTiles.size}" }

        val hand = state.players[player]
        val fromHand = state.players[fromPlayer]

        // 校验打牌者牌河顶端牌
        require(fromHand.discards.isNotEmpty() && fromHand.discards.last() == calledTile) {
            "Called tile $calledTile is not the last discard of player $fromPlayer"
        }

        // 校验手牌包含用于吃的两张牌
        require(handTiles.all { hand.closedTiles.contains(it) }) {
            "Player $player hand does not contain chi tiles $handTiles"
        }

        // 校验花色相同且三张牌形成连续顺子
        val allThree = (handTiles + calledTile)
        val suit = calledTile.suit
        require(allThree.all { it.suit == suit }) { "Chi tiles must all have suit $suit" }
        val sortedRanks = allThree.map { it.rank }.sorted()
        require(sortedRanks[1] == sortedRanks[0] + 1 && sortedRanks[2] == sortedRanks[1] + 1) {
            "Chi tiles must form a sequence, got ranks $sortedRanks"
        }

        // 校验 TilePool 状态
        require(state.tilePool.getLocation(calledTile) == TileLocation.discard(fromPlayer)) {
            "Called tile $calledTile not in discard of player $fromPlayer"
        }
        for (t in handTiles) {
            require(state.tilePool.getLocation(t) == TileLocation.hand(player)) {
                "Hand tile $t not in hand of player $player"
            }
        }

        // 执行转移
        val currentDrawn = hand.drawnTile
        if (currentDrawn != null) {
            hand.closedTiles.add(currentDrawn)
            hand.drawnTile = null
            hand.closedTiles.sort()
        }

        for (t in handTiles) {
            hand.closedTiles.remove(t)
        }
        fromHand.discards.removeLast() // 物理牌河移出（保留在 discardHistory 中）

        val meld = Meld(
            type = MeldType.CHI,
            tiles = allThree.sortedBy { it.rank },
            fromPlayer = fromPlayer,
            calledTile = calledTile
        )
        hand.addMeld(meld)

        state.tilePool.transfer(calledTile, TileLocation.discard(fromPlayer), TileLocation.meld(player))
        for (t in handTiles) {
            state.tilePool.transfer(t, TileLocation.hand(player), TileLocation.meld(player))
        }

        return meld
    }

    /**
     * 碰牌（ポン）
     */
    fun pon(state: GameState, player: Int, calledTile: Tile, fromPlayer: Int, handTiles: List<Tile>): Meld {
        require(player in 0..3) { "Invalid player: $player" }
        require(fromPlayer in 0..3) { "Invalid fromPlayer: $fromPlayer" }
        require(player != fromPlayer) { "Cannot pon own discard" }
        require(handTiles.size == 2) { "Pon requires exactly 2 hand tiles, got ${handTiles.size}" }

        val hand = state.players[player]
        val fromHand = state.players[fromPlayer]

        require(fromHand.discards.isNotEmpty() && fromHand.discards.last() == calledTile) {
            "Called tile $calledTile is not the last discard of player $fromPlayer"
        }
        require(handTiles.all { hand.closedTiles.contains(it) }) {
            "Player $player hand does not contain pon tiles $handTiles"
        }
        require(handTiles.all { it.sameType(calledTile) }) {
            "Pon tiles must have same type as $calledTile"
        }
        val distinctCopies = (handTiles + calledTile).distinct()
        require(distinctCopies.size == 3) { "Pon tiles contain duplicate copies: ${handTiles + calledTile}" }

        require(state.tilePool.getLocation(calledTile) == TileLocation.discard(fromPlayer)) {
            "Called tile $calledTile not in discard of player $fromPlayer"
        }
        for (t in handTiles) {
            require(state.tilePool.getLocation(t) == TileLocation.hand(player)) {
                "Hand tile $t not in hand of player $player"
            }
        }

        val currentDrawn = hand.drawnTile
        if (currentDrawn != null) {
            hand.closedTiles.add(currentDrawn)
            hand.drawnTile = null
            hand.closedTiles.sort()
        }

        for (t in handTiles) {
            hand.closedTiles.remove(t)
        }
        fromHand.discards.removeLast()

        val meld = Meld(
            type = MeldType.PON,
            tiles = handTiles + calledTile,
            fromPlayer = fromPlayer,
            calledTile = calledTile
        )
        hand.addMeld(meld)

        state.tilePool.transfer(calledTile, TileLocation.discard(fromPlayer), TileLocation.meld(player))
        for (t in handTiles) {
            state.tilePool.transfer(t, TileLocation.hand(player), TileLocation.meld(player))
        }

        return meld
    }

    /**
     * 大明杠（OPEN_KAN）
     */
    fun openKan(state: GameState, player: Int, calledTile: Tile, fromPlayer: Int, handTiles: List<Tile>): Meld {
        require(player in 0..3) { "Invalid player: $player" }
        require(fromPlayer in 0..3) { "Invalid fromPlayer: $fromPlayer" }
        require(player != fromPlayer) { "Cannot open kan own discard" }
        require(handTiles.size == 3) { "Open kan requires exactly 3 hand tiles, got ${handTiles.size}" }

        val hand = state.players[player]
        val fromHand = state.players[fromPlayer]

        require(fromHand.discards.isNotEmpty() && fromHand.discards.last() == calledTile) {
            "Called tile $calledTile is not the last discard of player $fromPlayer"
        }
        require(handTiles.all { hand.closedTiles.contains(it) }) {
            "Player $player hand does not contain open kan tiles $handTiles"
        }
        require(handTiles.all { it.sameType(calledTile) }) {
            "Kan tiles must have same type as $calledTile"
        }
        val distinctCopies = (handTiles + calledTile).distinct()
        require(distinctCopies.size == 4) { "Open kan tiles must have all 4 unique copies" }

        require(state.tilePool.getLocation(calledTile) == TileLocation.discard(fromPlayer)) {
            "Called tile $calledTile not in discard of player $fromPlayer"
        }
        for (t in handTiles) {
            require(state.tilePool.getLocation(t) == TileLocation.hand(player)) {
                "Hand tile $t not in hand of player $player"
            }
        }

        val currentDrawn = hand.drawnTile
        if (currentDrawn != null) {
            hand.closedTiles.add(currentDrawn)
            hand.drawnTile = null
            hand.closedTiles.sort()
        }

        for (t in handTiles) {
            hand.closedTiles.remove(t)
        }
        fromHand.discards.removeLast()

        val meld = Meld(
            type = MeldType.OPEN_KAN,
            tiles = handTiles + calledTile,
            fromPlayer = fromPlayer,
            calledTile = calledTile
        )
        hand.addMeld(meld)

        state.tilePool.transfer(calledTile, TileLocation.discard(fromPlayer), TileLocation.meld(player))
        for (t in handTiles) {
            state.tilePool.transfer(t, TileLocation.hand(player), TileLocation.meld(player))
        }

        return meld
    }

    /**
     * 暗杠（CLOSED_KAN）
     *
     * 规范约束（DESIGN.md 3.3 & ACCEPTANCE.md A04）：
     * 未参与暗杠的 drawnTile 必须先合并回闭手，不能被随后的岭上摸牌覆盖丢失。
     */
    fun closedKan(state: GameState, player: Int, tiles: List<Tile>): Meld {
        require(player in 0..3) { "Invalid player: $player" }
        require(tiles.size == 4) { "Closed kan requires exactly 4 tiles, got ${tiles.size}" }
        val hand = state.players[player]

        val first = tiles.first()
        require(tiles.all { it.sameType(first) }) { "All closed kan tiles must have same type" }
        require(tiles.distinct().size == 4) { "Closed kan requires 4 distinct copies" }

        // 校验手牌包含全部4张牌
        require(tiles.all { hand.contains(it) }) {
            "Player $player does not hold all 4 tiles for closed kan: $tiles"
        }
        for (t in tiles) {
            require(state.tilePool.getLocation(t) == TileLocation.hand(player)) {
                "Tile $t not in hand of player $player"
            }
        }

        // 处理 drawnTile：
        // 若 drawnTile 参与了暗杠，则消耗它；
        // 若 drawnTile 未参与暗杠，则先将其合并回 closedTiles！
        val currentDrawn = hand.drawnTile
        if (currentDrawn != null) {
            if (tiles.contains(currentDrawn)) {
                hand.drawnTile = null
            } else {
                hand.closedTiles.add(currentDrawn)
                hand.drawnTile = null
                hand.closedTiles.sort()
            }
        }

        for (t in tiles) {
            hand.closedTiles.remove(t)
        }

        val meld = Meld(
            type = MeldType.CLOSED_KAN,
            tiles = tiles,
            fromPlayer = player,
            calledTile = tiles.first()
        )
        hand.addMeld(meld)

        for (t in tiles) {
            state.tilePool.transfer(t, TileLocation.hand(player), TileLocation.meld(player))
        }

        return meld
    }

    /**
     * 加杠（ADDED_KAN，小明杠）
     *
     * 规范约束（DESIGN.md 3.3 & ACCEPTANCE.md A04）：
     * 加杠若使用刚摸到的牌（drawnTile == tile），将其置入副露并清空 drawnTile；
     * 若加杠使用暗牌，而 drawnTile 是其他牌，先将 drawnTile 合并回闭手，保证随后岭上牌不覆盖旧牌。
     */
    fun addedKan(state: GameState, player: Int, tile: Tile): Meld {
        require(player in 0..3) { "Invalid player: $player" }
        val hand = state.players[player]
        require(hand.contains(tile)) { "Player $player does not hold tile $tile for added kan" }
        require(state.tilePool.getLocation(tile) == TileLocation.hand(player)) {
            "Tile $tile not in hand of player $player"
        }

        val existingPon = hand.melds.find {
            it.type == MeldType.PON && it.tiles.first().sameType(tile)
        } ?: throw IllegalArgumentException("No matching PON found for added kan with tile $tile")

        require(existingPon.tiles.none { it == tile }) {
            "Tile $tile is already part of the PON meld"
        }

        // 移除牌与合并未参杠的 drawnTile
        if (hand.drawnTile == tile) {
            hand.drawnTile = null
        } else {
            hand.closedTiles.remove(tile)
            val currentDrawn = hand.drawnTile
            if (currentDrawn != null) {
                hand.closedTiles.add(currentDrawn)
                hand.drawnTile = null
                hand.closedTiles.sort()
            }
        }

        hand.melds.remove(existingPon)
        val newMeld = Meld(
            type = MeldType.ADDED_KAN,
            tiles = existingPon.tiles + tile,
            fromPlayer = existingPon.fromPlayer,
            calledTile = existingPon.calledTile
        )
        hand.addMeld(newMeld)

        state.tilePool.transfer(tile, TileLocation.hand(player), TileLocation.meld(player))
        return newMeld
    }

    /**
     * 岭上摸牌（RINSHAN_DRAW）
     *
     * 规范约束（DESIGN.md 3.1 & 3.3，ACCEPTANCE.md A03 & A04）：
     * 1. 取王牌区 rinshanPointer 处的岭上牌放入玩家 drawnTile；
     * 2. 从活牌山末端（wallEndExclusive - 1）取出一张替补牌补入 deadWallReplacements，缩小 wallEndExclusive；
     * 3. 补入牌不加入岭上可摸槽位或改变宝牌索引；
     * 4. 有效王牌张数恒等于 14。
     */
    fun drawRinshan(state: GameState, player: Int): Tile {
        require(player in 0..3) { "Invalid player: $player" }
        val hand = state.players[player]
        require(hand.drawnTile == null) {
            "Cannot draw rinshan while player $player still holds drawnTile ${hand.drawnTile}"
        }
        require(state.rinshanPointer in 0..3) {
            "Rinshan tiles exhausted: pointer=${state.rinshanPointer} (must be in 0..3 for at most 4 rinshan draws)"
        }
        require(state.deadWall.size == 14) {
            "Dead wall must have exactly 14 tiles, got ${state.deadWall.size}"
        }
        val availableIndices = state.availableWallIndices()
        require(availableIndices.isNotEmpty()) {
            "No non-reserved live wall tile available for dead wall replacement"
        }

        val rinshanTile = state.deadWall[state.rinshanPointer]
        require(state.tilePool.getLocation(rinshanTile) == TileLocation.DEAD_WALL) {
            "Rinshan tile $rinshanTile not in DEAD_WALL"
        }

        val replIndex = availableIndices.last()
        val replacementTile = state.wall[replIndex]
        require(state.tilePool.getLocation(replacementTile) == TileLocation.WALL) {
            "Replacement tile $replacementTile not in WALL"
        }

        // 从可用槽位移除替补实体；尾端可能有锁定/预留牌，不能直接覆盖尾部。
        // removeAt 会保持其余实体（含锁定/预留牌）在活牌山连续区间内，
        // 且不会把尾端的预留牌当作替补偷走。
        state.wall.removeAt(replIndex)
        if (replIndex < state.wallPointer) state.wallPointer--

        // 推进指针与补入
        state.rinshanPointer++
        state.wallEndExclusive--
        state.deadWallReplacements.add(replacementTile)
        state.tilePool.transfer(replacementTile, TileLocation.WALL, TileLocation.DEAD_WALL)

        hand.drawnTile = rinshanTile
        state.tilePool.transfer(rinshanTile, TileLocation.DEAD_WALL, TileLocation.hand(player))

        return rinshanTile
    }

    /**
     * 将手牌中的一张牌与活牌山指定槽位（默认下一张可用牌）等额置换（供偷梁换柱/袖里藏花使用）
     */
    fun swapHandWithWall(state: GameState, player: Int, handTile: Tile, wallIndex: Int = -1): Tile {
        require(player in 0..3) { "Invalid player: $player" }
        val resolvedIndex = if (wallIndex < 0) {
            state.nextAvailableWallIndex()
                ?: throw IllegalArgumentException("No available live wall slot for swap")
        } else {
            wallIndex
        }
        require(resolvedIndex in 0 until state.wallEndExclusive) {
            "wallIndex $resolvedIndex out of wall range [0, ${state.wallEndExclusive})"
        }
        val hand = state.players[player]
        require(hand.contains(handTile)) { "Player $player does not hold tile $handTile" }

        val wallTile = state.wall[resolvedIndex]
        require(!state.isProtectedSleeveBloomTile(wallTile)) {
            "wallTile $wallTile is locked or reserved by sleeve bloom"
        }
        require(state.tilePool.getLocation(handTile) == TileLocation.hand(player)) {
            "handTile $handTile not in hand of player $player"
        }
        require(state.tilePool.getLocation(wallTile) == TileLocation.WALL) {
            "wallTile $wallTile not in live wall"
        }

        // 执行置换。手牌牌在 wall 列表中可能仍留有摸走前的旧槽位；
        // 该槽位不能继续以 WALL 身份出现，否则会与新槽位重复计数。
        val oldIndex = state.wall.indexOf(handTile)
        if (oldIndex >= 0 && oldIndex != resolvedIndex) {
            state.wall[oldIndex] = wallTile
        }
        state.wall[resolvedIndex] = handTile
        if (hand.drawnTile == handTile) {
            hand.drawnTile = wallTile
        } else {
            hand.closedTiles.remove(handTile)
            hand.closedTiles.add(wallTile)
            hand.closedTiles.sort()
        }

        state.tilePool.transfer(handTile, TileLocation.hand(player), TileLocation.WALL)
        state.tilePool.transfer(wallTile, TileLocation.WALL, TileLocation.hand(player))

        return wallTile
    }

    /**
     * 袖里藏花杠后原语：将刚补到的岭上牌与下一张未取岭上牌交换
     */
    fun swapDrawnWithNextRinshan(state: GameState, player: Int): Tile {
        require(player in 0..3) { "Invalid player: $player" }
        val hand = state.players[player]
        val currentDrawn = hand.drawnTile
            ?: throw IllegalArgumentException("Cannot swap rinshan: player $player has no drawnTile")
        require(state.tilePool.getLocation(currentDrawn) == TileLocation.hand(player)) {
            "drawnTile $currentDrawn not in hand of player $player"
        }
        require(state.rinshanPointer in 1..3) {
            "rinshanPointer must be in 1..3, was ${state.rinshanPointer}"
        }
        require(state.totalKanCount < 4) {
            "totalKanCount must be < 4, was ${state.totalKanCount}"
        }
        val originalRinshan = state.deadWall[state.rinshanPointer - 1]
        require(originalRinshan == currentDrawn) {
            "Original rinshan tile mismatch: deadWall[${state.rinshanPointer - 1}] is $originalRinshan but drawnTile is $currentDrawn"
        }
        val nextRinshan = state.deadWall[state.rinshanPointer]
        require(state.tilePool.getLocation(nextRinshan) == TileLocation.DEAD_WALL) {
            "nextRinshan $nextRinshan not in DEAD_WALL"
        }

        // 置换 drawnTile 与 deadWall[rinshanPointer]
        state.deadWall[state.rinshanPointer] = currentDrawn
        hand.drawnTile = nextRinshan

        // 账本记账
        state.tilePool.transfer(currentDrawn, TileLocation.hand(player), TileLocation.DEAD_WALL)
        state.tilePool.transfer(nextRinshan, TileLocation.DEAD_WALL, TileLocation.hand(player))

        return nextRinshan
    }

    /**
     * 偷梁换柱杠后原语：将原有暗手牌与有效死山未展示槽位原地对调
     *
     * @param state 单局状态
     * @param player 施术者席位 (0..3)
     * @param handTile 原有暗手牌（必须在 closedTiles 中且不等于 drawnTile）
     * @param targetSlot 目标槽位（DeadWallSlot 或 ReplacementSlot）
     * @param expectedTileId 目标槽位期望的实体牌标识
     * @return 换入手牌的原死山实体牌
     */
    fun swapHandWithDeadWall(
        state: GameState,
        player: Int,
        handTile: Tile,
        targetSlot: com.majiang.cheat.beam.BeamSwapSlot,
        expectedTileId: Int
    ): Tile {
        require(player in 0..3) { "Invalid player: $player" }
        val hand = state.players[player]
        require(hand.closedTiles.contains(handTile)) {
            "Player $player does not hold tile $handTile in closedTiles"
        }
        require(hand.drawnTile != handTile) {
            "handTile $handTile cannot be drawnTile"
        }
        require(state.tilePool.getLocation(handTile) == TileLocation.hand(player)) {
            "handTile $handTile not in hand of player $player, was: ${state.tilePool.getLocation(handTile)}"
        }

        val targetTile = when (targetSlot) {
            is com.majiang.cheat.beam.BeamSwapSlot.DeadWallSlot -> {
                val idx = targetSlot.index
                require(idx in 0..13) { "DeadWallSlot index out of bounds: $idx" }
                require(idx in state.rinshanPointer..3 || (idx in 4..13 && !state.doraIndicators.contains(state.deadWall[idx]))) {
                    "DeadWallSlot index $idx is not an eligible unrevealed dead wall slot"
                }
                state.deadWall[idx]
            }
            is com.majiang.cheat.beam.BeamSwapSlot.ReplacementSlot -> {
                val idx = targetSlot.index
                require(idx in 0 until state.deadWallReplacements.size) {
                    "ReplacementSlot index out of bounds: $idx (replacements size: ${state.deadWallReplacements.size})"
                }
                state.deadWallReplacements[idx]
            }
            is com.majiang.cheat.beam.BeamSwapSlot.WallSlot -> {
                throw IllegalArgumentException("WallSlot cannot be used with swapHandWithDeadWall")
            }
        }

        require(targetTile.tileId == expectedTileId) {
            "Target tile id at $targetSlot is ${targetTile.tileId}, expected $expectedTileId"
        }
        require(state.tilePool.getLocation(targetTile) == TileLocation.DEAD_WALL) {
            "Target tile $targetTile at $targetSlot not in DEAD_WALL, was: ${state.tilePool.getLocation(targetTile)}"
        }
        require(!state.doraIndicators.contains(targetTile)) {
            "Target tile $targetTile at $targetSlot is a revealed dora indicator"
        }

        // 执行原地对调
        when (targetSlot) {
            is com.majiang.cheat.beam.BeamSwapSlot.DeadWallSlot -> {
                state.deadWall[targetSlot.index] = handTile
            }
            is com.majiang.cheat.beam.BeamSwapSlot.ReplacementSlot -> {
                state.deadWallReplacements[targetSlot.index] = handTile
            }
            is com.majiang.cheat.beam.BeamSwapSlot.WallSlot -> {}
        }

        hand.closedTiles.remove(handTile)
        hand.closedTiles.add(targetTile)
        hand.closedTiles.sort()

        state.tilePool.transfer(handTile, TileLocation.hand(player), TileLocation.DEAD_WALL)
        state.tilePool.transfer(targetTile, TileLocation.DEAD_WALL, TileLocation.hand(player))

        return targetTile
    }
}
