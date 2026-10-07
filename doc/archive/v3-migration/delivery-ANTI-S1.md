# ANTI-S1 任务交付报告：权威恢复投影与赛程/控制状态无损同步

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity（生产开发，对接 Codex 验收）
- **任务编号**：ANTI-S1（完整实现 [TASK-GLM-S.md](TASK-GLM-S.md)）
- **验收方**：Codex（测试设计、编译构建与端到端回归验收）

---

## 1. 所有权与写入范围说明

根据任务说明，本次交付严格限定在以下指定责任范围，未修改任何测试套件、Gradle 构建配置、UI/Core 模块、`GameClient`、`sync` 状态同步机或网络传输/服务器模块：

| 变更文件 | 所属范围 | 变更说明 |
|---|---|---|
| `engine/src/main/kotlin/com/majiang/access/PublicRoundRecord.kt` | `engine/access/**` | 新增单局公开结算记录 DTO，提供完全防御性隔离与深拷贝，杜绝泄露暗牌与出千细节 |
| `engine/src/main/kotlin/com/majiang/access/PublicSessionResult.kt` | `engine/access/**` | 新增整场终局结果 DTO，`rankedSeats` 严格遵循领域权威排序 (1st..4th) |
| `engine/src/main/kotlin/com/majiang/access/SessionView.kt` | `engine/access/**` | 新增整场赛程只读视图 DTO（生命周期、各家可见分、场风局号、本场棒、历史记录与终局结果） |
| `engine/src/main/kotlin/com/majiang/access/SeatControlView.kt` | `engine/access/**` | 新增席位连接与控制视图 DTO，`role` 严格限制为 `HUMAN/COMPUTER/TAKEOVER`，`controlEpoch` 绑定 Coordinator 决策代次 |
| `engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt` | `engine/access/**` | 扩展 5 个带默认值的兼容字段 (`session`, `controls`, `paused`, `settlementRoundId`, `settlementEvents`)，并实现事件与集合的深度防御性隔离 |
| `engine/src/main/kotlin/com/majiang/access/SessionAccess.kt` | `engine/access/**` | 新增 `querySession()`、`queryRoundRecords(offset, limit)`、`queryFinalResult()` 默认推导方法 |
| `engine/src/main/kotlin/com/majiang/host/TableHost.kt` | `engine/host/**` | 落地赛程视图投影、席位控制与代次绑定、时钟暂停时间戳稳定化、终局结算公开事件过滤与缓存生命周期、游标更新节流控制 |
| `client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt` | `client/network/protocol/**` | 为新增 DTO 补充显式严格 TypeAdapter，更新 PlayerSnapshot 编解码，确保役种计分明细 (`ScoreBreakdown`) 无损传输 |
| `doc/v3-migration/delivery-ANTI-S1.md` | `doc/v3-migration/**` | 本任务交付报告 |

---

## 2. 缺陷根因与恢复投影架构 (Architecture & Rationale)

### 2.1 架构缺陷
原 V3 契约中，`PlayerSnapshot` 仅携带当前局的即时手牌/牌河 (`PlayerView`)、当前待决决策 (`PlayerDecision`) 和时钟 (`ClockView`)：
1. **结算期重连断层**：当客户端在单局结束/结算展示（`ROUND_END` / `WAITING_READY`）或整场终局（`FINISHED`）期间断线重连，仅凭手牌投影无法获知刚结束单局的役种番数/符数/计分明细（`ScoreBreakdown`），更无法获知整场排位与终局原因；
2. **外部接管无感知**：掉线触发 AI 接管（Takeover）或重连后，客户端 UI 无法从快照中获知席位当前的控制者角色及权威控制代次（`controlEpoch`）；
3. **暂停倒计时缩水**：牌桌时钟暂停期间，若新客户端拉取快照，其接收到的 `serverNow` 若采用当前物理时间，客户端根据 `deadline - serverNow` 计算将误判玩家思考时间已被消耗。

### 2.2 恢复投影架构设计
遵循 `TASK-GLM-S.md` 约束，**不增加任何新的服务器请求类型**，沿用同一条单座只读快照推送链路（Snapshot Wire Path）：
- 在 `PlayerSnapshot` 中扩充权威赛程 (`session`)、席位控制 (`controls`)、暂停状态 (`paused`) 以及结算恢复事件 (`settlementEvents`)；
- 所有字段具备默认值，完全向下兼容已有的内存测试夹具；
- 在单局进行中，`settlementRoundId` 与 `settlementEvents` 保持为 `null` / 空，零带宽冗余；在结算期与终局期，权威投递由白名单过滤后的公开结算事件。

---

## 3. 具体实现细节

### 3.1 领域访问 DTO 设计 (`engine/src/main/kotlin/com/majiang/access/`)

1. **[`PublicRoundRecord`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/access/PublicRoundRecord.kt)**:
   - 属性：`roundId: String`, `resultId: String`, `finalScores: List<Int>`, `deltas: List<Int>`, `finalCheatPoints: List<Int>`, `cheatDeltas: List<Int>`；
   - 校验：`roundId` 与 `resultId` 禁止空白；
   - 隔离：`deepCopy()` 对所有整数列表使用 `Collections.unmodifiableList(ArrayList(...))` 防御性复制；绝不暴露暗手牌或出千账本的流水明细原因。

2. **[`PublicSessionResult`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/access/PublicSessionResult.kt)**:
   - 属性：`finalScores: List<Int>`, `rankedSeats: List<Int>`, `reason: String`, `finalCheatPoints: List<Int> = emptyList()`；
   - 契约：`rankedSeats` 由领域层权威计算排序（基于终场得分与 tie-break 规则从第一名到第四名），客户端无需且禁止自行重算排名。

3. **[`SessionView`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/access/SessionView.kt)**:
   - 属性：`lifecycle: String`, `scores: List<Int>`, `roundWind: Int`, `roundNumber: Int`, `dealerSeat: Int`, `honba: Int`, `roundRecords: List<PublicRoundRecord> = emptyList()`, `finalResult: PublicSessionResult? = null`；
   - 提供全场生命周期与公开单局历史记录的快照投影。

4. **[`SeatControlView`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/access/SeatControlView.kt)**:
   - 属性：`seat: Int`, `connected: Boolean`, `role: String`, `controlEpoch: Long`；
   - 严格约束：`role in setOf("HUMAN", "COMPUTER", "TAKEOVER")`，`controlEpoch >= 0L`，严格与 Coordinator 决策令牌代次一致。

5. **[`PlayerSnapshot`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt)**:
   - 保持前置属性位置不变，末尾追加：
     ```kotlin
     val session: SessionView? = null,
     val controls: List<SeatControlView> = emptyList(),
     val paused: Boolean = false,
     val settlementRoundId: String? = null,
     val settlementEvents: List<GameEvent> = emptyList()
     ```
   - `deepCopy()` 实现对 `session`、`controls` 的深度拷贝，并使用 `snapshotGameEvent(it)` 对 `settlementEvents` 中的多态领域事件进行无损独立快照克隆。

6. **[`SessionAccess`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/access/SessionAccess.kt)**:
   - 接口新增默认方法（Local / Remote 统一实现）：
     - `querySession(): CompletableFuture<SessionView>`：从 `snapshot()` 派生，若无 session 则抛出 `IllegalStateException` 异常；
     - `queryRoundRecords(offset: Int = 0, limit: Int = 50): CompletableFuture<List<PublicRoundRecord>>`：校验 `offset >= 0` 与 `limit in 1..100`，安全切片并返回不可变列表；
     - `queryFinalResult(): CompletableFuture<PublicSessionResult?>`：从 `querySession()` 提取并返回深拷贝。

---

### 3.2 TableHost 宿主恢复与控制整合 (`engine/src/main/kotlin/com/majiang/host/TableHost.kt`)

1. **赛程与控制投影构建 (`buildSessionView`, `buildSeatControlViews`)**:
   - `buildSessionView()` 读取 `coordinator.session` 与活跃 Round 分数，历史记录无缝映射为 `PublicRoundRecord`，终局结果直接提取领域排位 `res.rankings.map { it.seat }`；
   - `buildSeatControlViews()` 遍历 4 个座位，从 `connectionControl.seatState(s)` 获取连接状态与角色名称，从 `coordinator.queryDecision(s).controlEpoch` 读取权威代次，保证客户端决策令牌与控制视图完全契合。

2. **时钟暂停时间戳稳定化 (`pausedAuthorityTimeMillis`)**:
   - 当牌桌调用 `pause()` 时，记录暂停瞬间的单调时钟；调用 `resume()` 时清空；
   - 在 `buildClockViews()` 中，若当前牌桌处于暂停状态，返回给客户端的 `ClockView.serverNow` 锁定为 `pausedAuthorityTimeMillis`（而非物理流逝时间）。客户端据此计算 `deadline - serverNow` 时，剩余思考时间完全静止，不会在暂停期间显示倒计时耗尽。

3. **终局结算公开事件过滤与缓存生命周期**:
   - 定义结算公开事件白名单过滤器 `isSettlementEvent(event: GameEvent)`：仅允许 `TsumoWin`、`RonWin`、`DrawGame`、`ScoreSettled`、`RoundEnded`、`MatchEnded`、`DoraRevealed (仅限 isUraDora)` 以及 `CheatPointsChanged` 通过；
   - **绝对物理保密性**：结算事件严禁包含任何摸牌 (`TileDrawn`) 或私密发牌 (`TileDealt`)；
   - 过滤后的事件再经过既有的 `SessionUpdate.filterVisibleEvents(..., seat)` 按座位过滤，确保完全符合麻将可见性规范；
   - **缓存生命周期**：在单局结束时捕获到 `lastCompletedSettlementEvents` 和 `lastCompletedRoundId`，供结算期重连客户端拉取；当下一单局发牌开始（`phase != GamePhase.ROUND_END`）时，立即清空结算缓存，恢复纯手牌快照。

4. **游标节流与状态变化判定 (`onStateChanged`)**:
   - 在判定是否有可见变化时，加入对 `controls`、`paused`、`session`、`settlementRoundId` 与 `settlementEvents` 变更的检测；
   - 过滤纯 `serverNow` 的微小时间步进（`hasMeaningfulClockDifference` 忽略时钟自然流逝），避免游标无意义递增造成网络与客户端解析抖动。

---

### 3.3 协议序列化与严格解码 (`client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt`)

1. **新增显式 TypeAdapter**:
   - `PublicRoundRecordAdapter`：严格编解码 6 个必填字段；
   - `PublicSessionResultAdapter`：严格编解码比分、领域排名和原因；
   - `SessionViewAdapter`：包含嵌套数组与可选对象的严格校验；
   - `SeatControlViewAdapter`：校验 `role in SeatControlView.VALID_ROLES`，拒绝非法身份字面量。

2. **`PlayerSnapshotAdapter` 扩展与兼容性**:
   - `write()` 完整输出 `session`, `controls`, `paused`, `settlementRoundId`, `settlementEvents`；
   - `read()` 保持对未知字段的 `rejectUnknown` 严格拒绝；对于未包含新字段的历史快照，安全提供非 null 默认值（`emptyList()` / `false`），确保老版本测试夹具与离线持久化数据无缝解析；
   - 结算事件通过内嵌的 `GameEventAdapter` 递归解码，包含全部 `ScoreBreakdown`（番数、符数、役种明细列表 `YakuDetail`、宝牌拆解 `DoraBreakdown` 与高位点数标记），保证计分信息百分之百无损往返传输。

3. **编译器安全保持**:
   - 完整保留 R1 修复的 `enumValues<T>()` 泛型枚举反序列化，确保所有标准枚举解析安全合规。

---

## 4. 验证建议与验收测试矩阵 (Verification Guide for Codex)

因 Antigravity 在当前无头环境下不具备命令执行权限，所有修改均已通过文件层面的细致语法核对、类型系统审计与静态契约对比。建议 Codex 运行以下指令进行编译构建与测试验证：

### 4.1 核心命令
```bash
# 1. 验证引擎层单元与集成测试（包含 SessionAccess 与 TableHost 恢复投影）
./gradlew :engine:test --info

# 2. 验证客户端协议层严格序列化与反序列化
./gradlew :client:test --info

# 3. 运行全项目无头测试
./gradlew test --info
```

### 4.2 重点验收场景建议
1. **结算期重连与计分明细校验**:
   - 模拟单局通过 `RonWin` 或 `TsumoWin` 结束，进入 `WAITING_READY` 阶段；
   - 某席位调用 `host.querySnapshot(seat)` 或全新 `host.connect(seat).snapshot()`；
   - 断言：`snapshot.settlementRoundId` 与刚结束单局一致，`snapshot.settlementEvents` 包含 `ScoreSettled` 与对应和牌事件，且其中的 `ScoreBreakdown` 完整保留役种名称与番数，且不包含任何对手暗手牌事件。
2. **席位接管与控制代次对齐**:
   - 模拟玩家掉线或主动触发 `requestControl(seat, takeover = true)`；
   - 断言：`snapshot.controls` 中对应座位的 `role` 变为 `"TAKEOVER"`，且 `controlEpoch` 与 `snapshot.decision.controlEpoch` 严格相等；桌流发布更新游标递增 1。
3. **时钟暂停剩余思考时间不耗尽**:
   - 触发决策时钟开启，调用 `host.pause()`，等待一定时间（如 2000ms）；
   - 调用 `host.querySnapshot(seat)`；
   - 断言：`snapshot.paused == true`，`snapshot.clocks[seat].serverNow` 对应暂停时间戳，客户端计算 `deadline - serverNow` 与暂停前一致。
4. **下一局发牌结算投影自动清理**:
   - 在全员 Ready 后调用 `coordinator.nextRound()` 并完成新单局发牌；
   - 断言：`snapshot.settlementRoundId == null`，`snapshot.settlementEvents.isEmpty() == true`。
5. **协议 JSON 往返与严格拦截**:
   - 针对包含新字段的 `PlayerSnapshot` 执行 `ProtocolJson.toJson` 与 `ProtocolJson.fromJson`；
   - 验证往返深等价；
   - 传入未知字段或非法 `role`（如 `"ROBOT"`），断言解析抛出 `JsonParseException`。
