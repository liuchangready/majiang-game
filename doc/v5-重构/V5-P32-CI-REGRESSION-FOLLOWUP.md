# V5 P3.2b CI 调度回归修复交付（follow-up）

日期：2026-10-24

状态：**局部测试修复交付，等待 caller 独立验收；不是 G1/G5 通过，不是 P3.2 完整接线，也不是远端 CI 通过记录。**

工作树：`D:/majiang-wt/p32-ci-regression`

分支：`wt/v5-p32-ci-regression`

任务起点：`32f645ec9b134e9ad261a2df89437416b20b3119`（clean）
本任务唯一本地提交的父提交：`32f645ec9b134e9ad261a2df89437416b20b3119`

---

## 0. 任务来源、角色拒绝与范围

### 0.1 旧线程与真实拒绝结果

本任务是原 P3.2b 已接受组件（旧线程 `thread_20261024_031743_9246`）的新发现 CI 调度缺陷返修。caller 尝试在同线程/原 native session 发起 rework 时，bridge 原样返回：

```text
tool call error: tool call failed for `agent-mesh-bridge/request_rework`

Caused by:
    Mcp error: -32000: [Agent Mesh Bridge] Refusing review action for thread thread_20261024_031743_9246: no active reviewer role is held. A task accepts review decisions only from its current reviewer turn; finish or re-wake the same task instead of acting from a stale reviewer process.
```

因此本条是 caller 在 bridge 明确拒绝同线程返修之后下发的**显式后续任务**，不是对旧实现的重复开发；本任务不接管、不修改、不重置旧 thread 或旧工作树 `D:/majiang-wt/p32-streams`，也不绕过 reviewer 角色。

### 0.2 唯一授权范围

- 唯一工作树：`D:/majiang-wt/p32-ci-regression`。
- 唯一分支：`wt/v5-p32-ci-regression`。
- 只修复已留档的两个 TableRuntime/stream 调度失败；不重做整套 actor，不提前做 P3.4，不做完整 Wire 联合接线，不做兄弟 CI 环境恢复。
- 不转派、不接管兄弟线程、不改其他工作树、不 push、不建 PR、不触发远端 CI。
- 唯一提交文件：
  1. `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamActorTests.cs`
  2. `doc/v5-重构/V5-P32-CI-REGRESSION-FOLLOWUP.md`（本文件）
- 未修改生产 actor/stream 代码、verifier、测试跳过策略、超时参数、生产校验或既有交付文档；未删除测试；未增加 skip；未增加 `sleep`/盲重试。
- 保留无关 artifacts 和 Unity environment/Resources/Burst。

### 0.3 当前事实

- V4 仍是唯一生产架构；V5 未进入生产。
- G1、G5 均未通过；不得把本轮局部测试结果写成阶段门槛通过。
- 未做平台、真机、真实网络、Unity/IL2CPP、真实 WSS/TCP、PostgreSQL 验证。
- 本轮不新增 PG；未配置的 PG 统一入口不算 PASS。

---

## 1. caller 已留档的原始失败（不是本轮重跑结论）

以下为 caller 提供的真实失败记录，本任务原样保留，不改称 flaky：

| 证据 | 结果 |
|---|---|
| push run `40020648672`，提交 `8af240d` | TableRuntime **141 pass / 2 fail / 0 skip** |
| caller 统一 gate，提交 `8523d3d`（实际源码与 `32f645e` 相同） | **881 pass / 1 fail / 0 skip，exit 1**；其中 TableRuntime **149 pass / 1 fail / 0 skip**；build **0 warning / 0 error，12 DLL PASS** |
| push `40021452487` / PR `40021457715`（`32f645e`） | **882 pass / 0 fail / 0 skip**，但 wrapper env-restore **FAIL**；后续偶然调度通过不能关闭原始明确失败 |

两个失败的原始断言：

1. `RecoveryAfterCommitRebaselinesTheFinalCommittedState`
   - `Expected 3 frames observed 2`
   - 位置：`TableStreamActorTests.cs:310`、`TableStreamTestSupport.cs:131`
2. `NewBindingReplacesOldHandleAndOldHandleCannotCancelOrSyncNewStream`
   - `Expected 1 frame first displaced binding observed 0`
   - 位置：`TableStreamActorTests.cs:353`

本轮基线在修复前运行整个 `TableStreamActorTests` 类曾得到 **29 pass / 0 fail / 0 skip，exit 0**（见 §5 日志）。该绿色运行**不能**关闭上述历史失败：两次失败都取决于发送泵与 fence/rebind 的调度先后，原测试把“先发送”当成必然，而设计允许旧排队帧被合法丢弃。本任务用确定性顺序测试替代该调度假设，不以“再跑一次绿了”作为结论。

---

## 2. 根因判定：测试顺序假设，不是这两条路径的生产缺陷

### 2.1 真实设计

- `BeginRecovery` 递增 recovery generation 后立即对全部 channel 执行 **fence + purge**：清空旧 generation 的排队帧，并禁止旧 generation 再入队。代码：`dotnet/src/Mahjong.TableRuntime/TableActor.cs:1878-1885`。
- `TableStreamChannel.PurgeAndFence` 更新 `_allowedGeneration` 并清空队列：`dotnet/src/Mahjong.TableRuntime/TableStreamRegistry.cs:239-247`。
- `TryEnqueue` 对代次不匹配的帧直接拒绝：`TableStreamRegistry.cs:181-185`。
- 发送泵出队后、调用 sink 前，`TryClaimDispatch` 在 channel 锁内对**当前** fence 做一次原子认领；fence/close 先发生则丢弃，认领先发生则该次 sink 调用不可撤回：`TableStreamRegistry.cs:400-413`、`TableStreamRegistry.cs:330-336`。
- 新 binding 在同一个 actor turn 内先为新 channel 入队 full baseline，再移除并关闭旧 registration（`Displaced`）：`TableActor.cs:3720-3744`。因此旧 channel 已排队但尚未被 sink 认领的 baseline/update 可以合法地一帧都不发送。
- 恢复完成时对仍有效的流排入新的 full baseline，并置 `NeedsSync`，宿主显式 Sync 前不发增量：`TableActor.cs:4880-4927`。
- 设计与验收文档明确记录该行为：`doc/v5-重构/V5-P32B-ACTOR-STREAMS.md:30-31`、`:216`、`:223-224`、`:249`。

### 2.2 两个失败分别对应哪种合法顺序

| 失败 | 原测试假设 | 实际合法行为 | 判定 |
|---|---|---|---|
| `RecoveryAfterCommit...` | `SubmitAsync` 返回后旧 update 必已送达 sink，因此 recovery 后应看到第 3 帧 | update 可能仍在 channel 队列；`BeginRecovery` fence 后该旧帧被合法丢弃。最终 committed state 由 recovery full baseline 携带 | **测试顺序假设**；不是丢最终 baseline |
| `NewBinding...` | 连续两次 subscribe 后，第一个 sink 必已收到 baseline | 第二次 binding 在第一个 baseline 被发送泵认领前关闭旧 channel；旧 baseline 0 帧是合法结果，新 binding 的 full baseline 才代表当前身份 | **测试顺序假设**；不是旧帧穿越或新流缺失 |

这两种情况下，生产行为都满足：

- 恢复/重绑后的**新 generation full baseline** 是最终提交状态；
- 旧 generation 的排队帧不会在 fence 之后进入 sink；
- 旧 handle 不是当前 registration，不能 Sync/Unsubscribe 新流；
- 新流仍可继续接收后续 update。

因此本任务**不修改生产 actor/stream 代码**，只修测试到与设计一致，并增加确定性反例覆盖。

---

## 3. 修复内容

### 3.1 修正既有两个测试的调度假设

`dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamActorTests.cs`：

- `RecoveryAfterCommitRebaselinesTheFinalCommittedState`（`:283`）
  - 在 `SubmitAsync` 后先 `WaitForCountAsync(2)`，确认旧 update 已送达 sink，再 `BeginRecovery`。
  - 保留原 3 帧顺序断言（baseline→update→recovery baseline），并新增：
    - recovery baseline 的 `StateVersion == 1`、`ProjectionSeq == 3`、`RecoveryGeneration == 1`；
    - public board 中 seat 0 恰有 1 张弃牌（最终提交状态）；
    - `NeedsSync` 下 Sync 返回 `BaselineQueued`，并产生 seq 4 的 full baseline；随后再 Sync 为 `InSync`。
- `NewBindingReplacesOldHandleAndOldHandleCannotCancelOrSyncNewStream`（`:439`）
  - 在第二次 subscribe 前先等待第一个 sink 收到 1 帧 baseline（seq 1），再绑定 binding epoch 2。
  - 断言第二个 baseline 为 full、seq 2；旧 sink 仍只有 1 帧。
  - 保留旧 handle 不能 Unsubscribe/Sync、旧 binding/sync 被 `STALE_STREAM_VERSION` 拒绝的断言。
  - 新增：新流在旧 handle 被拒后仍能接收提交后的 update（seq 3，最终 stateVersion 1，seat 0 1 张弃牌）；旧 sink 仍为 1 帧；新流 Unsubscribe 后 Sync 被拒。

### 3.2 新增两个确定性反例

使用既有 `BeforeDispatchClaim` 测试 seam + TCS，不 sleep、不重试、不放大超时（仍为 5s）：

- `RecoveryDropsQueuedOldUpdateBeforeFenceAndRebaselinesCommittedState`（`:343`）
  - 先让初始 baseline 送达；arm seam 后提交一条命令，发送泵在**出队后、dispatch claim 前**暂停。
  - 此时执行 `BeginRecovery` + `CompleteRecovery`，再释放泵。
  - 断言旧 update 从未到达 sink：总帧数恰为 2（初始 baseline seq 1 + recovery baseline seq 3），recovery baseline 为 full、generation 1、stateVersion 1、最终 committed 弃牌 1 张。
  - 若生产错误地把旧 generation 帧放行，或 recovery baseline 丢失最终状态，该测试会直接失败。
- `NewBindingBeforeOldBaselineDispatchDropsOldFrameAndNewStreamContinues`（`:493`）
  - 第一个 binding 的 baseline 在 dispatch claim 前暂停；随后第二个 binding（binding epoch 2）替换旧 registration。
  - 释放泵后断言旧 sink **0 帧**，第二个 baseline 为 full、seq 2；旧 handle 的 Unsubscribe/Sync 均被拒；新流随后收到提交 update（seq 3，最终 stateVersion 1，1 张弃牌），旧 sink 仍 0 帧。
  - 若旧 baseline 穿越 replacement，或新流被旧 handle 取消/阻塞，该测试会直接失败。

### 3.3 未削弱项

- 未改 verifier、未增 skip、未放宽生产校验、未新增空通过。
- 未删除测试、未删除关键断言、未把 5s 超时随意加大。
- 没有用 `Task.Delay`/`sleep`/无变化全量重试等待绿色；所有顺序由 sink 计数、`BeforeDispatchClaim` 和 TCS 确定性控制。
- 修改仅限测试文件与本文件；`git diff --check` 无空白错误。

---

## 4. 验证环境

| 项 | 值 |
|---|---|
| OS / shell | Windows，PowerShell (`pwsh`) |
| .NET SDK | `D:/Unity/dotnet/dotnet.exe`，`8.0.426` |
| 共享库目标 | `netstandard2.1` / C# 9 |
| 测试目标 | `net8.0` |
| 配置 | `Release` |
| xUnit | `xunit.v3 4.0.2`（Microsoft Testing Platform；定向用 `-- --filter-class` / `-- --filter-method`） |

---

## 5. 实际命令、准确计数与退出码

以下均为本工作树实际执行的原始命令；日志为未提交 artifacts，路径相对工作树根。

| # | 命令 | 结果 | exit |
|---|---|---|---|
| 1 | `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal -- --filter-class "Mahjong.TableRuntime.Tests.TableStreamActorTests"`（修复前基线） | **29 passed / 0 failed / 0 skipped，总计 29** | 0 |
| 2 | 同上（修复后该类） | **31 passed / 0 failed / 0 skipped，总计 31** | 0 |
| 3 | 同上 + `--no-build -- --filter-method "Mahjong.TableRuntime.Tests.TableStreamActorTests.<方法名>"`，4 个方法各 10 次 | **40/40 次通过，0 非零退出**；每个方法 **10/10** | 0（全部） |
| 4 | `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal` | **152 passed / 0 failed / 0 skipped，总计 152** | 0 |
| 5 | 同上 + `--no-build -- --filter-class "Mahjong.TableRuntime.Tests.CodexP32bReviewProbes"` | **5 passed / 0 failed / 0 skipped** | 0 |
| 6 | 同上 + `--no-build -- --filter-class "Mahjong.TableRuntime.Tests.CodexP32bRound2ReviewProbes"` | **2 passed / 0 failed / 0 skipped** | 0 |
| 7 | 同上 + `--no-build -- --filter-class "Mahjong.TableRuntime.Tests.CodexP32bRound3ReviewProbes"` | **3 passed / 0 failed / 0 skipped** | 0 |
| 8 | `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --nologo -v:minimal --no-build` | **104 passed / 0 failed / 0 skipped** | 0 |
| 9 | `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal --no-build` | **313 passed / 0 failed / 0 skipped** | 0 |
| 10 | `D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --nologo -v:minimal` | **成功，0 warning / 0 error，12 个项目** | 0 |

计数对账：原始 TableRuntime 为 **149 pass + 1 fail = 150**；新增 2 个确定性反例后为 **152**，本轮全量 152/0/0。修复前的 29/0/0 是同一类在调度恰好偏向旧测试假设时的绿色运行，**不作为历史 CI 失败关闭证据**。

### 5.1 原始日志与 SHA256

日志均为本任务未提交 artifacts（`.log` 被 `.gitignore` 忽略），位于工作树根：

| 日志 | 内容 | SHA256 |
|---|---|---|
| `artifacts-p32-ci-regression-baseline-streamtests.log` | 修复前类运行 29/0/0，exit 0 | `f534b0aa2a4b6b048b96392156087489ed9a1ce9f6be0d2cccdc6d9509decd56` |
| `artifacts-p32-ci-regression-streamtests-after-gate.log` | 修复后类运行 31/0/0，exit 0 | `5c072caa02cef78f1549bd699574d448e5181159313858d48b84cb3aeb13d009` |
| `artifacts-p32-ci-regression-repeat-scheduling-final.log` | 4 方法 × 10 次 = 40/40，exit 0 | `8df1c50a326dd06f96d2b34cb8a0fc1db7ee1f36b4319517115562147ba4acd9` |
| `artifacts-p32-ci-regression-final-full-and-probes.log` | 全量 152/0/0 + P32b 探针 5/2/3 | `ffefb802bb409552521a62ac2b49abc53ef64d40220909f65199bd36bcb2d6d5` |
| `artifacts-p32-ci-regression-solution-build.log` | solution build 0 warning / 0 error，12 项目 | `b481d468ec8789f5aa8b3288241d8680b9db43192f90b8150dc687b1380dab8a` |
| `artifacts-p32-ci-regression-contracts-domain.log` | Contracts 104/0/0 + Domain 313/0/0 | `7128791c2c9a6a1b46d613196f88d9d120e01a62c836da0a5cd8c5075f0af74d` |

测试文件 SHA256（提交前）：

```text
8cfa44d8f60ca90215f234134015345343f2b7246c115f2f9a7c29d8edbb28d5  dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamActorTests.cs
```

完整清单另见工作树根 `artifacts-p32-ci-regression-sha256.txt`（未提交，包含本文件与测试文件）。

---

## 6. PASS / FAIL / BLOCKED / NOT_RUN

| 状态 | 项目 |
|---|---|
| **PASS** | 两个原失败已改为确定性“已发送/已 baseline”顺序；新增“queued old update 先 fence”与“replace 先于 old send”两个反例；4 个顺序测试受控重复 40/40；TableRuntime 全量 152/0/0；caller P32b 探针 5+2+3 全过；Contracts 104/0/0；Domain 313/0/0；solution build 12 项目 0/0；`git diff --check` 通过 |
| **FAIL** | 原始远端 CI 失败（`40020648672` 的 2 项 TR 失败；`40021452487` 的 wrapper env-restore）仍保持 FAIL/未关闭。本任务只提交本地修复与确定性测试，未触发远端 CI，不能把历史失败改写为 PASS |
| **BLOCKED** | 同线程/原 native session 的 rework 被 bridge 以“no active reviewer role is held”明确拒绝（原文见 §0.1）。这是任务来源事实，不阻塞本 follow-up 工作；本任务未尝试绕过角色 |
| **NOT_RUN** | 完整 P3.2 Wire 联合接线、P3.4 持久化/租约/冷恢复、PostgreSQL/统一 PG 入口、真实 WSS/TCP、Unity Editor/IL2CPP、Android/iOS 真机、跨物理网络、G1/G5 验收 |

边界重申：V4 仍是唯一生产；G1/G5 未通过；不得声称平台/真机/真实网络已测；不得把本轮 152/0/0 当作 caller 的 881/882 统一 gate 或 P3.2 完成。

---

## 7. 提交与复现

- 变更文件：
  - `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamActorTests.cs`
  - `doc/v5-重构/V5-P32-CI-REGRESSION-FOLLOWUP.md`
- 基线/父提交：`32f645ec9b134e9ad261a2df89437416b20b3119`
- 交付 commit：见本分支 `wt/v5-p32-ci-regression` 的 HEAD（caller 回执中给出）；仅本地提交，未 push、未建 PR、未触发远端 CI。
- 复现核心命令：

```powershell
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal --no-build -- --filter-method "Mahjong.TableRuntime.Tests.TableStreamActorTests.RecoveryDropsQueuedOldUpdateBeforeFenceAndRebaselinesCommittedState"
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal --no-build -- --filter-method "Mahjong.TableRuntime.Tests.TableStreamActorTests.NewBindingBeforeOldBaselineDispatchDropsOldFrameAndNewStreamContinues"
D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --nologo -v:minimal
```

请在独立验收时重点检查：

1. 两个原测试是否只等待 sink 确认，未放宽既有 3 帧/1 帧语义或删除断言；
2. 两个新反例是否真正在 dispatch claim 前暂停，并分别证明旧帧丢弃、新 generation full baseline 携带最终状态；
3. 旧 handle 的 Sync/Unsubscribe 拒绝与新流继续 update 是否都有断言；
4. 是否没有生产代码、verifier、skip、超时或生产校验的改动。
