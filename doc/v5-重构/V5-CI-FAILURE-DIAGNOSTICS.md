# V5 CI 失败诊断证据留存 — 改动记录与实测报告

日期:2026-10-24
范围:**独立 CI 诊断证据留存任务**,与在途 P3.1、真实 PostgreSQL 返修(thread_20261023_230533_bb60,他线程所有)、WSS 补验三条范围零交集。本文件只记录本次改动与实测,不是迁移方案、不是验收报告。
基线:已审查远端 PR19 head `5770b8a37e76dafa396cdcada7028269e6939f47`(分支 `wt/v5-ci-diagnostics-20261024`,工作树 `D:\majiang-wt\ci-diagnostics-20261024`,开工时工作树 clean)。
**V4 仍是唯一生产架构。本任务不宣布、也不构成 G1/G5 或任何阶段门槛通过。**

---

## 1. 已确认的证据缺口(缺陷本体)

历史远端 run [`39970580589`](https://github.com/liuchangready/majiang-game/actions/runs/39970580589)(head `7a988d2`,pull_request):

- `Mahjong.Client.Tests` = **131 passed / 1 failed / 0 skipped**(Total 132),整批 FAIL。
- 上传 artifact(`v5-verify-logs/20261023-143333/`)中的 `dotnetTest-Mahjong.Client.Tests.log` 结尾为:
  `error run failed: Tests failed: 'D:\a\...\dotnet\tests\Mahjong.Client.Tests\bin\Release\net8.0\TestResults\Mahjong.Client.Tests_net8.0_x64.log'`
  ——**该框架日志文件本身不在 artifact 内**,workflow 当时只上传 `artifacts/verify-v5/**`,而 `dotnet test`(xunit.v3/MTP,`TestingPlatformDotnetTestSupport=true`)把唯一含失败用例身份的日志写进了 `bin\...\TestResults\`,未被任何步骤收集。
- 完整 job log(run39970580589.log 全文)中 Client 段只有汇总行 `132 / 1 / 0 FAIL`,**无失败用例名、无断言信息**(只读核对,证据位于 `D:\majiang-wt\codex-integration-20261023\artifacts\progress-20261023-2345\`,本次未改动)。
- 同 SHA `5770b8a` 的后续 PR run `39977041658` 与 push run `39977029218` 均 618 passed / 0 failed / **1 真实 PG skip** → FAIL。**Client 132/132 的后续通过不证明旧失败已修复,不得宣称 flaky。**

## 2. 旧 Client 失败根因:**UNRESOLVED**

依据上述直接证据:留存证据中不存在任何失败用例身份(框架日志未上传、job log 无用例名、console 摘要不含名字),因此**无法定位当时的失败用例,也无法提出根因**。本任务不盲目重试 CI,不做无证据猜测。本任务的产出保证:此后任何 `dotnetTest` 模式失败都会把该框架日志随 artifact 上传,同类缺口不再复发。

## 3. 实测确立的 runner 行为(本地实证,非文档推断)

用 `D:\Unity\dotnet\dotnet.exe`(SDK 8.0.426,与 `global.json` 一致)+ `xunit.v3 4.0.2` + MTP 2.5.1 最小探针实测:

| 模式 | 失败行为 | 失败用例身份所在 |
|---|---|---|
| `dotnet test <csproj>`(dotnetTest/auto 路径) | exit 1;console 只有 `Failed! - Failed: 1, ... Total: 2` 汇总 + MSBuild error 行指向 `bin\<cfg>\<tfm>\TestResults\<asm>_<tfm>_<arch>.log` | **该 TestResults 日志**(UTF-16LE+BOM),内含失败用例全名、断言消息、堆栈、逐项计数、底层命令行 |
| `dotnet <dll> -noLogo -reporter default`(direct 路径) | exit 1;**不写任何 TestResults 文件**(实测验证) | console 本身(用例名+断言+堆栈+`Total: ...` 汇总),由脚本捕获为 `direct-<project>.log` |

## 4. 本次改动(3 个文件修改 + 1 个新文件)

### 4.1 `tools/verify-v5.ps1`

1. **运行独占的框架日志收集**:每个测试项目在每次 runner 调用(dotnetTest 与 direct 各自)前删除其 `bin\<cfg>\<tfm>\TestResults\` 与项目根 `TestResults\`,调用后把生成的一切文件按相对路径复制到 `<log dir>\test-results\<project>\`(附逐文件 SHA256)。**运行前清空 ⇒ 运行后找到的必然是本次产物**,旧 TestResults 永远不可能被当作本次证据;收集为 best-effort,任何收集失败写入 `collection-errors.log`,**绝不改变门禁结果、计数或退出码**。
2. **逐次调用归因**:`runs\<project>\<mode>.meta.txt` 记录精确命令行、CWD、退出码、解析计数、head SHA、purge/收集清单。
3. **运行身份**:`run-info.txt`(建目录时即写,连 BLOCKED 早退也留证)记录 UTC/本地时间、`git rev-parse HEAD`/branch(失败原样记录,不伪造)、仅公开 CI 元数据(GITHUB_REPOSITORY/RUN_ID/RUN_ATTEMPT/SHA/REF/RUNNER_OS——**不读取任何 token/secret**)、脚本路径与其 SHA256。LogDir 若已存在自动加 `-N` 后缀,保证每次运行独占目录。summary.log 头部新增 head/branch/ci provenance 行。
4. **`-NoProjectDiscovery`**(新增参数,默认关):跳过 `dotnet\tests|src` 自动发现,仅验证 manifest 项目——供隔离自测使用;生产调用不变(基线运行已验证发现逻辑照常)。
5. **`Resolve-UnderRoot`**:manifest 路径解析对**跨盘绝对路径**返回原路径(`Join-Path D:\repo C:\x` 会错误拼成 `D:\repo\C:\x`,自测首跑暴露,已修),相对路径行为不变。
6. 门禁逻辑(fail/skip/zero/missing/build-fail ⇒ FAIL;退出码 0/1/2)**一行未改**;测试摘要解析、fallback 保护、插件拷贝检查全部原样。

### 4.2 `.github/workflows/v5-verify.yml`

- artifact 更名 `v5-verify-logs-<run_id>-<run_attempt>`(重试互不覆盖);`if-no-files-found: error`(没证据=硬失败,不允许"绿了但无证据");`retention-days: 30`;`if: always()` 保持。上传范围仍为 `artifacts/verify-v5/**`,`test-results\` 与 `runs\` 随时间戳目录整体进入 artifact,相对路径保留,不复制 bin/obj。

### 4.3 `tools/tests/VerifyV5Diagnostics.Tests.ps1`(新)

隔离自测:在**任务自有 TEMP** 下生成一次性 xunit.v3/MTP 探针工程(独立 sln+manifest,`-NoProjectDiscovery -SkipPluginCopyCheck`,生产测试零改动,不触碰 PG),用与生产完全相同的 runner 参数运行真实 `verify-v5.ps1`,5 场景 33 项断言:

| 场景 | 证明 |
|---|---|
| S1 dotnetTest 真实失败 | exit 1;OVERALL FAIL;行计数 2/1/0 保持 FAIL;`runs\...\dotnetTest.meta.txt` 记录 exit=1/total=2/failed=1/真实 head SHA;框架日志被收集到 `<log dir>\test-results\Probe.Tests\`(即 workflow 上传子树)且**含失败用例全名 `Probe.Tests.ProbeTests.ControlledFailure` 与断言消息** |
| S2 先放旧失败日志→当前 PASS | exit 0;PASS;计数 2/0/0;收集物无旧标记;**预置的旧日志被 purge 删除**(运行独占成立) |
| S3 direct 真实失败 | exit 1;FAIL;计数保持;direct log 含用例名+断言+`Total: 2`;direct 不产生框架文件而收集不虚构 |
| S4 direct+旧日志→PASS | 同 S2 契约在 direct 模式成立 |
| S5 零用例项目 | exit 1;FAIL;`zero executed test cases` 门禁原样生效 |

## 5. 实测结果

### 5.1 诊断自测(本任务自身验证)

- 命令:`powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\VerifyV5Diagnostics.Tests.ps1`(证据保留版另加 `-KeepWorkspace`)
- 结果:**PASS,33/33 checks,退出码 0**(两次全量执行;首次 32/33 暴露并修复了 4.1-5 的跨盘路径缺陷与一条按实证修正的 S3 断言)。
- 证据目录:`artifacts\verify-v5-selftest-20261024-010644\`(131 个文件,S1–S5 全部产物)。
  - `logs-s1/summary.log` sha256 `16854a281f4307c710a1179da5a890382b859dc0c1763b989f958b79fcfe099b`
  - `logs-s1/test-results/Probe.Tests/Probe.Tests_net8.0_x64.log` sha256 `39568c3473d8a6eed0ae5338853e2913e8e234949efafd7782ba1cf7b93cd3a7`
  - `logs-s1/runs/Probe.Tests/dotnetTest.meta.txt` sha256 `96894badcdc384c99304cc6aa39a10bd69700820188633804634fd612b39cb62`

### 5.2 任务基线统一验证(生产树)

- 命令:`powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-v5.ps1 -Configuration Release -RunnerMode dotnetTest`
- 验证时 head:`5770b8a37e76dafa396cdcada7028269e6939f47`(未含本次改动提交;与已审查 PR19 head 一致)
- 证据目录:`artifacts\verify-v5\20261024-010458\`
- 结果:**OVERALL FAIL,退出码 1**;solution build PASS(exit 0);plugin copy PASS(12 assemblies);**618 passed / 0 failed / 1 真实 PG skipped**:

| 项目 | TOTAL | FAILED | SKIPPED | RESULT |
|---|---:|---:|---:|---|
| Mahjong.Client.Tests | 132 | 0 | 0 | PASS |
| Mahjong.Contracts.Tests | 82 | 0 | 0 | PASS |
| Mahjong.Domain.Tests | 313 | 0 | 0 | PASS |
| Mahjong.Ai.Tests | 14 | 0 | 0 | PASS |
| Mahjong.LobbyService.Tests(自动发现,不在 sln) | 78 | 0 | **1** | **FAIL(PG)** |
| Mahjong.Outbox.CrashWorker(helper) | — | — | — | PASS(仅构建) |

- 该 skip 由新证据链直接命名(此前只能看到计数):`Mahjong.LobbyService.Tests.PostgresIntegrationTests.PostgresLobbyRoundTripWhenARealDatabaseIsConfigured`,原因 `NOT_RUN: no PostgreSQL test database. Set MAHJONG_LOBBY_TEST_POSTGRES...`。**真实 PG 归属他线程返修,本任务不接管、不重复、不等待**;整批因此保持 FAIL/BLOCKED 属预期,本任务不要求也不尝试绕过 PG 变绿。
- 与远端同 SHA 两次 run(PR `39977041658` / push `39977029218` 均 618/0/1→FAIL)形态一致。

### 5.3 基线证据 SHA256(节选)

| 文件(相对 `artifacts\verify-v5\20261024-010458\`) | SHA256 |
|---|---|
| `summary.log` | `2dfa936fb549ae0ed365e6660cf08e8151543a5923063467e6351bc7ddfddb81` |
| `run-info.txt` | `0d7682a873f9ba091a950ef4b1ddbd5f4e5da022a7bd2bfb8c7c995d4f76a583` |
| `test-results/Mahjong.Client.Tests/Mahjong.Client.Tests_net8.0_x64.log` | `f2b9c2756fa4faf5f6d12b7dd7132cb017c79db2d12615358a24d83ad8690024` |
| `test-results/Mahjong.LobbyService.Tests/Mahjong.LobbyService.Tests_net8.0_x64.log` | `3860808c17b6b14aaf02a4063e62736b9ba356b831652997d558f98b94b20740` |
| `runs/Mahjong.LobbyService.Tests/dotnetTest.meta.txt` | `fef3a0a6e65a6005c1815ad8d6ca55f8162e0efff793332883cfb6b0ada8c844` |

改动文件 SHA256(提交前):

| 文件 | SHA256 |
|---|---|
| `tools/verify-v5.ps1` | `11d3e89e9ba20ef3d78f73c8d61af4e13f4251fb970a7a5f9bb02685c00ed990` |
| `tools/tests/VerifyV5Diagnostics.Tests.ps1` | `c3bf9798e9072fd4faf0f5c43db811e22cdd6bf1bdfb855e1997e4dd69f034e8` |
| `.github/workflows/v5-verify.yml` | `3e623b5fa918d9ce236e0e55636cb082b78a9c776f68fc1a39de2e41cb54572e` |

## 6. 实际未运行(不得推断为通过)

- **任何远端 CI**:本任务未 push、未建 PR、未触发 workflow_dispatch;新 workflow 上传行为(`if-no-files-found: error`、artifact 命名)只能在下一次真实远端运行中验证,本地仅能验证脚本侧产物布局。
- Unity/IL2CPP/Android/iOS、WSS、真实 PostgreSQL、TableRuntime 相关工作:一律未触碰(各有归属线程)。
- direct 模式未在生产全树上运行(自测已覆盖 direct 证据链;生产 direct 全量留给后续需要时)。
- 历史旧 Client 失败的根因(UNRESOLVED,§2)。

## 7. 提交与状态

- 分支:`wt/v5-ci-diagnostics-20261024`;验证时 HEAD:`5770b8a37e76dafa396cdcada7028269e6939f47`;交付为本地提交(未 push,由 caller 独立验收后合入唯一 PR19 `feat/v5-1-integration -> v5`,push/MR/merge-back 均由 caller 执行)。交付 commit 以 `git log` 实际记录为准。
- `artifacts\` 与 `artifacts\verify-v5-selftest-*\` 为未跟踪本地证据目录,按本项目先例不提交。
