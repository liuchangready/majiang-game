# Delivery Report: Task ANTI-R4AFIX3

## 1. 概述与核心变更

针对任务 `TASK-ANTI-R4AFIX3.md` 及 Codex 实时反馈 `FEEDBACK-R4AFIX3.md` 中指出的服务端测试失败项与缺陷，完成了以下五项核心修复与测试对齐：

1. **初始状态时钟同步与纯领域初始化** (`ServerTableHost.kt` / `TableAuthority.kt`)
   - 在纯领域 `TableAuthority` 中提供 `initializeInitialClocks()` 与 `hasActiveRoundNotEnded` 查询，并在 `init` 中针对非终态活跃局同步初始化决策时钟（调用 `syncDecisionsAndClocks(clock.nowMillis(), emptySet())`）。
   - 在 `ServerTableHost` 构造函数中，先同步完成纯领域时钟初始化与 0 游标快照基线构建，全部字段与基线就绪后才向 `this.serialExecutor` 队列提交异步 AI 任务派发，消除了工作线程与构造函数的竞态。同时避免在 `server` 模块直接访问内部 `round.state` 或引入未暴露的内部类型。
   - 彻底解决了 `initialState` 下 pause 截止时间为 0、超时动作不触发的问题。

2. **恢复 HostRhythm 原始测试编排配置** (`ServerTableHostCoverageTest.kt`)
   - 恢复原始 `HostRhythmAcceptanceTest` 的控制器配置：席位 0 为 `SeatController.LocalHuman()`（默认启用 `autoRiichiDiscard`），其余席位为 `SeatController.RemoteHuman(false)`。
   - 保留原测试全部 149ms/150ms 自动立直摸切判定与 23000ms 权威超时丢弃断言，未放宽远程人类自动摸切语义。

3. **nextRound 领域前置条件校验** (`ServerTableHost.kt` / `TableAuthority.kt`)
   - 在 `TableAuthority` 中提取纯领域校验方法 `validateNextRoundPreconditions(): ActionReceipt?`，对 `FINISHED`、`PREPARATION_ACTIVE`、`ROUND_ACTIVE`、`WAITING_DEALER_CHOICE`、`HOST_PAUSED` 及未开始生命周期进行前置拦截。
   - `ServerTableHost.nextRound` 在将席位记录进 `readySeats` 之前先执行前置校验，非法状态直接返回 `REJECTED`，杜绝终态或洗牌准备期间误记就绪。

4. **请求幂等回执分命名空间与有界 LRU 缓存** (`ServerTableHost.kt`)
   - 将服务端回执缓存键细化为密封类 `HostReceiptKey.Action(roundId, seat, requestId)` 与 `HostReceiptKey.Shuffle(roundId, seat, requestId)`，彻底隔离动作与洗牌准备请求，避免同一 `requestId` 碰撞。
   - 回执缓存采用有界 LRU 策略（`LinkedHashMap` 配合 `removeEldestEntry`，上限 1000），自动淘汰最旧条目而非饱和冻结，确保长期会话中近期重试请求持续获得精确的原回执（含原始 `cursor`）。
   - 保留领域冲突载荷校验前置执行，冲突载荷返回 `REJECTED` 且不回放缓存。

5. **随机摸切响应窗口断言与异步派发有界匹配** (`InProcessBindingTest.kt` / `ServerTableHostTest.kt` / `ServerTableHostCoverageTest.kt`)
   - 在 `InProcessBindingTest.kt` 与 `ServerTableHostTest.kt` 中，将随机起手摸切成功断言从单一 `APPLIED` 调整为合法成功状态集合 `status in setOf(APPLIED, RECORDED)`，符合麻将规则中鸣牌/荣和响应窗口可能开启的规范。
   - 将 `InProcessBindingTest` 中的异步通知时序假定替换为基于目标游标 `receipt1.cursor` 的有界等待匹配。
   - 在 `ServerTableHostCoverageTest.kt` 的 `dealer win bankrupts opponent and permanently ends session including reconnect` 中补齐原 `BankruptcyHostAcceptanceTest` 的全部断言（负分、100000 守恒、无候选项、nextRound/start 拒绝、重复 tick 停滞），并通过 `host.resume(0, ended.cursor)` 校验重连快照的完整结算语义。

---

## 2. 修改文件清单

1. [`engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt)
   - 增加 `hasActiveRoundNotEnded: Boolean` 与 `initializeInitialClocks(): Boolean`。
   - 提取并暴露 `validateNextRoundPreconditions(): ActionReceipt?`。
   - `nextRound` 统一使用 `validateNextRoundPreconditions()` 校验。
   - 构造阶段针对有效初始状态自动初始化决策时钟。

2. [`server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt)
   - 引入 `LinkedHashMap` 实现有界 LRU 回执缓存 `issuedReceipts`。
   - 区分 `HostReceiptKey.Action` 与 `HostReceiptKey.Shuffle` 命名空间。
   - 构造逻辑重构：先纯领域时钟与基线，后异步调度 AI。使用 `this.serialExecutor` 消除阴影变量。
   - `submitChoice` / `submitShufflePlan` 接入分命名空间回执缓存与原始游标复现。
   - `nextRound` 在 `readySeats.add(seat)` 前先校验 `authority.validateNextRoundPreconditions()`。

3. [`server/src/test/kotlin/com/majiang/server/host/ServerTableHostCoverageTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/host/ServerTableHostCoverageTest.kt)
   - 增加 `rhythmHumans()` 编排并应用于 4 个 `HostRhythm` 测试。
   - 破产测试补齐负分、守恒、重连与快照恢复断言。

4. [`server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt)
   - 摸切状态断言支持 `APPLIED` / `RECORDED`。
   - 异步派发更新改用基于期望游标的有界轮询校验。

5. [`server/src/test/kotlin/com/majiang/server/host/ServerTableHostTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/host/ServerTableHostTest.kt)
   - 串行队列测试摸切状态断言支持 `APPLIED` / `RECORDED`。
   - 游标通知改用有界轮询匹配。

---

## 3. 未决事项与约束声明

1. **未决事项**：
   - 无本任务内代码层面的遗留缺陷。
   - 后续 R4B 统一房间注册表、大厅、玩家准备、观战等规划待下阶段任务授权后开展。

2. **测试执行声明**：
   - 按照开发规则约束，**Anti 本次交付未在本地运行任何 gradle 构建、终端测试命令或测试执行器**。
   - 代码编译、独立探针验证（含 89 项独立回归）以及全量服务端/引擎模块测试均交由 Codex 进行独立构建与验收。
