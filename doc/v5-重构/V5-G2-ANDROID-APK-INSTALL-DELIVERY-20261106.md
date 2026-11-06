# Android APK 构建、模拟器启动与安装交付

日期：2026-11-06（Asia/Shanghai）

本报告只判定 APK_BUILD、EMULATOR_BOOT、APK_INSTALL 三项。

## 结果

| 项目 | 状态 | 证据 |
|---|---|---|
| APK_BUILD | PASS | `build/c05luna/aa82604ee7a547628cf981557e408f32/apk-build/summary.json`、`build-report.json` |
| EMULATOR_BOOT | PASS | `build/c05luna/aa82604ee7a547628cf981557e408f32/device/20261106-154737-startemulator-9e731979/state.json` |
| APK_INSTALL | PASS | `build/c05luna/aa82604ee7a547628cf981557e408f32/device/install/20261106-154937-install-992516d3/install-result.json` |

### APK_BUILD

- 冻结源码 HEAD：`51f35b3ec0a57e123991cd5f54fd47d121617f6a`（提交仅含验证工具改动；Unity production inputs 与接受基线 `59e8bd741385b5d7b39ccdbc47573dde552067d1` 一致）。
- Unity：`D:/game_dev_tools/unity/Editor/Unity.exe`，项目版本与 BuildReport 均为 `6000.3.27f1`。
- 构建：`BuildResult.Succeeded`，native exit 0，`errors=0`、`stepErrors=0`、`stepExceptions=0`；六个启用生产场景齐全；IL2CPP、x86_64；package `com.majiang.game`，activity `com.unity3d.player.UnityPlayerActivity`，target SDK 36。
- shared plugin provenance 列出 7 个自有 Mahjong DLL 和 `Google.Protobuf.dll`，共 8 个要求 DLL；其余 4 个 DLL 为依赖运行库。来源、长度和 SHA256 在 `apk-build/shared-provenance/plugin-provenance.json`。
- APK：`build/c05luna/aa82604ee7a547628cf981557e408f32/apk-build/Mahjong-emulator-x86_64.apk`，54,188,351 bytes，SHA256 `578B062F5CB4DCCB01ACA48B0E0575F9F5824CD7469B775057726D4AC0EFA3F1`。
- 临时 Unity 工程位于独占短路径 `D:/c05s/aa82604ee7a547628cf981557e408f32/project`。19 个 registry 包由锁文件身份核验后复制；路径审计完整且通过。构建使用 `-job-worker-count 1`。源码、生产输入和源插件构建前后未变。

### EMULATOR_BOOT

- AVD `medium_phone`，serial `emulator-5580`，port `5580`，ABI `x86_64`，`sys.boot_completed=1`。
- 本轮启动器 PID `20472`，start ticks `639295480611493214`；QEMU PID `3972`，start ticks `639295480617942432`。runner state 记录完整 owned process 集和启动参数（read-only、no snapshot load/save、AEHD acceleration）。
- 模拟器由本任务启动并保持运行供用户使用。启动日志目录当前仍活跃，不作为稳定封存文件；state、boot probe 和安装回执已稳定。

### APK_INSTALL

- 实际命令通过 runner 执行 `adb install -r -t <上述 APK>`，adb native exit 0，stdout 为 `Success`，stderr 为空。
- `pm path` 为唯一 `base.apk`；`dumpsys package`：`com.majiang.game`、versionCode `1`、versionName `1.0`、primary ABI `x86_64`、target SDK `36`。
- 设备端 `base.apk` SHA256 与本地 APK 完全一致：`578b062f5cb4dccb01aca48b0e0575f9f5824cd7469b775057726d4ac0efa3f1`。安装回执与原 APK、BuildReport、summary 绑定。

## 执行记录与修复

- 确定性回归：`tools/tests/C05AndroidEmulator.Tests.ps1`，131 项断言、18 个 fixture 全部通过；报告 `build/c05luna/00cabbb7a4dd4865bf4589f2cf85e18c/tests/test-summary.json`。
- 首次生产入口在 Unity 启动前因本树缺少私有 Android CLI/Ninja 工具而 BLOCKED；工具复制到本任务独占 GUID 根并显式传入后继续。该首败保存在 `build/c05luna/e3f0f3d6b5214aba8e168251a53e4837/apk-build/`。
- 最终构建期间 Unity 日志记录到一次 Burst `bcl.exe` 中间任务 exit 4，随后 Bee 继续执行；Gradle `assembleRelease` 成功，Unity native exit 0 且 BuildReport 三项错误计数均为 0。该中间子任务码未被当作构建通过证据。
- 任务范围内未执行游戏玩法、视觉、触控或生命周期验收；也未执行旧 Android 线程的其他 G2 项目。

## 使用与停止

APK 可直接从上述路径安装。模拟器当前保持在线，serial 为 `emulator-5580`。按任务 runner 正常停止本次 owned 实例：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/run-c05-android-emulator.ps1 -Action ShutdownEmulator -Serial emulator-5580 -EmulatorPort 5580 -AvdName medium_phone -EvidenceDir build/c05luna/aa82604ee7a547628cf981557e408f32/device/20261106-154737-startemulator-9e731979 -StateFile build/c05luna/aa82604ee7a547628cf981557e408f32/device/20261106-154737-startemulator-9e731979/state.json -AndroidSdkRoot D:/Android/Sdk -AvdHome D:/Android/avd
```

未卸载应用、未清空用户数据、未修改共享 AVD、未改动 `complete.flag` 或 V5-G2 标签。模拟器运行日志保持标记为活动文件；其余交付摘要、BuildReport、安装回执与测试摘要为稳定证据。

## 复核范围与限制

本轮按要求未委派其他线程；因此本交付的三项结果有真实执行证据和本地自动断言，但没有独立 reviewer 签收。摘要中的 `unityVersion` 字段显示 `UNKNOWN`，而 BuildReport 与 staging `ProjectVersion.txt` 均直接记录 `6000.3.27f1`；以这两个原始来源为准。没有做启动后的游戏画面或交互验收。
