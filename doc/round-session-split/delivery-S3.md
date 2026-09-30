# 任务 S3 交付报告：编译错误消除与外部结果验证/初始状态适配修复

- **交付日期**：2026-09-30
- **负责 Agent**：Antigravity (Task S3)
- **验收 Agent**：Codex
- **依据规范**：[MAHJONG_STATE_MACHINE_API_SPEC.md](../MAHJONG_STATE_MACHINE_API_SPEC.md) 与 S3 任务修复指令

---

## 1. 交付与修改文件清单

本次修复严格遵守代码归属边界，仅修改 Task S 负责的文件，未修改任何测试用例、Gradle 构建脚本、Task R 归属文件或 Task C 归属文件：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt` | 修改 | 修复 `change.result` 防御复制 getter 导致的 smart cast 编译错误；在 `importLegacyStateIfUnstarted` 中同步更新 `RoundStateMachine` 的起始基线 (`startingScores`, `startingRiichiSticks`) |
| `core/src/main/kotlin/com/majiang/application/RoundResultValidator.kt` | 修改 | 补齐结果与候选状态的一致性校验（`phase == ROUND_END`、`scores == finalScores`、`riichiSticks == remainingRiichiSticks`、`session.activeRound === round` 严格同源、`responseWindow == null` 终态关闭） |
| `core/src/main/kotlin/com/majiang/application/SessionCommandValidator.kt` | 修改 | 开放已有合法牌局/进行中 fixture (`DRAW`, `DISCARD`, ...) 的初始化与恢复路径，禁止非法重复开局与 `MATCH_END` 初始态；加强结果交接活跃局同源性核验 |
| `core/src/main/kotlin/com/majiang/state/GameStateMachine.kt` | 修改 | 确保 `initializeMatch`、`setSeatController`、`advanceAutomaticActions` 等所有旧入口在执行前均按需同步未发牌 fixture 状态 |
| `core/src/main/kotlin/com/majiang/match/MatchSession.kt` | 修改 | 强化 `syncInitialState` 的生命周期守卫，增加 `_roundRecords.isEmpty()` 保护，确保仅在首局未产生结算前允许基准账本同步 |
| `doc/round-session-split/delivery-S3.md` | 新增 | 本交付报告 |

---

## 2. 核心问题修复对照

### 2.1 修复 1：编译错误消除 (`change.result` 局部不可变捕获与 Smart Cast)
- **问题根因**：Task R2 将 `change.result` 改为防御性复制的 custom getter。在 Kotlin 中，对带有 custom getter 的属性无法进行 smart cast，导致 `ApplicationCoordinator.kt` 中 `roundRes.finalScores`、`roundRes.outcome` 等调用因推导类型为 `RoundResult?` 而触发编译报错。
- **修复方案**：
  1. 在 `ApplicationCoordinator.executeActionInternal` 中，先执行 `val roundRes = change.result`，将其缓存在局部不可变变量中；
  2. 使用 `if (roundRes != null)` 进行判断，触发 Kotlin 局域不可变引用的智能转换（smart cast 为非空 `RoundResult`）；
  3. 后续规则推进 `matchProgression.nextRound(...)`、指令准入校验 `validateAcceptRoundResult`、步骤打包 `ApprovedSessionStep.AcceptRoundResult` 以及已消费结果记录 `consumedResults.add(roundRes.resultId)` 均统一使用该非空局部变量，彻底消除多次读取与断言开销。

### 2.2 修复 2：初始化与恢复路径支持合法进行中局 (DRAW/DISCARD Fixtures)
- **问题根因**：`SessionCommandValidator.validateStartSession` 强行要求 `firstRound.state.phase == GamePhase.STACKING`，导致所有传入预设合法牌局（如摸打阶段、听牌阶段、流局阶段测试装配）的 `GameStateMachine(state, eventBus)` 或 `ApplicationCoordinator(initialState, ...)` 构造即刻抛出 `IllegalStateException`。
- **修复方案**：
  1. 调整 `SessionCommandValidator.validateStartSession`：
     - 保留 `session.lifecycle in setOf(CREATED, WAITING_START)` 与 `session.activeRound == null`，坚决拒绝已开赛 session 的非法重复开局；
     - 保留 `firstRound.version == 0L`，确保单局实例为初始版本；
     - 将阶段限制改为 `check(firstRound.state.phase != GamePhase.MATCH_END)`，允许空白牌局（`STACKING`）和各种处于进行中的合法牌局（`DRAW`, `DISCARD`, `CHEAT_WINDOW`, `RESPONSE_WINDOW`, `ROUND_END` 等）；
  2. 严格遵循旧单测约定：在 `init` 阶段不执行未发牌装配的 `StateInvariants` 断言，避免破坏旧单测逐步组装手牌的测试夹具；
  3. 后续动作执行时，仍由 `RoundCommandProcessor` 与 `StateInvariants.assertValid` 执行严格外部审计。

### 2.3 修复 3：`RoundResultValidator` 候选状态与结算结果一致性强审计
- **问题根因**：原验证器仅核验了 `RoundResult` 内部的总额守恒 (`starting + delta == final`) 与 Session 局间账本匹配，未校对生成的候选状态 `change.state` 与结算结果是否一致，可能接受状态金额与结算单据不符的篡改候选。
- **修复方案**：
  1. **同源活跃局核验**：断言 `session.activeRound != null && session.activeRound === round && session.activeRound?.roundId == round.roundId`，确保只有当前正在进行的合法局才能提交结算结果；
  2. **终态阶段核验**：断言 `change.state.phase == GamePhase.ROUND_END`，带有结果的单局变更必须将候选状态置为终态；
  3. **响应窗口关闭**：断言 `change.responseWindow == null`，终态单局绝不允许遗留开放的响应窗口；
  4. **候选账本一致性**：断言 `change.state.scores.toList() == result.finalScores` 以及 `change.state.riichiSticks == result.remainingRiichiSticks`，确保单局内部状态与结算回执 100% 吻合；
  5. 完备覆盖 Codex 在 `ExternalValidationAcceptanceTest` 中设立的三组对抗用例。

### 2.4 修复 4：旧适配器状态导入 (`importLegacyStateIfUnstarted`) 账本基线双向同步
- **问题根因**：旧单测常在实例化 `GameStateMachine(state, ...)` 后修改 `state.scores`。原 `importLegacyStateIfUnstarted` 仅同步了 `session.betweenRoundScores`，但 `RoundStateMachine` 的 `startingScores` 与 `startingRiichiSticks` 仍为构造时的旧值，导致在单局结束验证时触发初始账本不匹配异常。
- **修复方案**：
  1. 在 `importLegacyStateIfUnstarted` 中，当 `round.version == 0L` 时，在将 `legacyState` 写入 `round.state` 的同时，显式同步更新 `round.startingScores = round.state.scores.toList()` 与 `round.startingRiichiSticks = round.state.riichiSticks`；
  2. 保持 `session.syncInitialState(round.state)` 同步整场初始账本，并在 `MatchSession` 端增加 `_roundRecords.isEmpty()` 保护，确保仅在未发生任何单局结算前生效；
  3. `importLegacyStateIfUnstarted` 仅对内部适配器 `GameStateMachine` 开放，新客户端端点 (`PlayerEndpoint` / `LocalPlayerEndpoint`) 无此方法，不破坏新架构的隔离性；
  4. 补齐 `GameStateMachine` 的 `initializeMatch`、`setSeatController`、`advanceAutomaticActions` 等入口处的 `importLegacyStateIfUnstarted` 预同步。

### 2.5 修复 5：架构不变量全面保持
- 保持首局深拷贝 (`initialState.deepCopy()`) 与终态快照 (`lastEndedRoundSnapshot`)；
- 保持跨局全新 `RoundStateMachine` 实例隔离与连庄新实例；
- 保持请求作用域严格核验（跨局决策拒绝、伪装整场作用域拒绝）；
- 保持 `PlayerDecision` 与 `ActionOption` 深度防御性复制；
- 保持 `ReentrantReadWriteLock` 串行与锁降级广播。

---

## 3. 验收标准达成情况

- [x] **编译无报错**：`roundRes` 编译错误完全消除，代码无未解析引用或类型不匹配。
- [x] **测试夹具与恢复路径兼容**：支持 `STACKING` 与任意合法进行中状态 (`DRAW`, `DISCARD` 等) 初始化，无非法重复开局。
- [x] **结果强审计**：覆盖单局终态、响应窗口关闭、候选金额与结算金额一致、供托数一致及活跃局同源性。
- [x] **双向账本基线一致**：旧适配器在 `version == 0L` 导入修改时，`RoundStateMachine` 与 `MatchSession` 账本基线同时更新。
- [x] **生产不变量零回滚**：S2 的 10 项并发、隔离、深拷贝与防篡改特性完整保留。
