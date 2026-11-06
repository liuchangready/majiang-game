# V5 LocalHost 汇合候选门禁独立审查（95b4754）

- 日期：2026-10-29（Asia/Shanghai）。
- 性质：caller 委派的“汇合候选门禁审查输入”。本任务不重复实现 P4.1、不改派既有线程、不接管源代码修复；只交付本审查文档和本树 ignored 证据。caller 保留最终复核、裁决、集成与发布权。
- 审查对象：冻结候选 `95b47547b535f38a1fc6328445e209e7d402adcc`。
- 审查树：`D:/majiang-wt/localhost-batch-gate-ds-20261029`，分支 `wt/v5-localhost-batch-gate-ds-20261029`，审查 HEAD `17808cca932269ac8058270a5684b33d02d82abd`（已接受 PR20 头）。起点 `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` 是 HEAD 祖先（exit 0）；开始审查时 tracked clean。
- 证据根：`build/lh-gate-95b4754/`（ignored）。完整命令/native exit、XML 计数、DLL/日志/快照 SHA、manifest 与逐行重哈希见该目录。

## 0. 裁决（审查输入与候选门禁分开）

| 项目 | 结论 |
|---|---|
| 本审查输入 | **PASS**：证据完整，已一次给全确认缺陷；未执行项均单列 NOT_RUN/BLOCKED，没有用未执行项冒充通过。 |
| 候选门禁 | **REWORK**：候选不能按当前状态汇入唯一现有 PR20。已确认 3 个集成/门禁 MAJOR 与 1 个稳定性 MAJOR；修复后必须重跑本报告对应的门禁与负例。 |
| a/b/c/d 组件接受 | 保留既有组件级接受，不因本报告改写；**完整 LocalHost 仍未接受**。 |
| 阶段门禁 | G1 仍为既有 APPROVED；G2–G5 未批准；181 classified 仍未覆盖；V4 仍是唯一生产基线。 |
| 候选可接受部分 | fresh LocalHost 101/101（独立运行）、TableRuntime 413/413、Client 182/182、Architecture 130/130、Contracts 150/150、Domain 529/529、Ai 444/444、Client.Wss 80/80、TableService 152/152（测试数；控制 tag 路径未覆盖，见 D-LH-GATE-01）、Unity.TableModel 140/140；真实表 PG Infrastructure 42/42、Lobby PG 89/89；PG 冷恢复/备份演练 PASS；D-LH-CI-COLD 候选新进程冷读 PASS 一次；VisibilityGolden 8/8；catalog 6/6（CRLF 对照）。这些通过项不抵消下述缺陷。 |

**候选不能汇入的原因（摘要）**：

1. `Contracts` 的 Claim/Release 字段已改为 tag 20/21，但 TableService 生产映射仍读 tag 1，合法控制命令被当作 `PAYLOAD_INVALID`；WSS 控制路径在本候选上不可用。
2. LocalHost 的控制早退拒绝（旧 decision）没有按 request 持久化；丢回包后 `RECEIPT_QUERY` 得到 NotFound，持久 outbox 永久未决。
3. `tests/v5-golden/visibility-v1.json` 被就地改写，accepted companion 固定旧字节，mandatory companion gate 2 项失败；不能用 catalog 6/6 或 VisibilityGolden 8/8 替代。
4. PG 真实门禁暴露出 `ColdRestartRestoresCommittedStateVersion` 的测试竞态（saved=2 committed=1），LocalHost 行为 100/101；不能写成 101/101 稳定通过。

## 1. 基线与候选对象

### 1.1 审查树与祖先

| 项 | 实际值 |
|---|---|
| 审查 HEAD | `17808cca932269ac8058270a5684b33d02d82abd` |
| 审查分支 | `wt/v5-localhost-batch-gate-ds-20261029` |
| `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，是 HEAD 祖先（exit 0） |
| 候选 | `95b47547b535f38a1fc6328445e209e7d402adcc` |
| 候选双父 | `17808cca932269ac8058270a5684b33d02d82abd`、`865ca0a455bda625c660c6b7e1df049f88d4591e` |
| 候选 tree | `8017f1e355847c94f0cea1ae31855478238caae8` |
| `git merge-tree --write-tree 17808cc 865ca0a` | exit 0，tree 同为 `8017f1e355847c94f0cea1ae31855478238caae8`（自动 merge 结果与候选 tree 精确一致，无冲突） |
| `git diff --check 17808cc..95b4754` | exit 0，无输出 |
| 候选净 diff | 100 个路径，`+26519/−54`（含 PR21 文档、LocalHost/TableRuntime/Contracts/Client 源码与测试；不是本审查新增） |
| tracked 状态 | 审查开始与提交本文件前均 clean；本任务只新增本文件 |

`865ca0a` 是既有 PR21 头（`docs(localhost): accept P4.1d and preserve independent batch gate evidence`），其祖先包含已分别验收的 a/b/c/d 提交。PR21 仍是既有 Draft、base `feat/v5-post-g1-batch`；PR19 已 merged，不重开、不复用。候选只在本地，未 push，因此本报告不写任何远端 CI PASS。

### 1.2 精确 SHA 快照

```powershell
git archive --format=zip `
  -o build/lh-gate-95b4754/candidate-95b4754.zip `
  95b47547b535f38a1fc6328445e209e7d402adcc
# exit 0
Expand-Archive -LiteralPath build/lh-gate-95b4754/candidate-95b4754.zip `
  -DestinationPath build/lh-gate-95b4754/snapshot-95b4754
```

- zip SHA-256：`9720FD553EBC9AA387062ACA2E73146EF8F3677D8256E2486487A69D25810AFC`。
- 解压 2,214 个文件；无 `.git`；构建前无 `bin/`。
- 所有构建/测试/探针都只用该快照或它的 ignored 副本；未读取原 receiver 或 caller 的 `bin/Library` 作为测试产物。
- 快照没有 `.git`，运行 verifier 时设置 `GIT_CEILING_DIRECTORIES=<evidence root>`，使 git 发现停在证据根，不能向上读成审查分支。需要历史 blob pin 的 companion 检查另在 `git-candidate-95b4754/` 独立 clone 的 exact candidate SHA 上执行，避免测试误读审查分支。

### 1.3 本任务 expectedFiles

本任务唯一 tracked 交付：本文件。未修改产品、测试、verifier、黄金样本、pin、PR21 源码、Unity/UI、pin、scheduler 或 `complete.flag`；未 push/PR/MR/merge/deploy；未调用 bridge 批准/返修/恢复/另派。ignored 证据不进入 tracked 差异。

审查分支上的文档提交：本文件所在提交；提交哈希由 `git log -1 --format=%H -- doc/v5-重构/V5-LOCALHOST-BATCH-GATE-INDEPENDENT-REVIEW-20261029.md` 读取（自引用哈希不在文件内伪造）。审查基线仍为 `17808cc`，候选仍为 `95b4754`，二者不因本文档提交改变。

## 2. 构建与真实测试

### 2.1 fresh Release 构建

```powershell
D:/Unity/dotnet/dotnet.exe build <snapshot>/dotnet/Mahjong.sln `
  -c Release --no-incremental --nologo -m:1 -nr:false `
  -p:UseSharedCompilation=false
# exit 0；0 warnings / 0 errors；build-solution-release.log
```

solution 未登记的测试/辅助项目逐个 fresh 构建，全部 exit 0、0 warnings/0 errors：

`Mahjong.Architecture.Tests`、`Mahjong.Client.Wss.Tests`、`Mahjong.LobbyService.Tests`、`Mahjong.LobbyService.PostgresProbe`、`Mahjong.LocalHost.Tests`、`Mahjong.LocalHost.CrashWorker`、`Mahjong.Unity.TableModel.Tests`。日志：`build-<project>.log`，汇总 `per-project-build-summary.txt`。

fresh DLL 的完整 SHA 在 `fresh-dll-sha256.csv`。关键值（快照最终构建状态）：

| 产物 | SHA-256 |
|---|---|
| `Mahjong.LocalHost.dll` | `AAC0CE18B0C069DF2A2185E4FB716A7ADA233B694A7EC16EF038770CA18F9EC3` |
| `Mahjong.LocalHost.Tests.dll` | `9ABCA52F48002CE0458E2432B342EEEE7B5E3879A58B15553A9E5F303A105D04` |
| `Mahjong.LocalHost.CrashWorker.dll` | `0CEE876AC01F8A58816B167895DFC774C3255524D85D0373D41CCB673A05210B` |
| `Mahjong.TableRuntime.dll` | `FE09C273D25FE98DD2FCB39F0DFD118C665AF18DC5C162DD281290C2CD335BF7` |
| `Mahjong.TableRuntime.Tests.dll` | `5848763DAAF2EDA0063BA80CB2BB71612EB7929B3520961D22231233900A101A` |
| `Mahjong.Client.dll` | `F8D7CE4AAFF3C8F333445EB03092A508702BF4812307E0511E45CA3EC292DEBD` |
| `Mahjong.Client.Tests.dll` | `C10D0D0ECCBF598DA66F561ED3D89954FF0DEE0478AC95DF777169EE345D911F` |
| `Mahjong.Contracts.dll` | `72C4DCC0ED04628C30F37247C9B3417FB64C5443E13978D0AEC9E9BA24C5C227` |
| `Mahjong.TableService.dll` | `6AFA43B1B5C74CA52969BD3B30E683450F238EED1109D42B44D08E94CA5CA247` |
| `Mahjong.Infrastructure.Tests.dll` | `9F597BFAA7871EDF04A08142ABB142A5DD56BA607802AACEE22B00A287837596` |

所有测试均直接运行上述 fresh DLL，并由 xUnit v3 写出 per-case XML；没有使用 `dotnet test` 的旧产物、没有 `Total=0`、没有 skip 冲绿。XML、console 与 native exit 逐项核对。

caller 提供的 preflight（Architecture 130/130、companion 2 项失败、catalog 行尾诊断、manifest `A5B44F3675C41026C11F0147466CBDD67E55B3471C26BB2E47869F31A7A6F872` 6/6 重哈希）仅作对照；本报告所有计数和结论来自本轮 fresh 快照运行，未把其日志当作本轮证据。其 companion 两项失败与 catalog 的 CRLF 行尾结论与本轮独立复现一致；但 catalog 6/6 不能替代 companion，见 §4。

### 2.2 全量 XML 计数

两次完整 direct 运行：`unified/`（无 PG 环境）与 `pg-gate/`（lobby PG wrapper）。所有行均为直接运行 fresh DLL；`test-counts.csv` 给出逐行 XML SHA 与 exit。

| 工程 | unified（无 PG） | lobby PG wrapper | 说明 |
|---|---:|---:|---|
| Ai | 444/444 | 444/444 | 0 fail/error/skip |
| Architecture | 130/130 | 130/130 | 旧 c78 白名单修复在本候选仍 PASS |
| Client | 182/182 | 182/182 | 含 visible-event 回归 |
| Client.Wss | 80/80 | 80/80 | 0 fail/error/skip |
| Contracts | 150/150 | 150/150 | 0 fail/error/skip |
| Domain | 529/529 | 529/529 | 136/守恒在 Domain frozen tests 中 |
| Infrastructure | 1/42（41 fail，均为缺 `MAHJONG_P52_TEST_POSTGRES_ADMIN`） | 1/42（同） | 见 §2.4 的表 PG 补充运行：42/42 |
| LobbyService | 77/89（12 skipped，无 PG） | 89/89 | lobby PG 真实 89/89 |
| LocalHost | 101/101 | 100/101 | PG 负载下 `ColdRestartRestoresCommittedStateVersion` 1 次失败，见 D-LH-GATE-04 |
| TableRuntime | 413/413 | 413/413 | 0 fail/error/skip |
| TableService | 152/152 | 152/152 | 测试绿，但控制 tag 路径无覆盖，见 D-LH-GATE-01 |
| Unity.TableModel | 140/140 | 140/140 | 0 fail/error/skip |

合计：无 PG 运行 **2452 total / 2399 passed / 41 failed / 12 skipped / 0 errors / 0 not-run**；lobby PG 运行 **2452 / 2410 passed / 42 failed / 0 skipped**。两处失败分别是 41 个 Infrastructure 环境阻塞与 1 个 LocalHost 测试竞态；没有任何产品测试用例被 skip 或删减。表 PG 补充运行 Infrastructure **42/42**。

关键 XML SHA-256：

| XML | SHA-256 |
|---|---|
| `unified/.../Mahjong.LocalHost.Tests/direct-results.xml` | `70E1FFE9FEA2AE31C948B3FFB7E3805E664CAB8C460255D52843CE2DEFCE715B` |
| `pg-gate/.../Mahjong.LocalHost.Tests/direct-results.xml` | `DFF26B384DDD59C4177980DA0E50C0778EC8B64CC4CBC1279B76DC8BAC8549DF` |
| `table-pg-gate/tests.xml` | `FD572C9FF1BBC9EE6BC2EEFA1CA366CB0A72A22A02BE1B37269CEE9FDB67D4DA` |
| `table-pg-gate/tests-targeted.xml` | `15B62F42F398701C147A8880657B4F12BC7046B2B01E9B015A5C8ECFC1E6ECA8` |

### 2.3 首次失败明细（不重试掩盖）

**D-LH-GATE-04 首次失败**：

- 测试：`Mahjong.LocalHost.Tests.LocalHostRecoveryTests.ColdRestartRestoresCommittedStateVersion`。
- 位置：`dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:23-50`；辅助 `SubmitDiscardAsync` 在同文件 `:656-720`。
- 实际：`saved=2 committed=1`（`saved=2` 是 discard 后自动摸牌的正确持久版本，`committed=1` 是测试提前抓到的 snapshot 版本）。
- native exit 1；XML total 101 / passed 100 / failed 1 / errors 0 / skipped 0 / not-run 0；XML SHA `DFF26B38...`。
- 原始框架日志：`pg-gate/direct-Mahjong.LocalHost.Tests.log`。
- 该失败不在 standalone unified 运行出现（101/101），但本报告保留首次失败，不把重跑当修复。

**Infrastructure 环境失败（非候选产品缺陷）**：

- 测试：`Mahjong.Infrastructure.Tests.PostgresTableStoreTests.*`。
- 实际：`InvalidOperationException : BLOCKED: MAHJONG_P52_TEST_POSTGRES_ADMIN is required for the real PostgreSQL P5.2 tests.`（`PostgresTableTestEnvironment.cs:182`）。
- lobby PG wrapper 只设置 `MAHJONG_LOBBY_*` 四个变量，不设置 `MAHJONG_P52_TEST_POSTGRES_ADMIN`；因此 41 个 Infrastructure 用例是 **BLOCKED**，不是产品失败。
- 补充运行 `tools/ci/verify-v5-table-postgres.ps1` 后，真实表 PG Infrastructure **42/42**、targeted 13/13、crash/backup 全部 PASS（§2.4）。这说明旧 PG12“安装 bin/share 缺失”在当前候选未复现；lobby wrapper 的 41 项只是环境变量缺口。

### 2.4 真实 PostgreSQL：lobby 与 table

**lobby wrapper（用户指定命令）**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File <snapshot>/tools/ci/verify-v5-postgres.ps1 `
  -Configuration Release -RunnerMode direct `
  -DotnetPath D:/Unity/dotnet/dotnet.exe -Port 55720 `
  -LogDir <evidence>/pg-gate
# exit 1（verify stage FAIL；companion + Infrastructure 环境 + LocalHost 测试竞态）
```

实际阶段：precheck / start-postgres / ownership-marker / environment / migrate / archive-cluster-evidence / cleanup / env-restore / write-evidence 均 PASS；只有 `verify` 阶段 FAIL。PG 16.10 官方 zip SHA-256 `EBB3B6AF4FA69DEA9951B66855BC4D42DC04E56CCB9AA7024CE3C58BD89D6B0C`；集群根 `C:/Users/sncdc/AppData/Local/Temp/mahjong-p53-pg-ci-20261029-014033-223f7081`，port 55720，`rootDeleted=true`；lobby 89/89。安装目录 `pgsql/share/timezone`、`timezonesets` 存在，旧 PG12 的 bin/share 缺失未复现。

**table wrapper（补充，覆盖 Infrastructure 冷恢复/备份）**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File <snapshot>/tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -DotnetPath D:/Unity/dotnet/dotnet.exe `
  -Port 55730 -Root <evidence>/table-pg-root `
  -LogDir <evidence>/table-pg-gate
# exit 1：仅最后 cleanup 阶段拒绝 build/ 下的 root；所有实质阶段 PASS
```

实质阶段：cluster PASS（PostgreSQL 16.10，data_directory 精确匹配）；XML self-check 7 个负例 PASS；fresh-build PASS；Infrastructure 42/42、0 failed/skipped；targeted 13/13；crash-writer KILLED 后 committed state=1 seq=2；PostgreSQL crash restart 的 start time 从 `2026-10-28 17:49:00.045281+00` 变为 `2026-10-28 17:50:28.519112+00`，recovery lines=3；crash-reader state=1 seq=2 terminal=True receipt=Applied cheatVersion=1；pg_dump/pg_restore 到空库后 restored events=2、recovery ownerEpoch=3。

原始 cleanup FAIL 保留：wrapper 认为 `<evidence>/table-pg-root` 不在其允许的 `artifacts/` 根下，拒绝递归删除。随后按 ownership 校验做正常清理：`pg-owner.json` SHA `47B7C17AB26E45E9733A3800787131AFFB9718CE3A3E769987A9AA635D464AA2`，`pg_ctl -D <root>/data status` exit 3（no server running），55730 无监听，无 `postmaster.pid`；确认路径为本任务独占 root 后删除，`table-pg-cleanup.txt`。未触碰其他 PG 实例。

## 3. D-LH-CI-COLD（furiten 冷读）

### 3.1 候选 fresh OS 进程结果

- 测试：`Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw`。
- 实际：**PASS**。它在父测试提交 furiten 后，用 `System.Diagnostics.Process` 启动 `Mahjong.LocalHost.CrashWorker.dll ... furiten-read <monotonic> <utc>`，在新的 OS 进程中打开同一 save、走真实 `LocalTableRuntime.OpenAsync` 冷恢复，断言 worker exit 0、marker 四行、roundId 与 temp mask。
- 证据：`unified/direct-xml/Mahjong.LocalHost.Tests/direct-results.xml`（SHA `70E1...`）中该 test result=Pass；`pg-gate` 中同一 test 仍 Pass（该行唯一失败是另一个 recovery 测试）。没有用 dispose/new 代替 OS 进程。
- 该测试同时覆盖 `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw`、`ClientRiichiFuritenStillBlocksRonAfterTheNextDraw`（均 Pass）；LocalHost 全量 101/101（standalone）。

### 3.2 旧 c600611 记录关联

- 旧记录：push 40715805408 / c600611，`furiten-read worker exit=3`，LocalHost 92/93。该历史失败**保留、不销账**；本候选只证明同一测试路径 fresh 一次通过，不证明旧环境根因已修。
- 代码关联：在 c600611 的 `Mahjong.LocalHost.CrashWorker/Program.cs` 中，`furiten-read` 分支成功返回 0；`return 3` 只存在于默认 worker 路径的 `session.State != Ready`（“worker not ready”）分支。因此“furiten-read worker exit=3”与当时代码不自洽：exit 3 不可能由 `furiten-read` 分支直接产生。更可能是默认 mode 的冷进程测试、worker 路径错配或旧二进制/构建状态导致；需要原 CI 的 test name/worker stdout 才能最终归因。本轮没有旧 artifact，故不把它写成已修复。
- 建议：caller 保留旧失败记录；若需要销账，用旧 run 的完整原始 XML/worker stdout 对照，而不是用本候选的绿色替代。

## 4. D-LH-CI-GOLDEN（visibility 冻结字节与 companion）

### 4.1 两边实际运行

| 对象 | 命令 | native exit | 结果 |
|---|---|---:|---|
| accepted PR20（HEAD 17808cc） | `tools/verify-v5-companion-fixtures.ps1 -RepoRoot <review root> -VerifyGitPins` | 0 | **PASS 4/4**；visibility LF `30291EC2...`，render `642cfdda...`；历史 freeze blobs 全部相等 |
| candidate 95b4754（独立 clone exact SHA） | 同脚本 `-RepoRoot <clone> -VerifyGitPins` | 1 | **FAIL 2 项**：`HASH_MISMATCH` actual `1852B55F.../141007B`；`RENDER_SHA_MISMATCH` expected `642cfdda...` got `e8d8f1a5...`；历史 blob pin 未报 mismatch |

candidate 的 visibility 文件：

- LF：141,007 B，raw SHA-256 `1852B55F4EA51F6A7F1A5CED8228D52B2364095CCD31162D2B55015913C58522`。
- CRLF 对照：147,185 B，raw SHA-256 `B388934E339086F9519EA2703B0110A131A224A1EB55F94B4CA7D8D9BCDAA2BE`。
- accepted pins：LF `30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E` / 141,007 B；CRLF `6506C0729A36E4625E157A5E6EA2CCB79B9C2FF2E4F3F57988BBB9535CC52B94` / 147,185 B。

### 4.2 改变来源

`git diff --name-status 17808cc..95b4754 -- tests/v5-golden` 只有：

```text
M tests/v5-golden/visibility-v1.json
```

blob 从 accepted `eddcc00035978be64bca27efc27ec352ce686bf5` 变为 candidate `b890845591cfe8e8108276e9d4ca5635d1e582d2`，字节数仍为 141,007（LF）。diff 只改：

- `TableVisibilityProjection.cs` 的 `bytesRaw/sha256RawBytes/sha256LfNormalized/crlfCount`；
- `TableVisibilityContexts.cs` 的同类字段；
- `renderSha256LfNormalized`。

原因与候选源码一致：`TableVisibilityContexts.cs` 新增 `TemporaryFuriten`/`RiichiFuriten`，`TableVisibilityProjection.cs` 修改 kan/rinshan wall 证据校验。候选源码 LF 归一化哈希确实等于 fixture 内新值（`TableVisibilityProjection.cs` `EB493BF9...`；`TableVisibilityContexts.cs` `E31025DC...`），说明 fixture 是随源码主动更新的，不是测试篡改。

### 4.3 为什么旧字节/源码归档引用必须保持

`tests/v5-golden/visibility-v1.json` 是 accepted companion 的**冻结审计边界**：accepted PR20 的 verifier 固定了它的 LF/CRLF 字节和 render 摘要。就地刷新会把“旧接受样本曾被验证为哪些字节”抹掉，使后续无法证明 v1 的历史接受内容，也会让 companion gate 失去防篡改意义。新源码哈希应作为**新证据/新 fixture 版本**保存，而不是覆盖 v1。

### 4.4 catalog 与 VisibilityGolden

- catalog（同 candidate，LF checkout）exit 1：6 个 fixture 的 raw bytes/SHA 与 CRLF pin 不符；这是行尾环境，不是产品改动。
- 将同一 exact candidate 在独立 clone 用合法 `core.autocrlf=true` checkout 后，6 个 fixture 的 CRLF bytes/SHA 精确匹配 accepted pin：physical `B9384534...`/836369、scoring `53B18110...`/8315131、round `DF5FC5F8...`/43474、cheat `A196CBBD...`/42120、shuffle `386061B4...`/99467、ai `E898CDB6...`/51271。catalog exit 0：**6 fixtures / 73 requirements / 10,389 cases**。
- `VisibilityGoldenTests` 定向 8/8 PASS（XML `visibility-golden-focused.xml`，SHA `3A6E1C412F2510098F6BBFF5B5490C6D286C3031AD9293AC693C9ED95A7AA5D9`），说明消费者解析仍工作；但 8/8 不是 companion 的字节/pin 认证，不能替代四伴随校验。catalog 6/6 也不覆盖 visibility companion。

### 4.5 最小修复建议（由 caller/原 owner 执行，不动源码）

1. 将 `tests/v5-golden/visibility-v1.json` 恢复为 accepted blob `eddcc00`（LF 30291... / CRLF 6506...）；不要把新哈希写回 v1。
2. 把新的 source/render 证据放入交付报告，或另建 `visibility-v2.json` + 独立 verifier/pin，走显式复核；v2 不得冒充 v1。
3. 禁止修改 `tools/verify-v5-companion-fixtures.ps1` 的 `AcceptedCompanionPins`、禁止 `-InternalStructureTestMode`、禁止只刷新摘要。
4. 负例：对 v1 任意一字节改动，companion 必须 exit 1（HASH_MISMATCH）；对 render 字段改动，必须 `RENDER_SHA_MISMATCH`；新 v2 未获接受前，统一门禁必须仍以 v1 为准。

## 5. 跨模块净 diff 与集成攻击面

审查范围：`git diff 17808cca..95b47547`，重点 LocalHost→Client 的 `LOCAL_CONTROL` 闭环、Contracts 控制 tag 20/21、Domain `LegalActionOption.Tiles`、TableActor furiten/候选 checkpoint、visible-event 变更。

### 5.1 已确认正向

- `Contracts` 新增 `ClaimControl(string roundId, ulong expectedControlEpoch)` / `ReleaseControl(...)`，使用 tag 20 字符串 + tag 21 uint64；validator 与 factory 一致；Contracts 测试 150/150。
- `Client` 对 `LOCAL_CONTROL` 使用 `OutboxResolution.ControlApplied`，不伪造 `command_version/event_seq`；FileOutbox 校验拒绝带版本的控制 resolution；Client 182/182。
- `Domain.LegalActionOption.Tiles` 与 `TableActor.BuildCandidateTiles` 保留多牌候选；TableRuntime 413/413，包含 checkpoint v2/v3 兼容与候选实体恢复。
- `TableActor` furiten snapshot 只在 durable commit 成功后安装，`FuritenCommitted` 回调不接收拒绝/重放；LocalHost furiten 相关 5 个用例 Pass。
- `TableProjectionFactsCodec` v2 新布尔字段兼容 v1；`TableTimingCheckpointCodec` v3 兼容 v2；`TableProjectionFactsCodec.ReadIdentity` 防止旧 baseline facts 被误用到 synthetic commit。
- `LocalHost` 的 101/101、TableRuntime 413/413、visible-event Client 182/182、Architecture 130/130 在 standalone 运行均真实通过，说明主路径没有明显回归。

### 5.2 已确认负向

- **D-LH-GATE-01**：Contracts 的生产者/校验器已切到 20/21，TableService 的 `MapControl` 仍读 1。WSS 控制命令在进入 actor 前被 `PAYLOAD_INVALID` 拒绝；这不是测试问题，是跨模块生产路径不一致。
- **D-LH-GATE-02**：LocalHost 的控制早退拒绝不持久化；`TryRejectStaleDecision` 同时服务普通命令与控制命令，早退路径只 `Enqueue(RejectedReceipt)`，没有 request-keyed outcome。Client 重连查询得到 NotFound，持久 outbox 永远未决。
- **D-LH-GATE-03**：冻结 visibility fixture 被就地更新，mandatory companion 失败；属于门禁/证据链阻断。
- **D-LH-GATE-04**：recovery 测试在自动摸牌与 snapshot 之间抓版本，PG 负载下出现 saved=2 committed=1；属于测试稳定性阻断。

### 5.3 观察项（非本轮阻断，不建议当缺陷计数）

- `TableActor.GetRoundStateForHost()` 返回 live `RoundStateMachine` 引用；`GetSeatHandTileIds` 暴露本座位手牌给任何引用 TableRuntime 的宿主。当前 LocalHost 只读使用，TableService 不远程暴露；但这是新增的 host-only 可变 authority 面，建议后续改为 immutable snapshot 或 `internal`，不要让 UI/远程路径可达。
- Unity `TableCommandIntentFactory.CreateClaimControl/CreateReleaseControl` 仍抛 `ControlPayloadFieldCollision`（`unity/.../TableCommandIntentFactory.cs:647-688`）。P4.2-B 正在另一线程处理；本候选不重复其 Unity/UI 工作，也不把它算作本候选新增缺陷。
- `FuritenCommitted` 回调写入的 `bool[]` 与 driver 读取之间没有显式同步；本轮未复现数据竞争，不作为确认缺陷，仅记录为后续并发审查点。

## 6. 缺陷清单（一次给全）

### D-LH-GATE-01 — MAJOR — TableService 仍按旧 tag 读取控制命令，合法 Claim/Release 被拒绝

**位置**

- `dotnet/src/Mahjong.TableService/TableCommandMapper.cs:606-622`：`MapControl` 使用 `CommandPayloadFields.ExpectedControlEpoch`（=1）。
- `dotnet/src/Mahjong.Contracts/ProtocolCommand.cs:375-379`：validator 要求 `ExpectedControlEpochV2`（=21）与 `ControlRoundId`（=20）。
- `dotnet/src/Mahjong.Contracts/ProtocolCommand.cs:691-704`：factory 写 20/21。
- `dotnet/tests/Mahjong.TableService.Tests/SessionFlowTests.cs:321-367`：旧测试只发送旧 tag 的单字段 payload，并断言“控制命令永远无法满足 frozen validator”；它没有覆盖新的合法 tag 20/21 正例。

**复现**

```powershell
# ignored probe copy: build/lh-gate-95b4754/probe-tableservice-95b4754
D:/Unity/dotnet/dotnet.exe build `
  <probe>/dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj `
  -c Release --no-incremental --nologo -m:1 -nr:false -p:UseSharedCompilation=false
# exit 0；0 warning/0 error；probe-tableservice-build.log

D:/Unity/dotnet/dotnet.exe `
  <probe>/dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll `
  -noLogo -method Mahjong.TableService.Tests.ReviewProbeControlPayloadTests.AcceptedClaimControlPayloadFromContractsMapsToControlRoute `
  -result-xml <evidence>/probe-tableservice.xml
# exit 1
```

探针构造 `CommandPayloadFactory.ClaimControl("room-probe", 0)`（合法新 tag 20/21），经 `TableCommandMapper.Map` 进入 `MapControl`。

**实际**

`CanonicalEncodingException : Missing required field expectedControlEpoch (1).`；XML total 1 / passed 0 / failed 1 / errors 0 / skipped 0 / not-run 0；XML SHA `6BCAF45B5C7C03BE05C63E7E0EF959A8B956356825FCA1853DA511395F61CF98`；源码 SHA `C7038C5DE4A9E65581677873CF39750C998730A44AD8806FFA82532FFFB2E7FF`。host 的 `TableSessionHandler` 在 `Map` 外包了 `catch (Exception)`，会把该异常转成 `MALFORMED_MESSAGE` / `PAYLOAD_INVALID`，即合法控制命令被当作坏载荷。

**期望**

合法 `ClaimControl/ReleaseControl`（tag 20 字符串 + tag 21 uint64）映射为 `TableCommandRoute.Control`，`Reclaim=true`（Claim）/`false`（Release），`ExpectedControlEpoch` 等于输入；不得抛 `CanonicalEncodingException`，不得回 `PAYLOAD_INVALID`。旧 tag 1 单字段/重复字段仍应被 validator 拒绝；同 request 异字节仍冲突。

**修复要求**

- `MapControl` 改读 `CommandPayloadFields.ExpectedControlEpochV2`；如需要，校验 `ControlRoundId` 与 `authoritativeRoundId` 一致。
- 更新 `SessionFlowTests.ControlCommandKind_IsGatedByTheFrozenFieldContract` 为“合法新 tag 被接受 + 旧 tag/重复字段被拒绝”的正负矩阵；不要删除旧负例。
- 增加 host-level 测试：真实 `TableTestClient` 发送 `CommandPayloadFactory.ClaimControl/ReleaseControl`，期望 actor 收到 `Control` 路由并产生 receipt，而不是 ERROR。

**验收断言**

- 上述 probe exit 0，XML 1/1 Pass。
- TableService 全量 ≥152/152，0 failed/skipped/errors/not-run。
- 旧 tag 1 仍返回 `MALFORMED_MESSAGE/PAYLOAD_INVALID`；异字节冲突仍 `REQUEST_ID_CONFLICT`；无第二次控制变更。

### D-LH-GATE-02 — MAJOR — 旧 decision 的控制拒绝不持久化，丢回包后无法对账

**位置**

- `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs:773-777`：控制命令在进入 `HandleControlCommandAsync` 之前不经过普通 receipt 持久化。
- `LocalTableRuntime.cs:1107-1133`：`TryRejectStaleDecision` 只 `Enqueue(RejectedReceipt(...))`，不写 `PrepareReceiptDigest`/control outcome；普通命令在 `:790` 之后才准备 digest，控制路径直接 return。
- `LocalTableRuntime.cs:1150-1185`：`HandleControlCommandAsync` 的持久 `GetControlResult` 只覆盖已记录的成功结果；早退拒绝永远到不了这里。
- `LocalTableRuntime.cs:1476-1544`：`HandleReceiptQueryAsync` 只在 store 有 receipt/control record 时返回 FOUND；早退拒绝没有记录。
- `LocalTableStore.cs` 的 `CommitControlAsync`/`PrepareControlResult` 只在实际控制 change/记录路径写 `ControlResults`；早退拒绝不经过。

**复现**

在 ignored 副本 `probe-control-rejection-95b4754` 的 `LocalControlTests.cs` 中加入 `DroppedStaleControlRejectionCannotBeReconciledAfterReconnect`：用 `FileOutboxStore` 打开真实 `TableSession`，等 seat0 的真实 decision，推进确定性时钟 30s 使 decision 退休，提交一个带旧 decision 的 `ReleaseControl` 新请求；包装 channel 丢弃第一条 `DECISION_MISMATCH` 拒绝回执；确认 outbox `PendingCommands=1`、`NeedsReconciliation=true`；重连后等待 `RECEIPT_QUERY`，断言 pending 归零。

```powershell
D:/Unity/dotnet/dotnet.exe build `
  <probe>/dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj `
  -c Release --no-incremental --nologo -m:1 -nr:false -p:UseSharedCompilation=false
# exit 0；0 warning/0 error；probe-control-build.log

D:/Unity/dotnet/dotnet.exe `
  <probe>/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -class Mahjong.LocalHost.Tests.LocalControlTests `
  -method Mahjong.LocalHost.Tests.LocalControlTests.DroppedStaleControlRejectionCannotBeReconciledAfterReconnect `
  -result-xml <evidence>/probe-control-rejection.xml -parallelMode none
# exit 1
```

**实际**

`the rejected control request stayed unresolved: count=1 lastLookup=NotFound`；XML total 1 / passed 0 / failed 1 / errors 0 / skipped 0 / not-run 0；XML SHA `1783C3ECB91053B0C77DA22EDA95C1DB8FF7798C0E1F2CB33C083F643E0B2D29`；探针源 SHA `D0C76E8ECFEF4EF35D2AEBCB485F9CC749E3D41E3902C3CF658C09571F432A0A`。

**期望**

对已认证请求的确定性拒绝也必须是 request-keyed 持久结果：重连 `RECEIPT_QUERY` 返回 `FOUND` + `Rejected` + `DECISION_MISMATCH`，持久 outbox 解析为空；不得让 `NotFound` 成为终态。只有从未记录过的新请求才应被拒绝；拒绝不得产生控制副作用，`ControlVersion/ControlEpoch` 不变。同 request 异字节的 `REQUEST_ID_CONFLICT` 早退也应具备同一对账语义。

**修复要求**

- 在 `TryRejectStaleDecision` 的早退之前/之中，把拒绝按 `(account, scope, request)` 写入 durable receipt/control outcome；或把早退改走与 actor 拒绝相同的持久化路径，再 enqueue。
- 保留 scope/身份校验：只对已认证、同账户/房间/scope 的请求写拒绝记录；不能让过期/撤销 scope 借“持久拒绝”绕过授权。
- 保留真正新请求的旧 decision 拒绝与零副作用；不得把已记录请求的 exact replay 重新判成拒绝。
- 增加回归：丢拒绝回包 + 重连 + 冷重启；同 request 异字节；异 kind；过期/撤销 scope；并发 Claim/Release。

**验收断言**

- 上述 probe exit 0，XML 1/1 Pass；`PendingCommands` 为空，`LoadUnresolvedAsync` 为空。
- 拒绝回执查询返回原 reason/digest，`ControlVersion/ControlEpoch` 不变；无第二次控制记录。
- LocalHost 全量 ≥101/101；LocalControl 定向 ≥10/10；新增回归不得删除/弱化。

### D-LH-GATE-03 — MAJOR — visibility-v1 被就地改写，accepted companion gate 失败

**位置**

- `tests/v5-golden/visibility-v1.json`：`TableVisibilityProjection.cs` / `TableVisibilityContexts.cs` 的 source metadata 与 `renderSha256LfNormalized`。
- `tools/verify-v5-companion-fixtures.ps1:266-289`：accepted pins（LF/CRLF bytes、render、freeze commit、blob）。
- accepted PR20 blob `eddcc00035978be64bca27efc27ec352ce686bf5`；candidate blob `b890845591cfe8e8108276e9d4ca5635d1e582d2`。

**复现**

```powershell
# accepted baseline
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/verify-v5-companion-fixtures.ps1 `
  -RepoRoot D:/majiang-wt/localhost-batch-gate-ds-20261029 -VerifyGitPins
# exit 0；PASS 4/4

# candidate exact clone
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File <clone>/tools/verify-v5-companion-fixtures.ps1 `
  -RepoRoot <clone> -VerifyGitPins
# exit 1；2 failures
```

**实际**

- `HASH_MISMATCH`：actual `1852B55F4EA51F6A7F1A5CED8228D52B2364095CCD31162D2B55015913C58522` / 141,007 B；expected LF `30291EC2...` / 141,007 B 或 CRLF `6506C072...` / 147,185 B。
- `RENDER_SHA_MISMATCH`：expected `642cfdda1c11587c63ae9f8587c24c4959f7ed3e8bea5bd51d70a36a7b4cb4b8`，actual `e8d8f1a543f0554adeed88383060f49ea0392e56e8e8648a0cb7caab1edce502`。
- native exit 1；companion log SHA `71DB75C383B284DDC8022704A7386F37504BFFE2F745DDB4C1BDEBACED6D2889`（pg-gate run）。
- 只有 visibility 一个 fixture 变化；catalog 的 6 个 fixture 未变，CRLF 对照 6/6 PASS，但 catalog 不校验四伴随 fixture。

**期望**

accepted companion 4/4 PASS；`visibility-v1.json` 字节与 accepted pin 完全一致；新源码哈希/render 证据不影响 v1 的历史审计引用。

**修复要求**

- 由 caller 决定：把 v1 恢复为 `eddcc00`，把新证据放交付报告/新版本 fixture；或为 v2 建立独立 verifier/pin 并取得显式接受。
- 禁止刷新 accepted pin、禁止修改 verifier 放行、禁止用 `-InternalStructureTestMode` 或 catalog 6/6 替代。
- 负例：任意一字节改动必须 HASH_MISMATCH；render 字段改动必须 RENDER_SHA_MISMATCH；v2 未接受前不得让统一门禁转绿。

**验收断言**

- `tools/verify-v5-companion-fixtures.ps1 -VerifyGitPins` 4/4 PASS，且 `git cat-file -p 4086bf3:tests/v5-golden/visibility-v1.json` 的 blob 与 `eddcc00` 一致。
- `git diff 17808cc..<new-head> -- tests/v5-golden` 要么为空，要么只包含新版本文件且 verifier 已显式接受。
- catalog 6/6 与 VisibilityGolden 8/8 仍通过，但证据表明确列出它们不替代 companion。

### D-LH-GATE-04 — MAJOR — ColdRestartRestoresCommittedStateVersion 测试竞态（PG 负载下 1 次失败）

**位置**

- `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:23-50`：discard 后立即读 `session.PlayerSnapshot.StateVersion` 作为 `committedVersion`。
- 同文件 `:656-720`：`SubmitDiscardAsync` 只等待“任意 snapshot 版本 > baseline”，而 discard 会触发下一家自动摸牌，store 可先到 2；测试再读 snapshot 可能仍是 1。
- 该测试不在 `LocalHostSerial` 串行集合中；PG wrapper 并发/负载下暴露。

**复现**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File <snapshot>/tools/ci/verify-v5-postgres.ps1 `
  -Configuration Release -RunnerMode direct `
  -DotnetPath D:/Unity/dotnet/dotnet.exe -Port 55720 `
  -LogDir <evidence>/pg-gate
# exit 1；LocalHost total 101 / passed 100 / failed 1
```

**实际**

`ColdRestartRestoresCommittedStateVersion` → `saved=2 committed=1`；native exit 1；XML SHA `DFF26B384DDD59C4177980DA0E50C0778EC8B64CC4CBC1279B76DC8BAC8549DF`；framework log `pg-gate/direct-Mahjong.LocalHost.Tests.log`。standalone unified 同测试 101/101 Pass，说明不是每次必现，但首次失败已保留。

**期望**

测试捕获的是 discard 这一命令的 committed version（receipt 的 `CommandVersion`/`Evaluated+1`），或等到 snapshot 与 store 都稳定在目标版本；断言 store 与恢复 snapshot 精确等于该版本。不得用 sleep/放宽断言/重试掩盖；不得删除测试。

**修复要求**

- 从 `TableReceiptResolvedEventArgs` 取 receipt 的 `CommandVersion`（或等价权威版本）作为 `committedVersion`；或轮询至少到该版本且没有后续自动摸牌推进。
- 不要把断言改成 `>=` 或只断言 `>0`；保留 exact equality 与 roundId 断言。
- 如需串行化，加入 `LocalHostSerial` 或明确的测试隔离，但不得用串行化掩盖真实竞态。

**验收断言**

- 在真实 PG wrapper / 并发负载下重复运行 LocalHost 全量，101/101，0 failed/skipped/errors/not-run。
- 失败信息不再出现 `saved=2 committed=1`；store 与恢复 snapshot 精确相等。
- 该修复不改变产品代码；若认为产品 snapshot 发布顺序有问题，另立产品缺陷并给出事件顺序断言。

## 7. 逐维检查结论

| 维度 | 结论 | 证据/边界 |
|---|---|---|
| 范围与路径 | **通过（审查输入）** | 唯一 tracked 交付为本文件；候选净 diff 与双父/merge tree 精确；未改 receiver/caller 树；`git diff --check` exit 0；tracked clean。 |
| 假绿/skip/空测试/旧产物 | **通过（测试执行）** | fresh `--no-incremental`；直接运行 fresh DLL；XML 2452 非零；0 errors/not-run；无 skip 冲绿；旧产物未执行。 |
| 功能/终态/136 守恒 | **主路径通过，控制边界有缺陷** | Domain 529/529、TableRuntime 413/413、LocalHost 101/101（standalone）；136/守恒在 Domain/TableRuntime/Infrastructure 用例中真实执行；D-LH-GATE-02 控制拒绝未对账。 |
| 并发/deadline/epoch/恢复/幂等 | **有缺陷** | Claim/Release 竞争、deadline、旧 epoch、冷恢复主路径通过；D-LH-GATE-02 丢拒绝回包；D-LH-GATE-04 测试版本竞态；PG crash/backup 通过。 |
| 安全与隐私 | **未发现新增泄露** | 控制 receipt 只含 digest/kind/evaluated；LocalHost ticket/privacy 用例通过；未做真实远端渗透，WSS 控制路径被 D-LH-GATE-01 阻断。 |
| 证据与口径 | **完整** | 每个失败有 native exit/XML 计数/SHA/原始日志；PASS/FAIL/BLOCKED/NOT_RUN 分开；未把候选本地结果写 CI PASS。 |
| 环境与平台 | **有 BLOCKED/NOT_RUN** | lobby wrapper 的 41 个 Infrastructure 为缺 P52 env（BLOCKED）；table wrapper 42/42 补充；Unity/Player/Android/iOS/真机/跨网/整场/G2–G5 NOT_RUN；iOS BLOCKED。 |
| 文档一致性 | **本文件为审查输入，不夸大** | 候选未 push；PR21 仍 Draft；a/b/c/d 组件接受与整体未接受分开；G1 approved、G2–G5 未批准、181 classified 未覆盖、V4 生产。 |

## 8. NOT_RUN / BLOCKED（不得读成 PASS）

1. **Unity Editor / Player / IL2CPP / Android / iOS / 真实设备 / 跨物理网络 / 整场 / G2–G5**：本轮未执行；P4.2-B Unity/UI 另有在途 DS 线程，本任务不重复。iOS 环境 BLOCKED。
2. **远端 WSS/TCP 端到端、TLS、公网**：未执行。TableService 只做了本地 mapper probe；D-LH-GATE-01 表明该路径当前不可用。
3. **候选远端 CI**：候选未 push；本轮未获取/引用 PR21 最新 40732675017/40732456930 或 PR20 17808cc 双 CI 的 artifact；不写 CI PASS。旧 3afefd5 PR40729458798 FAIL 保留未归因，未盲重试。
4. **旧 c600611 push 40715805408 的 furiten-read exit 3**：保留原记录，未销账；本轮候选 fresh 冷读 PASS 一次，不证明旧环境根因。
5. **lobby wrapper 的 Infrastructure 41 项**：BLOCKED（缺 `MAHJONG_P52_TEST_POSTGRES_ADMIN`），不是产品失败；table wrapper 已补充 42/42，但原 wrapper exit 1/cleanup FAIL 保留。
6. **table PG wrapper cleanup**：原 cleanup FAIL 保留（root 不在 `artifacts/`）；本任务在 ownership/路径/PID/端口校验后正常删除自己的 root，未 kill 他人/全局工具链。
7. **`CommitControlAsync` 文件 replace 瞬间进程强杀、两进程同写 FileOutbox、真实 24h 墙钟**：未执行；不写成通过。
8. **安全渗透/票据越权 WSS**：未执行；只在 LocalHost 组件范围检查权限/隐私断言。

## 9. 候选可接受部分与建议

- **可接受（组件/回归）**：LocalHost standalone 101/101（含 D-LH-CI-COLD 新 OS 进程冷读、furiten 冷恢复、控制成功重放/冲突/过期、原子存档/恢复）；TableRuntime 413/413；Client 182/182；Architecture 130/130；Contracts 150/150；Domain 529/529；Ai 444/444；Client.Wss 80/80；TableService 152/152（测试数；控制 tag 路径未覆盖，见 D-LH-GATE-01）；Unity.TableModel 140/140；真实表 PG Infrastructure 42/42 + crash/backup；Lobby PG 89/89；VisibilityGolden 8/8；catalog 6/6（CRLF 对照）。
- **整体不可接受**：D-LH-GATE-01/02/03/04 未关闭前，不能把候选写成“可汇入 PR20”或“LocalHost 整体通过”。a/b/c/d 的组件接受继续有效，但不等于完整 LocalHost 接受。
- **最小修复顺序**：先修 D-LH-GATE-01（TableService 20/21）、D-LH-GATE-02（拒绝持久化）、D-LH-GATE-04（测试版本捕获），再处理 D-LH-GATE-03（恢复 v1 或版本化 v2）；之后重跑 companion、TableService、LocalControl、LocalHost 全量、PG lobby+table、`git diff --check`。
- **不要做**：不要刷新 companion pin/改 verifier、不要改 v1 摘要放行、不要删除/弱化探针、不要用 catalog 6/6 或 VisibilityGolden 8/8 替代 companion、不要用重试/串行化掩盖 D-LH-GATE-04、不要在本候选上 push 触发 CI。

## 10. 证据索引

证据根：`D:/majiang-wt/localhost-batch-gate-ds-20261029/build/lh-gate-95b4754/`（ignored）。

| 文件 | 用途 | SHA-256 |
|---|---|---|
| `commands.txt` | 原命令/native exit 索引 | `0BFAB8FF10139EC03D05E8EFD1D13279E167AF6D44FE212F6F1FDD6BFE969C13` |
| `summary.json` | 机器可读摘要 | `C118C7E45772B73EE867F82C0789A1D69C23064A6478531C4F0D7DAE9B8F18CC` |
| `test-counts.csv` | 逐工程 XML 计数/exit/XML SHA | `D964FF1F2F8C518B41B0A1E5CC61228178BDEC36CA0427AB3995E7C3C6120338` |
| `fresh-dll-sha256.csv` | fresh DLL 完整 SHA | `BA40C138E9353DCE0813FFF4801A4B44A307E4F55024FA279487726B67AE88AE` |
| `evidence-sha256.csv` | 外层证据 manifest（306 行） | `9CDB394EDA3230E68BAA74E56FDBF50183D95FFA099BC614E126449A953E649D` |
| `manifest-verify.log` | 306 行逐行重哈希结果（306 OK / 0 mismatch / 0 missing） | `A76F35A62F689EAD7FE6866B6187DEF31769F69F9DA45E26F41EA2950EA42DD5` |
| `candidate-95b4754.zip` | exact `git archive` | `9720FD553EBC9AA387062ACA2E73146EF8F3677D8256E2486487A69D25810AFC` |
| `metadata.txt` | 审查 HEAD/branch/candidate/parents/tree/zip/祖先退出码 | 见 manifest |
| `clone-metadata.txt` | 独立 clone HEAD/tree/clean | 见 manifest |
| `unified/` | 无 PG 的完整 direct 运行日志/XML/meta | 见 manifest |
| `pg-gate/` | lobby PG wrapper 原始日志/XML/meta/pg 证据 | 见 manifest |
| `table-pg-gate/` | table PG wrapper 原始日志/XML/summary/pg-owner | 见 manifest |
| `probe-tableservice*` | D-LH-GATE-01 探针源码/构建/运行/XML | 源码 `C703...`；XML `6BCA...` |
| `probe-control-rejection*` | D-LH-GATE-02 探针源码/构建/运行/XML | 源码 `D0C7...`；XML `1783...` |
| `companion-*.log` | accepted/candidate companion 原始输出 | 见 manifest |
| `catalog-git-candidate-*.log` | LF/CRLF catalog 对照 | 见 manifest |
| `visibility-golden-focused.xml` | VisibilityGolden 8/8 | 见 manifest |
| `table-pg-cleanup.txt` | 自己 root 的清理校验 | 见 manifest |

manifest 的 306 行全部重哈希为 OK；`evidence-sha256.csv` 本身不在自身行内（自引用排除），其 SHA 如上。`manifest-verify.log` 是逐行重哈希的原始结果。

## 11. 结论

- **审查输入：PASS**。本报告一次给全 4 个已确认缺陷，所有结论都有 fresh 快照、真实 XML/native exit、SHA 和 ignored 证据；未执行项已单列。
- **候选门禁：REWORK**。D-LH-GATE-01/02/03/04 未关闭前，候选不能汇入唯一现有 PR20；D-LH-CI-COLD 的一次通过、catalog 6/6、VisibilityGolden 8/8 均不能抵消 companion 失败或控制路径缺陷。
- **不是 REVIEW_INCOMPLETE**：4 个缺陷都已实际复现/定位；唯一的环境缺口（lobby wrapper 的 Infrastructure P52 env）已用 table wrapper 42/42 补充并明确标注。
- **不宣称**：P4.1 整体、G2–G5、Unity/Player/IL2CPP、Android/iOS、真实设备、跨物理网络、WSS 端到端、整场、远端 CI 通过。V4 仍是唯一生产基线；G1 仍 APPROVED，G2–G5 未批准，181 classified 未覆盖；PR21 仍 Draft，不 push/不合并/不建 `complete.flag`。
