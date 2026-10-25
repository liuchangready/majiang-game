# V5 G1 N-1b：七个出千命令的真实权威执行接线

日期：2026-10-25
工作树：`D:/majiang-wt/n1b-cheat-execution`，分支 `wt/v5-n1b-cheat-execution`
冻结基线：`3bfe952cc7adb9bdc2f5bb0a7589a26ec731886b`
（已核实 `git rev-parse --show-toplevel` / `HEAD` / `--abbrev-ref HEAD` / `status --porcelain`；基线是 PR19 `feat/v5-1-integration -> v5` 的已发布头，含已验收 N-1a。）

> **结论边界（必读）**：本次完成 N-1b 的 **actor 权威执行层与恢复/投影/回执组件**，不是 N-1b 整体无缺口通过，也不批准 G1/G5。仍有两个真实边界：D-N1b-01（RIICHI 自动摸牌与杠后补牌未跳过受保护预约牌）为 **BLOCKED**；D-N1b-02（协议 `CommandKind`→actor 的宿主入站映射）属 P4.1/P5.1 后阶段。V4 仍是唯一生产架构；不创建 `complete.flag`。

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
- `TableCheatLedgerCheckpointCodec` 升为 **3**，持久化完整 CheatState：ledger、tile marks（round registry/group/overlap）、sleeve 的 immediate/off-turn offer/window/reservation/windowSeq/usedOnTurn、beam 的 candidates/eligible hand/drawn/usedOnTurn，以及总 version/fingerprint；旧 schema 2 明确拒绝。
- `TableCommittedHistoryCodec` 升为 **2**，持久化 cheat 事件 record（typed `CheatEvent` + offer/keep/action/result/hand/target/turn）与统一 seq/version/ordinal；旧 schema 1 明确拒绝。
- checkpoint 顶层 schema 仍为 **2**；旧子 payload schema 由上述 codec 明确拒绝，不做静默补默认。
- 冷恢复不生成新 request id、不二次扣费/换牌；恢复后的旧 `recoveryGeneration` 命令被 `_strictRecoveryFence` 拒绝。

---

## 4. 生命周期与投影/隐私

- **discard**：正常打牌后为打牌者打开 off-turn window；受保护摸牌在普通 discard 的自动摸牌路径跳过受保护 tile。
- **immediate offer**：超时由权威 tick 调 `ExpireSleeveBloomImmediate`（强制 FIRST），消费该 decision 的 timing；不是玩家绕过 deadline。
- **off-turn offer**：tick 内 `ExpireOffTurnSleeveBlooms` 强制 FIRST；逾期人工 choice 仍按权威 deadline 拒绝/或由 P1.4 `RejectedWithState` 持久化强制 FIRST。
- **预约交付**：下一次真实普通摸牌（显式 `DrawTile` 或 discard 后自动摸牌）在**同一 command transaction**内交付；`TileDrawn` 公开事件 + owner-scoped `SleeveBloomReservationDelivered`，不二次扣费。
- **受保护墙牌**：`TilePositionMap.DrawFromWall(seat, isProtected)` 与 `RoundStateMachine.PrepareDrawProtected/PrepareDiscardProtected` 让普通摸牌/discard 自动摸牌跳过 off-turn 候选/预约牌，保持 136 张实体守恒。这是 N-1b 必要的 Domain 最小桥接。
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
| `TableCheckpointCodecs.cs` | 完整 cheat checkpoint schema 3、cheat command schema 1、system journal schema 1 |
| `TableCheckpointValidator.cs` | 过滤 cheat history、captured prefix 校验 |
| `TableRecoveryPayloads.cs` | committed history schema 2（含 typed cheat event） |
| `TableRecoveryService.cs` | 重放 schema 2/3、reservation draw/discard delivery、受保护摸牌、cheat 配置 |
| `TableDurableContracts.cs` | rejected-with-state commit 语义 |
| `TableRuntimePorts.cs` | `TableCommitRequest` rejected-with-state |
| `InMemoryTableStore.cs` | rejected-with-state 原子状态推进 + rejected receipt |
| `TableRuntimeReasonCode.cs` | cheat 稳定 reason code 映射 |
| `TableVisibilityProjection.cs` | recovered event facts 对 cheat/bridge 的覆盖规则 |

### Tests / Doc

| 文件 | 变更 |
|---|---|
| `dotnet/tests/Mahjong.TableRuntime.Tests/N1bCheatExecutionTests.cs`（新） | 19 个真实 actor/durable/wire 测试 |
| `doc/v5-重构/V5-G1-N1B-CHEAT-EXECUTION.md`（本文件） | 交付、证据、缺口 |

**范围扩大报告（准确路径/原因）**：名义 S-N1b 只列 `TableRuntimeContracts.cs`/`TableActor.cs`/`TableProjectionContractMapper.cs` + tests。实际额外修改的路径是：

- `RoundCheatTransition.cs`、`RoundStateMachine.cs`、`RoundTransition.cs`、`TilePositionMap.cs`：N-1b 必须有 Domain 纯 prepare/安装、受保护摸牌、桥事件；不把外部 map/状态直接塞进 actor。
- `Cheat/CheatState.cs`、`TileMark.cs`、`SleeveBloom.cs`、`BeamSwap.cs`：完整 cheat checkpoint/恢复需要受验证的恢复构造器；不修改 P1.4 的费用/物理/窗口规则。
- `TableActorMessages.cs`、`TableRuntimeReasonCode.cs`：typed envelope 进入同一 mailbox 与稳定拒绝码。
- `TableCheckpointCodecs.cs`、`TableCheckpointValidator.cs`、`TableRecoveryPayloads.cs`、`TableRecoveryService.cs`、`TableDurableContracts.cs`、`TableRuntimePorts.cs`、`InMemoryTableStore.cs`、`TableVisibilityProjection.cs`：第 6 条要求的 journal/checkpoint/冷恢复/旧 schema 拒绝/rejected-with-state/恢复 facts 校验；不新增第二套 receipt store。
- `TableProjectionContractMapper.cs` 未改：既有 20/21 payload 分支与 facts 字段已足够，本次只补“实际事件→facts”的来源。

未改：`AGENTS.md`、`CLAUDE.md`、`PROMPTS.md`、任务书/协议、`unity/`、`Engine/Resources`、Burst、`tests/v5-golden/*`、任何冻结 fixture/catalog/verifier/GoldenTests，以及 N-2/N-3 在途文件。

---

## 6. 验证（真实 exit/计数/零 skip/日志/SHA256）

工作树：`D:/majiang-wt/n1b-cheat-execution`；CLI：`D:/Unity/dotnet/dotnet.exe`；日志目录（独占）：`C:/Users/sncdc/AppData/Local/Temp/n1b-cheat-execution-0f3b9d6c/`。

| 验证 | 命令 | 实际 | exit |
|---|---|---|---|
| Release solution | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental` | 12/12 项目，**0 warnings / 0 errors** | 0 |
| Domain 全量 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build` | **346 passed / 0 failed / 0 skipped / 346 total** | 0 |
| TableRuntime 全量 | `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build` | **335 passed / 0 failed / 0 skipped / 335 total** | 0 |
| Contracts/Wire 回归 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --no-build` | **149 passed / 0 failed / 0 skipped / 149 total** | 0 |
| diff 格式 | `git diff --check` | 无输出 | 0 |

基线对照：Domain 346（N1b 未新增 Domain 测试文件）；TableRuntime 316 → **335（+19 N1b 用例）**；Contracts 149。

日志 SHA256（完整小写；最后两次构建后重跑）：

| 文件 | SHA256 | 字节 |
|---|---|---|
| `n1b-head-build.log` | `0f7d9aaee51c6b584e3530c8970e40d9e8bfbe150b9265e311356e24fe60a7ed` | 3468 |
| `n1b-head-domain.log` | `472e67e0c8f0da102a09ca900f639e8c731ef41016b00bc6e7652782c66f7d68` | 728 |
| `n1b-head-table-runtime.log` | `5c3febc8662a8c77d8c1ad22033a420e76cd038c4ca394fcc8c6c7f9653bbec8` | 788 |
| `n1b-head-contracts.log` | `8835a34b9b787612b6bf5107d767a8ae05a5693c2c759e10c76e9dd0f3c9aced` | 758 |

源码 SHA256（提交前）：

| 文件 | SHA256 |
|---|---|
| `RoundCheatTransition.cs` | `3c8b6b54f89b385c1f50bc0aeacd32df1cc241b21c5830e1eae6302267491f36` |
| `TableActor.cs` | `97fd2c02ee2838ac28a9511e26d96799e9bd5b915ae87fafaf43a6f0219948f9` |
| `TableRuntimeContracts.cs` | `a9d83a7ae051420915b53c8d4f88c24fd17989d41de037222d3657be5d65a230` |
| `TableCheckpointCodecs.cs` | `a97a86007510e6b7f4e219d521a9ad405efa47e443efd72db2e1f3341cf07c55` |
| `TableRecoveryService.cs` | `b13438037b8f5e94b5c601a9e6a0b952c575f050e73490ee1f0b86d789ecd24e` |
| `N1bCheatExecutionTests.cs` | `5d4445ded0f0fb40761e6fc2a7088d3b47359c3e3c346481914957c3ecd95517` |

---

## 7. 新增测试覆盖（19 项，真实 actor）

`N1bCheatExecutionTests.cs` 覆盖：

1. tile mark 成功：1 version、bridge+CheatPointsChanged+TileMarkApplied、扣费一次；重复同 request 原回执。
2. 同 request 不同 payload SHA → Conflict，版本不变。
3. 错 owner/control/expectedVersion/round 拒绝且零版本。
4. immediate sleeve 激活/选择：真实 offerId、keep、result tile、余额、stale offer 拒绝。
5. beam 激活/KeepOriginal：真实 BeamSwapResolved。
6. off-turn 逾期人工 choice：返回 Rejected、状态推进一次、强制 FIRST 事件；同 request 重放原拒绝。
7. 权威 tick 的 off-turn sweep 强制 FIRST；逾期人工 choice 被拒且版本不变。
8. commit 注入失败零副作用；重试只扣一次。
9. 终态 round 拒绝所有 cheat。
10. 错误座位 tile mark → NOT_YOUR_TURN、零版本。
11. pending decision 绑定：错 decision 拒绝、逾期拒绝、版本不变。
12. 已接受 cheat 命令不静默消费无关 decision（timing snapshot 仍保留 d1）。
13. beam SWAP：真实 hand/wall 实体交换、136 distinct、target slot 事件。
14. 受保护预约牌在正常 discard 自动摸牌中跳过并在下一次真实摸牌交付；不二次扣费。
15. durable checkpoint+journal 冷恢复：完整 cheat 状态、同 request Replayed、不重复扣费。
16. durable journal 恢复 off-turn reservation，随后交付一次。
17. durable **checkpoint 内含 off-turn reservation/candidates** 的保存/验证/冷恢复。
18. owner stream 收到 typed 20 + CheatPointsChanged；其他三席/spectator 无私有事件/offerId；owner 私有余额 990。
19. beam SWAP 的 typed Wire 21：offerId/action/handTile/targetSlot。

Domain 的既有 346 项（含 N-1a 的 Chi/Pon/Kan/Riichi/fingerprint/recovery、P1.4 CheatGoldenTests、136 守恒）全部保持通过。

---

## 8. 缺陷/缺口清单（一次全报；PASS/FAIL/BLOCKED/NOT_RUN）

### D-N1b-01 [BLOCKED] RIICHI 自动摸牌与杠后补牌不跳过受保护预约/候选牌

- **位置**：`dotnet/src/Mahjong.Domain/RoundStateMachine.cs` 的 `PrepareRiichi`（调用 `AdvanceToNextDraw` 未传 `isProtectedWallTile`）与 `PrepareOpenKan/PrepareClosedKan/PrepareAddedKan`（`Tiles.DrawReplacement` 无 protection 参数）；`dotnet/src/Mahjong.Domain/TilePositionMap.cs` `DrawReplacement`。
- **复现**：durable/in-memory actor，先 off-turn 预约 FIRST 使某张墙牌进入 `IsProtectedTile`，再在不改变该牌位置的局面下声明立直且无可鸣牌座位，或执行杠（岭上补牌从墙尾取牌）；随后尝试交付。
- **实际**：普通 `DrawTile`/`DiscardTile` 自动摸牌已跳过；RIICHI 自动摸牌/杠后 replacement 仍可能摸走受保护牌，之后 `DeliverSleeveBloomReservation` 返回 `CHEAT_STALE_OFFER`（“reserved tile is no longer in the live wall”），预约丢失。
- **期望**：所有 live-wall 正常摸牌、discard 自动摸牌、RIICHI 自动摸牌、`DrawReplacement` 都使用同一 `IsProtectedTile` 谓词；预约/候选不会被任何普通摸牌消费。
- **证据**：源码调用点；本次 19 项测试覆盖 normal draw/discard delivery，未覆盖 riichi/kan。`n1b-head-table-runtime.log`（335 pass）不改变该结论。
- **修复要求**：给 `PrepareRiichi`/`DrawReplacement`/kan claim 路径线程化 protection predicate；补 actor 级 riichi/kan 预约交付测试；不得用 swap/重建 map 绕过。
- **验收断言**：预约 FIRST 后，任意座位立直/明杠/暗杠/加杠导致的自动摸牌后，预约仍在墙中；下一次真实摸牌交付成功；136 unique、余额只扣一次。

### D-N1b-02 [BLOCKED，阶段边界] `CommandKind`→actor 的宿主入站映射未接

- **位置**：`dotnet/src/Mahjong.LocalHost`（0 .cs，P4.1）与 P5.1 TableService 入站适配；actor 已有 `SubmitCheatAsync(TableCheatCommandEnvelope)`。
- **实际**：协议 `CommandKind` 12..18 到 `RoundCheatAction` 的宿主映射、鉴权 scope 签发、网络入站尚未接线；本次只完成 actor 权威入口和 typed envelope。
- **期望**：P4.1/P5.1 在宿主层把已认证 `CommandKind` + 原始业务 bytes/SHA 映射到 `TableCheatCommandEnvelope`；不得在 actor 内暴露任意 reducer。
- **证据**：`Mahjong.LocalHost` 仍空壳；review §4.2 通道④属后阶段。
- **修复/验收**：由 P4.1/P5.1 owner 接；端到端测试覆盖 7 CommandKind、scope/API/SHA、回包/重放。

### D-N1b-03 [MAJOR，恢复加固] 顶层 `table_state` 未绑定最终 cheat fingerprint/version

- **位置**：`dotnet/src/Mahjong.TableRuntime/TableRecoveryService.cs` 重放结束处；`TableStateRow`/checkpoint metadata。
- **实际**：恢复用 checkpoint `CheatPayloadSha256` 校验 checkpoint 内 cheat，再用确定性 journal 重放；未把最终 `CheatState.Version/Fingerprint` 与 `table_state` 或独立顶层哈希比较。若重放实现出现“内部一致但错误”的 cheat 结果，现有顶层 round `StateHash` 不会发现。
- **期望**：`table_state` 或 checkpoint metadata 持久化 cheat version/fingerprint（显式 schema 增版），恢复结束时逐字比较；旧 schema 明确拒绝。
- **证据**：`TableActor.BuildCheckpoint` 写完整 cheat payload；`TableRecoveryService` 仅比较 round hash/fingerprint。
- **修复/验收**：新增顶层 cheat hash/version 字段+迁移/拒绝策略；冷恢复损坏注入被拒；正常恢复 byte-equal。

### D-N1b-04 [MINOR] in-memory 只缓存“已提交”的 cheat 回执，业务拒绝不缓存

- **位置**：`TableActor.ProcessCheatCommandCoreAsync` 的 `_inMemoryCheatReceipts` 写入点；仅 accepted/rejected-with-state 写入。
- **实际**：同一 request 的纯业务拒绝（例如 NOT_YOUR_TURN）在 in-memory 模式会重新评估；durable 模式有 `CommitRejectedReceiptAsync`。
- **期望**：in-memory 也返回原拒绝回执，或明确要求 cheat 全部走 durable。
- **修复/验收**：缓存拒绝结果（含 payload SHA）或明文限制；同 request 同 SHA 返回原结果、异 SHA Conflict。

### D-N1b-05 [MINOR] forced-state Rejected receipt 不携带 commandVersion/eventSeq

- **位置**：`TableDurableContracts.cs` / `ProtocolReceipt.ReceiptStatus.Rejected`；`CommitCheatAndInstallAsync` 的 rejected-with-state 分支。
- **实际**：调用者收到 Rejected，receipt 的 `EvaluatedStateVersion` 为旧版本、`CommandVersion/EventSeq` 为空；forced FIRST 的状态/事件已提交，但 receipt 字段无法证明新版本。
- **期望**：在冻结 receipt schema 允许的前提下，forced-state 使用可表达“已提交但业务拒绝”的显式状态/扩展字段；不得误报 Applied。
- **修复/验收**：schema 增版或显式 audit 字段；重放/查询仍返回 Rejected 且能定位 submitted version/event。

### D-N1b-06 [INFO] checkpoint/history 子 schema 显式增版，无旧版迁移工具

- **位置**：`TableCheatLedgerCheckpointCodec.FormatVersion=3`、`TableCommittedHistoryCodec.FormatVersion=2`；顶层 checkpoint schema 仍 2。
- **实际**：旧 cheat/history payload 明确 `InvalidDataException`；没有自动迁移。
- **期望/说明**：这是有意的 fail-closed 策略，符合“旧 schema 拒绝”；升级历史 checkpoint 需单独迁移工具，本次未做。

### D-N1b-07 [INFO] `RoundEventType.CheatApplied` 只做 provenance，不映射协议事件

- **位置**：`RoundTransition.cs:19`、`TableEventProjection.FromRoundEvent`。
- **说明**：每个 cheat command 需要一个 round event 来绑定版本/ordinal；它不重命名为 20/21，也不进入 public snapshot。协议 20/21 由 owner-scoped cheat record 投影。golden catalog 未改。

### D-N1b-08 [NOT_RUN] PG/WSS/Unity/IL2CPP/Android/macOS/真实设备/跨物理网络

- **说明**：本次只使用现有 `InMemoryTableStore`/`InMemoryRequestScopeAuthority`/内存 lease 做 durable 语义测试；没有 PG 生产实现、WSS 远端、Unity/IL2CPP、设备或真实网络证据。不得把内存 PASS 写成 PG/远端通过。G1/G5 仍未批准/未完成。

### D-N1b-09 [INFO] `TableSnapshot.RecentEvents` 过滤 owner-private cheat 事件

- **位置**：`TableActor.AppendRecent` / `ProcessCompleteRecoveryAsync` recent window。
- **说明**：为避免 authority snapshot 泄露余额/候选/账本，私有 cheat 事件只保留在 committed log 供 seat stream/checkpoint 使用；宿主若从 `TableSnapshot.RecentEvents` 取私有事件会看不到，应改用 seat stream。

---

## 9. 未宣称完成

- G1 **未批准**；G5 **未完成**；V4 仍是唯一生产架构。
- N-1b 的 actor/typed execution/recovery/projection 组件通过本次 335 + 346 + 149 测试；但 D-N1b-01 使“预约后任意真实摸牌交付”仍有 BLOCKED 缺口，D-N1b-02 属 P4.1/P5.1，不能宣告 N-1b 全闭环。
- 未执行 push、PR、merge、远端 CI、默认分支/部署/全局环境/转派；caller 独占复核/隔离集成/push/MR/merge-back。

---

## 10. 复现命令

```powershell
Set-Location D:/majiang-wt/n1b-cheat-execution
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build
& D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --no-build
git diff --check
git status --porcelain=v1
```
