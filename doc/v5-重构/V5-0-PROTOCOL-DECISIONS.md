# V5-0 协议预冻结决策

日期：2026-10-23
状态：**P0.2 协议语义预冻结，供 P2.1/G0/Codex 复核；不是 V5-1 验收。**
基线：`v5` 分支，`48125ff`；V4（MJ-TCP/1，JSON over TCP）仍是生产基线。
范围：冻结协议版本语义、ID/epoch、`state_version`/`command_version`/`event_seq`/`streamVersion`、错误码、消息族、未知结果与全量基线语义。
**正式 `.proto` 字段号、`reserved`、`oneof` 编号属于 P2.1，本文件不分配字段号。** 本文只给可复核的语义和待决问题。

> 证据标记：`路径:行号` / `路径#符号`。`V5 新约定` 表示 V4 没有对应实现；`待裁决` 表示产品/协议选择，不由本任务擅自定案。

---

## 0. 结论摘要

| 决策 | 预冻结值 | 说明 |
|---|---|---|
| 线协议 | **WSS + Protobuf（二进制消息）** | 不沿用 MJ-TCP/1 的 `MJTC` 12 字节头；V4/V5 不混连 |
| `protocolMajor` | **1** | V4 的 `PROTOCOL_VERSION=8`/`BUSINESS_VERSION=8` 不沿用 |
| `protocolMinor` | **0** | 初始值；minor 兼容策略见 §2.3 |
| `rulesetVersion` | **1** | 规则行为见 `V5-0-RULE-CONTRACT.md` |
| `projectionVersion` | **1** | 可见性/投影 schema 版本 |
| `eventSchemaVersion` | **1** | 事件 oneof/字段语义版本；字段号 P2.1 |
| 幂等键 | `(accountId, requestScopeId, requestId)` + 规范化载荷摘要 | 同键同载荷重放；异载荷冲突 |
| 未知结果 | `RECEIPT_QUERY` 返回 `APPLIED/RECORDED/REJECTED/UNKNOWN/NOT_FOUND` | V4 **没有** `RECEIPT_QUERY`；`UNKNOWN/NOT_FOUND` 是查询侧未确认结果，不是已持久化的确定业务回执，也不证明未提交/可重做 |
| 全量基线 | 始终完整快照 + 可见事件；`isFullSnapshot=true` 建立/重置 | 不做增量状态模型；缺口/超限回全量 |
| 流水位 | `streamVersion` = 该流最后一次实际发出投影时取的桌 `state_version`；可滞后/可重复，不是全桌当前版本 | 每流另有 `projectionSeq` 表示“第几次发出”，见 §5 |
| 版本隔离 | `state_version` 是表状态版本；`command_version` 等于其“命令后版本”；`event_seq` 是房间事件序号；`streamVersion` 是流投影水位；`projectionSeq` 是该流发射序号 | 五者不混用，见 §5 |
| 座位/观战 | 每座独立流；观战独立公开流 | 不共用一个桌级游标 |
| 字段号 | **P2.1 冻结** | 本文不写 tag 号 |

---

## 1. V4 协议事实（迁移对照）

### 1.1 线格式

- 固定 12 字节头：`"MJTC"` + `wireVersion=1` + `type(1..10)` + `flags=0` + 大端 `payloadLength(1..1,048,576)`；payload 是严格 UTF-8 JSON。
- 证据：`protocol/.../transport/tcp/TcpWire.kt:9-45,47-95`；`TcpFrame.kt:20-63`；`TcpMessages.kt:540-583`。
- 帧类型：HELLO/WELCOME/REQ/RSP/PLAYER_UPDATE/PUBLIC_UPDATE/ROOM_UPDATE/PING/PONG/CLOSE（10 类）。
- 业务版本 `BUSINESS_VERSION=8`；业务协议版本 `ApiProtocol.PROTOCOL_VERSION=8`；HELLO/WELCOME 硬相等，版本不符 `VERSION_MISMATCH`。
- 证据：`TcpWire.kt:34-45`；`ApiErrors.kt:92-100`；`TcpTableServer.kt:685-686`。
- 这三点都不能带到 V5：V5 不是 TCP 帧、不是 JSON、不是 business 8。

### 1.2 握手与限制

`TcpHello`：`wireVersion, businessVersion, lobbyResumeToken?`；`TcpWelcome`：`wireVersion, businessVersion, connectionEpoch, lobbyResumeToken, lobbyScope, limits`（`TcpMessages.kt:34-95`）。

`TcpLimits` 默认：
- `maxInFlightRequests=64`
- `requestTimeoutMillis=10000`
- `heartbeatIntervalMillis=3000`
- `idleDeadMillis=10000`

### 1.3 错误与关闭

- `ApiErrors.ALL` 共 17 个：`INVALID_PROTOCOL_VERSION, MALFORMED_REQUEST, ALREADY_BOUND, ROOM_NOT_FOUND, ROOM_FULL, SERVER_FULL, MISSING_ROOM_ID, MISSING_ROOM_OR_TOKEN, MISSING_ACTION_CHOICE, MISSING_SHUFFLE_PLAN, UNAUTHENTICATED_SESSION, INVALID_TOKEN, UNKNOWN_REQUEST_TYPE, CONFLICT, SERVER_BUSY, INTERNAL_ERROR, REQUEST_SCOPE_EXPIRED`（`ApiErrors.kt:13-87`）。
- 关闭原因 10 个：`SERVER_SHUTDOWN, ROOM_CLOSED, DISPLACED_BY_NEW_CONNECTION, LOCAL_CLOSED, HEARTBEAT_TIMEOUT, VERSION_MISMATCH, PROTOCOL_VIOLATION, HANDSHAKE_TIMEOUT, SERVER_OVERLOADED, SLOW_CONSUMER`；永久/临时分类见 `TcpCloseReasons.kt:17-105`。
- 业务拒绝不是 `ApiErrors`：V4 用 `ActionReceipt(status=REJECTED, reason=...)` 表达（`ApiErrors.kt:9-12`；`ActionReceipt.kt`；`ReceiptStatus.kt`）。

### 1.4 游标、历史与全量

- 每个座位流 `syncEpoch = "${sessionId}-$seat"`；公开流 `"${sessionId}-public"`；两者独立（`ServerTableHost.kt:391-397`）。
- 每次推送 `cursor = cursor + 1`；`fromCursor` 是旧游标；历史 256 条（`ServerTableHost.kt:358-363,379-384,2110-2113,2154-2157`）。
- `isFullSnapshot` 在协议映射中为 `fromCursor >= snapshot.cursor`（`EngineProtocolMapper.kt:1366-1371`）。
- `resume(seat, cursor)` **忽略传入 cursor**，直接返回当前全量快照（`ServerTableHost.kt:1150-1158`）。
- `resumePublic(checkpoint)` 先校验 `(sessionId, syncEpoch, cursor)`，再返回全量快照；游标不决定输出基线（`ServerTableHost.kt:1226-1252`）。
- 客户端：早期缓冲 64 条/4 MiB；重排缓冲 64；已应用游标去重 256；全量快照清空重排/游标（`PlayerStreamEngine.kt:60-63,104-156,267-274`；`SpectatorStreamEngine.kt:73-160`）。

### 1.5 回执与未知结果

- V4 业务幂等：`RequestScopeReceiptStore` 键 `(logicalPrincipal, scope, requestId)`；scope 默认 10 分钟；每成员在途 64、已完成 512；大厅副作用 1024/查询缓存 256；过期 scope 无回执返回 `REQUEST_SCOPE_EXPIRED`，禁止重做（`RequestScopeReceiptStore.kt:16-38,300-490`）。**该结果只说明 scope/回执已过期，不证明原请求未提交**：回执可能随 scope 清理而消失，而副作用已发生（`RequestScopeReceiptStore.kt:25-26,448-476`）。
- 客户端另有 `unconfirmedSideEffects` 内存重放（最多 128），按原 scope 重放；应用被杀/重启后无持久化（`TcpClientBinding.kt:342-357,417,2203-2284`）。
- **V4 没有 `RECEIPT_QUERY`/`receiptQuery`/`queryReceipt` 消息或 API**；全仓 grep 0 命中。V5 的 `RECEIPT_QUERY` 是新能力，不能写成 V4 已有。

---

## 2. V5 版本模型

### 2.1 版本号

| 版本 | 预冻结值 | 作用域 | 变更规则 |
|---|---:|---|---|
| `protocolMajor` | 1 | 连接/房间 | 不兼容语义变更才 +1；V4/V5 不混连 |
| `protocolMinor` | 0 | 连接/房间 | 只加可选字段/未知可忽略语义；不改变必填/状态机 |
| `rulesetVersion` | 1 | 房间 | 规则行为变更才 +1；活动中固定 |
| `projectionVersion` | 1 | 房间/流 | 投影白名单/字段语义变更才 +1；活动中固定 |
| `eventSchemaVersion` | 1 | 事件 | 事件 oneof/字段语义变更；P2.1 冻结字段号 |

- 房间创建时固定 `protocolMajor + rulesetVersion + projectionVersion`；活动房间不混用（方案 §5 F1）。
- `HELLO/WELCOME` 必须携带/回显这些版本语义；具体字段名/字段号 P2.1。
- `eventSchemaVersion` 可随事件携带；未知事件/未知字段不得静默当成已知语义。

### 2.2 V4/V5 不混连

- V4：TCP + `MJTC` + JSON + `wireVersion=1` + `businessVersion=8`。
- V5：WSS + Protobuf + `protocolMajor=1`，没有 `MJTC` 头。
- 因此：
  - V4 客户端连 V5 端口：不是同一线格式，直接失败/拒绝；不得回退到 V4。
  - V5 客户端连 V4 服务：不存在 V5 WSS 端点；不得协商降级。
  - 同一进程不得同时把 V4 和 V5 会话混在一个房间/一个 `sessionId`。
- 迁移期若必须共存，用不同端口/不同服务/显式 `protocolMajor` 拒绝，不做“同端口嗅探”。

### 2.3 兼容矩阵（预冻结）

| 情况 | 处理 |
|---|---|
| `protocolMajor` 不同 | 拒绝，关闭原因 `PROTOCOL_MAJOR_UNSUPPORTED`；不尝试降级 |
| `protocolMinor` 更高，且新增字段都是 optional/可忽略 | 允许；未知字段按 schema 规则忽略；不得改变必填语义 |
| `protocolMinor` 更低，但服务端只用双方都有的字段 | 允许；服务端不得发送该 minor 不存在的必填字段 |
| `rulesetVersion` 不同 | 拒绝，`RULESET_VERSION_UNSUPPORTED`；不能在同一房间混规则 |
| `projectionVersion` 不同 | 拒绝，`PROJECTION_VERSION_UNSUPPORTED`；投影白名单不同不能混 |
| 未知 `messageType` | `UNKNOWN_MESSAGE_TYPE`；不断连除非安全策略要求 |
| 未知事件类型 | 必须能跳过/保留；不能静默丢弃成“无事件”；具体由 P2.1 冻结 |
| 超限消息 | `MESSAGE_TOO_LARGE`；拒绝/关闭，不截断 |

---

## 3. 消息族

### 3.1 预冻结消息族

| 族 | 方向 | 用途 | 关联/幂等 | 备注 |
|---|---|---|---|---|
| `HELLO` / `WELCOME` | C→S / S→C | 版本、认证、连接代次、scope、限制 | `correlationId` | WELCOME 建立连接身份；不携带私有快照 |
| `COMMAND` / `RECEIPT` | C→S / S→C | 所有有副作用命令与其回执 | `requestId + requestScopeId` | 命令 body 用 oneof；回执不替代推送 |
| `PLAYER_SNAPSHOT` | C→S / S→C | 查询当前座位完整快照 | `requestId`（查询） | 返回全量；不返回增量 |
| `PUBLIC_SNAPSHOT` | C→S / S→C | 查询观战公开完整快照 | `requestId` | 仅公开白名单 |
| `PLAYER_UPDATE` | S→C | 玩家私有流推送 | `(streamVersion, projectionSeq)` 及其 `from*` | 全量快照 + 可见事件；同状态刷新也走这里 |
| `PUBLIC_UPDATE` | S→C | 观战公开流推送 | `(streamVersion, projectionSeq)` 及其 `from*` | 独立流；不共用玩家游标 |
| `ROOM_UPDATE` | S→C | 房间元数据/生命周期/成员/准备 | `roomVersion` | 大厅/房间用；不含牌局私有 |
| `SYNC_REQUEST` / `SYNC_RESPONSE` | C→S / S→C | 缺口/重连后的基线同步 | `(sessionId, streamVersion, projectionSeq)` | 响应是全量基线，不是增量猜测 |
| `RECEIPT_QUERY` / `RECEIPT_LOOKUP` | C→S / S→C | 未知结果对账 | `(requestScopeId, requestId)` | V5 新能力；V4 没有 |
| `HEARTBEAT`（PING/PONG） | 双向 | 连接保活 | `nonce` | 回显 16 hex；不与业务 deadline 混用 |
| `ERROR` / `CLOSE` | S→C（ERROR 可双向） | 错误/关闭 | `correlationId`/关闭码 | 关闭码稳定；不把业务拒绝塞成 ERROR |

### 3.2 命令族（语义，不分配字段号）

大厅/房间：
`CREATE_ROOM, JOIN_ROOM, LIST_ROOMS, RESUME_SESSION, LEAVE_ROOM, GET_ROOM_INFO, SET_READY, UPDATE_CONFIG`。

对局：
`START_MATCH, SUBMIT_ACTION, SUBMIT_SHUFFLE_PLAN, SUBMIT_TILE_MARK, SUBMIT_SLEEVE_BLOOM, SUBMIT_SLEEVE_BLOOM_CHOICE, SUBMIT_SLEEVE_BLOOM_OFF_TURN, SUBMIT_SLEEVE_BLOOM_OFF_TURN_CHOICE, SUBMIT_BEAM_SWAP, SUBMIT_BEAM_SWAP_CHOICE, NEXT_ROUND, CLAIM_CONTROL, RELEASE_CONTROL`。

查询/观战：
`QUERY_ROUND_RESULT, QUERY_PUBLIC_ROUND_RESULT, JOIN_SPECTATOR, RESUME_SPECTATOR, RESUME_PUBLIC, RESUME_CURSOR`（V5 可改名为 `SYNC_REQUEST`，语义见 §8）。

说明：
- 这些是 **V4 已验证的消息族映射**，V5 可合并/改名，但 P2.1 必须在 `.proto` 中给出明确 `oneof`，不能靠字符串 tag 分发。
- `COMMAND` 的 body 必须能区分“普通动作 / 千术 / 准备 / 控制”；同一 `requestId` 命名空间跨这些命令共享，避免同 id 复用到不同操作（V4 测试明确“all operations share request id namespace”，如 `BeamSwapContractTest`）。
- 查询类消息不使用 `requestScopeId` 作为副作用幂等键；只有有副作用命令需要。

---

## 4. Envelope、ID 与 epoch

### 4.1 Envelope 语义字段

预冻结的语义字段（字段号 P2.1 分配）：

```text
protocolMajor, protocolMinor,
messageType,
correlationId,
requestScopeId?, requestId?,
roomId?, sessionId?, roundId?, decisionId?,
controlEpoch?, bindingEpoch?, ownerEpoch?,
streamVersion?, fromStreamVersion?,
projectionSeq?, fromProjectionSeq?,
payload
```

规则：
- `correlationId`：连接内响应关联；可每次请求新生成，不承担幂等。
- `requestId`：客户端生成、跨重试稳定、承担幂等；不得用“当前时间/随机数每次重试都变”的值。
- `requestScopeId`：服务端颁发，绑定账户/角色/房间/有效期；没有 scope 的写命令拒绝。
- `roomId/sessionId/roundId/decisionId`：只有适用的消息才填；不适用时缺省，不用空字符串伪装。
- `controlEpoch`：座位控制者代次；只用于座位授权，不是桌租约。
- `bindingEpoch`：客户端本地绑定代次；**不作为服务端授权凭据**；服务端只信 `controlEpoch` + `requestScopeId` + 认证身份。
- `ownerEpoch`：桌租约代次；用于单写者 fencing，与 `controlEpoch` 不同。
- `streamVersion`：该流最后一次**实际发出投影**时取的桌 `state_version`；不是全桌当前 `state_version`，可滞后、可重复；只在该流基线/可见投影时重算，不用于命令授权。
- `fromStreamVersion`：该流**前一次实际发出投影**的 `streamVersion`；即使中间有只影响其他席位的私有命令，也不能写成全桌旧版本或要求 +1。
- `projectionSeq`：该流每发出一次投影（基线或 push）就 +1 的发射序号；用于区分“同一 `state_version` 的刷新”和“重复包”；不是 `state_version`、不是 `event_seq`、不是 `projectionVersion`。
- `fromProjectionSeq`：该流前一次实际发出投影的 `projectionSeq`；与 `fromStreamVersion` 一起做缺口/重复判断。

### 4.2 ID 规则

| ID | 生成者 | 生命周期 | 唯一性/复用 | 说明 |
|---|---|---|---|---|
| `roomId` | 服务端 | 房间 | 全局唯一；关闭后可回收但不得与活动房间冲突 | V4 是 8 位房间号；V5 可 opaque |
| `sessionId` | 服务端 | 整场 | 全局唯一；重连/换绑不变 | 不是 `roomId`；一个房间可能有多场？V5 需冻结（当前一房一场） |
| `roundId` | 服务端 | 单局 | 每局新 ID，**连庄也新**；永不复用 | `RoundStateMachine` 默认 UUID；V5 外部注入 |
| `decisionId` | 服务端 | 决策窗口 | 每次窗口/版本/控制代次变化而变；不跨局复用 | V4 形式如 `turn-<roundId>-<version>-<seat>`、`window-<roundId>-<windowId>-<seat>`、`session-dealer-choice-...` |
| `requestId` | 客户端 | 逻辑命令 | 全局/账户内唯一；重试复用；成功/拒绝后不得改 ID 重发 | 不能只用载荷哈希；同载荷不同意图也需不同 ID |
| `requestScopeId` | 服务端 | scope | 绑定账户/角色/房间/有效期；过期不可复活 | V4 10 分钟；V5 建议覆盖整场 + 结算后对账期 |
| `offerId` | 服务端 | 千术 offer | 每 offer 唯一；`roundId+seat` 内唯一 | 如 `bloom-$seat-$requestId`、`beam-$seat-$requestId` |
| `windowId` | 服务端 | 他家回合袖窗口 | 每次弃牌签发新 ID；旧 ID 失效 | `bloom-window-$seat-$turnNumber-$seq` |
| `connectionEpoch` | 服务端 | 连接 | 每次传输连接新值 | WELCOME 返回；不是座位控制代次 |
| `bindingEpoch` | 客户端 | 本地绑定 | 每次绑定/角色切换 +1 | 只用于客户端丢弃旧回调；不授权 |
| `controlEpoch` | 服务端 | 座位控制者 | 每次控制者变更 +1；单调 | 旧 epoch 提交拒绝 |
| `ownerEpoch` | 服务端 | 桌租约 | 每次租约获取 +1；单调 | DB fencing；不是座位控制 |
| `streamVersion` | 服务端 | 流投影水位 | 该流每次实际发出投影时重算为该刻的桌 `state_version`；可滞后/重复 | 不跨流比较；不是全桌当前版本 |
| `projectionSeq` | 服务端 | 流发射序号 | 该流每发出一次投影（含基线/同状态刷新）+1 | 与 `streamVersion` 配对；不是 `state_version`/`event_seq` |

### 4.3 Epoch 的最小权限原则

- `connectionEpoch`：只证明“这条连接”，不证明“这个座位”。
- `bindingEpoch`：只证明“客户端本地绑定代次”，不证明服务端身份。
- `controlEpoch`：证明“这个座位当前由谁控制”；只有它参与座位动作授权。
- `ownerEpoch`：证明“这个桌当前由哪个运行时实例写”；只参与持久化 fencing。
- 任何“用 bindingEpoch 出牌”“用 connectionEpoch 代替 controlEpoch”的实现都必须拒绝（方案 §2.1.8 明确禁止）。

---

## 5. `state_version` / `command_version` / `event_seq` / `streamVersion`（+ `projectionSeq`）

这是本文件最重要的预冻结部分。五者语义不同，禁止混用；`projectionSeq` 只是每流发射序号，不替代前四者。

### 5.1 定义

| 名称 | 作用域 | 递增规则 | 何时为空 | 用途 |
|---|---|---|---|---|
| `state_version` | 一张桌的权威状态 | 每个“已接受且改变状态”的命令恰好 +1；初始 0 | 查询/拒绝不改变 | 快照版本、回执 `command_version`、DB `expected_state_version` |
| `command_version` | 一条已接受且改变状态的命令 | **等于该命令产生的 `state_version`**；不是独立计数器 | 仅当**原命令**被拒绝时为 `null`（不存在“新命令接受但无状态变化”）；查询不生成回执；**幂等重放原样返回原回执的 `command_version`，不得改写为 `null`** | 把同命令的多个事件归组 |
| `event_seq` | 一张桌的事件日志 | 每个持久化领域事件 +1；房间内连续 | 无事件为 `null`/不写 | 重放顺序、事件连续性、审计 |
| `streamVersion` | 一个流（seat/public）的投影水位 | 该流每次实际发出投影时，取当时的桌 `state_version`；只在该流基线/可见投影时更新；可滞后、可相同、可跳跃；不是每流 +1 | 无基线为 0/空 | 客户端基线/状态缺口判断 |
| `projectionSeq` | 一个流（seat/public）的发射序号 | 该流从 0 开始；每发出一次投影（基线/push，含同 `state_version` 刷新）+1；不是 `state_version`/`event_seq` | 无基线为 0 | 重复/同版本刷新/发射缺口判断 |

回执中的 `evaluated_state_version`、`command_version`、`event_seq` 是**原命令**的字段，不是“本次重放时重新计算”的字段。同键同载荷重放必须原样返回这些原值（包括原 `APPLIED`/`RECORDED` 的非空 `command_version`）；“本次没有新副作用”只表示不产生新的版本/事件，不表示把原字段置空。

### 5.2 硬规则

1. **`state_version` 不是事件数**：一个命令可以产生 1..N 个事件，`state_version` 只 +1。
2. **`command_version` 不是第二个计数器**：它就是该命令后的 `state_version`；同一命令的所有事件共享它。
3. **`event_seq` 是全局房间事件序号**：同一命令的 N 个事件按 `event_ordinal=0..N-1` 写入，`event_seq` 连续；不同命令之间也连续；它不是 `state_version`。
4. **`streamVersion` 是该流最后一次实际发出投影时取的桌 `state_version`**，不是全桌当前 `state_version`。它只在该流发出基线或可见投影时重算；因为其他席位的私有命令会推进桌 `state_version` 而该流不发出投影，所以它**可以滞后**；因为投影刷新可能不改变桌状态，所以它**可以重复**。不得要求每流 +1，也不得用“全桌当前 `state_version`”去校验它。
4a. **`projectionSeq` 是每流发射序号**：该流从 0 开始，每发出一次投影（基线或 push，包括同 `state_version` 的刷新）就 +1。它回答“这是该流第几次发出”，不回答“桌状态第几版”。同一 `streamVersion` + 下一个 `projectionSeq` 是正常刷新，不是重复包。
4b. **`fromStreamVersion`/`fromProjectionSeq` 必须是该流前一次实际发出的值**，不是全桌的旧 `state_version`，也不是“当前减一”。若桌状态因其他席位私有事件从 5 到 6，而该流仍在 5/10，下一次该流真正发出投影（例如公开事件把桌状态推到 7）时，必须写 `fromStreamVersion=5, fromProjectionSeq=10, streamVersion=7, projectionSeq=11`；客户端 5/10 接受，不能判为缺口。
5. **拒绝不占版本**：`REJECTED` 不递增 `state_version`，不写事件；原回执只记 `evaluated_state_version`（评估时版本），`command_version=null`。
5a. **`UNKNOWN` 不是确定回执**：它是客户端/传输/查询侧“无法确认是否已提交”的状态，不是服务端已持久化的确定业务回执。它**不得断言** `state_version`/事件未变；提交可能已经发生并推进了版本。后续必须用 `RECEIPT_QUERY` 对账，不能用 `UNKNOWN` 驱动重发或改写版本。
6. **新受理命令不能是“接受但无状态变化”**：一个新 `requestId` 的命令只有两种结果——改变权威状态（`APPLIED`/有状态变化的 `RECORDED`，`state_version+1` 且至少 1 个事件）或被 `REJECTED`。不存在“新命令被接受但不改状态、也不占版本”的第三类。精确幂等重放（同键同载荷）不是新命令，返回**原确定回执的原始字段**（包括 `status/reasonCode/evaluated_state_version/command_version/event_seq`），只表示本次没有新副作用，不重算、不置空；查询/`RECEIPT_QUERY` 不是业务命令，不生成新回执。
6a. **`RECORDED` 不是 no-op 豁免**：它只表示“已持久接受、最终裁决待定”。若该命令改变权威状态（包括记录响应、准备/就绪、控制权变更、预留/占用等会影响后续裁决的状态），必须给新 `command_version` + 至少 1 个事件；若它不改变权威状态，只能是已有回执的幂等重放或非变更查询，不得以“纯簿记”为名新受理一条 `RECORDED`。若产品把 ready/控制状态视为“元数据而非权威状态”，应使用独立的非命令消息/存储，不能伪装成业务命令。
7. **接受且有状态变化必须至少 1 个事件**：否则 `STATE_EVENT_MISSING` 内部错误并回滚，不写 journal（方案 §3.2.2）。V4 的袖/梁二选一是反例，V5 必须补私有事件。
8. **`event_seq` 在流内不要求连续**：玩家流只收到自己的私有事件 + 公开事件；观战只收到公开事件。不能用“某个 `event_seq` 缺失”判断丢包；丢包判断用 `fromStreamVersion`/`fromProjectionSeq` 与当前流的 `streamVersion`/`projectionSeq`。
9. **`streamVersion` 不能替代 `state_version`**：DB/回执/审计用 `state_version`；客户端流基线用 `streamVersion`；不要把 `streamVersion` 写进 `command_version`，也不要用它判断桌状态是否最新。
10. **`event_seq`/`projectionSeq` 不能互相替代**：`event_seq` 是桌事件日志顺序；`projectionSeq` 是单流发射顺序；两者都不是 `state_version`，也都不要求跨流相同。
11. **无可见事件但公开快照字段变化时必须推送**：若该流白名单内的快照字段发生变化（分数、牌河、副露、立直、局/场进度、暂停、控制状态等），即使 `visibleEvents` 为空，也要发一次 push；此时 `streamVersion` 可以不变，`projectionSeq` 必须 +1。只有 `serverNow` 这类纯时间采样变化不单独触发。若字段与事件都没有变化，则不推送。

### 5.3 示例

#### 示例 A：桌状态推进，但该流没有可见事件

当前桌 `state_version=5`；观战流上次发出 `streamVersion=5, projectionSeq=10`。P0 的私有命令（其他席不可见）把桌推到 `state_version=6`，观战流不发出任何东西，仍是 `5/10`。

随后一个公开弃牌把桌推到 `state_version=7`，观战流发出：

```text
fromStreamVersion=5, streamVersion=7,
fromProjectionSeq=10, projectionSeq=11,
visibleEvents=[TileDiscarded...]
```

客户端 5/10 接受：`fromStreamVersion` 等于自己当前 5，`fromProjectionSeq` 等于 10；中间的 6 对该流不可见，不是缺口。**不能把 `fromStreamVersion` 写成 6，也不能要求 `streamVersion` 每步 +1。**

#### 示例 B：无可见事件，但公开快照字段变化

桌仍是 `state_version=7`；观战流上次 `streamVersion=7, projectionSeq=11`。仅公开的计时字段（如 `clocks.deadline`/`clocks.reserve`）或其它展示字段变化，且没有对应领域事件：

```text
fromStreamVersion=7, streamVersion=7,
fromProjectionSeq=11, projectionSeq=12,
visibleEvents=[]
```

这是正常的“同状态刷新”：`streamVersion` 不变、`projectionSeq+1`；客户端以 `projectionSeq` 判断不是重复包。若只 `serverNow` 变化、其他公开字段不变，则不推送。

#### 示例 C：命令版本/事件序号

一个命令产生 3 个事件，当前 `state_version=7`：

```text
after command: state_version=8, command_version=8
events: event_seq=101,102,103; command_version=8; event_ordinal=0,1,2
```

- 回执：`evaluated_state_version=7`, `command_version=8`, `event_seq=103`。
- 被拒绝的命令：`evaluated_state_version=8`, `command_version=null`, `event_seq=null`；`state_version` 仍 8。
- 某玩家流在 `state_version=8` 时只看到 1 个可见事件：`streamVersion=8`，`event_seq` 可能是 102（不是 101/103）；客户端不能要求 101..103 连续。
- 若客户端当前 `5/10`，下一次收到 `fromStreamVersion=6` 或 `fromProjectionSeq=11`（而自己仍是 10），说明中间该流有更新丢失/跨基线：请求全量；不要用 `event_seq` 差集猜测。

### 5.4 V4 字段的处置

- V4 `roundVersion` 是 `RoundStateMachine.version`，每次 `install` +1；它包含 eventless 的状态变化，因此**不能直接当 V5 `state_version`**。
- V4 `sessionVersion` 是 `MatchSession.version`，整场 +1；V5 可保留为 Domain 调试字段，但协议同步以 `state_version`/`streamVersion` 为准。
- V4 `cursor` 是“每次可见更新 +1”，不是事件序号，也不是 `state_version`。V5 的对应物是 **`projectionSeq`**（每流发射序号），不是 `streamVersion`；`streamVersion` 是桌 `state_version` 的投影水位。V5 建议不再在线路上保留 `cursor` 语义；若保留该名字，也只作为 `projectionSeq` 的别名，不能当 `streamVersion`。
- **V4 是 JSON 协议，没有 Protobuf 字段号可继承。** `reserved` 只能指 **V5 `.proto` 自身的字段号**：由 P2.1 给 V5 schema 分配/保留编号；不能声称“保留 V4 的字段号”，也不能把 V4 JSON 字段名当作 Protobuf tag。V5 若不暴露 `cursor`，直接在 V5 schema 中不定义该字段即可；若未来在 V5 内废弃某个字段，才由 P2.1 对 **V5 自己的编号** 做 `reserved`。
- `roundVersion` 是否进入 V5 Wire 待 P2.1；若保留，必须注明“Domain 版本，不用于流同步”，避免与 `state_version` 混用。

---

## 6. 错误码与回执

### 6.1 两类错误必须分开

1. **传输/协议/认证/容量错误**：连接级或请求信封级，返回 `ERROR` 或 `CLOSE`；不改变牌局状态。
2. **业务拒绝**：命令已到达权威但规则不允许，返回 `RECEIPT(status=REJECTED, reasonCode=...)`；不改变状态、不占版本。

V4 的原则正确（`ApiErrors.kt:9-12`），但 V5 需要把“错误码”和“拒绝原因码”分别冻结，不能把 `WRONG_PHASE` 之类的业务原因塞进 `ApiErrors`，也不能把 `SERVER_BUSY` 当成业务拒绝。

`UNKNOWN` 不属于上述两类：它既不是传输错误，也不是业务拒绝，而是客户端/传输/查询侧“尚未确认”的结果。它不能写成确定业务回执，也不能断言服务端版本未变；见 §6.4/§7.2。

### 6.2 V4 → V5 错误码映射

| V4 `ApiErrors` | V5 预冻结 | 类别 | 说明 |
|---|---|---|---|
| `INVALID_PROTOCOL_VERSION` | `PROTOCOL_MAJOR_UNSUPPORTED` | 协议 | major 不兼容；关闭 |
| `MALFORMED_REQUEST` | `MALFORMED_MESSAGE` | 协议 | 解码/字段/类型错误 |
| `ALREADY_BOUND` | `ALREADY_BOUND` | 状态 | 已绑定席位 |
| `ROOM_NOT_FOUND` | `ROOM_NOT_FOUND` | 业务 | 房间不存在/关闭 |
| `ROOM_FULL` | `ROOM_FULL` | 业务 | 席位满 |
| `SERVER_FULL` | `SERVER_FULL` | 容量 | 服务端总容量满；与 `SERVER_BUSY`（在途/队列满）区分 |
| `MISSING_ROOM_ID` | `MALFORMED_MESSAGE`（field=`roomId`） | 协议 | 缺必填字段 |
| `MISSING_ROOM_OR_TOKEN` | `MALFORMED_MESSAGE`（field=`roomId/token`） | 协议 | 缺必填字段 |
| `MISSING_ACTION_CHOICE` | `MALFORMED_MESSAGE`（field=`command`） | 协议 | 缺命令体 |
| `MISSING_SHUFFLE_PLAN` | `MALFORMED_MESSAGE`（field=`shufflePlan`） | 协议 | 缺准备方案 |
| `UNAUTHENTICATED_SESSION` | `UNAUTHENTICATED` | 认证 | 未绑定身份 |
| `INVALID_TOKEN` | `INVALID_TOKEN` | 认证 | token 无效/过期 |
| `UNKNOWN_REQUEST_TYPE` | `UNKNOWN_MESSAGE_TYPE` | 协议 | 未知消息类型 |
| `CONFLICT` | `REQUEST_ID_CONFLICT` | 幂等 | 同键异载荷 |
| `SERVER_BUSY` | `SERVER_BUSY` | 容量 | 在途/队列满 |
| `INTERNAL_ERROR` | `INTERNAL_ERROR` | 内部 | 兜底；不回传内部细节 |
| `REQUEST_SCOPE_EXPIRED` | `REQUEST_SCOPE_EXPIRED` | 幂等 | scope 过期且无回执；禁止重做；**不证明原请求未提交** |

### 6.3 V5 新增错误码（预冻结）

| 码 | 类别 | 何时 |
|---|---|---|
| `PROTOCOL_MINOR_UNSUPPORTED` | 协议 | minor 过新且包含必填语义，无法安全忽略 |
| `RULESET_VERSION_UNSUPPORTED` | 协议 | 房间 ruleset 不匹配 |
| `PROJECTION_VERSION_UNSUPPORTED` | 协议 | 投影版本不匹配 |
| `MESSAGE_TOO_LARGE` | 协议/容量 | 超过单消息上限 |
| `UNKNOWN_MESSAGE_TYPE` | 协议 | 未知 messageType |
| `STALE_STREAM_VERSION` | 同步 | `fromStreamVersion/fromProjectionSeq` 落后/无效，需全量基线 |
| `STREAM_GAP` | 同步 | 无法从历史补齐；返回/要求全量 |
| `LEASE_LOST` | 内部/容量 | 桌租约丢失，写失败；不发布状态 |
| `RECEIPT_NOT_FOUND` | 对账 | 查询键无确定回执（可能从未受理，也可能已清理）；**不等于未提交** |
| `RECEIPT_UNKNOWN` | 对账 | 查询侧无法判定是否提交；可能已提交，不是确定回执/成功/失败 |

说明：
- `RECEIPT_NOT_FOUND` 与 `RECEIPT_UNKNOWN` 是 **RECEIPT_QUERY 的结果**，不是普通命令错误；不要用 `INTERNAL_ERROR` 混过去。
- `STALE_CONTROL_EPOCH`、`DECISION_MISMATCH`、`WRONG_PHASE`、`DEADLINE_EXPIRED`、`NOT_YOUR_TURN`、`INSUFFICIENT_FUNDS` 等是**回执 reasonCode**，不是传输错误。
- 稳定码规则：大写 ASCII `[A-Z][A-Z0-9_]*`；只增不改；不复用旧码；未知码由客户端回退到通用文案，不能崩溃。

### 6.4 回执状态

| 状态 | 含义 | 是否确定业务回执 | `command_version` | 事件 | 版本 |
|---|---|---|---|---|---|
| `APPLIED` | 已提交并生效 | 是 | 原命令产生的 `state_version` | >=1 | `state_version+1` |
| `RECORDED` | 已持久接受、最终裁决待定（如响应已记录/准备已确认），且**已经改变权威状态** | 是 | 原命令产生的 `state_version` | >=1 | `state_version+1` |
| `REJECTED` | 规则拒绝 | 是 | `null` | 0 | 不变 |
| `UNKNOWN` | 客户端/传输/查询侧无法确认是否已提交；**可能已经提交** | **否** | 无断言 | 无断言 | **不得断言不变；可能已推进** |

`APPLIED`/`RECORDED`/`REJECTED` 是**确定业务回执**，可以写入 receipt ledger；`UNKNOWN` **不是**确定业务回执，只能存在于客户端 outbox、传输层或 `RECEIPT_QUERY` 响应中，不能作为已持久化的最终业务状态，也不能据此断言服务端 `state_version`/事件没有变化。

`RECORDED` 是最容易被误用的状态。V5 硬规则：

- **新受理命令**的 `RECORDED` 必须已经改变权威状态，因此必须有新 `command_version` 与至少 1 个事件；不能出现“新命令 + RECORDED + 无事件/无版本”。
- 如果业务上只是“已收到/已登记，但没有改变任何权威状态”，这不是一条新业务命令：它只能是对已有 `(scopeId, requestId)` 的**幂等重放**（返回原确定回执原字段）或查询（`RECEIPT_QUERY`/快照），不得生成新的 `RECORDED`。
- “纯簿记”不是豁免：ready/就绪、响应记录、控制权、预留/占用等只要会影响后续裁决，就是权威状态，必须事件化；若产品认为这些是元数据，应走独立的非命令消息，不要伪装成业务命令。
- `APPLIED` 与 `RECORDED` 的区别只在“最终裁决是否已完成”，不在“是否占版本/是否有事件”；两者只要改变状态就都占版本、都有事件。

对照表：

| 情况 | 结果 | 版本/事件 |
|---|---|---|
| 新命令 + 改变权威状态 | `APPLIED` 或 `RECORDED` | `state_version+1`，>=1 事件 |
| 新命令 + 不改变权威状态 | `REJECTED`（或改用非命令消息） | 不变，0 事件 |
| 同 `(scopeId,requestId)` 同载荷重放 | **原确定回执原字段**（含原 `command_version/evaluated_state_version/event_seq`） | 不产生新版本/新事件，但不得把原字段置空或重算 |
| 查询/`RECEIPT_QUERY` | 查询结果；有确定回执则原样返回，无确定结果则 `UNKNOWN/NOT_FOUND` | 不生成新业务回执，不因查询改变版本 |

补充：
- 重放 `APPLIED`/`RECORDED` 时，原 `command_version` 非空，重放结果仍为非空；原 `evaluated_state_version`/`event_seq` 也是原值。只有原 `REJECTED` 才保留 `command_version=null`。
- `UNKNOWN` 不能被写入确定回执账本；若查询最终得到确定回执，按该回执对账；若仍无确定结果，保持 `UNKNOWN/NOT_FOUND`，不自动重发。

### 6.5 关闭原因

V5 保留 V4 的分类语义（永久/临时），但用 WSS Close code + 应用码表达：
- 永久：`PROTOCOL_MAJOR_UNSUPPORTED, RULESET_VERSION_UNSUPPORTED, PROTOCOL_VIOLATION, DISPLACED_BY_NEW_CONNECTION, ROOM_CLOSED, SERVER_SHUTDOWN, LOCAL_CLOSED`
- 临时：`HANDSHAKE_TIMEOUT, HEARTBEAT_TIMEOUT, SERVER_OVERLOADED, SLOW_CONSUMER`
- 新增：`LEASE_LOST`（永久/需重建）、`MESSAGE_TOO_LARGE`（视策略）
- 具体 WSS close code 映射 P2.1；本文只冻结应用码语义。

---

## 7. 幂等、未知结果与回执查询

### 7.1 幂等键

- 键：`(accountId, requestScopeId, requestId)`；同时记录 `payloadSha256`（规范化业务载荷，不是 Protobuf 重序列化字节）。
- 同键同载荷：返回**原确定回执的原始字段**（`status/reasonCode/evaluated_state_version/command_version/event_seq` 等），不重算、不置空；不得重复扣费/出牌/结算。原 `APPLIED`/`RECORDED` 的 `command_version` 仍非空，原 `REJECTED` 的仍为 `null`。
- 同键异载荷/异 API：`REQUEST_ID_CONFLICT`，不执行。
- 键在 scope 有效期内持久；过期后旧回执可保留对账期；回执被清理后无回执也不证明未提交，**不得自动或人工盲目重做**。
- 每个成员/连接有在途与已完成上限；超限在受理前返回 `SERVER_BUSY`，不先执行再拒绝。

### 7.2 未知结果

定义：
- `UNKNOWN` = **客户端/传输/查询侧**无法确认该 `requestId` 是否已提交（网络断、进程崩溃、回执丢失、scope/存储不可读）。它不是服务端已持久化的确定业务回执，**不能断言** `state_version`/事件未变；提交可能已经发生并推进了版本。
- `UNKNOWN` 不是“失败”，也不是“成功”；客户端不得自动换新 `requestId` 重发，也不得据此回滚/改写本地已确认状态。
- 客户端持久 outbox：`PendingCommand{requestId, scopeId, payloadHash, createdAt, state}`；收到确定 `APPLIED/REJECTED` 后删除；`RECORDED` 保持待定；`UNKNOWN/NOT_FOUND` 保持待对账。
- 启动/重连：先 `RECEIPT_QUERY`；返回：
  - `APPLIED`/`RECORDED`/`REJECTED`：返回**原确定回执的原始字段**（包括原 `evaluated_state_version/command_version/event_seq`），按原回执处理；不重算、不置空、不重发；
  - `UNKNOWN`：查询侧仍无法判定；展示“结果待对账”，可人工重试查询；**不自动重发**，不写确定回执；
  - `NOT_FOUND`：没有可查的确定回执（可能从未受理，也可能原回执已过保留期被清理）；**不等于确定未提交**，展示“需人工/业务对账”，不自动重做。
- `UNKNOWN`、`NOT_FOUND`、`REQUEST_SCOPE_EXPIRED` 且无回执**都不足以证明**该具体 `requestId` 在原 scope 下未准入或未提交：回执可能已被清理，而提交/事件已经发生（`RequestScopeReceiptStore.kt:25-26,448-476`；V5 设计 §3.4.2 `DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md:1366-1398` 先查回执再查 scope）。因此**不得自动或人工盲目**以新 requestId 重做。
- 只有**可审计地证明**该具体 `requestId` 在原 scope 下“未准入且未提交”（例如服务端准入/事务审计明确记录该键未登记、且不存在对应事件/副作用/outbox；或客户端持久 outbox 可证明从未发送且服务端审计无记录）时，才允许用户显式以**新** requestId 重新发起。无法证明时，只能人工/业务对账，不得重做。不能静默换 ID，也不能把 `UNKNOWN/NOT_FOUND/REQUEST_SCOPE_EXPIRED` 当确定失败。

### 7.3 V4 对照

- V4 没有 `RECEIPT_QUERY`；客户端重连时旧 Future 可能失败，同时内存 `unconfirmedSideEffects` 按原 scope 重放（`TcpClientBinding.kt:2203-2284`）。这会造成“调用方看到失败但动作可能已生效”，且应用被杀后内存丢失。
- V5 必须把“回执查询”做成正式消息，并把回执/事件/outbox 放在同一事务；V4 的内存重放只能作为测试参考，不能当 V5 的对账语义。
- V4 的 `RequestScopeReceiptStore` 是内存实现，scope 10 分钟后清理回执；V5 需要持久化（PostgreSQL）与明确的保留期/清理条件（方案 §3.4、P3.4）。

### 7.4 事务边界（预冻结）

- 一个**新受理且改变状态**的命令 = 一个 `AppendBatch`：事件 + 回执 + outbox（如结算）同事务；`APPLIED`/有状态变化的 `RECORDED` 都走这条。
- `REJECTED` 也写确定回执（`status=REJECTED, command_version=null`），但不写事件、不递增版本。
- 同键同载荷的幂等重放**不创建新 `AppendBatch`**、不写新事件、不改版本；返回已存确定回执的**原始字段**（包括原 `command_version/evaluated_state_version/event_seq`），不得重算/置空。查询/`RECEIPT_QUERY` 同样不创建业务批。
- `UNKNOWN` **不是** `AppendBatch` 的结果：不写确定业务回执、不写事件、不递增版本；它只是客户端/传输/查询侧的未确认状态，不能据此断言版本不变。若数据库/存储暂时不可用，查询返回 `UNKNOWN`，待恢复后再对账。
- `REQUEST_SCOPE_EXPIRED` 且无回执、`NOT_FOUND`、`UNKNOWN` 都不能作为“未提交”证明，不能据此创建补偿性新命令/新业务批；只有可审计地证明该具体 requestId 未准入且未提交，才允许新 requestId。
- “新命令 + 无状态变化”不是合法 `AppendBatch`：要么拒绝，要么改造成非命令消息；不能写一条只有回执、没有事件/版本的“已接受”记录。
- DB/存储提交成功后才发布状态/推送；失败不得先发布。
- `ownerEpoch` + `expected_state_version` 双栅栏；旧租约/旧版本写入失败。
- 回执只存小型结果/原因码/版本，不存完整快照；快照由状态投影生成。

---

## 8. 全量基线、推送与恢复

### 8.1 语义

V5 初版保持 V4 已验证的“**完整快照 + 可见事件**”语义：
- `PLAYER_UPDATE`/`PUBLIC_UPDATE` 始终带完整快照；可见事件是本次状态变更产生的、该流可见的事件。**不能假设这些事件补齐了客户端缺口**；`fromStreamVersion`/`fromProjectionSeq` 不连续时走全量基线。
- `isFullSnapshot=true` 表示“这是基线/重置”：客户端安装快照，重置 `streamVersion/projectionSeq`，清空重排缓冲/已应用去重/早期缓冲；不从旧状态合并。基线可省略 `fromStreamVersion/fromProjectionSeq`，客户端以基线中的值作为新起点。
- `isFullSnapshot=false` 表示增量/刷新；必须满足 `fromStreamVersion == 客户端当前 streamVersion` **且** `fromProjectionSeq == 客户端当前 projectionSeq`；任一对不上就是缺口，请求 `SYNC_REQUEST` 全量基线。
- `streamVersion` 是**该流上次实际发出**的桌 `state_version`，允许落后于全桌当前 `state_version`（其他席私有命令推进但该流没发），也允许在同一 `state_version` 重复（仅公开字段刷新）。`projectionSeq` 每次发出 +1，专门用于识别同版本刷新/重复。
- **推送触发**：以下任一发生就应发一次 push：
  1. 该流有 `visibleEvents`；
  2. 该流白名单内的快照字段发生语义变化（分数、牌河、副露、立直、局/场进度、暂停、控制状态、计时 deadline/opensAt/reserve 等）；
  3. 全量基线/重置。
  仅有 `serverNow` 采样变化、其他字段不变时**不单独推送**；客户端用上次 `serverNow` + 本地单调时间推算倒计时。字段和事件都没有变化时不推送。
- 无可见事件但公开快照字段变化的 push：`streamVersion` 可不变，`projectionSeq` 必须 +1，`visibleEvents=[]`；客户端按 `projectionSeq` 接受，不得因 `streamVersion` 相同而丢弃。
- 全量基线本身也必须带 `state_version/streamVersion/projectionSeq/event_seq` 高水位、`rulesetVersion/projectionVersion`、`sessionId`；客户端校验不匹配则丢弃。
- 服务端在桌 actor 内原子建立“基线 + 订阅”；基线之后的事件按顺序进入同一流，不能在两者之间漏事件。
- 不做“先发完整状态再删字段”的公共投影；公共/玩家投影在服务端分别构造。

### 8.2 座位流与观战流

- 每个座位独立流：`PlayerStream(seat)`；`syncEpoch/streamVersion` 独立。
- 观战独立公开流：不与任何座位共用游标/版本。
- 不允许“桌级单游标”或“玩家/观战共用流”；V4 已按座位/公开分离（`ServerTableHost.kt:391-397`）。
- 观战只收公开事件；玩家只收“自己的私有事件 + 公开事件”。两者事件集合不同，但同一事件在各自流中的顺序必须一致（按 `event_seq`）。

### 8.3 恢复

- 客户端重连：保留认证身份/原 requestScope/未确认 outbox；重新 `HELLO/WELCOME`（或恢复握手）后：
  1. 获取全量基线（`SYNC_REQUEST`/`RESUME`）；
  2. `RECEIPT_QUERY` 未决命令；
  3. 安装基线，重置 `streamVersion/projectionSeq`，清空旧缓冲/去重；
  4. 再允许新动作。
- 服务端 `SYNC_REQUEST` 返回全量基线，不根据客户端 `fromStreamVersion` 做增量猜测；`fromStreamVersion/fromProjectionSeq` 可用于记录/判断是否需要基线，但响应始终是全量。
- 客户端早期缓冲（V4 初值 64 条/4 MiB）在基线安装前排空；溢出直接请求全量，不丢事件后继续。
- 重连保留 `controlEpoch` 语义：托管期间已生效动作不回滚；真人 `claimControl` 后 +1 epoch，旧决策失效。

### 8.4 缺口/溢出

| 情况 | 行为 |
|---|---|
| `fromStreamVersion != current.streamVersion` **或** `fromProjectionSeq != current.projectionSeq` | 请求全量基线；不合并、不猜测 |
| `projectionSeq != current.projectionSeq + 1`（且非基线） | 发射缺口；请求全量基线 |
| `streamVersion < current.streamVersion` | 旧包，丢弃 |
| `streamVersion == current.streamVersion` 且 `projectionSeq == current.projectionSeq + 1` | 同状态刷新（公开字段变化/空事件），接受 |
| `streamVersion > current.streamVersion` 且 `fromStreamVersion == current.streamVersion` 且 `projectionSeq == current.projectionSeq + 1` | 正常状态推进（可跳跃多个 state_version，因其他席私有命令），接受 |
| `streamVersion > current.streamVersion` 但 `fromStreamVersion != current.streamVersion` | 缺口，请求全量 |
| 早期缓冲 >64 条或 >4 MiB | 标记溢出；安装基线时丢弃旧缓冲；请求全量 |
| 重排缓冲 >64 | 丢弃最旧/请求全量；不得无限增长 |
| 已应用去重 >256 | 按 FIFO 淘汰；旧重复包可重放但不能再执行副作用 |
| 服务端历史不足 | 直接全量；不返回“部分历史” |
| `sessionId/stream id` 不匹配 | 丢弃；要求重新绑定 |

### 8.5 V4 对照

- V4 `resume` 忽略 cursor、直接返回全量（`ServerTableHost.kt:1150-1158`）；V5 保持“恢复=全量”的正确性，不把旧 cursor 当增量依据。
- V4 每次更新 `cursor+1`；V5 的对应物是 `projectionSeq`（每流每次发射 +1），不是 `streamVersion`。`streamVersion` 是桌 `state_version` 水位，可能跳跃、可能重复；不要用 V4 的“每条 +1”测试套 `streamVersion`。
- V4 的 `isFullSnapshot = fromCursor >= snapshot.cursor` 是“发送端自洽”的标记；V5 要明确 `isFullSnapshot` 只由“基线/重置”置 true，不由游标/版本比较推导，避免把普通更新误标为基线。

---

## 9. 心跳、限制与背压

### 9.1 V4 值（仅对照）

- 3s PING、10s 无完整合法入站判死；PING/PONG nonce 16 hex。
- 单帧最大 1 MiB；每连接出站 256 帧/4 MiB；入站邮箱 64 帧/4 MiB；总连接 512；在途 64。
- 证据：`TcpServerConfig.kt`；`TcpOutboundQueue.kt`；`TcpWire.kt`。

### 9.2 V5 初值（待 P0.3 实测）

- 设计草案建议 10s 心跳、30s 无有效入站判死；**这不是已验收值**，P0.3 用真实网络/移动端实测后冻结。
- 单消息 1 MiB 是初始上限；不得把示例容量当实测最大值（P0.3）。
- 每连接在途/队列同时受条数与字节数限制；命令队列不可丢，推送可合并/丢弃旧快照；慢客户端停止接纳新命令并断开，用全量恢复。
- 权威 deadline 独立于网络心跳：心跳超时只影响连接/托管，不直接改变牌局 deadline；反之，牌局超时即使网络正常也裁决。

### 9.3 背压

- 命令：有界 mailbox；满了返回 `SERVER_BUSY`（受理前），不先执行再拒绝。
- 推送：有界队列；慢消费者可丢弃旧快照/合并，但不能丢弃“状态变更命令”的已提交事件；客户端缺口用全量基线恢复。
- 观战扇出：独立公开流；多观战者共享一次编码（V4 `PublicBroadcastEquivalenceTest` 的字节一致语义可借鉴），但每连接仍有自己的队列/预算。
- DB 慢：停止推进并返回 `SERVER_BUSY/RETRY_LATER`；不得先回成功再异步写失败。

---

## 10. V4 → V5 消息/字段映射

| V4 | V5 | 处置 |
|---|---|---|
| `TcpHello/TcpWelcome` (`wireVersion/businessVersion`) | `HELLO/WELCOME` (`protocolMajor/minor/ruleset/projection`) | 重写；不沿用 business 8 |
| `TcpRequestEnvelope/TcpResponseEnvelope` (`requestScope/requestId`) | `COMMAND/RECEIPT` envelope | 保留语义；字段号 P2.1 |
| `PLAYER_UPDATE` (`snapshot/fromCursor/visibleEvents/isFullSnapshot`) | `PLAYER_UPDATE` (`snapshot/fromStreamVersion/streamVersion/fromProjectionSeq/projectionSeq/visibleEvents/isFullSnapshot`) | `cursor` -> `projectionSeq`；`streamVersion` 是桌 `state_version` 水位；事件序号 `event_seq` |
| `PUBLIC_UPDATE` | `PUBLIC_UPDATE` | 独立流；公开白名单；同样带 `streamVersion/projectionSeq` |
| `ROOM_UPDATE` | `ROOM_UPDATE` | 保留 `roomVersion`；不含牌局私有 |
| `SNAPSHOT/DECISION/RESUME_CURSOR` | `PLAYER_SNAPSHOT/SYNC_REQUEST` | 查询/恢复统一为全量基线 |
| `PUBLIC_SNAPSHOT/RESUME_PUBLIC` | `PUBLIC_SNAPSHOT/SYNC_REQUEST` | 观战检查点保留 `(sessionId, streamVersion)` |
| `RECEIPT_QUERY`（不存在） | `RECEIPT_QUERY/RECEIPT_LOOKUP` | **V5 新增** |
| `PING/PONG` | `HEARTBEAT` | 保留 nonce 回显；数值重测 |
| `CLOSE` + 10 close reasons | `CLOSE` + 应用码 | 保留永久/临时分类；WSS close code P2.1 |
| `ApiErrors` 17 码 | 见 §6.2/6.3 | 保留语义，重命名/补新码 |
| `ActionReceipt(REJECTED/RECORDED/APPLIED)` | `RECEIPT` + 查询侧 `UNKNOWN` | `UNKNOWN` 仅用于客户端/传输/查询未确认；`APPLIED/RECORDED/REJECTED` 确定回执持久化 |

---

## 11. 待裁决问题

| ID | 问题 | 预冻结默认 | 责任 |
|---|---|---|---|
| P-01 | `protocolMinor` 兼容策略：是否允许“更高 minor + 未知 optional 字段” | 允许，但必须 schema 明确 optional；未知必填拒绝 | P2.1 |
| P-02 | `RECEIPT_QUERY` 保留期 | 覆盖整场 + 结算后 24h（设计初值，非实测） | P3.4/产品 |
| P-03 | `UNKNOWN` 的 UI 语义：是否允许玩家“强制重试/放弃” | 默认只展示对账，不自动重发；`UNKNOWN/NOT_FOUND/REQUEST_SCOPE_EXPIRED` 且无回执均不构成未提交证明，禁止自动或人工盲目以新 requestId 重做；只有可审计证明该具体 requestId 未准入且未提交时才允许新 requestId，否则人工/业务对账 | 产品/P3.4 |
| P-04 | V5 心跳 10s/30s 是否采用 | 设计初值；P0.3 实测后冻结 | P0.3/P2.1 |
| P-05 | `bindingEpoch` 是否上线 | 默认只在客户端本地；不上线授权 | P2.1/P3.2 |
| P-06 | `roundVersion` 是否进 Wire | 默认不进；Domain 保留；若进，标注“非同步版本” | P2.1 |
| P-07 | `streamVersion` 是否严格等于全桌当前 `state_version` | **否**：`streamVersion` = 该流最后一次实际发出投影时取的桌 `state_version`，可滞后/可重复；`projectionSeq` 负责每次发射；不得要求每流 +1，也不得用全桌当前版本校验 | P2.1/P3.1/P3.2 |
| P-16 | 无可见事件但公开快照字段变化是否推送 | **是**：该流白名单字段语义变化即推送，`streamVersion` 可不变、`projectionSeq+1`、`visibleEvents=[]`；仅 `serverNow` 变化不单独推送 | P3.2 |
| P-08 | `event_seq` 是否每房间全局连续 | 默认是；每流可见事件不要求连续 | P3.1 |
| P-09 | 未知事件类型处理 | 默认保留/可跳过，不静默当无事件；具体 P2.1 | P2.1 |
| P-10 | 错误码命名/旧码映射是否保持 V4 字符串 | 默认语义映射，新码统一；是否保留别名 P2.1 | P2.1 |
| P-11 | 认证/ticket 方案（WSS/HTTPS） | 设计草案；未实现；不要写成已有 | P5/P2.1 |
| P-12 | 单消息 1 MiB 是否足够 | 设计初值；P0.3 实测最大快照后冻结 | P0.3 |
| P-13 | 观战扇出/慢消费者策略 | 默认合并/丢旧快照，不阻塞桌；需压测 | P3.2/P0.3 |
| P-14 | `state_version` 初始 0 还是 1 | 默认 0（首命令后 1）；P2.1 固化 | P2.1/P3.1 |
| P-15 | `command_version` 是否允许与 `state_version` 不同 | 默认不允许；它就是命令后的 `state_version` | P3.1 |

---

## 12. 实际读取、验证命令与限制

### 12.1 实际读取

- `protocol/.../transport/tcp/{TcpWire,TcpFrame,TcpFrameDecoder,TcpFrameEncoder,TcpMessages,TcpCloseReasons}.kt`
- `protocol/.../transport/api/{ApiMessage,ProtocolModels,ProtocolPublicModels,ProtocolScoreCodes}.kt`
- `protocol/.../server/api/{ApiErrors,LobbyApi,SessionApi,SpectatorApi,ResumeRequest,PublicCheckpoint,RoomBinding}.kt`
- `server/.../{tcp/TcpTableServer.kt,tcp/TcpRequestRouter.kt,host/ServerTableHost.kt,idempotency/*,mapping/EngineProtocolMapper.kt,room/SharedRoom.kt,inprocess/InProcessBinding.kt}`
- `client/.../{session/PlayerStreamEngine.kt,session/SpectatorStreamEngine.kt,transport/tcp/TcpClientBinding.kt,api/ViewModels.kt,api/PublicViewModels.kt,mapping/ProtocolViewMapper.kt}`
- `doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md` §3.2.2/§5/§8；`doc/v5-重构/V5-ARCHITECTURE-DESIGN.md` §8/§9。

### 12.2 验证命令

```powershell
# 版本/常量静态核对
Select-String -Path protocol/src/main/kotlin/com/majiang/transport/tcp/TcpWire.kt -Pattern 'WIRE_VERSION|BUSINESS_VERSION|MAX_PAYLOAD_BYTES|FRAME_TYPE_'
Select-String -Path protocol/src/main/kotlin/com/majiang/server/api/ApiErrors.kt -Pattern 'PROTOCOL_VERSION|const val'
Select-String -Path protocol/src/main/kotlin/com/majiang/transport/tcp/TcpCloseReasons.kt -Pattern 'const val'

# V4 没有 RECEIPT_QUERY（应为 0 命中）
Get-ChildItem engine/src,protocol/src,server/src,client/src -Recurse -Filter *.kt |
  Select-String -Pattern 'RECEIPT_QUERY|receiptQuery|queryReceipt' -CaseSensitive

# 定向测试（实际执行；结果见 §12.3）
.\gradlew.bat :protocol:test --tests "com.majiang.transport.tcp.TcpWireTest" `
  --tests "com.majiang.transport.tcp.TcpMessagesTest" `
  --tests "com.majiang.transport.api.ApiMessageCodecTest" `
  --tests "com.majiang.transport.api.ProtocolPublicModelsCodecTest" --console=plain

.\gradlew.bat :server:test --tests "com.majiang.server.idempotency.RequestScopeReceiptStoreTest" `
  --tests "com.majiang.server.idempotency.ReceiptStoreRegressionTest" `
  --tests "com.majiang.server.inprocess.DualTransportErrorCodeConsistencyTest" `
  --tests "com.majiang.server.tcp.PublicBroadcastEquivalenceTest" --console=plain

.\gradlew.bat :client:test --tests "com.majiang.client.session.PlayerStreamEngineTest" `
  --tests "com.majiang.client.session.SpectatorStreamEngineTest" --console=plain
```

### 12.3 实际结果

| 命令 | 结果 |
|---|---|
| `:protocol:test`（6 类批次，含上述 4 类） | 6 files / **56 tests / 0 failures / 0 errors / 0 skipped**；`BUILD SUCCESSFUL` |
| `:server:test`（7 类批次，含上述 4 类） | 7 files / **43 tests / 0 failures / 0 errors / 0 skipped**；`BUILD SUCCESSFUL` |
| `:client:test`（3 类批次，含上述 2 类） | 3 files / **17 tests / 0 failures / 0 errors / 0 skipped**；`BUILD SUCCESSFUL` |
| 全量 `.\gradlew.bat test --console=plain`（本轮复核） | `BUILD SUCCESSFUL`；66 actionable tasks（本轮 66 up-to-date）；现有 XML 合计 **1,852 tests / 0 failures / 0 errors / 0 skipped**（engine 591、protocol 199、server 356、client 235、presentation 471） |

本任务定向测试合计 **226 项通过**（四模块）；全量任务本轮为 up-to-date，不是重新执行；两者都不是 V5 验收。

关键静态计数：
- `ApiErrors.ALL` = 17；`TcpWire` 帧类型 = 10；`TcpCloseReasons` = 10。
- V4 源码 `RECEIPT_QUERY|receiptQuery|queryReceipt` = **0 命中**。
- V4 `PROTOCOL_VERSION=8`、`BUSINESS_VERSION=8`、`WIRE_VERSION=1`；V5 预冻结 `protocolMajor=1`，不复用 8。

### 12.4 限制

- 本文件不分配 `.proto` 字段号；P2.1 才能冻结 tag/reserved/oneof。
- 未实现/未运行 V5 WSS、Protobuf、PostgreSQL、真实 TICK/租约；所有 V5 语义是预冻结，不是已验收。
- 未做 Unity/IL2CPP/AOT、Android/iOS、跨物理网络、证书/TLS 实测。
- 未修改 V4 生产代码/测试；未提交/推送。
- 本文件不构成 G0 通过；G0 需 P0.1/P0.2/P0.3 齐全并由 Codex 复核。
```
