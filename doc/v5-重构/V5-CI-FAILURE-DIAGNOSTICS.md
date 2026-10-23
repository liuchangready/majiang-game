# V5 CI 失败诊断证据留存 — 改动记录与实测报告

日期:2026-10-24(round 2 返修更新;初版对应 round 1 提交 `7a9aa6c`)
范围:**独立 CI 诊断证据留存任务**,与在途 P3.1、真实 PostgreSQL 返修(thread_20261023_230533_bb60,他线程所有)、WSS 补验三条范围零交集。本文件只记录本次改动与实测,不是迁移方案、不是验收报告。
基线:已审查远端 PR19 head `5770b8a37e76dafa396cdcada7028269e6939f47`(分支 `wt/v5-ci-diagnostics-20261024`,工作树 `D:\majiang-wt\ci-diagnostics-20261024`,开工时工作树 clean)。
**V4 仍是唯一生产架构。本任务不宣布、也不构成 G1/G5 或任何阶段门槛通过。**

> **Round 2 返修摘要**:caller 独立检视 round 1 提交 `7a9aa6c` **未通过**,用探针 `artifacts/caller-review-round1/Probe-Diagnostics.ps1` 复现三项缺陷:(1) 不同来源根同名日志在收集时互相覆盖;(2) purge 失败后旧日志被当作本次证据收集;(3) runner 抛异常时已产出 console 输出未落盘。三项已在本次返修修复,自测扩为 **8 场景 63 项断言全部 PASS**(§5.1),详见 §8。§4.1 的保证表述已按实测改为不夸大版本。

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

依据上述直接证据:留存证据中不存在任何失败用例身份(框架日志未上传、job log 无用例名、console 摘要不含名字),因此**无法定位当时的失败用例,也无法提出根因**。本任务不盲目重试 CI,不做无证据猜测。本任务的产出保证(按实测表述,见 §4.1):正常运行结束后,`dotnetTest` 模式失败产生的框架日志会随 artifact 上传;runner 调用中途异常时,尽力留存已产出的 console 输出与异常记录并把该项目判为非 0(不伪造 exit=0)。**不声称"任何失败都可诊断"**——若进程级崩溃发生在任何文件落盘之前,可留证据仅剩 run-info/构建/门禁记录,此类残余缺口在 §6 列明。

## 3. 实测确立的 runner 行为(本地实证,非文档推断)

用 `D:\Unity\dotnet\dotnet.exe`(SDK 8.0.426,与 `global.json` 一致)+ `xunit.v3 4.0.2` + MTP 2.5.1 最小探针实测:

| 模式 | 失败行为 | 失败用例身份所在 |
|---|---|---|
| `dotnet test <csproj>`(dotnetTest/auto 路径) | exit 1;console 只有 `Failed! - Failed: 1, ... Total: 2` 汇总 + MSBuild error 行指向 `bin\<cfg>\<tfm>\TestResults\<asm>_<tfm>_<arch>.log` | **该 TestResults 日志**(UTF-16LE+BOM),内含失败用例全名、断言消息、堆栈、逐项计数、底层命令行 |
| `dotnet <dll> -noLogo -reporter default`(direct 路径) | exit 1;**不写任何 TestResults 文件**(实测验证) | console 本身(用例名+断言+堆栈+`Total: ...` 汇总),由脚本捕获为 `direct-<project>.log` |

## 4. 本次改动(3 个文件修改 + 1 个新文件)

### 4.1 `tools/verify-v5.ps1`

1. **按次调用的框架日志收集(含布局)**:每个测试项目在每次 runner 调用(dotnetTest 与 direct 各自)前删除其 `bin\<cfg>\<tfm>\TestResults\` 与项目根 `TestResults\`,调用后把生成的一切文件复制到 `<log dir>\test-results\<project>\<mode>\<项目内相对路径>\`(附逐文件 SHA256)。`<mode>` 段把同一次 auto 运行的两次调用分开;项目内相对路径把不同来源根的同名日志(`bin\...\TestResults\x.log` 与项目根 `TestResults\x.log`)分开——同名文件不再互相覆盖。收集为 best-effort,任何收集失败写入 `collection-errors.log`,**绝不改变门禁结果、计数或退出码**。
2. **purge 失败时的陈旧隔离(不再声称"必然是本次产物")**:若删除因文件被占用等原因失败(如日志被其他进程以共享读锁定),该目录**不会**被当作"已清空"处理:删除前先做文件快照(相对路径+SHA256),收集时把与快照逐项吻合(路径+哈希都相同)的文件**排除**出本次证据并记入 meta 与 `collection-errors.log`(内容已变化的同名路径视为本次产物予以收集);排除是有损对策,**gap 已如实记录**,不篡改 runner 退出码/计数/门禁。purge 正常成功时行为与独占收集一致(目录已空,找到的即本次产物)。
3. **调用异常时的证据保全**:`Invoke-Captured` 流式捕获 console 输出并在 `finally` 中落盘——runner 命令抛异常时,**异常发生前已产出的输出与异常本身都会写入**对应 console 日志,退出码记为 `EXCEPTION`(门禁按非 0 处理,**不伪造 exit=0**);随后仍尽力收集已生成的框架文件,meta 记录"invocation exception; exit unknown"。
4. **逐次调用归因**:`runs\<project>\<mode>.meta.txt` 记录精确命令行、CWD、退出码、解析计数、head SHA、逐目录 purge 结果(cleared / NOT CLEARED)、收集清单与被排除的陈旧文件清单。
5. **运行身份**:`run-info.txt`(建目录时即写,连 BLOCKED 早退也留证)记录 UTC/本地时间、`git rev-parse HEAD`/branch(失败原样记录,不伪造)、仅公开 CI 元数据(GITHUB_REPOSITORY/RUN_ID/RUN_ATTEMPT/SHA/REF/RUNNER_OS——**不读取任何 token/secret**)、脚本路径与其 SHA256。LogDir 若已存在自动加 `-N` 后缀,保证每次运行独占目录。summary.log 头部新增 head/branch/ci provenance 行。
6. **`-NoProjectDiscovery`**(新增参数,默认关):跳过 `dotnet\tests|src` 自动发现,仅验证 manifest 项目——供隔离自测使用;生产调用不变(基线运行已验证发现逻辑照常)。
7. **`Resolve-UnderRoot`**:manifest 路径解析对**跨盘绝对路径**返回原路径(`Join-Path D:\repo C:\x` 会错误拼成 `D:\repo\C:\x`,自测首跑暴露,已修),相对路径行为不变。
8. 门禁逻辑(fail/skip/zero/missing/build-fail ⇒ FAIL;退出码 0/1/2)**一行未改**;测试摘要解析、fallback 保护、插件拷贝检查全部原样。`EXCEPTION` 退出标签在门禁中同样按非 0 处理(既有 `-ne 0` 比较,未新增特判)。

### 4.2 `.github/workflows/v5-verify.yml`

- artifact 更名 `v5-verify-logs-<run_id>-<run_attempt>`(重试互不覆盖);`if-no-files-found: error`(没证据=硬失败,不允许"绿了但无证据");`retention-days: 30`;`if: always()` 保持。上传范围仍为 `artifacts/verify-v5/**`,`test-results\` 与 `runs\` 随时间戳目录整体进入 artifact,相对路径保留,不复制 bin/obj。

### 4.3 `tools/tests/VerifyV5Diagnostics.Tests.ps1`(新)

隔离自测:在**任务自有 TEMP** 下生成一次性 xunit.v3/MTP 探针工程(独立 sln+manifest,`-NoProjectDiscovery -SkipPluginCopyCheck`,生产测试零改动,不触碰 PG),用与生产完全相同的 runner 参数运行真实 `verify-v5.ps1`;round 2 扩为 **8 场景 63 项断言**(round 1 为 5 场景 33 项,未覆盖 caller 复现的三类边界):

| 场景 | 证明 |
|---|---|
| S1 dotnetTest 真实失败 | exit 1;OVERALL FAIL;行计数 2/1/0 保持 FAIL;`runs\Probe.Tests\dotnetTest.meta.txt` 记录 exit=1/total=2/failed=1/真实 head SHA;框架日志按项目内相对路径收集到 `<log dir>\test-results\Probe.Tests\dotnetTest\bin\Release\net8.0\TestResults\`(即 workflow 上传子树)且**含失败用例全名 `Probe.Tests.ProbeTests.ControlledFailure` 与断言消息** |
| S2 先放旧失败日志→当前 PASS | exit 0;PASS;计数 2/0/0;收集物无旧标记;**预置的旧日志被 purge 删除**(运行独占成立) |
| S3 direct 真实失败 | exit 1;FAIL;计数保持;direct log 含用例名+断言+`Total: 2`;direct 不产生框架文件而收集不虚构 |
| S4 direct+旧日志→PASS | 同 S2 契约在 direct 模式成立 |
| S5 零用例项目 | exit 1;FAIL;`zero executed test cases` 门禁原样生效 |
| S6 锁定旧日志+当前 PASS(round 2 新增) | 用 `FileShare.Read` 句柄锁定预置旧失败日志使 purge 必然失败:exit 仍 0、OVERALL PASS 不受影响;当前框架日志照常收集;**旧标记内容不出现在任何收集物中**;meta 记录 NOT CLEARED 与被排除陈旧文件;`collection-errors.log` 记录 purge 失败 |
| S7 锁定旧日志+当前新失败(round 2 新增) | 同一锁定保持、源改为真实失败:exit 1、OVERALL FAIL、计数 2/1/0;收集到的框架日志含**当前失败内容**(同路径内容已变化→视为本次产物)且无旧标记;旧标记不出现在任何收集物中 |
| S8 单元级回归(round 2 新增) | 按 caller 探针同款 AST 提取法从生产脚本抽出 `Get-TestResultsSnapshot`/`Clear-TestResultsDirs`/`Save-TestResults`/`Invoke-Captured`:不同来源根同名日志→2 条收集记录、各归各位、内容与 SHA256 逐一吻合;双次调用(direct 后跑)落在独立 `<mode>` 目录且不覆盖前次;`Invoke-Captured` 抛异常→返回 `$null`(非伪造 0)、异常前输出与异常文本均落盘;正常命令返回真实退出码(3) |

## 5. 实测结果

### 5.1 诊断自测(本任务自身验证;round 2 结果)

- 命令:`powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\VerifyV5Diagnostics.Tests.ps1`(证据保留版另加 `-KeepWorkspace`)
- 结果:**PASS,63/63 checks,退出码 0**(round 2 代码上两次全量执行:`-KeepWorkspace` 版与默认清理版各一次,均 63/63)。
- 返修过程中自测曾三次失败,每次都对应一个真实缺陷并被修复(不是改断言凑绿):
  1. S6/S7 陈旧隔离未生效——`Save-TestResults` 用项目相对路径去比对自己目录相对的快照路径,永不命中;修正为同基准目录相对路径比较(S8 亦按此时序重构)。
  2. S8 AST 提取漏了 `Clear-TestResultsDirs` 的依赖 `Get-TestResultsSnapshot`,补齐第 4 个函数。
  3. S8 夹具时序错(先收集后种文件)——改为生产时序 purge→runner 产出→收集。
- 证据目录:`artifacts\verify-v5-selftest-round2-20261024-012631\`(158 个文件,S1–S8 全部产物,复制自 `-KeepWorkspace` 通过运行)。
  - `logs-s1/summary.log` sha256 `258e154b195165ed7d35c0d70a3d0dd64f723cc64d67c6e2bef52cedbf07a32b`
  - `logs-s1/test-results/Probe.Tests/dotnetTest/bin/Release/net8.0/TestResults/Probe.Tests_net8.0_x64.log` sha256 `b787f7c4462077cc91e89b91d6b8d1a14104ecceaf30b8d6bb9a7c9c60758e03`
  - `logs-s1/runs/Probe.Tests/dotnetTest.meta.txt` sha256 `f12f03357db585e6782f6049d320e9ade7928290d837b936ba65f0898082ed42`
  - `logs-s6/runs/Probe.Tests/dotnetTest.meta.txt`(含 NOT CLEARED + 排除清单)sha256 `1fa3833609fd8f0df8c7c360b851433196c1d9b51fb6ca83df3b44353e8eb5c8`
  - `logs-s6/collection-errors.log`(purge 失败 gap 记录)sha256 `00d0ed68e3e007c19fe88daf626c2b46efec3f542a34d7a9c327a70ffb32b596`
  - `unit-s8/exception-console.log`(异常前输出+异常文本落盘)sha256 `874a44c6164eba42f2f4151e409463c34aba3541a51ee515e25ecafaff0e9cfe`
- round 1 的 33/33 结果与其证据目录 `artifacts\verify-v5-selftest-20261024-010644\` 仅对 round 1 代码有效,已不反映当前脚本。

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

### 5.3 基线证据 SHA256(节选;round 1 代码产出,布局为 round 1 版)

> round 2 返修改变了收集布局(test-results 增加按次 `<mode>` 段)与异常/陈旧处理,**门禁与计数逻辑未变**;caller 在 round 2 重跑了统一 Release `dotnetTest`(其声明:整批 PG 缺配置仍单列预期 FAIL),本表保留 round 1 基线运行产物哈希供核对。

| 文件(相对 `artifacts\verify-v5\20261024-010458\`) | SHA256 |
|---|---|
| `summary.log` | `2dfa936fb549ae0ed365e6660cf08e8151543a5923063467e6351bc7ddfddb81` |
| `run-info.txt` | `0d7682a873f9ba091a950ef4b1ddbd5f4e5da022a7bd2bfb8c7c995d4f76a583` |
| `test-results/Mahjong.Client.Tests/Mahjong.Client.Tests_net8.0_x64.log` | `f2b9c2756fa4faf5f6d12b7dd7132cb017c79db2d12615358a24d83ad8690024` |
| `test-results/Mahjong.LobbyService.Tests/Mahjong.LobbyService.Tests_net8.0_x64.log` | `3860808c17b6b14aaf02a4063e62736b9ba356b831652997d558f98b94b20740` |
| `runs/Mahjong.LobbyService.Tests/dotnetTest.meta.txt` | `fef3a0a6e65a6005c1815ad8d6ca55f8162e0efff793332883cfb6b0ada8c844` |

改动文件 SHA256(round 2 返修后、提交前;文档自身哈希因自引用不列,以提交内容为准):

| 文件 | SHA256 |
|---|---|
| `tools/verify-v5.ps1` | `6813bd03def93feb5da28a30b2c466904fac10dd8e6f74c59fb6d5f779b059d5` |
| `tools/tests/VerifyV5Diagnostics.Tests.ps1` | `c2eba233bd76a3507450e0874e52cf48aeeb98a1d0fbf9c785612a150a7e9e5b` |
| `.github/workflows/v5-verify.yml` | `df2d3a563a19895fd25c38ab9a87b9d79677d9f3053124144ee48fceb67d5774` |

round 1 已提交版本(供对照,均已被 round 2 取代):`tools/verify-v5.ps1` `11d3e89e…ed990`,`tools/tests/VerifyV5Diagnostics.Tests.ps1` `c3bf9798…4e8`,`…yml` `3e623b5f…72e`,提交 `7a9aa6c339fb84e557de5e617f43827c71a714bf`。

## 6. 实际未运行(不得推断为通过)

- **任何远端 CI**:本任务未 push、未建 PR、未触发 workflow_dispatch;新 workflow 上传行为(`if-no-files-found: error`、artifact 命名)只能在下一次真实远端运行中验证,本地仅能验证脚本侧产物布局。
- Unity/IL2CPP/Android/iOS、WSS、真实 PostgreSQL、TableRuntime 相关工作:一律未触碰(各有归属线程)。
- direct 模式未在生产全树上运行(自测已覆盖 direct 证据链;生产 direct 全量留给后续需要时)。
- 历史旧 Client 失败的根因(UNRESOLVED,§2)。
- **round 2 未重跑生产树统一 Release 全量验证**:生产统一测试 caller 已于本轮执行(整批 PG 缺配置仍单列预期 FAIL),本轮改动仅涉及证据收集路径与异常处理、无门禁/计数/业务改动;隔离自测已覆盖新逻辑。若需要,可随时以同一命令重跑,不构成阻碍。
- **残余覆盖缺口(round 2 明示)**:runner 进程在任何文件落盘前即被杀死/崩溃时,可留证据仅剩 run-info、构建与门禁记录;`Clear-TestResultsDirs` 的快照排除基于路径+SHA256 双吻合,理论上同路径同内容的"旧文件恰好等于本次产物"无法区分(但该内容与本次产物一致,诊断价值不受损);Windows 文件锁以外形态的 purge 部分失败(如目录只读)未逐一实测,统一走同一快照排除路径。

## 7. Round 2 返修记录(caller 检视 `7a9aa6c` 未通过 → 本轮修复)

caller 用 `artifacts/caller-review-round1/Probe-Diagnostics.ps1`(AST 抽取生产函数,不跑主体)在 round 1 提交上复现三项缺陷;round 1 自测 33 项未覆盖这些边界。逐项修复与回归:

| # | caller 复现的缺陷(round 1 实测) | 修复(round 2) | 回归证明 |
|---|---|---|---|
| 1 | 不同来源根同名日志(`bin\...\TestResults\same.log` 与项目根 `TestResults\same.log`)复制到同一 `test-results\<project>\same.log` 互相覆盖:source files=2, collected=1,meta 2 条且首条哈希不对应现存文件;auto 两次调用同 target 也会覆盖 | 收集布局改为 `test-results\<project>\<mode>\<项目内相对路径>`,双键(`<mode>` + 相对路径)保证互不覆盖;逐文件 SHA256 取自实际副本 | S8 单元级:2 源→2 条收集记录、两条路径各自内容/哈希吻合;S1/S3 的 `<mode>` 目录;S8 双次调用不覆盖前次 |
| 2 | `Clear-TestResultsDirs` 捕获删除错误(探针以 `FileShare.Read` 锁定旧文件)后仍无条件 Save:`purged=0` 但旧日志 `OLD-RUN-FAILURE-ONLY` 进入"本次证据" | purge 失败的目录不再按已清空处理:删除前快照(路径+SHA256),收集时排除与快照路径+哈希双吻合的文件,记入 meta `NOT CLEARED`/`excluded stale files` 与 `collection-errors.log`(gap 如实记录);内容已变化的同名路径视为本次产物;退出码/计数/门禁原样 | S6:锁定旧日志+当前 PASS→exit 0、PASS、旧标记零泄漏、meta 记录 NOT CLEARED+排除项;S7:同一锁定+当前新失败→exit 1、FAIL、计数 2/1/0、收集日志含当前 sentinel 无旧标记 |
| 3 | `Invoke-Captured` 先缓存全部输出,命令抛异常时无任何落盘(`captured output file exists=False`) | 改为流式捕获+`finally` 落盘:异常前输出与异常文本都写盘;返回 `$null`,上层记 `EXCEPTION` 并按非 0 门禁处理(不伪造 0);meta 标注 "invocation exception; exit unknown" | S8:抛异常→返回 `$null`、日志存在且含 `OUTPUT-BEFORE-EXCEPTION` 与 `SIMULATED-RUNNER-EXCEPTION`;对照:正常命令真实退出码 3 原样返回 |

文档保证表述同步改为非绝对版(§2、§4.1):不再声称"旧结果永不混入"(purge 失败路径以快照排除+gap 记录代替,非零残留风险如实列于 §6),不再声称"任何失败都可诊断"(进程在文件落盘前崩溃的情形不在覆盖内)。

## 8. 提交与状态

- 分支:`wt/v5-ci-diagnostics-20261024`;round 1 交付提交 `7a9aa6c339fb84e557de5e617f43827c71a714bf`(已被 caller 判未通过,见 §7);round 2 返修为同分支同 4 文件范围的新本地提交(未 push,由 caller 独立验收后合入唯一 PR19 `feat/v5-1-integration -> v5`,push/MR/merge-back 均由 caller 执行)。交付 commit 以 `git log` 实际记录为准。
- `artifacts\` 与 `artifacts\verify-v5-selftest-*\` 为未跟踪本地证据目录,按本项目先例不提交。caller 的检视证据 `artifacts\caller-review-round1\` 原样保留,未改动。
- `artifacts\` 与 `artifacts\verify-v5-selftest-*\` 为未跟踪本地证据目录,按本项目先例不提交。
