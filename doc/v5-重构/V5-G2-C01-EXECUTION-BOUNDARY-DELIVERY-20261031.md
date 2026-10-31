# V5 G2-C01 生产执行边界与内部耗墙终局交付（2026-10-31）

更新：2026-10-31，Asia/Shanghai。本文件是 **交付与证据报告**，不是验收批准，也不是 PR 合并记录。

## 0. 结论先行

| 项目 | 结果 | 说明 |
|---|---|---|
| A：共用生产执行边界（原 D-1-05(a)） | **PASS（组件/PlayMode 级）** | 正常 `RunMatchCore` 与 PlayMode 使用同一个 `FullMatchExecutionBoundary`；真实失败条件、单次提交、真实 observer/ledger、late-receipt 隔离均有定向负例。 |
| B1：`Applied receipt + 当前 RoundEnded` 终局确认 | **PASS（边界/负例级）** | 精确 Applied + 同请求 commandVersion + 合法当前 RoundEnded 成功；无 receipt、Rejected、错 request/version/round/seat/role/binding/scope 均不误确认、不推进。 |
| B2：内部 timeout/AI 耗墙 Pass | **PASS（TableRuntime 级）** | 真实 `TableActor.TickAsync`/`CompleteAiAsync` → `ApplyInternalCommandAsync`；WallExhausted、版本、事件、136 张、无重复副作用；有墙无 reservation 对照；真实 reservation 交付沿 `SubmitCheatAsync` 链。 |
| Unity PlayMode | **PASS 7/7** | 4 个执行边界负例 + 2 个真实 match session 用例 + 1 个 bootstrap 用例。 |
| fresh Windows Player 基本冒烟 | **PASS** | 同一个 fresh Player，native 0、tool 0、14-entry 证据 manifest 校验通过。 |
| fresh Windows Player **完整整场** | **FAIL** | 首次失败与最终失败均保留；玩家强制 `DrawTile` 的 receipt 未回，完整整场未完成。**不得据此宣称 G2 或 P4.5 通过。** |
| G2-C01 / G2 验收 | **未批准** | 调用方独立 review 后才可能批准；本报告不作批准。V4 仍为生产架构。 |

**一句话**：A/B 两组代码和定向回归已经交付并可复核；fresh Player 完整整场仍被一个强制摸打 receipt 缺口阻断。保留首次失败，不用 r15 旧样本冒充新 Player 通过。

## 1. 基线与工作树

- 冻结继承 base：`c3c95af6023e13915656acd686269475d5dcceb8`
- 工作树：`D:/majiang-wt/g2-c01-execution-boundary-ds-20261031`
- 分支：`wt/v5-g2-c01-execution-boundary-ds-20261031`
- 代码/测试提交：`5b52df3ff2746c72fc3e27f5dc8b925fcb3abbd9`
- 本报告单独提交（报告-only HEAD）；`base..HEAD` 的最终差异与 clean 状态在交接消息中给出。
- 继承的 r15 12 局整场成果和 D-1-05(b) 继续作为历史通过证据保留；本任务没有重跑、没有改写、没有把旧 Player 当作新代码证明。
- 旧工作树 `D:/majiang-wt/p45-full-match-driver-ds-20261031` 只读；未编辑、未写旧 thread 状态/锁。

## 2. 范围与文件

本任务只改以下文件（`git diff --stat 5b52df3f^..5b52df3f`）：

| 文件 | 变更 |
|---|---|
| `unity/Mahjong.Unity/Assets/Scripts/App/FullMatchExecutionBoundary.cs` | 新增共用执行边界（规划/单次提交/有界等待/确认/ledger/notice/failure stage）。 |
| `unity/Mahjong.Unity/Assets/Scripts/App/FullMatchExecutionBoundary.cs.meta` | Unity 资源元数据。 |
| `unity/Mahjong.Unity/Assets/Scripts/App/LocalFullMatchPlayerDriver.cs` | 正常 `RunMatchCore` 接入共享边界；删除测试专用影子实现；每轮 terminal 后放弃边界生命周期；测试 fixture 改为注入真实形状 transport/view + budget。 |
| `unity/Mahjong.Unity/Assets/Scripts/App/LocalFullMatchPolicy.cs` | `FullMatchActionPlan` 保留 accepted binding role；`IsOwnActionEvent` 增加角色一致性保护。 |
| `unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalFullMatchDriverPlayModeTests.cs` | 4 个共享边界负例；真实 session 用例接入同一边界；Applied-without-event 负例。 |
| `dotnet/tests/Mahjong.Unity.TableModel.Tests/LocalFullMatchExecutionBoundaryTests.cs` | 新增 21 个定向用例（含 Theory 展开）。 |
| `dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj` | 把生产 `FullMatchExecutionBoundary.cs` 链接进独立测试程序集。 |
| `dotnet/tests/Mahjong.TableRuntime.Tests/InternalExhaustedWallPassTests.cs` | 新增 4 个内部耗墙/交付用例。 |

未改：Domain、Contracts/Wire、Client、LocalHost 装配/回执代码、ConfirmVisibleEventObserver、SmokeActionEventPolicy、恢复 exporter/verifier、移动平台、观战/Compare/视觉。工作树中 Unity 生成的 tracked DLL/字体/Resources/Settings/slnx 均已归档到 ignored `build/` 并恢复 HEAD；没有提交二进制派生产物，也没有回滚用户文件。

## 3. A：共用生产执行边界

### 3.1 实现

`FullMatchExecutionBoundary` 不依赖 Unity API，负责：

- **规划**：`LocalFullMatchPolicy.TryCreateActionPlan` / `TryCreateMandatoryDrawPlan`；自己的 DISCARD 无 decision 时有界等待。
- **单次提交**：每个逻辑动作只调用一次 `SubmitAsync(plan.Command)`；requestId/scope/bytes 由既有 policy 生成，不在此处另造。
- **有界等待**：submit 20s、confirmation 30s、decision wait 60s（`FullMatchExecutionBudget.Production`）。fixture 只注入短预算，不伪造错误码。
- **确认**：`ConfirmedVisibleEventObserver` + `LocalFullMatchPolicy.IsOwnActionEvent`；DrawTile 的 `Applied receipt + 当前 observed RoundEnded` 分支；Pass 的 state-only 分支。
- **ledger**：真实 `Record` / `MarkResolved` / `MarkUnresolved`；未知结果同 ID 不重发，也不换 ID 重做；失败后 `Halt`，后续 Poll 返回同一 terminal outcome。
- **证据**：`PlanRecorded` / `ReceiptObserved` / `ActionConfirmed` / `ActionReplanned` notice 由边界产生，driver 只格式化，不再手写 action/request 行。
- **失败阶段**：`Planning` / `Submission` / `Confirmation`，用于保持既有 exit code 合同（planning → 42，submit/confirm → 43/44）。
- **生命周期**：driver 在每个权威 `RoundEnded` 记录/提交前调用 `AbandonExecutionBoundary()`；迟到的 receipt 不能跨轮污染下一轮视图。这是本轮从真实 Player 证据中修出的集成缺陷（见 §6.3）。

`RunMatchCore` 不再保留 `PlanStep`/内联 observer/ledger/手写 receipt 分支；旧 `RunFaultFixtureForTest(string mode)` 已删除。正常路径和 PlayMode 都调用同一个类。

### 3.2 定向回归（TableModel，21 个新用例）

- 无 decision：`OwnDiscardWithoutADecisionExpiresAtTheBoundedDecisionWaitWithoutSubmitting` —— 真实生产有界等待到期，0 次提交，重复 Poll 仍是同一 `DECISION_WAIT_EXPIRED`。
- never-completing submit：`NeverCompletingSubmitUsesTheBoundedSubmitBranchAndKeepsOneRequest` —— 真实 `SubmitAsync` task 不完成，budget 到期 `SUBMIT_TIMEOUT`，ledger unresolved，1 次提交。
- Sent 缺 receipt：`SentButUnresolvedReceiptTimesOutExactlyOnceAndNeverResendsTheSameId` —— `RECEIPT_UNRESOLVED`，1 次提交，同 ID 不重发。
- Applied 缺 event：`AppliedReceiptWithoutTheMatchingRoundEndedEventFailsAndStaysUnresolved` —— 真实 observer `HasAppliedReceipt` + 空/错 batch，`VISIBLE_EVENT_MISSING`，ledger unresolved，1 次提交。
- 同步 throw / 未发送：`SubmissionThatThrowsSynchronouslyIsUnresolvedAndNotRetried`、`SubmissionNotHandedToTheTransportFailsClosedAsUnresolved` —— `SUBMIT_NOT_SENT`，unresolved，不重发。
- Rejected：`RejectedReceiptIsADefinitiveNoOpAndIsNeverSubmittedAgain`（replanable）、`DefinitiveNonReplanableRejectionFailsClosedWithoutProgress`（非 replanable）、`RejectedDrawAtATerminalEmptyWallIsANoOpAndWaitsForTheSettlement`（terminal/empty-wall no-op，不误确认）。
- 终局确认成功：`MandatoryDrawConfirmsOnlyWithExactAppliedReceiptAndCurrentRoundEndedBatch` —— 断言 command kind/requestId/scope/expectedStateVersion/roundId/payload byte-equal，submit count 1，真实 ledger resolved，`ActionConfirmed` visibleEvent=true。
- 负例矩阵：`AppliedReceiptForADifferentRequestNeverConfirms`（错 request）；`WrongBatchIdentityNeverConfirmsTheAppliedRequest` 的 5 个 Theory（错 recipient seat / round / binding / scope / command-version）；`SpectatorRoleBatchNeverConfirmsAPlayerRequest`（role）。
- 终局无 receipt：`RoundEndedWithoutAnAppliedReceiptNeverConfirmsTheDraw` —— 即使 terminal event 已在当前 batch，没有 Applied receipt 也只 `RECEIPT_UNRESOLVED`，不确认。
- Rejected 不被 terminal event 覆盖：`RejectedDrawReceiptIsNotOverriddenByAMatchingRoundEndedEvent` —— 仍是 replan，不是 confirm。

所有用例使用真实 `SessionViewState`/`TableVisibleEventBatch`/生产 `ProjectPlayer`/真实 observer/ledger；没有传入预期错误码，也没有手写 action/request 日志。

### 3.3 Mutation 证据（最终代码，ignored `build/g2-c01-execution-boundary/mutation-final/`）

| Mutation | 改动 | 结果 |
|---|---|---|
| A | 删除 `IsOwnActionEvent` 的 `batch.Role != plan.Role` 保护 | `SpectatorRoleBatchNeverConfirmsAPlayerRequest` FAIL（Confirmed vs Failed），其余 217 pass；恢复哈希一致。 |
| B | Draw terminal 分支改用 `observedEvent.CommandVersion` 而不是 Applied receipt 的 commandVersion | `WrongBatchIdentityNeverConfirmsTheAppliedRequest("command-version")` FAIL（Confirmed vs Failed），其余 217 pass；恢复哈希一致。 |
| C | 删除 submit timeout 分支的 `_ledger.MarkUnresolved(plan)` | `NeverCompletingSubmitUsesTheBoundedSubmitBranchAndKeepsOneRequest` FAIL（unresolved 断言），其余 217 pass；恢复哈希一致。 |

这三条证明“删除/破坏生产保护会使相关测试失败”，不是孤立 policy 绿。

## 4. B1：终局确认与负例

`Advance` 的 DrawTile 终局分支只在以下全部成立时确认：

1. 该请求的 receipt `Disposition=Applied`、`ReceiptStatus=Applied`、`CommandVersion` 存在；
2. 当前 `VisibleEventBatch` 是 incremental，且 `StateVersion > plan.BaselineStateVersion`；
3. 事件的 `CommandVersion` 等于该 Applied receipt 的 commandVersion；
4. `LocalFullMatchPolicy.IsOwnActionEvent` 校验 round/role/session/room/table/scope/seat/binding/connection/owner；
5. 事件类型为 `RoundEnded`，且这是 DrawTile 的 terminal draw。

缺 receipt、Rejected、错 request/version/round/seat/role/binding/scope 的处理见 §3.2；它们要么 `VISIBLE_EVENT_MISSING`，要么 `RECEIPT_UNRESOLVED`，要么 replan 后等待，不会推进。`ConfirmVisibleEventObserver` 与 `SmokeActionEventPolicy` 没有被修改来放宽条件；`LocalFullMatchPolicy` 只增加了 role 一致性保护。

## 5. B2：内部耗墙与 reservation

`dotnet/tests/Mahjong.TableRuntime.Tests/InternalExhaustedWallPassTests.cs`（4 个）：

1. `InternalTimeoutPassAtAnExhaustedWallCommitsWallExhaustedOnce`：真实 `TableActor.TickAsync` → timeout → `ApplyInternalCommandAsync`；wall 0、Pass、WallExhausted、无 TileDrawn、1 次 commit、136 张；第二次 Tick 空。
2. `InternalAiPassAtAnExhaustedWallCommitsWallExhaustedOnce`：真实 `CompleteAiAsync` → AI 内部入口；同上；重复 AI 决策 `AiRejected`、版本不变。
3. `InternalAiPassWithLiveWallDrawsNormallyInsteadOfExhausting`：有墙、无 reservation 对照；正常 TileDrawn、wall-1、版本 1、未 finished、136。
4. `InternalAiPassDeliversARealReservationToTheOwnerDrawExactlyOnce`：沿真实 `SubmitCheatAsync`（SleeveBloom off-turn → choice）产生 reservation，再由 AI Pass 交付；断言 TileDrawn tile、`SleeveBloomReservationDelivered`、commandVersion 7、无 CheatPointsChanged、reservation 清空、136。

关于 reservation 正例复用：已通过的 `reservation_is_delivered_on_the_next_real_draw_without_a_second_charge`（`N1bCheatExecutionTests.cs:639`）和 `response_pass_delivers_a_reservation_to_the_next_owner_draw`（`:1865`）原样保留，没有另造同义正例。新增的第 4 个用例只覆盖既有正例没有覆盖的 **AI 内部入口**；没有把 Hand 合成 `WithReservation` 当作 live-Wall 交付正例。第 1、2 个耗墙用例的 reservation 只作为“非交付 guard”，与外部 `ExhaustiveWallPassExternalEntryDoesNotRequireReservationDelivery` 同形，并额外断言 reservation 未被交付。

没有发现需要修改 `TableActor` 的内部缺陷；本轮未改 TableActor。

## 6. Unity 与 Player

### 6.1 Unity 验证环境

- Unity `6000.3.27f1 (672d562a42a7)`，Windows 10 0.19045，x64。
- 全程持有命名 mutex `Local\MahjongV5P45UnityValidation`；PlayMode 与 Player 互斥；只管理自己按 PID/start/executable/hash 确认的进程。
- PlayMode 使用 `-batchmode`（**不加 `-nographics`**；继承 runner 的 `-nographics` 会在 fresh Library 上触发 TMP importer 的 `No graphic device` 未处理错误，见 §6.2）。

### 6.2 首次失败与工具/环境探针（全部保留）

1. **继承的 Unity license 失败**（attempt-1，旧轮次）：native 198，`No valid Unity Editor license found`。已保留 `unity/attempts/attempt-1-license-blocked/`。
2. **继承的 PlayMode 编译失败**（attempt-2）：`FullMatchSessionTransport` 未找到、`PlayerControlView` 二义。已在最终代码修复；attempt-2 日志保留。
3. **本轮 attempt-4**：`-testFilter '*LocalFullMatchDriverPlayModeTests*'` 被 Unity 当正则解析，`ArgumentException: Quantifier {x,y} following nothing`，native 3。改用精确类型名后继续；attempt-4 保留。
4. **本轮 attempt-5**：PlayMode 7 例中 6 过、1 失败；唯一失败是 `MatchModeRealSession_CompletesOneRoundAndOpensNext` 遇到 TMP importer 的 `Unhandled log message: 'No graphic device is available...'`。去掉 `-nographics` 后 attempt-6 7/7；attempt-5 保留。
5. **attempt-7**：runner 的 mutex 被前一个被终止进程遗弃，`AbandonedMutexException`；runner 改为按 OS 语义接管后继续。该失败不是代码失败，attempt-7 保留。

最终 PlayMode：attempt-10，7/7，exit 0；XML SHA-256 `85E9958C5920C5402F62748796E1A6F11AF67D02917C74A8849312D19417E998`。

### 6.3 fresh Windows Player：首次失败、探针、修复、最终失败

**首次失败（本任务第一个真实 Player 失败，已完整保留）**

- 命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-full-match.ps1 -Mode All -RunDir build/g2-c01-execution-boundary/player-full-match-20261031 -TimeoutSeconds 5400`
- 构建：Unity build native 0，Player manifest 205 files，build-shared/check plugins 0。
- 运行：`p45-20261031-230952-c2e282b0`，Player native **43**，`RECEIPT_UNRESOLVED`，request `fm-draw-0ceebb1df5ee842fc40b4a709453bc88`，round `local-main-r1`，baseline 83，30s 无 receipt；`pending=0`、`lastError=<none>`。19 个 Discard/Pass 均已确认；第一个强制 `DrawTile` 失败。
- 证据：`build/g2-c01-execution-boundary/player-first-failure-20261031-2310/`（summary/full-match.jsonl/player.log/exit/process/outbox/source/player-files/unity-build/build-shared/check-plugins + SHA-256 列表）。

**第二次复现**：`p45-20261031-231317-dc9f7737`，native 43，baseline 81；同一症状。证据保留在同目录 `player-second-failure-20261031-2314/`。

**探针 1（wall-0 客户端 guard + host settle，已回退）**：为了区分“客户端不应在空墙摸打”与“host 不应只给 AI 结算”，曾临时改 `FullMatchBoundary` wall-0 wait 和 `LocalTableDriver` 对所有 seat 结算。结果 `p45-20261031-233055-992351fa` 在 r2 state 83 连续 3 次 session recovery 后 `ROUND_STALLED`（native 49）。该探针证明 host driver 不是这条 match 的实际结算 owner；相关改动已完整回退，`LocalTableDriver.cs` 恢复 HEAD。证据保留在 `player-full-match-20261031-b`。

**探针 2 / 真实集成缺陷（已修）**：`p45-20261031-234228-a05845f1` 在 r6 先观察到 `RoundEnded`（state 88，`DrawGame#175.0@cmd88`、`RoundEnded#176.1@cmd88`），driver 记录并提交 r6、打开 r7；30s 后 r6 的 Applied draw receipt 才到，旧边界仍持有 r6 的在途 plan，于是被 r7 视图判成 `VISIBLE_EVENT_MISSING`（native 44）。根因是 **boundary 跨轮生命周期**，不是 receiver/确认条件。修复：每个 terminal round 在 `RecordRoundCompletionAsync` 前调用 `AbandonExecutionBoundary()`，销毁 boundary/transport，迟到 receipt 不再跨轮。PlayMode round-transition 用例仍 7/7。证据保留在 `player-full-match-20261031-final`。

**最终失败（最终代码，必须保留）**

- 命令同上，`-RunDir build/g2-c01-execution-boundary/player-full-match-20261031-final2`
- Unity build native 0；Player manifest 205 files，manifest SHA-256 `5F88A1716C7C5C5928B1C36ADAFA7062CC570E101552CE2FF5421902770B5AAE`
- 运行：`p45-20261031-234942-9ad79bef`，Player native **43**，`RECEIPT_UNRESOLVED`
- 失败请求：`fm-draw-fc158796e013ea8512b4a6ae3c8dc370`，round `local-main-r1`，baseline 77，30s 无 receipt；`state=Ready`、`pending=0`、`lastError=<none>`；没有任何 `round_end_observed`/`round_terminal`。
- 该失败发生在 **玩家第一个强制 DrawTile**，不是 r15 的 12 局路径；boundary 的 lifecycle reset 没有机会触发（没有 terminal）。
- 证据：`build/g2-c01-execution-boundary/player-final2-failure-20261031-2350/`（summary `6B76BD35...`、jsonl `B4B27DFF...`、exit/outbox/player.log/source/player-files + SHA 列表）。

**结论口径**：fresh Player **基本冒烟 PASS**（同一个 final2 Player，LocalSmoke，native 0、tool 0、14-entry manifest `FB0E110D...` 校验通过）；fresh Player **完整整场 FAIL**。不得把基本冒烟、r15 旧样本或组件 7/7 写成完整整场通过。

## 7. 完整命令、计数与原生 XML

最终 .NET 证据在 `build/g2-c01-execution-boundary/final-evidence-20261031-2355/`。为避免 mutation 恢复后 timestamp 早于 DLL 导致的旧 DLL 增量构建，三项目均用 `-t:Rebuild` 强制全量重建，再 `--no-build` 运行。

| Suite | Total | Pass | Fail | Skip | native exit | 原生 XML SHA-256 |
|---|---:|---:|---:|---:|---:|---|
| `Mahjong.Unity.TableModel.Tests` | 218 | 218 | 0 | 0 | 0 | `592A4F3E9502A03E55E5144320006382F67012C1A2A6F41E4C650D2A614DA45A` |
| `Mahjong.TableRuntime.Tests` | 482 | 482 | 0 | 0 | 0 | `412EF1E330AC4CF03192518E295F1AB86E163514FAF57161B247E0F8278C2135` |
| `Mahjong.LocalHost.Tests` | 127 | 127 | 0 | 0 | 0 | `FCF0FFA4A2A2DEA42A2FF906858528F29C0C91ECAFFAE47AE3637B16C58B5F7F` |
| `VerifyP45FullMatchDriver.Tests.ps1` | 32 | 32 | 0 | 0 | 0 | log `D8315A50...` |
| Unity PlayMode `LocalFullMatchDriverPlayModeTests` | 7 | 7 | 0 | 0 | 0 | `85E9958C...` |
| Windows Player basic LocalSmoke | — | PASS | 0 | 0 | native 0 / tool 0 | manifest `FB0E110D...` |
| Windows Player full match | — | **FAIL** | 1 | 0 | native 43 / tool 10 | summary `6B76BD35...` |

关键命令（原样）：

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.CrashWorker/Mahjong.LocalHost.CrashWorker.csproj -c Release -t:Rebuild
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj -c Release -t:Rebuild
D:/Unity/dotnet/dotnet.exe test  dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj -c Release --no-build -- --results-directory <dir> --report-xunit-xml --report-xunit-xml-filename results.xml
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release -t:Rebuild
D:/Unity/dotnet/dotnet.exe test  dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build -- --results-directory <dir> --report-xunit-xml --report-xunit-xml-filename results.xml
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release -t:Rebuild
D:/Unity/dotnet/dotnet.exe test  dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-build -- --results-directory <dir> --report-xunit-xml --report-xunit-xml-filename results.xml
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyP45FullMatchDriver.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-full-match.ps1 -Mode All -RunDir build/g2-c01-execution-boundary/player-full-match-20261031-final2 -TimeoutSeconds 5400
```

`git diff --check` exit 0；`git status --porcelain` 在最终提交后应为空。`source-code.patch` SHA-256 `E20C6A5DA07E81291F0E44CA435D961088F43F0E22A2FF1E03C27ABCB39A56ED`（代码/测试变更，排除 Unity 生成物）。

## 8. 缺陷映射

| 原缺陷 | 本轮处理 | 状态 |
|---|---|---|
| D-1-05(a) 失败 fixture 直写 Fail，绕过生产路径 | 抽取 `FullMatchExecutionBoundary`；正常/PlayMode 共用；删除影子 fixture；真实失败条件 + ledger + observer + 单次提交 + 同 ID 不重发；mutation 证明保护。 | 组件/PlayMode 级关闭；完整 Player 仍受 §6.3 receipt 缺口阻断。 |
| D-1-05(c) 终局确认 `Applied + RoundEnded` | 纳入共享边界；精确成功 + 无 receipt/Rejected/错 request/version/round/seat/role/binding/scope 负例；late-receipt 跨轮生命周期修复。 | 边界级关闭；真实 Player 的强制 Draw receipt 仍未回。 |
| D-1-05(c) 内部 timeout/AI 耗墙、有墙无 reservation、真实 reservation 交付 | 4 个真实 TableActor 内部用例；外部入口正例保留；reservation 正例复用映射。 | 组件级关闭。 |
| D-1-05(b) match-channel deadline 退役旧 decision | 未重开；LocalHost 127/127 仍包含 `RealMatchChannelDeadlineRetiresDecisionAndRejectsStaleControl`。 | 保持关闭（历史成果）。 |
| r15 12 局整场 | 未重跑替代；历史证据保留；新 full-match 失败不推翻 r15 历史，但也不能用 r15 证明新代码。 | 历史通过，非本轮 fresh 证明。 |

## 9. 未关闭项 / 平台边界

1. **Fresh Player 完整整场 FAIL（MAJOR/BLOCKER 级）**：玩家首个强制 `DrawTile` 偶尔 30s 无 receipt，且没有 RoundEnded push。当前证据定位到 client persist-before-send / LocalHost draw→settlement receipt 交界：
   - `dotnet/src/Mahjong.Client/TableSession.cs:344`（persist-before-send，`Sent=true` 后等待 `ResolveTrackedAsync`/`ReceiptResolved`）；
   - `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs:865`（wall-0 DrawTile → ExhaustiveDraw settlement conversion）；
   - `dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs:186`（human Draw 不主动结算；AI-only 分支）；
   - `dotnet/src/Mahjong.Client/TableSession.cs:1716`（`ReceiptResolved` 只在 `ResolveTrackedAsync` 时触发）。
   这些文件不在本任务 A/B 允许范围；未改 Client/LocalHost 回执实现，也未用放宽/重试掩盖。需要独立 host-receipt 任务或调用方明确授权后再修。
2. **Android/iOS/移动平台、观战、Compare/恢复导出、视觉截图结论**：NOT_RUN/未覆盖；基本 Player 冒烟只证明 Windows hidden Player 的启动/菜单/大厅/一局/一动作/退出，不证明整场、三绝技、AI、流局/和牌或视觉。
3. **G2/P4.5 未批准**：A/B 组件绿不等于阶段绿；调用方独立 review 后才可能批准，PR20/PR24 统一收口不在本任务执行。
4. **r15 旧样本**：只作为历史通过证据；不作为新代码 fresh Player 通过证明。

## 10. 复核入口

- 代码/测试：`5b52df3ff2746c72fc3e27f5dc8b925fcb3abbd9`
- 组件证据索引：`build/g2-c01-execution-boundary/final-evidence-20261031-2355/evidence-index.json`
- PlayMode：`build/g2-c01-execution-boundary/unity/attempts/attempt-10/`
- Player 首次失败：`build/g2-c01-execution-boundary/player-first-failure-20261031-2310/`
- Player 最终失败：`build/g2-c01-execution-boundary/player-final2-failure-20261031-2350/`
- Player 基本冒烟：`build/g2-c01-execution-boundary/player-basic-smoke-20261031/`
- Mutation：`build/g2-c01-execution-boundary/mutation-final/`
- 生成物恢复：`build/g2-c01-execution-boundary/generated-final-restore-20261031-2352/`

本报告只表示本轮代码/测试交付与证据，不表示 G2-C01 或 G2 已通过，也不构成 PR 合并、部署或 complete.flag。
