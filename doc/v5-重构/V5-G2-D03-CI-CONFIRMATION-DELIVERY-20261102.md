# G2-D03 CI 确认与测试修复交付（D-CI-01）

日期：2026-11-02（Asia/Shanghai）。任务：`G2-D03-CI-01`。范围仅限 D03 Riichi 的 CI 失败确认、测试等待/目标选择修复与同轮回归；不是重启 D03 全任务，不触碰 D05、Unity/Player、三绝技人工范围或任何生产代码。

## 1. 结论

- **D-CI-01 是测试侧时序假设缺陷，不是已证实的产品缺陷。** 失败测试在 `receipt` 已确认 `Applied` 后，先读 `session.PlayerSnapshot`（客户端最新已应用快照），再断言该快照相对基线 `+1`。Riichi 场景的 driver 会在目标命令之后继续提交一条合法后续命令；当该后续 publication 在断言前已被 pump 应用时，`session.PlayerSnapshot` 已是目标之后的状态，`+1` 断言失败。
- 修复只改测试：按 `receipt.CommandVersion + roundId + seat + event type` 从保留的 `SnapshotChanged` 增量 publication 队列中选择**精确目标 publication**，再用该 publication 的 `PlayerSnapshot` 做严格 `before + 1` 断言。保留 `+1`，未改成 `>=`，未删除断言，未加 sleep/重试、未禁用 driver/AI/deadline。
- Riichi 分支现在**先确定后续合法 publication 已发生**（`latest > target`），再选择目标 publication，证明后续推进不会把 latest 快照误当目标提交态；随后从包含后续命令的持久化 envelope 中再次按原 request/scope/version/round/seat/event 选出同一 commit，保留 136 张守恒。
- 本轮实际结果：目标类 4/4、全 LocalHost 147/147（0 failed / 0 skipped / 0 not-run）、TableRuntime 483/483、四个 TOOL_FIXTURE 走 fresh verifier 均 native 0、verifier selftest 0、变异矩阵 13 拒绝 + 1 个既有正向变异接受。全 LocalHost 的 CrashWorker 真实进程用例通过。
- **未运行**：Unity/Player/IL2CPP/设备/PG、D05、三绝技人工范围、完整 G2 场景矩阵。`V4` 仍是生产架构；`G2–G5` 未批准。未 push/PR/MR/merge/deploy/scheduler/complete.flag，未修改桥接状态或锁。调用方独立批准后才可集成到 D05 最终候选。

## 2. 工作树、输入与身份

| 项目 | 精确值 |
|---|---|
| 工作树 | `E:/majiang-review/g2-d03-ci-confirmation-ds-20261102-1812` |
| 分支 | `wt/v5-g2-d03-ci-confirmation-ds-20261102-1812` |
| 修复前 HEAD | `957ad475e90a99e83fd7c413c0cbe6e30c00620f`（tree `927e56cedb61cc149cf8182b73ccfeeb61af3207`） |
| 代码修复提交 | `85b14fe74c7fed5025f4b5e3adc4eb1b826e4b4b`（tree `ec43f7fb3f3effe9472137008f25649246c80c33`；父 `957ad475`） |
| 报告提交 | 紧随 `85b14fe7` 的 docs-only 提交；最终 HEAD 以 `git log -1` 为准 |
| 已接受起点 | `origin/v5` = `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，快进到已接受增量 `957ad475e90a99e83fd7c413c0cbe6e30c00620f` |
| PR 实际 checkout | `f4ce7c924ed54d133cca44258b308e370a058268`（父 `8fd7c6c`、`957ad475`）；调用方已 fresh 取回并验证其 tree 与 `957ad475` 同为 `927e56ce…`。本任务只在本工作树执行，不把 API `headSha=957ad475` 与 PR 实际 checkout 混同 |
| 本机 dotnet | `D:/Unity/dotnet/dotnet.exe`，SDK `8.0.426` |
| CI dotnet | `8.0.427`（CI summary 记录）；本地/CI SDK 小版本差异不是本缺陷原因，缺陷由客户端“latest vs target”语义和调度时序解释 |
| 测试框架 | xUnit v3 `4.0.2`；测试 `net8.0`、库 `netstandard2.1`、C# 9 |
| NuGet | seed 只读源 `E:/majiang-review/caller-c04-d03-r4-8c2e719a/build/caller-review/shared-seeded-packages`；本树隔离 `NUGET_PACKAGES=$ROOT/nuget` |
| 独占输出根 | `E:/majiang-review/g2-d03-ci-confirmation-ds-20261102-1812/build/79178b1f6705414e80ea01fa3dd4aa4d` |

允许的 tracked 修改只有两处：`dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerActionChannelTests.cs` 与本文档。`git diff 957ad475..85b14fe7` 只含测试文件；生产、其他测试/runner/verifier、Unity、协议、D05 三十路径、census、工作流与项目注册均未改。

## 3. 缺陷清单（本轮唯一组）

```text
[ID] D-CI-01
严重级别：MAJOR
位置：dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerActionChannelTests.cs:150（修复前）
      fixed_player_action_scenario_is_legal_and_applies_over_the_real_local_channel
      -> WaitForVersionAsync(session, before.StateVersion) -> session.PlayerSnapshot
复现：
  原件：E:/majiang-review/scheduled-v5-20261102-181203-9bf382e7378743a7accec80d8c6d897b/
        ci-41423059742/v5-verify-logs-41423059742-1/verify-v5/unified/
        direct-xml/Mahjong.LocalHost.Tests/direct-results.xml
  本地确定性反事实探针（临时保留“读 latest”旧路径，强制 Riichi 后续 publication 先发生）：
    D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll
      -class Mahjong.LocalHost.Tests.C04PlayerActionChannelTests -noLogo -automated
      -result-xml build/79178b1f6705414e80ea01fa3dd4aa4d/c04-oldpath-fail.xml
实际：
  CI 41423059742 unified FAIL / native 1；LocalHost 147 total / 146 pass / 1 fail / 0 skip；
  唯一失败为 Riichi，消息 “The first real submitted command must advance the pre-seeded checkpoint exactly once.”；
  其余 13 个测试套件 PASS；push CI 41423049035 为 LocalHost 147/147、统一 PASS。
  本地未改测试连续 60 次通过，说明原缺陷是时序窗口型，不能以本地全绿否定 CI 原始失败。
  反事实探针：Riichi 在目标 publication 之后已出现后续 publication，latest > target，旧“读 latest +1”路径
  确定性失败；同次探针另有一个动作也因 latest 选择出现游标断言失败。
期望：
  Applied receipt 对应的目标 commit 必须恰好推进一次（before+1）；目标事件/seat/round、同一目标 publication、
  以及持久化 envelope 中该 request/scope/version/event 的唯一 commit 必须绑定同一份原件。
  后续合法推进发生后，仍必须选择该目标 publication/commit，不得用 latest 快照替代。
证据：
  CI 原件 SHA-256 816F7F0BAE33076DA606F9C2CD7753AF161B9D254F80217554462506ECCB0CDC；
  direct-Mahjong.LocalHost.Tests.log SHA-256 D4FEBEF958E4BF6A685D5E31FD3B4F90E30CBD18EFE6504AC9AF9F6A19172147；
  summary.log SHA-256 74CB171CBAA0D7B3D356AF7EB299D323D836CE0BCBC0444B464F2FD9DA7363C4；
  本地反事实 XML SHA-256 87624345DEAB5731E93CD2F0CAD16A4EC815370471938BBC94EA6BE8D2CA43CF。
修复要求：
  1) 不得把 +1 放宽为 >=、不得删断言、不得只加 sleep/重试、不得禁用 driver/AI/deadline 使失败消失；
  2) 以 receipt.CommandVersion 选精确 publication（同时校验 round、增量 kind、seat、事件类型/命令版本）；
  3) 先证明 Riichi 后续合法 publication 已发生，再选目标，证明 latest 不再参与目标身份；
  4) 从包含后续命令的持久化 envelope 中按原 request/scope/version/round/seat/event 重选唯一 commit；
  5) 保留并强化既有 request/scope/version/event 负例，补 round/seat/event-type 负例与 136 守恒；
  6) 仅测试等待/目标选择与必要诊断；若事实指向产品缺陷则停在诊断并报 BLOCKED，不改 production。
验收断言：
  a) 目标类 4/4、exit 0；
  b) 全 LocalHost 147/147、0 failed/skipped/not-run、exit 0；
  c) TableRuntime 483/483、exit 0；
  d) 四案 fresh TOOL_FIXTURE 经 C04PlayerActionVerifier native 0；
  e) Riichi 目标 publication 的 StateVersion == ReceiptCommandVersion == before+1，且 latest publication > target；
  f) 目标 publication 在后续推进后仍唯一存在且为同一引用；
  g) 136 张物理实体完整/唯一，所有负例均抛 InvalidOperationException。
```

同类缺陷只有这一组：本轮未发现第二个独立缺陷；CI 的 LocalHost 其余 146 项、其余 13 个套件均通过，且本任务固定分母不扩展 G2 场景。

## 4. 根因分析（真实生产通道时序与已保留记录）

### 4.1 生产通道

1. `LocalTableRuntime`/`TableActor` 提交一条命令后，按该 commit 生成 `TableStreamFrame`；`LocalTableRuntime.OnFrame` 把每个 frame 编码为独立 `PUSH` 并进入本地 loopback 队列。
2. `TableSession.ApplyPush` 对每个被接受的增量 push 执行：先投影事件，再写 `_playerSnapshot = player`、`_confirmedStateVersion = player.StateVersion`，最后 `RaiseSnapshotChanged(cursor, isFullSnapshot, batch)`。
3. 因此 `session.PlayerSnapshot` 的语义是**客户端已应用的最新快照**，不是“某 receipt 对应的目标 commit 快照”。每个被接受的增量 publication 都会各自触发 `SnapshotChanged`，测试保留的 `visiblePublications` 队列因此同时保留目标与后续 publication。
4. `ReceiptResolved` 绑定的是 durably committed 的 `CommandVersion`/`EventSeq` 高水位；Applied receipt 本身不承诺 `session.PlayerSnapshot` 仍停在目标版本。
5. Riichi 场景 `AutoPlay=true`，driver 在“立直宣告”commit 之后继续推进一条合法后续命令。若 pump 在测试读取 `session.PlayerSnapshot` 前已应用该后续 publication，latest 就是 `target + k`（k≥1），旧 `before+1` 断言失败。

### 4.2 为什么此前专项会通过

- 旧路径 `WaitForVersionAsync` 只等到 `snapshot.StateVersion > before`；它既可能先看到目标，也可能先看到目标之后的 publication。CI 的调度窗口让后续 publication 先落入了 `session.PlayerSnapshot`，本机 60 次连续运行没有触发该窗口。
- 此前真实专项通过是真实结果，但“通过”只证明当时序恰好停在目标时 `+1` 成立；它没有独立证明“latest 等于目标”。因此本轮不推翻专项，只修正该未证明的时序假设。
- 已接受的 D03 证据本身已经把“目标事件 seq”和“receipt high-water”分开，并把“精确持久化 Commit”与“latest checkpoint”分开；本轮把同一原则落实到测试等待与目标 publication 选择。

### 4.3 是否存在产品缺陷

- 未发现产品缺陷：目标 commit 的 publication 仍然被保留并可由 receipt 的 version/round/seat/event 唯一识别；后续合法命令只会产生新的 publication，不会覆盖目标 publication。
- 因此修复只落在测试：不修改 `TableSession`、`LocalTableRuntime`、`LocalTableDriver`、协议或任何 production 文件。若后续出现目标 publication 被丢弃、被合并或目标 commit 被后续 commit 覆盖的证据，应另立产品缺陷并停在可复现诊断，不以放宽断言解决。

## 5. 修复映射（逐条）

| 缺陷 | 修复 | 位置 |
|---|---|---|
| D-CI-01 目标身份 | 删除 `WaitForVersionAsync` 的 latest 选择；新增 `WaitForAppliedPublicationAsync`，按 `commandVersion + roundId + Incremental + batch.StateVersion + event.CommandVersion/Seat/Type` 精确匹配 | `C04PlayerActionChannelTests.cs` |
| D-CI-01 时序假设 | Riichi 在选目标前先 `WaitForLaterPublicationAsync`，断言 `later.StateVersion > target` 且 `session.PlayerSnapshot.StateVersion > target`；随后仍从保留队列选目标 | 同上 |
| D-CI-01 严格一次 | 保留 `Assert.True(before.StateVersion + 1UL == after.StateVersion, "…exactly once.")`，并新增 `Assert.Equal(targetVersion, after.StateVersion)`；`after`/`actionBatch` 均来自目标 publication | 同上 |
| D-CI-01 publication 唯一性 | 新增 `Assert.Single(visiblePublications, 目标 version/event)`；Riichi 后续推进后再次 `Assert.Single` 并用 `Assert.Same` 确认目标 publication 未被替换 | 同上 |
| D-CI-01 持久化 commit | Riichi 在后续 capture 上再次调用 `ReadCommittedActionPhysicalAudit`，以原 request/scope/baseline/target/event/seat 选唯一 commit；保留 136 行审计 | 同上 |
| 负例完整性 | 保留 foreign-request、foreign-scope、wrong-version、wrong-event-seq、tampered-fingerprint；新增 wrong-round、wrong-seat、wrong-event-type | 同上 |
| 死代码 | 删除不再使用的 `visibleEvents` 队列与 `WaitForActionEventAsync`；保留 `WaitForDecisionAsync`、`WaitForReplacementDrawAsync` | 同上 |

未改 production、未改 verifier、未改 runner、未改项目注册、未改 D05 路径。没有 skip、空测试、`Total=0`、旧 DLL/旧产物代替本轮结果。

## 6. 验证命令、退出码与真实计数

所有命令均使用 `D:/Unity/dotnet/dotnet.exe`，`$ROOT` = `E:/majiang-review/g2-d03-ci-confirmation-ds-20261102-1812/build/79178b1f6705414e80ea01fa3dd4aa4d`，并设置隔离的 `TEMP/TMP/DOTNET_CLI_HOME/NUGET_PACKAGES` 与 `DOTNET_ROOT=D:/Unity/dotnet`。

### 6.1 fresh 构建

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj `
  -c Release -t:Rebuild --source <seed> -nr:false
# native exit 0，0 warnings / 0 errors
```

构建输出同时生成正常路径的 `Mahjong.LocalHost.Tests.dll`、`Mahjong.LocalHost.CrashWorker.dll`、`Mahjong.TableRuntime.Tests.dll`；未使用仅 artifacts-path 的输出，避免历史 13 项辅助 DLL 假失败。

### 6.2 目标回归

```powershell
$env:C04_ACTION_VERIFIER_FIXTURE_DIR = "$ROOT/action-fixtures-final2"
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -class Mahjong.LocalHost.Tests.C04PlayerActionChannelTests -noLogo -automated `
  -result-xml "$ROOT/c04-target-final3.xml"
```

`total=4 passed=4 failed=0 skipped=0 not-run=0`，native exit 0，XML SHA-256 `B29A15DEF93830664AEA6C0838F3BA7E4CA6875A04609307F301BF7823492F4C`。

### 6.3 全 LocalHost

```powershell
$env:C04_ACTION_VERIFIER_FIXTURE_DIR = "$ROOT/action-fixtures-full"
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -automated -failSkips -result-xml "$ROOT/localhost-full.xml"
```

`total=147 passed=147 failed=0 skipped=0 not-run=0`，native exit 0，XML SHA-256 `E7B9214038AFC25FCE42DAF105D21CE1252BED97AEAE96E76986019D79B9CDE9`。`-failSkips` 生效，未以 skip 换绿。

### 6.4 TableRuntime 辅助闭包

```powershell
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll `
  -noLogo -automated -failSkips -result-xml "$ROOT/tableruntime-full.xml"
```

`total=483 passed=483 failed=0 skipped=0 not-run=0`，native exit 0，XML SHA-256 `19FF57DB8B0E03EA4273B26B9425790EE3AB66E73F7CD94BF2E9DF2123A3F81D`。

### 6.5 CrashWorker 真实进程辅助闭包

全 LocalHost XML 中以下真实子进程用例均 `Pass`，未跳过：

- `LocalHostRecoveryTests.CrashWorkerRealProcessKillLeavesDurableReceipt`
- `LocalHostRecoveryTests.CrashWorkerRealProcessKill_NonInitialCheatLeavesDurableCheatState`
- `LocalHostRecoveryTests.CrashWorkerRealProcessKill_SettlementOutboxLeavesDurableOutbox`
- `C04RecoveryRequestReconciliationTests.ForceKilledProcess_ReconcilesOriginalRequestWithoutDuplicateSideEffect`

CrashWorker 正常路径 DLL 为 `dotnet/tests/Mahjong.LocalHost.CrashWorker/bin/Release/net8.0/Mahjong.LocalHost.CrashWorker.dll`，SHA-256 `5B8F8F912F9A0B4A0D9D6C49F7BC7E6F535FD85B75D2F7428109344BECA4E77C`。

### 6.6 四真实通道 TOOL_FIXTURE

全 LocalHost 运行在 `$ROOT/action-fixtures-full/` 生成四份真实通道夹具；随后 fresh `-t:Rebuild` 构建 `C04PlayerActionVerifier` 并逐案执行：

```powershell
D:/Unity/dotnet/dotnet.exe exec dotnet/tools/C04PlayerActionVerifier/bin/Release/net8.0/Mahjong.C04PlayerActionVerifier.dll `
  verify "$ROOT/action-fixtures-full/C04-G2-01.json"
# C04-G2-02 / 03 / 04 同理
```

四案均输出 `C04_PROTOBUF_AND_AUTHORITY_IMAGE_PASS`，native exit 0。夹具 SHA-256 见封存清单。

| 案 | Baseline | Receipt/目标 | 目标事件 | Receipt event high-water | After snapshot | 136 |
|---|---|---|---:|---:|---:|---|
| C04-G2-01 Pon | 15 | 16 | PonDeclared seq 2 | 2 | 16 | 136/136 |
| C04-G2-02 Chi | 15 | 16 | ChiDeclared seq 2 | 2 | 16 | 136/136 |
| C04-G2-03 Riichi | 0 | 1 | RiichiDeclared seq 1 | 3 | 1 | 136/136 |
| C04-G2-04 ClosedKan | 0 | 1 | KanDeclared seq 2 | 3 | 1 | 136/136 |

Riichi 的关键点：目标事件 seq 1 与 receipt high-water 3 分开；`AfterStateVersion=1` 是**目标提交态**，不是后续 latest（测试已断言 latest > 1）。四案的 `AfterStateVersion` 均等于 `Baseline+1`，物理审计均为 136 行。

### 6.7 Verifier 自身负例

```powershell
D:/Unity/dotnet/dotnet.exe exec <verifier> selftest
D:/Unity/dotnet/dotnet.exe exec <verifier> make-mutations "$ROOT/action-fixtures-full/C04-G2-01.json" "$ROOT/verifier-mutations"
# 对每个变异 JSON 执行 verify
```

- `selftest` native 0：protobuf round-trip、2 个非法编码拒绝、canonical digest、错误 digest 拒绝。
- 14 个变异中 13 个 native 1（拒绝），1 个 `counter-distinct-from-cursor-model` native 0（既有正向变异）；未把负例改成通过。
- 本任务未修改 verifier；它只作为独立工具复核四份真实通道夹具。

### 6.8 测试内负例矩阵（统一）

| 负例 | 传入 | 期望 |
|---|---|---|
| foreign request | `requestId="foreign-request"` | `InvalidOperationException` |
| foreign scope | `scopeId="foreign-scope"` | `InvalidOperationException` |
| wrong version | `commandVersion=target+1` | `InvalidOperationException` |
| wrong event seq | `eventSeq=targetEventSeq+1` | `InvalidOperationException` |
| wrong round（新增） | `roundId=before.RoundId+"-foreign"` | `InvalidOperationException` |
| wrong seat（新增） | `actionSeat=1` | `InvalidOperationException` |
| wrong event type（新增） | `TileDiscarded` 代替动作事件 | `InvalidOperationException` |
| tampered fingerprint | `NextStateFingerprint ^= 1` 后重编码 | `InvalidOperationException` |
| exact target | 原 request/scope/version/round/seat/event | 返回 136 行，`AssignedCount=136`、`Unassigned=0` |

### 6.9 首次失败与反事实

- **首次失败保留**：CI 41423059742 的 `direct-results.xml`（SHA `816F…`）、`direct-Mahjong.LocalHost.Tests.log`（SHA `D4FE…`）、`summary.log`（SHA `74CB…`）未改写。
- **本地反事实探针**：临时把目标选择恢复为旧“读 `session.PlayerSnapshot` 最新值”路径（只用于证明缺陷，不进入提交），在强制 Riichi 后续 publication 已发生后运行目标类；Riichi 确定性失败，消息与 CI 一致。该探针 XML/日志为 `c04-oldpath-fail.xml` / `.log`，SHA 见清单；随后已恢复并 `-t:Rebuild`，最终提交不含探针代码。
- 本地未改测试连续 60 次通过（`repro/run-01..60`），如实记录为“原缺陷是时序窗口型”，不把本地 60 次通过当作 CI 失败不存在。

## 7. 源码、工具与产物身份

| 项目 | 身份 |
|---|---|
| 修复前 HEAD | `957ad475e90a99e83fd7c413c0cbe6e30c00620f`，tree `927e56cedb61cc149cf8182b73ccfeeb61af3207` |
| 代码提交 | `85b14fe74c7fed5025f4b5e3adc4eb1b826e4b4b`，tree `ec43f7fb3f3effe9472137008f25649246c80c33` |
| 测试文件 git blob | `7da5617aca614fd14691ec46ed57f0f6e7d4e900`（与工作树一致） |
| 测试文件 SHA-256 | `D92AA29F6D1AD8D4D226E23FAB7236266D077772850E65B08BCE7253DBF4A3F3` |
| LocalHost 测试 DLL | `9DFF27FF3F7D16D1CECE4924422F2519CADE7683334E51A76C34CEE3E937D462` |
| C04 verifier DLL | `C814ABB3942DA9A3CEB599E15517209688F2D4918856C98D990729407CB95663` |
| CrashWorker DLL | `5B8F8F912F9A0B4A0D9D6C49F7BC7E6F535FD85B75D2F7428109344BECA4E77C` |
| dotnet SDK | `8.0.426`（本机）；CI `8.0.427` |
| xUnit | `4.0.2` |
| 锁/共享资源 | 未使用 Unity/PG/设备资源；未启动 Player，未持共享 Unity 锁 |

代码 diff 已导出为 `$ROOT/code.diff`；`git diff --check 957ad475..85b14fe7` 无输出，`git status --porcelain` 在代码提交后为 clean。

## 8. 未覆盖与边界

| 项目 | 状态 |
|---|---|
| Unity / Player / IL2CPP / Android / 设备 | **NOT_RUN**；本任务测试-only，不重开 D03 已接受 Player 专项 |
| D05 三十路径 / D05 冻结基线 / 其 worktree | 未触碰；未写入 |
| 三绝技人工范围 | 未触碰 |
| 完整随机麻将 / 完整 G2 场景矩阵 | 未运行；固定分母仅本 CI 缺陷 + 原四动作回归 |
| PG / WSS / LobbyService / 网络多进程 | 未运行（不属于本任务闭包） |
| push / PR / MR / merge / deploy / scheduler / complete.flag / 桥接状态/锁 | 未执行 |
| G2–G5 阶段 | 未批准；V4 仍为生产架构 |

## 9. 封存清单

证据根：`E:/majiang-review/g2-d03-ci-confirmation-ds-20261102-1812/build/79178b1f6705414e80ea01fa3dd4aa4d`。完整 SHA-256/长度清单见 `$ROOT/manifest-sha256.txt`，包括：

- `task-card.txt`、`code.diff`、`committed-test.cs`
- `build-before.stdout.txt`、`build-final-rebuild.stdout.txt`
- `c04-target-final3.xml` / `.log`、`localhost-full.xml` / `.log`、`tableruntime-full.xml` / `.log`
- `c04-oldpath-fail.xml` / `.log`（临时反事实探针，保留首次失败）
- `verifier-build.stdout.txt`、`verifier-selftest.log`、`verifier-mutations-create.log`、逐变异日志
- 四份 `action-fixtures-full/C04-G2-0{1..4}.json`
- 外部 CI 原件路径与 SHA（不复制、不改写）

封存后不再写已哈希产物；代码提交与报告提交分离，报告提交为 docs-only。

调用方复核建议：先核对 `git log`/`git diff 957ad475..HEAD` 只含测试与本报告；再按 §6 命令复跑目标类与全 LocalHost；最后独立决定是否将本修复集成到 D05 最终候选，并保留其冻结基线不变。

## 10. 调用方独立复核（2026-11-02）

Codex 在本工作树对 `50278cfb6c944810ac4be7755fb51d546b60486b` 独立检查代码、真实通道时序及交付原件，接受本任务唯一缺陷组 **D-CI-01** 的修复。相对 `957ad475` 只有指定测试与本文档变更，严格 `before + 1`、同 publication 快照/事件、原 request/scope 的持久化 commit 绑定和 136 张守恒均保留；无 production、D05、runner/verifier 或平台范围修改。接收方封存清单的 29 项长度与 SHA-256（含外部 CI 失败原件）全部匹配；反事实 XML 保留 Riichi 原断言失败，不改写为成功。

调用方使用 `D:/Unity/dotnet/dotnet.exe` 8.0.426、独占 NuGet/TEMP/CLI 目录和正常 bin 路径，实际执行 LocalHost `build -c Release -t:Rebuild --source <既有只读 seed> -nr:false`，并直接运行 fresh xUnit DLL（`-noLogo -automated -failSkips -result-xml <独占 XML>`；定向额外指定 `-class Mahjong.LocalHost.Tests.C04PlayerActionChannelTests`）。构建 native 0、零警告/错误；定向 **4/4**、全 LocalHost **147/147**、TableRuntime **483/483**，均 native 0、零 failed/skipped/not-run。四项真实 CrashWorker/ForceKilledProcess 用例均 Pass。

调用方 fresh Rebuild C04PlayerActionVerifier 后，对本次 LocalHost 新生成的四份 `TOOL_FIXTURE_LOCALHOST_CHANNEL` 逐案 `verify` 均 native 0；`selftest` native 0；`make-mutations` 的 13 个负例均 native 1，既有 `counter-distinct-from-cursor-model` 正例 native 0。Riichi 原件为 baseline 0、receipt/目标/after 1、`RiichiDeclared` seq 1、receipt high-water 3、物理实体 136/136；用例实际先确认 later/latest > target，再选原目标 publication。

调用方证据根：`build/reviewer-ci01-1e09d9c8f1d540e0958b04b20e71e034/`。命令/退出见 `*.args.json`、`*.exit` 与日志，构建命令亦见本节；结果见 `summary.json`、`verifier-results.json`，fresh DLL 身份见 `artifacts.json`。关键 XML SHA-256：

| 原件 | SHA-256 |
|---|---|
| `target-final.xml` | `8C210F039B0308FB5BAAC1712C1708B356E3C4D72BF34A77063377EC7ADFA74A` |
| `localhost.xml` | `36781C4F0E8DFA1A6C5999BA494583C93853DF5A3D55818FD491520E8B1B14B1` |
| `tableruntime.xml` | `D64FDBE191E57C7EDFBC57B2330CE721614D0A5A3482C1EB8D958499CE1E8370` |
| `manifest-sha256.txt` | `14216C4740A1A068EC377D5173450C7CE4BAF045CB423C10A67CF97B4D0EBB09` |

调用方首次脚本文件调用受本机执行策略限制，未启动测试；随后 scriptblock 调用的第一次定向 XML 为 4/4，但包装器未获得 native exit，保留为 UNKNOWN。修正自有 Process handle 保留及非空 exit 检查后，以独立 `target-final` 原件重跑得到 native 0；未修改测试或断言，原记录保留。

本结论仅接受测试侧 CI 缺陷修复；未新执行 Unity/Player/IL2CPP/设备/PG 或完整 G2 矩阵，不认证新 publication 的远端 CI，不修改 D05 冻结基线。发布沿用现有 Draft PR20 `feat/v5-post-g1-batch`，不另开子任务 PR、不合并 v5、不部署或修改 scheduler；V4 仍为生产架构，G2–G5 未批准。
