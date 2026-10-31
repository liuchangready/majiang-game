# G2-C05 完整游戏 Android 与视觉/输入工具交付

日期：2026-11-01。实现者：Codex GPT-6-Luna。冻结源码基线：a8d58ffb81957e3f2afb7281d1d009b834e0ee5d（PR20 Draft 候选，不是 G2 或 v5 合并证明）。本交付是工具和开发构建证据；最终验收须在 C03 统一候选上重跑。

## 工具行为

tools/unity/build-p45-android-game.ps1 以全局互斥 Local\MahjongV5P45UnityValidation 串行执行：强制重建七个共享程序集及依赖闭包，运行现有 provenance verifier，再由 Editor-only C05FullGameAndroidBuild.Build 构建 EditorBuildSettings 所有启用场景。构建入口核对 Boot/Menu/Lobby/Table/Spectator/Settlement、Domain/Contracts/Wire/TableRuntime/Client/LocalHost/AI/Google.Protobuf、TMP Settings/字体、Android 包标识、IL2CPP 和 ARM64-only。必须同时有 Unity 原生退出码 0、成功 BuildReport marker、Android APK 和 APK badging 元数据才记 PASS。证据写入 ignored build/p45-android-game/<run>/，包含共享构建/provenance、Unity 原始日志、报告 marker、APK、元数据、摘要、SHA-256 和字节数。互斥等待默认 30 秒，Unity 上限 7200 秒；超时只按本工具启动进程的 PID+StartTime 识别。

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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Devices -Apk $apk
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action DeviceInfo -Serial $serial -Apk $apk -EvidenceDir (Join-Path $runDir 'device-info')
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Install -Serial $serial -Apk $apk -EvidenceDir (Join-Path $runDir 'install')
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Start -Serial $serial -Apk $apk -EvidenceDir (Join-Path $runDir 'start')
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-android-game.ps1 -Action Capture -Serial $serial -Apk $apk -EvidenceDir (Join-Path $runDir 'device')
# 人工复核并记下场景、操作、结果和截图 SHA；后台恢复后再次 Capture。
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

## 实际结果

| 检查 | 状态 | 证据 |
|---|---|---|
| Unity/Android 工具链确认 | PASS（组件可用）；当前本轮构建因互斥 BLOCKED | Unity 6000.3.27f1 revision 672d562a42a7；AndroidPlayer/IL2CPP、OpenJDK 17.0.20.1、SDK android-36/build-tools 35/36、NDK 27.2.12479018、CMake 3.22.1、adb 均存在。复用已接受 P2.2 私有 manifest SHA-256 E4785B1BE9FD4CE158E6730C1F7E01C58A7C974D45A407EF7E9CC215F3BEE934、Ninja 1.13.2 与 adapter 哈希校验。 |
| 共享程序集构建与 provenance | PASS | build/p45-android-game/20261101-030909-9116d361/shared-build.log、plugin-provenance.log；19 个产物，8 个必需程序集。构建 provenance 快照 SHA-256 8A058815EA5EEC18C21C324E3AA07DCE05F920AD867A384B213F5C77C4C14DE0 |
| 工具夹具 | PASS，10/10 | 命令：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyP45AndroidGame.Tests.ps1；exit 0。覆盖 unauthorized、无设备、多设备、adb install exit 12 传播、近黑截图不通过、空截图拒绝、Unity 缺失、旧输出目录拒绝、PID+StartTime 超时归属、BuildReport/APK 缺失/失败/陈旧拒绝；仅 TOOL_FIXTURE。PowerShell 5.1 parser：3 scripts，0 errors。 |
| 完整 Android APK | FAIL（首次真实尝试）；后续重跑 BLOCKED | 冻结 HEAD a8d58ffb。首轮 Unity 原生 exit 1；BuildReport target Android/backend IL2CPP/ARM64、result Failed、errors=3、warnings=1、APK bytes=0。日志中 Android CLI/sdkmanager 调用 `sdk list` 以 `-1073740791 (0xC0000409)` 崩溃。原件：build/p45-android-game/20261101-030909-9116d361/unity.log，1,840,720 bytes，SHA-256 49B41399C6B1B35AA076AB337917C7F5686F3D94C9CD206293E568DBA9F278E7；BuildReport JSON 在同目录 build-report-detail.json。没有 APK、marker PASS 或 badging。 |
| 带已接受 adapter 环境的完整构建重试 | BLOCKED | 30 秒互斥等待失败，原始 BuildReport/无 APK 记录在 build/p45-android-game/20261101-032108-d584acd2/failure-summary.json；当时 C01 Player 持有 Local\\MahjongV5P45UnityValidation，未启动第二个 Unity。 |
| 设备安装/启动/截图/输入/后台恢复 | NOT_RUN | 初始真实 `adb devices -l` exit 0，结果仅设备表头；无授权设备。 |

已知依赖：本树未预置私有 helper；只读 repair CheckOnly 在本树以 exit 1 报告本地 Ninja/CLI adapter 缺失。构建入口要求显式传入既有 P2.2 接受 manifest，并固定校验其 manifest、Ninja、adapter 哈希；未安装、下载、升级或复制系统工具。首次构建没有向 Unity 注入该 adapter 环境，故失败；带 adapter 的下一次尝试被 C01 的全局互斥挡住，需在最终候选上重跑。

未覆盖/依赖：在互斥可用时按已接受 manifest 重新执行一次 fresh 完整构建；可用授权 Android 设备与人工玩法操作者；至少 16:9 和超宽屏设备；设备分辨率安全区语义复核；菜单至完整牌局/结算真实操作；后台恢复。Wire AOT 历史探针不替代完整游戏 APK。本任务不证明 G2/P4.5 完成，不证明 G4/iOS 或完整整场验收；V4 仍是生产基线。
