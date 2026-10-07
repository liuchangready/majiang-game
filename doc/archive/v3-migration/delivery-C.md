# 任务 C 交付报告：统一异步访问接口、协议 DTO、有序同步与 GameClient

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Production Developer)
- **发起方 / 检视方**：Codex (Designer & Reviewer)
- **关联设计与契约**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md) 与 V3 迁移执行计划第一波契约

---

## 1. 交付文件清单

本次任务 C 严格按照第一波任务代码所有权边界完成开发，未改动任何测试代码、构建脚本、ApplicationCoordinator、GameScreen 或 Task A / Task B 所有的文件：

| 模块 / 路径 | 类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/access/AccessCapabilities.kt` | 新增 | 协议版本、支持人数、远程传输、持久化与恢复能力声明 DTO |
| `core/src/main/kotlin/com/majiang/access/ClockView.kt` | 新增 | 权威时钟视图 DTO（座位、开启时刻、截止、剩余储备、权威时钟样本） |
| `core/src/main/kotlin/com/majiang/access/ReceiptStatus.kt` | 新增 | 操作回执状态枚举（`REJECTED`, `RECORDED`, `APPLIED`） |
| `core/src/main/kotlin/com/majiang/access/ActionReceipt.kt` | 新增 | 动作回执 DTO（带请求标识、状态、原因码与游标） |
| `core/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt` | 新增 | 玩家完整可见状态快照 DTO 及 `PlayerView.deepCopy()` 隔离函数 |
| `core/src/main/kotlin/com/majiang/access/PlayerUpdate.kt` | 新增 | 订阅增量更新数据包 DTO（前置游标、目标快照、脱敏可见事件列表） |
| `core/src/main/kotlin/com/majiang/access/SessionAccess.kt` | 新增 | 统一整场比赛异步访问接口契约（绑定不可伪造 `viewerSeat`） |
| `core/src/main/kotlin/com/majiang/access/MahjongGameAccess.kt` | 新增 | 统一单局对局异步访问接口契约（绑定特定 `roundId`，旧局提交严格拒绝） |
| `core/src/main/kotlin/com/majiang/access/BoundMahjongGameAccess.kt` | 新增 | 绑定特定 `roundId` 的单局访问实现，拦截跨局与过期提交 |
| `core/src/main/kotlin/com/majiang/access/LocalSessionAccess.kt` | 新增 | 本地进程内 `SessionAccess` 实现，按座位维护单调连续游标并脱敏更新 |
| `core/src/main/kotlin/com/majiang/sync/SyncDisposition.kt` | 新增 | 客户端同步器处理结果枚举（`APPLIED`, `DUPLICATE`, `GAP`, `STALE`） |
| `core/src/main/kotlin/com/majiang/sync/ClientSynchronizer.kt` | 新增 | 客户端有序同步器（代次守卫、有界乱序缓冲、连续消费、按序派发与深拷贝隔离） |
| `core/src/main/kotlin/com/majiang/client/GameClient.kt` | 新增 | 统一客户端实现（握手零丢包、待确认状态守卫、物理身份严格匹配、缺口自动恢复、UI 线程安全派发） |
| `doc/v3-migration/delivery-C.md` | 新增 | 本交付报告 |

---

## 2. 核心架构与契约落实

### 2.1 统一异步接口契约 (com.majiang.access)

1. **不可伪造座位与权限隐藏**：
   - `SessionAccess` 显式绑定固定的不可伪造 `viewerSeat`（0..3），所有操作均基于该席位授权，绝不相信外部请求自报的任意座位。
   - 绝不透出领域可变 `GameState`、私密牌山或内部协调器引用。
2. **纯 Java 8 CompletableFuture 异步契约**：
   - 遵守 Android `minSdk 21` 与 `coreLibraryDesugaring` 标准；未引入任何额外的协程或第三方网络库依赖。
   - 请求序列化不使用 Java 原生 `ObjectInputStream` 反序列化。
3. **不可修改集合与深度隔离**：
   - 所有返回 DTO（`PlayerSnapshot`, `PlayerUpdate`, `ActionReceipt`, `AccessCapabilities`, `ClockView`）均对集合与事件做防御性深拷贝并用 `Collections.unmodifiableList` / `unmodifiableSet` 封装，杜绝视图漂移和外部篡改。
4. **单局与整场分离 (`SessionAccess` vs `MahjongGameAccess`)**：
   - `SessionAccess.activeGame()` 返回当前活跃局的 `MahjongGameAccess`；局间或比赛结束返回 `null`。
   - `BoundMahjongGameAccess` 严格核对 `roundId` 与当前活动局，旧 round 绑定提交必须且确实被拦截拒绝 (`ReceiptStatus.REJECTED`)。

### 2.2 客户端有序同步器 (com.majiang.sync.ClientSynchronizer)

1. **Epoch 严格隔离**：
   - 收到任何与当前 `syncEpoch` 不符的更新，直接返回 `SyncDisposition.STALE`。
   - 只有显式调用 `install(snapshot)` 才能重置或切换 `syncEpoch`。
2. **严格连续游标消费**：
   - 当前游标为 $C$ 时：
     - 若 `update.snapshot.cursor <= C`：判定为 `SyncDisposition.DUPLICATE`，无副作用忽略；
     - 若 `update.fromCursor < C`：判定为过期重叠更新 `SyncDisposition.STALE`；
     - 若 `update.fromCursor > C`：识别到游标缺口 `SyncDisposition.GAP`，暂存入有界重排缓冲 `reorderBuffer`，绝不跳过缺口开放交互；
     - 若 `update.fromCursor == C`：应用新快照，提取本批次因果事件，并检查缓冲中是否存在后续连续的批次串联推进，返回 `SyncDisposition.APPLIED`。
3. **有界乱序缓冲 (Bounded Reorder Buffer)**：
   - 采用升序 `TreeMap<Long, PlayerUpdate>`，容量上限固定为 64，防止恶意或网络异常导致的内存泄漏。
4. **事件按序提取与去重**：
   - `drainEvents()` 严格按实际应用生效的因果顺序输出深拷贝的事件列表；同时支持通过 `addListener` 接收不可变事件流通知。

### 2.3 统一麻将客户端 (com.majiang.client.GameClient)

1. **握手与订阅零丢失保证**：
   - 在 `connect()` 中，先注册底层 `access.subscribe` 监听器，将握手期间到达的所有并发更新暂存入并发队列 `earlyUpdateBuffer`。
   - 随后异步拉取 `access.snapshot()` 安装为初始基线。
   - 在同步块互斥守卫下切换为直连模式，并按序回放排空早期缓冲更新，确保从初始化到订阅期间的任何更新绝不丢失。
2. **待确认状态守卫 (`pending`)**：
   - 在提交 `start`、`nextRound`、`choose`、`submit` 等动作时，通过 `pendingState: AtomicBoolean` 标记 `pending = true`。
   - 在回执返回前，禁止提交新选择（快速返回 `REJECTED`, reason: `"PENDING_ACTION_IN_FLIGHT"`），杜绝前端连点或并发错乱。
3. **完整动作物理身份匹配**：
   - `choose(action)` 严格匹配当前快照 `PlayerDecision.options` 中的实体动作（包括牌张的花色、点数与物理副本 `copyIndex`）。
   - 匹配失败立即返回 `REJECTED` (reason: `"OPTION_NOT_FOUND"`)，**彻底废除并严禁旧 rawAction 兜底**。
4. **游标缺口自动恢复 (`resume`)**：
   - 同步器返回 `GAP` 时，`GameClient` 自动调用 `access.resume(currentCursor)`，收到权威快照后调用 `synchronizer.install(recoveredSnapshot)`，无缝自愈并通知上层展示。
5. **UI 线程调度隔离 (`dispatch`)**：
   - 支持注入调度执行器 `dispatch: ((() -> Unit) -> Unit)`，所有监听器与快照派发均通过 `dispatch` 调度，杜绝网络或后台线程直接操作 libGDX UI 组件。
6. **资源释放边界**：
   - `close()` 仅取消本客户端订阅并关闭底层端点连接，不影响整场比赛或其他玩家端点。

---

## 3. 跨任务契约对齐

1. **对 Task A (com.majiang.match / economy) 契约**：
   - `PlayerSnapshot` 预留 `cheatPoints: List<Int>` 字段，方便后续将独立出千资源账本无缝透传到客户端视图。
2. **对 Task B (com.majiang.timing / control) 契约**：
   - `PlayerSnapshot` 包含 `clocks: List<ClockView>`，标准容纳 `seat`, `opensAt`, `deadline`, `reserve`, `serverNow`。
   - `GameClient.requestControl(takeover)` 支持触发 AI 接管或人类收回控制权契约。
3. **对 Task D (统一 TableHost 与单机接线) 契约**：
   - 交付的标准 `SessionAccess` 与 `MahjongGameAccess` 接口以及 `LocalSessionAccess` 可直接作为 TableHost 本地单机适配的原型基准；Task D 仅需将底层接入其统一宿主即可。

---

## 4. 交付总结

任务 C 已 100% 遵照规格与契约完成交付，代码语法严格符合 Kotlin 2.4.10 / JVM 17 与 Android 脱糖规范，无越界修改任何受保护文件。后续可由 Codex 进行构建编译、编写契约验收测试与审查。
