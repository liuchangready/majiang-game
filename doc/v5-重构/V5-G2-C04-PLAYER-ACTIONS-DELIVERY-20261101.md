# V5 G2-C04-D03 Player 动作确认交付（2026-11-01）

## 范围与冻结身份

本交付只实现 C04-G2-01..04 四个真实 Player 动作场景。接受基线为 origin/v5 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7；本地候选基于此前唯一已接受的 C03/UI15 publication 734b3775fb7e658fcac33d3d6ce07cf195e96001；实现代码候选提交为 3be88b077081a6f6e1d35aa1ebc8078c49e46928。最终报告 SHA 作为封存交付元数据列出。D01、C05 与其他 C04 范围未读取、合入或重跑。本报告不修改 census，不宣称正式 C04/G2 通过。

V4 仍是唯一生产架构；G2–G5 未批准。P4.4、UI15、C01 与 2a3a359 投影历史仅作为已接受前置事实，本轮未重跑，也未重新打开其实现。

## 固定场景分母和执行状态

固定分母是四行，始终为 C04-G2-01..04：

| Census 行 | 固定合法初态/目标动作 | 确认要求 | .NET | PlayMode | Windows Player |
|---|---|---|---|---|---|
| C04-G2-01 | Pon；seat0 持物理牌 0、1；seat3 在 response window 打出牌 2 | Applied receipt 与同 request/round/seat 的 PonDeclared；meld 实体守恒 | BLOCKED：初始 subscribe version mismatch | NOT_RUN | NOT_RUN |
| C04-G2-02 | Chi；seat0 持物理牌 0、8；seat3 打出牌 4 | Applied receipt 与 ChiDeclared；called/hand 实体进入 meld | BLOCKED：初始 subscribe version mismatch | NOT_RUN | NOT_RUN |
| C04-G2-03 | Riichi；冻结 seat0 的 14 张候选手牌 | Applied receipt 与 RiichiDeclared；Riichi 和一根供托成立 | PASS | NOT_RUN | NOT_RUN |
| C04-G2-04 | Closed Kan；seat0 持牌 0、1、2、3；死山 replacement 实体 73 固定在 wall position 122 | Applied receipt 与 KanDeclared；replacement draw 可见；136 实体守恒 | PASS | NOT_RUN | NOT_RUN |

Pon/Chi 场景的固定 seed 分别是 c04-pon-seed-4401、c04-chi-seed-4402。被测 seat0 先以受控的合法 discard 进入真实回合；seat1 和 seat2 分别以 80、84 的合法 discard 推进；seat3 最后打出目标实体并在真实 response window 等待 seat0。所有前置 command 使用 RoundStateMachine.Execute 接受路径并在每步断言 TilePositionMap.Validate(...).AssignedCount == 136。目标 Pon/Chi 通过真实客户端通道提交，fixture 不写回执、领域事件或执行结果。

Riichi 使用已接受合法候选形状 [0,4,8,12,16,20,24,28,32,36,37,72,76,124]，固定 seed c04-riichi-seed-4403。Closed Kan 使用 [0,1,2,3,36,40,44,48,52,56,60,64,68,72]，固定 seed c04-closed-kan-seed-4404；dead-wall replacement 是实体 73。该 case 选择暗杠，故 chankan 不适用；报告不得把它写成已覆盖 chankan。

## 完整已知缺陷

### C04-D03 — MAJOR（本任务）

- 位置：census C04-G2-01..04；原 full-match runner tools/unity/run-p45-full-match.ps1 及 Player action driver。
- 原样复现：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-full-match.ps1 -Mode All -RunDir <dir> -UnityPath <unity> -DotnetPath <dotnet> -TimeoutSeconds 5400，检查参数与四个 per-action 结果：runner 只有 Mode/RunDir/UnityPath/DotnetPath/TimeoutSeconds，没有 Pon/Chi/Riichi/Kan selector。
- 实际 vs 期望：实际上随机整场命中、投影测试、领域 history 或 meld 截图都不能证明 seat0 的 Player 动作及其 Applied 回执。期望是四个固定场景分别通过真实 TableSession.SubmitAsync、LocalHost 权威、协议和客户端可见事件，并将事件精确关联到 request/scope/seat/round/version/epoch。
- 原始证据与 SHA-256：初始冻结 census doc/v5-重构/V5-G2-C04-PLAYER-SCENARIO-CENSUS-20261101.json；原 runner tools/unity/run-p45-full-match.ps1（文件 SHA 在候选证据 manifest 列出）。历史 accepted projection work doc/v5-重构/V5-P4.5-PON-CHI-RIICHI-PROJECTION-FIX-20261031.md SHA256 D8418EC8CB8549AC99EA6D9E5DB4D2C0E6457E9D67F94831C495B2CB475CCC76 不构成 Player 证明。旧 Player 失败报告 doc/v5-重构/V5-P4.5-COMBINED-PLAYER-RECOVERY-REGRESSION-20261030.md SHA256 9CEE90740AE71C2F4561FDA208F4FC5A41B51C760274A5058A5757C24AA9AC95，记录源 0d338ec79a9ca5d01897d7c1c70ec2b0e2f73156 上 Pon 缺事件、native exit 31/tool exit 10；这是历史事实，不是本轮运行。
- 修复要求：增加隔离 opt-in 的固定场景、真实生产 UI/命令意图路径、复用 ConfirmedVisibleEventObserver 的确认、固定四 case runner、负例边界和逐 case 原生证据。普通应用不得启用任意改牌入口。
- 验收断言：Applied 但缺对应 round/seat/request 的 PonDeclared/ChiDeclared/RiichiDeclared/KanDeclared 必失败；错误身份、轮次、座位或 epoch、native timeout/nonzero、缺 case、payload hash 不符或证据覆盖均拒绝。Kan 断言 closed-kan meld 和真实 replacement draw。各阶段真实执行结果见下文及独占 evidence manifest。

本轮没有扩大范围。C01、UI15、P4.4 及投影修复不是本轮缺陷；D01 恢复和 C05 Android 不属于本任务。

### C04-D03-BOOTSTRAP — BLOCKER（本轮发现，需额外授权文件）

- **位置：** `dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs:105-153` 的 `CreateInitialCheckpoint`；调用 `Capture` 和 `TableCheckpointFactory.Create` 时将已准备的 round 固定绑定到 StateVersion 0。`LocalPlayerActionScenario.BuildCallWindow` 的合法前置 command 把 Pon/Chi round 推进到 version 13。
- **原样复现：** `D:\Unity\dotnet\dotnet.exe test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~C04PlayerActionChannelTests`，E 盘隔离 TEMP/TMP/NUGET_PACKAGES/DOTNET_CLI_HOME。C04-G2-01/02 打开真实 channel 后，订阅在决定快照前失败。
- **实际 vs 期望：** 实际错误为 `INTERNAL_ERROR/Stream subscribe failed: TableProjectionException: Context state version 0 predates committed round version 13.`；期望是 LocalHost 首个投影/checkpoint 的状态版本与 opt-in 预置 round version 一致，随后真实客户端订阅响应窗口。
- **证据路径与 SHA-256：** `build/8113a3f2d6154dc08356a79cc3bf87e0/dotnet/LocalHost-final.log` SHA256 `4f032a3c71654914b7b5c973b6d57aca3687a407f0d8f97bf909663430fb77bd`；封存证据 manifest SHA256 `25b182a1f200f1f44b43ee70d2a263e4fd84a17d9eb9af6a11c5c37a2e64dc17`。MTP 原生结果：138 total / 136 passed / 2 failed / 0 skipped / exit 1。Riichi 与 Closed Kan 理论通过；Pon/Chi 为失败两例。
- **最小修复请求：** 需要额外授权修改单个文件 `dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs`：仅当显式 C04 fixture 提供已准备 round 时，使 checkpoint 与 projection context 使用该 round 的版本，并正确绑定初始 event cursor；其他普通启动行为维持现有默认。不得通过伪造 round history、伪造 receipt/event 或 private reflection 绕过。
- **验收断言：** 四种初态均能从真实 LocalHost subscribe 建立一致基线；Pon/Chi snapshot 含当前有效 seat0 response candidate；本轮四个动作通过现有真实通道测试，然后依序重跑定向 PlayMode 和 Windows Player。
## 实际通道、opt-in 和清理所有权

LocalTableChannelFactory.EnablePlayerActionScenario 只允许四个固定名称，先构造并校验真实初态，并拒绝与 FullMatch 模式组合。默认普通应用没有设置该 opt-in；没有常规生产任意改牌接口。Player probe 仅由显式 -c04-player-actions <new-case-dir> -c04-player-action-case <fixed-id> -c04-player-action-run-id <GUID> 激活。它点击正常 Menu/Lobby 控件，通过生产 AppRoot、TablePresenter command intent/owner、SessionCoordinator (ITableCommandSink) 和 TableSession.SubmitAsync 提交。只从真实 Player snapshot 取候选。ConfirmedVisibleEventObserver 同时关联 Applied receipt 和 incremental visible event。

Player 摘要记录 requestId、scope、canonical payload bytes/base64 与 SHA-256、room/session/seat/round、decision/control/binding/owner epoch、前后 stateVersion/eventSeq/cursor、Applied commandVersion、事件类型/座位/round/commandVersion/eventSeq 和事件 tiles。runner 对 payload bytes 重新算 SHA，验证 receipt identity 与 event identity；没有对应事件则失败。PlayMode 通过同一应用入口检查四种动作；runner 对 native exit、timeout、case 清单、XML 计数、hash、fresh shared plugin provenance 和唯一路径实施门禁。

runner 只接受仓库 build/<GUID-N> 新目录；它拒绝已有路径和非 GUID 路径。source 副本、TEMP/TMP、NuGet、dotnet home、Unity Library/Temp、Player、日志和证据均位于该 GUID 目录。fresh shared plugin build/provenance guard 在该副本内执行；没有清理或改写工作树 Assets/Plugins。runner 使用 Local\MahjongV5P45UnityValidation OS mutex 串行化全部 Unity/Player 阶段，记录实际等待；超时不绕过锁。摘要必须先写入，再封存 artifact hash manifest；封存后不再追加日志。

工具夹具命令：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerActions.Tests.ps1。该命令在独占 build/<GUID> fixture 目录验证一个有效记录及 12 个负例：Applied 缺事件、错误 round/seat/receipt identity/epoch、native 非零、native timeout、缺 case、缺 payload、payload hash 不符、重复 evidence root、非 GUID root。fixture 结果标记为 TOOL_FIXTURE，绝不标记为 GAME。

## 本轮命令和原生退出

| 阶段 | 实际命令 | 原生退出 / 计数 | 原始证据 |
|---|---|---|---|
| 定向 .NET LocalHost 四 case | D:\Unity\dotnet\dotnet.exe test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~C04PlayerActionChannelTests；TEMP/TMP/NUGET_PACKAGES/DOTNET_CLI_HOME 指向 E 盘 build/c04/dotnet | native exit 1；138 total / 136 passed / 2 failed / 0 skipped | build/8113a3f2d6154dc08356a79cc3bf87e0/dotnet/LocalHost-final.log SHA256 4f032a3c71654914b7b5c973b6d57aca3687a407f0d8f97bf909663430fb77bd |
| runner 工具夹具 | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerActions.Tests.ps1 | 已通过：1 positive / 12 negative；native exit 0 | build/8113a3f2d6154dc08356a79cc3bf87e0/tool-fixture-result.json |
| Unity targeted PlayMode | runner 内 -runTests -testPlatform PlayMode -testFilter Mahjong.Unity.Tests.PlayMode.C04PlayerActionScenarioTests | NOT_RUN；被上述 LocalHost bootstrap blocker 阻断 | 无 XML；不启动 Unity |
| fresh Windows Player build + 四 case | powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -RunRoot build/<GUID> | NOT_RUN；bootstrap blocker 阻断；Unity mutex 未获取 | 无 Player package/case logs；不启动 Player |

本轮未运行全量 C03、全量 PG、Android、随机整场或其他 task。`.NET` 初次 no-restore 资产缺失及中间 fixture 调整的失败次数与错误摘要记入 `build/8113a3f2d6154dc08356a79cc3bf87e0/attempt-history.json`；这些较早尝试当时没有独立原始日志文件，故没有冒称有其 raw-log hash。最终失败原始 MTP log 已单独复制并哈希。后续 PASS 不会覆盖该失败记录。

## 变更与最终候选

仅以下授权路径可进入提交：

- dotnet/src/Mahjong.LocalHost/LocalPlayerActionScenario.cs
- dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerActionChannelTests.cs
- unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs
- unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerActionProbe.cs 及其独立 GUID .meta
- unity/Mahjong.Unity/Assets/Editor/C04PlayerActionBuild.cs 及其独立 GUID .meta
- unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PlayerActionScenarioTests.cs 及其独立 GUID .meta
- tools/unity/run-p45-c04-player-actions.ps1
- tools/tests/C04PlayerActions.Tests.ps1
- 本报告

逐文件 diff、代码 SHA、候选 commit、source closure、fresh shared plugin provenance、Player package hash、PlayMode XML/hash、四行逐项状态和完整 artifact manifest 按实际已完成范围附在独占 evidence summary/manifest。生成物不入库。提交后工作树必须 tracked clean，暂存区仅含上述清单。任务范围仅为本候选版本的 D03 专项；后续 D01/C05 增量合并后必须重新绑定并验证最终候选。没有 push、PR/MR、approve、merge、deploy、scheduler 或 complete.flag 操作。
