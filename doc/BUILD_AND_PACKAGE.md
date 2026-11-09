# 编译与出包

核对起点：清理前快照 `e074806a39a674b898120c828038efcd1b5c58d7`。以下命令在当前任务工作树根目录执行；先核对 HEAD、工作树状态和脚本参数。`D:/majiang` 的其他 checkout 可能有独立修改，不能直接在那里执行。

## 工具和构建前置

| 工具 | 当前已验证路径 / 版本 |
|---|---|
| Unity | `D:/game_dev_tools/unity/Editor/Unity.exe`，`6000.3.27f1` |
| .NET SDK | `D:/game_dev_tools/dotnet/dotnet.exe`，本机 SDK `8.0.426` |
| Android SDK | `D:/Android/Sdk` |
| Android AVD | `D:/Android/avd`，`medium_phone`，API 36 / x86_64 |
| 私有 Android 工具链 | 例如 `build/android-toolchain/private`，须实际含脚本要求的 CLI/Ninja 文件 |

先检查空间、内存、Unity 许可是否可识别、已有进程与共享锁。长构建先填[任务模板](LONG_VALIDATION_TASK_TEMPLATE.md)。任务输出用新的 GUID 目录；源码/插件来源与干净候选守卫必须保留。正确 Unity 项目是 `unity/Mahjong.Unity`，启动时将其绝对路径作为一个参数，不把仓库根当 Unity 项目。

## .NET 编译与共享 DLL

只编译 .NET solution：

```powershell
& 'D:/game_dev_tools/dotnet/dotnet.exe' build dotnet/Mahjong.sln -c Release
```

这不等于更新 Unity 插件。改过 `dotnet/`、插件过期或来源记录缺失时，在 Unity 构建/测试前执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-shared.ps1 `
  -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe

powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-shared-plugins.ps1 `
  -Configuration Release
```

插件写入 `unity/Mahjong.Unity/Assets/Plugins/Mahjong/`；来源记录位于工程 `Library/SharedPluginProvenance/`。不要手工拼接旧 DLL 或删除守卫绕过来源不一致。

## Windows Player

现有带共享锁和构建证据的入口：

```powershell
$runId = [guid]::NewGuid().ToString('N')
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-c04-privacy-matrix.ps1 `
  -UnityPath D:/game_dev_tools/unity/Editor/Unity.exe `
  -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe `
  -BuildPlayer -RunRoot "build/$runId"
```

输出为 `build/<GUID>/player/Mahjong.exe` 和同目录完整依赖；交付时保留整个 player 目录，不能只拷贝 exe。构建根还包含 summary、BuildReport、插件来源、包文件清单与日志。构建脚本可自动复用其所有权标记管理的 Library 缓存；是否允许复用仍以具体验收合同为准。

编辑器中打开 `unity/Mahjong.Unity` 并加载 Boot 后可进入正常菜单；与构建 Player 的结果分别记录。

## Android 模拟器 APK

构建使用 x86_64 / IL2CPP。源码工作树与 staging 须通过长路径预算；Android 新构建宜使用如 `D:/g2x` 的短任务工作树。不要因缓存问题重装环境或改全局虚拟化/分页文件。

```powershell
$runId = [guid]::NewGuid().ToString('N')
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/build-c05-android-emulator.ps1 `
  -UnityPath D:/game_dev_tools/unity/Editor/Unity.exe `
  -OutputRoot "build/$runId" `
  -PrivateToolchainRoot build/android-toolchain/private `
  -AndroidSdkRoot D:/Android/Sdk -AvdHome D:/Android/avd
```

前置未满足时应保留 BLOCKED 原因；上例不会自动创建私有工具链。当前脚本自动选择受管理的冷/增量 staging，必要时显式提供短 `StagingRoot`。只有 materialized PackageCache 缺失且已有经核验的种子时才传 `RegistryCacheSource`。不删除缓存或旧证据作为常规重试步骤。

当前 `JobWorkerCount` 默认 8，并有内存闸门；不要照搬旧任务的 worker=1，也不要未经资源核对提高 Bee 编译线程。构建与运行模拟器前分别查询既有实例，复用有所有权记录的实例。

产物为 `build/<GUID>/Mahjong-emulator-x86_64.apk`，同根保留 `summary.json`、`build-report.json` 和 Unity 日志。验证/安装使用 `tools/unity/run-c05-android-emulator.ps1`，按 Probe → 查询/启动自有模拟器 → ValidateArtifact → Install → Start → Capture/Input/BackgroundResume 的实际需要执行，并传入同包 APK、设备 serial 和原 state 文件；无需每次重复构建或启动模拟器。

ARM64 真机包的已有脚本是 `tools/unity/build-p45-android-game.ps1`，其参数、插件闭包与设备证据必须单独核对；x86_64 模拟器结果不能代表 ARM64 真机。iOS 未在当前 Windows 环境形成真机验收结果。

## 出包判定

- Unity 退出码、BuildReport 结果与错误计数、启用场景、共享 DLL 和完整包清单必须一致。
- 保存源码/生成输入身份、8 个运行时 DLL（含 Google.Protobuf）、资源/插件、全包哈希、实际命令和原生退出码。
- 6 个正式场景为 Boot / Menu / Lobby / Table / Spectator / Settlement。
- 构建成功、安装成功和游戏可用是三种证据。画面、输入、完整玩法、恢复及最终同包验收见[测试说明](TESTING.md)。

旧 Kotlin/libGDX 七模块、Gradle wrapper、桌面启动器及旧 Android 启动器已不在当前源码树中；本仓库没有 Gradle 桌面或 Android 游戏构建入口。Unity Android 支持及其生成的 Gradle 模板仍由 Unity 构建流程使用，不能与旧根级 Gradle 游戏方案混淆。
