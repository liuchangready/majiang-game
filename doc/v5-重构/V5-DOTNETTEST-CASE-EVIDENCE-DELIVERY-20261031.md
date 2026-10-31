# V5 `dotnet test` 逐例原生证据交付（D-A68-02 工具修复）

- 日期：2026-10-31（Asia/Shanghai）
- 工作树：`D:/majiang-wt/dotnettest-results-ds-20261031`
- 分支：`wt/v5-dotnettest-results-ds-20261031`
- HEAD：`355bf8750d7ea15fc586b87920ebfca2bfb72122`
- 基线：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（accepted `origin/v5`）；`git merge-base --is-ancestor` 对基线与 `355bf875` 均为 0
- 源码树：`dotnet/`、`tools/`、`unity/`、`.github/` 在 `7fe7dbf3e37aefb437132d3584cb2ff09cff6af5` 与 `355bf875` 之间无差异；`355bf875` 仅新增调用方 CI 审计/报告
- 任务性质：已记录 **D-A68-02（MAJOR）** 的实际工具修复与本地验证；不是再审计旧 CI，也不是 PR20/组件/阶段验收
- 范围：仅本工作树实现/测试/提交；不 push、不 PR/MR、不 merge/deploy、不 approve、不 dispatch/rerun/cancel CI、不调用或委派 agent、不启动 Unity、不改共享插件或机器信任

> **结论先行：** 本地已实现并验证真实 `dotnet test` 的 xunit.v3 4.0.2 / Microsoft.Testing.Platform 逐例原生 XML 捕获与生产门禁；`dotnetTest`-only 不再回退 direct，meta/summary 记录真实 runner、命令、head、产物路径、SHA-256 与精确 counts。历史 CI 的 direct 结论仍只属于 direct；**远端 CI 的 `dotnetTest` 仍是 NOT_RUN，D-A68-02 的远端证据口径没有被本轮关闭**。

---

## 1. 本轮结论与边界

| 项目 | 状态 | 说明 |
|---|---|---|
| D-A68-02 本地工具修复 | **PASS（本地）** | 真实 `dotnet test` 原生逐例 XML、生产 gate、负例矩阵、真实正/负 live 探针均已落地 |
| 本地 `dotnetTest` 两工程抽样 | **PASS** | TableService 180/180、Architecture 132/132；native XML 由本次 `dotnet test` 产生并被 gate 验证 |
| 本地 PG full 14 工程 | **PASS（本地）** | 真实独立 PostgreSQL cluster；`dotnetTest` 统一入口；14/2,732、0 fail/skip/notRun/errors；cleanup PASS，见 §5.6.2 |
| 历史 CI（7fe，41181934433 / 41181937456） | **PASS（仅 historical direct）** | 两 run 仍是 direct runner；`direct-results.xml` 不是 dotnetTest XML |
| 远端 CI `dotnetTest` | **NOT_RUN** | 本轮未 dispatch/rerun/cancel；后续调用方验收后才触发新远端证据 |
| 全量历史 whitespace | **仍 FAIL（exit 2，1,573 条）** | D-A68-03 未修；本轮只保证 delta diffcheck clean |
| PR20 / G2–G5 | **NOT_GIVEN / 未批准** | 本报告不批准任何阶段或 PR |
| V4 生产、classified 181、Android/iOS/真机跨网 | **不变** | V4 唯一生产；classified 181 未覆盖；Android FAIL / iOS BLOCKED / 真机跨物理网 NOT_RUN |

**不把 local dotnetTest 冒充 remote dotnetTest；不把 direct XML 改名为 dotnetTest；不根据 console 生成假 XML；不把历史 direct 运行改称 dotnetTest。**

---

## 2. 旧缺口与参数支持的实际核验

### 2.1 旧缺口

- 7fe 的两个 CI run 都是 `runner mode: direct`，14 个 meta 均为 `in-process (direct)`，命令是 `dotnet.exe <test.dll> ... -result-xml ...`；artifact 没有 `.trx`、没有 `dotnetTest-*` 日志，也没有 `dotnet test` 命令。
- 因此历史 `direct-results.xml` 只能证明 direct 逐例 XML，不能证明 `dotnet test`。
- 冻结本地 `dotnet test` TableService 180/180 也只有 aggregate console/framework 日志，没有逐例 XML/TRX；这正是 D-A68-02 的工具缺口。

### 2.2 参数不是猜的：三层实际证据

1. **`dotnet test --help`（SDK 8.0.426）** 明确 `[[--] <additional arguments>...]`，即 `--` 之后的参数传给测试应用。
2. **direct 模式的 xUnit.net v3 In-Process Runner v4.0.2 help** 列出 `-result-xml <filename>`、`-result-trx <filename>` 等；这是 direct 模式的单横线参数，不能直接用于 MTP 模式。
3. **MTP 模式的实际 help**：用 `dotnet test ... -- --help` 触发 MTP 启动后，测试应用在 `TestResults\<assembly>_<tfm>_<arch>.log` 中打印的 help 明确列出：
   - `--report-xunit-xml`
   - `--report-xunit-xml-filename`
   - `--results-directory`
   - 以及 `--report-xunit-trx` / `--report-xunit-trx-filename`

实际探针（TableService，Release）：

```powershell
D:\Unity\dotnet\dotnet.exe test dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj `
  -c Release --nologo --no-build -- `
  --report-xunit-xml --report-xunit-xml-filename dotnet-test-results.xml `
  --results-directory <unique-absolute-results-dir>
```

结果：退出 0，生成 `dotnet-test-results.xml`；XML 根为 `assemblies schema-version="3"`，`assembly/@test-framework="xUnit.net v3 4.0.2+0c7aa4991f"`，`total=180 passed=180 failed=0 skipped=0 errors=0 not-run=0`，180 个 `<test>` 节点。该文件由本次 `dotnet test` 调用原生生成，不是 console 解析或手写。

> 说明：`dotnet test` 的 MTP MSBuild 桥接把附加参数按空格拼接；若 `--results-directory` 路径含空格，测试应用会把它拆成多个参数并在写文件前失败。生产实现因此在 Windows 上仅对该**命令行参数**使用目录的 8.3 短别名，gate/meta 仍使用真实长路径；无短别名时 fail closed，不会伪造报告。

---

## 3. 实现

### 3.1 `tools/verify-v5.ps1`

新增/调整：

1. `dotnetTest` 分支给真实调用追加原生报告参数：
   - `--report-xunit-xml`
   - `--report-xunit-xml-filename dotnet-test-results.xml`
   - `--results-directory <LogDir>\dotnetTest-xml\<project>`
2. 每个 run/project 的 results 目录在调用前递归清除并重建；清除失败则项目 FAIL，且**不启动** `dotnet test`，避免旧文件冒充本次证据。
3. `LogDir` 统一转绝对路径（`dotnet test` 子进程按自己的 cwd 解析相对 results 目录）。
4. 新增 `Get-DotnetTestResultsArgument`：仅当路径含空白且 Windows 8.3 别名可用时返回无空白别名；否则返回原路径并让调用 fail closed。
5. 新增 `Get-DotnetTestConsoleCounts`：解析 `dotnet test`/MTP 的 VSTest 风格汇总行（英文与 zh-CN），console 只作 cross-check，不作为逐例来源。
6. 新增生产 gate `Test-DotnetTestInvocationEvidence`，严格校验：
   - XML 必须存在、非空、可解析，根为 `assemblies`，且恰有一个 `<assembly>`；
   - `schema-version`、`xUnit.net v3` framework、assembly leaf 与**精确 assembly 文件路径**（rooted，Windows 大小写不敏感）匹配；
   - 逐例 `<test>` 节点存在，`@id` 非空且全局唯一；display name **不要求唯一**；
   - assembly 的 total/passed/failed/skipped/errors/not-run 与节点、collection 聚合互相一致；
   - `total>0`、`passed==total`、failed/errors/skipped/not-run 全为 0；
   - 文件位于本次 run/project 的 results 目录内、文件名精确、`LastWriteTimeUtc` 不早于本次调用（5 秒时钟余量）；
   - native exit 精确为 0；console 的 Total/Passed/Failed/Skipped 与 native 逐项一致；
   - 缺失/损坏/截断/过期/越界/错误 assembly/错误 framework/native 非零而 XML 绿色/console 缺失或不一致，全部 fail closed。
7. `dotnetTest` 分支一旦产生 console summary 或 native XML，就以 native gate 决定该行；gate FAIL 直接 FAIL，**不再 fallback direct**。`dotnetTest`-only 模式在任何缺失情况下都 FAIL，不 fallback。
8. `auto` 的既有 fallback 仅在“既无 console summary、也无 native XML、且无 build/restore error”时保留；fallback 后 runner 仍写 `in-process (direct)`，meta/summary 明确写“该行 counts 来自 direct，不是 dotnetTest 证据”。
9. meta 新增 `dotnetTest xml` 路径、SHA-256、gate 结果与精确 counts（total/passed/failed/skipped/errors/not-run/nodes/framework）；summary 增加 `runner mode`、`dotnetTest xml`、`dotnetTest counts` 与独立 evidence root 说明。
10. 未修改任何测试 csproj/package/production 源码，未增加 skip，未削弱 direct 门禁。

### 3.2 `tools/ci/test-v5-dotnettest-results.ps1`（新增）

- 用 PowerShell AST 从 `tools/verify-v5.ps1` 提取**真实生产函数**：`Invoke-Captured`、`Get-DotnetTestConsoleCounts`、`Get-DotnetTestResultsArgument`、`Read-DirectResultXml`、`Test-DotnetTestInvocationEvidence`；测试不复制 gate 算法。
- 合成 XML 仅用于 gate 负例/正例，明确不是真实 `dotnet test` 产物。
- 负例矩阵覆盖：missing/empty/malformed/truncated、zero、summary-only、错误 assembly leaf、同 leaf 错误路径、错误 framework、assembly/collection 聚合不一致、duplicate native IDs、stale、results 目录越界、XML 越界、路径 escape、错误文件名、fail/skip/notRun/errors、native 非零但 XML 绿色、native exit unknown、console 缺失/总数/失败/跳过不一致、results 参数含空格。
- 正例包含：绿色 XML；含**两个完全相同 display name** 的合法 case（gate 必须通过，证明 display name 不被当作 native ID）；含空格的结果路径参数。
- **真实 live 探针**（不是 fixture 冒充）：
  - 在 ignored `build/` 下动态创建两个独占 xunit.v3 4.0.2/MTP 工程；
  - 正向工程 3 个 case（其中两个同名 display name）真实 `dotnet test` 退出 0，原生 XML 3/3 Pass，production gate PASS；
  - 受控失败工程 1 个失败 case 真实 `dotnet test` 退出 1，原生 XML 1 个 Fail 节点，production gate 拒绝；首失败为 `Probe.Negative.FailingTests.ControlledFailure`。
- 记录 `results.json`（cases/passed/failed、production script SHA-256、dotnet 版本、每个 case 的 expected/actual/reason/native exit/first failure）。

### 3.3 PG wrappers

- `tools/ci/verify-v5-table-postgres.ps1`：新增明确 `[ValidateSet('auto','dotnetTest','direct')] $RunnerMode = 'direct'`；`-RunUnifiedVerification` 原先把 `-RunnerMode direct` 硬编码给 `verify-v5-postgres.ps1`，现改为传递 `$RunnerMode`。默认仍是 `direct`。
- `tools/ci/verify-v5-postgres.ps1`：已有 `RunnerMode` 参数并原样传给 `tools/verify-v5.ps1`；本轮把默认值从 `auto` 改为 `direct`，与当前 CI/验收默认一致。调用方仍可显式传 `dotnetTest`/`auto`。
- PG 端口选择、loopback bind 预检、ownership marker、env 快照/恢复、crash/restart/reader、backup/empty-DB restore、outbox/receipt、cleanup 全部保持原语义，没有放宽或跳过。
- 表侧 stage 3 的 direct xUnit runner 与 42/42、targeted 13/13 仍是 **direct**，不是 dotnetTest；dotnetTest 只用于 unified 14 工程 leg。报告不会把两者混为一谈。

---

## 4. 验证命令

```powershell
# 0. 身份/ancestry（实际执行）
git status --porcelain=v1 -b
git rev-parse HEAD
git merge-base --is-ancestor 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7 HEAD
git merge-base --is-ancestor 355bf8750d7ea15fc586b87920ebfca2bfb72122 HEAD

# 1. fresh source Release build
D:\Unity\dotnet\dotnet.exe build dotnet/Mahjong.sln -c Release --nologo

# 2. 既有回归（只读复用，不修改）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-direct-results.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-captured-exit.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-native-exit.ps1

# 3. 新 gate/负例/真实 live 探针
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-dotnettest-results.ps1

# 4. 统一入口 dotnetTest，两个真实非 PG 工程（scoped manifest）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 `
  -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe -RunnerMode dotnetTest `
  -ManifestPath <ignored>/manifest-dotnettest-two.json -NoProjectDiscovery -LogDir <fresh>

# 5. 真实 PG：table wrapper 传递 dotnetTest 给 lobby/unified wrapper，再跑 14 工程
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -RunnerMode dotnetTest -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -Port 55592 -LobbyPort 55593 -Root <fresh-artifacts-root> -LogDir <fresh-artifacts-log> `
  -PgZipPath D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip `
  -RunUnifiedVerification
```

未执行：任何 CI dispatch/rerun/cancel、push、PR/MR/merge/deploy、approve、Unity、设备/真机、跨网。

---

## 5. 验证结果

### 5.1 Fresh source Release build

| 项 | 结果 |
|---|---|
| 命令 | `D:\Unity\dotnet\dotnet.exe build dotnet/Mahjong.sln -c Release --nologo` |
| native exit | **0** |
| warnings / errors | **0 / 0** |
| 用时 | 28.6 s（wall） |
| 日志 | `build/fresh-release-build-20261031.log` |
| SHA-256 | `11DA8D3DB7A42F94ED5D76406DA8D89D74B4B3007B4143B9C5F5F4D127A98649` |

### 5.2 既有回归（未修改脚本）

| 脚本 | 结果 | native exit | 证据 |
|---|---|---|---|
| `test-v5-direct-results.ps1` | **19/19** cases behaved as expected | 0 | `build/regression-20261031/test-v5-direct-results.log`，SHA-256 `4D37EF0BA7D60DBFA32D4BD898C958BD51268B51620A86D7A98C149BC867FD3C` |
| `test-v5-captured-exit.ps1` | **51/51** passed | 0 | 同上目录 `test-v5-captured-exit.log`，SHA-256 `B78FA935763F0A14E362A3D7A41597DFC5D0E1B1BBADCD00FB389F39BF429225` |
| `test-v5-native-exit.ps1` | **6/6** cases behaved as expected | 0 | 同上目录 `test-v5-native-exit.log`，SHA-256 `7EFCEE63E4CB2F9ADBB4B0A2C91490D6FE9B6A129D42420AADAC19EB74D81512` |

### 5.3 新 `dotnetTest` gate/负例/真实 live 探针

- 最终结果：**36/36** cases behaved as expected；0 failed（`results.json`: cases=36, passed=36, failed=0）。
- 组成：31 个 gate case（2 positive + 29 negative）、1 个 results 参数 helper case、2 个 console parser case、2 个真实 live dotnet test case。
- 合成 fixture 只作 gate 负例/正例，不冒充真实产物；真实 live 记录只有两个 live case。
- Live positive：真实 `dotnet test` 退出 **0**，native XML **3/3 Pass**，duplicate display name group=1，gate **PASS**（display name 未被误判为 native ID）。
- Live negative：真实 `dotnet test` 退出 **1**，native XML `failed=1`，gate **FAIL**；首失败 `Probe.Negative.FailingTests.ControlledFailure`。该结果证明不是旧 DLL/未编译被掩盖。
- 负例覆盖（实际全部拒绝）：missing/empty/malformed/truncated、zero、summary-only、wrong assembly leaf、wrong assembly path、wrong framework、assembly/collection aggregate mismatch、duplicate native IDs、stale、outside LogDir、outside results dir、path escape、unexpected filename、failed/skipped/not-run/errors、native nonzero + green XML、native exit unknown、missing console、console total/failed/skipped mismatch。
- 证据：`artifacts/dotnettest-results-selftest-398ee3a304a34887bee8cf53463ba638/results.json`，SHA-256 `2F0FB3F9F89223F26F59A58FBC33AAC353C01041ADDAD6B91AA5CC3685EE4AFC`；live fixture 根路径 `build/dotnettest-selftest-82b7d73e13834fe2afa2c0013a029d20/live-fixtures`。

### 5.4 统一入口 `RunnerMode dotnetTest`：TableService + Architecture

scoped manifest 仅列两个真实非 PG 测试工程（`build/manifest-dotnettest-two.json`，ignored），`-NoProjectDiscovery`；plugin copy 与 companion gate 仍执行。

| 工程 | 模式 | native total | passed | failed | skipped | errors | not-run | nodes | native XML SHA-256 | gate |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|
| `Mahjong.TableService.Tests` | dotnetTest/MTP | 180 | 180 | 0 | 0 | 0 | 0 | 180 | `0FC03FDAC2B50D638FB0C41C0021C93DCF01E6D4A7818A7F01FD384A3557001E` | PASS |
| `Mahjong.Architecture.Tests` | dotnetTest/MTP | 132 | 132 | 0 | 0 | 0 | 0 | 132 | `CED8DEBC7C41C88ED67F4133B2DB8FFB0EECA8C289023BCA7F82DC516A016DA4` | PASS |

- 统一入口 native exit **0**；`OVERALL: PASS`；plugin copy PASS；companion 4/4 PASS。
- 两个 meta 均记录 `runner used : dotnet test (MTP; native xunit.v3 XML)`、精确命令、exit 0、native XML 路径/SHA/gate/counts；命令含 `-- --report-xunit-xml --report-xunit-xml-filename dotnet-test-results.xml --results-directory ...`。
- 证据：`build/unified-dotnettest-two-9ba400e0/`（summary.log、runs/*.meta.txt、dotnetTest-xml/*、test-results/*）。
- 该 scoped manifest 只是抽样探针，不是默认 14 工程分母；不取代 §5.6。

### 5.5 无 PG 的 full 14 工程压力探针（非验收 full）

- 默认 manifest + discovery，`RunnerMode dotnetTest`，在**未设置 PG 环境**的本机直接跑 14 个测试工程。
- 结果：**14 工程 / 2,732 total**；2,658 passed / 62 failed / 12 skipped / 0 errors / 0 notRun；2,730 unique display names，2 个 duplicate-name group（Architecture 1、HTTPS Admission 1）。
- 失败的 3 个工程都是 PG 依赖：`Mahjong.TableService.DurableHost.Tests`（21 fail）、`Mahjong.Infrastructure.Tests`（41 fail）、`Mahjong.LobbyService.Tests`（12 skip）；不是代码断言被放宽，也不是 gate 漏检。
- 该 run 的首失败/首个 skip（仅用于证明 gate 捕获真实失败，不当作代码缺陷）：
  - `Mahjong.TableService.DurableHost.Tests`：`Mahjong.TableService.DurableHost.Tests.PostgresDurableHostRecoveryAnchorTests.ColdRecovery_TsumogiriOpening_FactSurvivesDifferentSecondSource`
  - `Mahjong.Infrastructure.Tests`：`Mahjong.Infrastructure.Tests.PostgresSupplementRegressionTests.EarlyWakePreparationDelay_CannotIssueScopeBeforeMinimumElapsed`
  - `Mahjong.LobbyService.Tests`：首个 skip `Mahjong.LobbyService.Tests.PostgresIntegrationTests.PostgresLobbyRoundTripWhenARealDatabaseIsConfigured`
- 该 run 的 native gate 如实把这些行判 FAIL；因此它**不是** full PASS，只用于证明 2,732 分母、同名合法 case 和真实失败/跳过会被捕获。权威 full 结果见 §5.6。
- 证据：`build/unified-dotnettest-full-11cb2ca0/`；`native-xml-summary.json` SHA-256 `06C612C072EE8F941A2CFC0DE805749A934126DEC6A306C382EEBFF0AF81B314`。

### 5.6.1 尝试 1（长 root）：测试全绿，cleanup 因 Windows 长路径失败

- 命令同 §4；root 为 `artifacts/pg-cluster-dotnettest-20261031-172729`，LogDir 为 `artifacts/dotnettest-pg-20261031-172729`。
- 全部测试/统一/companion stage 均 PASS：table 42/42、targeted 13/13、crash/restart/reader/backup、unified 14 工程/2,732/2,732；`environmentRestored=true`。
- wrapper 总 exit **1**，唯一失败是 `cleanup`：`Could not find a part of the path 'properties.sql'.`。这是 Windows 长路径/`pgAdmin 4` 子树删除问题（与 D-A68-02 无关），不是测试失败；本轮保留该 FAIL，不把它写成 PASS，也不删除/掩盖证据。
- 证据：`artifacts/dotnettest-pg-20261031-172729/wrapper-summary.json`（`exit=1`, `cleanup=FAIL`, `unified-verification=PASS`, `environmentRestored=true`）、`unified/summary.log`（`OVERALL: PASS`, 20 rows: 20 PASS）。
- 该尝试的 unified native XML 已生成并通过生产 gate；但因为 wrapper 总 exit 1，不作为最终 full PASS。

### 5.6.2 尝试 2（短 root）：全 stage + cleanup PASS

- 命令同 §4；为规避 Windows 长路径清理问题，root 改为短路径 `artifacts/pg2`，LogDir 为 `artifacts/dotnettest-pg2-20261031-175613`。
- 结果：wrapper native exit **0**，输出 `P52-TABLE-POSTGRES-PASS`；`environmentRestored=true`（16 个环境变量全部 restored）；wrapper 日志最后为 `ownership-verified cluster removed: ...\artifacts\pg2`；`wrapper-summary.json` 的 `stages` 均为 PASS（result PASS），无 cleanup FAIL。
- 端口：`port-preflight` 对 `127.0.0.1:55592` 实际 exclusive bind，`candidates=[55592=Bindable]`；`portMode=explicit`。
- 表侧真实 PG：`pg-start` native exit 0；PostgreSQL **16.10**，独立 data dir/port 55592；`xml-gating-selfcheck` 7 个负例全部拒绝；`tests` **42/42**（0 fail/skip/notRun）；`tests-targeted` `PostgresTableStoreTests` **13/13**；`crash-writer` **KILLED**（pid 35296，committed state=1 seq=2，killed before publication）；`postgres-crash-restart` native exit 0（postmaster start 09:56:48.302639Z → 09:58:40.802189Z，recovery lines=3）；`crash-reader` state=1 seq=2 terminal=True receipt=Applied cheatVersion=1；`backup-restore` `pg_dump`/`pg_restore` 到空库，restored events=2，recovery ownerEpoch=3。
- 统一 leg：`unified-verification` PASS，`unified/summary.log` 为 `OVERALL: PASS`（20 rows: **20 PASS**，0 FAIL/BLOCKED/NOT_RUN；companion 4/4 PASS）；14 个测试工程全部 `dotnet test (MTP)` 原生 XML，**2,732/2,732**，0 fail/skip/notRun/errors；2,732 unique native IDs；2,730 unique display names，2 个合法同名 group。
- 注：表侧 42/42 与 targeted 13/13 是表 wrapper 的 **direct** runner；只有 unified 14 工程是 **dotnetTest**。报告没有把 42/13 计入 dotnetTest 分母，也没有把 direct 结果改名。
- 证据路径：
  - `artifacts/dotnettest-pg2-20261031-175613/wrapper-summary.json`，SHA-256 `261F2FB5B6F17170F098AF329F6D7D2D8BD976FD92E454EBFD8E43C75E79D6AD`
  - `artifacts/dotnettest-pg2-20261031-175613/manifest.sha256.json`，SHA-256 `7AA561DC6D47041E98D2E04C5A80B4AA07DDE197841E589304E5E338FAB10CDE`（235 个文件条目）
  - `artifacts/dotnettest-pg2-20261031-175613/unified/summary.log`，SHA-256 `B28260A632978E3448EFDBFCDED9A5987FA521BDF5DF0D343AEEDCFBDA06E45D`
  - `artifacts/dotnettest-pg2-20261031-175613/unified/native-xml-summary.json`，SHA-256 `5D6553F1779AFB1D02C674544BFE8B60E10E1466C7066715EB9CD3562C508A7B`
  - wrapper stdout：`artifacts/dotnettest-pg2-20261031-175613.run.log`，SHA-256 `D13B5FB67699F7C54D5DA0FC459F11808CF680C3CB16A44393D3017D889F0743`
  - unified run identity：`artifacts/dotnettest-pg2-20261031-175613/unified/run-info.txt`，SHA-256 `2AF875BBA279295A40B5835D56F2D8600E3E60222CA6CDE330DE59663DE2ED0B`
- 14 个 dotnetTest 原生 XML 的 SHA-256：

| 工程 | total | native XML SHA-256 |
|---|---:|---|
| Mahjong.Ai.Tests | 444 | `6D328E04E8F5B9F05BF38FD61B739435EB4D5C8786B249F7C654C491BF309F59` |
| Mahjong.Architecture.Tests | 132 | `38CDC31B09A6D51A1E2142CFD6B83A08A1B32044C416EF133DC15E598E4E9E79` |
| Mahjong.Client.Tests | 182 | `9C1242363764EA510782DB424DD1D49AF45EF6C6317B40F67F9B96DC85E72B0F` |
| Mahjong.Client.Wss.Tests | 82 | `5DC7C79E8E7570BE19CA1372F7A6287B6AE5DD47EB6EC2D717C08DDAFFBDCEF8` |
| Mahjong.Contracts.Tests | 152 | `D4D686AD15D0C2B8241149C2615263E85B75D488323EC8DB2C2F4B53C4EB9FA7` |
| Mahjong.Domain.Tests | 529 | `9B1ED70A96403342542454C4C69E2CB15CE61033E2DF0BA0DA22829874A1FB85` |
| Mahjong.Infrastructure.Tests | 42 | `1AC9A168112D63786FE8983A866028FF0DFEA047F7F98D56880A9BEC461C5F73` |
| Mahjong.LobbyService.Tests | 89 | `9CA4103A3F90A4B3DA7A111D96E180FC3CA0CCB96A07A279CFF0BA27A16D9D94` |
| Mahjong.LocalHost.Tests | 124 | `522506E8301CD7E9C8965899BB0AD8DC56AF67D404E47BFEC58A90B7F4CCF4EA` |
| Mahjong.P5.4b.B1.HttpsAdmission.Tests | 54 | `ECF03E7AA5ACC929B286135803F915E197656452FD54D63866EAF8C8513755A2` |
| Mahjong.TableRuntime.Tests | 476 | `C84872136C4FAAEC4DE178714C4E6105A46CD8EF8CF75D88FDE10AF1ECF2C6C9` |
| Mahjong.TableService.DurableHost.Tests | 59 | `423ED990C513147710B1949370062AFBC759B0FBECF6058DD03F0781794FCE82` |
| Mahjong.TableService.Tests | 180 | `31F3827C4FF8857BAB200F0CB375A77D0CAAB94A69FAF25E76E0EEB1E9CBA025` |
| Mahjong.Unity.TableModel.Tests | 187 | `29620142763E16248F3FDD3D0A29A3C5C4DB4DB2D74BFA01C6EDC78E2CE51B03` |




---

## 6. 逐例总数、同名与首失败

### 6.1 PG full 的 14 工程逐例总数（dotnetTest/MTP native XML）

| 工程 | total | passed | failed | skipped | errors | not-run | nodes | unique display names | duplicate-name groups | 首失败 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| Mahjong.TableRuntime.Tests | 476 | 476 | 0 | 0 | 0 | 0 | 476 | 476 | 0 | — |
| Mahjong.Domain.Tests | 529 | 529 | 0 | 0 | 0 | 0 | 529 | 529 | 0 | — |
| Mahjong.Ai.Tests | 444 | 444 | 0 | 0 | 0 | 0 | 444 | 444 | 0 | — |
| Mahjong.Client.Tests | 182 | 182 | 0 | 0 | 0 | 0 | 182 | 182 | 0 | — |
| Mahjong.Unity.TableModel.Tests | 187 | 187 | 0 | 0 | 0 | 0 | 187 | 187 | 0 | — |
| Mahjong.Client.Wss.Tests | 82 | 82 | 0 | 0 | 0 | 0 | 82 | 82 | 0 | — |
| Mahjong.Architecture.Tests | 132 | 132 | 0 | 0 | 0 | 0 | 132 | 131 | 1 | — |
| Mahjong.TableService.DurableHost.Tests | 59 | 59 | 0 | 0 | 0 | 0 | 59 | 59 | 0 | — |
| Mahjong.P5.4b.B1.HttpsAdmission.Tests | 54 | 54 | 0 | 0 | 0 | 0 | 54 | 53 | 1 | — |
| Mahjong.LobbyService.Tests | 89 | 89 | 0 | 0 | 0 | 0 | 89 | 89 | 0 | — |
| Mahjong.Contracts.Tests | 152 | 152 | 0 | 0 | 0 | 0 | 152 | 152 | 0 | — |
| Mahjong.Infrastructure.Tests | 42 | 42 | 0 | 0 | 0 | 0 | 42 | 42 | 0 | — |
| Mahjong.TableService.Tests | 180 | 180 | 0 | 0 | 0 | 0 | 180 | 180 | 0 | — |
| Mahjong.LocalHost.Tests | 124 | 124 | 0 | 0 | 0 | 0 | 124 | 124 | 0 | — |
| **合计** | **2,732** | **2,732** | **0** | **0** | **0** | **0** | **2,732** | **2,730** | **2** | **无** |

- 14 个工程 / 2,732 total / 2,732 passed / 0 failed / 0 skipped / 0 errors / 0 notRun；2,732 个 native test ID 全部唯一（14 个 XML 汇总去重后仍为 2,732）。
- 2,730 unique display names；两个合法同名 group：Architecture（132 total/131 unique）和 HTTPS Admission（54 total/53 unique）。同名 display name 不是 native ID，gate 不要求其唯一，因此 2,730 vs 2,732 不会被误判。
- 首失败：**无**（14/14 全绿）。受控真实失败探针的首失败是 `Probe.Negative.FailingTests.ControlledFailure`，见 §5.3；无 PG 压力探针的首失败/首个 skip 见 §5.5。
- 20 个 project rows = 14 test + 6 build-only helper；helper 不计测试、不计分母（D-A68-04 口径）。



---

## 7. 严格口径：历史 direct、本轮 local dotnetTest、未来 remote dotnetTest

| 来源 | runner | 是否本轮 | 逐例产物 | 结论适用范围 |
|---|---|---|---|---|
| 历史 CI 41181934433 / 41181937456 | direct | 否 | `direct-results.xml` | 仅 historical direct 的 14 工程/2,732 |
| 本轮 scoped local | `dotnet test` + MTP | 是 | `dotnetTest-xml/*/dotnet-test-results.xml` | TableService 180、Architecture 132 |
| 本轮 PG full local | `dotnet test` + MTP | 是 | 14 个 `dotnetTest-xml/*/dotnet-test-results.xml` | 本地 14 工程/2,732（PG 环境） |
| 未来 remote CI `dotnetTest` | `dotnet test` + MTP | **否 / NOT_RUN** | 无 | **不能由本轮本地结果外推** |

- 历史 CI 的 `direct-results.xml` **不是** dotnetTest XML；本轮没有把它改名，也没有用 direct 冒充 dotnetTest。
- 本轮没有 push、没有 dispatch/rerun/cancel CI；没有产生远端 dotnetTest artifact。
- 默认 `RunnerMode direct` 仍存在于 PG wrapper 的默认值；这是为了保持当前 CI/验收行为，不等于 D-A68-02 已在远端关闭。

---

## 8. Known defects / 限制（一次性列出）

1. **D-A68-02 远端证据仍 OPEN**：本轮只交付本地工具修复与本地证据；远端 CI 仍是 direct，dotnetTest NOT_RUN。后续调用方验收后才应触发新的远端 dotnetTest step/job。
2. **D-A68-03 历史 whitespace 仍存在**：`git diff --check 8fd7c6cb..7fe7dbf` 仍为 1,573 条；本轮不重写历史 patch/evidence，只保证本轮 delta clean。
3. **D-A68-04 口径**：20 个 project rows = 14 test + 6 build-only helper；2,732 分母只算 14 个测试工程，helper 不计测试。
4. **D-A68-05 / D-A68-08**：companion 仅 4/11、历史 blob pins NOT_RUN、artifact 树不同构等旧口径未在本轮处理。
5. **MTP 参数空格限制**：`dotnet test` 的 MTP MSBuild 桥接不自动给 `--results-directory` 加引号；Windows 使用 8.3 短别名规避，非 Windows/无短别名时 fail closed（不产生假报告）。这是工具边界，不是测试结果。
6. **PG full 仅本机 Windows + PostgreSQL 16.10**：crash/restart/backup 证据是本地真实 cluster；不是远端 CI、不是生产数据库、不是跨机器容灾。
7. **Unity/Player/IL2CPP/Android/iOS/真实 HTTPS/WSS/跨物理网/真机**：NOT_RUN，未由本轮覆盖。
8. **G2–G5 未批准；PR20 未验收**：本报告是调用方验收输入，不是批准。
9. **未修改生产/测试断言/package/workflow/sln/manifest/Domain/Unity/B1 gate**：也没有新增 skip 或降低 direct 门禁。
10. **未处理 B2/P45/P4.5**：B2 的 TableService 生产与 terminal tests/worker 工具不在本任务范围；P4.5 第 15 轮按协议等待人工裁决，未返修/复制/retarget；helper/audit 的 ledger reviewer-role 限制未绕过。

---

## 9. 变更文件、scope 与 SHA-256

### 9.1 tracked 变更（expectedFiles）

| 文件 | 变更 |
|---|---|
| `tools/verify-v5.ps1` | dotnetTest 原生 XML 参数、gate、meta/summary、短路径、absolute LogDir |
| `tools/ci/test-v5-dotnettest-results.ps1` | 新增 36-case gate/负例/真实 live 探针 |
| `tools/ci/verify-v5-table-postgres.ps1` | 新增 RunnerMode（默认 direct）并传递 |
| `tools/ci/verify-v5-postgres.ps1` | 默认 direct；既有传递保持不变 |
| `doc/v5-重构/V5-DOTNETTEST-CASE-EVIDENCE-DELIVERY-20261031.md` | 本报告 |

未修改：`dotnet/tests/**` 业务断言、`dotnet/src/**` 生产、`*.csproj`/package、`dotnet/Mahjong.sln`、`tools/v5-verify.manifest.json`、workflow、Unity/Resources/Burst、B1 gate、其它 worktree。

### 9.2 tracked 文件 SHA-256（提交前工作树内容）

| 文件 | SHA-256 |
|---|---|
| `tools/verify-v5.ps1` | `15AC9CEB6005E49295B57FC7E85C7B927DC1740032A2FF7185C73466655ACDE0` |
| `tools/ci/test-v5-dotnettest-results.ps1` | `609543E12AD6DFDB159676A85797B73F0C1682B8CEFAF23DE4A223B3383110CD` |
| `tools/ci/verify-v5-table-postgres.ps1` | `85B67C3836CF82ACA84565AC92119F932DB9D17C79A471D8C0B46A031B43B965` |
| `tools/ci/verify-v5-postgres.ps1` | `FCB3B35387FED80703A098000053EDF70BFDD8A1FB6E7CC456A8DE8E0E270D10` |
| 本报告 `V5-DOTNETTEST-CASE-EVIDENCE-DELIVERY-20261031.md` | 不在文内自引用；最终提交 SHA/文件 SHA 由提交后输出 |

### 9.3 证据 SHA-256 manifest

- 完整 evidence bundle manifest 由 PG wrapper 生成：`artifacts/dotnettest-pg2-20261031-175613/manifest.sha256.json`，SHA-256 `7AA561DC6D47041E98D2E04C5A80B4AA07DDE197841E589304E5E338FAB10CDE`（235 个条目，覆盖 `build-*`、`pg-*`、`psql-*`、`tests*`、`crash-*`、`restore-*`、`table_store.dump`、`wrapper-summary.json` 等）。
- native XML 逐例 summary：`artifacts/dotnettest-pg2-20261031-175613/unified/native-xml-summary.json`，SHA-256 `5D6553F1779AFB1D02C674544BFE8B60E10E1466C7066715EB9CD3562C508A7B`。
- 14 个 native XML 的 SHA-256 见 §5.6.2；原始 stdout/stderr/framework logs、meta、companion 证据保留在 PG LogDir 与 `unified/`；不提交 DLL、秘密、连接串、key、种子或 private saves。

### 9.4 Git/提交

- 基线：`8fd7c6cb...`；冻结审计头：`355bf875...`；`git merge-base --is-ancestor` 两者均为 0。
- 源码树身份：`git diff --quiet 7fe7dbf3e37aefb437132d3584cb2ff09cff6af5 HEAD -- dotnet tools unity .github` 为 exit 0；`355bf875` 只新增两份调用方 CI 文档，无源码差异。
- `git diff --check`（本轮工作树）exit **0**。
- 提交前 `git status --porcelain=v1` 只有 expectedFiles：3 个 M + 2 个 ??（新脚本、本报告）；提交后必须 clean。
- 最小单提交，message：`tools(v5): capture real dotnet test per-case evidence`。最终 commit SHA 由提交后输出，不在文内自引用。
- 提交后应复核 `git status --porcelain=v1 -b` clean，且 `git diff --check 355bf875..HEAD` exit **0**。历史 `git diff --check 8fd7c6cb..7fe7dbf` 仍是 1,573 条，未修（D-A68-03）。
- 本轮未 push、未建/改 PR/MR、未 merge、未 deploy、未 approve、未 dispatch/rerun/cancel CI。

---

## 10. 最终判定

1. 本地已把 `dotnet test` 的真实逐例原生 XML 纳入生产 gate；缺失/损坏/过期/错误 assembly/路径逃逸/native 非零/console 不一致均 fail closed，`dotnetTest`-only 不回退 direct。
2. 真实 TableService 180/180 与 Architecture 132/132 已由本轮 `dotnet test` 原生 XML 通过 gate；真实 PG full 的 14 工程/2,732 结果见 §5.6。
3. 同名 display name（2,730 unique / 2,732 total）被正确接受；native ID 唯一性单独校验，不把 display name 当 ID。
4. 历史 CI direct、本轮 local dotnetTest、未来 remote dotnetTest 已严格区分；远端 dotnetTest 仍 NOT_RUN，D-A68-02 远端口径未关闭。
5. 默认 direct 仍存在；本报告不因本地 dotnetTest 绿而宣称 CI D02 已关闭，也不宣称 PR20/G2–G5/Unity/Android/iOS/真机跨网通过。
6. 本报告与证据供调用方独立验收；最终 PR20/publication 由调用方独占，本任务不 push/PR/merge/approve。
