# V5 PR20 9136508 双 CI 原始证据独立审计（2026-10-28）

范围：只读复核已完成的原始 runs
[push 40564851868](https://github.com/liuchangready/majiang-game/actions/runs/40564851868)
与
[pull_request 40564866626](https://github.com/liuchangready/majiang-game/actions/runs/40564866626)，
仓库 `liuchangready/majiang-game`，PR
[#20](https://github.com/liuchangready/majiang-game/pull/20)，目标 `v5`。
本报告不重做 Android 工具修复、不重审旧 `467b64b`，也不把本报告当作 G2/G3 或 receiver 代码接受。

**结论：** 两个 completed run 的原始产物支持 `9136508` 的 `.NET/CI` 结果为 PASS：
11 个实际 runner 独立合计 **2,286/2,286**，failed/errors/skipped/not-run 均为 0，
native exit 0；Release 构建 **0 warnings / 0 errors**，12 DLL 闭包，4/4 companion fixtures；
真实 PostgreSQL 16.10 的 table 35/35、targeted 13/13、Lobby 89/89 全 Pass，强杀 writer、
PG immediate-shutdown 自动恢复、cold reader、空库 `pg_dump`/`pg_restore`、owner epoch 与
outbox/cheat/receipt 实值、cleanup 均有原始记录。PR run 的实际 checkout 是 GitHub 合成
merge `0247000`，其 tree 与 `9136508` **完全相同**；API 的 `head_sha=9136508` 不是该 run 的
实际 `GITHUB_SHA`。

Android 工具链/精简 Wire AOT APK 仍是**历史独立验收**（55/55，APK 33,978,023 bytes 及其
SHA 可复核），当前双 CI **没有执行**这些工具测试，故为 **NOT_RUN**，不能写成 fresh PASS。
完整 Android 游戏/真机、iOS、跨物理网络均未在本轮运行。P5.2 的 PG 存储证据不表示在途
P5.4b-A 持久 WSS 宿主已经通过。

本轮没有发现新的、可归责于 `9136508` 双 CI 的实质不一致；未关闭的缺口和口径限制见 §7。
G1 仍为既有 APPROVED；G2–G5 仍未批准；V4 仍是唯一生产基线。

---

## 1. Run、实际 checkout 与产物完整性

### 1.1 Run 与 checkout 身份

| 项 | push | pull_request |
|---|---|---|
| Run ID / attempt | `40564851868` / 1 | `40564866626` / 1 |
| URL | https://github.com/liuchangready/majiang-game/actions/runs/40564851868 | https://github.com/liuchangready/majiang-game/actions/runs/40564866626 |
| Event | `push` | `pull_request` |
| API `head_sha` | `91365086c4ae75978a96d85e1a35ff8ab3d70770` | `91365086c4ae75978a96d85e1a35ff8ab3d70770` |
| 实际 `GITHUB_SHA` / git HEAD | `91365086c4ae75978a96d85e1a35ff8ab3d70770` | `0247000d29276b75d9d21a4d888acd918f378905` |
| 实际 checkout ref | `refs/heads/feat/v5-post-g1-batch` | `refs/pull/20/merge` |
| Tree | `244de125550919e83fe921aeeaa50fd44fff9dce` | `244de125550919e83fe921aeeaa50fd44fff9dce` |
| Job ID | `122403272562` | `122403324037` |
| Runner | GitHub Actions `1000000178` | GitHub Actions `1000000179` |
| Region / OS / image | `eastus` / Windows Server 2025 `10.0.26100` / `windows-2025-vs2026` `20261021.278.1` | `westus` / 同一 OS/image |
| Conclusion | `success` | `success` |
| Artifact | id `12761764683`, 4,092,282 bytes | id `12763675021`, 4,092,259 bytes |
| Artifact SHA256 | `df8258f92eaf85e5f2a7ce0e670e7468fb5bcf500cbb062cdd325817631252b2` | `4caca58fc2851bea80e8803af81a3563e33f0092166ebcb82a01689660c1216d` |

API 元数据与原始 `GITHUB_SHA` 的关系已在 `run-info.txt` 中分别记录：

- push：`GITHUB_SHA=9136508...`，`GITHUB_REF=refs/heads/feat/v5-post-g1-batch`。
- PR：`GITHUB_SHA=0247000...`，`GITHUB_REF=refs/pull/20/merge`，`GITHUB_REF_NAME=20/merge`。

PR run 的 job log 明确记录了实际 fetch/checkout：

```text
git fetch ... +0247000d29276b75d9d21a4d888acd918f378905:refs/remotes/pull/20/merge
HEAD is now at 0247000 Merge 91365086c4ae75978a96d85e1a35ff8ab3d70770 into 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
```

### 1.2 PR 合成对象与 `9136508` 的 tree 证明

`0247000` 是 GitHub 生成的 PR merge commit：

```text
tree 244de125550919e83fe921aeeaa50fd44fff9dce
parent 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
parent 91365086c4ae75978a96d85e1a35ff8ab3d70770
```

独立核对：

```text
git rev-parse "9136508^{tree}"   -> 244de125550919e83fe921aeeaa50fd44fff9dce
git rev-parse "0247000^{tree}"   -> 244de125550919e83fe921aeeaa50fd44fff9dce
git diff --exit-code --quiet 9136508 0247000 -> exit 0
git diff --numstat 9136508 0247000 | Measure-Object -> 0 条
```

`git ls-remote origin refs/pull/20/merge` 在本轮返回的仍是
`0247000d29276b75d9d21a4d888acd918f378905`；但本报告不是靠"当前 ref"证明历史对象，而是靠
job log 的精确 fetch SHA 与该 SHA 自身的 tree/父提交。因此，PR 合成 tree 与 `9136508`
**全等**；PR run 实际测试的实现树就是 `9136508` 的树。

### 1.3 下载与完整性

- 两个 run 的全部 artifact bundle、完整 job log、run/jobs/artifacts API JSON 均下载到独占
  ignored 目录 `$E`（见 §9）。
- 本地 artifact zip SHA256 与 GitHub API `digest` 逐字节一致（见上表）。
- 未使用徽章或 summary 代替原始日志；未触发任何新的 remote rerun。
- `summary.log`、`run-info.txt`、job log、artifact 内的 `native-exit-*` 与 PG 日志均已保留原样。

---

## 2. Fresh 构建、11 个实际 runner 与唯一总数

### 2.1 每个 run 的实际计数

两个 run 的 11 个 runner 计数相同。每个 runner 的 `direct.meta.txt` 记录 `exit code   : 0`、
`total`、`failed`、`skipped`、`head`；`direct-<project>.log` 的
`=== TEST EXECUTION SUMMARY ===` 记录 `Errors/Failed/Skipped/Not Run`。两者逐项一致，
且 `head` 均等于该 run 的实际 checkout。

| # | 实际 runner | push total | PR total | failed | errors | skipped | not-run | exit |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | `Mahjong.Ai.Tests` | 444 | 444 | 0 | 0 | 0 | 0 | 0 |
| 2 | `Mahjong.Architecture.Tests` | 92 | 92 | 0 | 0 | 0 | 0 | 0 |
| 3 | `Mahjong.Client.Tests` | 182 | 182 | 0 | 0 | 0 | 0 | 0 |
| 4 | `Mahjong.Client.Wss.Tests` | 80 | 80 | 0 | 0 | 0 | 0 | 0 |
| 5 | `Mahjong.Contracts.Tests` | 149 | 149 | 0 | 0 | 0 | 0 | 0 |
| 6 | `Mahjong.Domain.Tests` | 529 | 529 | 0 | 0 | 0 | 0 | 0 |
| 7 | `Mahjong.Infrastructure.Tests` | 35 | 35 | 0 | 0 | 0 | 0 | 0 |
| 8 | `Mahjong.LobbyService.Tests` | 89 | 89 | 0 | 0 | 0 | 0 | 0 |
| 9 | `Mahjong.TableRuntime.Tests` | 400 | 400 | 0 | 0 | 0 | 0 | 0 |
| 10 | `Mahjong.TableService.Tests` | 152 | 152 | 0 | 0 | 0 | 0 | 0 |
| 11 | `Mahjong.Unity.TableModel.Tests` | 134 | 134 | 0 | 0 | 0 | 0 | 0 |
| | **唯一总数** | **2,286** | **2,286** | **0** | **0** | **0** | **0** | **0** |

算术独立复核：`444+92+182+80+149+529+35+89+400+152+134 = 2,286`。这个 2,286 是从本 run
的 11 个 `direct.meta.txt` 与 11 个 raw runner log 逐项重算的，**不是**从历史 `467b64b`
的 2,286 抄来的；历史数字仍只作为历史记录保留（见 §5）。

### 2.2 不纳入唯一总数的项目

- 3 个 build-only helper：`Mahjong.Outbox.CrashWorker`、`Mahjong.Infrastructure.CrashWorker`、
  `Mahjong.LobbyService.PostgresProbe`。它们只在 `sln-build.log` / 单独 build log 中验证为
  构建 PASS，`summary.log` 明确 "contributes no test cases"。
- P5.2 table wrapper 的 `tests` 阶段另行执行 `Mahjong.Infrastructure.Tests` 35/35；同一
  job 的 unified 2,286 中已包含该项目 35 项一次，故 wrapper 的 35 是重复执行，不追加。
- `tests-targeted.xml` 的 13/13 是 `PostgresTableStoreTests` 子集，不追加。
- 6 个 launcher 探针不是 xunit 用例，不追加。
- `summary.log` 的 14 个 project rows = 11 个 test runner + 3 个 helper；不能把 14 当作
  "14 个测试 runner"。

### 2.3 Selection / XML 口径

- 11 个 test runner 的命令均为 `dotnet <test-dll> -noLogo -reporter default`，没有
  `-class`/`-filter`/`-trait`/`-where`；每个 `Total > 0`，即完整程序集被执行，属于
  positive selection。
- 11 个 `direct.meta.txt` 的 `total/failed/skipped` 与 raw runner log 的
  `Total/Errors/Failed/Skipped/Not Run` 完全一致；`head` 与实际 checkout 一致。
- **11 个 direct runner 没有逐 runner XML**。`verify-v5.ps1` 的 direct 路径不传 `-xml`，
  因此 artifact 中只有 `runs/<project>/direct.meta.txt` 与 `direct-<project>.log`；
  `test-results/<project>/` 在全部成功的 run 中不存在。本报告没有把不存在的 XML 写成
  "XML 已验证"；对 11 个 runner 的权威原始记录是 meta + raw console summary。
- PostgreSQL 的 `tests.xml`/`tests-targeted.xml` 确实存在，并已用 XML 解析逐节点核对
  （§3），这不是 direct runner 的 XML。

### 2.4 Release build、12 DLL 与 companion fixtures

| 检查 | 两个 run 的结果 | 证据 |
|---|---|---|
| clean checkout | 2,099 files；先删目录再 checkout，无历史 `bin/obj` | job log Checkout |
| solution build | 17 projects，`Build succeeded`，**0 Warning / 0 Error** | `unified/sln-build.log` |
| 单独构建的 test projects | `Architecture`、`Client.Wss`、`LobbyService`、`TableRuntime`、`Unity.TableModel`，全部 0/0 | `unified/build-*.log` |
| `LobbyService.PostgresProbe` helper | 0/0 | `unified/build-Mahjong.LobbyService.PostgresProbe.log` |
| P5.2 fresh builds | `build-tests.log`、`build-worker.log` 均 0/0 | `table/build-tests.log`、`table/build-worker.log` |
| .NET SDK | `8.0.426`（`global.json` 固定；`dotnet --version` = 8.0.426） | `unified/sdk-diagnostic.log` |
| 12-DLL 闭包 | 19 files = 12 DLL + 7 PDB；`Unity plugins ready` | `unified/plugin-copy.log`、`unified/plugins/` |
| companion fixtures | **4/4**，8+32+4+3 = 47 cases，native exit 0 | `unified/companion-fixtures.log`、`.meta.txt` |
| historical git pins | **NOT_RUN**（shallow CI 未 fetch 历史冻结 blob；只验证当前字节与嵌入 provenance） | `companion-fixtures.meta.txt` |

12 个 DLL 为：`Google.Protobuf`、`Mahjong.Ai`、`Mahjong.Client`、`Mahjong.Contracts`、
`Mahjong.Contracts.Wire`、`Mahjong.Domain`、`Mahjong.LocalHost`、`Mahjong.TableRuntime`、
`System.Buffers`、`System.Memory`、`System.Numerics.Vectors`、
`System.Runtime.CompilerServices.Unsafe`。

补充：`sdk-diagnostic.log` 显示宿主 `Host Version: 10.0.14`，但 `global.json` 选中的 SDK
是 `8.0.426`，`dotnet --version` 与 build/test 均使用 8.0.426。这里不把 "宿主版本 10"
误写成 ".NET 10 SDK 跑了测试"；此前 `.NET 10 SDK` 的历史事故与本 run 的 SDK 选择是两件事。

---

## 3. PostgreSQL 16.10 存储与 Lobby 证据

### 3.1 Table P5.2

| 项 | push | PR |
|---|---|---|
| PG server | `PostgreSQL 16.10`，Visual C++ build 1944，64-bit | 同 |
| table cluster | `127.0.0.1:55592`，`mahjong_p52_test` | 同 |
| `tests.xml` | total 35 / passed 35 / failed 0 / skipped 0 / errors 0 | 同 |
| `tests-targeted.xml` | total 13 / passed 13 / failed 0 / skipped 0 / errors 0 | 同 |
| raw `tests.log` | `Total: 35, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`，exit 0 | 同 |
| raw `tests-targeted.log` | `Total: 13, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`，exit 0 | 同 |
| targeted 性质 | `PostgresTableStoreTests` 子集，重复执行，不加入 2,286 | 同 |

35 个 table 用例均属于 `postgres-table-p52` collection，XML 中每个 test 节点 `result=Pass`；
13 个 targeted 用例同样是该 collection 的子集，全部 `Pass`。关键覆盖包括
`ActorCommit_RoundTripsRealEventsReceiptAndColdRecovery`、
`LeaseDualInstanceAndOldOwnerFencing`、`SameRequestDifferentPayload_IsConflictWithoutNewSideEffects`、
`TerminalOutbox_IsAtomicAndConsumerIsIdempotent`、`Recovery_RejectsEventGapInsteadOfSkipping`、
`PayloadKeyIsRequiredAndDatabaseBytesDoNotContainPlaintext` 等。

### 3.2 Writer 强杀与真实 PG crash/restart

- crash writer 的 `crash-writer.log.kill.json`：
  - push：`pid=5256`，`exitCode=-1`，`forced=true`，`killedUtc=2026-10-27T19:01:44.7925564Z`。
  - PR：`pid=5244`，`exitCode=-1`，`forced=true`，`killedUtc=2026-10-27T19:01:23.0699171Z`。
- `crash-commit-marker.json` 证明 writer 在强杀前已写 post-commit marker：
  `stateVersion=1`、`lastEventSeq=2`、`eventCount=2`、`requestId=p52-crash-request`、
  `outboxResultVersion=1`、`outboxSha256=c0b108...`、`cheatVersion=1`、
  `committedAtUtcMs=...`。
- PG 日志证明不是"模拟崩溃"：
  - `received immediate shutdown request`；
  - `database system was not properly shut down; automatic recovery in progress`；
  - `redo starts at 0/1577688`；`redo done`；`database system is ready to accept connections`。
- 两个 run 的 `postgres-restart-recovery-lines.log` 都包含上述 3 条关键恢复行。

### 3.3 Cold reader 实值

两个 run 的 `crash-reader-result.json` 字节相同，实值为：

```json
{"stateVersion":1,"lastEventSeq":2,"eventCount":2,"commandCount":1,
 "receiptStatus":"Applied","outboxPublished":false,
 "outboxSha256":"c0b108923986159032998a13a1946582cbe1d948375ccc3b2abce926b0aa2a55",
 "terminal":true,"outboxPresent":true,"outboxPayloadMatches":true,
 "cheatStateRestored":true,"cheatVersion":1,"recoveryOwnerEpoch":2,"recovered":true}
```

这证明 cold reader 是重启后的独立读取结果，不是同进程内存对象：state/event/receipt/outbox/
cheat 均为实值，cold reader 记录的 `recoveryOwnerEpoch=2`；空库 restore 后
`restore-reader` 记录 `recoveryOwnerEpoch=3`。

### 3.4 空库 `pg_dump` / `pg_restore`

- `table_store.dump` 为 `--format=custom --schema=table_store` 产物。
- wrapper 先创建空 restore database `mahjong_p52_restore`，再 `pg_restore`，再执行
  `GRANT`；`restore-reader-result.json` 的实值为：
  `stateVersion=1`、`eventCount=2`、`receiptStatus=Applied`、`outboxSha256` 同上、
  `cheatVersion=1`、`recoveryOwnerEpoch=3`、`recovered=true`。
- `createdb`/`pg_dump`/`pg_restore` 的 stdout 日志为空是工具成功时的正常行为；对应
  `.stderr` 为 0，wrapper 的 `backup-restore` stage 记录 `PASS (exit 0)` 与
  "restored events=2; recovery ownerEpoch=3"。本报告不把空 stdout 冒充内容证据，
  依据是 exit/stderr/reader 实值。

### 3.5 Cleanup 与 Lobby

- Table cluster：wrapper 最后记录
  `ownership-verified cluster removed: ...\artifacts\p52-pg-cluster-...`，随后
  `P52-TABLE-POSTGRES-PASS`。
- Lobby cluster：`cleanup.log` 显示先停止、再删除
  `...\mahjong-batch-lobby-...`；`wrapper-summary.json` 的 `rootDeleted=true`，
  `cleanup` stage `PASS (exit 0)`。
- 环境变量：`env-restore.jsonl` / `wrapper-summary.json` 记录 4 个
  `MAHJONG_LOBBY_*` 变量均已恢复为调用前状态（`restoredToPreCall=true`，SHA-256 记录，
  不打印值）。
- Lobby migration：同一 `001_lobby.sql` 连续应用 2 次，均 exit 0；8 张 lobby 表；
  `postgres-evidence.jsonl` 14 个事件，覆盖 terminal projection、receipt terminal replay、
  expired reservation、migration repeated、cross-room join、cross-connection row lock、
  receipt replay/conflict、transaction rollback、ticket consumption、room fill。
- `Mahjong.LobbyService.Tests` 在真实 PG env 下 89/89，0 failed/errors/skipped/notrun。
  这个 89 已包含在 2,286 中，不另加。

### 3.6 Wrapper / inner 退出码与 launcher 探针

- `table/unified.log`：
  `verifier exit : 0`，`overall exit  : 0`，`failed stages : (none)`。
- `wrapper-summary.json`：`plannedExit: 0`，`exit: 0`。
- 这与 9136508 的 job step `Run unified V5 verification` 结论 `success` 一致；本轮没有
  出现 b5c1c17 那种 "inner 1 / outer 0" 的错配。
- launcher 探针 6/6，两个 run 的 `results.json` 字节相同：
  `exit-0=0`、`exit-1=1`、`exit-2=2`、`exit-17=17`、`timeout=throw`、
  `forced-writer=-1`。
- **不存在单独的 exit-code 6 探针**。如果 "6 探针" 指的是探针数量，则 6/6 已真实执行；
  如果验收意图是 exit code 6 这一具体用例，则该用例没有定义/没有执行，应为
  **NOT_RUN**，不能写成 PASS。

### 3.7 边界

以上是 **P5.2 存储组件 + Lobby PG 合同** 的证据。它不表示在途 P5.4b-A 的持久
WSS TableService 宿主已经接线完成或通过；后者的真实宿主进程、冷恢复、WSS 准入仍
**NOT_RUN/unaccepted**。

---

## 4. Android 工具链接受候选到 PR20 的代码范围

### 4.1 提交与文件范围

| 提交 | 角色 | 内容 |
|---|---|---|
| `fcce34e` | Android 工具修复初版 | 4 files |
| `0e7dd70` | 接受候选（R2 关闭 review findings） | 4 files, +401/−71；parent `fcce34e`，冻结起点 `3601403` |
| `bd5d453` | PR20 集成 merge | 第一父 `c7001a8`，第二父 `0e7dd70`；相对第一父仅 4 files |
| `9136508` | 接受文档 | 仅新增 `CODEX-V5-P2.2-ANDROID-TOOLCHAIN-ACCEPTANCE.md`（93 lines） |

`bd5d453` 相对 `c7001a8` 的精确 name-status：

```text
A  doc/v5-重构/V5-P2.2-ANDROID-TOOLCHAIN-REPAIR-DELIVERY.md
A  tools/tests/VerifyV5AndroidToolchain.Tests.ps1
A  tools/unity/repair-android-toolchain.ps1
M  tools/unity/test-wire-aot-platform-recheck.ps1
```

三份工具/测试源码与 `0e7dd70` 逐文件零差异：

```text
git diff --quiet 0e7dd70 bd5d453 -- tools/unity/repair-android-toolchain.ps1                   exit 0
git diff --quiet 0e7dd70 bd5d453 -- tools/unity/test-wire-aot-platform-recheck.ps1             exit 0
git diff --quiet 0e7dd70 bd5d453 -- tools/tests/VerifyV5AndroidToolchain.Tests.ps1             exit 0
git diff --quiet 0e7dd70 bd5d453 -- "doc/v5-重构/V5-P2.2-ANDROID-TOOLCHAIN-REPAIR-DELIVERY.md" exit 0
```

对应 blob 相同：doc `41420d2b...`、repair `1f285620...`、test-wire-aot `ba0ad9e8...`、
tests `f59f83ca...`。
`bd5d453` 相对 `c7001a8` 对 `dotnet/`、`unity/`、`.github/`、`tools/ci/`、
`tools/verify-v5.ps1`、`build.gradle` 的 diff 为空（exit 0）；没有把 Unity 资源、
Burst、Editor 环境或其它在途文件带进来。`9136508` 相对 `bd5d453` 只增加接受文档。

### 4.2 当前双 CI 实际覆盖

工作流 `.github/workflows/v5-verify.yml` 只执行：

```text
./tools/ci/test-v5-native-exit.ps1
./tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -RunUnifiedVerification -LogDir artifacts/verify-v5/table -UnifiedLogDir artifacts/verify-v5/unified
```

其中没有 `repair-android-toolchain.ps1`、`VerifyV5AndroidToolchain.Tests.ps1` 或
`test-wire-aot-platform-recheck.ps1` 的调用；workflow 注释也明确 "makes no
Unity/IL2CPP/Android/iOS claim"。因此：

- Android 工具链测试在当前两个 run：**NOT_RUN**。
- 当前 CI 没有 fresh APK 构建，也没有设备运行；**不得**把 CI 绿灯写成 Android 工具
  测试通过。
- 本轮没有启动 Unity/共享 Editor、没有重建 APK、没有改 Android SDK/PATH/信任。

### 4.3 历史独立验收（可引用，但不是当前 CI）

原始任务树 `D:/majiang-wt/android-toolchain-ds-20261027` 的历史证据仍在，且与接受文档
一致：

| 项 | 值 |
|---|---|
| 正式脚本测试 | **55/55**，fail/skipped/notRun 均 0，exit 0 |
| 证据目录 | `build/android-toolchain-tests/20261028-025122-c775312ca734`（`summary.json`） |
| 原 APK | `build/android-toolchain-repair/recheck/20261028-013201-d856a5bc11f6/il2cpp/android/MahjongAotSmoke.apk` |
| APK bytes / SHA256 | **33,978,023** / `F03A13B4573DD5AABEFAD24B6B83440B34E34ED342B6873287A64C6127308019` |
| 原 caller review manifest | `evidence-sha256.csv` SHA256 `43A29319865A9020E8152CBB5D3B4C10710386B8AFBA349C9D7C9354E4F39DFA` |
| 集成证据 manifest | `D:/majiang-wt/codex-android-accepted-20261028/build/codex-android-integrated/evidence-sha256.csv` SHA256 `3CC844FB9E3C0EBEFFC0C9C9CDA21B5241FCF65D47D32157E00564049921632E` |
| 原 Unity build | native exit 0，marker `target=Android/backend=IL2CPP/result=Succeeded/errors=0`（历史日志） |

这些是**历史独立验收**，不是 `40564851868`/`40564866626` 的 fresh 结果。该 APK 是精简
Wire AOT 探针（`com.mahjong.aotsmoke`/arm64-v8a），不是完整游戏 Android 交付；完整游戏
Android 构建、整场 E2E、真机运行均 **NOT_RUN**。iOS 仍 **BLOCKED/NOT_RUN**。

---

## 5. 历史失败的保留状态

本轮新绿灯不改变以下历史记录：

1. **`b5c1c17` push run `40544348887` 仍是 FAIL。**
   原始记录（push）：2,285 passed / 1 failed / 2,286 selected；失败用例
   `RuntimeDecisionRuntimeTests.IllegalAction_AndWrongDecisionCannotAdvanceTheAuthority`
   （expected Rejected / actual Applied）；direct runner 152 selected、151 passed、1 failed、
   exit 1；inner Lobby wrapper exit 1；outer table wrapper 错记为 PASS/exit 0。
   该失败不是 flaky，也没有被 9136508 的新证据关闭；9136508 只证明修复后的 head 两 run
   为 PASS。
2. **`39970580589` 的缺失 Client framework log 事件仍是 UNRESOLVED。**
   后续 Client 绿灯不证明其根因已修复或已关闭；本报告不把它重新分类为 flaky，也不关闭。
3. **`467b64b` 的两次 2,286/2,286 仍是历史独立记录。**
   本报告没有重审 `467b64b`；本 run 的 2,286 是从 9136508 的 11 个 runner 重新计算的。
4. **旧 `467b64b` 审计与 Android 工具修复不重做。**
   本报告只核实"新提交远端 CI 待另行核实"这一已记录义务。

---

## 6. 当前精确 SHA 的 PASS/FAIL/BLOCKED/NOT_RUN

| 项目 | 结果 | 依据/边界 |
|---|---|---|
| push `40564851868` @ `9136508` | **PASS** | 11 runners 2,286/2,286；0 fail/errors/skipped/notrun；exit 0；build 0/0；12 DLL；4/4 fixtures；PG table 35/35、targeted 13/13；launcher 6/6 |
| PR `40564866626` @ 合成 `0247000`（tree == `9136508`） | **PASS** | 同上；实际 `GITHUB_SHA=0247000`，tree 与 `9136508` 全等 |
| PR 合成 tree 与 `9136508` 全等 | **PASS** | `git diff --exit-code --quiet 9136508 0247000` = 0；tree 均为 `244de125...` |
| .NET Release build / 12-DLL 闭包 / companion 4/4 | **PASS** | 两个 run 的 `sln-build.log`、`plugin-copy.log`、`companion-fixtures.*` |
| PG 16.10 table 存储组件 | **PASS（组件）** | 35/35 XML；强杀/重启/cold reader/空库 restore/cleanup 实值 |
| PG 16.10 Lobby 合同 | **PASS（组件）** | 89/89；migration 2 次；14 个 PG evidence 事件；cleanup/env restore |
| launcher 退出码/强杀探针 | **PASS 6/6** | 0/1/2/17/timeout/forced-writer；**无 exit-6 用例** |
| Android 工具链组件 | **历史 ACCEPTED；当前 CI NOT_RUN** | 55/55、APK SHA 33,978,023 bytes，均为历史独立证据 |
| 完整游戏 Android / 真机 | **NOT_RUN** | 本轮未构建完整 APK、未启设备、未启 Unity |
| iOS | **BLOCKED / NOT_RUN** | 无 macOS/Xcode/iOS 真机证据 |
| 跨物理网络 | **NOT_RUN** | 只有 loopback PG/WSS；无真实跨网 E2E |
| P5.4b-A 持久 WSS 宿主 | **NOT_RUN / unaccepted** | 在途任务；P5.2 存储证据不代替 |
| `b5c1c17` push `40544348887` | **FAIL（历史保留）** | 2,285/2,286；未因新绿灯关闭 |
| `39970580589` Client framework log | **UNRESOLVED（历史保留）** | 缺失原始 framework log；不判 flaky |
| G1 | **APPROVED（既有）** | 本审计不改变 |
| G2–G5 | **UNAPPROVED** | 本审计不批准 |
| V4 | **唯一生产基线** | 未切换、未部署 |

---

## 7. 已知缺口 / 口径限制

以下都不是"空 PASS"；它们是本轮明确未关闭或未覆盖的项：

1. **Android 工具测试不在当前 CI**：两个 run 均未执行 55/55 工具套件；当前 CI 的
   NOT_RUN 不能由历史 55/55 替代。
2. **完整 Android 游戏/真机 NOT_RUN**：历史 APK 是精简 Wire AOT 探针，不是 full game；
   本轮未重建 APK、未启动设备/Editor。
3. **iOS BLOCKED/NOT_RUN**：没有 macOS/Xcode/iOS 真机。
4. **跨物理网络 NOT_RUN**：现有 PG/WSS 都是 loopback；没有真实跨网、弱网、后台恢复。
5. **P5.4b-A 持久 WSS 宿主未接受**：本报告只确认 P5.2 PG 存储组件；full 远端 E2E
   仍等待持久宿主被接受。
6. **历史 git pins NOT_RUN**：4/4 companion fixtures 只验证当前字节与嵌入 provenance，
   没有 fetch 历史冻结 blob。
7. **11 个 direct runner 没有逐 runner XML**：`-reporter default` 只产生 raw console
   summary + meta；不能声称存在 XML 并已解析。
8. **没有 exit-code 6 launcher 探针**：只有 0/1/2/17/timeout/forced-writer 六个 case；
   若验收意图是 exit 6，必须标 NOT_RUN。
9. **`b5c1c17` FAIL 与 `39970580589` UNRESOLVED 保留**：新 run 不关闭旧根因。
10. **181 classified 仍未覆盖；G2–G5 未批准**：本报告不改变门禁状态。

本轮在 `9136508` 的双 CI 证据范围内未发现新的实质 BLOCKER/MAJOR/MINOR 不一致。由于
本任务是只读审计，不修改代码、不放松断言、不新增 verifier 例外；若 caller 认为
"11 runner 无 XML"或"无 exit-6 探针"本身是验收缺陷，应按上文两项 NOT_RUN 处理，
而不是把它们写成通过。

---

## 8. 门禁与任务分配（本轮初始台账）

### 8.1 门禁

- **G1：APPROVED（既有）**；本审计不重新批准、不改变结论。
- **G2–G5：unapproved**。
- **V4：production**；V5 未切换、未部署。
- **181 classified：仍 uncovered**。
- **Android 工具链/精简 Wire AOT APK 组件：已接受**；但 full-game Android/真机
  **NOT_RUN**，iOS **BLOCKED/NOT_RUN**，跨物理网络 **NOT_RUN**。
- **P4.2-B/P4.5：仍等待 accepted LocalHost + Unity B2b**。
- **full 远端 E2E：仍等待 accepted 持久 WSS 宿主**。
- 本报告不是 G2/G3，不是 receiver 代码接受，也不是 PR20 合并批准。

### 8.2 初始台账（一次读取，不是事后轮询）

在审计开始时执行了**一次** `mcp__agent-mesh-bridge__list_recent_tasks`
（`statusFilter=all`, `limit=30`），记录到：

| Receiver | Active/target | 线程 | 观察状态 |
|---|---:|---|---|
| DeepSeek | 2/2 | B2b `thread_20261027_230256_426c`；本任务 `thread_20261028_034458_e06d` | `reworking`（round 3）；`running`（round 1） |
| GLM / Claude Code | 1/1 | durable-host `thread_20261027_230302_93fb` | `reworking`（round 1） |
| Anti / Antigravity | 1/1 | atomic-store `thread_20261027_154439_11d2` | `reworking`（round 4） |
| **合计** | **4/4** | | |

- 本任务没有 dispatch、转派、resume、pause、retry、retarget 或派生 subagent。
- 原 P4.1c/d 手动暂停未被恢复/绕过/复制；failed parent 与旧失败责任保留。
- 上述台账只用于本轮初始分配核对，不是后续轮询，也不构成对其它线程的验收。

---

## 9. 可复现命令、证据路径与 manifest

### 9.1 可复现命令

下载（在 `$E` 下；脚本使用 `gh auth token`，不打印 token）：

```powershell
$E = 'D:\majiang-wt\pr20-ci-audit-ds-20261028\build\pr20-ci-audit-20261028-b2609aa2b00e'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $E/download-evidence.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $E/download-run2.ps1
```

重新计算/核对（只读；不触发 CI）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $E/audit-ci.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $E/audit-facts.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $E/make-manifest.ps1
```

Git 身份与 tree：

```text
git rev-parse HEAD
git show -s --format="%H%n%P%n%T%n%an%n%ad%n%s" 0247000d29276b75d9d21a4d888acd918f378905
git diff --exit-code --quiet 9136508 0247000
git ls-remote origin refs/pull/20/head refs/pull/20/merge
git diff --check 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7..HEAD
```

Android 集成范围：

```text
git diff --quiet 0e7dd70 bd5d453 -- tools/unity/repair-android-toolchain.ps1
git diff --quiet 0e7dd70 bd5d453 -- tools/unity/test-wire-aot-platform-recheck.ps1
git diff --quiet 0e7dd70 bd5d453 -- tools/tests/VerifyV5AndroidToolchain.Tests.ps1
git diff --name-status c7001a8 bd5d453
git diff --name-status bd5d453 9136508
git diff --quiet c7001a8 bd5d453 -- dotnet unity .github tools/ci tools/verify-v5.ps1 build.gradle
```

原 CI 入口（job 实际执行；本审计未在本地重跑）：

```text
./tools/ci/test-v5-native-exit.ps1
./tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -RunUnifiedVerification `
  -LogDir artifacts/verify-v5/table -UnifiedLogDir artifacts/verify-v5/unified
```

### 9.2 证据根与 manifest

```text
E = D:/majiang-wt/pr20-ci-audit-ds-20261028/build/pr20-ci-audit-20261028-b2609aa2b00e
```

该目录在本树 `build/` 下，被 `.gitignore` 忽略；未写入任何其它 worktree。

| 文件 | SHA256 |
|---|---|
| artifact zip（push） | `DF8258F92EAF85E5F2A7CE0E670E7468FB5BCF500CBB062CDD325817631252B2` |
| artifact zip（PR） | `4CACA58FC2851BEA80E8803AF81A3563E33F0092166EBCB82A01689660C1216D` |
| job log（push） | `E42B6CDA2B69A6D77A816CA8A5DE1F1CAED671B31A2D60A6D15FE8DA9C51254B` |
| job log（PR） | `606FFA94AB2FB064E4226DC53241F831524D84C0FDA3E86746CF6F41B8545004` |
| API `run.json`（push） | `533E51A8C4039B24553F0FF402291F19485BFB3858599641674C37184C48EBBA` |
| API `run.json`（PR） | `08F6EB2E3693F5D8BE1527B306C158C8D787848DC7199AF37BADB128993E18A3` |
| `summary.log`（push） | `0D0836F42EBE42B8293D37EC903F7E130A638AAEC6E7EC505325F5CDC7C47067` |
| `summary.log`（PR） | `1EB67D332C51674C4C4B5C85813F7B0D0D43518619E81410B1AE4D0BE8325D3A` |
| `sln-build.log`（push） | `34A133031955E1529465C5BED98638E62C790DE1E56CBED3A1DC4BC8AEDC7679` |
| `sln-build.log`（PR） | `D88B2E08E87AA03A1E97ED9C0F3E22E5BF83ACC30481F247B67BCD6671159E35` |
| `tests.xml`（push） | `3001E33B37A85D6C12A71B78B1A2FC97FB4F915B23BB74C09CF912E0B4C1A419` |
| `tests.xml`（PR） | `70707125B01EFEC858DA04F877F5AB42AEFA5C6F2082860EF3B2C1D60690A387` |
| `tests-targeted.xml`（push） | `69AEA89D30E57FC4DC9104708EC4CE0B8A689F7276A704F4911E37A7E11BFECC` |
| `tests-targeted.xml`（PR） | `7CABCB4B8DC4EC5561641212578BCDA95EA22216D343ABA2D70C76C8C1C4D55E` |
| `crash-reader-result.json`（两 run 相同） | `C879E5C627EBC0D174E3A98F826F38A40007C9DDD3C1BA5214378FE063B121B4` |
| `restore-reader-result.json`（两 run 相同） | `B0D075D5336AFC4B5720781FBDEC3E08D9641D8236111460F75BB83D19FCCA86` |
| `postgres-evidence.jsonl`（push） | `B7C9DA568C51A1FD422DF3F6337EAEE5E8F0206B391FCA5CB632CD24BC9DB985` |
| `postgres-evidence.jsonl`（PR） | `4976FC6A8E5E1E4A01FC709B0048FE23405F194A22120FE95F9AA4E21575E02C` |
| `native-exit/.../results.json`（两 run 相同） | `3886116BC8BC9272683E1D9E16E2402CE6F1809EEC8EBC2BD8A98ADD1D0E3DD0` |
| `audit-ci-result.json` | `42460C074E5FFE3A2371BC912DB78D3F966FF7454D5A1BE364BAD8F154D6F6F1` |
| `audit-facts.txt` | `E5161A11F3D75646E64C574322E7F893A0B47ED2E5349D80D8489F1C68DC31F1` |
| `ledger-allocation.json` | `980A0F072EEDF4F13AE9F187BC6C65007020942471BAC302D8C3762E043DBC66` |
| **`manifest.sha256.txt`** | **`F41508993BB0575DE4BDAB91506E57FB8ADA30E3C4A9ECEE82FC34ACBD6BA0C4`** |

Manifest 覆盖 **473 files / 30,382,279 bytes**；`manifest.meta.json` 记录
`files=473`、`totalBytes=30382279`、上述 SHA。已逐行重新计算 473 个文件，0 mismatch。

### 9.3 关键原始路径

```text
$E/run-40564851868/api/run.json
$E/run-40564851868/api/jobs.json
$E/run-40564851868/api/artifacts.json
$E/run-40564851868/job-logs/122403272562-verify.log
$E/run-40564851868/artifact-zips/v5-verify-logs-40564851868-1.zip
$E/run-40564851868/artifacts-extracted/v5-verify-logs-40564851868-1/verify-v5/unified/
$E/run-40564851868/artifacts-extracted/v5-verify-logs-40564851868-1/verify-v5/table/
$E/run-40564851868/artifacts-extracted/v5-verify-logs-40564851868-1/native-exit-8ffd0d6ff27848b691bcd3c958636196/

$E/run-40564866626/api/run.json
$E/run-40564866626/api/jobs.json
$E/run-40564866626/api/artifacts.json
$E/run-40564866626/job-logs/122403324037-verify.log
$E/run-40564866626/artifact-zips/v5-verify-logs-40564866626-1.zip
$E/run-40564866626/artifacts-extracted/v5-verify-logs-40564866626-1/verify-v5/unified/
$E/run-40564866626/artifacts-extracted/v5-verify-logs-40564866626-1/verify-v5/table/
$E/run-40564866626/artifacts-extracted/v5-verify-logs-40564866626-1/native-exit-290f17d5449c4f58a296568be2a5d901/
```

关键单项：

```text
$E/run-40564851868/.../verify-v5/unified/summary.log
$E/run-40564851868/.../verify-v5/unified/sln-build.log
$E/run-40564851868/.../verify-v5/unified/plugin-copy.log
$E/run-40564851868/.../verify-v5/unified/companion-fixtures.meta.txt
$E/run-40564851868/.../verify-v5/unified/runs/<project>/direct.meta.txt
$E/run-40564851868/.../verify-v5/unified/direct-<project>.log
$E/run-40564851868/.../verify-v5/table/tests.xml
$E/run-40564851868/.../verify-v5/table/tests-targeted.xml
$E/run-40564851868/.../verify-v5/table/crash-writer.log.kill.json
$E/run-40564851868/.../verify-v5/table/crash-reader-result.json
$E/run-40564851868/.../verify-v5/table/restore-reader-result.json
$E/run-40564851868/.../verify-v5/table/pg/postgres.log
$E/run-40564851868/.../verify-v5/unified/pg/postgres-evidence.jsonl
$E/run-40564851868/.../verify-v5/unified/pg/migrate.log
$E/run-40564851868/.../verify-v5/unified/pg/cleanup.log
```

（PR run 将 `40564851868` 替换为 `40564866626`；`native-exit-*` 目录名按上表。）

---

## 10. 约束执行情况

- 只在 `D:/majiang-wt/pr20-ci-audit-ds-20261028` 的桥接原生会话执行；未转派、未派生
  subagent、未改派/恢复其它线程。
- 未修改生产/测试源码、workflow、verifier/架构白名单、sln、Unity 资源、scheduler、
  共享 SDK/Editor/PATH/信任；未启动共享 Editor。
- 未写其它 worktree；用户 `Resources`/`Burst`/环境未改。
- 未 push、未创建/修改 PR/MR、未合并/部署、未创建 `complete.flag`。
- 原 P4.1c/d 手动暂停未恢复/绕过/复制；failed parent 与旧失败责任保留。
- 新增证据只在 `$E`（本树 ignored `build/` 的 GUID 独占目录）；本报告是本任务唯一
  tracked 新增文件。
- 本报告只审计 CI/代码证据，不是 G2/G3 或 receiver 代码接受；不改变 G1/G2–G5 状态，
  也不改变 PR20 的合并决定。
