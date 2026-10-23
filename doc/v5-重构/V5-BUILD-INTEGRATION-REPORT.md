# V5 构建测试集成报告（wt/v5-build-integration）

- 分支：`wt/v5-build-integration`（独立 worktree `D:\majiang-wt\build-integration`）
- 基线：`92f52fe`（docs(v5): record durable outbox round 2 verification）
- 日期：2026-10-23
- 结论：**本地全部验证 PASS（.NET 侧 461 用例 0 失败）；CI 与 Unity 侧为 NOT_RUN/BLOCKED，未宣称通过**（详见 §6/§7）。

## 1. 范围与路径台账

本任务仅修改以下 6 个路径（即 expectedFiles），未触碰其他已跟踪文件：

| # | 路径 | 变更 |
|---|---|---|
| 1 | `dotnet/Mahjong.sln` | 纳入 4 个项目（见 §4） |
| 2 | `tools/unity/build-shared.ps1` | 重写：依赖闭包复制 + 下限/冲突防护 + 可指定输出目录 |
| 3 | `tools/verify-v5.ps1` | 新增：统一验证入口 |
| 4 | `tools/v5-verify.manifest.json` | 新增：本任务所需配置清单 |
| 5 | `.github/workflows/v5-verify.yml` | 新增：CI 调用统一验证入口 |
| 6 | `doc/v5-重构/V5-BUILD-INTEGRATION-REPORT.md` | 新增：本报告 |

明确不做（与任务约束一致）：

- 不改 WSS transport、测试源码、既有 csproj（`Mahjong.Client.Tests.csproj` 内"solution 文件超出范围"的注释已过时，见 §9）。
- 不改/不引用 P5.3（durable outbox 返修中）未提交源码；基线中已有的 `Mahjong.Outbox.CrashWorker` 仅作为 helper 纳入构建调度。
- 不硬引用尚不存在的 `dotnet/tests/Mahjong.Client.Transport.Tests`（DeepSeek 并行开发中）。
- 未触碰 `D:\majiang` 主工作树、其他 worktree；未切分支/合并/推送/建 MR；验证全程未覆盖已跟踪的 Unity Plugins DLL / ProjectSettings（`git diff unity/` 全程为空）。

## 2. 环境

| 项 | 值 |
|---|---|
| dotnet SDK | 8.0.426（`D:\Unity\dotnet\dotnet.exe`，可通过 `-DotnetPath` 传入；未传时按 DOTNET_ROOT/PATH 解析） |
| 共享库下限 | netstandard2.1 / C# 9（`dotnet/Directory.Build.props`） |
| 测试框架 | xunit.v3 4.0.2（Microsoft.Testing.Platform 自宿主 Exe 工程，net8.0） |
| OS | Windows 10 Pro（PowerShell 5.1 兼容编写，两个脚本均在 5.1 下运行验证） |

## 3. 交付内容

### 3.1 `dotnet/Mahjong.sln`

新增 4 个项目（带完整 Debug/Release 配置映射，归入既有 src/tests 解决方案文件夹）：
`Mahjong.Ai`、`Mahjong.Ai.Tests`、`Mahjong.Client.Tests`、`Mahjong.Outbox.CrashWorker`。解决方案从 8 个项目增至 12 个，构建退出码 0。

### 3.2 `tools/unity/build-shared.ps1`

- 复制闭包：7 个共享程序集（Domain / Contracts / Contracts.Wire / TableRuntime / Client / LocalHost / **Ai**）+ 运行时依赖。
- 依赖解析：读取各工程 `obj/project.assets.json` 的 `netstandard2.1` 目标，仅取 `type == "package"` 的 NuGet 条目（过滤 ProjectReference 占位路径），从 NuGet 全局缓存定位实际 DLL。本次闭包解析出 `Google.Protobuf 3.28.3` 与 4 个传递依赖（System.Buffers / System.Memory / System.Numerics.Vectors / System.Runtime.CompilerServices.Unsafe）。
- 禁止复制：测试工程、`Mahjong.Outbox.CrashWorker`、xunit/MTP 运行器、ApplicationInsights、Grpc.*、Google.Protobuf.Tools（即 SDK/工具链一律不进插件目录）。
- 防护（缺失即 throw、退出非 0）：TFM 必须 netstandard2.1；C# 语言版本 ≤9.0（csproj 显式覆盖优先于 Directory.Build.props）；同名 DLL SHA-256 不一致视为闭包冲突；必需集合缺失（含 Google.Protobuf.dll）报错。
- 新参数 `-PluginOutputDir`：可指定插件输出目录；默认仍为 `unity\Mahjong.Unity\Assets\Plugins\Mahjong`，验证时传临时目录以避免覆盖已跟踪资产。

### 3.3 `tools/verify-v5.ps1`（统一验证入口）

```
powershell -ExecutionPolicy Bypass -File tools\verify-v5.ps1 [-Configuration Release] [-DotnetPath <path>]
           [-ManifestPath tools\v5-verify.manifest.json] [-LogDir artifacts\verify-v5\<ts>] [-RunnerMode auto|dotnetTest|direct]
           [-SkipPluginCopyCheck]
```

- 清单项目 + 磁盘发现项目（`dotnet/tests/**/*.Tests.csproj` 等 3 种模式）合并为验证清单，标注 `Source=manifest|discovered`；新测试工程落入模式即自动参与验证（Transport.Tests 合并后无需改脚本）。
- 必需项目缺失（磁盘或 sln）→ FAIL，不静默。
- 逐项目：sln 构建退出码；测试工程执行 `dotnet test`（英文输出 `DOTNET_CLI_UI_LANGUAGE=en`），解析 vstest 真实用例计数；`dotnet test` 失败时记录原因并回退到同一程序集的官方 in-process runner（xunit.v3 MTP 自宿主直跑），再次解析真实计数；**Total=0 判 FAIL（零用例不报绿）；未运行的测试一律不记通过**。
- `Mahjong.Outbox.CrashWorker` 按 manifest `kind=helper` 识别为 console helper（Client.Tests 重启测试的子进程），仅验证构建、不计用例。
- 插件复制检查：以临时目录为 `-PluginOutputDir` 子进程调用 build-shared.ps1，校验必需程序集存在 + 禁止模式不存在；已跟踪 Unity 资产全程不被触碰。
- 汇总表 + `summary.log`：每行含 KIND/必需性/是否在 sln/runner/真实 Total/Failed/Skipped/RESULT/日志路径。退出码：0=全 PASS，1=任一 FAIL，2=环境受阻。

### 3.4 `tools/v5-verify.manifest.json`

仅含本任务所需配置：solution 路径、5 个项目（4 个必需测试工程 + 1 个必需 helper，注明 helper 不贡献用例）、插件复制必需集合与禁止模式、direct runner 参数。schemaVersion=1，可演进。

### 3.5 `.github/workflows/v5-verify.yml`

windows-latest + .NET 8 SDK，调用与本地完全相同的 `tools/verify-v5.ps1 -Configuration Release`，`if: always()` 上传 `artifacts/verify-v5/**` 日志；注释明确**不包含 Unity/IL2CPP/Android/iOS 平台声明**。本地未执行远端 CI → 本次 CI 状态记 **NOT_RUN**。

## 4. 项目清单与真实测试数（本机实测）

dotnet：`D:\Unity\dotnet\dotnet.exe`（8.0.426）。Release 与 Debug 两次完整验证均 PASS，计数一致。

| 项目 | 类型 | 在 sln | 执行方式 | Total | Failed | Skipped | 结果 |
|---|---|---|---|---|---|---|---|
| Mahjong.Domain.Tests | test | 是 | dotnet test | 233 | 0 | 0 | PASS |
| Mahjong.Contracts.Tests | test | 是 | dotnet test | 82 | 0 | 0 | PASS |
| Mahjong.Ai.Tests | test | 是 | dotnet test | 14 | 0 | 0 | PASS |
| Mahjong.Client.Tests | test | 是 | dotnet test | 132 | 0 | 0 | PASS |
| Mahjong.Outbox.CrashWorker | helper | 是 | 仅构建 | — | — | — | PASS |
| **合计** | | | | **461** | **0** | **0** | **OVERALL PASS (exit 0)** |

同一程序集 in-process direct runner 复核（`-RunnerMode direct`）：计数逐项一致（233/82/14/132），OVERALL PASS exit 0 —— 证明两条 runner 路径等价；本机 `dotnet test` 可用，未触发 NamedPipe 限制（若触发，脚本会记录 dotnet test 失败原因并回退 direct，不把未运行记通过）。

## 5. 构建与复制证据

命令与退出码（日志在 `artifacts/verify-baseline/`、`artifacts/verify-v5/<时间戳>/`；`*.log` 已被 gitignore，仅本地留存）：

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build dotnet\Mahjong.sln -c Release`（经 verify-v5） | 0 | 12 项目 0 错误 0 警告 |
| `tools\verify-v5.ps1 -Configuration Release`（终版干净运行） | 0 | OVERALL PASS，见 §4 |
| `tools\verify-v5.ps1 -Configuration Debug` | 0 | OVERALL PASS，计数一致 |
| `tools\verify-v5.ps1 -Configuration Release -RunnerMode direct` | 0 | OVERALL PASS，计数一致 |
| `tools\unity\build-shared.ps1 -PluginOutputDir <临时目录>`（经 verify-v5 子进程） | 0 | 12 个 DLL + PDB |

插件目录终态（临时目录，非已跟踪路径；`git diff unity/` 为空）：

- Mahjong 侧 7：Domain / Contracts / **Contracts.Wire** / TableRuntime / Client / LocalHost / **Ai**（Ai 本次新纳入闭包）
- 包依赖 5：**Google.Protobuf 3.28.3**、System.Buffers 4.4.0、System.Memory 4.5.3、System.Numerics.Vectors 4.4.0、System.Runtime.CompilerServices.Unsafe 4.5.2（按 assets.json 实际依赖闭包解析）
- 禁止物核验：无测试程序集、无 CrashWorker、无 xunit/MTP/AppInsights/Grpc.Tools/SDK 产物。

## 6. 负向验证（失败必须可见，不得静默全绿）

用真实临时探针工程实测（探针已删除，仅留日志）：

| 场景 | 结果 |
|---|---|
| 探针工程含 1 个失败用例（Probe 2 用例 1 失败） | 该行 FAIL，OVERALL FAIL，exit 1 |
| 探针工程零用例（无 [Fact]） | FAIL"zero executed test cases"，exit 1 |
| 必需项目缺失（manifest 声明但磁盘不存在） | FAIL"required project missing from disk"，exit 1 |
| 探针工程编译失败 | FAIL"test project build failed under dotnet test"（编译错误归因，不误报 BLOCKED），exit 1 |
| 新工程自动发现 | 5 manifest + 2 discovered 合计 7 行，均在表中可见 |

防护下限探针（伪工程树内端到端触发）：

| 场景 | 结果 |
|---|---|
| 共享库 TFM 改 net8.0 | throw"must stay on netstandard2.1"，exit 1 |
| LangVersion 改 12 | throw"exceeds the shared-library C# 9 floor"，exit 1 |
| 同名 DLL 内容不同（Google.Protobuf 3.28.3 vs 3.27.0 逻辑探针；离线缓存无双版本包，端到端不可触发，故用与 build-shared 完全相同的 Add-Candidate/SHA-256 代码路径验证） | throw"Conflicting copies of assembly 'Google.Protobuf.dll' ... 3.28.3[953AACA8], 3.27.0[18911834]" |
| 真实树回归（同包同内容多工程贡献） | 静默去重，终版复制 PASS |

## 7. 状态口径（PASS / FAIL / BLOCKED / NOT_RUN）

| 项 | 状态 | 说明 |
|---|---|---|
| sln 构建（Release/Debug） | PASS | exit 0，日志在案 |
| 461 测试用例（4 工程） | PASS | 真实计数，dotnet test 与 direct runner 双路径一致 |
| CrashWorker helper 构建 | PASS | 仅构建，不计用例 |
| 插件闭包复制（临时目录） | PASS | 12 DLL 必需物齐全、禁止物为零 |
| 下限/冲突防护 | PASS | §6 探针 |
| GitHub Actions CI | **NOT_RUN** | 本地未执行远端 CI；workflow 仅静态校验，推送后由 GitHub 首跑验证 |
| Unity Editor 加载新插件集 / IL2CPP / 各平台 | **BLOCKED / NOT_RUN** | 本机无 Unity 许可（授权检查失败），无 IL2CPP 构建环境；**不作任何平台通过声明**。link.xml 已含 Wire/Google.Protobuf 的 ignoreIfMissing，二者现在实际存在，保形生效 |

不把测试端点/本地验证当真实 TableService 或全链验收；本报告仅覆盖 .NET 构建测试集成与插件复制闭包。

## 8. 本地复现命令

```powershell
# 完整验证（构建 + 461 用例 + 插件闭包临时目录复制检查）
powershell -ExecutionPolicy Bypass -File tools\verify-v5.ps1 -Configuration Release
# 指定 SDK
powershell -ExecutionPolicy Bypass -File tools\verify-v5.ps1 -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe
# 仅插件复制
powershell -ExecutionPolicy Bypass -File tools\unity\build-shared.ps1 -PluginOutputDir <临时目录>
```

日志：`artifacts\verify-v5\<时间戳>\summary.log`（总表）+ 每步独立日志。

## 9. 遗留集成项（单文件合并事项）

1. **过时注释（csproj，3 个文件）**：`dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj`、`dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj`、`dotnet/src/Mahjong.Outbox.CrashWorker/Mahjong.Outbox.CrashWorker.csproj` 中"solution 文件超出范围、有意不加 sln"的注释现已失真（已纳入 sln）。csproj 不在本任务范围内，建议后续单文件清理。
2. **Transport.Tests 免编辑接入**：DeepSeek 的 `dotnet/tests/Mahjong.Client.Transport.Tests` 合并后即被发现模式自动纳入验证；若需设为必需，在 manifest `projects` 追加一行即可（同样单文件）。
3. **sln 是后续合并点**：任何新工程需要合并进 `dotnet/Mahjong.sln`（含 Debug/Release 配置映射）；verify-v5 对 manifest 必需项目会强制校验 sln 成员资格，防漏。
4. **`artifacts/` 未入 .gitignore**（当前仅全局 `*.log` 忽略；plugins 临时 DLL 无 log 后缀）。本次证据目录仅本地留存未提交；建议后续在 `.gitignore` 追加 `artifacts/`。
5. **Unity 侧加载验证**：待有许可环境后，用 Editor 加载临时目录产物做一次装载检查（纯 .NET 验证不能替代）。
6. **CI 首跑**：workflow 语法无本地校验器，推送后首次 GitHub 运行即真实验证；在此之前 CI 记 NOT_RUN。
