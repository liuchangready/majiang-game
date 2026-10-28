# V5 PostgreSQL 补验修复交付（D-PG-SUP-01/02，第 2 轮返修）

日期：2026-10-28
工作树：`D:/majiang-wt/pg-sup-repair-ds-20261028-1545`
分支：`wt/v5-pg-sup-repair-ds-20261028-1545`
冻结基线：`6b6a5f9ac66938393523aa43a2224863a9e8d7cb`
第 1 轮代码提交：`f51f94988344766dcaa4d539aba961efde8674b1`
本轮返修代码提交：`f1aff487f505e8fd554a37bd850a90b1938b9f7d`

结论：**D-R1-01（expiry 回归未识别事务开始时钟）已修复并由两类变异证明；D-R1-02（D02 归因层次）已按原始堆栈逐项更正。D-PG-SUP-01 现可 CLOSED；D-PG-SUP-02 原始根因仍 UNRESOLVED，不把本轮绿色写成原始 4 项失败已销账。**

本轮未 push、未创建 PR/MR、未合并 `v5`、未部署、未修改 scheduler、未创建 `complete.flag`。仅修改 P5.2 Infrastructure 测试/fixture 与本报告；未修改生产 Infrastructure、SQL、TableRuntime、Contracts、csproj/sln、统一 verifier、既有 PG wrapper 或现有门槛。原始 expectedFiles 和边界保持不变。

---

## 1. 本轮审核输入与冻结

- 被审核 HEAD：`1b4ba83ee3c57eab042c29bc25cc2a9901b9941a`。
- 本轮返修代码提交：`f1aff487f505e8fd554a37bd850a90b1938b9f7d`，其父为第 1 轮交付链
  `1b4ba83 -> b7202fa -> f51f949 -> 6b6a5f9`。
- 原始失败证据、53/53 manifest 审计、历史 37/37 成功运行均保留原语境，未覆盖、未删除。
- 本轮只处理 D-R1-01、D-R1-02；未重开 G1，未处理 LocalHost/持久宿主/Unity/其它线程，未操作 bridge 线程。
- 真实 PG 16.10；未改 wrapper/pin/机器 trust/PATH；未触碰 GLM 55597 或其它 DS 集群。

本轮实际修改 3 个 Infrastructure 测试/fixture 文件 + 本报告：

```text
M  dotnet/tests/Mahjong.Infrastructure.Tests/P52TestSupport.cs
M  dotnet/tests/Mahjong.Infrastructure.Tests/PostgresTableStoreTests.cs
M  dotnet/tests/Mahjong.Infrastructure.Tests/PostgresSupplementRegressionTests.cs
M  doc/v5-重构/V5-PG-SUPPLEMENT-REPAIR-DELIVERY-20261028.md
```

`git diff --cached --check`、`git diff --check` 均 exit 0；新增测试仍由现有工程自动包含，未改 csproj/sln。

---

## 2. 原始失败证据（保持）

原始失败根：`D:/majiang-wt/cx-xml-1028/artifacts/caller-xml-review`。

| 文件 | SHA256 |
|---|---|
| `table/tests.xml` | `60E38E542CB0616201A3D21AE1A5E0EA5231D28C2AF4D2BF75892056EFAEE335` |
| `table/tests.log.stdout` | `B42269E8575F7E22AF1430DA0974CE3FE8CE0C1FFF05403FEEFBF1E2A554AF63` |
| `table/pg/postgres.log` | `9CF4D9F463ED5E8B2E45C8965465BACF7C062647AF5871C835726AB83D83E53B` |
| `table/wrapper-summary.json` | `107F2F663362F466A1EC1AC0C92CBFC6D42E70BBADCF825B7FF8B724D4BA08E9` |

原始结果仍为：37 total / 32 passed / 5 failed / 0 errors / 0 skipped / 0 not-run；runner 与 wrapper native exit 1；Infrastructure 239.055s；真实 PG 16.10，loopback 55782。历史 37/37 成功运行保留，不作为本轮结果覆盖。

---

## 3. D-R1-01：expiry 回归无法识别事务开始时钟（已修复）

### 3.1 上一轮缺陷的精确机制

上一轮两个 expiry 用例在看到 Lock 后，把 `issued` 改成 `clock_timestamp()-5000`、`expires` 改成 `clock_timestamp()-1000`。提交事务的 `xact_start` 只比这次回写早不足 1s，因此即使被测实现把 scope 时钟改为事务冻结的 `now()`，`now()` 仍晚于回写的 expiry，照样拒绝。测试只能证明“回写后的行已过期”，不能证明“放锁后使用新的数据库时钟”。

### 3.2 本轮修复

新增测试侧 helper `P52ScopeBoundary.CommitAfterExpiryCrossesTransactionStartAsync`（`P52TestSupport.cs`）：

1. blocker 用 `SELECT ... FOR UPDATE` 锁住 scope 行。
2. 启动真实 `Store.CommitAsync`，等待 `pg_stat_activity` 中出现唯一的 P52 scope 行锁等待。
3. 从 `pg_stat_activity` 精确读取被阻塞 backend 的 `pid`、`xact_start` 和当前数据库时间。
4. 构造 `issued < expiry`，且 `transactionStart < expiry <= DB-now-at-release`；只改 `expires_at_utc_ms`，不把 expiry 回写到 `txnStart` 之前。
5. 用独立 admin 连接轮询 `clock_timestamp()`，直到数据库时钟实际跨过 expiry；这是数据库观测门控，不是任意 sleep。
6. 断言 `txnStart < expiry <= releaseDbNow` 后才释放行锁。
7. 写 ignored evidence 文件，记录 `pid/issued/txnStart/expiry/observedDbNow/releaseDbNow`。
8. 返回 commit 结果，调用方继续断言 `ScopeRejected` + `RequestScopeExpired` + receipt/event/state 零副作用。

原测试名、原测试义务、targeted 13 项均保留。正常边界证据示例（final run）：

```text
issued=1793178028933
txnStart=1793178029106
expiry=1793178029107
releaseDbNow=1793178029279
=> issued < txnStart < expiry <= releaseDbNow
```

### 3.3 慢准备用例改为因果顺序断言

上一轮 `DelayedScopePreparation...` 使用 `scopeLifetime=3s` 和 `issuedAge<=1000ms`，会把 `IssueScope/AcquireLease/调度` 的机器速度绑进断言。本轮改为：

- 准备实际 2s，`ScopePreparationElapsed >= 2s`；
- 记录准备完成时刻 `ScopePreparationCompletedAtUtcMs`，断言 `issued >= preparationCompleted`（准备前取 issued 的变异必然失败）；
- scope 使用默认 1 小时有效期；
- 在 issue 之后额外等待 1.1s（`afterScopeIssueDelay`），使 scope age > 1s，但仍断言 commit 接受；
- 不再有任何 `issuedAge` 上限，后续合法调度超过 1s 不会仅因 issue age 失败。

### 3.4 变异验证（独占 ignored 副本，生产源码未改）

**变异 A：事务冻结时钟。** 从 `f1aff48` `git archive` 到 `D:/majiang-pg-tmp/ds-pg-clock-mutant-20261028-r2`，仅把两处 `clock_timestamp()` 改为 `now()`：

- `PostgresTableStore.State.cs` 的 `DbNowMsAsync`；
- `PostgresTableStore.Commit.cs` 的 `scopeBoundary`。

fresh Release **0 warnings / 0 errors**，PG16.10，wrapper exit 1：**40 total / 38 passed / 2 failed / 0 errors / 0 skipped / 0 not-run**。两个 expiry 用例均失败：

```text
Expected: ScopeRejected
Actual:   Committed
```

失败测试分别为 `ExpiryAtCommitBoundary_WaitsForLockThenUsesDatabaseClockAndRejects` 与
`ExpiryAtCommitBoundary_AfterDelayedPreparation_RejectsWithoutSideEffects`。变异副本的 boundary evidence 同样显示 `txnStart < expiry <= releaseDbNow`；因此红不是边界构造失败，而是冻结事务时钟被测试识别。变异副本未提交，未改动本树生产源码。

**变异 B：准备前取 issued。** 从 `f1aff48` `git archive` 到 `D:/majiang-pg-tmp/ds-pg-issued-mutant-20261028-r2`，仅在 `P52TestSupport.CreateAsync` 增加准备前的 `issuedBeforePreparation`，并在准备后使用它。fresh Release **0 warnings / 0 errors**，PG16.10，wrapper exit 1：**40 total / 39 passed / 1 failed / 0 errors / 0 skipped / 0 not-run**。失败信息为：

```text
The scope was issued before preparation completed:
issued=1793178861618; prepared=1793178863640
```

该红来自因果顺序断言，不是 issue-age 上限；同时该用例仍完成 1.1s 后置延迟，说明后续合法调度超过 1s 本身不会被判失败。

### 3.5 统一负例矩阵

| 场景 | 期望 | 实际证据 |
|---|---|---|
| 正常准备、正常 scope | 接受 | r2 full 40/40 |
| 2s 慢准备 + 1.1s post-issue 调度 | 接受 | 新用例 40/40 中 PASS |
| 准备前取 issued 的变异 | 失败并指出 issued<prepared | 变异 B，39/40，1 failed |
| 锁等待期间跨 expiry，txnStart<expiry<=release | 拒绝且零副作用 | 原测试 + delayed 测试，r2 40/40，boundary evidence |
| 事务冻结时钟变异 | 失败并显示 Committed | 变异 A，38/40，2 failed |
| 锁等待期间 revoke | 拒绝且零副作用 | `RevocationAtCommitBoundary...` PASS |
| 拒绝路径 | receipt/event/state 零 | `AssertNoSideEffectsAsync` 保留 |

没有 skip、空测试、放宽断言、统一抬高 timeout 或旧产物复用。两类变异只存在于 ignored 独占副本；本树生产源码与 normal test 结果分开报告。

---

## 4. D-R1-02：D02 归因层次更正（报告已修正，D02 仍 UNRESOLVED）

### 4.1 三个 Npgsql 堆栈逐项对应

原始 `tests.xml` 中三个 Npgsql 失败均为连接建立/认证阶段，但调用层不同：

| 原始测试 | 堆栈入口 | 调用层 |
|---|---|---|
| `RevocationAtCommitBoundary...` | `ResetAsync -> PostgresTableTestEnvironment.ExecuteAsync -> OpenAsync` | 测试 helper 的 Reset 连接 |
| `StoreFailureBeforeCommit...` | `ResetAsync -> PostgresTableTestEnvironment.ExecuteAsync -> OpenAsync` | 测试 helper 的 Reset 连接 |
| `TerminalOutbox_IsAtomicAndConsumerIsIdempotent` | `PostgresTableStore.IssueScopeAsync -> PostgresTableStore.OpenAsync -> NpgsqlDataSource.OpenConnectionAsync` | **生产** `IssueScopeAsync` 的连接建立 |

因此更正为：**3 项都发生于连接建立，其中 2 项在测试 helper，1 项在生产的 IssueScope 连接建立；不能把第三项归为测试 helper 缺陷，也不能据此断言生产存储事务/时钟已正确**。第 4 项 ActorCommit 原始断言无 `ReasonCode/Detail`，仍无法归因。

### 4.2 资源观测的边界

final run 观测到 PostgreSQL backend 峰值 21、TCP TIME_WAIT 峰值 140、最低可用内存 267.6MB；这只排除 **该 fresh 运行窗口内** 的连接数/端口饱和，不能证明原始 06:24 失败窗口没有饱和，也不能证明两次压力相同。原始窗口仍缺少当时的宿主机采样、`pg_stat_activity` 和 actor `Detail`。宿主存在 8 个非本任务的长期 CPU 自旋进程、低内存区间，但这只能作为待证环境因素，不作为原始根因结论。

### 4.3 D02 结论与下一探针

**D-PG-SUP-02：UNRESOLVED（原始根因）。** 本轮没有修改生产代码、没有统一抬高 timeout、没有盲 retry、没有把新绿 run 称为原始失败已修复。新增 failure-only 诊断在 r2 与 final 两次 fresh 运行中均未触发；原始失败证据仍保留。

若再次发生，下一探针：

1. 读取 `fixture-failure-*.txt` / `actor-failure-*.txt`，保留 `phase/elapsedMs/ReasonCode/Detail` 与 3s DB probe；
2. 抓取 `pg_stat_activity` 的 `wait_event_type/query`、backend 数、`max_connections`、postmaster 启动时间和客户端 TCP 状态；
3. 只有能证明 actor 的 `CommitFailed` 同样来自连接建立且 DB probe 同步失败时，才可归入连接建立层；否则继续 UNRESOLVED。

---

## 5. 本轮命令、native exit 与计数

环境：`D:/Unity/dotnet/dotnet.exe` 8.0.426；PG zip `D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip`，SHA256 `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`。

### 5.1 fresh Release build

```powershell
& D:/Unity/dotnet/dotnet.exe build `
  dotnet/tests/Mahjong.Infrastructure.Tests/Mahjong.Infrastructure.Tests.csproj `
  -c Release --no-incremental --nologo -v:minimal
```

实际：exit 0，**0 warnings / 0 errors**；最终测试 DLL SHA256 `86DDF7055E7A8F737BEEDF58D199C4C3B03091BE5CBE9D6D37EE41DD90E1BA16`。日志：`artifacts/build-ds-repair-r2.log`。

### 5.2 r2 真实 PG wrapper（未跑 unified）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55822 -Root artifacts/pgsup-r2 -LogDir artifacts/pgsup-r2-run `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip
```

实际：wrapper exit 0；full **40/40**（0 failed/0 skipped/0 errors/0 not-run）；targeted **13/13**；crash writer killed；PG immediate restart；cold reader；空库 restore 全 PASS；ownership cleanup PASS。

### 5.3 final：同一 `-RunUnifiedVerification`，未改入口

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55822 -Root artifacts/pgsup-r2-final `
  -LogDir artifacts/pgsup-r2-final-logs `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -RunUnifiedVerification -UnifiedLogDir artifacts/pgsup-r2-final-logs/unified `
  -LobbyRoot D:/majiang-pg-tmp/ds-pg-sup-repair-r2-lobby -LobbyPort 55823
```

实际：最外层 `P52-TABLE-POSTGRES-PASS`，native exit 0；全部 stage PASS；full **40/40**；targeted **13/13**；unified 11 个测试工程 **2297/2297**；companion **4/4**；12 DLL closure；solution build **0 warnings / 0 errors**；0 skip/failed/errors/not-run。

### 5.4 计数

| 阶段 | selected | passed | failed | skipped | errors | not-run | native exit |
|---|---:|---:|---:|---:|---:|---:|---:|
| Infrastructure full（原 37 + 新 3） | 40 | 40 | 0 | 0 | 0 | 0 | 0 |
| Infrastructure targeted（原 13） | 13 | 13 | 0 | 0 | 0 | 0 | 0 |
| unified 11 test projects | 2297 | 2297 | 0 | 0 | 0 | 0 | 0 |
| companion fixtures | 4 | 4 | 0 | 0 | 0 | 0 | 0 |

unified 分项：444 + 92 + 182 + 80 + 149 + 529 + 40 + 89 + 400 + 152 + 140 = **2297**，即原 2294 + 3，未删分母。

### 5.5 PG、恢复与清理

- 表集群 PG **16.10**，loopback **55822**，data directory `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final/data`；Lobby PG **16.10**，loopback **55823**。
- 开始/结束端口均无监听；表集群与 Lobby 集群均由 ownership marker 停止并删除；未触碰其它服务。
- crash marker：`stateVersion=1, lastEventSeq=2, eventCount=2`，writer pid 63308 强杀后未发布。
- PG immediate restart：`08:59:46.725122Z -> 09:02:11.596223Z`，automatic recovery 3 行。
- cold reader：`recovered=true, stateVersion=1, lastEventSeq=2, terminal=true, receiptStatus=Applied, cheatStateRestored=true, outboxPayloadMatches=true, recoveryOwnerEpoch=2`。
- 空库 restore：`events=2, recovered=true, receiptStatus=Applied, outboxPayloadMatches=true, recoveryOwnerEpoch=3`。

### 5.6 一次操作路径错误（保留，不计为产品/测试失败）

第一次 final 调用把 `-Root` 与 `-LogDir` 都指向 `artifacts/pgsup-r2-final`，wrapper 在创建 LogDir 后正确以 “non-empty and has no P5.2 ownership marker” 拒绝，exit 1。该目录只含本次 wrapper 日志；已原样移动到 `artifacts/pgsup-r2-final-logdir-collision` 保留，随后用独立 `-LogDir artifacts/pgsup-r2-final-logs` 成功重跑。该失败没有计入 40/40 或 2297，也没有被隐藏。

---

## 6. 关键证据与 SHA256

final 全量摘要：`artifacts/pgsup-r2-final-logs/unified/summary.log`（2297/2297、companion PASS）。

| 证据 | 绝对路径 | SHA256 |
|---|---|---|
| 原始失败 XML | `D:/majiang-wt/cx-xml-1028/artifacts/caller-xml-review/table/tests.xml` | `60E38E542CB0616201A3D21AE1A5E0EA5231D28C2AF4D2BF75892056EFAEE335` |
| final full XML（40/40） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/tests.xml` | `E8549224F143B07CD977B157F00818108D0242FAA2A26190406F76B3FF4C54FB` |
| final targeted XML（13/13） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/tests-targeted.xml` | `95FD259E6DBDEB51DFF74BC9CC22E7EAFF712E026B18BEAFDC988E4F62FCA13F` |
| final wrapper summary | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/wrapper-summary.json` | `97FE094FBFC1DD1843C33990AD6A101C46A6B1DE4B94FFE03353BD025F7E65A3` |
| final PG log | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/pg/postgres.log` | `6747656A24A9FD996F6F6BD3D06B625D9A8F2FEC0B7DB48721B2ECD3C7EE68B4` |
| unified summary（2297/2297） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/unified/summary.log` | `D6466B10BAB419482E7E69E44EAF3BB587A70545ECCEF878C50384D9E081D1A7` |
| unified PG wrapper summary | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/unified/pg/wrapper-summary.json` | `5FF3B59A5BCF04B26C7C4C004B6711182D618819FAAE8EEDFA13AE0FB05DC3DC` |
| Infrastructure direct XML | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/unified/direct-xml/Mahjong.Infrastructure.Tests/direct-results.xml` | `93B33B5E295B6EED8C5B2A27FD9BF3D749F69B753CC4607588D86505AA00C7FF` |
| companion meta（4/4） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/unified/companion-fixtures.meta.txt` | `FED8CAE94011B0A96D367A279EB09D983FAAB6E62C431EA0FD25436AE3498B69` |
| 12-DLL plugin closure | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/unified/plugin-copy.log` | `5DD4AF63AD4C5B2777DAB35328878BBAD92C69F45E5419977C6443E8A50CA193` |
| solution build | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/unified/sln-build.log` | `36A951A258451042B648463C473FA00336467101CE5D29E6CCD7B8181711E7DD` |
| crash marker | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/crash-commit-marker.json` | `48976B9AE73B3611851F06BA9B143282F5818943C34C6E562F021A2AF3A2E4CD` |
| crash reader | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/crash-reader-result.json` | `C879E5C627EBC0D174E3A98F826F38A40007C9DDD3C1BA5214378FE063B121B4` |
| restore reader | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-r2-final-logs/restore-reader-result.json` | `B0D075D5336AFC4B5720781FBDEC3E08D9641D8236111460F75BB83D19FCCA86` |
| 冻结时钟变异 XML（38/40） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/ds-clock-mutant-r2/tests.xml` | `4C073B02F796D3F9DF9B5E4D13CF5203BFDEEA8E5B1EA62A707BD01D58005B64` |
| 冻结时钟变异 console | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/ds-clock-mutant-r2/tests.log.stdout` | `CBAB18036C0214C00BF835E17F709FE4FC050385523030B627352E243C4D7C96` |
| 冻结时钟变异 summary | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/ds-clock-mutant-r2/wrapper-summary.json` | `2D4AD447A6C120C6955D7BD72D725D50E779E6EB324D9144E1A3D1C53BE169D9` |
| 冻结时钟变异 State diff | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/ds-clock-mutant-r2/mutation-state.diff` | `3F2758EBADFF3D8AA38FF06E2D5929D4C75ED154DE4159AC342B3E498AA1E9DE` |
| 冻结时钟变异 Commit diff | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/ds-clock-mutant-r2/mutation-commit.diff` | `0B4CA876354FA7965FA2FD213D44D750BC4A4960A53FBBF2A94F5931EE1759B8` |
| 准备前 issue 变异 XML（39/40） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/ds-issued-mutant-r2/tests.xml` | `ED10C7E628B5B58E551CC9BC67E500A7D2AA48015E737560D56905F924E30593` |
| 准备前 issue 变异 console | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/ds-issued-mutant-r2/tests.log.stdout` | `2743A2FAD814A6A5E6BE1607DC2295F22F9153920D36317052AA142599DDF21A` |
| 准备前 issue 变异 summary | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/ds-issued-mutant-r2/wrapper-summary.json` | `373A9584EA97D933D7F53298280452E429AEF6D6A3DCDEDB462B836256B2EBF9` |
| 准备前 issue 变异 diff | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/ds-issued-mutant-r2/mutation-issued.diff` | `828B1EC7FD73274733F4693C7DE060AA51596552841899116A83A705F42F2012` |

normal run（`artifacts/pgsup-r2-run`）和 final run 的 scope boundary evidence 均保留；例如 final：
`artifacts/pgsup-r2-final-logs/tests/scope-expiry-boundary-*.txt`，每份都记录 `issued < txnStart < expiry <= releaseDbNow`。

---

## 7. 返修映射与结论

| 缺陷 | 状态 | 修复/更正 | 验收 |
|---|---|---|---|
| D-R1-01 expiry 未识别事务开始时钟 | CLOSED | helper 读取被阻塞 backend 的 `xact_start`，构造 `txnStart < expiry <= releaseDbNow` 并在跨界后放锁；新增慢准备因果断言，移除 issue-age 上限 | normal 40/40；冻结时钟变异 38/40、两 expiry 均 `Committed` 失败；准备前 issue 变异 39/40、因果断言失败 |
| D-R1-02 D02 归因层次 | CLOSED（报告） | 逐项列出 2 项测试 helper + 1 项生产 IssueScope 连接建立；当前窗口观测不冒充原失败窗口排除证据 | 报告 §4.1/4.2/4.3/§7 一致；D02 仍 UNRESOLVED，旧失败保留 |
| D-PG-SUP-01 | CLOSED | fixture 准备后签发 scope；真实锁边界/数据库时钟；原测试与 3 项新回归 | full 40/40、targeted 13/13 |
| D-PG-SUP-02 | UNRESOLVED | 不修改生产、不抬高 timeout、不盲 retry；保留原始失败与新增诊断 | r2/final 均未触发诊断；原始根因未归因 |

最终 tracked HEAD 由提交后 `git rev-parse HEAD` 核验；代码修复提交为 `f1aff487f505e8fd554a37bd850a90b1938b9f7d`。`git diff --check 6b6a5f9..HEAD` 必须为 exit 0；工作树 tracked clean。

G1 保持 APPROVED；G2–G5 未批准；V4 仍生产；181 classified 仍未覆盖；远端提交 CI **NOT_RUN**；未 push/建 PR/MR。Unity/Android 真机/iOS/跨物理网络/完整 LocalHost/持久宿主/整场/远端 E2E 非本任务范围。

---

## 8. Codex 调用者第 2 轮独立验收（2026-10-28）

**本任务范围 PASS。D-R1-01、D-R1-02 关闭；D-PG-SUP-01 CLOSED；D-PG-SUP-02 的原始四项失败根因仍 UNRESOLVED。** 上轮明确允许 D02 保留未归因，本次批准测试时序修复、诊断与如实报告，不把新的通过结果当作原始四项失败已修复。同批 LocalHost、持久宿主、Unity 插件任务的状态不用于否决本组件，也不随本次批准。

审核并实际测试的 HEAD 为 `8d54e4f22480ebcaf306a4372eaa82ec79465b39`；本节之后的调用者提交只增加本报告，不改变已测试的代码。相对冻结基线 `6b6a5f9`，净范围为三份 Infrastructure 测试/fixture 与本报告，生产代码、SQL、工程文件、wrapper 和统一 verifier 均无改动。审核前 tracked clean；`git diff --check 6b6a5f9..HEAD` exit 0。

### 8.1 本工作区独立实跑

调用者重新检查独占端口 55842/55843 无监听，使用原 wrapper、原包 pin、独立目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55842 -Root artifacts/cx-r2-pg -LogDir artifacts/cx-r2-review `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -RunUnifiedVerification -UnifiedLogDir artifacts/cx-r2-review/unified `
  -LobbyRoot D:/majiang-pg-tmp/cx-r2-sup-lobby-55843 -LobbyPort 55843
```

最外层 native exit **0**，`P52-TABLE-POSTGRES-PASS`。真实 PostgreSQL **16.10**；fresh Release 测试与 CrashWorker 构建成功，0 warning / 0 error；测试 DLL SHA256 为 `29A2D80F708229237EBED5625C11A314E7341482C0965347DBBEBF76822C27BD`。solution 构建同为 0 warning / 0 error。

| 阶段 | selected | passed | failed | errors | skipped | not-run | native exit |
|---|---:|---:|---:|---:|---:|---:|---:|
| Infrastructure 独立 full | 40 | 40 | 0 | 0 | 0 | 0 | 0 |
| 原定向测试 | 13 | 13 | 0 | 0 | 0 | 0 | 0 |
| 原统一入口的 11 个 direct runner | 2297 | 2297 | 0 | 0 | 0 | 0 | 0 |
| companion fixture 门槛 | 4 | 4 | 0 | 0 | 0 | 0 | 0 |

独立 full 耗时 83.639s；统一入口的 Infrastructure 40 项耗时 69.610s、TableService 152 项耗时 91.383s。原 37 个测试名全部保留，新增 3 项有意义回归；统一总数仍为 2294 + 3，未删分母。12 DLL 闭包通过。

writer pid 52340 在 durable commit 后、publication 前强杀；PG immediate restart 的 postmaster 时间从 `09:22:22.789863Z` 变为 `09:25:23.534851Z`，自动恢复记录 3 行。独立 cold reader 验证 `stateVersion=1,lastEventSeq=2,terminal=true,receiptStatus=Applied,cheatVersion=1,recoveryOwnerEpoch=2`；空库 restore 验证 2 个事件及 `recoveryOwnerEpoch=3`，outbox/cheat 恢复一致。两个集群均经 ownership 校验清理，最终目录不存在、55842/55843 无监听。

### 8.2 代码、负例与证据独立核对

- 阅读完整本任务净 diff：scope 在准备后签发；慢准备实际 2s、issue 后合法调度 1.1s，由因果顺序断言检查；原 expiry 测试名和零副作用断言保留。
- 实跑边界记录确认 `txnStart < expiry <= releaseDbNow`。例如 `1793179418942 < 1793179418943 <= 1793179419115`；被阻塞 backend 唯一，scope issue 早于 expiry，commit 放锁前尚未完成。
- 本轮未再次执行变异；独立审计 receiver 本轮原生 XML、console、manifest 和源码副本。冻结时钟变异恰为 38/40，两个 expiry 测试均因实际 Committed 而失败；提前取 issued 变异恰为 39/40，唯一失败为因果顺序断言。每份副本对照 385 个归档输入，忽略纯换行差异后，分别只有声明的两处生产时钟修改和一处测试 fixture 修改，没有其它源码变化。
- 直接重读原始 XML 堆栈：Revocation 与 StoreFailure 两项在测试 helper 建连；TerminalOutbox 一项在生产 IssueScope 建连。报告更正与原证据一一对应。原始 53/53 文件重哈希一致，未用 fresh 资源观测排除原始失败窗口的假设；D02 仍 UNRESOLVED。
- 调用者审计脚本 `artifacts/cx-r2-audit.ps1` 完成交付证据 **405 checks / 0 failures**；`artifacts/cx-r2-runtime-audit.ps1` 合并独立实跑证据后为 **650 checks / 0 failures**。涵盖报告 23 个哈希、原始 53 份、receiver final 194 份、两个变异各 55 份、本次 wrapper 194 份 manifest 条目，以及本次 11 个 XML 节点数、console、meta/native exit、XML SHA、测试 HEAD、恢复/清理、companion 和闭包。

独立实跑和审计证据根：`D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/`。

| 证据 | 相对上述证据根的路径 | SHA256 |
|---|---|---|
| caller full XML | `cx-r2-review/tests.xml` | `541EAF3BD7130D42E7C66547CEACD88DE63412A7DC59DF403758DF96EC4BD432` |
| caller targeted XML | `cx-r2-review/tests-targeted.xml` | `C80987CC7E6A902E6AF11BD6B6EF76B8B549423ACF9BE103BEF4EF576DD673F2` |
| caller unified summary | `cx-r2-review/unified/summary.log` | `B07B8215CEB09DC2646BA5EFB10C946577934E85A63FB2E5EBA9BC0828D10762` |
| caller wrapper summary | `cx-r2-review/wrapper-summary.json` | `3D6B939F07EECCA309DAF85B43473253F914AC6C10725972D569342DCBC1FF8B` |
| caller 194-file manifest | `cx-r2-review/manifest.sha256.json` | `32D8D5F79AA9359A0770414A828BA095CE135CC51B8BA674C2D3449F47B19CC8` |
| caller 650-check audit | `cx-r2-runtime-audit.json` | `07EAECE89318940D40255AB2B44781D025714BC1D130C8C71B51C70A3213E720` |
| mutant source comparison | `cx-r2-mutant-source-audit.json` | `81226F4625E2AF4B9BC702B0787663EC286794925166AA17D5D2D4A699D5129B` |

### 8.3 批次发布边界

本任务沿用 `thread_20261028_154729_a893` 与既有调用者会话，由调用者批准后统一推送现有唯一草稿 [PR20](https://github.com/liuchangready/majiang-game/pull/20)，源分支 `feat/v5-post-g1-batch`，目标 `v5`。本轮检查时同批已接受任务均已同步；其余三项仍返修，未纳入。未新建任务/会话或转派，没有额外子任务 MR。

本文 §1–7 的“未 push”描述 receiver 交付时状态；最终推送 SHA、PR 同步结果由 bridge `record_merge_request` 记录。新头远端 CI 须按其自身运行验证，当前本地结果不把新 CI 标为通过。G1 不重开，G2–G5 保持未批准；V4 仍生产，181 classified 缺口和所有设备/跨物理网络缺口保留。未合并 `v5`、未部署、未改 scheduler 或 `complete.flag`。

---

## 9. 后续返修：D-CI-DELAY-01 exact-head 远端 CI 的计时边界（2026-10-28，新任务）

### 9.1 任务来源与边界

本线程 `thread_20261028_154729_a893` 的 PG 补验已在 `8a0c8cb` 接受并归档。桥接 `rework-controller.js` 明令 completed terminal、禁止晚到 rework 复活，因此本任务是**新信息后续**而非原 PG 补验的复制：接受后首次对 exact head 跑远端双 CI，才暴露下述 1999ms 计时边界。原始接受记录、原始失败证据和原线程结论均保留，不重做 D-PG-SUP-02 归因、旧 CI 审查或 LocalHost。

工作树：`D:/majiang-wt/pg-delay-followup-ds-20261028-1822`
分支：`wt/v5-pg-delay-followup-ds-20261028-1822`
冻结基线：`8a0c8cbf73f30fe3bd943e3965bbef40224f49aa`（已审查 PR20 发布头）
上游：`origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`（基线祖先，HEAD 领先 120、落后 0）
本轮代码提交：`9be3ec5236b33b744583fe52636cd6f1dacc8e73`

本轮只改 `expectedFiles` 中的两个 Infrastructure 测试/fixture 文件，并在本报告追记；没有额外路径：

```text
M  dotnet/tests/Mahjong.Infrastructure.Tests/P52TestSupport.cs
M  dotnet/tests/Mahjong.Infrastructure.Tests/PostgresSupplementRegressionTests.cs
M  doc/v5-重构/V5-PG-SUPPLEMENT-REPAIR-DELIVERY-20261028.md
```

未修改生产 Infrastructure、SQL、TableRuntime、Domain、Client、Wire、统一 verifier/CI、csproj/sln、Unity 或二进制；未 push、未建 PR/MR、未合并 `v5`、未改 scheduler/`complete.flag`、未部署。P4.1c/d 的 paused 与 P4.1a HTTP500 failed 保留原状，本任务不恢复、不复制、不绕过。

### 9.2 冻结核实

- `HEAD = 8a0c8cb...`、`branch = wt/v5-pg-delay-followup-ds-20261028-1822`、tracked status 干净。
- `git merge-base --is-ancestor origin/v5 HEAD` 为真；`8fd7c6cb..HEAD = 120`，`HEAD..origin/v5 = 0`。
- 工作树确从 `origin/v5` 建立并 ff-only 到已审查 PR20 发布头；未更换基线、未覆盖其它工作树。
- 冻结记录写入 ignored 的 `artifacts/freeze-baseline.json`。

### 9.3 缺陷记录

```text
[ID] D-CI-DELAY-01
严重级别：MAJOR
位置：dotnet/tests/Mahjong.Infrastructure.Tests/PostgresSupplementRegressionTests.cs:31
      PostgresSupplementRegressionTests.DelayedScopePreparation_EstablishesScopeAfterPreparationAndCommits
      dotnet/tests/Mahjong.Infrastructure.Tests/P52TestSupport.cs:540 附近
      P52Fixture.CreateAsync 的 scopePreparationDelay / afterScopeIssueDelay
复现：exact head 8a0c8cb 的远端双 CI：
      https://github.com/liuchangready/majiang-game/actions/runs/40665584740 (pull_request)
      https://github.com/liuchangready/majiang-game/actions/runs/40665578400 (push)
      本地受控提前唤醒探针与去掉单调补足的负变体（见 9.6）。
实际：PR run 的 PG 为 Total 40 / Passed 39 / Failed 1 / native 1；
      push run 的首轮 PG 40/40、target 13/13、crash/restart/reader/backup restore PASS，
      统一阶段 Infrastructure 39/40，最终 native 1。
      两份 XML 的唯一失败为同一方法、同一原始 message：
      The fixture did not actually delay preparation: preparationMs=1999
      stack 指向 8a0c8cb 的 PostgresSupplementRegressionTests.cs:31。
期望：fixture 以单调 Stopwatch 真实测得准备至少 2.000s；scope 只在准备完成之后签发；
      issue 后至少 1.100s 的合法调度仍提交成功；过期/撤销边界仍拒绝且零副作用；
      scope-before-preparation 变体仍红。
证据：本树 `artifacts/caller-evidence/`：PR tests.xml、push unified direct-results.xml、
      两个 log-failed、sha256.csv；更完整只读 bundle：
      `D:/majiang-wt/codex-p54b-review-20261028-1818/artifacts/caller-p54b-review/ci-40665584740`
      与 `.../ci-40665578400`。SHA256 见 9.8。
修复要求：不得仅把 1999 放宽为允许值；不得删掉严格因果/有效期断言；不得把展示 ms 加 1
      或四舍五入冒充真实 elapsed；不得固定扩超时、任意 sleep、伪造 Stopwatch 或跳过负例。
验收断言：真实 PG full 42/42；新增提前唤醒不能提前签发 scope；取消可结束；
      准备前取 issued 变体仍红；事务冻结时钟变体仍红；targeted 13/13、crash/restart/
      reader/backup restore 与统一 verifier 全 PASS。
```

### 9.4 1999ms 与 fixture 代码的机制

`P52Fixture.CreateAsync` 在慢准备处使用 `await Task.Delay(scopePreparationDelay)`，测试再用一层 `Stopwatch` 量该段并断言 `ScopePreparationElapsed >= 2s`。`Task.Delay` 是计时器通知，不是精确时长保证；续体可以在 Stopwatch 读到 2.000s 之前被唤醒，实测值落在 `[1999, 2000)` ms，`(long)TotalMilliseconds` 显示 1999，严格下界因此失败。这是**测试 fixture 的测量边界**，不是生产计时、租约或 scope 逻辑的缺陷，也不能据此称远端已绿、不能拿历史 6b6a5f9 绿色覆盖 8a 的失败，更不能无证据称 flaky。

同一类边界还存在于 `afterScopeIssueDelay`：旧实现同样只 `Task.Delay(1100)` 且不测量，后面只断言 scope age `>= 1000`，等于默许最多 100ms 的提前返回。该路径本轮一并收紧。

本地没有把“原样 `Task.Delay(2000)` 是否稳定复现 1999”当作通过条件；机制由远端原始 XML 和**受控提前唤醒**测试确定性证明，未复现的部分如实保留为未复现，不写成 PASS。

### 9.5 修复与断言映射

`P52TestSupport.cs` 新增 `DelayAtLeastAsync`：

1. 启动单调 `Stopwatch`；
2. 每次计算 `remaining = minimum - watch.Elapsed`，不足则继续 `await wait(remaining, ct)`；
3. 每次 await 不超过剩余下界、可取消；达到下界才返回；
4. 默认 `wait = Task.Delay`，测试可注入受控提前唤醒源，但最小 elapsed 始终由真实 Stopwatch 守住。

两个 delay 都改走该 helper，并分别暴露真实测量值 `ScopePreparationElapsed` 与 `AfterScopeIssueElapsed`。主用例保留并加强：

| 原义务 | 当前断言 |
|---|---|
| 实际至少 2s 准备 | `ScopePreparationElapsed >= 2s`（单调测量，不足继续 await） |
| issue 在 preparation 之后 | `Scope.IssuedAtUtcMs >= ScopePreparationCompletedAtUtcMs` |
| 至少 1.1s issue 后调度 | `AfterScopeIssueElapsed >= 1.1s` 且外层 `elapsed >= 3.1s`，scope age `>= 1s` |
| 有效 scope 真实提交 | `result.IsCommitted`、`state_version=1`、event count 一致 |
| 过期/撤销零副作用 | 原 expiry/revocation 边界用例与 `AssertNoSideEffectsAsync` 保留 |

`PostgresSupplementRegressionTests.cs` 新增两项正式用例（只在本文件）：

- `EarlyWakePreparationDelay_CannotIssueScopeBeforeMinimumElapsed`：前五次计时通知立即返回（受控提前唤醒），断言 fixture 仍测得准备/后置延迟下界，且 scope 只在 preparation 完成后签发。若 guard 被移除，该用例确定性失败，而不是依赖机器快慢。
- `DelayAtLeast_IsCancellableBeforeTheFullMinimumElapses`：30s 下界在取消后迅速结束，证明有界等待可取消；10s 只是活性上界，不是性能门槛。

没有把 1999 改为允许值、没有加 1ms/四舍五入、没有固定加 sleep、没有改生产计时，也没有放宽 scope-before-prepare 或 expiry 断言。

### 9.6 负例矩阵（真实 PG，逐项实跑）

三个负变体均从 `9be3ec5` 的 `git archive` 隔离副本运行，只修改声明的目标文件，主工作树未被改动；另以同 HEAD 的 pristine `git archive` 逐文件比较，除声明的目标文件外无差异（`artifacts/mutants-source-comparison.txt`），运行后 wrapper 均 `ownership-verified cluster removed`。

| 场景 | 修改 | 结果 | 失败证据 |
|---|---|---:|---|
| A. 去掉单调补足（回到单次 `Task.Delay`） | `P52TestSupport.cs` 的 `DelayAtLeastAsync` 只 await 一次 | **41/42，1 failed** | `EarlyWake...`：`An early timer notification shortened preparation: preparationMs=0` |
| B. 准备前捕获 issued | `CreateAsync` 在准备前取 `issuedBeforePreparation`，准备后仍使用它 | **40/42，2 failed** | `EarlyWake...`：issued=1793185128437 < prepared=1793185128741；`Delayed...`：issued=1793185129307 < prepared=1793185131319 |
| C. 事务冻结数据库时钟 | 生产 `PostgresTableStore.State.cs` 的 `DbNowMsAsync` 与 `Commit.cs` 的 scopeBoundary 由 `clock_timestamp()` 改为 `now()` | **40/42，2 failed** | 两个 expiry 用例均为 `Expected: ScopeRejected` / `Actual: Committed` |
| 正常修复 | 无 | **42/42** | 见 9.7 |
| 过期/撤销 | 无 | PASS 且零副作用 | 原边界用例保留 |
| 取消 | 无 | PASS | 30s wait 在取消后结束 |

变体 A 的 0ms 与远端 1999ms 是同一缺陷类：把一次计时器通知当作已满足时长。A 证明新用例能确定性抓住该缺陷；C 证明新修复没有放行事务冻结时钟；B 证明因果断言仍有效。三个变体都只跑到 tests 阶段即按预期失败，targeted/crash/unified 对变体为 **NOT_RUN**，不冒充通过。

### 9.7 本轮真实运行与计数

环境：`D:/Unity/dotnet/dotnet.exe` 8.0.426；PG zip `D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip`，SHA256 `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`；真实 PostgreSQL 16.10；表端口 55864，Lobby 端口 55865，均 loopback 独占。

fresh Release build（最终 wrapper 的 fresh-build 阶段执行同形命令）：

```powershell
& D:/Unity/dotnet/dotnet.exe build `
  dotnet/tests/Mahjong.Infrastructure.Tests/Mahjong.Infrastructure.Tests.csproj `
  -c Release --no-incremental --nologo -v:minimal
```

实际 exit 0，0 warnings / 0 errors；测试 DLL SHA256 `E56B70FECE952DC5425653682E4AADC9AA63150FA9C4E32160F18CA331B00D55`。

最终统一入口（未改脚本）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55864 -Root artifacts/pg-delay-final/pg `
  -LogDir artifacts/pg-delay-final/logs `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -RunUnifiedVerification -UnifiedLogDir artifacts/pg-delay-final/logs/unified `
  -LobbyRoot D:/majiang-pg-tmp/ds-pg-delay-final-lobby-55865 -LobbyPort 55865
```

实际最外层 `P52-TABLE-POSTGRES-PASS`，native exit 0，全部 stage PASS：

| 阶段 | selected | passed | failed | skipped | errors | not-run | native exit |
|---|---:|---:|---:|---:|---:|---:|---:|
| Infrastructure full（原 40 + 新 2） | 42 | 42 | 0 | 0 | 0 | 0 | 0 |
| 原 targeted（PostgresTableStoreTests） | 13 | 13 | 0 | 0 | 0 | 0 | 0 |
| 统一 11 个测试工程 | 2299 | 2299 | 0 | 0 | 0 | 0 | 0 |
| companion fixtures | 4 | 4 | 0 | 0 | 0 | 0 | 0 |

统一分项：80 + 400 + 444 + 92 + 182 + 140 + 149 + 89 + 529 + 152 + 42 = **2299**（原 2294 + 5，未删分母）。solution build 0 warnings / 0 errors；12 个 DLL 的 Unity plugin closure PASS；companion 4/4。

恢复与清理：

- crash writer pid **50368** 在 durable commit 后、publication 前强杀；marker `stateVersion=1,lastEventSeq=2,eventCount=2`。
- PG immediate restart 的 postmaster 时间 `10:38:06.951973Z -> 10:39:45.027256Z`，automatic recovery 3 行。
- cold reader：`recovered=true,stateVersion=1,lastEventSeq=2,terminal=true,receiptStatus=Applied,cheatVersion=1,recoveryOwnerEpoch=2`。
- 空库 restore：`events=2,recovered=true,receiptStatus=Applied,recoveryOwnerEpoch=3`。
- 表集群与 Lobby 集群均经 ownership marker 停止并删除；55864/55865 最终无监听，目录不存在。运行期间未触碰其它 caller 的 57541–57544、Unity 集群、共享 DB 或未知进程；本机现存 `codex-integrated-r3-20261024` 的 postgres 进程不属本任务，未操作。

**被取代的预跑（不计验收）**：最终断言修订前另有一次同代码树 42/42 的 unified 预跑（`artifacts/pg-delay-followup`）。该预跑仍含 `delayCalls >= 6` 的调用次数下界，理论受极慢调度影响；已改为只以单调 elapsed 为下界并完成上表的最终重跑。预跑只作过程留痕，不用于验收。

### 9.8 关键证据与 SHA256

最终证据根：`D:/majiang-wt/pg-delay-followup-ds-20261028-1822/artifacts/pg-delay-final/`。

| 证据 | 路径 | SHA256 |
|---|---|---|
| final full XML（42/42） | `logs/tests.xml` | `D6FA5895E5AFE5A96474F20160522759181F30653D116C98F80A8170F524D998` |
| final targeted XML（13/13） | `logs/tests-targeted.xml` | `8CF5362FE6F82C816B0CC86C87D0E3C6E2A57FAFC8B0D2ABF22DEECF7D55F60E` |
| wrapper summary | `logs/wrapper-summary.json` | `E7C2F42ADE7F4A6592A537303C0F2F90C71A4ABDBB74BB2D2C80DFCD0F83F965` |
| full console | `logs/tests.log` | `2DF91FAC04D7FBA0B8A6C0BB0FC55E8F7D09340EECC3179B559719D7471C296B` |
| PG log | `logs/pg/postgres.log` | `A8496F1574540C86EE58825540A27EC52AC3172571F2304A9A5B335207040D75` |
| unified summary（2299/2299） | `logs/unified/summary.log` | `FEC9287E71BC9B39DD821C68376389F25D45469BB9E9CEA12548C0B6363A0AD2` |
| unified Infrastructure direct XML（42/42） | `logs/unified/direct-xml/Mahjong.Infrastructure.Tests/direct-results.xml` | `2E3D4106F88B3E215E47E6DB02407145D227F3D760E8F901BD71342422F5D1E4` |
| crash marker | `logs/crash-commit-marker.json` | `C10BC3598B8B20EE41D20A0441DB2F67436829473EA21744BCD486C157D035A6` |
| crash reader | `logs/crash-reader-result.json` | `C879E5C627EBC0D174E3A98F826F38A40007C9DDD3C1BA5214378FE063B121B4` |
| restore reader | `logs/restore-reader-result.json` | `B0D075D5336AFC4B5720781FBDEC3E08D9641D8236111460F75BB83D19FCCA86` |
| fresh test build log | `logs/build-tests.log` | `DC586F21B290D3D507F3D656266AE704C508A1327ECDB24C35FA4525B2976915` |
| solution build log | `logs/unified/sln-build.log` | `7E0F642494415C32BE5370D19D03681F0D8BB862D2B8658FC5A69FBCE2A89364` |
| 12-DLL plugin closure | `logs/unified/plugin-copy.log` | `7092E6FE3A181AB0E6AC8E45DB0D56B5B9F21E81C133716ACFEC82F0CA8BBA57` |
| companion meta（4/4） | `logs/unified/companion-fixtures.meta.txt` | `400ED7A6A7B138F69544B8355B499DE64DDE8885270D3C070FA9787BD3638EC7` |
| 外层 console | `artifacts/pg-delay-final-console.log` | `D843AE3B80C94E473907624D5D588173AD4BB14D212305C238FAB022BD842022` |

原始远端证据（本树副本）：

| 证据 | SHA256 |
|---|---|
| `artifacts/caller-evidence/pr-pg-tests.xml` | `43E233FD0B356F8865475085B0F55F957B02F27DF27EA801E36B9A3C4384A25B` |
| `artifacts/caller-evidence/push-unified-infrastructure.xml` | `0061EFED054D7879EBA02847322DD3D6C0D1409C04599601B46AA07A629E7EE5` |
| `artifacts/caller-evidence/ci-40665584740-failed.log` | `43FDB01AEBDA9C37DE0336AC8B3B8A5C30AA9E7CAC8BFF2CBFAC3A24C98F4C22` |
| `artifacts/caller-evidence/ci-40665578400-failed.log` | `18B2945E7EF0FEB05DCABBAD9B535CD8F584B3C9A49A1486025CAD932538F965` |

负变体证据（`D:/majiang-pg-tmp/` 短路径隔离副本；主树未改）：

| 变体 | 证据 | SHA256 |
|---|---|---|
| A guard-removed | `ds-pg-delay-guard-20261028/artifacts/run/logs/tests.xml` | `A4D704187B09AF8B9533F82F708141974C48870FE831050C16551A34FF795BE7` |
| A guard-removed | `ds-pg-delay-guard-20261028/P52TestSupport.cs.diff` | `497DB47AC392ECF654117B9E99F3244AB22FC9579C7EF59D2F0547E570FB556D` |
| B issued-before-prep | `ds-pg-delay-issued-20261028/artifacts/run/logs/tests.xml` | `D973A7D9B8D7771092ED6B681C124D89CF20A1C3556B806200470D978282F8B9` |
| B issued-before-prep | `ds-pg-delay-issued-20261028/P52TestSupport.cs.diff` | `295E3B78F8B76BB10126B27ED8BF5457B3EA1E45A794702E75EFCB2DB8E59D57` |
| C frozen clock | `ds-pg-delay-clock-20261028/artifacts/run/logs/tests.xml` | `CFC7C096F432E9C2135B58CB5E4D302E3142586123BC0F0654E391B7445214AB` |
| C frozen clock | `ds-pg-delay-clock-20261028/PostgresTableStore.State.cs.diff` | `A90B883E95B798FA9A21F7A9CAF2769D669BC658ECDF00E522E3478D544488BE` |
| C frozen clock | `ds-pg-delay-clock-20261028/PostgresTableStore.Commit.cs.diff` | `B1AE10080CBAA5785CEF10872234DEAAC1D0E283D110BB20D916FA32742C7593` |
| 变异副本逐文件比较（仅声明文件） | `artifacts/mutants-source-comparison.txt` | `EC5790272BCF1E01BA219CBFC5B1CC2A08D53DE5B23E7FA2A554334A0BB7C33D` |

### 9.9 修复映射与结论

| 缺陷 | 状态 | 根因/修复 | 验收 |
|---|---|---|---|
| D-CI-DELAY-01 | **CLOSED** | fixture 把一次 `Task.Delay` 通知当精确 2s；改为单调 Stopwatch 下界 + 继续有界可取消 await；后置延迟同样测量并断言 1.1s | final 42/42；受控提前唤醒 PASS；取消 PASS；guard-removed 41/42；issued-before-prep 40/42；frozen-clock 40/42；targeted 13/13；unified 2299/2299 |
| D-PG-SUP-01 | 保持 CLOSED | 原接受记录不变 | 本轮 full 42/42 再次覆盖原边界用例 |
| D-PG-SUP-02 | **UNRESOLVED** | 未重做归因，未改生产、未 retry、未抬 timeout | 原始失败与本报告 §2/§4 保留 |
| D-R1-01 / D-R1-02 | 保持 CLOSED（报告） | 原接受记录不变 | 未重做 |

结论：本轮修复的是一个**测试测量边界**，不是生产计时变更；严格“实际至少 2s 准备 + 至少 1.1s issue 后调度 + issue 在 preparation 之后 + 有效 scope 真实提交 + 过期/撤销零副作用”的原义务全部保留并加强。远端 8a0c8cb 的失败是真实失败，已由新代码修复；但**本轮没有发远端 CI**，远端 CI 为 **NOT_RUN**，receiver 未 push、未重跑 CI。G1 保持 APPROVED；G2–G5 未批准；V4 仍生产；181 classified 缺口保留；未合并 `v5`、未部署、未改 scheduler/`complete.flag`。最终 tracked HEAD 与 `git diff --check 8a0c8cb..HEAD` 由提交后 `git rev-parse HEAD` / `git diff --check` 核验，要求 tracked clean 且 exit 0。

## 10. Codex 调用者独立验收与统一发布（2026-10-28）

同一线程 `thread_20261028_182408_3497` 已调用 `approve_task`，桥接返回 **COMPLETED**。D-CI-DELAY-01 本任务范围 **PASS / CLOSED**，没有发起返修或新任务。审查与实际运行的 HEAD 为 `69ba5246cf54d1effcab8414a4e77c2a3a1f9f64`；本节后续提交只补验收记录，不改变被测代码。

调用者检查 `8a0c8cb..69ba524`：只改两个测试/fixture 与本既有交付报告。以既有报告延续证据链，没有另建任务书列出的 followup 报告。生产代码、SQL、统一 verifier/CI、工程配置与二进制均未改。严格单调等待下界、scope 签发因果、真实有效 Commit、过期/撤销零副作用均保留；未发现本任务范围的编译错误、测试失败或功能缺陷。

调用者在本工作树实际执行原脚本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55874 -Root artifacts/caller-delay-review/pg -LogDir artifacts/caller-delay-review/logs `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -RunUnifiedVerification -UnifiedLogDir artifacts/caller-delay-review/logs/unified `
  -LobbyRoot D:/majiang-pg-tmp/caller-delay-review-lobby-55875 -LobbyPort 55875
```

真实 PostgreSQL 16.10；fresh Release 测试构建与 solution 构建均 **0 warnings / 0 errors**。新 DLL 直接运行：Infrastructure **42/42**、targeted **13/13**，统一 11 工程 **2299/2299**；所有用例 failed/errors/skipped/not-run 均为 0，正常命令 native exit 0。12-DLL 闭包、companion **4/4（47 cases）** 通过；可选历史 git blob pins 未请求，仍为 NOT_RUN。最外层 **P52-TABLE-POSTGRES-PASS / exit 0**。

writer pid 50960 在 durable commit 后、publication 前被预期强杀；PG postmaster 从 `2026-10-28 11:13:30.939591+00` 变为 `11:15:06.221695+00`，自动恢复记录 3 行。独立冷读得到 stateVersion=1、lastEventSeq=2、terminal=true、receipt=Applied、cheatVersion=1；空库恢复 events=2、recoveryOwnerEpoch=3。表与 Lobby 集群均经 ownership 校验清理，55874/55875 无监听且两个集群根目录不存在。

调用者独立审计 `artifacts/audit-delay-evidence.ps1` **1103 checks / 0 failures / exit 0**：27 个报告 SHA 全匹配；receiver 与三个负变体清单的非自引用条目逐个核对哈希/长度；本次及 receiver 的 11 份 direct XML、full/targeted、旧双 CI 和三个变体 XML 均按节点核对真实分母。变体 A 清单包含无法自校验的 `manifest.sha256.json` 自引用，审计单列 `SELF_REFERENCE_NOT_VERIFIABLE`，不宣称该条通过。这是未改动 wrapper 的证据清单问题，不影响独立固定的 XML/日志哈希或本次功能验收。调用者没有重跑变体；其源码差异与原 XML 预期红分别为 **41/42、40/42、40/42**，不得写成本次重新执行。

证据根 `D:/majiang-wt/pg-delay-followup-ds-20261028-1822/`：

| 证据 | SHA256 |
|---|---|
| `artifacts/caller-delay-review/logs/tests.xml` | `C7CCC37137BB879FE6FBE6119AA633DC393D9810AE6D67B62FAD50C0EE715234` |
| `artifacts/caller-delay-review/logs/tests-targeted.xml` | `B02795FC273219452F375937472F45B2C12DA96EE82F7C38709387CCE5107D34` |
| `artifacts/caller-delay-review/logs/wrapper-summary.json` | `8783DFEFF9EA7D4724A8949092F4E8EF7722050DB955FD8964B9EA476B8C4F10` |
| `artifacts/caller-delay-review/logs/unified/summary.log` | `86E2D823FF5114DBADA9C49253DCDBF3B7BD21467696DA576C2682E56DDA52E1` |
| `artifacts/caller-delay-audit.txt` | `7BFFC141240E109A8474294DABD13D11C4BB122B01FD305F013B25C871D54C45` |

完整命令输出在 `artifacts/caller-delay-review-console.log`；各 native 日志、XML、meta 和恢复 JSON 在上述 logs 根。本次被测 DLL SHA256 为 `2E9D32673DB217E9AB7FD5296B6DD486693AD8BDA1028E115A52DACB2ACC59CD`。

统一发布沿用既有唯一草稿 [PR20](https://github.com/liuchangready/majiang-game/pull/20)，源 `feat/v5-post-g1-batch`，目标 `v5`；仅纳入本任务两个修复提交与调用者验收记录。其余已验收组件已有发布记录；Unity 来源校验与持久宿主仍在各自交付链，LocalHost 另由其既有调用者管理，不混入未验收/他人工作树改动。最终发布 SHA、远端回读和 MR 同步以同线程 `record_merge_request` 为准。

`8a0c8cb` 双 CI 的原始 1999ms 失败保留。§9 的远端 NOT_RUN 指 receiver 交付状态；调用者推送后自动触发的新头 CI 须独立验证，本次本地 PASS 不等于远端 PASS。D-PG-SUP-02 原始根因仍 UNRESOLVED；G2–G5 未批准、V4 生产、181 classified 与设备/跨网缺口不变。未合并 `v5`、未部署、未改 scheduler/`complete.flag`。
