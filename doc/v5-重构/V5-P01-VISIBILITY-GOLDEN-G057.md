# V5 P0.1-a/P0.1-b 可见性/快照深拷贝黄金 —— G-057a..G-057d（R1 返修）

日期：2026-10-25
任务：G-057a 旧 `PlayerView` 快照深拷贝、G-057b 事件列表深拷贝与逐席可见性、G-057c 公开投影白名单/授权里宝/不可变、G-057d 真实 136 实体状态的 owner/他座/观战隔离。
性质：**严格 test-only**。不修改 V4 生产代码、V5 生产代码、旧五组 fixture、manifest/catalog、既有测试或已接受审计报告。
本轮：针对复核 `D-G057-R1-01..04` 批量返修；只改原 5 个 expectedFiles。
结论：R1 四组 MAJOR 全部修复并有真实负例；剩余 V4 既有隐私差异与明确的模型差异见 §8。**G1 仍未批准，G5 未完成，V4 仍是唯一生产架构**；没有任何平台通过声明。

---

## 1. 基线、范围、归属

| 项 | 实测值 |
|---|---|
| worktree | `D:/majiang-wt/visibility-golden-g057` |
| branch | `wt/v5-visibility-golden-g057` |
| 冻结 base | `1432d775526de921f2cde3aba283236fc3b8e8c1` |
| R1 起始 HEAD | `544f9c1a306a135254f0fe6a1dee139e5a30d381` |
| base 是否为祖先 | 是 |
| 交付 | 仍仅 5 个 expectedFiles；无第 6 个跟踪文件 |

```text
engine/src/test/kotlin/com/majiang/projection/V5VisibilityGoldenExporterTest.kt
tests/v5-golden/visibility-v1.json
dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs
tools/v5-golden/probe-visibility-binding.ps1
doc/v5-重构/V5-P01-VISIBILITY-GOLDEN-G057.md
```

未改 V4/C# 生产、旧 fixtures、`catalog-v1.json`、manifest、兄弟任务或用户主 checkout。

---

## 2. 复核缺陷逐条修复映射

### D-G057-R1-01（MAJOR）：遗漏现有完整生产映射路径

- **实际（复核）**：新测试只到 metadata；`TableProjectedEvent`/`ProtocolEventRecord` 空 payload 被当成“V5 无字段”。但 `TableProjectionContractMapper.MapEvent/MapPublicSnapshot/MapPlayerSnapshot`、`TableProjectionEventFacts.Breakdown`、`ScoreBreakdownView` 已存在；`MapPlayerSnapshot` 已支持袖牌/owner meld。
- **修复**：
  - `VisibilityGoldenTests` 新增 `FixtureEvent`/`Facts`，从 fixture 的 raw INPUT 事件构建 `TableProjectionEventFacts`，调用 `MapEvent` 并逐字段检查 `ProjectedEventPayload`。
  - C 用例授权分支比较 `Breakdown.UraTiles/UraHits/TotalPoints/DoraCount/UraCount/Yaku`；未授权分支不伪称自动裁剪：对“raw breakdown + `uraAuthorized=false`”实测拒绝（`ScoreBreakdownView` 抛 `ArgumentException`），无 breakdown 的未授权 Tsumo 可映射且 `Breakdown=null`。
  - `MapPublicSnapshot` 用 typed settlement records 重建并逐事件 payload 非空。
  - D 用例调用 `MapPlayerSnapshot`：owner seat1 的 `Private.SleeveTiles`/`OwnerMelds`/ledger 与 owner extension/V4 逐值一致；seat0 空；观战无。
  - `sourceHashes` 新增 `TableProjectionContractMapper.cs`、`ProtocolProjectionPayload.cs`、`TableProjectionContractMapperTests.cs`；`fieldClassification` 改为按实际 mapped payload 分类。
- **验收断言**：C# 定向 8/8；`negative-input-breakdown-ura`、`negative-output-breakdown-ura` 真实 exit1；袖牌 owner 有/他座观战无；`MapEvent` payload 不允许空 metadata 通过。

### D-G057-R1-02（MAJOR）：可比字段遗漏/输入被忽略，probe 漏类别

- **实际（复核）**：14 个单字段变异仍绿；副露 `fromSeat/calledTileId`、事件 `turn/fromSeat`、clock、cheat、hands/discards 重复表示均未真正绑定。
- **修复**：
  - `BuildScenario` 增加输入一致性：`currentPlayer==dealerSeat`、`wallRemaining==` 稠密墙数、`hands.closed/drawn` 与 HAND placement、`discards` 与 DISCARD placement、sleeve、melds 与 MELD slots 逐项相等。
  - `AssertBoardCommon` 增加副露 `fromSeat`/`calledTileId`；owner meld 同样逐值。
  - `AssertScenarioEvents` 对每个可见事件调用 `MapEvent`，比较 payload `TurnNumber/Seat/TileIds/Meld/FromSeat/RiichiDiscard/Tsumogiri/WallRemaining/RoundWind/RoundNumber`；`BuildFixtureEvents` 校验 turn 非降序。
  - clock：从 fixture `input.clock` 构建 `ClockView`，与 `expected.clock` 比较 `ServerTimeMs/DeadlineRemainingMs/ReserveRemainingMs/DecisionSeat`；清空 caller clock 列表后旧快照仍保留；换 clock 后新快照变化、旧不变。
  - cheat：`BuildCheat` 核对每席 points；`AssertCheat` 比较 `entryId/amount/balanceAfter/reason`。
  - probe 新增 18 个关键负例（副露 fromSeat/called/牌、currentPlayer、弃牌/手牌重复表示、事件 turn/fromSeat、clock、copyProbe、cheat points/reason、合法 136 两位置交换、breakdown ura 输入/输出）；原 20 负例与 5 正例保留。
- **验收断言**：上述每项变异均真实 exit1/Failed≥1/Errors0/Skipped0/NotRun0；正表示控制全 PASS；总 native run=44。

### D-G057-R1-03（MAJOR）：默认 exporter 不核对真实 V4 结果

- **实际（复核）**：只查 schema/case ID；陈旧分数、实体、副露或缺失 fixture 都通过。
- **修复**：
  - 默认模式把本次 `buildCases()` 的四 case 序列化文本与 fixture `cases` **完整逐字节比较**；同时重算 `renderSha256LfNormalized`。
  - 缺 fixture、stale expected、stale placement、stale meld 均在新增 `defaultModeRejectsStaleOrMissingFixture` 中 `AssertionError`。
  - 默认仍不写 fixture；显式 export 才写。
- **验收断言**：未改 fixture 默认测试通过且文件 SHA 不变；隔离副本改 `scores[0]=24999` 后默认 Gradle 实测 exit1/Failed=1（§7）。

### D-G057-R1-04（MAJOR）：快照深拷贝测试的源变异无效

- **实际（复核）**：`Scenario.Placements/Scores/Dora` 在建 map/RoundSpec 后修改，已与投影输入无关；没有“后续权威状态变化 → 新投影不同、旧快照不变”的敏感性控制。
- **修复**：
  - G-057a 用真实 `RoundCommand.Discard` 产生新的 `RoundStateMachine`，重新投影：新投影 river 增加、手牌变化；旧 V4-golden 快照逐值不变。
  - G-057c 用真实 `ClockView` 输入：旧快照保留旧 clock，新 context/不同 clock 的新快照显示新值；清空 caller clock 列表不改变旧快照。
  - 保留并加强只读/producer 数组复制；新增 `TableMeldFact` tileIds、`TableSeatPublicFacts` discards 的构造层复制证明。
  - 文档明确：构造层复制与不可变快照分别证明，不宣称未测试的层。
- **验收断言**：新增真实 discard/clock 敏感性断言通过；旧快照逐值等于 V4 golden；只读 Add 实际抛 `NotSupportedException`。

---

## 3. Fixture、来源与默认校验

- 生成命令：

```powershell
$env:V5_VISIBILITY_GOLDEN_EXPORT='1'
.\gradlew.bat :engine:test --tests "com.majiang.projection.V5VisibilityGoldenExporterTest" --rerun-tasks --console=plain
```

- 默认：真实执行四 case + 全 case 内容比较 + render hash；不写文件。
- 双次构造/渲染字节一致；导出变化日志列出 changed top-level keys，不静默忽略。
- `sourceHashes[]` 现覆盖 20 个 V4/V5 源码/测试（含 mapper/payload/mapper tests），raw bytes 与 LF-normalized 明确分开。
- fixture 实际值：

| 项 | 值 |
|---|---|
| path | `tests/v5-golden/visibility-v1.json` |
| bytes | 139876 |
| raw SHA-256 | `b901eb6d07cb1da5df0f7c4f3c39a259273cd35b6307b2f2d0913e9f228d385b` |
| LF-normalized SHA-256 | 同上（LF 文件） |
| `renderSha256LfNormalized` | `8d28b1f0a71efe9d87d87f36c549e5f8fefcec3d319bbe1e3e8da74af4a7d8c2` |
| `v4ProductionTreeClean` | true |
| `trackedModifiedFiles` | 4 个 task path（提交后为 0 diff） |

事件 INPUT 现在包含：`turnNumber`、`seat`、`tileIds`、`isUraDora`、`fromSeat`、`calledTileId`、`riichiDiscard`、`tsumogiri`、`wallRemaining`、`roundWind/roundNumber`、`yaku/han/fu/score`、`breakdown{uraTiles,uraHits,uraHitTiles,indicatorTiles,doraCount,uraCount,totalPoints,han,fu,score,yaku,uraAuthorized}`。C 用例另有 `expected.clock`。

---

## 4. 完整绑定字段

<details>
<summary><b>V4 → V5 typed projection → MapEvent/MapPublicSnapshot/MapPlayerSnapshot</b></summary>

| V4 字段/行为 | V5 路径 | 断言 |
|---|---|---|
| phase/round/wind/honba/riichi/dealer/current/wallRemaining | Board / MapPhase | 逐值 |
| scores Int | ScoreView.Score long | 逐值 |
| doraIndicators | Board.DoraIndicators | 逐值 |
| riichiStatus | PublicFacts.Riichi → PlayerPublicView.Riichi | 逐值 |
| discards | DiscardView.TileId/顺序 | 逐值；turnNumber 表示映射 |
| meld type/tiles/fromSeat/calledTileId | TableMeldFact → MeldView / owner MeldView | 全部逐值 |
| handTileCounts | PlayerPublicView.HandCount | 逐值 |
| myClosedTiles/myDrawnTile/myAllClosedTiles | Private.ClosedTiles/DrawnTileId | 逐值 |
| mySleeveTiles | Owner.SleeveTiles → MapPlayerSnapshot → Private.SleeveTiles | owner 逐值 |
| myFuriten/myDiscardHistory | Private.Furiten/DiscardHistory | 逐值 |
| lastDiscard/lastDiscardPlayer | Board.LastDiscard + river 推导 | 逐值 |
| events turnNumber | EventFacts.TurnNumber → payload.TurnNumber | 逐值 |
| Chi/Pon fromSeat/calledTileId | EventFacts.Meld/FromSeat → payload.Meld/FromSeat | 逐值 |
| TileDrawn wallRemaining | EventFacts.WallRemaining → payload.WallRemaining | 逐值 |
| TileDiscarded riichi/tsumogiri | EventFacts → payload flags | 逐值 |
| RoundEnded wind/number | EventFacts → payload | 逐值 |
| TsumoWin yaku/han/fu/score | EventFacts → payload | 逐值 |
| TsumoWin breakdown ura | EventFacts.Breakdown → ScoreBreakdownView | 授权逐值；未授权 raw 拒绝 |
| clock serverNow/deadline/reserve/seat | ClockView | 逐值 |
| privateCheat points/entry fields | BuildCheat/Owner.Cheat/MapOwnerCheat | 逐值 |
| 136 placements | TilePositionMap | 结构 + 真实 view 值 |

</details>

---

## 5. 测试计数与 exit（最终实跑）

| 项 | exit | 计数 |
|---|---:|---|
| 新鲜 Release solution `--no-incremental` | 0 | 0 warning / 0 error |
| TableRuntime.Tests 构建 | 0 | 0 warning / 0 error |
| C# `VisibilityGoldenTests` 定向 | 0 | Total=8, Errors=0, Failed=0, Skipped=0, NotRun=0 |
| TableRuntime 全量 | 0 | Total=373, Errors=0, Failed=0, Skipped=0, NotRun=0 |
| Domain TilePositionMapTests | 0 | 59/0 |
| Domain TileMovementGoldenTests | 0 | 10/0 |
| Domain CheatVisibilityTests | 0 | 10/0 |
| Contracts ProjectionWireMapperTests | 0 | 16/0 |
| Contracts ProtocolContractTests | 0 | 30/0 |
| Gradle exporter | 0 | tests=2, failures=0, errors=0, skipped=0 |
| Gradle SessionSnapshotContractTest | 0 | tests=8, failures=0, errors=0, skipped=0 |
| Gradle PublicProjectionTest | 0 | tests=12, failures=0, errors=0, skipped=0 |
| Probe 全量 | 0 | **44 native runs**：baseline + 38 FAIL 负例 + 5 PASS 正例 |
| Probe `-SelfNegative` | 1 | 绿色 baseline 后故意 exit1 |
| 隔离陈旧 fixture 默认 Gradle | 1 | 2 tests, 1 failed（default 检测到 stale case） |

所有 negative 均 `Total=8 / Errors=0 / Skipped=0 / NotRun=0`，Failed≥1 且 exit1；正例 `exit0/Failed=0`。

---

## 6. Probe 矩阵（44 native runs）

原有 20 负例与 5 正例保留；新增 18 个关键负例全部实测 exit1：

| 新增负例 | 类别 | 实际 |
|---|---|---|
| `negative-input-current-player` | INPUT/一致性 | exit1 Failed=1 |
| `negative-output-meld-fromseat` | OUTPUT/副露 | exit1 Failed=1 |
| `negative-output-meld-called` | OUTPUT/副露 | exit1 Failed=1 |
| `negative-input-meld-fromseat` | INPUT/副露 | exit1 Failed=1 |
| `negative-input-meld-called` | INPUT/副露 | exit1 Failed=1 |
| `negative-input-meld-tile` | INPUT/物理牌 | exit1 Failed=1 |
| `negative-input-discard-tile` | INPUT/河重复表示 | exit1 Failed=1 |
| `negative-input-hand-tile` | INPUT/手牌重复表示 | exit1 Failed=3 |
| `negative-output-event-turn` | OUTPUT/事件 | exit1 Failed=1 |
| `negative-output-event-fromseat` | OUTPUT/事件 | exit1 Failed=1 |
| `negative-input-event-turn` | INPUT/事件 | exit1 Failed=1 |
| `negative-input-clock-deadline` | INPUT/clock | exit1 Failed=2 |
| `negative-output-copy-clocks` | OUTPUT/拷贝 | exit1 Failed=1 |
| `negative-input-cheat-points` | INPUT/账本 | exit1 Failed=2 |
| `negative-input-cheat-reason` | INPUT/账本 | exit1 Failed=1 |
| `negative-input-placement-swap` | INPUT/合法 136 身份 | exit1 Failed=1 |
| `negative-input-breakdown-ura` | INPUT/里宝 | exit1 Failed=1 |
| `negative-output-breakdown-ura` | OUTPUT/里宝 | exit1 Failed=1 |

正控制：SOURCE_ONLY legalActions/dead-wall、placement 顺序、scope note、CRLF fixture 均 PASS。计数门自检仍拒绝 Total0/short/skip/notRun/error/exit drift。

---

## 7. 关键日志与 SHA-256

| 证据 | SHA-256 |
|---|---|
| fixture raw/LF | `b901eb6d07cb1da5df0f7c4f3c39a259273cd35b6307b2f2d0913e9f228d385b` |
| `build/rework-solution-build.log` | `5c70aff3e6f86c7a1483fdc0d15fbd30bcac5f120ff61e9d9a231bb5be1f6958` |
| `build/rework-trt-build.log` | `09e8fb7123fe19ceda0b8a0616e443ae3095960d69daa6d39d20ecf39155bd49` |
| `build/rework-csharp-targeted-final.log` | `d5a16cfb76433a9c8597b5e209903a68ecc9ac271743e713cd29e531298bd7df` |
| `build/rework-trt-full-final.log` | `04356fbab01b60c849b538a99739a428b69e9073fdc9c543538af5c1b71519dd` |
| `build/rework-domain-TilePositionMapTests.log` | `7e2763c0a4f82a9cdfb85f2cfbe963844af7ee6b4d2eae45553c680e84d8b2b9` |
| `build/rework-domain-TileMovementGoldenTests.log` | `88aaf8382a2beb06846071b7b5d833e2c7f3c089bb4f781509648995ba3193ad` |
| `build/rework-domain-CheatVisibilityTests.log` | `6c8def4f5194ad3b6b790f416280612d511bb7db22a84cb8e65fc55d0d3f6f97` |
| `build/rework-contracts-ProjectionWireMapperTests.log` | `470afcd70dc7f6df8cb3388262ded40a79a8ff7b1e37c10458ecd32242f5feb5` |
| `build/rework-contracts-ProtocolContractTests.log` | `258711953a22b6888f9e3f779c934149fb42691686d32695619275c093fed27c` |
| `build/rework-gradle-final.log` | `3fef63dc990e40991867ef6561cda6f4e84516d7e03ae3396c8f74ec7676ac10` |
| `build/rework-export6.log` | `58d5732d9640ec77ad1a9a63ca26d843a3224894c3933f06c952b2f6439f3f9c` |
| `build/rework-stale-fixture.log` | `11d6633ea5698eff5826aaea5310fe5a3747df981abee25cb22154b4735d69ca` |
| `build/rework-probe-final.log` | `42aadfb73064a8a13c6807a7848a8563fb2dd1da4d045953de2e96a09706ea4f` |
| `build/rework-probe-selfneg.log` | `f11ded02839c4f1f133081b171c64b17bf52bc43befa34cba6a24ec74995e90d` |
| `build/visibility-binding-probe/20261025T100614Z-9272/results.csv` | `a8239e04089d7e3343b52e8300249fc2fa35dbbfbe2a4b9cf445bf51d57700f1` |
| 同上 `baseline.log` | `cd1f1292d76bf38e0609d00c00722c8443da1dd09c06a228e78544bb77f18f87` |
| 同上 `negative-input-clock-deadline.log` | `ecd86e1765d6d3a9537e33f81622022c03b6b9e70b0092fb32b6b5e57ff294d0` |
| 同上 `negative-input-meld-fromseat.log` | `3fa900134951c6e5fc444cf201df04122b0d297b52cb7c09448c5e45644cc93a` |
| 同上 `negative-input-breakdown-ura.log` | `05b2ae353263a80e1f86c3b06c60185ce4a8067a5d458be7d7a77cfe4c7fd642` |

---

## 8. 剩余缺陷/差异（修正后的准确口径）

**本轮 R1 四组 MAJOR：全部 FIXED。** 以下为仍存在、但不属于本轮可修范围或明确不可表示的项。

```text
[D-01-01]
严重级别：MAJOR
位置：engine/src/main/kotlin/com/majiang/session/SessionUpdate.kt:85-90；
      dotnet/src/Mahjong.TableRuntime/TableEventProjection.cs:594-614
复现：V5VisibilityGoldenExporterTest + VisibilityGoldenTests.G057bEventVisibilityAndMappedPayloadsMatchV4
实际：V4 seat0..3 各收到 1 条 CheatPointsChanged（fixture sourceOnly.v4CheatPointsChangedVisibilityCounts=[1,1,1,1]）；
      V5 仅 owner，其他席/观战 null。
期望：千术流水仅 owner；不能把 V4 else 分支移植到 V5。
证据：fixture sourceOnly 字段；build/rework-export6.log、build/rework-csharp-targeted-final.log。
修复要求：V4 冻结；V5 保持 ProtocolEventPolicy private，P3.2 不放宽。
验收断言：G057b 的 owner-only 断言保持；若 V4 修复，必须以新信息显式更新 fixture。
```

```text
[D-01-03]
严重级别：MINOR
位置：PublicTableProjection.kt:222-244；ProtocolSnapshot.cs:717-757
实际：V4 summary 有 lifecycle/scores/roundRecords/finalResult；V5 只有 finished/finishReason/finalScores/rankings。
期望/分类：仅 finished=false 可比；roundRecords 等 UNREPRESENTABLE，待 P2.1/P3.2 定义。
证据：fixture expected.session；G057c。
修复/验收：补字段后再加逐值断言；当前不冒充对齐。
```

```text
[D-01-04]
严重级别：MINOR
位置：PlayerView.kt:124-142；ProtocolSnapshot.cs:887-911
实际：V4 legalActions 深拷贝通过；V5 DecisionView 由 P3.3 提供，无同形映射。
期望/分类：UNREPRESENTABLE；不得伪造 GameAction→LegalActionView。
证据：fixture VIS-G057A；sourceOnly.legalActions。
修复/验收：P3.3 实现后另立动作映射样本。
```

```text
[D-01-05]
严重级别：MINOR
位置：PublicTableProjection.kt:482-506；TableVisibilityProjection.cs:1009-1017
实际：V4 公开暗杠四张身份；V5 公开 board 仅隐藏占位/数量。
期望/分类：V5 更严格；fixture 不含暗杠，不把差异伪装 PASS。
证据：knownDivergences VG-D03；C# 暗杠事件测试（观战空/owner 四张）。
修复/验收：新增暗杠 board 样本时明确期望 V5 隐藏。
```

**已修正的旧说明**：

- D-01-02 “V5 无 ScoreBreakdown/交 P2.1 补字段”：**失实，已撤销**。现有 `TableProjectionEventFacts.Breakdown + MapEvent + ScoreBreakdownView` 已绑定；剩余的是“mapper 不自动剥除，调用方必须显式授权/拒绝”的契约，已在 C 用例实测。
- D-01-06 “袖牌无法映射”：**失实，已撤销**。`MapPlayerSnapshot` 已把 owner sleeve/owner meld 写入 `PrivatePlayerView`；已逐值验证 owner 有、他座/观战无。

**其余差异（非缺陷）**：V4 未过滤 SessionUpdate 事件列表在 V5 无对应；V4 弃牌 turnNumber 与 V5 river index 为表示映射。

---

## 9. 边界

- **G1 仍未批准**；不导出全部 73 例，不改 catalog/manifest。
- **G5 未完成**；没有平台源码变更，未重跑 PG/WSS/Unity/IL2CPP/Android，不声称平台通过。
- **V4 仍是唯一生产架构**；未 push、未建 PR、未触发 CI、未改默认分支/全局环境。
- caller 复核通过后才统一 MR；receiver 不自行合并/推送。
