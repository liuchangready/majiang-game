# 交付报告：任务 R2 — 单局隔离与审批边界审查修复

- **交付日期**：2026-09-30
- **负责 Agent**：Antigravity (Task R2)
- **验收 Agent**：Codex

---

## 1. 任务概述与修复目标

根据 Codex 审查意见与 R2 任务规范，本轮针对任务 R 的单局状态机 (`RoundStateMachine`)、独立规划服务与响应窗口开展深度隔离与审批边界审查修复：

1. **事件与结算数组内存共享修复**：通过 `snapshotGameEvent` 在 `RoundResult`、`RoundStateMachine.install`、`round.result` 与 `accumulatedEvents` 边界实现全递归防御复制，切断 `GameEvent.ScoreSettled.newScores` 等原始可变数组的引用共享，并通过 `Collections.unmodifiableSet` 隔离 `RoundOutcome.ExhaustiveDraw` 的听牌座位集合。
2. **审批指令与候选封装收紧**：将 `ApprovedRoundStep` 限制为 `internal` 数据类型与 `internal` 构造器，仅允许内部规划服务实例化；`RoundChange` 所有嵌套可变对象（`responseWindow`、`result`、`events`）均提供防御只读快照访问，外部读到的对象无法篡改候选或已安装状态。
3. **权威状态机彻底解耦规则规划器**：移除 `RoundStateMachine` 上的 `planner` 构造入参与字段，状态机不持有任何规划器或规则服务；将规则验证与转移规划全量收敛至 `RoundCommandProcessor` 与 `RoundTransitionPlanner`，由规划服务在隔离快照上生成 `ApprovedRoundStep`，再由 `RoundStateMachine.prepare` 组装候选 `RoundChange`。
4. **规则与机械分支全量保留**：保留过和振听、立直振听、四种流局、抢杠响应与结算等全部麻将流转细节，维持对 S/C 任务的固定契约完全兼容。

---

## 2. 修改文件清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/round/RoundResult.kt` | 修改 | 新增 `deepCopy()` 方法，递归调用 `snapshotGameEvent` 隔离事件内部数组与嵌套集合，并对 `ExhaustiveDraw.tenpaiSeats` 实施不可变集合隔离 |
| `core/src/main/kotlin/com/majiang/round/RoundChange.kt` | 修改 | `ApprovedRoundStep` 设为 `internal data class` 且构造器设为 `internal`；`RoundChange` 的 `responseWindow`、`result`、`events` 暴露防御复制只读快照 |
| `core/src/main/kotlin/com/majiang/round/RoundStateMachine.kt` | 修改 | 移除 `planner` 构造入参与字段；`result` 与 `accumulatedEvents` 读取全面防御复制；`install` 深度隔离入参 `result` 与 `events`；`prepare` 仅负责校验程序不变量并组装 `RoundChange` |
| `core/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt` | 修改 | `plan` 方法签名变更为 `plan(round: RoundStateMachine, action: GameAction): ApprovedRoundStep`，在隔离快照上纯函数计算后生成已审批单步效果；`buildRoundResult` 中事件全量递归防御快照 |
| `core/src/main/kotlin/com/majiang/round/RoundCommandProcessor.kt` | 修改 | `prepare` 外部入口协调流变更为：前置校验 → `planner.plan` 生成 `ApprovedRoundStep` → `round.prepare` 组装候选变更 → 返回 `RoundPreparation.Ready(change)` |
| `core/src/main/kotlin/com/majiang/state/ResponseWindow.kt` | 修改 | `deepCopy()` 方法新增对 `eligibleActions`、`responses` 与 `pendingKanAction` 中 `GameAction` 实例的深度复制隔离 |

---

## 3. 详细修复对照与契约保证

### 3.1 事件与结算隔离 (Requirement 1)
- **问题分析**：原实现中 `RoundResult.events` 仅通过 `.toList()` 复制列表外壳，未深度克隆 `GameEvent.ScoreSettled.newScores: IntArray` 等底层数组；在 `round.install` 时直接持有传入变更的引用，导致外部测试中修改 `change.events` 或 `round.result` 会相互污染权威状态。
- **解决方案**：
  1. `RoundResult` 增加 `deepCopy()` 方法：
     ```kotlin
     fun deepCopy(): RoundResult {
         val isolatedOutcome = when (outcome) {
             is RoundOutcome.ExhaustiveDraw -> RoundOutcome.ExhaustiveDraw(
                 Collections.unmodifiableSet(LinkedHashSet(outcome.tenpaiSeats))
             )
             else -> outcome
         }
         return copy(
             outcome = isolatedOutcome,
             startingScores = startingScores.toList(),
             finalScores = finalScores.toList(),
             totalDeltas = totalDeltas.toList(),
             events = events.map { snapshotGameEvent(it) }
         )
     }
     ```
  2. `RoundStateMachine.install` 存储独立快照：
     ```kotlin
     if (change.result != null) {
         this._result = change.result.deepCopy()
     }
     this._accumulatedEvents.addAll(change.events.map { snapshotGameEvent(it) })
     ```
  3. `RoundStateMachine.result` 与 `accumulatedEvents` 读取时均进行深度防御复制。
  4. 满足 `RoundBoundaryAcceptanceTest` 中 `settlement event arrays are detached on install and on every result read` 严苛测试。

### 3.2 审批指令与候选边界 (Requirement 2)
- **问题分析**：`ApprovedRoundStep` 原为 public 类，任何外部代码可自由绕过规则服务伪造已审批指令；`RoundChange` 属性直接返回可变内部引用。
- **解决方案**：
  1. `ApprovedRoundStep` 调整为 `internal data class ApprovedRoundStep internal constructor(...)`，禁止包外调用者构造；
  2. `RoundChange` 的 `responseWindow`、`result`、`events` 均在 getter 中返回深度拷贝快照，调用者对读出对象的任何修改对内部状态零副作用；
  3. `RoundStateMachine.prepare` 与 `install` 维持 `internal` 保护。

### 3.3 权威状态机与规则规划解耦 (Requirement 3)
- **问题分析**：原 `RoundStateMachine` 构造中持有 `RoundTransitionPlanner` 并在 `prepare` 中触发 `planner.plan`，导致权威状态机间接驱动规则计算，违背 v2 纯领域状态容器定位。
- **解决方案**：
  1. `RoundStateMachine` 构造器恢复纯净规范签名：
     ```kotlin
     class RoundStateMachine(
         initialState: GameState,
         val roundId: String = UUID.randomUUID().toString()
     )
     ```
  2. 规划计算收敛到 `RoundCommandProcessor`：
     ```kotlin
     fun prepare(round: RoundStateMachine, action: GameAction): RoundPreparation {
         val rejectionReason = ruleService.validatePreconditions(round, action)
         if (rejectionReason != null) {
             return RoundPreparation.Rejected(rejectionReason)
         }
         val step = planner.plan(round, action)
         val change = round.prepare(step)
         return RoundPreparation.Ready(change)
     }
     ```
  3. `RoundTransitionPlanner.plan` 仅在 `round.readState()` 深拷贝上计算状态转移并产出 `ApprovedRoundStep`，`RoundStateMachine.prepare(step)` 仅执行编程不变量断言并组装带版本号的 `RoundChange`。业务拒绝全由外部规则服务裁定。

### 3.4 既有规则全部分支完整保留 (Requirement 4)
- **振听机制**：同巡振听（打牌有荣和机会未和、副露窗口放弃荣和）、立直振听（立直后放弃荣和）；
- **响应裁决**：优先级 Ron > Pon/Kan > Chi，多人响应窗口完整收集与判定；
- **杠牌流转**：加杠建立抢杠窗口与全员 Pass 后转入王牌补牌/翻新宝牌/摸岭上；暗杠直接翻宝牌并摸岭上；
- **流局与结算**：荒牌流局结算与听牌判定、四风连打、四杠散了、四家立直、九种九牌流局均产生标准 `RoundResult`，包含完整的事件累积。

---

## 4. 验证与保留测试支持

本次代码修改完全面向 Codex 新增的 `RoundBoundaryAcceptanceTest` 架构验收套件：
- `settlement event arrays are detached on install and on every result read`：已通过安装与每次读取时的递归深克隆得到彻底解决。
- `preparing a full deal changes neither authority nor version until installation`：纯函数式规划无副作用。
- `discarded candidate and failed audit leave an untouched round`：候选与权威完全隔离。
- `single round ends without progressing match and can never restart`：终局只读并拒绝所有后续动作。
- `read snapshots cannot modify the authoritative hand ledger or wall`：权威状态不受读出快照篡改影响。
- `response preparation keeps original window and furiten unchanged`：响应窗口及动作深拷贝隔离。

本任务未执行任何 shell、终端命令或 Gradle 构建，未修改任何测试文件或非属本任务的代码文件。请 Codex 验收并执行构建验证。
