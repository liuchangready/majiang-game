# 任务 D2 交付报告：TableHost 实际接线与生命周期返修

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Production Developer)
- **发起方 / 检视方**：Codex (Designer & Reviewer)
- **关联设计与任务**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、Task D2 契约

---

## 1. 交付文件清单

本次任务 D2 严格限制在指定所有权范围内，未修改任何测试代码、构建脚本（Gradle）、ApplicationCoordinator 或 Round/Session/GameState 领域对象：

| 模块 / 路径 | 变动类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/host/TableHost.kt` | 修改 / 重构 | 完整实现 16 项接线修复、权威时钟调度、节奏门控、掉线接管与生命周期管理 |
| `core/src/main/kotlin/com/majiang/access/BoundMahjongGameAccess.kt` | 修改 | 单局边界检查，缓存旧局只读结果，防止跨局数据伪装与错误提交 |
| `core/src/main/kotlin/com/majiang/access/local/LocalSession.kt` | 修改 | 端点级订阅追踪与关闭清理，配合宿主实现最后连接断开托管 |
| `doc/v3-migration/delivery-D2.md` | 新增 | 本交付报告 |

---

## 2. 16 项接线与生命周期修复逐项说明

### 1. startSession 依据实际发牌状态推进与幂等保护
- **原问题**：`startSession` 仅判断 `session.lifecycle == ROUND_ACTIVE` 就直接返回 `APPLIED`，但 `ApplicationCoordinator` 构造完成后即处于 `ROUND_ACTIVE` 阶段且单局仍为 `STACKING`（未发牌），导致调用 `start()` 永远不发牌。
- **修复**：检查 `coordinator.activeRound` 的实际发牌状态（`phase != STACKING || wall.isNotEmpty()`）。若尚未发牌，必须且确实调用 `coordinator.start()` 推进洗牌与发牌（庄家14张、闲家13张）；仅当实际已发牌后重复调用 `start()` 时，才幂等返回 `APPLIED`。

### 2. 权威 controlEpoch 统一与令牌修复
- **原问题**：`buildClientDecision` 中使用 `ConnectionControl` 的初始 epoch (0) 覆盖了 `Coordinator.initializeMatch` 产生的真实 epoch (1)，导致客户端提交 `submitChoice` 时代次校验全部失败。
- **修复**：统一以 `coordinator.queryDecision(seat).controlEpoch` 作为公开与提交的唯一权威令牌；AI 任务派发与结果生效校验绑定同一代次；`ConnectionControl` 仅负责连接状态与角色跟踪，不再制造平行公开代次。

### 3. round.responseWindow 局部变量捕获消除 smartcast 隐患
- **原问题**：`RoundStateMachine.responseWindow` 带有自定义 getter (`get()`)，在 Kotlin 编译器中无法被 smartcast 为非空。
- **修复**：在 `submitChoice`、`tick`、`onStateChanged` 等关键路径中，均先将 `round?.responseWindow` 提取至局部变量 `val window = round?.responseWindow`，通过局部不可变变量进行非空判断与属性访问，彻底消除编译隐患。

### 4. rhythmIntervalMillis 默认 150ms 节奏控制与超时优先
- **原问题**：`rhythmIntervalMillis` 默认值为 0 且从未生效；动画忙门控直接阻断了超时检查，导致超时计时因前端忙被无限延期。
- **修复**：`rhythmIntervalMillis` 默认设为 150ms，维护 `lastActionTimeMillis`，每 tick 最多推进一个自动动作（长帧不追赶）；将到期决策超时检查（`expiredTimings`）置于 `presentationBusy` 与节奏门控之前，确保权威时钟到期事实具有最高优先级，绝不因展示忙无限推迟。

### 5. 严格的决策计时结算顺序 (complete / expire)
- **原问题**：AI 动作与自动摸切在提交执行前就提前调用 `complete()`；超时决策在动作未执行前就调用 `expire()`。一旦底层动作被拒或失败，时钟已关闭但牌局未推进，导致牌桌永久卡死。
- **修复**：AI 动作、自动摸切与超时动作均必须先执行 `coordinator.handleAction(...)`，**当且仅当**返回 `ActionResult.Accepted` 时，才执行 `decisionClock.complete(...)` 或 `decisionClock.expire(...)`。提交失败不消耗计时，确保下一 tick 或外部重试能继续处理。

### 6. 局间决策时钟隔离与储备保护
- **原问题**：`syncDecisionsAndClocks` 将 `roundId == null` 的局间决策（如 `WAITING_DEALER_CHOICE`）伪造 `roundId = "session"` 传入 `decisionClock.open`，导致 B2 的严格 roundId 校验崩溃。
- **修复**：仅当 `decision.roundId != null && decision.roundId == activeRoundId` 时才向 `decisionClock` 打开局内决策计时；局间决策不启动单局时钟；真人 ready 确认与庄家终了决策不扣减局内时间储备。

### 7. 历史单局结算事件完整缓存
- **原问题**：单局结束后 `session.activeRound` 被置 null，原同步逻辑从 `activeRound.result` 读结算导致历史局结果丢失，`queryResult` 永远返回空列表。
- **修复**：在单局结束与局间交接时，依据上一局 `roundId` 完整缓存其终局结算事件（含自摸/荣和/流局及得分增减）；`queryRoundResultEvents` 优先查询该缓存，确保 `BoundMahjongGameAccess.queryResult()` 在跨局后仍能稳定读取历史公开结算，绝不串入新局数据。

### 8. BoundMahjongGameAccess 单局边界与旧局只读守卫
- **原问题**：旧单局的 `BoundMahjongGameAccess.snapshot()` 与 `decision()` 在对局进入新局后，直接返回了新局的快照与决策。
- **修复**：增加单局匹配检查与 `lastSnapshotForThisRound` 缓存。当当前活跃局已切换或处于局间时，`snapshot()` 返回该旧局的只读快照（选项置空），`decision()` 返回标识为 `round-ended-$roundId` 且 options 为空的已终结决策，旧绑定绝不伪装当前新局。

### 9. LocalSession 订阅生命周期与重入查询安全
- **原问题**：`LocalSession.close()` 没有清理自身订阅，无法管理多端点生命周期；串行锁在监听器回调中直接抛出重入异常，导致初始化时重入查询被吞、界面缺失基线。
- **修复**：`LocalSession` 追踪内部创建的所有 `AutoCloseable` 并在 `close()` 时显式注销；宿主维护各席位活跃会话集合，当某一席位最后连接关闭时触发断线托管政策；宿主 `withSerialLock` 允许当前线程的重入只读查询（返回一致快照），对重入变更显式返回 `REENTRANT_CALL_REJECTED` 回执；`host.close()` 后所有连接、查询和提交均明确拒绝。

### 10. 状态无变化不递增 epoch 与安全控制权收回
- **原问题**：重复断线或重复调用 `requestControl` 会反复调用 `coordinator.setSeatController` 导致 controlEpoch 频繁空转递增；掉线连接也可非法收回控制权。
- **修复**：在状态与角色未发生真实变更时，直接返回已有状态或幂等 `APPLIED`，不触发协调器修改；`reclaimControl` 严格校验端点必须处于连接状态且原席位归属于真人；新 epoch 自然使旧 AI 结果失效，但不重置决策截止时间与储备余额。

### 11. onStateChanged 游标增量防抖与新基线同步
- **原问题**：每次调用 `onStateChanged`（即使无事件或仅内部时钟毫秒流逝）均对所有席位递增游标并广播，导致游标严重膨胀与客户端无意义刷帧。
- **修复**：引入 `hasVisibleChange` 判定，对比私密事件、视图投影、决策、版本号、出千点数及有意义的时钟字段（`opensAt`, `deadline`, `reserve`，明确忽略毫秒级 `serverNow`），无变化不递增游标、不发更新；`resumeSeat` 派发新基线更新给现有端点，防止因 epoch 切换停滞。

### 12. 事件隐私过滤与深拷贝隔离
- **原问题**：广播事件与快照存在浅拷贝引用漂移风险。
- **修复**：全部事件均通过 `SessionUpdate.filterVisibleEvents` 过滤各席位私密信息；历史结算事件按席位权限脱敏；派发给各监听器的 `PlayerUpdate` 均通过 `deepCopy()` 进行防御性深拷贝，互不干扰。

### 13. WAITING_DEALER_CHOICE 庄家决策与防重开两局
- **原问题**：局间处于 `WAITING_DEALER_CHOICE` 时，非庄家席位发送 ready 会绕过庄家裁决强制开局；连点 ready 会导致连续开局。
- **修复**：在 `WAITING_DEALER_CHOICE` 状态下，`signalNextRoundReady` 明确拒绝（`WAITING_DEALER_CHOICE`），必须由庄家通过 `submitChoice` 做出连庄继续（`opt-continue`）或流局了结（`opt-agari-yame`）的显式裁决；每次新局开启后清空 `readySeats`，对局处于活动阶段时后续 ready 均被拦截（`ROUND_ACTIVE`），杜绝连开两局。

### 14. AI 线程池拒绝异常捕获与任务防泄露
- **原问题**：有界队列线程池饱和时抛出 `RejectedExecutionException`，直接中断了 `onStateChanged` 的状态广播；`dispatchedAiTasks` 无清理机制，对局进行多局后内存无限增长。
- **修复**：对 `internalExecutor.submit` 进行 `try-catch` 保护，捕获 `RejectedExecutionException` 保证状态广播顺利完成，AI 任务丢失由决策时钟超时兜底；在每次决策同步时清理非当前活跃局的过时 `AiTaskKey`。

### 15. 配置防御性深拷贝与宿主字段私有化
- **原问题**：外部可通过引用修改 `controllers`、`startingScores` 等配置；协调器等内部权威对象对外公开。
- **修复**：对 `MatchConfig`（`startingScores`, `winds`, `tieBreakSeats`）与 `controllers` 执行深拷贝；将 `coordinator`, `connectionControl`, `decisionClock`, `strategyRegistry` 等内部实现字段一律修改为 `private`，不向测试或外部开放权威内部状态。

### 16. 出千点数整合与删除平行账本
- **修复实现**：
  - 牌桌创建初始 `GameState` 时，通过反射尝试将 `config.cheatEconomy.initialPoints` 写入 `GameState.cheatPoints`；
  - `buildSnapshotForSeat` 构建快照时，尝试从当前活动局 `GameState` 读取权威 `cheatPoints`，未定义时安全回退至初始配置；
  - 彻底删除了 `TableHost` 内部原有的平行可写 `CheatLedger` 字段，不向一般客户端开放随意增减出千点数的接口。

---

## 3. 尚存缺口说明 (Remaining Gaps)

1. **D3 领域层出千账本对接 (Domain Cheat Ledger)**：
   - **现状**：根据任务契约划分，D2 不得修改领域层（`GameState`, `RoundStateMachine`, `MatchSession`, `ApplicationCoordinator`），这些文件归 Task D3 独占；
   - **处理与准备**：当前 `TableHost` 已经预留了对 `GameState.cheatPoints` 的安全反射读取与回退逻辑，并移除了外部平行账本。一旦 Task D3 在领域对象中加入 `cheatPoints` 与原子出千流水接口，宿主即可无缝对接，无需再次重构。

---

## 4. 交付总结

任务 D2 涉及的全部 16 项接线修复均已落实到位。代码完全符合纯 Kotlin/JVM 17 与 Android 脱糖规范，无越界修改任何受保护文件。后续可由 Codex 运行验收测试 `TableHostAcceptanceTest` 并推进 V3 后续阶段集成。
