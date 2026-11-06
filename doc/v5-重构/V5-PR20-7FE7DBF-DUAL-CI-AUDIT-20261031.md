# V5 PR20 7FE7DBF 双 CI 独立审计（2026-10-31）

- 审计对象：PR #20 的源提交 `7fe7dbf3e37aefb437132d3584cb2ff09cff6af5`，源分支 `feat/v5-post-g1-batch`，base `v5` = `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
- 审计工作树：`D:/majiang-wt/ci-7fe7-audit-ds-20261031`，分支 `wt/v5-ci-7fe7-audit-ds-20261031`，冻结 HEAD = `7fe7dbf3e37aefb437132d3584cb2ff09cff6af5`。
- 证据根目录：`build/ci-7fe-audit/`（ignored）；完整清单 `EVIDENCE-SHA256.txt`，630 entries / 103,791 bytes / SHA-256 `44CAF1016B56CED339F30472D7182D9F1858E05FFA20CA8151E18C0C5ED3104B`。
- 审计方式：只用 `gh`/GitHub API 只读获取指定两个 run/attempt/job/step/head/checkout、完整 job log、原始 artifact ZIP 与 artifact 内全部文件；在 ignored 证据树中直接解析逐例 XML、meta、summary、PG 日志、plugin provenance 与 companion meta。另在冻结源码上执行了受支持的本地 TableService `dotnet build`/`dotnet test` 探针与 20 次隐私用例执行，全部保存在 ignored 证据树。
- 未运行/未触发：未 `workflow_dispatch`、未 rerun/cancel 任何 CI，未 push 任何 ref，未建/改/合并 PR，未 `record_merge_request`，未 `approve`，未修改 scheduler/`complete.flag`，未启动 Unity/Player/IL2CPP/Android/iOS，未改共享插件或机器信任，未调用/委派其他 agent，未改其他 worktree。

> **结论边界：** 本报告是**审计输入**，只证明这两个已结束 run 的 GitHub 事件、实际 checkout、workflow/step 退出码、原始逐例 XML、PostgreSQL 灾备/恢复、companion/plugin/native-exit 证据与 artifact 完整性，并给出本批仍存在的证据口径缺口。它**不是 PR20 验收**，不是 G2/G3/G4/G5 验收，也不把本次任何门禁行写成“组件通过”或“阶段通过”。最终验收由调用方独立完成。V4 仍是唯一生产基线；G1 已批准、G2–G5 未批准；classified 181 仍未覆盖；Android 仍为 FAIL、iOS 仍为 BLOCKED、真机跨物理网仍为 NOT_RUN。

---

## 1. 结论摘要

| 项目 | push run 41181934433 | PR run 41181937456 |
|---|---|---|
| event / attempt | `push` / 1 | `pull_request` / 1 |
| API `head_sha` | `7fe7dbf3...6af5` | `7fe7dbf3...6af5` |
| 实际 checkout | `7fe7dbf3e37aefb437132d3584cb2ff09cff6af5` | `72f728e7d7e0b2161bb42f1857495f7d966fc678`（`refs/pull/20/merge`） |
| job | `124480946465` / **SUCCESS** | `124480956023` / **SUCCESS** |
| step 5 `Run unified V5 verification` | exit 0 / SUCCESS | exit 0 / SUCCESS |
| 14 个 direct 测试工程 | **2,732 / 2,732 / 0 / 0 / 0 / 0** | **2,732 / 2,732 / 0 / 0 / 0 / 0** |
| `Mahjong.TableService.Tests` | **180 / 180** | **180 / 180** |
| 表侧 PG 42 + targeted 13 | 42/42、13/13 PASS | 42/42、13/13 PASS |
| crash writer / immediate restart / fresh reader / empty-DB restore | 全部 PASS | 全部 PASS |
| companion golden | 4/4 PASS（47 cases，native exit 0） | 4/4 PASS（47 cases，native exit 0） |
| native-exit 探针 | 6/6 PASS | 6/6 PASS |
| 12 DLL / 19 plugin outputs | 12/12、19/19 存在且 hash 匹配 | 12/12、19/19 存在且 hash 匹配 |
| artifact | `v5-verify-logs-41181934433-1`，4,492,175 bytes，digest 匹配 | `v5-verify-logs-41181937456-1`，4,492,185 bytes，digest 匹配 |
| 本次首失败 | **无** | **无** |
| `dotnetTest` 逐例 XML | **NOT_RUN**（CI 明确 `-RunnerMode direct`） | **NOT_RUN** |
| Unity/Player/IL2CPP/Android/iOS/真实 HTTPS/WSS/跨网/真机 | NOT_RUN | NOT_RUN |

**一句话结论：** 两个精确 head 的 run 都已完成且 step 5 为 exit 0；14 个真实测试工程在两个 run 均为 **2,732/2,732**，TableService 为 **180/180**，表侧 PG 42/42、targeted 13/13、crash/restart/restore 均 PASS，companion 4/4 与 12 DLL/19 outputs 均成立。**但这只是两个 run 的 CI 证据，不是 PR20、组件或 G2–G5 验收。** D-A68-02（direct vs `dotnetTest` 逐例口径）与 D-A68-03（历史 whitespace）仍未关闭；ledger 批准仍受 reviewer-role 限制；PR 仍为 Draft。

---

## 2. 冻结身份、PR merge ref 与真实 checkout

### 2.1 引用与提交

| 引用 | SHA |
|---|---|
| `refs/heads/v5`（accepted base） | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| `refs/heads/feat/v5-post-g1-batch` | `7fe7dbf3e37aefb437132d3584cb2ff09cff6af5` |
| `refs/pull/20/head` | `7fe7dbf3e37aefb437132d3584cb2ff09cff6af5` |
| `refs/pull/20/merge` | `72f728e7d7e0b2161bb42f1857495f7d966fc678` |

- `git merge-base --is-ancestor 8fd7c6c 7fe7dbf` 返回 **0**；`git rev-list --count 8fd7c6c..7fe7dbf` = **384**。
- base tree：`61f50856aedd78e0f8609e282baa8313ae8f2a14`。
- head tree：`0c6b16cc067b8366754b5e5c1b80daa6ea81294f`。

### 2.2 7fe 的集成拓扑

- `7fe7dbf` 是单父提交，父为 `efe5e9e3a33430e1c3047d101302731b32be4db4`。
- `efe5e9e3` 是双父合并：`8b3bdd35...cdc` + `3e8836a2...b1d4`。
- `3e8836a2` 的 tree 为 `af2932cc224c9f84830c771e2bc2c82a28d860bd`；集成 merge `efe5e9e3` 的 tree **完全相同**，证明接收者交付树与调用方集成树一致。
- `git diff --name-status efe5e9e3..7fe7dbf` 仅新增调用方验收报告 `doc/v5-重构/CODEX-V5-RECEIVE-TIMEOUT-ACCEPTANCE-20261031.md`（54 insertions）；没有代码变更。
- `git diff --name-status 8b3bdd35..7fe7dbf` 仅 5 个文件：两份报告、`StreamPrivacyTests.cs`、`TableTestClientReceiveTimeoutTests.cs`、`TestSupport/TableTestClient.cs`；**没有 B2/P45 未提交代码或越界文件**。

### 2.3 PR 合成合并 vs source head

GitHub API commit object：

| 提交 | parents | tree | 说明 |
|---|---|---|---|
| `72f728e7d7e0b2161bb42f1857495f7d966fc678` | `8fd7c6cb...` + `7fe7dbf3...` | `0c6b16cc067b8366754b5e5c1b80daa6ea81294f` | PR 合成合并 |
| `7fe7dbf3...6af5` | `efe5e9e3...db4` | `0c6b16cc067b8366754b5e5c1b80daa6ea81294f` | source head |

**结论：** PR run 实际执行的是合成合并 `72f728e`，不是 API `head_sha` `7fe7dbf`；但两者 tree **完全相同**，所以源码内容相同、只是内嵌 checkout SHA 不同。push run 实际 checkout `7fe7dbf`。

PR Checkout 原始日志（`build/ci-7fe-audit/logs/job-pr-41181937456.log`）：

```text
git ... fetch ... +72f728e7d7e0b2161bb42f1857495f7d966fc678:refs/remotes/pull/20/merge
HEAD is now at 72f728e Merge 7fe7dbf3e37aefb437132d3584cb2ff09cff6af5 into 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
```

push Checkout 原始日志（`build/ci-7fe-audit/logs/job-push-41181934433.log`）：

```text
git ... fetch ... +7fe7dbf3e37aefb437132d3584cb2ff09cff6af5:refs/remotes/origin/feat/v5-post-g1-batch
7fe7dbf3e37aefb437132d3584cb2ff09cff6af5
```

### 2.4 产品版本差异

从两个 artifact 的 `verify-v5/unified/plugins/` 读取 12 DLL：

| DLL | push ProductVersion | PR ProductVersion | 结论 |
|---|---|---|---|
| `Mahjong.Domain.dll` | `1.0.0+7fe7dbf3...6af5` | `1.0.0+72f728e...fc678` | 同 tree，不同内嵌 checkout SHA |
| `Mahjong.Contracts.dll` | `1.0.0+7fe7dbf3...6af5` | `1.0.0+72f728e...fc678` | 同上 |
| `Mahjong.Contracts.Wire.dll` | `1.0.0+7fe7dbf3...6af5` | `1.0.0+72f728e...fc678` | 同上 |
| `Mahjong.TableRuntime.dll` | `1.0.0+7fe7dbf3...6af5` | `1.0.0+72f728e...fc678` | 同上 |
| `Mahjong.Client.dll` | `1.0.0+7fe7dbf3...6af5` | `1.0.0+72f728e...fc678` | 同上 |
| `Mahjong.LocalHost.dll` | `1.0.0+7fe7dbf3...6af5` | `1.0.0+72f728e...fc678` | 同上 |
| `Mahjong.Ai.dll` | `1.0.0+7fe7dbf3...6af5` | `1.0.0+72f728e...fc678` | 同上 |
| `Google.Protobuf.dll` / 4 个 `System.*.dll` | 包自身版本 | 同左 | 5/5 hash 两边相同 |

7 个自有 DLL 的 SHA-256 因此两边不同；5 个包 DLL 相同。这不是源码差异，而是 .NET 构建把 checkout SHA 写入 `ProductVersion` 的结果；不能用 DLL hash 直接推断 tree 不同。

PR 状态（2026-10-31）：`state=OPEN`、`isDraft=true`、`mergeable=MERGEABLE`、`mergeStateStatus=CLEAN`、head=`7fe7dbf3...`；两个 `verify` check 均为 SUCCESS。

### 2.5 调用方输入与本审计的独立部分

- 调用方输入（PR body / `CODEX-V5-RECEIVE-TIMEOUT-ACCEPTANCE-20261031.md`）：`efe5e9e3` 与接收者 tree 完全相等；fresh Release build 0 warning/0 error；TableService direct 180/180；255/255 receiver evidence 大小/hash；22/22 XML。调用方还记录 `MR_RECORDED/synced` 成功。
- 本审计独立复核了 7fe 的树/父提交/增量范围、`efe5e9e3` 与 `3e8836a2` tree 相等、本地 fresh build 0/0、本地 `dotnet test` 180/180、20 次隐私执行，以及**新的两个 CI run** 的 28 个 XML（14×2）。
- 本审计**没有**重读调用方 ignored 的 255 文件 evidence 根；255/22 仍按调用方已记录输入处理，未将其混写成新 CI 的独立产物。

---

## 3. CI 运行、步骤与 native exit

### 3.1 Workflow

- path：`.github/workflows/v5-verify.yml`；name：`v5-verify`。
- runner：`windows-latest`；push runner `GitHub Actions 1000000344`，PR runner `GitHub Actions 1000000345`，不是同一台机器。
- SDK：`actions/setup-dotnet@v4`，`dotnet-version: 8.0.426`。
- 关键 step：Checkout、Setup .NET 8 SDK、`./tools/ci/test-v5-native-exit.ps1`、`./tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -RunUnifiedVerification ...`、`if: always()` 上传 artifact。

### 3.2 逐 step 结果

| run | 关键 step | 结果 | 时间（UTC） |
|---|---|---|---|
| push 41181934433 | Checkout | success | 07:46:50–07:47:02 |
| push | Verify native failure exit propagation | success（6/6） | 07:47:06–07:47:13 |
| push | Run unified V5 verification | **success / exit 0** | 07:47:13–08:08:18 |
| push | Upload verification logs | success | 08:08:18–08:08:20 |
| PR 41181937456 | Checkout | success（`72f728e`） | 07:46:54–07:47:07 |
| PR | Verify native failure exit propagation | success（6/6） | 07:47:12–07:47:19 |
| PR | Run unified V5 verification | **success / exit 0** | 07:47:19–08:08:06 |
| PR | Upload verification logs | success | 08:08:06–08:08:09 |

- push job 总时长 **21m36s**；PR job **21m21s**。
- 两边 `P52-TABLE-POSTGRES-PASS`；artifact 均存在，upload step 不是 `if: always()` 的失败补救。
- 本次没有 rerun/attempt 2；API `run_attempt=1`。

### 3.3 native-exit 探针

两个 artifact 的 `native-exit-*/results.json` 均为 **6/6 PASS**：

```text
exit-0=0; exit-1=1; exit-2=2; exit-17=17; timeout=throw; forced-writer=-1 (nonzero, forced)
```

`writer.log.kill.json` 记录 `forced=true` 与 `exitCode=-1`；这是预期强制终止，不是 unified 失败。

### 3.4 历史 run 不得替代

| 历史 head | push | PR | 本报告用途 |
|---|---|---|---|
| `8b3bdd35...cdc` | 41174114270 success | 41174118274 success | 仅时序背景，不替代 7fe |
| `a68c157c...fff` | 41167846340 success | **41167851418 failure** | 原失败保留，不刷新、不删红例 |
| `7fe7dbf3...6af5` | **41181934433 success** | **41181937456 success** | 本次唯一证据 |

旧 `a68` 的首失败仍是 `Mahjong.TableService.Tests.StreamPrivacyTests.SpectatorStream_NeverCarriesPrivateData`，原始 XML SHA-256 `B5CE4904A88523FD8DC3A97436CD12AAD3D22319F9A8CE4ED14ECEEA51EE8847`；本报告不把该失败改写为成功，也不拿 8b3 双绿替代它。

---

## 4. 逐工程真实 XML、唯一用例与两 run 差异

### 4.1 实际是 14 个 direct 测试工程

两个 artifact 各有 14 个 `verify-v5/unified/direct-xml/<project>/direct-results.xml`，与 14 个 `runs/<project>/direct.meta.txt` 一一对应；另有 6 个 build-only helper（不计测试）。`summary.log` 的 **20 project rows = 14 test + 6 helper**，不能把 20 行当成 20 个测试工程。

### 4.2 逐工程计数（两 run 完全相同）

顺序：`total / passed / failed / skipped / notRun / errors`。

| 工程 | push 41181934433 | PR 41181937456 | 唯一用例名 |
|---|---|---|---|
| `Mahjong.Ai.Tests` | 444 / 444 / 0 / 0 / 0 / 0 | 444 / 444 / 0 / 0 / 0 / 0 | 444 |
| `Mahjong.Architecture.Tests` | 132 / 132 / 0 / 0 / 0 / 0 | 132 / 132 / 0 / 0 / 0 / 0 | 131 |
| `Mahjong.Client.Tests` | 182 / 182 / 0 / 0 / 0 / 0 | 182 / 182 / 0 / 0 / 0 / 0 | 182 |
| `Mahjong.Client.Wss.Tests` | 82 / 82 / 0 / 0 / 0 / 0 | 82 / 82 / 0 / 0 / 0 / 0 | 82 |
| `Mahjong.Contracts.Tests` | 152 / 152 / 0 / 0 / 0 / 0 | 152 / 152 / 0 / 0 / 0 / 0 | 152 |
| `Mahjong.Domain.Tests` | 529 / 529 / 0 / 0 / 0 / 0 | 529 / 529 / 0 / 0 / 0 / 0 | 529 |
| `Mahjong.Infrastructure.Tests` | 42 / 42 / 0 / 0 / 0 / 0 | 42 / 42 / 0 / 0 / 0 / 0 | 42 |
| `Mahjong.LobbyService.Tests` | 89 / 89 / 0 / 0 / 0 / 0 | 89 / 89 / 0 / 0 / 0 / 0 | 89 |
| `Mahjong.LocalHost.Tests` | 124 / 124 / 0 / 0 / 0 / 0 | 124 / 124 / 0 / 0 / 0 / 0 | 124 |
| `Mahjong.P5.4b.B1.HttpsAdmission.Tests` | 54 / 54 / 0 / 0 / 0 / 0 | 54 / 54 / 0 / 0 / 0 / 0 | 53 |
| `Mahjong.TableRuntime.Tests` | 476 / 476 / 0 / 0 / 0 / 0 | 476 / 476 / 0 / 0 / 0 / 0 | 476 |
| `Mahjong.TableService.DurableHost.Tests` | 59 / 59 / 0 / 0 / 0 / 0 | 59 / 59 / 0 / 0 / 0 / 0 | 59 |
| `Mahjong.TableService.Tests` | **180 / 180 / 0 / 0 / 0 / 0** | **180 / 180 / 0 / 0 / 0 / 0** | 180 |
| `Mahjong.Unity.TableModel.Tests` | 187 / 187 / 0 / 0 / 0 / 0 | 187 / 187 / 0 / 0 / 0 / 0 | 187 |
| **合计** | **2,732 / 2,732 / 0 / 0 / 0 / 0** | **2,732 / 2,732 / 0 / 0 / 0 / 0** | **2,730（project-qualified）** |

- 14 个 XML 的 assembly `total/passed/failed/skipped/not-run/errors` 与 `test` 节点计数、`direct.meta.txt` 的 `total/failed/skipped/exit code`、`direct-<project>.log` 的 `Total/Errors/Failed/Skipped/Not Run` **28/28 组全部一致**；见 `build/ci-7fe-audit/parsed/meta-log-xml-crosscheck.csv`。
- 每个工程 `total > 0`、`nodeCount = total`；没有 `Total=0`、skip、notRun、errors。
- 两 run 的 project 集合与用例名集合 **0 差异**；`projectSetDiffs=[]`。
- 全局 2,732 个 case 中，`project::name` 唯一数为 **2,730**。两处同名只是 xUnit 展示名重复：`Mahjong.Architecture.Tests`（132 节点 / 131 唯一名）与 `Mahjong.P5.4b.B1.HttpsAdmission.Tests`（54 / 53）；其余 12 个工程唯一名数等于节点数。
- framework 全部 `xUnit.net v3 4.0.2+0c7aa4991f`，target `.NETCoreApp,Version=v8.0`，assembly `errors=0`。
- 每个 meta 都记录 invocation 前清理 `TestResults`，且结果 XML 在本次 run 的 `direct-xml/` 下；没有旧 DLL/旧 XML 复用证据。

### 4.3 TableService 180 与 9 个新 helper 回归

- `TableService.Tests` 180 nodes / 18 classes；原 171 + 新增 `TableTestClientReceiveTimeoutTests` 9 = 180。
- 9 个新回归在两个 run 均 Pass：`TimeoutLeavesRealSocketOpen_AndLateFrameIsReadableOnce`、`RepeatedTimeouts_KeepOneUnderlyingReceive`、`FragmentAcrossTimeouts_StaysCompleteAndDecodesOnce`、`ReadUntilTimeout_KeepsReplayOrderAndPendingFrame`、`WatermarkAndBaseline_SurviveTimeoutsAndReplay`、`CloseAndTransportFault_PropagateUnchanged`、`OversizeAndUndecodableFrames_StillFail`、`Dispose_ObservesParkedReceiveWithoutBackgroundLeak`、`ConcurrentWaits_NeverIssueConcurrentReceive`。
- `StreamPrivacyTests` 3/3 Pass，包括 `SpectatorStream_NeverCarriesPrivateData`。
- push XML 108,969 bytes / SHA-256 `CDFB9D77D297F56BAA0868C3A96E61973555946A09404B0B9FE999FCEA8B41DC`；PR XML 108,974 bytes / SHA-256 `9D7AA7FB425A56F403233671820C75B9940FF9C59BB2018AE6D1FD0675D3E0F7`。

### 4.4 direct 与 dotnetTest 的口径

- 两个 `run-info.txt` 均为 `runner mode: direct`；14 个 meta 均为 `runner used: in-process (direct)`，命令是 `dotnet.exe <test.dll> ... -result-xml ...`。
- artifact 中没有 `.trx`、没有 `dotnetTest-*` 日志，也没有 `dotnet test` 命令。
- 因此 `direct-results.xml` 只能叫 **direct 原生逐例 XML**，不能叫 dotnetTest 逐例 XML。D-A68-02 仍开放。

---

## 5. D-A68-01 / D-A68-06：读超时 helper

### 5.1 代码状态

冻结 7fe 的 helper 修复（`dotnet/tests/Mahjong.TableService.Tests/TestSupport/TableTestClient.cs`）：

- L361 `ReceiveAsync`、L393 `AcquireReadGateAsync`、L416 `ReceiveCoreAsync`、L470 `ReceiveOneMessageAsync`、L666 `DisposeAsync`。
- 单一 pending complete-message receive；超时只取消当前等待（`Task.WaitAsync`），不取消 socket 级 receive；`_readGate` 串行化消费者；`DisposeAsync` 取消 lifetime、Abort/Dispose socket 并观察 pending。
- `StreamPrivacyTests.cs` L80 目标用例，L130 断言 timeout 后 `WebSocketState.Open`；隐私断言保留（`PublicUpdate`、`View != null`、无 `PlayerUpdate`）。

### 5.2 本次实测

| 检查 | 结果 | 证据 |
|---|---|---|
| 两 run `TableService.Tests` | 180/180，0 fail/skip/notRun/errors | 两个 `direct-results.xml` |
| 两 run `StreamPrivacyTests` | 3/3 Pass | XML |
| 两 run 9 个新 helper 回归 | 9/9 Pass | XML |
| 本地 fresh Release build | exit 0；0 warning / 0 error；32.38s | `logs/local-fresh-release-build.log`，SHA-256 `3808BD754...7E92F` |
| 本地 `dotnet test` TableService | exit 0；180/180；1m38.977s | `logs/local-table-dotnet-test.log`，SHA-256 `40687FF...FCD2` |
| 本地隐私用例 20 轮 | 每轮 exit 0；每轮 1/1 Pass；0 skip/notRun/errors | `privacy-20/round-01..20.xml`、`privacy-20-summary.csv`，SHA-256 `EC950F...382F` |

- 本地 `dotnet test` 只产生 framework console/log（`logs/local-dotnet-test-framework.log`，550,448 bytes，SHA-256 `8B0A60...F914`），**没有**逐例 XML/TRX；这正说明 D-A68-02 的缺口。
- 20 轮隐私执行是**本地 direct 探针**，不是 CI 产物；CI 每个 run 只跑 1 次该用例。
- 服务端 teardown 日志在测试完成后可能出现 `state=Aborted`/`ConnectionClosedPrematurely`；这不是本次 client read-after-abort 失败，判断以逐例 XML 的 `Pass` 和测试内 `Open` 断言为准。
- **边界：** 本结果支持“在这个 helper、这两个 run 与 20 次本地执行范围内没有复现 D-A68-01”，**不能**写成“WebSocket 超时 race 永不存在”。D-A68-01/同因 D-A68-06 在本组件范围标记 CLOSED；不改变全产品/其他 transport 的风险判断。

---

## 6. PostgreSQL 42/13、crash/restart/restore、双栅栏与 outbox

### 6.1 表侧 stage

PR artifact `verify-v5/table/wrapper-summary.json`：13 stages，`exit=0`，`environmentRestored=true`，`environment` 16 项全部 `restored=true`，`lines` 以 `ownership-verified cluster removed` 结束。

| stage | 结果 | 关键证据 |
|---|---|---|
| `port-preflight` | PASS exit 0 | 127.0.0.1:55592 Bindable |
| `pg-start` | PASS exit 0 | `pg_ctl` native exit 0 |
| `cluster` | PASS exit 0 | PostgreSQL 16.10，port 55592，独立 data dir |
| `xml-gating-selfcheck` | PASS exit 0 | 7 个负例 XML 全部拒绝 |
| `fresh-build` | PASS exit 0 | tests + crash worker，8.0.426 |
| `tests` | PASS exit 0 | 42/42；DLL SHA `454AEE...C034` |
| `tests-targeted` | PASS exit 0 | `PostgresTableStoreTests` 13/13 |
| `crash-writer` | PASS exit **KILLED** | pid 2096；committed state=1 seq=2；killed before publication |
| `postgres-crash-restart` | PASS exit 0 | postmaster start 07:47:33.218 → 07:48:48.504；recovery lines=3 |
| `crash-reader` | PASS exit 0 | state=1 seq=2 terminal=True receipt=Applied cheatVersion=1 outbox 前缀 `c0b108...` |
| `backup-restore` | PASS exit 0 | `pg_dump`/`pg_restore` 到空库；restored events=2；recovery ownerEpoch=3 |
| `unified-verification` | PASS exit 0 | 真实 table + lobby PostgreSQL |
| `result` | PASS exit 0 | all P5.2 real-PostgreSQL stages passed |

- `tests.xml`：42 total / 42 passed / 0 failed / 0 skipped / 0 not-run / 0 errors；25,535 bytes / SHA-256 `4F5BB97A7E27E7A9B6CFDF0681992B56FC2C2A5BCC7638D0C4555E1E1CDC7B3C`。
- `tests-targeted.xml`：13/13 / 0 / 0 / 0 / 0；8,440 bytes / SHA-256 `532225DF27902581B06FD982364A0E202C741E55EE3B5E0AFF1DE54CF2EDEBFE`。
- 13 个 targeted case 全部 Pass，包括 `LeaseDualInstanceAndOldOwnerFencing`、`StaleStateFenceRejectsWithoutReceiptOrEvent`、`ConcurrentDuplicateSubmission_CommitsOnceAndReplaysTheOther`、`TerminalOutbox_IsAtomicAndConsumerIsIdempotent`、`OutboxPayloadConflict_RollsBackEventReceiptAndState`、`ActorCommit_RoundTripsRealEventsReceiptAndColdRecovery`。

### 6.2 crash writer → immediate restart → fresh reader

`crash-commit-marker.json` 记录：`stateVersion=1`、`lastEventSeq=2`、`eventCount=2`、`outboxResultVersion=1`、`outboxSha256=c0b108923986...a55`、`cheatVersion=1`；marker SHA-256 `CBFD4E29...01FF4`。

`crash-writer.log.kill.json`：`pid=2096`、`forced=true`、`exitCode=-1`、`killedUtc=2026-10-31T07:48:48.128Z`；marker SHA-256 同 `CBFD4E29...01FF4`。

`postgres-restart-recovery-lines.log`（276 bytes，SHA-256 `FCD0C9CC...C20B`）明确：

```text
database system was not properly shut down; automatic recovery in progress
redo starts at 0/1577688
database system is ready to accept connections
```

`crash-reader-result.json`（独立 reader 进程）：`READER-OK`，`stateVersion=1`、`lastEventSeq=2`、`eventCount=2`、`commandCount=1`、`receiptStatus=Applied`、`terminal=true`、`outboxPresent=true`、`outboxPayloadMatches=true`、`outboxPublished=false`、`cheatStateRestored=true`、`cheatVersion=1`、`recoveryOwnerEpoch=2`、`recovered=true`。

- `outboxPublished=false` 是 writer 在 publication 前被 kill 的预期结果；不能写成 “outbox 已发布”。这里证明的是：committed event/receipt/outbox payload 在 immediate restart 后仍可读，且 fresh reader 只读取、不重复发布。
- `LostCommitResponse_IsOutcomeUnknownAndReceiptReconcilesExactlyOnce`、`RealLostCommitAcknowledgement_MustReturnOutcomeUnknownAndKeepOneReceipt`、`TerminalRoundOutbox_IsGeneratedFromRealDomainResultAndRecovered`、`TerminalOutbox_IsAtomicAndConsumerIsIdempotent` 在 42/42 中均 Pass，支持“不重复回执/不重复发布”的测试级断言；这不是对真实网络未知结果的全产品证明。

### 6.3 backup → empty DB restore

- `pg_dump` / `pg_restore` exit 0；`table_store.dump` 44,713 bytes。
- `restore-reader-result.json`：`READER-OK`，state=1、seq=2、events=2、receipt=Applied、terminal=true、outboxPresent=true、outboxPayloadMatches=true、outboxPublished=false、cheatVersion=1、`recoveryOwnerEpoch=3`、recovered=true。SHA-256 `B0D075D5...FCCA86`。
- 空库 restore 后的 ownerEpoch=3（高于 crash reader 的 2），与 wrapper `recovery ownerEpoch=3` 一致。

### 6.4 双栅栏、owner/environment、cleanup

- 双栅栏/租约：`LeaseDualInstanceAndOldOwnerFencing`、`StaleStateFenceRejectsWithoutReceiptOrEvent`、`CheckpointAndTimingFences_RoundTripAndRejectStaleVersions` 均 Pass；DurableHost `DualHost_SecondHostWithForeignLiveLease_FailsClosed` 亦在 59/59 中 Pass。
- 表侧环境：16 个进程变量全部 `restored=true`；`environmentRestored=true`。
- Lobby PG wrapper：10 stages 全 PASS（precheck/start/ownership/migrate/verify/archive/cleanup/env-restore/write-evidence）；`plannedExit=0`、`failedStages=[]`；4 个环境变量 `restoredToPreCall=true`；`cluster.rootDeleted=true`、`ownership=owned`；`cleanup` 记录 owned cluster deleted。
- 原始 PG 日志有 FATAL/ERROR 噪声：`role "postgres" does not exist`、`permission denied`/负向权限测试、`canceling statement`/`terminating connection`（故障注入）、迁移前 `schema_migration` 不存在、lobby 侧 3 条 role 缺失。它们与 42/13 的负向/故障路径同窗，测试 XML 仍 0 fail/0 errors；不能把原始 ERROR 文本直接当测试失败。

> 本节结论全部来自该 run 的 `tests.xml`、`tests-targeted.xml`、crash/restore JSON、PG server log、wrapper JSON 与独立 reader 日志，不用 memory 推断 PG。

---

## 7. Plugin、companion golden、架构/确定性

### 7.1 12 DLL / 19 outputs

- 两个 run 的 `verify-v5/unified/plugins/` 均包含 **12 DLL + 7 PDB = 19 outputs**；`plugin-copy.log` 记录 `copied 19 files`，`shared-plugin-provenance.json` 记录 `inputCount=149`、`copiedFileCount=19`、`build.exitCode=0`、`forcedRebuild=true`、`requiredOwnAssemblies=7`。
- 19/19 outputs 的 path/bytes/SHA-256 已逐一重算，0 mismatch；7 个 required own assemblies + 5 个 package DLL 全部存在。
- 产品版本：7 个自有 DLL 为 `1.0.0+72f...`（PR）/`1.0.0+7fe...`（push）；5 个包 DLL 版本与 hash 两边相同。详见 §2.4。
- 149 inputs 是 provenance 的记录；本审计没有把 149 个历史输入全部重放；19 outputs 与 required DLL 已独立核验。

### 7.2 Companion golden

- `companion-fixtures.meta.txt`：`raw native exit=0`、`timed out=False`、`result=PASS`、`4/4 accepted fixtures certified`；4 个 fixture 共 **47 cases**（8+32+4+3）。
- 4 个已认证 fixture 的 SHA-256 与当前 repo 字节一致：
  - `movement-v1.json` `C2D5469E...7C13`，8 cases
  - `shuffle-reversed-v1.json` `5A5EA43F...B619`，32 cases
  - `visibility-v1.json` `30291EC2...F346E`，4 cases
  - `ai-discard-flags-v1.json` `448BAB14...CBE8`，3 cases
- `tests/v5-golden/` 共 **11** 个 fixture；其余 7 个未在 companion gate 认证：`ai-v1`、`catalog-v1`、`cheat-v1`、`physical-v1`、`round-v1`、`scoring-v1`、`shuffle-v1`。
- `git blob pins: NOT_RUN`（shallow CI 未取历史 freeze blobs）；不能把 4/4 写成“11/11 全部 golden 通过”。

### 7.3 架构/确定性

- `Mahjong.Architecture.Tests` 132/132，0 fail。
- `Mahjong.Domain.Tests` 529/529，0 fail；Domain determinism source scan 包含在其中。
- 这些是测试层证据，不等于 G1/G2/G3 验收；本 CI 不覆盖 Unity/IL2CPP/真机。

---

## 8. 证据完整性与 manifest

### 8.1 Artifact ZIP 与 API digest

| run | artifact | bytes | 本地 SHA-256 | API digest | 结论 |
|---|---|---:|---|---|---|
| push 41181934433 | `v5-verify-logs-41181934433-1` | 4,492,175 | `B8E224B838B0D35F8117D001D18F213595300D30480C75B4B9BD3FDA06C98B54` | `sha256:b8e224b8...8b54` | 匹配 |
| PR 41181937456 | `v5-verify-logs-41181937456-1` | 4,492,185 | `B6C11A039528109DEC26DFC6FD36C002B2DC4B13E9FF636D9657332F6644EC5B` | `sha256:b6c11a03...ec5b` | 匹配 |

两个 ZIP 均为 **269 entries**；用 bsdtar 解包后均为 **269 files**（长路径原因：.NET `ZipFile.ExtractToDirectory` 在 Windows 260 路径失败，改用 `tar -xf` 成功；原始 ZIP 未改写）。`EVIDENCE-SHA256` 的 `ZIP/extract` 检查记录 `zip_entries=269 extracted_files=269`。

### 8.2 内部 manifest 交叉核对

| manifest | entries | 结果 | 位置 |
|---|---:|---|---|
| 表侧 `table/manifest.sha256.json` | **152** | **152/152** path/bytes/SHA-256 匹配 | `artifacts-pr/.../verify-v5/table/manifest.sha256.json`（SHA-256 `4A1397...D4BCF`） |
| Lobby `pg/evidence-sha256.txt` | **12** | **12/12** SHA-256 匹配 | `.../verify-v5/unified/pg/evidence-sha256.txt`（SHA-256 `81E218...D383`） |
| plugin outputs | **19** | **19/19** Name/bytes/SHA-256 匹配 | `.../verify-v5/unified/plugins/shared-plugin-provenance.json`（SHA-256 `E3C886...E2B6`） |
| 审计总 manifest | **630** | **630/630** bytes/SHA-256 匹配；0 duplicate；无 self-entry | `build/ci-7fe-audit/EVIDENCE-SHA256.txt`，103,791 bytes，SHA-256 `44CAF101...104B` |

审计总 manifest 覆盖 API metadata、两个 job log、两个 ZIP、两个 269-file 解包树、28 个 direct XML、PG/crash/restore/plugin/companion 证据、本地 build/test/privacy 证据与审计脚本；排除 manifest 自身。原始 ZIP、job log、失败/负向框架日志均保留在 ignored `build/ci-7fe-audit/`。

### 8.3 两棵 artifact 树对比

- 各 **269 files**；共有相对路径 **185**。
- path-only：push **84**、PR **84**（native-exit GUID、VBCSCompiler GUID、`psql-*`、scope-expiry 时间戳、pg_ctl 时间戳等）。
- 共有文件中 **78** 个字节相同、**107** 个不同（日志/XML 时间/执行状态/DLL ProductVersion 等）。
- 不能只用“文件数相同/ZIP 大小接近”推断内容相同；跨 run 的稳定口径是 14 工程计数、用例名集合与通过状态。

### 8.4 关键证据入口

| 证据 | bytes | SHA-256 |
|---|---:|---|
| push job log | 62,396 | `2EAB25A2753930FC5019D41EA1926887AFBB98F4DF00D149CD53074E143E3398` |
| PR job log | 64,836 | `19F339AFB42D10247DAA0FEB45C02ADEE632883B570C3E5EE4CE776FE6A9DA05` |
| PR `unified/summary.log` | 11,589 | `301742E24F49F85E6045B587631FA710066188E23B795A09A60BCAF66DD26A16` |
| push `unified/summary.log` | 11,633 | `11063236D74F3BC9B7C0B1ABD95DF6994BF25D2AE20B96B3CC4C65965A27B839` |
| PR TableService direct XML | 108,974 | `9D7AA7FB425A56F403233671820C75B9940FF9C59BB2018AE6D1FD0675D3E0F7` |
| push TableService direct XML | 108,969 | `CDFB9D77D297F56BAA0868C3A96E61973555946A09404B0B9FE999FCEA8B41DC` |
| PR `table/tests.xml` | 25,535 | `4F5BB97A7E27E7A9B6CFDF0681992B56FC2C2A5BCC7638D0C4555E1E1CDC7B3C` |
| PR `table/tests-targeted.xml` | 8,440 | `532225DF27902581B06FD982364A0E202C741E55EE3B5E0AFF1DE54CF2EDEBFE` |
| PR `crash-reader-result.json` | 347 | `C879E5C627EBC0D174E3A98F826F38A40007C9DDD3C1BA5214378FE063B121B4` |
| PR `restore-reader-result.json` | 347 | `B0D075D5336AFC4B5720781FBDEC3E08D9641D8236111460F75BB83D19FCCA86` |
| PR `pg/wrapper-summary.json` | 8,995 | `9582D2F57DA59ADDF8D2D9884850CB2F6D1BFFE9D311D9E683DF7338F7443954` |
| PR `companion-fixtures.meta.txt` | 1,984 | `E50F347816F84BA2EAA0C6313ED2833FDEDE123D1F5E11C2E67D62A5E8382BC9` |
| 审计总 manifest | 103,791 | `44CAF1016B56CED339F30472D7182D9F1858E05FFA20CA8151E18C0C5ED3104B` |

---

## 9. 一次性缺陷清单（BLOCKER / MAJOR / MINOR）

### D-A68-01（BLOCKER，本组件范围 CLOSED）

- **位置：** `dotnet/tests/Mahjong.TableService.Tests/TestSupport/TableTestClient.cs:361,393,416,470,666`；`StreamPrivacyTests.cs:80,130`。
- **复现：** 在冻结 7fe 上执行 TableService direct 全量；或本地执行 `dotnet exec ...Mahjong.TableService.Tests.dll -method ...StreamPrivacyTests.SpectatorStream_NeverCarriesPrivateData ... -result-xml ...`。
- **实际：** 两 run TableService 180/180，隐私用例 3/3 Pass，9 个 helper 回归 9/9 Pass；本地隐私 20/20 exit 0。
- **期望：** 读超时只取消等待，不 abort socket；后续读取不抛 client `WebSocketException`；隐私断言不降级。
- **证据：** 两个 TableService XML（§4.3）、9 个回归、`privacy-20/`、local build/test 日志。
- **修复要求：** 已按持久 pending receive + 单消费者门 + lifetime-only cancel + dispose 观察 pending 实现；禁止回退为“超时即取消 socket”。
- **验收断言：** 目标隐私用例连续 20 次 exit 0/1-of-1 Pass；TableService 180/180；全量 2,732/2,732；两 run 均无 `WebSocketException: ... Aborted` 的 client read 失败。
- **边界：** 只证明本 helper/本 head/本次执行范围；不证明任何 WebSocket 超时 race 永不存在。

### D-A68-06（MINOR，同因，本组件范围 CLOSED）

- **位置：** 同一 helper 与 9 个新回归。
- **复现：** `TableTestClientReceiveTimeoutTests` 9 条 direct 执行。
- **实际：** 两 run 9/9 Pass；覆盖 late frame、repeated wait、fragment、replay、watermark、close/fault、oversize/undecodable、dispose、concurrent waits。
- **期望：** 精确 timeout→Open→下一帧一次交付，且并发等待不启动第二个 socket receive。
- **证据：** §4.3 的 XML/用例名；`TableTestClientReceiveTimeoutTests.cs:32-281`。
- **修复要求：** 保持这 9 条为常驻回归；不得用 skip/放宽断言替代。
- **验收断言：** 9/9 Pass；TableService 180/180。

### D-A68-02（MAJOR，OPEN：CI 仍无 dotnetTest 逐例证据）

- **位置：** CI runner mode；`tools/ci/verify-v5-table-postgres.ps1` 调 unified verifier 时仍为 `direct`；`tools/verify-v5.ps1` 的 direct 分支。
- **复现：** `gh run view 41181937456 --log` / 两个 artifact 的 `run-info.txt`。
- **实际：** 两 run 均 `runner mode: direct`；14 个 meta 均 `in-process (direct)`；artifact 无 `.trx`/`dotnetTest-*`；`direct-results.xml` 是 direct XML。
- **期望：** 若 G1/验收要求 `dotnet test` 逐例全绿，则必须有 `dotnet test` 命令、逐例 XML/TRX 与 exit 0；否则验收口径必须明确接受 direct runner 并说明差异。
- **证据：** `run-info.txt`、14 个 meta、`direct-results.xml`；本地 `dotnet test` 180/180 只有 console 无逐例 XML（§5.2）。
- **修复要求：** 由 caller 决定增加 dotnetTest job/step 或正式记录 direct 等价性；不得把 direct XML 改名成 dotnetTest。
- **验收断言：** 14/14 工程、2,732 total、0 fail/skip/notRun/errors，且日志中存在可核验的 `dotnet test` 命令与逐例 XML/TRX；或接受 direct 的书面口径。

### D-A68-03（MAJOR，OPEN：历史 whitespace 仍 1,573 条）

- **位置：** 全量 `8fd7c6c..7fe7dbf` 的历史 patch/evidence 文档，不是 7fe 新代码。
- **复现：** `git diff --check 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7..7fe7dbf3e37aefb437132d3584cb2ff09cff6af5`
- **实际：** **exit 2，1,573 条** trailing-whitespace；本次 delta `8b3bdd35..7fe7dbf` **exit 0**。
- **期望：** 全量检查若作为门禁，应为 exit 0；若历史证据保留，则必须把范围限定为 delta。
- **证据：** `build/ci-7fe-audit/full-base-diff-check.txt`（1,573 行）、`delta-8b3-diff-check.txt`（0 行）。
- **修复要求：** 不得为 diff-check 重写历史 patch/evidence；只允许 delta 干净，或另开保留证据语义的清理任务。
- **验收断言：** 报告 delta `git diff --check 7fe7dbf..HEAD` exit 0；若要求全量 clean，则 1,573 条归零且有单独历史证据审计。

### D-A68-04（MINOR，口径仍存在）

- **位置：** `summary.log` 的 20 project rows。
- **实际：** 20 rows = **14 test + 6 build-only helper**；14 个测试工程各有 1 XML + 1 meta。
- **期望：** 分母统一写 14 个测试工程；helper 只算 build-only，不能把 20 行当 20 个 runner。
- **证据：** 两个 `summary.log`、14 个 `direct-xml`、6 个 helper 行（§4.1/§7.1）。
- **修复要求：** 报告/门禁表继续分列 14 test + 6 helper；不得硬编码 2,732 或 20。
- **验收断言：** 报告分母 = 14；目标总数 = 该 run 的真实 XML 总和。

### D-A68-05（MINOR，OPEN：companion 仅 4/11，blob pins NOT_RUN）

- **位置：** `tools/verify-v5-companion-fixtures.ps1` 的 accepted fixture 集；`tests/v5-golden/` 11 个 fixture。
- **实际：** 4/4 accepted certified，47 cases；其余 7 个未认证；`git blob pins: NOT_RUN`。
- **期望：** 不得把 4/4 写成 11/11 全部 golden 通过；历史 freeze blob 若属门禁必须另跑。
- **证据：** `companion-fixtures.meta.txt`、`companion-fixtures.log`、source fixture 列表。
- **修复要求：** 保持 4/4 + 7/11 NOT_RUN 的分母；需要时在完整历史 checkout 执行 blob pin 校验。
- **验收断言：** 4/4 与 7/11 NOT_RUN 分列；或 11/11 均有逐字节/逐 pin 证据。

### D-A68-07（MINOR，历史 provenance；7fe 新 merge 无此问题）

- **位置：** 旧 `a68c157` 的双父合并（`c777e345` + `a14e031b`，非对称并集）。
- **实际（历史）：** 旧 merge tree 等于 c777 tree + 1 个 B2 文档，不是两边文件的对称并集。
- **实际（7fe）：** PR merge `72f728e` 的 parents 为 `8fd7c6c` + `7fe7dbf`，tree 与 7fe 完全相同；`8fd7c6c` 是 7fe 的祖先，因此不存在“第二父内容丢失”的旧问题。
- **期望：** 新 head 的合成 merge 身份可解释；历史 a68 记录不被改写。
- **证据：** §2.2/§2.3、`api/commit-pr-merge.json`、`api/commit-head.json`。
- **修复要求：** 对 7fe 无需修复；保留旧 a68 审计与失败记录。
- **验收断言：** 新 merge parents/tree 与 source head 一致；旧记录保持原样。

### D-A68-08（MINOR，证据对比口径）

- **位置：** 两个 run 的 artifact 树。
- **实际：** 269 files each；185 共有；84 path-only each；78 共有文件字节相同、107 不同。
- **期望：** 跨 run 对比使用工程/用例集合与计数，不用文件数/ZIP 大小直接推断内容相同。
- **证据：** §8.3、`parsed/artifact-tree-diff.json`。
- **修复要求：** 无需改代码；报告保留该差异说明。
- **验收断言：** 14 工程/用例集合/通过状态分别对比；不要求两棵树逐文件同构。

### P-01（PROCESS BLOCKER，不是代码缺陷）：ledger 审批受 reviewer-role 限制

- **位置：** `approve_task(thread_20261031_141347_580a)`。
- **实际：** 桥接器以当前定时会话没有 active reviewer role 拒绝；ledger 仍 `waiting_review`；PR20 body 已记录该限制。
- **期望：** 由原始 reviewer 会话或后续有角色的 review 流程完成批准；不得由本审计绕过角色自行 approve。
- **证据：** PR20 body（记录拒绝原因与此 head）；本次未调用桥接器。
- **修复要求：** 不绕过角色、不自行 approve、不把代码独立验收与 ledger 批准混同。
- **验收断言：** ledger 状态由有权会话变更为 approved；本报告不提供该状态。

> 本次未发现新的代码或 CI BLOCKER/MAJOR。D-A68-02/D-A68-03 是继承的 MAJOR 证据口径问题；D-A68-04/05/08 是继承的 MINOR 口径问题；D-A68-01/06 在本组件范围关闭；D-A68-07 仅保留历史语义。

---

## 10. PASS / FAIL / BLOCKED / NOT_RUN 与门禁状态

| 项目 | 状态 | 说明 |
|---|---|---|
| push run 41181934433 | **PASS（仅该 run）** | step 5 exit 0；2,732/2,732 |
| PR run 41181937456 | **PASS（仅该 run）** | step 5 exit 0；2,732/2,732 |
| source tree / PR merge tree | **PASS** | 7fe tree = 72f tree = `0c6b16...` |
| 实际 checkout 身份 | **PASS** | push 7fe；PR 72f；API head 均 7fe |
| workflow / step / native exit | **PASS** | native 6/6；step 5 0/0 |
| 14 工程 direct XML | **PASS** | 2,732/2,732；0 fail/skip/notRun/errors |
| TableService direct | **PASS** | 180/180；含 9 新 helper |
| 表侧 PG 42 + targeted 13 | **PASS** | 42/42、13/13 |
| crash/restart/reader/empty-DB restore | **PASS** | state=1 seq=2；ownerEpoch 2→3 |
| 双栅栏 / outbox / receipt | **PASS（测试级）** | 相关 42/42 用例 Pass |
| companion 4/4 | **PASS（4/4）** | 47 cases；7/11 NOT_RUN |
| plugin 12 DLL / 19 outputs | **PASS（存在/哈希）** | 19/19；5 package hash 同，7 own 因 ProductVersion 不同 |
| artifact / manifest | **PASS** | 269 files each；ZIP digest；152/12/19；630/630 |
| `dotnetTest` 逐例 XML | **NOT_RUN** | CI 明确 direct；D-A68-02 |
| companion 11/11 | **NOT_RUN** | 仅 4/11 |
| git blob pins | **NOT_RUN** | shallow CI |
| 全量 `git diff --check` | **FAIL（exit 2）** | 1,573 条 pre-existing whitespace；D-A68-03 |
| 报告 delta diff-check | **PASS（exit 0）** | 仅新增本报告 |
| PR20 ledger approval | **BLOCKED（角色）** | `waiting_review`；不自行 approve |
| PR20 / G2–G5 | **NOT_GIVEN** | 本报告不批准 |
| Unity / Player / IL2CPP / Android / iOS / 真实 HTTPS/WSS / 跨网 / 真机 | **NOT_RUN** | 本 CI 只覆盖 .NET tree |
| V4 生产基线 | **仍是唯一生产** | 未改变 |
| G1 | **APPROVED（既有决定）** | 本审计不重审 |
| G2–G5 | **未批准** | 未改变 |
| classified 181 | **未覆盖** | 未改变 |
| Android / iOS / 真机跨网 | **FAIL / BLOCKED / NOT_RUN** | 保留既有状态 |

**PR20 验收结论：NOT_GIVEN。** 本次两个 run 的 CI 证据为 PASS，但该 PASS 只覆盖该 workflow 的 .NET/direct/PG/companion/plugin 行为，不构成 PR20 整批、组件或阶段验收；最终验收由 caller 独立完成。

---

## 11. Git 状态、diff 与范围

### 11.1 工作树与范围

- 工作树：`D:/majiang-wt/ci-7fe7-audit-ds-20261031`；分支 `wt/v5-ci-7fe7-audit-ds-20261031`。
- 冻结 HEAD：`7fe7dbf3e37aefb437132d3584cb2ff09cff6af5`。
- `git status --porcelain=v1` 在取数/构建/本地测试前后均为空（`build/` ignored）。
- 本任务唯一 tracked 变更：`doc/v5-重构/V5-PR20-7FE7DBF-DUAL-CI-AUDIT-20261031.md`。未改生产/测试/verifier/workflow/sln、Unity/Resources/Burst、共享插件或机器信任。
- 未调用/委派 agent；未 push；未创建/修改/合并 PR；未 `record_merge_request`；未 `approve`；未修改 scheduler/`complete.flag`。
- 临时 worktree 保留，未删除；merge-back 义务留给 caller。

### 11.2 base → final

```text
base  8fd7c6cb701dfbb41c28938de4186749bdd1d6c7  tree 61f50856aedd78e0f8609e282baa8313ae8f2a14
final 7fe7dbf3e37aefb437132d3584cb2ff09cff6af5  tree 0c6b16cc067b8366754b5e5c1b80daa6ea81294f

git diff --stat 8fd7c6c..7fe7dbf
662 files changed, 221,898 insertions(+), 1,645 deletions(-)

git diff --name-status 8b3bdd35..7fe7dbf
5 files: 2 docs + 3 test-helper sources
```

- `git diff --check 8fd7c6c..7fe7dbf`：exit 2 / 1,573 条 pre-existing whitespace；见 D-A68-03。
- `git diff --check 8b3bdd35..7fe7dbf`：exit 0。
- 本报告提交后必须复核：`git diff --check 7fe7dbf..HEAD` exit 0；`git diff --stat 7fe7dbf..HEAD` 仅本报告 1 file；`git status --porcelain` 为空。

### 11.3 报告自身的提交身份

报告不能包含自身提交 SHA。提交后以 `git log -1 --format=%H`、`git show -s --format='%H %T %s' HEAD` 读取；报告 blob 的 SHA-256 以 `Get-FileHash 'doc/v5-重构/V5-PR20-7FE7DBF-DUAL-CI-AUDIT-20261031.md'` 或 `git show HEAD:...` 计算。该提交不 push，不改变远端 PR。

---

## 12. 复现与证据读取

### 12.1 读取顺序

1. `build/ci-7fe-audit/EVIDENCE-SHA256.txt`（630 项全量清单）。
2. `api/run-push-final.json` / `api/run-pr-final.json` / `api/*-jobs-final.json`。
3. `logs/job-push-41181934433.log` / `logs/job-pr-41181937456.log`。
4. `artifacts-pr/.../verify-v5/unified/summary.log`、`run-info.txt`、`direct-xml/`。
5. `artifacts-pr/.../verify-v5/table/tests.xml`、`tests-targeted.xml`、`wrapper-summary.json`、`crash-*`、`restore-*`。
6. `artifacts-pr/.../verify-v5/unified/pg/wrapper-summary.json`、`evidence-sha256.txt`。
7. `artifacts-pr/.../verify-v5/unified/companion-fixtures.meta.txt`、`plugins/shared-plugin-provenance.json`。
8. `parsed/`（direct counts、meta/log/XML crosscheck、manifest verification、artifact tree diff、plugin versions）。
9. `logs/local-*`、`privacy-20/`（本地探针，不是 CI 替代）。

### 12.2 关键命令

```powershell
gh api repos/liuchangready/majiang-game/actions/runs/41181934433
gh api repos/liuchangready/majiang-game/actions/runs/41181937456
gh run view 41181934433 --repo liuchangready/majiang-game --attempt 1 --log
gh run view 41181937456 --repo liuchangready/majiang-game --attempt 1 --log

powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/ci-7fe-audit/audit/parse-test-xml.ps1 `
  -PushRoot build/ci-7fe-audit/artifacts-push/v5-verify-logs-41181934433-1 `
  -PrRoot build/ci-7fe-audit/artifacts-pr/v5-verify-logs-41181937456-1 `
  -OutDir build/ci-7fe-audit/parsed

powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/ci-7fe-audit/audit/crosscheck.ps1 `
  -PushRoot build/ci-7fe-audit/artifacts-push/v5-verify-logs-41181934433-1 `
  -PrRoot build/ci-7fe-audit/artifacts-pr/v5-verify-logs-41181937456-1 `
  -OutDir build/ci-7fe-audit/parsed

powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/ci-7fe-audit/audit/verify-manifests.ps1 `
  -PushRoot build/ci-7fe-audit/artifacts-push/v5-verify-logs-41181934433-1 `
  -PrRoot build/ci-7fe-audit/artifacts-pr/v5-verify-logs-41181937456-1 `
  -OutDir build/ci-7fe-audit/parsed

powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/ci-7fe-audit/audit/verify-evidence-manifest.ps1 `
  -Root build/ci-7fe-audit -Manifest build/ci-7fe-audit/EVIDENCE-SHA256.txt

dotnet build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj `
  -c Release --no-incremental --nologo -v:minimal
dotnet test dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj `
  -c Release --nologo -v:minimal
```

---

## 13. 最终判定

1. **两个指定 run 在精确 head 上均已完成且 step 5 exit 0：** push `41181934433` 与 PR `41181937456` 的 API `head_sha` 都是 `7fe7dbf3...`；push 实际 checkout 7fe，PR 实际 checkout 合成 merge `72f728e...`，两者 tree 完全相同。
2. **真实测试计数是 14 工程 / 2,732 total / 2,732 passed / 0 fail / 0 skip / 0 notRun / 0 errors，两 run 相同：** 不是 11 个 runner，也不是把 20 个 project rows 当测试工程；TableService 为 180/180，包含 9 个新 helper 与 3 个隐私用例。
3. **表侧 PG 42/42 与 targeted 13/13 是真实执行证据：** crash writer 在 committed state=1/seq=2 后被强制终止；PostgreSQL immediate restart 有 3 条 recovery 线；独立 reader 恢复 state/receipt/outbox；空库 `pg_dump/pg_restore` reader 为 ownerEpoch=3；双栅栏/outbox/receipt 用例均 Pass。没有用 memory 推断 PG。
4. **D-A68-01/06 在本组件证据范围关闭，但不是永远无 race：** 180/180、9/9、20/20 本地隐私执行均通过；服务端 teardown 的 `Aborted` 日志按测试完成后清理解释，不能与 client read-after-abort 失败混淆。
5. **仍开放的证据口径：** D-A68-02（CI 无 dotnetTest 逐例 XML，MAJOR）、D-A68-03（1,573 条历史 whitespace，MAJOR）、D-A68-04（20 rows = 14 test + 6 helper，MINOR）、D-A68-05（4/11 + blob pins NOT_RUN，MINOR）、D-A68-08（artifact 树不同构，MINOR）。D-A68-07 只保留旧 a68 历史语义；7fe 的新 merge 已核验。
6. **审批状态不因 CI 绿而改变：** `approve_task(thread_20261031_141347_580a)` 被桥接器以无 active reviewer role 拒绝，ledger 仍 `waiting_review`；本审计不绕过角色、不自行 approve、不把代码独立验收与 ledger 批准混同。MR 已由 caller 记录为 `MR_RECORDED/synced`，那不是 ledger approval。
7. **门禁状态不变：** V4 仍是唯一生产基线；G1 已批准，G2–G5 未批准；classified 181 未覆盖；Android FAIL / iOS BLOCKED / 真机跨物理网 NOT_RUN。Unity/Player/IL2CPP/真实 HTTPS/WSS/设备上下文**未由本 CI 覆盖**。
8. **本报告是审计输入，不是 PR20/组件/阶段验收。** 最终验收由 caller 独立完成；本文与 `build/ci-7fe-audit/` 只提供可复核的原始证据、计数、哈希、缺口与修复断言。

---

### 附：并行任务边界

本轮未取消、暂停、重做、复制或 retarget 任何其他线程；未调用桥接器，未读取/修改其他 worktree。P4.5 `thread_20261031_024102_96e6` 仍为 `waiting_review`（同类缺陷第 15 轮无代码变化，按 review protocol §3 等待人工裁决，保留已认可 b 与 12 局样本）；B2 `thread_20261031_124526_0eef` 仍 running，单局可信终局验收后的 service match 编排不在本任务执行；helper `thread_20261031_141347_580a` 的 ledger 批准受上述 reviewer-role 限制。按任务上下文，DeepSeek 活跃 total 2 / ceiling 2（B2 + 本任务），GLM/Anti 目标 0，本轮未派发。V4 生产、G1 已批准、G2–G5 未批准、classified 181 未覆盖、Android FAIL / iOS BLOCKED / 真机跨物理网 NOT_RUN 均保持不变。
