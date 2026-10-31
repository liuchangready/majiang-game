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

---

## 11. R3 返修（2026-11-01）

本节是 R3 返修交付，覆盖本轮 3 个 MAJOR 中已授权的两组，并纠正 R2 报告的越界与推断。R1 正文与历史证据保留。

### 11.1 C01-R2-01：terminal 事件历史（已修复）

R2 的 Draw terminal 分支只读 `_transport.View.Current.VisibleEventBatch`；observer 已观察到的 terminal 会被后续空/不同版本 publication 覆盖。R3 改为遍历 `ConfirmedVisibleEventObserver.Batches` 的不可变历史，仍用既有 `LocalFullMatchPolicy.IsOwnActionEvent` 的 round/role/scope/seat/binding/connection/owner/commandVersion 全量 fence 配对；`View.Current` 不再替代事件历史。

新增 4 个 TableModel 用例：

- `DrawTerminalObservedBeforeLaterEmptyPublicationStillConfirms`：terminal cmd6 先到，空 publication cmd7 后到，Applied cmd6 后到 → 1 confirm / 1 commit。
- `DrawTerminalObservedAfterIntermediateEmptyPublicationStillConfirms`：空 cmd6 先到，terminal cmd7 后到，Applied cmd7 → 1 confirm。
- `LaterDifferentVersionTerminalDoesNotEraseTheEarlierObservedTerminal`：terminal6 后再来 terminal7，Applied6 仍配对 terminal6。
- `DrawReceiptForAnUnobservedVersionDoesNotConfirmAnEarlierTerminal`：只有 terminal6，Applied7 → `VISIBLE_EVENT_MISSING`，0 commit/unresolved。

**验证**：TableModel **235/235**；Mutation E（把历史遍历退回 `View.Current`）实际 **2 failed / 233 passed / 235 total**，失败为 `DrawTerminalObservedBeforeLaterEmptyPublicationStillConfirms` 与 `LaterDifferentVersionTerminalDoesNotEraseTheEarlierObservedTerminal`；恢复哈希一致。PlayMode attempt-13 **7/7**，包括此前 attempt-12 失败的同一 wall-0 request `fm-draw-0ceebb...`。

### 11.2 C01-R2-03：范围恢复（已修复）

- 删除 `SessionCoordinator.cs` 中越界的 `ReconcilePendingAsync` 28 行；删除 `IFullMatchExecutionTransport.ReconcileAsync`、budget reconcile 字段、boundary `ObserveReconcile`/字段/两个 reconcile 用例及两个 transport 实现。
- 删除越界报告 `V5-G2-C01-EXECUTION-BOUNDARY-DELIVERY-R2-20261101.md`；R2 的有效内容并入本文件本节。已提交历史与 ignored 证据保留。
- 最终 `git diff --name-only c3c95af..HEAD` 只涉及原 expectedFiles 的 9 个文件（R1 的 8 个代码/测试 + 本报告）；`SessionCoordinator.cs` 与 R2 报告在最终树中为零差异。
- 未使用既有允许 API 之外的生产扩展；没有请求新的 scope 授权。

### 11.3 C01-R1-02：保留的 full-match 阻断（未关闭）

**观察事实**：

- 最终代码 fresh Player `p45-20261101-013326-201333eb`（native 43，tool 10）在 `local-main-r1` 的首个玩家强制 `DrawTile` 失败：
  - request `fm-draw-ba0bdf37a073a97425ca380c46b8c24e`，baseline 80
  - `command_submission`：`sent=true`、`handedToTransport=true`、`needsReconciliation=true`
  - 30s 内没有 `transport_receipt_seen`、没有 `client_receipt_seen`、没有 `RoundEnded`；`RECEIPT_UNRESOLVED`。
- R2 成功样本 `p45-20261101-005817-57a45a02` 保留：12 局、`matchFinished=true`、`WestCompletion`、native 0/tool 0；该样本的玩家摸打均为正常有牌摸打，未覆盖本次 baseline 80 的路径。
- PlayMode attempt-13 7/7；R2 attempt-12 的 6/7 失败记录和 XML `1530D961...` 保留，不删除、不重写。

**未证实推断撤回**：R2 报告曾把“无 coordinator `ReceiptResolved`”写成 host 没有 receipt / wall0 根因。R3 不这样断言。当前只能确认：客户端已提交并 handoff、指定 30s 内没有 coordinator receipt 事件、没有 RoundEnded；**没有** host egress/`SendAsync` 返回/原始 lookup 结果，也**没有**证明 Client resolver 从未进入或 wall0 必为根因。没有据此编辑 Client/LocalHost/Domain/Contracts。

**所需下一步（caller 裁决）**：在只读 probe 中包装真实 `ITableChannel`，记录原 canonical 帧、host `SendAsync` 返回/异常、raw receipt/lookup 响应、outbox/pump/派发边界；或授权最小 LocalHost draw/settlement 诊断入口。当前不自动扩层。C01-R1-02 仍为 MAJOR/未关闭。

### 11.4 R3 最终计数

| Suite | Total | Pass | Fail | Skip | native exit | XML SHA-256 |
|---|---:|---:|---:|---:|---:|---|
| TableModel | 235 | 235 | 0 | 0 | 0 | `46D289FB...` |
| TableRuntime | 482 | 482 | 0 | 0 | 0 | `A1270FA2...` |
| LocalHost | 127 | 127 | 0 | 0 | 0 | `ACBE4E2C...` |
| Tool | 32 | 32 | 0 | 0 | 0 | log `6745E63F...` |
| PlayMode attempt-13 | 7 | 7 | 0 | 0 | 0 | `CD0BF2C8...` |
| Player R2 success sample | — | PASS | 0 | 0 | 0 / 0 | summary `6B99801B...` |
| Player R3 final | — | **FAIL** | 1 | 0 | native 43 / 10 | summary `8DAA9944...` |

- R3 代码/测试提交：`077c273b10f5c6d730be0afe2515781627e9f8c0` + scope 修复 `1a42528e29370f94c928e0d7fe9177e37e905489`
- R3 报告提交（最终 HEAD）：本文件所在提交
- `git status --porcelain` 空；`git diff --check base..HEAD` 0；最终差异 9 个原 expectedFiles。
- 未 push/PR/MR/merge/deploy/scheduler/complete.flag；未自行批准。
