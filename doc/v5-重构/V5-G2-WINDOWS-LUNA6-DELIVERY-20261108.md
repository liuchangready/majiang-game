# G2 Windows Luna6 执行交付（2026-11-08）

## 结论与范围

本交付记录实际 Windows Player/PlayMode 执行及首败修复，不是 G2 签署。统一入口生成的计划为 41 行（33 C04 + 8 C05）；本轮 Windows 分母固定为 32。Android 范围 9 行（C04-G8-03 与 C05 八行）全部 `NOT_RUN`。

逐例结果：**19 PASS、5 PARTIAL、1 FAIL、7 NOT_RUN**。32 行不满足整体验收条件；`V5-2-REVIEW.md` 中 `G2_NOT_ACCEPTED` 状态不变，V4 生产地位不变。详见 [逐例绑定](V5-G2-WINDOWS-LUNA6-BINDING-20261108.json)。

## 基线、输入与候选

- 派发基线：本地 `g2_base`，`D:/majiang-wt/g2_base`。
- 本任务工作树分支：`mesh/task/thread_20261108_190230_2b67`。
- 实际起始 HEAD 与 merge-base：`4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`；派发前基线树同 SHA、干净。本任务没有提交、推送、合并或发布。
- 实际 dirty 输入：`dotnet/tests/Mahjong.LocalHost.Tests/` 下 7 个测试文件、`tools/unity/run-p45-full-match.ps1`、本任务卡和交付文档。产品 C# 源和 Unity 场景未修改。
- Unity：`D:/game_dev_tools/unity/Editor/Unity.exe`，`6000.3.27f1`；.NET：`D:/game_dev_tools/dotnet/dotnet.exe`，SDK `8.0.426`。使用 Unity Windows Player，本轮没有用 Gradle/V4 桌面版代测。
- 任务计划：`build/g2-win-plan-8d5065e9/plan.json`，SHA-256 `9871A8D3D06C496DBE3E7BBBAF45156B26D09507B829887FD2FBD22D6F1ACC22`。初态、触发、断言、绑定逐例保留在 JSON。

## 已执行的组件与套件

| 范围 | 实际结果 | 稳定证据 |
|---|---|---|
| 入口合同/计划 | `G2TestRunner.Tests.ps1` 两次均 PASS（30 assertions）；计划 41 行、Windows 32、Android 9 | `build/g2-runner-test-4a4f5bd7053f4fd483ae97c8972117c0`、`build/g2-runner-test-6a03b35fd5f14537bb95e59f99f098a0`、Plan wrapper |
| WindowsFullMatch | runner 原生 exit 0；Player 完成 12 局到 `WestCompletion`，266 个动作；12 局均为荒牌流局、无和牌。G1-04 及本地整场 G7-01 有真实 Player 证据；G1-02 和牌、G1-03 连庄断言缺失 | `build/4091659e35504e70849026b37de28d0c/executions/p45-20261108-191351-2636b2d1/driver/summary.json`，SHA-256 `00C98B91BBD65BEDAD40AE8C27AF4C39B74383CE3BA74D0C1887C4C12DC574F3`；封存 `evidence/artifacts.json` SHA-256 `2AB89F92D297F0F687255FCD0A0F132C1D80200E47D5DD4A5B1D2494DF9FFF56` |
| WindowsCheats | 新候选 verifier 已显式构建并绑定。真实 Player 授权流程 29/29 assertions、负例 15/15；三绝技、精确费用/私有账本、136 唯一实体、越权与余额不足拒绝均在断言中通过。套件报告仍为 `FULL_D04=NOT_PASSED`，只据其原始行判对应 G3，不据 runner 完成推导整套通过 | `build/f2661164058e4d8bb89c62cda564a73c/authorized/case-authorized.json`、`negative/case-negative.json`、`seal.json`；seal SHA-256 `F4D352E798D8936BF0BF1CAB55222DE4DA35E16FFFEBB07F431B2649A4A53097` |
| WindowsRecovery | 真实 Windows Player + verifier，G6 01–08 共 8/8 PASS。包含本任务自有 Player 的强制终止与冷启动；没有杀其他进程 | `build/ea220144afac47018211497446d09ad2/summary.json` SHA-256 `88950C827E26092C1E53D1AC20E5B16D5A96AE7C4A5EAD35DCD7F49282E39B1E`；逐项 `cases/C04-G6-01..08/{result,verification}.json` |
| WindowsPrivacy | 独立 Unity PlayMode 真实测试 7/7 PASS、失败/跳过/未跑均为 0；包含 four-owner、role-switch、closed-binding、recovery 原始场景与 verifier | `build/57682b7e70fa4bd59b7431611cf2eb65/summary.json`，`testXml.total=7, passed=7`；raw scene 与 verifier 文件在其 `windows-app-cases` / `verifier-tests` 下。封存 manifest SHA-256 `4c0be86904ec0e0dff421ff62be6f02a2e8e8b9c7fbe2d870db7a74ee66b5c61` |
| WindowsAppMatrix | 普通 Windows Player 7/7 场景 PASS；3 个参数负例按预期拒绝；P2 原 outbox 跨进程恢复 PASS，receipt 查询 1、重复命令 0；包执行前后 SHA 一致 | `build/5f60c821e46044969eb301d59ccfb859/summary.json`、`windows-app-cases/`、`seal.json`；seal SHA-256 `050C8C291CC0EA3083AA8FB07A132432EC7DE6A6F599AD6DBAB658D3495D762C` |
| WindowsActions | **未完成 Player 阶段。** 冻结源上的 LocalHost 测试首先发现 13 个测试夹具路径错误；修复后同一 LocalHost Release suite 独立复验 184/184 PASS，但冻结归档 runner 对未提交 dirty 候选只允许干净 HEAD，不能将修复吸收入该 runner 的候选。G2 四个 Player 案不得据组件复验标 PASS | 首次失败 `build/03df57915be94225a3aec489dc03a072/failure.json`；修复源码和复验见 `localhost-rework-4b351fab/` |

## 首败、根因与处置

| 首次结果 | 根因 | 修复/复验与当前状态 |
|---|---|---|
| WindowsFullMatch 前置失败 `Illegal characters in path` | PowerShell 5.1 收到 `git ls-files --others` 对中文 untracked 路径的默认 C 八进制转义，并把引号作为路径字符 | `tools/unity/run-p45-full-match.ps1` 的该只读枚举改为 `git -c core.quotepath=false`；UTF-8 精确路径正例能定位并 hash、旧默认转义负例复现；FullMatch 之后真实构建/运行通过。原失败保留于 `build/g2-win-fullmatch-611a2c3f/`。 |
| WindowsActions 首次因 dirty-source guard 结束，未运行测试 | 本任务卡、FullMatch 修复及 Unity 生成文件使工作树非 clean | 将本任务文档和工具修复临时移至 ignored `build/`、只还原本任务生成的 tracked Unity 输出后按合同运行 clean-source runner；不清理共享文件。 |
| clean Actions 的 NuGet restore 为 NU1101 | runner 使用隔离空缓存，缺 `Google.Protobuf`、`Grpc.Tools`、`xunit.v3` 等包 | 从既有全局 NuGet 缓存核验 `.sha512`/SHA-256，生成 `build/luna6-clean-run-20261108/nuget-seed-audit.json`（SHA-256 `8FC6F33DFC9FD23E0E00027EED723917473149057D5EB6A55AA5081A3A7CD524`），通过 runner 显式 seed；不改机器配置。 |
| NuGet seed 在长路径复制发生 MAX_PATH 异常；cheats 另一次在 junction 下被 `REPARSE_POINT` 守卫拒绝 | Windows 深路径限制；证据守卫禁止 evidence-root reparse point | 在本任务完成核验的工作树上用临时 `SUBST R:` 短盘符；该路径不是 junction/reparse，runner 的根身份/head 检查仍通过。 |
| WindowsActions LocalHost 测试：184 项中 171 pass、13 fail | 测试夹具将 crash-worker / TableRuntime DLL 写死为旧 `bin/Debug` 或 `bin/Release`，runner 使用 `--artifacts-path` 后产物位于 `dotnet-artifacts/bin/<Project>/release` | 新增 `P41Harness.FindBuiltProjectAssembly`，先沿测试产物祖先搜索 artifacts 布局，再 fallback repo Debug/Release；修正 6 个测试文件引用。独立 Release build/test XML：184 pass、0 fail、0 skipped、0 not-run；XML SHA-256 `33807967360090C051421AACB4D80D34CE898DC5ED6A14C731DBB2B6E8B2`。该 patch 已回到当前工作树，但不能绕过 Actions 的 clean-commit archive 守卫；WindowsActions/Player 与 4 个 G2 行保留 NOT_RUN。 |
| 普通 Player 的可视启动截图未通过目视布局检查 | 实际 Player 已到 `Menu`，但画面只见裁切的标题/大块空黑画布；Player.log 同时出现 EventSystem/UI 多个 missing-script 警告以及 `InputSystemUIInputModule.OnEnable/OnDisable` NullReferenceException。Windows 可用桌面当前为 1248×832，非合同要求的 1280×720；所请求的 1280×720 windowed 启动仍得到 525×323 客户区 | 保留 `player-fullscreen-startup.png`、`player-windowed-1280x720.png` 及原始 Player.log。该行为暂列 G8-01 FAIL，根因未充分定位、未修复、未复验，不能将画面判 PASS。 |

首次 malformed FullMatch 参数调用、PowerShell 未使用 `ExecutionPolicy Bypass` 的 pre-run 调用均在 runner/Player 启动前被拒绝，不产生产品用例结果；其后使用合同参数重跑的原件仍保留。无外部进程被终止，无共享 Unity 锁被绕过。

## 包与输入闭包

所有执行包的源码 HEAD 均为 `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`，但 runner 分别构建新包，不能拼成“同一包 32 项通过”。Player 可执行文件 SHA-256 为 `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`；各自完整包文件清单与 BuildReport 位于对应 raw root：

| 套件 | Windows Player/包绑定 |
|---|---|
| FullMatch | `build/4091659e35504e70849026b37de28d0c/player/Mahjong.exe`；`package/c04-package-manifest.csv` SHA-256 `AC85D5A23E351C011A251AC24730F7956DDF8DA4C2B799F56F33BEA24456DF73`，192 files；BuildReport 在 `package/c04-build-report.json` |
| Cheats | `build/f2661164058e4d8bb89c62cda564a73c/build/player/Mahjong.exe`；verifier package manifest SHA-256 `8179CE91C82FDE31C6AD5D645991C32B5A7B4528DE4BE58AB87F25650385B401`，192 files；`build/player/c04-build-report.json` |
| Recovery | `build/ea220144afac47018211497446d09ad2/player/Mahjong.exe`；package manifest SHA-256 `539E113AE8859D7430CB6D4B22D898C912B243ACC40C17987F5066D481BA153D`，192 files；`build-marker.json` 与 `logs/editor-build.json` |
| AppMatrix | `build/5f60c821e46044969eb301d59ccfb859/player/Mahjong.exe`；包 SHA-256 `54dab661015ae6f260477796a259434df5abde87a5b7d6db2919c55d186257a6`，192 files；BuildReport `player-build.json` SHA-256 `C78458605E20B7C1A7DA6C95D9186E250319679BACDAB595A76080BB9FC72AF2` |

AppMatrix 8 个共享 DLL 闭包（文件 SHA-256）为：`Google.Protobuf.dll` `16921420D56EE464D4B4C43BA70C7300172E2CF3852F80CB91B71932569D3A5F`、`Mahjong.Ai.dll` `CE5B833EFC56817325D724E342D7CBE72F18862E61A8C02AA0C28CB955E1ECC5`、`Mahjong.Client.dll` `5C0B8A865106F28A33649E30EA202F1E90E7C50377B364CDB00318114988A0E1`、`Mahjong.Contracts.dll` `91B99D8AB3591AF0FB20B464945E505A0F6F884949A163E128F895A3E2477824`、`Mahjong.Contracts.Wire.dll` `FA658F6EC385A4620287E4E978D93A5826B69E982ADBFAAC9099CEB42C6C2645`、`Mahjong.Domain.dll` `34A5CC0B7692B037FC2650EF60F04952AE3FACCEFBCB95111644CAB49024272A`、`Mahjong.LocalHost.dll` `2777E2EFFB7130B154D4C08ADFCC23749DE30772142F91759894C92113218FF3`、`Mahjong.TableRuntime.dll` `E6D65D29F3721F433264CFCF23B64B0FF8A64A535CC19A74D49FB4CA4814935A`。完整 Player package file list、shared plugin provenance 与 543 项输入闭包均在 AppMatrix raw root，manifest seal 覆盖 3004 个证据文件/292,971,279 bytes。独立 PlayMode XML 为 `build/57682b7e70fa4bd59b7431611cf2eb65/playmode-results.xml`；输入闭包 SHA-256 `1DFA95B1B634760FE5C7BBA33075B94248BD9B4CF2A3350E8E33CCEC8A80CCC0`。

## 接续检查点

当前任务分支保留上述最小代码/测试修复与完整 `build/` 原件。下一步必须先在当前任务生命周期中解决并复验普通 Player 菜单布局/missing-script 根因；再让已修正 LocalHost fixture 的候选进入 WindowsActions 冻结归档和真实 Player 的 G2-01..04；补 G1 和牌/连庄、G4 显式 claim/控制代次、G7-02 实际权威拒绝 LOCAL 资产、G8 支持比例截图及鼠标/键盘效果。修复若改变共享 DLL 或 Player 输入，需重建候选并按各 runner 的 fresh-build 合同重新绑定受影响证据。Android 9 行不属于本任务，仍 `NOT_RUN`。独立 reviewer 依 V5 复核协议复核；此交付不授权提交、推送、合并或 G2 最终签署。

## 独立复核第 2 轮：D-1-01 至 D-1-05 全量缺陷台账与处置

复核原结论为 1 BLOCKER、4 MAJOR；原首次失败和旧 binding 均保留。本节逐项记录本轮修复映射及未关闭条件。不得把本轮执行过构建/测试写成 G2 通过。

### D-1-01 — BLOCKER — 普通 Windows Player UI/layout

- **位置/复现：** Boot/Menu 的 EventSystem、Canvas、PlayButton；binding C04-G8-01/02。启动原件 `build/5f60c821e46044969eb301d59ccfb859/player/Mahjong.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720`，查看原图 `build/g2-win-visual-luna6-20261108/player-windowed-1280x720.png` 和日志。原图 SHA `810599DC0963B664DB687AEC41EFEA1DA79419163D8DBD83552984838FD6FCC7`，原日志 SHA `19282C0E03875C83ED757BE8BCADA4777D42213CFEB9C9B042C00E296F4511A0`，从未覆盖。
- **实际 vs 期望：** 旧原件有 UGUI/Input System missing-script 及 InputSystemUIInputModule NRE。GUID→MonoScript 诊断证明引用的标准包脚本及类型有效；编辑器重新编译后 scene scan 无 missing component，新 Build log 也无 missing-script/NRE。但新鲜普通 Player 图像仍只有标题与背景局部，菜单按钮不可见。请求 1280×720，实测 window 525×323、client 512×288、DPI 240；进程正常 close、exit 0。期望完整菜单、可操作流程与输入→请求→权威回执闭环。
- **本轮证据：** 新鲜 build-only Player `build/l6-r1-c00350e8c86f4ad3ad8e377ffff0d4f0/player/Mahjong.exe`，192 files；全包 `player-files.json` SHA `C0AAB4D93760B214797687D01795836E448BFAFBB1021C281AA40684DCC9D357`，exe SHA `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`。Unity log SHA `3FBEB853F7D7D299ADCFCBC671D0CEDF1B803845A56CE64F3FDA621A4AF6023F`；普通运行 `.../ui-observation/process.json` SHA `2B0BEAD92786398D887AB61311622A373240F7D4864CB872A1D91434B396D084`；新截图 `.../ui-observation/windowed-player.png` SHA `8D91C97FF6A24BCB420D4003E08E6CB38C84A81A4DB1172AB2F9750ED8FBF2D3`；GUID 诊断 `build/ui-diag-2c76b2f8a90649cda4878e6a47a39144/unity.log` SHA `E19D4FD541F6FB99A98FC122CA54802B30C051EED3EE5F68BDD9F04165365A57`。本次指定 Player `-logFile` 未产生文件，不声明新 Player.log 通过。
- **DPI 观察与封存：** `.../ui-observation/dpi-probe.json` SHA `DB2F3281325D0244DC85AAA4C6A19A5056D7788389FEF82E2C00E534F2BDD0D4`，Win32 `GetProcessDpiAwareness` HRESULT 0、awareness 2（per-monitor aware）。本轮普通启动原件已封存于 `.../ui-observation/manifest.json` SHA `AD55AE2841C8C4C8178AFA509540A45EF3D5946F08CC5807449153B0391CBCA6`；Build-only evidence manifest `.../evidence/artifacts.json` SHA `3F2409568DF520D5C932063039DD688BF7B08CC493561CB739F3C9301473086E`。裁切不能简单归因于 DPI-unaware，Canvas/窗口映射根因仍待处理。
- **修复要求/验收断言：** 继续定位导入缓存时序与实际 DPI/窗口映射，修复窗口/布局；支持尺寸全帧截图目视通过，用真实鼠标/键盘完成菜单→房间→对局→结算及弹窗，逐次绑定输入、最多一次请求与权威回执。**状态仍 BLOCKER：新鲜截图失败。**

### D-1-02 — MAJOR — 固定 Windows 32 项逐案覆盖

- **位置/复现：** binding 的 G1-01..04、G2-01..04、G4-01..03、G7-02；原 `build/03df57915be94225a3aec489dc03a072/failure.json` 与 FullMatch `.../driver/summary.json`。复核时为 7 NOT_RUN、5 PARTIAL、1 FAIL；12 局全荒牌，Actions 在 LocalHost 首败后未进 Player。
- **实际 vs 期望：** G1 缺真实和牌/荒牌并列、连庄断言；G2 缺碰吃立直/双立直/杠岭上抢杠 Player 正负例；G4 缺旧 AI 决策拒绝、同房重入、显式 claim/epoch；G7-02 缺实际权威入口拒绝 LOCAL 资产及合法服务生成正例。期望 census 原断言、请求/状态/回执、负例完整绑定。
- **修复要求/验收断言：** 修复 fixture 后必须让 WindowsActions 在合法 clean candidate 上跑真实 PlayMode/Player，并补足上述最小场景及 G7 服务路径；各行 0 skip、真实计数/状态与原件齐全。当前没有桥接冻结检查点工具；本执行者不手工提交、不藏 dirty 输入、不绕 clean guard。本轮没有把缺失 Player 行判 PASS。**未关闭。**

### D-1-03 — MAJOR — P41 artifacts 依赖错配/旧 DLL 回退

- **位置/复现：** `dotnet/tests/Mahjong.LocalHost.Tests/P41Harness.cs` 的 `FindBuiltProjectAssembly` 和调用点。原 probe `build/review-2551ab1d/probe/Program.cs`、`probe.log` 复现 Debug artifacts 错返 repo Release DLL。
- **修复：** helper 按实际 artifacts 根及活动 Debug/Release 配置定位依赖；隔离根缺失即失败，不跨根/配置回退。新增 `P41HarnessArtifactResolutionTests.cs`：Debug/Release 同根正例；隔离 Debug 依赖缺失但 repo 存在旧 Release DLL 的负例。初始补丁迭代 184 pass/3 fail（artifacts 识别错误把目标依赖项目名当成测试项目名）原始日志留于 `build/rework-d103-0ad6ac868e02454cab9b895e571e579a/`；根因已修正。
- **复验命令/结果：** Release：`D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --artifacts-path build/rework-d103-focused2-a35641d45ff744ea9451fa34272ecd2c --filter FullyQualifiedName~P41HarnessArtifactResolutionTests --logger trx;LogFileName=d103-focused.trx`，native exit 0，实际 187/187、0 skip，日志 SHA `3F3CDF177A5F19B27569D6B61ED1D122153AD9E26148B65742177B569324FA8C`。Debug 使用 `-c Debug --artifacts-path build/rework-d103-debug-0b1d04c34b6440e5ac1057b084335ef5 --logger trx;LogFileName=d103-debug.trx`，native exit 0，187/187、0 skip，日志 SHA `8A3D5E9A6B889F26FA29CD860DB39A860CF5F221C12DAC5270649FEA4423D700`。虽传 `--filter`，当前 Testing Platform 仍实际执行全套 187 项，报告按真实计数，不称 3 项定向计数。
- **工具回归：** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/G2TestRunner.Tests.ps1` native exit 0、30 assertions；`build/g2-runner-test-0771255ce47743c298dc03ccf37c0927/test-summary.json` SHA `BBAE3989455EB5188471DEBCCDCC99EC8EDEF4BA3E1D45908EA6EC3E44E3F237`，标记 `TOOL_FIXTURE`，不作为产品行通过证据。
- **状态/断言：** helper 与真实正负路径用例通过；D-1-03 的 Actions Player 前置复验尚未在冻结候选上运行，因此 D-1-03 仍有未关闭验收项（也与 D-1-02 交叠）。

### D-1-04 — MAJOR — G7-01 没有离线网络证据的误 PASS

- **位置/复现：** binding C04-G7-01，与原 FullMatch `build/4091659e35504e70849026b37de28d0c/executions/p45-20261108-191351-2636b2d1/driver/summary.json` 及 `build/4091659e35504e70849026b37de28d0c/command.json` 对照 trigger/assertion。
- **实际 vs 期望：** 旧理由只有没有启动远端 service 进程及完成 12 局；summary 无网络隔离、channel kind 或进程出站观察。不能推出真实 offline/no attempt。期望同一 Player 在真实离线隔离环境完成整场并观察本地 transport/出站行为。
- **本轮修正/验收：** binding 已将旧 PASS 更正 `NOT_RUN` 并保留原件。本轮未改变系统网络/安全设置，也未取得受控离线环境。必须绑定 channel 类型、环境、进程、包、比分/账本/排名与无远端尝试观察后才能 PASS。**未关闭。**

### D-1-05 — MAJOR — 返修候选与同包闭包

- **位置/复现：** Delivery 基线/包闭包及 binding `candidate/packages/runs`。对比每套件各自 manifest 与 seal；exe 相同 hash 不证明场景/资源/插件全包相同。
- **实际 vs 期望：** 旧 FullMatch/Cheats/Recovery/AppMatrix 各自构包；新桥接 HEAD `bda5e039f920a939003eea847758928300d75fd1` 上又有本轮返修 dirty 输入。期望最终候选及同一 Player 32 行统一绑定、8 DLL/资源/插件/BuildReport 闭包。
- **本轮证据/要求：** 本轮新 Build-only 包 192 files、manifest SHA `C0AAB4D93760B214797687D01795836E448BFAFBB1021C281AA40684DCC9D357` 只作 UI 复验，不代表最终矩阵。Runner `source.json` 捕获在 shared DLL 构建前；随后 `GeneratedAssetEvidence` 又记录 87 个生成文件和 patch SHA `B8923B56A6AE888E751F0B2590B7A1677DE38E851C6474C65E5BBC7C9179AED7`，故 build-only 包输入不能以 source.json 单独代表冻结闭包。Binding 已列本次包的 8 个共享 DLL SHA。现有分套件包仍是支持证据。待桥接生命周期冻结候选后，遵守每个 runner fresh-build/verifier 合同重建受影响证据；不得放宽 clean/fresh guard。**未关闭。**

## 本轮状态汇总

- 起点及 merge-base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1` 与桥接交付 HEAD `bda5e039f920a939003eea847758928300d75fd1` 的关系保持；未人工提交/推送/合并。本轮返修后工作树 dirty，Build-only Player 不是冻结候选。
- D-1-03 helper 修复和 Release/Debug 187 项全量回归通过，但 Actions preflight 未跑，故该 ID 仍有验收项未关闭；D-1-04 已纠正旧误 PASS 为 NOT_RUN。D-1-01 仍 BLOCKER，D-1-02/D-1-04/D-1-05 仍 MAJOR 未关闭；任务范围不通过，不 approve、不签署 G2。
- 本轮统一负例矩阵只覆盖 D-1-03 artifacts 正负例。其他缺陷的 UI/用例/离线网络/最终同包验收尚未完成；不得以工具测试、exit 0 或本轮实际构建替代。
- 将 G7-01 的旧误 PASS 更正为 NOT_RUN 后，32 行当前计数为 18 PASS、5 PARTIAL、1 FAIL、8 NOT_RUN；原 reviewer 缺陷描述中的 7 NOT_RUN 是纠正前计数。
- 本轮更新 binding 的 SHA-256：`32A0B65C238A88A22A273C5404717EABD82A1F97E290427A821750F6EBA6DFB6`。

## 独立复核第 3 轮：五组 MAJOR 的批量处置

本轮起点是桥接自动提供的干净 HEAD `7f3942490eab8000e731f94049b48c10850a2257`，父为 `bda5e039f920a939003eea847758928300d75fd1`，merge-base/原始派发基线为 `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`。未重置、未手工提交。源码及本节记录现处于 dirty 状态；WindowsActions 修复后尚未在新冻结候选上重跑，32 行矩阵仍未通过。

### 执行及组件证据

- 在未改源码的 clean HEAD 上先执行 WindowsActions。成功短路径 runner `build/508260b3991f44cea1c374e5c8688019` 的 fresh Release build exit 0；LocalHost 187 项为 186 passed / 1 failed / 0 skipped / 0 not-run，Actions 在进入 PlayMode/Player 前停止。失败是 reopen 后旧 decisionId 被复用并被接受。完整目录副本保存在 `E:/g2-r3-evidence-thread_20261108_190230_2b67/508260b3991f44cea1c374e5c8688019`；复制前后 3,783 文件、960,194,469 bytes，关键 `frozen-source.tar`、`failure.json`、`manifest.json`、`summary.json` SHA256 一致。C 盘原工作树中仍有少量占用文件；不覆盖/伪造原失败。此前缺 NuGet seed 的尝试、MAX_PATH 复制尝试及错误短路径尝试也各自保留，不计产品失败。
- D-2-01 原 Release 首败由独立报告复现：`dotnet.exe build ...Mahjong.LocalHost.Tests.csproj -c Release --artifacts-path build/review-r2-a198409d/Release`，随后 `dotnet.exe exec .../Mahjong.LocalHost.Tests.dll -noLogo -automated -failSkips -result-xml .../Release.xml`；187 项 186 pass / 1 fail。`TerminalRoundSurvivesColdRestart` 在 `File.Replace` 报 Win32 1175 / HRESULT `0x80070497`（无法删除要被替换的文件）后返回 `COMMIT_OUTCOME_UNKNOWN`。以真实 Windows 句柄锁住备份目标可稳定复现完全相同的 HRESULT/Win32 1175，因此确认了本代码路径上的具体竞争机制。独立首败发生时存档现场已被失败用例清理，无法证明当时具体哪个进程/句柄持有备份；不把持有者推断成杀毒或固定外因。代码现在只对该异常做最多四次有界重试，重试前校验原存档可读、临时候选未变且目标提交字节完全匹配；持续锁仍保留 OutcomeUnknown/旧 envelope，不吞错、不覆盖写、不盲目重提副作用。
- 当前 dirty 源码完整 Release：`D:/game_dev_tools/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --artifacts-path E:/g2-r3-release-artifacts-20261108`，exit 0、0 warning/error；随后从仓库短路径 junction `R:\thread_20261108_190230_2b67\build\r3-release-link\bin\Mahjong.LocalHost.Tests\release\Mahjong.LocalHost.Tests.dll` 执行 `-noLogo -automated -failSkips -result-xml E:\g2-r3-release-LocalHost.xml`，exit 0，189/189 passed、0 failed/skipped/not-run，XML SHA256 `675F3A84B33F8BCF09A33626EFF1EF149D81B195072076AF3E2FDBBA99A8A697`；build log `E:\g2-r3-release-build.log` SHA256 `9A48DAACD2C8044C918DAC0CED4A0C75099032930C003FDF72D0D81FB6F1F17D`；test log `E:\g2-r3-release-LocalHost.log` SHA256 `967650E5421BA1F6C1A9BEAE63DB9713DDC281BB0CE39DAC0D5E51F07C9F5B34`。
- 当前 dirty 源码完整 Debug：build 同项目 `-c Debug --artifacts-path E:/g2-r3-debug-artifacts-20261108`，exit 0、0 warning/error；经仓库短路径 junction 执行完整套件 exit 0，189/189 passed、0 failed/skipped/not-run，XML SHA256 `657BAAD1DAB7674F15DB2E4C5FB577149B65F287D5F96F908E84606A9BEF6480`；build log `E:\g2-r3-debug-build.log` SHA256 `B3CD5F6ABA1FB45B3252F3BF01DDE370C2AD4E9E3D1E11971736D33CDBB19005`；test log `E:\g2-r3-debug-linked2-LocalHost.log` SHA256 `C1E97F506B3E170A2E229025F607CB576417F0E23CC2D494DC973ACD727E2A59`。两套 XML 与日志均在 E 盘；不要把第一次磁盘满造成的失败运行计作代码结论。
- 统一工具合同：`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/tests/G2TestRunner.Tests.ps1`，native exit 0，30 assertions；`build/g2-runner-test-568fb551c035461aaf717cc17088c4a3/test-summary.json` SHA256 `94F629AC18365827BD689C1EF0BC3B7C888C38F78B180BCF1597908FF77A29BD`，分类仅为工具/fixture 证据。

### D-1-01 — MAJOR — G8 全覆盖缺口（更正上一轮误诊）

- **位置：** 普通 Windows Player 菜单、布局、键鼠输入及 G8-01/02 绑定。
- **复现：** `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File build/review-r2-a198409d/ui/capture.ps1 -OutputRoot <新独占目录> -Player <本轮 fresh Player 绝对路径>`；复核实际成功时给采集脚本独占 `TEMP/TMP`，采集线程 DPI context 为 `-4`，以 `GetClientRect/ClientToScreen` 和真实 client 像素坐标采集。context `-1` 得 client 512×288/window 525×323，context `-4` 同一窗口同一时点得 client 1280×720/window 1312×808。
- **实际 vs 期望：** `build/review-r2-a198409d/ui2/client-dpi-aware.png` 完整显示基线 Menu、标题和两按钮，目视通过；对应 Player.log 无 missing-script、InputModule 或 NRE，进程正常关闭 exit 0。上一轮把 DPI-unaware 采集造成的 512×288 局部图归为产品裁切，现撤回该根因和产品 FAIL。仍未覆盖支持比例的桌面/牌桌/结算全帧，也没有真实鼠标/键盘全流程输入→请求→权威回执，因此 G8 仅 PARTIAL。
- **证据：** PNG SHA256 `301D0C5801995FE032D3E148935A6F9DF4BA335E02A21847145FB1C35AABC4EF`；capture.log SHA256 `1B6ED3F18D7CA21B2F76FB7B01CC42764F3A9B882BFD32DBFA4640437B2C5C61`；player.log SHA256 `8536B04EAC094AB162E51D5294FB1CD35CFB732E4913ADB0E54A551AD69BAFDB`。第一次共享 TEMP 下 Add-Type 失败发生在 Player 启动前，原件保留，不计产品失败。
- **修复要求/验收断言：** 采集端全程固定相同 DPI context，记录 DPI、真实 client/window 像素与坐标变换；不修改生产 Canvas/DPI。新鲜普通 Player 的基线/宽/高支持比例完整帧目视通过，鼠标/键盘实际完成菜单→房间→对局→结算/弹窗；一次输入至多一次请求，焦点/取消/旧输入零错误提交，绑定包 hash、状态、请求及回执。当前未完成。

### D-1-02 — MAJOR — 固定 32 项执行覆盖

- **位置：** Binding 的 G1-01..04、G2-01..04、G4-01..03、G7-02；WindowsActions 真实 PlayMode/Player 前置。
- **复现：** 当前 clean HEAD Actions：`tools/unity/run-p45-c04-player-actions.ps1`（经 `build/508260b3991f44cea1c374e5c8688019` runner 启动）；查看 `failure.json` 和 `summary.json`。Release 前置首败后 runner 没进入 PlayMode/Player。
- **实际 vs 期望：** 首败已具体证明旧决策 ID 在控制权重开后复用并被接受。代码修复及 189 组件测试覆盖 stale-ID 负例，但本轮没有新的冻结候选 Player 执行；G1 和牌/荒牌/连庄、新 roundId 绑定，G2 碰吃立直/双立直/杠岭上抢杠及非法零变更，G4 旧 AI 拒绝/重入/显式 claim+epoch，以及 G7-02 实际 ITableChannel→TableSessionHandler 权威拒绝 LOCAL 与合法 server-derived 正例仍未补齐。
- **证据：** clean candidate 失败首件在 `E:/g2-r3-evidence-thread_20261108_190230_2b67/508260b3991f44cea1c374e5c8688019/failure.json` SHA256 `899060EA0DD486EC2A101E41D5B661B9AF8EE6C3E948A5C30D9F9342972B85E2`，同目录 `summary.json` SHA256 `B7782FDACAF7BC34CE0BD75057D62B3B6524EEA9C42A58CDB52782D05FB8AB26`；修复代码见 `LocalTableDriver.cs`、`C04RoomTicketTests.cs`、`LocalControlTests.cs`；本轮 Release/Debug XML 及首次败案各自封存。
- **修复要求/验收断言：** decisionId 每次新开必须唯一且旧绑定/旧 AI 决策拒绝；保留原 136 实体、deadline、终态、账本/幂等。取得合法 clean checkpoint 后重跑 Actions 真实四案、独立 PlayMode 和普通 Player 七案/P2，并补完上述 census 全断言，0 skip，绑定实际状态/请求/回执/截图与原件 hash。尚未关闭。

### D-1-04 — MAJOR — G7-01 离线证明

- **位置：** Binding C04-G7-01 及 FullMatch 的离线结论。
- **复现：** 对照原 `build/4091659e35504e70849026b37de28d0c/command.json`、driver summary 与原 assertion；现 binding 已把误 PASS 改为 NOT_RUN。
- **实际 vs 期望：** 无远端 service 进程和完成 12 局不证明网络隔离或没有出站连接尝试。本轮没有受控离线环境、channel 类型及进程 egress 观察。
- **修复要求/验收断言：** 在不改全局网络/安全设置、不影响其他会话前提下，以安全受控的真实离线环境/合法等价环境用同候选普通 Player 从菜单完成本地整场；记录进程、local transport、环境和无远端尝试观察，并绑定比分/排名/账本/终态。当前保持 NOT_RUN。

### D-1-05 — MAJOR — 同候选同包输入闭包

- **位置：** Binding `candidate/packages/runs`、最终 Player、BuildReport、资源/插件/8 DLL 输入闭包。
- **复现：** 对照 FullMatch/Cheats/Recovery/AppMatrix 各自 package manifests；相同 `Mahjong.exe` hash 不等于相同完整包。
- **实际 vs 期望：** 当前各套件仍是独立包，Round 3 未构建 32 项共同最终包；本轮组件 DLL/源码有改动，所以旧候选证据不能代表最终候选。期望同一冻结候选和同一完整 Player 对所有必需真实行提供文件闭包及 BuildReport。
- **修复要求/验收断言：** 所有已知实现修复与组件正负例完成后经桥接合法冻结候选；严格遵守各 runner fresh-build/clean guard，重新绑定 8 DLL、全资源/插件、BuildReport 和每项实际运行原件/hash。不能拼套件包、跳过 fresh、刷新摘要或放宽 verifier。当前未关闭。

### D-2-01 — MAJOR — Windows 原子替换锁竞争

- **位置：** `LocalTableStore.Persist` 的 `File.Replace` 失败分支、`LocalFileSystem.OpenRead`；对应 `TerminalRoundSurvivesColdRestart`。
- **复现：** 独立当前 HEAD 命令见 `build/review-r2-a198409d/Release-test.log` 与 `Release.xml`；本轮实际负例为 `LocalTableStoreTests.AtomicReplaceRetriesOnceAfterARealTransientBackupLock` 和 `PersistentBackupLockLeavesOldEnvelopeAndOutcomeUnknown`。前者用 Windows 原生真实文件句柄锁定 `.prev`，观测 HRESULT 0x80070497 后释放锁；后者持续锁定所有尝试。
- **实际 vs 期望：** 原首败整局写盘得到 COMMIT_OUTCOME_UNKNOWN 并失败；Win32 1175 确认为无法删除被替换目标。期望对单次瞬时可恢复竞争保留原子提交并完成终态冷恢复；若持续占用则保持旧 envelope/unknown，不冒报成功、不破坏原子性。
- **修复映射：** `LocalFileSystem.OpenRead` 允许 read/delete share；`LocalTableStore.cs` 只对精确 1175/HRESULT 0x80070497 有界退避最多四次，并在重试前核验主档、临时文件与目标提交字节。新增 transient 正例与 persistent-lock 负例；恢复后的 decisionId 缺陷另由 D-1-02 同批修复。
- **复验命令/结果：** 当前源码 Release/Debug build 均 exit 0、0 warning/error；完整 LocalHost 每套 189/189，0 skip/not-run，XML SHA 分别为 `675F3A84B33F8BCF09A33626EFF1EF149D81B195072076AF3E2FDBBA99A8A697`、`657BAAD1DAB7674F15DB2E4C5FB577149B65F287D5F96F908E84606A9BEF6480`。包括两条锁竞争用例和 `TerminalRoundSurvivesColdRestart`。冻结 Actions 候选前置仍需复验，故缺陷实现/组件级通过、任务验收仍开放。

### 本轮范围结论与接续检查点

本轮逐 ID 状态：D-1-01 为 MAJOR、G8 仅 PARTIAL；D-1-02 OPEN；D-1-04 OPEN/NOT_RUN；D-1-05 OPEN；D-2-01 已完成根因修复并通过当前源码 Release/Debug 组件层，但待冻结候选 Actions 复验。Binding 当前 Windows 计数 18 PASS、6 PARTIAL、0 FAIL、8 NOT_RUN；Android 九行继续排除并为 NOT_RUN。没有普通 Player 最终同包 32 项证据，不 approve、不签署 G2，不改变 V4 地位。

桥接在本轮开始时已提供干净 `7f394249...`，故先实际运行 Actions，而没有以“无 checkpoint”跳过可运行前置。源码修复后工作树 dirty。当前工具面没有可调用的 bridge checkpoint/freeze 操作；不得手工提交或藏起输入。下一可执行检查点是桥接按既有线程生命周期为本 dirty 修复生成合法 clean candidate；生成后首先重跑 WindowsActions，若前置通过继续 PlayMode/Player 与同包矩阵。真实离线环境需单独确认安全可用；不得将此资源/输入尚未取得写成 PASS。
# 第4轮返修与完整缺陷状态（2026-11-08）

## 本轮结论

本任务范围仍未通过，未签署 G2、未提交/推送/合并。桥接当前候选起点为 `cf477a7497c8c016e540917dab24f7a2d4318c46`（父 `7f3942490eab8000e731f94049b48c10850a2257`，merge-base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`）。D-3 修复使工作树 dirty；当前任何 WindowsActions 旧包或 R3 分套件包都不是修复后最终同包矩阵。

## 首次失败、修复与复验

| ID | 严重级别 | 位置 | 首次实际结果/证据 | 本轮修复与映射 | 本轮复验和剩余验收 |
|---|---|---|---|---|---|
| D-3-01 | MAJOR | `dotnet/src/Mahjong.LocalHost/LocalTableStore.cs` 的 `Persist` 有界 backup replace 重试边界；`LocalTableStoreTests.cs` | R3 确定性注入原件 `E:\g2-review-r3-8303bbb83a284eb88cb92a7f554eac4e\probe.log`（SHA256 `5934C4CB7BC06CEF44833F1758CF5C97DA834F96FE59B358DBA1182EC8578FB4`）：首次 0x80070497 替换前失败时 token 已取消，但再试一次且提交。 | 每次下一次替换前检查取消；清理候选临时文件并返回 Aborted，让现有 `CompleteWrite` 负责 Unknown/fencing；候选字节已显示线性化提交时仍返回 Committed。新增 `CancellationAfterTransientReplaceFailureDoesNotRetryOrChangeEitherEnvelope` 与 `CancellationAfterAtomicReplaceErrorStillInstallsCommittedFactsOnce`。 | 定向4案 exit 0、4/4（两条新案+`AtomicReplaceRetriesOnceAfterARealTransientBackupLock`+持续锁案）；完整 Release/Debug 各 exit 0、191/191、0 skip。修复后独立探针 `E:\g2-r4-D3-probe-after.log`：tokenCancelled=True、replaceAttempts=1、verdict=Aborted、primaryChanged=False。完整 hash 列见下。 |
| D-1-02 | MAJOR | G1/G2/G4/G7-02 真实场景及 WindowsActions | R3 Actions 先前 187/186/1/0，未启动 Player；失败原件路径与 hash 保留于前轮。 | 首先在 clean `cf477...` 上运行 Actions，没有先改源码。冻结归档/Release 前置/脚本正负例/PlayMode/Player 全部实际到达。 | 四个 PlayMode 正例与四个普通 Player 动作正例 PASS；G2 census 的非法负例、双立直事实、抢杠分支尚未覆盖；G1、G4 玩家场景和 G7-02 服务入口仍缺，故相应行 PARTIAL/NOT_RUN。本任务尚未完成。不得以当前 dirty DLL 重跑要求 clean 的 runner。 |
| D-1-01 | MAJOR | G8-01/02 | R2 生产 UI 裁切/缺脚本判断已按 DPI-aware 原件纠正；不重开生产 UI 缩放缺陷。 | 无生产 UI 修改。保留正确 DPI context 的采集方法与旧原件。 | Actions 普通包仅有四个动作案例，没有覆盖菜单→完整对局→结算、多比例完整帧及真实鼠标/键盘输入绑定。G8 仍 PARTIAL；须最终包完整复验。 |
| D-1-04 | MAJOR | G7-01 | 旧 FullMatch“无远端服务进程”证据不足；本轮无真实网络隔离/进程出站观察。 | 无过度结论或改全局网络。 | G7-01 非 PASS。仍须在安全受控离线环境绑定本地 transport、进程身份/出站观察、Player 同包与完整结算。 |
| D-1-05 | MAJOR | 最终 candidate/package/runs 闭包 | 以前套件各自 fresh 构建，不能合并成一个 Player。 | 无摘要刷新或拼包。Actions clean 包只保留为其起点 `cf477...` 的支持证据。 | D-3 修改 LocalHost 输入后，尚未经桥接冻结的新候选构建；无 32 行同一最终包完整文件/8 DLL/资源插件/BuildReport 闭包。该项 OPEN。 |

## 第4轮真实命令与封存证据

WindowsActions 在修改 D-3 源码之前，候选 `cf477a7497c8c016e540917dab24f7a2d4318c46`、源码 clean。原命令为：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File R:\thread_20261108_190230_2b67\tools\unity\run-p45-c04-player-actions.ps1 -RunRoot R:\thread_20261108_190230_2b67\build\4f8760efef9c45d0b585c9c2fbfb5ee8 -UnityPath D:\game_dev_tools\unity\Editor\Unity.exe -DotnetPath D:\game_dev_tools\dotnet\dotnet.exe -NugetSeedRoot C:\Users\sncdc\.nuget\packages -NugetSeedAuditPath R:\thread_20261108_190230_2b67\build\g2-r3-nuget-seed-audit.json -ExpectedCandidate cf477a7497c8c016e540917dab24f7a2d4318c46 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30 -TimeoutSeconds 1800
```

结果：归档/抽取通过；LocalHost 与 TableRuntime 前置 exit 0；Unity PlayMode XML 原生 exit 0，4/4、0 skip、0 inconclusive，`build/4f8760efef9c45d0b585c9c2fbfb5ee8/logs/playmode.xml` SHA256 `5ADD627B9CC3AE6BFD775D2E25B2627EC0126FE0308B7BDF4BE4C3CCE5DBBC30`；普通 Player 构建 exit 0；Pon/Chi/Riichi/ClosedKan 四个 Player 进程及 authority verifier 各 exit 0。Runner summary PASS，PlayMode 实际计数 4；封存 `summary.json` SHA256 `14B212BA80C1070172E5B84863059EB58BE9F551D6F96F5679DF3D69EBC9E526`、`manifest.json` SHA256 `6DD9FF192AB675D86D69F2C0AD56CA3B831A3D44549DB8012B5B891660CFCA04`、`player-package.json` SHA256 `2E8A5CAA16F68C89102BA21ADA4FDCDCD83E8623DA50498E0DB0CF3187688629`。四案逐行原始 JSONL、摘要、execution、Player log 及 hash 由 summary 绑定。该 run 不覆盖 Census 全部非法分支，也不属于 D-3 修复后的最终候选。

证据目录原稳定路径仍是 `build/4f8760efef9c45d0b585c9c2fbfb5ee8`（junction），完整副本在 `E:\g2-r4-actions`。共 4,267 个文件、1,093,417,431 字节；迁移前后逐文件 SHA256 一致。独立文件 SHA256 清单 `E:\g2-r4-actions-files.sha256.json` SHA256 `91B14CD9BD29B8CF232B1B1E04095BC682F255A9ED4510EA4C1BA66C93E68069`。因长路径跨卷 Move 失败的残余未丢弃：37 个文件留在 `build/4f8760efef9c45d0b585c9c2fbfb5ee8-source-remnant-r4`，4,230 个在 `E:\g2-r4-actions-source-original`；完整封存证据只从稳定 junction / E 盘校验副本读取。C 盘释放后约 1.09 GB 可用，E 盘约 247 GB。

D-3 定向命令（Release DLL 经 `build/r4-release-link` junction 回到本仓库，避免 `P41Harness` 在 E 盘路径找不到仓库根）：

```powershell
D:\game_dev_tools\dotnet\dotnet.exe exec build\r4-release-link\bin\Mahjong.LocalHost.Tests\release\Mahjong.LocalHost.Tests.dll -noLogo -automated -failSkips -method Mahjong.LocalHost.Tests.LocalTableStoreTests.CancellationAfterTransientReplaceFailureDoesNotRetryOrChangeEitherEnvelope -method Mahjong.LocalHost.Tests.LocalTableStoreTests.CancellationAfterAtomicReplaceErrorStillInstallsCommittedFactsOnce -method Mahjong.LocalHost.Tests.LocalTableStoreTests.AtomicReplaceRetriesOnceAfterARealTransientBackupLock -method Mahjong.LocalHost.Tests.LocalTableStoreTests.PersistentBackupLockLeavesOldEnvelopeAndOutcomeUnknown -result-xml E:\g2-r4-D3-targeted2.xml
```

结果 native exit 0，XML `4/4/0/0/0`；XML SHA256 `0A425E5CF83AEC0A05866727B1D06FE227D9B33C4C4671854E73024BA4C95D4B`，机器输出 SHA256 `43DE09ECB5ED91D518871D092AE8B155A955D0952257D2299D7180994FC160F9`。取消探针对修复后 DLL 的独立复测命令为 `D:\game_dev_tools\dotnet\dotnet.exe exec build\review-r3-8303bbb8\probe-artifacts\bin\ReviewCancel\release\ReviewCancel.dll build\r4-release-link\bin\Mahjong.LocalHost.Tests\release\Mahjong.LocalHost.Tests.dll`，exit 0，原始输出 `E:\g2-r4-D3-probe-after.log` SHA256 `8D7BBFD1E2D3D84CBD370DFE822F9214AA235183C1D5930940E19390178D7744`。

完整组件命令：

```powershell
D:\game_dev_tools\dotnet\dotnet.exe build dotnet\tests\Mahjong.LocalHost.Tests\Mahjong.LocalHost.Tests.csproj -c Release --artifacts-path E:\g2-r4-release
D:\game_dev_tools\dotnet\dotnet.exe exec build\r4-release-link\bin\Mahjong.LocalHost.Tests\release\Mahjong.LocalHost.Tests.dll -noLogo -automated -failSkips -result-xml E:\g2-r4-Release.xml
D:\game_dev_tools\dotnet\dotnet.exe build dotnet\tests\Mahjong.LocalHost.Tests\Mahjong.LocalHost.Tests.csproj -c Debug --artifacts-path E:\g2-r4-debug --disable-build-servers -nodeReuse:false
D:\game_dev_tools\dotnet\dotnet.exe exec build\r4-debug-link\bin\Mahjong.LocalHost.Tests\debug\Mahjong.LocalHost.Tests.dll -noLogo -automated -failSkips -result-xml E:\g2-r4-Debug.xml
```

Release 与 Debug build/test 原生 exit 均为 0；Release build log SHA256 `331AE0292F04A59E5B830F58C48CF8C3E520D88EF4FB46F1D91CA463EFDCD5B5`（0 warnings/0 errors），两配置各 `191 total, 191 passed, 0 failed, 0 skipped, 0 not-run`。Release XML SHA256 `C0E29D5245C349C7768A7FFAA2F46D18F336304E6872F21277D46CB6D44CFF23`；Debug XML SHA256 `5CDC1A514ED5966CB22C6148109B333894736CD761CA2FE94A6FFF94F1D0B246`；Debug build log SHA256 `01EE0F3313F17E7D71622BEF9AB901161D1CD47B32788864B104F13EA5DA0BDE`；Debug test log SHA256 `9042836FC8B17014E1C7818DF29BE9C0B406D4804537B716BAC4D4A5D7DFC27D`。Release build 终端记载 0 warnings/0 errors、exit 0；Release XML 封存于 E 盘；Release test 的 stdout/stderr 保留在本轮工具执行记录中，未另存独立文本文件。结果仅是 LocalHost 组件证据，不是完整 G2。

### G7-02 服务侧支持证据（不等于 LOCAL 资产行关闭）

```powershell
D:\game_dev_tools\dotnet\dotnet.exe build dotnet\tests\Mahjong.TableService.Tests\Mahjong.TableService.Tests.csproj -c Release --artifacts-path E:\g2-r4-tableservice --disable-build-servers -nodeReuse:false
D:\game_dev_tools\dotnet\dotnet.exe exec E:\g2-r4-tableservice\bin\Mahjong.TableService.Tests\release\Mahjong.TableService.Tests.dll -noLogo -automated -failSkips -method Mahjong.TableService.Tests.B2TerminalSettlementTests.DealerTsumo_GoesThroughRealMapperActorAndRoundEnd_WithResultAndNoClientSettlementBody -method Mahjong.TableService.Tests.B2TerminalSettlementTests.RawSettlementBearingEnvelope_IsRejectedBeforeAnyScoring -result-xml E:\g2-r4-TableService-focused.xml
```

构建 exit 0、0 warnings/errors；两个真实 Kestrel/WebSocket 服务用例 exit 0，XML 2/2、0 skip：server-derived Dealer Tsumo 正例，以及 raw settlement-bearing envelope 经 TableSessionHandler 拒绝的负例。Build log SHA256 `2B7CD6093B6AA28F20B9ECDAFDB09AAB78DB1C3FC30CDD0A34F4761B8C4DA166`；XML SHA256 `FCE1CCFC7876DDE14F9151AD9F4C489E3391550BF787049A0A5C3CF8801A794A`；test log SHA256 `0789A1A66815A6B7B1329529D3111E2FA68FDA9A6E74C25DB639EF0970008C26`。这是 G7-02 的权威服务正负侧支持证据，不是用户指定的 LOCAL 资产请求，也没有证明拒绝前后账本/outbox/版本零增量，因此 binding 只记 `PARTIAL`。

## 缺陷卡（本轮全部五组）

| ID / 严重级别 / 位置 | 复现命令或脚本 | 实际 vs 期望 | 证据路径 | 修复要求 | 验收断言 |
|---|---|---|---|---|---|
| D-3-01 / MAJOR / `LocalTableStore.Persist` 重试循环及 `LocalTableStoreTests.cs` | 原复现：`D:/game_dev_tools/dotnet/dotnet.exe exec build/review-r3-8303bbb8/probe-artifacts/bin/ReviewCancel/release/ReviewCancel.dll build/review-r3-8303bbb8/Release/bin/Mahjong.LocalHost.Tests/release/Mahjong.LocalHost.Tests.dll`；探针在首次 replace 前取消并注入 0x80070497。 | 原实际 token 已取消仍第二次 replace，Primary 改变并 Committed；期望未线性化取消后不得再替换并走安全 Unknown/fencing；已线性化的提交仍 Committed。 | 原首败 `E:\g2-review-r3-8303bbb83a284eb88cb92a7f554eac4e\probe.log` SHA `5934C4CB7BC06CEF44833F1758CF5C97DA834F96FE59B358DBA1182EC8578FB4`；修复后 `E:\g2-r4-D3-probe-after.log` SHA `8D7BBFD1E2D3D84CBD370DFE822F9214AA235183C1D5930940E19390178D7744`。 | 每次新替换前检查 cancellation；预提交取消清除临时文件并经 `CompleteWrite` fencing，先核候选字节以保护已提交结果；保留精确 HRESULT、有界次数、退避与原子替换。 | 定向四案及完整 Release/Debug 套件实际非零计数通过；取消前置负例 `replaceAttempts=1/primaryChanged=False/OutcomeUnknown`；提交后异常正例 Committed 且单次；瞬时/持续锁原案通过。 |
| D-1-02 / MAJOR / `WindowsActions` 及 G1/G2/G4/G7-02 census 覆盖 | `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 ... -ExpectedCandidate cf477a7497c8c016e540917dab24f7a2d4318c46 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30 -TimeoutSeconds 1800`；本轮完整参数见上文。 | R3 clean Actions 首败 187 项中 186 pass/1 fail 且未启动 Player；本轮 clean Actions 四个 Player 正例通过，但 census 负例、G1 和牌/荒牌连庄、G4 旧 AI/claim/reentry、G7-02 服务 LOCAL 负例仍缺。期望每条原 assertion/binding 有真实端到端证据。 | R3 首败 `E:/g2-r3-evidence-thread_20261108_190230_2b67/508260b3991f44cea1c374e5c8688019/failure.json` SHA `899060EA0DD486EC2A101E41D5B661B9AF8EE6C3E948A5C30D9F9342972B85E2`；R4 Actions `build/4f8760efef9c45d0b585c9c2fbfb5ee8/summary.json` 及逐案 evidence。 | 将缺失场景补为最小真实 Player/服务自动化；保留 136、scope/epoch/deadline/幂等/终态断言，非法动作零变更。 | 四个 Player 正例仍绑定；每个 Census 负例/业务结果/状态/请求/回执逐条有原件；G1/G4/G7-02 缺项未关闭前不能标全行 PASS。 |
| D-1-01 / MAJOR / G8-01/02 与 DPI-aware UI capture | `powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File build/review-r2-a198409d/ui/capture.ps1 -OutputRoot <new-exclusive-root> -Player <fresh-Player-absolute-path>`；同一线程 DPI context 用 `-4` 验证 client 实像素。 | 之前“产品 UI 裁切/缺脚本”已被正确 DPI 采集撤回；当前仅基线菜单通过，支持布局/对局/结算完整帧及真实键鼠还未完成。期望保持生产 UI 原样并完成完整 G8。 | `build/review-r2-a198409d/ui2/client-dpi-aware.png`、`capture.log`、`player.log`（R2 修正原件）；当前 Actions 包不是 D-3 修复后最终包。 | 采集测量/capture/input 使用同一 DPI context、绝对 Player `-logFile`、独占 smoke-data；不改生产 Canvas。 | 多比例菜单/牌桌/结算全帧目视；鼠标键盘可达完整关键流程，一输入最多一个合法请求，取消/焦点/旧输入不提交，并绑定包/状态/请求/回执。 |
| D-1-04 / MAJOR / C04-G7-01 真实离线 | 旧复现入口 `tools/unity/run-p45-full-match.ps1`（真实命令参数保存在 `build/g2-win-fullmatch-2e85604a/command.json`）；当前环境预检见 `E:\g2-r4-offline-preflight.txt`。 | 旧证据仅“不运行远端服务进程”，没有隔离或出站观察；本轮预检发现 WLAN、Meta Tunnel Up，无 Sandbox，防火墙默认出站均 NotConfigured。期望同一普通 Player 在真实隔离中完成一场且无远端尝试。 | 旧 `command.json`/FullMatch summary（不能作离线证明）；本轮预检 SHA `6E7C9555727E4092C5FB33529ABC2AE960724C0C61E556C35E76C76DF51DB340`。 | 不改全局网络/安全配置；需既有安全隔离环境，保留真实本地 transport 和进程出站观察。 | 同候选 Player 从菜单整场到终态，绑定隔离环境、进程、channel、无远端尝试、得分/排名/账本与包 hash；当前仍 NOT_RUN。 |
| D-1-05 / MAJOR / final candidate 与 `packages/runs` 全包输入闭包 | 对现存各 suite 的 `summary.json`、`player-package.json`/`player-files.json`、`evidence/artifacts.json` 与 candidate/input closure 做 SHA 和路径对照；各 runner 单独 fresh 构建命令列于 task card。 | 旧套件分别生成不同包；即便 `Mahjong.exe` hash 一样，也不能证明场景/资源/插件/8 DLL/BuildReport 一致。D-3 源修改后的最终包尚未构建。期望 32 行绑定同一冻结候选/同一全包。 | 当前 R4 Actions `manifest.json`/`player-package.json` 仅覆盖 clean `cf477...` 起点；R3 分套件 artifacts 与本轮 D3 XML 各自保留。 | 已知改动后经桥接合法冻结候选，遵守 fresh-build/clean guard；不拼包、不 skip、不改 verifier。 | 最终 32 行同候选、同 Player 完整清单，8 DLL、资源/插件、BuildReport、source/generated inputs 与逐行原件 hash 一致；Android 9 仍 NOT_RUN。 |

## 复核人要求的统一负例矩阵及验收映射

| 缺陷组 | 必须负例/边界 | 修复验收 |
|---|---|---|
| D-3-01 原子保存 | 第一次精确 0x80070497 替换前失败并取消；替换已发生后报告异常并取消；瞬时锁与持续锁。 | 预提交只尝试一次、primary/backup 不变且进入既有 Unknown/fencing；已线性化仍 Committed 且单副作用；既有重试数和字节校验不变。当前定向4案及完整 LocalHost 两配置已通过。 |
| D-1-02 玩家/服务覆盖 | Pon/Chi/Riichi/Kan 非法或陈旧动作零变更；双立直条件事实；抢杠窗口；旧 AI decision 被废止；重入请求对账；显式 claim 增 epoch；LOCAL 资产经实际 `ITableChannel → TableSessionHandler` 被拒，版本/事件/账本/outbox 零增量，并有合法 server-derived 正例。 | 每个 census 原断言以真实 PlayMode/Player 或真实服务入口和原始请求/状态/回执证明。当前动作四案只有正向部分；余项仍 OPEN。 |
| D-1-01 G8 UI | 基线/宽/高支持比例菜单、牌桌、结算完整画面；取消/焦点/旧输入不得提交；重复输入至多一次请求。 | DPI-aware 同上下文测量；绝对 Player log；截图语义人工检查与输入—请求—回执绑定。仅菜单基线此前通过，其余未完成。 |
| D-1-04 离线 | 同一 Player 在隔离网络中完成完整本地对局；无远端 channel/egress。 | 记录隔离环境、进程身份、transport、出站观察、结算分数/账本/终态。当前未执行，NOT_RUN。 |
| D-1-05 同包闭包 | 每行必须与相同冻结候选及完整包 manifest 对应；不同 suite 包不得混配。 | 8 DLL、场景/资源/插件、BuildReport、完整文件 SHA 与逐行原件绑定；修复后最终矩阵尚未构建。 |

当前可确认的根因/修复仅 D-3-01；其余四组不宣称修复。最早 Actions 失败、R1/R2/R3 首败、历史错误 DPI 判断与后续更正均保留，不覆盖原件。当前无需等待兄弟任务；应由桥接在源码修复批次完成后产生下一干净检查点，再继续要求 clean 的 Player/fresh 全矩阵。

G7-01 安全环境预检原件：`E:\g2-r4-offline-preflight.txt` SHA256 `6E7C9555727E4092C5FB33529ABC2AE960724C0C61E556C35E76C76DF51DB340`。检查发现物理 WLAN 与 Meta Tunnel 均为 Up，防火墙 profiles 虽启用但出站默认策略均为 `NotConfigured`，当前没有 Windows Sandbox 可执行入口。建立仅针对 Player 的阻断需要添加主机防火墙规则，违反“不改全局机器网络/安全配置”的执行约束，因此未声称本次离线场景通过，也未改机器设置；其他 G1/G4/G7-02/G8 工作仍可继续。
