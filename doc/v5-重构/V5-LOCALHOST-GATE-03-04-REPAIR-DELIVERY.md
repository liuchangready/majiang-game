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
| D-LH-GATE-03-SHALLOW | **本任务范围修复完成（本地候选，第 2 轮新增）**：移除对完整 Git 历史的依赖；accepted v1 字节 pin + 离线 provenance 校验；full-history、depth=1 shallow、无 `.git` 快照均 8/8。 |
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

### 2.7 D-LH-GATE-03-SHALLOW 返修（第 2 轮新增 MAJOR）

**ID / 严重级别 / 位置**

- `D-LH-GATE-03-SHALLOW` / MAJOR / 返修新引入的本任务 CI 兼容性回归。
- 位置：`dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs` 的 `AssertSourceHash` / `ReadGitBlob`；`.github/workflows/v5-verify.yml` 的 `actions/checkout@v4` 未设置 fetch-depth，默认浅克隆。
- 新增信息：R1 为恢复 v1 后消费对齐新增 `ReadGitBlob`，依赖完整 Git 历史；冻结代码和默认 CI 均不保证该历史对象存在，因此上一轮无法发现。

**复现**

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj `
  -c Release --no-incremental --nologo -m:1 -nr:false -p:UseSharedCompilation=false
git clone --depth 1 --single-branch --branch wt/v5-lh-gate-repair-ds-20261029 `
  --no-checkout file:///D:/majiang <shallow>
git -C <shallow> -c core.autocrlf=true checkout --force HEAD
# 复制 fresh dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0
D:/Unity/dotnet/dotnet.exe <shallow>/.../Mahjong.TableRuntime.Tests.dll `
  -class Mahjong.TableRuntime.Tests.VisibilityGoldenTests -result-xml <shallow-visibility.xml>
```

**实际（修复前）**

- `<shallow>/git rev-parse --is-shallow-repository` = `true`；HEAD = `ec6243dcd198a10d2bb32156fa47d55f561218c1`；baseline blob `git cat-file -e` exit 1。
- 8 total / 7 passed / 1 failed / 0 errors / 0 skipped / 0 not-run，native exit 1；`GoldenMetaProvenanceAndCaseIdentityAreReal` 失败：
  `git cat-file failed for 1432d...:dotnet/src/Mahjong.TableRuntime/TableVisibilityProjection.cs exit=128`。
- 本树复现证据：`build/lh-gate-repair-20261029/shallow-before.xml`（SHA-256 `047035F69DE9BBF13953CB6BB8F18CEFB44FFFEC46C427401060B218BE88B2D1`）、`shallow-before.log`（`DC4333F0FD172A0F4D382BB65025BFC12CD56D8CB5AEAC8F9532BCF7B31DC4AA`）。
- caller 原证据根 `build/caller-lh0304-834e2156d9a143bea9efd4653b3ff2b7/` 保留未覆盖；其 shallow XML SHA `9427F8F5C88425567A5F8A73C6A7CA4F65D078D955CCE94CFB3EC5630F1731AF`、log SHA `448F2345FE5E9D50AC94A2532E5E08786918609CBA07E52FB74E1EC499BA4659`。

**修复**

- 删除 `ReadGitBlob` 及 `git cat-file` 依赖，不再要求工作树拥有 `v5BaselineCommit` 历史对象。
- 在测试内固定 accepted `visibility-v1` 的 LF/CRLF 精确字节身份（`30291E.../141,007`、`6506C0.../147,185`）；每次 provenance 检查先验证 fixture 本身是 accepted 冻结字节。
- 对当前源码仍与 fixture 相等的条目，保留原 exact raw/LF/行尾校验；对因后续 accepted 组件变化而不再相等的历史条目，验证 fixture 内部 raw/LF/bytes/lineEndings 元数据自洽，并以 accepted fixture 字节 pin 作为离线审计锚。任何 fixture 字节篡改（包括 source hash 修改后重算 render 使其自洽）都会命中 accepted-bytes pin；render 篡改仍命中 `VerifyRenderHash` 与/或 pin。
- 未新增 fixture/v2、未刷新旧 pin、未修改 companion/catalog verifier、统一 verifier、CI/workflow fetch-depth；未联网补历史；未 skip 或放宽为只检查字符串长度。

**验收（本轮新跑）**

| 场景 | 结果 | native exit | 证据 |
|---|---|---:|---|
| full-history VisibilityGolden | **8 / 8** | 0 | `shallow-fix/full-visibility.xml` SHA `EDA75C587E4867ACA8C3BB2FC494B97037C8F1A924ADF69E114A5D54B8E34824` |
| depth=1 shallow VisibilityGolden（final HEAD `10ae8b2`） | **8 / 8** | 0 | `shallow-final.xml` SHA `5C16BF9407944A0DEECF34F1549F2C3BC2A9E43C017F2281B5B7B98AA602DD54`；修复中复跑 `shallow-after.xml` `E47A46547AA02FD7216636458E9DDC170C0372E72B9CBF1277F0FE5EF0F49FED` |
| 无 `.git` 源码快照 VisibilityGolden（final HEAD `10ae8b2`） | **8 / 8** | 0 | `nogit-final.xml` SHA `EF696EB257BD44E9506063DB858FB3B22AC3EACBD618936C02B873FA62F3E5D3`；修复中复跑 `nogit-after.xml` `7CB6014C7FEB278A1ADA8F1F51A90D64E3EBBF5539147DFD5AB99E5483E8B479` |
| full TableRuntime | **413 / 413** | 0 | `shallow-fix/full-tableruntime.xml` SHA `4A8FAAEFE8A69B090019F0BB73068E06E0659BDA11C60B24F26F7D86CEABAB6D` |

负例矩阵（均 8 total / 7 passed / 1 failed / native 1）：

| 负例 | 结果 | 证据 |
|---|---|---|
| source provenance 字节篡改（并重算 render 自洽） | accepted fixture pin 失败：`visibility-v1 is not the accepted frozen fixture` | `shallow-fix/neg-source-selfconsistent2.xml` SHA `690F9D6DBB83567B80ADE7C490E1EA3DE3041AD24AAC36741322FD04793E996C` |
| 简单 source 篡改 | fixture/render 一致性失败 | `shallow-fix/neg-source.xml` SHA `3DE59B19511D2CF9EC9E949CD18096D92263B7E803BEC22EB29D051CC796F6E5` |
| render 字段篡改 | `VerifyRenderHash` 失败 | `shallow-fix/neg-render.xml` SHA `59A8409BB8B2512594808E03EE54CA089DEF5F1AA08C739568C9CD997072BAB0` |

修复构建：`build/lh-gate-repair-20261029/build/tableruntime-shallow-fix-build2.log`，0 warnings/0 errors，SHA `92909265DCC4466600E2040A04A3B97E9EA634D21CB5EF915AE367FFDF972375`；TableRuntime.Tests fresh DLL `301174150921F34A6009EBDB3A91D8BCFCBC0B74FCEC0F31D9EA32E9EF2A3C82`。

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

本轮（D-LH-GATE-03-SHALLOW 返修后）新跑：TableRuntime 413/413 与 VisibilityGolden 8/8；full-history、depth=1 shallow、无 `.git` 快照的 VisibilityGolden 均 8/8。LocalHost 102/102、Client 182/182、Architecture 130/130 本轮未重跑，保留 caller 本轮 fresh 运行及上一轮证据，仅作为历史引用；后续测试改动只涉及 `VisibilityGoldenTests.cs`，未改这些工程的生产源码。

| 工程 | total / passed | failed / errors / skipped / not-run | native exit | XML SHA-256 | 来源 |
|---|---:|---|---:|---|---|
| LocalHost | 102 / 102 | 0 / 0 / 0 / 0 | 0 | `36A463ECE4BD0159FEA32CFD6EED35E35120E5E1F77FE03AB9A8CC093CBD5C6D` | 上一轮 + caller 本轮 |
| TableRuntime | 413 / 413 | 0 / 0 / 0 / 0 | 0 | `4A8FAAEFE8A69B090019F0BB73068E06E0659BDA11C60B24F26F7D86CEABAB6D` | 本轮新跑 |
| VisibilityGolden | 8 / 8 | 0 / 0 / 0 / 0 | 0 | `EDA75C587E4867ACA8C3BB2FC494B97037C8F1A924ADF69E114A5D54B8E34824` | 本轮新跑 |
| Client | 182 / 182 | 0 / 0 / 0 / 0 | 0 | `D241D697FCBFCD82EBB4845260E54C01CA8CB1CCE0CFE164A0F08AB936E3F65B` | 上一轮 + caller 本轮 |
| Architecture | 130 / 130 | 0 / 0 / 0 / 0 | 0 | `1CE84BD20CF154BDDCAB96B00B9445BA2219C524A9D6748B7EE5B3F142B28C33` | 上一轮 |

其他：
- accepted companion：4/4 PASS（`companion-verify-final.log`）；caller 本轮也复核 4/4 native 0。
- catalog：6 fixtures / 73 requirements / 10,389 cases PASS（CRLF accepted 表示；`catalog-crlf.log`）；caller 本轮 CRLF 精确候选复核同数。
- LocalHost 并行模式 3 次中 2 次 102/102；第 3 次出现既有 `LocalHostConcurrencyTests.PartialCancellationOfConcurrentInitialOpenLetsTheOtherWaiterSucceed` 单测 flake（与本任务文件/断言无交集），该单测随后单独运行 1/1 PASS。此 flake 保留记录，不冒充本任务通过，也没有为了它修改产品代码或测试。
- 当前最终构建产物 SHA-256：LocalHost `C23CF8714073706C985D1F2718068D01529F88DCD0A60F331DB271A094D96C34`；TableRuntime `C0846D673F61256A8E2A2E05ECB3ACE09A2E0E3016000AC64CB93937B9E57D74`；Client `34F0F704810B9B75D3B345AB6DDEE17EED82C22777992E477B6675254BF6E39B`；CrashWorker `1C54F069E6330CBE8F1642ED7AE704490897055DE9725EA816C9FEA851547FBB`；LocalHost.Tests `0461A3BDB347085D8744776BBAD1D6BA7D5A597AC58A451336DDDE85793E1A5E`；TableRuntime.Tests `301174150921F34A6009EBDB3A91D8BCFCBC0B74FCEC0F31D9EA32E9EF2A3C82`。

逐 XML 的 total/passed/failed/errors/skipped/not-run 与 SHA 汇总见 `build/lh-gate-repair-20261029/test-counts-final.tsv`；完整 ignored 证据清单见 `build/lh-gate-repair-20261029/final-evidence-sha256.tsv`。原始日志、构建日志、PG 证据和负例均在该 ignored 目录。没有使用 skip、空测试、旧 DLL、`Total=0` 或刷新摘要冲绿。

## 5. 净 scope 与明确未做项

本任务 tracked 变更（本地提交）：
- `tests/v5-golden/visibility-v1.json`：恢复 accepted 精确字节。
- `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs`：D-04 确定性边界、冷 OS 比较、双时序测试。
- `dotnet/tests/Mahjong.LocalHost.CrashWorker/Program.cs`：新增只读冷进程边界证据模式。
- `dotnet/tests/Mahjong.TableRuntime.Tests/VisibilityGoldenTests.cs`：D-03 冻结 fixture 的历史 baseline 消费对齐，以及 D-LH-GATE-03-SHALLOW 的离线 accepted-bytes/provenance 校验（无 Git 历史依赖）。
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

- 本任务只关闭 D-LH-GATE-03/04（含第 2 轮 D-LH-GATE-03-SHALLOW）；D-01/02、P4.2-B、P4.5、G2–G5 仍需各自线程/最终整合复验。
- caller 继续独立验收并将 accepted delta 整合到唯一 PR20 `https://github.com/liuchangready/majiang-game/pull/20`；receiver 不 push、不建/改 PR/MR、不 merge/deploy、不创建 `complete.flag`。
- 临时 worktree 的 bridge merge-back 义务仍由 caller 记录与执行；本任务不删除/改写其它工作树、用户文件或 Resources/Burst。
- 本报告所在提交即最终本地交付提交；其 SHA 由 `git log -1 --format=%H -- doc/v5-重构/V5-LOCALHOST-GATE-03-04-REPAIR-DELIVERY.md` 读取。
