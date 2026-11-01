# V5 CI dotnetTest 结果目录路径自检兼容性修复交付（2026-11-01）

状态：本任务范围实现与证据交付；**不是** G2 验收、不是 PR20 合并审查、不是远端 CI 新头认证。
V4 仍是唯一生产基线；G2–G5 未批准。

## 0. 身份、范围与结论

- 工作树：`E:/majiang-review/ci-dotnettest-path-ds-20261101-2345`
- 分支：`wt/v5-ci-dotnettest-path-ds-20261101-2345`
- accepted base：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`
- frozen candidate：`28a000349e604a9214c8225c15d102445cdb2cb6`（PR20 head，OPEN/Draft，base v5）
- 本任务 code commit 与报告 SHA-256：见交付回复；报告不嵌入自身哈希。
- 只修改以下 3 个授权文件，未改 workflow、产品/.NET 业务、Unity、scheduler、`complete.flag`、其他线程/锁：

| 文件 | 变更 |
|---|---|
| `tools/verify-v5.ps1` | 新增 `Resolve-DotnetTestResultsPath` 能力判定；生产调用点在无法转发路径时显式 fail-closed（不启动 runner）；`Get-DotnetTestResultsArgument` 保留为兼容边界 |
| `tools/ci/test-v5-dotnettest-results.ps1` | 结果路径 helper 合同矩阵（4 类 + 真实 OS 分支）；新增真实 raw 空格路径拒绝 live probe；原有矩阵/live 正负例全部保留 |
| `doc/v5-重构/V5-CI-DOTNETTEST-PATH-DELIVERY-20261101.md` | 本报告 |

本任务范围结论：**CI-PATH-01（MAJOR）FIXED**；本任务新增/修改路径上 0 个未修复 BLOCKER/MAJOR。
旧 CI 审计、已接受的 table 证据改造、C01/C02/C03 及已接受组件均不重开。

## 1. 缺陷闭环总表

| ID | 严重度 | 位置 | 原状态 | 本轮状态 |
|---|---|---|---|---|
| CI-PATH-01 | MAJOR | `tools/ci/test-v5-dotnettest-results.ps1:221-232`；`tools/verify-v5.ps1:491-512`、调用点原 1498 / 现约 1574 | 自检把“Windows 必有 space-free 8.3 别名”当作普遍能力；CI 上 helper 返回原长路径后自检直接 throw，dotnetTest gate 与 unified 步骤未执行 | **FIXED** |

同一根因的三个可复现症状合并为一组，**不拆成多条**：

1. **CI-PATH-01a**：自检断言 `($spaceArg -notmatch '\s') -and (Test-Path ...)`；远端 runner 的 helper 返回原长路径（含空格）时断言失败，脚本在 `:232` 抛出，后续 47 个用例（含 2 个 live probe）均未执行。
2. **CI-PATH-01b**：生产 helper 在无别名时只“返回原路径”，能力信息丢失；调用点仍会启动 `dotnet test`，只能依赖“无报告 -> gate 失败”的隐式 fail-closed，诊断不明确。
3. **CI-PATH-01c**：测试没有确定性覆盖“别名不可用/无效/命令失败”分支，也没有真实运行证据证明 raw 空格路径会被拒绝；本机 8.3 可用时旧断言只能证明“本机能用”，不能证明远端合同。

## 2. 原始失败与真实根因

### 2.1 远端原始失败（只读保留，未 rerun/retry）

冻结头 `28a00034` 的两次已完成 CI 均 FAIL：

- PR run `41320281905` / job `124888801070`：https://github.com/liuchangready/majiang-game/actions/runs/41320281905
- push run `41320276716` / job `124888785815`：https://github.com/liuchangready/majiang-game/actions/runs/41320276716

两次均在 `Verify dotnetTest-result evidence gate` 失败；原始 step 日志与 artifact metadata 已保留在本工作树证据根的 `history/preserved-ci/`（原件同时仍在
`E:/majiang-review/scheduled-ci-path-prep-20261101-2345/`）：

| 原件 | 字节 | SHA-256 |
|---|---:|---|
| `41320281905-failed.log` | 2,819 | `358886CA12BA779C82FD67C809BA005D1745989335F22764A04AEED4050BEF09` |
| `41320276716-failed.log` | 2,819 | `763620572535489B2BC7CFE84E1668C7452EC9DEF6CCE5A4896FE67DEFF77061` |
| `41320281905-artifacts.json` | 761 | `01648554F94EA208838C1A5E2C5E7A93C0F3D4B0D4B998B7983A542D783B2D25` |
| `41320276716-artifacts.json` | 761 | `028B49497A7C25703AA7DF74E5BAB7350A2F295F17557392F1BE577012B934B7` |
| `caller-helper-probe.json` | 412 | `0ADF759482DB7A18B1E003D95DEB85A1F4D93C791DDBACC87E954443711F8E0F` |

原始 actual：

```text
Results-directory argument helper failed for
'D:\a\majiang-game\majiang-game\artifacts\dotnettest-results-selftest-27d4ab689b06474ba7bff9cd9d2e4f89\00 space path\results'
-> 'D:\a\majiang-game\majiang-game\artifacts\dotnettest-results-selftest-27d4ab689b06474ba7bff9cd9d2e4f89\00 space path\results'
At D:\a\majiang-game\majiang-game\tools\ci\test-v5-dotnettest-results.ps1:232 char:25
Process completed with exit code 1.
```

期望：Windows 上没有保证存在的 8.3 别名时，自检应判定“该路径不可转发”并验证 fail-closed，而不是要求环境必有一个不保证存在的能力；普通空格无关注释路径与真实正负例继续通过。

### 2.2 真实根因（本机真实运行复现）

锁定 runner（xunit.v3 `4.0.2` / Microsoft.Testing.Platform `2.5.1` / SDK `8.0.426`）通过 MSBuild bridge 转发 `--` 选项时，把参数重新拼成空格分隔的单一命令行。以真实小项目分别执行：

- raw 长路径（不替换别名）：`--results-directory "…\00 space path\results"` → 原生 exit **1**，声明目录**没有** `dotnet-test-results.xml`；框架日志首行为：

```text
Option '--results-directory' from provider 'Platform command line provider' (UID: PlatformCommandLineProvider) expects at most 1 arguments
Command line: --internal-msbuild-node testingplatform.pipe.… --report-xunit-xml --report-xunit-xml-filename dotnet-test-results.xml --results-directory E:\…\00 space path\results
```

- 同一路径经生产 helper 取得 8.3 别名：原生 exit **0**，XML 写到原长声明目录，`Test-DotnetTestInvocationEvidence` 返回 PASS（2/2，console 2，node 2）。

证据：

- `build/path-probe-37da38293cd7/probe-results.json`，SHA-256 `C59FE45C1538D4EA8BEF813F8D1275E69583315609DD117E6E3C334D2DA07894`
  - raw：native 1 / XML false / 无 gate
  - via helper：native 0 / XML true / gate true / XML SHA `F35422DC3174E0F3EB024877027C14517EE3F30EAE90FE3EB937D11EA0A31A8F`
- `raw-long-path.log` SHA `E320154CCF51C265C76A89A0194DD64F04EF2C74336E09800926CCEE44A71E02`
- `helper-argument.log` SHA `39A2C0B0B7ECFDA0EA94D2F3CACB182DA6370371DCCF679B04EA29E5414E3CEA`
- 框架失败日志 `Probe.Path.Tests_net8.0_x64.log` SHA `4B082E0F55BA16B1A71F38EC555F072385B4C6F1D420794362090690E93CF3DA`

结论：该 runner 组合**确实不支持**含空格的 raw 结果路径；唯一可行方式是 space-free 的 8.3 别名。8.3 是否可用是**环境能力，不是保证**；本机可用不能覆盖远端不可用。修复选择“显式检测 + 对该调用 fail-closed”，不修改机器/卷的 8.3 设置。

## 3. 修复映射

### 3.1 生产共享路径（仅 `tools/verify-v5.ps1`）

新增真实生产函数：

```powershell
Resolve-DotnetTestResultsPath([string]$Path, [scriptblock]$ShortNameResolver = $null)
```

返回统一计划对象：

| Mode | Supported | 含义 |
|---|---:|---|
| `direct` | true | 路径无空白，原样作为单个参数 |
| `short-alias` | true | 含空白，取得有效、存在、无空白的 8.3 别名 |
| `unsupported-no-alias` | false | 含空白且无 space-free 别名（返回原长路径） |
| `unsupported-invalid-alias` | false | helper 返回不存在/无效路径 |
| `unsupported-resolver-error` | false | helper/命令抛错 |
| `unsupported-invalid-path` | false | 路径含双引号，拒绝构造 cmd 探针 |
| `non-windows-direct` | true | 非 Windows 保持原行为（本任务未实测） |
| `invalid` | false | null/空/空白路径 |

- `Get-DotnetTestResultsArgument` 保留原名与返回字符串的兼容边界，内部委托上述函数；返回原长路径**不再**被解释为成功。
- 生产调用点（约 1574 行）在启动 runner 前读取计划；`Supported=false` 时：
  - 不执行 `dotnet test`，不产生/声明 XML；
  - 写 `dotnetTest.meta.txt`：`runner = dotnet test (NOT INVOKED: results argument cannot be forwarded)`、`exit code = NOT_RUN`、`dotnetTest xml gate = NOT_RUN: <mode>: <reason>`；
  - 行结果 `FAIL`、`Runner=dotnet test (NOT INVOKED)`、`ExitCode=NOT_RUN`，summary detail 明确写出 bridge 拒绝原因；
  - `anyFail=true`，绝不落回 direct 或任何 PASS。
- 该判定只影响“含空白且无可用别名”的结果目录；普通无空格路径与有效别名路径行为不变。CI 实际 LogDir `D:/a/majiang-game/majiang-game/artifacts/...` 无空格，生产不受影响；若未来本地/远端 LogDir 带空格且无 8.3，则得到明确的 FAIL 而不是隐式缺报告。

### 3.2 自检合同（`tools/ci/test-v5-dotnettest-results.ps1`）

- AST 仍从真实 `tools/verify-v5.ps1` 提取生产函数；新增 `Resolve-DotnetTestResultsPath` 到提取列表，不复制弱 verifier。
- 原 `00 results path argument` 单条“Windows 必须 space-free”断言替换为确定性矩阵：

| # | Case | 类别 | 期望 |
|---:|---|---|---|
| 1 | `00 results path direct no whitespace` | 无空格路径 | `direct` / supported / 参数不变；resolver 不被调用 |
| 2 | `00 results path valid short alias fixture (TOOL_FIXTURE)` | 有效 short alias | `short-alias` / supported / 参数为无空白目录 |
| 3 | `00 results path alias unavailable fixture (TOOL_FIXTURE)` | 别名不可用/返回原长路径 | `unsupported-no-alias` / 不支持 / 参数为原长路径 |
| 4 | `00 results path invalid alias fixture (TOOL_FIXTURE)` | helper 返回无效路径 | `unsupported-invalid-alias` / 不支持 |
| 5 | `00 results path resolver failure fixture (TOOL_FIXTURE)` | helper/命令失败 | `unsupported-resolver-error` / 不支持 |
| 6 | `00 results path real OS capability (no 8.3 assumption)` | 真实本机分支 | 只断言不变量：supported=>space-free 且存在；unsupported=>原长路径；**不假设 8.3 必有** |

- 第 6 条在 CI（无 8.3）会记录 `unsupported-no-alias`、`CapabilitySupported=false` 且 `Pass=true`；在本机（有 8.3）记录 `short-alias`、`CapabilitySupported=true`。这是能力分支记录，不是把断言改成永远 true。
- 第 2 条的 TOOL_FIXTURE 只验证决策形状（存在、无空白、参数替换）；真实 8.3 别名与原长目录的等价性由第 6 条真实 `%~sI` 分支和 call-site B（XML 真正落到原长声明目录）证明，夹具本身不冒充 OS 别名。
- 新增真实 live 负例 `46 live dotnet test rejects raw whitespace results argument`：把 raw 长路径直接交给锁定 runner，断言：
  - native exit 非 0（实测 1）；
  - 声明目录无 `dotnet-test-results.xml`；
  - 生产 `Test-DotnetTestInvocationEvidence` 返回 `Ok=false`、Reason 为缺报告；
  - 该用例不是 synthetic XML，不是 skip/空测试。
- 原有 47 个非 helper 用例（2 parser + 31 gate + 12 identity/schema + 2 live）全部保留且通过；原 live 正例（3 cases，含重复 display name）与原 failing negative（1 failed node，native 1）继续有效。

### 3.3 四类边界与“不可转 PASS”清单

| 边界 | 覆盖 | 断言 |
|---|---|---|
| 无空格路径 | 矩阵第 1 条；live 正例；call-site C | 参数不变，真实 dotnet test 写 XML 到声明目录，gate PASS |
| 有效 short alias | 矩阵第 2 条；真实 OS 分支（本机）；call-site B | 参数 space-free 且存在，XML 落到原长声明目录，gate PASS 2/2 |
| 别名不可用/返回原长路径 | 矩阵第 3 条；真实 OS 分支（CI 预期）；call-site A（TOOL_FIXTURE 模拟无 8.3）；live 负例 46 | 显式 unsupported；生产不启动 runner；raw 运行 native 1、无 XML、gate 拒绝 |
| helper/命令失败或无效路径 | 矩阵第 4/5 条 | unsupported；不得返回 PASS |

缺报告、空 XML、坏 XML、过期 XML、越界 XML、重复 id、计数不一致、failed/skipped/not-run/errors、native 非零/未知、console 缺失/不一致、Total 0、schema/framework 不匹配等原负例全部保留，仍由同一生产 gate 拒绝；没有用 skip、空测试、放宽断言、刷新摘要或合成 XML 获得绿色。

## 4. 真实 CLI / 生产调用点证据

为避免只做字符串单测，使用真实 `tools/verify-v5.ps1` + 真实 throwaway xunit.v3/MTP 项目执行三次调用点回归（21/21 断言）：

| Case | 环境 | 结果 |
|---|---|---|
| A `A-spaced-no-alias` | LogDir 含空格；TOOL_FIXTURE fake `ComSpec`（仅子进程环境变量）模拟卷无 8.3 别名 | native exit 1；`OVERALL: FAIL`；summary 明确 `cannot be forwarded through the MTP/MSBuild bridge`；row `dotnet test (NOT INVOKED)` / `0 0 0 FAIL`；**无** XML；**无** `dotnetTest-Probe.Tests.log`；meta `exit code : NOT_RUN` |
| B `B-spaced-real-os` | LogDir 含空格；真实 cmd `%~sI`（本机有 8.3） | native exit 0；`OVERALL: PASS`；XML 存在，2/2 Pass；meta exit 0；meta command 含 `~` 短别名 |
| C `C-normal` | 普通无空格 LogDir；真实 cmd | native exit 0；`OVERALL: PASS`；XML 2/2；meta exit 0；meta command 为 direct 路径 |

- 证据：`build/ci-path-evidence-20261101-2350/cli-rerun2/call-site-results.json`，SHA-256 `0F93BD068A743FA88874555FC11113CB07BE9E5A328E2D5D93282E3DD10F4A6B`；21 passed / 0 failed。
- A 的 fake `ComSpec` 是**受控环境夹具**，只在该子进程环境生效，未改系统/卷 8.3、未建全局 drive/junction；B/C 使用真实 `C:\Windows\system32\cmd.exe`。
- 首次 call-site 尝试（`history/cli-first-attempt/`、`history/cli-second-attempt/`）分别因 fake `.cmd` 不能作为 ComSpec 与记录断言问题失败；原件保留，未删除/覆盖，最终证据使用 `cli-rerun2/`。

## 5. 实际执行、计数与首失败

### 5.1 本机 Windows PowerShell 5.1 实际执行

| 命令 | exit | 结果 |
|---|---:|---|
| `tools/ci/test-v5-dotnettest-results.ps1`（完整矩阵 + 3 live） | 0 | **54/54**；新增 helper 矩阵 6 条、raw live 负例 1 条；原有全部保留 |
| `tools/ci/test-v5-native-exit.ps1` | 0 | **24/24** |
| `tools/ci/test-v5-direct-results.ps1` | 0 | **19/19** |
| `tools/ci/test-v5-table-evidence.ps1` | 0 | **82/82** |
| call-site 真实 CLI（A/B/C） | 0 | **21/21** 断言 |
| `build/path-probe-…/probe.ps1`（raw vs helper） | n/a | raw native 1/无 XML；helper native 0/XML true/gate 2/2 |
| `tools/tests/VerifyV5Diagnostics.Tests.ps1`（既有漂移，**不计通过**） | 1 | **32 PASS / 7 FAIL**，S7 中止；冻结基线同为 32/7（§7） |
| baseline `28a00034` 行格式复现 | 1 | 证明 diagnostics 旧断言在**冻结基线**已不匹配（见 §7） |

SDK `8.0.426`；shell Windows PowerShell `5.1.19041.6456`；dotnet `D:\Unity\dotnet\dotnet.exe`。所有 TEMP/TMP、NuGet cache、DOTNET_CLI_HOME、build/restore 与证据均在本工作树独占 E 盘；未向 D 盘写入大文件。

### 5.2 首失败保留

1. **远端首失败**：`41320281905` / `41320276716` 的 `test-v5-dotnettest-results.ps1:232` 原样保留（§2.1），未 rerun/retry。
2. **本任务首次 call-site 失败**：fake `.cmd` 作为 ComSpec 被 PowerShell 5.1 拒绝（`ApplicationFailedException: not a valid application`）；改用受控 fake `.exe` 后 A 通过。原件在 `history/cli-first-attempt/`、`history/cli-second-attempt/`，未覆盖。
3. **首次 diagnostics 回归**：32 PASS / 7 FAIL 后中止；经冻结基线复现证明为**既有、非本任务引入**的测试/格式漂移（§7），未修改该测试，也未把它写成通过。

### 5.3 完整 54 例清单

`results.json` 的 `cases=54 passed=54 failed=0`；逐例字段如下（NodeCount / ConsoleTotal / NativeExit / XmlExists 为空表示该用例不适用）。完整 JSON：`build/ci-path-evidence-20261101-2350/dotnettest-selftest-final/results.json`，SHA-256 `98C25392739D9D27B2A4CDB401A002913F3803E0D209AB5C447BA5249C280440`。

| # | Case | ExpectedOk | ActualOk | Pass | Nodes | Console | Native | XML |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | 00 results path direct no whitespace | True | True | True |  |  |  |  |
| 2 | 00 results path valid short alias fixture (TOOL_FIXTURE) | True | True | True |  |  |  |  |
| 3 | 00 results path alias unavailable fixture (TOOL_FIXTURE) | False | False | True |  |  |  |  |
| 4 | 00 results path invalid alias fixture (TOOL_FIXTURE) | False | False | True |  |  |  |  |
| 5 | 00 results path resolver failure fixture (TOOL_FIXTURE) | False | False | True |  |  |  |  |
| 6 | 00 results path real OS capability (no 8.3 assumption) | True | True | True |  |  |  |  |
| 7 | parser english passed | True | True | True |  | 180 |  |  |
| 8 | parser english failed | True | True | True |  | 2 |  |  |
| 9 | 01 positive green with duplicate display names | True | True | True | 3 | 3 |  |  |
| 10 | 02 positive path with spaces | True | True | True | 1 | 1 |  |  |
| 11 | 03 missing xml | False | False | True | 0 | 3 |  |  |
| 12 | 04 empty xml | False | False | True | 0 | 3 |  |  |
| 13 | 05 malformed xml | False | False | True | 0 | 3 |  |  |
| 14 | 06 truncated xml | False | False | True | 0 | 3 |  |  |
| 15 | 07 zero tests | False | False | True | 0 | 0 |  |  |
| 16 | 08 assembly summary only | False | False | True | 0 | 3 |  |  |
| 17 | 09 wrong assembly leaf | False | False | True | 0 | 3 |  |  |
| 18 | 10 wrong assembly path same leaf | False | False | True | 3 | 3 |  |  |
| 19 | 11 wrong runner framework | False | False | True | 3 | 3 |  |  |
| 20 | 12 assembly total vs node mismatch | False | False | True | 2 | 3 |  |  |
| 21 | 13 collection total vs node mismatch | False | False | True | 3 | 3 |  |  |
| 22 | 14 assembly passed mismatch | False | False | True | 2 | 2 |  |  |
| 23 | 15 collection passed mismatch | False | False | True | 2 | 2 |  |  |
| 24 | 16 duplicate native ids | False | False | True | 3 | 3 |  |  |
| 25 | 17 stale xml | False | False | True | 0 | 3 |  |  |
| 26 | 18 results directory outside logdir | False | False | True | 0 | 3 |  |  |
| 27 | 19 xml outside project results directory | False | False | True | 0 | 3 |  |  |
| 28 | 20 path escape | False | False | True | 0 | 3 |  |  |
| 29 | 21 unexpected file name | False | False | True | 0 | 3 |  |  |
| 30 | 22 failed test | False | False | True | 2 | 2 |  |  |
| 31 | 23 skipped test | False | False | True | 2 | 2 |  |  |
| 32 | 24 not-run test | False | False | True | 2 | 2 |  |  |
| 33 | 25 framework errors | False | False | True | 1 | 1 |  |  |
| 34 | 26 native exit nonzero with green xml | False | False | True | 3 | 3 |  |  |
| 35 | 27 native exit unknown | False | False | True | 3 | 3 |  |  |
| 36 | 28 console summary missing | False | False | True | 3 |  |  |  |
| 37 | 29 console total mismatch | False | False | True | 3 | 2 |  |  |
| 38 | 30 console failed mismatch | False | False | True | 3 | 3 |  |  |
| 39 | 31 console skipped mismatch | False | False | True | 3 | 3 |  |  |
| 40 | 34 identity exact 4.0.2 without metadata schema 3 | True | True | True | 3 | 3 |  |  |
| 41 | 35 identity 4.0.2+build.1 schema 3 | True | True | True | 3 | 3 |  |  |
| 42 | 36 identity wrong patch 4.0.1 | False | False | True | 3 | 3 |  |  |
| 43 | 37 identity wrong patch 4.0.20 | False | False | True | 3 | 3 |  |  |
| 44 | 38 identity wrong patch 4.0.200 | False | False | True | 3 | 3 |  |  |
| 45 | 39 identity trailing junk 4.0.2INVALID | False | False | True | 3 | 3 |  |  |
| 46 | 40 identity empty build metadata 4.0.2+ | False | False | True | 3 | 3 |  |  |
| 47 | 41 identity wrong runner family v2 | False | False | True | 0 | 3 |  |  |
| 48 | 42 schema 999 unsupported | False | False | True | 3 | 3 |  |  |
| 49 | 43 schema 2 unsupported | False | False | True | 3 | 3 |  |  |
| 50 | 44 schema 4 unsupported | False | False | True | 3 | 3 |  |  |
| 51 | 45 schema 3 valid with metadata | True | True | True | 3 | 3 |  |  |
| 52 | 32 live dotnet test positive | True | True | True | 3 | 3 | 0 | True |
| 53 | 33 live dotnet test controlled failure | False | False | True | 1 | 1 | 1 | True |
| 54 | 46 live dotnet test rejects raw whitespace results argument | False | False | True | 0 |  | 1 | False |

## 6. 证据路径与 SHA

证据根：`build/ci-path-evidence-20261101-2350/`（worktree 相对；本树独占，未覆盖既有证据）。下表全部为 worktree 相对路径：

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| `build/ci-path-evidence-20261101-2350/dotnettest-selftest-final/results.json` | 40,031 | `98C25392739D9D27B2A4CDB401A002913F3803E0D209AB5C447BA5249C280440` |
| `build/ci-path-evidence-20261101-2350/runs/dotnettest-selftest-final/stdout.log` | 14,078 | `305E3A748D07EA10389CE1084064674114FA19B074A44D0262135081D938E141` |
| `build/ci-path-evidence-20261101-2350/runs/dotnettest-selftest-final/stderr.log` | 0 | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` |
| `build/ci-path-evidence-20261101-2350/runs/dotnettest-selftest-final/native.json` | 559 | `338C6500A68C82FCAA41BFE6D98EC2ECEC2D4EA8DFA02FD1E7724912DFEC8DA2` |
| `build/ci-path-evidence-20261101-2350/cli-rerun2/call-site-results.json` | 9,468 | `0F93BD068A743FA88874555FC11113CB07BE9E5A328E2D5D93282E3DD10F4A6B` |
| `build/path-probe-37da38293cd7/probe-results.json` | 38,225 | `C59FE45C1538D4EA8BEF813F8D1275E69583315609DD117E6E3C334D2DA07894` |
| `artifacts/native-exit-551af1fc3bce483986db26ac66da5280/results.json` | — | `25391C0EFF2AC04F75FF19B127F9B619E471A0544539B1FA2C0E2E623B7262F6` |
| `artifacts/direct-results-selftest-4039cfb23eb44d3fb280af0238240190/results.json` | — | `07ABBE55E8C4D3C5AB89EB539A1E2650439D39C16171FFAD477548F243EA48E8` |
| `artifacts/table-evidence-selftest-7721c7d80c7648049c27ac1f44cbf34c/results.json` | — | `0E92CF2B8E317EE0971E986D52762742C73D4B6ADE6E059A0FB0F8DAB88F3AEF` |
| `build/baseline-28a00034/build/baseline-row-check.json` | — | `7A440ABB4B9FAF9A0890EA073532EE1EFE650F75BAAF6E8822B665B49A0A2993` |

- live fixture 根：`build/dotnettest-selftest-48ef72e14df74b688dfe14b94f04470d/live-fixtures/`
- 正例 XML：`…/Probe.Positive.Tests/native-results/dotnet-test-results.xml`；负例 XML：`…/Probe.Negative.Tests/native-results/dotnet-test-results.xml`；raw 负例目录 `…/Probe.Positive.Tests/native results/results/`（无 XML）。
- 原始测试 XML/console/native 记录均在上述 GUID 目录与 `runs/` 下，未覆盖。
- 结果声明全部结束后才生成 `evidence-manifest.sha256.json`（逐文件 SHA/bytes，排除自身与 `.tmp`），manifest 生成后不再追加已哈希日志。

## 7. 既有、非本任务引入的发现（不修复）

`tools/tests/VerifyV5Diagnostics.Tests.ps1`（不在本任务授权 3 文件内）的 `Assert-RowCounts` 仍按 `dotnet test` 无 `(MTP)` 后缀的旧 summary 格式匹配；冻结基线 `28a00034` 的真实行已是
`dotnet test (MTP)`，因此该断言在**基线**就为 false。基线复现（真实运行）：

```text
row: Probe.Tests  test  -  NO!  dotnet test (MTP)  2  1  0  FAIL  dotnetTest-Probe.Tests.log
regex matches row: false
collectedFiles: []
```

- 基线 verify SHA-256：`41510892820C6B1E72B8BD182C171C23778E992F0E8AD6B87505862CCB1C065B`（与 D-A68-02 独立验收记录一致）。
- 在**冻结基线 28a00034 的独立提取树**上完整运行同一 diagnostics 测试，得到与当前树**完全相同**的 **32 PASS / 7 FAIL**、相同 7 条失败、同样在 S7 中止（`build/ci-path-evidence-20261101-2350/history/baseline-diagnostics/`：stdout SHA `3C21FBB6A326E3B02587C95968E455D900F4DF9234132B2E51BC501AEB123609`，stderr SHA `71404F66A1AA5153881D8D929A86AAFCB3727B7CEFCF19902F3FCD9B4A41B5E2`）。这证明失败是基线既有测试/runner 格式漂移，**不是本任务改动引入**。
- 本任务运行该 diagnostics 回归得到 32 PASS / 7 FAIL 并在 S7 中止；失败集中在旧行格式、旧框架日志收集预期与 zero-test 文本；均不经过本任务改动的 helper/guard 代码路径。该测试**未被本任务写成通过**，也未修改。当前树原始输出：`build/ci-path-evidence-20261101-2350/runs/diagnostics-regression/`（stdout SHA `3C21FBB6A326E3B02587C95968E455D900F4DF9234132B2E51BC501AEB123609`，stderr SHA `279EA6022F776C9EA8A54A342777B8B572CD21B47B70BBA5E84D432221B621F1`）。
- 结论：这是既有 MINOR 级测试漂移；若要修复需单独授权修改该测试（例如按 `(MTP)` 兼容解析），本任务不做。

## 8. 残余与明确未覆盖

1. **无新远端 CI 运行**：本任务未 push、未 rerun/retry、未建/改 PR、未 record MR、未 approve/merge/deploy。不能声明新提交 CI 已 PASS；本报告的所有“PASS”仅指本机 Windows PowerShell 5.1 实际执行。
2. **无完整 dotnetTest 全量 suite**：未运行 14 工程/统一 .NET 全量；54/54 是门禁自检 + 3 个 live 小项目，不是全量 suite。
3. **无自然整场 PG / Unity / Player / Android / 设备**：未启动 Unity、Player、PG、Android、设备；G2–G5 未批准，V4 生产。
4. **历史 `39970580589` framework 日志缺失仍 BLOCKED**：未重跑旧 CI、未宣布其根因已修。
5. **非 Windows 分支 NOT_RUN**：`non-windows-direct` 仅保留原行为，未实测。
6. **8.3 能力**：未修改机器/卷 8.3 设置；未新建全局 drive/junction；本机有 8.3、CI 无 8.3 是环境事实，报告不假设二者相同。
7. **call-site A 的 fake ComSpec** 是 TOOL_FIXTURE；它证明生产 guard 在“无别名”输入下的行为，不等于远端 runner 的真实 8.3 状态。远端真实失败见 §2。
8. 已接受旧组件、C01/C02/C03、table/direct/native 历史范围不重开；本报告不改变其结论。

## 9. 提交/工作树约束

- 只暂存/提交第 0 节 3 个授权文件；`git diff --cached --check` 与 `git diff --check` 为 0。
- 生成物（`artifacts/`、`build/`、NuGet cache、日志、manifest）全部 ignored，不提交。
- 提交后逐项核对暂存区、diff-check、tracked clean；工作树中仅有本任务授权的 3 个文件被提交。
- 未修改 workflow、产品/.NET 业务、Unity、scheduler、`complete.flag`、其他线程/树/锁；未杀他人进程、未清理其他工作树。
