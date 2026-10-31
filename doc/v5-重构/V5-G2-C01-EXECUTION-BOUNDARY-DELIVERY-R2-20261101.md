# V5 G2-C01 R2 修复交付（2026-11-01）

更新：2026-11-01，Asia/Shanghai。本文件是 **第 2 轮返修交付与证据**，不是验收批准。

## 0. 结论

| 本轮缺陷 | 状态 | 说明 |
|---|---|---|
| C01-R1-01 terminal 分支绕过在途确认、Dispose 丢失未知请求 | **已修复** | 生产 terminal/commit gate 尊重 active boundary；缺/错确认 0 commit/0 next-open、同 ID unresolved；精确延迟 Applied + 同轮 terminal 一次确认。 |
| C01-R1-02 最终路径无法完成真实 full-match | **未关闭（BLOCKED）** | 已用诊断把停止点定位到“Client 已发送、未收到任何 ReceiptResolved、同 ID 对账也无回执”；确定性 PlayMode one-round 复现同一 wall-0 强制摸打。fresh Player full match 在未触发该 wall-0 摸打的 run 上 PASS，但不能证明该路径已修复。 |

- 基线：`c3c95af6023e13915656acd686269475d5dcceb8`
- R1 代码：`5b52df3ff2746c72fc3e27f5dc8b925fcb3abbd9`
- R1 报告：`d6736ca48a8c9b85550e6a5e14d50157dcf98d61`
- R2 代码/测试提交：`dbc84f36566315b08d2183f2d8e51ada27d08da5`
- R2 报告提交（最终 HEAD）：本报告所在提交
- 工作树 clean；`git diff --check base..HEAD` 0。

## 1. C01-R1-01 修复映射

### 1.1 生产 terminal/commit gate

新增 `FullMatchTerminalCommitGate`（`FullMatchExecutionBoundary.cs`），由生产 driver 和 PlayMode one-round 共用：

- terminal 快照先捕获/记录 `round_terminal` 证据，但 **不得** 直接 commit/close/open。
- 若 `_execution.HasActiveRequest`：继续用同一个 boundary、同一个原 plan、同一轮 terminal 视图 Poll：
  - 精确 `Applied` receipt + 同请求 commandVersion + 合法当前 `RoundEnded` → `Confirmed`；
  - 明确 Rejected → `Replanned`（no-op，不是 Confirmed）；
  - 缺 receipt/never-completing submit/错身份/错版本 → 有界 `Failed`；
  - `Waiting` → 在 match/round 有界时间内继续等待，不提前 commit。
- 只有 `NoActiveRequest / Confirmed / Replanned` 才允许 commit；`Failed` 直接 `Fail`，**0 commit、0 next-open**。
- `_terminals.Add`/`RecordScenarios` 移到 gate 通过之后；gate 失败不会把未确认终局写入 committed terminal 集合。

位置：`LocalFullMatchPlayerDriver.cs` terminal 分支（R1 的 599-646 位置现由 gate 循环替代）；`FullMatchExecutionBoundary.cs` 的 `FullMatchTerminalCommitGate`。

### 1.2 Dispose/abandon 保留未知状态

`FullMatchExecutionBoundary.Dispose` 在 `_activePlan != null` 时先 `_ledger.MarkUnresolved(_activePlan)`，再 cleanup。结果：

- `unresolvedAfterDispose=true`
- `MayIssueNewRequestId=false`
- 原 requestId/scope/bytes 不被替换

这与 caller probe 的 `unresolvedAfterDispose=false/mayIssueNewIdAfterDispose=true` 相反，已由新用例和 mutation 覆盖。

### 1.3 定向/集成用例

`LocalFullMatchExecutionBoundaryTests.cs` 新增：

- `TerminalGateWaitsForDelayedAppliedReceiptThenCommitsOnce`：延迟 Applied + 同轮 terminal → 1 confirm / 1 commit / 1 next-open，0 泄漏 handler。
- `TerminalGateMissingReceiptFailsBoundedWithZeroCommitAndPreservesIdentity`：缺 receipt → `RECEIPT_UNRESOLVED`，0 commit/0 next-open，1 submit，同 ID unresolved；Dispose 后仍 unresolved。
- `TerminalGateNeverCompletingSubmitFailsBoundedWithoutCommit`：never-completing submit → `SUBMIT_TIMEOUT`，0 commit，1 submit，unresolved。
- `TerminalGateRejectsMismatchedIdentityWithZeroCommit`（7 个 Theory）：错 request / command-version / round / recipient-seat / role / binding / scope → 0 commit/0 next-open，1 submit，同 ID unresolved。
- `TerminalGateDefinitiveRejectionIsANoOpAndNeverConfirmed`：Rejected → `Replanned`，1 commit，不是 Confirmed。
- `TerminalGateWithNoActiveOwnRequestCommitsNormally`：无 own active → 正常 commit。
- `DisposedActiveRequestRemainsUnresolvedAndNotReissuable`：Dispose 保留 unresolved/同 ID。
- 另有 `SentWithoutReceiptReconcilesTheOriginalRequestAndConfirms`、`ReconciledButStillUnresolvedFailsBoundedWithoutResendOrNewId`（同 ID 对账，不重发）。

TableModel 实际 **233/233**（R1 218 + 15）。

### 1.4 生产 fence mutation

Mutation D：把 `FullMatchTerminalCommitGate` 的 active-request fence 改为恒 `NoActiveRequest`。实际 **11 failed / 222 passed / 233 total**（11 个 gate 用例失败），恢复后源文件哈希一致。证据：`build/g2-c01-execution-boundary/mutation-r2/mutation-d-terminal-fence/results.xml`。

## 2. C01-R1-02 诊断与未关闭原因

### 2.1 精确停止点（不是 pending=0 猜测）

最终诊断 Player `p45-20261101-003759-ffc86fde`（native 43）：

- 失败 request：`fm-draw-a9617a88322f126268149686be213d0e`，`local-main-r2`，baseline 91。
- `command_submission`：`sent=true`、`wasDuplicate=false`、`handedToTransport=true`、`deliveryUnknown=false`、`needsReconciliation=true`、`lastReceipt=null`、`queryInFlight=false`。
- 同一 request 之后 **没有** `transport_receipt_seen`、没有 `client_receipt_seen`、没有 `action_confirmed`；30s 后 `RECEIPT_UNRESOLVED`。
- 另外加入同 ID 只读对账 `ReconcilePendingAsync`；PlayMode attempt-12 仍在该 wall-0 draw 上 30s 无回执并 `RECEIPT_UNRESOLVED`。对账没有产生 receipt，说明 host 端没有可返回的该 request 回执，而不是 boundary 订阅/过滤丢失。
- `client_pending` 的 `pending=0` 是 view 快照滞后，不是“无 pending”；`command_submission` 已明确 `needsReconciliation=true`，因此没有用 `pending=0` 归罪 Client。

### 2.2 确定性生产路径复现

`LocalFullMatchDriverPlayModeTests.MatchModeRealSession_CompletesOneRoundAndOpensNext` 使用真实 local session + 共享 gate。attempt-12 失败：

- 测试文件行：`unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalFullMatchDriverPlayModeTests.cs:294`
- 失败：`RECEIPT_UNRESOLVED`
- request：`fm-draw-0ceebb1df5ee842fc40b4a709453bc88`
- round：`local-main-r1`，baseline 83，30s，`pending=0`、`lastError=<none>`
- 同一 request 也是 R1 首次 Player 失败（`player-full-match-20261031/executions/p45-20261031-230952-c2e282b0`）的 request。

这是本任务自身新路径的确定性 wall-0 强制摸打复现，不是随机整场重跑，也不是旧 r15 样本。

### 2.3 冻结层定位

在客户端已确认：

- `TableSession.SubmitAsync`（`dotnet/src/Mahjong.Client/TableSession.cs:344`）persist-before-send，`Sent=true` 才返回；
- `SendTrackedAsync`（`:1647`）handoff 后置 `NeedsReconciliation=true`；
- `ResolveTrackedAsync`（`:1716`）是 `ReceiptResolved` 唯一发起点；本 request 从未进入该分支。
- 同 ID `ReconcilePendingAsync` 也没有产生 receipt。

host 侧待裁决点（本任务未改）：

- `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs:865-879`：wall-0 `DrawTile` → `ExhaustiveDraw` settlement conversion；
- `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs:895`：普通 `_actor.SubmitAsync` draw 路径；
- `dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs:186-199`：human Draw 不主动结算（AI-only wall-0 settle）；
- 若需要修，所需 scope 是 LocalHost draw/settlement/回执路径，或由 caller 明确授权的最小 TableActor/host 入口修复。当前没有编辑 Client/LocalHost/Domain/Contracts/严格 observer。

### 2.4 fresh Player full match 的真实结果

- **PASS run**：`p45-20261101-005817-57a45a02`，12 个 round、`matchFinished=true`、`WestCompletion`、finalScores `[25000,25000,25000,25000]`、rankings `[0,1,2,3]`、Player native 0、tool 0；205-file manifest `709E341E...`。
- 该 run 的 player draw 都是正常有牌摸打（r3 85、r6 83、r7 82、r8 84 ...），**没有触发 wall-0 强制摸打**；没有 `client_reconcile_requested` 行。
- 因此 fresh full match PASS 是真实通过证据，但 **不能** 证明 §2.2 的 wall-0 路径已修复；C01-R1-02 仍不关闭。

## 3. 最终测试计数与命令

最终证据根：`build/g2-c01-execution-boundary/r2-final-20261101-0110/`。

| Suite | Total | Pass | Fail | Skip | native exit | XML SHA-256 |
|---|---:|---:|---:|---:|---:|---|
| TableModel | 233 | 233 | 0 | 0 | 0 | `2CF04E1D...` |
| TableRuntime | 482 | 482 | 0 | 0 | 0 | `65D845E3...` |
| LocalHost | 127 | 127 | 0 | 0 | 0 | `11DAA73D...` |
| Tool | 32 | 32 | 0 | 0 | 0 | log `86EA34E4...` |
| PlayMode attempt-11（gate，无 wall-0 draw） | 7 | 7 | 0 | 0 | 0 | `9D29BF88...` |
| PlayMode attempt-12（gate+reconcile，wall-0 draw） | 7 | 6 | **1** | 0 | 2 | `1530D961...` |
| Player reconcile run | — | **PASS** | 0 | 0 | native 0 / tool 0 | summary `6B99801B...` |
| Player diagnostic fail | — | **FAIL** | 1 | 0 | native 43 / tool 10 | summary `B66C00BA...` |

命令：

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj -c Release -t:Rebuild
D:/Unity/dotnet/dotnet.exe test  dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj -c Release --no-build -- --results-directory <dir> --report-xunit-xml --report-xunit-xml-filename results.xml
# TableRuntime / LocalHost 同 pattern，均 -t:Rebuild
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyP45FullMatchDriver.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-full-match.ps1 -Mode All -RunDir build/g2-c01-execution-boundary/player-r2-reconcile-20261101-0101 -TimeoutSeconds 5400
```

PlayMode 使用 `run-unity-validation.ps1`，持有 `Local\MahjongV5P45UnityValidation`，不使用 `-nographics`，只管理自有 PID/start/path/hash。

## 4. 保留的失败与证据

- R1 首次失败：`player-first-failure-20261031-2310/`（request `fm-draw-0ceebb...`，baseline 83）。
- R1 第二次：`player-second-failure-20261031-2314/`。
- R1 late-receipt 探针：`player-full-match-20261031-final/executions/...a05845f1`（r6 先 commit、r7 打开，30s 后 receipt），已由 R1/R2 的 boundary 生命周期处理不再复现。
- R2 诊断失败：`player-r2-diag-20261101-0034/executions/...ffc86fde`（request `fm-draw-a9617...`，baseline 91）。
- R2 PlayMode wall-0 复现：`unity/attempts/attempt-12/playmode-results.xml`（request `fm-draw-0ceebb...`，baseline 83）。
- R2 Player PASS：`player-r2-reconcile-20261101-0101/executions/...57a45a02`。
- Mutation D：`mutation-r2/mutation-d-terminal-fence/`（11 gate failures，恢复哈希一致）。

## 5. 范围与未关闭项

- 改动 10 个文件：R1 的 9 个 + `SessionCoordinator.cs`（App orchestration 的只读 `ReconcilePendingAsync` 包装；未改 Client/LocalHost/Domain/Contracts/Wire）。
- `ExitCode`、20/30s bounds、严格 role/scope/version/seat/binding/round 匹配、observer/smoke 策略均未放宽。
- **C01-R1-02 未关闭**：确定性 PlayMode wall-0 draw 仍无 receipt；fresh full match PASS run 未覆盖该路径。正式结论为 **BLOCKED on frozen LocalHost draw/settlement receipt path**，需要 caller 裁决 scope。
- Android/iOS/Compare/观战/视觉/C02 未运行，不作为本轮理由。
- 本报告不构成 G2-C01/G2 批准；未 push/PR/MR/merge/deploy/scheduler/complete.flag。
