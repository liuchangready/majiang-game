# V5 PR20 `392b973` 双 CI 原始证据独立复核（2026-10-28）

## 0. 结论

冻结源码头：`392b9739aff96d0fa83acd4bef3c2a3667ccc7d4`（PR20，目标 `v5`）。
本轮只读复核两个新头 run：

- push [40624278181](https://github.com/liuchangready/majiang-game/actions/runs/40624278181)
- pull_request [40624283836](https://github.com/liuchangready/majiang-game/actions/runs/40624283836)

结论：**两个 run 在可取得的原始证据上 PASS，未发现新的 material code/test defect。**
但有一个验收子项必须单独标 **BLOCKED**：11 个 direct test runner 没有逐 runner XML/TRX，
因此“每个测试程序集的 XML test case 节点与计数一致”无法用本次 artifact 证明。计数与
native exit 是用 `direct.meta.txt` + raw runner console summary 逐项重算的；没有把不存在的
XML 写成“XML 已验证”。PostgreSQL 的 `tests.xml` / `tests-targeted.xml` 确实存在，并已逐节点
解析核对，见 §7。

本报告只描述 `392b973` 的两个新 run，不复制 `9136508`、`c694140` 或任何更早 run 的结论。
G1 仍为 APPROVED；G2–G5 未批准；V4 仍是生产基线；181 classified 仍未覆盖。

---

## 1. 冻结基线、隔离与范围

工作树：`D:\majiang-wt\pr20-ci-392b973-ds-20261028`
分支：`wt/v5-pr20-ci-392b973-ds-20261028`

| 项 | 实测值 |
|---|---|
| `git rev-parse HEAD` | `392b9739aff96d0fa83acd4bef3c2a3667ccc7d4` |
| HEAD parent | `8e7431969ce857c9223255ea7e2b38132560e970` |
| HEAD tree | `b5fd59189611a139ca901b3382a4498bb83b2f0b` |
| `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| `git merge-base --is-ancestor 8fd7c6c HEAD` | exit 0 |
| `git status --porcelain=v1` | 空（冻结时） |
| `git diff --check 392b973..HEAD` | exit 0 |

未 push、未创建/修改 PR 或 MR、未合并、未部署、未创建 `complete.flag`；未调用 bridge、
未转派、未 resume/retry/retarget、未操作 GLM/Anti 线程或其它工作树。B2b 代码审查/返修不在
本轮范围；本任务只核验本头 CI 证据。

---

## 2. 两个 run 的身份、实际 checkout 与 artifact

### 2.1 Metadata

| 项 | push 40624278181 | PR 40624283836 |
|---|---|---|
| workflow / name | `.github/workflows/v5-verify.yml` / `v5-verify` | 同左 |
| event | `push` | `pull_request` |
| head_branch | `feat/v5-post-g1-batch` | `feat/v5-post-g1-batch` |
| `head_sha` | `392b9739aff96d0fa83acd4bef3c2a3667ccc7d4` | `392b9739aff96d0fa83acd4bef3c2a3667ccc7d4` |
| status / conclusion | `completed` / `success` | `completed` / `success` |
| run_attempt | 1 | 1 |
| created / updated (UTC) | `03:01:16Z` / `03:09:02Z` | `03:01:20Z` / `03:09:34Z` |
| job | `122605350080` `verify` success | `122605369709` `verify` success |
| artifact | `12789305729` `v5-verify-logs-40624278181-1` | `12788109635` `v5-verify-logs-40624283836-1` |
| artifact size | 4,092,527 bytes | 4,092,488 bytes |
| artifact digest | `sha256:841cb281326ac030a0ce22b093ce626ba741ed80ec29b094329b0949670067ae` | `sha256:3153f1ecb8c1d5f8f81b2ecb68b5346123a73026908821998adff8ead8b2957f` |
| 实际 checkout | `392b9739aff96d0fa83acd4bef3c2a3667ccc7d4` | `303d22452cb8bd9cbf2f86a28f3beefb0f012287` |

两个 run 的 `pull_requests` 都指向 PR20，base 为 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
push run 的 event 是 `push`，不是 `pull_request`；PR run 的 event 是 `pull_request`。与实际
元数据一致，没有 event 对应关系异常。

### 2.2 PR 实际 checkout 证明（不是拿 headSha 代替）

PR run 的 checkout 日志原文：

```text
fetch ... +303d22452cb8bd9cbf2f86a28f3beefb0f012287:refs/remotes/pull/20/merge
checkout --progress --force refs/remotes/pull/20/merge
HEAD is now at 303d224 Merge 392b9739aff96d0fa83acd4bef3c2a3667ccc7d4 into 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
```

只读核验：

```text
git ls-remote origin refs/pull/20/head refs/pull/20/merge
392b9739aff96d0fa83acd4bef3c2a3667ccc7d4  refs/pull/20/head
303d22452cb8bd9cbf2f86a28f3beefb0f012287  refs/pull/20/merge
exit 0

git fetch --no-tags origin refs/pull/20/merge
exit 0
git rev-parse FETCH_HEAD
303d22452cb8bd9cbf2f86a28f3beefb0f012287

git cat-file -p 303d22452cb8bd9cbf2f86a28f3beefb0f012287
tree b5fd59189611a139ca901b3382a4498bb83b2f0b
parent 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
parent 392b9739aff96d0fa83acd4bef3c2a3667ccc7d4

git rev-parse 303d224^{tree} -> b5fd59189611a139ca901b3382a4498bb83b2f0b
git rev-parse 392b973^{tree} -> b5fd59189611a139ca901b3382a4498bb83b2f0b
git diff --quiet 392b973 303d224 -> exit 0
```

因此 PR 的实际 checkout 是临时 merge 对象 `303d224`，其两个 parent 恰为已接受基线
`8fd7c6c` 与冻结源码 `392b973`；merge tree 与冻结源码 tree 全等（同一 tree 对象，且
`git diff --quiet` exit 0）。这不是用 API `head_sha` 冒充实际 checkout。push run 的实际
checkout 则是 `392b973` 本身（checkout 日志的 fetch refspec 为
`+392b973...:refs/remotes/origin/feat/v5-post-g1-batch`）。

---

## 3. 下载、原始日志与独立审计

证据根（ignored）：

```text
E = D:\majiang-wt\pr20-ci-392b973-ds-20261028\build\ci-audit-392b973-04d71aeecccb4ccfab094e87770ecfc8
```

实际执行的原生命令/退出码：

```powershell
gh api repos/liuchangready/majiang-game/actions/runs/40624278181            # exit 0
gh api repos/liuchangready/majiang-game/actions/runs/40624278181/jobs       # exit 0
gh api repos/liuchangready/majiang-game/actions/runs/40624278181/artifacts  # exit 0
gh api repos/liuchangready/majiang-game/actions/runs/40624278181/logs       # exit 0, raw zip 16,503 bytes
gh run view 40624278181 --log                                               # exit 0
gh run download 40624278181 --dir E\40624278181\artifacts                   # exit 0
gh api repos/liuchangready/majiang-game/actions/artifacts/12789305729/zip   # exit 0, 4,092,527 bytes
# pull_request 40624283836 执行同组命令，raw zip 17,088 bytes，artifact 4,092,488 bytes
```

两个 artifact zip 的本地 SHA256 与 GitHub API `digest` 逐字节一致（见 §2.1），每个 artifact
解压后为 **226 files**。完整 raw job log、`gh run view` 串联日志、API run/jobs/artifacts JSON、
artifact zip 与解压内容均保留在 E，未覆盖原始日志。

独立审计脚本（只读，仅写 `audit-result.json`）：

```text
E/audit.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File E\audit.ps1
AUDIT-PASS checks=649 failures=0 limitations=2
audit native exit = 0
```

`limitations=2` 是同一事实在两个 run 各记录一次：11 个 direct runner 无逐 runner XML。
脚本实际解析/重算的内容包括：

- run/job/artifact metadata、event/branch/head、checkout 日志、artifact digest；
- `run-info.txt` 的 head、CI provenance、verifier script SHA；
- `summary.log` 的 11 个 test row + 3 个 helper row、总数、OVERALL；
- 每个 `direct.meta.txt` 的 exit/total/failed/skipped/head；
- 每个 `direct-<project>.log` 的 `Total/Errors/Failed/Skipped/Not Run`；
- 12 DLL 的精确集合、7 个 Mahjong 生产 DLL、0 个 Tests DLL；
- 6 个 native-exit case、forced-writer kill record；
- 4 个 companion fixture 的 case 数与 SHA（独立重算当前仓文件）；
- PG `tests.xml`、`tests-targeted.xml` 的 assembly totals 和每个 test 节点；
- writer marker / kill JSON / crash reader / restore reader 全字段一致性；
- table `manifest.sha256.json` 134/134（含长路径 `tmp\VBCSCompiler`，用 `\\?\` 前缀读取）；
- Lobby `wrapper-summary.json`、`migrate.log`、`postgres-evidence.jsonl`、`cleanup.log`、
  `env-restore.jsonl`、`evidence-sha256.txt` 12/12；
- 日志中不存在 `P52-TABLE-POSTGRES-FAIL` / `Tests failed:`。

脚本没有重跑 CI/Unity/PG，也没有把本地运行冒充远端结果。

---

## 4. 构建、12 DLL 与 11 个测试程序集

### 4.1 Release solution build

两个 run 的 `unified/sln-build.log` 均为：

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

`summary.log` 记录 `solution build : PASS (exit 0)`。sln 内 17 个项目；5 个不在 sln 的
test project（Architecture、Client.Wss、LobbyService、TableRuntime、Unity.TableModel）和
LobbyService.PostgresProbe helper 各自有独立 build log，均为 `Build succeeded` + 0/0。
table wrapper 的 fresh-build 也为 exit 0（`build-tests.log` / `build-worker.log`）。

### 4.2 12 DLL 闭包

`unified/plugins/` 实际为 19 个文件 = **12 DLL + 7 PDB**，DLL 精确集合：

```text
Google.Protobuf.dll
Mahjong.Ai.dll
Mahjong.Client.dll
Mahjong.Contracts.dll
Mahjong.Contracts.Wire.dll
Mahjong.Domain.dll
Mahjong.LocalHost.dll
Mahjong.TableRuntime.dll
System.Buffers.dll
System.Memory.dll
System.Numerics.Vectors.dll
System.Runtime.CompilerServices.Unsafe.dll
```

即 7 个 Mahjong 生产程序集 + Google.Protobuf + 4 个 System 兼容程序集；Tests DLL = 0。
两个 run 的集合与数量相同。`summary.log` 的 `12 assemblies` 与目录实际一致；本审计不是
只信 summary，而是逐个枚举并比对精确集合。

### 4.3 11 个 test 程序集的实际计数

两个 run 的 11 个 direct runner 计数**完全相同**。命令均为
`dotnet <test-dll> -noLogo -reporter default`，没有 `-class/-filter/-trait/-where`，每个
`Total > 0`，因此 `Total` 即 selected（完整程序集）。`passed` 由
`Total − Errors − Failed − Skipped − Not Run` 计算；四项均为 0，所以 `passed = Total`。

| # | 测试程序集 | selected=Total（push=PR） | passed | failed | errors | skipped | not-run | native exit |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | `Mahjong.Ai.Tests` | 444 | 444 | 0 | 0 | 0 | 0 | 0 |
| 2 | `Mahjong.Architecture.Tests` | 92 | 92 | 0 | 0 | 0 | 0 | 0 |
| 3 | `Mahjong.Client.Tests` | 182 | 182 | 0 | 0 | 0 | 0 | 0 |
| 4 | `Mahjong.Client.Wss.Tests` | 80 | 80 | 0 | 0 | 0 | 0 | 0 |
| 5 | `Mahjong.Contracts.Tests` | 149 | 149 | 0 | 0 | 0 | 0 | 0 |
| 6 | `Mahjong.Domain.Tests` | 529 | 529 | 0 | 0 | 0 | 0 | 0 |
| 7 | `Mahjong.Infrastructure.Tests` | 37 | 37 | 0 | 0 | 0 | 0 | 0 |
| 8 | `Mahjong.LobbyService.Tests` | 89 | 89 | 0 | 0 | 0 | 0 | 0 |
| 9 | `Mahjong.TableRuntime.Tests` | 400 | 400 | 0 | 0 | 0 | 0 | 0 |
| 10 | `Mahjong.TableService.Tests` | 152 | 152 | 0 | 0 | 0 | 0 | 0 |
| 11 | `Mahjong.Unity.TableModel.Tests` | 140 | 140 | 0 | 0 | 0 | 0 | 0 |
| | **合计** | **2,294** | **2,294** | **0** | **0** | **0** | **0** | **0** |

算术独立复核：`444+92+182+80+149+529+37+89+400+152+140 = 2,294`。这不是从旧报告
`2,288` 抄来的；与 0747 报告的 2,288 相比，本头 TableModel 为 **140**（+6），而
Infrastructure 为 37（0747 已为 37），所以总数是 **2,294**。本轮没有把 2,288 硬填进来。

3 个 **build-only helper** 不贡献测试数，未计入 2,294：

- `Mahjong.Outbox.CrashWorker`
- `Mahjong.Infrastructure.CrashWorker`
- `Mahjong.LobbyService.PostgresProbe`

`summary.log` 的 14 个 project row = 11 个 test + 3 个 helper；不能把 14 当作 14 个测试
程序集。P5.2 wrapper 另行执行的 Infrastructure 37/37 与 targeted 13/13 是重复执行/子集，
不追加进统一 2,294。

### 4.4 BLOCKED：11 个 direct runner 无逐 runner XML

这是本轮唯一未能完成的验收子项，必须明确标 **BLOCKED / NOT_AVAILABLE**，不能写成 PASS：

- `tools/v5-verify.manifest.json` 的 `directRunnerArgs` 为 `-noLogo -reporter default`，
  没有 `-xml`/`--report-xunit`/`--report-trx`；
- 两个 artifact 中只有 `runs/<project>/direct.meta.txt` 和
  `direct-<project>.log`；没有 `test-results/<project>/`，也没有 11 个 runner 的
  XML/TRX；
- 因此无法用 XML test case 节点证明每个 case 的 Pass/Fail；对 11 个 runner 的权威原始记录
  是 meta + raw console summary；
- PostgreSQL 的 `tests.xml` / `tests-targeted.xml` 是 **PG 项目自己的 XML**，不能拿来代替
  11 个 direct runner 的逐用例 XML。

若验收要求必须逐 runner XML，则本子项应按 `BLOCKED/NOT_RUN` 处理，而不是把 2,294 的
console summary 写成“XML 已验证”。

---

## 5. 六项 native exit 传播反证与 companion fixtures

### 5.1 六项 native exit

两个 run 的 `native-exit-*` 目录各有一份 `results.json`，6/6 全 Pass：

| case | expected | actual（两个 run 相同） | pass |
|---|---|---:|---:|
| exit-0 | 0 | 0 | true |
| exit-1 | 1 | 1 | true |
| exit-2 | 2 | 2 | true |
| exit-17 | 17 | 17 | true |
| timeout | throw | true | true |
| forced-writer | nonzero | -1 | true |

`forced-writer` 的 `writer.log.kill.json` 记录 `exitCode=-1`、`forced=true`；这是强杀后的
非零退出，不是 native 0。timeout case 的 stdout/stderr 也在 artifact 中。没有 exit-6
case；本轮不声称跑过 exit-6（若验收另有 exit-6 要求，应标 NOT_RUN）。

生产函数确实来自实际脚本，不是 mock：`tools/ci/test-v5-native-exit.ps1` 用 AST 从
`tools/ci/verify-v5-table-postgres.ps1` 抽取 `Invoke-Native`、`Invoke-WriterAndKill`、
`Get-Sha256`；`Invoke-Native` 执行 `$null = $process.Handle`，并在 `$null -eq $nativeExit`
时抛错；`Invoke-WriterAndKill` 同样保留 handle、拒绝 null，并 `Stop-Process -Force`。
本审计对这 6 项及上述源码锚点做了检查。

### 5.2 四个 companion fixture gate

实际命令（`companion-fixtures.meta.txt` 原文）：

```text
C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive `
  -ExecutionPolicy Bypass -File ...\tools\verify-v5-companion-fixtures.ps1 `
  -RepoRoot ... -FixtureDir ...\tests\v5-golden
raw native exit: 0
result: PASS
```

`companion-fixtures.log` stdout：`PASS: 4/4 companion golden fixtures verified successfully`。

| fixture | cases | SHA256（与当前仓独立重算一致） |
|---|---:|---|
| `movement-v1.json` | 8 | `C2D5469E6C532476E5DA48C4478E7C080A401B7F2F41F095B7A8B880AB6C7C13` |
| `shuffle-reversed-v1.json` | 32 | `5A5EA43F774E8341D740E6B5840AAFD4B671AEDFF9830D59DA161D758C14B619` |
| `visibility-v1.json` | 4 | `30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E` |
| `ai-discard-flags-v1.json` | 3 | `448BAB14DEF9809DBDCC4BE1148CD53370FEE1A44541535794AC7DFBE5B5CBE8` |

合计 8+32+4+3 = **47 cases**；两个 run 相同。companion verifier 的 CI 工作区 SHA256
（CRLF 形式）为 `50D3B32ED9F06F99463AD160DD893269F4DA9D789F84C6E71D57977B33FEC51F`，
与本地 LF 文件的 CRLF 归一化结果一致。

**historical git pins：NOT_RUN**。meta 原文：
`git blob pins : NOT_RUN (not requested; historical freeze blobs are not fetched in shallow CI...)`。
本轮没有 fetch 历史冻结 blob，也没有把 4/4 当前字节验证写成历史 pin 已验证。

---

## 6. 证据哈希的关键锚点

完整 manifest 见 §11。关键文件的 SHA256（两个 run 分别列）：

| 文件 | push 40624278181 | PR 40624283836 |
|---|---|---|
| `unified/summary.log` | `628ED16A65ABDBB1DD4B87E3C95285E1AE66600B72B31443F499539329E93E3F` | `4BDFEB5042E02E64C826BDC9C26639E9F111BC22297934439381E95A4839BE16` |
| `unified/sln-build.log` | `C482B0E815201EC74DCD7956A70D538D6113FB27D6DC5D791968E0FFCD21DE81` | `77CA4C4D443DDA7BC252B977E1F3E5455EDAA895A2A958A73974C748264C2B31` |
| `unified/plugin-copy.log` | `8E330FC637687C7CA5F16C5E64C7AC1C0018B100E9985277E02F7E7A3441D900` | `50E1930DB4F3BBD8F93246C1B718A6AA63F4E629DA6AC78E88E3B777A92CE8B6` |
| `unified/companion-fixtures.meta.txt` | `EFD6079281DC2BE4661842B11669E9F787BCBBA4DC0C15889F5F756D68144D79` | `C76869B5E3DC6A5718CA02793F5872006F5AF5F62EFFA0DF0D9403AE70D46480` |
| `table/wrapper-summary.json` | `0DFFA99828292CF24A43BA8D9DE888EC65201FDA10F94F601B4F1BBAFF50B212` | `B5A60B7A254A99233E7AC33331B453C6C5591A9299773C633A87C277F54D5B4F` |
| `table/tests.xml` | `B25B00D2EC98A8CFD73A1E8F75DC51892A24E5A5A55CBFCB7D047866067808EE` | `07E4717499F03B9689FE013350C44416537835BF348423642988DD14A51ECECA` |
| `table/tests-targeted.xml` | `DE3ED63641CFE0B9CB1FADAD8549EA95C6BF6ABB5DE4F365B4BDAA0657BD0EBD` | `41982C7370A920BDDB34DCBDB3FFA2D2E5C67AD427E9AD0808E6F929F67C1903` |
| `table/crash-commit-marker.json` | `5875C28EA62945107791C9BFE9DB211627451CC22A0818455374377B3C808D2B` | `D39A77236807AC607C5C667723D83737D153BE98E454587F36299699DCACEFBA` |
| `table/crash-reader-result.json` | `C879E5C627EBC0D174E3A98F826F38A40007C9DDD3C1BA5214378FE063B121B4` | 同左 |
| `table/restore-reader-result.json` | `B0D075D5336AFC4B5720781FBDEC3E08D9641D8236111460F75BB83D19FCCA86` | 同左 |
| `unified/pg/wrapper-summary.json` | `DAD378D1B844805E742E5B8DFCE84D26927405300456D3B0DA7EF70B8576FF3B` | `E952E24D9C4341C501EC59F91D326118D439F3F478BFE50A985232ADE0EE87D1` |
| `unified/pg/migrate.log` | `8C1DA0B8BF48E3938B19528822871C8259DBF471D5FD20BDC568002C4E3763E6` | 同左 |
| `unified/pg/cleanup.log` | `9F5D3440CD392C1DEE8933DAF8707F028818042F02C66C6D292E223621E6F625` | `4E18D5CB88AF60F2E9D76B5D219C0ACE577EC9487F00128884D4F56108A5F9F9` |

verifier 工作区 SHA256（CRLF 形式）在两个 run-info 中均为
`2CF58DCAF0F2D098F438DBFB8832582F8ADD66A41FB4523118548E1853235B04`。
本地 `tools/verify-v5.ps1` 为 LF，原始哈希是
`FB51764AD14C2F6BDCD1719D76C473FFFD052C2642BE68EE1F18A5FAB8AD6856`；把它按
CI 的 CRLF 形式归一化后正好是 `2CF58D...`。这不是两个不同文件；报告不把 LF 原始哈希
冒充 CI 哈希。

---

## 7. PostgreSQL 16.10 table 存储证据

### 7.1 版本与全量/定向计数

`wrapper-summary.json` 的 cluster stage：`server=16.10`。`pg/postgres.log` 原文：
`starting PostgreSQL 16.10, compiled by Visual C++ build 1944, 64-bit`。

| 项 | 实际值 |
|---|---|
| full XML | `total=37, passed=37, failed=0, errors=0, skipped=0, not-run=0` |
| full XML test 节点 | 37 个，全部 `result="Pass"`，37 个唯一 name |
| targeted XML | `total=13, passed=13, failed=0, errors=0, skipped=0, not-run=0` |
| targeted XML test 节点 | 13 个，全部 `Pass`，全部属于 `PostgresTableStoreTests` |
| wrapper `tests` stage | exit 0，`total=37 passed=37 failed=0 skipped=0 notRun=0` |
| wrapper `tests-targeted` stage | exit 0，`total=13 passed=13 failed=0 skipped=0` |
| table wrapper exit | 0 |

targeted 13 是 `PostgresTableStoreTests` 子集，不追加到 37；full 37 与统一 verifier 的
Infrastructure 37 是同一项目的重复执行，也不追加到 2,294。

### 7.2 原 CLOCK_REGRESSION case 与新增 UTC 两个反证

历史失败 case 的名称是
`Mahjong.Infrastructure.Tests.PostgresR1RegressionTests.NonEmptyCheatLedgerAndFacts_WithCheckpointAndTail_RecoverStrictly`；
其历史失败消息为 `CLOCK_REGRESSION: UTC time moved backwards across boot.`（这是历史事实，
不是本 run 的断言）。本 run 的 `tests.xml` 中：

| 用例 | push 次数/结果/耗时 | PR 次数/结果/耗时 |
|---|---|---|
| `NonEmptyCheatLedgerAndFacts_WithCheckpointAndTail_RecoverStrictly` | 1 / Pass / 1.348s | 1 / Pass / 1.411s |
| `CrossBootRecovery_UsesInjectedUtcTimeline_NotWallClockElapsed` | 1 / Pass / 0.686s | 1 / Pass / 0.818s |
| `CrossBootRecovery_RejectsGenuineUtcRollback_WithInjectedClock` | 1 / Pass / 1.024s | 1 / Pass / 0.575s |

三个 case 在各自 run 的 full XML 中各出现恰好 1 次，且均 `Pass`；没有用 targeted 13 冒充
它们的运行证据，也没有只看 wrapper 摘要。原 case 在本次 XML 中仍以原名出现；本轮没有做
测试源码的“断言是否被放宽”代码级审查，不能仅凭一次 Pass 宣称其断言强度未变。两个新增
反向用例分别记录“注入 UTC 时间线成功恢复”和“真实跨 boot UTC 回退被拒绝”的本次实测结果。

### 7.3 writer 提交后强杀、immediate restart、cold reader、空库 restore

两个 run 均按以下顺序执行，关键值如下（push / PR）：

| 步骤 | push 40624278181 | PR 40624283836 |
|---|---|---|
| writer durable commit marker | `stateVersion=1, lastEventSeq=2, eventCount=2, outbox=c0b108923986..., outboxResultVersion=1` | 同左 |
| committedAtUtcMs | 1793156629981（`03:03:49.981Z`） | 1793156626569（`03:03:46.569Z`） |
| forced kill | pid 7180, exit -1, forced true, killed `03:03:50.4047883Z` | pid 6564, exit -1, forced true, killed `03:03:46.9578280Z` |
| kill marker SHA | 与 marker 文件实算 SHA 一致 | 同左 |
| PostgreSQL immediate stop/start | `received immediate shutdown request` → `automatic recovery in progress` → `redo starts` → `ready` | 同左 |
| postmaster start changed | `03:02:12.259213+00` → `03:03:50.837797+00`；recovery lines=3 | `03:02:13.07524+00` → `03:03:47.392945+00`；recovery lines=3 |
| crash reader（cold） | exit 0；state=1 seq=2 events=2 command=1；receipt=Applied；outbox unpublished；terminal=true；cheat restored；ownerEpoch=2 | 同左 |
| pg_dump/pg_restore 到空库 | stage exit 0；restored events=2；ownerEpoch=3 | 同左 |
| restore reader（cold） | exit 0；state=1 seq=2 events=2 command=1；receipt=Applied；outbox unpublished；terminal=true；cheat restored；ownerEpoch=3 | 同左 |
| cluster cleanup | `ownership-verified cluster removed: ...\p52-pg-cluster-20261028-030150`；`pg-cleanup-stop.log` `server stopped` | `...\p52-pg-cluster-20261028-030154`；`server stopped` |

两个 reader 的 `outboxSha256` 均为 `c0b108923986159032998a13a1946582cbe1d948375ccc3b2abce926b0aa2a55`，
`outboxPresent=true`、`outboxPayloadMatches=true`、`outboxPublished=false`；`stateVersion=1`、
`eventCount=2`、`lastEventSeq=2`、`commandCount=1`、`receiptStatus=Applied`、`terminal=true`、
`cheatStateRestored=true`、`cheatVersion=1`、`recovered=true`。这组值同时覆盖 state、event、
receipt、outbox 的一致性；ownerEpoch 分别为 2/3，证明 restart 与 empty-DB restore 各自重新
取得租约，而不是复用旧 epoch。

`table_store.dump` 为 PostgreSQL custom-format dump（push 44,692 bytes / PR 44,695 bytes；
header 含 `mahjong_p52_test` 与 `16.10`）；`pg_dump.log`、`pg_restore.log` 的
stdout/stderr 为空，但 wrapper stage 记录 exit 0，且 restore reader 是按恢复库实际读出的
JSON。这里不把空 console log 解释为“命令未执行”。

### 7.4 Stage exits 与残留日志

| stage | push exit | PR exit |
|---|---:|---:|
| cluster | 0 | 0 |
| xml-gating-selfcheck | 0 | 0 |
| fresh-build | 0 | 0 |
| tests | 0 | 0 |
| tests-targeted | 0 | 0 |
| crash-writer | **KILLED**（expected；kill record exit -1） | **KILLED** |
| postgres-crash-restart | 0 | 0 |
| crash-reader | 0 | 0 |
| backup-restore | 0 | 0 |
| unified-verification | 0 | 0 |
| result | 0 | 0 |
| table wrapper `exit` | 0 | 0 |

`pg/postgres.log` 在两处 start 后各有一行
`FATAL: role "postgres" does not exist`，发生在测试开始前；没有对应失败 stage，也不是
测试断言失败。它很可能是启动探活/默认 role 尝试的残留日志（本报告没有单独复现该探活），
原样保留、不计为 PASS，也不升级为产品缺陷。`crash-writer` 的 `-1` 是强杀退出，不是
native 成功。

---

## 8. Lobby 真实迁移、事务、回执与清理/环境恢复

Lobby wrapper 的 cluster 为自己的临时 root，ownership=`mahjong-p53-pg`，真实
PostgreSQL 16.10，`postgres-evidence.jsonl` 的 `server.identity` 明确为
`PostgreSQL 16.10 ...`。实际证据：

| 项 | 实际值 |
|---|---|
| 迁移 | `001_lobby.sql` wrapper 执行 2 次，`Migration run 1/2: exit code 0`；`Migration applied 2 time(s) successfully` |
| schema | `lobby tables: 8`；含 `uq_assignment_active_account` 与 `uq_assignment_active_seat` 索引 |
| 第三次迁移 | `postgres-evidence.jsonl` 的 `migration.repeated` 记录 `migrationRuns=3`、`lobbyTables=8`（wrapper 已执行 2 次；第三次由 Lobby 测试路径记录） |
| receipt replay | `firstStatus=201, replayStatus=201, replayed=true, sameBody=true, roomRows=1, conflict=REQUEST_ID_CONFLICT` |
| terminal replay | join/spectate/issue 均 replayed，same body；ticketRows=1，activeAssignments=0，newKeyDenial=ROOM_CLOSED |
| transaction rollback | `receiptRowsAfterFailure=0, roomRowsAfterFailure=0, reusedKeyCommitted=true, assignmentStatusAfterExecuteRollback=Reserved` |
| ticket consumption | `consumptionRows=1`，winner assignment 1 个，loser/third = `TICKET_ALREADY_CONSUMED` |
| cross-room join | 3 rounds，均 `ALREADY_ASSIGNED`，activeAssignments=1 |
| row lock | `observedUngrantedLocks=1, blockedWhileHeld=true, heldMilliseconds=281` |
| room fill | attempts=6，winners=3，roomFullRejections=3，seats=1,2,3，activePlayers=4 |
| 清理 | `rootDeleted=true`，`cleanup.log` 显示 `server stopped` + `Deleted <root>` |
| 环境恢复 | `restoredToPreCall=true`，4 个变量均 `restored=true`，`env-restore.jsonl` 4 条 |
| Lobby wrapper | `failedStages=[]`，`plannedExit=0`，`verifier.ran=true, exitCode=0` |

`evidence-sha256.txt` 的 12/12 文件与本审计实算一致。以上只是 **P5.2/P5.3 存储与大厅
合同证据**，不构成 GLM 在途持久 WSS 宿主（P5.4b）通过，也不构成 G3/G4/G5 通过。

---

## 9. PASS / FAIL / BLOCKED / NOT_RUN 清单

| 项 | 状态 | 依据 |
|---|---|---|
| 两个 run 的 metadata/event/branch/head/conclusion | **PASS** | API run/jobs + checkout 日志 |
| PR merge `303d224` parents 与全树等于 `392b973` | **PASS** | `git cat-file`、两个 tree 相同、`git diff --quiet` exit 0 |
| artifact 完整性与 API digest | **PASS** | 两个 zip 实算 SHA 与 API digest 一致；各 226 files |
| solution build / 0 warnings / 0 errors | **PASS** | `sln-build.log` + `summary.log` exit 0 |
| 12 DLL 闭包（7 Mahjong + protobuf + 4 System，0 Tests） | **PASS** | 插件目录精确枚举；两个 run 相同 |
| 11 个 test 程序集计数 + native exit | **PASS** | 2,294/2,294；0 failed/errors/skipped/not-run；exit 0 |
| 11 个 test 程序集的逐用例 XML 节点 | **BLOCKED** | direct runner 不产生/不上传 XML；不能伪造 |
| 3 个 build-only helper 不计测试数 | **PASS** | summary 明确 `contributes no test cases` |
| 6 项 native exit 传播反证 | **PASS** | 6/6；生产函数 AST 抽取；forced-writer -1 |
| 4 个 companion fixture gate | **PASS** | 4/4，47 cases，exit 0，SHA 与当前仓一致 |
| historical git pins | **NOT_RUN** | shallow CI 未 fetch 历史冻结 blob |
| PG 16.10 full 37/37 | **PASS** | XML 37 节点全 Pass；assembly total/passed=37 |
| PG 16.10 targeted 13/13 | **PASS** | XML 13 节点全 Pass，全部 PostgresTableStoreTests |
| 原 CLOCK_REGRESSION case + 两个 UTC 反证 | **PASS** | 各 1 次、全 Pass；见 §7.2 |
| writer commit → forced kill | **PASS** | marker 先于 kill；kill JSON exit -1/forced true |
| PG immediate restart | **PASS** | immediate shutdown → automatic recovery → ready；start changed |
| cold reader / empty-DB restore reader | **PASS** | 两个 reader JSON；ownerEpoch 2/3；outbox 一致 |
| state/event/receipt/outbox 一致性 | **PASS** | 1/2/2/1；Applied；outbox 同 SHA 未发布；terminal/cheat |
| table cluster 归属清理 | **PASS** | wrapper `ownership-verified cluster removed`；stop log |
| Lobby 迁移/事务/回执/清理/环境恢复 | **PASS** | wrapper 2 次迁移 + 测试路径记录第 3 次；8 表；receipt/rollback/concurrency；rootDeleted/env restore |
| Framework 失败日志 | **N/A（无失败）** | 两个 run 0 failed；没有失败日志可原样保留 |
| 旧 `39970580589` 缺失日志根因 | **UNRESOLVED，未重审** | 本报告不关闭、不称 flaky/已修复 |
| Windows Player | **NOT_RUN** | 本 CI 只覆盖 .NET；未构建/运行 Player |
| 完整游戏 Android / 真机 | **NOT_RUN** | 无 APK/设备/Editor 运行 |
| iOS | **BLOCKED** | 无 macOS/Xcode/iOS 真机 |
| 跨物理网络 / 弱网 / 后台恢复 | **NOT_RUN** | 仅 loopback PG |
| 持久 WSS 宿主（P5.4b） | **未接受** | 存储证据不替代在途宿主 |
| G1 | **APPROVED（既有）** | 本轮不重新批准 |
| G2–G5 | **未批准** | 本轮不改变门禁 |
| 181 classified | **未覆盖** | 本轮不改变 |

---

## 10. 缺陷与证据限制

### 10.1 Material defects

**本轮未发现新的 material code/test defect。** 两个 run 的原始失败面为 0：11 个 runner
0 failed/errors/skipped/not-run；PG full/targeted 0 failed；Lobby wrapper `failedStages=[]`。
没有通过 skip、空测试、旧 DLL、`Total=0`、刷新摘要或放宽 verifier 获得绿色。

### 10.2 BLOCKED-01（evidence，不是代码缺陷）

- **ID**：BLOCKED-01
- **项**：11 个 direct runner 的逐用例 XML test-case 节点
- **实际**：`directRunnerArgs = -noLogo -reporter default`；artifact 中只有 `direct.meta.txt`
  与 `direct-<project>.log`；无 XML/TRX，`test-results/<project>/` 不存在。
- **影响**：无法逐节点证明每个 case 的 Pass/Fail；计数只能由 meta + console summary 证明。
- **期望**：若验收必须有 XML 节点，应在 direct runner 增加支持的 XML/TRX 报告参数并在
  workflow 上传；在此之前该子项标 `BLOCKED/NOT_RUN`，不得写成 XML 已验证。
- **修复要求**：本任务不越界修改 `tools/verify-v5.ps1` 或 workflow；由 caller 决定是否另派
  工具链修改。
- **验收断言**：修改后每个 direct runner 都应产出可解析的 XML/TRX，节点数、
  Pass/Fail/Skip/NotRun 与 meta/console 完全一致，并随 artifact 上传。

### 10.3 其它口径/残留（不是 material defect）

1. `sln-build.log` / `plugin-copy.log` 本身不含 raw exit code；exit 0 来自
   `summary.log`。日志同时有 `Build succeeded` 与 0/0，未见失败证据。
2. `pg_dump.log` / `pg_restore.log` 的 stdout/stderr 为空；成功依据是 wrapper stage
   exit 0、44,692/44,695-byte custom dump、restore reader JSON，而不是这两份空日志。
3. `postgres.log` 的两行 `FATAL: role "postgres" does not exist` 很可能是 start 后探活/
   默认 role 残留，不对应失败 stage；原样保留，不计 PASS/缺陷。
4. `crash-writer` stage exit 为 `KILLED`（kill record -1），不是 native 0；这是强杀测试的
   预期语义。
5. 六项 native-exit 不含 exit-6；若另有 exit-6 验收要求，应标 `NOT_RUN`。
6. `summary.log` 的 14 行是 11 test + 3 helper；3 个 helper 的构建 PASS 不等于测试通过。
7. verifier 的 `pluginCopy.requiredAssemblies` 只列 8 个必需项，但本次实际目录为 12 个
   DLL；本审计独立核对了 12 个精确集合。该差异不影响本 run 的 12-DLL 结论，但属于未来
   漂移检测的硬化点，不是本 run 的失败。

---

## 11. Manifest 与可复核路径

证据根：

```text
D:\majiang-wt\pr20-ci-392b973-ds-20261028\build\ci-audit-392b973-04d71aeecccb4ccfab094e87770ecfc8
```

完整 manifest：

```text
manifest.csv
SHA256 E8939D8163A1174142EC1ECCBB97CB5ACE98E41D3B12FC055FCE7FD219D472DD
494 files, 30,551,823 bytes
```

`manifest-meta.json`：

```text
SHA256 BDB748B60D0B966963F2CC8A2A19D1FC7C7C0CCDCB41B9EEB249540B6C15E183
```

覆盖范围：API run/jobs/artifacts JSON、raw job log zip 与解压文件、`gh run view` 串联日志、
两个 artifact zip 与全部 226×2 个解压文件、`audit.ps1`、`make-manifest.ps1`、
`audit-result.json`、`audit-console.txt`。**tmp/compiler cache 未排除**：包括
`tmp\VBCSCompiler\...`，用 `\\?\` 长路径前缀实算；`manifest.csv` 与
`manifest-meta.json` 本身不在 manifest 内。原始日志/artifact 未被覆盖或删改。

审计脚本/结果：

```text
audit.ps1            SHA256 7138C3B93B4A5293542820F653EE6BECE4F089B1C085FCABEAA82DDE12F63AD3
audit-result.json    SHA256 EAF5B26041243E30F792639794C867539338DB8424EC1BCDD56E37D33E4B7146
audit-console.txt    SHA256 48980D0586E2FE09108A5203CBFF5D2E496213B1D77E80780B4E7CD8D7BC6107
audit native exit    0
checks               649
failures             0
limitations          2（同一 direct-XML 事实在两个 run 各一次）
```

---

## 12. 最终边界

- 本报告只提交一个 tracked 文件：本文件。没有修改生产代码、测试、统一 verifier、workflow、
  `Mahjong.sln`、Unity 资源、环境/机器信任/scheduler；没有 push/PR/MR/merge/deploy/
  `complete.flag`。
- 本报告不把 `392b973` 的组件/CI 通过扩大为 G2–G5、Windows Player、完整游戏 Android/
  真机、iOS、跨物理网络或持久 WSS 宿主通过。
- 精简 Wire Android 工具链组件仍按既有接受记录处理：**不**用历史 Android 总括 FAIL 否定
  该组件验收，也**不**把该组件通过扩大为完整游戏 Android/真机通过。
- 旧 `39970580589` 的 framework log 缺失根因仍 **UNRESOLVED**；本轮没有重审该问题，也没有
  用这两个绿 run 宣称关闭。
- 11 个 direct runner 的 XML test-case 节点仍是 **BLOCKED**；这是本报告唯一未满足的
  验收子项，不能写成 XML 已验证。
