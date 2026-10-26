# V5 Domain 确定性 Source-Scan 守护（I-4 / S-2）

- 日期：2026-10-26
- 任务：`V5-1-REVIEW.md` §9.2 S-2（`I-4` 的落地形），非 G1/G5 门条款
- 基线：`d9d17d8299fc348149f581a0741ddd07b1f519f6`（既有唯一 PR19 已评审/发布头）
- 分支：`wt/v5-domain-determinism-guard-20261026`
- 独占 worktree：`D:/majiang-wt/domain-determinism-guard-20261026`
- 生产代码/原测试/csproj/统一 verifier/fixtures：**零修改**
- 新增文件：仅本报告与 `dotnet/tests/Mahjong.Domain.Tests/DomainDeterminismSourceScanTests.cs`
- 第 2 轮返修：修复 caller D-R1-01（插值孔洞内嵌字符串的词法状态），详见 §10
- 第 3 轮返修：修复 caller D-R2-01（格式说明中的引号/字符被误当 C# 字面量），详见 §11
- 第 4 轮返修：修复 caller D-R3-01（nullable `?` 残留 conditionDepth 吞掉格式冒号），详见 §12
- 本轮证据根：`build/artifacts/domain-determinism-guard/rework-r4/`；第 1/2/3 轮证据与 caller 的
  `caller-domain-review-e90f3512a13841fc9c72ab7c9a23b941/`、
  `caller-domain-r2-987b59932deb443aa5f8db83efe19ab6/`、
  `caller-domain-r3-bf37b7f5688a4a40be58760bfb0c5fd3/` 原样保留，未覆盖

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
8. **插值孔洞/格式/可空类型词法回归（D-R1-01 / D-R2-01 / D-R3-01）**：34 个样本覆盖
   插值孔洞内嵌普通/逐字/插值字符串、转义引号、字符、注释、嵌套花括号、转义花括号、
   多行形式，以及 6 类格式说明、对应无外部 API 负控制、`?:` / `global::` / 命名参数 /
   nullable cast 对照。另有 8 个 caller nullable 表达式 + 1 个嵌套 block 作用域控制
   （`int? x` 声明）× 正/负控制，验证 `?` 不残留到格式冒号。孔洞表达式仍不扫描，但内层引号、
   格式文本、nullable `?` 不得提前结束外层，且外层之后的真实 API 必须继续命中。
   独立 Fact 分别断言 `Syntax.cs:4:23` 与 `Syntax.cs:6:23` 精确坐标。

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

### 4.4 插值孔洞与格式说明回归（D-R1-01 / D-R2-01）

`InterpolatedHolesDoNotLeakQuotesOrSwallowOuterRealApis` 的 34 个样本逐例调用真实
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
| 孔洞表达式边界（已接受） | `$"{DateTime.Now}"` → 0（孔洞表达式不扫描） |
| **格式：转义双引号** | `$"{1:00\"DateTime.Now}"` + 外层 Guid → 恰 1；无外层 API 版本 → 0 |
| **格式：成对转义双引号** | `$"{DateTime.MinValue:yyyy-MM-dd\"T\"HH:mm:ss}"` + 外层 Guid → 恰 1；无外层 API → 0 |
| **格式：转义单引号** | `$"{1:0\\'}"` + 外层 Guid → 恰 1；无外层 API → 0 |
| **格式：逐字成对双引号** | `$@"{DateTime.MinValue:yyyy-MM-dd""T""HH:mm:ss}"` + 外层 Guid → 恰 1；无外层 API → 0 |
| **格式：单引号文本** | `$"{DateTime.MinValue:yyyy-MM-dd'T'HH:mm:ss}"` + 外层 Guid → 恰 1；无外层 API → 0 |
| **格式：简单格式** | `$"{1:0}"` + 外层 Guid → 恰 1；无外层 API → 0 |
| **格式对照：三元 `?:`** | `(flag ? "DateTime.Now" : "Guid.NewGuid()")` + 外层 Guid → 恰 1 |
| **格式对照：命名参数 `:`** | `Echo(name: "DateTime.Now")` + 外层 Guid → 恰 1 |
| **格式对照：`global::`** | `global::System.DateTime.MinValue:yyyy` + 外层 Guid → 恰 1 |
| **格式对照：nullable cast `?`** | `(int?)value:format` + 外层 Guid → 恰 1 |
| **nullable 状态组** | 8 个 caller 表达式（`F<int?>()`、`default(int? /* comment */)`、`new List<int?>()`、`(value as int?)`、`((int?)value)`、`F<int?>() ?? 0`、`F<List<int?>>()`、`F<int? /* comment */>()`）+ 1 个嵌套 block 控制（`int? x` 声明），各加 `X()=>0` 负控制；正例各恰 1，负控制 0 |

配套 `InterpolatedHoleDiagnosticsPointAtTheOuterRealApi` 对关键样例断言外层
`Guid.NewGuid` 恰 1 条、行号 1、列号 > 1；`FormatSpecifierDiagnosticsMatchCallerCoordinates`
对 caller 形状断言 `Syntax.cs:4:23 symbol=Guid.NewGuid`，同形状无外部 API 文本为 0。
`NullableTypeQuestionsDoNotLeakIntoFormatColonState` 对 8 个 caller nullable 表达式与
1 个嵌套 block 控制逐个断言 `Guid.NewGuid@Syntax.cs:6:23`，并断言其无 API 版本为 0。
修复前 caller 33 探针本组 7/7 失败；修复后 33/33 通过。D-R2-01 的 6 个格式样例修复前
3/6 假绿，修复后 6/6 通过。D-R3-01 的 8 个 nullable 样例修复前 6/8 假绿，修复后 8/8
通过（见 §5.5/§5.6/§5.7 与 §11/§12）。

## 5. 执行证据（原样命令 / native exit / 真实计数）

### 5.1 编译先于运行（fresh Release，--no-incremental）

```powershell
$env:TEMP='D:\majiang-wt\domain-determinism-guard-20261026\build\artifacts\domain-determinism-guard\rework-r4\tmp'
$env:TMP=$env:TEMP
& 'D:/Unity/dotnet/dotnet.exe' build `
  'dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj' `
  -c Release --no-incremental --nologo
```

- native exit：**0**
- 结果：`Mahjong.Domain -> ...\Mahjong.Domain.dll`、
  `Mahjong.Domain.Tests -> ...\Mahjong.Domain.Tests.dll`
- **0 warnings / 0 errors**
- 日志：`build/artifacts/domain-determinism-guard/rework-r4/build.log`
  SHA-256 `E68AF2EA48C110BC46BC28917405051344FD6B338B8A3B28AEC26BF6E84916DE`

### 5.2 新类 direct runner（xUnit v3，-class + -result-xml）

```powershell
& 'D:/Unity/dotnet/dotnet.exe' `
  'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' `
  -class Mahjong.Domain.Tests.DomainDeterminismSourceScanTests `
  -result-xml 'build/artifacts/domain-determinism-guard/rework-r4/source-scan-tests.xml'
```

- native exit：**0**
- 控制台：`Total: 11, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`
- XML：`total=11, passed=11, failed=0, skipped=0, errors=0`
- 用例（11）：`RealDomainProductionSourcesContainNoImplicitAuthorityApis`、
  `EveryForbiddenGroupFailsWithFileLineColumnAndSymbol`、
  `CommentsStringsAndLegalLookalikesAreNotFlagged`、
  `InterpolatedHolesDoNotLeakQuotesOrSwallowOuterRealApis`、
  `InterpolatedHoleDiagnosticsPointAtTheOuterRealApi`、
  `FormatSpecifierDiagnosticsMatchCallerCoordinates`、
  `GeneratedFoldersAreExcludedAndVacuousScansFailClosed`、
  `RepositoryRootAndEnumerationFailuresAreTerminal`、
  `DiagnosticsCarryExactSourceLinesForMultipleViolations`、
  `NullableTypeQuestionsDoNotLeakIntoFormatColonState`、
  `InjectedImplicitApiIntoProductionCopyFailsAndLeavesRepositoryUntouched`
- XML SHA-256 `1DD60D5F82E288C3EB92EA431D85F4B72AAB651B976A21A3D7BF8D651374405B`
- 日志 SHA-256 `01EB9C0C1B75547268805E6AB54E223556AA2C5B55E1E222A52B35011BFEF52F`

### 5.3 原有 Domain 风险补充（显式时间输入）

```powershell
& 'D:/Unity/dotnet/dotnet.exe' `
  'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' `
  -class Mahjong.Domain.Tests.DecisionClockTests `
  -result-xml 'build/artifacts/domain-determinism-guard/rework-r4/decision-clock-tests.xml'
```

- native exit：**0**
- XML：`total=14, passed=14, failed=0, skipped=0, errors=0`
- 选择理由：`DecisionClock` 是最直接消费显式毫秒输入的域类型；验证 source-scan
  守护未伴随任何行为回归。未重跑 Domain 全量。
- XML SHA-256 `100450CB1743B2F334B342686258D326866F81E06C06C89D96B6B636F582B258`
- 日志 SHA-256 `FA82AF590602DCDCED4C69324D823E8250A609B1849A0C855DE0E8C062562307`

### 5.4 两类合并选中（新类 + DecisionClockTests）

```powershell
& 'D:/Unity/dotnet/dotnet.exe' `
  'dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' `
  -class Mahjong.Domain.Tests.DomainDeterminismSourceScanTests `
  -class Mahjong.Domain.Tests.DecisionClockTests `
  -result-xml 'build/artifacts/domain-determinism-guard/rework-r4/targeted.xml'
```

- native exit：**0**
- XML：`total=25, passed=25, failed=0, skipped=0, errors=0, not-run=0`
- XML SHA-256 `8383DB76692D850C4C845E1EDE554428FB0E12B852B24C0283569E214CDAEF46`
- 日志 SHA-256 `6962825EAFBD42B0BAD8314DBE02FD5B7A251B258BBE3A90E18C95CD653BB39D`

### 5.5 Caller 33 探针（回归）

```powershell
$R='D:/majiang-wt/domain-determinism-guard-20261026/build/artifacts/domain-determinism-guard/rework-r4'
& 'D:/Unity/dotnet/dotnet.exe' "$R/probe/bin/Release/net8.0/Probe.dll" `
  'D:/majiang-wt/domain-determinism-guard-20261026/dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll' `
  $R
```

- native exit：**0**
- `TOTAL=33 FAILED=0`；插值孔洞组全部通过，外层 `Guid.NewGuid` 保留。
- 日志：`rework-r4/probe-33.log`，SHA-256
  `846B542AE0DE9854FEA1A9D6B2CAF2B0858F5BCEB652B40E61EBD4E0B2BAB6E7`
  （内容与第 2 轮修复后一致）。
- 第 1 轮修复前历史对照仍为 `FAILED=7`，日志 SHA-256
  `69CE3ADCF17047293B0887F936AA17D1EF8FE1D12B4A85F5AEA502FEC795CF00`；caller 原
  `$E` 历史产物未覆盖。

### 5.6 Caller 格式说明 6 例（D-R2-01）

```powershell
$R='D:/majiang-wt/domain-determinism-guard-20261026/build/artifacts/domain-determinism-guard/rework-r4'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$R/run-format-cases.ps1" -EvidenceRoot $R
```

- probe build native **0**，0 warnings/0 errors；syntax build native **0**，
  0 warnings/0 errors（C#9 / TreatWarningsAsErrors）。
- 六例逐个 native **0**，`TOTAL=6 FAILED=0`；每例恰 1 条：
  `Syntax.cs:4:23 symbol=Guid.NewGuid`。
- 日志：`rework-r4/format-cases.log`，SHA-256
  `823175892787EDEDB8374FA4E5C91AF25143B86BCCD4D3216B340B8A07063B9F`
- 脚本（与 caller 同源复制）：`rework-r4/run-format-cases.ps1`，SHA-256
  `CB21EE9AE6A16949EAA00101316F4CD897159B9C4DBB358100BA2BD90A3558A8`
- caller 原始失败日志 `$E/format-cases.log`（SHA-256
  `D065DF0D49025341035348DD1137E545B8358D42B7940CC139B386A624B55973`）原样保留；
  `$E/format-matrix`、`$E/format-probe` 也未被覆盖。

### 5.7 Caller nullable 8 例（D-R3-01）

```powershell
$R='D:/majiang-wt/domain-determinism-guard-20261026/build/artifacts/domain-determinism-guard/rework-r4'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$R/run-nullable-cases.ps1" -EvidenceRoot $R
```

- probe build native **0**，0 warnings/0 errors；syntax build native **0**，0 warnings/0 errors（C#9 / TreatWarningsAsErrors）。
- 八例逐个 native **0**，`TOTAL=8 FAILED=0`；每例恰 1 条：`Syntax.cs:6:23 symbol=Guid.NewGuid`。
- 日志：`rework-r4/nullable-cases.log`，SHA-256 `56212CA0F1BE30068DA24696F93669B07D4A93BE3825D5A9BDE03172C635DA62`
- 脚本（与 caller 同源复制）：`rework-r4/run-nullable-cases.ps1`，SHA-256 `49E7BEFE3D1829871EF51CD55BB1D47B7EFB5DB20F1ECEF743C6E2E0D9969024`
- caller 原 `$E/nullable-cases.log`、`nullable-matrix` 未覆盖。

### 5.8 状态判定

| 项 | 状态 | 依据 |
|---|---|---|
| 新增 source-scan 守护实现与插值/格式/nullable 回归 | **PASS** | 11/11，native 0 |
| 真实 Domain 生产源 0 违例 | **PASS** | 44 文件，0 违例 |
| 12 禁用组/27 写法灵敏度 | **PASS** | 全部失败命中 |
| 插值/格式统一矩阵 | **PASS** | 34 样本 + 精确外置诊断全绿 |
| Caller nullable 8 例 | **PASS** | 8/8，每例 `Syntax.cs:6:23` |
| Caller 33 探针 | **PASS** | 33/33，native 0 |
| Caller 格式说明 6 例 | **PASS** | 6/6，每例 `Syntax.cs:4:23` |
| 生产副本注入灵敏度 | **PASS** | 精确文件/行/列/符号；原 SHA 不变 |
| fresh Release 构建 | **PASS** | 0 warnings / 0 errors |
| 新类 + DecisionClockTests 合并选中 | **PASS** | 25/25，native 0 |
| D-R1-01 | **FIXED / PASS** | 见 §10；33/33 |
| D-R2-01 | **FIXED / PASS** | 见 §11；格式 6/6 |
| D-R3-01 | **FIXED / PASS** | 见 §12；nullable 8/8 |
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
   `$"{DateTime.Now}"` 的孔洞不会命中）；孔洞内嵌普通/逐字/插值字符串、字符、注释与
   花括号会被正确跨越（D-R1-01）。顶层 `:` 后的格式说明按格式文本处理：其中 `"`、`'`、
   `\`、成对 `""` 与转义 `{{`/`}}` 不再被当作 C# 字面量起点（D-R2-01）；但 `?:`、
   `global::`、命名参数、nullable cast 的 `?` 需依赖小型嵌套/条件启发式，不是完整语法树。
   nullable `?` 通过“下一个有效字符（跳过空白与注释）为 `>`/`)`/`]`/`,`/`;`/`}`/`:`/`=`”
   识别为类型标记，不进入三元计数（D-R3-01）；这不是完整泛型/类型推断，只是防止状态
   泄漏到格式冒号的词法启发式。不扫描格式文本中的 API，也不声称完整 C# 语义。
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
| 内嵌违例自检样本 | 27 样本/12 组 + 34 样本插值/格式矩阵 + 8 nullable 正例/8 负控制，调用真实扫描函数；另有生产副本注入 |
| 插值孔洞内嵌字符串 | 内层引号/注释/花括号不误报，外层真实 API 不漏报；caller 33 探针 33/33 |
| 格式说明引号/字符 | 6 类格式正例 + 6 负控制全绿；每例外层 `Guid.NewGuid@Syntax.cs:4:23` |
| nullable `?` 状态 | 8 例（泛型/注释/嵌套/`??`）各恰 1 条 `Guid.NewGuid@Syntax.cs:6:23`；无 API 控制 0 |
| 枚举/RepoRoot 失败硬失败 | 见 §2/§4；空枚举、缺目录、无仓库根均抛异常 |
| 不扫描测试目录/生成目录 | 仅 Domain 生产 `.cs`；`bin`/`obj` 排除；测试目录探针不纳入 |
| 不升格 G1/G5 门槛 | 见 §1/§6；本任务不设门、不改门 |

## 9. 结论

S-2 已按接受范围落地：Domain 当前 44 个生产 `.cs` 扫描 0 违例；12 个禁用组/27 种写法
全部能被真实扫描函数检出；34 样本插值/格式矩阵与 8 caller nullable + 1 嵌套 block
正例/9 负控制不再泄漏内层引号、吞掉外层 API、把格式文本误当 C# 字面量或把 nullable `?`
残留为三元状态；
注释、字符串与合法标识不误报；真实生产副本注入 `System.DateTime.UtcNow` 能返回精确
文件/行/列/符号诊断，且实仓源哈希不变。
新增测试类 11/11、原有 `DecisionClockTests` 14/14、两类合并 25/25，fresh Release 构建
0/0 通过；caller 独立 33 探针 `FAILED=0`，6 例格式矩阵 `FAILED=0`（`Syntax.cs:4:23`），
8 例 nullable 矩阵 `FAILED=0`（`Syntax.cs:6:23`），native 均 0。

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
| 自检固化 | 第 2 轮当时新增 18 样本矩阵 + 独立外层精确诊断 Fact（当时共 9 个 Fact） |

### 10.3 统一负例矩阵与验证

- 第 2 轮当时 18 个插值/孔洞样本：见 §4.4；当时全部符合期望（0 误报 / 外层恰 1）。
- caller 33 探针：修复前 `FAILED=7`，修复后 `TOTAL=33 FAILED=0`，native 0；
  关键样例 `$"{Echo("a\"b")}"; object X() => Guid.NewGuid();` 修复后恰 1 条
  `Guid.NewGuid`。
- 真实 Domain 44 源：0 违例；生产副本注入、空/缺目录硬失败、生成目录排除均保持 PASS。
- 第 2 轮当时新增测试类：9/9；`DecisionClockTests`：14/14；合并选中：23/23；
  所有 XML `failed=0, errors=0, skipped=0, not-run=0`。
- 修复前后均只改 `DomainDeterminismSourceScanTests.cs` 与本报告；未改生产/原测试/
  csproj/verifier/fixtures/golden，未改 caller 历史产物。

### 10.4 新增信息说明

第 2 轮没有发现 D-R1-01 之外的新缺陷。该缺陷在上一轮无法由既有 27 样本矩阵发现，因为
矩阵只覆盖“整段字符串中的禁词”和“无嵌套插值”，没有覆盖“孔洞内部再出现字符串”的
词法状态；caller 的 33 探针首次提供了该反例，故第 2 轮一次性补齐同组全部 7 例及扩展
嵌套/逐字/注释/花括号形式，没有把同类问题留到下一轮。

第 3 轮 caller 的新格式说明探针又暴露 D-R2-01：第 2 轮新增的孔洞解析尚未区分“表达式
冒号”和“格式说明冒号”，把格式文本中的引号/字符当作 C# 字面量。该函数在第 1 轮不存在，
属于第 2 轮新引入逻辑的缺陷；已在本轮按 §11 修复，未留同类格式文本问题。

## 11. 第 3 轮返修：D-R2-01（MAJOR，已修复）

### 11.1 缺陷记录

| 字段 | 内容 |
|---|---|
| ID | **D-R2-01** |
| 严重级别 | **MAJOR**（BLOCKER/MINOR=0） |
| 位置 | `DomainDeterminismSourceScanTests.cs` 的 `MaskInterpolationHole` / `TryMaskNestedString` 及与 `MaskInterpolatedString` 的调用；报告 §4.4/§6/§10 旧能力结论 |
| 复现 | caller 证据根 `$E=D:/majiang-wt/domain-determinism-guard-20261026/build/artifacts/caller-domain-r2-987b59932deb443aa5f8db83efe19ab6`；执行 `powershell -NoProfile -ExecutionPolicy Bypass -File "$E/run-format-cases.ps1" -EvidenceRoot $E`。脚本 fresh 构建独立 reflection probe 与 6 个 C#9 样例（TreatWarningsAsErrors=true），调用交付 DLL 真实 `ScanDomainDirectory`；任一非 1 违例返回 native 1。 |
| 实际（修复前） | 格式说明中的引号/字符被当作新的 C# 表达式字面量，消耗外层结束符。case1 `$"{1:00\"DateTime.Now}"`、case2 `$"{DateTime.MinValue:yyyy-MM-dd\"T\"HH:mm:ss}"`、case3 `$"{1:0\\'}"` 均实际 0（`DOMAIN-DETERMINISM-PASS`），吞掉外层 `Guid.NewGuid`。6 例 `TOTAL=6 FAILED=3`，native 1。case4 逐字 `""`、case5 单引号、case6 简单格式为控制通过。 |
| 期望 | 六例每例恰 1 条外部 `Guid.NewGuid`，精确 `Syntax.cs:4:23`；native 0。 |
| 证据 | caller 原始日志 `$E/format-cases.log` SHA-256 `D065DF0D49025341035348DD1137E545B8358D42B7940CC139B386A624B55973`；`run-format-cases.ps1` SHA-256 `CB21EE9AE6A16949EAA00101316F4CD897159B9C4DBB358100BA2BD90A3558A8`；`format-probe/Program.cs` SHA-256 `ED1D7757642FD748D196B04B06E0C72136874F7E93B71787CEF8C3B8C3829F55`；caller `targeted.xml` SHA-256 `84009C50850C3824A00E73A78F12578DF1C984D5DF5A145688D50068BF284B62`；caller `build.log` SHA-256 `0E7778BAA816B8EFEAF43C376B69B58C7BF13D96FE3C77E165C6B79219A24071`。本轮复制到 `rework-r3/` 后运行的新日志见 §5.6，caller 原目录未覆盖。 |
| 修复要求 | 仅两个 expectedFiles 内修复格式说明词法边界：顶层 `:` 后的格式文本按字面量处理，引号/单引号/反斜杠/成对 `""`/`{{`/`}}` 不得吞掉孔洞外真实 API；不把任意冒号盲目当格式起点；保留 `?:`、`global::`、命名参数、nullable、嵌套插值、表达式字符/注释/花括号对照；不扫描孔洞表达式，不引入 package/完整解析器，不改生产/csproj/verifier/fixtures。 |
| 验收断言 | fresh Release `--no-incremental` native 0/0 warning/0 error；新类 + DecisionClockTests total>0 且 failed/errors/skipped/not-run 全 0；旧 33 探针仍 33/33；复制 6 样例 + probe 到自有新证据根后 6/6，每例恰 1 且 `Guid.NewGuid@Syntax.cs:4:23`；无外部 API 负控制 0；真实 44 源 0 违例；副本注入/枚举 fail-closed 保持；clean status/diff-check。 |

### 11.2 统一修复映射

| 修复点 | 实现 |
|---|---|
| 顶层冒号识别 | `MaskInterpolationHole` 维护 `braceDepth`/`parenDepth`/`bracketDepth`/`conditionalDepth`；仅在最外层孔洞（braceDepth=1）且 paren/bracket=0 时把冒号当格式起点 |
| `?:` 不误判 | 顶层 `?` 增加 `conditionalDepth`，对应 `:` 先按三元冒号消费；`??`、`?.`、`?[`、nullable `(int?)` 不增加条件深度 |
| `global::` 不误判 | `::` 成对跳过，不作为格式分隔 |
| 命名参数/括号/索引器 | `:` 在 paren/bracket/嵌套 brace 内保持表达式语义 |
| 格式文本 | `MaskFormatSpecifier` 从顶层 `:` 起按字面量跨到孔洞 `}`；`"`、`'`、`\`、`""` 不进入字符串/字符状态；`{{`/`}}` 按转义花括号跨过 |
| 外层恢复 | 格式文本结束后回到 `MaskInterpolatedString`，外层关闭引号之后代码继续扫描 |
| 自检 | 新增 6 格式正例 + 6 无 API 负控制 + `?:`/命名参数/`global::`/nullable 四个对照；`FormatSpecifierDiagnosticsMatchCallerCoordinates` 断言 `Syntax.cs:4:23` |

### 11.3 统一负例矩阵

- 6 格式正例：case1 转义双引号、case2 成对转义双引号、case3 转义单引号、case4 逐字成对
  双引号、case5 单引号文本、case6 简单格式——每例外层 Guid 恰 1。
- 6 无外部 API 负控制：同格式文本、`X() => 0`，全部 0。
- 4 对照：三元 `?:`、命名参数 `:`、`global::`、nullable cast `?`，外层 Guid 均恰 1。
- 精确坐标：`Syntax.cs:4:23 symbol=Guid.NewGuid`，同形状无 API 版本 0。
- 旧 33 探针：33/33；真实 44 源：0 违例；副本注入/空枚举/生成目录排除保持 PASS。

### 11.4 新增信息说明

本轮缺陷由 caller 的新格式说明探针首次提供，第 2 轮 18 样本矩阵只覆盖“孔洞内嵌字符串”
和简单 `:format`，没有覆盖格式文本中的转义双引号/单引号/反斜杠；第 2 轮因此无法发现。
该缺陷不是要求扩大孔洞 API 扫描或完整 C# 语义，而是第 2 轮新增逻辑再次吞掉孔洞外真实
API，违背原修复验收断言。本轮已批量覆盖同组 3 个假绿与全部格式/对照形式，没有把同类
问题留到下一轮；未发现其他新缺陷。

## 12. 第 4 轮返修：D-R3-01（MAJOR，已修复）

### 12.1 缺陷记录

| 字段 | 内容 |
|---|---|
| ID | **D-R3-01** |
| 严重级别 | **MAJOR**（BLOCKER/MINOR=0） |
| 位置 | `DomainDeterminismSourceScanTests.cs` 的 `MaskInterpolationHole`（`conditionalDepth` 与 `'?'` 处理）、`NextSignificantCharacter`；报告 §2/§6/§9/§11 相关能力声明 || 复现 | caller 证据根 `$E=D:/majiang-wt/domain-determinism-guard-20261026/build/artifacts/caller-domain-r3-bf37b7f5688a4a40be58760bfb0c5fd3`；执行 `powershell -NoProfile -ExecutionPolicy Bypass -File "$E/run-nullable-cases.ps1" -EvidenceRoot $E`。脚本 fresh 构建 format-probe 与 8 份同一项目 `Syntax.cs`（LangVersion9.0/TreatWarningsAsErrors=true），调用交付 DLL 真实 `ScanDomainDirectory`，任一非 1 违例返回 native 1。 |
| 实际（修复前） | `?` 被无条件当作三元运算符：泛型实参 `F<int?>()`、注释后 `default(int? /*c*/)`、`new List<int?>()`、`F<int?>() ?? 0`、嵌套 `F<List<int?>>()`、`F<int? /*c*/>()` 的 `?` 都残留 `conditionalDepth`，导致顶层格式 `:` 被当作三元冒号消费，随后格式转义引号再次按表达式字符串处理，遮蔽范围吞到字符串外。6/8 实际 0（`DOMAIN-DETERMINISM-PASS`），`TOTAL=8 FAILED=6`，native 1；case4 `(value as int?)`、case5 `((int?)value)` 通过。 |
| 期望 | 8/8，每例恰 1 条外部 `Guid.NewGuid`，精确 `Syntax.cs:6:23`；同形状删除外部 API 为 0；native 0。 |
| 证据 | caller 原始 `nullable-cases.log` SHA-256 `EC81F105EA45E9ADFD48D54E14B4B7654A512606899EEDBE4E779E2D2C3D7F1B`；`run-nullable-cases.ps1` SHA-256 `49E7BEFE3D1829871EF51CD55BB1D47B7EFB5DB20F1ECEF743C6E2E0D9969024`；`nullable-matrix/case1/Syntax.cs` SHA-256 `464251AA48D620BDE6FC96030561F443B3E94B074D39736575EC6D6C6E96F82D`；caller `build.log` SHA-256 `F9ABF7134D5E19883F053E6F5727F3310F509AF03D72F714C0E7E276FAAD0A69`；caller `targeted.xml` SHA-256 `D0B6B9C8676C90BFD2D5DA5965956F13F1DF73C5CCD2E130AF48811FBCF4698C`；caller `probe33.log` SHA-256 `846B542AE0DE9854FEA1A9D6B2CAF2B0858F5BCEB652B40E61EBD4E0B2BAB6E7`；caller `format-cases.log` SHA-256 `89F1C7CFA824300EEC194AE27EC94DD5BC77335CF96C4A809B7EE989DAF5E5D0`。本轮复制源码/脚本到 `rework-r4/` 后运行，caller 原目录未覆盖。 |
| 修复要求 | 仅两个 expectedFiles 内解决整组状态泄漏；把 8 例与无外部 API 对照固化为调用真实 scanner 的自检，精确文件/行/列/符号断言；保留旧 34 样本、旧 33 探针、格式 6 例与生产副本注入/fail-closed 扫描；保持显式时间输入/ShuffleConfig 合法；不改生产、原测试、csproj、verifier、fixtures、golden。 |
| 验收断言 | fresh Release `--no-incremental` native 0/0 warning/0 error 后才跑新 DLL；新类 + DecisionClockTests total>0 且 failed/errors/skipped/not-run 全 0；33/33、格式 6/6、nullable 8/8（每例 `Guid.NewGuid@Syntax.cs:6:23`）；正反控制全绿；真实 44 源 0 违例；枚举失败硬失败；副本注入有效；仓库生产源不变；clean status/diff-check。 |

### 12.2 统一修复映射

| 修复点 | 实现 |
|---|---|
| 注释/空白后的有效字符 | `NextSignificantCharacter` 跳过空白、`//` 行注释、`/* ... */` 块注释，避免 `int? /*c*/ )` 把 `/` 当后继 |
| nullable 类型标记 | `?` 后有效字符为 `>`/`)`/`]`/`,`/`;`/`}`/`:`/`=` 时不增加 `conditionalDepth`，直接按类型标记消费 |
| 嵌套泛型 | `F<List<int?>>()` 的 `?` 后为 `>`，不残留条件计数；后续 `>>` 正常跨过 |
| `??` / `?.` / `?[` | 仍按既有特殊分支跳过，不进入三元计数 |
| nullable cast/pattern | `(int?)value`、`(value as int?)`、`default(int?)` 保持控制通过 |
| 三元 `?:` | 真分支起首不是 nullable 后继集合时仍增加条件计数，对应 `:` 消费；保留既有 `?:` 对照 |
| 作用域恢复 | `conditionalDepth` 在 `{`/`(`/`[` 入栈、在对应 `}`/`)`/`]` 出栈恢复，避免嵌套 block 内 `int? x` 声明泄漏到外层格式冒号 |
| 格式冒号 | 无条件计数残留时，顶层 `:` 仍进入 `MaskFormatSpecifier`，格式文本不按 C# 字面量解析 |
| 自检 | 新增 `NullableTypeQuestionsDoNotLeakIntoFormatColonState`：8 caller 表达式 + 1 嵌套 block 控制，各配正/负控制；正例断言 `Guid.NewGuid@Syntax.cs:6:23`，负控制 0 |

### 12.3 统一负例矩阵

| # | 孔洞表达式 | 正例 | 负控制 |
|---|---|---|---|
| 1 | `F<int?>()` | 恰 1 `Guid.NewGuid@Syntax.cs:6:23` | 0 |
| 2 | `default(int? /* nullable comment */)` | 恰 1 | 0 |
| 3 | `new System.Collections.Generic.List<int?>()` | 恰 1 | 0 |
| 4 | `(value as int?)` | 恰 1（控制） | 0 |
| 5 | `((int?)value)` | 恰 1（控制） | 0 |
| 6 | `F<int?>() ?? 0` | 恰 1 | 0 |
| 7 | `F<System.Collections.Generic.List<int?>>()` | 恰 1 | 0 |
| 8 | `F<int? /* nullable comment */>()` | 恰 1 | 0 |
| 9 | `new Func<object>(() => { int? x = null; return x; })()`（嵌套 block 控制） | 恰 1 | 0 |

- 旧 34 样本插值/格式矩阵、旧 33 探针、格式 6 例、生产副本注入、空/缺目录硬失败、
  `bin`/`obj` 排除均保持 PASS。
- 真实 44 个 Domain 生产源仍 0 违例；测试未改实仓源。

### 12.4 新增信息说明

本轮缺陷由 caller 的新 nullable 探针首次提供：第 3 轮新增 `conditionalDepth` 和
`NextNonWhitespace` 时只覆盖了 `?` 后直接 `)` 的 cast，没有覆盖泛型 `>`、注释后 `)`、
嵌套泛型与 `??`；第 3 轮 34 样本/格式 6 例中也没有这些 nullable 形式，因此此前无法发现。
本轮已批量覆盖 8 个 caller 样例、1 个嵌套 block 控制及对应负控制，没有把同类状态泄漏
留到下一轮；未发现其他新缺陷。