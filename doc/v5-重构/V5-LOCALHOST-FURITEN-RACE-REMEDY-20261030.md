# V5 LocalHost Furiten 竞态测试补救（2026-10-30）

范围：只针对 PR20 未验收候选中的
`LocalHostCandidateAcceptanceTests.ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw`
（下称“目标用例”）提出并实验评估一个**确定性测试补救**。本报告基于已归档候选
`53cd8c3a5a85d84ebf8964634b0018b93412c262` 与已接受诊断
`V5-PR20-E22-CI-DIAGNOSIS-20261030.md`，不把候选代码合入分支，不修改本工作树的
生产代码、测试、verifier、solution、Unity 资产、其他工作树、全局信任、PR 或 remote。

**结论先行：** 目标用例的失败机制是“测试读取持久化 furiten 时，LocalHost 自动驱动仍可推进
并让 seat 0 摸牌”，已由既有诊断的受控探针证明；本报告把该机制转换为可重复的测试顺序，
给出**仅改一个测试文件、28 insertions(+)/8 deletions(-)** 的最小补丁。补丁不新增生产 API，
使用候选已存在的测试缝（`AutoPlay`、`DriverForTest`、`DriverRunning`）。在本机实验中：

- 候选原样目标用例 **20/20 PASS**；
- 候选 + 注入 750 ms **3/3 FAIL**，复现同一断言；
- 补救后目标用例 **20/20 PASS**，补救 + 750 ms **10/10 PASS**；
- 补救后 LocalHost 全量 **115/115 PASS**，0 failed / 0 skipped；
- 移除“停驱动”的阴性对照 **3/3 FAIL**（`state` 5→7）；
- 丢失持久化位、取消摸牌清除的两次行为变异各 **3/3 FAIL**，分别命中冷重启正向断言与
  下一次摸牌负向断言。

这些结果只证明**本机受控实验中的测试补救**，不证明 CI 当次精确交错，不证明 CI 已修复，
也不构成 PR20、G2/G3/G4/G5 或任何 gate 的通过。V4 仍是生产版本；G2–G5 未批准。

---

## 1. 输入、基线与不可变事实

| 项 | 值 |
|---|---|
| 本工作树 HEAD | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| 本工作树分支 | `wt/v5-furiten-race-remedy-20261030` |
| 候选 PR20 head | `53cd8c3a5a85d84ebf8964634b0018b93412c262` |
| 候选 head tree | `36e8351d932b5f33d440ed5e147c2615620d725b` |
| 候选 head parent | `b51da98c32208393b0906c70718a391be37cca8e` |
| 候选 head 说明 | `docs(v5): correct PR20 e22 CI diagnosis attribution and path` |
| 已接受诊断文档 | `doc/v5-重构/V5-PR20-E22-CI-DIAGNOSIS-20261030.md`（候选树内） |
| 诊断文档 SHA256 | `1C6DFEC3E178E52CAEA206F64576677255F3E58232CB372D904441BCC46EFAD9` |
| 候选测试文件 blob | `d5486eaebb9a6db096130f8ae05ddc08adb3d8e5` |
| 候选测试文件 SHA256 | `4DFBE23FE73BE89A3022164458AD3C71C20B6B5EA678E3422963739CADC91B4B` |

候选代码只通过 `git archive` 解出到被忽略目录：

```powershell
git archive --format=tar -o build/furiten-race-remedy-20261030/pr20-53cd8c3.tar 53cd8c3a5a85d84ebf8964634b0018b93412c262
tar -xf build/furiten-race-remedy-20261030/pr20-53cd8c3.tar -C build/furiten-race-remedy-20261030/candidate
```

归档 tar SHA256：`E7854D3A57EDA345871BF51C2E5CC11EDEAF14D10B26B85236AAEC4DA0CB3F86`。
`build/` 由仓库 `.gitignore:3` 忽略，未进入任何提交。本轮没有 merge、没有 commit 候选代码，
也没有 push、建/改 PR、触发或重跑 CI。

### 1.1 目标用例涉及的既有事实

- 测试方法起始：候选 `LocalHostCandidateAcceptanceTests.cs:903`；失败断言在候选
  `:1063`（`Assert.True(store.LoadTemporaryFuriten()[0])`）。
- `P41Harness` 未显式关闭自动驱动；`LocalTableHostOptions.AutoPlay` 默认 `true`
  （候选 `LocalTableHost.cs:61`）。
- `LocalTableRuntime` 在构造时启动 `LocalTableDriver`（候选 `LocalTableRuntime.cs:89-92`）；
  driver 每约 15 ms tick 并推进 AI 座位（候选 `LocalTableDriver.cs:96-143`）。
- seat 0 是 `LocalHuman`，但其下一巡的“摸牌”由上一枚 discard/window 命令的原子推进完成；
  `TableActor` 在 `DrawTile` 时清除该座位的 temporary furiten（候选
  `TableActor.cs:4598-4618`）。
- 因此，pass 回执提交后到 store 读取之间，driver 仍可能让 seat 1–3 摸打，最终推进到
  seat 0 摸牌，把刚持久化的 `true` 清成 `false`。

### 1.2 失败顺序（机制，不是 CI 精确交错）

```text
[测试] 提交 pass
   -> [actor] 原子提交 pass：seat 0 temporary furiten = true，save 写入 true
   -> [driver] 继续 tick：seat 1..3 摸打
   -> [actor] 原子推进到 seat 0 摸牌：temporary furiten = false，save 写入 false
   -> [测试] 打开新的 LocalTableStore 读取 -> false -> 1063 断言失败
```

已接受诊断用“pass 后延迟 750 ms”的探针复现：`state=7 furiten=False`；
“停 driver + 同样延迟”得到 `state=5 furiten=True`。该诊断同时明确：CI 当次失败瞬间的
`save`、`stateVersion` 与 driver tick 未在 artifact 中，**精确交错仍是 BLOCKED/UNKNOWN**；
同 tree 的 push run 通过也不能证明因果关系。本报告沿用这个边界，不把机制复现写成
“CI 根因已证明”。

---

## 2. 补救设计：把 driver 生命周期变成测试的显式步骤

目标不是让测试“更宽容”，而是让测试在**确定的边界**观察真实持久化状态，然后仍然用
真实 driver 完成“下一次摸牌清除 furiten”的行为验证。

### 2.1 关键顺序

| 阶段 | 候选原顺序 | 补救后顺序 |
|---|---|---|
| 打开首个 runtime | 构造时自动 `Start()` | `AutoPlay=false`，打开后显式 `Start()` |
| 等待 seat 0 的 Ron 决策 | driver 可自由 tick | 同左；seat 0 有 open decision，AI 不能越过 |
| 提交 pass 前 | 无屏障 | `StopAsync()`；断言 driver 停止且状态版本未变 |
| pass 回执后 | driver 可继续推进 | driver 已停，状态冻结在 pass 提交边界 |
| 读 save / facts | 可能读到下一次摸牌后的 false | 断言 save 的 `StateVersion` 等于 pass 的 `CommandVersion`，再断言 true |
| 冷重启 | 新 runtime 自动启动 driver，可能先摸牌 | `AutoPlay` 仍为 false；先断言恢复快照 furiten=true，再显式 `Start()` |
| 下一次摸牌 | 可能在 observation 前发生 | 显式启动 driver 后等待新决策，断言 furiten=false |

为什么 stop 放在 pass **之前**：seat 0 的 Ron 决策已经打开；此时 driver 不可能越过人类决策
去推进回合，`StopAsync()` 可以确定地等待 driver 退出。若在 pass 回执之后才 stop，driver
可能已经提交了下一次摸牌，仍是有竞态的“补救”。

为什么冷重启也保持 `AutoPlay=false`：冷重启的价值是验证“从 committed facts 恢复的
furiten 位”，必须在任何新摸牌之前读取恢复快照；读取后再启动真实 driver，验证它会在
下一次摸牌时按规则清除。这样正向（恢复后仍为 true）和负向（下一次摸牌后为 false）
都由真实行为产生，而不是由测试硬编码。

### 2.2 为什么不需要改生产代码

候选已有、且其他测试已在使用的测试缝：

- `LocalTableHostOptions.AutoPlay`（`internal`，候选 `LocalTableHost.cs:61`）；
- `LocalTableRuntime.DriverForTest` / `DriverRunning`（`internal`，候选
  `LocalTableRuntime.cs:263-269`）；
- `LocalTableDriver.Start()` / `StopAsync()`（候选 `LocalTableDriver.cs:58-94`）；
- `InternalsVisibleTo("Mahjong.LocalHost.Tests")`（候选 `LocalHost/AssemblyInfo.cs`）。

`AutoPlay=false` 的用法在 `LocalHostRecoveryTests.cs:241/318/460/634` 已有先例；
`BeforeAiSubmitForTest` 的受控停顿在 `LocalHostRecoveryTests.cs:92-142` 已有先例；
`DriverRunning` 在 `LocalHostConcurrencyTests.cs:219-223` 已有先例。因此本补丁只组合
既有测试缝，不扩大生产 API，也不改变生产默认 `AutoPlay=true` 的行为。

---

## 3. 精确最小补丁

文件：`dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs`
（唯一文件；相对候选 blob `d5486ea`，补丁后 blob `0bc67c8`）。

```diff
diff --git a/dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs b/dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs
index d5486ea..0bc67c8 100644
--- a/dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs
+++ b/dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs
@@ -903,6 +903,7 @@ namespace Mahjong.LocalHost.Tests
         public async Task ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw()
         {
             await using var harness = new P41Harness();
+            harness.Options.AutoPlay = false;
             CancellationToken ct = TestContext.Current.CancellationToken;
 
             // Same legal wall as RonCandidateAndSubmissionTest: seat 0 waits on 3p (44) and
@@ -919,7 +920,11 @@ namespace Mahjong.LocalHost.Tests
             harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);
 
             ulong versionAfterPass;
+            ulong passCommandVersion;
             (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
+            LocalTableRuntime? runtime = ((LocalTableChannel)channel).RuntimeForMatch;
+            Assert.NotNull(runtime);
+            runtime!.DriverForTest.Start();
             try
             {
                 using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
@@ -971,6 +976,15 @@ namespace Mahjong.LocalHost.Tests
                     ronDecision.Actions,
                     action => action.ActionType == (uint)ProtocolActionType.DeclareRon);
 
+                // Freeze the automatic driver while seat 0 has an open human decision. The
+                // response window cannot advance without the pass, and the stop happens before
+                // the pass commits, so no autonomous draw can clear furiten between the pass
+                // receipt and the durable-store read below.
+                ulong versionBeforeStop = session.PlayerSnapshot!.StateVersion;
+                await runtime.DriverForTest.StopAsync();
+                Assert.False(runtime.DriverRunning);
+                Assert.Equal(versionBeforeStop, session.PlayerSnapshot!.StateVersion);
+
                 var passResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                     TaskCreationOptions.RunContinuationsAsynchronously);
                 session.ReceiptResolved += (_, args) =>
@@ -1000,6 +1014,8 @@ namespace Mahjong.LocalHost.Tests
                     passResolved.Task,
                     pumpCts.Token);
                 Assert.Equal(ReceiptStatus.Applied, passReceipt.Resolution.ReceiptStatus);
+                Assert.True(passReceipt.Resolution.CommandVersion.HasValue);
+                passCommandVersion = passReceipt.Resolution.CommandVersion!.Value.Value;
 
                 for (int i = 0; i < 50 && session.PlayerSnapshot?.PrivateView?.Furiten != true; i++)
                 {
@@ -1009,8 +1025,6 @@ namespace Mahjong.LocalHost.Tests
                 PlayerSnapshot afterPass = session.PlayerSnapshot!;
                 Assert.True(afterPass.PrivateView.Furiten, "the owner projection must carry temporary furiten");
                 versionAfterPass = afterPass.StateVersion;
-                LocalTableRuntime? runtime = ((LocalTableChannel)channel).RuntimeForMatch;
-                Assert.NotNull(runtime);
                 Assert.True(runtime!.ActorForTest.FuritenForHost.IsTemporary(0));
                 Assert.False(runtime.ActorForTest.FuritenForHost.IsTemporary(1));
 
@@ -1060,6 +1074,9 @@ namespace Mahjong.LocalHost.Tests
             // latest projection facts; inspect them before any recovery path can normalize them.
             using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
             {
+                TableStateRow savedState = Assert.IsType<TableStateRow>(
+                    await store.LoadStateAsync(P41Harness.RoomId, ct));
+                Assert.Equal(passCommandVersion, savedState.StateVersion.Value);
                 Assert.True(store.LoadTemporaryFuriten()[0]);
                 Assert.False(store.LoadRiichiFuriten()[0]);
                 TableCommittedFactsSnapshot facts = Assert.IsType<TableCommittedFactsSnapshot>(
@@ -1075,24 +1092,27 @@ namespace Mahjong.LocalHost.Tests
             }
 
             // Cold restart: the furiten bit must come from the committed projection facts, not
-            // from process memory, and must survive until seat 0 actually draws.
+            // from process memory, and must survive until seat 0 actually draws. AutoPlay is
+            // still off, so the restored snapshot is inspected before any new draw can commit;
+            // only then is the real driver started to reach the clearing draw.
             (ITableChannel reopened, TableSession restored) = await OpenAsync(harness, ct);
+            LocalTableRuntime? restoredRuntime = ((LocalTableChannel)reopened).RuntimeForMatch;
+            Assert.NotNull(restoredRuntime);
+            Assert.False(restoredRuntime!.DriverRunning);
             try
             {
                 using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                 pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
                 Task pump = StartPump(restored, pumpCts.Token);
-                for (int i = 0; i < 50 && restored.PlayerSnapshot?.PrivateView?.Furiten != true; i++)
-                {
-                    await Task.Delay(20, pumpCts.Token);
-                }
 
                 Assert.True(restored.PlayerSnapshot!.PrivateView.Furiten, "cold restart lost the committed furiten fact");
-                Assert.True(restored.PlayerSnapshot.StateVersion >= versionAfterPass);
+                Assert.Equal(versionAfterPass, restored.PlayerSnapshot.StateVersion);
 
+                restoredRuntime.DriverForTest.Start();
                 PlayerSnapshot nextTurn = await WaitForDecisionAsync(restored, pumpCts.Token);
                 Assert.True(nextTurn.StateVersion > versionAfterPass);
                 Assert.False(nextTurn.PrivateView.Furiten, "the next draw must clear temporary furiten");
+                Assert.False(restoredRuntime.ActorForTest.FuritenForHost.IsTemporary(0));
 
                 pumpCts.Cancel();
                 await pump;
```

补丁 SHA256：`DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D`。
补丁后测试文件 SHA256：`448EE73F5702232447BFF6471EFAEE7C2D5D2B0786822749EDD63916EBF52BE4`。
补丁已在候选 blob 的独立 git 副本上执行 `git apply --check`，exit 0；应用后的文件与实验用
fixed 文件逐字节一致（两者 SHA256 均为 `448EE73F...`）。

### 3.1 逐项说明

1. `harness.Options.AutoPlay = false`：禁止 runtime 构造时自动启动 driver，使
   `Start()`/`StopAsync()` 成为测试的显式控制点。
2. 提前取得 `runtime` 并 `Start()`：首个 runtime 仍然使用真实 `LocalTableDriver` 推进
   AI，直到 seat 0 的 Ron 决策打开；只是启动点从构造函数移到测试代码。
3. 在 pass 前 `StopAsync()`：此时 seat 0 有 open human decision，driver 无法越过它推进；
   `StopAsync()` 等待 driver 任务退出。`Assert.False(DriverRunning)` 与
   `Assert.Equal(versionBeforeStop, ...)` 把“确实停了且没有偷偷提交”变成负向断言。
4. `passCommandVersion`：从 pass 回执取 `CommandVersion`，作为“pass 提交后的唯一状态版本”。
5. save 读取前断言 `savedState.StateVersion == passCommandVersion`：证明 store 没有看到
   后续自动摸打；这比“只读 furiten”更能区分“持久化错”与“测试读晚了”。
6. 冷重启保持 `AutoPlay=false`：先断言 `DriverRunning == false`，再断言恢复快照
   `Furiten == true` 且 `StateVersion == versionAfterPass`；此时还没有任何新摸牌。
7. 然后才 `Start()` 真实 driver，等待下一次人类决策，断言 `StateVersion` 前进且
   `Furiten == false`；同时断言 actor 的 temporary furiten 已清除。这就是原有的
   “下一次摸牌清除 temporary furiten”行为，只是顺序被固定。

### 3.2 没有做什么

- 没有修改 `P41Harness`、`LocalTableDriver`、`TableActor`、`LocalTableStore` 或其他生产代码；
- 没有删除、跳过、放宽任何既有断言；目标用例仍是 `[Fact]`，仍由真实 driver 推进；
- 没有把 `Assert.True` 改成重试循环、没有增加超时来“等绿”；
- 没有修改 verifier、solution、Unity、CI/workflow、fixture、golden 或 gate；
- 没有把候选代码合入本分支或提交。

---

## 4. 实验环境与命令

| 项 | 值 |
|---|---|
| OS | Windows 10 专业版，NT 10.0.19045 |
| CPU | 16 logical processors |
| RAM | 16,890,716,160 bytes |
| .NET SDK | `8.0.426` |
| PowerShell | `5.1.19041.6456` |
| 测试 runner | `xunit.v3` direct executable（与诊断记录的 CI `direct` 模式一致） |
| 候选根 | `build/furiten-race-remedy-20261030/candidate/` |
| 实验根 | `build/furiten-race-remedy-20261030/probe-*/` |

构建命令（每个 probe 各自执行，exit 0）：

```powershell
dotnet build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj `
  -c Release --nologo
```

目标用例命令：

```powershell
dotnet <probe>/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default `
  -method 'Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw'
```

全量 LocalHost 命令：

```powershell
dotnet <probe>/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default
```

构建结果：候选基线 `0 warning / 0 error`，38.76 s；补救后 `0 warning / 0 error`；
所有 probe 构建 exit 0。原始构建日志：

- `logs-baseline-build.txt` SHA256 `8B3A26C56D0F8C0FBF4477C34BFB399EAA7E00346BA032B42190FE04D7F1666B`
- `logs-fix-build2.txt` SHA256 `9F7D4AC7C42389FD5231291E8CF40099A52A6575E0B614AF4372666DE3C1DEE6`

---

## 5. 实验结果矩阵

“PASS/FAIL”均指实际命令 exit code 与 runner 汇总，不是预期推演。

| # | 变体 | 改动 | 运行数 | 结果 | 关键计数 / 失败点 |
|---|---|---:|---:|---|---|
| E1 | 候选原样 | 无 | 20 | **PASS 20/20** | 每次 `Total: 1, Failed: 0`；耗时 1.32–1.49 s |
| E2 | 候选 + 750 ms | pass 后、读 store 前延迟 | 3 | **FAIL 3/3（对照）** | exit 1；`Assert.True()` Expected True / Actual False；候选行 1063（延迟后行 1065） |
| E3 | 补救 | 本报告补丁 | 20 | **PASS 20/20** | 每次 `Total: 1, Failed: 0`；耗时 1.36–3.97 s |
| E4 | 补救 + 750 ms | 同上延迟 | 10 | **PASS 10/10** | exit 0；耗时 2.09–4.21 s |
| E5 | 补救但删掉 `StopAsync` + 750 ms | 阴性对照：driver 继续跑 | 3 | **FAIL 3/3（对照）** | exit 1；`Assert.Equal()` Expected 5 / Actual 7；边界断言先于 store 读取命中 |
| E6 | 补救 + 恢复时丢弃 seat 0 持久化 furiten | 行为变异 | 3 | **FAIL 3/3（对照）** | exit 1；`cold restart lost the committed furiten fact` |
| E7 | 补救 + 取消摸牌清除 | 行为变异 | 3 | **FAIL 3/3（对照）** | exit 1；`the next draw must clear temporary furiten` |
| E8 | 补救后全量 LocalHost | 同 E3 | 1 | **PASS 115/115** | `Total: 115, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`；26.414 s |

说明：

- E1/E3 的“20/20”只说明**本机**该顺序下未触发；E2 说明原测试确实存在可观测竞态，不能把
  E1 写成“无竞态”。
- E4 说明补救不是靠“更快地抢在 driver 前读”取胜：停掉 driver 后，额外 750 ms 不会改变
  结果。
- E5 说明 `StopAsync` 是必要步骤：不停止时，750 ms 足以让状态从 5 推进到 7，新增边界
  断言立即失败；这正是原测试的机制。
- E6/E7 说明冷重启正向断言与“下一次摸牌清除”的负向断言都不是装饰：对应的行为变异会
  分别让它们失败。
- E8 说明补丁没有破坏 LocalHost 其他 114 个用例；没有 skip、没有 `Total: 0`、没有旧产物
  掩盖。

### 5.1 关键日志

| 文件（相对实验根） | SHA256 |
|---|---|
| `baseline-runs/run-1.log` | `794AFBB98295A2EC61F2EDBCCD31003D17C38CB247C62C9C0C434771F1A70AAE` |
| `probe-fix-runs/run-1.log` | `0806C454A3A4D5A79EA02A74A0732F84BAEA465C4C31CBD1B23E0BD5873D91E7` |
| `probe-fix-runs/run-20.log` | `4CE9F1291C7047570ACD0AB6E2DC2FC6982653B96878084702B1D0EC162634E6` |
| `probe-fix-delay-runs/run-1.log` | `E92DA091530C8C924132A70F15C973EACACF7E73A9C5B05CD7C3B6E140FF7B0D` |
| `probe-fix-delay-runs/run-10.log` | `4AC074AEBC35F479B9A209997C074C533BAF11DD1675DD571A1F5D91F02AAB7E` |
| `probe-original-delay-runs/run-1.log` | `C05938A917A988CB2916B00620ACDF9370C7C20B11156E71D053C5884501F264` |
| `probe-mut-nostop-runs/run-1.log` | `377C88DEF07C5045367A957B603F650521A937FD24D6E3211C9532D5816A61FA` |
| `probe-mut-persist-runs/run-1.log` | `A93A93B174E66F37AC71F8F4995C299B81D8B9E443D7BFE2D59AE6C1AA3742E3` |
| `probe-mut-clear-runs/run-1.log` | `9F2EFF0E4585285EA22C6B3D608900465D355F7FFAF8FCB30F3A5C5C87E3E7CA` |
| `logs-fix-full-suite.txt` | `290A71972C5C122B937914B8E9C5E93711A3A4D5BBFAFE915DC438B879EE96C2` |

汇总 CSV：

| 文件 | SHA256 |
|---|---|
| `baseline-runs/results.csv` | `D0378842E8D3B52548E13F92E7A1282F0E952271B874F61A829FA7EA44E3627B` |
| `probe-fix-runs/results.csv` | `EB17CEFFFA684700B201DB6E41021C02737CBB2FCE88EB1E1786CE816E69C9FE` |
| `probe-fix-delay-runs/results.csv` | `B4E130EE9865B90963D462003657D7C8CC7AA845D0F7852F754DD118169FEFA0` |
| `probe-original-delay-runs/results.csv` | `77903B04E1DBC0AA6C15572822A545A1FC8F715F118AF9B45888C3E60BCE757C` |
| `probe-mut-nostop-runs/results.csv` | `39EB6178737933182561506E3BB816FCEF5EC533E61CF6C21437610F880885BA` |
| `probe-mut-persist-runs/results.csv` | `CB652D002A4C8272DF3B1EA2F2F8EE3F6370D1C42CE68444950432AA4C2D8CDB` |
| `probe-mut-clear-runs/results.csv` | `3D6EA9B4D40B44E67BCE2ABF51CD756910813D300FA672514116EB0825499F68` |

完整 88 项清单见 `SHA256SUMS.txt`，其自身 SHA256 为
`AFED9D716933E921A4405723382885ABEE79CC483D0A89378C93442B87E94CB7`。

### 5.2 变异探针的精确内容（只存在于 ignored probe）

这些变异只用于灵敏度验证，**不是提议的补丁**，也不得进入候选或生产：

1. **E5 无停止**：删除 `StopAsync`/`DriverRunning`/版本相等三行，保留
   `AutoPlay=false` 与显式 `Start()`，在 forged Ron 后加 750 ms。失败：
   `Assert.Equal() Failure: Values differ / Expected: 5 / Actual: 7`。
2. **E6 丢失持久化位**：在 `TableActor` 恢复路径把
   `FuritenFromProjectionFacts(...).WithTemporary(0, false)` 安装进去，模拟冷恢复丢掉
   seat 0 temporary furiten。失败：`cold restart lost the committed furiten fact`。
3. **E7 取消摸牌清除**：注释 `TableActor` 中
   `next = next.WithTemporary(committed.Seat, false)` 与
   `next.WithTemporary(seat, false)` 两处。失败：
   `the next draw must clear temporary furiten`。

E6/E7 的失败证明：补丁中的正向/负向断言对真实行为变化敏感，不是“为了变绿而加的空断言”。

---

## 6. 可靠性、灵敏度与限制

### 6.1 可支持的结论

- 补救后的目标用例在 20 次连续运行中 20/20 通过；在额外 750 ms 的 10 次运行中 10/10
  通过。说明该顺序对“driver 在 pass 后继续跑”的时序不再敏感。
- 不停止 driver 的阴性对照 3/3 失败，且失败值正好是 `5 -> 7`，与诊断机制的
  “pass 后下一次摸牌”一致。说明补救的关键不是延迟，而是边界。
- 冷重启正向断言与下一次摸牌负向断言分别被两个行为变异 3/3 打穿。说明断言保留了真实
  furiten 行为，而不是只验证“能读到某个布尔值”。
- 全量 LocalHost 115/115 通过，说明该测试级顺序调整没有影响同程序集其他用例；没有
  新增 skip 或减少用例。

### 6.2 不能支持的结论

- **不证明 CI 失败根因**：本机没有 CI 当次的 save/state/tick；精确交错仍是
  BLOCKED/UNKNOWN。E2 只证明同一断言在同一机制下可复现。
- **不证明 CI 已修复**：CI 没有重跑；本报告没有触发、取消或修改任何 run。
- **不证明其他环境稳定**：只在 Windows 10、.NET 8.0.426、16 logical CPU 的本机执行；
  没有在 CI runner、容器、单核、强 CPU 争用或不同文件系统下复测。
- **不证明生产行为改变**：补丁只改测试；生产 `AutoPlay` 默认仍为 `true`，driver 的
  启动/停止语义未改。
- **不证明 PR20 可接受**：候选仍未验收；本报告不合并、不提交候选代码，不改变 PR20 状态。
- **不构成 gate**：G2–G5 未批准，V4 仍是唯一生产基线；Unity/Android/iOS/PG/WSS/
  跨物理网络均未运行。

### 6.3 残余风险

1. `StopAsync()` 的前提是“seat 0 有 open human decision，AI 不能越过”。当前 driver 与
   `TableActor` 满足这个前提；若未来 driver 允许在人类决策期间执行其它自动动作，屏障
   需要重新审查。
2. `AutoPlay=false` 使首个 runtime 的 driver 启动点晚于构造函数；初始决策由
   `TableSession` 的 sync 打开，随后显式 `Start()`。该顺序在本实验稳定，但与生产默认
   启动点不同；这是测试控制点，不是生产行为变更。
3. 750 ms 是诊断使用的量级，不是 CI 实测值；本报告用它证明“停驱动后延迟不改变结果”，
   没有把它写成 CI 的实际延迟。
4. 目标用例依赖固定牌墙；若牌墙或规则改动导致 pass 后的座位顺序变化，`StopAsync` 的
   “人类决策阻塞”前提仍需复核。
5. E5/E6/E7 是手工变异，只用于灵敏度；没有做变异测试框架的自动化覆盖。

---

## 7. 归属、集成与复核建议

**补丁归属：** `Mahjong.LocalHost.Tests` 测试所有者；只涉及
`LocalHostCandidateAcceptanceTests.cs` 一个文件。建议由 LocalHost 测试 owner 复核后，
作为独立测试修复提交；不要与生产改动、verifier 改动或 PR20 的其他候选文件混在一个提交。

**集成步骤（由 caller 决定，不在本轮执行）：**

1. 在候选或后续集成分支上应用本报告第 3 节补丁（`git apply` 或手工等价修改）。
2. 构建 `Mahjong.LocalHost.Tests`（Release），exit 0、0 warning / 0 error。
3. 目标用例连续运行至少 20 次；期望 20/20 PASS，0 failed / 0 skipped。
4. 运行 LocalHost 全量；期望 115/115 PASS，0 failed / 0 skipped。
5. 在 CI 下次运行时观察该用例；不要因为本机 20/20 就关闭诊断。若 CI 仍失败，保留失败时
   的 `build/p41-tests/**/room-1.save`、`stateVersion` 与 driver tick 证据，按原诊断的
   BLOCKED 项继续定位。
6. 复核 diff 只含目标测试文件；确认没有生产/verifier/Unity/CI 改动。

**复核断言（可直接执行）：**

```powershell
# 1) 构建
dotnet build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo
# 期望 exit 0，0 warning / 0 error

# 2) 目标用例 20 次
1..20 | ForEach-Object {
  dotnet dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
    -noLogo -reporter default `
    -method 'Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw'
  if ($LASTEXITCODE -ne 0) { throw "run $_ failed" }
}
# 期望 20/20 exit 0，每次 Total: 1, Failed: 0, Skipped: 0

# 3) 全量
dotnet dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default
# 期望 Total: 115, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0
```

**不要做的事：** 不要把 `AutoPlay=false` 扩展到生产默认；不要为了通过而删除
`Assert.True(store.LoadTemporaryFuriten()[0])`、冷重启断言或下一次摸牌断言；不要用
`Task.Delay` 替代 `StopAsync` 屏障；不要在未保留失败证据的情况下重跑 CI 并宣称修复。

---

## 8. 状态口径

| 项 | 状态 | 依据 |
|---|---|---|
| 目标用例失败机制 | **已由既有诊断 + 本报告 E2 复现** | 750 ms 后同一断言 false；E5 显示 state 5→7 |
| 本报告测试补救可构建 | **PASS** | 0 warning / 0 error |
| 目标用例（补救后，本机） | **PASS 20/20** | E3 |
| 目标用例（补救 + 750 ms） | **PASS 10/10** | E4 |
| 冷重启正向断言灵敏度 | **PASS（变异 3/3 失败）** | E6 |
| 下一次摸牌负向断言灵敏度 | **PASS（变异 3/3 失败）** | E7 |
| LocalHost 全量（补救后） | **PASS 115/115** | E8 |
| 候选原样（本机） | **PASS 20/20（但不证明无竞态）** | E1 |
| 候选 + 延迟 | **FAIL 3/3（预期对照）** | E2 |
| CI 精确交错 | **BLOCKED / UNKNOWN** | 无失败时 save/state/tick；未重跑 CI |
| CI 是否已修复 | **NOT_RUN / 未证明** | 未触发或重跑 CI |
| 远端 CI、全 solution `dotnet test` | **NOT_RUN** | 本轮只做 LocalHost 项目 |
| Unity / IL2CPP / Android / iOS / PG / WSS / 跨物理网络 | **NOT_RUN** | 不在本任务范围 |
| PR20 状态 | **未接受** | 未合并、未提交候选代码、未改 PR |
| V4 / G2–G5 | **不变** | V4 生产；G2–G5 未批准 |

---

## 9. 证据索引

实验根：`build/furiten-race-remedy-20261030/`（被 `.gitignore` 的 `build/` 忽略）。

```text
build/furiten-race-remedy-20261030/
  pr20-53cd8c3.tar                         # git archive，未 merge
  candidate/                               # 候选解出树（只读基线）
  inputs/                                  # 诊断文档、目标测试、runtime/driver/store 副本
  probe-fix/                               # 补救后源码 + 构建
  probe-fix-runs/                          # 补救 20 次日志 + results.csv
  probe-fix-delay/                         # 补救 + 750 ms
  probe-fix-delay-runs/                    # 10 次日志
  probe-original-delay/                    # 候选 + 750 ms（E2）
  probe-original-delay-runs/               # 3 次失败日志
  probe-mut-nostop/                        # E5 变异
  probe-mut-nostop-runs/
  probe-mut-persist/                       # E6 变异
  probe-mut-persist-runs/
  probe-mut-clear/                         # E7 变异
  probe-mut-clear-runs/
  artifacts/                               # 补丁、候选/修复测试副本、诊断副本
  SHA256SUMS.txt                           # 88 项证据清单
  logs-*.txt                               # 构建/全量/变异日志
```

关键文件：

| 文件 | SHA256 |
|---|---|
| `artifacts/final-test-only.patch` | `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D` |
| `artifacts/LocalHostCandidateAcceptanceTests.candidate.cs` | `4DFBE23FE73BE89A3022164458AD3C71C20B6B5EA678E3422963739CADC91B4B` |
| `artifacts/LocalHostCandidateAcceptanceTests.fixed.cs` | `448EE73F5702232447BFF6471EFAEE7C2D5D2B0786822749EDD63916EBF52BE4` |
| `artifacts/V5-PR20-E22-CI-DIAGNOSIS-20261030.md` | `1C6DFEC3E178E52CAEA206F64576677255F3E58232CB372D904441BCC46EFAD9` |
| `pr20-53cd8c3.tar` | `E7854D3A57EDA345871BF51C2E5CC11EDEAF14D10B26B85236AAEC4DA0CB3F86` |
| `SHA256SUMS.txt` | `AFED9D716933E921A4405723382885ABEE79CC483D0A89378C93442B87E94CB7` |

候选测试 blob `d5486ea...` 与归档内
`53cd8c3:dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs`
逐字节一致；修复测试 blob `0bc67c8...` 是本报告补丁的目标 blob。

---

## 10. 复核问题与边界声明

1. **这是“测试补救”还是“掩盖竞态”？** 不是掩盖：测试仍使用真实 driver，仍验证
   pass 后临时 furiten、冷重启恢复、下一次摸牌清除；只是把“读取持久化位”的时点固定在
   pass 提交边界，而不是让测试与 15 ms tick 赛跑。E5/E6/E7 证明断言仍对真实行为敏感。
2. **为什么不直接等待到 seat 0 摸牌再断言 false？** 那只能验证“最终会清除”，无法验证
   “pass 后 committed facts 仍为 true 且冷重启保留它”；会删掉本用例的核心正向语义。
3. **为什么不只在 pass 后 `StopAsync`？** 那仍然是“回执后抢时间”；driver 可能在
   `StopAsync` 返回前提交下一次摸牌。放在 pass 前、human decision 打开后，才有确定的
   不可推进边界。
4. **为什么冷重启不直接自动播放？** 自动播放会在断言前摸牌，重新引入同一个竞态；先以
   `AutoPlay=false` 读取 committed 恢复快照，再启动真实 driver，能同时覆盖“保留”与
   “下一次摸牌清除”两个方向。
5. **本报告是否证明 CI 失败是 flaky？** 否。只证明测试观测竞态机制可复现；CI 当次精确
   交错仍为 BLOCKED/UNKNOWN。不能据此写“flaky 已修复”或“候选无回归”。
6. **是否改变生产默认？** 否。`AutoPlay` 默认仍是 `true`；补丁只在测试内显式关闭/启动，
   不改变 LocalHost 生产装配。

**本报告只提交本文件。** 所有 probe、日志、补丁副本与哈希清单都留在 ignored
`build/furiten-race-remedy-20261030/`，不进入提交；本工作树的生产/测试/verifier/
solution/Unity 资产零改动。
