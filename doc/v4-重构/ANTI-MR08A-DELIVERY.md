# MR-08a 交付报告：计时与身份委托 (AuthorityCountdownTracker, DecisionIdentityGuard)

**交付人**：Anti (开发者)  
**检视人**：Codex (独立检视与验收)  
**对应计划**：[IMPROVEMENT-PLAN.md](doc/v4-重构/IMPROVEMENT-PLAN.md) 第 239–248 行 (MR-08 / MR-08a) 及 9.2 (D-09) 相关架构规范  
**基线分支**：`feat/mj-tcp-migration`

---

## 1. 任务范围与交付目标

本任务为 `IMPROVEMENT-PLAN.md` 中 **MR-08：表现层与 Codec 的局部拆分** 的第一阶段子任务 **MR-08a（计时与身份委托）**。

按计划要求与用户指令：
1. 抽出纯表现层委托组件：
   - `AuthorityCountdownTracker`：封装权威时钟 (`serverNow`, `deadline`) 的本地单调时间采样与流逝推算；同一代次刷新不重置基准；暂停与超时阻断。
   - `DecisionIdentityGuard`：追踪快照代次与决策身份 (`sessionId`, `syncEpoch`, `roundId`, `decisionId`, `controlEpoch`, `cursor`, `extraToken`)；统一决策变更判断与快照一致性比对。
2. 业务模型迁移：
   - 首先迁移 `SleeveBloomChoiceModel` 与 `BeamSwapChoiceModel`，接入上述两个委托组件。
   - 保持原有不同阶段预算、候选失效、取消、费用及请求语义 100% 不变。
3. 评估 `TileMark` 与洗牌准备阶段 (`ShufflePreparationModel`)。
4. 增添有意义的独立契约与等价边界测试。
5. 遵守约束：不改动或清理工作区未跟踪文件；不 commit/push；勿运行 Gradle（由 Codex 独立串行验证与检视）。

---

## 2. 变更文件清单

### 2.1 新增源码 (Main)
- `presentation/src/main/kotlin/com/majiang/presentation/game/AuthorityCountdownTracker.kt`
  - 纯表现层权威倒计时追踪器，纯 JVM / 标准库依赖，无 engine / server / protocol / GL 依赖。
  - 负责 `sampledLocalMillis`、`sampledServerNow`、`cachedDeadline` 采样管理，提供 `remainingMillis()` 与 `isDeadlineValid()`。
- `presentation/src/main/kotlin/com/majiang/presentation/game/DecisionIdentityGuard.kt`
  - 纯表现层决策身份守卫，仅依赖 `client.api.GameSnapshotView`。
  - 负责维护 `(lastSessionId, lastSyncEpoch, lastRoundId, lastDecisionId, lastControlEpoch, lastCursor, lastExtraToken)`，提供 `hasDecisionChanged()` 与 `isSameSnapshot()`。

### 2.2 迁移修改源码 (Main)
- `presentation/src/main/kotlin/com/majiang/presentation/game/SleeveBloomChoiceModel.kt`
  - 移除内嵌的 3 个时钟字段与 6 个代次追踪字段，委托给 `countdownTracker` 与 `identityGuard`。
  - 保持其专属候选牌特征追踪 (`lastFirstTileId`, `lastSecondTileId`) 与二选一草稿语义。
- `presentation/src/main/kotlin/com/majiang/presentation/game/BeamSwapChoiceModel.kt`
  - 移除内嵌的 3 个时钟字段与 6 个代次追踪字段，委托给 `countdownTracker` 与 `identityGuard`。
  - 保持其专属手牌/槽位候选特征追踪 (`lastSelectableHandTileIds`, `lastCandidatesSlots` 等) 与双选草稿语义。

### 2.3 新增测试 (Test)
- `presentation/src/test/kotlin/com/majiang/presentation/game/AuthorityCountdownTrackerContractTest.kt`
  - 独立测试倒计时采样捕获、单调时钟推算、同一代次防刷新延长、代次变更重采样、暂停冻结与补偿恢复、异常时钟回退防护。
- `presentation/src/test/kotlin/com/majiang/presentation/game/DecisionIdentityGuardContractTest.kt`
  - 独立测试决策五大维度变更检测、快照一致性比对、cursor 推进与 extraToken 区分、reset 清空语义。
- `presentation/src/test/kotlin/com/majiang/presentation/game/Mr08aDelegationEquivalenceTest.kt`
  - 针对 SleeveBloom 与 BeamSwap 迁移后的端到端等价验证：倒计时流逝精度、断线重连采样保留（不重置预算）、候选变更草稿清空而保持模态、cancel 清空状态但不破坏会话基准。

---

## 3. 架构设计与实现取舍

1. **组合优于继承 (Composition over Inheritance)**：
   - 避免将 `SleeveBloomChoiceModel` 和 `BeamSwapChoiceModel` 强行抽象为单一通用基类（计划明文禁止“强行继承同一基类”）。
   - 各模型通过组合持有 `AuthorityCountdownTracker` 与 `DecisionIdentityGuard` 实例，暴露业务模型专属的属性和行为，对外公共契约（类名、公共属性、方法签名）保持 100% 稳定不变。
2. **专属候选特征与核心决策身份解耦**：
   - 核心决策身份 (`sessionId`, `syncEpoch`, `roundId`, `decisionId`, `controlEpoch`) 是通用权威决策属性，由 `DecisionIdentityGuard` 统一守卫。
   - 业务特定的候选失效逻辑（如 SleeveBloom 的摸牌实体变更、BeamSwap 的可选手牌/牌山槽位集合变更）保留在各自模型中，作为专属检查逻辑与核心决策变更以 `||` 组合，既消除了重复代码，又保留了领域特异性。
3. **断线重连采样保留语义对齐**：
   - 在 `BeamSwapChoiceModel` 中，断线重连时保留原有权威采样时间戳，重连后继续扣除断线期间流逝的真实时间（防止利用掉线刷新思考预算）。
   - 抽出 `AuthorityCountdownTracker` 后，该机制更加纯粹：只要属于同一代次快照 (`isSameIdentity = true`)，`updateSample` 绝不重置 `sampledLocalMillis`。

---

## 4. TileMark 与准备阶段评估报告

按计划“先迁移 SleeveBloom/BeamSwap，再评估 TileMark 与准备阶段”之指示，评估结果如下：

### 4.1 落汗绝技模型 (TileMarkSelectionModel) 评估
- **特性比较**：
  - `TileMarkSelectionModel` 同样拥有 `sampledLocalMillis`, `sampledServerNow`, `cachedDeadline`，并通过 `isDeadlineValid(now)` 判定超时；
  - 同样维护 `lastSessionId`, `lastSyncEpoch`, `lastRoundId`, `lastDecisionId`, `lastControlEpoch`, `lastCursor` 并监控 `lastSelectableIds`。
- **差异点**：
  - SleeveBloom / BeamSwap 是**权威 offer 驱动的模态**（offer 存在即占用面板，超时后仍保持占用，直至权威清除 offer）；
  - TileMark 是**玩家主动草稿交互**（点击 `begin()` 打开，点击 `toggle()` 选 4 张牌，任何 pause / disconnect / 点数不足直接清空草稿并退出模式）。
- **结论与建议**：
  - `AuthorityCountdownTracker` 与 `DecisionIdentityGuard` 完全能满足 TileMark 的需求；
  - MR-08a 先在 SleeveBloom/BeamSwap 完成验证是正确的小步切片；建议在 MR-08 后续子任务或独立微小提交中直接迁入 TileMark，保持风险单点可控。

### 4.2 开局准备阶段模型 (ShufflePreparationModel) 评估
- **特性比较**：
  - 准备阶段属于发牌前的独立生命周期，消费的是 `ShufflePreparationView`，而非 `GameSnapshotView`；
  - 无 `decisionId`、无 `clocks: List<ClockInfo>`，其时间字段为 `opensAtMillis`, `deadlineMillis`, `serverNowMillis`；
  - 倒计时为整秒向上取整 (`remainingSeconds`)，且包含 `GENERATING` 等特有状态；
  - 暂停语义不同：对局暂停时准备阶段**冻结流逝时间**（`elapsed = 0`，保留剩余秒数），而非局内决策的直接归零阻断。
- **结论与建议**：
  - **准备阶段不应强行接入 `AuthorityCountdownTracker` 或 `DecisionIdentityGuard`**；
  - 这与计划第 243 行“不同阶段的预算、候选失效、取消与费用规则留在各业务模型中，不强行继承同一基类”高度一致，强制共用只会引入无意义的抽象泄漏与特例分支。

---

## 5. 未运行 Gradle 说明

按用户指示：
> “勿运行 Gradle（Codex 将独立串行测试）”

本次交付 Anti **严格未执行任何 Gradle 构建或测试指令**。所有代码均经过细致的语法、类型系统、契约不变量与边界推演静态审查。请 Codex 在独立验收环节执行测试并验证。

---

## 6. 需要 Codex 独立检视的风险与重点清单

1. **时钟刷新幂等性**：
   - 检视 `AuthorityCountdownTracker.updateSample` 中 `isSameIdentity == true` 时保持原有 `sampledLocalMillis` 的行为，确认在同一快照重复推送到来时，思考预算不会被无限延期。
2. **断线重连时间补偿**：
   - 检视 `BeamSwapChoiceModel` 与 `SleeveBloomChoiceModel` 在掉线期间时间推进后重连的计算，确认剩余时间包含掉线时长。
3. **取消 (cancel) 与身份基准解耦**：
   - 检视调用 `model.cancel()` 时，只清除选择状态与倒计时采样（`countdownTracker.clear()`），不重置 `identityGuard`（防止同局内后续快照误判为初次进入或遗漏代次对比）。
4. **现有测试兼容性**：
   - 检视 `SleeveBloomChoiceModelContractTest`、`BeamSwapChoiceModelContractTest`、`SleeveBloomScreenContractTest`、`BeamSwapScreenContractTest` 是否能够全部无缝通过。
