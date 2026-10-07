# 任务 T1：server 契约层（传输无关接口 + DTO）

你是 Anti，负责本任务的纯代码开发。**只用文件读写工具，禁止运行任何命令、构建、测试**——构建与测试由主会话执行。完成后在最终回复中列出你创建/修改的全部文件路径清单。

必读设计文档（先读再写代码）：
- `doc/SERVER_CLIENT_API.md`（接口契约的最终依据，尤其 §2/§3/§5）
- `doc/GAME_ARCHITECTURE_V4.md`（分层与依赖规则）

## 目标

在 server 模块新建 `com.majiang.server.api` 包，落地传输无关接口层。本任务**纯新增**，不修改任何现有文件。

## 文件所有权（独占，只允许在这些路径下新建文件）

```
server/src/main/kotlin/com/majiang/server/api/
server/src/test/kotlin/com/majiang/server/api/
```

其余一切文件只读。不要修改 settings.gradle / build.gradle（server 模块已依赖 engine 与 kotlin(jvm)，无需改动）。

## 交付物

### 1. 接口（对齐 SERVER_CLIENT_API.md §2，KDoc 必须写明语义契约）

`LobbyApi.kt`：
- `listRooms(): CompletableFuture<List<RoomSummary>>`
- `createRoom(req: CreateRoomRequest): CompletableFuture<RoomBinding>`
- `joinRoom(req: JoinRoomRequest): CompletableFuture<RoomBinding>`
- `resumeSession(req: ResumeRequest): CompletableFuture<RoomBinding>`
- `leaveRoom(): CompletableFuture<ActionReceipt>`
- 接口 KDoc：全异步；REJECTED 回执是合法结果；绑定前可用。

`SessionApi.kt`：
- `capabilities / snapshot / decision / submit(ActionChoice) / submitShufflePlan(ShufflePlanRequest) / start / nextRound / resume(cursor: Long) / requestControl(takeover: Boolean) / queryRoundResult(roundId: String)`
- 返回类型一律 `CompletableFuture<...>`，载荷类型直接复用 engine 既有 DTO（见 §3）。

`PushChannel.kt`：
- `onUpdate(listener: (PlayerUpdate) -> Unit): AutoCloseable`
- `onDisconnect(listener: (reason: String) -> Unit): AutoCloseable`

`ServerConnection.kt`（组合入口）：
- `interface ServerConnection : LobbyApi, SessionApi, PushChannel, AutoCloseable`
- 只读属性 `val closed: Boolean`、`val disconnectReason: String?`

### 2. 新增 DTO（api 包自有；对齐 SERVER_CLIENT_API.md §3.1 字段）

- `CreateRoomRequest(playerName: String? = null, config: MatchConfig? = null, protocolVersion: Int)`
- `JoinRoomRequest(roomId: String, playerName: String? = null, preferSeat: Int? = null)`
- `ResumeRequest(roomId: String, token: String, cursor: Long? = null)`
- `RoomBinding(roomId: String, seat: Int, resumeToken: String, snapshot: PlayerSnapshot)`
- `RoomSummary(roomId, roomName, hostPlayerName, playerCount, maxPlayers = 4, matchStarted, configSummary = "")`
- 校验放 `init {}`：seat 0..3、roomId/resumeToken 非空白、playerCount in 1..maxPlayers、protocolVersion >= 1；参照 engine 现有 DTO 的写法（`engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt` 是风格样板）。
- `toString()` 脱敏 token（参考 `client/src/main/kotlin/com/majiang/network/protocol/NetworkMessages.kt` 的写法）。

### 3. 错误码与常量

`ApiErrors.kt`：全部错误码字符串常量（SERVER_CLIENT_API.md §5 表格逐条落地）+ `ApiProtocol`（`PROTOCOL_VERSION = 4`、`DISCONNECTED_BY_NEW_CONNECTION` 等 KICK 原因常量）。

### 4. 复用 engine 载荷（不要复制、不要新建）

`PlayerSnapshot`、`PlayerUpdate`、`ActionReceipt`、`ActionChoice`、`PlayerDecision`、`MatchConfig`、`ShufflePlanRequest`、`AccessCapabilities`、`GameEvent` 直接 import `com.majiang.access.*` / `com.majiang.application.*` / `com.majiang.match.*` / `com.majiang.stacking.*` / `com.majiang.event.*`（engine 包）。过渡期决策见 `doc/v4-migration/PLAN.md` §过渡期决策 1。

### 5. 单元测试（你写文件，主会话跑）

`server/src/test/kotlin/com/majiang/server/api/` 下：
- DTO 参数校验测试（非法值逐一拒绝、合法值通过、toString 不含 token 明文）。
- 一个内存假实现 `FakeServerConnection`（实现 `ServerConnection`，仅用于编译期契约验证与后续 T2/T5 测试复用），配套最小冒烟测试。
- 测试风格参照 `engine/src/test/` 现有 JUnit5 用例；依赖已在 server/build.gradle 声明（junit-jupiter、kotlin-test），不要新增依赖。

## 陷阱清单

1. **禁止修改任何现有文件**——包括看起来"应该顺带改"的（NetworkMessages.kt、engine DTO、build.gradle）。旧 TCP 栈在 T7 前保持原样。
2. `ActionChoice`/`PlayerDecision` 在 `com.majiang.application` 包；`PlayerSnapshot` 系列在 `com.majiang.access`——别 import 错。
3. 所有集合字段用不可变包装（`Collections.unmodifiableList` + 防御性拷贝），风格对齐 engine DTO。
4. `RoomBinding.snapshot` 非空（绑定即推初始快照，SERVER_CLIENT_API §3.1）。
5. KDoc 用中文，写清幂等契约：`(seat, requestId)` 去重、重复提交重放回执不二次执行。
6. 不要新建 Gradle 模块/源集；包路径严格 `com.majiang.server.api`。

## 完成标准

主会话将执行：`:server:compileKotlin :server:test` 与全量 `test`。你的代码需保证：编译零错误、测试零失败、除上述两个独占路径外 `git status` 无任何其它改动。
