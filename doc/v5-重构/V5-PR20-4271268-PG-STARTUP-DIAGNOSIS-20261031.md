# V5 PR20 4271268 PostgreSQL 启动失败诊断（CI 41140326888）

日期：2026-10-31
诊断对象：PR20 冻结头 `427126801fc0b052cb36b6423a4b0a79f869096a` 的 push run 41140326888
工作树：`D:/majiang-wt/ci-pg-start-4271268-ds-20261031`，分支 `wt/v5-ci-pg-start-4271268-ds-20261031`
冻结基线：从 accepted `origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` 创建，fast-forward 到已逐项接受并发布的 PR20 `4271268`；本任务不移动该 HEAD。
结论属性：**组件级启动/端口探测修复 + 证据诊断**。不是 G2/G3/G5 验收，不改变 V4 生产基线。

> 当前状态一句话：失败发生在任何统一测试之前的真实 PostgreSQL 绑定阶段，`pg_ctl start` native exit 1，PG 16.10 对 `127.0.0.1:55592` 收到 `EACCES/Permission denied`；旧 preflight 只做 TCP connect，无法发现“无人监听但 bind 被拒绝”的端口。修复改为“实际独占 bind 能力探测 + 显式端口不替换 + 有界默认候选”，并保留 native exit 与原日志。

---

## 1. 结论

| 项目 | 结论 |
|---|---|
| 41140326888 失败阶段 | 统一测试之前；`verify-v5-table-postgres.ps1` 的 `Initialize-Cluster` → `pg_ctl start` |
| 失败性质 | 真实 PostgreSQL 启动失败（绑定 socket 被 OS 拒绝），**不是**业务测试失败、断言失败或已知 flaky |
| 可证明的直接原因 | PostgreSQL 16.10 无法在 `127.0.0.1:55592` 创建监听 socket；server log 为 `could not bind IPv4 address "127.0.0.1": Permission denied` → `FATAL: could not create any TCP/IP sockets` |
| 不可证明的部分 | 该 runner 上端口为何不可绑定：administered/excluded port range、其他 socket 的 `SO_EXCLUSIVEADDRUSE`、策略/安全软件等在该 run 中**没有采集**，因此具体机制为 **UNKNOWN**；不把推断写成已证明 |
| 旧实现缺陷 | `Test-PortInUse` 仅 `ConnectAsync`，连接失败即判“端口空闲”；无法覆盖“无 listener 但 bind 返回 WSAEACCES”的端口 |
| 成功 run 对照 | 同 head、同 workflow 的 PR run 41140332081 的 step 5 成功（约 21 分钟）；其内部 PG 日志因 artifact 下载需认证而 **UNKNOWN**，不以 metadata 冒充内容证据 |
| 修复 | 实际独占 IPv4 loopback bind 探测；显式 `-Port` 精确使用或 BLOCKED，绝不静默替换；无 `-Port` 时从 `-DefaultPort` 起有界选择，跳过 lobby 端口，候选耗尽明确 BLOCKED；启动失败保留 native exit 与原始日志、不重试、不迁移端口 |
| 验证 | 自测 149/149 PASS、0 fail/skip/NOT_RUN（exit 0）；真实 PG 16.10 启动/查询/immediate 重启/cleanup；实际 table gate 42/42、13/13、crash/restore 全 PASS（exit 0）；未跑完整 unified |
| 阶段门槛 | G1 已批准；G2–G5 未批准。本报告不宣称任何 G 通过 |

---

## 2. 失败 run 原始证据

### 2.1 身份与时间线

| 字段 | 值 |
|---|---|
| run id | `41140326888`（push） |
| workflow | `v5-verify`，workflow id `394054164`，`.github/workflows/v5-verify.yml`，attempt 1 |
| head SHA | `427126801fc0b052cb36b6423a4b0a79f869096a` |
| job | `verify` / `124352753654`，runner `1000000332` |
| job 起止（UTC） | 2026-10-30T22:46:43Z → 22:47:34Z |
| 失败 step | `Run unified V5 verification`，22:47:09Z → 22:47:29Z |
| 失败前 step | `Verify native failure exit propagation` 6/6 PASS（22:47:02Z–22:47:09Z） |
| 失败后 artifact | `v5-verify-logs-41140326888-1`，10,393 bytes，digest `sha256:6bd0909efd34b151aa63d73abd1535d1b3460d6a460b899c8d377dd282410c93` |

### 2.2 原始日志（原文）

`artifacts/verify-v5/table/pg/postgres.log`（本地证据路径见 §2.4）：

```text
2026-10-30 22:47:24.325 UTC [2488] LOG:  starting PostgreSQL 16.10, compiled by Visual C++ build 1944, 64-bit
2026-10-30 22:47:24.326 UTC [2488] LOG:  could not bind IPv4 address "127.0.0.1": Permission denied
2026-10-30 22:47:24.326 UTC [2488] WARNING:  could not create listen socket for "127.0.0.1"
2026-10-30 22:47:24.326 UTC [2488] FATAL:  could not create any TCP/IP sockets
2026-10-30 22:47:24.327 UTC [2488] LOG:  database system is shut down
```

`pg-start.log` 原文：

```text
waiting for server to start.... stopped waiting
pg_ctl: could not start server
Examine the log output.
```

`wrapper-summary.json` 关键字段：`port = 55592`、`exit = 1`，`stages` **只有一条**：

```json
{ "stage": "result", "status": "FAIL", "exitCode": 1,
  "detail": "pg_ctl start failed with exit 1 (see ...\\pg-start.log)." }
```

同目录只有 `download.log`、`initdb.log`、`pg-start.log(+stdout/stderr)`、`pg/postgres.log`、`wrapper.log`、`wrapper-summary.json`、`manifest.sha256.json`；**没有** `build-tests.log`、`tests.xml`、`tests.log`、`crash-*`、`pg-restart*`。`initdb.log` 末尾为 `Success.`，说明二进制、下载 SHA 与 initdb 均成功，失败只发生在 server 启动绑定。

### 2.3 为什么不能归为业务测试失败 / flaky / 用另一次成功销账

1. 失败发生在 `Initialize-Cluster` 内，早于 `fresh-build`、`tests`、`tests-targeted`、`crash-*`、`backup-restore` 任何阶段；summary 的 `stages` 没有这些阶段。
2. 没有任何测试 XML，故不存在 `total=0`、skip、旧产物或测试断言失败；不能把该 run 记为“业务测试失败”。
3. 日志是真实 PostgreSQL 16.10 的 server 启动日志，`pg_ctl` native exit 1；不是 wrapper 的 PowerShell 异常或超时。
4. 失败 step 总耗时约 20 秒，其中包含下载/解压/initdb；没有进入构建与测试的耗时。
5. 用另一次 run（41140332081）成功来“销账”不成立：它是不同 event、不同 runner 的独立执行；其内部日志无法匿名下载（见 §2.4），只能证明“同一个 head 在另一个 runner 上完整通过”，不能证明本 run 的失败原因不存在。

### 2.4 成功 run 对照（只读 metadata，内部日志 UNKNOWN）

| 字段 | 失败 run 41140326888 | PR run 41140332081 |
|---|---|---|
| GitHub event | `push` | `pull_request` |
| workflow / attempt | `v5-verify` / 1 | 同 |
| API head SHA | `4271268...` | 同 |
| job | `124352753654`，runner `1000000332` | `124352769439`，runner `1000000333` |
| job 起止（UTC） | 22:46:43 → 22:47:34 | 22:46:45 → 23:08:06 |
| step 5 | **failure** 22:47:09 → 22:47:29 | **success** 22:47:09 → 23:08:01 |
| 结论 | failure | success |
| artifact | `v5-verify-logs-41140326888-1`，10,393 bytes | `v5-verify-logs-41140332081-1`，4,490,745 bytes，digest `sha256:ed85336197e1546330151bd790910285f0f66e4d75be84970f71219d91902c57` |
| 内部 PG 日志 | 已下载并核对（本树 ignored `build/`） | **UNKNOWN**：匿名下载 `archive_download_url` 返回 HTTP 401 `Requires authentication` |

两条 run 的 workflow、head、step 定义相同，启动时间相差 3 秒，分属两个不同 runner；一个在 PostgreSQL 绑定阶段失败，另一个完整跑完 21 分钟。可支持的结论仅是：**这是 runner/环境相关的瞬时绑定拒绝，不是该 head 的确定性代码/测试失败**。不能从该对照推出具体端口保留机制。

失败 run 的 37 个 artifact 文件、`job-raw.log`、`run-meta.json` 与本地 SHA 清单保存在：

```text
build/caller-initial-ci-41140326888/
  job-raw.log                          SHA-256 88E66909107D2D39AF99C39D305ECFDB9EBAC9742C3DF0CA682ECC6B50DCA154
  run-meta.json                        SHA-256 454F98017E1B0AB8E1EC4AAB6EA5CE7D034106C562C619D0C124C11D441FD443
  caller-evidence-sha256.csv           37 行，逐项复核 0 mismatch
  v5-verify-logs-41140326888-1/...     pg/postgres.log SHA-256 32B03FF28FEBA990EB837601D780ED75BA2982AA9A1143CD4F1906F218CA571F
                                       pg-start.log     SHA-256 432F5F526EE17D2C0810E25ABEDB696FBF81F3CCB3BCB345D10007658D6E7918
                                       wrapper-summary.json SHA-256 036F44484DC6ED31DAC11F7C51267535356A8E1C68AA31EAFA70E2D234F3BDC7
```

本次诊断**未修改**该目录；复核 `caller-evidence-sha256.csv` 37/37 匹配。成功/失败 run 的 GitHub API metadata 另存于 `build/ci-pg-startup-selftest/network/`（见 §9）。私有原始数据只落在 ignored `build/`，公开报告不写凭据。

---

## 3. 根因可证明范围

### 3.1 已证明

1. `pg_ctl start` 的真实 native exit 为 **1**；server log 由 PostgreSQL 16.10 写出。
2. OS 对该次 `bind(127.0.0.1:55592)` 返回 **EACCES**（Windows `WSAEACCES` 10013，文本 `Permission denied`），不是 `EADDRINUSE`；PostgreSQL 因此没有创建任何 TCP/IP socket，直接 `FATAL`。
3. 失败前 `download.log`、`initdb.log` 存在且 initdb `Success.`；没有构建/测试产物，故失败点精确在“cluster 启动”。
4. 旧 preflight（`Test-PortInUse`）只做 `ConnectAsync('127.0.0.1', 55592)`，超时/异常即返回 `$false`；在该场景下没有任何进程接受连接，所以它必然判“空闲”，wrapper 继续 `pg_ctl start`。
5. 本机可以用**真实 socket**复现同一失败类：一个绑定 `0.0.0.0:<port>`、设置 `SO_EXCLUSIVEADDRUSE`、但不 `listen` 的 socket，使 `connect(127.0.0.1:<port>)` 返回 false，同时 `bind(127.0.0.1:<port>)` 返回 `AccessDenied / 10013`。这与 CI 的“connect 看不到、bind 被拒绝”完全同类（注意：这是失败类复现，不是对 CI runner 具体机制的证明）。
6. 旧 `pg_ctl start` 失败路径只抛出 `pg_ctl start failed with exit 1 (see pg-start.log)`；虽然 finally 会复制 `pg/postgres.log`，但 summary 不含 native exit 与 server log 片段，证据可读性不足。

### 3.2 UNKNOWN（必须明确）

- **该 runner 上 55592 为何不可绑定，UNKNOWN。** 可能的机制包括但不限于：
  - Windows administered/excluded port range 覆盖 55592；
  - 另一个进程以 `SO_EXCLUSIVEADDRUSE` 绑定该端口（可能只绑在其他地址，因此 loopback connect 不可达）；
  - 安全软件/策略对 bind 的拒绝。
  该 run 没有采集 `netsh int ipv4 show excludedportrange`、`GetActiveTcpListeners`、`netstat -ano` 或等价快照，runner 已销毁；因此不得写成“确认是 excluded range”或“确认是 Hyper-V 保留”。
- **41140332081 的内部 PG/server 日志、实际端口选择与 clean-up 细节，UNKNOWN**：artifact 匿名下载 401；只核对了 run/job metadata 与 step 结论。
- **同 head 重跑是否复现，UNKNOWN**：本任务禁止 rerun/dispatch CI，未做重跑。
- **是否与 runner 镜像/区域/负载相关，UNKNOWN**：两边 job 的 runner 名称均只显示 `GitHub Actions`，没有更多宿主信息。

### 3.3 connect-only 探针缺口

Windows 上 `connect()` 与 `bind()` 不是同一权限面：

- 端口被别的进程 `listen`：connect 成功 → 旧探针能发现（但只在 127.0.0.1 可路由时）。
- 端口落在 excluded/reserved range：无 listener，connect 失败；`bind()` 返回 `WSAEACCES`。
- 端口被其他 socket 以 `SO_EXCLUSIVEADDRUSE` 占用（且未 listen，或只绑其他地址）：connect 失败；`bind()` 返回 `WSAEACCES`。
- 端口刚被 TIME_WAIT/ephemeral 占用等：connect 可能失败，bind 也可能失败（错误码不同）。

因此修复后的 preflight 顺序是：

1. 端口范围 `1..65535` 校验；
2. 本 wrapper 保留端口（5432/5433/55460/55463）拒绝；
3. 查询 OS active TCP listener 表（不创建 socket）；
4. 在 `127.0.0.1` 上做一次**独占** `bind`，按 `SocketErrorCode` 分类为 `Bindable / InUse / AccessDenied / Unbindable`；
5. 探测 socket 在返回前释放，不“占着”端口给 `pg_ctl`。

本地实测（只读/临时 socket）：55592、55593 均 `Bindable`，本机 `netsh int ipv4 show excludedportrange protocol=tcp` 无排除项。该本机状态**不能**外推到 CI runner。

### 3.4 TOCTOU（必要说明）

探测释放 socket 后、`pg_ctl` 真正 bind 之前存在窗口；本修复不假装消除它：

- 选择阶段记录每个候选的真实状态；释放后不保留端口。
- `Initialize-Cluster` 在 initdb 之前再做一次同样的 bind 探测；若已变化则 **BLOCKED**，不重试、不改端口。
- 若变化发生在这次 recheck 之后，`pg_ctl start` 会真实失败；此时按 **FAIL exit 1** 保留 native exit 与两份原始日志，不做盲目重试、不自动换端口、不停止/附加任何其他 cluster。

---

## 4. 最小修复映射

只改 3 个精确文件：

```text
tools/ci/verify-v5-table-postgres.ps1
tools/tests/VerifyV5TablePostgresStartup.Tests.ps1        （新增）
doc/v5-重构/V5-PR20-4271268-PG-STARTUP-DIAGNOSIS-20261031.md （本文件）
```

未改：任何源码、Client/Runtime/Unity、统一 verifier `tools/verify-v5.ps1`、workflow、lobby PG wrapper `tools/ci/verify-v5-postgres.ps1`、PR24 待验收代码。

### 4.1 端口探测

| 生产函数 | 行为 |
|---|---|
| `Get-PortProbe` | 范围/保留端口 → OS listener 表 → 真实独占 `127.0.0.1` bind；返回 `state`、`errorCode`、`detail`；socket 在 finally 释放 |
| `Test-TcpListenerReachable` | 保留原 connect 语义，仅用于 cleanup 判断自有 postmaster 是否在监听；**不再用于 preflight** |

### 4.2 显式/默认参数语义

| 输入 | 语义 |
|---|---|
| `-Port <n>`（显式） | **精确使用**。无效/保留/lobby 冲突/InUse/AccessDenied 一律 `BLOCKED`（exit 2），错误文本明确 `Refusing to substitute`；绝不自动换端口 |
| 未给 `-Port` | 从 `-DefaultPort`（默认 55592）起，最多 `-PortCandidateLimit`（默认 16，范围 1..64）个连续候选；逐个做真实探测，第一个 `Bindable` 且未被排除者胜出 |
| 候选耗尽 | `BLOCKED`，消息逐个列出 `port=state`（例如 `55592=AccessDenied;55593=lobby-excluded;...`），并 `Refusing to guess an unverified port` |
| `-RunUnifiedVerification` | `-LobbyPort` 必须 1..65535；从默认候选中排除该端口；显式 `-Port` 等于 lobby port → `BLOCKED`；两 cluster 端口不共享 |
| 非 unified | 不校验/排除 lobby port（lobby wrapper 未参与），但仍拒绝 55460 等保留端口 |

> 注意：之前 `-Port` 带默认值 55592，无法区分“用户显式指定 55592”与“未指定”。现在 `-Port` **无默认值**，用 `$PSBoundParameters.ContainsKey('Port')` 区分；`-DefaultPort` 只在未指定 `-Port` 时生效。CI 现有命令行（不带 `-Port`）仍从 55592 开始，行为向后兼容。

### 4.3 两 cluster 联动

- table cluster 只在 `127.0.0.1:<selected>`；lobby cluster 继续由未修改的 `verify-v5-postgres.ps1` 使用 `-LobbyPort`。
- `-RunUnifiedVerification` 时：选择阶段排除 `$LobbyPort`；显式冲突直接 BLOCKED；unified 调用仍传原始 `$LobbyPort`，不修改 lobby wrapper。
- table 的 cleanup 只按自己的 `Root`/`marker`/`DataDir` 操作；`Stop-And-RemoveCluster` 仍先做 owner/root/port 校验、`data_directory` 校验、自有 `postgres.exe` 引用检查，再删除。

### 4.4 启动失败：native exit + 原日志保留

`Initialize-Cluster` 的 `pg_ctl start` 失败分支现在：

1. 捕获 native exit（真实 `pg_ctl` 进程退出码，已保留 process handle 防 5.1 丢码）；
2. 读取 `postgres.log` 最后 20 行（压成一行，避免多行注入）；
3. 写 `pg-start-diagnostics.txt`（native exit、pg_ctl log、server log、tail）；
4. 记录 `pg-start` stage `FAIL` + native exit；
5. 抛错并在 wrapper.log 写入 `pg_ctl start failed with native exit 1`；
6. finally 复制原始 `pg/postgres.log`（以及 `Root/logs/*`）到证据目录，然后按所有权清理**自有** root；
7. **不重试** `pg_ctl`，不改端口，不删除/停止其他 cluster。

`Restart-ClusterImmediate` 的 restart start 失败同样记录 native exit + server tail + `pg-restart` FAIL。

### 4.5 env 与所有权不回退

- `Restore-ProcessEnvironment` 在 finally 对 16 个变量逐一恢复（present-nonempty 原值；原本 absent 的删除），并返回不含值本身的记录：`variable / wasSetBeforeRun / restored / restoredSha256`；空值/未设置用 `(empty)/(unset)` 标记，哈希用 `Get-TextSha256`。
- summary 新增 `environmentRestored` 与 `environment`；`payloadKeyRecorded` 仍为 false，payload key 不落盘。
- 所有权校验保持并更严格：`Assert-OwnedCluster` 仍要求 `owner=mahjong-p52-pg`、`marker.root` 全路径等于 `$Root`、`marker.port` 等于本次端口；外部/损坏 marker 一律拒绝，不删除其 root。
- `Stop-And-RemoveCluster` 仍只允许 `RepoRoot/artifacts` 下的自有 root，并检查无 `postgres.exe` 引用该 `DataDir`。

### 4.6 退出码/状态口径

| 情况 | 状态 | exit |
|---|---|---|
| 全部真实阶段通过 | `PASS` | 0 |
| 显式端口不可用、候选耗尽、lobby 冲突、缺 dotnet/PG 等前置条件 | `BLOCKED` | 2 |
| 真实 `pg_ctl`/build/test/crash/restore 失败 | `FAIL` | 1 |

`P52-TABLE-POSTGRES-BLOCKED` 与 `P52-TABLE-POSTGRES-FAIL` 分列，避免把环境阻断写成业务失败。

---

## 5. 验证（实际执行，真实计数）

### 5.1 新自测：`tools/tests/VerifyV5TablePostgresStartup.Tests.ps1`

命令（Windows PowerShell 5.1，本机）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/tests/VerifyV5TablePostgresStartup.Tests.ps1 -BasePort 55740
```

结果：**TOTAL=149 PASS=149 FAIL=0 SKIP=0 NOT_RUN=0，exit 0**。
证据：`build/ci-pg-startup-selftest/20261030-233432-32a82d8d/`（`console.log`、`selftest-summary.json`、`manifest.sha256.json`）。

场景矩阵（每个场景都执行生产函数或生产脚本真实路径，不是源码文本匹配）：

| 场景 | 覆盖 | 关键断言 |
|---|---|---|
| S0 静态契约 | 生产脚本可解析；关键函数/参数语义存在；无 `netsh`/防火墙/服务/`sc`/`reg` 等全机改动命令 | 9 checks PASS |
| S1 真实 bind 矩阵 | 空闲 → `Bindable`；真实 loopback listener → `InUse`；`SO_EXCLUSIVEADDRUSE` holder（不 listen）→ `connect=false` 且 `AccessDenied/10013`；holder 释放后恢复 `Bindable`；0/70000/5432/55460 | 13 checks PASS；复现了 CI 的 connect 盲区 |
| S2 selector 语义 | 显式精确；显式 AccessDenied 拒绝且不替换；默认跳过 AccessDenied 继续；默认跳过 lobby；无效/保留/lobby 冲突/候选上限；`-Probe` 定向注入的全拒绝与部分选择 | 16 checks PASS |
| S3 端到端外部占用 | 真实 listener 与真实 exclusive holder 各跑一次完整生产脚本 | exit 2 BLOCKED；无 root、无 pg-start.log、无 pg_ctl；外部 listener/holder 原样存活；env 已恢复（共 15 checks） |
| S4 端到端显式非法 | `-Port 0`、`-Port 70000`、`-Port 55460`、table=lobby 冲突 | 各 exit 2 BLOCKED；无 root；无 tests.xml（20 checks） |
| S5 端到端候选耗尽 | 3 个真实 exclusive holder 占满 `-DefaultPort..+2`，默认模式 | exit 2 BLOCKED；逐候选 `AccessDenied`；无 root、无 pg_ctl；holder 存活（11 checks） |
| S6 env/所有权 | 生产 `Restore-ProcessEnvironment` 恢复 present-nonempty 与 absent（fresh child 读回）；外来 marker 拒绝且 root 保留；完整脚本 cleanup FAIL 不删外来 root | 14 checks PASS。注：Windows 不能表示 present-empty（赋值 `''` 即删除），故不伪造该用例 |
| S7 真实 PG 16.10 | 生产 `Initialize-Cluster` + `Restart-ClusterImmediate`：默认模式选端口、真实启动、`SHOW server_version=16.10`、`SHOW port`、`data_directory` 归属、loopback-only、marker、PG 启动 stage、immediate stop/start、start time 变化、recovery lines、提交行 42 跨重启存活、`pg_ctl stop` exit 0、无自有 postgres 进程、长路径安全删除 | 22 checks PASS |
| S8 真实启动失败 | 用真实 PG data dir + 非法 `shared_buffers` 跑完整生产脚本；`pg_ctl` native exit 1；`pg-start` stage FAIL；`pg-start.log`/`pg/postgres.log`/`pg-start-diagnostics.txt` 保留；无 tests 阶段/无 tests.xml；只有一次 FATAL/尝试，无 ready、无重试；自有 root 被清理 | 20 checks PASS |
| S9 完整脚本 env 恢复 | 真实自有 cluster 启动后，用 fake dotnet 在 fresh build 返回 9；finally 必须停/删 cluster 并把 caller sentinel 恢复（按哈希核对） | 9 checks PASS |

说明：

- S1/S3/S5 的 exclusive holder 是本机可真实制造的 `WSAEACCES` 类；S2 的 `-Probe` 注入仅用于确定性证明“全候选耗尽/部分选择”这条分支，报告中已准确注明，不冒充真实端口保留。
- S7 的 `real-pg` root 位于 `build/`（ignored），使用 `\\?\` 长路径删除；删除前校验绝对路径属于本次 evidence root、marker 一致、无自有 `postgres.exe` 引用。
- 自测退出码：0 全部通过；1 有失败；2 缺 dotnet/PG zip/生产脚本等前置条件（BLOCKED，不产生假绿）。

### 5.2 真实 PostgreSQL 16.10 证据

- 版本：`16.10`（server log `starting PostgreSQL 16.10, compiled by Visual C++ build 1944, 64-bit`）。
- 端口：S7 选择 55755；listener 仅 `127.0.0.1`。
- immediate restart：`pg_postmaster_start_time` 从 `2026-10-30 23:35:31` 变为 `23:35:38`；recovery lines=3；`startup_probe` 行 `42` 跨重启存活。
- stop：真实 `pg_ctl ... stop -m fast` exit 0；无自有 `postgres.exe` 进程；root 删除成功。
- S8：真实 `pg_ctl start` native exit 1；server log 只有一次 `invalid value for parameter "shared_buffers"` FATAL，无 `database system is ready`，证明没有盲重试。
- S9：真实 cluster `cluster` stage PASS；fake dotnet build native exit 9；`environmentRestored=true`；sentinel 哈希匹配。

### 5.3 实际 table gate（不含 unified）

命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DefaultPort 55760 -PortCandidateLimit 4 `
  -DotnetPath D:\Unity\dotnet\dotnet.exe `
  -LogDir build/ci-pg-startup-selftest/table-gate-20261031-074121
```

结果：**exit 0 / `P52-TABLE-POSTGRES-PASS`**。summary stages 全部 PASS：

| stage | 结果 |
|---|---|
| port-preflight | PASS，mode=default，port=55760，candidateIndex=0 |
| pg-start | PASS，native exit 0 |
| cluster | PASS，server=16.10，data_directory=本树 artifacts 自有 root |
| xml-gating-selfcheck | PASS，7 个负例 |
| fresh-build | PASS，8.0.426 |
| tests | PASS，**42/42**，failed=0，skipped=0，notRun=0 |
| tests-targeted | PASS，**13/13** |
| crash-writer | PASS，KILLED（post-commit marker 后强杀） |
| postgres-crash-restart | PASS，start time 变化，recovery lines=3 |
| crash-reader | PASS，state=1 seq=2 terminal=True receipt=Applied cheatVersion=1 |
| backup-restore | PASS，pg_dump/pg_restore，restored events=2 |
| result | PASS |

证据：`build/ci-pg-startup-selftest/table-gate-20261031-074121/`（wrapper-summary.json、manifest.sha256.json、tests.xml 等）；console：`build/ci-pg-startup-selftest/table-gate-console-20261031-074121.log`。

**未跑完整 unified**：该 leg 在本 head 的历史成功耗时约 21 分钟，且本修复只涉及 table 启动/端口探测，不改变统一 verifier 或 lobby wrapper；按任务“完整 unified 非常昂贵，只在修改确实需要时跑”，本次不跑，unified 记为 **NOT_RUN**，不借旧 run 写新 PASS。G2/G3 仍为未批准。

### 5.4 首次失败证据完整性

- `build/caller-initial-ci-41140326888/` 未修改；`caller-evidence-sha256.csv` 37 行逐项复核 **0 mismatch**。
- `pg/postgres.log`、`pg-start.log`、`wrapper-summary.json`、`job-raw.log`、`run-meta.json` SHA 见 §9。
- 首次失败证据与本次修复证据分目录存放；公开文档不包含私有凭据/payload key。

---

## 6. 已知问题与严重级别

| ID | 级别 | 状态 | 说明 |
|---|---|---|---|
| D-01 | **BLOCKER** | **已修复（待调用者集成）** | 当前 head 的 push CI 在统一测试前因 `pg_ctl start` 绑定失败 exit 1；根因是 preflight 未验证 bind 能力。修复后默认模式会跳过不可 bind 的 55592，显式端口则明确 BLOCKED |
| D-02 | **MAJOR** | **已修复** | `Test-PortInUse` 仅 connect，漏掉“无 listener 但 bind 返回 WSAEACCES”的端口；替换为 listener 表 + 独占 bind 探测，并保留 connect 仅用于 cleanup |
| D-03 | MAJOR（证据缺口） | **UNKNOWN，未关闭** | CI runner 上 55592 不可绑定的具体 OS 机制未采集；本报告不猜测 excluded range/Hyper-V/安全软件。若需彻底关闭，需要 CI 在失败时上传 `netsh int ipv4 show excludedportrange protocol=tcp`、listener 表或等价快照 |
| D-04 | MINOR（证据缺口） | **UNKNOWN** | PR 成功 run 41140332081 的内部 PG 日志/端口无法匿名下载（401），只核对 metadata 与 step 结论 |
| D-05 | MINOR（既有、本任务未修） | **OPEN，范围外** | 生产 `Stop-And-RemoveCluster` 用 `Remove-Item -Recurse`；在非常深的工作树 root（本机 `build/.../realpg-*`，pgAdmin 文件路径 > 260 字符）会 `DirectoryNotFoundException`。CI 的 `artifacts/p52-pg-cluster-*` 路径较短（约 228 字符）未触发；本任务自测改用 `\\?\` 长路径删除。未在本次启动修复中扩大 scope 修改生产 cleanup |
| D-06 | MINOR（观测） | **既有** | `pg_isready` 未带 `-U`，会在 server log 留下 `FATAL: role "<os-user>" does not exist` 噪声；不影响 readiness（exit code 正确），本任务未改 |
| D-07 | MINOR（观测） | **既有** | `pg_ctl` 文本随 OS locale 本地化（本机中文），自测断言已避免依赖固定英文文案，仅用 `pg_ctl` 与 native exit/日志结构 |
| D-08 | INFO | **NOT_RUN** | 完整 unified、WSS/HTTPS、Unity/IL2CPP、Android/iOS 真机均未在本任务执行；不构成 G2/G3/G4/G5 证据 |

没有把 D-03/D-04 的 UNKNOWN 写成已证明；没有用“另一次 run 成功”抵消 D-01。

---

## 7. 环境与局限

| 项 | 本机实测 |
|---|---|
| OS | Microsoft Windows NT 10.0.19045.0（x64） |
| PowerShell | 5.1.19041.6456（与 CI 的 `shell: powershell` 同主版本） |
| .NET SDK | 8.0.426（`D:\Unity\dotnet\dotnet.exe`） |
| PostgreSQL | 16.10 EDB Windows x64 binaries；zip SHA-256 `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C` |
| 本机 55592/55593 | `Bindable`；`netsh ... excludedportrange` 无排除项（仅本机，不外推 CI） |
| CI runner | windows-2025-vs2026，image 20261021.278.1；destroyed，无法补采 |
| 网络 | `curl.exe` 可读取 GitHub API metadata；artifact 匿名下载 401 |

局限：

- 本机无法复刻 CI runner 的端口保留/宿主状态；`WSAEACCES` 只用真实 exclusive holder 复现了失败类，并明确标注不是 CI 具体机制。
- 自测的 `-Probe` 注入只针对 selector 分支；真实 EACCES 证据由 S1/S3/S5 的 socket 提供。
- 完整 unified 未跑；本修复不改变 unified verifier、lobby wrapper、workflow，unified/CI 结果仍需调用者在集成后独立复跑。
- Windows 不能表示 present-empty 进程变量；自测覆盖 present-nonempty/absent，并在文档中说明。

---

## 8. 范围、提交与清洁状态

- 只改 3 个精确文件（脚本、测试、本报告）。
- 未 push、未建/改/合并 PR、未 rerun/dispatch 任何 CI、未改默认分支、未改 scheduler/`complete.flag`、未改其他工作树、未启动 Unity、未委派子代理。
- 未修改机器 excluded ports、防火墙、服务或全局设置；未停止/附加任何既有 PostgreSQL；未使用 55460 lobby 端口。
- 交付为本地最小提交；最终 commit SHA、diffstat、`git diff --check` 与 `git status --porcelain` 由最终交付回复给出（提交后 `git log -1 --format=%H` 复核）。
- 证据目录均为 ignored `build/`；`artifacts/` 下本任务临时 cluster 已由生产 cleanup 删除，复核无残留。

---

## 9. 证据 SHA 清单（本次新增）

| 路径 | bytes | SHA-256 |
|---|---:|---|
| `build/ci-pg-startup-selftest/20261030-233432-32a82d8d/selftest-summary.json` | 543 | `E45E1B2FE62642320A44F9C838C7D4A79BBAE18D0F296D3A5A097A9F55CF0EDF` |
| 同上 `manifest.sha256.json` | 300691 | `079B6384D03B9D06FB649ECBB271CAFBF57956E5728B2AD9D8D02DCC1A0FC4D4` |
| 同上 `console.log` | 27659 | `151B0FCAB19C0D519F3694C088DB2166F5E8B113896EDE902DC76C75CCD98CF0` |
| `build/ci-pg-startup-selftest/table-gate-20261031-074121/wrapper-summary.json` | 10974 | `3DC2D6622BFD3922916B774B6ACD17F3C65D950F47FC679ED8C165122846AE4C` |
| 同上 `manifest.sha256.json` | 19215 | `03A6D7D088B4DDEB929CCA7402D1131C7BF7099B0A79DCBC505D2EAD79DFF24C` |
| `build/ci-pg-startup-selftest/table-gate-console-20261031-074121.log` | 3596 | `01AE1760DB1BEFD99A6FD05A1B74427DD4F77975855566DF65E11C5E0AE2E205` |
| `build/ci-pg-startup-selftest/network/run-41140332081.json` | 14924 | `70F661E2BC4EE8205F6749B3DD0E1B462192B1C874BD521B961DE5894574D7BE` |
| `build/ci-pg-startup-selftest/network/jobs-41140332081.json` | 3465 | `D5A3BCA675A908845EA29EAFF7497916FF1D1BF7F1AF97B576AB2540C6CF311F` |
| `build/ci-pg-startup-selftest/network/run-41140326888.json` | 14913 | `15E5726E5ABD71CFF12D101CD1AAB4CB6393FB0C3BB49DA74D7E89EAE62EB3D1` |
| `build/caller-initial-ci-41140326888/job-raw.log` | 28659 | `88E66909107D2D39AF99C39D305ECFDB9EBAC9742C3DF0CA682ECC6B50DCA154` |
| `.../verify-v5/table/pg/postgres.log` | 456 | `32B03FF28FEBA990EB837601D780ED75BA2982AA9A1143CD4F1906F218CA571F` |
| `.../verify-v5/table/pg-start.log` | 111 | `432F5F526EE17D2C0810E25ABEDB696FBF81F3CCB3BCB345D10007658D6E7918` |
| `.../verify-v5/table/wrapper-summary.json` | 1112 | `036F44484DC6ED31DAC11F7C51267535356A8E1C68AA31EAFA70E2D234F3BDC7` |

首次失败 37 文件完整清单及逐项 SHA 见 `build/caller-initial-ci-41140326888/caller-evidence-sha256.csv`；本次复核 37/37 一致。
