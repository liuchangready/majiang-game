# V5 P0.1 / §8.3 黄金样本目录核对与机器验证报告

日期：2026-10-24
工作树：`D:/majiang-wt/golden-catalog`
分支：`wt/v5-golden-catalog`
起点/冻结提交：`205506f07f6a1ccfef6b32cf97390431e3349f8d`（PR19 `feat/v5-1-integration -> v5` 当前已审 head）
唯一交付方式：本地 commit；未 push、未创建 PR、未触发远端 CI。
生产基线：V4 仍是唯一生产；G1 `BLOCKED`，G5 未完成。

---

## 0. 结论先行

| 项目 | 结果 | 证据 |
|---|---|---|
| 五组已接受 fixture 的目录核对 | **PASS** | `catalog-v1.json` 记录 5 个文件、真实 SHA-256、schema、V4 commit、exporter 命令、分组计数、样本 ID |
| G-xxx 语义映射与锚点 | **PASS（逐项保留）** | 73 条 G 需求全部保留；17 full、20 partial、33 none、3 v5-only；partial 不冒充整项 |
| 只读 validator 正例 | **PASS** | `verify-v5-golden-catalog.ps1` exit 0，5/73/10382/10000/42 |
| validator 负例自测 | **PASS** | 1 个正例 + 76 个 TEMP 坏输入负例；坏 catalog/SHA/重复 ID/未知 schema/缺文件均 exit 1 |
| 真实 fixture 未被自测修改 | **PASS** | 自测前后 5 个 fixture SHA-256 完全相同 |
| C# Golden 定向运行 | **PASS** | 5 个 reader 类、42 Facts、0 failed、0 skipped，exit 0 |
| 全 73 条 G 需求 | **NOT DONE** | 33 none、20 partial；不得写 73/73 或 G1 PASS |
| 重新导出全部 V4 / P3.x | **NOT_RUN（范围外）** | 本任务只核对现有五组；不重做 physical 全量或 P3.x |
| Unity/IL2CPP/Android/iOS 真机/真实网络/PG/WSS/CI | **NOT_RUN** | 本任务不涉及；不得声称通过 |

**不能从本报告得出的结论：**“golden 100% 已完成”“G1 已通过”“V5 已生产”“五组 fixture 覆盖了 73 条需求”。本报告只证明：现有五组文件真实存在、可机械校验、可被 5 个 C# reader 定向读取；未导出/部分覆盖项仍按责任任务保留。

---

## 1. 授权范围与未改动项

### 1.1 本任务写范围

1. `tests/v5-golden/catalog-v1.json`（新增）
2. `tools/verify-v5-golden-catalog.ps1`（新增）
3. `tools/tests/VerifyV5GoldenCatalog.Tests.ps1`（新增）
4. `doc/v5-重构/V5-0-GOLDEN-MANIFEST.md`（更新当前事实、逐项状态、分母；保留历史盘点）
5. `doc/v5-重构/V5-GOLDEN-CATALOG-RECONCILIATION.md`（本报告，新增）

### 1.2 只读/未改动

- 五组 fixture：`tests/v5-golden/{physical,scoring,round,cheat,shuffle}-v1.json` 未改；自测在独占 TEMP 副本中注入坏输入。
- 五个 V4 exporter：未改、未重跑原 physical 全量、未补写导出器。
- 五个 C# Golden reader：未改、未放宽断言。
- V4 `Domain`/`Ai`/`TableRuntime`、Unity 环境/Resources/Burst、CI wrapper、其他文档：未改。
- 兄弟工作树/线程：未读取覆盖、未合入未验收代码、未接管；G-049 AI exporter 仍在独立冻结树，其 `ai-v1.json` 未接受，本目录不纳入。

---

## 2. 五组 fixture 的真实事实

所有值由 `Get-FileHash -Algorithm SHA256` 从实际文件计算；catalog 由 `verify-v5-golden-catalog.ps1` 再算一遍并比对。

| fixture | bytes | SHA-256 | schema | 内嵌 V4 commit | 具体 case |
|---|---:|---|---|---|---:|
| `physical-v1.json` | 836,369 | `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995` | `v5-physical-golden-v1` | `32f645ec9b134e9ad261a2df89437416b20b3119` | 6 |
| `scoring-v1.json` | 8,315,131 | `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E` | `v5-scoring-golden-v1` | `97831c88f664a0ff6589b7fa062464d2ca057e29` | 10,238 |
| `round-v1.json` | 43,474 | `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` | `v5-round-golden-v1` | `21a7d2c495397b943fc1a170dc23ebc0dc77d4e9` | 25 |
| `cheat-v1.json` | 42,120 | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` | `v5-cheat-golden-v1` | `a96c195c852e035ff6434584135718bbb8a05d29` | 81 |
| `shuffle-v1.json` | 99,467 | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` | `v5-shuffle-golden-v1` | `37b4dd610c5e7908dc4938378278c1475e0eee73` | 32 |

说明：

- 五组文件冻结在 PR19 head `205506f`；文件内嵌 `v4Commit` 是各自原始导出时的 V4 提交，**不是**都等于 `205506f`。catalog 分别记录 `sourceV4Commit`，不把冻结提交冒充 V4 导出提交。
- `physical-v1.json` 的 `exporterSourceSha256` 是 CRLF→LF 规范化后的 SHA-256；catalog 同时记录 exporter 原始 SHA 与 LF 规范化 SHA，validator 两种都重算。其 LF 值与文件内声明一致：`d1d72e393a3ee455016b15a17252d2ae6b74acd9478c8822dfd3b20acaf968ac`。
- `round-v1.json` 还记录 `v4EngineSourceCommit=3f5370c...` 与 `v4DotnetRulesCommit=21a7d2c...`；catalog 保留这两个字段，不合并成一个来源。

### 2.1 分组计数（catalog 机械声明）

| fixture | 分组 | count | ID/键 |
|---|---|---:|---|
| physical | `samples` | 6 | `G-001a`…`G-003b` |
| scoring | `winSamples` / `noWinSamples` / `fuSamples` / `doraSamples` / `settlementSamples` / `scoreSamples` / `randomSamples` | 48 / 4 / 9 / 6 / 24 / 147 / 10,000 | 具体 ID；random 用 `^R-[0-9]{6}$` + first/last |
| round | `matchProgression` / `decisionClock` / `roundTerminal` / `chankanRon` / `controlEpoch` | 16 / 6 / 1 / 1 / 1 | `RG-003a..p` / `RG-004a..f` / `RG-001` / `RG-002` / `RG-005` |
| cheat | `tileMarkHandler` / `sleeveBloomHandler` / `beamSwapHandler` / `tileMovement` | 20 / 32 / 23 / 6 | 具体 `caseId`；另有 `ledger` 9 个键、`tileMarkState` 3 个键 |
| shuffle | `cases` / `slotMatrix` / `demandVectors` / `overlapMatrix` / `configSnapshot` | 32 / 16 / 6 / 6 / 9 | seeds 0..31；slot 用 `dealerSeat/seat` 复合键 |

具体 case 总数 = 6 + 10,238 + 25 + 81 + 32 = **10,382**。

---

## 3. G-xxx 覆盖与分母

### 3.1 覆盖分级

| 覆盖 | 数量 | 含义 |
|---|---:|---|
| `full` | 17 | 该 G 条目的全部约定断言在当前五组 fixture + C# reader 范围内都有证据；仍不是 G1 验收 |
| `partial` | 20 | 只覆盖该 G 条目的部分断言；单列具体缺口，不冒充整项完成 |
| `none` | 33 | 五组 fixture 中没有该行为；保留原责任任务 |
| `v5-only` | 3 | G-055/G-058/G-059，V4 无对应行为，不计入 V4 对齐 |

### 3.2 17 条 full

`G-001a`、`G-001b`、`G-001c`、`G-002`、`G-003a`、`G-016`、`G-017`、`G-018`、`G-020`、`G-021`、`G-022`、`G-023`、`G-025`、`G-026`、`G-027`、`G-029`、`G-042`。

### 3.3 20 条 partial（必须单列）

| ID | 部分覆盖 | 缺口 |
|---|---|---|
| `G-003b` | 真实删除/池账本失配探针 + C# 缺项/单账本 invariant | V4 `MutableMap` duplicate-key 分支 NOT_CONSTRUCTIBLE；C# 无 mutable 损坏等价 |
| `G-008` | 两个手牌/牌山等额置换 case | 无普通摸打/鸣牌/杠 movement case |
| `G-013` | 48 个 win 样本覆盖 evaluator | 精确 4 番/30 符/7700 的 G-013 手牌未单列 |
| `G-014` | 9 个符样本覆盖切上 | 精确 28→30 的 G-014 case 不在 fixture |
| `G-015` | `D-002` indicator=2/red=1/total=3 | 单红五+指示牌 1/1/2 的 case 不在 fixture |
| `G-019` | `winSamples` 出现全部 42 个 code，C# 断言 42 | 41/42 的 D-01 裁决仍未解决；数量口径不等于裁决 |
| `G-028a` | `RoundTerminalReplaysExactly` 断言 version/phase/result/scores/rejectedActions 与 `TsumoWin` 存在 | 未断言 `MatchEnded` 不存在，也未断言完整 `eventTypes`（`ScoreSettled`/`RoundEnded`） |
| `G-041` | `Ledger_Section_ReplaysAgainstCheatLedger` 断言 replay/conflict/overdraw/overflow/invalid-seat/summary/snapshot | 未断言 round version、麻将 scores、136 张守恒；原 G 行的这些断言仍无证据 |
| `G-031` | 账本拒绝证明余额/entry 零副作用 | 未断言 no-event command 的事件列表/version |
| `G-032` | `tm-allowed` + ledger charge | 无单 case 的完整 post-state/group/locations/deadline |
| `G-033` | 容量 + 过期 case | C# 只重放过期/跨座；容量 case 只到 reason 级 |
| `G-034` | 19 个拒绝 case + 稳定 reason | C# 只重放代表子集，未逐 case |
| `G-035` | 袖里藏花激活/选择 + goldenInputs | C# 绑定了子集，不是完整 snapshot |
| `G-036` | 他家回合/预留/过期 case | C# 绑定预留交付，不是每个 timing 分支 |
| `G-037` | stale/wrong-window/no-offer reason | C# 重放子集 |
| `G-038` | 偷梁换柱激活/swap/keep | C# 绑定候选实体与 keep，不是完整 snapshot |
| `G-039` | invalid/stale/foreign case | C# 重放子集 |
| `G-043` | 32 个固定 seed 墙、赤五、王牌隔离、3/4 施术者 | 非法 seat/config 拒绝未导出 |
| `G-046` | `RG-004a..f` 的 9 次 open 成功/常规 complete/cancel/pause/resume/expiry/remainingReserve | 无重复 open 不刷新、无旧 roundId key 的 open 拒绝；C# `DecisionClockTracesReplayExactly` 无这两个失败/异常分支 |
| `G-048` | `RG-005` 仅 engine `ControllerRegistry` epoch | server `ConnectionControl` grace/reclaim 未导出；fixture 自述“C# tests cover it without a V4 golden” |

### 3.4 33 条 none（仍保留责任任务）

`G-004`、`G-005`、`G-006`、`G-007`、`G-009`、`G-010`、`G-011`、`G-012`、`G-024`、`G-028b`、`G-030`、`G-040a`、`G-040b`、`G-040c`、`G-044`、`G-045`、`G-047`、`G-049`、`G-050a`、`G-050b`、`G-050c`、`G-051`、`G-052`、`G-053`、`G-054`、`G-056a`、`G-056b`、`G-056c`、`G-056d`、`G-057a`、`G-057b`、`G-057c`、`G-057d`。

### 3.5 3 条 v5-only

`G-055`（持久 outbox/RECEIPT_QUERY）、`G-058`（ownerEpoch/lease/Postgres 合同）、`G-059`（Protobuf/WSS/TLS/Unity/IL2CPP）。

### 3.6 分母纪律

| 口径 | 数值 | 不能怎么用 |
|---|---:|---|
| G 目录需求 | 73 | 不能把 fixture case 数当成需求完成数 |
| fixture 具体 JSON case | 10,382 | 不能与 73 相加 |
| 其中 scoring `randomSamples` | 10,000 | 是 10,382 的子集，不第二次相加 |
| C# Golden `[Fact]` | 42 | 是测试方法数，不是 JSON case 数或需求数 |
| fixture 文件 | 5 | 不是 73 条需求的完成数 |
| V4 exporter / C# reader 类 | 5 / 5 | 只说明工具链存在，不说明覆盖完整 |

---

### 3.7 17 条 full 的 C# 证据

| ID | C# method / 字段证据 |
|---|---|
| `G-001a` | PhysicalGoldenTests.G001aStandardSetMatchesDomainTileIdentityAndRedFiveRules: TileId fields, 136 tiles, redFiveIds 19/55/91 |
| `G-001b` | PhysicalGoldenTests.G001bTypeIndexBoundariesAnd34GroupsMatchDomain: boundaryTypeIndexes, 34 groups, totalTiles=136 |
| `G-001c` | PhysicalGoldenTests.G001cTileIdRoundTripAndIllegalInputsMatch: valid 0..135, boundaries 0/135, illegal inputs |
| `G-002` | PhysicalGoldenTests.G002ZoneOwnerSemanticsMapToImmutablePositions: legal/illegal zone-owner probes; no-seat owner structural equivalence |
| `G-003a` | PhysicalGoldenTests.G003aExportedWallsDriveDomainMapAndConservationWithoutRngClaim: 32 walls, red fives, 136 positions, dead-wall segment |
| `G-016` | ScoringGoldenTests.CuratedWinAndNoWinSamplesMatchV4 -> CompareNoWinSample (N-001): Evaluate returns null |
| `G-017` | ScoringGoldenTests.ScoreTableSamplesMatchV4 -> CompareScoreSample: base/raw/cap/payments/payment plans |
| `G-018` | ScoringGoldenTests.ScoreTableSamplesMatchV4 -> S-YAKUMAN-1/2/3/4: base points, multipliers, payments |
| `G-020` | ScoringGoldenTests.SettlementSamplesMatchV4 -> ST-001/ST-002: deltas, newScores, remainingRiichiSticks |
| `G-021` | ScoringGoldenTests.SettlementSamplesMatchV4 -> ST-003/ST-004/ST-007: tsumo deltas/newScores/sticks |
| `G-022` | ScoringGoldenTests.SettlementSamplesMatchV4 -> ST-100..ST-115: all 16 tenpai masks deltas/newScores/sticks |
| `G-023` | ScoringGoldenTests.SettlementSamplesMatchV4 -> ST-008: abortive draw deltas/newScores/sticks |
| `G-025` | RoundReplayGoldenTests.MatchProgressionMatrixReplaysExactly -> RG-003a..e: next/dealer/wind/round/honba/scores |
| `G-026` | RoundReplayGoldenTests.MatchProgressionMatrixReplaysExactly -> RG-003f..j,p: next/final/rankings/reason/transferSeat |
| `G-027` | RoundReplayGoldenTests.MatchProgressionMatrixReplaysExactly -> RG-003m..o: agari-yame/final/rankings/transferSeat |
| `G-029` | RoundReplayGoldenTests.ChankanRonReplaysExactly: phase/stateHash/rinshan/kan/events/result/scores/version |
| `G-042` | ShuffleGoldenTests.SlotMatrixMatchesCSharpShuffleSlots: 16 dealer/seat initialSlots/drawSlots |

`G-028a`、`G-041`、`G-046` 已从 full 降为 partial，原因见 §3.3；没有通过修改 C# 或 fixture 来凑 full。

### 3.8 reader → 必读字段/分支 → 校验位置对照

| C# reader | 必读字段/分支 | catalog 校验位置 |
|---|---|---|
| `PhysicalGoldenTests.G001a…` | `tiles[].tileId/typeIndex/copyIndex/suit/rank/is*`、`redFiveTileIds`、`nonRedFiveCount`、`standardSetOrderIsTileIdOrder` | `physical-v1:samples.conditionalArrayItemRules[G-001a]` + `conditionalRules[G-001a]` |
| `PhysicalGoldenTests.G001b…` | `boundarySamples[].typeIndex/copyIndex/tileId/suit/rank`、`boundaryTypeIndexes`、`groups[].tileIds`、`allGroupsFour`、`tileIdFormula` | `conditionalArrayItemRules[G-001b]` + `conditionalRules[G-001b]` |
| `PhysicalGoldenTests.G001c…` | `validRoundTrips[].tileId/typeIndex/copyIndex/suit/rank`、`invalid[].input/exceptionType/semanticCategory`、`allRoundTripEqual` | `conditionalArrayItemRules[G-001c]` + `conditionalRules[G-001c]` |
| `PhysicalGoldenTests.G002…` | `cases[].caseId/zone/player/outcome/constructed/csharpMapping`；accepted 分支 `zoneOut/playerOut`；rejected 分支 `exceptionType/message/semanticCategory` | `conditionalArrayItemRules[G-002]` + item `allowedValues`/`conditionalRules` |
| `PhysicalGoldenTests.G003a…` | `cases[].wall/baseWall/positions[].tileId/zone/seat/slot/redFiveTileIds`、4 个汇总 bool、`input.randomInputs` | `conditionalArrayItemRules[G-003a]` + `conditionalRules[G-003a]` |
| `PhysicalGoldenTests.G003b…` | `probeA.firstAudit.outcome/exceptionType/semanticCategory/message`、`probeA.mutation.mapSizeAfter`、`probeA.after.poolSize/mapSize/location(null)/missingTileIds[]`、`probeA.autoRepairObserved`、`probeA.secondAudit.outcome/message`；`probeB.before.auditOutcome/actualTilesSha256`、`probeB.firstAudit.outcome/exceptionType/semanticCategory/message`、`probeB.after.poolZone/poolPlayer/closedTilesStillContains/poolSize/actualTilesCount/actualTilesSha256`、`probeB.autoRepairObserved`、`probeB.secondAudit.outcome/message`、`probeB.csharpMapping.status/note`；`duplicateKeyStaticLimitation.status/observedMapSize/auditBranchReachability/reason/firstValue.zone/finalValue.zone` | `conditionalRules[G-003b]` + `conditionalArrayItemRules[G-003b].missingTileIds` |
| `ScoringGoldenTests` win/noWin/fu/dora/settlement | `input.*`、`expected.dora.*`、`expected.yaku[].code/han`、`expected.fu.lines[]`、`expected.paymentPlan.components[]`、`expected.paymentPlans.*.components[]` | `scoring-v1:winSamples/noWinSamples/fuSamples/doraSamples/settlementSamples` 的 `rules`/`arrayItemRules` |
| `ScoringGoldenTests` score/random | `score.input.fu/han/yakumanCount`、`paymentPlans` 组件；`random.context.*`、`expected.status`、`melds[].*`、`expected.yaku[].code/han` | `scoreSamples` rules/arrayItemRules；`randomSamples` fast path + `allowedValues` |
| `RoundReplayGoldenTests` | roundTerminal/chankan 的 input/settlement/expected；matchProgression 分支；decisionClock/controlEpoch `operations[].op` | `round-v1:*` shapes + `elementRules`/`conditionalRules` |
| `CheatGoldenTests`/`ShuffleGoldenTests` | ledger/tileMarkState/handler/movement；slotMatrix/cases/overlap/demand | `cheat-v1:*`/`shuffle-v1:*` shapes |

## 4. C# 消费位置与定向证据

| fixture | C# reader | Facts | 实际断言范围 |
|---|---|---:|---|
| physical | `dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs` | 9 | provenance、6 个样本、136 身份/赤五、typeIndex 边界、TileLocation 语义、32 墙位置/hash、G-003b 结构 invariant |
| scoring | `ScoringGoldenTests.cs` | 7 | provenance/42 code、48+4 curated、147 score table、9 fu、6 dora、24 settlement、10,000 random 分布 |
| round | `RoundReplayGoldenTests.cs` | 6 | provenance、RG-001 terminal、RG-002 chankan、16 match matrix、6 clock、RG-005 epoch |
| cheat | `CheatGoldenTests.cs` | 16 | ledger、tileMarkState、75 handler cases 的 reason/代表 case、6 movement 物理事实 |
| shuffle | `ShuffleGoldenTests.cs` | 4 | provenance、16 slot matrix、6 demand/overlap、32 墙/赤五/王牌/3-4 施术者 |

定向命令与实际结果：

```powershell
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal
# 实测：322 passed / 0 failed / 0 skipped，exit 0（全量 Domain，不是 42 项定向）

D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class "*GoldenTests" -noColor -noLogo
# 实测：Total: 42, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0，exit 0
```

关键实测发现：该测试工程是 xUnit v3 Microsoft.Testing.Platform。`dotnet test --filter "FullyQualifiedName~Golden"` **被忽略**，实际运行 322 个 Domain 测试；因此 42 项定向证据使用 direct runner 的 `-class "*GoldenTests"`。不能把 322 全量数写成 42 项定向数，也不能把 42 项写成 73 条需求完成。

---

## 5. 只读 validator

### 5.1 文件

`tools/verify-v5-golden-catalog.ps1`（PowerShell 5.1，只读）。

### 5.2 实际校验项

1. catalog 存在、JSON 可解析、`schema=v5-golden-catalog-v1`、`catalogVersion=1`。
2. baseline `freezeCommit` 是 40-hex。
3. 恰好 5 个 fixture；路径必须在 `tests/v5-golden/`；文件存在。
4. 每个 fixture 的实际 SHA-256 与 bytes 必须等于 catalog 声明。
5. fixture 实际 `schema` 必须等于 catalog 声明，且必须是五个已接受 schema 之一；未知 schema 失败。
6. `rulesetVersion`、`generatorVersion`、`extractionStatus`、`generatedByCommand` 必须与 fixture 内的真实字段相等；`extractionStatus` 必须是 `exported`。
7. `sourceV4Commit` 必须实际等于 fixture 的 `v4Commit`，不能只检查 40-hex；`round-v1` 的 `sourceV4EngineCommit`/`sourceV4DotnetCommit` 必须分别等于 fixture 的 `v4EngineSourceCommit`/`v4DotnetRulesCommit`。
8. exporter 文件存在；原始 SHA-256 与 LF 规范化 SHA-256 都必须等于 catalog。`physical-v1` 还必须从 fixture 读取 `exporterSourceSha256`，与真实 exporter 的 LF 哈希相等，并与 catalog 的 `declaredSha256Lf` 一致。
9. C# reader 文件存在；`[Fact]` 实际数量必须等于 catalog；C# 文件必须引用对应 fixture 文件名。
10. 每个分组：JSON path 存在、count 等于实际；数组 ID 无重复、与声明顺序一致、pattern/first/last 一致；复合键/对象键一致。`requiredKeys` 存在时必须是数组，null/标量失败。
11. `groupShapes` 为五个 schema 的实际消费字段建立最低结构/类型/length 校验：数组逐 case 检查 requiredKeys/rules；scoring 的 win/fu/dora/settlement/score/random 检查 `input`/`expected`/`context` 的消费字段（`score.input.fu/han/yakumanCount`、`expected.payments`/`paymentPlans`、`random.context.winTile/isTsumo/...`、`expected.status` 分支等）；sample 检查 `roundTerminal`/`chankanRon`/`controlEpoch` 的 `input`/`settlement`/`expected`/`operations` 及关键嵌套字段（`input.roundId`、`input.scores`、`settlement.finalScores`、`expected.eventTypes` 等）；`decisionClock`/`controlEpoch` 的 `operations` 用 `elementRules` 按 `op` 分支校验每个操作元素（`beginRound/open/complete/cancel/pause/resume/expire/expired/timingDeadline/remainingReserve/initial/setController/initialize/validateEpoch`）；`ledger` 检查 `chargeOnce`/`replaySamePayload`/`conflictDifferentPayload`/`flowSummary` 等结果字段；ID-only 单例、空对象、空/null 操作、关键字段 null 或错误类型都必须失败。
12. 每个 fixture 的 `caseTotal` 等于其 `countAsCase` 分组计数之和。
13. 73 条 requirement ID 唯一；coverage/manifestStatus 合法；owner 非空；full/partial 至少一个锚点，none/v5-only 不得有锚点；`full` 必须有非空 `csharpEvidence`（具体 method/字段），否则 `FULL_EVIDENCE_MISSING`。
14. 每个锚点 `fixture:group/id` 必须解析到实际 fixture/group/case；对象组的锚点解析到实际键。
15. coverage 计数必须等于 denominator；fixture 数、case 数、random 子集数、C# Facts 数、类数都必须等于 denominator。
16. 从 `V5-0-GOLDEN-MANIFEST.md` 提取 73 个 G 行（不读 §8.5 当前状态表），与 catalog requirement 集合完全一致；每条的历史 `manifestStatus` 必须等于对应行的反引号状态 token。

### 5.3 结果语义

- 正常：`PASS ...`，exit 0。
- 坏 catalog/SHA/重复 ID/未知 schema/漏文件/锚点缺失：`FAIL <CODE>: ...`，exit 1。
- 缺 `Get-FileHash` 或 `ConvertFrom-Json`：`BLOCKED missing-tool ...`，exit 2。
- validator 不执行生产代码、不写 tracked fixture、不修改任何文件。

### 5.4 正例

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1
```

实际输出：

```text
PASS catalog=D:\majiang-wt\golden-catalog\tests\v5-golden\catalog-v1.json sha256=DA069B64B11B403C6B49AB61ACB45D2AF230348EC397AC3EC21841706B6A0F80 fixtures=5 requirements=73 cases=10382 random=10000 facts=42 coverage=full:17/partial:20/none:33/v5-only:3
```

exit 0。

---

## 6. TEMP 自测（坏输入）

`tools/tests/VerifyV5GoldenCatalog.Tests.ps1` 把 catalog、5 个 fixture、5 个 exporter、5 个 C# reader、manifest 复制到 `%TEMP%\v5-golden-selftest-<guid>`，对每个 case 重新复制一份并注入一个缺陷，调用只读 validator，断言 exit 1 且输出含预期错误码。真实仓库 fixture 不写；运行前后比较 5 个 SHA-256。

### 6.1 自测 case

| # | case | 注入 | 期望 |
|---:|---|---|---|
| 1 | missing-fixture | 删除 `physical-v1.json` | `FIXTURE_MISSING` |
| 2 | sha-change | 改 `cheat-v1.json` rulesetVersion，不更新 catalog | `FIXTURE_SHA_MISMATCH` |
| 3 | source-commit | catalog physical `sourceV4Commit` 改 000…，fixture 不动 | `SOURCE_COMMIT_MISMATCH` |
| 4 | ruleset-version | catalog scoring `rulesetVersion` 改 2 | `RULESET_VERSION_MISMATCH` |
| 5 | extraction-status | catalog cheat `extractionStatus` 改 pending | `EXTRACTION_STATUS_MISMATCH` |
| 6 | command | catalog shuffle `generatedByCommand` 改 bogus | `COMMAND_MISMATCH` |
| 7 | round-engine-commit | catalog round `sourceV4EngineCommit` 改 000… | `SOURCE_ENGINE_COMMIT_MISMATCH` |
| 8 | exporter-source-field | fixture physical `exporterSourceSha256` 改 000…，刷新目录 SHA | `EXPORTER_SOURCE_SHA_MISMATCH` |
| 9 | unknown-schema | fixture physical schema 改 `v5-evil-golden-v1`，刷新 SHA | `FIXTURE_SCHEMA_UNKNOWN` |
| 10 | duplicate-case-id | 复制 `samples[0]`，刷新 SHA/count | `DUPLICATE_CASE_ID` |
| 11 | bad-anchor | `G-001a` 锚点改 `G-999` | `ANCHOR_MISSING` |
| 12 | bad-group-count | `winSamples.count` 48→47 | `GROUP_COUNT_MISMATCH` |
| 13 | missing-requirement | 删除最后一条 requirement，保留 denominator 73 | `REQUIREMENT_COUNT_MISMATCH` |
| 14 | duplicate-requirement | 复制 `G-001a`，denominator 74 | `DUPLICATE_REQUIREMENT_ID` |
| 15 | unknown-coverage | `G-001a` coverage 改 `done` | `COVERAGE_UNKNOWN` |
| 16 | fact-count | physical `factCount` 9→8 | `CSHARP_FACT_COUNT_MISMATCH` |
| 17 | case-structure | 删除 `physical samples[0].expected`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 18 | singleton-id-only | `roundTerminal` 只留 `sampleId`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 19 | requiredkeys-null | physical `samples.requiredKeys` 改 null | `GROUP_STRUCTURE_INVALID` |
| 20 | requiredkeys-scalar | physical `samples.requiredKeys` 改字符串 | `GROUP_STRUCTURE_INVALID` |
| 21 | nested-object-key | 删除 `ledger.chargeOnce`，刷新 SHA | `GROUP_KEYS_MISMATCH` |
| 22 | singleton-wrong-type | `roundTerminal.expected` 改字符串，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 23 | terminal-empty-input | `roundTerminal.input={}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 24 | clock-operation-empty | `controlEpoch.operations[0]={}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 25 | ledger-charge-empty | `ledger.chargeOnce={}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 26 | terminal-array-null | `roundTerminal.input.scores=null`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 27 | clock-operations-null | `controlEpoch.operations=null`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 28 | clock-operation-null | `controlEpoch.operations=[null]`，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 29 | score-middle-null-input | `scoreSamples[70].input=null`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 30 | random-middle-null-context | `randomSamples[5000].context=null`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 31 | random-middle-wrong-context | `randomSamples[5000].context` 改字符串，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 32 | empty-consumed-objects | score input/expected 与 random context 改 `{}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 33 | missing-consumed-fields | 删除 score `input.fu` 与 random `context.winTile`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 34 | wrong-consumed-types | score `input.fu` 与 random `context.isTsumo` 改错类型，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 35 | win-dora-empty | `winSamples[24].expected.dora={}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 36 | score-payment-component-empty | `paymentPlans.dealerTsumo.components[0]={}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 37 | physical-tile-empty | `physical samples[0].expected.tiles[0]={}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 38 | physical-tile-wrong-type | physical tile `tileId` 改字符串，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 39 | random-closed-object | `randomSamples[5000].closed[0]={}`，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 40 | random-meld-empty | `randomSamples[5000].melds=[{}]`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 41 | random-yaku-empty | win random 的 `expected.yaku[0]={}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 42 | required-nullable-key-missing | 删除 score `expected.capCode` 键，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 43 | random-dora-object | `randomSamples[5000].dora=[{}]`，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 44 | fixture-schema-case | fixture physical schema 改大写，刷新 SHA | `FIXTURE_SCHEMA_UNKNOWN` |
| 45 | catalog-schema-case | catalog schema 改大写 | `CATALOG_SCHEMA` |
| 46 | extraction-status-case | catalog cheat `extractionStatus` 改 `Exported` | `EXTRACTION_STATUS_MISMATCH` |
| 47 | fixture-extraction-case | fixture cheat `extractionStatus` 改 `Exported`，刷新 SHA | `EXTRACTION_STATUS_MISMATCH` |
| 48 | exporter-sha | 向 physical exporter 追加注释 | `EXPORTER_SHA_MISMATCH` |
| 49 | group-id-mismatch | `samples.ids[0]` 改 `G-999a` | `GROUP_IDS_MISMATCH` |
| 50 | manifest-row-removed | 删除 manifest 的 `G-001a` 历史行 | `MANIFEST_ROW_COUNT` |
| 51 | catalog-schema | catalog schema 改 v2 | `CATALOG_SCHEMA` |
| 52 | random-status-unknown | `randomSamples[5000].expected={status:'invalid-status'}`，刷新 SHA | `CASE_STRUCTURE_UNKNOWN_VARIANT` |
| 53 | random-status-case | `randomSamples[5000].expected.status='WIN'`，刷新 SHA | `CASE_STRUCTURE_UNKNOWN_VARIANT` |
| 54 | branch-discriminators-unknown | match `expected.type=NextRoundX`、settlement `RON`、op `INITIAL`，刷新 SHA | `CASE_STRUCTURE_UNKNOWN_VARIANT` |
| 55 | physical-boundary-copyindex-missing | 删除 G-001b `boundarySamples[0].copyIndex`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 56 | physical-boundary-copyindex-null | G-001b `copyIndex=null`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 57 | physical-boundary-copyindex-type | G-001b `copyIndex` 改字符串，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 58 | physical-zone-case-empty | G-002 `cases[0]={}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 59 | physical-redfive-object | G-003a `redFiveTileIds[0]={}`，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 60 | physical-position-empty | G-003a `positions[0]={}`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 61 | physical-wall-object | G-003a `wall[0]={}`，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 62 | physical-basewall-object | G-003a `baseWall[0]={}`，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 63 | physical-zoneout-missing | G-002 accepted 删除 `zoneOut`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 64 | physical-rejected-field-missing | G-002 rejected 删除 `exceptionType/semanticCategory`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 65 | physical-invalid-input-missing | G-001c `invalid[0].input` 删除，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 66 | g003b-duplicatekey-missing | 删除 G-003b `duplicateKeyStaticLimitation`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 67 | g003b-probeb-poolzone-missing | 删除 `probeB.after.poolZone`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 68 | g003b-location-missing | 删除 `probeA.after.location`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 69 | g003b-missingtileids-object | `probeA.after.missingTileIds[0]={}`，刷新 SHA | `CASE_STRUCTURE_TYPE` |
| 70 | g003b-csharpmapping-missing | 删除 `probeB.csharpMapping.status`，刷新 SHA | `CASE_STRUCTURE_MISSING` |
| 71 | sample-id-wrong | catalog roundTerminal `ids=[RG-999]`，fixture 不动 | `GROUP_IDS_MISMATCH` |
| 72 | sample-id-missing | 删除 roundTerminal `ids` 键 | `GROUP_IDS_MISMATCH` |
| 73 | sample-id-empty | roundTerminal `ids=[]` | `GROUP_IDS_MISMATCH` |
| 74 | sample-id-multi | roundTerminal `ids=[RG-001,RG-002]` | `GROUP_IDS_MISMATCH` |
| 75 | sample-id-scalar | roundTerminal `ids=RG-001` 字符串 | `GROUP_STRUCTURE_INVALID` |
| 76 | sample-id-actual-change | fixture roundTerminal.sampleId 改 RG-999，刷新 SHA | `GROUP_IDS_MISMATCH` |

### 6.2 实际结果

```text
SELFTEST PASS cases=77 positives=1 negatives=76
real-fixtures-unchanged=true
```

77/77 个 case 通过：1 个正例 exit 0，76 个坏输入负例均 exit 1 并命中预期错误码；真实 5 个 fixture 的 SHA-256 在自测前后逐项相等。

日志：`artifacts/golden-catalog/validator-selftest.log`（gitignored 工件，不随提交；路径/SHA 见 §9）。

---

## 7. Manifest 更新口径

`V5-0-GOLDEN-MANIFEST.md` 的更新不是抹掉历史：

1. 头部增加“当前事实（2026-10-24）”与五组 fixture 的真实表；原 2026-10-23 五条盘点改为“0.1 原始盘点（历史，保留）”。
2. §0.1 状态表的 `exported` 行改为“2026-10-23 为 0，当前见 §0.2/§8.3.2”。
3. §8.3 拆成“8.3.1 历史口径（保留）”与“8.3.2 当前机械口径”。
4. §8.4 更新为当前可运行命令，并记录 `dotnet test --filter` 被 MTP 忽略的实测事实。
5. 新增 §8.5，逐条列出 73 个 G 的当前 coverage、锚点、责任任务和说明；§1–§7 表中的历史提取状态保持原样，不改成当前值。
6. §9 边界声明更新为当前事实，明确未 push、未改 fixture/生产码、G1/G5 未过。

---

## 8. 命令、退出码与原始日志

| # | 命令 | 结果 | exit |
|---:|---|---|---:|
| 1 | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1` | PASS 5/73/10382/10000/42 | 0 |
| 2 | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5GoldenCatalog.Tests.ps1` | 1 正例 + 76 负例全 PASS；真实 fixture 不变 | 0 |
| 3 | `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal` | 322/0/0（全量 Domain；filter 被 MTP 忽略） | 0 |
| 4 | `D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class "*GoldenTests" -noColor -noLogo` | 42/0/0/0 | 0 |
| 5 | `git status --porcelain` / `git diff --check` | 仅授权文件；无空白错误 | 0 |

原始日志（均在 `artifacts/golden-catalog/`，gitignored）：

- `validator-positive.log`
- `validator-selftest.log`
- `csharp-golden-tests.log`（322/0/0）
- `csharp-golden-targeted.log`（42/0/0）

### 8.1 未运行

- 未重新导出 physical/scoring/round/cheat/shuffle；未运行原 V4 exporter。
- 未运行全量 Gradle；未运行 PG/WSS/CI/远端。
- 未运行 Unity/IL2CPP/Android/iOS 真机/跨物理网络。
- 未运行 G1 全套、G5、V5 发布。
- 未处理 G-049 AI 在途 exporter；其 `ai-v1.json` 未被接受，本目录固定到五组。

---

## 9. 文件与证据 SHA-256

| 文件 | bytes | SHA-256 | 说明 |
|---|---:|---|---|
| `tests/v5-golden/catalog-v1.json` | 484414 | `16F7FD7F980398388E4E5DA45A3D5FB755C19990915F403987094617E90E891B` | 本任务新增；catalog |
| `tools/verify-v5-golden-catalog.ps1` | 59126 | `9635064A70C82F1463EB8AB18F8D6F0A23797D204CCAE5699AE29CE7B337CFA0` | 本任务新增；只读 validator |
| `tools/tests/VerifyV5GoldenCatalog.Tests.ps1` | 46057 | `46163D4D42AD4C647F632D423DDD2B43A025D1D2FA842389072F919CDEE51122` | 本任务新增；TEMP 自测 |
| `doc/v5-重构/V5-0-GOLDEN-MANIFEST.md` | 70168 | `DA3ADB0B71D2EF7069F0103BCF592D299FE145DE8526E248EBE5CAF30BBBDA11` | 本任务更新；保留历史并增补当前事实 |
| `tests/v5-golden/physical-v1.json` | 836369 | `B9384534E826473007511FD34096085C56EFD0E0F49E877978E50F847EDA0995` | 只读 fixture；未改 |
| `tests/v5-golden/scoring-v1.json` | 8315131 | `53B1811079033E9044E222F7BB1FA263506FD724F09D6B13F84FE9CC2FCCD94E` | 只读 fixture；未改 |
| `tests/v5-golden/round-v1.json` | 43474 | `DF5FC5F8199C039823BAC1C668F911637EEB80E7F8A785C84D011401CF2452B7` | 只读 fixture；未改 |
| `tests/v5-golden/cheat-v1.json` | 42120 | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` | 只读 fixture；未改 |
| `tests/v5-golden/shuffle-v1.json` | 99467 | `386061B42691CCF36381E2D87DC4787367958C8E9E96A93BA69D1E3FE5DAC282` | 只读 fixture；未改 |
| `engine/src/test/kotlin/com/majiang/model/V5PhysicalGoldenExporterTest.kt` | 45239 | `B86286AE5CAE67BCB1E395C48CA755E884439683367D76AABC6711D6A7775EA4` | 只读 exporter；LF 规范化 SHA=d1d72e393a3ee455016b15a17252d2ae6b74acd9478c8822dfd3b20acaf968ac |
| `engine/src/test/kotlin/com/majiang/rules/V5ScoringGoldenExporterTest.kt` | 62671 | `284E65279640AFD5AE9B06F7FF4ACAA5E2F2A2F0CC732B802110D594027913A6` | 只读 exporter |
| `engine/src/test/kotlin/com/majiang/rules/V5RoundGoldenExportTest.kt` | 42276 | `D3CBD13FE7DE38155CD493192EBD1CCA052F643936426AEB519FB3C9AEA3A28B` | 只读 exporter |
| `engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt` | 81686 | `402AA8EBF03B8E3F2AEB5712A94E7FC1C8508EE327981BC014F7A0A2A67902E4` | 只读 exporter |
| `engine/src/test/kotlin/com/majiang/v3/V5ShuffleGoldenExporterTest.kt` | 16122 | `EF6CC325110F640C372576B07FA4D2C0B5F7B84AA2D69F9E84307A81240A1B3E` | 只读 exporter |
| `dotnet/tests/Mahjong.Domain.Tests/PhysicalGoldenTests.cs` | 34932 | `DF8F48F87B9EC958B108534BBD9E1306E517F729411CAA44310D76D68E836CAD` | 只读 C# reader；9 Facts |
| `dotnet/tests/Mahjong.Domain.Tests/ScoringGoldenTests.cs` | 31834 | `0244635A87904021BCE7E5BC724F0F524CCE180033CED3C5B39DAD1798EFCBFA` | 只读 C# reader；7 Facts |
| `dotnet/tests/Mahjong.Domain.Tests/RoundReplayGoldenTests.cs` | 25622 | `B7BEB497372B030EA66366DFDC8E884306B8FE6C490C26219048538307B895AE` | 只读 C# reader；6 Facts |
| `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | 69774 | `6B667231509A37685AE06B4F8C45DA3D5A01D9A740BFEF3295209D3ED8E2446F` | 只读 C# reader；16 Facts |
| `dotnet/tests/Mahjong.Domain.Tests/ShuffleGoldenTests.cs` | 11797 | `B1B3940A0338835586814B78CB650095203B4EFA358D79BA4BACF82F5F68905B` | 只读 C# reader；4 Facts |
| `artifacts/golden-catalog/validator-positive.log` | 510 | `3ECE982BAECBBDD36B7931FCC43F41E2EE20A5A163569B27EA2C4F71461F731E` | validator 正例原始日志（gitignored） |
| `artifacts/golden-catalog/validator-selftest.log` | 30535 | `380CEA7E1C119B500020F03DE546D9068CA157B6C3ABEF25DC9DAB0880706155` | 自测原始日志（gitignored） |
| `artifacts/golden-catalog/csharp-golden-tests.log` | 1474 | `51A609CD7559CBCC787484BD49A28237284EC56FEA021FE6E496046626C2048D` | 322/0/0 全量 Domain 日志（gitignored） |
| `artifacts/golden-catalog/csharp-golden-targeted.log` | 702 | `BC22B4EB32CABF8E4D530B991AD986ED70E7ABE53DC2D12675235BDCF30EB400` | 42/0/0 定向日志（gitignored） |
| `doc/v5-重构/V5-GOLDEN-CATALOG-RECONCILIATION.md` | - | 提交后计算 | 本报告；SHA 在提交后由最终交付消息给出（自引用不写入表） |

---

## 10. 边界与后续

1. 本任务只完成“现有五组已接受 fixture 的清单核对与机器验证”，不重新导出全部 V4，不重做已完成 physical 任务，不扩到 P3.x/P4/P5。
2. `full` 17 条只是当前 catalog 覆盖候选；`partial` 20 条和 `none` 33 条仍需各自责任任务补导出/补断言。G1 仍 `BLOCKED`。
3. 五组 fixture 中不含 G-049 AI、G-040 私有投影、G-050+ 协议/投影、G-044 看破、G-045 SecureRandom 等；本任务没有用五组文件冒充全量。
4. 本任务未修改统一 verifier、未放宽校验、未用 skip/空测试获得绿色。
5. 交付为本地 commit，供 caller 独立复核；caller 负责唯一 PR19 `feat/v5-1-integration -> v5` 与 merge-back。未 push、未建 PR、未触发远端 CI。
