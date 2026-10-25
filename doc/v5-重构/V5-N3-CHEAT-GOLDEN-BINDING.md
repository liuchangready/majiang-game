# V5 N3 cheat-v1 字段行为绑定（G1 剩余消费者测试任务）

日期：2026-10-25。工作树：`D:/majiang-wt/n3-cheat-binding`，分支 `wt/v5-n3-cheat-binding`，冻结基线 `900a043cfa276c0e172672b8b7cc7d6d4ebc4767`。本地交付提交见本工作树 `git log -1`。

本报告只覆盖 **cheat-v1 测试消费者字段绑定**。它不是 N3/G1 通过：G1 仍未批准，V4 仍是唯一生产架构，G5 未完成，`complete.flag` 不存在。本轮没有修改生产 C#、V4 exporter/fixture、catalog/manifest、统一 verifier、项目/solution 或用户环境。

## 0. 范围与结论

| 对象 | 结果 | 说明 |
|---|---|---|
| 75 个 handler case | **68 PASS / 1 FAIL / 6 BLOCKED** | 68 = Bound/Refined，逐例调用真实 Domain 入口；1 = `sb-activate-wall-empty-drift` 真实行为分歧；6 = 4 个 actor 层 + 2 个不可规范表示的 dead-wall 过滤态 |
| 6 个 tileMovement case | **6 PASS** | 5 个新增精确重建 + 既有 M5 全 136 placements 对照 |
| ledger 深字段/快照 | **PASS** | 每条 entry 的 id/seat/amount/balanceAfter/reason、顺序、余额与 flow 汇总逐项绑定 |
| TileMarkState 深字段 | **PASS** | group 全字段、版本轨迹、到期、跨座位、拒绝零副作用 |
| 原 16 个 Fact | **16 PASS** | 保持有效断言；纠正了 drawn-backfill「V4-only」、disabled 换输入、foreign-hand 换输入三处旧断言 |
| 外部单字段错值矩阵 | **22/22 FAIL_AS_EXPECTED** | 原 fixture 正控制 96/0/0/0/0，exit0；每个变异 exit1 且真实 Failed>0 |
| Domain 全量 | **465 passed / 0 failed / 0 skipped / 0 Not Run** | native exit 0 |
| Release solution 构建 | **0 warning / 0 error** | native exit 0 |
| solution 测试 | **1181 passed / 0 failed / 0 skipped** | 435 AI + 149 Contracts + 132 Client + 465 Domain，native exit 0 |
| PG/WSS/Unity/IL2CPP/Gradle/远端 CI | **NOT_RUN** | 本任务不需要为测试绑定变更重跑平台重型全量 |

“68 PASS”是**行为绑定**计数，不是 N3 总完成度；6 个 BLOCKED 与 1 个 FAIL 仍计入 75 的分母，未删除、未改名、未转成 skip。

## 1. 输入重建与 JSONpath 口径

### 1.1 源锚点

- V4 只读导出器：`engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt`
  - ledger：:111-187；tileMarkState：:193-254；tileMark handler：:359-427；
  - sleeve bloom：:432-765；beam swap：:767-976；tileMovement：:1043-1378。
- V4 测试桌构造器：`engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt:8-76`。重建遵循其真实顺序：先 reserve 显式 hands/drawn/wallPrefix/deadWallPrefix，再按座位补到 14（当前座位在 DISCARD/CHEAT_WINDOW）或 13，再补满 14 张王牌，最后把剩余实体按升序追加到活墙。
- V4 生产源：`engine/src/main/kotlin/com/majiang/economy/CheatLedger.kt:70,138,148,179`、`CheatLedgerEntry.kt`、`engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkState.kt:42,68,120`、`engine/src/main/kotlin/com/majiang/authority/cheat/{TileMark,SleeveBloom,BeamSwap}SubmissionHandler.kt`、`engine/src/main/kotlin/com/majiang/model/TileMovement.kt:469,518,567,611`。
- C# 真实入口：`dotnet/src/Mahjong.Domain/Cheat/CheatSubmission.cs:113,226,327,447,539,664,756,914`；物理原语：`dotnet/src/Mahjong.Domain/TilePositionMap.cs:637,686,1203,1258,1321,1375`。

### 1.2 重建控制

`V4TableReconstruction_MatchesExportedM5InputPlacements` 用上述构造器规则重建 `mv-reservation-delivery-then-beam` 的交付前牌桌，并逐项对照 golden 的 **136 个 `inputPlacements`**、`inputWallPointer=0`、`inputWallEndExclusive=69`、`inputRinshanPointer=0`、`lastDiscard=16/seat0`。136/136 一致。这是后续 5 个 movement 精确重建的可信控制，不是用 C# 输出反算输入。

### 1.3 槽位/指针映射

- movement 的 V4 `wall[k]`（从 0 开始的活墙列表）↔ C# `Wall(53+k)`；V4 指针 0 ↔ C# 53。M1a 的 `wallIndex=0` 因此绑定到 C# `Wall(53)`，不是旧测试的同构替代 `Wall(54)`。
- handler 导出的 `wallPointer=53`、`wallEndExclusive=113`、`offerSecondWallIndex=53` 已经是 V4 绝对槽位口径；直接绑定 C# `WallPointer/WallEndExclusive/PositionOf(...).Index`，不再加 53。
- `deadWall[i]` ↔ C# `DeadWall(i)`；`replacement[i]` ↔ `DeadWallReplacement(i)`；手牌按座位+容器，不按 V4 列表排序。
- 无法在 C# 规范牌桌中表示的 V4 过滤态单列为 BLOCKED（见 F-02），不以空测试或放宽断言消失。

### 1.4 允许的原因细化，不冒充完全相同

V4 把多个语义合并成粗码；C# 有更细的 typed code。以下均为“同输入、同拒绝、零副作用，C# 更细”，在 `CaseSpec.Kind=Refined` 中逐例登记：

- `WRONG_PHASE` → `ResponseWindowOpen`（response window）；
- `SLEEVE_BLOOM_DISABLED` / `BEAM_SWAP_DISABLED`（真实输入 `configuredCost=5`）→ `CostMustBeTen`，**没有**把输入换成 `featureEnabled=false`；
- `SLEEVE_BLOOM_WALL_EMPTY` / `BEAM_SWAP_NO_CANDIDATES` → `NoWallCandidate` 或 `CandidateUnavailable`；
- `SLEEVE_BLOOM_NO_WINDOW`（实际是已签发窗口的 id 不匹配）→ `WindowMismatch`；
- `BEAM_SWAP_INVALID_CHOICE` 的窗外槽 → `CandidateNotFound`；冻结外手牌/非法 keep 参数 → `InvalidSelection`；
- `SLEEVE_BLOOM_CHOICE_PENDING` / `BEAM_SWAP_CHOICE_PENDING` / `BEAM_SWAP_ALREADY_PENDING` → `OtherOfferPending` / `OfferAlreadyPending`。

## 2. Ledger：快照不是布尔

`Ledger_Snapshot_RestoresEveryEntryFieldInOrder` 对真实 `CheatLedger` 执行：

- `chargeOnce`：`entryId=skill-1`、`seat=0`、`amount=-7`、`balanceAfter=33`、`reason=TILE_MARK`（V4 导出器 :115 的源字面量）逐项断言；四座余额 `33 40 40 40`；entryCount=1。
- 同 id 同载荷：`Replayed=true`、`Assert.Same` 原 entry、entryCount=1。
- flow：`spend` 后 `gain`，顺序固定；逐条断言 entryId/seat/amount/balanceAfter/reason；`seat0TotalSpent=7`、`seat1TotalGained=5`、`seat1NetDelta=5` 从 entries 重算后与 golden 对照。
- `FromSnapshot(10, finalBalances, flow.Entries)`：恢复 2 条 entry，逐条逐字段、顺序、余额、`Version=2`；`entriesEqual=true` 只是导出摘要，不是唯一断言。
- 余额漂移 `finalBalances[0]+1` 必须 `ArgumentException`；这是负例控制，证明恢复路径不是无条件接受。

| JSONpath（可逐叶展开） | 实际调用/断言 |
|---|---|
| `$.ledger.initialPoints[0..3]` | `CheatLedger.Initialize(Economy(initialPoints[0]))`；四座起始余额 |
| `$.ledger.chargeOnce.entryId/seat/amount/reason` | `Apply(...)` 的输入；`Entry.EntryId/Seat/Amount/Reason` 逐项断言 |
| `$.ledger.chargeOnce.balanceAfter` | `Entry.BalanceAfter==33` |
| `$.ledger.chargeOnce.balancesAfter[0..3]` | `Ledger.BalanceOf(seat)` 四座 |
| `$.ledger.chargeOnce.entryCount` | `Ledger.Entries.Count` |
| `$.ledger.replaySamePayload.returnsOriginalEntry/balanceAfter/entryCount` | 二次同载荷 `Apply`；`Replayed`、`Assert.Same`、entryCount |
| `$.ledger.conflictDifferentPayload.requested.seat/amount` | 同 id 异载荷 `Apply`；`EntryConflict`、`Ledger=null` |
| `$.ledger.conflictDifferentPayload.balancesAfter[0..3]/entryCount` | 冲突后源 ledger 余额/条数不变 |
| `$.ledger.insufficientFunds.initialPoints/amount/outcome/exception` | 10 点账本扣 11；`InsufficientFunds`、零副作用 |
| `$.ledger.overflow.amount/outcome/exception` | `int.MaxValue`；`BalanceOverflow`、零副作用 |
| `$.ledger.invalidSeat.seat/outcome/exception` | seat4；`InvalidSeat`、零副作用 |
| `$.ledger.flowSummary.spend.seat/amount` | `Apply("spend",...)`；entry0 全字段 |
| `$.ledger.flowSummary.gain.seat/amount` | `Apply("gain",...)`；entry1 全字段 |
| `$.ledger.flowSummary.seat0TotalSpent/seat1TotalGained/seat1NetDelta` | 从 entries 重算后对照 |
| `$.ledger.flowSummary.finalBalances[0..3]` | `BalanceOf(seat)` 四座 |
| `$.ledger.snapshotRoundTrip.balancesEqual/entriesEqual` | 导出摘要；不作为唯一断言，另做 entry 逐字段/顺序/Version 断言 |

## 3. TileMarkState：版本、到期、跨座位、零副作用

`TileMarkState_BindsEveryExportedFieldAndZeroSideEffect` 绑定：

- 初始 version0/ordinal0/dealtIds/settledIds 空；`registerDealtRound("r1")` 后 ordinal1、version1、`CurrentRoundId=r1`、同 id 返回同一实例。
- `g1` 全字段：groupId、owner0、tileIds `[0,1,2,3]` 顺序、createdRoundId `r1`、createdOrdinal1、expiresAfter4、version2；`MarkedTilesForSeat(0)` 顺序一致；同 id 同内容返回同一实例；同 id 异内容 `GroupIdConflict` 且源状态/版本不变。
- 三组容量：3 组接受，第 4 组 `GroupLimitExceeded` 且源状态/组数不变；每组实体数固定为 4，因此 12 张上限与 3 组上限在公开构造边界重合；`seatTileLimitIsTwelve=true` 被绑定，`TileLimitExceeded` 的 13+ 分支在公开 API 下不可达（这是 C# 的结构事实，不是跳过）。
- 跨座位同一实体 `[0,1,2,3]` 允许，组数2、version3。
- 到期：createdOrdinal1→expires4；完成 r3 仍有1组/version7；完成 r4 后0组/version9；`SettledRoundIds` 精确 `["r1","r2","r3","r4"]`，`DealtRoundOrdinal=4`，`IsSettled("r3")=true`。

| JSONpath（可逐叶展开） | 实际调用/断言 |
|---|---|
| `$.tileMarkState.registerDealtRound.ordinalAfterFirst` | `RegisterDealtRound("r1").DealtRoundOrdinal` |
| `$.tileMarkState.registerDealtRound.sameIdReplays` | 再次 `RegisterDealtRound("r1")`；`Assert.Same` |
| `$.tileMarkState.registerDealtRound.versionAfterFirstRegister` | `Version==1`（同时与 `versionTrail[1]` 对照） |
| `$.tileMarkState.addGroup.expiresAfterRoundOrdinal` | `Groups[0].ExpiresAfterRoundOrdinal==4` |
| `$.tileMarkState.addGroup.sameIdSameContentReplays` | `AddGroup(g1)` 二次；`Assert.Same` |
| `$.tileMarkState.addGroup.sameIdDifferentContentException` | `AddGroup(g1 异内容)`；`GroupIdConflict`、源状态/版本不变 |
| `$.tileMarkState.addGroup.seatGroupLimitIsThree/seatGroupLimitException` | 3 组接受；第 4 组 `GroupLimitExceeded`、零副作用 |
| `$.tileMarkState.addGroup.seatTileLimitIsTwelve` | 3×4=12；第 4 组在 group limit 处拒绝（公开 API 的 13+ 分支不可达） |
| `$.tileMarkState.addGroup.crossSeatSameTilesAllowed/crossSeatGroupCount` | 座位1 同实体 `[0,1,2,3]`；组数2/version3 |
| `$.tileMarkState.addGroup.versionTrail[0..3]` | 初始0、register1、add2、cross3 的逐点断言 |
| `$.tileMarkState.expiry.createdRoundOrdinal` | `g1.CreatedRoundOrdinal==1` |
| `$.tileMarkState.expiry.groupsAfterCompletingRound3` | 完成 r3 后组数1 |
| `$.tileMarkState.expiry.groupsAfterCompletingRound4` | 完成 r4 后组数0 |
| `$.tileMarkState.expiry.settledRoundCount` | `SettledRoundIds.Count==4` 且逐项 `r1..r4` |
| `$.tileMarkState.expiry.versionAfterFirstAdd/versionAfterCompletingRound3/versionAfterCompletingRound4` | 2/7/9 |

## 4. 75 个 handler case 的完整映射

每条 JSONpath 都可直接展开；`PASS`/`PASS-refined` 行由对应的 `[Theory]` 真实调用，`BLOCKED`/`FAIL-open` 行由 manifest 固定并在报告中单列，不能计入行为通过。表内 `Allowed / —` 的 reason 为 JSON null。

真实调用计数：68 个 handler case（16 tileMark + 31 sleeve + 21 beam）各至少一次真实 Domain 调用；6 个 movement 各真实调用物理入口；22 个外部变异各一次独立测试进程；ledger/state 两个深字段 Fact。各行的 V4 输入源按 section 固定：`tileMarkHandler` 为 exporter `:359-427`，`sleeveBloomHandler` 为 `:432-765`，`beamSwapHandler` 为 `:767-976`；7 个 sleeve 成功例的 `goldenInputs` 逐字段读取，其余 68 个没有 `goldenInputs`，输入由 exporter/`TestTableBuilder` 构造代码重建。

| Case | JSONpath | 真实入口 | V4 outcome / reason | 状态 | C# code | 测试 |
|---|---|---|---|---|---|---|
| tm-allowed | $.tileMarkHandler[?(@.caseId=='tm-allowed')] | CheatSubmission.ApplyTileMark | Allowed / — | PASS | None | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-not-your-turn | $.tileMarkHandler[?(@.caseId=='tm-not-your-turn')] | CheatSubmission.ApplyTileMark | Rejected / NOT_YOUR_TURN | PASS | NotYourTurn | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-wrong-phase-preparation | $.tileMarkHandler[?(@.caseId=='tm-wrong-phase-preparation')] | CheatSubmission.ApplyTileMark | Rejected / WRONG_PHASE | PASS | WrongPhase | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-wrong-phase-draw | $.tileMarkHandler[?(@.caseId=='tm-wrong-phase-draw')] | CheatSubmission.ApplyTileMark | Rejected / WRONG_PHASE | PASS | WrongPhase | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-wrong-phase-round-end | $.tileMarkHandler[?(@.caseId=='tm-wrong-phase-round-end')] | RoundStateMachine terminal guard + actor decision binding | Rejected / DECISION_MISMATCH | BLOCKED-actor | — | BindingManifest row; actor dependency |
| tm-response-window | $.tileMarkHandler[?(@.caseId=='tm-response-window')] | CheatSubmission.ApplyTileMark | Rejected / WRONG_PHASE | PASS-refined | ResponseWindowOpen | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-riichi-locked | $.tileMarkHandler[?(@.caseId=='tm-riichi-locked')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_RIICHI_LOCKED | PASS | RiichiLocked | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-invalid-selection-three | $.tileMarkHandler[?(@.caseId=='tm-invalid-selection-three')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_INVALID_SELECTION | PASS | InvalidSelection | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-invalid-selection-duplicate | $.tileMarkHandler[?(@.caseId=='tm-invalid-selection-duplicate')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_INVALID_SELECTION | PASS | InvalidSelection | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-tile-not-owned | $.tileMarkHandler[?(@.caseId=='tm-tile-not-owned')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_TILE_NOT_OWNED | PASS | TileNotOwned | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-already-marked | $.tileMarkHandler[?(@.caseId=='tm-already-marked')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_ALREADY_MARKED | PASS | AlreadyMarked | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-capacity-groups | $.tileMarkHandler[?(@.caseId=='tm-capacity-groups')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_CAPACITY_EXCEEDED | PASS | MarkCapacityExceeded | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-capacity-tiles | $.tileMarkHandler[?(@.caseId=='tm-capacity-tiles')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_CAPACITY_EXCEEDED | PASS | MarkCapacityExceeded | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-insufficient-funds | $.tileMarkHandler[?(@.caseId=='tm-insufficient-funds')] | CheatSubmission.ApplyTileMark | Rejected / INSUFFICIENT_FUNDS | PASS | InsufficientFunds | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-sleeve-pending | $.tileMarkHandler[?(@.caseId=='tm-sleeve-pending')] | CheatSubmission.ApplyTileMark | Rejected / SLEEVE_BLOOM_CHOICE_PENDING | PASS-refined | OtherOfferPending | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-offturn-sleeve-pending | $.tileMarkHandler[?(@.caseId=='tm-offturn-sleeve-pending')] | CheatSubmission.ApplyTileMark | Rejected / SLEEVE_BLOOM_CHOICE_PENDING | PASS-refined | OtherOfferPending | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-beam-pending | $.tileMarkHandler[?(@.caseId=='tm-beam-pending')] | CheatSubmission.ApplyTileMark | Rejected / BEAM_SWAP_CHOICE_PENDING | PASS-refined | OtherOfferPending | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-no-active-decision | $.tileMarkHandler[?(@.caseId=='tm-no-active-decision')] | DecisionClock/actor decision admission | Rejected / NO_ACTIVE_DECISION | BLOCKED-actor | — | BindingManifest row; actor dependency |
| tm-deadline-expired | $.tileMarkHandler[?(@.caseId=='tm-deadline-expired')] | DecisionTiming.IsExpired(now == deadline) | Rejected / DEADLINE_EXPIRED | BLOCKED-actor | — | BindingManifest row; actor dependency |
| tm-decision-mismatch | $.tileMarkHandler[?(@.caseId=='tm-decision-mismatch')] | actor round/decision identity binding | Rejected / DECISION_MISMATCH | BLOCKED-actor | — | BindingManifest row; actor dependency |
| sb-activate-allowed | $.sleeveBloomHandler[?(@.caseId=='sb-activate-allowed')] | CheatSubmission.ActivateSleeveBloom | Allowed / — | PASS | None | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-dealer-extra | $.sleeveBloomHandler[?(@.caseId=='sb-activate-dealer-extra')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED | PASS | DealExtraDraw | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-already-used | $.sleeveBloomHandler[?(@.caseId=='sb-activate-already-used')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW | PASS | AlreadyUsedThisTurn | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-beam-pending | $.sleeveBloomHandler[?(@.caseId=='sb-activate-beam-pending')] | CheatSubmission.ActivateSleeveBloom | Rejected / BEAM_SWAP_ALREADY_PENDING | PASS-refined | OtherOfferPending | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-sleeve-pending | $.sleeveBloomHandler[?(@.caseId=='sb-activate-sleeve-pending')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_ALREADY_PENDING | PASS | OfferAlreadyPending | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-wall-empty | $.sleeveBloomHandler[?(@.caseId=='sb-activate-wall-empty')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_WALL_EMPTY | PASS-refined | NoWallCandidate | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-wall-empty-drift | $.sleeveBloomHandler[?(@.caseId=='sb-activate-wall-empty-drift')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_WALL_EMPTY | FAIL-open | — | SleeveBloom_WallEmptyDrift_PinsKnownDivergence |
| sb-activate-no-drawn-tile | $.sleeveBloomHandler[?(@.caseId=='sb-activate-no-drawn-tile')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_NO_DRAWN_TILE | PASS | NoDrawnTile | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-not-your-turn | $.sleeveBloomHandler[?(@.caseId=='sb-activate-not-your-turn')] | CheatSubmission.ActivateSleeveBloom | Rejected / NOT_YOUR_TURN | PASS | NotYourTurn | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-wrong-phase | $.sleeveBloomHandler[?(@.caseId=='sb-activate-wrong-phase')] | CheatSubmission.ActivateSleeveBloom | Rejected / WRONG_PHASE | PASS | WrongPhase | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-response-window | $.sleeveBloomHandler[?(@.caseId=='sb-activate-response-window')] | CheatSubmission.ActivateSleeveBloom | Rejected / WRONG_PHASE | PASS-refined | ResponseWindowOpen | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-disabled | $.sleeveBloomHandler[?(@.caseId=='sb-activate-disabled')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_DISABLED | PASS-refined | CostMustBeTen | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-insufficient | $.sleeveBloomHandler[?(@.caseId=='sb-activate-insufficient')] | CheatSubmission.ActivateSleeveBloom | Rejected / INSUFFICIENT_FUNDS | PASS | InsufficientFunds | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-rinshan-allowed | $.sleeveBloomHandler[?(@.caseId=='sb-activate-rinshan-allowed')] | CheatSubmission.ActivateSleeveBloom | Allowed / — | PASS | None | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-rinshan-empty | $.sleeveBloomHandler[?(@.caseId=='sb-activate-rinshan-empty')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_WALL_EMPTY | PASS-refined | NoWallCandidate | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-choose-first | $.sleeveBloomHandler[?(@.caseId=='sb-choose-first')] | CheatSubmission.ChooseSleeveBloom | Allowed / — | PASS | None | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-choose-second | $.sleeveBloomHandler[?(@.caseId=='sb-choose-second')] | CheatSubmission.ChooseSleeveBloom | Allowed / — | PASS | None | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-choose-stale-turn | $.sleeveBloomHandler[?(@.caseId=='sb-choose-stale-turn')] | CheatSubmission.ChooseSleeveBloom | StaleOffer / SLEEVE_BLOOM_STALE_OFFER | PASS | StaleOffer | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-choose-stale-drawn | $.sleeveBloomHandler[?(@.caseId=='sb-choose-stale-drawn')] | CheatSubmission.ChooseSleeveBloom | StaleOffer / SLEEVE_BLOOM_STALE_OFFER | PASS | StaleOffer | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-choose-stale-index | $.sleeveBloomHandler[?(@.caseId=='sb-choose-stale-index')] | CheatSubmission.ChooseSleeveBloom | StaleOffer / SLEEVE_BLOOM_STALE_OFFER | PASS | StaleOffer | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-choose-offer-mismatch | $.sleeveBloomHandler[?(@.caseId=='sb-choose-offer-mismatch')] | CheatSubmission.ChooseSleeveBloom | Rejected / SLEEVE_BLOOM_OFFER_MISMATCH | PASS | OfferMismatch | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-choose-no-offer | $.sleeveBloomHandler[?(@.caseId=='sb-choose-no-offer')] | CheatSubmission.ChooseSleeveBloom | Rejected / SLEEVE_BLOOM_NO_PENDING_OFFER | PASS | NoPendingOffer | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-choose-rinshan | $.sleeveBloomHandler[?(@.caseId=='sb-choose-rinshan')] | CheatSubmission.ChooseSleeveBloom | Allowed / — | PASS | None | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sbo-activate-allowed | $.sleeveBloomHandler[?(@.caseId=='sbo-activate-allowed')] | CheatSubmission.ActivateOffTurnSleeveBloom | Allowed / — | PASS | None | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sbo-activate-no-window | $.sleeveBloomHandler[?(@.caseId=='sbo-activate-no-window')] | CheatSubmission.ActivateOffTurnSleeveBloom | Rejected / SLEEVE_BLOOM_NO_WINDOW | PASS-refined | WindowMismatch | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sbo-activate-reserved | $.sleeveBloomHandler[?(@.caseId=='sbo-activate-reserved')] | CheatSubmission.ActivateOffTurnSleeveBloom | Rejected / SLEEVE_BLOOM_RESERVED_PENDING | PASS | ReservedPending | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sbo-activate-own-turn | $.sleeveBloomHandler[?(@.caseId=='sbo-activate-own-turn')] | CheatSubmission.ActivateOffTurnSleeveBloom | Rejected / NOT_YOUR_TURN | PASS | NotYourTurn | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sbo-activate-wall-empty | $.sleeveBloomHandler[?(@.caseId=='sbo-activate-wall-empty')] | CheatSubmission.ActivateOffTurnSleeveBloom | Rejected / SLEEVE_BLOOM_WALL_EMPTY | PASS | WallEmpty | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sbo-choose-allowed | $.sleeveBloomHandler[?(@.caseId=='sbo-choose-allowed')] | CheatSubmission.ChooseOffTurnSleeveBloom | Allowed / — | PASS | None | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sbo-choose-expired | $.sleeveBloomHandler[?(@.caseId=='sbo-choose-expired')] | CheatSubmission.ChooseOffTurnSleeveBloom | Rejected / DEADLINE_EXPIRED | PASS | DeadlineExpired | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sbo-choose-stale-first | $.sleeveBloomHandler[?(@.caseId=='sbo-choose-stale-first')] | CheatSubmission.ChooseOffTurnSleeveBloom | Rejected / SLEEVE_BLOOM_STALE_OFFER | PASS | StaleOffer | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sbo-choose-stale-second | $.sleeveBloomHandler[?(@.caseId=='sbo-choose-stale-second')] | CheatSubmission.ChooseOffTurnSleeveBloom | Rejected / SLEEVE_BLOOM_STALE_OFFER | PASS | StaleOffer | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-allowed | $.beamSwapHandler[?(@.caseId=='bm-activate-allowed')] | CheatSubmission.ActivateBeamSwap | Allowed / — | PASS | None | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-no-candidates | $.beamSwapHandler[?(@.caseId=='bm-activate-no-candidates')] | CheatSubmission.ActivateBeamSwap | Rejected / BEAM_SWAP_NO_CANDIDATES | PASS-refined | NoWallCandidate | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-dealer-extra | $.beamSwapHandler[?(@.caseId=='bm-activate-dealer-extra')] | CheatSubmission.ActivateBeamSwap | Rejected / BEAM_SWAP_DEALER_EXTRA_EXCLUDED | PASS | DealExtraDraw | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-riichi | $.beamSwapHandler[?(@.caseId=='bm-activate-riichi')] | CheatSubmission.ActivateBeamSwap | Rejected / BEAM_SWAP_RIICHI_LOCKED | PASS | RiichiLocked | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-no-drawn | $.beamSwapHandler[?(@.caseId=='bm-activate-no-drawn')] | CheatSubmission.ActivateBeamSwap | Rejected / BEAM_SWAP_NO_DRAWN_TILE | PASS | NoDrawnTile | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-beam-pending | $.beamSwapHandler[?(@.caseId=='bm-activate-beam-pending')] | CheatSubmission.ActivateBeamSwap | Rejected / BEAM_SWAP_ALREADY_PENDING | PASS | OfferAlreadyPending | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-sleeve-pending | $.beamSwapHandler[?(@.caseId=='bm-activate-sleeve-pending')] | CheatSubmission.ActivateBeamSwap | Rejected / BEAM_SWAP_ALREADY_PENDING | PASS | OfferAlreadyPending | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-already-used | $.beamSwapHandler[?(@.caseId=='bm-activate-already-used')] | CheatSubmission.ActivateBeamSwap | Rejected / BEAM_SWAP_ALREADY_USED_THIS_DRAW | PASS | AlreadyUsedThisTurn | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-disabled | $.beamSwapHandler[?(@.caseId=='bm-activate-disabled')] | CheatSubmission.ActivateBeamSwap | Rejected / BEAM_SWAP_DISABLED | PASS-refined | CostMustBeTen | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-insufficient | $.beamSwapHandler[?(@.caseId=='bm-activate-insufficient')] | CheatSubmission.ActivateBeamSwap | Rejected / INSUFFICIENT_FUNDS | PASS | InsufficientFunds | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-deadwall-allowed | $.beamSwapHandler[?(@.caseId=='bm-activate-deadwall-allowed')] | CheatSubmission.ActivateBeamSwap | Allowed / — | PASS | None | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-activate-deadwall-empty | $.beamSwapHandler[?(@.caseId=='bm-activate-deadwall-empty')] | physical dead-wall map with no eligible candidate | Rejected / BEAM_SWAP_NO_CANDIDATES | BLOCKED-unrepr | — | BeamDeadWallFilterStates_AreNotCanonicalTilePositionMaps |
| bm-activate-deadwall-dora-only | $.beamSwapHandler[?(@.caseId=='bm-activate-deadwall-dora-only')] | physical dead-wall map with only the dora indicator located in the dead wall | Rejected / BEAM_SWAP_NO_CANDIDATES | BLOCKED-unrepr | — | BeamDeadWallFilterStates_AreNotCanonicalTilePositionMaps |
| bm-choose-swap | $.beamSwapHandler[?(@.caseId=='bm-choose-swap')] | CheatSubmission.ChooseBeamSwap | Allowed / — | PASS | None | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-choose-keep | $.beamSwapHandler[?(@.caseId=='bm-choose-keep')] | CheatSubmission.ChooseBeamSwap | Allowed / — | PASS | None | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-choose-invalid-slot | $.beamSwapHandler[?(@.caseId=='bm-choose-invalid-slot')] | CheatSubmission.ChooseBeamSwap | Rejected / BEAM_SWAP_INVALID_CHOICE | PASS-refined | CandidateNotFound | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-choose-invalid-foreign-hand | $.beamSwapHandler[?(@.caseId=='bm-choose-invalid-foreign-hand')] | CheatSubmission.ChooseBeamSwap | Rejected / BEAM_SWAP_INVALID_CHOICE | PASS | InvalidSelection | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-keep-original-args-forbidden-at-request | $.beamSwapHandler[?(@.caseId=='bm-keep-original-args-forbidden-at-request')] | CheatSubmission.ChooseBeamSwap | Rejected / BEAM_SWAP_INVALID_CHOICE | PASS-refined | InvalidSelection | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-choose-stale-turn | $.beamSwapHandler[?(@.caseId=='bm-choose-stale-turn')] | CheatSubmission.ChooseBeamSwap | StaleOffer / BEAM_SWAP_STALE_OFFER | PASS | StaleOffer | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-choose-stale-drawn | $.beamSwapHandler[?(@.caseId=='bm-choose-stale-drawn')] | CheatSubmission.ChooseBeamSwap | StaleOffer / BEAM_SWAP_STALE_OFFER | PASS | StaleOffer | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-choose-deadwall-allowed | $.beamSwapHandler[?(@.caseId=='bm-choose-deadwall-allowed')] | CheatSubmission.ChooseBeamSwap | Allowed / — | PASS | None | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-choose-deadwall-dora-stale | $.beamSwapHandler[?(@.caseId=='bm-choose-deadwall-dora-stale')] | CheatSubmission.ChooseBeamSwap | StaleOffer / BEAM_SWAP_STALE_OFFER | PASS | StaleOffer | BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| bm-choose-replacement-allowed | $.beamSwapHandler[?(@.caseId=='bm-choose-replacement-allowed')] | CheatSubmission.ChooseBeamSwap | Allowed / — | PASS | None | BeamSwap_BoundCase_ReplaysThroughDomainEntry |

### 4.1 逐例执行的共同断言

- 拒绝：`Success=false`、`Changed=false`、`Error=声明的 C# code`、`State=null`、`Map=null`、`Events=[]`，且输入 `CheatState` 的 ledger 余额/entryCount、mark version/groupCount、sleeve version/offer/reservation、beam version/offer 与 map 的 136 位置快照全部不变。
- Stale（immediate/beam）：`Error=StaleOffer`、`Changed=true`、`State` 非空且 offer 已清除、`Map=null`、ledger 不变、事件为 clear。
- Stale（off-turn）：V4 语义是 plain rejection，`State=null`、offer 仍挂起；与 immediate/beam 的 “带清除状态” 分开断言。
- 到期：`DeadlineExpired`、`Changed=true`、offer 清除、reservation 落在 FIRST、事件 Keep=First。
- Allowed：逐例检查 offer/group/reservation/candidate/map 后态、扣费 entry 的 id/amount/balance/reason、事件序列、136 守恒；不只看 `Success`。

### 4.2 特别说明

- `tm-capacity-tiles`：V4 handler 输入是 `groups=2, marked=(16..27)`（12 张）+4；C# `TileMarkGroup` 固定 4 张，12 张只能由 3 个组构成，因此 C# 走 3 组后第 4 组，group limit 映射到同一个 `MarkCapacityExceeded`。行为码一致，输入形状不是逐字同构；已在行内登记。
- `sb-activate-disabled` / `bm-activate-disabled`：真实输入是 `configuredCost=5`，不是 `featureEnabled=false`。旧测试换输入的做法已删除；现在断言 `CostMustBeTen`，仍为零副作用。
- `bm-choose-invalid-foreign-hand`：真实输入是 `handTileId=52`（当前摸牌），不是另一座手牌；旧测试已纠正。
- `bm-choose-deadwall-dora-stale`：C# 用真实 `BeamSwapState.Restore` 注入“冻结候选含 dora 槽”的 V4 输入，再走真实 `ChooseBeamSwap`；不是只读数组。

## 5. 6 个物理移动

| Case | JSONpath | 真实入口/断言 | 结果 |
|---|---|---|---|
| `mv-swap-hand-with-wall-direct` | `$.tileMovement[?(@.caseId=='mv-swap-hand-with-wall-direct')]` | `TilePositionMap.SwapHandWithWall`，V4 wall0→C# Wall53；factsBefore/After、指针、容器、136 | PASS |
| `mv-swap-drawn-with-wall-backfill` | `...=='mv-swap-drawn-with-wall-backfill'` | `DrawFromWall` → `ActivateSleeveBloom` → `ChooseSleeveBloom(SECOND)`；drawn 回 Wall54、墙牌进手并成为新 drawn evidence、指针不前进 | PASS（表示差异见 5.1） |
| `mv-reservation-delivery` | `...=='mv-reservation-delivery'` | `DeliverSleeveBloomReservation`；removeAt、wallEnd-1、指针不动、17 进手、16 在河 | PASS |
| `mv-reservation-delivery-then-beam` | `...=='mv-reservation-delivery-then-beam'` | 既有 `DeliveryThenBeam`：136 placements、8 候选实体+槽、beamGot/nextDraw、factsAfter | PASS |
| `mv-rinshan-and-drawn-swap` | `...=='mv-rinshan-and-drawn-swap'` | `DrawReplacement` + `SwapDrawnWithNextRinshan`；rinshanPointer1、wallEnd-1、deadWall1=108、109 进手、135 replacement | PASS |
| `mv-swap-hand-with-dead-wall` | `...=='mv-swap-hand-with-dead-wall'` | `DrawReplacement` + `SwapHandWithDeadWall(1)` + dora/WallSlot 拒绝 + `SwapHandWithReplacement(0)`；factsAfter 四项 | PASS |

### 5.1 drawn-backfill 的表示差异（不是 V4-only）

旧注释声称该路径“C# 无入口”，只因为把入口限定为 `ChooseBeamSwap`（它按设计拒绝摸牌实体）。实际 `ChooseSleeveBloom` 的 normal-draw SECOND 分支调用 `SwapHandWithWall`，允许 drawn 作为手牌来源，并把墙牌写成新的 `DrawnTileEvidence`；因此该法律 **有真实入口**。

C# 规范牌桌在摸牌后不保留 V4 的“旧墙槽洞”：`DrawFromWall` 把实体从 Wall53 移到手中。V4 的 `backfilledIndex=0/backfilledTileId=29` 表示 29 填回旧洞；C# 对应后态是 Wall53 为空、29 成为新的 drawn evidence，而 33 在 Wall54、29 在手中。实体/容器/座位/槽位/后续摸牌顺序一致；`backfilledIndex` 这个“洞”字段不能在 C# 规范 map 中物化。测试显式断言这一映射，不声称逐字相同，也不因此把 case 算作 BLOCKED。

## 6. 一次性缺陷/缺口清单

共同复现命令（只读本树，测试会自行创建内存牌桌；矩阵证据写新 TEMP）：

```powershell
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class '*CheatGoldenBindingTests*' -showLiveOutput -noLogo
```

具体 test 名在每条 F-01/F-02/F-03 中给出；xUnit 的 `-class` 输出会列出 theory 的 `caseId`。

### F-01（MAJOR，OPEN DIVERGENCE）sleeve 的“下一张墙牌漂移”被 C# 重新搜索而放行

- **位置**：`dotnet/src/Mahjong.Domain/Cheat/CheatSubmission.cs:278-286`（`FindFirstAvailableWallIndex`）与 V4 `SleeveBloomSubmissionHandler` 的 `nextWallTileId/nextWallTileInWall` 检查；golden `sleeveBloomHandler[sb-activate-wall-empty-drift]`。
- **复现**：`SleeveBloom_WallEmptyDrift_PinsKnownDivergence`。真实输入：drawn=16，wall 首个候选 32 不在墙（32 在河中），40/44/48 仍在墙；V4 的 `nextWallTileId=32` 已离开墙，返回 `Rejected/SLEEVE_BLOOM_WALL_EMPTY`。
- **实际**：C# 重新扫描，选 40 为第二候选，返回 `Allowed`，扣10、生成 offer。**期望**：同输入应 `Rejected/NoWallCandidate`（V4 `SLEEVE_BLOOM_WALL_EMPTY`），零扣费、零 offer。
- **证据**：`C:/Users/sncdc/AppData/Local/Temp/n3-cheat-binding-probe-7acbace0dfd64cc384aa41d58acdd8d8` 的测试源码/日志；本树 `CheatGoldenBindingTests.cs` 的 pin 测试。该 pin 在行为被修正为拒绝后会失败，迫使更新绑定，不是永久豁免。
- **修复要求**：不要改规则/放宽断言。需要 caller 决策：(a) 接受 C# “重新搜索更稳”的行为并更新 V4/golden 语义；或 (b) 给 Domain 增加冻结候选输入（`nextWallTileId`/`frozenWallIndex`）并在 `ActivateSleeveBloom` 校验其仍在墙；或 (c) 由 actor 层在调用前做该检查。选 (b)/(c) 时需补生产测试与本 case 的正/负矩阵。
- **验收断言**：同输入 `Success=false`、`Error=NoWallCandidate`、ledger 40 不变、无 offer、map 136 不变；正控制 `sb-activate-allowed` 仍 Allowed。

### F-02（BLOCKED，UNREPRESENTABLE）beam dead-wall 的“空集/仅 dora”过滤态不是规范牌桌

- **位置**：V4 `BeamSwapSubmissionHandler.ActivationInput.deadWallTileIdsInDeadWall` 与 golden `bm-activate-deadwall-empty` / `bm-activate-deadwall-dora-only`；C# `TilePositionMap.Validate` 要求 `deadWall + replacements == 14`（`TilePositionMap.cs:1587-1593`），`ActivateBeamSwap` 只从规范 DeadWall/Replacement 读候选（`CheatSubmission.cs:842-882`）。
- **复现**：`BeamDeadWallFilterStates_AreNotCanonicalTilePositionMaps` 构造两种过滤态，均 `ConservationException`（有效王牌区≠14）。
- **实际**：C# 无法表示“14 张王牌实体仍在，但不在 deadWall 位置”的 V4 handler 过滤态；规范 map 下 dead-wall 窗口至少有 10+4 个物理候选。**期望**：若产品要求该拒绝，需要 handler 级 `deadWallTileIdsInDeadWall` 输入或候选抑制面，而不是把实体搬出王牌区。
- **证据**：上述测试、`TilePositionMap.cs:1587-1593`、golden 两 case。
- **修复要求**：新增 handler-level 输入或在 actor 适配层维护该集合；不得通过空王牌区/伪造 136 位置绕过守恒。`bm-activate-deadwall-allowed`、`bm-choose-deadwall-allowed`、`bm-choose-deadwall-dora-stale`、`bm-choose-replacement-allowed` 已真实绑定；dora 排除另有 `BeamSwap_DoraIndicator_IsNeverACandidate` 正/负验证。
- **验收断言**：在规范 map 上调用新入口，空集/仅 dora 均 Rejected，其他合法槽仍 Allowed；136 守恒；无扣费/offer 副作用。

### F-03（BLOCKED，ACTOR LAYER）4 个 tile-mark 决策/截止时间 case 不属于 `CheatSubmission`

- **位置**：`tm-wrong-phase-round-end`、`tm-no-active-decision`、`tm-deadline-expired`、`tm-decision-mismatch`；`CheatTurnContext` 只有 `NowMillis`，没有 decisionId/deadline/round-ended 字段；`ApplyTileMark` 不比较 deadline。
- **实际**：Domain handler 不能在这些 V4 输入上给出 `DECISION_MISMATCH`/`NO_ACTIVE_DECISION`/`DEADLINE_EXPIRED`。**期望**：由 actor 决策门先校验 round/decision 身份与 `DecisionTiming.IsExpired(now==deadline)`，再调用 Domain。
- **对应已接受入口**：`dotnet/src/Mahjong.Domain/DecisionClock.cs` 的 `DecisionTiming.IsExpired`/`DecisionClock.Timing`；下一精确接线点是未来的 actor/TableRuntime 命令分发（N-1b 完成后），不是 `CheatSubmission` 内偷偷加时钟依赖。
- **修复要求**：N-1b/actor 层接线后补 4 例；在那之前 manifest 固定 BLOCKED，不计 PASS。**验收断言**：deadline 相等即拒绝、无 decision 即拒绝、round ended/round id 不符即拒绝，均零扣费；合法 case 仍 Allowed。

### F-04（MINOR，fixture note 不自洽）`tm-capacity-tiles` 的 note 与 exporter 输入不一致

- **位置**：`tests/v5-golden/cheat-v1.json` 该 case 的 note “8 marked + 4 > 12”；exporter `V5CheatGoldenExporterTest.kt:418` 实际 `marked=(16..27).toSet()` 是 12 张。
- **实际/期望**：行为 reason 一致（`TILE_MARK_CAPACITY_EXCEEDED`），但 note 的 8 与输入 12 不一致。**修复**：下一次合法导出时把 note 改成 “12 marked + 4 > 12”，或删除 note 中的计数；本轮不改 fixture（用户所有）。
- **验收**：重新导出后 fixture note 与 `marked` 集合一致；断言不变。

### F-05（MINOR，V4 list artifact）M3 `factsAfter` 的 108 槽位

- **位置**：`V5CheatGoldenExporterTest.kt:1315-1330`；V4 `TileMovement.kt:591` 保留 `deadWall[0]` 旧引用，`swapDrawnWithNextRinshan` 又写 `deadWall[1]=108`，`factsJson` 的 `indexOf(108)` 返回旧槽 0；权威字段 `deadWallSlot1AfterSwap=108`。
- **实际/期望**：C# 规范 map 只有一个 108，位于 `DeadWall(1)`；测试绑定 `deadWallSlot1AfterSwap` 与其它两条 facts，对 108 只断言容器为 deadWall，不把重复列表 artifact 当槽位。**修复**：下次导出可让 `factsJson` 对重复实体显式给槽位/去重；本轮不改。
- **验收**：重导出后 108 的 factsAfter index=1；当前绑定保持 `deadWallSlot1AfterSwap=1`。

## 7. 外部单字段错值矩阵（22 项）

脚本：`tools/v5-golden/probe-cheat-binding.ps1`。每次从原始副本复制，只改一个字段，`V5_CHEAT_GOLDEN_PATH` 指向独占 TEMP 副本，`try/finally` 恢复环境变量；原 fixture 不写。正控制为未修改副本，96 tests / 0 failed / exit0。每项要求：native exit1、Total=96、Errors=0、Skipped=0、Not Run=0、真实 Failed>0，且预期测试名出现在失败列表；`ConvertTo-Json` 写副本时显式去 BOM，避免把编码异常冒充行为失败。

| ID | 类别 | 字段 | 结果 | exit / Total / Failed / Skipped | 首个失败测试 |
|---|---|---|---|---|---|
| outcome-tilemark | outcome | tileMarkHandler[tm-allowed].outcome | FAIL_AS_EXPECTED | 1/96/3/0 | CheatGoldenTests.Handler_Sections_CoverEveryRejectionReason |
| reason-tilemark | reason | tileMarkHandler[tm-not-your-turn].reason | FAIL_AS_EXPECTED | 1/96/3/0 | CheatGoldenTests.Handler_TileMark_RepresentativeCases_ReplayAgainstCheatSubmission |
| reason-beam | reason | beamSwapHandler[bm-activate-riichi].reason | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenBindingTests.BindingManifest_PreservesTheFullDenominator |
| ledger-amount | ledger-amount | ledger.chargeOnce.amount | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenTests.Ledger_Section_ReplaysAgainstCheatLedger |
| ledger-balance | ledger-balance | ledger.chargeOnce.balancesAfter[0] | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenTests.Ledger_Section_ReplaysAgainstCheatLedger |
| ledger-flow | ledger-flow | ledger.flowSummary.seat1NetDelta | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenTests.Ledger_Section_ReplaysAgainstCheatLedger |
| ledger-snapshot-balance | ledger-balance | ledger.flowSummary.finalBalances[1] | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenTests.Ledger_Section_ReplaysAgainstCheatLedger |
| ledger-reason | ledger-reason | ledger.chargeOnce.reason | FAIL_AS_EXPECTED | 1/96/1/0 | CheatGoldenBindingTests.Ledger_Snapshot_RestoresEveryEntryFieldInOrder |
| mark-version | mark-version | tileMarkState.addGroup.versionTrail[2] | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenTests.TileMarkState_Section_ReplaysAgainstTileMarkState |
| mark-expiry | mark-expiry | tileMarkState.expiry.groupsAfterCompletingRound3 | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenTests.TileMarkState_Section_ReplaysAgainstTileMarkState |
| mark-ordinal | mark-ordinal | tileMarkState.registerDealtRound.ordinalAfterFirst | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenTests.TileMarkState_Section_ReplaysAgainstTileMarkState |
| sleeve-window | input-window | sleeveBloomHandler[sbo-activate-allowed].goldenInputs.windowId | FAIL_AS_EXPECTED | 1/96/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-offer | input-offer | sleeveBloomHandler[sb-choose-second].goldenInputs.offerId | FAIL_AS_EXPECTED | 1/96/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-tile | input-tile | sleeveBloomHandler[sb-activate-allowed].goldenInputs.drawnTileId | FAIL_AS_EXPECTED | 1/96/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-pointer | input-pointer | sleeveBloomHandler[sb-activate-allowed].goldenInputs.wallPointer | FAIL_AS_EXPECTED | 1/96/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| movement-container | physical-container | tileMovement[mv-swap-hand-with-wall-direct].factsAfter[0].container | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| movement-seat | physical-seat | tileMovement[mv-swap-hand-with-wall-direct].factsAfter[1].seat | FAIL_AS_EXPECTED | 1/96/1/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| movement-index | physical-index | tileMovement[mv-swap-hand-with-wall-direct].factsAfter[0].index | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| movement-hand-tile | physical-tile | tileMovement[mv-swap-hand-with-wall-direct].handTileId | FAIL_AS_EXPECTED | 1/96/2/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| movement-pointer | physical-pointer | tileMovement[mv-reservation-delivery].wallPointerAfter | FAIL_AS_EXPECTED | 1/96/3/0 | CheatGoldenTests.TileMovement_ReservationDelivery_ReplaysThroughCsharpEntrypoint |
| movement-candidate-slot | physical-slot | tileMovement[mv-reservation-delivery-then-beam].candidateSlots[0] | FAIL_AS_EXPECTED | 1/96/1/0 | CheatGoldenTests.DeliveryThenBeam_BindsToGoldenCandidateEntities |
| beam-outcome | outcome | beamSwapHandler[bm-choose-keep].outcome | FAIL_AS_EXPECTED | 1/96/4/0 | CheatGoldenTests.Handler_Sections_CoverEveryRejectionReason |

覆盖类别：outcome、reason、ledger-amount/balance/flow/reason、mark-version/expiry/ordinal、input-window/offer/tile/pointer、physical-container/seat/index/tile/pointer/slot。没有只触发 header/hash；每项都在 `matrix/failedTests` 中记录真实失败 test 名。

### 7.1 fixture 身份（三者区分）

| 口径 | 值 |
|---|---|
| raw SHA-256 | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` |
| LF 归一化 SHA-256 | `3B33824F53E6E5808D2475CC212FCF3A914BBB5DF9A1A367BF2078A252F1F68D` |
| Git blob SHA-1 | `61307f255c8d1b02f1b64084768b6a500f3307b4` |

原始 fixture 未被修改；`git status` 不含 `tests/v5-golden/cheat-v1.json`。

## 8. 命令、native exit、计数与日志

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/Mahjong.sln -c Release --no-build
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -showLiveOutput -noLogo
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class '*CheatGolden*' -showLiveOutput -noLogo
powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-cheat-binding.ps1 -Repo D:/majiang-wt/n3-cheat-binding
```

| 验证 | 结果 | native exit | 原始日志 |
|---|---|---:|---|
| solution Release 非增量构建 | 0 warning / 0 error | 0 | `C:/Users/sncdc/AppData/Local/Temp/n3-cheat-solution-build-final.log`（SHA-256 `9CCA363C33A598260DED280D95F54CA51AE767E29E5FD0C2F337201505B2FFA2`） |
| solution 测试 | 1181 passed / 0 failed / 0 skipped（435+149+132+465） | 0 | `.../n3-cheat-solution-tests-final.log`（`BF80615E275F79F98E9B24B63EEB4017B5DEB1AC4A40435A3179E40912D5804E`） |
| Domain 全量 | 465 passed / 0 failed / 0 skipped / 0 Not Run | 0 | `.../n3-cheat-domain-full-final.log`（`C98F9DA51DF5C08297A1344DED58418982779B064180D3EE4FADFF53E52F460B`） |
| Cheat 定向（旧+新） | 96 passed / 0 failed / 0 skipped | 0 | 矩阵正控制 `positive-control.log`（`B7AB680F88CBE92E85225E57FC22A33A5AB14E858A9C998347ED4942B03CB8F0`） |
| 错值矩阵 | 22/22 FAIL_AS_EXPECTED；0 unexpected pass；0 tool failure | 0（矩阵本身；每个变异 exit1） | `.../n3-cheat-binding-probe-7acbace0dfd64cc384aa41d58acdd8d8`；console `817765AB237D3659A223D1336DB390E5371C1BDC26005CAC43E3BC9E4F76DC77` |
| 原 16 Fact | 16 passed | 0 | 见旧类日志/上面的 Cheat 定向 |

证据根（新独占）：`C:/Users/sncdc/AppData/Local/Temp/n3-cheat-binding-probe-7acbace0dfd64cc384aa41d58acdd8d8`。

| 证据文件 | SHA-256 |
|---|---|
| `summary.json` | `C92B492EF74FC669472D6AD9A50C3B651090EB011E10C316851EBCA32BD21897` |
| `matrix/matrix.csv` | `22D0B289C942AD64A7314187EE92AF1E23007865392D733B35DEA326EA342F17` |
| `evidence-sha256.csv` | `2DE2C1C0763D43238CF19CC585555CD6DCEA59B5117F5EBD490594BBCFE1F8D4` |
| `meta.json` | `151856B65738C7CF171DAD13F42CEFBC281498D1C93628FD1E87E5D67A25D13C` |

源码 SHA-256：

| 文件 | SHA-256 |
|---|---|
| `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenBindingTests.cs` | `63D70A201789A0B2759685550FEBA3CFDF28032D5B6E33E7718214C370B981AE` |
| `dotnet/tests/Mahjong.Domain.Tests/CheatGoldenTests.cs` | `96C07EC799801F8F86AF7468F6AF3D518ADAC3C30D8B3AFB2B53980835D97C56` |
| `tools/v5-golden/probe-cheat-binding.ps1` | `12FEF86805F57D31BCB20054E38CEFF288964964FD0E43B7099323E01A7621BA` |
| `tests/v5-golden/cheat-v1.json` | `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E` |

## 9. 状态分列

| 状态 | 内容 |
|---|---|
| **PASS** | 68/75 handler（Bound/Refined）真实调用；6/6 movement；ledger/state 深字段；22 项外部变异矩阵；Domain 465；solution 1181；Release 构建 0/0 |
| **FAIL** | 1/75 handler：`sb-activate-wall-empty-drift`（V4 拒绝 vs C# Allowed）；有 pin 测试与修复要求，未放宽 |
| **BLOCKED** | 4 个 actor/decision tile-mark case；2 个 beam dead-wall 过滤态不可规范表示；共 6，均有依赖/验收断言 |
| **NOT_RUN** | PG/WSS、Unity/IL2CPP、Android/真机、真实网络、V4 Gradle 全量、远端 CI；没有为测试绑定变更重跑平台重型全量 |

## 10. 剩余最小任务

1. **F-01 决策**：caller 选“接受 C# 重新搜索并更新 V4/golden 语义”或“加冻结候选输入/actor 预检”；后者需生产改动与同 case 负例。
2. **F-02 入口**：若产品要求 dead-wall 空集/仅 dora 拒绝，增加 handler-level `deadWallTileIdsInDeadWall` 或等价候选抑制面；否则把这两个 partial case 的产品语义写回 golden/manifest，不能伪装为 Domain 行为已验证。
3. **F-03 接线**：N-1b/actor 完成 `DecisionClock`/round identity 预检后，把 4 个 case 从 BLOCKED 转为真实调用；目前不向项目偷加时钟/决策依赖。
4. **F-04/F-05 文档/导出**：下次合法重导出修正 note 与 M3 facts 槽位；当前 fixture 属于用户，未改。
5. **完整 N3/G1**：另审；本组件不关闭 G1，不改变 V4 唯一生产基线。

## 11. 完成边界

本轮 PASS 仅为上述 cheat-v1 消费者绑定与本地 solution 回归。75 个 handler 的分母保留：68 通过、1 真实 FAIL、6 BLOCKED；6 个 movement 通过，但 drawn-backfill 的 V4 洞表示差异已显式映射。完整 N3、G1、G5、平台矩阵和远端 PR/CI 均未由本任务关闭。
