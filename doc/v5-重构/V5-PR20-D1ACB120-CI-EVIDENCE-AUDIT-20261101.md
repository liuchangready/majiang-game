# PR20 d1acb120 CI 原生证据独立审计

日期：2026-11-01（Asia/Shanghai）<br>
范围：仅审计冻结源码头 `d1acb120cc2b753c07d2e267af57461e61169026` 的两次既有 CI run。<br>
结论口径：判断这两次 run 的 CI 原生证据；不构成 Android/Unity/Player 运行验收、C03/C04/C05 集成验收或 G2 审批。

## 1. 范围与工作区

- 分支：`wt/v5-pr20-d1ac-ci-audit-luna6-20261101`。
- 审计开始时 HEAD：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；工作区 clean。审计目标代码及脚本均按完整冻结 SHA `d1acb120cc2b753c07d2e267af57461e61169026` 用 `git show` 读取，没有从旧 main 取代码。
- 唯一提交文件为本报告。原始下载、run/job JSON、日志、zip、逐文件 SHA-256/长度清单、源码副本、解析器及临时 XML 均位于 git-ignored 的 `build/pr20-d1ac-audit-20261101-7f4d2a91-8d66-47e8-91d1-a2b633f81d2a/`。
- PR20 在本次复核时已从冻结头移动到 `60dec69a63d7d9474c86dca863a8764b7489138e`，仍为 OPEN/Draft，base 为 `v5`。按任务约束，本报告仍只判断冻结头及其指定两次 run。
- 配置路由为 Codex，调用方给定 `configuredModel=gpt-6-luna`，本轮未设置模型覆写。当前执行环境不向我暴露 serving runtime 的模型标签，因此我不能独立证明后端实际模型标识；报告只陈述可见配置，不伪称有模型遥测证据。

本轮未派发或重跑 CI，未创建 CI ref，未 push、更新 PR、审批任务、记录 merge、merge、deploy、改 scheduler 或 `complete.flag`。未运行 Unity、Player、adb、设备或 `Local\MahjongV5P45UnityValidation`。

## 2. 两个 run 的原生身份与 checkout

通过 GitHub API 读取 run、jobs、artifacts，并读取完整 job 日志。API 返回的 `head_sha`、`run_attempt` 等只用于定位和交叉核对；测试结论由下载的原始 logs/artifacts 独立复算。

| Run / job | 事件 | head SHA | attempt | workflow / job | run / job 结论 | 时间（UTC） |
|---|---|---|---:|---|---|---|
| [41244381363](https://github.com/liuchangready/majiang-game/actions/runs/41244381363) / 124664649547 | `pull_request` | `d1acb120cc2b753c07d2e267af57461e61169026` | 1 | `v5-verify` / `verify` | completed / success | 21:36:25–21:57:08 |
| [41244378077](https://github.com/liuchangready/majiang-game/actions/runs/41244378077) / 124664639249 | `push` | `d1acb120cc2b753c07d2e267af57461e61169026` | 1 | `v5-verify` / `verify` | completed / success | 21:36:24–21:58:30 |

冻结 workflow 为 `.github/workflows/v5-verify.yml`。两个 job 的实际步骤均为 setup job、Checkout、Setup .NET 8 SDK、Verify native failure exit propagation、Run unified V5 verification、Upload verification logs and test-framework diagnostics，以及 post/complete 步骤；所有列出的步骤 conclusion 均为 success。jobs API 的步骤编号从 6 跳到 11，但名称、时间和日志连续，没有漏跑证据。

PR run checkout 了 `refs/pull/20/merge` 合成提交 `bf67ff1276a5c9a77fba1498706685b49ed7fb58`，而不是字面 source SHA。GitHub commit API 记录它的两个父提交为 base `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` 和 source `d1acb120cc2b753c07d2e267af57461e61169026`，且 merge commit tree 与冻结 source tree 都是 `ca38424eccd4b35b18bc4a8b5a1722d1f670e6e3`。因此 PR 事件的 checkout 身份是合成提交，但其源码 tree 与冻结头完全相同。Push run 日志及 artifact `run-info.txt` 记录的 head 是字面冻结 SHA。该 provenance 事实为 PASS，不将合成 SHA 混写为 source SHA。

冻结 workflow 调用：

```text
./tools/ci/test-v5-native-exit.ps1
./tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -RunUnifiedVerification -LogDir artifacts/verify-v5/table -UnifiedLogDir artifacts/verify-v5/unified
```

经 `git show <冻结SHA>:<路径>` 只读核对了 `.github/workflows/v5-verify.yml`、`tools/ci/verify-v5-table-postgres.ps1`、`tools/verify-v5.ps1` 和 `tools/ci/test-v5-native-exit.ps1`；没有修改这些门禁或生产代码。

## 3. 原始证据、下载完整性与复核命令

获取命令（所有 API 为 GET；完整日志由 Actions logs API 取回，artifact 用 run download 和 artifact ZIP API 分别取回）：

```powershell
gh api repos/liuchangready/majiang-game/actions/runs/41244381363
gh api repos/liuchangready/majiang-game/actions/runs/41244381363/jobs
gh api repos/liuchangready/majiang-game/actions/runs/41244381363/artifacts
gh run view 41244381363 --repo liuchangready/majiang-game --log
gh run download 41244381363 --repo liuchangready/majiang-game --dir <build独占目录>/pr-artifacts

gh api repos/liuchangready/majiang-game/actions/runs/41244378077
gh api repos/liuchangready/majiang-game/actions/runs/41244378077/jobs
gh api repos/liuchangready/majiang-game/actions/runs/41244378077/artifacts
gh run view 41244378077 --repo liuchangready/majiang-game --log
gh run download 41244378077 --repo liuchangready/majiang-game --dir <build独占目录>/push-artifacts
```

原始 artifact ZIP 的 SHA-256 与 GitHub artifact API `digest` 相同；ZIP 内每个文件的大小及 SHA-256 另行逐一计算并写入下列完整清单（每 run 270 文件，清单本身也有 SHA-256）：

| Run | Artifact ID / server digest | ZIP 字节 / SHA-256 | 解压文件 / 未压缩字节 | 逐文件清单 SHA-256 |
|---|---|---|---:|---|
| PR | `13059323003` / `sha256:51b97fb89bb91a15056767fc4c5f8d5e2c36223ddd9fe8bdc0fea3001beedaa5` | 4,503,218 / `51b97fb89bb91a15056767fc4c5f8d5e2c36223ddd9fe8bdc0fea3001beedaa5` | 270 / 13,188,065 | `pr-file-manifest.json` `b4bbeb7d523c9ca7b78af76295a8198a0b2a3ef02c54c0ca774ac2fb882d3b5c` |
| Push | `13058638253` / `sha256:ed6f44eb281c429d4ffd1e3d33549109cbffd23eec19e3cb053896acc77eea26` | 4,503,979 / `ed6f44eb281c429d4ffd1e3d33549109cbffd23eec19e3cb053896acc77eea26` | 270 / 13,189,537 | `push-file-manifest.json` `2ffaecc8b540b04e8eb6eac9ae91437020e3035288533709a05ff54f9a88a7be7` |

完整 job log ZIP 也保存并计算 SHA-256：PR 7,531 字节，`c34f35c2597c0ce36f3388d4da9b1fb18d16f83c16787def63a57a2663ceafea`；Push 7,174 字节，`cae71ec9a24cc15450fedaf0d5a5f6055e682ad20cc308cdcba717359f3211c26`。`gh run view --log` 的文本日志 SHA-256 分别为 PR `f9f6a6d035e35ed512dee333a7182c24721923a2fd208ee5c93b8a36a02c2466`、Push `e24fcc5fde0afac2299826feff62bbc0f3ffaeffc398af80cb59ecf10b42cc9b`。

内部 table manifest 在两个 artifact 中各列 152 项；将 manifest 的 Windows 路径分隔符规范化后，152/152 项均与 ZIP 文件长度和 SHA-256 相符。Unity provenance 的 19 个输出也均与 ZIP 内文件长度和 SHA-256 相符。以上清单包括完整 artifact 的逐文件 SHA/长度，不依赖 GitHub run 的绿色元数据。

## 4. 原生测试结果独立复算

从每份 artifact 的 14 个 `verify-v5/unified/direct-xml/<工程>/direct-results.xml` 读取 xUnit v3 XML，逐例计算 case ID、`name`、`result`；分别重算 assembly 与 collection 数并检查与 `<test>` 节点相等。另交叉检查每个 `direct.meta.txt` 的 native exit、逐例 XML gate 和运行前 TestResults 清理记录。两 run 的 per-project `(工程, test name, result)` 多重集完全一致；每个 run 内 2,813 个 test ID 均唯一。测试显示名有两个重复组（参数化测试），但对应 test ID 不重复。

| 工程 | Assembly / collection 数 | 每 run P/F/E/S/N/T |
|---|---:|---:|---:|
| Mahjong.Ai.Tests | 1 / 13 | 444/0/0/0/0/444 |
| Mahjong.Architecture.Tests | 1 / 1 | 132/0/0/0/0/132 |
| Mahjong.Client.Tests | 1 / 10 | 182/0/0/0/0/182 |
| Mahjong.Client.Wss.Tests | 1 / 6 | 82/0/0/0/0/82 |
| Mahjong.Contracts.Tests | 1 / 8 | 152/0/0/0/0/152 |
| Mahjong.Domain.Tests | 1 / 34 | 529/0/0/0/0/529 |
| Mahjong.Infrastructure.Tests | 1 / 1 | 42/0/0/0/0/42 |
| Mahjong.LobbyService.Tests | 1 / 11 | 89/0/0/0/0/89 |
| Mahjong.LocalHost.Tests | 1 / 18 | 127/0/0/0/0/127 |
| Mahjong.P5.4b.B1.HttpsAdmission.Tests | 1 / 4 | 54/0/0/0/0/54 |
| Mahjong.TableRuntime.Tests | 1 / 57 | 483/0/0/0/0/483 |
| Mahjong.TableService.DurableHost.Tests | 1 / 2 | 59/0/0/0/0/59 |
| Mahjong.Unity.TableModel.Tests | 1 / 12 | 235/0/0/0/0/235 |
| Mahjong.TableService.Tests | 1 / 19 | 203/0/0/0/0/203 |
| **直接 runner 合计** | **14 / 196** | **2,813/0/0/0/0/2,813** |

`P/F/E/S/N/T` 分别为 passed、failed、errors、skipped、not-run、total；该表每行分别适用于 PR 和 Push，两 run 的逐例 `(工程, test name, result)` 多重集一致。Table 阶段的额外结果为每 run full `42/0/0/0/0/42`、targeted `13/0/0/0/0/13`。

每个直接 runner XML 的 assembly 属性、collection 合计、实际 test 节点都一致；每 run 的唯一 ID 数为 2,813；`failed=0`、`errors=0`、`skipped=0`、`not-run=0`，所有节点 `result=Pass`。14/14 `direct.meta.txt` 显示 native exit 0、XML gate PASS，并记录运行前清理 TestResults 的事实。构建阶段 fresh-build 成功；没有构建失败后继续使用旧 DLL 的证据。

每 run 另有 table PostgreSQL 阶段 XML：`tests.xml` 为 42/42 Pass，`tests-targeted.xml` 为 13/13 Pass，各自 1 assembly / 1 collection / 节点 ID 唯一。13 项是 42 项集合的子集。它们是单独阶段的额外执行：执行实例数为 `2,813 + 42 + 13 = 2,868`；**唯一案例合计仍为 2,813**，不把 targeted 13 再加进唯一案例数。

## 5. 构建、程序集闭包与 native exits

- 每 run 检查 15 份构建日志：solution build、11 个独立 build、table tests/worker build、plugin-copy。总计每 run 均为 `0 Warning(s)` / `0 Error(s)`，并对 `warning CS/NU`、`error CS/NU` 行扫描为 0。Actions 日志另有 Node/punycode deprecation 提示；它们属于 runner/action runtime，不是 .NET 编译 warning/error。
- `shared-plugin-provenance.json` 声明 `forced-clean-outputs=true`，清理七个自有项目的 Release bin/obj 输出后重建，149 个输入、19 个输出，构建 exit 0。19 个文件为 12 DLL + 7 PDB；DLL 为 7 个自有程序集（Domain、Contracts、Contracts.Wire、TableRuntime、Client、LocalHost、Ai）和 5 个包程序集（Google.Protobuf、System.Buffers、System.Memory、System.Numerics.Vectors、System.Runtime.CompilerServices.Unsafe）。两 run 的 19/19 文件均与 provenance 的长度/SHA-256 一致；没有额外插件输出。该 12-DLL 闭包不是测试 assembly 数，测试 XML 为 14 个 test assembly。
- 每份 `native-exit-*/results.json` 均含 6/6 通过项：预期退出码 0、1、2、17，超时异常，以及被强杀 writer（实际 -1，期望非零）。`test-v5-native-exit.ps1` 整步 native exit 0。超时项有原始 stdout/stderr 各 15 字节；缺失的合并日志作为本报告 MINOR 记录，不把该缺失伪装成存在。
- 两 run 的 P52 阶段均为成功：fresh build、42 项全量 Infrastructure 测试、13 项 targeted、crash writer、immediate PostgreSQL restart/recovery、reader、backup/restore、unified verification 和 cleanup。除预期的 crash writer 为 `KILLED`（-1）外，所记录阶段退出均为 0；workflow 的 unified verifier、外层 wrapper 及 CI step 均为 0/success。
- SDK 记录为 PR run `8.0.426`、Push run `8.0.427`（summary、sdk-diagnostic 与 table wrapper 记录相符）。冻结 workflow 请求 8.0.426；冻结 `global.json` 使用 `rollForward=latestPatch`，故 runner 的实际 patch 版本不同是已记录的 roll-forward 结果，不是把 .427 写成 .426。本轮不是单一 patch 版本的逐位可复现构建声明。

## 6. 真实 PostgreSQL 证据

该证据来自上传的 PostgreSQL server log、pg_ctl/psql/dump/restore 日志、crash worker 结果、marker、reader JSON、evidence JSONL 与 wrapper 阶段记录；不是内存测试或 wrapper 单独自述。

| 流程 | 观察到的原生证据 | 结论 |
|---|---|---|
| Table PG | PostgreSQL 16.10，loopback `127.0.0.1:55592`；真实 initdb / pg_ctl start；独立 crash worker 写入已提交状态与 outbox marker 后被强杀；`pg_ctl stop -m immediate` 后重启，新 postmaster 启动；服务端日志含 `automatic recovery in progress`、`redo starts`、`redo done` 和 ready。 | PASS |
| Crash reader | 重启后独立 reader 返回状态版本 1、事件序号 2、终态、Applied receipt、outbox 存在且 payload hash 与 crash marker 一致、cheat state restored、`recovered=true`。 | PASS |
| Backup/restore | `pg_dump` custom format 到 dump 文件；恢复到空数据库后 reader 验证同一持久状态/outbox，恢复 owner epoch 前进；pg_dump/pg_restore/权限阶段 native exit 0。 | PASS |
| Lobby PG | 独立 PostgreSQL 16.10 loopback `127.0.0.1:55593`；`postgres-evidence.jsonl` 每 run 14 条事件，覆盖 migration 重放、真实连接并发、终态投影和回执重放等；wrapper 所有 stage exit 0，owner-verified cleanup 与环境恢复成功。 | PASS |

报告不包含牌面、种子、私有账本内容、账号标识或凭据。PG server 原始日志中预期的负例权限/取消事件没有作为测试失败计数。

## 7. 问题与边界（按严重级别、状态）

当前冻结头审计范围内没有 BLOCKER、MAJOR 或业务测试 FAIL。下列 MINOR/NOT_RUN 项是 CI 门禁覆盖或证据完整性边界；不推翻当前逐例 XML 与真实 PG 已通过的事实。

### MINOR / FAIL：table XML gate 只信 assembly 汇总

- 位置：冻结 `tools/ci/verify-v5-table-postgres.ps1:725-753` 的 `Get-XmlCounts` / `Test-XmlCountsPass`；其 inline `Assert-XmlGatingSelfTest` 仅测 7 个反例。
- 当前源码复现：独立只读探针从冻结源码 AST 提取上述原函数及 `tools/verify-v5.ps1` 的 strict `Read-DirectResultXml`，只将两个 XML fixture 写入本报告的 ignored build 目录。命令：

  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/pr20-d1ac-audit-20261101-7f4d2a91-8d66-47e8-91d1-a2b633f81d2a/repro-table-gate.ps1 -AuditDir build/pr20-d1ac-audit-20261101-7f4d2a91-8d66-47e8-91d1-a2b633f81d2a
  ```

- 实际：`assembly-only-green`（assembly total=7/passed=7、0 test nodes）及 `duplicate-id-green`（汇总全绿但两节点 ID 相同）都被 table helper 判绿；同冻结版本 strict parser 分别以 test node 数不符和 duplicate ID 判 invalid。探针输出 SHA-256：`gate-gap/RESULT.txt` `58df4b2e1c26e402cc7569abf38fbfd6acfad207315a309bb6aa58dab9549adf`。冻结源码与旧头报告中描述的门禁边界相同。
- 当前实际数据：table 的 `tests.xml`/`tests-targeted.xml` 各自有 42/13 个唯一节点；unified strict runner 对同一 Infrastructure suite 的直接 XML 也有 42 个完整节点。本次没有 false-green XML 结果。
- 期望/可执行验收断言：对同两 fixture，table helper 必须拒绝 assembly-only XML 和重复 ID XML；有效 42/13 结果仍逐项与节点、collection、assembly 计数一致。修复门禁不在本次任务范围内。

### MINOR / FAIL：native timeout 合并日志缺失

- 位置：每份 artifact 的 `native-exit-<id>/timeout.log`。冻结 `test-v5-native-exit.ps1` 在 timeout case 向生产 `Invoke-Native` 传该 LogPath；超时分支抛异常，未产生合并文件。
- 实际：两个 artifact 均无 `timeout.log`，但 `timeout.log.stdout` 与 `.stderr` 各 15 字节，`results.json` 的 timeout case 为 `Pass=true`；另外 5 个 native-exit case 有预期结果。逐文件 SHA/长度在对应完整 file manifest 内。
- 复现命令：

  ```powershell
  # 只读查询已下载 ZIP 内条目；不重新运行 native-exit 测试
  powershell.exe -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; `$z=[IO.Compression.ZipFile]::OpenRead('build/pr20-d1ac-audit-20261101-7f4d2a91-8d66-47e8-91d1-a2b633f81d2a/pr-artifact.zip'); `$z.Entries | Where-Object FullName -match 'native-exit-.+/timeout' | Select-Object FullName,Length"
  ```

- 期望/可执行验收断言：timeout 路径应保存合并日志，或消费方明确以两个原始 stream 作为证据；仅 `Pass=true` 不证明合并日志存在。当前缺文件是证据完整性 MINOR，不表示 timeout 行为断言失败。

### MINOR / NOT_RUN：strict XML 门禁自检与 dotnetTest 门禁自检未由 workflow 执行

- 位置：冻结树中的 `tools/ci/test-v5-direct-results.ps1` 19 例矩阵、`tools/ci/test-v5-dotnettest-results.ps1`；workflow 两个 job 的完整 steps/logs 未调用它们。实际执行了 table wrapper inline 的 7 例 XML selfcheck。
- 实际：本轮 `RunnerMode=direct`，14 份 direct XML 都逐例有效；dotnetTest/TRX 路径没有被执行，direct 19 例门禁自检也未执行。
- 复现命令：在目标 run 日志和冻结 workflow 源码中检索 `test-v5-direct-results.ps1` 与 `test-v5-dotnettest-results.ps1`，均无 step/invocation；artifact `verify-v5/table/tmp/xml-selfcheck/` 只有 7 个 inline fixture。
- 期望/可执行验收断言：不得将“19 例 CI 自检通过”或“dotnetTest 路径已验证”写成 PASS。若后续宣称这两条路径由 CI 覆盖，job 必须有对应测试调用和原生 exit/逐例结果。

### MINOR / NOT_RUN：历史 golden `git pins` 检查未运行

- 位置：每份 artifact `verify-v5/unified/summary.log` 的 `git pins : NOT_RUN` 行。
- 实际：当前 golden fixture 字节及嵌入 provenance 可离线检查，但 historical freeze blob 未请求/比对。
- 复现命令：对解压 artifact 的 `verify-v5/unified/summary.log` 执行 `Select-String -Pattern '^\s*git pins\s*:'`；输出明确为 NOT_RUN。
- 期望/可执行验收断言：只可声称当前文件字节与 embedded provenance 已检查，不可声称历史 Git pin 已验收。

### BLOCKED / BLOCKED（历史项，未被本轮关闭）：run 39970580589 的 Client 失败缺框架日志

- 位置：旧头独立审计报告 `a44dd7b86badb3d3a19a8850862aafa04bb04f57` §9 所记历史 run `39970580589`。
- 实际：当时 `Mahjong.Client.Tests` 为 Failed 1 / Passed 131 / Total 132，但历史 artifact 缺该次 TestResults 逐例框架日志，根因仍无法判定。
- 期望/可执行验收断言：只在找回该原始 run 的缺失框架日志并独立复核后才能关闭该历史项。本轮 d1ac 两 run 的 Client 182/182 通过不构成旧失败根因关闭证据。

### INFO / PASS：SDK patch 版本随 runner 的 rollForward 结果不同

- 位置：冻结 workflow 的 `8.0.426` setup 与冻结 `global.json` `rollForward=latestPatch`；PR 实际 8.0.426、Push 实际 8.0.427，见各自 `sdk-diagnostic.log`、table `wrapper-summary.json` 及 fresh-build stage。
- 实际：两个 run 均记录真实 patch 版本，CI 成功；不是将 8.0.427 报成 8.0.426。
- 期望/可执行验收断言：若以后要求 bit-for-bit 固定 SDK，应以日志证明同一 patch 或禁用 roll-forward；本轮不声称 SDK patch 固定一致。

### INFO / PASS：PR 合成 checkout 与 source SHA 不同

- 位置：PR run checkout `bf67ff1276a5c9a77fba1498706685b49ed7fb58`；source 是冻结 d1ac。两者 tree 均为 `ca38424eccd4b35b18bc4a8b5a1722d1f670e6e3`。
- 实际：tree 相同、父提交含目标 source SHA；push run 是字面 source SHA。未将 synthetic commit 误报为 source SHA。
- 期望/可执行验收断言：如验收要求字面 SHA checkout，应单独要求 PR job `checkout` source SHA；本次源码等价性已由 commit/tree 证据通过，不用它替代未来字面 SHA 要求。

## 8. 状态总表

| 检查 | 状态 | 结果 |
|---|---|---|
| 两 run SHA/event/attempt/workflow/job/checkout | PASS | PR merge tree 与 source tree 相同；Push 字面 checkout |
| 原始 job logs / artifact 下载与 SHA | PASS | artifact digest=ZIP SHA，270/270 文件 manifest；table 152/152、plugin 19/19 |
| 14 工程 direct XML / native exit / 唯一 ID | PASS | 每 run 2,813/2,813 Pass；0 fail/error/skip/notrun；集合多重集相同 |
| Table PG 42、targeted 13 | PASS | 42/42 与 13/13 节点完整，targeted 为子集，不加进 2,813 唯一案例 |
| 构建日志 | PASS | 每 run 15 份，0 warning / 0 error |
| 12-DLL 闭包及 provenance | PASS | 7 自有 + 5 包 DLL；19 输出逐文件哈希与长度一致，fresh clean build |
| 真实 table/lobby PostgreSQL 与 crash/restore | PASS | PG 16.10、真实端口/进程/重启 redo/读回/outbox/backup restore/cleanup |
| Table 汇总 XML helper 对 assembly-only / duplicate ID | FAIL / MINOR | 冻结源码探针复现判绿；strict reader 拒绝；当前真实 XML 本身有效 |
| Native timeout 合并日志 | FAIL / MINOR | 合并文件缺失，原始 stdout/stderr 与 result 仍在 |
| Direct 19 例门禁自检、dotnetTest 门禁自检 | NOT_RUN / MINOR | workflow 未调用；本次真实 direct XML 已逐例复核 |
| Historical git pins | NOT_RUN / MINOR | artifact 明确写 NOT_RUN |
| 历史 Client 失败根因 | BLOCKED / 历史 | 缺旧 run 框架日志；本次绿色不关闭 |
| BLOCKER / MAJOR / 当前业务测试 FAIL | 无 | 本次冻结头 CI 证据没有此类发现 |

## 9. 复核入口与交付边界

原始证据/每文件 SHA-256 均留在 ignored build 目录：

```text
build/pr20-d1ac-audit-20261101-7f4d2a91-8d66-47e8-91d1-a2b633f81d2a/
```

逐文件清单：`pr-file-manifest.json`、`push-file-manifest.json`；完整 ZIP：`pr-artifact.zip`、`push-artifact.zip`、`pr-logs.zip`、`push-logs.zip`；独立 XML 汇总探针：`audit-probe.ps1`；table gate 当前源码复现：`repro-table-gate.ps1` 与 `gate-gap/RESULT.txt`；跨 run/manifest/provenance 校验：`verify-cross.ps1`。所有下载及解析结果均不纳入提交。

只提交此报告，不宣布本范围之外的门通过。等待调用方独立复核与集成。
