# 交付报告：任务 R — 单局状态机与独立单局服务

- **交付日期**：2026-09-30
- **负责 Agent**：Antigravity (Task R)
- **验收 Agent**：Codex

---

## 1. 任务概述与职责边界

本任务严格依照《单局状态机、整场 Session 与客户端：分类开发设计》规范，从 `build/round-session-split/GameStateMachine.before.kt` 剥离本局麻将规则流转逻辑，实现纯净的单局状态机与独立的单局规则与规划服务。

### 职责履行清单
1. **单局实例隔离**：`RoundStateMachine` 仅保存并应用单局状态转移，终态固定为 `ROUND_END`。每次发牌使用独立新实例与新 `roundId`。
2. **纯粹单局流转**：移出逐座位控制者、AI 策略决策、真人/AI 身份分支判定、整场推进 (`MatchProgression`)、和止 (`DeclareAgariYame`) 与事件总线外部发布。
3. **零副作用与候选隔离**：所有状态变更均在 `candidate = round.readState()` 深拷贝副本上试探规划；`round.prepare` 绝不修改权威状态。外部验证或审计失败时不调用 `install`，状态机零痕迹。
4. **统一摸牌阶段**：摸牌后统一使用 `GamePhase.CHEAT_WINDOW`，具体座位的人机身份完全由外部协调器负责，内部不再决定人机阶段差别。
5. **机械后续全覆盖**：出牌后自动构建响应窗口；若无人可响应自动推进至下家摸牌（或牌山耗尽荒牌流局、途中流局）；加杠后建立抢杠窗口，无人抢杠自动补入王牌、翻新宝牌并摸岭上牌。
6. **物理守恒与积分结算**：局末产生 `RoundResult`，`totalDeltas` 从本局起始积分精确计算（涵盖局中立直扣点与供托棒收取/留存），累计包含本局全部事件。
7. **防御性复制**：`readState()` 提供深复制；`responseWindow` 与 `result` 读取时均进行防御复制；`ResponseWindow` 扩展了 `deepCopy()`。

---

## 2. 交付与修改文件清单

| 文件路径 | 类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/state/ResponseWindow.kt` | 修改 | 新增 `deepCopy()` 方法，支持 `responses` 与 `eligibleActions` 的完整深拷贝 |
| `core/src/main/kotlin/com/majiang/round/RoundResult.kt` | 新增 | 单局终局结算数据结构，包含 outcome、startingScores、finalScores、totalDeltas、events 等 |
| `core/src/main/kotlin/com/majiang/round/RoundChange.kt` | 新增 | `ApprovedRoundStep`、只读变更快照 `RoundChange` 以及规划结果密封接口 `RoundPreparation` (`Ready` / `Rejected`) |
| `core/src/main/kotlin/com/majiang/round/RoundRuleService.kt` | 新增 | 纯函数式单局规则服务：`legalActions` 生成与 `validatePreconditions` 前置合法性校验 |
| `core/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt` | 新增 | 候选状态试探规划器：实现摸牌、打牌、立直、副露、抢杠、自摸、荣和、四种流局等全部机械流转 |
| `core/src/main/kotlin/com/majiang/round/RoundStateMachine.kt` | 新增 | 权威单局状态机：版本递增、只读防御投影、`prepare` 纯函数规划、`install` 原子生效 |
| `core/src/main/kotlin/com/majiang/round/RoundCommandProcessor.kt` | 新增 | 外部规划入口：结合 `RoundRuleService` 与 `RoundTransitionPlanner`，提供 `legalActions` 与 `prepare` |

---

## 3. 跨任务固定契约执行对照

### 3.1 com.majiang.round API 对照

```kotlin
// 1. RoundStateMachine
class RoundStateMachine(
    initialState: GameState,
    val roundId: String = UUID.randomUUID().toString(),
    internal val planner: RoundTransitionPlanner = RoundTransitionPlanner()
) {
    internal val state: GameState // 初始局保留传入引用以兼容旧 fixture；客户端永不可访问
    val version: Long             // 从 0L 开始，每次 install 递增 1L
    val responseWindow: ResponseWindow? // 防御复制 (deepCopy)
    val result: RoundResult?      // 防御复制
    fun readState(): GameState   // 深复制 (deepCopy)
    internal fun prepare(step: ApprovedRoundStep): RoundChange // 无副作用候选计算
    internal fun install(change: RoundChange)                  // 外部验证和审计后原子应用
}

// 2. RoundResult
data class RoundResult(
    val resultId: String,
    val roundId: String,
    val outcome: RoundOutcome,
    val startingScores: List<Int>,
    val finalScores: List<Int>,
    val totalDeltas: List<Int>,
    val startingRiichiSticks: Int,
    val remainingRiichiSticks: Int,
    val events: List<GameEvent>
)

// 3. RoundChange
class RoundChange internal constructor(
    val roundId: String,
    val sourceVersion: Long,
    val state: GameState,
    val responseWindow: ResponseWindow?,
    val result: RoundResult?,
    val events: List<GameEvent>
)

// 4. RoundPreparation
sealed interface RoundPreparation {
    data class Ready(val change: RoundChange) : RoundPreparation
    data class Rejected(val reason: RejectionReason) : RoundPreparation
}

// 5. RoundCommandProcessor
class RoundCommandProcessor(
    val ruleEngine: RiichiRuleEngine = RiichiRuleEngine.INSTANCE,
    val winningScoreEvaluator: WinningScoreEvaluator = WinningScoreEvaluator.INSTANCE,
    val settlementService: SettlementService = SettlementService.INSTANCE,
    val random: Random = Random.Default
) {
    fun legalActions(round: RoundStateMachine, seat: Int): List<GameAction>
    fun prepare(round: RoundStateMachine, action: GameAction): RoundPreparation
}
```

---

## 4. 关键验收用例支持细节

1. **Round.prepare 无副作用**：
   - `round.prepare` 完全在 `round.readState()` 创建的独立副本上运行。
   - 权威状态的 `state`、`version`、`responseWindow`、`result` 与 `accumulatedEvents` 在 `prepare` 期间保持不变。
   - 外部审计（如 `StateInvariants.assertValid`）失败直接丢弃 `change`，不调用 `round.install`，零污染。
2. **终局不可重开与和止隔离**：
   - 若 `round.result != null` 或 `round.state.phase == GamePhase.ROUND_END`：
     - `processor.prepare` 拒绝所有后续动作（包括 `StartStacking`），返回 `Rejected(WRONG_PHASE)`；
     - `round.prepare` 抛出 `IllegalStateException`。
   - `DeclareAgariYame` 属于整场 Session 裁决范围，`processor.prepare` 明确拒绝，返回 `Rejected(UNSUPPORTED_ACTION)`；`round.prepare` 抛出 `IllegalArgumentException`。
3. **StartStacking 约束**：
   - 仅允许尚未发牌的新实例 (`state.phase == STACKING && state.wall.isEmpty()`) 执行。
   - 洗牌（使用注入的 `random`）、牌山王牌划分、翻开首张宝牌指示牌、庄家跳牌等全部发牌流程一次性完成，庄家获得 14 张牌（含 `drawnTile`），阶段转入 `CHEAT_WINDOW`。
4. **单局立直与结算 delta**：
   - 立直宣言扣除 1000 点并增加场上 `riichiSticks`；
   - 结算时和牌者收取场上全部供托立直棒；
   - `RoundResult.totalDeltas` 以本局起始积分作为基准计算：`finalScores[i] - startingScores[i]`，立直扣除与胜者收益完全守恒闭环。
5. **源码依赖隔离检查**：
   - `round` 包内未引用或导入任何 `MatchProgression`、`AIPlayer`、`AIStrategy`、`SeatController`、`StateInvariants` 或网络相关类，严格遵守单局领域边界。

---

## 5. 限制与交接说明

1. **守恒审计由应用层触发**：
   - 根据规范，`RoundStateMachine` 与 `RoundCommandProcessor` 不直接调用 `StateInvariants.assertValid`，保证单局引擎纯粹专注于状态转移；守恒审计由 S 任务的 `ApplicationCoordinator` 在收到 `Ready(change)` 后、调用 `install(change)` 前显式执行。
2. **事件广播由协调器负责**：
   - `RoundStateMachine` 仅在其内部记录已提交的事件清单，并通过 `RoundChange.events` 返回本次变更所产生的新事件；向 `GameEventBus` 或客户端订阅者的广播由应用协调器统一完成。
3. **AI 决策与立直自动摸切由协调器调度**：
   - `RoundCommandProcessor` 不驱动 AI；当响应窗口收齐或需要下一次出牌时，由外部应用协调器查询 `legalActions` 并推进自动/策略流程。
