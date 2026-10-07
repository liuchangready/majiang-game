# 交付报告：R4B1RUNTIME 客户端房间缓存与旧绑定失效

**任务编号**：`R4B1RUNTIME`  
**开发者**：Antigravity (Anti)  
**日期**：2026-10-06  

---

## 1. 修改文件清单

1. [`client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt)
2. [`client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt)

*(未修改任何 client transport、protocol、server、reviewer/acceptance 测试目录、其他 client.api 文件或构建配置)*

---

## 2. 核心改动说明

针对 `V4RuntimeAcceptanceTest.kt` 中的 3 项房间运行时失败及相关契约要求，完成了以下机制落地：

### 2.1 乱序与重复房间更新版本对账与去重 (`reorderedAndDuplicateRoomUpdatesCannotRollReadinessBackward`)
- 在 `ServerConnectionSession` 中引入 `roomLock`、`cachedRoomInfo: RoomInfo?`、`cachedRoomVersion: Long?`。
- 入站房间推送 `handleInboundRoomUpdate` 严格校验 `!closed`、`!isLeavingRoom`、`epoch == currentEpoch`、`currentBinding != null` 以及 `protoRoomInfo.roomId == currentBinding.roomId`。
- 在 `roomLock` 保护下比对版本号：仅当 `cachedRoomVersion == null || mapped.version > cachedRoomVersion` 时更新本地缓存并派发；旧版本（如 2 < 3）及重复版本（如 3 == 3）被严格静默过滤，不再向 UI 监听器倒退或重复派发。

### 2.2 迟到房间查询与最新已观察版本对账 (`delayedRoomQueryReturnsLatestAlreadyObservedVersion`)
- `getRoomInfo()` 异步查询通过 `trackRoomFuture(resultFuture, roomEpoch)` 纳入代次与生命周期跟踪。
- 传输层返回 `protoInfo` 后，在对账阶段比对本地已观察缓存：若本地已通过 PUSH 观察到同房间更高版本（如 v2 >= v1），则返回最新的已确认视图 `cachedRoomInfo`（v2），避免慢查询把陈旧信息暴露给 UI。

### 2.3 成功离房立即终结挂起查询与废弃回调 (`successfulLeaveInvalidatesRoomReadsAndCapturedRoomCallbacks`)
- 当 `leaveRoom()` 收到 `ReceiptStatus.APPLIED` 回执时：
  1. 立即清除 `currentBinding = null`；
  2. 递增 `currentRoomEpoch` 生成新代次；
  3. 清空 `syncLock` 快照/乱序缓冲与 `roomLock` 房间版本缓存；
  4. 复位 `activeActionId` 与 `pendingState`；
  5. 立即将属于已失效房间代次的所有挂起 Room Future（包括在途的 `getRoomInfo()`、`setReady()`、`updateConfig()` 等）以 `IOException("Room left: binding retired")` 异常终结。
- 对于底层连接未断开、但在大厅未绑定状态下被触发的旧房间回调（例如被测试捕获的旧 `roomListeners`），`handleInboundRoomUpdate` 鉴于 `currentBinding == null` 立即丢弃，绝不在未绑定大厅状态向 UI 派发旧房间事件。

### 2.4 注销抑制已排入 UI 队列回调 (`removingRoomObserverSuppressesAlreadyQueuedUiCallback`)
- 房间监听器包装为带有 `@Volatile var active = true` 的 `RoomObserver` 令牌。
- `onRoomUpdate` 返回的 `AutoCloseable` 将对应 `observer.active` 置为 `false` 并从集合中移除。
- `notifyRoomUpdateListeners` 派发任务在进入用户回调前校验 `observer.active`、`!closed`、连接代次、房间代次与绑定一致性，即便更新在注销前已进入外部调度队列，在排水执行时也会被精准抑制。

### 2.5 重连代次与房间状态复用
- 自动断线重连（`doReconnect`）成功复用 `currentBinding` 时，保持 `currentRoomEpoch` 不变并保留已知房间缓存；旧连接回调因连接 `epoch` 变更被拒绝，新连接自动注册 `onRoomUpdate`。
- 主动建房（`createRoom` / `joinRoom`）时递增 `currentRoomEpoch` 并重置房间缓存，确保不同对局之间的房间版本号完全隔离。

### 2.6 模块单元测试补充
- 在 `FakeServerConnection` 中补充实现了 `getRoomInfo`、`setReady`、`updateConfig`、`onRoomUpdate`、`emitRoomUpdate` 及其模拟属性。
- 在 `ServerConnectionSessionTest.kt` 中补充了 7 项针对性单元测试，覆盖版本倒退拦截、延迟查询对账、离房异常终结与未绑定事件丢弃、调度队列注销抑制、房间 ID 不匹配过滤、断线重连恢复以及新建房间重置缓存等全部边界场景。

---

## 3. 未解决项 (Unresolved Items)

- **无**。所有针对 `R4B1RUNTIME` 任务要求的房间缓存与失效机制均已完整实现并对齐契约。

---

## 4. 验证与测试声明

- **测试未由 Anti 运行 (Tests NOT run by Anti)**。
- 依据任务指令与 `AGENTS.md` 规则，Anti 仅使用内置文件编辑/读写工具完成业务代码与测试用例落盘，未执行任何终端/Shell 命令、Gradle 构建或自动化测试。
- 最终代码构建与集成测试验收由 Codex 统一执行。
