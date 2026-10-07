package com.majiang.server.inprocess

import com.majiang.match.MatchConfig
import com.majiang.server.api.RoomSummary
import com.majiang.server.api.ServerConnection
import com.majiang.server.host.ServerTableHost
import com.majiang.server.mapping.EngineProtocolMapper
import com.majiang.server.room.RoomRegistry
import com.majiang.server.room.SharedRoom
import com.majiang.transport.api.ProtocolRoomConfig
import java.util.concurrent.CompletableFuture
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 进程内单机麻将服务器 (InProcessServer)
 *
 * 遵循 V4 架构与 TASK-ANTI-R4B1ROOMFIX1 规范：
 * 1. 专为单机模式打造，直连同进程共享房间 [SharedRoom]，零网络协议、零序列化、零心跳；
 * 2. 内部持有统一 [RoomRegistry]，通过 [createLocalTable] 创建单机桌并返回 [InProcessBinding]；
 * 3. 暴露未绑定的连接端点 [createConnection]，供菜单/大厅场景实现建房、离房与在同一连接上再次建房/加入；
 * 4. 离开房间返回大厅且连接保持活跃 (closed = false)；
 * 5. 跨建房/离房/重置保持单人装配策略 [isSingleplayer]，确保大厅重构桌同样继承单人自动就绪行为；
 * 6. 服务端活跃房间 [currentRoom]、[currentHost] 与 [tick] 自动跟踪大厅创建的活跃房间；
 * 7. 宿主自动操作由外部 tick 驱动，禁止私起后台线程轮询 tick。
 */
class InProcessServer(
    val isSingleplayer: Boolean = true
) : AutoCloseable {

    private val isClosed = AtomicBoolean(false)
    val registry: RoomRegistry = RoomRegistry()

    @Volatile
    private var activeRoom: SharedRoom? = null

    @Volatile
    private var activeBinding: InProcessBinding? = null

    /** 服务是否已关闭 */
    val closed: Boolean get() = isClosed.get()

    /** 当前活跃的共享房间（优先返回当前活跃房间，次选注册表中首个存活房间） */
    val currentRoom: SharedRoom?
        get() {
            val r = activeRoom
            if (r != null && !r.closed) return r
            return registry.listSummaries().firstOrNull()?.let { registry.getRoom(it.roomId) }
        }

    /** 当前活跃的牌桌宿主（若无活跃牌桌或已关闭则为 null） */
    val currentHost: ServerTableHost? get() = currentRoom?.tableHost

    /** 当前活跃的连接绑定（若无活跃连接或已关闭则为 null） */
    val currentConnection: ServerConnection? get() = activeBinding

    internal fun onRoomActivated(room: SharedRoom, binding: InProcessBinding?) {
        activeRoom = room
        if (binding != null) {
            activeBinding = binding
        }
    }

    internal fun onRoomDeactivated(room: SharedRoom) {
        if (activeRoom === room) {
            activeRoom = null
        }
    }

    /**
     * 创建未绑定的服务端连接端点 (ServerConnection)，供大厅/菜单场景调用。
     *
     * 允许在该连接上调用 listRooms / createRoom / joinRoom / leaveRoom，
     * 支持在同一连接通道上离开房间后再次建房或加入。
     */
    fun createConnection(): ServerConnection {
        if (isClosed.get()) {
            throw IllegalStateException("InProcessServer is closed")
        }
        return InProcessBinding(
            server = this,
            initialRoom = null,
            viewerSeat = 0
        )
    }

    /**
     * 创建本地单机牌桌并返回统一服务端连接入口 [ServerConnection]
     *
     * 内部装配单人模式共享房间 [SharedRoom]，绑定指定真人席位 [humanSeat] 并取得 [InProcessBinding] 端点；
     * 其余空置席位按默认策略配置为电脑 AI 补位并自动全员就绪。
     * 若此前已存在旧牌桌，将先释放旧牌桌宿主以保证资源安全。
     *
     * @param config 比赛规则配置（默认为默认半庄战配置）
     * @param humanSeat 真人玩家所占席位 (0..3，默认为 0 房主位)
     * @return 进程内直连服务端连接凭据 [ServerConnection] (即 [InProcessBinding])
     * @throws IllegalStateException 若当前 InProcessServer 已关闭
     * @throws IllegalArgumentException 若 humanSeat 不在 0..3 范围内
     */
    fun createLocalTable(
        config: MatchConfig = MatchConfig(),
        humanSeat: Int = 0
    ): ServerConnection {
        if (isClosed.get()) {
            throw IllegalStateException("InProcessServer is closed")
        }
        require(humanSeat in 0..3) { "humanSeat must be in 0..3, got $humanSeat" }

        // 关闭旧宿主与旧连接，避免多宿主并存与资源泄露
        closeCurrentTable()

        val protocolConfig = ProtocolRoomConfig(
            matchConfig = EngineProtocolMapper.toProtocolMatchConfig(config)
        )
        val room = registry.createRoom(
            hostPlayerName = "Host",
            initialConfig = protocolConfig,
            isSingleplayer = isSingleplayer,
            hostSeat = humanSeat
        )
        val binding = InProcessBinding(
            server = this,
            initialRoom = room,
            viewerSeat = humanSeat
        )

        onRoomActivated(room, binding)
        return binding
    }

    /**
     * 查询大厅房间列表。
     */
    fun listRooms(): List<RoomSummary> {
        if (isClosed.get()) {
            return emptyList()
        }
        return registry.listSummaries()
    }

    /**
     * 步进驱动当前活跃牌桌的宿主时钟。
     *
     * 单机模式下由外部渲染循环或测试循环显式调用；严禁在绑定层内部自建线程轮询 tick。
     *
     * @param presentationBusy 表现层是否处于动画或展示繁忙状态
     * @return 若本 tick 推进并执行了一个动作返回 true，否则返回 false
     */
    fun tick(presentationBusy: Boolean = false): CompletableFuture<Boolean> {
        if (isClosed.get()) return CompletableFuture.completedFuture(false)
        return currentRoom?.tick(presentationBusy) ?: CompletableFuture.completedFuture(false)
    }

    /**
     * 释放当前活动的本地桌及绑定端点
     */
    private fun closeCurrentTable() {
        try {
            activeBinding?.close()
        } catch (_: Throwable) {
        }
        activeBinding = null
        try {
            activeRoom?.close()
        } catch (_: Throwable) {
        }
        activeRoom = null
    }

    /**
     * 释放 InProcessServer 及其持有的本地宿主与房间资源
     */
    override fun close() {
        if (isClosed.compareAndSet(false, true)) {
            closeCurrentTable()
            registry.close()
        }
    }
}
