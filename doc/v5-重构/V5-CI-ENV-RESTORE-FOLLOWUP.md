# V5-CI-ENV-RESTORE-FOLLOWUP — env-restore 三态恢复修复（后继任务）

生产 CI 失败（runs 40021452487 / 40021457715，`v5-verify`）的修复与验收记录。
本文件是派单指定的四个可写文件之一，由后继任务独立完成；**不是**对原 wrapper 线程的重做。

## 1. 指派来源与原线程

- 原 CI 真实 PG wrapper 线程：`thread_20261024_015954_9287`（已验收组件）。
- caller 对该线程的同线程返修请求被 bridge 真实拒绝（原样审计，非推测）：

```json
{"content":[{"type":"text","text":"tool call error: tool call failed for `agent-mesh-bridge/request_rework`\n\nCaused by:\n    Mcp error: -32000: [Agent Mesh Bridge] Refusing review action for thread thread_20261024_015954_9287: no active reviewer role is held. A task accepts review decisions only from its current reviewer turn; finish or re-wake the same task instead of acting from a stale reviewer process."}],"isError":true}
```

- 因此本任务为显式后继派单：唯一工作树 `D:/majiang-wt/ci-env-restore`，分支 `wt/v5-ci-env-restore`，起点 commit `32f645ec9b134e9ad261a2df89437416b20b3119`（clean）。不转派、不接管兄弟线程、不改其他工作树、不 push / 不建 PR / 不触发远端 CI（PR19 `feat/v5-1-integration -> v5` 与 merge-back 由 caller 负责）。
- 可写范围仅四文件：`tools/ci/verify-v5-postgres.ps1`、`tools/tests/VerifyV5PostgresCi.Tests.ps1`、`.github/workflows/v5-verify.yml`、本文件。

## 2. 生产 CI 失败证据（只读引用）

构件：`v5-verify-logs-40021452487-1`，运行目录 `20261023-204409-pg-08634aa4`（本地只读副本 `D:/majiang/artifacts/progress-20261024-current/latest-push-artifacts/`）。

`pg/env-restore.jsonl` 原文（4 条全部失败，`restoredSha256` 为空串哈希 E3B0…）：

```
{"variable":"MAHJONG_LOBBY_TEST_POSTGRES","wasSetBeforeRun":false,"restored":false,"restoredSha256":"E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"}
{"variable":"MAHJONG_LOBBY_PG_ROOT","wasSetBeforeRun":false,"restored":false,"restoredSha256":"E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"}
{"variable":"MAHJONG_LOBBY_PG_DATA","wasSetBeforeRun":false,"restored":false,"restoredSha256":"E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"}
{"variable":"MAHJONG_LOBBY_PG_EVIDENCE","wasSetBeforeRun":false,"restored":false,"restoredSha256":"E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"}
```

同目录 `pg/wrapper-summary.json`：`head=32f645ec9b134e9ad261a2df89437416b20b3119`，`branch=feat/v5-1-integration`，precheck/start-postgres/ownership-marker/migrate/cleanup 均 PASS，env-restore 失败 → wrapper exit 1。runner 日志：镜像 `windows-2025-vs2026`（20261021.278），步骤宿主为 `pwsh.exe`（PowerShell 7.6.x / .NET 10）。

## 3. 实测矩阵（本机 exclusive TEMP，双运行时）

证据目录：`%TEMP%\majiang-env-matrix-20261024\`（M0–M11 / M12–M16 / M17–M18 三段脚本，PS 5.1 与 PS 7.6.6 各跑一遍）。关键行原文：

PowerShell 5.1.19041.6456 (Desktop, .NET Framework 4.0.30319)：

```
M12 after set-empty       -> parent-get=<null> child:absent(null)
M13 after set-null        -> parent-get=<null> child:absent(null)
M14 set-item-null         -> provider=ok parent-get=<null> child:absent(null)
```

PowerShell 7.6.6 (Core, .NET 10.0.12)：

```
M12 after set-empty       -> parent-get=<empty-string> child:empty-string(NAME=-style residue or runtime artifact)
M13 after set-null        -> parent-get=<empty-string> child:empty-string(NAME=-style residue or runtime artifact)
M14 set-item-null         -> provider=ok parent-get=<null> child:absent(null)
M15 never-set child view  -> parent-get=<empty-string> child:empty-string(NAME=-style residue or runtime artifact)
```

要点（全部为实测，非文档推断）：

1. **M13（CI 根因）**：.NET 10 下 `[Environment]::SetEnvironmentVariable(name,$null)` 后，块内残留 `NAME=` 空条目——新子进程读到 `''`（exit 102），父进程 `GetEnvironmentVariable` 返回 `''` 而非 `$null`。旧恢复判据 `$null -eq (Get …)` 在该运行时**恒假** → env-restore FAIL。
2. **M14（修复基础）**：`Set-Item -LiteralPath ('Env:\'+name) -Value $null` 在两个运行时都是**真删除**（父进程读 null、子进程读 absent）。
3. **M15**：.NET 10 下 method-delete 会**凭空制造** `NAME=` 残留——即使变量本已缺失。这对测试装置同样是陷阱（见 §5.2）。
4. **M9**（cmd 观察不可用作探针）：自定义块含 `VAR=` 空条目时，cmd `if defined` 返回"未定义"（exit 0）——cmd 看不见空条目，故子进程观察必须用**新子进程的 .NET 读回**（absent→exit 0 / 空串→exit 102 / 非空→exit 101）。
5. Windows 语义下"present-empty 子进程可见"只有在保留 `NAME=` 条目的运行时（.NET 10）才可表示；PS 5.1 的 set-empty 即删除。

矩阵期间发现的度量工具陷阱（与本缺陷同类，已记录）：`function Get-Repr([string]$Value)` 的 `[string]` 形参在绑定时把 `$null` 强转为 `''`，会伪造 null/empty 区分；修正为无类型形参后以 M8 的 `nullEq=` 直读为准。

## 4. wrapper 修复（tools/ci/verify-v5-postgres.ps1）

快照与恢复按三态显式分类，不再把缺失与空串合并：

- `Get-WrapperEnvValueState`：`absent` / `present-empty` / `present-nonempty`（`.DESCRIPTION` 第 4 条同步更新，引用本文件）。
- `Test-WrapperChildEnvState`：spawn 当前宿主可执行的新子进程做 .NET 读回，exit 0/102/101 → `absent`/`empty`/`defined`，20s 超时/异常 → `unknown`。
- `Remove-WrapperProcessEnvVariable`：优先 `Set-Item Env:\` provider 路径（双运行时真删除），异常回退 method 路径；返回所用机制。
- 恢复分支与记录（新增字段 `preCallState/childStateBefore/childStateAfter/mechanism/outcome/note`，原字段 `variable/wasSetBeforeRun/restored/restoredSha256` 全保留）：
  - absent：provider 删除，**子进程探针裁决**——`absent` → `restored-absent=true`，`restoredSha256='(unset)'`；`unknown` → 回退父读 null 判据并记 note；其余 → 失败并记残留哈希。
  - present-empty：method 设空 + 子进程验证——能保留（如 .NET 10）→ `restored-empty-value=true`（sha 为空串哈希）；运行时即删（如 PS 5.1）→ `failed-runtime-cannot-represent-empty=false` 并显式 note（**不冒充成功、不并入 absent**）。
  - present-nonempty：method 设回原值 + 区分大小写 `-ceq` 精确比对 + 子进程 `defined` 观察；sha 记原值哈希。
- 日志仅结构性状态与 SHA-256，绝不写原值；machine/user 持久目标零触碰（`Env:\` 驱动器仅进程作用域）；未新增任何 `Remove-Item` 行（S0 静态约束：Remove-Item 只准指向 staging）。
- `finally`、cleanup 失败传播、verifier exit 逐字传播、exit 1/2 语义、stub17 优先、63 项 ownership/安全负例、已接受 pg 脚本与统一 verifier **一律未改**。

## 5. 自测扩展（tools/tests/VerifyV5PostgresCi.Tests.ps1）

### 5.1 新增 S9（三态环境矩阵，双宿主）

- 进程内：分类断言、探针映射（never-set→absent / set→defined）、absent 恢复（子进程验证、`(unset)`、无值泄漏）、nonempty 精确恢复（sha=哨兵哈希）、present-empty 按运行时能力一致性断言（keepsEmpty↔`restored-empty-value` / !keepsEmpty↔`failed-runtime-cannot-represent-empty`）。
- 双宿主 leg（`-PwshPath`）：独立子脚本在本机另一运行时重复 absent/nonempty/empty 三态并打印 `S9-DUAL-PASS`。
- S9 用合成快照直填三态，装置本身与运行时惯用法解耦（这正是 S6 踩坑后的对照设计）。
- `Invoke-WrapperRun` 改为以**当前宿主可执行**拉起 wrapper 子进程：pwsh 宿主的 selftest 等价复刻 CI 的 pwsh 运行环境（原先写死 `powershell`）。

### 5.2 S6 装置修复（诚实记录：先发现后修复）

第一轮 pwsh 7.6.6 宿主全量 selftest 得 **189 checks / 3 failures**，全部落在 S6 case B/C。根因不在 wrapper 修复，而在 **S6 原装置**：case B/C 用 `[Environment]::SetEnvironmentVariable(n,$null)` 制造"unset 前置态"、用父进程 `$null -eq Get` 断言——在 .NET 10 下（M13/M15）装置实际造出的是 `NAME=` 残留（present-empty），断言目标（真 absent）从未达成。PS 5.1 宿主不受影响（M12/M13 为真删除），故第一轮 189/0。

修复（强度只增不减，三条原断言全保留，新增三条）：

- 装置改用 wrapper 自己的 provider 删除（M14：双运行时父+子双侧真删）实现真 absent。
- case B/C 的"已删除"判定改用 `Test-WrapperChildEnvState` fresh-child 探针（与 wrapper 判定同一标准），并保留父读 null 断言（provider 路径下两运行时均成立，实测 M14）。
- 新增：case B 记录 `outcome=restored-absent`、`childStateAfter=absent` 断言。
- selftest 收尾清理同样改 provider 删除，避免向后续场景子进程遗传 `NAME=` 残留。

## 6. 验证结果（原命令、原始计数、exit）

宿主版本：Windows PowerShell 5.1.19041.6456（Desktop，.NET Framework 4.0.30319）；PowerShell 7.6.6（Core，.NET 10.0.12）。

| # | 命令（原样） | 结果 | exit |
|---|---|---|---|
| 1 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5PostgresCi.Tests.ps1 -PwshPath <TEMP>/pwsh7/pwsh.exe`（S6 修复前） | 189 checks, 0 failures（含 S1 真实 PG 端到端、dual pwsh7.6.6 leg 全过） | 0 |
| 2 | `<TEMP>/pwsh7/pwsh.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyV5PostgresCi.Tests.ps1 -PwshPath C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`（S6 修复前） | **189 checks, 3 failures**（§5.2，由此发现 S6 装置缺陷并修复） | 0 |
| 3 | 同 #1（S6 修复后重跑） | **192 checks, 0 failures**（+3 新断言全过） | 0 |
| 4 | 同 #2（S6 修复后重跑） | **192 checks, 0 failures**（dual PS5.1 leg：keepsEmpty=False→`failed-runtime-cannot-represent-empty` 一致性过） | 0 |

（#1/#2 与 #3/#4 严格串行：共享端口 55460/55461。）

真实 PG 统一入口（**一次**，pwsh 7.6.6，与 CI 同运行时）：

```
<TEMP>/pwsh7/pwsh.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-postgres.ps1 \
  -Configuration Release -Port 55464 -DotnetPath D:/Unity/dotnet/dotnet.exe \
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip
```

- 前置说明（如实）：首次发射因后台 shell 无 `DOTNET_ROOT` 且 PATH 无 dotnet，在**守卫处** `BLOCKED: dotnet.exe not found`，exit 2——**任何 stage 均未开始**，未占端口、未建集群、未产生 LogDir；补 `-DotnetPath` 后为实际唯一一次 case 运行（非对失败 case 的重试）。
- 结果：**OVERALL: PASS**，10 个 wrapper stage 全 PASS，verifier exit 0 逐字传播，overall exit 0。
- 计数（summary.log 原行）：9 project rows **9 PASS / 0 FAIL / 0 BLOCKED / 0 NOT_RUN**；dotnet test 合计 **882 tests / 0 failed / 0 skipped**（Ai 14、Client 132、Client.Wss 80、Contracts 104、Domain 313、LobbyService 89〔真实 PG〕、TableRuntime 150）；solution build PASS；plugin copy PASS（12 程序集，tracked Unity assets 未动）。
- 集群：`C:\Users\sncdc\AppData\Local\Temp\mahjong-p53-pg-ci-20261024-055407-11a7a715`，port 55464（先实测空闲），identity verified，migrate 001_lobby.sql 两次 exit 0，`rootDeleted: true`，ownership owned；zip 为只读缓存且经已接受 start-postgres.ps1 按 pin 校验（pin 见 `dotnet/scripts/pg-lobby-verify/pg-common.ps1:17`，与实测 `ebb3b6af…` 一致）。
- **env-restore.jsonl（本次运行，修复生效直证）**：4 条记录全部 `wasSetBeforeRun=false, restored=true, restoredSha256="(unset)", preCallState=absent, childStateAfter=absent, mechanism=provider-set-item-null, outcome=restored-absent`——与 §2 的 CI 失败形态（`restored=false` + E3B0）逐项反转。
- LogDir：`artifacts/verify-v5/20261024-055407-pg-02beca15/`（untracked，留树内供独立验收；`pg/evidence-sha256.txt` 为其内部清单）。

## 7. 资源与哈希清单

改动文件（工作树终态 SHA-256 / git blob）：

| 文件 | SHA-256 |
|---|---|
| `tools/ci/verify-v5-postgres.ps1` | `38f7e1cdc887196f0198c17349809a379f6f901a2cb407caa1de3e1f5a7999ba` |
| `tools/tests/VerifyV5PostgresCi.Tests.ps1` | `faee4722909fd3827f4841f63a45945aef2c1df02026f584a60c6f5ac6d4b0d6` |
| `.github/workflows/v5-verify.yml` | `4ff61e452edc912ddbb26d64ec448306f6512dfc328e0a167ff5341e46ef8fb0` |

workflow 仅在 run 步骤上方追加注释（说明 `shell: pwsh` = PS7/.NET 10 语义与 wrapper 的运行时自适应设计），无功能改动。

外部资源（记录版本/来源/哈希）：

- PowerShell 7.6.6 win-x64 portable：官方 GitHub release zip，SHA-256 `02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860`，与官方 `hashes.sha256`（其文件哈希 `388b105a0f329ef7ce9739166ca8e86d3fee6d31698137099e0558c9eb63c9f5`，UTF-16LE）逐字比对一致；解压至独占 `%TEMP%\majiang-env-matrix-20261024\pwsh7\`，未机器安装、未改全局 PATH。
- PostgreSQL 16.10 zip（只读使用）：`D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip`，SHA-256 `ebb3b6af4fa69dea9951b66855bc4d42dc04e56ccb9aa7024ce3c58bd89d6b0c`。

矩阵/自测/运行日志（独占 `%TEMP%\majiang-env-matrix-20261024\`）：

| 文件 | SHA-256 |
|---|---|
| `ps51-matrix.log` | `a49b16757f93a38bb693c4230a47fbab5a835184943a0d40ddd4899945a957e8` |
| `pwsh766-matrix.log` | `30fb45cc0ea55aaf39999d8c353c6d43bfd77cc1aaf690f6e485311a781f2e5f` |
| `ps51-matrix2.log` | `705e97fdc5e78dc0e5089ee2ba7216465edf8b1720785393ae69c265af1632a9` |
| `pwsh766-matrix2.log` | `f2df25e213734e3f4bb5a3ed6dd81f43e4369ca098bfeee9c4d5f08c648dc075` |
| `ps51-matrix3.log` | `6ecbd595838a62b4efb3beaceb7554941fc15a6c735e1ff83f4d926ad14ca026` |
| `pwsh766-matrix3.log` | `bda4459a2ed3eb03e9e5c75f0d4cbba7ba9a0e3de93540b398992c7c9a4bd916` |
| `selftest-ps51.log`（终轮 #3） | `7f5f643a3d64fe595b9a6d17be96e3c4900421731275150f15ec4c0f82fe1826` |
| `selftest-pwsh766.log`（终轮 #4） | `d75de12fc2450b26d6e33897cbf6a8666df63ac6f5c98ae466e986cf74fba81d` |
| `smokeconsole-ps51.log` | `bef634e984c01f839248dadda182724b7273d31877a789cbe336e7bf6202f4dc` |
| `smoke/console-pwsh766.log` | `8863bcb73f74c4b6fc9a9fcf1d7df9d82eac1da03a794e51d2943b23227296ab` |
| `smoke/logs-ps51/pg/env-restore.jsonl` | `ddeca8ed65a73429198593ebd2cf3cc2cba1bb628a3d39b82a219dd9ef1914fe` |
| `smoke/logs-pwsh766/pg/env-restore.jsonl` | `1fad70863ea7254e871c59e68d96a05c50514873b28103e40aa1199de20793e3` |
| `realpg-run-console.log` | `34294f377fae8798013024b19dff8f5dc3352f53a84771187c5dfea896795fda` |

CI 证据引用（只读）：`D:/majiang/artifacts/progress-20261024-current/latest-push-artifacts/v5-verify-logs-40021452487-1/20261023-204409-pg-08634aa4/pg/{env-restore.jsonl,wrapper-summary.json}`、同目录 `latest-push-job.log`（UTF-16LE，含 pwsh 路径与镜像版本）。

## 8. 边界与未测事项（防越界声明）

- 本仓库唯一生产架构为 V4；V5 的 G1/G5 门未过。本文件只主张：**本工作树内** wrapper/selftest/workflow 三文件的行为与上述证据一致。
- 未主张平台（Unity/IL2CPP/Android/iOS）、真机、真实网络已测；workflow 覆盖面注释原样保留该边界。
- 未 push、未建 PR、未触发远端 CI；未改 machine/user 持久环境；未改已接受 pg 脚本（start/restart/migrate/cleanup/pg-common）与统一 verifier（其 sha 见 run-info.txt：`FA8D539BB6E036A7FD002011F001F5D5FDB27BA8B20CD5A9089098E87F3CE307`）。
- CI runner（GitHub windows-2025-vs2026 镜像）上的最终等价验证由 caller 的 PR19 链路完成；本文件的双宿主 192/0 与真实 PG 一次全绿为本地等价证据。
