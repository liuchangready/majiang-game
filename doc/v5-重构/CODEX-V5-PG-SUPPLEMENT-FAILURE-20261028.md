# PostgreSQL 独立补验失败记录（2026-10-28）

本次补验 **FAIL**：37 项 Infrastructure 测试全部执行，32 passed / 5 failed / 0 errors / 0 skipped / 0 not-run，runner 与最外层 wrapper 均 native exit 1。原始失败未重试、未覆盖。此前已记录的成功运行保留其原始结论；本记录是新增的失败证据，不能用旧成功覆盖。

## 冻结与范围

- 补验树：`D:/majiang-wt/cx-xml-1028`，分支 `wt/cx-xml-1028`。
- 执行 HEAD：`a763493eefb61e95b2c9eb122a63303eef67988b`。从已 fetch 的 `origin/v5=8fd7c6c` 建树，先纳入已接受 PR20 `1b0d693`，再合入 XML 候选 `ed9c307`。
- 并发 caller 已将同一候选验收并发布为 PR20 `3ee2e8c7abddbc50b475b0d472328c2df484c147`；bridge 线程 `thread_20261028_121602_2b5e` 已 completed 且 MR 已登记。本轮没有重复批准该线程。
- `git diff --quiet a763493 3ee2e8c -- dotnet tools unity .github` exit 0：上述实现路径完全相同，差异仅为另一 caller 的验收文档。
- 本报告不修改生产实现、正式测试、统一 verifier、SQL、信任设置或其它任务工作树。本次失败发生在 PG wrapper 的前置 Infrastructure 全量测试，**尚未执行新 XML 统一门禁**，不能把它归因为 XML 补丁。

## 已独立通过的检查

| 检查 | 本轮实际结果 |
|---|---|
| XML 工具边界矩阵 | 19/19，native exit 0；调用真实生产判定函数 |
| 原有 native exit 探针 | 6/6，native exit 0 |
| 交付方最终 table 证据 manifest | 109/109 SHA256 一致 |
| 独立读取交付方原生 XML / console / meta | 11 个程序集、2294/2294 Pass；每份 XML 节点、程序集、原始 console、meta native exit 0 与 SHA256 一致 |
| 本轮 Infrastructure / crash worker fresh Release build | 各 native exit 0，均 0 warnings / 0 errors |
| 本轮独占 PostgreSQL | 16.10，loopback 55782；XML wrapper 自检 7 项通过 |

以上 2294 是对交付方既有运行产物的独立审计，**不是本轮 fresh unified 成功运行**。

## 原样补验命令

工作目录 `D:/majiang-wt/cx-xml-1028`：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55782 -Root artifacts/pgcx -LogDir artifacts/caller-xml-review/table `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -RunUnifiedVerification -UnifiedLogDir artifacts/caller-xml-review/unified `
  -LobbyRoot D:/majiang-pg-tmp/cx-xml-1028-lobby -LobbyPort 55783
```

实际 Infrastructure runner 用时 `239.055s`。最外层记录 `P52-TABLE-POSTGRES-FAIL`、exit 1。wrapper 按归属停止并删除了自有 `artifacts/pgcx` 集群，日志确认 `ownership-verified cluster removed`。没有停止其它服务。表测试失败后，定向 13 项、writer kill、PG 重启、cold reader、空库 restore、Lobby 与统一 11 runner 均 **NOT_RUN**。

## 完整失败清单与下一步

| 测试 | 原始异常 / 实际行为 | 已确认范围 |
|---|---|---|
| `ExpiryAtCommitBoundary_WaitsForLockThenUsesDatabaseClockAndRejects` | `ArgumentOutOfRangeException: Scope expiry must follow issue time`，`P52TestSupport.cs:188`，测试调用 `PostgresTableStoreTests.cs:322` | 已确认 fixture 时序缺陷，尚未走到被测 commit boundary |
| `ActorCommit_RoundTripsRealEventsReceiptAndColdRecovery` | `PostgresTableStoreTests.cs:29`，Expected `Accepted` / Actual `Failed`，用时 44.541s | 失败原因细节尚未被该断言输出，未判定为环境或产品缺陷 |
| `RevocationAtCommitBoundary_BlocksThenRejectsWithoutSideEffects` | Npgsql stream read timeout，`ResetAsync → CreateAsync`，用时 13.408s | 连接/准备阶段超时；根因未关闭 |
| `StoreFailureBeforeCommit_DoesNotPublishThroughActor` | Npgsql stream read timeout，`ResetAsync → CreateAsync`，用时 10.617s | 连接/准备阶段超时；根因未关闭 |
| `TerminalOutbox_IsAtomicAndConsumerIsIdempotent` | Npgsql stream read timeout，`IssueScopeAsync → CreateAsync`，用时 31.111s | scope 准备阶段超时；根因未关闭 |

`D-PG-SUP-01`（MAJOR，测试准备时序）：测试在调用 fixture 前取得 `now`，传入绝对 `scopeExpiresUtcMs=now+700`；`CreateAsync` 先 await `ResetAsync` 和 `CreateRoomAsync`，之后才用 wall clock 取得默认 `issued`。准备耗时跨过 700ms 时，scope 构造即出现 `expires <= issued`。修复须在准备完成后建立有效 scope，再在真实行锁已确认阻塞后受控跨越/调整 expiry，并继续断言数据库时钟拒绝及零副作用。不能只延长任意 sleep、放宽生产校验或跳过测试。

`D-PG-SUP-02`（待定位，三项连接超时与一项 actor Failed）：需保留本次原始结果，结合服务端/进程资源与 actor 失败细节定位。不能仅依据同时出现的慢运行称其为 flaky 或环境问题，不能靠盲重试、统一提高所有 timeout 或放宽 Accepted 断言销账。若增加诊断，应输出脱敏的结果码/细节并保持既有断言。修复归属为 P5.2 Infrastructure 测试与其复核，**不扩大正在执行的持久宿主或 Unity 任务范围**；下轮派单前先核对全局 reservation，避免并发重复任务。

没有在此轮发布代码修复或撤回历史成功记录。此组新问题尚未验收关闭。

## 原始证据

根：`D:/majiang-wt/cx-xml-1028/artifacts/caller-xml-review`。本次 table manifest 的 **53/53** 文件已独立重算 SHA256，0 mismatch。

| 相对路径 | SHA256 |
|---|---|
| `table/tests.xml` | `60E38E542CB0616201A3D21AE1A5E0EA5231D28C2AF4D2BF75892056EFAEE335` |
| `table/wrapper-summary.json` | `107F2F663362F466A1EC1AC0C92CBFC6D42E70BBADCF825B7FF8B724D4BA08E9` |
| `table/manifest.sha256.json` | `5B5A9C717A82E23A621ABCE3DC0820C67DBDF3005866398470663D5ED8F35E17` |
| `wrapper-console.log` | `C10F83B562317D3FD2D8B2941F7B4BF08F54D468ECC910C533A52C5B30C13F97` |
| `receiver-audit.json` | `3666857C1CE1DE2D1A39B1E8772B13A8CCA6C219D938946B1225D28901554C9A` |

补验脚本与最终交接在同根的 `audit.ps1`、`handoff.json`，原始框架输出在 `table/tests.log.stdout`，native 最外层退出码在 `wrapper-exit.txt`。边界探针证据分别在原 receiver 树 `artifacts/direct-results-selftest-47250dffc87e4465b910391cede368b8` 和 `artifacts/native-exit-f24ad446ca9540cf8d9cd434d94473c0`，未改写交付方正式日志。

## 门槛与并发边界

全局 recent ledger 本轮只读取一次：DeepSeek 当时 1 running + 1 waiting_review（目标 2）、GLM 1 reworking（目标 1）、Anti 1 reworking（目标 1）。此后仅针对已发生发布变化的 XML 线程确认 completed/MR；本轮没有再读全局列表或新增 dispatch，避免并发 caller 交接期间重复占槽。下一轮以新的全局快照核对所有 pending dispatch 后补位。

已观察的持续任务为 DeepSeek `thread_20261028_124552_6fe6`（插件来源门禁）、GLM `thread_20261027_230302_93fb`（持久宿主）、Anti `thread_20261027_154438_fc96`（LocalHost 候选）。手动暂停的 P4.1c/d 未恢复；失败旧 P4.1 由既有拆分线程承接，没有复制。

G1 保持 APPROVED；G2–G5 未批准，V4 仍生产，181 classified 仍未覆盖。P4.5 等待 P4.1 和正式装配；G3 等待持久宿主及真实远端 E2E；G4 仍需 G2+G3 与真实设备/网络；G5 仍需发布/回退和用户最终批准。iOS BLOCKED，Android 真机及跨物理网络 NOT_RUN。调度保持 PT30M / IgnoreNew，`complete.flag` 不存在。
