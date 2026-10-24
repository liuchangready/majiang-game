# V5 G1 N-2 权威结算计算组件交付报告

日期：2026-10-25
工作树：`D:/majiang-wt/n2-settlement-calculator`
分支：`wt/v5-n2-settlement-calculator`
基线/交付起点：`8254c9b783740ddf519cebc15bb50a8f9974cf58`（唯一 PR19 最新已发布头）
结论口径：**N-2 计算组件 PASS；N-2 整体未收口，G1 未通过，V4 仍是唯一生产基线，G5 未完成。**

本交付只回答“如何从当前不可变 RoundStateMachine 与可信权威事实，调用既有算分/支付组件产生可执行结算命令”。它没有把计算器接入 `TableActor`、`TableRuntime`、协议宿主或旧调用方，也没有替换任何调用方预置 `RoundSettlement` 的入口。原调用方可携带预算 settlement 的旧入口尚未收口，不能把本组件交付写成 N-2 完成或 G1 通过。

---

## 1. 范围、文件与边界

### 1.1 实际改动（仅 expectedFiles）

| 文件 | 类型 | 说明 |
|---|---|---|
| `dotnet/src/Mahjong.Domain/Rules/RoundSettlementCalculation.cs` | 新增 | 不可变结果、可信事实模型、稳定拒绝码/异常、源 round/version/fingerprint 绑定 |
| `dotnet/src/Mahjong.Domain/Rules/RoundSettlementCalculator.cs` | 新增 | 四个权威入口（Tsumo/Ron/ExhaustiveDraw/AbortiveDraw）、事实推导/校验、调用既有 `WinningScoreEvaluator`/`YakuEvaluator`/`SettlementService`、生成 `RoundCommand` |
| `dotnet/tests/Mahjong.Domain.Tests/RoundSettlementCalculatorTests.cs` | 新增 | 17 项定向行为/负例/真实状态机执行测试 |
| `dotnet/tests/Mahjong.Domain.Tests/RoundSettlementGoldenTests.cs` | 新增 | V4 scoring/round 黄金可表示子集差分；300 胜例 + 100 无和例 + RG-001/RG-002 |
| `doc/v5-重构/V5-G1-N2-SETTLEMENT-CALCULATOR.md` | 新增 | 本报告 |

未修改 `RoundStateMachine.cs`、`RoundTypes.cs`、`RoundTransition.cs`、`RoundRecovery.cs`、现有 `Rules/*`、既有测试、fixtures、catalog/manifest、Unity 环境/Resources/Burst、`CLAUDE.md`/`PROMPTS.md`/任务书或协议。未 push、未建 PR、未触发远端 CI、未改默认分支、未部署。

### 1.2 明确未做

- 未把计算器接入 `TableActor`/`TableRuntime`/协议/LocalHost/Unity。
- 未改变 `RoundCommand` 仍携带调用方传入 `RoundSettlement` 的现状；该旧入口仍可被调用，N-2 整体不能因此关闭。
- 未新增第二套算分；未接收调用方声称的 finalScores/han/fu/payment 作为权威结果。
- 未读取系统时间、未生成 ID/随机数、未使用 Unity/网络/数据库/新外部依赖。
- 未把 10,238 条 scoring 样本说成全部覆盖本入口；只跑可物理映射子集并记录跳过原因。

---

## 2. 权威输入模型与事实分层

### 2.1 入口

`RoundSettlementCalculator` 提供：

```csharp
CalculateTsumo(RoundStateMachine round, RoundSettlementFacts facts)
CalculateRon(RoundStateMachine round, int winnerSeat, RoundSettlementFacts facts)
CalculateExhaustiveDraw(RoundStateMachine round, RoundSettlementFacts facts)
CalculateAbortiveDraw(RoundStateMachine round, RoundSettlementFacts facts)
```

返回不可变 `RoundSettlementCalculation`：

- `Settlement`：已构造的 `RoundSettlement`（resultId、outcome、finalScores、remainingRiichiSticks、UTC、monotonic）
- `Command`：当前 `RoundCommand.Tsumo/Ron/ExhaustiveDraw/AbortiveDraw`，可交给现有 `Prepare`/`Install`
- `Breakdown`：胜和时的 `ScoreBreakdown`（含役、宝牌、符、`PaymentPlan`）；流局为 `null`
- `Payments`：既有 `SettlementService` 的 `SettlementResult`（deltas/newScores/remainingSticks）
- `SourceRoundId/SourceVersion/SourceFingerprint`：源绑定
- `WinnerSeat/LoserSeat/SeatWind/IsDealer/WinContext`
- `Execute(round)`：先校验源绑定，再调用 `round.Execute(Command)`，即仍走现有 `Prepare`/`Install` 与状态机守恒第二道闸。

### 2.2 可从当前 RoundStateMachine/TilePositionMap 推导（不接受调用方覆盖）

| 事实 | 来源/推导 |
|---|---|
| roundId、version、fingerprint | `RoundStateMachine` 当前值；输出绑定 |
| dealer/roundWind/roundNumber/honba/riichi sticks/starting scores | `RoundSpec` |
| phase、current seat、events、window | `RoundStateMachine` |
| 和牌者/放铳者 | Tsumo=`CurrentSeat`；Ron=显式 winner + `Window.FromSeat`（winner 不得等于 fromSeat） |
| seat wind | `((winner - dealer + 4) % 4) + 1`，与 V4 `GameState.seatWind` 一致 |
| 手牌实体牌/张数/闭门/副露实体 | `Tiles.TilesInZone(Hand, seat)`、`Meld` 区槽位；按 `13 - 3*melds` 校验 |
| 当前摸牌 | `Tiles.DrawnTile`（seat/tile/source） |
| 普通 Ron 目标牌 | `Window.TargetTile`、`Tiles.LastDiscard`、`Tiles.PositionOf`（必须是放铳者河底/latest discard） |
| 抢杠目标牌 | `Window.IsChankan`，并对 `Tiles.AddedKan(fromSeat, target)` 做不可变 dry-run，确认手牌 + 三张 Pon 组 |
| 宝牌指示牌 | `Tiles.DoraIndicators`；必须位于 `DeadWall(4+2i)`，最多 5 张、无重复 |
| 里宝指示牌 | 仅 riichi 时从 `DeadWall(5+2i)` 推导；无资格不读取、不伪造 |
| 副露实体组/张数/槽位连续性 | `Meld` 区 4 槽一组，3/4 张；与可信 meld 记录做集合精确匹配，且必须覆盖全部组 |
| dora/red 计数 | `WinningScoreEvaluator.Explain` 对当前手牌 + 指示牌计算；不接收调用方 doraCount |
| 流局听牌 | 对每座闭门部分 `HandDecomposer.CalculateWaits`；与 V4 `isTenpai(allClosedTiles)` 同一冻结组件，含副露后 10/7/4/1 张闭门形状 |

### 2.3 当前地图无法表示、必须由可信权威记录提供（逐项校验）

| 可信事实 | 所在模型 | 校验 |
|---|---|---|
| meld 类型（Chi/Pon/OpenKan/ClosedKan/AddedKan） | `RoundSettlementSeatFacts.Melds`（`Meld`） | 类型/张数/物理集合必须与 Meld 区组一致；Chi 只能来自上家；ClosedKan `fromPlayer=-1`；开副露 `fromPlayer` 0..3 且不得等于本座；`calledTile` 必须在组内 |
| riichi / double riichi / ippatsu | 同名 bool | double/ippatsu 必须蕴含 riichi；riichi 不得有明副露 |
| rinshan / haitei / houtei / chankan | 同名 bool | 与摸牌 source、`WallRemaining==0`、`Window.IsChankan` 强制相等；不允许漏报或误报 |
| tenhou / chiihou | 同名 bool | tenhou 必须 tsumo + 庄家 + `DrawSource.DealExtra`；chiihou 必须 tsumo + 闲家 + 门清 + 无鸣牌/杠/自己弃牌；二者不可同时 |
| 流局 tenpai | `RoundSettlementSeatFacts.IsTenpai` | 必须与闭门 `HandDecomposer.CalculateWaits` 推导完全一致；四座都需记录 |
| 途中流局原因 | `RoundSettlementFacts.AbortiveDrawReason` | 必须显式给出；FourKans 要求 `KanCount==4`；FourRiichi 要求四座门清 riichi；NineTerminals 要求至少一座 13 张闭门含 ≥9 种幺九/字牌 |

“缺证据拒绝”落实为：没有 winner 的 seat facts → `SeatFactMissing`；draw 少任一座 → `SeatFactMissing`；meld 记录少/多/错 → `MeldGroupMismatch`；没有 drawn evidence → `NoDrawnTile`；没有窗口 → `WrongWindow`；没有 abortive reason → `AbortiveReasonMissing`。不猜、不补默认值。

---

## 3. 校验、拒绝与命令第二道闸

### 3.1 拒绝方式

显式 `RoundSettlementRejectedException` + `RoundSettlementRejectionCode`（稳定 byte 值）。所有拒绝发生在构造 `RoundSettlement`/`RoundCommand` 之前，或仅对不可变 round 做只读校验；源 round 的 version/fingerprint/phase/events/tiles 不变。不会用 skip/空测试/删断言获得绿色。

已覆盖的拒绝类：

- 终态：`RoundFinished`
- 阶段/窗口：`WrongPhase`、`WrongWindow`、`WindowKindMismatch`
- 赢家/输家：`InvalidSeat`（Ron 显式校 winner）、`WinnerIsDiscarder`、`WinnerNotEligible`、`ResponseAlreadyRecorded`
- 摸牌：`NoDrawnTile`、`DrawnTileMismatch`
- 手牌/牌源：`HandCountMismatch`、`DuplicatePhysicalTile`、`HandShapeInvalid`
- 副露：`MeldGroupMismatch`、`MeldSeatMismatch`、`MeldTileMismatch`
- 规则事实：`RiichiStateInvalid`、`WinFlagMismatch`
- 宝牌/里宝：`DoraIndicatorMismatch`、`UraIndicatorUnavailable`
- 流局：`WallNotExhausted`、`TenpaiEvidenceMismatch`、`AbortiveReasonMissing`、`AbortiveReasonMismatch`、`AbortiveReasonUnexpected`
- 数值：`ArithmeticOverflow`（checked long 溢出）
- 守恒/展示一致性：`SettlementConservationViolation`（支付守恒由 `SettlementService` 内部 checked 计算；另校验 winner 实际到账 = `ScoreBreakdown.TotalPoints`）
- 源绑定：`SourceRoundMismatch`、`StaleSourceVersion`、`ForeignSourceState`

### 3.2 命令仍走 Prepare/Install

`RoundSettlementCalculation.Execute(round)`：

1. 校验 `round.RoundId`、`Version`、`SourceFingerprint` 与计算时完全一致；
2. 调用 `round.Execute(Command)`；
3. 状态机执行 `Prepare` → `Install`，并在 `BuildTerminal` 再校验起始分/供托与 settlement 的守恒，以及 outcome/winner/loser 一致性。

因此计算器不是绕过状态机直接改终态；即使计算层出现错误，第二道闸仍会拒绝。定向测试 `StateMachineConservationGateStillRejectsForgedSettlement` 直接构造伪造 finalScores 的旧式 settlement，状态机以 `SettlementConservationViolation` 拒绝、version 不变。

### 3.3 多赢家语义

当前 C# 响应窗支持多座位 Ron claim，`ResolveCompletedWindow` 按“离放铳者最近、同距座位号最小”裁决，与 V4 语义一致。计算器按每个候选赢家分别产生 settlement；测试 `MultiWinnerRon_ClosestSeatWins...` 计算座位 1/2 两个候选、顺序提交，最终只应用最近座位 1，座位 2 候选不生效。

注意：每次 accepted claim 都会推进权威 version。第一个 claim 后，预先按旧 version 计算的第二个 claim 会被本组件以 `StaleSourceVersion` 拒绝；宿主必须在每个 claim 后重新计算（测试即如此），或未来把 source 绑定纳入 command envelope 的显式授权语义。这是安全默认，不是多赢家不支持。

---

## 4. 测试与原始证据

### 4.1 命令、exit、计数

工作树：`D:/majiang-wt/n2-settlement-calculator`，HEAD `8254c9b783740ddf519cebc15bb50a8f9974cf58`（交付前）；CLI `D:/Unity/dotnet/dotnet.exe`。

| 验证 | 命令 | 实际结果 | exit |
|---|---|---|---|
| 新增定向 + 黄金 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v minimal -- --filter-class '*RoundSettlement*'` | **21 passed / 0 failed / 0 skipped**（17 定向 + 4 黄金） | 0 |
| Domain 全量 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v minimal` | **343 passed / 0 failed / 0 skipped**（基线 322 + 新增 21） | 0 |
| TableRuntime 依赖回归 | `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v minimal` | **313 passed / 0 failed / 0 skipped** | 0 |
| Solution Release build | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v minimal` | **0 warnings / 0 errors** | 0 |

证据目录（独占 TEMP，未写入工作树）：

`C:/Users/sncdc/AppData/Local/Temp/n2-settlement-evidence-d7dc25df1e304cdba9518e2d8343669e/`

| 文件 | 字节 | SHA256 |
|---|---:|---|
| settlement-tests.log | 1358 | `B89DCBCF24031F92B02E6EC3C71E443C8CC698905901744996FBF23E2AD910ED` |
| settlement-tests-xunit.log | 1356 | `C860457661FA426E2C6A9F15AB9693696BE73253A8DD065267B42BED256B5C2B` |
| domain-full.log | 1368 | `43037690C0FB3B86A7E9597F3CF84E5169857A16934CA85D4A44E720C9794BAA` |
| domain-full-xunit.log | 1300 | `3A0B814076D0FE06E6B3B0D4AF8B7062E7476C7374E63AFD56B1B0C2CD15AA4D` |
| table-runtime-full.log | 2862 | `8F21E968418A92265B39CFC5466B7F56EC095D09896AD3AA7A1868CAF16E190F` |
| table-runtime-full-xunit.log | 1094 | `1A1D0F9EC4090107FED9CE73320F69533673B923881C1BED342E99EF8BF09271` |
| solution-release-build.log | 3588 | `494EAA181807E77EE0F8D50CDAB3A246305E9EA673ABB9803A46CAA1B181B775` |

黄金计数来自 xunit 原始日志：

```text
[N2-GOLDEN] scoring win checked=300 skipped=1013 scanned=2237
[N2-GOLDEN] scoring noWin checked=100 skipped=393 scanned=1211
```

“skipped”是 **入口物理/规则可表示性不满足** 的样本（非空 drawn 字段、dora/ura 与手牌实体重复、riichi 与明副露、副露 caller 不符合冻结所有权、roundWind=4、seatWind 不在直接夹具范围等），不是测试 skip，也不计入通过分母。没有把 10,238 条全部算作本入口覆盖。

### 4.2 定向测试覆盖（17 项）

1. 庄家 tsumo：V4 12000 分、最终分/支付、命令真实执行、终态/版本。
2. 闲家 tsumo：真实弃牌→摸牌路由、2 本场 + 3 立直棒、守恒。
3. 普通 Ron：V4 2600 分、`LastDiscard`/河底绑定、真实执行。
4. 抢杠 Ron：真实 `AddedKan` 打开 CHANKAN_WINDOW、未吞杠、未摸岭上、最终 ChankanRon。
5. 流局（庄家听牌）：3000/1000 罚符、供托保留、`MatchProgression.NextRound` 连庄/本场+1。
6. 副露听牌流局：10 张闭门 + 1 副露，`HandDecomposer.CalculateWaits` 推导听牌。
7. 途中流局：无收支、供托保留；缺 reason/错 reason 拒绝。
8. 多赢家：两候选、最近座位裁决、败选候选不生效。
9. 源绑定：旧 version 拒绝、同 roundId/version 异 fingerprint 拒绝、源状态不变。
10. 终态/无役：终态不可复活；无役荣和拒绝。
11. 错误窗口/座位：无窗口、非 eligible、自摸自己弃牌、chankan 标志不符。
12. 副露证据：错误 caller、错误实体组、riichi+明副露。
13. 宝牌位置：指示牌不在 `DeadWall(4+2i)`。
14. checked 溢出 + 手牌张数错误，均源状态不变。
15. 旧式伪造 settlement 仍被状态机守恒闸拒绝。
16. 公共模型只读复制（facts seats / finalScores / deltas / melds）。
17. 真实 136 张实体表构建器贯穿所有用例，避免“只调函数不执行命令”。

### 4.3 黄金差分覆盖（4 项）

- `RepresentableV4ScoringWinSamplesFlowThroughNewEntryAndExecute`：300 个随机 V4 胜例，逐项比较 yaku code/han、yakuHan、scoreHan、isYakuman、yakumanCount、fu、basePoints、capCode、PaymentPlan total、最终分数，并把产出命令真实执行到状态机终态。
- `RepresentableV4ScoringNoWinSamplesAreRejectedByNewEntry`：100 个随机 V4 无和例，新入口全部以 `NoWinningHand` 拒绝。
- `V4RoundTerminalSettlementFieldsMatchNewEntry`：RG-001 的 `versionAfter/phaseAfter/resultFinalScores/resultDeltas/resultOutcome/resultId` 与 C# 结果一致；`TsumoWin`/`RoundEnded` 事件存在。
- `V4RoundChankanFieldsMatchNewEntryOnRepresentableSubset`：RG-002 的 finalScores/totalDeltas/winner/loser/versionAfterRon/ChankanRon 一致；未产生 `KanDeclared`/`RinshanDrawn`。

### 4.4 黄金字段“够/不够”逐项

| 黄金 | 足够比较 | 不足/不可比较 |
|---|---|---|
| scoring-v1 `randomSamples` | 手牌、drawn、melds、context、dora/ura、expected 的役/翻/符/base/cap/totalPoints | 不包含真实 roundId/version/window/河底/供托扣除；部分 dora/ura 与手牌实体冲突或标量 flag 不可由当前 round 表示，不能直接作为入口输入 |
| scoring-v1 `settlementSamples`（24） | `SettlementService` ron/tsumo/exhaustive/abortive 的 deltas/newScores/remainingSticks（既有 `ScoringGoldenTests.SettlementSamplesMatchV4`，本次 Domain 全量已跑） | **不是**新入口的 24 个用例：样本只给 fu/han，没有手牌/round/window，无法单独驱动本入口；本入口通过同一条 `SettlementService` 调用链复用其规则 |
| round-v1 `roundTerminal`（RG-001） | finalScores、totalDeltas、remainingSticks、outcome、resultId、versionAfter、phaseAfter、TsumoWin/RoundEnded | 无手牌/dora/窗口输入；V4 的 `ScoreSettled` 在 C# Domain 无同名枚举，不能逐字对照事件序列 |
| round-v1 `chankanRon`（RG-002） | finalScores、totalDeltas、winner/loser、remainingSticks、versionAfterRon、ChankanRon、无 Kan/Rinshan | addedTileId 的物理牌与冻结样本 19 不同（测试用 16，同为 5m 类型且避免红宝改变分数）；无完整手牌/宝牌输入；同样缺 ScoreSettled 事件 |

SCORING 全量 10,238 条中只有 300 条胜例 + 100 条无和例被本入口实际执行；其余未映射样本明确不计入分母，不能写成“全量覆盖本入口”。

---

## 5. 已知缺口/后续接线（按协议一次性列全）

以下均为当前交付边界内的真实缺口，不因本组件测试通过而关闭。

**N2-GAP-1 [MAJOR] 计算器尚未接入权威动作/宿主入口**
位置：`Mahjong.TableRuntime/TableActor`、`TableRuntimeContracts`、协议→域适配（P4.1/P5.1 未开工）。
实际：`RoundCommand` 仍由调用方携带 `RoundSettlement`；`RoundStateMachine` 只验证，计算器在生产代码零调用者。
期望：在权威命令边界调用 `CalculateTsumo/Ron/ExhaustiveDraw/AbortiveDraw`，拒绝/审计“调用方自带 settlement”的旧路径后才能说 N-2 接线完成。
证据：本报告 §1.2；`RoundStateMachine.cs:20-22` 头注释与 `RequireSettlement`。
修复要求：N-1a 验收后，由 caller 定义宿主接线点；禁止本组件自行改 `RoundTypes/StateMachine`。
验收断言：Tsumo/Ron/流局端到端命令不再由调用方提供 finalScores/han/fu/payment；伪造旧 settlement 在宿主入口被拒绝或有显式审计。

**N2-GAP-2 [MAJOR] 裸 RoundCommand 不携带 source fingerprint**
位置：`RoundSettlementCalculation.Execute` 与 `RoundCommand`。
实际：`Execute` 会先校验 roundId/version/fingerprint；但调用方若直接取 `calc.Command` 调 `round.Execute`，只受状态机 phase/window 校验，不能阻止同 roundId/version 的异源或重复分支执行。
期望：command/envelope 层携带 sourceVersion/fingerprint（或 receipt/幂等键），使“提取命令后执行”也受源绑定保护。
修复要求：在 N-2 接线时扩 command envelope 或保留 `RoundSettlementCalculation` 作为唯一执行面；不得只加薄壳。
验收断言：旧 version、异 fingerprint、重复执行同一计算对象均有明确拒绝/幂等语义。

**N2-GAP-3 [MAJOR] ScoreSettled 事件在 C# Domain 不存在**
位置：`RoundEventType`（16 值，无 ScoreSettled）；V4 `GameEvent.ScoreSettled`。
实际：支付/最终分保存在 `RoundResult.FinalScores/TotalDeltas` 与 `RoundSettlementCalculation.Payments`；事件序列只有 TsumoWin/RonWin/ExhaustiveDraw/AbortiveDraw/RoundEnded。
期望：协议/N-3 全字段对齐需要明确 ScoreSettled 的 C# 承载（新事件或投影合成），不能用“测试只比 fixture 内部结构”冒充行为对齐。
修复要求：由 N-3 或 host 投影任务定义，不在本组件擅自扩 `RoundEventType`。
验收断言：V4 事件序列逐项对照，或记录不可表示原因与批准责任。

**N2-GAP-4 [MAJOR] 可信事实的宿主认证边界**
位置：`RoundSettlementSeatFacts`（meld 类型/caller、riichi/double/ippatsu、rinshan/haitei/houtei/chankan、tenhou/chiihou、tenpai）。
实际：当前 round map 不保存这些历史；本组件只校验“组合合法/物理绑定”，不能证明宿主没有漏报（尤其 tenhou/chiihou、riichi/ippatsu 的缺席无法从 map 反推）。
期望：宿主必须从已验证记录/事件提供这些事实；不能让客户端或未验证调用方直接构造 facts。
修复要求：N-1a/宿主接线时加“事实来源”校验、授权/签名/回执绑定；不得把 facts 当公开 DTO。
验收断言：伪造/漏报/跨局 facts 在宿主入口被拒绝；旧 round/旧 controlEpoch/旧窗口无法复活。

**N2-GAP-5 [MINOR] 多赢家逐 claim 必须重算**
位置：`RoundSettlementCalculation.VerifySource`。
实际：第一个 claim accepted 后 version 前进，预计算的第二 claim 被 `StaleSourceVersion` 拒绝。
期望：多 Ron 场景由权威按 claim 顺序重算，或未来 command 绑定允许“同一窗口只追加 claim”的安全重绑定。
验收断言：多赢家端到端只应用裁决赢家；不重复支付；失败候选零副作用。

**N2-GAP-6 [MINOR] 杠后新宝牌未建模**
位置：`TilePositionMap`/`RoundStateMachine` 无 dora 指示牌转移；P1.1 已记录“V4 search/ura dora reveal on added kan 未导出”。
实际：本组件只校验当前 `DoraIndicators` 位于 `DeadWall(4+2i)`；若 N-1a 新增杠后翻宝，需同步位置/数量约定。
期望：杠后 dora/ura 由权威 map 明确表达；计算器仍只读 map，不自行加宝牌。
验收断言：杠后新增指示牌后分数/里宝资格与 V4 一致，且不影响非 riichi 的 ura。

**N2-GAP-7 [INFO] RG-002 物理牌差异**
测试用 addedTile=16、Pon=[17,18,19]，冻结 RG-002 用 19；二者同为 5m 类型。该差异只影响实体牌身份，不影响本用例可比较的支付字段；已在 §4.4 记录，没有改写冻结样本。

**N2-GAP-8 [INFO] 与 V4 现有 exhaustive tenpai 的关系**
`CalculateExhaustiveDraw` 使用闭门部分 `HandDecomposer.CalculateWaits`，与 V4 `RiichiRuleEngine.isTenpai(candidate.players[it].allClosedTiles)` 同一组件；副露后 10/7/4/1 张闭门已由新增测试覆盖，未发现规则差异。若后续改变“听牌”定义（如形式听牌/振听），属规则版本变更，不在本组件自行修改。

---

## 6. 后续必须完成的接线与防伪测试

1. **N-1a 验收后**：由 caller 指定宿主接线点；计算器只接收不可变 round + 宿主已验证 facts；不得复制/合并兄弟在途文件。
2. **替换旧入口**：`TableActor`/协议路径不再由调用方传 `RoundSettlement`；计算、执行、结果持久化在同一权威串行路径完成；失败无事件/无版本/无物理变化。
3. **command/envelope 源绑定**：sourceVersion + sourceFingerprint（或 receipt）进入权威命令；解决 N2-GAP-2。
4. **事实认证**：meld/riichi/ippatsu/tenhou/chiihou/tenpai 来源签名或权威记录校验；解决 N2-GAP-4。
5. **ScoreSettled/协议投影**：N-3 决定 C# 事件或投影合成；解决 N2-GAP-3。
6. **防伪结算测试矩阵**：
   - 伪造 finalScores/han/fu/payment、错误 winner/loser/dealer/seat wind；
   - 错误牌源/重复实体/副露 caller/副露组、闭门张数；
   - 错误窗口/旧 round/旧 version/旧 controlEpoch/终态复活；
   - 无役/非和牌、宝牌/里宝资格、dora 指示牌位置；
   - checked long 溢出、守恒破坏；
   - 多赢家顺序、重复 claim、败选候选零副作用；
   - 冷恢复后同一 facts/round 重算一致，重放不重复支付；
   - 宿主/协议端到端 Tsumo/Ron/抢杠/流局/本场/立直棒。
7. **N-2 关闭条件**：上述接线与测试完成，且旧“调用方携带 settlement”入口被实际替换/拒绝；本组件单测通过不等于 N-2 整体/G1 通过。

---

## 7. 复现命令

```powershell
# 新增定向 + 黄金
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v minimal -- --filter-class '*RoundSettlement*'

# Domain 全量
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v minimal

# 依赖回归
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v minimal

# Release solution build
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v minimal
```

原始日志与 SHA256 见 §4.1 的独占 TEMP 目录；本报告不覆盖/刷新历史证据，不宣称远端 CI、PG/WSS/Unity/IL2CPP/真机/物理网络结果。
