# V5 P3.2b CI 调度回归修复交付（follow-up）

日期：2026-10-24

状态：**局部测试修复交付，等待 caller 独立验收；不是 G1/G5 通过，不是 P3.2 完整接线，也不是远端 CI 通过记录。**

工作树：`D:/majiang-wt/p32-ci-regression`

分支：`wt/v5-p32-ci-regression`

任务起点：`32f645ec9b134e9ad261a2df89437416b20b3119`（clean）

第 1 轮本地提交：`8e6b7ca2c824a20d8d153ce72745a6e621ac9a1b`（本文件所在分支的父提交；已接受两个原失败的“先送达”测试修复，但换绑反例的确定性被第 2 轮复核指出不足）

本文件状态：第 2 轮复核整改，追加 **pump-exit 屏障** 与 **临时 mutation 证据**；仍只处理两个已保存失败。

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
- 本轮提交文件：
  1. `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamActorTests.cs`
  2. `dotnet/src/Mahjong.TableRuntime/TableStreamRegistry.cs`
  3. `dotnet/src/Mahjong.TableRuntime/TableStreamContracts.cs`
  4. `dotnet/src/Mahjong.TableRuntime/TableActor.cs`
  5. `doc/v5-重构/V5-P32-CI-REGRESSION-FOLLOWUP.md`（本文件）
- 生产代码变化仅限 **internal test-only observation seam**：`ChannelCreated`、`ObservePumpExit`、`PumpExit`。没有改变 `fence`、`Close`、`TryClaimDispatch`、`RebaselineStreams` 的认领/发布语义；生产默认不启用 observation，也不分配对应 TCS。
- 未改 verifier、测试跳过策略、超时参数、生产校验或既有交付文档；未删除测试；未增加 skip；未增加 `sleep`/盲重试。
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
- `TableStreamChannel.PurgeAndFence` 更新 `_allowedGeneration` 并清空队列：`TableStreamRegistry.cs` 的 `PurgeAndFence`（当前 `:265`）。
- `TryEnqueue` 对代次不匹配的帧直接拒绝：`TableStreamRegistry.cs` 的 `TryEnqueue`（当前 `:184`）。
- 发送泵出队后、调用 sink 前，`TryClaimDispatch` 在 channel 锁内对**当前** fence 做一次原子认领；fence/close 先发生则丢弃，认领先发生则该次 sink 调用不可撤回：`TableStreamRegistry.cs` 的 `TryClaimDispatch`（当前 `:449`）与 `PumpLoopAsync`。
- 新 binding 在同一个 actor turn 内先为新 channel 入队 full baseline，再移除并关闭旧 registration（`Displaced`）：`TableActor.cs:3720-3744`。因此旧 channel 已排队但尚未被 sink 认领的 baseline/update 可以合法地一帧都不发送。
- 恢复完成时对仍有效的流排入新的 full baseline，并置 `NeedsSync`，宿主显式 Sync 前不发增量：`TableActor.cs` 的 `RebaselineStreams`（当前 `:4835`）。
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

因此本任务不改变生产 fence/close/认领/恢复语义，也不修改生产缺陷路径；只修测试到与设计一致，并增加 internal test-only observation seam 与确定性反例覆盖。

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
- `NewBindingReplacesOldHandleAndOldHandleCannotCancelOrSyncNewStream`（当前 `:449`）
  - 在第二次 subscribe 前先等待第一个 sink 收到 1 帧 baseline（seq 1），再绑定 binding epoch 2。
  - 断言第二个 baseline 为 full、seq 2；旧 sink 仍只有 1 帧。
  - 保留旧 handle 不能 Unsubscribe/Sync、旧 binding/sync 被 `STALE_STREAM_VERSION` 拒绝的断言。
  - 新增：第二流 baseline 后 Sync 为 `InSync`；新流在旧 handle 被拒后仍能接收提交后的 update（seq 3，最终 stateVersion 1，seat 0 1 张弃牌），update 后 Sync 仍为 `InSync`；旧 sink 仍为 1 帧；新流 Unsubscribe 后 Sync 被拒。

### 3.2 新增两个确定性反例（pump-exit 屏障）

第 2 轮复核指出：仅 release 后等待 `secondSink` 帧、`Displaced` 通知和 actor 操作，不能证明旧 pump 已越过 `TryClaimDispatch` 或退出；`Displaced` 是 `Close` 同步发出的，不代表旧泵已运行。为此增加最小 internal test-only seam：

- `TableStreamOptions.ChannelCreated`：测试捕获创建的 channel；
- `TableStreamOptions.ObservePumpExit`：测试开启后，每次 pump invocation 分配一个 completion；
- `TableStreamChannel.PumpExit`：当前 pump invocation 的 Task；`PumpAsync` 在 `PumpLoopAsync` 返回后的 `finally` 完成它。泵返回前已经过该 invocation 的最后一次 `TryClaimDispatch`，因此 `await PumpExit` 构成“旧泵已处理/丢弃其在途帧并退出”的屏障。生产默认关闭，不分配该状态，也不改变 `fence`/`Close`/认领语义。

两个反例：

- `RecoveryDropsQueuedOldUpdateBeforeFenceAndRebaselinesCommittedState`（当前 `:343`）
  - 先让初始 baseline 送达；arm seam 后提交一条命令，发送泵在**出队后、dispatch claim 前**暂停。
  - 捕获 `channel.PumpExit` 并断言尚未完成；执行 `BeginRecovery` + `CompleteRecovery`，再 release。
  - **先 await `PumpExit`**，确认旧泵已越过 claim 并退出；再断言 sink 总帧数恰为 2（初始 baseline seq 1 + recovery baseline seq 3），recovery baseline 为 full、generation 1、stateVersion 1、最终 committed 弃牌 1 张。
  - 若生产放行旧 generation，旧 update 会成为 `frames[1]` 并使 `StreamAssert.Baseline` 失败；若最终 baseline 丢失，则 2 帧等待超时。
- `NewBindingBeforeOldBaselineDispatchDropsOldFrameAndNewStreamContinues`（当前 `:509`）
  - 第一个 binding 的 baseline 在 dispatch claim 前暂停；捕获 `oldChannel.PumpExit` 并断言尚未完成。
  - 第二个 binding（binding epoch 2）替换旧 registration；release 后 **先 await `oldPumpExit`**，确认旧泵已越过 claim 并退出，然后才断言旧 sink **0 帧**。
  - 随后等待第二个 baseline（full、seq 2），断言新流 Sync 为 `InSync`；旧 handle 的 Unsubscribe/Sync 均被拒；新流收到提交 update（seq 3，最终 stateVersion 1，1 张弃牌）并再次 Sync 为 `InSync`；旧 sink 仍 0 帧。
  - 若旧 baseline 穿越 replacement，`oldPumpExit` 完成后旧 sink 为 1 帧，测试稳定失败；若新流被旧 handle 取消/阻塞，Sync 或 update 断言失败。

### 3.3 临时 mutation 证据（已恢复，不提交）

为证明反例不是空通过，做了 3 次临时 mutation，每次只跑定向测试，随后从备份恢复并校验 `TableStreamRegistry.cs`/`TableActor.cs` 的 SHA256 与 mutation 前完全一致：

| mutation | 临时改法 | 定向测试 | 实际失败 | exit | 日志 |
|---|---|---|---|---|---|
| A | `TryClaimDispatch` 去掉 `_closed` 检查 | `NewBindingBefore...` | `Expected: 0 / Actual: 1`（旧 sink 收到已关闭 binding 的帧） | 1 | `artifacts-codex-p32-mutation-closed-frame.log` |
| B | `TryClaimDispatch` 忽略 recovery generation | `RecoveryDrops...` | `The frame must be an explicit full baseline`（旧 update 成为 `frames[1]`） | 1 | `artifacts-codex-p32-mutation-old-generation.log` |
| C | `TablePublicationKind.Recovered` 不调用 `RebaselineStreams` | `RecoveryDrops...` | `Expected 2 stream frames but observed 1`（最终 baseline 丢失，5s 超时） | 1 | `artifacts-codex-p32-mutation-missing-baseline.log` |

恢复后的生产 SHA256：

```text
e2cf43e7853439703d97df8ff66f559770715c4f638ccb5e5702f9c0e9707c5c  dotnet/src/Mahjong.TableRuntime/TableStreamRegistry.cs
a788be427007973524e8eeb8c73775fe400ae7ef1edc77343bfa37db3d3eff60  dotnet/src/Mahjong.TableRuntime/TableActor.cs
```

注意：从备份恢复的源文件时间戳可能早于 mutation 构建出的 DLL，普通增量构建会复用旧 DLL。本轮恢复后首次全量曾因此出现 10 个 recovery baseline 超时的假象；执行 `dotnet build ... -t:Rebuild` 强制重建后全量恢复 **152/0/0**。最终验证在强制重建后执行；caller 原命令（无 `--no-build`）另有一次 152/0/0 记录。

### 3.4 未削弱项

- 未改 verifier、未增 skip、未放宽生产校验、未新增空通过。
- 未删除测试、未删除关键断言、未把 5s 超时随意加大。
- 没有用 `Task.Delay`/`sleep`/无变化全量重试等待绿色；所有顺序由 sink 计数、`BeforeDispatchClaim`、`PumpExit` 和 TCS 确定性控制。
- 生产代码改动仅为 internal observation seam；`TryClaimDispatch`、`Close`、`PurgeAndFence`、`RebaselineStreams` 的行为未改。修改范围：测试文件、3 个 TableRuntime 源文件、本文件；`git diff --check` 无空白错误。

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
| 1 | `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal -- --filter-class "Mahjong.TableRuntime.Tests.TableStreamActorTests"`（第 1 轮修复前基线） | **29 passed / 0 failed / 0 skipped，总计 29** | 0 |
| 2 | 同上（第 2 轮屏障版类运行） | **31 passed / 0 failed / 0 skipped，总计 31** | 0 |
| 3 | 同上 + `--no-build -- --filter-method "Mahjong.TableRuntime.Tests.TableStreamActorTests.<方法名>"`，4 个方法各 10 次（屏障版，最终重建后） | **40/40 次通过，0 非零退出**；每个方法 **10/10** | 0（全部） |
| 4 | `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal`（caller 给出的原命令；强制重建后的最终二进制） | **152 passed / 0 failed / 0 skipped，总计 152** | 0 |
| 5 | 同上 + `--no-build -- --filter-class "Mahjong.TableRuntime.Tests.CodexP32bReviewProbes"` | **5 passed / 0 failed / 0 skipped** | 0 |
| 6 | 同上 + `--no-build -- --filter-class "Mahjong.TableRuntime.Tests.CodexP32bRound2ReviewProbes"` | **2 passed / 0 failed / 0 skipped** | 0 |
| 7 | 同上 + `--no-build -- --filter-class "Mahjong.TableRuntime.Tests.CodexP32bRound3ReviewProbes"` | **3 passed / 0 failed / 0 skipped** | 0 |
| 8 | `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --nologo -v:minimal --no-build` | **104 passed / 0 failed / 0 skipped** | 0 |
| 9 | `D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --nologo -v:minimal --no-build` | **313 passed / 0 failed / 0 skipped** | 0 |
| 10 | `D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release -t:Rebuild --nologo -v:minimal`（mutation 恢复后强制重建） | **成功，0 warning / 0 error** | 0 |
| 11 | `D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --nologo -v:minimal` | **成功，0 warning / 0 error，12 个项目** | 0 |
| 12 | mutation A：`TryClaimDispatch` 去掉 `_closed` 检查；`NewBindingBefore...` 连跑 3 次 | **3/3 失败**（`Expected: 0 / Actual: 1`） | 1（每次） |
| 13 | mutation B：`TryClaimDispatch` 忽略 generation；`RecoveryDrops...` | **1/1 失败**（旧 update 被放行，`frames[1]` 非 baseline） | 1 |
| 14 | mutation C：`Recovered` 不调用 `RebaselineStreams`；`RecoveryDrops...` | **1/1 失败**（`Expected 2 stream frames but observed 1`，5s 超时） | 1 |

计数对账：原始 TableRuntime 为 **149 pass + 1 fail = 150**；新增 2 个确定性反例后为 **152**，本轮全量 152/0/0。修复前的 29/0/0 是同一类在调度恰好偏向旧测试假设时的绿色运行，**不作为历史 CI 失败关闭证据**。

### 5.1 原始日志与 SHA256

日志均为本任务未提交 artifacts（`.log` 被 `.gitignore` 忽略），位于工作树根；mutation 日志故意保留失败结果：

| 日志 | 内容 | SHA256 |
|---|---|---|
| `artifacts-p32-ci-regression-baseline-streamtests.log` | 第 1 轮修复前类运行 29/0/0，exit 0 | `f534b0aa2a4b6b048b96392156087489ed9a1ce9f6be0d2cccdc6d9509decd56` |
| `artifacts-codex-p32-review-streamtests-barrier.log` | 第 2 轮屏障版类运行 31/0/0，exit 0 | `a678ff2835460fdf8f0bb2e28299ea2d300bc04148f52862bf6b7953f62be321` |
| `artifacts-codex-p32-review-repeat-barrier.log` | 屏障版 4 方法 × 10 次 = 40/40，exit 0 | `da0ac2ccdd94e6eb11723b0d40afdcec46b8d923c13ffacfbdaae870cf1d102d` |
| `artifacts-codex-p32-review-tableruntime.log` | caller 原命令（无 `--no-build`）全量 152/0/0，exit 0 | `0a04a4301114b25ffec7f0f776ff4bfa6304889310113a7193fa91c9c47bc875` |
| `artifacts-codex-p32-review-p32b-probes.log` | P32b 探针 5/2/3，全部 exit 0 | `70ab6889e423bbf45c03b4e17f5f3783ac1f8fb2ff4b294967d850fa3e2d4bbf` |
| `artifacts-codex-p32-review-rebuild-after-mutation.log` | mutation 恢复后 `-t:Rebuild`，0 warning / 0 error | `8e5dde5331f0ed388928fdce808e138c65fff27adcecd409f049788801c2d68e` |
| `artifacts-codex-p32-review-solution-build.log` | solution build 0 warning / 0 error，12 项目 | `1d0d37bb30b294d25c7799799f3f4ec337b9227a1289854f58092e7896278d10` |
| `artifacts-codex-p32-review-contracts-domain.log` | Contracts 104/0/0 + Domain 313/0/0 | `9de9768898a1140c896020648214b18d9f176a545c028926361592db89060222` |
| `artifacts-codex-p32-mutation-closed-frame.log` | mutation A，3/3 稳定失败，exit 1 | `0e0d45640833836d6e0cb650bd6146c56815ff4755951c9b5e4e4e0dcfab28e2` |
| `artifacts-codex-p32-mutation-old-generation.log` | mutation B，1/1 失败，exit 1 | `9312a60ca5c810b6602b1b25294c1639a9328c2d9ac74f570c293c1d1f6b667d` |
| `artifacts-codex-p32-mutation-missing-baseline.log` | mutation C，1/1 失败，exit 1 | `672aebac5253be79844a832a2697374dcad7f795ec913063997d09ca4332a601` |

代码 SHA256（提交前）：

```text
c37f89ce5a663000fb75b508bb1d1f6d905523251cf3d1bd81cb799f3a52121f  dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamActorTests.cs
e2cf43e7853439703d97df8ff66f559770715c4f638ccb5e5702f9c0e9707c5c  dotnet/src/Mahjong.TableRuntime/TableStreamRegistry.cs
29d58b7567c4816d8d1d4a6e711a2d0c6c303e4db2751d1c7efd4227f213e245  dotnet/src/Mahjong.TableRuntime/TableStreamContracts.cs
a788be427007973524e8eeb8c73775fe400ae7ef1edc77343bfa37db3d3eff60  dotnet/src/Mahjong.TableRuntime/TableActor.cs
```

完整清单另见工作树根 `artifacts-codex-p32-review-sha256.txt`（未提交，包含本文件、代码与全部日志）。

---

## 6. PASS / FAIL / BLOCKED / NOT_RUN

| 状态 | 项目 |
|---|---|
| **PASS** | 两个原失败已改为确定性“已发送/已 baseline”顺序；新增“queued old update 先 fence”与“replace 先于 old send”两个反例；两个反例均以 `PumpExit` 在 claim 后退出作为屏障，旧 sink 断言发生在屏障之后；4 个顺序测试受控重复 40/40；TableRuntime 全量 152/0/0；caller P32b 探针 5+2+3 全过；Contracts 104/0/0；Domain 313/0/0；solution build 12 项目 0/0；mutation A/B/C 分别稳定失败后已恢复；`git diff --check` 通过 |
| **FAIL** | 原始远端 CI 失败（`40020648672` 的 2 项 TR 失败；`40021452487` 的 wrapper env-restore）仍保持 FAIL/未关闭。本任务只提交本地修复与确定性测试，未触发远端 CI，不能把历史失败改写为 PASS |
| **BLOCKED** | 同线程/原 native session 的 rework 被 bridge 以“no active reviewer role is held”明确拒绝（原文见 §0.1）。这是任务来源事实，不阻塞本 follow-up 工作；本任务未尝试绕过角色 |
| **NOT_RUN** | 完整 P3.2 Wire 联合接线、P3.4 持久化/租约/冷恢复、PostgreSQL/统一 PG 入口、真实 WSS/TCP、Unity Editor/IL2CPP、Android/iOS 真机、跨物理网络、G1/G5 验收 |

边界重申：V4 仍是唯一生产；G1/G5 未通过；不得声称平台/真机/真实网络已测；不得把本轮 152/0/0 当作 caller 的 881/882 统一 gate 或 P3.2 完成。

---

## 7. 提交与复现

- 变更文件：
  - `dotnet/tests/Mahjong.TableRuntime.Tests/TableStreamActorTests.cs`
  - `dotnet/src/Mahjong.TableRuntime/TableStreamRegistry.cs`（internal `PumpExit` seam）
  - `dotnet/src/Mahjong.TableRuntime/TableStreamContracts.cs`（internal `ChannelCreated`/`ObservePumpExit` seam）
  - `dotnet/src/Mahjong.TableRuntime/TableActor.cs`（创建 channel 时传递 test seam）
  - `doc/v5-重构/V5-P32-CI-REGRESSION-FOLLOWUP.md`
- 第 1 轮提交/本轮父提交：`8e6b7ca2c824a20d8d153ce72745a6e621ac9a1b`；原始任务起点：`32f645ec9b134e9ad261a2df89437416b20b3119`
- 本轮交付 commit：见本分支 `wt/v5-p32-ci-regression` 的 HEAD（caller 回执中给出）；仅本地提交，未 push、未建 PR、未触发远端 CI。
- 复现核心命令：

```powershell
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal --no-build -- --filter-method "Mahjong.TableRuntime.Tests.TableStreamActorTests.RecoveryDropsQueuedOldUpdateBeforeFenceAndRebaselinesCommittedState"
D:/Unity/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --nologo -v:minimal --no-build -- --filter-method "Mahjong.TableRuntime.Tests.TableStreamActorTests.NewBindingBeforeOldBaselineDispatchDropsOldFrameAndNewStreamContinues"
D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --nologo -v:minimal
```

请在独立验收时重点检查：

1. 两个原测试是否只等待 sink 确认，未放宽既有 3 帧/1 帧语义或删除断言；
2. 两个新反例是否真正在 dispatch claim 前暂停，并在 `await PumpExit` 之后才断言旧 sink 无帧；
3. `PumpExit` 是否由 `PumpAsync` 的 `finally` 在 `PumpLoopAsync` 返回后完成，生产默认不启用 observation、不改变认领语义；
4. 旧 handle 的 Sync/Unsubscribe 拒绝、新流 Sync 有效与继续 update 是否都有断言；
5. mutation A/B/C 是否分别证明“放行已关闭旧 binding 帧”“放行旧 generation 帧”“丢最终 baseline”都会使反例失败，且恢复后的源文件哈希与 mutation 前一致；
6. 是否没有 verifier、skip、超时或生产校验的改动。
