# V5 CI 失败诊断证据留存 — 改动记录与实测报告

日期:2026-10-24(round 3 返修更新;历史:round 1 提交 `7a9aa6c`、round 2 提交 `31580fb`,均经 caller 独立检视)
范围:**独立 CI 诊断证据留存任务**,与在途 P3.1、真实 PostgreSQL 返修(thread_20261023_230533_bb60,他线程所有)、WSS 补验三条范围零交集。本文件只记录本次改动与实测,不是迁移方案、不是验收报告。
基线:已审查远端 PR19 head `5770b8a37e76dafa396cdcada7028269e6939f47`(分支 `wt/v5-ci-diagnostics-20261024`,工作树 `D:\majiang-wt\ci-diagnostics-20261024`,开工时工作树 clean)。
**V4 仍是唯一生产架构。本任务不宣布、也不构成 G1/G5 或任何阶段门槛通过。**

> **返修摘要**:caller 独立检视 round 1 提交 `7a9aa6c` **未通过**(探针复现:同名日志覆盖、purge 失败后旧日志混入、异常时输出不落盘)→ round 2 提交 `31580fb` 修复,自测扩为 8 场景 63 项;caller 检视 round 2 **仍未通过**(真实 `FileShare.None` 独占锁探针 `artifacts/caller-review-round2/Probe-UnreadableStale.ps1` 复现:快照期哈希不可读的旧文件,在锁运行中被释放、收集期哈希可读后,被误判"内容已变化"而当作本次证据收集)→ 本轮(round 3)修复:哈希缺失不构成内容变化证据,不可信快照整体保守排除,自测扩为 **11 场景 88 项断言全部 PASS**(§5.1)。详见 §7、§8。§4.1 的保证表述已按实测改为不夸大版本。

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
2. **purge 失败时的陈旧隔离(只收集可确证当前归属的文件)**:若删除因文件被占用等原因失败,该目录**不会**被当作"已清空"处理:删除前先做文件快照(相对路径+SHA256+哈希可读标志)。收集侧保守规则——快照与收集**双侧哈希都可读且相同**→证明未变化,排除;**双侧可读且不同**→证明本次运行中已重写,收集;**任一侧哈希不可读**(如 `FileShare.None` 独占锁,即使锁在运行中被释放、收集期重新可读)→**保守排除**——哈希缺失不构成内容变化证据;**快照枚举本身失败**→整个目录不可信,**全部排除**。每条排除连同原因记入 meta 与 `collection-errors.log`(gap 如实记录);不篡改 runner 退出码/计数/门禁。purge 正常成功时行为与独占收集一致(目录已空,找到的即本次产物)。
3. **调用异常时的证据保全**:`Invoke-Captured` 流式捕获 console 输出并在 `finally` 中落盘——runner 命令抛异常时,**异常发生前已产出的输出与异常本身都会写入**对应 console 日志,退出码记为 `EXCEPTION`(门禁按非 0 处理,**不伪造 exit=0**);随后仍尽力收集已生成的框架文件,meta 记录"invocation exception; exit unknown"。
4. **逐次调用归因**:`runs\<project>\<mode>.meta.txt` 记录精确命令行、CWD、退出码、解析计数、head SHA、逐目录 purge 结果(cleared / NOT CLEARED)、收集清单与被排除的陈旧文件清单。
5. **运行身份**:`run-info.txt`(建目录时即写,连 BLOCKED 早退也留证)记录 UTC/本地时间、`git rev-parse HEAD`/branch(失败原样记录,不伪造)、仅公开 CI 元数据(GITHUB_REPOSITORY/RUN_ID/RUN_ATTEMPT/SHA/REF/RUNNER_OS——**不读取任何 token/secret**)、脚本路径与其 SHA256。LogDir 若已存在自动加 `-N` 后缀,保证每次运行独占目录。summary.log 头部新增 head/branch/ci provenance 行。
6. **`-NoProjectDiscovery`**(新增参数,默认关):跳过 `dotnet\tests|src` 自动发现,仅验证 manifest 项目——供隔离自测使用;生产调用不变(基线运行已验证发现逻辑照常)。
7. **`Resolve-UnderRoot`**:manifest 路径解析对**跨盘绝对路径**返回原路径(`Join-Path D:\repo C:\x` 会错误拼成 `D:\repo\C:\x`,自测首跑暴露,已修),相对路径行为不变。
8. 门禁逻辑(fail/skip/zero/missing/build-fail ⇒ FAIL;退出码 0/1/2)**一行未改**;测试摘要解析、fallback 保护、插件拷贝检查全部原样。`EXCEPTION` 退出标签在门禁中同样按非 0 处理(既有 `-ne 0` 比较,未新增特判)。

### 4.2 `.github/workflows/v5-verify.yml`

- artifact 更名 `v5-verify-logs-<run_id>-<run_attempt>`(重试互不覆盖);`if-no-files-found: error`(没证据=硬失败,不允许"绿了但无证据");`retention-days: 30`;`if: always()` 保持。上传范围仍为 `artifacts/verify-v5/**`,`test-results\` 与 `runs\` 随时间戳目录整体进入 artifact,相对路径保留,不复制 bin/obj。

### 4.3 `tools/tests/VerifyV5Diagnostics.Tests.ps1`(新)

隔离自测:在**任务自有 TEMP** 下生成一次性 xunit.v3/MTP 探针工程(独立 sln+manifest,`-NoProjectDiscovery -SkipPluginCopyCheck`,生产测试零改动,不触碰 PG),用与生产完全相同的 runner 参数运行真实 `verify-v5.ps1`;round 3 现为 **11 场景 88 项断言**(round 1 为 5 场景 33 项,round 2 为 8 场景 63 项,均未覆盖后续 caller 复现的边界):

| 场景 | 证明 |
|---|---|
| S1 dotnetTest 真实失败 | exit 1;OVERALL FAIL;行计数 2/1/0 保持 FAIL;`runs\Probe.Tests\dotnetTest.meta.txt` 记录 exit=1/total=2/failed=1/真实 head SHA;框架日志按项目内相对路径收集到 `<log dir>\test-results\Probe.Tests\dotnetTest\bin\Release\net8.0\TestResults\`(即 workflow 上传子树)且**含失败用例全名 `Probe.Tests.ProbeTests.ControlledFailure` 与断言消息** |
| S2 先放旧失败日志→当前 PASS | exit 0;PASS;计数 2/0/0;收集物无旧标记;**预置的旧日志被 purge 删除**(运行独占成立) |
| S3 direct 真实失败 | exit 1;FAIL;计数保持;direct log 含用例名+断言+`Total: 2`;direct 不产生框架文件而收集不虚构 |
| S4 direct+旧日志→PASS | 同 S2 契约在 direct 模式成立 |
| S5 零用例项目 | exit 1;FAIL;`zero executed test cases` 门禁原样生效 |
| S6 锁定旧日志+当前 PASS(round 2) | 用 `FileShare.Read` 句柄锁定预置旧失败日志使 purge 必然失败:exit 仍 0、OVERALL PASS 不受影响;当前框架日志照常收集;**旧标记内容不出现在任何收集物中**;meta 记录 NOT CLEARED 与被排除陈旧文件;`collection-errors.log` 记录 purge 失败 |
| S7 锁定旧日志+当前新失败(round 2) | 同一锁定保持、源改为真实失败:exit 1、OVERALL FAIL、计数 2/1/0;收集到的框架日志含**当前失败内容**(双侧哈希可读且不同→证明本次重写)且无旧标记;旧标记不出现在任何收集物中 |
| S8 单元级回归(round 2) | 按 caller 探针同款 AST 提取法从生产脚本抽出 `Get-TestResultsSnapshot`/`Clear-TestResultsDirs`/`Save-TestResults`/`Invoke-Captured`:不同来源根同名日志→2 条收集记录、各归各位、内容与 SHA256 逐一吻合;双次调用(direct 后跑)落在独立 `<mode>` 目录且不覆盖前次;`Invoke-Captured` 抛异常→返回 `$null`(非伪造 0)、异常前输出与异常文本均落盘;正常命令返回真实退出码(3) |
| S9 独占锁运行中释放+当前 PASS(round 3) | caller round-2 检视的复现回归:旧日志以 `FileShare.None` 独占锁定(快照期**连哈希都读不到**),第二 runspace 在确认快照完成后(purge 失败的 `collection-errors.log` 出现)**运行中释放锁**,使收集期重哈希可读——naive 比较会把旧文件当"已变化":实测 exit 0、OVERALL PASS、当前框架日志照常收集、**旧内容零泄漏**、meta 记录"hash unavailable at snapshot time"保守排除 |
| S10 独占锁运行中释放+当前 FAIL(round 3) | 同机制+真实失败源:exit 1、OVERALL FAIL、计数 2/1/0;收集日志含当前 sentinel、无旧标记;旧内容零泄漏 |
| S11 快照枚举失败(round 3,故障注入) | 注入首个 `Get-ChildItem` 调用失败(快照失明)+真实 `FileShare.None` 锁使 purge 失败:整个目录不可信→**0 收集**(连"看似当前"的新文件也不收)、2 条排除记录、`collection-errors.log` 记录 snapshot enumeration failed;与真实文件锁证据(S9/S10)区分 |

## 5. 实测结果

### 5.1 诊断自测(本任务自身验证;round 3 结果)

- 命令:`powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\VerifyV5Diagnostics.Tests.ps1`(证据保留版另加 `-KeepWorkspace`)
- 结果:**PASS,88/88 checks,退出码 0**(round 3 代码上两次全量执行:`-KeepWorkspace` 版与默认清理版各一次,均 88/88)。
- round 3 返修中新自测一次失败(86/88):S9/S10 的"运行中释放锁"观察哨兵用的是"purge 删除解锁哨兵文件",但 `Remove-Item -Recurse` 碰到按枚举顺序更早的锁定文件即抛出,哨兵根本未被删——改用确定性信号(purge 失败在快照后必写 `collection-errors.log`,其出现即证明快照已完成)。这是测试观察机制的修正,不是产品缺陷;产品侧保守排除逻辑在首跑即全部通过。
- round 2 返修中新自测曾三次失败,均对应真实缺陷并已修复:S6/S7 陈旧排除的路径基准错(项目相对 vs 目录相对);S8 AST 提取漏 `Get-TestResultsSnapshot` 依赖;S8 夹具时序错(先收集后种文件)。
- 证据目录:`artifacts\verify-v5-selftest-round3-20261024-014355\`(182 个文件,S1–S11 全部产物,复制自 `-KeepWorkspace` 通过运行)。
  - `logs-s9/summary.log` sha256 `fde7122c76e3e8daa6c17f26776f4e02c417dcdb165b9ad64acd178fdc9fc9de`
  - `logs-s9/runs/Probe.Tests/dotnetTest.meta.txt`(hash unavailable 保守排除)sha256 `6339d14856e2bb4abc12cfe723b9db3021a899ebca18b108d4f24612bdb5e54f`
  - `logs-s9/collection-errors.log` sha256 `37f1546ef7ac7be9f5977e1103a2f19694fc643ef64d6bf74a5d24879029b44a`
  - `logs-s10/runs/Probe.Tests/dotnetTest.meta.txt` sha256 `aefc22a8793f6dd16437542460075144336e876538b8439bda80aa85b899f737`
  - `logs-s10/test-results/Probe.Tests/dotnetTest/bin/Release/net8.0/TestResults/Probe.Tests_net8.0_x64.log`(当前失败内容)sha256 `d875614def4440aa18b71bd03b635bd7a3a476b31ed63c2e8e5e5cdf8e058848`
  - `unit-s11/collection-errors.log`(snapshot enumeration failed)sha256 `a93fcab80f3124b9a77850eeb43941fc51dece488b0b7453f33b429158e94fcf`
- 历史:round 1 的 33/33(`artifacts\verify-v5-selftest-20261024-010644\`)与 round 2 的 63/63(`artifacts\verify-v5-selftest-round2-20261024-012631\`)仅对各自当时代码有效,已不反映当前脚本。

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

> **归属校正(caller round-3 检视指出)**:caller 的统一生产全量 Release `dotnetTest`(618/0/1,整批 PG 缺配置单列预期 FAIL)是在 **round 1 检视期间**运行的,对应 **round 1 代码**(`7a9aa6c`),**不是**在 round 2/3 返修代码上运行;其结果只能证明 round 1 代码的生产树行为,不得归给返修后代码。round 2/3 返修改变了收集布局与陈旧/异常处理,**门禁与计数逻辑未变**;返修后代码尚未做过生产树全量运行(见 §6)。本表保留 round 1 基线运行产物哈希供核对。

| 文件(相对 `artifacts\verify-v5\20261024-010458\`) | SHA256 |
|---|---|
| `summary.log` | `2dfa936fb549ae0ed365e6660cf08e8151543a5923063467e6351bc7ddfddb81` |
| `run-info.txt` | `0d7682a873f9ba091a950ef4b1ddbd5f4e5da022a7bd2bfb8c7c995d4f76a583` |
| `test-results/Mahjong.Client.Tests/Mahjong.Client.Tests_net8.0_x64.log` | `f2b9c2756fa4faf5f6d12b7dd7132cb017c79db2d12615358a24d83ad8690024` |
| `test-results/Mahjong.LobbyService.Tests/Mahjong.LobbyService.Tests_net8.0_x64.log` | `3860808c17b6b14aaf02a4063e62736b9ba356b831652997d558f98b94b20740` |
| `runs/Mahjong.LobbyService.Tests/dotnetTest.meta.txt` | `fef3a0a6e65a6005c1815ad8d6ca55f8162e0efff793332883cfb6b0ada8c844` |

改动文件 SHA256(round 3 返修后、提交前;文档自身哈希因自引用不列,以提交内容为准):

| 文件 | SHA256 |
|---|---|
| `tools/verify-v5.ps1` | `fa8d539bb6e036a7fd002011f001f5d5fdb27ba8b20cd5a9089098e87f3ce307` |
| `tools/tests/VerifyV5Diagnostics.Tests.ps1` | `0ed7de4c14ee426cfdb6e2e213ba14bbca770621eae396f54ab0da6430e255f2` |
| `.github/workflows/v5-verify.yml` | `df2d3a563a19895fd25c38ab9a87b9d79677d9f3053124144ee48fceb67d5774`(round 2 起未再改动) |

历史版本(供对照,均已被取代):round 1 提交 `7a9aa6c` 中 `tools/verify-v5.ps1` `11d3e89e…ed990`、自测 `c3bf9798…4e8`、`…yml` `3e623b5f…72e`;round 2 提交 `31580fb` 中 `tools/verify-v5.ps1` `6813bd03…9d5`、自测 `c2eba233…e5b`、`…yml` `df2d3a56…774`。

## 6. 实际未运行(不得推断为通过)

- **任何远端 CI**:本任务未 push、未建 PR、未触发 workflow_dispatch;新 workflow 上传行为(`if-no-files-found: error`、artifact 命名)只能在下一次真实远端运行中验证,本地仅能验证脚本侧产物布局。
- Unity/IL2CPP/Android/iOS、WSS、真实 PostgreSQL、TableRuntime 相关工作:一律未触碰(各有归属线程)。
- direct 模式未在生产全树上运行(自测已覆盖 direct 证据链;生产 direct 全量留给后续需要时)。
- 历史旧 Client 失败的根因(UNRESOLVED,§2)。
- **生产树统一 Release 全量未在返修代码上运行**:caller 的统一全量(618/0/1,PG 单列预期 FAIL)系 **round 1 检视期间**运行,对应 round 1 代码(§5.3 归属校正);round 2/3 返修代码仅有隔离自测(88/88)覆盖。返修不涉门禁/计数/业务改动,如需生产树复核可随时以同一命令重跑。
- **残余覆盖缺口(round 3 明示)**:runner 进程在任何文件落盘前即被杀死/崩溃时,可留证据仅剩 run-info、构建与门禁记录;快照与收集双侧哈希都可读且恰好相同的"旧文件与本次产物同内容"不可区分(但内容一致,诊断价值不受损);Windows 共享读锁/独占锁以外形态的 purge 部分失败(如目录只读)未逐一实测,统一走同一保守排除路径。

## 7. 返修记录一(caller 检视 round 1 `7a9aa6c` 未通过 → round 2 交付 `31580fb`)

caller 用 `artifacts/caller-review-round1/Probe-Diagnostics.ps1`(AST 抽取生产函数,不跑主体)在 round 1 提交上复现三项缺陷;round 1 自测 33 项未覆盖这些边界。逐项修复与回归:

| # | caller 复现的缺陷(round 1 实测) | 修复(round 2) | 回归证明 |
|---|---|---|---|
| 1 | 不同来源根同名日志(`bin\...\TestResults\same.log` 与项目根 `TestResults\same.log`)复制到同一 `test-results\<project>\same.log` 互相覆盖:source files=2, collected=1,meta 2 条且首条哈希不对应现存文件;auto 两次调用同 target 也会覆盖 | 收集布局改为 `test-results\<project>\<mode>\<项目内相对路径>`,双键(`<mode>` + 相对路径)保证互不覆盖;逐文件 SHA256 取自实际副本 | S8 单元级:2 源→2 条收集记录、两条路径各自内容/哈希吻合;S1/S3 的 `<mode>` 目录;S8 双次调用不覆盖前次 |
| 2 | `Clear-TestResultsDirs` 捕获删除错误(探针以 `FileShare.Read` 锁定旧文件)后仍无条件 Save:`purged=0` 但旧日志 `OLD-RUN-FAILURE-ONLY` 进入"本次证据" | purge 失败的目录不再按已清空处理:删除前快照(路径+SHA256),收集时排除与快照路径+哈希双吻合的文件,记入 meta `NOT CLEARED`/`excluded stale files` 与 `collection-errors.log`(gap 如实记录);内容已变化的同名路径视为本次产物;退出码/计数/门禁原样 | S6:锁定旧日志+当前 PASS→exit 0、PASS、旧标记零泄漏、meta 记录 NOT CLEARED+排除项;S7:同一锁定+当前新失败→exit 1、FAIL、计数 2/1/0、收集日志含当前 sentinel 无旧标记 |
| 3 | `Invoke-Captured` 先缓存全部输出,命令抛异常时无任何落盘(`captured output file exists=False`) | 改为流式捕获+`finally` 落盘:异常前输出与异常文本都写盘;返回 `$null`,上层记 `EXCEPTION` 并按非 0 门禁处理(不伪造 0);meta 标注 "invocation exception; exit unknown" | S8:抛异常→返回 `$null`、日志存在且含 `OUTPUT-BEFORE-EXCEPTION` 与 `SIMULATED-RUNNER-EXCEPTION`;对照:正常命令真实退出码 3 原样返回 |

文档保证表述同步改为非绝对版(§2、§4.1):不再声称"旧结果永不混入"(purge 失败路径以快照排除+gap 记录代替,非零残留风险如实列于 §6),不再声称"任何失败都可诊断"(进程在文件落盘前崩溃的情形不在覆盖内)。

## 8. 返修记录二(caller 检视 round 2 `31580fb` 未通过 → round 3 本轮修复)

caller 用真实 Windows 独占锁探针 `artifacts/caller-review-round2/Probe-UnreadableStale.ps1`(AST 抽取当轮生产 `Get-TestResultsSnapshot`/`Clear-TestResultsDirs`/`Save-TestResults`,无 mock、无业务改动)在 `31580fb` 上复现第四项缺陷:

| 复现(round 2 实测) | 根因 | 修复(round 3) | 回归证明 |
|---|---|---|---|
| 旧 `old-run.log` 以 `FileShare.None` 独占打开:枚举可成功但 `Get-FileHash` 失败,快照 SHA 为占位符 `(hash unavailable)`;runner 执行期间释放锁并另写 `current.log` 后,收集期旧文件重哈希成功、不再等于占位符→`collected=2/excluded=0/oldLogLeaked=True` | round 2 的排除条件是"快照哈希==收集哈希",把**哈希不可读**当成了"哈希不同"——哈希缺失被误用为内容变化证据;且 `Get-TestResultsSnapshot.Error`(枚举失败)完全被忽略,快照失明时未入快照的旧文件会被当新文件 | (1) 快照条目带 `HashOk` 标志;收集规则改为:**双侧哈希可读且不同**才视为本次重写予以收集,**任一侧不可读→保守排除**(哈希缺失≠内容变化);(2) `SnapshotError` 传播到 purge 状态,**快照枚举失败→整个目录不可信、全部排除**,meta 标签与 `collection-errors.log` 记明;(3) 退出码/计数/门禁原样 | S9(独占锁+运行中释放+PASS):exit 0、PASS、旧内容零泄漏、meta 记"hash unavailable at snapshot time";S10(同机制+FAIL):exit 1、FAIL、2/1/0、当前 sentinel 在、旧标记零泄漏;S11(故障注入快照失明+真实锁):0 收集、2 条排除、collection-errors 记 snapshot enumeration failed。S9/S10 用真实文件锁证据,S11 为允许的故障注入,两者区分 |

文档同步校正(caller round-3 指出):§5.3/§6 中"caller 统一生产全量"的归属——该全量是在 **round 1 检视期间**运行(round 1 代码),不是在 round 2/3 返修代码上运行,不得把旧测试归给新代码;并删除文末重复的 artifacts 说明行。

## 9. 提交与状态

- 分支:`wt/v5-ci-diagnostics-20261024`;round 1 交付 `7a9aa6c339fb84e557de5e617f43827c71a714bf`、round 2 交付 `31580fb`(均已被 caller 判未通过,见 §7、§8);round 3 返修为同分支同 4 文件范围的新本地提交(未 push,由 caller 独立验收后统一收口既有唯一 PR19 `feat/v5-1-integration -> v5`,push/MR/merge-back 均由 caller 执行)。交付 commit 以 `git log` 实际记录为准。
- `artifacts\`、`artifacts\verify-v5-selftest-*\` 为未跟踪本地证据目录,按本项目先例不提交;caller 的检视证据 `artifacts\caller-review-round1\`、`artifacts\caller-review-round2\` 原样保留,未改动。
