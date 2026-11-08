# 构建命令手册（Windows / Android）

本文件是 Unity 两个平台构建的唯一命令参考。`AGENTS.md` 只保留一句指引，具体命令以本文件为准。

适用范围：本仓库的 Unity 客户端（`unity/Mahjong.Unity`），不涉及 `dotnet/` 服务端与 Gradle 原生模块。

---

## 0. 先做这一步：共享 DLL

Unity 客户端通过 `Assets/Plugins/Mahjong/*.dll` 消费 `dotnet/src` 里编译出来的程序集。改动 `dotnet/` 下任何代码后，必须先重建这些 DLL，否则 Unity 会报找不到类型（例如 `TableVisibleEventBatch`）。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/build-shared.ps1 `
  -Configuration Release `
  -DotnetPath "D:\game_dev_tools\dotnet\dotnet.exe"
```

- 输出：`unity/Mahjong.Unity/Assets/Plugins/Mahjong/`（19 个文件）
- 同时写出 provenance 记录：`unity/Mahjong.Unity/Library/SharedPluginProvenance/provenance.json`
- 校验：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-shared-plugins.ps1`
  - 校验失败会阻断后续 Android 构建（`SHARED_PLUGIN_PROVENANCE_BUILD_BLOCKED`）
- 如果清空过 `Library/`，provenance 会一起消失，必须重跑本步

---

## 1. 自动冷/增量：先读这段

**两个平台都已支持自动判定：同一条命令，缓存不存在就冷构建，存在就增量构建。**

| 平台 | 缓存位置 | 增量判定依据 |
|---|---|---|
| Android | `build\c05-emulator-staging\a` | 所有权标记 + 源码闭包摘要 + `Library/Bee/Android/Prj/IL2CPP` |
| Windows | `build\library-cache\staging-project\Library` | `Library` 目录 + `.c04-library-cache.json` 标记 |

**不需要任何额外参数。** 想要强制冷构建时，才需要显式传参：

- Android：`-ReuseStaging:$false`（或直接删掉 staging 目录）
- Windows：删掉 `build\library-cache` 目录

---

## 2. Android（x86_64 模拟器 APK）

### 2.1 前置条件：仓库路径必须够短

**这是最容易踩到的坑，先读这一段。**

Android 构建的 staging 位于 `<repo>/build/c05-emulator-staging/a`，而 Unity 包缓存里存在长路径文件：

```
<staging>/project/Library/PackageCache/com.unity.collections@88d5bf65ce66/
  Unity.Collections.LowLevel.ILSupport/source~/
  Unity.Collections.LowLevel.ILSupport.CodeGen/
  CollectionsUnsafeUtilityPostProcessor.cs.meta
```

在 Windows 的 260 字符限制下，这条路径对仓库根长度极其敏感：

| 仓库根 | 该路径长度 | 结果 |
|---|---|---|
| `D:\g2t`（6 字符） | 230 | 通过 |
| `D:\majiang-wt\g2-trim-imports-20261107`（38 字符） | **267** | `FAIL_PATH_BUDGET` |

**结论：Android 构建必须在短路径仓库根下执行。** 建议把工作树放在 `D:\g2t` 之类的短路径，构建通过后再合并。

仓库路径过长时的报错形如：

```
BLOCKED: staging path budget=FAIL_PATH_BUDGET
```

详见 `staging-path-budget.json` 里的 `overLimit` 字段，它会直接给出超限的具体路径。

### 2.2 常用命令（自动冷/增量）

**冷构建不要传 `-RegistryCacheSource`。** `Copy-C05EStagingProject` 会把源工程的
`Library`（含 `PackageCache`）一起复制进 staging，再传种子会因为目标已存在而被拒绝：

```
BLOCKED: refusing to merge a registry cache into an existing staging PackageCache
```

只有源工程没有 `Library/PackageCache` 时才需要种子。

```powershell
$g = [guid]::NewGuid().ToString('N')
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/build-c05-android-emulator.ps1 `
  -UnityPath "D:\game_dev_tools\unity\Editor\Unity.exe" `
  -OutputRoot "build/$g" `
  -PrivateToolchainRoot build/android-toolchain/private `
  -AndroidSdkRoot "D:\Android\Sdk" `
  -AvdHome "D:\Android\avd"
```

- `-OutputRoot` 必须位于本仓库 `build/` 下，且**必须是尚不存在的目录**（脚本拒绝复用已有输出目录）
- 产物：`build/<guid>/Mahjong-emulator-x86_64.apk`（约 40 MB）
- 证据：同目录下 `summary.json`、`build-report.json`、`unity.log`

> 已完成冷构建后，再次执行同一条命令会自动走增量（staging `a` 保留在 `build/c05-emulator-staging/a`）。

### 2.3 私有工具链

`build/android-toolchain/private` 需要预先准备（含 `android-cli-sdkmanager16.cmd` 与 `ninja.exe`）。缺失时构建会在启动前抛 `BLOCKED: private Android toolchain missing ...`。

### 2.4 关键限制：staging 目录名必须很短

Windows 的 JDK 文件 API 限制路径 260 字符，而下面这条路径在 staging 内部就已经接近上限：

```
<staging>/project/Library/Bee/Android/Prj/IL2CPP/Gradle/launcher/build/
  intermediates/desugar_graph/release/dexBuilderRelease/out/currentProject/
  jar_<128 hex>_bucket_N/graph.bin
```

实测结论：

| staging 名 | 最深路径 | 结果 |
|---|---|---|
| `20261108-131406-a1b2c3d4`（默认时间戳名） | 344 | 拒绝 |
| `cold`（4 字符） | 260 | 拒绝（判定用 `>= 260`） |
| `a`（1 字符） | 257 | 通过 |

所以默认自动 staging 固定为 `build\c05-emulator-staging\a`。**自己指定 `-StagingRoot` 时请勿超过 3 个字符。**

### 2.5 已缓解的长路径

`:unityLibrary` 的 AGP transform 缓存会被重定向到 `D:\dev_cache\android\gc`：

- 由 `Assets/Editor/C05EmulatorAndroidBuild.cs` 里的 `IPostGenerateGradleAndroidProject` 回调注入
- 原始路径 262 字符 → 重定向后 180 字符
- `:launcher` 的 build 目录**不能**重定向：Unity 从 `launcher/build/outputs/apk/release` 拷贝 APK，重定向会触发 `DirectoryNotFoundException`

### 2.6 并行度

`-JobWorkerCount` 默认 8。实测 8 相对于 1 端到端快约 13%，收益集中在 `Writing asset files`（243.6s → 120.6s）。

构建前有内存闸门：空闲物理内存与提交余量均需 ≥ 2.5 GB，否则抛 `BLOCKED_MEMORY`。8 个 worker 的实测峰值会把空闲物理内存压到约 1.09 GB，因此构建期间不要同时跑模拟器（一个空闲 AVD 约占 4 GB）。

`-BeeBuildThreads` 控制 IL2CPP 的 clang++ 并行度。**实测设为 8 会 OOM**（`LLVM ERROR: out of memory`），留空使用默认值。

### 2.7 实测耗时（本机）

| 场景 | 耗时 |
|---|---|
| 冷构建 | 约 838 s |
| 增量（稳态） | 约 82 s |

---

## 3. Windows（StandaloneWindows64 Player）

### 3.1 常用命令（自动冷/增量）

```powershell
$g = [guid]::NewGuid().ToString('N')
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-c04-privacy-matrix.ps1 `
  -UnityPath  "D:\game_dev_tools\unity\Editor\Unity.exe" `
  -DotnetPath "D:\game_dev_tools\dotnet\dotnet.exe" `
  -BuildPlayer `
  -RunRoot build/$g
```

- `-RunRoot` 必须位于本仓库 `build/` 下，且**最后一段必须是 32 位十六进制 GUID**（脚本校验 `^[0-9a-fA-F]{32}$`）
- 产物：`build/<guid>/player/Mahjong.exe`（约 667 KB）+ 依赖
- 证据：`summary.json`、`player-build.json`、`unity-editor.log`

### 3.2 自动 Library 缓存

- 缓存：`build\library-cache\staging-project\Library`（约 1.7 GB）+ `build\library-cache\.c04-library-cache.json`
- 有缓存 → 复制进新 staging 后跳过导入；无缓存 → 冷构建，成功后自动发布缓存
- 缓存是**复制**而非移动，可反复复用
- 只在构建跑到 `RUNNER_STAGE_PASS` 之后才发布，失败不会污染缓存

显式复用旧运行目录时仍可用 `-ReuseLibraryCacheFrom <path>`，它保持原语义（要求是已封存的 `build/<GUID>/staging-project/Library`，且会**移动**该目录）。

### 3.3 实测耗时（本机）

| 场景 | 耗时 |
|---|---|
| 冷构建 | 约 388 s |
| 增量（稳态） | 约 126 s |

---

## 4. 缓存布局

工具链缓存不含任何项目代码，统一放在仓库之外：

```
D:\dev_cache\
  android\
    gradle\     GRADLE_USER_HOME（插件/依赖解析）
    gc\         AGP transform 缓存（:unityLibrary）
  windows\
    nuget\      NUGET_PACKAGES
    dotnet\     DOTNET_CLI_HOME
```

- 由构建脚本以**进程级**环境变量注入（`GRADLE_USER_HOME`、`C05_GRADLE_BUILD_ROOT`），在 `finally` 中恢复，不污染调用方环境
- `-CacheRoot` 可覆盖根目录
- 这些目录可以随时删除，只会让下一次构建重新生成；**与你的源码编译产物无关**
- 你的代码产物在 staging 内：`Library/ScriptAssemblies/Mahjong.Unity.*.dll` 与 `Library/Bee/artifacts/Android/*/libil2cpp.so`

---

## 5. 构建守卫（遇到报错先看这里）

| 报错 | 含义 | 处理 |
|---|---|---|
| `SHARED_PLUGIN_PROVENANCE_BUILD_BLOCKED` | 共享 DLL 与源码不一致，或 provenance 缺失 | 重跑 `build-shared.ps1` |
| `BLOCKED: staging path budget=FAIL_PATH_BUDGET` | 路径超 260 字符 | 缩短 staging 目录名（≤3 字符） |
| `BLOCKED_MEMORY` | 空闲内存不足 2.5 GB | 关闭模拟器等大内存进程 |
| `BLOCKED: refusing to merge a registry cache into an existing staging PackageCache` | 复用了 staging 却又传了 `-RegistryCacheSource` | 复用时不传该参数 |
| `RunRoot must end in a GUID-N run id` | Windows `-RunRoot` 末段不是 32 位十六进制 | 用 GUID 命名 |
| `Refusing to reuse an existing evidence/output directory` | 输出目录已存在 | 换新的 GUID 目录 |
| `BLOCKED: staging root already exists and must not be reused without -ReuseStaging` | 显式指定的 staging 已存在但未声明复用 | 加 `-ReuseStaging` 或换目录 |
| `LLVM ERROR: out of memory` | IL2CPP 并行过高 | 不要设置 `-BeeBuildThreads` |

### 强制全量重建

```powershell
# Android
Remove-Item -Recurse -Force build\c05-emulator-staging\a

# Windows
Remove-Item -Recurse -Force build\library-cache

# 两侧同时清（含 AGP 与 Gradle 缓存）
Remove-Item -Recurse -Force build\c05-emulator-staging, build\library-cache, D:\dev_cache\android\gc
```

---

## 6. 源码改动边界

两个 runner 都带源码边界守卫：构建前后对 `Assets`/`Packages`/`ProjectSettings` 做 SHA256 比对，任何未在 `AllowedPaths` 内声明的改动都会让构建失败。

- Android 的允许清单在 `tools/unity/build-c05-android-emulator.ps1` 的 `$script:AllowedPaths`
- Unity 导入可能改写 `*.asset`（行尾空格），这类改动会被 `Repair-C05EPostBuildSource` 记录为失败，需要手动恢复后再构建

**构建前请确认 `git status` 干净**，否则很可能在导入阶段被守卫拦下。
