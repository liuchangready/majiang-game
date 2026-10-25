# V5 N3-AI 回放与状态黄金绑定（真实 C# 动作执行）

日期：2026-10-25
状态：**组件级任务完成（PASS + 明确 BLOCKED 字段）**；**不是** N3/G-049/G1 通过。
分支：`wt/v5-n3-ai-replay-binding`；工作树：`D:/majiang-wt/n3-ai-replay-binding`；冻结基线/唯一 PR19 已发布头：`900a043cfa276c0e172672b8b7cc7d6d4ebc4767`。
V4 仍是唯一生产架构；G1 未批准，G5 未完成，`catalog-v1.json`/manifest/统一 verifier 未改。

---

## 1. 授权、独立性与文件边界

本任务在 dispatch 指定的独占 worktree/branch 内完成，开工前已确认：`git rev-parse HEAD = 900a043…`、`branch = wt/v5-n3-ai-replay-binding`、`git status --porcelain` 为空、`git worktree list` 中该路径独占。

只读输入（未修改）：`AGENTS.md`、`doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`、`V5-0-1-IMPLEMENTATION-TASKS.md`、`V5-1-REVIEW.md`、`CODEX-V5-N3-ROUND-ACCEPTANCE.md`、`V5-P1.6-AI-GOLDEN.md`、用户只读协议 `D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`。

未读改兄弟补齐任务工作树；未接触 GLM 审计 `thread_20261025_051932_5eeb`（仍在其原 `8254c9b` 基线 R3 返修）及其在途产物；未等待、未重写其报告、未把其结论作为已接受依赖。

实际改动（expectedFiles，共 4 个）：

| # | 文件 | 类型 | 说明 |
|---|---|---|---|
| 1 | `dotnet/tests/Mahjong.Ai.Tests/AiReplayGoldenTests.cs` | 修改 | 新增共享只读 fixture 加载器（含 TEMP 探针用 `MAHJONG_AI_GOLDEN` 严格覆盖）；原 3 个 Fact 与全部断言保留；部分只读辅助方法改为 `internal static` 供新测试复用 |
| 2 | `dotnet/tests/Mahjong.Ai.Tests/AiReplayStateGoldenTests.cs` | 新增 | 3 个 Fact：接受 case 真实执行 RoundStateMachine；6 个拒绝 case 零副作用；导出字段全覆盖/不可表示声明 |
| 3 | `tools/v5-golden/probe-ai-replay-binding.ps1` | 新增 | 43 例 TEMP 单字段错值矩阵（5 正控制 + 38 负例） |
| 4 | `doc/v5-重构/V5-N3-AI-REPLAY-BINDING.md` | 新增 | 本文 |

未修改：`dotnet/src/**` 生产源码、`tests/v5-golden/ai-v1.json`（raw SHA 前后不变）、V4 exporter/fixture、catalog/manifest、统一 verifier、`*.csproj`/`*.sln`、Unity 环境/Resources/Burst、主 checkout 用户未提交文件。未新增 TableRuntime/Contracts/Server 引用（`Mahjong.Ai.Tests.csproj` 仍只引用 `Mahjong.Ai` + `Mahjong.Domain`）。

---

## 2. 真实执行链与输入来源

V4 导出侧（只读源锚点）：

- `engine/src/test/kotlin/com/majiang/state/V5AiGoldenExporterTest.kt`
  - `:107-113` 用 `TestTableBuilder.create(hands=mapOf(1 to hand), phase=DISCARD, currentPlayer=1, dealer=1, wallPrefix=[SOU9, WIND2])` 构造真实 V4 状态；
  - `:114-124` 真实 `TableAuthority(controllers=[RemoteHuman, Computer, RemoteHuman, RemoteHuman], initialState=state)`；
  - `:128-153` 真实 `buildAiSeatSnapshot` + `decisionClock.timing(1)`，记录 roundId/decisionId/epoch/version/deadline/option；
  - `:160-391` 7 次真实 `submitAiChoice`（6 拒绝 + 1 接受），`:750-784` 导出有界 state summary，`:786-805` 导出事件。
- `engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt:36-72`：`turnNumber=12`、当前玩家 DISCARD 目标 14 张、其他座位目标 13 张、`wallEndExclusive = wall.size`（该构造下 = 69）、dead wall 14。
- `engine/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt:181-201`：V4 接受弃牌后 `TileDiscarded` 事件、`phase=RESPONSE_WINDOW`；`openResponseWindow:330-349` 在存在合法响应时只开窗、不追加“开窗”事件。
- `engine/src/main/kotlin/com/majiang/state/GameState.kt:317`：`cheatPoints = IntArray(4) { 0 }`；`engine/src/main/kotlin/com/majiang/economy/CheatEconomyConfig.kt:10-12`：默认 `enabled=false`、`initialPoints = List(4){0}`。

C# 消费侧执行链（全部走真实公开生产端口）：

1. `AiReplayGoldenTests.BuildObservation` 从 `v5Observation` 构造冻结观察；
2. `AiDecisionService.Replay(recordedIntent, observation)` 注入记录意图（不跑启发式）；
3. `AiDecisionService.Submit(replay, authority, submittedAtMillis)` 真实准入；
4. 接受 case：同一 `AiDecisionSubmission` 进入真实 `DeadlineArbiter.ProcessTick`（`DecisionClock` + `ControlRegistry(seat1=Computer → epoch 1)` + `PendingDecision`），断言 `Timeouts=0/RejectedAi=0/AcceptedAi=1`，且 **`tick.AcceptedAi[0].Action` 与 `admitted.Submission.Action` 是同一实例**；
5. 用该条记录的动作构造 `RoundCommand.Discard(roundId, seat, tile, eligibleSeats)`，提交真实 `RoundStateMachine.Execute`；
6. 对真实 `RoundStateMachine`/`CheatLedger` 投影，与 V4 导出的 `beforeState`/`afterState`/`events` 逐字段比较。

**初始状态只由导出输入构造**（不读 `expected.afterState`）：

| C# 初态输入 | 来源 |
|---|---|
| roundId/dealer/roundWind/roundNumber/honba/riichi/scores/phase/seat | `v5Observation` |
| 本家 14 张手牌物理身份与顺序 | `v5Observation.myHandTileIds` |
| 其余三家各 13 张 | V4 `TestTableBuilder.kt:46-50` 目标规则（DISCARD 非当前家 13） |
| dead wall 14 张、无宝牌指示 | 规则常数 + `visibleDoraIndicators=[]` |
| 活牌山 69 张、绝对槽 53..121 | `v5Observation.remainingWallCount=69` + `TilePositionMap.InitialDealCount=53`/`LiveWallSlotCount=122` |
| 牌河/副露/袖/摸牌空 | `v5Observation` 的 `visibleDiscards/visibleMelds/myMelds/myDrawnTileId` 空值（非空则严格断言失败） |
| cheatPoints 初值 0 | V4 `GameState.kt:317` 与默认 `CheatEconomyConfig`；C# `CheatLedger.Initialize` |

未导出的容器身份（其他三家手牌、牌山顺序、王牌具体牌）由“导出本家手牌的确定性升序补集”填充，只为满足 136 张守恒；**任何断言都不比较这些身份**，本文不宣称覆盖它们。

---

## 3. 逐字段/逐 JSONPath 绑定表

状态投影字段集合（顺序即导出顺序）被精确断言为：
`roundId, roundVersion, phase, currentPlayer, dealerSeat, turnNumber, wallPointer, wallEndExclusive, wallRemaining, riichiSticks, honbaSticks, scores, cheatPoints, lastDiscardTileId, lastDiscardPlayer, seats`。
`seats[i]` 精确字段集：`seat, closedCount, drawnTileId, meldCount, discardCount, sleeveCount`。

| JSONPath（`v4.beforeState`/`afterState`） | V4 语义 | 真实 C# 调用/断言 | 输入来源 | 语义映射/源锚点 | 状态 |
|---|---|---|---|---|---|
| `roundId` | 对局 ID | `RoundStateMachine.RoundId`；等于 observation.roundId 与命令 roundId | `v5Observation.roundId` | 字符串恒等；V4 UUID 是运行随机值，不作为哈希 | 已绑定 |
| `roundVersion` | 局内版本 | `round.Version`：before=0，after=1 | 构造函数固定 0；`Build` 递增 | `RoundStateMachine.cs:1689` 每次接受 +1 | 已绑定 |
| `phase` | V4 `GamePhase` | `round.Phase`；严格 switch `DISCARD→Discard`、`RESPONSE_WINDOW→ResponseWindow`，未知值抛错 | 观察 phase=DISCARD；执行后由真实转换决定 | `RoundTypes.cs:12-23`；`RoundStateMachine.cs:583-590` | 已绑定 |
| `currentPlayer` | 当前行动家 | `round.CurrentSeat` | `v5Observation.seat`（CreateDealt=dealer） | `RoundStateMachine.cs:240`/`:585` | 已绑定 |
| `dealerSeat` | 庄家 | `round.DealerSeat` | `v5Observation.dealerSeat` | `RoundSpec.DealerSeat` | 已绑定 |
| `turnNumber` | V4 巡目 | **无 C# 端口**：`typeof(RoundStateMachine).GetProperty("TurnNumber") == null` 反射守卫；V4 侧断言 before==after | `v4` 导出 | `RoundCheatTransition` 中的 turnNumber 只是外部输入，不是 round 状态 | **BLOCKED（不可表示）** |
| `wallPointer` | V4 活牌山 0 基下标 | `round.Tiles.WallPointer - 53`；C# 侧断言 `>=0` 且 `WallRemaining == end-pointer`（本 fixture 无洞） | `TilePositionMap.InitialDealCount=53` | V4 `[0,69)` ↔ C# 绝对槽 `[53,122)`；`TilePositionMap.cs:194-196`、`TestTableBuilder.kt:64-67` | 已绑定（偏移映射） |
| `wallEndExclusive` | V4 活牌山末端 | `round.Tiles.WallEndExclusive - 53` | 53 + `remainingWallCount` | 同上 | 已绑定（偏移映射） |
| `wallRemaining` | 剩余活牌 | `round.Tiles.WallRemaining`（物理 Wall 且 `< WallEndExclusive`） | `v5Observation.remainingWallCount=69` | `TilePositionMap.cs:296-313`；本 fixture 无洞，恒等于 span | 已绑定 |
| `riichiSticks` | 立直棒 | `round.RiichiSticks` | `v5Observation.riichiSticks` | `RoundSpec.RiichiSticks` | 已绑定 |
| `honbaSticks` | 本场 | `round.HonbaSticks` | `v5Observation.honbaSticks` | `RoundSpec.HonbaSticks` | 已绑定 |
| `scores` | 四家点数 | `round.Scores` 数组逐元素 | `v5Observation.scores` | 4 项 long；`RoundSpec`/构造函数守恒校验 | 已绑定 |
| `cheatPoints` | 四家出千点 | `CheatLedger.Balances`（不可变账本） | V4 默认 0 | **跨端口**：RoundStateMachine 无该字段，C# 由独立 `CheatLedger.Initialize` 表示；`CheatLedger.cs:156-167` | 已绑定（跨端口，见 §5） |
| `lastDiscardTileId` | 最后弃牌 | `round.Tiles.LastDiscard?.Tile`；null ↔ JSON null | 由真实执行决定 | `TilePositionMap.cs:321`、`LastDiscardEvidence` | 已绑定 |
| `lastDiscardPlayer` | 最后弃牌家 | `round.Tiles.LastDiscard?.Seat ?? -1`；null 时 V4=-1 | 由真实执行决定 | 同上 | 已绑定 |
| `seats[].seat` | 座位号 | 投影下标 0..3 且 `seat` 相等 | 数组下标 | — | 已绑定 |
| `seats[].closedCount` | 门前手牌数 | `round.Tiles.CountAt(Hand, seat)` | before 14/13；after 由真实执行 | `TilePositionMap.CountAt`；V4 `closedTiles.size` | 已绑定 |
| `seats[].drawnTileId` | 摸牌实体 | `round.Tiles.DrawnTile` 且 Seat==seat 时取 Tile，否则 null | 观察 `myDrawnTileId=null` | C# 把“摸牌”作为 evidence，V4 可能并入 closedTiles；本 fixture 二者均为 null | 已绑定（表示差异已声明） |
| `seats[].meldCount` | 副露组数 | `round.Melds.Count(m => m.Seat==seat)`；本 fixture 全 0，且物理 `CountAt(Meld,seat)=0` | 空副露 | V4 `player.melds.size` ↔ C# 副露记录组数；本 fixture 无副露，物理组数也断言为 0 | 已绑定（零值 fixture） |
| `seats[].discardCount` | 牌河数 | `round.Tiles.CountAt(Discard, seat)` | 由真实执行决定 | `TilePositionMap.cs`；V4 `discards.size` | 已绑定 |
| `seats[].sleeveCount` | 袖牌数 | `round.Tiles.CountAt(Sleeve, seat)` | 0 | `TilePositionMap`；V4 `sleeveTiles.size` | 已绑定 |
| `v4.events[0].type` | 结果事件类型 | C# 提交事件精确序列 `[TileDiscarded, ResponseWindowOpened]`；`[0].Type` 对照 | 由真实执行决定 | V4 只有 `TileDiscarded`；C# 额外提交结构化开窗事件（`RoundStateMachine.cs:582`），V4 开窗无事件（`RoundTransitionPlanner.kt:340-349`） | 已绑定（结构映射） |
| `events[0].kind` | V4 动作类别 | 对照首个 C# 事件 `TileDiscarded` + `LegalActionKind.Discard` | 记录 intent | V4 `legalKind` = Discard | 已绑定 |
| `events[0].player`/`tileId` | 事件载荷 | `committed[0].Seat` / `committed[0].Tile` | 由真实执行决定 | `RoundEvent` | 已绑定 |
| `events[0].structured` | V4 结构化标记 | 断言 true；C# `RoundEvent` 本身结构化 | 导出 | — | 已绑定 |
| `events[0].text` | V4 `toString()` | **不比较**（C# 无对应文本/`isTsumogiri`/`isRiichiDiscard` 字段） | 导出 | 见 §5 | 不可表示 |
| `v4.eventCount` | 本次回执事件数 | 等于 `v4.events.length`；接受时 1，拒绝时 0 | 导出 | — | 已绑定（V4 内部一致性） |
| `v4.receiptStatus` | APPLIED/REJECTED | 接受 ↔ `AiSubmissionResult.IsAccepted` 且 `RoundTransition.IsAccepted`；拒绝 ↔ `!IsAccepted && Submission==null` | 导出 | V4 单一回执 vs V5 准入+转换两层，语义映射见 §4 | 已绑定（语义映射） |
| `v4.reason` | V4 拒绝原因 | 拒绝时断言 `STALE_AI_SUBMISSION`；接受时断言 JSON null | 导出 | V4 合并原因，V5 分裂码；不宣称码相等 | 已绑定（语义映射） |
| `v4.stateChanged` | 状态是否改变 | 接受 true/拒绝 false；拒绝另以 C# `StateHash`/`SourceFingerprint`/`Version`/`Events` 不变证明 | 导出 + 真实执行 | — | 已绑定 |

`v5` 侧 7 个 case 的 `intentActionRef`、`submittedAtMillis`、`authority`、`authorityMapping`、`expected.rejectionCode` 仍由原 `AiReplayGoldenTests` 的 `BuildIntent`/`BuildAuthority`/`AssertV4CallBinding`/`AssertV4Evidence` 全量断言；新测试复用同一构造器，不重新解释。

---

## 4. 7 个 case 的执行/零副作用结论

| case | 类型 | C# 真实调用 | 结果 | 零副作用证据 |
|---|---|---|---|---|
| AI-002-old-round | stale-round | Replay+Submit（authority.roundId 来自 `v4Call`） | `StaleRound`，无 Submission | round Version=0、StateHash、SourceFingerprint、Events=0、Window=null 全部不变；投影同时等于 V4 before/after |
| AI-003-old-decision | stale-decision | 同上（authority.currentDecisionId 来自 `v4Call`） | `StaleDecision` | 同上 |
| AI-004-old-epoch | stale-epoch | 同上（authority epoch=真实旧值 0） | `StaleEpoch` | 同上 |
| AI-005-wrong-seat | non-own-seat | 同上（authority seat=0、候选空） | `StaleDecision` | 同上 |
| AI-006-candidate-not-legal | illegal-candidate | 同上（authority 候选排除记录动作） | `IllegalAction` | 同上 |
| AI-007-exact-deadline | exact-deadline | 同上（`now == deadline`） | `DeadlineExpired` | 同上 |
| AI-001-legal-intent-before-deadline | legal-intent | Replay→Submit→DeadlineArbiter→同一记录动作→`RoundCommand.Discard`→`RoundStateMachine.Execute` | 接受；Version 0→1；Phase Discard→ResponseWindow；手牌 14→13；Discard 0→1；lastDiscard=行动牌/座位 1 | 与 V4 afterState 逐字段一致；事件 `[TileDiscarded, ResponseWindowOpened]` |

拒绝 case 的“零副作用”是在**真实 Domain 对象**上取 `Tiles.StateHash`、`SourceFingerprint`、`Version`、`Events.Count`、`Window`、`Result`、`CheatLedger.Version` 前后快照比较，且拒绝结果 `Submission == null`（结构上不可能提交到 round）。没有用“没执行所以没变化”的空断言代替：每轮都重新投影并与 V4 的 before/after 双向比较。

接受 case 的事件语义：V4 回执仅 `TileDiscarded` 一条；C# 真实提交两条（弃牌 + 开窗）。这是**事件表示差异**，不是事件丢失：C# 的 `ResponseWindowOpened` 有源锚点（`RoundStateMachine.cs:582`），V4 的开窗不写入 receipt（`RoundTransitionPlanner.kt:340-349`）。测试断言 C# 序列精确为这两条、ordinal 为 0/1、首条与 V4 导出事件逐字段一致，并断言窗口 `FromSeat/TargetTile/IsChankan`。

---

## 5. 不可表示范围、语义映射与未关闭项

1. **`turnNumber`（BLOCKED 字段）**：V4 导出 12 且弃牌后不变；C# `RoundStateMachine` 无巡目字段，`RoundCheatTransition` 的 `TurnNumber` 只是调用方输入。测试用反射显式断言该属性不存在，并断言 V4 before==after；**不伪造 C# 值**。要真正绑定需新增 Domain 轮次计数字段（生产 scope），不在本任务授权内。
2. **响应窗口合格座位集合未导出**：V4 `afterState.phase=RESPONSE_WINDOW` 只证明至少有一个合格家；具体集合/动作未导出。C# 命令声明一个非弃牌家 `(seat+1)%4` 以走真实开窗路径；测试只比较窗口来源/目标/非抢杠，**不比较窗口成员资格**。
3. **其他三家手牌、牌山顺序/身份、王牌具体牌未导出**：只按守恒与规则常数构造补集，测试只比较数量/位置/范围，不比较身份；不得据此宣称“全树/全实体已覆盖”。
4. **V4 事件文本与逐弃牌立直/摸切标记**：`text`、`isTsumogiri`、`isRiichiDiscard` 在 C# `RoundEvent` 无对应字段；测试明确不比较文本，只比较结构化载荷。原 fixture 的 `isClaimed/逐张 isRiichi=false` 限制继续有效。
5. **cheatPoints 跨端口**：C# round 不持有出千点，使用独立不可变 `CheatLedger`；两者是同一语义的不同生产对象。本 fixture 全 0，账本 Version 不变；不宣称 round 与 ledger 已在 TableRuntime 组合（本任务未接 TableRuntime）。
6. **无 V4 规范哈希**：fixture 无 V4 state/canonical hash；测试用 `forbiddenHashFields` 断言不存在此类字段，C# `StateHash` 只用于 C# 侧零副作用/守恒，不做跨语言哈希比较。
7. **fixture 只覆盖一个接受弃牌**，未覆盖 AI 的 tsumo/ron/kan/riichi/三绝技 intent；未导出 `AiDecisionRecorded` 事件（V4 不存在）。
8. **G-049/N3/G1**：本任务只补 N3-AI 消费者绑定；未改 catalog/manifest，不把测试数增加写成 G-049 full 或 G1 通过。
9. **非庄家 DISCARD 初态**：本 fixture `currentPlayer == dealerSeat == 1`，与 `RoundStateMachine.CreateDealt` 把 `CurrentSeat` 设为庄家的构造一致；若未来导出“非庄家为当前行动家且非 DRAW”的初始态，需要另一个 Domain 构造入口。测试用 `Assert.Equal(observation.DealerSeat, observation.Seat)` 显式拒绝该未覆盖形态，不做静默回退。

---

## 6. 探针矩阵（TEMP 副本单字段错值）

脚本：`tools/v5-golden/probe-ai-replay-binding.ps1`
机制：Release 非增量构建 → 将真实 fixture 原样复制到**新 GUID TEMP** → 每例只改一个逻辑字段（caller 指定的拒绝 case scores 为 before+after 成对）→ `MAHJONG_AI_GOLDEN` 指向该副本运行编译后的 `AiReplay*` 测试 → `try/finally` 恢复环境变量 → 汇总。

- 43 例 = **5 正控制 + 38 负例**（`matrix.csv` 逐例记录）。
- 基线/正控制 `Total=6, Failed=0, Errors=0, Skipped=0, Not Run=0, exit=0`。
- 每个负例必须：`exit != 0`、`Total == 6`、`Failed >= 1`、`Errors=0`、`Skipped=0`、`Not Run=0`，且日志有真实 `[FAIL]`；**文件不存在/解析异常/`Total=0` 一律记为 TOOL-FAILURE 并 exit 2，不算预期 FAIL**。
- 结果：`unexpectedResults=0`、`toolFailure=false`、`PROBE-PASS`、脚本 exit 0。

caller 指定两个 scores 负例：

| 例 | 变异 | 实际 |
|---|---|---|
| `N01-accepted-after-scores0` | 唯一接受 case `afterState.scores[0]=12345` | exit 1 / Total 6 / Failed 1；断言 `Expected:[12345,25000,25000,25000] Actual:[25000×4]` |
| `N02-rejected-before-and-after-scores0` | 第 1 个拒绝 case `beforeState.scores[0]` 与 `afterState.scores[0]` 同改 12345 | exit 1 / Total 6 / Failed 1（`AiReplayStateGoldenTests` 投影不匹配） |

其他覆盖：四席 `closedCount/drawnTileId/meldCount/discardCount/sleeveCount`（N03-N10）、墙 `wallPointer/wallEndExclusive/wallRemaining`（N11-N14）、事件 `tileId/player/type/structured/eventCount`（N15-N19）、`lastDiscard/phase/roundVersion/dealer/currentPlayer/棒数/cheatPoints/turnNumber`（N20-N29）、receipt/reason/拒绝后态/意图 ref（N30-N33）、未知枚举/缺失字段/错型/多余字段/fixture 身份哨兵（N34-N38）。`N15-event-tile` 即“真实 bound-event 失败控制”。

正控制：未改副本（P01）、仅改 `note`（P02）、`now/submittedAt` 同改到 `deadline-2` 仍是合法接受（P03）、仅改 `generatedAtHead`（P04）、仅改 `sessionId`（P05）——全部 `exit 0 / 6 passed`，证明测试绑定行为而非整文件哈希，且 `N38` 证明实际读取的是 TEMP 副本而非输出目录旧副本。

fixture 三种摘要严格区分（均未变化）：

- raw SHA-256：`E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`（当前 Windows 工作副本，CRLF）
- LF 归一化 SHA-256：`4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2`（与历史文档一致）
- Git blob SHA-1：`032d79e95e45bde9cb29046cad5f49f338fa78f3`

---

## 7. 实际命令、计数、native exit、日志与 SHA-256

CLI 固定 `D:/Unity/dotnet/dotnet.exe`（8.0.426）。最终测试证据根：
`E = C:/Users/sncdc/AppData/Local/Temp/ai-replay-binding-final-807cf6b67e9d4d1590dd4bd629bd6047`
探针证据根：
`P = C:/Users/sncdc/AppData/Local/Temp/ai-replay-probe-final3-667f6c21d3634faab181b2cf64f86f14`
探针控制台：
`C = C:/Users/sncdc/AppData/Local/Temp/ai-replay-probe-final3-console-4fdff8ae4ab747a9869375d92ae9b03d.log`

| 命令（在仓库根执行） | 结果 | native exit |
|---|---|---:|
| `dotnet build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-incremental -v:minimal` | 0 warning / 0 error | 0 |
| `dotnet dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -noLogo` | **438 passed / 0 failed / 0 skipped / 0 Not Run** | 0 |
| `... -class "*AiReplay*"` | **6 passed / 0 failed / 0 skipped**（原 3 Fact + 新 3 Fact） | 0 |
| `dotnet build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-incremental` | 0 warning / 0 error | 0 |
| `dotnet dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -noLogo` | **385 passed / 0 failed / 0 skipped** | 0 |
| `... -class "*RoundReplayGoldenTests*"` | 6 passed / 0 failed | 0 |
| `... -class "*RoundStateMachineTests*"` | 23 passed / 0 failed | 0 |
| `dotnet build dotnet/Mahjong.sln -c Release --no-incremental -v:minimal` | 0 warning / 0 error | 0 |
| `powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-ai-replay-binding.ps1 -RepoRoot D:/majiang-wt/n3-ai-replay-binding -EvidenceRoot <新 TEMP>` | 43/43 符合预期（5 正 + 38 负），`unexpected=0` | 0 |
| `git diff --check` | 无空白错误 | 0 |

证据 SHA-256（完整清单另存 `E/evidence-sha256.csv`）：

| 文件 | SHA-256 |
|---|---|
| `E/ai-build.log` | `A51C626F8B00FA6297F957C9DA60910D52AECFF1EA9A342B30DA7C58D2758C8E` |
| `E/ai-tests-full.log` | `5D8B2F7B47917A0175AA3D0F0146AA6582A0A30F032AF1ABFA2397B5FCA7EE5E` |
| `E/ai-replay-golden.log` | `EEDB88E27D59B08AF1C43B6FB3D867CDAB10451AAABF686073A0608999A435BA` |
| `E/domain-build.log` | `45F21197C0175CFA3100D23501B28DCCD643E032862E5D3BC384CF55D5793039` |
| `E/domain-tests-full.log` | `240E9C195421A44E5CD2759D76DD5CDF9D3E584C2613BA1EFA63CA648F27FCB0` |
| `E/domain-round-golden.log` | `3B23FFDC741150833873A2288276DD4DED75D0E9808B8C15D22AE7386C0C4433` |
| `E/domain-round-machine.log` | `6F0362FCFF49EA99B4440FBDBFA3BE4064B9225A2C7D58449743F5E6B22948DF` |
| `E/solution-build.log` | `19F37708E887CAB8283FF76E91FB1F0C4B39453001900DBFFDE55A2944E19FB2` |
| `P/build.log` | `1C25CEC075F6E88617B32B45E203CC8FF86A7884C0BA54EE5F656090372277F7` |
| `P/matrix.csv` | `F09BA1C3592EA6ADBDF2BA399D4042FFD17B048E8D9F1DBA36BD63485EEB1B49` |
| `P/summary.json` | `DE39141D55CF7562FC0F81660B822EB1B130E288E732669A4DF427021C1B18FD` |
| `P/meta.json` | `64D883444AFA540E5188767FA96BF45EE79F35B5E3C6DB4541C285ECAC83DBD4` |
| `C`（探针控制台） | `0E6F90294D9AA8B1C0EF1D090D6FDE32EE2E3589AAEBC66CC3123AEE20D1B897` |

交付源码 raw / LF 归一化 SHA-256（当前工作副本）：

| 文件 | raw SHA-256 | LF SHA-256 |
|---|---|---|
| `AiReplayGoldenTests.cs` | `21B006F1132EE1AF271710E117AD2DC747639B6B60B252C5A43C592A009C4AEE` | `C456A535761006F9092D91AF24C4651CC7B74FDE986DB245520206965AC37F55` |
| `AiReplayStateGoldenTests.cs` | `FF27B1863B8622D76676C475E07CE352FBD796211EC60F5631F2EA5F3FEB04E3` | 同 raw |
| `probe-ai-replay-binding.ps1` | `6F1D372C1D7281605DDAE3009CD8720383EE99A0E9940E78D36BC3E4D936EF8F` | 同 raw |
| `tests/v5-golden/ai-v1.json`（未改） | `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9` | `4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2` |

构建前置说明：本 worktree 是新建的，首次直接运行 Domain 测试时 `Mahjong.Domain.Tests.dll` 尚不存在，dotnet 报“找不到命令或文件”（该次运行未产生测试计数，也不计入任何 PASS/FAIL）；随后先做 Domain.Tests Release 非增量构建（0/0），再运行，得到上表的 385。该工具失败未被当作测试 FAIL。`E/domain-tests-full.log` 保存的是构建后的成功运行。

---

## 8. 结论分列

**PASS**

- 接受 case 的真实 C# 执行链（Replay → Submit → DeadlineArbiter → 同一记录动作 → RoundStateMachine）与 V4 `afterState` 全可表示字段逐字段一致。
- 6 个拒绝 case 的真实准入拒绝、`Submission==null`、round 版本/哈希/指纹/事件/窗口/账本零副作用；且投影同时匹配 V4 before 与 after。
- 原 3 个 Fact 与 7 case 身份/时限/意图/拒绝映射断言保留并继续通过。
- 43/43 探针符合预期（含 caller 两个 scores 负例、bound-event、四席、墙、事件、严格 schema）。
- Ai.Tests 全量 438、Domain 全量 385、Round 定向 6、RoundStateMachine 23、solution Release 构建 0/0，均 native exit 0。
- 工作树仅 4 个授权文件变化；fixture 未改；`git diff --check` exit 0。

**FAIL**：无（本轮实测未发现新的真实生产缺陷）。

**BLOCKED**

- `turnNumber` 无 C# Domain 端口：已用反射守卫 + 文档声明，未伪造；真正绑定需新增生产字段（超出本任务 scope）。
- 响应窗口合格集合、其他三家手牌/牌山/王牌身份、V4 事件文本与逐弃牌标记、跨语言 canonical hash：fixture 未导出或无对应 V5 oracle，已逐项声明，未宣称覆盖。
- `cheatPoints` 是跨端口（`CheatLedger`）语义绑定，不是 round 字段；TableRuntime 组合未在本任务覆盖。

**NOT_RUN**

- PostgreSQL/WSS、Unity/IL2CPP、Android/iOS 真机、真实网络、远端 CI、Gradle/PG 重型全量：本任务无需绑定变更测试，未运行。
- catalog/manifest/统一 verifier 未运行、未修改；G-049 仍未计入 G1；G1 未批准、G5 未完成。

---

## 9. 复现

```powershell
# 1) 构建与全量
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -noLogo
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -noLogo -class "*AiReplay*"

# 2) Domain 全量/定向 + solution
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -noLogo
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -noLogo -class "*RoundReplayGoldenTests*"
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental

# 3) 错值矩阵（每次必须新 TEMP 目录）
$ev = Join-Path $env:TEMP ('ai-replay-probe-' + [guid]::NewGuid().ToString('N'))
powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-ai-replay-binding.ps1 `
  -RepoRoot D:/majiang-wt/n3-ai-replay-binding -EvidenceRoot $ev
"PROBE_EXIT=$LASTEXITCODE"

# 4) 单点调试（只读 fixture；探针脚本内部使用）
$env:MAHJONG_AI_GOLDEN = '<TEMP copy>.json'
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Ai.Tests/bin/Release/net8.0/Mahjong.Ai.Tests.dll -noLogo -class "*AiReplay*"
Remove-Item Env:MAHJONG_AI_GOLDEN
```

提交与工作树：仅本地最小提交，未 push、未建 PR、未触发 CI、未合并兄弟代码、未修改默认分支。最终提交 SHA 与 clean status 记录于交付回执（本文随提交一起入库）。
