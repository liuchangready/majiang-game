# V5 PR20 e22e52a CI 失败独立诊断（2026-10-30）

范围：只读诊断 PR20 精确 head `e22e52adc44076418e770e0a8cb9b9f1916089fa` 的
[失败 run 40932995962](https://github.com/liuchangready/majiang-game/actions/runs/40932995962)
与
[成功 run 40932986363](https://github.com/liuchangready/majiang-game/actions/runs/40932986363)。
仓库 `liuchangready/majiang-game`，PR [#20](https://github.com/liuchangready/majiang-game/pull/20)
（Draft，base `v5`）。本轮未触发、未重跑、未取消任何 CI；未 push、未建/改/合并 PR；未改动
生产代码、verifier、Unity 资产或其他工作树；未申请 gate 批准。修正说明：本版仅修正归因
口径与交付文件名（原 `V5-PR20-E22-CI-FAILURE-DIAGNOSIS-20261030.md` 改为本文件），
artifact、计数与探针日志未变。

**结论：** 两个 run 的实际测试树**完全相同**（tree `0f20e8a6...`），但 PR run 在
`Mahjong.LocalHost.Tests` 的 1 个 furiten 冷重启用例上失败，push run 同一用例通过。
失败用例是
`LocalHostCandidateAcceptanceTests.ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw`，
断言 `LocalHostCandidateAcceptanceTests.cs:1063`，`Assert.True()` 实得 `False`；该程序集
**115 / 1 failed / 0 skipped**，整个 job **2,641 / 1 failed / 0 skipped**。现有证据可以
明确排除的是 PR merge 与 push 的**源码树差异**：两者 tree 全等。e22 未直接修改 LocalHost，
SDK/构建/PG 阶段也通过，但这些观察不足以证明 e22 改动、SDK、构建或 PG 与本失败无关；
同树一次 push 通过也不能排除间接影响或其他确定性条件。独立受控探针已复现同一断言：
pass 后让本地自动驱动继续推进 750 ms，状态推进到 `state=7 furiten=False`，断言失败；同一
延迟下先停驱动，则 `state=5 furiten=True`，断言通过。故**失败机制（测试在自动驱动仍可
推进时读取持久化 furiten）已由探针证实**；**CI 当次精确调度交错仍是假设**，因为 artifact
不含失败时的 save 文件或状态版本。

**V4 仍是生产版本；G2–G5 未批准；本报告不构成 PR20 批次、G2/G3 或任何 gate 的通过。**

---

## 1. Run、实际 checkout 与时间线

| 项 | 失败 run | 成功 run |
|---|---|---|
| Run ID / attempt | `40932995962` / 1 | `40932986363` / 1 |
| URL | https://github.com/liuchangready/majiang-game/actions/runs/40932995962 | https://github.com/liuchangready/majiang-game/actions/runs/40932986363 |
| Workflow | `v5-verify`（ID 394054164） | 同左 |
| Event | `pull_request` | `push` |
| API `headBranch` / `headSha` | `feat/v5-post-g1-batch` / `e22e52a...` | `feat/v5-post-g1-batch` / `e22e52a...` |
| 实际 `GITHUB_SHA` / git HEAD | `f87ce9d73795317bf463ed19ab2c8ff7bad6229e` | `e22e52adc44076418e770e0a8cb9b9f1916089fa` |
| 实际 checkout ref | `refs/pull/20/merge` | `refs/heads/feat/v5-post-g1-batch` |
| 实际 tree | `0f20e8a68a44edafaaeb4dc5cdf5c0cd6965c501` | `0f20e8a68a44edafaaeb4dc5cdf5c0cd6965c501` |
| Job ID | `123656076066` | `123656041468` |
| Created（UTC） | `2026-10-29T18:40:19Z` | `2026-10-29T18:40:15Z` |
| Job started / completed（UTC） | `18:40:21Z` / `19:01:54Z` | `18:40:17Z` / `19:01:38Z` |
| Step 5 `Run unified V5 verification` | `18:40:47Z` → **failure** `19:01:48Z` | `18:40:46Z` → success `19:01:33Z` |
| 结论 | **failure** | **success** |
| Artifact | `v5-verify-logs-40932995962-1`，ID `12924478527`，4,478,082 bytes | `v5-verify-logs-40932986363-1`，ID `12925126810`，4,477,530 bytes |
| Artifact SHA256（GitHub digest，已本地逐字节核对） | `59982F2EDE555D54BC6AD0007AD8A5346FF25B74C12FC0F93E0E4A06A69940FC` | `E1EFBB925080E359005E6565D2F7CAEC0F59F5AAE9C8FDF3AFED14A8E6254CE1` |
| Artifact created（UTC） | `19:01:50Z` | `19:01:34Z` |

UTC+8（本机/报告时区）对应：失败 run 创建 `2026-10-30 02:40:19`，完成 `03:01:54`；
成功 run 创建 `02:40:15`，完成 `03:01:39`。

### 1.1 PR 合成 merge 与 tree 全等证明

失败 run 的 Checkout 原始日志：

```text
git fetch ... +f87ce9d73795317bf463ed19ab2c8ff7bad6229e:refs/remotes/pull/20/merge
HEAD is now at f87ce9d Merge e22e52adc44076418e770e0a8cb9b9f1916089fa into 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
```

`f87ce9d` 是 GitHub 生成的 PR merge commit，父提交为
`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` 与 `e22e52adc44076418e770e0a8cb9b9f1916089fa`；
`git merge-base e22e52a f87ce9d` 返回 `e22e52a`。独立核对：

```text
git show -s --format=%T e22e52adc44076418e770e0a8cb9b9f1916089fa -> 0f20e8a68a44edafaaeb4dc5cdf5c0cd6965c501
git show -s --format=%T f87ce9d73795317bf463ed19ab2c8ff7bad6229e -> 0f20e8a68a44edafaaeb4dc5cdf5c0cd6965c501
git diff --quiet e22e52adc44076418e770e0a8cb9b9f1916089fa f87ce9d73795317bf463ed19ab2c8ff7bad6229e -> exit 0
```

实际测试的 LocalHost 测试文件 blob 也相同：

```text
e22e52a:dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs -> d5486eaebb9a6db096130f8ae05ddc08adb3d8e5
f87ce9d:dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs -> d5486eaebb9a6db096130f8ae05ddc08adb3d8e5
```

因此，失败的 PR run 与成功的 push run 测试的是**同一份源码树**；API 的 `head_sha=e22e52a`
不是 PR run 的实际 `GITHUB_SHA`。源码树相同只排除了 tree 差异，不排除运行时、调度、
进程/文件系统或其他条件差异。

---

## 2. 原始 artifact / 原生日志完整性

两个 run 的 artifact 均完整下载并保留原文件；各 269 个条目。artifact zip 的本地 SHA256
与 GitHub API `digest` 完全一致（见 §1 表），下载/解压 exit 0。失败 run 的 artifact 内：

- `verify-v5/unified/summary.log`（SHA256 `25ECF5F1...`）
- `verify-v5/unified/run-info.txt`（`6876CDE3...`）
- `verify-v5/unified/direct-Mahjong.LocalHost.Tests.log`（`FAD2FEF9...`）
- `verify-v5/unified/direct-xml/Mahjong.LocalHost.Tests/direct-results.xml`（`82F2D8BE...`）
- 其余 13 个测试程序集的 `direct-*.log` 与 `direct-xml/*/direct-results.xml`
- `native-exit-*` 探针原始结果、`table/` 与 `unified/` 的 PG/插件/构建日志

成功 run 的对应文件同样保留（LocalHost 日志 `F2C99E43...`，XML `41CDB6D8...`）。

**关于“原生框架日志”的口径：** 这两个 run 的 runner mode 是 `direct`（xunit.v3 /
Microsoft.Testing.Platform 可执行文件），不是 `dotnet test`。因此 artifact 中没有
`bin/.../TestResults/*.log` 这类 dotnetTest 框架日志；失败用例的身份、断言与堆栈在
`direct-Mahjong.LocalHost.Tests.log` 和 direct runner 原生 `-result-xml` 中均有记录。
这不是 artifact 缺失，也不能把“没有 TestResults 文件”写成失败身份未知。

原始证据目录（本工作树独占、ignored）：

```text
.artifacts/ci-e22-diagnosis-20261030/
  run-40932995962.json, run-40932986363.json, pr20.json
  artifact-40932995962.zip, artifact-40932986363.zip
  artifact-40932995962/, artifact-40932986363/          (269 files each)
  logs-40932995962.zip, logs-40932986363.zip
  logs-40932995962/, logs-40932986363/                  (raw per-step job logs)
  src-e22e52ad.zip, src-f87ce9d.zip                     (git archive, no merge into branch)
  src-e22e52ad/, src-f87ce9d/                           (owned ignored extracts)
  SHA256SUMS.txt, SHA256SUMS-artifact-*.txt
  counts.csv, commands-and-exits.txt
  probe/                                                (local probes; see §4)
```

`.artifacts/` 通过 `.git/info/exclude` 本地忽略，不进入任何提交；本报告是唯一被提交的产物。

---

## 3. 精确失败：测试、断言、计数

失败 run 的 `summary.log` 尾部：

```text
Mahjong.LocalHost.Tests  test  -  NO!  in-process (direct)  115  1  0  FAIL
OVERALL: FAIL  (project rows: 19 PASS, 1 FAIL, 0 BLOCKED, 0 NOT RUN; companion gate: PASS)
```

失败 run 的 `direct-Mahjong.LocalHost.Tests.log`：

```text
Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw [FAIL]
  Assert.True() Failure
  Expected: True
  Actual:   False
  Stack Trace:
    dotnet\tests\Mahjong.LocalHost.Tests\LocalHostCandidateAcceptanceTests.cs(1063,0): at ...ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw()
    dotnet\tests\Mahjong.LocalHost.Tests\LocalHostCandidateAcceptanceTests.cs(1103,0): at ...ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw()
=== TEST EXECUTION SUMMARY ===
  Mahjong.LocalHost.Tests  Total: 115, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0, Time: 13.942s
[verify-v5] native exit code: 1
```

direct runner 原生 `-result-xml` 中该 `<test>` 节点：

| 属性 | 值 |
|---|---|
| `name` | `Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw` |
| `result` | `Fail` |
| `time` | `0.25` 秒 |
| `start-rtf` | `2026-10-29T19:00:51.3212467+00:00` |
| `finish-rtf` | `2026-10-29T19:00:51.5947244+00:00` |
| `source-line` | `902`（测试方法起始；断言在 1063） |
| `exception-type` | `Xunit.Sdk.TrueException` |
| `message` | `Assert.True() Failure / Expected: True / Actual: False` |

失败点是 `LocalHostCandidateAcceptanceTests.cs:1063`：

```csharp
using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
{
    Assert.True(store.LoadTemporaryFuriten()[0]);   // 1063: actual False
    Assert.False(store.LoadRiichiFuriten()[0]);
    ...
}
```

该失败在测试方法 `finally` 关闭 session/channel 之后、冷重启之前发生；artifact 中
native exit 1 与该断言一致，没有 timeout/PG/native crash 的独立证据。

### 3.1 两 run 逐项目计数

| 测试程序集 | 失败 run Total / Failed / Skipped | 成功 run Total / Failed / Skipped |
|---|---:|---:|
| `Mahjong.TableRuntime.Tests` | 453 / 0 / 0 | 453 / 0 / 0 |
| `Mahjong.Domain.Tests` | 529 / 0 / 0 | 529 / 0 / 0 |
| `Mahjong.Ai.Tests` | 444 / 0 / 0 | 444 / 0 / 0 |
| `Mahjong.Client.Tests` | 182 / 0 / 0 | 182 / 0 / 0 |
| `Mahjong.Unity.TableModel.Tests` | 140 / 0 / 0 | 140 / 0 / 0 |
| `Mahjong.Client.Wss.Tests` | 82 / 0 / 0 | 82 / 0 / 0 |
| `Mahjong.Architecture.Tests` | 132 / 0 / 0 | 132 / 0 / 0 |
| `Mahjong.TableService.DurableHost.Tests` | 59 / 0 / 0 | 59 / 0 / 0 |
| `Mahjong.P5.4b.B1.HttpsAdmission.Tests` | 54 / 0 / 0 | 54 / 0 / 0 |
| `Mahjong.LobbyService.Tests` | 89 / 0 / 0 | 89 / 0 / 0 |
| `Mahjong.Contracts.Tests` | 150 / 0 / 0 | 150 / 0 / 0 |
| `Mahjong.Infrastructure.Tests` | 42 / 0 / 0 | 42 / 0 / 0 |
| `Mahjong.TableService.Tests` | 170 / 0 / 0 | 170 / 0 / 0 |
| `Mahjong.LocalHost.Tests` | **115 / 1 / 0** | **115 / 0 / 0** |
| **14 个测试程序集合计** | **2,641 / 1 / 0** | **2,641 / 0 / 0** |

另有 6 个 build-only helper（`Outbox.CrashWorker`、`LocalHost.CrashWorker`、
`P5.4b.B1.HttpsAdmission.Worker`、`Infrastructure.CrashWorker`、`PersistenceWorker`、
`LobbyService.PostgresProbe`），不计测试用例。`summary.log` 的 project rows 为
19 PASS / 1 FAIL（失败）与 20 PASS（成功），与上表 14 个 test runner + 6 个 helper 一致。

算术复核：`453+529+444+182+140+82+132+59+54+89+150+42+170+115 = 2,641`。PR 描述中较早的
“2576/2577、13 test assemblies”对应历史实现，不是本 e22 artifact 的计数；本报告只使用
上述原始 artifact 计数。

---

## 4. 与成功 run 的差异和受控探针

### 4.1 两 run 的唯一测试差异

成功 run 的 LocalHost 程序集：

```text
Mahjong.LocalHost.Tests  Total: 115, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 13.772s
[verify-v5] native exit code: 0
```

目标用例在同一 XML 中 `result=Pass`，`time=0.347`，`start-rtf=2026-10-29T19:00:37.5100333+00:00`，
`finish-rtf=2026-10-29T19:00:37.8576013+00:00`。其余 13 个测试程序集的 Total/Failed/Skipped
逐项相同（§3.1）；没有第二个失败、没有 skip/not-run/zero-test 差异。失败 run 中该用例
`0.250s`，成功 run `0.347s`，两者都在 0.5 秒内完成；失败 run 的用例开始时间更晚
（`19:00:51` vs `19:00:37`），与两次 suite 的调度/并行时序不同相一致，但该时间差本身
不能定位具体交错。

### 4.2 测试为什么会读到 False（机制）

`P41Harness` 默认 `AutoPlay = true`（`dotnet/src/Mahjong.LocalHost/LocalTableHost.cs:61`）。
`LocalTableRuntime` 构造时启动 `LocalTableDriver`（`LocalTableRuntime.cs:89-92`）；driver
每 15 ms tick 并推进 AI 座位（`LocalTableDriver.cs:96-143`）。测试只停止**客户端 pump**，
没有停止服务端 driver。pass 之后：

1. pass 命令把 seat 0 的 temporary furiten 写入 save（受控探针中 `state=5`，furiten=true）；
2. driver 继续让 seat 1–3 摸打，最终 seat 0 摸牌；
3. `TableActor` 在 `DrawTile` 时清除 temporary furiten（`TableActor.cs:4615-4618`）；
4. 测试随后打开新的 `LocalTableStore` 读 `LoadTemporaryFuriten()[0]`，读到的是更新后的
   false（受控探针中 `state=7`，furiten=false）。

测试在 `await LocalHostCommandTests.WithTimeoutAsync(...)`（pass receipt）到 store 读取之间
没有同步屏障，也没有停止 driver；这使“pass 后的持久化位”和“下一次摸牌后的持久化位”
之间存在可观测竞态。

### 4.3 探针 B：让 driver 推进，复现同一失败

在独立 `git archive` 解出的 `src-probe`（e22 的副本，**不是生产树**）中，在 rejected
forged Ron 之后、`pumpCts.Cancel()` 之前插入：

```csharp
await Task.Delay(750, pumpCts.Token);
Console.WriteLine("DIAG-AFTER-PASS state=" + session.PlayerSnapshot!.StateVersion +
                  " furiten=" + session.PlayerSnapshot.PrivateView.Furiten);
```

重建 exit 0，运行该单测 exit 1：

```text
DIAG-AFTER-PASS state=7 furiten=False
... [FAIL]
  Assert.True() Failure
  Expected: True
  Actual:   False
  ... LocalHostCandidateAcceptanceTests.cs(1063,0)
=== TEST EXECUTION SUMMARY ===
  Mahjong.LocalHost.Tests  Total: 1, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0, Time: 4.972s
```

日志：`probe/delay-probe.log`，SHA256 `8668CC9B...`。这与 CI 失败是**同一断言、同一文件、
同一行**。

### 4.4 探针 C：停 driver + 同一延迟，断言通过

在同一 probe 中，pass 断言之后加入
`await runtime.DriverForTest.StopAsync();`，保留同样 750 ms 延迟，并在 store 断言后提前返回。
重建 exit 0，运行 exit 0：

```text
DIAG-AFTER-PASS state=5 furiten=True
DIAG-CONTROLLED storeTemporaryFuriten=True driverStopped=True
=== TEST EXECUTION SUMMARY ===
  Mahjong.LocalHost.Tests  Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 2.032s
```

日志：`probe/controlled-probe.log`，SHA256 `7EF44E50...`。两组探针只差“driver 是否继续
推进”；store 读取代码、pass 命令、断言均相同。

### 4.5 本地基线与复现边界

- 未修改的 e22 测试单测，在本机连续 20 次：**20/20 exit 0**，每次 `Total: 1, Failed: 0`。
- 未修改的 LocalHost 全量，在本机连续 5 次：**5/5 exit 0**，每次 `Total: 115, Failed: 0`；
  本机全量约 26 s，比 CI 的 13.9 s 慢。
- 这些结果只说明本机条件下未触发该失败，**不足以证明不存在确定性代码错误**，也不足以
  证明 CI 失败已修复。它同时说明该失败对执行时序/环境敏感，但敏感性本身不等于已找到
  CI 当次的具体确定性条件。
- 独立验收输入（caller）：caller 已在 e22 归档中实际运行目标单测 **1/1 通过**，并核对
  两份 artifact SHA256 与本报告一致。该输入支持 e22 归档可复现通过，不改变 CI 失败事实，
  也不证明 CI 当次交错或排除其他确定性条件。

### 4.6 证据支持的结论与仍未证实项

**已由原始 artifact + 受控探针证实：**

1. 失败 run 是 `pull_request`，实际测试 `f87ce9d`；成功 run 是 `push`，实际测试 e22；
   两者 tree 全等，LocalHost 测试源码 blob 全等。故**源码树/PR merge 差异可明确排除**；
   源码树相同不排除运行环境、调度、进程/文件系统等差异。
2. 失败 run 的唯一失败是 LocalHost 的
   `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw`，断言在
   `LocalHostCandidateAcceptanceTests.cs:1063`，LocalHost **115/1/0**，全 job
   **2,641/1/0**，native exit 1。
3. 该测试依赖“pass 后立即读取持久化 furiten”，但 `AutoPlay` driver 在测试读取前仍可推进；
   推进到 seat 0 摸牌会按设计清除 temporary furiten。受控探针在同一行复现 False，并在
   停止 driver 后恢复 True。
4. 成功 run 的同一用例 Pass，同一 tree 的 LocalHost 为 **115/0/0**，全 job **2,641/0/0**。

**仍未证实（不得写成已证实）：**

- CI 失败 run 的**精确调度交错**：artifact 没有失败时的 save 文件、没有 `state`/furiten
  诊断输出，也没有 driver 的 tick 时序。我们只能证明“该机制可复现且与断言完全一致”，
  不能从 artifact 反推失败瞬间的 `stateVersion` 或具体 tick。
- e22 heartbeat 改动、SDK、构建、PG 等差异的**因果作用**：这些项目未直接落在失败点，
  相关阶段或同 tree run 通过，但这些只支持“未获直接证据”，不能排除间接影响或其他
  确定性条件。同树一次 push 通过只证明该树在该次 run 通过，不构成因果排除。
- 因此不能把本次 CI 失败简单归为“flaky 已修复”或“候选代码回归”；本报告只确认
  测试观测竞态这一机制，并保留精确交错的 UNKNOWN。

**可明确排除的只有源码树差异：**

- PR merge `f87ce9d` 与 e22 tree 全等（§1.1），LocalHost 测试 blob 全等；因此“PR merge
  内容与 push 源树不同”可排除。
- 这不等于排除运行环境、调度、进程/文件系统、构建产物实例或其他条件差异。

**未获直接证据支持、但不能写成“已排除”的项：**

- e22 的 heartbeat 绑定改动：候选 diff 的 11 个文件全在 `Mahjong.TableService`、
  `Mahjong.Client.Wss`、B1 测试与文档，未直接触碰 LocalHost；同 tree 的 push run 通过。
  这些只说明未获直接证据支持其作为原因，不能排除共享进程/时序/构建产物等间接影响。
- SDK/构建：两 run 均 `8.0.426`，solution build PASS（0 warning / 0 error），插件复制
  PASS（12 DLL），companion 4/4 PASS。仅说明这些阶段通过，不能排除其对时序/环境的
  间接影响。
- PostgreSQL：失败 run 的 P5.2 table/lobby PG 阶段均 PASS；无直接证据指向 PG，但不能
  据此排除其资源占用/调度影响。
- “本机 20/20、caller 1/1 通过”：只说明各自环境通过；不能据此证明失败与确定性条件
  无关，也不能证明 CI 失败已修复。

**方法/交付核对：**

- 两 run 的 failure 计数、native exit 与 XML 均来自原始 runner；本诊断未改生产测试，
  未削弱断言，未用重试/超时放宽替换失败，未重跑 CI。

---

## 5. 证据索引与 SHA256

完整清单见 `.artifacts/ci-e22-diagnosis-20261030/SHA256SUMS.txt`、`SHA256SUMS-artifact-*.txt`
与 `counts.csv`。关键项：

| 文件（相对证据目录） | SHA256 |
|---|---|
| `artifact-40932995962.zip` | `59982F2EDE555D54BC6AD0007AD8A5346FF25B74C12FC0F93E0E4A06A69940FC` |
| `artifact-40932986363.zip` | `E1EFBB925080E359005E6565D2F7CAEC0F59F5AAE9C8FDF3AFED14A8E6254CE1` |
| `logs-40932995962.zip` | `96441974E0B64E6B35370B76E1AEFC85F4BD176C80AB6A003CB9840D3BFFE199` |
| `logs-40932986363.zip` | `52D40FF2C66427C4DCEFDCB5C54D7EBEB00D3381F180B5944DE89A4D6AEFA90D` |
| 失败 `summary.log` | `25ECF5F1FC8F5783927B76AC53406F068617DB2AE98605924AE4190F0A53B12D` |
| 成功 `summary.log` | `6BFC36193D97D7F75718361EA95900808C835CBE5094AEF8DC9D822551A2A869` |
| 失败 `run-info.txt` | `6876CDE307F0200D09D2D74D6261733FAA9E1498D6CD948AFAF373738FB6B907` |
| 成功 `run-info.txt` | `7824E5B3B477287FD310624684B29E1C38A2901C428CD5A8F10ABA2A13764022` |
| 失败 `direct-Mahjong.LocalHost.Tests.log` | `FAD2FEF90C2895A55AE7D0FA8BAE7F99B13F14E25E1F78C6AD4EA2DE4980A30A` |
| 成功 `direct-Mahjong.LocalHost.Tests.log` | `F2C99E43B38CA41B4661F765314ADA62030C58050E369BE614385D163B9B5E7D` |
| 失败 LocalHost `direct-results.xml` | `82F2D8BE34EDECE3637F9F0A490BD79726C95486AAF64233C1DB537B0FF47C17` |
| 成功 LocalHost `direct-results.xml` | `41CDB6D809327407944A8C9F7E56522478F49A096E2EEF54AC9BA16FD3BCC5DD` |
| `src-e22e52ad.zip`（git archive） | `F634EAA041BE97D1AA877DD70C2F2BEE67D505EE9252B5698841D908FB629308` |
| `src-f87ce9d.zip`（PR merge archive） | `622F9CBA90EBE35A2725921F36C24AA444F9609FBCE55581994B322371DD2681` |
| `probe/delay-probe.log`（探针 B） | `8668CC9B15FF38A34DE954B16B96EEA0D695F22794316D0D630F4565FB54D602` |
| `probe/controlled-probe.log`（探针 C） | `7EF44E504F6AE8B9638F66D9A4854F92EFC067897F04DF8FED18865083B876D0` |

artifact 内每个条目的 SHA256 另见 `SHA256SUMS-artifact-40932995962.txt` /
`SHA256SUMS-artifact-40932986363.txt`（各 269 条）。

---

## 6. 复现与限制

关键命令（完整命令、退出码、环境见 `commands-and-exits.txt`）：

```powershell
# 1) 只读 run 元数据 / artifact / 原始 job logs
gh run view 40932995962 -R liuchangready/majiang-game --json ...
gh run view 40932986363 -R liuchangready/majiang-game --json ...
gh run download 40932995962 -R liuchangready/majiang-game -n v5-verify-logs-40932995962-1 -D <evidence>
gh run download 40932986363 -R liuchangready/majiang-game -n v5-verify-logs-40932986363-1 -D <evidence>
# 二进制 zip 用 curl + gh auth token 保存；本地 SHA256 与 API digest 一致
```

```powershell
# 2) 不把 PR 代码合入分支：只取 merge ref 到 FETCH_HEAD，再 git archive 到独占 ignored 目录
git fetch --no-tags origin refs/pull/20/merge
git archive --format=zip -o artifacts/.../src-f87ce9d.zip f87ce9d...
git archive --format=zip -o artifacts/.../src-e22e52ad.zip e22e52a...
git diff --quiet e22e52a f87ce9d   # exit 0
```

```powershell
# 3) 独立构建与单测（src-e22e52ad 内，不触碰生产树）
cd <evidence>/src-e22e52ad
D:\Unity\dotnet\dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo
D:\Unity\dotnet\dotnet.exe <...>/Mahjong.LocalHost.Tests.dll -noLogo -reporter default -method <failing test>
```

```powershell
# 4) 受控探针：在 src-probe 副本中加 750ms 延迟（B），再加 DriverForTest.StopAsync()（C）
#    重建后运行同一 -method；B exit 1（同 1063 断言），C exit 0（state=5 furiten=True）
```

**限制 / BLOCKED：**

- **无必需 artifact 缺失**：两个 run 的 artifact、完整 job log、summary、run-info、direct
  log、direct XML 均已取得并通过 SHA256 核对。
- **BLOCKED（精确 CI 交错）**：失败时的本地 save 文件与 `stateVersion`/driver tick 未在
  artifact 中，无法从 CI 证据直接证明“失败瞬间 seat 0 已摸牌”。本报告以受控探针证明该
  机制，但不把精确交错写成已观测。若 caller 需要该级别证明，需要下次失败时保留
  `build/p41-tests/**/room-1.save`、失败时 `stateVersion` 和 driver tick 日志；本轮没有
  为了补这个证据而重跑 CI。
- 本机探针结果受本机调度影响：20/20 单测、5/5 全量通过；这只能说明本机未触发，
  不能据此证明失败与确定性条件无关，也不能替代 CI 证据或作为“已修复”的证明。
- 未运行/未声明：Unity Editor/IL2CPP、Android/iOS 真机、跨物理网络、真实生产部署、
  G2/G3 全链路。V4 仍是生产；G2–G5 未批准。

---

## 7. 状态口径

| 项 | 状态 | 说明 |
|---|---|---|
| Run 40932995962（PR20 e22） | **FAIL** | 已观测；LocalHost 1 个断言失败 |
| Run 40932986363（push e22） | **PASS** | 已观测；同 tree，LocalHost 115/115 |
| 失败用例身份 | **已定位** | `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw`，`LocalHostCandidateAcceptanceTests.cs:1063` |
| 失败计数 | **已定位** | LocalHost 115/1/0；全 job 2,641/1/0；14 test assemblies |
| 失败机制 | **已由受控探针证实（机制可复现）** | 测试读取持久化 furiten 时 auto-play driver 仍可推进；停 driver 后同断言通过；不证明 CI 当次交错 |
| CI 精确交错 | **BLOCKED/UNKNOWN** | artifact 无失败时 save/state/tick；不以重跑替代 |
| e22 heartbeat 改动归因 | **未证实（缺乏直接证据）** | 未直接触碰 LocalHost；同 tree push PASS，但没有隔离 A/B，不能排除间接影响 |
| PR merge 内容归因 | **源码树差异明确排除** | `f87ce9d` tree = e22 tree；不排除运行环境/调度差异 |
| SDK/构建/PG 归因 | **未证实（缺乏直接证据）** | 8.0.426、build 0/0、PG stages PASS，唯一失败在 LocalHost；不能据此排除间接/其他确定性条件 |
| 本报告对 PR20 的影响 | **不批准** | PR20 仍为 Draft；不构成批次或 gate 通过 |
| V4 / G2–G5 | **不变** | V4 生产；G2–G5 未批准 |

本报告只提交这一个文档；artifacts 与探针全部留在 ignored 目录。Caller 独立复核并决定
是否接受该诊断；本报告不修改 PR、不 push、不合并、不触发 CI。
