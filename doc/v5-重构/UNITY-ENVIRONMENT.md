# Unity 开发环境说明（V5 客户端）

日期：2026-10-23
适用：本机 Windows 10（19045）+ Unity 6.3 LTS + 共享 C# 规则核心  
状态：**工具链可用；Unity Personal 许可已激活；工程可导入、编译并构建 Windows 播放器。IL2CPP 与 Android 尚待补工具链。**

> 本文描述本机环境与仓库骨架，不代表 V5 已实施。架构目标与信任边界见
> [V5 架构设计](V5-ARCHITECTURE-DESIGN.md)。V4（Kotlin + libGDX）仍是当前生产基线。

## 1. 目录约定

工具与工程分离：**工具全部在 `D:\Unity`，仓库工程在 `D:\majiang`。**

```text
D:\Unity\
├── Hub\Editor\6000.3.27f1\      Unity 编辑器（约 15.2 GB，含 IL2CPP 与 Android 模块）
├── dotnet\                      .NET 8 SDK（dotnet.exe，已加入用户 PATH）
├── downloads\                   安装包缓存（约 5.8 GB，可删除）
├── builds\                      冒烟构建产物（EnvCheck / EnvCheck2）
├── logs\                        Unity 批量模式与构建日志
└── install-log.txt              编辑器安装记录

D:\majiang\
├── unity\Mahjong.Unity\         Unity 客户端工程（V5）
│   ├── Assets\
│   │   ├── Scripts\{App,UI,Table,Spectator,Platform}   表现层程序集（asmdef）
│   │   ├── Plugins\Mahjong\                            共享 C# 程序集（DLL + PDB + .meta）
│   │   ├── Scenes\SampleScene.unity                    2D URP 模板场景
│   │   └── Settings\                                   URP 渲染管线资源
│   ├── Packages\manifest.json                          已对齐 6.3 推荐版本
│   └── ProjectSettings\                                1280×720 窗口、可缩放、Burst AOT 设置
├── dotnet\                      共享 C# 解决方案（5 个 netstandard2.1 工程）
├── tools\unity\                 安装、构建、检查、启动脚本
└── engine\ protocol\ ...        V4 Kotlin 实现（冻结，作为对照与黄金样本来源）
```

Unity Hub 由 winget 以 **MSIX** 方式按用户安装，位置由系统固定在
`C:\Program Files\WindowsApps\UnityTechnologies.UnityHub_*`，无法改到 D 盘；其体积很小，
编辑器本体仍安装在 D 盘。

## 2. 已安装组件

| 组件 | 版本 | 位置 | 说明 |
|---|---|---|---|
| Unity Hub | 3.22.4.65535 | 系统 MSIX（按用户） | 已登录并激活 Personal |
| Unity 编辑器 | 6000.3.27f1 (LTS) | `D:\Unity\Hub\Editor\6000.3.27f1` | `Unity.exe -version` 已确认 |
| Windows Build Support (IL2CPP) | 6000.3.27f1 | `PlaybackEngines\windowsstandalonesupport\Variations\win64_player_development_il2cpp` | 模块已装，但缺 MSVC |
| Android Build Support | 6000.3.27f1 | `PlaybackEngines\AndroidPlayer` | 未内置 SDK/NDK |
| .NET SDK | 8.0.426 | `D:\Unity\dotnet` | 只作构建器，产物为 `netstandard2.1` |
| Git / Git LFS | 2.51.0 / 3.7.0 | `D:\Git` | LFS 可用，尚未启用跟踪 |

安装包与编辑器均通过 Authenticode 校验（签发者 `CN=Unity Technologies SF`）。

## 3. 版本选择理由

- **Unity 6.3 LTS**：当前仍在维护的 LTS 流（6.0 LTS 已接近支持末期），桌面与移动同源；
  官方跨平台 API 配置为 **.NET Standard 2.1**，与共享库下限一致。
- **.NET Standard 2.1 + C# 9**：Unity 6 脚本编译上限，共享程序集必须遵守，否则无法加载；
  服务器专用工程可以用更高版本，但新 API 不得渗入共享公开接口。
- **.NET 8 SDK**：仅作为构建器；产物与 Unity 运行时解耦。

## 4. 共享 C# 程序集与 Unity 的衔接

```text
dotnet\src\Mahjong.Domain      ─┐
dotnet\src\Mahjong.Contracts    ├─ dotnet build ─→ tools\unity\build-shared.ps1
dotnet\src\Mahjong.TableRuntime │                 └─→ unity\...\Assets\Plugins\Mahjong\*.dll(+pdb)
dotnet\src\Mahjong.Client       │
dotnet\src\Mahjong.LocalHost   ─┘
```

- 共享库只依赖 BCL，不依赖 `UnityEngine`；Unity 侧以预编译 DLL 引用，PDB 一并复制以便断点调试。
- 表现层 asmdef：`Mahjong.Unity.App` 依赖 `UI / Table / Spectator / Platform`，其余四个互不依赖；
  业务代码只消费 `Mahjong.Client` 的视图与命令门面，不直接改权威状态。
- 修改共享库后必须重新执行 `build-shared.ps1`，Unity 才会看到新 DLL。

## 5. 常用命令

```powershell
# 检查工具链（只读，必需项失败时退出码为 1）
powershell -ExecutionPolicy Bypass -File tools\unity\check-env.ps1

# 构建共享程序集并复制到 Assets\Plugins\Mahjong
powershell -ExecutionPolicy Bypass -File tools\unity\build-shared.ps1 -Configuration Release

# 打开 Unity 工程
powershell -ExecutionPolicy Bypass -File tools\unity\open-project.ps1

# 在新机器上复现同样的编辑器安装（含模块与签名校验）
powershell -ExecutionPolicy Bypass -File tools\unity\install-editor.ps1 `
    -EditorVersion 6000.3.27f1 -Changeset 672d562a42a7 -Modules windows-il2cpp,android

# 批量导入 / 编译（无玩家构建）
& "D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe" -batchmode -quit -nographics `
    -projectPath "D:\majiang\unity\Mahjong.Unity" -logFile "D:\Unity\logs\batch-open.log"

# 构建 Windows 播放器
& "D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe" -batchmode -quit -nographics `
    -projectPath "D:\majiang\unity\Mahjong.Unity" `
    -buildWindows64Player "D:\Unity\builds\EnvCheck\MahjongCheater.exe" `
    -logFile "D:\Unity\logs\build-win.log"
```

安装脚本使用 `__COMPAT_LAYER=RunAsInvoker` 以当前（已过滤的）管理员令牌运行 Unity 安装器，
避开 UAC 弹窗并把文件写入 D 盘；每个安装包先做 Authenticode 校验再执行。
注意 PowerShell 不会等待 GUI 子进程，脚本/批量调用请用 `Start-Process -Wait` 或检查日志尾部的
`Build Finished, Result: Success`。

## 6. 许可（已激活）

2026-10-23 00:18 通过 Unity Hub 登录并激活 **Unity Personal**。许可实体为：

```text
%LOCALAPPDATA%\Packages\UnityTechnologies.UnityHub_2vrhnee42bhxm\
    LocalCache\Local\Unity\licenses\UnityEntitlementLicense.xml
```

其中 `EntitlementGroup ... Product="Unity Personal"`；`check-env.ps1` 会读取该文件并显示时间戳。

换机或重装时需要重新登录一次：

1. 启动 **Unity Hub** → 登录 Unity ID。
2. *Settings → Licenses → Add license → Get a free personal license*。
3. Hub 的 *Installs* 若未列出脚本直装的编辑器，用 *Locate* 指向 `D:\Unity\Hub\Editor\6000.3.27f1`。

## 7. 工程状态与验证记录

模板来自编辑器自带的 `com.unity.template.2d-cross-platform-2d-6.1.6`，其包版本是按 6.1 锁定的，
在 6.3 上会直接编译失败（`InputSystem` 引用 `BuildTarget.ReservedCFE`、`collab-proxy` 的
`ObjectInfo` 歧义）。已把全部依赖升级到 6.3 编辑器清单推荐的版本：

| 包 | 原版本 | 现版本 |
|---|---|---|
| com.unity.inputsystem | 1.12.0 | 1.20.1 |
| com.unity.collab-proxy | 2.6.0 | 2.13.6 |
| com.unity.render-pipelines.universal | 17.0.3 | 17.3.0 |
| com.unity.2d.animation | 10.1.4 | 13.0.6 |
| com.unity.2d.psdimporter | 10.0.0 | 12.0.2 |
| com.unity.2d.spriteshape | 10.0.7 | 13.0.0 |
| com.unity.2d.aseprite | 1.2.1 | 3.0.2 |
| com.unity.2d.tilemap.extras | 4.2.1 | 6.0.3 |
| com.unity.test-framework | 1.4.5 | 1.6.0 |
| com.unity.timeline | 1.8.7 | 1.8.13 |
| com.unity.visualscripting | 1.9.5 | 1.9.12 |

| 项 | 结果 | 证据 |
|---|---|---|
| Unity Hub 安装与登录 | 通过 | MSIX `3.22.4.65535`，账号已激活 Personal |
| Unity 许可 | 通过 | `UnityEntitlementLicense.xml`（2026-10-23 00:18） |
| 编辑器安装 | 通过 | `Unity.exe -version` → `6000.3.27f1` |
| IL2CPP / Android 模块 | 通过 | 两个 PlaybackEngines 目录存在 |
| 工程批量导入 | 通过 | 退出码 0，0 个 `error CS` |
| 包解析 | 通过 | `packages-lock.json` 全部为 6.3 推荐版本 |
| 共享程序集构建 | 通过 | 5 个 `netstandard2.1` 程序集，0 警告 0 错误 |
| 共享 DLL 进入工程 | 通过 | 5 DLL + 5 PDB + Unity 生成的 `.meta` |
| Windows 播放器构建 | 通过 | `Build Finished, Result: Success`，产物 `D:\Unity\builds\EnvCheck2` |
| 播放器冒烟 | 通过 | 以 `-batchmode -nographics` 启动并稳定运行 15 秒 |
| IL2CPP 构建 | 未验证 | 缺 Visual Studio C++ 工具链（MSVC） |
| Android 构建 | 未验证 | 缺 SDK/NDK |
| iOS 构建 | 不覆盖 | 需要 macOS + Xcode |

构建中遇到过一次 `Burst internal compiler error: UnauthorizedAccessException`，路径为
`Library\Bee\artifacts\WinPlayerBuildProgram\AsyncPluginsFromLinker`（Burst 1.8.31 的 AOT 链接步骤
与该增量构建暂存目录同名冲突）。删除 `Library\Bee` 后重新构建即通过；随后把 Burst AOT 关闭再打开
各构建一次均成功，因此判断为首次构建的暂存竞态，而非稳定故障。若复现：删除 `Library\Bee`，
并用 `Start-Process -Wait` 确认构建真正结束后再重试。

## 8. 已知缺口与后续

- **IL2CPP 缺 MSVC**：IL2CPP 模块已装，但本机没有 Visual Studio / MSVC。构建 IL2CPP 播放器或
  Android IL2CPP 前，需要安装 Visual Studio 2022/2026 Build Tools 的“使用 C++ 的桌面开发”工作负载
  （需要一次 UAC 授权）。
- **Android SDK/NDK**：模块只带 Gradle 模板，未内置 SDK/NDK。可在
  *Edit → Preferences → External Tools* 指向 V4 已有的
  `C:\Users\<user>\AppData\Local\Android\Sdk`（当前有 platform 36、build-tools，但无 NDK），
  或为该编辑器补装 SDK & NDK 工具；NDK 版本必须与 Unity 6.3 要求一致。
- **iOS**：需要 macOS + Xcode，本机不覆盖。
- **资源导入**：`assets/` 下 50 张 PNG 与 1 个 TTF 尚未导入；导入时需确定 Sprite 模式、
  Pixels Per Unit、字体渲染方式，并与 V4 的 1280×720 虚拟坐标对齐。
- **渲染管线**：模板使用 URP 2D + Linear 色彩空间；2D 麻将美术若出现色彩偏差，需要统一
  Sprite 导入设置与色彩空间，而不是逐图校正。
- **CI**：尚未配置无头构建；需要 Unity 许可（或 License Server）与 Library 缓存策略。
- **LFS**：`.gitattributes` 目前只统一换行与 diff 驱动，未启用二进制 LFS 跟踪。
- **代码**：`dotnet/src` 与 `Assets/Scripts` 目前只有工程/程序集边界，没有规则或 UI 实现；
  V5-0（黄金样本盘点）与 V5-1（共享核心）尚未开始。

## 9. 建议的下一步

1. 用 `open-project.ps1` 在编辑器里确认 Game 视图为 1280×720、SampleScene 渲染正常。
2. 安装 VS Build Tools（C++ 工作负载），补一次 IL2CPP 构建验证。
3. 按 V5-0 从 V4 抽取语言中立黄金样本（输入、随机种子、预期事件、状态摘要）。
4. 再开始 V5-1 的 `Mahjong.Domain` C# 实现，避免边写规则边调环境。
