# 并行C：客户端房间缓存与旧绑定失效

用户授权多个agent并行，Anti优先，额度耗尽由Codex切Claude/GLM。本任务与服务端UDP、客户端UDP传输任务并行，使用既有ServerConnection/LobbySession接口，不改变公共契约。

独占文件：client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt，client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt，必要时在同测试目录新增专用RoomRuntimeTest。不要修改client transport、protocol、server、reviewer、其他client.api文件或构建配置。只用文件工具，不shell/构建/提交/再委派。

读CONTRACT.md，实际运行时实现，以及tools/architecture/v4-review/V4RuntimeAcceptanceTest.kt中的房间用例（只读）。当前3个真实失败：

1. `reorderedAndDuplicateRoomUpdatesCannotRollReadinessBackward`：版本3,2,3只能派发3，目前派发[3,2,3]。
2. `delayedRoomQueryReturnsLatestAlreadyObservedVersion`：查询v1迟到、已推送v2，Future必须返回当前已观察v2，不能把旧结果回滚给UI。
3. `successfulLeaveInvalidatesRoomReadsAndCapturedRoomCallbacks`：成功leave时立即终结旧房间挂起查询并使旧回调失效；即使底层连接没替换，也不能在未绑定状态接受旧房间事件。

需要有room绑定代次/roomId守卫和版本缓存，与连接epoch配合；离房后建新房、同room重连都正确重置/复用。保留两个已通过房间用例：重连保留room监听且拒绝旧transport；注销抑制已排入UI队列回调。

保留R3现有快照/cursor/syncEpoch、动作pending、重连退避、线程dispatch与close挂起Future语义。不要让锁内执行UI回调；异步异常仍以Future呈现。有效规则/测试不能删除或削弱。

补模块测试，结束前读FEEDBACK-R4B1RUNTIME.md（若有），交付delivery-ANTI-R4B1RUNTIME.md。Codex统一构建验收；不声称未运行的测试通过，不进入B2/R5/R6。
