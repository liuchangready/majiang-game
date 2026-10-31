# G2-C05 完整游戏 Android 与视觉/输入工具交付

日期：2026-11-01。实现者：Codex GPT-6-Luna。冻结源码基线：a8d58ffb81957e3f2afb7281d1d009b834e0ee5d（PR20 Draft 候选，不是 G2 或 v5 合并证明）。本交付是工具和开发构建证据；最终验收须在 C03 统一候选上重跑。

## 工具行为

tools/unity/build-p45-android-game.ps1 以全局互斥 Local\MahjongV5P45UnityValidation 串行执行：强制重建七个共享程序集及依赖闭包，运行现有 provenance verifier，再由 Editor-only C05FullGameAndroidBuild.Build 构建 EditorBuildSettings 所有启用场景。Editor 入口使用 CleanBuildCache，核对 Boot/Menu/Lobby/Table/Spectator/Settlement、Domain/Contracts/Wire/TableRuntime/Client/LocalHost/AI/Google.Protobuf、TMP Settings/字体、Android 包标识、IL2CPP、ARM64-only、minSdk、横屏限定、全屏和安全区设置。必须同时有 Unity 原生退出码 0、成功 BuildReport marker、时间新鲜 APK、aapt2 包/SDK/ABI 元数据才记 PASS；不以 APK 存在或 Unity exit 0 单独判成功。证据写入 ignored build/p45-android-game/<run>/，包含共享构建/provenance、Unity 原始日志、报告 marker、APK、元数据、摘要、SHA-256 和字节数。互斥等待默认 30 秒，Unity 上限 7200 秒；超时只按本工具启动进程的 PID+StartTime 识别。

tools/unity/run-p45-android-game.ps1 的设备动作先采集 adb devices -l。仅唯一授权设备可省略 serial；多设备必须传 -Serial。未授权、离线、无设备分别 fail-closed 或 NOT_RUN，不猜 serial。设备操作只对指定 serial 和 com.majiang.game 执行。Capture 输出 PNG、目标进程 logcat 和分辨率/density/rotation/window 摘要；空文件拒绝，黑/近黑或无法解码标记需调查。截图语义始终要求人工复核，工具不会给视觉 PASS。

## 复现命令

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyP45AndroidGame.Tests.ps1
$manifest = 'PATH_TO_ACCEPTED_P2_2_PRIVATE_MANIFEST'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-p45-android-game.ps1 -AndroidToolchainManifest $manifest
$summary = Get-ChildItem build/p45-android-game -Filter summary.json -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$runDir = Split-Path $summary.FullName -Parent
$apk = Join-Path $runDir 'Mahjong.apk'
$serial = 'AUTHORIZED_DEVICE_SERIAL'

# 列举设备；出现多个授权 serial 时，后续命令必须传 -Serial。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Devices
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action DeviceInfo -Serial $serial -EvidenceDir (Join-Path $runDir 'device-info')
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Install -Serial $serial -Apk $apk -EvidenceDir (Join-Path $runDir 'install')
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Start -Serial $serial -Apk $apk -EvidenceDir (Join-Path $runDir 'start')
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Capture -Serial $serial -Apk $apk -EvidenceDir (Join-Path $runDir 'device')
# 在设备上人工点击菜单/大厅/牌桌并用系统返回；记录每个真实 UI 状态。再手动切后台/恢复后采集。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Capture -Serial $serial -Apk $apk -EvidenceDir (Join-Path $runDir 'device')
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Stop -Serial $serial -Apk $apk -EvidenceDir (Join-Path $runDir 'stop')
```

在 C03 最终统一候选合入后，先确认其确切 HEAD 和允许 dirty paths，再重新运行 tests、provenance 重建/验证及 build 命令；需新建证据目录，不复用旧 APK。构建后必须从新 APK 的 BuildReport、包元数据、哈希和设备运行结果填写最终矩阵。不得把本次开发树结果称为 clean final-tree build。

## 运行矩阵与人工步骤

| 项目 | 可重复自动动作 | 人工/语义核对 | 本轮状态 |
|---|---|---|---|
| 菜单 → 大厅 → 牌桌 → 结算可达性 | 仅启动和截图；不推断导航成功 | 逐屏记录按钮、实际转场及结束画面；需可用本地房间并完成真实玩法 | NOT_RUN：无授权设备 |
| 横屏 16:9、超宽屏 | DeviceInfo 记录物理分辨率、density、rotation、window 显示信息 | 至少一台 16:9 与一台超宽设备分别人工检查裁切、字号和点击区 | NOT_RUN：无设备 |
| 安全区 | Capture 附 window/display 摘要 | 对照刘海/系统栏位置检查可交互内容是否在安全区内 | NOT_RUN：无设备 |
| 触屏点击/返回 | 工具只安装、启动、截图、停止，不注入坐标伪装业务结果 | 逐项人工点击菜单/大厅/牌桌/返回；记录触点对应的 UI 和实际状态变化 | NOT_RUN：无设备 |
| 后台/恢复 | 人工切后台再前台后 Capture/logcat | 确认无黑帧、会话是否保留、输入是否恢复；单纯进程 exit 0 不算通过 | NOT_RUN：无设备 |
| 视觉证据 | 截图哈希及尺寸采集；近黑仅标调查 | 人工复核画面语义与场景；视频/截图按节点编号归档 | 未执行 |

人工记录模板：设备型号/Android/API；serial 脱敏标识；分辨率/density/rotation/安全区；APK SHA-256；步骤编号与人工点击/返回/后台动作；观察到的屏幕/状态变化；截图文件名、尺寸、SHA-256；logcat 文件与相关错误；PASS/FAIL/BLOCKED/NOT_RUN 及原因。只有坐标 tap 或截图文件存在不能标业务动作通过。

## 实际结果与 R2 返修

本节为返修后的最终状态；上方首轮记录保留用于追溯。首轮独立复核原件 SHA-256：42CD2E5F4603825E79F5BF2F393190BC47B28E78EED7C8C320E2BCD2D0F41496。复核清单 4 MAJOR + 2 MINOR 均为首轮新增问题，没有历史遗留。

| 检查 | 状态 | 证据 |
|---|---|---|
| Unity/Android 工具链 | PASS | Unity 6000.3.27f1 revision 672d562a42a7；AndroidPlayer/IL2CPP、OpenJDK 17.0.20.1、SDK 36/build-tools 35/36、NDK 27.2.12479018、CMake 3.22.1、adb 可用。已接受 P2.2 私有 manifest SHA-256 E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934、Ninja 1.13.2 和 adapter 校验通过；未安装/升级/复制工具。 |
| 共享程序集与 provenance | PASS | build/p45-android-game/20261101-r2-clean-2ccc7295/shared-build.log 与 plugin-provenance.log；19 个产物、8 个必需程序集，provenance verifier PASS。 |
| PowerShell 5.1 与工具套件 | PASS | build、run、tests 共 3 个脚本 parser 0 errors。VerifyP45AndroidGame.Tests.ps1 exit 0，26/26 assertions；build/p45-tests-final-r1-rework.log SHA-256 2CB9184BE900E590A30F644DC6F4CE5D0C9C9D14AD02C711448031677238623F。伪 Unity/adb 均标 TOOL_FIXTURE，不是游戏运行证据；保留证据在 build/p45-tool-tests/acf32dcf94a6449db76e794485ed435e/。 |
| 首轮真实 Android 构建 | FAIL（原件保留） | 冻结 HEAD a8d58ffb。Unity exit 1，BuildReport Failed/errors=3/warnings=1，无 APK。Android CLI/sdkmanager 的 sdk list 以 -1073740791 (0xC0000409) 崩溃。build/p45-android-game/20261101-030909-9116d361/unity.log，1,840,720 bytes，SHA-256 49B41399C6B1B35AA076AB337917C7F5686F3D94C9CD206293E568DBA9F278E7；BuildReport JSON 在同目录。 |
| 当时互斥繁忙的构建 | BLOCKED（原件保留） | 30 秒互斥等待失败；当时 C01 Player 持有 Local\MahjongV5P45UnityValidation，没有启动第二个 Unity。build/p45-android-game/20261101-032108-d584acd2/failure-summary.json。 |
| R2 缓存 APK 尝试 | FAIL（正确拒绝旧时间产物） | BuildReport Succeeded，但 APK 时间早于本轮；工具拒绝它。build/p45-android-game/20261101-r2-validated-7a2a24c0/failure-summary.json SHA-256 34350A7D44D4ED640D2B3C2BE9E062B9FC1AA4E85D50CC49158659ED89B04A4C。此缓存行为是新信息：此前冷构建无法发现；Editor 入口随后启用 CleanBuildCache，freshness 断言保持严格。 |
| R2 clean-cache 完整游戏 Android APK | PASS（开发树） | build/p45-android-game/20261101-r2-clean-2ccc7295/；native exit 0，BuildReport Succeeded/errors=0/warnings=194，APK 52,142,409 bytes，SHA-256 0506AEEA2593B7E70C7B2796CE353D3B401A12ADCBF1FFFCC24A0464481C8D6B。source HEAD ce74c277272e395df6ab09d063d356dc889b2d12；构建时 4 个允许任务文件 dirty。summary.json SHA-256 62AD41746D25B3B40E889F35154CA02A7EFBF97B1B55AD0042C9F8577821716B；Unity log SHA-256 8B22109DF4453DE80EB9C0734037A9E0BFF0AA0B213C38279E951CCB3F03101E；BuildReport SHA-256 AC9C1279A141E43761833403A66ABCC6C709D0302EB0E5AF07CC0011EDBCEF52。 |
| APK 与生产构建设置 | PASS | BuildReport 包含 Boot/Menu/Lobby/Table/Spectator/Settlement、Android、IL2CPP、ARM64、TMP。Editor 检查 minSdk、横屏限定、全屏和 outside-safe-area。aapt2 实测 package com.majiang.game、minSdk 25、targetSdk 36、arm64-v8a；metadata SHA-256 3952FE66E4A72AB88063A7CFEB107B13D8BBBDE3D19A6523BCBC85ECFF675BEE。 |
| 设备安装/启动/视觉输入 | NOT_RUN | 本轮真实 adb devices -l exit 0、设备列表为空；build/p45-android-game-device/20261101-042326-859f236e/device-list.json。菜单至牌桌/结算、16:9/超宽屏/安全区、触屏/返回/后台恢复、真实截图语义均未执行。 |

### R1 缺陷逐项修复映射

| ID / 级别 | 位置、复现命令 | 实际 vs 期望 | 证据 | 修复与验收断言 |
|---|---|---|---|---|
| C05-R1-01 MAJOR | tools/tests/VerifyP45AndroidGame.Tests.ps1；运行完整工具套件（套件自身建立预存哨兵并连续运行两次）。 | 原 finally 删除既有目录和哨兵；期望拒绝复用且历史内容保留。 | 首轮 caller-c05-r1/existing-output-test.log、observed-evidence.json；修复后 build/p45-tool-tests/acf32dcf94a6449db76e794485ed435e/。 | 每轮 GUID 专属目录、显式归属、绝对规范化路径在独占 build 根；哨兵经两次完整运行保留；失败输出不清理。 |
| C05-R1-02 MAJOR | tools/unity/build-p45-android-game.ps1:Get-C05SourceBoundary；powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-p45-android-game.ps1 -SourceFixtureTest。 | 原 staged scene 漏检；期望 index/worktree/untracked 均拒绝非允许输入，Git native failure fail-closed，允许 dirty 字节绑定。 | build/p45-tool-tests/acf32dcf94a6449db76e794485ed435e/ 与最终 summary.json。 | 实际入口 fixture 拒绝 staged/unstaged/untracked；Git exit 错误拒绝；允许 dirty 文件记录 index blob 和 worktree SHA。 |
| C05-R1-03 MAJOR | tools/unity/run-p45-android-game.ps1:Get-C05VerifiedArtifact；全套件安装动作负例。 | 原 21-byte 假 APK 可触发 adb install，Start/Capture 忽略所选 APK；期望 side effect 前验证 APK/BuildReport/hash/package/SDK/ABI。 | install/launch/capture JSON 在 build/p45-tool-tests/acf32dcf94a6449db76e794485ed435e/。 | 非 APK、wrong package、hash/report mismatch、横屏错误、SDK 不合法均在 install 前拒绝；Start/Capture 记录 artifact/build SHA 与 sourceHead，runtimeArtifactLinkage 明确 UNVERIFIED_INSTALLED_PACKAGE。 |
| C05-R1-04 MAJOR | tools/unity/run-p45-android-game.ps1:Invoke-C05Adb/Invoke-C05AdbBinary；完整套件 hung devices/capture、stderr-heavy、DeviceInfo/pidof/logcat exit 17。 | 原 native failure 被压平，WaitForExit 无界且 stdout/stderr 可能死锁；期望逐条保留退出/流/状态、有界等待、仅杀本进程 PID+start identity。 | 各动作 JSON 在 build/p45-tool-tests/acf32dcf94a6449db76e794485ed435e/。 | 错误保持 FAIL/PARTIAL；hung action bounded；stdout/stderr 并行排空；外来同名进程仍存活；黑/空截图 screenshotIsGamePass 始终 false。 |
| C05-R1-05 MINOR | tools/unity/build-p45-android-game.ps1:Start-C05HiddenProcess；powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-p45-android-game.ps1 -ArgumentFixtureTest。 | 原 Start-Process 参数丢失路径边界；期望实际生产启动器保留 Windows argv。 | argv recorder 结果在 build/p45-tool-tests/acf32dcf94a6449db76e794485ed435e/。 | 空格、内引号、尾反斜线通过生产启动函数原样回读。 |
| C05-R1-06 MINOR | tools/unity/run-p45-android-game.ps1:device selection；套件跑无 serial/明确 serial offline 和空设备。 | 原无 serial 时唯一 offline 被报 NOT_RUN；期望只有无设备为 NOT_RUN，offline/unauthorized 为 BLOCKED。 | device-list 与动作结果在 build/p45-tool-tests/acf32dcf94a6449db76e794485ed435e/。 | 多设备歧义、offline、unauthorized、无设备分别断言；BLOCKED 不增加 install 调用并保留原始列表。 |

C03 最终统一候选验收时须在其确切 HEAD 重新跑工具套件、共享 DLL/provenance 和 clean-cache 完整 Android 构建；有授权设备时再执行安装、启动、截图/logcat 与人工视觉/输入矩阵。本轮只交付工具与开发构建证据，不宣布 C05/G2/G4/iOS 完成。
## R2 返修结果：C05-R1-03 MAJOR 验证缺陷组

R2 原始复核件 `D:/majiang-wt/caller-c05-r2-review-ec1af417/build/caller-review/REVIEW.md` SHA-256 `C9956F155FD6FB781281ED32135AEA80B662EEFB246E18FE5A7928F16A18593E`；证据清单 SHA-256 `C45E4CE2EC79E41DC579FBC5B3822C7657F9A7DC08E3353A53FC5B3C9F49D94C`。R2 仅有 1 个 MAJOR 缺陷组（0 BLOCKER、无单列 MINOR）；R1-01/02/04/05/06 按 caller 的独立探针已关闭，本轮没有重开。

复现命令（caller 独立复核树）：

```powershell
Set-Location D:\majiang-wt\caller-c05-r2-review-ec1af417
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/caller-review/probe-verdict.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/caller-review/probe-entry.ps1
```

两个 probe 的 native exit 0 表示复现脚本完成，TOOL_FIXTURE，不是游戏 PASS。原件证据：`build/caller-review/verdict-results.json` SHA-256 `0C10E015E41A193E65A620B0F873E82F0D05BCE558AD7C82B478E8763E61BE3B`；`entry-results.json` SHA-256 `788D5F06BD0A64D27C86A247E1083CF6E3467C11A910FD11A745EE03285C949B`。caller 原件保持只读。

| 子项（同属 C05-R1-03 MAJOR） | 位置与实际 vs 期望 | 修复映射及验收断言 |
|---|---|---|
| 场景/TMP | `tools/unity/build-p45-android-game.ps1:Get-C05ProjectContract/Assert-C05BuildOutcome` 与 `tools/unity/run-p45-android-game.ps1:Get-C05VerifiedArtifact`。旧构建门禁把 `Report.scenes` 自己作为 RequiredScenes，故空场景和缺 Settlement 也能过；旧安装门禁不查生产场景和 TMP，完整 Install 进 fake adb。期望场景期望集来自固定六个生产场景及实际启用的 `EditorBuildSettings.asset`，报告/摘要/输入彼此一致，TMP 必须存在。 | build 读取启用场景与 `ProjectSettings.asset`，固定验证 Boot/Menu/Lobby/Table/Spectator/Settlement；报告场景须按序与启用输入一致且包含六场。run 两侧证据报告均验证六场与 `tmpResources=true`。定向门禁拒绝 empty scene、missing Settlement、TMP false/missing；完整入口 12 种坏证据对 Install/Start/Stop 的副作用均 0。 |
| ABI | 同上两个脚本的 `Get-C05BadgingContract`/`Get-C05RunBadgingContract`。旧子串匹配放过 `'arm64-v8a' 'x86_64'` 与 `unrelated-native-code`；only-x86 对照能被拒绝。期望仅接受恰好一条完整、锚定的 native-code 记录，内容恰为单一 `arm64-v8a` token。 | 按完整行解析且要求恰一条、恰一个 ABI token；额外、错误、缺失、重复、畸形记录均拒绝。构建与安装门禁均走实际解析函数；完整入口逐 Install/Start/Stop 断言 0 mutation。 |
| SDK 关联 | 同上两个脚本。新安装门禁曾接受 BuildReport minSdk=25 与 APK metadata minSdk=21/targetSdk=36；两份记录分别看似合理但相互矛盾。期望 APK minSdk 精确绑定 report 和 ProjectSettings；显式 targetSdk 必须与项目设置相等，自动设置 0 时 targetSdk 必须等于已接受工具链中解析出的最高 Android platform，且 target>=min>=21。 | build 对齐真实 `ProjectSettings.asset`、BuildReport 和 aapt badging，并从已接受工具链 platforms 解析自动 target。run 要求 metadata minSdk 等于报告，target 对齐显式设置或 summary 中被记录的自动平台意图。覆盖 metadata/report、report/project min/target 矛盾、显式 target 与 APK 不同。错误输入均在 Install/Start/Stop 调用前失败。 |

统一负例矩阵：两个 build gate 覆盖空/缺场景、TMP false/missing、ABI extra/wrong/missing/duplicate/malformed、metadata/report 与 report/project 的 SDK 矛盾，并保留 valid control；实际 run 脚本用套件 fake adb 为 12 类场景/TMP/ABI/SDK 无效证据分别调用 Install、Start、Stop 共 36 次，要求非零退出且 install/monkey/force-stop 调用计数保持 0。有效 APK fixture 的 Install、Start、Stop 正向控制仍通过；另保留 install 原生失败传播负例。

返修验证：

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-p45-android-game.ps1 -VerdictFixtureTest`：native exit 0，包含 18 个拒绝断言和有效对照；日志 `build/p45-verdict-r2-artifact-gates.log` SHA-256 `F67D6D0CB581AD33C6E3E387C9E093E2ED202ABFC77563542AA23A29DECB60D5`。
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyP45AndroidGame.Tests.ps1`：native exit 0，29/29 named assertions；内部连续两次完整运行，36/36 无效 run-entry mutation 断言通过。日志 `build/p45-tests-r2-artifact-gates-final.log` SHA-256 `FF3DF64B730B1C80207E67297233A5B9F08267DDBC677796631C7D248FE7F283`；实际完整证据目录 `build/p45-tool-tests/21738db247f64d56a220d03fdef51062/`。
- PS5.1 parser：build/run/tests 三脚本各 0 parse errors；`git diff --check` 通过。
- 仅修改原六 expectedFiles 中的 build/run/tests/交付报告四个文件；Editor 构建入口、ProjectSettings、场景、共享 verifier、业务代码和冻结 base 未改。R2 认可的 APK 仍是 `ce74c277` 加允许 dirty 输入的历史开发结果；本轮没有 Unity 重建，没有刷新或重新标记 APK 证据。工具链缺陷不影响其 caller 已确认的 aapt2/ZIP/场景结果。

新增信息与轮次边界：安装前验证函数和 SDK/settings 字段是在 R2 新加入的验证面，因此 metadata/report SDK 矛盾本轮才可由新完整入口复现。空场景自引用和 ABI 子串缺陷已在补充 R1 caller 复核中发现，但当时同 caller 并行路径已派出 R2，未进入那份六项返修提示；这是 caller 反馈遗漏，不是接收方忽略显式要求，也不是 R2 引入的产品回归。现将同组问题一次列全并共同关闭。

### 当前已知限制

本轮没有为了门禁脚本修正重复构建 Unity；开发 APK 是历史证据，不代表新当前 HEAD、clean final tree 或 C03 候选。caller 的独立真实设备枚举为 0 台，安装/启动、视觉与输入矩阵仍为 NOT_RUN；caller 原 `real-devices.log` 是 daemon 继承句柄尚开文件，标记 UNSEALED，不为释放它去杀共享 adb server。无 C05/G2/G4/iOS 完成声明。