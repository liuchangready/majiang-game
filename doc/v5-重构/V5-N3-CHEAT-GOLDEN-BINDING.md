# V5 N3 cheat-v1 字段行为绑定（G1 剩余消费者测试任务，第 3 轮返修）

日期：2026-10-25。工作树：`D:/majiang-wt/n3-cheat-binding`，分支 `wt/v5-n3-cheat-binding`，冻结基线 `900a043cfa276c0e172672b8b7cc7d6d4ebc4767`。第 3 轮返修提交见本工作树 `git log -1`。

本报告只覆盖 **cheat-v1 测试消费者字段绑定**。它不是 N3/G1 通过：G1 仍未批准，V4 仍是唯一生产架构，G5 未完成，`complete.flag` 不存在。未修改生产 C#、V4 exporter/fixture、catalog/manifest、统一 verifier、项目/solution 或用户环境；仍仅原四文件。

## 0. 第 3 轮结论

| 对象 | 结果 | 说明 |
|---|---|---|
| 75 个 handler case | **67 PASS / 0 FAIL / 8 BLOCKED** | 67 个真实调用；8 个准确 BLOCKED，未计 PASS |
| 6 个 tileMovement case | **6 PASS** | 5 个精确重建 + M5 全 136 placements；M3 stale index 显式映射 |
| ledger / TileMarkState 深字段 | **PASS** | initialPoints 四座、entry 全字段、拒绝 outcome/exception、version/异常、到期、零副作用 |
| 原 16 Fact | **16 PASS** | drawn-backfill、disabled、foreign-hand 已纠正 |
| 外部单字段错值矩阵 | **46/46 FAIL_AS_EXPECTED** | 正控制 94/0/0/0/0 exit0；每项 native1 且真实 Failed>0 |
| stale/expired 断言审计 | **15/15 PASS**（8 stale + 7 expired） | 正控制与 6+6 个恶意后态/事件均被 helper 拒绝 |
| 7 项 enum 严格性探针 | **7/7 native1 / Total94 / Failed1** | 数字、组合、重复 token 不再归一化 |
| 296 叶扫描 | **294 native1；2 项合法等价** | `factsBefore.tileId 8→9`、`inputKanCount 0→1` |
| 8 项 strict probes | **8/8 native1 / Total94 / Failed1** | keep/seat/version/fact schema/requestException |
| **readiness 探针** | **BLOCKED_INPUT：native1 / Total1 / Failed1** | map-only 合法候选控制为 Allowed；明确缺 frozen
extWallTileId` 输入，不称产品 FAIL |
| Domain 全量 | **463 passed / 0 failed / 0 skipped / 0 Not Run** | native exit 0 |
| Release solution 构建 | **0 warning / 0 error** | native exit 0 |
| solution 测试 | **1179 passed / 0 failed / 0 skipped** | 435 AI + 149 Contracts + 132 Client + 463 Domain，native exit 0 |
| PG/WSS/Unity/IL2CPP/Gradle/远端 CI | **NOT_RUN** | 无生产变更，未重跑平台重型全量 |

`MATRIX=PASS`（46/46 变异真实失败）；`READINESS=BLOCKED`（8 个 BLOCKED + F-01 缺输入能力门 native1）。默认脚本 readiness 非零；`-MatrixOnly` 为 `MATRIX_ONLY_PASS`/`readinessStatus=NOT_RUN`。标准测试全绿**不**代表组件闭合。

## 1. 第 3 轮 4 组缺陷修复映射

### D-R2-01（MAJOR）stale/expired 后态与事件未完整校验

- **位置**：`AssertStaleWithState`、`sb-choose-stale-index`、`sbo-choose-expired`。
- **实际**：旧 helper 只比 ledger/marks/map；BeamSwap.UsedOnTurn、SleeveBloom.UsedOnTurn、他席 window、seat1 reservation 丢失、TileMarks.Version+1、错误 stale 事件均能通过；expired 无入口前 snapshot。
- **修复**：`AssertStaleWithState` 现在接收调用前 Snapshot、输入 state、**显式 expectedState** 和 **expectedEvents**；逐字段比较整个 expected state（含 fingerprint、entries、marks、sleeve offer/reservation/window/usedOnTurn、beam offer/usedOnTurn、136 locations）和完整事件序列。stale 只允许清除对应 offer；stale-index 的 seat1 `protect-32` reservation 保留在 expected state 中。新增 `AssertExpiredWithState`：expected state 由 `TryResolveOffTurnOffer(First)` 构造，只允许 offer→FIRST reservation，事件精确 `SleeveBloomResolved(2,offerId,First)`；入口前捕获 before。
- **验收**：独立 TEMP 审计 `ReviewStaleAssertions` 8/8 通过（positive、ledger-control、beam-used、sleeve-used、sleeve-window、reservation-drop、mark-version、wrong-event）；`ReviewExpiredAssertions` 7/7 通过（positive + 6 恶意变化）。恶意后态均抛 `TargetInvocationException`，正控制通过。

### D-R2-02（MAJOR）GoldenEnum 接受数字/组合 token

- **位置**：`GoldenEnum<T>`；`sb-choose-first.goldenInputs.keep`。
- **实际**：`Enum.TryParse("0", true)` 与 `"FIRST, FIRST"` 可归一化成 FIRST，随后 `Enum.IsDefined` 通过；整套实际 Allowed。
- **修复**：先 `Enum.GetNames` 做**原始 token 名字**（大小写不敏感）校验，找不到即断言失败；只对 canonical name 调 `Enum.Parse`。禁止数字、逗号组合、重复、空白。所有 `goldenInputs` 的 keep/drawKind/phase 都走同一解析。
- **验收**：调用者 `probe-enum.ps1` 7/7 native1/Total94/Failed1；正式矩阵新增 4 项数字/组合 token（keep、drawKind、phase）均 FAIL_AS_EXPECTED；canonical FIRST/SECOND/NORMAL/RINSHAN/DISCARD 正控制仍通过。

### D-R2-03（MAJOR）readiness 把缺输入投影当产品 FAIL

- **位置**：`probe-cheat-binding.ps1` 生成的 TEMP readiness 测试；报告 F-01/§9。
- **实际**：旧 readiness 对 map-only 调用断言 V4 Rejected，消息写成 `FAIL_CONFORMANCE`/“生产修正后应转绿”；但完整 V4 输入还含独立
extWallTileId=32/nextWallTileInWall=false`，C# API 无此参，不能据此判定生产违约。
- **修复**：readiness 改为能力门：
  - 先断言 map-only 调用 **Allowed**（合法其它墙候选控制）；
  - 再反射检查 `ActivateSleeveBloom` 是否有 frozen nextWall/frozen 参数；没有则失败，消息 `BLOCKED_INPUT: ...`；若未来参数出现，则 `Assert.Fail("BLOCKED_INPUT: ... update to same-input conformance")`，直到用真正同输入 Rejected 断言替换。
  - summary 输出 `readinessProbe.status=BLOCKED_INPUT`、`readinessStatus=BLOCKED`，不称产品 FAIL；`readinessCauses` 含 `F-01:missing-frozen-next-wall-input`。
- **验收**：默认 readiness native1/Total1/Failed1，消息清晰 `BLOCKED_INPUT`；map-only 合法候选仍 Allowed；67/75+8 BLOCKED 分母不变；未改生产/fixture。

### D-R2-04（MINOR）readiness 工具失败优先级

- **位置**：summary `$readinessStatus` 优先级。
- **实际**：故意把生成 readiness 项目的 TargetFramework 改坏，`readinessProbe.status=TOOL_FAILURE`，但 blocked.Count>0 先命中，顶层仍 `BLOCKED`/`MATRIX_PASS_READINESS_BLOCKED`。
- **修复**：工具/发现/异常失败优先于 blocked：`TOOL_FAILURE`/`TOOL_OR_UNEXPECTED_FAILURE` → `readinessStatus=FAIL`、`status=FAIL`；正常缺输入 → BLOCKED；MatrixOnly → `NOT_RUN`/`MATRIX_ONLY_PASS`。native build/test exit 原样保留。
- **验收**：正常默认 = matrix PASS/readiness BLOCKED/exit1；故意 readiness 编译失败（NETSDK1013）= readiness FAIL/status FAIL/exit1；`-MatrixOnly` = matrix PASS/readiness NOT_RUN/status MATRIX_ONLY_PASS/exit0。

## 2. 输入重建与严格 schema

- V4 exporter：`V5CheatGoldenExporterTest.kt`（ledger :111-187；tileMarkState :193-254；tileMark handler :359-427；sleeve :432-765；beam :767-976；tileMovement :1043-1378）。
- V4 构造器：`TestTableBuilder.kt:8-76`。
- C# 入口：`CheatSubmission.cs:113,226,327,447,539,664,756,914`；`TilePositionMap.cs:637,686,1203,1258,1321,1375`。
- N1b 已接受：`TableActor.cs:223 SubmitCheatAsync`、`:1639 ProcessCheatCommandCoreAsync`、`:1662/:1893 ValidateAuthorityIdentity`、`:1689 RoundCheatTransition.Prepare`；下一 actor 测试 `TableRuntime.Tests/N1bCheatExecutionTests.cs`。
- `AssertKeys` 精确 key 集；`GoldenString/Int/Long/Bool/Enum/NullableInt` 拒绝缺失/错型/未知 token；`AssertFact` 区分 `FactsAfter` 与 `InputPlacements` 的 nullable schema。

## 3. 75 个 handler 的完整映射

`PASS`/`PASS-refined` 行由 `[Theory]` 真实调用；`BLOCKED` 行由 manifest 固定并单列。表内 `Allowed / —` 的 reason 为 JSON null。

| Case | JSONpath | 真实入口 | V4 outcome / reason | 状态 | C# code | 测试 |
|---|---|---|---|---|---|---|
| tm-allowed | $.tileMarkHandler[?(@.caseId=='tm-allowed')] | CheatSubmission.ApplyTileMark | Allowed / — | PASS | None | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-not-your-turn | $.tileMarkHandler[?(@.caseId=='tm-not-your-turn')] | CheatSubmission.ApplyTileMark | Rejected / NOT_YOUR_TURN | PASS | NotYourTurn | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-wrong-phase-preparation | $.tileMarkHandler[?(@.caseId=='tm-wrong-phase-preparation')] | CheatSubmission.ApplyTileMark | Rejected / WRONG_PHASE | PASS | WrongPhase | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-wrong-phase-draw | $.tileMarkHandler[?(@.caseId=='tm-wrong-phase-draw')] | CheatSubmission.ApplyTileMark | Rejected / WRONG_PHASE | PASS | WrongPhase | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-wrong-phase-round-end | $.tileMarkHandler[?(@.caseId=='tm-wrong-phase-round-end')] | RoundStateMachine terminal guard + actor decision binding | Rejected / DECISION_MISMATCH | BLOCKED-actor | — | BindingManifest row |
| tm-response-window | $.tileMarkHandler[?(@.caseId=='tm-response-window')] | CheatSubmission.ApplyTileMark | Rejected / WRONG_PHASE | PASS-refined | ResponseWindowOpen | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-riichi-locked | $.tileMarkHandler[?(@.caseId=='tm-riichi-locked')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_RIICHI_LOCKED | PASS | RiichiLocked | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-invalid-selection-three | $.tileMarkHandler[?(@.caseId=='tm-invalid-selection-three')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_INVALID_SELECTION | PASS | InvalidSelection | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-invalid-selection-duplicate | $.tileMarkHandler[?(@.caseId=='tm-invalid-selection-duplicate')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_INVALID_SELECTION | PASS | InvalidSelection | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-tile-not-owned | $.tileMarkHandler[?(@.caseId=='tm-tile-not-owned')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_TILE_NOT_OWNED | PASS | TileNotOwned | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-already-marked | $.tileMarkHandler[?(@.caseId=='tm-already-marked')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_ALREADY_MARKED | PASS | AlreadyMarked | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-capacity-groups | $.tileMarkHandler[?(@.caseId=='tm-capacity-groups')] | CheatSubmission.ApplyTileMark | Rejected / TILE_MARK_CAPACITY_EXCEEDED | PASS | MarkCapacityExceeded | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-capacity-tiles | $.tileMarkHandler[?(@.caseId=='tm-capacity-tiles')] | TileMarkGroup exactly-four invariant / tile-limit guard | Rejected / TILE_MARK_CAPACITY_EXCEEDED | BLOCKED-input | — | BindingManifest row |
| tm-insufficient-funds | $.tileMarkHandler[?(@.caseId=='tm-insufficient-funds')] | CheatSubmission.ApplyTileMark | Rejected / INSUFFICIENT_FUNDS | PASS | InsufficientFunds | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-sleeve-pending | $.tileMarkHandler[?(@.caseId=='tm-sleeve-pending')] | CheatSubmission.ApplyTileMark | Rejected / SLEEVE_BLOOM_CHOICE_PENDING | PASS-refined | OtherOfferPending | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-offturn-sleeve-pending | $.tileMarkHandler[?(@.caseId=='tm-offturn-sleeve-pending')] | CheatSubmission.ApplyTileMark | Rejected / SLEEVE_BLOOM_CHOICE_PENDING | PASS-refined | OtherOfferPending | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-beam-pending | $.tileMarkHandler[?(@.caseId=='tm-beam-pending')] | CheatSubmission.ApplyTileMark | Rejected / BEAM_SWAP_CHOICE_PENDING | PASS-refined | OtherOfferPending | TileMark_BoundCase_ReplaysThroughApplyTileMark |
| tm-no-active-decision | $.tileMarkHandler[?(@.caseId=='tm-no-active-decision')] | DecisionClock/actor decision admission | Rejected / NO_ACTIVE_DECISION | BLOCKED-actor | — | BindingManifest row |
| tm-deadline-expired | $.tileMarkHandler[?(@.caseId=='tm-deadline-expired')] | DecisionTiming.IsExpired(now == deadline) | Rejected / DEADLINE_EXPIRED | BLOCKED-actor | — | BindingManifest row |
| tm-decision-mismatch | $.tileMarkHandler[?(@.caseId=='tm-decision-mismatch')] | actor round/decision identity binding | Rejected / DECISION_MISMATCH | BLOCKED-actor | — | BindingManifest row |
| sb-activate-allowed | $.sleeveBloomHandler[?(@.caseId=='sb-activate-allowed')] | CheatSubmission.ActivateSleeveBloom | Allowed / — | PASS | None | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-dealer-extra | $.sleeveBloomHandler[?(@.caseId=='sb-activate-dealer-extra')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED | PASS | DealExtraDraw | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-already-used | $.sleeveBloomHandler[?(@.caseId=='sb-activate-already-used')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW | PASS | AlreadyUsedThisTurn | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-beam-pending | $.sleeveBloomHandler[?(@.caseId=='sb-activate-beam-pending')] | CheatSubmission.ActivateSleeveBloom | Rejected / BEAM_SWAP_ALREADY_PENDING | PASS-refined | OtherOfferPending | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-sleeve-pending | $.sleeveBloomHandler[?(@.caseId=='sb-activate-sleeve-pending')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_ALREADY_PENDING | PASS | OfferAlreadyPending | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-wall-empty | $.sleeveBloomHandler[?(@.caseId=='sb-activate-wall-empty')] | CheatSubmission.ActivateSleeveBloom | Rejected / SLEEVE_BLOOM_WALL_EMPTY | PASS-refined | NoWallCandidate | SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sb-activate-wall-empty-drift | $.sleeveBloomHandler[?(@.caseId=='sb-activate-wall-empty-drift')] | CheatSubmission.ActivateSleeveBloom + frozen nextWallTileId input | Rejected / SLEEVE_BLOOM_WALL_EMPTY | BLOCKED-input | — | readiness probe / manifest |
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
| bm-activate-deadwall-empty | $.beamSwapHandler[?(@.caseId=='bm-activate-deadwall-empty')] | physical dead-wall map with no eligible candidate | Rejected / BEAM_SWAP_NO_CANDIDATES | BLOCKED-input | — | BeamDeadWallFilterStates / manifest |
| bm-activate-deadwall-dora-only | $.beamSwapHandler[?(@.caseId=='bm-activate-deadwall-dora-only')] | physical dead-wall map with only the dora indicator located in the dead wall | Rejected / BEAM_SWAP_NO_CANDIDATES | BLOCKED-input | — | BeamDeadWallFilterStates / manifest |
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

### 3.1 关键修正

- `tm-capacity-tiles`：V4 源 `groups=2, marked=(16..27)=12, selected=0/4/8/12`；C# 四张组不变量使 2 组不可能 12 张，3 组先触发 group limit。该 case 为 **BLOCKED-input**，不再计 PASS；`tm-capacity-groups` 仍真实绑定 group limit。
- disabled 输入使用真实 `configuredCost=5`，不换 `featureEnabled=false`。
- Beam activation 用源默认 `drawnTileId=16`、`replacement=0`；choice 用 `52/108/135`；stale-index 保留候选 53，用 seat1 reservation 使 first-available=54。
- `requestException`、M4 dora/wall 拒绝异常字符串精确校验。

## 4. 6 个物理移动

| Case | 真实入口/断言 | 结果 |
|---|---|---|
| `mv-swap-hand-with-wall-direct` | `SwapHandWithWall`，wall0→Wall53；factsBefore/After、指针、136 | PASS |
| `mv-swap-drawn-with-wall-backfill` | `DrawFromWall` → `ActivateSleeveBloom` → `ChooseSleeveBloom(SECOND)`；drawn 回 Wall54、墙牌进手/新 drawn evidence | PASS |
| `mv-reservation-delivery` | `DeliverSleeveBloomReservation`；removeAt、wallEnd-1、指针不动 | PASS |
| `mv-reservation-delivery-then-beam` | 136 placements、8 候选实体+槽、beamGot/nextDraw、factsAfter | PASS |
| `mv-rinshan-and-drawn-swap` | `DrawReplacement` + `SwapDrawnWithNextRinshan`；deadWall1=108、109 进手、135 replacement；V4 stale index=0 显式消费并映射权威 slot1 | PASS |
| `mv-swap-hand-with-dead-wall` | `DrawReplacement` + `SwapHandWithDeadWall(1)` + dora/WallSlot 拒绝 + `SwapHandWithReplacement(0)` | PASS |

drawn-backfill 的 V4 洞表示：C# Wall53 为空、29 成为新 drawn evidence；实体/容器/座位/槽位/后续摸牌一致，不冒充逐字相同。

## 5. 第 2 轮已保留修复

D-R1-01..05 的有效部分继续保留：不再反向 pin；ledger/state 全字段；7 组 `goldenInputs` 严格 key/type；fact nullable schema；M5 delivered/pointer/kanCount；M3 index 映射；M4 metadata；N1b 既有链路；拒绝前 Snapshot。第 3 轮新增 D-R2-01..04 后，定向 94、Domain 463、solution 1179 再次实跑通过。

## 6. 第 3 轮外部矩阵（46 项）

脚本：`tools/v5-golden/probe-cheat-binding.ps1`。原始 fixture 单字段复制到独占 TEMP；`try/finally` 恢复 `V5_CHEAT_GOLDEN_PATH`；写副本去 BOM；正控制 94/0/0/0/0 exit0。每项要求 native1、Total=94、Errors=0、Skipped=0、NotRun=0、真实 Failed>0，预期 test 名出现。

| ID | 类别 | 字段 | 结果 | exit / Total / Failed / Skipped | 首个失败测试 |
|---|---|---|---|---|---|
| outcome-tilemark | outcome | tileMarkHandler[tm-allowed].outcome | FAIL_AS_EXPECTED | 1/94/3/0 | CheatGoldenTests.Handler_Sections_CoverEveryRejectionReason |
| reason-tilemark | reason | tileMarkHandler[tm-not-your-turn].reason | FAIL_AS_EXPECTED | 1/94/3/0 | CheatGoldenTests.Handler_TileMark_RepresentativeCases_ReplayAgainstCheatSubmission |
| reason-beam | reason | beamSwapHandler[bm-activate-riichi].reason | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenBindingTests.BindingManifest_PreservesTheFullDenominator |
| ledger-amount | ledger-amount | ledger.chargeOnce.amount | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenTests.Ledger_Section_ReplaysAgainstCheatLedger |
| ledger-balance | ledger-balance | ledger.chargeOnce.balancesAfter[0] | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenTests.Ledger_Section_ReplaysAgainstCheatLedger |
| ledger-flow | ledger-flow | ledger.flowSummary.seat1NetDelta | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenTests.Ledger_Section_ReplaysAgainstCheatLedger |
| ledger-snapshot-balance | ledger-balance | ledger.flowSummary.finalBalances[1] | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenTests.Ledger_Section_ReplaysAgainstCheatLedger |
| ledger-reason | ledger-reason | ledger.chargeOnce.reason | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Ledger_Snapshot_RestoresEveryEntryFieldInOrder |
| mark-version | mark-version | tileMarkState.addGroup.versionTrail[2] | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenTests.TileMarkState_Section_ReplaysAgainstTileMarkState |
| mark-expiry | mark-expiry | tileMarkState.expiry.groupsAfterCompletingRound3 | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenTests.TileMarkState_Section_ReplaysAgainstTileMarkState |
| mark-ordinal | mark-ordinal | tileMarkState.registerDealtRound.ordinalAfterFirst | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenTests.TileMarkState_Section_ReplaysAgainstTileMarkState |
| sleeve-window | input-window | sleeveBloomHandler[sbo-activate-allowed].goldenInputs.windowId | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-offer | input-offer | sleeveBloomHandler[sb-choose-second].goldenInputs.offerId | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-tile | input-tile | sleeveBloomHandler[sb-activate-allowed].goldenInputs.drawnTileId | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-pointer | input-pointer | sleeveBloomHandler[sb-activate-allowed].goldenInputs.wallPointer | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| movement-container | physical-container | tileMovement[mv-swap-hand-with-wall-direct].factsAfter[0].container | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| movement-seat | physical-seat | tileMovement[mv-swap-hand-with-wall-direct].factsAfter[1].seat | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| movement-index | physical-index | tileMovement[mv-swap-hand-with-wall-direct].factsAfter[0].index | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| movement-hand-tile | physical-tile | tileMovement[mv-swap-hand-with-wall-direct].handTileId | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| movement-pointer | physical-pointer | tileMovement[mv-reservation-delivery].wallPointerAfter | FAIL_AS_EXPECTED | 1/94/3/0 | CheatGoldenTests.TileMovement_ReservationDelivery_ReplaysThroughCsharpEntrypoint |
| movement-candidate-slot | physical-slot | tileMovement[mv-reservation-delivery-then-beam].candidateSlots[0] | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenTests.DeliveryThenBeam_BindsToGoldenCandidateEntities |
| beam-outcome | outcome | beamSwapHandler[bm-choose-keep].outcome | FAIL_AS_EXPECTED | 1/94/4/0 | CheatGoldenTests.Handler_Sections_CoverEveryRejectionReason |
| ledger-initial-points | ledger-balance | ledger.initialPoints[1] | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Ledger_Snapshot_RestoresEveryEntryFieldInOrder |
| ledger-entry-id | ledger-amount | ledger.chargeOnce.entryId | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Ledger_Snapshot_RestoresEveryEntryFieldInOrder |
| ledger-reject-exception | ledger-reason | ledger.insufficientFunds.exception | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Ledger_Snapshot_RestoresEveryEntryFieldInOrder |
| mark-register-version | mark-version | tileMarkState.registerDealtRound.versionAfterFirstRegister | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.TileMarkState_BindsEveryExportedFieldAndZeroSideEffect |
| mark-group-exception | mark-version | tileMarkState.addGroup.sameIdDifferentContentException | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.TileMarkState_BindsEveryExportedFieldAndZeroSideEffect |
| sleeve-seat | input-window | sleeveBloomHandler[sb-activate-allowed].goldenInputs.seat | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-keep-unknown | input-offer | sleeveBloomHandler[sb-choose-first].goldenInputs.keep | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-keep-missing | input-offer | sleeveBloomHandler[sb-choose-first].goldenInputs.keep | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-seat-missing | input-window | sleeveBloomHandler[sb-choose-first].goldenInputs.seat | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-draw-kind | input-tile | sleeveBloomHandler[sb-choose-second].goldenInputs.drawKind | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-null-typed | input-tile | sleeveBloomHandler[sb-activate-allowed].goldenInputs.nextDeadWallTileId | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-cost | input-pointer | sleeveBloomHandler[sb-choose-first].goldenInputs.cost | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| facts-wall-seat | physical-seat | tileMovement[mv-swap-hand-with-wall-direct].factsAfter[0].seat | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| facts-hand-index | physical-index | tileMovement[mv-swap-hand-with-wall-direct].factsAfter[1].index | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Movement_SwapHandWithWall_BindsFactsBeforeAndAfter |
| m5-delivered | physical-tile | tileMovement[mv-reservation-delivery-then-beam].deliveredTileId | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenTests.DeliveryThenBeam_BindsToGoldenCandidateEntities |
| m5-delivery-pointer | physical-pointer | tileMovement[mv-reservation-delivery-then-beam].wallPointerBeforeDelivery | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenTests.DeliveryThenBeam_BindsToGoldenCandidateEntities |
| m3-stale-index | physical-index | tileMovement[mv-rinshan-and-drawn-swap].factsAfter[0].index | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Movement_RinshanAndDrawnSwap_BindsDeadWallSlotAndReplacement |
| m4-dora-exception | metadata | tileMovement[mv-swap-hand-with-dead-wall].doraRefusalException | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Movement_DeadWallSwap_BindsCompositeFactsAndRefusals |
| m4-wall-exception | metadata | tileMovement[mv-swap-hand-with-dead-wall].wallSlotRefusalException | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.Movement_DeadWallSwap_BindsCompositeFactsAndRefusals |
| request-exception | metadata | beamSwapHandler[bm-keep-original-args-forbidden-at-request].requestException | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.BeamSwap_BoundCase_ReplaysThroughDomainEntry |
| sleeve-keep-numeric-token | input-offer | sleeveBloomHandler[sb-choose-first].goldenInputs.keep | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-keep-combined-token | input-offer | sleeveBloomHandler[sb-choose-first].goldenInputs.keep | FAIL_AS_EXPECTED | 1/94/1/0 | CheatGoldenBindingTests.SleeveBloom_BoundCase_ReplaysThroughDomainEntry |
| sleeve-draw-numeric-token | input-tile | sleeveBloomHandler[sb-activate-allowed].goldenInputs.drawKind | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenTests.SleeveBloomSecondChoice_BindsToGoldenInputs |
| sleeve-phase-combined-token | input-window | sleeveBloomHandler[sbo-activate-allowed].goldenInputs.phase | FAIL_AS_EXPECTED | 1/94/2/0 | CheatGoldenTests.OffTurnReservationPath_BindsToGoldenInputsAndDelivers |

### 6.1 296 叶与 7 enum

- 调用者 `probe-leaves-v2.ps1` 硬编码 Total=96；移除两个不可绑定 case 后行为套件 Total=94，TEMP 副本仅改该期望，原证据未改。
- 296 项：**294 native1**，2 项合法等价：`factsBefore.tileId 8→9`（仍是 seat0 手牌，事实保持真）、`inputKanCount 0→1`（进入 `FromPlacements` 且 `map.KanCount` 被断言；物理行为不依赖）。
- enum：调用者 `probe-enum.ps1` 7/7 native1/Total94/Failed1；数字、组合、重复 token 均被 GoldenEnum 名字白名单拒绝。

## 7. 命令、结果、证据

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/Mahjong.sln -c Release --no-build
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -showLiveOutput -noLogo
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll -class '*CheatGolden*' -showLiveOutput -noLogo
powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-cheat-binding.ps1 -Repo D:/majiang-wt/n3-cheat-binding
# 纯矩阵 exit0：
powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-cheat-binding.ps1 -Repo D:/majiang-wt/n3-cheat-binding -MatrixOnly
```

| 验证 | 结果 | native exit | 日志 |
|---|---|---:|---|
| solution 构建 | 0 warning / 0 error | 0 | `C:/Users/sncdc/AppData/Local/Temp/n3-cheat-r3-solution-build.log`（`4494FBC6EE4FA65DE7425F88D49640483A4FF8805A27C16B80C2DB051526B141`） |
| solution 测试 | 1179 passed / 0 failed / 0 skipped（435+149+132+463） | 0 | `.../n3-cheat-r3-solution-tests.log`（`3A5A02E23FB0875A83E0FD966ADA115EDB5B0B118D9C49BE8EA3C5B60E0CD188`） |
| Domain 全量 | 463 passed / 0 failed / 0 skipped / 0 Not Run | 0 | `.../n3-cheat-r3-domain-full.log`（`0DA3D517740FC2EABB8F8C559895DCED4C01CD8FFF76908657426AFF087F9D2D`） |
| Cheat 定向 | 94 passed / 0 failed / 0 skipped | 0 | 正控制 `positive-control.log`（`8FC07333CA734A42E5E1AC2B1B3A55EB4670D45FF6C43DD4C14A97CFC1933B8B`） |
| 46 项矩阵 | 46/46 FAIL_AS_EXPECTED；0 unexpected/tool/wrong | matrix 0；每变异 1 | 最终根 `matrix/matrix.csv`（`7F40E2FE2A459EA9650F782FC9A1822B064E73BD480A31F71728E1116C8F9341`） |
| readiness | **BLOCKED_INPUT：Total1/Failed1/Errors0/Skip0/NotRun0** | 1 | `readiness/readiness.log`（`0F7A18289471B95F29AA40C72445E6CD73A8E67389B5567F64313E376D8E5A66`） |
| 默认脚本整体 | MATRIX=PASS，READINESS=BLOCKED，status=MATRIX_PASS_READINESS_BLOCKED | 1 | `summary.json`（`ABFAF94C803EC84293A440B31E38638125670D767EBDC2FBC597120B92DC8E82`） |
| MatrixOnly | MATRIX=PASS，READINESS=NOT_RUN，status=MATRIX_ONLY_PASS | 0 |
3-cheat-r3-matrixonly-8add5b805abe46d7bb7ed6d8c7096e09/summary.json`（`AD926B406C2042C906C8236948D8EFBB3F709E28A541FE1E4B6CB643F114FBB6`） |
| stale audit | 8/8 passed；6 恶意后态/事件被拒 | 0 |
3-cheat-r3-stale-fa0c15bcb3b94fe3b149c46b8fcf5b9a/review.log`（`298772BA0BBB37E0CBC07E4804F19F26C8D6863CC91E7C1FB3B8B79370B2E9FA`） |
| expired audit | 7/7 passed；6 恶意后态/事件被拒 | 0 | `.../review-expired.log`（`C680BCF62353BEF19D386D8B61CB624F4E4F1F00C6570948F171C30C1E455A7D`） |
| enum probe | 7/7 native1 / Total94 / Failed1 | 每项1 |
3-cheat-r3-enum-db1b25bc5f684cf9a74843a689a20419/results.csv`（`9978EFBBD86B7B41D6DDBD30129E01394064501460A2B1CF341618AE0B733528`） |
| 工具失败审计 | readinessProbe=TOOL_FAILURE，readinessStatus=FAIL，status=FAIL | 1 |
3-cheat-r3-toolfail-627ca250d1aa4975a4e947f5b7043a3e/summary.json`（`51341701DF20D1284817742188C1517E5ACEA35EB381BB8CBEE470058721FB58`） |

最终默认证据根：`C:/Users/sncdc/AppData/Local/Temp/n3-cheat-r3-final-298d30bb204c4fa3ae6a840c00da30bf`；`evidence-sha256.csv` `1E40D50BC058E5C153C7BD049FF4C17B67080125CF436256298C5C88609C720D`。

源码 SHA-256：

| 文件 | SHA-256 |
|---|---|
| `CheatGoldenBindingTests.cs` | `786130E13CC4AE6248DF0207B0F873AF733EF1FFCCBF23C6FB24E30B517DA9D0` |
| `CheatGoldenTests.cs` | `3B869A5491FCB029640B95364A61A3739EB0D6B45B80B90273151D00E68F88E5` |
| `probe-cheat-binding.ps1` | `AD41DF1F6BF46143F178EA15E4C9BF43491E92FB8457B7A32D73F65C143A3DB6` |
| `tests/v5-golden/cheat-v1.json` | raw `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`；LF `3B33824F53E6E5808D2475CC212FCF3A914BBB5DF9A1A367BF2078A252F1F68D`；Git blob `61307f255c8d1b02f1b64084768b6a500f3307b4` |

## 8. 未闭合项（不冒充 PASS）

### F-01（BLOCKED_INPUT）sleeve 冻结 next-wall 输入缺失

- **位置**：`CheatSubmission.cs:278-286`；`sb-activate-wall-empty-drift`。
- **复现**：默认脚本 readiness 段；`readiness.log` 消息 `BLOCKED_INPUT: ActivateSleeveBloom has no frozen nextWallTileId/nextWallTileInWall input...`。
- **实际 vs 期望**：map-only 调用 Allowed 是合法其它候选控制；V4 完整输入还需独立
extWallTileId=32/nextWallTileInWall=false`，C# 无此参。**不得**据此断言产品违约或要求拒绝其它合法墙牌。
- **修复**：caller 决策：接受 C# 重搜索并更新 golden 语义，或增加 frozen 输入，或 actor 预检；生产/fixture 未授权修改。
- **验收**：默认 readiness 仍 native 非零/Total>0/清晰 BLOCKED_INPUT；合法候选 Allowed 控制通过；真正 frozen 输入出现后替换为同输入 Rejected 断言；分母 67/75、8 BLOCKED 保留。

### F-02（BLOCKED_INPUT）beam dead-wall 空集/仅 dora

- `bm-activate-deadwall-empty`/`-dora-only`；`TilePositionMap.cs:1587-1593` 要求有效王牌区=14；过滤态不是规范 map。需 handler-level `deadWallTileIdsInDeadWall` 输入或候选抑制面；不得搬空王牌区。dora 排除另有正/负测试。

### F-03（BLOCKED_ACTOR）4 个 tile-mark 决策/截止时间

- N1b 已接受；`TableActor.SubmitCheatAsync → ValidateAuthorityIdentity → RoundCheatTransition.Prepare` 存在；`CheatTurnContext` 无 decisionId/deadline/round-ended，Domain.Tests 不引用 TableRuntime。下一测试 `TableRuntime.Tests/N1bCheatExecutionTests.cs`；未声称完成。

### F-04/F-05/F-06/F-07

- F-04：`tm-capacity-tiles` note 8 vs exporter 12，下次合法导出修正，不改 fixture。
- F-05：M3 108 的 V4 stale index=0 是重复列表 artifact；测试显式消费并映射权威 slot1，不冒充 C# 槽位。
- F-06：`tm-capacity-tiles` 2×6 输入不可由四张组构造；计入 BLOCKED，需 state-level 输入或新可达 case。
- F-07：296 叶中 2 项合法等价（factsBefore tile 8→9、inputKanCount 0→1），不是漏绑。

## 9. 状态分列

| 状态 | 内容 |
|---|---|
| **PASS** | 67/75 handler；6/6 movement；ledger/state；46/46 矩阵；294/296 叶；8/8 strict；15/15 stale/expired audit；7/7 enum；Domain 463；solution 1179；build 0/0 |
| **FAIL** | 0 个产品行为 FAIL；readiness 是 **BLOCKED_INPUT native1**，不是产品违约 |
| **BLOCKED** | 8 个 handler：4 actor + F-01 + 2 beam 过滤态 + F-06 capacity；分母保留 |
| **NOT_RUN** | PG/WSS、Unity/IL2CPP、Android/真机、真实网络、V4 Gradle 全量、TableRuntime 重跑、远端 CI；无生产变更 |

## 10. 完成边界

67/75 行为绑定通过，但 8 个 BLOCKED 与 F-01 缺输入能力门仍未闭合；依赖已列。G1/N3/G5 不通过，V4 仍唯一生产基线。没有 push/PR/CI/合并兄弟代码；主 checkout 用户未提交文件与 GLM 在途工作树未触碰。
