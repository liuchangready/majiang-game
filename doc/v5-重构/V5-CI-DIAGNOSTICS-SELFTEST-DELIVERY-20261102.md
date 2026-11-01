# V5 Diagnostics 自检恢复交付（2026-11-02）

状态：**实现与本地实测完成，待调用方独立复核与 PR20 发布归属处理**。本交付不代表合并 `v5`、不代表 V4/V5 生产验收、不代表 G2–G5 批准。

## 0. 身份、范围与结论

- 工作树：`E:/majiang-review/diagnostics-selftest-ds-20261102-0113`
- 分支：`wt/v5-diagnostics-selftest-ds-20261102-0113`
- accepted base：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`
- 冻结来源（本任务复现对照）：`28a000349e604a9214c8225c15d102445cdb2cb6`
- 已接受候选代码：`ccdfac6b16d116a3b336b6b63de51103f66bb7bf`
- 既有 PR20 仍 OPEN/Draft；本任务不 push、不改 PR、不 approve、不 record MR、不 merge/deploy、不改 scheduler/`complete.flag`。
- 授权文件仅两个：
  1. `tools/tests/VerifyV5Diagnostics.Tests.ps1`
  2. `doc/v5-重构/V5-CI-DIAGNOSTICS-SELFTEST-DELIVERY-20261102.md`
- 未修改 `tools/verify-v5.ps1`、生产 helper、workflow、业务/.NET、包版本、AGENTS 或主仓文档。
- 环境：Windows PowerShell `5.1.19041.6456`；`D:/Unity/dotnet/dotnet.exe`，SDK/runtime `8.0.426`；所有 TEMP/TMP/DOTNET_CLI_HOME、构建、日志、证据、NuGet 测试缓存均在本树独占 E 盘 `build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/`。D 盘未写入。
- 结论：原脚本在冻结基线与当前候选上均可复现 **32 PASS / 7 FAIL，并在 S7 以原生未处理错误中止（native exit 1）**；修复后同一完整 S1–S11 分母在 PS 5.1 + 当前 SDK 上实际运行 **125/125 checks PASS，native exit 0**，S8–S11 与负例矩阵全部实际执行。受控失败场景（S1/S3/S5/S7/S10）仍按预期非零，未被写成产品全绿。

## 1. 原样复现（修复前）

命令（在独占 `TEMP`/`DOTNET_CLI_HOME`/NuGet 缓存下，PS 5.1）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/tests/VerifyV5Diagnostics.Tests.ps1 `
  -DotnetPath D:/Unity/dotnet/dotnet.exe -KeepWorkspace
```

原始输出：

- stdout：`build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/repro/original.stdout.log`
  （2,864 bytes，SHA-256 `2C14AB4D61E5282A510B31F2CC6E97CB1D1AA3846C52F0C12CDB0EFB6DB8708C`）
- stderr：`build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/repro/original.stderr.log`
  （731 bytes，SHA-256 `9DC93EC13CF488C5A7B3A9A46EF468EB00731E729BDFFA5725B78574A7A03361`）
- 原生退出：`1`
- 保留的原始 workspace：`build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/temp/VerifyV5Diagnostics-20261102-011751-da468e63/`
- 固定分母：S1–S11。原脚本实际执行到 S7 的断言检查后，在 S7 的 `Get-ChildItem -LiteralPath $evidence7` 处未处理异常中止；S8–S11 与 run identity **NOT_RUN**。
- 原脚本实际断言结果：**32 PASS / 7 FAIL**；S7 中止不是第 8 个断言失败，而是原生未处理错误。
- 第一条失败：S1 `row keeps real counts ...`（HEAD 脚本第 352 行）。
- 中止点：HEAD 脚本第 514 行；实际错误为 `Get-ChildItem : Cannot find path '...\logs-s7\test-results' because it does not exist.`

原始失败清单（逐条原样）：

| ID | 场景/位置 | 实际 | 期望 |
|---|---|---|---|
| D-DIAG-01 | S1/S2/S7/S10 `Assert-RowCounts`（HEAD:290,352,383,504,648） | summary runner 字段为 `dotnet test (MTP)`，旧正则按 `dotnet test` 匹配，返回 false | 准确绑定真实 MTP runner 列与同一行计数 |
| D-DIAG-02 | S1/S6/S7（以及未运行到的 S9/S10）旧框架日志路径（HEAD:365-370,477,505-510） | 当前 accepted runner 把 raw framework log 写到 `dotnetTest-xml/Probe.Tests/Probe.Tests_net8.0_x64.log`；项目 `test-results` 目录不存在 | 从当前 accepted 证据根读取 raw 日志，并仍验证旧 stale 文件不得混入 |
| D-DIAG-03 | S7 HEAD:513-514 | `$evidence7` 不存在，`Get-ChildItem` 在 `$ErrorActionPreference='Stop'` 下抛出，脚本中止，native exit 1；S8–S11 NOT_RUN | 枚举前先证明目录存在；缺失目录不能中止后续场景，也不能被当作“当前失败证据通过” |
| D-DIAG-04 | S5 HEAD:462 | 旧文本 `zero executed test cases` 不再出现；当前真实 gate 在 native exit 1 处失败，raw 日志写 `Zero tests ran` | 零测试仍必须非零失败，断言当前真实 XML/raw/native 合同，而不是旧文案 |

补充事实：原 S1/S6/S7 的当前 raw framework log 实际存在且包含失败 case/断言（S1）或 Passed 摘要（S6），只是位置与旧断言不同；因此不是“日志不存在”，而是“旧路径/旧格式断言漂移”。S1 的 native XML（2 total / 1 failed）与 console/meta 均一致，证明失败是自检断言问题，不是生产采集回归。

## 2. 修复映射

### 2.1 行合同（D-DIAG-01）

`Get-SummaryRows` 按 summary 表头列偏移解析，不再使用宽松 `dotnet test` 正则；`Test-SummaryRow` 要求：

- 只有一个 `Probe.Tests` 数据行；重复行、其他项目行拒绝；
- runner 必须是已知生产 runner 之一：`dotnet test (MTP)`、`in-process (direct)`、`dotnet test (no native report)`、`dotnet test (NOT INVOKED)`，并与期望 runner 全字段相等；
- `KIND=test`；`TOTAL/FAILED/SKIPPED` 为整数、非负、`failed+skipped <= total`；
- 非零分母场景 `total > 0`；`PASS` 行必须 `failed=0, skipped=0, total>0`；
- 计数与期望逐项相等；`LOG` 必须是非空 `.log` 列。
- 零测试场景显式使用 `-AllowZeroTotal`，并要求 `result=FAIL`。

### 2.2 证据合同（D-DIAG-02）

`Get-DotnetTestEvidence` / `Test-DotnetTestEvidence` 从当前 accepted 合同读取：

- native per-case XML：`<log dir>/dotnetTest-xml/Probe.Tests/dotnet-test-results.xml`；
- raw framework log：同一结果目录下唯一的 `Probe.Tests_net8.0_x64.log`；
- console：`<log dir>/dotnetTest-Probe.Tests.log`；
- 逐调用 meta：`<log dir>/runs/Probe.Tests/dotnetTest.meta.txt`。

断言 summary 行、XML 计数、raw 日志计数、meta `exit code/total/failed/skipped` 与 native exit 全部一致；失败场景必须同时满足：

- XML 恰好一个同名失败 case，`result=Fail`，failure message 含本次断言文本；
- raw framework log 含 case 名与断言文本，且是 `Failed!` 摘要；
- native exit 非零。

PASS 场景要求 XML 全部节点 Pass、raw 为 `Passed!`、native exit 0。零测试场景要求 native exit 非零、XML `total=0` 且无节点、raw 为 `Failed!` 或 `Zero tests ran`。

`summary.log` 仅作为派生 gate 行，不再作为失败内容证明；raw/XHTML/XML/console 的角色在测试标签中明确区分。

### 2.3 S7 中止（D-DIAG-03）

S7 在枚举 `test-results` 前加 `Test-Path`；缺失目录不再抛异常，但也不被当作“stale 未泄漏”或“当前失败已证明”的替代证据。S7 的当前失败由 `dotnetTest-xml` 的 XML + raw 日志证明，陈旧内容仍由 meta `NOT CLEARED/excluded stale files`、locked 文件仍存在、以及任何存在的 `test-results` 内容扫描共同验证。

### 2.4 零测试（D-DIAG-04）

S5 不再要求旧 `zero executed test cases` 文本；改为：

- exit 非零、`OVERALL: FAIL`；
- 行 runner 为 `dotnet test (MTP)`，`total/failed/skipped = 0/0/0`，`result=FAIL`；
- native XML `total=0`；
- native exit 非零；
- raw 为 `Failed!` 或 `Zero tests ran`；
- detail 允许当前 `native exit is 1` 或零测试旧 gate 文案之一，但整行仍必须 FAIL。

### 2.5 保持的既有断言

S1–S11 的意图全部保留：

- S1/S3：真实 xunit.v3/MTP 抛错项目的受控失败仍非零，case 名/断言、真实计数、native meta 与本次框架证据保留；
- S2/S4/S6/S9/S10：stale 日志清理、不可清理、独占锁中途释放、快照不可枚举都不能泄漏旧证据；
- S8：两 source root 同名文件隔离、auto 两 runner 分目录、`Invoke-Captured` 保留 partial+exception 且不伪造 exit 0；
- S11：快照枚举失败时整个不可信目录排除；
- run identity：记录真实 HEAD。

### 2.6 自检负例矩阵（TOOL_FIXTURE，22 checks）

在 S8 之后、S9 之前增加纯测试断言负例，全部标记 `TOOL_FIXTURE`，不进入产品覆盖分母：

- 错误 runner（旧 `dotnet test`、direct 行冒充 MTP）；
- 错误 total、failed、skipped；
- `Total=0` 伪装 PASS；
- `failed=1` 伪装 PASS；
- 其他项目行、重复 `Probe.Tests` 行；
- PASS summary 对失败 XML、成功 summary 对 native exit 1、FAIL 行对 native exit 0；
- XML 缺失败 case、缺断言文本；
- raw 日志缺 case、缺断言、完全缺失；
- XML 缺失、畸形、节点数与 total 不一致。

这些负例使用同一套实际断言函数，证明修复不会因“只改 regex”或“缺日志算 PASS”而假绿。

## 3. 修复后完整执行

命令同 §1，`-KeepWorkspace`：

- 最终 stdout：`build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/repro/fixed-r4.stdout.log`
  （11,548 bytes，SHA-256 `3A4A1B3037EE9A76CB01D953C457825F84CD4F49C9DF6D6FE190354CA1F21762`）
- 最终 stderr：`.../repro/fixed-r4.stderr.log`
  （0 bytes，SHA-256 `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`）
- 原生退出：`0`
- 最终 workspace：`build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/temp/VerifyV5Diagnostics-20261102-013543-78db59f2/`
- 结果：**125/125 checks PASS**。

逐场景断言数（同一 fixture 会重复运行，计数不相加为独立产品覆盖）：

| 场景 | 断言数 | 结果 | runner | native exit | total/failed/skipped | XML counts |
|---|---:|---|---|---:|---|---|
| S1 | 12 | PASS | `dotnet test (MTP)` | 1 | 2/1/0 FAIL | T=2 P=1 F=1 S=0 NR=0 E=0 |
| S2 | 6 | PASS | `dotnet test (MTP)` | 0 | 2/0/0 PASS | T=2 P=2 F=0 S=0 NR=0 E=0 |
| S3 | 10 | PASS | `in-process (direct)` | 1 | 2/1/0 FAIL | T=2 P=1 F=1 S=0 NR=0 E=0 |
| S4 | 5 | PASS | `in-process (direct)` | 0 | 2/0/0 PASS | T=2 P=2 F=0 S=0 NR=0 E=0 |
| S5 | 6 | PASS | `dotnet test (MTP)` | 1 | 0/0/0 FAIL | T=0 P=0 F=0 S=0 NR=0 E=0 |
| S6 | 9 | PASS | `dotnet test (MTP)` | 0 | 2/0/0 PASS | T=2 P=2 F=0 S=0 NR=0 E=0 |
| S7 | 8 | PASS | `dotnet test (MTP)` | 1 | 2/1/0 FAIL | T=2 P=1 F=1 S=0 NR=0 E=0 |
| S8 | 17 | PASS | 单元/真实子进程 | 3（真实 exit3 正例） | 2 source roots、两模式、异常 | 不适用 |
| TOOL_FIXTURE | 22 | PASS | 断言负例 | 多种合成 | 多种合成 | 合成 |
| S9 | 9 | PASS | `dotnet test (MTP)` | 0 | 2/0/0 PASS | T=2 P=2 F=0 S=0 NR=0 E=0 |
| S10 | 9 | PASS | `dotnet test (MTP)` | 1 | 2/1/0 FAIL | T=2 P=1 F=1 S=0 NR=0 E=0 |
| S11 | 10 | PASS | 单元/故障注入 | 不适用 | snapshot blind：0 collected | 不适用 |
| run identity | 2 | PASS | `run-info.txt` | 不适用 | HEAD 匹配 | 不适用 |

S8 的实际 17 项包含：生产脚本 AST 可解析、4 个 helper 提取、2 个 source root 同名文件隔离（2 collected、各自内容、SHA 匹配）、dotnetTest/direct 分目录、`Invoke-Captured` 异常 partial+exception 且 exit `$null`、真实 `cmd /c exit 3` 返回 3。S11 实际 10 项包含：3 helper 提取、purge 失败、snapshot 错误、0 collected、2 个 stale 记录、旧/新文件均未收集、`collection-errors.log` 记录。

S1/S5/S7 关键原始证据（最终 workspace 内）：

| 文件 | bytes | SHA-256 |
|---|---:|---|
| `logs-s1/summary.log` | 4,240 | `991FAB7A57C8ADB332072B223341DAB47EB26B3B04A6B80FCA74FDFC0348D7B0` |
| `logs-s1/dotnetTest-xml/Probe.Tests/dotnet-test-results.xml` | 2,844 | `007BDCB9CD24080F6822F7CE5F0209456E1704BE51C56961B4EDD36928226DF8` |
| `logs-s1/dotnetTest-xml/Probe.Tests/Probe.Tests_net8.0_x64.log` | 3,420 | `4C22726E169A316AA5C23E68AFE12B6704307DC7ED3933DFC0E72B8C506B2E1E` |
| `logs-s5/summary.log` | 3,844 | `2A0B2BA360E3AB747D7887804143092592B4789A125B03379E4B4841966BF12C` |
| `logs-s5/dotnetTest-xml/Probe.Tests/dotnet-test-results.xml` | 940 | `FFF4FD6D21DC134F40783717D584CE55B7A0B31A810172C9CE16D33919B6C69F` |
| `logs-s5/dotnetTest-xml/Probe.Tests/Probe.Tests_net8.0_x64.log` | 2,328 | `1C53221FDBA89CDA72720744C602E88B7E38783C72EA6F2A97304789E0CFEC8C` |
| `logs-s7/summary.log` | 4,240 | `D97B5506F1898945375F053DBD1EA0910D8650EF30120F6205E1F1AACC9FA4AF` |
| `logs-s7/dotnetTest-xml/Probe.Tests/dotnet-test-results.xml` | 2,844 | `8A6E0C2C5C85B912D41EFC0DB7B35C02F73E12F11208D24434147338FED58D06` |
| `logs-s7/dotnetTest-xml/Probe.Tests/Probe.Tests_net8.0_x64.log` | 3,418 | `7D5C072BA34332439BA1BB3B7A8101ED4705C3A11F82B650B113CCB2631565B7` |

其余场景的 summary/XML/raw SHA 全部列在 `build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/evidence-manifest.csv`，逐文件包含长度与 SHA-256。

## 4. 定向回归（本改动相关的已接受自检）

全部在 PS 5.1 下实际执行，均 native exit 0；它们是工具边界矩阵，不是 14 工程全量。

| 已接受矩阵 | 命令（工作目录为本树） | 结果 | 证据 |
|---|---|---:|---|
| dotnetTest native XML gate | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-dotnettest-results.ps1 -DotnetPath D:/Unity/dotnet/dotnet.exe -EvidenceDir build/<GUID>/regression/dotnettest` | **61/61**，exit 0 | `regression/dotnettest/results.json` |
| captured exit / `Invoke-Captured` | `powershell ... -File tools/ci/test-v5-captured-exit.ps1 -EvidenceDir build/<GUID>/regression/captured` | **51/51**，exit 0 | `regression/captured/results.json` |
| direct XML gate | `powershell ... -File tools/ci/test-v5-direct-results.ps1` | **19/19**，exit 0 | `regression/direct-results/results.json` |
| native exit/metadata helper | `powershell ... -File tools/ci/test-v5-native-exit.ps1` | **24/24**，exit 0 | `regression/native-exit/results.json` |

说明：`test-v5-direct-results.ps1` 与 `test-v5-native-exit.ps1` 把证据写到其脚本固定的 `artifacts/` 下；本次运行后已把本次新建的 `direct-results-selftest-*`、`native-exit-*` 证据移入上述 GUID 目录，未触碰其他案卷。`test-v5-dotnettest-results.ps1` 的 live fixture 也移入 `regression/dotnettest-live-fixtures/`。矩阵 JSON 中可能保留创建时路径字段；文件本体、哈希与计数不受移动影响。

回归输出 SHA：

| 文件 | bytes | SHA-256 |
|---|---:|---|
| `regression/dotnettest.stdout.log` | 8,068 | `E1896C1075D71E3B928A38A0C88CDCB8906A504A8EFF1B58B975F074D7AF5B93` |
| `regression/dotnettest/results.json` | 45,793 | `1241E92CD068372A42A6E07642706DE8D0CB35D11D048567E321B20157BDFE75` |
| `regression/captured.stdout.log` | 10,306 | `073F76CFFA4D5C6FF48E22C545534CECB890079C89270E6E903DD6B03A4383FC` |
| `regression/captured/results.json` | 44,086 | `FBD9A433BB316C64D09B00299D89742FBD76484840F421E50E442C5AEED99A70` |
| `regression/direct.stdout.log` | 2,722 | `093E626677E991A4E1070F760DF6FE4464211021126E1EB3EC030B85DD527F74` |
| `regression/direct-results/results.json` | 12,971 | `47E287F9E994CBC0DC5CAD16FCE1C0A362C9FACF2819D03C43C0FF537A26BB69` |
| `regression/native.stdout.log` | 3,318 | `4487F0096A4DD303D255E6EC8FB1FDB0EA04982ECD15DA7451E3B53078870AC7` |
| `regression/native-exit/results.json` | 6,139 | `25391C0EFF2AC04F75FF19B127F9B619E471A0544539B1FA2C0E2E623B7262F6` |

## 5. 证据、可运行补验与封存

- 原始失败：`build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/repro/original.stdout.log` + `original.stderr.log`，原始 workspace 保留。
- 最终通过：`.../repro/fixed-r4.stdout.log` + `fixed-r4.stderr.log`，最终 workspace 保留。
- 版本汇总 manifest：`build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/evidence-manifest.csv`（路径、bytes、SHA-256；不含 manifest 自身；不包含 NuGet 缓存/CLI home）。
- 报告副本：`build/.../report/V5-CI-DIAGNOSTICS-SELFTEST-DELIVERY-20261102.md`（与本文同字节；manifest 记录其 SHA-256）。
- 复跑命令：

```powershell
$env:TEMP='E:/majiang-review/diagnostics-selftest-ds-20261102-0113/build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/temp'
$env:TMP=$env:TEMP
$env:DOTNET_CLI_HOME="$env:TEMP/dotnet-cli-home"
$env:NUGET_PACKAGES='E:/majiang-review/diagnostics-selftest-ds-20261102-0113/build/9f3a7c2e-5b1d-4e8a-9c6f-2d4b8a1e7c30/nuget-packages'
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/tests/VerifyV5Diagnostics.Tests.ps1 `
  -DotnetPath D:/Unity/dotnet/dotnet.exe -KeepWorkspace
```

预期：`VerifyV5Diagnostics: PASS (125/125 checks)`，native exit 0。若生产合同再次漂移，新断言应明确失败，不得通过放宽正则或删除断言变绿。

## 6. 明确未覆盖 / 不宣称

1. 未运行当前头远端 CI、未下载/重跑 `41329645233` / `41329638524`；既有失败历史保持。
2. 未运行 14 工程全量 dotnetTest、PG、Unity、Player、Android、设备、随机整场。
3. 非 Windows 分支未实测；本交付只证明 Windows PowerShell 5.1 + 本机 dotnet 8.0.426。
4. 本交付是 TOOL_FIXTURE/自检修复，不是产品测试全绿；S1/S3/S5/S7/S10 的受控失败是预期负例。
5. 未 push、未建/改 PR、未 approve、未 record MR、未 merge/deploy、未改 scheduler/`complete.flag`；G2–G5 未批准，V4 仍为生产。
6. 历史 `39970580589` framework 日志缺失仍 BLOCKED；本任务未声称其已修复。
7. 当前 accepted runner 的 raw 日志位于 `dotnetTest-xml/<project>/`，不再写入项目 `TestResults`；本自检不制造“应有但实际没有”的旧路径日志，仍保留 stale/purge/独占锁/快照盲区断言。

## 7. 提交与工作树约束

- 只暂存/提交 §0 的两个授权文件；`git diff --cached --check`、`git diff --check` 必须为 0。
- 生成物（`build/`、`artifacts/`、日志、NuGet 缓存、manifest）全部 ignored，不提交。
- 未修改 workflow、生产 verifier/helper、业务/.NET、Unity、scheduler、其他线程/树/锁；未杀他人进程。
- 提交后逐文件审计暂存区、diff-check、tracked clean；commit SHA 与 report SHA 在交付返回中报告。
