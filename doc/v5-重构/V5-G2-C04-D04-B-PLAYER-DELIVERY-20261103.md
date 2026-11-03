# G2-C04-D04-B 真实 Player 流程、原始采集与第二 discard 修复交付

日期：2026-11-03，Asia/Shanghai

任务：G2-C04-D04-B / 原 R1-05（真实 Player 第二 discard 与完整正负流程）

执行者：DeepSeek Harness（targetAgent=deepseek_harness，本线程唯一执行者）

工作树：`E:/majiang-review/g2-c04-d04-player-ds-20261103`

分支：`wt/v5-g2-c04-d04-player-ds-20261103`

冻结 base：`a234432e57efa790c40987d9720091d93bd270a8`（既有 Draft PR20 头）

代码提交：`0dac2cefa8b943e91d50473910e81b88f0d3fd8b`（首轮 `60f43ad7`、R1 `5dc8534a`、R2 `223284df`、R3 `28bd63d0` 的最终 Player 推进修复）
冻结集成 HEAD：`0dac2cefa8b943e91d50473910e81b88f0d3fd8b`（B `0dac2cef` + accepted C `f042fbf1/69b9e669` + caller `9243ad91/97f9ba21`）
证据根：`build/i57a9cb72f/`（最终任务卡/预检/日志）、`build/c5a1b3/`（fresh Player 包/case/A 输出）、`build/p5a1b3/repo/`（同 DLL 复制 PlayMode）

## 结论（组件与实际边界）

本交付已完成 **B 自身可执行的确定性组件/意图/真实通道/PlayMode 以及最终同包 Windows Player 六行**：定位并修复了“SleeveBloom SECOND 后第二 discard 失败”的真实产品原因，更新了过时的 110/100、隐藏入口断言，完整正流程在真实 `TablePresenter → TableIntent → TableSession → LocalHost` 链上实际跑到 120→119→109→99、3 笔私有 ledger、SECOND/SWAP 精确物理换位。R1–R3 补齐了 raw 基线、discard 原始件关联、raw 精确绑定、B producer→A、PlayMode 负例/136/精确换位断言。最终在冻结集成候选 `0dac2cef` 上实际生成 fresh Windows 完整包并执行 `-Case all`：**authorized PASS 29/29、negative PASS 15/15、A Verifier2 两 case native 0、包 seal PASS（1612 files / 51,896,845 bytes）、BuildReport errors 0 / stepErrors 0 / stepExceptions 0、PlayMode 3/3**。

同包六行已实际通过，B 自身范围结论为 **PASS（组件 + 同包 GAME）**。D04/C04/C05/C06 与 G2–G5 的项目级批准仍由 caller/reviewer 决定；本报告不代替 D05 或其他任务裁决。

## 1. 范围与最终 diff

实际 tracked 变更仅 6 个代码路径 + 本报告，全部在拆分合同的 12 路径内：

```text
dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerCheatChannelTests.cs
dotnet/tests/Mahjong.Unity.TableModel.Tests/TableCommandIntentFactoryTests.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerCheatChannelFactory.cs
unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerCheatProbe.cs
unity/Mahjong.Unity/Assets/Scripts/Table/TablePresenter.cs
unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PlayerCheatScenarioTests.cs
doc/v5-重构/V5-G2-C04-D04-B-PLAYER-DELIVERY-20261103.md
```

未改 Domain、Client、Contracts、TableActor、LocalTableRuntime、asmdef、D01/D03/UI15/census、A/C verifier/runner/build、其他树、机器设置、调度器、锁或 `complete.flag`。未取用 C/Luna 在途代码，未 push/建改 PR/MR/merge/deploy/approve。`git diff --check` native 0。代码与文档分开提交（本报告为 docs-only 提交）。

## 2. 缺陷映射：C04-G3-R1-05（原 MAJOR，旧 8 轮历史）

### 2.1 根因 A：合法 discard 选择后 `DiscardButton` 永远不可用

- 位置：`TablePresenter.RenderActionButtons`。
- 实际：`SelectDiscardTile` 内部调用 `SelectAction`，得到 `TableSelectionKind.Action`，`TableSelection.HandTileId` 为 null；旧代码却用 `Selection.HandTileId.HasValue` 控制 `DiscardButton`，因此任何合法出牌选择后按钮都是 `active=False/interactable=False`。旧 probe 用 `button.onClick.Invoke()` 绕过 interactable，掩盖了真实玩家无法点击的事实。
- 复现：PlayMode 首次 GL 运行，日志 `C04_TEST_DISCARD ... selection=Action/DiscardTile button=False/False`；XML 在 `first discard` 断言失败。
- 证据：`unity-playmode-original-gl2.log` SHA `CA162DA691141509E1E3BE48946D726F645C1DF1A905B235C9CD5189D1348A44`，`unity-playmode-original-gl2.xml` SHA `6C0F823D939368A59B39B2ECA988F0980466AFB458347DA08EA0A47DBCE393E0`。
- 修复：`discardSelected = Selection.Kind == Action && Selection.Action.Action == DiscardTile`；`Cancel` 按钮改用 `Selection.HasSelection`。
- 验收：PlayMode 实际点击 `DiscardButton` 得到 `button=True/True`、`SubmitAction APPLIED`。

### 2.2 根因 B：SECOND 后把“未确认的新摸牌实体”当作可出牌候选

- 位置：`C04PlayerCheatProbe.DiscardEntity`、`AdvanceToNextDealerTurn`、PlayMode `DiscardEntity`。
- 实际：SleeveBloom SECOND 后权威仍保留同一 `DecisionId` 的旧 candidate 集；`PrivateView.DrawnTileId` 变成从 wall 换入的新实体，但该实体不在确认 candidate 中。旧 probe 直接 `DiscardEntity(bloomed.Own.DrawnTileId)`，`SelectDiscardTile` 返回 `NOT_A_CANDIDATE`，随后按钮不可用，`G3-ADVANCE-2` 超时。历史 .NET 通道测试直接发 `SubmitAction` 可绕过 UI candidate，故旧 .NET 测试一直绿、真实 UI 一直红。
- 复现：PlayMode 日志 `requested=0 selected=0 decision=local-human-10-0-CheatWindow closed=... drawn=14 candidates=...`；`second`/新摸牌实体不在 candidates，fallback 选择仍在手中的确认候选 0 后 `APPLIED`。
- 修复：
  - `TablePresenter` 仅允许选择 `PrivateDecision.Candidates` 中 `DiscardTile` 的实体；非候选点击 fail-closed 并显示原因，不再静默。
  - probe/PlayMode 从“仍在手中且被确认”的候选里选择：优先请求实体，否则选择合法手牌候选；记录 `requested/selected/decision/closed/drawn/candidates/selection/button/pending/receipt/version` 有界诊断。
- 证据：同上 GL2→GL3 日志；`.NET` `C04PlayerCheatChannelTests` 新增断言 `DoesNotContain(second, discardCandidates)` 且断言仍可用在手中的 candidate。
- 验收：第二 discard 实际 `SubmitAction APPLIED`，随后第三 dealer turn 打开、BeamSwap 99/3 与 SWAP 全部通过。

### 2.3 残留边界（精确、未冒充修复）

SECOND 后权威未重新打开 decision（`LocalTableDriver` 只在 `GetPendingDecisionId(seat)==null` 时开新 decision），因此“换入的新摸牌实体”在本 decision 内仍不是可出牌候选。B 授权范围不含 TableActor/LocalTableDriver，本修复让真实 Player 只选择契约内合法候选并继续完整流程；没有改权威、没有造候选、没有绕过授权。若要求“换入实体本身可立即打出”，需要另立精确最小请求修改 `LocalTableDriver`/`TableActor` 的 cheat 后 decision 刷新，不能由 B 暗中越界完成。

### 2.4 R1 复核 A：raw before 基线改为命令即时前态

- 复核实际：`RunAuthorizedCaseV2` 在 mark 后 capture，随后两次合法 discard/advance 未更新 `_lastCapture`；`sleeve-bloom` 的 before 错误复用 mark post-state。A Verifier2 要求 `beforeFacts.StateVersion == command.ExpectedStateVersion` 且 accepted recovery 必须紧邻命令提交。复核把 `action[1].before = action[0].after` 后得到 native1：`sleeve-bloom:COMMAND_BASELINE_VERSION_MISMATCH`、`The accepted action command version does not immediately follow its baseline.`。
- 修复：在 SleeveBloom 点击前、所有合法 turn 推进之后重新 `CaptureFacts()`，并以该 capture 作为 `bloomBefore`；其余行保持“上一动作 after = 下一动作 before”的即时关系。没有放宽 A、没有改 A/C 文件、没有拿 latest 代替。
- 验证：新增 `AuthorizedSleeveBaselineIsImmediatePreCommandStateNotMarkPostState`（真实 LocalHost 通道）：mark post-state 与 bloom 命令 baseline 版本必须不同，baseline 版本等于当前 snapshot，ledger 119/1；bloom APPLIED 后版本前进。
- 独立 producer/consumer 正负对照（A 工具，非 Unity/Player）：fresh A capture + verify **native 0 / PASS**；仅把 `sleeve-bloom.before` 替换为 `tile-mark.after` 后 **native 1**，错误与复核完全一致。证据：`r2-producer-capture.log`、`r2-producer-verify-positive.json`、`r2-producer-verify-stale.json`。

### 2.5 R1 复核 B：discard 原始 intent/receipt/publication 关联

- 复核实际：`DiscardEntity` 的 `sentStart/receivedStart` 未使用；诊断只有 `pendingRequestId`（提交前通常为空），APPLIED 后只等 `StateVersion` 增长，无法定位具体 discard command/receipt/accepted PlayerUpdate。
- 修复：提交后记录实际 `requestId`、`commandVersion`；用捕获区间定位原始 command、typed receipt、按 `CommandVersion` 匹配的 accepted PlayerUpdate；写出 `raw/discard-NN-command.pb`、`-receipt.pb`、`-private-update.pb` 并把 path/SHA 写入 `C04DiscardDiagnostic`。任一原件缺失即 `DISCARD_RAW_ASSOCIATION_MISSING`，有界 5s 等待 publication；最终还校验 publication 仍在 received transcript 中，仍检查 `DiscardButton` 真实 active/interactable 与合法候选。
- 验收：PlayMode 日志 `pending=none request=... commandVersion=... receipt=APPLIED`，诊断含三份原始件 SHA；不再用泛化 view version 代替目标提交。

### 2.6 R1 复核 C：PlayMode 六行覆盖补齐

- 新增 `InsufficientFundsIsRejectedByRealPresenterWithZeroAuthorityDelta`：真实 Presenter 连续 12 次合法 SleeveBloom 到 0，再发动一次得到 **REJECTED / CHEAT_INSUFFICIENT_FUNDS**；前后 authority `stateVersion/eventSeq/cheatFingerprint/tilesSha256/完整136 audit/ledger count/balance` 全部相等。
- 新增 136 物理审计：通过只读 `C04PhysicalSnapshot`（App 薄封装 `LocalTableHost.CaptureRecoveryAsync` + `LocalPlayerActionScenario.ReadPhysicalAudit`），初始/每次关键提交/最终均断言 0..135 恰好一次共 136。
- 精确 SECOND/SWAP：SECOND 断言 `drawnBefore` 从 `HAND|0|` 到 `WALL|`、`second` 从 `WALL|` 到 `HAND|0|`；SWAP 断言选中的 hand 实体从 `HAND|0|` 到目标槽位的精确位置、目标实体到 `HAND|0|`，不再只看 offer 消失或余额。
- PlayMode 最终 **3/3 PASS**，skipped 0；仍是组件层，不是同包 Player 六行 GAME。

### 2.7 R1 复核 MINOR：合法 discard 高亮

- 修复：`RenderOwnHand` 的高亮从 `Selection.HandTileId` 改为 `SelectedHandEntity()`；Action/DiscardTile 选择取 `Action.TileIds[0]`，cheat 对话框仍用 `HandTileId`。
- 验收：PlayMode `AssertSelectedDiscardHighlight` 对实际选中 closed/drawn 实体断言对应文本为 `Color.yellow`，并在每次 discard 前执行；Cancel 仍清空选择。

### 2.8 R2 复核 B：diagnostic 失败必须传播为动作/用例失败

- 复核实际：`WaitReceipt` 对 REJECTED 也把 `_waitOk=true`；新 raw 失败分支只写 `diagnostic.failure`，没有设置 `_waitOk=false` 或 `_caseFailed`；最终 transcript 失败只写 `diagnostic.waitOk=false`，没有回写 `_waitOk`。调用方只检查 `_waitOk`，因此缺原件/拒绝 receipt 仍可继续并保持 PASS。
- 修复：新增统一 `FailDiscard`，每个失败分支同时 `diagnostic.failure=code`、`diagnostic.waitOk=false`、`_waitOk=false`、`Check(...false)`（置 `_caseFailed=true`）；成功只有在 APPLIED、request/commandVersion、原始 command/receipt/publication 关联、publication 仍在 transcript、UI view 前进、`stateVersionAfter >= commandVersion` 全部成立后才把 `_waitOk=true`。`pendingRequestId` 只记录真实 pending，不再用 receipt ID 冒充 admission；request 证明由原始 command+typed receipt+publication 提供。
- 实际回归：PlayMode 正流程每次 discard 都通过该路径；`TryAssociateRaw` 对缺失 request 的负例断言 false。完整 6 变体矩阵仍应由未修改的 A/独立 fixture 复跑（normal/rejected/missing command/receipt/publication/outside transcript），本报告不把日志当拒绝；代码现在在任一变体上中止外层次并置 FAIL。

### 2.9 R2 复核 C：精确双向位置、无关实体与 unauthorized 零副作用

- SECOND：新增 `AssertOnlyExchange(before, after, drawnBefore, second)`：`after[first] == before[second]`、`after[second] == before[first]`，并逐实体断言其余 134 位置不变。
- SWAP：`AssertOnlyExchange(beforeSwap, afterSwap, handTile, targetTile)`，同样双向精确互换且其余 134 不动；不再只看 `HAND|0|` 前缀或单一方向。
- unauthorized：提交前/拒绝后各做一次只读 authority snapshot，断言 `stateVersion/eventSeq/cheatFingerprint/tilesSha256/完整136 audit/ledger count/balance` 全等；记录真实 `requestId` 与 payload SHA；与 insufficient 一起覆盖两个独立请求/载荷、精确 `DECISION_MISMATCH` / `CHEAT_INSUFFICIENT_FUNDS`。
- PlayMode 最终仍 `3/3 PASS`，新增断言全部实际执行。

### 2.10 R3 复核 B：TryAssociateRaw 精确绑定与 B producer→A smoke

- 复核新信息：旧 `TryAssociateRaw` 只按 requestId 选 command/receipt、按调用方 version 独立选 publication，未比较 typed receipt 的 CommandVersion/status 与 zone/room/session/seat/private owner；因此“另一已存在版本”、伪造 REJECTED receipt、foreign session/room publication 都会被误接受。
- 修复：association 现在以 command 的 scope/room/session/seat 为根，要求 typed receipt 同 scope/room/session/seat、canonical digest 一致；positive 必须 **APPLIED + HasCommandVersion/EventSeq + reason 为空 + caller version 与 receipt 版本一致**；negative 必须 **REJECTED + 无 command/event version + reason 非空**；publication 必须由 receipt 的 `CommandVersion` 匹配，并校验 envelope room/session/seat、snapshot session、private owner seat、optional scope，以及对应 cheat 事件类型/Private visibility。缺任一返回 false，probe 经 `FailDiscard` 中止。
- PlayMode 负例矩阵：missing request/command/receipt/publication、wrong unknown version、**另一已存在版本**、typed receipt 改 REJECTED、publication 改 foreign session、foreign room 全部断言 false；正常 5 行仍 true。
- B producer→unchanged A：PlayMode 用生产 `C04RawEvidenceExporter` 将真实通道/恢复原件写出 5 行 authorized index 和 2 行 negative index；未修改 A Verifier2 直接验证：**authorized native0 / PASS**、**negative native0 / PASS**，negative 精确 `DECISION_MISMATCH`、`CHEAT_INSUFFICIENT_FUNDS` 且 state/event/ledger/physical SHA 前后相等。
- 这仍标注为组件/PlayMode 证据，不是 Windows Player 或最终同包 GAME。

## 3. 原始通道采集（A 合同对齐）

`C04PlayerCheatChannelFactory` 新增 `C04RawFrameCapture`：在真实 ticket channel 的 `SendAsync`/`ReceiveAsync` 处逐帧保存原始字节，不复制解码/判断逻辑，不合成帧。probe 使用捕获帧、receipt `CommandVersion` 和只读 `LocalTableHost.CaptureRecoveryAsync` 写出 A 合同要求的：

- `raw/<action>-welcome.pb`
- `raw/<action>-command.pb`
- `raw/<action>-receipt.pb`
- `raw/<action>-private-update.pb`（authorized，按 receipt commandVersion 匹配 PlayerUpdate）
- `raw/<action>-before.save` / `-before-cheat.bin`
- `raw/<action>-after.save` / `-after-cheat.bin`
- `raw/index.json`（`schemaVersion=2`, `producer=LOCALHOST_CHANNEL`, `sourceHead`, `packageManifestSha256`, 五行动作，`sentFrames`/`receivedFrames` 全量原始帧）
- case JSON 增加 `rawEvidenceIndex` / `rawEvidenceIndexSha256` / `packageManifestSha256`

五行动作 id 固定为 `tile-mark`, `sleeve-bloom`, `sleeve-second`, `beam-swap`, `beam-swap-choice`；负例行固定为 `unauthorized`、`insufficient-funds`，不写 accepted publication。每行 before 在命令点击前即时 capture，after 使用 receipt 后的 capture；SleeveBloom 行不再复用 mark post-state，也不拿后台 latest 替代目标提交。Player 原始件本身本轮 **NOT_RUN**，最终仍须由冻结同包运行后交给 A Verifier2；不得把本段代码能力当成已通过原始件验证。

## 4. 实际执行与计数

### 4.1 .NET 组件

```text
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --artifacts-path build/<GUID>/localhost-artifacts
D:/Unity/dotnet/dotnet.exe <...>/Mahjong.LocalHost.Tests.dll
```

- R1 最终 native 0；**LocalHost Total 179 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0**（26.542s）。
- `C04PlayerCheatChannelTests` 定向 native 0；**5/5**（新增 raw 基线回归）。
- 首轮全量曾因 isolated artifacts 未生成 `Mahjong.TableRuntime.Tests/bin/Debug` 与 `Mahjong.LocalHost.CrashWorker/bin/Release` 出现 13 个 fixture 缺失失败；补齐这两个 helper 的默认输出后重跑得到 178/178。R1 增加一个测试后为 179/179。首次失败保留，不当作产品失败或最终通过。

```text
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj --artifacts-path build/<GUID>/tablemodel-artifacts
D:/Unity/dotnet/dotnet.exe <...>/Mahjong.Unity.TableModel.Tests.dll
```

- 最终 native 0；**TableModel Total 236 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0**。
- `TableCommandIntentFactoryTests` 定向 **24/24**，含新增 `DiscardSelectionRejectsSwappedInDrawAndUsesOnlyConfirmedInHandCandidates`。

### 4.2 Unity PlayMode（同工作树、刷新后的 shared plugin 闭包）

```text
D:/Unity/Hub/Editor/6000.3.27f1/Editor/Unity.exe -batchmode \
  -projectPath unity/Mahjong.Unity -runTests -testPlatform PlayMode \
  -testFilter Mahjong.Unity.Tests.PlayMode.C04PlayerCheatScenarioTests \
  -testResults build/<GUID>/r7-unity-playmode-3.xml \
  -logFile build/<GUID>/r7-unity-playmode-3.log
```

- R1 最终 native 0（Unity log: `Test run completed. Exiting with code 0 (Ok)`）。
- XML：`total=3 passed=3 failed=0 inconclusive=0 skipped=0`；`AuthorizedThreeCheatsThroughPresenterWithExactPrivateLedger`、`InsufficientFundsIsRejectedByRealPresenterWithZeroAuthorityDelta`、`UnauthorizedDecisionReceiptIsRejectedWithoutVisibleDelta` 均 PASS。
- 正流程实际：TileMark -1→119、SleeveBloom -10→109、SECOND 不重复扣费、第二 discard 使用确认候选 APPLIED、BeamSwap -10→99、SWAP 不重复扣费；SECOND/SWAP 精确物理位置与 136 审计均实际断言；负例精确 `DECISION_MISMATCH` / `CHEAT_INSUFFICIENT_FUNDS`，后者 state/event/cheat/136/ledger 零变化。
- 运行前为通过既有 shared-plugin provenance gate 执行了默认 `tools/unity/build-shared.ps1`（0 warning/0 error，19 files），随后已用 `git checkout` 还原 14 个 tracked plugin DLL/PDB；最终 tracked 状态只有允许路径。最终正式包必须重新构建并以包内 DLL 身份重跑，不能复用本次 transient 同步。

Unity 版本/进程：`6000.3.27f1`；未改机器许可；未终止他人既有 Unity 进程。`-nographics` 首跑出现 `No graphic device is available to initialize the view`，属于环境限制，已保留并按桌面 GL 重跑。

## 5. 证据与 SHA-256

全部位于 `build/6ea1d77e-57e2-4c7f-bca8-1d4ce9ca3d46/`：

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `c04-class-final.log` | 872 | `6910137FB5E5E1600819799FEDFCC4A90ABDDF048101DEC02A49ADFF5DC53DC2` |
| `localhost-full3.log` | 878 | `E1EF5C68B6DAFAC354EA233540DC2ABF331D0EDE7C3321258D49ACEDCBE2F582` |
| `tablemodel-class.log` | 944 | `2330C41C863E7F2E2B09C1393A3737F1DAE3ADF2BDECC4C4C916C938EC3A06BD` |
| `tablemodel-full.log` | 946 | `060884C3848D3DE98BA9A434B6209F1B4554BED54E0266FD3AEAE92C98D8306F` |
| `unity-playmode-original-gl3.xml` | 5860 | `49E46CE2A89074C6AA5EC3E420CEF32EC99E7BED66006AFF2563784B92F00F43` |
| `unity-playmode-original-gl3.log` | 136906 | `6814413A78EFCD9C6BB6B7CD656C3F33A1B11BB139A786600E31C0B9765668D4` |
| `unity-playmode-original-gl2.log`（按钮首失败） | 71713 | `CA162DA691141509E1E3BE48946D726F645C1DF1A905B235C9CD5189D1348A44` |
| `unity-playmode-original-gl2.xml` | 7175 | `6C0F823D939368A59B39B2ECA988F0980466AFB458347DA08EA0A47DBCE393E0` |
| `unity-playmode-original.xml`（无 GL 首败） | 7131 | `99DF1503B7EBAC05AF6663FB8AB0B395EE9D9143CDE60EC74F6640D0FCCA6B0F` |
| `unity-playmode-original.log` | 1418197 | `EB48DB19A7BCAEB3105D08CF50E3679607B9AEF8C6D8624E9B3B2252078CDEAD` |
| `build-shared-default.log` | 10470 | `E803A9C3103988BF50C801B05BD776C8317F5C9C643569231FEADCF3A2B8F99C` |
| `build-shared.log`（scratch） | 15520 | `F087E649AB2197FFDB0201522D2E3196B4E46A2628FEEB888977FE22A81AB56E` |
| `r2-c04-class.log`（5/5） | 872 | `D5FE227F14E6617EE0534911DB11B0868DAEA5B84182979B732D88664C06095B` |
| `r2-localhost-full.log`（179/179） | 878 | `3F2AD13B0855B4B07B3142EEF0DA00F461878E2EAB2BB29EB3DAAFA66982E264` |
| `r2-unity-playmode.xml`（3/3） | 10827 | `E27C5F8113E54C5C5491F67EF0C6972850C1B09880133E63C692F3EF7857038F` |
| `r2-unity-playmode.log` | 371528 | `1F70D8D071269BB1F51F3B9F1FF9CA5595FC9CDB65A12FB5795AC6C0E62FC37E` |
| `r2-verifier-build.log` | 5046 | `2CD165BD7973BEFBCE78553273567A50904E2292FFFA71595FC76E970C412725` |
| `r2-producer-capture.log` | 588 | `E0248EEE0F4599A3778A204199DFED6E1562C888CB57552CC17F9BBF331B17CD` |
| `r2-producer-verify-positive.json` | 6696 | `CBBF08E25C0BBB37D182ED5381AEC03F4CEA1C5358BA0C2AF56907CAF072DBF2` |
| `r2-producer-verify-stale.json`（预期 negative） | 2406 | `48572200B67CF1EF9529F0A50BBA08B04F7ED9B1E31637210220BC16B5FB5421` |
| `r2-build-shared-default.log` | 10470 | `50FCC58FCB7572708CC92EECB6CF666FF0F9185C09DB6C2215D2ABA154417C52` |
| `r3b-unity-playmode.xml`（R2 最终 3/3） | 10830 | `F91E06069132A6ECF4C1016FAC32EF92D1D19921E88CFDC6481C18B709D22877` |
| `r3b-unity-playmode.log` | 371593 | `C6F31E77735DC3FDE6B8C2FE84CF6362BD60F6D02B7FD4F43BED7BB104693233` |
| `r3b-build-shared.log` | 10470 | `E4614F378C4678714628CC7D20CB9C8279B9956891012F9983D5B1D8638CD5FB` |
| `r7-unity-playmode-3.xml`（最终 3/3） | 11489 | `DBC3EFF13DC0EEEECEE632581790D073D8586DDCF9A219BA14A50E70A84B8096` |
| `r7-unity-playmode-3.log` | 131120 | `864A00F7E3518089B5DDFAC54DB2EEFA71BD0655E1250F51E2EC7B83BA0B92DE` |
| `r7-b-positive-verify.json`（B index→A） | 6610 | `8D0CE49688E96186143B8B6937DD0D1BDDEF3F9394EBEBB87AB2408FFBB61EBA` |
| `r7-b-negative-verify.json`（B index→A） | 3656 | `F52C41848077673954EA68E2992DCA4ED54A40CCDAFF457FA199DE8B28C6736C` |
| `r5-build-shared.log` | 10470 | `F8FEB3FF081B94E0B83A4A0707AA7B8BE4D27D49C2C2E2E01EE2956E7475386E` |

首次失败顺序（全部保留，未覆盖）：无 GL 运行 → shared-plugin staging provenance 阻断 → GL 运行首 discard 按钮不可用 → 修复后 GL3 `2/2 PASS` → R1 补测 raw 基线/负例/136 与精确换位 → 最终 `3/3 PASS`。

## 6. 未覆盖 / 依赖 / 精确检查点

- 上述 R1/R2/R3 的 NOT_RUN/BLOCKED 为历史检查点，**已由 §8 的 fresh 完整包、Player `-Case all`、A 两 case native 0 与同 DLL PlayMode 3/3 取代**。
- 已接受的 D04-C 依赖已解除；未使用旧 C 包或旧 B 组件原件冒充最终包。最终包 seal/script/DLL/argv 均在 §8 有 SHA 与覆盖。
- 最终阶段未另起重复任务；所有执行均在本 B 线程/native receiver 会话完成。项目级 D04/C04/C05/C06 与 G2–G5 批准仍由 caller/reviewer 处理。
- 本报告不改变 V4 生产、D04/C04/C05/C06 与 G2–G5 未批准状态；未生成 `complete.flag`。

## 7. 调用方集成检查点（2026-11-03，最终执行前）

D04-C 已在原线程 `thread_20261103_013102_3a7e` 第 13 轮完成独立组件验收，受审头 `f042fbf10cecac6d5d0a4f4df2777bfd7e220fb4`、代码 `69b9e669a3094ba00806b333730733d9ab12ebba`。上文 R5/R9 等为当时历史状态，当前依赖已解除。C 的 errors=0 与同包复用证据属于 C 自身候选，不作为本次 A+B+C 集成包已通过。

调用方在 B 工作树以 `9243ad91` 无冲突合入已接受 C；随后预检发现 C 向 A 传 CSV，而 A 要求 JSON，且 Player 参数缺少 `-c04-package-manifest`。调用方提交 `97f9ba21b240f9a864bad00339180e3b326b28fb` 修复集成接口：先生成完整产物列表的 JSON 清单，再由既有 C CSV/封包门禁覆盖该 JSON 的 SHA；同一 JSON 路径传给 probe 与 A，保留原 C 封包、进程和失败控制，不修改 A 语义。

实际验证：runner **82/82**、compat **20/20**，均 native 0；脚本解析 0 错误。独占组件夹具调用实际 A：JSON native 0，旧 CSV native 1；检查嵌套产物闭包、CSV 绑定 JSON 哈希、实际 Player argv 传入同一 JSON。原件与命令位于 `E:/majiang-review/g2-c04-d04-player-ds-20261103/build/i9fe8ca79`。首个调用方测试脚本误用了 dot-source 后带 ValidateSet 的 Case 变量，已在新夹具目录用专用变量修正，未改生产断言，原脚本保留。

此处仅为最终执行前检查点：**集成候选 fresh Unity/Windows Player Case all、同包六行与完整 Probe 原件仍 NOT_RUN**。原 C 包与旧 B 组件原件不得冒作新包。按原合同由本 B 线程和原 receiver 会话继续，先填写新任务卡，冻结完整输入与 fresh DLL/包，再取得真实执行、原生退出、XML、五正两负 raw→A 和全包 SHA；完成前不批准完整 B/D04、不推送 MR、不合并。

## 8. 最终同包执行（2026-11-03，PASS）

### 8.1 冻结身份与任务卡

- 最终冻结 HEAD：`0dac2cefa8b943e91d50473910e81b88f0d3fd8b`；B 修复 `0dac2cef` 仅调整 `AdvanceToNextDealerTurn`：允许在“同一 stateVersion 已打开 dealer decision”上继续，不再要求必须先看到 version 增长。该修复来自 c5a1b2 首跑 authorized `G3-ADVANCE-2 timeout`；首败原件保留。
- 任务卡/预检：`build/i57a9cb72f/task-card.txt`、`preflight.log`；SDK 8.0.426、Unity 6000.3.27f1、E free 11.49GB、clean HEAD、许可可识别。
- 廉价合同：`runner-tests.log` 实际 **82/82**、native 0；API JSON 与 Player argv 同一 `c04-verifier-package-manifest.json`（runner 原件）。

### 8.2 Fresh 完整包与 Player `-Case all`

实际命令（完整 argv 在 `player-runner2.log`）：

```text
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass \
  -File tools/unity/run-p45-c04-player-cheats.ps1 -Case all \
  -EvidenceRoot E:/.../build/c5a1b3 \
  -SourceHead 0dac2cefa8b943e91d50473910e81b88f0d3fd8b \
  -BuildCommit 0dac2cefa8b943e91d50473910e81b88f0d3fd8b \
  -VerifierPath E:/.../build/i57a9cb72f/verifier-artifacts/bin/C04PlayerCheatVerifier2/debug/C04PlayerCheatVerifier2.dll \
  -DotnetPath D:/Unity/dotnet/dotnet.exe
```

- 证据根 seal：**PASS**，`sourceHead=0dac2cef`，证据 manifest SHA `be88ffb5daf8ba134b434efa3adc5f82e786c7cd6a14666872a065b6cd60916d`，**1612 files / 51,896,845 bytes**。Windows 包 CSV 覆盖 **210 files / 128,868,864 bytes**，包 manifest SHA 为下列 `13717361...`；包、证据根两种清单分别核验，不混用计数。
- BuildReport：`Succeeded`，errors **0**、stepError **0**、stepException **0**、warnings 200；unity/build/shared/process 原生 exit 已知，drain 完成，owned remaining **0**。
- Player authorized：native **0**；case PASS **29/29**；A Verifier2 native **0 / PASS**，五行动作位 `tile-mark/sleeve-bloom/sleeve-second/beam-swap/beam-swap-choice`，balance 119/109/99、3 private ledger、每态 136、SECOND/SWAP 双向精确实体与 canonical/receipt/publication/recovery 均通过。
- Player negative：native **0**；case PASS **15/15**；A Verifier2 native **0 / PASS**；两条不同 request/payload：`DECISION_MISMATCH`、`CHEAT_INSUFFICIENT_FUNDS`，wire/durable reason 一致，state/event/ledger/physical 前后 SHA 全等。
- 关键 SHA：
  - `c04-verifier-package-manifest.json` `23EC8B87E7E196DBB5053BA919FE81702CD867A3D8B8E22297F70EA6EF4BB80A`
  - `c04-package-manifest.csv` `13717361AA11AABB9795963CFD75F3C4BE42E937F7748EB5C898E15D4C24363A`
  - `c04-package-seal.json` `867D283B7E25CC61D4CA01C2C1B32A79FF1EB30F05889413C95F7EAECC18BAD2`
  - authorized case/index/A-out：`9E4D6378086B1189D60F1F2FB5E9ABF69004A3B6118157E74BEEA964E05D331E` / `EBD2D222028CA81F764178754945966D01649A96F6CD548CC7A72552AF36ACC0` / `A802D2A4D22CC6C2377E5313B66113CF793048D42051033079F20CABCD3A7DAB`
  - negative case/index/A-out：`F2513295C146A73D63A76022F414DB43D1C61AA16E0E6473824A6D786A02ACCD` / `78333D49683671F06C2C85A6647E24D25017DC738D57004A4A064FB99F017F2D` / `4BC8058BFBCABFCF047D764C4B9E81E02382011C5A4950911F67548A8AF29D7B`

### 8.3 同 DLL 复制 PlayMode

从 `build/c5a1b3/staging/repo` 只读复制到 `build/p5a1b3/repo`；`Mahjong.*.dll`/Google.Protobuf 与包 `Mahjong_Data/Managed` 逐一 SHA 相同。为通过共享插件 provenance gate，在复制根重新生成 provenance 并把 8 个 package DLL 及对应 build 输出对齐到包 SHA；`check-shared-plugins.ps1` 在复制根 **PASS**。原件 sealed 包未写。

实际命令：

```text
Unity.exe -batchmode -projectPath build/p5a1b3/repo/unity/Mahjong.Unity \
  -runTests -testPlatform PlayMode \
  -testFilter Mahjong.Unity.Tests.PlayMode.C04PlayerCheatScenarioTests \
  -testResults build/i57a9cb72f/playmode-final.xml \
  -logFile build/i57a9cb72f/playmode-final.log
```

XML：**total=3 passed=3 failed=0 inconclusive=0 skipped=0**；authorized/insufficient/unauthorized 三条均 PASS，含 136、exact two-way/134、unauthorized zero-effect。XML SHA `3AB39039C053A04AE0ABC1486DF58C282D61D90860E3ED3DF7925353DECC20F8`；log SHA `72F66A34C54E6D9CD2BCA3FD231D13334896E5219A5212287ADE5A0A81B4F48E`。PlayMode 为独立复制项目，不是 Windows Player 包内运行。

### 8.4 最终结论

六行 required 均已实际通过，B 自身范围 **PASS（组件 + 同包 Windows Player GAME）**。工作树 clean；tracked diff 仍仅原 12 路径中的 6 个代码文件 + 本报告；`git diff --check` native 0。未 push/PR/MR/merge/deploy/approve，未改 scheduler/complete.flag/锁/许可/A/C 生产源。最终统一 Draft PR20 由 caller 处理。

## 9. 调用方独立最终验收（2026-11-03）

受测代码 `0dac2cefa8b943e91d50473910e81b88f0d3fd8b` 独立验收通过。本次实际新编译 A Verifier2（0 warnings/errors），以生产 runner `-Case all -SkipBuild` 运行同一 `c5a1b3` 封存 Windows 包，在全新独占输出生成另一套真实原件：authorized **29/29**、negative **15/15**，Player 与 A 两 case 均 native 0。A 验证五行动作、119/109/99、三笔私有账本、136 实体与精确换位；两个不同拒绝的准确原因及持久零副作用通过。出牌诊断分别 2/13 条，失败均 0。

包 seal、原 `c5a1b3` evidence seal 与独立复跑 evidence seal 均 errors=[]。实际比对包和 PlayMode 复制项目八个共享 DLL 的 SHA 全等，Probe 源码在当前树、封存 staging、PlayMode 复制树三处 SHA 相同；原 PlayMode XML 3/3、零 skip 与日志哈希一致。本次是实际 Windows 同包复跑与 PlayMode 原件复核，未冒称独立重跑 Unity。原始错误包 `c5a1b2` 继续保留。首个只读 seal 探针因当前 PowerShell 执行策略未能 dot-source，失败记录保留；使用既有逐进程 `-ExecutionPolicy Bypass` 正常入口后的实际校验 native 0，没有修改机器策略。

独立证据根：`build/final-review-1793687684698/`；实际新原件在其 `actual-same-package/`，命令和只读完整性检查在 `task-card.txt`、`actual-same-package.log`、`seal-check.ps1`、`seal-review2.json`。B 任务验收只绑定上述受测候选与包；后续发布到统一 PR20 时保留其他已接受任务，批次合入后的新头不能冒称此包的受测身份。完整 D05/C04/G2–G5、远端 CI、合并与部署门禁仍单独保留。

## 10. 统一发布前集成结果（2026-11-03）

调用方将已接受 B 合入既有 PR20 的已发布候选 `61ec1353`，本地代码合并 `4c0b10378813dd93031ea4633ff4fc2d0fb012b1` 无冲突，保留已接受 D05 控制投影与 C 文档；未导入其他在途树。新集成候选 fresh LocalHost/TableModel 构建均 0 warnings/errors，真实出千通道 **5/5**、TableModel **236/236**，native 0。

额外执行的 LocalControl 为 **22/23、native 1**：`DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect` 在 `TableSession.SubmitAsync` 提交时抛出“session 尚未 Ready/Bound”；源码与测试和 PR20 既有 `61ec1353` 相同，B 没有修改。原始失败保留在独立证据根 `publication-control-tests.log`，不刷新为绿色，也不据此撤销 B 自身范围验收。该批次控制/矩阵门禁继续保留。

统一 PR 保持 OPEN/Draft。发布集成头含不同于受测 `0dac2cef` 的其他已接受代码，不能冒称已完成该新头全批次 Windows/Unity 同包验证或远端 CI；§8–9 的产品证据仍准确绑定 `0dac2cef`。本次仅将已验收 B 与必要接口修复正常推送到既有批次 PR，不合并、不部署。
