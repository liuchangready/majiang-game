# G2-C05 Android 模拟器优先补验任务（DSH）

日期：2026-11-03，Asia/Shanghai。用户明确要求将“C05 Android补验：核定设备环境，准备完整游戏构建、画面和输入验证”通过bridge下发DSH，并补充“先使用android虚拟机”。本卡采用最新模拟器优先指令，不能只等待真机。

## 身份、候选与并行边界

- 执行器deepseek_harness，使用其配置默认模型（查询为deepseek/deepseek-v4.1-flash），不擅自切换。当前调用方/专属reviewer独立验收。
- 工作树E:/majiang-review/g2-c05-android-validation-ds-20261103；分支wt/v5-g2-c05-android-validation-ds-20261103。
- 起始候选fresh核定 **2c8238cd29386ab52994c8e3839dfdb85059660c**，现有唯一OPEN/Draft PR20，origin/v5仍8fd7c6cb。本树从候选直接创建，caller先仅加本卡，交付clean起点；之后真正受测源码/构建/文档身份分别记录。
- 派发前ledger226项，仅D05最终验证thread_20261103_151553_d142为queued，占Luna1/1；DSH0/2，本任务占DSH一个槽，总至多2/3。与Luna11个expectedFiles无交集。
- C05旧工具及环境准备两任务已组件验收，不重开它们。旧完整ARM64 APK只绑定旧源码，旧无设备快照不能当今天结果。D05最终验证在独立树进行，不改它的文件或混用它的包；最终统一候选变化按实际Android输入影响重绑，不将本次2c候选冒称所有未来版本通过。
- 读取AGENTS、doc/LONG_VALIDATION_TASK_TEMPLATE.md、当前20轮复核协议，以及C05-ACCEPTANCE、ANDROID-CANDIDATE-READINESS/BINDING、ANDROID-GAME-DELIVERY原件。旧readiness cde5/UI15前状态只是历史；当前候选已含后续UI、控制和三绝技修复。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。

## 环境已知事实与任务授权

本caller仅做短只读发现，未启动/下载模拟器或构建：
- 现有SDK在C:/Users/sncdc/AppData/Local/Android/Sdk，Unity AndroidPlayer/SDK为现有绑定路径；pinned adb存在。
- 发现sdkmanager/avdmanager（含cmdline-tools/16.0及latest），未发现上述SDK中的emulator/emulator.exe、system-images，也未发现用户.android/avd中的AVD；ANDROID_AVD_HOME/ANDROID_SDK_ROOT/ANDROID_HOME未设置。接收方必须再次核定，不能直接写NO_DEVICE后停止。
- 旧接受的私有Android toolchain manifest仍可按历史报告显式使用：D:/majiang-wt/android-toolchain-ds-20261027/build/android-toolchain/private/toolchain.json，历史SHA E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934。先核验真实文件/辅助程序/哈希，不伪造默认manifest、不把本树默认缺文件当SDK完全不可用。
- 用户选择模拟器，本任务允许在**本任务独占E盘SDK/cache/AVD目录**用官方SDK管理工具准备必要emulator、platform-tools、一个匹配系统镜像和独立测试AVD；不修改共享SDK/现有AVD、用户系统环境或机器虚拟化配置。记录确切包ID、版本、下载来源/结果/空间、路径和现有许可状态；使用已有有效许可，不自动签署新条款、修改Unity许可或用户全局配置。若新许可、虚拟化硬条件或安装环境确实阻断，给最小准确依赖，继续工具/源码/APK等独立可做工作。
- 优先复用已安装兼容镜像/工具；确实缺失才在独占目录准备，不反复下载或升级整套共享SDK。JDK/SDK/NDK/CMake/IL2CPP工具先核路径、版本和进程可用性，Android镜像许可与Unity许可分别记录。只需一台隔离模拟器，不自动启动多实例。
- 模拟器运行权限是本项目完整测试APK安装/启动/停止、截图/视频、受控tap/swipe/back和后台恢复；不操作其他用户设备/app或杀共享adb daemon。仅自己的独立AVD可改变测试显示配置，并记录/还原；不改变机器Hyper-V/WHPX/BIOS或重启系统。

## ABI与构建合同（先确认，不能安装后才发现不匹配）

1. 先核模拟器宿主能力、可用Android系统镜像ABI和Unity支持的AndroidArchitecture/IL2CPP目标；不能预设ARM64-only APK必能在任意Windows模拟器运行。
2. 如果目标AVD需要x86_64等不同ABI，构建**明确标记的ANDROID_EMULATOR测试变体**，使用同一完整游戏场景、相同共享DLL和实际IL2CPP支持。配置变化只在独占staging，完整记录允许的精确变化、variant/ABI/源码/工具身份，不改源ProjectSettings/PackageManifest/asmdef/业务代码。
3. 原已接受C05FullGameAndroidBuild、build/run-p45-android-game及其ARM64-only门禁全部只读保持；不能让原工具接受错误ABI/模拟器APK或把模拟器变体标成ARM64真机包。新任务使用单独C05EmulatorAndroidBuild及新runner/测试，必要读取/复用已有机制但不降低严格BuildReport/源码/完整包门禁。
4. 若当前Unity/镜像组合不支持所需IL2CPP ABI，明确BLOCKED_SUPPORTED_ABI并给实际能力/错误证据，不静默改为Mono再声称原IL2CPP验收通过。可以继续其他独立准备，不伪造成功。
5. 先完成组件/工具实际正负例，再freeze最终源码/输入/共享8DLL，生成真实完整游戏APK：Boot/Menu/Lobby/Table/Spectator/Settlement六场景，不是Wire/AOT精简探针。记录IL2CPP、ABI、min/target SDK、包名/启动activity、APK长度/SHA/aapt2元数据、实际BuildReport零error/stepError/Exception、原生退出及完整inputs/provenance。
6. 安装/启动/截图/输入动作前验证这个APK和build evidence一致，记录目标serial与AVD/镜像身份及设备实际ABI。新变体和旧ARM64包交叉馈入必须明确拒绝，不能仅凭包名或adb exit0认为正确APK在运行。

## 精确tracked权限（7路径）

```text
tools/unity/build-c05-android-emulator.ps1
tools/unity/run-c05-android-emulator.ps1
tools/tests/C05AndroidEmulator.Tests.ps1
unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs
unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs.meta
doc/v5-重构/V5-G2-C05-ANDROID-EMULATOR-DELIVERY-20261103.md
doc/v5-重构/V5-G2-C05-ANDROID-EMULATOR-BINDING-20261103.json
```

本任务卡只读；新.meta独立GUID。Domain/Client/proto/LocalHost/actor、正常presentation流程、ProjectSettings/Packages/asmdef、旧C05工具/报告、D05当前11路径及其他任务文件均只读。需要额外tracked路径给最小可复现理由，不擅自扩功能；忽略build/<GUID>的阶段卡、脚本、截图/视频、native logs和SDK/AVD缓存可写。实际共享源文件不能因Android目标切换、导入、自动生成或失败被改写。

## 固定交付矩阵（逐行实际判定，不缩分母）

| 行 | 需取得的真实结果 |
| --- | --- |
| E1 环境与镜像 | Unity/AndroidPlayer/IL2CPP/JDK/SDK/NDK/CMake、pinned adb/emulator、system image/AVD/ABI/空间/许可/加速能力的实际有界探针与身份 |
| B1 完整游戏APK | 冻结源码/全部输入/8共享DLL/六生产场景/实际variant ABI、IL2CPP、SDK/package/activity、BuildReport/native/APK完整绑定；不是旧APK或AOT探针 |
| D1 模拟器启动 | 单台自有AVD启动成功、adb online且sys.boot_completed，serial/版本/ABI/分辨率/density/方向/renderer/加速实际记录；启动失败不计应用失败 |
| R1 应用启动 | 对验证过的APK安装启动，前台包身份、app真实可用、logcat/崩溃/ANR证据；adb start成功或进程存在不能代替游戏可用 |
| V1 有效画面 | 菜单、本地建房/进入对局、牌桌/手牌/动作区域和可达弹窗等实际截图/视频；字体清楚、无黑帧/严重遮挡、横屏及安全区/宽高比真实观察 |
| I1 输入 | 本项目目标app实际tap/swipe/back等触控路径，菜单导航、按钮/牌选择和至少一条合法本地动作的前后可见变化；记录坐标/事件/时间和截图/游戏反馈，adb exit0不能代替动作确认 |
| L1 生命周期与显示 | 自有AVD后台→恢复、返回/重进、可用横屏/显示配置的实际结果；不以单设备/模拟器推广其他机型与真机 |
| F1 封存与交接 | 进程/模拟器自有身份与正常关闭，稳定manifest/hash、所有实际行PASS/FAIL/BLOCKED/NOT_RUN、首失败/未覆盖/候选变化和原样接续命令 |

截图必须是模拟器真实截取的原始PNG/视频，不生成/编辑图片来证明结果。实际查看画面再判V1，不能只看文件存在、非黑像素或screencap native0；若执行模型不能视觉查看，交专属reviewer真实原件进行视觉复核，本项此前保持CAPTURED_REQUIRES_REVIEW。触控不能直接调用UI方法或写视图冒充native输入。

无模拟器/下载/许可/加速阻断时，不能只报一个BLOCKED就结束：完成可独立进行的工具机制测试、候选绑定、可用ABI的APK构建或精确build handoff、输入脚本和逐步验证方案；实际运行/画面/触控保持BLOCKED或NOT_RUN，不能据环境准备PASS宣称C05完成。若模拟器可用且所有前置满足，继续真实安装/启动/捕获/输入，不新增逐阶段人工等待。

## 分阶段、资源与时限

先写自己build/<GUID>的完整任务卡：实际命令/进入条件/断言/timeout/输出/取消/候选与固定分母。默认：
- A短环境与ABI探针每项120秒，adb每步30秒；sdkmanager下载/部署每阶段最多1800秒（可基于实际大小说明调整），30秒心跳和进度，失败不机械重下。
- B工具/脚本和真实native/参数/坏APK/错ABI/错包/错serial/offline/timeout/drain/封存等负例每组180秒，保留无损哨兵与初失败；工具fixture明确标注，不冒充设备。
- C冻结后完整Android IL2CPP冷构建可沿既有有限上限7200秒，提前写原因/阶段；监控新鲜stage日志，每30秒心跳。只有修因/输入变化/资源变化才重试，不把完整构建当字段调试循环。
- D自有AVD冷启动最多600秒，应用每步30–180秒、每个显示/输入场景明确有界；允许不同AVD软件渲染模式尝试须有诊断和变化理由，不盲启动多个进程。
- E所有自有writer/模拟器退出后封存，未知退出UNKNOWN，不伪装正常成功。

Unity/共享插件构建与Luna D05共同遵守 **Local\MahjongV5P45UnityValidation**；不能改锁名/绕锁/双重持锁。默认总等待600秒，30秒心跳；资源忙记录RESOURCE_BUSY checkpoint，不当产品bug、不强杀他人。E盘独占SDK/AVD/cache/TEMP/Library/staging/output/evidence；不用固定S:覆盖他人映射。启动后台helper用Hidden/可用-no-window模式，除非确需用户可见交互且已说明；可实际screencap的软件/硬件renderer选择要记录，不以-no-window黑画面当有效渲染。

仅验证身份可核的自有PID/start/后代、模拟器serial/AVD目录；正常退出后确认writer/drain停止再释放资源及稳定封存。不用kill-server、不停他人模拟器、不清现有AVD/应用数据，独占测试AVD的日志与残留按阶段保存。不修改机器许可/全局环境/共享SDK或用户数据。

## 并行、候选与最终结论

此任务可与D05的环境/组件阶段并行，昂贵Unity阶段互斥，不等待D05整轮完成才开始Android独立准备。最终D05若新增/修改与Android输入相关的runtime胶水，按实际closure影响接续同线程重绑最终候选，不擅自取在途文件；不将旧2c包标为未来最终头已测。

新工具/脚本/Editor适配先通过正负例后再冻结构建。旧C05环境/工具验收不因本次新环境缺失被否定，也不将其旧52MB APK/无设备结果当本轮受测身份。Android模拟器、Android真机、Windows、Editor、iOS结果分别记录；本次模拟器通过不等于ARM64真机或G2/G4全平台通过。

唯一新交付报告ANDROID-EMULATOR-DELIVERY，machine binding JSON同名新日期：包含设备/AVD/包/source/build/工具版本身份、固定八行逐项状态、全部原样命令/native/计数/XML/log/截图/视频/manifest SHA、实际所见画面和输入、首失败、未覆盖和最小后续依赖、code/docs提交与clean/diffcheck。代码和docs-only报告分开，SDK/AVD/APK/证据不进Git；旧报告与原件只读保留。

执行者不得再委派、approve、push、建改PR、merge v5/main、部署生产、改scheduler/complete.flag/旧bridge状态或他人会话。专属reviewer独立验收，准备/构建/模拟器/真机和全C05/G2结论分开；V4继续生产，G2–G5不自动放行。
