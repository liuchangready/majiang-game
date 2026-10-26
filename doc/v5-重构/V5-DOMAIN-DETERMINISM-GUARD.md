# V5 Domain 确定性 Source-Scan 守护（I-4 / S-2）

- 日期：2026-10-26
- 任务：`V5-1-REVIEW.md` §9.2 S-2（`I-4` 的落地形），非 G1/G5 门条款
- 基线：`d9d17d8299fc348149f581a0741ddd07b1f519f6`（既有唯一 PR19 已评审/发布头）
- 分支：`wt/v5-domain-determinism-guard-20261026`
- 独占 worktree：`D:/majiang-wt/domain-determinism-guard-20261026`
- 生产代码/原测试/csproj/统一 verifier/fixtures：**零修改**
- 新增文件：仅本报告与 `dotnet/tests/Mahjong.Domain.Tests/DomainDeterminismSourceScanTests.cs`
- 第 2 轮返修：修复 caller D-R1-01（插值孔洞内嵌字符串的词法状态），详见 §10
- 本轮证据根：`build/artifacts/domain-determinism-guard/rework-r2/`；第 1 轮证据与 caller 的
  `caller-domain-review-e90f3512a13841fc9c72ab7c9a23b941/` 原样保留，未覆盖

## 1. 目标与判定口径

本任务把已接受的 Domain 确定性 source 事实扫描固化为可执行守护，保护当前
`Mahjong.Domain` 不新增隐式随机、墙上时钟、计时/延迟、`Guid.NewGuid` 等隐式权威输入。
它**不是**新门、**不重设 G1/G5**、不改变任何已接受门判定，也不把 source-scan 升格为
G1 门槛。G1 原文仍是“Domain 不含隐式随机/时钟/浮点权威”的状态要求；本守护只是
I-4/S-2 建议项的可回归证据。

范围严格限定：

- 扫描 `dotnet/src/Mahjong.Domain` 下全部真实生产 `.cs`，当前 44 个文件；
- 排除任意层级名为 `bin` / `obj` 的生成目录；
- 不扫描 `dotnet/tests/**`，测试侧的 `new Random(seed)` / TEMP 装置不会被误当生产；
- 不修改 Domain 实现、原测试、`Mahjong.Domain.Tests.csproj`、统一 verifier 或任何 golden/fixture。

## 2. 实现摘要

新增测试类 `DomainDeterminismSourceScanTests`（含内部真实扫描器
`DomainDeterminismScan`，测试调用该函数而非复写正则）：

1. **真实生产扫描**：`RealDomainProductionSourcesContainNoImplicitAuthorityApis`
   - 从 `AppContext.BaseDirectory` 向上定位仓库根，必须同时存在 `AGENTS.md`、
     `dotnet/Mahjong.sln`、`dotnet/src/Mahjong.Domain`；否则抛
     `InvalidOperationException`，硬失败。
   - 枚举失败/目录缺失/`.cs` 数量为 0 均抛异常，绝不返回 `total=0` 的 PASS。
   - 断言扫描文件数 ≥ 40，并与测试侧独立 `Directory.GetFiles` 计数相等；
     当前实测 `FilesScanned=44`。
2. **禁用组真实失败自检**：27 个内嵌样本覆盖 12 个禁用组，每组调用真实扫描函数，
   断言 `文件:行:列` 与规范符号，任何漏报都会让同一 Fact 失败并列出全部漏报项。
3. **合法对照**：注释、普通/逐字/转义字符串、字符字面量、`RecordedRandomDraw`、
   `RandomDrawFactory`、`MyStopwatch`、`StopwatchFactory`、`TaskDelayed`、
   `ThreadSleeper`、`Guidance`、`DateTimeOffsetValue` 均不得命中；`Guid.Empty`、
   `Task.CompletedTask`、`DateTimeOffset.FromUnixTimeMilliseconds/Seconds` 合法。
4. **生成目录与空枚举负例**：`bin/`、`obj/`、`nested/bin/` 中的违例不参与扫描；
   空目录/不存在目录/空白参数必须硬失败。
5. **仓库根失败负例**：空白、仅空格、不存在的起始目录、以及“向上无仓库标记”均失败。
6. **多违例精确定位**：同一文件两处违例分别报第 5、6 行，诊断含
   `文件:行:列` 与 `symbol=`。
7. **真实生产副本注入灵敏度**：把 44 个真实 Domain 源复制到进程 TEMP，先证明副本
   基线为 0 违例；再向 `DecisionClock.cs` 副本注入
   `System.DateTime.UtcNow`，扫描必须失败并报
   `DecisionClock.cs:<注入行>:<列> symbol=DateTime.UtcNow`；测试目录中的探针不得被扫描；
   原仓库 `DecisionClock.cs` 的 SHA-256 前后不变。
8. **插值孔洞词法回归（D-R1-01）**：18 个样本覆盖插值孔洞内嵌普通/逐字/插值字符串、
   转义引号、字符、注释、嵌套花括号、转义花括号、格式说明与多行形式；孔洞表达式仍不
   扫描，但内层引号不得提前结束外层，且外层之后的真实 API 必须继续命中。另有独立
   Fact 断言外层 `Guid.NewGuid` 的精确行/列/符号。

### 2.1 匹配的 12 个禁用组

| # | 规范符号 | 覆盖形式（自检矩阵） |
|---|---|---|
| 1 | `new Random` | `new Random(`、`new System.Random(`、`new global::System.Random(`、`new` + 换行 + `Random(` |
| 2 | `DateTime.Now` | 普通、`System.`、`global::System.`、`DateTime` + 换行 + `.Now` |
| 3 | `DateTime.UtcNow` | 普通、`System.` |
| 4 | `DateTimeOffset.Now` | 普通、`global::System.` |
| 5 | `DateTimeOffset.UtcNow` | 普通、`System.` |
| 6 | `Guid.NewGuid` | 普通、`System.`、`global::System.` |
| 7 | `Stopwatch` | `Stopwatch.StartNew`、`new System.Diagnostics.Stopwatch`、`global::System.Diagnostics.Stopwatch` |
| 8 | `Environment.TickCount` | `Environment.TickCount`、`System.Environment.TickCount64` |
| 9 | `Thread.Sleep` | `Thread.Sleep`、`System.Threading.Thread.Sleep` |
| 10 | `Task.Delay` | `Task.Delay`、`System.Threading.Tasks.Task.Delay` |
| 11 | `Math.Random` | `Math.Random`、`System.Math.Random` |
| 12 | `Random.Shared` | `Random.Shared`、`System.Random.Shared` |

正则使用标识符左边界 `(?<![\w@])` 与右边界 `\b`，并在限定符/点号两侧允许
`\s*`，因此普通、全限定、`global::`、跨行空白形式均可命中；相邻合法标识符
（如 `RandomDrawFactory`、`MyStopwatch`）不会因前缀/子串被误报。

### 2.2 词法遮蔽

扫描前用等长空格遮蔽：

- `//` 行注释、`/* ... */` 块注释（保留 `\r`/`\n`，行号不漂移）；
- 普通字符串、逐字字符串 `@"..."`、插值字符串 `$"..."` / `$@"..."` / `@$"..."`；
- 字符字面量与转义。

插值字符串按孔洞结构跨越：字面量中的 `{{` / `}}` 按转义处理；孔洞中的嵌套普通/逐字/
插值字符串、字符、`//` 与 `/* ... */` 注释、嵌套花括号会被正确跳过。孔洞表达式内容仍
不进入扫描（已接受边界），但内层引号不会提前结束外层字符串，也不会把外层之后的真实
API 一并吞掉。这是 D-R1-01 的修复点。

遮蔽后仅对代码 token 运行正则。该实现是小型词法守卫，不引入巨型 C# 解析器。

## 3. 显式白名单与理由（非全文件豁免）

白名单是**语义规则**，不是对某个文件整体跳过；同文件若新增隐式 API 仍会被扫描命中。

1. **`PersistedUtcTime` / `DateTimeOffset` 外部注入 UTC tick**
   - 允许：`new PersistedUtcTime(new DateTimeOffset(ticks, TimeSpan.Zero))`、
     `persisted.Value.UtcDateTime.Ticks`、`DateTimeOffset.FromUnixTimeMilliseconds` /
     `FromUnixTimeSeconds`。
   - 理由：`PersistedUtcTime` 是调用方供给的持久化记录输入，构造时强制零偏移；
     Domain 只读取该记录，不读墙上时钟。`DateTimeOffset` 类型本身**不整体禁用**，
     只禁用 `.Now` / `.UtcNow` 两个隐式墙上时钟入口。
2. **`ShuffleConfig` / `ShuffleTier` 的 `double` 强度参数**
   - 允许：`double strength`、`double.IsNaN`、`"R"` 不变文化格式化等现有调参。
   - 理由：这是已接受、可命名、可调的码牌平衡参数，不是隐式系统输入；shuffle 确定性
     由 shuffle-v1 与冻结跨 JVM/.NET LCG 差分覆盖。本守护**不禁止 `double` 关键字**，
     也不为过 lint 改业务。
3. **其他显式输入**：`Guid.Empty`、`Guid.Parse`、`Task.CompletedTask`、`TimeSpan.Zero`
   合法；只有 `Guid.NewGuid` / `Task.Delay` 等隐式入口命中。

## 4. 自检与灵敏度证据

### 4.1 禁用组自检

`EveryForbiddenGroupFailsWithFileLineColumnAndSymbol` 的 27 个样本逐组调用真实
`ScanDomainDirectory`，断言：

- `Violations.Count > 0`；
- `Symbol` 等于该组规范符号；
- `File == "Probe.cs"`；
- `Line >= 1 && Column >= 1`；
- `ToString()` 含 `Probe.cs:<line>:<column>` 与 `symbol=<expected>`。

结果：**27/27 样本均失败命中，0 漏报**（该 Fact PASS）。

### 4.2 合法样本对照

`CommentsStringsAndLegalLookalikesAreNotFlagged` 的合法文件扫描为 **0 违例**；
同一测试随后用真实扫描函数对 `class P { object M() => DateTime.Now; }` 做正控制，
必须得到 1 条 `DateTime.Now`，证明不是“全文件被遮蔽”的假绿。

### 4.3 生产副本注入（行为灵敏度）

`InjectedImplicitApiIntoProductionCopyFailsAndLeavesRepositoryUntouched`：

1. 复制真实 44 个 Domain `.cs` 到进程 TEMP 的独占随机根（前缀
   `mj-domain-determinism-`）；
2. 副本基线扫描 0 违例、44 文件；
3. 在副本 `DecisionClock.cs` 末尾注入：
   `internal static System.DateTime InjectedWallClock() => System.DateTime.UtcNow;`
4. 扫描返回失败，诊断精确为
   `DecisionClock.cs:<注入行>:<列> symbol=DateTime.UtcNow`，`SourceText` 含
   `System.DateTime.UtcNow`；
5. `dotnet/tests/.../IgnoredProbe.cs` 中的 `System.DateTime.Now` 未被纳入；
6. 原仓库 `DecisionClock.cs` SHA-256 前后一致，证明未改实仓源、未更新任何 golden/hash。

TEMP 根在递归删除前解析最终绝对路径，校验其仍位于进程 `TEMP` 根之下、文件名以
独占前缀开头且不等于 TEMP 根本身；不满足则拒绝删除。测试运行使用
`D:/majiang-wt/domain-determinism-guard-20261026/build/artifacts/domain-determinism-guard/tmp`
作为进程级 `TEMP`/`TMP`，不修改机器级环境。

### 4.4 插值孔洞回归（D-R1-01）

`InterpolatedHolesDoNotLeakQuotesOrSwallowOuterRealApis` 的 18 个样本逐例调用真实
`ScanDomainDirectory`，统一矩阵覆盖：

| 类别 | 样本/期望 |
|---|---|
| 普通字面量 + 孔洞变量 | `$"DateTime.Now {value} new Random()"` → 0 |
| 孔洞内嵌普通字符串 | `$"{Echo("DateTime.Now")}"`、`Guid.NewGuid()`、`Task.Delay(1)` → 各 0 |
| 孔洞字符串 + 外层真实 API | `$"{Echo("DateTime.Now")}"; ... Guid.NewGuid();` → 恰 1 条 `Guid.NewGuid` |
| 内层转义引号 + 外层真实 API | `$"{Echo("a\"b")}"; ... Guid.NewGuid();` → 恰 1 条 `Guid.NewGuid`（关键假绿样例） |
| 逐字插值两种前缀 | `$@"{Echo("DateTime.Now")}"`、`@$"{Echo("DateTime.Now")}"` → 各 0 |
| 孔洞内嵌逐字字符串/双引号转义 | `$"{Echo(@"DateTime.Now")}"`、`$"{Echo(@"a""DateTime.Now""")}"` → 各 0 |
| 孔洞内嵌字符/注释/花括号 | `$"{Echo('{') /* } DateTime.Now */}"; ... Guid.NewGuid();` → 恰 1 条外层 Guid |
| 孔洞字符串含 `}` | `$"{Echo("} DateTime.Now")}"; ... DateTime.UtcNow;` → 恰 1 条外层 UtcNow |
| 嵌套插值 + 外层真实 API | `$"{Echo($"{DateTime.Now}")}"; ... Guid.NewGuid();` → 恰 1 条外层 Guid |
| 转义花括号 | `$"{{DateTime.Now}}"` → 0 |
| 孔洞内 lambda 块/字符串 | `$"{new Func<string>(() => { return "DateTime.Now"; })()}"` + 外层 Guid → 恰 1 条外层 Guid |
| 多行孔洞 | 多行 `$"{ ... Echo("DateTime.Now") ... }"` + 外层 Guid → 恰 1 条外层 Guid |
| 格式说明 | `$"{value:DateTime.Now}"` → 0 |
| 孔洞表达式边界（已接受） | `$"{DateTime.Now}"` → 0（孔洞表达式不扫描） |

配套 `InterpolatedHoleDiagnosticsPointAtTheOuterRealApi` 对关键样例断言：
`Guid.NewGuid` 恰 1 条、行号 1、列号 > 1、`Location == Probe.cs:1:<column>`，
诊断含 `symbol=Guid.NewGuid`。修复前 caller 的 33 探针本组 7/7 失败；修复后 33/33 通过
（见 §5.5 与 §10）。

## 5. 执行证据（原样命令 / native exit / 真实计数）

### 5.1 编译先于运行（fresh Release，--no-incremental）

```powershell
$env:TEMP='D:\majiang-wt\domain-determinism-guard-20261026\build\artifacts\domain-determinism-guard\rework-r2\tmp'
$env:TMP=$env:TEMP
& 'D:/Unity/dotnet/dotnet.exe' build `
  'dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj' `
  -c Release --no-incremental --nologo
```

- native exit：**0**
- 结果：`Mahjong.Domain -> ...\Mahjong.Domain.dll`、
  `Mahjong.Domain.Tests -> ...\Mahjong.Domain.Tests.dll`
- **0 warnings / 0 errors**
- 日志：`build/artifacts/domain-determinism-guard/rework-r2/build.log`
  SHA-256 `50EBC3E436BD064A2CD4C8F57ED9F42CFC30313EBED3915B53C9B626D63C43AD`

### 5.2 新类 direct runner（xUnit v3，-class + -result-xml）

```powershell
& 'D:/Unity/dotnet/dotnet.exe' `
  'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' `
  -class Mahjong.Domain.Tests.DomainDeterminismSourceScanTests `
  -result-xml 'build/artifacts/domain-determinism-guard/rework-r2/source-scan-tests.xml'
```

- native exit：**0**
- 控制台：`Total: 9, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`
- XML：`total=9, passed=9, failed=0, skipped=0, errors=0`
- 用例（9）：`RealDomainProductionSourcesContainNoImplicitAuthorityApis`、
  `EveryForbiddenGroupFailsWithFileLineColumnAndSymbol`、
  `CommentsStringsAndLegalLookalikesAreNotFlagged`、
  `InterpolatedHolesDoNotLeakQuotesOrSwallowOuterRealApis`、
  `InterpolatedHoleDiagnosticsPointAtTheOuterRealApi`、
  `GeneratedFoldersAreExcludedAndVacuousScansFailClosed`、
  `RepositoryRootAndEnumerationFailuresAreTerminal`、
  `DiagnosticsCarryExactSourceLinesForMultipleViolations`、
  `InjectedImplicitApiIntoProductionCopyFailsAndLeavesRepositoryUntouched`
- XML SHA-256 `C1DE5FFA8DC7C47F53DF560EDFC3B016AF826154E6613F0FE3EE2F1B11D5801E`
- 日志 SHA-256 `FD76DFBEDDA1905161D10900D59EE92BDAC468704674EF9873A3915E8B477133`

### 5.3 原有 Domain 风险补充（显式时间输入）

```powershell
& 'D:/Unity/dotnet/dotnet.exe' `
  'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' `
  -class Mahjong.Domain.Tests.DecisionClockTests `
  -result-xml 'build/artifacts/domain-determinism-guard/rework-r2/decision-clock-tests.xml'
```

- native exit：**0**
- XML：`total=14, passed=14, failed=0, skipped=0, errors=0`
- 选择理由：`DecisionClock` 是最直接消费显式毫秒输入的域类型；验证 source-scan
  守护未伴随任何行为回归。未重跑 Domain 全量。
- XML SHA-256 `E2177F2F8DC9CA540F6EAC0746BC10305615F3418C013D547A138D164EAA97EB`
- 日志 SHA-256 `D7250991C945970AE069CFBB527F0E48030E02C8A2C70C2455F852C317CF0F66`

### 5.4 两类合并选中（新类 + DecisionClockTests）

```powershell
& 'D:/Unity/dotnet/dotnet.exe' `
  'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' `
  -class Mahjong.Domain.Tests.DomainDeterminismSourceScanTests `
  -class Mahjong.Domain.Tests.DecisionClockTests `
  -result-xml 'build/artifacts/domain-determinism-guard/rework-r2/targeted.xml'
```

- native exit：**0**
- XML：`total=23, passed=23, failed=0, skipped=0, errors=0, not-run=0`
- XML SHA-256 `9E2D8938B5D887C315E6738E8EF37351DC9F63C8409167890750C831598FC6BA`
- 日志 SHA-256 `E9CB487C4F97077589AFDF0B0494BD1DB52B4E9C315F3E6C71EA1EA2E49ABFF8`

### 5.5 Caller 33 探针（修复前/后）

```powershell
$R='D:/majiang-wt/domain-determinism-guard-20261026/build/artifacts/domain-determinism-guard/rework-r2'
& 'D:/Unity/dotnet/dotnet.exe' "$R/probe/bin/Release/net8.0/Probe.dll" `
  'D:/majiang-wt/domain-determinism-guard-20261026/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' `
  $R
```

- 修复前（同一探针/旧 DLL）：native **1**，`TOTAL=33 FAILED=7`；7 例均为插值孔洞组，
  其中样例 4 `$"{Echo("a\"b")}"; ... Guid.NewGuid();` 实际 0（假绿）。
  日志 SHA-256 `69CE3ADCF17047293B0887F936AA17D1EF8FE1D12B4A85F5AEA502FEC795CF00`
  （与 caller 原 `probe.log` 一致）。
- 修复后（新 DLL）：native **0**，`TOTAL=33 FAILED=0`；样例 4 实际恰 1 条
  `Guid.NewGuid`。
  日志 SHA-256 `846B542AE0DE9854FEA1A9D6B2CAF2B0858F5BCEB652B40E61EBD4E0B2BAB6E7`。
- 探针与样本复制到本轮自有 `rework-r2/probe`、`rework-r2/samples`；caller 原
  `$E/probe.log`、`$E/targeted.xml` 等历史产物未覆盖。

### 5.6 状态判定

| 项 | 状态 | 依据 |
|---|---|---|
| 新增 source-scan 守护实现与插值回归 | **PASS** | 9/9，native 0 |
| 真实 Domain 生产源 0 违例 | **PASS** | 44 文件，0 违例 |
| 12 禁用组/27 写法灵敏度 | **PASS** | 全部失败命中 |
| 插值孔洞 18 样本 + 精确外置诊断 | **PASS** | 矩阵/单例全绿 |
| Caller 33 探针 | **PASS** | 33/33，native 0；样例 4 恰 1 |
| 生产副本注入灵敏度 | **PASS** | 精确文件/行/列/符号；原 SHA 不变 |
| fresh Release 构建 | **PASS** | 0 warnings / 0 errors |
| 新类 + DecisionClockTests 合并选中 | **PASS** | 23/23，native 0 |
| D-R1-01 | **FIXED / PASS** | 见 §10；修复前 7/7 失败，修复后 33/33 |
| Domain 全量测试 | **NOT_RUN** | 本任务仅改测试新增文件，不重跑全量 |
| PG / Unity / Gradle / WSS / CI / 部署 | **NOT_RUN** | 明确不在本任务范围；未触发 |
| G1/G5 门槛变更、P4/P5 集成 | **NOT_RUN** | 本任务不实施、不等待、不宣称 |
| 依赖/包缺失 | **BLOCKED：无** | net8.0 + xunit.v3 4.0.2 现有依赖即可 |
| 其他已知失败 | **FAIL：无** | 本轮无遗留 BLOCKER/MAJOR/MINOR |

## 6. 能力边界（如实声明，不宣称过度覆盖）

本守护是**源码词法事实扫描**，不能替代以下证据：

1. 不构建完整 C# AST/语义模型；不展开 `using` 别名（如
   `using DT = System.DateTime; DT.Now`）、不解析 `nameof` / 反射 / `dynamic` /
   表达式树 / 委托间接调用。
2. 不分析 IL、JIT、运行时反射或跨模块数据流；不能证明“任何路径都不产生隐式输入”。
3. 插值字符串按孔洞结构整体遮蔽，**孔洞表达式内容仍不扫描**（例如
   `$"{DateTime.Now}"` 的孔洞不会命中）；但孔洞内嵌普通/逐字/插值字符串、字符、注释与
   花括号会被正确跨越，不会误报内层字面量或吞掉外层之后的真实 API（D-R1-01）。
4. `#if` 预处理分支按当前源码文本保守扫描；不模拟编译条件。
5. 浮点只覆盖已接受的 `ShuffleConfig` 调参合法例，**不宣称覆盖全部浮点权威数据流**；
   不禁止所有 `double` 关键字。
6. 仅覆盖上方 12 个已接受 symbol 组，不声称覆盖 `SecureRandom`、
   `RandomNumberGenerator`、`DateTime.Today` 等未列入组的 API；新增组需另立接受项。
7. 不是 G1/G5 门槛证明。G1 整体仍未最终批准、G5 未完成；本任务不改变该状态。

## 7. 未覆盖 / 回滚边界

- 未运行：PG、Unity/IL2CPP、Gradle、WSS、CI、部署、Android 真机。
- 未触碰：Domain 生产实现、原测试、任何 `csproj`、`tools/verify-v5.ps1`、
  `tools/ci/**`、golden/fixture、`CLAUDE.md`、`doc/ui`、
  `round-preparation/PROMPTS.md`、Unity 环境/Resources/Burst/Plugins/ProjectSettings。
- 回滚：仅删除本报告与 `DomainDeterminismSourceScanTests.cs` 即可；无生产代码、
  无 schema、无 golden、无构建脚本副作用。测试 TEMP 副本由测试自身在校验后递归清理。
- 未 push、未建组件 PR、未合 v5、未触发 CI、未部署、未创建 `complete.flag`。

## 8. 验收断言对照（S-2）

| S-2 要求 | 本交付 |
|---|---|
| 新增 `Mahjong.Domain.Tests/DomainDeterminismSourceScanTests.cs`，不改现有测试 | 仅新增该文件；原测试零修改 |
| 禁用符号组 = §3.4 regex | 12 组全部覆盖，含 §3.4 的 `new Random`、四个时钟、`Guid.NewGuid`、`Stopwatch`、`Environment.TickCount(64)`、`Thread.Sleep`、`Task.Delay`、`Math.Random`，并纳入同类 `Random.Shared` |
| 豁免表显式列 `PersistedUtcTime` 消费路径与 `ShuffleConfig` 数值调参并附理由 | 见 §3；不允许全文件豁免 |
| 内嵌违例自检样本 | 27 样本/12 组 + 18 样本插值孔洞矩阵，调用真实扫描函数；另有生产副本注入 |
| 插值孔洞内嵌字符串 | 内层引号/注释/花括号不误报，外层真实 API 不漏报；caller 33 探针 33/33 |
| 枚举/RepoRoot 失败硬失败 | 见 §2/§4；空枚举、缺目录、无仓库根均抛异常 |
| 不扫描测试目录/生成目录 | 仅 Domain 生产 `.cs`；`bin`/`obj` 排除；测试目录探针不纳入 |
| 不升格 G1/G5 门槛 | 见 §1/§6；本任务不设门、不改门 |

## 9. 结论

S-2 已按接受范围落地：Domain 当前 44 个生产 `.cs` 扫描 0 违例；12 个禁用组/27 种写法
全部能被真实扫描函数检出；插值孔洞 18 样本不再泄漏内存引号或吞掉外层 API；
注释、字符串与合法标识不误报；真实生产副本注入 `System.DateTime.UtcNow` 能返回
精确文件/行/列/符号诊断，且实仓源哈希不变。
新增测试类 9/9、原有 `DecisionClockTests` 14/14、两类合并 23/23，fresh Release 构建
0/0 通过；caller 独立 33 探针修复前 `FAILED=7`、修复后 `FAILED=0`（native 0）。

本交付仅新增两个文件，不构成 G1/G5 通过、不改变 V4 唯一生产事实，也不实施任何受
G1 阻断的 P4/P5 集成。

## 10. 第 2 轮返修：D-R1-01（MAJOR，已修复）

### 10.1 缺陷记录

| 字段 | 内容 |
|---|---|
| ID | **D-R1-01** |
| 严重级别 | **MAJOR**（BLOCKER/MINOR=0） |
| 位置 | `dotnet/tests/Mahjong.Domain.Tests/DomainDeterminismSourceScanTests.cs` 的 `MaskCommentsAndLiterals` / `MaskRegularString` / `MaskVerbatimString`；报告旧 §2.2/§6 |
| 复现 | caller 33 探针（反射调用真实 `ScanDomainDirectory`）：
`& 'D:/Unity/dotnet/dotnet.exe' "$E/probe/bin/Release/net8.0/Probe.dll" 'D:/majiang-wt/domain-determinism-guard-20261026/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' $E`
其中 `$E=D:/majiang-wt/domain-determinism-guard-20261026/build/artifacts/caller-domain-review-e90f3512a13841fc9c72ab7c9a23b941` |
| 实际（修复前） | 插值孔洞内嵌字符串的引号被当成外层结束引号：内层 `"DateTime.Now"` / `"Guid.NewGuid()"` / `"Task.Delay(1)"` 各误报 1 条；`$"{Echo("a\"b")}"` 后的外层 `Guid.NewGuid` 被吞掉（0，假绿）；`$@"..."` / `@$"..."` 各误报 1 条。33 探针 `TOTAL=33 FAILED=7`，native 1。 |
| 期望 | 内层字面量 0 误报；外层真实 `Guid.NewGuid` 恰 1 条；逐字插值 0；33 探针 native 0。 |
| 证据 | 修复前：`build/artifacts/domain-determinism-guard/rework-r2/probe-before-fix.log` SHA-256 `69CE3ADCF17047293B0887F936AA17D1EF8FE1D12B4A85F5AEA502FEC795CF00`（与 caller 原 `probe.log` 一致）；修复后 `probe-after-fix.log` SHA-256 `846B542AE0DE9854FEA1A9D6B2CAF2B0858F5BCEB652B40E61EBD4E0B2BAB6E7`。caller 原 `$E` 目录未覆盖。 |
| 修复要求 | 仅两个 expectedFiles 内，小型词法逻辑正确跨越插值孔洞中的普通/逐字/嵌套插值字符串、转义、字符、花括号与注释；孔洞表达式仍不扫描，但不得误报内层或漏掉孔洞外代码。 |
| 验收断言 | fresh Release `--no-incremental` native 0/0 warning/0 error；新类 + `DecisionClockTests` 选中 total>0、failed/errors/skipped/not-run=0；caller 33 探针 native 0 且样例 4 恰 1；真实 44 源 0 违例；副本注入与 fail-closed 枚举保持；原仓源不改。 |

### 10.2 统一修复映射

| 修复点 | 实现 |
|---|---|
| 顶层识别 `$"` / `$@"` / `@$"` | 统一进入 `MaskInterpolatedString`，按前缀确定 verbatim 规则 |
| 孔洞边界 | `MaskInterpolationHole` 从单个 `{` 起按花括号深度跨到匹配 `}`；`{{`/`}}` 字面量转义单独跳过 |
| 内层普通字符串 | `MaskRegularString` 处理 `\` 转义，内层 `\"` 不再结束外层 |
| 内层逐字字符串 | `MaskVerbatimString` 处理 `""` 转义；`$@"..."` / `@$"..."` 用 verbatim 规则 |
| 内层嵌套插值 | `TryMaskNestedString` 递归进入 `MaskInterpolatedString`，不会把内层 `{`/`}` 误当外层 |
| 字符/注释/花括号 | 孔洞内 `'...'`、`//`、`/* ... */`、嵌套 `{}` 均被跨越；孔洞整体随后遮蔽 |
| 外层恢复 | 孔洞结束后回到外层字面量循环，外层关闭引号之后代码继续被正则扫描 |
| 自检固化 | 新增 18 样本矩阵 + 独立外层精确诊断 Fact（共 9 个 Fact） |

### 10.3 统一负例矩阵与验证

- 18 个插值/孔洞样本：见 §4.4；修复后全部符合期望（0 误报 / 外层恰 1）。
- caller 33 探针：修复前 `FAILED=7`，修复后 `TOTAL=33 FAILED=0`，native 0；
  关键样例 `$"{Echo("a\"b")}"; object X() => Guid.NewGuid();` 修复后恰 1 条
  `Guid.NewGuid`。
- 真实 Domain 44 源：0 违例；生产副本注入、空/缺目录硬失败、生成目录排除均保持 PASS。
- 新增测试类：9/9；`DecisionClockTests`：14/14；合并选中：23/23；
  所有 XML `failed=0, errors=0, skipped=0, not-run=0`。
- 修复前后均只改 `DomainDeterminismSourceScanTests.cs` 与本报告；未改生产/原测试/
  csproj/verifier/fixtures/golden，未改 caller 历史产物。

### 10.4 新增信息说明

本轮没有发现 D-R1-01 之外的新缺陷。该缺陷在上一轮无法由既有 27 样本矩阵发现，因为
矩阵只覆盖“整段字符串中的禁词”和“无嵌套插值”，没有覆盖“孔洞内部再出现字符串”的
词法状态；caller 的 33 探针首次提供了该反例，故本轮一次性补齐同组全部 7 例及扩展
嵌套/逐字/注释/花括号形式，没有把同类问题留到下一轮。
