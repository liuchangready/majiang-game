# V5 BATCH-LH-COLD-01 独立诊断报告

日期：2026-10-29（Asia/Shanghai）
线程：仅本 DeepSeek 会话；未转派、未启动子 Agent、未操作其他任务。
结论：**BATCH-LH-COLD-01 已独立复现，根因已定位；批次仍为 FAIL；本报告不构成批次通过，也未修改任何生产代码。**

---

## 0. 范围、基线与声明

### 0.1 工作树与基线

| 项 | 值 |
|---|---|
| 工作树 | `D:/majiang-wt/local-cold-diagnosis-20261029` |
| 分支 | `wt/v5-local-cold-diagnosis-20261029` |
| 新工作树基线 | `origin/v5` = `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（accepted baseline） |
| 默认统一门禁失败头 | `b93d2a36d4a5ed2fcaf0c6670852a81e2899013f` |
| PR20 冻结诊断输入 | `ab8d45f27f9f9d277127eba8872abb7456292557`（未通过批次，不视为 accepted baseline） |
| 已接受 LocalHost 输入 | `4a08dd783ebff2d49f53a723ca3ad84dbe56851e` |
| 稳定生产基线 | V4；G1 approved；G2–G5 未批准 |

`ab8d45f` 与 `b93d2a3` 在本缺陷相关路径上无源码差异：

```text
git diff --stat b93d2a3 ab8d45f -- \
  dotnet/src/Mahjong.LocalHost \
  dotnet/src/Mahjong.TableRuntime \
  dotnet/tests/Mahjong.LocalHost.Tests \
  dotnet/tests/Mahjong.LocalHost.CrashWorker
# 输出为空（native exit 0）
```

因此，`b93d2a3` 的失败在 `ab8d45f` 上可同一源码复现；本报告不把 `ab8d45f` 视为 accepted，也不把 `b93d2a3`/`ab8d45f` 的批次结论改为通过。

### 0.2 约束遵守

- 未修改其他工作树、分支、生产代码、Unity、Resources、Burst、`.sln`、统一 verifier。
- 未 merge、push、创建/更新 PR、部署。
- 未删除或弱化任何既有断言，未增加 skip，未用 sleep/重试冲绿。
- 分析/验证只在本次工作树 ignored 目录 `build/v5-cold-diagnosis-20261029/` 内进行：
  - 用 `git archive` 从 `ab8d45f` 和 `4a08dd7` 建立候选源码副本；
  - 只在候选副本内加入诊断输出/实验补丁，未回写本工作树 tracked 文件。
- 本次唯一提交物为本报告；证据全部保留在 ignored 目录或既有只读证据目录。

---

## 1. 证据台账（PASS / FAIL / BLOCKED / NOT_RUN）

| ID | 检查 | 实际结果 | native exit | 状态 |
|---|---|---|---:|---|
| E1a | 原始 `b93d2a3` 失败 XML/日志与 ticket 记录一致性审计 | XML SHA256 与 ticket 记录完全一致；失败节点/行号/`Expected`/`Actual` 一致；原始 meta 记录 exit 1 | 1（原始记录） | **PASS（审计）** |
| E1b | 原始 `b93d2a3` 默认统一门禁 LocalHost 结果 | 115 total / 114 passed / 1 failed / 0 errors / 0 skipped / 0 not-run；失败即 `PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged` | 1 | **FAIL（批次）** |
| E2 | `ab8d45f` 候选 LocalHost.Tests + CrashWorker fresh Release 构建 | 0 warnings / 0 errors | 0 / 0 | **PASS** |
| E3 | `ab8d45f` 定向运行失败用例 | Total 1 / Failed 1；Expected `local-human-0-0-CheatWindow`，Actual `""` | 1 | **FAIL** |
| E4 | `ab8d45f` 完整 LocalHost.Tests 程序集 | 115 total / 114 passed / 1 failed / 0 errors / 0 skipped / 0 not-run | 1 | **FAIL** |
| E5 | `4a08dd7` 同一测试文件、同一用例 | Total 1 / Failed 0 | 0 | **PASS** |
| E6 | 父进程/冷进程聚焦探针（`evidence/probe2`，真实 OS 子进程、真实存档、真实 wire） | 父待决 `local-human-0-0-CheatWindow`；冷待决为空；版本/回执/摘要一致 | 1（测试本身失败，探针完成） | **PASS（诊断执行）** |
| E7 | 冷恢复 `FilterRecovered` 聚焦探针（`evidence/probe2`，与 E6 同一次父/冷执行） | `filter_before=1`、`filter_after=0`、`kept=False` | 1（测试本身失败，探针完成） | **PASS（诊断执行）** |
| E8 | open-decision 聚焦探针（`evidence/probe3`，与 E6/E7 独立运行） | 记录到 stale-snapshot open：snapshot v0/seat0，actor 实际 v1/seat1 | 1（测试本身失败，探针完成） | **PASS（诊断执行）** |
| E9 | 修复方向实验：转换期失效 + 等待屏障校正（仅候选副本） | 定向用例 3/3 次 `Failed: 0`；父/冷同为 `local-human-4-0-CheatWindow`，v4/e8 | 0 | **PASS（仅实验探针，不是验收）** |
| E10 | 在 `ab8d45f` 重跑默认统一门禁（含 PG/WSS/companion/全部门禁项目） | 未运行；需 PostgreSQL、端口、companion 等完整环境，且原批次已 FAIL，不能用局部结果替代 | — | **NOT_RUN** |
| E11 | 生产代码/Unity/Resources/Burst/verifier 修改 | 按任务要求未做 | — | **NOT_RUN（by design）** |
| E12 | 批次合并/发布 | 未做；仍为 Draft PR20 的未通过批次 | — | **NOT_RUN（out of scope）** |

> `E9` 只证明最小修复方向能消除本用例的失败，不证明批次、全量 LocalHost 或统一门禁通过。没有任何“单测重跑绿 → 批次通过”的推断。`E6/E7` 与 `E8` 是两次独立执行，不可把它们的日志拼成同一次运行。

---

## 2. 原始 XML / 日志核实

### 2.1 原始证据位置与摘要

- 批次证据根：`D:/majiang-wt/receipt-batch-a9a74d09/artifacts/codex-batch-82d7eefd/`
- 失败 XML：`unified/direct-xml/Mahjong.LocalHost.Tests/direct-results.xml`
  - SHA256：`5E1E966FC13CD2D98AD38198538D77921B7C469ADB87A235BA8F19250F809F48`
  - 长度：69,637 bytes
  - 与调用方验收文档记录值一致。
- 原始 framework log：`unified/direct-Mahjong.LocalHost.Tests.log`
  - SHA256：`69AF401EE25560128B3CC0BCD10CB6A97D4C353714D5D8922032D510DFB0302E`
  - 长度：1,125 bytes
- 原始运行元数据：`unified/runs/Mahjong.LocalHost.Tests/direct.meta.txt`
  - SHA256：`62DB68D6D2B7636CBB8C0495FCFFBC1AA83FFB1E6AA03F8718D721F17D75314F`
  - head：`b93d2a36d4a5ed2fcaf0c6670852a81e2899013f`
  - command：`D:/Unity/dotnet/dotnet.exe ...Mahjong.LocalHost.Tests.dll -noLogo -reporter default -result-xml ...`
  - exit code：1
  - total：115 / failed：1 / skipped：0
  - XML gate：`FAIL: native exit is 1, not 0`

### 2.2 失败节点原文

```text
Mahjong.LocalHost.Tests.LocalControlTests.PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged [FAIL]
Assert.Equal() Failure: Strings differ
Expected: "local-human-0-0-CheatWindow"
Actual:   ""
  at ...LocalControlTests.cs:line 1278
  at ...LocalControlTests.cs:line 1343
=== TEST EXECUTION SUMMARY ===
  Mahjong.LocalHost.Tests  Total: 115, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0
[verify-v5] native exit code: 1
```

该失败不是编译失败、不是 `Total=0`、不是旧 DLL：候选副本 fresh Release 构建后在同一源码上稳定复现；原始 XML 也明确记录 native exit 1。

---

## 3. 源码差异：失败为何出现在 `ab8d45f`

### 3.1 相关路径差异

`git diff --stat 4a08dd7 ab8d45f` 在 LocalHost/TableRuntime/测试/Worker 范围内只有以下文件变化：

```text
dotnet/src/Mahjong.TableRuntime/TableActor.cs                       | 312 +++++++++------
dotnet/src/Mahjong.TableRuntime/TableKanWallEvidence.cs             | 442 +++++++++++++++ (新增)
dotnet/src/Mahjong.TableRuntime/TableResponseDecisionReconciliation.cs | 257 +++++++++++++++ (新增)
dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs        | 123 ++++--
```

关键点：

- `LocalControlTests.cs` 在 `4a08dd7` 与 `ab8d45f` 的 blob 完全相同：
  - `a4a06206779a583d55d6c337b6c7306255175cb7`
- `LocalTableDriver.cs` 在两个输入上也完全相同：
  - `d583701b7bdc18e4ed20657512bce0790af5ede4`
- `TableActor.cs` 发生变化：
  - `4a08dd7`：`184f87c16cfd646344b554cc7280ce8593005940`
  - `ab8d45f`：`de2142bf830138a61cf903e46de73418887453b4`
- 新增 `TableResponseDecisionReconciliation.cs`：
  - `ab8d45f`：`2818057d94af3bac86c3c9836693a25cc32733e6`

结论：这不是 LocalHost 测试或 LocalTableDriver 的单独改动，而是 **TableRuntime 的恢复/转换期决策协调逻辑** 与既有 LocalHost 冷恢复用例的集成交互。

### 3.2 引入时间

```text
662e1bb  fix(v5): fence closed response windows and model kan wall evidence
         -> 首次加入 TableResponseDecisionReconciliation + FilterAfterTransition/FilterRecovered
aa37074  fix(v5): close response recovery bypass and prove kan cold recovery
         -> 扩展恢复过滤规则
b1f3ba5  fix(v5): preserve legal turn kan and close wall-tail recovery gaps
```

`4a08dd7` 的树不包含这些 TableRuntime 变更；同一测试在 `4a08dd7` 上通过（E5），在 `ab8d45f` 上失败（E3/E4）。这构成明确的集成回归，而不是“测试本身一直坏”。

---

## 4. 独立进程与聚焦探针事实

### 4.1 探针方法

在 ignored 候选副本中只增加诊断输出，不改变权威逻辑。本报告使用两次**独立**探针执行，日志不得合并：

- `evidence/probe2`（父/冷/过滤探针）：
  - 父进程：在失败用例内记录父 runtime 的 round/phase/current seat/pending、store state/timing journal，以及 post-flush 状态；
  - 冷进程：在 `Mahjong.LocalHost.CrashWorker` 的 `rejected-receipt-cold-session` 模式中记录冷启动磁盘上的 timing journal、冷恢复后的 round/phase/current seat/window/pending，以及 `TableResponseDecisionReconciliation.FilterRecovered` 的输入/输出/逐决策 kept 值。
- `evidence/probe3`（open-decision 探针，另一次执行）：
  - 只在 `LocalTableDriver.OpenDecisionAsync` 记录 snapshot state version/seat/phase 与 actor 实际 seat/phase；
  - 该执行不提供 §4.2/§4.4 的父/冷/过滤数值。
- 两次执行各自写入独立文件，均不参与用例断言；原始失败仍以原有断言和 native exit 判定。
- 引用边界：§4.2 与 §4.4 的父/冷/过滤数值只来自 `evidence/probe2`；§4.3 的 open 序列只来自 `evidence/probe3`。不得把两者拼成同一次执行日志。

候选副本 SHA256：

- `ab8d45f` 全仓 archive：`5AE2CB33E030B9E1179BF0F1B26B39E70178647943DA3D208E6FD7861702F3A5`
- `4a08dd7` 全仓 archive：`FB8552339AB2C0462B36CCE82F85030865AE12BCE66D434FB2608D1285650DA4`

### 4.2 父进程事实（evidence/probe2）

以下数值来自 `evidence/probe2/parent-diag.txt` 的父/冷/过滤探针执行。

```text
parent_pending=local-human-0-0-CheatWindow
parent_round_id=room-1-r1
parent_phase=CheatWindow
parent_current_seat=1
parent_window_null=True
parent_pending_0=local-human-0-0-CheatWindow
parent_pending_1=local-ai-1-1-CheatWindow
parent_pending_2=<null>
parent_pending_3=<null>
parent_store_state_version=1
parent_store_last_event_seq=2
parent_timing_version=5
parent_timing_state_version=1
parent_timing_decision_count=2
parent_timing_decision=0|local-human-0-0-CheatWindow|...
parent_timing_decision=1|local-ai-1-1-CheatWindow|...
```

注意：本节的父进程数值与 §4.3 的 open-decision 序列**不是同一次执行**。open 序列来自 `evidence/probe3`，独立运行，仅用于证明 stale-snapshot open 可发生；它不用来替换或拼接本节的父进程时序。

### 4.3 open-decision 独立探针（evidence/probe3，另一次运行）

以下序列来自 `evidence/probe3/open-decision-diag.txt`，与 §4.2/§4.4 的 `evidence/probe2` 是两次独立执行；不得把本序列与 probe2 的父/冷/过滤数值视为同一进程、同一 run 或同一份日志。

```text
open_decision=local-human-0-0-CheatWindow|snapshot_v=0|snapshot_seat=0|snapshot_phase=CheatWindow|actual_seat=0|actual_phase=CheatWindow|pending_before=<null>
open_decision=local-human-0-0-CheatWindow|snapshot_v=0|snapshot_seat=0|snapshot_phase=CheatWindow|actual_seat=1|actual_phase=CheatWindow|pending_before=<null>
open_decision=local-ai-1-1-CheatWindow|snapshot_v=1|snapshot_seat=1|snapshot_phase=CheatWindow|actual_seat=1|actual_phase=CheatWindow|pending_before=<null>
open_decision=local-ai-2-2-CheatWindow|snapshot_v=2|snapshot_seat=2|snapshot_phase=CheatWindow|actual_seat=2|actual_phase=CheatWindow|pending_before=<null>
```

第二行是关键的 stale-snapshot open：`snapshot_v=0 / seat0`，但 actor 实际已是 `seat=1`。`ProcessOpenDecisionAsync` 接受了它，因为该函数只校验 roundId、controlEpoch、round 未结束、该 seat 没有已存在 pending，**不校验该 seat 是否仍是当前行动 seat / 该 snapshot 是否仍是当前状态**（`TableActor.cs:5216-5259`）。该事实说明 open 路径存在竞态窗口；本次失败的可复现主因仍是 §4.4 的转换期未回收，不应把 probe3 的时序当作 probe2 的逐条日志。

### 4.4 磁盘与冷进程事实（evidence/probe2）

本节全部父/冷/过滤数值来自 `evidence/probe2` 的**同一次**父/冷执行；open-decision 序列见 §4.3 的另一次独立执行（`evidence/probe3`），两者不可合并。

父进程 flush 后：

```text
post_store_state_version=2
post_store_last_event_seq=4
post_pending_0=local-human-0-0-CheatWindow   (当前 seat=2，仍存在)
post_timing_version=7
post_timing_decision_count=2
post_timing_decision=0|local-human-0-0-CheatWindow
post_timing_decision=2|local-ai-2-2-CheatWindow
```

真正交给冷进程的磁盘 cut（父 dispose 前最后一笔 autonomous commit 之后）：

```text
disk_state_version=3
disk_last_event_seq=6
disk_timing_version=8
disk_timing_decision_count=1
disk_timing_decision=0|local-human-0-0-CheatWindow|...
disk_checkpoint_state_version=0
disk_checkpoint_last_event_seq=0
```

冷进程恢复后：

```text
round_id=room-1-r1
phase=CheatWindow
current_seat=3
is_finished=False
window_null=True
pending_0=<null>
pending_1=<null>
pending_2=<null>
pending_3=<null>

filter_current_seat=3
filter_phase=CheatWindow
filter_window_null=True
filter_before=1
filter_after=0
filter_decision=0|local-human-0-0-CheatWindow|kept=False
```

冷进程的空值不是“读不到存档”、不是“回执丢失”、不是“版本不匹配”：

- 回执仍为 `Rejected / DECISION_MISMATCH / CLAIM_CONTROL`；
- `receipt_evaluated=1`、`created/expires/digest` 与父进程一致；
- 冷 `state_version=3`、`last_event_seq=6` 与磁盘 baseline 一致；
- `cold_round_id` 与父一致；
- 唯一不一致是 pending decision 的身份/归属。

### 4.5 对照：同一用例在 `4a08dd7`

冷 marker：

```text
state_version=2
last_event_seq=4
cold_round_id=room-1-r1
cold_pending_decision=local-human-0-0-CheatWindow
```

`4a08dd7` 的冷恢复没有 `FilterRecovered` 的归属过滤，直接把 timing journal 中的旧决策装回，所以父/冷“相等”；这并不证明该决策仍然合法，只证明旧实现把 stale 决策一起保留了。

### 4.6 修复方向实验（仅候选副本）

在候选副本上同时采用：

1. `FilterAfterTransition` 在每次提交转换时按 next state 的归属回收非当前 turn 的 pending（并取消对应 timing）；
2. `WaitUntilFollowUpsSettleAsync` 的里程碑要求 `CurrentSeat == 0` 且 seat0 pending 非空（或 round finished）。

结果（3 次定向运行，均为 `Failed: 0`）：

```text
parent_pending=local-human-4-0-CheatWindow
parent_current_seat=0
parent_store_state_version=4
parent_store_last_event_seq=8
cold_current_seat=0
cold_pending_decision=local-human-4-0-CheatWindow
```

版本、回执、摘要、冷 scope 隔离等既有断言全部保持通过。该实验只用于验证因果和修复范围，不是交付代码，也不代表默认统一门禁通过。

---

## 5. 因果链

本节引用两套独立探针：§4.2/§4.4 的父/冷数值来自 `evidence/probe2`，§4.3 的 open 序列来自 `evidence/probe3`。下面的因果链是跨两次观察的归纳，**不是**同一次执行日志；不得把 probe3 的 open 行与 probe2 的父/冷数值拼成一条时间线。

1. **初始决策与退休**：测试最初捕获的决策是 `local-human-initial`，并主动让它退休；随后 Driver 在 v0/seat0 打开 `local-human-0-0-CheatWindow`（§4.3 probe3 第一行：snapshot v0/seat0，actor 也确为 v0/seat0）。
2. **stale-snapshot open**：Driver 再次用旧 snapshot（v0/seat0）调用 `OpenDecisionAsync`，而 actor 实际已是 v1/seat1（§4.3 probe3 第二行）；`ProcessOpenDecisionAsync` 接受该 seat0 决策，未校验当前行动 seat。
3. **转换期不回收**（§4.2/§4.4 probe2）：AI 后续提交把 state 推进到 v1/v2/v3；`TableResponseDecisionReconciliation.FilterAfterTransition` 对“非 response decision”一律保留（源码注释：“A turn/cheat/settlement decision ... must survive an unrelated commit unchanged”），所以 seat0 的旧 turn 决策继续留在 `_pending` 和 timing journal。
4. **测试屏障误判**（§4.2/§4.4 probe2）：`WaitUntilFollowUpsSettleAsync` 只检查 `GetPendingDecisionId(0)` 非空；旧决策使它立即返回，测试于是在“当前 seat 尚为 1/2、turn 尚未回到 0”的 cut 上抓取 `parentPendingDecision`。
5. **冷恢复正确丢弃**（§4.4 probe2）：冷进程从磁盘恢复出 current seat 3、无 response window；`FilterRecovered` 按“无 window 时只有当前 seat 可拥有 turn decision”判定 seat0 决策非法，`filter_after=0`，于是 `cold_pending_decision=""`。
6. **断言失败**：测试拿父进程的 stale pending（`local-human-0-0-CheatWindow`）与冷进程合法恢复结果（空）比较，触发 XML 中的 `Expected/Actual`。

关键判断：

- 冷进程空值是 **恢复过滤器的正确 fail-closed 行为**，不是空值兼容问题；
- 父进程持有的是一个在 v1 之后已不归当前 seat 所有的 turn decision；
- `4a08dd7` 的通过来自“旧实现不丢弃任何 timing decision”，不能作为当前正确性依据；
- 单独重跑成功只可能来自时序变化（stale open 是否发生、何时 dispose），不能消除“turn decision 未按归属回收”的确定性缺口。

---

## 6. 精确修复范围

> 以下仅为诊断结论与建议范围；本报告不实施任何代码修改。

### 6.1 必需修复 A：转换期按归属回收 pending

文件：`dotnet/src/Mahjong.TableRuntime/TableResponseDecisionReconciliation.cs`
函数：`FilterAfterTransition`（`ab8d45f:22-83`）
现状问题：`if (!wasResponseDecision) { kept.Add(pending); continue; }`（`ab8d45f:59-65`）无条件保留所有非 response 决策，导致 turn 决策在 `CurrentSeat` 改变后仍存活。

要求：

- 让 `FilterAfterTransition` 与 `FilterRecovered`（`ab8d45f:92-147`）使用同一套“该决策是否仍被 next state 拥有”的判定，避免两套规则再次漂移：
  - `nextState.IsFinished`：取消全部 timing，返回空；
  - `nextState.Window != null`：仅保留 eligible 且未 responded 的 seat；
  - `nextState.Window == null`：仅保留 `pending.Key.Seat == nextState.CurrentSeat` 且候选含 turn anchor（或真实 turn kan）的决策；
  - 其余：`candidateClock.Cancel(pending.Key, now)` 并移除。
- 必须在同一 candidate clock / pending 列表上完成，确保 **state、pending、timing journal 在同一次提交中原子更新**；不能提交后再补删。
- 保留已有多 seat response window 语义：同一 window 内其他 eligible、未响应 seat 的 decision/timing 不得被误删。
- 保留 `ClosedWindowDropsStaleDecisionAndTimingInTheSameCommit` 等既有 response 负例；不得删除/弱化。

### 6.2 必需修复 B：测试等待屏障按“回合真正回到 0”判定

文件：`dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`
函数：`WaitUntilFollowUpsSettleAsync`（`ab8d45f:2710-2716`）
现状问题：`!string.IsNullOrEmpty(GetPendingDecisionId(0))` 会被 stale seat0 decision 满足，导致过早采样。

要求：

- 条件改为：`(CurrentSeat == 0 && seat0 pending 非空) || round.IsFinished`；
- 保持事件驱动轮询/既有 `WaitUntilAsync`，不得加 `sleep`、不得放宽为“任意 pending 非空”、不得靠重试；
- 该修改只修正“采样 cut”，不改变原断言；`assert parentPending == coldPending` 必须保留。

### 6.3 建议修复 C（防御层，需明确契约）

可选地在 `TableActor.ProcessOpenDecisionAsync`（`ab8d45f:5216-5259`）增加“当前状态归属”fence：

- 无 response window 时，只接受 `decision.Key.Seat == _round.CurrentSeat` 的 turn decision；
- 有 window 时，只接受 eligible 且未 responded 的 seat；
- 在 timing/pending 写入前 fail-closed 返回 `Rejected`，不得产生 timing journal 行。

注意：现有 `TableResponseDecisionInvalidationTests.ClosedWindowDropsStaleDecisionAndTimingInTheSameCommit`（`ab8d45f:75-124`）明确把“stale host open 被 actor 接受、下一次提交再失效”作为既有 host-race 契约。若采用 C，必须同步、显式地修订该契约测试的期望（改为 open 时拒绝），不得悄悄改断言。最小修复 A+B 已足以消除本用例的失败；C 用于避免 stale pending 在“下一次提交之前”被 UI/客户端观察到，属于防御性增强。

### 6.4 回归测试范围

1. `dotnet/tests/Mahjong.TableRuntime.Tests/TableResponseDecisionInvalidationTests.cs`
   - 新增：turn 决策在 `CurrentSeat` 推进的提交中被原子移除；`GetTimingSnapshotAsync` 对应 seat 的 `PendingKey/Timing` 均为 null；
   - 新增：同一提交中 response window 内其他 eligible seat 的决策仍保留；
   - 新增：提交失败（注入 fault）时 pending/timing 不变；
   - 新增：finished round 清空全部 pending/timing。
2. `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`
   - 保留原用例与 line 1278 断言；
   - 在捕获 `parentPendingDecision` 前断言 `parent_round.CurrentSeat == 0`；
   - 断言 `parentPendingDecision` 的 `local-human-<stateVersion>-0-<phase>` 与当前 state/seat 一致，防止 stale ID 再次被当作“同一决策”；
   - 断言冷/父 `cold_round_id`、pending、state version、event seq 一致。
3. 负例矩阵：
   - seat != current 且无 window 的 turn decision：转换后不得存活、恢复后不得存活；
   - window 内 eligible/unresponded：必须存活；
   - window 已关闭/已 respond：必须删除；
   - 旧 controlEpoch、旧 roundId、重复 seat、非法 candidates：仍按既有 fail-closed 拒绝；
   - 失败提交/取消提交：不得部分删除或部分写入 timing。

### 6.5 明确禁止的“修复”

- 把冷空值改成空字符串兼容、把断言改为 `string.IsNullOrEmpty`；
- 删除 `Assert.Equal(parentPendingDecision, MarkerValue("cold_pending_decision="))`；
- 在测试或 Driver 中加 sleep/重试，靠时序碰巧通过；
- 修改统一 verifier、降低 XML gate、skip 该用例；
- 用新的 cold scope / 新 requestId 重做原命令来伪造“原键重放”；
- 把 `4a08dd7` 的旧“保留所有 timing decision”行为当作正确性回退。

---

## 7. 保留回执/版本/身份的验收断言

修复后，以下断言必须原样保留并通过；不得因 pending 修复而放宽：

### 7.1 回执与摘要

- `receipt_found=1`
- `receipt_status=Rejected`
- `receipt_reason=DECISION_MISMATCH`
- `receipt_api=CLAIM_CONTROL`
- `receipt_evaluated=<父进程同一值>`（本复现为 1）
- `receipt_created=<父进程同一值>`（本复现为 1700000030000）
- `receipt_expires=<父进程同一值>`（本复现为 1700086430000）
- `receipt_command_version_present=0`
- `receipt_event_seq_present=0`
- `digest_hex=<父进程同一 digest>`（本复现为 `f4b46cf7309c556b0624517cef58d80292ad21ad88617fe65d8930956cf5d5ff`）
- `original_created_after` / `original_evaluated_after` / `query2_evaluated` 与父值一致
- `client_disposition=Rejected`、`client_reason=DECISION_MISMATCH`、`client_pending=0`、`client_unresolved=0`

### 7.2 版本与身份

- `cold_round_id == parent roundId`（本复现 `room-1-r1`）
- `cold_pending_decision == parentPendingDecision`，且非空
- `cold_pending_decision != retiredDecisionId`（本复现 retired 为 `local-human-initial`）
- `cold_pending_decision` 的 state/seat 段与 `cold` 的 `state_version`、`CurrentSeat==0` 一致（建议新增，防止 stale ID）
- `recovery_generation > 0`（必须是冷恢复，不是新房间）
- `cold_scope` 非空且 `!= parent scope`
- `state_version` / `last_event_seq` 等于父进程交给冷进程的磁盘 baseline
- `final_state_version` / `final_last_event_seq` 与冷启动一致
- `final_control_version=0`、`final_control_record_present=0`
- `query_status=Found`、`query_receipt_status=Rejected`、`query_receipt_reason=DECISION_MISMATCH`、`query_digest=digest`
- `replay_key_scope=cold-issued`、`original_key_cold_command_replay=unreachable`（保持对“原键不可在冷 wire 重放”的诚实标注）
- `newkey1_replayed=0`、`newkey2_replayed=1`、`newkey1_evaluated=0`、`newkey2_evaluated=0`
- `newkey_record_created > 0`
- 父侧最终磁盘：原回执字段不变、cold-scope 新键记录存在且只一条、control result 为 null、outbox unresolved 为空

### 7.3 关闭 BATCH-LH-COLD-01 的门槛

1. 定向用例 `PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged`：1/1，native exit 0；
2. 完整 `Mahjong.LocalHost.Tests`：115/115，0 failed / 0 errors / 0 skipped / 0 not-run，native exit 0；
3. 在正确集成分支上重跑**默认统一门禁**（含原 `verify-v5-table-postgres.ps1 -RunUnifiedVerification` 的完整项目行），native exit 0，且无 skip/空测试/旧产物；
4. 逐项核对统一 verifier 的 PASS/FAIL/BLOCKED/NOT_RUN 计数、XML SHA、native exit、原始日志；
5. 只有以上全部成立，BATCH-LH-COLD-01 才可关闭；定向单测通过、或本报告的实验探针通过，都不能替代默认统一门禁，也不能宣布批次可合并。

---

## 8. 未运行 / 未覆盖

- 未在 `ab8d45f` 上重跑默认统一门禁全链路（PostgreSQL、WSS、DurableHost、companion、Lobby 等）；原因见 E10。
- 未运行 Unity、IL2CPP、Android 真机/模拟器、跨物理网络；本缺陷不涉及这些路径，但也不从本报告推断它们通过。
- 未执行任何生产代码修复、未创建修复分支、未 push、未 merge、未操作 PR20 或其他任务。
- 未读取或修改其他工作树 tracked 内容；仅只读访问既有批次证据和 ticket 文档。
- 探针运行使用本机 `D:/Unity/dotnet/dotnet.exe`（SDK 8.0.426）和候选副本；不把本机环境结果写成远端 CI 结果。

---

## 9. 证据路径与 SHA256

### 9.1 本工作树 ignored 证据

根：`D:/majiang-wt/local-cold-diagnosis-20261029/build/v5-cold-diagnosis-20261029/`

`evidence/probe2`（父/冷/过滤）与 `evidence/probe3`（open-decision）是两次独立执行；下表中的文件不得合并为同一次 run 的日志。

| 文件 | SHA256 | 说明 |
|---|---|---|
| `evidence/repro-ab8d45f/cold-worker-marker.txt` | `0306709CEA17386D3F48D154FCDAEC309537F469B1E6B412BAF0C7E8EEA0D59E` | 未修复冷 marker，`cold_pending_decision=` 为空 |
| `evidence/repro-ab8d45f/repro-results.xml` | `AD02A6D5C2D2A37221DC2FC87B7E81DA2BE09C864E11A7B1BE2B9800BDB8F5F7` | 定向复现 XML |
| `evidence/repro-ab8d45f/repro-stdout.log` | `7C85E34D78B82F901DCD3333623AFC6FF06297F348D4DA668B0B4F98AA36476F` | 定向复现 stdout，`Total: 1, Failed: 1` |
| `evidence/full-localhost-ab8d45f/results.xml` | `0B207BD280CF86ECD67F380A57AFFDD05805BEC12AB36A086F7201CFBCB5CFCC` | 完整 LocalHost XML，115/1 fail |
| `evidence/full-localhost-ab8d45f/stdout.log` | `DFAF9652CAB46940CA8E69827028F9791088B9E6301798460848C60D9C9744F7` | 完整 LocalHost stdout |
| `evidence/accepted-4a08dd7/cold-worker-marker.txt` | `FF485366F2009BA10589BFD97333516CB5508B4593441CF9C7D580319C052E93` | 4a08dd7 对照，cold pending 保留 |
| `evidence/accepted-4a08dd7/results.xml` | `33A7A1FD55EE7248D3CC47828347735AFAF872C60739A5BD403AD5E24D2CB382` | 4a08dd7 定向通过 |
| `evidence/probe2/parent-diag.txt` | `B5AC6B962067B973A1E5567162D988D5A1734D3012ED8A570FD7C3238F0C8FD2` | 父进程 pending/seat/timing |
| `evidence/probe2/disk-diag.txt` | `98862C936283C8F1AF61702988BF2D2CDCCB73AFA67AD584BDF1CBCF24EC926B` | 冷启动磁盘 cut |
| `evidence/probe2/cold-store-diag.txt` | `F255AA4CCB57E0F286BC393D6B7B85ED271DB7C6F01AD4985684B56ECF0DB051` | 冷进程 timing/checkpoint |
| `evidence/probe2/cold-round-diag.txt` | `6F90536ACD338367CF38AE87E086D67A811C0DBAAE5752186C815BE7F32A1D44` | 冷恢复 round/pending |
| `evidence/probe2/recovery-filter-diag.txt` | `8CCB8BCDC7D8F70EBF43F84431D4BBCABA12066397FB7CE0F077DFC725FDDF3B` | `filter_before=1 / after=0 / kept=False` |
| `evidence/probe2/postflush-diag.txt` | `BCC6BA662530F4F0A6488DA46E9DCEEA2C0665F3501DD44C08C7F25E396CB0B0` | flush 后 stale pending 仍在 |
| `evidence/probe3/open-decision-diag.txt` | `F88D8DC0FE95FA883247771B4587E67361093BE826B1D8D214FB06DFB32A0637` | stale-snapshot open 证据 |
| `evidence/fixprobe2/cold-worker-marker.txt` | `CF51B9F3663E7815B2840B0E3417F1FC08FF27BC4C067F22170F0748C5A2A190` | 修复探针冷 marker，pending 一致 |
| `evidence/fixprobe2/parent-diag.txt` | `8A5F62DEDAB2A7C601872B38AF3B436F953F62374707DF2C363A2EDBDC14CF6C` | 修复探针父 `local-human-4-0-CheatWindow` |
| `evidence/fixprobe2/cold-round-diag.txt` | `2A2524E7F8D28629B19180068666FB8938CB07CD69324317FF7F5E423B1D1C37` | 修复探针冷 current seat 0 |
| `evidence/fixprobe2/results.xml` | `26B7CCCD36411EA42BB9B2A46FEADDC6A2B648913B30EBE736A1003718D9520B` | 修复探针定向通过（仅实验） |
| `candidate-ab8d45f.zip` | `5AE2CB33E030B9E1179BF0F1B26B39E70178647943DA3D208E6FD7861702F3A5` | `git archive ab8d45f` |
| `candidate-4a08dd7.zip` | `FB8552339AB2C0462B36CCE82F85030865AE12BCE66D434FB2608D1285650DA4` | `git archive 4a08dd7` |

### 9.2 既有只读证据

| 路径 | SHA256 / 值 |
|---|---|
| `D:/majiang-wt/receipt-batch-a9a74d09/artifacts/codex-batch-82d7eefd/unified/direct-xml/Mahjong.LocalHost.Tests/direct-results.xml` | `5E1E966FC13CD2D98AD38198538D77921B7C469ADB87A235BA8F19250F809F48` |
| 同目录 `direct-Mahjong.LocalHost.Tests.log` | 见 §2；native exit 1 |
| `D:/majiang-wt/localhost-batch-failure-ds-20261029/doc/v5-重构/CODEX-V5-REMOTE-RECEIPT-TICKET-ACCEPTANCE-20261029.md` | 只读；BATCH-LH-COLD-01 来源 |
| `D:/majiang-wt/remote-receipt-ticket-ds-20261029-1446` | 只读；ticket 相关原始任务工作树，未操作 |

---

## 10. 复现命令

以下命令在候选副本内执行；路径以本工作树为例。

```powershell
$root = 'D:/majiang-wt/local-cold-diagnosis-20261029'
$cand = "$root/build/v5-cold-diagnosis-20261029/candidate-ab8d45f"
$dotnet = 'D:/Unity/dotnet/dotnet.exe'

# 1) 建立候选副本（本报告已建立；如需重来，使用新目录）
git archive --format=zip --output="$root/build/v5-cold-diagnosis-20261029/candidate-ab8d45f.zip" ab8d45f
# 解压后将 dotnet/ 放在 candidate-ab8d45f/dotnet/，并复制根 global.json

# 2) fresh Release 构建
Push-Location $cand
& $dotnet build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-incremental -nr:false
& $dotnet build dotnet/tests/Mahjong.LocalHost.CrashWorker/Mahjong.LocalHost.CrashWorker.csproj -c Release --no-incremental -nr:false

# 3) 定向运行（原始断言）
$env:LH02_EVIDENCE_DIR = "$root/build/v5-cold-diagnosis-20261029/evidence/repro-ab8d45f"
& $dotnet dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default -result-xml "$env:LH02_EVIDENCE_DIR/repro-results.xml" `
  -method 'Mahjong.LocalHost.Tests.LocalControlTests.PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged'
# 期望：Total 1 / Failed 1 / native exit 1

# 4) 完整 LocalHost 程序集
& $dotnet dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default -result-xml "$root/build/v5-cold-diagnosis-20261029/evidence/full-localhost-ab8d45f/results.xml"
# 期望：115 total / 114 passed / 1 failed / native exit 1

# 5) 对照 4a08dd7
#    使用 candidate-4a08dd7 重复构建与定向运行；期望 Total 1 / Failed 0 / native exit 0
```

修复探针不是本报告交付物；其关键改动是：

- `TableResponseDecisionReconciliation.FilterAfterTransition` 对非当前 seat 的 turn decision 执行 `candidateClock.Cancel`；
- `WaitUntilFollowUpsSettleAsync` 增加 `CurrentSeat == 0`；
- 仅用于候选副本验证，且 3 次定向运行 `Failed: 0`。

---

## 11. 结论与交接

- **BATCH-LH-COLD-01：REPRODUCED / ROOT CAUSE FOUND / OPEN**。
- 根因是 **turn decision 的归属回收缺口**：`FilterAfterTransition` 无条件保留非 response 决策，使 stale `local-human-0-0-CheatWindow` 在 `CurrentSeat` 推进后仍留在父 runtime 和 timing journal；冷恢复的 `FilterRecovered` 正确地把“无 window 且 seat != current seat”的决策丢弃，导致父/冷不相等。
- stale-snapshot open（`snapshot v0/seat0` → actor `v1/seat1`）是同一缺口的触发/放大路径；测试的 `WaitUntilFollowUpsSettleAsync` 又把这个 stale pending 当作“回合已回到 0”，从而在错误 cut 上采样。
- 回执、摘要、版本、scope 隔离均未损坏；不要把本失败归因于回执提交、wire 解码或冷进程读盘失败。
- 修复范围与验收断言见 §6/§7；在默认统一门禁实际通过前，批次结论仍为 FAIL。
- 本报告为唯一交付文件；未修改生产代码或其他任务。请 caller 独立复核并合入唯一 PR20。

---

## 附录 A：关键源码位置（均为 `ab8d45f`）

| 位置 | 作用 |
|---|---|
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs:5216-5259` | `ProcessOpenDecisionAsync`：只校验 round/epoch/pending，不校验当前 seat/snapshot 状态 |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs:3597,3832,4097` | 三处调用 `FilterAfterTransition`（正常/durable/cheat 提交路径） |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs:5618` | 恢复调用 `FilterRecovered` |
| `dotnet/src/Mahjong.TableRuntime/TableResponseDecisionReconciliation.cs:22-83` | `FilterAfterTransition`；`:59-65` 无条件保留非 response 决策 |
| `dotnet/src/Mahjong.TableRuntime/TableResponseDecisionReconciliation.cs:92-147` | `FilterRecovered`；正确的“current seat / window ownership”规则 |
| `dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs:292-337` | `OpenDecisionAsync`；决策 ID 用 snapshot state version 拼接 |
| `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs:1077-1078` | 测试等待 follow-up 后抓取 `parentPendingDecision` |
| `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs:1278` | 失败断言（父/冷 pending 相等） |
| `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs:2710-2716` | `WaitUntilFollowUpsSettleAsync`；只检查 seat0 pending 非空 |
| `dotnet/tests/Mahjong.LocalHost.CrashWorker/Program.cs:524` | 冷 marker 读取 `GetPendingDecisionId(0) ?? ""` |
| `dotnet/tests/Mahjong.TableRuntime.Tests/TableResponseDecisionInvalidationTests.cs:75-124` | 既有 host-race / stale response 契约测试（采用 C 时须显式修订） |

## 附录 B：关键 SHA

```text
8fd7c6cb701dfbb41c28938de4186749bdd1d6c7  accepted origin/v5 baseline
b93d2a36d4a5ed2fcaf0c6670852a81e2899013f  failing default unified gate head
ab8d45f27f9f9d277127eba8872abb7456292557  frozen PR20 diagnostic input
4a08dd783ebff2d49f53a723ca3ad84dbe56851e  accepted LocalHost input
662e1bb949904eb269f847ac083719843ed67554  first FilterAfterTransition/FilterRecovered
aa3707445e0bc007192eba09f9ef69faccc07c65  expanded recovery filter
b1f3ba5e8959773a56d78572c7f8ae125de18a3f  turn-kan recovery follow-up
```
