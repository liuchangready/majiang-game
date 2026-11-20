# G2-Local-Lite 第二轮返修与交付记录

日期：2026-11-20。本文记录 bridge 第 1 轮复核 D-1-01 至 D-1-05 的批量修复和真实验证状态。范围只限当前 G2-Local-Lite Windows 候选，不代表 G2 总验收、Android、设备或上线验收。

## 源码、包与证据身份

- 基线 HEAD：`5fe836504af627da67cf46af0ca6657eafdacee5`；执行树：`E:/agent-space/thread_20261119_192821_cc27/worktree`；分支：`mesh/task/thread_20261119_192821_cc27`。未提交、未推送、未合并、未部署。
- 最终候选输出目录：`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f`；完整 Windows Player：`E:\agent-space\thread_20261119_192821_cc27\worktree\build\review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f\player`；EXE：`E:\agent-space\thread_20261119_192821_cc27\worktree\build\review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f\player\Mahjong.exe`。不要只复制 EXE。
- 最终包清单路径及文件数、逐文件 SHA-256：`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/player-package.json`；包清单本身 SHA-256：`e074bd3b326b1bff91a93fa4f3d6f2ab14b8c8a8de3946fbd48b9352b45a8065`。Player 根清单 SHA-256：`37dc4504a92ddb7decc2fe73f1725f28d93d25db3121a43dd2aff4f3a2e34133`。
- 包内 build identity 绑定基线 HEAD 与运行源码差异 SHA-256：`ac85aec4b305164b0f2a5dd2342c0d97b08ee38223781d812add4fa1e183a402`。为避免交付报告自引用，该摘要排除 `doc/G2_LOCAL_LITE_DELIVERY_20261119.md`；产品源码、测试、构建入口及任务卡仍纳入摘要。该排除规则位于 `tools/unity/build-g2-local-lite-player.ps1`。
- 共享插件由 `tools/unity/build-shared.ps1 -Configuration Release` 正式重建并由 `tools/unity/check-shared-plugins.ps1 -Configuration Release -Quiet` 核验；未手工编辑或替换 `Assets/Plugins/Mahjong` 中的 DLL/PDB。provenance：`unity/Mahjong.Unity/Library/SharedPluginProvenance/provenance.json`，SHA-256 `6c9071731508f16c904323b73e559f80ae327f91f83f5d900fc5ba68234dea4c`。
- Unity 构建、测试 XML、原生退出和包清单均在 `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f`。同包生命周期证据：`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da`；其隔离测试数据根：`E:/agent-space/g2ll-delivery-9c3e4536edbb44df92beed711d1eb0da`。测试运行证据未自动清除，保留旧轮次失败目录。

## 复核缺陷与本轮修复映射

以下逐项对应 D-1-01 至 D-1-05；后续如发现新缺陷，需说明此前不可发现的新信息。组件自动输入不计作物理鼠标输入。

### D-1-01 — BLOCKER：同包 Player 矩阵与生命周期证据缺失

- **位置：**原交付矩阵及 `build/final-20261119-173000-9f5de421/`；修复覆盖 `tools/unity/build-g2-local-lite-player.ps1`、`tools/unity/run-g2-local-lite-player-lifecycle.ps1`、本文件验收矩阵。
- **复现命令：**`powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/build-g2-local-lite-player.ps1 -RunRoot E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f -TimeoutSeconds 1800 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30`；随后 `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/run-g2-local-lite-player-lifecycle.ps1 -PlayerDirectory E:\agent-space\thread_20261119_192821_cc27\worktree\build\review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f\player -DataRoot E:/agent-space/g2ll-delivery-9c3e4536edbb44df92beed711d1eb0da -EvidenceRoot E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da -TimeoutSeconds 300 -MutexTimeoutSeconds 600 -HeartbeatSeconds 30`。
- **实际 vs 期望：**上一轮只有 Menu 启动和组件动作。本轮同候选运行了真实 Player 应用路由/Client/LocalHost/TableRuntime，普通局与 pon、chi、riichi、closed-kan、dealer-tsumo 交替 10 次；10 个 roomId、roundId、scopeId、绑定代次全部唯一，房间成功关闭，Player 原生退出 0。五个 C04 PlayMode 用例仍是自动 Client 输入。当前执行环境没有 Windows 鼠标/桌面控制工具，隐藏启动也不能代替真实操作者操作，因此手动 UI、结算页面和真实鼠标链路仍按矩阵保留 BLOCKED。
- **证据：**`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da/player-lifecycle-result.json`、`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da/player.log`、`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da/manifest.json`；Unity XML `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/editmode-results.xml`、`editmode-lite-font.xml`、`playmode-c04-actions.xml`。最终候选隐藏默认启动的菜单证据为 `build/review-r2-player-menu-f04ce2551af94cd4b2e72bcec085a209/menu-startup.json` 和 `player.log`；确认到 `MENU_PRESENTER_STARTED` 后采样 2 次 TCP PID 表为 0，隐藏窗口的 `CloseMainWindow` 返回 false，无法证明正常关闭，程序由本验证脚本清理。失败原件保留，不算通过。
- **修复要求及映射：**以固定候选包执行真实房间轮换并将 L01-L12 状态逐项记录；同包操作不以 Button.onClick 伪装鼠标。其余可自动验证部分由 L09、L10/L11 的组件与持久化矩阵补充。
- **验收断言：**自动真实会话 10/10、身份唯一及关闭通过；物理鼠标、窗口内退出与完整游戏期间网络观测的证据仍需交互桌面操作者补齐，故 D-1-01 的完整人工验收保留 BLOCKED。

### D-1-02 — BLOCKER：固定局面重复使用同一个权威 roundId

- **位置：**`unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs`；`dotnet/src/Mahjong.LocalHost/LocalPlayerActionScenario.cs`；`dotnet/src/Mahjong.LocalHost/LocalSnapshotFacts.cs` 的 `c04-` 检查点版本兼容规则。
- **复现命令：**`D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj`；最终候选另执行上述生命周期命令并检查 `lifecycle-matrix.json`。
- **实际 vs 期望：**修复前同一 fixture 重复创建会沿用固定 roundId。现在 Lite fixture 采用 `c04-<scenario>-lite-<GUID>` 唯一 roundId；普通 Lite 局采用唯一 roundId。旧 C04 创建 API/固定 ID 不变，`c04-` 前缀继续启用既有检查点版本语义。最终 Player 10/10 投影 roundId 与 roomId 唯一，scopeId 与 generation 也唯一。
- **证据：**`dotnet/src/Mahjong.LocalHost.Tests/C04PlayerActionChannelTests.cs` 新增 fixture 新身份/旧身份兼容断言；最终 Player `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da/.../lifecycle-matrix.json`；Unity PlayMode `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/playmode-c04-actions.xml` 5/5。测试层保留实体守恒和前置状态检查；没有改写投影伪造 ID。
- **修复要求及映射：**每次 Lite fixture 构建时注入唯一权威 ID，同时保留旧调用及合法前置操作。已在 factory/scenario 完成。
- **验收断言：**相同 fixture 可重复进入且 ID 全新；旧 C04 入口 ID 与 checkpoint 语义兼容；10 个同包房间身份一致且唯一。已通过。

### D-1-03 — BLOCKER：旧 outbox 跨局查询、开局中断泄漏与 room dispose 无界

- **位置：**`unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs`；`unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs`；`unity/Mahjong.Unity/Assets/Scripts/App/SessionCoordinator.cs`；`dotnet/src/Mahjong.Client/TableSession.cs`。
- **复现命令：**`D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj`；`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f` 的 Unity EditMode AppComposition 测试；最终 Player 生命周期命令。统一负例矩阵为 Client 的 unknown 回执重启查询、AppComposition 的取消/关停竞争与 close fault，以及十局实际轮换关闭。
- **实际 vs 期望：**每个 Lite room 现在绑定 `client/outbox/<roomId>`，下一局不会扫描或重发旧房间未决请求；未决文件留在旧作用域供原会话/原 scope 对账，不会把“查无”当作已结算。room gate 等待和房间 Dispose 均有限 3 秒；超时保留 room 引用并隔离，无法准备第二宿主。session 未登记的启动失败会尝试关闭实际 room；关闭失败使 composition quarantine。普通产品的既有恢复路径未改。
- **证据：**最终同包 10 个 room 各自有独立 outbox 目录、10 个本地存档及其 `.save.prev`，见 `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da/manifest.json`；`Mahjong.Client.Tests` 182/182 覆盖 unknown outcome 保留原请求并查询而非重发；Unity `AppCompositionTests` 17/17，涵盖 open-during-close、并发 shutdown、in-flight quarantine、close fault；实际轮换 10/10 且 `roomClosed=true`。`.NET` 命令输出没有生成 TRX 文件，日志中的实际非零用例数和原生退出码如实记录，不声称存在 XML。
- **统一负例矩阵：**

  | 负例 | 断言来源 | 结果 |
  |---|---|---|
  | 请求在提交前中断 | `FileOutboxStoreTests.KilledBeforeSaveLeavesNoCommittedRecord` | Client 全量 182 用例通过 |
  | 命令已应用但回执未知/进程退出 | `UnknownOutcomeAfterARealWorkerExitIsQueriedNotResent`、`UnknownOutcomeAfterRestartQueriesTheOriginalRequestInsteadOfResending` | Client 全量通过；查询原 request，不重发 |
  | resolve 前被杀且记录需保留 | `KilledBeforeResolveKeepsTheUnknownEntryForReceiptQuery` | Client 全量通过 |
  | close fault、shutdown 重入及关闭时旧回调 | `CloseFaultMustNotReportCompleted`、`RepeatedShutdownMustPreserveInflightQuarantine`、`QueuedPublishMustNotDeliverAfterShutdown` | Unity EditMode 17/17 通过 |
  | 关闭中再次打开、并发关闭 | `OpenDuringCloseMustNotReturnClosingCoordinator`、`ConcurrentCompositionShutdownMustShareTaskAndResult` | Unity EditMode 17/17 通过 |
  | Lite 跨房间 outbox 串读 | `LocalLiteGamePreparation_RotatesRoomIdWithoutOpeningAHost` 与最终 Player 10-room 轮换 | 新 room 路径分离，10/10 通过 |
  | 真实 LocalTableRoom writer Dispose 卡住/失败 | 没有对实际 room writer 注入超时的 Player 负例 | **NOT_RUN**；代码有 3 秒上限并保留 room/quarantine，但 coordinator fake 不等价于真实 writer 证据 |

- **修复要求及映射：**每 room 隔离 outbox、保留启动失败宿主引用、有界关停；已在 AppComposition/factory 完成。未决数据没有随重开删除。回执重启与竞态的既有负例沿用原 Client/Unity 断言。实际 room writer 卡死负例仍 NOT_RUN，见 L10/L11，不声称通过。
- **验收断言：**新 room 不加载旧 outbox；重复重开不重放旧动作/不二次结算；close 成功后才新建 host；close fault/timeout 显示错误且拒绝第二 host。代码与既有组件负例及实际轮换支持这些断言。真实 Player 注入 writer 卡死/失败及“旧 host 已应用但丢回执”仍未在本机物理 Player 中注入，L10/L11 标为 BLOCKED，不能按完整负例矩阵通过。

### D-1-04 — MAJOR：random/case 阶段残留、错误不可见、诊断/存档根错配

- **位置：**`unity/Mahjong.Unity/Assets/Scripts/UI/LocalLiteRunContext.cs`；`AppRouter.cs`；`GameBootstrapper.cs`；`MenuPresenter.cs`；`SettlementPresenter.cs`。
- **复现命令：**同包生命周期命令；`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/editmode-lite-font.xml` 中的 LocalLiteRunContext 写入失败与 fixture→random 测试；Player 运行日志检查 `run.json`、`current-case.json` 和 `logs/player-public.log`。
- **实际 vs 期望：**`RecordStage(null,...)` 现在明确切至 `random`；启动/桌面/结算/重开/返回由 Lite 路由记录 stage；诊断 I/O 错误作为受控可见错误处理，不会在 `_starting` 锁前抛出。Lite data root 以真实 `-smoke-data` 根为父目录，`run.json` 绑定包的 buildGuid/source identity，每次运行日志归入对应 run，公开日志只写白名单事件码与阶段。
- **证据：**`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da/.../run.json`、`current-case.json`、`logs/player-public.log`、`logs/cases.jsonl` 及哈希清单；10 个案例日志和存档均在独立 dataRoot。默认 Menu 的隐藏启动/关闭探测未能正常发出窗口关闭，见 `build/review-r2-player-start-restart-fixed-*/`；不声称已通过正常 GUI 重启。
- **修复要求及映射：**统一路由记录 case/stage、显式 random、可见失败、逐 run 诊断，并统一 smoke/Lite root。已完成。
- **验收断言：**fixture→random 后 current case 为 random，重新开始有新 run/room；诊断文件失败不锁住启动；普通公开日志不含手牌、种子或余额账本。自动案例验证通过，双次真实正常 GUI 关闭/重启仍 BLOCKED。

### D-1-05 — MAJOR：结算按钮文字重叠及测试局面字体失败留错标签

- **位置：**`unity/Mahjong.Unity/Assets/Scripts/Spectator/SettlementPresenter.cs`；`unity/Mahjong.Unity/Assets/Scripts/UI/MenuPresenter.cs`；`unity/Mahjong.Unity/Assets/Scripts/UI/FontFallbackResolver.cs`；`unity/Mahjong.Unity/Assets/Tests/EditMode/LocalLiteFontFallbackTests.cs`。
- **复现命令：**最终 Player build runner 自动执行 `LocalLiteFontFallbackTests`；XML：`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/editmode-lite-font.xml`。
- **实际 vs 期望：**重新开始按钮从未改造的模板克隆；每个结算动作仅保留一个文字节点。系统字体缺失时，Lite 菜单与结算按钮使用可读 ASCII fallback，不保留“开始一局”等错误 TMP 原文。故障注入测试检查真实按钮层级的可见、启用标签数和文本，不只测单一字体 glyph。
- **证据：**`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/editmode-lite-font.xml` 3/3 通过，包含字体解析、菜单/结算真实层级、I/O 故障路径。
- **修复要求及映射：**从干净模板克隆并清除重复标签；字体不可用时替换正确标签。已完成。
- **验收断言：**三个菜单动作及两个结算动作各显示一个准确可读标签。自动场景层级检查通过；人工“测试局面/再开一局/返回开始”鼠标导航仍受物理输入 BLOCKED 影响。

## 验证命令与结果

所有 Unity 长阶段使用 `Local\MahjongV5P45UnityValidation` 共享锁，最大等待 600 秒，stage 有界超时、30 秒心跳，GUID 独占目录；构建与 Player 验证完成后进程已退出。首次失败原件保留：`build/review-r2-player-b991e09b869642f5a1d1fa9951c208c1/`、`build/review-r2-player-74ee44ab13e14d2885219ce659e741c7/`、`build/review-r2-player-a8afc88ac3e241fb981d594284e35c23/`、`build/review-r2-player-81c74ba53a794899b0a4f434012cfd9d/`、`build/review-r2-player-2ad23cc7df3145a1911f8065b0e618c0/`、`build/review-r2-player-6d41e118a1a94efdb729f8bdd6b3851f/`、`build/review-r2-lifecycle-c1a8bfbd7b5a4540a50be41b7d950cd9/`、`build/review-r2-lifecycle-d3d7dbe5ec2c4dc58272cd66ae250f96/`、`build/review-r2-dotnet-b071958ae07043bbbd64e617fadac0db/`、`build/review-r2-dotnet-42fd8c7c8da44aa4b1e9817014950222/`、`build/review-r2-dotnet-corrected-eecbf2c108e74e319eab7be88f343202/`、`build/review-r2-player-start-restart-1f32e85ed83d49148ac33a4458823738/`、`build/review-r2-player-start-restart-fixed-fe94334184234a22bdc7ec85908e43c2/`、`build/review-r2-player-menu-f04ce2551af94cd4b2e72bcec085a209/`。只在输入/修复变化后重试，没有覆盖旧证据。

| 阶段 | 实际命令/测试 | 结果 |
|---|---|---|
| 生产共享插件 | Player build runner 内调用 `tools/unity/build-shared.ps1 -Configuration Release`，再调用 `tools/unity/check-shared-plugins.ps1 -Configuration Release -Quiet` | 两者原生 exit 0；provenance 与 DLL/PDB 来源在最终 package manifest 中可核 |
| Unity EditMode | build runner 的 `AppCompositionTests` | XML 17/17 passed，0 failed，exit 0；`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/editmode-results.xml` |
| Lite UI EditMode | build runner 的 `LocalLiteFontFallbackTests` | XML 3/3 passed，0 failed，exit 0；`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/editmode-lite-font.xml` |
| Unity PlayMode | build runner 的 `C04PlayerActionScenarioTests` | XML 5/5 passed，0 failed，exit 0；自动 Client/LocalHost 动作，不是鼠标证据；`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/playmode-c04-actions.xml` |
| .NET LocalHost | `D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --logger "trx;LogFileName=localhost.trx" --results-directory E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-direct-608e39bd3e7242b4b4955b12f601be86/localhost-results` | 206 passed / 0 failed / 0 skipped，exit 0；`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-direct-608e39bd3e7242b4b4955b12f601be86/localhost.log`。命令未生成 TRX，故不引用 TRX |
| .NET Client | `D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj --logger "trx;LogFileName=client.trx" --results-directory E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-client-766d59fae197483aad2b497720cbe190/results` | 182 passed / 0 failed / 0 skipped，exit 0；`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-client-766d59fae197483aad2b497720cbe190/client.log`。命令未生成 TRX，故不引用 TRX |
| .NET TableRuntime | `D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --logger "trx;LogFileName=table-runtime.trx" --results-directory E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-direct-608e39bd3e7242b4b4955b12f601be86/table-runtime-results` | 498 passed / 0 failed / 0 skipped，exit 0；`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-direct-608e39bd3e7242b4b4955b12f601be86/table-runtime.log`。命令未生成 TRX，故不引用 TRX |
| 同包真实 host 生命周期 | `tools/unity/run-g2-local-lite-player-lifecycle.ps1`，最终 Player | 10/10 cases、10 room/round/scope/generation，`roomClosed=true`，Player exit 0；采样 18 次 TCP PID 表、连接 0。无 UDP/系统全局抓包 |
| 同包默认 Menu 与真实鼠标 | Windows Player | 当前 agent 工具没有桌面/鼠标输入接口；隐藏窗口能到 Menu，但 `CloseMainWindow` 未退出。鼠标及正常 GUI 关闭重启 NOT_RUN/BLOCKED；不能用 Button.onClick 替代 |

主要证据原件 SHA-256：

| 原件 | SHA-256 |
|---|---|
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/manifest.json` | `37dc4504a92ddb7decc2fe73f1725f28d93d25db3121a43dd2aff4f3a2e34133` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/player-package.json` | `e074bd3b326b1bff91a93fa4f3d6f2ab14b8c8a8de3946fbd48b9352b45a8065` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/editmode-results.xml` (17/17) | `5a01d3dea19b0a93b094b5268207d0e8112c4ff2f674f5799e86b61afed3c02b` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/editmode-lite-font.xml` (3/3) | `81deba91f5b46bcec8e76ec0ce36c4867efcce03cba8e4a4cf77c150c93fe538` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/playmode-c04-actions.xml` (5/5) | `5caf702a888d8efd676bf42778178801f988b34068c8918cbea184df04f16313` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/logs/runner.log` | `b4777d30946a1a64f499167a095080b5b56634d95b61b474693549e40d2507a3` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f/logs/player-build.unity.log` | `e750235e854212e6dc2a3e28465c103df860d3809e01a412d812ee762cf4f18c` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da/manifest.json` | `28b880b524f182fb9f845aa7fbd8268be16bd5595f1425ee8ac8ae0aac5ebad7` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da/player-lifecycle-result.json` | `c382093b34a26a9783bf6c8cc763036e71f98223a77d77a4f6690bdcf1a0a7ca` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da/player.log` | `80cfd254471e963e6502e92f1b93bd740a8ec4f87ae9e9f5bcdcaf707f6d6846` |
| final candidate Menu `menu-startup.json` | `8413215abdb3445394cd1bf4f5dce120ee31b8bb3203cd3d19b943c1ee1aa518` |
| final candidate Menu evidence manifest.json | 99698edf2d005f455e22e8f1d244bbd76182e259d6cce95e4e300d2b62ff09df |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-direct-608e39bd3e7242b4b4955b12f601be86/localhost.log` (206/206) | `6a61cc27e6c90779008bad57c8301fc73b9f8c2ecb01aeb3cc03c0d2324512f8` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-direct-608e39bd3e7242b4b4955b12f601be86/table-runtime.log` (498/498) | `cc2dc114ded59b511a14892506247610bdc8c785139697c672a5397435d327a5` |
| `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-client-766d59fae197483aad2b497720cbe190/client.log` (182/182) | `1b6bea3f277ab3cc54e9cff01bd5060eb2f68334b2f6fda87cb9814c51cdad42` |

## 固定验收矩阵 L01-L12

| 项目 | 状态 | 同包证据与剩余边界 |
|---|---|---|
| L01 Lite Menu，普通开始直达真人+3 Basic AI 牌桌 | BLOCKED | Lite 构建入口和 LocalAiProfiles.Basic 已在代码/Player host 配置；生命周期自动启动真实 host。但最终候选缺物理菜单点击到牌桌证据 |
| L02 pon 人工点击、来源实体/副露/后态 | BLOCKED | C04 自动组件用例通过；无物理鼠标输入 |
| L03 chi 合法组合人工选择及后续动作 | BLOCKED | C04 自动组合断言通过；无物理鼠标多组合选择证据 |
| L04 riichi 人工弃牌、真实立直/供托 | BLOCKED | C04 与规则用例自动断言；无 Player 鼠标证据 |
| L05 closed-kan、岭上补牌与守恒 | BLOCKED | C04 自动断言；无 Player 鼠标证据 |
| L06 dealer-tsumo 役种、支付与结算呈现 | BLOCKED | 权威组件覆盖自摸；最终 Player 结算视图无鼠标运行证据 |
| L07 荒牌真实结算呈现 | PASS | `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-dotnet-direct-608e39bd3e7242b4b4955b12f601be86/table-runtime.log` 中 498/498 通过，包含 `ExhaustiveDrawIsCalculatedWithFourSeatsAndKeepsScoreConservation`，由确定性规则输入执行权威流局结算并验证四家守恒。该项证明规则/算分结算，不声称 Player 结算视图鼠标已验收 |
| L08 荣和与非法/过期零变更 | BLOCKED | Client/TableRuntime 自动组件覆盖合法与拒绝边界；无同包 UI 鼠标链路 |
| L09 普通局+五案例同 Player 交替重开至少 10 次 | PASS | 最终 Player 10/10 轮换、房间与 round 身份唯一，保存文件隔离，host 正常关闭，Player exit 0；证据 `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r2-lifecycle-delivery-9c3e4536edbb44df92beed711d1eb0da` |
| L10 连点、返回/启动竞争、过期输入、关闭失败可见 | BLOCKED | AppComposition 17/17 包含并发关闭、in-flight quarantine、close fault 组件负例；缺最终 Player 对真实点击竞争和 LocalTableRoom writer 卡死注入 |
| L11 测试数据隔离、旧 outbox 未决对账、隐私 | BLOCKED | 最终同包 10 个 room 独立 outbox/save；Client 182 项含 unknown 重启查原请求；公开日志仅 `LOCAL_LITE_LIFECYCLE_PASS`。缺同 Player 注入旧未决 writer/结算与全流程隐私实测 |
| L12 无连接、鼠标输入、正常退出再启动 | BLOCKED | lifecycle 进程 TCP 表 18 次均 0，范围不含 UDP/OS 全局；鼠标不可用；最终包 Menu smoke 到达 `MENU_PRESENTER_STARTED`，2 次 TCP 表采样无连接；隐藏启动 CloseMainWindow 返回 false，验证后清理该自有进程，未改机器网络配置 |

未覆盖的人工 UI 分支如大明杠、加杠、荣和、过、多个鸣牌组合选择、流局结算视图和完整窗口重开，不因五个 C04 组件用例通过而宣称穷尽。未运行物理鼠标的原因是当前工具无交互桌面控制，最短续跑点：在 Windows 交互桌面启动 `E:\agent-space\thread_20261119_192821_cc27\worktree\build\review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f\player\Mahjong.exe`，按菜单完成 L01-L08、L12；对 L10/L11 执行独立失败注入或使用提供的组件入口验证后，产生日志、退出码和哈希并更新此矩阵。

## 最短启动、构建与回归

1. 将 `E:\agent-space\thread_20261119_192821_cc27\worktree\build\review-r2-player-delivery-cd3a5111b6c34444abe17b7ca7cca80f\player` 整个目录复制到受支持 Windows 机器并启动 `Mahjong.exe`。测试数据写入独立 `G2LocalLite/runs/<runId>`；正式单机数据不被清除。
2. 在此仓库根执行：

   ```powershell
   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/build-g2-local-lite-player.ps1 -RunRoot build/<new-guid> -TimeoutSeconds 1800 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30
   ```

3. 同包生命周期：

   ```powershell
   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/run-g2-local-lite-player-lifecycle.ps1 -PlayerDirectory <player-dir> -DataRoot <short-isolated-data-root> -EvidenceRoot build/<new-guid> -TimeoutSeconds 300 -MutexTimeoutSeconds 600 -HeartbeatSeconds 30
   ```

## 交付边界

本轮只交付 G2-Local-Lite 候选。没有删除既有模块/黄金样本/负例，没有修改规则合同、服务、数据库或远端代码；没有把组件测试、隐藏启动或 10 局自动路由循环称为物理鼠标/G2/设备/上线验收。所有已知复核项在本轮一次列出；新增缺陷仅在取得新证据时追加。

## 第 3 轮批量返修及最终候选（2026-11-20）

本节覆盖第 2 轮 D-1-01、D-1-03、D-1-04、D-2-01、D-2-02。D-1-02 的唯一 round 身份和 D-1-05 的按钮/字体树已由独立复核通过，本轮未改其行为。下文逐项列出复现输入、实际/期望、证据、修复映射和验收；未通过项保留 BLOCKED，不以旧产物或组件证据冒充完整验收。

### 最终冻结包和源码身份

- 最终完整 Windows Player：`E:\agent-space\thread_20261119_192821_cc27\worktree\build\review-r3-player-candidate-4-754a0b7f44e04c2e8a5e1703146a4f12\player`；启动 `Mahjong.exe`。共 193 个文件，包内每个文件 SHA-256 均与 `player-package.json` 一致（193/193）。
- 包清单：`build/review-r3-player-candidate-4-754a0b7f44e04c2e8a5e1703146a4f12/player-package.json`，SHA-256 `13c14ff076853eeb8d86376b8b0c9e2f0460f518abfa5bcdaa909ce32d18aaa8`；runner 总 manifest：同目录 `manifest.json`，SHA-256 `e1d68a665f491fd5481624b45475f88aafee8f562caa392cd87e77630293fdca`，收录 217 个文件。
- EXE SHA-256：`f083b0e879a9298cdcf9933a8f3b4f2b013e13f7d3e51221fc0d756b79e28807`。源码基线 HEAD `5fe836504af627da67cf46af0ca6657eafdacee5`；候选工作树差异 SHA-256 `6812cd19f7247ddf3d25d629052cb6f2dc484e99fc14682b1dbbf1b9c4611984`，记录于 `source-identity.txt` 与 `player-package.json`。共享插件 provenance SHA-256 `dec5f31f93031ed15bcd5104835a8708a42b6cae155bdecde8a21471fee17e39`。
- 官方共享 DLL/PDB 仅由 `tools/unity/build-shared.ps1 -Configuration Release` 生成并经 `tools/unity/check-shared-plugins.ps1 -Configuration Release -Quiet` 核验，二者 exit 0；未手工编辑或替换二进制。
- 最终 Unity 证据：`editmode-results.xml` 20/20、0 failed；`editmode-lite-font.xml` 3/3、0 failed；`playmode-c04-actions.xml` 5/5、0 failed；原生 build runner exit 0。SHA-256 依次为 `79a826702f1964d7d370aae4390d486df9fb5c38ef3d68e76f02bc3364d37202`、`7a4b81a7861d3efee2e1115e9ca781522c2e2bb08ad48e3017ddd8091c22557a`、`a93a610f8cc4fcec627c87c9a68b2ed2d67b68e0ee9789637424666c3f829132`。路径均在最终候选目录。
- .NET 全量命令：`D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --logger "console;verbosity=normal"`、Client 与 TableRuntime 同样命令替换 csproj。LocalHost 206/206、Client 182/182、TableRuntime 498/498，全部 0 failed/0 skipped/native exit 0；日志位于 `build/review-r3-dotnet-3f0a45dd3d4a4ae294eb192b6f6f2e09/`，没有生成 TRX，故不引用 TRX。SHA-256：LocalHost `33d1acdfb361ad66a01d556c9fdc8aefc7efb3a3d771582ad89ec4e34cd9efcb`，Client `ac186a5a85929af5e88eb4741c5c3abc93d04b7511677a6a2f8c831768a62073`，TableRuntime `8ff44a9b2f86e7a2c0266945ed0605f608a29faf8a9302f5df534f1fc1e31dbd`。

### 缺陷逐项修复映射

#### D-1-03 — BLOCKER：旧 scope 未决回执与正常关闭预算

- **位置：**`unity/Mahjong.Unity/Assets/Scripts/App/SessionCoordinator.cs`、`AppComposition.cs`、`AppRoot.cs`、`TablePresenter.cs`。
- **复现命令：**最终 build runner 命令见本节“验证命令”；其中 AppComposition XML 新增 `ShutdownReconcilesOldScopeBeforeClosingAndKeepsUnknownAuthorityOpen` 与 `UnknownOldReceiptMustQuarantineWithoutClosingItsAuthority`。在真实 Player 中可用 lifecycle 命令复现正常关闭；实际丢回执/LocalTableRoom writer 卡住注入仍缺。
- **实际 vs 期望：**关闭现在先在原 queue/client/binding 存活期间 capture 未决项并调用 `ReconcilePendingAsync`，记录原 scope/request；查清后才停 pump 和 close。已解决项可正常关闭；仍 Unknown 时不把“查无”当作未应用，返回 `CloseFault`，保持旧 run token/authority 和 outbox，AppComposition 隔离新开局，Table 明确显示“旧局未能安全关闭；新局已阻止启动”。`AppRoot` 退出最多等 `ShutdownWaitBudgetMs`（默认约 14.1 秒，含 receipt query、pump、channel 和最多 3 秒 room dispose），超限/失败写稳定日志。期望同 scope 有界收口、未决留存且不允许第二 host，已做安全关闭逻辑；但当前自动负例使用 FakeSession/OperationQueue，尚未在真实 Lite composition 注入 LocalHost 已应用丢回执、未应用、writer 卡住/失败。
- **证据路径：**最终 `editmode-results.xml` 20/20，含上述两个 shutdown 对账测试；`build/review-r3-lifecycle-normal-87d23d7a/player-lifecycle-result.json` 10/10、roomClosed=true；生命周期 manifest SHA-256 `f1a00f4a5f6b01a18f9dff4d85bb151ed34d7c7da8c579089172391382955d9a`，结果 JSON SHA-256 `6aa2e4a49db8af38b02becee7140c6bb74d7ca4312c4a67be6cb1167c36ad052`。本轮该项整体仍 **BLOCKED**，因为真实 Lite composition 的丢回执和 writer 故障注入未执行；不会把 Client 单测或空 outbox 算作该验收通过。
- **修复映射及验收断言：**同旧 queue 捕获原 request scope -> 原 channel 发 ReceiptQuery -> Applied/NotApplied 收口后关闭；Unknown/查询超时不关 writer、不清 outbox、不准第二 host；退出等待与 UI 失败信息沿同一关闭预算。自动测试分别断言 query-before-close 和 Unknown 时没有 close、原 request 留存、RunToken 未取消；真实旧房间及 writer 故障继续作为该缺陷的未闭合边界。

#### D-1-04 — MAJOR：启动期测试数据根 I/O 故障

- **位置：**`unity/Mahjong.Unity/Assets/Scripts/App/GameBootstrapper.cs`、`unity/Mahjong.Unity/Assets/Scripts/UI/LocalLiteRunContext.cs`。
- **复现命令：**在独占数据根创建普通文件 `blocked-root`，以同包执行 `Mahjong.exe -smoke-data <blocked-root> -logFile <evidence\player.log>`。本轮使用的同包故障原件为 `build/review-r3-boot-io-fault-7b02d7af/`。
- **实际 vs 期望：**`Directory.CreateDirectory`、build identity 读取与首次 run record 现在被启动阶段捕获；失败记录/输出稳定标识 `LOCAL_LITE_STARTUP_STORAGE_UNAVAILABLE`，不初始化 AppRoot、不进入 Menu、不创建 host。Player 的 Boot 场景显示可退出错误面板，文本说明测试数据目录不可用且牌局未启动。观测 marker=true、menuStarted=false；隐藏窗口 `CloseMainWindow=false`，验证 runner 只清理了自有 PID，native exit -1，不声称正常 GUI 退出证据。
- **证据路径：**`observed.json` SHA-256 `ded6d1dbd74fe988e987cdc95e6cc6316ff874e2c740e6073ea9a33fae70406a`；同包 `player.log` SHA-256 `6be89f4fe17b6bba3b5469f66e681f7fe596c28b0bf83856a0fafddefde0b8d4`。Stage 写入失败由 Lite UI EditMode 3/3 覆盖，包含已有 context 后的 I/O 失败负例。
- **修复映射及验收断言：**把最早的目录、身份与 run 初始化纳入受控启动路径；故障可见、菜单和 host 均不启动；stage 记录失败维持稳定错误，不允许漏记身份后开局。本次同包自动路径通过；没有交互桌面截图，显示文字由实际 Player OnGUI 代码与编译包确认，视觉鼠标检查仍归 D-1-01。

#### D-2-01 — MAJOR：共享房间所有权与失败启动清理

- **位置：**`unity/Mahjong.Unity/Assets/Scripts/App/AppComposition.cs`，真实负例 `Assets/Tests/EditMode/AppCompositionTests.cs`。
- **复现命令：**`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/build-g2-local-lite-player.ps1 -RunRoot build/review-r3-player-candidate-4-754a0b7f44e04c2e8a5e1703146a4f12 -TimeoutSeconds 1800 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30`。XML 用例 `SharedRoomPrimarySeatConflictAndReturnKeepExistingViewerAlive`。
- **实际 vs 期望：**普通配置真实 AppComposition/LocalTableRoom 先以 Player seat 0 建立 viewer，然后重复 primary seat 0 被 LocalTableException 拒绝；viewer 的 Ready、private projection、CanSubmit 保持，冲突后仍收到 Changed projection。无 primary 的非退出 shutdown 保留 viewer；APP_QUIT 最后关闭 viewer。失败启动清理仅在无其他注册 viewer 时关闭缓存 room；Lite 部分打开失败仍只清理自己的 room。
- **证据路径：**最终 `editmode-results.xml` 20/20，0 failed；其中 seat 冲突、后续投影、非退出保留、最终退出断言同一个用例；Player 生命周期正常关闭 `roomClosed=true`。
- **修复映射及验收断言：**清理前检查 Composition 已注册的 viewer 所有权；seat 冲突不关共享 host，非退出不关存活 viewer，退出逐一关闭。本项通过自动真实 LocalTableRoom 测试；不声称物理鼠标证据。

#### D-2-02 — MAJOR：Player lifecycle runner 的 timeout/cancel/短锁收尾

- **位置：**`tools/unity/run-g2-local-lite-player-lifecycle.ps1`。
- **复现命令：**使用最终 Player 分别执行同包生命周期脚本，参数 `-TimeoutSeconds 1 -MutexTimeoutSeconds 600 -HeartbeatSeconds 30`（超时）；以 `-MutexCancelFile <file>` 启动，等待 runner.log 出现 `PLAYER_STARTED` 后创建 cancel 文件（取消）；短锁测试让另一个自有 PowerShell PID 持有 `Local\MahjongV5P45UnityValidation`，运行 `-MutexTimeoutSeconds 1 -HeartbeatSeconds 30`。
- **实际 vs 期望：**超时和取消先请求 `CloseMainWindow`，隐藏窗返回 false 后只 kill runner 自己启动的 PID，WaitForExit 确认退出后才封存 manifest 并释放锁；负例以 exit 1 明确失败，没有假 PASS。短锁等待按剩余截止时间以最多 1 秒块轮询，30 秒 heartbeat 不再覆盖 1 秒锁期限。
- **证据路径：**timeout `build/review-r3-runner-timeout-95ddac2c/runner.log`，manifest SHA-256 `d216e3f0dc7cc7508ed5a42c14de95244d72ba814013258b179e6468330da6c6`；cancel `build/review-r3-runner-cancel-4ae22dd8/runner.log`，manifest SHA-256 `35327246cc54f07d41084a4fde4eec352e71c6f1d61c631d20016a35c8d80ae7`；短锁 `build/review-r3-runner-short-lock-86bdf50d/runner.log`，manifest SHA-256 `ab6ff94b6c425961b78f8dc627bdd5fc7d6d2b83fbe9b896a987948d0cc45022`。timeout/cancel 均 Player exit 1，CloseMainWindow=false，fallback kill 日志存在，自有 PID 已不存在；短锁 runner exit 1、超时已记录、Player 未启动、manifest 封存。
- **修复映射及验收断言：**取消/超时/等待锁共用有界清理与证据路径；在 writer 已停前不写 PASS manifest。三种负例均按预期非零退出且不残留自有 Player。本项通过。

#### D-1-01 — BLOCKER：固定 L01-L12 同包交互和生命周期矩阵

- **位置：**本交付文档 L01-L12；最终 Player 和上述 lifecycle/负例原件。
- **复现命令/动作：**构建最终 Player 用本节命令；自动会话用 `tools/unity/run-g2-local-lite-player-lifecycle.ps1`。物理输入必须在 Windows 交互桌面启动完整 Player 目录后使用左键，不得用 Button.onClick。
- **实际 vs 期望：**同包自动普通/五案例交替 10 次、完整 host 关闭通过；16 次 TCP PID 表采样未见 TCP 连接（只覆盖该 Player PID 和自动生命周期时段，不含 UDP/OS 全局）。启动 Menu 的自动日志有 `MENU_PRESENTER_STARTED`。C04 PlayMode 是自动 Client/LocalHost 输入，不是鼠标；结算 UI、真实鼠标、整段物理运行的网络观测、正常窗口退出再启动仍未实测。
- **证据路径：**最终包见上方；生命周期 `build/review-r3-lifecycle-normal-87d23d7a/`。数据根 `E:/agent-space/g2ll-r3-data-87d23d7a/G2LocalLite/runs/20261120T015552458Z-ee61f426870c46de920de5d5d5cb65e5/` 保留 10 个 room 的独立 outbox/save/run evidence。`run.json` 和 cases 不含手牌、种子或账本余额。该数据根的 outbox 均为空锁文件，没有旧请求，不能证明成功/未应用/丢回执处理。
- **修复映射及验收断言：**物理鼠标无法由当前执行接口提供；不伪造 PASS。下表分别给出每项当前结论和最短续跑操作。此缺口是交互桌面/操作者输入边界，不再通过继续改代码解决。

### 统一负例矩阵

| 输入 | 证据 | 结果/断言 |
|---|---|---|
| 旧 scope 请求查询后已解决 | AppComposition EditMode `ShutdownReconcilesOldScopeBeforeClosingAndKeepsUnknownAuthorityOpen` | 原 request/scope query 次序先于 close；shutdown 才能完成 |
| 旧 scope 查询 Unknown | AppComposition EditMode `UnknownOldReceiptMustQuarantineWithoutClosingItsAuthority` | 未盲目重放/结案；没有 close，RunToken 未取消，旧 request 留存并隔离新 host |
| 真实 Lite 已应用丢回执 / 未应用、room writer 慢/失败 | 尚未有真实 composition 注入 | BLOCKED；FakeSession 不代替真实 LocalHost 故障注入 |
| 真实共享 room seat 0 冲突；非退出与最终退出 | `SharedRoomPrimarySeatConflictAndReturnKeepExistingViewerAlive` | viewer Ready、可提交、private projection 仍到达；非退出保留，APP_QUIT 关闭 |
| 启动数据根是普通文件 | `build/review-r3-boot-io-fault-7b02d7af/` | 稳定故障 marker、无 Menu；仅清理自有 Player |
| Player 运行超时 1 秒 | `build/review-r3-runner-timeout-95ddac2c/` | 先请求正常关闭，拒绝后 fallback kill；exit 1，自有 PID 已停，manifest 已封存 |
| Player 运行中取消 | `build/review-r3-runner-cancel-4ae22dd8/` | 同上；exit 1，自有 PID 已停，无假 PASS |
| mutex 由本轮父进程持有，runner 等 1 秒、heartbeat 30 秒 | `build/review-r3-runner-short-lock-86bdf50d/` | runner exit 1，SHARED_MUTEX_TIMEOUT，未启动 Player，内部短截止未等满 heartbeat |

### 最终固定矩阵 L01-L12

| 项目 | 状态 | 证据/缺口 |
|---|---|---|
| L01 简化开始界面；普通开始真人+3 Basic AI | BLOCKED | 同包自动路径到 Menu/启动 LocalHost 有证据；缺真实鼠标点击“开始一局”及完整 UI 桌面记录 |
| L02 pon 人工操作/来源实体/副露/后态 | BLOCKED | C04 自动 Client 用例；缺物理点击 |
| L03 chi 组合选择及后续合法动作 | BLOCKED | C04 自动 Client 用例；缺物理多组合选择 |
| L04 riichi 弃牌与供托 | BLOCKED | 规则/自动动作测试；缺物理输入同包结算记录 |
| L05 closed-kan/岭上补牌/守恒 | BLOCKED | C04 自动 Client 用例；缺物理输入 |
| L06 dealer-tsumo 役种/支付/结算视图 | BLOCKED | 权威自动测试；缺同包鼠标与 Settlement 场景证据 |
| L07 荒牌真实规则结算与四家守恒 | PASS（权威组件范围） | 本轮 TableRuntime 498/498，包含确定性荒牌结算与四家守恒；不声称 Player 结算视图已验 |
| L08 荣和及非法/过期零变更 | BLOCKED | Client/TableRuntime 组件覆盖，结合物理鼠标链路的要求仍缺输入证据 |
| L09 普通局+五案例交替至少 10 次且无串局 | BLOCKED（自动轮换子项通过） | 同包 10/10、房间/局/scope/generation 唯一、roomClosed=true；无玩家动作，所以没有重复费用/重复结算输入可审计 |
| L10 连点/竞争/过期输入/真实 writer 关闭失败可见 | BLOCKED | 生命周期 runner timeout/cancel、Composition close-fault 组件通过；缺真实 LocalTableRoom writer 故障和鼠标连点 |
| L11 旧 outbox 对账/持久化/隐私隔离 | BLOCKED | 10 个 room 数据隔离、公开日志无私有账本；未决 query 的自动负例在 FakeSession，真实 Lite composition outbox 本轮没有未决请求 |
| L12 本地运行联网范围、真实鼠标、窗口退出重启 | BLOCKED | 自动完整生命周期 Player TCP 表采样 16 次为 0；不含 UDP/系统全局。物理鼠标、正常 GUI 关闭后重启仍未测试 |

**人工续跑最短清单：**在可操作 Windows 交互桌面，将整个 `E:\agent-space\thread_20261119_192821_cc27\worktree\build\review-r3-player-candidate-4-754a0b7f44e04c2e8a5e1703146a4f12\player` 作为原件复制或直接运行 `Mahjong.exe`。用鼠标完成 L01 的“开始一局”和“测试局面”；逐个从测试局面列表选 pon、chi、riichi、closed-kan、dealer-tsumo，保存牌桌/结算截图和对应 `G2LocalLite/runs/<runId>` 日志；按返回、再开、退出后正常关窗并再次启动。需在完整玩家操作时段采集 OS 级网络观察，单靠 16 次 Player PID TCP 表不能推断 UDP 或系统范围。不要刷新/覆盖候选包或既有证据。

### 第 3 轮验证命令和退出

- 候选构建：`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/build-g2-local-lite-player.ps1 -RunRoot build/review-r3-player-candidate-4-754a0b7f44e04c2e8a5e1703146a4f12 -TimeoutSeconds 1800 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30`，Unity runner exit 0；共享锁 `Local\MahjongV5P45UnityValidation`，心跳 30 秒，独占 GUID 输出。
- 同包轮换：`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/run-g2-local-lite-player-lifecycle.ps1 -PlayerDirectory <final-player> -DataRoot E:/agent-space/g2ll-r3-data-87d23d7a -EvidenceRoot build/review-r3-lifecycle-normal-87d23d7a -TimeoutSeconds 300 -MutexTimeoutSeconds 600 -HeartbeatSeconds 30`，10/10、native exit 0、roomClosed=true。
- 负例：timeout runner exit 1；cancel runner exit 1；short mutex runner exit 1；startup I/O marker verified, menuStarted=false, harness-cleared own PID (native -1). 原生失败是负例预期，不能计为正常产品通过。
- 首败保留：前三次候选输出目录 `build/review-r3-player-candidate-b93f72f4a61b45c6b772fc4d095fd1df/`、`build/review-r3-player-candidate-2-37a7c2cf32d943e0b2c5ea69ab238f32/`、`build/review-r3-player-candidate-3-96e35b915dcb4ed8b425f557428bf53f/` 均保留。分别为缺少 EditMode asmdef LocalHost 引用、测试属性误用、等待 viewer 首快照的测试竞态；逐一修复原因后改用新 GUID 重试。
- 本轮无提交/推送/合并/部署。验证结束时自有 Unity/Player 已停止；工作树状态以 `git status --short --branch` 实际输出为准。没有改动 g2_base、main 或其他工作树。

## 第 4 轮复核收敛（2026-11-20）

本轮只处理独立复核仍未闭合的 D-1-03、D-1-01。第 1/2 轮其余缺陷维持先前复核状态，不重开。以下自动证据绑定同一候选：源码 HEAD `5fe836504af627da67cf46af0ca6657eafdacee5`，worktree diff SHA-256 `b29c4c2f1da652da62070ce02e3826050dc1833ec3f0fe00e3c1e8f6a7659592`；Player 完整目录 `E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r4-player-9-5c6c837f438e4276a5208c31f55c9e51/player/`。`player-package.json` 列出的 193 个文件逐项重算 SHA-256，193/193 匹配；exe SHA-256 `f083b0e879a9298cdcf9933a8f3b4f2b013e13f7d3e51221fc0d756b79e28807`；build identity `680f08c89646a137a7d2cc8186f495c95d0ff860c87af2a28222450576989271`。包清单 `manifest.json` SHA-256 `5c754fdb41dc6c4f2278f106771774c196dc9584b55f80f83d37419823001e19`；包清单原件为 `player-package.json`，SHA-256 `7c98af52d9d8fbc802aa216f6c3b656e055ec367e7df7bba6e39e90096fddc65`。共享 DLL/PDB 由官方 Release `build-shared.ps1` 生产，报告 copied 19 files / inputs 160 files；随后 `check-shared-plugins.ps1 -Configuration Release -Quiet` 成功。provenance SHA-256 `09898288431bf75455df4b123a56599742238f8921a7101e9548bbaa52bacce0`。

### 缺陷映射

| ID / 级别 | 位置与复现 | 修复及实际/期望 | 验收断言与证据 |
|---|---|---|---|
| D-1-03 / BLOCKER | `SessionCoordinator.cs` 关闭/查账路径，`TableSessionOperationQueue.cs` owner capture，`TableSession.cs` receipt 状态，`AppComposition.cs` Lite room close。原复现命令：`D:/game_dev_tools/dotnet/dotnet.exe run --project build/independent-r3-25278078211344aba308d934460e5b8a/shutdown-probe/Probe.csproj "-p:PlayerManaged=<绝对路径>/Mahjong_Data/Managed"`。 | 修复为：关闭先拒绝新 Submit 并等待已接收 Submit；单一关闭预算覆盖队列 capture、待查及已在途的同 scope ReceiptQuery；在途查询只等待，不重复发 query/command；Unknown、超时、writer 或 room dispose 故障均 quarantine 并阻止新 host，旧 outbox 保留。实际测试从真实 Lite `AppComposition -> TableSession -> LocalTableRoom` 发动作，而非 FakeSession 代替产品接线。 | 通过测试：`ShutdownWaitsForAlreadyInFlightOldScopeLookupWithoutIssuingAnotherQuery`、`InFlightOldScopeLookupTimeoutQuarantinesAndKeepsAuthorityOpen`、`ShutdownCaptureStallIsBoundedAndLateCaptureCannotCloseAuthority`、`RealLiteComposition_ReconcilesAppliedCommandAfterReceiptLossBeforeClosingRoom`、`RealLiteComposition_UnappliedCommandRemainsUnknownAndBlocksSecondHost`、`RealLiteComposition_WriterCloseFailureQuarantinesWithoutSecondHost`（CloseFault/CloseTimeout 两例）、`RealLiteComposition_LocalTableRoomDisposeTimeoutOrFaultQuarantines`（延迟/失败两例）。`AppCompositionTests` XML 29/29。Applied + 丢首回执例在关闭竞态中只发 1 次真实 action，query 的 requestId/scope 等于原值，收到真实 `TsumoWin` 并结算同一 round 后才关 room；未应用例得到 Unknown，未决 outbox request/scope 保留，旧 authority 仍开且拒绝第二 host。证据：`build/review-r4-player-9-5c6c837f438e4276a5208c31f55c9e51/editmode-results.xml`；Player 原件 `build/review-r4-player-9-5c6c837f438e4276a5208c31f55c9e51/player/`。 |
| D-1-01 / BLOCKER | 同包生命周期和 L01–L12 矩阵；原复现为生命周期 runner 后检查房间记录，再实际鼠标操作菜单、五案例、结算、退出/重启。 | 自动可执行部分已补：同一候选 Player 运行 10 个交替普通局/五固定局面的真实房间循环，并在 dealer-tsumo 局经 Client 提交真实自摸，检查权威 `TsumoWin`、同局 `RoundEnd` 及 room 关闭。人工物理输入和普通窗口关闭仍无可用交互桌面，因此不把组件或隐藏窗 runner 记作鼠标证据。 | lifecycle 结果 `caseCount=10`、room/round/scope/generation 各 10 个唯一、`roomClosed=true`，Player native exit 0；记录 1 个 action request，`acceptedActionEvent=TsumoWin`，`settlementRoundId` 与 dealer-tsumo round 相同；TCP 表取样 13 次、0 条 TCP connection（不含 UDP/系统范围抓包）。证据：`build/review-r4-lifecycle-9-517f3799933445ccb12f1d94c7e22e1d/player-lifecycle-result.json`，manifest SHA-256 `122000b870e24ae8b40c4770b452bc66c79118a170937ff3f1f5f4de713520bf`。 |

统一关闭负例矩阵实际均在上述 29 个 AppComposition 用例中执行：旧请求已应用但丢回执；命令未到 Host 且 lookup Unknown；lookup 已在途并成功/超时；owner capture 超过预算并迟到返回；channel/writer close 延迟/抛错；真实 LocalTableRoom dispose 延迟/抛错；已接收 Submit 与 shutdown 竞态。断言覆盖原 requestId/scope、最多一次 command、未知记录保留、超时不报告 Completed、旧 authority 未释放时不能启动第二 host。过期 callback 由 generation gate 与 `QueuedPublishMustNotDeliverAfterShutdown` 覆盖。以上是自动组件与真实本地权威负例，不是人工鼠标或整机网络验收。

### 本轮真实命令与结果

- Unity/Player 构建（共享锁 `Local\\MahjongV5P45UnityValidation`，等待上限 600 秒，心跳 30 秒，独占输出，native exit 0）：`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/build-g2-local-lite-player.ps1 -RunRoot build/review-r4-player-9-5c6c837f438e4276a5208c31f55c9e51 -TimeoutSeconds 1500 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30`。结果：AppComposition EditMode 29/29、Lite UI EditMode 3/3、C04 PlayMode 5/5，均 0 failure；Player build errors 0 / 193 files。XML SHA-256：`editmode-results.xml` `30ce739c7cef46c58b133c88934e6dc9c53c13902030fcdee7346bfa595db69c`；`editmode-lite-font.xml` `f11c4647afc7b4e87acaded0f67f26434970266019480357b8b90927ac7ffdf2`；`playmode-c04-actions.xml` `276ea35e2517613f2b7bffe317b00cde4c041dd072d28e1b10f0c228aca6f1cf`。runner log `build/review-r4-player-9-5c6c837f438e4276a5208c31f55c9e51/logs/runner.log` SHA-256 `10fa2d39b7c11505abd50fc5613751a9565fc2ca140177cde0cb40e587acc8c6`。
- 固定 .NET 回归命令（逐套件 native exit 0，无 skipped）：`D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --no-restore --logger "console;verbosity=minimal"` 206/206；`D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj --no-restore --logger "console;verbosity=minimal"` 182/182；`D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --no-restore --logger "console;verbosity=minimal"` 498/498。日志及 SHA-256：`build/review-r4-dotnet-9-4b20f5ce4ae2482b926b2e3848bff38c/LocalHost.log` `4db45fdfd121fb79fd616052f9d9f4c7ceb41cbc9f6739fdf2e65c4faf0c6a29`；`Client.log` `0d9f9d186de4e829b310c490688eaa27bf59b74adf3c2643810a364e80550b36`；`TableRuntime.log` `dd72dbf0ba382a7146f1501b2525080ffcd2ae7af4522a7315fcf0a1c66c611a`。
- 同包运行（Player native exit 0）：`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/run-g2-local-lite-player-lifecycle.ps1 -PlayerDirectory build/review-r4-player-9-5c6c837f438e4276a5208c31f55c9e51/player -DataRoot E:/agent-space/g2-local-lite-r4-2a2b7df38d4443d0a8116767688b1acf -EvidenceRoot build/review-r4-lifecycle-9-517f3799933445ccb12f1d94c7e22e1d -TimeoutSeconds 300 -MutexTimeoutSeconds 600 -HeartbeatSeconds 30`。结果 JSON SHA-256 `65837e0a4413b16cd4fea361507e2c22cb3adf2c6f4939132c7ae10df0aff9c5`。数据目录在执行树外，10 局 run 记录保留；一个 action 的实体/outbox 记录在清单内。

### 固定矩阵本轮状态与人工续跑

| 项目 | 本轮状态 | 证据边界 |
|---|---|---|
| L01 简化菜单并用鼠标开始普通局 | BLOCKED | Player 自动启动/生命周期通过；未有物理菜单点击和现场牌桌截图。 |
| L02 pon | BLOCKED | C04 PlayMode 自动组件通过；缺同包鼠标点击、副露现场证据。 |
| L03 chi | BLOCKED | C04 PlayMode 自动组件通过；缺鼠标选择组合及后续合法动作现场证据。 |
| L04 riichi | BLOCKED | 组件规则通过；缺鼠标弃牌、供托结算现场证据。 |
| L05 closed-kan | BLOCKED | C04 PlayMode 自动组件通过；缺鼠标操作与岭上补牌现场证据。 |
| L06 dealer-tsumo | BLOCKED | 同包真实 Client action、TsumoWin、RoundEnd 已验证；缺鼠标触发和 Settlement 场景实见证据。 |
| L07 荒牌 | PASS（权威规则/算分层） | 498/498 TableRuntime 包含确定性荒牌结算及四家分数守恒；不代表 Player 结算页鼠标验收。 |
| L08 荣和及非法/过期边界 | BLOCKED | 本轮没有同包鼠标动作，不能用其它动作组件结果替代。需保留原定真实 Client/权威合法、非法/过期零变更证据并结合可见鼠标路径。 |
| L09 10 次交替重开 | PASS（自动闭环范围） | 同一 Player 10 局身份唯一并关闭；一次真实自摸提交及终态结算记录；D-1-03 的掉回执测试断言 action 只提交一次。未宣称其它动作均做过重复扣费测试。 |
| L10 并发/失败关闭/过期回调 | PASS（自动范围） | 29 个 AppComposition 测试覆盖并发 shutdown、Submit 竞态、超时/故障、拒绝第二 host 与迟到回调；不包括人工连点。 |
| L11 数据隔离/未决 outbox/隐私 | PASS（自动范围） | 10 个 room scope/outbox 隔离；Unknown 真实 Lite 记录保持未决；公开 run/case 日志证据无暗牌字段。范围不扩展为正式对账/全隐私审计。 |
| L12 无连线与正常窗口退出重启 | BLOCKED | 生命周期期 Player PID TCP table 取样 13 次无 TCP 连接；不含 UDP/OS 全局观察。隐藏进程 Application.Quit 不等同 GUI 点叉正常退出；缺交互桌面重启证据。 |

**外部人工验收的最短步骤：**在可操作的 Windows 交互桌面，运行上述冻结包目录中的 `Mahjong.exe`（不要只复制 exe）；记录 exe SHA 与本节 build identity。鼠标点击“开始一局”并记录桌面/牌桌；返回开始，点“测试局面”，依次选 pon、chi、riichi、closed-kan、dealer-tsumo 并亲自点击实际动作，保存每步截图、牌桌结果、dealer-tsumo 结算截图及对应 `Application.persistentDataPath/G2LocalLite/runs/<runId>/` 证据。再执行“再开一局”“返回开始”、窗口关闭、重新启动；保存两个 run 的日志/身份及 OS 范围网络观察。L08 在出现荣和候选时点击荣和，并对非法/过期候选使用既有真实 Client 负例，不新增产品局面入口。若操作者未提供这些原件，以上 BLOCKED 项保持 BLOCKED。

## 第 5 轮复核返修：D-1-03 transient transport loss（2026-11-20）

### 缺陷与修复映射

- **ID/严重级别：D-1-03 / BLOCKER。** 新增实测：同副本 Applied receipt 到达时发生 transient transport loss，Client 已从 Ready 进入 Reconnecting，但原 command 的 delivery outcome 仍未知。此前第 4 轮丢回执注入没有断开 transport，Client 保持 Ready；故这是新状态覆盖，不重开已通过的 Ready/in-flight/capture/writer 断言。
- **位置：** `unity/Mahjong.Unity/Assets/Scripts/App/SessionCoordinator.cs` 的 `RunShutdownOnceAsync` 关闭前检查；回归在 `unity/Mahjong.Unity/Assets/Tests/EditMode/AppCompositionTests.cs`。
- **复现命令：** `D:/game_dev_tools/dotnet/dotnet.exe run --project build/independent-r4-2813dd8e51394901aa0161628f4ce19a/transport-loss-probe/Probe.csproj "-p:PlayerManaged=<候选包>/Mahjong_Data/Managed" -p:NuGetAudit=false`。原独立 R4 候选实测为 `authority receipt=Applied`、`state=Reconnecting`、`deliveryUnknown=true`，但关闭 `Completed`、关闭旧 channel、0 次 ReceiptQuery、outbox 仍有 1 条未决。这说明 Ready 门槛跳过了关停清点。
- **修复要求与结果：** 关闭不再用 Ready 作为是否 capture/清点的总门槛。所有状态均在同一有界 deadline 内 drain 已接收 Submit 并 capture；只有发起新的 ReceiptQuery 才要求 Ready。Reconnecting 且有未决请求时返回 `CloseFault` 并 quarantine，保留旧 room/outbox，不重放且拒绝第二 host；Reconnecting 且无未决时仍可正常关闭。
- **验收断言与证据：** 新增真实 Lite composition 测试 `RealLiteComposition_AppliedReceiptTransportLossQuarantinesUnresolvedBeforeRoomClose` 断言真实 authority Applied、action 计数为 1、原 scope/request 保留、0 次错误状态下 query、非 Completed、旧 host 未关、第二 host 被拒绝；`RealLiteComposition_NonReadyClientWithoutUnresolvedCommandsClosesCleanly` 覆盖空 outbox 正例。Unity 同候选 EditMode 31/31；既有 Ready 在途查询、deadline、unknown、writer/room close 负例均仍通过。复用用户给定的真实 transport-loss harness，最终候选结果为 `shutdown=CloseFault quarantined=True oldChannelCloses=0 receiptQueries=0 durableUnresolved=1`，native exit 0。探针日志 `build/review-r5-transport-probe-0397ddd1ea994b3aa18c9a74e43355d9/transport-loss-probe.log` SHA-256 `f491d11d5a3120876c53a85fb4779eae530a978695089d13b3c9afbf62a20ad7`。

### 定向候选证明

- 独立输出：`build/review-r5-candidate-2-a136079448a148409f70a8e70c044218/`；Player 完整目录：`E:/agent-space/thread_20261119_192821_cc27/worktree/build/review-r5-candidate-2-a136079448a148409f70a8e70c044218/player/`。构建 runner native exit 0；完整 Player 193 个文件逐项验证 193/193，package manifest `player-package.json` SHA-256 `dc34cc631d678b820cfdcaaccd2fa8d593e5e9e5bf63e5316475c6267c90e277`；manifest SHA-256 `a9052998d60a3ddf66c9936ef1e6800faf3b77fd0423cb6114fcd9e691fdd992`；exe SHA-256 `f083b0e879a9298cdcf9933a8f3b4f2b013e13f7d3e51221fc0d756b79e28807`。
- 源码身份：HEAD `5fe836504af627da67cf46af0ca6657eafdacee5`，worktree diff SHA-256 `4daaca3590b8b2a93165730cf6a83c108376662ebfaa5fe397a203c2dcc6f0c7`，由 `player-package.json` 与 build identity 记录。共享 DLL/PDB 仍经官方 Release build-shared 产生 19 个文件并由 provenance checker 核验；provenance SHA-256 `e3c05a06d9dfb451eadfc0ec67952636371e68434202d79f041803e1e9ad66fd`。
- Unity XML：AppComposition 31/31、Lite UI 3/3、C04 PlayMode 5/5，无失败/跳过。文件分别为 `editmode-results.xml` SHA-256 `8f8950e4b08235e4610568833a26a38ae7c89c62ecf84c658883149d4a8bbfac`、`editmode-lite-font.xml` `a8f9321e13e40a21235b7871d6f0d87eac3b3a01a18f453618ee74939bff3c31`、`playmode-c04-actions.xml` `4498abca056606b416e66f2286e008b4cd867e39814289be3091195c584ea7a7`。
- 最终候选 Player 生命周期重跑 10/10，room/round/scope/generation 唯一、roomClosed=true、Player exit 0；14 次 Player TCP table sample 无 TCP 连接（不含 UDP/OS-wide capture）。结果 `build/review-r5-lifecycle-2-fc703ab7f768488e82f581cb5bf59302/player-lifecycle-result.json` SHA-256 `1e14d774d74ec94b27d5c7831627c36570d7b2f07000638594709cabc6169544`；manifest SHA-256 `07d6ead948c10aacbd308419d687e08252bc973bb898050019de27e351c43fb6`。
- 首败保留：`build/review-r5-candidate-7a3f2ed37a2142d98ee04e9709578d61/` 的两条新测试首次错误读取了没有 transport-state 通知更新的旧 `SessionCoordinator.Current` 投影，日志/XML 显示测试读到 Ready。改为检查同一个 coordinator owner queue 的真实 Client 状态后，原状态和关闭断言保留并在新 GUID 输出通过；没有放宽断言、skip 或覆盖首败。

本轮仅修改了关闭状态门槛及对应 Editor 回归/返修记录；.NET 核心实现无变化，沿用上一轮已记录的 886 项回归结果。本轮未重跑物理鼠标/窗口退出人工验收；它们仍是先前明确的外部 BLOCKED 项。

## 最终验收收口（用户确认，2026-11-20）

用户通过 Decision ID `3e9c046c-e00d-4ec2-aeb7-741beb15a48b` 确认已在 Windows 交互桌面使用第 5 轮冻结包 `review-r5-candidate-2` 完成 L01–L06、L08、L12 人工验收：鼠标操作成功，结算页正常，关窗后重启可正常进入，运行期连接观察符合预期。该记录来源为用户明确确认；本执行环境没有另行取得物理鼠标原件或 OS 全局抓包，故不冒称独立采集。

结合第 5 轮自动证据（AppComposition 31/31、Lite UI 3/3、C04 PlayMode 5/5、同候选 Player 生命周期 10/10、193/193 包哈希、构建 exit 0）及此前 L07/L09/L10/L11 自动验收结果，L01–L12 当前均按用户确认范围验收通过；D-1-03 已关闭，本任务无阻断缺陷。此结论仅限 G2-Local-Lite 版本范围，不扩展为 G2 总验收、移动端、设备或上线验收。
