# 任务 T5：client 门面（视图模型 + 会话接口 + UDP 客户端绑定）

你是 Anti，负责本任务的纯代码开发。**只用文件读写工具，禁止运行任何命令、构建或测试**——由主会话执行。完成后列出你创建/修改的全部文件路径清单。

必读（按序）：
- `doc/GAME_ARCHITECTURE_V4.md` §3（client 职责）、§4（三层模型：视图模型由 client 拥有）
- `doc/SERVER_CLIENT_API.md` §4（共享数据结构字段）、§6/§7（绑定与 UDP 协议）
- `protocol/src/main/kotlin/com/majiang/server/api/`（ServerConnection 契约——你要包装它）
- `protocol/src/main/kotlin/com/majiang/transport/udp/`（T3 组件）、`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`（消息编解码——只消费）
- `engine/src/main/kotlin/com/majiang/access/`（PlayerSnapshot/PlayerUpdate/ClockView/SeatControlView/SessionView 等被映射类型）
- `engine/src/main/kotlin/com/majiang/session/PlayerView.kt`（被映射的投影视图）
- 映射范围基准：`presentation/src/main/kotlin/com/majiang/screen/GameScreen.kt` 的 engine import 列表（Tile/Meld/MeldType/Suit/PlayerView/MeldChoice/RiichiDiscardOption/FuritenStatus/ScoreBreakdown/DoraBreakdown 等——这些是 T6 要替换成视图模型的全部类型）

## 目标

presentation 未来只 import `com.majiang.client.api`：本任务交付视图模型、映射器、会话门面，以及 UDP 客户端绑定（实现 ServerConnection）。

## 文件所有权（独占）

```
client/src/main/kotlin/com/majiang/client/api/
client/src/main/kotlin/com/majiang/client/transport/udp/
client/src/test/kotlin/com/majiang/client/api/
client/src/test/kotlin/com/majiang/client/transport/udp/
```

其余只读：**禁止修改** GameClient.kt、client/network/（旧 TCP）、protocol、engine、presentation。禁止 import `com.majiang.server.udp/inprocess`（server 实现包，模块依赖也不允许）。

## 交付物

### 1. 视图模型（`client/api`，全部不可变 + 防御性拷贝，风格对齐 engine DTO）

按映射范围基准逐一定义（命名 `*View`）：`TileView(tileId: Int, suit, rank)`（0..135 编码，与 engine Tile 双向映射）、`MeldView`、`LegalActionView`（密封接口：Discard/Ron/Pon/Chi/Kan/Riichi/Tsumo/Pass/千术动作——对照 `engine/state/GameAction.kt` 的变体逐一对应用）、`ClockInfo`、`ControlsInfo`、`SessionProgressView`、`SettlementDetailView`（含 ScoreBreakdown 役种明细投影）、`RoomSummaryView`、聚合根 `GameSnapshotView`（对应 PlayerSnapshot 全字段）、`GameUpdateView`（对应 PlayerUpdate）。

### 2. `EngineDtoMapper`（纯 object 函数）

engine DTO → 视图模型；`PlayerSnapshot→GameSnapshotView`、`PlayerUpdate→GameUpdateView`、`Tile→TileView`（及 List/Set 批量）。**方向单一**（不提供反向），KDoc 写明"表现层永不持有 engine 类型"的设计约束。

### 3. 会话门面接口

- `LobbySession`：`listRooms(): CompletableFuture<List<RoomSummaryView>>`、`createRoom(playerName, config): CompletableFuture<GameSnapshotView>`（返回初始视图）、`joinRoom(roomId, playerName, preferSeat?)`、`resumeSession(roomId, token, cursor?)`、`leaveRoom()`。
- `GameSession`：`snapshot()/decision()/submit(choice)/submitShufflePlan(...)/start()/nextRound()/resume(cursor)/requestControl(takeover)/queryRoundResult(roundId)/onUpdate(listener)/onDisconnect(listener)/close()/closed/disconnectReason`——出入参全部视图模型（decision 返回 `DecisionView`：候选 LegalActionView 列表 + decisionId + controlEpoch）。

### 4. `ServerConnectionSession`（`client/api`）

实现 `LobbySession + GameSession`：包装任意 `ServerConnection`（protocol 契约），方法直通 + `EngineDtoMapper` 映射；关闭/断线语义透传。T6 组合根将用它把 InProcessBinding/UdpClientBinding 适配给表现层。

### 5. `UdpClientBinding`（`client/transport/udp`，实现 `ServerConnection`）

- `UdpClient.connect(host, port): CompletableFuture<UdpClientBinding>`（真实 DatagramSocket；`UdpFrame` 编解码、ReplayGuard、connectionId 保存）。
- 大厅/会话请求经 `ReliableRequestor`（payload 用 `ApiMessageCodec`），RSP→结果映射（RoomBinding/快照/回执），错误码→异常或 REJECTED 语义对齐 T1 契约。
- PUSH：`PushSequencer` 缺口→RESEND、窗口外→全量快照自愈；分片经 `Fragmenter` 重组；`onUpdate` 派发（监听器注册前的早期推送按序缓冲，对齐旧 `ClientTransport` 行为）。
- `HeartbeatPacer` 3s PING；KICK→`close(reason)`；判死→`onDisconnect`。
- 全部时间经 `UdpClock/UdpScheduler` 注入（生产用系统实现，与 T4 对称）。

### 6. 测试（你写文件，主会话跑）

- Mapper 保真：tileId 编码往返、快照全字段映射、集合防御性拷贝（改 engine 侧集合不影响视图）。
- ServerConnectionSession：用 `FakeServerConnection` 思路自建轻量 fake（**不要** import server 测试源集——它在 protocol 测试里，跨模块不可见；自己仿写）。
- UdpClientBinding：**不发真实包**——注入 `send: (ByteArray) -> Unit` 与伪造入站帧，覆盖：REQ 重传触发（假时钟）、RSP 完成幂等、PUSH 缺口 RESEND、KICK 关闭、早期推送缓冲回放。真实端到端联调由主会话在 T4 验收后执行。

## 陷阱清单

1. 视图模型**禁止**包含 engine 类型字段（连 `Suit`/`MeldType` 枚举都要有自己的编码）。
2. client 模块可见 protocol（server.api/transport），**不可见** server 模块实现。
3. `GameAction` 是 engine/state 的密封类型——`LegalActionView` 一一对应其全部变体（含千术动作），漏一个 T6 就编译不过。
4. 提交动作的入参也要视图模型版（`ActionChoiceView`?）——**不要**：提交沿 server.api 的 `ActionChoice`（requestId/decisionId/optionId 都是原始类型，无 engine 语义泄漏），门面直接透传，KDoc 说明。
5. 禁改 GameClient/旧 TCP 栈；T6/T7 处理。

## 完成标准

主会话执行 `:client:test` 与全量 `test`，并做 T4×T5 真实 UDP 联调抽检。除独占路径外无其它改动。
