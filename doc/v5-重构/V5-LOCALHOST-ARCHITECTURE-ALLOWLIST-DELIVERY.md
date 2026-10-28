# V5 LocalHost 架构允许清单修正交付（D-LH-CI-ARCH）

| 项 | 值 |
|---|---|
| 日期 | 2026-10-28（Asia/Shanghai） |
| 工作树 | `D:/majiang-wt/localhost-arch-ds-20261028` |
| 分支 | `wt/v5-localhost-arch-ds-20261028` |
| 基线 | `origin/v5` = `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；创建后 fast-forward 至已接受 PR20 头 `14f1da3a417d542fdccbf26cd4b77f8f9064a6d8` |
| 修正对象 | PR21 `c60061133be2a3350792d90d5c3c70ca2d0fa8a4` 的 `D-LH-CI-ARCH` 三项 Architecture 失败 |
| 唯一改动 | `dotnet/tests/Mahjong.Architecture.Tests/DependencyDirectionTests.cs` 与本文件 |
| 阶段事实 | G1 **APPROVED**；V4 仍是唯一生产架构；G2–G5 **未批准**；本树没有 `complete.flag`，本任务不创建 |
| 结论 | 仅关闭 LocalHost 架构允许清单与已接受合同的不一致；不宣称 PR21、G2 或任何远端 CI 通过 |

---

## 0. 结论摘要

1. `D-LH-CI-ARCH` 的三项失败已按已接受 LocalHost 通道合同修正：
   - `ProjectDeclarationGraphMatchesTheExactAllowlist`
   - `AssemblyReferenceGraphIsWithinTheAllowedClosureAndKeepsRequiredPositiveEdges`
   - `ForbiddenEdgeInjectedIntoARealProjectFileIsDetectedThroughXmlParsing`
2. LocalHost 项目声明层授权四条具体边：`TableRuntime`、`Contracts`、`Contracts.Wire`、`Client`；当前已接受空壳基线只把 `TableRuntime` 作为**必需**项目边，避免基线因尚未落地的实现而变红。
3. LocalHost 程序集层授权闭包为 `Ai`、`Client`、`Contracts`、`Contracts.Wire`、`Domain`、`TableRuntime`，第三方仅 `Google.Protobuf`；`Domain`/`Ai` 只作为传递闭包存在，不新增直接 `ProjectReference`。
4. 旧的 `LocalHost -> Client` 禁用负例已改为合同正例；原负例槽位替换为真实禁止的 `LocalHost -> LobbyService`，并新增服务端/反向/Unity/任意 Google/vendor/Socket 负例。
5. 新增隔离真实 XML fixture：内容与 PR21 `c600611` 的 LocalHost csproj 逐字节一致（652 bytes，SHA-256 `71652DDE...`），四条边整体放行；注入 `LocalHost -> LobbyService` 后 `Assert.Single(diagnostics)` 精确得到一个 `PROJECT_FORBIDDEN_EDGE`，不再被三条既有诊断污染。
6. 本树没有完整 LocalHost 生产实现，也未把 PR21 生产代码合入基线。因此本报告只证明**架构守卫的允许清单/正负矩阵**，不证明真实完整 host、P4.1d、冷恢复、黄金或 G2 通过。

---

## 1. 来源、基线与强制阅读

### 1.1 Git 事实

| 项 | 实际值 |
|---|---|
| `git rev-parse --show-toplevel` | `D:/majiang-wt/localhost-arch-ds-20261028` |
| `git rev-parse --abbrev-ref HEAD` | `wt/v5-localhost-arch-ds-20261028` |
| `git rev-parse HEAD` | `14f1da3a417d542fdccbf26cd4b77f8f9064a6d8` |
| 从 `origin/v5` 创建后 ff | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` 是 HEAD 祖先 |
| 初始 `git status --porcelain` | 空 |
| 嵌套 `AGENTS.md` | 仅有仓库根 `AGENTS.md`；`**/AGENTS.md` 未发现第二份 |
| `complete.flag` | 全树未发现，未创建 |

### 1.2 PR21 远端失败对象

| run | checkout | 本任务相关结果 |
|---|---|---|
| [push 40715805408](https://github.com/liuchangready/majiang-game/actions/runs/40715805408) | `c60061133be2a3350792d90d5c3c70ca2d0fa8a4` | Architecture 3 fail；源分支测试文件较旧，Total 78 |
| [PR 40715821874](https://github.com/liuchangready/majiang-game/actions/runs/40715821874) | PR 合成 checkout `0b6c48cf...` | Architecture 92 项中 3 fail（caller 进度记录），本树未重跑远端合成树 |

PR21 原始日志由 caller 保存在 `D:/majiang-wt/codex-progress-20261028-2345/artifacts/pr21-c600611-ci/`。本任务只读其中 Architecture 日志与项目 XML 事实，不复制 PR21 源码。

分母说明：c600611 源分支自身的测试文件较旧，push run 为 Total 78；PR 合成树使用 PR20 的测试文件，caller 记录为 Architecture 92（89 pass / 3 fail）。本树基线是 PR20 头，实测 92；两者不能横向当作同一次重试。

原始诊断（原样要点）：

```text
ProjectDeclarationGraphMatchesTheExactAllowlist:
  PROJECT_FORBIDDEN_EDGE Mahjong.LocalHost -> Mahjong.Client
  PROJECT_FORBIDDEN_EDGE Mahjong.LocalHost -> Mahjong.Contracts
  PROJECT_FORBIDDEN_EDGE Mahjong.LocalHost -> Mahjong.Contracts.Wire

AssemblyReferenceGraphIsWithinTheAllowedClosureAndKeepsRequiredPositiveEdges:
  ASSEMBLY_FORBIDDEN_THIRD_PARTY Mahjong.LocalHost -> Google.Protobuf
  ASSEMBLY_FORBIDDEN_MAHJONG_REFERENCE Mahjong.LocalHost -> Mahjong.Client
  ASSEMBLY_FORBIDDEN_MAHJONG_REFERENCE Mahjong.LocalHost -> Mahjong.Contracts.Wire

ForbiddenEdgeInjectedIntoARealProjectFileIsDetectedThroughXmlParsing:
  Assert.Single() Failure: The collection contained 4 matching items
  [3 条 LocalHost 既有禁止边 + TableRuntime -> Client 注入边]
```

### 1.3 PR21 LocalHost 真实项目 XML

只读来源：

```powershell
git show c60061133be2a3350792d90d5c3c70ca2d0fa8a4:dotnet/src/Mahjong.LocalHost/Mahjong.LocalHost.csproj
```

| 项 | 实际值 |
|---|---|
| Git blob | `f70e62d7cfe7576a1787f7ab25da09df022e6c9d` |
| 文件字节数 | `652` |
| SHA-256 | `71652DDE5C005470D7F9432C4A10AF19C642740F08F644C94B239536B65755F5` |
| 直接 `ProjectReference` | `TableRuntime`、`Contracts`、`Contracts.Wire`、`Client` |

测试中的 fixture 常量与上述 blob 逐字节一致；测试自身断言 `652` 字节和上述 SHA-256，防止后续漂移。

### 1.4 已读强制材料

- 根 `AGENTS.md`
- `doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`
- `doc/v5-重构/V5-P4.1-CONTRACT-ADDENDUM.md`
- `doc/v5-重构/V5-P4.2-SHELL-PREPARATION.md` §3（目标程序集图）
- `doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md`
- caller 本轮：`D:/majiang-wt/codex-progress-20261028-2345/doc/v5-重构/CODEX-V5-PROGRESS-20261028-2345.md`
- 参考：`doc/v5-重构/V5-MODULE-DEPENDENCY-GUARD.md`（既有 I-5/S-1 语义）

---

## 2. 根因

旧规则把 LocalHost 当成“只依赖 TableRuntime 的空壳”：

```text
allowedProjectReferences = { Mahjong.TableRuntime }
allowedMahjongAssemblyReferences = { Mahjong.Ai, Mahjong.Contracts, Mahjong.Domain, Mahjong.TableRuntime }
allowedThirdPartyAssemblyReferences = { }
requiredMahjongAssemblyReferences = { }
```

已接受的 P4.1/P4.2 合同要求 LocalHost 作为组合端口适配器：

- `ITableChannel` 在 `Mahjong.Client`；
- 本地存档/租约/检查点类型来自 `Mahjong.TableRuntime` 与 `Mahjong.Contracts`；
- protobuf wire 信封来自 `Mahjong.Contracts.Wire`，并由 `Google.Protobuf` 提供运行时类型；
- `Domain`/`Ai` 只经 `TableRuntime` 传递，不需要直接项目边。

因此旧规则同时拒绝了：

- 项目层三条已接受直接边（`Client`、`Contracts`、`Contracts.Wire`）；
- 程序集层 `Client`、`Contracts.Wire`、`Google.Protobuf`。

旧负例 `LocalHost -> Client` 也建立在同一过时假设上。真实 XML 负例复制的是 PR21 四边 csproj，于是 `LocalHost` 的三条既有禁止边与注入的 `TableRuntime -> Client` 一起进入集合，`Assert.Single(predicate)` 只挑一条，掩盖了额外错误。

---

## 3. 修正内容

### 3.1 `ModuleRule` 拆分 allowed / required 项目边

`ModuleRule` 新增：

- `RequiredProjectReferences`：必须真实声明的正向项目边；
- `ForbiddenAssemblyReferences`：模块级能力拒绝集。

语义：

- 每个已声明项目边必须落在 `AllowedProjectReferences`；
- 每个 `RequiredProjectReferences` 必须真实声明；
- 其余九个模块未显式传 required 时，`required = allowed`，保留原有精确 allow/deny 语义；
- LocalHost 显式 `required = { TableRuntime }`，`allowed = { TableRuntime, Contracts, Contracts.Wire, Client }`。

这样：

- 已接受 PR20 基线（LocalHost 只有 csproj + TableRuntime）保持绿色；
- PR21 四边 csproj 也保持绿色；
- 少了 `TableRuntime` 仍然报 `PROJECT_MISSING_EDGE`；
- 多了 `Domain`/`Ai`/服务端/任意 Mahjong 仍报 `PROJECT_FORBIDDEN_EDGE`。

### 3.2 LocalHost 项目授权

| 直接项目边 | 状态 | 依据 |
|---|---|---|
| `Mahjong.TableRuntime` | **允许 + 必需** | 既有合法边；本地权威/存档/租约/检查点 |
| `Mahjong.Contracts` | 允许，可选 | 本地存档强类型/状态版本/事件序号 |
| `Mahjong.Contracts.Wire` | 允许，可选 | protobuf wire 信封与编解码归属 |
| `Mahjong.Client` | 允许，可选 | `ITableChannel` 端口 |
| `Mahjong.Domain` | **禁止直接边** | 只允许经 `TableRuntime` 的程序集传递闭包 |
| `Mahjong.Ai` | **禁止直接边** | 同上，无直接使用凭据 |
| `LobbyService` / `TableService` / `Infrastructure` | **禁止** | 服务端/基础设施能力不得进入本地宿主 |
| 其它 `Mahjong.*` | **禁止** | 精确十模块白名单 |

### 3.3 LocalHost 程序集授权

允许的 Mahjong 程序集引用恰为项目闭包：

```text
Mahjong.Ai
Mahjong.Client
Mahjong.Contracts
Mahjong.Contracts.Wire
Mahjong.Domain
Mahjong.TableRuntime
```

第三方程序集仅：

```text
Google.Protobuf
```

必需程序集正向边为空：当前 LocalHost 无源码，空程序集不应被要求发出未使用的 `Client`/`Wire`/`Protobuf` 边；完整 host 集成时由 caller 核实真实图。

### 3.4 LocalHost 网络能力拒绝

`System.Net.*` 仍被共享分类器识别为 BCL，不能伪装成第三方拒绝。为避免本地宿主通过 BCL 名称长出网络能力，LocalHost 规则增加仅本模块生效的 `ForbiddenAssemblyReferences`：

```text
System.Net.Http
System.Net.Mail
System.Net.NameResolution
System.Net.NetworkInformation
System.Net.Quic
System.Net.Requests
System.Net.Security
System.Net.Sockets
System.Net.WebSockets
```

命中时产生新诊断：

```text
ASSEMBLY_FORBIDDEN_CAPABILITY
```

该拒绝集不改变其它模块的 BCL 策略；不是全局“System.Net 一刀切”。

### 3.5 真实 XML 隔离 fixture

新增 `AcceptedLocalHostContractProjectGraphIsGreenInIsolatedRealXmlFixture`：

1. 复制当前 `dotnet/src/*` 十份真实 csproj 到随机 TEMP；
2. 用与 `c600611` 逐字节一致的 LocalHost csproj 覆盖；
3. 断言 fixture 为 4 条 `ProjectReference`、652 bytes、SHA-256 `71652DDE...`；
4. 运行真实 XML 发现/ canonical 路径/精确白名单校验，期望 0 诊断。

新增 `ForbiddenServiceEdgeInjectedIntoAcceptedLocalHostXmlFixtureIsDetectedExactlyOnce`：

1. 在上述四边 fixture 上注入 `LocalHost -> LobbyService`；
2. `Assert.Single(diagnostics)` 精确断言全集只有 1 条；
3. 断言 `PROJECT_FORBIDDEN_EDGE`、`From=LocalHost`、`To=LobbyService`。

### 3.6 负例替换与强化

- 两处旧 `[InlineData("Mahjong.LocalHost", "Mahjong.Client")]` 替换为 `("Mahjong.LocalHost", "Mahjong.LobbyService")`，保留原非法边槽位与服务端语义。
- `LocalHost -> Client` 改为正例，由合成图与真实 XML fixture 两处覆盖。
- 原 `ForbiddenEdgeInjected...`、`ForbiddenProjectEdges...`、`ForbiddenAssembly...`、`UnknownThirdParty...` 的 predicate 式 `Assert.Single` 改为 `Assert.Single(diagnostics)` + 显式 code 断言，避免再次忽略额外诊断。
- 新增 LocalHost 专属负例矩阵（见 §4）。

### 3.7 范围

未修改：

- 任何生产 `csproj`、`Directory.Packages.props`、manifest、solution；
- 任何黄金/verifier/Unity/IL2CPP/Resources/Plugins；
- 主树、其它 receiver 树、caller 树；
- PR21 生产源码。

---

## 4. 最终允许/拒绝矩阵

### 4.1 项目声明图

| 方向 | 结果 | 覆盖 |
|---|---|---|
| `LocalHost -> TableRuntime` | 允许 + 必需 | 基线 + 四边 fixture + `LocalHostStillRequires...` |
| `LocalHost -> Contracts` | 允许，可选 | 四边 fixture + 合成正例 |
| `LocalHost -> Contracts.Wire` | 允许，可选 | 四边 fixture + 合成正例 |
| `LocalHost -> Client` | 允许，可选 | 四边 fixture + 合成正例 |
| `LocalHost -> Domain` | 拒绝 | `LocalHostTransitiveServiceAndReverseProjectEdgesRemainForbidden` |
| `LocalHost -> Ai` | 拒绝 | 同上 |
| `LocalHost -> LobbyService` / `TableService` / `Infrastructure` | 拒绝 | 同上 + 四边 fixture 注入 |
| `Client -> LocalHost` | 拒绝 | 同上 |
| `Domain -> LocalHost` | 拒绝 | 同上 |
| `TableRuntime -> LocalHost` | 拒绝 | 原负例保留 |
| `TableRuntime -> Client` | 拒绝 | 原真实 XML 注入 |
| `Domain -> Client` / `Domain -> Contracts.Wire` | 拒绝 | 新增到原项目负例矩阵 |
| 其它既有模块负例 | 全部保留 | `ForbiddenProjectEdges...` 共 20 例 |

### 4.2 程序集图

| 引用 | 结果 | 覆盖 |
|---|---|---|
| `LocalHost -> Ai/Client/Contracts/Contracts.Wire/Domain/TableRuntime` | 允许 | `AcceptedLocalHostAssemblyClosureIsAuthorized` |
| `LocalHost -> Google.Protobuf` | 允许 | 同上 |
| `LocalHost -> LobbyService/TableService/Infrastructure` | 拒绝 | `LocalHostServiceAndReverseAssemblyEdgesRemainForbidden` |
| `Client/TableRuntime/Domain -> LocalHost` | 拒绝 | 同上 |
| `TableRuntime -> Client`、`Domain -> Contracts.Wire` | 拒绝 | 同上 |
| `LocalHost -> UnityEngine.*` | 拒绝 | `LocalHostThirdPartyNetwork...` |
| `LocalHost -> Google.Cloud.*` / `Newtonsoft.Json` / `MySql.Data` / `Npgsql` / `WebSocketSharp` | 拒绝 | 同上 |
| `LocalHost -> Microsoft.AspNetCore.Http` | 拒绝（未授权共享框架） | 同上 |
| `LocalHost -> System.Net.Http/Sockets/WebSockets` | 拒绝（能力边界） | 同上 |
| `LocalHost -> System.Net.Mail/NameResolution/NetworkInformation/Quic/Requests/Security` | 拒绝（能力边界） | 规则集覆盖；测试抽样 3 项 |
| 其它模块负例 | 全部保留 | 程序集 Mahjong 负例 16 例 + 第三方负例 9 例 |

---

## 5. 独立验证

环境：Windows；SDK `D:/Unity/dotnet/dotnet.exe --version` = `8.0.426`。所有 native exit 均来自实际进程，未用 `$?` 代替。

### 5.1 fresh Release 构建

```powershell
D:/Unity/dotnet/dotnet.exe build "dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj" -c Release --no-incremental --nologo
```

实际：`已成功生成`，`0 个警告`，`0 个错误`，**native exit 0**。日志 `final-build.log`。

### 5.2 直接运行实际 Release 测试 DLL

```powershell
D:/Unity/dotnet/dotnet.exe "dotnet/tests/Mahjong.Architecture.Tests/bin/Release/net8.0/Mahjong.Architecture.Tests.dll" `
  -noLogo -reporter default `
  -result-xml "build/artifacts/localhost-arch-20261028/final-arch.xml"
```

实际：`Total: 130, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`，**native exit 0**。

`final-arch.xml` 聚合读数：`total=130 passed=130 failed=0 errors=0 skipped=0 not-run=0`。

本树基线为 92 项；修正后 130 项，新增 38 项有效用例，0 skip、0 空测试。

### 5.3 `dotnet test` 同工程

```powershell
D:/Unity/dotnet/dotnet.exe test "dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj" -c Release --no-build --nologo
```

实际：`失败: 0，通过: 130，已跳过: 0，总计: 130`，**native exit 0**。

### 5.4 定向矩阵

| 方法 | 用例数 | 结果 | native exit |
|---|---:|---|---:|
| `ProjectDeclarationGraphMatchesTheExactAllowlist` | 1 | 1/1 pass | 0 |
| `AssemblyReferenceGraphIsWithinTheAllowedClosureAndKeepsRequiredPositiveEdges` | 1 | 1/1 pass | 0 |
| `ForbiddenEdgeInjectedIntoARealProjectFileIsDetectedThroughXmlParsing` | 1 | 1/1 pass | 0 |
| `AcceptedLocalHostContractProjectGraphIsGreenInIsolatedRealXmlFixture` | 1 | 1/1 pass | 0 |
| `ForbiddenServiceEdgeInjectedIntoAcceptedLocalHostXmlFixtureIsDetectedExactlyOnce` | 1 | 1/1 pass | 0 |
| `AcceptedLocalHostAssemblyClosureIsAuthorized` | 1 | 1/1 pass | 0 |
| `LocalHostThirdPartyNetworkAndServerAssemblyReferencesAreRejected` | 11 | 11/11 pass | 0 |
| `LocalHostServiceAndReverseAssemblyEdgesRemainForbidden` | 8 | 8/8 pass | 0 |
| `LocalHostTransitiveServiceAndReverseProjectEdgesRemainForbidden` | 7 | 7/7 pass | 0 |

均 `Failed=0, Skipped=0, Errors=0, Not Run=0`。定向 log/XML 在 `build/artifacts/localhost-arch-20261028/target-*.{log,xml}`，全部 SHA 见 `evidence-sha256.csv`。

### 5.5 已接受基线 solution build 与 Client/Contracts 回归

```powershell
D:/Unity/dotnet/dotnet.exe build "dotnet/Mahjong.sln" -c Release --no-incremental --nologo
```

实际：`已成功生成`，`0 个警告`，`0 个错误`，**native exit 0**。

```powershell
D:/Unity/dotnet/dotnet.exe "dotnet/tests/Mahjong.Client.Tests/bin/Release/net8.0/Mahjong.Client.Tests.dll" -noLogo -reporter default -result-xml "...\Mahjong.Client.Tests.xml"
D:/Unity/dotnet/dotnet.exe "dotnet/tests/Mahjong.Contracts.Tests/bin/Release/net8.0/Mahjong.Contracts.Tests.dll" -noLogo -reporter default -result-xml "...\Mahjong.Contracts.Tests.xml"
```

| 套件 | Total | Passed | Failed | Skipped | native exit |
|---|---:|---:|---:|---:|---:|
| `Mahjong.Client.Tests` | 182 | 182 | 0 | 0 | 0 |
| `Mahjong.Contracts.Tests` | 149 | 149 | 0 | 0 | 0 |

未运行 PostgreSQL、WSS、Unity/IL2CPP、Android/iOS/设备、桌面 GL、远端 CI。

### 5.6 证据 SHA-256

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `DependencyDirectionTests.cs` | 116674 | `DDDD581A9F3A19567115FF6ADB1AB51A7F1F18CE3A0ED1FDA2E1403E9512CA4A` |
| `final-build.log` | 3350 | `552FB1B4A7EAF7FBBB9C9A8610DF6B76FAB602398565B0D0F8F567D89ED33FCA` |
| `final-arch.log` | 764 | `539BFDC08665095A5130B297AE2555F142063E05CB410C8D68CDE0565F7701C4` |
| `final-arch.xml` | 84354 | `E6BB26768AF1FF82F42750E010435A8F1E10E5E3DFCFE647EFF13D1B26253B51` |
| `dotnet-test-arch.log` | 816 | `00C53B78AAFEC16AE17300A6AE04F2EEF69A76725216887032D003C084D2D690` |
| `solution-build.log` | 7384 | `E137F74539A4222B0464166D704E21387E0A1779C32ACACA32D10D95822A4B33` |
| `Mahjong.Client.Tests.log` | 704 | `55097C88653274EAA06B3D6EFCB6179CAB2C6BDD4C38ABBCC8924BEA85797909` |
| `Mahjong.Client.Tests.xml` | 105422 | `FA22CE72F725E2919898C3961B86B275A0A940CC02C5BD489CFAB522689AF8BC` |
| `Mahjong.Contracts.Tests.log` | 734 | `6BF176D34632C58557B40EDB6DDD1E4DA0C5AFE202D1296B9EE0C0D7DBFE2B37` |
| `Mahjong.Contracts.Tests.xml` | 84514 | `150CEA15DAAB16BB7C276D963A7D6A75963C19271874348BDD7A290EDF6EC4F4` |
| `pr21-localhost.csproj`（只读提取） | 652 | `71652DDE5C005470D7F9432C4A10AF19C642740F08F644C94B239536B65755F5` |
| `evidence-sha256.csv` | 8868 | `9942CE19E3AABB7F0CFF119BED81C18025F2ABF5206DA9CEC8379CA18818E146` |

`evidence-sha256.csv` 同时列出全部 `target-*.log/.xml` 的 SHA-256。

### 5.7 工作树与提交边界

提交前检查：

```text
git diff --check              -> exit 0
git status --porcelain=v1     -> 仅两条 scope 路径
```

本交付为本地提交，**不 push、不建 PR、不建 MR、不 merge、不部署**。最终提交 SHA 由 `git rev-parse HEAD` 读取；本文件与测试文件属于同一提交。未创建 `complete.flag`。

---

## 6. 真实完成与 NOT_RUN

### 6.1 真实完成（PASS）

| 项 | 证据 |
|---|---|
| LocalHost 项目四边精确授权 | 四边合成正例 + 真实 XML fixture |
| 基线仅 `TableRuntime` 仍绿 | `SyntheticBaselineIsGreenBeforeMutation` + 真实基线图 |
| `TableRuntime` 必需边保留 | `LocalHostStillRequiresTheTableRuntimeProjectEdge` |
| LocalHost 程序集 Client/Wire/Protobuf 闭包 | `AcceptedLocalHostAssemblyClosureIsAuthorized` |
| 空壳不要求未使用程序集边 | `LocalHostEmptyShellIsNotRequiredToEmitUnusedAssemblyEdges` |
| 服务端/反向/任意 Mahjong 拒绝 | 项目 7 例 + 程序集 8 例 |
| Unity/任意 Google/vendor/Socket/共享框架拒绝 | 程序集 11 例 |
| 真实 XML 注入精确单诊断 | `Assert.Single(diagnostics)` |
| fresh Release 构建/全量/定向/solution/Client/Contracts | §5 |
| 范围、clean、diff-check | only two paths；commit 后复核 |

### 6.2 NOT_RUN / 未声明通过

| 项 | 状态 | 原因 |
|---|---|---|
| 真实 PR21 LocalHost 完整集成 | **NOT_RUN** | 本树无完整 host 源码；未合入 PR21 生产代码 |
| PR21 / PR20 远端 CI 重跑 | **NOT_RUN** | 不 push、不触发远端 |
| 完整 solution 全量测试 | **NOT_RUN** | 只跑 Architecture + Client/Contracts；未跑 Domain/Ai/TableRuntime/TableService/Infrastructure 全量 |
| PostgreSQL / PG 统一 wrapper | **NOT_RUN** | 明确不重复昂贵 PG |
| WSS / 真实网络 | **NOT_RUN** | 不在范围 |
| Unity Editor / IL2CPP / Android / iOS / 真机 | **NOT_RUN / BLOCKED** | 不在范围 |
| P4.1d / 冷恢复 / 黄金 / 完整 host 验收 | **NOT_RUN** | 由原 owner 与 caller 负责 |
| G2–G5 | **未批准** | 不以本组件修复升级门槛 |

---

## 7. 缺陷与修复映射

| ID | 级别 | 原缺陷 | 修复 | 验收 |
|---|---|---|---|---|
| `D-LH-CI-ARCH-01` | MAJOR | LocalHost 三条已接受项目边被 `PROJECT_FORBIDDEN_EDGE` 拒绝 | 项目 allowed 四边 + required `TableRuntime`；新增正例与真实 XML fixture | 项目图 0 诊断；注入服务端边精确 1 条 |
| `D-LH-CI-ARCH-02` | MAJOR | LocalHost `Client`/`Contracts.Wire`/`Google.Protobuf` 程序集边被拒 | 程序集闭包六 Mahjong + `Google.Protobuf`；空壳 required 为空 | 合成闭包 0 诊断；反向/第三方/服务端拒绝 |
| `D-LH-CI-ARCH-03` | MAJOR | 真实 XML 负例继承 3 条 LocalHost 既有诊断，`Assert.Single(predicate)` 忽略额外错误 | 基线负例改为 `Assert.Single(diagnostics)`；新增四边 fixture 注入测试 | 精确 1 条 `PROJECT_FORBIDDEN_EDGE` |

本轮未新增其它缺陷类别；未修复且明确保留给 caller 的其它 CI 失败：

- `D-LH-CI-GOLDEN`（visibility/render pin）
- `D-LH-CI-COLD`（furiten cold-read worker）
- companion gate 两项校验
- PR PG 统一 wrapper 失败

这些不属于 `D-LH-CI-ARCH`，本任务不以它们阻塞自身交付，也不宣称它们通过。

---

## 8. 交付范围与后续

1. 本任务只改两条路径：
   - `dotnet/tests/Mahjong.Architecture.Tests/DependencyDirectionTests.cs`
   - `doc/v5-重构/V5-LOCALHOST-ARCHITECTURE-ALLOWLIST-DELIVERY.md`
2. 本地提交后 tracked clean；`git diff --check` exit 0。
3. caller 独立审查本 diff 与证据，再统一到现有 PR20；本任务不修改 PR20/PR21 远端状态。
4. 完整 host 集成时，caller 需在真实生产图上复核：
   - LocalHost 四条直接项目边确实存在；
   - LocalHost 实际程序集闭包不超出六 Mahjong + `Google.Protobuf`；
   - 不存在网络/服务端/基础设施/Unity 能力；
   - required positive edges 与真实实现一致。
5. 本报告不构成 PR21、G2、G3、G4、G5 或远端 CI 的通过声明；V4 仍是唯一生产架构。
