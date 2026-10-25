# V5 N3-A Round Golden Consumer Field Binding（RG-001 / RG-002）

日期：2026-10-25
任务：`thread_20261025_061655_e4bd` 体系下的 N3-A round-v1 黄金消费者字段绑定（第二个 DS 空位的新任务）
工作树：`D:/majiang-wt/n3-round-binding`
分支：`wt/v5-n3-round-binding`
冻结基线 / 交付前 HEAD：`8ef0f49f452349cfb1d7ad35cfeec8558d7075db`
交付前 `git status --porcelain=v1`：空；HEAD 与 `git rev-parse --abbrev-ref HEAD` 均与任务书一致。
交付文件：`dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs`、本报告。
未 push、未开 PR、未 merge/rebase、未改默认分支、未触发远端 CI、未部署、未改全局环境、未转派。

## 0. 结论（PASS / FAIL / BLOCKED / NOT_RUN 分开）

| 范围 | 状态 | 依据 |
|---|---|---|
| 本任务：RG-001 / RG-002 消费者字段与终局生命周期绑定 | **PASS（仅本范围）** | 2 个既有 Fact 真实执行真实 `RoundStateMachine` / `MatchSession`；定向 6/0/0/0；46 个负例全部 exit1；6 个 identity 保留控制 exit0；正控制原 fixture 6/0/0/0 |
| Release solution build `--no-incremental` | **PASS** | exit0，0 warnings / 0 errors |
| Domain 全量 | **PASS** | 385 passed / 0 failed / 0 skipped，exit0 |
| `RoundReplayGoldenTests` 定向 | **PASS** | Total 6 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0，exit0；仍为原 6 个 Fact，无 skip、无空测试 |
| 负例矩阵 | **PASS（预期失败）** | 46/46 negative exit1；0 unexpected pass |
| identity 保留控制 | **PASS（预期通过）** | 6/6 preservation exit0；0 unexpected failure |
| 原始 fixture 内容 | **PASS（不可变）** | SHA256 `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` 交付前后相同；未改 `tests/v5-golden/*` |
| N3 整体 | **FAIL / 未完成** | 本任务只覆盖 round-v1 的 RG-001/RG-002 消费者；manifest 的 20 项 partial、G-028a 等未因本任务关闭 |
| G1 | **FAIL / BLOCKED（不批准，维持）** | 门 2 黄金行为全字段口径与门 5 三绝技权威链仍未关闭；N1b/N2 整体接线不在本任务，故 G1 不能被本任务放行 |
| G5 | **NOT_RUN** | 未运行 PG/WSS/Unity/IL2CPP/Android/iOS/设备/弱网/跨物理网络/远端 CI；不宣称 G5 |
| N1b / N2 整体 / P4.1 / P5.1 | **BLOCKED / NOT_TOUCHED（其他责任范围）** | 未修改出千生产接线、结算宿主接线、LocalHost、TableService 或宿主组合根；不借测试宣告其完成 |
| PG/WSS/Unity/IL2CPP/Android/iOS/设备/弱网/跨物理网络 | **NOT_RUN** | 本任务不运行、不宣称通过；历史环境缺口不由本报告改写 |

本交付**不**使用 `complete.flag`，**不**把本范围测试通过写成完整 N3/G1/G5 通过，**不**改动 V4 唯一生产基线。

## 1. 范围与约束遵守

### 1.1 允许与禁止

- 唯一代码文件：`dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs`。
- 唯一交付报告：`doc/v5-重构/V5-N3-ROUND-GOLDEN-BINDING.md`（本文件）。
- 未修改：`P13` / `RoundStateMachineTests.cs`、其他 `*GoldenTests.cs`、`tests/v5-golden/` 全部 fixture、`catalog-v1.json`、`V5-0-GOLDEN-MANIFEST.md`、verifier、`*.csproj`、`Mahjong.Domain` 生产代码、TableRuntime / LocalHost / 宿主生产代码、Unity / Resources / Burst。
- 未读取或复制两个兄弟线程的未提交产物；未合并其分支；未提前实现 N1b；未把 GLM 的 `V5-G1-N3-GOLDEN-FIELD-AUDIT.md`（尚未接受、未提交）当作已验证全字段依据，也未修改该报告。
- `origin/v5` 只用于核对规划/历史失败口径；不把额外规划分支或历史失败报告误读为恢复旧线程。
- 只读参考（未提交、未复制）：主 checkout 的 `D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`，以及 `AGENTS.md`、迁移方案、任务书、规则合同、V5-1-REVIEW 的 N3/G1 段、N1a/N2 验收、V4 exporter 与 Domain 源码。
- 未覆盖 caller 的 `E=D:/majiang-wt/codex-progress-20261025-0950/artifacts/progress-review` 原始证据；该目录保持只读。

### 1.2 交付前身份核对

```text
git -C D:/majiang-wt/n3-round-binding status --porcelain=v1 -b
## wt/v5-n3-round-binding
git rev-parse HEAD
8ef0f49f452349cfb1d7ad35cfeec8558d7075db
git rev-parse --abbrev-ref HEAD
wt/v5-n3-round-binding
```

`git worktree list` 确认 `D:/majiang-wt/n3-round-binding 8ef0f49 [wt/v5-n3-round-binding]`；主 checkout 为 `D:/majiang 8ef0f49 [feat/v5-1-integration]`，未被本任务触碰。

### 1.3 交付文件清单

```text
M dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs
A doc/v5-重构/V5-N3-ROUND-GOLDEN-BINDING.md
```

`git diff --check` exit0。最终 `git status --porcelain=v1` clean（提交后见 §11）。

## 2. 实际改动

`RoundReplayGoldenTests.cs` 保留原有 6 个 Fact 与 match/clock/control 断言，只强化 RG-001 / RG-002：

1. 增加精确字段集合校验 `AssertExactProperties`：缺失、未知、重复字段直接失败。
2. 增加严格枚举解析：`RoundPhase`、`RoundMeldKind`、`RoundOutcomeKind`、Tsumo/Ron outcome；未知值抛 `XunitException`，不映射默认值。
3. RG-001：
   - 读取并固定校验 `input.phaseBefore/versionBefore`；
   - 读取 `settlement.outcome/winner/resultId/finalScores/totalDeltas/remainingSticks/completedAt*`，逐项与真实 `RoundResult` 对照；
   - 解析 `expected.phaseAfter/resultOutcome/resultFinalScores/resultDeltas`；
   - 新增 `AssertTerminalLifecycle`，精确绑定 `TsumoWin, ScoreSettled, RoundEnded` 语义；
   - `rejectedActions` 严格 switch；未知动作失败；`DeclareAgariYame` 走真实 `MatchSession.ChooseDealerEnd`，不再是 Tsumo 的别名。
4. RG-002：
   - `input.meldTiles` 逐张绑定 `TilePosition.Meld(0, 0..2)` 与真实 136 map；
   - `addedTileId` 绑定 `Hand(0,13)` 与 `DrawnTile` evidence；
   - `meldTypeAfterKan` 由实际 meld 区三张同型物理牌判定为 `PON`，并严格解析 fixture；
   - `legalRonSeat1` 由真实 `RoundResponseWindow` 的 `IsChankan/FromSeat/TargetTile/EligibleSeats` 计算；
   - `eventTypes` 精确绑定 `ChankanWindowOpened, ResponseClaimed` 前缀 + `RonWin, ScoreSettled, RoundEnded` 终局语义；
   - `hasKanDeclared/hasRinshanDraw` 直接取 `result.Events.Contains`，`RinshanDraw -> RinshanDrawn` 名称映射，不使用“曾发生状态旗标”。
5. 保留并强化：
   - `hashBefore` 在加杠窗口与 Ron 终局均不变；
   - 岭上指针/杠数/手牌数/副露数不变；
   - 版本从 0→1（RG-001）与 0→1→2（RG-002）；
   - 所有 rejected action 后版本与 `ROUND_END` 不变。

## 3. RG-001 `roundTerminal` 字段绑定账本

V4 exporter：`engine/src/test/kotlin/com/majiang/rules/V5RoundGoldenExportTest.kt` 的 `exportTerminal()`（`:115-186`）；终局事件构造参考 `RoundTransitionPlanner.kt:581-584`（Tsumo）与 `:714-744`（`RoundResult.events = accumulatedEvents + stepEvents`）。
C# 消费者：`RoundReplayGoldenTests.cs` 的 `RoundTerminalReplaysExactly()`（`:100-303`）与 helper（`:778-919`）。

| JSON 叶路径 | V4 exporter / 来源 | C# 断言锚点（最终文件行） | 类别 / 真实关系 |
|---|---|---|---|
| `sampleId` | `:149` literal `RG-001` | `:115` | fixture identity；必须 RG-001 |
| `kind` | `:150` literal | `:116` | fixture identity；必须 `round-terminal` |
| `status` | `:151` literal | `:117` | fixture identity；必须 `exported` |
| `source.file` | `:153` literal source | `:119-122` | provenance；非空；不参与业务差分 |
| `source.testClass` | `:154` | `:119-122` | provenance；非空 |
| `source.testMethod` | `:155` | `:119-122` | provenance；非空 |
| `input.roundId` | `:158` actual `round.roundId` | `:160,178,206` | caller-supplied identity；真实 round/result 保留 |
| `input.dealer` | `:159` literal 0 | `:161,179` | input → `RoundSpec.DealerSeat` |
| `input.scores` | `:160` actual `result.startingScores` | `:162,180,214` | input → `StartingScores`；逐座比较 |
| `input.riichiSticks` | `:161` actual `result.startingRiichiSticks` | `:163,181,213` | input → `StartingRiichiSticks` |
| `input.phaseBefore` | `:162` actual `round.readState().phase.name` | `:164,170,182` | 严格解析 + 固定 `DISCARD` 构造 + 真实状态相等 |
| `input.versionBefore` | `:163` actual `round.version` | `:165,171,183` | 固定 0 + 真实版本相等 |
| `settlement.resultId` | `:166` actual `result.resultId` | `:190,205` | caller-supplied opaque identity；round-trip 保留 |
| `settlement.outcome` | `:167` literal `Tsumo` | `:188,208` | 严格只接受 Tsumo；C# `PrepareTsumo` 校验 |
| `settlement.winner` | `:168` literal 0 | `:190,198,209` | 输入 winner → `RoundOutcome.Tsumo` / `Result.Outcome` |
| `settlement.finalScores` | `:169` actual `result.finalScores` | `:192,210,218-223` | 输入 → `Result.FinalScores`；逐座 + delta 恒等 |
| `settlement.totalDeltas` | `:170` actual `result.totalDeltas` | `:210,218-223` | 输入不进入 `RoundSettlement`；真实 `Result.TotalDeltas` 必须逐座相等 |
| `settlement.remainingSticks` | `:171` actual `result.remainingRiichiSticks` | `:193,212` | 输入 → `Result.RemainingRiichiSticks`；守恒校验 |
| `settlement.completedAtUtc` | `:172` fixture literal | `:194,215` | fixture 提供的时间输入；真实 `CompletedAtUtc` 保留并相等 |
| `settlement.completedAtMonotonicMillis` | `:173` fixture literal | `:195,216` | fixture 提供的时间输入；真实 `CompletedAtMonotonicMillis` 保留并相等 |
| `expected.versionAfter` | `:176` actual `round.version` | `:227` | 真实终态版本必须 1 |
| `expected.phaseAfter` | `:177` actual `round.readState().phase.name` | `:228-230` | 严格解析 + 真实 `RoundPhase.RoundEnd` |
| `expected.eventTypes` | `:178` actual `result.events` | `:242-258`；helper `:778-827` | 精确终局语义序列，见 §4 |
| `expected.resultOutcome` | `:179` literal `Tsumo` | `:231-233` | 严格解析 + 真实 `Result.Outcome.Kind` |
| `expected.resultFinalScores` | `:180` actual `result.finalScores` | `:235` | 与真实 `Result.FinalScores` 相等 |
| `expected.resultDeltas` | `:181` actual `result.totalDeltas` | `:236` | 与真实 `Result.TotalDeltas` 相等 |
| `expected.rejectedActions` | `:182` actual processor rejection list | `:262-296` | 精确列表 `StartStacking, DeclareTsumo, DeclareAgariYame`；逐项真实拒绝 |
| `expected.versionAfterRejections` | `:183` actual `round.version` | `:303` | 拒绝后版本仍 1 |

补充真实关系：

- C# `BuildTerminal` 从 `Spec.Scores` / `Settlement.FinalScores` 计算 `TotalDeltas`（`RoundStateMachine.cs:1506-1569`），测试逐座断言 `FinalScores[i] - StartingScores[i] == TotalDeltas[i]`。
- C# `PrepareTsumo` 校验 outcome/winner/守恒后才生成 `TsumoWin`（`RoundStateMachine.cs:521-554`）。
- `settlement.totalDeltas` 不是 `RoundSettlement` 构造参数，因此它不能被“构造后再自证”；必须与真实 `Result.TotalDeltas` 比。

## 4. RG-002 `chankanRon` 字段绑定账本

V4 exporter：`V5RoundGoldenExportTest.kt` 的 `exportChankanRon()`（`:191-287`）；`added kan` 参考 `RoundTransitionPlanner.kt:254-259,671-695`。
C# 消费者：`ChankanRonReplaysExactly()`（`:299-557`）与 helper（`:778-919`）。

| JSON 叶路径 | V4 exporter / 来源 | C# 断言锚点（最终文件行） | 类别 / 真实关系 |
|---|---|---|---|
| `sampleId` | `:250` literal `RG-002` | `:313` | fixture identity |
| `kind` | `:251` literal | `:314` | fixture identity |
| `status` | `:252` literal | `:315` | fixture identity |
| `source.file` | `:254` literal source | `:316-320` | provenance；非空；不参与业务差分 |
| `source.testClass` | `:255` | `:316-320` | provenance；非空 |
| `source.testMethod` | `:256` | `:316-320` | provenance；非空 |
| `input.roundId` | `:259` actual `round.roundId` | `:356,365,538` | caller identity；round/result 保留 |
| `input.kanserSeat` | `:260` literal 0 | `:357,375-391,395` | 真实 meld 区 owner / 窗口 FromSeat |
| `input.addedTileId` | `:261` actual `tileId(addedTile)` | `:359,362,385-388,395` | 真实 `Hand(0,13)` + `DrawnTile` evidence |
| `input.ronSeat` | `:262` literal 1 | `:358,430,445,473` | 真实窗口唯一 eligible seat / Ron winner |
| `input.eligibleSeats` | `:263` actual window `eligibleSeats` | `:360,399,429` | 精确 `[1]`；窗口 eligible 必须相等 |
| `input.meldTiles` | `:264` actual `ponTiles` | `:361,374-391,408,424-441` | 每张 `TilePosition.Meld(0,0..2)`；顺序/席位/同型 |
| `expected.phaseAfterKan` | `:267` actual `GamePhase.CHANKAN_WINDOW` | `:416` | 严格解析 + 真实窗口阶段 |
| `expected.meldTypeAfterKan` | `:268` actual `MeldType.PON` | `:424-436` | 严格 `RoundMeldKind` + 真实 meld 区三张同型物理牌 |
| `expected.rinshanPointerAfterKan` | `:269` literal 0 | `:419` | 真实 `Tiles.RinshanPointer` |
| `expected.totalKanCountAfterKan` | `:270` literal 0 | `:420` | 真实 `Tiles.KanCount` |
| `expected.legalRonSeat1` | `:271` actual window eligible action | `:449-454` | 真实窗口 `IsChankan/FromSeat/TargetTile/EligibleSeats.Contains(1)` |
| `expected.phaseAfterRon` | `:272` actual `round.readState().phase.name` | `:505` | 严格解析 + 真实 `RoundEnd` |
| `expected.rinshanPointerAfterRon` | `:273` **实际写出 `afterKan.rinshanPointer`** | `:509` | 真实 Ron 后指针；见 §8.2 provenance 限制 |
| `expected.totalKanCountAfterRon` | `:274` **实际写出 `afterKan.totalKanCount`** | `:510` | 真实 Ron 后杠数；见 §8.2 provenance 限制 |
| `expected.eventTypes` | `:275` actual `result.events` | `:552-557`；helper `:778-827` | 精确前缀 + 终局语义 |
| `expected.hasKanDeclared` | `:276` `events.contains("KanDeclared")` | `:520,523` | 直接 `result.Events.Contains(KanDeclared)`，无状态旗标豁免 |
| `expected.hasRinshanDraw` | `:277` `events.contains("RinshanDraw")` | `:521,526` | `RinshanDraw -> RinshanDrawn` 名称映射后的真实 Contains |
| `expected.resultOutcome` | `:278` literal `Ron` | `:531-532` | 严格只接受 Ron + 真实 `Result.Outcome.Kind` |
| `expected.winner` | `:279` literal 1 | `:443-445,533` | 真实 `WinnerSeat` = ronSeat |
| `expected.loser` | `:280` literal 0 | `:443-445,534` | 真实 `LoserSeat` = kanser |
| `expected.finalScores` | `:281` actual `result.finalScores` | `:535,540-547` | 输入 → `Result.FinalScores`；逐座 + delta 恒等 |
| `expected.totalDeltas` | `:282` actual `result.totalDeltas` | `:536,540-547` | 真实 `Result.TotalDeltas` 必须逐座相等 |
| `expected.resultId` | `:283` actual `result.resultId` | `:537` | caller-supplied opaque identity；round-trip 保留 |
| `expected.versionAfterRon` | `:284` actual `round.version` | `:539` | 真实 2 |

补充真实关系：

- C# `PrepareAddedKan` 在 eligible 非空时只打开 `RoundPhase.ChankanWindow` 并发出 `ChankanWindowOpened`，不移动副露、不消耗岭上（`RoundStateMachine.cs:1049-1101`）。
- 测试在窗口打开后断言 `Tiles.StateHash` 不变、meld 区仍 3 张、手牌数不变、`RinshanPointer/KanCount` 均为 0；随后真实 `RoundCommand.Ron` 完成窗口。
- C# `ResolveCompletedWindow` 对 `IsChankan` 的 Ron 走 `BuildTerminal(..., RoundEndReason.ChankanRon, ...)`（`RoundStateMachine.cs:1149-1183`），终局仍不会提交加杠/岭上。
- `P13.ChankanDealt` 的副露只在物理 map 中（`RoundStateMachineTests.cs:98-174`），没有 `RoundMeld` 记录；测试显式断言 `round.Melds` 为空并绑定物理 map，未伪造 `RoundMeld`。详见 §8.1。

## 5. 终局生命周期语义投影（关键合同）

V4 `RoundResult.events` 在 RG-001 / RG-002 为：

```text
RG-001: TsumoWin,  ScoreSettled, RoundEnded
RG-002: RonWin,    ScoreSettled, RoundEnded
```

C# `RoundEventType` 没有 `ScoreSettled` 成员（`RoundTransition.cs:65-86`）。C# 终局 `RoundResult` 的 `Events` 是整局累积历史，终局 tail 为：

```text
RG-001: TsumoWin, RoundEnded
RG-002: ChankanWindowOpened, ResponseClaimed, RonWin, RoundEnded
```

本测试采用**严格语义投影**，不是“删掉 ScoreSettled 不检验”：

1. `result != null`、`result.ResultId` 非空、`FinalScores/TotalDeltas` 各 4 项、`RemainingRiichiSticks >= 0` 是 ScoreSettled 槽的真实结算依据。
2. `AssertTerminalLifecycle` 要求：
   - C# 历史长度恰为 `prefix.Length + 2`；
   - 前缀逐项精确相等（RG-001 为空；RG-002 必须为 `ChankanWindowOpened, ResponseClaimed`）；
   - 前缀后恰好一个 win 事件，再恰好一个 `RoundEnded`，且 `RoundEnded` 是最后一个；
   - 整局恰好一个 win、一个 `RoundEnded`、零个 `MatchEnded`；
   - fixture `eventTypes` 必须恰为 `[win, ScoreSettled, RoundEnded]`，且 `ScoreSettled` 恰好一次。
3. 因此以下任一情况都真实 FAIL：
   - `ScoreSettled` 缺失 / 重复 / 错序；
   - `RoundEnded` 缺失 / 重复 / 非最后；
   - 额外 `MatchEnded`；
   - win 事件错误或重复；
   - C# 终局 tail 多出/缺少事件；
   - 前缀被窗口事件污染或被任意过滤。
4. 不改生产枚举、不硬编码假 `ScoreSettled` 事件；`ScoreSettled` 的语义由真实 non-null `RoundResult` 承担，并在报告中明确记录这一非等价映射。

`hasKanDeclared` / `hasRinshanDraw`：

- `hasKanDeclared = result.Events.Any(e => e.Type == RoundEventType.KanDeclared)`；
- `hasRinshanDraw = result.Events.Any(e => e.Type == RoundEventType.RinshanDrawn)`；
- 对应 V4 `events.contains("KanDeclared")` / `events.contains("RinshanDraw")`，只做 `RinshanDraw -> RinshanDrawn` 名称映射；
- 不使用 `KanCount`、`RinshanPointer`、窗口状态或“曾发生”旗标替代布尔比较。

## 6. `DeclareAgariYame` 的真实 API 路径

V4 的 `GameAction.DeclareAgariYame` 在 `RoundCommandProcessor.prepare` 中由 round 终态先行拒绝；V5 C# `RoundActionKind` 不含该动作（`RoundTypes.cs:31-47`），匹配层 API 是 `MatchSession.ChooseDealerEnd`（`MatchSession.cs:498-556`）。

`AssertAgariYameRejectedAfterRoundEnd`（`:840-919`）实际执行：

1. 用同一冻结 spec/settlement 在各新 round 上真实执行到 `ROUND_END`；
2. `MatchSession.StartSession(active)` → `AcceptRoundResult(finished.State, ...)`；
3. 断言 session 进入 `WaitingReady` 且 `CanDealerDeclareAgariYame == false`；
4. 调用真实 `session.ChooseDealerEnd(true, ...)`；
5. 断言 `DealerChoiceUnavailable`、`choice.State == null`、session 版本不变；
6. 断言 finished round 仍 `ROUND_END`、`Result != null`、版本不变；fixture 的 terminal 也保持 `ROUND_END/Result != null/版本 1`。

这修复了旧测试把 `DeclareAgariYame` 当成 `Tsumo` 命令的假执行；但本报告不声称“单局层新增了 AgariYame 动作”，它仍是 match 层命令。

## 7. 已知缺陷全量清单与修复映射（同类合并）

以下 6 组覆盖本轮已知全部 MAJOR；未只修 caller 的 5 个探针。

### N3-RG-D01 [MAJOR] RG-001 输入 phase/version 与 outcome 被硬编码

- **位置（修复前）**：`RoundReplayGoldenTests.cs:109-130`（旧文件）。`P13.Dealt(..., phase: RoundPhase.Discard)`、`RoundOutcome.Tsumo(...)`、`Assert.Equal(RoundPhase.RoundEnd, ...)` 写死；`input.phaseBefore/versionBefore`、`settlement.outcome`、`expected.phaseAfter/resultOutcome` 未读取。
- **复现**：matrix `terminal.phaseBefore.wrong`、`terminal.phaseBefore.unknown`、`terminal.versionBefore.wrong`、`terminal.settlement.outcome.wrong`、`terminal.phaseAfter.wrong`、`terminal.resultOutcome.wrong`。
- **实际（修复前）**：caller 的 `phase-unbound.log`（5 组探针）为 6 pass / 0 fail / 0 skip，exit0；其余同类字段由旧代码静态可见未读取。
- **期望**：严格解析并固定构造，真实状态/版本/outcome/phase 必须匹配；未知值失败。
- **证据**：`matrix-console.log`；`logs/negative.terminal.phaseBefore.wrong.log`、`...unknown.log`、`...versionBefore.wrong.log`、`...settlement.outcome.wrong.log`、`...phaseAfter.wrong.log`、`...resultOutcome.wrong.log`；caller `E/phase-unbound.log` 只作修复前对照。
- **修复**：`:159-184`、`:186-223`、`:225-236`。
- **验收断言**：上述 6 个独立负例均 exit1；正控制原 fixture exit0。

### N3-RG-D02 [MAJOR] RG-001 终局事件只 Contains TsumoWin，完整序列未绑定

- **位置（修复前）**：`RoundReplayGoldenTests.cs:134`（旧文件）；只 `Assert.Contains(...TsumoWin)`。
- **复现**：`terminal.eventTypes.match-ended-plus-reordered`、`...remove-score-settled`、`...duplicate-score-settled`、`...round-ended-before-score-settled`、`...extra-match-ended`、`...missing-field`。
- **实际（修复前）**：caller `E/event-sequence-unbound.log`：6 pass / 0 fail / 0 skip，exit0；旧测试不读 `eventTypes`。
- **期望**：精确 `TsumoWin, ScoreSettled, RoundEnded`；`ScoreSettled`/`RoundEnded` 缺失、重复、错序、`MatchEnded` 额外出现均失败。
- **证据**：`logs/negative.terminal.eventTypes.*.log`；`matrix.csv`。
- **修复**：`AssertTerminalLifecycle` `:778-827`；调用 `:242-258`。
- **验收断言**：6 个独立负例 exit1；正控制 6/0/0/0；C# committed/state/result 三个事件序列均精确。

### N3-RG-D03 [MAJOR] `rejectedActions` 把所有非 StartStacking 当作 Tsumo，AgariYame 未真实调用

- **位置（修复前）**：`RoundReplayGoldenTests.cs:136-145`（旧文件）。
- **复现**：`terminal.rejectedActions.remove-agari-yame`、`...unknown-action`；以及 D01 中的 AgariYame 探针。
- **实际（修复前）**：`DeclareAgariYame` 被构造为 `RoundCommand.Tsumo(...)`；未知动作同样走 Tsumo；`MatchSession.ChooseDealerEnd` 从未调用。
- **期望**：精确列表 `StartStacking, DeclareTsumo, DeclareAgariYame`；未知动作显式失败；AgariYame 走真实 match API 并验证 `ROUND_END`/版本不变。
- **证据**：`logs/negative.terminal.rejectedActions.remove-agari-yame.log`、`...unknown-action.log`；正控制原 fixture。
- **修复**：`:261-303`；`AssertAgariYameRejectedAfterRoundEnd` `:840-919`。
- **验收断言**：删除 AgariYame 或加入未知动作均 exit1；AgariYame 负例通过真实 `DealerChoiceUnavailable` 路径，round/session 版本均不变。

### N3-RG-D04 [MAJOR] RG-002 meldTiles / meld 类型 / legal Ron / phase / 布尔 / 事件未绑定

- **位置（修复前）**：`RoundReplayGoldenTests.cs:150-233`（旧文件）；`meldTiles` 完全未读；`meldTypeAfterKan`、`legalRonSeat1`、`phaseAfterKan/phaseAfterRon`、`hasKanDeclared/hasRinshanDraw`、`eventTypes` 未读/未断言。
- **复现**：`chankan.hasKanDeclared.false-to-true`、`chankan.eventTypes.match-ended`、`chankan.phaseAfterKan.wrong`、`chankan.phaseAfterKan.unknown`、`chankan.phaseAfterRon.wrong`、`chankan.meldTypeAfterKan.chow`、`chankan.legalRonSeat1.false`、`chankan.eligibleSeats.extra-seat`、`chankan.ronSeat.wrong`、`chankan.meldTiles.reorder`、`chankan.addedTileId.wrong`、`chankan.meldTiles.missing`、`chankan.hasRinshanDraw.true/.wrong-type`。
- **实际（修复前）**：caller `E/flags-unbound.log`、`E/meld-and-legal-unbound.log`、`E/phase-unbound.log` 全部 6 pass / 0 fail / 0 skip，exit0；`meldTiles` 从未进入比较。
- **期望**：meld 三张物理牌逐张绑定 `Meld(0,0..2)`；`PON` 由实际三张同型牌判定；窗口唯一 eligible `[1]`；`legalRonSeat1` 由真实窗口计算；phase 严格解析；布尔取 result events Contains；事件序列精确。
- **证据**：`logs/negative.chankan.*.log`；`E/*` 只作修复前对照。
- **修复**：`:362-441`（输入/meld）、`:447-454`（legal Ron）、`:505-527`（phase/布尔）、`:549-557`（事件）。
- **验收断言**：上述 13 个独立负例全部 exit1；正控制 6/0/0/0；`P13.ChankanDealt` 物理布局不被修改。

### N3-RG-D05 [MAJOR] RG-001 / RG-002 输出与 settlement 字段未完整绑定

- **位置（修复前）**：`RoundReplayGoldenTests.cs:125-147,179-232`（旧文件）。`settlement.resultId/totalDeltas/completedAt*`、`expected.resultOutcome/resultFinalScores/resultDeltas/versionAfter/versionAfterRon` 等未读或仅弱比较；`resultId` 未与 `Result.ResultId` 绑定。
- **复现**：`terminal.totalDeltas.first-123456789`、`terminal.resultFinalScores.wrong`、`terminal.resultDeltas.wrong`、`terminal.settlement.finalScores.sum-preserving`、`terminal.settlement.remainingSticks.wrong`、`terminal.settlement.completedAtUtc.invalid-format`、`terminal.settlement.resultId.empty`、`terminal.versionAfter.wrong`、`chankan.finalScores.sum-preserving`、`chankan.totalDeltas.wrong`、`chankan.winner.wrong`、`chankan.loser.wrong`、`chankan.versionAfterRon.wrong`、`chankan.resultId.empty`。
- **实际（修复前）**：`settlement.totalDeltas` 探针 caller `E/settlement-deltas-unbound.log` 为 6/0/0 exit0；其余字段旧代码未读或只做弱断言。
- **期望**：真实 `RoundResult.ResultId/FinalScores/TotalDeltas/RemainingRiichiSticks/CompletedAtUtc/CompletedAtMonotonicMillis` 与 fixture 逐项一致；版本/胜负/名次信息一致；空 resultId/错格式显式失败。
- **证据**：`logs/negative.terminal.*`、`logs/negative.chankan.*`；`matrix.csv`。
- **修复**：RG-001 `:186-236`；RG-002 `:529-547`。
- **验收断言**：上述 14 个独立负例 exit1；正控制 6/0/0/0。

### N3-RG-D06 [MAJOR] 缺失/未知/错型字段与未知枚举被吞或默认

- **位置（修复前）**：`RoundReplayGoldenTests.cs:508-528`（旧 helper）只有 `GetProperty`/`GetInt64`/`GetBoolean`；没有精确字段集校验，phase 被硬编码，action fallback 到 Tsumo，未知事件不参与序列。
- **复现**：`terminal.eventTypes.missing-field`、`chankan.hasRinshanDraw.wrong-type`、`terminal.phaseBefore.unknown`、`chankan.phaseAfterKan.unknown`、`terminal.rejectedActions.unknown-action`。
- **实际（修复前）**：缺失字段可能由 `GetProperty` 抛异常，但未知额外字段被忽略；未知 phase 不解析（硬编码）；未知 action 走 Tsumo；未知事件不参与检查。
- **期望**：`AssertExactProperties` 对 sample/source/input/settlement/expected 精确字段集；`ParsePhase/ParseMeldKind/ParseOutcomeKind/ParseTsumoOutcome/ParseRonOutcome` 未知值抛 `XunitException`；错型由强类型 JSON 访问抛异常。
- **证据**：`logs/negative.terminal.eventTypes.missing-field.log`、`...chankan.hasRinshanDraw.wrong-type.log`、`...terminal.phaseBefore.unknown.log`、`...chankan.phaseAfterKan.unknown.log`、`...terminal.rejectedActions.unknown-action.log`。
- **修复**：`AssertExactProperties` `:1022-1046`；严格 parser `:908-1014`；字段集合调用 `:105-157,304-360`。
- **验收断言**：5 个独立负例 exit1；正控制原 fixture 6/0/0/0。

## 8. 真实边界、未导出与不宣称项

### 8.1 `P13.ChankanDealt` 无 `RoundMeld` 记录

`P13.ChankanDealt`（`RoundStateMachineTests.cs:98-174`）只把 16/17/18 放入 `TilePosition.Meld(0,0..2)`，并以 19 作为 `Hand(0,13)` 的 drawn tile；没有创建 `RoundMeld` 记录。本任务按范围禁止修改该文件，因此测试：

- 显式 `Assert.Empty(round.Melds)`；
- 逐张绑定 `TilePosition.Meld(0,0..2)` 与 `TileAt`；
- 断言 meld 区恰 3 张、同型、与 added tile 同型；
- 断言 `round.Tiles.TilesInZone(Meld,0)` 顺序等于 fixture `meldTiles`。

因此“meldTypeAfterKan = PON”绑定的是真实物理布局 + 三张同型签名，而不是伪造的 `RoundMeld`。若未来 N1b 或其他任务给 P13 增加真实 `RoundMeld`，应另立任务扩展断言；本报告不宣称已绑定 `RoundMeld` 记录。

### 8.2 V4 exporter 的 afterRon provenance 不独立

`V5RoundGoldenExportTest.kt:273-274` 写的是 `afterKan.rinshanPointer` / `afterKan.totalKanCount`，而不是 Ron 后状态。因 fixture 两值都为 0，测试对真实 Ron 后状态的断言仍通过，但这是**provenance 限制**：不能把 fixture 的 afterRon 值当作独立于 afterKan 的 V4 观测。本任务未改 golden（禁止），已在测试中断言真实 Ron 后 `RinshanPointer/KanCount` 为 0；报告明确此限制，不宣称“afterRon 每个值都有独立 V4 oracle”。

### 8.3 caller-supplied identity / synthetic 字段

- `resultId`、`roundId`：C# 合同是 caller 提供的 opaque identity；有效新值会被真实 `RoundResult` 原样保留。因此“改成一个不同的有效 UUID/roundId”是 preservation control（exit0），不是负例；空值/错型才是负例（exit1）。
- RG-001 `completedAtUtc` / `completedAtMonotonicMillis`：V4 exporter 写的是 fixture 常量（`:172-173`），C# 作为 caller 时间输入保存；有效改值 preservation exit0，非法格式 exit1。真实墙上时钟/单调时钟不在本测试范围。
- RG-002 `completedAt*`：fixture 未导出；测试使用固定 `2026-10-23T00:00:00+00:00` / `2000` 仅用于构造 `RoundSettlement`，不进入业务差分，也不宣称 V4 值。
- `finalScores` / `totalDeltas`：C# 的 settlement 由调用方提供（N2 整体接线未完成），本测试验证守恒、逐座 delta 恒等、与真实 `Result` 的保留关系；**不**重新计算 V4 算分，不宣称 N2 完成。

### 8.4 未导出/未覆盖

- `unexportedSamples` 中的完整 136 位 V4 状态摘要、`server.ConnectionControl` grace/reclaim、V4 加杠里宝 reveal 仍未导出；本任务不补造。
- RG-002 未导出 `remainingSticks` / `completedAt*`；测试构造值已单列。
- match/clock/control 的既有断言保留；本任务不把它们的未导出样本纳入本报告分母，也不修改 catalog/manifest。

## 9. 验证执行记录（原始命令 / 数值 exit / 计数 / 日志 / SHA）

证据根：`C:/Users/sncdc/AppData/Local/Temp/n3-round-binding-evidence-20261025-2/`（独占 TEMP）。
CLI：`D:/Unity/dotnet/dotnet.exe`。所有 native exit 在命令后立即捕获；失败日志保留，不覆盖原始 fixture。

### 9.1 命令与结果

```powershell
# 1. Release solution build
& D:/Unity/dotnet/dotnet.exe build D:/majiang-wt/n3-round-binding/dotnet/Mahjong.sln `
  -c Release --no-incremental
# exit 0；0 warnings / 0 errors

# 2. Domain full
& D:/Unity/dotnet/dotnet.exe test `
  D:/majiang-wt/n3-round-binding/dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj `
  -c Release --no-build
# exit 0；385 passed / 0 failed / 0 skipped

# 3. RoundReplayGoldenTests targeted
& D:/Unity/dotnet/dotnet.exe `
  D:/majiang-wt/n3-round-binding/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll `
  -class '*RoundReplayGoldenTests*' -showLiveOutput -noLogo
# exit 0；Total 6 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0

# 4. Negative + preservation matrix
powershell -NoProfile -ExecutionPolicy Bypass `
  -File C:/Users/sncdc/AppData/Local/Temp/n3-round-binding-evidence-20261025-2/run-matrix.ps1
# exit 0；46 negative exit1 + 6 preservation exit0
# positive original fixture: Total 6 / Errors 0 / Failed 0 / Skipped 0
# SOURCE_FIXTURE_SHA256=DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7
```

### 9.2 证据文件与 SHA256

| 文件 | 字节 | SHA256 |
|---|---:|---|
| `final-solution-build.log` | 3396 | `BEE33FA7CCBF6962B4B244059B6064070DDCB31822503E0FDD01FDD8FB023DA4` |
| `final-domain-full.log` | 716 | `83682FDC5AD50DDFF3127CE756A5A2A8B02ED8D456E5E14FB4EBE752FA6A1B1E` |
| `final-round-targeted.log` | 700 | `DA8FC8EAE3E26067560153CFEF1A5CC99460CBC229CD0F05C5AB4625E1BDC021` |
| `matrix-console.log` | 16778 | `7088BA44308EBA3F278473096EABF5C2AAB9CCB654DD01CB5C63A82A6A3B61FD` |
| `matrix.csv` | 24290 | `07E1C7DF785C1AB05FA99DD0935C72DFF97F72018F0AE94D66F10A9908306B2D` |
| `summary.json` | 524 | `35D94918D9212C04FD7471BAB546FC8ED452C40F1A0CE4EB8460A57F35DC9985` |
| `run-matrix.ps1` | 14107 | `5DF5D952CF2D05EDCD877D55AC47CD564A179ABB9884ADB2B8179EFD58218CC7` |
| `logs/positive.original-fixture.log` | 700 | `FE60078089C88D1AFE9467C51F0B7C75F1F72B9AF3E35324E0E74395677B17ED` |
| 测试文件 `RoundReplayGoldenTests.cs` | — | `C16B9D03B80A2C25F3FC5D49224A3AD50AAFA9FBC2227C281EA6A099E078BBB1` |
| 原始 fixture `tests/v5-golden/round-v1.json` | 43474 | `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` |

### 9.3 5 个 caller 已知探针的修复前后

| 探针 | 修复前 caller 证据 | 本交付矩阵 | 失败锚点 |
|---|---|---|---|
| `hasKanDeclared/hasRinshanDraw false->true` | `E/flags-unbound.log` 6/0/0 exit0 | `chankan.hasKanDeclared.false-to-true` exit1；`chankan.hasRinshanDraw.true` exit1 | `:520-526` |
| 终局 eventTypes 改错/加 MatchEnded | `E/event-sequence-unbound.log` 6/0/0 exit0 | `terminal.eventTypes.match-ended-plus-reordered`、`chankan.eventTypes.match-ended` 等 exit1 | `:778-827` |
| `roundTerminal.settlement.totalDeltas[0]=123456789` | `E/settlement-deltas-unbound.log` 6/0/0 exit0 | `terminal.totalDeltas.first-123456789` exit1 | `:210` |
| `phaseAfter` / afterKan / afterRon 改错 | `E/phase-unbound.log` 6/0/0 exit0 | `terminal.phaseAfter.wrong`、`chankan.phaseAfterKan.wrong`、`chankan.phaseAfterRon.wrong` exit1 | `:228-230,416,505` |
| `meldTypeAfterKan=CHOW` + `legalRonSeat1=false` | `E/meld-and-legal-unbound.log` 6/0/0 exit0 | `chankan.meldTypeAfterKan.chow`、`chankan.legalRonSeat1.false` exit1 | `:435-436,454` |

修复前的 `E` 证据保持只读，未被本任务覆盖；本交付的等价负例全部重新在 TEMP 副本执行。

### 9.4 统一负例矩阵

- 46 个 negative：全部 exit1，0 unexpected pass。每个 case 独立从原始 fixture 复制、单独变异、单独启动测试进程，避免同一 Fact 首个失败掩盖其他未绑定字段。
- 6 个 preservation：全部 exit0，0 unexpected failure，证明 caller-supplied identity 被真实保留。
- 1 个 positive：原始 fixture 6 pass / 0 fail / 0 skip / 0 Not Run，exit0。
- 完整 case 名称、mutation、exit、日志路径、fixture SHA 见 `matrix.csv`；脚本见附录 A。

代表性失败日志：

| case | SHA256 |
|---|---|
| `logs/negative.chankan.hasKanDeclared.false-to-true.log` | `5822C467606C91DD62AD72689F1FE70F0AEB07DBDE415A4176ECC1C3A4954C47` |
| `logs/negative.terminal.eventTypes.match-ended-plus-reordered.log` | `37A584C12B004636D62E148830995BBC724EEC65C02583596B2FEE5CCBD5B30C` |
| `logs/negative.chankan.eventTypes.match-ended.log` | `9F0E434EF1097D8C2797BFE72DCB78667DD37C79D016CE4606D425A6C84C4CCB` |
| `logs/negative.terminal.totalDeltas.first-123456789.log` | `2076C47BC8271904A7D81A48F540BEFA8667F0330CDE4820F67223BC3A3DDD18` |
| `logs/negative.terminal.phaseAfter.wrong.log` | `68167991FE7916B959396346DB1507EFE41AF4B12049EB14502922C0602DDCD1` |
| `logs/negative.chankan.phaseAfterKan.wrong.log` | `1D1B38D3046115815D0BD2B939AA06F7727E06E4346D1C29DD055DC512E484BC` |
| `logs/negative.chankan.phaseAfterRon.wrong.log` | `C37609275EE61DC660A8BB6F0E5934BCE54DF604923124DCAFBC38D333CA9D8E` |
| `logs/negative.chankan.meldTypeAfterKan.chow.log` | `22796BC085D8EB826B54207A24E7EB93E7502CC983832BA713E4F31FD2A01CD4` |
| `logs/negative.chankan.legalRonSeat1.false.log` | `E8E3930528CA18997CBF33434D00D32C25256744795BBCE9F97FF9B326A197E2` |

## 10. 残余责任与不宣称

- **N3 整体未完成**：本任务只覆盖 `round-v1.json` 的 `roundTerminal`/`chankanRon` 消费者；manifest 的 20 项 partial、G-028a 的完整口径、其他 GoldenTests / catalog / verifier 不在本任务范围。
- **G1 仍 FAIL**：门 2（黄金行为全字段对齐）与门 5（三绝技权威链）不由本任务关闭；N1b 出千接线、N2 整体宿主接线仍在其他责任范围。
- **G5 NOT_RUN**：PG/WSS/Unity/IL2CPP/Android/iOS/设备/弱网/跨物理网络/远端 CI/Kotlin 全量均未运行；不拿历史 CI 或本范围测试代替。
- **V4 唯一生产**：本任务没有修改生产代码；不宣称 P4.1/P5.1/完整 N3 完成。
- **未改 `complete.flag`**：未创建、未修改。
- **未 push / 未 PR / 未合并**：最终状态见 §11。

## 11. 最终交付状态

- 本地最小 commit 包含：强化后的 `RoundReplayGoldenTests.cs` + 本报告。
- 提交后 `git status --porcelain=v1` 应为空；`git diff --check HEAD^ HEAD` exit0。
- 最终 commit SHA 以本次会话交付回复中的 `git rev-parse HEAD` 为准；报告与测试同笔提交，不再追加生产文件。

## 附录 A：参数化负例脚本（TEMP 原样）

脚本路径：`C:/Users/sncdc/AppData/Local/Temp/n3-round-binding-evidence-20261025-2/run-matrix.ps1`
SHA256：`5DF5D952CF2D05EDCD877D55AC47CD564A179ABB9884ADB2B8179EFD58218CC7`

```powershell
param(
    [string]$Repo = 'D:/majiang-wt/n3-round-binding',
    [string]$EvidenceRoot = 'C:\Users\sncdc\AppData\Local\Temp\n3-round-binding-evidence-20261025-2',
    [string]$Dotnet = 'D:/Unity/dotnet/dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$testDll = Join-Path $Repo 'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll'
$sourceFixture = Join-Path $Repo 'tests/v5-golden/round-v1.json'
$fixtureRoot = Join-Path $EvidenceRoot 'fixture-root'
$fixture = Join-Path $fixtureRoot 'tests/v5-golden/round-v1.json'
$logs = Join-Path $EvidenceRoot 'logs'
New-Item -ItemType Directory -Path (Split-Path $fixture) -Force | Out-Null
New-Item -ItemType Directory -Path $logs -Force | Out-Null

$sourceHashBefore = (Get-FileHash $sourceFixture -Algorithm SHA256).Hash
$results = New-Object System.Collections.Generic.List[object]
$oldRoot = $env:MAHJONG_REPO_ROOT
$env:MAHJONG_REPO_ROOT = $fixtureRoot

function Reset-Fixture {
    Copy-Item -LiteralPath $sourceFixture -Destination $fixture -Force
}

function Invoke-Case {
    param(
        [string]$Kind,
        [string]$Name,
        [string]$Expectation,
        [scriptblock]$Mutate
    )

    Reset-Fixture
    $document = Get-Content -LiteralPath $fixture -Raw | ConvertFrom-Json
    & $Mutate $document
    $document | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $fixture -Encoding utf8
    $changedHash = (Get-FileHash $fixture -Algorithm SHA256).Hash
    if ($changedHash -eq $sourceHashBefore) {
        throw "Mutation '$Name' did not change the fixture bytes."
    }

    $log = Join-Path $logs ($Kind + '.' + $Name + '.log')
    & $Dotnet $testDll -class '*RoundReplayGoldenTests*' -showLiveOutput -noLogo *> $log
    $exit = $LASTEXITCODE
    $text = Get-Content -LiteralPath $log -Raw
    $failureLine = ($text -split "`r?`n" | Where-Object { $_ -match '\[FAIL\]|Assert\.|Exception|error' } | Select-Object -First 1)
    $counts = ($text -split "`r?`n" | Where-Object { $_ -match 'Total: \d+, Errors:' } | Select-Object -Last 1)
    $failedAsExpected = $Kind -eq 'negative' -and $exit -ne 0 -and $text -match '\[FAIL\]'
    $preservedAsExpected = $Kind -eq 'preservation' -and $exit -eq 0 -and $text -match 'Total: 6, Errors: 0, Failed: 0, Skipped: 0'

    $results.Add([pscustomobject]@{
        Kind = $Kind
        Case = $Name
        Expectation = $Expectation
        Exit = $exit
        OutcomeExpected = ($failedAsExpected -or $preservedAsExpected)
        Counts = $counts
        FailureLine = $failureLine
        Log = $log
        FixtureSha256 = $changedHash
    })

    if ($Kind -eq 'negative' -and -not $failedAsExpected) {
        throw "Negative case '$Name' did not fail with a framework [FAIL] (exit $exit)."
    }
    if ($Kind -eq 'preservation' -and -not $preservedAsExpected) {
        throw "Preservation case '$Name' did not pass 6/0/0 (exit $exit)."
    }
    Write-Output ("{0}-PASS {1} exit={2} {3}" -f $Kind.ToUpperInvariant(), $Name, $exit, $counts)
}

# Five caller-known defects: negative cases.

Invoke-Case 'negative' 'chankan.hasKanDeclared.false-to-true' 'known probe 1: boolean must come from result events' {
    param($d)
    $d.chankanRon.expected.hasKanDeclared = $true
}

Invoke-Case 'negative' 'terminal.eventTypes.match-ended-plus-reordered' 'known probe 2a: exact lifecycle order' {
    param($d)
    $d.roundTerminal.expected.eventTypes = @('MatchEnded', 'RoundEnded', 'ScoreSettled', 'TsumoWin')
}

Invoke-Case 'negative' 'chankan.eventTypes.match-ended' 'known probe 2b: MatchEnded must fail' {
    param($d)
    $d.chankanRon.expected.eventTypes = @('MatchEnded')
}

Invoke-Case 'negative' 'terminal.totalDeltas.first-123456789' 'known probe 3: input deltas must equal real result' {
    param($d)
    $d.roundTerminal.settlement.totalDeltas[0] = 123456789
}

Invoke-Case 'negative' 'terminal.phaseAfter.wrong' 'known probe 4a: expected phase binding' {
    param($d)
    $d.roundTerminal.expected.phaseAfter = 'CHEAT_WINDOW'
}

Invoke-Case 'negative' 'chankan.phaseAfterKan.wrong' 'known probe 4b: expected phase binding' {
    param($d)
    $d.chankanRon.expected.phaseAfterKan = 'ROUND_END'
}

Invoke-Case 'negative' 'chankan.phaseAfterRon.wrong' 'known probe 4c: expected phase binding' {
    param($d)
    $d.chankanRon.expected.phaseAfterRon = 'CHANKAN_WINDOW'
}

Invoke-Case 'negative' 'chankan.meldTypeAfterKan.chow' 'known probe 5a: meld type must bind physical map' {
    param($d)
    $d.chankanRon.expected.meldTypeAfterKan = 'CHOW'
}

Invoke-Case 'negative' 'chankan.legalRonSeat1.false' 'known probe 5b: legal Ron flag must bind window' {
    param($d)
    $d.chankanRon.expected.legalRonSeat1 = $false
}

# Exact lifecycle sequence: missing/duplicate/out-of-order/extra.

Invoke-Case 'negative' 'terminal.eventTypes.remove-score-settled' 'missing ScoreSettled semantic slot' {
    param($d)
    $d.roundTerminal.expected.eventTypes = @('TsumoWin', 'RoundEnded')
}

Invoke-Case 'negative' 'terminal.eventTypes.duplicate-score-settled' 'duplicate ScoreSettled' {
    param($d)
    $d.roundTerminal.expected.eventTypes = @('TsumoWin', 'ScoreSettled', 'ScoreSettled', 'RoundEnded')
}

Invoke-Case 'negative' 'terminal.eventTypes.round-ended-before-score-settled' 'ScoreSettled/RoundEnded order' {
    param($d)
    $d.roundTerminal.expected.eventTypes = @('TsumoWin', 'RoundEnded', 'ScoreSettled')
}

Invoke-Case 'negative' 'terminal.eventTypes.extra-match-ended' 'extra MatchEnded' {
    param($d)
    $d.roundTerminal.expected.eventTypes = @('TsumoWin', 'ScoreSettled', 'RoundEnded', 'MatchEnded')
}

Invoke-Case 'negative' 'chankan.eventTypes.remove-score-settled' 'missing ScoreSettled semantic slot' {
    param($d)
    $d.chankanRon.expected.eventTypes = @('RonWin', 'RoundEnded')
}

Invoke-Case 'negative' 'chankan.eventTypes.duplicate-score-settled' 'duplicate ScoreSettled' {
    param($d)
    $d.chankanRon.expected.eventTypes = @('RonWin', 'ScoreSettled', 'ScoreSettled', 'RoundEnded')
}

Invoke-Case 'negative' 'chankan.eventTypes.round-ended-before-score-settled' 'ScoreSettled/RoundEnded order' {
    param($d)
    $d.chankanRon.expected.eventTypes = @('RonWin', 'RoundEnded', 'ScoreSettled')
}

Invoke-Case 'negative' 'chankan.eventTypes.extra-match-ended' 'extra MatchEnded' {
    param($d)
    $d.chankanRon.expected.eventTypes = @('RonWin', 'ScoreSettled', 'RoundEnded', 'MatchEnded')
}

Invoke-Case 'negative' 'terminal.eventTypes.missing-field' 'missing required array' {
    param($d)
    $d.roundTerminal.expected.PSObject.Properties.Remove('eventTypes')
}

Invoke-Case 'negative' 'chankan.hasRinshanDraw.wrong-type' 'wrong JSON type is not coerced' {
    param($d)
    $d.chankanRon.expected.hasRinshanDraw = 'false'
}

Invoke-Case 'negative' 'terminal.phaseBefore.wrong' 'fixed construction must match fixture' {
    param($d)
    $d.roundTerminal.input.phaseBefore = 'CHEAT_WINDOW'
}

Invoke-Case 'negative' 'terminal.phaseBefore.unknown' 'unknown phase must fail explicitly' {
    param($d)
    $d.roundTerminal.input.phaseBefore = 'MATCH_END'
}

Invoke-Case 'negative' 'chankan.phaseAfterKan.unknown' 'unknown phase must fail explicitly' {
    param($d)
    $d.chankanRon.expected.phaseAfterKan = 'MATCH_END'
}

Invoke-Case 'negative' 'terminal.versionBefore.wrong' 'pre-execution version binding' {
    param($d)
    $d.roundTerminal.input.versionBefore = 1
}

Invoke-Case 'negative' 'terminal.versionAfter.wrong' 'post-execution version binding' {
    param($d)
    $d.roundTerminal.expected.versionAfter = 999
}

Invoke-Case 'negative' 'terminal.resultOutcome.wrong' 'result outcome binding' {
    param($d)
    $d.roundTerminal.expected.resultOutcome = 'Ron'
}

Invoke-Case 'negative' 'terminal.settlement.outcome.wrong' 'settlement outcome must be Tsumo' {
    param($d)
    $d.roundTerminal.settlement.outcome = 'Ron'
}

Invoke-Case 'negative' 'terminal.rejectedActions.remove-agari-yame' 'rejected action list must be exact' {
    param($d)
    $d.roundTerminal.expected.rejectedActions = @('StartStacking', 'DeclareTsumo')
}

Invoke-Case 'negative' 'terminal.rejectedActions.unknown-action' 'unknown action must fail explicitly' {
    param($d)
    $d.roundTerminal.expected.rejectedActions = @('StartStacking', 'DeclareTsumo', 'DeclareAgariYame', 'UnknownAction')
}

Invoke-Case 'negative' 'terminal.resultFinalScores.wrong' 'result final scores binding' {
    param($d)
    $d.roundTerminal.expected.resultFinalScores = @(37001, 21000, 21000, 20999)
}

Invoke-Case 'negative' 'terminal.resultDeltas.wrong' 'result deltas binding' {
    param($d)
    $d.roundTerminal.expected.resultDeltas = @(12001, -4000, -4000, -4001)
}

Invoke-Case 'negative' 'terminal.settlement.finalScores.sum-preserving' 'changed final scores change real deltas' {
    param($d)
    $d.roundTerminal.settlement.finalScores = @(36000, 21000, 21000, 22000)
}

Invoke-Case 'negative' 'terminal.settlement.remainingSticks.wrong' 'score/stick conservation' {
    param($d)
    $d.roundTerminal.settlement.remainingSticks = 1
}

Invoke-Case 'negative' 'terminal.settlement.completedAtUtc.invalid-format' 'wrong timestamp format fails' {
    param($d)
    $d.roundTerminal.settlement.completedAtUtc = 'not-a-timestamp'
}

Invoke-Case 'negative' 'terminal.settlement.resultId.empty' 'blank result id is rejected' {
    param($d)
    $d.roundTerminal.settlement.resultId = ''
}

Invoke-Case 'negative' 'chankan.eligibleSeats.extra-seat' 'eligible seat list must be exact' {
    param($d)
    $d.chankanRon.input.eligibleSeats = @(1, 2)
}

Invoke-Case 'negative' 'chankan.ronSeat.wrong' 'ron seat binding' {
    param($d)
    $d.chankanRon.input.ronSeat = 2
}

Invoke-Case 'negative' 'chankan.meldTiles.reorder' 'physical meld slot order' {
    param($d)
    $d.chankanRon.input.meldTiles = @(18, 16, 17)
}

Invoke-Case 'negative' 'chankan.addedTileId.wrong' 'physical drawn tile binding' {
    param($d)
    $d.chankanRon.input.addedTileId = 20
}

Invoke-Case 'negative' 'chankan.meldTiles.missing' 'missing physical input' {
    param($d)
    $d.chankanRon.input.PSObject.Properties.Remove('meldTiles')
}

Invoke-Case 'negative' 'chankan.resultId.empty' 'blank result id is rejected' {
    param($d)
    $d.chankanRon.expected.resultId = ''
}

Invoke-Case 'negative' 'chankan.finalScores.sum-preserving' 'changed final scores change real deltas' {
    param($d)
    $d.chankanRon.expected.finalScores = @(22000, 28000, 25000, 25000)
}

Invoke-Case 'negative' 'chankan.totalDeltas.wrong' 'result deltas binding' {
    param($d)
    $d.chankanRon.expected.totalDeltas = @(-2600, 2600, 0, 1)
}

Invoke-Case 'negative' 'chankan.winner.wrong' 'winner binding' {
    param($d)
    $d.chankanRon.expected.winner = 2
}

Invoke-Case 'negative' 'chankan.loser.wrong' 'loser binding' {
    param($d)
    $d.chankanRon.expected.loser = 1
}

Invoke-Case 'negative' 'chankan.hasRinshanDraw.true' 'event Contains binding' {
    param($d)
    $d.chankanRon.expected.hasRinshanDraw = $true
}

Invoke-Case 'negative' 'chankan.versionAfterRon.wrong' 'post-ron version binding' {
    param($d)
    $d.chankanRon.expected.versionAfterRon = 3
}

# Opaque caller-supplied identities / synthetic timestamps are preservation controls:
# C# cannot independently know a different valid UUID/round id/timestamp is "wrong"; it must
# preserve exactly the supplied value. These cases must stay green.

Invoke-Case 'preservation' 'terminal.resultId.changed-valid' 'opaque result id is preserved' {
    param($d)
    $d.roundTerminal.settlement.resultId = '11111111-2222-3333-4444-555555555555'
}

Invoke-Case 'preservation' 'terminal.roundId.changed-valid' 'caller round id is preserved' {
    param($d)
    $d.roundTerminal.input.roundId = 'rg-001-terminal-copy'
}

Invoke-Case 'preservation' 'terminal.completedAtUtc.changed-valid' 'synthetic timestamp is preserved' {
    param($d)
    $d.roundTerminal.settlement.completedAtUtc = '2026-10-23T00:00:01.0000000+00:00'
}

Invoke-Case 'preservation' 'terminal.completedAtMonotonic.changed-valid' 'synthetic monotonic time is preserved' {
    param($d)
    $d.roundTerminal.settlement.completedAtMonotonicMillis = 1001
}

Invoke-Case 'preservation' 'chankan.resultId.changed-valid' 'opaque result id is preserved' {
    param($d)
    $d.chankanRon.expected.resultId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
}

Invoke-Case 'preservation' 'chankan.roundId.changed-valid' 'caller round id is preserved' {
    param($d)
    $d.chankanRon.input.roundId = 'rg-002-chankan-copy'
}

Reset-Fixture
$positiveLog = Join-Path $logs 'positive.original-fixture.log'
& $Dotnet $testDll -class '*RoundReplayGoldenTests*' -showLiveOutput -noLogo *> $positiveLog
$positiveExit = $LASTEXITCODE
if ($positiveExit -ne 0) {
    throw "Positive original-fixture control failed with exit $positiveExit."
}
$positiveText = Get-Content -LiteralPath $positiveLog -Raw
if ($positiveText -notmatch 'Total: 6, Errors: 0, Failed: 0, Skipped: 0') {
    throw "Positive original-fixture control did not report six passing facts."
}

$sourceHashAfter = (Get-FileHash $sourceFixture -Algorithm SHA256).Hash
if ($sourceHashAfter -ne $sourceHashBefore) {
    throw "Original fixture changed: $sourceHashBefore -> $sourceHashAfter"
}

$results | Export-Csv -NoTypeInformation -Encoding utf8 (Join-Path $EvidenceRoot 'matrix.csv')
$summary = [pscustomobject]@{
    SourceFixture = $sourceFixture
    SourceSha256Before = $sourceHashBefore
    SourceSha256After = $sourceHashAfter
    NegativeCases = @($results | Where-Object { $_.Kind -eq 'negative' }).Count
    NegativeUnexpectedPasses = @($results | Where-Object { $_.Kind -eq 'negative' -and -not $_.OutcomeExpected }).Count
    PreservationCases = @($results | Where-Object { $_.Kind -eq 'preservation' }).Count
    PreservationUnexpectedFailures = @($results | Where-Object { $_.Kind -eq 'preservation' -and -not $_.OutcomeExpected }).Count
    PositiveExit = $positiveExit
    PositiveSummary = 'Total: 6, Errors: 0, Failed: 0, Skipped: 0'
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'summary.json') -Encoding utf8
$summary | Format-List
Write-Output 'MATRIX-COMPLETE'
```
