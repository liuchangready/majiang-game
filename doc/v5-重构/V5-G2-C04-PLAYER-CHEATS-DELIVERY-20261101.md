# V5 G2-C04 三绝技固定真实 Player 专项交付（2026-11-01）

## 1. 绑定与冻结范围

| 项 | 值 |
|---|---|
| acceptedBase | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| frozenCandidate | `f3ba015b7b54061a0b144ddf546cd6f8e37548ca` |
| branch | `wt/v5-g2-c04-player-cheats-ds-20261101-1545` |
| 任务 ID | G2-C04-D04 / 固定 C04-G3-01..06 |
| Unity / .NET | `6000.3.27f1` / `D:\Unity\dotnet`（8.0.32） |
| 证据根 | 本树 ignored `build/c04-player-cheats-final-20261101-171354` |
| 结论上限 | 本文件只是本受测候选的 C04-G3 专项证据，**不是正式 C04/G2 通过**；恢复/动作增量合回后必须重新绑定最终版本 |

工作树从 acceptedBase 创建并 ff 到 frozenCandidate；开始前 `git status --porcelain` 为空、`git diff --check` 为 0、acceptedBase 为 HEAD 祖先。实现期间只新增本任务 13 个授权文件；Unity/共享 DLL 构建产生的 tracked 插件、字体、Settings、ProjectSettings、slnx 和 PerformanceTestRun 生成物已按精确路径恢复到 HEAD，未整目录 clean/restore。

## 2. 实现文件（逐文件）

| 文件 | 作用 |
|---|---|
| `dotnet/src/Mahjong.LocalHost/LocalPlayerCheatScenario.cs` | 唯一纯 .NET 受控初态：固定 136 实体 0..135、固定庄家手牌/牌山、120 初始点数、1/10/10 费用；只暴露枚举选择的 `CreateOptions` 与只读 facts，不接受调用方构造的轮/牌/账本 |
| `dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerCheatChannelTests.cs` | 4 个 .NET 用例：场景 136、三绝技真实 TableSession/LocalTableChannel/权威、缺失 decision 授权拒绝零增量、余额不足拒绝零增量 |
| `unity/.../App/C04PlayerCheatChannelFactory.cs` | 独立 `ITableChannelFactory`；嵌套通道实现 `ILocalTableTicketSource`，通过 AppComposition 现有 channelFactory 注入真实 LocalTableHost |
| `unity/.../App/C04PlayerCheatProbe.cs` | 严格命令行 opt-in Windows Player probe；真实 TablePresenter 按钮/意图 → ITableCommandSink → TableSession → LocalTableChannel；只读 LocalTableHost recovery capture 记录 136/ledger/request 事实 |
| `unity/.../Editor/C04PlayerCheatBuild.cs` | 仅 Editor 的 Windows64 构建入口；写 build report，不改设置/资产 |
| `unity/.../Tests/PlayMode/C04PlayerCheatScenarioTests.cs` | PlayMode 真实 LocalHost + 真实 TablePresenter 组件级驱动与授权拒绝用例 |
| `tools/unity/run-p45-c04-player-cheats.ps1` | 持 `Local\MahjongV5P45UnityValidation`；拒绝覆写证据根；build-shared/provenance guard/Unity build/Player/native exit/验证/manifest/summary |
| `tools/tests/C04PlayerCheats.Tests.ps1` | `SCRIPT_TOOL_FIXTURE` 11 项负例：缺 case/缺 receipt/错身份/哈希不符/非零 native/超时/空断言/未知 blocked/覆写拒绝 |
| 四个新 `.meta` | Unity 自动生成并核验全局唯一 GUID |
| 本报告 | 交付与逐行结论 |

## 3. 实际执行与计数

### 3.1 .NET（DOTNET_PURE）

命令：

```powershell
$env:DOTNET_ROOT='D:\Unity\dotnet'
dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.exe `
  -class Mahjong.LocalHost.Tests.C04PlayerCheatChannelTests `
  -result-xml build/c04-player-cheats-final-20261101-171354/dotnet-c04.xml
```

结果：**Total 4 / Passed 4 / Failed 0 / Skipped 0 / native 0**，6.780s。原 XML `66959BEBEC8CC3D0C0EDED0B45AEEB0A999ED20FD8468EA5F2E20B775F7435CC`；console `AE9A1CFC979170CAAD24C12ABA8A16A24A1B0A43B4C16D42296C7F6AF6A88F77`。

覆盖事实：

- `AuthorizedThreeCheatsUseTheRealChannelWithExactFeesAnd136Entities`：TileMark −1（119）、SleeveBloom −10（109）、BeamSwap −10（99）；3 条私有 ledger；三次 136/136；choice 不重复扣费；SleeveBloom SECOND/BeamSwap SWAP 造成物理哈希变化；持久化 request 的 raw payload hash 与 receipt 匹配。
- `MissingDecisionAuthorizationIsRejectedByTheRealAuthorityWithZeroDelta`：错误 decision 身份在权威处 Rejected；stateVersion/eventSeq/cheatVersion/cheatFingerprint/tiles hash/ledger entries/balances 全部零增量。
- `InsufficientBalanceIsRejectedByTheRealAuthorityWithZeroDelta`：12 次合法扣费后余额 0，下一次 SleeveBloom 被权威 Rejected（reason 含 `INSUFFICIENT`）；零版本/事件/ledger/实体增量。
- `ScenarioDealsAll136UniqueEntitiesAndPinsTheAcceptedEconomy`：136 唯一实体 0..135，费用/初始点数固定。

### 3.2 PLAY MODE（UNITY_PLAYMODE，组件层，不是 Player）

命令：

```powershell
Unity.exe -batchmode -projectPath unity/Mahjong.Unity `
  -runTests -testPlatform PlayMode `
  -testFilter Mahjong.Unity.Tests.PlayMode.C04PlayerCheatScenarioTests `
  -testResults build/dev-27bde11b520945fabd7f2a8660ea1738/playmode-c04e.xml
```

结果：**Total 2 / Passed 1 / Failed 1 / Skipped 0 / runner exit 2**。原件 `build/dev-27bde11b520945fabd7f2a8660ea1738/playmode-c04e.xml`（最终定向运行）；历史首个失败原件 `playmode-c04.xml`/`c04b.xml`/`c04d.xml` 保留。

- `UnauthorizedDecisionReceiptIsRejectedWithoutVisibleDelta`：PASS；真实 sink 发送错误 decision，authority Rejected，可见版本/点数/ledger 不变。
- `AuthorizedThreeCheatsThroughPresenterWithExactPrivateLedger`：FAIL；首轮 TileMark 入口缺失（`PrivateCheatView.TileMark == null`、`TileMarkButton` inactive），随后 SleeveBloom 被 stale `WindowId` 误判为 off-turn，收不到 `SubmitSleeveBloom` 回执。这是 PlayMode 复现的产品入口缺陷，不是测试假绿；没有 skip/放宽断言。

### 3.3 WINDOWS PLAYER（真实 GAME，本候选）

构建：`C04PlayerCheatBuild.Build`，`result=Succeeded`，Player 128,277,084 bytes；`totalErrors=3` 为 BuildReport 非致命错误计数，日志原件保留，不能称为 0 error 构建。共享 DLL 用本任务 final `LocalPlayerCheatScenario` 重新 build-shared，provenance 151 inputs / 19 outputs；`check-shared-plugins` 通过后才允许 Player 构建。

Runner：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-c04-player-cheats.ps1 `
  -Case authorized -EvidenceRoot build/.../player-authorized `
  -PlayerPath build/dev-.../player/Mahjong.exe `
  -SourceHead f3ba015b7b54061a0b144ddf546cd6f8e37548ca `
  -BuildCommit local-dev -SkipBuild
```

结果：**native 1 / status FAIL / errors = NATIVE_EXIT_1, MISSING_RAW_HASH ×2**；11 assertions，9 pass / 2 fail。

- `G3-03-RECEIPT` PASS：Player TablePresenter 发出 `SubmitBeamSwap`，权威 Applied，点数 120→110，ledger `-10@110/BEAM_SWAP`，136/136 守恒。
- `G3-03-OFFER` PASS：8 个真实墙候选、13 个可选手牌。
- `G3-04-BEAM-FEE` PASS、`G3-05-AFTER-BEAM` PASS。
- `G3-03-CHOICE-RECEIPT` FAIL：Player 的 SWAP choice 被权威 `REJECTED/MALFORMED_MESSAGE`；`G3-03-CHOICE` 超时，实体未发生 swap。**不能声称偷梁换柱成功。**
- `G3-01-UI-ENTRY` PASS（复现失败条件）：首轮 `tileMark=null`、`TileMarkButton.active=False`。
- `G3-02-UI-ENTRY` PASS（复现失败条件）：第二轮 `window=bloom-window-0-0-0`、immediate offer 为空；Presenter 因此选择 off-turn，immediate 路径不可达。

证据原件：`player-authorized/authorized/case-authorized.json` SHA `BEE118512C2AF52C3E2085FA3F305566172F299D22F696CA62D275B5F53B0B68`；`player.log` `0EE5F09627787AEC908D296987C0BF4297522BA26909836F97FA0A57E4D55520`；`c04-g3-cheats.save` `E3C559B4B2447D503EC7D66AF44AC41C72AD0CEB5F51F2375D4C2C43BD154008`；manifest `A5D24DD40B8609563868EB6E8AF66F74818E8F59950983D8864DE2785EAE6989`；summary `D58015013CC210B9235845F748A7F18C81B59F361DFE675754F429890310C091`。

### 3.4 SCRIPT_TOOL_FIXTURE

`tools/tests/C04PlayerCheats.Tests.ps1`：**passed=11 failed=0**，native 0。日志 `9F7DEFC74BBDFC83C25435E84FDFFD099EDF5D44C9F022B14F2437F12E9F8D0C`。该层只证明 verifier/拒绝覆写/缺 receipt/错身份/哈希/非零 native/超时，不代表任何 Player 游戏通过。

## 4. C04-G3 固定行结论（本受测候选）

| 行 | 结论 | 依据/缺口 |
|---|---|---|
| C04-G3-01 落汗成功 | **BLOCKED** | 首轮 `PrivateCheatView.TileMark == null`；`TableProjectionContractMapper.MapOwnerCheat` 在 `MarkGroups.Count == 0` 时返回 null；`TableCommandIntentFactory.CreateSubmitTileMark` 抛 `TILE_MARK_UNAVAILABLE`。.NET 权威路径 4/4 中 TileMark 成功，但不是 Player UI 证据 |
| C04-G3-02 袖里藏花成功 | **BLOCKED** | 弃牌后 `WindowId` 仍非空；`TablePresenter.OpenSleeveBloomDialog` 只要 `WindowId` 非空就选 `SubmitSleeveBloomOffTurn`，在自己回合被权威拒绝；immediate 路径不可达 |
| C04-G3-03 偷梁换柱成功 | **FAIL/PARTIAL** | 激活 Applied、费用/offer/136 均 PASS；SWAP choice 被 `MALFORMED_MESSAGE` 拒绝，无实体交换。.NET 组件层同 payload 的 SWAP 通过，差异在 Player presenter/command payload |
| C04-G3-04 精确费用/私有 ledger | **PARTIAL PASS** | Player BeamSwap 激活 −10、balance 110、一条私有 ledger；TileMark/SleeveBloom 未能在 Player 执行，不能给三笔完整结论 |
| C04-G3-05 136 物理守恒 | **PASS（本候选 Player + .NET）** | Player before/after BeamSwap 均 136/136，.NET 三动作均 136/136；save/tiles SHA 保留 |
| C04-G3-06 授权缺失/余额不足拒绝 | **.NET PASS / PLAYER NOT_RUN** | .NET 两独立 request 均 Rejected 且零增量；Player 侧因 G3-01/G3-02 入口阻断无法用同一 UI 完成 spend-down，未伪造 Player 结果 |

## 5. 本轮完整缺陷清单（一次给全）

### C04-G3-R1-01 BLOCKER — 首个落汗在 Player UI 不可达

- 位置：`unity/Mahjong.Unity/Assets/Scripts/Table/TablePresenter.cs:1234`（按钮仅在 `cheat.TileMark != null` 显示）、`TableCommandIntentFactory.cs:240-241`、`dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs:377`。
- 复现：Player `-c04-player-cheats-case authorized`，或 PlayMode `AuthorizedThreeCheats...`；观察 `LatestView.Own.Cheat.TileMark == null`、`TileMarkButton.activeSelf == false`。
- 实际：首轮无法打开/确认落汗；`CreateSubmitTileMark` 抛 `TILE_MARK_UNAVAILABLE`。
- 期望：在有合法 decision 的自己的回合，即使尚无 mark group，也应投影可选 4 张手牌并允许首次提交。
- 证据：`case-authorized.json` 中 `G3-01-UI-ENTRY` actual `tileMark=null buttonActive=False`；PlayMode XML `playmode-c04e.xml`。
- 修复要求：投影层在 `MarkGroups.Count == 0` 时仍提供 owner `TileMark`（含 selectable hand IDs、roundId），Presenter 仅在真正无 decision/无候选时隐藏按钮；不得用 probe 直接提交或手写 view 绕过。
- 验收断言：同一 Player/PlayMode 路径首次 mark 得到 Applied、−1、119、一个私有 group、136 不变；错误 seat/epoch/round 仍 Rejected 零增量。

### C04-G3-R1-02 BLOCKER — 袖里藏花 immediate 路径被 stale off-turn window 抢占

- 位置：`TablePresenter.cs:1856-1880`（`isOffTurn = !string.IsNullOrEmpty(cheat.WindowId)`）、`dotnet/src/Mahjong.TableRuntime/TableActor.cs:1630-1636`（弃牌开窗）、`SleeveBloom.WithClearedWindow` 仅在 reservation delivery 使用。
- 复现：Player 首轮弃北后推进到庄家下一摸；观察 `window=bloom-window-0-0-0`、`ImmediateSleeveBloom == null`；点击 `SleeveBloomButton` 实际发出 `SubmitSleeveBloomOffTurn`。
- 实际：immediate `SubmitSleeveBloom` 无法发出；off-turn 在自己回合被权威拒绝；PlayMode 收不到 `SubmitSleeveBloom` 回执。
- 期望：自己回合且 `DrawnTile` 属于自己时应走 immediate；off-turn window 在所有者下一次摸牌/回合边界关闭或不再被当作 immediate 的替代。
- 证据：`case-authorized.json` 的 `G3-02-UI-ENTRY` actual `window=bloom-window-0-0-0 offer=False`；PlayMode 失败原件。
- 修复要求：明确 window 生命周期并在 Presenter 选择路径时同时校验 current seat/phase；禁止用 `KeepOriginal` 或仅创建 offer 冒充换牌成功。
- 验收断言：Player immediate 激活 −10、109、offer，SECOND 后实体哈希变化；off-turn 仅在真正他家回合可提交并有 reservation delivery 证据。

### C04-G3-R1-03 BLOCKER — Player BeamSwap SWAP choice 被 MALFORMED_MESSAGE 拒绝

- 位置：`TablePresenter.OnConfirmBeamSwapClicked` → `TableCommandIntentFactory.CreateSubmitBeamSwapChoice` → `CommandPayloadFactory.SubmitBeamSwapChoice`；权威 `LocalTableRuntime.SubmitCheatAsync` 解析 `HandTileId`/`TargetSlot`。
- 复现：Player authorized 场景，BeamSwap 激活 Applied 后选 `BeamSlot_0` + 一手牌 + Confirm；receipt `REJECTED/MALFORMED_MESSAGE`。
- 实际：`SubmitBeamSwapChoice` payload 被权威判 malformed，offer 未清除，无实体交换。
- 期望：合法 SWAP 得到 Applied，`BeamSwapResolved(SWAP)`，手牌/墙实体互换，136 不变。
- 证据：`case-authorized.json` request `57f4d510...` status `REJECTED/MALFORMED_MESSAGE`，payload SHA `3599B1EA...`；Player log。
- 修复要求：对比 Player presenter payload 与 .NET 已通过的同名 payload 字节；修正可选字段写入/解析（不得放宽 verifier）；若需改共享 Contracts/Tableruntime，先提交最小文件请求，不在本任务扩层。
- 验收断言：同一 Player 路径 SWAP Applied；choice 不重复扣费；实体哈希变化；错误 slot/hand 仍 Rejected 零增量。

### C04-G3-R1-04 MAJOR — Player 证据缺 raw payload hash

- 位置：`C04PlayerCheatProbe.CheckRequestApplied/Rejected` 与 evidence request 记录。
- 复现：runner 验证 `case-authorized.json` 报 `MISSING_RAW_HASH ×2`。
- 实际：两条 request 的 `rawPayloadSha256` 为空，只有 canonical digest。
- 期望：每条成功/拒绝 request 都有 canonical digest 与 64-hex raw payload hash，并与权威 request fact 一致。
- 证据：`runner-authorized.log`；`case-authorized.json`。
- 修复要求：在 receipt 落账时从权威 request fact 或原始 canonical bytes 填 raw hash，禁止用空值/近似值通过。
- 验收断言：verifier 对每条 request 要求 64-hex raw hash 且等于权威 fact；缺失即 FAIL。

### C04-G3-R1-05 MAJOR — 定向 PlayMode 失败与 G3-06 Player 未运行

- 位置：`C04PlayerCheatScenarioTests.AuthorizedThreeCheats...`；Player negative 未执行。
- 复现：PlayMode 命令 native 2，1 passed / 1 failed；Player runner 只跑 authorized。
- 实际：PlayMode 在 TileMark/SleeveBloom 入口失败；negative Player 为 NOT_RUN。
- 期望：两个 PlayMode 用例均 PASS；Player authorized + negative 均有 native 退出与逐行结论。
- 证据：`playmode-c04e.xml`；`runner-authorized.log`。
- 修复要求：先关闭 R1-01/R1-02，再重跑 PlayMode 和 negative；不得跳过失败用例或用组件证据代替。
- 验收断言：PlayMode Total 2 / 2 PASS / native 0；Player authorized 与 negative 均 native 0 且逐 request/ledger/136 断言通过。

### C04-G3-R1-06 MINOR — BuildReport errors=3 与成功结果并存

- 位置：`C04PlayerCheatBuild.Build` 报告与 runner。
- 复现：Player build report `result=Succeeded, errors=3`。
- 实际：Player 可启动并产生证据，但报告不是 0 error。
- 期望：要么解释并消除 3 个非致命 error，要么在 summary/manifest 中单列 `BUILD_REPORT_ERRORS=3`，不得写成 0 error。
- 证据：`player-build2.json` / `player-build2.log`。
- 修复要求：保留原件；build script 记录 errors 原值，runner 明确显示。
- 验收断言：最终 build report `errors=0`，或报告与 summary 均显式记录非零并说明原因。

## 6. 已确认的正向/边界证据

- 受控初态：`LocalPlayerCheatScenario` 固定 14 张庄家手牌与按槽位固定的 136 实体 0..135；没有 private reflection、没有预写 receipt/event/ledger。
- 费用：本候选实际配置 `TILE_MARK=1, SLEEVE_BLOOM=10, BEAM_SWAP=10, initial=120`；.NET 精确断言 119/109/99 与 3 条 ledger；Player BeamSwap 断言 −10/110。
- 136：Player 前后 recovery capture 均 `TileCount=136/Unique=136`；save SHA 与 tiles SHA 保留。
- 拒绝：.NET unauthorized/insufficient 均 Rejected 且 stateVersion/eventSeq/cheatVersion/cheatFingerprint/tiles/ledger/balances 零增量；两 request ID/payload 不同。
- opt-in：无 `-c04-player-cheats` 时 `Bootstrap` 在创建 GameObject/目录/host/线程前返回；工具测试验证 runner 拒绝覆写已存在证据根。
- 清理：Player 只写本树 ignored `build/<GUID>`；Unity 生成的 tracked 插件/设置/字体/slnx 已按精确路径恢复 HEAD，未删历史证据/用户原件。

## 7. 最小必要文件请求（不擅自扩层）

要关闭上述 BLOCKER，需要另行授权的最小范围：

1. `unity/Mahjong.Unity/Assets/Scripts/Table/TablePresenter.cs`：首 mark 入口、immediate/off-turn window 选择、SWAP choice 提交。
2. `dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs`（及必要的 `TableVisibilityProjection.cs`）：首个 `TileMarkView` 的 selectable hand IDs。
3. 若 R1-03 证明是 wire 合同而非 presenter：`dotnet/src/Mahjong.Contracts/ProtocolCommand.cs` / `LocalTableRuntime.SubmitCheatAsync` 的最小解析修复。

本任务未修改以上任何文件，也未修改 Luna 的 `LocalTableChannelFactory.cs`/`LocalPlayerActionScenario.cs`/动作 runner/测试或恢复 D01 文件。

## 8. 历史/平台边界

- 已有 C01 12 rounds、C02 8 GAME、C03 2,820/2,820、UI15 等均为历史证据；本轮未重跑，不据此声称新运行。
- 本轮只执行 4 个 .NET 定向用例、2 个 PlayMode 定向用例、1 次 Windows Player authorized probe 和 11 项工具负例；未运行 Android/iOS/PG/随机整场/正式 C04 矩阵。
- PR20 保持 OPEN/Draft；不 push、不改 PR、不 record MR、不 approve、不合并 v5、不部署、不改 scheduler/complete.flag。
- 本轮结论仅对本受测候选 `f3ba015b` + 本任务新增文件闭包有效；恢复/动作增量合回后需重新绑定并重跑。
