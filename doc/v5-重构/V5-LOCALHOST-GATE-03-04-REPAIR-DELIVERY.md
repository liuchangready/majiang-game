# V5 LocalHost 汇合门禁 D-LH-GATE-03/04 返修交付

- 日期：2026-10-29（Asia/Shanghai）。
- 任务：仅修复已确认的 D-LH-GATE-03（MAJOR，visibility 冻结样本被就地改写）与 D-LH-GATE-04（MAJOR，冷恢复测试捕获错误权威边界）。
- 工作树：`D:/majiang-wt/lh-gate-repair-ds-20261029`；分支 `wt/v5-lh-gate-repair-ds-20261029`。
- 冻结 HEAD：`7f223f2870b91c579d0ba46c59f915ea02c2657d`（`merge(v5): prepare accepted LocalHost components for isolated gate repair`）。
- 起点与祖先：`origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`、唯一 PR20 accepted 头 `209de29504ded1991310decbc4e154949d949c35`、已组件接受的 LocalHost `865ca0a455bda625c660c6b7e1df049f88d4591e` 均已用 `git merge-base --is-ancestor` 核对为 HEAD 祖先（exit 0）。不是从旧 main、closed PR19 或其它分支开始。
- 本交付是 caller 隔离的本地候选修复，不代表完整 LocalHost 已接受，也没有 push/PR/MR/merge/deploy/`complete.flag`。

## 0. 结论与边界

| 项目 | 结论 |
|---|---|
| D-LH-GATE-03 | **本任务范围修复完成（本地候选）**：`tests/v5-golden/visibility-v1.json` 已恢复为 accepted blob `eddcc00`；accepted companion 4/4 PASS，负例按预期失败。 |
| D-LH-GATE-04 | **本任务范围修复完成（本地候选）**：冷恢复测试改为在确定性静止边界捕获，并通过独立 OS 进程比较 state/event 版本、roundId、136 实体、账本与回执；LocalHost 102/102（本地串行、PG 负载、重复运行）。 |
| D-LH-GATE-01 | **未修复，继续 OPEN**：TableService 控制 tag 映射不在本任务范围，未触碰 TableService/Contracts。 |
| D-LH-GATE-02 | **未修复，继续 OPEN/BLOCKED**：LocalTableRuntime/Store/ControlTests 的拒绝持久化不在本任务范围，未触碰。 |
| P4.2-B `thread_20261028_223624_e4ae` | 未触碰其 Unity 范围；其 parked waiting_review 依赖完整 LocalHost，仍等待。 |
| 完整 LocalHost / P4.5 / G2–G5 | **未通过**；仍受 D-01/D-02 与最终整合复验阻断。G1 仍 APPROVED，G2–G5 未批准，181 classified 未覆盖，V4 仍是唯一生产基线。 |
| 远端 | 未 push、未建/改 PR、未 MR、未合并、未部署；PR19 保持已合并关闭，PR21 未触碰。caller 继续负责把 accepted delta 整合到唯一 PR20。 |

本任务未转派、未创建子任务、未调用其它 agent；未恢复旧失败父线程或手动暂停任务。

## 1. 冻结与初始核对

开始修改前实际执行：

```powershell
git -C D:/majiang-wt/lh-gate-repair-ds-20261029 status --porcelain=v1 -b
git -C D:/majiang-wt/lh-gate-repair-ds-20261029 rev-parse HEAD
git -C D:/majiang-wt/lh-gate-repair-ds-20261029 merge-base --is-ancestor 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7 HEAD
git -C D:/majiang-wt/lh-gate-repair-ds-20261029 merge-base --is-ancestor 209de29504ded1991310decbc4e154949d949c35 HEAD
git -C D:/majiang-wt/lh-gate-repair-ds-20261029 merge-base --is-ancestor 865ca0a455bda625c660c6b7e1df049f88d4591e HEAD
```

结果：HEAD 精确为 `7f223f2870b91c579d0ba46c59f915ea02c2657d`；三个祖先检查均 exit 0；开始时 tracked clean。修改前 `visibility-v1.json` 的 blob 为候选 `b890845591cfe8e8108276e9d4ca5635d1e582d2`，accepted blob `eddcc00035978be64bca27efc27ec352ce686bf5` 在对象库中存在。

## 2. D-LH-GATE-03 — MAJOR — 冻结 visibility 样本被就地改写

### 2.1 位置与复现

- 位置：`tests/v5-golden/visibility-v1.json`，`sourceHashes` 中 `TableVisibilityProjection.cs` / `TableVisibilityContexts.cs` 的 raw/LF 元数据，以及 `renderSha256LfNormalized`。
- 原复现（报告 §4/§6）：

```powershell
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/verify-v5-companion-fixtures.ps1 `
  -RepoRoot D:/majiang-wt/lh-gate-repair-ds-20261029 -VerifyGitPins
```

- 修复前实际：候选 blob `b890845...`；LF 141,007 B / SHA `1852B55F4EA51F6A7F1A5CED8228D52B2364095CCD31162D2B55015913C58522`；CRLF 147,185 B / SHA `B388934E339086F9519EA2703B0110A131A224A1EB55F94B4CA7D8D9BCDAA2BE`；render `e8d8f1a543f0554adeed88383060f49ea0392e56e8e8648a0cb7caab1edce502`。accepted companion 报 `HASH_MISMATCH` 与 `RENDER_SHA_MISMATCH`。
- 期望：accepted 冻结字节与 accepted pin 精确一致，历史审计边界不被新源码摘要覆盖。

### 2.2 修复

按 caller 明确选择的最小修复，使用安全字节方法从 git 对象库恢复 accepted blob，不使用 PowerShell 文本重定向：

```powershell
cmd /c "git cat-file blob eddcc00035978be64bca27efc27ec352ce686bf5 > tests\v5-golden\visibility-v1.json"
git hash-object tests/v5-golden/visibility-v1.json
```

结果：`git hash-object` = `eddcc00035978be64bca27efc27ec352ce686bf5`；文件 141,007 B。

未修改：
- 任何 `pin`、`verify-v5-companion-fixtures.ps1`、`verify-v5-golden-catalog.ps1`、census 或旧报告；
- `Domain`/`TableRuntime` 生产实现；
- 未新增 `visibility-v2.json`。

新源码哈希与 render 仅作为本报告/ignored 证据，不写回 v1。

### 2.3 恢复后的精确字节

| 表示 | 字节 | SHA-256 |
|---|---:|---|
| LF（工作树/接受的 raw blob） | 141,007 | `30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E` |
| CRLF（按 accepted pin 转换核对） | 147,185 | `6506C0729A36E4625E157A5E6EA2CCB79B9C2FF2E4F3F57988BBB9535CC52B94` |
| render（`renderSha256LfNormalized`） | — | `642cfdda1c11587c63ae9f8587c24c4959f7ed3e8bea5bd51d70a36a7b4cb4b8` |

CRLF 由 accepted LF blob 逐字节将 `0x0A` 展开为 `0x0D 0x0A` 得到，checksum 与 accepted pin 精确相等。

### 2.4 accepted companion 与负例

正向：

```powershell
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/verify-v5-companion-fixtures.ps1 `
  -RepoRoot D:/majiang-wt/lh-gate-repair-ds-20261029 -VerifyGitPins
```

- native exit 0；**PASS 4/4**；visibility LF `30291EC2...`；四个历史 freeze commit blob 全部相等。
- 证据：`build/lh-gate-repair-20261029/companion-verify-final.log`，SHA-256 `3BDEE388A2095D9D8FC9100EDB699B1FD07B0A64A0A356F258957666CF0953E8`。

负例（独占 ignored 副本，未改 verifier）：

| 负例 | 改动 | native exit | 结果 | 证据 |
|---|---|---:|---|---|
| 一字节 mutation | `visibility-v1.json` 第 100 字节 XOR 0x01 | 1 | `HASH_MISMATCH`（另有 GENERATOR_MISMATCH） | `build/lh-gate-repair-20261029/negatives/one-byte.log`，SHA-256 `E66C5D695DE9DACDD93C051121B4F931DA521BEC9AC9A03CFAB696BB7A7C4AE2` |
| render 字段 mutation | 把 `642c...` 改为 `...b9` | 1 | `HASH_MISMATCH` + `RENDER_SHA_MISMATCH` | `build/lh-gate-repair-20261029/negatives/render.log`，SHA-256 `918AB083BE000E6C8698214013146EFD81BD3A45C059FA8C2A802769F0FB9004` |

### 2.5 `VisibilityGoldenTests` 的冻结样本消费对齐（新信息）

恢复 v1 后，`Mahjong.TableRuntime.Tests.VisibilityGoldenTests.GoldenMetaProvenanceAndCaseIdentityAreReal` 最初 1 项失败：fixture 的 `sourceHashes` 指向 accepted 基线源码，而当前候选已包含后续组件接受的 Furiten/kan 源码变化。原测试无条件把 fixture 的 source hash 与当前 HEAD 文件比较，因此“恢复 accepted v1”和“VisibilityGolden 8/8”在原测试下不能同时成立。

未刷新 v1、未改 runtime、未放宽 companion。修复改为在 fixture 声明的 `v5BaselineCommit=1432d775526de921f2cde3aba283236fc3b8e8c1` 上验证精确历史字节：
- 当前源码 hash 与 fixture 相等时，仍按原逻辑校验 raw/LF/行尾；
- 当前源码已被后续接受修改时，从 `git cat-file blob <v5BaselineCommit>:<file>` 读取历史 blob，按 fixture 声明的 CRLF/LF 表示重建 raw bytes，并精确比较 `bytesRaw`、`sha256RawBytes`、`sha256LfNormalized`、`crlfCount`、`lfOnlyCount`；
- 任一精确值不等仍失败；不是 skip，也不是只比较 LF 摘要。

这使得 accepted v1 作为冻结审计样本仍被严格验证，而不是要求历史样本跟随当前源码刷新。`VisibilityGoldenTests` 8/8 PASS（XML SHA `4AB7D9A6DD24858FE3EB644093CDF38B42D6718D60A3EAE6D976D0C821D76899`）。

### 2.6 catalog 对照

catalog 的 6 个 fixture 在 LF 工作树中不是 accepted CRLF pin；为不触碰当前 fixture，用 `core.autocrlf=true` 的临时 detached worktree（HEAD `7f223f2`，位于 ignored build，运行后已删除）执行原 verifier：

```powershell
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File <crlf-worktree>/tools/verify-v5-golden-catalog.ps1 -RepoRoot <crlf-worktree>
```

结果：PASS，`fixtures=6 requirements=73 cases=10389`，catalog SHA `236C8E0EE9B7FFB86187CB7D3AB650C9EA13FC0A4C47675E044B64BB0C93116D`，native exit 0。证据：`build/lh-gate-repair-20261029/catalog-crlf.log`，SHA-256 `C028FFCD2DEE6B384338F4A4F3CC2B7F54F4B809979738A4FD2630EBB89AA8F3`。catalog 不校验四个 companion fixture；本报告没有用它替代 companion。

## 3. D-LH-GATE-04 — MAJOR — 冷恢复断言捕获了不同权威版本边界

### 3.1 位置与原始失败

- 原位置：`dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:23-50`（`ColdRestartRestoresCommittedStateVersion`），辅助 `SubmitDiscardAsync` 同文件 `:656-720`。
- 原始 PG 失败（保留未覆盖）：`D:/majiang-wt/localhost-batch-gate-ds-20261029/build/lh-gate-95b4754/pg-gate/` 原 XML SHA `DFF26B384DDD59C4177980DA0E50C0778EC8B64CC4CBC1279B76DC8BAC8549DF`，100/101，native exit 1，`saved=2 committed=1`。
- 复现口径：`tools/ci/verify-v5-postgres.ps1` 的统一 verify 负载；standalone 101/101 不能抵消已发生的竞态。

### 3.2 事件级复核补充（新信息）

本轮按 committed event 逐版本核对后确认：discard 命令在**同一个命令版本 v1** 内同时写入 `TileDiscarded(seat=0)` 与 `TileDrawn(seat=1)`（下一家自动摸牌），不是两个独立版本。原始报告把 `saved=2` 描述为“discard 后自动摸牌版本”并不精确；按当前事件证据，`saved=2` 是随后自动 AI 弃牌命令 v2 的持久版本，`committed=1` 是测试在 v2 PUSH 到达前读到的 discard 快照 v1。竞态结论不变：**自动后续命令先于 PUSH 观察提交**，但版本归因必须按事件修正。

本修复因此不再假设“摸牌是独立版本”，而是等待所有自动后续提交结束后的确定性静止边界。

### 3.3 修复

文件：
- `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs`
- `dotnet/tests/Mahjong.LocalHost.CrashWorker/Program.cs`（新增冷进程只读模式）
- `dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs`（仅 D-03 消费对齐）

测试改动：
1. `SubmitDiscardAsync` 返回 receipt 的 `CommandVersion`/`EventSeq`/scope/request/tile，不再把 `session.PlayerSnapshot.StateVersion` 直接当权威版本；也不再等待“任意 snapshot > baseline”。
2. 新增 `WaitForQuiescentSnapshotAsync`：等待 `StateVersion >= discard command version` 且到达下一个**人类决策/终局/人类 Draw** 静止点。此时本地 driver 不再能自动提交命令，snapshot 与 durable store 应精确一致。最终断言仍是 `Assert.Equal` 精确版本，不是 `>=`。
3. `ColdRestartRestoresCommittedStateVersion` 在静止点捕获 `final.StateVersion`，随后关闭 session，读取 durable store 的 `StateVersion`、`LastEventSeq`、checkpoint、events 与 receipt，断言：
   - store state version == 最终 snapshot version；
   - roundId 精确相等；
   - v1 同时包含 seat0 `TileDiscarded` 与 seat1 `TileDrawn`；
   - 每个 accepted command version `1..N` 至少有一个 event，event seq 唯一且最后 seq == state.LastEventSeq；
   - receipt 的 status/evaluated/command version/event seq/payload digest 精确；
   - 136 实体守恒与 tile hash。
4. 新增 `recovery-boundary-read` CrashWorker 模式：**独立 OS 进程**打开同一 save，走真实 `LocalTableRuntime.OpenAsync` 冷恢复，逐行输出 state/event 版本、checkpoint 版本、roundId、`StateFingerprint`/`StateHash`、136 tile hash、账本余额/entries/digest、event count/summary/digest、receipt 全字段。父测试逐行精确比较，不用 `dispose/new` 替代冷进程。
5. 新增 `ColdRestartBoundaryIsStableBeforeAndAfterAutomaticFollowUp`：用现有 `DriverForTest.BeforeAiSubmitForTest` 在 seat1 自动弃牌**已选择但未提交**处暂停；先观察并读取 v1（含自动摸牌）的 snapshot/store，确认没有 v2 event；释放后等待静止点并做同样的精确 store + 冷 OS 比较。这样同时覆盖：
   - 自动后续提交先于最终观察（`ColdRestartRestoresCommittedStateVersion`）；
   - 先观察 discard v1，再发生自动后续提交（gate 测试）。

未使用 sleep 等待业务完成、未扩大 timeout 掩盖、未把最终断言改为 `>=`、未删弱断言、未盲重试、未改产品 runtime/store/actor。

### 3.4 验收与证据

本地 fresh Release（`--no-incremental`）构建后直接运行 fresh DLL：

| 运行 | 结果 | XML SHA-256 |
|---|---:|---|
| LocalHost 最终 fresh DLL（`--no-incremental` 后） | **102 / 102**，0 failed / 0 errors / 0 skipped / 0 not-run，native exit 0 | `36A463ECE4BD0159FEA32CFD6EED35E35120E5E1F77FE03AB9A8CC093CBD5C6D` |
| LocalHost 全量串行 run4（重复） | **102 / 102**，同上 | `756516D8527E2C1245DEAFA498185B6E99890396D539A08B2AEA7620A1F0CA5A` |
| 定向 `ColdRestartRestoresCommittedStateVersion` | 1 / 1 Pass，native exit 0 | `3BC019383C4D7B1D52127854EF236C86DDD89AD9A49A754B0E217D36FDFBB023` |
| 定向 `ColdRestartBoundaryIsStableBeforeAndAfterAutomaticFollowUp` | 1 / 1 Pass，native exit 0 | `29A206CAA9F0044BFC940C4C41AA098314F64A8B217EB96AEAAFCEB76C15E84E` |
| 真实 PG wrapper 下的 LocalHost | **102 / 102**，0 failed / 0 errors / 0 skipped / 0 not-run，native exit 0 | `2AD6364C8F7ED8C612A93598191AA4932C20B794B027F6498DAD76608EF0684B` |

PG wrapper 本次实际命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-postgres.ps1 `
  -Configuration Release -RunnerMode direct `
  -DotnetPath D:/Unity/dotnet/dotnet.exe -Port 55861 `
  -LogDir build/lh-gate-repair-20261029/pg-gate-final
```

wrapper 阶段：precheck / start-postgres / ownership-marker / environment / migrate / archive-cluster-evidence / cleanup / env-restore / write-evidence 均 PASS；`verify` 阶段 FAIL 的唯一项目级原因是 `Mahjong.Infrastructure.Tests` 41 项因缺 `MAHJONG_P52_TEST_POSTGRES_ADMIN` 环境变量（BLOCKED），不是本轮 D-03/04 失败。LocalHost 102/102、Companion 4/4、TableRuntime 413/413、Client 182/182、Architecture 130/130、TableService 152/152、Domain 529/529、Ai 444/444 均 PASS。PG 集群为本任务独占 loopback 端口 55861，wrapper 已按 ownership marker/PID/exe/data 校验清理，root 已删除。证据：`build/lh-gate-repair-20261029/pg-gate-final/summary.log`（SHA-256 `E8AD39A743BC4F7F3CDC9D2B31B26943E77F683FFC4656C90FF326186BD2D1CA`）与 `pg-gate-final-wrapper.log`（`4B736DEBB0B86AA1AF95BB0A3A593EEECC18E619EFF0E6465A936A4EAA66B659`）。

没有把 Infrastructure 环境失败算作本任务返修，也没有声称统一门禁全批通过。

## 4. 回归与证据汇总

fresh Release 构建：
- `dotnet/Mahjong.sln`：native exit 0，0 warnings / 0 errors；日志 `build/lh-gate-repair-20261029/build/solution-release-final.log`，SHA-256 `7617B3E26F3E25CA335F4462D679548B731501D959AD329060F75C6BE9D15932`。
- `Mahjong.LocalHost.Tests`：0 warnings / 0 errors；日志 SHA `8BA764715DD07CDB8BCB3F9825E257916E0B7EA025C064965C088E7FF2E32011`。
- `Mahjong.TableRuntime.Tests`：0 warnings / 0 errors；日志 SHA `00D00B02DA516B7B399B55471AE1BB926D8278C4AB5CE85DFDC858BE63F02C4A`。

直接运行 fresh DLL 的回归：

| 工程 | total / passed | failed / errors / skipped / not-run | native exit | XML SHA-256 |
|---|---:|---|---:|---|
| LocalHost | 102 / 102 | 0 / 0 / 0 / 0 | 0 | `36A463EC...` |
| TableRuntime | 413 / 413 | 0 / 0 / 0 / 0 | 0 | `85B8B926E1DCF98FDAEFF2CA3569C846EE1FF0382B3A35F3E9E6DA5D4620B342` |
| VisibilityGolden | 8 / 8 | 0 / 0 / 0 / 0 | 0 | `D38B28F25268CF9995FA2FB9DB168AC766FD6658DC3AD536660574B893EC4DDA` |
| Client | 182 / 182 | 0 / 0 / 0 / 0 | 0 | `D241D697FCBFCD82EBB4845260E54C01CA8CB1CCE0CFE164A0F08AB936E3F65B` |
| Architecture | 130 / 130 | 0 / 0 / 0 / 0 | 0 | `1CE84BD20CF154BDDCAB96B00B9445BA2219C524A9D6748B7EE5B3F142B28C33` |

其他：
- accepted companion：4/4 PASS（`companion-verify-final.log`）。
- catalog：6 fixtures / 73 requirements / 10,389 cases PASS（CRLF accepted 表示；`catalog-crlf.log`）。
- LocalHost 并行模式 3 次中 2 次 102/102；第 3 次出现既有 `LocalHostConcurrencyTests.PartialCancellationOfConcurrentInitialOpenLetsTheOtherWaiterSucceed` 单测 flake（与本任务文件/断言无交集），该单测随后单独运行 1/1 PASS。此 flake 保留记录，不冒充本任务通过，也没有为了它修改产品代码或测试。
- 关键 DLL SHA-256：LocalHost `0DC3D1201F6DF9E5DD9C498357F3AA1F76542B5B43D3E414FA9063474BC4883D`；TableRuntime `E523FB113247414B73EDB8E7D5F121EF6259D5EFDE52D3C705A05D65D7F700A1`；Client `7A3DDC4B07E0268B6E0D71A9D5E32B4F901520A6FC9EEF9437B2CD43667C13C0`；CrashWorker `1C54F069E6330CBE8F1642ED7AE704490897055DE9725EA816C9FEA851547FBB`；LocalHost.Tests `07347404CDBB81EDCF9A872F9F9EABF156704071242643D89A73B8E494587DC1`；TableRuntime.Tests `F6D05C6561C58D3895B107424D866E938678925AA0158F5CF6D6D52690B2FCCD`。

逐 XML 的 total/passed/failed/errors/skipped/not-run 与 SHA 汇总见 `build/lh-gate-repair-20261029/test-counts-final.tsv`；完整 ignored 证据清单见 `build/lh-gate-repair-20261029/final-evidence-sha256.tsv`。原始日志、构建日志、PG 证据和负例均在该 ignored 目录。没有使用 skip、空测试、旧 DLL、`Total=0` 或刷新摘要冲绿。

## 5. 净 scope 与明确未做项

本任务 tracked 变更（本地提交）：
- `tests/v5-golden/visibility-v1.json`：恢复 accepted 精确字节。
- `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs`：D-04 确定性边界、冷 OS 比较、双时序测试。
- `dotnet/tests/Mahjong.LocalHost.CrashWorker/Program.cs`：新增只读冷进程边界证据模式。
- `dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs`：D-03 冻结 fixture 的历史 baseline 消费对齐。
- 本报告。

未修改：
- 产品 `LocalTableRuntime.cs`、`LocalTableStore.cs`、`LocalActor`/domain/timer/store；
- `Domain`、`TableRuntime` 生产实现、`Contracts`、`TableService`、`Client`、`LocalHost` 生产源码；
- Unity、Resources、Burst、共享 sln、架构白名单、统一 verifier/CI、scheduler；
- 任何 pin、companion/catalog verifier、census、旧报告；
- D-01/D-02 的 TableService/LocalTableRuntime/Store/ControlTests 范围；
- P4.2-B 的 Unity 范围。

最终本地提交后应满足：tracked clean；`git diff --check 7f223f2870b91c579d0ba46c59f915ea02c2657d..HEAD` 原生 exit 0；最终 HEAD 由 `git rev-parse HEAD` 读取并记录在 `build/lh-gate-repair-20261029/final-metadata.txt`（自引用哈希不在本文件内伪造）。

## 6. NOT_RUN / BLOCKED

以下没有执行，不得读成 PASS：
1. Unity Editor / Windows Player / IL2CPP / Android / iOS / 真实设备：NOT_RUN；Android/iOS 工具与设备环境仍按既有记录。
2. 跨物理网络、TLS/公网 WSS 端到端、远端 CI：NOT_RUN；本候选不 push，不引用或触发远端 CI。
3. 完整整场（P4.5）、G2–G5：NOT_RUN/BLOCKED；D-01/D-02 仍未关闭，完整 LocalHost 未接受。
4. `TableService` 控制 tag 路径（D-01）、LocalHost 控制拒绝持久化（D-02）：不在本任务范围，继续 OPEN。
5. 旧 `c600611` / push `40715805408` 的 furiten-read exit 3 历史记录：继续保留，未销账；本轮不涉及该根因。
6. 真实 24h 墙钟、断电、双进程同写、生产密钥/信任库变更：NOT_RUN。

## 7. 交付与 handoff

- 本任务只关闭 D-LH-GATE-03/04；D-01/02、P4.2-B、P4.5、G2–G5 仍需各自线程/最终整合复验。
- caller 继续独立验收并将 accepted delta 整合到唯一 PR20 `https://github.com/liuchangready/majiang-game/pull/20`；receiver 不 push、不建/改 PR/MR、不 merge/deploy、不创建 `complete.flag`。
- 临时 worktree 的 bridge merge-back 义务仍由 caller 记录与执行；本任务不删除/改写其它工作树、用户文件或 Resources/Burst。
- 本报告所在提交即最终本地交付提交；其 SHA 由 `git log -1 --format=%H -- doc/v5-重构/V5-LOCALHOST-GATE-03-04-REPAIR-DELIVERY.md` 读取。
