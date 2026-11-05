# G2 最终头远端 CI 复核报告（2026-11-05）

## 结论与边界

两个已存在的远端 run 均以 attempt 1 / SUCCESS 结束；job logs 与 artifact 均已下载，artifact SHA-256 与 GitHub 返回 digest 相符，原件内容、计数矩阵及 manifest hash 已复算。远端 CI 执行证据为 PASS。

任务最终结论仍为 `BLOCKED / FINAL_REMOTE_CI_VERIFIED 未宣告`：最后一次成功的 PR 实时 REST 查询在 2026-11-05 09:13:05 UTC 记录 PR20 为 OPEN/Draft、head `59e8bd741385b5d7b39ccdbc47573dde552067d1`、base `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；09:26 UTC 的再次查询遇到 TLS/连接关闭，无法证明该时点后的 PR head。两个 run 完成后的 run 记录仍关联原 head/base，但不替代最后实时查询。

用户明确禁止继续委派，因此没有启动专属独立 reviewer；本报告是接收者对 GitHub 原件、job log、artifact 与 hash/计数矩阵的审计，不能标成独立复核。独立审查状态 `NOT_RUN`。本任务不批准 G2、C06、V5 合并或平台验收。

## 身份和 checkout

- 工作树：`E:/majiang-review/g2-final-remote-ci-luna6-20261105`，分支 `wt/v5-g2-final-remote-ci-luna6-20261105`。
- 派发时本地 HEAD：`6da3e72681242257bd8741eee11a54be0d62dc0a`；被审源码固定 `59e8bd741385b5d7b39ccdbc47573dde552067d1`。二者之间只有任务卡 docs-only 提交；审计期间没有修改业务、工具、workflow、测试、依赖或 Android 源码。
- PR20 在最后一次成功实时 API 响应中仍 OPEN/Draft，head branch `feat/v5-post-g1-batch`，head `59e8bd74…`，base branch `v5`，base `8fd7c6cb…`，`merge_commit_sha=0794af7f3dd601f4fc75154f92add6b96fcc40f6`。该 merge SHA 没有被假定为源码 SHA。
- workflow id `394054164`，路径 `.github/workflows/v5-verify.yml`。GitHub run API 未给出独立 workflow SHA 字段；日志中的 `GITHUB_REF`、实际 checkout 命令及 checkout 后提交用于辨认其实际事件 ref 与 checkout。

| Run | Event / attempt | API head SHA | job / 完成 UTC | 实际 checkout SHA | artifact |
|---|---|---|---|---|---|
| `41995915276` | `push` / 1 | `59e8bd741385b5d7b39ccdbc47573dde552067d1` | `verify` `127138901833` / 09:00:08 SUCCESS | `59e8bd741385b5d7b39ccdbc47573dde552067d1` | `v5-verify-logs-41995915276-1`，ID `13401696225`，4,791,397 bytes，2026-12-05 09:00:02 UTC 过期 |
| `41995924338` | `pull_request` / 1 | `59e8bd741385b5d7b39ccdbc47573dde552067d1` | `verify` `127138931940` / 08:59:25 SUCCESS | `0794af7f3dd601f4fc75154f92add6b96fcc40f6` | `v5-verify-logs-41995924338-1`，ID `13400998687`，4,789,685 bytes，2026-12-05 08:59:18 UTC 过期 |

Push checkout 日志实际 fetch/checkout `59e8bd74…`。PR checkout 日志实际 fetch `0794af7…` 到 `refs/remotes/pull/20/merge`，并记录 `HEAD is now at 0794af7 Merge 59e8bd74… into 8fd7c6c…`。合成 merge commit API 原件的 parents 顺序为 base `8fd7c6c…`、source `59e8bd7…`；PR artifact 的 `GITHUB_SHA`、`GITHUB_REF=refs/pull/20/merge` 和 summary head 也都记录 `0794af7…`。因此未以 run API `head_sha` 代替 PR job 的真实 checkout 身份。

最后一次成功 live PR 响应保存在证据目录 `finalPr.json`（文件时间 09:13:05 UTC）；最终再次读取在 09:26 UTC 遇到 `Invoke-WebRequest: underlying connection closed while sending`。`git ls-remote` 返回 128 / TLS `unexpected eof while reading`，网页读取为 cache miss。最新 head 在该失败点之后为 UNKNOWN；未猜测、未 rerun、未改 PR。

## Job gates 与真实验证结果

两个 run 的 step 1–9 均完成 SUCCESS，包含 checkout、.NET setup、native exit、direct XML gate、dotnetTest evidence gate、table evidence gate、统一 V5 验证和 always-upload artifact。原始 job log 中的四个自测矩阵如下（负例期望被拒绝，表格中的 `Pass=True` 表示行为与期望相符）：

| Gate | Run 41995915276 | Run 41995924338 |
|---|---:|---:|
| Native exit/redaction | 24/24 | 24/24 |
| Direct result XML | 19/19 | 19/19 |
| dotnetTest evidence | 61/61 | 61/61 |
| Table evidence XML/native | 82/82 | 82/82 |

Table gate 日志中的 `evidence seal FAIL: manifest path is a directory` 是 71-seal-failure 负例的预期拒绝；同一 step 最后给出 `82/82 table evidence cases behaved as expected`，job step 本身 SUCCESS，不是生产验证失败。

两个 artifact 的 `verify-v5/unified/summary.log`、逐工程 XML、direct console log、`direct.meta.txt` 及 native exit 已逐工程交叉核对。实际 runner mode 为 `direct`，14 个测试工程各自 XML 全部匹配控制台及 metadata，native exit 均为 0，test IDs 唯一，assembly 与 collection 计数一致；总计 2,872/2,872，failed/error/skipped/not-run 均为 0。该分母与历史 2,872 参考值相同。21 行 project summary 均 PASS（14 个 test 工程 + 7 个 build-only helper），0 FAIL / BLOCKED / NOT_RUN；solution build exit 0，插件 scratch copy 12 assemblies PASS，companion fixture 4/4 PASS。

| 测试工程 | Pass / Total |
|---|---:|
| Mahjong.Client.Wss.Tests | 82 / 82 |
| Mahjong.TableRuntime.Tests | 483 / 483 |
| Mahjong.Ai.Tests | 444 / 444 |
| Mahjong.Client.Tests | 182 / 182 |
| Mahjong.Architecture.Tests | 132 / 132 |
| Mahjong.Unity.TableModel.Tests | 236 / 236 |
| Mahjong.Domain.Tests | 529 / 529 |
| Mahjong.TableService.DurableHost.Tests | 59 / 59 |
| Mahjong.P5.4b.B1.HttpsAdmission.Tests | 54 / 54 |
| Mahjong.LobbyService.Tests | 89 / 89 |
| Mahjong.Contracts.Tests | 152 / 152 |
| Mahjong.Infrastructure.Tests | 42 / 42 |
| Mahjong.TableService.Tests | 204 / 204 |
| Mahjong.LocalHost.Tests | 184 / 184 |
| **合计** | **2,872 / 2,872** |

`verify-v5/table/tests.xml` 为 42/42；定向 `PostgresTableStoreTests` 的 `tests-targeted.xml` 为 13/13。两个 XML 的 assembly/collection/test node、`tests*.log` 控制台、native invocation metadata 与结构化 `tests-evidence.json` 全部一致：exit 0、errors/failed/skipped/notRun 0，test IDs 唯一。

实际 PostgreSQL 阶段包括：PG 16.10 启动；42/42 与 13/13；crash writer 在已提交 seq=2 后被预期终止（native exit -1）；restart recovery 记录 3 条恢复日志；crash reader 恢复终态/receipt/outbox；backup restore 到空 DB 后核对 events=2、receipts=1、outbox=1、tables=13。表和 lobby 两个 wrapper 均报告 cleanup PASS、`rootDeleted=true`、stop exit 0；lobby wrapper 记录 4 个 PG 环境变量恢复到调用前 absent，`restoredToPreCall=true`。这只是这两个隔离 CI runner 的证据，不外推为 Unity、Android、设备或公网部署通过。

## Artifact、manifest 与来源复核

原始完整 job log ZIP 与 artifact ZIP 各按 run/attempt 独占目录保存；没有覆盖旧证据。artifact ZIP 下载 SHA-256 与 GitHub artifact API digest 相同：

| Run | Artifact ZIP SHA-256 | job log ZIP SHA-256 |
|---|---|---|
| `41995915276` | `0B5C12879116A226A9D0E6D40200700980ACA6B5943D2D808B2CF87A34D563AC` | `490DC5B1B659FC92C958C7D0E0758FA13D4864B6E9726232267F5093A3AB75D2` |
| `41995924338` | `A22FE812994BB3C764ABB9F9C1207085FE164A100986DDF83037B5AD5D45D7EE` | `300C314B829E4B87D70B311E7116DA26BE4391BB8B206BF20D43EA8903881E8F` |

每个 artifact ZIP 都有 625 个条目，完整 inventory 及文件大小保存在各自 `artifact-inventory.json`。展开前检查了绝对路径、`..`、盘符、duplicate normalized paths 和 symbolic link；没有发现 zip-slip 项。首次普通 Windows 解包因一条 274 字符路径达到 MAX_PATH 而中止；该次 partial extract 未用于核验，完整副本改用证据根内短目录及 extended path 提取。原始 ZIP 未改动。

每个 `verify-v5/table/manifest.sha256.json` 有 222 项，按产物字节数和 SHA-256 全部复算通过，manifest 不含自身。`verify-v5/unified/pg/evidence-sha256.txt` 的 12 项（含 `root-logs/` 子目录）全部复算通过。14 个工程的 XML 与 metadata SHA 也逐项核对。

`git pins` 在两份 unified summary 中均明确为 `NOT_RUN`：历史 freeze blobs 未请求/拉取；summary 同时写明 current bytes 与嵌入 provenance 已离线验证。它不是 21 个 project rows 的 NOT_RUN，也没有被写成通过或被旧本地结果替代。该边界原样保留。

修复来源核对：`75d2782a` 修改 PostgreSQL cleanup 与 table evidence 自测脚本；`59e8bd74` 在其后继 commit 中仅调整证据文档行尾。任务卡记录的 `git diff source..dispatch` 仅含本任务卡。push job 直接执行 source；PR merge job 执行以 source 为第二 parent 的实际 merge。实际 step 7 运行 `tools/ci/test-v5-table-evidence.ps1` 并完成 82/82；统一 PG wrapper 的实际执行和 cleanup 结果见上述 artifact/log。历史失败 run `41971880599` / `41971887843` 保留为 FAIL，不以本次、本地或后续结果覆盖。

## Findings 与检查范围

本轮没有从新 run/artifact 中发现新的代码、workflow、假绿或计数缺陷；不提交 BLOCKER/MAJOR/MINOR 代码返修清单。已知 FU-02 UNKNOWN / SYNC-03 未解释继续保持 UNKNOWN；Unity/Windows/Android 新包绑定与 C06 不属于本任务。此次仅审远端原件与本地源码身份，没有本地 build、全量测试、Unity/设备或真实 PG 重跑，也没有修改生产源码。

审计辅助脚本 `build/e98c7d38-23e1-4c58-b662-410fb80199a8/audit-evidence.ps1` exit 0，生成 `audit-computed.json`：两份 artifact 的 XML/console/meta 矩阵一致；table counts/PG manifests/cleanup 与环境恢复断言均通过。稳定证据索引为同目录 `evidence-manifest.json`，原始 API 响应、完整 ZIP、log ZIP、inventory 与自算矩阵均在该证据根中。匿名下载曾因私有 Actions 内容返回 403/401；随后使用已配置 Git Credential Manager 在内存中请求，凭据未写入文件或输出。

## 收口状态

- CI 原件审计：`PASS`（两条已存在 run、两个 artifact 的内容和完整性均确认）。
- 最后一次成功 live PR 查询（09:13:05 UTC）：`OPEN/Draft`，head 正确；09:26 UTC 之后的 live head：`UNKNOWN/BLOCKED`。
- 独立 reviewer：`NOT_RUN`，遵循用户“不继续委派”要求；本报告不宣称独立 reviewer 已签署。
- 最终任务状态：`BLOCKED`，不写 `FINAL_REMOTE_CI_VERIFIED`，不据此批准 G2。
- 下一可验证接续点：恢复 GitHub API/remote TLS 后，仅查询 PR20 的当前 state/head/base 与现存 runs；若 head 仍为 `59e8bd741385b5d7b39ccdbc47573dde552067d1`，可使用已封存的两份 run/artifact 结果，无需 rerun。若 head 已移动，按新 SHA/run 重新绑定，不能复用旧结果认证新头。