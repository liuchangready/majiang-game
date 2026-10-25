# V5 N3 cheat-v1 字段行为绑定（G1 剩余消费者测试任务，第 2 轮返修）

日期：2026-10-25。工作树：`D:/majiang-wt/n3-cheat-binding`，分支 `wt/v5-n3-cheat-binding`，冻结基线 `900a043cfa276c0e172672b8b7cc7d6d4ebc4767`。第 2 轮返修提交见本工作树 `git log -1`。

本报告只覆盖 **cheat-v1 测试消费者字段绑定**。它不是 N3/G1 通过：G1 仍未批准，V4 仍是唯一生产架构，G5 未完成，`complete.flag` 不存在。未修改生产 C#、V4 exporter/fixture、catalog/manifest、统一 verifier、项目/solution 或用户环境；仍仅原四文件。

## 0. 第 2 轮结论

| 对象 | 结果 | 说明 |
|---|---|---|
| 75 个 handler case | **67 PASS / 0 FAIL / 8 BLOCKED** | 67 个真实调用；8 个准确 BLOCKED，未计 PASS |
| 6 个 tileMovement case | **6 PASS** | 5 个精确重建 + M5 全 136 placements；M3 stale-index 叶显式映射 |
| ledger 深字段/快照 | **PASS** | initialPoints 四座、entry 全字段、拒绝 outcome/exception、flow、snapshot 顺序 |
| TileMarkState 深字段 | **PASS** | group 全字段、版本、到期、跨座位、异常语义、拒绝零副作用 |
| 原 16 Fact | **16 PASS** | 保持有效断言；纠正 drawn-backfill、disabled、foreign-hand |
| 外部单字段错值矩阵 | **42/42 FAIL_AS_EXPECTED** | 正控制 94/0/0/0/0 exit0；每项 native1 且真实 Failed>0 |
| 296 叶扫描 | **294 native1；2 项合法等价输入** | 2 项 = `factsBefore.tileId 8→9`、`inputKanCount 0→1`，见 §6.1 |
| 8 项严格性探针 | **8/8 native1 / Total94 / Failed1** | keep unknown/missing/wrong type、seat missing、version missing、fact schema、requestException |
| **独立 readiness 符合性探针** | **FAIL_CONFORMANCE：native1 / Total1 / Failed1** | 同一黄金期望，C# 返回 Allowed；不再用反向 pin 计绿 |
| Domain 全量 | **463 passed / 0 failed / 0 skipped / 0 Not Run** | native exit 0 |
| Release solution 构建 | **0 warning / 0 error** | native exit 0 |
| solution 测试 | **1179 passed / 0 failed / 0 skipped** | 435 AI + 149 Contracts + 132 Client + 463 Domain，native exit 0 |
| PG/WSS/Unity/IL2CPP/Gradle/远端 CI | **NOT_RUN** | 无生产变更，未重跑平台重型全量 |

**矩阵状态与绑定就绪分离**：`MATRIX=PASS`（42/42 变异真实失败）；`READINESS=BLOCKED`（8 个 BLOCKED + F-01 符合性探针 native1）。默认脚本以 readiness 非零退出；`-MatrixOnly` 才返回 matrix-only exit0。标准测试全绿**不**代表组件闭合。

## 1. D-R1-01 修复：不再反向 pin

- 删除了 `SleeveBloom_WallEmptyDrift_PinsKnownDivergence`（旧代码断言 `Assert.True(actual.Success)` 计绿）。该 case 现在为 **BLOCKED-input**：V4 输入包含独立的 `nextWallTileId=32 / nextWallTileInWall=false`，`CheatSubmission.ActivateSleeveBloom` 没有这个参数；仅把 32 移到河的 map 不能重放完整 V4 调用。
- 新增独立符合性探针（脚本默认路径；`-MatrixOnly` 可跳过 readiness）：在 TEMP 生成 xUnit 项目，引用现有测试程序集源码，按黄金要求断言 `Rejected/SLEEVE_BLOOM_WALL_EMPTY`。实际 C# 返回 Allowed，得到 **native1 / Total1 / Failed1 / Errors0 / Skipped0 / NotRun0**；日志 `readiness/readiness.log`。生产行为修正后该探针应转绿。
- `probe-cheat-binding.ps1` 现在输出 `matrixStatus`、`readinessStatus`、`readinessCauses`；8 个未闭合项 + F-01 未解决时 readiness 非零。`-MatrixOnly` 仅用于独立矩阵证据，summary 明示 `readinessStatus=NOT_RUN`。
- 不选择产品策略：没有改 V4/golden 规则、没有加生产参数、没有把其他合法墙牌宣布为必须拒绝。F-01 的准确依赖见 §8。

## 2. 输入重建与严格 schema

### 2.1 源锚点

- V4 exporter：`engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt`（ledger :111-187；tileMarkState :193-254；tileMark handler :359-427；sleeve :432-765；beam :767-976；tileMovement :1043-1378）。
- V4 构造器：`engine/src/testFixtures/kotlin/com/majiang/testing/TestTableBuilder.kt:8-76`。
- C# 入口：`CheatSubmission.cs:113,226,327,447,539,664,756,914`；`TilePositionMap.cs:637,686,1203,1258,1321,1375`。
- N1b 已接受入口：`dotnet/src/Mahjong.TableRuntime/TableActor.cs:223 SubmitCheatAsync`、`:1639 ProcessCheatCommandCoreAsync`、`:1662/:1893 ValidateAuthorityIdentity`、`:1689 RoundCheatTransition.Prepare`；测试文件 `dotnet/tests/Mahjong.TableRuntime.Tests/N1bCheatExecutionTests.cs`。

### 2.2 严格性

`AssertKeys` 对每个 `goldenInputs` 做精确 key 集校验；`GoldenString/Int/Long/Bool/Enum/NullableInt` 分别拒绝缺失、错型和未知枚举；`AssertFact` 区分 `FactsAfter`（hand/discard seat=number,index=null；wall/dead/replacement seat=null,index=number）与 `InputPlacements`（wall/dead/replacement seat=-1，hand/discard index=number）。未知 `keep`、删除 `keep`、删除 `seat`、`keep=123`、null 字段改字符串均真实失败。

## 3. 75 个 handler 的完整映射

`PASS`/`PASS-refined` 行由 `[Theory]` 真实调用；`BLOCKED` 行由 manifest 固定并单列，不能计入通过。表内 `Allowed / —` 的 reason 为 JSON null。各行 V4 输入源按 section 固定：tileMark `:359-427`，sleeve `:432-765`，beam `:767-976`；7 个 sleeve 成功例的 `goldenInputs` 逐字段读取。

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

- `tm-capacity-tiles`：V4 源输入是 `groups=2, marked=(16..27)=12, selected=0/4/8/12`。C# `TileMarkGroup` 固定四张，2 组不可能有 12 张；3 组会先触发 group limit，不能冒充 tile-count guard。该 case 从 PASS 改为 **BLOCKED-input**，分母保留；`tm-capacity-groups` 仍真实绑定 group limit。
- disabled 输入继续使用真实 `configuredCost=5`，不换成 `featureEnabled=false`；旧错误已删除。
- `bm-keep-original-args-forbidden-at-request.requestException` 断言精确 `IllegalArgumentException`；M4 `doraRefusalException`/`wallSlotRefusalException` 同样精确校验，不再忽略 metadata。
- Beam activation 使用 V4 activation 默认输入：`drawnTileId=16`、`replacement=0`；choice 使用 `beamTable`/`deadTable` 的 `drawn=52` 或 `108`、`replacement=135`。不再把 choice 的桌构造器当作 activation 输入。
- `sb-choose-stale-index`：保留冻结候选 32 在 `Wall(53)`，用同 tile 的 reservation 使其受保护，使 `nextAvailableWallIndex=54`；不再移动候选本身。行为仍是 StaleOffer，且 `offerSecondWallIndex=53` 得到消费。

## 4. 6 个物理移动

| Case | 真实入口/断言 | 结果 |
|---|---|---|
| `mv-swap-hand-with-wall-direct` | `SwapHandWithWall`，wall0→Wall53；factsBefore/After、指针、136 | PASS |
| `mv-swap-drawn-with-wall-backfill` | `DrawFromWall` → `ActivateSleeveBloom` → `ChooseSleeveBloom(SECOND)`；drawn 回 Wall54、墙牌进手/新 drawn evidence | PASS（表示差异见 §4.1） |
| `mv-reservation-delivery` | `DeliverSleeveBloomReservation`；removeAt、wallEnd-1、指针不动 | PASS |
| `mv-reservation-delivery-then-beam` | 136 placements、8 候选实体+槽、beamGot/nextDraw、factsAfter | PASS |
| `mv-rinshan-and-drawn-swap` | `DrawReplacement` + `SwapDrawnWithNextRinshan`；deadWall1=108、109 进手、135 replacement；108 的 V4 stale index=0 显式消费并映射到权威 slot=1 | PASS |
| `mv-swap-hand-with-dead-wall` | `DrawReplacement` + `SwapHandWithDeadWall(1)` + dora/WallSlot 拒绝 + `SwapHandWithReplacement(0)` | PASS |

### 4.1 drawn-backfill

旧“V4-only/no C# entry”说法错误：`ChooseSleeveBloom` normal-draw SECOND 分支调用 `SwapHandWithWall`，允许 drawn 作为手牌来源并重写 `DrawnTileEvidence`。C# 规范 map 不保留 V4 旧墙槽洞：`backfilledIndex=0` 映射为 Wall53 为空、29 成为新 drawn evidence；实体/容器/座位/槽位/后续摸牌一致。该 case 为 PASS，不冒充逐字相同，也不因此 BLOCKED。

## 5. 第 2 轮 5 组缺陷修复映射

### D-R1-01（MAJOR）反向 pin / exit0

- **位置**：旧 `CheatGoldenBindingTests.cs:1493-1511`；脚本 `blocked/summary/exit`；报告 §6/§9。
- **实际 vs 期望**：旧测试把实际 Allowed 当成功；未来黄金拒绝反而失败。旧脚本 7 项 blocked 仍 exit0。
- **修复**：删除反向 pin；case 改为 BLOCKED-input；新增独立 TEMP xUnit readiness 探针，黄金期望 `Rejected/NoWallCandidate`，实际 Allowed → native1/Total1/Failed1。脚本默认 readiness 非零；`-MatrixOnly` 分离矩阵。报告明确 matrixStatus 与 readinessStatus。
- **验收**：同一黄金期望探针 native1、Total>0、Failed>0、零 errors/skip/not-run；未来生产满足黄金时转绿。已绑定案例正控制/负例仍有效。

### D-R1-02（MAJOR）漏绑与宽松 schema

- **位置**：ledger/state、sleeve 分支、AssertFact/M3/M4/M5、矩阵。
- **实际 vs 期望**：initialPoints[1..3]、ledger 拒绝 outcome/exception、versionAfterFirstRegister、异常字符串、sleeve 多数 goldenInputs、keep 未知/缺失/错型、nullable schema、M5 delivered/pointer、M3 index、M4 metadata 曾被忽略或宽松。
- **修复**：所有上述字段逐叶断言；`AssertKeys` 精确 key 集，`GoldenEnum` 严格解析，`AssertFact` 区分 factsAfter/inputPlacements 的 nullable schema；M5 增补 deliveredTileId/wallPointerBeforeDelivery/inputKanCount/lastDiscard schema；M3 显式消费 stale index=0 并映射权威 slot1；M4 异常字符串精确；矩阵扩到 42 项，覆盖新类别。
- **验收**：8/8 strict probes native1/Total94/Failed1；296 叶扫描仅 2 项合法等价输入；42/42 单字段变异真实 Failed>0；合法等价项见 §6.1。

### D-R1-03（MAJOR）换输入冒充分支

- **位置**：`tm-capacity-tiles`、Beam activation、stale-index。
- **实际 vs 期望**：旧 `tm-capacity-tiles` 用 3 组 12 张验证 group limit，不是 tile-count guard；Beam activation 用了 choice 的 drawn52/replacement135；stale-index 移动候选。
- **修复**：`tm-capacity-tiles` 改 BLOCKED-input（2×6 不可由四张组构造）；Beam activation 改 drawn16/replacement0，choice 保留 52/108/135；stale-index 保留候选 53 并用 reservation 使 first-available=54。
- **验收**：`tm-capacity-groups` 与 tile-limit 不再混同；activation/choice 源输入分别有构造器锚点；stale-index 消费 offerSecondWallIndex=53/nextAvailableWallIndex=54；分母重算 67 PASS/8 BLOCKED。

### D-R1-04（MAJOR）零副作用取样时点

- **位置**：旧 `AssertRejected` 在入口后 `Snapshot.Capture`。
- **实际 vs 期望**：执行后与自身比较，无法检测改动。
- **修复**：`AssertRejected`/`AssertStaleWithState` 强制接收调用前 Snapshot；所有 caller 在入口前捕获并传入。Snapshot 覆盖 ledger entry 全字段、mark 全字段/round ids、sleeve offer/reservation、beam offer、`CheatState.Fingerprint`、136 locations；stale 分支只允许 offer 清除，ledger/marks/beam/map 必须不变。
- **验收**：代码中 helper 不再内部生成 before；拒绝/stale 分支的真实前态与后态对照；定向+全量真实计数/exit。

### D-R1-05（MINOR）依赖文档与冻结基线不符

- **实际 vs 期望**：旧报告写“未来 actor dispatch/N-1b 完成后”；冻结基线已接受 N1b，存在 `SubmitCheatAsync`/`ValidateAuthorityIdentity`/`RoundCheatTransition.Prepare`。
- **修复**：4 个 ActorLayerBlocked 的 dependency 改为现有 N1b 链路 + 精确下一测试文件 `TableRuntime.Tests/N1bCheatExecutionTests.cs`；明确 Domain.Tests 不引用 TableRuntime，不声称这 4 例完成。
- **验收**：准确引用已接受 N1b；只有真实残余（CheatTurnContext 无 decision/deadline/round-ended 输入）标 BLOCKED。

## 6. 第 2 轮外部矩阵（42 项）

脚本：`tools/v5-golden/probe-cheat-binding.ps1`。原始 fixture 单字段复制到独占 TEMP；`try/finally` 恢复 `V5_CHEAT_GOLDEN_PATH`；写副本显式去 BOM；正控制未修改副本 94/0/0/0/0 exit0。每项要求 native1、Total=94、Errors=0、Skipped=0、NotRun=0、真实 Failed>0，且预期 test 名出现。

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

### 6.1 296 叶扫描（调用者脚本）

- 原始调用者 `probe-leaves-v2.ps1` 硬编码 Total=96；本轮移除两个不可绑定 case（drift 反向 pin、capacity theory）后行为套件 Total=94。使用 TEMP 副本仅把该期望从 96 改为 94，其余不变；调用者原证据未改。
- 结果：296 项中 **294 项 native1**，2 项 native0。
- 2 项合法等价输入：
  1. `$.tileMovement[mv-swap-hand-with-wall-direct].factsBefore[0].tileId 8→9`：9 仍是 seat0 手牌，`AssertFacts` 仍真实读取并断言 hand/seat；输入事实保持真，不是漏绑。
  2. `$.tileMovement[mv-reservation-delivery-then-beam].inputKanCount 0→1`：值进入 `FromPlacements(..., kanCount)` 且 `map.KanCount` 被断言等于 fixture；该 case 的物理行为不依赖 kanCount，属于合法等价输入。
- 其余 59 个旧 native0 叶现在均 native1；8/8 strict probes native1/Total94/Failed1。

## 7. 证据与命令

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
| solution 构建 | 0 warning / 0 error | 0 | `C:/Users/sncdc/AppData/Local/Temp/n3-cheat-r2-solution-build.log` |
| solution 测试 | 1179 passed / 0 failed / 0 skipped（435+149+132+463） | 0 | `.../n3-cheat-r2-solution-tests.log` |
| Domain 全量 | 463 passed / 0 failed / 0 skipped / 0 Not Run | 0 | `.../n3-cheat-r2-domain-full.log` |
| Cheat 定向 | 94 passed / 0 failed / 0 skipped | 0 | 正控制 `positive-control.log` |
| 42 项矩阵 | 42/42 FAIL_AS_EXPECTED；0 unexpected/tool/wrong | 0（matrix） | `matrix/matrix.csv` |
| readiness | FAIL_CONFORMANCE：Total1/Failed1/Errors0/Skip0/NotRun0 | 1 | `readiness/readiness.log` |
| 默认脚本整体 | MATRIX=PASS，READINESS=BLOCKED | 1 | `summary.json` |
| 296 叶 | 294 native1；2 合法等价 | 每项 1（除2项） | `n3-cheat-r2-leaf-9d22/leaf-probes-v2/results.csv` |
| 8 strict | 8/8 native1 / Total94 / Failed1 | 每项 1 | `n3-cheat-r2-strict-9d22/strict-probes/results.csv` |

最终默认证据根（新独占）：`C:/Users/sncdc/AppData/Local/Temp/n3-cheat-r2-final-cca1b707168047b19ca48b0c4b53d6af`。

| 文件 | SHA-256 |
|---|---|
| `summary.json` | `ABDA80D11DEDA927F703EEF26DDDA9828AEB6295697930F1F6E8391865FE739E` |
| `matrix/matrix.csv` | `EA3A4BCCFC87F8B85227D8586FB40ABBB4DBCC313EFC77A0407202600FD16DE3` |
| `evidence-sha256.csv` | `76A07DA54629340F331DD1E94DC46E2E4633630152B10E6ADF83AE95D6335A3A` |
| `readiness/readiness.log` | `F72356C6C6D87F632699AB827E9CAED5FA49A90A921EC3EC330A5412C502D8B9` |
| `logs/positive-control.log` | `BCF559884B846D0C2BCBEF919DA26ED1AFC98ECCB3F69451C55593FFE2DDA925` |

纯矩阵 exit0 证据根：`C:/Users/sncdc/AppData/Local/Temp/n3-cheat-r2-matrix-final-cf7c091be4a742f9bd816dc3de258886`，42 行，`matrixStatus=PASS`、`readinessStatus=NOT_RUN`、`blockedCount=8`，`summary.json` SHA-256 `CEABF9F58D925D7DAC5BE36867736341514ACEAB2AEFB360FAD29BF5747D6322`。

| 日志/CSV | SHA-256 |
|---|---|
| Domain 全量 `n3-cheat-r2-domain-full.log` | `F14BCEFEBC5C05565B4C320D0104E538D251B5CE43E64802D7447F3B8FD3CC1D` |
| solution 构建 `n3-cheat-r2-solution-build.log` | `BD31477C5C059467ED0CFA829FB542BBEA7AFB63126DB50C13C05CA5422C6D9C` |
| solution 测试 `n3-cheat-r2-solution-tests.log` | `CE24D02095C87F48EE2380B07E98A3592728268BB636F24258C2029EDB0681BD` |
| 最终脚本 console `n3-cheat-r2-final-console.log` | `3154DA2A85BC1E3943AD1B4208D16DCB767D856D03FA1AC6F9BB6C5D9D86B9D4` |
| 296 叶 `results.csv` | `03ECFDBA8019147EA7A179FEC70B040B8354C39CE3491D602E36572B41EA4ABF` |
| 8 strict `results.csv` | `A9E52A6E13B16D991771BA1C3FD0189888ADCF59128A5EA84BABB54DE3C86C77` |
| 296 叶 console | `15C116F1CBE3689521F2DBA86B8B7BF552A622CF946323093851EFE3546097DF` |
| strict console | `E4BA87476C5EC8CFD7AD9B2A00489B2FFEAB5859627DB17F205D7026708E9909` |

源码 SHA-256：

| 文件 | SHA-256 |
|---|---|
| `CheatGoldenBindingTests.cs` | `9C95A5C4CCA543E5A85F922E5F5C5338B1E43710A06626C152714FDFCACD23EE` |
| `CheatGoldenTests.cs` | `3B869A5491FCB029640B95364A61A3739EB0D6B45B80B90273151D00E68F88E5` |
| `probe-cheat-binding.ps1` | `F0C9010AE707680A15693883134E5C933502F81EEB1939CDD55718C7433B5DD6` |
| `tests/v5-golden/cheat-v1.json` | raw `A196CBBD97AB8F61BE8679B74C8F17DF2713CB37AEF0922E5F3C9F725B28AB4E`；LF `3B33824F53E6E5808D2475CC212FCF3A914BBB5DF9A1A367BF2078A252F1F68D`；Git blob `61307f255c8d1b02f1b64084768b6a500f3307b4` |

## 8. 剩余缺口与最小 scope

### F-01（BLOCKED-input，readiness FAIL）sleeve 冻结 next-wall 输入缺失

- **位置**：`CheatSubmission.cs:278-286`；`sb-activate-wall-empty-drift`。
- **复现**：默认脚本 readiness 段，或 `-class '*CheatBindingReadinessTests*'`（TEMP 生成工程）。
- **实际 vs 期望**：C# 重新搜索返回 Allowed；V4 冻结 `nextWallTileId=32`，期望 Rejected/NoWallCandidate。
- **证据**：`readiness/readiness.log`（native1/Total1/Failed1）。
- **修复**：caller 决策：接受 C# 重搜索并更新 golden 语义，或给 Domain 增加冻结候选输入（`nextWallTileId/nextWallTileInWall`），或 actor 预检。不得改规则或凭空选策略。
- **验收**：readiness 探针转绿；合法墙牌仍 Allowed；8 个 blocked 清零后 readiness 才为 PASS。

### F-02（BLOCKED-input）beam dead-wall 空集/仅 dora

- **位置**：`bm-activate-deadwall-empty`、`bm-activate-deadwall-dora-only`；`TilePositionMap.cs:1587-1593`。
- **复现**：`BeamDeadWallFilterStates_AreNotCanonicalTilePositionMaps`；两种过滤态 `ConservationException`。
- **期望**：需要 handler-level `deadWallTileIdsInDeadWall` 输入或候选抑制面；不得搬空王牌区/伪造 136 位置。
- **验收**：新入口下空集/仅 dora Rejected，其他合法槽 Allowed，136 守恒；dora 排除另有正/负测试。

### F-03（BLOCKED-actor）4 个 tile-mark 决策/截止时间

- **位置**：`tm-wrong-phase-round-end`、`tm-no-active-decision`、`tm-deadline-expired`、`tm-decision-mismatch`。
- **实际**：N1b 已接受；`TableActor.SubmitCheatAsync → ValidateAuthorityIdentity → RoundCheatTransition.Prepare` 存在；但 `CheatTurnContext` 无 decisionId/deadline/round-ended，Domain.Tests 不引用 TableRuntime。
- **下一测试**：`dotnet/tests/Mahjong.TableRuntime.Tests/N1bCheatExecutionTests.cs`（或同项目 actor 测试），不新增 Domain.Tests 项目引用。
- **验收**：deadline 相等/无 decision/round ended/id 不符均拒绝，零扣费；合法 case 仍 Allowed。

### F-04（MINOR）`tm-capacity-tiles` fixture note 与 exporter 输入不一致

- note 说“8 marked +4”，exporter `:418` 实际 `marked=(16..27)=12`。行为 reason 一致，但 note 不自洽；下次合法导出改为 12。本轮不改 fixture。

### F-05（MINOR）M3 `factsAfter` 108 的 V4 重复列表 artifact

- V4 `TileMovement.kt:591` 保留旧 `deadWall[0]` 引用，`factsJson indexOf` 返回 0；权威 `deadWallSlot1AfterSwap=1`。本轮显式消费 index=0 并映射权威 slot1，不把 artifact 当 C# 槽位；下次导出可去重。

### F-06（BLOCKED-input）`tm-capacity-tiles` 的 2×6 输入不可构造

- V4 handler 输入 `groups=2, marked=12` 与 C# `TileMarkGroup` 固定四张冲突；13+ tile-limit 分支不可由公开构造器到达。该 case 不再计 PASS，需 state-level group-size 输入或新的可达导出 case；不得用 group limit 冒充 tile-count guard。

### F-07（MINOR）296 叶中的 2 项合法等价

- `factsBefore[0].tileId 8→9`、`inputKanCount 0→1` 保持输入语义/物理行为；已记录为等价控制，不是漏绑。若要求所有叶严格不等价，需要更窄的 fact 选择器或 kanCount-sensitive case，但不应把合法变化硬判失败。

## 9. 状态分列

| 状态 | 内容 |
|---|---|
| **PASS** | 67/75 handler；6/6 movement；ledger/state 深字段；42 项矩阵；294/296 叶；8/8 strict；Domain 463；solution 1179；Release 0/0 |
| **FAIL** | readiness F-01：同一黄金期望 native1/Total1/Failed1（C# Allowed vs V4 Rejected）；不是行为测试全绿 |
| **BLOCKED** | 8 个 handler：4 actor + F-01 + 2 beam 过滤态 + F-06 capacity；均保留分母和依赖 |
| **NOT_RUN** | PG/WSS、Unity/IL2CPP、Android/真机、真实网络、V4 Gradle 全量、TableRuntime 重跑、远端 CI；无生产变更，不盲跑 |

## 10. 完成边界

67/75 行为绑定通过，但 8 个 BLOCKED 与 readiness FAIL 仍未闭合；F-01/F-02/F-03/F-06 的依赖已列。G1/N3/G5 仍不通过，V4 仍唯一生产基线。没有 push/PR/CI/合并兄弟代码；主 checkout 用户未提交文件与 GLM 在途工作树未触碰。
