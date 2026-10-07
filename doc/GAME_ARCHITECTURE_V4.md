# 千术麻将游戏方案 v4：菜单/牌桌拆分、client 桥接与 server 接口化

日期：2026-10-05
状态：原始目标设计，正在迁移；不考虑既有代码兼容性。
2026-10-05 后续实施以 [V4 最终契约](v4-migration/CONTRACT.md) 与 [迁移计划](v4-migration/PLAN.md) 为准：远端采用 UDP、推送为全量快照、游标按投影独立、窗口 256；下文保留原始总体设计，冲突处由最终契约覆盖。完成状态只看 [验收状态](v4-migration/STATUS.md)。
范围：表现层拆分、client/server/engine 四层职责、server 接口清单、多 client 会话与断线重连、同步模型。
产品默认值（赛制、20+3 计时、出千点数、怀疑度、码牌洗牌、击飞、掉线托管等）沿用 [V3 方案](GAME_ARCHITECTURE_V3.md) 第 1.1 节，本文不重复定义；麻将规则算法见 [规则设计](RIICHI_RULES_DESIGN.md)。

## 1. 设计原则

1. **表现层永不直接触碰 engine**：牌桌画面只认识 client 暴露的视图模型，不 import `Tile`、`Meld` 等 engine 类型；engine 改内部表示不波及 UI。
2. **单机与联机同一条代码路径**：单机 = server 跑在本进程（进程内传输），联机 = server 跑在远端（TCP）。表现层和 client 对部署方式无感知。
3. **server 是唯一权威**：所有动作、超时、AI、控制切换在一桌一个串行权威循环里裁决；client 只提交意图、渲染结果，不推进状态。
4. **契约归属清晰**：线上协议由 server 定义并拥有；客户端视图模型由 client 定义并拥有；领域模型由 engine 定义并拥有。三层模型互不共享类型，靠映射转换。

## 2. 模块划分与依赖方向

```
┌────────────────────────── presentation（libGDX，单模块） ──────────────────────────┐
│   presentation.menu（菜单画面包）        presentation.game（牌桌画面包）             │
│   主菜单/建房/加入/设置                  牌桌渲染/动画/输入/结算                     │
│   presentation.common（共享 UI 基件：字体、按钮、皮肤加载）                          │
└────────┬──────────────────────────────┬─────────────────────────────┘
         └──────────┬───────────────────┘
                    ▼  仅依赖 client 的视图模型与门面接口
              ┌───────────┐
              │  client   │  连接管理、协议编解码、快照/增量同步、
              └─────┬─────┘  断线重连、缓冲对账
                    ▼  依赖 server 的协议契约（仅 api 包）
              ┌───────────┐
              │  server   │  房间/会话/多连接、权威循环、托管、
              └─────┬─────┘  重连恢复、接口提供者（本地/远端双部署）
                    ▼  唯一驱动者
              ┌───────────┐
              │  engine   │  纯核心游戏逻辑，零外部依赖
              └───────────┘
```

依赖规则（架构测试固化）：

- Gradle 模块依赖：`presentation → client → (server 的 api 输出)`，`server → engine`，`engine → 零依赖`。
- presentation 内部按包约束（模块级编译器无法表达，用包扫描架构测试强制）：
  - `presentation.menu`、`presentation.game`、`presentation.common` 禁止 import `com.majiang.engine.*`（engine 模块包）与 `com.majiang.server.impl.*`；
  - 仅允许 import `com.majiang.client.api.*`（视图模型与门面）及 presentation 自身包；
  - `com.majiang.client` 禁止 import server 实现包与 engine 任何包，仅依赖 `com.majiang.server.api`（协议契约）。
- 组合根（应用壳 `MahjongGame` 与平台启动器）是唯一允许同时装配 client、选择 server 部署模式、切换 menu/game 画面的位置。

## 3. 各层职责

### engine —— 核心游戏逻辑（纯 Kotlin，零依赖）

- 领域模型：`Tile`、`Meld`、`PlayerHand`、牌山与物理位置。
- 规则服务：合法动作判定、役种/符/番/算分、振听、和牌分解。
- 单局状态机 `RoundStateMachine`：每次发牌新建，绑定 roundId，结束的单局不可重新激活。
- 整场 `MatchSession`：局间账本、进度、排名、击飞。
- 千术系统：出千执行器（袖里藏牌/偷天换日/透视/声东击西）、怀疑度、出千点数经济。
- 码牌洗牌：[STACKING_ALGORITHM.md](../STACKING_ALGORITHM.md) 的时空影响算法。
- 计时策略（默认 20+3）、AI 策略接口（策略实现为 engine 侧插件）。
- 对外唯一入口 `TableAuthority`（发牌、提交动作、超时推进、查询按座位投影）；不知道连接数，也不知道自己在本地还是远端。

### server —— 房间与接口提供者

- **接口提供**：发布完整协议契约（第 5 节），是对外的唯一边界。
- **房间生命周期**：`LOBBY → PREPARING(码牌) → PLAYING → SETTLED → 下一局/终局 → CLOSED`。
- **多 client 会话**：一桌 4 座位 + 观战；每个连接绑定 `(playerId, seat, 连接代次 generation)`；同座多设备登录时只有最新代次有效，旧代次挂起请求全部终结。
- **权威循环**：每桌一个串行命令队列，按序裁决玩家动作、到期超时、AI 决策、控制切换、局间交接；同一时刻只有一个胜出，其余收到拒绝回执。
- **断线处理**：掉线即按房间策略 AI 托管（或暂停）；断线期间对局继续推进，已发生的托管动作不回滚。
- **投影与脱敏**：按座位生成各自快照；对手暗牌、他人出千余额、私人账本原因永不进入他人快照；透视类千术经 engine 裁决后临时扩大该座位可见性。
- **双部署**：
  - 本地模式 `InProcessServer`：与 client 同 JVM，进程内传输，保持异步语义；
  - 远端模式 `TcpServer`：帧协议 + JSON（后续可换二进制），独立 JVM 启动。

### client —— 表现层与 server 之间的桥

- **门面**：对 menu 暴露 `LobbySession`，对 game 暴露 `GameSession`，两者共用同一条连接。
- **传输抽象**：`InProcessTransport` / `TcpTransport`，由组合根按模式选择注入。
- **协议编解码**：帧解析、消息序列化、版本协商。
- **同步**：全量快照直接替换本地权威视图副本，增量按游标应用；缺口时请求补发或触发全量刷新。
- **重连**：心跳超时/读错误检测断线 → 指数退避自动重连 → `reconnect(token)` → 对账游标续传；期间对表现层只暴露"连接中/已恢复"状态，不丢 UI。
- **视图模型映射**：协议消息映射为客户端自有类型（如 `TileView(tileId)`），表现层不接触 engine 类型。

### presentation.menu —— 菜单画面包

主菜单、单机开局（选赛制、AI 填座）、联机（建房/列房/加入/准备）、设置（皮肤/音效）。经 `LobbySession` 与 server 交互；收到开赛推送后由应用壳切换到 game 画面。

### presentation.game —— 牌桌画面包

渲染 `GameSnapshot`（手牌、牌河、副露、计时、怀疑度、出千余额）、播动画、收输入；把玩家意图包装为 `ActionChoice` 提交 `GameSession`；本地不判定合法性、不推进状态，候选以 server 下发的 `legalActions` 为准。

## 4. 数据模型与同步

| 层 | 模型 | 说明 |
|---|---|---|
| engine | `Tile`、`Meld`、`GameState`… | 领域对象，server 内映射为协议消息 |
| server 协议 | `SnapshotMsg`、`UpdateMsg`、`ActionMsg`… | 线上契约，稳定编号（tileId 0..135、suit/rank 码、错误码枚举） |
| client | `GameSnapshot`、`LegalAction`、`TileView`… | 视图模型，从协议消息映射 |

同步机制：

- 每事件携带单调递增游标 cursor（每桌一个序列）。
- 订阅先收全量快照，其后收增量；增量带 `prevCursor`，不连续即触发对账。
- server 保留有限事件窗口（默认最近 512 条 + 每局边界检查点快照），超出窗口的重连直接重放全量。
- 请求-响应与订阅推送双通道：查询走请求，对局变化走推送。

## 5. Server 接口清单

### 5.1 连接与会话

| 接口 | 说明 |
|---|---|
| `connect(clientVersion, auth?) → ConnectionInfo` | 握手；返回 playerId、sessionToken、心跳间隔、协议能力集 |
| `heartbeat() → ok` | 保活；超时未达即判掉线 |
| `disconnect()` | 主动离开 |

### 5.2 大厅 / 房间（menu 使用）

| 接口 | 说明 |
|---|---|
| `listRooms() → [RoomSummary]` | 房间列表：id、名称、座位占用、赛制、状态 |
| `createRoom(RoomConfig) → RoomId` | 建房（赛制、计时、出千配置、码牌开关） |
| `joinRoom(roomId, preferSeat?) → SeatAssignment` | 加入并分配座位 |
| `leaveRoom()` | 离开；房主离开默认中止比赛（首期不做主机迁移） |
| `getRoomInfo() → RoomInfo` | 座位、准备状态、房主、完整配置 |
| `setReady(ready)` | 准备 |
| `updateConfig(RoomConfig)` | 房主改配置（仅 LOBBY） |
| `startMatch()` | 房主开赛 → 全员收到开赛推送 |

### 5.3 对局（game 使用）

| 接口 | 说明 |
|---|---|
| `subscribe(cursor?) → Snapshot` + 推送流 | 订阅本座位视图；带 cursor 则尝试续传 |
| `submitAction(ActionChoice) → ActionReceipt` | 提交动作；回执为受理/拒绝（含原因码）/已被超时动作取代 |
| `legalActions() → [ActionCandidate]` | 当前可行动作（含千术动作，按余额与怀疑度过滤） |
| `queryRoundRecords(offset, limit)` | 历史单局记录 |
| `queryFinalResult()` | 终局排名与整场明细 |

### 5.4 控制与恢复

| 接口 | 说明 |
|---|---|
| `reconnect(sessionToken) → ResumeInfo` | 重连：校验并换发新连接代次，返回全量快照 + 事件游标；旧代次立即失效 |
| `claimControl()` | 重连后从 AI 托管收回本座控制权（不撤销托管期已提交动作） |
| `releaseControl()` | 显式交出托管 |
| `pause() / resume()` | 房主暂停/恢复（仅房间策略允许时） |

### 5.5 推送事件（server → client）

| 事件 | 说明 |
|---|---|
| `RoomEvent` | 座位变化、准备、配置变更、开赛 |
| `SnapshotEvent(cursor, snapshot)` | 全量视图 |
| `UpdateEvent(prevCursor, cursor, deltas)` | 增量：手牌/牌河/副露/计时/怀疑度/出千余额/阶段 |
| `ControlEvent` | 托管接管/收回、暂停、被顶号、被移出 |
| `MatchEvent` | 单局结束与结算明细、终局排名 |
| `ErrorEvent(code)` | 协议错误、鉴权失败、版本不兼容 |

## 6. 关键流程

**单机开局**：menu 选单机 → 组合根注入 InProcessTransport、装配本地 server → `createRoom`（3 座 AI 自动填）→ `startMatch` → 切 game 画面 → `subscribe` → 渲染。与联机完全同链路，只是 server 在本进程。

**联机开局**：menu `listRooms`/`createRoom` → `joinRoom` → `setReady` → 房主 `startMatch` → 各端收开赛推送切 game → 对局。

**断线重连**：TCP 断 → client 心跳检测 → 退避重连 → `reconnect(token)` 换新代次 → 收全量快照对账（缺增量则续传）→ `claimControl()` 收回托管 → UI 无缝恢复。

**权威裁决序**（server 每桌串行循环）：玩家动作 > 到期超时 > AI 决策 > 控制切换；同一时刻只有一个胜出，其余收到拒绝回执。

## 7. 目录结构

```
majiang/
├── engine/               纯核心逻辑（零依赖）
├── server/
│   └── src/main/kotlin/com/majiang/server/
│       ├── api/          协议契约：消息、错误码、游标语义（client 仅依赖此包）
│       ├── room/         房间、座位、生命周期
│       ├── session/      连接、代次、多 client 会话
│       ├── authority/    每桌权威循环、托管、超时调度
│       └── transport/    InProcess / TCP 双部署
├── client/
│   └── src/main/kotlin/com/majiang/client/
│       ├── api/          视图模型 + LobbySession/GameSession 门面（presentation 仅依赖此包）
│       ├── transport/    传输抽象与实现
│       ├── protocol/     编解码
│       └── sync/         快照/增量同步、重连、对账
├── presentation/
│   └── src/main/kotlin/com/majiang/presentation/
│       ├── menu/         菜单画面
│       ├── game/         牌桌画面（皮肤、渲染、动画）
│       └── common/       共享 UI 基件（字体、按钮、皮肤加载）
├── lwjgl3/  android/     启动薄壳（组合根：装配 client、选择 server 部署模式、切换画面）
└── assets/
```

菜单与牌桌先作为 presentation 模块内的两个包共存，共享 `common` 基件；包级依赖隔离由架构测试（import 扫描）强制。若后续需要更硬的编译期隔离，再升级为独立 Gradle 模块，职责边界不变。
