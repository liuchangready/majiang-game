# G2 最终统一候选 CI 收绿 — 本地交付（2026-11-05）

**状态：本地候选验证 PASS，等待独立复核；远端 CI NOT_RUN。** 本交付不表示 G2 阶段批准或最终 CI 已收绿。

## 身份与范围

- 工作树/分支：`E:/majiang-review/g2-final-ci-green-luna6-20261105` / `wt/v5-g2-final-ci-green-luna6-20261105`。
- 派发头：`8b084f0c4a6b60f17f32fe79ce8f59af0698a583`；任务卡 SHA-256：`3266B1BC9231494E924286E95E3E0D4118CE5AD07FE21DDF3EE8DDE05ECE1F53`。
- 已验证源码提交：`6850fcc1913301c9d58d473a447dfc3c5dbb7856`。只改任务卡八个代码/测试文件，交付文档与 binding 在后续 docs-only 提交。
- 任务运行证据根：`build/b1118ef0-4f4b-4fa8-aea6-c59523dc1368`。完整最终运行 ID：`c19a633f17c74f4d9ab376bdf0a48a23`。
- 修改列表：`dotnet/scripts/pg-lobby-verify/pg-common.ps1`、`dotnet/scripts/pg-lobby-verify/start-postgres.ps1`、`dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs`、`dotnet/tests/Mahjong.LocalHost.Tests/C04RoomTicketTests.cs`、`dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`、`tools/ci/verify-v5-table-postgres.ps1`、`tools/tests/VerifyV5PostgresCi.Tests.ps1`、`tools/tests/VerifyV5TablePostgresStartup.Tests.ps1`。
- 未修改 Domain、Contracts、Wire、Client 生产、Unity/Android、workflow、`tools/verify-v5.ps1`、机器配置、依赖、gate/过滤规则。旧任务树与旧证据只读。

## 已知缺陷全集、修复映射与验收

### G2-VALIDATION-02 — MAJOR — 已修复，待独立复核

- **位置/复现：** `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs` 中 `DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect`（旧行约 575–690）。旧 unified 复现入口为 `tools/ci/verify-v5-table-postgres.ps1 -RunUnifiedVerification`；定向命令形如 `Mahjong.LocalHost.Tests.exe -method Mahjong.LocalHost.Tests.LocalControlTests.DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect -noLogo -result-xml <独占 XML>`。
- **原实际/预期：** 原统一运行 LocalHost 183/184，目标断言实际 `Synchronizing`、预期 `Ready`。旧原件保持不变：XML SHA-256 `0B0DFB7806A4583E0EF9FE664A8901A0D9EB17C1057FD8FD9515F15FB7CC7757`，诊断日志位于旧树 `E:/majiang-review/g2-final-ci-luna6-20261104/build/541f2f42-fd58-49e6-b7ac-526c178c9abc/unified/direct-Mahjong.LocalHost.Tests.log`。
- **修复：** 在测试中将 baseline 观察绑定到当前 session/room/scope/binding epoch/connection epoch/seat/role；区分原持久请求 scope 与重连后的当前 binding scope。增加过期 correlation、替换 correlation、foreign session、foreign binding epoch 和 stale incremental 负例；原请求/scope/digest/持久化 outbox 与唯一副作用断言保留。
- **本轮证据/验收：** 固定 5 次，每次独占 XML，5/5 PASS；最终全量 LocalHost 184/184 PASS。接收 baseline 时检查 `Ready`，四种无效 SYNC 和旧增量不得完成 reconciliation，确认真实当前 baseline 被应用、outbox replay 不重复副作用、早期缓冲无 gap。详见 binding `focused` 和 `unified.projects`。

### G2-VALIDATION-01 — MAJOR — 已修复/恢复并通过真实 PG，待独立复核

- **位置/复现：** `dotnet/scripts/pg-lobby-verify/{pg-common.ps1,start-postgres.ps1,cleanup-postgres.ps1}`、`tools/ci/verify-v5-postgres.ps1` 的 Lobby 临时 PG 启停链。旧 unified 表现为临时安装缺 `psql.exe`、`pg_ctl.exe`、`share/timezone`、`share/timezonesets`，LobbyService 12 项 Npgsql stream-close 失败且 cleanup 失败；旧 XML SHA-256 `F8B66D240299BEFCD2A0AC7B7F81A8BBF04DDB09F9F442CEFDD3306E51242EC6`，原 Lobby 77/89。复现入口为完整 table-postgres + `-RunUnifiedVerification`。
- **修复：** 启动前由生产脚本验证 PostgreSQL runtime closure：`pg_ctl/initdb/psql/createdb/pg_isready/postgres` 与 timezone/timezonesets 资源必须齐全；测试对缺可执行文件、缺两类资源及完整闭包做真实脚本函数的正负例。未改变 gate、skip、数据库选择或机器配置。
- **包与验收：** 使用官方 pinned PostgreSQL 16.10 包，322,530,154 bytes，SHA-256 `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`；独立 runtime 闭包核对 1,631 个 `bin/lib/share` 文件，缺失/尺寸不匹配为 0。最终自有 loopback PG：table 55592、Lobby 55593；迁移重复执行成功、真实 `psql` 和 Npgsql 链运行，LobbyService 89/89，环境恢复 PASS，两套 cluster 持有 ownership marker 并正常停止/删除，最终无自有残留进程。PG 闭包自检 153/153；最终 table 脚本 42/42、targeted 13/13。负例证据见 `build/ci-pg-startup-selftest/20261105-033556-dcc74ab4/selftest-summary.json` 和最终 table `tmp/xml-selfcheck`；所有原始失败仍保留。

### G2-REVIEW-01 — MAJOR / OPEN_SCOPE_GAP — 本轮按授权补齐，待独立复核

- **位置/复现：** `dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs`、`dotnet/tests/Mahjong.LocalHost.Tests/C04RoomTicketTests.cs`，测试 `RecoveredOpenHumanDecisionRetiresOnOriginalDeadlineWithoutBinding`。原 C04 的 `PrepareRecoveryCaptureOnNextOpen` 使恢复使用 `deferDriver=true`、`DriverRunning=false`，手动 `actor.TickAsync` 只能证明 actor。
- **修复：** 增加 `internal` driver iteration completion barrier 与测试 accessor；只在一次真实 driver iteration 完成 actor tick 和真人绑定 gate 后通知等待者，不改变生产调度、自动推进时序、绑定门控、领域 deadline 或恢复策略，无公开平台 API。C04 启动恢复后真实 driver，验证 deadline 前不退休、deadline 后按原 timing 退休，无绑定不得重开真人决策。合法首次恢复 grace `5000ms` 只在源 `RecoveryGraceApplied=false` 时应用一次；已应用时不重复增加。
- **本轮证据/验收：** 固定 5 次、每次独占 XML，5/5 PASS；完整 TableRuntime 483/483 与 LocalHost 184/184 PASS。无 `sleep`、手动 actor tick 或 StopAsync 代替 driver 完成观察。详细断言及十份 XML hash 见 binding。

## 阶段结果

- **A–C：** 环境短预检完成；SDK 8.0.426/runtime 8.0.32、PowerShell 5.1，最终运行前 ports 55592/55593 空闲；磁盘空间满足本次运行。没有运行 setup/environment 脚本、没有改 PATH/许可/驱动。完整 pinned archive 在本树独占运行根复制后运行。
- **B：** 修改前保留的编译/runner尝试失败均在 RUNBOOK 逐项记录，不作为绿结果；修复后固定定向 5+5 PASS，完整 LocalHost/Client/TableRuntime suites 分别 184/184、182/182、483/483 PASS。
- **C：** PG runtime closure 1,631/1,631 文件匹配；自检 153/153 PASS；真实 wrapper 启动、重复迁移、环境恢复、marker ownership、正常清理成功。最终 unified 的 LobbyService 89/89 PASS。
- **D：** 冻结源码提交 `6850fcc1913301c9d58d473a447dfc3c5dbb7856` 后执行真实完整入口，日志见 `build/b1118ef0-4f4b-4fa8-aea6-c59523dc1368/final-c19a633f17c74f4d9ab376bdf0a48a23/`。命令：

  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -RunUnifiedVerification -Port 55592 -LobbyPort 55593 -Root E:/majiang-review/g2-final-ci-green-luna6-20261105/artifacts/g2-final-ci-green-luna6-20261105-c19a633f17c74f4d9ab376bdf0a48a23/table-root -LobbyRoot D:/g2-final-ci-green-luna6-20261105/lobby-c19a633f17c74f4d9ab376bdf0a48a23 -PgZipPath E:/majiang-review/g2-final-ci-green-luna6-20261105/artifacts/g2-final-ci-green-luna6-20261105-table-retry2/download/postgresql-binaries.zip -LogDir build/b1118ef0-4f4b-4fa8-aea6-c59523dc1368/final-c19a633f17c74f4d9ab376bdf0a48a23/table -UnifiedLogDir build/b1118ef0-4f4b-4fa8-aea6-c59523dc1368/final-c19a633f17c74f4d9ab376bdf0a48a23/unified
  ```

  运行 1434 秒；最终 table wrapper summary `exit=0`、`P52-TABLE-POSTGRES-PASS`；内层 unified wrapper `verifier exit=0`、cleanup/environment restore PASS；summary.log 为 `OVERALL: PASS (project rows: 21 PASS, 0 FAIL, 0 BLOCKED, 0 NOT_RUN; companion gate: PASS)`。实际发现 21 行：14 个测试项目 2,872/2,872 PASS、0 fail/error/skip/notrun，另 7 个 helper 行 PASS。14 份逐例 XML、sha256 与实际项目数写在 companion JSON。
- **进程/退出码记录：** table 与 Lobby cluster 都经各自 ownership marker 正常停止删除，环境恢复成功。外层 PowerShell 监控器的 Process.ExitCode 字段采集为空（记录为 UNKNOWN，不伪写 0）；生产 table wrapper 的最终 summary `exit=0`，内层 unified verifier 捕获的 native exit `0`，每项目 summary 均记录 XML 与 native exit 0 一致。
- **D 首次尝试：** 初次全入口尝试输出 start wrapper exit 1、write-evidence `OutOfMemoryException`，verifier NOT_RUN，原件保存在 `build/b1118ef0-4f4b-4fa8-aea6-c59523dc1368/formal/retry2/`；原因保持 UNKNOWN，不被后续成功覆盖。随后 pinned archive/runtime closure、真实 start/cleanup wrapper 独立探针及新独占根证明路径可用，再以新的 unique roots 执行上述完整运行。旧的 2026-11-04 Unified 19 PASS / 2 FAIL 仍保持 FAIL，不被本次结果改写。
- **E：** 原始日志/XML、失败尝试、manifest 与 hashes 均保留在独占 build 根；本轮没有 push、PR 修改、合并、部署或远端 CI 操作。

## 保留风险与结论

- 历史 `FU-02` 首败原因仍为 UNKNOWN；历史 `SYNC-03` 间歇记录仍保留。本轮没有声称解释或关闭它们。
- D05 的已接受范围保持原样；Unity/普通 Windows 同包矩阵未因本轮变化重跑。Android/C06 不在本轮范围；V4 继续生产。
- 独立 reviewer 尚未复验，状态 `PENDING`。远端新候选 CI 尚未运行，状态 `NOT_RUN`；旧 source push run `41729487041 SUCCESS` 与旧 PR 合成 merge run `41729493387 FAILURE` 是不同身份，不能替代本候选远端 CI。
- **结论：本地完整候选验证 PASS，交独立复核；G2 最终 CI 收绿尚未完成。**
