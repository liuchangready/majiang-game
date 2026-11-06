# Android APK 构建、模拟器启动与安装接续任务

日期：2026-11-06，Asia/Shanghai。执行者：Codex / gpt-6-luna（用户明确指定）。

## 目标与固定分母

只交付以下三个实际结果，不要求本任务完成游戏玩法、视觉、触控、生命周期或 G2 全阶段验收：

1. APK_BUILD：从冻结源码构建完整游戏 IL2CPP x86_64 APK；六生产场景、八共享 DLL、package/activity、ABI、BuildReport 的 errors/stepErrors/stepExceptions、源码/输入/插件 provenance 和 APK 大小/SHA256 均有实际证据。BuildReport 三项错误计数必须存在且为零。
2. EMULATOR_BOOT：本任务所有的模拟器实例实际 adb online、sys.boot_completed=1、ABI 匹配；保存 serial/port/AVD、PID/start ticks、启动日志与实际原生退出。旧模拟器/旧截图不计通过。
3. APK_INSTALL：将第1项同一个 APK 安装到第2项设备，实际 adb install 成功，pm path/dumpsys package 核定包名/版本/ABI，能拉取 base.apk 时逐字节 SHA256 对账；否则明确安装来源校验方法与限制。只做必要启动探测，不把完整游戏验收作为安装交付的前置。

三项逐一 PASS/FAIL/BLOCKED/NOT_RUN/UNKNOWN；工具夹具与提案通过不能顶替它们。前项通过后自动继续。成功后保留本任务模拟器运行供用户使用，记录连接/停止命令与所有权；这属于交付的运行资源，不当残留失败。不关闭用户或其他任务的模拟器，不卸载或清空用户应用数据。

## 已冻结的起点与继承

- 新树：E:/mj/c05-luna-20261106；分支 wt/v5-g2-android-apk-install-luna6-20261106。
- 从 V5-G2 标签的 59e8bd741385b5d7b39ccdbc47573dde552067d1 建立，正式运行前读取并冻结实际 HEAD。新里程碑不移动。
- 旧 Android 树 D:/majiang/build/bridge-dispatch-20261104/android，旧 HEAD 4a855c3c49e65ec97708784d84a784df9f11c199，旧 thread_20261104_181451_ad90 第18轮已停止、review_failed；仅作只读证据源，不恢复/改路由/改原树或历史报告。
- 59e 与 4a 的 C05 builder/runner/tests/Editor build入口、Packages/ProjectSettings 无已提交差异。新树已逐字节继承旧未提交 runner、tests 和两份历史报告。runner SHA256 F71A35419324AADDDF99D721474A9262E2C1687265CA81AC6D6FEE1D19678DB3；tests 590B613B004F9CA365F23F72450D2000DAAB4651759942D2B3561FA7491A1A3E。
- 新源码候选的 .NET/应用输入按59e实际重新绑定；旧4a APK、旧成功或失败不能重标成新源码通过。

## 当前授权和文件范围

用户要求启动新任务继承当前知识并先完成三项实际目标。新任务授权执行必要的构建/模拟器/安装及最小修复，覆盖旧任务仅四文件且不能应用 builder 提案的历史限制；无需为已列出的常规可逆步骤再等确认。

允许修改本树：

- tools/unity/build-c05-android-emulator.ps1
- tools/unity/run-c05-android-emulator.ps1
- tools/tests/C05AndroidEmulator.Tests.ps1
- unity/Mahjong.Unity/Assets/Editor/C05EmulatorAndroidBuild.cs 及其 .meta（只限实际构建入口必要缺陷）
- doc/v5-重构/V5-G2-ANDROID-APK-INSTALL-TASK-20261106.md
- doc/v5-重构/V5-G2-ANDROID-APK-INSTALL-DELIVERY-20261106.md
- doc/v5-重构/V5-G2-ANDROID-APK-INSTALL-BINDING-20261106.json

允许在 fresh D:/c05s/<32hexGUID> 下写 staging/cache/生成文件，可创建 D:/c05s 容器，不复用或清理其他子目录；其他缓存/输出/日志在本任务 ignored build/<GUID> 独占根，先验最长路径预算。可只读核定并复制旧缓存至新stage，源缓存不变。不得绕 reparse/所有权检查。

不改业务规则、Domain/Client/Server运行时、Packages/ProjectSettings源文件、共享AVD、许可证、全局编码、系统分页文件、虚拟化/驱动/BIOS或其他机器配置；不安装/卸载工具。必要共享 DLL/PDB 只在owned stage以真实共享构建/正式插件检查生成，不为构建污染源树。只有证实超出上述范围才能给出具体最小依赖，不重复请求已授权的builder修复/短根/构建/安装。

## 必须继承的证据和已知缺陷

先读 D:/majiang/AGENTS.md、本树AGENTS.md、D:/majiang/doc/LONG_VALIDATION_TASK_TEMPLATE.md、D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md；当前同缺陷20轮规则适用。本卡就是长验证范围卡，实际运行前在ignored新根补命令、阶段、预算和进程记录。

以下相对路径均相对旧 Android 树，只读：

- build/reviewer-round18-b8af46834fac4f61a75ebbdf8c2863ed/D-R16-proposal-round18.md 及 D-R16-R17-proposal.patch。
- patch SHA256 99FB76667E106A6E6E8361E502E02CE8B0AACD78DF2F8889196FA9FFBF38E545；提案 SHA256 C1D7C878D889996B0B38973ECDACF96CB8541CCBF57932DEB79EE411386BE646。父任务已实际 apply --check --ignore-space-change 成功，尚未应用；由执行者在新树核hash后应用并验证。
- build/review18-a42138efb2d34e289c3909fa8ad56249/review.json：提案经过独立复核，UTF8与缓存库存缺陷在提案层关闭；129断言/18夹具为旧原型结果。真正19包复制/完整构建未执行。
- 可读缓存种子：build/c05-emulator-staging/round15-attempt7-ba433de489ef4f28b3b597e81c6ab356/project/Library/PackageCache。19包身份/锁已核，实际复制和普通路径读取仍须在新stage验证。不得执行旧失败缓存中的代码来假造新结果。
- 长路径已实证：268/283/307字符文件普通访问失败、扩展路径存在；29771文件中6192条>=260、最大337。参考 build/reviewer-round16-99e02738c61f4d70b7c44557a9316d7e/long-path-result.json。
- 失败原日志：build/g2-c05-android-round15-cd6532f88c0e4dd1b6b27e094aed24ed/B1-attempt7-ba433de489ef4f28b3b597e81c6ab356/unity.log。约12799行明确 compiler exit 0xC000012D、系统虚拟内存不足；10364/10369行 CoreCLR 0x80004005/0x8007000E。内存不足已发生，不能继续只说UNKNOWN或以小csc探针成功排除；谁消耗内存、完整峰值与分页文件容量关系尚未查清。完整构建前采样系统commit/available/pagefile与自有进程并发，优先通过本任务进程级编译并发限制等最小已验证方法控制峰值，不更改系统分页文件或关他人程序。
- 仍有 shadergraph/urtshader Host type is not matching any asset type 错误，不能无证据声称长路径解释全部。先排已证实原因再复验，保存首败。
- 旧许可证/Android模块缺失已被后续实际工具探测取代，不永久沿用历史BLOCKED。原误发Windows补丁、DLL恢复指令不属于Android，不继承。
- 两个stage-only资源差异 Font SDF / PerformanceTestRunInfo 必须保留并分类输入/输出；不能靠忽略差异或改包清单过关。旧12个fixture junction只读保留，不递归清理。

## 环境、阶段与边界

正确 Unity：D:/game_dev_tools/unity/Editor/Unity.exe，6000.3.27f1；.NET：D:/game_dev_tools/dotnet/dotnet.exe。显式传参数，不用脚本失效默认Unity路径。SDK/AVD优先核用户已有 D:/Android/Sdk 和 D:/Android/avd、medium_phone；D:/game_dev_tools/android-sdk/android-avd及C:/Users/sncdc/AppData/Local/Android/Sdk亦存在，只有实际核定后按子进程显式选取，不混用不同SDK的adb/server/emulator，不改全局环境。先读集中环境指南但它不授权重装。

父任务短预检（2026-11-06约15:05，仅快照）：16GiB物理内存、free约4.9GiB，系统virtual total约28GiB/free约7.7GiB；C:/pagefile.sys分配12547MiB、使用3619MiB、峰值4823MiB。必须运行前fresh复测，不能按旧快照认定现在资源足够或永久不足。Windows验证另在原线程运行，共享Unity必须串行。

阶段：A环境/路径/内存/AVD短预检 → B应用已审提案与受影响确定性组件 → C真实参数/metadata/缓存/输出/失败传播合同对齐 → D冻结当前HEAD和输入，构建APK、启动AVD、安装核验 → E稳定证据交付。无需重复无变化的2872项或旧129/18全部，只测实际受影响项和正式入口要求项。

共享锁 Local\MahjongV5P45UnityValidation，等待最多600秒，30秒进度心跳；不双重持锁。短probe120秒、adb30秒、AVD600秒（有实际进展可记录延至1200秒）、冷Unity/IL2CPP7200秒，分段exec后台会话可观察；长等待不阻塞沟通。没有资源变化不盲重试，不绕锁、不杀他人。正常取消且核PID+startTicks/path的自有writer后再封存，主错和清理错都保留。

使用medium_phone现有镜像，支持的read-only/no-snapshot-save或任务独占AVD副本/overlay；不wipe、不改共享配置、不重复下载镜像。真实accel-check/功能探测决定参数，旧无加速/软件QEMU崩溃不等于当前仍失败。后台helper默认Hidden；用户未要求前台窗口。

成功交付：稳定APK绝对路径/大小/SHA256、冻结源码/输入/插件与BuildReport绑定，设备serial/ABI/boot_completed、实际安装回执及pm信息、模拟器所有权和停止命令、复现命令/native/超时、全部失败与三项状态、manifest/hash。保持设备运行时把活跃日志放在明确未封存目录，不能封存仍在写的文件。

不继续委派、不恢复旧Android任务、不改Windows任务，不push/PR/merge/deploy、不移动V5-G2标签、不改scheduler/complete.flag。执行者可在本独立分支提交已授权修改以满足clean-source门禁；运行矩阵中不写任务文档造成dirty。独立reviewer只审本三项目标，不能因未做游戏八项全矩阵反复返修。

复核每轮一次给全全部已知缺陷，含ID/级别/位置/原样复现/actual与expected/证据/hash/最小修复/验收；同类合并，批修全部已知问题。不得skip/放宽断言/旧包重标或工具假绿，已有输入未变的成功不机械重复。
