# V5 LocalHost 拒绝回执持久化交付报告 (D-LH-GATE-02) — 2026-10-29

- 工作树: `D:\majiang-wt\lh-rejected-receipts-glm-20261029`（分支 `wt/v5-lh-rejected-receipts-glm-20261029`）
- 基线（冻结）: `eeff5f9...` → 完整 SHA `eeff5f9688d95afb5d8187137baab92103f1c8ce`（= 已审查发布的 PR20 头）
- 首轮交付: `06c53de`（fix）+ `f75b6be`（test）+ `4833a19`（docs），审查冻结 HEAD = `4833a19d009e65f083b26895ac52179701d0cf9d`
- 复核轮（本报告第 1a 节 D-LH02-R1-01..04 全部修复）证据构建/运行 SHA: `18f1693ea14e5c8c88131335785cacfab8fe30ad`（tree `1d839cb110452bb72862aea263c1995f18693a8a`；第 5 节全部 fresh 构建与运行产自此快照）
  - `3b81849` fix(v5): authorize new stale-decision rejections on both command paths
  - `18f1693` test(v5): cold-session replay, unauthorized-scope and cancellation regressions
  - HEAD 本身 docs(v5) 提交在 18f1693 之上仅追加本报告文件，源码与测试零变化（SHA 因报告内容含自引用以提交说明定位）
- 证据根（本树 gitignored）: `build/lh02-glm-12a21ae95767/`，最终快照 `final-18f1693/`（`git archive 18f1693` 展开，无 .git）
- 范围声明：本报告只关闭 **D-LH-GATE-02 (MAJOR)** 及其复核轮四缺陷（D-LH02-R1-01/02/03 MAJOR、D-LH02-R1-04 MINOR）。不宣称 LocalHost 批次整批通过、不宣称 Unity 相关目标通过、不宣称 G2–G5 任一通过（V4 仍是唯一生产架构；G1 APPROVED 维持不变）。

---

## 1. 缺陷清单与逐 ID 映射

| 缺陷 ID | 描述 | 状态 | 修复位置 |
|---|---|---|---|
| D-LH-GATE-02 (MAJOR) | LocalHost 旧 decision 控制拒绝只 enqueue 不持久化：`HandleControlCommandAsync` 先 `GetControlResult` 重放/冲突咨询，再 `TryRejectStaleDecision` 早退，早于任何 control outcome 持久化；回包丢失后真实 Client outbox 对账时 receipt query 永远 NotFound | **已关闭** | `LocalTableRuntime.RejectStaleDecision/RejectDurably` + `LocalTableStore.TryCommitRejectedReceipt` |
| （同类缺陷，同窄机制一并修复） | 普通命令路径复用 `TryRejectStaleDecision`，旧 decision 的普通命令拒绝同样不持久化；且 normal 路径在 `PrepareReceiptDigest` 之前早退，导致"已 Applied 的回执在 decision 退休后被二次拒绝"的潜在错误 | **已关闭（任务书明确授权：同窄持久拒绝机制内修复并测）** | 同上（normal 路径改为先 `GetReceiptDigest` 重放咨询，再持久化拒绝） |

未覆盖/未触碰：P4.1、并行 TableRuntime 响应/杠修复（61f0 在途）、TableService 持久宿主（93fb 在途/D01 映射）、公共协议、Domain、Client、TableRuntime（本修复零改动）、03/04 已关闭门禁（fixture/pin/断言零改动）。

### 1a. 复核轮缺陷清单与逐 ID 映射（独立 Codex 审查，全部本轮修复）

审查证据根（审查方独占运行）：`build/codex-lh02-review-bd0b875d9a5e4c90ba48d08a3c651397`（localhost 111 中 110 过 1 败 exit 1；scope-probe 4 选 2 过 2 败 exit 1；localcontrol 19/19、client 182/182、tableruntime 413/413；`git diff --check=0`、tracked clean）。

| 缺陷 ID | 级别 | 描述（实际 vs 预期） | 复现/证据 | 修复 | 验收断言（全部真实通过） |
|---|---|---|---|---|---|
| D-LH02-R1-01 | MAJOR | 普通命令路径持久化旧 decision 拒绝前**未做 scope 鉴权校验**：过期/撤销 scope 可借持久拒绝路径获得落盘记录（预期：与控制路径一致，未授权新请求只回 transport-only 拒绝、零写盘） | 审查方 `scope-probe.txt` 四格探针 2/4 失败（expired/revoked 于 normal 路径均落盘） | `LocalTableRuntime.RejectStaleDecisionAsync`（`3b81849`）改为与控制路径同一排序：先已提交回执守卫（精确重放/异字节冲突无需重鉴权、保持可对账），再 `ValidateControlScopeAsync` 身份/房间/角色/过期/撤销校验，未授权 → 仅 `Enqueue` transport-only 拒绝；新授权请求才 `RejectDurably` | `ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath`（control/normal × expired/revoked 四格：全部 `RequestScopeExpired`、`TryGet`/digest/control result 全 null、ControlVersion 0、状态行零移动、query NotFound）+ `ForeignScopeCommandStaysUnauthenticatedWithoutDurableRecord`（异 scope → `Unauthenticated` 零落盘，同 key 随后以真 scope 仍可正常持久化，证明 key 未被污染） |
| D-LH02-R1-02 | MAJOR | T5 `StaleNormalCommandRejectionReconcilesFromPersistentOutboxOnReconnect` 在 fresh 运行失败（Expected 1, Actual 2 at :1433）：基线门"seat0 有未决 decision 即静止"不成立——真实定位（时间线探针）：**cheat window 阶段 AI 逐座提交与 seat0 未决 decision 交错**，旧门在 sv=1 时放行而 sv 随后走到 2+，基线取早导致 after ≠ baseline | 审查方 fresh localhost 运行 T5 FAIL；本轮 `logs/r2-t2-diag-{1,2,3}.log`（`TEMP svAtGate=1 base=1 after=2 pending=…CheatWindow`）+ `logs/r2-probe.log` 时间线（t=40ms sv=1 且 D1 仍未决；sv 1→4 期间 pending="-"） | 基线门改为**真实静止边界**：持久状态行在驱动 tick（15ms）下连续 5 次、间隔 25ms 的 `(StateVersion, LastEventSeq)` 全等才视为 settle（`WaitForDurableStateStillnessAsync`）；需要活跃 decision 的测试（T2/T4/T5/AppliedReplay）在此基础上保留 decision-open 前置条件。精确等值断言全部保留，无 sleep/>=/删除断言/吞失败/重试到绿；03/04 已接受测试零改动 | T5 fresh 连续 4 次 PASS（`logs/r2-t5-gate-{1..4}.log`）；LocalControl 类 fresh 3 次 22/22；同型测试一次性审计：所有状态不变断言的基线统一走静止门（一次性完成，无逐测试打补丁）。首轮失败记录保留：`logs/r1-t5-repro-11..14.log`（背景稳定性循环 T5 连败 4 次）+ 审查方 evidence root |
| D-LH02-R1-03 | MAJOR | 冷进程验收只做了 store-only 读（`rejected-receipt-read`），未覆盖真实恢复 + 拒绝查询 + 同字节重放 + 冷 outbox 对账（预期：独立 OS 进程内完整闭环） | 审查意见 + 交付报告 §4 矩阵行口径过宽 | CrashWorker 以**独立真实模式** `rejected-receipt-cold-session` 替换（`18f1693`）：①独立 store 句柄读磁盘真相后释放；②`LocalTableHost.CreateChannelAsync` 真实恢复（runtime 在 open 握手后暴露，`RecoveryGeneration>0` 断言冷恢复而非新房）；③对**原 scope** 发真实 wire `ReceiptQuery`（Found/Rejected/DECISION_MISMATCH/原 digest）；④同 request 同业务 bytes 同 kind 在**冷连接新 scope** 下重放两次（Hello 恒发新 scope、命令绑定门只认连接 scope——原 scope 身份由③的显式查询承担，此为架构边界而非回避）：首次 fresh `Rejected/DECISION_MISMATCH/Replayed=false`，第二次 `Replayed=true` 同 digest，store 读证明每 key 单记录且原 `CreatedAtUtcMs` 不变；⑤真实冷 `TableSession(FileOutboxStore)` 对账：disposition Rejected、FromReceiptLookup、PendingCommands 0、`LoadUnresolvedAsync` 空；⑥最终 fresh store 句柄逐行核对磁盘真相（state/event/control 零变化）。03/04 模式零改动，无直接 store 调用伪造 wire 重放 | T2 `PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged` 端到端 fresh 通过 ×3（`logs/r2-t2-coldsession-{1..3}.log`），40+ 个 marker 逐行断言 + parent 终态磁盘核对。诚实口径：AutoPlay=false + 冻结时钟保证**自主推进静止**（状态行持恒），拒绝/重放隔离由显式重放断言证明，两者在报告中分列 |
| D-LH02-R1-04 | MINOR | 交付报告 §3 声称"控制命令处理路径不存在可取消的调用方 token"，与代码不符：`SendAsync`/`HandleControlCommandAsync` 全程传播 `CancellationToken`，仅 `TryCommitRejectedReceipt` 内部传 `CancellationToken.None`（预期：文档如实 + 取消边界有确定性真实测试） | 审查意见 + `LocalTableRuntime.cs` 实际代码（`SendAsync` 首语句 `ThrowIfCancellationRequested` :337；`_controlGate.WaitAsync(ct)` :1381；`TryGetAsync(ct)` :1275；`ValidateControlScopeAsync` 内 `LookupAsync(ct)` :1616；`TryCommitRejectedReceipt` 持 `CancellationToken.None`） | 不可取消段精确口径：**线性化点之前**的可取消段 = 入口 token 检查 → 控制门等待 → 回执守卫读 → 鉴权 scope 读；`TryCommitRejectedReceipt` 的 persist→publish 段**按设计**持 `None`（持久提交成功后才发布最终拒绝的不变量依赖它）。§3 措辞已如实改写 | `CancelledStaleCommandBeforeLinearizationWritesNothingAndKeyStaysReusable`：预线性化取消（入口检查即抛 OCE）→ 零持久记录/零 digest/零 control/状态零移动/query NotFound，且**幂等 key 未被消费**（同 bytes 随后正常持久化 `Rejected/DECISION_MISMATCH/Replayed=false`，原样重发 → `Replayed=true` 同 digest、CreatedAt 不变、单记录）。已提交后取消不在可测语义内（persist 段不可取消是设计），如实声明 |

## 2. 复现（独占 ignored 副本，fresh）

- 原只读探针源：`D:/majiang-wt/localhost-batch-gate-ds-20261029/build/lh-gate-95b4754/probe-control-rejection-95b4754/dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`（`DroppedStaleControlRejectionCannotBeReconciledAfterReconnect` + `DropRejectedControlReceiptChannel`）
- 独占副本：`build/lh02-glm-12a21ae95767/probe-repro/dotnet`（`LocalControlTests.cs` 经 `cmp` 与探针字节一致，73671 字节）
- fresh 构建：`logs/probe-repro-build.log`（exit 0，0 警告 0 错误）
- fresh 运行：`logs/probe-repro-run.log` + `probe-repro.xml`
  - 命令：`D:/Unity/dotnet/dotnet.exe .../probe-repro/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -noLogo -class Mahjong.LocalHost.Tests.LocalControlTests -method Mahjong.LocalHost.Tests.LocalControlTests.DroppedStaleControlRejectionCannotBeReconciledAfterReconnect -result-xml .../probe-repro.xml -parallelMode none`
  - native exit **1**；`Total: 1, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0`
  - 失败原文：`the rejected control request stayed unresolved: count=1 lastLookup=NotFound`

## 3. 修复设计（约束逐条对应）

1. **复用已有持久 receipt/control outcome 端口**：`LocalTableStore.TryCommitRejectedReceipt` 完全复用既有持久化原语——`DeepCloneForCandidate → 变异 → Persist → 仅 Committed 才 swap _save`；只写既有 `Receipts`/`ReceiptDigests` 两个字典。新增端口为 `internal`，不进公共协议。
2. **异字节冲突不能覆盖原成功/拒绝**：`TryCommitRejectedReceipt` 发现同 key 已有记录时直接返回既有记录（不写盘）；`ReplayOrConflictReceipt` 对既有记录做 `Matches(api, payloadSha256)` 判定——同字节同 kind → `Replayed` 回执；异字节或异 kind → `RequestIdConflict` 拒绝，原记录（含 CreatedAtUtcMs）零变化。
3. **鉴权 scope 边界**：控制路径顺序保持 `ValidateControlScopeAsync` 在持久化拒绝**之前**；过期/撤销 scope 的旧 decision 请求仍然只发 transport-only 的 `RequestScopeExpired` 拒绝，**不**落地任何持久记录（不得借写拒绝越权）。复核轮（D-LH02-R1-01）将同一排序下沉到 `RejectStaleDecisionAsync` 共享实现：**两条**命令路径都先过已提交回执守卫（精确重放/冲突无需重鉴权、保持可对账），新请求必须通过身份/房间/角色/过期/撤销校验才允许任何持久写入。
4. **保留原业务 digest/kind**：`ReceiptDigests[key] = CanonicalPayloadDigest(payload).ToHex()`（与 Client OutboxEntry.DigestSha256 同一 canonical 摘要）；`Api = DurableReceiptApi(kind)`（`CLAIM_CONTROL`/`RELEASE_CONTROL`）；`TablePayloadSha256` 存入 `TableDurableReceipt.PayloadSha256` 用于回放/冲突判定。重放回执回传同一 canonical digest（断言与 client 侧一致）。
5. **零状态/事件/控制代次变化**：candidate 只含 `Receipts[key]` + `ReceiptDigests[key]` 两处新增；冷进程 worker 逐行断言 `state_version`/`last_event_seq` 与重启前磁盘基线一致、`control_version=0`、`control_record_present=0`；在线断言 `ControlVersionForTest=0`、`ControlSnapshot` 0/0。
6. **不为新拒绝制造成功 LOCAL_CONTROL 记录**：`ControlResults`/`_pendingControlResults` 零触碰；冷 worker 报 `control_record_present=0`。
7. **持久提交成功后才发布最终拒绝**：`RejectDurably` 仅在 `TryCommitRejectedReceipt` 返回 null（本请求首次成功 Committed）后 enqueue 最终 `Rejected(DecisionMismatch)` 回执；commit 抛 `LocalTableException`（Aborted/不可用）→ enqueue `InternalError` 错误信封，client 条目保持 `NeedsReconciliation`，绝不伪称已落盘。
8. **未知结果保持显式对账**：store 的 `_faulted` 姿态（"The disk evidence does not contain the queried receipt; the outcome remains unknown."）+ client 对 NotFound 保持 pending 两条既有边界在测试中真实覆盖（T8 重开查询 NotFound、条目 unresolved）。
9. **普通命令同类缺陷**：normal 路径同样先查 `GetReceiptDigest`（已 Applied → `Replayed` 回执，不再二次拒绝），再走同一 `RejectDurably` 持久化拒绝。

取消边界说明（复核轮 D-LH02-R1-04 如实改写）：控制命令处理路径**全程传播调用方 `CancellationToken`**。可取消段止于线性化点之前：`SendAsync` 入口的 `ThrowIfCancellationRequested`（LocalTableRuntime.cs:337）→ 控制门 `_controlGate.WaitAsync(ct)`（:1381）→ 回执守卫 `TryGetAsync(ct)`（:1275）→ 鉴权 scope 读 `LookupAsync(ct)`（:1616）。此后 `TryCommitRejectedReceipt` 在锁内同步提交并**按设计**持 `CancellationToken.None`——"持久提交成功后才发布最终拒绝"的不变量依赖 persist→publish 段不可取消。复核轮新增确定性真实测试 `CancelledStaleCommandBeforeLinearizationWritesNothingAndKeyStaysReusable`：预线性化取消零落盘且幂等 key 保持可复用（同 bytes 随后正常持久化、原样重发精确重放单记录）；已提交后的取消不在可测语义内，如实声明。`Persist` 对命中前钩子的取消按干净 Aborted 分类，可观测行为（无持久记录 + InternalError + 条目 unresolved）与写失败一致，仍由 T8 同路径覆盖。

## 4. 验收矩阵 → 测试映射（一次完成）

| 矩阵项 | 测试（均为 `Mahjong.LocalHost.Tests.LocalControlTests`） | 结果 |
|---|---|---|
| 真实 FileOutboxStore+TableSession、确定性退休 decision、带旧 decision 提交 Release、丢第一 DECISION_MISMATCH 回包；重连查询 Found、保留原 reason/status/digest/kind；PendingCommands 与 LoadUnresolvedAsync 均空；ControlVersion/Epoch/state/event 不变 | `DroppedStaleControlRejectionReconcilesFromPersistentOutboxOnReconnect` | PASS |
| 真正 OS 冷进程恢复：真实恢复+原 scope 拒绝查询+同 request 同业务 bytes/kind 重放 ×2 单记录+真实冷客户端 outbox 对账+终态磁盘逐行核对、ControlVersion/ControlEpoch/state/event 不变 | `PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged`（CrashWorker 真实模式 `rejected-receipt-cold-session`，首轮的 store-only `rejected-receipt-read` 已被其替换；03/04 既有模式零改动） | PASS（复核轮后） |
| 原**成功** exact replay 在 decision 退休后仍成功（不再被二次拒绝） | `AppliedCommandReplayAfterDecisionRetirementReplaysInsteadOfStaleRejection` | PASS |
| 同 request 异字节/异 kind 冲突不覆盖、单记录、CreatedAt 不变 | `PersistedStaleControlRejectionExactReplayAndConflictKeepSingleRecord` | PASS |
| 普通旧 decision 命令同类缺陷修复 + outbox 对账 | `StaleNormalCommandRejectionReconcilesFromPersistentOutboxOnReconnect` | PASS |
| 过期 scope：transport-only、零持久记录、raw query NotFound | `ExpiredScopeStaleDecisionStaysTransportOnlyWithoutDurableRecord` | PASS |
| 并发 Claim/Release 拒绝：并发不同 id 全持久化且互不覆盖；并发相同 id 恰好单记录+Replayed | `ConcurrentStaleControlRejectionsPersistExactlyOncePerRequest` | PASS |
| 写失败：注入 fault → 不伪称落盘、client 条目 unresolved、重开查询 NotFound、控制零移动 | `RejectedReceiptCommitFailureLeavesOutcomeUnresolved` | PASS |
| 跨 account/room/scope 隔离：本 scope Found、外 scope/外 requestId NotFound | `PersistedRejectionIsScopedToItsAccountAndScope` | PASS |
| 并发 Claim/Release 控制权（既有门禁不回退） | `ConcurrentClaimAndReleaseAdmitExactlyOneControlChange`（既有） | PASS |
| 既有 control outbox 对账（既有） | `DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect` 等 | PASS |
| （复核轮 R1-01）过期/撤销 scope 在 control+normal 两路径的旧 decision 请求：transport-only、零持久记录、状态零移动、query NotFound（四格矩阵） | `ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath` | PASS（复核轮新增） |
| （复核轮 R1-01）异 scope 命令：`Unauthenticated` 零落盘、幂等 key 不被污染（同 key 真 scope 随后仍可正常持久化） | `ForeignScopeCommandStaysUnauthenticatedWithoutDurableRecord` | PASS（复核轮新增） |
| （复核轮 R1-04）预线性化取消：零落盘、幂等 key 可复用；随后同 bytes 正常持久化 + 精确重放单记录 | `CancelledStaleCommandBeforeLinearizationWritesNothingAndKeyStaysReusable` | PASS（复核轮新增） |

复核轮测试过程修正（如实记录，非弱化）：首轮的 settle 门（"seat0 出现未决 decision 即认为 AI 后续提交已 settle"）经时间线探针证明不成立——cheat window 阶段逐座提交与 seat0 未决 decision 交错（sv 在门放行后仍 1→4），基线取早导致 T5 精确等值断言失败。终版改为真实静止边界：持久状态行 `(StateVersion, LastEventSeq)` 连续 5 次 25ms 间隔全等；scope 过期（24h）级联会把 cheat window 停在"无未决 decision、未终局"的静止态（探针实测 sv/ev 恒 0 持续 12s+），故无需活跃 decision 的测试只用静止门。T2 冷进程验证初版在 open 握手前读 `RuntimeForMatch`（懒暴露，恒 null）导致 worker exit 4；改为 open 握手后读取并断言 `RecoveryGeneration>0`。两处均无断言弱化，精确等值断言全部保留。

测试过程修正（如实记录，非弱化）：T8 初版在 `BeforeMove` 上等待注入（首发 save 文件已存在，实际走 `Replace` 路径）；改挂双钩后曾用 `Assert.Throws` 断言 store 已故障化，但注入的"安装前"失败被 `Persist` 分类为干净 Aborted（store 未故障化），依赖后续 autopilot 写竞争故障化属 flaky。终版改为确定性断言：命中即摘钩、等待 client 观测到 InternalError、对 `TryGetAsync` 接受两种真实形态（null 或显式 "outcome remains unknown" 异常——两者都证明无持久记录）。T2 初版从 live 运行时内存读 state 基线（内存可领先磁盘）；终版按 gate-04 既有模式在 dispose 后用独立 `LocalTableStore.OpenAsync` 读磁盘基线。两处均无断言弱化。

## 5. 最终证据（18f1693 fresh 快照，fresh 构建，fresh 产物）

全部命令形如 `D:/Unity/dotnet/dotnet.exe <fresh dll> -noLogo [-class|-method X] -result-xml <xml> -parallelMode none`；dotnet 8.0.426（D:/Unity/dotnet/dotnet.exe）。全部产物自 `final-18f1693/`（`git archive 18f1693` 展开）fresh 构建运行；`Mahjong.sln` 不含测试工程，测试按 round-1 同模式逐工程构建。

| 套件 | 命令目标 | native exit | Total | Failed | Skipped | Errors | Not Run | 日志 / XML |
|---|---|---|---|---|---|---|---|---|
| Mahjong.sln fresh 全量构建 | `build Mahjong.sln -c Release --no-incremental` | 0 | — | 0 警告 0 错误 | | | | `logs/final2-r1-build-solution.log` |
| LocalHost.CrashWorker fresh 构建 | per-project `--no-incremental` | 0 | — | 0 警告 0 错误 | | | | `logs/final2-r1-build-Mahjong.LocalHost.CrashWorker.log` |
| LocalHost.Tests fresh 构建 | per-project `--no-incremental` | 0 | — | 0 警告 0 错误 | | | | `logs/final2-r1-build-Mahjong.LocalHost.Tests.log` |
| Client.Tests fresh 构建 | per-project `--no-incremental` | 0 | — | 0 警告 0 错误 | | | | `logs/final2-r1-build-Mahjong.Client.Tests.log` |
| TableRuntime.Tests fresh 构建 | per-project `--no-incremental` | 0 | — | 0 警告 0 错误 | | | | `logs/final2-r1-build-Mahjong.TableRuntime.Tests.log` |
| LocalHost 全量 | fresh `Mahjong.LocalHost.Tests.dll` 全跑 | 0 | **114** | 0 | 0 | 0 | 0 | `logs/final2-r1-localhost-full.log` / `final2-r1-localhost-full.xml` |
| LocalControl 定向（含 12 条新增/修订回归，非零） | `-class Mahjong.LocalHost.Tests.LocalControlTests` | 0 | **22** | 0 | 0 | 0 | 0 | `logs/final2-r1-localcontrol.log` / `final2-r1-localcontrol.xml` |
| Client 全量 | fresh `Mahjong.Client.Tests.dll` | 0 | **182** | 0 | 0 | 0 | 0 | `logs/final2-r1-client.log` / `final2-r1-client.xml` |
| TableRuntime 全量 | fresh `Mahjong.TableRuntime.Tests.dll` | 0 | **413** | 0 | 0 | 0 | 0 | `logs/final2-r1-tableruntime.log` / `final2-r1-tableruntime.xml` |

- 上游黄金/平台失败：无（复核轮无任何失败需要分列；未刷新任何黄金 pin）。
- 稳定性筛：LocalControl 类自快照连续 3 次 22/22（`logs/final2-r1-localcontrol.log` + `final2-r1-localcontrol-stability-{2,3}.log`）；T2 冷进程端到端快照外开发期 ×3 + 快照内全量含 1 次；T5 fresh ×4（`logs/r2-t5-gate-{1..4}.log`）。
- 首轮失败记录保留（D-LH02-R1-02 归因）：审查方 evidence root `build/codex-lh02-review-bd0b875d9a5e4c90ba48d08a3c651397`（fresh localhost 110/111，T5 FAIL `Expected 1, Actual 2`）；本轮复现 `logs/r2-t2-first.log`（同型 FAIL）、`logs/r2-t2-diag-{1,2,3}.log`（`TEMP svAtGate=1 base=1 after=2 pending=…CheatWindow`）、`logs/r2-probe.log`（cheat-window 交错时间线：sv=1 时 D1 仍未决、sv 1→4 期间 pending="-"）；round-1 稳定性循环连败 `logs/r1-t5-repro-11..14.log`。真实原因为基线门不等价于 settle 边界（详见 §4 过程修正），非 flaky 归因。
- SHA256 manifest：`build/lh02-glm-12a21ae95767/manifest-18f1693.sha256`（覆盖上表全部日志/XML + 快照 zip）。样本：
  - `final2-r1-localhost-full.xml` = `f271208fa3d462dbfbd0182e88c763ddcf919dcaccff4e7d2f3b910404530503`
  - `final2-r1-localcontrol.xml` = `9098592f6985860ce1647dcabb91a2ba5c68bd9faef241e9199ec10275d3b7be`
  - `final2-r1-client.xml` = `3ffd1914a2a0cb32f21a0b9992de8baf5ba59c5b914cabcad6078dbdc457cef3`
  - `final-18f1693.zip` = `2d512a50d19031bbe79602076d2950b20f688bdd35c27da74a044633f6d994ac`
- 首轮（f75b6be）证据保留于同证据根：`logs/final2-*.log`、`final2-*.xml`、`manifest-f75b6be.sha256`、`final-f75b6be.zip`（round-1 报告口径下的完整链）。

## 6. 净 diff 与树状态

- 净 diff（eeff5f9..18f1693，5 文件，+2505/−17，含首轮报告文件 +104）：
  - `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs`（+302/−17，首轮持久化修复 + 复核轮 R1-01 双路径鉴权排序）
  - `dotnet/src/Mahjong.LocalHost/LocalTableStore.cs`（+49，首轮 `TryCommitRejectedReceipt` 持久化原语，复核轮零改动）
  - `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`（+1474，首轮矩阵 + 复核轮四格/异 scope/取消回归 + 真实静止门）
  - `dotnet/tests/Mahjong.LocalHost.CrashWorker/Program.cs`（+593，首轮 read 模式 + 复核轮 `rejected-receipt-cold-session` 真实冷进程闭环）
  - `doc/v5-重构/V5-LOCALHOST-REJECTED-RECEIPT-DELIVERY-20261029.md`（本报告）
- 复核轮净变化（4833a19..18f1693，3 文件）：`LocalTableRuntime.cs`（+62/−27）、`Program.cs`（+516/−23）、`LocalControlTests.cs`（+494/−70）。
- `git status --porcelain=v1`：空（tracked clean，证据全部在 gitignored `build/` 下）
- `git diff --check eeff5f9688d95afb5d8187137baab92103f1c8ce..HEAD`：exit 0，无输出
- 仅本地 commits，未 push、未建 PR/MR、未 merge、未部署、未触碰 complete.flag/scheduler；PR20（https://github.com/liuchangready/majiang-game/pull/20）仍由 caller 统一验收。

## 7. 边界确认

- 公共协议、Domain、Client、TableRuntime 源码零改动（diff 文件清单可证）。
- CrashWorker 首轮新增独立 `rejected-receipt-read` 模式，复核轮（D-LH02-R1-03）将其**替换**为真实冷进程闭环模式 `rejected-receipt-cold-session`；03/04 既有模式与其断言零改动，03/04 门禁未恢复/未修改。
- 未重做 P4.1，未触碰 TableService（93fb/D01）与 TableRuntime 在途修复（61f0）路径；本修复不需要其接口变动，无最小阻塞上报项。
- 预期文件范围：仅上述 4 个源码/测试既有路径 + 本报告，无新增源码路径。
- 审查方 scope-probe 四格探针已折入正式回归（`ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath`，覆盖 control/normal × expired/revoked 全四格并扩展至普通路径新请求鉴权），审查冻结 HEAD `4833a19` 之后本轮仅以上述 3 文件 + 本报告变化。
