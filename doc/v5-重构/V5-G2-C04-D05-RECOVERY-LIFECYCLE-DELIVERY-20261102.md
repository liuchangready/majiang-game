# G2-C04-D05-R1 恢复与生命周期（任务 A）交付

日期：2026-11-02，Asia/Shanghai。执行者：DeepSeek Harness（配置默认模型 `deepseek/deepseek-v4.1-flash`，未切换模型、未再委派）。
任务：`G2-C04-D05-R1-RECOVERY-LIFECYCLE`。工作树 `E:/majiang-review/g2-c04-d05-recovery-lifecycle-ds-20261102-r1`，分支 `wt/v5-g2-c04-d05-recovery-lifecycle-ds-20261102-r1`。

结论：**A 的组件返修已完成并提交，等待专属 reviewer/父代理独立复核，供 B 集成接续。不是 D05/G2/C04 验收通过，不表示 G5 完成。** 本报告不 approve、不 push、不建/改 PR、不 merge、不 deploy、不改 scheduler。

## 0. 身份、范围与基线

| 项 | 值 |
| --- | --- |
| 任务卡基线 | `6123f0a17e8a47997e577086a125dc58da7a3d7e`（在 `90140b34` 之上只有调用方新分工卡） |
| 被审前版 | `47ecf6a6`（代码）/`390aa95f`（报告）；本轮不修改、不覆盖 |
| 本任务代码提交 | `0a2f28a1e903b08f94e95cd9996b4817e662d349` |
| 本报告 | docs-only 独立提交（提交号见最终交付消息；不改变代码提交） |
| 唯一允许 tracked 文件 | 19（见任务卡 A 节） |
| 实际修改 tracked 文件 | 6：`LocalTableRoom.cs`、`LocalTableRuntime.cs`、`C04RoomTicketTests.cs`、`AppComposition.cs`、`LocalTableChannelFactory.cs`、`LocalHostCompositionPlayModeTests.cs` |
| 未动的 13 个授权路径 | 保持原样；未修改 Domain/Client/proto/actor/schema/asmdef |
| 取证只读根 | `E:/majiang-review/caller-d05-review-20261102-r1/build/caller-review`（未写入、未覆盖） |
| 本轮独占证据根 | `E:/majiang-review/g2-c04-d05-recovery-lifecycle-ds-20261102-r1/build/6f81cf757b7348b9b3d1e38de3ab3421` |

完整复核报告 SHA 复核：`doc/v5-重构/CODEX-V5-G2-C04-D05-REVIEW-R1-20261102.md` 实测
`E1792E3D4E32268F143A6C929AFD3223074487A7D070D937515F0974890993BB`，与任务卡一致；已全文读取，未只读摘要。旧合并线程未复活，Luna 线程/树/文件未操作。

## 1. R1-02 全量缺陷与逐项处置

R1-02 为 **MAJOR**，同类合并为 5 个子项；本轮没有把已可复现问题留到下一轮。R1-01（隐私 wire 解码）与 R1-03/R1-04（矩阵/runner）属于 Luna/B，本报告不处理、不冒称关闭。本轮未发现 R1-02 之外的新产品缺陷；AI 推进的 seat0 scope 缺失是在修复过程中由新运行证据揭示的同一组根因，单列于 §1.2。

### 1.1 R1-02.1 恢复 controller/epoch 被新 SeatPlan 覆盖

- **位置**：`LocalTableRuntime.OpenCoreAsync/CreateActor`（旧版固定 seat0=LocalHuman、其余 Computer）；`LocalSnapshotFacts.CreateInitialCheckpoint`。
- **复现**：真实探针 `RecoveredAllAiDifferentSeatPlan`（源 `R/probes/Program.cs`，复制到本轮独占根后运行，未改审查原件）：
  `D:\Unity\dotnet\dotnet.exe exec build/<GUID>/probes/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll build/<GUID>/room-probes.json`
- **首次实际**：`Cold recovery failed: RECOVERY_CONTROL_INVALID ... Seat 0 changes controller without advancing controlEpoch.` 初始证据 `room-probes.json` SHA `981EC1AED33DA19A699A4B058FDDCDD633D4F98A8CF9EF5D4632531FE12FB2D4`（与复核报告一致）。
- **期望**：恢复的 checkpoint controller/epoch 优先于新的初始 SeatPlan；all-AI 房间在新 SeatPlan=AllFourHumanSeats 下仍成功冷恢复并 tick。
- **根因**：actor 初始 controller 由常量写死，checkpoint 保存的 all-Computer registry 在恢复校验中与固定 seat0 Human 冲突（同 epoch 不同 kind）。
- **修复**：恢复分支先解码 `TableControlCheckpointCodec`，用持久化 `SeatControllerKind` 初始化 `TableActorOptions.InitialControllers`；fresh 分支用 `SeatPlan` 初始化；二者与 `CreateInitialCheckpoint` 使用同一来源。
- **验收断言/结果**：真实探针 recovered all-AI 四席均 `Computer`、`actualCanTick=true`、`defectReproduced=false`；单测 `ColdRecoveryUsesPersistedAllAiControllersInsteadOfNewHumanSeatPlan` 通过。
- **证据**：`final-room-probes.json`（§4）。

### 1.2 R1-02.2 ReleaseControl 后冷恢复、仅观战绑定，已 release AI 不能推进

- **位置**：`LocalTableRoom.CanTickAuthority`、`LocalTableRuntime` 构造的 per-seat AI scope、`LocalTableDriver.SubmitAsync`。
- **复现**：真实探针 `ReleasedSeatColdReopenWithoutHumanBinding`：seat0 真人 `ReleaseControl` 回执 `Recorded/LOCAL_CONTROL`，关闭后同计划冷恢复，只绑定观战。
- **首次实际**：`recoveredSeat0Automated=true`、`epoch=1`，但 `actualCanTick=false`。后又在新增“AI 实际推进”测试中暴露第二次真实失败：`ArgumentException: A durable command identity requires scopeId, api and payloadSha256 together.`，说明即使 gate 放行，seat0 自动化后 driver 没有 seat0 的 AI scope，AI 仍不能落子。
- **期望**：released AI 无需真人 binding 即可 tick 并实际推进；旧 human binding/旧 epoch 命令被拒。
- **修复**：
  1. `CanTickAuthority` 改为当前 controller 驱动：仅 `LocalHuman/RemoteHuman` 且尚无 live player binding 的席位阻塞 `AwaitingSeats`；`Computer/Takeover` 不阻塞，观战永不计入。
  2. `LocalTableRoom.InitializeControl` 由 fresh `SeatPlan` 或 recovered checkpoint 预置；增加 `_controlInitialized` guard，确保 actor 构造期间 driver 的首次轮询不能把未初始化的全 false 种子误判为 Playing。
  3. `LocalTableRuntime` 为 **0..3 全四席**签发 AI scope（旧版只发 1..3）；seat0 变为自动化后 driver 才具备可提交的 durable identity。
- **验收断言/结果**：探针 `actualCanTick=true`；单测 `RecoveredReleasedSeatTicksWithoutHumanBindingAndKeepsEpoch`、`RecoveredReleasedSeatActuallyAdvancesAsAiWithoutHumanBinding` 通过，后者在冷恢复+仅观战后观测到 authority `StateVersion` 实际增长。
- **证据**：`final-room-probes.json`、`final-localhost.xml`；首次失败诊断 `ai-progress-diag.txt` SHA `EA5FCDE92E6A79086CB41E84DF7A1E9BFD59DA957F99CA5FFC55E7FABF258EF6`，修复后重跑 `ai-progress-diag2.txt` SHA `8E13F4C220B555897806F5DC76F4E890E24D65795EED61AE4A1B7E1741F37610`。

### 1.3 R1-02.3 controller/live binding/phase/deadline/epoch 矩阵与旧 decision/epoch

- **位置**：`LocalTableRoom.CanTickAuthority`/`CanOpenHumanDecision`；`LocalTableDriver.DriveOneAsync`；`TableActor` 现有 epoch/shape 校验（未改 actor）。
- **复现/覆盖**：新增/复用以下真实 room/channel/TableSession 用例：
  - `FreshAllHumanRoomStartsOnlyAfterEveryInitialHumanBinds`：fresh × 4 human × bound/unbound × preparing；全绑定前 false，第 4 席绑定后 true，detach 一席仍 true（不回退 AwaitingSeats）。
  - `RecoveredHumanControllerSurvivesASeatPlanThatDropsIt`：recovered × human × unbound，且新 SeatPlan=AllFourHumanSeats 不得把 recovered 的 Computer 席 1..3 改成 human；仅绑定 seat0 后即 Playing。
  - `AutoPlayDriverCannotLeaveAwaitingSeatsBeforeAControlSeedExists`：AutoPlay=true 构造函数竞态；无 binding 时 driver 不得离开 AwaitingSeats、不得打开 decision。
  - `OpenDecisionDeadlineStillRunsAfterOwnerDetachWithoutOpeningANewOne`：已有 23s deadline 在 5s 时 owner detach，再推进 19s（若 detach 重置 deadline 则到 28s，不会到期）；实际旧 decision 到期退役，且 LocalHuman 无 binding 不再新开 decision。
  - `RecoveredReleasedSeatTicksWithoutHumanBindingAndKeepsEpoch`：recovered epoch1，旧 epoch0 `ClaimControl` 返回 `STALE_CONTROL_EPOCH`，epoch1 claim 成功到 epoch2=LocalHuman。
  - 既有 `LocalControlTests.ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch` 继续运行，覆盖旧 decisionId 在 deadline 后拒绝与旧 epoch 拒绝。
- **实际 vs 期望**：修复后全部按上表通过；deadline 不重开、不延长；旧 decision/epoch 按现有 actor 规则拒绝。
- **修复要求对应**：SeatPlan 只用于 fresh 初始化和发 player ticket；运行期只认 current/recovered controller、live binding、phase、已有 deadline。未改 actor/domain/schema。
- **证据**：`final-localhost.xml` 164/164；`final-c04-room.xml` 16/16；命令与 SHA 见 §4。

### 1.4 R1-02.4 viewer 关闭隔离、默认单 viewer owner 释放、多 viewer 不受影响

- **位置**：`AppComposition.CloseRoomViewerAsync`、`AppComposition.ExecuteShutdownAsync`、`LocalTableChannelFactory`、`LocalTableRoom.Detach`。
- **实际（被审源码）**：`CloseRoomViewerAsync` 无条件从 `_roomViewers` 移除，即使 `ShutdownAsync` 返回非 Completed/quarantined；默认 factory 缓存 room，但 primary 普通 shutdown 只有 `APP_QUIT` 才 `CloseRoomAsync`，退出重进后旧 lease/driver 常驻。
- **期望**：失败/隔离关闭保留资源归属；默认单 viewer owner 正常退出释放整桌 lease/driver；多 viewer 模式只 detach 自己，其他 viewer/authority 不变。
- **修复**：
  1. 只有 `Outcome==Completed && !Quarantined && !viewer.IsQuarantined` 才从 `_roomViewers` 移除；否则保留该 viewer（其 channel/stream/dispatcher 仍归 composition 所有）。`ShutdownAsync` 抛异常时同样不会执行移除。
  2. primary session 正常 `Completed` 且 `_roomViewers.Count==0` 时，无论是否 `APP_QUIT` 都调用 `ILocalRoomViewerFactory.CloseRoomAsync`，实际 dispose room（停止 driver、关闭 actor、释放 store lease）。有显式 room viewer 时保留整桌，仍只 detach 被关闭的连接。
  3. `LocalTableChannelFactory.IsRoomOpen` 是最小只读诊断属性（不改变任何既有签名），供 PlayMode/父代理直接观测 room 是否仍被 owner 持有。
- **验证**：多 viewer detach 不伤其他 viewer/authority 仍由既有真实 `Four_players_and_spectator_share_one_room_with_owner_scoped_snapshots` 覆盖并通过；`SpectatorCloseLeavesAuthorityAndSeatControlUnchanged` 新增观战关闭不影响 seat control/authority。默认 owner 释放已扩展 PlayMode `RealLocalHost_ReturnToMenuThenReenter_RecoversSameRoom`，断言 menu 后 `IsRoomOpen==false`、再进入重新打开。
- **未运行边界**：本环境未执行 Unity PlayMode，故“默认 owner 释放”与“quarantine 保留”仍是**代码实现 + 静态审查 + PlayMode 待运行**，不写成 PASS（见 §5）。Quarantine 分支没有伪造 runtime 证据。

### 1.5 R1-02.5 ticket 过期/failed/detached 有界退役与禁止重放

- **位置**：`LocalTableRoom` ticket registry（旧版 `FailTicket` 不删除，Detached 永久留在 `_tickets`）。
- **实际**：反复 attach/detach 使字典无限增长；expired/failed/detached 均可长期保留。
- **期望**：退役有界、不可重放；重复/并发/取消仍正确拒绝；room close 清空。
- **修复**：统一 `RetireTicket`：置终态、释放 seat/spectator reservation、立即从 live registry 删除并计数；`PruneExpired`/`CancelReserved`/`Detach`/attach 竞态失败全部走同一路径。已退役 value 永远不再可查，`ConnectAsync/AttachAsync` 只能得到拒绝。并发 attach 失败不再把成功者的 Attached 误标 Failed，并 dispose 失败方新建的 connection。
- **验收断言/结果**：
  - `TicketRetirementIsBoundedAndDetachedTicketsCannotBeReplayed`：128 次 issue→attach→detach，`RetainedTicketCountForTest` 每轮回到 0、`RetiredTicketCountForTest` 精确 128；全部旧 ticket 重放均抛 `LocalTableException`。
  - `ExpiredFailedDetachedAndConcurrentTicketsAreRejectedOnceAndReusableOnlyByANewTicket`：并发 `ConnectAsync` 恰好 1 成功/1 拒绝；expired prune 后重放拒绝；取消 Open 后同一 ticket 拒绝、新 ticket 可重新预留。
  - 既有 `Ticket_reservation_is_single_use...`、`Cancelled_hello_mismatch_and_expired...`、`Spectator_limit...` 继续通过。
- **证据**：`final-c04-room.xml`。

## 2. 代码修复映射

| 文件 | 修复 |
| --- | --- |
| `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs` | recovered control checkpoint 解码；fresh/checkpoint 初始化 actor controllers；room 在 actor/driver 前 seed；四席 AI scope（含 seat0） |
| `dotnet/src/Mahjong.LocalHost/LocalTableRoom.cs` | current-controller/`_controlInitialized` 的 AwaitingSeats→Playing gate；`InitializeControl`/`MarkControlChanged`；ticket `RetireTicket` 有界退役；并发 attach 清理；`RetainedTicketCountForTest`/`RetiredTicketCountForTest` |
| `dotnet/tests/Mahjong.LocalHost.Tests/C04RoomTicketTests.cs` | 10 个真实 room/channel/TableSession 回归（见 §3） |
| `unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs` | 关闭失败/隔离保留 viewer；默认单 owner 正常关闭释放 room |
| `unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs` | 只读 `IsRoomOpen` 诊断属性 |
| `unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalHostCompositionPlayModeTests.cs` | 扩展 return-to-menu 重进用例，断言 room 释放/重开 |

公共组合接口保持兼容；唯一新增公共 surface 是 `LocalTableChannelFactory.IsRoomOpen`（只读诊断，理由：让 PlayMode/父代理能直接验证默认 owner 是否释放 room；不改变创建、ticket、session 或 lifecycle 语义）。其余新增均为 `internal` test seam，未扩大 matrix 任务的默认源码路径。

## 3. 新增真实回归（LocalHost.Tests，10 个）

| # | 用例 | 覆盖 |
| --- | --- | --- |
| 1 | `ColdRecoveryUsesPersistedAllAiControllersInsteadOfNewHumanSeatPlan` | R1-02.1 |
| 2 | `RecoveredReleasedSeatTicksWithoutHumanBindingAndKeepsEpoch` | R1-02.2/.3（release、recovered epoch、旧 epoch claim 拒绝、claim epoch2） |
| 3 | `RecoveredReleasedSeatActuallyAdvancesAsAiWithoutHumanBinding` | R1-02.2 实际 AI 推进（非只查 gate） |
| 4 | `RecoveredHumanControllerSurvivesASeatPlanThatDropsIt` | recovered controller 优先于 SeatPlan |
| 5 | `FreshAllHumanRoomStartsOnlyAfterEveryInitialHumanBinds` | fresh 全 human、preparing/started、detach 不回退 |
| 6 | `AutoPlayDriverCannotLeaveAwaitingSeatsBeforeAControlSeedExists` | actor 构造期 driver 竞态 |
| 7 | `OpenDecisionDeadlineStillRunsAfterOwnerDetachWithoutOpeningANewOne` | 已有 deadline 不重开/不延长；无 binding 不新开 |
| 8 | `SpectatorCloseLeavesAuthorityAndSeatControlUnchanged` | 观战关闭不影响 authority/control |
| 9 | `TicketRetirementIsBoundedAndDetachedTicketsCannotBeReplayed` | 128 轮有界退役/禁止重放 |
| 10 | `ExpiredFailedDetachedAndConcurrentTicketsAreRejectedOnceAndReusableOnlyByANewTicket` | 并发/取消/过期/failed 一次性拒绝 |

无 skip、无空测试、无放宽断言、无旧 DLL/旧摘要取绿。原有 154 项全部保留并继续运行。

## 4. 实际命令、native/XML/计数与 SHA

环境：`D:\Unity\dotnet\dotnet.exe` 8.0.426；Windows 10.0.19045。所有命令在任务工作树执行，退出码为 PowerShell 捕获的真实 native exit。

```powershell
# 探针（真实 room/channel/TableSession，既有 P41Harness）
D:\Unity\dotnet\dotnet.exe build build/<GUID>/probes/CallerRoomProbes.csproj -c Release -v:m --nologo
D:\Unity\dotnet\dotnet.exe exec build/<GUID>/probes/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll build/<GUID>/final-room-probes.json

# LocalHost 全量
D:\Unity\dotnet\dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release -v:m --nologo
D:\Unity\dotnet\dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -noColor -parallelMode none -result-xml build/<GUID>/final-localhost.xml

# TableRuntime 全量
D:\Unity\dotnet\dotnet.exe exec dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll `
  -noLogo -noColor -parallelMode none -result-xml build/<GUID>/final-tableruntime.xml
```

| 检查 | native exit | 计数（XML 实测） |
| --- | --- | --- |
| 探针构建 | 0 | 0 warning / 0 error |
| 探针运行 | 0 | 3 个 probe，全部 `defectReproduced=false`；正对照 `beforeHello=false/live=true/afterDetach=true` |
| LocalHost 构建 | 0 | 0 warning / 0 error |
| LocalHost 全量 | 0 | Total 164 / Passed 164 / Failed 0 / Skipped 0 / Errors 0 / NotRun 0，36.943s |
| C04RoomTicketTests | 0 | Total 16 / Passed 16 / Failed 0 / Skipped 0 / Errors 0 / NotRun 0，2.721s |
| TableRuntime 构建 | 0 | 0 warning / 0 error |
| TableRuntime 全量 | 0 | Total 483 / Passed 483 / Failed 0 / Skipped 0 / Errors 0 / NotRun 0，6.060s |
| C04RoomTicketTests 连续复跑 3 次 | 0/0/0 | 每次 16/16，无 flaky |

稳定证据（均在独占根 `build/6f81cf757b7348b9b3d1e38de3ab3421`）：

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `final-room-probes.json` | 837 | `20EA6428B64C4F5D87AC46893725727E09E602118C7A305C3568D7EDFA2CD5C7` |
| `final-room-probes.stdout.txt` | 1680 | `53895AD36961BE1B0C78FF35C0DB2590B4F55679C835F2A1F54D03DECCCD5CD3` |
| `final-probe-build.txt` | 2812 | `2DF142948E74C9156D38779C170797E60FE77FCED400CA3B0533757BD11EAFD3` |
| `final-localhost.xml` | 105098 | `E53FBD8A186A181336240372C1D3CE3EE60FF9F740B92F460786D60BFE78784B` |
| `final-localhost.stdout.txt` | 736 | `5730DA1929CF4B056A3026221881EBAF9B3CE64BAEEB1A698EDEE9E07512F4B0` |
| `final-localhost-build.txt` | 3570 | `E0C6ECE715DE7BE1FD4C5BC4787B84233D61D8B14F383D509399AB306825D960` |
| `final-c04-room.xml` | 10634 | `3A6D136763A51429C591B80727499E4BC86D310900CC4213A39AD25ED0B41D90` |
| `final-c04-room.stdout.txt` | 732 | `DD9988AE2C28D59AC23423686F477F3A1B20874CCC8D39E98E54C60F8EA37A29` |
| `final-tableruntime.xml` | 315811 | `26A0ADB3DF84379A8566A7E705FA3DF2F2E2855F3E78EBEA5ADC27D935246991` |
| `final-tableruntime.stdout.txt` | 764 | `F1384AF226BEEF7B4DA7CA37A0252C7E7540B63CE3A3D5DC264F1900A580C77C` |
| `final-tableruntime-build.txt` | 2496 | `7E22D2B1C4B5011844FF0871A63927919C144A3368284C1C3E94F9473725B8CB` |
| 初始复现 `room-probes.json` | 719 | `981EC1AED33DA19A699A4B058FDDCDD633D4F98A8CF9EF5D4632531FE12FB2D4` |
| 初始复现 stdout | 1444 | `D91456A197BB7EFD2B0E446B4B9B3635B4D1FB183069D048E4A4D0A7D2514BC3` |
| AI 推进首次失败诊断 | 3464 | `EA5FCDE92E6A79086CB41E84DF7A1E9BFD59DA957F99CA5FFC55E7FABF258EF6` |

初始复现证据与复核报告 `room-probes.json` SHA 完全一致（`981EC1…`），证明不是用旧绿测试替代复现。首次 AI 推进失败保留在 `ai-progress-diag.txt`，未覆盖。

源码/二进制身份（代码提交 `0a2f28a1e903b08f94e95cd9996b4817e662d349`）：

| 对象 | SHA-256 |
| --- | --- |
| `LocalTableRoom.cs` | `AF85D3C22EF020632CC429DF5B0B73077D171D423830F9C0E910D84CD688F1C4` |
| `LocalTableRuntime.cs` | `60D049C65C44EA9DEF83B7F3D3597F114CC1EBEDFD40C45734D1667BAC7AFA2A` |
| `C04RoomTicketTests.cs` | `C20998A5123CDCCF661E3C1A90D872EBA57F2D003D4670ECCAC1EA3D20AF7690` |
| `AppComposition.cs` | `821E11F9E23F6C07372AD1ED7F65B4746C5A7D88FCD7F16A6123A5901138CDDF` |
| `LocalTableChannelFactory.cs` | `2011886AB2CB082CEA92773FF5A922AB8E5A01262313012A6DC3542A82496E9C` |
| `LocalHostCompositionPlayModeTests.cs` | `4813BC571DF27F0DFEE406FFC9712BA0CD66B2CFA033B1E53EEA6B7854936D5A` |
| 构建 `Mahjong.LocalHost.dll` | `81EB0D46DFCDADCD286E3298CB7B034BA7F417C654AFDAFFD349E13637D99166` |
| 构建 `Mahjong.LocalHost.Tests.dll` | `1579D2E80533F4C8AA282CDA8EB7D49D9DA0C1749D78D61ECBF29E55DFD5BF98` |

## 5. 未覆盖/未运行边界（不得当作 PASS）

- **Unity PlayMode：NOT_RUN**。本环境未获取共享锁 `Local\MahjongV5P45UnityValidation`，未运行 Unity 冷导入/PlayMode；因此 `AppComposition`/factory 的 Unity 编译与 `RealLocalHost_ReturnToMenuThenReenter_RecoversSameRoom` 扩展断言**没有 runtime 证据**。`CloseRoomViewerAsync` 的 quarantine 保留分支也只有源码审查 + 既有 `SessionCoordinator` quarantine 语义，未做 composition 级超时注入。父代理/B 应在同一候选上按锁规则运行并记录 XML/native/计数。
- **Windows Test Player、Android、IL2CPP、真机、真实网络、Unity 多视图矩阵：NOT_RUN**，均不属于本组件任务；不冒称同包/同候选通过。
- **R1-01 / R1-03 / R1-04、Luna probe/verifier、B 的 N/U/P 矩阵：未处理、未改写**。D05/C04/G2–G5 仍未批准。
- 本任务未改 Domain/Client/proto/actor/schema/asmdef，未新增后台重连产品功能，未改旧任务历史，未创建/删除任何 bridge 线程。
- 未 approve/push/建改 PR/merge/deploy/改 scheduler/complete.flag。报告与代码分开提交，生成物在 `build/<GUID>` 且不入 Git。

## 6. 接续检查点

1. 专属 reviewer 复核 `0a2f28a1` 与本草稿报告的 19 路径范围、证据 SHA、无 skip/假绿。
2. 在共享 Unity 锁下运行扩展后的 `RealLocalHost_ReturnToMenuThenReenter_RecoversSameRoom`，确认 `IsRoomOpen` 三处断言；如需 quarantine composition 证据，补一个“自有 pump 不响应取消 → PumpBarrierTimeout → viewer 仍被 composition 跟踪”的 PlayMode 用例（本任务未伪造）。
3. B 在冻结候选上使用本交付的 production room/controller 边界接入 N/U/P 与 G5；旧 154/483/2496/205 证据只绑定旧版本，不能直接冒称新候选通过。
4. 原合并线程保持删除状态；Luna `thread_20261102_224144_4290` 及其六文件权限不变。

本任务 A 到此为**可复核的组件交付**，不是 D05 完成，也不替代父代理/专属 reviewer 的独立验收。
