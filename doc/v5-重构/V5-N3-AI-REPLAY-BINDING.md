# V5 N3-AI 回放与状态黄金绑定（真实 C# 动作执行）

日期：2026-10-25（第 2 轮返修后）
状态：**组件级返修完成（PASS + 明确 BLOCKED 字段）**；**不是** N3/G-049/G1 通过。
分支：`wt/v5-n3-ai-replay-binding`；工作树：`D:/majiang-wt/n3-ai-replay-binding`；冻结基线/唯一 PR19 已发布头：`900a043cfa276c0e172672b8b7cc7d6d4ebc4767`。
第 1 轮交付：`6d168123b3115ae52ced6bf834035176a7c2e600`；本轮修复 D-R1-01 / D-R1-02。
V4 仍是唯一生产架构；G1 未批准，G5 未完成，`catalog-v1.json`/manifest/统一 verifier 未改。

---

## 1. 授权、独立性与文件边界

本任务在 dispatch 指定的独占 worktree/branch 内完成。开工时与每轮返修前均确认：`HEAD` 基于 `900a043…`、分支 `wt/v5-n3-ai-replay-binding`、`git status --porcelain` 为空、worktree 独占。未读改兄弟补齐任务工作树；未接触 GLM 审计 `thread_20261025_051932_5eeb` 及其在途产物。

只读输入：`AGENTS.md`、`doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`、`V5-0-1-IMPLEMENTATION-TASKS.md`、`V5-1-REVIEW.md`、`CODEX-V5-N3-ROUND-ACCEPTANCE.md`、`V5-P1.6-AI-GOLDEN.md`、用户只读协议 `D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`，以及第 1 轮独立复核证据 `C:/Users/sncdc/AppData/Local/Temp/codex-n3-ai-review-1792897707972`。

实际改动（expectedFiles，仍为 4 个）：

| # | 文件 | 类型 | 说明 |
|---|---|---|---|
| 1 | `dotnet/tests/Mahjong.Ai.Tests/AiReplayGoldenTests.cs` | 修改 | 共享严格 fixture 加载器；严格符号枚举解析；原 3 个 Fact 与全部断言保留 |
| 2 | `dotnet/tests/Mahjong.Ai.Tests/AiReplayStateGoldenTests.cs` | 新增 | 3 个 Fact：真实执行、拒绝零副作用、字段全覆盖/不可表示守卫；按 V4 构造器重建全桌并推导响应资格 |
| 3 | `tools/v5-golden/probe-ai-replay-binding.ps1` | 新增 | **48 例** TEMP 单字段错值矩阵（5 正控制 + 43 负例） |
| 4 | `doc/v5-重构/V5-N3-AI-REPLAY-BINDING.md` | 新增 | 本文 |

未修改：`dotnet/src/**` 生产源码、`tests/v5-golden/ai-v1.json`、V4 exporter/fixture、catalog/manifest、统一 verifier、`*.csproj`/`*.sln`、Unity 环境/Resources/Burst、主 checkout 用户未提交文件。未新增 TableRuntime/Contracts/Server 引用（`Mahjong.Ai.Tests.csproj` 仍只引用 `Mahjong.Ai` + `Mahjong.Domain`）。

---

## 2. 源输入重建与真实执行链

### 2.1 按 V4 构造器重建全桌（D-R1-01 修复）

V4 导出只给出一张 JSON 有界 state summary，但 accepted case 的**输入桌**由源码完全确定。第 2 轮不再用“任意升序补集”，而是逐项重放真实构造器：

- `V5AiGoldenExporterTest.kt:99-124`：`hand` 14 张、`TestTableBuilder.create(hands=mapOf(1 to hand), phase=DISCARD, currentPlayer=1, dealer=1, wallPrefix=[SOU9 copy0, WIND2 copy0])`。
- `TestTableBuilder.kt:20-76`：`TilePool.buildStandardSet()` 先建标准 136；`reserve` 顺序是 hands → drawn → melds → discards → sleeves → **wallPrefix** → deadWallPrefix；随后按 seat 0..3 依次把 `closedTiles` 补到目标（DISCARD 当前家 14、其他三家 13）；再取 14 张王牌；`wall = wallPrefix + remaining`，`wallEndExclusive = wall.size`。
- `TilePool.buildStandardSet()`：`for typeIndex in 0..33, for copyIndex in 0..3`，与 C# `TileId = typeIndex*4+copyIndex` 完全同序（升序 0..135）。

C# 测试的重建顺序（`AiReplayStateGoldenTests.BuildInitialRound`）：

1. `remaining = 0..135`；严格 `Reserve(hands[1])`（重复/缺失立即失败）；
2. 严格 `Reserve(68)`、`Reserve(112)`（SOU9 copy0、WIND2 copy0）；
3. seat 0..3 依次从 remaining 头部补到 14/13；
4. 头部 14 张为 dead wall；
5. `wall = [68,112] + remaining`，断言 69 张，映射到 C# 绝对槽 53..121。

由提交代码实际打印（TEMP 反射 harness，只读实仓）：

```text
source hand[0]=[1,2,3,5,6,7,9,10,11,13,14,15,17]
source hand[1]=[0,4,8,12,16,20,24,28,32,72,76,36,40,108]
source hand[2]=[18,19,21,22,23,25,26,27,29,30,31,33,34]
source hand[3]=[35,37,38,39,41,42,43,44,45,46,47,48,49]
source wallPrefix=[68,112]
derived EligibleSeats=[0]
```

这与复核方独立 harness 的 `hand[2]` 完全一致。测试另断言 seat2 的精确 13 张（构造器输出，不是 JSON oracle）、`wallPrefix` 前两张、136 守恒。

### 2.2 响应资格由源码输入推导（不再硬设座位）

V4 `LegalActionProvider.buildEligibleResponseActions`（`LegalActionProvider.kt:153-229`）与 `RiichiRuleEngine`：

- 荣和：`isFuriten` + `winningScoreEvaluator.canWinWithYaku`（`:168-192`）。C# 测试用真实 `HandDecomposer.CanWin(hand + discard)` 证明结构上不可能和牌（本项目三家均 false）；若结构上可能，则因缺 yaku/furiten 端口而**抛错声明 BLOCKED**，不猜资格、不伪造荣和。
- 碰：`canPon` = 同种 >= 2（`RiichiRuleEngine.kt:206-208`，`LegalActionProvider.kt:197-201`）。
- 大明杠：`canOpenKan` = 同种 >= 3，且活牌山 > 0、`rinshanPointer < 4`、`totalKanCount < 4`（`RiichiRuleEngine.kt:255-257`，`LegalActionProvider.kt:204-209`）。
- 吃：仅 `(discarder+1)%4`，且 `canChi` 的三种顺子（`RiichiRuleEngine.kt:146-…`，`LegalActionProvider.kt:212-217`）。
- 本 fixture 无立直，故 V4 的立直鸣牌禁止（`:195`）vacuous。

实测：丢弃 tile 0（MAN1 copy0）时，seat0 有 1/2/3 三张同种（碰+明杠），seat2 无同种、不能吃（其手牌 MAN5..9）、`CanWin` false，seat3 也无响应 ⇒ **真实资格 = seat0**。测试断言 `DeriveEligibleResponseSeats == [0]`，并把该列表交给 `RoundCommand.Discard`；执行后断言 `Window.EligibleSeats == [0]`。**不再使用 `(seat+1)%4` 或任何任意非空列表。** V4 未导出 window eligible map，因此这是“源码已知输入 + 源码锚点规则”的推导，不是对 JSON 字段的差分；窗口成员资格不谎称为 JSON oracle。

### 2.3 执行链

1. `BuildObservation` 从 `v5Observation` 构造冻结观察（枚举严格符号解析，见 §3/§6）；
2. `AiDecisionService.Replay` 注入记录意图，不跑启发式；
3. `AiDecisionService.Submit` 真实准入；
4. 接受 case：同一 `AiDecisionSubmission` 进入真实 `DeadlineArbiter.ProcessTick`（`DecisionClock` + `ControlRegistry(seat1=Computer → epoch 1)`），断言 `Timeouts=0/RejectedAi=0/AcceptedAi=1`，且 `tick.AcceptedAi[0].Action` 与 `admitted.Submission.Action` **同一实例**；
5. 用该记录的动作构造 `RoundCommand.Discard(roundId, seat, tile, derivedEligible)`，提交真实 `RoundStateMachine.Execute`；
6. 对真实 `RoundStateMachine`/`CheatLedger` 投影，与 V4 `beforeState`/`afterState`/`events` 逐字段比较。

---

## 3. 逐字段/逐 JSONPath 绑定表

状态字段集合（顺序即导出顺序）被精确断言为：
`roundId, roundVersion, phase, currentPlayer, dealerSeat, turnNumber, wallPointer, wallEndExclusive, wallRemaining, riichiSticks, honbaSticks, scores, cheatPoints, lastDiscardTileId, lastDiscardPlayer, seats`。
`seats[i]` 精确字段集：`seat, closedCount, drawnTileId, meldCount, discardCount, sleeveCount`。

| JSONPath（`v4.beforeState`/`afterState`） | V4 语义 | 真实 C# 调用/断言 | 输入来源 | 映射/源锚点 | 状态 |
|---|---|---|---|---|---|
| `roundId` | 对局 ID | `RoundStateMachine.RoundId`；等于 observation/命令 roundId | `v5Observation.roundId` | 字符串恒等；UUID 是运行随机值 | 已绑定 |
| `roundVersion` | 局内版本 | `round.Version`：before=0，after=1 | 构造固定 0；`Build` 递增 | `RoundStateMachine.cs:1689` | 已绑定 |
| `phase` | V4 `GamePhase` | `round.Phase`；严格 switch，未知值抛错 | 观察 DISCARD；执行后真实转换 | `RoundTypes.cs:12-23`；`RoundStateMachine.cs:583-590` | 已绑定 |
| `currentPlayer` | 当前行动家 | `round.CurrentSeat` | `v5Observation.seat` | `RoundStateMachine.cs:240/585` | 已绑定 |
| `dealerSeat` | 庄家 | `round.DealerSeat` | `v5Observation.dealerSeat` | `RoundSpec.DealerSeat` | 已绑定 |
| `turnNumber` | V4 巡目 | **无 C# 端口**；`V4TurnNumber` 严格按 **Int32 非负整数**校验（拒绝 12.5、1e30、字符串、缺失），并断言 V4 before==after；反射守卫 `GetProperty("TurnNumber")==null` | `v4` 导出 | V4 Kotlin `Int`；C# round 无该字段 | **BLOCKED（不可表示）** |
| `wallPointer` | V4 活牌山 0 基下标 | `Tiles.WallPointer - 53`；断言 `>=0` 且 `WallRemaining == end-pointer` | `InitialDealCount=53` | V4 `[0,69)` ↔ C# 绝对槽 `[53,122)`；`TilePositionMap.cs:194-196` | 已绑定（偏移） |
| `wallEndExclusive` | V4 活牌山末端 | `Tiles.WallEndExclusive - 53` | 53 + `remainingWallCount` | 同上 | 已绑定（偏移） |
| `wallRemaining` | 剩余活牌 | `Tiles.WallRemaining` | `v5Observation.remainingWallCount=69` | `TilePositionMap.cs:296-313`；无洞 | 已绑定 |
| `riichiSticks` | 立直棒 | `round.RiichiSticks` | observation | `RoundSpec` | 已绑定 |
| `honbaSticks` | 本场 | `round.HonbaSticks` | observation | `RoundSpec` | 已绑定 |
| `scores` | 四家点数 | `round.Scores` 逐元素 | `v5Observation.scores` | `RoundSpec` 守恒校验 | 已绑定 |
| `cheatPoints` | 四家出千点 | `CheatLedger.Balances` | V4 默认 0 | 跨端口：round 无该字段；`CheatLedger.cs:156-167` | 已绑定（跨端口） |
| `lastDiscardTileId` | 最后弃牌 | `Tiles.LastDiscard?.Tile`；null ↔ null | 真实执行 | `TilePositionMap.cs:321` | 已绑定 |
| `lastDiscardPlayer` | 最后弃牌家 | `Tiles.LastDiscard?.Seat ?? -1` | 真实执行 | 同上 | 已绑定 |
| `seats[].seat` | 座位号 | 投影下标 0..3 | 数组下标 | — | 已绑定 |
| `seats[].closedCount` | 门前手牌 | `Tiles.CountAt(Hand, seat)` | 源重建 14/13；执行后真实 | `TestTableBuilder` 目标规则 | 已绑定 |
| `seats[].drawnTileId` | 摸牌实体 | `Tiles.DrawnTile` 且 Seat==seat | observation 为 null | C# evidence vs V4 closedTiles 表示差异 | 已绑定（表意差异） |
| `seats[].meldCount` | 副露组数 | `round.Melds.Count(seat)`；本 fixture 全 0 且物理 `CountAt(Meld)=0` | 空副露 | V4 `player.melds.size` | 已绑定（零值 fixture） |
| `seats[].discardCount` | 牌河数 | `Tiles.CountAt(Discard, seat)` | 真实执行 | `TilePositionMap` | 已绑定 |
| `seats[].sleeveCount` | 袖牌数 | `Tiles.CountAt(Sleeve, seat)` | 0 | `TilePositionMap` | 已绑定 |
| `v4.events[0].type` | 结果事件类型 | C# 精确序列 `[TileDiscarded, ResponseWindowOpened]` | 真实执行 | V4 receipt 仅 `TileDiscarded`；C# 额外提交结构化开窗（`RoundStateMachine.cs:582`；V4 开窗不写 receipt，`RoundTransitionPlanner.kt:340-349`） | 已绑定（结构映射） |
| `events[0].kind/player/tileId` | 事件载荷 | 对照 `committed[0]` 的 Type/Seat/Tile | 记录 intent + 真实执行 | `RoundEvent` | 已绑定 |
| `events[0].structured` | 结构化标记 | true；C# `RoundEvent` 本身结构化 | 导出 | — | 已绑定 |
| `events[0].text` | V4 `toString()` | **不比较**（C# 无文本/isTsumogiri/isRiichiDiscard 字段） | 导出 | 见 §5 | 不可表示 |
| `v4.eventCount` | 本次回执事件数 | 等于 `v4.events.length` | 导出 | — | 已绑定 |
| `v4.receiptStatus` | APPLIED/REJECTED | 接受 ↔ 准入接受且 round 转换接受；拒绝 ↔ `!IsAccepted && Submission==null` | 导出 | 两层语义映射 | 已绑定 |
| `v4.reason` | V4 拒绝原因 | 拒绝时 `STALE_AI_SUBMISSION`；接受时 JSON null | 导出 | V4 合并原因 vs V5 分裂码，不宣称码相等 | 已绑定 |
| `v4.stateChanged` | 状态改变 | 接受 true/拒绝 false；拒绝另以 StateHash/Fingerprint/Version/Events 不变证明 | 导出 + 真实执行 | — | 已绑定 |

`v5` 侧 7 case 的 `intentActionRef`、`submittedAtMillis`、`authority`、`authorityMapping`、`expected.rejectionCode` 仍由原 `AiReplayGoldenTests` 的 `BuildIntent`/`BuildAuthority`/`AssertV4CallBinding`/`AssertV4Evidence` 全量断言；新测试复用同一构造器。

---

## 4. 7 个 case 的执行/零副作用结论

| case | 类型 | 真实调用 | 结果 | 零副作用证据 |
|---|---|---|---|---|
| AI-002-old-round | stale-round | Replay+Submit（authority 来自 v4Call） | `StaleRound`，无 Submission | Version/StateHash/SourceFingerprint/Events/Window/Result/CheatLedger 全部不变；投影同时等于 V4 before/after |
| AI-003-old-decision | stale-decision | 同上 | `StaleDecision` | 同上 |
| AI-004-old-epoch | stale-epoch | 同上（真实旧 epoch 0） | `StaleEpoch` | 同上 |
| AI-005-wrong-seat | non-own-seat | 同上（authority seat=0、候选空） | `StaleDecision` | 同上 |
| AI-006-candidate-not-legal | illegal-candidate | 同上（候选排除记录动作） | `IllegalAction` | 同上 |
| AI-007-exact-deadline | exact-deadline | 同上（now==deadline） | `DeadlineExpired` | 同上 |
| AI-001-legal-intent-before-deadline | legal-intent | Replay→Submit→Arbiter→同一记录→`RoundCommand.Discard(derived [0])`→`RoundStateMachine.Execute` | 接受；Version 0→1；Discard→ResponseWindow；手牌 14→13；牌河 0→1；lastDiscard=行动牌/seat1；Window.EligibleSeats=[0] | 与 V4 afterState 逐字段一致；事件两条 |

拒绝 case 的零副作用是在真实 Domain 对象上取快照比较；`Submission == null` 使拒绝结果结构上无法提交到 round。接受 case 的事件语义：V4 回执只有 `TileDiscarded`，C# 真实提交 `[TileDiscarded, ResponseWindowOpened]`（ordinal 0/1）；首条与 V4 导出事件逐字段一致，窗口来源/目标/非抢杠/资格 [0] 均断言。

---

## 5. 不可表示范围、语义映射与未关闭项

1. **`turnNumber`（BLOCKED 字段）**：V4 导出值经 **Int32 非负整数**严格校验（12.5、1e30、字符串、缺失均失败），V4 before==after；C# 无巡目字段，反射守卫显式声明，不伪造。真正绑定需新增 Domain 字段（生产 scope）。
2. **响应窗口资格**：V4 未导出 eligible map；本轮按 §2.2 源码锚点从重建手牌推导 `[0]` 并断言窗口资格，**不是**对 JSON 字段的差分。若未来某 case 出现结构上和牌，因缺 yaku/furiten 端口会显式抛错，而不是猜荣和。
3. **其他三家手牌/牌山/王牌身份**：现在是**源码构造器已知输入**（§2.1），不再是任意补集；但 JSON 没有这些字段的 oracle，因此测试只断言构造顺序/数量/资格前提，不宣称“JSON 全实体差分覆盖”。
4. **V4 事件文本与逐弃牌立直/摸切**：`text`、`isTsumogiri`、`isRiichiDiscard` 在 C# `RoundEvent` 无字段；只比较结构化载荷。
5. **cheatPoints 跨端口**：round 不持有出千点，使用独立 `CheatLedger`；本 fixture 全 0，账本 Version 不变；未接 TableRuntime 组合。
6. **无 V4 规范哈希**：fixture 无 state/canonical hash；测试用 `forbiddenHashFields` 断言不存在，C# `StateHash` 只用于 C# 侧零副作用/守恒。
7. **fixture 只覆盖一个接受弃牌**：未覆盖 AI 的 tsumo/ron/kan/riichi/三绝技 intent；无 `AiDecisionRecorded` 事件。
8. **非庄家 DISCARD 初态**：本 fixture `currentPlayer == dealerSeat == 1`；测试用断言拒绝其他形态（`CreateDealt` 只把 CurrentSeat 设为庄家）。
9. **G-049/N3/G1**：本任务只补 N3-AI 消费者绑定；未改 catalog/manifest，不把测试数增加写成 G-049 full 或 G1 通过。

---

## 6. 第 2 轮缺陷修复映射与验证

### D-R1-01（MAJOR）响应资格伪造 → 已修复

- **修复**：删除 `EligibleSeatsFor`/`(seat+1)%4` 与任意补集；`BuildInitialRound` 重放 V4 `TestTableBuilder` 的 reserve/fill/dead-wall/wall 顺序（含 `[68,112]`）；`DeriveEligibleResponseSeats` 用真实 `HandDecomposer.CanWin` + V4 源码锚点的碰/杠/吃判定推导；`RoundCommand.Discard` 与 `Window.EligibleSeats` 使用推导值。
- **验证**：source-check 打印 seat hands 与 `derived EligibleSeats=[0]`；测试断言 seat0 三张同种、seat2 无同种/不能吃/不能荣和、seat3 无响应；seat2 精确 13 张；N39（破坏 Man1 预留→seat0 失去三张）与 N40（hand 与 wallPrefix 双预留 68）均真实失败。
- **不宣称**：V4 未导出资格 map；这是源码输入推导，不是 JSON 字段 oracle。

### D-R1-02（MAJOR）未知枚举/非法巡目假绿 → 已修复

- **修复**：新增 `ParseDefinedEnum<T>`：必须 JSON 字符串且是 `Enum.GetNames` 精确成员；`Enum.Parse` 不再接受 `"254"`。`BuildObservation` 的 `legalActions[].kind`、`v5Observation.phase`、`ActionOptionFromJson` 全部改用该函数。`turnNumber` 使用 `TryGetInt32` + 非负校验（`V4TurnNumber`），保留“C# 无端口”声明。
- **验证**：复核方 `extra-byte-enums.ps1` 在最终构建上重跑：control=0；fractional/1e30/action-enum 254 均 exit1 且 Failed≥1；unknown-phase/schema/missing-turn/string-turn 仍失败。探针 N41/N42/N43 常驻。
- **不宣称**：没有新增 C# `TurnNumber` 字段，也没有放宽任何既有断言。

---

## 7. 探针矩阵（TEMP 副本单字段错值）

脚本：`tools/v5-golden/probe-ai-replay-binding.ps1`
机制：Release 非增量构建 → 真实 fixture 复制到新 GUID TEMP → 每例只改一个逻辑字段（caller 指定的拒绝 case scores 为 before+after 成对）→ `MAHJONG_AI_GOLDEN` 指向副本运行 `AiReplay*` → `try/finally` 恢复环境变量 → 汇总。

- **48 例 = 5 正控制 + 43 负例**（原 43 例全部保留，新增 N39-N43）。
- 基线 `Total=6, Failed=0, Errors=0, Skipped=0, Not Run=0, exit=0`。
- 每个负例必须 `exit != 0`、`Total == 6`、`Failed >= 1`、`Errors=0`、`Skipped=0`、`Not Run=0` 且有真实 `[FAIL]`；文件不存在/解析异常/`Total=0` 记 TOOL-FAILURE 并 exit 2，**不**算预期 FAIL。
- 结果：`unexpectedResults=0`、`toolFailure=false`、`PROBE-PASS`、exit 0。

关键负例：

| 例 | 变异 | 实际 |
|---|---|---|
| `N01` | 接受 case `afterState.scores[0]=12345` | exit1 / Total6 / Failed1 |
| `N02` | 首拒绝 case before+after `scores[0]=12345` | exit1 / Total6 / Failed1 |
| `N15` | `events[0].tileId=135` | exit1 / Total6 / Failed2 |
| `N39` | hand 预留 Man1 copies 1-3，seat0 失去三张 | exit1 / Failed1；`Expected:3 Actual:0` |
| `N40` | hand 第 14 张改 68（与 wallPrefix 冲突） | exit1 / Failed2；`source table tile T68 is reserved twice` |
| `N41` | `legalActions[1].kind="254"` | exit1 / Failed4；`kind is not a defined LegalActionKind member: 254` |
| `N42` | 拒绝 case before+after `turnNumber=12.5` | exit1 / Failed1；`must be a V4 Int32, raw=12.5` |
| `N43` | 拒绝 case before+after `turnNumber=1e30` | exit1 / Failed1 |

正控制：P01 未改副本、P02 仅改 `note`、P03 时间同改到 `deadline-2`、P04 仅改 `generatedAtHead`、P05 仅改 `sessionId`，全部 exit0 / 6 passed；证明绑定行为而非整文件哈希，且 N38/N41 证明实际读取 TEMP 副本。

fixture 三种摘要严格区分（未变化）：

- raw SHA-256：`E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`（Windows 工作副本 CRLF）
- LF 归一化 SHA-256：`4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2`
- Git blob SHA-1：`032d79e95e45bde9cb29046cad5f49f338fa78f3`

复核方脚本重跑（其 `extra-byte-enums.ps1`，新 Out）：control exit0/Total6/Failed0；fractional、1e30、action-enum 254 分别 exit1/Failed1/1/4；unknown-phase 4；unknown-schema 2；missing-turn 2；string-turn 1。

---

## 8. 实际命令、计数、native exit、日志与 SHA-256

CLI `D:/Unity/dotnet/dotnet.exe` 8.0.426。

证据根：

- 最终测试：`R2 = C:/Users/sncdc/AppData/Local/Temp/ai-replay-binding-r2-final-14fca7a4458f4dc7b900a73931d723fb`
- 探针矩阵：`P2 = C:/Users/sncdc/AppData/Local/Temp/ai-replay-probe-rework1-4fa6d8b04d7044b69d270a4771a97979`
- 探针控制台：`C2 = C:/Users/sncdc/AppData/Local/Temp/ai-replay-probe-rework1-console-2d3b2f40117442c9bc2e6b5b75c2683a.log`
- 源码重建打印：`H = C:/Users/sncdc/AppData/Local/Temp/ai-replay-source-check-3272059d4e27491e8fe92fcdf23d6bac`

| 命令（仓库根） | 结果 | exit |
|---|---|---:|
| Ai.Tests Release `--no-incremental` | 0 warning / 0 error | 0 |
| Ai.Tests 全量 | **438 passed / 0 failed / 0 skipped / 0 Not Run** | 0 |
| `-class "*AiReplay*"` | **6 passed**（原 3 Fact + 新 3 Fact） | 0 |
| Domain.Tests Release `--no-incremental` | 0 warning / 0 error | 0 |
| Domain 全量 | **385 passed / 0 failed / 0 skipped** | 0 |
| Round 定向 / RoundStateMachine 定向 | 6 / 23 passed | 0 |
| solution Release `--no-incremental` | 0 warning / 0 error | 0 |
| 48 例探针 | 48/48 符合预期，unexpected=0 | 0 |
| 复核方 extra-byte 脚本 | 全部符合修复后预期 | 0 |
| `git diff --check 900a043..HEAD` | 无空白错误 | 0 |

日志 SHA-256：

| 文件 | SHA-256 |
|---|---|
| `R2/ai-build.log` | `2609A8DDDFEB4B32099828B0DB6A84E44F5AA86CF4B11D8E4184A466C943AC21` |
| `R2/ai-tests-full.log` | `C5733655D93104EA224B708407E0F17DA578548329104C3B33797092A45CE9D3` |
| `R2/ai-replay-golden.log` | `2F0D5AB2BCC7D97F6C20D5D2A64CFAF5577E612D576C16BDC662BF398097A5B5` |
| `R2/domain-build.log` | `8C25E23A1FA9EEA9BD9ADFF189ECE07AFF74D1BDC9BFA5D6A6C00CAC865A3639` |
| `R2/domain-tests-full.log` | `50CA2FD85E46512AFA9C8C21E0B7FEFB9B63F9328B2AE80DCE9A28BD2724EB3E` |
| `R2/domain-round-golden.log` | `0191EBF5A9FCD1E1CCF854992C05D9D3397B00BD8C37E9B99D1B1D1E65CB337E` |
| `R2/domain-round-machine.log` | `84A61B6C493C8B46D77C4C5CF80E8CB73C154BC2CE29497F5BCAD2474E6DF1CB` |
| `R2/solution-build.log` | `4D5402039DCCC141880A5931519B80CD9EF387F90EF62531795B897DBBBE17B6` |
| `P2/build.log` | `12805E044DB1BD615124AAD4F9029AE041D508A524B62E6F47EFDBE4F75BAA4B` |
| `P2/matrix.csv` | `BA5D815CFE0C44CC1AC9B820897968476F3A0F7AFA8DA0AA27FE53E12ACEE19E` |
| `P2/summary.json` | `63D529F4CF708AF25D341F1DEE80639E57057D224133261B07B777CD3EF34B68` |
| `P2/meta.json` | `AB060E7D8C7642630ECEA47BA6165FE8531FD55B82BBFA91AE919C2322BCE6AE` |
| `C2` 探针控制台 | `F9F6A6E707DB337D90FBBE819B99CD3E6D74B82A3DECFAF4736DB9F35B072D71` |
| `H/source-check.log` | `88B9B870AB3BE8A18E9CBFC3AF541661E595061DECA071DF84EE37A8D77D217E` |

交付源码 raw / LF SHA-256：

| 文件 | raw SHA-256 | LF SHA-256 |
|---|---|---|
| `AiReplayGoldenTests.cs` | `24F91115233E47F5E6FC992A68EBB570807AC9D9D68FBB8544C80BBF0B06A9F5` | `F1B0AAE406BD453CE0E48A96167154D956E860F87F3C56646D93A5829879768E` |
| `AiReplayStateGoldenTests.cs` | `0032D47C87EFA1791F28ADC884A21FCE5837A35CDA4004E9CAA6CB83F504411A` | 同 raw |
| `probe-ai-replay-binding.ps1` | `7139D6B5D6F51282461743E639C775F1B32049287C2DBC17F96C1BBD2F38C098` | 同 raw |
| `tests/v5-golden/ai-v1.json`（未改） | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` | `4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2` |

---

## 9. 结论分列

**PASS**

- D-R1-01：源构造器重建（reserve 顺序、`[68,112]`、seat hands、王牌、牌山）+ 真实规则推导资格 `[0]`；window 资格断言；N39/N40 负控制。
- D-R1-02：严格符号枚举 + Int32 turnNumber；复核方三个假绿用例全部真实失败；N41-N43 常驻。
- 接受 case 真实执行与 V4 after 全可表示字段一致；拒绝 case 零副作用；原 3 Fact 保留。
- 48/48 探针、Ai 438、Domain 385、Round 6、RoundStateMachine 23、solution 构建 0/0。
- 仅 4 个授权文件；fixture 未改；`git diff --check` exit0。

**FAIL**：无（本轮未发现新的生产缺陷）。

**BLOCKED**

- `turnNumber` 无 C# Domain 端口（已严格校验 V4 Int，未伪造字段）。
- 结构上和牌时的 yaku/furiten 端口缺失（本 fixture 三家均结构 false，不触发；测试会抛错而非猜资格）。
- V4 响应窗口 eligible map 未导出：资格是源码输入推导，不是 JSON 字段差分。
- 其他桌身份为源码已知输入，但 JSON 无全实体 oracle；V4 事件文本/摸切/立直逐张标记无 C# 字段；cheatPoints 跨端口；无跨语言 canonical hash；TableRuntime 组合未覆盖。

**NOT_RUN**

- PostgreSQL/WSS、Unity/IL2CPP、Android/iOS 真机、真实网络、远端 CI、Gradle/PG 重型全量。
- catalog/manifest/统一 verifier 未运行、未修改；G-049 未计入 G1；G1 未批准、G5 未完成。

---

## 10. 复现

```powershell
# 1) 构建与全量
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -noLogo
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -noLogo -class "*AiReplay*"

# 2) Domain / solution
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -noLogo
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental

# 3) 48 例错值矩阵（每次新 TEMP）
$ev = Join-Path $env:TEMP ('ai-replay-probe-' + [guid]::NewGuid().ToString('N'))
powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-ai-replay-binding.ps1 `
  -RepoRoot D:/majiang-wt/n3-ai-replay-binding -EvidenceRoot $ev
"PROBE_EXIT=$LASTEXITCODE"

# 4) 单点（只读 fixture）
$env:MAHJONG_AI_GOLDEN = '<TEMP copy>.json'
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -noLogo -class "*AiReplay*"
Remove-Item Env:MAHJONG_AI_GOLDEN
```

提交与工作树：仅本地最小提交，未 push、未建 PR、未触发 CI、未合并兄弟代码、未修改默认分支。第 1 轮提交 `6d16812…`，第 2 轮最终提交与 clean status 记录于交付回执。
