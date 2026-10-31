# V5 `dotnet test` 逐例原生证据交付（D-A68-02 工具修复）

- 日期：2026-10-31（Asia/Shanghai）
- 工作树：`D:/majiang-wt/dotnettest-results-ds-20261031`
- 分支：`wt/v5-dotnettest-results-ds-20261031`
- Code fix HEAD：`6a0876d980c59cb5f4b69b2e17ad471423e77c67`（round 2 code fix；round 1 reviewed delivery `b4c04d31a85256e1d233e76995bd23cf9172d348`；docs commit 由提交后输出）
- 基线：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（accepted `origin/v5`）；`git merge-base --is-ancestor` 对基线与 `355bf875` 均为 0
- Round 2 结论：**REWORK 完成**；唯一 rework 缺陷组 **D-DNT-R1-01（MAJOR）** 已修复并重验；无其他已知 rework 缺陷留到下一轮
- 源码树：`dotnet/`、`tools/`、`unity/`、`.github/` 在 `7fe7dbf3e37aefb437132d3584cb2ff09cff6af5` 与 `355bf875` 之间无差异；`355bf875` 仅新增调用方 CI 审计/报告
- 任务性质：已记录 **D-A68-02（MAJOR）** 的实际工具修复与本地验证；不是再审计旧 CI，也不是 PR20/组件/阶段验收
- 范围：仅本工作树实现/测试/提交；不 push、不 PR/MR、不 merge/deploy、不 approve、不 dispatch/rerun/cancel CI、不调用或委派 agent、不启动 Unity、不改共享插件或机器信任

> **结论先行：** 本地已实现并验证真实 `dotnet test` 的 xunit.v3 4.0.2 / Microsoft.Testing.Platform 逐例原生 XML 捕获与生产门禁；round 2 修复 D-DNT-R1-01（runner 版本/尾随 junk/原生 schema 身份错误放行），`dotnetTest`-only 不再回退 direct，meta/summary 记录真实 runner、命令、head、产物路径、SHA-256 与精确 counts。历史 CI 的 direct 结论仍只属于 direct；**远端 CI 的 `dotnetTest` 仍是 NOT_RUN，D-A68-02 的远端证据口径没有被本轮关闭**。

---

## 1. 本轮结论与边界

| 项目 | 状态 | 说明 |
|---|---|---|
| D-A68-02 本地工具修复 | **PASS（本地）** | 真实 `dotnet test` 原生逐例 XML、生产 gate、负例矩阵、真实正/负 live 探针均已落地 |
| **D-DNT-R1-01（round 2，MAJOR）** | **FIXED / PASS（本地）** | runner 版本锚定 4.0.2 + 合法 `+build` metadata；原生 schema 固定 3；caller 原 probe exit 0；扩展自测 48/48；14 XML replay 2,732/2,732 |
| 本地 `dotnetTest` 两工程抽样 | **PASS** | 修复后统一入口重跑：TableService 180/180、Architecture 132/132，合计 312/312；native XML 由本次 `dotnet test` 产生并被 gate 验证 |
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
   - `schema-version` 必须精确为支持的 **3**；framework 必须完整锚定 `xUnit.net v3 4.0.2`（允许合法 `+build` metadata，拒绝 4.0.20/4.0.200/INVALID/空 metadata/v2）；assembly leaf 与**精确 assembly 文件路径**（rooted，Windows 大小写不敏感）匹配；
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
11. Round 2 只加强新 `dotnetTest` gate：版本锚定与 schema 3 校验；`Test-DirectInvocationEvidence` 与 direct XML 策略未改。`Read-DirectResultXml` 已返回 `SchemaVersion`，新 gate 直接比较，不复制解析器。

### 3.2 `tools/ci/test-v5-dotnettest-results.ps1`（新增）

- 用 PowerShell AST 从 `tools/verify-v5.ps1` 提取**真实生产函数**：`Invoke-Captured`、`Get-DotnetTestConsoleCounts`、`Get-DotnetTestResultsArgument`、`Read-DirectResultXml`、`Test-DotnetTestInvocationEvidence`；测试不复制 gate 算法。
- 合成 XML 仅用于 gate 负例/正例，明确不是真实 `dotnet test` 产物。
- 负例矩阵覆盖：missing/empty/malformed/truncated、zero、summary-only、错误 assembly leaf、同 leaf 错误路径、错误 framework、assembly/collection 聚合不一致、duplicate native IDs、stale、results 目录越界、XML 越界、路径 escape、错误文件名、fail/skip/notRun/errors、native 非零但 XML 绿色、native exit unknown、console 缺失/总数/失败/跳过不一致、results 参数含空格；round 2 追加 4.0.1/4.0.20/4.0.200/4.0.2INVALID/4.0.2+ 空 metadata/v2 runner 与 schema 999/2/4。
- 正例包含：绿色 XML；含**两个完全相同 display name** 的合法 case（gate 必须通过，证明 display name 不被当作 native ID）；含空格的结果路径参数；round 2 追加 exact `4.0.2`、`4.0.2+build.1`、`4.0.2+0c7aa4991f` + schema 3。
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

Round 2 只改 PowerShell 工具（`tools/verify-v5.ps1` 与新自测脚本），未改任何 .NET 源码/测试/csproj；因此没有重跑 fresh build，沿用 round 1 的 0 warning / 0 error 结论。

### 5.2 既有回归（未修改脚本）

| 脚本 | 结果 | native exit | 证据 |
|---|---|---|---|
| `test-v5-direct-results.ps1` | **19/19** cases behaved as expected | 0 | `build/regression-r2/test-v5-direct-results.log`，SHA-256 `D6CABE47D158EF6644DA059139099D8B796975E1D753D24581403B7001A53D4B` |
| `test-v5-captured-exit.ps1` | **51/51** passed | 0 | 同上目录 `test-v5-captured-exit.log`，SHA-256 `FCCE016FD5FDA1BFF270695C4A42C6FB077D21CF3BAB9600DCA72C3CC2A8B1ED` |
| `test-v5-native-exit.ps1` | **6/6** cases behaved as expected | 0 | 同上目录 `test-v5-native-exit.log`，SHA-256 `B8094EA3F87BA3DD8B61C19F47831DF068282B77A50920945597BF1FD0D4549D` |

### 5.3 新 `dotnetTest` gate/负例/真实 live 探针

- 最终结果：**48/48** cases behaved as expected；0 failed（`results.json`: cases=48, passed=48, failed=0）。
- 组成：31 个原有 gate case（2 positive + 29 negative）+ **12 个 D-DNT-R1-01 身份/schema gate case**（3 positive + 9 negative）+ 1 个 results 参数 helper case + 2 个 console parser case + 2 个真实 live dotnet test case = 48；原有 36 case 全部保留，未删除/弱化。
- 新增身份/schema 正例：`xUnit.net v3 4.0.2`（无 metadata）、`4.0.2+build.1`、`4.0.2+0c7aa4991f` + schema 3；负例：`4.0.1`、`4.0.20`、`4.0.200`、`4.0.2INVALID`、`4.0.2+`（空 metadata）、`xUnit.net v2 ...`、schema 999/2/4；这些负例的 Reason 必须分别命中 `runner identity mismatch` 或 `schema identity mismatch`（v2 由共享解析器以 “not an xUnit.net v3” 拒绝，同属 runner 身份拒绝）。
- 合成 fixture 只作 gate 负例/正例，不冒充真实产物；真实 live 记录只有两个 live case。
- Live positive：真实 `dotnet test` 退出 **0**，native XML **3/3 Pass**，duplicate display name group=1，gate **PASS**（display name 未被误判为 native ID）。
- Live negative：真实 `dotnet test` 退出 **1**，native XML `failed=1`，gate **FAIL**；首失败 `Probe.Negative.FailingTests.ControlledFailure`。该结果证明不是旧 DLL/未编译被掩盖。
- 负例覆盖（实际全部拒绝）：missing/empty/malformed/truncated、zero、summary-only、wrong assembly leaf、wrong assembly path、wrong framework、assembly/collection aggregate mismatch、duplicate native IDs、stale、outside LogDir、outside results dir、path escape、unexpected filename、failed/skipped/not-run/errors、native nonzero + green XML、native exit unknown、missing console、console total/failed/skipped mismatch，以及本轮的 runner patch/尾随 junk/空 metadata/v2/schema 999/2/4。
- 证据：`artifacts/dotnettest-results-selftest-6a43fc279654406fbf507566f01181a0/results.json`，SHA-256 `5FD8E7EDDFE368CBF96A0CB3275275AAE23168D8656D980A63C43EF376FAA54E`；selftest 日志 `build/dotnettest-selftest-r2b.log`，SHA-256 `B7C33A6C145C388E6E42AEA3CB792B2DC5C5B72B72AE89E20C7261498D2089E6`；live fixture 根路径 `build/dotnettest-selftest-f025793c701b49e5b4da9a1c09bd5191/live-fixtures`。

### 5.4 统一入口 `RunnerMode dotnetTest`：TableService + Architecture

scoped manifest 仅列两个真实非 PG 测试工程（`build/manifest-dotnettest-two.json`，ignored），`-NoProjectDiscovery`；plugin copy 与 companion gate 仍执行。实际命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5.ps1 `
  -Configuration Release -DotnetPath D:\Unity\dotnet\dotnet.exe -RunnerMode dotnetTest `
  -ManifestPath <worktree>\build\manifest-dotnettest-two.json -NoProjectDiscovery `
  -LogDir <worktree>\build\unified-dotnettest-r2-final-5bfc582c
```

| 工程 | 模式 | native total | passed | failed | skipped | errors | not-run | nodes | native XML SHA-256 | gate |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|
| `Mahjong.TableService.Tests` | dotnetTest/MTP | 180 | 180 | 0 | 0 | 0 | 0 | 180 | `E141EBC9C976A593E7FC87FC07CAF1BFBD99DD1B20B1B08018BEE1572E1A44A5` | PASS |
| `Mahjong.Architecture.Tests` | dotnetTest/MTP | 132 | 132 | 0 | 0 | 0 | 0 | 132 | `19B21963AC57DF8F5683B4002FA71577502C005F4B0F5CEB19DC4BCC795EAB97` | PASS |

- 统一入口 native exit **0**；`OVERALL: PASS`；plugin copy PASS；companion 4/4 PASS；实际分母 **312/312**（180+132）。
- 两个 meta 均记录 `runner used : dotnet test (MTP; native xunit.v3 XML)`、精确命令、exit 0、native XML 路径/SHA/gate/counts；head 为修复 commit `6a0876d9`，script SHA-256 为 `41510892820C6B1E72B8BD182C171C23778E992F0E8AD6B87505862CCB1C065B`。
- 证据：`build/unified-dotnettest-r2-final-5bfc582c/`；`summary.log` SHA-256 `259DE9559732E6C7A45A8B3ED75FD9F4DC8E509DF5475EFC90C0EDCF28B235E2`；`run-info.txt` SHA-256 `BA24B14A1EFE85982168C07FE97DCA881894481A52FB418C2FCDE94B540986B5`；两个 meta 的 SHA-256 分别为 `DDAF86736741E184E7C62B286D5D94B5361F6321025BEFD7452B0D6A1E9DF255`（Architecture）和 `9B30C5F19505F4B14BF2832C67BC73D41B423505319A812A90CA2956159EE5C8`（TableService）。
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




### 5.7 D-DNT-R1-01（MAJOR）修复、原红例保留与重验

#### 5.7.1 缺陷与原始红例

- **ID/级别**：`D-DNT-R1-01`，**MAJOR**；本任务 round 1 新增发现，本轮完整已知 rework 缺陷组仅此 1 组（BLOCKER=0，MINOR=0）。
- **位置**：`tools/verify-v5.ps1` `Test-DotnetTestInvocationEvidence`（round 1 约 921-930，尤其 928 的 `-notlike "$ExpectedFramework*"`）；共享 `Read-DirectResultXml` 约 651-656 只解析 schema 整数，dotnet gate 未校验支持 schema。
- **复现**：
  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/caller-dnt-075f61/boundary-probes.ps1
  ```
- **原始红例实际**（round 1，`exit 1`）：control `xUnit.net v3 4.0.2+0c7aa4991f`/schema 3 => True；`4.0.20+other`/3 => **True（错误）**；`4.0.2INVALID`/3 => **True（错误）**；`4.0.2+0c7aa4991f`/999 => **True（错误）**；三者 Reason 均为 `3/3 test cases Pass; native dotnet test XML matches console and native exit 0`。
- **期望**：仅 control True；其余 False，Reason 明确为 `runner identity mismatch` 或 `schema identity mismatch`；不能靠改路径/缺 console 等非身份原因伪修复。
- **原红例保留**：
  - `build/caller-dnt-075f61/boundary-probes.ps1`，SHA-256 `9DA07FFA1E5861C7EA082BE11BFA688F6D8AEF77DACFFBA42426EE551AC9CA71`（未修改）
  - `build/caller-dnt-075f61/boundary-probe-results.red.json`，SHA-256 `1391B6EDAA0B8E25AD98B5C18A605C2F20D810BC2B73B4E6A5DEEEF1D99EC0C8`（原 caller 红 JSON，重放旧 gate 生成并单独保留）
  - `build/caller-dnt-075f61/boundary-probes.red.log`，SHA-256 `0580FF820B864C9B646878BCB0872DA0A3152FB5DC6ACA661FF9849A481E1A4B`
  - 原 4 个输入 XML 仍在 `build/caller-dnt-075f61/boundary/*/results/dotnet-test-results.xml`，未覆盖。

#### 5.7.2 修复映射

1. **版本完整锚定，不用前缀**：`Test-DotnetTestInvocationEvidence` 的 framework 检查改为大小写敏感锚定正则
   `^<escaped ExpectedFramework>(?:\+[0-9A-Za-z][0-9A-Za-z.-]*)?$`。
   - 接受：`xUnit.net v3 4.0.2`、`xUnit.net v3 4.0.2+0c7aa4991f`、`4.0.2+build.1`；
   - 拒绝：`4.0.1`、`4.0.20`、`4.0.200`、`4.0.2INVALID`、`4.0.2+`（空 metadata）、`xUnit.net v2 ...`；
   - 错误文本固定为 `dotnet test runner identity mismatch: expected 'xUnit.net v3 4.0.2' with optional '+build' metadata, got '...'`。
2. **原生 schema 固定 3**：新增 `ExpectedSchemaVersion = '3'` 参数；解析器返回 `SchemaVersion` 后显式比较，失败文本为 `dotnet test result XML schema identity mismatch: expected supported schema '3', got '...'`。未知/不支持 schema 不再放行。
3. **不改已验收 direct 策略**：只加强新 `dotnetTest` gate；`Test-DirectInvocationEvidence`/direct 行为未改，避免不必要回归。
4. **生产自测扩展**：`tools/ci/test-v5-dotnettest-results.ps1` 新增 12 个身份/schema case（3 positive + 9 negative），原有 36 case 全部保留；新增 case 要求 Reason 命中身份/schema 文本。
5. **证据重验而非重跑 PG**：用修复后的 gate 对已记录的 PG full 14 个 native XML + 原始 run start/meta exit/console 做 replay；未启动新 PG cluster，未重跑 2,732 业务。

#### 5.7.3 修复后重验

- **原 caller probe 原样重跑**：`exit 0`；control True；`wrong-patch-version` False（runner identity）；`framework-prefix-junk` False（runner identity）；`unsupported-schema` False（schema identity）。
  - 绿 JSON：`build/caller-dnt-075f61/boundary-probe-results.json`，SHA-256 `8B1903B5522C46F80ED547254274B166E7028E112262BA0F7F853BDFE020FD10`
  - 绿日志：`build/caller-dnt-075f61/boundary-probes.green-final.log`，SHA-256 `FBAE43728138F02DDB038596FC3EB0E5A1A01E49FE07C9A00A78002912379D34`
- **生产自测**：48/48，exit 0；见 §5.3；新增 34-45 覆盖 exact 4.0.2、metadata、wrong patch、trailing junk、empty metadata、v2、schema 999/2/4。
- **既有回归**：direct 19/19、capture 51/51、native-exit 6/6，全部 exit 0；见 §5.2。
- **实际 scoped 统一入口重跑**：修复 commit `6a0876d9` 下重跑，312/312（TableService 180 + Architecture 132），native exit 0，plugin/companion PASS；见 §5.4。
- **14 XML replay**：
  - 模式：`evidence-replay-not-new-execution`；原始 run start `2026-10-31T09:59:48Z`；原始 head `355bf875`；原始 meta exit 全 0；console 从原 `dotnetTest-*.log` 解析。
  - 结果：14/14 `ReplayOk=True`，2,732/2,732，0/0/0/0，schema 全 3，framework 全 `xUnit.net v3 4.0.2+0c7aa4991f`。
  - `build/dnt-r2-replay-14/replay-results.json`，SHA-256 `6467394110E7B04A2F19D9C224CC9153E1BCDBAA27AE133847CEB9BC8745986D`
  - `build/dnt-r2-replay-14/replay.log`，SHA-256 `B568ECF4EEEF1ACD065DA8CF9CE53B5A4EFD66DCAD9A4216FFEBF9448D087318`
- **验收断言**：原 probe exit 0；生产自测 48/48；live 3/3 + native exit1；direct/capture/native 19/51/6；scoped 312/312；replay 14/2,732/2,732；全部满足。



---

## 6. 逐例总数、同名与首失败

### 6.1 PG full 的 14 工程逐例总数（dotnetTest/MTP native XML；round 2 以修复后 gate replay 重验）

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
- Round 2 未启动新 PG，也未重跑 2,732 业务；上表 14 个 native XML 经修复后的 gate 以原始 `run start=2026-10-31T09:59:48Z`、原始 meta exit 0、原始 console 记录 replay，14/14 `ReplayOk=True`，总计 2,732/2,732，全部 schema=3、framework=`xUnit.net v3 4.0.2+0c7aa4991f`。这是**证据重验**，不是新执行；见 §5.7.3 与 `build/dnt-r2-replay-14/replay-results.json`。



---

## 7. 严格口径：历史 direct、本轮 local dotnetTest、未来 remote dotnetTest

| 来源 | runner | 是否本轮 | 逐例产物 | 结论适用范围 |
|---|---|---|---|---|
| 历史 CI 41181934433 / 41181937456 | direct | 否 | `direct-results.xml` | 仅 historical direct 的 14 工程/2,732 |
| 本轮 scoped local（round 2，commit 6a0876d9） | `dotnet test` + MTP | 是 | `dotnetTest-xml/*/dotnet-test-results.xml` | TableService 180 + Architecture 132 = 312/312 |
| 本轮 PG full local（round 1 执行；round 2 replay） | `dotnet test` + MTP | 执行：round 1；重验：round 2 | 14 个 `dotnetTest-xml/*/dotnet-test-results.xml` | 本地 14 工程/2,732（PG 环境）；round 2 仅 replay，不新执行 |
| 未来 remote CI `dotnetTest` | `dotnet test` + MTP | **否 / NOT_RUN** | 无 | **不能由本轮本地结果外推** |

- 历史 CI 的 `direct-results.xml` **不是** dotnetTest XML；本轮没有把它改名，也没有用 direct 冒充 dotnetTest。
- 本轮没有 push、没有 dispatch/rerun/cancel CI；没有产生远端 dotnetTest artifact。
- 默认 `RunnerMode direct` 仍存在于 PG wrapper 的默认值；这是为了保持当前 CI/验收行为，不等于 D-A68-02 已在远端关闭。

---

## 8. Known defects / 限制（一次性列出）

0. **round 2 rework 缺陷完整性**：本轮唯一已知 rework 缺陷组是 **D-DNT-R1-01（MAJOR）**，已在本轮修复并重验（§5.7）；BLOCKER=0，MINOR=0，无其他已知 rework 缺陷留到下一轮。以下 1–10 是继承边界/范围限制，不是本轮新增缺陷，也不构成本轮 blocked 或 rework 理由。
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
| `tools/verify-v5.ps1` | dotnetTest 原生 XML 参数、gate、meta/summary、短路径、absolute LogDir；round 2 锚定 4.0.2 版本 + 合法 build metadata、强制 schema 3 |
| `tools/ci/test-v5-dotnettest-results.ps1` | 新增 48-case gate/负例/真实 live 探针；round 2 增补 12 个 runner/schema 身份 case，原 36 case 保留 |
| `tools/ci/verify-v5-table-postgres.ps1` | 新增 RunnerMode（默认 direct）并传递 |
| `tools/ci/verify-v5-postgres.ps1` | 默认 direct；既有传递保持不变 |
| `doc/v5-重构/V5-DOTNETTEST-CASE-EVIDENCE-DELIVERY-20261031.md` | 本报告 |

未修改：`dotnet/tests/**` 业务断言、`dotnet/src/**` 生产、`*.csproj`/package、`dotnet/Mahjong.sln`、`tools/v5-verify.manifest.json`、workflow、Unity/Resources/Burst、B1 gate、其它 worktree。

### 9.2 tracked 文件 SHA-256（round 2 提交前工作树内容）

| 文件 | SHA-256 |
|---|---|
| `tools/verify-v5.ps1` | `41510892820C6B1E72B8BD182C171C23778E992F0E8AD6B87505862CCB1C065B` |
| `tools/ci/test-v5-dotnettest-results.ps1` | `A3402DCD81FC63B331557545F0614EE8BFE9827B19D22566670189A84AFBF17E` |
| `tools/ci/verify-v5-table-postgres.ps1` | `85B67C3836CF82ACA84565AC92119F932DB9D17C79A471D8C0B46A031B43B965` |
| `tools/ci/verify-v5-postgres.ps1` | `FCB3B35387FED80703A098000053EDF70BFDD8A1FB6E7CC456A8DE8E0E270D10` |
| 本报告 `V5-DOTNETTEST-CASE-EVIDENCE-DELIVERY-20261031.md` | 不在文内自引用；最终 docs commit SHA/文件 SHA 由提交后输出 |

### 9.3 证据 SHA-256 manifest

- 完整 evidence bundle manifest 由 PG wrapper 生成：`artifacts/dotnettest-pg2-20261031-175613/manifest.sha256.json`，SHA-256 `7AA561DC6D47041E98D2E04C5A80B4AA07DDE197841E589304E5E338FAB10CDE`（235 个条目，覆盖 `build-*`、`pg-*`、`psql-*`、`tests*`、`crash-*`、`restore-*`、`table_store.dump`、`wrapper-summary.json` 等）。
- native XML 逐例 summary：`artifacts/dotnettest-pg2-20261031-175613/unified/native-xml-summary.json`，SHA-256 `5D6553F1779AFB1D02C674544BFE8B60E10E1466C7066715EB9CD3562C508A7B`。
- 14 个 native XML 的 SHA-256 见 §5.6.2；原始 stdout/stderr/framework logs、meta、companion 证据保留在 PG LogDir 与 `unified/`；不提交 DLL、秘密、连接串、key、种子或 private saves。
- Round 2 新证据：
  - caller 原 probe：`build/caller-dnt-075f61/boundary-probes.ps1` `9DA07FFA1E5861C7EA082BE11BFA688F6D8AEF77DACFFBA42426EE551AC9CA71`
  - 原红 JSON/绿 JSON：`1391B6EDAA0B8E25AD98B5C18A605C2F20D810BC2B73B4E6A5DEEEF1D99EC0C8` / `8B1903B5522C46F80ED547254274B166E7028E112262BA0F7F853BDFE020FD10`
  - 原红/绿日志：`0580FF820B864C9B646878BCB0872DA0A3152FB5DC6ACA661FF9849A481E1A4B` / `FBAE43728138F02DDB038596FC3EB0E5A1A01E49FE07C9A00A78002912379D34`
  - 48-case 自测 `results.json`：`5FD8E7EDDFE368CBF96A0CB3275275AAE23168D8656D980A63C43EF376FAA54E`
  - scoped 312/312 `summary.log` / `run-info.txt`：`259DE9559732E6C7A45A8B3ED75FD9F4DC8E509DF5475EFC90C0EDCF28B235E2` / `BA24B14A1EFE85982168C07FE97DCA881894481A52FB418C2FCDE94B540986B5`
  - 14 XML replay `replay-results.json` / `replay.log`：`6467394110E7B04A2F19D9C224CC9153E1BCDBAA27AE133847CEB9BC8745986D` / `B568ECF4EEEF1ACD065DA8CF9CE53B5A4EFD66DCAD9A4216FFEBF9448D087318`

### 9.4 Git/提交

- 基线：`8fd7c6cb...`；冻结审计头：`355bf875...`；round 1 reviewed delivery：`b4c04d31a85256e1d233e76995bd23cf9172d348`；round 2 code fix：`6a0876d980c59cb5f4b69b2e17ad471423e77c67`；`git merge-base --is-ancestor` 基线与 `355bf875` 均为 0。
- 源码树身份：`git diff --quiet 7fe7dbf3e37aefb437132d3584cb2ff09cff6af5 355bf875 -- dotnet tools unity .github` 为 exit 0；`355bf875..6a0876d9` 只含本任务 expectedFiles 的 5 个路径（round 2 修改其中 2 个工具文件），没有 `dotnet/**` 源码、Unity、`.github` 变更。
- `git diff --check`（round 2 工作树）exit **0**。
- Round 2 提交安排：`6a0876d9` = 最小代码修复（`tools/verify-v5.ps1` + 自测脚本，2 files，28+/2-）；本报告作为 docs commit 紧接其后。提交后 `git status --porcelain=v1 -b` 应 clean。
- 提交后应复核 `git status --porcelain=v1 -b` clean，且 `git diff --check 355bf875..HEAD` exit **0**。历史 `git diff --check 8fd7c6cb..7fe7dbf` 仍是 1,573 条，未修（D-A68-03）。
- 本轮未 push、未建/改 PR/MR、未 merge、未 deploy、未 approve、未 dispatch/rerun/cancel CI。

---

## 10. 最终判定

1. 本地已把 `dotnet test` 的真实逐例原生 XML 纳入生产 gate；缺失/损坏/过期/错误 assembly/路径逃逸/native 非零/console 不一致均 fail closed，`dotnetTest`-only 不回退 direct；round 2 进一步锚定 xUnit.net v3 4.0.2（允许合法 build metadata）并强制原生 schema 3。
2. 真实 TableService 180/180 与 Architecture 132/132 已在修复后统一入口重跑，合计 **312/312**；真实 PG full 的 14 工程/2,732 已由修复后 gate 以原始运行元数据 replay 重验，见 §5.6/§5.7。
3. 同名 display name（2,730 unique / 2,732 total）被正确接受；native ID 唯一性单独校验，不把 display name 当 ID。
4. 历史 CI direct、本轮 local dotnetTest、未来 remote dotnetTest 已严格区分；远端 dotnetTest 仍 NOT_RUN，D-A68-02 远端口径未关闭。
5. **D-DNT-R1-01 已修复**：原 caller probe exit 0（control true，三个非法身份 false），48/48 扩展自测、direct 19/19、capture 51/51、native-exit 6/6、scoped 312/312、replay 2,732/2,732 全部满足；原红例/原 probe 哈希未变，没有删除或弱化既有 case。
6. 默认 direct 仍存在；本报告不因本地 dotnetTest 绿而宣称 CI D02 已关闭，也不宣称 PR20/G2–G5/Unity/Android/iOS/真机跨网通过。
7. 本报告与证据供调用方独立验收；最终 PR20/publication 由调用方独占，本任务不 push/PR/merge/approve。
