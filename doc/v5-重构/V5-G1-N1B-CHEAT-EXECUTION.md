# V5 G1 N-1b：七个出千命令的真实权威执行接线

日期：2026-10-25
工作树：`D:/majiang-wt/n1b-cheat-execution`，分支 `wt/v5-n1b-cheat-execution`
冻结基线：`3bfe952cc7adb9bdc2f5bb0a7589a26ec731886b`
（已核实 `git rev-parse --show-toplevel` / `HEAD` / `--abbrev-ref HEAD` / `status --porcelain`；基线是 PR19 `feat/v5-1-integration -> v5` 的已发布头，含已验收 N-1a。）

> **结论边界（必读）**：D-R1-01..05、D-R2-01..05、D-R3-01..03、D-R4-01..02 均已批量修复。N-1b actor 组件面在最终测试与 16 个复核探针上通过，但 D-R1-06（协议 `CommandKind`→actor 宿主入站映射）仍属 P4.1/P5.1；PG/WSS/Unity/IL2CPP/设备/网络未跑。G1/G5 不批准，不创建 `complete.flag`。

---

## 1. 入口与七个动作映射

唯一公开入口：

```csharp
TableActor.SubmitCheatAsync(TableCheatCommandEnvelope, CancellationToken)
```

它进入与普通命令**同一个** `TableMailbox` 业务队列，由唯一串行 consumer 处理。客户端不提供 `CheatTurnContext`、map、`CheatState`、余额或候选；actor 在队列内从权威 `RoundStateMachine` 与自身持有的 `CheatState` 重建上下文。

| `CommandKind` 语义 | 类型化 `RoundCheatAction` | 唯一 P1.4 入口 |
|---|---|---|
| `SubmitTileMark=12` | `SubmitTileMark` | `CheatSubmission.ApplyTileMark` |
| `SubmitSleeveBloom=13` | `SubmitSleeveBloom` | `CheatSubmission.ActivateSleeveBloom` |
| `SubmitSleeveBloomChoice=14` | `SubmitSleeveBloomChoice` | `CheatSubmission.ChooseSleeveBloom` |
| `SubmitSleeveBloomOffTurn=15` | `SubmitSleeveBloomOffTurn` | `CheatSubmission.ActivateOffTurnSleeveBloom` |
| `SubmitSleeveBloomOffTurnChoice=16` | `SubmitSleeveBloomOffTurnChoice` | `CheatSubmission.ChooseOffTurnSleeveBloom` |
| `SubmitBeamSwap=17` | `SubmitBeamSwap` | `CheatSubmission.ActivateBeamSwap` |
| `SubmitBeamSwapChoice=18` | `SubmitBeamSwapChoice` | `CheatSubmission.ChooseBeamSwap` |

`TableCheatCommandEnvelope` 是新的不可变 typed envelope，带 `RoundCheatCommand` + `RequestId` + `RequestScopeId` + `Api` + `PayloadSha256` + `OwnerEpoch` + `ControlEpoch` + `DecisionId` + `RecoveryGeneration` + `ExpectedStateVersion`；`roundId/seat/action/payload` 在 `RoundCheatCommand`。envelope 构造器要求 scope/api/SHA 同时出现；actor 对 in-memory 与 durable 均要求该身份，缺失返回 `PAYLOAD_IDENTITY_REQUIRED`。

---

## 2. 数据所有权与 pure prepare

- `RoundCheatTransition`（Domain，新）是纯函数桥：输入唯一为不可变 round、不可变 cheat 聚合、typed command、由 actor 构造的 `CheatTurnContext`、economy/sleeve 配置。
- `CheatTurnContext` 的 `currentPlayer/phase/responseWindowOpen/riichi/turn/roundId/roundOrdinal/lastDrawKind/nowMillis` 全部由 actor 从 `RoundStateMachine` 与 `CheatState.TileMarks.DealtRoundOrdinal` 读取；`now` 来自注入 `ITableClock`。Domain 不读时钟/随机/外网。
- 七动作都只调用既有 P1.4 方法，不复制物理/费用规则。
- 成功 prepare 返回 `RoundCheatChange`：`RoundChange`（下一 round，版本恰好 +1）、下一 `CheatState`、`RoundCheatEvent[]`。桥接只允许 `RoundEventType.CheatApplied` 或 `TileDrawn` 两种事件；它不接收“外部 map 直接安装”，仍走 `RoundStateMachine.Install` 的 sourceVersion/sourceFingerprint/parent 链校验。
- 无移动的 offer/mark 也通过 `CheatApplied` 桥推进 **一个** actor state version 与一个 round version；不会零版本隐变。
- 多私有事件（例如 mark 扣费 + mark 应用）在同一个 command version/事务中，事件 ordinal 从 0 连续。
- 纯 replay/no-op：`CheatSubmission` 返回 `Success=true, Changed=false` 时 prepare 返回 `CHEAT_REPLAYED`，actor 不推进版本、不写事件、不扣费；有 receipt 时 receipt-first 返回原回执。
- P1.4 `Success=false, Changed=true`（旧的 off-turn 强制 FIRST / stale 清理）会形成 `RoundCheatChange`，actor 持久化状态/事件后仍向调用者返回 **Rejected**，不会误报成功。持久化结果按同一 journal 重放可得到同一状态；同一 request 重放返回原拒绝回执。

---

## 3. 原子提交点、配置入口与恢复

### 3.1 两种明确入口

- **in-memory**：`TableActorOptions.CheatEconomy` + `SleeveBloomConfig` + 可选 `InitialCheatState`；`ITableCommitPort` 负责原子提交，无 `ITableStore`。
- **durable**：同样 options + `TableDurableContext`；actor 自身持有 `_cheat`，checkpoint 直接写该聚合，**不再**把旧 `CheatStateProvider` 的旧状态写入新命令。恢复时 `TableRecoveryBaseline.Cheat` 安装回 `_cheat`。
- `CheatEconomy` 存在时强制要求 `SleeveBloomConfig`；`InitialCheatState` 存在时强制要求 `CheatEconomy`。durable 恢复若配置了出千但 baseline 无 cheat 聚合，直接失败；带 cheat/facts 的恢复仍要求显式 `RecoveryStateSink`。

### 3.2 提交顺序

候选 round / map / CheatState / ledger / projection facts / decision clock / timing / events / receipt 全部在调用 commit 前冻结：

1. 队列内重建 context；
2. pure prepare；
3. `RoundStateMachine.Install` 校验候选直接子状态；
4. 计算事件 seq/ordinal、receipt、timing snapshot、checkpoint/facts；
5. `ITableCommitPort` 或 `ITableStore.CommitAsync` 原子提交；
6. **只有**提交成功后才安装 `_round/_cheat/version/eventSeq/clock/pending` 并发布。

失败/取消/租约丢失/`StaleOwner`/`StaleState` 在安装前返回，内存零变化。in-memory 与 durable 均不新增第二套 authority receipt store；durable 使用现有 `IReceiptStore`，in-memory 仅对已提交 cheat 结果做 request-id 原回执缓存（不参与 durable authority）。

### 3.3 journal / checkpoint / 恢复

- `TableCheatCommandCodec`（schema 1）保存 typed command + 注入的 `turnNumber/nowMillis/roundOrdinal/lastDrawKind`；journal schema 2。
- 系统到期动作 `OffTurnExpiry/ImmediateExpiry/BeamExpiry` 用 `TableCheatSystemCodec`（schema 3），无客户端 request id。
- 普通 `DrawTile`/`DiscardTile` 仍用 round command journal（schema 1）；恢复时按相同规则重放（含预约交付/受保护摸牌）。
- `TableCheatLedgerCheckpointCodec` 升为 **4**，持久化完整 CheatState：ledger、tile marks（round registry/group/overlap）、sleeve 的 immediate/off-turn offer/window/reservation/windowSeq/usedOnTurn（offer 含 boot id）、beam 的 candidates/eligible hand/drawn/usedOnTurn，以及总 version/fingerprint；旧 schema 3 明确拒绝。
- `TableCommittedHistoryCodec` 升为 **2**，持久化 cheat 事件 record（typed `CheatEvent` + offer/keep/action/result/hand/target/turn）与统一 seq/version/ordinal；旧 schema 1 明确拒绝。
- checkpoint 顶层 schema 仍为 **2**；旧子 payload schema 由上述 codec 明确拒绝，不做静默补默认。
- 冷恢复不生成新 request id、不二次扣费/换牌；恢复后的旧 `recoveryGeneration` 命令被 `_strictRecoveryFence` 拒绝。

---

## 4. 生命周期与投影/隐私

- **discard**：正常/超时/AI discard 后为打牌者打开 off-turn window；discard 自动摸牌、response Pass、riichi、所有 kan replacement 都跳过受保护 tile。
- **immediate/beam offer**：正常 discard/riichi/terminal 等决定性命令在同一事务内先按 P1.4 fallback 结算/清除，避免旧 global offer 残留；超时由权威 tick 强制 FIRST/KEEP_ORIGINAL。
- **off-turn offer**：tick 内 `ExpireOffTurnSleeveBlooms` 强制 FIRST；逾期人工 choice 仍按权威 deadline 拒绝/或由 P1.4 `RejectedWithState` 持久化强制 FIRST。
- **预约交付**：下一次真实普通摸牌（显式 `DrawTile`、discard/response/riichi 自动摸牌）在**同一 command transaction**内交付；`TileDrawn` 公开事件 + owner-scoped `SleeveBloomReservationDelivered`，不二次扣费。
- **受保护墙牌**：`TilePositionMap.DrawFromWall(seat, isProtected)`/`DrawReplacement(seat, isProtected)` + `RoundStateMachine.Prepare(command, isProtectedWallTile)` 统一覆盖 draw/discard/pass/riichi/kan；`DrawReplacement` 在受保护墙尾时保持 `WallEndExclusive` 覆盖留守牌，保持 136 张实体守恒。
- **事件投影**：`TableEventProjection.FromRoundEvent` 识别 `TableEventRecord.Cheat`，走 `FromCheatEvent`；`CheatPointsChanged`、`SleeveBloomResolved`、`BeamSwapResolved` 分别映射到已有协议 20/21 typed Wire payload。`CheatApplied` 是内部 provenance 桥，不映射为任何协议事件；`TileMarkApplied`/`SleeveBloomOfferCleared`/`SleeveBloomReservationDelivered` 不硬改成其他协议事件。
- `TableProjectionContractMapper` 已有点位由实际事件 facts 填充：offerId、keptFirst、resultTileId、beam resolution、handTileId、targetSlot；actor 从 `TableCheatEventRecord` 合成并合并到 projection facts，checkpoint 持久化后再由恢复校验。
- 私有性：`TableEventProjection.Project` 只把 private 事件给 owner；spectator/其他三席不产生这些事件；`CheatPublicSnapshot` 仍为空；owner 私有 balance/ledger/mark/offer/reservation/candidates 只在 `CheatState.ViewFor`→player/owner snapshot。`TableSnapshot.RecentEvents` 刻意过滤 cheat 事件，避免 authority snapshot 泄露；owner 私有事件只走 seat stream/checkpoint。

---

## 5. 逐文件清单

### Domain

| 文件 | 变更 |
|---|---|
| `dotnet/src/Mahjong.Domain/RoundCheatTransition.cs`（新） | typed payload/command/journal/prepare、七动作分发、reservation draw、discard+delivery composition、过期/系统动作 |
| `dotnet/src/Mahjong.Domain/RoundTransition.cs` | 追加 `RoundEventType.CheatApplied=19` |
| `dotnet/src/Mahjong.Domain/RoundStateMachine.cs` | `PrepareCheatBridge/PrepareCheatComposed`、受保护 draw/discard 桥、suppressed discard |
| `dotnet/src/Mahjong.Domain/TilePositionMap.cs` | `DrawFromWall` 受保护 tile 跳过重载 |
| `dotnet/src/Mahjong.Domain/Cheat/CheatState.cs` | `CheatEvent.Restore`（恢复构造器） |
| `dotnet/src/Mahjong.Domain/Cheat/TileMark.cs` | `TileMarkState.Restore` |
| `dotnet/src/Mahjong.Domain/Cheat/SleeveBloom.cs` | `SleeveBloomState.Restore` |
| `dotnet/src/Mahjong.Domain/Cheat/BeamSwap.cs` | `BeamSwapState.Restore` |

### TableRuntime

| 文件 | 变更 |
|---|---|
| `TableRuntimeContracts.cs` | `TableCheatCommandEnvelope`、`TableCheatEventRecord`、cheat/system journal、`TableEventRecord.Cheat`、options |
| `TableActor.cs` | `SubmitCheatAsync`、统一 authority guard、cheat commit/install、discard/draw delivery、tick expiry、overlay、recovery 安装 |
| `TableActorMessages.cs` | mailbox 可承载 typed cheat envelope |
| `TableEventProjection.cs` | cheat record→typed projection、offer/resolution/action/tile/target facts |
| `TableCheckpointCodecs.cs` | 完整 cheat checkpoint schema 4、cheat command schema 1、system journal schema 1 |
| `TableCheckpointValidator.cs` | 过滤 cheat history、captured prefix 校验 |
| `TableRecoveryPayloads.cs` | committed history schema 2（含 typed cheat event） |
| `TableRecoveryService.cs` | 重放 schema 2/3、reservation draw/discard delivery、受保护摸牌、cheat 配置 |
| `TableDurableContracts.cs` | rejected-with-state commit、`TableStateRow.CheatVersion/CheatFingerprint`、commit request `NextCheat*` |
| `TableRuntimePorts.cs` | `TableCommitRequest` rejected-with-state |
| `InMemoryTableStore.cs` | rejected-with-state 原子状态推进 + rejected receipt |
| `TableRuntimeReasonCode.cs` | cheat 稳定 reason code 映射 |
| `TableVisibilityProjection.cs` | recovered event facts 对 cheat/bridge 的覆盖规则 |

### Tests / Doc

| 文件 | 变更 |
|---|---|
| `dotnet/tests/Mahjong.TableRuntime.Tests/N1bCheatExecutionTests.cs`（新） | 36 个真实 actor/durable/wire 测试 |
| `doc/v5-重构/V5-G1-N1B-CHEAT-EXECUTION.md`（本文件） | 交付、证据、缺口 |

**范围扩大报告（准确路径/原因）**：名义 S-N1b 只列 `TableRuntimeContracts.cs`/`TableActor.cs`/`TableProjectionContractMapper.cs` + tests。实际额外修改的路径是：

- `RoundCheatTransition.cs`、`RoundStateMachine.cs`、`RoundTransition.cs`、`TilePositionMap.cs`：N-1b 必须有 Domain 纯 prepare/安装、受保护摸牌、桥事件；不把外部 map/状态直接塞进 actor。
- `Cheat/CheatState.cs`、`TileMark.cs`、`SleeveBloom.cs`、`BeamSwap.cs`：完整 cheat checkpoint/恢复需要受验证的恢复构造器；不修改 P1.4 的费用/物理/窗口规则。
- `TableActorMessages.cs`、`TableRuntimeReasonCode.cs`：typed envelope 进入同一 mailbox 与稳定拒绝码。
- `TableCheckpointCodecs.cs`、`TableCheckpointValidator.cs`、`TableRecoveryPayloads.cs`、`TableRecoveryService.cs`、`TableDurableContracts.cs`、`TableRuntimePorts.cs`、`InMemoryTableStore.cs`、`TableVisibilityProjection.cs`：第 6 条要求的 journal/checkpoint/冷恢复/旧 schema 拒绝/rejected-with-state/恢复 facts 校验；不新增第二套 receipt store。
- `TableProjectionContractMapper.cs` 已改：D-R2-02 曾加入 UTC 阈值猜域，D-R3-03 已移除该阈值并恢复显式 monotonic 投影时间契约；不改变冻结 Wire payload。

未改：`AGENTS.md`、`CLAUDE.md`、`PROMPTS.md`、任务书/协议、`unity/`、`Engine/Resources`、Burst、`tests/v5-golden/*`、任何冻结 fixture/catalog/verifier/GoldenTests，以及 N-2/N-3 在途文件。

---

## 6. 验证（真实 exit/计数/零 skip/日志/SHA256）

工作树：`D:/majiang-wt/n1b-cheat-execution`；CLI：`D:/Unity/dotnet/dotnet.exe`；日志目录（独占）：`C:/Users/sncdc/AppData/Local/Temp/n1b-cheat-execution-0f3b9d6c/`。

| 验证 | 命令 | 实际 | exit |
|---|---|---|---|
| Release solution | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental` | 12/12 项目，**0 warnings / 0 errors** | 0 |
| Domain 全量 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build` | **346 passed / 0 failed / 0 skipped / 346 total** | 0 |
| TableRuntime 全量 | `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build` | **352 passed / 0 failed / 0 skipped / 352 total** | 0 |
| Contracts/Wire 回归 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --no-build` | **149 passed / 0 failed / 0 skipped / 149 total** | 0 |
| 复核 7 探针 | 复核方 `Review.csproj --filter-method '*Review_*'` | **7 passed / 0 failed / 0 skipped** | 0 |
| 复核 R2 3 探针 | 复核方 `Review.csproj --filter-method '*ReviewR2_*'` | **3 passed / 0 failed / 0 skipped** | 0 |
| 复核 R3 3 探针 | 复核方 `Review.csproj --filter-method '*ReviewR3_*'` | **3 passed / 0 failed / 0 skipped** | 0 |
| 复核 R4 3 探针 | 复核方 `Review.csproj --filter-method '*ReviewR4_*'` | **3 passed / 0 failed / 0 skipped** | 0 |
| diff 格式 | `git diff --check` | 无输出 | 0 |

基线对照：Domain 346（N1b 未新增 Domain 测试文件）；TableRuntime 316 → **352（+36 N1b 用例）**；Contracts 149。

日志 SHA256（完整小写；本轮最终代码）：

| 文件 | SHA256 | 字节 |
|---|---|---|
| `r16-build.log` | `eb474339d8a4b64937b391838d8b33b76deaea242a38544f5274969e1ce533d6` | 3468 |
| `r16-domain.log` | `64c99b6a7fb81f28aa446fcddf8be58ed842bbde19b0a0ef1730e2045610c755` | 728 |
| `r16-table-runtime.log` | `19b3d321bef777a4ccb28721b6647632f701b875092554292db251dbd2de3344` | 788 |
| `r16-contracts.log` | `71f79f89990e339c26c204c1bf701d616517a4ea358da523d5fd3dfc2bfdf1bb` | 758 |
| `r16-r1-probes.log` | `fd801ba463b4163647b7231f007bbb92d4906fea60a541aaa4fb47ca0106efe0` | 2682 |
| `r16-r2-probes.log` | `c8faa007c9b408345d21d245525615c22de66e8e50c2866bcc21112a28b168ab` | 2658 |
| `r16-r3-probes.log` | `e133474a5a13c2706539aea8508fc1c10f183fc74d731f459cbfd8c9e35d2f30` | 2658 |
| `r16-r4-probes.log` | `1b3e65159b144b56fd27c1b9f3b523cb20e1cf2e3a515b4b7225bd6e1f1470dc` | 2658 |

源码 SHA256（本轮提交前）：

| 文件 | SHA256 |
|---|---|
| `SleeveBloom.cs` | `7bd95e087f0fe534fadf874d3bba0637b5cf6748a92c625df94eb3b1adafb736` |
| `CheatSubmission.cs` | `bc5a5c142fbf53d5741cb28af2a59d3a16d5b402b19e3a5715a6204885c9c9f7` |
| `CheatState.cs` | `d7276b9f3e903c1441cbb3932c0e63b77fece6b5aa20f3cd2ab28b2ca948d7c7` |
| `RoundCheatTransition.cs` | `5fc02a87fcc5305a7dd4ddd242ea199bf4bb93b14a1c7b6f21eab74b512dfb4a` |
| `RoundStateMachine.cs` | `ff3c867129a63fd8aef8effde96a769a8cc19defa1b5cb0a4c80f7dce3dc27a0` |
| `TilePositionMap.cs` | `350d9cd8a2878f90001ccb3aa909fa4bb1223c5dc2502b6f223756ef87201dbb` |
| `TableActor.cs` | `65c5d5904a82c40501fcd4dbb9151e77e7b87dcdcb7be539505a84696576bf06` |
| `TableRuntimeContracts.cs` | `d97b7b630e15ac689563901e7463394fbbc4a9bdb9394a4db0d3a326c63f2c52` |
| `TableCheckpointCodecs.cs` | `985bac0a1467bed27801e6b965ec6d54e5b232838c2e0ff5c6d7fae2dd804153` |
| `TableProjectionContractMapper.cs` | `06d2cbac62c460193379cef08be645834942d2586d7c6b9a2cfce7dbe4733415` |
| `TableVisibilityProjection.cs` | `bcafc0fd90f97cca5c2193654e1f67ad069eb79c8cf2bd982d702578fcd3caf7` |
| `TableRecoveryService.cs` | `8eb75b0b13a6d72296b260499cef85a84cbfe26d52ce9e60efd2aeb36539f44e` |
| `InMemoryTableStore.cs` | `986804eecb1ae2cb1aaa8bf0f941c7cdb5912fb231ee2f4f6270c14cd36044f6` |
| `N1bCheatExecutionTests.cs` | `f6bde8813b7595316bab1e5bf8dad05ebca38a5f073a9b0337232951a98f5b60` |

---

## 7. 本轮返修映射（D-R1-01..05）

| ID | 修复映射 | 验证 |
|---|---|---|
| D-R1-01 | `RoundStateMachine.Prepare(command, isProtectedWallTile)` 统一线程到 `PrepareDraw/PrepareDiscard/PreparePass/PrepareRiichi/PrepareOpenKan/PrepareClosedKan/PrepareAddedKan`；`ResolveCompletedWindow`→`CommitOpenKan/CommitAddedKan/AdvanceToNextDraw` 全部传递；`TilePositionMap.DrawReplacement(seat, isProtected)` 跳过受保护墙尾并保持 `WallEndExclusive` 覆盖留守牌；`ApplyInternalCommandAsync` 统一 timeout/AI 的 delivery/protected/window 逻辑。 | `internal_timeout_discard_opens_window_and_preserves_foreign_reservation`、`response_pass_after_discard_preserves_foreign_reservation`、`closed_kan_replacement_skips_a_protected_live_wall_tail_tile`、`riichi_automatic_draw_skips_a_protected_reservation_tile` 全 PASS；复核 3 个 D-R1-01 探针 PASS。 |
| D-R1-02 | `RoundCheatTransition.PrepareCommandWithOfferSettlement` 在 discard/riichi/terminal 等决定性命令同一事务内用 P1.4 `ExpireSleeveBloomImmediate/ExpireBeamSwap` 结算；`PrepareDiscardDelivery` 同样先清理打牌者 immediate/beam；terminal commit 生成 checkpoint/outbox。 | `normal_discard_settles_pending_immediate_and_beam_offers`、`terminal_tsumo_settles_a_pending_immediate_offer_before_ending_the_round`、`durable_terminal_tsumo_with_pending_offer_commits_checkpoint_and_outbox` PASS；复核 immediate discard 探针 PASS。 |
| D-R1-03 | `ProcessCheatCommandAsync` 在任何 Domain/回执处理前要求 inner `RoundCheatCommand.RequestId` 与 outer `RequestId` 逐字相等，否则 `CHEAT_REQUEST_ID_MISMATCH`，零事件/零版本/零扣费。 | `inner_and_outer_request_ids_must_match_before_domain_execution` PASS；复核 D-R1-03 探针 PASS。 |
| D-R1-04 | 新增 actor 级 `_cheatRequestBindings`：以 request id 绑定 scope/API/SHA/结果；同 scope/API/SHA 返回原结果（含业务拒绝），异 scope 拒绝，异 API/SHA Conflict；cold recovery 从 durable journal schema 2 重建绑定（不允许跨 scope 取得旧私有回执）；commit 失败不写绑定。 | `business_rejection_replays_after_turn_changes_and_distinct_scope_cannot_reuse_private_result`、`old_owner_epoch_is_rejected_after_recovery_and_concurrent_cheats_commit_at_most_once` PASS；复核 D-R1-04 两探针 PASS。 |
| D-R1-05 | `TableStateRow` 增加 `CheatVersion/CheatFingerprint`，`TableDurableCommitRequest` 每次 accepted commit 同步写入；`TableRecoveryService` 冷恢复最终逐字比较完整 cheat 聚合；termimal checkpoint 用 `cheatOverride` 写新聚合；新增 tamper 负例与 durable fault 注入。 | `durable_cheat_binding_is_persisted_and_tamper_recovery_is_refused`、`durable_commit_fault_leaves_no_cheat_state_version_or_charge` PASS；完整 checkpoint/outbox 冷恢复 PASS。 |
| D-R1-06 | 协议 `CommandKind`→actor 的宿主入站映射 | 仍属 P4.1/P5.1，**BLOCKED/阶段事实**，本轮不抢做。 |

`N1bCheatExecutionTests.cs` 现为 **36 项**真实 actor/durable/wire 测试；Domain 既有 346 项全部保持。
复核方 TEMP 7 探针在本轮代码上 **7 passed / 0 failed / 0 skipped**（`r16-r1-probes.log`）。

### 新增/加强的负例矩阵

- 身份：inner≠outer、同 request 异 scope、同 scope 异 API/SHA、错 owner/control/expectedVersion/round/recovery、终态、错座位。
- 生命周期：timeout discard 开窗 + 保护他家预约；response Pass 保护；immediate/beam 在正常 discard/terminal 清理；off-turn 逾期强制 FIRST；riichi/closed kan 自动摸牌保护。
- 持久化：commit fault 零副作用；正常 checkpoint+journal/terminal checkpoint 冷恢复；cheat state version/fingerprint 篡改拒绝；durable 同 request 原回执；old owner 拒绝。
- 物理/经济：136 unique、beam SWAP 真实位置、kan replacement 保护墙尾、余额只扣一次。

---

## 7.1 第 3 轮 D-R2-01..05 修复映射

| ID | 修复 | 验证 |
|---|---|---|
| D-R2-01 | `PrepareCommandWithOfferSettlement` 增加 `deferAutomaticDraw`：先结算 immediate/beam，再把 riichi/Pass 的延迟自动摸牌与下一 seat 预约交付组合进同一 `RoundChange`/version；公开、AI/timeout、journal replay 共用。 | `ReviewR2_riichi_offer_settlement_must_also_deliver_next_seat_reservation` PASS；D-R1 原 7 探针仍 PASS。 |
| D-R2-02 | cheat offer/context 的 `NowMillis` 改用注入 UTC（`NowUtc()`），跨 boot 时旧 monotonic deadline 不会复活；恢复仍按原 offer 与 UTC 判定过期。 | `ReviewR2_cross_boot_expired_offer_must_not_gain_new_time` PASS；同 clock 的 D-R1 过期测试仍 PASS。 |
| D-R2-03 | `TableStateRow.CheatBound` 明确标记“已提交完整 cheat 聚合”；清空 version/fingerprint 但 bound=true、单字段为零、有 cheat journal 而 binding 缺失均 fail-closed；初始无 cheat/版本 0 合法。 | `ReviewR2_zeroed_final_cheat_binding_must_fail_closed` PASS；篡改 version+1 测试仍 PASS。 |
| D-R2-04 | `_cheatRequestBindings` 固定容量 `8192`，满载时拒绝新请求（`CHEAT_RECEIPT_CAPACITY_EXCEEDED`）而不静默 evict；durable recovery 重建也受容量约束。 | 代码审计 + 现有 identity/replay 回归；容量策略已文档化。 |
| D-R2-05 | 文档计数统一为最终实测值，D-R2 缺陷单列，不升格 G1/G5。 | 本文件 §6/§8/§9。 |

---

## 7.2 第 4 轮 D-R3-01..03 修复映射

| ID | 修复 | 验证 |
|---|---|---|
| D-R3-01 | 容量检查改为仅对 `prior == null` 的新 request 生效；已知 request 先做 scope/API/SHA 验证后始终进入权威 receipt lookup，满载不再拒绝已提交/冷恢复的 durable 回执。 | `ReviewR3_durable_receipt_replays_at_capacity` PASS；`cheat_request_binding_capacity...` 仍 PASS。 |
| D-R3-02 | `TableRecoveryBaseline` 携带 `CheatBound/CheatVersion/CheatFingerprint`；`TableRecoveryService` 最终 state 重读、`ProcessDurableRecoveryAsync` 最终校验、`RevalidateRecoveryInstallAsync`（host Install 后、Serving 前）全部比较这 3 项。 | `ReviewR3_final_binding_tamper_during_install_must_fail_closed` PASS；正常/首次 tamper 测试仍 PASS。 |
| D-R3-03 | 同 boot 使用权威 monotonic；跨 boot 在 offer 上持久化 `BootId`，人工 choice 对 boot 不符的单个 offer 拒绝，tick 对同 offer 强制 FIRST；投影按 `BootId` 显示 0 剩余；不使用全局 flag，不依赖阈值。 | `ReviewR3_utc_rollback...` PASS；R4 checkpoint/重复恢复/tick 探针 PASS。 |

---

## 7.3 第 5 轮 D-R4-01..02 修复映射

| ID | 修复 | 验证 |
|---|---|---|
| D-R4-01 | 冷恢复从全部 schema2 journal 重建 `_cheatRequestBindings`，不再按本地容量静默截断；decode 失败进入 faulted 而非吞掉；容量仅限制真正新 request，已知/恢复回执始终进入权威 receipt lookup。 | `ReviewR4_cold_recovery_capacity_shrink_preserves_committed_receipt` PASS；仓内 `cold_recovery_capacity_shrink...` PASS。 |
| D-R4-02 | `SleeveBloomOffer` 持久化 `BootId`（cheat schema 4）；人工 choice 按目标 offer 的 boot 拒绝，tick 对 boot 不符 offer 强制 FIRST 并产生 reservation；checkpoint/重复恢复保留 offer boot；投影对旧 boot 显示 0 剩余。无全局 flag，单个 offer 结算后自然清除。 | R4 checkpoint/重复恢复与 tick 两探针 PASS；仓内两个回归 PASS；R1/R2/R3 旧 13 探针全 PASS。 |

---

## 8. 缺陷/缺口清单（PASS/FAIL/BLOCKED/NOT_RUN）

### D-R4-01 [已修复/PASS] 冷恢复/容量下调仍须重放 committed receipt

原缺陷：恢复按 capacity 静默截断 binding，旧已提交 request 被当新 ID 拒绝；decode catch 吞异常。
修复：恢复装入全部 schema2 身份，容量只限制新 ID；decode 失败 faulted；已知身份始终进入权威 receipt lookup。
证据：`ReviewR4_cold_recovery...` + 仓内同名回归。

### D-R4-02 [已修复/PASS] 跨 boot offer 必须逐 offer 持久化并 tick 强制 FIRST

原缺陷：全局 boot flag 不持久、不接 tick；checkpoint 后重复恢复清除，旧 deadline 在新 boot 保留。
修复：`SleeveBloomOffer.BootId`（schema 4）；choice 按目标 offer boot 拒绝；tick 强制 FIRST；checkpoint/replay/投影一致；无全局 flag。
证据：R4 两探针 + 仓内两回归；旧 13 探针保持。

### D-R3-01 [已修复/PASS] 满载 durable 已提交回执必须重放

原缺陷：容量检查位于 prior 检查之后但未区分已知 request，durable Accepted 落空后直接容量拒绝。
修复：仅新 request 受容量限制；已知身份保持 scope/API/SHA 验证后进入权威 receipt lookup。
证据：`ReviewR3_durable_receipt_replays_at_capacity`；原容量测试仍通过。

### D-R3-02 [已修复/PASS] 安装期最终 cheat binding 必须 fail-closed

原缺陷：最终 state 重读/install 后 revalidation 未比较 `CheatBound/Version/Fingerprint`，sink 内篡改可被 Recovered 安装。
修复：baseline 携带完整 binding；service 最终重读、actor 最终校验、host Install 后 final fence 三重比较。
证据：`ReviewR3_final_binding_tamper_during_install_must_fail_closed`；首次/正常恢复仍通过。

### D-R3-03 [已修复/PASS] 同 boot UTC 回拨不能延长 offer

原缺陷：cheat context 改用 UTC，UTC 回拨可让已耗尽窗口复活；投影按数值阈值猜时间域。
修复：同 boot 使用权威 monotonic；跨 boot 由 boot id 检测，存在未结 offer 时 choice fail-closed；投影恢复显式 monotonic，无阈值。
证据：`ReviewR3_utc_rollback_must_not_extend_live_offer_deadline`；R2 cross-boot 探针仍 PASS。

### D-R2-01 [已修复/PASS] riichi + immediate offer + next-seat reservation 组合

原缺陷：非 Discard/Draw 先 `TrySettleOfferForCommand` 早返回，绕过 deferred reservation delivery；riichi 后 seat1 已摸牌但预约仍在 WALL。
修复：settlement 接受 `deferAutomaticDraw`，同一次 `RoundCheatChange` 中先结算 offer、再交付下一 seat 预约；public/internal/journal replay 共用。
证据：`ReviewR2_riichi_offer_settlement_must_also_deliver_next_seat_reservation` PASS；余额/136/一次交付断言。

### D-R2-02 [已修复/PASS] 跨 boot offer 期限不能复活

原缺陷：offer 使用旧 boot 单调 `CreatedAt/Deadline`，新 boot monotonic 重置后 SECOND 被接受。
修复：cheat context 使用注入 UTC（`NowUtc()`），跨 boot 的旧 deadline 仍按 UTC 判定；已过期只能 Rejected/forced FIRST。
证据：`ReviewR2_cross_boot_expired_offer_must_not_gain_new_time` PASS；同 clock 过期测试仍 PASS。

### D-R2-03 [已修复/PASS] 零值最终 cheat binding 必须 fail-closed

原缺陷：`CheatVersion==0 && CheatFingerprint==0` 直接跳过校验。
修复：新增 `TableStateRow.CheatBound`；bound=true 时零/单字段缺失/mismatch 均拒绝；有 cheat journal 无 binding 也拒绝；初始无 cheat 仍合法。
证据：`ReviewR2_zeroed_final_cheat_binding_must_fail_closed` PASS；tamper version+1 测试仍 PASS。

### D-R2-04 [已修复/MINOR] `_cheatRequestBindings` 无界

修复：容量 8192；满载拒绝新请求（不 evict），durable recovery 重建受同一上限；旧回执仍可重放。文档记录保留策略；不新增持久 receipt store。
证据：代码审计、identity/scope/replay 回归；容量耗尽负例在 `CHEAT_RECEIPT_CAPACITY_EXCEEDED` 契约下可复现。

### D-R2-05 [已修复/MINOR] 文档过时计数

修复：§6/§9 统一为最终实测计数（见下），D-R2 FAIL 单列，未升格 G1/G5。
证据：本文件与 `r16-*` 日志。

### D-R1-01 [已修复/PASS] 所有自动摸牌来源统一受保护

原缺陷：riichi/response Pass/timeout/AI/kan replacement 不走同一 protection/delivery/window 逻辑。
修复：见 §7；`Prepare(command,predicate)` 是唯一自动摸牌入口，timeout/AI 走 `ApplyInternalCommandAsync` 的同一组合，`DrawReplacement` 支持受保护墙尾且不破坏 136。
证据：4 个新增 actor 测试 + 复核 timeout/pass 探针；`r7-domain.log`、`r7-table-runtime.log`、`r2-review-probes.log`。

### D-R1-02 [已修复/PASS] immediate/beam offer 生命周期

原缺陷：discard/terminal 后旧 global offer 残留，阻塞后续激活。
修复：`PrepareCommandWithOfferSettlement`/`PrepareDiscardDelivery` 在同一 command version 内按 P1.4 fallback 结算；terminal checkpoint/outbox 同步。
证据：3 个新增测试（含 durable terminal）+ 复核 immediate discard 探针。

### D-R1-03 [已修复/PASS] 双 request id 绕过扣费身份

原缺陷：inner/outer 不一致时 outer 收回执、inner 记账，两个标记共用一笔扣费。
修复：actor 入口硬校验一致性，拒绝且无 Domain 调用。
证据：`inner_and_outer_request_ids_must_match_before_domain_execution`；复核探针 PASS（balance=1000、ledger=1、markGroups=1）。

### D-R1-04 [已修复/PASS，原 D-N1b-04 升级] 回执隔离/业务拒绝重放

原缺陷：只按 RequestId+SHA 缓存 accepted；拒绝不复用；跨 scope 返回旧 Accepted/私有事件。
修复：actor 级 `_cheatRequestBindings` 绑定 scope/API/SHA/结果；拒绝也缓存；跨 scope 拒绝；异 API/SHA Conflict；recovery 从 journal 重建绑定；commit 失败不写缓存。
证据：两个新增测试 + 复核两探针；`old_owner_epoch...` 同时覆盖并发（恰好 1 version）。

### D-R1-05 [已修复/PASS，原 D-N1b-03] 完整 cheat 聚合顶层绑定

原缺陷：`table_state` 只绑定 round，cheat-only 变化不可由 round hash 检出。
修复：`TableStateRow.CheatVersion/CheatFingerprint` + `TableDurableCommitRequest.NextCheat*` + recovery 逐字比较；terminal checkpoint 使用候选 cheat 聚合；tamper/fault 负例。
证据：`durable_cheat_binding_is_persisted_and_tamper_recovery_is_refused`（篡改后 `RecoveryStateHashMismatch`）、`durable_commit_fault...`；正常 checkpoint/journal 恢复 byte/fingerprint/version 一致。

### D-R1-06 [BLOCKED，阶段边界] 协议 `CommandKind`→actor 宿主入站映射

- **位置**：`Mahjong.LocalHost`（P4.1）/P5.1 TableService；actor 已有 typed 入口。
- **实际/期望**：协议 12..18、scope 签发、网络入站尚未接线；由宿主层把已认证 CommandKind+原始 SHA 映射到 `TableCheatCommandEnvelope`。
- **证据/修复**：review §4.2 通道④；由 P4.1/P5.1 owner 接，端到端测试覆盖。
- **判定**：不阻断本轮 actor 组件结论，但阻断“网络/宿主全链路 N-1b”。

### D-R1-07 [INFO，保留] forced-state Rejected 回执字段

按复核要求保留冻结 `Receipt` 字段：forced FIRST 仍返回 business Rejected，不伪造 commandVersion/eventSeq；提交版本由 journal/事件/`TableStateRow` audit 关联。不得误报 Applied。

### D-R1-08 [INFO] checkpoint/history 子 schema 增版

`TableCheatLedgerCheckpointCodec=4`、`TableCommittedHistoryCodec=2`，旧子 schema 明确拒绝；顶层 checkpoint schema 仍 2。无自动迁移工具。

### D-R1-09 [INFO] `RoundEventType.CheatApplied` 只做 provenance

不映射为协议 20/21，不进入 public snapshot；private 20/21 由 `TableCheatEventRecord` 投影。

### D-R1-10 [NOT_RUN] PG/WSS/Unity/IL2CPP/Android/macOS/真实设备/跨物理网络

仅 InMemoryTableStore/内存 lease 的 durable 语义测试；不得把内存 PASS 写成 PG/远端通过。G1/G5 未批准/未完成。

---

## 9. 未宣称完成

- G1 **未批准**；G5 **未完成**；V4 仍是唯一生产架构；无 `complete.flag`。
- 组件面通过 Domain 346 / TableRuntime 352 / Contracts-Wire 149（0 fail/0 skip）与复核 16/16；D-R1-06 宿主全链路、PG/WSS/Unity/设备/网络为阶段外，NOT_RUN。
- 未执行 push、PR、merge、远端 CI、默认分支/部署/全局环境/转派；caller 独占复核/隔离集成/push/MR/merge-back。

---

## 10. 复现命令

```powershell
Set-Location D:/majiang-wt/n1b-cheat-execution
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test C:/Users/sncdc/AppData/Local/Temp/codex-n1b-review-b429c7bd/probes/Review.csproj -c Release -- --filter-method '*Review_*'
git diff --check
git status --porcelain=v1
```
