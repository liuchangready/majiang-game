# 任务 S2 交付报告：首轮代码审查问题修复与架构强化

- **交付日期**：2026-09-30
- **负责 Agent**：Antigravity (Task S2)
- **验收 Agent**：Codex
- **设计依据**：[MAHJONG_STATE_MACHINE_API_SPEC.md](../MAHJONG_STATE_MACHINE_API_SPEC.md) 与首轮审查意见

---

## 1. 交付与修改文件清单

本次修复严格遵守代码归属边界，只对 Task S 负责的文件进行修改与新增，未修改任何测试用例、Gradle 构建脚本或 Task R / Task C 归属文件：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/match/MatchSession.kt` | 修改 | 修复 `ruleConfig` 传入；结果、record、pending plan 嵌套集合防御性深拷贝；`StartNextRound` 准备阶段零副作用支持 |
| `core/src/main/kotlin/com/majiang/application/ControllerRegistry.kt` | 新增 | 抽取独立控制者与控制代次 (controlEpoch) 注册表 |
| `core/src/main/kotlin/com/majiang/application/DecisionRegistry.kt` | 新增 | 抽取独立决策缓存注册表，深拷贝隔离，安全消费与失效管理 |
| `core/src/main/kotlin/com/majiang/application/RequestRegistry.kt` | 新增 | 抽取独立请求幂等注册表，深拷贝回执与结果隔离 |
| `core/src/main/kotlin/com/majiang/application/RoundResultValidator.kt` | 新增 | 抽取独立单局终局结果验证器：守恒律、初始账本匹配、差额封闭与版本校验 |
| `core/src/main/kotlin/com/majiang/application/SessionCommandValidator.kt` | 新增 | 抽取独立 Session 指令准入与生命周期验证器 |
| `core/src/main/kotlin/com/majiang/application/EventSnapshot.kt` | 新增 | 动作与结果深拷贝 (`GameAction.deepCopy()`, `ActionResult.deepCopy()`) |
| `core/src/main/kotlin/com/majiang/application/ActionDecisionDto.kt` | 修改 | 为 `PlayerDecision` 与 `ActionOption` 补齐 `deepCopy()` |
| `core/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt` | 重构 | 解决状态别名、结算视角丢失、锁降级一致性读锁、原子准备与安装、合法决策保护等全部实证缺陷 |
| `core/src/main/kotlin/com/majiang/state/GameStateMachine.kt` | 修改 | 修复 `SeatController` 导包错误，通过 `importLegacyStateIfUnstarted` 与 `syncLegacyFacade` 保持旧 fixture 兼容 |
| `doc/round-session-split/delivery-S2.md` | 新增 | 本交付报告 |

---

## 2. 十项实证问题修复对照

### 2.1 修复 1：编译报错（SeatController 包路径修正）
- **现象**：`ApplicationCoordinator.kt` 与 `GameStateMachine.kt` 误导入了不存在的 `com.majiang.model.SeatController`。
- **修复**：统一修正引用为 `com.majiang.state.SeatController`。

### 2.2 修复 2：状态别名消除与旧局彻底冻结
- **现象**：原实现直接将 `initialState` 作为 `firstRound` 的状态引用；随后在局流转和终局时对 `initialState` 就地 `copyInto` 与写入 `phase = MATCH_END`，篡改了已结束的 `RoundStateMachine` 历史状态。
- **修复**：
  1. `ApplicationCoordinator` 构造时通过 `initialState.deepCopy()` 创建 `firstRound`，完全断开对象引用别名；
  2. 协调器内部不再就地修改 `initialState`；终局时仅更新 Session 内部状态；
  3. 保留只向后兼容旧测试的 `importLegacyStateIfUnstarted(state)`，仅在初始局未执行任何动作 (`version == 0L`) 且通过 `GameStateMachine` 适配入口调用时允许导入预置配置；
  4. 新客户端端点直接与 `ApplicationCoordinator` 通信，无法覆写权威状态；
  5. 确保 `SessionBoundaryAcceptanceTest` 中关于旧局终态固定为 `ROUND_END`、分值与版本不可变、`PlayerView` 深度冻结断言 100% 通过。

### 2.3 修复 3：结算期牌桌快照保留 (Ended Round Snapshot)
- **现象**：`activeRound == null`（单局已结算、等待下一局或整场结束）时，`queryView` 原先返回空牌张、空副露、空舍牌，导致 UI 表现层在结算窗口牌桌闪烁或消失。
- **修复**：
  1. `ApplicationCoordinator` 在单局结算生成 `RoundResult` 时，捕获并保存 `lastEndedRoundSnapshot: GameState? = change.state.deepCopy()`；
  2. 在 `activeRound == null` 时，`queryView(seat)` 基于 `lastEndedRoundSnapshot` 生成底层投影，完美保留本局刚结束时的手牌、副露、舍牌、Dora 指示牌以及本局风局本场 metadata (`roundWind`, `roundNumber`, `honbaSticks`, `dealerSeat`)；
  3. 视图上层叠加 Session 的最终局间账本 (`betweenRoundScores`)、剩余供托与阶段 (`ROUND_END` 或 `MATCH_END`)；下一局的 metadata 严格留待下一局真正 `nextRound()` 启动后才生效；
  4. `activeRound` 属性保持返回 `null`，不将已结束单局误报为活动局。

### 2.4 修复 4：下一局原子规划与无副作用准备 (Zero-Pollution NextRound)
- **现象**：原实现先执行 `session.install(StartNextRound)`，再调用发牌与审计；若发牌失败或审计异常，Session 已被提前污染。
- **修复**：
  1. `executeNextRoundInternal` 严格实施两阶段提交：
     - 先构造 `newRoundState` 与全新 `RoundStateMachine` 实例；
     - 调用 `session.prepare(ApprovedSessionStep.StartNextRound(nextRound))` 生成 `sessionChange`（不安装）；
     - 调用 `roundCommandProcessor.prepare(nextRound, StartStacking())` 生成 `roundChange`（不安装）；
     - 执行 `StateInvariants.assertValid(roundChange.state)` 守恒审计；
     - 所有校验与审计全部通过后，才依次原子安装 `session.install(sessionChange)` 与 `nextRound.install(roundChange)`；
  2. 若处于 `WAITING_DEALER_CHOICE`，`MatchSession.prepare(StartNextRound)` 会在纯计算中自动应用 `pendingProgressionPlan.nextRound` 的场风庄家参数并转入 `ROUND_ACTIVE`，任何准备失败绝不写入 Session。

### 2.5 修复 5：决策时效、作用域严格匹配与合法缓存保护
- **现象**：`submitChoice` 未校验 `choice.roundId == registeredDecision.roundId`；未在锁内复核决策实时有效性；非法选择提前清除了决策缓存。
- **修复**：
  1. 严格校验 `choice.roundId == registeredDecision.roundId`：单局内决策若提交 `roundId = null` 伪装整场作用域直接拒绝；旧局 `roundId` 在连庄新局中提交直接拒绝；
  2. 在串行读写锁内执行实时资格有效性检查：
     - 单局决策：核验 `activeRound?.roundId`、阶段未结束、若为响应窗口核验 `window.id` 与 `!window.hasResponded(seat)`、若为自家回合核验 `currentPlayer == seat` 与版本号；
     - 整场决策：核验 `session.lifecycle == WAITING_DEALER_CHOICE`、`seat == currentDealer` 与 Session 版本；
  3. **合法缓存保护**：任何因参数不匹配、代次过期、作用域不符、内部规则拒绝的请求，**绝不清除** `decisionRegistry` 中的有效决策缓存，允许玩家继续提交正确选择；
  4. 成功执行后仅调用 `decisionRegistry.consume(seat)` 清除本座位的决策，保留同窗口其他座位的合法决策。

### 2.6 修复 6：可变引用泄漏彻底消除 (Deep Defensive Copy)
- **现象**：直接返回 `existing` 决策对象与 `receipt.result`；事件与动作中的嵌套数组、集合存在浅拷贝泄漏风险。
- **修复**：
  1. 实现了 `PlayerDecision.deepCopy()`、`ActionOption.deepCopy()`、`GameAction.deepCopy()`、`ActionResult.deepCopy()`；
  2. `DecisionRegistry` 与 `RequestRegistry` 在写入与读取时一律进行深拷贝；
  3. `RoundResult` 与 `MatchSession` 内部的所有列表、`payments`、`newScores` 均使用不可变包装或 `.clone()` 切断引用。

### 2.7 修复 7：统一读写串行边界与锁降级广播 (Consistency & Lock Downgrade)
- **现象**：仅用 CAS 保护写操作，读操作 (`queryView`, `queryDecision`, `legalActions`, `controllerFor`) 可能在并发时观察到半提交状态。
- **修复**：
  1. 引入统一 `ReentrantReadWriteLock`；
  2. 读操作全量在 `rwLock.readLock().withLock` 保护下执行，保证强一致性快照；
  3. 写操作使用 `currentExecutingThread` 与 `rwLock.writeLock().tryLock()` 严格串行化：
     - 若同线程重入或跨线程并发冲突，安全返回 `ActionResult.Rejected(ACTION_NOT_AVAILABLE)`，杜绝死锁；
  4. **锁降级发布**：顶层写操作提交完全成功后，先获取 `readLock` 再释放 `writeLock` 进行事件广播。广播期间依然维持 `isProcessingAction = true` 拒绝重入写入，但允许外部读线程安全并发查询 100% 已提交状态。

### 2.8 修复 8：抽出独立外部注册与验证组件
- **现象**：`ApplicationCoordinator` 原先堆积了验证、守恒、注册、调度近千行代码。
- **修复**：
  1. `RoundResultValidator`：独立校验 `roundId`、未消费 `resultId`、`sourceVersion`、初始账本匹配、`final = start + delta`、全场点数 + 供托守恒（100,000 点物理闭环）；
  2. `SessionCommandValidator`：独立校验 Session 准入生命周期与前置条件；
  3. `RequestRegistry`：独立管理 `requestId` 幂等回放；
  4. `DecisionRegistry`：独立管理座位决策缓存与失效；
  5. `ControllerRegistry`：独立管理座位控制者与代次递增；
  6. `MatchSession` 保持纯粹领域容器，不调用任何验证器。

### 2.9 修复 9：事件发布与订阅隔离
- **现象**：广播给各监听器的事件列表与回执事件共享引用，监听器异常可能阻断后续发布。
- **修复**：
  1. 广播时对每个监听器独立分发 `Collections.unmodifiableList(events.map { snapshotGameEvent(it) })`；
  2. 单个监听器抛出异常被独立 `try-catch` 捕获隔离，不影响其他端点与协调器状态；
  3. `eventBus.publish` 发布独立的事件快照。

### 2.10 修复 10：MatchSession 集合防御性复制与 ruleConfig 对齐
- **现象**：`MatchSession` 的 `ruleConfig` 忽略了传入的 `matchProgression.config`；`roundRecords` 与 `pendingProgressionPlan` 返回内部可变引用。
- **修复**：
  1. `ApplicationCoordinator` 构造 `MatchSession` 时显式传入 `ruleConfig = matchProgression.config`；
  2. `MatchSession` 内部对 `betweenRoundScores`、`_roundRecords`、`pendingProgressionPlan`、`result` 全量实施防御性复制与只读封装；
  3. `install` 与 `readState` 均使用不可变切片复制。

---

## 3. 验收标准与契约完全对齐

- [x] `ApplicationCoordinator` 与 `GameStateMachine` 编译通过，无任何缺失包路径引用；
- [x] `RoundStateMachine` 实例完全隔离，旧 Round 在新局启动与尾庄和止时不被修改；
- [x] 单局结算结果只交接一次，供托与立直扣点严格守恒；
- [x] 局间与终局状态下 `queryView` 完整展现牌桌手牌、副露与本局 metadata；
- [x] 新局发牌与尾庄继续选项准备阶段无副作用，失败零污染；
- [x] `submitChoice` 严格匹配作用域与实时资格，合法决策缓存不被非法请求清除；
- [x] 多线程查询与并发写入一致性保证，无死锁与半提交；
- [x] 独立验证组件与纯净 Session 容器架构边界清晰；
- [x] 广播事件与回执完全深度隔离。
