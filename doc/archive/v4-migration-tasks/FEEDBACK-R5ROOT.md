# Codex 跨路接口复核（开发中）

COMMON 路正在把 `SkinManager`、`GhostHandTexture` 等实迁 `com.majiang.presentation.common.render`；ROOT 当前新 `UiAssets.kt` 仍 import `com.majiang.render.*`。请在交付前对齐COMMON最终包名，不留下旧render依赖或双类副本。不要修改COMMON拥有的文件。

`UiAssets.createDefault()` 的本地 `FreeTypeFontGenerator` 若 `generateFont` 抛错，尚未赋给 `fontGenerator` 成员，catch 当前只释放成员null，可能泄漏原生字体资源。请把生成器生命周期收束为成功转交/失败立即释放，仍由组合根最终dispose。

`MahjongGame.kt` 当前 `Class.forName("com.majiang.screen.GameScreen")` / `SpectatorScreen` 和按首参数猜构造器的反射转发是临时绕过并行接口的壳，最终不符合R5/R6源码归属与真实入口要求。GAME 路将画面迁 `com.majiang.presentation.game` 后，请改为直接 import/调用已冻结构造器，利用编译期类型检查；不要保留旧 `com.majiang.screen.*` 字符串或兜底反射。跨路若构造签名需要调整，交付中明确通知 Codex 与 GAME 路，不要自行复制画面代码。

R5要求初次UDP连接失败释放socket和自有scheduler。`ServerConnectionSession.connectUdp` 已关闭失败的自有scheduler，但 `UdpClient.connect` 当前在创建`DatagramSocket()`后解析地址或`socket.connect`异常时仅`future.completeExceptionally`，没有关闭已创建socket。已在本任务简报追加 ROOT 对 `client/transport/udp/UdpClient.kt` 的**仅限失败清理**拥有权；请保留成功连接和运行中断线恢复语义，并补可验证异常路径。`MahjongGame.openRemoteLobby` 的异步完成也须检查画面/根是否已dispose或被新连接请求取代，迟到成功应关闭其session，不能复活旧画面。

只读中途复核补充：

1. `MahjongGame.launchGame()` 及 `MenuScreen.handleTouchMain` 单机快速入口目前只 `createRoom()` 就 `showPlayerTable()`，但此时房间仍是LOBBY；须串联 `lobby.start()`，仅APPLIED后切玩家牌桌，失败保留可见错误和大厅状态。
2. `MenuScreen.onRoomInfoUpdated` 目前把本座硬编码 `0`，加入非房主席位时 ready/host显示及权限错误。可在 `createRoom()/joinRoom()` 成功拿到 `GameSnapshotView.viewerSeat` 并缓存；观战角色须单独标记。PLAYING房间更新仅当前已绑定玩家且菜单仍适用时才切玩家桌，旧订阅/观战不能误切，防重复导航。
3. `MahjongGame.leaveToMenu` 当前无论 `leaveRoom()` 异常或REJECTED都切菜单，单机还关宿主；`MenuScreen` 房间离开按钮也忽略回执。应只在APPLIED后切屏/销毁本地宿主或回大厅，失败保持可信视图和绑定并提示错误。
4. `MenuScreen` 的 `constructor(game: MahjongGame)` 是旧兼容重载，资产未初始化时自行 `UiAssets.createDefault`，与根唯一资源所有权冲突；按用户无需旧入口兼容的授权删除并迁移对应测试。`MahjongGame` 中若还有其他反射转发/旧入口壳，同样收口为直接类型调用。
5. 当前菜单的“赤五开关”仅切换 `cfgRedFive` UI 布尔值，`MatchConfig`/建房请求无此字段，实际权威规则不变。若R5不新增完整权威配置字段，请移除可切换开关并明示固定赤五规则；不能向用户显示可配置却不生效的规则。
6. 菜单的网络回调虽 `postRunnable` 到GL线程，但 dispose 后、换大厅连接后，旧回调仍可改旧屏状态/再请求/导航。请对异步完成与房间订阅逐次检查 disposed、当前会话/请求代次；过期结果应静默丢弃并按所有权关闭迟到连接，不能让旧 PLAYING 更新重复创建牌桌。
7. 大厅“断开返回”目前调用`navigation.leaveToMenu()`后立即把同一菜单设MAIN，但仍持有`activeLobby`；`leaveToMenu`对未绑定大厅也发`leaveRoom()`，未真实断开UDP，随后MAIN又复用旧会话。请区分“返回并复用大厅”与“断开连接”，文案和实际socket/会话生命周期一致；异步操作成功后才切状态，失败保持现状并提示。
