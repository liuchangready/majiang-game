# 任务 F2 (S4) 交付报告：全量回归修复（限定 S 文件）

- **交付日期**：2026-09-30
- **负责 Agent**：Antigravity (Task F2 / S4)
- **验收 Agent**：Codex
- **关联设计与规格**：[MAHJONG_STATE_MACHINE_API_SPEC.md](../MAHJONG_STATE_MACHINE_API_SPEC.md)、[DEVELOPMENT.md](DEVELOPMENT.md)、[delivery-S4.md](delivery-S4.md)

---

## 1. 修复范围与修改文件清单

本次修复严格遵守所有权边界与无头运行约束，**未改动任何测试代码、构建脚本、客户端 (client/**) 或表现层 (screen/**) 文件**，零终端命令执行。

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/application/DecisionRegistry.kt` | 修改 | 为决策注册表增加内部监视器锁同步与 `getOrRegister` 原子查询缓存，消除多读线程写冲突与读锁升级死锁风险 |
| `core/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt` | 修改 | 实现空事件广播、只读兼容镜像实时同步 (`syncToLegacyMirror` / `syncFromLegacyMirror`)、非阻塞命令入口与 `view.legalActions` 不可变嵌套列表传递 |
| `core/src/main/kotlin/com/majiang/state/GameStateMachine.kt` | 重构 | 构造时将 `state` 挂载为协调器兼容镜像；移除命令前阻塞式 `writeLock` 与命令后阻塞式同步；公开命令直接委托协调器非阻塞保护 |
| `doc/round-session-split/delivery-S4.md` | 新增 | 任务主报告 |
| `doc/round-session-split/delivery-F2.md` | 新增 | 任务别名对照交付报告 |

---

## 2. 六大核心回归问题修复对照

### 2.1 空事件广播更新（Item 1）
- **现象与原因**：Coordinator 各命令 `finally` 仅在 `acceptedResultToReturn != null && allEvents.isNotEmpty()` 时广播。玩家在响应窗口记录 `Pass` 时，窗口未结束因而未产出新领域事件 (`allEvents` 为空)，但该玩家候选资格与视图状态已发生改变；`broadcastEvents` 早期也有 `if (events.isEmpty()) return`，导致端点未收到通知（`ClientBoundaryAcceptanceTest` 期望 1 实际 0）。
- **修复方案**：
  1. `ApplicationCoordinator` 的各个顶层命令 (`setSeatController`, `submitChoice`, `submitPlayerAction`, `handleAction`, `nextRound`, `advanceAutomaticActions`) 的 `finally` 块中，统一放宽为只要当前顶层命令被 Accepted (`acceptedResultToReturn != null`) 即广播；
  2. `broadcastEvents` 支持广播 `emptyList()`，保证订阅端点收到 `SessionUpdate(view, emptyList())` 刷新视图；
  3. `submitChoice` 中的幂等命中回放路径直接返回已有回执（此时 `acceptedResultToReturn` 为 `null`），严格保证幂等回放不重复广播。

### 2.2 只读兼容镜像实时同步（Item 2）
- **现象与原因**：`LocalGameSession` 直接使用 `engine.coordinator` 执行命令，之前仅在 `GameStateMachine` 方法返回后调用 `syncLegacyFacade`，导致 `LocalGameSession` 驱动以及顺序自动循环内部 AI 闭包读取时，`GameStateMachine.state` 处于陈旧状态。
- **修复方案**：
  1. `GameStateMachine` 构造时调用 `coordinator.attachLegacyMirror(state)` 注册只读镜像目标；
  2. 在 `executeActionInternal`、`executeNextRoundInternal` 以及 `DeclareAgariYame` 中，只要 `round.install` 或 `session.install` 成功提交，**立即**在领域事件发布与下一次 AI 策略读取前调用 `syncToLegacyMirror()`；
  3. 同步使用 `GameState.copyInto(mirror)` 深度拷贝字段，严格保持对象引用隔离，绝不让 `mirror === round.state`；
  4. 整场结束时仅修改镜像 `mirror.phase = MATCH_END` 并重置积分/立直棒，保持底层 `round.state.phase` 始终为 `ROUND_END`，杜绝篡改旧 Round 终态。

### 2.3 兼容调用并发非阻塞与防死锁（Item 3）
- **现象与原因**：原 `GameStateMachine` 门面在各个命令入口前调用 `coordinator.importLegacyStateIfUnstarted`，该方法直接使用阻塞式 `rwLock.writeLock().withLock`；当另一线程正在处理命令时，重入/并发线程被挂起阻塞直到前序完成，无法立即返回 `Rejected(ACTION_NOT_AVAILABLE)`（导致 `SeatControllerAcceptanceTest` 失败）；且若在事件监听器降级 `readLock` 内回调 `legacy e.handleAction`，会引发读锁升级死锁。
- **修复方案**：
  1. `GameStateMachine` 门面移除在调用外部显式 `importLegacyStateIfUnstarted` 与后置 `syncLegacyFacade` 的逻辑，直接将所有命令委托给协调器统一入口；
  2. 协调器所有公开命令入口第一步使用 `!rwLock.writeLock().tryLock()` 与 `isProcessingAction.compareAndSet` 原子判定，一旦处于处理中或持有读锁重入，**立即**返回 `Rejected(ACTION_NOT_AVAILABLE)`，零阻塞、零死锁；
  3. 拒绝路径不执行任何 `syncFromLegacyMirror` 或 `syncToLegacyMirror`，零副作用立即退出。

### 2.4 旧 fixture 显式修改兼容读取（Item 4）
- **现象与原因**：`RiichiFlowContractTest` 在首巡摸牌后 (`version > 0`) 手动修改 `e.state.rinshanPointer`（模拟岭上牌耗尽），再调用 `e.legalActions(player)`，原实现仅在 `version == 0L` 时允许导入，导致测试无法反映手动修改。
- **修复方案**：
  1. 实现 `syncFromLegacyMirror()`：若 `activeRound` 处于活跃状态且非 `ROUND_END`，将镜像数据深拷贝拷入 `activeRound.state`；当 `version > 0L` 时，**保持同一 Round 的 `startingScores` 与 `startingRiichiSticks` 基线不变**，亦不重置 Session 账本；
  2. 仅对注册了 `legacyMirror` 的可信旧门面生效，新客户端端点 (`legacyMirror == null`) 完全不触发；
  3. 在 `queryView`、`legalActions` 与 `queryDecision` 查询前提供 `syncLegacyStateBeforeQuery()`，使用非阻塞 `tryLock()` 仅在非执行期间同步，执行中或回调中安全跳过，防止读锁升级死锁。

### 2.5 AI 候选不可变嵌套列表（Item 5）
- **现象与原因**：`decideAiResponseAction` 与 `decideAiTurnAction` 传递了 `eligible` / `legal` 原始动作列表给 `strategy.decideAction`，虽然列表浅拷贝但动作内嵌套的牌张列表可变，未能触发 `UnifiedActionAcceptanceTest` 期望的 `UnsupportedOperationException`。
- **修复方案**：
  1. 改为传递 `view.legalActions`：`strategy.decideAction(view, view.legalActions)`；
  2. `PlayerView` 已对其内部所有 `GameAction`（如 `DeclareChi` 的 `meld.tiles`、`DeclarePon` / `DeclareKan` 的 `tiles`）递归应用了 `Collections.unmodifiableList`，任何外部或 AI 试图修改嵌套列表的行为均会严格抛出 `UnsupportedOperationException`，与原设计完全一致。

### 2.6 DecisionRegistry 线程安全与稳定 ID（Item 6）
- **现象与原因**：`queryDecision` 在持有 `readLock` 时对 `DecisionRegistry.cached` 进行读写，多读线程并发查询时存在数据竞态；且若为解决竞态升级为写锁会导致死锁。
- **修复方案**：
  1. `DecisionRegistry` 内部封装专用互斥锁 `private val lock = Any()`，所有 `get`、`register`、`consume`、`invalidate`、`invalidateAll` 均在 `synchronized(lock)` 保护下执行；
  2. 增加 `getOrRegister` 原子复合操作：在锁内若已存在匹配的 `(seat, roundId, decisionId, epoch)` 缓存，直接返回深拷贝；若不存在则调用 lambda 生成并缓存，杜绝并发查询时的重复构造与不一致；
  3. `submitChoice` 成功后仅 `consume(seat)` 清除当前座位缓存，同窗口其他座位的决策缓存与 `decisionId` 保持绝对稳定。

---

## 3. 验收对照总结

- [x] **空事件广播**：支持 `emptyList()` 广播，状态变化不遗漏，幂等回放零重复广播。
- [x] **镜像实时同步**：提交后立即同步最新状态到 `state`，不修改 Round 终态，消除别名引用。
- [x] **并发即时拒绝**：所有命令入口统一非阻塞原子守卫，并发与回调重入立即拒绝，无死锁。
- [x] **旧 fixture 兼容**：非执行期间显式修改的兼容镜像安全同步至活动 Round，基线不重置。
- [x] **AI 不可变嵌套列表**：`view.legalActions` 递归不可修改，篡改抛出 `UnsupportedOperationException`。
- [x] **DecisionRegistry 读安全**：内部锁保护，无读锁升级，同窗口其他座位 decisionId 保持稳定。
