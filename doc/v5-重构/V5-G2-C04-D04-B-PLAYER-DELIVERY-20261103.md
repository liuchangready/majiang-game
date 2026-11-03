# G2-C04-D04-B 真实 Player 流程、原始采集与第二 discard 修复交付

日期：2026-11-03，Asia/Shanghai

任务：G2-C04-D04-B / 原 R1-05（真实 Player 第二 discard 与完整正负流程）

执行者：DeepSeek Harness（targetAgent=deepseek_harness，本线程唯一执行者）

工作树：`E:/majiang-review/g2-c04-d04-player-ds-20261103`

分支：`wt/v5-g2-c04-d04-player-ds-20261103`

冻结 base：`a234432e57efa790c40987d9720091d93bd270a8`（既有 Draft PR20 头）

代码提交：`60f43ad70a6ed09fefaa6a18cd4bd2bd5f77493a`
证据根：`build/6ea1d77e-57e2-4c7f-bca8-1d4ce9ca3d46/`（仓库 ignored，独占）

## 结论（组件与实际边界）

本交付关闭的是 **B 自身可执行的确定性组件/意图/真实通道/PlayMode 部分**：定位并修复了“SleeveBloom SECOND 后第二 discard 失败”的真实产品原因，更新了过时的 110/100、隐藏入口断言，完整正流程在真实 `TablePresenter → TableIntent → TableSession → LocalHost` 链上实际跑到 120→119→109→99、3 笔私有 ledger、SECOND/SWAP 精确物理换位，PlayMode `2/2 PASS`。

**整个 D04 仍不能 approve。** 同候选同包 Windows Player `-Case all`、A Verifier2 对 Player 原始件的最终验证、完整包 manifest/SHA、六行 GAME 证据均未在本轮执行；它们依赖 D04-C 的真实 `errors=0` 完整包（原线程 `thread_20261103_013102_3a7e` 仍在 R5 reworking，未见可消费的最终包）。本报告不把组件通过、PlayMode 通过或历史分 run 拼成最终 D04 PASS。

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

五行动作 id 固定为 `tile-mark`, `sleeve-bloom`, `sleeve-second`, `beam-swap`, `beam-swap-choice`；负例行固定为 `unauthorized`、`insufficient-funds`，不写 accepted publication。before 使用上一动作成功后的精确 capture，after 使用 receipt 后的 capture；不拿后台 latest 替代目标提交。Player 原始件本身本轮 **NOT_RUN**，最终仍须由冻结同包运行后交给 A Verifier2；不得把本段代码能力当成已通过原始件验证。

## 4. 实际执行与计数

### 4.1 .NET 组件

```text
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --artifacts-path build/<GUID>/localhost-artifacts
D:/Unity/dotnet/dotnet.exe <...>/Mahjong.LocalHost.Tests.dll
```

- 最终 native 0；**LocalHost Total 178 / Errors 0 / Failed 0 / Skipped 0 / Not Run 0**（24.941s）。
- `C04PlayerCheatChannelTests` 定向 native 0；**4/4**。
- 首次全量曾因 isolated artifacts 未生成 `Mahjong.TableRuntime.Tests/bin/Debug` 与 `Mahjong.LocalHost.CrashWorker/bin/Release` 出现 13 个 fixture 缺失失败；补齐这两个 helper 的默认输出后重跑得到 178/178。首次失败保留，不当作产品失败或最终通过。

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
  -testResults build/<GUID>/unity-playmode-original-gl3.xml \
  -logFile build/<GUID>/unity-playmode-original-gl3.log
```

- 最终 native 0（Unity log: `Test run completed. Exiting with code 0 (Ok)`）。
- XML：`total=2 passed=2 failed=0 inconclusive=0 skipped=0`，`AuthorizedThreeCheatsThroughPresenterWithExactPrivateLedger` 与 `UnauthorizedDecisionReceiptIsRejectedWithoutVisibleDelta` 均 PASS。
- 正流程实际：TileMark -1→119、SleeveBloom -10→109、SECOND 不重复扣费、第二 discard 使用确认候选 APPLIED、BeamSwap -10→99、SWAP 不重复扣费；`Unauthorized` 精确 `DECISION_MISMATCH`。
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

首次失败顺序（全部保留，未覆盖）：无 GL 运行 → shared-plugin staging provenance 阻断 → GL 运行首 discard 按钮不可用 → 修复后 GL3 `2/2 PASS`。

## 6. 未覆盖 / 依赖 / 精确检查点

- **NOT_RUN**：同冻结包的 Windows Player `-Case all`、Player 原始件 raw index 的 A Verifier2 校验、完整包 manifest/所有文件 SHA、六行 GAME、同包 Unity Player 退出码/XML/raw 分母。
- **BLOCKED**：D04-C 原线程 `thread_20261103_013102_3a7e` 仍 R5 reworking；进程子项 01 已独立关闭，02/03/04（输入转换、真实 source→stage→package 来源、真实 BuildReport errors=6）仍开放，无 `errors=0` 完整包。未取 C 在途文件，未用旧 runner/坏包抢做昂贵构建。
- 最终阶段不得另起重复验证任务：由 parent caller / 原 dedicated reviewer 在同一 B 线程接续，先集成已接受的 A + C 并冻结，再执行最终 PlayMode + Player Case all。若届时 C 仍未交付，应由 dedicated reviewer 以真实 `waitingForThreadId=thread_20261103_013102_3a7e` 挂起，而不是虚构依赖或把本报告当 D04 通过。
- 本报告不改变 V4 生产、D04/C04/C05/C06 与 G2–G5 未批准状态；未生成 `complete.flag`。
