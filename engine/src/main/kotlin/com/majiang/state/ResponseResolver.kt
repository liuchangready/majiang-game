package com.majiang.state

import com.majiang.model.MeldType

/**
 * 响应窗口仲裁器（纯函数无副作用）
 *
 * 遵循 DESIGN.md 4.2 规定与日麻权威头跳规则：
 * - 优先级排序：Ron (荣和) > Pon / OpenKan (碰/大明杠) > Chi (吃) > Pass (放弃)
 * - 同级动作按与打牌者/放铳者的逆时针相对距离裁决（头跳）：(seat - discarder + 4) % 4，近者胜出。
 * - Pon 与 OpenKan 为同级操作；Chi 仅允许下家。
 * - 人类与 AI 一视同仁，不得让人类天然优先于 AI。
 * - 重复响应无副作用：已有记录的座位不被后续重复响应覆盖。
 * - 窗口完成度判定：所有具有响应资格的座位均提交响应后，窗口方可关闭。
 */
class ResponseResolver {

    /**
     * 仲裁胜出结果
     */
    data class ArbitrationWinner(
        val seat: Int,
        val action: GameAction
    )

    /**
     * 计算动作的绝对优先级数值
     * - Ron: 3
     * - Pon / Kan: 2
     * - Chi: 1
     * - Pass 或其他: 0
     */
    fun priority(action: GameAction): Int = when (action) {
        is GameAction.DeclareRon -> 3
        is GameAction.DeclarePon -> 2
        is GameAction.DeclareKan -> {
            if (action.kanType == MeldType.OPEN_KAN) 2 else 0
        }
        is GameAction.DeclareChi -> 1
        is GameAction.Pass -> 0
        else -> 0
    }

    /**
     * 获取动作关联的执行玩家座位索引
     */
    fun actionPlayer(action: GameAction): Int = when (action) {
        is GameAction.DeclareRon -> action.player
        is GameAction.DeclarePon -> action.player
        is GameAction.DeclareKan -> action.player
        is GameAction.DeclareChi -> action.player
        is GameAction.Pass -> action.player
        is GameAction.DiscardTile -> action.player
        is GameAction.DrawTile -> action.player
        is GameAction.DeclareRiichi -> action.player
        is GameAction.DeclareTsumo -> action.player
        is GameAction.DeclareAbortiveDraw -> action.player
        is GameAction.DeclareAgariYame -> action.player
        else -> -1
    }

    /**
     * 计算相对打牌者的逆时针步长距离 (1=下家, 2=对家, 3=上家)
     */
    fun distance(seat: Int, discarder: Int): Int {
        val d = (seat - discarder + 4) % 4
        return if (d == 0) 4 else d
    }

    /**
     * 比较两个响应动作的优劣（基于打牌者视角）
     * @return 正数表示 a 优于 b；负数表示 b 优于 a；0 表示同等
     */
    fun compare(discarder: Int, a: GameAction, b: GameAction): Int {
        val prioA = priority(a)
        val prioB = priority(b)
        if (prioA != prioB) {
            return prioA.compareTo(prioB)
        }

        // 若双方均为 Pass 或无有效副露意图，视为平局
        if (prioA == 0) return 0

        // 同级动作：头跳裁决，相对距离近者（distance 较小者）胜出
        val distA = distance(actionPlayer(a), discarder)
        val distB = distance(actionPlayer(b), discarder)
        // 注意：distance 越小越优先，所以比较方向取反
        return distB.compareTo(distA)
    }

    /**
     * 从各座位提交的响应集合中仲裁出最高优先级的动作
     *
     * @param discarder 打牌者（放铳者）座位号
     * @param responses 各座位提交的动作映射 (seat -> action)
     * @return 胜出的 GameAction，若全员 Pass 或无有效副露/荣和，返回 null
     */
    fun resolve(discarder: Int, responses: Map<Int, GameAction>): GameAction? {
        val winningAction = responses.values
            .filter { priority(it) > 0 }
            .maxWithOrNull { a, b -> compare(discarder, a, b) }

        return winningAction
    }

    /**
     * 从动作列表中仲裁出最高优先级的动作
     */
    fun resolve(discarder: Int, actions: List<GameAction>): GameAction? {
        return actions
            .filter { priority(it) > 0 }
            .maxWithOrNull { a, b -> compare(discarder, a, b) }
    }

    /**
     * 仲裁并返回胜出者信息 (座位与动作)
     */
    fun resolveWinner(discarder: Int, responses: Map<Int, GameAction>): ArbitrationWinner? {
        val action = resolve(discarder, responses) ?: return null
        val seat = actionPlayer(action)
        return ArbitrationWinner(seat, action)
    }

    /**
     * 判定响应窗口是否已收集完毕：
     * 必须且仅当所有有响应资格的座位 (eligibleSeats) 均已记录响应时返回 true。
     */
    fun isWindowComplete(eligibleSeats: Set<Int>, responses: Map<Int, GameAction>): Boolean {
        if (eligibleSeats.isEmpty()) return true
        return eligibleSeats.all { it in responses.keys }
    }

    /**
     * 安全记录新响应（保证重复响应无副作用）
     * 若该座位已存在有效响应，则保持原响应不变。
     */
    fun recordResponse(
        currentResponses: Map<Int, GameAction>,
        player: Int,
        action: GameAction
    ): Map<Int, GameAction> {
        if (player in currentResponses) {
            return currentResponses // 幂等：已有响应不被覆盖
        }
        val updated = currentResponses.toMutableMap()
        updated[player] = action
        return updated
    }

    companion object {
        val INSTANCE = ResponseResolver()
    }
}
