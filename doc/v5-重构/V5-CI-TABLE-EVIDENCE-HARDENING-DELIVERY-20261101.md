# V5 CI table 原生证据与 XML 门禁修复交付（2026-11-01）

状态：本任务范围实现与证据交付；**不是** G2 验收、不是 PR20 合并审查、不是远端 CI 新头认证。
V4 仍是唯一生产基线；G2–G5 未批准。

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
| `tools/ci/verify-v5-table-postgres.ps1` | `DDE3235010D32E6227FB4CF34D4DE69EE7034AF54447876865F12CD9BEF2DAEA` |
| `tools/ci/test-v5-native-exit.ps1` | `0311DCD7E41306E2C70EAF5D71F18DFFBA66B4234BBF4CC02A6989967BFB2DA1` |
| `tools/ci/test-v5-table-evidence.ps1` | `6ABC7CC8AD477182929ABBAC4B9BA1E95E0640FDEF0DA063F7731891B0AB4268` |
| `tools/tests/VerifyV5TablePostgresStartup.Tests.ps1` | `D6E0244B880D241CF7ADB6A05A147815A7BC1C2B52D150A533FB11A1BB4A27FA` |
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
