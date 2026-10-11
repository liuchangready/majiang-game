package com.majiang.cheat.mark

import java.util.ArrayList
import java.util.Collections
import java.util.LinkedHashSet

/**
 * 跨局落汗标记状态（不可变领域状态组件）
 *
 * @property version 状态版本号（递增）
 * @property dealtRoundOrdinal 已发牌局次计数（从0开始）
 * @property dealtRoundIds 历次已发牌局标识列表（有序，不可变）
 * @property settledRoundIds 已完成结算的局标识集合（不可变）
 * @property groups 当前生效的落汗标记组列表（不可变）
 */
class TileMarkState private constructor(
    val version: Long,
    val dealtRoundOrdinal: Int,
    dealtRoundIds: List<String>,
    settledRoundIds: Set<String>,
    groups: List<TileMarkGroup>
) {
    val dealtRoundIds: List<String> = Collections.unmodifiableList(ArrayList(dealtRoundIds))
    val settledRoundIds: Set<String> = Collections.unmodifiableSet(LinkedHashSet(settledRoundIds))
    val groups: List<TileMarkGroup> = Collections.unmodifiableList(ArrayList(groups))

    constructor() : this(
        version = 0L,
        dealtRoundOrdinal = 0,
        dealtRoundIds = emptyList(),
        settledRoundIds = emptySet(),
        groups = emptyList()
    )

    /**
     * 登记新成功发牌的局。
     *
     * 同一 ID 已登记则返回原实例；
     * 新 ID 只能在上一局已结算或初始空状态登记，局次加 1；
     * 拒绝空 ID。
     */
    fun registerDealtRound(roundId: String): TileMarkState {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        if (roundId in dealtRoundIds) {
            return this
        }
        require(dealtRoundIds.isEmpty() || dealtRoundIds.last() in settledRoundIds) {
            "Previous round '${dealtRoundIds.last()}' has not been settled"
        }
        val newDealtRoundIds = ArrayList(dealtRoundIds).apply { add(roundId) }
        return TileMarkState(
            version = version + 1L,
            dealtRoundOrdinal = dealtRoundOrdinal + 1,
            dealtRoundIds = newDealtRoundIds,
            settledRoundIds = settledRoundIds,
            groups = groups
        )
    }

    /**
     * 添加新的落汗标记组。
     *
     * 仅当前已登记且未结算局可创建，创建 roundId/ordinal 必须匹配；
     * 同一组 ID 已存在且内容相同返回原实例，内容不同拒绝；
     * 一个座位各组实体不重叠，最多 12 张/3 组；
     * 不同座位允许标记同一实体。
     */
    fun addGroup(group: TileMarkGroup): TileMarkState {
        require(dealtRoundIds.isNotEmpty()) { "No round currently registered" }
        val currentRoundId = dealtRoundIds.last()
        require(currentRoundId !in settledRoundIds) {
            "Current round '$currentRoundId' has already been settled"
        }
        require(group.createdRoundId == currentRoundId) {
            "group.createdRoundId ('${group.createdRoundId}') must match current round '$currentRoundId'"
        }
        require(group.createdRoundOrdinal == dealtRoundOrdinal) {
            "group.createdRoundOrdinal (${group.createdRoundOrdinal}) must match dealtRoundOrdinal $dealtRoundOrdinal"
        }

        val existing = groups.firstOrNull { it.groupId == group.groupId }
        if (existing != null) {
            if (existing == group) {
                return this
            }
            throw IllegalArgumentException(
                "Group with id '${group.groupId}' already exists with different definition: existing=$existing, new=$group"
            )
        }

        val seatGroups = groups.filter { it.ownerSeat == group.ownerSeat }
        require(seatGroups.size < 3) {
            "Seat ${group.ownerSeat} already has ${seatGroups.size} groups (maximum 3 groups allowed)"
        }
        require(seatGroups.sumOf { it.tileIds.size } + group.tileIds.size <= 12) {
            "Seat ${group.ownerSeat} would exceed 12 marked tiles"
        }

        val seatTileIds = seatGroups.flatMap { it.tileIds }.toSet()
        require(group.tileIds.none { it in seatTileIds }) {
            "Tile IDs in group '${group.groupId}' overlap with existing marks for seat ${group.ownerSeat}"
        }

        val newGroups = ArrayList(groups).apply { add(group) }
        return TileMarkState(
            version = version + 1L,
            dealtRoundOrdinal = dealtRoundOrdinal,
            dealtRoundIds = dealtRoundIds,
            settledRoundIds = settledRoundIds,
            groups = newGroups
        )
    }

    /**
     * 结算并完成指定局。
     *
     * 必须是已登记当前局，已结算 ID 重复调用无变化；未知 ID 拒绝。
     * 在结算最后有效局时移除到期组，其他组继续保留。
     */
    fun completeRound(roundId: String): TileMarkState {
        require(roundId.isNotBlank()) { "roundId cannot be blank" }
        if (roundId in settledRoundIds) {
            return this
        }
        require(dealtRoundIds.isNotEmpty() && roundId == dealtRoundIds.last()) {
            "Cannot complete round '$roundId': not the current registered round (current: ${dealtRoundIds.lastOrNull()})"
        }
        val newSettledRoundIds = LinkedHashSet(settledRoundIds).apply { add(roundId) }
        val newGroups = groups.filter { it.expiresAfterRoundOrdinal > dealtRoundOrdinal }
        return TileMarkState(
            version = version + 1L,
            dealtRoundOrdinal = dealtRoundOrdinal,
            dealtRoundIds = dealtRoundIds,
            settledRoundIds = newSettledRoundIds,
            groups = newGroups
        )
    }

    /**
     * 获取指定座位的落汗标记组列表（只读）。
     */
    fun groupsForSeat(seat: Int): List<TileMarkGroup> {
        require(seat in 0..3) { "seat must be in 0..3, got: $seat" }
        return Collections.unmodifiableList(groups.filter { it.ownerSeat == seat })
    }

    /**
     * 清理指定座位的全部标记组。
     *
     * 不改变其他座位、发牌局次或结算历史；非法座位拒绝；无变化返回原实例。
     */
    fun clearSeat(seat: Int): TileMarkState {
        require(seat in 0..3) { "seat must be in 0..3, got: $seat" }
        val remaining = groups.filter { it.ownerSeat != seat }
        if (remaining.size == groups.size) {
            return this
        }
        return TileMarkState(
            version = version + 1L,
            dealtRoundOrdinal = dealtRoundOrdinal,
            dealtRoundIds = dealtRoundIds,
            settledRoundIds = settledRoundIds,
            groups = remaining
        )
    }

    /**
     * 清除全部标记组，保留局次与历史。
     *
     * 空组时无变化返回原实例。
     */
    fun clearAll(): TileMarkState {
        if (groups.isEmpty()) {
            return this
        }
        return TileMarkState(
            version = version + 1L,
            dealtRoundOrdinal = dealtRoundOrdinal,
            dealtRoundIds = dealtRoundIds,
            settledRoundIds = settledRoundIds,
            groups = emptyList()
        )
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TileMarkState) return false
        return version == other.version &&
                dealtRoundOrdinal == other.dealtRoundOrdinal &&
                dealtRoundIds == other.dealtRoundIds &&
                settledRoundIds == other.settledRoundIds &&
                groups == other.groups
    }

    override fun hashCode(): Int {
        var result = version.hashCode()
        result = 31 * result + dealtRoundOrdinal.hashCode()
        result = 31 * result + dealtRoundIds.hashCode()
        result = 31 * result + settledRoundIds.hashCode()
        result = 31 * result + groups.hashCode()
        return result
    }

    override fun toString(): String {
        return "TileMarkState(version=$version, dealtRoundOrdinal=$dealtRoundOrdinal, dealtRoundIds=$dealtRoundIds, settledRoundIds=$settledRoundIds, groups=$groups)"
    }
}
