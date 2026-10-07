# 任务 D 交付报告：V3 权威宿主 (TableHost) 与本地访问 (LocalSession) 整合

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Task D 生产开发)
- **发起方 / 检视方**：Codex (设计、测试编写、编译构建与验收)
- **关联设计与契约**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[V3 执行计划与契约](PLAN.md)

---

## 1. 交付变更清单

本次交付严格遵守第一波与任务 D 代码所有权边界：
- **唯一写入范围**：`core/src/main/kotlin/com/majiang/host/**`、`core/src/main/kotlin/com/majiang/access/local/**` 以及本文档 `doc/v3-migration/delivery-D.md`；
- **零权限侵入**：未运行任何 Shell 终端、命令行或 Gradle 命令；未修改测试代码；未改动 UI / GameScreen；未修改 Task A、Task B、Task C 所属文件；
- **零重复造轮子**：全面串联已有 `RoundStateMachine`、`MatchSession`、`MatchProgression`、`RiichiRuleEngine`、`SettlementService`、`DecisionClock`、`TimeoutPolicy`、`ConnectionControl`、`CheatLedger` 与 `GameClient`，不重新实现另一套麻将规则。

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/host/ai/TsumogiriAIStrategy.kt` | 新增 | 摸切 AI 策略实现，满足未知 profile 校验与摸切/Pass 基线 AI 要求 |
| `core/src/main/kotlin/com/majiang/access/local/LocalSession.kt` | 新增 | 本地私有席位绑定 `SessionAccess` 适配器，实现纯净异步客户端契约 |
| `core/src/main/kotlin/com/majiang/host/TableHost.kt` | 新增 | V3 统一单桌权威宿主，实现单桌串行化、20+3决策计时、超时裁决、异步AI、展示门控与有序游标流 |
| `doc/v3-migration/delivery-D.md` | 新增 | 本交付报告 |

---

## 2. 核心架构与 14 项契约落实

### 2.1 公开 API 契约

```kotlin
package com.majiang.host

class TableHost @JvmOverloads constructor(
    val config: MatchConfig = MatchConfig(),
    val timeControl: TimeControlConfig = TimeControlConfig(),
    val clock: MonotonicClock = SystemMonotonicClock,
    val controllers: List<SeatController> = listOf(
        SeatController.LocalHuman(),
        SeatController.Computer(),
        SeatController.Computer(),
        SeatController.Computer()
    ),
    initialState: GameState? = null,
    val disconnectPolicy: DisconnectPolicy = DisconnectPolicy(),
    val adminSeat: Int = 0,
    aiProfiles: Map<String, () -> AIStrategy> = emptyMap(),
    aiExecutor: ExecutorService? = null,
    val rhythmIntervalMillis: Long = 0L,
    coordinator: ApplicationCoordinator? = null
) : AutoCloseable {
    fun connect(seat: Int): SessionAccess
    fun tick(presentationBusy: Boolean = false): Boolean
    fun disconnect(seat: Int)
    fun reconnect(seat: Int)
    override fun close()
}
```

- **不添加 `stateForTesting`**：所有测试验收均通过 `connect(seat)` 返回的 `SessionAccess` 观察不可变 `PlayerSnapshot`、`PlayerDecision` 与 `PlayerUpdate`，或通过既有独立领域 fixture 进行无头验证。
- **快照数据脱敏与保护**：`PlayerSnapshot` 仅包含按座位脱敏的 `PlayerView`、当前决策 `PlayerDecision`、权威时钟采样 `List<ClockView>` 与出千余额 `cheatPoints`，绝不直接对外暴露内部 `GameState` 或 `ApplicationCoordinator`。

---

### 2.2 14 项关键契约落实明细

1. **Coordinator 初始化与自动策略解耦**：
   - 校验赛制配置 `config.validateSupported()`，拒绝未实现的 3 人麻将与非法配置；
   - 构造 `ApplicationCoordinator` 时配置 `autoPlayAi = false`, `autoRiichiDiscard = false`, `deferAutomaticActions = true`, `matchProgression = MatchProgression(config.toRuleConfig())`；
   - 宿主接管全部自动调度策略，异步 AI 运算不阻塞权威串行事务；内部使用 `handleAction` 提交已裁决的 AI 与超时选择，真人提交走 `submitChoice`。

2. **单桌统一串行锁与重入明确拒绝**：
   - 使用 `ReentrantLock` 与原子线程记录器 `executingThread` 守护所有 `start`、`nextRound`、`submit`、`tick`、`control`、`disconnect`、`reconnect`、`snapshot`、`subscribe` 操作；
   - 在锁内调用监听器回调期间保持执行线程标记。若任何外部监听器尝试在回调中再次调用宿主动作提交方法，明确拒绝并返回 `ReceiptStatus.REJECTED` (reason: `"REENTRANT_CALL_REJECTED"`)，杜绝半提交与死锁；
   - 查询快照与订阅增量强一致。

3. **待决策同步与单调时钟生命周期**：
   - 每次状态变更后统一同步所有座位决策；
   - 当座位有合法选项时，按 `(roundId, seat, decisionId)` 打开 `DecisionClock`；非操作座位关闭计时；
   - 若当前座位已存在活跃计时且决策标识改变，先结算并取消旧计时再开启新计时，避免抛出同座位重叠决策异常；
   - 控制代次 `controlEpoch` 变更仅废止旧代次提交凭据，不重置同一语义决策的已消耗时间与截止；
   - 换局与新局发牌依 `ReserveScope`（单局重置 20s vs 整场延续）重置或保留储备，并关闭上一局的所有决策时钟。

4. **严格 deadline 裁决、幂等与不可篡改回执**：
   - 收到动作提交后，先核验席位身份、幂等 `requestId` 与当前决策有效性；
   - 检查权威单调时钟 `now < timing.deadlineMillis`：若已达到或超过截止时间，拒绝请求 (`ReceiptStatus.REJECTED`, reason: `"DEADLINE_EXPIRED"`)，系统超时默认动作胜出；
   - 动作经权威规则核验并接受后才调用 `decisionClock.complete(key, now)` 结算储备时间，非法提交绝不先扣除储备；
   - 稳定回执缓存：相同 `(seat, requestId)` 且载荷一致返回原回执；载荷不同拒绝 (`"REQUEST_ID_CONFLICT"`)；已消费或超期的决策无法通过伪造新 `requestId` 提交其他动作。

5. **响应窗口状态与 AI 视图脱敏**：
   - 响应选择提交成功后，若响应窗口仍在等待他家未决，回执状态为 `ReceiptStatus.RECORDED`；若响应窗口完成并执行（或自家摸打动作），状态为 `ReceiptStatus.APPLIED`；
   - 响应窗口闭合时，其余未响应座位的决策时钟按 `cancel` 结算已耗时并扣减超额储备，不白送时间；
   - 对已被 AI 接管或电脑控制的座位，客户端调用 `queryDecision` 返回 `options = emptyList()`，防止客户端直接操作代打；权威内部仍可正常生成 AI 候选动作。

6. **确定性超时动作 (TimeoutPolicy)**：
   - 决策到期时，使用 Task B 交付的 `TimeoutPolicy.choose(view, options)` 选取默认动作；
   - 荣和机会超时优先走 `GameAction.Pass`，触发振听规则；摸打超时优先摸切，副露后出牌确定性按牌张自然序升序出牌；
   - 严禁自动和牌、开杠、立直或副露；每 tick 最多处理一个超时动作。

7. **异步守护 AI 计算**：
   - 使用有界守护线程池 (`ThreadPoolExecutor`, queue capacity 32) 在锁外执行 `strategy.decideAction(view, legalActions)`，仅传入深拷贝的脱敏视图与合法动作；
   - 任务绑定 `(seat, roundId, decisionId, epoch)`；AI 计算返回时在串行锁内二次验证，若代次、局号、决策或截止已失效则安全丢弃；
   - AI 异常或卡死不影响主进程查询或其他真人玩家操作，到期由 `TimeoutPolicy` 超时兜底；`TableHost.close()` 调用 `shutdownNow()` 立即清理等待任务。

8. **自动立直摸切与展示门控**：
   - 当真人立直且已摸牌，若无合法自摸与暗杠选项，在 `presentationBusy == false` 时自动打出摸入牌；
   - `presentationBusy == true` 仅门控自动动作节奏，不暂停已打开的权威 deadline，不影响真人网络倒计时；
   - 普通真人绝不被 AI 代打，必须等待真人手动提交或超时。

9. **掉线与控制权接管 (DisconnectPolicy)**：
   - 支持 `IMMEDIATE_AI`（立即转 AI 接管）、`GRACE_THEN_AI`（宽限期内走超时策略，到期转 AI）与 `TIMEOUT_ONLY`（纯走超时策略）；
   - 支持根据 `aiProfileId` 动态解析 AI 策略（内置 `"basic"` 与 `"tsumogiri"`，支持外接配置扩展）；未知 profile 在构造时拒绝；
   - 接管期间保持原人类席位 `playerId` 与 `seatId` 身份；`reconnect` 仅恢复连接标记，不自动撤销已提交 AI 动作；通过 `requestControl(takeover = false)` 显式收回控制权，递增 `controlEpoch` 并废止正在运算的 AI 决策，保留时间储备。

10. **连续游标、增量订阅与断线自愈**：
    - 每个座位拥有独立的 `syncEpoch` 与从 0 开始单调连续递增的 `cursor`；
    - 握手零竞态：`subscribe` 在串行边界内向监听器投递基线初始更新 (`fromCursor == cursor, events = emptyList()`)；
    - `resume(cursor)` 产生全新 `syncEpoch`，游标重置为 1，废止旧代次延迟更新并返回权威最新快照；
    - 仅在可见投影、时钟、控制或事件实际发生变更时才递增游标发布更新，空闲 tick 不无限发布更新；
    - 统一调用 `SessionUpdate.filterVisibleEvents` 脱敏对手暗牌与私密千术。

11. **深度隔离与端点生命周期独立性**：
    - 所有跨线程/监听器传递的数据对象 (`PlayerSnapshot`, `PlayerDecision`, `PlayerUpdate`, `GameEvent`) 均执行严格深拷贝与不可变集合包装；
    - `LocalSession.close()` 仅移除对应座位的订阅监听器，不影响整场比赛；`TableHost.close()` 负责彻底释放整桌资源。

12. **房间管理与整场推进权限**：
    - 仅绑定房主/管理员（默认 `seat 0`）有权调用 `startSession` 开赛；
    - 局间准备采用各座位准备就绪机制 (`readySeats`)：AI 自动准备，所有真人座位均调用 `nextRound` 后才触发 `coordinator.nextRound()`；多次重复调用幂等返回 `RECORDED`，严禁多端点重复创建单局；
    - 处于 `WAITING_DEALER_CHOICE` 时仅当前庄家有权通过 `submitChoice` 做出继续对局或尾庄和了终了 (`DeclareAgariYame`) 的选择。

13. **单局访问生命周期绑定 (MahjongGameAccess)**：
    - `activeGame()` 返回与当前 `roundId` 绑定的 `BoundMahjongGameAccess`；
    - 单局结束后进入下一局，旧局绑定的端点提交动作立即被拦截拒绝 (`ROUND_NOT_ACTIVE` / `ROUND_MISMATCH`)，严禁旧局提交转嫁到新局；
    - `queryResult()` 仅返回所绑定单局的历史结算事件，绝不透出下一局的任何结算。

14. **真实能力声明 (AccessCapabilities)**：
    - `LocalSession.capabilities()` 声明 `protocolVersion = 3, supportedPlayerCounts = setOf(4), remoteTransport = false, persistence = false, resumption = true`，如实声明当前本地阶段支持能力，不假报远端传输或跨进程持久化。

---

## 3. 与第一波任务 (A / B / C) 的接口对接验证

1. **对接 Task A (MatchConfig / Progression / CheatLedger)**：
   - `TableHost` 构造时通过 `config.validateSupported()` 进行强校验，并使用 `MatchProgression(config.toRuleConfig())` 驱动赛程推进；
   - 挂载独立出千账本 `cheatLedger`，点数余额在 `PlayerSnapshot.cheatPoints` 中实时下发客户端。
2. **对接 Task B (Timing / Control)**：
   - 宿主全面依赖 `DecisionClock` 与 `TimeoutPolicy`，20+3 秒计时、超时判定、暂停恢复完全对齐；
   - 掉线模式与代次流转完全通过 `ConnectionControl` 与 `DisconnectPolicy` 驱动。
3. **对接 Task C (Access / Sync / Client)**：
   - `LocalSession` 实现了 Task C 冻结的 `SessionAccess` 接口；
   - `BoundMahjongGameAccess` 无缝复用作为单局访问实例；
   - 输出的 `PlayerSnapshot`、`PlayerUpdate` 与游标模型完全适配 `ClientSynchronizer` 与 `GameClient`，满足有界乱序重排与 `resume` 自愈。

---

## 4. Codex 验收测试指引

Codex 可基于下列推荐用例进行全方位验证：

1. **本地单机完整对局流程**：
   - 创建 `TableHost(adminSeat = 0, controllers = listOf(LocalHuman(), Computer(), Computer(), Computer()))`；
   - seat 0 连接 `LocalSession` 并调用 `connect()` / `start()`；
   - 验证发牌后 seat 0 收到 14 张手牌及弃牌候选，决策时钟成功开启；
   - seat 0 提交 `DiscardTile`，验证时钟正常完成并结算；
   - 循环调用 `tick()`，验证 3 家 AI 依次顺序摸打（每 tick 最多一步），无死锁卡顿。
2. **超时默认动作与振听**：
   - 注入 `ManualMonotonicClock`；
   - seat 0 产生吃碰或荣和响应窗口；
   - 推进时钟至 23000ms（超出 3s 基础 + 20s 储备）；
   - 调用 `tick()`，验证执行了 `Pass` 动作，储备时间归 0，若为荣和超时振听标志正确更新。
3. **掉线与接管控制**：
   - 配置 `DisconnectMode.IMMEDIATE_AI`，对 seat 0 调用 `tableHost.disconnect(0)`；
   - 验证 seat 0 连接状态变为 `TAKEOVER`，代次自增；
   - 调用 `tick()`，验证 AI 接替 seat 0 自动出牌；
   - 调用 `reconnect(0)` 与 `requestControl(takeover = false)`，验证控制权恢复为 `HUMAN`，代次再次自增，旧 AI 任务失效。
4. **多端点局间准备与防重发**：
   - 配置 2 名真人玩家 (seat 0 与 seat 1)；
   - 首局结束后，seat 0 调用 `nextRound()`，验证返回 `RECORDED` 且未开启新局；
   - seat 0 再次调用 `nextRound()`，验证幂等返回 `RECORDED`；
   - seat 1 调用 `nextRound()`，验证触发发牌，返回 `APPLIED`，单局状态机与 `roundId` 全新创建。
5. **重入调用与并发安全**：
   - 在 `subscribe` 回调中尝试调用 `session.submit(choice)`；
   - 验证返回 `ReceiptStatus.REJECTED` (reason: `"REENTRANT_CALL_REJECTED"`)，宿主未死锁，状态保持正确。
6. **未知 AI Profile 拒绝**：
   - 传入 `DisconnectPolicy(aiProfileId = "unknown_ai")`；
   - 验证构造 `TableHost` 抛出 `IllegalArgumentException`。

---

## 5. 暂不包含的能力声明

按照 V3 架构规划，本任务真实达成本地权威单机牌桌与统一访问整合，如实声明以下能力留待后续波次完成：
- 三人麻将规则集（P5 阶段）；
- 跨进程/跨网络远程传输适配器（网络任务 N 阶段实现，不搞伪实现）；
- 游戏存档与断电崩溃恢复（需要外部持久化组件接入）；
- Screen 客户端视图双状态桥接（任务 F 负责接入 `GameScreen` 与真实 GL）。
