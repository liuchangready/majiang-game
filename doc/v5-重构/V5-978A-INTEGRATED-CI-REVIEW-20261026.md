# V5 P5.4a session operations 新头（978a156）集成证据核验报告

日期：2026-10-26。
执行者：Claude Code / glm-5.3-flash 独立任务（agent-mesh-bridge 授权的四个独立任务之一）。
工作树：`D:\majiang-wt\integrated-ci-glm-20261026`（自有 temporary git worktree，分支 `wt/v5-integrated-ci-glm-20261026`）。
冻结基线：`978a15667f80094ed63c86f69c53195439c95a2c`（PR19 已发布头，工作树 clean，与远端 PR19 headRefOid 一致）。
本任务唯一授权交付文件：`doc/v5-重构/V5-978A-INTEGRATED-CI-REVIEW-20261026.md`（即本报告）。
版本溯源：第 1 轮曾误以 `doc/v5-重构/V5-P54A-SESSIONOPS-NEWHEAD-EVIDENCE-20261026.md` 路径交付（commit `8910f13`）；第 2 轮按返修 D-C1-01 移至本唯一授权路径并按 D-C1-02 补齐证据完整性，内容主体与实测数据不变。未修改任何生产/测试/fixture/verifier/旧验收/CLAUDE.md/PROMPTS.md/Unity 环境。P2.2（thread_20261026_103011_957d）未读取、未计入。未触发任何 retry/rerun/dispatch/push/CI。G1 总判定、complete.flag 均不在本任务权限内。

结论先行：**978a156 已具备"新头集成证据"**——push run 对本头的精确 checkout 全绿、P5.4a session operations 增量的 21 个新用例以计数闭合方式确认在新头 CI 中真实执行、四个增量文件在本头字节级保留、本地 fresh Release 定向 direct runner 46 用例（21+25）全绿。该结论是**增量级集成证据**，不是 G1 通过、不是全量本地重跑替代、也不是对 39970580589 的根因结论。

---

## 1. CI 产物核验（gh 只读，无 retry/rerun/dispatch）

### 1.1 run 元数据与产物下载

| 项 | run 40273525993（push） | run 40273530505（PR） |
|---|---|---|
| URL | https://github.com/liuchangready/majiang-game/actions/runs/40273525993 | https://github.com/liuchangready/majiang-game/actions/runs/40273530505 |
| event / branch | push / feat/v5-1-integration | pull_request / feat/v5-1-integration |
| status / conclusion / attempt | COMPLETED / success / 1 | COMPLETED / success / 1 |
| created（UTC） | 2026-10-26T05:18:40Z | 2026-10-26T05:18:43Z |
| job | verify #121411310049 success | verify #121411323493 success |
| GITHUB_SHA（实际 checkout） | **978a15667f80094ed63c86f69c53195439c95a2c**（本头精确） | **a3ac2fefd38c09dba6bf6c057752d28fc1461d6b**（merge ref `refs/pull/19/merge`） |
| artifact | v5-verify-logs-40273525993-1，id 12628399668，1,330,443 B，expired=false | v5-verify-logs-40273530505-1，id 12629840637，1,330,362 B，expired=false |

下载命令（原样，gh 只读）：

```
gh run download 40273525993 --repo liuchangready/majiang-game -D artifacts/ci-978a156-20261026/run-40273525993   # exit 0
gh run download 40273530505 --repo liuchangready/majiang-game -D artifacts/ci-978a156-20261026/run-40273530505   # exit 0
```

**原始 archive 条目与本地派生文件口径（D-C1-02 修正）**：两 run 下载后的即时 `find -type f | wc -l` 均为 **62**（该计数在生成任何本地派生文件之前取得），且下述两份 62 行清单在生成时以 `find ! -name "evidence-sha256-unix.txt"` 显式排除本地派生项——因此 **62 = CI 原始 archive 条目数，两份清单 = 原始条目全量 SHA256，不含任何本地文件**。当前本地目录额外含 **3 个本地派生校验文件**（非 CI 产物）：push run 根 `20261026-051901-pg-d8c01017/evidence-sha256-unix.txt`、push run `pg/evidence-sha256-unix.txt`、PR run `pg/evidence-sha256-unix.txt`（均为 `pg/evidence-sha256.txt` 的 CRLF→LF/反斜杠→正斜杠转换副本，供 `sha256sum -c` 使用；push 目录现状 64 文件、PR 目录现状 63 文件）。另有 2 份本地生成的清单文件。全部原始/派生/清单文件均位于 ignored `artifacts/`，不入库。

全量 SHA256 清单（本地生成，62 行=原始条目）：

| 清单 | SHA256（完整） |
|---|---|
| `artifacts/ci-978a156-20261026/sha256-push-run.txt` | `f6f83d4b5600e89ceb6a9c136adbdfa175877751a7268222a8009bcfb0f0e527` |
| `artifacts/ci-978a156-20261026/sha256-pr-run.txt` | `dbc56ed49ee0fa749b588827d8b72acfdc6847601d0a4b5ab99af2727938836e` |

run 元数据 JSON（gh 只读取得）：

| 文件 | SHA256（完整） |
|---|---|
| `artifacts/ci-978a156-20261026/run-40273525993-meta.json` | `cf5e3fe1e82d51814d67d819fe8e1661f0203c2c73293310478fc1209fab4bb2` |
| `artifacts/ci-978a156-20261026/run-40273530505-meta.json` | `9ebdeb8da636244bd1de668c1423240298376a8055697d12b85e19d19811b35c` |

PR merge commit 已核实：`a3ac2fef` 的 parents 为 `457274c9b74643a9e7dc73cabfb64cc8a2eed223`（main）+ `978a15667f80094ed63c86f69c53195439c95a2c`（PR19 head）——PR run 绿是"含本头的 merge 树"绿；**本头精确证据以 push run 为准**（与 08c4a75 时代 G1 审查的口径一致）。

### 1.2 脚本溯源

两 run `run-info.txt` 记录 `tools/verify-v5.ps1` SHA `FA8D539BB6E036A7FD002011F001F5D5FDB27BA8B20CD5A9089098E87F3CE307`；本头该文件实际 SHA 逐字一致（且与 V5-G1-RUNTIME-REVIEW-20261026.md:254 记录的 08c4a75 时代哈希相同——验证器未被改动）。manifest `tools/v5-verify.manifest.json` 本头 SHA `8F75E014EE34FCBD8974C6B32BA04CF41F6EFD70E521F858A3AD0196CC264886`。CI provenance 行记录 GITHUB_REPOSITORY/GITHUB_RUN_ID/GITHUB_RUN_ATTEMPT=1/GITHUB_SHA/RUNNER_OS=Windows。

### 1.3 计数与零 skip（框架日志逐项）

| 工程 | push run（框架日志） | PR run（框架日志） | 08c4a75 时代基线 |
|---|---|---|---|
| Mahjong.Ai.Tests | 444 / 0 / 0 | 444 / 0 / 0 | 444 |
| **Mahjong.Client.Tests** | **153 / 0 / 0** | **153 / 0 / 0** | **132** |
| Mahjong.Client.Wss.Tests | 80 / 0 / 0 | 80 / 0 / 0 | 80 |
| Mahjong.Contracts.Tests | 149 / 0 / 0 | 149 / 0 / 0 | 149 |
| Mahjong.Domain.Tests | 517 / 0 / 0 | 517 / 0 / 0 | 517 |
| Mahjong.LobbyService.Tests | 89 / 0 / 0 | 89 / 0 / 0 | 89 |
| Mahjong.TableRuntime.Tests | 397 / 0 / 0 | 397 / 0 / 0 | 397 |
| **合计** | **1,829 / 0 / 0** | **1,829 / 0 / 0** | 1,808 |

**+21 计数闭合**：153 − 132 = 21，恰等于新增 `TableSessionOperationQueueTests` 的 21 个 `[Fact]` 方法（本头源码 grep 计数一致）；总数 1,829 − 1,808 = 21，其余六个工程计数逐一不变。这证明新头 CI 真实执行了增量用例，不是旧产物或刷新摘要。sln-build 两 run 均 PASS exit 0、`0 Warning(s) 0 Error(s)`。

如实限制：**CI 框架日志为 summary 级（UTF-16），无 per-test 明细**（Client.Tests 框架日志全文 948 字节，仅 total/failed/succeeded/skipped + 命令行）。per-test 证据不由 CI 提供，由本报告 §3 本地 direct runner 的 XML 补齐；本报告不捏造 CI per-test 结果。Client.Tests 框架日志 SHA `24A62057B639774DA57F03D7753F904957393F777D074F4FD6CC88FC126683C1`，与该 run `dotnetTest.meta.txt` 记录逐字一致（meta 记录 command=`dotnet test …-c Release --nologo`、exit 0、total 153、failed 0、skipped 0、head=978a156、TestResults 运行前已清点"not present before the run"）。

### 1.4 PG wrapper（两 run 同构核验）

push run `pg/wrapper-summary.json`（SHA `3A227756B1BFF86FCF63CBDF63C73C9BE757F4E0F09E001095368CD1E320A992`，与 evidence 清单自洽）：

- 10 阶段全 PASS exit 0：precheck（端口空闲）→ start-postgres（身份已验证）→ ownership-marker → environment（为子进程设 4 变量）→ migrate（001_lobby.sql 应用两次 exit 0）→ verify（verifier exit 逐字传播）→ archive-cluster-evidence → cleanup（owned cluster 已删除，rootDeleted=true）→ env-restore（`restoredToPreCall=true`，4 变量恢复为 pre-call absent）→ write-evidence。failedStages=[]，plannedExit=0。
- PG 实体：PostgreSQL 16.10，端口 55460，库 mahjong_lobby_test，cluster root 位于 runneradmin Temp 下的专用目录，ownership="owned"。
- `pg/evidence-sha256.txt` 12 项哈希两 run 各自 `sha256sum -c` 全 OK（经上述派生转换副本校验，exit 0）：cleanup.log、env-restore.jsonl、migrate.log、owner-marker.json、postgres-evidence.jsonl、start-postgres.log、verifier-console.log、wrapper-steps.log、wrapper-summary.json、root-logs×3。保留全部原始日志（无失败日志——两 run PG 路径零失败）。
- `postgres-evidence.jsonl` 实际内容：server.identity（PG 16.10）、terminal.projection（ROOM_CLOSED/assignment CANCELLED/新加入拒绝）、receipt.terminalReplay（join/spectate/issue 重放、同载荷）、recovery.expiredReservation、migration.repeated（迁移 3 次、lobby 8 表、唯一索引断言）、concurrency.crossRoomJoin（ALREADY_ASSIGNED 拒绝）。
- **归属口径**：以上全部为 **Lobby 存储层（P5.3 侧）验证**；本任务不将其冒充为 P5.2（TableRuntime 持久化）证据。

### 1.5 插件闭包（12 DLL）

两 run plugin-copy 均 PASS 且清单逐一相同：Google.Protobuf 3.28.3、Mahjong.Ai、Mahjong.Client、Mahjong.Contracts、Mahjong.Contracts.Wire、Mahjong.Domain、Mahjong.LocalHost、Mahjong.TableRuntime、System.Buffers 4.4.0、System.Memory 4.5.3、System.Numerics.Vectors 4.4.0、System.Runtime.CompilerServices.Unsafe 4.5.2。tracked Unity assets untouched。注：Mahjong.LocalHost.dll 仍为 3,584 字节空壳——本头 `dotnet/src/Mahjong.LocalHost` 下 0 个 `.cs`，与 P4 未启动（G1 未批准）的计划状态一致，非遗漏。

## 2. P5.4a session operations 验收记录核实（先核名）

- 任务简报所称 `CODEX-V5-P54A-SESSION-OPERATIONS-ACCEPTANCE.md`：**在 978a156 工作树与全部 git 历史中均不存在**（`git log --all --name-only` 与全库 grep 均无）。
- 实际"已接受"载体仅有：① merge commit `74216a7`（"Merge independently accepted P5.4a session operation queue into unified PR19"，merge of 08c4a75 + bfc0839）；② `CODEX-V5-G1-RUNTIME-REVIEW-ACCEPTANCE.md` 第 43 行（"The merge retains already accepted P5.4a and gate 2 changes"）——且同文件第 47 行明确 **"It does not certify a new full-suite run on the newer P5.4a integration head"**，即本任务要补的证据缺口。
- `doc/` 全库对 `TableSessionOperationQueue` / `ITableSessionFrameIngress` 的技术记载为零（唯一相关行是 `V5-P4.2-SHELL-PREPARATION.md:1311` 的修复要求："由 Client/P5.4a owner 增加明确的 session operation queue 或 channel frame ingress API；P4.2 的 SessionCoordinator 只调用该入口"——本增量即该要求的交付物）。`V5-0-TRACEABILITY.md:310` 的 P5.4a "partial" 口径早于本增量，未更新。
- 该缺口的缺陷登记见 §6 D-R3-01；本报告 §1/§3/§4 的独立实测即对该增量的补充证据。

## 3. 本地 fresh 构建与定向 direct runner（自有冻结树）

前置：`git rev-parse HEAD` = 978a156，`git status --porcelain` 空。SDK：`D:/Unity/dotnet/dotnet.exe` 8.0.426（与 CI 同版本；本机运行时 .NET 8.0.32 x64）。进程级 `TEMP/TMP=D:\mj-icglm-temp-20261026`（D 盘独占，仅进程内设置）。

```
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-incremental --nologo
  → exit 0，已成功生成，0 个警告 0 个错误
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Wss.Tests/Mahjong.Client.Wss.Tests.csproj -c Release --no-incremental --nologo
  → exit 0，已成功生成，0 个警告 0 个错误
```

真实类名核实：`Mahjong.Client.Tests.TableSessionOperationQueueTests`（dotnet/tests/Mahjong.Client.Tests/TableSessionOperationQueueTests.cs，本头新增）；`Mahjong.Client.Wss.Tests.TableSessionRunnerTests`（dotnet/tests/Mahjong.Client.Wss.Tests/TableSessionRunnerTests.cs，属 **Client.Wss.Tests** 工程而非 Client.Tests，故两工程各做 fresh 构建；该文件本头未改动，Wss.Tests 计数 80 与旧头一致）。无需以完整 suite 替代。

定向运行（xUnit v3 4.0.2 direct runner，运行对象为本树 fresh Release 新 DLL，非旧产物）：

```
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Client.Tests/bin/Release/net8.0/Mahjong.Client.Tests.dll -noColor -noLogo -class Mahjong.Client.Tests.TableSessionOperationQueueTests -result-xml artifacts/local-978a156-20261026/operationqueue.xml
  → native exit 0；EXECUTION SUMMARY：Total 21, Errors 0, Failed 0, Skipped 0, Not Run 0
  → XML assembly 行：total="21" passed="21" failed="0" skipped="0" not-run="0" errors="0"（21 个结果全部 result="Pass"，0 Fail/Skip；选择非 0 核对通过）

D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Client.Wss.Tests/bin/Release/net8.0/Mahjong.Client.Wss.Tests.dll -noColor -noLogo -class Mahjong.Client.Wss.Tests.TableSessionRunnerTests -result-xml artifacts/local-978a156-20261026/runner.xml
  → native exit 0；EXECUTION SUMMARY：Total 25, Errors 0, Failed 0, Skipped 0, Not Run 0
  → XML assembly 行：total="25" passed="25" failed="0" skipped="0" not-run="0" errors="0"（25 个结果全部 result="Pass"；选择非 0 核对通过）
```

要求行为的用例覆盖映射（全部实跑全绿，名称取自 XML `name` 属性）：

| 要求行为 | 覆盖用例（Client.Tests/TableSessionOperationQueueTests，除注明外） |
|---|---|
| pump ingress 拆分（receive 只等传输、apply 单 owner 变更状态） | `RunnerDrivesSplitIngressAndAcceptsSubmitWhileReceiveIsParked`（经 runner 实驱 split ingress）、`QueuedDisconnectFencesRunningReceiveAndNextPumpCanContinue`、`OpenCompletionPublishesHandshakingBeforeCallerResumes`、`ApplyCompletionPublishesOwnerStateBeforeCallerResumes` |
| operation lane 串行与顺序 | `ConcurrentSubmitsExecuteInSequenceOrderAndExposeLifecycle`、`InboundApplyWaitsForRunningSubmitAndThenAdvancesInOrder`、`ThousandSubmitReceiptAndPushInterleavingsKeepOneConsistentBaseline`、`CallbackReentrancyAndOperationFaultsDoNotPoisonTheOwnerQueue` |
| 阻塞 receive 时命令仍推进 | `ParkedReceiveDoesNotBlockSubmitAndCloseFencesLateFrame`、`RunnerDrivesSplitIngressAndAcceptsSubmitWhileReceiveIsParked`、`CanceledPumpsRetainAtMostOneUnfinishedReceivePerBinding` |
| close/reconnect/cancel/disposal 顺序 | `CloseCancelsQueuedSubmitsAndIsIdempotent`、`CloseCompletionPublishesClosedBeforeCallerResumes`、`CloseEndsParkedReceiveWhenChannelHonorsCancellation`、`DisconnectCompletionPublishesReconnectingBeforeCallerResumes`、`QueuedDisconnectFencesReceiveFaultWithoutDeadlock`、`ParkedReceiveIsFencedByDisconnect`、`RunnerStopsWhenQueueClosesAnIgnoringReceive`、`CallerCancellationEndsIgnoringReceivePumpWithoutApplyingLateFrame`、`CancellationAfterDurableSaveKeepsIdentityAndReconcilesById`、`ReconnectAppliesBaselineBeforeQueryingAndNeverResendsBlindly`（Wss.Tests/TableSessionRunnerTests 另有 25 例 runner 级 close/reconnect/cancel 序回归） |
| 未知结果不盲重做（P5.4a 门槛语义） | `ReconnectAppliesBaselineBeforeQueryingAndNeverResendsBlindly`、`CancellationAfterDurableSaveKeepsIdentityAndReconcilesById`、`SpectatorAdmissionFailsBeforePersistenceAndSend` |

disposal 口径说明：`TableSessionOperationQueue` 不实现 `IDisposable`；资源终局以 `CloseAsync(reasonCode)`（IsClosing/IsClosed 幂等门）表达，迟到的 receive 帧被 `ITableSessionFrameIngress.ApplyFrameAsync` 的终态门丢弃，CancellationTokenRegistration 均在终态路径显式 Dispose。上述 close/cancel 序用例即其顺序覆盖。

### 3.1 本地证据完整 SHA256 总表（D-C1-02 修正，全部完整 64 hex，无一缩写）

| 证据文件 | SHA256 |
|---|---|
| `artifacts/local-978a156-20261026/build-Client.Tests.log` | `89218f9c86b704d3e7dfbfcd5d684c97cb35de36f8cbd111eacded74eb716fe2` |
| `artifacts/local-978a156-20261026/build-Client.Wss.Tests.log` | `17d1fc3ce30e76b7a90a6c082eb5782e6851ce813d74fea116e450a43eabc62f` |
| `artifacts/local-978a156-20261026/run-OperationQueueTests.log` | `28d157a673adf0b2e294eaef64a65cf224c9b62ec504951fa457a884e8a4b8a9` |
| `artifacts/local-978a156-20261026/run-RunnerTests.log` | `3f579b192ae8540533b8cf28532dd5ab00498a475d0f94087e4ad7c5401d2fd4` |
| `artifacts/local-978a156-20261026/operationqueue.xml` | `0fd5298933b2a8a6ea43f6f6f54d3d8eb6ba8c22b9838fb57dc8d0774e7f8bf1` |
| `artifacts/local-978a156-20261026/runner.xml` | `f2f43f49658b496fbe308c30298e59c51fef1ce4fa7f4b01dc0bf077a00ef922` |
| `artifacts/ci-978a156-20261026/run-40273525993-meta.json` | `cf5e3fe1e82d51814d67d819fe8e1661f0203c2c73293310478fc1209fab4bb2` |
| `artifacts/ci-978a156-20261026/run-40273530505-meta.json` | `9ebdeb8da636244bd1de668c1423240298376a8055697d12b85e19d19811b35c` |
| `artifacts/ci-978a156-20261026/sha256-push-run.txt` | `f6f83d4b5600e89ceb6a9c136adbdfa175877751a7268222a8009bcfb0f0e527` |
| `artifacts/ci-978a156-20261026/sha256-pr-run.txt` | `dbc56ed49ee0fa749b588827d8b72acfdc6847601d0a4b5ab99af2727938836e` |
| CI push run `summary.log` | `81d794f34a53e5ee6eb4d01497c6683f9e0b112ac506815ae3f1f8ebda755e58` |
| CI push run Client.Tests 框架日志 | `24a62057b639774da57f03d7753f904957393f777d074f4fd6cc88fc126683c1` |
| CI push run Client.Wss.Tests 框架日志 | `7b420e10d4d3209fa2c84898dc8e862c4fb101ef9c81989534ab75dea4b9d6aa` |
| CI push run `pg/wrapper-summary.json` | `3a227756b1bff86fcf63cbdf63c73c9be757f4e0f09e001095368cd1e320a992` |
| CI PR run `summary.log` | `721a8774bb232dc54b2f9c45231c38c1915aed0743c6d542dba4e35e3e3702a3` |
| CI PR run `pg/wrapper-summary.json` | `c16fc4bb052fb484ef38b091318f02548e1bf5e25a52cc71cde213da7697e3e6` |

注：XML 含运行时间戳/GUID，每轮独立生成必然不同哈希；caller 复验轮自产的 operationqueue.xml（`E813B611CEEDEB09756154AF3F1324E84312DBC46A1FB20D95C13EBD9E464A9E`）/runner.xml（`84151D9CC09EE8BC4CD1850FA416AC63480D7E59AB91DD7FA5B41F14FAFECBEE`）与本报告所载本任务 XML 属不同运行实例，两者各自与自身日志/exit 自洽，互不替代。本任务 XML 与日志字节未因返修改动。

## 4. 集成遗漏审查（对照"已接受"增量，非抄 CI 标签）

1. **内容完整性**：`git diff bfc0839..978a156` 限定四个增量文件（TableSession.cs / TableSessionOperationQueue.cs / TableSessionRunner.cs / TableSessionOperationQueueTests.cs）输出 0 行——增量内容在本头**字节级保留**，未被后续 G1 文档轮次改动。
2. **CI 真实执行**：+21 计数闭合（§1.3）证明新用例在新头 CI 实际运行且全绿，不是旧头 1808 的复用。
3. **激活边界（计划内，非遗漏）**：本头生产代码中 `TableSessionOperationQueue` 仅被 `TableSessionRunner` 以可选参数（默认 null，保持旧路径）引用；全部构造点均在测试代码；`unity/` 与生产组合根零引用。指定消费者是 P4.2 的 `SessionCoordinator`（V5-P4.2-SHELL-PREPARATION.md:1311），而 P4 属 G1 批准后的后阶段、不得启动。因此"队列已交付、尚未接线生产入口"是**当前计划内的边界状态**，本报告如实记录，不称为缺陷、也不称为已激活。
4. **行为差异观察（接线时需明确，非当前缺陷）**：组合路径（无队列，08c4a75 起既有）中 ReceiveAsync 的非关闭类异常会冒泡到 runner 以 `UnexpectedError` 终止；队列路径经 `ApplyReceiveFaultAsync` 将其归为 `Fault(InternalError)`（会话 FAULTED，runner 以 Faulted 终止）。两路径均为终态且队列路径有专门用例（`QueuedDisconnectFencesReceiveFaultWithoutDeadlock`）；因队列路径尚无生产接线，现阶段不构成行为回归。P4.2 接线时必须择一明确并对齐。
5. **runner 集成面**：`TableSessionRunner` 的 Open/Pump/State/Binding/LastError 在传入队列时全部改道队列（含 `operations.Session` 与 session 同实例校验、IsClosing/IsClosed 提前 Closed 停止），由 `RunnerDrivesSplitIngress…`/`RunnerStopsWhenQueueCloses…` 与 Wss.Tests runner 序回归覆盖。

## 5. 范围结论：978a156 是否具备新头集成证据

**具备（增量级）。** 证据三角：① push run 40273525993 对 978a156 的精确 checkout 全绿（1,829/0/0，脚本哈希与本头一致，PG wrapper 全绿且证据哈希 12/12×2 run 闭合）；② 增量 21 用例以 132→153 计数闭合确认在新头 CI 真实执行；③ 本地同 SDK fresh Release 定向 direct runner 46 用例（21+25）全绿、非零选择、0 skip/0 not-run，XML 与 exit 在案；④ 增量四文件本头字节级保留。

**该结论不外推的部分**：PR run 为 merge ref `a3ac2fef` 绿（含 main 457274c 的合并树），不作本头精确证据；CI 框架日志无 per-test 明细（per-test 证据由 §3 本地 XML 提供）；PG 证据仅 Lobby 存储层（P5.3 侧），非 P5.2；队列未接线生产入口（P4 未启动）；`Mahjong.Client.Wss.Tests` 的 80 例为未改动的既有回归（其绿不含增量新语义，增量语义由 Client.Tests 21 例承载）；G1 总判定、G5、complete.flag 留 caller，本报告不代判。历史 run 39970580589（Client131/132）根因 **UNRESOLVED 承继**：当前绿不构成其 flaky/fixed 证明，本任务未重跑、未重试该 run。

## 6. 缺陷清单（按 V5-REVIEW-REWORK-PROTOCOL §1.3 格式，一次给全）

第 1 轮缺陷总数：**1**（MINOR 1）；第 2 轮返修新增对交付自身的缺陷 2 组（D-C1-01 MAJOR、D-C1-02 MINOR，均已在第 2 轮修正，见版本溯源）。

```text
[D-R3-01]
严重级别：MINOR（文档/可追溯性，非功能）
位置：doc/v5-重构/ 缺文件 + V5-0-TRACEABILITY.md:310 + doc/v5-重构/CODEX-V5-G1-RUNTIME-REVIEW-ACCEPTANCE.md:43,47
复现：git log --all --oneline --name-only | grep -i "SESSION-OPERATIONS"（空）；
      git show --stat 74216a7（仅 merge 信息，无验收文档）；
      grep -rn "TableSessionOperationQueue\|ITableSessionFrameIngress" doc/（仅 V5-P4.2-SHELL-PREPARATION.md:1311 需求行）
实际：已接受增量 bfc0839/74216a7（session operation queue + split pump ingress，+3,185 行）在仓库内无任何
      验收记录文件；"已接受"主张仅存在于 merge commit 信息与 G1 验收记录的一句转述；TRACEABILITY 的
      P5.4a 口径早于该增量；doc/ 对新公共类型零技术记载。
期望：按仓库既有惯例（CODEX-V5-*-ACCEPTANCE.md 系列），每个"独立已接受"增量应有可独立复核的验收记录
      （范围、缺陷清单/负例、证据指针），且追溯矩阵反映其存在。
证据：本报告 §2；artifacts/ci-978a156-20261026/（本报告 §1 全部哈希）；artifacts/local-978a156-20261026/（§3）
修复要求：由 caller/P5.4a owner 将原始验收记录（bridge ledger 中如存在）固化为 doc/v5-重构/ 下的验收文档，
      或在 V5-0-TRACEABILITY.md / progress 文档补一行登记该增量（内容 bfc0839、集成 74216a7、本报告为其
      新头证据）；禁止回写改写任何既有验收文档内容。该项为 caller 侧追溯补记，本轮返修不要求执行。
验收断言：下一头 `git log --all --name-only | grep -i "SESSION-OPERATIONS"` 非空且新文档引用 bfc0839/74216a7
      与本报告路径；`grep -c "TableSessionOperationQueue" doc/v5-重构/*.md` ≥ 2。

[D-C1-01]（第 2 轮返修项，已修正）
严重级别：MAJOR（交付路径）
位置：第 1 轮 commit 8910f13 新增 doc/v5-重构/V5-P54A-SESSIONOPS-NEWHEAD-EVIDENCE-20261026.md
修复记录：本报告已移至唯一授权路径 doc/v5-重构/V5-978A-INTEGRATED-CI-REVIEW-20261026.md（本文件），
      原错误路径已删除；后续最小 commit 保留既有提交历史，未 amend/rebase。
[D-C1-02]（第 2 轮返修项，已修正）
严重级别：MINOR（证据完整性/来源口径）
位置：原报告 §1.1（SHA 缩写、62 文件口径）、§3（build/run 日志 SHA）
修复记录：§1.1 补原始 archive（62 条/ run，下载后即时 find 计数在派生文件产生前取得）与本地派生
      （3 个 evidence-sha256-unix.txt + 2 份清单）的明确边界；§3.1 新增完整 64-hex SHA 总表 16 项
      （metadata×2、build×2、run 日志×2、XML×2、清单×2、CI summary/框架日志/wrapper-summary×6）；
      正文缩写 SHA 全部替换或补全。
```

未登记为缺陷的三项已知边界（§5 已述，供返修方与复核方对齐口径）：队列未接线生产入口（P4 计划内）、两 pump 路径接收异常停止类别差异（接线时定夺）、CI 框架日志无 per-test 明细（验证器设计如此，未经本任务修改）。

## 7. 口径表

| 项 | 口径 | 依据 |
|---|---|---|
| push run 40273525993 全量（本头精确） | **PASS** | summary OVERALL PASS、1,829/0/0、sln 0W0E、PG 12/12 哈希 |
| PR run 40273530505 全量（merge ref） | **PASS（含本头，非精确 checkout）** | §1.1 parents 核实 |
| TableSessionOperationQueueTests 定向 | **PASS** | exit 0，21/21/0/0/0，XML `0fd5298933b2a8a6ea43f6f6f54d3d8eb6ba8c22b9838fb57dc8d0774e7f8bf1` |
| TableSessionRunnerTests 定向 | **PASS** | exit 0，25/25/0/0/0，XML `f2f43f49658b496fbe308c30298e59c51fef1ce4fa7f4b01dc0bf077a00ef922` |
| 本地 fresh Release 构建（两工程） | **PASS** | exit 0，0 警告 0 错误 |
| P5.4a session operations 验收记录存在性 | **FAIL**（缺口，caller 侧补记） | §6 D-R3-01 |
| CI per-test 明细 | **NOT_RUN（CI 侧无此证据形态）** | 框架日志 summary 级；per-test 由本地 XML 补齐 |
| P5.2 持久化、Unity/IL2CPP、真机、弱网、跨物理网络 | **NOT_RUN** | 本任务范围外，未冒充 |
| 历史失败 run 39970580589 根因 | **UNRESOLVED（承继）** | 未重试、未重跑、不称 flaky |
| PG wrapper 真实起停/env 恢复 | **PASS（Lobby 存储层口径）** | §1.4，不冒充 P5.2 |

## 8. 未覆盖与依赖

- 未覆盖：本地全量 1,829 重跑（依约以两套本头 CI 代替）；PG/Unity/Gradle/数据库未触碰；未与兄弟任务共享任何资源；P2.2 未读取；`dotnet test --filter` 外层形态未用（按约直接用 direct runner `-class`）。
- 依赖与边界：本报告依赖 GitHub Actions 产物服务可持续下载（两 artifact 当前 expired=false）；依赖 caller 已确认的 PR19 headRefOid=978a156；临时证据均在 `artifacts/`（经 `.git/info/exclude` 忽略）与本树 ignored bin/obj，D 盘 TEMP 仅进程内指向。本任务未 push、未建 PR、未触发 CI、未动 worktree 归属；merge-back 义务由 caller 既有流程承接。
