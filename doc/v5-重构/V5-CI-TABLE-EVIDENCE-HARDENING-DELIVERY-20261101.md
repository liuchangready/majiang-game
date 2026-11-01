# V5 CI table 原生证据与 XML 门禁修复交付（2026-11-01）

状态：本任务范围实现与证据交付；**不是** G2 验收、不是 PR20 合并审查、不是远端 CI 新头认证。
V4 仍是唯一生产基线；G2–G5 未批准。

> **R2 说明（2026-11-01）**：Caller R1 复核（`REVIEW.txt` SHA256
> `568B8B6A6EDF7AFE5D7C91A92D83F5B473808BF82E4A7AB1A6A43A4B85119858`，
> manifest `90A1AC91632447A1006C9FDCC881C83DE738AC99FF34B6A9ED750A23ADE85106`，
> 2098 项/0 mismatch）给出 0 BLOCKER / 4 MAJOR 返修。第 12 节是 R2 当前结果，
> 在计数、哈希与证据路径上取代第 1–11 节的 R1 历史快照；R1 原件与其首失败均保留。
> 本报告不重开历史 CI 报告或其他组件。
>
> **R3 说明（2026-11-01）**：Caller R2 复核
> （`REVIEW.txt` SHA256 `3CD7A807B8D1BC1A9F9D96F4373620EB0CE68CD4874BF94A37853AFF5B39F4C3`，
> manifest `836C9DF14E24702F3E1047A6543119D839FD1EA0713E672B4AE4902D835226B4`，
> 2034 项/0 mismatch）确认 R1-01/R1-04 关闭，仅保留 R1-02/R1-03 两组残余。
> 第 13 节是 R3 当前结果，在计数、哈希与证据路径上取代第 12 节；R1/R2 原件、
> 首失败与已关闭结论均保留，不重开 01/04。

## 0. 身份、范围与结论

- 工作树：`E:/majiang-review/ci-table-evidence-ds-20261101-2048`
- 分支：`wt/v5-ci-table-evidence-ds-20261101-2048`
- accepted base：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`
- frozen candidate：`06e892cf41a48c751370143c490699a26439a637`
- 本任务 code commit 与报告 SHA-256：见交付回复（报告不嵌入自身哈希）。
- 只修改/新增以下 6 个授权文件，未改业务、协议、Domain、Runtime、LocalHost、Unity、`global.json`、scheduler、`complete.flag`、其他线程或锁：

| 文件 | 变更 |
|---|---|
| `tools/ci/verify-v5-table-postgres.ps1` | 严格 table XML/原生证据/PG restore/cleanup/阶段 schema |
| `tools/ci/test-v5-native-exit.ps1` | 原生 exit/timeout/启动失败/安静成功/引号/脱敏矩阵（10 例） |
| `tools/ci/test-v5-table-evidence.ps1` | 新增；XML + ownership/preflight/cleanup 负例矩阵（60 例） |
| `tools/tests/VerifyV5TablePostgresStartup.Tests.ps1` | 最小适配 AST 抽取依赖；S7 进程匹配兼容 `/` 与 `\` |
| `.github/workflows/v5-verify.yml` | 显式执行 direct/dotnetTest/table 门禁自检并上传独占证据 |
| `doc/v5-重构/V5-CI-TABLE-EVIDENCE-HARDENING-DELIVERY-20261101.md` | 本报告 |

源 CI 审计 `thread_20261101_184543_a20f` 已由 caller 接受、completed、MR synced 至 `06e892cf`；本任务只处理其报告的 D-01/D-03/D-04/D-05/D-13 工具缺口，**不重审、不重开、不接管其历史线程**。历史 `39970580589` 仍为 BLOCKED；本报告不把它写成已修复。

## 1. 缺陷闭环总表

| ID | 严重度 | 原状态 | 本轮状态 | 位置 |
|---|---|---|---|---|
| D-01 | MINOR | table XML gate 只看 assembly 汇总；assembly-only / duplicate-id 均 green | **FIXED** | `verify-v5-table-postgres.ps1` `Test-TableResultEvidence`；调用点 tests/tests-targeted |
| D-03 | MAJOR | helper 无 command/native exit；dump/restore 空日志；无空库前置；无 owner marker/最终 stop 日志 | **FIXED（本表 wrapper 范围）** | `Invoke-Native`、`Invoke-WriterAndKill`、`Stop-And-RemoveCluster`、dump/restore 阶段 |
| D-04 | MINOR | timeout 在合并日志/metadata 之前 throw | **FIXED** | `Invoke-Native` timeout 分支；`test-v5-native-exit.ps1` |
| D-05 | MINOR | direct 19 例、dotnetTest 矩阵未在 CI 执行 | **FIXED（已加 CI step；本地实跑）** | `.github/workflows/v5-verify.yml` |
| D-13 | INFO | crash-writer stage `exitCode` 为字符串 `KILLED` | **FIXED** | `Add-Stage` + crash-writer 调用点 |
| D-02 | INFO | SDK/runtime patch 差异 | 保留 INFO，未改 `global.json` | 非本轮必修 |
| D-06 | MINOR | dotnetTest 全量模式未执行 | **NOT_RUN 保留** | 本任务只跑门禁自检，不跑第二个完整 suite |
| D-07 | MINOR | `git pins` 历史 blob 未校验 | **NOT_RUN 保留** | 未扩写为已验证 |
| D-08 | MAJOR(历史) | `39970580589` 缺 framework 原日志 | **BLOCKED 保留** | 未重跑旧 CI、未宣布根因已修 |
| D-09 | INFO | provenance 是 checkout 原字节，非 Git canonical LF | 保留 INFO | 未改生成器/合同 |
| D-10 | INFO | 1 对 exact-case 重名 display name，test id 唯一 | 保留 INFO | 未改测试逻辑 |
| D-11 | INFO | Actions Node.js 20 deprecation warning | 保留 INFO | 未改 action 版本 |
| D-12 | INFO | PR run 测合成 merge，不是字面 head | 保留 INFO | 未重跑 CI |

**本任务范围内 0 个未修复 BLOCKER；D-03 的 known MAJOR 组已闭环。** 残余 D-06/D-07/D-08 是明确不在本轮授权的模式/历史项，不是被弱化或伪造为 PASS。

## 2. D-01：严格 table 结果验证

- **原复现（只读审计）**：`probe-table-gate.ps1` 对 assembly-only（`total=42,passed=42`，0 个 `<test>`）和 duplicate-id（两个相同 `test@id`）返回 `gateGreen=true`；实际 `tests.xml`/`tests-targeted.xml` 也 true。
- **原证据**：`audit-output/gate-probe.json` SHA256 `C992CA73C25874AB2FBA69ACC64BE610398F0CF146F094E71B6865529883E7EB`；probe SHA256 `0A9B80BF55BE6EC2B30606DE1407A1C47B59A8BF01DBE5BB63D4328EFA9B07DD`。
- **实际（修复前）**：只检查 assembly 六个汇总属性；节点数、collection 汇总、唯一 id、assembly 身份均不检查；零 test 的 assembly-only 可通过。
- **期望**：只接受实际 xUnit.net v3 结构：单 assembly、正确身份/framework、非空真实 test 节点、唯一 id、collection/assembly/test 计数一致、全 Pass；native 非零仍拒绝。
- **修复**：`Test-TableResultEvidence` 替代 `Get-XmlCounts`/`Test-XmlCountsPass`，同时承担结构、身份/freshness/LogDir、native exit 和 green 决策；生产 `tests`/`tests-targeted` 调用与 `test-v5-table-evidence.ps1` 调用的是**同一函数**。使用禁用 DTD 的 `XmlReader`，拒绝 DOCTYPE/XXE、多 assembly、collection 外 test、assembly 下额外节点、未知 result、缺/非法/负数属性。
- **原 7 个 selfcheck 保留语义**：zero-tests、failed、errors、skipped、not-run、passed-mismatch、missing-not-run；改为真实 test 节点形状，逐个调用同一决策函数；不吞异常、不以异常当成功。生产日志仍为 `negative XML cases=7`。
- **负例矩阵（tool fixture，独占）**：60/60 通过。覆盖 assembly-only 42/42、duplicate-id、wrong assembly、collection total/passed mismatch、assembly total/passed mismatch、missing/非法属性、负数、zero tests、failed/skipped/not-run/errors、error node 与 errors 属性交叉、未知 result、多 assembly、stray test、额外节点、XXE、stale XML、LogDir 外 XML、native exit 1/null + green XML。
- **正例**：3/3、13/13 fixture 接受；最终真实 PG 运行 `tests.xml` 42/42、`tests-targeted.xml` 13/13 由同一函数接受。
- **验收断言**：assembly-only/duplicate-id 必须 false；实际 42/13 true；负例均由 `Test-TableResultEvidence` 拒绝；真实 XML 有 42/13 个唯一 test id、collection/assembly 计数一致。
- **新证据**：`E:/majiang-review/ci-table-evidence-ds-20261101-2048/artifacts/table-evidence-selftest-2f68d3b4fc7c466984e5e92f8fe7e038/results.json` SHA256 `1BF207C9B75A962F1550746D2B21487E5E82FA1A23DF829785215B32EFD4E856`。

## 3. D-03：原生 command/argv/exit/时间/PID/日志与 PG restore/cleanup 闭包

### 3.1 `Invoke-Native` 结构化原件

- **原复现**：`pg-dump.log`/`pg-restore.log`/`create-restore-db.log`/`drop-restore-db.log`/`download.log`/`crash-writer.log` 均 5 bytes（UTF-8 BOM+CRLF）；无 command/native exit；table artifact 无 `pg-owner.json`，`pg/postgres.log` 无最终 shutdown 行。
- **原证据**：上述日志 SHA256 均为 `F01A374E9C81E3DB89B3A42940C4D6A5447684986A1296E42BF13F196EED6295`；table summary PR `D4AD9718...` / push `6640CD81...`。
- **修复**：每个 `Invoke-Native` 现在写：
  - 真实分离 `<log>.stdout`、`<log>.stderr`，以及逐字节拼接的 `<log>`；
  - `<log>.meta.json` 与 `<log>.meta.txt`：exe、exe SHA256、cwd、保留边界的 `argv` 数组、commandLine、UTC start/end、duration、PID、`started`、真实 `nativeExit`（int 或 null+原因）、`timedOut`、`killed`、`stillRunning`、timeout、三个日志的 path/bytes/SHA256、脱敏事实、error；
  - 成功/非零/超时/启动失败/exit unavailable 路径都先落盘再返回/throw；不使用 `[int]$null`，null 明确记录为 null。
  - Windows argv 引号按 CommandLineToArgvW 规则显式生成；含空格/内嵌引号/尾随反斜杠的参数完整保留。
  - 真实 argv 用于执行；仅 metadata 的 argv/commandLine 脱敏。`-SensitiveValues` 命中的值、`Password=`/`Pwd=` 字段替换为 `<redacted>`，记录 `redaction.applied`、计数、`secretValuesRecorded=false`；不记录进程环境。
  - 超时只 `Kill()` 本次启动的进程（不杀进程树），等待退出/flush 后封存，再 throw。
  - 安静 helper 的 0 字节输出合法；不因 stdout 空误报失败。
  - `tar.exe` 解压也纳入同一 `Invoke-Native` 证据合同。
- **`Invoke-WriterAndKill`**：同样的 `.meta.json/.meta.txt`，另含 marker、marker SHA256、killReason、killRecordPath；post-commit 强杀写整数 `exitCode` 的 `.kill.json`（`forced=true`、`reason=post-commit-marker`）。
- **新证据（最终真实运行）**：`build/real-pg-20261101-132250/table/tests.log.meta.json`、`pg-dump.log.meta.json`、`pg-restore.log.meta.json`、`create-restore-db.log.meta.json`、`drop-restore-db.log.meta.json`、`restore-preflight-empty.log.meta.json`、`pg-extract.log.meta.json`、`crash-writer.log.meta.json`；每项均 `started=true`、`nativeExit=0`（crash-writer 为 `-1` 且 `killed=true`），argv 保留边界。
- **验收断言**：每个 helper 有可解析 command/native exit；dump/restore 空 stdout 仍 PASS 但 metadata 有原生 exit；timeout 有合并日志；crash writer 的 kill JSON 与 stage 的整数 exit 一致。

### 3.2 restore 前空库与身份

- **原缺口**：restore 前只有代码顺序 `dropdb`→`createdb`，没有独立 `SELECT count(*)` 原件。
- **修复**：createdb 后、pg_restore 前执行并封存：
  ```sql
  SELECT current_database()::text || '|' || COALESCE(host(inet_server_addr()), '') || '|' ||
         COALESCE(inet_server_port()::text, '') || '|' ||
         (SELECT count(*) FROM information_schema.schemata WHERE schema_name='table_store')::text || '|' ||
         (SELECT count(*) FROM information_schema.tables WHERE table_schema='table_store')::text || '|' ||
         (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='table_store')::text
  ```
  决策函数 `Test-RestorePreflightEmpty` 要求恰有一行、6 字段、database/host/port 与本次实际连接一致、三个对象计数全为 0；任一失败 fail-closed，不执行 restore。真实结果 `mahjong_p52_restore|127.0.0.1|55592|0|0|0`。
- **身份绑定**：`cluster-identity.json` 记录 server 16.10、实际 `data_directory`、expected data_directory、host/port/database、postmaster start、marker path/SHA、连接身份 `mahjong_p52_test|127.0.0.1|55592`；`dataDirectoryMatches=true`。
- **dump 产物**：`pg-dump-artifact.json` 记录 `PGDMP` magic、custom dump 类型、字节数、SHA256、native exit、log/meta 路径。最终 dump 44,710 bytes、SHA256 `43A2146E21664C014C7362D1499885244BFB85FFBA6244AFCE04A882A024024A`，magic 校验通过。
- **restore 闭包**：`backup-restore-evidence.json` 串联 dump、pre-restore 空库、drop/create、pg_restore、grant、post-restore 行数、reader 结果和 reader 的 SHA；最终 events=2、receipts=1、outbox=1、tables=13，reader `eventCount=2`、`recoveryOwnerEpoch=3`、`terminal=true`、outbox SHA 与 marker 一致；closure 五项全 true。
- **验收断言**：`restore-preflight.json` 有 query + native + raw output + decision；`backup-restore-evidence.json` 的 `closure.emptyBeforeRestore/dumpNativeExitZero/restoreNativeExitZero/eventCountMatchesTableRows/outboxShaMatchesMarker` 全 true。

### 3.3 ownership/stop/cleanup 原件

- **修复**：清理严格按以下顺序：
  1. `Assert-OwnedCluster`（owner/root/port/database，原有强检查）；
  2. 原样复制本次 `pg-owner.json` 到 LogDir，记录 SHA；
  3. 绑定 live `data_directory` 与 expected；记录 pre-stop 进程数/PID；
  4. `pg_ctl stop -m fast`，记录 native exit + `pg-cleanup-stop.log`；
  5. post-stop 进程数/PID 必须为 0；
  6. 保存最终 `pg/postgres.log`/`pg_ctl` 日志；
  7. `Test-CleanupRootContained` 严格限制在 `RepoRoot/artifacts` 下（拒绝根本身、`artifacts-evil`、外部路径）；
  8. 再校验 marker 后，长路径安全删除本 root；
  9. 写 `cleanup-evidence.json`（rootDeleted/rootExistsAfter/error 等）。
- 进程匹配同时接受 `\` 与 `/` 的 data_directory 表示，避免真实 postmaster 被 `IndexOf` 漏掉。最终 pre-stop 为 1 个 PID `55948`，post-stop 0；`rootDeleted=true`、`rootExistsAfter=false`。
- 不删除 foreign 目录，不附着/停止已有 PG；S3/S6/S8 的真实 listener/foreign marker 哨兵均保留。拷贝 marker 仅作证据，**不能替代** `Assert-OwnedCluster`。
- **验收断言**：最终 run 的 `pg-owner.json` SHA `F0444D36115028136BD7E940DA15E027D3C71F46E40695B3E19DE9DE015A9390` 与 cleanup evidence 一致；`pg/postgres.log` 末行 `database system is shut down`；root 删除后无 `postgres.exe`、无 55592 listener。

## 4. D-04：timeout 合并日志

- **原复现**：`native-exit-*/results.json` 的 timeout Pass=true，但 `timeout.log` 不存在，只有 `.stdout`/`.stderr` 15 bytes。
- **修复**：timeout 分支先 `Kill()` 本次进程、等待退出、读取两个真实 stream、写 `<log>` 合并日志和 metadata（`timedOut=true`、`killed=true`、真实/PID/null exit 原因、stillRunning），再 throw。
- **验收**：`test-v5-native-exit.ps1` 10/10 通过；timeout case 断言 `timeout.log` 存在、同时含 `stdout code=0`/`stderr code=0`、metadata `timedOut=true`/`killed=true`/`stillRunning=false`；仍以 `Command timed out*` throw。
- **证据**：`artifacts/native-exit-367858ad95f249b98f827da6ae575e8e/results.json` SHA256 `AF9CF599D195C493F418DFBE4B8BE25A52F9F37AC44F3B8FF1F5E596F84AFC53`。

## 5. D-05：CI 显式执行门禁自检

- **原复现**：`git grep -n -e test-v5-direct-results.ps1 -e test-v5-dotnettest-results.ps1 <head> -- .github tools` exit 1；job log 无调用。
- **修复**：workflow 在 unified verification 前新增三个 `shell: powershell` step：
  - `./tools/ci/test-v5-direct-results.ps1`
  - `./tools/ci/test-v5-dotnettest-results.ps1`
  - `./tools/ci/test-v5-table-evidence.ps1`
  任一非零即 job 失败；upload-artifact 增加 `direct-results-selftest-*`、`dotnettest-results-selftest-*`、`table-evidence-selftest-*`，native timeout 原件继续随 `native-exit-*` 上传。
- **local 实测**：direct 19/19、dotnetTest 48/48、table 60/60、native 10/10；全部 exit 0。
- **边界**：这不等于 dotnetTest 全量模式已执行；完整 `dotnet test` 仍 NOT_RUN（D-06）。

## 6. D-13：crash-writer 阶段 schema

- **原实际**：`wrapper-summary.json` 的 `stages[crash-writer].exitCode` 为字符串 `"KILLED"`，真实 kill JSON 为整数 `-1`。
- **修复**：`Add-Stage` 增加独立 `killed` 字段；crash-writer 传真实整数 `$writerResult.exitCode` 和 `-Killed $true`。最终：stage `exitCode=-1`（Int32）、`killed=true`；kill JSON `exitCode=-1`、`forced=true`、`reason=post-commit-marker`。
- **验收**：解析器可按整数/`null` 读取；kill 事实未被改写。

## 7. 实际执行、计数与首失败

### 7.1 工具/门禁自检（PowerShell 5.1）

| 命令 | exit | 结果 |
|---|---:|---|
| PS 5.1 parser（6 个脚本） | 0 | 全部 `PARSE-OK` |
| `tools/ci/test-v5-native-exit.ps1` | 0 | **10/10** |
| `tools/ci/test-v5-table-evidence.ps1` | 0 | **60/60** |
| `tools/ci/test-v5-direct-results.ps1` | 0 | **19/19** |
| `tools/ci/test-v5-dotnettest-results.ps1` | 0 | **48/48**（含 2 个 live dotnet-test probe） |
| `tools/tests/VerifyV5TablePostgresStartup.Tests.ps1` | 0 | **149/149**，0 skip/0 not-run |

### 7.2 真实 PostgreSQL 16.10 table leg（最终 frozen code）

命令（PowerShell 5.1；只 table leg，不带 `-RunUnifiedVerification`）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath 'D:\Unity\dotnet\dotnet.exe' `
  -Root 'E:\majiang-review\ci-table-evidence-ds-20261101-2048\artifacts\p52-pg-cluster-20261101-132250' `
  -LogDir 'E:\majiang-review\ci-table-evidence-ds-20261101-2048\build\real-pg-20261101-132250\table' `
  -PgZipPath 'D:\majiang-pg-tmp\cache\postgresql-16.10-1-windows-x64-binaries.zip'
```

- exit `0`，console `P52-TABLE-POSTGRES-PASS`；SDK `8.0.426`；port `55592`（bounded default 首候选 Bindable）；PostgreSQL `16.10`；loopback。
- 真实 full table suite `42/42`（42 test nodes、1 collection，0 fail/error/skip/not-run）。
- targeted `PostgresTableStoreTests` `13/13`（13 nodes、1 collection）。
- crash writer：退出 `-1`、`forced=true`、state=1 seq=2；immediate restart 有 3 行 recovery；reader `terminal=true`、`receipt=Applied`、outbox SHA `c0b108923986...`。
- dump→preflight 空库 `0|0|0`→restore→reader：events=2、receipts=1、outbox=1、tables=13；reader eventCount=2、ownerEpoch=3、outbox SHA 与 marker 一致。
- cleanup：stop exit 0、pre-stop PID 55948、post-stop 0、rootDeleted=true、rootExistsAfter=false。
- 环境恢复：`environmentRestored=true`，所有 captured 变量 restored，`payloadKeyRecorded=false`。

**首失败（保留，不隐藏）**：

1. 首轮真实 run `build/real-pg-20261101-130443/table` 在 cluster identity 处失败：`inet_server_addr()` 文本为 `127.0.0.1/32`，与期望 `127.0.0.1` 不符；同时 Root 放在 `build\` 下，原有 cleanup guard 只允许 `artifacts\`，清理拒绝删除（cluster 已停止，root 已用长路径安全方式手动删除）。修复：使用 `host(inet_server_addr())`，最终 run 的 Root 放在 `artifacts\`。
2. 首轮 startup 回归 S7 因 `Get-PgScalar` 对真正 0-byte 结果调用 `.Trim()` 失败（旧实现有 BOM 所以未暴露）；修复为 shared-read + null-safe。
3. 首轮 startup S8 cleanup 在 PostgreSQL zip 的深 `pgAdmin` 路径上触发 MAX_PATH；修复为 `\\?\` 长路径枚举删除。最终 startup 149/149。

这些都是本任务工具迭代的首失败，不是产品/业务失败；最终证据使用修复后的 frozen code。首轮失败日志保留在对应 `build/` 子目录中。

### 7.3 真实 PG 证据与 SHA

根：`E:/majiang-review/ci-table-evidence-ds-20261101-2048/build/real-pg-20261101-132250/table/`

| 文件 | 字节 | SHA256 |
|---|---:|---|
| `wrapper-summary.json` | 12,492 | `93533B418E3BA9BCD03F1B9199C9BE6712B4FE89A8A2F46F48A419606E541EDC` |
| `manifest.sha256.json` | 34,896 | `FD35C4E389FBD689507249450A0387E69D71A1327209B7B53859C3A8838B0259` |
| `tests-evidence.json` | 1,652 | `96F5BA63B22A60EFB181E0CBB7A96AC7142239624CF56963AABCE0B686456ED8` |
| `tests-targeted-evidence.json` | 1,699 | `7F69DAD4E0EFDBAAA14D131C1D2CA8345309A3B419EC4E5DFE5D87E104F98DDB` |
| `backup-restore-evidence.json` | 7,020 | `C789EB63EC1006D7FC7C552A1FFACD812983546BAE784EBB9389BE62F55B9B38` |
| `cleanup-evidence.json` | 1,408 | `453BDEA6D35B91B480A12366A972DCFF8F2C3D5B18F39B14CB275EB943F71FEE` |
| `pg-dump-artifact.json` | 717 | `5D1F0727214F37F4B3DD4E3BB19D281936AE8A842DD54EE5B8BA1A488C24DD61` |
| `restore-preflight.json` | 2,210 | `A269B75C1F7F242CF8CB7711C4D7AED9B2069F99F64373E4EA29A440115B7492` |
| `pg-owner.json` | 267 | `F0444D36115028136BD7E940DA15E027D3C71F46E40695B3E19DE9DE015A9390` |
| `crash-writer.log.kill.json` | 390 | `358FD8E9C609D4F8DF9FCEBAE1EADB5C82DAAD39254476AC545F54FEB3A929F9` |
| `pg-cleanup-stop.log` | 51 | `FD9D5642E2BD6859D384EF377F18FD80284DA84F0DDF6B060BBC0F5961D66648` |
| `tests.xml` | 26,474 | `FDAFE474D07A4C5373B30FE0EDE10B8F8B966E0E510A568BD078332F493F9C7E` |
| `tests-targeted.xml` | 8,747 | `EE64BFBC190CF122915FC28FB612FC9B72242ECD8723F32B8FF482824A38B5FE` |
| `table_store.dump` | 44,710 | `43A2146E21664C014C7362D1499885244BFB85FFBA6244AFCE04A882A024024A` |

- `manifest.sha256.json` 对 195 个文件逐项复算：0 missing / 0 mismatch。
- startup 最终证据：`build/ci-pg-startup-selftest/20261101-132258-9894a1d3/`；`selftest-summary.json` SHA `AC367D535894D2D8CB23F3A53869922F01529B0608CE5CCACC5F003A639F53D1`，manifest 1,807 项 0 mismatch。

## 8. Fixture 与真实 PG 分列

| 类别 | 证据 | 结论 |
|---|---|---|
| table XML/ownership/preflight/cleanup fixture | `artifacts/table-evidence-selftest-2f68d3b4fc7c466984e5e92f8fe7e038` | 60/60 TOOL_FIXTURE |
| native exit/timeout/quiet/引号/脱敏 fixture | `artifacts/native-exit-367858ad95f249b98f827da6ae575e8e` | 10/10 TOOL_FIXTURE |
| direct XML gate fixture | `artifacts/direct-results-selftest-f8a2c9eefb274208987bd7705049f7dd` | 19/19 TOOL_FIXTURE |
| dotnetTest gate fixture + 2 live probes | `artifacts/dotnettest-results-selftest-e14acb685936467eaa9f00817c8e1c5f` | 48/48；live probe 是真实小项目，不是全量 suite |
| 真实 PostgreSQL 16.10 table leg | `build/real-pg-20261101-132250/table` | 42/42 + 13/13 + crash/restore，REAL PG |
| startup/port/env/ownership + S7/S8/S9 real PG | `build/ci-pg-startup-selftest/20261101-132258-9894a1d3` | 149/149，REAL PG |

**不得把 fixture 计数写成真实 PG 全量，也不得把 table 42/13 子集写成 14 工程全量。**

## 9. 文件 SHA（本任务源码/配置）

| 文件 | SHA256 |
|---|---|
| `.github/workflows/v5-verify.yml` | `503779B2EBED98CB56C394AC0F2AF26F96A99C65890012E80292C63D1476E173` |
| `tools/ci/verify-v5-table-postgres.ps1` | `69E60FD0CF55EB3C570B1414B9D8AFB56F7BF113356E5D961950E4D1CA9680AD` |
| `tools/ci/test-v5-native-exit.ps1` | `B980710EB077A7762A8C74F969A8252A051266A3FA649891BEAA1B28D98486AE` |
| `tools/ci/test-v5-table-evidence.ps1` | `B1369A2E9B5D328A1556F53B7C961233956E698CA3C8A87B4F2D0D1554DAA888` |
| `tools/tests/VerifyV5TablePostgresStartup.Tests.ps1` | `271B0B01072DFCF43140B6D43B17BA9D4F4FCB6DBF56529FED980BC94297FB8B` |
| 本报告 | 见交付回复（不嵌入自身哈希） |

## 10. 残余与明确未覆盖

1. **dotnetTest 全量模式 NOT_RUN**：本轮只执行门禁自检和 2 个 live 小 probe；没有第二个完整 `dotnet test` suite，也没有 14 工程全量重跑。不得把 48/48 自检写成 dotnetTest 全量通过。
2. **`git pins` 历史冻结 blob NOT_RUN**：未 fetch/比对历史 blob。
3. **历史 `39970580589` 仍 BLOCKED**：缺 framework 原日志；没有重跑旧 CI，也没有用新绿色宣布根因修复。
4. **远端 CI 未触发/未重跑**：本任务不 push、不建/改 PR、不 record MR、不 approve、不 merge v5、不部署；不能宣称远端新头或 G2 通过。
5. **D-02/D-09 为 INFO**：SDK/runtime patch 差异和 checkout 原字节表示保留事实；未改 `global.json`、未强制 pin、未归一化 provenance。
6. **平台**：只验证 Windows PowerShell 5.1；非 Windows native/超时行为、Unity/Player/Android/设备、lobby/unified leg 不在本轮范围。
7. **单条 durable state**：crash/restore 的 events=2/receipts=1/outbox=1 是一条 durable table 状态，不是自然整场终局或 G2 结算验收。
8. **tool fixture 的边界**：fixture 只证明门禁决策；真实 PostgreSQL 正例由 `build/real-pg-20261101-132250` 单独提供。

## 11. 提交/工作树约束

- 提交仅包含第 0 节 6 个授权文件；生成物（`artifacts/`、`build/`、NuGet cache、PG zip 副本、dump、日志）全部 ignored，不提交。
- 提交前逐文件检查暂存区、`git diff --cached --check`、`git diff --check`；提交后 tracked clean。
- 未修改任何业务/协议/Domain/Runtime/LocalHost/Unity 文件；未触及其他线程、scheduler、`complete.flag` 或锁。

## 12. R2 返修：caller R1 四组 MAJOR 闭环

Caller 复核原件：`E:/majiang-review/caller-table-evidence-r1-2f61b4c7/build/caller-review/REVIEW.txt`
（SHA256 `568B8B6A6EDF7AFE5D7C91A92D83F5B473808BF82E4A7AB1A6A43A4B85119858`），
manifest `review-manifest.sha256.json`
（SHA256 `90A1AC91632447A1006C9FDCC881C83DE738AC99FF34B6A9ED750A23ADE85106`，
2098 项/0 mismatch）。本轮在**同一 thread/session/工作树/pinned model**、同样 6 个
expectedFiles 内完成；未新建任务、未转派、未接管历史人工裁决。

### 12.1 逐项修复映射

| Caller ID | 严重度 | 根因 | R2 修复 | 验收 |
|---|---|---|---|---|
| CI-EVID-R1-01 | MAJOR | metadata 显式写 UTF-8 no-BOM，Windows PowerShell 5.1 `Get-Content -Raw` 默认 ANSI；startup S1 只认英语 detail | 所有 metadata/JSON 读取显式 `-Encoding UTF8`；保留原始 console 日志默认编码；S1 改为 locale-neutral 非空真实 OS detail + typed `AccessDenied`/`10013`；新增确定性非 ASCII metadata 用例 | native 21/21；startup 149/149；非 ASCII error 逐字符保留 |
| CI-EVID-R1-02 | MAJOR | `Password/Pwd` 正则停在引号内第一个 `;`，tail 泄漏 | `Get-RedactedArgumentList` 改为引号/转义/分号感知扫描；整段 quoted/unquoted 值替换为 `<redacted>`；记录 `credentialFieldsRedacted`；执行 argv 仍用原始值，仅 metadata 脱敏 | 21/21；caller exact `Password="SYNTH_HEAD;SYNTH_TAIL"` 与 `Pwd='...'` 无 head/tail；metadata 与 meta.txt 无泄漏；实际 child argv 长度不变 |
| CI-EVID-R1-03 | MAJOR | `result="Pass"` 下真实 `<failure>` 子节点仍被接受 | 按 result 校验 test 子节点：Pass 仅 `output`/`traits`；Fail 允许 `failure`；Skip/NotRun 允许 `reason`；矛盾/未知/嵌套 failure 拒绝 | 75/75；caller exact `pass-with-failure-child.xml` 拒绝、`valid.xml` 接受；真实 42/13 仍接受 |
| CI-EVID-R1-04 | MAJOR | LogDir 被 `Force` 创建并覆盖既有封存；finalizer 只 warning；manifest 可含旧 manifest | 任何写入/finally 前 `Initialize-OutputDirectory` 取得所有权；非空 LogDir 直接 BLOCKED 且不写；写入 owner marker；summary/log/manifest 写读失败一律非零；temp→atomic move；manifest 排除自身并逐行回读校验 | sealed 目录逐字节不变、exit2、无新 root；finalizer seal 失败 exit1 且不会 PASS；fresh manifest 0 bad/无 self |

### 12.2 R1-01 编码/locale

- `test-v5-native-exit.ps1` 的所有 `.meta.json`、`.meta.txt`、`.kill.json` 读取改为
  `Get-Content -Raw -Encoding UTF8`；`startup-failure` 的真实本地化 error 不再触发
  ANSI `ConvertFrom-Json` 失败。
- `verify-v5-table-postgres.ps1` 的 marker/reader/restore-reader JSON 和 finalizer
  readback 全部显式 UTF8；`Get-PgScalar` 已使用 shared UTF8 reader。
- Startup S1 保留 `state=AccessDenied` 与 `errorCode=10013` 两个强断言；detail 断言改为
  非空且长度 >= 8 的真实 OS message（本机中文
  `以一种访问权限不允许的方式做了一个访问套接字的尝试。` 通过），不再要求英语。
- 新增 `non-ascii-metadata`：用生产 `Write-NativeInvocationMetadata` 写 UTF-8 no-BOM
  的 `本地化错误：找不到指定的文件。`，再用显式 UTF8 读取 JSON 与 meta.txt，逐字符
  `-ceq` 比较。

### 12.3 R1-02 quoted credential

- 新扫描器识别 `Password`/`Pwd`（大小写/空白），支持单/双引号、内嵌分号、
  doubled quote、backslash-escaped quote、无引号值和 `SensitiveValues`。
- metadata 新增 `redaction.credentialFieldsRedacted` 与
  `credentialFieldsRedacted=` 行；不再用 `secretValuesRecorded=false` 代替实际边界。
- 实测矩阵（21/21 全过）：double/single quoted semicolon、spacing/case、doubled quote、
  backslash quote、unquoted、escaped semicolon、SensitiveValues、benign argv、
  quoted metadata path。quoted metadata case 同时断言 child 收到完整原始 argv
  （`unused-length` 等于原始长度）。
- Caller exact fixtures 在 fresh `build/r2-caller-originals-*` 中复算：
  `replay.json` SHA256 `3EBE6B1927BEE321B01DD6D308D30C4CB4EA8B11AA5D4FD504BACCF5C1ABCDC7`；
  两例 `tailLeaked=false/headLeaked=false`，sanitized argv 为
  `...Password=<redacted>;Username=fixture` / `...Pwd=<redacted>;Username=fixture`。

### 12.4 R1-03 XML 子节点一致性

- Pass 下显式 `failure`/`reason`/`error`/未知 child 一律拒绝；Fail 下 `reason`、Skip 下
  `failure` 也拒绝；合法 `output`/`traits` 保留。
- 原 7 个 selfcheck 与既有 60 例全部保留；新增 12 例 XML 矛盾/合法 child 矩阵（含 3 例嵌套矛盾），另加 3 例 seal，总 **75/75**。
- Caller exact `pass-with-failure-child.xml` 在 fresh 目录复算：
  `accepted=false`，reason
  `result XML test 'Probe.Test' result=Pass contains contradictory <failure> child`；
  `valid.xml` 仍 `accepted=true`（1/1）。
- 真实 PostgreSQL 运行 `tests.xml` 42/42、`tests-targeted.xml` 13/13 通过同一函数。

### 12.5 R1-04 输出所有权与 seal

- 主流程在 port preflight/任何写文件之前调用 `Initialize-OutputDirectory`：
  - 已存在非空目录 → `BLOCKED: LogDir is not empty; refusing to overwrite existing evidence`，
    `exit 2`，不写 summary/log/manifest，不创建 Root；
  - 空目录/新目录 → 写 `p52-logdir-owner.json` 后取得所有权。
- Finalizer 使用临时 manifest + atomic move；明确排除 `manifest.sha256.json` 与
  `.tmp`；写后逐行重算 SHA/bytes；summary exit 与 wrapper.log 均回读；任一步失败
  `script:Exit=1`、`SealOk=false`，最终不打印 PASS。
- 复算：
  - Caller `previous-sealed-evidence-port0` 的 fresh copy：exit 2、4→4 文件、
    `changed=[]`、`rootCreated=false`；`refusal.json` SHA256
    `18F45AF46F1595C1FF22E90202D6A1CBD3421232CF1A0A0EE2DA8AB0DC574395`。
  - Finalizer ownership 缺失/ manifest 为目录两例：exit 1、SealOk false；
    `seal-failure.json` SHA256
    `836E35B9079F3CDD462E56EC5298D4D357DC8E35489AAB64838463E5B5B12AA6`。
  - 真实 fresh run manifest 196 行、0 bad、0 self row。

### 12.6 R2 最终实际运行

| 项 | 结果 |
|---|---|
| PS 5.1 parser | 所有脚本 `PARSE-OK` |
| native（原 10 项 + 11 新增） | **21/21**，native exit 0 |
| table evidence（原 60 项 + 15 新增） | **75/75**，native exit 0 |
| direct gate | **19/19**，native exit 0 |
| dotnetTest gate | **48/48**，native exit 0；仍非全量 suite |
| startup/port/PG regression | **149/149**，0 skip/0 not-run，native exit 0 |
| real table-only PG 16.10 | exit 0，`P52-TABLE-POSTGRES-PASS`，42/42 + 13/13 |

最终真实 PG：`E:/majiang-review/ci-table-evidence-ds-20261101-2048/build/real-pg-r2c-20261101-143009/table/`
（cluster root 在 `artifacts/p52-pg-cluster-r2c-20261101-143009`，已删除）。
- `wrapper-summary.json` SHA `7D8C5162C1287E6A156A4101058823DA040F4C5671C5F8FC2A13D63B3D7CED82`
- `manifest.sha256.json` SHA `6F8A1B78D01E4AF18C4BD1B397BF1C23CE77F4908CA5DD6BE205376ED4A36910`，196 行/0 bad/0 self
- `tests-evidence.json` `3D7303E402FCD6978A970F1B5F8CD10D5C7FEE4AA34F36862E27B518E3F7902A`
- `tests-targeted-evidence.json` `3807179E3BDBDB69408B8D2E5EEBC0ED999284A933DD5D9AF65548D15AD0AD79`
- `backup-restore-evidence.json` `2E917BA0B08A289DA5D1029A0F6A020CD21F435C7F6671B94A8B6A4FD159D370`
- `cleanup-evidence.json` `D6CEAD8730375FBD7C63A47F23E3C6E2FD794171B23C808B32978EBE5C12AF05`
- `restore-preflight.json` `DDE1848A2C5718001D32063358A2A5DC1CB8CD85A95D81AAEAF6DB9320CAC034`
- `pg-dump-artifact.json` `45A419A8BEB3587E41FD54063649B84930AB38E7F5A8167D580404C7AE530B63`
- `pg-owner.json` `446E9F748E0AC8AD6DF67C5E8C760D3BFCA02106D6DE787B8803F7C5CCC60CDD`
- `crash-writer.log.kill.json` `AE76C226B9CE754A6FD10A69BD02A44EC03492FC0B70BC1596F71FF72998E97C`
- `tests.xml` `E33A6929A7D261B84B027E4CFB49565078923BB401EE5993B05EE5300A76600D`
- `tests-targeted.xml` `179EC548143B0F843ED1504B48C532A88B7B2FE688E7C019757A183F887AB3A0`
- `table_store.dump` 44,726 bytes / `0F8C78994496D5C199B12E59598A83CA28316F94CDA0031FD04BA713CBFE1E39`
- `p52-logdir-owner.json` `5AFD5A8D575F0799FC9DF21928C01CC6CF4B6FFB892B3DA70833AC736D9C9F5F`
- preflight raw：`mahjong_p52_restore|127.0.0.1|55592|0|0|0`；dump→restore closure 5/5 true；
  events=2/receipts=1/outbox=1/tables=13；reader eventCount=2/ownerEpoch=3/terminal=true。
- cleanup：stop native 0，pre-stop PID 8124，post-stop 0，rootDeleted=true，
  rootExistsAfter=false；最终无 `postgres.exe`、无 55592 listener。
- `environmentRestored=true`；`payloadKeyRecorded=false`；captured 变量全部 restored。

Startup 最终证据：`build/ci-pg-startup-selftest/20261101-143020-e5512e44/`
- `selftest-summary.json` SHA `A7521497794EDB34B72F9519E69B219132DBA326F5DD4AF253C759CC4C0BB189`
- `manifest.sha256.json` SHA `E8FD0052B6F9A3218E5964E36B5A7E2B96E319AADAD5B1FDC6408F72B016D018`，1817 行/0 bad

Self-test evidence：
- native `artifacts/native-exit-680d1e189db147bcbb7eb8d34b19a09b/results.json`
  SHA `79B11CE9DD20C85147F8868A52A42D0FCEE8F50F05605CAD5E2C9558E24824B0`
- table `artifacts/table-evidence-selftest-14fc2d7c782348acab43fdce334e81b4/results.json`
  SHA `29C4A4319431C700B3C3D190F1EF5911F10459393542EFDB7C3424D27B7FC951`
- direct `artifacts/direct-results-selftest-3cb0af4133ba44b0af91576a995df8df/results.json`
  SHA `6E23CD7000C6536AC42F5238F182334AA6E0029EDC34872BF566E5DF890E6192`
- dotnetTest `artifacts/dotnettest-results-selftest-5b2ee585806f45f9b5fcce67eaeec5e0/results.json`
  SHA `595EDFD1DE44F83C7CF22E6B30187DC6759A236CF445ADD69E2B1CA16B998AC8`

### 12.7 R2 首失败（保留）

1. R2 首轮 real run `build/real-pg-r2-20261101-141551/table` 在 restore preflight
   报 `must return 6 fields (got 1)`：机械编码替换误把生产调用
   `-RawOutput` 变成 `-Raw -Encoding UTF8Output`，并非 SQL 或 PG 缺陷。修复为
   `-RawOutput` 后，最终 real run 通过；失败原件保留，不刷新旧摘要。
2. table evidence 首轮 70 例因空 `wrapper.log` 未创建/JSON 数组被 PowerShell 管道折叠
   而失败；修复为显式写空日志 + 先赋值再 `@()` 计数后 72/72。
3. startup 首轮 S1 英语 detail 断言失败；已按 locale-neutral 修复，最终 149/149。
4. native 首轮 UTF-8 no-BOM 读取失败；已显式 UTF8 读取，最终 21/21。

### 12.8 残余（不因 R2 改变）

- dotnetTest **全量** NOT_RUN；48/48 是门禁自检 + 2 个 live 小 probe。
- `git pins` 历史 blob NOT_RUN；历史 `39970580589` 仍 BLOCKED。
- 远端 CI 未触发/未重跑；未 push/改 PR/record MR/approve/merge/deploy；不能宣称远端新头或 G2。
- 非 Windows、Unity/Player/Android/设备、lobby/unified leg 未运行。
- 单条 durable table 状态不是自然整场终局或 G2 结算验收。
- 本报告与 R2 证据不改变 D-02/D-09 的 INFO 边界，也不改写历史 R1 原件。

## 13. R3 返修：R1-02 / R1-03 两组残余闭环

Caller R2 复核原件：`E:/majiang-review/caller-table-evidence-r2-7c9d12e4/build/caller-review/REVIEW.txt`
（SHA256 `3CD7A807B8D1BC1A9F9D96F4373620EB0CE68CD4874BF94A37853AFF5B39F4C3`），
manifest（SHA256 `836C9DF14E24702F3E1047A6543119D839FD1EA0713E672B4AE4902D835226B4`，
2034 项/0 mismatch）。R1-01/R1-04 保持关闭；本轮只修 R1-02/R1-03 残余，六文件范围不变。

### 13.1 R1-02 残余：literal backslash + doubled quote

- **根因**：quoted 扫描把反斜杠当作转义前缀；实际 Npgsql 语法中反斜杠是字面字符，
  只有双写引号（`""` / `''`）表示引号转义。`SYNTH_HEAD\""X;SYNTH_TAIL` 因此被提前结束。
- **修复**：quoted 分支只把 doubled quote 当转义，反斜杠按字面处理；完整值替换为
  `<redacted>`。真实执行 argv 仍用原始输入，metadata 才脱敏。
- **exact 输入**（caller `grammar-inputs.txt` 原文）：
  - `Host=x;Password="SYNTH_HEAD\""X;SYNTH_TAIL";Username=fixture`
  - `Host=x;Pwd='SYNTH_HEAD\''X;SYNTH_TAIL';Username=fixture`
- **验收**：
  - 两例 production 函数输出均为 `...Password=<redacted>;...` / `...Pwd=<redacted>;...`，
    `headLeaked=false`、`tailLeaked=false`；fresh replay `replay.json` SHA
    `57179BC27098EBFF1578702299281A5775004310C5C017579BF0595D07ED9A94`。
  - 实际 `Invoke-Native` metadata：`grammar-metadata-double/single` 的 `.meta.json` 与
    `.meta.txt` 均无 `SYNTH_HEAD`/`SYNTH_TAIL`；child stdout 长度 60/55，证明 argv 原样。
  - native 全矩阵 **24/24**，原 21 项全保留，新增该两组 exact grammar 的函数与 metadata case。
- **边界纠正**：旧 R2 的 `Password="...\";..."`（单个反斜杠+单引号）不是 Npgsql 接受的
  形状，已被实际 grammar 的 doubled-quote 输入替代；不把 generic builder 的 quoted KEY
  探索列为缺陷。

### 13.2 R1-03 残余：assembly `<errors>` 子树

- **根因**：assembly 子节点白名单允许 `<errors>`，但末尾只统计
  `assembly.SelectNodes('.//errors/error')`；`<errors><failure>`、`<errors><unknown>`、
  `<errors>text</errors>` 在 `errors=0` 时被窄 XPath 视为空。
- **修复**：完整校验 assembly `<errors>`：
  - 只允许至多一个 `<errors>`；
  - 直接 element 只能是 `<error>`；`failure`/`unknown` 等直接拒绝；
  - 非空 text/CDATA 拒绝；空白与注释允许；
  - 标准 `<error>` 必须有 `@type` 或 `@name`，其直接子节点只允许
    `failure/message/stack-trace/output`；
  - 直接 error 计数与 assembly `errors` 属性必须一致（标准 error 在 `errors=0` 时仍拒绝）。
- **验收**：table 矩阵 **82/82**（原 60 项 + R2 15 项 + 本轮 7 项 assembly errors）。
  Caller exact `xml-r2` 复算：`valid.xml`/`assembly-empty-errors.xml` 接受；
  `original-test-failure.xml`、`assembly-errors-error/failure/unknown/text.xml` 拒绝。
  Fresh replay `replay.json` 同 SHA `57179B...`，7/7 XML 预期一致。
- 合法空 `<errors />`、空白/注释、Pass `output`/`traits` 保留；真实 42/13 仍通过。

### 13.3 R3 最终实际运行

| 项 | 结果 |
|---|---|
| PS 5.1 parser | 所有脚本 `PARSE-OK` |
| native（原 21 + 3 新 grammar/metadata） | **24/24**，native exit 0 |
| table evidence（原 75 + 7 assembly errors） | **82/82**，native exit 0 |
| direct gate | **19/19** |
| dotnetTest gate | **48/48**（仍非全量 suite） |
| startup/PG regression | **149/149**，0 skip/0 not-run |
| real table-only PG 16.10 | exit 0，`P52-TABLE-POSTGRES-PASS`，42/42 + 13/13 |

最终真实 PG：`build/real-pg-r3-20261101-145758/table/`
- `wrapper-summary.json` `F28BEFA59DFB566A985121D868AF8B09DFBAB15820DECD30C8714C237B838AEA`
- `manifest.sha256.json` `2FA7E6F88E9E7951C72F0936FA2D05F6CFC871F73D913B8EFD0EA0482800BD05`，196 行/0 bad/0 self
- `tests-evidence.json` `ED133369E18F94FF049D1C8229223E49A91AB0F7168C8123402DAC26D97BDCC7`
- `tests-targeted-evidence.json` `9EEE9C8CA171389020558413DD998E351075FC0D7B160107E45ECDBEB83D4AC3`
- `backup-restore-evidence.json` `C2D3FCB62C170B34F000829E96C3DF0B437B1C29F0B1C96F8E3EB30E92420692`
- `cleanup-evidence.json` `AB2714A9C32C5D8C2A40F2727C3FDC730129831479828E5F8B82F4CA21E78A18`
- `restore-preflight.json` `B75943C5FC297197001F5B10F594D172DAE8D378719C367F9C16AD2822E15B02`
- `pg-dump-artifact.json` `47EDB700080FAEA79CD2398054F5992540DC623A1BE5E3B9A1B90BFFF50CD2FD`
- `pg-owner.json` `101A519B6F3142DDFA6A3AF0CCE97AFC7BAC62E133A481FE8F2D33E354B41D11`
- `crash-writer.log.kill.json` `FD84EA51721E7B9B7078F169F788C2FFC44EAEB21ED6E287628E56757D91D6BE`
- `tests.xml` `64725561E9FF7A8351269A696083C7A41FB4A5F4622AB38BA8A7C9705DC670F1`
- `tests-targeted.xml` `6A87D81A16AE0F00BD2AC5F947ACA5A7BF48823136CA306E06038DB9B502E725`
- `table_store.dump` 44,718 bytes / `4A5A1B54F79989A600A97AEC7921F45A54393F5FD433A11CCE0020B1F172C2E7`
- `p52-logdir-owner.json` `6945242EA9D8866FC47649F2F1621E49E3F57020E93B10AE00341664952B33CA`
- cleanup：pre-stop PID `16728`、post-stop 0、rootDeleted=true、rootExistsAfter=false；
  最终 `postgres.exe` 0、55592 listener 0；`environmentRestored=true`。
- 真实 `tests.xml`/targeted 42/13、唯一 id；preflight `mahjong_p52_restore|127.0.0.1|55592|0|0|0`；
  dump→restore→reader closure 全 true（events=2/receipts=1/outbox=1/tables=13）。

Startup 最终：`build/ci-pg-startup-selftest/20261101-145806-d4f1c540/`
- summary `85D9E2C5D9B9C97AA324539D98D6EE1D2BEA38F8900B2180802137FD8717D0E9`
- manifest `4EC9CFC564BA05D56A69345FF2943613233BF1E290D3B66DE7B3A0D95348DA0A`，1817 行/0 bad

Self-test evidence：
- native `artifacts/native-exit-1f4b87058d754120a962b023310eac7b/results.json`
  `25391C0EFF2AC04F75FF19B127F9B619E471A0544539B1FA2C0E2E623B7262F6`
- table `artifacts/table-evidence-selftest-64941b2a94fa4676a9bc4315469ab891/results.json`
  `82BE63399F516B8EC5A1F47F50719FC9948CE9A765D98BC7F05F8558EC30410F`
- direct `artifacts/direct-results-selftest-14df108c7546449aa70d7f8f5c567081/results.json`
  `B6CC284ACB1CB8E9651ABD860165509F8AA6B30BD97881A6FD3A8D55F58B41F0`
- dotnetTest `artifacts/dotnettest-results-selftest-d7a40bb3d02144e29d49567ff4cb0adb/results.json`
  `564813C994BDF5B6AF24EA24A0D7F9BF510E7714ACE67AF5F3565D512CC680AB`

### 13.4 R3 首失败与残余

- R3 真实 table leg 首次执行 exit 0，无新增产品首失败；R1/R2 首失败原件继续保留。
- 旧 R2 单反斜杠 fixture 不是实际 Npgsql grammar，已替换为 caller 的 doubled-quote
  exact 输入；这不是删除测试，而是修正到真实受支持语法，并保留原 native 21 项。
- dotnetTest 全量 NOT_RUN；git pins NOT_RUN；历史 `39970580589` BLOCKED；远端 CI
  未触发/未重跑，未 push/改 PR/record MR/approve/merge/deploy；Unity/Player/Android/
  设备及 lobby/unified leg 未运行。
- 单条 durable table 状态不是自然整场终局或 G2 结算验收；D-02/D-09 继续 INFO。
