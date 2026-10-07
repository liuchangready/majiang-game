package com.majiang.presentation.common

import com.majiang.client.api.LobbySession
import com.majiang.client.api.Receipt
import java.util.concurrent.CompletableFuture

/**
 * 表现层牌桌与场景统一导航契约 (TableNavigation)。
 *
 * 遵循 R5-UI-INTERFACE-PREPARATION 契约：
 * 1. 由应用组合根 (MahjongGame) 统一实现；
 * 2. 所有方法仅暴露 client.api 类型与标准类型，杜绝向 Screen 泄漏 engine 或 protocol；
 * 3. 根持有具体 runtime，负责屏幕切换、导航、tick、pause/resume、leave 与 close 语义；
 * 4. 菜单初次连接拿 [LobbySession]，建房/加房/观战成功后通知根切屏。
 */
interface TableNavigation {

    /**
     * 打开本地单机大厅会话
     */
    fun openLocalLobby(): CompletableFuture<LobbySession>

    /**
     * 建立远端 UDP 连接并打开大厅会话（初始 socket/DNS 必须在后台执行）
     *
     * @param host 远端服务器主机名或 IP
     * @param port 远端服务器 UDP 端口
     */
    fun openRemoteLobby(host: String, port: Int): CompletableFuture<LobbySession>

    /**
     * 导航进入真人玩家对局牌桌场景 (GameScreen)
     */
    fun showPlayerTable()

    /**
     * 导航进入独立观战牌桌场景 (SpectatorScreen)
     */
    fun showSpectatorTable()

    /**
     * 主动离开当前对局牌桌返回主菜单场景（先完成 leaveRoom 语义）
     */
    fun leaveToMenu(): CompletableFuture<Receipt?>

    /**
     * 退出游戏
     */
    fun exitGame()
}
