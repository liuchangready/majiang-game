# V5 Unity 共享插件来源保护（D-U-01）交付报告

日期：2026-10-28（Asia/Shanghai）
任务：仅修复已接受报告 `V5-UNITY-CURRENT-PLAYER-VALIDATION-20261028.md` §8 记录的 **D-U-01**。
结论：**PASS（仅限 D-U-01 来源保护；不代表 P4.2-B/P4.5/G2–G5/整场可玩）**。

## 1. 冻结、提交与范围

| 项 | 值 |
|---|---|
| 工作树 | `D:/majiang-wt/unity-plugin-provenance-ds-20261028` |
| 分支 | `wt/v5-unity-plugin-provenance-ds-20261028` |
| 冻结 SHA | `1b0d693c30dfce85c86b438566da7742edc49c70` |
| 首轮提交 | `65c2603305fdfeaea3593d689a55949e08389b5e` |
| 本轮返修提交 | 本次本地提交（字面 SHA 见交接回执；报告不自引用自身哈希） |
| 最终 tracked 状态 | `git status --porcelain=v1 -uall` 0 行 |
| `git diff --check 1b0d693..HEAD` | exit 0 |

范围：`tools/unity/build-shared.ps1`、`check-shared-plugins.ps1`、`shared-plugin-provenance-lib.ps1`、`test-shared-plugin-provenance.ps1`、`Assets/Editor/SharedPluginProvenanceGuard.cs`、`SharedPluginProvenancePlayProbe.cs` 及稳定 meta、本报告。未改 csproj/sln/产品程序集/正式现有测试/边界策略/统一 verifier；未提交二进制与 ignored 构建产物。V4 仍生产，G1 已批准，G2–G5 未批准，181 classified 仍未覆盖；P4.1c/d 仍 paused。

## 2. 输入边界（实际实现）

生产者与校验器共用 `shared-plugin-provenance-lib.ps1`（schema 2 / boundary 4），覆盖：

- 7 个共享工程目录下 `.cs/.csproj/.proto/.resx/.json/.props/.targets/.config/.ruleset/.editorconfig/.rsp`（排除 `bin/obj`）；
- **csproj 与受支持 props/targets 中的** `Compile/Protobuf/EmbeddedResource` 显式 include；支持 `$(MSBuildThisFileDirectory)`、`$(MSBuildThisFile)`，项目自有文件另支持 `$(MSBuildProjectDirectory)`；其他属性表达式/元数据表达式/通配符 fail closed；
- props/targets 与 csproj 中的 `ProjectReference`，仅允许 7 个共享工程；
- 仓库根、`dotnet/`、`dotnet/src/` 的 `Directory.Build.props/targets/rsp`、`Directory.Packages.props`、`NuGet.config`；项目目录内 `NuGet.config` 由 `.config` 遍历覆盖；
- `global.json`、`Mahjong.sln`、生产者/共享定义两个工具文件。

明确拒绝：`Import`、`Compile/Protobuf/EmbeddedResource Remove`、item `Exclude`、`DefaultItemExcludes`、显式 `Reference/Analyzer/AdditionalFiles`、仓库外/通配符/不支持表达式的 include、7 工程外 `ProjectReference`。机器/用户级 `NuGet.config` 不在仓库边界内，报告不声称覆盖；仓库级 NuGet 配置按字节记录。

输入身份为原始字节 SHA-256，路径相对仓库、`/` 分隔、大小写不敏感、排序后比较；不做 LF/CRLF 归一；文档/报告不在扩展白名单，文档-only 变化不使记录失效。第三方运行时闭包由生产者与校验器**分别**从 `obj/project.assets.json` + NuGet cache 重新解析，按程序集名/长度/SHA-256 双向核对。

## 3. 首轮发现与本轮修复（D-R1-01..05）

### D-R1-01（MAJOR）MSBuild 输入闭包与增量构建
- props/targets 级显式 include 未纳入；根 `NuGet.config` 未纳入；默认 `dotnet build` 在源码 mtime 被保留时会复用旧 DLL，却记录新源码哈希。
- 修复：新增 MSBuild 声明遍历（csproj + 所有受支持 props/targets，含 `MSBuildThisFileDirectory` 解析）；根/dotnet/src NuGet 配置纳入；构建前删除 7 工程 `bin/<Configuration>`、`obj/<Configuration>`，强制真实编译；manifest 记录 `build.mode=forced-clean-outputs`、`forcedRebuild=true`、`inputCount`、`cleanedOutputs`；使用 `-m:1 -nodeReuse:false` 降低 MSBuild 子节点崩溃导致的假失败。

### D-R1-02（MAJOR）证据结构与运行时闭包
- checker 的 `$configuration` 覆盖参数、build.exitCode/counts 未验证、只硬编码 Google.Protobuf，删除非 Protobuf 依赖可从记录与目录一起消失仍 PASS。
- 修复：schema 2 强制验证 schema/kind/config/TFM/langVersion/scope/target/tool/inputs/outputs/packages/build 元信息；`requestedConfiguration` 与 `recordedConfiguration` 分离；`build.exitCode=0`、`forcedRebuild`、`copiedFileCount`/`inputCount` 与列表一致、时间戳有序；7 自有输出必须是 project 类型且 project 身份匹配；包项与 package 输出双向一致；独立重解析运行时闭包，缺失/多余/哈希不符均非零；配置/作用域错误按精确 code 拒绝。

### D-R1-03（MAJOR）JUnit/partial 口径
- NOT_RUN 被写成成功 testcase，`skipped=0`；`-SkipUnity` 漏 P15b；默认矩阵可能把 partial 当 PASS。
- 修复：reporter 按 Status 输出 `<skipped/>` 并统计 `skipped`/`partial`；JSON/XML 同一 cases 分母；P22 直接调用生产 reporter 断言 two testcases/skipped=1；默认存在 NOT_RUN 即 `OverallFail=true`（exit 1），仅显式 `-AllowPartial` 才允许 partial 返回 0，且仍如实写 skipped。

### D-R1-04（MINOR）交付报告
- 首轮缺本文件；本轮补交并提交，内容与实际边界/结果一致。

### D-R1-05（MINOR）fixture/删除边界
- EvidenceRoot 任意路径、递归删除预存 fixture、`StartsWith` 无目录边界、跨 shell `rd /s /q`。
- 修复：入口在**任何写入/删除前**拒绝不在 `<repo>/build/` 严格子目录的 EvidenceRoot（含仓库根、build 根、`buildfoo` 同前缀、外部路径）；fixture 使用每次运行 GUID 子目录，存在即拒绝；清理仅用 PowerShell `Remove-Item -LiteralPath -Recurse`，失败保留并报告，不跨 shell 删除。

### D-R2-01（MAJOR，本轮新增）props/targets 普通相对 Include 语义
- **问题**：上一轮把 props/targets 中的普通相对 Include 按 props 文件目录解析；真实 MSBuild 按**消费项目目录**解析，只有 `$(MSBuildThisFileDirectory)` 才是文件相对。同名 decoy 可被误当真实来源。
- **修复**：`Resolve-SharedPluginMsBuildInput` 对普通相对路径使用 `$ProjectDirectory`；`Directory.Build.props/targets` 按每个项目解析“最近生效”文件；支持窄 `Condition="'$(MSBuildProjectName)' ==/!= 'X'"`（ItemGroup 与 item），其他 Condition/表达式 fail closed；Compile/EmbeddedResource/Protobuf/ProjectReference 统一处理。
- **验证**：P33 在 props 与 targets 同时放普通相对 include、`MSBuildThisFileDirectory` include 与同名 decoy；manifest 只含 `dotnet/src/caller-linked.cs`/`dotnet/src/targets-linked.cs`，改真实文件非零、改 decoy 仍 0。

### D-R2-02（MAJOR，本轮新增）自定义 IntermediateOutputPath
- **问题**：清理固定 `obj/<Configuration>` 无法覆盖 `<IntermediateOutputPath>obj/provenance-cache/...`；旧中间产物可让非法源码假绿。
- **修复**：对所有 csproj 与生效 props/targets 扫描 `IntermediateOutputPath/BaseIntermediateOutputPath/MSBuildProjectExtensionsPath/OutputPath/BaseOutputPath/OutDir`；非空即 fail closed（不递归删除 MSBuild 指向位置）。默认布局与 Debug/Release scratch 继续通过。
- **验证**：P39 在 fixture 设置自定义 `IntermediateOutputPath` → build-shared 非零、无 manifest、checker 非零，日志含 `Unsupported custom MSBuild output layout`；P31/P32 保留旧 mtime 的非法/合法语义变更仍分别失败/生成新 DLL。

### D-R2-03（MINOR，本轮新增）EvidenceRoot junction
- **问题**：仅字符串 `StartsWith` 无法阻止 `build/<junction>/run` 写到物理 build 外。
- **修复**：入口在写入前遍历 repo 根到 EvidenceRoot 的每级已存在路径，发现 reparse point/junction 即拒绝；fixture 创建与递归清理同样检查；清理仍只用 `Remove-Item -LiteralPath`。
- **验证**：本树 `build/<junction>` 指向 `artifacts/`，`-EvidenceRoot build/<junction>/run` exit 1，目标 `run/` 未创建；junction 以 .NET 非递归 `Directory.Delete` 移除，目标未删。原有 4 个路径负例、空格路径正例保持。
## 4. 验证（本轮实际）

### 4.1 完整矩阵
命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/test-shared-plugin-provenance.ps1 `
  -EvidenceRoot build/d-r1-matrix6-d260f3d32ee7 -UnityTimeoutSeconds 600 -BuildTimeoutSeconds 900
```

结果：**39/39 PASS，0 FAIL，0 NOT_RUN**，wrapper exit 0；JUnit `tests=39 failures=0 skipped=0 partial=false time=548.280`。覆盖 P01–P15b/P16/P17/P18/P19/P20/P21/P22/P24–P39，包括：
- 保留旧 mtime 的非法源码真实编译失败、无记录；保留旧 mtime 的合法语义变更生成新 DLL；
- props 级 linked `Compile`/`EmbeddedResource` 纳入并检测源码变化；
- 根 NuGet.config / dotnet/src NuGet.config 的 added/changed/removed；
- 不支持表达式与 7 工程外 `ProjectReference` fail closed；
- build.exitCode=42、缺 build、copiedFileCount 错、遗漏 System.Numerics.Vectors、包/输出哈希冲突、Release 请求/ Debug 记录、真实 Debug scratch 记录；
- 缺证据真实 BuildPlayer 阻断（native 1）、坏 DLL 真实 BuildPlayer 阻断（PLUGIN_CORRUPT）、缺证据真实 Play 取消（native 42）、有效同步真实 Play 进入（native 0）。

### 4.2 partial 真实运行
`-SkipUnity -SkipFixtureBuild`（未加 `-AllowPartial`）：exit **1**；`tests=39 pass=25 fail=0 not_run=14 partial=true`；XML `skipped=14`、14 个 `<skipped/>`、`partial=true`。默认完整运行 0 NOT_RUN。

### 4.3 路径边界负例
`-EvidenceRoot` 分别指向 `%TEMP%\du01-outside`、`<repo>\buildfoo`、仓库根、`<repo>\build`：均在写入前 exit 1，外部目录未创建。

### 4.4 真实 fresh shared/Player/smoke/boundary
本轮 acceptance（与最终代码同一工作树；证据根 `build/d-r2-matrix-d44b1f2abd74/`）：
- 删除 7 工程 Release 输出后 `build-shared -Configuration Release`：exit 0，0 warning/0 error，copied 19，inputs 131 / outputs 19 / packages 5；
- checker：exit 0，7/7 自有 DLL 与 fresh bin 一致，5/5 包闭包一致；
- 真实 Unity `-buildWindows64Player`：exit 0，114.7s，`Build Finished, Result: Success.`，Mono，6 levels；
- Player managed 7/7 与 fresh 源码逐字节一致；
- 既有 `run-player-smoke.ps1`：Player native exit 0，4 张 PNG；
- `check-unity-boundaries.ps1 -StrictCompiled`：exit 0，`Passed=true`，`asmdef_dag=PASS`，`metadata_direct_refs=PASS`。

（Player exe/smoke/boundary 的精确 SHA 与日志见交付回执；本文件不重复长表。）

## 5. 恢复与提交

- 本轮所有临时 fixture 位于 `build/d-r1-*` ignored 独占目录；源码注入均在 fixture 或本树备份/恢复文件内。
- 测试产生的 tracked 生成差异（14 插件 + URP/slnx/ProjectSettings/字体/PerformanceTest meta）在交付前按显式路径恢复 HEAD；旧 tracked 插件字节保留，ignored 证据保留。
- 提交仅含本任务工具/Editor/测试/本报告；无 DLL/PDB/Library/Player/日志。未 push、未建 PR/MR、未合并 v5、未部署、未改 scheduler、未创建 `complete.flag`。
- 历史 D-U-01 的 Player 证据仍属 `392b973`/报告 `97a38c5`；本报告不重写历史，只记录本轮修复。
- B2b 300s 超时根因仍未关闭；P4.2-B/P4.5/LocalHost/整场/Android/iOS/跨网/G2–G5 均 **NOT_RUN**。
