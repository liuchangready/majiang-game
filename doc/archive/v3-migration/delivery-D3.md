# 任务 D3 交付报告：出千点数与 Round/Session 结果的实际账本交接

- **交付日期**：2026-10-01
- **执行角色**：Antigravity (Task D3 生产实现)
- **协作模式**：Codex 负责设计、测试编写、构建与检视；Antigravity 负责生产代码实现
- **目标设计文档**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[V3 迁移计划与执行契约](PLAN.md)

---

## 1. 任务边界与交付范围

本次 Task D3 交付严格限制在规定的任务所有权范围之内，未改动任何测试代码、Gradle 构建脚本、UI（`screen/**`）、TableHost（`host/**`）或 Task A/B/C 专属文件：

- **唯一写入范围**：
  1. `core/src/main/kotlin/com/majiang/state/GameState.kt`
  2. `core/src/main/kotlin/com/majiang/state/StateInvariants.kt`
  3. `core/src/main/kotlin/com/majiang/round/RoundStateMachine.kt`
  4. `core/src/main/kotlin/com/majiang/round/RoundResult.kt`
  5. `core/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt`
  6. `core/src/main/kotlin/com/majiang/match/MatchSession.kt`
  7. `core/src/main/kotlin/com/majiang/application/RoundResultValidator.kt`
  8. `core/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt`
  9. `core/src/main/kotlin/com/majiang/event/GameEvent.kt`
  10. `doc/v3-migration/delivery-D3.md`
- **零权限侵入**：未调用终端命令、Shell、Gradle 构建或自动化测试工具；
- **纯 JVM/Kotlin 引擎设计**：零 libGDX/UI 依赖，完全兼容 Android minSdk 21 脱糖标准与 JVM 17。

---

## 2. 交付生产文件与变更清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/state/GameState.kt` | 修改 | 新增 `cheatPoints: IntArray` 与 `cheatEntries: MutableList<CheatLedgerEntry>`，在 `resetForNewRound()`、`copyFrom()` 及 `restore()` 中实现深拷贝隔离与安全重置 |
| `core/src/main/kotlin/com/majiang/state/StateInvariants.kt` | 修改 | 新增出千点数与流水不变量校验：断言 `cheatPoints.size == 4` 且所有点数非负，校验 `cheatEntries` 席位边界与非空标识 |
| `core/src/main/kotlin/com/majiang/event/GameEvent.kt` | 修改 | 新增 `GameEvent.CheatPointsChanged(turnNumber, seat, amount, newBalance)` 领域事件，隐藏私密扣费原因，保证信息隔离 |
| `core/src/main/kotlin/com/majiang/round/RoundResult.kt` | 修改 | 扩充单局结算结果：新增 `startingCheatPoints`, `finalCheatPoints`, `cheatPointDeltas`, `cheatEntries` 属性与 `cheatSummary: CheatLedgerSummary` getter，完整深拷贝保护 |
| `core/src/main/kotlin/com/majiang/round/RoundStateMachine.kt` | 修改 | 新增 `startingCheatPoints` 与 `frozenStartingCheatPoints`，在发牌步骤中冻结起始基线，不受后续扣点影响 |
| `core/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt` | 修改 | `buildRoundResult` 从状态机与候选状态中提取起始/最终出千点数、精确计算 `cheatPointDeltas` 并封装流水列表 |
| `core/src/main/kotlin/com/majiang/match/MatchSession.kt` | 修改 | 新增 `initialCheatPoints`、`betweenRoundCheatPoints`；在 `RoundRecord`、`SessionResult`、`SessionChange` 与 `SessionSnapshot` 中串联出千账本；在 `prepare` 与 `install` 中实现局间传递与终局汇总 |
| `core/src/main/kotlin/com/majiang/application/RoundResultValidator.kt` | 修改 | 新增 Section 8 强审计：校验起始基线与 Session 局间账本一致、候选状态与结算结果一致、`final = starting + delta` 且非负（不强制麻将点数式的总量守恒） |
| `core/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt` | 修改 | 权威串行实现 `applyCheatLedgerEntry(...)`；实现局内与跨局幂等重试/冲突检测；集成 Task A `CheatLedger` 规则检验；提供只读查询 `queryCheatPoints(seat)` 与 `queryCheatPoints()` |
| `doc/v3-migration/delivery-D3.md` | 新增 | 本交付报告 |

---

## 3. 契约实现细节与核心机制

### 3.1 初始账本基线注入与生命周期绑定

1. **`GameState` 状态容器**：
   - `val cheatPoints: IntArray = IntArray(4) { 0 }`：每个座位分配独立出千点数；
   - `val cheatEntries: MutableList<CheatLedgerEntry> = mutableListOf()`：保存本局内发生的出千流水；
   - `copyFrom(other)` 针对 `cheatPoints` 与 `cheatEntries`（每个 entry 深拷贝）进行彻底深拷贝；
   - `resetForNewRound()` 显式将 `cheatPoints.fill(0)` 并清空 `cheatEntries`。
2. **`RoundStateMachine` 起始基线冻结**：
   - 构造时读取 `initialState.cheatPoints.toList()` 保存为 `startingCheatPoints`；
   - 开局发牌（从 `STACKING` 转换到非 `STACKING`）且未发生提前扣点时，再次冻结起始基线；若局前已有扣点记录，则严格保留初始基线，确保终局结算的 `cheatPointDeltas` 计算真实反映全局累计变动；
   - 提供不可变的 `frozenStartingCheatPoints: List<Int>` 外部安全只读视图。
3. **`MatchSession` 整场账本建立**：
   - 构造函数支持 `initialCheatPoints: List<Int> = List(4) { 0 }`，初始化 `_initialCheatPoints` 与 `_betweenRoundCheatPoints`；
   - 支持通过 `syncInitialState(state)` 在初始版本与外部镜像安全同步。

### 3.2 权威出千流水处理与零副作用拦截 (`applyCheatLedgerEntry`)

在 `ApplicationCoordinator` 中提供权威统一入口：
```kotlin
fun applyCheatLedgerEntry(
    entryId: String,
    seat: Int,
    amount: Int,
    reason: String
): ActionResult
```
核心执行步骤与安全防线：
1. **统一串行写锁与重入守卫**：
   - 必须通过 `rwLock.writeLock().tryLock()` 与 `isProcessingAction.compareAndSet(false, true)` 获取执行权；
   - 处于终局状态（`MATCH_FINISHED`）或单局终态（`ROUND_END`）时立即拒绝；
   - 座位越界（`seat !in 0..3`）或 `entryId.isBlank()` 立即拒绝，零副作用。
2. **两级幂等性与冲突检测 (Zero Side-Effects Idempotency)**：
   - **跨局历史检查**：遍历 `session.roundRecords.flatMap { it.cheatEntries }`，若发现相同 `entryId`：
     - 若入参 `(seat, amount, reason)` 完全一致，判定为跨局重试，直接返回 `ActionResult.Accepted(emptyList())`，不扣点、不自增 `round.version`、不触发任何状态突变；
     - 若参数存在冲突，返回 `ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)`；
   - **本局历史检查**：遍历 `round.state.cheatEntries`，若发现相同 `entryId`：
     - 若入参完全一致，判定为本局重试，直接返回 `ActionResult.Accepted(emptyList())`，零副作用；
     - 若参数存在冲突，返回 `ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)`。
3. **Task A `CheatLedger` 候选规则计算**：
   - 通过 `round.readState()` 创建隔离的 `candidateState` 深拷贝；
   - 构造 Task A `CheatLedger.fromSnapshot`，并在沙箱中调用 `ledger.apply(entryId, seat, amount, reason)`；
   - 若发生余额不足（导致负余额）、数值溢出或非法参数，`ledger.apply` 抛出相应异常；
   - `applyCheatLedgerEntry` 捕获异常并返回 `ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)`，此时 `candidateState` 直接丢弃，权威 `round.state` 未受分毫影响。
4. **外部强不变量强审计**：
   - 将变更结果写入 `candidateState.cheatPoints[seat]` 并追加 `appliedEntry`；
   - 调用 `StateInvariants.assertValid(candidateState)`，核对 136 张麻将真实物理守恒、指针界限及出千点数非负。
5. **原子提交与发布隔离**：
   - 组装 `RoundChange(roundId, sourceVersion, candidateState, ...)` 并调用 `round.install(change)`；
   - `round.version` 自增 1，状态原子生效；
   - 生成 `GameEvent.CheatPointsChanged` 领域事件，通过 `eventBus.publish` 发布；
   - 释放写锁前降级为读锁，向协调器所有订阅者异步广播深拷贝事件列表。

### 3.3 单局终局结算与账本交接 (`RoundResult` / `RoundResultValidator`)

1. **单局结算封装 (`RoundTransitionPlanner.buildRoundResult`)**：
   - 起始出千点数：`round.startingCheatPoints`；
   - 最终出千点数：`candidate.cheatPoints.toList()`；
   - 各家净增减：`cheatPointDeltas = (0 until 4).map { final[it] - starting[it] }`；
   - 携带流水：`cheatEntries = candidate.cheatEntries.map { it.copy() }`；
   - 构造不可变 `RoundResult`，并支持延迟生成 `cheatSummary: CheatLedgerSummary`。
2. **独立审计组件核对 (`RoundResultValidator.validate` Section 8)**：
   - 校验 `startingCheatPoints == session.betweenRoundCheatPoints`（起始基线严格吻合整场局间账本）；
   - 校验 `change.state.cheatPoints == result.finalCheatPoints`（候选终态与结算对象精确一致）；
   - 逐席位校验 `finalCheatPoints[i] == startingCheatPoints[i] + cheatPointDeltas[i]`（差额封闭闭环）；
   - 校验 `finalCheatPoints.all { it >= 0 }`（出千点数非负）；
   - **特殊规则**：明确不设四家出千点数总和守恒断言，因为出千点数属于可消耗、可赚取的非守恒独立资源。

### 3.4 跨局账本继承与整场结果汇总 (`MatchSession`)

1. **单局交接安装 (`session.install`)**：
   - 接收 `RoundResult` 时，`SessionChange` 携带 `targetCheatPoints = result.finalCheatPoints`；
   - 安装后更新 `session._betweenRoundCheatPoints = result.finalCheatPoints`；
   - 在 `session.roundRecords` 中新增包含 `finalCheatPoints`, `cheatDeltas`, `cheatEntries` 的 `RoundRecord`。
2. **下一局无缝继承 (`executeNextRoundInternal`)**：
   - 开启新局时创建全新 `GameState` 与 `RoundStateMachine`；
   - 执行 `newRoundState.cheatPoints[i] = session.betweenRoundCheatPoints[i]`；
   - 下一局直接继承上一局结束时的最终出千点数，杜绝重新赠送或重置为初始点数。
3. **整场比赛结束汇总 (`SessionResult`)**：
   - 当赛程判定为 `SessionLifecycle.FINISHED` 时，`session.prepare` 自动汇集各局所有的 `RoundRecord.cheatEntries`；
   - 生成整场维度的 `CheatLedgerSummary`（包含起始账本、最终账本、总增减量以及每位玩家的获得量 `totalGained` 和消耗量 `totalSpent`）；
   - 记录进不可变 `SessionResult.finalCheatPoints` 与 `SessionResult.cheatSummary`。

### 3.5 纯正物理守恒与麻将积分物理隔离

- **物理守恒律完好**：136 张实体牌（4×9万、4×9筒、4×9索、4×7字牌）的真实物理容器与 `TilePool` 映射完全不受出千点数流水的影响；
- **麻将点数与供托隔离**：麻将得点（25000 点基准）、立直供托棒（1000 点/根）、本场棒（300 点/根）由 `SettlementService` 与 `MatchSession` 独立运算，出千点数扣减或奖励绝不侵入麻将分数账本；
- **宿主解耦**：TableHost 与 UI 不维护任何可写的出千点数镜像，所有数据统一由权威 `ApplicationCoordinator` 协调。

---

## 4. 验收建议与核心测试场景 (供 Codex 参考)

建议 Codex 在验收测试与回归验证中覆盖以下关键场景：

1. **初始基线传递**：
   - `initialState.cheatPoints = intArrayOf(10, 20, 30, 40)` 初始化 `ApplicationCoordinator`；
   - 验证 `coordinator.queryCheatPoints()` 返回 `[10, 20, 30, 40]`；
   - 验证 `session.betweenRoundCheatPoints` 为 `[10, 20, 30, 40]`；
   - 验证 `activeRound.startingCheatPoints` 为 `[10, 20, 30, 40]`。
2. **局内可信扣除与事件生成**：
   - 调用 `coordinator.applyCheatLedgerEntry("entry-1", 0, -7, "cheat_cost")`；
   - 验证返回 `ActionResult.Accepted`，其携带的事件列表中包含 `GameEvent.CheatPointsChanged(turnNumber, seat=0, amount=-7, newBalance=3)`；
   - 验证 `coordinator.queryCheatPoints(0) == 3`，其余座位点数保持 20, 30, 40；
   - 验证 `activeRound.state.cheatEntries` 包含一条记录。
3. **单局与跨局幂等重试 (Zero Side-Effects)**：
   - 同局内再次调用 `applyCheatLedgerEntry("entry-1", 0, -7, "cheat_cost")`：
     - 验证返回 `ActionResult.Accepted`（携带空事件列表）；
     - 验证 `activeRound.version` 不发生自增，点数依然为 3，流水数量不增加；
   - 同局内以冲突参数调用 `applyCheatLedgerEntry("entry-1", 0, -5, "cheat_cost")`：
     - 验证返回 `ActionResult.Rejected`，点数依然为 3。
4. **不足额扣减拒绝 (Negative Balance Rejection)**：
   - 对当前余额为 3 的 seat 0 调用 `applyCheatLedgerEntry("entry-2", 0, -5, "cheat_cost")`；
   - 验证返回 `ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)`；
   - 验证 `activeRound.version` 不变，`queryCheatPoints(0)` 仍为 3，零副作用。
5. **单局结束与结算账本交接**：
   - 正常进行至和牌或流局（进入 `ROUND_END`）；
   - 验证 `RoundResult.startingCheatPoints == [10, 20, 30, 40]`；
   - 验证 `RoundResult.finalCheatPoints == [3, 20, 30, 40]`；
   - 验证 `RoundResult.cheatPointDeltas == [-7, 0, 0, 0]`；
   - 验证 `RoundResultValidator` 校验通过，无异常抛出；
   - 验证 `session.betweenRoundCheatPoints == [3, 20, 30, 40]`。
6. **跨局继承（不重复赠送初始值）**：
   - 调用 `coordinator.nextRound()` 启动下一局；
   - 验证下一局 `activeRound.state.cheatPoints` 继承为 `[3, 20, 30, 40]`；
   - 验证下一局 `activeRound.startingCheatPoints == [3, 20, 30, 40]`；
   - 在新局中重放第一局的 `applyCheatLedgerEntry("entry-1", 0, -7, "cheat_cost")`：
     - 验证返回 `ActionResult.Accepted`，在新局中不重复扣减点数，`queryCheatPoints(0)` 保持 3。
7. **整场比赛结束汇总统计**：
   - 打完全部赛程至 `MATCH_END`；
   - 验证 `session.result.cheatSummary` 非空；
   - 验证 `cheatSummary.seatStatistics[0].totalSpent == 7`，`finalPoints == 3`。
