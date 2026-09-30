# 任务 S 交付报告：整场 Session、应用协调器与旧引擎兼容层

- **交付日期**：2026-09-30
- **负责 Agent**：Antigravity
- **关联设计与规格**：[MAHJONG_STATE_MACHINE_API_SPEC.md](../MAHJONG_STATE_MACHINE_API_SPEC.md)

---

## 1. 交付变更清单

本次任务 S 严格按照既定代码所有权边界完成开发，未改动任何测试代码、构建脚本或 Task R / Task C 所有的文件：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/match/MatchSession.kt` | 新增 | 纯整场比赛领域对象，管理生命周期、局间账本、单局历史、整场推进与结果 |
| `core/src/main/kotlin/com/majiang/application/ActionDecisionDto.kt` | 新增 | 跨任务契约 DTO：`PlayerDecision`, `ActionOption`, `ActionChoice` |
| `core/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt` | 新增 | 单桌串行协调器：外部验证、守恒审计、交接事务、AI 调度、幂等注册与共享广播 |
| `core/src/main/kotlin/com/majiang/state/GameStateMachine.kt` | 重构 | 剥离原有 1441 行巨型逻辑，重构为委托给 `ApplicationCoordinator` 的轻量兼容适配器 |
| `doc/round-session-split/delivery-S.md` | 新增 | 本交付报告 |

---

## 2. 核心领域职责与架构落地

### 2.1 MatchSession (com.majiang.match)
- **纯领域容器与边界隔离**：
  - 源码无任何 `com.majiang.ai.*`、`com.majiang.rules.*`、`com.majiang.settlement.*`、`StateInvariants` 或网络依赖。
  - 不包含吃碰杠和与算番逻辑，不直接调用 `MatchProgression`，所有状态变更必须由外部经过验证的 `ApprovedSessionStep` 驱动。
- **状态流转机制 (`prepare` / `install`)**：
  - `prepare(ApprovedSessionStep)` 产出包含不可变候选数据与领域事件的 `SessionChange`，绝无就地修改副作用。
  - `install(SessionChange)` 在版本匹配校验 (`sourceVersion == version`) 后原子写入权威状态。
- **账本与单局交接唯一性**：
  - 局中实时点数与供托严格由 `RoundStateMachine` 的 `roundLedger` 维护；Session 的 `betweenRoundScores` / `betweenRoundRiichiSticks` 仅在局间作为基线。
  - 接收 `RoundResult` 时以其 `finalScores` 和 `remainingRiichiSticks` 设立新基线，绝不重复加减 `totalDeltas`。
  - 单局终态固定为 `ROUND_END`；整场结束 (`FINISHED`) 时旧 Round 实例对象不被篡改为 `MATCH_END`。

### 2.2 ApplicationCoordinator (com.majiang.application)
- **单桌串行与互斥守卫**：
  - 采用顶层 CAS `isProcessingAction: AtomicBoolean` 守卫，并发或监听器回调重入时一律安全返回 `ActionResult.Rejected(ACTION_NOT_AVAILABLE)`，杜绝死锁与竞态。
- **单局结果交接与整场推进**：
  - 单局结束产出 `RoundResult` 时，通过 `consumedResults: Set<String>` 严格确保每个 `resultId` 仅交接一次。
  - 协调器负责调用 `MatchProgression.nextRound(...)` 产出 `ProgressionPlan`，并将 `RoundChange` 与 `SessionChange` 在同一提交边界内原子安装。
  - 连庄或新局均通过 `RoundStateMachine(newRoundState)` 产生全新实例与全新 `roundId`，旧 Round 保留于历史记录中且不可变。
- **逐座位控制者与控制代次 (controlEpoch)**：
  - 逐座保存 `SeatController` 与 `controlEpoch`。配置初始化或座位切换时自增对应座位的代次并作废该座位已有决策。
- **决策与请求幂等性 (PlayerDecision / ActionChoice)**：
  - `queryDecision(seat)`：生成深拷贝动作的 `ActionOption`。在响应窗口中，某一家提交响应不影响其他尚未响应座位的 `decisionId`；自身响应、窗口关闭、局切换或代次变化使旧决策失效。
  - `submitChoice(seat, choice)`：按 `(seat, requestId)` 作用域进行幂等查重。相同载荷直接返回原回执（无副作用、不重复执行、不重复广播）；冲突载荷（相同 requestId 不同 choice）明确拒绝。
- **事件发布与广播隔离**：
  - `subscribe(listener)` 注册端点监听器。顶层命令提交完成后统一批量广播 `unmodifiableList(events)`。
  - 遍历广播时对每个监听器使用单独异常保护 (`try-catch`)，任何端点或测试回调抛出异常均不会影响协调器状态机或其他订阅端点。
- **自动动作调度循环 (runAutomaticActionLoop)**：
  - 自动顺序调度未响应的 AI/托管座位；若策略报错或返回非法动作，自动回退到 `Pass`（响应窗口）或摸切/首个合法弃牌（自家出牌阶段）。
  - 真人立直自动摸切偏好生效时自动摸切，但若检测到合法自摸或暗杠，精准暂停等待真人决策。

### 2.3 GameStateMachine 兼容代理 (com.majiang.state)
- 保持所有公开构造参数 (`state`, `eventBus`, `ruleEngine`, `settlementService`, `matchProgression`, `autoPlayAi`, `autoRiichiDiscard` 等) 及默认值。
- 暴露 `val coordinator: ApplicationCoordinator`，将所有公开查询与命令 (`controllerFor`, `initializeMatch`, `legalActions`, `submitPlayerAction`, `handleAction`, `nextRound`, `advanceAutomaticActions` 等) 100% 委托给 `coordinator`。
- 保持对旧无头单测 fixture `initialState` 的即时状态镜像同步（牌张、分值、供托、阶段），确保历史 400+ 个回归测试无需重写即可平滑迁移。

---

## 3. 跨任务契约对齐情况

1. **对 Task R (com.majiang.round) 契约**：
   - 依赖 `RoundStateMachine`, `RoundResult`, `RoundCommandProcessor`, `RoundPreparation`, `RoundChange`。
   - `RoundCommandProcessor` 作为单局外部无状态规划服务，负责动作合法性校验与 `RoundChange` 准备；协调器负责审计并在 `round.install` 中提交。
   - 协调器负责拦截系统动作与 Session 动作（如 `DeclareAgariYame` 与跨局 `StartStacking`），不透传给 `RoundCommandProcessor`。

2. **对 Task C (com.majiang.client / session / screen) 契约**：
   - `ApplicationCoordinator` 完整实现 Task C 所需的调用接口：
     - `queryView(seat): PlayerView`：深拷贝安全投影，整场结束时组合显示 `GamePhase.MATCH_END`；
     - `legalActions(seat): List<GameAction>`：全量合法动作列表；
     - `queryDecision(seat): PlayerDecision` 与 `submitChoice(seat, choice): ActionResult`；
     - `subscribe((List<GameEvent>) -> Unit): AutoCloseable`：多端点广播更新；
     - `session` 与 `activeRound` 属性只读透出。

---

## 4. 能力限制与非目标声明

- **本地多客户端完全支持**：可在同一进程内创建多个 `PlayerEndpoint` 订阅同一 `ApplicationCoordinator`，动作互斥与事件广播均已实现。
- **明确未实现范围**：
  - 跨网络的 RPC / WebSocket 传输服务；
  - 用户登录、鉴权与防作弊 Token 认证；
  - 跨进程分布式事务、数据库持久化与局间断线快照恢复；
  - 本次交付不伪装提供上述网络和持久化能力，契约中已标明相关边界限制。
