package com.majiang.application

import com.majiang.event.GameEvent
import com.majiang.event.snapshotGameEvent
import com.majiang.state.ActionResult
import com.majiang.state.GameAction

/**
 * 对 [GameAction] 进行深度不可变复制，防止客户端或注册表篡改内部数据。
 */
fun GameAction.deepCopy(): GameAction = when (this) {
    is GameAction.StartStacking -> this.copy(stackingPlans = this.stackingPlans.toList())
    is GameAction.DealComplete -> this
    is GameAction.DrawTile -> this.copy()
    is GameAction.DiscardTile -> this.copy(tile = this.tile.copy())
    is GameAction.DeclareChi -> this.copy(meld = this.meld.copy(tiles = this.meld.tiles.map { it.copy() }))
    is GameAction.DeclarePon -> this.copy(tiles = this.tiles.map { it.copy() })
    is GameAction.DeclareKan -> this.copy(tiles = this.tiles.map { it.copy() })
    is GameAction.DeclareRiichi -> this.copy(discardTile = this.discardTile.copy())
    is GameAction.DeclareTsumo -> this.copy()
    is GameAction.DeclareRon -> this.copy()
    is GameAction.Pass -> this.copy()
    is GameAction.DeclareAbortiveDraw -> this.copy()
    is GameAction.DeclareAgariYame -> this.copy()
}

/**
 * 对 [ActionResult] 进行深拷贝，隔离包含的事件数组。
 */
fun ActionResult.deepCopy(): ActionResult = when (this) {
    is ActionResult.Accepted -> ActionResult.Accepted(events.map { snapshotGameEvent(it) })
    is ActionResult.Rejected -> ActionResult.Rejected(reason)
}
