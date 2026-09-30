# 任务 D 交付报告 (Delivery D - 第二轮返修)

- **开发者**：Antigravity (Task D Sole Developer)
- **审查与验收方**：Codex
- **日期**：2026-09-29
- **阶段**：第二轮并行返修 (Round 2 Rework)
- **前置审查基准**：Codex 审查意见 `doc/architecture-v2/review/D.md` (涵盖核心事务重入、响应窗口齐备后裁决、抢杠/头跳仲裁、加杠/暗杠预校验、守恒与立直棒清零、两立直/里宝牌上下文、UI 动作与会话解耦)

---

## 1. 完成项与独占修改源文件清单

依据 `TASKS.md` 第二轮并行返修文件所有权划分：
- 审查第 8 项由任务 B 独占修改（`WinningScoreEvaluator.kt`）；
- 审查第 6、7 项由任务 C 独占修改（`GameEventBus.kt`、`SessionUpdate.kt`、`LocalGameSession.kt`）；
- **任务 D 严格遵守所有权边界，未编辑属于 B 和 C 的任何文件**。
- 任务 D 独占负责其余审查项，完成以下 5 个生产文件的修复与完善：

### 核心修改生产文件清单

1. [`core/src/main/kotlin/com/majiang/state/GameStateMachine.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/GameStateMachine.kt)
   - **统一响应窗口收集与唯一裁决（审查 1）**：
     - 将 `RESPONSE_WINDOW` 与 `CHANKAN_WINDOW` 阶段的 `Pass`、`DeclareRon`、`DeclarePon`、`DeclareChi`、`DeclareKan (OPEN_KAN)` 统一收敛至 `handleResponseWindowAction` 分支。
     - 动作先记录至 `ResponseWindow.recordResponse`；在全部有资格座位响应齐备前，保持 `phase`、`scores` 与牌桌状态完全不变，不触发结算，返回 `Accepted(emptyList())`。
     - 齐备后通过 `ResponseResolver.resolveWinner` 头跳裁决离打牌者最近的唯一赢家；荣和结算目标牌取 `window.targetTile`、放铳者取 `window.fromPlayer`，抢杠取 `window.isChankan`，彻底解耦对已过期 `lastDiscard` 的依赖。
     - 碰 (`DeclarePon`) 与吃 (`DeclareChi`) 接入 `executePonCommitInternal` 与 `executeChiCommitInternal`，调用 `TileMovement.pon` 与 `TileMovement.chi` 正常推进，消除 AI 选碰被拒绝的问题（如 seed=7 场景）。
   - **命令全生命周期防重入与事件完整捕获（审查 2）**：
     - 重入保护标志 `isProcessingAction = true` 覆盖整个命令执行生命周期（包含事务试探、审计、状态提交、事件发布及后续自动推进）。
     - 外部监听器（如 `DealingComplete`）在回调中尝试调用公开入口 `handleAction` 时，被单一入口守卫直接拦截并返回 `ActionResult.Rejected(RejectionReason.ACTION_NOT_AVAILABLE)`。
     - 内部摸牌、自动打牌与响应裁决通过内部专用私有函数/流水线执行，绝不递归调用公开 `handleAction`，避免自身锁死。
     - 公开命令返回的 `ActionResult.Accepted.events` 收集了该命令触发的全部后续自动摸打、副露、裁决与结算事件，顺序与 `eventBus` 发布顺序严格一致。
   - **合法性预校验与不变量异常透传（审查 3、4）**：
     - 所有动作在修改任何状态（甚至在 candidate 深度拷贝与窗口记录）之前，必须先通过 `validateActionPreconditions` 严格校验。
     - 拒绝时保证状态、窗口响应记录、资金、事件与下一局元数据零副作用。
     - 移除了将不变量损坏捕获为 `Rejected` 的宽松逻辑，`StateInvariants.assertValid` 抛出的守恒与王牌结构异常直接向外抛出，明确区分程序内部错误与玩家非法操作。
   - **严格语义相等匹配与窗口防二次响应（审查 4）**：
     - 废除原有的宽泛 `containsAll` / 仅比对 `kanType` 逻辑。
     - 响应窗口中对 `DeclarePon`、`DeclareChi`、`DeclareKan` 执行牌张身份、数量 (`size`) 与双向包含的严格等价匹配，并比对 `fromPlayer` 与 `calledTile`。
     - 玩家回合内的暗杠与加杠必须与 `legalActions` 生成的具体选项在类型、牌张数量与精确 `Tile` 身份上完全相等；空列表、重复牌、伪造牌组在预校验阶段直接拒绝 `ACTION_NOT_AVAILABLE`，绝不打开响应窗口或产生任何异常。
     - 窗口中同一玩家第二次提交响应直接返回 `ActionResult.Rejected(RejectionReason.ALREADY_RESPONDED)`。
   - **大明杠行动者设置、杠条件约束与一发清除（审查 5）**：
     - `executeOpenKanCommitInternal` 在岭上摸牌与审计前明确设置 `state.currentPlayer = player`。
     - 暗杠、加杠、大明杠、碰、吃发生后，全量清除四家一发标志 (`ippatsuActive.fill(false)`) 并设置 `isFirstUninterruptedGo = false`。
   - **码牌计划校验（审查 9）**：
     - `GameAction.StartStacking` 若传入非空 `stackingPlans`，返回 `ActionResult.Rejected(RejectionReason.UNSUPPORTED_ACTION)`；空列表保持原本确定性洗牌与可复现发牌行为。
   - **局末推进同步与终局立直棒归零（审查 10）**：
     - `StartStacking(ROUND_END)` 与 `nextRound()` 统一推进路径，在开局时检测并应用 `nextRoundMetadata`（庄家、场风、局号、本场），消费后清空元数据，避免不同入口状态不一致。
     - 在局末触发 `ProgressionResult.MatchFinished`（含正常终局、西入终局、尾庄和了终了 `DeclareAgariYame`）并发奖剩余供托给第一名后，将 `riichiSticks` 严格置 0，保证总资金严格守恒（全场总分恒为 100,000）。
     - 终局发布一次 `GameEvent.MatchEnded`，终局状态下 `nextRound()` 与 `StartStacking` 均拒绝 `MATCH_FINISHED`。
   - **里宝牌生成（审查 11）**：
     - 胜者宣告立直时，自摸与荣和提交时从 `deadWall` 对应里宝槽位（`4 + 2*k + 1`）填充 `uraDoraIndicators`，并发布 `GameEvent.DoraRevealed(isUraDora = true)`；未立直和牌清空里宝指示牌。

2. [`core/src/main/kotlin/com/majiang/state/GameState.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/GameState.kt)
   - 新增 `doubleRiichiDeclared: BooleanArray`，记录各座位是否成功宣告两立直。
   - 新增 `isRinshanDraw: Boolean`，显式标识当前是否处于岭上摸牌后的自摸和牌窗口。
   - 在 `resetForNewRound()`、`copyFrom()` 与 `deepCopy()` 中完整补齐上述字段的重置与深拷贝，保证事务回滚与权威引用保持。

3. [`core/src/main/kotlin/com/majiang/state/LegalActionProvider.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/LegalActionProvider.kt)
   - **杠可用性硬性约束（审查 5）**：
     - 在回合内暗杠/加杠判定以及响应窗口大明杠判定中，统一前置增加 `state.wallRemaining > 0 && state.rinshanPointer < 4` 约束；牌山耗尽或岭上 4 槽位用完时，严禁向玩家或 AI 提供必然执行失败的杠动作。

4. [`core/src/main/kotlin/com/majiang/rules/RiichiRuleEngine.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/rules/RiichiRuleEngine.kt)
   - **和牌上下文与役种判定修复（审查 11）**：
     - `buildWinContext` 中的 `isDoubleRiichi` 改为直接读取 `state.doubleRiichiDeclared[player]`，不再因打出立直宣言牌导致牌河非空而误判为 false。
     - `isTenhou` 与 `isChiihou` 依赖 `state.isFirstUninterruptedGo`、庄家座位与门清首摸状态。
     - `isRinshan` 依赖 `state.isRinshanDraw || phase == KAN_DRAW`。
     - `isHaitei` 明确在非岭上自摸且 `wallRemaining == 0` 时成立；`isHoutei` 在放铳且 `wallRemaining == 0` 时成立。

5. [`core/src/main/kotlin/com/majiang/screen/GameScreen.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/GameScreen.kt)
   - **完整动作按钮支持（审查 12）**：
     - 增加九种九牌流局按钮（`btnAbortiveDrawRect`，显示 `9 TERMINALS`），支持 `canHumanAbortiveDraw()` 触发 `DeclareAbortiveDraw`。
     - 杠按钮标签动态化：依据合法动作中的杠类型分别显示 `ANKAN`（暗杠）、`CHAKAN`（加杠）、`MINKAN`（大明杠）或 `KAN`。
     - 返回主菜单 (`backButtonRect`) 时，显式调用 `sessionSubscription?.close()` 与 `session.close()` 释放会话与总线监听器。

---

## 2. 与 DESIGN.md 的偏差

**无**。所有实现严格贴合 DESIGN.md 3、4、5、6 节以及 INTEGRATION.md 1~12 项补充契约，且已全面修复第一轮整合审查反馈。

---

## 3. 自检命令、结果与日志路径

- **自检状态**：未执行命令行构建与测试。
- **原因说明**：依据本次 headless 会话执行约束，command 权限未授权（严禁调用 RunCommand、终端、shell、gradle、powershell、rg、Get-Content 等）。
- **静态代码审查自检**：
  - 核对 `ActionResult.kt` 中的实际定义，确认 `RejectionReason` 包含且仅包含：
    `INVALID_PLAYER`, `WRONG_PHASE`, `NOT_YOUR_TURN`, `TILE_NOT_OWNED`, `INVALID_COMBINATION`, `ACTION_NOT_AVAILABLE`, `ALREADY_RESPONDED`, `MATCH_FINISHED`, `UNSUPPORTED_ACTION`。
    生产代码中引用的拒绝原因与该枚举 100% 逐字对应。
  - 对照 `EngineProtocolContractTest.kt` 的 8 个测试逻辑与 `MatchIntegrationContractTest.kt`，逐行推演状态机行为，确认：
    1. 监听器中递归调用 `handleAction` 返回 `Rejected(ACTION_NOT_AVAILABLE)`；
    2. 双家荣和第一个响应后状态仍为 `RESPONSE_WINDOW`，分数不结算，第二次提交同一响应返回 `Rejected(ALREADY_RESPONDED)`，齐备后结算离打牌者最近者；
    3. 伪造加杠（空牌组、错误牌组）直接返回 `Rejected(ACTION_NOT_AVAILABLE)`，状态保持 `DISCARD`，不抛异常也不打开窗口；
    4. 加杠抢杠窗口允许合法荣和，被抢杠时不推进加杠也不摸岭上牌；
    5. `StartStacking` 与 `nextRound()` 在下一局时均应用场风/局号/庄家更新；
    6. 终局立直棒清零，总资金（分数+供托）严格守恒为 100,000。
  - 测试由 Codex 负责执行 `:core:test --rerun-tasks`。

---

## 4. 必须由其他任务接入的接口

- **任务 B**：纯结算 `SettlementService`、拆解评估 `WinningScoreEvaluator`、场次推进 `MatchProgression`、仲裁器 `ResponseResolver` 已全链路接入状态机与规则引擎。
- **任务 C**：`GameScreen` 与表现层已接入 `LocalGameSession`，退回主菜单已接入会话与订阅注销；`LocalGameSession` 返回值与订阅者事件隔离由任务 C 独占修复。

---

## 5. 未完成项和已知限制

1. **码牌算法拓展**：依据规范，非空 `stackingPlans` 统一返回 `RejectionReason.UNSUPPORTED_ACTION`，保持空计划的确定性可复现发牌。
2. **立直暗杠保护**：立直后为保证合法待牌形态不改变，不生成暗杠与加杠动作（未立直玩家全量正常支持）。

---

## 6. 最后一轮定点返修摘要 (审查定点修复)

依据 Codex 最后一轮定点返修意见，严格仅在独占文件 `LegalActionProvider.kt` 与 `GameStateMachine.kt` 中完成 6 处定点修复：

1. **加杠实体牌选择修正** (`GameStateMachine.kt`)：
   - 在 `openResponseWindow` 中，针对 `isChankan` 场景，通过权威手牌 `drawnTile == tile || hand.closedTiles.contains(tile)` 从 `pendingKan.tiles` 中精确定位唯一新增的实体牌（而非错误取到已在副露中的旧牌）；
   - 抢杠响应窗口 `targetTile`、无人抢杠后的 `TileMovement.addedKan` 与荣和结算一致使用该张新增实体牌。

2. **吃碰后伪自摸防护** (`LegalActionProvider.kt` & `GameStateMachine.kt`)：
   - `LegalActionProvider` 移除 `allClosed.size % 3 == 2` 的宽松自摸生成逻辑，严格限定必须持有真实 `currentDrawn != null` 才评估自摸；
   - `GameStateMachine.executeActionOnCandidate` 对 `DeclareTsumo` 移除 `closedTiles.last()` 兜底，`drawnTile == null` 时直接返回 `false`。

3. **伪造吃牌面子类型校验** (`GameStateMachine.kt`)：
   - `validateActionPreconditions` 中针对 `DeclareChi` 补齐 `action.meld.type == MeldType.CHI` 自身类型约束，杜绝伪造其他类型面子绕过校验被 Accepted 记录。

4. **鸣牌响应同样视为放弃荣和** (`GameStateMachine.kt`)：
   - 统一手工响应与 AI 响应的三处逻辑：当响应窗口中存在合法的 `DeclareRon` 机会时，玩家或 AI 若选择任意非 Ron 响应（包含 `Pass`、`DeclarePon`、`DeclareChi`、`DeclareKan`），均视为放弃和了，正确标记 `sameTurnFuriten` 或 `riichiFuriten`；预校验拒绝的非法请求不产生副作用。

5. **和了事件翻数补齐宝牌** (`GameStateMachine.kt`)：
   - `RonWin` 与 `TsumoWin` 事件中的 `han` 字段由 `candidate.yakuResult.totalHan` 改为使用 `candidate.scoreResult.han` / `evaluated.scoreResult.han`，使事件翻数与实际结算付款点数完全一致（役满保留其 0 语义）；并在 `yakuNames` 中附加包含的宝牌数量。

6. **第一巡流局检查时序调整** (`GameStateMachine.kt`)：
   - 移除 `executeActionOnCandidate` 中 `DiscardTile` 与 `DeclareRiichi` 立即将 `isFirstUninterruptedGo` 置为 `false` 的逻辑；
   - 将 `isFirstUninterruptedGo` 结束标记延后至 `advanceToNextDrawInternal` 中流局检查（荒牌、四风连打等）之后、下一位摸牌之前执行（副露/杠中断仍立即置 `false`），确保第四位玩家打出同种风牌时可正常触发 `FOUR_WINDS` 流局。

