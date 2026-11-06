# G2 Windows / Android 快速构建优化与统一计时

日期：2026-11-07，Asia/Shanghai。用户要求交 Gemini 完成所有优化后统一测量，Codex 负责独立检查。执行者 Antigravity / 配置默认 Gemini，禁止继续委派。

## 起点、范围与交接

- 从本地 g2_base 的 66f31612f23baf9e00e8eb242d0259c5ec2c9d9e 创建独立树 E:/mj/g2-fast-20261107，分支 task/g2-fast-build-gemini-20261107。记录准备提交与最终真实被测 HEAD，不修改 g2_base 或 V5-G2 标签。
- 原只测量任务 thread_20261106_225849_5259 当前 paused。原 Windows thread_20261105_180011_21e1 为 review_failed；原树与证据只读，不恢复、不重置、不接管或删除原后台进程。paused 不保证其子进程都已停止，必须读实际进程/锁，避免重复构建。
- 本任务授权必要的构建工具、受影响检查器/测试、Editor 构建入口、Fast 构建配置、确证未使用的包/Shader 裁剪及文档改动。完成全部可便宜确认的实现与合同对齐后再冻结并统一执行昂贵计时；不得边修一个点边跑完整两端构建，也不得只交方案。
- 先读本树 AGENTS.md、D:/majiang/doc/LONG_VALIDATION_TASK_TEMPLATE.md、D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md。本卡给范围与阶段；在 ignored build/<GUID> 补实际命令、端口、唯一缓存/输出根、资源预算和取消方式。按 ponytail 最小改法复用现有 builder/verifier，不新增整套调度、缓存服务或重复校验系统。

## 必须一次完成的六组优化

1. **日常增量 / 正式验收分开。** 现有正式入口的来源、完整场景、BuildReport、零错误、失败传播及冷构建语义保留；显式 Fast 模式用于日常迭代，不能默默降低正式验收。摘要须声明 profile、后端、ABI、代码生成、压缩、缓存与是否实际编译。新增参数需真实存在且测试覆盖，文档命令可复制。
2. **平台独立稳定短缓存。** Windows64、Android x86_64 分别拥有持久 staging/cache，保留 Library/Bee、Shader/IL2CPP/Gradle 所需中间产物；平台不来回切换共享同一 Library。每轮独立日志/最终包根与封存，缓存不当不可变证据。当前 Android 外部短根禁止 ReuseStaging，此次授权最小修改以实现受控复用，不绕路径/owner/reparse/writer guard。source authored inputs 按冻结字节同步，并处理文件删除；不得只复制 PackageCache 就宣称完整构建缓存命中。
3. **共享 DLL 按实际输入复用。** 当前 build-shared.ps1 删除七项目 bin/obj 并构建整个 Mahjong.sln；Fast 输入不变时校验并复用已成功 provenance 的完整 DLL 依赖闭包，变化时重建受影响依赖，不让测试/服务端无关工程进入客户端关键路径。保留正式 forced rebuild 入口。先看 shared-plugin-provenance-lib.ps1/checker/Editor guard 的实际合同，声明真实 rebuild/reuse，不能将 reused 记录伪写 forcedRebuild=true。内容哈希而非仅mtime决定有效性；复制相同字节不无谓重写导入时间戳。
4. **Android 增量与 IL2CPP 构建配置。** Fast 不使用 CleanBuildCache，并真实保留可命中的 IL2CPP/Bee/Gradle 缓存；采用 Unity 6000.3 支持的 Optimize for code size and build time（API等价配置需核官方文档）。模拟器只x86_64，真机ARM64另配置，不能混为一个兼容缓存。Windows保持当前有效后端，只有实际使用IL2CPP才应用对应优化；不要将Windows shader慢误当成IL2CPP慢。可采用适合开发的LZ4等较快压缩，但配置差异与运行性能影响明确记录，不冒充同一正式性能包。
5. **资源与并行控制。** 当前16GiB RAM，曾有CoreCLR 0x8007000E、编译器exit0xC000012D和Windows Shader OOM。以可观察、适度且可配置的低并行控制Shader/import/job/native阶段，不能假设job-worker-count控制所有编译器。Windows/Android串行，在构建结束后才为本任务启动模拟器；不停止其他任务或用户已交付设备。系统commit/物理内存与自有树峰值均采样，资源不足准确BLOCKED，不靠无限重试。不得改系统pagefile/驱动/BIOS/许可/全局环境。
6. **确证未使用的包和 Shader 审计裁剪。** 审计当前manifest中的VisualScripting/Timeline/2D工具及实际Shader变体/AlwaysIncluded/URP功能，只裁剪依赖与资源引用证实未用者，保留游戏实际材质、中文字体、2D显示和测试所需包。用户已授权本范围最小改动，无需为常规Package/Graphics最小修复再申请历史Windows scope。不能整批合入未验收319cf555、通过关闭Burst/删除测试/删必要shader获取假速度。每个候选保留依赖/引用证据、实际变更或不适用理由；不适用不可虚报已优化。

Unity缓存兼容键含Editor版本、目标平台/ABI、后端、构建profile、锁文件与会影响缓存兼容性的配置。普通代码/资源变化应触发增量更新而不是每改一行都删除全部Library。共享DLL已完成产物的复用键则覆盖完整受支持MSBuild输入、配置、依赖和工具身份。混淆两种键会导致全量失效或旧DLL假命中。

## 修改路径边界

仅在本树修改：

- tools/unity/build-shared.ps1
- tools/unity/check-shared-plugins.ps1
- tools/unity/shared-plugin-provenance-lib.ps1
- tools/unity/test-shared-plugin-provenance.ps1
- tools/unity/build-c05-android-emulator.ps1
- tools/unity/run-p45-c04-privacy-matrix.ps1
- tools/unity/run-p45-c04-player-actions.ps1（仅与共享Fast/profile/缓存调用保持必要一致）
- tools/unity/run-c05-android-emulator.ps1（仅本任务启动/安装必要衔接）
- tools/tests/C05AndroidEmulator.Tests.ps1
- tools/tests/C04PrivacyRunner.Tests.ps1
- tools/tests/C04PrivacyPlayer.Tests.ps1
- tools/tests/C04PlayerActions.Tests.ps1
- 可新增 tools/tests/FastBuild.Tests.ps1、tools/benchmarks/g2-fast-build/ 下最小统一计时/采集与自检文件；不另造通用框架。
- unity/Mahjong.Unity/Assets/Editor/C04PrivacyBuild.cs、C05EmulatorAndroidBuild.cs、SharedPluginProvenanceGuard.cs 及必要同名meta。
- 可新增一个必要的 Editor Fast build helper 及meta，配置只在任务owned staging或明确Fast profile生效，正常运行不带诊断副作用。
- unity/Mahjong.Unity/Packages/manifest.json、packages-lock.json；ProjectSettings/GraphicsSettings.asset、ProjectSettings.asset；Assets/Settings/UniversalRP.asset、UniversalRenderPipelineGlobalSettings.asset（仅上述已审计Fast配置/未使用功能裁剪）。
- doc/v5-重构/G2-FAST-BUILD-TASK-20261107.md、G2-FAST-BUILD-REPORT-20261107.md、G2-FAST-BUILD-BINDING-20261107.json；doc/DEV_ENVIRONMENT_SETUP.md 的快速构建用法（若本树没有该文档可新增最小指南并链接实际入口）。

不修改dotnet/src业务算法、client/server协议规则、经济/136守恒/幂等/控制代次，既有业务测试不弱化。共享项目与消费方依赖/目标框架保持，不能为了少编译删模块。缓存/临时/最终产物只能在已核task-owned短根与本树ignored build根；不写源码Plugins、用户默认工程缓存、不改其他worktree。正式全局验证器和工作流不改。

## 先便宜验证，再统一计时

阶段A短预检：核本树HEAD/clean、真实工具/SDK/AVD/许可、系统commit/磁盘、原任务存活writer与Unity共享锁。只查环境不重装。阶段B/C批量完成上述六组改动、受影响组件和producer/checker合同，然后一次冻结候选。中途不写报告造成正式runner因dirty停止；可在独立分支提交已授权源码/卡，计时原件写ignored根，最终再补报告。

廉价验证至少覆盖：

- 内容变化但mtime不变不得命中旧DLL；错误输入/依赖/配置/Unity版本/平台/ABI/后端/包锁不得误命中。
- DLL损坏/缺失/新增冲突、provenance过期或不完整、前次失败/取消残留均拒绝或受控重建，成功复用的bytes/hash可核。
- 缓存有活动writer或跨task owner、路径越界、reparse、错误平台、cache schema不兼容、输出碰撞均拒绝；正式fresh模式和失败seal仍可用。
- no-op与真实C#改动的producer字段/计数/构建模式一致，不能跳过Unity直接复印旧EXE/APK称作no-op build。
- 条件profile参数只作用预期入口，正式六场景、ABI/package/activity、8DLL、BuildReport errors/stepErrors/stepExceptions不可缺项或放宽；实际两个平台产物都应可启动做必要smoke。

## 统一计时矩阵

仅在实现与廉价回归全部完成后，统一执行基线66f31612与优化冻结候选的对照。允许在本任务owned短路径从该固定基线建立只读源工作树/副本用于控制组，绝不改用户g2_base本体。之前只测量任务paused且未给出完整成功对照，不能恢复它或将不明退出/旧计时当本轮数据。

两版本 × Windows64/Android-x86_64 × 三场景，共12个观测格：

1. 首次构建：没有该平台工程导入/编译缓存；允许已有下载包、已核registry/NuGet种子和OS页缓存，逐项说明，不能把“冷”说成重新下载全部工具。
2. 无源码变化重建：紧接该版本首次成功包，执行其真实入口；基线本来强制清理也如实测该行为。优化版必须记录缓存层与实际执行了哪些阶段，不把它称完整重编。
3. 仅一处C#改动重建：控制组和优化组做相同、受控且确实进入Player程序集的诊断变化，保留输入前后字节/hash与实际编译证据；只在隔离受控输入中进行，不写用户基线/业务源码。注释变更或只改Editor脚本不能冒充Player脚本编译。

每格默认取一次实际成功或失败观测，不为挑最好数字反复运行；失败保留native与耗时，后继前置不满足填BLOCKED/NOT_RUN。只有证实修复原因或输入/资源变化才重试。最终代码修复后，只有被实际影响的格子重验，不机械重跑整张表。源码/编译选项不同的结果明确profile差异，不能用Fast较小场景/换ABI/关必要功能冒充同等工作量加速。

报告分开：锁等待、输入同步/哈希、共享DLL、Editor启动/导入、C#、Shader、IL2CPP/C++（Windows非该后端则N/A）、Gradle、打包签名、完整封存；保留端到端wall time与可得分阶段原件，嵌套或并行阶段不相加。记录可核cache hits/misses、CPU/线程或进程数、系统commit及自有树峰值。加速倍数只能在有可比成功基线时计算；历史Android1089.874秒是旧BuildReport片段，不是本轮端到端对照。基线失败则没有最短成功耗时，不虚算倍数。

每个平台对最终代表性包做必要启动smoke：Windows真实Player进程/入口、Android实际安装启动到生产入口（优先复用本任务有所有权的设备，避免强迫重启其他实例）；确认包身份/ABI/scene/8DLL/BuildReport均有效。裁剪有视觉风险的资源必须查看真实截图或提供直接可验证输入输出，不能以fixture替代。不要求整场/G2全矩阵作为本次速度优化验收，但不能宣称这些已通过。

## 工具、资源和执行边界

- Unity D:/game_dev_tools/unity/Editor/Unity.exe，6000.3.27f1；.NET D:/game_dev_tools/dotnet/dotnet.exe，8.0.426；SDK D:/Android/Sdk；AVD D:/Android/avd / medium_phone / API36 x86_64 2GiB，AEHD2.2。每子进程显式路径，不信旧脚本D:/Unity默认值。
- 私有CLI/Ninja只读种子：E:/mj/c05-luna-20261106/build/c05luna/aa82604ee7a547628cf981557e408f32/toolchain/private。registry种子：D:/c05s/aa82604ee7a547628cf981557e408f32/project/Library/PackageCache。复制到本任务独占根并绑定身份，不改源种子。已有19包真实元数据可复用，新的锁变化必须匹配相应库存，不能放宽校验。
- 短staging建议D:/c05s/<task-owned-GUID>，本任务可创建容器及自身新子树；第一次建立平台缓存后按此次实现受控复用，不改其他兄弟目录，不借junction/subst或驱动映射绕路径。
- 本任务Windows/Android串行；共享锁 Local\MahjongV5P45UnityValidation，最多等待600秒，30秒心跳。cache自身也要互斥writer，不双重持同一个Unity锁。
- 短probe120秒、组件/合同自检默认600秒、Windows每次构建3600秒、Android每次7200秒、模拟器boot600秒、安装300秒、启动smoke180秒；必要有实际进展的调整先说明。不能用一个同步工具调用阻塞整个长流程，使用后台session/有界轮询并报告真实进度。
- Windows进程操作需核绝对路径在task-owned根后原生PowerShell同shell执行；只处理自有PID+startTicks/path，优先正常取消，后台helper默认Hidden。不强杀他人进程，不改系统pagefile/虚拟化/许可/防病毒全局排除，不清空全局SDK/AVD/NuGet/UPM/历史证据。

## 交付与独立复核

交付逐项优化映射、完整代码diff与冻结commit、可复制命令、默认正式与Fast模式说明、受影响正负例/native/count、12格计时CSV/JSON和报告、首败、包/BuildReport/源码/输入/工具/缓存的hash绑定、实际查看过的smoke原件、可靠退出和稳定manifest。依次区分实现完成、测试通过、性能改善；不把未测优化写成已提速。只读未适用项有证据和理由，仍存在未修项如实OPEN，不能说“所有优化完成”。

开发者不得继续委派、不自行接受、不push/创建PR/merge/deploy、不移动基线或标签。由Codex逐项独立检查源码和原始结果，必要时定向复验；先给一份完整清单再批量返修，不用全部昂贵计时作为发现每一个脚本错误的方法。最终未获复核不合入g2_base，亦不代替G2整体签署。

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。
