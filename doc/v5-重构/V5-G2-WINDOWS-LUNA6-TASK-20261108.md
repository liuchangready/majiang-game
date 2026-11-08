# G2 Windows Luna6 执行任务卡（2026-11-08）

## 任务与身份

- 任务 ID：`thread_20261108_190230_2b67` / `G2-C06-PLATFORM-WIN-LUNA6-20261108`
- 目标：以当前 `g2_base` 为起点构建 V5 Unity Windows 完整游戏 Player，执行 G2 固定 Windows 32 项；按组保留首次失败、就地修复并复验，然后推进下一组。
- 执行模型：桥接默认 `gpt-6-luna`（按派发要求，不变更、不委派）。独立 reviewer 按 V5 复核协议复核；本执行者不签署 G2。
- 工作树：`C:\Users\sncdc\.agent-mesh-bridge\worktrees\thread_20261108_190230_2b67`
- 分支：`mesh/task/thread_20261108_190230_2b67`
- 实际起始 HEAD、与 `g2_base` 的 merge-base：`4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`，与派发基线一致；基线树 `D:/majiang-wt/g2_base` 此刻同 SHA，干净且本地领先远端 19（不拉取/不改写）。源主目录 `D:/majiang` 的未提交修改不作输入、不触碰。旧 Windows 工作树/线程/原件保留且不移植。
- 当前状态：初始任务工作树干净；任何测试/修复输入变化均须记录。所有真实证据写入本任务独占 `build/<GUID>/`，不得覆盖历史目录或移动拆散包闭包。

## 固定分母和真实断言

从 `V5-G2-C04-PLAYER-SCENARIO-CENSUS-20261101.json` 读取 33 项 C04，其中 Windows 分母固定 32；`C04-G8-03` 是 Android，Windows 不运行并标 `NOT_RUN`。另 8 个 C05 Android 平台行均 `NOT_RUN`，不计 Windows 通过。完整 Plan 应含 41 行；Windows 成功分母不得写成 41。

逐项采用原 Census 的 `trigger`、`initialFixture`、`assert`、`bindings`，不得更改历史原件或标准：

- G1（4）：菜单建本地房/ready/码牌/首回合；真实和牌及荒牌流局；连庄和新 `roundId`；整场终局与排名。须绑定权威状态、136 张、请求/回执/事件、比分账本与终态。
- G2（4）：碰、吃、立直（记录双立直事实及条件是否真实满足）、杠/岭上/抢杠边界，含非法负例与不变式。
- G3（6）：三绝技真实 UI 授权成功；精准一次费用与仅私有账本；全部 0..135 唯一实体守恒；未授权与余额不足各自独立拒绝且无状态变更。
- G4（3）：断线 AI 接管、旧 AI 决策拒绝；离开重入/快照/未决请求对账；真人显式收回与 epoch。另要求真实独立 PlayMode 和普通 Windows Player 七案/P2 证据。
- G5（3）：四席隐私、观战公开流、角色切换先清缓存且旧 binding 不再投递；绑定 session/scope/role/cursor/epoch。
- G6（8）：有效主档重启；只终止本任务自有 Player 的强制终止/冷启动；截断/坏 checksum/缺主档但备份有效；两档缺失；两档坏；不兼容存档拒绝；校验原请求对账/幂等。不得杀他人进程。
- G7（2）：真实离线本地完整整场；服务/结算实际权威入口拒绝 LOCAL 资产（客户端自拒绝、fixture/mock 不算）。
- G8（2 Windows）：最终真实画面与支持的分辨率/长宽比布局；鼠标/键盘合法输入关键流程。人工检查截图语义，黑帧、桌面/菜单停留、仅 exit 0 不算通过。

入口映射仅作取证起点：`WindowsFullMatch`、`WindowsActions`、`WindowsCheats`、`WindowsRecovery`、`WindowsPrivacy`、`WindowsAppMatrix`。套件 runner completed 不自动等于任何一行 PASS。无完整映射断言的 G1 分支、G7、G8 必须补充本任务内最小真实自动化或实际交互证据；不得静默漏测。

## 修改范围

允许改动：

- 初始预计：本任务卡、`doc/v5-重构/V5-G2-WINDOWS-LUNA6-DELIVERY-20261108.md`、本任务 binding/逐例台账 JSON、`tools/tests/G2TestRunner.Tests.ps1`（仅当本轮入口合同确有缺陷）、以及修复所需的 `tools/unity/`、`tools/tests/`、`unity/MahjongV5/`（以仓库实际路径为准）、相关 `dotnet/` 项目/测试。
- 发现根因后，先在任务卡“范围变更记录”精确补列文件和因果；只做满足合同的最小修复。
- 不允许改历史 Census、C06 原件、验收阈值、统一 verifier 以放行、无关架构、引入无关依赖；不写入源主目录。
- 绝不提交/推送/合并/发布；保留任务分支和所有证据，最终交给 reviewer。

## 已知状态/缺陷与保留边界

- `V5-2-REVIEW.md` 当前是 `PRE_REVIEW_COMPLETE` / `G2_NOT_ACCEPTED`。当前 Windows 包尚未被接受。
- 历史旧任务 `thread_20261105_180011_21e1` 为 `review_failed`，其 `319cf555` 不属于当前基线。保留旧首次失败/证据，只读借鉴；当前必须从本候选重新证实，不能改写旧结果。
- 已知限制：统一入口不覆盖所有 C04 行；七案需要独立 PlayMode 与普通 Player 两类证据；不同 suite 可能产出不同 Player，不能混拼同包全矩阵。`FU-02 UNKNOWN`、`SYNC-03` 等既有风险按原记录保留，不从历史通过倒推关闭。
- 当前具体可复现缺陷全集：WindowsFullMatch 在 `Write-SourceRecord` 遍历包含中文路径的未跟踪文件时，Git 默认八进制转义路径被 PowerShell 当作字面路径，触发 `ArgumentException: Illegal characters in path`，原件见 `build/g2-win-fullmatch-611a2c3f/`。修复为该只读路径枚举显式 `git -c core.quotepath=false`；保留业务 guard、原始路径/hash 证据和其它 Git 全局设置。廉价正例：显式 UTF-8 路径被 `Test-Path` 定位且可取 hash；负例：默认 quote 输出不可作为有效磁盘路径（复现旧问题）。实际修复回归及本套件重跑记录在 Delivery。

## 阶段、命令和门槛

| 阶段 | 命令/动作 | 进入下一阶段条件 | 有界时间 | 产物 |
|---|---|---|---|---|
| A 核定与预检 | `git` 身份核对；`powershell -NoProfile -ExecutionPolicy Bypass -File tools/tests/G2TestRunner.Tests.ps1`；`powershell -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-g2-tests.ps1 -Mode Plan -OutputRoot <新 build/GUID>`；检查全部 plan/census 合同、脚本 parser/interface、工具、缓存、许可可识别性、空间/锁/自有进程 | 32 Windows 行逐例映射完整；生产/验证契约一致；Player 构建依赖、许可和资源可用；已知缺陷批量关闭 | 工具回归 10 分钟；锁最多累计 600 秒 | 工具原始日志/summary、41行 plan/csv/hash、预检记录 |
| B 组件与合同 | 按修复批次执行对应 .NET/PowerShell/Unity PlayMode 真实组件正负例；检查 verifier 真实构建/绑定；检查非零、超时、取消与封存路径 | 所有已知相关缺陷修复且廉价正负例通过；入口不能假绿 | 单组件 20 分钟；具体 runner 超时由合同核实 | 原始失败、原因、修复映射、复验日志/XML/hash |
| C 候选冻结 | 记录源码 HEAD 与 dirty input；审计允许路径；清洁候选；冻结 verifier 和 8 DLL/插件输入闭包 | candidate 与所有合同文件一致；无未解释变化 | 30 分钟 | binding 初稿、候选输入清单/hash |
| D Windows 实包与场景 | 用既有 runner 的实际 Unity 命令/锁/guard，依序 G1→G2→G3→G4/G5→G6→G7→G8；按组避免重复昂贵构建，仍取得各自合同要求的 Player/PlayMode 原件。每组 30秒心跳，逐案判定；失败先保留原件并批修同类根因，组件复验后仅重跑受影响组合 | 真实 Player 矩阵满足原断言；最终候选/包同一性及影响闭包明确；失败不得被 summary/skip 隐藏 | 锁 600 秒；单次 Unity 导入/构建至多 90 分钟；单组执行至多 60 分钟；应用长场景按现有 runner deadline | 完整 Unity BuildReport、同包路径/hash、8 DLL closure、场景状态/请求/回执/截图、XML/native exit、manifest |
| E 封存/交付 | 停止本任务 writer；核对源码/环境/包和每案；先写报告与 binding 草案，再 stable manifest/hash；交独立 reviewer | 证据闭包一致、结论分层且未把 Windows 结果写成 Android/G2 签署 | 30 分钟 | Delivery、binding JSON、逐例台账、稳定 SHA 清单、恢复检查点 |

命令固定使用 Windows PowerShell 5.1 与 V5 工具：Unity `D:/game_dev_tools/unity/Editor/Unity.exe`、.NET `D:/game_dev_tools/dotnet/dotnet.exe`；不得用 Gradle/V4 替代。实际套件命令必须由 `run-g2-tests.ps1` 与子 runner 生成，原样留存 `command.json`/日志；WindowsCheats 必须明确传本候选新 verifier。Plan 输出须限定仓库 `build/` 下尚不存在的短 GUID 根，Player/Runner 输出用各自要求的独占 GUID 根。所有既有 runner 的守卫、锁和 fresh 行为照合同执行。

## 共享资源、进程、输出与取消

- Unity 共享锁：使用每个原 runner 的原锁；入口层不外加锁。实际锁名称和获得时间从执行器核实后录入。等待最多 600 秒，30 秒报告心跳；不绕锁。
- 运行前记录 Unity/Player 进程树与路径。仅正常停止自有 writer；仅在 G6 指定场景中结束具所有权记录的本任务 Player。绝不杀其他进程。
- 输出：`<repo>/build/<本任务 GUID>/`，短目录名，拒绝已有目标；长期只读缓存按原 runner 方式复用。不开删/搬他人目录。
- 每 30 秒写明当前阶段/耗时/自有进程 PID 与产物增长；每阶段设置有限超时。取消前采集自有 PID/后代、日志尾部、等待锁/设备原因；使用 runner 支持的正常取消并等 writer 退出，未知退出记 UNKNOWN，保留原件。
- 不改许可证、机器配置、不联网找文档、不强杀 Unity、不双重持锁。空间按实际缓存和包预算核对后补记；若不足只报告阻断，不清理共享内容。

## 重试与逐例记录

每一失败先复制/封存原始日志、XML、截图、请求/回执和 hash；按根因归组批修，更新允许文件范围；完成便宜正负例后复验受影响行及同包闭包再继续下一组。只有根因已处理、相关输入/环境变化或资源恢复后重试。记录每案 `PASS/FAIL/BLOCKED/NOT_RUN/UNKNOWN`，含第一次结果、修复文件/变更、复验命令/native exit/XML计数、游戏状态/请求回执/截图、稳定原件和 SHA-256。不得把 helper/fixture、runner exit 0、Total=0、菜单启动或历史包称为产品 PASS。

## 交付要求/最终状态

Delivery 必含实际基线与候选 commit/dirty inputs、Unity Windows 包绝对路径与启动方式/全包 hash/8 DLL 闭包/资源和插件清单/BuildReport、原样命令/native exit/XML count、32项逐例状态与首败→修复→复验、截图和请求回执绑定、稳定原件路径/hash、未覆盖项及接续点。组件、工具、PlayMode、Windows Player、Android 范围分开陈述。Windows 结果不等于 Android 通过或 G2 签署；不改变 V4 生产地位。独立 reviewer 依 V5 协议完整复核；如有确切缺陷，在同一线程批量返修。不得自动批准/触发合并。

## 范围变更记录

- 初始化：确认入口映射并发现 G7/G8/部分 G1/G4/G5 的完整真实执行覆盖需核实或补齐；落实前不得判 PASS。
- 2026-11-08：将 `tools/unity/run-p45-full-match.ps1` 纳入范围，按上述 Unicode 路径根因最小修改。G2工具回归30项通过；统一计划41行已核对为32项Windows、9行Android（其中C04-G8-03一项加C05八行），Android均排除本任务Windows实测分母。
- 2026-11-08：Actions 首次真实 LocalHost suite 184 项中 13 项因 `--artifacts-path` 下 fixture 仍拼旧 `bin/Debug|Release` 位置失败。范围据此扩展至 `P41Harness.cs` 及 `C04RecoveryRequestReconciliationTests.cs`、`LocalControlTests.cs`、`LocalHostCandidateAcceptanceTests.cs`、`LocalHostRecoveryTests.cs`、`LocalSaveBackupRecoveryTests.cs`、`LocalSettlementFactsPortTests.cs`；改用构建产物目录发现函数。修复后独立 Release 复验 184/184 通过；冻结归档 runner 尚未接受 dirty 候选，WindowsActions Player 阶段和四项 G2 仍 NOT_RUN。
- 2026-11-08 检查点：FullMatch、Cheats、Recovery、Privacy、AppMatrix 的实际命令、runner 子进程、Player/verifier 原件与哈希已写入 Delivery/binding。G6 8/8、G5 三案及 G3 六案有对应证据；G1 两行、G2 四行、G4 三行、G7-02 与 G8 尚有缺口。普通 Player 启动截图实际发现菜单画面裁切和 Missing-script/InputModule NullReferenceException，保留原图/日志，G8-01 标 FAIL，根因修复与复验仍待处理。当前 Windows 分母逐例为 19 PASS、5 PARTIAL、1 FAIL、7 NOT_RUN；不签署 G2，也不把 Android 范围计入。

## 2026-11-08 独立复核返修记录（第 2 轮）

- 保留原派发起点/merge-base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`；返修输入来自桥接交付 HEAD `bda5e039f920a939003eea847758928300d75fd1`，未重置、未手工提交。当前工作树已含本轮变更，不能称 clean/frozen candidate。
- 本轮纳入范围：`dotnet/tests/Mahjong.LocalHost.Tests/P41Harness.cs`、新增 `P41HarnessArtifactResolutionTests.cs`、`tools/unity/run-p45-full-match.ps1`；Unity Build-only 重新生成的 `unity/Mahjong.Unity/Assets/Plugins/Mahjong/` 下 8 个 DLL 与对应 PDB。临时 Unity 诊断脚本在 `Assets/Editor` 执行后已移除；Unity 仅格式化的字体/ProjectSettings 与 PerformanceTest 临时资产已恢复。
- D-1-03 修复及验收：helper 只按当前 artifacts 根和活动 Debug/Release 目录定位同配置依赖；隔离根缺文件时明确抛 `FileNotFoundException`，不查 repo 旧产物。新增 Debug/Release 正例和“隔离 Debug 缺失、repo 有旧 Release”的负例。Release 命令 `D:/game_dev_tools/dotnet/dotnet.exe test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --artifacts-path build/rework-d103-focused2-a35641d45ff744ea9451fa34272ecd2c --filter FullyQualifiedName~P41HarnessArtifactResolutionTests --logger trx;LogFileName=d103-focused.trx` native exit 0，187/187，0 skip；日志 SHA `3F3CDF177A5F19B27569D6B61ED1D122153AD9E26148B65742177B569324FA8C`。Debug 同命令 `-c Debug --artifacts-path build/rework-d103-debug-0b1d04c34b6440e5ac1057b084335ef5 --logger trx;LogFileName=d103-debug.trx` native exit 0，187/187，0 skip；日志 SHA `8A3D5E9A6B889F26FA29CD860DB39A860CF5F221C12DAC5270649FEA4423D700`。`--filter` 在当前 Microsoft Testing Platform 配置下仍实际执行整个套件，故记录真实全量计数；不把它写作 3 项定向计数。初次补丁测试 184 pass/3 fail 的真实发现是 artifacts 识别错误地要求输出项目名等于被查目标项目名；根因已修正，首次日志仍保留在 `build/rework-d103-0ad6ac868e02454cab9b895e571e579a/`。
- 本轮真实 Windows Build-only 命令由短路径 `R:` 执行：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File R:\tools\unity\run-p45-full-match.ps1 -Mode Build -RunDir R:\build\l6-r1-c00350e8c86f4ad3ad8e377ffff0d4f0 -UnityPath D:\game_dev_tools\unity\Editor\Unity.exe -DotnetPath D:\game_dev_tools\dotnet\dotnet.exe -TimeoutSeconds 1200`，共享锁 `Local\MahjongV5P45UnityValidation` 等待 0.089 秒、Unity native exit 0、runner exit 0。新 Player 在 `build/l6-r1-c00350e8c86f4ad3ad8e377ffff0d4f0/player/Mahjong.exe`；192 文件、108,954,668 bytes，`player-files.json` SHA `C0AAB4D93760B214797687D01795836E448BFAFBB1021C281AA40684DCC9D357`；exe SHA 仍为 `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`。Unity Build log SHA `3FBEB853F7D7D299ADCFCBC671D0CEDF1B803845A56CE64F3FDA621A4AF6023F` 未出现 `Script attached ... missing` 或 `NullReferenceException`；但这不能覆盖普通 Player 视觉验收。
- Build runner 在 shared DLL 生成之前捕获 `source.json`，随后另记录 87 个生成文件 patch SHA `B8923B56A6AE888E751F0B2590B7A1677DE38E851C6474C65E5BBC7C9179AED7`；所以新包仅作为 UI 复验输入，其 source snapshot 不能独自证明最终冻结闭包。
- 普通 Player 实际启动及新鲜 UI 证据：`build/l6-r1-c00350e8c86f4ad3ad8e377ffff0d4f0/ui-observation/process.json` SHA `2B0BEAD92786398D887AB61311622A373240F7D4864CB872A1D91434B396D084`；截帧 `windowed-player.png` SHA `8D91C97FF6A24BCB420D4003E08E6CB38C84A81A4DB1172AB2F9750ED8FBF2D3`。请求 1280×720 windowed，实际 window 525×323、client 512×288、DPI 240；进程正常 close，exit 0。图像仍只有标题/背景局部，按钮和完整菜单不可见；因此 D-1-01 仍 BLOCKER。Player 的 `-logFile` 未生成指定文件，本轮不宣称取得新的 Player.log。
- 补充 DPI 观察：`build/l6-r1-c00350e8c86f4ad3ad8e377ffff0d4f0/ui-observation/dpi-probe.json` SHA `DB2F3281325D0244DC85AAA4C6A19A5056D7788389FEF82E2C00E534F2BDD0D4`；Win32 `GetProcessDpiAwareness` 返回 HRESULT 0、awareness 2（per-monitor aware）。因此不能把画面裁切简单归为进程 DPI-unaware；仍需找出 Unity 窗口/Canvas 的坐标映射根因。
- 返修后入口合同回归：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/G2TestRunner.Tests.ps1` native exit 0，30 assertions；`build/g2-runner-test-0771255ce47743c298dc03ccf37c0927/test-summary.json` SHA `BBAE3989455EB5188471DEBCCDCC99EC8EDEF4BA3E1D45908EA6EC3E44E3F237`，其中 `evidenceClass=TOOL_FIXTURE`，只证明工具合同回归。
- 独立 Unity API 诊断证明 Boot/Menu 引用的 InputSystem、UGUI、TMP GUID 都解析到正确 MonoScript 类型；重新导入/脚本编译后的 scene scan 无 missing component。诊断日志 `build/ui-diag-2c76b2f8a90649cda4878e6a47a39144/unity.log` SHA `E19D4FD541F6FB99A98FC122CA54802B30C051EED3EE5F68BDD9F04165365A57`。这是对旧 Missing-script 原因的进一步定位；本轮新鲜 Player 截图仍失败，故不能将根因全部关闭。
- D-1-04：将 G7-01 从旧 binding 的误 PASS 更正为 `NOT_RUN`。FullMatch 的“没有远端 service 进程”不足以证明网络隔离或没有出站尝试；本轮未修改系统网络设置，也没有提供受控离线环境证据。
- D-1-02/D-1-05：保留原行状态；没有可用的桥接冻结/提交检查点工具，本执行者不得手工提交，也不得移动/藏起 dirty 输入绕过 WindowsActions clean guard。故本轮未声称 Actions Player 已运行、32 项已闭合、同一最终候选/同包矩阵已建立。现有各套件各自包仍只能作支持证据。
- G7-01 独立复核前 binding 原有 PASS 已按 D-1-04 改为 NOT_RUN；因此目前 32 个 Windows 行为 18 PASS、5 PARTIAL、1 FAIL、8 NOT_RUN（原复核批次的 7 NOT_RUN 计数未包含被纠正的 G7-01）。
- 返修负例矩阵：Debug/Release artifacts 根与配置交叉检查；在隔离 Debug 目标依赖缺失、但仓库旧 Release DLL 存在时必须失败；保留初次失败日志证明补丁最初错误识别路径；实际 Release/Debug 两套件均 187 项、0 skip。其他 D-1-01/02/04/05 的产品级负例/完整验收尚未完成，不得用本矩阵代替。

## 2026-11-08 独立复核第 3 轮接续

- 本轮候选起点：桥接已自动提供干净 HEAD `7f3942490eab8000e731f94049b48c10850a2257`（父 `bda5e039f920a939003eea847758928300d75fd1`），与原始基线 `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1` 的关系保留。先在未改源码 clean candidate 实际运行 WindowsActions；不可把该前置失败后的 PlayMode/Player 缺项写成 PASS。
- 首次真实 Actions 前置：fresh Release build exit 0；LocalHost 187/186/1/0/0（total/pass/fail/skip/not-run）。唯一失败是恢复后旧 decisionId 复用并被接受。该首次 failure/summary/frozen-source 原件完整副本位于 `E:/g2-r3-evidence-thread_20261108_190230_2b67/508260b3991f44cea1c374e5c8688019`；复制前后 3,783 文件、960,194,469 bytes，关键 archive/failure/manifest/summary 的 SHA256 已核对。
- Windows 文件系统失败机制已通过 Win32 真实文件句柄负例复现：`File.Replace` 在 backup 目标被无 delete-share 句柄占用时 HRESULT `0x80070497` / error 1175。独立首败具体持有进程/句柄未知，因失败清理已移除现场，未归因到杀毒或其他外因。最小修复仅对该异常做最多四次有界退避；每次重试先验证原存档/临时候选/目标提交字节，持续锁仍返回 UNKNOWN。恢复后决策 ID 加唯一 nonce；新负例覆盖瞬时真实 backup lock 的成功重试、持续 lock 下原 envelope 不变且 UNKNOWN、重开后旧 decision 被拒绝。
- 采集更正：同一 Player/context `-4` 的真实 client 为 1280×720、window 为 1312×808，基线菜单完整；context `-1` 的 512×288 是采集线程 DPI 虚拟化。Runtime Player.log 无 missing-script/InputModule/NRE。撤销上一轮“产品裁切”判断；不改 Canvas、机器 DPI。G8 仅基线菜单证据，完整宽高比例与键鼠请求闭环仍缺。
- 当前 dirty 源码组件 Release/Debug 分别 build exit 0、0 warning/error，完整 LocalHost 各 189/189、0 skip/not-run。Release XML `E:/g2-r3-release-LocalHost.xml` SHA `675F3A84B33F8BCF09A33626EFF1EF149D81B195072076AF3E2FDBBA99A8A697`；Debug XML `E:/g2-r3-debug-linked2-LocalHost.xml` SHA `657BAAD1DAB7674F15DB2E4C5FB577149B65F287D5F96F908E84606A9BEF6480`。`G2TestRunner.Tests.ps1` exit 0，30 assertions，摘要 SHA `94F629AC18365827BD689C1EF0BC3B7C888C38F78B180BCF1597908FF77A29BD`（仅工具证据）。完整逐 ID 首败/修复/断言见 Delivery 第 3 轮及 Binding.reviewRound3。
- C 盘阶段性无剩余空间；组件 Debug 第一次因为不能写入 repo build fixture 而失败，未作为产品 FAIL。其后把构建/临时/结果 XML 放 E 盘，经仓库 `R:` junction 让测试仍从仓库路径发现 fixture，完整 Debug/Release 全量通过。Actions 冻结产物副本在 E 盘；原 C 盘根仍留少量被占用文件，未覆盖清除。后续 runner 按现存空间使用短输出目录并记录 30 秒心跳/有界超时。
- 范围变更文件：`dotnet/src/Mahjong.LocalHost/LocalFileSystem.cs`、`LocalTableStore.cs`、`LocalTableDriver.cs`；`dotnet/tests/Mahjong.LocalHost.Tests/LocalTableStoreTests.cs`、`C04RoomTicketTests.cs`、`LocalControlTests.cs`；任务卡、Delivery、Binding。保持改动最小，不改 Unity 生产 UI/验证器/历史 Census 或 G2 阈值。
- 本轮五组全量结论：D-1-01 改为 MAJOR/G8 PARTIAL；D-1-02、D-1-04、D-1-05 仍 OPEN；D-2-01 的代码和组件验收 PASS，但仍需冻结 Actions 候选前置。当前 Windows 32 项为 18 PASS、6 PARTIAL、0 FAIL、8 NOT_RUN；Android 九行 NOT_RUN。
- 接续：先待桥接按同一 thread 生命周期为本轮 dirty 修复生成合法 clean candidate，再跑 WindowsActions；不得手工提交、藏改动或放松 clean/fresh guard。随后完成 G1/G2/G4/G7-02、G8、G7-01 离线与同包 32 行闭包。未完成项保持 OPEN/NOT_RUN；本轮不签署 G2、不更改 V4 生产地位。
# 第4轮执行增补（2026-11-08）

桥接复核起点为干净候选 `cf477a7497c8c016e540917dab24f7a2d4318c46`，父提交 `7f3942490eab8000e731f94049b48c10850a2257`，merge-base 仍为 `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`。源码修复后当前树为 dirty；不得绕过 clean/fresh guard。WindowsActions 已在修改前、以该干净候选实际完成冻结归档、前置、PlayMode 和四案 Windows Player；详见 Delivery 第4轮。后续需要候选冻结时走桥接生命周期，不手工提交。

## 第4轮实测与门禁

- D-3-01 已在 `LocalTableStore.Persist` 每次原子替换重试前检查取消；预提交取消通过既有 `CompleteWrite` 转为安全 `OutcomeUnknown`/fencing，不从内存写路径直接抛取消；已提交候选仍以候选字节核验保留 `Committed`。
- Release 与 Debug 都从本轮 dirty 源在 `E:\g2-r4-release`、`E:\g2-r4-debug` 独立构建并通过，完整 191 项/191 通过/0 失败/0 跳过/0 未运行。D-3 定向四案 4/4 通过；取消探针对修复后当前 Release DLL 输出 `replaceAttempts=1`、`verdict=Aborted`、`primaryChanged=False`。命令、XML 与 SHA256 见 Delivery 第4轮证据表。
- WindowsActions 在 clean `cf477...` 的四个 PlayMode 正例 4/4 通过；普通 Windows Player 的 Pon、Chi、Riichi、ClosedKan 四个正例各 exit 0，汇总 `C04_PLAYER_ACTIONS_PASS`。Riichi 原件只证明普通立直，不证明双立直；ClosedKan 原件证明岭上替换摸牌，不证明抢杠；逐 Census 负例仍缺。因此 G2 行仅将实际正例绑定并保持 `PARTIAL`，不得将它们计为完整合同 PASS。
- 当前 32 行总判定仍不通过。G1 真实牌局分支、G2 缺少的负例/双立直/抢杠、G4 玩家 Player 控制代次与重入、G7 权威入口与离线隔离、G8 完整布局与真实输入、同一最终候选/同包闭包仍须完成。WindowsActions 的旧 clean 包只作本轮实际支持证据；因 LocalHost 源已修改，它不是本轮最终候选包。Android 9 行继续排除并为 `NOT_RUN`。
- G7-01 离线隔离只做了只读预检，未执行离线 Player：WLAN 与 Meta Tunnel 均为 Up；Windows Defender Firewall 三 profile 为 Enabled 但 DefaultOutboundAction=NotConfigured；找不到 `WindowsSandbox.exe`，而按程序配置防火墙会修改主机安全配置，超出本轮安全环境条件。预检原件 `E:\g2-r4-offline-preflight.txt` SHA256 `6E7C9555727E4092C5FB33529ABC2AE960724C0C61E556C35E76C76DF51DB340`。没有改适配器、防火墙或安全设置；该环境条件不阻止其他可做场景继续。
- G7-02 另运行真实 TableService Kestrel/WebSocket 结算正例及 raw settlement envelope 拒绝负例各一项，2/2、0 skip；这只证明服务端有权威结算边界，不等于指定 LOCAL 资产请求及其零版本/事件/账本/outbox 增量断言。原件/hash 在 Delivery 与 Binding，G7-02 保持 PARTIAL。
- WindowsActions 原件已从 C 盘完整复制到 `E:\g2-r4-actions` 并逐文件校验 4,267 个文件/1,093,417,431 字节；原 `build/4f8760efef9c45d0b585c9c2fbfb5ee8` 稳定路径是指向该 E 盘副本的 junction。文件级 SHA256 清单 `E:\g2-r4-actions-files.sha256.json` SHA256 `91B14CD9BD29B8CF232B1B1E04095BC682F255A9ED4510EA4C1BA66C93E68069`。长路径导致跨卷移时留下的 37 个文件另存于 `build/4f8760efef9c45d0b585c9c2fbfb5ee8-source-remnant-r4`，其余残留副本在 `E:\g2-r4-actions-source-original`；两者未删除，完整可引用证据以 E 盘校验副本为准。迁移后 C 盘可用空间约 1.09 GB，E 盘约 247 GB。

### 当前变更文件范围

- `dotnet/src/Mahjong.LocalHost/LocalTableStore.cs`：取消检查，仅在重试边界。
- `dotnet/tests/Mahjong.LocalHost.Tests/LocalTableStoreTests.cs`：预提交取消无第二次替换与双 envelope 不变；提交后异常仍 Committed；覆盖既有瞬时/持续锁案。
- 本任务 Task、Delivery 与 Binding 文档：追加第4轮事实与原件绑定；不修改 Census、统一 verifier 或验收标准。
## Round 5 implementation increment (2026-11-08)

The bridge starting candidate was clean `7f232051d6643c9a6694b437fb30032eb9de3c32` (parent `cf477a7497c8c016e540917dab24f7a2d4318c46`, merge-base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`). The only code/test source change is `dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerActionChannelTests.cs`; task/delivery/binding-supplement records were also updated. The worktree is dirty and has not been committed, pushed, or merged.

D-1-02 gained real LocalHost `ITableChannel` component coverage: four illegal entity-tile actions (Pon, Chi, Riichi, Kan) require a Rejected receipt with no command version/event sequence and unchanged authority version/event sequence/136-entity hash; a no-candidate Chi request has the same zero-mutation assertions. The existing first uninterrupted Riichi acceptance now requires the published `DoubleRiichi=true` fact. This does not replace Player/PlayMode evidence or close D-1-02.

Release and Debug full LocalHost suites each passed 196/196 with native exit 0, 0 failed/skipped/not-run. Release XML: `E:\g2-r5-localhost\LocalHost-final5.xml`, SHA-256 `F9B974D363402F0B29E7147B8A9FB1EA27C0FAD085CC018835BD8E5A47AFF2EB`. Debug XML: `E:\g2-r5-debug.xml`, SHA-256 `255440A0E74A4E1CE1037D11C58166E84F1D64195137B0E289894A3A6EEB125B`. Release logs: `E:\g2-r5-localhost\build-final4.log`, `E:\g2-r5-localhost\LocalHost-final5.log`; Debug logs: `E:\g2-r5-debug-build.log`, `E:\g2-r5-debug-test.log`.

Still open: G1 complete Player win/draw/dealer continuation/new-round binding; G2 kan/rinshan/chankan and remaining negative cases; G4 old AI result rejection/re-entry reconciliation/claim epoch in this candidate; G7-02 real LOCAL asset through service with zero side effects; G8 full layouts and physical input; G7-01 safe offline environment; final same-candidate/same-Player 32-row closure. Current Android 9 rows remain NOT_RUN. Overall task remains incomplete.

Per-row supplement: [Binding R5 supplement](V5-G2-WINDOWS-LUNA6-BINDING-R5-SUPPLEMENT-20261108.json). Original 32-row statuses remain unchanged.

## Round 6 continuation (2026-11-09)

Bridge start: `33173f613ac88a4b74d11cdc5126368b17f7bcf4` (parent `7f232051d6643c9a6694b437fb30032eb9de3c32`, merge-base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`). This round added Player negative/control probes and verifier assertions, refreshed shared Unity plugin inputs, and passed the existing 4-case C04 PlayMode suite. An earlier Unity compile passed before the final LocalHost plugin refresh; the follow-up compile did not complete because the C volume ran out of space while Unity wrote `Library/Bee` (no C# diagnostic). The LocalHost/service changes passed their .NET suites. Full Windows acceptance remains incomplete; Binding stays 18 PASS / 11 PARTIAL / 3 NOT_RUN. D-1-02, D-1-01, D-1-05, and D-1-04 remain open. Full execution and defect map: [Round 6 execution supplement](V5-G2-WINDOWS-LUNA6-R6-EXECUTION-SUPPLEMENT-20261109.md); evidence summary: [Binding R6 supplement](V5-G2-WINDOWS-LUNA6-BINDING-R6-SUPPLEMENT-20261109.json).

## Round 7 continuation (2026-11-09)

Independent review identified D-6-01: the R6 compile invocation selected the worktree root and added 536 generated root-project files to the bridge candidate. All 536 files are hash-manifested and physically preserved; their index entries have been removed without deleting bytes. A correctly quoted invocation compiled `unity/Mahjong.Unity` with exit 0 and the log confirms that exact root. The correction is staged for the bridge lifecycle; preserved files remain physically present, so no clean final candidate is claimed. D-1-02/01/05/04 remain open and the task remains unaccepted. Details, full repros, evidence and acceptance assertions: [Round 7 execution supplement](V5-G2-WINDOWS-LUNA6-R7-EXECUTION-SUPPLEMENT-20261109.md). Root-file manifest: `E:/g2-r7-d6-preserved-root-generated/preserved-generated-files.json`, SHA-256 `85E183586DBDB3FBD4D506EB2180F97DBAB03073C65F2678DC6D053C56156F65`.
