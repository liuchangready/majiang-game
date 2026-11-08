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
