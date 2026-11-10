> 复核结论（2026-11-11）：接受原简报明确允许的调查/停止条件交付。T1 reviewer 独立重跑 1,443/1,443；远端 API-15 未实现、未通过。此文档记录受测基线 `58b27b00696c458585a790409931c121f3e51314`，不表示发布分支或远端 main 已跑过这些测试。
>
> 下方任务卡和执行记录来自开发阶段；“未提交/推送/合并”描述开发者交付时状态。末尾复核记录和 caller 文档 MR 是后续收口，原始日志未改写。
# Remote T1/T2 task card — 2026-11-11

- Task ID: `remote-t1-t2-20261111`; objective: map existing T1 U-08/U-09/U-10 coverage and add one API-15 remote real Client control reclaim journey.
- Executor/reviewer: Codex Luna implements; primary agent designs and reviews.
- Worktree: `D:/majiang-wt/remote-t1-t2-20261111`; branch `task/remote-t1-t2-20261111`; HEAD `58b27b00696c458585a790409931c121f3e51314` (accepted baseline).
- Existing changes: clean at start. No commit, push, merge, or deployment.
- Allowed changes: existing B1 worker/runner and task evidence documentation. Excluded: production code/config, TLS/trust weakening, public test switches/injection, new framework, Kestrel/LocalHost/raw-probe substitution, destructive edits.
- T1 reuse: Domain `CheatLedgerTests`, `DecisionClockTests`, `ControlRegistryTests`; TableRuntime.Tests, Client.Tests, Client.Wss.Tests. Add nothing unless review finds a concrete uncovered pure contract.
- Fixed API-15 assertions: initial current control/decision; real WSS close; fresh production Client WSS reconnect; connected projection without epoch/control change; old epoch/decision rejected without game state side effect; explicit claim receipt and newer epoch/control projection; fresh legal discard accepted once. Synchronize with client baseline/receipt/projection barriers and bounded waits, never sleeps as a success condition.
- Existing valid remote evidence: B1 real LobbyService/TableService processes + isolated PostgreSQL + production Client/TableSession/OperationQueue + strict root-trust WSS; B2 separately has remote terminal/recovery evidence. Reuse the existing B1 runner; neither counts as this run.
- Output root (unique, exclusive): `artifacts/remote-t1-t2/208bf21758f54ad9930ffb8abcb5fba8`; runner logs under `artifacts/remote-t1-t2/208bf21758f54ad9930ffb8abcb5fba8/logs`; root and logs must remain inside workspace `artifacts/` per B1 runner guard. Preserve first failure.
- Stage A: verify source/runner/API, current worktree and tool/PG prerequisites. Entry now; success means clean baseline and exact source entrypoints confirmed.
- Stage B: implement worker-only journey, compile worker, inspect final diff. Bounded build 10 min; outputs in unique root.
- Stage C: run existing B1 HTTPS admission runner against real isolated PG and services, including all existing B1 cases plus API-15. Script suite timeout 360 sec; allow total 10 min; record heartbeat every 30 sec; stop only processes started by this invocation and let runner clean normally.
- Stage D: verify native exit, actual scenario assertions/counts, worker and service logs/XML/summary, and source hashes; preserve first failure and report PASS/FAIL/BLOCKED/NOT_RUN accurately.
- Shared resources: isolated PostgreSQL/ports per script defaults (58471/58472/58473); no shared Unity/device lock. If ports busy, do not kill/steal; rerun only with ports confirmed free and changed inputs recorded.
- Retry policy: only after root cause repaired, inputs changed, or blocked resources recovered. No blind reruns.
- Final binding: exact working tree source, build used by script, command/exit code, actual scenario count, evidence paths and any omitted T1 scope.

## 本轮执行结果与结论

状态：T1 映射及选定测试通过；远端 API-15 BLOCKED。工作树保留本任务卡和 artifacts 证据；B1 worker 源码改动已撤回，避免把失败旅程留在现有 runner 中。未提交、推送或合并。

### T1 U-08/U-09/U-10 复用映射

- U-08 账本/幂等：`Mahjong.Domain.Tests/CheatLedgerTests.Apply_ChargesExactlyOnceAndRecordsEntry`、`IdenticalPayload_ReplaysOriginalEntryWithoutStateChange`、`SameEntryId_DifferentPayload_IsConflictWithZeroSideEffects`；`Mahjong.Contracts.Tests` 的规范载荷与 request digest 契约。没有发现需要复制的纯逻辑合同缺口。
- U-09 时钟/控制：`DecisionClockTests.PauseResumeAndControllerReplacementKeepOldCallbackAndDecisionStale`；`ControlRegistryTests.ReconnectAloneDoesNotReclaimAndExplicitReclaimBumpsEpoch`、`StaleGraceTimerCannotTakeOverANewerController`；`TableControlTimingTests.ReconnectNeverReclaimsButExplicitReclaimInvalidatesOldAiAndPlayerEpochs`、`StrictRecoveryFenceRejectsOldAiAndOldPlayerButAcceptsFreshObservation`。
- U-10 Client/绑定/通道：`TableSessionTests.SubmitRetainsRequestScopeAndOriginalCanonicalBytes`、`OldBindingMessagesDoNotOverwriteNewBaseline`、`RestartRecoveryQueriesTheOriginalScopeAndRetriesTheExactIdentity`；`Client.Wss.Tests` 中 `HelloSendFailureBeforeFirstWelcomeRemainsReconnectable`、`ChannelOpenFailureBeforeFirstWelcomeRemainsReconnectable`、`TransportFailureDuringLookupClearsInFlightAndReissuesAfterReconnect`。现有测试已经覆盖纯逻辑边界；遗漏是生产服务与真实 Client 的成功控制回执互操作，不应复制 T1。

### 实际 T1 执行

官方 `tools/verify-v5.ps1` 定向运行 solution 内 3 个工程：Domain 534/534、Contracts 152/152、Client 182/182；总计 868/868，失败/错误/跳过/未运行均 0，原生退出 0。XML 和摘要位于 `artifacts/remote-t1-t2/208bf21758f54ad9930ffb8abcb5fba8/t1-solution-verify/`。

`TableRuntime.Tests` 和 `Client.Wss.Tests` 不在当前 solution，故按各项目 xUnit 直接入口分别 fresh build 后运行：493/493 和 82/82，失败/错误/跳过/未运行均 0，两个 build 和两个 runner exit 均 0。XML：`Mahjong.TableRuntime.Tests-direct.xml`、`Mahjong.Client.Wss.Tests-direct.xml`；日志在同目录 `logs/`。

首次把五个项目都放入显式 manifest 的 verifier 运行以 exit 1 结束，原因是 verifier 拒绝要求 solution 未调度的 TableRuntime/Client.Wss 工程（`required project is not scheduled in the solution`）；该配置失败原件在 `t1-focused-verify/summary.log`。修正入口后未把该配置失败算为测试失败或通过。

### API-15 阻断证据

使用原 `tools/ci/verify-v5-https-admission.ps1`，真实 LobbyService、TableService、隔离 PostgreSQL、生产 Client/TableSession/OperationQueue 和严格根证书 WSS。阶段门禁、PostgreSQL、服务新构建及 B1.Tests 均通过；B1.Tests XML 是 54/54。worker suite 原生 exit 1，执行到既有 `crash_command_committed_and_receipt_lost` 后报 `ClientBlockedException`，没有产生任何 `api15_*` 通过断言。首败保留在 `artifacts/remote-t1-t2/208bf21758f54ad9930ffb8abcb5fba8/b1-logs/`，包括 `suite.log`、`suite.json`、服务日志、`runner.log`、`wrapper-summary.json`、`manifest.sha256.json`；runner 已确认自有 PostgreSQL 清理完成、端口释放。

源码根因：TableService `TableSessionHandler` 的成功控制分支发送 `ReceiptStatus.Applied`，只写 `ReasonCode` 和 evaluated state version，没有 `command_version` / `event_seq` / `canonical_payload_sha256`。生产 Client `TableSession.HandleReceiptAsync` 的特殊本地控制确认只匹配 `ClaimControl/ReleaseControl + LOCAL_CONTROL + Recorded + matching non-empty canonical digest`；其他 `Applied` 会进入普通 `BuildResolution`，而 `OutboxResolution.FromReceipt` 明确要求 Applied/Recorded 回执有 command version 和 event sequence。两侧合同不兼容，Client 无法完成成功控制回执确认；这不是测试等待或固定牌面问题。日志还记录 `StaleControlEpoch` stream termination，说明控制 epoch 前进会使旧流终止，这应在控制回执可靠确认后通过新基线/新绑定处理，不能压掉它来假绿。

最小替代方案：由生产协议所有者修复成功控制回执合同，使服务发送 Client 已定义的 `RECORDED + LOCAL_CONTROL` 控制回执，并提供与原请求匹配的 canonical payload digest（不伪造 commandVersion/eventSeq）；拒绝结果继续走 rejected 回执。随后用同一 API-15 旅程补充真实连接证据。这是源码静态确认的互操作阻碍，不是本次 suite 异常的已证实因果。生产代码修复不属于本轮测试改动，因此本轮停止在此，不保留失败的 worker 测试改动，也不宣称 API-15 通过。

### 实际命令与结果

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -RunnerMode direct -NoProjectDiscovery -SkipPluginCopyCheck -ManifestPath artifacts/remote-t1-t2/208bf21758f54ad9930ffb8abcb5fba8/t1-solution.manifest.json -LogDir artifacts/remote-t1-t2/208bf21758f54ad9930ffb8abcb5fba8/t1-solution-verify
```

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-https-admission.ps1 -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -Root artifacts/remote-t1-t2/208bf21758f54ad9930ffb8abcb5fba8/b1-root -LogDir artifacts/remote-t1-t2/208bf21758f54ad9930ffb8abcb5fba8/b1-logs -SuiteTimeoutSeconds 360
```

远端 runner 命令 exit 1；B1.Tests 自身 XML 54/54 PASS，但整个 B1/T2 阶段 FAIL，API-15 为 BLOCKED，不能把部分组件绿灯写成 T2 通过。

## 独立复核与收口

- 任务：`thread_20261111_012014_c8a1`，沿用同一任务和 reviewer 会话。桥接源码工作树 `E:/agent-space/thread_20261111_012014_c8a1/worktree`，分支 `mesh/task/thread_20261111_012014_c8a1`，受测 HEAD 为 `58b27b00696c458585a790409931c121f3e51314`。源码 diff 为空；调查报告原先仅在上方 D: 任务目录，已由 caller 汇总进本 MR。
- 原简报明确要求：如现有基础设施不支持可靠/正确旅程，停止编码并说明确切阻碍与最小替代方案。此次接受的是该停止条件交付，未把 API-15 或整个 T2 判为通过。无新生产代码、无新测试、无删除/放宽断言，规则、隐私与守恒合同不变。
- reviewer 对覆盖映射逐项核对代码，并在桥接源码工作树 fresh build 后使用生产项目的直接 xUnit 入口重跑：Domain 534、Contracts 152、Client 182、TableRuntime 493、Client.Wss 82，合计 1,443。五次 build 和五次 test 的 native exit 均为 0；XML 全部无失败、错误、跳过或未运行。
- 本次独立证据根：`E:/agent-space/thread_20261111_012014_c8a1/worktree/artifacts/remote-t1-t2-review/9a436d6e0de542e0929fac1930023bc5/`。相邻较早 GUID 仅是 reviewer 进程封装未保留 ExitCode 的失败记录：Domain 实际构建成功，但该封装取得 null 后停止，未计为测试通过；改用持有进程句柄的封装后完成本次运行。原件保留。
- 每个工程的实际命令形式如下，P 依次取上面的五个完整工程名（加 `Mahjong.` 前缀及 `.Tests` 后缀；Wss 为 `Mahjong.Client.Wss.Tests`）。每个外部进程上限 120 秒，独占输出，不使用共享数据库、Unity 或设备：

```powershell
D:/game_dev_tools/dotnet/dotnet.exe build dotnet/tests/P/P.csproj -c Release --nologo
D:/game_dev_tools/dotnet/dotnet.exe dotnet/tests/P/bin/Release/net8.0/P.dll -xml <独立证据根>/P.xml
```

- 计数、XML SHA-256、逐工程完整命令见相邻 `REMOTE_T1_T2_REVIEW_20261111.json`；原 XML、构建/运行日志保留在上述本地证据根，不作为远端下载附件承诺。
- **首败边界校正**：开发者 `suite.json` 最后一个成功断言是 `crash_command_committed_and_receipt_lost`，随后为 `ClientBlockedException`；`RecoverPid=0`、`RecoveredProjectionConfirmed=false`，甚至未记录 `real_os_kill_client`。因此不能写成“恢复完成后失败”，也不能仅靠这份脱敏日志认定异常由 claim 控制回执导致。控制回执不兼容由源码独立确认（服务 `TableSessionHandler.cs:1565,2670`，客户端 `TableSession.cs:1452,1862` 与 `Outbox.cs:253`），不是本次远端异常的已证实调用链。
- 生产修复的候选是让远端控制回执与客户端支持的控制确认合同对齐；`LOCAL_CONTROL + RECORDED + digest` 是当前客户端已实现路径，但是否作为远端稳定合同需协议所有者决定，不能把本报告当成改协议授权。还需确认 epoch 前进、旧流失效、新基线绑定及未决控制请求对账语义。
- reviewer 未重跑输入未变的远端 B1，保留开发者原 exit 1；B1 单元 54/54 仅是原件复核，未冒充独立重跑。API-15 新断言为零，仍需生产合同修复后进行真实远端正负例验证；没有 G2、Windows/Android 或上线验收结论。
- 通过 `approve_task` 接受调查交付；没有依赖线程待交付，不发无代码返修。近期已验收改动已分别进入现有 PR28/29；其他仍在运行的独立 UI/AI 任务不是本测试任务的依赖，不取其未交付修改。本次唯一 MR 只收录本任务尚未推送的两份调查/复核文档。
- 文档发布工作树：`E:/agent-space/thread_20261111_012014_c8a1/mr-worktree`，分支 `publish/remote-t1-t2-review-20261111`，从实际远端 `origin/main` 的 `93ce7cc7b4a2599f8a83ac41e8d8fde110a5e7d5` 建立以避免重复携带既有 PR 的代码。受测源码仍是上方 58b27b 基线；发布树只作报告承载，未在该较早 main 上重跑产品测试。caller 仅提交/推送文档和创建 MR，不合并或部署。