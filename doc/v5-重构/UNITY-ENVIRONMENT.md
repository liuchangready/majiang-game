# Unity 开发环境说明（V5 客户端）

日期：2026-10-22  
适用：本机 Windows 10（19045）+ Unity 6.3 LTS + 共享 C# 规则核心  
状态：**工具链与工程骨架已就绪；Unity 许可尚未激活，工程导入与编译验证待登录后完成。**

> 本文描述本机环境与仓库骨架，不代表 V5 已实施。架构目标与信任边界见
> [V5 架构设计](V5-ARCHITECTURE-DESIGN.md)。V4（Kotlin + libGDX）仍是当前生产基线。

## 1. 目录约定

工具与工程分离：**工具全部在 `D:\Unity`，仓库工程在 `D:\majiang`。**

```text
D:\Unity\
├── Hub\Editor\6000.3.27f1\      Unity 编辑器（15.2 GB，含 IL2CPP 与 Android 模块）
├── dotnet\                      .NET 8 SDK（dotnet.exe，已加入用户 PATH）
├── downloads\                   安装包缓存（约 5.8 GB，可删除）
├── logs\                        Unity 批量模式日志
└── install-log.txt              本次安装记录

D:\majiang\
├── unity\Mahjong.Unity\         Unity 客户端工程（V5）
│   ├── Assets\
│   │   ├── Scripts\{App,UI,Table,Spectator,Platform}   表现层程序集（asmdef）
│   │   ├── Plugins\Mahjong\                            共享 C# 程序集（构建产物）
│   │   ├── Scenes\SampleScene.unity                    2D URP 模板场景
│   │   └── Settings\                                   URP 渲染管线资源
│   ├── Packages\manifest.json
│   └── ProjectSettings\                                已改为 1280×720 窗口、可缩放
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
| Unity Hub | 3.22.3.65535 | 系统 MSIX（按用户） | 首次启动需登录 Unity ID |
| Unity 编辑器 | 6000.3.27f1 (LTS) | `D:\Unity\Hub\Editor\6000.3.27f1` | `Unity.exe -version` 已确认 |
| Windows Build Support (IL2CPP) | 6000.3.27f1 | `Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_player_development_il2cpp` | 桌面发行版构建 |
| Android Build Support | 6000.3.27f1 | `Editor\Data\PlaybackEngines\AndroidPlayer` | 未内置 SDK/NDK，见第 8 节 |
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

# 打开 Unity 工程（需已激活许可）
powershell -ExecutionPolicy Bypass -File tools\unity\open-project.ps1

# 在新机器上复现同样的编辑器安装（含模块与签名校验）
powershell -ExecutionPolicy Bypass -File tools\unity\install-editor.ps1 `
    -EditorVersion 6000.3.27f1 -Changeset 672d562a42a7 -Modules windows-il2cpp,android

# 批量模式编译 / 导入（需已激活许可）
& "D:\Unity\Hub\Editor\6000.3.27f1\Editor\Unity.exe" -batchmode -quit -nographics `
    -projectPath "D:\majiang\unity\Mahjong.Unity" -logFile "D:\Unity\logs\batch-open.log"
```

安装脚本使用 `__COMPAT_LAYER=RunAsInvoker` 以当前（已过滤的）管理员令牌运行 Unity 安装器，
避开 UAC 弹窗并把文件写入 D 盘；每个安装包先做 Authenticode 校验再执行。

## 6. 首次启动：Unity 许可（必须人工一次）

Unity 需要登录 Unity ID 并激活许可后才能打开工程或批量构建；许可绑定账号，脚本无法代替。

1. 从开始菜单启动 **Unity Hub**（首次启动要求登录）。
2. 登录 Unity ID，激活 **Personal** 许可。
3. 在 *Installs* 中确认安装位置为 `D:\Unity\Hub\Editor`。本次编辑器是脚本直装的，
   Hub 初次可能不列出它；用 *Locate*（或 *Add* → *Locate*）指向
   `D:\Unity\Hub\Editor\6000.3.27f1` 即可注册。
4. 打开 `D:\majiang\unity\Mahjong.Unity`，等待包解析与首次导入。

未激活时的实测证据（2026-10-22 23:49）：

```text
[Licensing::Client] Error: Code 404 while processing request
No valid Unity Editor license found. Please activate your license.
Exiting ... return code 198
```

激活后重新运行 `check-env.ps1`，`Unity license` 一项应变为 PASS。

## 7. 验证记录

| 项 | 结果 | 证据 |
|---|---|---|
| Unity Hub 安装 | 通过 | MSIX `3.22.3.65535` 已注册 |
| 编辑器安装 | 通过 | `Unity.exe -version` → `6000.3.27f1`；目录 15.2 GB |
| Windows IL2CPP 模块 | 通过 | `Variations\win64_player_development_il2cpp` 存在 |
| Android 模块 | 通过 | `PlaybackEngines\AndroidPlayer` 存在；SDK/NDK 未内置 |
| .NET 共享库构建 | 通过 | 5 个 `netstandard2.1` 程序集，0 警告 0 错误 |
| 共享 DLL 进入工程 | 通过 | `Assets\Plugins\Mahjong` 下 5 个 DLL + 5 个 PDB |
| Unity 工程骨架 | 通过 | 2D URP 模板（URP 17.0.3 + Input System 1.12.0）+ 5 个 asmdef |
| 工程设置 | 通过 | 1280×720、窗口化、可缩放、后台运行；productName 已改 |
| 编辑器批量模式打开工程 | **待许可** | 退出码 198，日志见第 6 节 |
| Android 真机构建 | 未做 | 依赖许可与 SDK/NDK 配置 |

## 8. 已知缺口与后续

- **Unity 许可未激活**：唯一阻塞工程导入与编译的项，需要人工登录一次。
- **Android SDK/NDK**：模块只带 Gradle 模板，未内置 SDK/NDK。可在
  *Edit → Preferences → External Tools* 指向 V4 已有的
  `C:\Users\<user>\AppData\Local\Android\Sdk`（当前有 platform 36、build-tools，但无 NDK），
  或为该编辑器补装 SDK & NDK 工具模块；NDK 版本必须与 Unity 6.3 要求一致。
- **iOS**：需要 macOS + Xcode，本机不覆盖。
- **资源导入**：`assets/` 下 50 张 PNG 与 1 个 TTF 尚未导入；导入时需确定 Sprite 模式、
  Pixels Per Unit、字体渲染方式，并与 V4 的 1280×720 虚拟坐标对齐。
- **渲染管线**：模板使用 URP 2D + Linear 色彩空间；2D 麻将美术若出现色彩偏差，需要统一
  Sprite 导入设置与色彩空间，而不是逐图校正。
- **CI**：尚未配置无头构建；需要 Unity 许可（或 License Server）与 Library 缓存策略。
- **LFS**：`.gitattributes` 目前只统一换行与 diff 驱动，未启用二进制 LFS 跟踪。
- **工程骨架之外的代码**：`dotnet/src` 与 `Assets/Scripts` 目前只有工程/程序集边界，没有任何
  规则或 UI 实现；V5-0（黄金样本盘点）与 V5-1（共享核心）尚未开始。

## 9. 建议的下一步

1. 人工激活许可，运行 `open-project.ps1` 完成首次导入，确认控制台无编译错误。
2. 用 `SampleScene` 验证 URP 2D 渲染、1280×720 Game 视图与 Windows IL2CPP 构建。
3. 按 V5-0 从 V4 抽取语言中立黄金样本（输入、随机种子、预期事件、状态摘要）。
4. 再开始 V5-1 的 `Mahjong.Domain` C# 实现，避免边写规则边调环境。
