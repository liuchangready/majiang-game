# PR20 冻结头 `bae6b8b0` 两次既有 CI 原生证据独立审计

日期：2026-11-01（Asia/Shanghai）。
审计工作树：`E:/majiang-review/pr20-bae6-ci-audit-ds-20261101-1013`，分支
`wt/v5-pr20-bae6-ci-audit-ds-20261101-1013`，审计代码基线及唯一报告提交的父提交为 accepted
`origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；接收方报告提交为
`19cd9bbb886ec4f9ff63d04e813cd155d4dbf262`。
冻结审计目标：`bae6b8b0c98c9ed80256373060a9e754d6fc4296`（PR20 head，只读）。

**结论边界：本报告只审计该冻结头的两次既有 CI 的 .NET/PostgreSQL 原生证据。不是 C03 统一候选
复验，不是 C04/C05 功能验收，不是 G2 验收/批准，不是 PR 合并审查，不改实现、门禁、workflow、
PR、调度或账本。**

## 0. 结论摘要

- **本审计范围内执行的 14 个 direct 测试工程：`2813/2813` Pass，`0` failed/errors/skipped/not-run。**
  两次 run 的逐例“名称 + result”multiset 相同（SHA256
  `6C98589D8D9848D37C9CB4D00537B29AAD8AC50EFFDFCF43332DEEC9C01870FB`），但 test id 交集为
  `0`，是两次独立执行，不是复制同一份 XML。
- **不是 `dotnet test` 证据。** 两次 run 的 artifact 均为 `runner mode: direct`，没有
  `dotnetTest-xml/` 或 `dotnet-test-results.xml`；14 个 meta 的命令均为
  `dotnet.exe <Tests.dll> ... -result-xml ...`。`dotnetTest` 模式本轮 **NOT_RUN**。任何
  “远端 dotnetTest 全绿”表述都不成立；direct 结果也不能改标签冒充 dotnetTest。
- **构建面 PASS（实际执行范围）**：SDK `8.0.426`；每 run 15 份构建日志均
  `Build succeeded / 0 Warning(s) / 0 Error(s)`；19 个插件文件（12 个 DLL：7 个自有 +
  5 个包；7 个 PDB），149 个 provenance 输入全部可解析，7/7 自有 DLL 内嵌对应 run head。
- **真实 PostgreSQL 面 PASS（实际执行范围）**：PostgreSQL `16.10`，表集群
  `127.0.0.1:55592`，lobby 集群 `127.0.0.1:55593`；42 例全量/13 例 targeted 子集均
  `42/42`、`13/13`；writer 强杀后重启恢复 3 行，reader 读到 `Applied`/`terminal=true`/outbox；
  dump→空库 restore 后 `eventCount=2`、`recoveryOwnerEpoch=3`。这是单条 durable-table 状态的
  故障恢复证据，**不是自然整场终局/结算终局**。
- **FAIL：0**（就本审计实际执行的 direct、构建、PG 阶段而言）。
  **BLOCKED：历史 `39970580589` 的 Client 失败仍缺 framework 原日志，未关闭。**
  **NOT_RUN：dotnetTest、19 例 direct 门禁自检、dotnetTest 门禁自检、git pins、Unity/Player/
  Android/adb/设备、自然整场终局；另当前 PR 新头不在本冻结范围。**
- 修复前不要宣称：远端 dotnetTest 通过、table XML 门禁具备完整结构校验、19 例门禁自检已由 CI
  验证、历史 git pins 已核对、timeout 合并日志完整、`39970580589` 已修复。
- 审计期间 PR20 从 `bae6b8b0` 前进到 `4b5012f0abedde309a14b8a7d8201eca53d4fc31`（2 个
  doc-only commit）。本报告仍只完成冻结 `bae6` 审计，不下载/不判新头，不改范围。

## 1. 冻结 source 与两次 run 的实际身份

| 对象 | 实际值 |
|---|---|
| accepted `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（未移动） |
| 冻结 PR head | `bae6b8b0c98c9ed80256373060a9e754d6fc4296` |
| `bae6^{tree}` | `6da5de0cb5ebd6b8286930c7f577cf863a80a875` |
| PR run API `head_sha` | `bae6b8b0c98c9ed80256373060a9e754d6fc4296` |
| PR run 实际 checkout | `1b8c01b3fdbd1c7547bd4ec4778e3fa7f74b10c9`（GitHub 合成 merge） |
| PR merge parents | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` + `bae6b8b0...` |
| PR merge tree | `6da5de0cb5ebd6b8286930c7f577cf863a80a875`，与 `bae6^{tree}` **相同** |
| push run 实际 checkout | `bae6b8b0c98c9ed80256373060a9e754d6fc4296`，tree 同为 `6da5de0...` |
| `bae6` 相对 parent | 仅修改 `doc/v5-重构/V5-PR20-1EB029D-CI-EVIDENCE-AUDIT-20261101.md`（+16/−4） |

**必须分开的身份。** PR 事件的测试对象是合成 merge commit `1b8c01b`，不是字面 `bae6`；
同时其 tree 与 `bae6` 完全相同。push run 才是字面 `bae6`。因此：

- 语义上，两次 run 测的源码内容都是冻结 `bae6` tree；
- provenance 标签上，PR run 的受测 commit 必须写 `1b8c01b`，不能写成“字面 checkout bae6”；
- 若验收要求“字面 head checkout”，只能引用 push run，或另加 tree 相等证明。

**审计期间 PR 移动。** 2026-11-01T02:39:07Z 重新读取远端：

- `refs/pull/20/head = 4b5012f0abedde309a14b8a7d8201eca53d4fc31`；
- `refs/pull/20/merge = 8aba45d917cc1b3717f6c6ce9af2d0eb78100a85`；
- 当前头相对 `bae6` ahead 2 commits：`1df3b21`（C04 census 准备）、`4b5012f`（C04 准备
  acceptance），共新增 2 个 doc 文件，无代码/workflow 变化；
- 本审计未下载、未运行、未判断 `4b5012f`/`8aba` 的 CI，不将冻结 `bae6` 的绿色推广到新头。

## 2. 两次 run、job 与每 step 元数据

| 项 | PR run | push run |
|---|---|---|
| run id | [41261636998](https://github.com/liuchangready/majiang-game/actions/runs/41261636998) | [41261632618](https://github.com/liuchangready/majiang-game/actions/runs/41261632618) |
| event | `pull_request` | `push` |
| job | `124715927068` | `124715914994` |
| workflow | `v5-verify` / `.github/workflows/v5-verify.yml` | 同左 |
| API `head_sha` | `bae6b8b0...` | `bae6b8b0...` |
| `run_attempt` | 1 | 1 |
| status/conclusion | completed/success | completed/success |
| created（UTC） | 2026-11-01T01:34:21Z | 2026-11-01T01:34:18Z |
| updated（UTC） | 2026-11-01T01:55:32Z | 2026-11-01T01:55:50Z |
| runner | GitHub Actions 1000000363 / windows-latest | GitHub Actions 1000000362 / windows-latest |

两次 job 的 step 均为 `success`：

| # | step | PR | push |
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

编号从 6 跳到 11 是 GitHub Actions step 编号现象；workflow 声明 5 个业务 step，日志无缺失步骤。
不是漏执行。

checkout 原生日志：

- PR：`fetch ... +1b8c01b...:refs/remotes/pull/20/merge`；
  `HEAD is now at 1b8c01b Merge bae6b8b0... into 8fd7c6cb...`；
  `git log -1 --format=%H` 输出 `1b8c01b3fdbd1c7547bd4ec4778e3fa7f74b10c9`。
- push：`fetch ... +bae6b8b0...:refs/remotes/origin/feat/v5-post-g1-batch`；
  `git log -1 --format=%H` 输出 `bae6b8b0c98c9ed80256373060a9e754d6fc4296`。

## 3. artifact/job log 下载与完整性

artifact 均为二进制 ZIP，使用 `curl.exe -sS -L` 直接落盘，未用 PowerShell 文本重定向。

| run | artifact | id | server bytes | 下载 ZIP SHA256 / server digest | 解压 |
|---|---|---:|---:|---|---|
| PR | `v5-verify-logs-41261636998-1` | 13065987167 | 4,503,122 | `45C16C2652759CC94670D86C06A936094CB8211A63FB702768ADD25603065056`（一致） | 270 files / 13,188,093 bytes |
| push | `v5-verify-logs-41261632618-1` | 13065788692 | 4,504,328 | `36500758547855EC626778EEBB108A37516CFED2AFE5E72FDE87FFD33B6D9157`（一致） | 270 files / 13,189,166 bytes |

- GitHub artifact API digest：
  - PR `sha256:45c16c2652759cc94670d86c06a936094cb8211a63fb702768add25603065056`
  - push `sha256:36500758547855ec626778eebb108a37516cfed2afe5e72fde87ffd33b6d9157`
- ZIP 条目集合与解压文件集合 **270/270 完全一致**：无缺、无多、无重复条目。
- table 自带的 `manifest.sha256.json`：每 run **152/152** 条目长度+SHA256 全部一致；唯一
  “额外”文件是 manifest 自身。22 个 `tmp/VBCSCompiler` 长路径文件用 `\\?\` 长路径 IO 复核，
  全部通过；没有用普通 `Test-Path` 的假缺失掩盖。
- job 原始日志：
  - PR `25,163` bytes，SHA256 `E1286A4836198D77E1E6C50AFABE4E86D5D8D314DECB969489583C5C0BD8EFEF`
  - push `24,136` bytes，SHA256 `353D23BE217BDF3064C8831E826CBE9CC06118E252BEDD31458574FFA67569F1`
- artifact 上传日志记录 `270 files uploaded`，与 ZIP/解压计数一致。
- 原始证据目录：
  `E:/majiang-review/pr20-bae6-ci-audit-ds-20261101-1013/build/bac82f092fb941e3ad9dd321ff28ce7c/evidence`

### 3.1 全文件 manifest

`MANIFEST-SOURCE-BAE6.csv`：`git ls-tree -r bae6` 的 **2431** 个文件，388,606,879 bytes。
`MANIFEST-EVIDENCE.csv`：**548** 个证据文件（2×270 解压文件、4 个下载 ZIP、2 个 job log、
2 个 artifact 元数据），35,442,257 bytes。
`MANIFEST-ALL-FILES.csv`：排除 manifests/tmp 后 **3003** 个文件。
`MANIFEST-SUMMARY.json` SHA256：`95C3D66A23AA36EA89E11DE0AAFDC5067E85E80A085C7E1E549C5E5FA2021873`。

关键 manifest 哈希：

| 文件 | SHA256 |
|---|---|
| `MANIFEST-SOURCE-BAE6.csv` | `298991001091A753B6B6312C48090A1FDCB1937D44182062F309B838D914340C` |
| `MANIFEST-EVIDENCE.csv` | `378409223C2FC6CB7B82084C56CF0B10FB5E009EAC096CB5651E0EF48DC214DB` |
| `MANIFEST-ALL-FILES.csv` | `E9864910CF498217F87471D78F8448CC90D77B71AFBADE337DBADCAD5C87D09F` |
| `bae6` source zip | `E960B6D3A328028CA8827888BD4A50BAED0AA116413A83F4F904E0998745C65C` |
| PR summary.log | `1EB84FD5AE88578499BF98980652BB54E9D02EC6B12875906E0716250FC703B0` |
| push summary.log | `CDAAAECCBD81983B8CC0BF19FB7A5C4FBDD0EB2EE48D83CACD213025518DEAE6` |

## 4. 逐例原生结果独立重算

方法：逐工程解析 `direct-xml/<project>/direct-results.xml`，不读 summary 的 total 作为输入：

1. 统计 assembly 下全部 `<test>` 节点；
2. 按 `result` 统计 Pass/Fail/Skip/NotRun/未知；
3. 校验 assembly 的 `total/passed/failed/skipped/not-run/errors`；
4. 校验每个 `<collection>` 的节点数与属性汇总，并求 collection 总和；
5. 统计 test `id` 与 display name 的唯一性；
6. 与 `direct.meta.txt` 的 `total/exit/result xml sha256`、`direct-*.log` 的 console summary
   及 `summary.log` 对照；
7. 用 `project|name|result` 排序集合计算跨 run multiset SHA256。

### 4.1 每工程计数（两 run 均相同）

| 工程 | Total | Pass | Failed | Skipped | NotRun | Collections | Unique IDs |
|---|---:|---:|---:|---:|---:|---:|---:|
| Mahjong.Ai.Tests | 444 | 444 | 0 | 0 | 0 | 13 | 444 |
| Mahjong.Architecture.Tests | 132 | 132 | 0 | 0 | 0 | 1 | 132 |
| Mahjong.Client.Tests | 182 | 182 | 0 | 0 | 0 | 10 | 182 |
| Mahjong.Client.Wss.Tests | 82 | 82 | 0 | 0 | 0 | 6 | 82 |
| Mahjong.Contracts.Tests | 152 | 152 | 0 | 0 | 0 | 8 | 152 |
| Mahjong.Domain.Tests | 529 | 529 | 0 | 0 | 0 | 34 | 529 |
| Mahjong.Infrastructure.Tests | 42 | 42 | 0 | 0 | 0 | 1 | 42 |
| Mahjong.LobbyService.Tests | 89 | 89 | 0 | 0 | 0 | 11 | 89 |
| Mahjong.LocalHost.Tests | 127 | 127 | 0 | 0 | 0 | 18 | 127 |
| Mahjong.P5.4b.B1.HttpsAdmission.Tests | 54 | 54 | 0 | 0 | 0 | 4 | 54 |
| Mahjong.TableRuntime.Tests | 483 | 483 | 0 | 0 | 0 | 57 | 483 |
| Mahjong.TableService.DurableHost.Tests | 59 | 59 | 0 | 0 | 0 | 2 | 59 |
| Mahjong.TableService.Tests | 203 | 203 | 0 | 0 | 0 | 19 | 203 |
| Mahjong.Unity.TableModel.Tests | 235 | 235 | 0 | 0 | 0 | 12 | 235 |
| **合计** | **2813** | **2813** | **0** | **0** | **0** | **196** | **2813** |

独立断言（每 run 均成立）：

- assembly `total` = test 节点数 = **2813**；196 个 collection 的节点总和也 = 2813；
- assembly `passed` = Pass 节点 = 2813；failed/errors/skipped/not-run 全 0；
- 14/14 工程 `total > 0`，无 `Total=0`、无空 pass、无永远 skip、无未知 `result`；
- 2813 个 test `id` **全部唯一**；test id 唯一数 = 2813；
- 14/14 XML `assembly@name` 完整路径 = `direct.meta.txt` 实际执行的 DLL 路径；
- 14/14 `exit code: 0`，14/14 `result xml gate: PASS`，14/14 XML SHA 与 meta 记录一致；
- 14/14 meta 记录 TestResults 在 invocation 前为 `cleared - not present before the run`，
  没有复用旧 result XML；
- 14/14 `test-framework=xUnit.net v3 4.0.2+0c7aa4991f`，`target-framework=.NETCoreApp,Version=v8.0`。

**display name 的小边界：** `Mahjong.Architecture.Tests` 有 1 对相同 display name
`UnsupportedDeclarationFormsFailClosed(fragment: "...")`（xUnit 截断显示），但两条的 test id
不同、method/source line 相同，分别计为 2 个 Pass 节点；ID 唯一性不受影响。若把 display name
当唯一键会误去重，因此本报告以 test id 校验唯一性，以 name+result 做跨 run multiset。

### 4.2 两 run 独立性与 multiset

- PR 测试窗口：`2026-11-01T01:37:46.175Z` → `01:54:45.017Z`，约 1018.842s。
- push 测试窗口：`2026-11-01T01:37:50.366Z` → `01:54:55.968Z`，约 1025.602s。
- 两 run 的 2813 个 test id 交集 **0**；窗口不同、ID 不同，是两次独立执行。
- 两 run 的 `project|name|result` 排序集合 SHA256 同为
  `6C98589D8D9848D37C9CB4D00537B29AAD8AC50EFFDFCF43332DEEC9C01870FB`；
  project 集、total、passed、逐工程 canonical SHA 全部相同。

### 4.3 table 专用阶段不重复累加

| 阶段 | XML | Total | Pass | Failed | Skipped | NotRun | Collections |
|---|---|---:|---:|---:|---:|---:|---:|
| table `tests` | `tests.xml` | 42 | 42 | 0 | 0 | 0 | 1 |
| table `tests-targeted` | `tests-targeted.xml` | 13 | 13 | 0 | 0 | 0 | 1 |

- 13 个 targeted 用例名全部包含在 42 个用例中；42 个用例名与 unified 的
  `Mahjong.Infrastructure.Tests` 42 个用例名一致。
- 因此**唯一测试分母是 2813**。本 job 的测试执行次数为 `2813 + 42 + 13 = 2868`，但
  42/13 是 Infrastructure 的重复/子集执行，不能加到 2813 冒充额外覆盖。
- `Mahjong.Unity.TableModel.Tests` 的 235 已包含在 2813 内；去掉它才是 2578，不能再把 235
  作为额外集合相加。
- 仓库 `dotnet/tests/` 下 21 个 `.csproj`：14 个 `*.Tests` 全部被发现执行；另 7 个
  crash-worker/probe/helper 只构建、不计测试数。

### 4.4 direct vs dotnetTest 的实际执行

- 两次 run 的 `run-info.txt`、`summary.log` 均写 `runner mode: direct`；
- 14 个 `direct.meta.txt` 的 `runner used` 均 `in-process (direct)`，命令均
  `dotnet.exe <Tests.dll> -noLogo -reporter default -result-xml ...`；
- artifact 中 `dotnetTest-xml/` 目录不存在，`dotnet-test-results.xml` 文件数为 **0**；
- workflow 调用 `verify-v5-table-postgres.ps1` 时未传 `-RunnerMode dotnetTest`；该脚本
  `$RunnerMode` 默认 `direct`（第 44–61 行），并原样传给 `verify-v5-postgres.ps1`。
- 结论：**direct 14/14、2813/2813 PASS；dotnetTest NOT_RUN。** 不把 direct 结果重标为
  dotnetTest，也不引用旧头的 dotnetTest 成功替代本头。

## 5. 构建、warning/error、插件闭包与 provenance

### 5.1 构建

- 实际 SDK：`8.0.426`（`sdk-diagnostic.log`、`run-info`/`summary` 一致）；不是旧头审计时的
  `8.0.427`。
- 每 run 检查 15 份构建日志（table 2、unified 11 个按工程构建、`sln-build.log`、
  `plugin-copy.log`）：**15/15 `Build succeeded`，15/15 `0 Warning(s)`，15/15 `0 Error(s)`**。
- `sln-build.log` 与 `plugin-copy.log` 均 0 warning/0 error；table `build-tests`/`build-worker`
  也均 0/0。
- `plugin-copy.log` 记录 `native exit code: 0`、`copied 19 files`、`inputs: 149 files`、
  `outputs: 19 files`、`Unity plugins ready`。

### 5.2 19 个插件 / 12 个 DLL / 149 inputs

实际拆解（不盲抄历史）：

- **19 个插件文件** = 12 个 DLL + 7 个 PDB；
- 12 个 DLL = **7 个自有**（Domain、Contracts、Contracts.Wire、TableRuntime、Client、
  LocalHost、Ai）+ **5 个包**（Google.Protobuf 3.28.3、System.Buffers 4.4.0、System.Memory
  4.5.3、System.Numerics.Vectors 4.4.0、System.Runtime.CompilerServices.Unsafe 4.5.2）；
- summary 写 “12 assemblies” 指 12 个 DLL，不是 19 个文件；两者不矛盾。

provenance 独立复核：

- `shared-plugin-provenance.json` 有 **149** 个 inputs、**19** 个 outputs、**5** 个 packages、
  **7** 个 requiredOwnAssemblies；
- 149/149 输入均在冻结树中解析：**144 个 raw-byte 直接匹配**，**5 个仅 CRLF 归一后匹配**，
  unexplained=0；
- 19/19 output 文件长度+SHA256 匹配；无缺、无多；7/7 自有 DLL 存在；5/5 package 输出哈希匹配；
- 2 个工具脚本（`build-shared.ps1`、`shared-plugin-provenance-lib.ps1`）的声明哈希与冻结树
  在 CRLF 归一后一致；raw 比对会差在换行；
- 5 个 CRLF 文件是：`dotnet/Directory.Build.props`、`dotnet/src/Mahjong.Contracts.Wire/mahjong.proto`、
  `global.json`、`tools/unity/build-shared.ps1`、`tools/unity/shared-plugin-provenance-lib.ps1`。
  差异是 CI checkout 的 CRLF 与 `git archive` LF 的字节差，不是源码内容差；
- 7/7 自有 DLL 的 `ProductVersion` 内嵌当前 run head：PR run 为 `...+1b8c01b...`，push run 为
  `...+bae6b8b0...`。因此两 run 的 7 个自有 DLL/PDB 字节**不相同是预期的**（嵌入 commit 不同），
  每个 artifact 各自与自己的 run 身份自洽；不宣称跨 run 二进制相同。

### 5.3 命令/脚本 SHA 绑定

- `run-info.txt` 记录 `verify-v5.ps1` SHA256
  `76B58C2FDDAF33EFFFB7269739A081136FA6F2914B6B9F132772EB31BD63F2BD`；冻结树该脚本按
  CI CRLF 归一后完全一致（raw LF 为 `41510892...`）。
- table wrapper `tools/ci/verify-v5-table-postgres.ps1` raw SHA256
  `85B67C3836CF82ACA84565AC92119F932DB9D17C79A471D8C0B46A031B43B965`，CRLF
  `40CE67A7...`；F2 的 probe 直接加载该冻结脚本第 725–753 行函数。
- `tools/verify-v5.ps1` 的 direct 命令、`direct.meta.txt` 的 XML SHA、14/14 XML 的实际 SHA
  三者相互绑定；没有只凭 console summary 判绿。

## 6. 真实 PostgreSQL、crash/restart、backup/restore

### 6.1 实际版本、端口、启动

- `postgres.log`：`starting PostgreSQL 16.10, compiled by Visual C++ build 1944, 64-bit`；
  `listening on IPv4 address "127.0.0.1", port 55592`；`database system is ready to accept
  connections`。
- table 集群：`127.0.0.1:55592`，database `mahjong_p52_test`/`mahjong_p52_restore`。
- lobby 集群：`127.0.0.1:55593`，database `mahjong_lobby_test`，独立 temp data directory，
  `owner=mahjong-p53-pg`；`postgres-evidence.jsonl` 记录
  `PostgreSQL 16.10 ... 127.0.0.1:55593`。
- 两个端口都是显式 loopback，不是公网监听；集群 root 在 runner 的 worktree/artifacts 或专用
  TEMP 下，cleanup 后 `rootDeleted=true`。

### 6.2 阶段与原始计数

| 阶段 | 实际结果 | 原始证据 |
|---|---|---|
| table `tests` | 42/42，0 fail/skip/notRun，native exit 0 | `tests.xml`、`tests.log` |
| table `tests-targeted` | 13/13，0 fail/skip/notRun，native exit 0 | `tests-targeted.xml`、`tests-targeted.log` |
| crash writer | committed state=1 seq=2 后被强杀，`exitCode=-1`，`forced=true` | `crash-commit-marker.json`、`crash-writer.log.kill.json` |
| crash restart | immediate shutdown 后重启，3 行恢复，`redo done`，ready | `postgres.log`、`postgres-restart-recovery-lines.log` |
| crash reader | `receiptStatus=Applied`，`terminal=true`，`stateVersion=1`，`eventCount=2`，outbox 存在且 payload 匹配 | `crash-reader-result.json`、`crash-reader.log` |
| backup/restore | dump→空库 restore，restored events=2，reader `recovered=true`，`recoveryOwnerEpoch=3` | `table_store.dump`、`restore-reader-result.json` |

关键原始值：

- writer marker：`stateVersion=1`、`lastEventSeq=2`、`eventCount=2`、`requestId=p52-crash-request`、
  `receipt/outbox` 字段齐全；marker SHA256 `D1BC3963FF129FB0316B8913CA46AE3BF7B23AB78A60112D8F98DB17CAAEDFF8`。
- writer kill：pid `4284`（push；PR 为 `8636`），`killedUtc=2026-11-01T01:36:13.858Z`，
  `exitCode=-1`，`forced=true`。
- crash reader：`outboxSha256=c0b108923986159032998a13a1946582cbe1d948375ccc3b2abce926b0aa2a55`，
  `outboxPresent=true`、`outboxPayloadMatches=true`、`cheatStateRestored=true`、
  `recoveryOwnerEpoch=2`、`recovered=true`、`terminal=true`。
- restore reader：同 state/receipt/outbox，`recoveryOwnerEpoch=3`、`recovered=true`。
- dump 文件 `table_store.dump` 44,703 bytes（push run）。

`postgres.log` 中还有 `FATAL: role "postgres" does not exist`、`canceling statement ...`、
`permission denied for schema table_store` 等行；这些是负例/探针产生的预期错误文本，不等于
测试失败。对应 wrapper、42/13 XML 和 native exit 均为 PASS/0；报告不把这些预期错误改写成
“零错误服务器日志”。

### 6.3 它不证明什么

- `commandCount=1`、`eventCount=2`、单条 receipt/outbox、`terminal=true` 是**一条 durable
  table 状态**的故障恢复事实，不是四席自然整场的结算/终局。
- 42 例 Infrastructure 全量与 13 例 targeted 是 PG 存储/故障用例；不能把它们当“自然整场终局”
  证据。
- 本审计没有运行 Unity/Player，也没有运行 `GAME` 采集；不把 memory、普通 PG 用例或本 CI 的
  durable 状态包装成 G2 的整场终局验收。

## 7. 门禁、selfcheck、companion、git pins

### 7.1 实际 workflow 与 gate

- workflow 在 Windows PowerShell 5.1 下执行：
  1) `tools/ci/test-v5-native-exit.ps1`；
  2) `tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -RunUnifiedVerification ...`；
  3) 上传 `artifacts/verify-v5/**`、`artifacts/native-exit-*/**`。
- table wrapper 的 XML 自检只有 **7 个负例**：zero-tests、failed、errors、skipped、not-run、
  passed-mismatch、missing-not-run；它只读取 assembly 六个属性。
- companion gate：4/4 fixture（movement 8、shuffle-reversed 32、visibility 4、
  ai-discard-flags 3），native exit 0；`git pins` 明确
  `NOT_RUN (historical freeze blobs not requested/fetched; current bytes and embedded provenance
  verified offline)`。
- native-exit 自检 6/6 通过：exit 0/1/2/17、timeout throw、forced-writer nonzero；其中
  `timeout.log` 合并文件未落盘，只有 15-byte stdout/stderr 和 `results.json` 的
  `Pass=true`。

### 7.2 已确认的门禁缺陷

见 §9 的 D-01/D-02/D-03/D-04。核心事实：

- table XML 门禁只做 assembly 汇总，不检查 test 节点数、collection 汇总、重复 id 或 assembly
  身份。用冻结函数对“assembly-only 42/42”和“重复 test id”两个负例实测均返回 green。
- 19 例 direct 门禁自检和 sibling dotnetTest 门禁自检脚本存在，但 workflow/CI 没有任何调用；
  CI 只跑了 table 内 7 例。
- `git pins` 是显式 NOT_RUN，不是已验证。
- 本轮实际 42/13 XML 节点、唯一 id、collection 汇总均正确，且 unified 严格门禁对同一
  Infrastructure 42 例 PASS；因此 D-01 是门禁深度 MINOR，**不是本轮 42/2813 的假绿**。

## 8. PASS / FAIL / BLOCKED / NOT_RUN

| 项 | 严格分类 | 依据 |
|---|---|---|
| 两次 run 元数据/step/attempt/success | **PASS** | run/job API、日志、step 全 success |
| artifact ZIP server digest/本地 SHA/270 文件 | **PASS** | §3 |
| 两次 run 逐例 direct 结果 2813/2813 | **PASS** | §4；0 fail/error/skip/notrun |
| 跨 run multiset 一致、ID 独立 | **PASS** | §4.2；ID 交集 0 |
| table 42/42 与 targeted 13/13 | **PASS**（子集执行） | §4.3；不加入 2813 |
| 构建 0 warning/0 error、SDK 8.0.426 | **PASS** | §5.1 |
| 19 文件/12 DLL/149 inputs/7 自有 DLL | **PASS** | §5.2；7/7 内嵌 run head |
| PG 16.10 启动、crash/restart、backup/restore | **PASS**（实际执行范围） | §6 |
| 本轮 direct 业务测试失败 | **FAIL = 0** | 未发现 |
| `dotnetTest` 模式 | **NOT_RUN** | 无 dotnetTest XML，脚本默认 direct |
| 19 例 direct 门禁自检 | **NOT_RUN** | CI 无调用 |
| dotnetTest 门禁自检 | **NOT_RUN** | CI 无调用 |
| `git pins` 历史 blob | **NOT_RUN** | summary 自报 |
| Unity/Player/Android/adb/设备 | **NOT_RUN** | 明确不在范围 |
| 自然整场终局/结算终局 | **NOT_RUN** | 只有单条 durable table 状态 |
| 当前 PR 新头 `4b5012f` 的 CI | **NOT_RUN** | 冻结范围外，未下载 |
| 历史 `39970580589` 根因 | **BLOCKED** | 缺 framework 原日志，见 §10 |

## 9. 已知缺陷与限制清单

### D-01（MINOR，门禁深度）table XML 门禁只看 assembly 汇总

- 位置：`tools/ci/verify-v5-table-postgres.ps1:725-753`（`Get-XmlCounts`/`Test-XmlCountsPass`），
  自检 `755-784`。
- 复现：`build/<GUID>/audit-scripts/probe-table-xml-gate.ps1` 从冻结脚本 AST 加载上述两个函数，
  写入 `assembly-only`（total/passed=42，0 个 test 节点）和 `duplicate-id`（2 个
  `<test id="same">`）两个临时 XML。
- 实际：两例 `table_gate_green=True`。
- 期望：两例均 false；应拒绝“仅汇总无节点”和“重复 test id”。
- 证据：`audit-output2/gate-probe/table-gate-probe.json` SHA256
  `AEA6068A83D0C3D4D19839CCA3695FA43D28E6E7BC449B0CE2DF8624A18AE00D`；probe 脚本 SHA256
  `B5A263A07501DCE4C742511967284AEC20420C1E54DFAE6409DBA122A814F217`。
- 修复要求：table 门禁复用严格结构校验（节点数、collection 汇总、唯一 id、assembly 身份），
  或把上述两例加入负例矩阵；不要删除 42/13 现有校验。
- 验收断言：两个负例实际返回 false；实际 `tests.xml`/`tests-targeted.xml` 仍 42/13 true；
  本轮不能把 table 42/13 只归因于 `Test-XmlCountsPass`。

### D-02（NOT_RUN，覆盖）19 例 direct 门禁自检与 dotnetTest 门禁自检未在 CI 执行

- 位置：`.github/workflows/v5-verify.yml:54-61`；`tools/ci/test-v5-direct-results.ps1`（19 个
  `Invoke-EvidenceCase` 调用）；`tools/ci/test-v5-dotnettest-results.ps1`。
- 复现：`git grep -e test-v5-direct-results.ps1 -e test-v5-dotnettest-results.ps1 bae6 -- .github tools`
  → exit 1，无引用；job step 列表也没有这两个脚本。
- 实际：CI 只执行 table 内 7 例 selfcheck；19 例 direct 矩阵和 dotnetTest 门禁自检均未执行。
- 期望：若声明门禁矩阵已由 CI 验证，应在 CI 显式执行；否则只能声明 7 例。
- 证据：两脚本 SHA256 `4CACA2108751C8779CAC405B12881D495553294C6E0CF78DBA7EC0405F0D75A1`、
  `A3402DCD81FC63B331557545F0614EE8BFE9827B19D22566670189A84AFBF17E`；job 日志无调用行。
- 修复要求：增加 CI step，或修正验收表述；“19 例已由 CI 验证”当前不成立。
- 验收断言：CI 日志出现两脚本实际调用、退出 0、19/19 与 dotnetTest 矩阵分别有原始结果；
  在修复前不得引用这两组矩阵作为本头 CI 证据。

### D-03（NOT_RUN，子门禁）git pins 历史冻结 blob 未校验

- 位置：`tools/verify-v5.ps1` 的 summary 输出；两 run `summary.log:17`。
- 复现：直接读取 `summary.log` 第 17 行。
- 实际：`git pins : NOT_RUN (historical freeze blobs not requested/fetched; current bytes and
  embedded provenance verified offline)`。
- 期望：若宣称历史 pin，必须拉取并比对历史 freeze blob；当前只验证当前 fixture 字节和嵌入
  provenance。
- 证据：两 run summary SHA256 见 §3.1。
- 修复要求：保持 NOT_RUN 边界，或在 CI 中显式 fetch 并比对；不得扩写为“历史 blob 已验证”。
- 验收断言：修复前，golden 证据只能表述为“当前字节 + 离线 provenance”，不是完整历史 pin。

### D-04（MINOR，证据完整性）native-exit timeout 合并日志缺失

- 位置：两 run 的 `native-exit-*/`。
- 复现：列目录并读 `results.json`。
- 实际：`timeout.log` 不存在；`timeout.log.stdout`/`.stderr` 各 15 bytes；
  `results.json` 的 timeout `Pass=true`、`Expected=throw`、`Actual=true`。
- 期望：若声明完整合并日志，应有 `timeout.log`；当前只能凭 stdout/stderr + results.json。
- 证据：PR native-exit 目录 `4fa49a10...`；push `b86559c3...`；`results.json` 721 bytes。
- 修复要求：超时路径在抛错前落盘合并日志；或明确 timeout 证据只有 stdout/stderr。
- 验收断言：修复后 timeout 有真实合并日志；修复前不把“6/6 均有合并日志”写进验收。

### D-05（MINOR，provenance 标签）PR run 测的是合成 merge commit

- 位置：`.github/workflows/v5-verify.yml` 的默认 checkout；PR job 日志。
- 复现：PR job `fetch +1b8c...:refs/remotes/pull/20/merge`；`HEAD is now at 1b8c01b`；
  artifact `run-info.txt` 的 `head=1b8c`。
- 实际：PR run 受测 commit 为 `1b8c01b`，不是字面 `bae6`；独立计算 tree 相等
  （`6da5de0...`）。
- 期望：若验收要求字面 SHA，应引用 push run 或设置 `ref: ${{ github.event.pull_request.head.sha }}`；
  当前只能用“tree 相同”证明源码内容。
- 证据：PR job log SHA `E1286A48...`；`run-info.txt`；merge tree `6da5de0...`。
- 修复要求：修正报告标签；如需字面 checkout，改 workflow 并产生新 run（本轮禁止重跑）。
- 验收断言：引用 PR run 时必须同时写 `1b8c` 与 tree 相等证明；不能单写“bae6 checkout”。

### D-06（MINOR，可复现性）provenance input 哈希是 checkout CRLF 字节

- 位置：`shared-plugin-provenance.json` 的 149 个 inputs。
- 复现：从 `git archive` 的 LF 冻结树逐文件算 SHA，与 provenance 比对。
- 实际：144/149 raw 匹配；5/149 仅在 LF→CRLF 归一后匹配；unexplained=0。5 个文件见 §5.2。
- 期望：验证者应知道哈希基于 CI checkout 行尾；或改为对 canonical LF 字节取哈希。
- 证据：`provenance-audit.json` SHA256 `9F929F8024B71C1048EFA459B6916F112AB863A394BCC9BE683D22085C26DF25`。
- 修复要求：文档明确行尾策略，或在生成 provenance 时记录/归一化；不要改源码内容。
- 验收断言：修复前，独立复核必须同时报告 raw 144/149 + CRLF 5/149；不能把 raw 5 个差异
  误判为源码漂移。

### D-07（NOT_RUN，验收覆盖）dotnetTest 模式未执行

- 位置：`tools/ci/verify-v5-table-postgres.ps1:44-61` 默认 `RunnerMode=direct`；
  `.github/workflows/v5-verify.yml:61` 未显式传 dotnetTest。
- 复现：读 `run-info.txt`、14 个 `direct.meta.txt`；查找 `dotnet-test-results.xml`。
- 实际：14/14 direct；`dotnetTest-xml/` 不存在；`dotnet-test-results.xml` 0 个。
- 期望：若验收声明“dotnet test 独立全绿”，必须显式 `-RunnerMode dotnetTest` 并保留原生
  dotnetTest XML/console/exit；本头没有。
- 证据：`meta-binding-audit.json` SHA256
  `1A0B19FBDDF017A695F0B5E16F1A97156E4C5EA630D1A83289E614C59974F5CA`。
- 修复要求：要么补跑 dotnetTest 并审计，要么在报告中明确 direct-only；不得混称。
- 验收断言：direct 2813/2813 只能作为 direct 证据；dotnetTest 缺失时状态必须是 NOT_RUN。

### D-08（BLOCKED，历史）`39970580589` 缺 framework 原日志

- 位置：历史 run `39970580589`，head `7a988d2b351470327fe2c4f51c19ce394dcb99a9`，
  `pull_request`，attempt 1，failure，2026-10-23。
- 复现：本次只读 run 元数据；未重新下载旧 archive/日志。
- 实际：历史 Client 失败缺少 framework 原日志；后续 green run 不构成根因修复证据。
- 期望：保留 UNRESOLVED/BLOCKED，直到原始 framework 日志或等价根因证据出现。
- 证据：`build/<GUID>/api/run-39970580589.json`；历史审计边界。
- 修复要求：不删除、不改写历史；补证必须来自原始日志/可复现实验，不能凭新 run 绿色宣布修复。
- 验收断言：历史项在报告中始终为 BLOCKED，直到有缺失日志或明确根因复现。

### D-09（INFO，元数据）step 编号 7–10 缺失

- 位置：jobs API step 数组。
- 实际：setup step 1、workflow 声明的五个 step 2–6、post/complete 11–13 全部 success；编号跳号是 GitHub 现象。
- 期望：以 step 名称/conclusion 和日志判断，不以编号连续性推断漏执行。
- 证据：`jobs-41261636998.json`/`jobs-41261632618.json`。
- 验收断言：不得把编号缺口写成“步骤缺失”。

### D-10（INFO，artifact 卫生）table artifact 含 22 个分析器临时 DLL

- 位置：`verify-v5/table/tmp/VBCSCompiler/**`。
- 实际：22 个文件、7,319,680 bytes；另有 7 个 `tmp/xml-selfcheck/*.xml` 负例 fixture。
  它们被 table manifest 覆盖，不是测试结果，不能计入 2813/42/13。
- 期望：可减少上传体积，但不影响正确性。
- 证据：`artifact-integrity.json`；table manifest 152/152。
- 验收断言：不得把这些文件当测试用例或“额外覆盖”。

### D-11（INFO，命名）1 对重复 display name，ID 仍唯一

- 位置：`Mahjong.Architecture.Tests.DependencyDirectionTests.UnsupportedDeclarationFormsFailClosed`
  （source line 279）。
- 实际：2 个 test id 不同、均 Pass；xUnit 把两个 theory 片段截断成相同 display name。
- 期望：以 test id 作为唯一键；不要用 display name 去重。
- 证据：`results-audit2.json` 中 `duplicate_names=1`，`duplicate_ids=0`。
- 验收断言：2813 唯一 id 不变；跨 run multiset 仍按 name+result 计数，不误删一条。

## 10. 历史失败 `39970580589`（保留，不关闭）

- run：`39970580589`，`pull_request`，head `7a988d2b351470327fe2c4f51c19ce394dcb99a9`，
  attempt 1，conclusion `failure`，2026-10-23T14:33:01Z 创建、14:35:13Z 更新。
- 历史边界：Client 失败缺少 framework 原日志；本次没有重新下载旧 archives，也没有用新
  `bae6` 的绿色宣布该历史根因已修。
- 分类：**BLOCKED/UNRESOLVED（历史）**。它与本轮执行范围无重叠；本轮不据此重新否定或重开
  其他任务，也不把新 green 写成历史修复。

## 11. 实际命令、native 退出、首失败与平台限制

### 11.1 主要实际命令与退出

| 命令（摘要） | native exit | 结果 |
|---|---:|---|
| `git status --short --branch`、`git rev-parse HEAD`、`git rev-parse origin/v5` | 0 | HEAD/branch clean；origin/v5=8fd7 |
| `git fetch --no-tags origin refs/pull/20/merge` | 0 | 得到 `1b8c01b`，tree 与 `bae6` 相同 |
| `git archive --format=zip --output=... bae6` | 0 | 329,442,392 bytes |
| `gh api .../actions/runs/{id}`、`/jobs`、`/artifacts`、`pulls/20` | 0 | 元数据/step/artifact digest |
| `curl.exe -sS -L .../artifacts/{id}/zip` | 0 | 两 ZIP 均与 server digest 一致 |
| `curl.exe -sS -L .../actions/jobs/{id}/logs` | 0 | 25,163 / 24,136 bytes |
| `tar -xf <artifact.zip> -C <dir>` | 0 | 270/270 文件 |
| `audit-results2.ps1` | 0 | 14 工程，2813/2813 |
| `audit-provenance.ps1` | 0 | 149 inputs、19 outputs、7/7 DLL commit |
| `audit-integrity.ps1` | 0 | ZIP/manifest 全部通过 |
| `audit-run-windows.ps1` | 0 | 两 run ID 交集 0 |
| `probe-table-xml-gate.ps1` | 0 | 两个负例均 green（缺陷复现） |
| `make-manifest.ps1` | 0 | 2431 源文件 + 548 证据文件 + 3003 全文件 |
| `gh api .../actions/runs/39970580589` | 0 | 历史 failure 元数据 |
| `gh api .../compare/bae6...4b5012f` | 0 | ahead 2，doc-only |
| `git grep ... test-v5-direct-results.ps1 ...` | 1（无匹配） | 证明 CI 无调用，不是失败 |

### 11.2 保留的首次失败/不完整记录

1. **首次 `git archive | tar` 管道卡住**：PowerShell 管道下 `tar` 进程无进展；已 kill 该 job/tar
   进程，改用 `git archive --format=zip` 落盘后 `bsdtar` 解压，exit 0。未使用卡住的半成品。
2. **首次 artifact 下载用 `gh api ... > file`**：PowerShell 文本重定向把 ZIP 写成 UTF-16
   （magic `FF FE`），长度 8,581,882/8,583,808 bytes，不是 ZIP；已丢弃并用
   `curl.exe -sS -L` 二进制重下。最终两 ZIP 的 magic 为 `50 4B 03 04`，SHA 与 server digest
   一致。
3. **首次 `Expand-Archive` 解压不完整**：22 个 `tmp/VBCSCompiler` 长路径触发 MAX_PATH，
   只得到 248/270 文件；已用 `bsdtar` 重新解压到同一目录，得到 270/270，且 152/152 table
   manifest 通过 `\\?\` 长路径复核。
4. **首次审计脚本因把 2813 个 name/result 键重复写入 JSON 造成 142MB 输出**：已改为只保留
   canonical SHA + 计数，原始 XML 仍是逐例真源；最终 `results-audit2.json` 261,413 bytes。

### 11.3 平台/范围限制

- 运行环境：GitHub `windows-latest` / Windows Server 2025，GitHub Actions runner 2.338.2，
  Git 2.56，.NET SDK 8.0.426，xUnit.net v3 4.0.2。
- 本审计未运行 Unity/Player/Android/adb/设备，不争 `Local\MahjongV5P45UnityValidation`，不运行
  C03 本地全量或受控 GAME 采集，不实现 C04 未来缺口。
- 本审计未改 workflow、门禁、业务、测试、PR、scheduler、`complete.flag`、账本；未
  approve_task/record_merge_request/push/merge/deploy。
- D 盘仅约 107MiB；所有下载/解压/临时脚本/manifest/审计输出均在 E 盘该树 ignored `build/`
  下；未删除/搬移历史证据，未改系统 TEMP/TMP（只对本子进程设置 E 盘 TEMP/TMP）。
- 未下载当前 PR 新头 `4b5012f` 的 CI；未把本报告扩展到 C03/C04/G2–G5。

## 12. 最终本地状态与非批准声明

- 审计报告是本任务唯一允许新增的 tracked 文件：
  `doc/v5-重构/V5-PR20-BAE6B8B0-CI-EVIDENCE-AUDIT-20261101.md`。
- 本地审计代码基线保持 accepted `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；
  其上仅新增本报告，接收方 HEAD 为 `19cd9bbb886ec4f9ff63d04e813cd155d4dbf262`，`build/` 全部 ignored；工作树 clean、diff-check 0。
- 本报告不是 G2 验收、不是 C03/C04/C05 批准、不是 PR20 合并审查、不是历史 39970580589 修复
  证明；不把 direct 2813/2813 写成 dotnetTest 通过，不把单条 durable state 写成自然整场终局
  通过。
- 调用方独立复核后可决定报告集成；本任务不创建组件 PR、不 push、不合并、不部署。

## 13. 调用方独立复核与报告接受

调用方在 `E:/majiang-review/caller-bae6-audit-closeout-6c18b39a` 将接收方唯一报告集成到既有 PR20 头 `4b5012f0abedde309a14b8a7d8201eca53d4fc31`，另行 GET 两个 run/job/artifact 元数据、原始 artifact/log ZIP 和合成 merge/source commit API。两份 artifact 均匹配服务器长度与 SHA；每份 270 项内容均与接收方 manifest 对应，table manifest 152/152、插件 outputs 19/19 长度/摘要一致。未重新运行或调度 CI。

实际本地校验 `verify-evidence.ps1`、`check-boundaries.ps1`、`verify-bindings-provenance.ps1`、`repro-table-gate.ps1` 全部 native exit 0：逐节点重算每 run 14 工程、196 collections、2813/2813 Pass、0 failed/errors/skipped/not-run，table 42/13 保持重复/子集口径；跨 run name/result multiset 一致，原始 ID 交集为 0。28 组 XML/命令/DLL 路径/SHA/console 绑定、两次各 149 输入（144 raw、5 CRLF）、7 自有 DLL 精确 checkout SHA、15 构建日志、6 native-exit 探针、13 table PG wrapper 阶段及 lobby 14 事件/cleanup/env-restore 均核验。原始 job 日志的长度与本报告摘要也匹配。冻结函数两个恶意 XML 均复现 table gate green、strict gate 拒绝；该已知门禁缺口没有被修复或隐藏。

调用方原始证据均在该树 `build/caller-review/`：`independent-results.json` SHA256 `5BE3BDD6448867BACB4868ECA7DDE45F7C047AFA3B83EC825B365368F83092FA`；`bindings-provenance-results.json` SHA256 `CE24E6EF46ED5ED99279F2EBB185C0386EE4A5543BBE32224CCC628B9764E1B6`；`boundary-results.json` SHA256 `F05F2E9FE2528ED124C6F2AAD2E94109DCBB804DCD29B2B9C44686103F224972`；`raw-job-bindings.json` SHA256 `AE794C52B95A79ABB394C8467F2CB6B53FF92B9406822EB2956D78C3D484B47D`。接收方报告和原始证据保持只读；调用方只修正本集成副本的基线/报告提交措辞与步骤编号，并追加此复核记录。

本审计报告范围通过；沿原线程接受并归档，纳入唯一现有 Draft PR20。接受的是冻结 `bae6` 的原生证据审计，不是当前发布头 CI 通过。PR 合成 merge 与 literal push、CRLF 与 LF 输入、direct 与 dotnetTest 的区别继续有效；历史 Client 根因仍 BLOCKED，dotnetTest/selfcheck/git pins/自然整场 PG 终局等限制不变。本次未运行 Unity、Player、adb 或设备，不批准 C03、正式 C04/C05、G2–G5，也不授权合并或部署。
