# MR-09b storedReceipts 与预览重复计算测量探针 交付说明

- **交付日期**：2026-10-18
- **开发方**：Anti
- **验收方**：Claude Code / Codex（独立检视与运行测试；不代写 `CLAUDE-MR09B-REVIEW.md`）
- **基线状态**：工作区当前状态（MR-09a 已验收未提交改动之上；本任务与 09a 文件零重叠，验收按增量 diff 隔离）
- **任务简报**：`tools/architecture/v4-improvement/ANTI-MR09B-TASK.txt`

---

## 1. 核心声明与设计定界

1. **仅交付测量仪器与测量用例**：
   - 生产代码仅增加内部只读测量指标函数与有界探针，**严禁且未实现**任何 LRU 淘汰、准入配额、预览缓存或任何形式的内存剔除逻辑。
   - 配额与缓存引入与否，严格基于本次测量的实测数据由验收方依据 `IMPROVEMENT-PLAN.md` 另行裁定。
2. **零命令执行**：
   - 本次交付全程仅使用文件读写工具，未执行任何构建命令、测试命令或 shell 操作；编译、测试与指标采集由验收方代跑。
3. **零生产行为破坏**：
   - 未改动任何 public API，未引入任何外部库依赖（仅使用 JDK 17 标准并发库），未改动任何 build 脚本。
   - `TableAuthority` 既有的 `submitChoice`、`submitAiChoice`、`querySnapshot`、`close` 等所有业务逻辑一行未改；`createPreview` 的候选推导、等值匹配与返回逻辑一行未改。
   - MR-09a 交付的 6 个文件（`GameState.kt`、`PlayerHand.kt`、`RoundTransitionPlanner.kt`、`RiichiMirrorInvariantTest.kt`、`GameStateFieldChecklistTest.kt`、`Mr09RiichiWriteFunnelScanTest.kt`）完整无损，零行改动。

---

## 2. 改动文件清单与意图

本次交付严格受限于简报第 5 节所有权清单，共涉及 4 个文件（2 修改、2 新建）：

| 序号 | 文件路径 | 变更类型 | 核心意图 |
|---|---|---|---|
| 1 | `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt` | 修改 | 在 `storedReceipts` 声明后新增只读指标 `ReceiptStoreMetrics` 数据类及只读遍历分析函数 `receiptStoreMetrics()` / `approxReceiptEntryBytes` |
| 2 | `engine/src/main/kotlin/com/majiang/session/PlayerPresentationPreviewProvider.kt` | 修改 | 在单例对象内新增调用测量计数器、有界去重集合（容量上限 8192）及快照结构 `PreviewCallMetrics`，并在 `createPreview` 首行埋点 |
| 3 | `engine/src/test/kotlin/com/majiang/authority/Mr09ReceiptAndPreviewMeasurementTest.kt` | 新建 | 测量用例套件（3 个测试）：基线单拉取构成、多拉取断线重连重复推导量化、非法 requestId 线性增长与 close 归零验证 |
| 4 | `doc/v4-重构/ANTI-MR09B-DELIVERY.md` | 新建 | 本交付说明文档，包含指标口径、代码依据、测量留空表与自查结论 |

---

## 3. 指标口径说明（公式与局限）

### 3.1 回执库测量指标 (`ReceiptStoreMetrics`)

- **数据类定义**：
  ```kotlin
  data class ReceiptStoreMetrics(
      val currentEntries: Int,
      val rejectedEntries: Int,
      val recordedEntries: Int,
      val appliedEntries: Int,
      val perSeatEntryCounts: List<Int>,
      val approxRetainedBytes: Long
  )
  ```
- **统计方式**：
  - `currentEntries`：`storedReceipts.size`。
  - `rejectedEntries` / `recordedEntries` / `appliedEntries`：遍历 `storedReceipts` 根据 `value.receipt.status` 分别累加。
  - `perSeatEntryCounts`：根据 `key.seat`（0..3）分别统计各席位累计持有的条目数。
- **保留字节近似估算公式 (`approxReceiptEntryBytes`)**：
  - `ReceiptKey`：对象头 16B + int seat 4B + requestId 引用 8B = 28B
  - `key.requestId`：String 对象头 24B + 字符数组开销 2 × length
  - `StoredSubmission`：对象头 16B + 3 个有效/固定引用 24B = 40B
  - `ActionReceipt`：对象头 16B + 4 个字段（requestId 引用 8B + status 引用 4B + reason 引用 8B + cursor 引用 8B）= 44B
  - `receipt.requestId`：String 对象头 24B + 2 × length
  - `receipt.reason`：若非空，累加 24B + 2 × length
  - 可空载荷对象（`choice`、`tileMarkPayload`、`sleeveBloomPayload`、`sleeveBloomChoicePayload`、`beamSwapPayload`、`beamSwapChoicePayload`）：按对象头 16B + 引用数 × 8B + 字符串字段（`sessionId`/`offerId` 等）24B + 2 × length 累加。
- **估算局限性**：
  - 粗粒度量级对比，不代表 JVM 实际物理堆占用（未计 HotSpot 8 字节对齐填充、Compressed OOPs 指针压缩、`ConcurrentHashMap.Node` 与内部桶数组额外开销、字符串常量池驻留与引用共享）。

### 3.2 预览提供器测量指标 (`PreviewCallMetrics`)

- **数据类定义**：
  ```kotlin
  data class PreviewCallMetrics(
      val callCount: Long,
      val nullRoundCount: Long,
      val distinctDecisions: Int,
      val identityFrozen: Boolean,
      val duplicateCalls: Long
  )
  ```
- **统计方式**：
  - `callCount`：`previewCallCount.sum()`，进入 `createPreview` 的总调用次数（使用 JDK `LongAdder` 线程安全累加）。
  - `nullRoundCount`：`previewNullRoundCount.sum()`，因 `decision.roundId == null` 早退的调用次数。
  - `distinctDecisions`：`seenDecisionIdentities.size`，有效局中不同决策身份 `"$roundId|$decisionId|$controlEpoch"` 的唯一数。
  - `identityFrozen`：当集合条目达到上限 `PREVIEW_IDENTITY_CAP = 8192` 时置为 `true`，停止向 Set 中写入，防止长时间运行内存泄露。
  - `duplicateCalls`：重复推导次数，计算公式为：
    `callCount - nullRoundCount - (if (identityFrozen) 0L else distinctDecisions.toLong())`。
- **测试隔离支持**：
  - 提供 `resetPreviewCallMetrics()` 清零全部计数器与去重集合，供独立用例前隔离。

---

## 4. 关键代码事实与实读依据

### 4.1 `submitChoice` 拒绝路径存储回执的行号依据

动笔前逐字核对 `engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt` L479-577 中的 `submitChoice` 实现，确认各分支的回执落库行为：

1. **前置边界早退（不存入回执，直接返回拒绝）**：
   - L480-482：`isClosed || coordinator.session.lifecycle == SessionLifecycle.CLOSED`（原因：`SESSION_CLOSED`）
   - L483-485：`decisionClock.isPausedState`（原因：`HOST_PAUSED`）
   - L486-488：`seat !in 0..3`（原因：`INVALID_SEAT`）
   - L496-498：相同 `(seat, requestId)` 但载荷冲突早退（原因：`REQUEST_ID_CONFLICT`）
   - L501-503：与洗牌预设 requestId 冲突早退（原因：`REQUEST_ID_CONFLICT`）
2. **业务校验拒绝（全部存入 `storedReceipts[receiptKey] = StoredSubmission(...)`）**：
   - L505-508：`isPreparationActive()` 准备阶段拒绝 -> **L507 存入 `REJECTED("WRONG_PHASE")`**
   - L513-516：`choice.controlEpoch != currentDecision.controlEpoch` 代次陈旧 -> **L515 存入 `REJECTED("STALE_CONTROL_EPOCH")`**
   - L519-522：`currentDecision.decisionId != choice.decisionId || choice.roundId != currentDecision.roundId` 决策或单局不匹配 -> **L521 存入 `REJECTED("DECISION_MISMATCH")`**
     > **用例 3 (Spam) 验证依据**：构造 `decisionId = "bogus-decision"` 时，在此处命中并在 L521 存入回执，随后于 L522 返回。
   - L525-529：`selectedOption == null` 未找到对应选项 -> **L528 存入 `REJECTED("OPTION_NOT_FOUND")`**
   - L538-542：`now >= timing.deadlineMillis` 权威时钟超时 -> **L540 存入 `REJECTED("DEADLINE_EXPIRED")`**
   - L548-555：协调器执行拒绝 (`actionResult !is ActionResult.Accepted`) -> **L554 存入 `REJECTED(reasonStr)`**
3. **成功执行生效**：
   - L562-575：状态根据响应窗口是否等待置为 `RECORDED` 或 `APPLIED`，并在 **L575 存入 `storedReceipts`**。

### 4.2 `GameAction.Pass` 实际类名与选择策略

1. **实际类名**：
   - 逐字实读 `engine/src/main/kotlin/com/majiang/state/GameAction.kt` L49，类名为：
     ```kotlin
     /** 跳过/放弃副露机会 */
     data class Pass(val player: Int) : GameAction()
     ```
   - 证实其为 `GameAction` 的密封子类，接受参数为 `player: Int`。
2. **测试驱动中的选择策略**：
   - 按照简报规范实现四级策略：
     ```kotlin
     val chosenOption = decision.options.firstOrNull {
         it.action is GameAction.DiscardTile && (it.action as GameAction.DiscardTile).tile == drawnTile
     } ?: decision.options.firstOrNull {
         it.action is GameAction.Pass
     } ?: decision.options.firstOrNull {
         it.action is GameAction.DiscardTile
     } ?: decision.options.first()
     ```
     - 优先弃掉刚摸到的牌（摸切）；
     - 若处于响应窗口则优先放弃鸣牌（`Pass`）；
     - 否则选择首个合法弃牌动作；
     - 终局/局间等其他阶段选择首个可用选项。

### 4.3 `PlayerDecision` 字段名与驱动调用行为

1. **字段名核对**：
   - 通过 `TableAuthority.kt` L513/519/2335、`PlayerPresentationPreviewProvider.kt` L32/35/36、`PlayerSnapshot.kt` L109-116、`EngineProtocolMapper.kt` L334-339 及测试夹具证实，`PlayerDecision` 包含字段：
     - `val roundId: String?`
     - `val decisionId: String`
     - `val controlEpoch: Long`
     - `val options: List<ActionOption>`
2. **`isAutomated` 驱动行为与单机同步测试 Harness 语义澄清**：
   - 实读 `TableAuthority.kt` L2170-2185 确认：当 `isAutomated == true` 时，`TableAuthority.queryDecision` 会将 `options` 统一置为 `emptyList()`。该语义系面向 `ServerTableHost` 外部异步 AI 运行器（外部运行器通过 `queryAiRequests` 获取带 view 的请求并在外部计算）；
   - 在引擎内无头的同步驱动 Harness（如本测试套件的 `drive()`）中，必须四席统一使用 `isAutomated = false`，各席位（包括 Computer 席位）才能获取真实的候选动作列表进行同步模拟驱动。
3. **`TableAuthority.start()` 启动守卫根因与无条件调用**：
   - `TableAuthority` 构造时（L102-136）内部 `ApplicationCoordinator` 已完成初始化，使 `coordinator.session.lifecycle` 初始即处于 `ROUND_ACTIVE` 状态；
   - 原守卫条件 `if (lifecycle == WAITING_START || lifecycle == CREATED)` 会误判并跳过 `start()`，导致牌墙未发且首轮即退出；
   - `TableAuthority.start(0)` 内部对于已开局状态具有幂等性保障（`isInitialStateAlreadyDealt` 分支正常返回 `APPLIED`），因此 `drive()` 统一无条件调用 `authority.start(adminSeat, clock.nowMillis())` 确保发牌。
4. **`querySnapshot` 入参类型适配**：
   - 实读 `TableAuthority.kt` L2277，签名第二项为 `syncEpoch: String`；简报正文示意中的字面量 `0L` 适配为 `"epoch-0"`，避免 Kotlin 编译器类型推导失败。

---

## 5. 测量数据留空表（供验收方实测运行后填写）

验收方运行 `./gradlew :engine:test --tests '*Mr09ReceiptAndPreviewMeasurementTest*' --info`，从 stdout 中提取 `MR09-MEAS` 行并填入下表：

| 测试用例 | 条目总数 (entries) | 拒绝条目 (rejected) | 拒绝占比 (rejectedShare) | 记录条目 (recorded) | 生效条目 (applied) | 每席条目 (perSeat) | 保留字节 (approxBytes) | 单条字节 (bytesPerEntry) | 预览调用数 (calls) | 唯一决策数 (distinct) | 重复调用数 (duplicates) | 重复率 (dup/calls) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **用例 1 (Baseline)** | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ |
| **用例 2 (Polling k=3)** | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ | _待填_ |
| **用例 3 (RequestId Spam)**| _待填 (≥500)_ | _待填_ | _待填_ | 0 | 0 | _待填_ | _待填_ | _待填_ | — | — | — | — |

---

## 6. 验收标准对照（R09b-T1～T6）

| 标准编号 | 标准要求 | 实现位置 / 支撑用例 | 说明 |
|---|---|---|---|
| **R09b-T1** | 零行为变更：生产 diff 仅两处内部指标；`:engine:test` 全量零回归 | `TableAuthority.kt` L197-254<br/>`PlayerPresentationPreviewProvider.kt` L24-70, L79 | 仅为只读采集函数与私有计数器，无状态改变，无淘汰逻辑 |
| **R09b-T2** | 真实对局测量：用例 1 跑通，`MR09-MEAS match1` 输出条目数/构成/字节 | `Mr09ReceiptAndPreviewMeasurementTest`<br/>`per match receipt store and single poll preview baseline` | 测量单局 baseline，输出完整 entries、構成、perSeat、approxBytes 与 preview 指标 |
| **R09b-T3** | 重复计算证据：用例 2 在多次拉取下 `duplicateCalls > 0` 且可量化 | `Mr09ReceiptAndPreviewMeasurementTest`<br/>`reconnect style polling measures preview recompute duplication` | 断言 `duplicateCalls > 0`，输出多次 polling 模型下的重复推导次数与对照 |
| **R09b-T4** | 增长风险量化：用例 3 实测唯一 requestId 线性增长 (≥M) 与 close 归零 | `Mr09ReceiptAndPreviewMeasurementTest`<br/>`unique requestId spam documents linear unbounded growth` | 提交 M=500 非法 requestId 断言 `currentEntries >= 500`；`close()` 后断言为 0 |
| **R09b-T5** | 守护不破坏：`-I v4-review` 套件（含 MR-09a 扫描）全绿，无镜像直写 | 静态合规性保证 | 未改动任何 GameState/PlayerHand 镜像，未引入任何违反架构依赖 |
| **R09b-T6** | 零外溢：除所有权清单外对工作区增量 diff 为空 | 仅 2 改 2 新建 | 严格限制于简报第 5 节授权清单，零跨界修改 |

---

## 7. 陷阱清单逐条自查结论

| 陷阱编号 | 自查项 | 自查结果与结论 |
|---|---|---|
| 1 | 动笔前读遍关键文件 | **已通过**：已逐字通读全部 8 处参考源文件，核对行号与方法签名。 |
| 2 | `submitChoice` 拒绝路径是否存回执 | **已核实**：详见第 4.1 节。业务校验失败均会存入回执，非法 `decisionId` 确在 L521 存入 `DECISION_MISMATCH` 回执。 |
| 3 | 只用 JDK 自带库 | **已通过**：指标实现仅使用 `java.util.concurrent.atomic.LongAdder`、`java.util.concurrent.ConcurrentHashMap` 与 `java.util.concurrent.atomic.AtomicInteger`。 |
| 4 | `recordPreviewCall` 调用位置 | **已通过**：置于 `createPreview` 第一行（L79），早于 `val roundId = decision.roundId ?: return null`。 |
| 5 | 指标函数只读性与合法性 | **已通过**：指标函数为纯只读遍历与累加；`PreviewCallMetrics` 作为 `object` 内部的 `data class` 完全符合 Kotlin 语法规范。 |
| 6 | 测试断言基于动作后数据，数值不硬断言 | **已通过**：除 Spam 的 `currentEntries >= M` 与 `close()` 归零外，所有测量值仅通过 `MR09-MEAS` 打印，仅保留合理性 sanity 断言。 |
| 7 | 驱动循环有界与超时 | **已通过**：设置测试级 `@Timeout(180)`，驱动循环硬编码 20000 次迭代上限，且无操作时即刻 break，绝无物理 sleep。 |
| 8 | `GameAction.Pass` 真实类名与策略 | **已通过**：实读确认类名为 `GameAction.Pass(val player: Int)`，策略按摸切 -> Pass -> DiscardTile -> 首选项执行。 |
| 9 | Kotlin 语法合法性自查 | **已通过**：object 内 `private const val PREVIEW_IDENTITY_CAP = 8192` 合法；data class 内无 const；模板字符未多余转义。 |
| 10 | 逐字回读改动文件 | **已通过**：三个代码文件均已调用 `view_file` 逐字回读检查，括号匹配，无无效 import，所有 KDoc 无 `*/` 字符序列。 |

---

## 8. 第 1 轮验收反馈与返修记录 (Round 1 Rework Log)

### 8.1 验收结果摘要
- **生产代码两文件**：合格（编译通过、只读指标、零行为变更、diff 与简报一致）。
- **用例 3 (Spam)**：合格（成功验证 `entries=500`、`~352B/条`，且 `close()` 后条目归零）。
- **用例 1 & 2**：初测未通过（`currentEntries == 0`，0.002s 退出，对局未真正驱动）。

### 8.2 返修项与根因记录
1. **问题 1（必修）：`drive()` 跳过 `start()` 导致无人有选项**
   - **根因**：`TableAuthority` 在初始化（L102-136）时由 `ApplicationCoordinator` 自动建局，使得 `coordinator.session.lifecycle` 初始即为 `ROUND_ACTIVE`；此时牌墙未发，`queryDecision` 全部返回 0 选项。原 `drive()` 守卫条件仅认 `WAITING_START`/`CREATED`，导致 `start()` 被跳过，驱动循环首轮因无动作即退出。
   - **修复**：修改 `engine/src/test/kotlin/com/majiang/authority/Mr09ReceiptAndPreviewMeasurementTest.kt`，移除条件判断，无条件调用 `authority.start(adminSeat, clock.nowMillis())`。
2. **问题 2（必修）：`isAutomated=true` 剥离 options 导致 Computer 席无法推进**
   - **根因**：`TableAuthority.queryDecision(seat, isAutomated)`（L2170-2185）在 `isAutomated = true` 时会强制返回 `options = emptyList()`（该语义面向 `ServerTableHost` 外部异步 AI 运行器）；引擎内同步驱动 harness 必须全部使用 `isAutomated = false`，Computer 席位才能获取实际可用选项。
   - **修复**：修改 `Mr09ReceiptAndPreviewMeasurementTest.kt`，`drive()` 中四席统一调用 `authority.queryDecision(seat, isAutomated = false)`。
3. **问题 3（注释与代码格式瑕疵清理）**
   - **修复 1**：`PlayerPresentationPreviewProvider.kt` L29 删除 `PREVIEW_IDENTITY_CAP = 8192` 行尾多余的说明性注释。
   - **修复 2**：`PlayerPresentationPreviewProvider.kt` L65 将空体 `if (!seenDecisionIdentities.add(identity) ) { ... }` 简化为 `seenDecisionIdentities.add(identity)`。

### 8.3 返修文件上下文逐字回读自查
- **`Mr09ReceiptAndPreviewMeasurementTest.kt`**：
  - L59：`authority.start(adminSeat, clock.nowMillis())` 无条件启动，入参与类型正确；
  - L66：`val decision = authority.queryDecision(seat, isAutomated = false)`，布尔字面量无误；
  - 循环守卫、四级出牌策略、`querySnapshot` 轮询及断言结构完整无损。
- **`PlayerPresentationPreviewProvider.kt`**：
  - L29：`private const val PREVIEW_IDENTITY_CAP = 8192` 声明清晰规范；
  - L57-68：`recordPreviewCall` 先做 `roundId == null` 统计早退，再做 `previewIdentityFrozen` 早退，随后通过 `val identity = "$roundId|$decisionId|$controlEpoch"` 构造并调用 `seenDecisionIdentities.add(identity)`，最后检查 cap 冻结，控制流严密且无多余语法痕迹。
- **`TableAuthority.kt`**：本轮**零改动**。
- **构建与测试**：全程**未执行任何命令**，无 shell 副作用。

---

## 9. 第 2/3 轮验收反馈与驱动模型返修记录 (Round 3 Rework Log)

### 9.1 验收结果与问题现象
- **用例 3 (Spam)**：持续保持通过并稳定产出数据（`entries=500`, `~352B/条`, `close()` 后条目归零）。
- **用例 1 & 2 现象**：测试转绿，但测量数据失真：
  - `entries=20001`，其中 `rejected=20000`、`applied=1`、`roundEnded=0`、`lifecycle=ROUND_ACTIVE`，迭代次数打满 20000 次上限；
  - 拒绝占比达到 100%，预览唯一决策仅 4 个，表明对局并未真实流转推进。

### 9.2 根因分析与真实最坏情形模型定位
1. **`submitChoice` 的 `isHuman` 门禁阻断 Computer 席位**：
   - 实读 `ApplicationCoordinator.kt` L516-518：
     ```kotlin
     // 3. 控制者身份必须为真人
     if (!controllerRegistry.isHuman(seat)) {
         return ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)
     }
     ```
   - 生产环境中，AI/托管席位由 `ServerTableHost` 在权威调度队列中通过 `submitAiChoice`（`TableAuthority.kt` L716+）异步提交，且 `submitAiChoice` **不写 `storedReceipts`**（AI 无需客户端幂等与重放回执）；
   - 测试 Harness 在用例 1 & 2 中配置了 `1 LocalHuman + 3 Computer`，但 `drive()` 统一对 4 席调用 `submitChoice`。这导致 Seat 0 摸切成功一次后，轮到 Computer 席位（Seat 1）时，其提交的选项全部被 `isHuman` 拒绝并记录为 `REJECTED` 回执，造成 20000 次空转。
2. **4×LocalHuman 驱动模型的正确性与最坏情形价值**：
   - 将四席全部配置为 `SeatController.LocalHuman()` 后，四席均具备调用 `submitChoice` 资格；摸切与响应窗口（如 Pass）均能正常 `APPLIED`，对局顺利在多巡及局间推进；
   - **真实最坏情形（Worst-Case）**：四席全为真人客户端是回执库保留条目最多、内存压力最大的极端场景（每个客户端的每个请求均入库 `storedReceipts`）。以此模型进行基线测量，对后续配额/LRU 决策具有最强安全裕度。

### 9.3 代码改动清单
- **文件**：[`Mr09ReceiptAndPreviewMeasurementTest.kt`](file:///D:/majiang/engine/src/test/kotlin/com/majiang/authority/Mr09ReceiptAndPreviewMeasurementTest.kt)
  - **用例 1（第 125-130 行）**：将 `controllers` 参数改为 4 个 `SeatController.LocalHuman()`；
  - **用例 2（第 168-173 行）**：将 `controllers` 参数改为 4 个 `SeatController.LocalHuman()`；
  - **用例 3（Spam）**：保持原状（仅对 seat 0 提交且已通过）。

### 9.4 逐字回读自查结论
- **`Mr09ReceiptAndPreviewMeasurementTest.kt`**：
  - 用例 1（L125-130）与用例 2（L168-173）的 `controllers` 列表元素均为 `SeatController.LocalHuman()`，括号闭合与逗号完整，构造函数调用匹配；
  - 未修改任何其它逻辑行；
- **其他文件守恒**：
  - `TableAuthority.kt`：**零改动**；
  - `PlayerPresentationPreviewProvider.kt`：保持第 1 轮清理后状态，本轮**零改动**；
  - MR-09a 既有文件与全量测试：**零改动**；
  - 全程**未运行任何命令**，未提交 git。
