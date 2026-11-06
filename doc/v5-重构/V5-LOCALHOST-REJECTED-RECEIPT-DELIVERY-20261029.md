# V5 LocalHost 拒绝回执持久化交付报告 (D-LH-GATE-02) — 2026-10-29

- 工作树: `D:\majiang-wt\lh-rejected-receipts-glm-20261029`（分支 `wt/v5-lh-rejected-receipts-glm-20261029`）
- 基线（冻结）: `eeff5f9...` → 完整 SHA `eeff5f9688d95afb5d8187137baab92103f1c8ce`（= 已审查发布的 PR20 头）
- 首轮交付: `06c53de`（fix）+ `f75b6be`（test）+ `4833a19`（docs），审查冻结 HEAD = `4833a19d009e65f083b26895ac52179701d0cf9d`
- 复核轮（本报告第 1a 节 D-LH02-R1-01..04 全部修复）证据构建/运行 SHA: `18f1693ea14e5c8c88131335785cacfab8fe30ad`（tree `1d839cb110452bb72862aea263c1995f18693a8a`；第 5 节全部 fresh 构建与运行产自此快照）
  - `3b81849` fix(v5): authorize new stale-decision rejections on both command paths
  - `18f1693` test(v5): cold-session replay, unauthorized-scope and cancellation regressions
  - HEAD 本身 docs(v5) 提交在 18f1693 之上仅追加本报告文件，源码与测试零变化（SHA 因报告内容含自引用以提交说明定位）
- 证据根（本树 gitignored）: `build/lh02-glm-12a21ae95767/`，最终快照 `final-18f1693/`（`git archive 18f1693` 展开，无 .git）
- 复核轮 2（本报告第 1b 节 D-LH02-R2-02/03/04 全部返修）审查冻结 HEAD = `26a2db08e60b5b5402166236e2a4c95d4b4efaf9`（= `26a2db0` docs 提交，代码与 `18f1693` 零差异）；审查证据根（审查方独占运行）: `build/codex-lh02-r2-4e6c50d9896745329f099fd899959e4b`，`review-manifest.csv` SHA256 = `096FDA7C4FE655D88DA408073FCF6064EF2DAA6EDF8F01A533FC178315B81A69`（本轮已复核一致）
- 范围声明：本报告只关闭 **D-LH-GATE-02 (MAJOR)** 及其两轮复核缺陷（D-LH02-R1-01..04 与 D-LH02-R2-02/03/04，外加复核轮 2 依新证据追加的 LocalHost 产品缺陷：AutoPlay 死配置，见 §1b 末行）。不宣称 LocalHost 批次整批通过、不宣称 Unity 相关目标通过、不宣称 G2–G5 任一通过（V4 仍是唯一生产架构；G1 APPROVED 维持不变）。

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

上表状态更新（复核轮 2 后）：R1-01 **接受，语义零改动**（本轮未触碰）；R1-02 的 5×25ms 静止门实现被复核轮 2 判定非确定并已**整体替换**（见 §1b）；R1-03/R1-04 交付被复核轮 2 判定证据缺口，返修见 §1b。§1a 各行保留为该轮历史口径。

### 1b. 复核轮 2 缺陷清单与逐 ID 映射（独立审查返修，全部本轮修复）

审查证据根（审查方独占运行）：`build/codex-lh02-r2-4e6c50d9896745329f099fd899959e4b`（审查冻结 HEAD `26a2db0`，`review-manifest.csv` SHA256 见文件头）。本轮返修在同一会话/同一线程完成，未另起实例、未转派；返修后净变化仅 3 个既有文件 + 本报告。

| 缺陷 ID | 级别 | 描述（实际 vs 预期） | 复现/证据 | 修复 | 验收断言（全部真实通过） |
|---|---|---|---|---|---|
| D-LH02-R2-02（= R1-02 返修） | MAJOR | R1-02 交付的 settle 门（持久状态行 5 次 ×25ms 全等）仍是**时间启发式**：审查探针 `ReviewerHeldAiMustNotBeDeclaredQuiescent` 证明被 gate 暂停的 AI 后续提交可在门放行后落盘，静止声明非确定（预期：复用既有 AI gate/权威队列同步的确定性方案，确定性覆盖自主提交前与后，保留精确等值） | 审查方探针（evidence root） | 删除时间启发式 `WaitForDurableStateStillnessAsync`（含其全部调用点），改为**确定性暂停**：`AiSubmitHold` 经既有驱动门 `LocalTableDriver.BeforeAiSubmitForTest` 把 AI 停在提交前里程碑（hold 内零自主提交源：驱动被 park、AutoPlay 驱动循环为唯一 AI/自动出牌来源），待挂起变更排空后经权威队列（mailbox 串行）取 `ActorForTest.GetSnapshotAsync` **冻结快照**作状态基线，持久行仅在被拒回执持久化（full save）时冲刷到该快照——一切持久行等值断言改对 frozen/preAttemptRow 表达，不在任何时刻声明"已静止"。一次性审计：T2/T4/T5/probe-revoke/probe-expiry/foreign/cancel 全部原静止门调用点同型重写，grep 零残留引用；无 sleep/更长等待/重复跑绿 | 正式 held-AI 负例 `HeldAiFollowUpIsNotDeclaredSettledAndItsLateCommitLandsAfterRelease`：hold 下探针被拒回执持久化后持久行 == 冻结快照精确等值（heldVersion/heldEventSeq），释放后 AI 迟到提交真实落盘且状态前移——证明"hold 下零提交、释放后恢复"两半都被确定性覆盖；fresh ×3（final `repeat-HeldAi...-{1,2,3}`，exit 0）；LocalControl 类 23/23、LocalHost 全量 115/115 |
| D-LH02-R2-03（= R1-03 返修） | MAJOR | R1-03 交付的冷会话把幂等键换了：冷进程对同 request 重放走的是 Hello 恒发的新 scope + 新 key，`Replayed=true` 实为同键双发自证，未证明"原 account/scope/request/api/bytes 首次即 Replayed=true 保原 evaluated/reason/digest/CreatedAt 无额外记录"（预期：原键重放或明确报告契约限制与最小可行方案，不得宣称全矩阵 PASS） | 审查方 T2 复审（evidence root） | 诚实口径收窄 + 证据加厚：①原键冷 COMMAND 重放经代码审计判定**架构不可达**——`HandleHelloAsync` 每连接恒发 fresh scope（LocalTableRuntime.cs:428/435-437/444/445），命令绑定门只认连接 scope（:730-735），冷连接无法以原 scope 发命令；报告如实声明该契约限制（不降鉴权、不反射伪造、不扩公共协议；精确限制与最小可行方案见 §1c），原键重放语义由既有 warm 测试 `PersistedStaleControlRejectionExactReplayAndConflictKeepSingleRecord` 覆盖。②冷进程仍交付：对**原 scope** 的真实 wire `ReceiptQuery`（新键写入前 query1 与写入后 query2，均 Found/Rejected/DECISION_MISMATCH/原 digest）；冷客户端 outbox 对账（原 scope，disposition Rejected/FromReceiptLookup/Pending 0/unresolved 空）；新键隔离负例（同 request 新 scope 首发 fresh Rejected/Replayed=false、再发 Replayed=true 同 digest、每 key 单记录）；worker marker 如实标注 `replay_key_scope=cold-issued`、`original_key_cold_command_replay=unreachable`。③冷进程静止改为**构造性成立**：worker 以冻结时钟 + `AutoPlay=false` 运行，重启后无任何自主提交源（本项同时暴露并修复 AutoPlay 死配置产品缺陷，见本表末行） | T2 端到端 final fresh ×3（`repeat-Persisted...-{1,2,3}`，exit 0）：冷恢复 `RecoveryGeneration>0`、记录数/原 CreatedAt/状态/事件/控制代次精确不变（逐行 trace_state 七步 + moved_event/moved_command 空集断言）、原 scope 前后查询不变、冷 outbox 对账闭环；warm 重放语义 `r3-replay2/3` fresh ×2 exit 0 |
| D-LH02-R2-04（= R1-04 返修） | MINOR | R1-04 交付的取消测试只覆盖入口预取消一种 token 边界；未覆盖"已进入处理未线性化"与"不可取消 persist→publish 段内真实晚取消"（预期：既有控制 gate 或文件系统 hook 确定性触发真实取消，逐 token 说明边界、native 结果与磁盘/outbox 断言） | 审查方取消用例复审（evidence root） | `CancelledStaleCommandBeforeLinearizationWritesNothingAndKeyStaysReusable` 重写为**三边界矩阵**，全部在 AiSubmitHold 冻结基线下确定性触发：①**入口边界**——token 在 `SendAsync` 首语句检查（LocalTableRuntime.cs:337）前已取消：OCE 即抛，零落盘/零 digest/零 control result，幂等 key 未消费（同 bytes 随后正常持久化、原样重发精确重放单记录）；②**控制门边界**——用既有控制 gate：`GatedFileSystem` 挂 persist 前钩子持住 `_controlGate.WaitAsync`（HandleControlCommandAsync:1381），第二个请求确定性 park 在门等待（未完成任务对象 = 到达证明，gate 放行前取消）→ 零持久写入（对账 against preAttemptRow，两读之间无持久化触发）；③**段内晚取消**——token 在 `TryCommitRejectedReceipt` 的 `Persist(CancellationToken.None)`（LocalTableStore.cs:1349）执行中真实触发：提交照常 Committed（persist→publish 按设计不可取消），结果可查询、单记录、随后同 bytes 精确重放 Replayed=true；若提交未完成则零落盘。每个 token 的触发边界、native 结果、磁盘行与 outbox 断言逐格落在测试断言与报告 §3 | final fresh ×3（`repeat-Cancelled...-{1,2,3}`，exit 0）+ LocalControl 类/LocalHost 全量含该测试 |
| （新缺陷，仅本轮新信息可发现）LocalHost 产品缺陷：`LocalTableHostOptions.AutoPlay` 为死配置 | 产品缺陷（本任务边界内的 LocalHost 修复面） | `LocalTableRuntime` 构造函数无条件 `_driver.Start()`（修前 :89），`AutoPlay` 选项经校验但**从未被消费**：worker 设 `AutoPlay=false` 并注释"driver stays disabled"，驱动实际照跑——冷进程恢复后驱动 15–20ms 轮询照常推进状态（AI 出牌、摸牌），冷静止断言系统性不成立（预期：开关真实生效；默认 true 时其余消费者零行为变化） | 本轮调试链（首败证据全保留）：`r3-cold2`（worker marker 丢失）→ `r3-cold3`（state moved）→ `r3-cold5/7`（状态 2→3、事件 4→6）→ `r3-cold8`（reconcile 段移动）→ `r3-cold9`（moved_event TileDiscarded/seat3 + TileDrawn/seat0）→ `r3-cold10`（**铁证** `moved_command AI_DISCARD/acct=local-room-1-seat3/req=local-ai-1/v4`）。此前不可发现的原因：AutoPlay=false 唯一消费者就是本轮新增的冷进程 worker（R1-03 才引入真实 OS 冷进程模式），此前所有路径都用默认 true，死接线在既有测试面内不可观测 | `LocalTableRuntime.cs` 5 行：`if (options.AutoPlay) { _driver.Start(); }`——默认 true，全部既有测试零行为变化（Client 182、TableRuntime 413、LocalHost 全量含 03/04 模式全绿可证） | `repeat-Persisted...-{1,2,3}` 冷进程 moved_event/moved_command 空集 + trace_state 七步恒值；四套全量 final fresh 全绿 |

### 1c. R2-03 精确契约限制与最小可行方案（本轮只报告，不实施）

- **精确契约限制（代码级，可复核）**：`HandleHelloAsync`（`LocalTableRuntime.cs:411-446`）对每条连接调用 `_scopes.Issue(...)` 生成 fresh scope 并写入 `_connectionScopeId`；`HandleCommandAsync` 在解码后、任何持久化/线性化之前要求 `envelope.RequestScopeId == _connectionScopeId`，不匹配即 `Unauthenticated` 返回（`:730-735`）。因此冷进程即使持有原 scope id，也无法用原 scope 发 COMMAND；唯一能携带原 scope 的 wire 消息是只读 `ReceiptQuery`（`:1782` 起）。若服务端接受原 scope 发命令，等价于允许跨连接/跨认证复用 scope，直接破坏“每连接唯一 scope”的鉴权边界。
- **最小可行方案（需协议/产品决策；违反本任务“不改公共协议/不放宽鉴权”约束，故本轮不实施）**：
  1. `Hello` 增加可选 `ResumeScopeId` + 由 LocalHost 可信恢复通道签发的 `ResumeProof`（绑定 account/sessionId/旧 scope 仍有效且未过期）；服务端验证后把该连接绑定回原 scope。改动面：公共 wire DTO 增可选字段 + 鉴权新增一条受证明约束的恢复分支。
  2. 或新增仅 LocalHost 内部通道的 `ReceiptReplay(account, scope, requestId, bytes)` 管理操作，以同一幂等键触发服务端重放。改动面：内部管理面，不新增身份/权限。
- **所需边界**：任一方案都必须保持 ①scope 只能由认证握手签发/恢复；②不匹配 scope 仍在持久化前拒绝；③新 scope 不得覆盖原 receipt。在不改协议/鉴权的前提下，本轮以“原 scope 只读查询（新键写入前后各一次，字段恒等）+ 原记录字段恒等 + 新键隔离负例 + 每步 trace 状态恒值”作为可交付证据，并**明确不宣称“原键冷 COMMAND 重放 PASS”**。

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

取消边界说明（复核轮 2 D-LH02-R2-04 终版口径，取代第 1a 节历史行）：控制命令处理路径**全程传播调用方 `CancellationToken`**。可取消段止于线性化点之前：`SendAsync` 入口的 `ThrowIfCancellationRequested`（LocalTableRuntime.cs:337）→ 控制门 `_controlGate.WaitAsync(ct)`（:1381）→ 回执守卫 `TryGetAsync(ct)`（:1275）→ 鉴权 scope 读 `LookupAsync(ct)`（:1616）。此后 `TryCommitRejectedReceipt` 在锁内同步提交并**按设计**持 `CancellationToken.None`——"持久提交成功后才发布最终拒绝"的不变量依赖 persist→publish 段不可取消。复核轮 2 将取消测试升级为三边界矩阵（`CancelledStaleCommandBeforeLinearizationWritesNothingAndKeyStaysReusable`，全部在 AiSubmitHold 冻结基线下确定性触发）：**①入口边界**（token 在 :337 检查前已取消）→ OCE 即抛、零落盘、幂等 key 未消费（同 bytes 随后正常持久化、原样重发精确重放单记录）；**②控制门边界**（`GatedFileSystem` 持住 persist 前钩子使第二请求确定性 park 在 :1381 门等待，gate 放行前取消）→ 零持久写入，状态对账 against preAttemptRow（与冻结快照之间无任何持久化触发）；**③段内晚取消**（token 在 `TryCommitRejectedReceipt` 的 `Persist` 执行中（LocalTableStore.cs:1349）真实触发）→ persist→publish 按设计不可取消：提交已 Committed 则原结果可查询、单记录、同 bytes 随后精确重放 Replayed=true；未完成则零落盘。每个 token 的触发边界、native 结果、磁盘行与 outbox 断言逐格落在测试断言中；`Persist` 对命中前钩子的取消按干净 Aborted 分类，可观测行为（无持久记录 + InternalError + 条目 unresolved）与写失败一致，仍由 T8 同路径覆盖。

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
| （复核轮 R1-04）预线性化取消：零落盘、幂等 key 可复用；随后同 bytes 正常持久化 + 精确重放单记录 | `CancelledStaleCommandBeforeLinearizationWritesNothingAndKeyStaysReusable` | PASS（复核轮新增；复核轮 2 升级为三边界矩阵，见下） |
| （复核轮 2 R2-02）held-AI 负例：hold 下被拒回执持久化后持久行 == 冻结快照精确等值、无 AI 提交；释放后 AI 迟到提交真实落盘且状态前移 | `HeldAiFollowUpIsNotDeclaredSettledAndItsLateCommitLandsAfterRelease` | PASS（复核轮 2 新增，fresh ×3：`r3-heldai2/3/4`） |
| （复核轮 2 R2-04）取消三边界：入口预取消零落盘 + key 可复用；控制门 park 后 gate 内取消零写入（对账 preAttemptRow）；persist 段内晚取消 → 已 Committed 可查询、单记录、精确重放 | `CancelledStaleCommandBeforeLinearizationWritesNothingAndKeyStaysReusable`（三格一体） | PASS（复核轮 2 重写，fresh ×3：`r3-cancel4/5/6`） |
| （复核轮 2 R2-03）冷进程：原 scope wire 查询前后不变 + 冷 outbox 对账 + 新键隔离负例 + 七步 trace 状态恒值 + moved_event/moved_command 空集 + 记录数/原 CreatedAt/控制代次精确不变 | `PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged`（返修终版） | PASS（隔离/查询/对账/状态恒值子矩阵；final fresh ×3 `repeat-Persisted...-{1,2,3}`。原键冷 COMMAND 重放不在可交付范围，精确限制与最小可行方案见 §1c，不宣称该子项 PASS） |

复核轮测试过程修正（如实记录，非弱化）：首轮的 settle 门（"seat0 出现未决 decision 即认为 AI 后续提交已 settle"）经时间线探针证明不成立——cheat window 阶段逐座提交与 seat0 未决 decision 交错（sv 在门放行后仍 1→4），基线取早导致 T5 精确等值断言失败。终版改为真实静止边界：持久状态行 `(StateVersion, LastEventSeq)` 连续 5 次 25ms 间隔全等；scope 过期（24h）级联会把 cheat window 停在"无未决 decision、未终局"的静止态（探针实测 sv/ev 恒 0 持续 12s+），故无需活跃 decision 的测试只用静止门。T2 冷进程验证初版在 open 握手前读 `RuntimeForMatch`（懒暴露，恒 null）导致 worker exit 4；改为 open 握手后读取并断言 `RecoveryGeneration>0`。两处均无断言弱化，精确等值断言全部保留。

复核轮 2 测试过程修正（如实记录，非弱化，首败证据全保留于 `r3-*.log/xml`）：①上述 5×25ms 静止门被审查探针 `ReviewerHeldAiMustNotBeDeclaredQuiescent` 证明仍非确定（时间启发式无法排除门放行后的 AI 迟到提交），终版整体替换为 AiSubmitHold 确定性暂停 + 权威队列冻结快照基线（见 §1b R2-02），`WaitForDurableStateStillnessAsync` 及其全部调用点删除（grep 零残留）。②T2 冷进程断言"原记录与新键记录 CreatedAt 必须不同"属**无效断言**：worker 时钟冻结为父进程 argv 传入值，两记录合法共享同一 `CreatedAtUtcMs`，记录区分度只由 KEY 承担（两记录各在各 scope 共存，已断言）——删除该断言并改为注释说明，非弱化（时间不携带信息）。③冷进程"状态零移动"断言连败 7 次（`r3-cold2..10`），经 per-step `trace_state` 七步探针 + `moved_event`/`moved_command` 落盘 dump 定位到铁证 `moved_command AI_DISCARD/acct=local-room-1-seat3/req=local-ai-1/v4`：**AutoPlay 是死配置**（`_driver.Start()` 无条件执行，选项从未被消费），冷进程驱动照跑。修复为真实接线（§1b 末行产品缺陷），非测试放宽——修后 moved 集合为空是构造性成立。④"父进程侧安静"的过强断言（快照与冲刷行比较）在释放后的驱动尾流下属过度声明，删除；冷精确性改由 worker 侧构造性保证（冻结时钟 + AutoPlay=false）。

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

### 5a. 交付轮最终 fresh 快照证据（8dff20d，独立 `git archive` 展开）

最终验收快照由**最终代码提交 `8dff20d6c302a2f440d4b4d166192b857b19f9fb`** 的 `git archive` 独立展开（仅 `dotnet/` + `global.json`，无 `.git`、无工作树二进制；zip SHA256=`3A0C417BB7B3FBA0AA36EF0D0DAF6097ECAE12EB5527F8D0AF4E64A4B9127497`）。快照内 fresh 构建：`Mahjong.LocalHost.Tests`（bin/Debug/net8.0，运行目标）、`Mahjong.LocalHost.CrashWorker`（bin/Release/net8.0，worker 实际拉起目标）、`Mahjong.Client.Tests`（bin/Debug/net8.0），全部 exit 0、0 警告 0 错误（`fresh-build-*.log`）。命令：`D:/Unity/dotnet/dotnet.exe <snapshot dll> -noLogo ... -result-xml <xml> -parallelMode none`，dotnet 8.0.426。下表全绿行 native exit 均为 **0**（实测；与日志 `Failed: 0/Errors: 0` 一致）。

| 套件 | native exit | Total | Failed | Skipped | Errors | Not Run | 日志 / XML（`build/lh02-final-r3/`） |
|---|---|---|---|---|---|---|---|
| LocalControl 定向类（含 held-AI 负例） | 0 | **23** | 0 | 0 | 0 | 0 | `fresh-localcontrol-class.log/.xml` |
| LocalHost 全量 | 0 | **115** | 0 | 0 | 0 | 0 | `fresh-localhost-full.log/.xml` |
| Client 全量 | 0 | **182** | 0 | 0 | 0 | 0 | `fresh-client-full.log/.xml` |
| TableRuntime 全量 | 0 | **413** | 0 | 0 | 0 | 0 | `fresh-tableruntime-full.log/.xml` |
| held-AI 负例 ×3 | 0 | 1×3 | 0 | 0 | 0 | 0 | `repeat-HeldAi...-{1,2,3}.log/.xml` |
| 取消三边界 ×3 | 0 | 1×3 | 0 | 0 | 0 | 0 | `repeat-Cancelled...-{1,2,3}.log/.xml` |
| 冷进程端到端 ×3 | 0 | 1×3 | 0 | 0 | 0 | 0 | `repeat-Persisted...-{1,2,3}.log/.xml` + `cold-marker-{1,2,3}/cold-worker-marker.txt` |

- 冷进程原始 marker 已由 `LH02_EVIDENCE_DIR` 证据缝保留（`cold-marker-1/cold-worker-marker.txt` SHA256=`AEC4BDF07FD0E4DD6050C5F9115F2D74CE5F35F9ED6B306DDB0E750688ECB5C7`）：`recovery_generation=1`、原记录 `receipt_evaluated=1`、`query2_evaluated=1`、`newkey1/newkey2_evaluated=0`、`original_status/reason/digest/evaluated/created_after` 与父进程完全一致、`trace_state=open/hello/query1/newkey2/query2` 全部 `v2:e4`、无 `moved_event`/`moved_command` 行、`client_pending=0`、`client_unresolved=0`。这既证明原键只读结论未被新键写入改动，也明确证明两次冷 COMMAND 是**新键**（evaluated 0），不是原键重放。
- SHA256 manifest：`build/lh02-final-r3/manifest-final.sha256`（[src] 3 个代码/测试文件 + 本报告 + [bin] 4 个 DLL + 上述全部日志/XML/marker + 快照 zip + 冻结 `review-manifest.csv`）。
- 前序首次全绿链保留于 `build/lh02-glm-12a21ae95767/`（`r3-*`，含 AutoPlay 首败/归因链）；本轮终验以 `8dff20d` 独立快照为准。全部行 native exit=0，无任何 fail/skip/error/notrun；LocalControl 23≥22、LocalHost 115≥114。

## 6. 净 diff 与树状态（交付轮终态）

- 冻结审查 HEAD：`26a2db08e60b5b5402166236e2a4c95d4b4efaf9`；最终代码 HEAD：`8dff20d6c302a2f440d4b4d166192b857b19f9fb`；本报告 docs 提交紧随其后（提交说明 `docs(v5): deliver D-LH-GATE-02 round-3 rework evidence`，因报告含自引用不在正文固化自身 SHA）。
- 复核轮 2 代码净 diff（`26a2db0..8dff20d`，3 文件，+1210/−373）：
  - `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs`（+4/−1，仅 AutoPlay 接线——本轮唯一产品修复）
  - `dotnet/tests/Mahjong.LocalHost.CrashWorker/Program.cs`（+278/−68，诚实隔离 marker + trace/moved-dump 证据仪器 + 冻结时钟/AutoPlay=false 构造性静止）
  - `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`（+928/−304，hold/冻结快照基线重写、held-AI 正式负例、取消三边界矩阵、T2 父侧 pre-cold-flush、marker 证据缝）
- 全程净 diff（`eeff5f9..8dff20d`，4 个既有源码/测试文件）：`LocalTableRuntime.cs`（+289/−18）、`LocalTableStore.cs`（+49/−0，首轮原语，本轮零改动）、`Program.cs`（+803/−0）、`LocalControlTests.cs`（+2098/−0）；加本报告。
- `git diff --check 26a2db0..8dff20d`：exit 0，无输出。
- `git status --porcelain=v1`：空（tracked clean；证据全部在 gitignored `build/` 下）。
- 仅本地 commits（`8075f58`、`6578701`、`8dff20d` + 本报告），未 push、未建 PR/MR、未 merge、未部署、未触碰 complete.flag/scheduler；PR20 仍由 caller 统一验收。

## 7. 边界确认

- 公共协议、Domain、Client、TableRuntime 源码零改动（diff 文件清单可证）。
- CrashWorker 首轮新增独立 `rejected-receipt-read` 模式，复核轮（D-LH02-R1-03）将其**替换**为真实冷进程闭环模式 `rejected-receipt-cold-session`；03/04 既有模式与其断言零改动，03/04 门禁未恢复/未修改。
- 未重做 P4.1，未触碰 TableService（93fb/D01）与 TableRuntime 在途修复（61f0）路径；本修复不需要其接口变动，无最小阻塞上报项。
- 预期文件范围：3 个源码/测试既有路径 + 本报告（共 4 个 tracked 文件），无新增源码路径。
- 审查方 scope-probe 四格探针已折入正式回归（`ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath`，覆盖 control/normal × expired/revoked 全四格并扩展至普通路径新请求鉴权）；审查冻结 HEAD `26a2db0` 之后本轮仅以上述 3 文件 + 本报告变化。
