# G2-C04-D05 最终矩阵接续交付（D05-FINAL-SYNC）

日期：2026-11-03/04，Asia/Shanghai。执行者：DeepSeek Harness（配置默认
`deepseek/deepseek-v4.1-flash`，未切换模型、未委派）。本报告不 approve、不 push、
不建/改 PR、不 merge、不 deploy、不修改 scheduler/complete.flag/生产代码。

## 0. 结论先行

- 本任务唯一已知缺陷 **D05-FINAL-SYNC-01（MAJOR）已在允许范围内按第 3 轮复核要求
  完成 application-callback 绑定**：barrier 只接受与匹配 `CONTROL_APPLIED`
  SyncResponse 的 snapshot/cursor/state/event/control 指纹一致的**下一个**
  `SnapshotChanged`；任何被拒响应、替换 SyncRequest、下一入站帧或 full PUSH 都会
  清除该一次性额度。仅 full PUSH / 旧 correlation / receipt 不再满足。代码提交
  `26594b3b6179d3548c0ef44b7e4f20bd97f2211f`（第 2 轮 `39d05a58`，第 1 轮 `0832cf67`）。
- 第 3 轮 5×5 定向矩阵（含 drop→supersession 真实负例）全部 XML total=1/pass=1、
  native0；fresh 无 filter 完整 LocalHost **183/183**、Client **182/182**，
  均 0 fail/error/skip/notrun。
- 旧四参数 barrier / 无 delegate probe 证据已撤回，不再作为关闭证明；本轮改用接入
  当前 observer 的五参数 barrier 新 probe，正例与 drop→resync 负例均独立通过。
- 按复核指令未在范围裁决前重跑昂贵 Unity/Windows 矩阵；平台结论仍沿用已独立核对
  的同候选原件（6/7、5/7），不构成通过。
- **最终 D05 同候选同包矩阵未闭合**：真实 PlayMode 7 案 **6/7**、真实 Windows
  Test Player 同包 7 案 **5/7**。失败均为 Unity 测试自身的同步/时序缺口，
  位于本任务明确排除的 `unity/.../C04PrivacyRoleSwitchPlayModeTests.cs`；
  按“若真实新失败需要额外文件，先给精确最小 scope 请求”的规则，本轮未越界修改。
- 完整 LocalHost 出现一个**新发现的、间歇性的**同文件族同步缺口
  `LocalControlTests.DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect`
  （17 次完整运行中 2 次 `180/181`），同样不在允许的两文件内；已保留首败并给出
  最小 scope 请求。
- 因平台矩阵 FAIL，**D05/C04/G2 仍不得判通过**；本报告不把组件通过、raw verifier
  通过或 Windows 包已构建解释为矩阵通过。V4 继续生产。

## 1. 身份、范围与不重开项

| 项 | 值 |
| --- | --- |
| 任务 | `G2-C04-D05-FINAL-20261103` |
| 工作树 | `E:/majiang-review/g2-d05-final-ds-20261103-1515` |
| 分支 | `wt/v5-g2-d05-final-ds-20261103-1515` |
| 起点 HEAD | `2c8238cd29386ab52994c8e3839dfdb85059660c`（clean） |
| 代码提交（第 3 轮） | `26594b3b6179d3548c0ef44b7e4f20bd97f2211f` |
| 代码提交（第 2 轮） | `39d05a58980baa4ca1faba90eca7bf115179fb45` |
| 代码提交（第 1 轮） | `0832cf67936251978c624dda49bfd17653c7bb36` |
| 本报告 | docs-only 提交（最终交接消息给出精确 docs head） |
| `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| 允许 tracked 修改 | `dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs`、本报告 |
| 实际 tracked 修改 | 同上 2 个；无生产/runner/verifier/Unity/asmdef/proto/ProjectSettings 改动 |
| 本任务独占根 | 第 3 轮 `build/3eeb24d990f141b5962ae79c5698b19a`；第 2 轮 `build/12d656809f6e46df95e61d5c75c8262d`；第 1 轮 `build/02f82a1c6144498ab2d7b709f6942517`（均 git ignored） |
| 共享锁 | `Local\MahjongV5P45UnityValidation`；PlayMode 等待 424 s，Windows 等待 0 s，均 ≤ 600 s |

不重开：D05 B9 `thread_20261102_232828_d9f0`、隐私 `thread_20261102_224144_4290`、
冷恢复 `thread_20261102_232827_4202`、控制投影 `thread_20261103_061642_138c`、
控制对账 `thread_20261103_141558_2c68`、D04-B `thread_20261103_074639_795b`；
未复活 `thread_20261102_224144_b057`，未复制 `thread_20261102_232828_d9f0`。

## 2. D05-FINAL-SYNC-01 修复映射

### 2.1 根因

`TableSession.HandleReceiptAsync` 对 `ReleaseControl/ClaimControl` 的
`Recorded/LOCAL_CONTROL` 回执先调用 `ResolveTrackedAsync`（同步触发
`ReceiptResolved`），随后才 `RequestFullBaselineAsync("CONTROL_APPLIED")`。
原测试在 `ReceiptResolved` 后立即断言 `Ready`，命中 transient `Synchronizing`。

### 2.2 修复（仅测试）

位置：`C04PrivacyStreamMatrixTests.Slow_real_channel_queue_saturates_without_stopping_room_or_other_bindings`。

- 提交前同时注册 receipt 与 `SnapshotChanged` 观察点；
- **第 3 轮 application 绑定**：`ControlCorrelatedChannel.ReceiveAsync` 收到匹配
  correlation/binding 的 `SyncResponse(PlayerStream)` 时只登记一个
  `PendingApplication`（session/cursor/state/event/control 指纹）；该登记仅在
  **本帧处理期间**有效，下一次 `ReceiveAsync` 入口、任何新的 SyncRequest、
  full PUSH 或另一响应都会先清除它。`TryConsumeAppliedResponse(args)` 必须与
  该指纹逐项相等才返回 true。这样“收到但被拒/被替换的响应”不会留下可借用额度；
- 新增 `RejectOneTargetResponseChannel` 真实负例：把目标响应 snapshot 的
  `SessionId` 改为 foreign（envelope correlation/binding 仍正确），生产
  `TableSession` 在应用前丢弃；随后通过现有生产 `RequestFullBaselineAsync(force:true)`
  发出 `TEST_UNRELATED_RESYNC`，其正常应用不得继承旧额度；断言
  `targetResponse=1, consumed=0, barrier=False, state=Ready`；
- 新增 `ControlPushInjectionChannel` 真实负例：receipt 前投递 production snapshot
  构造的 full PUSH + superseded SyncResponse，并把 `CONTROL_APPLIED` request 扣在
  transport send 前；断言 receipt=True、forwarded=0、targetResponse=0、barrier 未满足；
- 到齐后断言：稳定 `Ready`、`PendingCommands` 为空、actor/client epoch 一致
  （`LocalTableHost.CaptureRecoveryAsync.Facts.ControlEpochs[0]` 与客户端 baseline
  都等于期望 epoch）、durable control request 恰好 1 条、该 request 的 command frame
  恰好 1 次、control request/response/forwarded/applied 各 1 次、receipt 为
  `Recorded/LOCAL_CONTROL`；
- 保留原有慢消费者真实队列溢出（`accepted >= 257`、`TableChannelClosedException`）、
  room/其他 binding 继续 Ready、replacement viewer attach、原回执状态断言；
- 负例矩阵：receipt-only、matching predicate 但无 applied response、旧 session/
  binding、旧 epoch、claim 形态、非 baseline PUSH、superseded/foreign 目标响应、
  drop→审计替换→无关 baseline、缺目标 response 的有界等待均不解锁；正常 correlated
  response 实际应用后才通过。

### 2.3 目标矩阵（第 3 轮固定 5 次 × 5 案）

命令（完整方法名，XML 每份断言 total=1）：

```powershell
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -method <full method name> -noLogo -noColor -result-xml build/3eeb24d990f141b5962ae79c5698b19a/r3-target-<case>-<n>.xml
```

| 用例 | 5 次结果 | 证据 |
| --- | --- | --- |
| `Slow_real_channel_queue_saturates_without_stopping_room_or_other_bindings` | 5/5 PASS，total=1/pass=1，0 fail/skip/notrun | `r3-target-Slow...-1..5.xml` |
| `Control_baseline_barrier_rejects_unrelated_full_push_and_missing_control_response` | 5/5 PASS | `r3-target-...-1..5.xml` |
| `Control_baseline_barrier_rejects_dropped_target_response_credit_after_supersession` | 5/5 PASS | `r3-target-...-1..5.xml` |
| `Control_baseline_barrier_rejects_receipt_only_foreign_and_stale_baselines` | 5/5 PASS | `r3-target-...-1..5.xml` |
| `Control_baseline_barrier_bounded_wait_times_out_without_matching_baseline` | 5/5 PASS | `r3-target-...-1..5.xml` |

本轮新 probe（own-root，接入当前 observer 的五参数 barrier，非旧四参数）：
- positive control：`POSITIVE_PASS`，`barrier=True state=Ready request=1
  targetResponse=1 forwarded=1 consumed=1`，native0；
- drop→supersession：`AFTER_REJECT ... targetResponses=1 consumed=0 barrier=False`，
  随后 `REVIEW_UNRELATED_RESYNC` 正常应用，`barrier=False consumed=0`，
  `NEGATIVE_PASS`，native0。
- 旧 `build/12d656.../probe-r2`（四参数、无 delegate）证据**撤回**，不再作为关闭
  证明；其“barrier=False”是无条件不通过，不能证明修复。原五参数定向测试的 25 份
  XML 为实际执行证据，不因撤回旧 probe 而失效。

### 2.4 已知缺陷首次失败的可复现性与修复对照

在未修改的起点测试文件上 fresh 重建并连续运行完整 LocalHost 5 次，其中 1 次
复现原缺陷：`Slow_real_channel... [FAIL]`（`baseline-full-5.xml`，179 total /
178 pass / 1 fail / 0 skip / 0 error）。修改后该用例在 15 次定向运行与全部
完整运行中 0 次失败。该对照直接证明原竞态真实存在且本次同步点覆盖它。

### 2.5 第 3 轮 R1-01 逐项映射

| R1-01 要求 | 本轮实现/证据 |
| --- | --- |
| 关联实际 CONTROL_APPLIED outbound SyncRequest correlation | `ControlCorrelatedChannel.SendAsync` 解码真实出站帧，记录当前 session/binding/seat 的 `CONTROL_APPLIED` correlation |
| 关联匹配 inbound SyncResponse | `ReceiveAsync` 仅接受同 correlation、PlayerStream、同 binding 的 `SyncResponse` |
| 等待实际被 TableSession 应用 | 登记一次性 `PendingApplication` 指纹；只有下一 `SnapshotChanged(IsFullBaseline)` 的 session/cursor/state/event/control 全部匹配才消费 |
| 收到但被拒响应不留额度 | foreign-snapshot 目标响应被生产丢弃后，下一次 Receive 入口清除登记；`consumed=0` |
| 替换/后续无关 baseline 不继承额度 | 新 SyncRequest（含非 control `REVIEW_UNRELATED_RESYNC`）先清除登记；其正常 baseline 应用后 `barrier=False consumed=0` |
| full PUSH 不得误解锁 | 真实负例在 receipt 前投递 production snapshot 构造的 full PUSH；barrier 未满足 |
| 旧/替换 correlation 不得解锁 | 负例同时投递 superseded SyncResponse；`TargetResponseCount=0`，barrier 未满足 |
| receipt-only / 缺 response 有界失败 | 负例扣住 target request，receipt=True、forwarded=0、state=Synchronizing；helper 用例有界 `OperationCanceledException` |
| 正常响应必须仍可解锁 | 新 probe positive control `POSITIVE_PASS`；目标测试 5/5 |
| 保留 Ready/pending/epoch/单 command/单 durable 副作用/慢消费者/replacement | 目标测试原断言全部保留并新增 control request/response/forwarded/applied 各 1 次断言 |
| 不以手工 helper 代替关键负例 | `Control_baseline_barrier_rejects_unrelated_full_push_and_missing_control_response` 使用真实 room/channel/TableSession |

## 3. 组件与工具回归

### 3.1 Release 构建

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj `
  -c Release -t:Rebuild -v:minimal
```

native exit 0，0 warning / 0 error（`build.log`）。

### 3.2 完整 LocalHost

```powershell
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -noColor -result-xml build/3eeb24d990f141b5962ae79c5698b19a/localhost-full.xml
```

- **第 3 轮 fresh 无 filter 完整 LocalHost：183 total / 183 pass / 0 fail / 0 error /
  0 skip / 0 notrun，native exit 0**；XML SHA-256
  `557DCD020E8ECF0A181B1AB6A8B24E60A07721C8C6F5C83B17AB79A36C5F2DD8`，
  log SHA-256 `C2903149FF8EC0692E959BBD1F9A4EB5226877A8521109DA9B06C1A38EBA54A1`。
- 第 2 轮历史保留：182/182，XML SHA
  `57B14E5E...CE7DF2D8`（第 3 轮新增 drop→supersession 用例后为 183）。
- 第 1 轮历史保留：17 次完整运行中 15 次 181/181、2 次 180/181，失败均为
  `LocalControlTests.DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect`
  `LocalControlTests.cs:690`。首败 `localhost-full.xml` SHA-256
  `9DD43DBA9F22A6D50E0215C7A9C1F386D62D6D7917C67D76324C795DAE1D28CE`；
  第二次失败 `fixed-full-4.xml` SHA-256
  `9FE23807AA53B02E48EE24E8DD5F2971D3649E129CD40E9AAA10B464174EB1C0`。
- 该外部失败只在完整并行套件出现，单跑 8/8 PASS；根因是等待“lookup resolution
  之后的任意 full baseline”，而不是与 CONTROL_APPLIED/epoch 对应的目标 response。
  本轮未获授权修改该文件，故仍保留为 D05-FINAL-SYNC-03，见 §5；不以本轮
  183/183 单次绿色抹除它。

### 3.3 Client

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release -t:Rebuild -v:minimal
D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.Client.Tests/bin/Release/net8.0/Mahjong.Client.Tests.dll -noLogo -noColor -result-xml .../client-full.xml
```

native exit 0；**182/182 / 0 fail / 0 error / 0 skip / 0 notrun**；
第 3 轮 XML SHA-256
`A69823E92CB461140F9FB15F1F581F83C9371AE01C31DC53ABC44DF07B982474`；
第 1/2 轮 XML SHA
`7729DFBB...F486E2D2`、`2569C98D...81BD0DAA` 保留。

### 3.4 runner / verifier 工具

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyRunner.Tests.ps1 `
  -TestRunRoot build/02f82a1c6144498ab2d7b709f6942517/runner-tests
```

native exit 0；**16/16 PASS**（源身份/闭包/XML 分母/marker/进程/超时/取消/锁/封存），
日志 SHA-256 `EF4C98EA7603B66BBAA4FEF6EFA3EB137EE83645556C10A886F5A45747354765`。
两次 Unity run 的 verifier self-test 均为 native exit 0：
`SELF_TEST_PASS positive=17 negative=67 serializedFrames=5 source=WireCodec`。

### 3.5 冻结与目录合同

- 源码闭包（`dotnet` + `tools/unity`）540 文件 / 14,181,085 B /
  SHA-256 `0147e9261c0c00b0030ca010fb1c05af50a98a10e85c2f27524a7f14341219dc`；
  PlayMode 与 Windows 两次 run 相同。
- staged Unity 输入 364 文件 / SHA-256
  `4124e07fba943b3adb4e0a137d7d4e3e0b46c2a2b43dc0f41e66330708f9dbb9`；两次相同。
- `-PreflightOnly` native exit 0，`SOURCE head=0832cf67 clean=true`。

## 4. 真实 Unity / Windows 矩阵

### 4.1 PlayMode（同候选、未改生产 runner）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-c04-privacy-matrix.ps1 `
  -RunRoot build/02e27e3be3b34f1da5456356e88f0be4 `
  -ExpectedSourceHead 0832cf67936251978c624dda49bfd17653c7bb36 `
  -TimeoutSeconds 1800
```

- Unity `6000.3.27f1` / `6000.3.27.6761814`；dotnet `8.0.3226.50405` / SDK 8.0.426；
  mutex 等待 424 s；Unity native exit **2**，`normalCloseHonored=true`，无 timeout/force stop。
- XML：`Total 7 / Passed 6 / Failed 1 / Skipped 0 / Inconclusive 0`；SHA-256
  `CB507495F52F69296A8533F13EE2378285604A35F0EF67D9CF0478F02F9741A1`。
- 通过：four-players/role-switch、delayed-old-raw、cold-reopen、sleeve/fee ownership、
  deadline、closed→spectator→new-player。
- 失败：`RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly` —
  `TableSession.SubmitAsync` at `TableSession.cs:357`：
  `A command can be submitted only from a ready, bound session`；调用点
  `C04PrivacyRoleSwitchPlayModeTests.cs:985`（claim 回执后提交 stale command）。
- 失败后按“首败保留、原因未改变不重试”停止，未用重跑换绿。
- Unity log SHA-256 `7A2DED2790F6D155764D24FE5B5790137F67478C04688F0BE023AA89DC3A959D`；
  summary SHA-256 `E1435391CFD57869C52E7D3DC41A1F678F04D25926D52299C80BB31038182E11`。

### 4.2 Windows Test Player（同一 Unity Test Player 包，七案）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-c04-privacy-matrix.ps1 `
  -RunRoot build/b5327245af6c4068b1112d69b6f0b788 `
  -ExpectedSourceHead 0832cf67936251978c624dda49bfd17653c7bb36 `
  -WindowsPlayerTests -TimeoutSeconds 1800
```

- 该模式由 Unity `-runTests -testPlatform StandaloneWindows64 -buildPlayerPath .../test-player`
  构建**一个** Test Player 并在其中运行同一 `testFilter` 的 7 案；不是独立
  standalone exe，也未拼接旧 XML。
- Unity native exit **2**，耗时 343.777 s，无 timeout/force stop；mutex 等待 0 s。
- XML：`Total 7 / Passed 5 / Failed 2 / Skipped 0 / Inconclusive 0`；SHA-256
  `BB8FD76274C2EA91864A0C47955F1155930305B2E5F28B5B3CACAE55CCB4619A`。
- 失败 1：`RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly` —
  与 PlayMode 同一 `TableSession.cs:357` / `C04PrivacyRoleSwitchPlayModeTests.cs:985`。
- 失败 2：`RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate` —
  `C04PrivacyRoleSwitchPlayModeTests.cs:372`，`Expected: 1 / But was: 2`
  （恢复后 AI 推进使 `StateVersion` 在断言前前进 1；PlayMode 本次该案通过）。
- 因 XML 失败，未修改的 runner 在 `Assert-C04UnityXml` 处中止，**未进入**
  `Get-C04TestPlayerPackage`/raw 场景校验的 PASS 分支，因此没有 runner 封存的
  package manifest；`summary.json` 为 FAIL，`testPlayerPackage` 为空。
- 为身份可追溯，对实际构建的 `test-player/PlayerWithTests` 做**补充只读清单**
  （非 runner PASS 产物）：357 文件 / 201,837,297 B / 规范化清单
  SHA-256 `8b557090361fef689c9cb41d8bd735d1848e5cf490a7db4153c0ae53d99cce9d`；
  8 个共享 DLL 均存在（`Mahjong.Domain/Contracts/Contracts.Wire/TableRuntime/
  Client/LocalHost/Ai`、`Google.Protobuf`），`Mahjong.exe` 位于
  `test-player/PlayerWithTests/Mahjong.exe`。
- 该包不能标记为“已接受同包矩阵”：2 案失败。未把它降级成独立 exe 结果，也未用
  PlayMode 的 6/7 抵销。

逐案结果（同一 `testFilter`，PlayMode 与 Windows Test Player 各一次真实执行）：

| # | 固定用例 | PlayMode | Windows Test Player |
| --- | --- | --- | --- |
| 1 | `RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions` | PASS | PASS |
| 2 | `RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding` | PASS | PASS |
| 3 | `RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate` | PASS | **FAIL**（StateVersion 1→2） |
| 4 | `RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator` | PASS | PASS |
| 5 | `RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly` | **FAIL**（submit not Ready） | **FAIL**（submit not Ready） |
| 6 | `RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose` | PASS | PASS |
| 7 | `RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw` | PASS | PASS |

### 4.3 raw / typed verifier（补充证据，不作为失败案通过）

对两次 run 已封存的真实 raw 帧，用未修改的 `tools/tests/C04PrivacyMatrix.Tests.ps1`
与同一 verifier DLL 逐场景执行（runner 因 XML 失败未执行到该步）：

| 场景 | PlayMode | Windows |
| --- | --- | --- |
| role-switch | PASS，11 帧，raw SHA `b230d056...`，67 negatives | PASS，11 帧，raw SHA `f4f9442d...`，67 negatives |
| four-owner | PASS，49 帧，raw SHA `75fdfff5...`，67 negatives | PASS，49 帧，raw SHA `b55aefc3...`，67 negatives |
| closed-binding | PASS，6 帧，raw SHA `35d45716...`，67 negatives | PASS，6 帧，raw SHA `886f1937...`，67 negatives |
| recovery | PASS，8 帧，raw SHA `0ff539b3...`，67 negatives | **MISSING/NOT_RUN**（ColdReopen 在 seal 前失败） |

这些结果证明真实 WireCodec 帧的 Welcome/scope/owner/audience/version、私有账本/
袖牌非空、旧 binding/版本拒绝等合同在已产生的帧上成立；不改变 7 案平台失败，
也不能把工具夹具当 GAME 通过。PlayMode 每场景 67 个变异负例均 native 非零。

## 5. 新发现缺陷（全部一次给出；均在本任务允许文件之外）

本节的失败对**本接续任务**是新信息：只有在实际运行完整 LocalHost / 真实 PlayMode /
Windows Player 后才可观察；任务卡只列 D05-FINAL-SYNC-01。Unity release→claim 6/7
在原 D05-FINAL 线程已有记录，本任务不重开、不复制其修复、也不把原线程历史改写；
本次仅以同候选原件再次确认并给出精确最小 scope 请求。按规则未修改以下文件。

### D05-FINAL-SYNC-02（MAJOR，Unity 测试同步缺口）

- 位置：`unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs`，
  `RunReleasedControlScenarioAsync` 约 947–985 行（claim 回执后直接提交 stale action）。
- 复现：§4.1 / §4.2 的 runner 命令；PlayMode XML 与 Windows XML 均出现。
- 实际：claim `ReceiptResolved` 后，测试用 `PlayerSnapshot` epoch 轮询认为已切换，
  但客户端仍在 `Synchronizing`（CONTROL_APPLIED full baseline 在途），
  `SubmitAsync` 抛 “A command can be submitted only from a ready, bound session.”
- 期望：在提交 stale command 前等待**claim 的匹配 full baseline**（当前 session、
  `IsFullBaseline`、epoch = claimEpoch+1、`ControlledByComputer=false`）并确认
  `Ready`/无 pending；旧 binding/旧 epoch/无 baseline 有界失败。
- 修复要求：仅改该测试的同步点（可复用与 D05-FINAL-SYNC-01 相同的 barrier 思路），
  不修改 `TableSession`/生产 runner/断言强度；禁止随机 sleep 代替同步点。
- 验收断言：目标案 PlayMode 与同一 Windows Test Player 包均 7/7；5 次目标同步正负例
  全通过；无 skip/notrun。

### D05-FINAL-SYNC-03（MAJOR，LocalHost 完整套件间歇同步缺口）

- 位置：`dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`，
  `DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect` 约 619–690 行。
- 复现：§3.2 完整 LocalHost 命令；17 次中 2 次在 `LocalControlTests.cs:690` 失败
  （`Expected Ready / Actual Synchronizing`）；单跑该用例 8/8 PASS。
- 实际：`reconciledBaseline` 只要求“lookup resolution 之后任意 full baseline”，
  之后 `WaitReadyAsync` 返回，但后续 CONTROL_APPLIED baseline 请求再次进入
  `Synchronizing`，稳定 Ready 断言失败。
- 期望：以 matching CONTROL_APPLIED baseline（当前 binding/session、epoch/
  automation 对应）为同步点，再断言稳定 Ready；保留全部重连/对账/零重复副作用断言。
- 修复要求：仅改该测试的 barrier，不修改 `TableSession` 或放宽/删除断言；
  不用 `-parallelMode none` 或 flaky 重跑规避。
- 验收断言：该用例 5 次定向通过；fresh 完整 LocalHost 连续 5 次 0 fail/error/skip/notrun，
  native 0。

### D05-FINAL-MATRIX-04（MAJOR，Windows 冷重开精确状态断言竞态）

- 位置：同 Unity 文件 `RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate`
  约 372–373 行。
- 复现：§4.2 Windows Test Player run；`Expected: 1 / But was: 2`。
- 实际：恢复会话后 AI 推进使 `PlayerSnapshot.StateVersion` 在精确断言前前进 1；
  PlayMode 本次同案通过，但该断言对权威自动推进没有冻结点。
- 期望：在该场景捕获并断言“对账完成时的原始状态”而非随后被 AI 推进的最新状态；
  可通过测试内关闭该场景的自动推进/在 reconciliation 同步点冻结快照实现。
- 修复要求：测试内冻结，不修改生产 authority/deadline/AI 行为；不得把精确相等
  放宽为“>=”或用重跑取绿。
- 验收断言：Windows 同包该案稳定通过，且 StateVersion/EventSeq 精确等于
  reconciliation 基线；重复 5 次通过。

### 最小 scope 请求（一次给全）

1. `unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs`
   —— 仅上述 `RunReleasedControlScenarioAsync` 与
   `RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate` 两处测试同步/冻结；
2. `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`
   —— 仅 `DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect` 的 barrier。

不含生产代码、runner、verifier、asmdef。若 reviewer 只授权其中一项，请按项裁决，
不把未授权项写成已关闭。

## 6. 固定分母与逐行状态

| 行 | 状态 | 依据/边界 |
| --- | --- | --- |
| N1 | PASS（沿用已接受原件） | 本任务未重跑；未变化输入，不冒充新执行 |
| N2 | PASS（沿用已接受原件） | 同上 |
| N3 | PASS（沿用已接受原件） | sleeve/fee 正对照在已接受组件与 raw verifier 中 |
| N4 | PASS（本次 raw 复核） | delayed-old/closed-binding/role-switch raw 逐帧 verifier 通过 |
| N5 | PASS（本次目标用例） | 慢消费者真实队列溢出 5/5、room/其他 binding 继续、replacement attach |
| N6 | PASS（组件）/ FAIL（平台矩阵） | 控制 epoch/旧 decision 合同通过；Unity 7 案未全绿 |
| N7 | PASS（本次 raw 复核） | recovery raw 8 帧 verifier 通过；Windows recovery raw 缺失 |
| N8 | PASS（沿用已接受原件） | 未重跑；无新输入变化 |
| U1 | PASS（PlayMode 4 人/观战案） | 同候选真实 PlayMode |
| U2 | FAIL（7 案未全绿） | claim 后 stale submit 同步缺口 |
| U3 | FAIL（7 案未全绿） | 同上；冷重开在 Windows 另有精确状态竞态 |
| P1 | FAIL | Windows 同包 5/7，未达成七案 |
| P2 | FAIL/BLOCKED | Windows 恢复 raw 未 seal；同包矩阵失败 |
| C04-G5-01 | PASS（raw 范围） | four-owner 49 帧 owner 私有非空、public 无私有，verifier PASS |
| C04-G5-02 | PASS（raw 范围） | 私有 ledger/sleeve/offer 非空由真实 raw verifier 验证 |
| C04-G5-03 | PASS（raw 范围）/ 平台 FAIL | role-switch/delayed/closed-binding raw 通过；7 案包未全绿 |

## 7. 状态汇总

| 项 | 状态 |
| --- | --- |
| D05-FINAL-SYNC-01 修复 | **PASS（第 3 轮 application 绑定）**：5×5 定向 + drop→supersession 真实负例 + 183/183 |
| 完整 LocalHost 本轮 | **PASS 183/183**（第 3 轮首次 fresh；仍保留 SYNC-03 外部风险） |
| 完整 LocalHost 稳定性 | **FAIL/BLOCKED**（外部 SYNC-03：第 1 轮 15/17 次绿色；本轮未获授权修） |
| Client 182 | **PASS** |
| runner 16 / verifier 17+67 | **PASS** |
| 真实 PlayMode 7 案 | **FAIL 6/7**（D05-FINAL-SYNC-02） |
| 真实 Windows 同包 7 案 | **FAIL 5/7**（SYNC-02 + MATRIX-04） |
| Windows 完整包 manifest | **BLOCKED**（runner XML gate 失败，未 seal；仅补充只读清单） |
| raw verifier（PlayMode 4 场景） | **PASS**（67 负例/场景） |
| raw verifier（Windows recovery） | **NOT_RUN**（该案未 seal） |
| 最终 D05/C04/G2 | **未通过** |

## 8. 证据清单（本树 `build/`）

- 第 3 轮组件根：`build/3eeb24d990f141b5962ae79c5698b19a/`
  - `build.log` SHA `8637FCE55FE65D67641BEDADF7CFA7F144B1898A95D1D2BF0BB2CA13ED0B4BC2`
  - `localhost-full.xml` SHA `557DCD020E8ECF0A181B1AB6A8B24E60A07721C8C6F5C83B17AB79A36C5F2DD8`
  - `client-full.xml` SHA `A69823E92CB461140F9FB15F1F581F83C9371AE01C31DC53ABC44DF07B982474`
  - `r3-target-*-1..5.xml`（25 份，全部 total=1/pass=1）
  - `probe-positive.log` SHA `18231B9A00B78489B71106E0AE309CAAEA8175C693DF90A252B3A57726074779`
  - `probe-negative.log` SHA `D5E425EDF2CA3C85E8AEADF06D5A78D8091AA206EC229C40E66856069BF563A1`
- 第 2 轮组件根（旧四参数 probe 已撤回，仅保留历史）：`build/12d656809f6e46df95e61d5c75c8262d/`
  - `build.log` SHA `42A7821BA047C03EA52BF13844CA9BA4BDFD6AA215F11A50459619D539AC07AA`
  - `localhost-full.xml` SHA `57B14E5ECBB8D7B97B46F9C2C4513E07ED450DF1620C7EC983B0975BCE7DF2D8`
  - `client-full.xml` SHA `2569C98DCC1326E8F334AEA832F93E306335205381A77735C0EE286981BD0DAA`
  - `r2-target-*-1..5.xml`（20 份，全部 total=1/pass=1）
  - `probe-r2-defect.log` SHA `6826A55D804281946A1DD9CA9BD54BA2C700234E98B977DA6918BDA228BC14CE`
- 第 1 轮组件根：`build/02f82a1c6144498ab2d7b709f6942517/`
  - `build.log` SHA `9D07EE71EA637EA15707953634D4D65E0FCF82DAB122BF4631A0AF1E7D3018A8`
  - `localhost-full.xml`（首败）SHA `9DD43D...1D28CE`
  - `fixed-full-1.xml`（181/181）SHA `229142...C3BAB3EF`
  - `fixed-full-4.xml`（LC 失败）SHA `9FE238...4EB1C0`
  - `baseline-full-5.xml`（原缺陷复现）SHA `601D95C5CD9974DDEB834A4587655ABA029F42AF3F12BC7714D504C2C1C3B351`
  - `client-full.xml` SHA `7729DF...F486E2D2`
  - `runner-tests.log` SHA `EF4C98...54747354765`
- PlayMode 根：`build/02e27e3be3b34f1da5456356e88f0be4/`
  - `playmode-results.xml` SHA `CB5074...F02F9741A1`
  - `unity-editor.log` SHA `7A2DED...DC3A959D`
  - `summary.json` SHA `E14353...B31038182E11`
  - raw：`privacy-evidence/{role-switch,four-owner,closed-binding,recovery}-raw/raw-frames.json`
- Windows 根：`build/b5327245af6c4068b1112d69b6f0b788/`
  - `windows-player-results.xml` SHA `BB8FD7...55CCB4619A`
  - `unity-editor.log` SHA `C4B8C3...72D6D53`
  - `summary.json` SHA `FC5969...2FA27C8`
  - `test-player/PlayerWithTests/`（357 文件，补充清单 SHA `8b5570...99cce9d`）
- 每次 run 的 `manifest.sha256`、`seal.json` 由未修改 runner 生成；本报告未改写
  任何已封存文件。旧证据根只读引用，未覆盖。

## 9. 未关闭项与风险

1. PlayMode/Windows 失败案均由 Unity 测试自身的 post-receipt/同步点或自动推进
   精确断言引起；未证明生产 `TableSession`/authority 回归。但不能据此判平台通过。
2. 完整 LocalHost 的 2/17 次失败说明“全绿”仍非稳定属性；仅以一次绿色不足以
   接受，需 D05-FINAL-SYNC-03 的确定性修复。
3. 因 XML 失败，Windows 包未被 runner 封存为 PASS 包；补充清单只证明构建身份，
   不替代 `Get-C04TestPlayerPackage` 的合同。
4. 本轮未 push/PR/merge/deploy，未改 scheduler/complete.flag/许可/机器配置，
   未杀他人进程；`origin/v5` 与 PR20 远端未由本任务改变。
5. 未重跑 Runtime 483/483、TableModel 236/236、旧 Windows 12 局等未变化输入；
   按原身份保留，不冒充本候选新执行。

## 10. 交接结论

D05-FINAL-SYNC-01 已按第 3 轮要求把响应额度绑定到单个 application callback：
drop→supersession 真实负例、25 份定向 XML、183/183 LocalHost 与 182/182 Client
通过，正 probe 也验证正常应用仍可解锁；旧四参数 probe 证据已撤回。可交专属
reviewer 复核组件关闭。D05-FINAL-SYNC-02/03/MATRIX-04 仍为范围外 MAJOR，
最终 D05 矩阵与 G2 结论保持 **FAIL/BLOCKED**，等待原下发方范围裁决后在同一候选链
继续。按复核指令未在裁决前重跑昂贵 Unity/Windows 矩阵；远端 PR20 现头
`d8a7716b...` 为 caller 后续接受项，未拉入或归到本候选。本报告不 approve、
不合并、不把 component PASS 升级为 D05/C04/G2 PASS。
