# R5 ROOT 定点返修：真实大厅流程与异步生命周期

R4已接受。初版R5ROOT `R5ROOT-1791346004621` 已真实退出并交付，但 Codex 只读检视发现下列实质缺口；初版尚未编译/测试，不能接受。沿用 ROOT 的独占范围，允许 `MahjongGame.kt`、`MenuScreen.kt`、`UiAssets.kt`、`TableNavigation.kt`、`client.api`必要门面、`UdpClient.kt`仅初连失败清理、ROOT拥有的对应测试、平台/Gradle/server main。不要改 COMMON/GAME拥有的文件和 Codex reviewer。COMMON/GAME两路仍在并行写入，不运行shell/Gradle/测试，不进入R6。

先读 `TASK-ANTI-R5ROOT.md`、`R5-UI-INTERFACE-PREPARATION.md`、`FEEDBACK-R5ROOT.md` 和初版交付文件，再逐项完成：

1. 单机 `MahjongGame.launchGame()` 和菜单快速开始均需在 `createRoom()` 成功后调用 `LobbySession.start()`；只在APPLIED时切玩家牌桌。失败保持大厅/错误可见，不把LOBBY伪装为开赛。对应测试要验证真实生命周期。
2. 菜单房间成员/准备/房主权限使用 `createRoom()/joinRoom()` 返回的 `GameSnapshotView.viewerSeat`，不得硬编码seat0。跟踪玩家/观战角色与当前绑定，旧或观战的 PLAYING 房间回调不能触发玩家切屏，不能重复导航。
3. `MahjongGame.leaveToMenu()` 和菜单房间离开按钮：仅在回执APPLIED时切屏/释放本地主机或返回大厅；异常/REJECTED保留可信画面、会话并报告错误。明确大厅“断开返回”是否真正关闭UDP会话；与按钮文案、`activeLobby`复用语义一致，等待操作完成再转MAIN，不能只更新本地状态。
4. 删除 `MenuScreen(game: MahjongGame)` 旧兼容构造及额外资产创建，根是唯一资源所有者；必要旧测试迁为新构造器并保留实际断言。
5. 赤五开关当前只改UI而MatchConfig不发送设置。若权威契约固定赤五，请取消可切换控件并展示固定规则；若实现可配置，需全链路真实规则字段，不允许UI虚假开关。本批优先固定规则展示，不跨其它开发者文件。
6. 菜单与组合根全部异步完成/订阅回调需在执行时验证未dispose、请求代次及当前会话/角色，旧菜单和被取代连接的迟到回调不得继续发查询、改画面或重复导航；过期成功连接按根所有权关闭。
7. 初版所述共同资源、直接类型安全构造器、UDP失败清理、后台首次connect、双端入口与server UDP默认不得回退。GAME路最终画面构造签名仍以冻结契约为准。

只用内置文件工具落盘，交付 `delivery-ANTI-R5ROOTFIX.md` 列实际文件、各缺口如何处理、测试未运行及遗留。交付前读取 `FEEDBACK-R5ROOTFIX.md` 若存在。Codex在三路写入全部结束后串行执行真实构建和验收。
