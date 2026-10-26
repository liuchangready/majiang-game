# V5 模块依赖方向精确白名单（已接受 I-5 / S-1 落地）

日期：2026-10-26
状态：**组件级实现与定向验证完成；非 G1/G5 门槛，不改变任何阶段判定**
基线：`d9d17d8299fc348149f581a0741ddd07b1f519f6`（既有唯一 PR19 已评审/发布头）
工作树：`D:/majiang-wt/module-dependency-guard-20261026`
分支：`wt/v5-module-dependency-guard-20261026`

---

## 0. 元信息与结论

本任务落地 `V5-1-REVIEW.md` §9.2 的 **S-1**（依赖方向精确白名单增强），也就是 §7.3/§12.2 的 **I-5**（建议，非门条款）。它补齐 `V5-1-REVIEW.md` §3.3 与 `V5-G1-RUNTIME-REVIEW-20261026.md` §3 明确列出的未保护边：

- TableRuntime 的**正向**允许集（Domain/Ai/Contracts）；
- Wire ✗ Domain/Client；
- LocalHost 的精确项目边与其余禁边；
- 8 个程序集的实际 Mahjong/第三方引用边界。

结论（只按实测写）：

| 项 | 结果 |
|---|---|
| 新测试工程 fresh Release `--no-incremental` 构建 | **PASS**，exit 0，0 warning / 0 error |
| direct runner 全类执行 | **PASS**，Total 60 / Failed 0 / Errors 0 / Skipped 0 / Not Run 0，exit 0 |
| direct runner 定向方法执行 | **PASS**，Total 1 / Failed 0 / Errors 0 / Skipped 0 / Not Run 0，exit 0 |
| `dotnet test` 仅新工程（`--no-build`） | **PASS**，60 passed / 0 failed / 0 skipped，exit 0 |
| 项目声明图精确允许集 | **PASS** |
| 程序集实际引用允许集 + 必要正向边 | **PASS** |
| 负例矩阵（禁止边/未知程序集/未知第三方/缺失必要边/合法对照） | **PASS**（60 项内全部真实断言） |
| 未修改 unified verify 脚本即可自动发现 | **PASS**（只读 glob 复现，见 §6.5） |
| G1/G5 门槛影响 | **无**；不升级为强制门，不重设门槛 |

**没有** push、组件 PR、合并 `v5`、触发 CI、部署或 `complete.flag`。本文件与两个测试文件构成唯一最小本地 commit。

---

## 1. 范围与基线

### 1.1 expectedFiles（唯一改动）

| 文件 | 性质 |
|---|---|
| `dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj` | 新增测试工程（xUnit v3 可执行） |
| `dotnet/tests/Mahjong.Architecture.Tests/DependencyDirectionTests.cs` | 新增校验器、真实图断言、负例矩阵 |
| `doc/v5-重构/V5-MODULE-DEPENDENCY-GUARD.md` | 本文件 |

未改动：任何生产项目/csproj、现有测试、`dotnet/Mahjong.sln`、`tools/verify-v5.ps1`、`tools/v5-verify.manifest.json`、fixtures、`CLAUDE.md`、`doc/ui`、`round-preparation/PROMPTS.md`、Unity 环境/Resources/Burst/Plugins/ProjectSettings。

### 1.2 基线核实

提交前实测：

```text
git rev-parse HEAD
d9d17d8299fc348149f581a0741ddd07b1f519f6
git rev-parse --abbrev-ref HEAD
wt/v5-module-dependency-guard-20261026
git status --porcelain=v1 --untracked-files=all
（仅三个 expectedFiles）
git diff --check
（无输出）
```

`git worktree list` 中本树登记为 `D:/majiang-wt/module-dependency-guard-20261026  d9d17d8 [wt/v5-module-dependency-guard-20261026]`，`git rev-parse --show-toplevel` 返回本树，未触碰主 checkout `D:/majiang`、规划树 `D:/majiang-v5-planning`、兄弟树。

### 1.3 与门槛的关系

- `V5-1-REVIEW.md` §3.3/§9.2 与 `V5-G1-RUNTIME-REVIEW-20261026.md` §3 均明确：门 3 已 PASS；精确白名单是 **I-5/S-1 建议，非门条款**。
- 本任务**不**新增 G1 强制门，**不**改变 G1/G5 判定，**不**实施受 G1 阻断的 P4/P5 集成。
- V4 仍是唯一生产架构；V5 G1 整体尚未最终批准，G5 未完成。

---

## 2. 双源：项目声明图 vs 程序集实际引用图

测试读取两个**独立**事实源，不读取新测试工程自己的引用闭包：

1. **项目声明图**：受控 XML 解析 `dotnet/src/<Name>/<Name>.csproj` 的 `ProjectReference` / `PackageReference`。
2. **程序集实际引用图**：`System.Reflection.Metadata` + `PEReader` 读取每个生产项目 fresh Release 输出 DLL 的 `AssemblyReferences`。

两者语义不同，不能混用：

- 项目声明边是**精确正/负允许集**：允许的必须声明，声明的必须被允许；
- 程序集引用只要求在**允许闭包内**，且只强制**代码实际需要**的正向边。编译器不会为未使用的 `ProjectReference` 发出程序集引用，因此不能要求此类边必定出现在 DLL。

### 2.1 八个真实 src 工程的项目声明图（实测）

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

发现规则：只枚举 `dotnet/src/*/`，每个目录必须恰好一个 `.csproj`，工程名必须等于目录名。测试显式断言 **8**，并与固定名单逐项比对；空枚举、少一个、多一个、名字漂移都会失败。

### 2.2 八个程序集的实际引用（fresh Release，逐 DLL 读取）

| 程序集 | 实际引用（分类后） | 强制正向边 |
|---|---|---|
| `Mahjong.Domain` | netstandard（BCL） | 无 |
| `Mahjong.Contracts` | netstandard（BCL） | 无 |
| `Mahjong.Contracts.Wire` | Contracts、Google.Protobuf、netstandard | Contracts；Google.Protobuf |
| `Mahjong.Ai` | Domain、netstandard | Domain |
| `Mahjong.TableRuntime` | Domain、Ai、Contracts、netstandard | Domain；Ai；Contracts |
| `Mahjong.Client` | Contracts、Contracts.Wire、Google.Protobuf、netstandard | Contracts；Contracts.Wire；Google.Protobuf |
| `Mahjong.LocalHost` | netstandard（仅 BCL） | 无（见 §2.3） |
| `Mahjong.LobbyService` | Microsoft.AspNetCore* / Microsoft.Extensions*（共享框架）、System.*（BCL）、Npgsql（第三方） | Npgsql |

`Client -> Google.Protobuf` 是真实使用（`TableSession.cs` 使用 `ByteString`），虽由 Wire 传递提供，但在 DLL 中实际发出，故列为必要正向边。`LobbyService` 未发出 `Mahjong.Contracts`，故不列为必要程序集边。

### 2.3 声明但未发出的边（明确区分，不伪造正向断言）

| 声明边 | 实际 DLL | 处理 |
|---|---|---|
| `LocalHost -> TableRuntime` | 未发出（`Mahjong.LocalHost` 当前**只有 csproj、没有 .cs 源码**，程序集为空） | 项目图强制精确存在；程序集层不要求 |
| `LobbyService -> Contracts` | 未发出（Lobby 未使用 Contracts 类型，仅有项目引用） | 项目图强制精确存在；程序集层不要求，但允许闭包含 Contracts |

这正是任务书“不要要求优化器未实际发出的未使用引用必定出现在编译 DLL”的落点；若未来 LocalHost/Lobby 真正使用这些类型，程序集层允许闭包已覆盖，无需放宽规则。

### 2.4 允许闭包（程序集层）

项目边精确，程序集层允许**项目边闭包**（防止公共 API 暴露传递类型时误报）：

| 模块 | 允许的 Mahjong 程序集引用 | 其中必须出现 |
|---|---|---|
| Domain | ∅ | ∅ |
| Contracts | ∅ | ∅ |
| Wire | Contracts | Contracts |
| Ai | Domain | Domain |
| TableRuntime | Domain、Ai、Contracts | Domain、Ai、Contracts |
| Client | Contracts、Contracts.Wire | Contracts、Contracts.Wire |
| LocalHost | TableRuntime、Domain、Ai、Contracts（TableRuntime 闭包） | ∅ |
| LobbyService | Contracts | ∅ |

### 2.5 第三方与 BCL 分类

第三方（**逐模块精确、必须显式列出**）：

| 模块 | 允许且必须出现的第三方程序集 |
|---|---|
| Wire | Google.Protobuf |
| Client | Google.Protobuf（真实使用 `ByteString`） |
| LobbyService | Npgsql |
| 其余五个 | ∅ |

包声明精确允许集：Wire = `Google.Protobuf` + `Grpc.Tools`（后者必须保持 `PrivateAssets=All`，否则诊断 `PROJECT_BUILD_ONLY_PACKAGE_LEAKED`）；Lobby = `Npgsql`；其余为空。

BCL/共享框架**不做一刀切拒绝**，按固定分类器识别：

- BCL：`mscorlib`、`netstandard`、`System`、`System.*`、`Microsoft.CSharp`、`Microsoft.VisualBasic`、`Microsoft.Win32`、`Microsoft.Win32.*`、`WindowsBase`；
- 共享框架：`Microsoft.AspNetCore`、`Microsoft.AspNetCore.*`、`Microsoft.Extensions.*`、`Microsoft.Net.Http.*`；
- 其余一律归为 third-party，必须命中该模块的显式允许集，否则失败。因此 `Microsoft.Data.SqlClient`、`Newtonsoft.Json`、`Grpc.Core` 等不会被 `System.*`/`Microsoft.*` 前缀吞掉。

Domain 的 `System.Net`/`System.Data`/`Microsoft.Data` 禁边仍由既有 `TilePositionMapTests.cs:1049` 守护；本守卫不重复、不弱化它。

---

## 3. 校验器与诊断

### 3.1 真实校验器（非字面量自证）

`ModuleDependencyValidator` 接收三份输入：已知 8 工程名、项目/程序集事实、规则表，返回 `DependencyDiagnostic` 列表。所有测试都断言列表内容（代码、from、to、detail），而不是只比较硬编码字符串。

规则表自身还有两道自洽检查：

1. `Required*` 必须是 `Allowed*` 的子集；
2. `AllowedMahjongAssemblyReferences` 必须落在该模块**项目声明边的传递闭包**内（防止规则表被悄悄放宽，例如给 Domain 允许 Contracts）。

### 3.2 诊断码

| 代码 | 含义 |
|---|---|
| `PROJECT_MISSING` | 规则对应的生产工程未被发现 |
| `PROJECT_FORBIDDEN_EDGE` | 声明了不在精确允许集的 `ProjectReference` |
| `PROJECT_MISSING_EDGE` | 允许集要求的声明边缺失 |
| `PROJECT_UNKNOWN_MAHJONG_REFERENCE` | 引用了 8 工程之外的 `Mahjong.*` |
| `PROJECT_UNKNOWN_MAHJONG_PROJECT` | 发现图里出现规则表之外的 `Mahjong.*` 工程 |
| `PROJECT_FORBIDDEN_PACKAGE` | 包不在该模块精确允许集 |
| `PROJECT_MISSING_PACKAGE` | 允许集要求的包声明缺失 |
| `PROJECT_BUILD_ONLY_PACKAGE_LEAKED` | 构建期包未保持 `PrivateAssets=All` |
| `PROJECT_NOT_IN_RULE_TABLE` | 非 Mahjong 工程未纳管 |
| `ASSEMBLY_MISSING` | fresh Release DLL 不存在 |
| `ASSEMBLY_FORBIDDEN_MAHJONG_REFERENCE` | 程序集引用了允许闭包外的已知 Mahjong 程序集 |
| `ASSEMBLY_MISSING_MAHJONG_REFERENCE` | 必要正向 Mahjong 引用缺失 |
| `ASSEMBLY_UNKNOWN_MAHJONG_REFERENCE` | 程序集引用了 8 工程之外的 `Mahjong.*` |
| `ASSEMBLY_UNKNOWN_MAHJONG_ASSEMBLY` | 检查到规则表之外的 Mahjong 程序集 |
| `ASSEMBLY_FORBIDDEN_THIRD_PARTY` | 第三方引用不在该模块精确允许集 |
| `ASSEMBLY_MISSING_THIRD_PARTY` | 必要第三方引用缺失 |
| `ASSEMBLY_NOT_IN_RULE_TABLE` | 非 Mahjong 程序集未纳管 |
| `RULE_INCONSISTENT` | 规则表自身不一致（含闭包越界） |

每条 from->to 诊断都带具体模块名与目标名，不是“某处失败”。

---

## 4. 负例矩阵（统一）

负例分两层：**内存合成图**覆盖诊断分支，**自有随机 TEMP 中的真实 csproj 副本**覆盖 XML 解析/发现路径。生产 csproj 一律不改。

| # | 类别 | 注入 | 期望诊断 | 测试 |
|---|---|---|---|---|
| N1 | 禁止项目边（12 组） | Domain→Contracts；Contracts→Wire；Wire→Domain；Wire→Client；Ai→Contracts；TableRuntime→Wire；TableRuntime→LocalHost；Client→Domain；Client→TableRuntime；LocalHost→Client；Lobby→Domain；Lobby→TableRuntime | `PROJECT_FORBIDDEN_EDGE`，from/to 精确 | `ForbiddenProjectEdgesAreRejectedWithSpecificDiagnostics` |
| N2 | 禁止程序集边（9 组） | Domain→Contracts；Wire→Domain；Wire→Client；Ai→Contracts；Client→Domain；Client→TableRuntime；LocalHost→Client；Lobby→TableRuntime；TableRuntime→Wire | `ASSEMBLY_FORBIDDEN_MAHJONG_REFERENCE` | `ForbiddenAssemblyMahjongReferencesAreRejectedWithSpecificDiagnostics` |
| N3 | 未知 Mahjong 工程边 | TableRuntime→Mahjong.Shadow | `PROJECT_UNKNOWN_MAHJONG_REFERENCE` | `UnknownMahjongProjectEdgeIsRejected` |
| N4 | 未知 Mahjong 工程节点 | 图中新增 Mahjong.Shadow | `PROJECT_UNKNOWN_MAHJONG_PROJECT` | `UnknownMahjongProjectNodeIsRejected` |
| N5 | 未知第三方包 | Client + Newtonsoft.Json | `PROJECT_FORBIDDEN_PACKAGE` | `UnknownThirdPartyPackageIsRejected` |
| N6 | 缺失必要项目边 | 删除 TableRuntime→Domain | `PROJECT_MISSING_EDGE` | `MissingAllowedProjectEdgeIsRejected` |
| N7 | 缺失包声明 | 删除 Wire 的 Google.Protobuf | `PROJECT_MISSING_PACKAGE` | `MissingDeclaredPackageIsRejected` |
| N8 | 构建期包泄漏 | Grpc.Tools 去掉 `PrivateAssets=All` | `PROJECT_BUILD_ONLY_PACKAGE_LEAKED` | `BuildOnlyPackageMustRemainPrivateAssetsAll` |
| N9 | 未知 Mahjong 程序集引用 | Client→Mahjong.Shadow | `ASSEMBLY_UNKNOWN_MAHJONG_REFERENCE` | `UnknownMahjongAssemblyReferenceIsRejected` |
| N10 | 未知 Mahjong 程序集节点 | 检查 Mahjong.Shadow | `ASSEMBLY_UNKNOWN_MAHJONG_ASSEMBLY` | `UnknownMahjongAssemblyNodeIsRejected` |
| N11 | 未知第三方程序集（4 组） | Client→Newtonsoft.Json；Lobby→MySql.Data；Wire→Npgsql；Domain→Google.Protobuf | `ASSEMBLY_FORBIDDEN_THIRD_PARTY` | `UnknownThirdPartyAssemblyReferenceIsRejected` |
| N12 | 缺失必要 Mahjong 程序集边 | TableRuntime 去掉 Domain | `ASSEMBLY_MISSING_MAHJONG_REFERENCE` | `MissingRequiredAssemblyMahjongReferenceIsRejected` |
| N13 | 缺失必要第三方程序集边 | Wire 去掉 Google.Protobuf | `ASSEMBLY_MISSING_THIRD_PARTY` | `MissingRequiredThirdPartyAssemblyReferenceIsRejected` |
| N14 | 合法对照 | 追加 `System.Data.Common`、`System.Collections`、`Microsoft.AspNetCore.Http`、`Microsoft.Extensions.DependencyInjection.Abstractions`、`Npgsql`（Lobby）、`Google.Protobuf`（Wire） | 无诊断 | `LegalBclSharedFrameworkAndPerModuleThirdPartyReferencesStayGreen` |
| N15 | 规则表被放宽 | 给 Domain 的 assembly allowlist 加入 Contracts（项目闭包外） | `RULE_INCONSISTENT`（detail 含 closure） | `RuleTableCannotWidenAnAssemblyAllowlistBeyondTheProjectClosure` |
| N16 | 真实 csproj 副本变异 | 复制 8 个 csproj 到自有随机 TEMP，向 TableRuntime 注入 `..\Mahjong.Client\Mahjong.Client.csproj` | `PROJECT_FORBIDDEN_EDGE`，TableRuntime→Client | `ForbiddenEdgeInjectedIntoARealProjectFileIsDetectedThroughXmlParsing` |
| N17 | 分类器 | 17 个 BCL/共享框架/Mahjong/第三方样本 | 精确 `ReferenceKind` | `ReferenceClassifierSeparatesBclFrameworkMahjongAndThirdParty` |

TEMP 变异负例的清理边界：随机根名 `mahjong-arch-guard-<guid>`；递归删除前解析 `Path.GetFullPath`，断言仍在进程 TEMP 之下、名字前缀属于本测试、且根不是 reparse point，否则拒绝删除。

**不是空测试/跳过**：60 项全部真实执行；无 `Skip`、无 `Total=0`、无旧 DLL（构建 `--no-incremental` 先于运行，见 §6.1）。

---

## 5. 覆盖范围（精确清单）

### 5.1 项目边（声明层，精确允许/禁止）

| 模块 | 允许 | 明确禁止（非穷举，规则为精确白名单，未列即禁） |
|---|---|---|
| Domain | ∅ | 任何 Mahjong 工程、任何包 |
| Contracts | ∅ | 任何 Mahjong 工程、任何包 |
| Wire | Contracts | Domain、Client、TableRuntime、LocalHost、Lobby 等 |
| Ai | Domain | Contracts、Wire、TableRuntime、LocalHost、Lobby 等 |
| TableRuntime | Domain、Ai、Contracts | Wire、Client、LocalHost、Lobby 等 |
| Client | Contracts、Wire | Domain、TableRuntime、LocalHost、Ai、Lobby 等 |
| LocalHost | TableRuntime | Client、Domain、Ai、Contracts、Lobby 等 |
| Lobby | Contracts | Domain、TableRuntime、LocalHost、Client、Wire、Ai 等 |

### 5.2 程序集引用（实际层）

- 8 个 DLL 全覆盖；Mahjong 引用按闭包检查，第三方按模块精确检查；
- 必要正向边：Wire→Contracts/Google.Protobuf；Ai→Domain；TableRuntime→Domain/Ai/Contracts；Client→Contracts/Wire/Google.Protobuf；Lobby→Npgsql；
- Domain/Contracts 无 Mahjong 与第三方引用；LocalHost 当前仅 BCL。

### 5.3 新工程自动发现（未改脚本）

`tools/verify-v5.ps1` 的发现段（未修改，SHA `FA8D539B…F3CE307`）使用：

```powershell
foreach ($pattern in @('dotnet\tests\*\*.Tests\*.Tests.csproj',
                       'dotnet\src\*\*.Tests\*.Tests.csproj',
                       'dotnet\tests\*\*.csproj')) { ... }
$isTest = $found.BaseName -like '*.Tests'
```

本工程路径是 `dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj`，**命中第三个 glob** `dotnet\tests\*\*.csproj`，并由 `BaseName -like '*.Tests'` 判为 test；它**不**命中第一个“嵌套一层”的 `*.Tests\*.Tests.csproj` glob。只读复现结果：

```text
PATTERN dotnet\tests\*\*.Tests\*.Tests.csproj -> 0
PATTERN dotnet\src\*\*.Tests\*.Tests.csproj  -> 0
PATTERN dotnet\tests\*\*.csproj               -> 10
  dotnet\tests\Mahjong.Architecture.Tests\Mahjong.Architecture.Tests.csproj  isTest=True
```

因此“未修改 verify 脚本即可发现”成立，但成立路径是第三个 glob + `.Tests` 分类，而不是嵌套 glob。测试 `VerifyScriptAutoDiscoveryRuleFindsThisUnmodifiedProject` 固定了该事实。

---

## 6. 实测命令与结果（原样）

工作目录：`D:/majiang-wt/module-dependency-guard-20261026`
所有证据写入本树已忽略的 `build/artifacts/module-dependency-guard-20261026/`。

### 6.1 fresh Release 构建（先于运行）

```powershell
D:/Unity/dotnet/dotnet.exe build "dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj" -c Release --no-incremental --nologo
```

实际：`已成功生成`，`0 个警告`，`0 个错误`，**exit 0**。日志 `build-final4.log`。
构建同时重编 8 个生产工程；运行只使用本次产出的 DLL。

### 6.2 direct runner（全类，XML）

```powershell
D:/Unity/dotnet/dotnet.exe "dotnet/tests/Mahjong.Architecture.Tests/bin/Release/net8.0/Mahjong.Architecture.Tests.dll" `
  -noLogo -reporter default `
  -class "Mahjong.Architecture.Tests.DependencyDirectionTests" `
  -result-xml "build/artifacts/module-dependency-guard-20261026/architecture-class.xml"
```

实际：`Total: 60, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`，**exit 0**。
XML 属性：`total=60 passed=60 failed=0 errors=0 skipped=0 not-run=0`。
60 = 18 个 `Fact` + 42 个 `Theory` 展开用例。

### 6.3 direct runner（定向方法，XML）

```powershell
D:/Unity/dotnet/dotnet.exe "dotnet/tests/Mahjong.Architecture.Tests/bin/Release/net8.0/Mahjong.Architecture.Tests.dll" `
  -noLogo -reporter default `
  -method "Mahjong.Architecture.Tests.DependencyDirectionTests.ForbiddenEdgeInjectedIntoARealProjectFileIsDetectedThroughXmlParsing" `
  -result-xml "build/artifacts/module-dependency-guard-20261026/architecture-method.xml"
```

实际：`Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`，**exit 0**；XML `total=1 passed=1`。
未使用 `dotnet test` 外层 `--filter` 作为定向依据。

### 6.4 仅新工程的 `dotnet test`（补充口径）

```powershell
D:/Unity/dotnet/dotnet.exe test "dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj" -c Release --no-build --nologo
```

实际：`已通过! - 失败: 0，通过: 60，已跳过: 0，总计: 60`，**exit 0**。日志 `dotnet-test.log`。

### 6.5 发现规则只读复现

```powershell
$root = (Get-Location).Path
Get-ChildItem -Path (Join-Path $root 'dotnet\tests\*\*.csproj') -File |
  ForEach-Object { $_.FullName; $_.BaseName -like '*.Tests' }
```

实际命中本工程且 `isTest=True`，见 `auto-discovery-probe.log`。未执行统一 verify、PG、Unity、WSS 全量。

### 6.6 初始编译迭代说明

首次/第二次本地编译分别暴露：`ReferenceKind` 可访问性不一致（CS0051）、xUnit 分析器 xUnit2031；修复后最终 `--no-incremental` 构建 0 warning/0 error。失败日志 `build-1.log`、`build-2.log` 保留，未以旧产物运行。

---

## 7. 证据 SHA-256

| 文件 | SHA-256 |
|---|---|
| `dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj` | `7A48277B1D1CBAB56E64600656C85C23F320358755E9F7FBCB03646ACB2DA927` |
| `dotnet/tests/Mahjong.Architecture.Tests/DependencyDirectionTests.cs` | `D645FA8492A1E84109B4A144A708AA76E6A79F85A48A9DAE8DF491592438334A` |
| `build/artifacts/.../build-final4.log` | `D74778BED28AC9CB7FB7C02E91CD4F6D0567CA30438136E285015DF530AB2990` |
| `build/artifacts/.../direct-class.log` | `B45F50DBCF53DDF67EE2B800FD93D8C9B25DB00818C2AE75FD1C892C2D623F1E` |
| `build/artifacts/.../architecture-class.xml` | `66E15DE4D53FB2ABE8F6FB785B4979331D5D2C7DFCE1B21D36B7C5B7D7F2F582` |
| `build/artifacts/.../direct-method.log` | `A194469FCF8DD0770B63380F3E7C541597DF47A533C1226672EBD80B8694175F` |
| `build/artifacts/.../architecture-method.xml` | `4304A2AC21658D3E6890EAD5FCAB9BB84627427BAD12E7DA6284A49E35C51FAD` |
| `build/artifacts/.../dotnet-test.log` | `7D8526C7071CAB9B8EB62859774A408CF81AD26F30D52B475AB8246503CEC37C` |
| `build/artifacts/.../list-classes.log` | `ECA0D077A78E40CBB84ACD4F827B07C28FD519FBF0B52D88679F263929D5C3E1` |
| `build/artifacts/.../assembly-refs.txt` | `94E09E356F0F56915196E0B442989850A436AC643B8D4667B9A08A999525C5A3` |
| `build/artifacts/.../project-graph.txt` | `0FAA4AB217A956E434A6C661BD94AC2DD0CAD7E86B454793D2136163A9905884` |
| `build/artifacts/.../auto-discovery-probe.log` | `6E4583DA44D3D1D74B0CF43A795A1D2FC658BEDA457F04AC0E45BB5E9E7A10E3` |
| `tools/verify-v5.ps1`（未修改） | `FA8D539BB6E036A7FD002011F001F5D5FDB27BA8B20CD5A9089098E87F3CE307` |

本文件自身的 SHA 在提交后由调用方读取；测试源码/工程文件的哈希在提交前固定。

---

## 8. 明确不覆盖（NOT_RUN / 边界）

1. **未运行统一 `tools/verify-v5.ps1` 全量**、未运行 PG/Unity/Gradle/WSS/桌面 GL 全量；按任务书只做相关工程/类与独占 TEMP 负例。
2. **未运行远端 CI**、未 push、未建组件 PR、未合 `v5`、未部署。
3. 程序集检查是**编译期引用元数据**，不含 IL 调用图、反射/动态加载、DI 运行时装配、条件编译分支。
4. BCL/共享框架只做固定前缀分类，不逐个类型枚举；Domain 的 `System.Net`/`System.Data` 禁边仍由既有测试守护，本守卫不重复其负例。
5. NuGet **传递依赖图**未遍历；第三方以实际程序集引用为准并逐模块显式允许，未列入的传递第三方会 fail-closed（需显式规则）。
6. 不固定包版本；只记录当前 `Google.Protobuf 3.28.3`、`Grpc.Tools 2.68.1`、`Npgsql 8.0.9`。版本升级不由本守卫阻断。
7. 程序集新鲜度由外部 `--no-incremental` 构建保证；测试只能断言 DLL 存在且可读，不能证明其时间戳。
8. `LocalHost` 无源码、空程序集；其 `TableRuntime` 边只在项目层强制。`LobbyService` 的 `Contracts` 边同理。
9. 不检查 Unity/IL2CPP/Android/macOS 平台工程，不检查 `dotnet/src` 之外的生产 helper。
10. 本守卫**不是** G1 门 3 的替代或升级；门 3 的现状 PASS 仍以既有 8 处断言与双 run 为准。

---

## 9. PASS / FAIL / BLOCKED / NOT_RUN 分列

| 状态 | 项目 |
|---|---|
| **PASS** | fresh Release `--no-incremental` 构建（exit 0，0/0） |
| **PASS** | direct runner 全类 60/60（0 failed/errors/skipped/not-run，exit 0） |
| **PASS** | direct runner 定向方法 1/1（exit 0） |
| **PASS** | 仅新工程 `dotnet test --no-build` 60/60（exit 0） |
| **PASS** | 项目声明图精确允许集（8 工程，正/负双向） |
| **PASS** | 程序集引用闭包 + 必要正向边（8 DLL） |
| **PASS** | 负例矩阵 N1–N17（真实断言，含具体 from->to 诊断） |
| **PASS** | 未改 verify 脚本的自动发现（第三个 glob + `.Tests` 分类） |
| **FAIL** | 无 |
| **BLOCKED** | 无（所需依赖/包均已存在，未新装全局工具/包） |
| **NOT_RUN** | 统一 verify 全量、PG、Unity/IL2CPP、WSS、桌面 GL、远端 CI、push/PR/合并/部署 |

---

## 10. 回滚与集成边界

- 回滚边界：仅回退本任务最小 commit 的三个文件，即恢复基线；不涉及任何生产项目/测试/solution/verify 脚本/fixtures。
- `build/artifacts/module-dependency-guard-20261026/` 为 ignored 证据，可整体删除，不影响 tracked 树。
- 本树为 caller 隔离集成的 temporary worktree；集成后仍保留 **merge-back 义务**，不删树。
- 不 push、不建组件 PR、不合 `v5`、不触发 CI、不部署、不写 `complete.flag`；最终隔离集成与普通 push 由 caller 执行。
- 结论仅为 **I-5/S-1 组件级落地**，不构成 G1/G5 通过，不改变“V4 唯一生产架构、G1 整体未最终批准、G5 未完成”的阶段事实。
