# V5 LocalHost 拒绝回执持久化交付报告 (D-LH-GATE-02) — 2026-10-29

- 工作树: `D:\majiang-wt\lh-rejected-receipts-glm-20261029`（分支 `wt/v5-lh-rejected-receipts-glm-20261029`）
- 基线（冻结）: `eeff5f9...` → 完整 SHA `eeff5f9688d95afb5d8187137baab92103f1c8ce`（= 已审查发布的 PR20 头）
- 证据构建/运行 SHA: `f75b6be241d1c69f4b70c8b60a98fbf312b2231a`（tree `7830d02eff7582812b29711f800f41317610a740`；第 5 节全部 fresh 构建与运行产自此快照）
- 最终 HEAD: 本报告的提交（`docs(v5): deliver D-LH-GATE-02 rejected-receipt persistence report`，在 f75b6be 之上仅追加本报告文件，源码与测试零变化；提交 SHA 见 `git log`，因报告内容含 SHA 自引用而以提交说明定位）
  - `06c53de` fix(v5): persist stale-decision rejections before publishing them
  - `f75b6be` test(v5): acceptance matrix for durable stale-decision rejections
  - HEAD 本身 docs(v5): deliver D-LH-GATE-02 rejected-receipt persistence report
- 证据根（本树 gitignored）: `build/lh02-glm-12a21ae95767/`，最终快照 `final-f75b6be/`（`git archive f75b6be` 展开，无 .git）
- 范围声明：本报告只关闭 **D-LH-GATE-02 (MAJOR)**。不宣称 LocalHost 批次整批通过、不宣称 Unity 相关目标通过、不宣称 G2–G5 任一通过（V4 仍是唯一生产架构；G1 APPROVED 维持不变）。

---

## 1. 缺陷清单与逐 ID 映射

| 缺陷 ID | 描述 | 状态 | 修复位置 |
|---|---|---|---|
| D-LH-GATE-02 (MAJOR) | LocalHost 旧 decision 控制拒绝只 enqueue 不持久化：`HandleControlCommandAsync` 先 `GetControlResult` 重放/冲突咨询，再 `TryRejectStaleDecision` 早退，早于任何 control outcome 持久化；回包丢失后真实 Client outbox 对账时 receipt query 永远 NotFound | **已关闭** | `LocalTableRuntime.RejectStaleDecision/RejectDurably` + `LocalTableStore.TryCommitRejectedReceipt` |
| （同类缺陷，同窄机制一并修复） | 普通命令路径复用 `TryRejectStaleDecision`，旧 decision 的普通命令拒绝同样不持久化；且 normal 路径在 `PrepareReceiptDigest` 之前早退，导致"已 Applied 的回执在 decision 退休后被二次拒绝"的潜在错误 | **已关闭（任务书明确授权：同窄持久拒绝机制内修复并测）** | 同上（normal 路径改为先 `GetReceiptDigest` 重放咨询，再持久化拒绝） |

未覆盖/未触碰：P4.1、并行 TableRuntime 响应/杠修复（61f0 在途）、TableService 持久宿主（93fb 在途/D01 映射）、公共协议、Domain、Client、TableRuntime（本修复零改动）、03/04 已关闭门禁（fixture/pin/断言零改动）。

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
3. **鉴权 scope 边界**：控制路径顺序保持 `ValidateControlScopeAsync` 在持久化拒绝**之前**；过期/撤销 scope 的旧 decision 请求仍然只发 transport-only 的 `RequestScopeExpired` 拒绝，**不**落地任何持久记录（不得借写拒绝越权）。
4. **保留原业务 digest/kind**：`ReceiptDigests[key] = CanonicalPayloadDigest(payload).ToHex()`（与 Client OutboxEntry.DigestSha256 同一 canonical 摘要）；`Api = DurableReceiptApi(kind)`（`CLAIM_CONTROL`/`RELEASE_CONTROL`）；`TablePayloadSha256` 存入 `TableDurableReceipt.PayloadSha256` 用于回放/冲突判定。重放回执回传同一 canonical digest（断言与 client 侧一致）。
5. **零状态/事件/控制代次变化**：candidate 只含 `Receipts[key]` + `ReceiptDigests[key]` 两处新增；冷进程 worker 逐行断言 `state_version`/`last_event_seq` 与重启前磁盘基线一致、`control_version=0`、`control_record_present=0`；在线断言 `ControlVersionForTest=0`、`ControlSnapshot` 0/0。
6. **不为新拒绝制造成功 LOCAL_CONTROL 记录**：`ControlResults`/`_pendingControlResults` 零触碰；冷 worker 报 `control_record_present=0`。
7. **持久提交成功后才发布最终拒绝**：`RejectDurably` 仅在 `TryCommitRejectedReceipt` 返回 null（本请求首次成功 Committed）后 enqueue 最终 `Rejected(DecisionMismatch)` 回执；commit 抛 `LocalTableException`（Aborted/不可用）→ enqueue `InternalError` 错误信封，client 条目保持 `NeedsReconciliation`，绝不伪称已落盘。
8. **未知结果保持显式对账**：store 的 `_faulted` 姿态（"The disk evidence does not contain the queried receipt; the outcome remains unknown."）+ client 对 NotFound 保持 pending 两条既有边界在测试中真实覆盖（T8 重开查询 NotFound、条目 unresolved）。
9. **普通命令同类缺陷**：normal 路径同样先查 `GetReceiptDigest`（已 Applied → `Replayed` 回执，不再二次拒绝），再走同一 `RejectDurably` 持久化拒绝。

取消边界说明：`TryCommitRejectedReceipt` 在锁内同步提交并传 `CancellationToken.None`——控制命令处理路径不存在可取消的调用方 token；`Persist` 对线性化点之前的取消按干净 Aborted 分类，其可观测行为（无持久记录 + InternalError + 条目 unresolved）与写失败完全一致，由 T8 同路径覆盖。未声称存在独立的取消测试。

## 4. 验收矩阵 → 测试映射（一次完成）

| 矩阵项 | 测试（均为 `Mahjong.LocalHost.Tests.LocalControlTests`） | 结果 |
|---|---|---|
| 真实 FileOutboxStore+TableSession、确定性退休 decision、带旧 decision 提交 Release、丢第一 DECISION_MISMATCH 回包；重连查询 Found、保留原 reason/status/digest/kind；PendingCommands 与 LoadUnresolvedAsync 均空；ControlVersion/Epoch/state/event 不变 | `DroppedStaleControlRejectionReconcilesFromPersistentOutboxOnReconnect` | PASS |
| 真正 OS 冷进程恢复再查相同拒绝、同字节重放无第二记录、ControlVersion/ControlEpoch/state/event 不变 | `PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged`（CrashWorker 新增 store-only 模式 `rejected-receipt-read`，parent 以磁盘基线对行断言；03/04 既有模式零改动） | PASS |
| 原**成功** exact replay 在 decision 退休后仍成功（不再被二次拒绝） | `AppliedCommandReplayAfterDecisionRetirementReplaysInsteadOfStaleRejection` | PASS |
| 同 request 异字节/异 kind 冲突不覆盖、单记录、CreatedAt 不变 | `PersistedStaleControlRejectionExactReplayAndConflictKeepSingleRecord` | PASS |
| 普通旧 decision 命令同类缺陷修复 + outbox 对账 | `StaleNormalCommandRejectionReconcilesFromPersistentOutboxOnReconnect` | PASS |
| 过期 scope：transport-only、零持久记录、raw query NotFound | `ExpiredScopeStaleDecisionStaysTransportOnlyWithoutDurableRecord` | PASS |
| 并发 Claim/Release 拒绝：并发不同 id 全持久化且互不覆盖；并发相同 id 恰好单记录+Replayed | `ConcurrentStaleControlRejectionsPersistExactlyOncePerRequest` | PASS |
| 写失败：注入 fault → 不伪称落盘、client 条目 unresolved、重开查询 NotFound、控制零移动 | `RejectedReceiptCommitFailureLeavesOutcomeUnresolved` | PASS |
| 跨 account/room/scope 隔离：本 scope Found、外 scope/外 requestId NotFound | `PersistedRejectionIsScopedToItsAccountAndScope` | PASS |
| 并发 Claim/Release 控制权（既有门禁不回退） | `ConcurrentClaimAndReleaseAdmitExactlyOneControlChange`（既有） | PASS |
| 既有 control outbox 对账（既有） | `DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect` 等 | PASS |

测试过程修正（如实记录，非弱化）：T8 初版在 `BeforeMove` 上等待注入（首发 save 文件已存在，实际走 `Replace` 路径）；改挂双钩后曾用 `Assert.Throws` 断言 store 已故障化，但注入的"安装前"失败被 `Persist` 分类为干净 Aborted（store 未故障化），依赖后续 autopilot 写竞争故障化属 flaky。终版改为确定性断言：命中即摘钩、等待 client 观测到 InternalError、对 `TryGetAsync` 接受两种真实形态（null 或显式 "outcome remains unknown" 异常——两者都证明无持久记录）。T2 初版从 live 运行时内存读 state 基线（内存可领先磁盘）；终版按 gate-04 既有模式在 dispose 后用独立 `LocalTableStore.OpenAsync` 读磁盘基线。两处均无断言弱化。

## 5. 最终证据（f75b6be fresh 快照，fresh 构建，fresh 产物）

全部命令形如 `D:/Unity/dotnet/dotnet.exe <fresh dll> -noLogo [-class|-method X] -result-xml <xml> -parallelMode none`；dotnet 8.0.426（D:/Unity/dotnet/dotnet.exe）。

| 套件 | 命令目标 | native exit | Total | Failed | Skipped | Errors | Not Run | 日志 / XML |
|---|---|---|---|---|---|---|---|---|
| Mahjong.sln fresh 全量构建 | `build Mahjong.sln -c Release --no-incremental` | 0 | — | 0 警告 0 错误 | | | | `logs/final2-build-solution.log` |
| LocalHost.CrashWorker fresh 构建 | per-project `--no-incremental` | 0 | — | 0 警告 0 错误 | | | | `logs/final2-build-Mahjong.LocalHost.CrashWorker.log` |
| LocalHost.Tests fresh 构建 | per-project `--no-incremental` | 0 | — | 0 警告 0 错误 | | | | `logs/final2-build-Mahjong.LocalHost.Tests.log` |
| TableRuntime.Tests fresh 构建 | per-project `--no-incremental` | 0 | — | 0 警告 0 错误 | | | | `logs/final2-build-Mahjong.TableRuntime.Tests.log` |
| LocalHost 全量 | fresh `Mahjong.LocalHost.Tests.dll` 全跑 | 0 | **111** | 0 | 0 | 0 | 0 | `logs/final2-localhost-full.log` / `final2-localhost-full.xml` |
| LocalControl 定向（含 9 条新增回归，非零） | `-class Mahjong.LocalHost.Tests.LocalControlTests` | 0 | **19** | 0 | 0 | 0 | 0 | `logs/final2-localcontrol.log` / `final2-localcontrol.xml` |
| Client 全量 | fresh `Mahjong.Client.Tests.dll` | 0 | **182** | 0 | 0 | 0 | 0 | `logs/final2-client.log` / `final2-client.xml` |
| TableRuntime 全量 | fresh `Mahjong.TableRuntime.Tests.dll` | 0 | **413** | 0 | 0 | 0 | 0 | `logs/final2-tableruntime.log` / `final2-tableruntime.xml` |

- 上游黄金/平台失败：无（本轮无任何失败需要分列；未刷新任何黄金 pin）。
- 稳定性筛：T8 单测连续 5 次 exit 0；LocalControl 类连续 3 次 19/19（`logs/t8-stability-*.log`、`logs/localcontrol-stability-*.log`）。
- SHA256 manifest：`build/lh02-glm-12a21ae95767/manifest-f75b6be.sha256`（覆盖上表全部日志/XML + 快照 zip + 复现证据）。样本：
  - `final2-localhost-full.xml` = `679a7574c7fdf34406ce4852e531a5b345499b7c8190b2f25d6d896ccfa66bb3`
  - `final2-localcontrol.xml` = `84fdbda28a8ad7384a619f0936020b31e152e9557238211a62def9dc7b4f1be4`
  - `final-f75b6be.zip` = `263392857571bdbfac99418a1b4cded0c8d7ca7a619cb0cf33d392ac741f69c8`

## 6. 净 diff 与树状态

- 净 diff（eeff5f9..f75b6be，4 文件，+1344/−12）：
  - `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs`（+242/−12，修复）
  - `dotnet/src/Mahjong.LocalHost/LocalTableStore.cs`（+49，修复）
  - `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`（+988，测试）
  - `dotnet/tests/Mahjong.LocalHost.CrashWorker/Program.cs`（+77，测试）
- `git status --porcelain=v1`：空（tracked clean，证据全部在 gitignored `build/` 下）
- `git diff --check eeff5f9688d95afb5d8187137baab92103f1c8ce..HEAD`：exit 0，无输出
- 仅本地 commits，未 push、未建 PR/MR、未 merge、未部署、未触碰 complete.flag/scheduler；PR20（https://github.com/liuchangready/majiang-game/pull/20）仍由 caller 统一验收。

## 7. 边界确认

- 公共协议、Domain、Client、TableRuntime 源码零改动（diff 文件清单可证）。
- CrashWorker 仅新增独立 `rejected-receipt-read` 模式；03/04 既有模式与其断言零改动，03/04 门禁未恢复/未修改。
- 未重做 P4.1，未触碰 TableService（93fb/D01）与 TableRuntime 在途修复（61f0）路径；本修复不需要其接口变动，无最小阻塞上报项。
- 预期文件范围：仅上述 4 个既有路径，无新增源码路径。
