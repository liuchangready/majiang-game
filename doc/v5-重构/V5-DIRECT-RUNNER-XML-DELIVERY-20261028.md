# V5 Direct Runner 原生 XML 证据补齐（2026-10-28）

状态：**本地维护补齐已交付并通过本地真 PostgreSQL 统一验证；远端新提交 CI 为 NOT_RUN。**
本文件是工具与证据交付记录，不是 G2–G5 验收，也不是完整 V5 完成声明。

---

## 0. 结论

- 冻结基线：`59303151c4fa500aba16779e52b4ec22712697ba`（PR20 已审查发布源）。
- 修复唯一入口 `tools/verify-v5.ps1`：每次真实 direct test invocation 现在显式写出
  xunit.v3 原生逐用例 XML，并在同一入口内做结构、程序集身份、test 节点、集合汇总、
  console 摘要和 native exit 的一致性判定。
- 新增独立边界负例矩阵 `tools/ci/test-v5-direct-results.ps1`：**19/19** 通过，且每个
  负例都调用生产判定函数 `Test-DirectInvocationEvidence`，未镜像重写算法。
- 修改后既有 native exit 探针仍为 **6/6 PASS**，强杀值为 `-1`。
- 真实独占 PostgreSQL 16.10 统一入口（`verify-v5-table-postgres.ps1 -RunUnifiedVerification`）
  最终尝试 **全部 stage PASS、wrapper exit 0**。统一 verifier 的 **11 个 direct runner
  全部具有真实 XML、非空 test case 节点、2294/2294 Pass**；每个 XML 的节点数、SHA-256、
  console 摘要与 `direct.meta.txt` 独立交叉核对一致。
- PostgreSQL 表存储 full `37/37`、targeted `13/13`；crash/restart、cold reader、
  empty-DB restore、Lobby 迁移/回执/清理/环境恢复均 PASS。
- 未修改任何 csproj、生产/正式测试、manifest 门槛/schema、现有 PG wrappers、native
  exit 生产 launcher 或既有 native exit 探针；未 push/PR/MR/merge/deploy/`complete.flag`。
- V4 仍是唯一生产基线；G1 仍为既有 APPROVED，本任务不重新批准；G2–G5 仍未批准；
  181 classified 仍未覆盖。历史 `392b973` 报告不被改写，本工具结果不倒填旧 CI。

---

## 1. 冻结基线、分支与范围

工作树：`D:/majiang-wt/ci-direct-xml-ds-20261028`
分支：`wt/v5-ci-direct-xml-ds-20261028`

| 项 | 实测值 |
|---|---|
| `git rev-parse HEAD`（冻结） | `59303151c4fa500aba16779e52b4ec22712697ba` |
| HEAD parent | `b0d689af0ef6407d4fa683ba3c768314281d7bca` |
| HEAD tree | `550c403cb6537cee01e32770dbf6069bf041ca28` |
| `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| `git merge-base --is-ancestor 8fd7c6c HEAD` | exit 0 |
| 冻结时 `git status --porcelain=v1` | 空 |
| 起始范围 | 仅 3 个允许文件 |

未从旧 `main` 或已关闭 PR19 source 开发；未带入其它 receiver 的未提交代码；未操作
GLM/Anti/Windows Player 线程或其它工作树；未动用户 Resources/Burst/环境。

允许且实际修改的文件：

1. `tools/verify-v5.ps1`（修改）
2. `tools/ci/test-v5-direct-results.ps1`（新增）
3. `doc/v5-重构/V5-DIRECT-RUNNER-XML-DELIVERY-20261028.md`（新增，本文件）

---

## 2. 既有缺口（不重做旧 CI 审查）

`CODEX-V5-PR20-392B973-DUAL-CI-REVIEW-20261028.md` §4.4 / §10.2 已把
**BLOCKED-01** 冻结为：11 个 direct invocation 仅有 raw console summary +
`direct.meta.txt`，没有逐 runner XML；不得把 console 计数写成“XML 已验证”。

本任务只修这个已记录的工具缺口：

- 不重新审查 `392b973` 的两个旧 run；
- 不把本文件的 2294 写成旧 CI 的新发现；
- PostgreSQL wrapper 自己已有的 `tests.xml` / `tests-targeted.xml` 不是本缺口，
  也不能代替 11 个 direct runner 的 XML。

---

## 3. 实现：同一入口补采集与判定

### 3.1 官方参数确认

当前测试程序集是 xunit.v3 `4.0.2` self-host executable。实际执行：

```powershell
& 'D:\Unity\dotnet\dotnet.exe' `
  dotnet/tests/Mahjong.Domain.Tests/bin/Release/net8.0/Mahjong.Domain.Tests.dll --help
```

runner 帮助列出 `-result-xml <filename>`（“output results to xUnit.net v2+ (XML) file”），
另列 `-result-trx`、`-result-nunit`、`-result-junit` 等。`-help` 是未知选项（exit 3），
`--help` 打印帮助后 exit 2；两者都不是测试运行结果。实现采用官方 `-result-xml`，
未猜参数，也未改 manifest 的 `directRunnerArgs`。

### 3.2 采集路径与新鲜度

每个真实 direct invocation：

```text
<LogDir>\direct-xml\<ProjectName>\direct-results.xml
```

- 路径按 project 唯一隔离，位于本次运行独占的 `LogDir` 内；CI 现有
  `artifacts/verify-v5/**` 上传会自然包含。
- 调用前只删除这一个自有路径（文件或同名残留目录），不删除共享/未知结果。
- 命令行在原 manifest 参数后附加 `-result-xml <本次路径>`；若 manifest 未来已含
  `-result-xml`，先剔除旧值再写入本次路径，避免写到别处。
- 命令行与 `direct.meta.txt` 均记录精确 XML 路径、SHA-256 和 gate 结论。
- 判定要求 XML 的 `LastWriteTimeUtc` 不早于本次 invocation 起始时间（5 秒容差），
  且路径必须在本次 `LogDir` 之下；历史 XML、跨项目 XML、目录外 XML 都会被拒绝。
- 原有 `direct-<project>.log`、`runs/<project>/direct.meta.txt`、
  `test-results/<project>/direct/**` 原始框架失败日志、console/native exit、
  provenance、全量 project discovery 全部保留。

### 3.3 真实 XML 结构与计数判定

生产函数：

- `Get-DirectRunnerCounts`：解析 raw console summary 的 Total/Errors/Failed/
  Skipped/Not Run，并派生 Passed。
- `Read-DirectResultXml`：解析并结构校验真实 `-result-xml` 文档。
- `Test-DirectInvocationEvidence`：一次 invocation 的最终生产判定，综合
  XML 路径/新鲜度、程序集身份、XML 计数、console 计数和 native exit。

`Read-DirectResultXml` 至少拒绝：

- 根不是 `<assemblies>`、缺少整数 `schema-version`；
- `<assembly>` 不是恰好一个，或缺少 `name`；
- `name` 叶子与本次测试程序集名不一致（wrong assembly）；
- 不是 `xUnit.net v3` 结果（`test-framework` 不匹配）；
- 缺少 `total/passed/failed/skipped/errors/not-run` 或不是非负整数；
- test 节点缺少 `id`/`name`、重复 id、未知 result；
- 有 test 节点但没有 `<collection>` 汇总；
- collection 的 `total/passed/failed/skipped/not-run` 与其 test 子节点不一致；
- collection 总数与 test 节点总数不一致；
- assembly `total` 与 test 节点数不一致，或 `total` 不等于
  `passed+failed+skipped+not-run`；
- assembly 的 passed/failed/skipped/not-run 与 test 节点 result 计数不一致；
- `errors` 属性与 `<errors><error>` 节点存在性不一致（框架错误不会被无 test 节点
  的集合级异常漏掉）。

`Test-DirectInvocationEvidence` 还要求：

- XML 存在、非空、路径在本 `LogDir` 下、last-write 不早于本次 invocation；
- native exit 必须为 `0`（`$null`/异常绝不当作 0）；native 非 0 时即使 XML 全绿也 FAIL；
- XML 必须有 `total > 0` 且 test 节点数等于 `total`；
- failed/errors/skipped/not-run 任一非 0 即拒绝（保持既有零 skip/失败阻断语义）；
- XML 的 Total/Passed/Failed/Skipped/Errors/NotRun 必须与 raw console summary
  完全相同；console 缺失/不可解析也拒绝，不能用 console 代替 XML 报 PASS；
- 结构失败、计数不符、旧结果等均使 project 行 `FAIL`，从而 `$anyFail` 使顶层
  native exit 为 1；不存在 fallback 到 console 的“XML 已通过”路径。

### 3.4 RunnerMode 语义保持不变

- `direct`：执行 direct，必须产/验 XML。
- `auto`：仍先 `dotnetTest`；只有它没有可解析 summary 且日志无 build/restore error
  时才回退 direct。`dotnetTest` 已产生的失败 summary 不会被后续 direct 成功掩盖。
- `dotnetTest`：只跑 `dotnetTest`，不创建 direct XML 目录、不声称执行过 direct。
- `dotnetTest` 的 build error、stale TestResults purge/attribution 行为未改；
  native exit 仍是权威。

---

## 4. 独立边界测试：`tools/ci/test-v5-direct-results.ps1`

该脚本用 PowerShell AST 从 `tools/verify-v5.ps1` 提取真实生产函数
`Get-DirectRunnerCounts`、`Get-TestSummaryFromDirectRunner`、
`Read-DirectResultXml`、`Test-DirectInvocationEvidence`，然后对临时 fixture 调用
`Test-DirectInvocationEvidence`。每个负例由生产判定拒绝，不是复制算法自证。

Fixture 只写本树 ignored 的 GUID 目录：

```text
D:\majiang-wt\ci-direct-xml-ds-20261028\artifacts\direct-results-selftest-ed90122e0be6426da19afb01d043b3bb
```

结果：**19/19 cases behaved as expected**，native exit 0。

| # | 用例 | 期望 | 实际 |
|---:|---|---|---|
| 1 | 正例，LogDir/XML 路径含空格 | accept | PASS |
| 2 | XML 缺失 | reject | reject |
| 3 | 0 字节 XML | reject | reject |
| 4 | 非 XML 文本 | reject | reject |
| 5 | 截断 XML | reject | reject |
| 6 | 0 tests | reject | reject |
| 7 | 只有 assembly 摘要、无 test case 节点 | reject | reject |
| 8 | wrong assembly | reject | reject |
| 9 | assembly total 与节点数不符 | reject | reject |
| 10 | collection total 与子节点不符 | reject | reject |
| 11 | failed 用例（native exit 0，单独验 XML gate） | reject | reject |
| 12 | errors + 无 test 节点的框架错误 | reject | reject |
| 13 | skipped | reject | reject |
| 14 | not-run | reject | reject |
| 15 | native exit 非 0 但 XML 看似 green | reject | reject |
| 16 | 旧 XML（last-write 早于本次） | reject | reject |
| 17 | XML 在本次 LogDir 之外 | reject | reject |
| 18 | console summary 缺失 | reject | reject |
| 19 | console 与 XML 总数不符 | reject | reject |

证据：

```text
results.json SHA256 CFDAA23EEB070EB5F7DABA15394A97D709545C443DA792B58C5C94DD00F53C6F
productionScriptSha256 793FB63289B5EAD748E69535959858216D69538F32B21AA248E86F5820F86571
testScriptSha256 4CACA2108751C8779CAC405B12881D495553294C6E0CF78DBA7EC0405F0D75A1
```

该矩阵是工具边界测试，**不冒充** 2294 个正式用例；未修改正式测试。

---

## 5. Native exit 回归

修改后执行原样命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-native-exit.ps1
```

结果：**6/6 PASS**，native exit 0。

| case | expected | actual |
|---|---:|---:|
| exit-0 | 0 | 0 |
| exit-1 | 1 | 1 |
| exit-2 | 2 | 2 |
| exit-17 | 17 | 17 |
| timeout | throw | true |
| forced-writer | nonzero | -1 |

证据：

```text
D:\majiang-wt\ci-direct-xml-ds-20261028\artifacts\native-exit-87126326a4304de58b45ff1c02966dbd\results.json
SHA256 3886116BC8BC9272683E1D9E16E2402CE6F1809EEC8EBC2BD8A98ADD1D0E3DD0
```

该哈希与 caller 准备阶段 6/6 证据一致。既有 native exit 生产 launcher 与探针均未修改。

---

## 6. 真 PostgreSQL 统一入口验收

### 6.1 最终命令与隔离

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release `
  -DotnetPath 'D:\Unity\dotnet\dotnet.exe' `
  -Port 55692 `
  -Root 'artifacts/p52d3' `
  -LogDir 'artifacts/v5-direct-xml-accept-20261028-attempt3/table' `
  -PgZipPath 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip' `
  -RunUnifiedVerification `
  -UnifiedLogDir 'artifacts/v5-direct-xml-accept-20261028-attempt3/unified' `
  -LobbyRoot 'D:\majiang-pg-tmp\lobby-ds-a2' `
  -LobbyPort 55693
```

隔离与安全：

- 表 cluster 端口 `55692`、Lobby cluster 端口 `55693`；与 GLM 持久宿主使用的
  `55597` 及既有默认 `55460/55592/55593` 不同。启动前确认端口空闲。
- 表 cluster 根为本次独占的 `artifacts/p52d3`；Lobby 根为 worktree 外的独占
  `D:\majiang-pg-tmp\lobby-ds-a2`；两者都按现有 ownership marker 校验后清理。
- 使用既有缓存包，wrapper 内置 SHA-256 pin：
  `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`。
- 未改共享 cluster、未杀未知进程、未改机器 trust/PATH/scheduler。
- `fresh-build` 用 .NET SDK `8.0.426`；统一入口 `RunnerMode=direct`。

### 6.2 Stage 结果（最终尝试）

| stage | status | exit |
|---|---|---:|
| cluster | PASS | 0 |
| xml-gating-selfcheck | PASS | 0（7 个负例被拒） |
| fresh-build | PASS | 0 |
| tests | PASS | 0 |
| tests-targeted | PASS | 0 |
| crash-writer | PASS | KILLED（预期强杀） |
| postgres-crash-restart | PASS | 0 |
| crash-reader | PASS | 0 |
| backup-restore | PASS | 0 |
| unified-verification | PASS | 0 |
| result | PASS | 0 |
| cleanup | PASS（ownership-verified） | 0 |

wrapper：`P52-TABLE-POSTGRES-PASS`，`WRAPPER_EXIT=0`。

统一 verifier summary：`OVERALL: PASS`，14 个 project row = 11 test + 3 build-only
helper；0 FAIL/BLOCKED/NOT_RUN；plugin copy 12 assemblies；companion gate 4/4 PASS。
统一 verifier 自身 native exit 0。

### 6.3 11 个 direct runner 的真实 XML

下表由独立只读审计脚本重新解析每个 XML、console summary 和 `direct.meta.txt` 得出，
不是抄 summary.log：

| # | 程序集 | XML test 节点 | passed | failed | errors | skipped | not-run | meta exit | XML SHA-256 |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---|
| 1 | `Mahjong.Ai.Tests` | 444 | 444 | 0 | 0 | 0 | 0 | 0 | `10075900E66FD4FDBBD955D144E9CF4D96058276AD14D25AB28AC6CFE6E8AEE3` |
| 2 | `Mahjong.Architecture.Tests` | 92 | 92 | 0 | 0 | 0 | 0 | 0 | `E2F5479F22D45FDD1D58057B014895137E63F3F5D85CEDD9445DE8B3381417A5` |
| 3 | `Mahjong.Client.Tests` | 182 | 182 | 0 | 0 | 0 | 0 | 0 | `6CE157733C593B2EC6F892E5221A6479B4EF46977F5156B3D0965862A8D602A6` |
| 4 | `Mahjong.Client.Wss.Tests` | 80 | 80 | 0 | 0 | 0 | 0 | 0 | `DCF76A0406F614FE7496762C2732ED264B2CCA6D4E853AA61B5B666363F8B407` |
| 5 | `Mahjong.Contracts.Tests` | 149 | 149 | 0 | 0 | 0 | 0 | 0 | `9450AA44957874DA040525DA3BB53A67526E112D199F6E03ABDFC2E0757600B9` |
| 6 | `Mahjong.Domain.Tests` | 529 | 529 | 0 | 0 | 0 | 0 | 0 | `4F3E7CC0709DB54876475102D79A689750C4002D0AC9B1296547A3F97C2E9796` |
| 7 | `Mahjong.Infrastructure.Tests` | 37 | 37 | 0 | 0 | 0 | 0 | 0 | `D542A200DAB16DFCA500C7361E81B5EBBBA977BA2A14CB173D8B1D1C7F8C6C05` |
| 8 | `Mahjong.LobbyService.Tests` | 89 | 89 | 0 | 0 | 0 | 0 | 0 | `084643D83422A1C083ED2C75D7BD13D35CE9B194ACDD98B1BA4A32D53A0F78DC` |
| 9 | `Mahjong.TableRuntime.Tests` | 400 | 400 | 0 | 0 | 0 | 0 | 0 | `B531B497F4043D3DCA3C53597285DCACD24F826871C6A9620F2DF6A154F967B0` |
| 10 | `Mahjong.TableService.Tests` | 152 | 152 | 0 | 0 | 0 | 0 | 0 | `C90B8F27EDFBE641278245609B28DB11E6884692340D30D71FB9B1A45A53E3DD` |
| 11 | `Mahjong.Unity.TableModel.Tests` | 140 | 140 | 0 | 0 | 0 | 0 | 0 | `D7A7D2FF2AD875763F97C73F59D035090CBB28BAE7A980D3D7B5673616850722` |
| | **合计** | **2,294** | **2,294** | **0** | **0** | **0** | **0** | | |

独立审计结论：`projects=11 total=2294 allPass=True problems=0`。
每个 XML 均为非空 test case 集合，assembly 叶子名与 project 一致；XML 的
total/passed/failed/skipped/errors/not-run 与 raw console 完全相同，`direct.meta.txt`
的 exit=0、total/failed/skipped、XML 路径和 SHA-256 也与文件一致。

### 6.4 PostgreSQL 与 Lobby 证据

- 表存储 full XML：`total=37 passed=37 failed=0 skipped=0 errors=0 not-run=0`，
  37 个 test 节点。
- targeted XML：`total=13 passed=13 failed=0 skipped=0`，13 个节点，均为
  `PostgresTableStoreTests`。
- crash writer：commit marker 先落库，强杀 pid `5896`，kill record exit `-1`。
- immediate restart：postmaster start time 改变，recovery lines=3。
- cold reader：state=1/seq=2/events=2，receipt=Applied，outbox 未发布，terminal=true。
- `pg_dump` → 空库 `pg_restore` → restore reader：events=2，ownerEpoch=3。
- Lobby：迁移执行 2 次成功，unified verifier exit 0；环境变量在 finally 恢复；
  Lobby cluster 以 ownership marker 清理。
- 表 cluster 最终 `ownership-verified cluster removed: ...\artifacts\p52d3`。

### 6.5 产物与 SHA-256

证据根：

```text
D:\majiang-wt\ci-direct-xml-ds-20261028\artifacts\v5-direct-xml-accept-20261028-attempt3
```

| 产物 | SHA-256 |
|---|---|
| `table/wrapper-summary.json` | `0ACF2F5E2F4170B9E4FAAE89EEAFCC5F08E76BAF5A6E0273964521054B23A6C9` |
| `table/wrapper.log` | `2756C765EE69E1C03BA3AB20C06C6EA17A1A7C5238E703990C8C5C1E35075973` |
| `table/manifest.sha256.json` | `364C64B5C749097964B26AFC08C0D4FA328555903A75996E6A32F33A3DEDDE92` |
| `table/tests.xml` | `7F67B714327C3D92E3CCD46F8217291786C0F623C1E9B0125530B2ED6693B57F` |
| `table/tests-targeted.xml` | `A9AFA725C8538829494D151490B1B65E08287033427281FAD0EF68697B11E42C` |
| `table/unified.log` | `18C562A241F1BED0392F3A955D89717D83C7A74A2980AA015DD23946A1084169` |
| `unified/summary.log` | `E608D176BFE9523D6619E118A1BC7190AA7584ED5D8F96B4693810131C8BEE1B` |
| `unified/run-info.txt` | `2CA22EA22C17F4093B51DE598C1B95F0BFC93D9D453DBCC633A8CE60E81F8FCE` |
| `unified/independent-direct-xml-audit.json` | `C6209EC0A419B644BD88715342FEAB889087BBE9470F15D89BFEEB6A31586628` |

`table/manifest.sha256.json` 覆盖 109 个文件；独立重算 **109/109** 与 manifest 条目
一致（0 mismatch）。原始 console/native exit、full XML、raw failure logs、meta、
companion 证据均在上述根内，可直接上传/复核。

---

## 7. 初次失败、根因与修复（保留原 FAIL）

为避免把第一次不完整运行写成 PASS，保留两次失败尝试的 summary 与日志：

### 7.1 尝试 1：LobbyRoot 在 worktree 内 + 长路径 cleanup

命令使用 `-LobbyRoot artifacts/v5-direct-xml-accept-20261028/lobby-root`。Lobby wrapper
在任何副作用前拒绝：

```text
BLOCKED: refusing a disposable cluster root inside the worktree:
D:\majiang-wt\ci-direct-xml-ds-20261028\artifacts\v5-direct-xml-accept-20261028\lobby-root
```

因此 `unified-verification` 得到 child exit 2，表 wrapper 报 FAIL；同一尝试的 cleanup
又因 Windows 260 字符路径限制在删除 PostgreSQL `pgsql\pgAdmin 4\...` 时失败
（`Could not find a part of the path 'properties.sql'.`）。

证据：

```text
artifacts/v5-direct-xml-accept-20261028/table/wrapper-summary.json
exit=1  SHA256 93C691694F0264FD3F34D15FE6199D4BD2B2C677A8FBFC1E05F84234628DF772
```

### 7.2 尝试 2：仅长路径 cleanup

把 LobbyRoot 移到 worktree 外后，表测试、crash/restart、backup/restore、
`unified-verification` 全部 PASS（unified verifier exit 0，2294/2294 XML 已生成），
但表 cluster cleanup 仍因 root 路径过长失败：

```text
Could not find a part of the path 'create.sql'.
```

wrapper exit 1；表 cluster 的 `data` 已停止，但 `pgsql` 子树删除不完整。

证据：

```text
artifacts/v5-direct-xml-accept-20261028-attempt2/table/wrapper-summary.json
exit=1  SHA256 98F525182056803CA42334E798C55F77440D57271E542CED50C9128EE7C4568A
```

### 7.3 尝试 3：短 root 修复

只把表 cluster root 缩短为 `artifacts/p52d3`（仍在本树 `artifacts` 下，满足 wrapper
ownership 删除守卫），其余命令、端口、pin、统一入口不变。最终 `cleanup` PASS，
wrapper exit 0。

这两次失败是**环境/路径配置问题**，不是 verifier XML 逻辑失败；但它们作为真实
FAIL 保留，未删除、未覆盖。

---

## 8. PASS / FAIL / BLOCKED / NOT_RUN

| 项 | 状态 | 依据 |
|---|---|---|
| direct runner 原生 `-result-xml` 采集 | **PASS** | 11/11 XML 存在、非空 |
| XML 结构/程序集/节点/集合/errors 判定 | **PASS** | 19/19 边界矩阵；11/11 实跑 |
| XML 与 console/meta/native exit 一致 | **PASS** | 独立审计 problems=0 |
| 最终真 PostgreSQL 统一入口 | **PASS** | wrapper exit 0；全部 stage PASS |
| 表存储 full 37/37 | **PASS** | tests.xml 37 节点 |
| targeted 13/13 | **PASS** | tests-targeted.xml 13 节点 |
| native exit 6/6 | **PASS** | 0/1/2/17/timeout/-1 |
| 初次 LobbyRoot 失败 | **FAIL（已修复并重跑）** | 尝试 1 summary exit 1 |
| 初次/第二次长路径 cleanup | **FAIL（已用短 root 重跑）** | 尝试 1/2 summary exit 1 |
| 远端新提交 CI | **NOT_RUN** | 本任务不 push |
| Windows Player / Unity PlayMode | **NOT_RUN** | 非本任务 |
| Android 真机 / iOS | **NOT_RUN / BLOCKED** | 非本任务，无设备 |
| 跨物理网络 / 持久 WSS 宿主 | **NOT_RUN / 未接受** | 非本任务 |
| G1 | **APPROVED（既有，不重开）** | 本任务不改门禁 |
| G2–G5 | **未批准** | 本任务不改变 |
| 181 classified | **未覆盖** | 本任务不改变 |

---

## 9. 复核命令

```powershell
# 生产边界负例
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/test-v5-direct-results.ps1

# native exit 6 项
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/test-v5-native-exit.ps1

# 独立审计最终统一运行的 11 个 XML/console/meta
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File artifacts/audit-direct-xml.ps1 `
  -UnifiedDir artifacts/v5-direct-xml-accept-20261028-attempt3/unified
```

注意：`artifacts/audit-direct-xml.ps1` 是本次只读审计辅助脚本，位于 ignored
`artifacts/`，不属于 tracked 交付范围，也不冒充正式测试。

---

## 10. 边界

- 本文件只追加 direct runner XML 证据链；没有改写 `392b973` 的历史报告或旧 CI 结论。
- 本地 2294/2294 与 PG 37/37、13/13 是本次冻结头的实际运行结果；远端新提交 CI
  尚未运行，必须由 caller 发布后单独复核。
- 不宣称 G2/G3/G4/G5、Windows Player、Android 真机、iOS、跨网、LocalHost 整场、
  持久 WSS 宿主或完整 V5 通过。
- V4 仍是生产基线；G1 仍为既有 APPROVED；G2–G5 未批准；181 classified 未覆盖。

---

## 11. Caller 独立验收（2026-10-28）

验收源码冻结为 `ed9c307b69d7e5b6a5a5bd01fd5f62829dbedfbb`，生产 verifier SHA-256
为 `793FB63289B5EAD748E69535959858216D69538F32B21AA248E86F5820F86571`。
独立检查确认相对 `5930315` 仅三个授权文件发生变化，工作树干净、`git diff --check`
退出 0；未发现本任务范围内需返修的编译、测试或功能缺陷。

Caller 实际重新运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-direct-results.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/test-v5-native-exit.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5Diagnostics.Tests.ps1 `
  -DotnetPath D:/Unity/dotnet/dotnet.exe -KeepWorkspace
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55782 -Root artifacts/p52cr `
  -LogDir artifacts/caller-direct-xml-20261028/table `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -RunUnifiedVerification -UnifiedLogDir artifacts/caller-direct-xml-20261028/unified `
  -LobbyRoot D:/majiang-pg-tmp/lobby-caller-xml-20261028 -LobbyPort 55783
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File artifacts/caller-direct-xml-20261028/audit-direct-xml.ps1
```

| 独立复验 | 结果 |
|---|---|
| 新 XML 判定边界矩阵 | 19/19，native exit 0；证据 `artifacts/direct-results-selftest-bf0989b6105346779c61193c4fb91b26` |
| 原有 native exit 探针 | 6/6，native exit 0；证据 `artifacts/native-exit-8bd74ba271b54eb099849d95d2dffe65` |
| 原有诊断回归 | 88/88，native exit 0；真实失败计数、原始失败日志、锁定历史日志排除和异常退出均保持 |
| 真 PG 统一入口 | 全阶段 PASS，wrapper native exit 0；表 37/37、定向 13/13、强杀/重启/冷读/空库恢复及 ownership cleanup 通过 |
| 统一 verifier | 14 PASS / 0 FAIL / 0 BLOCKED / 0 NOT_RUN，companion PASS；Release solution build 0 warnings/errors |
| 独立 XML/console/meta 审计 | 190 检查通过；11 份原生 XML、2294/2294 Pass、0 failed/errors/skipped/notrun；每份精确路径、SHA、console 和 meta native exit 0 一致 |
| 真实含空格路径 | Domain runner 529/529，native exit 0；真实 XML 再经生产 `Test-DirectInvocationEvidence` 判定 PASS，独立于 2294 分母 |

本次统一运行中 Infrastructure runner 报告耗时 `2232.979s`；状态查询也出现异常长延迟。
最终原始结果为 37/37、native exit 0。未删改日志、缩小测试集或将这段耗时改写为普通耗时。

本轮证据绝对根：`D:/majiang-wt/ci-direct-xml-ds-20261028/artifacts/caller-direct-xml-20261028`。
下表路径相对该根；`direct-xml-audit.json` 包含全部 11 个程序集的节点数及完整 XML SHA。

| 产物 | SHA-256 |
|---|---|
| `direct-xml-audit.json` | `B0D8CD76F96D5DD3F2E20AA5EB6C66685F1D6DD7D926AB2831F95C873E982A57` |
| `unified/summary.log` | `80587440D3ADC461E04342DE30EF721CDD4A594C7B9C286A1481A98057EBB82C` |
| `table/wrapper-summary.json` | `378598D4F1A36C495199EEB794AB79EE8CB6A8C15DAB1FB8BE68A7C6DC8DAEE9` |
| `table/manifest.sha256.json` | `E8FB467B1AA7539498303CB34659514C561A3551FA7B047FE0377E04EACE8018` |
| `diagnostics-console.log` | `DCAF363ADACDA17DE2F9FEA36013A3A32FA82B58EAF8D948A71ED450ECF6B0F8` |

结论：本任务范围独立验收 **PASS**，可批准并并入既有唯一 PR20。此次是本地源码验收；
新发布头的远端 CI 必须单独记录，不把本地结果倒填为远端结果。Windows Player 报告等已接受
同批改动沿用 PR20，未接受的并行实现不混入。V4/G1/G2–G5 与 181 classified 边界不变。
