# 单局状态机、整场 Session 与客户端：分类开发设计

日期：2026-09-30。开发由 Antigravity 并行完成，Codex 负责设计、验收测试、联调与检视。

依据：[v2 规格](../MAHJONG_STATE_MACHINE_API_SPEC.md)。本文固定本次开发的实现边界与任务交接。

## 1. 实施范围

完成现有本地游戏的真实分层迁移：新路径必须成为默认游戏路径。不能只给旧的巨型 GameStateMachine 套上三个新类，也不能保留两套麻将算法。保留既有规则与必要回归测试，旧入口可以作为轻量兼容适配器。

本轮提供本地多客户端端点、统一查询与动作入口、独立验证、单局实例隔离、跨局结果交接、请求幂等与旧决策拒绝。远端使用同一端点契约；实际网络服务器、账户认证服务、持久化存档与跨进程事务不在本次实现范围，不得宣称已实现。必须明确能力限制。

## 2. 三类领域职责

| 分类 | 实现 | 责任 |
|---|---|---|
| 单局 | com.majiang.round.RoundStateMachine | 只保存并应用本局状态转移，终态固定 ROUND_END；每次发牌新实例、新 roundId |
| 单局外部服务 | RoundCommandProcessor / RoundRuleService / RoundTransitionPlanner | 动作验证、候选生成、规则与算分计算、准备本局变更；重用已有算法 |
| 整场 | com.majiang.match.MatchSession | 局间账本、进度、活动局引用、单局历史、整场完成；不调用验证和规则服务 |
| 应用层 | com.majiang.application.ApplicationCoordinator | 单桌串行、外部验证、审计、提交、单局结果交接、共享事件发布 |
| 应用外部组件 | 控制者注册表、自动策略、请求/决策注册表、结果验证器 | 控制代次、AI、立直自动摸切、幂等、时效、合法性和守恒 |
| 客户端 | com.majiang.client.PlayerEndpoint / LocalPlayerEndpoint | 查询投影视图与候选、提交选择、订阅；端点关闭不关闭整场 |
| 兼容 | GameStateMachine / GameSession / LocalGameSession | 只转发新实现，保持旧调用者与测试可迁移；不能留下原巨型引擎 |

Round 和 MatchSession 内不能包含权限、合法动作生成、守恒审计、AI 或 MatchProgression 判断。审批与审计在外部进行，内部 prepare/install 只对已批准变更工作。单局规则服务可以准备计算好的牌张变化与结算效果；不能把完整旧引擎作为隐藏依赖。

## 3. 并行任务与文件所有权

### R：单局状态机与独立单局服务

只新增/修改 `core/src/main/kotlin/com/majiang/round/**`；必要时修改 `state/ResponseWindow.kt` 添加防御复制功能。不得修改旧 GameStateMachine、其他 state 文件、match、application、client、session、screen 或测试。

从 `build/round-session-split/GameStateMachine.before.kt` 提取本局逻辑。移出控制者与 AI、身份判断、整场推进、和止、外部事件发布；所有候选计算与响应窗口变更隔离于权威状态，拒绝或外部审计失败时不得留下变化。规则校验放独立 RoundRuleService；规则与算分复用旧实现；MatchProgression 不得出现在 round 包。

### S：整场 Session、应用协调器与旧引擎兼容层

只新增/修改 `match/MatchSession.kt`、`application/**`、`state/GameStateMachine.kt`。可新增其他 match 文件，不修改现有 MatchProgression 算法。不得修改 round、client、session、screen 或测试。

把旧引擎改成轻量代理，构造参数与现有公开入口尽量兼容。Session 独立保存整场状态，外部协调器调用 MatchProgression。每局终态生成结果并且只交接一次；连庄也创建新实例；结算和供托不得重复加减。活动局账本来自 Round，局间账本来自 Session。整场完成时不能把旧 Round 改为 MATCH_END，客户端组合视图可以显示 MATCH_END。

移出并保留逐座位控制者、顺序 AI 调度、立直摸切暂停选择、错误 AI 安全兜底、重入拒绝等功能。客户端订阅必须是共享协调器级广播。补齐基于决策标识和控制代次的 submitChoice，在同一串行边界检查与执行，不接受过期选择。

### C：客户端接口、默认接入与兼容适配

只新增/修改 `client/**`、`session/GameSession.kt`、`session/LocalGameSession.kt`、`screen/GameScreen.kt`；可以修改 session 投影文件但尽量保持 API。不得修改 round、application、match、GameStateMachine 或测试。

提供 PlayerEndpoint 与 LocalPlayerEndpoint，GameScreen 面向 PlayerEndpoint。旧 GameSession 继承新 PlayerEndpoint，LocalGameSession 是兼容薄适配。默认游戏实际使用新 Coordinator + MatchSession + Round 路径。复用现有 PlayerView / SessionUpdate 的脱敏和快照，不把可变 GameState 暴露给客户端。多个端点可以连接同一协调器，任一端点动作必须更新所有订阅端点；关闭一个端点不影响其他端点或比赛。正常 UI 提交通过候选选择接口进行，旧 submit(GameAction) 仅为经过相同校验的兼容入口。

### 依赖与冲突处理

R、S、C 同时启动，按下面固定契约独立开发。不要为解决暂时缺失的符号而修改其他任务文件。交付后 Codex 构建并反馈联调修复。开发禁止执行 shell、构建和测试，禁止修改测试；使用 Antigravity 内置读写与搜索工具。

## 4. 固定跨任务接口

下列接口是任务间约定。实现者可以增加辅助类型，但不要任意改名、参数或可见性；发现缺项在交付说明中写明。

### 4.1 R 提供的 com.majiang.round API

```kotlin
class RoundStateMachine(initialState: GameState, val roundId: String = UUID.randomUUID().toString()) {
    internal val state: GameState // 初始局可以保留传入引用以兼容旧 fixture；客户端永不可访问
    val version: Long
    val responseWindow: ResponseWindow? // 防御复制
    val result: RoundResult? // 防御复制
    fun readState(): GameState // 深复制
    internal fun prepare(step: ApprovedRoundStep): RoundChange
    internal fun install(change: RoundChange) // 外部验证和审计后调用
}
data class RoundResult(
    val resultId: String, val roundId: String,
    val outcome: RoundOutcome,
    val startingScores: List<Int>, val finalScores: List<Int>, val totalDeltas: List<Int>,
    val startingRiichiSticks: Int, val remainingRiichiSticks: Int,
    val events: List<GameEvent>
)
// RoundChange 提供以下只读属性，内部构造，隔离候选与已安装数据
// roundId: String; sourceVersion: Long; state: GameState;
// responseWindow: ResponseWindow?; result: RoundResult?; events: List<GameEvent>
sealed interface RoundPreparation {
    data class Ready(val change: RoundChange) : RoundPreparation
    data class Rejected(val reason: RejectionReason) : RoundPreparation
}
class RoundCommandProcessor(
    val ruleEngine: RiichiRuleEngine = RiichiRuleEngine.INSTANCE,
    val winningScoreEvaluator: WinningScoreEvaluator = WinningScoreEvaluator.INSTANCE,
    val settlementService: SettlementService = SettlementService.INSTANCE,
    val random: Random = Random.Default
) {
    fun legalActions(round: RoundStateMachine, seat: Int): List<GameAction>
    fun prepare(round: RoundStateMachine, action: GameAction): RoundPreparation
}
```

R 的 processor 不含 AI、控制者、身份、审计和事件总线；是纯单局外部规划与校验服务。它拒绝 DeclareAgariYame，以及对已结束 Round 的所有操作（包括 StartStacking）。StartStacking 只允许尚未发牌的新实例，完成后规则内部自动摸牌等机械步骤沿用原行为。

RoundChange 应覆盖整个操作的所有机械后续（如出牌后建立窗口，无响应则摸下一张），但不包含 AI 决策。响应窗口必须深复制，包括候选集合和响应记录，不能通过 prepare 先修改原窗口。所有本局结束都产生 RoundResult，含完整本局累计事件 / 结算信息；totalDeltas 从本局起始积分计算，含立直扣点。

Round 统一使用 CHEAT_WINDOW 作为已摸牌等待动作阶段即可；具体座位的人类/AI 身份属于外部，不再让 Round 决定人机阶段差别。如旧 UI 依赖 DISCARD，保持两者语义兼容即可。

### 4.2 S 提供的应用层 API

`ApplicationCoordinator` 保留旧引擎所需能力，可由 `GameStateMachine.coordinator` 获取。C 必须依赖以下接口，不自造重复协调器：

```kotlin
class ApplicationCoordinator(
    initialState: GameState = GameState(),
    eventBus: GameEventBus = GameEventBus(),
    autoPlayAi: Boolean = true,
    autoRiichiDiscard: Boolean = true,
    random: Random = Random.Default,
    matchProgression: MatchProgression = MatchProgression()
) {
    val session: MatchSession
    val activeRound: RoundStateMachine?
    val hasControllerConfiguration: Boolean
    fun controllerFor(seat: Int): SeatController
    fun initializeMatch(controllers: List<SeatController>)
    fun initializeMatch(humanIndex: Int = 0, aiStrategies: Map<Int, AIPlayer> = emptyMap())
    fun setSeatController(seat: Int, controller: SeatController): ActionResult
    fun queryView(seat: Int): PlayerView
    fun legalActions(seat: Int): List<GameAction> // 规则候选，不屏蔽 AI；客户端自行按控制者过滤
    fun start(): ActionResult
    fun nextRound(): ActionResult
    fun submitPlayerAction(seat: Int, action: GameAction): ActionResult
    fun handleAction(action: GameAction): ActionResult // 可信兼容入口，仍走外部规则校验
    fun advanceAutomaticActions(): ActionResult
    fun subscribe(listener: (List<GameEvent>) -> Unit): AutoCloseable // 每个顶层提交结束广播，回调隔离
    fun queryDecision(seat: Int): PlayerDecision
    fun submitChoice(seat: Int, choice: ActionChoice): ActionResult
}
data class PlayerDecision(val roundId: String?, val decisionId: String,
    val controlEpoch: Long, val options: List<ActionOption>)
data class ActionOption(val optionId: String, val action: GameAction)
data class ActionChoice(val requestId: String, val roundId: String?,
    val decisionId: String, val controlEpoch: Long, val optionId: String)
```

这些 DTO 属于 application 包，由 S 创建。ActionOption.action 必须深复制，客户端不得修改后影响注册候选。Session 范围选项例如和止，其 roundId 为 null，不能转发给旧 Round；局内选项携带当前 roundId。现有 start/nextRound 通过外部 Session 命令校验提供兼容。

同一响应窗口中别家提交动作不能让尚未响应座位的 decisionId 失效；自己已响应、窗口变化、局变化或控制代次变化必须使旧选择失效。requestId 按座位作用域去重，相同载荷返回原回执、不同载荷拒绝且零副作用。订阅不得因去重而重复发布。批处理提交后统一广播，旧 eventBus 保留受保护的已提交事件发布。

S 的 GameStateMachine 维持旧构造参数和公开字段 / 方法的兼容代理，内部不得保留原 1400 行规则。源码基线保存在 build/round-session-split/GameStateMachine.before.kt。从前由局内直接触发的 match progression 改为协调器识别 RoundResult 后交给 Session。

### 4.3 C 的端点接口

PlayerEndpoint 至少保留 `viewerSeat/view/legalActions/start/submit/nextRound/subscribe/close`，增加 `queryDecision()/submitChoice(choice)`，以及能力说明（可用简单结构声明 local=true、remoteTransport=false、persistence=false）。为兼容 FakeGameSession 等测试替身，新能力可给显式不支持的默认实现，不能伪装可用。

LocalPlayerEndpoint 构造参数固定为 `viewerSeat: Int = 0, coordinator: ApplicationCoordinator = ApplicationCoordinator(), controllers: List<SeatController>? = null`。LocalGameSession 保留原构造参数并转发 engine.coordinator。初始化已有明确控制配置时不得覆盖；默认 2 号观察座位仍应按旧约定让 2 号为真人，其余 AI。

所有入口对关闭、绑定座位、系统动作与控制权保持现有拒绝语义；规则与请求验证在 Coordinator 外部验证组件完成，端点不复制麻将合法性算法。queryView/decision 的组合须防止版本撕裂。屏幕保持现有输入动画闸门、听牌选择、副露组合、振听和详细结算展示。

## 5. 验收和保留测试

既有规则、物理守恒、响应优先级、振听、立直、算分、隐私投影、控制者和输入闸门测试全部保留。只有旧测试明确绑定“复用旧局对象”这种被新设计废除的行为时，Codex 才调整为新边界断言，不能为通过测试删除断言或关闭验证。

新增验收用例覆盖：

1. Round.prepare 无副作用；外部审计失败不提交；结束后不能重新开始或和止。
2. Session 连庄也创建新 Round；旧 Round 终态、积分与结果保持不变。
3. 单局结果交接只进行一次；局中立直扣点与供托不重复计入。
4. Session 终局与 Round 终局分离，尾庄和止只作用于 Session。
5. 同一 Coordinator 的两端点同步更新；端点关闭与回调异常不影响另一端点。
6. 旧局、旧窗口、旧控制代次动作拒绝；多人响应互不误作废。
7. 请求重放不重复出牌或发布事件，载荷冲突拒绝。
8. 默认 GameScreen 走新路径，快照和候选不可改动权威数据。
9. 源码依赖边界检查确保 Round / MatchSession 不包含规则验证、AI、审计或网络依赖。

验证顺序：新契约测试 → 全量 core 测试 → 桌面编译及 Android Debug 构建 → 实际桌面冒烟。网络与持久化未实现的能力不纳入完成声明。

## 6. 交付与审查

三个任务各写 `doc/round-session-split/delivery-R.md` / `delivery-S.md` / `delivery-C.md`，记录变更、契约偏差和限制。Codex 记录验收结果到 REVIEW.md。未通过构建或验收时继续向 Anti 下发具体修复任务，不以未经验证的交付报告当作完成。
