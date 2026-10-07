# 任务 ANTI-SHUFFLE-B 交付报告：洗牌千术准备阶段、权威发牌与经济整合

日期：2026-10-02  
开发者：Antigravity (Task ANTI-SHUFFLE-B 生产开发)  
协作角色：Codex（测试设计、编译构建、代码检视与验收）  
设计依据：[SHUFFLE-CONTRACT.md](SHUFFLE-CONTRACT.md) 与 [TASK-ANTI-SHUFFLE-B.md](TASK-ANTI-SHUFFLE-B.md)

---

## 1. 任务边界与交付范围

本次交付严格遵守第一波洗牌千术任务所有权与隔离约束：
- **唯一写入范围**：
  * `engine/src/main/kotlin/com/majiang/match/MatchConfig.kt`
  * `engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt`
  * `engine/src/main/kotlin/com/majiang/access/SessionAccess.kt`
  * `engine/src/main/kotlin/com/majiang/access/local/LocalSession.kt`
  * `engine/src/main/kotlin/com/majiang/round/RoundStateMachine.kt`
  * `engine/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt`
  * `engine/src/main/kotlin/com/majiang/host/TableHost.kt`
  * 本交付报告 `doc/v3-migration/delivery-ANTI-SHUFFLE-B.md`
- **零范围越权**：
  * 未修改 `engine/.../stacking/**`（严格归属 Task A 所有权）；
  * 未修改 `client/**` 与 `server/**`（严格归属 Task C 所有权）；
  * 未修改 `core/**`（严格归属 Task D 所有权）；
  * 未修改任何测试文件或执行 Gradle/Shell 终端命令（严格归属 Codex 验收权限）；
  * 保持既有 511 项存量测试与默认直接发牌模式 100% 向后兼容（`shuffleConfig.enabled = false` 保持默认）。

---

## 2. 核心架构与功能实现清单

### 2.1 赛制配置扩展 (`com.majiang.match.MatchConfig`)
- 新增 `val shuffleConfig: ShuffleConfig = ShuffleConfig()`，默认 `enabled = false`；
- 在 `validateSupported()` 中追加洗牌千术参数域合法性强校验：
  * `selectionMillis > 0L`（选择窗口时间必须为正）；
  * `plannedDraws in 0..18`（瞒天过海预设摸牌数必须在合法区间）；
  * `strength in 0.0..1.0`（千术强度必须在 [0, 1]）；
  * `interference >= 0.0`（干扰衰减系数非负）。

### 2.2 快照与端点协议 (`com.majiang.access`)
1. **`PlayerSnapshot.kt`**：
   - 构造器参数末尾新增 `val preparation: ShufflePreparationView? = null`，保持所有位置参数与旧序列化/反序列化向后兼容；
   - `deepCopy()` 补充 `preparation = preparation?.deepCopy()`，保证客户端状态投影与历史更新的防御性深拷贝隔离。
2. **`SessionAccess.kt`**：
   - 新增方法 `fun submitShufflePlan(request: ShufflePlanRequest): CompletableFuture<ActionReceipt>`；
   - 提供默认实现返回 `REJECTED(reason = "UNSUPPORTED_ACTION")`，确保 FakeSessionAccess 等存量测试用例不被编译破坏。
3. **`LocalSession.kt`**：
   - 重写 `submitShufflePlan(request)`，校验端点未关闭后转发至宿主 `host.submitShufflePlan(viewerSeat, request)`；端点关闭时返回 `REJECTED(reason = "SESSION_CLOSED")`。

### 2.3 状态机基线保护 (`com.majiang.round.RoundStateMachine`)
- 修复发牌扣费时的基线重置隐患：
  * 在 `RoundStateMachine.install(change)` 中，开局发牌动作仅在 `_accumulatedEvents.none { it is GameEvent.CheatPointsChanged } && change.events.none { it is GameEvent.CheatPointsChanged }` 时，才用发牌后的点数更新 `startingCheatPoints`；
  * 若发牌事件中包含洗牌扣费产生的 `CheatPointsChanged`，则严格保留初始局 `initialState.cheatPoints` 作为 `startingCheatPoints`，确保 `RoundResult.cheatDeltas`（计算公式为 `finalPoints - startingCheatPoints`）能真实反映洗牌扣费的负差额（如 -40、-20、-60）。

### 2.4 协调器权威发牌接缝 (`com.majiang.application.ApplicationCoordinator`)
- 构造器支持注入 `shuffleConfig: ShuffleConfig = ShuffleConfig()`；
- 新增只读并发查询方法 `fun epochFor(seat: Int): Long`，提供读锁保护下的控制代次快照；
- 在 `executeNextRoundInternal` 中，当 `shuffleConfig.enabled` 为 true 时：
  * 创建进入 `GamePhase.STACKING` 阶段但牌山为空（`wall.isEmpty()`）的全新 `RoundStateMachine`；
  * 安装 `ApprovedSessionStep.StartNextRound` 并更新 session 状态，使牌局平稳停留在准备阶段，等待洗牌窗口截止；
- 新增外部批准操作原子发牌接缝 `fun executeDealWithPreparation(roundId, wallTiles, cheatCosts, costReasons): ActionResult`：
  * **权威锁与状态守卫**：持有写锁，校验阶段为 STACKING 且当前活动局尚未发牌；
  * **136 张物理牌与不变量严格审计**：核验牌张总数为 136，且包含标准牌池（含红五与 34 种牌型配额）所有元素；
  * **原子扣费与发牌**：在同一事务候选状态中，通过 `CheatLedger` 扣除各席位已确认预留的千术费用并生成 `CheatPointsChanged` 事件；随后构造主牌山（122张）、王牌（14张）、首张宝牌指示牌（deadWall[4]），并为四家分发 13+1 张手牌；
  * **强不变量审计与原子安装**：`StateInvariants.assertValid(candidate)` 审计物理守恒与点数非负后，打包成单个 `RoundChange` 原子 `round.install`；
  * **事务回滚保证**：若牌张验证、费用超支或不变量审计失败，直接拒绝返回，绝不修改权威状态机或已扣除账本，保留原种子以备重试。

### 2.5 权威牌桌宿主完整驱动 (`com.majiang.host.TableHost`)
1. **自动经济配置预设**：
   - 当 `shuffleConfig.enabled = true` 且未显式指定 `cheatEconomy.enabled` 时，依据赛制风数自动预设：
     * 东风战 (`winds = listOf(1)`)：整场 50 点；
     * 半庄战 (`winds = listOf(1, 2)`)：整场 100 点；
     * 技能费用：`shuffle_initial = 40`，`shuffle_draw = 20`；
   - 若传入了显式 `cheatEconomy` 配置，则严格保留用户自定义配置；
   - 若 `initialState` 为已发牌状态，`isInitialStateAlreadyDealt` 标记生效，防止向预设残局逆插洗牌准备阶段。
2. **准备阶段生命周期管理**：
   - 维护独立状态：`preparationRoundId`、`prepStatus` (`SELECTING` / `GENERATING`)、`prepOpensAtMillis`、`prepDeadlineMillis`、`prepRoundSeed`、`prepConfirmed`、`prepSelections`、`prepReserved`；
   - 提供 5 个测试控制与断言公开方法：
     * `fun isPreparationActive(): Boolean`
     * `fun preparationView(seat: Int): ShufflePreparationView?`
     * `fun preparationDeadline(): Long`
     * `fun forcePreparationExpire(): Boolean`
     * `fun setPreparationDeadline(deadlineMillis: Long)`
3. **`submitShufflePlan(seat, request)` 强校验与私有预留**：
   - **重入守卫与单桌串行锁**：防止锁重入死锁；
   - **幂等与冲突处理**：按 `ShuffleReceiptKey(seat, roundId, requestId)` 记录，重试同载荷幂等返回原回执（即使在已发牌后亦然），冲突载荷拒绝（`CONFLICTING_PAYLOAD`）；
   - **身份与代次校验**：校验已连接真人（`SEAT_DISCONNECTED` / `SEAT_NOT_HUMAN`），校验控制代次（`STALE_EPOCH`）；
   - **阶段与时间校验**：必须处于当前准备阶段（`WRONG_PHASE`），未到截止时间（`PREPARATION_EXPIRED`）；
   - **重复确认锁定**：席位已确认后不可更改（`ALREADY_CONFIRMED`）；
   - **费用核验与预留**：费用必须在 0..60 且不超过当前出千点数（`INVALID_COST` / `INSUFFICIENT_FUNDS`）；
   - **原子锁定与隐私发布**：将选择记录至 `prepSelections[seat]`，标记 `prepConfirmed[seat] = true`，预留 `prepReserved[seat] = cost`；**仅递增并发布该席位自身的 `PlayerUpdate` 流**，绝不向其他席位泄漏预留或目标。
4. **权威 Tick 步进整合 (Step 0)**：
   - 在 `tick()` 开头优先判定 `isPreparationActive()`；
   - 自动为 AI 席位确认（默认不花费 `aiMaySpend = false`，选空目标）；
   - 达到固定 10 秒截止（`now >= prepDeadlineMillis`）时：
     * 将未确认席位自动归为无千术（费用 0）；
     * 状态切为 `ShufflePreparationStatus.GENERATING`；
     * 调用 Task A 的 `ShuffleWallGenerator().generate(...)`；
     * 调用 `coordinator.executeDealWithPreparation(...)` 原子发牌并扣费；
     * 成功后清理准备状态，开启 `decisionClock.beginRound(round.roundId)`，同步决策并广播 `onStateChanged`；
     * 若生成或审计失败，保持牌山与余额完全不变，保留原 roundSeed，返回 false 等待重试。
5. **准备期倒计时与决策隔离**：
   - 在 `isPreparationActive()` 期间，`buildClientDecision(seat)` 返回 `options = emptyList()`，普通麻将动作 `submitChoice` 被统一拒绝为 `WRONG_PHASE`；
   - `syncDecisionsAndClocks()` 取消所有 `decisionClock` 计时，不开启任何 3+20 倒计时，不消耗玩家思考储备；
   - `buildClockViews()` 稳定返回闲置空标记（0L, 0L）；
   - 准备倒计时完全由 `ShufflePreparationView` 的独立时间戳（`opensAtMillis`, `deadlineMillis`, `serverNowMillis`）提供；
   - `pause()` 时冻结 `serverNowMillis`；`resume()` 时将暂停流逝的时间精确补偿平移至 `prepDeadlineMillis`，严格遵循“恢复平移，不赠送时间”；
   - `applyCheatLedgerEntry` 检查当前可用余额必须满足 `available = currentPoints - prepReserved[seat]`，彻底防止并发操作挪用已确认预留的千术费用；
   - `close()` 彻底释放并清空准备阶段的所有状态集合。

---

## 3. 契约核对表

| 契约项 | 规格要求 | 实现状态 | 说明 |
|---|---|---|---|
| **配置扩展** | `MatchConfig.shuffleConfig: ShuffleConfig` | ✅ 完全满足 | 默认 enabled=false，强校验时间/摸牌/强度参数 |
| **快照字段** | `PlayerSnapshot.preparation: ShufflePreparationView?` | ✅ 完全满足 | 位于末尾，deepCopy 隔离，无准备阶段为 null |
| **异步端点** | `SessionAccess.submitShufflePlan(request)` | ✅ 完全满足 | 默认 UNSUPPORTED_ACTION，LocalSession 转发至 Host |
| **初始已发牌兼容** | 初始已有牌局不逆插准备阶段 | ✅ 完全满足 | `isInitialStateAlreadyDealt` 识别残局与夹具测试 |
| **赛制经济预设** | 50 (东风) / 100 (半庄)，显式配置保持 | ✅ 完全满足 | 启用且未显式指定时由 Host 依据 winds 自动设为 50 或 100 |
| **10秒独立窗口** | 固定 10 秒截止，不提前发牌 | ✅ 完全满足 | 全员提前确认不跳过倒计时；超时未确认自动无千术 |
| **时钟独立** | 准备阶段不使用 decisionClock 计时 | ✅ 完全满足 | decisionClock 闲置取消；pause 冻结，resume 平移，不白送时间 |
| **普通动作互斥** | 准备期 options 为空，普通动作拒绝 | ✅ 完全满足 | options 为空，submitChoice 返回 WRONG_PHASE |
| **原子扣费与发牌** | 扣费与发牌一次原子提交 | ✅ 完全满足 | 准备期仅预留，发牌时 executeDealWithPreparation 统一提交 |
| **基线不重置** | 发牌扣费保留起始点数基准 | ✅ 完全满足 | RoundStateMachine 不重写 startingCheatPoints，保留结算 delta |
| **隐私投影** | 选择与预留仅本人可见 | ✅ 完全满足 | buildPreparationViewForSeat 仅投影本座 confirmed/selection/reserved |
| **事务回滚** | 生成/审计失败不扣款且保留种子 | ✅ 完全满足 | candidate 状态丢弃，原状态与 prepRoundSeed 保持不变 |

---

## 4. Codex 验收指引

Codex 可基于本交付为 `TableHost`、`ApplicationCoordinator` 与 `LocalSession` 编写并执行针对洗牌准备阶段的验收测试套件：

1. **`TableHostShufflePreparationTest`**：
   - **赛制经济预设测试**：
     * `winds = listOf(1)` 且 `shuffleConfig.enabled = true`，启动后验证四家初始出千点数均为 50；
     * `winds = listOf(1, 2)`，验证四家初始点数均为 100；
     * 显式传入 `cheatEconomy = CheatEconomyConfig(enabled=true, initialPoints=listOf(200, 200, 200, 200))`，验证保持 200。
   - **准备阶段激活与私有视图**：
     * 调用 `startSession(0)` 后，验证 `isPreparationActive() == true`；
     * 各席位查询 `preparationView(seat)`，验证 `status == SELECTING`，`deadlineMillis - opensAtMillis == 10000L`；
     * 验证普通决策 `queryDecision(seat).options.isEmpty()`；
     * 验证普通提交 `submitChoice(seat, ...)` 返回 `ReceiptStatus.REJECTED` 且 reason 为 `WRONG_PHASE`。
   - **方案提交与幂等/隐私验证**：
     * 座位 0 提交 `ShufflePlanRequest(selection = initialTargets: CHINITSU_MAN, drawTargets: DAISANGEN)`（费用 60 点）；
     * 验证返回 `ReceiptStatus.RECORDED`，`preparationView(0).reservedPoints == 60`，`availablePoints == 40`；
     * 重复提交相同 request，验证幂等返回原回执；
     * 提交不同 selection，验证返回 `CONFLICTING_PAYLOAD`；
     * 检查座位 1 的快照或更新，验证座位 1 看到的自身 `preparationView(1).reservedPoints == 0`，绝不泄露座位 0 的选择。
   - **资金保护测试**：
     * 在座位 0 预留 60 点后（当前余额 100，可用 40），调用 `host.applyCheatLedgerEntry(entryId, 0, -50, "test")`，验证返回 `ACTION_NOT_AVAILABLE` 被拒绝，防止扣透预留款；
     * 扣除 -30 点，验证成功，可用变为 10 点。
   - **到期自动发牌与原子扣费**：
     * 调用 `host.forcePreparationExpire()`；
     * 验证 `isPreparationActive() == false`；
     * 验证活跃单局 phase 变为 `GamePhase.CHEAT_WINDOW` 或 `DISCARD`，庄家手牌为 14 张，闲家各 13 张；
     * 验证座位 0 的出千点数扣减为 40（100 - 60），其余未提交座位的点数保持 100；
     * 验证此时庄家的 `decisionClock` 正式开启，正常 3+20 倒计时启动。
   - **暂停与恢复平移验证**：
     * 在准备阶段开始 2 秒后（剩余 8 秒），调用 `pause()`；
     * 时钟推进 5 秒后调用 `resume()`；
     * 验证 `preparationDeadline()` 延后了 5 秒，剩余准备时间依然严格保持 8 秒。
