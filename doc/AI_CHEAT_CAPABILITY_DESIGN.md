# AI 千术能力扩展设计

## 目标与现状

让电脑玩家在合法时机主动使用现有千术，并使后续新增千术在注册能力适配器后自动进入 AI 的可选行动集合。AI 与真人遵守同一套权威规则、点数结算、超时和物理守恒约束。此文是实施设计，尚未宣称功能已实现。

当前 `AIStrategy.decideAction(PlayerView, List<GameAction>)` 只能选择普通麻将动作。`GameAction` 不包含千术，落汗、袖里藏花、偷梁换柱分别通过 `TableAuthority.submit*` 提交；袖里藏花和偷梁换柱还存在激活后的选择阶段。`ServerTableHost.syncDecisionsAndClocks()` 只调度普通动作，遇到千术待选 offer 会跳过 AI 请求。洗牌准备阶段由 `ShufflePreparationHandler.step()` 对原生 AI 按点数固定选方案，托管座位默认跳过，且未使用同一个 AI 策略接口。

## 设计边界

- `engine` 保存规则、各千术的权威入口与玩家私有投影；AI 只接收自身座位可见的不可变数据。不得给策略传 `GameState`、牌山顺序或其他玩家暗牌。
- `server` 保持每桌串行权威裁决；在串行队列取得一致的只读投影，在异步 AI 线程生成候选并评分，提交回串行队列，复用现有 `TableAuthority.submit*` 校验与扣费。
- `client`、`presentation` 和现有 `SessionApi` 不因 AI 内部决策增加依赖；真人提交路径保持原契约。
- AI 千术开关由房间配置和策略配置控制。`aiMaySpend` 的洗牌语义需要并入统一的允许花费策略，迁移时保留旧配置的实际效果。原生电脑与掉线托管可分别配置预算，托管默认不主动花点，避免改变既有体验。

## 一、统一的 AI 决策输入和输出

保留现有 `AIStrategy` 供旧策略使用，新增一层兼容的 `AiDecisionStrategy`。策略看到一次完整的本座位决策快照，以及同一快照生成的候选动作；输出一个候选 `optionId`，不直接构造千术请求。

```kotlin
data class AiDecisionContext(
    val view: PlayerView,
    val privateCheatView: AiCheatView, // 仅本座位的已授权信息、点数与风险
    val roundId: String,
    val decisionId: String?,
    val controlEpoch: Long,
    val deadlineMillis: Long?,
    val candidates: List<AiCandidate>
)

data class AiCandidate(
    val optionId: String,             // 当前决策内唯一，提交时重新定位
    val capabilityId: String,         // "mahjong" 或稳定的千术 ID
    val phase: AiPhase,
    val cost: Int,
    val risk: AiRisk,
    val estimatedEffect: AiEffect,    // 仅基于可见信息的效果估计
    val terminalForWindow: Boolean,   // 执行后是否结束当前决策窗口
    val parameters: AiVisibleParams    // 不含隐藏牌或可变领域对象
)

interface AiDecisionStrategy {
    fun choose(context: AiDecisionContext): String? // 返回 optionId；null 表示使用安全兜底
}
```

`AiCandidate` 是内部候选契约，不直接作为网络 DTO。策略对普通动作和千术使用相同的排序入口：先筛掉非法或超预算候选，再比较和牌收益、向听改善、点数成本、暴露风险和将来保留点数的价值。对阶段性千术，激活与后续 offer 选择是两个独立决策，策略可以在第二次观察权威投影后继续选择。`BasicAIStrategy`、`TsumogiriAIStrategy` 通过包装器继续工作，未配置新策略的座位行为保持原样。

## 二、千术能力注册表

在 `engine` 定义 `CheatAiCapability` 和只读 `CheatAiRegistry`。每种千术提供一个适配器；组合根注册适配器后，通用 AI 调度器遍历注册表生成候选，不在 AI 策略或调度器内增加 `when (千术类型)`。注册表启动时校验 ID 唯一、候选数量上限、阶段和提交入口；适配器未注册则不会对 AI 暴露。新增千术的开发验收必须包含 AI 适配器或显式声明暂不支持 AI。

```kotlin
interface CheatAiCapability {
    val id: String                 // 稳定 ID，日志与配置使用；不可复用旧 ID
    val version: Int              // 候选语义改变时递增
    fun describe(): AiCapabilityDescriptor
    fun candidates(view: AiSeatSnapshot, limits: AiCandidateLimits): List<AiCandidate>
    fun submit(seat: Int, selected: AiCandidate, stamp: AiDecisionStamp, now: Long): MutationResult
}
```

`AiSeatSnapshot` 必须由权威层一次性按座位投影，包含当前 `PlayerSnapshot` 可见的洗牌准备、落汗、袖里藏花、偷梁换柱私有视图和自身点数；对外冻结深拷贝。AI 内部投影应取真实的本座位合法决策，不能直接复用 `querySnapshot()` 对自动座位清空 `decision.options` 后的结果。候选适配器只能读取该投影，并复用各千术已有的可用性判断或提交校验，避免 UI、AI 两套规则漂移。`submit()` 仅在串行队列内运行，按 `capabilityId + optionId` 解析选中项，再调用已有 `TableAuthority.submitShufflePlan` / `submitTileMark` / `submitSleeveBloom*` / `submitBeamSwap*`。适配器不得直接改 `GameState`、账本或牌池。

注册入口采用显式装配：默认构造四个内置适配器（洗牌准备、落汗、袖里藏花、偷梁换柱），未来千术在组合根追加一个适配器即可进入候选集。避免依赖 Android 环境下不稳定的类路径自动扫描。“自动暴露”指注册后自动进入 AI 决策，不是仅新增规则文件便无条件执行。

## 三、现有千术候选及阶段

| 能力 | 可见与可选时机 | AI 候选 | 提交后续 |
| --- | --- | --- | --- |
| 洗牌准备 | `STACKING` 且本座位未确认 | 跳过、预算内合法目标组合 | 走现有预留与发牌时扣费；替换固定点数档位选择 |
| 落汗 | 私有 `TileMarkView` 表示窗口可用 | 不使用、按自身已知实体选择标记或合法管理操作 | 走 `submitTileMark`，维持标记上限、点数与透视边界 |
| 袖里藏花 | 本回合激活窗口；或本人弃牌后签发的他家回合预约窗口 | 不使用、激活；offer 出现后选择保留/交换及合法候选 | 分别走 `submitSleeveBloom*`，offer 未解决前不提交普通出牌 |
| 偷梁换柱 | 自身私有视图所示可激活窗口 | 不使用、激活；offer 出现后选择原牌/换牌及合法目标 | 分别走 `submitBeamSwap*`，不得读取未授权的牌山实体 |

候选生成应限制数量，特别是目标牌、牌型组合。适配器在当前视图内先挑选少量有价值的目标，并总是提供“不使用/保留原状”的安全选项。若候选不足以表达一个千术的全部合法选择，可将“生成候选”分成按阶段的受限搜索；不得用隐藏信息补全。`estimatedEffect` 是启发式预测，不承诺结果。

## 四、调度、时效与兜底

1. 在桌级串行队列读取同一时点的 `AiSeatSnapshot` 和 `AiDecisionStamp(sessionId, roundId, decisionId?, controlEpoch, phase, stateVersion, offerId?, windowId?, deadline)`；同一座位同一 stamp 仅派发一次。
2. 异步线程基于已冻结投影调用注册表生成不可变候选、让策略排序并返回 `optionId`，不持有权威对象，不执行提交。
3. 结果回到串行队列时先按当前状态重取 stamp。逐项验证 `roundId`、`decisionId`（适用时）、`controlEpoch`、阶段、版本、offer/window ID、deadline 和座位控制权；过期结果直接丢弃。千术请求使用稳定幂等 `requestId`，由 stamp 与 `optionId` 派生；只对已知同一请求重放回执，不把未知结果当成功。
4. 通过对应权威入口提交。`APPLIED`、`RECORDED`、`REJECTED` 分开处理：准备方案的 `RECORDED` 是已确认，offer 激活 `APPLIED` 后应在新快照上再次调度选择；拒绝后刷新候选，限定重试次数，不能在同一窗口无限循环。
5. 超时先于 AI 提交裁决。策略异常或无合法千术候选时采用现有普通动作安全兜底；offer 已出现时采用对应千术的“保留/放弃”兜底，禁止直接摸切跳过 offer。暂停、控制权收回、跨局、回合结束均使旧结果失效。

准备阶段目前由 `checkPreparationStep()` 自动确认 AI。实施时改为“策略提交优先、到截止时间仍未确认则按现有默认跳过”，并保证四座位都确认或截止后才发牌。真人和托管原有超时行为仍由权威层统一处理。

## 五、策略参数与平衡

新增配置 `AiCheatPolicy`，至少包含 `enabled`、`maxSpendPerRound`、`reservePoints`、`riskTolerance`、`candidateLimit` 和每个 `capabilityId` 的开关。点数、怀疑度和使用次数仍由现有权威规则最终裁决。策略评分可以使用：

`收益 = 和牌/向听预期改善 - 点数机会成本 - 被察觉风险 - 未来窗口保留价值`。

使用固定种子的随机微扰避免完全机械，但随机数只影响候选排名，不改变规则或物理结果。记录每次 AI 决策的 capability ID、候选摘要、分数、选择、提交回执和拒绝原因；日志不得包含他家暗牌、未公开牌山或内部完整状态。

## 六、实施顺序与验收

1. 加 `AiDecisionContext`、候选/注册表及旧策略兼容包装；先让普通动作经统一入口，确认行为不变。
2. 接入洗牌准备与三种现有千术适配器，逐个复用现有私有投影和 `TableAuthority.submit*`；将 offer 后续选择纳入再次调度。
3. 在 `ServerTableHost` 加统一异步任务与串行提交，不再仅依赖 `GameAction`；保留 `roundId/decisionId/controlEpoch` 检查，扩展阶段、版本及 offer/window 检查。
4. 增加策略和房间配置，原生 AI 可启用千术；托管默认遵循原有不花点行为，明确启用后才可花点。
5. 自动化验收覆盖：每种现有千术可被 AI 看见并成功执行；点数不足或禁用时不出现；offer 完成/超时/拒绝后的回退；跨局与收回控制权丢弃旧结果；136 张物理守恒与账本一致；没有对手暗牌或牌山泄露；加入一个测试用新千术适配器后，通用调度器和策略无需修改即可发现并选择。

完成条件：AI 可以在合适时机选择普通动作或任一已注册千术；新增千术只需实现权威规则、私有投影和 `CheatAiCapability` 并注册，通用 AI 决策与调度代码无需新增分支。
