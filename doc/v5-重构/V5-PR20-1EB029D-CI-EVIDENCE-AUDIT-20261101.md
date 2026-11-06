# PR20 精确头 `1eb029d` 两次既有 CI 原生证据独立审计

日期：2026-11-01（Asia/Shanghai）。审计工作树：`D:/majiang-wt/pr20-1eb-ci-audit-ds-20261101`，
分支 `wt/v5-pr20-1eb-ci-audit-ds-20261101`，创建自 accepted `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
冻结审计目标：`1eb029d34378a587bc36b812baae4cef26f16665`（PR20 head，只读）。

**结论边界：本报告只审计该冻结头的两次既有 CI 的 .NET/PostgreSQL 原生证据。不是 C01/C02/C05
验收，不是 C03 正式集成，不是 G2 批准，不宣布任何任务通过，不修改 PR/计划/调度。**
在本审计范围内：两次 run 的构建、14 个测试工程逐例结果、退出码、12-DLL 插件闭包、真实 PostgreSQL
启动/重启/跨进程读写均 **PASS**；未发现 FAIL；未发现本轮实际证据中的“零总数、缺文件、重复计数、
旧 DLL/旧日志、永远 skip、空 pass”。另有 3 项 MINOR 证据/门禁问题、4 项 NOT_RUN 和 1 项历史未解失败
（BLOCKED），详见 §11–§12。

## 1. 冻结目标与远端一致性

2026-11-01 实际读取（未改动远端）：

| 项 | 值 |
|---|---|
| PR20 | Draft OPEN，`feat/v5-post-g1-batch -> v5`，mergeStateStatus `CLEAN` |
| PR20 headRefOid | `1eb029d34378a587bc36b812baae4cef26f16665` |
| `refs/pull/20/head` | `1eb029d34378a587bc36b812baae4cef26f16665` |
| `refs/heads/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| `refs/pull/24/head` | `81fa51bc43bbab4b33cbae7ad6f003e082892f3a` |

审计期间 PR20 头未移动；本报告不自动扩大或改变范围。工作树 HEAD `8fd7c6c`、分支
`wt/v5-pr20-1eb-ci-audit-ds-20261101`、开始时 `git status --porcelain` 为空。

## 2. 两次 run 的真实身份

| run | event | job | attempt | conclusion | 创建/完成（UTC） |
|---|---|---|---:|---|---|
| [41235402555](https://github.com/liuchangready/majiang-game/actions/runs/41235402555) | `pull_request` | 124638002614 | 1 | success | 19:41:32 / 20:01:54 |
| [41235398157](https://github.com/liuchangready/majiang-game/actions/runs/41235398157) | `push` | 124637989240 | 1 | success | 19:41:29 / 20:02:53 |

两 run 的 `head_sha` 元数据均为 `1eb029d34378a587bc36b812baae4cef26f16665`，workflow 均为
`.github/workflows/v5-verify.yml` / `v5-verify`，`run_attempt=1`，无重试。`gh pr checks 20`
显示两条 `verify pass`，与实际 job 结论一致。

各 step（API 返回；两次均为 success）：

| # | step | run A | run B |
|---:|---|---|---|
| 1 | Set up job | success | success |
| 2 | Checkout | success | success |
| 3 | Setup .NET 8 SDK | success | success |
| 4 | Verify native failure exit propagation | success | success |
| 5 | Run unified V5 verification | success | success |
| 6 | Upload verification logs and test-framework diagnostics | success | success |
| 11 | Post Setup .NET 8 SDK | success | success |
| 12 | Post Checkout | success | success |
| 13 | Complete job | success | success |

API 的 step 编号从 6 跳到 11，7–10 不在返回数组中；workflow 本身声明 5 个 step（Checkout、
Setup .NET、native-exit、unified、upload），job 日志也没有缺失的步骤内容。这是 GitHub step
编号的元数据现象，不是漏执行（见 F6）。

## 3. Checkout provenance：两次 run 不是同一个 checkout 对象

- **run A（pull_request）** job 日志实际执行：

  ```text
  fetch ... +e5f0d248d6be0929681fc2fb9ffdfa8a1fc072d5:refs/remotes/pull/20/merge
  HEAD is now at e5f0d24 Merge 1eb029d34378a587bc36b812baae4cef26f16665 into 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
  ```

  artifact `summary.log:3` 与 `run-info.txt` 记录 `head=e5f0d248...`、`GITHUB_SHA=e5f0d248...`、
  `GITHUB_REF=refs/pull/20/merge`。这是 GitHub 为 PR 生成的合成 merge commit，**不是字面上的
  `1eb029d` checkout**。

- **run B（push）** job 日志实际执行：

  ```text
  fetch ... +1eb029d34378a587bc36b812baae4cef26f16665:refs/remotes/origin/feat/v5-post-g1-batch
  log -1 --format=%H -> 1eb029d34378a587bc36b812baae4cef26f16665
  ```

  artifact `summary.log:3` 记录 `head=1eb029d...`、`branch=feat/v5-post-g1-batch`。

独立核对 merge 对象：

```text
1eb029d^{tree}                  = 275806a74eb5ee27b1a2c6334aa584f3e087408d
refs/remotes/pull/20/merge^{tree} = 275806a74eb5ee27b1a2c6334aa584f3e087408d
merge parents                   = 8fd7c6cb... 1eb029d...
git merge-base --is-ancestor 8fd7c6cb... 1eb029d... -> 0
```

因此 run A 测试的是 **与冻结 head 完全相同的 tree**（tree hash 相同），但 provenance 上的
被测试 commit 是 `e5f0d24`；run B 才是字面上的 `1eb029d`。这是 F1 的事实基础。

## 4. artifact 完整性与下载

| run | artifact | id | 字节 | 下载 zip SHA256 | API digest |
|---|---|---:|---:|---|---|
| A | `v5-verify-logs-41235402555-1` | 13056351328 | 4,502,035 | `9265FC0A77F729E64DA9FB58CA053EDF6BA65DFDD50ABDE4A7437E3B2021DF68` | 相同 |
| B | `v5-verify-logs-41235398157-1` | 13056386306 | 4,502,639 | `AE8001ECD776492EB1D728F4879CE8A16CF590FA0F081E5D8F6ADEC6A41453AB` | 相同 |

两 zip 解压后各 **270 个文件**：A 13,187,693 bytes，B 13,187,855 bytes。artifact 名称含
`run_id`/`run_attempt`，attempt=1，不会与重试混淆。

下载使用 `curl.exe` 保留二进制；最初 `gh api .../zip > file` 被 PowerShell 文本重定向破坏，
已改用 curl 并重新校验到 API digest 完全一致，未使用被破坏的文件作为证据。完整命令与 URL
见 `download-log.md`，所有文件的 SHA256/长度见 `EVIDENCE-SHA256.csv`（1226 个文件，
清单自身 SHA256 `1076BA570E01814AA4C5CC5EE643CBD5A8760C27270EB1F2F4966FAADFEDD8F0`）。

证据内部哈希校验：

- table `manifest.sha256.json`：**152/152** 条目长度+SHA256 全部一致（含 22 个
  `tmp/VBCSCompiler` 长路径文件；使用 `\\?\` 长路径 IO 复核）。
- unified 14 个工程：XML 实际 SHA256 与各自 `direct.meta.txt` 的 `result xml sha256` **14/14** 一致。
- unified lobby PG `pg/evidence-sha256.txt`：**12/12** 一致。
- 14/14 `direct.meta.txt` 记录 `exit code: 0` 且 `result xml gate: PASS`。

## 5. 逐例原生结果的独立重算

逐工程解析 `direct-xml/<project>/direct-results.xml`，独立统计 test 节点、重复 id、
collection aggregate、assembly aggregate，并与 `direct-*.log` 的 console summary、
`direct.meta.txt` 的 total/exit、以及 XML SHA256 交叉核对。两次 run 结果相同：

| 工程 | Total | Passed | Failed | Skipped | NotRun | XML nodes | run A XML SHA256（前 12） |
|---|---:|---:|---:|---:|---:|---:|---|
| Mahjong.Ai.Tests | 444 | 444 | 0 | 0 | 0 | 444 | `8D7CD4FA8A74` |
| Mahjong.Architecture.Tests | 132 | 132 | 0 | 0 | 0 | 132 | `1A55EDB03CBF` |
| Mahjong.Client.Tests | 182 | 182 | 0 | 0 | 0 | 182 | `C88AFC4664AA` |
| Mahjong.Client.Wss.Tests | 82 | 82 | 0 | 0 | 0 | 82 | `3FE4E7FCBDF8` |
| Mahjong.Contracts.Tests | 152 | 152 | 0 | 0 | 0 | 152 | `4753264B768D` |
| Mahjong.Domain.Tests | 529 | 529 | 0 | 0 | 0 | 529 | `211D7F77F6DE` |
| Mahjong.Infrastructure.Tests | 42 | 42 | 0 | 0 | 0 | 42 | `AEB979555B68` |
| Mahjong.LobbyService.Tests | 89 | 89 | 0 | 0 | 0 | 89 | `91E5A0E79B36` |
| Mahjong.LocalHost.Tests | 127 | 127 | 0 | 0 | 0 | 127 | `7B2F070F2A52` |
| Mahjong.P5.4b.B1.HttpsAdmission.Tests | 54 | 54 | 0 | 0 | 0 | 54 | `424DEA8332E1` |
| Mahjong.TableRuntime.Tests | 483 | 483 | 0 | 0 | 0 | 483 | `15F65DCDF1A0` |
| Mahjong.TableService.DurableHost.Tests | 59 | 59 | 0 | 0 | 0 | 59 | `DFB971B2D58B` |
| Mahjong.TableService.Tests | 203 | 203 | 0 | 0 | 0 | 203 | `D54AA0EE1639` |
| Mahjong.Unity.TableModel.Tests | 235 | 235 | 0 | 0 | 0 | 235 | `CB03C5207E7E` |
| **合计** | **2813** | **2813** | **0** | **0** | **0** | **2813** | |

独立复算断言（A/B 均通过）：

- assembly `total` = test 节点数 = 2813；collection totals 之和 = test 节点数。
- assembly `passed/failed/skipped/not-run` 与节点逐项一致；`errors=0` 且无 error 节点。
- 无重复 test id，无未知 `result` 值；每个工程 `total > 0`。
- XML 的 assembly `@name` 完整路径 = `direct.meta.txt` 中实际执行的 DLL 路径（14/14）。
- console summary 与 XML 的 Total/Errors/Failed/Skipped/NotRun 一致（14/14）。
- 14/14 meta `exit code: 0`，14/14 gate PASS；14/14 meta 记录 `TestResults purge before this
  invocation: cleared - not present before the run`，即本轮没有复用旧 result XML。
- A/B 的 2813 个“测试名+result”集合完全相同（onlyA=0、onlyB=0）；本轮观测到 test id 集合
  不同，因此跨 run 身份以“名称+result”结算，不以 id 结算。A/B 首个/最后一个 test 的
  `start-rtf` 窗口分别约为 `03:44:39–04:01:23` 与 `03:45:20–04:02:07`（+08:00），
  是两次独立执行，不是复制同一份 XML。

### 5.1 table 专用阶段与计数口径

| 阶段 | XML | Total | Passed | Failed | Skipped | NotRun | 说明 |
|---|---|---:|---:|---:|---:|---:|---|
| table `tests` | `tests.xml` | 42 | 42 | 0 | 0 | 0 | `Mahjong.Infrastructure.Tests` 全量 |
| table `tests-targeted` | `tests-targeted.xml` | 13 | 13 | 0 | 0 | 0 | `PostgresTableStoreTests` 子集 |

- 13 个 targeted 案例全部包含在 42 个案例中；42 个案例与 unified 的
  `Mahjong.Infrastructure.Tests` 42 个案例名称集合完全一致。
- 因此**唯一测试案例数为 2813**；本 job 的测试执行次数为 `2813 + 42 + 13 = 2868`
  （Infrastructure 42 在 table 阶段与 unified 阶段各执行一次，targeted 13 是 42 的子集）。
  报告不把 42/13 再加到 2813 上冒充“额外覆盖”。
- 仓库 `dotnet/tests/` 下 21 个 `.csproj`：14 个 `*.Tests` 全部被发现并执行；另 7 个
  worker/probe 作为 build-only helper，不贡献测试计数。没有遗漏的测试工程。

## 6. 门禁与原生结果工具检查

workflow 冻结内容只有以下执行步骤（`.github/workflows/v5-verify.yml:41-71`）：

```text
actions/checkout@v4
actions/setup-dotnet@v4 (dotnet-version: 8.0.426)
./tools/ci/test-v5-native-exit.ps1
./tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -RunUnifiedVerification \
  -LogDir artifacts/verify-v5/table -UnifiedLogDir artifacts/verify-v5/unified
actions/upload-artifact@v4 (if: always(), retention 30 days)
```

统一 verifier 的直接运行门禁（`tools/verify-v5.ps1`）确实是结构化的：

- `Read-DirectResultXml`（612–788）要求恰好一个 `<assembly>`、xUnit.net v3、完整
  total/passed/failed/skipped/errors/not-run 属性、test id 唯一、collection 汇总与节点一致、
  assembly 汇总与节点一致、`errors` 与 error 节点一致。
- `Test-DirectInvocationEvidence`（790–859）拒绝：缺失/过期 XML、LogDir 外 XML、汇编身份
  不符、native exit 非 0、`Total <= 0`、Failed/Errors/Skipped/NotRun 非 0、console 缺失或
  与 XML 不一致。
- `Get-DirectRunnerCounts`（576–600）从 console summary 推导 Passed；不一致会被上述
  XML/console 交叉检查拒绝。
- 构建失败/未构建时拒绝执行旧 DLL（1418、1603），zero total 再拒绝一次（1680）；
  OVERALL/exit 在 1903–1913 按 FAIL=1、BLOCKED=2、PASS=0 传播。

两类已发现的边界（不影响本轮已取得的 42/2813 计数，但属于门禁深度/覆盖问题）：

1. table 阶段的 `Get-XmlCounts`/`Test-XmlCountsPass`
   （`tools/ci/verify-v5-table-postgres.ps1:725-753`）只读 assembly 属性，不数 `<test>` 节点、
   不查重复 id、不校验 collection。其 7 例自检（755–784）不包含“只有 assembly 汇总的绿色 XML”
   和“重复 id 的绿色 XML”。复现见 §11 F2。
2. CI 没有调用 `tools/ci/test-v5-direct-results.ps1`（19 例 direct 门禁自检）或
   `test-v5-dotnettest-results.ps1`；整个 `tools/`、`.github/` 中没有对它们的引用。CI 实际只
   跑 table 内的 7 例自检。见 §11 F3/F4。

`dotnet test` 路径（`Test-DotnetTestInvocationEvidence`，861–972）本轮 **NOT_RUN**：workflow
默认 `RunnerMode=direct`，artifact 中没有 `dotnetTest-xml/`；所有计数来自 direct runner 的
逐例 XML，而不是 dotnetTest 的 TRX/console 摘要。因此本报告不冒称 dotnetTest 门禁已被本轮
CI 覆盖。

## 7. 构建、warning/error 与 12-DLL 闭包

15 份构建相关日志（sln-build、11 份单工程 build、table 的 build-tests/build-worker、
plugin-copy）全部 `0 Warning(s)` / `0 Error(s)`；按 `warning CSxxxx`/`error CSxxxx` 等诊断
模式扫描为 0 命中。唯一的 `##[warning]` 是 GitHub Actions 的 Node.js 20 deprecation
平台提示，不是仓库构建警告。

12-DLL Unity 插件闭包（`plugin-copy.log` + `shared-plugin-provenance.json`）独立复核：

- 输出 **19 个文件 = 12 个 DLL + 7 个 PDB**；12 个 DLL 为 7 个 Mahjong 自有程序集
  （Domain、Contracts、Contracts.Wire、TableRuntime、Client、LocalHost、Ai）加 5 个包程序集
  （Google.Protobuf 3.28.3、System.Buffers 4.4.0、System.Memory 4.5.3、
  System.Numerics.Vectors 4.4.0、System.Runtime.CompilerServices.Unsafe 4.5.2）。
- 19 个输出文件的长度/SHA256 与 provenance 逐项一致；无额外文件；7 个
  `requiredOwnAssemblies` 全部存在。
- provenance 记录 `build.mode=forced-clean-outputs`、`forcedRebuild=true`、清理 14 个
  bin/obj 目录、149 个输入、19 个输出、build exit 0；构建日志 0 warning/0 error。
- 不要把“12 DLL 闭包”误读为 12 个测试程序集：本轮实际执行的是 **14 个测试 DLL**。

## 8. 真实 PostgreSQL 证据

### 8.1 table 集群（crash/restart/restore）

| 证据 | run A | run B |
|---|---|---|
| 集群根 | `artifacts/p52-pg-cluster-20261031-194214` | `...-194221` |
| 版本/监听 | PostgreSQL 16.10，`127.0.0.1:55592` | 同版本/端口，独立集群 |
| initdb | `initdb.log` 成功，data directory 在 worktree artifacts 内 | 独立 logs/data |
| 首次 postmaster | 19:42:28.130 pid 8300 | 19:42:33.906 pid 5600 |
| writer | pid 9176，`killedUtc=19:43:26.3798401Z`，forced，exit -1 | pid 8664，`killedUtc=19:44:10.0329938Z` |
| writer 持久化标记 | state=1 seq=2，eventCount=2，outbox SHA `c0b108923986...` | 同结构，marker SHA 不同 |
| 真实重启 | immediate shutdown 19:43:26.461；新 postmaster 19:43:26.671 pid 2500；`automatic recovery in progress`、`redo starts`、`ready` 19:43:27.642 | 19:44:10.516 自动恢复、redo，新 postmaster 就绪 |
| 重启后 reader | 独立进程 `READER-OK`：state=1 seq=2，`receiptStatus=Applied`，`terminal=true`，outbox hash 与 marker 一致，`recovered=true`，ownerEpoch=2 | 同结构，独立进程/独立数据 |
| 备份/恢复 | `pg_dump --format=custom` → `table_store.dump`（44,697B）；drop/create 空库；`pg_restore`；grant；restore reader `recovered=true`，ownerEpoch=3 | 同流程，独立文件 |
| 清理 | `pg-cleanup-stop` 成功；ownership-verified cluster removed | 独立清理成功 |

支持“跨进程、真实 PG”的额外事实：

- `Mahjong.Infrastructure.CrashWorker/Program.cs` 使用 `Npgsql` 和
  `MAHJONG_P52_TEST_POSTGRES`/`_ADMIN`；writer/reader 是 wrapper 用 `Start-Process`/
  `Invoke-Native` 启动的两个独立 `dotnet` 进程（wrapper 894–925）。
- `pg/postgres.log` 记录 19:50–19:59 期间 unified 阶段的真实连接/语句，包括预期的
  least-privilege `permission denied for schema table_store` 和 advisory-lock cancel；
  这些是负例探针，不是测试失败。
- 13 个 `PostgresTableStoreTests`（`tests-targeted.xml`）全部 Pass；42 个 Infrastructure
  测试包含这些 PG 用例。因此 green 不是纯内存测试或元数据替代。

### 8.2 lobby 集群（unified 阶段）

- `pg/postgres-evidence.jsonl` 首事件：PostgreSQL 16.10，`127.0.0.1:55593`，
  database `mahjong_lobby_test`，owner `mahjong-p53-pg`，独立临时 data directory。
- `pg/wrapper-summary.json` 阶段全部 PASS：precheck、start-postgres、ownership-marker、
  environment、migrate（`001_lobby.sql` 连续执行两次均 exit 0）、verify、archive、
  cleanup、env-restore、write-evidence；`rootDeleted=true`。
- 14 条 PG 证据事件覆盖：`migration.repeated`（记录 migrationRuns=3）、跨连接 row lock
  （observedUngrantedLocks=1，blocked 265ms）、跨 room join 竞争 3 轮、receipt 重放、
  事务回滚、ticket 单次消费、room fill（6 次尝试/3 成功/3 拒绝）、terminal projection 等。
- 该 lobby 集群与 table 集群端口/根目录/owner 均不同；不是同一个数据库的重复声明。

## 9. 历史失败 run `39970580589`（保留，不关闭）

| 项 | 值 |
|---|---|
| run | 39970580589，`pull_request`，head `7a988d2b351470327fe2c4f51c19ce394dcb99a9`，attempt 1，failure |
| 失败步骤 | job 120471866324，`Run unified V5 verification` |
| 失败工程 | `Mahjong.Client.Tests`：Failed 1 / Passed 131 / Total 132 |
| 日志指向 | `dotnet/tests/Mahjong.Client.Tests/bin/Release/net8.0/TestResults/Mahjong.Client.Tests_net8.0_x64.log` |
| artifact | `v5-verify-logs` id 12509740305，869,710B，解压 27 个文件；**没有** TestResults 逐例框架日志 |
| 历史 zip SHA256 | `9567FFDD3CD15B5AD3E8C909204469C47A5A29607EEE1EA094439395CAFAB00A` |

本轮没有重跑该 run、没有下载/伪造缺失的框架日志，也不把当前新 run 的绿色解释为该历史失败的
根因已解决。历史根因仍为 **BLOCKED（缺框架日志）**，只保留上述事实。

## 10. 本轮证据分类

| 检查 | 状态 | 依据 |
|---|---|---|
| PR20/远端头一致，未移动 | PASS | `gh pr view`、`git ls-remote` |
| run/job/attempt/workflow 结论 | PASS | 两 run `success`，attempt 1，5 个声明步骤及 job 的 setup/post/complete 步骤全 success |
| artifact 下载完整、digest 一致 | PASS | zip SHA256 = API digest；270 文件/run |
| 14 工程逐例 XML 计数 | PASS | 2813/2813，0 fail/skip/notrun |
| console/meta/XML/exit 交叉核对 | PASS | 14/14 meta exit 0 + gate PASS，14/14 console 一致 |
| 14 工程 XML→实际 DLL 路径绑定 | PASS | 14/14 assembly 路径等于 meta command 的 DLL 路径 |
| table 42/42 与 targeted 13/13 | PASS | XML 42/13 节点，console 一致；13 是 42 子集 |
| build warning/error | PASS | 15 份构建日志均 0/0 |
| 12-DLL 插件闭包 | PASS | 12 DLL/19 文件哈希与 provenance 全一致，无额外文件 |
| table PG 真实启动/重启/跨进程读写/恢复 | PASS | PG 16.10、55592、writer kill + immediate restart + redo + reader + dump/restore |
| lobby PG 迁移/并发/回执/清理 | PASS | PG 16.10、55593、14 条证据事件、owner 清理成功 |
| table XML 门禁对“仅汇总/重复 id” | **MINOR（门禁深度）** | 见 F2；不影响本轮实际 42 个逐例节点 |
| direct/dotnetTest 19 例门禁自检 | **NOT_RUN** | CI 未调用 |
| `git pins` 历史 blob 校验 | **NOT_RUN** | summary 自报 NOT_RUN |
| dotnetTest 模式 | **NOT_RUN** | 本轮为 direct |
| Unity/Player/Android/adb/设备 | **NOT_RUN** | 明确不在本审计范围，未运行 |
| 历史 39970580589 根因 | **BLOCKED** | 缺 TestResults 框架日志 |

本审计范围内 **没有 FAIL 的业务/测试证据，也没有 BLOCKED 的本轮 CI 证据**；F2 是门禁代码的
MINOR（防御深度）问题，不是本轮 42/2813 结果的失败。

## 11. 本轮已知问题清单

### F1（MINOR，provenance）PR 事件 run 测试的是合成 merge commit，而非字面 head

- 位置：`.github/workflows/v5-verify.yml:41-42`（默认 `actions/checkout@v4` + PR 事件）。
- 复现：run A job log 的 `fetch ... +e5f0d24...:refs/remotes/pull/20/merge` 与
  `HEAD is now at e5f0d24 Merge 1eb029d... into 8fd7c6c...`；artifact A `summary.log:3`
  `head: e5f0d248...`；artifact B `summary.log:3` `head: 1eb029d...`。
- 实际：A 的受测 commit（checkout 与 artifact 记录）为 `e5f0d24`；期望（严格字面）：`1eb029d`。
- 但独立计算的 tree 完全相同：`1eb029d^{tree} = e5f0d24^{tree} =
  275806a74eb5ee27b1a2c6334aa584f3e087408d`，且 `8fd7c6c` 是 `1eb029d` 的祖先，
  所以 A 的源码内容就是冻结 head，**不是**错误测试了别的代码。
- 证据 SHA：A zip `9265FC0A...`、A summary `0517E3D7E5486D652CE3F4111D29C04E330EA6954ABCBDA3D2F671EEE49304DB`、
  B summary `EE2F264861BF2247909397DEE50DA7A36DAE876FA1284F666E25737708BB4927`。
- 验收断言：若要求“字面 SHA checkout”，必须引用 run B（push）或附加 tree 相等证明；如需
  PR 事件也字面 checkout，应在 workflow 设置
  `ref: ${{ github.event.pull_request.head.sha }}` 并产生新 run。本轮未改 workflow、未重跑。

### F2（MINOR，门禁深度）table 阶段 XML 门禁只看 assembly 汇总

- 位置：`tools/ci/verify-v5-table-postgres.ps1:725-753`（`Get-XmlCounts`/`Test-XmlCountsPass`），
  自检 `755-784` 仅 7 例。
- 复现（本轮实际执行，evidence `gate-gap/RESULT.txt`，SHA256
  `57EF74F294D6BE50F1145D671D677280E48C5614B580C9D8D455246646FE4C4D`）：

  ```text
  CASE assembly-only-green: table_gate_green=True unified_gate_ok=False
    unified_reason=... assembly total=7 but 0 test case node(s) exist
  CASE duplicate-id-green: table_gate_green=True unified_gate_ok=False
    unified_reason=... duplicate test id 'same'
  ```

- 实际：table 门禁对“只有 assembly 汇总、无 `<test>`”的 XML 和“重复 test id”的 XML 都返回
  green；期望：拒绝这两类输入（与 `Read-DirectResultXml` 一致）。
- 本轮没有 false green：实际 `tests.xml` 有 42 个唯一节点，`tests-targeted.xml` 有 13 个唯一
  节点，且 unified 的严格门禁对同一 Infrastructure 42 个节点已 PASS。
- 验收断言：table 阶段应复用结构化 XML 门禁（节点计数、collection 汇总、唯一 id、assembly
  身份）或扩展 7 例自检；在修复前，table 42/13 的结论必须同时引用逐例节点/独立复算，不能只
  引用 `Test-XmlCountsPass`。

### F3（NOT_RUN，覆盖）19 例 direct 门禁自检与 dotnetTest 门禁自检未在 CI 执行

- 位置：`tools/ci/test-v5-direct-results.ps1`（19 例，含 missing/empty/malformed/zero/
  assembly-only/wrong identity/count mismatch/failed/errors/skipped/not-run/stale/outside/
  console mismatch 等）；`tools/ci/test-v5-dotnettest-results.ps1`。
- 复现：在两 run 的 job step 列表和完整 job 日志中没有这两个脚本；在冻结树的
  `tools/`、`.github/` 全量搜索没有调用引用。CI 实际只有 table 内 7 例自检。
- 实际：NOT_RUN；期望：CI 显式执行至少 19 例 direct 门禁自检，dotnetTest 路径若声明支持亦应
  有自检。
- 证据：冻结 `test-v5-direct-results.ps1` SHA256 `4CACA2108751C8779CAC405B12881D495553294C6E0CF78DBA7EC0405F0D75A1`，
  `test-v5-dotnettest-results.ps1` SHA256 `A3402DCD81FC63B331557545F0614EE8BFE9827B19D22566670189A84AFBF17E`；
  job 日志 A/B 无调用行。
- 验收断言：不能把“19 例门禁矩阵已由 CI 验证”写入验收；本轮只验证了 7 例 inline 自检。

### F4（NOT_RUN，子门禁）`git pins` 历史冻结 blob 未校验

- 位置：`verify-v5.ps1:1822` / summary 输出；run A `summary.log:17`：
  `git pins : NOT_RUN (historical freeze blobs not requested/fetched; current bytes and
  embedded provenance verified offline)`。
- 实际：仅校验当前 fixture 字节和嵌入式 provenance；期望（若声明历史 pin）：拉取并比对
  历史 freeze blob。
- 证据：run A `summary.log` SHA256 `0517E3D7E5486D652CE3F4111D29C04E330EA6954ABCBDA3D2F671EEE49304DB`，
  第 17 行自报 NOT_RUN。
- 验收断言：本轮的 golden fixture 证据是“当前字节 + 离线 provenance”，不是完整历史 git pin；
  不得扩写为历史 blob 已验证。

### F5（INFO，可复现性）workflow 写 8.0.426，实际 SDK 8.0.427

- 位置：`.github/workflows/v5-verify.yml:47` `dotnet-version: 8.0.426`；`global.json`
  `version=8.0.426`、`rollForward=latestPatch`；artifact `sdk-diagnostic.log` 实际
  `dotnet --version 8.0.427`，构建日志也写 `8.0.427`。
- 实际：`latestPatch` 选择了镜像上更高的 8.0.427；期望：若必须精确 8.0.426，应改
  `rollForward=disable` 或固定 SDK。按当前 `global.json` 这是预期行为，不是失败。
- 证据：`sdk-diagnostic.log` SHA256 `9501F34B6D47D529002ECFB9B38334761BB25C5C00427872A9DFADA911226A2F`，
  记录 `8.0.427`。
- 验收断言：报告实际 SDK 为 **8.0.427**，不要写“用 8.0.426 构建”。

### F6（INFO，元数据）job step 编号 7–10 缺失

- 位置：jobs API step 数组；workflow 声明 5 个 step，API 从 6 跳到 11。
- 实际：所有 workflow step 和 Post/Complete 均 success，日志无缺失步骤；这是 GitHub 编号
  现象，不是漏执行。
- 证据：jobs JSON A `89574425DB477454281261FF02E8E0A84069354AE56D24FEF156CC7C00A4FE35`，
  B `1BBE565C3ADA6A213A835130ACA111A9D41E2E5EFAB8341650E6461CD98B57FC`。
- 验收断言：以 step 名称/conclusion 和 job 日志为准，不以编号连续性推断步骤缺失。

### F7（INFO，artifact 卫生）table artifact 含构建临时分析器 DLL

- 位置：`verify-v5/table/tmp/VBCSCompiler/...` 22 个文件，合计 7,319,720 bytes（解压后占
  artifact 13.18MB 的约 55%）；另有 7 个 `tmp/xml-selfcheck/*.xml` 负例 fixture。
- 实际：这些是 Roslyn 编译器分析器缓存和门禁自检 fixture，被上传且被 152 条 manifest 覆盖；
  期望：只上传证据/必要日志可减小 artifact，但不影响本轮正确性。
- 证据：table `manifest.sha256.json` SHA256 `CC5B0D3F98A0C1427743228F2989287448477EEE477D8EEC0A63722B07A45E2E`，
  152/152 校验通过。
- 验收断言：manifest 152/152 完整；artifact 中这些文件不是测试结果，不能计入测试数。

### F8（MINOR，证据完整性）native-exit 的 timeout 合并日志缺失

- 位置：artifact `native-exit-eb5b1f1cc47d4b9b956f7775af115aa6/`。
- 实际：`timeout.log` 不存在；`timeout.log.stdout`/`.stderr` 各 15 bytes（子进程已启动，
  `stdout code=0`），`results.json` 的 timeout 用例 `Pass=true`；脚本传入的合并日志路径
  `-LogPath .../timeout.log` 没有落盘。
- 为什么仍可接受：timeout 断言来自生产 `Invoke-Native` 抛出的 `Command timed out`，且
  stdout/stderr 已保留；没有把缺失文件当作通过。
- 证据：`results.json` SHA256 `3886116BC8BC9272683E1D9E16E2402CE6F1809EEC8EBC2BD8A98ADD1D0E3DD0`；
  `timeout.log.stdout` SHA256 `37C0EAAA6691E31EDAACB464BC318C2E59B618EFF3AB6F338DCC949FC878440A`，
  `timeout.log.stderr` SHA256 `ED99DA26D6F9996D206EF47D8C22D4DAF8FD0FC60D7E58416DD5298CC880E539`。
- 验收断言：timeout 只有原始 stdout/stderr + results.json，无合并日志；若要求完整合并日志，
  应在超时路径落盘后再断言。其余 5 个 native-exit 用例（0/1/2/17/forced-writer）均有合并日志
  和结果。

### F9（历史 BLOCKED）`39970580589` 缺框架日志

- 见 §9。历史根因未解决；当前 run 的绿色不构成该历史失败的关闭证据。

## 12. 关键证据路径、命令与真实 native 退出

证据目录（GUID 独占，`build/` 已 gitignore）：

```text
D:/majiang-wt/pr20-1eb-ci-audit-ds-20261101/build/pr20-1eb-ci-audit-a429359db3e54d0fb5506543793dd30b/
```

主要文件：

| 文件 | 说明 |
|---|---|
| `logs/job-124638002614-run-41235402555-pr.log` | run A 完整 job 日志，22,542B，SHA `FFDE9649...` |
| `logs/job-124637989240-run-41235398157-push.log` | run B 完整 job 日志，24,200B，SHA `9B3830B4...` |
| `artifacts/runA/`、`artifacts/runB/` | 两 run 各 270 个解压文件 |
| `artifacts/*.zip` | 与 API digest 一致的原始 artifact |
| `historical/` | 历史失败 run 的 zip 与 27 个文件 |
| `recount-runA.json`、`recount-runB.json` | 逐工程独立复算结果 |
| `binary-binding.txt` | 14/14 XML assembly 路径 = 实际 DLL 路径 |
| `gate-gap/RESULT.txt` | table 门禁差异复现 |
| `native-exits.txt` | 本轮审计命令的真实 native 退出 |
| `EVIDENCE-SHA256.csv` | 1226 个证据文件的长度/SHA256 |
| `download-log.md` | 下载命令/URL/API/attempt |

本轮关键命令的真实 native 退出（完整列表见 `native-exits.txt`）：

```text
git merge-base --is-ancestor 8fd7c6c 1eb029d              -> 0
git rev-parse 1eb029d^{tree} / merge^{tree}               -> 0
recount runA / runB                                       -> 0
compare-runs / crosscheck                                 -> 0
verify table manifest (152/152)                           -> 0
verify binary binding (14/14)                             -> 0
gate-gap reproduction                                     -> 0
tar -tf artifact A / B                                    -> 0
```

说明：系统默认禁止执行 PowerShell 脚本，所有本地脚本均显式使用
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File ...`。未运行 Unity/Player/adb/设备，
未抢占 `Local\MahjongV5P45UnityValidation`，未修改他人 worktree，未 rerun/dispatch 远端 CI，
未创建 CI 分支、未 push 任何代码。

## 13. 非批准声明

- 本报告只描述 `1eb029d` 的两次既有 CI 原生证据审计结果；**不宣布 C01/C02/C05 通过，不宣布
  C03 集成或 G2 通过，不批准任何任务，不 record MR，不修改 PR/计划/调度/complete.flag**。
- C01 后继、C02 r4、C05 r2 的状态按调用方台账保留；本轮未唤醒、返修、重定向或删除任何既有
  任务/线程。
- 历史 `39970580589` 的失败与缺失框架日志保留为历史；当前两次新 run 的绿色不构成其根因已
  解决的证据。
- 交付后等待调用方独立复核；若需正式 C03/G2 结论，应由调用方按各自门禁另行裁决。

## 14. 调用方独立复核补记（2026-11-01）

原始交付提交为 `a44dd7b86badb3d3a19a8850862aafa04bb04f57`。调用方重新从 GitHub GET 两次
run/jobs/artifacts、完整 artifact/job-log ZIP 与合成/source commit 元数据，在 E 盘独立工作树
实际执行解析和门禁负例。两 run 各 2813 个唯一 raw ID、14 个工程、196 个 collection，逐例全通过；
270 个归档文件逐项长度/哈希、152 项 table 清单、19 项插件输出、28 组 XML/实际命令/console/SHA
绑定、真实 PG crash/reader/restore 与历史失败缺日志事实均与本报告一致。没有重跑远端 CI 或 PG。

调用方仅校正三处声明步骤计数为 5，并明确上表哈希列展示 run A；job API 的 9 条步骤包括自动
setup/post/complete。验收报告范围通过，原生证据及历史缺口保留。本报告仍仅覆盖冻结 `1eb029d`，
后来的 PR20 提交及 C03/C04/C05/G2-G5 状态须分别判断。
