# 任务 B 交付报告：时钟计时、超时默认动作与掉线控制

日期：2026-10-01  
开发者：Antigravity (Task B 生产开发)  
协作角色：Codex（测试设计、编译构建、代码检视与验收）  
设计依据：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md) 与 [V3 迁移执行计划](plan.md)

---

## 1. 任务边界与交付范围

本次交付严格遵守第一波任务分工与所有权约束：
- **唯一写入范围**：`core/src/main/kotlin/com/majiang/timing/**`、`core/src/main/kotlin/com/majiang/control/**` 以及本文档 `doc/v3-migration/delivery-B.md`；
- **零权限侵入**：未调用终端命令、Shell、Gradle 构建或测试；未修改现有测试文件；未修改 Task A、Task C 所属文件；未修改 Coordinator、GameScreen 或 Gradle 配置；
- **纯 JVM/Kotlin 引擎设计**：零 libGDX/UI 依赖，完全兼容 Android minSdk 21 脱糖标准与 JVM 17。

---

## 2. 交付生产文件清单

### 2.1 计时模块 (`com.majiang.timing`)

1. [`MonotonicClock.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/MonotonicClock.kt)
   - `fun interface MonotonicClock { fun nowMillis(): Long }`：可注入的单调时钟函数式接口；
   - `SystemMonotonicClock`：基于 `System.nanoTime() / 1_000_000L` 的系统级单调实现，支持对象引用与实例构造；
   - `ManualMonotonicClock`：提供 `advance(millis)` 与 `set(millis)` 的手动步进时钟，方便单元测试进行时钟推进。

2. [`TimeControlConfig.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/TimeControlConfig.kt)
   - `enum class ReserveScope { ROUND, SESSION }`：储备时间作用域（单局重置 vs 整场共用）；
   - `data class TimeControlConfig(baseMillis: Long = 3000L, reserveMillis: Long = 20000L, reserveScope: ReserveScope = ReserveScope.ROUND)`：20 秒储备 + 3 秒单步基础思考配置，支持非负参数前置校验。

3. [`DecisionTiming.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/DecisionTiming.kt)
   - `data class DecisionKey(val roundId: String, val seat: Int, val decisionId: String)`：决策唯一三元组键，校验合法参数；
   - `enum class DecisionTimingStatus { SCHEDULED, ACTIVE, CLOSED, PAUSED }` 及类型别名 `typealias DecisionStatus = DecisionTimingStatus`；
   - `data class DecisionTiming(key, generation, opensAtMillis, deadlineMillis, reserveAtOpenMillis, baseMillis, status)`：决策计时明细 DTO，提供 `isExpired(nowMillis)`、`calculateReserveSpent(atMillis)` 与 `calculateRemainingReserve(atMillis)`。

4. [`DecisionClock.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/DecisionClock.kt)
   - 权威单调决策时钟核心类，线程安全（内部采用 `@Synchronized`）：
     * `beginRound(roundId)`：重复调用同局不刷新余额；`ROUND` 作用域新局重置 0..3 座位为 `config.reserveMillis`；`SESSION` 作用域新局保留已有余额；清空旧局决策计时与暂停状态；
     * `open(key, opensAtMillis = clock.nowMillis())`：同决策重复调用不刷新直接返回；同座位存在另一活跃决策抛出 `IllegalStateException`；截止时间公式严格为 `opensAt + baseMillis + reserveAtOpen`；自增代次 `generation`；
     * `timing(seat)`：查询座位当前活跃决策；
     * `remainingReserve(seat)`：查询座位当前剩余储备毫秒数；
     * `complete(key, atMillis = clock.nowMillis())`：严格在截止前完成（`atMillis < deadlineMillis`）；恰好等于或晚于 deadline 时拒绝并返回 `false`；若未到 `opensAtMillis` 则免费完成不扣除储备；计算耗时并扣减储备，关闭计时，单次决策仅结算一次；
     * `expired(nowMillis = clock.nowMillis())`：只读查询当前已到期的活跃决策列表，查询不消费；
     * `expire(key, generation)`：超时结算，严格校验 `key` 与 `generation`（旧定时器任务无副作用返回 `false`）；匹配时耗尽储备（归 0）并关闭决策；
     * `cancel(key, atMillis = clock.nowMillis())`：窗口因他家裁决不再需要该玩家时结算已耗时并扣减储备，关闭计时，不白送时间；
     * `pause(nowMillis)` / `resume(nowMillis)`：全局暂停与恢复，流逝时间补偿至 `opensAtMillis` 与 `deadlineMillis`，严格冻结并保留免费时间与储备时间，恢复时不重置 3 秒免费时间。

5. [`TimeoutPolicy.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/TimeoutPolicy.kt)
   - 超时默认动作决策策略：
     * `choose(view: PlayerView, options: List<GameAction>): GameAction?`
     * `choose(options: List<GameAction>, viewerSeat: Int = 0, drawnTile: Tile? = null): GameAction?`
     * 优先级严格遵循：
       1. **优先响应 Pass**：检测 `GameAction.Pass` 并优先返回；
       2. **否则合法摸切**：若有当前摸牌 `myDrawnTile` 且作为 `DiscardTile` 在候选列表中，选定摸切动作；
       3. **否则确定性合法弃牌**：从 `DiscardTile` 候选中按 `Tile` 自然序（花色 -> 点数 -> copyIndex 升序）取最小合法弃牌，不盲目打手牌末尾；
       4. **禁止动作防护**：绝不自动自摸、荣和、副露吃碰杠、立直或千术动作；
       5. **无合法必选动作**：返回 `null` 交由宿主报错与异常处理。

---

### 2.2 控制与掉线模块 (`com.majiang.control`)

1. [`DisconnectPolicy.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/control/DisconnectPolicy.kt)
   - `enum class DisconnectMode { IMMEDIATE_AI, GRACE_THEN_AI, TIMEOUT_ONLY }`；
   - `data class DisconnectPolicy(mode: DisconnectMode = IMMEDIATE_AI, graceMillis: Long = 10000L, aiProfileId: String = "basic")`。

2. [`ConnectionControl.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/control/ConnectionControl.kt)
   - 控制者与连接状态管理：
     * `enum class ControllerRole { HUMAN, COMPUTER, TAKEOVER }`；
     * `enum class ConnectionStatus { CONNECTED, DISCONNECTED }`；
     * `data class SeatConnectionState(seat, status, role, controlEpoch, disconnectedAtMillis, graceDeadlineMillis, aiProfileId)`；
     * `ConnectionControl(policy, clock, seatCount, initialRoles)`：
       - `disconnect(seat, atMillis)`：针对 `IMMEDIATE_AI` 立即切换为 `TAKEOVER` 并自增 `controlEpoch`；针对 `GRACE_THEN_AI` 记录宽限截止时间，保持原角色与代次；针对 `TIMEOUT_ONLY` 仅标记断线；防重复抖动；
       - `checkGraceExpiration(seat, nowMillis)` / `pollPendingTakeovers(nowMillis)`：宽限期到达时原子执行接管，角色切换为 `TAKEOVER`，自增 `controlEpoch`；
       - `reconnect(seat, atMillis, reclaim)`：恢复连接状态；支持同步收回或保留托管待后续显式收回；
       - `reclaimControl(seat)` / `requestControl(seat, takeover)`：收回控制权，角色还原为 `HUMAN`，自增 `controlEpoch`，废止先前接管凭据；
       - 纯状态逻辑，保留稳定座位身份（`seatId`），不持有也不修改麻将底层领域状态机。

---

## 3. 契约核对表

| 契约项 | 规格要求 | 实现状态 | 说明 |
|---|---|---|---|
| **单调时钟** | `MonotonicClock` + `SystemMonotonicClock` | ✅ 完全满足 | 函数式接口，nanoTime 单调转换，附带 ManualMonotonicClock |
| **时间配置** | `TimeControlConfig(base=3000, reserve=20000, scope=ROUND)` | ✅ 完全满足 | 默认 20+3，支持 ROUND 与 SESSION 两种作用域 |
| **决策键与时序** | `DecisionKey` + `DecisionTiming` | ✅ 完全满足 | 包含 generation、opensAt、deadline、reserveAtOpen、status |
| **时钟新局** | `beginRound(roundId)` | ✅ 完全满足 | 同局重复不刷新，ROUND 重置 20s，SESSION 保留，清理旧局计时 |
| **时钟开表** | `open(key, opensAtMillis)` | ✅ 完全满足 | 同决策重复不刷新，同座位并存阻塞决策抛异常，正确计算 deadline |
| **时钟完成** | `complete(key, atMillis)` | ✅ 完全满足 | deadline 当刻及之后拒绝，opensAt 前免费，扣减超额储备，结算一次 |
| **时钟到期** | `expire(key, generation)` + `expired()` | ✅ 完全满足 | expired 纯只读；expire 校验代次，旧定时器无副作用，储备归 0 |
| **时钟取消** | `cancel(key, atMillis)` | ✅ 完全满足 | 结算实际耗时，扣减相应储备，不白送时间 |
| **时钟暂停恢复** | `pause()` / `resume()` | ✅ 完全满足 | 补偿流逝时间至 opensAt 与 deadline，保留免费时间与剩余储备 |
| **超时策略** | `TimeoutPolicy.choose(view, options)` | ✅ 完全满足 | 优先 Pass，次选摸切，再选确定性最小弃牌；不和不杠不立直 |
| **掉线模式** | `DisconnectMode` (3 种) + `DisconnectPolicy` | ✅ 完全满足 | IMMEDIATE_AI, GRACE_THEN_AI, TIMEOUT_ONLY，参数校验 |
| **连接状态机** | `ConnectionControl` | ✅ 完全满足 | 断线、宽限倒计时到期接管、重连、收回控制，维护 controlEpoch |

---

## 4. Codex 验收指引

Codex 可基于本交付设计并运行如下测试套件进行完整验证：

1. **`DecisionClockTest`**：
   - 注入 `ManualMonotonicClock`；
   - 验证 `beginRound` 在同一 `roundId` 下多次调用不重置储备；跨局在 `ROUND` 模式下重置为 20000，在 `SESSION` 模式下保留已消耗后的储备；
   - 验证 `open` 开启决策：3s 基础 + 20s 储备，deadline = opensAt + 23000；同 key 再次 open 返回同一对象；不同 key 同 seat 抛出 `IllegalStateException`；
   - 验证 `complete`：
     * atMillis < opensAt：耗时 0，储备保持 20000，返回 true；
     * opensAt + 2000ms：免费时间内完成，储备保持 20000，返回 true；
     * opensAt + 5000ms：超出 2000ms，储备扣减为 18000，返回 true；
     * opensAt + 23000ms (恰好 deadline)：返回 false 拒绝；
     * 重复 complete 返回 false；
   - 验证 `expire`：代次不匹配返回 false 且无副作用；代次匹配返回 true 且储备归 0；
   - 验证 `cancel`：运行 6 秒后取消，扣减 3 秒储备，储备变为 17000；
   - 验证 `pause` / `resume`：运行 2 秒后暂停 10 秒再恢复，剩余免费时间应严格保持 1 秒，总截止时间相应延后 10 秒。

2. **`TimeoutPolicyTest`**：
   - 传入包含 `Pass`、`DeclareRon`、`DeclarePon` 的响应列表，验证输出为 `Pass`；
   - 摸打回合传入包含 `DeclareTsumo`、`DeclareRiichi`、摸切牌弃牌、其他手牌弃牌，验证输出为摸切牌弃牌；
   - 副露后必须出牌（`myDrawnTile == null`），候选包含 3筒、1万、8索，验证确定性选择 1万（Tile 自然序最小）；
   - 候选仅有 `DeclareTsumo` 或 `DeclareKan`，验证输出为 `null`。

3. **`ConnectionControlTest`**：
   - `DisconnectMode.IMMEDIATE_AI`：调用 `disconnect`，座位置为 `DISCONNECTED`，角色立即变为 `TAKEOVER`，`controlEpoch` 由 0 变为 1；
   - `DisconnectMode.GRACE_THEN_AI`：调用 `disconnect`，角色保持 `HUMAN`，代次不变；时钟前进未超过宽限期调用 `checkGraceExpiration` 返回 false；超过宽限期调用 `checkGraceExpiration` 返回 true，角色变为 `TAKEOVER`，代次自增；
   - `reconnect` 与 `reclaimControl`：重连后调用 `reclaimControl`，角色还原为 `HUMAN`，代次再次自增。
