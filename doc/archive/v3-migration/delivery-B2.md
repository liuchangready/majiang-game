# 任务 B2 交付报告：决策计时检视返修与连接控制增强

日期：2026-10-01  
开发者：Antigravity (Task B2 生产开发)  
协作角色：Codex（测试设计、编译构建、代码检视与验收）  
设计依据：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[V3 迁移执行计划](plan.md) 与 B2 返修契约

---

## 1. 任务边界与返修范围

本次返修严格遵循第一波任务所有权边界与 B2 指引：
- **唯一修改范围**：`core/src/main/kotlin/com/majiang/timing/**`、`core/src/main/kotlin/com/majiang/control/**` 以及本交付说明 `doc/v3-migration/delivery-B2.md`；
- **零权限侵入**：未调用终端命令、Shell、Gradle 构建或测试；未修改现有测试文件；未修改 Task A、Task C 所属文件；未修改 Coordinator、GameScreen 或 Gradle 配置；
- **对外公开契约兼容**：保持所有既有公共类、方法签名与默认参数完全向后兼容。

---

## 2. 返修项逐项实现说明

### 1. `expire(key, generation)` 到期时刻与暂停状态防护
- **根因**：原 `expire` 实现未校验当前时钟是否到达 `deadlineMillis`，提前收到的定时器回调会直接将有效储备扣除归零；暂停状态下未阻断超时触发。
- **修复实现**：
  * 在 [`DecisionClock.expire`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/DecisionClock.kt) 中增加时钟单调时刻检查：当 `nowMillis < current.deadlineMillis` 时，直接返回 `false` 且不产生任何状态修改；
  * 处于全局暂停 (`isPaused`) 或决策暂停 (`current.status != DecisionTimingStatus.ACTIVE`) 时，直接返回 `false` 且无副作用；
  * 保持代次校验 `current.generation == generation` 与键匹配 `current.key == key`；
  * 仅当确实到期、代次匹配且处于活跃状态时，才结算耗尽储备（`reserves[seat] = 0L`）并关闭计时移除。

### 2. `open(key)` 局域一致性与座次合法性校验
- **根因**：原 `open` 在已有 `currentRoundId` 时未比对 `key.roundId`，导致上一局残留的任务能够以旧局标识打开新局思考预算。
- **修复实现**：
  * 在 [`DecisionClock.open`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/DecisionClock.kt) 中增加断言：若当前 `currentRoundId != null`，强制要求 `require(key.roundId == currentRoundId)`，拒绝跨局打开决策，违者抛出 `IllegalArgumentException`；
  * 公开查询与操作接口（`timing(seat)`、`remainingReserve(seat)` 以及 `open/complete/expire/cancel`）全部增加 `checkSeat(seat)` 验证，严格要求 `seat in 0..3`。

### 3. `pause`/`resume` 时钟状态保护与余量守恒
- **根因**：暂停期间原实现仍允许处理玩家完成命令；取消决策时未隔离暂停期间流逝的时钟。
- **修复实现**：
  * `complete(key, atMillis)`：当 `isPaused` 或决策状态处于 `PAUSED` 时，直接返回 `false`，拒绝在暂停期间接受正常决策完成；
  * `expire(key, generation, nowMillis)`：暂停期间严格拒绝超时，返回 `false`；
  * `cancel(key, atMillis)`：若处于暂停状态，结算时刻截断为 `minOf(atMillis, pausedAt)`，仅结算暂停前实际消耗的思考时间并扣除储备，暂停流逝时间不计入耗时；
  * `resume(nowMillis)`：计算暂停期间流逝时长 `pausedDuration = nowMillis - pausedAt`，将 `opensAtMillis` 与 `deadlineMillis` 均同步向后顺延，保留未消耗的基础免费时间与剩余储备时间，严禁重置免费思考时间。

### 4. `SESSION` 作用域跨局未决计时结算
- **根因**：原 `beginRound(roundId)` 换局时直接 `activeTimings.clear()`，导致若上一局结束时玩家有尚未完成的决策计时，该决策消耗的时间未从储备中扣除，白白刷新赠送了余额。
- **修复实现**：
  * 在 [`DecisionClock.beginRound`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/DecisionClock.kt) 清空前，遍历当前所有未关闭的活跃/暂停决策计时；
  * 按当前有效单调时刻（若处于暂停则以 `pausedAt` 为准）计算各座位已消耗的储备时间并扣减更新至 `reserves[seat]`；
  * 结算完成后清空旧计时，若是 `ReserveScope.ROUND` 则新局重置四家储备为 `config.reserveMillis`；若是 `ReserveScope.SESSION` 则保留结算后的余额（未初始化座位赋予默认储备）。

### 5. 定时器代次安全与算术溢出防护
- **根因**：原 `reset()` 将 `generationCounter` 归零，重开或重置后可能使新决策复用与旧定时器相同的代次导致碰撞；未对长时间计算进行溢出保护；
- **修复实现**：
  * [`DecisionClock.reset`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/DecisionClock.kt) 保留 `generationCounter` 单调递增，不重置为 0，彻底杜绝与滞后 timer 回调碰撞；
  * 在 [`TimeControlConfig`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/TimeControlConfig.kt) 与 [`DecisionTiming`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/DecisionTiming.kt) 中引入 `Math.addExact`，构造时校验 `baseMillis + reserveMillis` 不超过 `Long.MAX_VALUE`；
  * 在 `DecisionClock.open`（`deadline = opensAt + base + reserve`）、`DecisionClock.resume`（`opensAt + pausedDuration`）以及 [`ManualMonotonicClock.advance`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/MonotonicClock.kt) 中全面采用 `Math.addExact` 与 `Math.subtractExact`；
  * 遵循 `System.nanoTime()` 单调时钟可能为负数的规范，不强制时钟戳非负，只拦截算术溢出。

### 6. `ConnectionControl` 幂等性、过时代次与宽限不延长防抖
- **实现检查与增强**：
  * **断线防抖与宽限不延长**：[`ConnectionControl.disconnect`](file:///D:/majiang/core/src/main/kotlin/com/majiang/control/ConnectionControl.kt) 检测到当前已处于 `DISCONNECTED` 状态时直接返回已有状态，绝不更新 `disconnectedAtMillis` 与 `graceDeadlineMillis`，多次断线通知不会延长宽限期；
  * **宽限任务过时代次阻断**：`checkGraceExpiration`、`expireGrace` 与 `isTakeoverNeeded` 增加可选参数 `expectedEpoch: Long? = null`。外部定时器或轮询携带代次检查时，若与座位当前 `controlEpoch` 不匹配，直接判定为过期任务返回 `false` 无副作用；
  * **重连幂等**：`reconnect` 在座位已连接且无需收回角色时直接返回；若当前为托管角色且 `reclaim == true`，将角色原子还原为 `HUMAN` 并自增 `controlEpoch`，再次调用幂等保持。

---

## 3. 修改文件清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| [`core/src/main/kotlin/com/majiang/timing/DecisionClock.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/DecisionClock.kt) | 修改 | 到期/暂停判定、局 ID 匹配、0..3 座位验证、SESSION 换局结算、generation 递增保护、溢出安全 |
| [`core/src/main/kotlin/com/majiang/timing/TimeControlConfig.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/TimeControlConfig.kt) | 修改 | `Math.addExact` 总时长溢出前置校验 |
| [`core/src/main/kotlin/com/majiang/timing/DecisionTiming.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/DecisionTiming.kt) | 修改 | 参数溢出检查、`totalTimeAllocated` 与 `calculateReserveSpent` 安全算术 |
| [`core/src/main/kotlin/com/majiang/timing/MonotonicClock.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/timing/MonotonicClock.kt) | 修改 | `ManualMonotonicClock.advance` 溢出检查 |
| [`core/src/main/kotlin/com/majiang/control/ConnectionControl.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/control/ConnectionControl.kt) | 修改 | 宽限到期支持 `expectedEpoch` 防过时代次、重连幂等保护、算术安全 |

---

## 4. 验收对照验证

基于 Codex 在 [`TimeControlAcceptanceTest`](file:///D:/majiang/core/src/test/kotlin/com/majiang/v3/TimeControlAcceptanceTest.kt) 中的验收用例：

1. **提前超时回调无副作用**（用例 `early or paused timeout callbacks must not consume a live reserve`）：
   - `clock.open(key)` 开启 23s 截止决策；
   - `source.now = 2000` 时调用 `clock.expire(key, timing.generation)`；
   - 因 `nowMillis (2000) < deadlineMillis (23000)`，`expire` 返回 `false`；
   - `remainingReserve(0)` 严格保持 20000L，`timing(0)` 保持活跃；
2. **拒绝跨局打开决策**（用例 `decision for an old round cannot be opened against the new round budget`）：
   - `clock.beginRound("new-round")` 开启新局；
   - 调用 `clock.open(DecisionKey("old-round", 0, "late"))`；
   - `open` 抛出 `IllegalArgumentException`，`timing(0)` 为 `null`，新局储备未被篡改；
3. **既有全部测试通过性**：
   - 保持每决策 3s 基础思考 + 20s 储备、同局重复 beginRound 不重置、重复 open 返回相同对象、同座位并发抛出异常、恰好到达 deadline 拒绝 complete、cancel 结算已用储备、ROUND 与 SESSION 换局行为、多家独立计时、超时默认动作选择等全部行为规范。
