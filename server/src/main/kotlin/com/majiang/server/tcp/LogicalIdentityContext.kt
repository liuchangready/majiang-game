package com.majiang.server.tcp

import com.majiang.server.room.SharedRoom

/**
 * 已验证逻辑身份上下文 (LogicalIdentityContext, D-08 / §9.2)
 *
 * 将经过认证的业务主体 (principal)、当前逻辑角色状态 (roleState) 与请求作用域 (requestScope)
 * 组合为强类型的业务上下文对象，作为 TCP 请求路由 (TcpRequestRouter) 与权威业务调度的唯一入口凭据。
 *
 * 核心架构约束：
 * 1. 杜绝万能 Context：仅封装身份元数据，严禁持有底层 SocketChannel、Selector、TcpGlobalBudget 等传输或网络调度资源；
 * 2. 身份隔离 (D-02)：明确区分大厅角色 (LobbyRoleState)、玩家角色 (PlayerState) 与观战角色 (SpectatorState)，
 *    杜绝未授权跨角色调用；
 * 3. 严格不可变：上下文实例在请求处理期间只读，不向外部暴露任何可变公开写入口。
 */
data class LogicalIdentityContext(
    val principal: String,
    val roleState: ServerRoleState,
    val requestScope: String
) {
    init {
        require(principal.isNotBlank()) { "principal cannot be blank" }
        require(requestScope.isNotBlank()) { "requestScope cannot be blank" }
        require(roleState !is UnauthenticatedRoleState) { "roleState cannot be unauthenticated" }
        require(principal == roleState.logicalPrincipal) {
            "principal '$principal' must match roleState.logicalPrincipal '${roleState.logicalPrincipal}'"
        }
    }

    /** 底层角色枚举类型 */
    val role: ConnectionRole
        get() = roleState.role

    /** 是否属于握手前未认证状态 */
    val isUnauthenticated: Boolean
        get() = roleState is UnauthenticatedRoleState

    /** 是否属于大厅未绑定房间的角色身份 */
    val isLobby: Boolean
        get() = roleState is LobbyRoleState || roleState is UnauthenticatedRoleState

    /** 是否为强类型玩家角色 */
    val isPlayer: Boolean
        get() = roleState is PlayerState

    /** 是否为强类型观战者角色 */
    val isSpectator: Boolean
        get() = roleState is SpectatorState

    /** 派生的玩家角色状态（若非玩家角色则为 null） */
    val playerState: PlayerState?
        get() = roleState as? PlayerState

    /** 派生的观战者角色状态（若非观战者角色则为 null） */
    val spectatorState: SpectatorState?
        get() = roleState as? SpectatorState

    /** 派生的大厅角色状态（若非大厅角色则为 null） */
    val lobbyState: LobbyRoleState?
        get() = roleState as? LobbyRoleState

    /** 派生的当前绑定房间（若未进入房间则为 null） */
    val room: SharedRoom?
        get() = (roleState as? RoomRoleState)?.room

    /** 派生的玩家席位 (0..3，若非玩家角色则为 null) */
    val seat: Int?
        get() = playerState?.seat

    /** 派生的观战者 ID (若非观战者则为 null) */
    val spectatorId: String?
        get() = spectatorState?.spectatorId

    /** 派生的绑定代次 (若未绑定房间则为 null) */
    val bindingGeneration: Long?
        get() = (roleState as? RoomRoleState)?.bindingGeneration

    companion object {
        /**
         * 从物理连接与请求作用域构建已验证逻辑身份上下文 (D-08 / §9.2)
         *
         * 核心原子性与身份隔离保证：
         * 1. 严格在物理连接的安装锁 (synchronized(tcpConn)) 内部原子读取 (principal, roleState)，
         *    彻底杜绝与 installPlayerRole / installSpectatorRole / unbindToLobbyRole 产生竞态交错；
         * 2. 校验必须处于已握手态 (isHandshaked) 且非关闭态；
         * 3. 校验 principal 必须与 roleState.logicalPrincipal 严格一致，杜绝任何旧 principal + 新角色的非法组合；
         * 4. 杜绝未认证角色 (UnauthenticatedRoleState) 生成上下文；
         * 5. 若校验不通过或 requestScope 为空，原子返回 null。
         */
        fun fromConnection(conn: TransportConnection, requestScope: String): LogicalIdentityContext? {
            if (requestScope.isBlank()) return null
            val tcpConn = conn as? TcpConnection ?: return null
            val (principal, roleState) = tcpConn.snapshotLogicalIdentity() ?: return null
            return LogicalIdentityContext(
                principal = principal,
                roleState = roleState,
                requestScope = requestScope
            )
        }
    }
}
