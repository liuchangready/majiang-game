package com.majiang.application

import com.majiang.state.SeatController

/**
 * 座位控制者与控制代次注册表
 *
 * 维护每个座位的当前控制者类型（真人/AI/托管）以及对应的递增控制代次 (controlEpoch)。
 * 任何控制者变更或重新初始化均自增控制代次，用于废止先前控制器已缓存的决策。
 */
class ControllerRegistry {
    private val controllers = Array<SeatController>(4) { seat ->
        if (seat == 0) SeatController.LocalHuman() else SeatController.Computer()
    }
    private val epochs = LongArray(4) { 0L }

    var hasExplicitConfiguration: Boolean = false
        private set

    fun getController(seat: Int): SeatController {
        require(seat in 0..3) { "Seat must be in 0..3, got $seat" }
        return controllers[seat]
    }

    fun getEpoch(seat: Int): Long {
        require(seat in 0..3) { "Seat must be in 0..3, got $seat" }
        return epochs[seat]
    }

    fun setController(seat: Int, controller: SeatController) {
        require(seat in 0..3) { "Seat must be in 0..3, got $seat" }
        controllers[seat] = controller
        epochs[seat]++
        hasExplicitConfiguration = true
    }

    fun initialize(newControllers: List<SeatController>) {
        require(newControllers.size == 4) { "Controllers list must have exactly 4 items, got ${newControllers.size}" }
        for (i in 0 until 4) {
            controllers[i] = newControllers[i]
            epochs[i]++
        }
        hasExplicitConfiguration = true
    }

    fun isHuman(seat: Int): Boolean {
        if (seat !in 0..3) return false
        val c = controllers[seat]
        return c is SeatController.LocalHuman || c is SeatController.RemoteHuman
    }

    fun isAutomated(seat: Int): Boolean {
        if (seat !in 0..3) return false
        val c = controllers[seat]
        return c is SeatController.Computer || c is SeatController.Takeover
    }

    val humanPlayerIndex: Int
        get() {
            val humans = (0 until 4).filter { isHuman(it) }
            return if (humans.size == 1) humans.first() else -1
        }
}
