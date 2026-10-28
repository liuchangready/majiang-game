# V5 PR20 `5cc27b6` 双 CI 原始证据独立审查（2026-10-28）

审查者：DeepSeek 第二槽（只读证据审查）
审查对象：PR20 当前已接受发布头 `5cc27b6fd551a7b32a89ab2f9351852973b6d431` 的两个 completed run
审查性质：**只核对远端原始 XML、日志、ZIP、API metadata 与当前头只读源码；不修生产代码，不重跑 PG/Unity/.NET 全量。**

## 0. expectedFiles 与结论

本次唯一 tracked 变更：

```text
A doc/v5-重构/CODEX-V5-PR20-5CC27B6-DUAL-CI-REVIEW-20261028.md
```

结论分三层：

1. **本次远端补验义务：PASS。** 两个 run 的原始 artifact 可取得，ZIP SHA256 与 API/job log 三处一致；11 个 direct runner 各 2,299/2,299，PG full 42/42、targeted 13/13，companion 4/4（47 cases），native exit 传播 6/6，fresh Release build 0 warnings/0 errors，12-DLL 闭包成立。
2. **证据缺口单列，不用总体 PASS 覆盖。** 10/11 个 direct runner 的测试 DLL SHA 未在 artifact 中记录；crash/restore reader 只记录 `stateVersion/lastEventSeq/eventCount/receipt/cheatVersion/ownerEpoch`，未记录 136 张计数、stateHash、checkpoint hash、ledger hash。前者无法从本 artifact 补算，后者由另外 42/42 中的专用恢复用例覆盖，但不能写成“crash 路径已直接证明 136/账本/hash”。
3. **历史失败不改写。** `8a0c8cb` 双 CI 的真实 `preparationMs=1999` 失败保留；D-PG-SUP-02 原始四失败根因仍 **UNRESOLVED**；本次新头绿只关闭本次远端补验义务，不关闭该历史缺陷，不重开已接受代码，也不扩大为 PG 性能调查。

冻结上下文：G1 仍 APPROVED、PR19 已合并；G2–G5 未批准；V4 仍是唯一生产基线；181 classified 缺口未覆盖。PR20 仍为 `open`、`merged=false`，唯一同批发布仍 targeting `v5`，由 caller 审核集成、push 与 MR 记录。本轮未操作 LocalHost PR21（另一 owner）、其他线程/工作树、PG 集群、Unity 集群或 scheduler/`complete.flag`。

## 1. 工作树、分支与冻结

| 项 | 实测值 |
|---|---|
| 工作树 | `D:/majiang-wt/pr20-ci-5cc27b6-ds-20261028-1950` |
| 分支 | `wt/v5-pr20-ci-5cc27b6-ds-20261028-1950` |
| `git rev-parse HEAD` | `5cc27b6fd551a7b32a89ab2f9351852973b6d431` |
| `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| `git merge-base --is-ancestor origin/v5 HEAD` | exit 0 |
| `origin/v5..HEAD` / `HEAD..origin/v5` | 123 / 0 |
| tracked status | 空（创建本报告前；报告提交后仍要求 tracked clean） |
| 基线来源 | 从 `origin/v5=8fd7c6c` 新建本树后 ff-only 到 `5cc27b6`；未使用旧 `main`/PR19 source |

caller 本轮已在同树 ignored `artifacts/native-exit-e1af36eb22e347ca9fc35ddf466050f1/` 实跑 native exit 6/6、native 0。这是 caller 的实际检查，本报告只把它作为旁证，**不冒充本次 reviewer 复跑**；本报告的 6/6 来自两个远端 artifact 内的 `native-exit-*/results.json`。

## 2. 两个 run：身份、实际 checkout 与 artifact

| 项 | push | pull_request |
|---|---|---|
| Run ID / URL | `40679445727` / https://github.com/liuchangready/majiang-game/actions/runs/40679445727 | `40679453530` / https://github.com/liuchangready/majiang-game/actions/runs/40679453530 |
| Event | `push` | `pull_request` |
| API `head_sha` | `5cc27b6fd551a7b32a89ab2f9351852973b6d431` | `5cc27b6fd551a7b32a89ab2f9351852973b6d431` |
| 实际 checkout / `GITHUB_SHA` | `5cc27b6fd551a7b32a89ab2f9351852973b6d431` | `696338afcaed1daadff9edc53dd2b5233df6ead1` |
| 实际 ref | `refs/heads/feat/v5-post-g1-batch` | `refs/pull/20/merge` |
| Tree | `5560b318865c27c426cf116d42c399d556aab100` | 同左 |
| Created / Updated (UTC) | `11:22:27` / `11:29:39` | `11:22:31` / `11:29:41` |
| Job ID / started / completed (UTC) | `122786089918` / `11:22:30` / `11:29:38` | `122786116237` / `11:22:34` / `11:29:40` |
| Conclusion | `success` | `success` |
| Artifact | id `12812546702`，`v5-verify-logs-40679445727-1`，4,291,907 bytes | id `12811464520`，`v5-verify-logs-40679453530-1`，4,291,971 bytes |
| API digest | `sha256:e52c07e434aea49bad7a5bb4021a600117e70e4b11844cbcec678f4a46fd306c` | `sha256:0dfb80bc283434ee5fa801c9620ca7d7b41dd979be0e46f0153516d715e203fd` |
| 下载 ZIP SHA256 | `E52C07E434AEA49BAD7A5BB4021A600117E70E4B11844CBCEC678F4A46FD306C` | `0DFB80BC283434EE5FA801C9620CA7D7B41DD979BE0E46F0153516D715E203FD` |
| 下载字节数 | 4,291,907 | 4,291,971 |
| ZIP 文件条目 | 242 | 242 |

### 2.1 PR 合成提交不是 API `head_sha`

PR run 的实际 checkout 日志：

```text
fetch ... +696338afcaed1daadff9edc53dd2b5233df6ead1:refs/remotes/pull/20/merge
HEAD is now at 696338a Merge 5cc27b6fd551a7b32a89ab2f9351852973b6d431 into 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
```

独立核对：

```text
merge SHA  : 696338afcaed1daadff9edc53dd2b5233df6ead1
parents    : 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7
             5cc27b6fd551a7b32a89ab2f9351852973b6d431
merge tree : 5560b318865c27c426cf116d42c399d556aab100
head tree  : 5560b318865c27c426cf116d42c399d556aab100
git merge-tree --write-tree 8fd7c6c 5cc27b6 : 5560b318865c27c426cf116d42c399d556aab100
git diff 696338a 5cc27b6 : 空，exit 0
```

因此：API `head_sha=5cc27b6` 是 PR 源分支头，不是该 run 的实际 checkout；PR 实际执行的是 `696338a`，其双父与 tree 均与 `5cc27b6` 一致，不能把 API 字段直接当成实际 checkout。PR API `merged=false`、`merge_commit_sha=696338a`，说明这只是 GitHub 的测试合并，不是已合并。

## 3. 原始证据获取与完整性

两个 run 的命令相同（job log 原文）：

```powershell
./tools/ci/test-v5-native-exit.ps1
./tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -RunUnifiedVerification `
  -LogDir artifacts/verify-v5/table -UnifiedLogDir artifacts/verify-v5/unified
```

证据根（ignored）：

```text
D:/majiang-wt/pr20-ci-5cc27b6-ds-20261028-1950/artifacts/ds-pr20-ci-5cc27b6-20261028-1950/
```

| 原始对象 | push | pull_request |
|---|---|---|
| artifact ZIP | `artifacts/v5-verify-logs-40679445727-1.zip` | `artifacts/v5-verify-logs-40679453530-1.zip` |
| job 完整日志 | `logs/job-122786089918.log`，23,814 bytes，`F61AF096CE79F156B2DCA246D0BA51826DB089678D465F2ED4A4E0A9BE33FEF3` | `logs/job-122786116237.log`，24,708 bytes，`2CD4C0602320C0301F8C8A3C526F37965952DA86406BF2EE13E1A0C6044DFA1D` |
| workflow run logs ZIP | `logs/run-40679445727.logs.zip`，16,404 bytes，`67525558EF157F16339A7FE5DB81812B3182E2CDD2F39BAAB72DD809A487819F` | `logs/run-40679453530.logs.zip`，17,149 bytes，`721157AE6C6B166002EDBE79BD00FB3265C01FED2436F0D34BD63B01F3AB42F1` |
| API run JSON | `meta/run-40679445727.json` | `meta/run-40679453530.json` |
| API jobs/artifacts JSON | `meta/run-40679445727-jobs.json`、`-artifacts.json` | `meta/run-40679453530-jobs.json`、`-artifacts.json` |
| PR / commit JSON | `meta/pull-20.json`、`meta/commit-696338a.json`、`meta/commit-5cc27b6.json` | 同左 |

核对结果：

- 下载 ZIP 的 SHA256 与 API digest 完全一致，字节数与 API `size_in_bytes`、job log “Uploaded bytes” 一致；job log 还打印了同一 digest。
- 两个 ZIP 均为 242 个文件条目；`tar -xf` 解出 `verify-v5/**` 与 `native-exit-*/**`，无缺项。
- ZIP entry 的 mtime 在解包后被时区归一化（显示为 `03:26Z`，实际 CI 为 `11:26Z`），因此**不以解包 mtime 做 freshness 判定**；本报告只使用 XML 内 `start-rtf/finish-rtf` 与内容 SHA。生产 gate 的 freshness 分支不适用于回放已解包 artifact，这一点不构成 PASS 或 FAIL。
- 完整 ZIP 目录清单与逐文件 SHA 见 `inventory/push-zip-listing.tsv`、`inventory/pr-zip-listing.tsv`（各 242 行 + 表头）。
- 不自引用 manifest 见 `inventory/sha256-manifest.tsv`：507 个条目，覆盖 507/508 个证据文件，唯一排除项是 manifest 自身；逐项长度/SHA 复算 0 mismatch。

## 4. 构建、12-DLL 闭包、companion 与 native exit

| 项 | push | pull_request |
|---|---|---|
| fresh Release tests build | `Build succeeded. 0 Warning(s) 0 Error(s)`（`build-tests.log`） | 同左 |
| fresh Release crash-worker build | `0 Warning(s) 0 Error(s)`（`build-worker.log`） | 同左 |
| solution build | `0 Warning(s) 0 Error(s)`（`unified/sln-build.log`） | 同左 |
| 12-DLL closure | 12 个 DLL，`copied 19 files`（含 7 PDB） | 同左 |
| companion fixtures | `PASS: 4/4`，8+32+4+3=47 cases | 同左 |
| companion raw native exit | 0 | 0 |
| native exit propagation | 6/6：0、1、2、17、throw、-1 | 6/6：同值 |
| full PG tests | 42/42 | 42/42 |
| targeted PG tests | 13/13 | 13/13 |
| Infrastructure test DLL SHA256 | `04149C45BC780FB64572F1C25B178DC743E36A5C89D6D0B2CFCD3D001718776B` | `821878DC1ACA08E7406AB96E6F715C337D4FD2129AE70D3DAF1EBE255D5B0821` |
| 外层 wrapper native exit | 0，`P52-TABLE-POSTGRES-PASS` | 0，`P52-TABLE-POSTGRES-PASS` |

12 个 DLL 名单（两 run 相同）：

```text
Google.Protobuf.dll, Mahjong.Ai.dll, Mahjong.Client.dll, Mahjong.Contracts.dll,
Mahjong.Contracts.Wire.dll, Mahjong.Domain.dll, Mahjong.LocalHost.dll,
Mahjong.TableRuntime.dll, System.Buffers.dll, System.Memory.dll,
System.Numerics.Vectors.dll, System.Runtime.CompilerServices.Unsafe.dll
```

说明：`tools/ci/verify-v5-table-postgres.ps1` 在 `5cc27b6` 的 fresh-build 分支使用 `--no-incremental`（脚本第 550、555 行）；job 日志不逐字回显该参数，但 build 日志显示实际重新编译且 0/0，wrapper stage 为 `fresh-build: PASS`。本报告不把“日志没回显”写成已回显。

## 5. 11 个 direct runner：XML / console / meta 三方

### 5.1 计数矩阵

两 run 的 `fail/errors/skipped/notRun` 全部为 0；下表 total 同时作为 `selected`（runner XML 没有独立的 `@selected` 字段，不能伪造该字段）。console 与 meta 均与 XML 同值。

| Project | push total/pass | PR total/pass | push console/meta | PR console/meta | exit |
|---|---:|---:|---|---|---:|
| Mahjong.Ai.Tests | 444/444 | 444/444 | 444/444 | 444/444 | 0 |
| Mahjong.Architecture.Tests | 92/92 | 92/92 | 92/92 | 92/92 | 0 |
| Mahjong.Client.Tests | 182/182 | 182/182 | 182/182 | 182/182 | 0 |
| Mahjong.Client.Wss.Tests | 80/80 | 80/80 | 80/80 | 80/80 | 0 |
| Mahjong.Contracts.Tests | 149/149 | 149/149 | 149/149 | 149/149 | 0 |
| Mahjong.Domain.Tests | 529/529 | 529/529 | 529/529 | 529/529 | 0 |
| Mahjong.Infrastructure.Tests | 42/42 | 42/42 | 42/42 | 42/42 | 0 |
| Mahjong.LobbyService.Tests | 89/89 | 89/89 | 89/89 | 89/89 | 0 |
| Mahjong.TableRuntime.Tests | 400/400 | 400/400 | 400/400 | 400/400 | 0 |
| Mahjong.TableService.Tests | 152/152 | 152/152 | 152/152 | 152/152 | 0 |
| Mahjong.Unity.TableModel.Tests | 140/140 | 140/140 | 140/140 | 140/140 | 0 |
| **合计（按 XML 自行加总）** | **2,299/2,299** | **2,299/2,299** | 2,299 | 2,299 | 0 |

加总式：`80 + 400 + 444 + 92 + 182 + 140 + 149 + 89 + 529 + 152 + 42 = 2,299`。这不是从旧报告抄来的常数，而是本报告对两份 `direct-results.xml` 的 assembly `total` 求和后与 console/meta 交叉核对的结果。

### 5.2 XML SHA256（逐工程）

| Project | push direct-results.xml | PR direct-results.xml |
|---|---|---|
| Mahjong.Ai.Tests | `BEC855899AF61F79FBE7B8F072EC396E954E197BBA28AA4D9B6E088DD30C1C4F` | `73567048E6B9EC7C0AD35B57FD0D7C5AD3440095AF7FE78D569D72C0BE3C5E5C` |
| Mahjong.Architecture.Tests | `5E7698D62654532C0EDCD96307B4BD5E9AD715FF60F94F018928821C207B33FF` | `42DEE76CF7C3E1F985ABD5519AD416BBD9D9F65B872A2D4F1B084F4739C251A2` |
| Mahjong.Client.Tests | `3B6F138AAFFC836C578D95A9B3C90F1281DB34A16C090C0600E05DDAE6C0AFE4` | `AECA473D62A28E1C72ABDBA5C2E6FB86A772D840F755764F845D21EF3325D548` |
| Mahjong.Client.Wss.Tests | `444093DF65E163B1E2B7BF369C010D2E8CDEAB77D4A56AFBFE568E3F7B6BA9A8` | `8F4A8E0C8BFDEBF7A665B4FAB6C0868CC869A7EBEDF67B30DE332461C1CD7676` |
| Mahjong.Contracts.Tests | `09A9CAB54A94D7886DB3D51A980AEB949BDE6D42A8B21F831D4A6EB8EEE4D850` | `D77B17DE79A34DA56EB0063424E72C15D2EDDEE674BAC178ADCA397A9C0E4EAF` |
| Mahjong.Domain.Tests | `EE3E02306D5737C7E42C3624901C390D48EE9EBB0ED5E036F1494311E46C82B6` | `10D7B4B19356A17CB3A3133966C643934DF767DF963F10017CE322322A65967D` |
| Mahjong.Infrastructure.Tests | `97629561DF63FD297298194BA7011794AA578D3E556ECADE012EE1474AB89ED2` | `A5613EA72E50FA49487EDBEAB64166D8C8DE3E09DAFA5BAE55068A151185F788` |
| Mahjong.LobbyService.Tests | `6DC2C8339F49385746CCED7DFF8486EAFB81BF46B3F151EAFBB3D498E8BB50FC` | `8AFB0A8FF79D96F9A2158E1CD2D3E50630512C7EF5722F8DDE7B01A910BAF90D` |
| Mahjong.TableRuntime.Tests | `0EE46652E6AE0E5563C2097FBAFC8F1C864EEA2F00A62510FE4BCBC448F7E181` | `BF93328539DB5940FB9BC228F8B723D0174B068A9FD362D8B75D71CEE13A2CB7` |
| Mahjong.TableService.Tests | `4E1841D21BA3D28A71609C3041D5DF650E36D7C701BCBBAB49A160E6E23F5A16` | `E6EFA33FFD260954DD6A8EA7451F58B01F12BEAA771BE62EEF6EFD83DABCCA9F` |
| Mahjong.Unity.TableModel.Tests | `A5845080A631B655E796C62E78AC40785AE95AA9BC4EC0537B67E338AB51A528` | `39F43C0D990A2475783EE95778B06A45AF7CE2A031B6875A8615C684E6EBDF90` |

每个 SHA 同时与 `runs/<project>/direct.meta.txt` 的 `result xml sha256` 及 `unified/summary.log` 的 `direct xml ... sha256` 三方一致。

### 5.3 三方一致与完整性

- 22 个 XML（11 工程 × 2 run）全部通过生产结构性 gate `Read-DirectResultXml`：root `assemblies`、唯一 `<assembly>`、xUnit.net v3、必需计数属性、test 节点 ID 唯一、collection 聚合与节点数一致、assembly 计数等于节点计数。
- console 由生产 `Get-DirectRunnerCounts` 解析；22 份全部与 XML 的 Total/Passed/Failed/Errors/Skipped/NotRun 六项一致。
- meta 的 `total/failed/skipped/exit code/head/result xml sha256` 22 份全部与 XML/console 一致；`exit code` 全为 0，`head` 与各自 run 的实际 checkout 一致（push `5cc27b6`，PR `696338a`）。
- 框架统一为 `xUnit.net v3 4.0.2+0c7aa4991f`，目标 `.NETCoreApp,Version=v8.0`；所有 XML 的 `start-rtf <= finish-rtf`，且落在 run `created_at..updated_at` 窗口内。
- 工程集合恰好 11 个，无缺失/多余；不同工程之间没有重复 test 全名。2,299 个 test 节点有 2,298 个不同 display name，唯一碰撞是 `Mahjong.Architecture.Tests.DependencyDirectionTests.UnsupportedDeclarationFormsFailClosed` 的两个不同 ID（`dd4d7f78...` 与 `709c22b4...`，均 Pass），属于 xUnit 显示名截断 `···`，不是重复执行或遗漏。
- 每个工程 XML 的 `total` 等于其 test 节点数，且所有节点 `result=Pass`；无 skip/not-run/error 节点。

### 5.4 未记录的 DLL SHA

`direct.meta.txt` 不包含测试 DLL 的 SHA；artifact 也不包含 `bin/Release` 二进制。除 Infrastructure 外的 10 个工程，本报告只能确认 XML/console/meta 与构建 0/0，**不能确认每个测试 DLL 的字节身份**。这是证据缺口，不是 runner 失败：

```text
push: 10/11 NOT_RECORDED；PR: 10/11 NOT_RECORDED
已记录: Infrastructure push 04149C45... / PR 821878DC...
```

不得用当前树本地重建的 DLL 冒充远端 DLL；本次没有这样做。

## 6. PostgreSQL：每 run 的真实 16.10、42/42、13/13 与恢复

### 6.1 表级结果

| 项 | push | pull_request |
|---|---|---|
| PG 版本 / 端口 / DB | 16.10 / 55592 / `mahjong_p52_test` | 同左 |
| full XML | 42/42，fail/errors/skipped/notRun=0 | 42/42，同 0 |
| full XML SHA | `15BF83231F00FF86A4A34C123EFFA48A647F3E5CC15F54786B97D4B6B6F7F4CF` | `1E9F91E1535EF273651148FFB700AF3249A859A3DDC0EB84D2A57683ED3017AF` |
| full 时间 | `11:24:03.256`–`11:24:40.654` | `11:24:11.796`–`11:24:49.492` |
| targeted XML | 13/13，同 0 | 13/13，同 0 |
| targeted XML SHA | `06D4F56D3C4AAD5C5B308827ED483A4BE1A82D344E7B95E4F3BDF37A7236840A` | `C58C84764968D67D3C1B6759BD9794237CC224CE4FBA92D7828BAD8E8EBF5C62` |
| targeted 时间 | `11:24:40.997`–`11:24:46.702` | `11:24:49.827`–`11:24:56.033` |
| full console | `Total: 42, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0` | 同左 |
| targeted console | `Total: 13, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0` | 同左 |
| full 分类 | TableStore 13 + Supplement 5 + R1 10 + Checkpoint/Recovery 9 + Fault 3 + Schema/Security 2 | 同左 |
| targeted class | 全部 `PostgresTableStoreTests`，13 个唯一方法 | 同左 |

### 6.2 1999ms 修复后的两个新用例与严格断言

两个新用例均存在于当前头 `5cc27b6`，并在两个 run 的 42/42 中实际出现且 Pass：

```text
PostgresSupplementRegressionTests.EarlyWakePreparationDelay_CannotIssueScopeBeforeMinimumElapsed
PostgresSupplementRegressionTests.DelayAtLeast_IsCancellableBeforeTheFullMinimumElapses
```

主用例 `DelayedScopePreparation_EstablishesScopeAfterPreparationAndCommits` 也保留并 Pass。当前头源码（只读）仍保留：

```text
fixture.ScopePreparationElapsed >= preparation            // preparation = 2s
elapsed.Elapsed >= preparation + afterIssue                // 2s + 1.1s
fixture.AfterScopeIssueElapsed >= afterIssue               // 1.1s
fixture.Scope.IssuedAtUtcMs >=
    fixture.ScopePreparationCompletedAtUtcMs               // issued 不早于 preparation
```

`P52TestSupport.DelayAtLeastAsync` 仍以单调 `Stopwatch` 计算 `remaining = minimum - watch.Elapsed`，每次 await 不超过剩余下界，循环到真实下界满足；没有把 1999 放宽为允许值、没有把显示 ms 加 1、没有固定 sleep、没有删掉因果/过期/撤销断言。`EarlyWake...` 用前五次立即返回的受控提前唤醒，`DelayAtLeast...` 用 30s 下界 + 取消后迅速结束，方向与原修复一致。

源码文件（当前头）：

```text
dotnet/tests/Mahjong.Infrastructure.Tests/P52TestSupport.cs
  sha256 268C1FAF06D1014FE8B515CCE25C9E5B92870E960D7E4B39843894F9D08A4030
dotnet/tests/Mahjong.Infrastructure.Tests/PostgresSupplementRegressionTests.cs
  sha256 C94149984E18023376843DA2A2DA578D35981016273D978812A5BBBAA8ADC89B
```

### 6.3 强杀、restart、cold reader、空库 restore 的实值

| 项 | push | pull_request |
|---|---|---|
| crash writer pid | 7740 | 6500 |
| writer 强杀时间 | `2026-10-28T11:24:47.7977933Z` | `2026-10-28T11:24:57.1443154Z` |
| 强杀 native exit | -1（forced） | -1（forced） |
| marker committedAtUtcMs | `1793186687442` = `11:24:47.442Z` | `1793186696819` = `11:24:56.819Z` |
| marker stateVersion / lastEventSeq / eventCount | 1 / 2 / 2 | 1 / 2 / 2 |
| marker cheatVersion / fingerprint | 1 / `4660678438120261006` | 1 / `4660678438120261006` |
| marker outboxSha256 | `c0b108923986159032998a13a1946582cbe1d948375ccc3b2abce926b0aa2a55` | 同左 |
| postmaster before → after | `11:23:14.999309+00` → `11:24:48.191132+00` | `11:23:23.752301+00` → `11:24:57.534423+00` |
| PG immediate shutdown | `received immediate shutdown request` | 同左 |
| automatic recovery | `11:24:48.659` redo，`11:24:50.211` ready | `11:24:58.007` recovery，`11:24:59.630` ready |
| cold reader | recovered=true，stateVersion=1，lastEventSeq=2，eventCount=2，commandCount=1，receipt=Applied，terminal=true，outboxPresent=true，outboxPayloadMatches=true，outboxPublished=false，cheatStateRestored=true，cheatVersion=1，ownerEpoch=2 | 同左 |
| 空库 restore reader | events=2，recovered=true，receipt=Applied，ownerEpoch=3，outbox/cheat 同上 | 同左 |

`pg_dump` 为 custom-format，artifact 保留 `table_store.dump`（push 44,694 bytes / PR 44,700 bytes）；payload 按设计不以明文暴露，`PayloadKeyIsRequiredAndDatabaseBytesDoNotContainPlaintext` 在 42/42 中 Pass。

### 6.4 checkpoint / event / receipt / 136 / 账本 / hash：哪些有直接实值

有直接实值：

- event：`lastEventSeq=2`、`eventCount=2`、`commandCount=1`。
- receipt：`Applied`。
- outbox：存在、payload matches、未 published。
- cheat：`cheatVersion=1`、`cheatStateRestored=true`、`cheatFingerprint=4660678438120261006`。
- ownerEpoch：cold reader 2，restore reader 3。
- terminal：true。

**没有直接实值（NOT_RECORDED）**：

- 136 张 tile 计数；
- stateHash；
- checkpoint hash；
- ledger hash。

crash/restore reader JSON 不输出这些字段，因此本报告**不从总体 PASS 推断它们在 crash 路径上已被直接证明**。它们在独立的 42/42 用例中覆盖并通过，包括：

```text
Recovery_ValidRoundAndOutbox
Recovery_RejectsDuplicatedTileConservation
Recovery_RejectsTamperedStateFingerprint
Recovery_RejectsCorruptCheckpointHash
Recovery_RejectsEventGapInsteadOfSkipping
NonEmptyCheatLedgerAndFacts_WithCheckpointAndTail_RecoverStrictly
CorruptCheatLedgerOrFacts_FailsStrictRecovery
CheckpointAndTimingFences_RoundTripAndRejectStaleVersions
ActorCommit_RoundTripsRealEventsReceiptAndColdRecovery
```

这些是同一 42/42 run 内 pass 的专用 fixture，不是 crash reader 的字段。要关闭该证据缺口，需要让 crash/restore reader 额外输出 136 计数、stateHash、checkpoint hash、ledger hash；本轮未修改测试/reader。

### 6.5 真实 Lobby DB 与清理

| 项 | 实测 |
|---|---|
| Lobby PG | `PostgreSQL 16.10, compiled by Visual C++ build 1944, 64-bit` |
| 端口 / DB | `127.0.0.1:55593` / `mahjong_lobby_test` |
| postmaster started | push `2026-10-28 11:25:37.381772+00`；PR `2026-10-28 11:25:42.369298+00` |
| migration | `migrationRuns=3`，`lobbyTables=8`，两个 active 唯一索引 |
| ownership | owner marker `mahjong-p53-pg`，port 55593；cleanup `Deleted C:`，wrapper `rootDeleted=true` |
| 表集群清理 | `pg-cleanup-stop.log`：`server stopped`；wrapper `ownership-verified cluster removed` |

table 与 lobby 是两个独立 loopback 集群；本报告没有连接或操作 caller/其他 owner 的其它 PG 集群。

## 7. 历史失败与已接受负变体：保持原记录

### 7.1 `8a0c8cb` 的真实 1999ms 失败

保留，不重写：

- push [40665578400](https://github.com/liuchangready/majiang-game/actions/runs/40665578400) 与 PR [40665584740](https://github.com/liuchangready/majiang-game/actions/runs/40665584740)。
- 两份 XML 的唯一失败为 `DelayedScopePreparation_EstablishesScopeAfterPreparationAndCommits`，原始 message 为 `The fixture did not actually delay preparation: preparationMs=1999`。
- 该失败是真实失败，不是 flaky，也不是本次新头可以覆盖的历史记录。新头绿只证明修复后的 exact head 在这两个新 run 上通过。

### 7.2 D-PG-SUP-02

原始四失败根因仍 **UNRESOLVED**。本报告没有重做归因、没有重跑故障、没有抬 timeout、没有改生产代码。`V5-PG-SUPPLEMENT-REPAIR-DELIVERY-20261028.md` §9.9 与 §10 的“UNRESOLVED”保持不变。

### 7.3 已接受负变体

§10 记录的三个历史负变体仍只作为历史证据引用，本轮**没有重新执行**：

| 变体 | 历史结果 |
|---|---:|
| A guard-removed | 41/42，1 failed |
| B issued-before-preparation | 40/42，2 failed |
| C frozen DB clock | 40/42，2 failed |

不得把这三个数写成本轮新跑结果；它们的作用是说明守卫/因果/数据库时钟断言未被弱化。

## 8. 审计脚本、manifest 与检查计数

脚本：

```text
artifacts/ds-pr20-ci-5cc27b6-20261028-1950/audit.ps1
sha256 C0325E4B9DF833247F4B2D36F3594A1B4D713D4B72E58633E31D2C6BF99E5850
```

实际执行：

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File artifacts/ds-pr20-ci-5cc27b6-20261028-1950/audit.ps1
```

结果：

```text
checks=397  passed=373  failed=0  not_recorded=24  exit=0
```

脚本的 `git.head` 断言固定为冻结头 `5cc27b6`；本次在冻结头执行。报告提交后 HEAD 是报告子提交，若直接在该 HEAD 运行，`git.head` 会按设计报 FAIL（其余证据检查不变）。独立复验请在 `5cc27b6` 的 worktree 运行，或先确认该单项差异只是报告提交。

`not_recorded` 恰好是：10 个非 Infrastructure 工程的 DLL SHA × 2 run = 20；`@selected` 不存在 × 2；crash 路径 136/stateHash/ledgerHash 未记录 × 2。没有 FAIL。

审计实际做了：

- 逐 run 核对 API `head_sha/event/conclusion/run_attempt`、job、artifact id/size/digest；下载 ZIP 与 API digest 对算。
- 逐 project 解析 22 个原生 XML，使用生产 `Read-DirectResultXml` 的同一实现（从 `tools/verify-v5.ps1` AST 提取，不执行 verifier）；用生产 `Get-DirectRunnerCounts` 解析 console；再与 meta、summary.log 的 SHA/计数交叉核对。
- 逐文件解析 ZIP 242 条 entry 与解包文件长度；完整目录清单写入 `inventory/*-zip-listing.tsv`。
- 逐项复算 table manifest 139 条（0 mismatch、0 self-reference、0 missing），并确认 table 140 个文件中除 manifest 自身外无未列项。
- 解析 PG full/targeted XML、console、wrapper-summary、crash marker/reader/restore JSON、postmaster log、lobby evidence/migration/cleanup、native results。
- 生成不自引用 `inventory/sha256-manifest.tsv`：507 条，覆盖 507/508 文件，排除自身；逐项 SHA/长度复算 0 mismatch。

生产 XML gate 的 freshness 分支没有对回放 artifact 使用：ZIP 解包 mtime 被时区归一化为 `03:26Z`，与 CI 实际 `11:26Z` 相差 8 小时；使用它会把时间戳伪影当成 evidence 失败或需要改 mtime。报告改用 XML 内 `start-rtf/finish-rtf` + 内容 SHA + 生产结构性 gate，不改 verifier，也不伪造 mtime。

CI `run-info.txt` 的 verifier SHA 为 `2762B3936F1D382DB89446235C7B3B126C6E439F8C473DD64170FDA5352227EF`；当前树 LF 文件 SHA 为 `793FB63289B5EAD748E69535959858216D69538F32B21AA248E86F5820F86571`。把当前文件规范化为 CRLF 后 SHA 恰为 `2762B393...`，说明差异只是 Windows checkout 换行，不是 verifier 被改。该核验不改变“未修改 verifier”的结论。

关键证据 SHA256：

| 证据 | SHA256 |
|---|---|
| push ZIP | `E52C07E434AEA49BAD7A5BB4021A600117E70E4B11844CBCEC678F4A46FD306C` |
| PR ZIP | `0DFB80BC283434EE5FA801C9620CA7D7B41DD979BE0E46F0153516D715E203FD` |
| push job log | `F61AF096CE79F156B2DCA246D0BA51826DB089678D465F2ED4A4E0A9BE33FEF3` |
| PR job log | `2CD4C0602320C0301F8C8A3C526F37965952DA86406BF2EE13E1A0C6044DFA1D` |
| audit script | `C0325E4B9DF833247F4B2D36F3594A1B4D713D4B72E58633E31D2C6BF99E5850` |
| audit result | `443B617C5F7D1E695900052F0F633BED5FB32FC24C1FAE384315FE9CFE910CFC` |
| audit console | `009FC4B52E827A78D15003F33200A5B1451C8988AE64D95CA3EB1B15881CB224` |
| self-excluding manifest | `03C7713C87A168FFBCA936D8B565B4AC3EBA44D449BFF702FFBA72D76B4516C2` |

## 9. 逐项状态矩阵

| 项目 | 状态 | 依据 |
|---|---|---|
| 两个 run 均 completed/success（PR 为同 tree 的合成 merge） | **PASS** | API + job JSON |
| API `head_sha=5cc27b6` | **PASS** | 两 run API |
| push 实际 checkout=`5cc27b6` | **PASS** | job log + run-info + meta |
| PR 实际 checkout=`696338a`，双父/tree 与 `5cc27b6` 相等 | **PASS** | job log + API commit + `git merge-tree` + `git diff` |
| 原始 ZIP、digest、size、job log 三处一致 | **PASS** | 两个 ZIP 重算 |
| fresh Release 0 warnings/0 errors（tests/worker/sln） | **PASS** | 6 份 build log |
| 12-DLL 闭包 | **PASS** | plugin-copy.log |
| companion 4/4、47 cases | **PASS** | meta/log/fixture SHA |
| native exit 传播 6/6 | **PASS** | 两 run results.json |
| 11 个 direct runner 2,299/2,299 | **PASS** | XML 自行加总 + console/meta |
| 22 个 XML 结构、ID、collection、计数 | **PASS** | 生产 `Read-DirectResultXml` |
| XML/console/meta 三方一致 | **PASS** | 22 组 |
| PG 16.10 full 42/42 | **PASS** | XML/console/wrapper |
| PG targeted 13/13 | **PASS** | XML/console/wrapper |
| 两个新用例存在且 Pass | **PASS** | XML + 当前头源码 |
| 2s + 1.1s + issued 因果断言未弱化 | **PASS** | 当前头源码 |
| 强杀/非零退出、PG immediate restart、cold reader | **PASS** | marker/reader/pg log |
| 空库 restore、ownerEpoch 3 | **PASS** | restore reader |
| table/lobby 真实 DB、migration、ownership cleanup | **PASS** | wrapper/evidence/cleanup |
| crash 路径直接记录 136/stateHash/ledgerHash | **NOT_RECORDED** | reader JSON 无字段 |
| 10/11 runner 的测试 DLL SHA | **NOT_RECORDED** | meta/artifact 无字段 |
| runner `@selected` | **NOT_RECORDED** | xUnit XML 无该属性，使用 total |
| 历史 8a0c8cb 1999ms 失败被改写 | **否，保留** | §9.3/§10 |
| D-PG-SUP-02 根因关闭 | **否，UNRESOLVED** | §9.9/§10 |
| 负变体本轮重新执行 | **NOT_RUN** | 仅历史引用 |
| P4.2-B / P4.5 / G2 / G3 / 整场 | **NOT_RUN / 未批准** | 本报告范围外 |
| Unity / IL2CPP / 真机 / 跨物理网络 / iOS | **NOT_RUN** | 本报告范围外 |
| 181 classified | **仍覆盖不足** | G1 台账 |

没有 FAIL；没有把 NOT_RUN 写成 PASS；没有把 NOT_RECORDED 写成通过。

## 10. 证据缺口与补验命令

### 10.1 10/11 runner 的 DLL SHA

现状：artifact 只有 `direct.meta.txt` 与 XML，不含测试 DLL；除 Infrastructure 外没有 SHA。补验不能靠本地 rebuild 冒充远端。若必须关闭，需要下一次 CI 在运行前把每个测试 DLL 的 SHA 写入 `runs/<project>/direct.meta.txt`（或独立 JSON），再重新跑 exact head。**本轮没有改 verifier，也没有盲 retry CI。**

### 10.2 crash 路径的 136/stateHash/checkpoint/ledger

现状：crash/restore reader 只输出状态版本、事件数、回执、outbox、cheatVersion、ownerEpoch。136、stateHash、checkpoint hash、ledger hash 只在 42/42 的专用 fixture 中覆盖，不在 crash reader 的 JSON 中。若必须关闭，需要 reader 增加这些字段并重跑；本轮未改测试/reader。

### 10.3 `@selected`

runner XML 没有 `@selected`。本报告把 `total` 同时作为 selected，不能声称有独立的“选中数”证据。

### 10.4 历史失败与负变体

`8a0c8cb` 的 1999ms 失败、D-PG-SUP-02 四失败、三个负变体都只按历史记录引用，本轮未重新执行；补验命令应回到原报告 §9/§10 的失败 run 与隔离变体，不应改 verifier 或盲重试。

## 11. 本地收口

- 只提交本报告一个 tracked 文件；证据、ZIP、日志、审计脚本、manifest 全部在 ignored `artifacts/ds-pr20-ci-5cc27b6-20261028-1950/`。
- 未 push、未建 PR/MR、未 merge、未部署、未改 scheduler/`complete.flag`。
- 未调用 bridge 改派/转派/子任务；未操作其他线程/工作树/PG/Unity；未触碰 LocalHost PR21。
- 提交后要求：`git status --short` tracked clean；`git diff --check 5cc27b6..HEAD` exit 0；HEAD 为 `5cc27b6` 的直接子提交（报告不写自身 SHA，避免自引用）。
- 这两个 run 相对上一已接受头 `8a0c8cb` 只增加 D-CI-DELAY-01 的两个 test/fixture 文件与既有交付报告文档；没有新增/未接受的 provenance、持久 TableService、LocalHost 生产增量。PR20 其余内容仍按既有批次记录，不由本报告重新验收。

## 12. 明确不宣称

本报告不宣称 P4.2-B、P4.5、G2、G3、整场、Unity、IL2CPP、Android/iOS 真机、跨物理网络、PG 性能或 D-PG-SUP-02 已通过。它只确认：在 `5cc27b6` 的这两个远端 run 上，已取得的原始证据支持构建、11 个 direct runner、PG 16.10 42/42 与 targeted 13/13、companion 4/4、native exit 6/6 和恢复/清理链的本次 PASS，并如实保留上述 NOT_RECORDED 缺口与历史失败。
