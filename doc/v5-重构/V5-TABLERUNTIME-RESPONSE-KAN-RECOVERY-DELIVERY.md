# V5 TableRuntime 响应窗口失效与鸣牌墙证据恢复交付

日期：2026-10-29
工作树：`D:/majiang-wt/runtime-response-kan-ds-20261029`
分支：`wt/v5-runtime-response-kan-ds-20261029`
冻结基线：`183e378da273081075a5eb54472ffe6dfd29f670`（caller 指定的唯一当前 PR20 已审查批次头）
代码提交：`662e1bb949904eb269f847ac083719843ed67554`；文档提交：本轮 docs commit（`git log -1`）
状态：**仅本范围两项缺陷的本地实现与验证；不是 G2/G3/G4/G5 通过，不是真实 PG/WSS 验收，不是发布。**

---

## 0. 结论摘要（先给口径）

| 项 | 结论 |
|---|---|
| D-TR-RESP-01（MAJOR） | **PASS（本地 TableRuntime 范围）**：已确定性复现，修复后新增 7/7 通过；失败提交/租约拒绝不消耗 pending/储备。 |
| D-TR-KAN-02（MAJOR） | **PASS（本地 TableRuntime 范围）**：已确定性复现，真实物理墙/死山补位模型补齐；新增 10/10 通过，含合法 checkpoint 校验。 |
| TableRuntime 全量 fresh | **416 / 417 PASS，1 FAIL**：唯一失败为 `GoldenMetaProvenanceAndCaseIdentityAreReal` 的**源码哈希 pin**；按禁令未刷新黄金摘要/未弱化断言。 |
| 真实 PG + WSS 新进程 | **NOT_RUN（本任务）**：按 caller 要求由原 `93fb` 在 runtime delta 接受整合后完成；不宣称其 49 项已验证本修复。 |
| 跨模块 TableService 事实源 | **BLOCKED（不在本任务可改范围）**：发现 `TableProjectionContextSource` 的旧反向墙模型只对 `TileDrawn` 自增，未对 `RinshanDrawn` 自增；真实服务在“普通摸牌后发生杠”的 checkpoint 上仍会与新验证器冲突。精确位置/最小修复见 §7。 |
| G1 / G2–G5 | G1 仍为 APPROVED；G2–G5 未批准；V4 仍是唯一生产基线；181 classified 未覆盖项不在本任务范围。 |
| 兄弟任务 | 全局 ledger 仅本轮只读一次；`thread_20261029_041602_732e`、`thread_20261027_230302_93fb`、`thread_20261028_223624_e4ae` 的原路径/预留/路由未触碰；未创建子任务/未转派。 |

---

## 1. 范围、基线与禁令遵守

### 1.1 实际改动文件（exact paths）

生产代码（仅 `Mahjong.TableRuntime`）：

- `dotnet/src/Mahjong.TableRuntime/TableActor.cs`
- `dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs`
- `dotnet/src/Mahjong.TableRuntime/TableResponseDecisionReconciliation.cs`（新增，纯响应决策/计时原子对账）
- `dotnet/src/Mahjong.TableRuntime/TableKanWallEvidence.cs`（新增，纯 Domain 物理墙/死山/补牌证据重建）

测试（新增，未修改既有兄弟测试）：

- `dotnet/tests/Mahjong.TableRuntime.Tests/TableResponseDecisionInvalidationTests.cs`
- `dotnet/tests/Mahjong.TableRuntime.Tests/TableKanRecoveryEvidenceTests.cs`

文档：

- `doc/v5-重构/V5-TABLERUNTIME-RESPONSE-KAN-RECOVERY-DELIVERY.md`

### 1.2 未触碰

未修改 `sln`、统一 verifier/架构白名单、CI、scheduler、Unity、`Domain`、`Contracts`、`Contracts.Wire`、`Infrastructure`、`TableService`、`LocalHost` 源码、任何黄金 fixture/pins、机器信任/全局 PATH/共享服务。未 push、未建 PR/MR、未 merge/deploy、未写 `complete.flag`。

### 1.3 工作树/祖先/清洁

```text
git rev-parse HEAD
662e1bb949904eb269f847ac083719843ed67554

git merge-base --is-ancestor 183e378da273081075a5eb54472ffe6dfd29f670 HEAD
exit 0

git diff --check 183e378..HEAD
exit 0

git status --porcelain=v1
0 lines (tracked clean)

git log --oneline 183e378..HEAD
662e1bb fix(v5): fence closed response windows and model kan wall evidence
```

说明：本树从 `origin/v5=8fd7c6cb` 创建后 ff 到 `183e378`；`183e378` 是 `HEAD` 的祖先；未从旧 main/关闭的 PR19 source 创建。未合入任何未接受 receiver 代码。对 `D:/majiang-wt/p54b-durable-host-glm-20261027` 仅只读 `§10.5`、测试源与 ignored 证据，未修改该兄弟树。

---

## 2. 缺陷 D-TR-RESP-01（MAJOR）：鸣牌后已结束响应窗口的 pending/clock 未原子失效

### 2.1 位置

- `dotnet/src/Mahjong.TableRuntime/TableActor.cs`：`CommitAndInstallAsync`（非 durable）、`CommitDurableAcceptedAsync`（durable）、`CommitCheatAndInstallAsync`（cheat）、`ProcessCompleteRecoveryAsync`（恢复）。
- 原缺陷点：durable 分支只排除 `consumedSeat`，把其他 `_pending` 原样复制进 `TableTimingCheckpoint`，只有 `nextState.IsFinished` 才 `Reset/Clear`；非 durable 只在提交后移除提交座位；恢复时直接按 timing 行安装所有决策。

### 2.2 复现（真正 Domain 合法状态 + 两个 eligible 响应座位）

新增 `TableResponseDecisionInvalidationTests` 使用完全显式 136 张合法牌局：

- 座位 0 摸 `Man3(copy3)` 后打出（`target=11`）；
- 座位 1（下家）持 `Man1/Man2`，可真实 Chi；
- 座位 2 持两张 `Man3`，可真实 Pon；
- Domain 的 `ResponseWindow.EligibleSeats = [1,2]`，不是人工伪造的 pending 列表。

actor probe 记录：

| 时点 | phase | window claims | state_version | pending | timing |
|---|---|---|---|---:|---|---|
| 开窗后 | `ResponseWindow` | `[]`，eligible `[1,2]` | 0 | seat1=`chi-1`、seat2=`pon-2` | 两者 deadline=2000 |
| seat1 提交 Chi 后 | `ResponseWindow` | `[1]` | 1 | seat1 已消费；seat2=`pon-2` 保留（窗口仍开放，符合多座位提交/优先级既定语义） | seat2 原 deadline 不变 |
| 再为已响应的 seat1 打开过期捕获 `stale-1`，seat2 Pass 关闭窗口后（修复前） | `Discard`（seat1） | 无 | 2 | **seat1 `stale-1` 仍残留** | **seat1 timing 仍残留** |
| 同上（修复后） | `Discard` | 无 | 2 | 全部 null | 全部 null |

修复前 fresh DLL 运行结果（`prefix-response.xml`）：

```text
total=5 passed=3 failed=2
FAIL ClosedWindowDropsStaleDecisionAndTimingInTheSameCommit
     Assert.All Failure: 1 out of 4 items ... did not pass
FAIL RecoveryDropsStaleClosedWindowDecisionsAndAllowsLegalPlay
     recovery status=Rejected reason=RECOVERY_TIMING_INVALID
     detail=Remaining reserve cannot exceed the configured reserve ... Actual value was 1000.
```

实际 vs 期望：

- 实际：窗口已关闭后，旧响应 decision 和它的 `DecisionTiming` 仍可能同时存在；恢复时又被安装，随后 tick 对已无窗口的 `Pass` 反复重试而无法推进。
- 期望：窗口关闭（或窗口身份改变、该座位已响应）的那个**同一个已提交事务边界**内，失效 decision 与 timing 一起失效；窗口尚未关闭时，多 eligible 座位的合法 decision 必须保留，不能无条件 clear 全部。

### 2.3 修复映射

1. 新增 `TableResponseDecisionReconciliation.FilterAfterTransition`：
   - 以 `priorWindow` + `nextState.Window` 精确判定“旧响应 decision 是否仍被当前窗口要求”；
   - 仅当 `nextWindow != null`、`FromSeat/TargetTile/IsChankan` 相同、seat 仍 eligible 且未 `HasResponded` 时保留；
   - 否则在 `candidateClock` 上 `Cancel` 该 key（结算已过时间）并丢弃 pending；
   - `nextState.IsFinished` 时 `Reset` 并清空；
   - 非窗口的 turn/settlement decision 不因无关提交被误删。
2. 非 durable `CommitAndInstallAsync`：改为先克隆 `DecisionClock`，在克隆上 `Complete/Expire` + 对账，**commit 成功后才安装**；commit 失败/取消/租约拒绝时原 clock/pending 零变化。移除了 tick/AI 路径在提交后再次 `Complete/Expire/Remove` 的双重消费（否则会把 actor 置为 faulted）。
3. durable `CommitDurableAcceptedAsync` / `CommitCheatAndInstallAsync`：在 `CaptureTiming` **之前**对账 `pendingAfter`/`candidateClock`，使 state、timing journal、checkpoint 三者在同一原子提交内一致。
4. 恢复 `ProcessCompleteRecoveryAsync`：先 `FilterRecovered`，只安装仍被恢复后 Domain 窗口要求、或合法的当前 turn/settlement decision；不再把已关闭窗口的旧响应 decision 安装回 actor。

### 2.4 回归矩阵（新增测试实际覆盖）

| 维度 | 用例 | 结果 |
|---|---|---|
| Chi + 开放窗口 + 多 eligible | `ClaimKeepsPeersWhileWindowIsOpenAndConsumesTheirCommit` | PASS：claim 后 seat2 pending/timing 保留；seat2 提交后窗口关闭、全部清空 |
| Pon + 开放窗口 | 同上（seat2 用 `pon-2` 提交） | PASS |
| 窗口关闭 + 过期 decision | `ClosedWindowDropsStaleDecisionAndTimingInTheSameCommit` | PASS（修复前 FAIL） |
| 真人提交 | 上述 Chi/Pass | PASS |
| timeout | `TimeoutOnRemainingResponseSeatClosesWindowAndConsumesItsDecision` | PASS：seat2 到点 Pass，窗口关闭，pending/timing 清空 |
| AI | `AiPassOnRemainingResponseSeatClosesWindow` | PASS：AI Pass 被接受，窗口关闭 |
| durable 提交失败 | `DurableCommitFaultLeavesPendingAndReserveUntouchedThenRetries` | PASS：`BeforeApply` fault 后 state_version=0、deadline/reserve/pending 不变；重试 APPLIED=1 |
| 非 durable 提交失败 | `FailedCommitDoesNotConsumeOrInvalidateAnyResponseDecision` | PASS：失败后两个座位 deadline/reserve/pending 全不变；重试接受 |
| checkpoint + 恢复 | `RecoveryDropsStaleClosedWindowDecisionsAndAllowsLegalPlay` | PASS（修复前 FAIL）：恢复后全部 pending/timing null，随后合法 discard 接受，版本 2→3 |
| 终态 | 既有全量回归中的 round-finished 用例 | PASS（未弱化） |
| 136 实体 | 响应 fixture 构造与恢复后继续动作 | PASS：`TilePositionMap.Validate` 136/0 |

说明：durable 响应窗口的“窗口内 claim + 对账”与非 durable 共用同一个 `FilterAfterTransition`；durable 侧另有故障注入/重试用例验证原子性。本任务未构造真实 PG 响应窗口链；见 §7/§8。

---

## 3. 缺陷 D-TR-KAN-02（MAJOR）：KanDeclared/RinshanDrawn 被“未建模”拒绝

### 3.1 位置

- `dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs`：`ValidateRecoveredEventFacts`（原约 500 行）对 `KanDeclared`/`RinshanDrawn` 直接抛：
  `Kan/replacement-draw wall evidence is not modeled; refusing to guess.`
- 同一函数只对 `TileDrawn` 做反向墙计数，未计入 `RinshanDrawn` 对 live wall 的消耗。

### 3.2 复现（真实 Domain 合法鸣牌/杠）

`TableKanRecoveryEvidenceTests` 用 136 张显式牌局生成真实 Domain 事件链：

- OpenKan：座位 0 打出 `Dragon1(copy3)`，座位 1 用三张同型明杠；随后岭上摸牌、再普通摸牌。
- ClosedKan：座位 0 四张同型暗杠；随后岭上摸牌、再普通摸牌。
- AddedKan：座位 1 先 Pon，再用手牌第四张加杠；覆盖 `chankanEligible=[]` 立即加杠和 `[2]` 抢杠窗口 + Pass 两种。
- 多杠：同一局先 OpenKan、再 ClosedKan，`KanCount=2`、`RinshanPointer=2`。
- checkpoint0+tail：取命令版本 2 起的 tail（不含更早命令）。
- 合法 checkpoint：用同一事件/事实构造 `TableCheckpointFactory.Create`，交给 `TableCheckpointValidator.TryValidate`。

修复前 fresh DLL 运行结果（`prefix-kan.xml`）：

```text
total=10 passed=4 failed=6
FAIL OpenKanReplacementAndFollowingNormalDrawValidate
FAIL ClosedKanReplacementAndFollowingNormalDrawValidate
FAIL AddedKanImmediateAndChankanWindowValidate
FAIL MultipleKansInOneRoundValidate
FAIL CheckpointZeroTailSliceWithKanValidates
FAIL LegalKanCheckpointPassesCheckpointValidator
message: Kan/replacement-draw wall evidence is not modeled; refusing to guess.
checkpoint validator: CHECKPOINT_INVALID: Checkpoint history/binding evidence is invalid:
  Kan/replacement-draw wall evidence is not modeled; refusing to guess.
```

该复现正是 receiver §10.5 所述“真实 PG 合法 OpenKan SaveCheckpointAsync = CHECKPOINT_INVALID”的纯 runtime 等价位置；修复前本任务直接核读了同一拒绝。

### 3.3 实际物理模型与修复

Domain 的实际模型：

- 普通摸牌：live wall 移出一张，`WallRemaining - 1`；
- 杠的岭上摸牌：rinshan tile 从死山进入手牌，同时 live wall 尾牌移入死山补位，`WallRemaining` 仍 `- 1`；
- `KanDeclared` 本身不移动 live wall（杠牌来自手牌/副露；dora indicator 是死山实体）。

因此反向重建时，`TileDrawn` **和** `RinshanDrawn` 都必须 `wall++`；`KanDeclared` 不增。新增 `TableKanWallEvidence`：

1. 反向墙重建每个 `TileDrawn` 的 `wallRemaining`（pipeline 中 `WallRemaining` 语义为该 draw 之后的剩余）。
2. 结构校验：
   - history 必须按 `event_seq` 严格递增、命令版本不倒退、同命令 ordinal 连续、新命令 ordinal=0；
   - 每个 `RinshanDrawn` 必须与**同一命令、同一座位**的 `KanDeclared` 配对；
   - 每个 `KanDeclared` 必须在其命令内有且仅有一次补牌；
   - 全历史时 `KanDeclared/RinshanDrawn` 数必须等于 recovered round 的 `KanCount/RinshanPointer/ReplacementCount`；
   - 有 `TilesDealt` 时，`finalWall + 全部普通摸牌 + 全部岭上摸牌 == 122 - 53 = 69`；拒绝任何非法墙迁移/负值/越界；
   - 每个 `KanDeclared` 的 `MeldView` 必须与 recovered Domain 的对应 kan meld 精确匹配（type、tile 顺序、called tile、fromSeat）；按 detail + 物理 tile + kind 匹配，避免多副露顺序歧义。
3. 保留并加强逐字段校验：
   - `TileDrawn.wallRemaining` 必须等于重建值；
   - `RinshanDrawn` 不得携带 wall/meld 事实；
   - `KanDeclared` 必须携带完整 meld，且 detail 必须是 `OPEN_KAN/CLOSED_KAN/ADDED_KAN`；
   - `WithoutMeld` 镜像 live mapper 的公开暗杠 redaction 边界，不把 owner 四张身份带入公共/他家投影校验。

### 3.4 负例矩阵（实际运行）

| 负例 | 用例 | 结果 |
|---|---|---|
| 篡改 `TileDrawn.wallRemaining` | `TamperedWallRemainingFailsClosed` | PASS：`TableProjectionException` |
| 删除 `RinshanDrawn`（含 fact） | `MissingReplacementDrawFailsClosed` | PASS：配对/覆盖失败 |
| 篡改 `KanDeclared` meld tile | `TamperedKanMeldFailsClosed` | PASS |
| 乱序 history | `ReorderedHistoryFailsClosed` | PASS：seq/ordinal 顺序失败 |
| 合法 OpenKan + 后续普通摸牌 | `OpenKanReplacementAndFollowingNormalDrawValidate` | PASS |
| 合法 ClosedKan + 后续普通摸牌 | `ClosedKanReplacementAndFollowingNormalDrawValidate` | PASS |
| 加杠（立即/抢杠窗口） | `AddedKanImmediateAndChankanWindowValidate` | PASS |
| 多杠 | `MultipleKansInOneRoundValidate` | PASS |
| checkpoint0+tail | `CheckpointZeroTailSliceWithKanValidates` | PASS |
| 合法 checkpoint 全套 | `LegalKanCheckpointPassesCheckpointValidator` | PASS：`TableCheckpointValidator.TryValidate=True` |
| 136 实体 | 所有 kan 脚本的 `BuildEvidence` | PASS：136 assigned / 0 unassigned |

---

## 4. 验证命令、真实计数与退出码

> 工具：`D:/Unity/dotnet/dotnet.exe`（SDK 8.0.426）。所有结果均为本机本树实际运行，不是 CI/远端结论。

### 4.1 fresh build（GUID 隔离输出）

```powershell
D:/Unity/dotnet/dotnet.exe build `
  dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj `
  --no-incremental --artifacts-path artifacts/response-kan-ds/fresh-9499a87081e34af594834c4a464d1931 `
  -v:minimal
```

结果：`build exit 0`，`0 warnings / 0 errors`；fresh DLL：
`artifacts/response-kan-ds/fresh-9499a87081e34af594834c4a464d1931/bin/Mahjong.TableRuntime.Tests/debug/Mahjong.TableRuntime.Tests.dll`

### 4.2 新增定向（直接运行 fresh DLL，不用旧 DLL）

```powershell
dotnet exec <fresh>/Mahjong.TableRuntime.Tests.dll -noColor -automated `
  -class Mahjong.TableRuntime.Tests.TableResponseDecisionInvalidationTests `
  -result-nunit <fresh>/response.xml

dotnet exec <fresh>/Mahjong.TableRuntime.Tests.dll -noColor -automated `
  -class Mahjong.TableRuntime.Tests.TableKanRecoveryEvidenceTests `
  -result-nunit <fresh>/kan.xml
```

真实计数：

| 套件 | native exit | total | passed | failed | skipped | errors | notRun |
|---|---:|---:|---:|---:|---:|---:|---:|
| TableResponseDecisionInvalidationTests | 0 | 7 | 7 | 0 | 0 | 0 | 0 |
| TableKanRecoveryEvidenceTests | 0 | 10 | 10 | 0 | 0 | 0 | 0 |

### 4.3 fresh TableRuntime 全量

```powershell
dotnet exec <fresh>/Mahjong.TableRuntime.Tests.dll -noColor -automated `
  -result-nunit <fresh>/full.xml
```

| native exit | total | passed | failed | skipped | errors | notRun |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 417 | 416 | 1 | 0 | 0 | 0 |

唯一失败：

```text
GoldenMetaProvenanceAndCaseIdentityAreReal
FAIL: source hash pin changed
```

原因：该黄金元数据测试对 production source 计算固定 SHA256 pin；本任务按授权修改了 `TableActor.cs`/`TableVisibilityProjection.cs` 并新增两个 TableRuntime 源文件，pin 必然变化。按任务禁令**未刷新黄金摘要/pins、未弱化断言**。该失败需由 caller 在验收时决定是否更新 pin；本任务不把它写成功能绿。

### 4.4 相关 focused 回归

| 套件 | 命令 | native exit | total/passed/failed |
|---|---|---:|---|
| Domain `RoundDecisionTests` | `dotnet exec Mahjong.Domain.Tests.dll -class ...RoundDecisionTests` | 0 | 24 / 24 / 0 |
| Domain `RoundStateMachineTests` | `dotnet exec ... -class ...RoundStateMachineTests` | 0 | 23 / 23 / 0 |
| Architecture Release | `dotnet exec .../Release/...Mahjong.Architecture.Tests.dll` | 0 | 130 / 130 / 0 |
| Infrastructure | `dotnet exec .../Release/...Mahjong.Infrastructure.Tests.dll` | 1 | 42 / 1 / 41 |

Infrastructure 的 41 失败全部是环境阻塞：
`BLOCKED: MAHJONG_P52_TEST_POSTGRES_ADMIN / MAHJONG_P52_TEST_POSTGRES is required ...`，不是本修复的功能失败；按 `BLOCKED/NOT_RUN` 记录，不冒充通过。

### 4.5 修复前对照（同一 fresh 测试代码，原 HEAD 生产源）

为证明“先复现再修复”，将 `fix.patch` 反向应用、移除两个新 source（保留新测试），fresh build 后运行：

| 套件 | native exit | total | passed | failed | 关键失败 |
|---|---:|---:|---:|---:|---|
| response | 1 | 5 | 3 | 2 | `ClosedWindowDropsStaleDecisionAndTimingInTheSameCommit`；`RecoveryDropsStaleClosedWindowDecisionsAndAllowsLegalPlay` |
| kan | 1 | 10 | 4 | 6 | OpenKan/ClosedKan/AddedKan/多杠/checkpoint0+tail/合法 checkpoint 全被 `Kan/replacement-draw wall evidence is not modeled` 拒绝 |

随后恢复 `fix.patch` 与新 source，fresh build 重新得到上述 7/7、10/10。修复前日志/XML 保留在 ignored `artifacts/response-kan-ds/prefix-evidence/`。

---

## 5. 版本/事件/receipt/136/账本一致性

- 响应测试断言：窗口内 claim 只推进一次 `state_version`；seat2 后续提交/超时/AI 各推进一次；失败提交不推进；恢复后合法 discard 从 2→3。
- 响应测试断言：`CommitPort.Requests` 在失败提交时为空/不新增；重试只有一个新请求，没有重复 receipt。
- kan 测试断言：`TilePositionMap.Validate` 136 assigned / 0 unassigned；`TableCheckpointValidator` 覆盖 history/binding、control/timing、facts、136 与 Domain 事件前缀。
- 账本/结算：本任务未执行结算/出千扣费；没有账本写入路径。真实 PG 账本唯一性仍属于 `93fb`/P5.4b 的 PG 链，本任务不宣称。
- 未使用 sleep、未扩大 timeout、未删探针、未用旧 DLL、未 skip/空测试。fake clock 固定 1000/1500/2000；commit gate 使用 `FailNext`/`ArmedFaultPoint.BeforeApply`。

---

## 6. 跨版本 checkpoint 兼容性

- **旧版本含 kan 的 checkpoint 从未被接受**：修复前 `ValidateRecoveredEventFacts` 对任何 `KanDeclared`/`RinshanDrawn` 直接拒绝，所以不存在“旧的合法 kan checkpoint”需要兼容；本修复不是放宽旧拒绝，而是首次为真实物理模型提供可验证的接受路径。
- **非 kan checkpoint 行为不变**：反向墙模型对只含 `TileDrawn` 的历史与旧实现等价（`RinshanDrawn` 计数为 0）。
- **schema 不变**：未修改 `TableCheckpointFactory.SchemaVersion`（仍为 2），未新增字段，未修改 Domain/Contracts。
- **新 checkpoint**：合法 OpenKan/ClosedKan/AddedKan/多杠/checkpoint0+tail 均通过 `TableCheckpointValidator`（本任务合成合法 facts）。
- **旧 provider 的 kan facts**：见 §7；若旧 TableService provider 生成了错误的 `TileDrawn.wallRemaining`，新验证器会 fail closed，不会静默修正或接受。这是有意的“不猜”行为。

---

## 7. 跨模块 BLOCKED：TableService 事实源仍使用旧反向墙模型

本任务严格未改 `TableService`。但实际核读发现：

`dotnet/src/Mahjong.TableService/TableProjectionContextSource.cs:477-486`：

```csharp
long wall = round.Tiles.WallRemaining;
for (int i = events.Count - 1; i >= 0; i--)
{
    if (events[i].Type == RoundEventType.TileDrawn)
    {
        wallAt[i] = unchecked((uint)wall);
        wall++;
    }
}
```

缺少：

```csharp
else if (events[i].Type == RoundEventType.RinshanDrawn)
{
    wall++;
}
```

后果：

- 对“先有普通 `TileDrawn`、后有杠/岭上摸牌”的合法服务端 checkpoint，provider 为更早的 `TileDrawn` 生成的 `wallRemaining` 会比实际物理值 **+1**；本任务的新验证器会按真实模型拒绝它（fail closed）。
- 对“第一弃即 OpenKan、杠前没有普通 TileDrawn”的场景（receiver §10.5 的实际 PG OpenKan 复现形态），provider 不会产生上述 +1，因此本修复可使该形态通过事实校验（仍需 93fb 的真实 PG 链复跑，不能由本任务宣称）。
- 真实服务端 ClosedKan/AddedKan/多杠若发生在普通摸牌之后，仍会被本任务验证器拒绝，直到 TableService owner 修复 provider；这不是本任务可以越界改的文件。

最小路径（交由 TableService/93fb owner）：

1. 在 `TableProjectionContextSource.BuildEventFacts` 的反向循环中加入 `RinshanDrawn` 的 `wall++`；
2. 同步修正仅测试用的同形 helper（如 `TableStreamWireTestSupport.WireEventFacts.Build`）；
3. 用本任务 `TableKanRecoveryEvidenceTests` 的 OpenKan/ClosedKan/AddedKan/多杠/checkpoint 模式在 TableService 事实层复跑；
4. 真实 PG/WSS 新进程仍按原 93fb 任务执行，本任务不冒充其 49 项。

因此本任务对 D-TR-KAN-02 的结论严格限定为 **TableRuntime 验证/投影合同 PASS；真实 TableService 生成端的普通摸牌+杠组合 BLOCKED**。

---

## 8. PASS / FAIL / BLOCKED / NOT_RUN（分开列出）

### PASS

- D-TR-RESP-01 本地复现、修复、7/7 定向通过。
- D-TR-KAN-02 本地复现、物理墙模型、10/10 定向通过。
- fresh TableRuntime 全量 416/417；除黄金 pin 外无功能失败。
- Domain `RoundDecisionTests` 24/24；`RoundStateMachineTests` 23/23。
- Architecture Release 130/130。
- `git status` tracked clean；`git diff --check 183e378..HEAD` exit 0。

### FAIL

- `GoldenMetaProvenanceAndCaseIdentityAreReal`：源码哈希 pin 因本任务授权源码变更而失配；**未刷新**。需 caller 验收时决定更新 pin；本任务不伪造绿。

### BLOCKED

- 真实 TableService 事实源在“普通摸牌后杠”组合的旧反向墙模型（§7），需 TableService owner 修复。
- 真实 PG/WSS 新进程完整重跑（按 caller 要求由原 93fb 在 runtime delta 整合后完成）。
- `MAHJONG_P52_TEST_POSTGRES*` 未配置，Infrastructure 41 项 PG 用例环境阻塞。
- D-LH-GATE-02 控制回执角色阻塞仍由原 owner 处理；本任务未绕过。

### NOT_RUN

- 真实四人对局整场、真实设备/Android/iOS/Unity/IL2CPP、跨物理网络、TLS/WSS 生产入口、容量/混沌、发布/灰度/回退。
- 真实 PostgreSQL 的 `SaveCheckpointAsync`/冷恢复/账本唯一性（本任务只做 TableRuntime 合成合法 checkpoint 校验）。
- G2/G3/G4/G5 门槛；本任务不宣称任何阶段完成。

---

## 9. 证据与 SHA256 完整 manifest

根目录：`artifacts/response-kan-ds/`（ignored，GUID 隔离；不进入 tracked）。

fresh 运行目录：`artifacts/response-kan-ds/fresh-9499a87081e34af594834c4a464d1931/`

| 文件 | bytes | SHA256 |
|---|---:|---|
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | 397343 | `EA6FB443E630B5EB7E2E69D9DD46955A9CA6B28B6A5F5FCDBD1697771A0C3484` |
| `dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs` | 62155 | `05EFCE36021650F1D057D01B51F17848CDA95C06083F2F0C67F2E4EB1BAEF9CA` |
| `dotnet/src/Mahjong.TableRuntime/TableResponseDecisionReconciliation.cs` | 7718 | `528A57FD59B88548271FA01CACABDBE886F5B8B9BD06CDC11A6AF020114F292F` |
| `dotnet/src/Mahjong.TableRuntime/TableKanWallEvidence.cs` | 16557 | `6E3967541CE665023131E7E6569843989FA314DB16BD8B67512BDAFC35B5C6CD` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableResponseDecisionInvalidationTests.cs` | 25187 | `AABFC50B868902A1CBB9248AC3302DA2C0D855DC3B3D89A2D0B23F2DB2CBA7D4` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableKanRecoveryEvidenceTests.cs` | 31642 | `458F2EAB6D58FADC7412FB41472DE4C47376E546CC10A193E1582338B4C9B233` |
| `.../fresh-.../build.log` | 4478 | `603B3DAAD399BFBEE4CBAF0CA51090EA3118ACDC99D85669746540122419D488` |
| `.../fresh-.../response.xml` | 6136 | `F3A9EBA3EFE25388279912ADFD8ED133FF7A1A203D27DB182A98535C5D9A639F` |
| `.../fresh-.../response.log` | 46015 | `5DBCBEEAA830FEAAB718BD1B7B7F1B1E61F405533507E283628D533AC8FE93A3` |
| `.../fresh-.../kan.xml` | 6943 | `ECEE3DA5A34C7614BAE07E370882EB7F04037BDC8AA935F3908FF9712760F791` |
| `.../fresh-.../kan.log` | 62955 | `08ECC36DBB8E842DC60101E93EB0FE6CF752FCFBEC985D4C5AA95F0FB024159F` |
| `.../fresh-.../full.xml` | 258759 | `15463EC1BA5C0918165748EFC1C8EA5D45056096E4A1433F46F0DDBA12BF1E54` |
| `.../fresh-.../full.log` | 2435919 | `C5B430204B66F13E705E88C3E1EE01C73E8120828EBE81BEB907308B91E93106` |
| `.../prefix-evidence/prefix-response.xml` | 9437 | `2B0B65E69769745ADEC0280804FC2F472CFDF874A6F86E7D084FF7EB03E411ED` |
| `.../prefix-evidence/prefix-response.log` | 37082 | `B0CD09F1B72BE07D89D82165638BE815086BC2E933E4DCD0914B17F8FB3D9D0F` |
| `.../prefix-evidence/prefix-kan.xml` | 18471 | `8498AAA9A48379EEE5F46F1215348A0B3647C04885A5F7FA0278F671B48F5222` |
| `.../prefix-evidence/prefix-kan.log` | 68861 | `9F68BD8C038DDAC1B27696FE0FB8FA036FD71E5646D4F463150E7DBB40A3D472` |
| `.../prefix-evidence/fix.patch` | 20953 | `D576FD211AC9EC65F9D6390DD15A03A838BF50B4D0FFDF4483173552844CEDE8` |

补充说明：

- `response.log/kan.log/full.log` 是 xUnit v3 `-automated` 的完整 JSON 行日志；`.xml` 是 NUnit v3 结果文件。
- 上表 source/test 哈希与提交 `662e1bb` 工作树内容一致；证据文件在 ignored artifacts 内，不随 commit 提交。
- 未把 `bin/obj`、旧 DLL、临时诊断文件作为证据；`ZzDiag.cs` 已删除且未提交。

---

## 10. 交接与未完成项

1. 本任务只处理 D-TR-RESP-01 / D-TR-KAN-02，不宣称原 TableService 真实新进程链、D-LH-GATE-01/02、G2/G3、整场/设备/跨网完成。
2. 请 caller 验收时决定黄金 source-hash pin 的更新；本任务不自行刷新。
3. 请 TableService/93fb owner 修复 §7 的 provider 反向墙自增，再跑真实 PG/WSS ClosedKan/AddedKan/多杠。
4. 本任务不 push、不建 PR/MR、不 merge/deploy、不写 `complete.flag`；所有发布/PR20 登记由 caller 执行。
5. 兄弟树/其他 owner 的 expectedFiles、预留与路由未修改；全局 ledger 仅只读一次。
