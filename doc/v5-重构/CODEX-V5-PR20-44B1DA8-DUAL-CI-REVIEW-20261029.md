# V5 PR20 `44b1da8` 双 CI 原始证据独立审查（2026-10-29）

审查者：DeepSeek 第二槽（只读证据审查）
审查 HEAD：`c646b6e417a8d4cead2f8b642c9f3b166dcc0709`
受审 CI head：`44b1da88ab66bb73a873f41696cc5d0d82210216`（push run `40739411504` / PR run `40739420590`）
审查性质：**只读 gh/API、job/run log 端点、artifact 端点与当前树 git 对象；不修产品/CI，不重跑 PG/Unity/.NET，不接管或恢复其他线程。**

## 0. expectedFiles 与结论

本次唯一 tracked 变更：

```text
A doc/v5-重构/CODEX-V5-PR20-44B1DA8-DUAL-CI-REVIEW-20261029.md
```

结论分三层，不混用：

1. **失败归因/审查输入：PASS。** 两个 run 的 run/job/check-run/annotation 原始 JSON 均可取得并相互一致；两个 `verify` job 都**没有分配 runner**、**没有执行任何 step**，唯一 failure annotation 明确为 GitHub Actions 账户计费/额度问题。该结论不是从 `conclusion=failure` 猜测，也不是把“日志取不到”称为 flaky。
2. **CI 结论：FAIL。** 失败发生在测试/构建之前，属于账户/环境阻塞；`0` 个 step、`0` 个测试进程、`0` 个测试用例被执行。它既不证明产品有缺陷，也不证明产品无缺陷。
3. **下游测试/产物审查：REVIEW_INCOMPLETE（NOT_RUN / NOT_RECORDED）。** 本次 run 没有 build、12-DLL 闭包、11 direct runner XML、PG full/targeted、writer 强杀/重启/coldreader/空库恢复、cleanup/envrestore、统一 verifier 退出码，也没有 artifact、digest、ZIP 清单或上传 manifest。按“失败于测试前则后续 NOT_RUN”处理，不补造任何计数。

历史口径保持：D-PG-SUP-02 根因仍未关闭；旧 Client 失败不被本次结果销账；前一 `02f22e3` 双 run `40735572667`/`40735580712` 是同一计费阻塞的对照；`8c8c792` PR run `40734448875` success 仅作历史对照，不替代当前失败，也不用于宣称当前 head 已通过。

冻结上下文：G1 仍 APPROVED；G2–G5 未批准；V4 仍是唯一生产基线；181 classified 缺口未覆盖；PR20 仍 `open`、`merged=false`。本轮未 push、未建 PR/MR、未合并、未部署、未操作 scheduler/`complete.flag`。

## 1. 工作树、分支与冻结

| 项 | 实测值 |
|---|---|
| 工作树 | `D:/majiang-wt/ci-44b1-audit-ds-20261029-0220` |
| 分支 | `wt/v5-ci-44b1-audit-ds-20261029-0220` |
| `git rev-parse HEAD` | `c646b6e417a8d4cead2f8b642c9f3b166dcc0709` |
| HEAD tree | `cbf261a60c1616c27b2c33e300282418655a62a7` |
| `origin/v5` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| `origin/v5..HEAD` / `HEAD..origin/v5` | 150 / 0 |
| `git merge-base --is-ancestor 8fd7c6c HEAD` | exit 0 |
| `git merge-base --is-ancestor 44b1da8 HEAD` | exit 0 |
| tracked status（写报告前） | 空 |
| 报告父提交 | `c646b6e417a8d4cead2f8b642c9f3b166dcc0709` |

`c646b6e` 是受审 CI head `44b1da8` 的后代，包含已接受的 LocalHost 文档收口；**它不是本次 CI 的 head**。本次报告不把 `c646b6e` 的树当作 `44b1da8` run 的实际 checkout，也不拿报告子提交去追新 CI。

## 2. 两个 run：身份、job 与真实状态

| 项 | push | pull_request |
|---|---|---|
| Run ID / URL | `40739411504` / https://github.com/liuchangready/majiang-game/actions/runs/40739411504 | `40739420590` / https://github.com/liuchangready/majiang-game/actions/runs/40739420590 |
| `run_number` | 233 | 234 |
| `check_suite_id` | 110428408867 | 110428434753 |
| Workflow | `.github/workflows/v5-verify.yml`，`workflow_id=394054164` | 同左 |
| Event / attempt | `push` / `1` | `pull_request` / `1` |
| Status / conclusion | `completed` / `failure` | `completed` / `failure` |
| `head_branch` / `head_sha` | `feat/v5-post-g1-batch` / `44b1da88ab66bb73a873f41696cc5d0d82210216` | 同左 |
| `head_commit.tree_id` | `848dbd69ece9918c02b122255740c3026a9430de` | 同左 |
| Created / updated（UTC） | `2026-10-28T17:49:52Z` / `17:49:55Z` | `2026-10-28T17:49:55Z` / `17:49:59Z` |
| Job ID / name | `122996625941` / `verify` | `122996661051` / `verify` |
| Job URL | https://github.com/liuchangready/majiang-game/actions/runs/40739411504/job/122996625941 | https://github.com/liuchangready/majiang-game/actions/runs/40739420590/job/122996661051 |
| Runner label | `windows-latest`（请求标签） | 同左 |
| `runner_id` / `runner_name` | `0` / 空 | `0` / 空 |
| `steps` | `null`（gh 视图 `0`） | `null`（gh 视图 `0`） |
| Job started / completed（UTC） | `17:49:52Z` / `17:49:54Z` | `17:49:56Z` / `17:49:59Z` |
| Job conclusion | `failure` | `failure` |
| Annotations | `1` 条，failure | `1` 条，failure |
| Artifacts（API） | `total_count=0` | `total_count=0` |
| PR 关联 | PR20；base `8fd7c6c`；API 当前 `pull_requests[0].head.sha=689391d` | 同左 |
| 实际 checkout ref | **NOT_RECORDED**（job 未启动，无 checkout 日志） | **NOT_RECORDED** |

必须区分两个“head”：

- run 的权威 `head_sha` 是 `44b1da8`，两个 run 相同，tree 均为 `848dbd6`。
- API 的 `pull_requests[0].head.sha` 现在是 `689391d`，这是 PR 在 run 之后继续前进后的**当前** PR 头；它不是本 run 的 `head_sha`，也没有被本 run checkout。
- 本 run 的 push 预期 ref 是 `refs/heads/feat/v5-post-g1-batch`，PR 预期 ref 是 `refs/pull/20/merge`；因 job 未启动，二者都只有事件语义，**没有实际 checkout 证据**。

## 3. 原始证据获取、完整性与不可得项

### 3.1 命令与 native exit

完整命令/UTC/exit 台账在 `audit/commands.tsv`（44 行，SHA256 见 §3.3）。关键命令：

| UTC（2026-10-28） | 命令 | native exit | 结果 |
|---|---|---|---|
| 18:47:10 / 18:47:11 | `gh api .../actions/runs/40739411504`、`.../40739420590` | 0 / 0 | 两个 run JSON 保存 |
| 18:47:19 / 18:47:20 | `gh api .../runs/<id>/jobs?per_page=100` | 0 / 0 | 各 1 个 job；`steps=null`、`runner_id=0` |
| 18:47:21 / 18:47:22 | `gh api .../runs/<id>/artifacts?per_page=100` | 0 / 0 | 各 `total_count=0` |
| 18:47:23 | `gh api .../pulls/20` | 0 | PR20 `open/merged=false` |
| 18:47:24 | `gh api .../commits/44b1da8` | 0 | 父 `02f22e3`，tree `848dbd6` |
| 18:47:44 / 18:47:45 | `gh api .../check-runs/122996625941`、`.../122996661051` | 0 / 0 | 两个 check run，均 failure |
| 18:47:46 / 18:47:47 | `gh api .../check-runs/<id>/annotations` | 0 / 0 | 各 1 条计费 annotation |
| 18:59:35 / 18:59:38 | `gh run view 40739411504 --log-failed`、`... 40739420590 --log-failed` | 1 / 1 | `log not found: 122996625941` / `122996661051` |
| 18:59:41 / 18:59:42 | `gh run download 40739411504`、`... 40739420590` | 1 / 1 | `no valid artifacts found to download` |
| 18:59:48 / 18:59:50 | `gh api --include .../actions/jobs/122996625941/logs`、`.../122996661051/logs` | 1 / 1 | HTTP 404，Azure `BlobNotFound` |
| 18:59:52 / 18:59:54 | `gh api --include .../actions/runs/40739411504/logs`、`.../40739420590/logs` | 0 / 0 | HTTP 200，22-byte 空 ZIP |
| 19:00:08 / 19:00:10 | `Invoke-WebRequest` 下载上述两个 run logs ZIP | 0 / 0 | HTTP 200；各 22 bytes、0 entries |
| 19:00:20–19:00:27 | `gh api` 读取 `40735572667`/`40735580712`/`40734448875` 及 jobs/artifacts | 0 | 对照 run |
| 19:00:33 / 19:00:34 | `gh api .../check-runs/122983052436/annotations`、`.../122983077556/annotations` | 0 / 0 | 同样计费 annotation |
| 19:00:52 / 19:00:54 | `gh api .../commits/689391d`、`.../commits/8605258` | 0 / 0 | 当前 PR 头 / 当前 PR test merge |
| 19:02:17 / 19:02:19 | `gh run view <id> --json headSha,event,attempt,status,conclusion,jobs` | 0 / 0 | 与 API 一致 |
| 19:05:22 | `gh api .../commits/44b1da8/check-runs?per_page=100` | 0 | `total_count=2`，只有上述两个 `verify` |
| 19:02:51–19:02:53 | `git merge-tree --write-tree`、ZIP entry 读取等探针 | 0（修正后） | 见 §7；首次包装器的 -1/128 是 PowerShell 解析 `^{tree}` 的审计工具问题，已在 `audit/probe-exits-corrected.txt` 说明 |

### 3.2 两个 run 的 artifact / log 不可得项

- **Artifact REST**：两个 `/artifacts` 都是 `{"total_count":0,"artifacts":[]}`；`gh run download` 两个都 exit 1，原文 `no valid artifacts found to download`。因此没有 ZIP、没有 API `digest`、没有上传清单、没有 manifest，也没有可解包/逐项哈希的对象。**这是 NOT_RECORDED，不是取不到后的静默丢失。**
- **Job log**：两个 job log API 都返回 HTTP 404 `BlobNotFound`（Azure XML 头 `x-ms-error-code: BlobNotFound`）。`gh run view --log-failed` 两个都 exit 1，原文 `log not found: <job id>`；与 caller 的 `122996661051` 结果一致。
- **Run log**：两个 run-level logs 端点都返回 HTTP 200，`Content-Type: application/zip`、`Content-Length: 22`；下载后是**合法空 ZIP**，0 entries，两者 SHA256 相同。空 ZIP 不包含任何 log，不能当作“日志已取得”。
- **结论**：没有伪造日志、没有用 API `conclusion` 猜单测失败、没有把缺失日志称为 flaky。失败原因由 check-run annotation 直接给出，见 §5。

### 3.3 证据索引与本报告 manifest

证据根（ignored，独占 GUID）：

```text
D:/majiang-wt/ci-44b1-audit-ds-20261029-0220/artifacts/ds-44b1da8-f3aa358c194648b4b7b09f9478cf006b/
```

关键原始证据（完整 64 hex SHA256）：

| 证据 | 路径 | 字节 | SHA256 |
|---|---|---:|---|
| push run JSON | `meta/run-40739411504.json` | 13,386 | `674B4B389C6DC7C8757F7B0197DC160955861D00FAA265092D657CD442C19FCC` |
| PR run JSON | `meta/run-40739420590.json` | 13,398 | `4C6FD6D91F72B7403A4622189D2AB407E8AE0B91853BC6249A8AF42DF89213A6` |
| push jobs JSON | `meta/run-40739411504-jobs.json` | 911 | `2481BB145E6C9309B761A9BA2952F84344A6766CD00EDCA1DBC184AB6B80E114` |
| PR jobs JSON | `meta/run-40739420590-jobs.json` | 911 | `7365D6C6B842931E779FF63DC5BC92706E764A35B9353F88F3BFC2D8E9AFCCEC` |
| push artifacts JSON | `meta/run-40739411504-artifacts.json` | 37 | `3C29B3D9EE293F8BB9C334939FF81B109E20A2136C790DF3CEDAFA67C56E20C6` |
| PR artifacts JSON | `meta/run-40739420590-artifacts.json` | 37 | `3C29B3D9EE293F8BB9C334939FF81B109E20A2136C790DF3CEDAFA67C56E20C6` |
| push check-run JSON | `meta/checkrun-122996625941.json` | 3,566 | `AD86A3D2AAC4954B98B877B20750D651A0510751C0BDA440D970A59F5E484845` |
| PR check-run JSON | `meta/checkrun-122996661051.json` | 3,566 | `1D84FFB46E9FF412E108F71C0273AE56BC9192475A3561A585D02152F919C194` |
| push annotation JSON | `meta/annotations-122996625941.json` | 448 | `A99893E64D4A59C42AC78888F622BF7E22D2E7ED1C33F5A8D40F9D572D786E9E` |
| PR annotation JSON | `meta/annotations-122996661051.json` | 448 | `A99893E64D4A59C42AC78888F622BF7E22D2E7ED1C33F5A8D40F9D572D786E9E` |
| push job-log 404 原文 | `logs/api-job-logs-122996625941.txt` | 1,027 | `01CB5DF9EBF2A3D74A541D930AD0853DA9B6E2901E021A91C2ACA04A374BB0CE` |
| PR job-log 404 原文 | `logs/api-job-logs-122996661051.txt` | 1,027 | `7A1FA847FD804D0AB96FDE0B1BB4260789BE12C3DD8EA58534316935AC85404D` |
| push run logs ZIP（空） | `logs/run-40739411504.logs.zip` | 22 | `8739C76E681F900923B900C9DF0EF75CF421D39CABB54650C4B9AD19B6A76D85` |
| PR run logs ZIP（空） | `logs/run-40739420590.logs.zip` | 22 | 同上 |
| PR20 当前 API | `meta/pull-20.json` | 42,688 | `0FA3BC7FCDCC17E4D8DB820A501AF5B4B9AF8B7D20C4783AB605340374E09BA2` |
| `44b1da8` commit API | `meta/commit-44b1da8.json` | 9,745 | `1DE393642922BE4E5AD7C9ABD04B935A40C5F42745465BBBE09D5E3A3DE8F0D4` |
| `689391d` commit API | `meta/commit-689391d.json` | 7,367 | `8AC3F3B3E8019BDBAA8FC0CB8BE509C0C3D306D29D97B101D62C0E869F9EC013` |
| `8605258` commit API | `meta/commit-8605258.json` | 1,267,273 | `2AB1DAE85E665D2DC8B9BF5623CCDEDA6BB66CE36C8AEA9D580D8B99A0353812` |
| `44b1da8` all check-runs | `meta/commit-44b1da8-check-runs.json` | 7,161 | `9C12A86D6E61C5741C10F369244D550C3FDA2A3E0C57251DDA97AFA3697DA33C` |
| 命令台账 | `audit/commands.tsv` | 6,792 | `DCC77977473F169B1A50271DEF0A734EFE69DAE04E4CE40D99562D2EE210612E` |
| 当前 workflow 源文件 | `.github/workflows/v5-verify.yml` | 3,573 | `B975D722C84EF5BF87F9215F26EF6F8A41A5E17D1C8CCBEBFC8FB5EEFE818A0A` |
| 当前 verifier 源文件 | `tools/ci/verify-v5-table-postgres.ps1` | 38,961 | `2CC3917CFD48678A606D3D52BC11BAAFE9ACB58E1170F0EEC106E29520F8A7F0` |
| 统一入口源文件 | `tools/verify-v5.ps1` | 75,249 | `793FB63289B5EAD748E69535959858216D69538F32B21AA248E86F5820F86571` |

`commands.tsv` 为 44 行、6,792 bytes；其完整 SHA256 为 `DCC77977473F169B1A50271DEF0A734EFE69DAE04E4CE40D99562D2EE210612E`。

**不自引用 manifest**：

```text
inventory/sha256-manifest.tsv
entries=54
bytes=5,602
sha256=24A4C14A09B90FF58FF648BBE0942905E969CC7F6E42B99551E27E4C649D932D
rehash: checked=54 mismatch=0
```

manifest 覆盖 54 个证据文件；manifest 自身与 `inventory/rehash-result.txt` 不列入自身。证据根共 56 个文件（54 + manifest + rehash-result）。逐行重哈希使用每行记录的完整 SHA256 与实际文件长度，**0 mismatch**；`rehash-result.txt` 记录了同一 manifest SHA。凭据/私钥/payload 扫描：`gho_`、`github_pat_`、`-----BEGIN ... PRIVATE KEY-----` 命中数均为 `0`；本报告不包含 token、私钥或 artifact payload。

## 4. 逐 run 下游证据：全部 NOT_RUN

| 应有证据 | push `40739411504` | PR `40739420590` | 说明 |
|---|---|---|---|
| Job 执行 | FAIL（计费，见 §5） | FAIL（计费，见 §5） | 未分配 runner |
| Checkout | NOT_RUN | NOT_RUN | `steps=null` |
| fresh Release tests/worker/sln build | NOT_RUN | NOT_RUN | 无 build log |
| 12-DLL 闭包 | NOT_RUN | NOT_RUN | 无 plugin-copy 产物 |
| companion 4/4 / 47 cases | NOT_RUN | NOT_RUN | 无 fixture 输出 |
| native exit 传播 6/6 | NOT_RUN | NOT_RUN | 无 `native-exit-*` artifact |
| 11 direct runner 原生 XML | NOT_RUN | NOT_RUN | 0 个 XML；0 个用例执行 |
| XML/console/meta 三方一致 | NOT_RUN | NOT_RUN | 无对象可比 |
| PG full / targeted | NOT_RUN | NOT_RUN | 未启动任何 PG |
| writer 强杀 / restart / cold reader / 空库恢复 | NOT_RUN | NOT_RUN | 无 marker/reader/restore JSON |
| cleanup / envrestore | NOT_RUN | NOT_RUN | 无 wrapper/cleanup 产物 |
| 统一 verifier 退出码 | NOT_RECORDED | NOT_RECORDED | verifier 未启动 |
| 上传 artifact / digest / ZIP entry | NOT_RECORDED | NOT_RECORDED | API `total_count=0` |
| artifact manifest 长度/SHA | NOT_RECORDED | NOT_RECORDED | 无 manifest |
| `selected` / crashworker 路径/产物/原退出 | NOT_RECORDED | NOT_RECORDED | 无发生 |
| test total / passed / failed / skipped / notRun | 0 executed；计数不存在 | 0 executed；计数不存在 | 不得写成 0/0/0 的“通过” |

静态源码旁证（不是执行结果）：当前 `dotnet/tests` 下有 11 个测试工程、2 个 CrashWorker、1 个 PostgresProbe；本报告不把它们折算成执行数，也不复用历史 `2,299/42/13`。本 run 的真实执行数是 **0 个 step、0 个测试进程、0 个测试用例**；`failed/skipped/notRun` 均为 `NOT_RUN`，不是 0 通过。

## 5. 首个实质失败与全部同次已知失败

### 5.1 两个目标 run 的失败

| ID | F1（push） | F2（PR） |
|---|---|---|
| Run / job | `40739411504` / `122996625941` | `40739420590` / `122996661051` |
| test/step | **无**（job `verify` 未启动，`steps=null`） | **无**（同上） |
| 原命令 | 未执行任何 shell step；workflow 预期命令见下 | 同左 |
| Actual | `runner_id=0`、`runner_name=""`、`steps=null`、`conclusion=failure`、annotation 1 条 | 同左 |
| Expected | 分配 `windows-latest` runner，进入 Checkout/Setup/Verify/Upload 步骤 | 同左 |
| Native exit | `NOT_RECORDED`（无进程可退出） | `NOT_RECORDED` |
| 原文件/位置 | check-run annotation：`path=".github"`、`start_line=1`、`annotation_level=failure` | 同左 |
| Annotation 原文 | `The job was not started because recent account payments have failed or your spending limit needs to be increased. Please check the 'Billing & plans' section in your settings` | 同左 |
| 证据 SHA256 | annotation `A99893E6...786E9E`；check-run `AD86A3D2...484845` | annotation `A99893E6...786E9E`；check-run `1D84FFB4...C194` |

workflow 预期但**未执行**的命令（`.github/workflows/v5-verify.yml` 第 55–61 行）：

```powershell
./tools/ci/test-v5-native-exit.ps1
./tools/ci/verify-v5-table-postgres.ps1 -Configuration Release -RunUnifiedVerification `
  -LogDir artifacts/verify-v5/table -UnifiedLogDir artifacts/verify-v5/unified
```

这两条命令的 native exit 是 `NOT_RUN`，不能写成 0 或非 0；`test-v5-native-exit.ps1` 的 6/6 也不存在。

### 5.2 全部同次已知失败

- 这两个 run 各只有 1 个 job、1 条 annotation、0 个 step；`44b1da8` 的 check-runs 总数为 `2`，恰好就是上述两个 `verify`，没有第三个 job/check-run。
- 因此同次已知失败就是 F1、F2 两个 **job 级**失败，没有“第一条之外还有单测失败”可列；也没有任何 test 失败、step 失败、skip 或 not-run 计数。
- 前一 `02f22e3` 对照：push job `122983052436`（17:25:25–17:25:27Z）与 PR job `122983077556`（17:25:28–17:25:30Z）同样 `runner_id=0`、`steps=null`（gh 视图 0），annotation 原文**逐字相同**。这是同一环境阻塞的重复证据，不是新的产品失败。
- `8c8c792` PR run `40734448875` 是历史对照：`completed/success`，job `122979099206` 有 `runner_id=1000000229`、9 个 step、1 个 artifact（`v5-verify-logs-40734448875-1`，4,304,638 bytes，digest `sha256:c4658b972d5924784ca949d407c1ad64402c0ac6d453fafa02cdccc22241dcf4`）。它证明同一 workflow/verifier 在此前可运行，但**不替代** 44b1 的失败，也不被本报告下载/重算为本次证据。

### 5.3 分类

- **环境/账户（GitHub Actions billing/spending limit）**，不是 runner 镜像故障（runner 从未分配），不是产品/测试失败，不是 workflow/verifier 内容错误，也不是证据归档代码错误。
- upload step 的 `if: always()` 无法在“job 未启动”时执行；因此没有 artifact 是计费阻塞的必然后果，不能倒推为 upload 逻辑缺陷。
- caller 的 `gh run view 40739420590 --log-failed` exit 1（`log not found:122996661051`）已复现：job log blob 确实 404，run-level logs 是 22-byte 空 ZIP。**不是 flaky，也不是“日志暂未生成”**；是 job 从未启动。

### 5.4 时间线与“verify HEAD”问题

- `8c8c792` job 于 17:18:33Z 启动、17:26:58Z 成功。
- `02f22e3` push 于 17:25:24Z 创建，17:25:27Z 已失败；此时前一成功 job 尚未结束。
- `44b1da8` push 于 17:49:52Z 创建，17:49:54Z 失败；PR 于 17:49:56–17:49:59Z 失败。
- 这与账户在 17:18–17:25Z 之间触发额度/计费限制的时间线一致；直接原因仍以 annotation 为准，不靠时间线推断。

**不存在“仅文档差异被 verify HEAD 严格拒绝”的情况**：job 没有 checkout、没有执行任何 step，workflow 中也没有独立的 verify-HEAD step；因此不能把失败归因于文档差异，也不存在“放宽 verifier 就能过”的检查。任何后续修复都必须是账户/额度层面，不是代码或 verifier 放宽。

## 6. PR 关联、merge SHA、双父与 tree

| 对象 | SHA | 父 | tree | 证据 |
|---|---|---|---|---|
| 受审 CI head `44b1da8` | `44b1da88ab66bb73a873f41696cc5d0d82210216` | `02f22e30842544812eb5bd3e24dd63bd2023d7ee` | `848dbd69ece9918c02b122255740c3026a9430de` | 本地 `git cat-file` + API commit |
| 当前 PR 头 `689391d` | `689391d6f755eede805fdef8ff945caf9db6f773` | `c646b6e417a8d4cead2f8b642c9f3b166dcc0709` | `ec8dcbd9dbba9cbd72694ce3fa6cab48eb2563d3` | API commit |
| 当前 PR test merge `8605258` | `860525895fb8f0e3664c38b3570b666d3901b9dc` | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` + `689391d6f755eede805fdef8ff945caf9db6f773` | `ec8dcbd9dbba9cbd72694ce3fa6cab48eb2563d3` | API commit |

关键结论：

- `8605258` 的父是 `8fd7c6c` 与 `689391d`，tree 与 `689391d` 相同；它是**当前** PR 头 `689391d` 的 GitHub test merge，不是 44b1 run 的 merge。
- 44b1 run 的 PR test-merge commit SHA **NOT_RECORDED**：job 未启动，没有 checkout 日志，GitHub 也没有为未启动的 run 保留可查询的合成提交。不能用 `8605258` 冒充。
- 独立 `git merge-tree --write-tree 8fd7c6c 44b1da8` 输出 `848dbd69ece9918c02b122255740c3026a9430de`，与 44b1 tree 相同。这证明“若做 test merge，tree 应为 848dbd6”，但它是**计算出的 tree**，不是已记录的 merge commit；报告不把它写成 pull merge SHA。
- API 的 `pull_requests[0].head.sha=689391d` 是 PR 当前头，不是本 run 的 checkout；本 run 的权威 head 仍是 `44b1da8`。

## 7. 独立探针与不变量

只读、无产品/CI 修改；所有探针在本树 ignored 证据目录，不连接/杀共享 PG，不启动 Unity/重型套件：

1. **workflow/verifier 内容身份**：`8c8c792`（success）、`02f22e3`（blocked）、`44b1da8`（blocked）三者的 workflow blob 均为 `9668462bb88a1982c20fbb8fb5fa2b558f6e97ca`，verifier blob 均为 `809154f0f8427637784008e9dcc51e207f908ce7`。同一 workflow/verifier 在 17:18Z 成功、17:25Z 与 17:49Z 被计费阻塞，排除“workflow YAML/verifier 内容在 44b1 被改坏”的解释。当前工作树文件 SHA256 见 §3.3（blob id 是 git 内容 id，不是文件 SHA256，二者不混用）。
2. **git 祖先与 tree**：`git merge-base --is-ancestor 8fd7c6c HEAD` exit 0；`44b1da8` 是 HEAD 祖先 exit 0；`git merge-tree` 输出 848dbd6，与 44b1 tree 相同。
3. **空 ZIP**：两个 run logs ZIP 均 22 bytes、0 entries；用 `System.IO.Compression.ZipFile` 实际打开，无异常。没有把空 ZIP 当日志。
4. **manifest 重哈希**：54 条逐行重算，0 mismatch；manifest SHA `24A4C14A...932D`；总文件 56。
5. **没有重放 verifier**：因为没有 artifact/XML/PG 产物；重放 accepted verifier 需要原始 artifact，而本 run 的 artifact 不存在。这里不是“拒绝重放”，而是 `NOT_RUN`。本轮未跑 PG/Unity/.NET 全量，未改 tracked，未改原产物。
6. 探针包装器首次用 PowerShell 的 `^{tree}` 写法导致 `commands.tsv` 记录 -1/128；这是审计工具解析问题，不是 git/CI 失败。已用 `git cat-file` 重新生成干净输出，并在 `audit/probe-exits-corrected.txt` 说明；报告中的 `git merge-tree` exit=0 以修正后输出为准。

## 8. 全维度核对

| 维度 | 结论 | 依据 |
|---|---|---|
| 范围/expectedFiles | 本报告只新增 1 个 tracked 文档；未修改产品/CI/其他树 | `git status`；§1；§11 |
| 44b1 提交自身范围 | 44b1 自身仅 1 个文档、59 行；其 tree 相对 `5cc27b6` 包含 21 个文件（11 文档 + 1 测试 + 4 个 `tools/unity` 脚本 + 5 个 Unity Editor 资源） | `git show --stat 44b1`；`git diff --name-status 5cc27b6..44b1`；`probes/scope-44b1.txt` |
| 假绿/误 PASS | 无 skip、无空测试、无旧产物、无 `Total=0` 被当通过；本报告不宣称任何测试 PASS | 0 XML、0 artifact、0 step |
| 计数 | 0 step / 0 测试进程 / 0 用例执行；`failed/skipped/notRun` 均 NOT_RUN | §4；不硬编码 2,299/42/13 |
| 并发/幂等/恢复 | NOT_RUN：无 PG、无 writer、无 cold reader、无 outbox/receipt 证据 | §4；未连接共享 PG |
| 安全/隐私 | 无 artifact/log payload；无 token/私钥命中；job 未 checkout，runner 未处理仓库代码；API 元数据不含凭据 | secret scan 0；空 ZIP；§3 |
| 环境/平台 | 账户计费/额度阻塞；`windows-latest` 未分配（`runner_id=0`），不是 runner 镜像问题；Android/iOS/真机/跨物理网络均 NOT_RUN | §5；§12 |
| 文档口径 | 44b1 commit message 是 docs，但其 tree 含 provenance 代码/测试；本报告按 tree 审，不把“docs commit”误写成“只测文档”；无 verify HEAD 拒绝 | §5.4；§6；§8 范围行 |
| 历史 | D-PG-SUP-02 仍 UNRESOLVED；旧 Client 失败未销账；02f22e3 同阻塞；8c8c792 仅历史对照 | §0；§5.2 |

## 9. 结论矩阵（PASS / FAIL / BLOCKED / NOT_RECORDED / NOT_RUN 分开）

| 项目 | 状态 | 依据 |
|---|---|---|
| 两个 run 的 ID/event/workflow/attempt/head/tree 元数据 | **PASS** | API run JSON + gh view |
| 两个 job 均 `completed/failure`、`runner_id=0`、`steps=null` | **PASS** | jobs API/check-run |
| 唯一 failure annotation 为账户计费/额度 | **PASS** | 两个 annotation JSON，逐字一致 |
| 两个 run 的 conclusion = FAIL | **FAIL** | API |
| 44b1 run 实际 checkout ref / PR test-merge SHA | **NOT_RECORDED** | job 未启动，无 checkout/log |
| 当前 PR `8605258` 的双父/tree（属于 689391d） | **PASS** | API commit；明确不用于 44b1 |
| workflow/verifier 内容在 8c8c792/02f22e3/44b1 相同 | **PASS** | git blob id |
| job log 获取 | **BLOCKED** | HTTP 404 BlobNotFound；gh exit 1 |
| job log 内容 | **NOT_RECORDED** | 无 blob，不能伪造 |
| run-level log | **NOT_RECORDED** | HTTP 200 但 22-byte 空 ZIP |
| artifact/digest/ZIP/manifest | **NOT_RECORDED** | `total_count=0`；gh download exit 1 |
| build / 12-DLL / companion / native exit | **NOT_RUN** | 0 step |
| 11 direct runner XML/console/meta | **NOT_RUN** | 0 XML、0 用例 |
| PG full/targeted、writer 强杀/restart/coldreader/空库恢复/cleanup/envrestore | **NOT_RUN** | 未启动 PG/进程 |
| 统一 verifier 退出码 | **NOT_RECORDED** | verifier 未启动 |
| 产品正确性 | **NOT_RUN**（不能判定有/无缺陷） | 无测试执行 |
| D-PG-SUP-02 | **仍 UNRESOLVED** | 历史证据，不由本 run 销账 |
| Android/iOS/真机/跨物理网络/整场/G2–G5 | **NOT_RUN / 未批准** | 本报告范围外 |
| 181 classified | **仍覆盖不足** | G1 台账 |

**总体：审查输入 PASS；CI 结论 FAIL；下游测试/产物 REVIEW_INCOMPLETE（NOT_RUN / NOT_RECORDED），绝不是 PASS。** 没有把 `NOT_RUN` 写成通过，没有把 `NOT_RECORDED` 写成 0，也没有把 8c8c792 的历史成功借给 44b1。

## 10. 缺陷清单与 owner 路由

本轮未发现可由本证据支持的**实现缺陷**；因此不建议对任何代码 owner 发起 rework：

```text
实现缺陷：0 条
```

阻塞项（不是代码缺陷）：

```text
[CI-BILLING-01]
严重级别：BLOCKER（仅对 CI 执行）
位置：GitHub Actions 账户/仓库计费设置（annotation path .github:1）
复现：gh run view 40739411504 / 40739420590；两个 job 均 runner_id=0、steps=null，
      annotation = recent account payments have failed or spending limit...
实际：job 未启动，0 step，0 测试，无 artifact/log
期望：runner 分配并执行 workflow step
证据：meta/annotations-*.json（SHA A99893E6...）、meta/checkrun-*.json、
      meta/run-*-jobs.json、logs/api-job-logs-*.txt（404）
修复要求：由仓库/账户 owner 处理 GitHub Actions 计费或 spending limit；不由代码
      owner 改 workflow/verifier，不放宽任何检查。
验收断言：修复后对新的 exact head 重新触发；只有实际 runner 分配、step 执行、
      artifact 上传并可由原始 XML/log 复核，才可重新评估测试结论。
```

```text
[EVID-44B1-01]
严重级别：BLOCKER（仅对证据验收）
位置：本 run 的 artifact/log 层
实际：artifacts total_count=0；job log 404；run log 空 ZIP
期望：ZIP/digest/entry 清单/manifest/XML/PG 原生证据
证据：§3.2；§4
修复要求：不是补造、不是用 API conclusion 推断；需要计费修复后的 fresh run。
验收断言：fresh run 的 artifact 可下载，ZIP SHA=API digest，manifest 逐行重哈希
      0 mismatch，11 runner XML/console/meta 与 PG 原生证据逐项可核。
```

建议 owner：`CI-BILLING-01` 归账户/仓库 owner（`liuchangready` 账户/组织计费管理员）；`EVID-44B1-01` 归 caller 在计费修复后重新取 evidence。**不**把这两项转成代码 owner 的 rework，也**不**由本审查者 request_rework/approve 任何线程。

## 11. 本地收口

- 只提交本报告一个 tracked 文件；所有 API JSON、日志端点原文、空 ZIP、探针、manifest、重哈希结果均在 ignored `artifacts/ds-44b1da8-f3aa358c194648b4b7b09f9478cf006b/`。
- 未 push、未建 PR/MR、未 merge、未部署、未操作 scheduler/`complete.flag`；未调用 bridge 转派/子任务；未操作其他线程/工作树/PG/Unity。
- 提交前 `git status --porcelain` 为空；提交后要求 tracked clean，`git diff --check` exit 0，HEAD 为 `c646b6e` 的直接子提交（报告不写自身 SHA，避免自引用）。
- 本报告不修改 `.github/workflows/v5-verify.yml`、`tools/ci/verify-v5-table-postgres.ps1`、`tools/verify-v5.ps1` 或任何产品/测试代码。

## 12. 明确不宣称

本报告不宣称：

- 44b1 的任何单测、direct runner、PG、native exit、12-DLL 或 verifier 已通过；
- 产品无缺陷或产品有缺陷；
- 44b1 的 PR test merge 已核验（该 SHA NOT_RECORDED）；
- D-PG-SUP-02 已关闭，或旧 Client 失败已被新绿销账；
- G2–G5、整场、Unity、IL2CPP、Android/iOS 真机、跨物理网络、PG 性能已通过；
- 181 classified 已覆盖。

它只确认：在 `44b1da8` 的这两个 run 上，原始 API/check-run/annotation 证据足以把失败精确定位为 GitHub Actions 账户计费/额度阻塞；job 从未启动，因此所有测试/构建/产物证据均为 `NOT_RUN`/`NOT_RECORDED`，CI 结论为 `FAIL`，审查输入为 `PASS`。
