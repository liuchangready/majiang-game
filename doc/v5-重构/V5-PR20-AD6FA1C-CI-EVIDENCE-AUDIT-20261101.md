# PR20 冻结头 `ad6fa1c` 两次既有 CI 原生测试/PG 证据独立审计

日期：2026-11-01（Asia/Shanghai）。
审计工作树：`E:/majiang-review/pr20-ad6fa1c-ci-audit-ds-20261101-1844`，分支
`wt/v5-pr20-ad6fa1c-ci-audit-ds-20261101-1844`，报告提交的父提交为 accepted
`origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
冻结审计目标：`ad6fa1c01cfa48ae8f836dbf194d0f05f7da1920`（PR20 head，只读）。
PR：<https://github.com/liuchangready/majiang-game/pull/20>。

**结论边界：本报告只审计该冻结头的两次既有 CI 的 .NET/PostgreSQL 原生证据，是独立静态复算，
不是本轮 fresh 产品执行。它不是 C04/C05 或 G2–G5 验收，不是 PR 合并审查，不改实现、门禁、
workflow、PR、调度或账本；不运行 Unity/Player/Android/adb/设备，不重跑 CI，不运行本地 PG 或
.NET 产品测试。**

## 0. 结论摘要

- **冻结身份已绑定。** PR run `41297112055` 是 `pull_request`，实际 checkout 为 GitHub 合成
  merge `e6ade3750e9207c113225eb4991536cc734397d4`，parents 为 accepted `8fd7c6c` +
  目标 `ad6fa1c`，tree 为 `7e29d11edd3fc4d3237bf406448f9b7fd860969f`，与目标 tree **相同**；
  push run `41297107641` 实际 checkout 字面 `ad6fa1c`，tree 同为 `7e29d11`。两 run API
  `head_sha` 均为 `ad6fa1c`，但 PR 的受测 commit 必须写 `e6ade`，不能写成字面 `ad6fa1c`。
- **两次 run 的 14 个 direct 测试工程各自为 `2830/2830` Pass，`0` failed/errors/skipped/
  not-run，`198` 个 collection，`2830` 个唯一 test id。** 两 run 的 `project|name|result`
  multiset SHA256 均为
  `197D43FAD93B92FF6E42E66BD86C69A3D5C077ADDFEB347235C564A514068BE4`，但原始 test id
  交集为 **0**，是两次独立执行，不是复制同一份 XML。
- **table 专用阶段：`42/42` 与 `13/13`**。13 个名称精确包含在 42 个名称中；但 targeted 的
  13 个 test id 与 42 的 id 交集为 0，且 42 的 id 与 unified `Mahjong.Infrastructure.Tests`
  的 42 个 id 交集也为 0。三者是分别执行的，**唯一分母仍是 2830**，不能把 42/13 再加进去。
- **构建：每 run 15 份构建日志均 `Build succeeded / 0 Warning(s) / 0 Error(s)`。**
  共享插件实际为 **19 个输出文件 = 12 DLL + 7 PDB**，`5` 个包，`7` 个 required own
  assemblies；本 run 的 provenance 输入为 **150**（不是旧报告的 149），150/150 路径可在目标
  tree 解析，7/7 自有 DLL 的 `ProductVersion` 内嵌各自 run 的 head。
- **PG：真实 PostgreSQL `16.10`，table 集群 `127.0.0.1:55592`，lobby 集群
  `127.0.0.1:55593`。** crash writer 在 `stateVersion=1/lastEventSeq=2/eventCount=2` 后强杀，
  restart 恢复日志 3 行，reader 返回 `receiptStatus=Applied`、`terminal=true`、outbox 匹配；
  dump 后 restore reader 返回 `eventCount=2`、`recoveryOwnerEpoch=3`、`recovered=true`。
  这是**一条 durable table 状态**的故障恢复证据，不是自然整场/结算终局。
- **产品测试失败：本审计实际执行范围内 `FAIL = 0`。**
  **但源 CI 证据整体为 `REVIEW_INCOMPLETE`**：D-03（table/PG helper 的 command/native/
  空库前置/ownership 原件不完整）、D-08（历史 `39970580589` 仍缺 framework 原日志）是
  MAJOR；D-01/D-04/D-05/D-06/D-07 是 MINOR。D-02（两 run 实际 SDK/runtime 不同，但
  `global.json:latestPatch` 明确允许）和 D-09（checkout 字节 / Git blob 的跨表示比较）
  是 **INFO 环境/可移植性限制，不是已证实的配置违例**。不能仅凭 GitHub 绿色或本报告把该头
  写成“完整证据通过”；本次报告返修本身不改变这些源 CI 证据状态。
- **NOT_RUN：** dotnetTest 模式、19 例 direct 门禁自检、dotnetTest 门禁自检、`git pins`
  历史冻结 blob、Unity/Player/Android/adb/设备、自然整场终局。历史
  `39970580589` 仍为 **BLOCKED/UNRESOLVED**。
- 审计期间 PR20 仍为 OPEN/Draft，精确头仍为 `ad6fa1c`；本报告没有扩展到任何新头。

## 1. 冻结 source、merge 与 run 的精确身份

| 对象 | 实际值 |
|---|---|
| accepted `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（未移动） |
| accepted 是目标祖先 | `git merge-base --is-ancestor 8fd7c6c ad6fa1c` → exit `0` |
| 冻结 PR head | `ad6fa1c01cfa48ae8f836dbf194d0f05f7da1920` |
| `ad6fa1c` tree | `7e29d11edd3fc4d3237bf406448f9b7fd860969f` |
| `ad6fa1c` parent | `a8a480fb1a45b814112b4a78cfcf8d6af0c61c72` |
| `ad6fa1c` 相对 parent | 2 文件：D01 报告 +41 行；`tools/tests/VerifyP45C04RecoveryReconciliation.Tests.ps1` 仅删末尾空行（−1） |
| PR 合成 merge | `e6ade3750e9207c113225eb4991536cc734397d4` |
| merge parents | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` + `ad6fa1c01cfa48ae8f836dbf194d0f05f7da1920` |
| merge tree | `7e29d11edd3fc4d3237bf406448f9b7fd860969f`，与目标 tree 相同 |
| push 实际 checkout | `ad6fa1c01cfa48ae8f836dbf194d0f05f7da1920`，tree 同为 `7e29d11` |
| workflow blob | `9668462bb88a1982c20fbb8fb5fa2b558f6e97ca`（3573 bytes；API contents 同 blob） |
| 目标 tree | `2459` 个 blob，`389,462,644` bytes（`git ls-tree -r -l ad6fa1c`） |
| accepted→目标 | `507` commits、`738` 个路径 |
| PR20 | `OPEN` / `Draft` / head `ad6fa1c` / base `v5=8fd7c6c` / mergeable `clean` |

`git fetch --no-tags origin refs/pull/20/merge:refs/audit/pr20-merge-e6ade375` exit `0`；
只创建本地 audit ref，没有改默认分支或 PR。`gh api` 的 PR contents API 返回
`.github/workflows/v5-verify.yml` blob `9668462b...`，与 `git rev-parse ad6fa1c:.github/workflows/v5-verify.yml`
一致。

**身份措辞必须分开：** PR run 的 `summary.log`、`run-info.txt`、14 个 `direct.meta.txt` 的
`head` 都是 `e6ade375...`，`GITHUB_SHA=e6ade375...`；push run 才是 `ad6fa1c...`。PR run
的 tree 与 `ad6fa1c` 相同，所以测试的源码内容等价于冻结目标，但 provenance 标签不能写成
“PR 字面 checkout ad6fa1c”。见 D-12。

## 2. 两次 run、job、step 与 runner

| 项 | PR run | push run |
|---|---|---|
| run id | [41297112055](https://github.com/liuchangready/majiang-game/actions/runs/41297112055) | [41297107641](https://github.com/liuchangready/majiang-game/actions/runs/41297107641) |
| event | `pull_request` | `push` |
| attempt | 1 | 1 |
| status/conclusion | completed/success | completed/success |
| created（UTC） | 2026-11-01T10:21:07Z | 2026-11-01T10:21:04Z |
| job started / completed（UTC） | 10:21:10Z / 10:42:57Z | 10:21:06Z / 10:43:13Z |
| job id | `124820148303` | `124820135330` |
| runner | GitHub Actions 1000000377 / `windows-latest` | GitHub Actions 1000000376 / `windows-latest` |
| runner version | 2.338.2 | 2.338.2 |
| runner image | `win25-vs2026/20261021.278` | `win25-vs2026/20261030.294` |
| git | 2.56.0.windows.2 | 2.56.0.windows.4 |
| actual SDK | **8.0.426** | **8.0.427** |
| runtime in XML | **.NET 8.0.32** | **.NET 8.0.33** |
| xUnit | 4.0.2+0c7aa4991f | 4.0.2+0c7aa4991f |
| artifact | 13079638714 | 13080232591 |
| artifact bytes / digest | 4,516,396 / `sha256:7babe04e...` | 4,516,173 / `sha256:93556b09...` |
| job log bytes / SHA256 | 25,087 / `81964780E282B7F53D9050148A6FCA2F58D0D5934C4D63A48C0510CDCC697352` | 24,197 / `F42D7A6AEE6C67523C272CD63FCCC3C0689A978C7D7170F690EA1418ABC7C858` |

两 job 的 9 个 step 全部 `success`：`Set up job`、`Checkout`、`Setup .NET 8 SDK`、
`Verify native failure exit propagation`、`Run unified V5 verification`、
`Upload verification logs and test-framework diagnostics`、三个 Post/Complete。编号从 6
跳到 11 是 GitHub step 编号现象，不是漏执行。

checkout 原生日志：

- PR：`fetch ... +e6ade375...:refs/remotes/pull/20/merge`；
  `HEAD is now at e6ade37 Merge ad6fa1c... into 8fd7c6c...`；
  `git log -1` 输出 `e6ade375...`。
- push：`fetch ... +ad6fa1c...:refs/remotes/origin/feat/v5-post-g1-batch`；
  checkout 后 `git log -1` 输出 `ad6fa1c01cfa48ae8f836dbf194d0f05f7da1920`。
- 两 run 的 `Upload` 日志都记录 `270 files uploaded`；PR 上传 bytes `4516396`，push
  `4516173`，与 artifact API 的 size 一致。

## 3. artifact 下载、ZIP 与封存清单

全部下载为二进制落盘，未用 PowerShell 文本重定向；下载脚本未输出凭据。

| 项 | PR | push |
|---|---|---|
| ZIP 本地 bytes | 4,516,396 | 4,516,173 |
| ZIP SHA256 | `7BABE04E83E597B965206CBE91BB71E90C955BC8DE9016E195E9B37588EB73EE` | `93556B093A07DB5987123E43E624B6E1015E6230D496B48818D3F4CD2F6D244B` |
| server digest 一致 | 是 | 是 |
| ZIP entries / 解压 files | 270 / 270 | 270 / 270 |
| 解压 bytes | 13,228,489 | 13,229,546 |
| 重复 entry / 缺失 / 多余 | 0 / 0 / 0 | 0 / 0 / 0 |
| table `manifest.sha256.json` | 152/152 匹配，7,438,852 bytes | 152/152 匹配，7,438,893 bytes |
| table 目录文件（含 manifest） | 153，7,468,584 bytes | 153，7,468,625 bytes |
| table manifest SHA256 | `FC3A98814027945C661A1AA68FD88353FA2A187A86D1969709F61634E56DCE34` | `62ACCC992F64B0898C12054077628DE2B73BE70DF9E49EC47BABAC0BCB4B3071` |

22 个 `tmp/VBCSCompiler/AnalyzerAssemblyLoader/**` 长路径文件用 `\\?\` 长路径 IO
复核，152/152 通过；没有把 PowerShell 5.1 的 MAX_PATH 假缺失当作真实缺件。这些分析器临时
DLL 不是测试用例，不能计入 2830/42/13。

## 4. 逐工程、逐例独立重算

方法：逐工程直接解析 `direct-xml/<project>/direct-results.xml`，不把 `summary.log` 的 total
当输入。对每个 assembly：

1. 统计 `//test` 节点和 `result`；
2. 校验 assembly 的 `total/passed/failed/skipped/not-run/errors`；
3. 校验每个 collection 的节点数与属性，并求 collection 总和；
4. 统计 test id 唯一性和 exact-case display name 唯一性；
5. 与 `direct.meta.txt` 的 command/exit/total/result-xml SHA 对照；
6. 与 `direct-*.log` 的 console summary 对照；
7. 两 run 分开计算，再比较 multiset 和 id 交集。

### 4.1 每工程计数（两 run 各自均相同）

| 工程 | Total | Pass | Failed | Skipped | NotRun | Collections | Unique IDs | Unique names |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Mahjong.Ai.Tests | 444 | 444 | 0 | 0 | 0 | 13 | 444 | 444 |
| Mahjong.Architecture.Tests | 132 | 132 | 0 | 0 | 0 | 1 | 132 | 131 |
| Mahjong.Client.Tests | 182 | 182 | 0 | 0 | 0 | 10 | 182 | 182 |
| Mahjong.Client.Wss.Tests | 82 | 82 | 0 | 0 | 0 | 6 | 82 | 82 |
| Mahjong.Contracts.Tests | 152 | 152 | 0 | 0 | 0 | 8 | 152 | 152 |
| Mahjong.Domain.Tests | 529 | 529 | 0 | 0 | 0 | 34 | 529 | 529 |
| Mahjong.Infrastructure.Tests | 42 | 42 | 0 | 0 | 0 | 1 | 42 | 42 |
| Mahjong.LobbyService.Tests | 89 | 89 | 0 | 0 | 0 | 11 | 89 | 89 |
| Mahjong.LocalHost.Tests | 143 | 143 | 0 | 0 | 0 | 20 | 143 | 143 |
| Mahjong.P5.4b.B1.HttpsAdmission.Tests | 54 | 54 | 0 | 0 | 0 | 4 | 54 | 54 |
| Mahjong.TableRuntime.Tests | 483 | 483 | 0 | 0 | 0 | 57 | 483 | 483 |
| Mahjong.TableService.DurableHost.Tests | 59 | 59 | 0 | 0 | 0 | 2 | 59 | 59 |
| Mahjong.TableService.Tests | 204 | 204 | 0 | 0 | 0 | 19 | 204 | 204 |
| Mahjong.Unity.TableModel.Tests | 235 | 235 | 0 | 0 | 0 | 12 | 235 | 235 |
| **合计** | **2830** | **2830** | **0** | **0** | **0** | **198** | **2830** | **2829** |

断言（每 run 独立成立）：

- assembly `total` = `//test` 节点数 = 2830；198 个 collection 节点总和 = 2830；
- `passed` = 2830；`failed/errors/skipped/not-run` 全 0；
- 14/14 工程 `total > 0`，无 Total=0、空测试、永远 skip、未知 result；
- 2830 个 test id 全局唯一（不是仅工程内唯一）；
- 2829 个 exact-case 唯一 display name；唯一重复组是
  `Mahjong.Architecture.Tests.DependencyDirectionTests.UnsupportedDeclarationFormsFailClosed`
  （两个不同 test id，均 Pass，source line 279）；P5.4b 的 `abc123`/`ABC123` 仅
  case-insensitive 同名，exact-case 不同，不计为重复；
- 14/14 XML `assembly@name` 是实际 DLL 路径；14/14 `test-framework=xUnit.net v3 4.0.2+0c7aa4991f`；
- 14/14 `exit code: 0`、14/14 `result xml gate: PASS`、14/14 XML 实际 SHA 与 meta 记录一致；
- 14/14 meta 的 `TestResults purge before this invocation` 均为
  `cleared - not present before the run`；没有复用旧 XML；
- 14 个 direct meta 的命令均为
  `dotnet.exe <Tests.dll> -noLogo -reporter default -result-xml <...>`，runner 为
  `in-process (direct)`。

### 4.2 两次 run 的独立性与共同用例

- PR 测试窗口：`2026-11-01T10:24:55.1682495Z` → `10:42:03.6428617Z`；
  push：`2026-11-01T10:25:13.5662002Z` → `10:42:18.9876269Z`。
- 两 run 的 2830 个 test id 交集 **0**；这证明不是同一份 XML 被复制两次。
- 两 run 的 `project|name|result` 排序集合 SHA256 同为
  `197D43FAD93B92FF6E42E66BD86C69A3D5C077ADDFEB347235C564A514068BE4`。
- 两 run 的 exact-case display name 交集 **2829**；逐工程 total/pass/fail/skip/not-run 相同。
- 结论：共同用例身份覆盖 2829 个唯一显示名；执行身份由 0 交集的 test id 和不同时间窗口
  证明。不是“两次结果相加冒充覆盖量”。

### 4.3 table 专用 42/13 的重复/子集口径

| 阶段 | XML | Total | Pass | Failed | Skipped | NotRun | Collections | Unique IDs |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| table `tests` | `tests.xml` | 42 | 42 | 0 | 0 | 0 | 1 | 42 |
| table `tests-targeted` | `tests-targeted.xml` | 13 | 13 | 0 | 0 | 0 | 1 | 13 |

- 13 个 targeted 名称 **精确** 包含在 42 个名称中（13/13）；
- 但 targeted 的 13 个 id 与 42 的 id 交集为 **0**；它们是同一批用例的另一次执行；
- 42 个 table 名称与 unified `Mahjong.Infrastructure.Tests` 的 42 个名称 **精确相等**，
  但两边的 42 个 id 交集为 **0**，说明 table 全量阶段与 unified 阶段也是分别执行；
- 因此唯一测试分母是 **2830**；本 job 的执行次数是 `2830 + 42 + 13 = 2885`，不是覆盖量
  2885；42/13 不能加入 2830。
- table 的 42/13 XML 节点、collection 汇总、唯一 id 和 assembly 属性均独立复算正确。

### 4.4 direct vs dotnetTest 与工程发现

- 两 run 的 `summary.log`/`run-info.txt` 都写 `runner mode: direct`；
- 14 个 meta 都是 direct；artifact 中不存在 `dotnetTest-xml/`，`dotnet-test-results.xml`
  文件数为 **0**；
- workflow 调用 `verify-v5-table-postgres.ps1` 时没有传 `-RunnerMode dotnetTest`；该脚本
  默认 direct。故 **dotnetTest = NOT_RUN**，direct 结果不能改标签冒充 dotnetTest。
- 目标 tree 的 `dotnet/tests` 下 14 个 `*.Tests.csproj` 全部被发现执行；21 个 project row
  = 14 test + 7 build-only helper；没有缺测试工程。

## 5. 构建、共享插件闭包与 provenance

### 5.1 构建

- 每 run 检查 15 份日志（11 个按工程 build、`sln-build.log`、`plugin-copy.log`、table
  `build-tests.log`、`build-worker.log`）：**15/15 `Build succeeded`，0 Warning(s)，
  0 Error(s)**。
- PR 实际 SDK 8.0.426；push 实际 SDK **8.0.427**。这是
  `sdk-diagnostic.log`、`summary.log`、table `wrapper-summary.json` 和 XML
  `assembly@environment` 共同显示的事实，说明两 run **不是字节级同环境复现**。
  但目标 `global.json` 明确为 `version=8.0.426` + `rollForward=latestPatch`，
  该策略允许同 major/minor/feature band 内不低于指定版本的已安装 patch；workflow 的
  `setup-dotnet` 安装请求不是 resolver/runtime 的 exact pin。没有引用到另一份已接受的
  “两 run SDK/runtime 必须字面相等”契约，所以这是 **INFO 环境可复现性限制**，
  不是已证实的配置违例。更严格的 pin 是未来可选策略，不是本轮必修。见 D-02。
- `plugin-copy.log`：`native exit code: 0`、`copied 19 files`、`inputs: 150 files`、
  `outputs: 19 files`、`Unity plugins ready`。PR 构建时间 10:42:05–10:42:33，push
  10:42:20–10:42:49（UTC，按 provenance）。

### 5.2 19 个输出 / 12 DLL / 7 PDB / 150 inputs

- **19 个输出文件 = 12 DLL + 7 PDB**；
- 12 DLL = 7 个自有（Domain、Contracts、Contracts.Wire、TableRuntime、Client、LocalHost、
  Ai）+ 5 个包（Google.Protobuf 3.28.3、System.Buffers 4.4.0、System.Memory 4.5.3、
  System.Numerics.Vectors 4.4.0、System.Runtime.CompilerServices.Unsafe 4.5.2）；
- provenance `build.inputCount` = **150**，`inputs` 数组也是 150；`outputs` 19，
  `packages` 5，`requiredOwnAssemblies` 7；不是旧报告的 149；
- 150/150 输入路径均可在目标 tree 解析；长度 150/150 匹配；150/150 的 expected SHA
  与 Git object 的 LF blob **不匹配**，把 blob 的 LF 表示为 CRLF 后 **150/150 匹配**，
  unexplained 0；`build-shared.ps1` 和 `shared-plugin-provenance-lib.ps1` 也是 CRLF 后
  匹配。这是 **Git object LF 与 Windows checkout CRLF 的跨表示比较**；provenance 的
  实际生成函数 `Get-SharedPluginFileSha256` 直接对文件系统输入调用
  `Get-FileHash -LiteralPath`，没有做行尾归一。0/150 与 150/150 不表示源码漂移，也不
  证明生成器违反 `raw-bytes-no-normalisation`。见 D-09（INFO）。
- 19/19 output 的长度+SHA256 匹配；无缺、无多；5/5 package 输出哈希匹配；7/7 required
  own assemblies 存在；7/7 自有 DLL 有对应 PDB；
- 7/7 自有 DLL 的 `ProductVersion` 内嵌各自 run 的 head：PR 为
  `1.0.0+e6ade3750e9207c113225eb4991536cc734397d4`，push 为
  `1.0.0+ad6fa1c01cfa48ae8f836dbf194d0f05f7da1920`。两 run 的 DLL/PDB 字节不相同是预期
  的，不宣称跨 run 二进制相同。

## 6. 真实 PostgreSQL、crash/restart、backup/restore

### 6.1 table 集群与阶段

- `pg/postgres.log`：`starting PostgreSQL 16.10, compiled by Visual C++ build 1944,
  64-bit`；`listening on IPv4 address "127.0.0.1", port 55592`；ready。
- database：`mahjong_p52_test`；restore database：`mahjong_p52_restore`；`initdb` 在
  `artifacts/p52-pg-cluster-20261101-102139/data`（PR）/
  `...-102202/data`（push）。
- `pg-start.log`、`pg-stop-immediate.log`、`pg-restart.log`、`pg-cleanup-stop.log` 均为
  成功文本；`postgres-restart-recovery-lines.log` 3 行，包含
  `automatic recovery in progress`、`redo starts`、`ready to accept connections`。
- table 42/42 与 targeted 13/13 的 XML 计数已独立复算；但 `tests.log`/
  `tests-targeted.log` 没有 native exit 行，只有 wrapper 的 stage 自报 `exit 0`。见 D-03。

### 6.2 crash writer / reader

| 事实 | PR | push |
|---|---|---|
| writer pid / killedUtc / exit / forced | 3560 / `2026-11-01T10:23:10.0273336Z` / -1 / true | 2500 / `2026-11-01T10:23:31.7863653Z` / -1 / true |
| marker stateVersion / lastEventSeq / eventCount | 1 / 2 / 2 | 1 / 2 / 2 |
| marker request/scope/account/match | `p52-crash-request` / `p52-crash-scope` / `p52-crash-account` / `p52-crash-match` | 同左 |
| outboxSha256 | `c0b108923986159032998a13a1946582cbe1d948375ccc3b2abce926b0aa2a55` | 同左 |
| reader receipt / terminal / recovered | `Applied` / true / true | 同左 |
| reader eventCount / commandCount / cheatVersion | 2 / 1 / 1 | 同左 |
| reader recoveryOwnerEpoch | 2 | 2 |
| reader outboxPresent / payloadMatches / cheatStateRestored | true / true / true | 同左 |

writer 在 `10:23:09.653Z` 提交 marker，`10:23:10.027Z` 被强杀（PR）；immediate stop 后
restart 在 `10:23:12.167Z` ready，reader 读到 `terminal=true`。这不是“自然整场终局”：
`commandCount=1`、`eventCount=2`、单条 receipt/outbox，只是**一条 durable table 状态**的
故障恢复事实。

### 6.3 dump / 空库 restore

- `table_store.dump`：PR 44,712 bytes，SHA256
  `5477DE4060F09C3E439154A9452DD50950B6F4767016675FCF12775415BC940C`；push 44,715
  bytes，SHA256 `A48A03FA84B5A75F073992A35F328AA5CD70BB436E868825103C2F725A658A09`；
  文件头 `PGDMP`，是 PostgreSQL custom dump。
- restore reader：`eventCount=2`、`recoveryOwnerEpoch=3`、`recovered=true`、
  `terminal=true`、outbox SHA 与 crash 一致。
- 但 `createdb-mahjong_p52_restore.log`、`create-restore-db.log`、`pg-dump.log`、
  `pg-restore.log`、`drop-restore-db.log` 都是 **5 bytes（UTF-8 BOM + CRLF）**；
  `restore-grant.log` 仅 3 行 `GRANT`；没有 dump/restore 的原样命令行、native exit
  或“restore 前空库 table/row count=0”的原件。stage 的 `backup-restore exit 0` 是
  `wrapper-summary.json` 的 PASS 自报。见 D-03。
- 因 reader 确实从 restored DB 读回了 2 个 event 和匹配 outbox，**restored durable state
  本身可判 PASS**；但“精确 pg_dump→空库→pg_restore 命令/native/空库前置”不能独立判 PASS，
  只能判 **BLOCKED**。不能把单条 `terminal=true` 写成自然整场/结算通过。

### 6.4 table ownership/cleanup 与 lobby 集群

- table：`wrapper.log` 最后一行 `ownership-verified cluster removed: ...`；
  `pg-cleanup-stop.log` 说 `server stopped`；但 artifact 中没有保留 `pg-owner.json`，
  `pg/postgres.log` 也没有最终 shutdown 行。见 D-03。
- lobby：`start-postgres.log` 记录 PostgreSQL 16.10、`127.0.0.1:55593`（loopback only）、
  database `mahjong_lobby_test`、owner `mahjong-p53-pg`；`migrate.log` 记录
  `001_lobby.sql` 连跑两次、每次 exit 0、8 个 lobby tables；`postgres-evidence.jsonl`
  14 行，包含 server identity、2 条 terminal.projection（CLOSED/FINISHED）、receipt
  replay、expired reservation、3 条 crossRoomJoin、row-lock、transaction rollback、
  ticket consumption、room fill；`cleanup.log` 删除 root，`wrapper-summary` 的
  `rootDeleted=true`、`ownership=owned`、`plannedExit=0`、`verifier.exitCode=0`。
- lobby 的 terminal.projection 是单个 room lifecycle 的收口，不是自然整场终局；这 14 条
  JSONL 也不能扩写成 G2 的整场/结算验收。

## 7. 门禁、自检、历史边界

### 7.1 实际执行的门禁

- `tools/ci/test-v5-native-exit.ps1`：6/6 `results.json` 为 Pass（exit 0/1/2/17、timeout
  throw、forced-writer nonzero）；timeout 的 `timeout.log` **不存在**，只有 15-byte
  `.stdout`/`.stderr`。见 D-04。
- table wrapper 的 `Assert-XmlGatingSelfTest`：7 个负例（zero-tests、failed、errors、
  skipped、not-run、passed-mismatch、missing-not-run），CI 日志
  `negative XML cases=7`。
- 但 table XML gate 只读 assembly 六个属性；用冻结函数实测：
  - `assembly-only`（total/passed=42，0 个 test 节点）→ `gateGreen=true`；
  - `duplicate-id`（两个 `<test id="same-id">`）→ `gateGreen=true`；
  - 实际 `tests.xml`/`tests-targeted.xml` 也 true。见 D-01。
- 19 例 direct 门禁自检 `tools/ci/test-v5-direct-results.ps1` 和 dotnetTest 门禁自检
  `tools/ci/test-v5-dotnettest-results.ps1` 没有被 workflow/CI 调用：
  `git grep -n -e test-v5-direct-results.ps1 -e test-v5-dotnettest-results.ps1 ad6fa1c -- .github tools`
  exit `1`，job log 无调用行。见 D-05/D-06。
- `summary.log` 明确写 `git pins : NOT_RUN (historical freeze blobs not requested/fetched;
  current bytes and embedded provenance verified offline)`。见 D-07。
- dotnetTest 模式没有执行：无 `dotnetTest-xml/`、无 `dotnet-test-results.xml`，14/14
  meta 都是 direct。见 D-06。

### 7.2 历史失败 `39970580589`（保留，不关闭）

- run `39970580589`，`pull_request`，head `7a988d2b351470327fe2c4f51c19ce394dcb99a9`，
  attempt 1，conclusion `failure`，2026-10-23T14:33:01Z 创建、14:35:13Z 更新。
- 历史 Client 失败仍缺 framework 原日志；本轮只读 run 元数据，没有重新下载旧 archives，
  也没有用 ad6fa1c 的绿色宣布该历史根因已修。
- 分类：**BLOCKED/UNRESOLVED（历史）**。见 D-08。

## 8. PASS / FAIL / BLOCKED / NOT_RUN

| 项 | 严格分类 | 依据 |
|---|---|---|
| 两 run 元数据、attempt、job、9 step | **PASS** | §2；全部 success |
| PR merge parents/tree 与 push 字面 checkout | **PASS** | §1；merge tree = target tree |
| artifact server digest / 本地 SHA / 270 文件 | **PASS** | §3 |
| direct 14 工程 2830/2830（每 run 分开） | **PASS** | §4；0 fail/error/skip/notrun |
| 跨 run multiset 一致、test id 独立 | **PASS** | §4.2；id 交集 0 |
| table 42/42 与 targeted 13/13（子集执行） | **PASS（结果）** | §4.3；不加入 2830 |
| 构建 15/15 0 warning/0 error | **PASS** | §5.1 |
| 19 输出/12 DLL/7 PDB/150 inputs/7 own DLL | **PASS（实际范围）** | §5.2；行尾表示边界见 D-09（INFO） |
| PG 16.10、55592/55593、loopback、启动/重启 | **PASS** | §6 |
| crash/restart/reader 与 restore durable 状态 | **PASS（单条状态）** | §6.2/6.3 |
| 两 run SDK/runtime 字面一致 | **INFO（环境限制，非契约）** | D-02；PR 8.0.426/8.0.32，push 8.0.427/8.0.33；`latestPatch` 允许 |
| table XML gate 结构深度 | **FAIL（门禁）** | D-01；assembly-only/duplicate-id 均 green |
| table/PG helper 原生命令、native exit、空库前置、ownership | **BLOCKED** | D-03；空日志/无 marker/无 pre-count |
| native-exit timeout 合并日志 | **BLOCKED（MINOR）** | D-04；timeout.log 缺失 |
| 19 例 direct 门禁自检 | **NOT_RUN** | D-05；CI 无调用 |
| dotnetTest 门禁自检 | **NOT_RUN** | D-05；CI 无调用 |
| dotnetTest 模式 | **NOT_RUN** | D-06；全为 direct |
| `git pins` 历史冻结 blob | **NOT_RUN** | D-07；summary 自报 |
| 历史 `39970580589` 根因 | **BLOCKED** | D-08；缺 framework 原日志 |
| Unity/Player/Android/adb/设备 | **NOT_RUN** | 明确不在范围 |
| 自然整场终局/结算终局 | **NOT_RUN** | 只有单条 durable table 状态 |
| 当前 PR 新头（若移动） | **NOT_RUN** | 本报告只审冻结 ad6fa1c |

**总体：`REVIEW_INCOMPLETE`（源 CI 证据）。** 产品测试实际执行为 0 FAIL；D-03/D-08 的
MAJOR 证据问题未关闭，D-01/D-04/D-05/D-06/D-07 仍有 MINOR 限制，不能把两次 GitHub 绿色
写成“该头完整证据通过”。**D-02/D-09 是 INFO 环境/表示限制，不再作为 FAIL 或必修违例；
本次报告范围（AUD-AD6-R1-01）已按此纠正，不等于把源 CI 整体改判 PASS。**

## 9. 已知缺陷与限制完整清单

本轮在 `ad6fa1c` 原件上重新复现/核对，不把 BAE6 报告的结论复制为新头结论。清单共
**13 项（含 INFO 观察）**：BLOCKER **0**；MAJOR **2**（D-03、D-08，其中 D-08 为历史
BLOCKED）；MINOR **5**（D-01、D-04、D-05、D-06、D-07）；INFO **6**（D-02、D-09、
D-10–D-13，其中 D-02/D-09 是环境/表示限制，不是已证实的配置违例）。

### D-01（MINOR，门禁深度）table XML gate 只看 assembly 汇总

- **位置：** `tools/ci/verify-v5-table-postgres.ps1:725-753`（`Get-XmlCounts`/
  `Test-XmlCountsPass`），自检 `755-785`；调用点 `875`、`889`。
- **复现：**
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/8ad5aaf679e247e4abbd31635233250d/audit-scripts/probe-table-gate.ps1`
- **实际：** `assembly-only`（`total=42,passed=42`，0 个 `<test>`）返回
  `gateGreen=true`；`duplicate-id`（两个 `<test id="same-id">`）返回
  `gateGreen=true`；实际 `tests.xml`/`tests-targeted.xml` 也 true。自检 7 例只覆盖
  assembly 属性，不覆盖节点数、collection 汇总、重复 id、assembly 身份。
- **期望：** 两类恶意 fixture 均 false；实际 42/13 仍 true；门禁至少复用 strict direct
  validator 的 test 节点/collection/唯一 id 检查。
- **证据：** `audit-output/gate-probe.json` SHA256
  `C992CA73C25874AB2FBA69ACC64BE610398F0CF146F094E71B6865529883E7EB`；
  probe 脚本 SHA256 `0A9B80BF55BE6EC2B30606DE1407A1C47B59A8BF01DBE5BB63D4328EFA9B07DD`；
  目标脚本 blob `d18deb61d16f22329b7465a365eb9c46ef13092d`（CRLF SHA
  `40CE67A76CD8776FC122617EE8F71050AE665E24D81CDCF5FA90D1D5B0B22DF8`）。
- **修复要求：** table gate 复用 strict 结构校验，或把 assembly-only/duplicate-id/
  collection mismatch/assembly mismatch 加入负例矩阵；不得删除既有 7 例或放宽断言。
- **验收断言：** 两个恶意 fixture 实际 false；实际 42/13 true；CI 日志出现新增负例且
  全部按预期拒绝。

### D-02（INFO，环境可复现性）PR/push 实际 SDK 与 runtime 不同，但 `latestPatch` 允许

- **位置：** `.github/workflows/v5-verify.yml:45-47`（`setup-dotnet` 声明 8.0.426）；
  `global.json:3-4`（`8.0.426` + `rollForward: latestPatch`）；两 run 的 runner image。
- **复现：**
  `git show ad6fa1c:global.json`；
  `Get-Content build/8ad5aaf679e247e4abbd31635233250d/evidence/extracted/pr/verify-v5/unified/sdk-diagnostic.log`；
  对 push 同路径读取；再读两 run `summary.log` 与 XML `assembly@environment`。
- **实际：** PR `dotnet --version = 8.0.426`、XML `64-bit (x64) .NET 8.0.32`；
  push `dotnet --version = 8.0.427`、XML `64-bit (x64) .NET 8.0.33`。
  workflow 声明 8.0.426，`setup-dotnet` 也安装了 8.0.426，但 `global.json` 的
  `latestPatch` 在 push runner image（`win25-vs2026/20261030.294`，预装 8.0.427）上
  选择了 8.0.427；PR image 是 `20261021.278`，选择 8.0.426。push 的
  `table/wrapper-summary.json` 和 `fresh-build` 也写 8.0.427。
- **契约核对（关键）：** `global.json` 明确是 `version=8.0.426` +
  `rollForward=latestPatch`；按 Microsoft 的
  [rollForward 定义](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json#rollforward)，
  `latestPatch` 允许使用同一 major/minor/feature band 内不低于指定版本的已安装 patch，
  8.0.427 是该策略允许的选择。`setup-dotnet` 的安装请求不等于 SDK resolver 或 runtime
  的 exact pin；`global.json` 本身不把运行时固定为 8.0.32。当前没有引用到另一份已接受的
  “两 run SDK/runtime 必须字面相等”契约，因此这是**实测环境差异/可复现性限制**，
  不是已证实的配置违例。
- **期望：** 报告保留两 run 实际版本不同、不是字节级同环境复现这一事实，并标为 INFO；
  更严格的 SDK/runtime pin 是未来可选强化策略，不是本轮必修；若确有既存 exact 契约，
  必须给出该契约的精确文档/代码与违反证据后再改判。
- **证据：** PR `sdk-diagnostic.log` SHA256
  `08D603D6F5F6CB87D2EE901C7E6B09B2C16C23566CECAB724E539BC543323A29`；push
  `9501F34B6D47D529002ECFB9B38334761BB25C5C00427872A9DFADA911226A2F`；summary SHA
  `5CD8C30C34D1E7845A95B9752A2EC7216A60A8CFADD7E04A55B71A75A03E944B` /
  `C54EB788B0354EB9D02C01E25BFEC5E8618978B204D8847EB4FA0ED34BA14346`。
- **修复要求：** 仅修正报告口径，不要求改 `workflow`/`global.json`/runtime/重跑 CI；
  如未来选择更强 pin，可另行作为政策变更评估，不能写成当前已违反的验收条件。
- **验收断言：** 报告保留 `8.0.426/8.0.32` 与 `8.0.427/8.0.33` 的实际值并标 INFO；
  不再以“workflow 安装了 8.0.426”推导 exact 约束，也不把允许的 patch 选择判为 FAIL；
  若未来引用 exact 契约，必须同时给出契约来源与违反证据。

### D-03（MAJOR，原生证据）table/PG helper 的 command、native exit、空库前置和 ownership 原件不完整

- **位置：** `tools/ci/verify-v5-table-postgres.ps1:386-419`（`Invoke-Native` 只把
  stdout/stderr 写入 `<log>`，不落 command/exit）；调用点 `561`（download）、`858`
  （build-tests）、`871`（tests）、`883`（tests-targeted）、`903/917`（writer/reader）、
  `939/946/951/957/964/975`（dump/drop/create/restore/grant/restore-reader）。
- **复现：**
  `Get-ChildItem build/8ad5aaf679e247e4abbd31635233250d/evidence/extracted/pr/verify-v5/table -File | Select Name,Length`；
  再读 `pg-dump.log`、`pg-restore.log`、`create-restore-db.log`、`drop-restore-db.log`、
  `tests.log`、`wrapper-summary.json`，并对 push 重复。
- **实际：**
  - 4 个 dump/restore helper 日志（`pg-dump.log`、`pg-restore.log`、
    `create-restore-db.log`、`drop-restore-db.log`）均为 **5 bytes**
    （`EF BB BF 0D 0A`，即 BOM+CRLF），没有命令、native exit、stderr 或阶段时间；
  - `crash-writer.log` 也为 5 bytes；kill JSON 记 `exitCode=-1/forced=true`，但没有
    command line；
  - `tests.log`/`tests-targeted.log` 只有 xUnit console summary，无 native exit 行；
  - `download.log` 也为 5 bytes，没有下载 URL/源 ZIP/SHA/退出码；
  - table artifact 中没有 `pg-owner.json`；`pg/postgres.log` 没有最终 shutdown 行；
    `wrapper-summary.json` 的 `backup-restore`/`tests` 只有 stage 级 `exit 0` 自报；
  - restore 前“空库”没有独立 `SELECT count(*)`/table count 原件，只有代码顺序
    `dropdb`→`createdb` 和 stage detail。
  - 下游 `restore-reader-result.json` 确实读回 `eventCount=2`、`ownerEpoch=3`，所以
    **restored state 本身有独立结果**；缺的是“这条命令确实以 0 退出、restore 前确实为空”
    的可复核原件。
- **期望：** 每个 helper 生成 `<log>.meta.txt`：原样 command line、native exit、开始/结束、
  timeout；测试日志追加 native exit；保留 `pg-owner.json` 和最终 `pg_ctl stop` 日志；
  restore 前执行并记录 `SELECT count(*) FROM information_schema.tables WHERE
  table_schema='table_store'` = 0（或等价空库断言）。
- **证据：** `pg-dump.log`/`pg-restore.log`/`drop-restore-db.log`/`create-restore-db.log`/
  `download.log` SHA256 均为
  `F01A374E9C81E3DB89B3A42940C4D6A5447684986A1296E42BF13F196EED6295`；table
  `wrapper-summary.json` SHA PR `D4AD9718CEFB3CEA4FEAD8F1088504D61BEF5DC8BE6E190BA2785ACCBB9E75A2`
  / push `6640CD814F20264DAD99F4DE19A2A31F83EA060096DA6D4A3E57951518DFC194`；
  restore reader `restore-reader-result.json`。
- **修复要求：** 扩展 `Invoke-Native` 统一写 meta；不要把 wrapper 的 stage 自报当 native
  原件；保留 ownership marker；增加 pre-restore 空库断言和 stop 日志。负例矩阵至少覆盖
  非零 exit、timeout、空日志、restore 前 count>0、marker 缺失；任一负例必须让 stage
  FAIL，不能只靠后续 reader 成功掩盖。
- **验收断言：** 所有 helper 有可解析的 command/native/exit；测试日志有 native exit；
  owner marker 与最终 stop 日志存在；restore 前 count=0 原件存在；实际 restore reader
  仍 `eventCount=2/ownerEpoch=3`；上述负例全部被拒绝。

### D-04（MINOR，证据完整性）native-exit timeout 合并日志缺失

- **位置：** `tools/ci/test-v5-native-exit.ps1:29-36`；`Invoke-Native` 超时路径
  `verify-v5-table-postgres.ps1:386-419`。
- **复现：** 列
  `build/8ad5aaf679e247e4abbd31635233250d/evidence/extracted/pr/native-exit-*/` 和 push
  同目录；读 `results.json`。
- **实际：** `timeout.log` 不存在；`timeout.log.stdout`/`.stderr` 各 15 bytes
  （`stdout code=0`/`stderr code=0`）；`results.json` 的 timeout `Pass=true`、
  `Expected=throw`、`Actual=true`。这是对抛错行为的自报，不是合并日志。
- **期望：** 超时路径在 kill/throw 前写 `timeout.log`，包含子进程 stdout/stderr、timeout
  原因和实际等待时间；或明确把 timeout 证据限定为 stdout/stderr + results.json。
- **证据：** 两 run `results.json` 721 bytes；PR native-exit 目录
  `native-exit-dc598fe6ded2426486fbcccada14745e`，push
  `native-exit-a895eab611d44459ad0e230b50b7e0ae`。
- **修复要求：** 超时分支先落盘合并日志再抛错；不得用“6/6 Pass”代替超时日志。
- **验收断言：** 修复后 timeout 有真实合并日志；负例（子进程不退出）仍 throw 且日志存在；
  修复前报告只能写 6/6 结果、不能写“6/6 均有合并日志”。

### D-05（MINOR，覆盖）19 例 direct 门禁自检与 dotnetTest 门禁自检未在 CI 执行

- **位置：** `.github/workflows/v5-verify.yml:57-61`；
  `tools/ci/test-v5-direct-results.ps1`（19 个 case）；
  `tools/ci/test-v5-dotnettest-results.ps1`。
- **复现：** `git grep -n -e test-v5-direct-results.ps1 -e test-v5-dotnettest-results.ps1 ad6fa1c -- .github tools`
  → exit `1`，无引用；job log 也没有这两个脚本的调用行。
- **实际：** CI 只跑 native-exit 6/6 和 table 内 7 例 selfcheck；19 例 direct 矩阵、
  dotnetTest 矩阵均 NOT_RUN。
- **期望：** 若验收声明这些门禁矩阵已由 CI 验证，必须显式执行并保留原始结果；否则只能
  声明 7 例 table selfcheck，不能引用 19 例/dotnetTest 矩阵。
- **证据：** 两个脚本 blob `c08a35aeb0c8a2725b25df3fda48d68aae1c4edc` /
  `fe51b098a66e8c7c8bb399555ae85cfb89d8296b`；`git grep` exit1；job log 无调用。
- **修复要求：** 增加 CI step（或修正验收表述）；不要删除/弱化既有 19 例。
- **验收断言：** CI 日志出现两脚本实际调用、exit 0、19/19 与 dotnetTest 矩阵分别有原始
  结果；修复前不得引用这两组矩阵作为本头 CI 证据。

### D-06（MINOR，模式覆盖）dotnetTest 模式未执行

- **位置：** `tools/ci/verify-v5-table-postgres.ps1` 默认 `RunnerMode=direct`；
  `.github/workflows/v5-verify.yml:59-61` 未显式传 `-RunnerMode dotnetTest`。
- **复现：** 检查 `summary.log`/`run-info.txt`/14 个 `direct.meta.txt`；查找
  `dotnetTest-xml/` 和 `dotnet-test-results.xml`。
- **实际：** 14/14 direct；`dotnetTest-xml/` 不存在；`dotnet-test-results.xml` 0 个。
- **期望：** 若验收声明 “dotnet test 独立全绿”，必须显式运行并保留原生 dotnetTest XML/
  console/exit；本头没有，状态必须 NOT_RUN。
- **证据：** `results-audit.json` 的 `dotnetTestDirPresent=false`、
  `dotnetTestXmlFiles=0`；14 个 meta 命令均为 direct。
- **修复要求：** 补跑 dotnetTest 并审计，或在验收中明确 direct-only；不得混称。
- **验收断言：** direct 2830/2830 只能作为 direct 证据；dotnetTest 缺失时状态必须是
  NOT_RUN。

### D-07（MINOR，历史绑定）`git pins` 历史冻结 blob 未校验

- **位置：** `tools/verify-v5.ps1:1859`；两 run `summary.log` 的 `git pins` 行。
- **复现：** 直接读 `summary.log` 的 `git pins` 行。
- **实际：** `NOT_RUN (historical freeze blobs not requested/fetched; current bytes and
  embedded provenance verified offline)`；只验证当前 fixture 字节和嵌入 provenance。
- **期望：** 若声明历史 pin，必须拉取历史 freeze blob 并比对；当前只能声明当前字节 +
  离线 provenance。
- **证据：** 两 run summary SHA（D-02 已列）；`verify-v5.ps1` blob
  `6ad4eed91a94ce8cda3a12a1e78a01418a20ed0f`。
- **修复要求：** 保持 NOT_RUN 边界，或在 CI 中显式 fetch 并比对；不得扩写为“历史 blob
  已验证”。
- **验收断言：** 修复前 golden 证据只能表述为“当前字节 + 离线 provenance”；若要求完整
  历史 pin，必须看到实际 blob 比对记录。

### D-08（MAJOR，历史 BLOCKED）`39970580589` 缺 framework 原日志

- **位置：** 历史 run `39970580589`，head `7a988d2b351470327fe2c4f51c19ce394dcb99a9`，
  `pull_request`，attempt 1，failure，2026-10-23。
- **复现：** `gh api repos/liuchangready/majiang-game/actions/runs/39970580589`；本轮只读
  元数据，未下载旧 archive/日志。
- **实际：** conclusion `failure`；历史 Client 失败仍缺 framework 原日志；后续 green run
  不构成根因修复证据。
- **期望：** 保留 UNRESOLVED/BLOCKED，直到原始 framework 日志或等价根因证据出现。
- **证据：** `evidence/api/run-39970580589.json` SHA256
  `4671D645686BCE47809D0049CAF9E494865E8E5DD4589281D48BCED0A1A28968`。
- **修复要求：** 不删除、不改写历史；补证必须来自原始日志/可复现实验，不能凭新 run 绿色
  宣布修复。
- **验收断言：** 历史项在报告中始终为 BLOCKED，直到有缺失日志或明确根因复现。

### D-09（INFO，表示/可移植性）provenance 哈希是 checkout 文件字节，不是 Git blob 的 canonical LF

- **位置：** `shared-plugin-provenance.json` 的 `inputs` 与
  `inputBoundary.lineEndings = raw-bytes-no-normalisation`；实际生成函数
  `Get-SharedPluginFileSha256`（`tools/unity/shared-plugin-provenance-lib.ps1:150-153`）。
- **复现：**
  `git show ad6fa1c:tools/unity/shared-plugin-provenance-lib.ps1`；
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/8ad5aaf679e247e4abbd31635233250d/audit-scripts/audit-provenance.ps1`；
  再用二进制 `git cat-file` 读目标 blob，分别复算 Git object LF 和 CI checkout CRLF。
- **实际：** 150 个 input 与 Git object LF blob 的 raw 匹配数为 **0**；把 blob 表示为
  CRLF 后 150/150 匹配；长度 150/150；unexplained 0。`Get-SharedPluginFileSha256` 直接
  调用 `Get-FileHash -LiteralPath` 对实际文件系统输入取哈希，函数内没有行尾归一。
  因此 0/150 与 150/150 是 **Git object LF 与 Windows checkout CRLF 的跨表示比较**：
  Git checkout 的转换、审计为比较不同表示而做的 CRLF 转换，都不证明 provenance 生成器
  在生成时归一过字节，也不证明它承诺对 Git blob 取哈希。
- **契约核对（关键）：** `raw-bytes-no-normalisation` 描述的是实际 checkout/input 文件
  的原字节哈希，不是 Git object 的 canonical LF 字节；它与“对实际文件 `Get-FileHash`”
  的实现一致，不构成已证实的语义冲突。
- **期望：** 报告保留 0/150 + 150/150 的精确数字，并说明 CI checkout/Windows 行尾边界和
  跨表示比较方法，作为 INFO/可移植性说明；不要求 canonical LF、不要求修改 schema，
  也不把该表示差异写成 MINOR 或 FAIL。
- **证据：** `audit-output/provenance-audit.json` SHA256
  `302BFDE2229D46B0296E44CA0B2357E5A056CC10B9925C9CAE2386A07CF21877`；
  PR provenance SHA `8DBDD92C5D10BC6521622E37BD6962E0C6BC1962DE43728994DA78A40BE785AD`，
  push `4C4C6829E449A9F934C837D9D02BD639E07D1192282EB53FC61F3F07163EECBE`。
- **修复要求：** 仅修正报告口径；可选的未来文档强化是明确“CI checkout bytes, CRLF on
  Windows”的说明，不是必修项；不要改源码/封存 manifest，也不要要求生成器改成 LF hash。
- **验收断言：** 报告同时保留 raw 0/150 与 CRLF 150/150、unexplained 0，并标 INFO；
  不以该跨表示差异判 MINOR/FAIL，不要求 canonical LF 或 schema 变更；若未来证明生成器
  实际做了归一或明确承诺 hash Git blob，再另行评估。

### D-10（INFO，命名）1 对 exact-case 重复 display name，test id 仍唯一

- **位置：** `Mahjong.Architecture.Tests.DependencyDirectionTests.UnsupportedDeclarationFormsFailClosed`
  （source line 279）；`Mahjong.P5.4b.B1.HttpsAdmission.Tests.RedactionTests.SensitiveInputsAreNotEchoed`
  的 `abc123`/`ABC123` 只是 case-insensitive 同名，不是 exact-case 重复。
- **复现：** 对每个 XML 的 `test@name` 做 exact-case `Group-Object -CaseSensitive`。
- **实际：** 2830 个 test id 全部唯一；2829 个 exact-case 唯一名；Architecture 有 1 对
  完全相同 display name，两个 id 不同、均 Pass。
- **期望：** 以 test id 为唯一键；若要求 display name 唯一，应消除该 theory 的截断/同名。
- **证据：** `results-audit.json` 中 `uniqueNames=2829`、`duplicateNames=1`；
  Architecture XML 两个 test id `c536dee6...`/`94694fea...`（PR）和
  `aadffe90...`/`d03fe300...`（push）。
- **修复要求：** 不需要改测试逻辑；文档明确 id 唯一性，或改 display name。不得用 name
  去重导致少算一条。
- **验收断言：** 2830 个 id 仍唯一；跨 run 仍按 name+result 计数；不把 1 对同名误删。

### D-11（INFO，平台）Actions 的 Node.js 20 deprecation warning

- **位置：** 两 run job log 末尾；不是构建日志。
- **复现：** 在 job log 中搜索 `##[warning]Node.js 20 is deprecated`。
- **实际：** `actions/checkout@v4`、`setup-dotnet@v4`、`upload-artifact@v4` 被强制在
  Node.js 24 下运行，产生 deprecation warning；构建本身 15/15 0 warning/0 error。
- **期望：** 升级 action 版本或记录该平台 warning；不应把它混算成构建 warning。
- **证据：** 两 job log SHA（§2）；`matches` 中唯一 `##[warning]` 是该行。
- **修复要求：** 升级 action；或在报告中单列平台 warning。
- **验收断言：** 构建日志仍 0/0；平台 warning 不被写成产品 warning 或测试失败。

### D-12（INFO，标签）PR run 测的是合成 merge，不是字面 head

- **位置：** PR job `Checkout`；`run-info.txt`/`summary.log`/14 个 meta 的 head。
- **复现：** PR job log 的 `fetch +e6ade375...:refs/remotes/pull/20/merge`、
  `HEAD is now at e6ade37`；`run-info` 的 `head=e6ade`。
- **实际：** PR 受测 commit 为 `e6ade375`；其 tree 与目标 `ad6fa1c` 相同。push run 才是
  字面 `ad6fa1c`。
- **期望：** 引用 PR run 时必须同时写 `e6ade` 与 tree 相等证明；不能单写“PR checkout
  ad6fa1c”。若验收要求字面 SHA，只能引用 push run 或改 workflow ref（本轮禁止重跑）。
- **证据：** PR job log SHA、`run-info.txt`、merge parents/tree、`git show -s --format=%T
  e6ade375`。
- **修复要求：** 修正报告标签；如需字面 checkout，改 workflow 并产生新 run，不能改本报告
  的冻结范围。
- **验收断言：** PR 证据始终同时出现 `e6ade` 和 `7e29d11` tree 相等；不得用 API
  `head_sha=ad6fa1c` 推断字面 checkout。

### D-13（INFO，schema）`crash-writer` 的 stage `exitCode` 是字符串 `"KILLED"`

- **位置：** `table/wrapper-summary.json` 的 `stages[crash-writer].exitCode`。
- **复现：** `(Get-Content wrapper-summary.json -Raw | ConvertFrom-Json).stages |
  Where-Object stage -eq 'crash-writer'`。
- **实际：** `exitCode` 为字符串 `"KILLED"`，其他 stage 为整数；真实的
  `crash-writer.log.kill.json` 是 `exitCode: -1`、`forced: true`。
- **期望：** 阶段 schema 保持类型一致（整数/`null` + 独立 `killed` 字段），或明确文档化
  该字符串。
- **证据：** PR wrapper summary SHA `D4AD9718...`；push `6640CD81...`；kill JSON。
- **修复要求：** 改为 `exitCode: -1`/`killed: true` 或明确 schema；不要改 kill 事实。
- **验收断言：** 解析器可按统一类型读取；`forced/exitCode` 仍与 kill JSON 一致。

### 9.1 统一负例矩阵

**table XML gate（D-01）**

| 负例 | 期望 | 本 run 实测/代码行为 |
|---|---|---|
| assembly-only（total/passed=42，0 test） | 拒绝 | **接受**（probe `gateGreen=true`） |
| duplicate test id | 拒绝 | **接受**（probe `gateGreen=true`） |
| zero tests | 拒绝 | 拒绝（selfcheck） |
| failed / errors / skipped / not-run | 拒绝 | 拒绝（selfcheck，仅 assembly 属性） |
| total 与节点数不一致 | 拒绝 | 不检查节点数（assembly 自洽即可） |
| collection 汇总不一致 | 拒绝 | 不检查 collection |
| empty/malformed XML | 拒绝 | 解析异常；但无结构校验 |

**table/PG helper（D-03）**

| helper 负例 | 期望 | 本 run 原件现状 |
|---|---|---|
| helper native exit 非 0 | stage FAIL，保留 exit/命令 | 代码检查 exit，但无 meta/原样命令 |
| helper timeout | stage FAIL，保留 timeout 日志 | 无 meta/timeout 日志 |
| helper 日志只有 BOM/空 | 不得 PASS | `pg-dump`/`pg-restore` 等 5-byte 日志仍 PASS |
| restore 前 table_store 非空 | 拒绝“空库 restore” | 无 pre-count 原件 |
| owner marker 缺失/不匹配 | cleanup 拒绝或保留 marker 证据 | table artifact 无 marker，cleanup 仍 PASS |
| test native exit 非 0 但 XML 全 Pass | 拒绝 | 代码检查 exit，但 `tests.log` 无 exit 行 |
| download SHA 不匹配 | FAIL | `download.log` 为空，无下载/SHA 原件 |

**环境/表示观察（D-02/D-09，不构成负例违例）**

| 观察 | 保留事实 | 结论 |
|---|---|---|
| SDK/runtime 差异 | PR 8.0.426/8.0.32；push 8.0.427/8.0.33 | `latestPatch` 允许；INFO 环境可复现性限制，不是 FAIL |
| provenance 输入表示 | Git LF raw 0/150；checkout CRLF 150/150；unexplained 0 | 生成器 hash 实际文件字节；INFO 跨表示说明，不是 MINOR |
| 未来更强 pin/LF hash | 未作为当前契约引用 | 可选政策/文档强化，不是本轮必修 |

**覆盖/模式（D-05/D-06/D-07）**

| 模式 | 本 run 实际 | 需要 |
|---|---|---|
| direct 14 工程 | 执行，2830/2830 | PASS |
| table 42/13 | 执行，42/42、13/13 | PASS（子集） |
| dotnetTest | NOT_RUN（无 XML/目录） | 若声明则必须显式执行 |
| 19 例 direct 门禁自检 | NOT_RUN（CI 无调用） | 若声明则必须执行 |
| dotnetTest 门禁自检 | NOT_RUN（CI 无调用） | 若声明则必须执行 |
| git pins 历史 blob | NOT_RUN（summary 自报） | 若声明则必须 fetch/比对 |
| Unity/Player/Android/设备 | NOT_RUN | 明确不在范围 |

### 9.2 AUD-AD6-R1-01 返修映射

| 复核子项 | 原报告 | 本轮修正 |
|---|---|---|
| A. SDK/runtime 差异 | D-02 标 MAJOR/FAIL，并把 `rollForward: disable`、固定 runtime、新 run 写成必修 | D-02 改为 **INFO 环境可复现性限制**；保留 8.0.426/8.0.32 与 8.0.427/8.0.33；说明 `global.json:latestPatch` 明确允许 8.0.427；更严格 pin 改为未来可选政策，不是本轮必修 |
| B. provenance 行尾 | D-09 标 MINOR，要求 canonical LF 或 schema 修改 | D-09 改为 **INFO 表示/可移植性说明**；保留 raw 0/150 + CRLF 150/150 + unexplained 0；引用 `Get-SharedPluginFileSha256` 直接对实际文件 `Get-FileHash`，说明跨表示比较不证明生成器归一；不要求 canonical LF/schema |
| 汇总/状态/矩阵 | MAJOR 3、MINOR 6、INFO 4；两 run SDK 行 FAIL | 改为 MAJOR 2（D-03/D-08）、MINOR 5（D-01/D-04/D-05/D-06/D-07）、INFO 6（D-02/D-09/D-10–D-13）；SDK 行改为 INFO；§9.1 增加观察/表示说明，二者不再作为负例违例 |

**报告范围复核缺陷计数：** 0 BLOCKER / 1 MAJOR 组（`AUD-AD6-R1-01`）/ 0 MINOR 组。
§9 的 D-01–D-13 是源 CI 审计发现，不是报告返修缺陷计数。

本轮只改了唯一授权报告；没有修改 verifier/workflow/global.json/产品，没有补跑原 CI，
也没有把源 CI 整体改判 PASS。D-03/D-08 等有原件支持的源 CI 缺口、timeout、
dotnetTest/selfcheck/gitpin NOT_RUN 与历史 `39970580589` BLOCKED 均保持不变。
本轮没有新增源 CI 缺陷；D-02/D-09 的实测事实和原件均保留，只纠正“观察→既有契约违例”
的推论及严重度。

## 10. 实际命令、退出与首失败

| 命令（摘要） | native exit | 结果 |
|---|---:|---|
| `git rev-parse HEAD` / `git status` / `git diff --check` | 0 | HEAD `8fd7c6c`，branch 正确，clean，diff-check 0 |
| `git merge-base --is-ancestor 8fd7c6c ad6fa1c` | 0 | accepted 是目标祖先 |
| `git fetch --no-tags origin refs/pull/20/merge:refs/audit/pr20-merge-e6ade375` | 0 | 得到 e6ade，parents/tree 已核 |
| `gh api .../runs/{41297112055,41297107641}` | 0 | 两 run 元数据 |
| `gh api .../runs/{...}/jobs?per_page=100` | 0 | 两 job，9 step success |
| `gh api .../runs/{...}/artifacts?per_page=100` | 0 | 两 artifact digest |
| `curl.exe -sS -L .../artifacts/{13079638714,13080232591}/zip` | 0 | 两 ZIP，SHA 与 server digest 一致 |
| `curl.exe -sS -L .../jobs/{124820148303,124820135330}/logs` | 0 | 25,087 / 24,197 bytes |
| `tar -tf` / `tar -xf` 两 ZIP | 0 | 270/270 files |
| `audit-results.ps1`（最终） | 0 | 2830/2830 每 run；0 fail/skip/notrun |
| `audit-provenance.ps1`（最终） | 0 | 150 inputs CRLF、19 outputs、7/7 DLL |
| `audit-integrity.ps1`（最终） | 0 | 270/270、152/152、0 missing/extra |
| `probe-table-gate.ps1` | 0 | 两个恶意 fixture 均 green（缺陷复现） |
| `make-manifest.ps1` | 0 | 2459 source + 597 all files |
| `git grep ... test-v5-direct-results.ps1 ...` | 1 | 无匹配，证明 CI 无调用 |
| `gh api .../runs/39970580589` | 0 | 历史 failure 元数据 |

**首失败/不完整记录（均为审计脚本，不是产品失败）：**

1. `pwsh` 命令在宿主不可用（宿主是 Windows PowerShell 5.1）；改用 `powershell.exe`。
2. `audit-results.ps1` 首次 `New-Object System.Text.MemoryStream` 类型名错误，exit 1；
   改 `System.IO.MemoryStream` 后 exit 0。
3. `audit-provenance.ps1` 首次把裸路径喂给 `git cat-file --batch`，150/150 显示 missing；
   改为 `ad6fa1c:<path>` 后 150/150 匹配。该次输出未用于结论。
4. `audit-integrity.ps1` 首次在 PowerShell 5.1 下把 22 个 `tmp/VBCSCompiler` 长路径报为
   missing/mismatch；用 `\\?\` 长路径 IO 后 152/152 匹配、0 mismatch。
5. 未发生网络错误；两 ZIP/两 job log 首次下载均 exit 0。未重跑 CI，未重试远端 run。

## 11. 证据路径、manifest 与哈希

- 审计根：`E:/majiang-review/pr20-ad6fa1c-ci-audit-ds-20261101-1844/build/8ad5aaf679e247e4abbd31635233250d/`。
- 原件：`evidence/extracted/pr/`、`evidence/extracted/push/`；ZIP 在 `evidence/zip/`；
  API JSON/job log 在 `evidence/api/`。
- 审计脚本：`audit-scripts/`；独立 JSON：`audit-output/`；manifest：`manifests/`。
- **`manifests/MANIFEST-SUMMARY.json` SHA256：**
  `13AA696262EA91BE7B8718C9E6A426B0AE8A52CC81AFCCF38C87DE2C098A6A07`。
- `MANIFEST-SOURCE-AD6FA1C.csv`：2459 files，389,462,644 bytes，SHA256
  `98182950458EA53EEDC364E2101BA8BC7A5E9C5780D13F6C08B12697464A943E`。
- `MANIFEST-ALL-FILES.csv`：597 files，44,665,056 bytes，SHA256
  `4506148A403D4F37A18F236E750A754D218F71596F8A3C9DBB88B8BE3D2F3831`。
- 关键文件：
  - PR summary `5CD8C30C...`；push summary `C54EB788...`；
  - PR run-info `F278DA00...`；push run-info `00E3D69A...`；
  - PR sdk diag `08D603D6...`；push sdk diag `9501F34B...`；
  - `audit-output/results-audit.json` `0865B397...`；
  - `audit-output/provenance-audit.json` `302BFDE2...`；
  - `audit-output/integrity-audit.json` `E2D139FC...`；
  - `audit-output/gate-probe.json` `C992CA73...`；
  - `audit-output/commands.json` `6DD5AD4A...`。
- 所有 SHA 均可在对应 CSV/JSON 或 `Get-FileHash` 复核；封存清单只做验证，未重算覆盖。

## 12. 最终本地状态与非批准声明

- 本任务唯一允许新增的 tracked 文件：
  `doc/v5-重构/V5-PR20-AD6FA1C-CI-EVIDENCE-AUDIT-20261101.md`。
- 审计代码基线保持 accepted `8fd7c6c`；上一轮报告提交 `d53a7607` 与本轮返修提交都只含
  本报告这一个 tracked 文件。提交后工作树 clean、`git diff --check` 0；本轮报告 commit
  SHA 见交付回复，**该 SHA 不是受测生产代码**。
- accepted base、frozen target、source merge、report commit 四者分开：accepted
  `8fd7c6c`、target `ad6fa1c`、PR merge `e6ade`、report commit 由本地 `HEAD` 给出。
- 本报告不是 G2 验收、不是 C04/C05 批准、不是 PR20 合并审查、不是历史
  `39970580589` 修复证明；不把 direct 2830/2830 写成 dotnetTest 通过，不把单条 durable
  state 写成自然整场终局通过。
- 未 push、未创建/修改 PR、未 `record_merge_request`、未 `approve`、未 merge v5、未部署、
  未改 scheduler/`complete.flag`/lock；未运行 Unity/Player/Android/adb/设备，未运行本地
  PG/.NET 产品测试，未重跑 CI。
- 调用方独立复核后可决定报告是否纳入现有 PR20；G2–G5 仍未批准，V4 仍是生产基线。
