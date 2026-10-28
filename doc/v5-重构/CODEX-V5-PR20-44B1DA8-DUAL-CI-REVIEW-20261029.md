# PR20 连续 CI 失败：账户阻止启动的独立核验

2026-10-29 02:48，Asia/Shanghai。执行者为 Codex caller。**本次证据审查 PASS；远端 CI 执行 BLOCKED，GitHub 原状态仍为 failure；构建、测试、PostgreSQL 和平台步骤均 NOT_RUN。**

GitHub check-run annotation 明确报告账户付款失败或支出限额导致 job 未启动。八次运行均无执行步骤、runner_id=0、零 artifact。此结论解释这些运行的失败原因，不证明产品正确，也不关闭既有产品缺陷。

## 基线、范围及归属

- 最新 fetch 的 `origin/v5`：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；PR19 已合并，未重开或复用其源分支。
- 本次审查/发布父提交为既有 PR20 头 `689391d6f755eede805fdef8ff945caf9db6f773`，base=v5，source=`feat/v5-post-g1-batch`。
- 隔离工作树 `D:/majiang-wt/codex-ci-platform-review-20261029-0245`，分支 `wt/v5-codex-ci-platform-review-20261029-0245`。先从最新 origin/v5 创建，再 ff 至上述已接受 PR20 头；祖先检查 exit 0。开始时 clean。
- 本次只新增本报告；`44b1da8..689391d -- dotnet tools tests unity .github` 内容相同，`git diff --exit-code` exit 0。未纳入候选 LocalHost `95b4754`、持久宿主 `5f365d1` 或 Unity 准备 `8878bd4`。
- 原 DeepSeek 审查线程 `thread_20261029_021901_e80c` 在原会话因 AUTH 401 退出，没有交付文件。caller 本次完成其 CI 证据核验范围，**不将接收方失败称为成功执行**。原树仍为 `c646b6e` 且 clean；不新建替代线程，不盲重试此同一审查。
- 主工作树未提交文件、其他 receiver 分支、Unity Resources/Burst 均未修改。本次未 rerun/cancel/dispatch 远端 CI、创建 CI-only ref、改变账单/额度或部署。

## 八次原始运行

均为 attempt 1、completed/failure；每次仅一个 `verify` job、0 steps、0 artifacts、1 条相同的账户阻断 annotation。

| 源提交 | push | pull_request |
|---|---|---|
| `02f22e30842544812eb5bd3e24dd63bd2023d7ee` | [40735572667](https://github.com/liuchangready/majiang-game/actions/runs/40735572667) | [40735580712](https://github.com/liuchangready/majiang-game/actions/runs/40735580712) |
| `44b1da88ab66bb73a873f41696cc5d0d82210216` | [40739411504](https://github.com/liuchangready/majiang-game/actions/runs/40739411504) | [40739420590](https://github.com/liuchangready/majiang-game/actions/runs/40739420590) |
| `c646b6e417a8d4cead2f8b642c9f3b166dcc0709` | [40743291394](https://github.com/liuchangready/majiang-game/actions/runs/40743291394) | [40743303925](https://github.com/liuchangready/majiang-game/actions/runs/40743303925) |
| `689391d6f755eede805fdef8ff945caf9db6f773` | [40743776377](https://github.com/liuchangready/majiang-game/actions/runs/40743776377) | [40743790793](https://github.com/liuchangready/majiang-game/actions/runs/40743790793) |

annotation 的关键原文为：

> The job was not started because recent account payments have failed or your spending limit needs to be increased.

逐 run 的 head/event/branch/workflow path/attempt、UTC 时间、job/runner/steps、PR 的 head/base 关联、check-run 及完整 annotation 均保留在原始 API 响应。PR 关联指向 #20、base v5。**没有执行 checkout，实际 CI 合成 checkout SHA/双父/tree 为 NOT_RECORDED**；不拿 API 的 source head_sha 冒充已执行的合成提交，也不拿当前 refs/pull 的可变值替代历史 checkout。

## 原始命令、日志与 native exit

证据根 E=`D:/majiang-wt/codex-ci-platform-review-20261029-0245/build/`。完整时间、命令、native exit、stdout/stderr 路径在 `E/ci-platform-audit/commands.json`，64 个 gh 命令：48 个 exit 0、16 个 exit 1。八次运行分别执行以下只读查询：

```powershell
gh api repos/liuchangready/majiang-game/actions/runs/<runId>
gh api 'repos/liuchangready/majiang-game/actions/runs/<runId>/jobs?filter=all&per_page=100'
gh api 'repos/liuchangready/majiang-game/actions/runs/<runId>/artifacts?per_page=100'
gh api 'repos/liuchangready/majiang-game/check-suites/<suiteId>/check-runs?per_page=100'
gh api 'repos/liuchangready/majiang-game/check-runs/<checkId>/annotations?per_page=100'
gh run download <runId> --dir <独占下载目录>
gh run view <runId> --log-failed
gh api --include repos/liuchangready/majiang-game/actions/runs/<runId>/logs
```

- 前五类查询均 native 0；artifacts API 的 total_count 均为 0。
- `gh run download` 八次均 native 1、`no valid artifacts found to download`；没有可下载的 verifier/test artifact，不能补造上传清单、digest、XML 或 DLL SHA。
- `gh run view --log-failed` 八次均 native 1、`log not found: <jobId>`。
- **logs REST 端点实际是 HTTP 200 / native 0，返回空 ZIP，非 404。** 初次 `--include` 结果只作为 HTTP/文本响应保留；随后用 `System.Diagnostics.Process.StandardOutput.BaseStream` 下载原始二进制，不经 PowerShell 文本重定向，另八条命令/UTC/exit/长度/SHA 在 `E/ci-platform-logzip/summary.json`。
- 八份原始日志 ZIP 均为 **22 字节、0 entries**，SHA256 均为 `8739C76E681F900923B900C9DF0EF75CF421D39CABB54650C4B9AD19B6A76D85`。此为空归档证据，不是测试日志存在或通过的证据。

## 本轮独立证据校验

原样命令，cwd 为本次隔离工作树：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/ci-platform-audit/audit.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/validate-ci-platform-audit.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/download-log-zips.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/finalize-evidence.ps1
```

四命令最终均 native 0。metadata consistency 校验 **366 checks / 0 failures**，含原清单 **134/134** 文件长度与 SHA；最终合并证据清单 **157/157** 文件逐行重哈希一致，另实际打开八份 ZIP 确认均零条目。这些是证据校验数，**不是产品测试通过数**。

初版本地校验器在 Windows PowerShell 5 的 JSON 数组包裹上报 `Expected eight frozen runs`，未得出通过结论；修正独占 ignored 校验脚本的数组读取后才执行上述完整校验。HTTP 状态与空 ZIP 以实际响应及二进制下载为准，未以“日志取不到”推断测试失败。未修改仓库 verifier 或任何产品断言。

| 证据 | 完整 SHA256 |
|---|---|
| `E/ci-platform-audit/manifest.csv`（134 行） | `B074F1D795EF41761E834D8EDED40BD66A54A92E3C4A722B926FEAD4E993563C` |
| `E/ci-platform-validation.json`（366 checks） | `FB733E0EADB6D5E3F7ACBFBC8F567A1AE54154CF5EC0E8666944459F41BBCC03` |
| `E/evidence-manifest.csv`（157 行） | `9B76E43D237BC5D717A2B2C702E6EA5303A1786E1B87BA7FECFC1349FF8C77B0` |

`E/evidence-validation.json` 保留最终逐行复核回执。原 API、stderr、ZIP、审计脚本、bridge 快照和本次读取的 PR body 均在清单内。证据无 token、私钥或游戏私有 payload。

## 结论矩阵与仍开放的义务

| 项目 | 本次结论 |
|---|---|
| 上述八次 CI 的账户阻断归因与证据核验 | PASS；GitHub 原 conclusion=failure 保留 |
| 远端 CI 获得 runner 并实际执行 | BLOCKED：账户付款或 spending limit；由账户有权限者处理，caller 未改账单 |
| build / 12 DLL closure / 11 direct runner / XML / verifier native exit | NOT_RUN；无实际产品测试进程退出码，不填历史测试计数 |
| 真实 PG full/targeted、writer 强杀/DB 重启/cold reader/空库恢复/cleanup/env restore | NOT_RUN；本次没有启动数据库 |
| Unity、整场、Android/iOS runtime、真机、跨物理网络 | NOT_RUN；保留各自既有历史状态，不改写为通过 |
| 历史 D-PG-SUP-02、旧 Client 框架日志缺失及其他历史失败 | 不销账；本次账户阻断解释不外推到其他运行 |

本轮全局 recent tasks 只查一次，100 条上限，跨全部工作区；mesh 总任务数 148。**DeepSeek 活动 0/目标2；GLM 0/目标1；Anti 0/目标1；合计 0/上限4。** 未派新任务、未盲重试、未改派或恢复用户手动暂停任务。

| 原线程 | 现状与下一步 |
|---|---|
| `thread_20261029_021901_e80c` / DeepSeek Flash | failed，AUTH 401。CI 审查范围由本 caller 报告完成；不重复下发同一审查 |
| `thread_20261029_015017_36bd` / DeepSeek Flash | failed，AUTH 401，未交付持久宿主独立复核；原树 `02f22e3` clean，不借失败线程宣称候选通过 |
| `thread_20261027_230302_93fb` / GLM Flash | round 3 failed，429；原树 `5f365d1` clean。既有 D-R2-01/02/03 清单保留，恢复可用后沿原线程批量返修 |
| `thread_20261028_223624_e4ae` / GLM Flash | waiting_review，原树 `8878bd4` clean；准备审查已有 D-01/03/05 MAJOR 等，仍须 caller 完整复核及同线程修复；真实激活另等完整 LocalHost 接受 |

模型发现确认 DeepSeek 默认 `deepseek/deepseek-v4.1-flash`、Claude Code 默认 `glm-5.3-flash`、Anti 默认 `gemini-3.8-flash-high`。mesh 标 DeepSeek online 只表示工具就绪，不能抵消两次真实 401；凭据修复前不盲重试。GLM bridge 报限流至上海 **10月29日03:04:30**，Anti 至 **11月2日00:54:27**；未因缺席改用其他模型。

LocalHost `95b4754` 的 D-LH-GATE-01/02/03/04 继续 REWORK，a/b/c/d 组件接受保留；完整 LocalHost 未接受，阻断 P4.2-B 真实工厂、P4.5/G2。持久宿主三项返修与接受阻断后续 HTTPS 大厅签票、多客户端整场对账/P5.4b/G3。P6 等 G2+G3，P7 等 G4；没有为补席绕过这些依赖。

G1 APPROVED；G2–G5 未批准；181 classified 未覆盖；V4 继续生产。scheduler 实查 Running/PT30M/IgnoreNew，未改配置；`complete.flag` 不存在。报告沿用唯一既有 PR20 发布，未 merge v5、未纳入未接受候选、未部署。报告提交产生的新 CI 另按其实际状态处理，不用本次审查代称新 CI 已完成。
