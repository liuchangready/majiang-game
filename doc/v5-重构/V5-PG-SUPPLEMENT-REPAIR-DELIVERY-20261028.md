# V5 PostgreSQL 补验修复交付（D-PG-SUP-01/02）

日期：2026-10-28
工作树：`D:/majiang-wt/pg-sup-repair-ds-20261028-1545`
分支：`wt/v5-pg-sup-repair-ds-20261028-1545`
冻结执行头：`6b6a5f9ac66938393523aa43a2224863a9e8d7cb`
代码修复提交：`f51f94988344766dcaa4d539aba961efde8674b1`

结论：**D-PG-SUP-01 已闭环；D-PG-SUP-02 的原始 4 项失败未复现，但原始根因未取得可证明的归因，按 UNRESOLVED 交付，不把本轮绿色写成原始失败已销账。** 本轮 fresh Release 构建、真实 PostgreSQL 16.10 的 Infrastructure 40/40、定向 13/13、强杀/重启/冷读/空库恢复，以及未改动入口的 unified 11 个测试工程 2297/2297 全部通过；这不改变 V4 生产基线、G1 APPROVED、G2–G5 未批准、181 classified 未覆盖的状态。

本任务未 push、未创建 PR/MR、未合并 `v5`、未部署、未修改 scheduler、未创建 `complete.flag`。仅修改 P5.2 Infrastructure 测试/fixture 并新增测试，未修改生产 Infrastructure、SQL、TableRuntime、Contracts、csproj/sln、统一 verifier、既有 PG wrapper 或现有门槛。

---

## 1. 冻结、范围与路径核对

### 1.1 冻结头

- caller 本轮 fetch 的 `origin/v5`：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
- 本树由该头 `git worktree add` 建立，再 fast-forward 到已审查发布 PR20 当前头 `6b6a5f9ac66938393523aa43a2224863a9e8d7cb`。
- 开始核验：`git rev-parse HEAD` = `6b6a5f9...`；`git branch --show-current` = `wt/v5-pg-sup-repair-ds-20261028-1545`；`git status --porcelain` 为空；`git merge-base --is-ancestor 6b6a5f9 HEAD` exit 0；`git rev-list --count 6b6a5f9..HEAD` = 0。因此测试证据最初建立在冻结头 `6b6a5f9` 上。
- 未复用旧 `main`、PR19 source 或 `D:/majiang-wt/pg-supplement-ds-20261028` 的工作内容；未带在途未提交代码。其它工作树、Unity 环境/Resources/Burst 和非本任务修改均保持不动。
- 未操作任何 bridge 线程；未恢复/复制/绕过手动 paused 的 P4.1c/d。GLM 持久宿主、Anti LocalHost、另一 DS 插件来源门禁均未触碰。

### 1.2 expectedFiles 与实际 diff

修复提交 `f51f949` 只包含 3 个 Infrastructure 测试/fixture 文件：

```text
M  dotnet/tests/Mahjong.Infrastructure.Tests/P52TestSupport.cs
M  dotnet/tests/Mahjong.Infrastructure.Tests/PostgresTableStoreTests.cs
A  dotnet/tests/Mahjong.Infrastructure.Tests/PostgresSupplementRegressionTests.cs
```

本报告是第 4 个 tracked 文件：

```text
A  doc/v5-重构/V5-PG-SUPPLEMENT-REPAIR-DELIVERY-20261028.md
```

净 diff（代码提交）：`3 files changed, 427 insertions(+), 40 deletions(-)`。新增测试文件由现有测试工程自动包含，未改 csproj/sln。`git diff --cached --check` 与 `git diff --check` 均为 exit 0。

未修改：`dotnet/src/Mahjong.Infrastructure/**`、`dotnet/src/Mahjong.TableRuntime/**`、`dotnet/src/Mahjong.Contracts/**`、`dotnet/Mahjong.sln`、任何 `.csproj`、`tools/ci/verify-v5-table-postgres.ps1`、`tools/ci/verify-v5-postgres.ps1`、`tools/verify-v5.ps1`、SQL migration、既有 verifier/门槛。`artifacts/**` 为 ignored 诊断与原始证据，不提交二进制。

---

## 2. 原始失败证据（原样保留，不覆盖历史成功）

原始失败证据根：`D:/majiang-wt/cx-xml-1028/artifacts/caller-xml-review`。caller 已独立重算 53/53 manifest SHA256，0 mismatch。本任务再次冻结关键文件：

| 文件 | SHA256 | 说明 |
|---|---|---|
| `table/tests.xml` | `60E38E542CB0616201A3D21AE1A5E0EA5231D28C2AF4D2BF75892056EFAEE335` | 原始失败 XML |
| `table/tests.log.stdout` | `B42269E8575F7E22AF1430DA0974CE3FE8CE0C1FFF05403FEEFBF1E2A554AF63` | 原始 console |
| `table/pg/postgres.log` | `9CF4D9F463ED5E8B2E45C8965465BACF7C062647AF5871C835726AB83D83E53B` | 原始 PG 16.10 日志 |
| `table/wrapper-summary.json` | `107F2F663362F466A1EC1AC0C92CBFC6D42E70BBADCF825B7FF8B724D4BA08E9` | wrapper FAIL |
| `table/wrapper.log` | `D0D238C9275AD183E3547F86E5904D107B8F18789621BA7DCDCB6729D50B9C58` | wrapper console |

冻结快照：`D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/caller-preparation/original-evidence-frozen.json`。
`caller-preparation/independent-failure-audit.json` 与本轮独立读取一致：37 total / 32 passed / 5 failed / 0 errors / 0 skipped / 0 not-run，runner 与 wrapper native exit 1，Infrastructure 用时 239.055s；真实 PG 16.10，loopback 55782。`invalid-attempt` 仅是 caller 最初 PowerShell JSON 数组解析失误，不作为被测业务失败。

原始失败清单保持：

| 原测试 | 原始异常 / 实际 |
|---|---|
| `PostgresTableStoreTests.ExpiryAtCommitBoundary_WaitsForLockThenUsesDatabaseClockAndRejects` | `ArgumentOutOfRangeException: Scope expiry must follow issue time`，`P52TestSupport.cs:188`，未走到 commit boundary |
| `PostgresTableStoreTests.ActorCommit_RoundTripsRealEventsReceiptAndColdRecovery` | `Expected Accepted / Actual Failed`，44.541s，原断言无 result 细节 |
| `PostgresTableStoreTests.RevocationAtCommitBoundary_BlocksThenRejectsWithoutSideEffects` | Npgsql `Exception while reading from stream` / `Timeout during reading attempt`，`ResetAsync → CreateAsync`，13.408s |
| `PostgresTableStoreTests.StoreFailureBeforeCommit_DoesNotPublishThroughActor` | 同类 Npgsql read timeout，`ResetAsync → CreateAsync`，10.617s |
| `PostgresTableStoreTests.TerminalOutbox_IsAtomicAndConsumerIsIdempotent` | 同类 Npgsql read timeout，`IssueScopeAsync → CreateAsync`，31.111s |

历史成功运行（此前 37/37）保留其原始语境，没有被本轮结果覆盖；本报告只新增 fresh 结果，不撤回原始失败。

---

## 3. D-PG-SUP-01：fixture 时序缺陷（CLOSED）

### 3.1 精确根因

原测试在调用 fixture 前取 `now`，传入绝对 `scopeExpiresUtcMs = now + 700`。`P52Fixture.CreateAsync` 先执行 `ResetAsync`、构造房间并 `CreateRoomAsync`，之后才用 wall clock 取得默认 `issued`；当准备耗时超过 700ms，`expires <= issued`，由 `TableScopeRecord` 构造器抛出 `Scope expiry must follow issue time`，根本没有执行到被测的 scope 行锁/数据库时钟/提交边界。

这不是生产 scope 校验缺陷；生产校验不应放宽。缺陷在测试 fixture 的“先决定过期时间、后准备”时序。

### 3.2 修复映射

| 缺陷 | 文件 | 修复 | 验证 |
|---|---|---|---|
| D01-a：fixture 在准备前隐含 scope 时间基准 | `P52TestSupport.cs` | `CreateAsync` 支持 `scopePreparationDelay` 和相对 `scopeLifetime`；先完成 reset/room/preparation，再取 `issued` 并计算 `expires`；两者同时传入、非正 lifetime、负 delay 都显式拒绝 | 新测试 1 的 `issuedAgeMs <= 1000` 与 2s 准备断言 |
| D01-b：fixture 失败时 store 泄漏 | `P52TestSupport.cs` | `CreateAsync` 增加失败清理，失败时 `DisposeAsync` store；保留原始异常并写 failure-only 诊断 | 编译与 40/40 全量；失败诊断路径不改变断言 |
| D01-c：原测试用准备前 `now` 预计算 expiry | `PostgresTableStoreTests.cs` | 先 `CreateAsync()` 建立有效 scope；锁住 scope 行；启动 commit 并确认 `pg_stat_activity` 处于 `Lock` 等待；在 blocker 事务内用数据库 `clock_timestamp()` 将 `issued/expires` 改到过去；释放锁；断言 `ScopeRejected` + `RequestScopeExpired` + 零副作用 | 原测试名与完整义务保留，目标 13/13 通过 |
| D01-d：缺少 >700ms 准备与 expiry/revoke 锁边界回归 | `PostgresSupplementRegressionTests.cs` | 新增 3 项确定性回归（见 3.3） | Infrastructure 40/40、unified 40/40 |

原 `ExpiryAtCommitBoundary...` 的断言完整保留并强化：`Disposition == ScopeRejected`、`ReasonCode == RequestScopeExpired`、receipt/event/state 均为零；仍使用数据库时钟，不再依赖任意 sleep 或固定 700ms 墙钟窗口。

### 3.3 新增回归与负例矩阵

`PostgresSupplementRegressionTests`（3 项）：

1. `DelayedScopePreparation_EstablishesScopeAfterPreparationAndCommits`
   - 2s 准备延迟 + 3s 相对 lifetime；断言实际准备 >=2s、scope issue age <=1s、expiry-issued 精确等于 lifetime；随后真实 commit 必须 `IsCommitted`，state version=1，event 数与候选一致。
   - 反证：若有人把 `issued` 移回准备之前，`issuedAge` 会 >=2s，测试必然失败；若有人让准备消耗 lifetime，commit 会被 scope 拒绝。
2. `ExpiryAtCommitBoundary_AfterDelayedPreparation_RejectsWithoutSideEffects`
   - 900ms 准备后建立有效 scope；真实 scope 行锁阻塞被 `WaitForRowLockWaitAsync` 证明；blocker 内用 `clock_timestamp()` 把 `issued/expires` 改到过去；释放后断言 `ScopeRejected` / `RequestScopeExpired`，无 receipt/event/state。
3. `RevocationAtCommitBoundary_AfterDelayedPreparation_RejectsWithoutSideEffects`
   - 900ms 准备后建立有效 scope；行锁阻塞后 blocker 内写 `revoked_at_utc_ms = clock_timestamp()`；释放后断言同一拒绝码与零副作用。

统一负例矩阵：

| 场景 | 期望 | 断言 |
|---|---|---|
| 准备 2s，scope 在准备后签发，3s lifetime | 接受 | `IsCommitted`、state=1、event 数一致 |
| 准备 900ms，commit 阻塞在 scope 行，数据库时钟跨过 expiry | 拒绝 | `ScopeRejected` + `RequestScopeExpired` |
| 准备 900ms，commit 阻塞在 scope 行，数据库时钟撤销 | 拒绝 | `ScopeRejected` + `RequestScopeExpired` |
| 无 blocker / 正常 scope | 正常提交 | 原 40 项全量通过 |
| 拒绝路径 | 零副作用 | `request_receipt=0`、`table_event=0`、`state_version=0` |

没有 skip、空测试、放宽生产校验、统一抬高 timeout、刷新摘要或旧产物复用。新延迟/同步只存在于测试 fixture。

---

## 4. D-PG-SUP-02：3 项连接超时 + 1 项 actor Failed（原始根因 UNRESOLVED）

### 4.1 原始证据与新增诊断

原始 3 项 Npgsql 失败的堆栈均落在 **测试 helper 的连接建立/认证**：

- `PostgresTableTestEnvironment.ExecuteAsync` 的 `OpenAsync` → `NpgsqlConnector.RawOpen/Authenticate` → `Timeout during reading attempt`；
- `ResetAsync`（TRUNCATE）和 `IssueScopeAsync` 尚未执行到 SQL；
- 因此这 3 项不是 PostgreSQL 表存储生产代码断言失败。

第 4 项 ActorCommit 的原始断言只输出 `Expected Accepted / Actual Failed`，没有 `ReasonCode`/`Detail`/连接阶段，无法从原 XML 反推是连接、提交还是 actor 逻辑。

本轮加入 failure-only 诊断（`P52TestSupport.cs`）：

- `P52Diagnostics.RecordActorFailureAsync`：失败时写 `status`、`reasonCode`、脱敏 `detail`、耗时，并用 3s 超时探针记录 `dbNow`、`max_connections`、全部连接数、P52 连接数、postmaster 启动时间；只在失败时写文件，成功路径不产生额外负载。
- `P52Diagnostics.RecordFixtureFailure`：记录 `phase`（reset/prepare/create-room/scope-preparation/issue-scope/acquire-lease）、耗时、异常类型与脱敏消息。
- `Sanitize` 屏蔽 `password/pwd`，不写 payload key、私有牌、token 或业务 payload 字节。
- Actor 断言改为等价强断言 `Assert.True(result.Status == Accepted, "status=...; reason=...; detail=...; elapsedMs=...")`；仍严格要求 Accepted，没有放宽。
- `P52Fixture.CreateAsync` 失败时释放 store，避免测试失败级联资源泄漏。

本轮两次 fresh 运行均没有出现 `fixture-failure-*.txt` 或 `actor-failure-*.txt`，说明新增探针未被触发；这不等于原始第 4 项已归因。

### 4.2 资源与连接证据

- 原始失败前后，宿主机存在 8 个非本任务的 `D:\Git\usr\bin\sh.exe -c "while :; do :; done"` 进程，启动时间均为 **2026-10-27 15:04:09**（早于 2026-10-28 06:24 的失败），到本轮结束时每个进程累计用户态 CPU 约 695,000s；这是持续的多核占用，不是本任务创建，也未由本任务终止。
- 本轮 final run 的 2s 采样（277 个有效样本）最低可用物理内存 **400.5MB**；原始失败时同样处于低内存区间。final run 仍 40/40 通过，说明低内存本身不足以证明确定性产品缺陷。
- final run 中 PostgreSQL backend 峰值 **21**，TCP TIME_WAIT 峰值 **260**；没有接近默认 `max_connections=100`，也没有端口耗尽迹象。连接建立超时不是连接数/端口耗尽。
- 原始 PG 日志在失败窗口只记录“could not receive data from client / aborted”，没有 `FATAL: too many clients`、没有 postmaster crash/recovery、没有数据库断言错误；与“客户端在认证/连接建立阶段超时”一致。
- 原始第 4 项耗时 44.541s，新增 actor 诊断已能在复现时给出 `ReasonCode`/`Detail`；本轮 run1 ActorCommit 3.046s、final 40/40 中的耗时正常，未复现 44s 失败。

资源快照：`artifacts/caller-preparation/host-resource-snapshot.json`、`host-resource-postrun.json`；采样：`artifacts/pgsup-monitor-run1.csv`、`artifacts/pgsup-monitor-final.csv`。

### 4.3 结论与下一探针

**D-PG-SUP-02：UNRESOLVED（原始根因）。** 本轮没有修改生产代码、没有统一抬高 timeout、没有盲 retry、没有把慢运行/新绿 run 直接称为 flaky 或环境问题。可以确认的是：3/4 原始失败的堆栈在测试连接建立层，未进入生产断言；第 4 项原始细节缺失，新增诊断在两次 fresh 运行中未触发。由于原始 4 项在相同宿主压力下未复现，不能声称“已修复”或“已关闭”，也不能据此撤回原始失败。

若再次发生，下一探针固定为：

1. 读取 `artifacts/pgsup-final/tests/actor-failure-*.txt` / `fixture-failure-*.txt`，保留原始 `phase`、`elapsedMs`、`ReasonCode`、`Detail` 与 3s DB probe；
2. 同时抓取 `pg_stat_activity` 的 `wait_event_type/query`、backend 数、`max_connections`、postmaster 启动时间和客户端 TCP 状态；
3. 仅当能在同一进程内证明 actor 的 `CommitFailed` 来自连接建立、且 DB probe 同时失败时，才允许把它归入连接建立层；否则继续保留 UNRESOLVED，不进入生产代码修改。

---

## 5. 本轮原样命令、native exit 与计数

环境：`D:/Unity/dotnet/dotnet.exe` 8.0.426；PG zip `D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip`，SHA256 `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`（与 wrapper pin 一致）；未改 wrapper/pin/机器 trust/PATH。

### 5.1 fresh Release build

```powershell
& D:/Unity/dotnet/dotnet.exe build `
  dotnet/tests/Mahjong.Infrastructure.Tests/Mahjong.Infrastructure.Tests.csproj `
  -c Release --no-incremental --nologo -v:minimal
```

实际：native exit 0，0 warnings / 0 errors；测试 DLL SHA256 `2A323ABF12C36441D9E12709C61D0039C22D6E282F58A884E196EFCD3D42C3B1`（run1 与 final 两次 fresh 构建相同）。日志：`artifacts/build-ds-repair.log`。

### 5.2 run1（先验证修复，不跑 unified）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55822 -Root artifacts/pgsup -LogDir artifacts/pgsup-run1 `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip
```

实际：wrapper native exit 0；Infrastructure **40/40**（0 failed/0 skipped/0 errors/0 not-run，92.531s）；targeted **13/13**；crash writer killed；PG immediate restart；cold reader；空库 restore 全 PASS。证据根：`artifacts/pgsup-run1`。

### 5.3 final（同一 `-RunUnifiedVerification`，不改入口）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55822 -Root artifacts/pgsup -LogDir artifacts/pgsup-final `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -RunUnifiedVerification -UnifiedLogDir artifacts/pgsup-final/unified `
  -LobbyRoot D:/majiang-pg-tmp/ds-pg-sup-repair-20261028-1545-lobby `
  -LobbyPort 55823
```

实际：最外层 `P52-TABLE-POSTGRES-PASS`，native exit 0；全部 stage PASS（cluster、xml-gating-selfcheck、fresh-build、tests、tests-targeted、crash-writer、postgres-crash-restart、crash-reader、backup-restore、unified-verification、result）；`ownership-verified cluster removed`。

### 5.4 计数

| 阶段 | selected | passed | failed | skipped | errors | not-run | native exit |
|---|---:|---:|---:|---:|---:|---:|---:|
| Infrastructure full | 40 | 40 | 0 | 0 | 0 | 0 | 0 |
| Infrastructure targeted（`PostgresTableStoreTests`） | 13 | 13 | 0 | 0 | 0 | 0 | 0 |
| unified 11 test projects | 2297 | 2297 | 0 | 0 | 0 | 0 | 0 |
| companion fixtures | 4 | 4 | 0 | 0 | 0 | 0 | 0 |

Infrastructure 40 = 原 37 完整保留 + 新增 3；targeted 13 = 原 13 完整保留。unified 2297 = 原 2294 + 3，未删分母：

```text
Mahjong.Ai.Tests                 444
Mahjong.Architecture.Tests        92
Mahjong.Client.Tests             182
Mahjong.Client.Wss.Tests          80
Mahjong.Contracts.Tests          149
Mahjong.Domain.Tests             529
Mahjong.Infrastructure.Tests      40
Mahjong.LobbyService.Tests        89
Mahjong.TableRuntime.Tests        400
Mahjong.TableService.Tests        152
Mahjong.Unity.TableModel.Tests    140
--------------------------------------
合计                             2297
```

11 个 direct runner 的 XML、console、`direct.meta.txt` native exit 均一致为 0；12 个程序集（DLL）plugin closure 通过，companion 4/4；solution build 0 warnings / 0 errors。XML gating 的 7 个负例自检 PASS；未修改 XML maintenance/native exit 防护。

### 5.5 PostgreSQL、崩溃恢复与清理

- 表集群：PostgreSQL **16.10**，loopback **55822**，data directory `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup/data`；Lobby 集群：PostgreSQL **16.10**，loopback **55823**，data directory `D:/majiang-pg-tmp/ds-pg-sup-repair-20261028-1545-lobby/data`。
- 开始前 55822/55823 均无监听；结束后均无监听。LobbyRoot 在 worktree 外且唯一；表 Root 使用短路径 `artifacts/pgsup`。
- crash writer：pid 51236，post-commit marker `stateVersion=1, lastEventSeq=2, eventCount=2`，强杀后未发布。
- PG immediate restart：postmaster start `08:06:12.529872Z -> 08:10:40.414576Z`，automatic recovery 3 行，redo 完成。
- cold reader：`recovered=true, stateVersion=1, lastEventSeq=2, terminal=true, receiptStatus=Applied, cheatStateRestored=true, outboxPayloadMatches=true, recoveryOwnerEpoch=2`。
- 空库 restore：`events=2, recovered=true, receiptStatus=Applied, outboxPayloadMatches=true, recoveryOwnerEpoch=3`。
- 表集群与 Lobby 集群均由 wrapper 以 ownership marker 停止并删除；未停止/删除任何非本任务进程或集群。GLM 55597 及其它 DS 集群未触碰。

---

## 6. 关键证据与 SHA256

完整 manifest（189 个文件）：`D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/manifest.sha256.json`。

| 证据 | 绝对路径 | SHA256 |
|---|---|---|
| 原始失败 XML | `D:/majiang-wt/cx-xml-1028/artifacts/caller-xml-review/table/tests.xml` | `60E38E542CB0616201A3D21AE1A5E0EA5231D28C2AF4D2BF75892056EFAEE335` |
| final 全量 XML（40/40） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/tests.xml` | `B564D1051A454934586CB1F1E00951419EB2F1DB1A46DD7B49D14FD13ABFE65A` |
| final targeted XML（13/13） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/tests-targeted.xml` | `FFFA8070E627B4CD66A803C2E06134942DEE2AFDF71C68D60528D78198C6F0A7` |
| final wrapper summary | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/wrapper-summary.json` | `B57700444C212FEA4D70A80FAA69DE013214A64606EB10701B9B8AEC03202AEB` |
| final wrapper console | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/wrapper.log` | `60C6ACA62C50EDAFBC5869331390F28780EF7971BD2871E2D52A277C79E154DC` |
| unified summary（2297/2297） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/unified/summary.log` | `D8F1A47BD28D1CB6074F6ECD4F06840EAEDC1A9686AD2CB874AF164C8D6B3140` |
| unified run identity | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/unified/run-info.txt` | `81C7C01CB58D4F0A82F1B86126F115674B6D7E1D75461C9AFE2FB297AB0CEBE6` |
| unified PG wrapper summary | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/unified/pg/wrapper-summary.json` | `799BF9C6B858003FDC82CAD6D7AB61ED787B937616CA35BBDA245A0C5E71E735` |
| Infrastructure direct XML | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/unified/direct-xml/Mahjong.Infrastructure.Tests/direct-results.xml` | `FAF8329EDDED11D79215CEC42F182E98863F0215AB7FFDC76FA1BEFA58EAB435` |
| companion meta（4/4） | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/unified/companion-fixtures.meta.txt` | `3B1C94B278C4DE85D7FB2069D62018FEBBB3801172C31000E1F99DBF66BE594A` |
| 12-DLL plugin closure | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/unified/plugin-copy.log` | `83B7E685470DB64EB0C8C097F7AF866B711354B3C990D7C6BAE09964064B3B2F` |
| solution build | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/unified/sln-build.log` | `90DC0B52A5DE3AC15944A786B35A2D346CF38F6F48061C54C06881D86263CAB8` |
| crash marker | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/crash-commit-marker.json` | `8F221A344A7EA723C2F563B76CEFC721342B7F524B48092265C327675975E5D3` |
| crash reader | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/crash-reader-result.json` | `C879E5C627EBC0D174E3A98F826F38A40007C9DDD3C1BA5214378FE063B121B4` |
| restore reader | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/restore-reader-result.json` | `B0D075D5336AFC4B5720781FBDEC3E08D9641D8236111460F75BB83D19FCCA86` |
| final PG log | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-final/pg/postgres.log` | `649F31350D84CA7ABB5336B763FBF84EB2F068017D67CF8B2AA37F50173FE987` |
| run1 wrapper summary | `D:/majiang-wt/pg-sup-repair-ds-20261028-1545/artifacts/pgsup-run1/wrapper-summary.json` | `2DD629A205FDD1D06B59985151BA86EC9675688D5CD4F82F7A07795C24D2B786` |

run1 的 `tests.xml`、targeted、crash/read/restore 全部保留在 `artifacts/pgsup-run1`，未覆盖。

---

## 7. 结论与门槛

| 项 | 结论 |
|---|---|
| D-PG-SUP-01 | **CLOSED**：fixture 在准备后签发 scope；真实行锁阻塞后由数据库时钟跨过 expiry/revoke；原测试义务保留；新增 3 项回归；40/40 与 13/13 通过 |
| D-PG-SUP-02 | **UNRESOLVED（原始根因）**：3/4 原始堆栈在测试连接建立层，第 4 项原始细节缺失；新增 failure-only 诊断未触发；两次 fresh 40/40、unified 2297/2297；不宣称原始失败已销账，不修改生产代码，不抬高 timeout，不盲 retry |
| V5-组件验收 | fresh Release 0 warning/0 error；真实 PG16.10；Infrastructure 40/40；targeted 13/13；unified 2297/2297；companion 4/4；12 DLL closure；crash/restart/cold reader/empty-DB restore 全 PASS；0 skip/failed/errors/not-run |
| 远端 CI | **NOT_RUN**：本任务未 push，按规则不能写远端新提交 CI 通过 |
| G 门槛 | G1 保持 APPROVED；G2–G5 未批准；V4 仍生产；181 classified 仍未覆盖 |
| 范围外 | Unity/Android 真机/iOS/跨物理网络/完整 LocalHost/持久宿主/整场/远端 E2E 均非本任务范围，保持原状态 |

最终 tracked HEAD 由提交后 `git rev-parse HEAD` 核验；代码提交为 `f51f94988344766dcaa4d539aba961efde8674b1`，本报告所在提交是其后的交付提交。`git diff --check 6b6a5f9..HEAD` 必须为 exit 0；工作树 tracked clean。
