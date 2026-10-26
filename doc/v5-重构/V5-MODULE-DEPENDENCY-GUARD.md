# V5 模块依赖方向精确白名单（I-5 / S-1 落地，第 2 轮返修）

日期：2026-10-26
状态：**D-DEP-R1-01 / D-DEP-R1-02 两组 MAJOR 已批量返修；组件级定向验证完成；非 G1/G5 门槛**
基线：`d9d17d8299fc348149f581a0741ddd07b1f519f6`（既有唯一 PR19 已评审/发布头）
第 1 轮交付：`804f1734d8fcdb09f205b5ff28fdcf99c2db821a`
工作树：`D:/majiang-wt/module-dependency-guard-20261026`
分支：`wt/v5-module-dependency-guard-20261026`

---

## 0. 第 2 轮返修映射（caller 独立探针 6 项假绿）

caller 证据根：`build/artifacts/codex-dependency-review-16a0e3447d304238817a620e8be48936`。
原探针与日志**原样保留**：`probe/Program.cs` SHA-256 `7589C59B47E5657AE2AFC4E8A06621292B39EA3222FF88706D7351B5728C61EA`，`probe.log` SHA-256 `C7D54A2D2E4898217D5618A8CC4E813976DC050023DA8A79D9FE1DFA513D70CA`。未修改、未删除、未改写其断言。

| 缺陷 | 本轮修复 | 真实负例 |
|---|---|---|
| D-DEP-R1-01 前缀分类绕过逐模块外部允许集 | 删除 `System.*` / `Microsoft.Extensions.*` 前缀放行，改为**真实共享框架程序集名精确目录**；共享框架逐模块授权，仅 Lobby | `DomainCannotBorrowSystemOrMicrosoftPrefixesAsCover`（4 例）+ Lobby 未知同前缀拒绝/真实共享框架放行 |
| D-DEP-R1-02 项目声明丢失目标身份、忽略声明边 | 声明事实保留 raw Include、canonical 绝对路径、目标名；validator 用 8 工程 canonical path map 校验；`Reference`/`ReferencePath`/`Import`/多值/通配/宏/Condition 等不支持形式一律 fail closed | `RedirectedProjectReferenceToUndiscoveredPathIsRejected`、`RawAssemblyReferenceDeclarationIsRejected`、`UnsupportedDeclarationFormsFailClosed`（6 例） |

原独立探针复跑结果（同一二进制、同一参数）：

```text
ASSEMBLY Domain -> System.Reactive diagnostics=1
ASSEMBLY Domain -> System.IO.Abstractions diagnostics=1
ASSEMBLY Domain -> Microsoft.AspNetCore.Http diagnostics=1
ASSEMBLY Domain -> Microsoft.Extensions.UnknownVendor diagnostics=1
XML redirected-project diagnostics=1
XML unused-raw-reference diagnostics=1
CONFIRMED_FALSE_GREENS=0
PROBE_EXIT=0
```

日志：`build/artifacts/codex-dependency-review-16a0e3447d304238817a620e8be48936/probe-final.log`，SHA-256 `332408F895B6C8A37AC92E4F2956398A373166B4B1057557E400279214EE5976`。
原探针**没有**遇到 unsupported XML 抛异常路径；6 项均由真实 validator 返回非空诊断完成验证。

---

## 1. 结论

| 项 | 结果 |
|---|---|
| fresh Release `--no-incremental` 构建 | **PASS**，exit 0，0 warning / 0 error |
| direct runner 全类 | **PASS**，Total 78 / Failed 0 / Errors 0 / Skipped 0 / Not Run 0，exit 0 |
| direct runner 定向 shadow 方法 | **PASS**，Total 1 / 0/0/0/0，exit 0 |
| direct runner 定向 4 项前缀绕过 | **PASS**，Total 4 / 0/0/0/0，exit 0 |
| 仅新工程 `dotnet test --no-build` | **PASS**，78 passed / 0 failed / 0 skipped，exit 0 |
| 原独立探针 | **PASS**，`CONFIRMED_FALSE_GREENS=0`，exit 0 |
| 本项目自建诊断复验探针 | **PASS**，6 项均有具体 from->to 诊断，`DIAGNOSTIC_PROBE_FAILURES=0`，exit 0 |
| 项目声明图精确允许集 | **PASS**（含 canonical target 与 unsupported fail-closed） |
| 程序集精确外部允许集 | **PASS**（真实框架目录 + 逐模块共享框架授权） |
| G1/G5 门槛影响 | **无**；不升级强制门，不重设门槛 |

78 = 23 个 `Fact` + 55 个 `Theory` 展开用例；无 `Skip`、无空测试、无 `Total=0`、无旧 DLL。

---

## 2. 范围与基线

### 2.1 expectedFiles（唯一改动，未扩大）

| 文件 | 性质 |
|---|---|
| `dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj` | 新增测试工程（xUnit v3 可执行） |
| `dotnet/tests/Mahjong.Architecture.Tests/DependencyDirectionTests.cs` | 校验器、真实图断言、两组负例 |
| `doc/v5-重构/V5-MODULE-DEPENDENCY-GUARD.md` | 本文件 |

未改动：任何生产项目/csproj、现有测试、`dotnet/Mahjong.sln`、`tools/verify-v5.ps1`、`tools/v5-verify.manifest.json`、fixtures、`CLAUDE.md`、`doc/ui`、`round-preparation/PROMPTS.md`、Unity 环境/Resources/Burst/Plugins/ProjectSettings。未触碰主 checkout、规划树、兄弟树、全局环境/注册表。

### 2.2 与门槛的关系

- 本任务是 `V5-1-REVIEW.md` §9.2 **S-1**（= §7.3/§12.2 **I-5**）的落地实现，**非门条款**。
- 门 3 维持既有 PASS；本任务**不**新增/升级 G1 强制门，**不**改变 G1/G5 判定，**不**实施 P4/P5 集成。
- V4 仍是唯一生产架构；G1 整体未最终批准、G5 未完成。

---

## 3. 双源事实与精确允许集

测试读取两个独立事实源，不读取新测试工程自己的引用闭包：

1. **项目声明图**：受控 XML 解析 `dotnet/src/<Name>/<Name>.csproj` 的 `ProjectReference` / `PackageReference`，并保留 raw Include、canonical 绝对目标路径、目标名与不支持声明。
2. **程序集实际引用图**：`System.Reflection.Metadata` + `PEReader` 读取 8 个生产项目 fresh Release DLL 的 `AssemblyReferences`。

### 3.1 八个工程的声明图（实测）

| 工程 | 有效 TFM | 声明 `ProjectReference` | 声明 `PackageReference` |
|---|---|---|---|
| `Mahjong.Domain` | netstandard2.1 | （无） | （无） |
| `Mahjong.Contracts` | netstandard2.1 | （无） | （无） |
| `Mahjong.Contracts.Wire` | netstandard2.1 | Contracts | Google.Protobuf 3.28.3；Grpc.Tools 2.68.1（`PrivateAssets=All`） |
| `Mahjong.Ai` | netstandard2.1 | Domain | （无） |
| `Mahjong.TableRuntime` | netstandard2.1 | Domain、Ai、Contracts | （无） |
| `Mahjong.Client` | netstandard2.1 | Contracts、Contracts.Wire | （无） |
| `Mahjong.LocalHost` | netstandard2.1 | TableRuntime | （无） |
| `Mahjong.LobbyService` | net8.0 | Contracts | Npgsql 8.0.9 |

发现规则：只枚举 `dotnet/src/*/`，每目录恰好一个 `.csproj`，工程名等于目录名；显式断言 **8** 并与固定名单逐项比对。空枚举、多/少工程、名字漂移均失败。

### 3.2 八个程序集的实际引用（fresh Release）

| 程序集 | 实际引用（分类后） | 强制正向边 |
|---|---|---|
| Domain | netstandard（平台） | 无 |
| Contracts | netstandard（平台） | 无 |
| Wire | Contracts、Google.Protobuf、netstandard | Contracts；Google.Protobuf |
| Ai | Domain、netstandard | Domain |
| TableRuntime | Domain、Ai、Contracts、netstandard | Domain；Ai；Contracts |
| Client | Contracts、Contracts.Wire、Google.Protobuf、netstandard | Contracts；Contracts.Wire；Google.Protobuf |
| LocalHost | netstandard（当前空程序集） | 无（见 §3.3） |
| LobbyService | 真实 ASP.NET Core 共享框架、System.* 平台、Npgsql | Npgsql |

### 3.3 声明但未发出的边（不伪造正向断言）

| 声明边 | 实际 DLL | 处理 |
|---|---|---|
| `LocalHost -> TableRuntime` | 未发出（`LocalHost` 只有 csproj、没有 `.cs` 源码） | 项目图强制精确存在；程序集层不要求 |
| `LobbyService -> Contracts` | 未发出（未使用 Contracts 类型） | 项目图强制精确存在；程序集层不要求，但允许闭包覆盖 |

### 3.4 项目声明 canonical 身份（本轮新增）

`ProjectReferenceFact` 保存：

- `RawInclude`：声明原文；
- `CanonicalPath`：相对该 csproj 目录解析后的绝对路径；
- `TargetName`：canonical 文件名（不含扩展名）。

validator 由 8 个已发现工程的 `Path.GetFullPath` 建立 canonical path -> 工程名映射，并逐条断言：

- canonical 目标必须命中这 8 个真实工程文件，否则 `PROJECT_REFERENCE_TARGET_NOT_DISCOVERED`；
- canonical 目标解析出的工程名必须等于声明文件名，否则 `PROJECT_REFERENCE_IDENTITY_MISMATCH`；
- 仅 basename 相同、路径指向 `..\..\shadow\Mahjong.Domain.csproj` 的同名冒充被拒绝；
- 缺失目标、逃出图、重复 canonical path 均失败。

不支持任意 MSBuild 解释器；以下形式**显式 fail closed**，不再转成空字符串后“成功”：

- `ProjectReference` / `PackageReference` 带 `Condition`、`Update`、`Remove`；
- Include 为空、多值（`;`）、通配（`*` / `?`）、MSBuild 属性/项表达式（`$(` / `@(` / `%(`）；
- 裸 `<Reference>`（含 HintPath）、`<ReferencePath>`、`<COMReference>`、`<FrameworkReference>`、`<Import>`。

对应诊断 `PROJECT_UNSUPPORTED_DECLARATION`，detail 保留原始声明与 fail-closed 理由。

### 3.5 程序集允许闭包

| 模块 | 允许的 Mahjong 程序集引用 | 必须出现 |
|---|---|---|
| Domain / Contracts | ∅ | ∅ |
| Wire | Contracts | Contracts |
| Ai | Domain | Domain |
| TableRuntime | Domain、Ai、Contracts | 三者 |
| Client | Contracts、Contracts.Wire | 两者 |
| LocalHost | TableRuntime、Domain、Ai、Contracts（闭包） | ∅ |
| LobbyService | Contracts | ∅ |

### 3.6 外部程序集身份：真实框架目录，不是前缀（本轮修复）

`FrameworkAssemblyCatalog` 在运行时读取**真实**共享框架目录：

- 平台/BCL：`typeof(object).Assembly.Location` 所在 `Microsoft.NETCore.App/<version>` 的全部 `*.dll` 文件名；
- 共享框架：相邻 `shared/Microsoft.AspNetCore.App/<version>` 的全部 `*.dll` 文件名；
- 精确文件名成员判定，大小写不敏感；测试断言平台集合 > 100、共享框架集合 > 50，目录缺失或为空会显式失败（不允许空枚举冒绿）。

因此：

- `System.Reactive`、`System.IO.Abstractions` 不在框架集合 → ThirdParty，Domain 必须显式允许（实际不允许）→ 拒绝；
- `Microsoft.Extensions.UnknownVendor` 不在框架集合 → ThirdParty → 拒绝；
- `Microsoft.AspNetCore.Http` 是真实共享框架成员 → SharedFramework；但只有 Lobby 的 `AllowSharedFramework=true`，Domain/Ai/Contracts 使用会得到 `ASSEMBLY_UNAUTHORIZED_SHARED_FRAMEWORK`；
- `netstandard`、`System.Collections`、`System.Runtime` 等真实 BCL 仍放行，不搞一刀切拒绝；
- Wire/Client 的 Google.Protobuf、Lobby 的 Npgsql 保持既有显式允许。

第三方逐模块精确：Wire/Client = Google.Protobuf；Lobby = Npgsql；其余为空。`Grpc.Tools` 必须保持 `PrivateAssets=All`，否则 `PROJECT_BUILD_ONLY_PACKAGE_LEAKED`。

---

## 4. 诊断码（本轮新增加粗）

| 代码 | 含义 |
|---|---|
| `PROJECT_MISSING` | 规则对应工程未发现 |
| `PROJECT_FORBIDDEN_EDGE` | 声明边不在精确项目允许集 |
| `PROJECT_MISSING_EDGE` | 允许集要求的项目边缺失 |
| `PROJECT_UNKNOWN_MAHJONG_REFERENCE` | 引用 8 工程之外的 `Mahjong.*` |
| `PROJECT_UNKNOWN_MAHJONG_PROJECT` | 图出现规则表之外 Mahjong 工程 |
| `PROJECT_FORBIDDEN_PACKAGE` | 包不在模块精确允许集 |
| `PROJECT_MISSING_PACKAGE` | 必要包声明缺失 |
| `PROJECT_BUILD_ONLY_PACKAGE_LEAKED` | 构建期包未保持 `PrivateAssets=All` |
| **`PROJECT_UNSUPPORTED_DECLARATION`** | Reference/Import/条件/多值/通配/宏等不支持声明，fail closed |
| **`PROJECT_REFERENCE_TARGET_NOT_DISCOVERED`** | canonical 目标不是 8 个真实工程文件之一（含同名 shadow） |
| **`PROJECT_REFERENCE_IDENTITY_MISMATCH`** | canonical 目标工程名与声明文件名不一致 |
| **`PROJECT_INVALID_REFERENCE_PATH`** | 声明目标路径无法解析 |
| **`PROJECT_INVALID_PROJECT_PATH`** | 已发现工程路径无法解析 |
| **`PROJECT_DUPLICATE_CANONICAL_PATH`** | 两个图节点解析到同一工程文件 |
| `ASSEMBLY_MISSING` | fresh Release DLL 不存在 |
| `ASSEMBLY_FORBIDDEN_MAHJONG_REFERENCE` | 已知 Mahjong 引用越出允许闭包 |
| `ASSEMBLY_MISSING_MAHJONG_REFERENCE` | 必要 Mahjong 正向边缺失 |
| `ASSEMBLY_UNKNOWN_MAHJONG_REFERENCE` | 引用 8 工程之外的 `Mahjong.*` |
| `ASSEMBLY_UNKNOWN_MAHJONG_ASSEMBLY` | 检查到规则表之外 Mahjong 程序集 |
| `ASSEMBLY_FORBIDDEN_THIRD_PARTY` | 第三方引用不在模块精确允许集 |
| `ASSEMBLY_MISSING_THIRD_PARTY` | 必要第三方引用缺失 |
| **`ASSEMBLY_UNAUTHORIZED_SHARED_FRAMEWORK`** | 真实共享框架成员未获该模块授权 |
| `RULE_INCONSISTENT` | 规则表内部不一致（含 Mahjong 闭包越界） |

规则表自身仍检查：`Required` 属于 `Allowed`；允许的 Mahjong 程序集边必须落在项目声明边传递闭包内；`Grpc.Tools` 必须同时属于包允许集。

---

## 5. 统一负例矩阵

负例分两层：内存合成图覆盖诊断分支；自有随机 TEMP 中的真实 csproj 副本覆盖 XML 解析/发现/canonical 路径。生产 csproj 一律不改。

| # | 类别 | 注入 | 期望诊断 | 测试 |
|---|---|---|---|---|
| N1 | 禁止项目边（12 组） | Domain→Contracts；Contracts→Wire；Wire→Domain/Client；Ai→Contracts；TableRuntime→Wire/LocalHost；Client→Domain/TableRuntime；LocalHost→Client；Lobby→Domain/TableRuntime | `PROJECT_FORBIDDEN_EDGE` | `ForbiddenProjectEdgesAreRejectedWithSpecificDiagnostics` |
| N2 | 禁止程序集边（9 组） | 同上模块的 Mahjong 越界引用 | `ASSEMBLY_FORBIDDEN_MAHJONG_REFERENCE` | `ForbiddenAssemblyMahjongReferencesAreRejectedWithSpecificDiagnostics` |
| N3 | 未知 Mahjong 工程边 | TableRuntime→Mahjong.Shadow | `PROJECT_UNKNOWN_MAHJONG_REFERENCE` | `UnknownMahjongProjectEdgeIsRejected` |
| N4 | 未知 Mahjong 工程节点 | 图新增 Mahjong.Shadow | `PROJECT_UNKNOWN_MAHJONG_PROJECT` | `UnknownMahjongProjectNodeIsRejected` |
| N5 | 未知第三方包 | Client + Newtonsoft.Json | `PROJECT_FORBIDDEN_PACKAGE` | `UnknownThirdPartyPackageIsRejected` |
| N6 | 缺失必要项目边 | 删除 TableRuntime→Domain | `PROJECT_MISSING_EDGE` | `MissingAllowedProjectEdgeIsRejected` |
| N7 | 缺失包声明 | 删除 Wire 的 Google.Protobuf | `PROJECT_MISSING_PACKAGE` | `MissingDeclaredPackageIsRejected` |
| N8 | 构建期包泄漏 | Grpc.Tools 去掉 PrivateAssets=All | `PROJECT_BUILD_ONLY_PACKAGE_LEAKED` | `BuildOnlyPackageMustRemainPrivateAssetsAll` |
| N9 | 未知 Mahjong 程序集引用 | Client→Mahjong.Shadow | `ASSEMBLY_UNKNOWN_MAHJONG_REFERENCE` | `UnknownMahjongAssemblyReferenceIsRejected` |
| N10 | 未知 Mahjong 程序集节点 | 检查 Mahjong.Shadow | `ASSEMBLY_UNKNOWN_MAHJONG_ASSEMBLY` | `UnknownMahjongAssemblyNodeIsRejected` |
| N11 | 未知第三方程序集（4 组） | Client→Newtonsoft.Json；Lobby→MySql.Data；Wire→Npgsql；Domain→Google.Protobuf | `ASSEMBLY_FORBIDDEN_THIRD_PARTY` | `UnknownThirdPartyAssemblyReferenceIsRejected` |
| N12 | 缺失必要 Mahjong 程序集边 | TableRuntime 去掉 Domain | `ASSEMBLY_MISSING_MAHJONG_REFERENCE` | `MissingRequiredAssemblyMahjongReferenceIsRejected` |
| N13 | 缺失必要第三方程序集边 | Wire 去掉 Google.Protobuf | `ASSEMBLY_MISSING_THIRD_PARTY` | `MissingRequiredThirdPartyAssemblyReferenceIsRejected` |
| N14 | 合法对照 | 真实 BCL、Lobby 真实共享框架、Wire/Client Protobuf、Lobby Npgsql | 无诊断 | `LegalBclSharedFrameworkAndPerModuleThirdPartyReferencesStayGreen` |
| N15 | 规则表被放宽 | Domain 的 assembly allowlist 加入 Contracts（项目闭包外） | `RULE_INCONSISTENT` | `RuleTableCannotWidenAnAssemblyAllowlistBeyondTheProjectClosure` |
| N16 | 真实 csproj 副本注入禁止边 | TableRuntime + Client 项目引用 | `PROJECT_FORBIDDEN_EDGE` | `ForbiddenEdgeInjectedIntoARealProjectFileIsDetectedThroughXmlParsing` |
| N17 | 分类器精确身份 | 20 个 BCL/共享框架/Mahjong/第三方样本 | 精确 `ReferenceKind` | `ReferenceClassifierSeparatesBclFrameworkMahjongAndThirdParty` |
| **N18** | **前缀绕过（本轮缺陷）** | Domain + System.Reactive / System.IO.Abstractions / Microsoft.AspNetCore.Http / Microsoft.Extensions.UnknownVendor | 前两者与第四者 `ASSEMBLY_FORBIDDEN_THIRD_PARTY`；第三者 `ASSEMBLY_UNAUTHORIZED_SHARED_FRAMEWORK` | `DomainCannotBorrowSystemOrMicrosoftPrefixesAsCover` |
| **N19** | **Lobby 同前缀未知第三方 / 真实共享框架对照** | Lobby + Microsoft.Extensions.UnknownVendor（拒绝）；Lobby + Microsoft.AspNetCore.Http（放行） | 前者 `ASSEMBLY_FORBIDDEN_THIRD_PARTY`；后者无诊断 | `LobbyUnknownMicrosoftExtensionsVendorIsRejected`、`LobbyRealSharedFrameworkReferenceIsAuthorized` |
| **N20** | **同名 shadow 目标** | 真实 csproj 副本把 TableRuntime→Domain 改为 `..\..\shadow\Mahjong.Domain.csproj` | `PROJECT_REFERENCE_TARGET_NOT_DISCOVERED`，保留 raw shadow 路径 | `RedirectedProjectReferenceToUndiscoveredPathIsRejected` |
| **N21** | **裸 Reference 声明** | 真实 csproj 副本加入 `<Reference Include="Newtonsoft.Json"/>` | `PROJECT_UNSUPPORTED_DECLARATION`，to=`Reference` | `RawAssemblyReferenceDeclarationIsRejected` |
| **N22** | **不支持声明形式** | 通配 ProjectReference、多值 ProjectReference、Import、`$(...)` PackageReference、Reference、ReferencePath | `PROJECT_UNSUPPORTED_DECLARATION`，逐例 to=声明元素 | `UnsupportedDeclarationFormsFailClosed` |
| **N23** | **框架目录真实性** | 平台/共享框架集合计数、System.Reactive/IO.Abstractions/UnknownVendor 非成员、ASP.NET 成员 | 精确 `ReferenceKind` + 集合非空 | `FrameworkCatalogUsesExactRealAssemblyIdentity` |

TEMP 清理边界：随机根名 `mahjong-arch-guard-<guid>`；递归删除前解析绝对路径，断言仍在进程 TEMP 之下、名称前缀属于本测试、且根不是 reparse point，否则拒绝删除。

---

## 6. 实测命令与结果（原样）

工作目录：`D:/majiang-wt/module-dependency-guard-20261026`。证据在已忽略的 `build/artifacts/module-dependency-guard-20261026/`。

### 6.1 fresh Release 构建（先于运行）

```powershell
D:/Unity/dotnet/dotnet.exe build "dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj" -c Release --no-incremental --nologo
```

实际：`已成功生成`，`0 个警告`，`0 个错误`，**exit 0**。日志 `build-r2-final.log`。

### 6.2 direct runner 全类

```powershell
D:/Unity/dotnet/dotnet.exe "dotnet/tests/Mahjong.Architecture.Tests/bin/Release/net8.0/Mahjong.Architecture.Tests.dll" `
  -noLogo -reporter default `
  -class "Mahjong.Architecture.Tests.DependencyDirectionTests" `
  -result-xml "build/artifacts/module-dependency-guard-20261026/architecture-class.xml"
```

实际：`Total: 78, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`，**exit 0**；XML `total=78 passed=78 failed=0 errors=0 skipped=0 not-run=0`。

### 6.3 direct runner 定向 shadow 方法

```powershell
D:/Unity/dotnet/dotnet.exe "dotnet/tests/Mahjong.Architecture.Tests/bin/Release/net8.0/Mahjong.Architecture.Tests.dll" `
  -noLogo -reporter default `
  -method "Mahjong.Architecture.Tests.DependencyDirectionTests.RedirectedProjectReferenceToUndiscoveredPathIsRejected" `
  -result-xml "build/artifacts/module-dependency-guard-20261026/architecture-method-shadow.xml"
```

实际：Total 1 / 0/0/0/0，**exit 0**。

### 6.4 direct runner 定向 4 项前缀绕过

```powershell
D:/Unity/dotnet/dotnet.exe "dotnet/tests/Mahjong.Architecture.Tests/bin/Release/net8.0/Mahjong.Architecture.Tests.dll" `
  -noLogo -reporter default `
  -method "Mahjong.Architecture.Tests.DependencyDirectionTests.DomainCannotBorrowSystemOrMicrosoftPrefixesAsCover" `
  -result-xml "build/artifacts/module-dependency-guard-20261026/architecture-method-prefix.xml"
```

实际：Total 4 / 0/0/0/0，**exit 0**。

### 6.5 仅新工程 `dotnet test`

```powershell
D:/Unity/dotnet/dotnet.exe test "dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj" -c Release --no-build --nologo
```

实际：`失败: 0，通过: 78，已跳过: 0，总计: 78`，**exit 0**。

### 6.6 原独立探针（未修改）

```powershell
$reviewDir='D:/majiang-wt/module-dependency-guard-20261026/build/artifacts/codex-dependency-review-16a0e3447d304238817a620e8be48936'
& D:/Unity/dotnet/dotnet.exe build "$reviewDir/probe/Probe.csproj" -c Release --no-incremental --nologo
& D:/Unity/dotnet/dotnet.exe "$reviewDir/probe/bin/Release/net8.0/Probe.dll" 'D:/majiang-wt/module-dependency-guard-20261026/dotnet/tests/Mahjong.Architecture.Tests/bin/Release/net8.0/Mahjong.Architecture.Tests.dll' 'D:/majiang-wt/module-dependency-guard-20261026' $reviewDir
```

实际：探针构建 exit 0；运行 6 个 `diagnostics=1`，`CONFIRMED_FALSE_GREENS=0`，**exit 0**。日志 `probe-final.log`。

### 6.7 自建诊断复验探针（独立版本，原探针不动）

`build/artifacts/module-dependency-guard-20261026/diagnostic-probe/` 加载真实测试 DLL，打印具体诊断：

```text
ASSEMBLY_FORBIDDEN_THIRD_PARTY Mahjong.Domain -> System.Reactive: third-party assembly reference is not in this module's exact allowlist
ASSEMBLY_FORBIDDEN_THIRD_PARTY Mahjong.Domain -> System.IO.Abstractions: third-party assembly reference is not in this module's exact allowlist
ASSEMBLY_UNAUTHORIZED_SHARED_FRAMEWORK Mahjong.Domain -> Microsoft.AspNetCore.Http: shared-framework assembly is not authorized for this module
ASSEMBLY_FORBIDDEN_THIRD_PARTY Mahjong.Domain -> Microsoft.Extensions.UnknownVendor: third-party assembly reference is not in this module's exact allowlist
PROJECT_REFERENCE_TARGET_NOT_DISCOVERED Mahjong.TableRuntime -> ..\..\shadow\Mahjong.Domain.csproj: ... same-basename shadow targets are rejected
PROJECT_UNSUPPORTED_DECLARATION Mahjong.TableRuntime -> Reference: <Reference> declaration 'Newtonsoft.Json' ... fails closed
```

实际：`DIAGNOSTIC_PROBE_FAILURES=0`，**exit 0**。日志 `diagnostic-probe.log`。

### 6.8 自动发现（未改 verify 脚本）

```text
PATTERN dotnet\tests\*\*.Tests\*.Tests.csproj -> 0
PATTERN dotnet\src\*\*.Tests\*.Tests.csproj  -> 0
PATTERN dotnet\tests\*\*.csproj               -> 10
  dotnet\tests\Mahjong.Architecture.Tests\Mahjong.Architecture.Tests.csproj  isTest=True
```

本工程命中第三个 glob `dotnet\tests\*\*.csproj`，并由 `BaseName -like '*.Tests'` 判为 test；**不是**嵌套 `*.Tests\*.Tests.csproj` glob。`tools/verify-v5.ps1` 未改，SHA 见 §7。

---

## 7. 证据 SHA-256

| 文件 | SHA-256 |
|---|---|
| `DependencyDirectionTests.cs` | `E3B9EAF1E73B41EAF7D3BB0619D027B1399DA319D18279B752E46653B474ACA3` |
| `Mahjong.Architecture.Tests.csproj` | `7A48277B1D1CBAB56E64600656C85C23F320358755E9F7FBCB03646ACB2DA927` |
| `build-r2-final.log` | `1FC584ACE7BA2BA0FE6565F46ACBDD55F90981D821194FA38029CC91660C116C` |
| `direct-class.log` | `7E5D532596C0ADF7B41734D4C5E672FD095267395EE2F0BA12E29BBCA825AF8A` |
| `architecture-class.xml` | `D33E003730BF8AABA1E6E95EB8E6C81BA889A0006FA56669FA17FF06481CE8CD` |
| `direct-method-shadow.log` | `C79A384043AB89DCD17923F0DFBF3FEE8754E444AC4868726A48C68128AB46B2` |
| `architecture-method-shadow.xml` | `9BFAA7DF04D5BC636E4F77B7C1F5172193290E6E7676BAB8BDF78FDF29686E8D` |
| `direct-method-prefix.log` | `352E58EBC615601D195AE44CB3A57718D27AD55B4E13546C5B961E60A75F5733` |
| `architecture-method-prefix.xml` | `440C4ED74574DBD2DB79B5204804A295079E09FEAE1599E66C0EFB1464706D0A` |
| `dotnet-test.log` | `42BD3ADAD23DF2646C3798F7EC450BD7EA5FA4884F275D28BEB6E12F5C0E5DFA` |
| `list-classes.log` | `ECA0D077A78E40CBB84ACD4F827B07C28FD519FBF0B52D88679F263929D5C3E1` |
| `assembly-refs.txt` | `94E09E356F0F56915196E0B442989850A436AC643B8D4667B9A08A999525C5A3` |
| `project-graph.txt` | `0FAA4AB217A956E434A6C661BD94AC2DD0CAD7E86B454793D2136163A9905884` |
| `auto-discovery-probe.log` | `6E4583DA44D3D1D74B0CF43A795A1D2FC658BEDA457F04AC0E45BB5E9E7A10E3` |
| `diagnostic-probe-build.log` | `E74AAF6DE2425F72633EE5F96C831AA7EB67C11CA09B04FF8D3FAEFB6672FBEE` |
| `diagnostic-probe.log` | `E54341C901E4DB850681BAF53177C1DC3F72521BB21477F158C564FC0863E60B` |
| 原 `probe/Program.cs`（未改） | `7589C59B47E5657AE2AFC4E8A06621292B39EA3222FF88706D7351B5728C61EA` |
| 原 `probe.log`（未改） | `C7D54A2D2E4898217D5618A8CC4E813976DC050023DA8A79D9FE1DFA513D70CA` |
| 修复后 `probe-final.log` | `332408F895B6C8A37AC92E4F2956398A373166B4B1057557E400279214EE5976` |
| `tools/verify-v5.ps1`（未改） | `FA8D539BB6E036A7FD002011F001F5D5FDB27BA8B20CD5A9089098E87F3CE307` |

本文件自身 SHA 由调用方在提交后读取；测试源码/工程文件哈希在提交前固定。

---

## 8. 覆盖边界（如实单列）

1. **未运行**统一 `tools/verify-v5.ps1` 全量、PG、Unity/IL2CPP、WSS、桌面 GL、远端 CI；只做相关工程/类与独占 TEMP 负例。
2. 框架目录分类依赖运行 CLR 旁安装的 `Microsoft.NETCore.App` / `Microsoft.AspNetCore.App`；目录缺失或集合为空会显式失败，不静默放行。集合按文件名精确成员判定，不使用前缀。
3. 项目声明解析**不是**完整 MSBuild 求值器：`Condition` 一律拒绝（不判断真假）、`Update/Remove` 拒绝、多值/通配/属性函数拒绝、`Reference`/`ReferencePath`/`COMReference`/`FrameworkReference`/`Import` 拒绝；这些形式 fail closed，可能对将来合法但未建模的 MSBuild 写法偏严格。
4. canonical 校验只覆盖 `dotnet/src` 下被发现的 8 个工程文件；不处理任意路径重定向到仓库外已存在但未纳管的工程（会因不在 canonical map 而拒绝）。
5. 程序集检查是编译期引用元数据，不含 IL 调用图、反射/动态加载、DI 运行时装配、条件编译分支；程序集新鲜度由外部 `--no-incremental` 构建保证。
6. NuGet 传递依赖图未遍历；第三方以实际程序集引用为准并逐模块显式允许，未列入的传递第三方会 fail-closed。
7. 不固定包版本；只记录当前 Google.Protobuf 3.28.3、Grpc.Tools 2.68.1、Npgsql 8.0.9。
8. `LocalHost` 无源码、空程序集；`LobbyService` 未发出 Contracts 引用；两者项目边仍强制，程序集层不要求未使用引用。
9. 本守卫**不是** G1 门 3 的替代或升级；门 3 现状 PASS 仍以既有 8 处断言与双 run 为准。

---

## 9. PASS / FAIL / BLOCKED / NOT_RUN

| 状态 | 项目 |
|---|---|
| **PASS** | fresh Release `--no-incremental` 构建（exit 0，0/0） |
| **PASS** | direct runner 全类 78/78（0 failed/errors/skipped/not-run，exit 0） |
| **PASS** | direct runner 定向 shadow 1/1（exit 0） |
| **PASS** | direct runner 定向前缀绕过 4/4（exit 0） |
| **PASS** | 仅新工程 `dotnet test --no-build` 78/78（exit 0） |
| **PASS** | 原独立探针 6 项全部 diagnostics=1，`CONFIRMED_FALSE_GREENS=0`（exit 0） |
| **PASS** | 自建诊断复验 6 项具体 from->to，`FAILURES=0`（exit 0） |
| **PASS** | 项目声明 canonical 目标 + 不支持声明 fail closed |
| **PASS** | 真实框架目录身份 + 逐模块共享框架授权 |
| **PASS** | 未改 verify 脚本的自动发现（第三个 glob + `.Tests`） |
| **FAIL** | 无 |
| **BLOCKED** | 无（未新装全局工具/包） |
| **NOT_RUN** | 统一 verify 全量、PG、Unity/IL2CPP、WSS、桌面 GL、远端 CI、push/PR/合并/部署 |

---

## 10. 回滚与集成边界

- 回滚边界：本轮返修 commit 仍只涉及三个 expectedFiles 允许范围（本轮 csproj 无变化）；回退该 commit 即回到第 1 轮 `804f173`（该版本存在本轮已确认的 6 项假绿，不可作为交付结论）。
- `build/artifacts/module-dependency-guard-20261026/` 与 caller 证据根均为 ignored 证据，可保留或整体删除，不影响 tracked 树。
- 本树为 caller 隔离集成的 temporary worktree；集成后保留 merge-back 义务，不删树。
- 不 push、不建组件 PR、不合 `v5`、不触发 CI、不部署、不写 `complete.flag`；由 caller 独立验收并统一更新既有唯一 PR19。
- 结论仅为 I-5/S-1 组件级落地，不构成 G1/G5 通过，不改变“V4 唯一生产架构、G1 整体未最终批准、G5 未完成”的阶段事实。
