# V5 TableRuntime 响应窗口失效与鸣牌墙证据恢复交付

日期：2026-10-29（第 2 轮返修后）
工作树：`D:/majiang-wt/runtime-response-kan-ds-20261029`
分支：`wt/v5-runtime-response-kan-ds-20261029`
冻结基线：`183e378da273081075a5eb54472ffe6dfd29f670`
代码提交：`662e1bb949904eb269f847ac083719843ed67554`（第 1 轮）、`aa37074`（第 2 轮批量返修）
文档提交：本轮 docs commit（`git log -1`）
状态：**本任务自身范围的两项缺陷 + 第 2 轮 4 项复核缺陷已修复并本地验证；不是 G2/G3/G4/G5 通过，不是真实 PG/WSS 验收，不是发布。**

---

## 0. 结论摘要

| 项 | 结论 |
|---|---|
| D-TR-RESP-01 / MAJOR（第 1 轮） | **PASS（本地 TableRuntime）**：closed-window pending/clock 原子失效；失败/取消/租约拒绝零消耗。 |
| D-TR-KAN-02 / MAJOR（第 1 轮） | **PASS（本地 TableRuntime）**：按 Domain 物理墙/死山补位模型验证 Open/Closed/Added/多杠；合法 checkpoint 与真实内存冷恢复通过。 |
| D-R1-RESP-KAN-01 / MAJOR | **PASS**：恢复过滤器不再把 `Kan` 一概视为 turn；非当前座的 `[Kan,Pass]` 过期响应被清除，当前合法 turn decision 保留。 |
| D-R1-RECOVERY-FENCE-02 / MAJOR | **PASS**：先对全部原始 recovered decisions 执行 finished/roundId/重复座位/controlEpoch/timing 校验，再做过期对账；错代次仍拒绝且保持 Recovering。 |
| D-R1-RESP-CHEAT-03 / MAJOR | **PASS**：非 durable `CommitCheatAndInstallAsync` 走同一 candidateClock/pendingAfter 原子安装；deferred SleeveBloom delivery 成功后清除 closed-window peers。 |
| D-R1-KAN-EVIDENCE-04 / MAJOR | **PASS（本任务内存范围）**：新增真实 `InMemoryTableStore` + `TableRecoveryService` + actor `CompleteRecoveryAsync` 的 checkpoint0+tail / final checkpoint 恢复，覆盖 Open/Closed/Added(chankan Pass)/多杠/chankan Ron 终态；墙耗尽/最后可补牌明确 NOT_RUN。 |
| 正式全量 | **426 / 427 PASS，1 FAIL**：唯一失败为黄金 source-hash pin；未刷新 pin/摘要。 |
| 独立复核探针 | **3 / 3 PASS**（同一 fresh 生产源码 + 探针 targets；未改 tracked 生产/测试/fixture）。 |
| 真实 PG/WSS 新进程 | **NOT_RUN**：由原 93fb/runtime delta 集成后执行；不宣称其 49 项。 |
| 跨模块 TableService provider | **BLOCKED**：`RinshanDrawn` 反向 wall 计数缺失；真实服务 ClosedKan/AddedKan/多杠在普通摸牌后仍会被 fail closed，需原 owner 修 provider（不在本任务范围）。 |
| G1 / G2–G5 | G1 仍 APPROVED；G2–G5 未批准；V4 唯一生产基线；181 classified 未覆盖未动。 |

---

## 1. 范围与冻结基线

### 1.1 实际改动文件

生产代码（仅 `Mahjong.TableRuntime`）：

- `dotnet/src/Mahjong.TableRuntime/TableActor.cs`
- `dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs`（第 1 轮）
- `dotnet/src/Mahjong.TableRuntime/TableResponseDecisionReconciliation.cs`
- `dotnet/src/Mahjong.TableRuntime/TableKanWallEvidence.cs`（第 1 轮）

测试（新增，未修改既有兄弟测试）：

- `dotnet/tests/Mahjong.TableRuntime.Tests/TableResponseDecisionInvalidationTests.cs`
- `dotnet/tests/Mahjong.TableRuntime.Tests/TableKanRecoveryEvidenceTests.cs`

文档：

- `doc/v5-重构/V5-TABLERUNTIME-RESPONSE-KAN-RECOVERY-DELIVERY.md`

未修改 `sln`、统一 verifier/架构白名单、CI、scheduler、Unity、`Domain`、`Contracts`、`Contracts.Wire`、`Infrastructure`、`TableService`、`LocalHost` 源码、黄金 fixture/pins、机器信任/全局 PATH/共享服务。未 push/建 PR/MR/merge/deploy/写 `complete.flag`。

### 1.2 工作树/祖先/清洁

```text
HEAD = 本轮 docs commit（代码 aa37074）
git merge-base --is-ancestor 183e378 HEAD  -> exit 0
git diff --check 183e378..HEAD             -> exit 0
git status --porcelain=v1                  -> 0 lines (tracked clean)
```

未从旧 main/关闭的 PR19 source 创建；未合入未接受 receiver 代码；兄弟树只读。

---

## 2. 第 2 轮四项复核缺陷：逐条映射

### 2.1 D-R1-RESP-KAN-01（MAJOR）：恢复过滤器误保留过期 OpenKan 响应

**位置**：`TableResponseDecisionReconciliation.cs` 的 `IsStillValid`/`HasTurnAnchor`；`TableActor.cs` 恢复安装段。

**复现**：
- 独立探针：`CodexIndependentReviewProbes.StaleLegalOpenKanResponseMustNotReturnAfterRecovery`（`probes.xml`）。
- 正式化：`TableResponseDecisionInvalidationTests.RecoveredStaleOpenKanResponseIsDropped`。
- 真实 136 实体：seat1 有 Man1+Man2（Chi），seat2 有 Man3 三张（OpenKan），seat0 弃 Man3 第四张，`eligible=[1,2]`；先证明 Domain OpenKan seat2 合法，再 Chi seat1 + Pass seat2 关闭窗口，恢复 seat2 的 `[Kan,Pass]` decision（seat2 非当前座）。

**修复前实际**：恢复 `Configured`，`seat2.PendingKey = round/2/stale-openkan`、`Timing` 仍在；`Assert.Null` 失败。
**修复后实际**：恢复 `Configured`，`seats[2].PendingKey=null`、`Timing=null`。
**期望**：关闭窗口的所有响应决策（含 OpenKan 响应候选）不可安装；当前座真正合法 turn Kan/暗杠/加杠/出牌必须保留。

**修复**：
- 无窗口时，非当前座的任何 decision 一律视为过期响应并清除（OpenKan 不再因 `Kan` 被误判为 turn）。
- 当前座仅保留带 turn/settlement anchor（`Discard/Tsumo/Riichi/Cheat/Other`）的 decision；`[Kan,Pass]` 无 anchor 被清除；真实 turn Kan 总是带 Discard 候选，不会被误删。
- 过滤发生在原始恢复行通过全部结构/身份/代次/时间校验之后。

**验收断言**：探针 PASS；正式测试 PASS；`OpenKan/Chi/Pon/Ron/Pass` 过期响应全部清除，窗口仍开放时未响应座位保留，当前合法 turn decision 保留；恢复后可开新决策并继续动作。

---

### 2.2 D-R1-RECOVERY-FENCE-02（MAJOR）：过滤器先执行导致 controlEpoch 校验被绕过

**位置**：`TableActor.cs` `ProcessCompleteRecoveryAsync` 恢复决策段。

**复现**：
- 探针：`StaleResponseMustNotBypassRecoveryControlEpochValidation`。
- 正式：`TableResponseDecisionInvalidationTests.RecoveredInvalidControlEpochStillRejectsBeforeFiltering`。
- 同一合法关闭窗口，恢复 `[Pon,Pass]` decision，`ControlEpoch=99`，control baseline epoch=0。

**修复前实际**：返回 `Configured`；过滤器先删除该 decision，后续 `candidateControl.GetEpoch` 校验永不执行。
**修复后实际**：`Rejected / RECOVERY_TIMING_INVALID`，actor 保持 `Recovering`，无部分安装。
**期望**：所有原始恢复输入先通过 finished/roundId/重复座位/controlEpoch/timing 等 fail-closed 校验，再做允许的过期对账。

**修复**：
- 恢复循环先遍历 **原始** `baseline.Timing.Decisions`，逐行执行原有 finished/roundId/duplicate/controlEpoch/`RestoreDecision` 校验并安装到 candidate；
- 随后才调用 `FilterRecovered`，对 kept 集之外的 decision 在 candidateClock 上 `Cancel` 并从 pending 移除；
- 无效行在过滤前即拒绝，不会因“本来会被清掉”而绕过围栏。

**验收断言**：探针 PASS；正式测试 PASS；错 roundId/错 controlEpoch/重复座位/非法 timing/终态带 open decision 仍拒绝且无部分安装；正确代次的合法 stale decision 才清理；既有恢复全量不回退。

---

### 2.3 D-R1-RESP-CHEAT-03（MAJOR）：非 durable cheat/deferred-delivery 未做响应对账

**位置**：`TableActor.cs` `CommitCheatAndInstallAsync` 非 durable 分支、`InstallCommittedCheat`。

**复现**：
- 探针：`NonDurableDeferredSleeveDeliveryMustClearClosedWindowPeers`。
- 正式：`TableResponseDecisionInvalidationTests.NonDurableDeferredSleeveDeliveryClearsClosedWindowPeers`。

真实双 eligible 窗口，给 next seat1 合法袖里藏花 wall reservation；seat1 Pass 后重新打开已响应 seat1 的 `stale1`，seat2 deadline timeout Pass，走 `PrepareDeferredDrawDelivery -> CommitCheatAndInstallAsync -> InstallCommittedCheat`。

**修复前实际**：timeout 提交 Accepted、补牌成功、窗口已闭，但 `seat1.PendingKey=round/1/stale1` 与 Timing 仍在。
**修复后实际**：`SleeveBloomReservationDelivered` 已实际收到；`seat1.PendingKey=null`、`Timing=null`。
**期望**：普通 round commit 与 cheat/deferred-delivery commit 共享同一原子 pending/timing 失效语义；失败/取消/租约拒绝零消耗。

**修复**：
- 在 `CommitCheatAndInstallAsync` 的 commit 之前构建 `cheatClock`/`cheatPendingAfter`（含 consumedSeat Complete/Expire 与 `FilterAfterTransition`）；
- 非 durable commit 成功后由 `InstallCommittedCheat` 一次安装 clock/pendingAfter，不再提交后重复 Complete/Expire/Remove；
- durable 分支复用同一 candidate，删除重复计算；
- commit 失败/取消/租约拒绝时不触碰 live clock/pending。

**验收断言**：探针 PASS；正式测试 PASS；reservation 事件/版本/136 实体不重复；可控 commit port failure/cancel/stale owner 时 pending/reserve/deadline 不变；开放窗口其他合法响应保留。

---

### 2.4 D-R1-KAN-EVIDENCE-04（MAJOR）：真实冷恢复与 checkpoint0+tail 证据未落实

**位置**：`TableKanRecoveryEvidenceTests.cs`；原文档 §3.4/§6/§8 口径过宽。

**修复前实际**：仅 `history.Skip` 后调用 `ValidateRecoveredEventFacts`；没有 checkpoint0/journal tail/TableRecoveryService/actor 安装；`LegalKanCheckpoint...` 只对 OpenKan 调 `TryValidate`；文档却写“OpenKan/ClosedKan/Added/多杠/checkpoint0+tail 均通过 TableCheckpointValidator”，超出实际断言。
**修复后实际**：新增真实内存恢复链（见 §3），用 `InMemoryTableStore` + `TableRecoveryService.RecoverAsync` + 新 actor `BeginRecoveryAsync/CompleteRecoveryAsync`，断言 state/source fingerprint/history/facts/136/pending/timing/后续命令一致；文档口径改为实际执行内容。

**未覆盖并明确 NOT_RUN**：墙耗尽/最后可补牌边界、真实 PG/WSS 新进程、真实设备/整场。原因：需要构造接近墙尾的 kan 物理牌局与真实服务进程；本任务不伪造。抢杠 Ron 终态已通过纯事实 + 真实 checkpoint0+tail 恢复覆盖。

---

## 3. 真实恢复矩阵（本任务内存范围）

新增测试实际执行：

| 形态 | checkpoint0+tail 真实恢复 | final checkpoint 真实恢复 | 断言 |
|---|---|---|---|
| OpenKan + 普通摸牌 | PASS | PASS | state/fingerprint、136、pending/timing、恢复后弃牌 Accepted |
| ClosedKan + 普通摸牌 | PASS | — | 同上 |
| AddedKan + chankan window + Pass | PASS | — | 同上 |
| 多杠（OpenKan + ClosedKan） | PASS | — | 同上；KanCount=2、RinshanPointer=2 |
| AddedKan + chankan Ron 终态 | PASS | — | terminal baseline 安装、pending 清空、不继续动作 |
| 合法 checkpoint 全套校验 | PASS（OpenKan `TryValidate`） | — | 历史/binding/facts/136 |

实现方式：

1. `BuildZeroCheckpoint`：真实 136 实体初始 round 的 version-0 checkpoint（无 history，control/timing/cheat/facts 完整）。
2. 用真实 `TableActor` 提交全部 Domain 命令（含 discard/OpenKan/ClosedKan/AddedKan/Pass/Ron），由于不调用 SaveCheckpoint，形成 **checkpoint0 + journal/timing tail**。
3. `TableRecoveryService.RecoverAsync` 读取 checkpoint0 + tail（或 final checkpoint），返回真实 baseline。
4. 新 `TableActor` 执行 `BeginRecoveryAsync` + `CompleteRecoveryAsync(baseline)`，断言安装状态、136、pending/timing、source fingerprint。
5. 非终态恢复后再开一个合法 Discard decision 并提交，断言命令版本继续推进。

测试事实源 `RecoveryProjectionSource` 与生产 `TableProjectionContextSource` 分离，按真实物理墙模型为 `TileDrawn`/`RinshanDrawn` 自增，并逐事件提供 meld/loser/roundEnded 事实；未修改 TableService。

---

## 4. 验证命令与真实计数

### 4.1 fresh build（GUID 隔离）

```powershell
D:/Unity/dotnet/dotnet.exe build `
  dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj `
  --no-incremental --artifacts-path artifacts/response-kan-ds/r2-fresh-93ac749f10854e90a3d51b6f8c71bafd `
  -v:minimal
```

native exit 0；0 warnings / 0 errors。fresh DLL：
`artifacts/response-kan-ds/r2-fresh-93ac749f10854e90a3d51b6f8c71bafd/bin/Mahjong.TableRuntime.Tests/debug/Mahjong.TableRuntime.Tests.dll`

### 4.2 正式新增定向

| 套件 | native exit | total | passed | failed | skipped | errors | notRun |
|---|---:|---:|---:|---:|---:|---:|---:|
| `TableResponseDecisionInvalidationTests` | 0 | 10 | 10 | 0 | 0 | 0 | 0 |
| `TableKanRecoveryEvidenceTests` | 0 | 17 | 17 | 0 | 0 | 0 | 0 |

### 4.3 正式 fresh 全量

| native exit | total | passed | failed | skipped | errors | notRun |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 427 | 426 | 1 | 0 | 0 | 0 |

唯一失败：`GoldenMetaProvenanceAndCaseIdentityAreReal`。
- 原测试失败确认为 `tests/v5-golden/visibility-v1.json` 对 `TableVisibilityProjection.cs` 的 source hash 失配：
  - expected LF hash：`40a9fc8dc13d8562d26d70c5d8819c87913a8b5851e4a75aeee0b8f1c3a57685`
  - actual：`9b35b07e100aa0664e2a8131541cf57af873a4be12cc7841498b05ae86295ade`
- 按禁令未刷新黄金 pin/摘要、未弱化断言；由 caller 在最终验收后处理批次 provenance 门禁。

### 4.4 独立复核探针（同一 fresh 生产源码）

```powershell
$reviewDir='artifacts/codex-response-kan-review-d51afca323824ea4b854125d9d0f3cc8'
$targetsPath=(Resolve-Path "$reviewDir/ReviewProbes.targets").Path
D:/Unity/dotnet/dotnet.exe build `
  dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj `
  --no-incremental --artifacts-path artifacts/response-kan-ds/r2-probe-36a4420a597849e4b6b659b9846b8cd3/build `
  "-p:CustomAfterMicrosoftCommonTargets=$targetsPath" -v:minimal
D:/Unity/dotnet/dotnet.exe exec `
  artifacts/response-kan-ds/r2-probe-36a4420a597849e4b6b659b9846b8cd3/build/bin/Mahjong.TableRuntime.Tests/debug/Mahjong.TableRuntime.Tests.dll `
  -noColor -automated -class Mahjong.TableRuntime.Tests.CodexIndependentReviewProbes `
  -result-nunit artifacts/response-kan-ds/r2-probe-36a4420a597849e4b6b659b9846b8cd3/probes.xml
```

结果：build exit 0，0 warnings/0 errors；probes **3 total / 3 passed / 0 failed / 0 skipped / 0 notRun**，native exit 0。
未修改 tracked 生产/测试/fixture；探针通过 `CustomAfterMicrosoftCommonTargets` 追加到测试项目。

### 4.5 focused 回归

| 套件 | native exit | total/passed/failed |
|---|---:|---|
| Domain `RoundDecisionTests` | 0 | 24 / 24 / 0 |
| Domain `RoundStateMachineTests` | 0 | 23 / 23 / 0 |
| Architecture Release | 0 | 130 / 130 / 0 |
| Infrastructure | 1 | 42 / 1 / 41（`MAHJONG_P52_TEST_POSTGRES*` 未配置，BLOCKED） |

---

## 5. 响应/墙证据负例矩阵

| 负例/边界 | 用例 | 结果 |
|---|---|---|
| 过期 OpenKan 响应恢复 | `RecoveredStaleOpenKanResponseIsDropped` | PASS |
| 错 controlEpoch 恢复 | `RecoveredInvalidControlEpochStillRejectsBeforeFiltering` | PASS（拒绝且保持 Recovering） |
| 非 durable deferred SleeveBloom | `NonDurableDeferredSleeveDeliveryClearsClosedWindowPeers` | PASS |
| 窗口关闭 + stale decision | `ClosedWindowDropsStaleDecisionAndTimingInTheSameCommit` | PASS |
| 失败提交 | `FailedCommitDoesNotConsumeOrInvalidateAnyResponseDecision` | PASS |
| durable 提交失败 | `DurableCommitFaultLeavesPendingAndReserveUntouchedThenRetries` | PASS |
| timeout/AI | `TimeoutOnRemainingResponseSeatClosesWindow...`、`AiPass...` | PASS |
| 篡改 wallRemaining | `TamperedWallRemainingFailsClosed` | PASS |
| 缺 Rinshan | `MissingReplacementDrawFailsClosed` | PASS |
| 篡改 kan meld | `TamperedKanMeldFailsClosed` | PASS |
| history 乱序 | `ReorderedHistoryFailsClosed` | PASS |
| 合法 Open/Closed/Added/多杠/chankan Ron | 真实内存恢复矩阵 | PASS |
| 墙耗尽/最后可补牌 | — | **NOT_RUN**（未构造；不伪造） |
| 真实 PG/WSS/设备/整场 | — | **NOT_RUN/BLOCKED**（原 owner） |

---

## 6. 跨模块 BLOCKED：TableService 事实源旧反向墙模型

`dotnet/src/Mahjong.TableService/TableProjectionContextSource.cs:477-486` 的反向循环只对 `TileDrawn` 自增，缺少：

```csharp
else if (events[i].Type == RoundEventType.RinshanDrawn)
{
    wall++;
}
```

后果：
- “普通 TileDrawn 之后发生杠/岭上摸牌”的合法服务端 checkpoint，较早 TileDrawn 的 `wallRemaining` 会比实际 +1；本任务新验证器 fail closed。
- “第一弃即 OpenKan、杠前无普通 TileDrawn”的形态不产生该 +1；可望通过事实校验，但仍需 93fb 真实 PG 链复跑。
- 真实服务端 ClosedKan/AddedKan/多杠在普通摸牌后仍会被拒绝，直到 TableService owner 修 provider；本任务未越界修改 TableService。

最小路径：provider 反向循环加入 `RinshanDrawn` 的 `wall++`；同步测试 helper；由 93fb 跑真实 PG/WSS。本任务不冒充其 49 项。

---

## 7. 兼容性与安全

- 旧版本含 kan 的 checkpoint 从未被接受（修复前直接拒绝）；不存在“旧的合法 kan checkpoint”需要兼容。本修复不是放宽旧拒绝。
- 非 kan checkpoint 行为不变（`RinshanDrawn` 计数为 0）。
- schema 不变：`TableCheckpointFactory.SchemaVersion` 仍为 2；未改 Domain/Contracts。
- 公开暗杠走 `WithoutMeld` redaction；未把 owner 四张身份带入公共/他家校验。
- 未使用 sleep、未扩大 timeout、未删探针、未用旧 DLL、未 skip/空测试。fake clock 固定 1000/1500/2000；失败路径用 `FailNext`/`ArmedFaultPoint`/真实 commit port。
- 136/版本/事件/receipt：测试断言 136 assigned/0 unassigned、state_version 精确递增、失败零推进、恢复后继续动作；真实 PG 账本唯一性仍属 93fb，不宣称。

---

## 8. PASS / FAIL / BLOCKED / NOT_RUN

### PASS

- D-TR-RESP-01、D-TR-KAN-02、D-R1-RESP-KAN-01、D-R1-RECOVERY-FENCE-02、D-R1-RESP-CHEAT-03、D-R1-KAN-EVIDENCE-04（本任务内存范围）。
- 正式定向：response 10/10，kan 17/17。
- 独立探针：3/3。
- 正式全量：426/427。
- Domain focused 24/24、23/23；Architecture 130/130。
- `git status` tracked clean；`git diff --check 183e378..HEAD` exit 0。

### FAIL

- `GoldenMetaProvenanceAndCaseIdentityAreReal`：source hash pin 失配（expected/actual 见 §4.3）；**未刷新**，需 caller 验收后处理。

### BLOCKED

- TableService provider 的 `RinshanDrawn` 反向 wall 计数（§6），需原 owner 修。
- 真实 PG/WSS 新进程（93fb）。
- Infrastructure 41 项 PG 用例（`MAHJONG_P52_TEST_POSTGRES*` 未配置）。
- D-LH-GATE-02 控制回执角色阻塞仍由原 owner 处理；本任务未绕过。

### NOT_RUN

- 墙耗尽/最后可补牌真实 kan 边界（未构造，不伪造）。
- 真实四人对局整场、真实设备/Android/iOS/Unity/IL2CPP、跨物理网络、TLS/WSS 生产入口、容量/混沌、发布/灰度/回退。
- 真实 PostgreSQL `SaveCheckpointAsync`/冷恢复/账本唯一性。
- G2/G3/G4/G5 门槛；本任务不宣称阶段完成。

---

## 9. 证据与 SHA256 manifest

fresh 目录：`artifacts/response-kan-ds/r2-fresh-93ac749f10854e90a3d51b6f8c71bafd/`
probe 目录：`artifacts/response-kan-ds/r2-probe-36a4420a597849e4b6b659b9846b8cd3/`

关键 SHA256（完整清单见 `r2-manifest.json`）：

| 文件 | bytes | SHA256 |
|---|---:|---|
| `TableActor.cs` | 398546 | `80E6089AD2099017C2025B048AD2B261315E2E2A6F642E7CECDBD2B3940E83B6` |
| `TableResponseDecisionReconciliation.cs` | 7249 | `A0F240EE238FD6E066F32689EF0C5242C1B4AAAD131DEA24273E55890ED24646` |
| `TableKanWallEvidence.cs` | 16557 | `6E3967541CE665023131E7E6569843989FA314DB16BD8B67512BDAFC35B5C6CD` |
| `TableResponseDecisionInvalidationTests.cs` | 32444 | `9221B39C412C7D713C543C22A7B8C92AD292DDC48F1988258D0A76C46D4DF345` |
| `TableKanRecoveryEvidenceTests.cs` | 57991 | `612AB69CBD48722EF4523E44D32991831207DABA7F8944B7ED1C85811B562557` |
| fresh `build.log` | 4520 | `23C41F5BECA4268005E997DCD0031F46EAF03B4713494421DB8EA4B666A882B7` |
| fresh `response.xml` | 7660 | `B73B70BB8A0EEDA884B72625C6A33BF8F6A2D05E027467359AD863077A66A9EB` |
| fresh `kan.xml` | 10389 | `375956449CF61960DE13F9A58B73E40935EF392122BA0FC287B97C262F70F327` |
| fresh `full.xml` | 263714 | `18382485A6FE8A9A9875C25809C994E2903EDB29FD2D370CD605381801A6544C` |
| probe `probes.xml` | 3992 | `8CE49A4F5A1C1651E24D1E658792F2DE80AE63E23A2356BD391D32CC2CBA3263` |

完整机器可读 manifest：`artifacts/response-kan-ds/r2-manifest.json`，逐文件记录 path/bytes/SHA256，覆盖：

- 4 个生产源文件、2 个正式测试文件；
- fresh `build.log`、`response.xml/log`、`kan.xml/log`、`full.xml/log`；
- probe `probe-build.log`、`probes.xml/log`；
- 修复前对照 `prefix-*` 与 `fix.patch`（第 1 轮目录）。

说明：证据文件在 ignored artifacts 内，不随 commit 提交；未把 bin/obj、旧 DLL、临时诊断文件作为证据。

---

## 10. 交接

1. 本任务只处理上述本任务缺陷；不宣称原 TableService 真实新进程链、D-LH-GATE-01/02、G2/G3、整场/设备/跨网完成。
2. 请 caller 验收时决定黄金 source-hash pin 更新；本任务不刷新。
3. 请 TableService/93fb owner 修 §6 provider 后跑真实 PG/WSS ClosedKan/AddedKan/多杠。
4. 未 push/建 PR/MR/merge/deploy/写 `complete.flag`；PR20 由 caller 统一收口。
5. 兄弟树/其他 owner expectedFiles、预留与路由未修改；全局 ledger 仅只读一次。
