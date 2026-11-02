# G2-C04-D05-R1 恢复与生命周期（任务 A）交付

日期：2026-11-02，Asia/Shanghai。执行者：DeepSeek Harness（配置默认模型 `deepseek/deepseek-v4.1-flash`，未切换模型、未再委派）。
任务：`G2-C04-D05-R1-RECOVERY-LIFECYCLE`。工作树 `E:/majiang-review/g2-c04-d05-recovery-lifecycle-ds-20261102-r1`，分支 `wt/v5-g2-c04-d05-recovery-lifecycle-ds-20261102-r1`。

结论：**第 2 轮返修已完成并提交，等待专属 reviewer/父代理再次独立复核，供 B 集成接续。不是 D05/G2/C04 验收通过，不表示 G5 完成。** 本报告不 approve、不 push、不建/改 PR、不 merge、不 deploy、不改 scheduler。

第 2 轮复核结论：任务 A 自身未通过，1 MAJOR（原 R1-02.3 遗留）/0 BLOCKER。本轮已关闭 `D-R1-A-01` 并补入持久回归；报告下方保留第 1 轮证据，同时以 R2 数值更正此前“完整恢复 deadline 矩阵”的结论。

## 0. 身份、范围与基线

| 项 | 值 |
| --- | --- |
| 任务卡基线 | `6123f0a17e8a47997e577086a125dc58da7a3d7e`（在 `90140b34` 之上只有调用方新分工卡） |
| 被审前版 | `47ecf6a6`（代码）/`390aa95f`（报告）；本轮不修改、不覆盖 |
| 第 1 轮代码提交 | `0a2f28a1e903b08f94e95cd9996b4817e662d349` |
| 第 1 轮报告提交 | `2144100f78444ecb7721be652cf124a7927b8e66` |
| **第 2 轮代码提交** | 见最终交付消息；本文随后的 docs-only 提交绑定该 SHA |
| 本报告 | docs-only 独立提交（提交号见最终交付消息；不改变代码提交） |
| 唯一允许 tracked 文件 | 19（见任务卡 A 节） |
| R2 实际修改 tracked 文件 | 3：`LocalTableRoom.cs`、`LocalTableRuntime.cs`、`C04RoomTicketTests.cs` |
| 累计实际修改 tracked 文件 | 6：上述 3 + `AppComposition.cs`、`LocalTableChannelFactory.cs`、`LocalHostCompositionPlayModeTests.cs` |
| 未动的 13 个授权路径 | 保持原样；未修改 Domain/Client/proto/actor/schema/asmdef |
| 第 1 轮只读取证根 | `E:/majiang-review/caller-d05-review-20261102-r1/build/caller-review`（未写入、未覆盖） |
| 第 1 轮独占证据根 | `E:/majiang-review/g2-c04-d05-recovery-lifecycle-ds-20261102-r1/build/6f81cf757b7348b9b3d1e38de3ab3421` |
| 第 2 轮独立复核证据根 | `E:/majiang-review/g2-c04-d05-recovery-lifecycle-ds-20261102-r1/build/reviewer-e23034a226d2468697e768704f7f7964`（只读） |
| 第 2 轮返修独占证据根 | `E:/majiang-review/g2-c04-d05-recovery-lifecycle-ds-20261102-r1/build/e839be3ed91e4c4682248d04e70f0220` |

第 2 轮复核报告 SHA-256 实测 `7A7AD4AAE73227B5D7938BB37676900A68A6A7EEAEEB935C9484E7984AA97BE3`；稳定 manifest `56235A518BDF7EF11890D15D748B0BD0FDB55A1D71E9A8FC9CD3014E4B522706`；复现源 `D1B09066420201B07D086B63400AB17ED70494F8365497158B955A3F284B0175`。均已全文读取，未只读摘要；旧合并线程未复活，Luna 线程/树/文件未操作。

## 1. 第 2 轮缺陷关闭：D-R1-A-01

### 1.0 D-R1-A-01（MAJOR）已开放真人决策冷恢复后 deadline 被永久阻塞

- **严重级别**：MAJOR（原 R1-02.3 遗留，不扩展 B/Luna 范围）。
- **位置**：`dotnet/src/Mahjong.LocalHost/LocalTableRoom.cs` 的 `CanTickAuthority/_phaseStarted`；`LocalTableRuntime.cs` 恢复初始化；`LocalTableDriver.DriveAsync` 因 gate=false 跳过 `actor.TickAsync`。
- **复现命令**（审查原件只读，复制到同深度新 GUID `build/e839…` 后执行）：
  ```powershell
  D:\Unity\dotnet\dotnet.exe build build/e839be3ed91e4c4682248d04e70f0220/deadline/CallerRoomProbes.csproj -c Release --nologo -v:q
  D:\Unity\dotnet\dotnet.exe exec build/e839be3ed91e4c4682248d04e70f0220/deadline/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll
  ```
- **首次实际**（与复核完全一致，输出 SHA `7EC2BA7B2488B479B8FE3D746DC68390D6BF194543C437C448100D3F9668BF31`）：`before/recovered/after` 全为 `local-human-initial`；`IsPaused=false`；恢复 `DeadlineMillis=29000`（含合法 `RecoveryGraceMillis=5000`），`clockNow=31000`；`CanTickAuthority=false`，旧决策未退休；仅手动 `actor.TickAsync` 后 `manualTickAfter=null`。
- **期望**：已开始的恢复房间/已开放 decision 按实际恢复 deadline 与合法宽限运行 tick；无真人 binding 不得回退 `AwaitingSeats` 或暂停已有 deadline；未绑定 LocalHuman 仍不得新开 decision；真正 fresh 未齐席位继续等待；不得增加宽限或重开 deadline。
- **根因**：`_phaseStarted` 是 room 进程内布尔，冷恢复新 room 默认 false。`InitializeControl` 只恢复 controller，未恢复已开局阶段；`CanTickAuthority` 对未绑定 LocalHuman 返回 false，driver 因而永不执行 `TickAsync`，即使 actor/timing 已有过期决策。第 1 轮只测了同进程 detach，未测“冷恢复 + 仅观战 + 已开放 decision”，因此该组合被漏掉。
- **修复**：恢复分支在 actor/driver 生效前读取权威 timing（优先 `LocalTableStore.LoadTimingAsync` 最新 journal，回退 base checkpoint），由 `HasStartedProgress` 判定是否已有 pending decision 或已提交 state/event；`LocalTableRoom.InitializeControl(controllers, alreadyStarted)` 在锁内同时恢复 controller 谓词与 `_phaseStarted`。fresh 分支显式传 `false`。不修改 actor/domain/schema，不手动 Tick，不反射写权威。
- **统一负例矩阵**（均为真实 room/channel/TableSession，生产 driver）：
  | 场景 | 断言 | 用例 |
  | --- | --- | --- |
  | fresh 无 pending、未绑定 | `CanTick=false`，driver 不打开 decision | `AutoPlayDriverCannotLeaveAwaitingSeatsBeforeAControlSeedExists`、`FreshAllHumanRoomStartsOnlyAfterEveryInitialHumanBinds` |
  | recovered human pending、仅 spectator | `CanTick=true`；deadline 前不早退；跨越恢复 deadline 后旧 decision 由 driver 退休；无新 human decision | `RecoveredOpenHumanDecisionRetiresOnOriginalDeadlineWithoutBinding` |
  | recovered human pending、live binding | 旧 decision 按恢复 deadline 退休；旧 decisionId 控制命令 `DECISION_MISMATCH`，control/epoch 不变 | `RecoveredOpenHumanDecisionRetiresWithLiveBindingAndRejectsOldDecision` |
  | recovered released AI | 无 binding tick 且实际推进；epoch1；旧 epoch0 拒绝、claim 到 epoch2 | `RecoveredReleasedSeatTicksWithoutHumanBindingAndKeepsEpoch`、`RecoveredReleasedSeatActuallyAdvancesAsAiWithoutHumanBinding` |
  | recovered released → claimed human | claim 后 LocalHuman/epoch2，且产生可行动 human decision | `RecoveredReleasedSeatClaimsControlAndOpensHumanDecision` |
  | 已开放 deadline + 同进程 detach | 已有 deadline 不重开/不延长，无 binding 不新开 | `OpenDecisionDeadlineStillRunsAfterOwnerDetachWithoutOpeningANewOne` |
  | 旧 decision/epoch | 旧 decision 在 deadline 后拒绝；旧 epoch 拒绝 | 上述 bound/claim 用例 + 既有 `LocalControlTests.ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch` |
- **验收断言/结果**：复核探针修复后 `recovered=local-human-initial`、`after=null`、`canTick=true`、`timingAfter.Seats[0].Timing=null/PendingKey=null`，`timingAfter.StateVersion=4`（其他 AI 席继续合法推进）；无新 seat0 human decision。恢复 `DeadlineMillis=29000`、`RecoveryGraceMillis=5000` 在跨越前保持不变；未重新加宽限。
- **证据**：`final2-deadline.txt` SHA `4C5D7054895F235E9FE652F8F907DBC7797E864842B646136E78AD37A5DBA3BA`（native 0）；首次失败 `deadline-repro.txt` SHA `7EC2BA7B…`；源码/测试见 §2/§3。

### 1.1 R1-02.1 恢复 controller/epoch 被新 SeatPlan 覆盖

- **位置**：`LocalTableRuntime.OpenCoreAsync/CreateActor`（旧版固定 seat0=LocalHuman、其余 Computer）；`LocalSnapshotFacts.CreateInitialCheckpoint`。
- **复现**：真实探针 `RecoveredAllAiDifferentSeatPlan`（源 `R/probes/Program.cs`，复制到本轮独占根后运行，未改审查原件）：
  `D:\Unity\dotnet\dotnet.exe exec build/<GUID>/probes/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll build/<GUID>/room-probes.json`
- **首次实际**：`Cold recovery failed: RECOVERY_CONTROL_INVALID ... Seat 0 changes controller without advancing controlEpoch.` 初始证据 `room-probes.json` SHA `981EC1AED33DA19A699A4B058FDDCDD633D4F98A8CF9EF5D4632531FE12FB2D4`（与复核报告一致）。
- **期望**：恢复的 checkpoint controller/epoch 优先于新的初始 SeatPlan；all-AI 房间在新 SeatPlan=AllFourHumanSeats 下仍成功冷恢复并 tick。
- **根因**：actor 初始 controller 由常量写死，checkpoint 保存的 all-Computer registry 在恢复校验中与固定 seat0 Human 冲突（同 epoch 不同 kind）。
- **修复**：恢复分支先解码 `TableControlCheckpointCodec`，用持久化 `SeatControllerKind` 初始化 `TableActorOptions.InitialControllers`；fresh 分支用 `SeatPlan` 初始化；二者与 `CreateInitialCheckpoint` 使用同一来源。
- **验收断言/结果**：真实探针 recovered all-AI 四席均 `Computer`、`actualCanTick=true`、`defectReproduced=false`；单测 `ColdRecoveryUsesPersistedAllAiControllersInsteadOfNewHumanSeatPlan` 通过。
- **证据**：`final2-probes.json`（§4）。

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
- **证据**：`final2-probes.json`、`final2-localhost.xml`；首次失败诊断 `ai-progress-diag.txt` SHA `EA5FCDE92E6A79086CB41E84DF7A1E9BFD59DA957F99CA5FFC55E7FABF258EF6`，修复后重跑 `ai-progress-diag2.txt` SHA `8E13F4C220B555897806F5DC76F4E890E24D65795EED61AE4A1B7E1741F37610`。

### 1.3 R1-02.3 controller/live binding/phase/deadline/epoch 矩阵与旧 decision/epoch

- **位置**：`LocalTableRoom.CanTickAuthority`/`CanOpenHumanDecision`；`LocalTableDriver.DriveOneAsync`；`TableActor` 现有 epoch/shape 校验（未改 actor）。
- **复现/覆盖**：新增/复用以下真实 room/channel/TableSession 用例（R2 更正：第 1 轮此处遗漏“冷恢复 + human pending + 无 binding”组合；现有下表已补齐）：
  - `FreshAllHumanRoomStartsOnlyAfterEveryInitialHumanBinds`：fresh × 4 human × bound/unbound × preparing；全绑定前 false，第 4 席绑定后 true，detach 一席仍 true（不回退 AwaitingSeats）。
  - `RecoveredHumanControllerSurvivesASeatPlanThatDropsIt`：recovered × human × unbound，且新 SeatPlan=AllFourHumanSeats 不得把 recovered 的 Computer 席 1..3 改成 human；仅绑定 seat0 后即 Playing。
  - `AutoPlayDriverCannotLeaveAwaitingSeatsBeforeAControlSeedExists`：AutoPlay=true 构造函数竞态；无 binding 时 driver 不得离开 AwaitingSeats、不得打开 decision。
  - `RecoveredOpenHumanDecisionRetiresOnOriginalDeadlineWithoutBinding`：冷恢复 open human decision、仅 spectator、零真人 binding；读取实际 `DeadlineMillis/RecoveryGraceMillis`，deadline 前不早退，跨越后由生产 driver 退休，无新 human decision，deadline/宽限不重开。
  - `RecoveredOpenHumanDecisionRetiresWithLiveBindingAndRejectsOldDecision`：同场景但 live binding；旧 decision 退休后旧 decisionId 控制命令 `DECISION_MISMATCH`，control/epoch 不变。
  - `OpenDecisionDeadlineStillRunsAfterOwnerDetachWithoutOpeningANewOne`：已有 23s deadline 在 5s 时 owner detach，再推进 19s（若 detach 重置 deadline 则到 28s，不会到期）；实际旧 decision 到期退役，且 LocalHuman 无 binding 不再新开 decision。
  - `RecoveredReleasedSeatTicksWithoutHumanBindingAndKeepsEpoch`：recovered epoch1，旧 epoch0 `ClaimControl` 返回 `STALE_CONTROL_EPOCH`，epoch1 claim 成功到 epoch2=LocalHuman。
  - `RecoveredReleasedSeatClaimsControlAndOpensHumanDecision`：recovered released → claim 后 LocalHuman/epoch2，并产生可行动 human decision。
  - 既有 `LocalControlTests.ControlAroundAuthoritativeDeadlineRejectsStaleDecisionAndStaleEpoch` 继续运行，覆盖旧 decisionId 在 deadline 后拒绝与旧 epoch 拒绝。
- **实际 vs 期望**：R2 修复后全部按上表通过；已开始恢复房间不回退 AwaitingSeats；deadline 不重开、不延长；旧 decision/epoch 按现有 actor 规则拒绝。
- **修复要求对应**：SeatPlan 只用于 fresh 初始化和发 player ticket；运行期只认 current/recovered controller、live binding、recovered timing/phase、已有 deadline。未改 actor/domain/schema。
- **证据**：R2 `final2-localhost.xml` 167/167、`final2-c04-room.xml` 19/19；R1 旧证据保留但对应旧候选。

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
- **证据**：`final2-c04-room.xml`。

## 2. 代码修复映射

| 文件 | 修复 |
| --- | --- |
| `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs` | recovered control checkpoint 解码；fresh/checkpoint 初始化 actor controllers；room 在 actor/driver 前 seed；四席 AI scope（含 seat0）；R2：恢复最新 timing 并判定 alreadyStarted |
| `dotnet/src/Mahjong.LocalHost/LocalTableRoom.cs` | current-controller/`_controlInitialized` 的 AwaitingSeats→Playing gate；`InitializeControl`/`MarkControlChanged`；ticket `RetireTicket` 有界退役；并发 attach 清理；`RetainedTicketCountForTest`/`RetiredTicketCountForTest`；R2：`InitializeControl(..., alreadyStarted)` |
| `dotnet/tests/Mahjong.LocalHost.Tests/C04RoomTicketTests.cs` | 13 个真实 room/channel/TableSession 回归（见 §3） |
| `unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs` | 关闭失败/隔离保留 viewer；默认单 owner 正常关闭释放 room |
| `unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs` | 只读 `IsRoomOpen` 诊断属性 |
| `unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalHostCompositionPlayModeTests.cs` | 扩展 return-to-menu 重进用例，断言 room 释放/重开 |

公共组合接口保持兼容；唯一新增公共 surface 是 `LocalTableChannelFactory.IsRoomOpen`（只读诊断，理由：让 PlayMode/父代理能直接验证默认 owner 是否释放 room；不改变创建、ticket、session 或 lifecycle 语义）。其余新增均为 `internal` test seam，未扩大 matrix 任务的默认源码路径。

## 3. 新增真实回归（LocalHost.Tests，13 个）

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
| 11 | `RecoveredOpenHumanDecisionRetiresOnOriginalDeadlineWithoutBinding` | **R2**：冷恢复 human pending + 零真人 binding，实际 deadline/grace 前不早退、跨越后 driver 退休、无新 decision、不重开 |
| 12 | `RecoveredOpenHumanDecisionRetiresWithLiveBindingAndRejectsOldDecision` | **R2**：同场景 live binding，旧 decision 退休后旧 decisionId 拒绝且 control 不变 |
| 13 | `RecoveredReleasedSeatClaimsControlAndOpensHumanDecision` | **R2**：recovered released → claim 到 LocalHuman/epoch2 并产生 human decision |

无 skip、无空测试、无放宽断言、无旧 DLL/旧摘要取绿。原有 154 项全部保留并继续运行；R2 全量 167 项。

## 4. 实际命令、native/XML/计数与 SHA（R2）

环境：`D:\Unity\dotnet\dotnet.exe` 8.0.426；Windows 10.0.19045。所有命令在任务工作树执行，退出码为 PowerShell 捕获的真实 native exit。R2 证据根 `build/e839be3ed91e4c4682248d04e70f0220`。

```powershell
# D-R1-A-01 复核探针（从只读审查根复制到 R2 新 GUID，未覆盖原件）
D:\Unity\dotnet\dotnet.exe build build/e839.../deadline/CallerRoomProbes.csproj -c Release --nologo -v:q
D:\Unity\dotnet\dotnet.exe exec build/e839.../deadline/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll

# 原两个 room 探针（同样复制到 R2 新 GUID）
D:\Unity\dotnet\dotnet.exe build build/e839.../probes/CallerRoomProbes.csproj -c Release --nologo -v:q
D:\Unity\dotnet\dotnet.exe exec build/e839.../probes/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll build/e839.../final2-probes.json

# LocalHost / TableRuntime 全量
D:\Unity\dotnet\dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release -v:m --nologo
D:\Unity\dotnet\dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -noColor -parallelMode none -result-xml build/e839.../final2-localhost.xml
D:\Unity\dotnet\dotnet.exe exec dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll `
  -noLogo -noColor -parallelMode none -result-xml build/e839.../final2-tableruntime.xml

# R2 专项（19 个 room/ticket 用例）
D:\Unity\dotnet\dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -noColor -parallelMode none -result-xml build/e839.../final2-c04-room.xml -class Mahjong.LocalHost.Tests.C04RoomTicketTests
```

| 检查 | native exit | 计数（XML 实测） |
| --- | --- | --- |
| R2 deadline 探针构建 | 0 | 0 warning / 0 error |
| R2 deadline 探针运行 | 0 | `recovered=local-human-initial`、`after=null`、`canTick=true`；`deadline=29000`、`grace=5000` 未变；`timingAfter` seat0 Timing/PendingKey=null；无新 seat0 decision |
| 原两个 room 探针 | 0 | 3 个 probe，全部 `defectReproduced=false`；正对照 `beforeHello=false/live=true/afterDetach=true` |
| LocalHost 构建 | 0 | 0 warning / 0 error |
| LocalHost 全量 | 0 | Total 167 / Passed 167 / Failed 0 / Skipped 0 / Errors 0 / NotRun 0，31.298s |
| C04RoomTicketTests | 0 | Total 19 / Passed 19 / Failed 0 / Skipped 0 / Errors 0 / NotRun 0，2.205s |
| TableRuntime 全量 | 0 | Total 483 / Passed 483 / Failed 0 / Skipped 0 / Errors 0 / NotRun 0，3.608s |
| C04RoomTicketTests 连续复跑 | 0 | 20/20 次均 19/19，无失败（等待上限 10s，避免负载下假阴性） |
| composition 组件探针（.NET shim，非 Unity） | 0 | default-owner-close `Completed/roomOpen=false`；quarantine `PumpBarrierTimeout/count=1/roomOpen=true`；primary-close 保留 `count=1/roomOpen=true` |
| journal 持久化 Release 探针 | 0 | child `Recorded/LOCAL_CONTROL`；restored `Takeover/automated=true/epoch=1/canTick=true` |

稳定证据（R2 根）：

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `deadline-repro.txt`（修复前） | 3496 | `7EC2BA7B2488B479B8FE3D746DC68390D6BF194543C437C448100D3F9668BF31` |
| `final2-deadline.txt`（修复后） | 2712 | `4C5D7054895F235E9FE652F8F907DBC7797E864842B646136E78AD37A5DBA3BA` |
| `final2-probes.json` | 837 | `20EA6428B64C4F5D87AC46893725727E09E602118C7A305C3568D7EDFA2CD5C7` |
| `final2-probes.stdout.txt` | 1680 | `53895AD36961BE1B0C78FF35C0DB2590B4F55679C835F2A1F54D03DECCCD5CD3` |
| `final2-localhost.xml` | 106860 | `818C2127FA59986471483A3CD752A23A051921E804D0A31C7FEA3FDAB2C6E967` |
| `final2-localhost.stdout.txt` | 736 | `8458E47CC591957C19BE4D6DA0EFE6E79F630747752456FD8146AA956C6B7789` |
| `final2-c04-room.xml` | 12401 | `C8269383C540FA43C9D897469D8E8B726903D3885D78BB77FED45C516E269196` |
| `final2-c04-room.stdout.txt` | 732 | `A2CAEB84704FD09E7C3302EF271438D20D4AA9E7AA823E0EB8BA51AD59FED3BE` |
| `final2-tableruntime.xml` | 315728 | `4043E2577A96E9F5E7A35EF4BCD3F1113DEB9872B9713EA4F2D5E3622D7D49F8` |
| `final2-tableruntime.stdout.txt` | 764 | `90B9667B65772D5D6E80728C4D023061DA051B76AEFDBC668B12E561A8049BDF` |
| `final2-composition.txt` | 730 | `9811F643BD345AE5E1B819D4EE43CF025E387AF8783C2CFA82CE8314953E1BCF` |
| `final2-journal.txt` | 408 | `F50673AA5B8B7ED263620C71CAD90F09442077629CC92EA927383F50F9E50C69` |

第 1 轮证据（`final-room-probes.json` `20EA6428…`、`final-localhost.xml` `E53FBD8A…` 164/164、`final-c04-room.xml` `3A6D1367…` 16/16）继续保留为 R1 历史；不得用其冒称 R2 候选，R2 以 `final2-*` 为准。

源码/二进制身份（R2 代码提交 `8ad03ce1f164208d358acd48927306c8411d40fa`）：

| 对象 | SHA-256 |
| --- | --- |
| `LocalTableRoom.cs` | `0B2D6B20A2CFF1265A2E352722B86652055DE73CF136DA4AA5509BD10B37FCAB` |
| `LocalTableRuntime.cs` | `97F032057A8310C61E7F5D7E7D259E0B8B2BD32FBF9A014444BA98D424D5E851` |
| `C04RoomTicketTests.cs` | `E77571D309266891EC89EC68BD1D7E2A75E7198218984DA569B6D50F92D9432C` |
| `AppComposition.cs` | `821E11F9E23F6C07372AD1ED7F65B4746C5A7D88FCD7F16A6123A5901138CDDF` |
| `LocalTableChannelFactory.cs` | `2011886AB2CB082CEA92773FF5A922AB8E5A01262313012A6DC3542A82496E9C` |
| `LocalHostCompositionPlayModeTests.cs` | `4813BC571DF27F0DFEE406FFC9712BA0CD66B2CFA033B1E53EEA6B7854936D5A` |
| 构建 `Mahjong.LocalHost.dll` | `BA45CB209F75C4BB500AE6090D687B3581BCF6072760F637A59AEDD01522837A` |
| 构建 `Mahjong.LocalHost.Tests.dll` | `6B22CE21E72C5F4F258271925F69B14608C7FEB07C30E3B6AEBD44E4ABF31947` |

首次失败保留：R1 的 `room-probes.json` `981EC1…`，R2 的 `deadline-repro.txt` `7EC2BA7B…`；均未被后续成功输出覆盖。`deadline3.txt` 的 24s 早期观察未跨合法恢复宽限，未作为缺陷证据。

## 5. 未覆盖/未运行边界（不得当作 PASS）

- **Unity PlayMode：NOT_RUN**。本环境未获取共享锁 `Local\MahjongV5P45UnityValidation`，未运行 Unity 冷导入/PlayMode；因此 `AppComposition`/factory 的 Unity 编译与 `RealLocalHost_ReturnToMenuThenReenter_RecoversSameRoom` 扩展断言**没有 runtime 证据**。`CloseRoomViewerAsync` 的 quarantine 保留分支也没有 Unity 级注入；R2 用 reviewer 的 .NET component harness 重跑默认 owner/quarantine/primary-close 三行，明确只是组件而非 Unity/Player。父代理/B 应在同一候选上按锁规则运行并记录 XML/native/计数。
- **Windows Test Player、Android、IL2CPP、真机、真实网络、Unity 多视图矩阵：NOT_RUN**，均不属于本组件任务；不冒称同包/同候选通过。
- **R1-01 / R1-03 / R1-04、Luna probe/verifier、B 的 N/U/P 矩阵：未处理、未改写**。D05/C04/G2–G5 仍未批准。
- 本任务未改 Domain/Client/proto/actor/schema/asmdef，未以手动 Tick 或反射写权威掩盖缺陷，未新增后台重连产品功能，未改旧任务历史，未创建/删除任何 bridge 线程。
- 未 approve/push/建改 PR/merge/deploy/改 scheduler/complete.flag。报告与代码分开提交，生成物在 `build/<GUID>` 且不入 Git。

## 6. 接续检查点

1. 专属 reviewer 复核 R2 代码提交 `8ad03ce1f164208d358acd48927306c8411d40fa` 与本文的 19 路径范围、证据 SHA、无 skip/假绿；重点重跑 `deadline/CallerRoomProbes` 与三个 R2 回归。
2. 在共享 Unity 锁下运行扩展后的 `RealLocalHost_ReturnToMenuThenReenter_RecoversSameRoom`，确认 `IsRoomOpen` 三处断言；如需 Unity 级 quarantine 证据，补“自有 pump 不响应取消 → PumpBarrierTimeout → viewer 仍被 composition 跟踪”的 PlayMode 用例（本任务未伪造）。
3. B 在冻结候选上使用本交付的 production room/controller 边界接入 N/U/P 与 G5；旧 154/164/483/2496/205 证据只绑定旧版本，不能直接冒称新候选通过。
4. 原合并线程保持删除状态；Luna `thread_20261102_224144_4290` 及其六文件权限不变。

本任务 A 到此为**第 2 轮可复核的组件交付**，不是 D05 完成，也不替代父代理/专属 reviewer 的独立验收。
