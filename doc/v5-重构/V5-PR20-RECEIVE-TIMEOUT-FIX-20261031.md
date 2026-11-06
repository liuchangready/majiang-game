# V5 PR20 读超时辅助修复交付报告（D-A68-01 / D-A68-06）

- 日期：2026-10-31
- 状态：**本地实现与验证完成，等待 caller 独立复核**。本报告不构成 PR20、组件或 G2–G5 验收。
- 工作树：`D:/majiang-wt/ci-receive-timeout-ds-20261031`
- 分支：`wt/v5-ci-receive-timeout-ds-20261031`
- base：`8b3bdd35545b1235b1212ea03fce1153e6ba3cdc`（tree `d240231fd6b0a57f235b99e91c8e31b965ae05ab`，已审查 PR20 集成头）
- 代码 final：`0e5224346fc9fa9cfcf72935302274960e1975e4`（tree `6a23f26fdd704d7c05aca5ba3aaa037cbc7b3182`，仅本任务三文件）
- 报告提交：本文件所在提交。提交 SHA 不能自引用；提交后由 `git rev-parse HEAD` 读取，并记录在 ignored `build/receive-timeout-fix/final-head.txt`。

> **结论边界：** 本任务只关闭已验收审计 D-A68-01 BLOCKER 及同因 D-A68-06，修复测试支撑代码并交付真实 helper 回归。V4 仍是唯一生产基线；G1 已批准、G2–G5 未批准；classified 181 未覆盖；Android FAIL / iOS BLOCKED / 真机跨网 NOT_RUN 均未改变。未 push、未建改 PR、未 record MR、未 merge 远端、未部署、未改 scheduler/`complete.flag`、未委派 agent。

---

## 1. 结论摘要

| 项目 | 结果 | 证据 |
|---|---|---|
| D-A68-01 BLOCKER | **本地修复 PASS，待 caller 独立复核** | `TableTestClient.cs`；9 个新回归；隐私用例 20/20 |
| D-A68-06 MINOR（同因时序证据） | **本地修复 PASS，待 caller 独立复核** | 真实 loopback + 可控 WebSocket 回归 |
| fresh Release build | PASS，exit 0，0 warning / 0 error | `build/receive-timeout-fix/fresh-release-build.log` |
| TableService 全量 direct XML | **180 / 180 passed**，0 failed / 0 skipped / 0 notRun / 0 errors | `table-direct-all.xml` |
| TableService 单独 `dotnet test` | **180 / 180 passed**，exit 0 | `table-dotnet-test.log` |
| 新 helper 回归 | **9 / 9 passed**，exit 0 | `new-regressions-direct.xml` |
| 目标隐私用例真实执行 | **20 / 20 轮 exit 0；每轮 1/1 passed** | `privacy-20-final/` |
| 原 helper 红例（ignored 临时工程） | **FAIL，exit 1，state=Aborted** | `first-failure-red.xml` |
| managed unified（`dotnet test`） | **FAIL（17 PASS / 3 环境阻塞）**；TableService 180 PASS | `unified-dotnetTest/summary.log` |
| PG 灾备 / 临时测试库 | **NOT_RUN / BLOCKED**（本任务未启动 PG） | 见 §6 |
| Unity / Player / IL2CPP / Android / iOS / 真机 / 跨网 | **NOT_RUN**（无授权） | 见 §6 |
| 远端 PR/push 双 CI | **NOT_RUN**（由 caller 在新精确 SHA 上发布） | 见 §7 |

**一句话结论：** 读超时不再取消底层 `WebSocket.ReceiveAsync`；超时只取消当前等待，下一次等待会接到原 pending 帧且只消费一次。原 helper 在同一真实 loopback 回归下确定性失败（socket Aborted），修复后 9/9 新回归、20/20 隐私执行及 180/180 TableService 全部通过。统一门禁仍因未提供 PostgreSQL 测试环境而整体 FAIL，不能写成 PASS。

---

## 2. 根因与修复

### 2.1 根因（沿用已验收审计与 caller 探针）

- 失败 run：PR `41167851418`，`Mahjong.TableService.Tests.StreamPrivacyTests.SpectatorStream_NeverCarriesPrivateData`；唯一失败为 `WebSocketException: invalid state ('Aborted')`。
- 同树 push run `41167846340` 通过，source tree 相同；不是服务端投影失败，也不是 a68 源码回归。
- `TableTestClient.ReceiveAsync` 把 `TestTimeout.Linked(timeout)` 的 token 传入 `_ws.ReceiveAsync`。读超时会中止 `ClientWebSocket`；`ReadUntilAsync` 随后复用同一 socket，下一次 receive 在 Aborted 状态抛 `WebSocketException`。
- caller 独立探针证据 `build/caller-timeout-baseline/probe.log`，640 bytes，SHA-256 `F9C67A9705163C2EB20ED9C35A4D4FF591FA9A317DF804C8FD6A23D847F8BB7B`；本轮复算一致。它是机制反证，不作为修复后测试。

### 2.2 `TableTestClient` 修复

实现选择：**持久 pending 完整 message receive**（不是后台 pump，也不是无限缓存）。

- **单一传输 owner**：`_pendingReceive` 是唯一的 `_ws.ReceiveAsync` 调用链；`ReceiveOneMessageAsync` 累积分片直到 `EndOfMessage`。
- **超时只取消等待**：`ReceiveCoreAsync` 用 `Task.WaitAsync(per-wait CTS)` 等待 `_pendingReceive`；超时抛出 `OperationCanceledException`（保留原 API 测试用法），但**不取消** socket 级 receive。pending task 原样保留给下一次等待。
- **只消费一次**：成功取到完整 message 后才在 `_receiveGate` 内清空 `_pendingReceive`；下一次等待不会重放同一帧。
- **不并发调用 `ReceiveAsync`**：`_readGate` 串行化所有 `ReceiveAsync` / `ReadUntilAsync` / `ExpectCloseAsync` 消费者；第二个等待只会排队或按自己的 deadline 超时，绝不启动第二个 socket receive。
- **保留原语义**：完整帧分片累积、`MaxTestInboundBytes = 4 MiB`、`close` 返回、协议/传输 fault 原样传播、`_replay` 顺序与 `MaxReplayFrames = 512`、watermark、baseline、decision 去重均未改。
- **确定性销毁**：`DisposeAsync` 先置 disposed、取消 lifetime CTS、`Abort`/`Dispose` socket，再 `await` 并观察 pending task（吞掉 teardown 异常），最后释放 lifetime CTS；不泄漏未观察 task，也不把关闭异常改写成读超时。
- **测试接入点**：仅在本文件新增 `AttachForTransportTests`、`UnderlyingReceiveCalls`、`HasPendingReceive`，用于让回归驱动真实 helper 的公共边界；无生产调用者。

### 2.3 `StreamPrivacyTests` 修复

- 保留全部隐私断言：`spectatorFrames.Count >= 1`、每帧 `MessageType == PublicUpdate`、`PublicUpdate.Snapshot.View != null`；未删 skip、未减窗口、未 catch `WebSocketException` 冒充超时。
- 采样窗口仍为 2000 ms；每次等待使用**总 deadline 的剩余时间**，而不是每次 500 ms 独立取消。
- 仅 `catch (OperationCanceledException)` 结束采样；随后断言 `spectator.State == WebSocketState.Open`，证明该超时没有 abort 真实 socket。
- 无数据时只结束采样，不把超时写成隐私通过；隐私断言仍逐帧执行。

### 2.4 保留的不变量

- `ReadUntilAsync` 仍先扫 replay，再读传输；匹配时按原顺序移除到匹配帧并推进 watermark。
- pending 帧晚到时只交付一次；超时后的 replay 顺序、watermark、baseline、decision 去重由新回归与既有全量测试共同覆盖。
- 真实 close 仍由 `ReadUntilAsync` 抛 `InvalidOperationException`；WebSocket fault 仍抛 `WebSocketException`；超过 4 MiB 仍失败；不可解码帧仍抛 `WireMalformedException`。

---

## 3. 全部已知缺陷映射（一次性）

| ID | 级别 | 本轮处置 | 证据 / 边界 |
|---|---|---|---|
| **D-A68-01** | **BLOCKER** | **本地修复**：helper 持久 pending receive；隐私采样改总 deadline；新增 9 个回归 | §2、§5.1–§5.6 |
| **D-A68-06** | MINOR | **本地修复**：同一回归可重复观测 timeout → 仍 Open → 下一帧一次交付；精确时序由 fake/loopback 断言 | §5.1、§5.3 |
| D-A68-02 | MAJOR | **未修复，仍待 caller 裁决**：direct vs `dotnet test` 口径 | 本报告分别列 direct XML 与 `dotnet test`；不把 direct XML 叫 dotnetTest |
| D-A68-03 | MAJOR | **未修复，仍待后续裁决**：历史 patch/evidence 的 1,573 条 whitespace | 全量 `8fd7c6c..HEAD` 仍 exit 2；本任务 delta `8b3bdd3..HEAD` exit 0；未改写证据 |
| D-A68-04 | MINOR | **未改审计口径**：本轮 unified 实际为 14 个测试工程 + 6 个 build-only helper；TableService 分母按新增后 180 | §5.4、§5.7；未用 171 硬编码 |
| D-A68-05 | MINOR | **未改**：companion 仍 4/4、47 cases；其余 7/11 fixture 未认证；git blob pins NOT_RUN | §5.7；不得写成全部 golden 通过 |
| D-A68-07 | MINOR | **未改**：a68 双父合并语义仍由 caller 确认 | 本任务不重做 provenance |
| D-A68-08 | MINOR | **未改**：两次 artifact 树非同构 | 本任务不重做 CI 证据审计 |

> 上述 D02/D03/D04/D05/D07/D08 不因本任务而消失；本报告只关闭 D01 与同因 D06 的测试支撑缺陷，不扩到 runner 口径、历史 whitespace、golden 或 merge provenance。

---

## 4. 文件范围

本任务 tracked 变更仅 4 个文件：

| 文件 | 变更 |
|---|---|
| `dotnet/tests/Mahjong.TableService.Tests/TestSupport/TableTestClient.cs` | helper 持久 pending receive、单消费者门、超时只取消等待、确定性销毁、内部测试接入点 |
| `dotnet/tests/Mahjong.TableService.Tests/StreamPrivacyTests.cs` | 总 deadline 剩余时间采样；保留隐私断言；断言超时后 socket Open |
| `dotnet/tests/Mahjong.TableService.Tests/TableTestClientReceiveTimeoutTests.cs` | 新增 9 个真实 helper 回归（真实 loopback + 可控 WebSocket） |
| `doc/v5-重构/V5-PR20-RECEIVE-TIMEOUT-FIX-20261031.md` | 本报告 |

代码提交 `0e52243` 的 stat：

```text
 .../StreamPrivacyTests.cs                          |  26 +-
 .../TableTestClientReceiveTimeoutTests.cs          | 647 +++++++++++++++++++++
 .../TestSupport/TableTestClient.cs                 | 368 +++++++++---
 3 files changed, 959 insertions(+), 82 deletions(-)
```

未修改生产服务、Domain、actor、client、Wire、PG、CI、verifier、sln、其他测试；未改其他 worktree；未触碰 Unity/Resources/Burst、共享插件或设备资源。

---

## 5. 验证与证据

### 5.1 原 helper 红例（ignored 临时工程）

从 base `8b3bdd3` 的 `HEAD:dotnet/tests/.../TableTestClient.cs` 原样复制到 ignored `build/receive-timeout-fix/red/`，用同一真实 Kestrel/ClientWebSocket 场景（150 ms 超时 → 断言仍 Open → 晚到帧可读一次）编译运行原 helper：

```text
dotnet exec build/receive-timeout-fix/red/bin/Release/net8.0/RedReceiveTimeout.Tests.dll \
  -class Mahjong.TableService.Tests.RedReceiveTimeoutTests -noLogo -noColor \
  -result-xml build/receive-timeout-fix/first-failure-red.xml
```

- native exit：**1**
- 结果：`Total: 1, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0`
- 失败：`Expected: Open / Actual: Aborted`
- XML：`first-failure-red.xml`，2,661 bytes，SHA-256 `B61FF5D3C624D414CE0144A3E1F783B18D070B8139E36DA6D2C17D036D8A2707`
- 日志：`first-failure-red.log`，SHA-256 `AC93C8BE3A3B4F804681C9CE4B2F00FE77CDDC53B9D5DAD14D6FDDC534F465BF`

这是**原 helper 的首失败**，不是修复后结果。修复后同一场景见 §5.3。

### 5.2 Fresh Release build

```text
dotnet build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj \
  -c Release --no-incremental -v:minimal
```

- native exit：**0**
- 0 warning / 0 error
- 日志：`fresh-release-build.log`，2,572 bytes，SHA-256 `A4916C2BEFC763F53993F07D2EE0E6EC82F93016B30C0C69F3AD80CCE9CA1EA6`

### 5.3 新 helper 回归（9/9）

```text
dotnet exec dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll \
  -class Mahjong.TableService.Tests.TableTestClientReceiveTimeoutTests \
  -noLogo -noColor -result-xml build/receive-timeout-fix/new-regressions-direct.xml
```

- native exit：**0**
- `Total: 9, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`
- XML：6,544 bytes，SHA-256 `834559E8FEA3437F16AD6F44C27268E6B53B4DE81CBE3204D52D9FD96D059A43`
- 日志：760 bytes，SHA-256 `8A2779662CA3B7125B3C923278165F06833F6441B61B48A7111F2F08DB7B00DC`

| # | 回归 | 覆盖 |
|---|---|---|
| 1 | `TimeoutLeavesRealSocketOpen_AndLateFrameIsReadableOnce` | 真实 loopback：超时后 ClientWebSocket 仍 Open；晚到帧可读；下一等待不重放 |
| 2 | `RepeatedTimeouts_KeepOneUnderlyingReceive` | 4 次超时只 1 次底层 receive；晚到帧可读 |
| 3 | `FragmentAcrossTimeouts_StaysCompleteAndDecodesOnce` | 分片跨多次等待超时；完整解码且只交付一次 |
| 4 | `ReadUntilTimeout_KeepsReplayOrderAndPendingFrame` | replay 顺序、pending 帧、后续等待不被损伤 |
| 5 | `WatermarkAndBaseline_SurviveTimeoutsAndReplay` | watermark、baseline 在超时与 replay 后仍正确，stale 帧丢弃 |
| 6 | `CloseAndTransportFault_PropagateUnchanged` | close 正常返回/在 ReadUntil 抛错；WebSocket fault 原样传播 |
| 7 | `OversizeAndUndecodableFrames_StillFail` | >4 MiB 失败；不可解码帧仍抛 `WireMalformedException` |
| 8 | `Dispose_ObservesParkedReceiveWithoutBackgroundLeak` | pending 销毁可完成；无 active receive/未观察 task；双 dispose 幂等 |
| 9 | `ConcurrentWaits_NeverIssueConcurrentReceive` | 并发等待只 1 个底层 receive；第二等待只超时，不偷 pending |

> 首次编写回归时曾出现 3 个**测试自身**问题（heartbeat nonce 非法、分片用例先入队后启动等待、不可解码异常类型断言错误），修正后为上述 9/9；中间日志 `new-regressions-dotnet-test.log` 仅保留为开发迭代，不作为验收证据。

### 5.4 TableService 全量 direct XML（180/180）

```text
dotnet exec dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll \
  -noLogo -noColor -result-xml build/receive-timeout-fix/table-direct-all.xml
```

- native exit：**0**
- `Total: 180, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`，time 93.957 s
- XML：111,507 bytes，SHA-256 `B59315C60CAB43B05EFF1993DA020319E2535597DBAD421C0CECAC1B46BA5A25`
- 日志：555,144 bytes，SHA-256 `11B4CCD4DF8936475513B564058B065ACC4787971C732723CFC16D004C43088F`
- 用例分母为**新增后的 180**（原 171 + 9 新回归），不是硬编码 171。

| class | total | failed | skipped |
|---|---:|---:|---:|
| `AdmissionTicketTests` | 15 | 0 | 0 |
| `BeamSwapSlotResolutionTests` | 4 | 0 | 0 |
| `ConcatenationFramingTests` | 65 | 0 | 0 |
| `ConcurrencyBackpressureTests` | 5 | 0 | 0 |
| `DisplacementTests` | 3 | 0 | 0 |
| `DrainLifecycleTests` | 3 | 0 | 0 |
| `FirstTurnDoubleRiichiProjectionTests` | 1 | 0 | 0 |
| `HandshakeFramingTests` | 12 | 0 | 0 |
| `ReceiptScopeContractTests` | 2 | 0 | 0 |
| `ReviewRound3DecisionRuntimeTests` | 5 | 0 | 0 |
| `ReviewRound5FactProvenanceTests` | 6 | 0 | 0 |
| `RuntimeDecisionRuntimeTests` | 4 | 0 | 0 |
| `ServerHeartbeatBindingTests` | 3 | 0 | 0 |
| `SessionFlowTests` | 10 | 0 | 0 |
| `StreamPrivacyTests` | 3 | 0 | 0 |
| `TableTestClientReceiveTimeoutTests` | **9** | 0 | 0 |
| `TableTicketRegistryLifecycleTests` | 26 | 0 | 0 |
| `TlsCertificateTests` | 4 | 0 | 0 |
| **合计** | **180** | **0** | **0** |

### 5.5 TableService 单独 `dotnet test`（180/180）

```text
dotnet test dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj \
  -c Release --no-build
```

- native exit：**0**
- `Failed: 0, Passed: 180, Skipped: 0, Total: 180`，duration 1m 33.103s
- 日志：`table-dotnet-test.log`，840 bytes，SHA-256 `89C1342BD21329516B7CEEBC591A4B5AA8B8D36AF5BEB387C1A4B4EF715E0568`
- **口径：** 这是真实 `dotnet test` 的聚合 console 结果；本环境该 MTP 配置未生成 `.trx`。上表逐例 XML 来自 **direct runner**，不是 `dotnet test` 产物。二者分开记录，不能互相冒名。

### 5.6 目标隐私用例 20 次真实执行

```text
dotnet exec ...Mahjong.TableService.Tests.dll \
  -method Mahjong.TableService.Tests.StreamPrivacyTests.SpectatorStream_NeverCarriesPrivateData \
  -noLogo -noColor -result-xml build/receive-timeout-fix/privacy-20-final/round-NN.xml
```

- 20 轮全部 native exit **0**；每轮 XML `total=1, passed=1, failed=0, skipped=0, notRun=0, errors=0`。
- 汇总：`privacy-20-final/privacy-20-summary.csv`，SHA-256 `16909B316AD6D069521430DB07C204DC0B67C18C6F42FEC1E20E0B93C7E123BA`；文本汇总 `SUMMARY.txt`，SHA-256 `A9F31714E74D3A1A8F5B6A4F5CDC417339336ACAE8F6EEE689C1A6D2158D15D4`。
- 每轮 XML/exit 文件均保留；manifest 逐项列出。20 轮均 PASS、0 skip；测试路径未把 `WebSocketException` 当作超时，也没有因 `Aborted` 失败。

| rounds | 每轮 native exit | 每轮 XML | 结果 |
|---|---|---|---|
| `round-01` … `round-20` | 0 | `total=1 / passed=1 / failed=0 / skipped=0 / notRun=0 / errors=0` | **PASS 20/20** |

### 5.7 Managed unified（`dotnet test`）— 环境阻塞，非本任务 PASS

```text
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 \
  -Configuration Release -RunnerMode dotnetTest -SkipPluginCopyCheck \
  -LogDir build/receive-timeout-fix/unified-dotnetTest
```

- native exit：**1**；`OVERALL: FAIL (17 PASS, 3 FAIL, 0 BLOCKED, 0 NOT_RUN)`。
- 运行身份：`head=5bc8c18b75287a3e8bbe33c087585f62f3f5df95`（报告主体提交；其后仅本文件证据口径修正），branch `wt/v5-ci-receive-timeout-ds-20261031`，运行前 tracked clean；`run-info.txt` 可复核。
- 该 run 全部测试行使用 `dotnet test`，没有 direct XML；未把 direct XML 写成 dotnetTest。
- 提交前同一工作树的同口径 run 归档在 `unified-dotnetTest-precommit/`，结果相同；本节数据引用提交后干净树的复跑。
- companion gate：**PASS 4/4**，47 cases；7/11 fixture 未认证；git blob pins **NOT_RUN**。
- plugin copy：**NOT_RUN**（`-SkipPluginCopyCheck`；无共享插件构建授权）。

| 工程 | total | pass | fail | skip | 结论 |
|---|---:|---:|---:|---:|---|
| **Mahjong.TableService.Tests** | **180** | **180** | 0 | 0 | **PASS** |
| Mahjong.TableService.DurableHost.Tests | 59 | 38 | 21 | 0 | **BLOCKED**：缺 `MAHJONG_P54B_TEST_PG_ADMIN` 等 PG 环境 |
| Mahjong.Infrastructure.Tests | 42 | 1 | 41 | 0 | **BLOCKED**：缺 `MAHJONG_P52_TEST_POSTGRES` |
| Mahjong.LobbyService.Tests | 89 | 77 | 0 | 12 | **NOT_RUN/BLOCKED**：缺 `MAHJONG_LOBBY_TEST_POSTGRES`；verifier 将 skip 记为 FAIL |
| 其余 10 个测试工程 + 6 个 helper | — | 全 PASS | 0 | 0 | PASS / build-only |

失败原因均为真实 PostgreSQL 测试环境未提供（`BLOCKED:` / `NOT_RUN:`），不是本 helper 修复失败。本任务未启动 PG、未使用 memory 冒充 PG、未把 skip 写成 PASS；统一门禁整体 **FAIL**，对应 PG 部分标 **BLOCKED/NOT_RUN**。

### 5.8 Whitespace 与范围

- 本任务 delta：`git diff --check 8b3bdd35545b1235b1212ea03fce1153e6ba3cdc..HEAD` → **exit 0**。
- 全量历史范围：`git diff --check 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7..HEAD` → **exit 2，1,573 条**，与审计 D-A68-03 记录的 pre-existing whitespace 数量一致；未改写历史 patch/evidence，未声称全量干净。原始输出：`full-base-diff-check.txt`。

### 5.9 Hash manifest

- `build/receive-timeout-fix/EVIDENCE-SHA256.txt`：**255 entries / 35,671 bytes / SHA-256 `F3C43588F1B1A363CDCD25A086E09EAE4CD91B10609CA4A2B8D223E7D0A6EEDE`**
- 覆盖本任务 ignored 证据（日志、XML、脚本、首失败、red 源、20 轮隐私、unified 日志）及 caller baseline 探针；排除 manifest 自身、`final-head.txt`、`bin/`/`obj/`/`TestResults/` 构建产物。
- 关键 SHA：
  - caller probe：`F9C67A9705163C2EB20ED9C35A4D4FF591FA9A317DF804C8FD6A23D847F8BB7B`
  - red XML：`B61FF5D3C624D414CE0144A3E1F783B18D070B8139E36DA6D2C17D036D8A2707`
  - full direct XML：`B59315C60CAB43B05EFF1993DA020319E2535597DBAD421C0CECAC1B46BA5A25`
  - dotnet test log：`89C1342BD21329516B7CEEBC591A4B5AA8B8D36AF5BEB387C1A4B4EF715E0568`
  - new regressions XML：`834559E8FEA3437F16AD6F44C27268E6B53B4DE81CBE3204D52D9FD96D059A43`
  - unified summary：`100B769B953AFB2D3FADADEE4C02AE7CC914014D2F70987CFC808AD69F102863`

---

## 6. PASS / FAIL / BLOCKED / NOT_RUN

| 项目 | 状态 | 说明 |
|---|---|---|
| D-A68-01 helper 修复 | **PASS（本地）** | 待 caller 独立复核 |
| D-A68-06 同因回归 | **PASS（本地）** | 9/9 新回归 |
| fresh Release build | **PASS** | exit 0 |
| TableService direct 全量 | **PASS** | 180/180/0/0/0/0 |
| TableService `dotnet test` | **PASS** | 180/180，exit 0 |
| 隐私用例 20 轮 | **PASS** | 20/20，每轮 exit 0 |
| managed unified | **FAIL** | 17 PASS + 3 环境阻塞；整体 exit 1 |
| TableService.DurableHost.Tests | **BLOCKED** | 21/59 fail，缺 PG 环境 |
| Infrastructure.Tests | **BLOCKED** | 41/42 fail，缺 PG 环境 |
| LobbyService.Tests | **NOT_RUN / BLOCKED** | 12 skip，缺 PG 环境 |
| 表侧 PG 42 + targeted 13 | **NOT_RUN** | 本任务未启动 PG；未重复灾备 |
| companion 4/4 | **PASS** | 47 cases；其余 7/11 fixture 未认证 |
| git blob pins | **NOT_RUN** | 浅历史未请求 |
| Unity / Player / IL2CPP / Android / iOS / 真机 / 跨网 | **NOT_RUN** | 无授权，未启动 |
| 共享插件构建 | **NOT_RUN** | `-SkipPluginCopyCheck` |
| 远端 PR/push 双 CI | **NOT_RUN** | caller 在新精确 SHA 发布后负责 |
| G1 / G2–G5 | **已批准 / 未批准** | 本报告不改 |
| V4 生产基线 | **仍是唯一生产** | 本报告不改 |
| classified 181 | **未覆盖** | 本报告不改 |
| Android / iOS / 真机跨网 | **FAIL / BLOCKED / NOT_RUN** | 保留既有状态 |

**接收者不自行批准组件或阶段；最终结论等待 caller 独立复核。**

---

## 7. 剩余依赖与边界

1. **caller 独立复核**：代码 `0e52243`、本报告、`build/receive-timeout-fix/` 证据。
2. **caller 发布新精确 SHA 的 push + PR 双 CI**：本任务未 push、未建改 PR、未 record MR、未 merge；不得用本地 180/180 代替远端双 CI。
3. **PG 环境**：统一门禁的 DurableHost/Infrastructure/Lobby PG 部分仍 BLOCKED/NOT_RUN；若验收要求 PG，需在独立可丢弃集群补跑。
4. **D-A68-02**：direct vs `dotnet test` 口径仍待 caller 裁决；本报告分别列出两者，不混用。
5. **D-A68-03**：全量历史 whitespace 仍 1,573 条；未改写 patch/evidence；本任务 delta clean。
6. **D-A68-05**：companion 仍只认证 4/11 fixture；git blob pins NOT_RUN。
7. **D-A68-07/08**：merge provenance 与 artifact 树对比不在本任务范围。
8. **Unity/Player/IL2CPP/Android/iOS/真机/跨网**：未运行，状态不变。
9. **并行资源**：未改/未重试 `thread_20261031_124526_0eef`（B2）与 `thread_20261031_024102_96e6`（P4.5）的代码或范围；未触碰其他 worktree。

---

## 8. 复现命令

```powershell
# 1) 原 helper 红例（ignored 临时工程；已保存首失败）
dotnet exec build/receive-timeout-fix/red/bin/Release/net8.0/RedReceiveTimeout.Tests.dll `
  -class Mahjong.TableService.Tests.RedReceiveTimeoutTests -noLogo -noColor `
  -result-xml build/receive-timeout-fix/first-failure-red.xml

# 2) fresh Release build
dotnet build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj `
  -c Release --no-incremental -v:minimal

# 3) 新 helper 回归（9/9）
dotnet exec dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll `
  -class Mahjong.TableService.Tests.TableTestClientReceiveTimeoutTests `
  -noLogo -noColor -result-xml build/receive-timeout-fix/new-regressions-direct.xml

# 4) TableService 全量 direct XML（180/180）
dotnet exec dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll `
  -noLogo -noColor -result-xml build/receive-timeout-fix/table-direct-all.xml

# 5) TableService 单独 dotnet test（180/180）
dotnet test dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj `
  -c Release --no-build

# 6) 隐私用例单轮（循环 20 次即 privacy-20-final）
dotnet exec dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll `
  -method Mahjong.TableService.Tests.StreamPrivacyTests.SpectatorStream_NeverCarriesPrivateData `
  -noLogo -noColor -result-xml build/receive-timeout-fix/privacy-20-final/round-01.xml

# 7) managed unified（PG 未提供，整体 FAIL/环境阻塞）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 `
  -Configuration Release -RunnerMode dotnetTest -SkipPluginCopyCheck `
  -LogDir build/receive-timeout-fix/unified-dotnetTest
```

---

## 9. 声明

- 本报告只证明本任务在本地以真实 Kestrel/ClientWebSocket、受控 WebSocket 和真实 TableService 测试程序集执行的结果；不证明远端 CI、Unity、Player、IL2CPP、Android、iOS、真实网络或 PG 灾备。
- 未把 skip、空测试、`Total=0`、旧 DLL/旧 XML 当作 PASS；未修改 verifier/CI/sln 放行。
- 未在 tracked 报告写入 ticket、token、连接串、seed、暗牌或私有存档；只写路径、计数、错误类别和 SHA-256。
- V4 仍是唯一生产基线；G1 已批准、G2–G5 未批准；classified 181 未覆盖；Android FAIL / iOS BLOCKED / 真机跨网 NOT_RUN。
- 最终验收由 caller 独立完成。
